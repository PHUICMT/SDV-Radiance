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
        private float[] _lightDecay = Array.Empty<float>();
        private Color[] _lightmapPixels = Array.Empty<Color>();
        private Texture2D? _lightmapTexture;

        internal Texture2D? Texture => _lightmapTexture;
        /// <summary>World tile coordinate of the map's (0,0) cell.</summary>
        internal Vector2 Origin;
        internal Vector2 MapSize;

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
                _lightDecay = new float[count];
                _lightmapPixels = new Color[count];
            }

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
            bool vanillaDark = !outdoors && (scriptedDark
                 || Game1.ambientLight.R < 245 || Game1.ambientLight.G < 245 || Game1.ambientLight.B < 245);
            // A HOUSE at midnight is not a mine. The game tints it down a little and then leaves
            // it evenly lit, so the fireplace and the lamps have nothing to stand out against.
            // Add a second, gentle layer there - and let the night-darkness slider drive it,
            // which is the setting players have been given for exactly this and which did
            // nothing at all on this lighting model until now.
            float nightSeed = vanillaDark && !scriptedDark
                ? MathHelper.Clamp(1f - config.LightingNightDarkness * 0.38f, 0.45f, 1f)
                : 1f;
            Vector3 sky = SkyColor(outdoors, config);
            var surf = SurfaceMap.For(location);
            for (int j = 0; j < th; j++)
            {
                for (int i = 0; i < tw; i++)
                {
                    int idx = j * tw + i;
                    bool solid = false;
                    // Sky occlusion only makes sense OUTDOORS. Interiors are already under a roof,
                    // and every interior tile carries Front-layer art (upper walls), which the
                    // height classifier reports as Roof — treating those as sky occluders zeroed
                    // the whole room's lightmap (black scene, then the warm lamp seed flooded it
                    // orange). Indoors, leave every cell open so ambient + lamps light it normally.
                    // Only WALLS and ROOF/canopy block sky light. Decks (piers, bridges) have
                    // height 1 but are walk-on-top surfaces OPEN to the sky — treating them as
                    // solid turned the whole beach pier into a giant dark pool. Water is open too.
                    if (surf != null && outdoors)
                        solid = surf.BlocksLight(tx0 + i, ty0 + j);
                    _lightDecay[idx] = solid ? SolidDecay : AirDecay;
                    // Open cells receive direct sky light; occluded cells only what floods in
                    // from their surroundings → soft shade under trees/buildings for free.
                    _lightCells[idx] = vanillaDark ? new Vector3(nightSeed) : (solid ? sky * OccludedSeed : sky);
                }
            }

            // ---- Seed the game's real light sources (lamps, torches, fires, windows) ----
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                foreach (var kv in lights.Values)
                {
                    var ls = kv;
                    if (!ShadowRenderer.WindowGlowing(location, ls))   // stale/dark window: not emitting
                        continue;
                    int ci = (int)(ls.position.Value.X / 64f) - tx0;
                    int cj = (int)(ls.position.Value.Y / 64f) - ty0;
                    if (ci < 0 || ci >= tw || cj < 0 || cj >= th)
                        continue;
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
                    float inten = MathHelper.Clamp(0.55f + 0.30f * ls.radius.Value, 0.6f, 1.7f) * (outdoors ? 1.25f : 0.5f);
                    // The same midday sink the DIRECT pools got ("a street lamp at noon reads as
                    // glass"): these seeds never had it, which went unnoticed while the flat bounce
                    // held the whole outdoor field near 1.28 — every cell glowed a little, so lamp
                    // cells did not stand out. With the bounce weighted (open ground now sits at
                    // exactly sky), a daylight lantern's >1.0 seed became the only thing feeding
                    // the shader's glow term, and it read as a bright pool at two in the afternoon.
                    // Full strength returns by 08:00/17:00; night and indoors are untouched.
                    if (outdoors)
                        inten *= 1f - 0.65f * (1f - MathHelper.Clamp(Math.Abs(GameClock.MinutesNow() - 750f) / 270f, 0f, 1f));
                    // TWO-TONE rooms: an indoor window is DAYLIGHT (cool, slightly blue) while
                    // lamps and fires stay warm — the warm-vs-cool split across a room is what
                    // makes it read as cinematic instead of uniformly orange. Outdoor window
                    // lights (town houses at night) are lamp-lit from inside, so they stay warm.
                    bool coolDaylight = !outdoors && ls.lightContext.Value == LightSource.LightContext.WindowLight;
                    Vector3 seedColor = new(1.00f, 0.83f, 0.58f);
                    if (coolDaylight)
                    {
                        // The sky is what is on the other side of this window, so it follows the
                        // clock and the calendar instead of being one fixed daylight colour (see
                        // WindowDaylight - that constant is why rooms stayed daylit at 2am).
                        ShadowRenderer.WindowDaylight(out Vector3 sunColour, out float sunStrength);
                        seedColor = sunColour;
                        inten *= sunStrength * WindowRoomScale;
                    }
                    // One seed cell; the bilinear upsample + the 5×5 bounce spread it into a soft
                    // pool. (A wide radial seed disc was tried to force a bigger pool but never read
                    // as wider on the coarse grid — reverted to keep it simple.)
                    int idx = cj * tw + ci;
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
                            int jj = cj + k;
                            int ii = ci + (int)Math.Round(lean * k);
                            if (jj >= th || ii < 0 || ii >= tw)
                                break;
                            float f = 1.0f - 0.85f * (k / (float)(steps + 1));
                            int sIdx = jj * tw + ii;
                            _lightCells[sIdx] = Vector3.Max(_lightCells[sIdx], shaft * f);
                        }
                    }
                    // OUTDOOR lit storefronts/windows at night pour WARM light DOWN onto the
                    // path in front (a saloon's windows lighting the ground). Short fading
                    // column, softened afterwards by the bilinear sample + the wide bounce.
                    else if (outdoors && ls.lightContext.Value == LightSource.LightContext.WindowLight)
                    {
                        var spill = new Vector3(1.00f, 0.84f, 0.60f);
                        for (int k = 1; k <= 4; k++)
                        {
                            int jj = cj + k;
                            if (jj >= th)
                                break;
                            float f = (1.0f - 0.22f * k) * inten * 2.2f;
                            int sIdx = jj * tw + ci;
                            _lightCells[sIdx] = Vector3.Max(_lightCells[sIdx], spill * f);
                        }
                    }
                }
            }

            // ---- Flood: two rounds of 4 directional sweeps (Terraria-style) ----
            for (int round = 0; round < 2; round++)
            {
                for (int j = 0; j < th; j++)          // left → right, then right → left
                {
                    Vector3 carry = Vector3.Zero;
                    for (int i = 0; i < tw; i++) Propagate(ref carry, j * tw + i);
                    carry = Vector3.Zero;
                    for (int i = tw - 1; i >= 0; i--) Propagate(ref carry, j * tw + i);
                }
                for (int i = 0; i < tw; i++)          // top → bottom, then bottom → top
                {
                    Vector3 carry = Vector3.Zero;
                    for (int j = 0; j < th; j++) Propagate(ref carry, j * tw + i);
                    carry = Vector3.Zero;
                    for (int j = th - 1; j >= 0; j--) Propagate(ref carry, j * tw + i);
                }
            }

            // ---- Fake one indirect bounce: 3×3 blur folded back in softly ----
            for (int j = 0; j < th; j++)
            {
                for (int i = 0; i < tw; i++)
                {
                    var acc = Vector3.Zero;
                    int n = 0;
                    // 5×5 (was 3×3): a wider bounce spreads each light into a softer, fluffier
                    // pool that fades out gradually instead of ending within one tile.
                    for (int dj = -2; dj <= 2; dj++)
                    {
                        int jj = j + dj;
                        if (jj < 0 || jj >= th) continue;
                        for (int di = -2; di <= 2; di++)
                        {
                            int ii = i + di;
                            if (ii < 0 || ii >= tw) continue;
                            acc += _lightCells[jj * tw + ii];
                            n++;
                        }
                    }
                    _blurredLightCells[j * tw + i] = acc / Math.Max(1, n);
                }
            }
            // Walls/roofs are ELEVATED surfaces in a top-down view: the dark cell value models
            // light blocked at ground level, but the pixels DRAWN there are facades and rooftops
            // in full daylight — lift them to ambient so buildings never render dimmer than the
            // ground they stand on (dark cells still attenuate propagation for the spill/shade).
            Vector3 lift = sky * (outdoors ? 0.92f : 0.85f);
            for (int idx = 0; idx < count; idx++)
            {
                // The bounce FILLS SHADE. It used to be a flat add, which put every open outdoor
                // cell at ~1.28 in broad daylight — and floodlight.effect reads anything over 1.0 as a
                // lamp core and adds a glow for it, so open ground got a few percent of extra light
                // it was never meant to have. On a winter beach, where snow is already close to
                // white and most of the screen is open, that pushed the whole field past clipping
                // and the detail in the snow disappeared. Weighting the bounce by how far the cell
                // is BELOW full light leaves open ground at exactly sky, still lifts real shade,
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

            if (_lightmapTexture == null || _lightmapTexture.Width != tw || _lightmapTexture.Height != th)
            {
                _lightmapTexture?.Dispose();
                _lightmapTexture = new Texture2D(graphicsDevice, tw, th, false, SurfaceFormat.Color);
            }
            _lightmapTexture.SetData(_lightmapPixels, 0, count);
            Origin = new Vector2(tx0, ty0);
            MapSize = new Vector2(tw, th);
            return true;
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
                amb = MathHelper.Clamp(amb * MathHelper.Lerp(0.42f, 1f, fill), 0.16f, 1f);
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
            int t = Game1.timeOfDay;
            int trulyDark;
            try { trulyDark = Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { trulyDark = 2000; }
            int mins = (t / 100) * 60 + t % 100;
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            float nightT = MathHelper.Clamp((mins - (m1 - 60)) / 60f, 0f, 1f);
            // Our flood gently DIMS the open night ground so lamp pools stand out. Kept MILD
            // (×0.82, was ×0.55 which turned a lampless farm nearly pitch black) — lamp seeds
            // are pushed above 1.0 so they show through the max() without needing a dark ground.
            sky *= MathHelper.Lerp(1f, 0.62f, nightT);
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
                if (!_windowedCached && Game1.currentLightSources != null)
                {
                    // Fallback for maps that skip DayTiles: any window light source counts.
                    foreach (var kv in Game1.currentLightSources)
                        if (kv.Value.lightContext.Value == LightSource.LightContext.WindowLight)
                        { _windowedCached = true; break; }
                }
            }
            return _windowedCached;
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
            Vector3 warmDusk = new(1.00f, 0.80f, 0.55f);
            Vector3 nightSky = new(0.36f, 0.52f, 1.00f);
            float morning = 1f - MathHelper.Clamp((nowMinutes - 360f) / 200f, 0f, 1f);   // 06:00 -> 09:20
            float evening = MathHelper.Clamp((nowMinutes - (darkMinutes - 200f)) / 170f, 0f, 1f);
            Vector3 chroma = Vector3.Lerp(Vector3.One, coolSky, morning);
            chroma = Vector3.Lerp(chroma, warmDusk, evening);
            chroma = Vector3.Lerp(chroma, nightSky, nightness);

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
