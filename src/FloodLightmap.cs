using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Phase L1 — flood-propagation lightmap (the Terraria technique): a small CPU grid over
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
        private const float SolidDecay = 0.45f;
        /// <summary>Cell values are stored ×0.5 in the texture so >1 (glow) survives; shader ×2.</summary>
        internal const float TexScale = 0.5f;

        private Vector3[] _cells = Array.Empty<Vector3>();
        private Vector3[] _blur = Array.Empty<Vector3>();
        private float[] _decay = Array.Empty<float>();
        private Color[] _pix = Array.Empty<Color>();
        private Texture2D? _tex;

        internal Texture2D? Texture => _tex;
        /// <summary>World tile coordinate of the map's (0,0) cell.</summary>
        internal Vector2 Origin;
        internal Vector2 MapSize;

        internal bool Build(GraphicsDevice gd, int w, int h, ModConfig config)
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null)
                return false;

            int tx0 = (int)Math.Floor(Game1.viewport.X / 64f) - Pad;
            int ty0 = (int)Math.Floor(Game1.viewport.Y / 64f) - Pad;
            int tw = Math.Max(1, w / 64 + 2) + Pad * 2;
            int th = Math.Max(1, h / 64 + 2) + Pad * 2;
            int count = tw * th;

            if (_cells.Length < count)
            {
                _cells = new Vector3[count];
                _blur = new Vector3[count];
                _decay = new float[count];
                _pix = new Color[count];
            }

            // ---- Seed pass: sky exposure + per-cell decay from the occluder grid ----
            // The flood is RELATIVE lighting: the game's own day/night & scripted darkness
            // stay in charge of the global level. Locations the game already darkens
            // (mines, volcano, any non-white ambient) run in ADD-ONLY mode — every cell
            // seeds at 1.0 so lamps enrich and cast shadows but nothing gets darker than
            // vanilla (multiplying on top of vanilla dark read as pitch black).
            bool outdoors = loc.IsOutdoors;
            bool vanillaDark = !outdoors &&
                (loc is StardewValley.Locations.MineShaft || loc is StardewValley.Locations.VolcanoDungeon
                 || Game1.ambientLight.R < 245 || Game1.ambientLight.G < 245 || Game1.ambientLight.B < 245);
            Vector3 sky = SkyColor(outdoors, config);
            var hf = ShadowRenderer.Height;
            for (int j = 0; j < th; j++)
            {
                for (int i = 0; i < tw; i++)
                {
                    int idx = j * tw + i;
                    bool solid = false;
                    if (hf != null)
                    {
                        try { solid = hf.GetHeightAt(loc, tx0 + i, ty0 + j) > 0; }
                        catch { hf = null; }
                    }
                    _decay[idx] = solid ? SolidDecay : AirDecay;
                    // Open cells receive direct sky light; occluded cells only what floods in
                    // from their surroundings → soft shade under trees/buildings for free.
                    _cells[idx] = vanillaDark ? Vector3.One : (solid ? Vector3.Zero : sky);
                }
            }

            // ---- Seed the game's real light sources (lamps, torches, fires, windows) ----
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                foreach (var kv in lights.Values)
                {
                    var ls = kv;
                    int ci = (int)(ls.position.Value.X / 64f) - tx0;
                    int cj = (int)(ls.position.Value.Y / 64f) - ty0;
                    if (ci < 0 || ci >= tw || cj < 0 || cj >= th)
                        continue;
                    // INDIRECT spill only (~1/3 strength): the crisp direct pool + its per-light
                    // shadows are computed analytically in floodlight.fx; the flood carries the
                    // bounce-like glow that bends around corners and through doorways.
                    float inten = MathHelper.Clamp(0.55f + 0.30f * ls.radius.Value, 0.6f, 1.7f) * 0.35f;
                    var seed = new Vector3(1.00f, 0.83f, 0.58f) * inten;
                    int idx = cj * tw + ci;
                    _cells[idx] = Vector3.Max(_cells[idx], seed);
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
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        int jj = j + dj;
                        if (jj < 0 || jj >= th) continue;
                        for (int di = -1; di <= 1; di++)
                        {
                            int ii = i + di;
                            if (ii < 0 || ii >= tw) continue;
                            acc += _cells[jj * tw + ii];
                            n++;
                        }
                    }
                    _blur[j * tw + i] = acc / Math.Max(1, n);
                }
            }
            for (int idx = 0; idx < count; idx++)
            {
                Vector3 v = _cells[idx] + _blur[idx] * 0.18f;
                _pix[idx] = new Color(
                    (byte)MathHelper.Clamp(v.X * 255f * TexScale, 0f, 255f),
                    (byte)MathHelper.Clamp(v.Y * 255f * TexScale, 0f, 255f),
                    (byte)MathHelper.Clamp(v.Z * 255f * TexScale, 0f, 255f), (byte)255);
            }

            if (_tex == null || _tex.Width != tw || _tex.Height != th)
            {
                _tex?.Dispose();
                _tex = new Texture2D(gd, tw, th, false, SurfaceFormat.Color);
            }
            _tex.SetData(_pix, 0, count);
            Origin = new Vector2(tx0, ty0);
            MapSize = new Vector2(tw, th);
            return true;
        }

        private void Propagate(ref Vector3 carry, int idx)
        {
            float d = _decay[idx];
            carry *= d;
            Vector3 c = _cells[idx];
            carry = Vector3.Max(carry, c);
            _cells[idx] = carry;
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
            float dd = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            float warm = MathHelper.Clamp((Math.Abs(dd) - 0.55f) / 0.45f, 0f, 1f);
            Vector3 sky = Vector3.Lerp(new Vector3(1f, 1f, 1f), new Vector3(1.03f, 0.96f, 0.88f), warm);
            if (Game1.isRaining)
                sky *= 0.93f;   // gentle overcast dimming; vanilla already grays rain out
            return sky;
        }

        internal void Dispose()
        {
            _tex?.Dispose();
            _tex = null;
        }
    }
}
