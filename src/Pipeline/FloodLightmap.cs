using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Flood-propagation lightmap (the Terraria technique): a small CPU grid over
    /// the visible tiles, seeded with per-tile SKY exposure (occluders from the Height
    /// Framework: tiles under buildings/canopies get no direct sky) and the game's REAL point
    /// lights, then swept directionally so light decays through air and dies quickly inside
    /// solids. A light 3×3 blur is folded back in as a fake indirect bounce. The result is
    /// uploaded as a tiny texture and multiplied over the scene with bilinear filtering —
    /// occlusion-aware ambient, shade under canopies, coloured lamp pools and window spill,
    /// all for a fraction of a millisecond of CPU and no fake rays from bright sprites.
    /// </summary>
    internal sealed class FloodLightmap
    {
        /// <summary>Off-screen margin in tiles so lights just outside the view still spill in.</summary>
        private const int Pad = 6;
        /// <summary>Per-cell survival factor while sweeping through open ground.</summary>
        private const float AirDecay = 0.86f;
        /// <summary>Per-cell survival through solid/occluding tiles (light dies in ~2-3 tiles).</summary>
        private const float SolidDecay = 0.65f;
        /// <summary>Sky light an occluded cell still starts with, as a fraction of the open sky.
        /// It used to be zero, which is what a roof does to DIRECT sun but not to the sky as a
        /// whole: a wall in the open still faces a bright hemisphere. Zero also made occlusion a
        /// cliff — one step took a block of cells from full sky to nothing, and the sweeps carried
        /// that into the open ground beside it, so crossing into a built-up stretch of a map
        /// visibly dimmed the screen. Shade, not darkness.</summary>
        private const float OccludedSeed = 0.5f;
        /// <summary>Cell values are stored ×0.5 in the texture so >1 (glow) survives; shader ×2.</summary>
        internal const float TexScale = 0.5f;

        private int _lastStartTileX = int.MinValue, _lastStartTileY = int.MinValue, _lastBuildTick = int.MinValue;
        private int _lastInputsHash;

        /// <summary>What the rebuild gate is allowed to do. Author diagnostic only, set by
        /// radiance_flood and never persisted.</summary>
        internal enum RebuildOverride { Auto, Every, Freeze }
        /// <summary>Diagnostic override for the rebuild gate. Auto in every normal session.</summary>
        internal static RebuildOverride RebuildMode = RebuildOverride.Auto;
        private GameLocation? _lastBuildLocation;

        /// <summary>
        /// Everything that can change the lightmap's CONTENT while the camera stands still,
        /// folded into one number. The rebuild used to run on a flat 3-tick clock, which is the
        /// right cadence for a flickering hearth and a 20-times-a-second tax everywhere else:
        /// standing in a windowless noon field paid ~1k tile lookups and three full-window CPU
        /// sweeps for a texture that came out identical every time. The measured cost was ~0.25 ms
        /// per frame in every scene, the second most expensive thing the mod did.
        /// <para>
        /// The hash covers the light list (a lamp toggling, a light walking on screen), the eased
        /// window scales (so the sun patch still FADES per frame while a switch is mid-ease), and
        /// the ambient tint (dusk and weather ramps). The game clock is deliberately NOT in it:
        /// it moves every frame, and its effect over the fallback cadence below is far below what
        /// an eye can pick out. Flame flicker is not in it either, and no longer needs to be: the
        /// bounce this grid carries does not flicker at all now (see the seed), so there is nothing
        /// left that wanted a faster clock than a fire once did.
        /// </para>
        /// </summary>
        private static int HashLightInputs(GameLocation location)
        {
            unchecked
            {
                int h = 17;
                var lights = Game1.currentLightSources;
                if (lights != null)
                {
                    foreach (var kv in lights.Values)
                    {
                        var ls = kv;
                        if (!ShadowRenderer.WindowGlowing(location, ls))
                            continue;
                        h = h * 31 + (int)(ls.position.Value.X / 16f);
                        h = h * 31 + (int)(ls.position.Value.Y / 16f);
                        h = h * 31 + (int)(ls.radius.Value * 16f);
                        h = h * 31 + ls.textureIndex.Value;
                    }
                }
                h = h * 31 + (int)(WindowPatchScale * 255f);
                h = h * 31 + (int)(WindowRoomScale * 255f);
                h = h * 31 + Game1.ambientLight.PackedValue.GetHashCode();
                return h;
            }
        }
        private Vector3[] _lightCells = Array.Empty<Vector3>();
        private Vector3[] _blurredLightCells = Array.Empty<Vector3>();
        /// <summary>Row pass of the separable bounce blur; see where it is filled.</summary>
        private Vector3[] _blurRowScratch = Array.Empty<Vector3>();
        private float[] _lightDecay = Array.Empty<float>();
        private Color[] _lightmapPixels = Array.Empty<Color>();
        private Texture2D? _lightmapTexture;

        internal Texture2D? Texture => _lightmapTexture;
        /// <summary>World tile coordinate of the map's (0,0) cell.</summary>
        internal Vector2 Origin;
        internal Vector2 MapSize;

        /// <summary>The lightmap value at one world tile, scaled back out of the ×0.5 storage.
        /// For the radiance_debug flood caption: flick a light switch and read off whether the
        /// map actually moved instead of trusting the composite to show it.</summary>
        internal string Probe(int tileX, int tileY)
        {
            if (_lightmapTexture == null)
                return "no texture";
            int xi = tileX - (int)Origin.X;
            int yi = tileY - (int)Origin.Y;
            if (xi < 0 || yi < 0 || xi >= (int)MapSize.X || yi >= (int)MapSize.Y)
                return "off-map";
            Color c = _lightmapPixels[yi * (int)MapSize.X + xi];
            float v = c.R / 255f / TexScale;   // stored ×TexScale (0.5); scale back for display
            return v.ToString("F2");
        }

        /// <summary>
        /// What the location IS, as every seed pass needs to know it: whether the sky is overhead,
        /// whether the game itself renders this place dark, and what an unoccluded cell starts at.
        /// Carried as one value because four separate answers escaping one block is four
        /// out-parameters, and nobody can read those at the call site.
        /// </summary>
        private readonly struct SceneSeed
        {
            public readonly bool Outdoors;
            /// <summary>Places the game renders dark BY DESIGN (mines, volcano): strictly add-only,
            /// because multiplying on top of vanilla dark read as pitch black.</summary>
            public readonly bool ScriptedDark;
            /// <summary>The game already darkens this location, so the flood may only add light.</summary>
            public readonly bool VanillaDark;
            /// <summary>Flat seed for a vanilla-dark room, driven by the night-darkness slider.</summary>
            public readonly float NightSeed;
            /// <summary>Colour and strength an open cell receives from the sky.</summary>
            public readonly Vector3 Sky;

            public SceneSeed(bool outdoors, bool scriptedDark, bool vanillaDark, float nightSeed, Vector3 sky)
            {
                Outdoors = outdoors;
                ScriptedDark = scriptedDark;
                VanillaDark = vanillaDark;
                NightSeed = nightSeed;
                Sky = sky;
            }
        }

        /// <summary>The tile rectangle this build covers: its top-left world tile and its size in
        /// cells. One parameter in place of the four that were threaded through every phase.</summary>
        private readonly struct TileWindow
        {
            public readonly int X0, Y0, W, H;
            public TileWindow(int x0, int y0, int w, int h) { X0 = x0; Y0 = y0; W = w; H = h; }
            public int Count => W * H;
        }


        internal bool Build(GraphicsDevice graphicsDevice, int width, int height, ModConfig config)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return false;

            int tx0 = (int)Math.Floor(Game1.viewport.X / 64f) - Pad;
            int ty0 = (int)Math.Floor(Game1.viewport.Y / 64f) - Pad;
            // Window size from the VIEWPORT (world px), never from the render target: screen
            // px / 64 undercounts tiles when zoomed out, and the window edge showed up as a
            // hard rectangle of missing GI in the middle of the screen.
            int tw = Math.Max(1, Game1.viewport.Width / 64 + 2) + Pad * 2;
            int th = Math.Max(1, Game1.viewport.Height / 64 + 2) + Pad * 2;
            int count = tw * th;

            // Rebuild when an INPUT changed, not on a clock. A tile crossing or resize always
            // rebuilds; a changed light list, window ease or ambient tint rebuilds (see
            // HashLightInputs); otherwise the fallback cadence only covers what the hash cannot
            // see, which is the game clock's slow drift. A third of a second of that is a fraction
            // of a game-minute, an order of magnitude below anything that reads as a step. Fires
            // used to force this to 3 and no longer do: nothing in this grid flickers.
            int inputsHash = HashLightInputs(location);
            const int cadence = 20;
            // Diagnostic override (radiance_flood). Freeze holds the last grid no matter what, so
            // anything still moving on screen provably is not this grid; Every rebuilds it on every
            // frame, so anything that stops moving provably WAS the rebuild rate rather than the
            // content. Between the two answers there is nothing left to guess about.
            if (RebuildMode == RebuildOverride.Freeze && _lightmapTexture != null
                && ReferenceEquals(location, _lastBuildLocation)
                && _lightmapTexture.Width == tw && _lightmapTexture.Height == th)
                return true;
            // The location is part of the identity, not the hash: two maps can put the camera at
            // the same tile with the same lights (none), and at the old 3-tick clock showing the
            // previous map's lightmap for 50 ms was invisible where a third of a second is not.
            if (RebuildMode != RebuildOverride.Every
                && _lightmapTexture != null && ReferenceEquals(location, _lastBuildLocation)
                && tx0 == _lastStartTileX && ty0 == _lastStartTileY
                && _lightmapTexture.Width == tw && _lightmapTexture.Height == th
                && inputsHash == _lastInputsHash
                && Game1.ticks - _lastBuildTick < cadence)
                return true;
            _lastStartTileX = tx0; _lastStartTileY = ty0; _lastBuildTick = Game1.ticks;
            _lastInputsHash = inputsHash;
            _lastBuildLocation = location;

            if (_lightCells.Length < count)
            {
                _lightCells = new Vector3[count];
                _blurredLightCells = new Vector3[count];
                _blurRowScratch = new Vector3[count];
                _lightDecay = new float[count];
                _lightmapPixels = new Color[count];
            }

            SceneSeed scene = DescribeScene(location, config);
            var win = new TileWindow(tx0, ty0, tw, th);
            SeedSkyExposure(location, scene, win);
            SeedLightSources(location, scene, win);
            SeedWindowGlows(location, scene, win);
            FloodSweeps(win);
            BounceBlur(win);
            ComposeLightmapPixels(scene, win);

            if (_lightmapTexture == null || _lightmapTexture.Width != tw || _lightmapTexture.Height != th)
            {
                _lightmapTexture?.Dispose();
                _lightmapTexture = VramTally.Track(new Texture2D(graphicsDevice, tw, th, false, SurfaceFormat.Color), "flood lightmap");
            }
            _lightmapTexture.SetData(_lightmapPixels, 0, count);
            Origin = new Vector2(tx0, ty0);
            MapSize = new Vector2(tw, th);
            return true;
        }

        /// <summary>Read the location once: what the seed passes below all need to agree on.</summary>
        private static SceneSeed DescribeScene(GameLocation location, ModConfig config)
        {
            // ---- Seed pass: sky exposure + per-cell decay from the occluder grid ----
            // The flood is RELATIVE lighting: the game's own day/night & scripted darkness
            // stay in charge of the global level. Locations the game already darkens
            // (mines, volcano, any non-white ambient) run in ADD-ONLY mode — every cell
            // seeds at 1.0 so lamps enrich and cast shadows but nothing gets darker than
            // vanilla (multiplying on top of vanilla dark read as pitch black).
            bool outdoors = location.IsOutdoors;
            // Places the game itself renders dark by design. These are the ones that went pitch
            // black when anything multiplied on top, so they stay strictly add-only.
            bool scriptedDark = !outdoors &&
                (location is StardewValley.Locations.MineShaft || location is StardewValley.Locations.VolcanoDungeon);
            // A storm dims a house's ambient a hair under white, but it is still day: the flat
            // add-only night seed is for places the game keeps dark, not for a daytime weather
            // dip. Gating the ambient term on real night stops a stormy morning from flipping the
            // room to a flat-bright seed while a clear one keeps the (dimmer) daylight curve -
            // the exact inverse of how daylight works, and how "dark on clear, bright on storm"
            // (1115938) was reported.
            bool itIsNight = GameClock.MinutesNow() >= ShadowRenderer.TrulyDarkMinutes() - 60f;
            bool ambientDark = Game1.ambientLight.R < 245 || Game1.ambientLight.G < 245 || Game1.ambientLight.B < 245;
            bool vanillaDark = !outdoors && (scriptedDark || (ambientDark && itIsNight));
            // A HOUSE at midnight is not a mine. The game tints it down a little and then leaves
            // it evenly lit, so the fireplace and the lamps have nothing to stand out against.
            // Add a second, gentle layer there - and let the night-darkness slider drive it,
            // which is the setting players have been given for exactly this and which did
            // nothing at all on this lighting model until now.
            float nightSeed = vanillaDark && !scriptedDark
                ? MathHelper.Clamp(1f - config.LightingNightDarkness * 0.38f, 0.45f, 1f)
                : 1f;
            Vector3 sky = SkyColor(outdoors, config);
            return new SceneSeed(outdoors, scriptedDark, vanillaDark, nightSeed, sky);
        }

        /// <summary>Sky exposure and per-cell decay, read off the occluder grid.</summary>
        private void SeedSkyExposure(GameLocation location, in SceneSeed scene, in TileWindow win)
        {
            var surf = SurfaceMap.For(location);
            for (int j = 0; j < win.H; j++)
            {
                for (int i = 0; i < win.W; i++)
                {
                    int idx = j * win.W + i;
                    bool solid = false;
                    // Sky occlusion only makes sense OUTDOORS. Interiors are already under a roof,
                    // and every interior tile carries Front-layer art (upper walls), which the
                    // height classifier reports as Roof — treating those as scene.Sky occluders zeroed
                    // the whole room's lightmap (black scene, then the warm lamp seed flooded it
                    // orange). Indoors, leave every cell open so ambient + lamps light it normally.
                    // Only WALLS and ROOF/canopy block scene.Sky light. Decks (piers, bridges) have
                    // height 1 but are walk-on-top surfaces OPEN to the scene.Sky — treating them as
                    // solid turned the whole beach pier into a giant dark pool. Water is open too.
                    if (surf != null && scene.Outdoors)
                        solid = surf.BlocksLight(win.X0 + i, win.Y0 + j);
                    _lightDecay[idx] = solid ? SolidDecay : AirDecay;
                    // Open cells receive direct scene.Sky light; occluded cells only what floods in
                    // from their surroundings → soft shade under trees/buildings for free.
                    _lightCells[idx] = scene.VanillaDark ? new Vector3(scene.NightSeed) : (solid ? scene.Sky * OccludedSeed : scene.Sky);
                }
            }

        }

        /// <summary>
        /// Where a light OUTSIDE the grid seeds, and how much of it arrives: the nearest cell on
        /// the grid's edge, carrying what the sweep would have carried across the missing cells.
        /// </summary>
        /// <remarks>
        /// <para>The grid is the visible tiles plus <see cref="Pad"/>, and a light beyond it used to
        /// be skipped. Every seed then depended on where the camera stood, and the camera moves in
        /// whole tiles: a lamp entering the padding fed nothing one frame and its full seed the
        /// next, or, after the first attempt at this (a fade across the padding), a quarter of it
        /// per tile crossed - which was still a step, and one that now landed on the visible
        /// columns instead of six tiles outside them. Simulated cell for cell before this was
        /// written: in a night town the fade produced a step on eighteen of forty crossings, up to
        /// eleven of 255 on screen, where the plain cut produced one; and seeding every light,
        /// clamped, produced none.</para>
        /// <para>Clamped is exact for the sweep this grid runs. Propagation is a max over
        /// axis-aligned paths with one factor of <see cref="AirDecay"/> per cell, so the value a
        /// light would have handed the edge cell across open ground is its seed times the decay
        /// raised to the Manhattan distance, and the sweep carries on inward from there exactly as
        /// it would have. Nothing about the result depends on where the edge is, which is the
        /// whole point: the grid can be rebuilt at any origin and read the same in the world.
        /// Occluders outside the grid are treated as air, which is the far tail of a pool the
        /// grid never showed at all before.</para>
        /// </remarks>
        private static bool ClampSeed(ref int ci, ref int cj, ref float inten, in TileWindow win)
        {
            int cx = Math.Clamp(ci, 0, win.W - 1);
            int cy = Math.Clamp(cj, 0, win.H - 1);
            int away = Math.Abs(ci - cx) + Math.Abs(cj - cy);
            if (away > 0)
                inten *= (float)Math.Pow(AirDecay, away);
            ci = cx;
            cj = cy;
            return inten > 0.002f;
        }

        private void SeedLightSources(GameLocation location, in SceneSeed scene, in TileWindow win)
        {
            // ---- Seed the game's real light sources (lamps, torches, fires, windows) ----
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                foreach (var kv in lights.Values)
                {
                    var ls = kv;
                    if (!ShadowRenderer.WindowGlowing(location, ls))   // stale/dark window: not emitting
                        continue;
                    // The TRUE cell, which may lie outside the grid; the columns below are laid
                    // from it so their cells stay where they are in the world. The seed itself is
                    // clamped onto the grid, decayed for the distance (see ClampSeed).
                    // The same drop the direct pool takes, or the bounce would sit a tile above
                    // the pool it is supposed to be the bounce of. See ShadowRenderer.FlameGlowOffset.
                    Vector2 glowPosition = ls.position.Value
                        + ShadowRenderer.FlameGlowOffset(location, ls.position.Value, ls.textureIndex.Value);
                    int trueCi = (int)(glowPosition.X / 64f) - win.X0;
                    int trueCj = (int)(glowPosition.Y / 64f) - win.Y0;
                    int ci = trueCi, cj = trueCj;
                    // INDIRECT spill (~half strength): the crisp direct pool + its per-light shadows
                    // are computed analytically in floodlight.effect; the flood carries the bounce-like
                    // glow that bends around corners and through doorways. Outdoors it sits above 1.0
                    // so it beats the dimmed night ground; indoors it stays gentle.
                    //
                    // NO FLAME FLICKER HERE, on purpose. This grid is a CPU sweep that cannot afford
                    // to run every frame, so multiplying the seed by the flicker sampled it at the
                    // rebuild rate and held it in between: the bounce moved in 3-frame steps while
                    // the direct pool around the same fire moved smoothly every frame, and the two
                    // rates beating against each other is what read as the floor around a lamp
                    // flashing. Physically the bounce is the half that should NOT snap anyway - it
                    // is light that has crossed the room and come back off a wall. The flame still
                    // breathes where it is visible, in the direct pool (RenderPipeline.Lighting)
                    // and in the shadows it casts, both of which are per-frame and free.
                    float inten = MathHelper.Clamp(0.55f + 0.30f * ls.radius.Value, 0.6f, 1.7f) * (scene.Outdoors ? 1.25f : 0.5f);
                    if (!ClampSeed(ref ci, ref cj, ref inten, win))
                        continue;
                    // The same midday sink the DIRECT pools got ("a street lamp at noon reads as
                    // glass"): these seeds never had it, which went unnoticed while the flat bounce
                    // held the whole outdoor field near 1.28 — every cell glowed a little, so lamp
                    // cells did not stand out. With the bounce weighted (open ground now sits at
                    // exactly scene.Sky), a daylight lantern's >1.0 seed became the only thing feeding
                    // the shader's glow term, and it read as a bright pool at two in the afternoon.
                    // Full strength returns by 08:00/17:00; night and indoors are untouched.
                    if (scene.Outdoors)
                        inten *= 1f - 0.65f * (1f - MathHelper.Clamp(Math.Abs(GameClock.MinutesNow() - 750f) / 270f, 0f, 1f));
                    // TWO-TONE rooms: an indoor window is DAYLIGHT (cool, slightly blue) while
                    // lamps and fires stay warm — the warm-vs-cool split across a room is what
                    // makes it read as cinematic instead of uniformly orange. Outdoor window
                    // lights (town houses at night) are lamp-lit from inside, so they stay warm.
                    bool coolDaylight = !scene.Outdoors && ls.lightContext.Value == LightSource.LightContext.WindowLight;
                    // A LIGHT'S BOUNCE IS THE LIGHT'S OWN COLOUR. This was one fixed warm constant
                    // for every source in the game, which is where the saloon's orange came from and
                    // had been coming from for a long time: all 66 of that room's map lights are
                    // white, and every one of them was being bounced back off the walls as
                    // (1.00, 0.83, 0.58). The note in the interior colour curve saying the lamps were
                    // measured white and therefore innocent was right about the lamps and wrong about
                    // us. Stardew stores a light's colour inverted, the same way the direct pass
                    // already reads it, and the seed is normalised so only the HUE comes from here
                    // and the strength keeps coming from the radius rule above.
                    Color raw = ls.color.Value;
                    Vector3 emitted = new(1f - raw.R / 255f, 1f - raw.G / 255f, 1f - raw.B / 255f);
                    float emittedPeak = Math.Max(emitted.X, Math.Max(emitted.Y, emitted.Z));
                    Vector3 seedColor = emittedPeak > 0.02f
                        ? emitted / emittedPeak
                        : new Vector3(1.00f, 0.83f, 0.58f);   // a light with no colour at all: warm, as before
                    if (coolDaylight)
                    {
                        // The scene.Sky is what is on the other side of this window, so it follows the
                        // clock and the calendar instead of being one fixed daylight colour (see
                        // WindowDaylight - that constant is why rooms stayed daylit at 2am).
                        ShadowRenderer.WindowDaylight(out Vector3 sunColour, out float sunStrength);
                        seedColor = sunColour;
                        // ×1.25 so the pool can actually show through the room's exposure (a bare
                        // sunStrength at 0.85 stays under the multiply-only floor and reads as if
                        // nothing happened when the toggle is flicked).
                        inten *= (1.25f * sunStrength) * WindowRoomScale;
                    }
                    // One seed cell; the bilinear upsample + the 5×5 bounce spread it into a soft
                    // pool. (A wide radial seed disc was tried to force a bigger pool but never read
                    // as wider on the coarse grid — reverted to keep it simple.)
                    int idx = cj * win.W + ci;
                    _lightCells[idx] = Vector3.Max(_lightCells[idx], seedColor * inten);

                    // SUN SHAFT: daylight through a window falls onto the floor below it — seed a
                    // fading column of cool light under the window so (after bilinear + the blur
                    // bounce) a soft bright patch spills across the floorboards.
                    if (coolDaylight)
                    {
                        // Kept below the bloom threshold so the window doesn't bloom into a
                        // glaring white patch (it was ~1.15 = over-bright + bloomed).
                        // The patch LEANS with the sun and stretches when the sun is low, from
                        // the same angle the shadows use - so first thing in the morning it
                        // reaches right across the floorboards, and by noon it is a short pool
                        // directly under the window.
                        ShadowRenderer.WindowShaft(out float lean, out float reach);
                        // The patch takes its OWN switch and not the room's intensity, so the two
                        // halves can be turned off independently of each other.
                        Vector3 shaft = seedColor * (0.72f * WindowPatchScale);
                        int steps = Math.Max(1, (int)Math.Round(reach));
                        for (int k = 1; k <= steps; k++)
                        {
                            int jj = trueCj + k;
                            int ii = trueCi + (int)Math.Round(lean * k);
                            if (jj >= win.H)
                                break;
                            if (jj < 0 || ii < 0 || ii >= win.W)
                                continue;
                            float f = 1.0f - 0.85f * (k / (float)(steps + 1));
                            int sIdx = jj * win.W + ii;
                            _lightCells[sIdx] = Vector3.Max(_lightCells[sIdx], shaft * f);
                        }
                    }
                    // OUTDOOR lit storefronts/windows at night pour WARM light DOWN onto the
                    // path in front (a saloon's windows lighting the ground). Short fading
                    // column, softened afterwards by the bilinear sample + the wide bounce.
                    else if (scene.Outdoors && ls.lightContext.Value == LightSource.LightContext.WindowLight)
                    {
                        var spill = new Vector3(1.00f, 0.84f, 0.60f);
                        for (int k = 1; k <= 4; k++)
                        {
                            int jj = trueCj + k;
                            if (jj >= win.H)
                                break;
                            if (jj < 0 || trueCi < 0 || trueCi >= win.W)
                                continue;
                            float f = (1.0f - 0.22f * k) * inten * 2.2f;
                            int sIdx = jj * win.W + trueCi;
                            _lightCells[sIdx] = Vector3.Max(_lightCells[sIdx], spill * f);
                        }
                    }
                }
            }

        }

        /// <summary>Seed window daylight from the room's glow sprites, for the interiors that
        /// publish their windows no other way.</summary>
        private void SeedWindowGlows(GameLocation location, in SceneSeed scene, in TileWindow win)
        {
            var lights = Game1.currentLightSources;
            // ---- Seed window daylight from the room's window glow sprites ----
            // Some interiors publish their windows only as lightGlows (no WindowLight source and
            // no DayTiles property - the vanilla farmhouse is one), so the loop above never
            // touches them. A glow sprite IS the game saying "this window is lit", so seed the
            // same cool daylight there; otherwise the flood leaves the floor beside a real
            // window at bare scene.Sky and it reads as a dark strip in front of the glass.
            int glowCount = location.lightGlows is { } lg ? lg.Count : -1;
            bool glowGate = !scene.Outdoors && !scene.ScriptedDark && WindowRoomScale > 0.01f && glowCount > 0;
            if (glowGate)
            {
                ShadowRenderer.WindowDaylight(out Vector3 sunColour, out float sunStrength);
                int seeded = 0;
                if (sunStrength > 0.03f)
                {
                    foreach (Vector2 gp in location.lightGlows)
                    {
                        int trueCi = (int)(gp.X / 64f) - win.X0;
                        int trueCj = (int)(gp.Y / 64f) - win.Y0;
                        int ci = trueCi, cj = trueCj;
                        // Skip any spot a real window light source already covered above.
                        bool covered = false;
                        if (lights != null)
                            foreach (var ls in lights.Values)
                                if (ls.lightContext.Value == LightSource.LightContext.WindowLight
                                    && Math.Abs((int)(ls.position.Value.X / 64f) - (win.X0 + trueCi)) <= 1
                                    && Math.Abs((int)(ls.position.Value.Y / 64f) - (win.Y0 + trueCj)) <= 1)
                                { covered = true; break; }
                        if (covered)
                            continue;
                        float glowInten = 1.35f * sunStrength * WindowRoomScale;
                        if (!ClampSeed(ref ci, ref cj, ref glowInten, win))
                            continue;
                        int sIdx = cj * win.W + ci;
                        _lightCells[sIdx] = Vector3.Max(_lightCells[sIdx], sunColour * glowInten);
                        // Spread the daylight down the first cells INTO the room, so the patch
                        // reads as light pooling in front of the glass rather than a single-cell
                        // glint that a toggle is easy to miss. Kept at/over 1.0 so it can actually
                        // ADD light - below 1.0 the flood can only darken less, which is why the
                        // old seed read as nothing next to the room's exposure.
                        for (int k = 1; k <= 3; k++)
                        {
                            int jj = trueCj + k;
                            if (jj >= win.H)
                                break;
                            if (jj < 0 || trueCi < 0 || trueCi >= win.W)
                                continue;
                            _lightCells[jj * win.W + trueCi] = Vector3.Max(_lightCells[jj * win.W + trueCi],
                                sunColour * glowInten * (1f - 0.25f * k));
                        }
                        seeded++;
                    }
                }
                LastWindowSeed = $"seed: {seeded} cell / scale={WindowRoomScale:F2} sun={sunStrength:F2} glows={glowCount}";
            }
            else
            {
                LastWindowSeed = $"seed: GATE (out={scene.Outdoors} scripted={scene.ScriptedDark} scale={WindowRoomScale:F2} glows={glowCount})";
            }

        }

        /// <summary>Two rounds of four directional sweeps: the propagation itself.</summary>
        private void FloodSweeps(in TileWindow win)
        {
            // ---- Flood: two rounds of 4 directional sweeps (Terraria-style) ----
            for (int round = 0; round < 2; round++)
            {
                for (int j = 0; j < win.H; j++)          // left → right, then right → left
                {
                    Vector3 carry = Vector3.Zero;
                    for (int i = 0; i < win.W; i++) Propagate(ref carry, j * win.W + i);
                    carry = Vector3.Zero;
                    for (int i = win.W - 1; i >= 0; i--) Propagate(ref carry, j * win.W + i);
                }
                for (int i = 0; i < win.W; i++)          // top → bottom, then bottom → top
                {
                    Vector3 carry = Vector3.Zero;
                    for (int j = 0; j < win.H; j++) Propagate(ref carry, j * win.W + i);
                    carry = Vector3.Zero;
                    for (int j = win.H - 1; j >= 0; j--) Propagate(ref carry, j * win.W + i);
                }
            }

        }

        /// <summary>The fake indirect bounce, as a separated 5x5 box blur.</summary>
        private void BounceBlur(in TileWindow win)
        {
            // ---- Fake one indirect bounce: 5×5 blur folded back in softly ----
            // 5×5 (was 3×3): a wider bounce spreads each light into a softer, fluffier pool that
            // fades out gradually instead of ending within one tile.
            //
            // SEPARATED into a row pass and a column pass: 25 reads per cell became 10. A box blur
            // separates exactly, and so does THIS one despite clamping at the edges, because the
            // clamped window stays a rectangle - the divisor is validColumns × validRows, a product
            // of one term per axis, so dividing by each axis in its own pass gives the same number.
            // Worth doing where it is: the grid is the viewport in tiles, so it grows quadratically
            // as the player zooms out, which is exactly the case the reports are about.
            for (int j = 0; j < win.H; j++)
            {
                int row = j * win.W;
                for (int i = 0; i < win.W; i++)
                {
                    int i0 = Math.Max(0, i - 2), i1 = Math.Min(win.W - 1, i + 2);
                    var acc = Vector3.Zero;
                    for (int ii = i0; ii <= i1; ii++)
                        acc += _lightCells[row + ii];
                    _blurRowScratch[row + i] = acc / (i1 - i0 + 1);
                }
            }
            for (int j = 0; j < win.H; j++)
            {
                int j0 = Math.Max(0, j - 2), j1 = Math.Min(win.H - 1, j + 2);
                int row = j * win.W;
                for (int i = 0; i < win.W; i++)
                {
                    var acc = Vector3.Zero;
                    for (int jj = j0; jj <= j1; jj++)
                        acc += _blurRowScratch[jj * win.W + i];
                    _blurredLightCells[row + i] = acc / (j1 - j0 + 1);
                }
            }
        }

        /// <summary>Fold the bounce back in, lift elevated surfaces, and pack to bytes.</summary>
        private void ComposeLightmapPixels(in SceneSeed scene, in TileWindow win)
        {
            // Walls/roofs are ELEVATED surfaces in a top-down view: the dark cell value models
            // light blocked at ground level, but the pixels DRAWN there are facades and rooftops
            // in full daylight — lift them to ambient so buildings never render dimmer than the
            // ground they stand on (dark cells still attenuate propagation for the spill/shade).
            Vector3 lift = scene.Sky * (scene.Outdoors ? 0.92f : 0.85f);
            for (int idx = 0; idx < win.Count; idx++)
            {
                // The bounce FILLS SHADE. It used to be a flat add, which put every open outdoor
                // cell at ~1.28 in broad daylight — and floodlight.effect reads anything over 1.0 as a
                // lamp core and adds a glow for it, so open ground got a few percent of extra light
                // it was never meant to have. On a winter beach, where snow is already close to
                // white and most of the screen is open, that pushed the whole field past clipping
                // and the detail in the snow disappeared. Weighting the bounce by how far the cell
                // is BELOW full light leaves open ground at exactly scene.Sky, still lifts real shade,
                // and leaves lamp cells (seeded above 1.0) free to glow as intended.
                Vector3 c = _lightCells[idx];
                Vector3 room = new(
                    MathHelper.Clamp(1f - c.X, 0f, 1f),
                    MathHelper.Clamp(1f - c.Y, 0f, 1f),
                    MathHelper.Clamp(1f - c.Z, 0f, 1f));
                Vector3 v = c + _blurredLightCells[idx] * 0.28f * room;
                if (_lightDecay[idx] == SolidDecay)
                    v = Vector3.Max(v, lift);
                _lightmapPixels[idx] = new Color(
                    (byte)MathHelper.Clamp(v.X * 255f * TexScale, 0f, 255f),
                    (byte)MathHelper.Clamp(v.Y * 255f * TexScale, 0f, 255f),
                    (byte)MathHelper.Clamp(v.Z * 255f * TexScale, 0f, 255f), (byte)255);
            }

        }

        private void Propagate(ref Vector3 carry, int idx)
        {
            float d = _lightDecay[idx];
            carry *= d;
            Vector3 c = _lightCells[idx];
            carry = Vector3.Max(carry, c);
            _lightCells[idx] = carry;
        }

        /// <summary>Direct-sky seed for open cells — RELATIVE only (the game's own day/night
        /// darkening stays in charge of the global level, so no double-darkening at night):
        /// ~1.0 with a warm golden-hour tint outdoors; interiors use the indoor-darkness
        /// slider (vanilla leaves rooms flat-bright — that darkening is the feature).</summary>
        /// <summary>How far into night the clock is, 0 at an hour before truly-dark, 1 from
        /// truly-dark on. The one ramp every outdoor night term shares, so they arrive together.</summary>
        internal static float NightAmount()
        {
            int t = Game1.timeOfDay;
            int trulyDark;
            try { trulyDark = Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { trulyDark = 2000; }
            int mins = (t / 100) * 60 + t % 100;
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            return MathHelper.Clamp((mins - (m1 - 60)) / 60f, 0f, 1f);
        }

        private static Vector3 SkyColor(bool outdoors, ModConfig config)
        {
            if (!outdoors)
            {
                // Interiors have no sky: a flat ambient set by the indoor-darkness slider,
                // with window/lamp seeds carving out the bright areas.
                float amb = MathHelper.Clamp(1f - config.LightingIndoorDarkness * 0.55f, 0.3f, 1f);
                // A room with windows is lit BY those windows, so its ambient follows the same
                // daylight they do - dim and warm at dawn, full at noon, dim again at dusk.
                // Flat ambient was why every interior read as noon at six in the morning.
                // Night needs no help here: when the game darkens a room itself the caller
                // hands out plain white instead of this (see the vanillaDark branch), so this
                // curve only ever shapes the hours the game leaves flat-bright.
                ShadowRenderer.WindowDaylight(out Vector3 dayColour, out float _);
                // The room FILLS IN later than the window starts pouring light through it, and
                // that gap is the whole effect: at six the sun is low enough to lay a bright
                // patch on the floor while the rest of the room is still last night's dark.
                // Tying the ambient to the window's own strength collapsed that gap - both hit
                // full together and the room read as noon at sunrise. Fill runs 06:20 to 09:00
                // and unwinds over the hour and a half before dark.
                float nowMinutes = GameClock.MinutesNow();
                float darkMinutes = ShadowRenderer.TrulyDarkMinutes();
                float fill = Math.Min(MathHelper.Clamp((nowMinutes - 380f) / 160f, 0f, 1f),
                                      MathHelper.Clamp((darkMinutes - nowMinutes) / 90f, 0f, 1f));
                // The wake floor follows the morning-darkness slider: at its default (0.25) the
                // room wakes at the historical ~0.42; 0 lifts it to a fully bright wake.
                float wakeFloor = MathHelper.Lerp(1f, 0.42f, MathHelper.Clamp(config.LightingMorningDarkness / 0.25f, 0f, 1f));
                amb = MathHelper.Clamp(amb * MathHelper.Lerp(wakeFloor, 1f, fill), 0.16f, 1f);
                // ...and takes its colour, so the air in the room agrees with the light coming
                // through the glass rather than staying neutral grey while the patch goes gold.
                return new Vector3(amb) * Vector3.Lerp(Vector3.One, dayColour, 0.5f);
            }
            float dayProgress = MathHelper.Clamp((GameClock.MinutesNow() - 720f) / 360f, -1f, 1f);
            float warm = MathHelper.Clamp((Math.Abs(dayProgress) - 0.55f) / 0.45f, 0f, 1f);
            Vector3 sky = Vector3.Lerp(new Vector3(1f, 1f, 1f), new Vector3(1.03f, 0.96f, 0.88f), warm);
            if (Game1.isRaining)
                sky *= 0.93f;   // gentle overcast dimming; vanilla already grays rain out

            // MOONLIGHT: after dark, open ground gets a cool lift scaled by the lunar phase
            // (SDV's 28-day month = one synthetic cycle) and season — cells under canopies
            // and buildings receive none, so a full moon paints real moon shade.
            float nightT = NightAmount();
            // HOW DARK, AND WHAT COLOUR OF DARK. Two decisions, and both used to be hardcoded.
            //
            // The depth was a bare 0.62 that no setting reached, while the night-darkness
            // slider's own help text promised "how dark the world gets outdoors after
            // nightfall" - a promise the code never kept, because the slider only ever ran
            // indoors. It drives this now, mapped so the default lands exactly on the old
            // 0.62: nobody's night changes until they move the thing that now works.
            //
            // The colour was neutral grey, and neutral dim is why the old night read as muddy
            // rather than as night. Eyes at low light lose red first (the Purkinje shift), and
            // every film and game night trades on it: the unlit world leans blue-cool, the
            // flames stay warm, and that warm-against-cool is the whole picture. Same
            // luminance as before - the tint is normalised - so nothing gets darker by gaining
            // a colour, and the moon lift below still rides on top.
            float nightFloor = MathHelper.Clamp(1f - config.LightingNightDarkness * 0.68f, 0.2f, 1f);
            // The cool cast follows the slider down: at the default it is the full moonlit blue,
            // and at zero it is gone entirely, so someone who slides the night away gets a night
            // that is simply a brighter vanilla rather than this mod's colour opinion at a lower
            // volume. Asked for in exactly those words: low should look like vanilla, only lit.
            Vector3 moonCool = new(0.910f, 1.003f, 1.220f);
            float coolShare = MathHelper.Clamp(config.LightingNightDarkness / 0.56f, 0f, 1f);
            sky *= Vector3.Lerp(Vector3.One, Vector3.Lerp(Vector3.One, moonCool, coolShare) * nightFloor, nightT);
            // Full moon lifts the night back up (cool) → a full-moon night is clearly brighter
            // and bluer than a new-moon one.
            if (nightT > 0f)
                sky += new Vector3(0.05f, 0.07f, 0.11f) * (ShadowRenderer.MoonStrength() * nightT);
            return sky;
        }

        // ---- Windowed-interior detection + time-of-day room exposure ----------------
        // The exposure multiplier only ever applies to rooms that are LIT BY DAYLIGHT,
        // and "has windows" is the test for that. Mines/volcano are excluded outright
        // (scripted darkness, add-only rule), and windowless interiors — the farm cave,
        // the sewer, mod caves — have no DayTiles/NightTiles map entries and no
        // WindowLight sources, so they are never touched.
        private static GameLocation? _windowedCacheLoc;
        private static bool _windowedCached;

        /// <summary>How much of the sun PATCH under a window to seed, 0 to 1 - the visible half,
        /// which a window-art mod draws too. Owned by the flood stage, which eases it from the
        /// setting; this class only multiplies by it.</summary>
        internal static float WindowPatchScale = 1f;
        /// <summary>Live reason the window-glow seed did or did not run this rebuild (for the
        /// radiance_debug flood caption - answers \"is the seed even attempted\" without a log).</summary>
        internal static string LastWindowSeed = "?";
        /// <summary>How much daylight a window contributes to the ROOM's light, 0 to 1. Separate
        /// from the patch because the two are worth different things when another mod is drawing
        /// windows: it can paint a beam, but it cannot make the room's lighting know about it.</summary>
        internal static float WindowRoomScale = 1f;

        internal static bool IsWindowedInterior(GameLocation? location)
        {
            if (location == null || location.IsOutdoors)
                return false;
            if (location is StardewValley.Locations.MineShaft || location is StardewValley.Locations.VolcanoDungeon)
                return false;
            // DayTiles/NightTiles is a MAP property - time independent, safe to cache per visit.
            if (!ReferenceEquals(location, _windowedCacheLoc))
            {
                _windowedCacheLoc = location;
                // Windows are the map's day/night switching tiles — the standard mechanism
                // vanilla AND content-pack interiors use to make panes glow by day and go
                // dark at night — so the property's presence is a time-independent answer
                // (the light sources themselves vanish after dark).
                var props = location.Map?.Properties;
                _windowedCached = props != null
                    && (props.ContainsKey("DayTiles") || props.ContainsKey("NightTiles"));
            }
            if (_windowedCached)
                return true;
            // The light-source and glow signals change through the day AND with when the room
            // was entered, so they are checked LIVE, never cached: entering a farmhouse at
            // night must not freeze the answer as "no windows" for the whole visit (that
            // freeze is what left the floor beside a real window at bare sky in the morning).
            if (Game1.currentLightSources != null)
                foreach (var kv in Game1.currentLightSources)
                    if (kv.Value.lightContext.Value == LightSource.LightContext.WindowLight)
                        return true;
            return location.lightGlows is { Count: > 0 };
        }

        /// <summary>
        /// The room's light level for the current time of day, as a colour multiplier for
        /// the whole interior. (1,1,1) anywhere this doesn't apply. Vanilla snaps every
        /// interior to flat-bright at 6:00 and holds it there all day; a real room lit by
        /// its windows takes until mid-morning to fill, starts sinking before dusk and is
        /// genuinely dark at night — that difference is this curve. Applied in
        /// floodlight.fx as its own term so the GI-strength slider cannot swallow it.
        /// </summary>
        internal static void IndoorLook(GameLocation? location, ModConfig config,
            out Vector3 exposure, out float saturation)
        {
            exposure = Vector3.One;
            saturation = 1f;
            if (!IsWindowedInterior(location))
                return;

            float nowMinutes = GameClock.MinutesNow();
            float darkMinutes = ShadowRenderer.TrulyDarkMinutes();
            // Full daylight from ~8:50; dimming begins 90 min before truly-dark and
            // bottoms out an hour after. (The SDV clock starts at 6:00, so there is no
            // "before dawn" side — mornings always enter through the 6:00 ramp.)
            float dayFill = Math.Min(
                MathHelper.Clamp((nowMinutes - 360f) / 170f, 0f, 1f),
                MathHelper.Clamp((darkMinutes + 60f - nowMinutes) / 150f, 0f, 1f));
            // How much of the dim is NIGHT (cool, deep) vs twilight (warm, gentler).
            float nightness = MathHelper.Clamp((nowMinutes - (darkMinutes - 20f)) / 80f, 0f, 1f);

            float floorMorning = MathHelper.Clamp(1f - config.LightingIndoorDarkness * 0.78f, 0.2f, 1f);
            float floorNight = MathHelper.Clamp(1f - config.LightingNightDarkness * 0.8f, 0.16f, 1f);
            float level = MathHelper.Lerp(MathHelper.Lerp(floorMorning, floorNight, nightness), 1f, dayFill);
            if (Game1.isRaining || Game1.isLightning || Game1.isSnowing)
                level *= MathHelper.Lerp(0.88f, 1f, 1f - dayFill);   // overcast steals midday, night is dark already

            // COLOUR WALKS THE DAY, and it is not the same walk the brightness takes.
            // Before the sun is properly up a room is lit by open sky rather than by the sun
            // itself, and open sky is blue - so early morning is cool, the middle of the day
            // is neutral, the hours before dark run warm, and night settles back to blue.
            // Each phase is its own slow ramp handing over to the next, so the room is never
            // seen changing colour; it has simply changed by the time you look again.
            //
            // The colour is a MULTIPLIER that dims the warm channels hard and leaves blue
            // almost alone - which is what the eye reads as "lit by open sky". Two earlier
            // shapes both failed, and for opposite reasons worth keeping written down:
            //
            //   A gentle multiply (0.78, 0.90, 1.20) only made the room darker. Orange pine
            //   is about (190,140,90); it has barely any blue for a blue factor to lift, so
            //   red stayed on top and the wood just dimmed.
            //
            //   Mixing every pixel toward one blue-grey did move the hue, but it collapsed
            //   the whole picture onto a single chroma - walls, floor and furniture all
            //   arriving at the same colour - which reads as grey and washed out.
            //
            // Cutting red to a third while blue keeps nearly all of its strength moves each
            // pixel's own balance to blue AND keeps them different from each other, so the
            // room goes cold without going flat.
            Vector3 coolSky = new(0.40f, 0.55f, 1.00f);
            // Softened and started later in 1.5.5. At (1.00, 0.80, 0.55) on a ramp opening 200
            // minutes before dark, a saloon at six in the evening was already half way into the
            // cast, and a room whose art is warm wood to begin with came out uniformly orange:
            // measured median saturation 0.87 against 0.73 at noon in the same room. The lamps
            // were not the cause and never had been - all 66 of the saloon's map lights are
            // white - it was this. The hour before dark still runs warm, which is the point;
            // it no longer starts in the middle of the afternoon.
            Vector3 warmDusk = new(1.00f, 0.90f, 0.76f);
            Vector3 nightSky = new(0.36f, 0.52f, 1.00f);
            float morning = 1f - MathHelper.Clamp((nowMinutes - 360f) / 200f, 0f, 1f);   // 06:00 -> 09:20
            float evening = MathHelper.Clamp((nowMinutes - (darkMinutes - 110f)) / 110f, 0f, 1f);
            Vector3 chroma = Vector3.Lerp(Vector3.One, coolSky, morning);
            chroma = Vector3.Lerp(chroma, warmDusk, evening);
            chroma = Vector3.Lerp(chroma, nightSky, nightness);

            // AND A CAST MAY NOT OUTRUN THE ONE THE OUTDOOR NIGHT IS ALLOWED.
            //
            // This chroma is a MULTIPLIER on the finished picture, so the gap between its
            // channels is how hard it pushes a warm surface toward blue - and the numbers above
            // were written as a mood rather than measured against anything. nightSky spans 0.36
            // to 1.00: a blue-to-red ratio of 2.78, which takes 45% of the red out of every warm
            // thing in the room. Brick, pine, and the fire itself, which is the one object in the
            // room that IS the light.
            //
            // The outdoor night is the comparison that settles it, because nobody has ever
            // reported that one: its cast is (0.910, 1.003, 1.220), a ratio of 1.34, and even
            // that reaches a pixel through the GI slider (0.30 by default) because outdoors the
            // tint lives in the light FIELD. This one bypasses the slider on purpose - the room
            // level must not be something the GI slider can swallow - so it arrives at three
            // times the spread and three times the authority, call it nine times the cast.
            // Measured side by side against the same farmhouse with the mod off: a hearth that
            // vanilla draws as orange brick with a glow on the boards came out a black block in
            // a violet room. That is the whole "the fire indoors is black" report.
            //
            // Pulled back toward neutral until the ratio is one an outdoor night would be
            // allowed. The room still reads as cold - the level below is untouched and the level
            // is what "dark" means - it just stops repainting everything in it.
            const float MaxCastRatio = 1.7f;
            float castLo = Math.Min(chroma.X, Math.Min(chroma.Y, chroma.Z));
            float castHi = Math.Max(chroma.X, Math.Max(chroma.Y, chroma.Z));
            if (castLo > 0.0001f && castHi > castLo * MaxCastRatio)
            {
                // Solve lerp(1, chroma, t) for the t whose ends land exactly on the ratio, so a
                // cast already inside it is untouched and one outside is walked in, not clamped.
                float denom = (castHi - 1f) - MaxCastRatio * (castLo - 1f);
                if (Math.Abs(denom) > 0.0001f)
                    chroma = Vector3.Lerp(Vector3.One, chroma,
                        MathHelper.Clamp((MaxCastRatio - 1f) / denom, 0f, 1f));
            }

            // Brightness and colour must not fight: a strong cast is dark all by itself, so
            // the chroma is rescaled to carry exactly the luminance the curve above asked
            // for. The darkness sliders stay the only thing that decides how dark a room is,
            // whatever colour the hour happens to be.
            float chromaLum = 0.299f * chroma.X + 0.587f * chroma.Y + 0.114f * chroma.Z;
            exposure = chroma * (level / Math.Max(chromaLum, 0.0001f));

            // Dimming an image flattens its colour as a side effect. A small lift on the way
            // out keeps the deep blues and the wood browns reading as themselves rather than
            // as two shades of grey - the difference between a cold room and a washed-out one.
            float cast = Math.Max(morning, Math.Max(evening, nightness));
            saturation = MathHelper.Lerp(1f, 1.22f, cast);
        }

        internal void Dispose()
        {
            _lightmapTexture?.Dispose();
            _lightmapTexture = null;
        }
    }
}
