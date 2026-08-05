using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

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
        /// <summary>Resolve the 16�16 source art of a map tile (first frame for animated tiles).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D texture, out Rectangle src)
            => TryTileArt(layer, tx, ty, out texture, out src, out _);

        /// <summary>As above, also reporting whether the tile is ANIMATED � animation is a strong
        /// "this is water/flowing art" signal (fountains, waterfalls, the beach surf line).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D texture, out Rectangle src, out bool animated)
        {
            texture = null!;
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
            if (!_tilesheetTextureCache.TryGetValue(t.TileSheet.ImageSource, out Texture2D? sheet))
            {
                try { sheet = Game1.content.Load<Texture2D>(t.TileSheet.ImageSource); }
                catch { sheet = null; }
                _tilesheetTextureCache[t.TileSheet.ImageSource] = sheet;
                // Shadow art ships in its OWN tilesheets, and the name says so. Every shadow sheet
                // in the map dump is called *Shadow(s) / *ShadowTilesheet / *CanopyShadow (15 of
                // them, vanilla and modded), so the sheet identity is a second, colour-independent
                // way to know a tile is a cast shadow and must never carve the water it falls on.
                if (sheet != null
                    && t.TileSheet.ImageSource.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                    _shadowTilesheets.Add(sheet);
            }
            if (sheet == null)
                return false;
            var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
            if (ib.Width != 16 || ib.Height != 16)
                return false;
            texture = sheet;
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
            // Teal / shallow edge � measured against the real tilesheets (2026-07-21, re-measured
            // 2026-07-22): the old loose gate (B > R+6) classified plain GRASS greens and rippled
            // meadows; at G-25 the DARK grass/fern strip tiles (summer avg (23,97,72), spring fern
            // clumps � full 245-256px tiles) still passed at G-B exactly 23-25 and waved on land.
            // Real teal water pixels sit at G-B = 19 on every sheet (beach tide pools, pond edges);
            // the 20-22 band is EMPTY, so G-20 splits them cleanly.
            if (c.G > c.R + 10 && c.B > c.R + 12 && c.B >= c.G - 20) return true;
            return false;
        }

        /// <summary>16�16 painted-water classification of one tile art, cached per (texture, rect,
        /// foam). With <paramref name="foam"/> (animated tiles that touch core water � the surf
        /// line), bright unsaturated wash pixels count as water too: white wave foam fails every
        /// hue gate, which left dead un-effected bands along the tide line.</summary>
        /// <summary>Whole-sheet pixel array for a tilesheet, read back once (main thread) and
        /// cached. Returns null for over-cap sheets (caller falls back to per-region GetData) or
        /// on failure. The single readback replaces one-per-tile readbacks (each a GPU stall).</summary>
        private Color[]? EnsureSheetPixels(Texture2D texture)
        {
            if (_tilesheetPixelCache.TryGetValue(texture, out Color[]? sheet))
                return sheet;
            long px = (long)texture.Width * texture.Height;
            if (px <= SheetPixCap)
            {
                try
                {
                    sheet = new Color[px];
                    // Read in STRIPS rather than one whole-surface GetData. The cap this replaces
                    // existed to bound the driver's staging cost for a huge sheet, and its fallback
                    // was a readback PER TILE � thousands of times more expensive than the
                    // allocation it avoided (a 240x156 map on an 8.64 Mpx sheet spent 43 s in one
                    // gather). Strips keep the staging bounded while still costing one readback per
                    // strip, so size no longer decides between "fast" and "unusable".
                    for (int y0 = 0; y0 < texture.Height; y0 += SheetStripRows)
                    {
                        int rows = Math.Min(SheetStripRows, texture.Height - y0);
                        texture.GetData(0, new Rectangle(0, y0, texture.Width, rows),
                            sheet, y0 * texture.Width, rows * texture.Width);
                    }
                }
                catch { sheet = null; }
            }
            if (sheet == null)
                _monitor.Log($"[water] tilesheet {texture.Width}x{texture.Height} not cached � tile art falls back to per-tile reads", LogLevel.Warn);
            _tilesheetPixelCache[texture] = sheet; // null = absurd size or failed ? per-tile fallback (deduped)
            return sheet;
        }

        /// <summary>Read back every tilesheet a location uses, once, on entry � so the first-touch
        /// GPU readbacks all land in the (already synchronous, warp-fade-hidden) location change
        /// instead of hitching mid-walk when you scroll into a region using a fresh sheet.</summary>
        private void PrewarmSheetPixels(GameLocation location)
        {
            if (ReferenceEquals(location, _prewarmedLocation))
                return;
            _prewarmedLocation = location;
            var map = location.map;
            if (map == null)
                return;
            foreach (var ts in map.TileSheets)
            {
                if (!_tilesheetTextureCache.TryGetValue(ts.ImageSource, out Texture2D? texture))
                {
                    try { texture = Game1.content.Load<Texture2D>(ts.ImageSource); }
                    catch { texture = null; }
                    _tilesheetTextureCache[ts.ImageSource] = texture;
                }
                if (texture != null)
                    EnsureSheetPixels(texture);
            }
        }

        /// <summary>Fill <see cref="_tileArtPixels"/> with a tile's 16�16 pixels. Reads from the cached
        /// whole-sheet pixel array (no GPU work) when available, falling back to a per-region
        /// <c>GetData</c> for over-cap sheets. Main-thread only (GPU readback on first sheet touch).</summary>
        private void ReadTileArt(Texture2D texture, Rectangle src)
        {
            _tileArtPixels ??= new Color[256];
            Color[]? sheet = EnsureSheetPixels(texture);
            if (sheet != null)
            {
                int tw = texture.Width;
                for (int row = 0; row < 16; row++)
                {
                    int soff = (src.Y + row) * tw + src.X;
                    if (soff < 0 || soff + 16 > sheet.Length) { Array.Clear(_tileArtPixels, row * 16, 16); continue; }
                    Array.Copy(sheet, soff, _tileArtPixels, row * 16, 16);
                }
            }
            else if (_tileArtCache.TryGetValue((texture, src), out Color[]? tile))
            {
                Array.Copy(tile, _tileArtPixels, 256);
            }
            else
            {
                // A refused sheet still gets read at most ONCE per distinct tile: the gather walks
                // every tile of the map, so an undeduped readback here is paid per painted cell,
                // not per piece of art. Bounded so a pathological map cannot grow this without end.
                try { texture.GetData(0, src, _tileArtPixels, 0, 256); } catch { Array.Clear(_tileArtPixels, 0, 256); }
                if (_tileArtCache.Count < 16_384)
                {
                    var copy = new Color[256];
                    Array.Copy(_tileArtPixels, copy, 256);
                    _tileArtCache[(texture, src)] = copy;
                }
            }
        }

        /// <summary>A CAST SHADOW, not a structure: near-black and translucent. Bridges, cliffs and
        /// trees drop these onto the water from the Buildings/Front/AlwaysFront layers, and carving
        /// them punched the shadow's exact silhouette out of the effect channel � but shaded water
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

        /// <summary>16�16 opacity bits + opaque-pixel count of one tile art, cached � used to
        /// carve piers/bridges/pads out of the water mask (count decides march-blocking).
        /// The old "=60% of the opaque art is water-COLOURED ? wave overlay, don't carve"
        /// bail-out is GONE (V4 D1: no colour guessing): it existed to keep unlabelled water
        /// overlays from carving themselves, but it also waved through every blue-ish or
        /// murky-toned STRUCTURE � the SVE crystal boulder in the Mountain lake and the
        /// FarmCave plank both rippled because their art happened to pass a colour test.
        /// Genuine water drawn on overlay layers is protected by its LABEL now (a label's
        /// liquid pixels are removed from the carve); art nobody labelled carves by opacity,
        /// so an unlabelled mod pond at worst goes calm instead of rippling its own rocks.</summary>
        /// <summary>Textures that came from a tilesheet whose name says "shadow".</summary>
        private readonly HashSet<Texture2D> _shadowTilesheets = new();
        private static readonly bool[] _emptyTileBits = new bool[256];

        private (bool[] bits, int count) SolidBits(Texture2D texture, Rectangle src)
        {
            var r = OpaqueBits(texture, src);
            return (r.bits, r.count);
        }

        /// <summary>The same opacity bits WITHOUT the "mostly water-coloured ? not a structure"
        /// bail-out. Only for art a label has explicitly called ground: that guess is right for a
        /// wave overlay and wrong for a snow-covered bank ledge, whose pale blue reads as water
        /// (winter_outdoorsTileSheet#211 is 189 of 220 opaque pixels water-coloured, so it carved
        /// nothing at all and the mirror ran straight over the bank).</summary>
        private (bool[] bits, int count, int water) OpaqueBits(Texture2D texture, Rectangle src)
        {
            if (_shadowTilesheets.Contains(texture))
                return (_emptyTileBits, 0, 0);     // a whole sheet of cast shadows carves nothing
            var key = (texture, src);
            if (_tileSolidBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0, w = 0;
            _tileArtPixels ??= new Color[256];
            try
            {
                ReadTileArt(texture, src);
                for (int p = 0; p < 256; p++)
                {
                    if (bits[p] = _tileArtPixels[p].A >= 128 && !ShadowWash(_tileArtPixels[p]))
                    {
                        n++;
                        if (WaterColor(_tileArtPixels[p])) w++;
                    }
                }
            }
            catch { /* leave all-false */ }
            entry = (bits, n, w);
            _tileSolidBitsCache[key] = entry;
            return entry;
        }

        /// <summary>Pixels where this art draws ANYTHING visible (alpha >= 32), shadow washes
        /// INCLUDED. Only for decisions a LABEL has already made, where no heuristic is left to
        /// protect: a waterfall's spray is visibly water far below the 128-opaque bar, and a
        /// bridge's painted shadow is part of the bridge when the label calls it ground. Fully
        /// transparent pixels are still the layer below showing through, so they stay out.</summary>
        private bool[] AnyAlphaBits(Texture2D texture, Rectangle src)
        {
            if (_shadowTilesheets.Contains(texture))
                return _emptyTileBits;
            var key = (texture, src);
            if (_tileAnyAlphaBitsCache.TryGetValue(key, out var bits))
                return bits;
            bits = new bool[256];
            _tileArtPixels ??= new Color[256];
            try
            {
                ReadTileArt(texture, src);
                for (int p = 0; p < 256; p++)
                    bits[p] = _tileArtPixels[p].A >= 32;
            }
            catch { /* leave all-false */ }
            _tileAnyAlphaBitsCache[key] = bits;
            return bits;
        }

        /// <summary>8-way one-tile dilation of a tile flag grid (src ? dst).</summary>
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
        /// uploads finished results � the 8-23 ms monolithic rebuild on every tile crossing
        /// was THE walking-near-water stutter. While a job is in flight the old mask keeps
        /// rendering (world-anchored content + padded window = no visible edge).
        /// </summary>
        private GameLocation? _locationWaterLocation;
        private bool _locationHasWater;

        /// <summary>
        /// Does THIS LOCATION have water anywhere, as opposed to "is water inside the mask window
        /// right now".
        /// <para>
        /// The stage used to switch on the window answer, which changes every few tiles as the
        /// player walks. Every switch is a chance for the presence fade to be wrong, and it was
        /// wrong twice: the fade never reached the shader's pixels, and the blend that replaced it
        /// added the frame to itself. Both read as the picture flashing near water.
        /// </para>
        /// A location answer changes only on a warp, which the game already covers with its own
        /// fade to black. The pass then runs for the whole visit; on a frame with no water on
        /// screen every pixel takes the shader's first early-out after two texture reads, so the
        /// cost of keeping it alive is a fraction of what the switching was worth.
        /// </summary>
        private bool LocationHasWater(GameLocation location)
        {
            if (!ReferenceEquals(location, _locationWaterLocation))
            {
                _locationWaterLocation = location;
                _locationHasWater = false;
                if (location.waterTiles?.waterTiles is { } wt)
                {
                    int ww = wt.GetLength(0), wh = wt.GetLength(1);
                    for (int y = 0; y < wh && !_locationHasWater; y++)
                        for (int x = 0; x < ww; x++)
                            if (wt[x, y].isWater) { _locationHasWater = true; break; }
                }
            }
            // Labelled or draw-hooked water need not appear in the game's own grid, so once a
            // compose has found any here, the location keeps the stage for the rest of the visit.
            if (_hasWaterInMask)
                _locationHasWater = true;
            return _locationHasWater;
        }

        private bool BuildWaterMask(int w, int h)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return false;

            // Bulk-read this location's tilesheets on entry, so every first-touch GPU readback
            // lands here (during the fade-covered location change) rather than hitching mid-walk.
            PrewarmSheetPixels(location);

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            // The window is PADDED past the viewport: 2 tiles left/right, 4 above. A
            // column's waterline anchor (Pass D run-top) must stay WORLD-anchored while
            // its shoreline scrolls just past the screen edge � anchored at the mask's
            // own first row instead, the whole reflection re-based and vanished in ONE
            // step as the player walked away, rather than fading out.
            int startTileX = (int)Math.Floor(vx / 64f) - 2;
            int startTileY = (int)Math.Floor(vy / 64f) - 4;
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed
            // out � parts of the screen simply had no water mask (no ripple/reflection).
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + 6);
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + 6);

            // Camera-follow params are valid for WHATEVER mask is currently bound (old or
            // new) � the mask content is tile-anchored; sub-tile scroll lives here.
            _waterMaskTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
            _waterMaskWorldTileOffset = new Vector2(vx / 64f, vy / 64f);

            // Poll the in-flight compose FIRST: apply it if it finished and still matches
            // the wanted window; keep showing the old mask while it runs; discard it if
            // the camera crossed again mid-compose (fall through to a fresh gather).
            if (_pendingWaterMaskJob is { } job)
            {
                if (!job.Done)
                    return _hasWaterInMask;
                _pendingWaterMaskJob = null;
                if (job.AnchorOnly)
                {
                    // P3a: publish the location-wide waterline anchor and shrink the
                    // map-sized scratch back down. The window mask was fresh when this
                    // was kicked; fall through so a camera move still rebuilds it now.
                    ConsumeAnchorJob(job);
                }
                else if (job.Failed)
                {
                    if (!_waterMaskJobFailureLogged) { _monitor.Log("Water mask compose failed once; rebuilding synchronously.", LogLevel.Warn); _waterMaskJobFailureLogged = true; }
                }
                else if (job.Location == location && job.StartTileX == startTileX && job.StartTileY == startTileY
                    && job.TileWidth == tilesW && job.TileHeight == tilesH)
                {
                    ApplyWaterMask(job);
                    return _hasWaterInMask;
                }
            }

            // The mask content is TILE-ANCHORED (sub-tile camera scroll is handled by the
            // WorldTileOffset shader param), so it only changes when the view crosses a tile
            // boundary � rebuilding the pixel grid every frame was a walking-stutter tax.
            // The 10 s safety refresh only exists to pick up rare map mutations (a bridge
            // built, ice melting); everything routine invalidates via location/origin keys,
            // and world EVENTS (a fish pond placed, a map re-patched) bump MaskEpoch so the
            // change lands on the next frame instead of up to 10 s late.
            if (_waterMask != null && location == _lastWaterLocation && startTileX == _lastWaterTileX && startTileY == _lastWaterTileY
                && _lastWaterHookVersion == WaterDrawHook.Version
                && _lastWaterLabelVersion == CurrentLabelVersion()
                && _lastWaterEpoch == MaskEpoch
                && _waterMask.Width == tilesW * 16 && Game1.ticks - _lastWaterBuildTick < 600)
            {
                _waterMaskPixelSize = new Vector2(tilesW, tilesH);
                // The window is fresh and no job is in flight � the cheap moment to build
                // this location's full-map waterline anchor if it doesn't have one yet.
                if (_hasWaterInMask)
                    MaybeKickAnchorJob(location);
                return _hasWaterInMask;
            }

            long gatherStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var newWaterMaskJob = GatherWaterMask(location, startTileX, startTileY, tilesW, tilesH);
            double gatherDurationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - gatherStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gatherDurationMilliseconds > 8)
                _monitor.Log($"[diag] water gather={gatherDurationMilliseconds:0.0}ms ({(location == _lastWaterLocation ? "scroll" : "location change")})", LogLevel.Debug);

            // On a LOCATION change the old mask is another map's content � turn the stage
            // off until the new compose lands (1-2 frames, hidden inside the warp fade).
            // A same-map scroll/zoom keeps rendering the old mask: its content is
            // world-anchored, so the old origin+size still map correctly.
            if (location != _lastWaterLocation || _waterMask == null)
                _hasWaterInMask = false;

            // The gather already knows, on this thread, whether the window it just read contains
            // water � but `_hasWaterInMask` was only ever updated when a COMPOSE landed, and a compose
            // is discarded whenever the view moved while it ran. Walk continuously and no job ever
            // matches on completion, so the flag keeps whatever it held the last time the player
            // stood still: the same tile measured wAny=1 on one pass and wAny=0 on another, which
            // is what took the stage in and out and read as the picture stepping brighter/darker.
            // Turn ON from the fresh gather immediately; leave turning OFF to a completed compose,
            // which also knows about label-only water. Lingering costs nothing (there is no water
            // on screen to affect), while dropping out early is exactly the visible fault.
            if (newWaterMaskJob.AnyWater)
                _hasWaterInMask = true;

            newWaterMaskJob.Task = System.Threading.Tasks.Task.Run(() =>
            {
                long composeStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                try { ComposeWaterMask(newWaterMaskJob); }
                catch { newWaterMaskJob.Failed = true; }
                finally
                {
                    newWaterMaskJob.ComposeDurationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - composeStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    newWaterMaskJob.Done = true;
                }
            });
            _pendingWaterMaskJob = newWaterMaskJob;
            return _hasWaterInMask;   // old mask renders this frame; the swap lands when compose does
        }

        // ---- per-frame sprite mask (things ON the water must not ripple) ----

        private RenderTarget2D? _spriteMaskRenderTarget;
        private SpriteBatch? _spriteMaskSpriteBatch;

        /// <summary>Solid exclusion box in WORLD px, centre-top anchored � bubbles, emotes:
        /// UI riding in the world layer that the water must never warp.</summary>
        private void StampUiBox(SpriteBatch spriteBatch, int cx, int top, int w, int h)
        {
            Vector2 tl = Game1.GlobalToLocal(Game1.viewport, new Vector2(cx - w / 2f, top));
            spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)tl.X, (int)tl.Y, w, h), Color.White);
        }

        // NPC.textAboveHead / textAboveHeadTimer went protected in 1.6 � the bubble mask below
        // needs to know a bubble is showing and how wide its text is, nothing more.
        private static readonly System.Reflection.FieldInfo? _npcTextField =
            HarmonyLib.AccessTools.Field(typeof(NPC), "textAboveHead");
        private static readonly System.Reflection.FieldInfo? _npcTextTimerField =
            HarmonyLib.AccessTools.Field(typeof(NPC), "textAboveHeadTimer");
        internal bool SpriteMaskReady;

        /// <summary>Is there any water within <paramref name="radiusTiles"/> of this tile in the
        /// current mask window? Both the sprite mask and the reflection RT stamp EVERY body, tree
        /// and placed object on screen, but only the ones whose pixels can actually meet water
        /// change anything: on a map with water in one corner that is a screenful of draw calls
        /// per frame spent on sprites nowhere near it. Unknown state answers yes, so a missing
        /// mask never silently drops an exclusion.</summary>
        private bool WaterWithinTiles(int tileX, int tileY, int radiusTiles)
        {
            if (_waterTileFlags == null || _waterMask == null)
                return true;
            int tilesW = _waterMask.Width / 16, tilesH = _waterMask.Height / 16;
            if (tilesW <= 0 || tilesH <= 0 || _waterTileFlags.Length < tilesW * tilesH)
                return true;
            int cx = tileX - _lastWaterTileX, cy = tileY - _lastWaterTileY;
            int x0 = Math.Max(0, cx - radiusTiles), x1 = Math.Min(tilesW - 1, cx + radiusTiles);
            int y0 = Math.Max(0, cy - radiusTiles), y1 = Math.Min(tilesH - 1, cy + radiusTiles);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * tilesW;
                for (int x = x0; x <= x1; x++)
                    if (_waterTileFlags[row + x])
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Bake every sprite that could be standing ON water � NPCs, farm animals
        /// (swimming ducks!), critters � into a screen-space mask, called from
        /// Display.RenderingWorld (the only spot where a render-target swap is safe).
        /// The water shader excludes these pixels from ripple/mirror so sprites never
        /// distort, while the water beside them keeps animating. Positions mirror the
        /// game's own draw math (bottom-centre at the collision box feet).
        /// </summary>
        public void BakeWaterSpriteMask()
        {
            SpriteMaskReady = false;
            GameLocation? location = Game1.currentLocation;
            if (location == null || !_hasWaterInMask)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_spriteMaskRenderTarget == null || _spriteMaskRenderTarget.Width != w || _spriteMaskRenderTarget.Height != h)
            {
                _spriteMaskRenderTarget?.Dispose();
                _spriteMaskRenderTarget = new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _spriteMaskSpriteBatch ??= new SpriteBatch(_device);

            try
            {
                _device.SetRenderTarget(_spriteMaskRenderTarget);
                _device.Clear(Color.Transparent);
                var spriteBatch = _spriteMaskSpriteBatch;
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

                // NPCs + monsters: bottom-centre at the collision-box feet, scale 4 �
                // the same anchor the game draws them at (small bob/jump offsets are
                // sub-pixel enough for an exclusion mask).
                foreach (NPC c in ShadowRenderer.CharactersIn(location))
                {
                    if (c?.Sprite?.Texture == null || c.IsInvisible)
                        continue;
                    // drawOffset is the shift the game applies at DRAW time and never writes back
                    // into Position or the collision box � a character on a seat, in the bus, or
                    // posed by an event is drawn somewhere its box does not admit to. Without it
                    // the exclusion landed off the body: the sprite kept rippling and a hole was
                    // punched into clean water beside it.
                    Rectangle bb = c.GetBoundingBox();
                    Vector2 off = c.drawOffset;
                    if (off != Vector2.Zero)
                        bb.Offset((int)off.X, (int)off.Y);
                    if (!WaterWithinTiles(bb.Center.X / 64, bb.Bottom / 64, 3))
                        continue;
                    StampSprite(spriteBatch, c.Sprite.Texture, c.Sprite.SourceRect, bb);
                    // A SPEECH BUBBLE is part of the world layer too (drawn above AlwaysFront),
                    // so a fisherman chatting over the river had his bubble rippled and tinted
                    // like the water behind it. Mask a generous box where vanilla draws it
                    // (~3 tiles above the feet, scroll background included); over-covering is
                    // harmless � the box only exists for the seconds the bubble does.
                    if ((_npcTextTimerField?.GetValue(c) as int? ?? 0) > 0
                        && _npcTextField?.GetValue(c) is string say && say.Length > 0)
                    {
                        int tw = (int)(StardewValley.BellsAndWhistles.SpriteText.getWidthOfString(say) * 1.1f) + 64;
                        var world = new Rectangle(bb.Center.X - tw / 2, bb.Top - 260, tw, 176);
                        Vector2 tl = Game1.GlobalToLocal(Game1.viewport, new Vector2(world.X, world.Y));
                        spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)tl.X, (int)tl.Y, world.Width, world.Height), Color.White);
                    }
                    // Emotes (the thought/exclamation balloon) live in the world layer too.
                    if (c.isEmoting)
                        StampUiBox(spriteBatch, bb.Center.X, bb.Top - 160, 80, 128);
                }
                // The player's own bubble/emote � their BODY is excluded via PlayerMask, but
                // the balloon floats above the mask's reach.
                var pw = Game1.player;
                if (pw != null && pw.isEmoting)
                {
                    Rectangle pbb = pw.GetBoundingBox();
                    StampUiBox(spriteBatch, pbb.Center.X, pbb.Top - 160, 80, 128);
                }
                // The CAST POWER METER is drawn by FishingRod.draw in the world layer, so it is
                // neither in the PlayerMask bake (FarmerRenderer only) nor stamped as a sprite -
                // charging a cast over water waved the meter and made max casts a guess. Cover
                // vanilla's spot (left of and above the farmer) generously; the box only exists
                // for the fraction of a second the meter does.
                if (pw?.CurrentTool is StardewValley.Tools.FishingRod castRod && castRod.isTimingCast)
                {
                    Rectangle pbb = pw.GetBoundingBox();
                    StampUiBox(spriteBatch, pbb.Center.X, pbb.Top - 280, 288, 240);
                }
                // The rod itself, the line and the floating bobber are all drawn by
                // FishingRod.draw in the world layer - outside the PlayerMask bake (body only)
                // and the sprite stamps - so a rod held out over the river waved with the
                // water beneath it. Let it stamp ITSELF, the same way crab pots do: whatever
                // the game draws for it lands in the exclusion pixel for pixel.
                if (pw?.UsingTool == true && pw.CurrentTool is StardewValley.Tools.FishingRod heldRod)
                {
                    try { heldRod.draw(spriteBatch); } catch { }
                    // FishingRod.draw covers only the line and the bobber; the ROD STICK is a
                    // tools-sheet sprite the game renders separately via Game1.drawTool - and
                    // that helper draws through Game1.spriteBatch, not the batch it is handed.
                    // Point Game1.spriteBatch at the mask batch for the one call so the stick
                    // lands in the mask, and restore it no matter what.
                    var gameBatch = Game1.spriteBatch;
                    try
                    {
                        Game1.spriteBatch = spriteBatch;
                        Game1.drawTool(pw);
                    }
                    catch { }
                    finally { Game1.spriteBatch = gameBatch; }
                }
                // Farm animals (ducks paddle straight into ponds).
                foreach (var a in location.animals.Values)
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    Rectangle abb = a.GetBoundingBox();
                    if (!WaterWithinTiles(abb.Center.X / 64, abb.Bottom / 64, 3))
                        continue;
                    StampSprite(spriteBatch, a.Sprite.Texture, a.Sprite.SourceRect, abb);
                }
                // Critters (seagulls, birds, frogs): base Critter.draw puts the 16�16
                // sprite's bottom edge at position.Y, centred on position.X.
                if (location.critters != null)
                {
                    foreach (var cr in location.critters)
                    {
                        if (cr?.sprite?.Texture == null)
                            continue;
                        if (!WaterWithinTiles((int)(cr.position.X / 64f), (int)(cr.position.Y / 64f), 3))
                            continue;
                        Vector2 tl = Game1.GlobalToLocal(Game1.viewport, cr.position + new Vector2(-32f, -64f));
                        spriteBatch.Draw(cr.sprite.Texture, tl, cr.sprite.SourceRect, Color.White,
                            0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                    }
                }

                // World OBJECTS standing on a water tile: beach forage in a tide pool, a crab pot,
                // anything dropped. They are drawn on top of the water, so the ripple was warping
                // them along with it (reported: a sea urchin in a tide pool rippling like liquid).
                // Objects are tile-keyed, so walk the visible tile range rather than the whole
                // dictionary, the same way the canopy pass below does.
                var vpO = Game1.viewport;
                int otx0 = (int)Math.Floor((vpO.X - 128) / 64f), otx1 = (int)Math.Floor((vpO.X + vpO.Width + 128) / 64f);
                int oty0 = (int)Math.Floor((vpO.Y - 128) / 64f), oty1 = (int)Math.Floor((vpO.Y + vpO.Height + 192) / 64f);
                for (int ovY = oty0; ovY <= oty1; ovY++)
                for (int ovX = otx0; ovX <= otx1; ovX++)
                {
                    if (!location.objects.TryGetValue(new Vector2(ovX, ovY), out var obj) || obj == null)
                        continue;
                    if (!WaterWithinTiles(ovX, ovY, 2))
                        continue;
                    // Let the OBJECT draw itself. Reconstructing the placement here (centre-bottom
                    // of the tile, nudged up a third) is right for an ordinary placed item and
                    // wrong for anything with its own draw: a CRAB POT sits a tile higher and bobs
                    // on the swell, so the hole landed beside the pot instead of on it — water
                    // notched next to it, and the flat unrippled patch read as a shadow that did
                    // not match. Only this stamp's ALPHA is read, so drawing it in its own colours
                    // costs nothing and it is the game's own geometry by construction.
                    try { obj.draw(spriteBatch, ovX, ovY, 1f); }
                    catch { /* a mod's draw threw — skip this object's exclusion */ }
                }

                // Tree/bush canopies overhanging a pond are SPRITES (terrain features), not
                // map art � Pass C can't carve them, so leaves at the water's edge rippled.
                // Stamp them with the same geometry the shadow baker uses. Walk only the on-screen
                // tile range (+ a canopy margin) and look each tile up, instead of enumerating EVERY
                // terrain feature every frame and culling � the old full walk was O(all crops/trees)
                // per frame on a mature farm.
                var viewport = Game1.viewport;
                var tfDict = location.terrainFeatures;
                int ctx0 = (int)Math.Floor((viewport.X - 256) / 64f), ctx1 = (int)Math.Floor((viewport.X + viewport.Width + 256) / 64f);
                int cty0 = (int)Math.Floor((viewport.Y - 512) / 64f), cty1 = (int)Math.Floor((viewport.Y + viewport.Height + 768) / 64f);
                for (int cvY = cty0; cvY <= cty1; cvY++)
                for (int cvX = ctx0; cvX <= ctx1; cvX++)
                {
                    Vector2 tile = new(cvX, cvY);
                    if (!tfDict.TryGetValue(tile, out var tf))
                        continue;
                    // A canopy only matters where it overhangs water. A grown tree's crown is
                    // 96 source rows — SIX tiles above its trunk — so the search is centred well
                    // above the base and reaches far enough to cover the whole crown plus slack.
                    // Anything tighter would drop the stamp for a tree whose top overhangs a pond
                    // several tiles north of it, and the leaves would ripple.
                    if (!WaterWithinTiles(cvX, cvY - 3, 6))
                        continue;
                    switch (tf)
                    {
                        // Grown tree: canopy (0,0,48,96) at tile*64+(32,64), origin (24,96) � Tree.draw's math.
                        case StardewValley.TerrainFeatures.Tree tree when tree.growthStage.Value >= 5 && !tree.stump.Value && tree.texture?.Value != null:
                            spriteBatch.Draw(tree.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                StardewValley.TerrainFeatures.Tree.treeTopSourceRect, Color.White, 0f, new Vector2(24f, 96f), 4f,
                                tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                            break;
                        // Mature fruit tree: 48x64 seasonal foliage at tile*64+(32,64), origin (24,80).
                        case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4 && !ft.stump.Value && ft.texture != null:
                            int season = Game1.GetSeasonIndexForLocation(ft.Location);
                            var fsrc = new Rectangle((12 + season * 3) * 16, ft.GetSpriteRowNumber() * 5 * 16, 48, 64);
                            spriteBatch.Draw(ft.texture,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                fsrc, Color.White, 0f, new Vector2(24f, 80f), 4f,
                                ft.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                            break;
                        // Bush: bottom-centre = (tile.X*64 + (eff+1)*32, (tile.Y+1)*64) � the shadow baker's anchor.
                        case StardewValley.TerrainFeatures.Bush bush when !bush.sourceRect.Value.IsEmpty:
                            var bsrc = bush.sourceRect.Value;
                            int eff = bush.size.Value switch { 3 => 0, 4 => 1, _ => bush.size.Value };
                            spriteBatch.Draw(StardewValley.TerrainFeatures.Bush.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + (eff + 1) * 32f, (tile.Y + 1) * 64f)),
                                bsrc, Color.White, 0f, new Vector2(bsrc.Width / 2f, bsrc.Height), 4f,
                                bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                            break;
                    }
                }

                spriteBatch.End();
                SpriteMaskReady = true;
            }
            finally
            {
                _device.SetRenderTargets(prev);
            }
        }

        private static void StampSprite(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Rectangle bb)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(bb.Center.X, bb.Bottom));
            spriteBatch.Draw(texture, feet, src, Color.White, 0f,
                new Vector2(src.Width / 2f, src.Height), 4f, SpriteEffects.None, 0f);
        }

        // ---- helpers -------------------------------------------------------

        // Wrapped like the cloud shadow's Time: unbounded seconds eventually push the
        // shader noise hashes past float/sin precision, which reads as hard axis-aligned
        // seams. 100-minute period, multiple of 60 so whole seconds stay whole.
        private static float Time() => (Determinism.Ticks % 360000) / 60f;

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
            return $"saved {p1} (origin tile {_lastWaterTileX},{_lastWaterTileY}, player tile {Game1.player?.TilePoint})";
        }
    }
}
