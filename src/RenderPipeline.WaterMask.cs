using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - the WATER MASK builder: a pixel-accurate map of where water really
    /// is. R = effect coverage (per-pixel art classification, opaque art carved out),
    /// G = shoreline-march water (floats never block; land-connected structures block as
    /// whole tiles), B = precomputed smoothed distance-to-waterline (the reflection anchor).
    /// Everything is cached: per-art classifications forever, the assembled mask until the
    /// camera crosses a tile boundary.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        // Tiles whose water came ONLY from animated-art nomination (fountains, waterfalls):
        // they join the effect channel but must be cleared from the march channel.
        private bool[]? _animOnlyTileBuf;
        // Same tiles, remembered BEFORE the pool-region pass consumes the buffer — drives a
        // SOFT mask value in Pass E (a fountain should barely shimmer, not churn like a lake).
        private bool[]? _animSoftTileBuf;

        /// <summary>Resolve the 16×16 source art of a map tile (first frame for animated tiles).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D tex, out Rectangle src)
            => TryTileArt(layer, tx, ty, out tex, out src, out _);

        /// <summary>As above, also reporting whether the tile is ANIMATED — animation is a strong
        /// "this is water/flowing art" signal (fountains, waterfalls, the beach surf line).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D tex, out Rectangle src, out bool animated)
        {
            tex = null!;
            src = default;
            animated = false;
            if (layer == null || tx < 0 || ty < 0 || tx >= layer.LayerWidth || ty >= layer.LayerHeight)
                return false;
            var t = layer.Tiles[tx, ty];
            if (t is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
            {
                t = at.TileFrames[0];
                animated = true;
            }
            if (t?.TileSheet == null)
                return false;
            if (!_sheetTexCache.TryGetValue(t.TileSheet.ImageSource, out Texture2D? sheet))
            {
                try { sheet = Game1.content.Load<Texture2D>(t.TileSheet.ImageSource); }
                catch { sheet = null; }
                _sheetTexCache[t.TileSheet.ImageSource] = sheet;
            }
            if (sheet == null)
                return false;
            var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
            if (ib.Width != 16 || ib.Height != 16)
                return false;
            tex = sheet;
            src = new Rectangle(ib.X, ib.Y, 16, 16);
            return true;
        }

        /// <summary>Painted-water test for a single art pixel: blue-dominant or teal/foam.
        /// Matches the shader's colour gates, but runs on the STATIC source art (stable,
        /// classify once per tile art) instead of the composited frame.</summary>
        private static bool WaterColor(Color c)
        {
            if (c.A < 200)
                return false;
            if (c.B > c.R + 14 && c.B + 10 >= c.G) return true;   // blue water
            // Teal / shallow edge — measured against the real tilesheets (2026-07-21):
            // the old loose gate (B > R+6) classified plain GRASS greens (avg 22,104,53 —
            // 227 outdoor tiles' worth) and rippled bushes/meadows near rivers. Real water
            // teal keeps B close to G (within 25) and clearly above R.
            if (c.G > c.R + 10 && c.B > c.R + 12 && c.B >= c.G - 25) return true;
            return false;
        }

        /// <summary>16×16 painted-water classification of one tile art, cached per (texture, rect,
        /// foam). With <paramref name="foam"/> (animated tiles that touch core water — the surf
        /// line), bright unsaturated wash pixels count as water too: white wave foam fails every
        /// hue gate, which left dead un-effected bands along the tide line.</summary>
        private bool[] ClassifyBits(Texture2D tex, Rectangle src, bool foam = false)
        {
            var key = (tex, src, foam);
            if (_waterBitsCache.TryGetValue(key, out bool[]? bits))
                return bits;
            bits = new bool[256];
            _artBuf ??= new Color[256];
            try
            {
                tex.GetData(0, src, _artBuf, 0, 256);
                for (int p = 0; p < 256; p++)
                {
                    Color c = _artBuf[p];
                    bool w = WaterColor(c);
                    if (!w && foam && c.A >= 200)
                    {
                        int maxc = Math.Max(c.R, Math.Max(c.G, c.B));
                        int minc = Math.Min(c.R, Math.Min(c.G, c.B));
                        w = maxc >= 190 && maxc - minc <= 25 && c.B >= c.R;   // white/pale foam
                    }
                    bits[p] = w;
                }
            }
            catch { /* leave all-false */ }
            _waterBitsCache[key] = bits;
            return bits;
        }

        /// <summary>How many of a classification's 256 bits are set.</summary>
        private static int CountBits(bool[] bits)
        {
            int n = 0;
            for (int p = 0; p < bits.Length; p++)
                if (bits[p]) n++;
            return n;
        }

        /// <summary>16×16 puddle classification of one tile art, cached: flat BLUE-GREY pixels
        /// (low saturation, blue at least a nudge over red, mid brightness) — the look of the
        /// walkable shallow pools that are plain ground in map data. Warm-grey stone, sand and
        /// grass all fail one of the gates.</summary>
        private (bool[] bits, int count) PuddleBits(Texture2D tex, Rectangle src)
        {
            var key = (tex, src);
            if (_puddleBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0;
            _artBuf ??= new Color[256];
            try
            {
                tex.GetData(0, src, _artBuf, 0, 256);
                for (int p = 0; p < 256; p++)
                {
                    Color c = _artBuf[p];
                    int maxc = Math.Max(c.R, Math.Max(c.G, c.B));
                    int minc = Math.Min(c.R, Math.Min(c.G, c.B));
                    // Measured from the island dig-site pool art (palette: (163,177,165),
                    // (144,157,158), (153,163,162), (112,134,141) — grey-GREEN, R always the
                    // lowest channel, B only +2..+29 over R, and B within ~12 of G). Guards
                    // against false positives: sand/warm stone are R-dominant, pure-neutral
                    // concrete/stone (B==R) fails the +2, dark cave floors fail brightness —
                    // and DARK FOREST GRASS (cool green, e.g. (60,90,70)) passed every old
                    // gate and rippled whole meadows at night: it fails the two new ones
                    // (B ≥ G−12: pool art is grey, grass keeps B well under G; G−R ≤ 25:
                    // grass is strongly green-dominant, pool art never exceeds +22).
                    bool puddleish = c.A >= 200
                        && maxc - minc <= 34          // flat / unsaturated
                        && c.B >= c.R + 2             // cool tint (never true for warm ground)
                        && c.G >= c.R                 // R is the lowest channel
                        && c.B >= c.G - 12            // grey, not green (kills grass)
                        && c.G - c.R <= 25            // pool art is never strongly green-dominant
                        && maxc >= 55 && maxc <= 200; // mid brightness (not shadow, not foam)
                    if (bits[p] = puddleish)
                        n++;
                }
            }
            catch { /* leave all-false */ }
            entry = (bits, n);
            _puddleBitsCache[key] = entry;
            return entry;
        }

        /// <summary>16×16 opacity bits + opaque-pixel count of one tile art, cached — used to
        /// carve piers/bridges/pads out of the water mask (count decides march-blocking).
        /// A tile whose opaque art is MOSTLY painted water (a waterfall, an animated water
        /// edge) is no structure at all — skipped entirely, or it carved whole water tiles
        /// into bright untouched patches. Below that bar, plain opacity rules: plank art
        /// keeps its dark blue-ish shadow pixels, so piers/bridges still block the march.</summary>
        private (bool[] bits, int count) SolidBits(Texture2D tex, Rectangle src)
        {
            var key = (tex, src);
            if (_solidBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0, w = 0;
            _artBuf ??= new Color[256];
            try
            {
                tex.GetData(0, src, _artBuf, 0, 256);
                for (int p = 0; p < 256; p++)
                {
                    if (bits[p] = _artBuf[p].A >= 128)
                    {
                        n++;
                        if (WaterColor(_artBuf[p])) w++;
                    }
                }
                if (w * 10 >= n * 6)   // ≥60% of the opaque art is water → water overlay, not structure
                {
                    Array.Clear(bits, 0, 256);
                    n = 0;
                }
            }
            catch { /* leave all-false */ }
            entry = (bits, n);
            _solidBitsCache[key] = entry;
            return entry;
        }

        /// <summary>8-way one-tile dilation of a tile flag grid (src → dst).</summary>
        private static void Dilate8(bool[] src, bool[] dst, int tilesW, int tilesH)
        {
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool l = i > 0, r = i < tilesW - 1, u = j > 0, d = j < tilesH - 1;
                    dst[idx] = src[idx]
                        || (l && src[idx - 1]) || (r && src[idx + 1])
                        || (u && src[idx - tilesW]) || (d && src[idx + tilesW])
                        || (l && u && src[idx - tilesW - 1]) || (r && u && src[idx - tilesW + 1])
                        || (l && d && src[idx + tilesW - 1]) || (r && d && src[idx + tilesW + 1]);
                }
            }
        }

        /// <summary>
        /// Build (or reuse) the per-tile water mask for the visible area, aligned to the
        /// viewport. Returns false (and skips the water stage) when the location has no
        /// water on screen, so we never distort a waterless frame.
        ///
        /// The heavy pixel work runs on a WORKER thread (see RenderPipeline.WaterMask.Async.cs):
        /// this method only gathers game-state inputs, launches/polls the compose job, and
        /// uploads finished results — the 8-23 ms monolithic rebuild on every tile crossing
        /// was THE walking-near-water stutter. While a job is in flight the old mask keeps
        /// rendering (world-anchored content + padded window = no visible edge).
        /// </summary>
        private bool BuildWaterMask(int w, int h)
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null)
                return false;

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            // The window is PADDED past the viewport: 2 tiles left/right, 4 above. A
            // column's waterline anchor (Pass D run-top) must stay WORLD-anchored while
            // its shoreline scrolls just past the screen edge — anchored at the mask's
            // own first row instead, the whole reflection re-based and vanished in ONE
            // step as the player walked away, rather than fading out.
            int startTileX = (int)Math.Floor(vx / 64f) - 2;
            int startTileY = (int)Math.Floor(vy / 64f) - 4;
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed
            // out — parts of the screen simply had no water mask (no ripple/reflection).
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + 6);
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + 6);

            // Camera-follow params are valid for WHATEVER mask is currently bound (old or
            // new) — the mask content is tile-anchored; sub-tile scroll lives here.
            _waterTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
            _waterWorldTileOffset = new Vector2(vx / 64f, vy / 64f);

            // Poll the in-flight compose FIRST: apply it if it finished and still matches
            // the wanted window; keep showing the old mask while it runs; discard it if
            // the camera crossed again mid-compose (fall through to a fresh gather).
            if (_waterJob is { } job)
            {
                if (!job.Done)
                    return _waterAny;
                _waterJob = null;
                if (job.Failed)
                {
                    if (!_loggedWaterJobFail) { _monitor.Log("Water mask compose failed once; rebuilding synchronously.", LogLevel.Warn); _loggedWaterJobFail = true; }
                }
                else if (job.Loc == loc && job.Tx == startTileX && job.Ty == startTileY
                    && job.TilesW == tilesW && job.TilesH == tilesH)
                {
                    ApplyWaterMask(job);
                    return _waterAny;
                }
            }

            // The mask content is TILE-ANCHORED (sub-tile camera scroll is handled by the
            // WorldTileOffset shader param), so it only changes when the view crosses a tile
            // boundary — rebuilding the pixel grid every frame was a walking-stutter tax.
            // The 10 s safety refresh only exists to pick up rare map mutations (a bridge
            // built, ice melting); everything routine invalidates via location/origin keys.
            if (_waterMask != null && loc == _lastWaterLoc && startTileX == _lastWaterTx && startTileY == _lastWaterTy
                && _lastWaterHookVer == WaterDrawHook.Version
                && _waterMask.Width == tilesW * 16 && Game1.ticks - _lastWaterTick < 600)
            {
                _waterMaskSize = new Vector2(tilesW, tilesH);
                return _waterAny;
            }

            var njob = GatherWaterMask(loc, startTileX, startTileY, tilesW, tilesH);

            // First sight of a location (or a resize): compose synchronously — there is no
            // valid old mask to show behind a background build (warp frame; hitch invisible).
            if (_waterMask == null || loc != _lastWaterLoc || _waterMask.Width != tilesW * 16)
            {
                ComposeWaterMask(njob);
                ApplyWaterMask(njob);
                return _waterAny;
            }

            njob.Task = System.Threading.Tasks.Task.Run(() =>
            {
                try { ComposeWaterMask(njob); }
                catch { njob.Failed = true; }
                finally { njob.Done = true; }
            });
            _waterJob = njob;
            return _waterAny;   // old mask renders this frame; the swap lands when compose does
        }

        // ---- per-frame sprite mask (things ON the water must not ripple) ----

        private RenderTarget2D? _spriteMaskRT;
        private SpriteBatch? _spriteMaskBatch;
        internal bool SpriteMaskReady;

        /// <summary>
        /// Bake every sprite that could be standing ON water — NPCs, farm animals
        /// (swimming ducks!), critters — into a screen-space mask, called from
        /// Display.RenderingWorld (the only spot where a render-target swap is safe).
        /// The water shader excludes these pixels from ripple/mirror so sprites never
        /// distort, while the water beside them keeps animating. Positions mirror the
        /// game's own draw math (bottom-centre at the collision box feet).
        /// </summary>
        public void BakeWaterSpriteMask()
        {
            SpriteMaskReady = false;
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !_waterAny)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_spriteMaskRT == null || _spriteMaskRT.Width != w || _spriteMaskRT.Height != h)
            {
                _spriteMaskRT?.Dispose();
                _spriteMaskRT = new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _spriteMaskBatch ??= new SpriteBatch(_device);

            try
            {
                _device.SetRenderTarget(_spriteMaskRT);
                _device.Clear(Color.Transparent);
                var sb = _spriteMaskBatch;
                sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

                // NPCs + monsters: bottom-centre at the collision-box feet, scale 4 —
                // the same anchor the game draws them at (small bob/jump offsets are
                // sub-pixel enough for an exclusion mask).
                foreach (NPC c in loc.characters)
                {
                    if (c?.Sprite?.Texture == null || c.IsInvisible)
                        continue;
                    StampSprite(sb, c.Sprite.Texture, c.Sprite.SourceRect, c.GetBoundingBox());
                }
                // Farm animals (ducks paddle straight into ponds).
                foreach (var a in loc.animals.Values)
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    StampSprite(sb, a.Sprite.Texture, a.Sprite.SourceRect, a.GetBoundingBox());
                }
                // Critters (seagulls, birds, frogs): base Critter.draw puts the 16×16
                // sprite's bottom edge at position.Y, centred on position.X.
                if (loc.critters != null)
                {
                    foreach (var cr in loc.critters)
                    {
                        if (cr?.sprite?.Texture == null)
                            continue;
                        Vector2 tl = Game1.GlobalToLocal(Game1.viewport, cr.position + new Vector2(-32f, -64f));
                        sb.Draw(cr.sprite.Texture, tl, cr.sprite.SourceRect, Color.White,
                            0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                    }
                }

                sb.End();
                SpriteMaskReady = true;
            }
            finally
            {
                _device.SetRenderTargets(prev);
            }
        }

        private static void StampSprite(SpriteBatch sb, Texture2D tex, Rectangle src, Rectangle bb)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(bb.Center.X, bb.Bottom));
            sb.Draw(tex, feet, src, Color.White, 0f,
                new Vector2(src.Width / 2f, src.Height), 4f, SpriteEffects.None, 0f);
        }

        // ---- helpers -------------------------------------------------------

        // Wrapped like the cloud shadow's Time: unbounded seconds eventually push the
        // shader noise hashes past float/sin precision, which reads as hard axis-aligned
        // seams. 100-minute period, multiple of 60 so whole seconds stay whole.
        private static float Time() => (Game1.ticks % 360000) / 60f;

        /// <summary>Debug: save the water masks to PNG (R=effect, G=march, B=edge distance).</summary>
        public string DumpMasks(string dir)
        {
            if (_waterMask == null)
                return "no water mask built (stand near water first)";
            string p1 = System.IO.Path.Combine(dir, "radiance-watermask.png");
            using (var fs = System.IO.File.Create(p1))
                _waterMask.SaveAsPng(fs, _waterMask.Width, _waterMask.Height);
            if (_waterMaskCore != null)
            {
                string p2 = System.IO.Path.Combine(dir, "radiance-watercore.png");
                using (var fs = System.IO.File.Create(p2))
                    _waterMaskCore.SaveAsPng(fs, _waterMaskCore.Width, _waterMaskCore.Height);
            }
            return $"saved {p1} (origin tile {_lastWaterTx},{_lastWaterTy}, player tile {Game1.player?.TilePoint})";
        }
    }
}
