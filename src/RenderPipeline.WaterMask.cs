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

        // How far past the viewport the mask window reaches, in tiles. TOP padding is the one that
        // matters: a column's reflection is anchored at the top of its water run, so once a pier or
        // a bank scrolls further than this above the screen its anchor is gone and the reflection
        // it was casting fades out mid-walk. 4 was not enough to hold a dock's reflection while
        // walking down it. The cost is real — every compose pass is O(mask pixels) and each tile
        // row is 16 texel rows — so this is the knob to turn back down if the water pass gets
        // expensive on big screens.
        private const int PadTop = 4;
        private const int PadBottom = 2;
        private const int PadSide = 2;

        // Waterline anchors kept in WORLD texel coordinates, one per mask column.
        //
        // Pass D anchors a column's reflection at the top of its water run, but that row is
        // MASK-LOCAL. Once the real shoreline scrolls above the window the run simply starts at
        // row 0, and the anchor silently re-bases to the top of the screen: the reflection a pier
        // was casting jumps in one step and then fades as you keep walking away from it. Padding
        // the window only buys a few more tiles of the same behaviour, and every extra tile row
        // costs 16 texel rows in every compose pass.
        //
        // Remembering where each column's shoreline really was lets a cut-off run keep the true
        // anchor. It lands above the window, i.e. NEGATIVE in mask space, and that is exactly
        // right: the distance below it just keeps growing, so the reflection fades the way
        // distance says it should instead of the way the camera happens to be framed.
        private readonly Dictionary<int, int> _colAnchor = new();
        private GameLocation? _colAnchorLoc;

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
                // Shadow art ships in its OWN tilesheets, and the name says so. Every shadow sheet
                // in the map dump is called *Shadow(s) / *ShadowTilesheet / *CanopyShadow (15 of
                // them, vanilla and modded), so the sheet identity is a second, colour-independent
                // way to know a tile is a cast shadow and must never carve the water it falls on.
                if (sheet != null
                    && t.TileSheet.ImageSource.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                    _shadowSheets.Add(sheet);
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
            // Teal / shallow edge — measured against the real tilesheets (2026-07-21, re-measured
            // 2026-07-22): the old loose gate (B > R+6) classified plain GRASS greens and rippled
            // meadows; at G-25 the DARK grass/fern strip tiles (summer avg (23,97,72), spring fern
            // clumps — full 245-256px tiles) still passed at G-B exactly 23-25 and waved on land.
            // Real teal water pixels sit at G-B ≤ 19 on every sheet (beach tide pools, pond edges);
            // the 20-22 band is EMPTY, so G-20 splits them cleanly.
            if (c.G > c.R + 10 && c.B > c.R + 12 && c.B >= c.G - 20) return true;
            return false;
        }

        /// <summary>16×16 painted-water classification of one tile art, cached per (texture, rect,
        /// foam). With <paramref name="foam"/> (animated tiles that touch core water — the surf
        /// line), bright unsaturated wash pixels count as water too: white wave foam fails every
        /// hue gate, which left dead un-effected bands along the tide line.</summary>
        /// <summary>Whole-sheet pixel array for a tilesheet, read back once (main thread) and
        /// cached. Returns null for over-cap sheets (caller falls back to per-region GetData) or
        /// on failure. The single readback replaces one-per-tile readbacks (each a GPU stall).</summary>
        private Color[]? EnsureSheetPixels(Texture2D tex)
        {
            if (_sheetPixCache.TryGetValue(tex, out Color[]? sheet))
                return sheet;
            if ((long)tex.Width * tex.Height <= SheetPixCap)
            {
                try { sheet = new Color[tex.Width * tex.Height]; tex.GetData(sheet); }
                catch { sheet = null; }
            }
            _sheetPixCache[tex] = sheet; // null = over cap or failed → per-region fallback
            return sheet;
        }

        /// <summary>Read back every tilesheet a location uses, once, on entry — so the first-touch
        /// GPU readbacks all land in the (already synchronous, warp-fade-hidden) location change
        /// instead of hitching mid-walk when you scroll into a region using a fresh sheet.</summary>
        private void PrewarmSheetPixels(GameLocation loc)
        {
            if (ReferenceEquals(loc, _prewarmedLoc))
                return;
            _prewarmedLoc = loc;
            var map = loc.map;
            if (map == null)
                return;
            foreach (var ts in map.TileSheets)
            {
                if (!_sheetTexCache.TryGetValue(ts.ImageSource, out Texture2D? tex))
                {
                    try { tex = Game1.content.Load<Texture2D>(ts.ImageSource); }
                    catch { tex = null; }
                    _sheetTexCache[ts.ImageSource] = tex;
                }
                if (tex != null)
                    EnsureSheetPixels(tex);
            }
        }

        /// <summary>Fill <see cref="_artBuf"/> with a tile's 16×16 pixels. Reads from the cached
        /// whole-sheet pixel array (no GPU work) when available, falling back to a per-region
        /// <c>GetData</c> for over-cap sheets. Main-thread only (GPU readback on first sheet touch).</summary>
        private void ReadTileArt(Texture2D tex, Rectangle src)
        {
            _artBuf ??= new Color[256];
            Color[]? sheet = EnsureSheetPixels(tex);
            if (sheet != null)
            {
                int tw = tex.Width;
                for (int row = 0; row < 16; row++)
                {
                    int soff = (src.Y + row) * tw + src.X;
                    if (soff < 0 || soff + 16 > sheet.Length) { Array.Clear(_artBuf, row * 16, 16); continue; }
                    Array.Copy(sheet, soff, _artBuf, row * 16, 16);
                }
            }
            else
            {
                try { tex.GetData(0, src, _artBuf, 0, 256); } catch { Array.Clear(_artBuf, 0, 256); }
            }
        }

        private bool[] ClassifyBits(Texture2D tex, Rectangle src, bool foam = false)
        {
            var key = (tex, src, foam);
            if (_waterBitsCache.TryGetValue(key, out bool[]? bits))
                return bits;
            bits = new bool[256];
            _artBuf ??= new Color[256];
            try
            {
                ReadTileArt(tex, src);
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
                ReadTileArt(tex, src);
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

        /// <summary>A CAST SHADOW, not a structure: near-black and translucent. Bridges, cliffs and
        /// trees drop these onto the water from the Buildings/Front/AlwaysFront layers, and carving
        /// them punched the shadow's exact silhouette out of the effect channel — but shaded water
        /// is still water and has to keep rippling. Measured over the whole map dump: AlwaysFront
        /// holds 192k distinct tiles of which only 11k are fully opaque and ~99k are exactly this
        /// dark translucent wash, so the rule has to be conservative. Near-black only (art is
        /// premultiplied, so a black wash stays black at any alpha) and grey, so no coloured art
        /// can fall through it.</summary>
        private static bool ShadowWash(Color c)
        {
            if (c.A >= 250)
                return false;                       // fully opaque art is never a wash
            // Brightness alone. A saturation term was tried and only cost recall: measured over
            // the 15 shadow sheets in the map dump it rejected 28% of two of them (SVE's building
            // shadow, IridiumQuarry) for being faintly blue, and at max(rgb) <= 40 a pixel is
            // indistinguishable from black whatever its hue, so hue cannot separate art from wash.
            return Math.Max(c.R, Math.Max(c.G, c.B)) <= 40;
        }

        /// <summary>16×16 opacity bits + opaque-pixel count of one tile art, cached — used to
        /// carve piers/bridges/pads out of the water mask (count decides march-blocking).
        /// A tile whose opaque art is MOSTLY painted water (a waterfall, an animated water
        /// edge) is no structure at all — skipped entirely, or it carved whole water tiles
        /// into bright untouched patches. Below that bar, plain opacity rules: plank art
        /// keeps its dark blue-ish shadow pixels, so piers/bridges still block the march.</summary>
        /// <summary>Textures that came from a tilesheet whose name says "shadow".</summary>
        private readonly HashSet<Texture2D> _shadowSheets = new();
        private static readonly bool[] _noBits = new bool[256];

        private (bool[] bits, int count) SolidBits(Texture2D tex, Rectangle src)
        {
            if (_shadowSheets.Contains(tex))
                return (_noBits, 0);        // a whole sheet of cast shadows carves nothing
            var key = (tex, src);
            if (_solidBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0, w = 0;
            _artBuf ??= new Color[256];
            try
            {
                ReadTileArt(tex, src);
                for (int p = 0; p < 256; p++)
                {
                    if (bits[p] = _artBuf[p].A >= 128 && !ShadowWash(_artBuf[p]))
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

            // Bulk-read this location's tilesheets on entry, so every first-touch GPU readback
            // lands here (during the fade-covered location change) rather than hitching mid-walk.
            PrewarmSheetPixels(loc);

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            // The window is PADDED past the viewport: 2 tiles left/right, 4 above. A
            // column's waterline anchor (Pass D run-top) must stay WORLD-anchored while
            // its shoreline scrolls just past the screen edge — anchored at the mask's
            // own first row instead, the whole reflection re-based and vanished in ONE
            // step as the player walked away, rather than fading out.
            int startTileX = (int)Math.Floor(vx / 64f) - PadSide;
            int startTileY = (int)Math.Floor(vy / 64f) - PadTop;
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed
            // out — parts of the screen simply had no water mask (no ripple/reflection).
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + PadSide * 2 + 2);
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + PadTop + PadBottom);

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
                && _lastWaterLabelVer == CurrentLabelVersion()
                && _waterMask.Width == tilesW * 16 && Game1.ticks - _lastWaterTick < 600)
            {
                _waterMaskSize = new Vector2(tilesW, tilesH);
                return _waterAny;
            }

            long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var njob = GatherWaterMask(loc, startTileX, startTileY, tilesW, tilesH);
            double gatherMs = (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gatherMs > 8)
                _monitor.Log($"[diag] water gather={gatherMs:0.0}ms ({(loc == _lastWaterLoc ? "scroll" : "loc change")})", LogLevel.Debug);

            // On a LOCATION change the old mask is another map's content — turn the stage
            // off until the new compose lands (1-2 frames, hidden inside the warp fade).
            // A same-map scroll/zoom keeps rendering the old mask: its content is
            // world-anchored, so the old origin+size still map correctly.
            if (loc != _lastWaterLoc || _waterMask == null)
                _waterAny = false;

            njob.Task = System.Threading.Tasks.Task.Run(() =>
            {
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { ComposeWaterMask(njob); }
                catch { njob.Failed = true; }
                finally
                {
                    njob.ComposeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    njob.Done = true;
                }
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

                // Tree/bush canopies overhanging a pond are SPRITES (terrain features), not
                // map art — Pass C can't carve them, so leaves at the water's edge rippled.
                // Stamp them with the same geometry the shadow baker uses. Walk only the on-screen
                // tile range (+ a canopy margin) and look each tile up, instead of enumerating EVERY
                // terrain feature every frame and culling — the old full walk was O(all crops/trees)
                // per frame on a mature farm.
                var vp = Game1.viewport;
                var tfDict = loc.terrainFeatures;
                int ctx0 = (int)Math.Floor((vp.X - 256) / 64f), ctx1 = (int)Math.Floor((vp.X + vp.Width + 256) / 64f);
                int cty0 = (int)Math.Floor((vp.Y - 512) / 64f), cty1 = (int)Math.Floor((vp.Y + vp.Height + 768) / 64f);
                for (int cvY = cty0; cvY <= cty1; cvY++)
                for (int cvX = ctx0; cvX <= ctx1; cvX++)
                {
                    Vector2 tile = new(cvX, cvY);
                    if (!tfDict.TryGetValue(tile, out var tf))
                        continue;
                    switch (tf)
                    {
                        // Grown tree: canopy (0,0,48,96) at tile*64+(32,64), origin (24,96) — Tree.draw's math.
                        case StardewValley.TerrainFeatures.Tree tree when tree.growthStage.Value >= 5 && !tree.stump.Value && tree.texture?.Value != null:
                            sb.Draw(tree.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                StardewValley.TerrainFeatures.Tree.treeTopSourceRect, Color.White, 0f, new Vector2(24f, 96f), 4f,
                                tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                            break;
                        // Mature fruit tree: 48x64 seasonal foliage at tile*64+(32,64), origin (24,80).
                        case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4 && !ft.stump.Value && ft.texture != null:
                            int season = Game1.GetSeasonIndexForLocation(ft.Location);
                            var fsrc = new Rectangle((12 + season * 3) * 16, ft.GetSpriteRowNumber() * 5 * 16, 48, 64);
                            sb.Draw(ft.texture,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                fsrc, Color.White, 0f, new Vector2(24f, 80f), 4f,
                                ft.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                            break;
                        // Bush: bottom-centre = (tile.X*64 + (eff+1)*32, (tile.Y+1)*64) — the shadow baker's anchor.
                        case StardewValley.TerrainFeatures.Bush bush when !bush.sourceRect.Value.IsEmpty:
                            var bsrc = bush.sourceRect.Value;
                            int eff = bush.size.Value switch { 3 => 0, 4 => 1, _ => bush.size.Value };
                            sb.Draw(StardewValley.TerrainFeatures.Bush.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + (eff + 1) * 32f, (tile.Y + 1) * 64f)),
                                bsrc, Color.White, 0f, new Vector2(bsrc.Width / 2f, bsrc.Height), 4f,
                                bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                            break;
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
