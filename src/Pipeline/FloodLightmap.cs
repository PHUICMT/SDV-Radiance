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

            // Rebuild throttle: the flood changes slowly (time, lights, viewport), but a full
            // rebuild is ~1k cross-mod HF lookups + CPU sweeps EVERY frame — a walking-stutter
            // tax. Reuse the last texture unless the view crossed a tile boundary or ~3 frames
            // passed (GI refreshes at ~20 Hz; the analytic direct light still runs at 60).
            if (_lightmapTexture != null && tx0 == _lastStartTileX && ty0 == _lastStartTileY
                && _lightmapTexture.Width == tw && _lightmapTexture.Height == th && Game1.ticks - _lastBuildTick < 3)
                return true;
            _lastStartTileX = tx0; _lastStartTileY = ty0; _lastBuildTick = Game1.ticks;

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
            bool vanillaDark = !outdoors &&
                (location is StardewValley.Locations.MineShaft || location is StardewValley.Locations.VolcanoDungeon
                 || Game1.ambientLight.R < 245 || Game1.ambientLight.G < 245 || Game1.ambientLight.B < 245);
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
                    _lightCells[idx] = vanillaDark ? Vector3.One : (solid ? sky * OccludedSeed : sky);
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
                    float inten = MathHelper.Clamp(0.55f + 0.30f * ls.radius.Value, 0.6f, 1.7f) * (outdoors ? 1.25f : 0.5f)
                                * ShadowRenderer.FireFlicker(ls.position.Value, ls.textureIndex.Value);
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
                    Vector3 seedColor = coolDaylight ? new Vector3(0.82f, 0.92f, 1.10f) : new Vector3(1.00f, 0.83f, 0.58f);
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
                        var shaft = new Vector3(0.60f, 0.66f, 0.76f);
                        for (int k = 1; k <= 3; k++)
                        {
                            int jj = cj + k;
                            if (jj >= th)
                                break;
                            float f = 1.0f - 0.28f * k;
                            int sIdx = jj * tw + ci;
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
                return new Vector3(amb);
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

        internal void Dispose()
        {
            _lightmapTexture?.Dispose();
            _lightmapTexture = null;
        }
    }
}
