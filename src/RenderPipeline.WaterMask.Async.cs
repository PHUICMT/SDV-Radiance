using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - ASYNC water-mask rebuild. The monolithic rebuild (8-23 ms every
    /// tile crossing) was THE walking-near-water stutter, so it is split into three phases:
    ///
    ///   Gather  (main thread, ~1 ms) - everything that touches game state: tile data,
    ///            Height Framework, art classifications (cached GetData), furniture and
    ///            building rects. Output is plain arrays the worker can chew on.
    ///   Compose (worker thread)     - passes A-E, the actual pixel crunching. Pure
    ///            array work on the gathered data; never touches Game1/GameLocation.
    ///   Apply   (main thread, ~1 ms) - SetData the finished buffers into the textures
    ///            and publish the new mask origin.
    ///
    /// While a compose is in flight the OLD mask keeps rendering: its content is
    /// world-anchored (MaskOrigin stays at the old origin until Apply), and the window
    /// is padded 2 tiles left/right + 4 above the viewport, so a one-or-two-frame lag
    /// never shows a naked edge. Jobs are strictly serialized - a new gather starts only
    /// when no job is in flight - so the shared scratch buffers need no locking.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Metadata for one in-flight rebuild. The pixel buffers live on the
        /// pipeline (jobs are serialized); this carries identity + results + status.</summary>
        private sealed class WaterMaskJob
        {
            public GameLocation Loc = null!;
            public int Tx, Ty, TilesW, TilesH, HookVer, LabelVer;
            public bool AnyWater;              // gather: any true water tile
            public bool AnyAnim;               // gather: any anim-nominated tile
            public bool AnyLabeled;            // gather: any label-nominated water art
            public bool WaterAny;              // compose: final any-water verdict
            public double ComposeMs;           // worker-side timing (diag)
            public System.Threading.Tasks.Task? Task;
            public volatile bool Done;
            public volatile bool Failed;
        }

        private WaterMaskJob? _waterJob;
        private bool _loggedWaterJobFail;

        // ---- gathered per-tile inputs (main thread writes, worker reads) ----
        private bool[]? _waterRingBuf;         // extra dilation scratch (ring must not destroy _waterBoolBuf)
        private bool[]?[]? _tileBitsBuf;       // effect-channel art classification per tile (null = none)
        private bool[]?[]? _tilePuddleBitsBuf; // puddle classification bits per tile (tier > 0 only)
        private bool[]?[]? _tileCarveBBuf;     // Buildings-layer opacity bits (null = no art)
        private bool[]?[]? _tileCarveFBuf;     // Front-layer opacity bits
        private bool[]? _tileBigSolidBuf;      // near-solid (>=230/256 opaque) Buildings/Front art
        private bool[]? _tileDeckBuf;          // Height Framework DECK tile
        private bool[]? _tileHasBldBuf;        // any Buildings art at all (arch fill test)
        private bool[]? _tileHasFrontBuf;
        private bool[]? _tileIceBuf;           // HF label class 9: frozen — reflection, no ripple
        private bool[]? _tileFlowBuf;          // HF label class 10: flowing/waterfall — ripple, no reflection
        private bool[]? _tileLavaBuf;          // HF label class 11: lava — slow molten flow, self-glow, no reflection
        private bool[]? _tileRefineBuf;        // P0-A: isWater tile whose bits REPLACE the full-tile fill (per-pixel shoreline)
        private bool[]?[]? _tileBitsStableBuf; // frame-CONSENSUS bits for animated tiles (march/edge channels) — the
                                               // current-frame bits drive effects, but reflections/wading anchor on the
                                               // consensus waterline so they don't lurch on every surf frame flip
        // Previous applied mask's R channel (world-anchored) — temporal smoothing source so
        // the surf-frame flips fade through rebuilds instead of snapping (see compose tail).
        private byte[]? _prevEffBuf;
        private int _prevEffTx, _prevEffTy, _prevEffW, _prevEffH;
        private GameLocation? _prevEffLoc;
        private bool[]? _tileWetFlag;          // per-tile: has any effect-water pixel (for body-size flood fill)
        private byte[]? _tileCalmBuf;          // per-tile 0..255 wave scale by water-body size (small = calmer)
        private readonly List<(int x0, int y0, int x1, int y1)> _carveRects = new();

        /// <summary>HF Studio label version, for the mask cache key (0 = no HF / old HF).
        /// Live-sync repaint: painting in the browser bumps this within seconds.</summary>
        private static int CurrentLabelVersion()
        {
            var hf = ShadowRenderer.Height;
            if (hf == null)
                return 0;
            try { return hf.GetLabelVersion(); }
            catch { return 0; }
        }

        /// <summary>Water bits from 256 HF Studio per-pixel labels. Classes 1 (water), 9 (ice) and
        /// 10 (flowing) are ALL water for the mask; per-class counts let the tile pick a behaviour
        /// (ice = mirror only, flowing = ripple only).</summary>
        private static (bool[] bits, int nWater, int nIce, int nFlow, int nLava) WaterBitsFromLabels(byte[] classes)
        {
            var bits = new bool[256];
            int nW = 0, nI = 0, nF = 0, nL = 0;
            for (int p = 0; p < 256; p++)
            {
                byte c = classes[p];
                if (c == 1) { bits[p] = true; nW++; }
                else if (c == 9) { bits[p] = true; nI++; }
                else if (c == 10) { bits[p] = true; nF++; }
                else if (c == 11) { bits[p] = true; nL++; }   // lava: slow molten flow + self-glow
            }
            return (bits, nW, nI, nF, nL);
        }

        /// <summary>Window-independent water flag straight from map data — used for boundary
        /// decisions that must not change as the gather window moves with the player.
        /// (Off-map counts as water so map-border water tiles don't become "shoreline".)</summary>
        private static bool MapWater(GameLocation loc, int tx, int ty)
        {
            try
            {
                var back = loc.map?.GetLayer("Back");
                if (back == null || tx < 0 || ty < 0 || tx >= back.LayerWidth || ty >= back.LayerHeight)
                    return true;
                return loc.isWaterTile(tx, ty);
            }
            catch { return true; }
        }

        /// <summary>Gather stage - read every game-state dependency into plain arrays.
        /// MUST run on the main thread (content loads, texture GetData via the
        /// classification caches, live entity lists).</summary>
        private WaterMaskJob GatherWaterMask(GameLocation loc, int startTileX, int startTileY, int tilesW, int tilesH)
        {
            int count = tilesW * tilesH;
            var job = new WaterMaskJob
            {
                Loc = loc, Tx = startTileX, Ty = startTileY,
                TilesW = tilesW, TilesH = tilesH, HookVer = WaterDrawHook.Version,
                LabelVer = CurrentLabelVersion(),
            };

            // Height Framework (when present) classifies the actual water SURFACE: ponds and
            // beach tide pools count as water (they reflect too), while pier/bridge DECKS over
            // water do not (no reflection painted onto planks). Fall back to isWaterTile.
            var hf = ShadowRenderer.Height;
            if (_waterBoolBuf == null || _waterBoolBuf.Length < count) _waterBoolBuf = new bool[count];
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
            job.AnyWater = any;

            // CANDIDATE ring: dilate three tiles (shore art + beach surf zone). These tiles are
            // NOT marked water — they only nominate their ART for per-pixel classification below,
            // so the final mask never spills a box past the painted waterline. The ring lands in
            // _waterBool2Buf; _waterBoolBuf (the real water flags) survives for the worker.
            if (_waterBool2Buf == null || _waterBool2Buf.Length < count) _waterBool2Buf = new bool[count];
            if (_waterRingBuf == null || _waterRingBuf.Length < count) _waterRingBuf = new bool[count];
            Dilate8(_waterBoolBuf, _waterBool2Buf, tilesW, tilesH);
            Dilate8(_waterBool2Buf, _waterRingBuf, tilesW, tilesH);
            Dilate8(_waterRingBuf, _waterBool2Buf, tilesW, tilesH);

            // P0-B: calibrate the water-colour palette from this window's interior water art
            // BEFORE any classification below (ClassifyBits keys on the palette version).
            BuildWaterPalette(loc, startTileX, startTileY, tilesW, tilesH);

            var back = loc.map?.GetLayer("Back");
            var bld = loc.map?.GetLayer("Buildings");
            var front = loc.map?.GetLayer("Front");
            // AlwaysFront* (incl. numeric suffixes some maps/mods add): roof peaks and other
            // over-player art live here — the fish shop's roof sits on ocean tiles, and
            // without carving these layers the water shimmered straight through it.
            List<xTile.Layers.Layer>? always = null;
            if (loc.map != null)
                foreach (var l in loc.map.Layers)
                    if (l.Id.StartsWith("AlwaysFront", StringComparison.Ordinal))
                        (always ??= new()).Add(l);
            bool outdoors = loc.IsOutdoors;

            if (_tileBitsBuf == null || _tileBitsBuf.Length < count) _tileBitsBuf = new bool[]?[count];
            if (_tilePuddleBitsBuf == null || _tilePuddleBitsBuf.Length < count) _tilePuddleBitsBuf = new bool[]?[count];
            if (_tileCarveBBuf == null || _tileCarveBBuf.Length < count) _tileCarveBBuf = new bool[]?[count];
            if (_tileCarveFBuf == null || _tileCarveFBuf.Length < count) _tileCarveFBuf = new bool[]?[count];
            if (_tileBigSolidBuf == null || _tileBigSolidBuf.Length < count) _tileBigSolidBuf = new bool[count];
            if (_tileDeckBuf == null || _tileDeckBuf.Length < count) _tileDeckBuf = new bool[count];
            if (_tileHasBldBuf == null || _tileHasBldBuf.Length < count) _tileHasBldBuf = new bool[count];
            if (_tileHasFrontBuf == null || _tileHasFrontBuf.Length < count) _tileHasFrontBuf = new bool[count];
            if (_puddleTileBuf == null || _puddleTileBuf.Length < count) _puddleTileBuf = new byte[count];
            if (_animOnlyTileBuf == null || _animOnlyTileBuf.Length < count) _animOnlyTileBuf = new bool[count];
            if (_tileIceBuf == null || _tileIceBuf.Length < count) _tileIceBuf = new bool[count];
            if (_tileFlowBuf == null || _tileFlowBuf.Length < count) _tileFlowBuf = new bool[count];
            if (_tileLavaBuf == null || _tileLavaBuf.Length < count) _tileLavaBuf = new bool[count];
            if (_tileRefineBuf == null || _tileRefineBuf.Length < count) _tileRefineBuf = new bool[count];
            if (_tileBitsStableBuf == null || _tileBitsStableBuf.Length < count) _tileBitsStableBuf = new bool[]?[count];

            bool anyAnim = false;
            bool animMatters = false;   // any ANIMATED art contributed water bits → mask must follow frames (P0-C)
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterBoolBuf[idx];
                    int tx = startTileX + i, ty = startTileY + j;
                    bool[]? bits = null;
                    bool[]? sbits = null;      // frame-consensus variant of bits (null = bits are already stable)
                    byte puddle = 0;
                    bool animOnly = false;
                    bool refineTile = false;   // P0-A: bits REPLACE the full-tile water fill
                    int iceN = 0, flowN = 0, lavaN = 0;   // accumulated across Back + Buildings labels
                    // ---- GROUND-TRUTH LABELS FIRST (HF Studio). A labeled Back art is
                    // authoritative: its water pixels join the mask (STATIC painted pools on
                    // custom maps included — no ring or animation requirement), and a labeled
                    // art with no water pixels never reaches the color classifier at all.
                    bool labeledBack = false;
                    if (!isWater && hf != null)
                    {
                        byte[]? lbl = null;
                        try { lbl = hf.GetPixelClasses(loc, tx, ty, "Back"); }
                        catch { hf = null; }
                        if (lbl != null)
                        {
                            labeledBack = true;
                            var (lb, nW, nI, nF, nL) = WaterBitsFromLabels(lbl);
                            if (nW + nI + nF + nL > 0)
                            {
                                bits = lb;
                                iceN += nI; flowN += nF; lavaN += nL;
                                job.AnyLabeled = true;
                            }
                        }
                    }
                    // isWater tiles (game-flagged): labels first — sub-type counts always (lava
                    // pools and frozen water ARE isWaterTile), and a WELL-COVERED label refines
                    // the tile per-pixel (class 0 = unpainted, NOT "no water", so a sparse label
                    // must not erase the tile). Without a label, a SHORELINE tile (any non-water
                    // 8-neighbour) refines from its ART instead of the full-tile fill (P0-A):
                    // the painted sand/rock inside a game-flagged water tile stops getting
                    // ripple/reflection, which was every "boxy shoreline" complaint at once.
                    else if (isWater)
                    {
                        if (hf != null)
                        {
                            byte[]? lbl = null;
                            try { lbl = hf.GetPixelClasses(loc, tx, ty, "Back"); }
                            catch { hf = null; }
                            if (lbl != null)
                            {
                                var (lb, nW, nI, nF, nL) = WaterBitsFromLabels(lbl);
                                if (nI + nF + nL > 0)
                                {
                                    iceN += nI; flowN += nF; lavaN += nL;
                                    job.AnyLabeled = true;
                                }
                                int nPainted = 0;
                                for (int p = 0; p < 256; p++)
                                    if (lbl[p] != 0) nPainted++;
                                if (nPainted >= 192)   // ≥75% painted → treat unpainted as not-water
                                {
                                    bits = lb;
                                    refineTile = true;
                                    job.AnyLabeled = true;
                                }
                            }
                        }
                        if (!refineTile)
                        {
                            // MAP-GLOBAL neighbour test — never the window flags: a tile near
                            // the window border used to flip shoreline↔interior as the player
                            // walked and the window edge crossed its neighbours, which moved
                            // the mask underfoot (proven with world-aligned dumps).
                            bool shoreline = !MapWater(loc, tx - 1, ty) || !MapWater(loc, tx + 1, ty)
                                          || !MapWater(loc, tx, ty - 1) || !MapWater(loc, tx, ty + 1)
                                          || !MapWater(loc, tx - 1, ty - 1) || !MapWater(loc, tx + 1, ty - 1)
                                          || !MapWater(loc, tx - 1, ty + 1) || !MapWater(loc, tx + 1, ty + 1);
                            if (shoreline && TryTileArt(back, tx, ty, out var wtex, out var wsrc, out bool wAnim))
                            {
                                // foam:true — white surf wash ON the water tile is water for effects.
                                var wb = ClassifyBits(wtex, wsrc, foam: true);
                                int wn = CountBits(wb);
                                // Trust the art when the palette is calibrated (interior water in
                                // sight = same art family). Uncalibrated windows (thin rivers have
                                // no interior tile) keep a floor: a near-empty result there means
                                // "classifier lost", not "tile is sand" — fall back to full fill.
                                if (_palColors.Count > 0 ? wn < 256 : wn is >= 16 and < 256)
                                {
                                    bits = wb;
                                    refineTile = true;
                                    if (wAnim)
                                    {
                                        animMatters = true;
                                        sbits = ConsensusBits(back, tx, ty, foam: true);
                                    }
                                }
                            }
                        }
                    }
                    if (!isWater && !labeledBack && TryTileArt(back, tx, ty, out var btex, out var bsrc, out bool bAnim))
                    {
                        if (_waterBool2Buf[idx])
                        {
                            // Surf line: animated tiles CARDINALLY touching core water get the
                            // foam rule too — white wave wash failed every hue gate and left
                            // dead un-effected bands along the tide.
                            bool coreAdj = (i > 0 && _waterBoolBuf[idx - 1])
                                        || (i < tilesW - 1 && _waterBoolBuf[idx + 1])
                                        || (j > 0 && _waterBoolBuf[idx - tilesW])
                                        || (j < tilesH - 1 && _waterBoolBuf[idx + tilesW]);
                            bits = ClassifyBits(btex, bsrc, foam: bAnim && coreAdj);
                            if (bAnim && CountBits(bits) > 0)
                            {
                                animMatters = true;
                                sbits = ConsensusBits(back, tx, ty, foam: coreAdj);
                            }
                        }
                        else if (bAnim)
                        {
                            // ANIMATED water art anywhere on the map — fountains, waterfalls,
                            // decorative pools that aren't isWaterTile and sit outside the
                            // shore ring. Mostly-water animated art joins the EFFECT channel
                            // only (ripple/sparkle/tint), never the march: a reflection must
                            // not anchor on a fountain rim or a waterfall face.
                            var ab = ClassifyBits(btex, bsrc);
                            if (CountBits(ab) >= 64)
                            {
                                bits = ab; animOnly = true; animMatters = true;
                                sbits = ConsensusBits(back, tx, ty, foam: false);
                            }
                        }
                        // Walkable shallow pools (island dig site) are plain GROUND in map data —
                        // recognise them by their ART: mostly flat blue-grey pixels. Rocky/pebbled
                        // pool variants only reach ~30-55% coverage → "weak" tier, accepted when
                        // surrounded by enough other pool tiles. OUTDOORS only: grey-blue interior
                        // floors (mines) must never classify as water.
                        if (outdoors)
                        {
                            var pb = PuddleBits(btex, bsrc);
                            puddle = pb.count >= 140 ? (byte)2 : pb.count >= 80 ? (byte)1 : (byte)0;
                            _tilePuddleBitsBuf[idx] = puddle > 0 ? pb.bits : null;
                        }
                        else
                            _tilePuddleBitsBuf[idx] = null;
                    }
                    else
                        _tilePuddleBitsBuf[idx] = null;
                    // Animated water art on the BUILDINGS layer — fountains (impassable basin)
                    // AND the beach surf wash (wave overlay tiles ON the sand/pier). Merged with
                    // (not gated behind) the Back result: inside the shore ring the Back layer
                    // always classifies (sand → empty bits, but NOT null), which used to skip
                    // this check entirely and left the whole tide line dead.
                    bool hasBld = TryTileArt(bld, tx, ty, out var t1, out var s1, out bool fAnim);
                    // Buildings-layer overlay water: labels first (a labeled fountain rim /
                    // surf overlay needs no animation), color-classified animated art otherwise.
                    bool[]? overlayBits = null;
                    bool labeledBld = false;
                    if (!isWater && hf != null)
                    {
                        byte[]? lbl = null;
                        try { lbl = hf.GetPixelClasses(loc, tx, ty, "Buildings"); }
                        catch { hf = null; }
                        if (lbl != null)
                        {
                            labeledBld = true;
                            var (ob, nW, nI, nF, nL) = WaterBitsFromLabels(lbl);
                            if (nW + nI + nF + nL >= 8)
                            {
                                overlayBits = ob;
                                iceN += nI; flowN += nF; lavaN += nL;
                                job.AnyLabeled = true;
                            }
                        }
                    }
                    bool[]? overlayStable = null;
                    if (!isWater && !labeledBld && hasBld && fAnim)
                    {
                        var fb = ClassifyBits(t1, s1);
                        if (CountBits(fb) >= 64)
                        {
                            overlayBits = fb;
                            animMatters = true;
                            overlayStable = ConsensusBits(bld, tx, ty, foam: false);
                        }
                    }
                    if (overlayBits != null)
                    {
                        // LABELED water is ground truth → full treatment (ripple + reflection),
                        // never the soft animated-art path. Only COLOR-classified animated art
                        // (labeledBld == false) stays effect-only so a fountain doesn't churn.
                        if (bits == null) { bits = overlayBits; sbits = overlayStable; animOnly = !labeledBld; }
                        else
                        {
                            // OR-merge into a copy — `bits` may be a cached array. The stable
                            // variant merges each layer's consensus (falling back to that
                            // layer's live bits when it isn't animated).
                            var merged = new bool[256];
                            bool anyStable = sbits != null || overlayStable != null;
                            var smerged = anyStable ? new bool[256] : null;
                            for (int p = 0; p < 256; p++)
                            {
                                merged[p] = bits[p] || overlayBits[p];
                                if (smerged != null)
                                    smerged[p] = (sbits ?? bits)[p] || (overlayStable ?? overlayBits)[p];
                            }
                            bits = merged;
                            sbits = smerged;
                            if (labeledBld) animOnly = false;
                        }
                    }
                    if (animOnly) anyAnim = true;
                    _animOnlyTileBuf[idx] = animOnly;
                    _puddleTileBuf[idx] = puddle;
                    // Surf tracking disabled → animated tiles use their frame-CONSENSUS bits for
                    // the EFFECT channel too: each rebuild (tile crossing / 10s safety) used to
                    // capture whichever frame the surf happened to be on, and the crossfade then
                    // MORPHED the effects to the new snapshot — a slow slide while standing
                    // still. Consensus in both channels = every rebuild reproduces the same
                    // mask bit-for-bit, exactly the stability 1.2.0 had.
                    if (!AnimTrackingEnabled && sbits != null)
                        bits = sbits;
                    _tileBitsBuf[idx] = bits;
                    _tileRefineBuf[idx] = refineTile;
                    _tileBitsStableBuf[idx] = sbits;
                    // Ice / flowing win over each other by pixel count; a plain-water majority
                    // keeps normal behaviour. Ice → reflection but no ripple (mask alpha 0);
                    // flowing → ripple but no reflection (scrubbed from the march channel).
                    _tileIceBuf[idx] = iceN > 0 && iceN >= flowN && iceN >= lavaN;
                    _tileFlowBuf[idx] = flowN > 0 && flowN > iceN && flowN >= lavaN;
                    _tileLavaBuf[idx] = lavaN > 0 && lavaN > iceN && lavaN > flowN;

                    // Structure / carve inputs (Pass C + the land-connectivity test + arch fill).
                    bool hasFront = TryTileArt(front, tx, ty, out var t2, out var s2);
                    _tileHasBldBuf[idx] = hasBld;
                    var cb = hasBld ? SolidBits(t1, s1) : default;
                    var cf = hasFront ? SolidBits(t2, s2) : default;
                    bool[]? fBits = hasFront ? cf.bits : null;
                    int fCount = cf.count;
                    // Fold every AlwaysFront layer's opacity into the Front carve channel.
                    if (always != null)
                        foreach (var l in always)
                            if (TryTileArt(l, tx, ty, out var t3, out var s3))
                            {
                                var ca = SolidBits(t3, s3);
                                if (ca.count == 0)
                                    continue;
                                if (fBits == null) fBits = ca.bits;
                                else
                                {
                                    var merged = new bool[256];
                                    for (int p = 0; p < 256; p++) merged[p] = fBits[p] || ca.bits[p];
                                    fBits = merged;
                                }
                                fCount = Math.Max(fCount, ca.count);
                            }
                    _tileHasFrontBuf[idx] = fBits != null;
                    _tileCarveBBuf[idx] = hasBld ? cb.bits : null;
                    _tileCarveFBuf[idx] = fBits;
                    // Height Framework DECK tiles (walkable piers / plank bridges — Back-layer
                    // wood) block as whole tiles too: the beach plank's art has a painted wet
                    // stain that classified as water, punching a 2-texel channel through the
                    // deck — and the ±10 shoreline smoothing then dragged the anchors of a
                    // full tile around it up above the plank (reflection missing on that side).
                    bool deck = false;
                    if (hf != null)
                        try { deck = hf.GetSurfaceAt(loc, tx, ty) == 4; } catch { hf = null; }
                    _tileDeckBuf[idx] = deck;
                    _tileBigSolidBuf[idx] = deck || (hasBld && cb.count >= 230) || fCount >= 230;
                }
            }
            job.AnyAnim = anyAnim;

            // P0-C: remember every AnimatedTile in the window with the frame it was classified
            // at. BuildWaterMask polls this list (throttled) and rebuilds when a frame flips —
            // but only when animated art actually fed the mask (animMatters), so a purely
            // decorative animation (a flag, a chimney) never costs a rebuild.
            _animWatch.Clear();
            _animWatchAffectsMask = animMatters;
            if (animMatters)
            {
                for (int j = 0; j < tilesH; j++)
                {
                    for (int i = 0; i < tilesW; i++)
                    {
                        int tx = startTileX + i, ty = startTileY + j;
                        if (back != null && tx >= 0 && ty >= 0 && tx < back.LayerWidth && ty < back.LayerHeight
                            && back.Tiles[tx, ty] is xTile.Tiles.AnimatedTile ab2)
                        {
                            int fi; try { fi = ab2.TileIndex; } catch { fi = -1; }
                            _animWatch.Add((ab2, fi));
                        }
                        if (bld != null && tx >= 0 && ty >= 0 && tx < bld.LayerWidth && ty < bld.LayerHeight
                            && bld.Tiles[tx, ty] is xTile.Tiles.AnimatedTile ab3)
                        {
                            int fi; try { fi = ab3.TileIndex; } catch { fi = -1; }
                            _animWatch.Add((ab3, fi));
                        }
                    }
                }
            }

            // FURNITURE and BUILDING entity rects (Pass C2 inputs). A fish tank's painted
            // water, a well's blue bucket art, a trough — water pixels inside an ENTITY
            // sprite, not a water body. Snapshot their drawn rects here: entity lists are
            // live game state the worker must never touch.
            _carveRects.Clear();
            foreach (var f in loc.furniture)
            {
                Rectangle bb = f.boundingBox.Value;
                int artH = f.sourceRect.Value.Height * 4;
                _carveRects.Add((bb.X, bb.Bottom - Math.Max(artH, bb.Height), bb.Right, bb.Bottom));
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
                _carveRects.Add((bx, bottom - artH, bx + bw2, bottom));
            }
            return job;
        }

        /// <summary>Compose stage - the pixel crunching (passes A-E). Pure array work on gathered
        /// data; safe on a worker thread. Jobs are serialized, so the shared scratch
        /// buffers are exclusively this job's while it runs.</summary>
        private void ComposeWaterMask(WaterMaskJob job)
        {
            int tilesW = job.TilesW, tilesH = job.TilesH;
            int count = tilesW * tilesH;
            const int Sub = 16;
            int pw = tilesW * Sub, ph = tilesH * Sub;
            int pcount = pw * ph;

            // CORE mask (undilated): the reflection's shoreline search must see bridges,
            // piers and banks as land — the dilated mask swallowed any land strip ≤4 tiles
            // wide (a bridge between two water bodies), which killed their reflections.
            if (_waterMaskCoreBuf == null || _waterMaskCoreBuf.Length < count)
                _waterMaskCoreBuf = new Color[count];
            for (int idx = 0; idx < count; idx++)
                _waterMaskCoreBuf[idx] = _waterBoolBuf![idx] ? Color.White : Color.Transparent;

            // ---- Pass A — composite: true water tiles solid, classified art per-pixel ----
            if (_waterPixBuf == null || _waterPixBuf.Length < pcount) _waterPixBuf = new Color[pcount];
            if (_waterPixBits == null || _waterPixBits.Length < pcount) _waterPixBits = new bool[pcount];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterBoolBuf![idx];
                    bool[]? bits = _tileBitsBuf![idx];
                    // P0-A: a refined water tile paints its CLASSIFIED bits instead of the
                    // full-tile fill — the shoreline follows the art, not the tile grid.
                    bool refine = _tileRefineBuf![idx] && bits != null;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits[row + px] = refine ? bits![arow + px]
                                : isWater || (bits != null && bits[arow + px]);
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
                    if (_puddleTileBuf![idx] == 0)
                        continue;
                    int buddies = ((i > 0 && _puddleTileBuf[idx - 1] > 0) ? 1 : 0)
                                + ((i < tilesW - 1 && _puddleTileBuf[idx + 1] > 0) ? 1 : 0)
                                + ((j > 0 && _puddleTileBuf[idx - tilesW] > 0) ? 1 : 0)
                                + ((j < tilesH - 1 && _puddleTileBuf[idx + tilesW] > 0) ? 1 : 0);
                    if (buddies < (_puddleTileBuf[idx] == 2 ? 1 : 2))
                        continue;
                    bool[]? pbits = _tilePuddleBitsBuf![idx];
                    if (pbits == null)
                        continue;
                    anyPuddle = true;
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
            job.WaterAny = job.AnyWater || anyPuddle || job.AnyAnim || job.AnyLabeled;
            if (!job.WaterAny)
                return;

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
            // ANIMATED tiles: the march/edge copy swaps to the frame-CONSENSUS bits. Effects
            // (_waterPixBits) keep chasing the live surf frame; the waterline the reflections
            // and wading shadow anchor on is the average wash line, so they stop lurching a
            // whole wave-band on every frame flip.
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool[]? sb = _tileBitsStableBuf![idx];
                    if (sb == null)
                        continue;
                    bool isWater = _waterBoolBuf![idx];
                    bool refine = _tileRefineBuf![idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits2[row + px] = refine ? sb[arow + px] : (isWater || sb[arow + px]);
                    }
                }
            }
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
                var seen = new bool[count];   // job-local: _waterBool2Buf belongs to gather now
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
                    bool big = _tileBigSolidBuf![idx];
                    _bigCarveBuf[idx] = big;
                    bool landNear = i == 0 || i == tilesW - 1 || j == 0 || j == tilesH - 1
                        || !_waterBoolBuf![idx - 1] || !_waterBoolBuf[idx + 1]
                        || !_waterBoolBuf[idx - tilesW] || !_waterBoolBuf[idx + tilesW];
                    // A deck is walkable — land-connected by definition, no seed test needed.
                    _bigSeedBuf[idx] = big && (landNear || _tileDeckBuf![idx]);
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
                            int kidx = j * tilesW + k;
                            if (_tileHasBldBuf![kidx] || _tileHasFrontBuf![kidx])
                                _bigSeedBuf[kidx] = true;
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
                    bool[]? carveB = _tileCarveBBuf![idx];
                    bool[]? carveF = _tileCarveFBuf![idx];
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
                            if (carveB != null && carveB[arow + px]) _waterPixBits[row + px] = false;
                            if (carveF != null && carveF[arow + px]) _waterPixBits[row + px] = false;
                        }
                    }
                }
            }

            // Pass C2 — carve FURNITURE and BUILDING entity rects gathered on the main thread.
            foreach (var (wx0, wy0, wx1, wy1) in _carveRects)
            {
                int px0 = Math.Max(0, wx0 / 4 - job.Tx * Sub);
                int py0 = Math.Max(0, wy0 / 4 - job.Ty * Sub);
                int px1 = Math.Min(pw, wx1 / 4 - job.Tx * Sub);
                int py1 = Math.Min(ph, wy1 / 4 - job.Ty * Sub);
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

            // FLOWING water (class 10 — waterfalls/streams) and LAVA (class 11) both scrub from
            // the march channel so neither grows a sky mirror; their effect channel stays (flow
            // ripples; lava undulates slowly + self-glows, handled in the shader by the alpha tag).
            for (int j = 0; j < tilesH; j++)
                for (int i = 0; i < tilesW; i++)
                {
                    if (!_tileFlowBuf![j * tilesW + i] && !_tileLavaBuf![j * tilesW + i])
                        continue;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits2![row + px] = false;
                    }
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

            // WATER-BODY SIZE → calm factor. A tiny tide pool should barely ripple while an
            // ocean rolls; flood-fill the water TILES (4-connected) and scale each tile's effect
            // value by its body's tile count. Works for heuristic AND labelled water alike — the
            // game "knows it's small" from the connected area, not from colour or a special label.
            if (_tileWetFlag == null || _tileWetFlag.Length < count) _tileWetFlag = new bool[count];
            if (_tileCalmBuf == null || _tileCalmBuf.Length < count) _tileCalmBuf = new byte[count];
            for (int j = 0; j < tilesH; j++)
                for (int i = 0; i < tilesW; i++)
                {
                    bool wet = _waterBoolBuf![j * tilesW + i];
                    if (!wet)
                        for (int py = 0; py < Sub && !wet; py++)
                        {
                            int r = (j * Sub + py) * pw + i * Sub;
                            for (int px = 0; px < Sub; px++)
                                if (_waterPixBits![r + px]) { wet = true; break; }
                        }
                    _tileWetFlag[j * tilesW + i] = wet;
                }
            {
                Span<int> stack = count <= 4096 ? stackalloc int[Math.Min(count, 4096)] : new int[count];
                var seen = new bool[count];
                var member = new List<int>(64);
                for (int start = 0; start < count; start++)
                {
                    if (!_tileWetFlag[start] || seen[start])
                        continue;
                    int sp = 0; stack[sp++] = start; seen[start] = true; member.Clear();
                    while (sp > 0)
                    {
                        int cur = stack[--sp]; member.Add(cur);
                        int cx = cur % tilesW, cy = cur / tilesW;
                        if (cx > 0 && _tileWetFlag[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack[sp++] = cur - 1; }
                        if (cx < tilesW - 1 && _tileWetFlag[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack[sp++] = cur + 1; }
                        if (cy > 0 && _tileWetFlag[cur - tilesW] && !seen[cur - tilesW]) { seen[cur - tilesW] = true; stack[sp++] = cur - tilesW; }
                        if (cy < tilesH - 1 && _tileWetFlag[cur + tilesW] && !seen[cur + tilesW]) { seen[cur + tilesW] = true; stack[sp++] = cur + tilesW; }
                    }
                    // size → calm: <=3 tiles ~0.5 (a puddle), ramping to full by ~36 tiles (a pond+).
                    // Bodies cut by the mask edge are likely larger off-screen → treat as full.
                    bool touchesEdge = false;
                    foreach (int idx in member)
                    {
                        int cx = idx % tilesW, cy = idx / tilesW;
                        if (cx == 0 || cy == 0 || cx == tilesW - 1 || cy == tilesH - 1) { touchesEdge = true; break; }
                    }
                    float calm = touchesEdge ? 1f : MathHelper.Clamp(0.5f + (member.Count - 3) / 33f * 0.5f, 0.5f, 1f);
                    byte cb = (byte)MathHelper.Clamp(calm * 255f, 0f, 255f);
                    foreach (int idx in member)
                        _tileCalmBuf[idx] = cb;
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
                        // ±14 window / ±36 px clamp (was ±10/±24): diagonal tide banks still
                        // showed a 16px mirror staircase — the wider window folds a full
                        // tile-step into the smoothed line; the clamp still stops a different
                        // water body sharing the row from dragging the anchor.
                        int x0 = Math.Max(0, x - 14), x1 = Math.Min(pw - 1, x + 14);
                        int n = _edgeCnt![x1 + 1] - _edgeCnt[x0];
                        float ts = n > 0 ? (float)(_edgeSum[x1 + 1] - _edgeSum[x0]) / n : t0;
                        ts = MathHelper.Clamp(ts, t0 - 36, t0 + 36);
                        bch = (byte)MathHelper.Clamp((float)Math.Round((y - ts) * 2f), 0f, 252f);
                    }
                    // Shallow puddles get a SOFTER mask value: every effect (ripple, sparkle,
                    // mirror) scales with it, so a walk-through pool shimmers gently instead of
                    // sparkling like open water. Animated-art water (fountains) is softer still —
                    // a fountain basin should barely shimmer, not churn like a lake.
                    int tileIdx = (y / Sub) * tilesW + (x / Sub);
                    byte effV = !eff ? (byte)0
                        : _animSoftTileBuf![tileIdx] ? (byte)140
                        : _puddlePixBits![p] ? (byte)205 : (byte)255;
                    // Body-size calm: a small pool ripples/glints gentler than an open lake.
                    if (eff) effV = (byte)(effV * _tileCalmBuf![tileIdx] / 255);
                    // ALPHA tags the water TYPE for the shader: 0 = ICE (mirror, no ripple),
                    // 128 = LAVA (slow molten flow + self-glow, no mirror), 255 = normal water.
                    byte alpha = _tileIceBuf![tileIdx] ? (byte)0 : _tileLavaBuf![tileIdx] ? (byte)128 : (byte)255;
                    _waterPixBuf[p] = new Color(effV, march ? 255 : 0, bch, alpha);
                }
            }

            // TEMPORAL SMOOTHING of the effect channel (rule: everything fades, nothing pops).
            // The surf animation flips its art every ~150-250ms and the R channel snapped with
            // it — ripple/mirror at the tide line appeared and vanished a wave-band at a time.
            // Blend each rebuild's R toward the previous applied mask at the same WORLD pixel
            // (the window is tile-anchored; offset by the origin delta) so the wet edge SWEEPS
            // over ~2-3 rebuilds instead of snapping. G/B/A stay exact — the waterline anchor
            // and type must never lag.
            if (_prevEffBuf != null && _prevEffW == pw && _prevEffH == ph
                && ReferenceEquals(job.Loc, _prevEffLoc))
            {
                int dx = (job.Tx - _prevEffTx) * Sub;
                int dy = (job.Ty - _prevEffTy) * Sub;
                for (int y = 0; y < ph; y++)
                {
                    int py = y + dy;
                    if (py < 0 || py >= ph)
                        continue;
                    int rowBase = y * pw, prowBase = py * pw;
                    for (int x = 0; x < pw; x++)
                    {
                        int px = x + dx;
                        if (px < 0 || px >= pw)
                            continue;
                        int p = rowBase + x;
                        byte prev = _prevEffBuf[prowBase + px];
                        byte cur = _waterPixBuf[p].R;
                        if (prev == cur)
                            continue;
                        var c = _waterPixBuf[p];
                        c.R = (byte)((cur * 2 + prev * 3) / 5);   // ~0.4 step per rebuild
                        _waterPixBuf[p] = c;
                    }
                }
            }
            if (_prevEffBuf == null || _prevEffBuf.Length < pcount)
                _prevEffBuf = new byte[pcount];
            for (int p = 0; p < pcount; p++)
                _prevEffBuf[p] = _waterPixBuf[p].R;
            _prevEffTx = job.Tx; _prevEffTy = job.Ty; _prevEffW = pw; _prevEffH = ph;
            _prevEffLoc = job.Loc;
        }

        /// <summary>Apply stage - main thread: upload the composed buffers and publish the new
        /// mask identity. Until this runs, the shader keeps the OLD texture + OLD origin
        /// (a consistent pair — the mask content is world-anchored).</summary>
        private void ApplyWaterMask(WaterMaskJob job)
        {
            var prevLoc = _lastWaterLoc;
            int prevTx = _lastWaterTx, prevTy = _lastWaterTy;
            _lastWaterLoc = job.Loc;
            _lastWaterTx = job.Tx;
            _lastWaterTy = job.Ty;
            _lastWaterTick = Game1.ticks;
            _lastWaterHookVer = job.HookVer;
            _lastWaterLabelVer = job.LabelVer;
            _waterAny = job.WaterAny;
            if (!job.WaterAny)
                return;   // stage stays off; no texture upload needed

            int tilesW = job.TilesW, tilesH = job.TilesH;
            int count = tilesW * tilesH;
            int pw = tilesW * 16, ph = tilesH * 16;
            // GPU CROSSFADE, CONTINUOUS (rule: nothing pops, at any rebuild rate): the fade
            // SOURCE for each apply is a SNAPSHOT OF WHAT IS ON SCREEN RIGHT NOW —
            // lerp(prevShown, lastApplied, blend), world-aligned into the new window. The
            // first version used the last mask as the source, so when rebuilds arrived
            // faster than the fade finished (surf flips every ~0.2s vs 0.3s fade) every
            // apply first SNAPPED the picture to the target — the fast up/down the user
            // called "ขึ้นไวลงไว". Rebasing from the shown state makes the response a true
            // exponential glide toward whatever the mask is doing, however often it changes.
            int pcountA = pw * ph;
            bool canFade = _waterMask != null && _waterMask.Width == pw && _waterMask.Height == ph
                && ReferenceEquals(prevLoc, job.Loc)
                && _prevShownBuf != null && _lastAppliedBuf != null && _prevShownLen == pcountA;
            if (canFade)
            {
                if (_shownScratch == null || _shownScratch.Length < pcountA)
                    _shownScratch = new Color[pcountA];
                int dx = (job.Tx - prevTx) * 16, dy = (job.Ty - prevTy) * 16;
                // MUST match the eased value the shader displayed this frame, or the
                // snapshot itself introduces a tiny pop at every swap.
                float mb = _maskBlend * _maskBlend * (3f - 2f * _maskBlend);
                for (int y = 0; y < ph; y++)
                {
                    int oy = y + dy;
                    bool inY = oy >= 0 && oy < ph;
                    int row = y * pw, orow = oy * pw;
                    for (int x = 0; x < pw; x++)
                    {
                        int ox = x + dx;
                        if (inY && ox >= 0 && ox < pw)
                        {
                            Color a = _prevShownBuf![orow + ox], c = _lastAppliedBuf![orow + ox];
                            _shownScratch[row + x] = new Color(
                                (byte)(a.R + (c.R - a.R) * mb), (byte)(a.G + (c.G - a.G) * mb),
                                (byte)(a.B + (c.B - a.B) * mb), (byte)(a.A + (c.A - a.A) * mb));
                        }
                        else
                            _shownScratch[row + x] = _waterPixBuf[row + x];   // newly scrolled-in: no fade
                    }
                }
                (_prevShownBuf, _shownScratch) = (_shownScratch, _prevShownBuf);
                if (_waterMaskPrev == null || _waterMaskPrev.Width != pw || _waterMaskPrev.Height != ph)
                {
                    _waterMaskPrev?.Dispose();
                    _waterMaskPrev = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
                }
                _waterMaskPrev.SetData(_prevShownBuf, 0, pcountA);
                // prev is REBASED into the new window — same origin as current from here on
                _waterMaskPrevOrigin = new Vector2(job.Tx, job.Ty);
                _waterMaskPrevSize = new Vector2(tilesW, tilesH);
                _maskBlend = 0f;
            }
            else
            {
                _waterMaskPrev?.Dispose();
                _waterMaskPrev = null;
                _maskBlend = 1f;   // location/size change: no valid prev — show the new mask at once
                if (_prevShownBuf == null || _prevShownBuf.Length < pcountA)
                    _prevShownBuf = new Color[pcountA];
                Array.Copy(_waterPixBuf, _prevShownBuf, pcountA);
            }
            _prevShownLen = pcountA;
            if (_lastAppliedBuf == null || _lastAppliedBuf.Length < pcountA)
                _lastAppliedBuf = new Color[pcountA];
            Array.Copy(_waterPixBuf, _lastAppliedBuf, pcountA);
            if (_waterMask == null || _waterMask.Width != pw || _waterMask.Height != ph)
            {
                _waterMask?.Dispose();
                _waterMask = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _waterMask.SetData(_waterPixBuf, 0, pw * ph);
            if (_waterMaskCore == null || _waterMaskCore.Width != tilesW || _waterMaskCore.Height != tilesH)
            {
                _waterMaskCore?.Dispose();
                _waterMaskCore = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _waterMaskCore.SetData(_waterMaskCoreBuf, 0, count);
            _waterMaskSize = new Vector2(tilesW, tilesH);

            if (MaskView)
                BuildMaskViewTex(pw, ph);
        }

        // ---- live debug overlay: what the mask ACTUALLY covers, per pixel ----

        /// <summary>Toggled by the radiance_maskview console command.</summary>
        internal static bool MaskView;
        private Texture2D? _maskViewTex;
        private Color[]? _maskViewBuf;

        /// <summary>Readable recolor of the freshly composed mask (built only while the
        /// overlay is on): cyan = full water effect, orange = effect-only art water
        /// (fountains/puddles, softer), green = march (reflection) shoreline band.</summary>
        private void BuildMaskViewTex(int pw, int ph)
        {
            int pcount = pw * ph;
            if (_maskViewBuf == null || _maskViewBuf.Length < pcount)
                _maskViewBuf = new Color[pcount];
            for (int p = 0; p < pcount; p++)
            {
                Color m = _waterPixBuf![p];
                bool eff = m.R > 0, march = m.G > 0;
                _maskViewBuf[p] =
                    eff && march ? new Color(0, m.R, 255) :          // cyan: effect + reflection water
                    eff ? new Color(255, (byte)(m.R / 2), 0) :       // orange: effect-only (soft art water)
                    march ? new Color(0, 220, 60) :                  // green: march-only (rare)
                    Color.Transparent;
                // Bright rim right AT the smoothed waterline (edge distance ~0) — the anchor line.
                if (march && m.B <= 2)
                    _maskViewBuf[p] = new Color(120, 255, 120);
            }
            if (_maskViewTex == null || _maskViewTex.Width != pw || _maskViewTex.Height != ph)
            {
                _maskViewTex?.Dispose();
                _maskViewTex = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _maskViewTex.SetData(_maskViewBuf, 0, pcount);
        }

        /// <summary>Draw the overlay into the world batch (RenderedWorld space = world px minus viewport).</summary>
        public void DrawMaskOverlay(SpriteBatch b)
        {
            if (_maskViewTex == null || !_waterAny)
                return;
            var vp = Game1.viewport;
            var dest = new Rectangle(_lastWaterTx * 64 - vp.X, _lastWaterTy * 64 - vp.Y,
                _maskViewTex.Width * 4, _maskViewTex.Height * 4);
            b.Draw(_maskViewTex, dest, Color.White * 0.55f);
        }
    }
}
