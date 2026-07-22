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
        /// Build a per-tile water mask for the visible area from the current location,
        /// aligned to the viewport. Returns false (and skips the water stage) when the
        /// location has no water on screen, so we never distort a waterless frame.
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
            int count = tilesW * tilesH;

            // The mask content is TILE-ANCHORED (sub-tile camera scroll is handled by the
            // WorldTileOffset shader param), so it only changes when the view crosses a tile
            // boundary — rebuilding the pixel grid every frame was a walking-stutter tax.
            // The 10 s safety refresh only exists to pick up rare map mutations (a bridge
            // built, ice melting); everything routine invalidates via location/origin keys.
            if (_waterMask != null && loc == _lastWaterLoc && startTileX == _lastWaterTx && startTileY == _lastWaterTy
                && _lastWaterHookVer == WaterDrawHook.Version
                && _waterMask.Width == tilesW * 16 && Game1.ticks - _lastWaterTick < 600)
            {
                _waterTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
                _waterWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
                _waterMaskSize = new Vector2(tilesW, tilesH);
                return _waterAny;
            }
            _lastWaterLoc = loc;
            _lastWaterTx = startTileX;
            _lastWaterTy = startTileY;
            _lastWaterTick = Game1.ticks;
            _lastWaterHookVer = WaterDrawHook.Version;

            // Height Framework (when present) classifies the actual water SURFACE: ponds and
            // beach tide pools count as water (they reflect too), while pier/bridge DECKS over
            // water do not (no reflection painted onto planks). Fall back to isWaterTile.
            var hf = ShadowRenderer.Height;
            if (_waterBoolBuf == null || _waterBoolBuf.Length < count)
                _waterBoolBuf = new bool[count];
            bool any = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool water;
                    try { water = hf != null ? hf.IsWaterSurface(loc, tx, ty) : loc.isWaterTile(tx, ty); }
                    catch { hf = null; water = loc.isWaterTile(tx, ty); }
                    // Walkable shallow pools (island dig site tide pools) aren't Water tiles,
                    // but they refill the watering can → "WaterSource" marks them as real water.
                    if (!water && loc.doesTileHaveProperty(tx, ty, "WaterSource", "Back") != null)
                        water = true;
                    // Draw-call truth: the game DREW water here but the tile data doesn't know it
                    // (a location/mod with custom drawWater logic). Only when isWaterTile is false —
                    // isWaterTile-true tiles keep their pipeline above, so HF's deck-over-water veto
                    // is never overridden by the hook.
                    if (!water && !loc.isWaterTile(tx, ty) && WaterDrawHook.WasDrawn(loc, tx, ty))
                        water = true;
                    if (water) any = true;
                    _waterBoolBuf[j * tilesW + i] = water;
                }
            }

            // CORE mask first (undilated): the reflection's shoreline search must see bridges,
            // piers and banks as land — the dilated mask swallowed any land strip ≤4 tiles
            // wide (a bridge between two water bodies), which killed their reflections.
            if (_waterMaskCoreBuf == null || _waterMaskCoreBuf.Length < count)
                _waterMaskCoreBuf = new Color[count];
            for (int idx = 0; idx < count; idx++)
                _waterMaskCoreBuf[idx] = _waterBoolBuf[idx] ? Color.White : Color.Transparent;

            // NOTE: no early-out on "no real water" here — walk-through puddles (art-classified
            // below) count as water too; a dig site with the ocean scrolled off-screen used to
            // shut the whole stage off and every pool went dead at once.

            // CANDIDATE ring: dilate three tiles (shore art + beach surf zone). These tiles are
            // NOT marked water — they only nominate their ART for per-pixel classification below,
            // so the final mask never spills a box past the painted waterline.
            if (_waterBool2Buf == null || _waterBool2Buf.Length < count)
                _waterBool2Buf = new bool[count];
            Dilate8(_waterBoolBuf, _waterBool2Buf, tilesW, tilesH);
            Dilate8(_waterBool2Buf, _waterBoolBuf, tilesW, tilesH);
            Dilate8(_waterBoolBuf, _waterBool2Buf, tilesW, tilesH);

            // ---- PIXEL-accurate mask (16 texels per tile = the art's own resolution) ----
            // True water tiles fill solid; candidate shore tiles contribute only the pixels of
            // their Back-layer art that are painted as water (classified ONCE per tile art and
            // cached); opaque Buildings/Front art (pier posts, bridges, lily pads, canopies)
            // carves holes so things standing in the water block the effect.
            const int Sub = 16;
            int pw = tilesW * Sub, ph = tilesH * Sub;
            int pcount = pw * ph;
            if (_waterPixBuf == null || _waterPixBuf.Length < pcount)
                _waterPixBuf = new Color[pcount];
            var back = loc.map?.GetLayer("Back");
            var bld = loc.map?.GetLayer("Buildings");
            var front = loc.map?.GetLayer("Front");
            if (_waterPixBits == null || _waterPixBits.Length < pcount)
                _waterPixBits = new bool[pcount];
            // Pass A — raw water pixels (true tiles solid, shore tiles by art classification).
            if (_puddleTileBuf == null || _puddleTileBuf.Length < count)
                _puddleTileBuf = new byte[count];
            if (_animOnlyTileBuf == null || _animOnlyTileBuf.Length < count)
                _animOnlyTileBuf = new bool[count];
            bool anyAnim = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterMaskCoreBuf![idx].R > 0;
                    int tx = startTileX + i, ty = startTileY + j;
                    bool[]? bits = null;
                    byte puddle = 0;
                    bool animOnly = false;
                    if (!isWater && TryTileArt(back, tx, ty, out var btex, out var bsrc, out bool bAnim))
                    {
                        if (_waterBool2Buf[idx])
                        {
                            // Surf line: animated tiles CARDINALLY touching core water get the
                            // foam rule too — white wave wash failed every hue gate and left
                            // dead un-effected bands along the tide.
                            bool coreAdj = (i > 0 && _waterMaskCoreBuf[idx - 1].R > 0)
                                        || (i < tilesW - 1 && _waterMaskCoreBuf[idx + 1].R > 0)
                                        || (j > 0 && _waterMaskCoreBuf[idx - tilesW].R > 0)
                                        || (j < tilesH - 1 && _waterMaskCoreBuf[idx + tilesW].R > 0);
                            bits = ClassifyBits(btex, bsrc, foam: bAnim && coreAdj);
                        }
                        else if (bAnim)
                        {
                            // ANIMATED water art anywhere on the map — fountains, waterfalls,
                            // decorative pools that aren't isWaterTile and sit outside the
                            // shore ring. Mostly-water animated art joins the EFFECT channel
                            // only (ripple/sparkle/tint), never the march: a reflection must
                            // not anchor on a fountain rim or a waterfall face.
                            var ab = ClassifyBits(btex, bsrc);
                            if (CountBits(ab) >= 64) { bits = ab; animOnly = true; }
                        }
                        // Walkable shallow pools (island dig site) are plain GROUND in map data —
                        // recognise them by their ART: mostly flat blue-grey pixels. Rocky/pebbled
                        // pool variants only reach ~30-55% coverage → "weak" tier, accepted when
                        // surrounded by enough other pool tiles. OUTDOORS only: grey-blue interior
                        // floors (mines) must never classify as water.
                        if (loc.IsOutdoors)
                        {
                            int pc = PuddleBits(btex, bsrc).count;
                            puddle = pc >= 140 ? (byte)2 : pc >= 80 ? (byte)1 : (byte)0;
                        }
                    }
                    // Animated water art on the BUILDINGS layer — fountains (impassable basin)
                    // AND the beach surf wash (wave overlay tiles ON the sand/pier). Merged with
                    // (not gated behind) the Back result: inside the shore ring the Back layer
                    // always classifies (sand → empty bits, but NOT null), which used to skip
                    // this check entirely and left the whole tide line dead.
                    if (!isWater && TryTileArt(bld, tx, ty, out var ftex, out var fsrc, out bool fAnim) && fAnim)
                    {
                        var fb = ClassifyBits(ftex, fsrc);
                        if (CountBits(fb) >= 64)
                        {
                            if (bits == null) { bits = fb; animOnly = true; }
                            else
                            {
                                // OR-merge into a copy — `bits` may be a cached array.
                                var merged = new bool[256];
                                for (int p = 0; p < 256; p++) merged[p] = bits[p] || fb[p];
                                bits = merged;
                            }
                        }
                    }
                    if (animOnly) anyAnim = true;
                    _animOnlyTileBuf[idx] = animOnly;
                    _puddleTileBuf[idx] = puddle;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits[row + px] = isWater || (bits != null && bits[arow + px]);
                    }
                }
            }
            // Puddle merge — strong tiles need ≥1 puddle neighbour, weak (rocky-variant) tiles
            // need ≥2 (pools span multiple tiles; a lone grey-blue tile must not turn to water).
            if (_puddlePixBits == null || _puddlePixBits.Length < pcount)
                _puddlePixBits = new bool[pcount];
            Array.Clear(_puddlePixBits, 0, pcount);
            bool anyPuddle = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    if (_puddleTileBuf[idx] == 0)
                        continue;
                    int buddies = ((i > 0 && _puddleTileBuf[idx - 1] > 0) ? 1 : 0)
                                + ((i < tilesW - 1 && _puddleTileBuf[idx + 1] > 0) ? 1 : 0)
                                + ((j > 0 && _puddleTileBuf[idx - tilesW] > 0) ? 1 : 0)
                                + ((j < tilesH - 1 && _puddleTileBuf[idx + tilesW] > 0) ? 1 : 0);
                    if (buddies < (_puddleTileBuf[idx] == 2 ? 1 : 2))
                        continue;
                    int tx = startTileX + i, ty = startTileY + j;
                    if (!TryTileArt(back, tx, ty, out var ptex, out var psrc))
                        continue;
                    anyPuddle = true;
                    bool[] pbits = PuddleBits(ptex, psrc).bits;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            if (pbits[arow + px])
                            {
                                _waterPixBits[row + px] = true;
                                _puddlePixBits[row + px] = true;
                            }
                    }
                }
            }
            _waterAny = any || anyPuddle || anyAnim;
            if (!_waterAny)
                return false;
            // Pass B — vertical CLOSE (fill gaps that have water above AND below), two widths:
            //   effect bits: ≤4 texels — heals the dark shading slit the shore art paints
            //                along the waterline without swallowing real land.
            //   march bits:  ≤12 texels (~0.75 tile) — anything painted INSIDE a water body
            //                (surf foam bands, starfish, sand flecks) must not read as a
            //                shoreline, or reflections re-anchor below it and shift down.
            //                Bridges/decks are ≥1 tile thick, so they still block.
            void CloseVertical(bool[] bits, int maxGap)
            {
                for (int x = 0; x < pw; x++)
                {
                    int last = -99;
                    for (int y = 0; y < ph; y++)
                    {
                        if (!bits[y * pw + x])
                            continue;
                        if (y - last > 1 && y - last <= maxGap + 1)
                            for (int k = last + 1; k < y; k++)
                                bits[k * pw + x] = true;
                        last = y;
                    }
                }
            }
            if (_waterPixBits2 == null || _waterPixBits2.Length < pcount)
                _waterPixBits2 = new bool[pcount];
            Array.Copy(_waterPixBits, _waterPixBits2, pcount);
            // Remember every anim-nominated tile before the region pass mutates the buffer —
            // Pass E writes these with a SOFT value so fountains shimmer gently.
            if (_animSoftTileBuf == null || _animSoftTileBuf.Length < count)
                _animSoftTileBuf = new bool[count];
            Array.Copy(_animOnlyTileBuf!, _animSoftTileBuf, count);
            // Animated-art water splits by REGION SHAPE: a wide flat pool (a fountain basin —
            // bbox at least as wide as tall) is a horizontal surface, so it KEEPS the march
            // and gets a real mirror (statue, rim, benches above reflect into it). A tall
            // narrow region is a waterfall FACE — vertical water, scrubbed from the march so
            // no reflection anchors on it (effect channel shimmer only).
            {
                Span<int> stack = count <= 4096 ? stackalloc int[Math.Min(count, 4096)] : new int[count];
                var seen = _waterBool2Buf!; // reuse as visited scratch (ring data is consumed by now)
                Array.Clear(seen, 0, count);
                for (int start = 0; start < count; start++)
                {
                    if (!_animOnlyTileBuf![start] || seen[start])
                        continue;
                    int sp = 0;
                    stack[sp++] = start;
                    seen[start] = true;
                    int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
                    int n = 0;
                    // collect the component (4-connected)
                    var member = new List<int>(16);
                    while (sp > 0)
                    {
                        int cur = stack[--sp];
                        member.Add(cur);
                        n++;
                        int cx = cur % tilesW, cy = cur / tilesW;
                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;
                        if (cx > 0 && _animOnlyTileBuf[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack[sp++] = cur - 1; }
                        if (cx < tilesW - 1 && _animOnlyTileBuf[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack[sp++] = cur + 1; }
                        if (cy > 0 && _animOnlyTileBuf[cur - tilesW] && !seen[cur - tilesW]) { seen[cur - tilesW] = true; stack[sp++] = cur - tilesW; }
                        if (cy < tilesH - 1 && _animOnlyTileBuf[cur + tilesW] && !seen[cur + tilesW]) { seen[cur + tilesW] = true; stack[sp++] = cur + tilesW; }
                    }
                    bool poolLike = (maxX - minX) >= (maxY - minY);
                    if (!poolLike)
                        continue; // waterfall column → scrubbed below
                    foreach (int idx in member)
                        _animOnlyTileBuf[idx] = false; // keep in march
                }
            }
            // Scrub what's left (waterfall faces) from the march copy.
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    if (!_animOnlyTileBuf![j * tilesW + i])
                        continue;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits2[row + px] = false;
                    }
                }
            }
            CloseVertical(_waterPixBits, 4);
            // March close is SPECK-AWARE: a run shorter than 3 texels only bridges gaps
            // ≤4 (a rim sliver above its slit), never the full 12 — wet-shading specks on
            // the bank otherwise chained into the body below, pulling the column's
            // waterline anchor up onto the bank (the surviving dark dashes).
            for (int x = 0; x < pw; x++)
            {
                int last = -99, runH = 0;
                for (int y = 0; y < ph; y++)
                {
                    if (!_waterPixBits2[y * pw + x])
                        continue;
                    int gap = y - last - 1;
                    if (gap == 0)
                        runH++;
                    else if (gap <= 12 && (gap <= 4 || runH >= 3))
                    {
                        for (int k = last + 1; k < y; k++)
                            _waterPixBits2[k * pw + x] = true;
                        runH += gap + 1;
                    }
                    else
                        runH = 1;
                    last = y;
                }
            }
            // Structure test for the MARCH channel: near-solid art (≥90% opaque) that is
            // CONNECTED TO LAND. A bridge or pier always touches a bank; a clump of lily pads
            // dense enough to fill its tile still floats in open water — opacity alone let pad
            // clusters re-anchor reflections below them. Connectivity: seed near-solid tiles
            // that touch a non-water tile (or the screen edge — the structure may continue
            // off-screen), then grow the seed through adjacent near-solid tiles.
            if (_bigCarveBuf == null || _bigCarveBuf.Length < count) _bigCarveBuf = new bool[count];
            if (_bigSeedBuf == null || _bigSeedBuf.Length < count) _bigSeedBuf = new bool[count];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    int tx = startTileX + i, ty = startTileY + j;
                    // Height Framework DECK tiles (walkable piers / plank bridges — Back-layer
                    // wood) block as whole tiles too: the beach plank's art has a painted wet
                    // stain that classified as water, punching a 2-texel channel through the
                    // deck — and the ±10 shoreline smoothing then dragged the anchors of a
                    // full tile around it up above the plank (reflection missing on that side).
                    bool deck = false;
                    if (hf != null)
                        try { deck = hf.GetSurfaceAt(loc, tx, ty) == 4; } catch { hf = null; }
                    bool big = deck
                            || (TryTileArt(bld, tx, ty, out var t1, out var s1) && SolidBits(t1, s1).count >= 230)
                            || (TryTileArt(front, tx, ty, out var t2, out var s2) && SolidBits(t2, s2).count >= 230);
                    _bigCarveBuf[idx] = big;
                    bool landNear = i == 0 || i == tilesW - 1 || j == 0 || j == tilesH - 1
                        || !(_waterMaskCoreBuf![idx - 1].R > 0) || !(_waterMaskCoreBuf[idx + 1].R > 0)
                        || !(_waterMaskCoreBuf[idx - tilesW].R > 0) || !(_waterMaskCoreBuf[idx + tilesW].R > 0);
                    // A deck is walkable — land-connected by definition, no seed test needed.
                    _bigSeedBuf[idx] = big && (landNear || deck);
                }
            }
            for (int sweep = 0; sweep < 2; sweep++)
            {
                for (int idx = 0; idx < count; idx++)                       // forward
                    if (_bigCarveBuf[idx] && !_bigSeedBuf[idx] &&
                        ((idx % tilesW > 0 && _bigSeedBuf[idx - 1]) || (idx >= tilesW && _bigSeedBuf[idx - tilesW])))
                        _bigSeedBuf[idx] = true;
                for (int idx = count - 1; idx >= 0; idx--)                  // backward
                    if (_bigCarveBuf[idx] && !_bigSeedBuf[idx] &&
                        ((idx % tilesW < tilesW - 1 && _bigSeedBuf[idx + 1]) || (idx + tilesW < count && _bigSeedBuf[idx + tilesW])))
                        _bigSeedBuf[idx] = true;
            }

            // ARCH FILL: a bridge's arch openings sit BETWEEN structure tiles in the same row.
            // Fill gaps ≤3 tiles between two structure tiles when the gap tile itself carries
            // Buildings/Front art (arch rims do; open water between two separate piers doesn't)
            // — the structure becomes ONE solid block with a level base, so every column's
            // reflection anchors on the same row, like a real bridge mirrored in water.
            for (int j = 0; j < tilesH; j++)
            {
                int lastStruct = -99;
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    if (!_bigSeedBuf[idx])
                        continue;
                    if (i - lastStruct > 1 && i - lastStruct <= 4)
                    {
                        for (int k = lastStruct + 1; k < i; k++)
                        {
                            int kx = startTileX + k, ky = startTileY + j;
                            if (TryTileArt(bld, kx, ky, out _, out _) || TryTileArt(front, kx, ky, out _, out _))
                                _bigSeedBuf[j * tilesW + k] = true;
                        }
                    }
                    lastStruct = i;
                }
            }

            // Pass C — carve opaque Buildings/Front art and emit two channels:
            //   R = EFFECT mask: carve everything opaque (no ripple/mirror ON posts, pads, bridges).
            //   G = MARCH mask: carve only land-connected structures (see above).
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    int tx = startTileX + i, ty = startTileY + j;
                    (bool[] bits, int count)? carveB = TryTileArt(bld, tx, ty, out var t1, out var s1) ? SolidBits(t1, s1) : null;
                    (bool[] bits, int count)? carveF = TryTileArt(front, tx, ty, out var t2, out var s2) ? SolidBits(t2, s2) : null;
                    // A structure tile blocks the march as a WHOLE tile (arch openings included):
                    // per-pixel carving gave each column its own edge and the mirror stepped.
                    bool structTile = _bigSeedBuf[idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                        {
                            if (structTile)
                                _waterPixBits2![row + px] = false;
                            if (carveB is { } cb && cb.bits[arow + px]) _waterPixBits[row + px] = false;
                            if (carveF is { } cf && cf.bits[arow + px]) _waterPixBits[row + px] = false;
                        }
                    }
                }
            }

            // Pass C2 — carve FURNITURE and BUILDING entities. A fish tank's painted water,
            // a well's blue bucket art, a trough — these are water pixels inside an ENTITY
            // sprite, not a water body, and the tile-art carve above can't see entities
            // (they aren't map layers). Their drawn rect (footprint + the taller art above
            // it) is cleared from BOTH channels. Entities don't move, so doing this at mask
            // rebuild costs nothing per frame.
            void CarveRect(int wx0, int wy0, int wx1, int wy1)
            {
                int px0 = Math.Max(0, wx0 / 4 - startTileX * Sub);
                int py0 = Math.Max(0, wy0 / 4 - startTileY * Sub);
                int px1 = Math.Min(pw, wx1 / 4 - startTileX * Sub);
                int py1 = Math.Min(ph, wy1 / 4 - startTileY * Sub);
                for (int y = py0; y < py1; y++)
                {
                    int row = y * pw;
                    for (int x = px0; x < px1; x++)
                    {
                        _waterPixBits![row + x] = false;
                        _waterPixBits2![row + x] = false;
                    }
                }
            }
            foreach (var f in loc.furniture)
            {
                Rectangle bb = f.boundingBox.Value;
                int artH = f.sourceRect.Value.Height * 4;
                CarveRect(bb.X, bb.Bottom - Math.Max(artH, bb.Height), bb.Right, bb.Bottom);
            }
            foreach (var b in loc.buildings)
            {
                if (b == null)
                    continue;
                int bx = b.tileX.Value * 64, bw2 = b.tilesWide.Value * 64;
                int bottom = (b.tileY.Value + b.tilesHigh.Value) * 64;
                int artH = b.tilesHigh.Value * 64;
                try { int sh = b.getSourceRect().Height * 4; if (sh > 0) artH = Math.Max(artH, sh); }
                catch { /* sprite not ready — footprint only */ }
                CarveRect(bx, bottom - artH, bx + bw2, bottom);
            }

            // Pass D — WATERLINE HEIGHT-MAP: per column, remember the top row of each
            // contiguous march-water run (= that pixel's shoreline). Runs shorter than
            // 6 texels are DROPPED from the march: isolated wet-shading specks in shore
            // art each became a tiny mirror (dist 0) that painted a dark dash onto the
            // bank. Runs cut off by the mask bottom are kept — they continue off-screen.
            if (_edgeBuf == null || _edgeBuf.Length < pcount)
                _edgeBuf = new short[pcount];
            for (int x = 0; x < pw; x++)
            {
                int top = -1;
                for (int y = 0; y <= ph; y++)
                {
                    int p = y * pw + x;
                    if (y < ph && _waterPixBits2![p]) { if (top < 0) top = y; _edgeBuf[p] = (short)top; }
                    else if (top >= 0)
                    {
                        if (y < ph && y - top < 6)
                            for (int k = top; k < y; k++)
                                _waterPixBits2![k * pw + x] = false;
                        top = -1;
                    }
                }
            }

            // Pass E — smooth the shoreline HORIZONTALLY (±10 texels window) and emit. Stepped
            // diagonal banks become a continuous slope, so a reflection is no longer sliced
            // into offset blocks — the shader reads this distance (B, half-texel units) instead
            // of marching. Uses per-row PREFIX SUMS (O(width) per row, was O(width×21)); the
            // window average is clamped to ±1.5 tiles of the pixel's own edge, which bounds the
            // pull from a different water body sharing the row (the old per-neighbour reject).
            if (_edgeSum == null || _edgeSum.Length < pw + 1) { _edgeSum = new int[pw + 1]; _edgeCnt = new int[pw + 1]; }
            for (int y = 0; y < ph; y++)
            {
                int rowBase = y * pw;
                for (int x = 0; x < pw; x++)
                {
                    int p = rowBase + x;
                    bool v = _waterPixBits2![p];
                    _edgeSum![x + 1] = _edgeSum[x] + (v ? _edgeBuf[p] : 0);
                    _edgeCnt![x + 1] = _edgeCnt[x] + (v ? 1 : 0);
                }
                for (int x = 0; x < pw; x++)
                {
                    int p = rowBase + x;
                    bool eff = _waterPixBits[p];
                    bool march = _waterPixBits2![p];
                    byte bch = 255;
                    if (march)
                    {
                        int t0 = _edgeBuf[p];
                        int x0 = Math.Max(0, x - 10), x1 = Math.Min(pw - 1, x + 10);
                        int n = _edgeCnt![x1 + 1] - _edgeCnt[x0];
                        float ts = n > 0 ? (float)(_edgeSum[x1 + 1] - _edgeSum[x0]) / n : t0;
                        ts = MathHelper.Clamp(ts, t0 - 24, t0 + 24);
                        bch = (byte)MathHelper.Clamp((float)Math.Round((y - ts) * 2f), 0f, 252f);
                    }
                    // Shallow puddles get a SOFTER mask value: every effect (ripple, sparkle,
                    // mirror) scales with it, so a walk-through pool shimmers gently instead of
                    // sparkling like open water. Animated-art water (fountains) is softer still —
                    // a fountain basin should barely shimmer, not churn like a lake.
                    byte effV = !eff ? (byte)0
                        : _animSoftTileBuf![(y / Sub) * tilesW + (x / Sub)] ? (byte)140
                        : _puddlePixBits![p] ? (byte)205 : (byte)255;
                    _waterPixBuf[p] = new Color(effV, march ? 255 : 0, bch, 255);
                }
            }
            if (_waterMask == null || _waterMask.Width != pw || _waterMask.Height != ph)
            {
                _waterMask?.Dispose();
                _waterMask = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _waterMask.SetData(_waterPixBuf, 0, pcount);
            if (_waterMaskCore == null || _waterMaskCore.Width != tilesW || _waterMaskCore.Height != tilesH)
            {
                _waterMaskCore?.Dispose();
                _waterMaskCore = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _waterMaskCore.SetData(_waterMaskCoreBuf, 0, count);

            _waterTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
            _waterWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _waterMaskSize = new Vector2(tilesW, tilesH);
            return true;
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
