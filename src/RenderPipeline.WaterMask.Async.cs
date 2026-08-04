using System;
using System.Collections.Generic;
using System.Linq;
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
            public int Tx, Ty, TilesW, TilesH, HookVer, LabelVer, Epoch;
            public bool AnyWater;              // gather: any true water tile
            public bool AnyLabeled;            // gather: any label-nominated water art
            public bool WaterAny;              // compose: final any-water verdict
            public double ComposeMs;           // worker-side timing (diag)
            public System.Threading.Tasks.Task? Task;
            public volatile bool Done;
            public volatile bool Failed;
            // P3a — location-wide waterline anchor (RenderPipeline.Waterline.cs):
            public bool AnchorOnly;            // full-map job: stop after Pass D, emit run lists
            public WaterlineAnchor? Anchor;    // window job: fresh anchor to override run tops with
            public int[]? AnchorColStart;      // AnchorOnly results (worker writes, main consumes)
            public short[]? AnchorTops;
            public short[]? AnchorBots;
        }

        private WaterMaskJob? _waterJob;
        private bool _loggedWaterJobFail;

        // ---- gathered per-tile inputs (main thread writes, worker reads) ----
        private bool[]?[]? _tileBitsBuf;       // effect-channel art classification per tile (null = none)
        private bool[]?[]? _tileKeepBuf;       // labelled water tile: pixels to KEEP in the effect channel (null = all)
        private bool[]?[]? _tileCarveBBuf;     // Buildings-layer opacity bits (null = no art)
        private bool[]?[]? _tileCarveFBuf;     // Front-layer opacity bits
        private bool[]? _tileBigSolidBuf;      // near-solid (>=230/256 opaque) Buildings/Front art
        private bool[]? _tileDeckBuf;          // Height Framework DECK tile
        private bool[]? _tileLabeledBuf;       // overlay art here is LABELLED liquid: resolved per pixel, skip the tile verdict
        private bool[]? _tileHasBldBuf;        // any Buildings art at all (arch fill test)
        private bool[]? _tileOverlayGroundBuf; // Buildings art over water that a label calls ALL ground
        private bool[]? _tileOverlayGroundFBuf;// same for Front + every AlwaysFront layer here
        private bool[]?[]? _tileIceBitsBuf;    // per-pixel ice from the label (null = use the tile verdict)
        private bool[]?[]? _tileLavaBitsBuf;   // per-pixel lava from the label
        private bool[]?[]? _tileFlowBitsBuf;   // per-pixel flowing (class 10) from the label
        private bool[]? _tileLandNearBuf;      // this water tile touches a non-water tile (or the mask edge)
        private bool[]? _tileHasFrontBuf;
        private bool[]? _tileIceBuf;           // HF label class 9: frozen — reflection, no ripple
        private bool[]? _tileFlowBuf;          // HF label class 10: flowing/waterfall — ripple, no reflection
        private bool[]? _tileLavaBuf;          // HF label class 11: lava — slow molten flow, self-glow, no reflection
        private bool[]? _tileWetFlag;          // per-tile: has any effect-water pixel (for body-size flood fill)
        private byte[]? _tileCalmBuf;          // per-tile 0..255 wave scale by water-body size (small = calmer)
        private readonly List<(int x0, int y0, int x1, int y1)> _carveRects = new();

        /// <summary>Label-set identity for the mask cache key (0 = no labels loaded). Labels are
        /// read once at startup, so this is constant for a session — it exists so a build with no
        /// labels can never reuse a cached mask built with them.</summary>
        private static int CurrentLabelVersion() => LabelStore.Instance?.Version ?? 0;

        /// <summary>Water bits from 256 HF Studio per-pixel labels. Classes 1 (water), 9 (ice) and
        /// 10 (flowing) are ALL water for the mask; per-class counts let the tile pick a behaviour
        /// (ice = mirror only, flowing = ripple only).</summary>
        /// <summary>
        /// Fill the holes a label carve punched in the march channel that are ENCLOSED by march
        /// water, and leave the ones that reach the outside carved.
        /// <para>
        /// Both shapes are "painted non-liquid inside a water tile", but they mean opposite things
        /// to the reflection. A pond's island or a lily pad is surrounded by water: if it survives
        /// as a hole, every column crossing it finds a false shoreline and re-anchors the mirror
        /// there. A fountain's stone lip, a bank ledge, a pier's footing all reach open land: those
        /// ARE the waterline, and treating them as water is what pushed the mirror out past the
        /// stone and left painted banks reading as water.
        /// </para>
        /// A four-way flood from the window border through everything that is not march water
        /// separates them in one O(n) sweep: whatever the flood reaches is connected to the
        /// outside and stays carved; the rest was enclosed and goes back to water. The border
        /// itself counts as outside, so a rim continuing past the window edge is still a rim.
        /// </summary>
        private void RestoreEnclosedMarch(bool[] march, int pw, int ph)
        {
            int n = pw * ph;
            if (_marchOutside == null || _marchOutside.Length < n)
                _marchOutside = new bool[n];
            var outside = _marchOutside;
            Array.Clear(outside, 0, n);
            if (_marchStack == null || _marchStack.Length < n)
                _marchStack = new int[n];
            var stack = _marchStack;
            int sp = 0;

            void Seed(int idx)
            {
                if (!march[idx] && !outside[idx]) { outside[idx] = true; stack[sp++] = idx; }
            }
            for (int x = 0; x < pw; x++) { Seed(x); Seed((ph - 1) * pw + x); }
            for (int y = 0; y < ph; y++) { Seed(y * pw); Seed(y * pw + pw - 1); }

            while (sp > 0)
            {
                int idx = stack[--sp];
                int x = idx % pw, y = idx / pw;
                if (x > 0) Seed(idx - 1);
                if (x < pw - 1) Seed(idx + 1);
                if (y > 0) Seed(idx - pw);
                if (y < ph - 1) Seed(idx + pw);
            }

            for (int i = 0; i < n; i++)
                if (!march[i] && !outside[i])
                    march[i] = true;
        }

        private bool[]? _marchOutside;
        private int[]? _marchStack;
        private bool[]? _speckSeen;
        private int[]? _speckMembers;

        /// <summary>Smallest connected march area that is real water. A wet-shading dash painted
        /// into shore art is a handful of texels; any actual body of water is hundreds.</summary>
        private const int MinMarchArea = 32;

        /// <summary>
        /// Drop march blobs too small to be water, by CONNECTED AREA rather than by how tall the
        /// blob happens to be in one column.
        /// <para>
        /// The rule here used to be "clear any column run shorter than six texels", aimed at the
        /// isolated wet-shading dashes in shore art, each of which otherwise became a tiny mirror
        /// sitting at distance zero and painted a dark dash onto the bank. It caught those, and it
        /// also caught every place real water tapers: the curved front rim of the town fountain,
        /// and the thin band of river that runs along the foot of a bank. Those are not specks -
        /// they are the edge of a body thousands of texels across, and the label says so.
        /// </para>
        /// Area answers what column height cannot. A blob touching the window border is kept
        /// whatever its size: it continues off-screen and its real extent is unknown.
        /// </summary>
        private void DropSpeckComponents(bool[] march, int pw, int ph)
        {
            int n = pw * ph;
            if (_speckSeen == null || _speckSeen.Length < n) _speckSeen = new bool[n];
            if (_marchStack == null || _marchStack.Length < n) _marchStack = new int[n];
            if (_speckMembers == null || _speckMembers.Length < n) _speckMembers = new int[n];
            var seen = _speckSeen;
            var stack = _marchStack;
            var members = _speckMembers;
            Array.Clear(seen, 0, n);

            for (int start = 0; start < n; start++)
            {
                if (!march[start] || seen[start])
                    continue;
                int sp = 0, count = 0;
                bool touchesBorder = false;
                seen[start] = true;
                stack[sp++] = start;
                while (sp > 0)
                {
                    int idx = stack[--sp];
                    members[count++] = idx;
                    int x = idx % pw, y = idx / pw;
                    if (x == 0 || y == 0 || x == pw - 1 || y == ph - 1)
                        touchesBorder = true;
                    if (x > 0 && march[idx - 1] && !seen[idx - 1]) { seen[idx - 1] = true; stack[sp++] = idx - 1; }
                    if (x < pw - 1 && march[idx + 1] && !seen[idx + 1]) { seen[idx + 1] = true; stack[sp++] = idx + 1; }
                    if (y > 0 && march[idx - pw] && !seen[idx - pw]) { seen[idx - pw] = true; stack[sp++] = idx - pw; }
                    if (y < ph - 1 && march[idx + pw] && !seen[idx + pw]) { seen[idx + pw] = true; stack[sp++] = idx + pw; }
                }
                if (!touchesBorder && count < MinMarchArea)
                    for (int k = 0; k < count; k++)
                        march[members[k]] = false;
            }
        }

        /// <summary>What the composed mask actually says for one tile, next to what the LABEL says
        /// for the art each layer draws there. The rule for this subsystem is that the game must
        /// match the labeler pixel for pixel, and until now there was no way to see the two side by
        /// side: a fix could be live and change nothing because the carve it feeds is only built
        /// from ONE layer family, and nothing said so.</summary>
        internal string DescribeTileMask(GameLocation? loc, int tx, int ty)
        {
            if (_waterPixBuf == null || _waterMask == null)
                return "[mask] no composed mask yet";
            const int Texels = 16;   // mask texels per tile (matches the compose pass)
            int px0 = (tx - _lastWaterTx) * Texels, py0 = (ty - _lastWaterTy) * Texels;
            int pw = _waterMask.Width;
            if (px0 < 0 || py0 < 0 || px0 + Texels > pw || py0 + Texels > _waterMask.Height)
                return $"[mask] tile ({tx},{ty}) is outside the mask window (origin {_lastWaterTx},{_lastWaterTy})";

            // COUNT is not enough: the shader ramps coverage down over the last texels of water
            // (edgeQ), so a band only a few texels wide can be fully inside the mask and still
            // render at a sixth of strength - which looks exactly like no coverage at all. Report
            // the strength as well as the count, so "not covered" and "covered but nearly
            // invisible" stop being the same reading.
            int eff = 0, march = 0, effSum = 0, effMin = 255, effMax = 0;
            var alphas = new Dictionary<byte, int>();
            for (int y = 0; y < Texels; y++)
                for (int x = 0; x < Texels; x++)
                {
                    Color c = _waterPixBuf[(py0 + y) * pw + px0 + x];
                    if (c.R > 0)
                    {
                        eff++; effSum += c.R;
                        if (c.R < effMin) effMin = c.R;
                        if (c.R > effMax) effMax = c.R;
                    }
                    if (c.G > 0) march++;
                    alphas[c.A] = alphas.TryGetValue(c.A, out int n) ? n + 1 : 1;
                }
            string strength = eff > 0 ? $" R avg={effSum / eff} min={effMin} max={effMax}" : "";
            string alphaTxt = string.Join(" ", alphas.OrderBy(kv => kv.Key)
                .Select(kv => $"{(kv.Key == 0 ? "ice" : kv.Key == 128 ? "lava" : kv.Key == 192 ? "flow" : kv.Key == 255 ? "water" : kv.Key.ToString())}:{kv.Value}"));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[mask] tile ({tx},{ty})  effect={eff}/256  march={march}/256{strength}  alpha[{alphaTxt}]");
            var labels = LabelStore.Instance;
            if (labels == null || !labels.Any)
                return sb.Append("[label] no label set loaded").ToString();
            foreach (string layerName in new[] { "Back", "Back2", "Buildings", "Buildings2", "Front", "Front2", "AlwaysFront" })
            {
                byte[]? lbl = labels.Get(loc, tx, ty, layerName);
                if (lbl == null)
                    continue;
                var hist = new Dictionary<byte, int>();
                foreach (byte c in lbl) hist[c] = hist.TryGetValue(c, out int n) ? n + 1 : 1;
                sb.AppendLine($"[label] {layerName,-11} " + string.Join(" ", hist.OrderBy(kv => kv.Key).Select(kv => $"{ClassName(kv.Key)}:{kv.Value}")));
            }
            return sb.ToString().TrimEnd();
        }

        private static string ClassName(byte c) => c switch
        {
            0 => "ground", 1 => "water", 2 => "wall", 3 => "roof", 4 => "deck", 5 => "void",
            6 => "emissive", 7 => "reflfloor", 8 => "mirror", 9 => "ice", 10 => "flow",
            11 => "lava", 12 => "window", 13 => "glass", 14 => "hot", 255 => "unset",
            _ => c.ToString(),
        };

        private static (bool[] bits, int nWater, int nIce, int nFlow, int nLava) WaterBitsFromLabels(byte[] classes)
        {
            var bits = new bool[256];
            int nW = 0, nI = 0, nF = 0, nL = 0;
            for (int p = 0; p < 256; p++)
            {
                byte c = classes[p];
                if (c == 1 || c == 14) { bits[p] = true; nW++; }   // 14 = hot spring: water, steam comes in v2
                else if (c == 9) { bits[p] = true; nI++; }
                else if (c == 10) { bits[p] = true; nF++; }
                else if (c == 11) { bits[p] = true; nL++; }   // lava: slow molten flow + self-glow
            }
            return (bits, nW, nI, nF, nL);
        }

        // ---- Pass F (SDF) buffers ----
        private byte[]? _waterSdfBuf;   // signed shore distance, 128 = waterline, ±4/texel
        private ushort[]? _sdfIn;       // chamfer scratch: distance to land (inside water)
        private ushort[]? _sdfOut;      // chamfer scratch: distance to water (outside)

        /// <summary>Two-pass 3-4 chamfer distance transform. d[p] ≈ 3 × (texel distance from
        /// the nearest texel where <paramref name="src"/> == <paramref name="seed"/>).
        /// Approximate (max ~8% error) but exact enough for a shoreline a few texels wide,
        /// and O(n) — the whole mask window costs well under a millisecond on the worker.</summary>
        private static void Chamfer34(bool[] src, bool seed, ushort[] d, int pw, int ph)
        {
            const int INF = 60000;
            int n = pw * ph;
            for (int p = 0; p < n; p++) d[p] = src[p] == seed ? (ushort)0 : (ushort)INF;
            for (int y = 0; y < ph; y++)
            {
                int row = y * pw;
                for (int x = 0; x < pw; x++)
                {
                    int p = row + x;
                    int v = d[p];
                    if (x > 0 && d[p - 1] + 3 < v) v = d[p - 1] + 3;
                    if (y > 0)
                    {
                        int up = p - pw;
                        if (d[up] + 3 < v) v = d[up] + 3;
                        if (x > 0 && d[up - 1] + 4 < v) v = d[up - 1] + 4;
                        if (x < pw - 1 && d[up + 1] + 4 < v) v = d[up + 1] + 4;
                    }
                    d[p] = (ushort)v;
                }
            }
            for (int y = ph - 1; y >= 0; y--)
            {
                int row = y * pw;
                for (int x = pw - 1; x >= 0; x--)
                {
                    int p = row + x;
                    int v = d[p];
                    if (x < pw - 1 && d[p + 1] + 3 < v) v = d[p + 1] + 3;
                    if (y < ph - 1)
                    {
                        int dn = p + pw;
                        if (d[dn] + 3 < v) v = d[dn] + 3;
                        if (x > 0 && d[dn - 1] + 4 < v) v = d[dn - 1] + 4;
                        if (x < pw - 1 && d[dn + 1] + 4 < v) v = d[dn + 1] + 4;
                    }
                    d[p] = (ushort)v;
                }
            }
        }

        /// <summary>Keep-mask for a label sitting on a tile the game already calls water: a pixel
        /// leaves the effect channel only when the author EXPLICITLY painted it something
        /// non-liquid. Unpainted (255) keeps the surface — a half-painted label (rock arc only,
        /// 217 such tiles in the shipped set) subtracts its rock and nothing else, instead of
        /// being thrown away by a liquid-count bar.</summary>
        private static bool[] KeepBitsFromLabels(byte[] classes)
        {
            var bits = new bool[256];
            for (int p = 0; p < 256; p++)
            {
                byte c = classes[p];
                bits[p] = c == 1 || c == 9 || c == 10 || c == 11 || c == 14 || c == 255;
            }
            return bits;
        }

        /// <summary>OR the label's ice (9) and lava (11) pixels into the tile's per-pixel sub-type
        /// masks, allocating only when there is something to record.</summary>
        private static void AddSubTypePixels(byte[] classes, ref bool[]? icePx, ref bool[]? lavaPx, ref bool[]? flowPx)
        {
            for (int p = 0; p < 256; p++)
            {
                byte c = classes[p];
                if (c == 9) (icePx ??= new bool[256])[p] = true;
                else if (c == 11) (lavaPx ??= new bool[256])[p] = true;
                else if (c == 10) (flowPx ??= new bool[256])[p] = true;
            }
        }

        /// <summary>True when a label EXISTS for this overlay tile and calls every pixel ground.
        /// Only meaningful over a water tile, where the art is the thing standing on the water.</summary>
        private static bool OverlayIsGround(LabelStore? labels, xTile.Layers.Layer? layer, int tx, int ty, bool isWater)
        {
            if (!isWater || labels == null || layer == null)
                return false;
            byte[]? l = labels.Get(layer, tx, ty);
            return l != null && CountLiquid(l) == 0;
        }

        /// <summary>How many of the 256 labels call this pixel liquid. Zero means the author
        /// deliberately said "all ground here", which is a fact, not the absence of one.</summary>
        private static int CountLiquid(byte[] classes)
        {
            int n = 0;
            for (int p = 0; p < 256; p++)
            {
                byte c = classes[p];
                if (c == 1 || c == 9 || c == 10 || c == 11 || c == 14) n++;
            }
            return n;
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
                LabelVer = CurrentLabelVersion(), Epoch = MaskEpoch,
                // Snapshot the location-wide waterline anchor if it is still valid for
                // exactly this identity — the worker reads it lock-free (immutable).
                Anchor = AnchorFresh(loc) ? _wlAnchor : null,
            };

            // The surface grid classifies the actual water SURFACE: ponds and beach tide pools
            // count as water (they reflect too), while pier/bridge DECKS over water do not — no
            // reflection is painted onto planks. Built once per location visit.
            var surf = SurfaceMap.For(loc);
            // Ground-truth labels ship WITH this mod (labels/), read once at startup — nothing
            // here touches the disk or depends on another mod being installed.
            var labels = LabelStore.Instance;
            if (labels is { Any: false }) labels = null;
            // The Desert never has waterTiles (the game excludes it by class in loadMap): its
            // pond is decorative art the game draws no overlay on, so nothing there is water,
            // whatever the tile properties say.
            bool desert = loc is StardewValley.Locations.Desert;
            // Fish ponds draw their own water in the sorted-sprite pass — never in waterTiles,
            // never a Back "Water" property. Their water is the interior of the footprint
            // (the 1-tile rim is masonry, per FishPond.isTileFishable).
            List<Rectangle>? pondRects = null;
            foreach (var b in loc.buildings)
            {
                if (b is StardewValley.Buildings.FishPond fp && fp.daysOfConstructionLeft.Value <= 0)
                    (pondRects ??= new()).Add(new Rectangle(
                        fp.tileX.Value + 1, fp.tileY.Value + 1,
                        Math.Max(0, fp.tilesWide.Value - 2), Math.Max(0, fp.tilesHigh.Value - 2)));
            }
            if (_waterBoolBuf == null || _waterBoolBuf.Length < count) _waterBoolBuf = new bool[count];
            bool any = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool water = !desert && (surf != null ? surf.IsWater(tx, ty) : loc.isWaterTile(tx, ty));
                    // Draw-call truth: the game DREW water here but the tile data doesn't know it
                    // (a location/mod with custom drawWater logic). Only when isWaterTile is false —
                    // isWaterTile-true tiles keep their pipeline above, so HF's deck-over-water veto
                    // is never overridden by the hook.
                    if (!water && !desert && !loc.isWaterTile(tx, ty) && WaterDrawHook.WasDrawn(loc, tx, ty))
                        water = true;
                    if (!water && pondRects != null)
                    {
                        foreach (var r in pondRects)
                            if (r.Contains(tx, ty)) { water = true; break; }
                    }
                    if (water) any = true;
                    _waterBoolBuf[j * tilesW + i] = water;
                }
            }
            job.AnyWater = any;

            // 1.6 maps can carry SEVERAL layers per family (Back2, Buildings3, Front-less
            // AlwaysFront4 ...), and Dynamic Reflections' issue tracker is full of maps whose
            // water art lives on Back2 (coral-reef beaches). Collect every RENDERED layer per
            // family: the family name plus a digits-only suffix — "Back-1" is the Tiled
            // convention for a DISABLED layer and must stay out.
            static bool IsFam(string id, string fam)
            {
                if (!id.StartsWith(fam, StringComparison.Ordinal))
                    return false;
                for (int k = fam.Length; k < id.Length; k++)
                    if (id[k] < '0' || id[k] > '9') return false;
                return true;
            }
            List<xTile.Layers.Layer>? backs = null, blds = null, always = null;
            List<xTile.Layers.Layer>? fronts = null;
            if (loc.map != null)
            {
                foreach (var l in loc.map.Layers)
                {
                    if (IsFam(l.Id, "AlwaysFront")) (always ??= new()).Add(l);
                    else if (IsFam(l.Id, "Back")) (backs ??= new()).Add(l);
                    else if (IsFam(l.Id, "Buildings")) (blds ??= new()).Add(l);
                    else if (IsFam(l.Id, "Front")) (fronts ??= new()).Add(l);
                }
            }
            var front = fronts is { Count: > 0 } ? fronts[0] : null;
            // Extra Front layers (Front2 ...) carve exactly like AlwaysFront: over-player art.
            if (fronts is { Count: > 1 })
                for (int k = 1; k < fronts.Count; k++)
                    (always ??= new()).Add(fronts[k]);

            if (_tileBitsBuf == null || _tileBitsBuf.Length < count) _tileBitsBuf = new bool[]?[count];
            if (_tileKeepBuf == null || _tileKeepBuf.Length < count) _tileKeepBuf = new bool[]?[count];
            if (_tileCarveBBuf == null || _tileCarveBBuf.Length < count) _tileCarveBBuf = new bool[]?[count];
            if (_tileCarveFBuf == null || _tileCarveFBuf.Length < count) _tileCarveFBuf = new bool[]?[count];
            if (_tileBigSolidBuf == null || _tileBigSolidBuf.Length < count) _tileBigSolidBuf = new bool[count];
            if (_tileDeckBuf == null || _tileDeckBuf.Length < count) _tileDeckBuf = new bool[count];
            if (_tileLabeledBuf == null || _tileLabeledBuf.Length < count) _tileLabeledBuf = new bool[count];
            if (_tileHasBldBuf == null || _tileHasBldBuf.Length < count) _tileHasBldBuf = new bool[count];
            if (_tileHasFrontBuf == null || _tileHasFrontBuf.Length < count) _tileHasFrontBuf = new bool[count];
            if (_tileOverlayGroundBuf == null || _tileOverlayGroundBuf.Length < count) _tileOverlayGroundBuf = new bool[count];
            if (_tileOverlayGroundFBuf == null || _tileOverlayGroundFBuf.Length < count) _tileOverlayGroundFBuf = new bool[count];
            if (_tileIceBitsBuf == null || _tileIceBitsBuf.Length < count) _tileIceBitsBuf = new bool[]?[count];
            if (_tileLavaBitsBuf == null || _tileLavaBitsBuf.Length < count) _tileLavaBitsBuf = new bool[]?[count];
            if (_tileFlowBitsBuf == null || _tileFlowBitsBuf.Length < count) _tileFlowBitsBuf = new bool[]?[count];
            if (_tileLandNearBuf == null || _tileLandNearBuf.Length < count) _tileLandNearBuf = new bool[count];
            if (_tileIceBuf == null || _tileIceBuf.Length < count) _tileIceBuf = new bool[count];
            if (_tileFlowBuf == null || _tileFlowBuf.Length < count) _tileFlowBuf = new bool[count];
            if (_tileLavaBuf == null || _tileLavaBuf.Length < count) _tileLavaBuf = new bool[count];

            // Volcano interiors hold lava, not water. The lava sub-class (slow molten flow,
            // self-glow, no mirror) otherwise only triggers on painted label class 11, which
            // ships dormant — so vanilla lava rendered as ordinary water, complete with a
            // mirror reflection. Tag it from the location instead so it reads as lava out of
            // the box; a painted label still wins per tile below.
            string locName = loc.NameOrUniqueName ?? loc.Name ?? "";
            bool locIsLava = loc is StardewValley.Locations.VolcanoDungeon
                || locName.Contains("Caldera", StringComparison.OrdinalIgnoreCase)
                || locName.Contains("Volcano", StringComparison.OrdinalIgnoreCase)
                // Mine floors 80-119: the game reuses the water overlay tinted Red*0.8 for lava
                // (decompiled MineShaft.loadLevel) — same machinery, molten look.
                || (loc is StardewValley.Locations.MineShaft ms && ms.getMineArea() == 80);

            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterBoolBuf[idx];
                    int tx = startTileX + i, ty = startTileY + j;
                    bool[]? bits = null;
                    int iceN = 0, flowN = 0, lavaN = 0;   // accumulated across Back + Buildings labels
                    // ---- GROUND-TRUTH LABELS FIRST (HF Studio). A labeled Back art is
                    // authoritative: its water pixels join the mask (STATIC painted pools on
                    // custom maps included — no ring or animation requirement), and a labeled
                    // art with no water pixels never reaches the color classifier at all.
                    // On a tile the game ALREADY calls water, Pass A fills all 256 pixels, so a
                    // label there cannot add coverage — it SUBTRACTS. `keep` is the set of pixels
                    // that stay in the effect channel; the rest is art sitting on the water (a
                    // pond's rock rim, the island in the middle, lily pads painted into the tile)
                    // and must not ripple. Sub-types read from the same label, which is what lets
                    // a real winter pond be marked ice at all.
                    bool[]? keep = null;
                    bool labeledBack = false;
                    // Per-PIXEL sub-type, collected from the same labels as the counts. The counts
                    // decide the tile's fallback; these decide each pixel, which is what a
                    // half-frozen tile needs: #1269 is 184 ice pixels and 72 water, and a whole-tile
                    // verdict froze all 256, so the ripple stopped dead on a tile boundary and the
                    // river showed square patches. Null when nothing here is labelled.
                    bool[]? icePx = null, lavaPx = null, flowPx = null;
                    if (labels != null && backs != null)
                    {
                        // Topmost Back-family label wins (Back2 draws over Back).
                        byte[]? lbl = null;
                        foreach (var bl in backs)
                        {
                            byte[]? l2 = labels.Get(bl, tx, ty);
                            if (l2 != null) lbl = l2;
                        }
                        if (lbl != null)
                        {
                            labeledBack = !isWater;
                            var (lb, nW, nI, nF, nL) = WaterBitsFromLabels(lbl);
                            if (nI > 0 || nL > 0 || nF > 0) AddSubTypePixels(lbl, ref icePx, ref lavaPx, ref flowPx);
                            if (isWater)
                            {
                                // Subtract only what the author explicitly painted non-liquid;
                                // unpainted pixels keep the surface, so a half-painted label can
                                // never erase a lake (the old >7-liquid guard is obsolete).
                                keep = KeepBitsFromLabels(lbl);
                                iceN += nI; flowN += nF; lavaN += nL;
                                job.AnyLabeled = true;
                            }
                            else if (nW + nI + nF + nL > 0)
                            {
                                bits = lb;
                                iceN += nI; flowN += nF; lavaN += nL;
                                job.AnyLabeled = true;
                            }
                        }
                    }
                    // V4: no colour classification, ever. Where the game says water and no label
                    // refines it, the tile fills whole — exactly the coverage vanilla's own overlay
                    // draws, which can never spill onto land the game didn't flood. Per-pixel
                    // truth comes from labels alone (97% of game-water art is labelled). The old
                    // surf/anim/puddle colour paths (WaterColor H2/H3, foam H4, PuddleBits H5) are
                    // gone: they are what put ripple on snow, sand and grass in every recolor.
                    // Buildings family: the first layer with art supplies the primary art
                    // (t1/s1 — the label-vs-opacity overrides below key off it); every further
                    // layer's opacity is UNIONED into the carve, and the topmost label wins.
                    bool hasBld = false;
                    Texture2D t1 = null!; Rectangle s1 = default;
                    (bool[] bits, int count) cbAcc = (null!, 0);
                    byte[]? bldLbl = null;
                    if (blds != null)
                    {
                        foreach (var bl in blds)
                        {
                            if (TryTileArt(bl, tx, ty, out var tb, out var sb, out _))
                            {
                                var solid = SolidBits(tb, sb);
                                if (!hasBld) { hasBld = true; t1 = tb; s1 = sb; cbAcc = solid; }
                                else if (solid.count > 0)
                                {
                                    var merged = new bool[256];
                                    for (int p = 0; p < 256; p++) merged[p] = (cbAcc.bits?[p] ?? false) || solid.bits[p];
                                    cbAcc = (merged, Math.Max(cbAcc.count, solid.count));
                                }
                            }
                            if (labels != null)
                            {
                                byte[]? l2 = labels.Get(bl, tx, ty);
                                if (l2 != null) bldLbl = l2;
                            }
                        }
                    }
                    // Buildings-layer overlay water: labels only (a labelled fountain rim /
                    // surf overlay needs no animation).
                    bool[]? overlayBits = null;
                    if (!isWater)
                    {
                        byte[]? lbl = bldLbl;
                        if (lbl != null)
                        {
                            var (ob, nW, nI, nF, nL) = WaterBitsFromLabels(lbl);
                            if (nW + nI + nF + nL >= 8)
                            {
                                overlayBits = ob;
                                iceN += nI; flowN += nF; lavaN += nL;
                                if (nI > 0 || nL > 0 || nF > 0) AddSubTypePixels(lbl, ref icePx, ref lavaPx, ref flowPx);
                                job.AnyLabeled = true;
                            }
                        }
                    }
                    if (overlayBits != null)
                    {
                        // Labelled overlay water is ground truth → full treatment (ripple +
                        // reflection). The colour-classified animated-art path is gone (V4).
                        if (bits == null) bits = overlayBits;
                        else
                        {
                            // OR-merge into a copy — `bits` may be a cached array.
                            var merged = new bool[256];
                            for (int p = 0; p < 256; p++) merged[p] = bits[p] || overlayBits[p];
                            bits = merged;
                        }
                    }
                    _tileBitsBuf[idx] = bits;

                    // Structure / carve inputs (Pass C + the land-connectivity test + arch fill).
                    bool bldLabeledLiquid = false;   // label says the overlay here IS water
                    bool frontLabeledLiquid = false;
                    bool hasFront = TryTileArt(front, tx, ty, out var t2, out var s2);
                    _tileHasBldBuf[idx] = hasBld;
                    _tileOverlayGroundBuf![idx] = false;   // buffers are reused frame to frame
                    _tileOverlayGroundFBuf![idx] = false;
                    var cb = cbAcc;   // union of every Buildings-family layer's opacity
                    // Ground-labelled overlay art is carved from its OPACITY, not from SolidBits'
                    // guess — see OpaqueBits. Snow-covered bush and ledge art on the front layers
                    // trips the same "mostly water-coloured → must be a wave overlay" bail as the
                    // bank ledge did (#31 is 131 water-coloured and carved 0, #32 194 and 0), which
                    // is why a snowy bush over the river came out rippling AND mirrored.
                    // `fCount` deliberately stays on SolidBits: it decides STRUCTURE, and handing a
                    // ledge its full opacity there would scrub whole tiles from the march and put
                    // the staircase back along the shoreline.
                    bool frontArt = false, frontAllGround = true;
                    bool[]? fBits = null;
                    int fCount = 0;
                    if (hasFront)
                    {
                        frontArt = true;
                        var cfSolid = SolidBits(t2, s2);
                        fCount = cfSolid.count;
                        bool g = OverlayIsGround(labels, front, tx, ty, isWater);
                        if (!g) frontAllGround = false;
                        fBits = g ? OpaqueBits(t2, s2).bits : cfSolid.bits;
                    }
                    // Fold every AlwaysFront layer's opacity into the Front carve channel.
                    if (always != null)
                        foreach (var l in always)
                            if (TryTileArt(l, tx, ty, out var t3, out var s3))
                            {
                                frontArt = true;
                                var ca = SolidBits(t3, s3);
                                bool g = OverlayIsGround(labels, l, tx, ty, isWater);
                                if (!g) frontAllGround = false;
                                var cbits = g ? OpaqueBits(t3, s3).bits : ca.bits;
                                int cn = g ? OpaqueBits(t3, s3).count : ca.count;
                                if (cn == 0)
                                    continue;
                                if (fBits == null) fBits = cbits;
                                else
                                {
                                    var merged = new bool[256];
                                    for (int p = 0; p < 256; p++) merged[p] = fBits[p] || cbits[p];
                                    fBits = merged;
                                }
                                fCount = Math.Max(fCount, ca.count);
                            }
                    // Only when EVERY overlay here is labelled ground: one unlabelled layer, or one
                    // that carries liquid, and the march keeps its say (a bridge on Front must still
                    // hang a reflection, and that is decided by the deck/structure path).
                    _tileOverlayGroundFBuf[idx] = frontArt && frontAllGround;
                    _tileHasFrontBuf[idx] = fBits != null;
                    _tileCarveBBuf[idx] = hasBld ? cb.bits : null;
                    _tileCarveFBuf[idx] = fBits;
                    // Buildings-layer art ON a water tile. Pass C already carves it by opacity,
                    // but SolidBits deliberately drops a tile whose opaque art is ≥60% water
                    // (else a wave-overlay or waterfall tile carves itself into a dead patch) —
                    // which is exactly the shape of a pond's rim tile: mostly water, one arc of
                    // rock. A LABEL resolves it without guessing: cut the pixels the art draws
                    // opaquely that the label does not call liquid, and leave everything else.
                    // GROUND-LABELLED overlay art on a water tile — the bank ledge case. SolidBits
                    // refuses to treat art as structure when ≥60% of its opaque pixels pass the
                    // colour test, which is right for a wave overlay and wrong for a SNOWY ledge:
                    // pale blue snow passes that test too, so the ledge carved NOTHING out of either
                    // channel and the mirror painted over the bank. Measured on the Town river:
                    // #211 189/220 water-coloured, #184 203/234, #897 236/253, all carving zero.
                    // A label that paints every pixel ground leaves nothing to guess, so take the
                    // opacity bits at face value. Per pixel, never as a whole tile: the ledge covers
                    // the top of the tile and the water below it must keep its mirror, and a
                    // whole-tile verdict is what puts a staircase along a shoreline.
                    if (isWater && hasBld)
                    {
                        byte[]? gl = bldLbl;
                        if (gl != null && CountLiquid(gl) == 0)
                        {
                            var ob2 = OpaqueBits(t1, s1);
                            if (ob2.count > 0)
                            {
                                _tileCarveBBuf[idx] = ob2.bits;
                                _tileOverlayGroundBuf![idx] = true;
                            }
                        }
                    }
                    if (isWater && hasBld && cb.bits != null && !_tileOverlayGroundBuf![idx])
                    {
                        byte[]? olbl = bldLbl;
                        if (olbl != null)
                        {
                            var (ob, oW, oI, oF, oL) = WaterBitsFromLabels(olbl);
                            var k = keep != null ? (bool[])keep.Clone() : null;
                            if (k == null)
                            {
                                k = new bool[256];
                                for (int p = 0; p < 256; p++) k[p] = true;
                            }
                            for (int p = 0; p < 256; p++)
                                if (cb.bits[p] && !ob[p]) k[p] = false;
                            keep = k;
                            iceN += oI; flowN += oF; lavaN += oL;
                            if (oI > 0 || oL > 0 || oF > 0) AddSubTypePixels(olbl, ref icePx, ref lavaPx, ref flowPx);
                            int oLiquid = oW + oI + oF + oL;
                            if (oLiquid > 0)
                            {
                                job.AnyLabeled = true;
                                // A label BEATS the art's opacity. A bridge's cast shadow is opaque
                                // overlay art drawn across the river, and Pass C would punch its
                                // exact rectangle out of the effect channel — but a shadow is still
                                // water and has to keep rippling. Where the label says liquid, take
                                // those pixels out of the carve (clone: SolidBits caches its array
                                // per art, so writing to it would poison every other tile using it),
                                // and stop a mostly-liquid tile counting as a solid structure.
                                var carve = (bool[])cb.bits.Clone();
                                for (int p = 0; p < 256; p++)
                                    if (ob[p]) carve[p] = false;
                                _tileCarveBBuf[idx] = carve;
                                // ANY painted liquid is enough. The old bar was half the tile, and
                                // half is not a fact about anything — a pier deck has ZERO liquid
                                // painted on it while a beach wave line has 94 of 256, so the two
                                // are never in danger of being confused. What the bar actually did
                                // was fail wave tiles by a few pixels (spring_beach#175 and #226 sit
                                // at 94, #158 squeaks through at 129) and hand them to the structure
                                // test, which erased them from the march a whole tile at a time.
                                // That is the staircase along every labelled shoreline.
                                bldLabeledLiquid = true;
                            }
                        }
                    }
                    // Same override for the FRONT / ALWAYSFRONT carve. Cast shadows and overhang art
                    // land there just as often as on Buildings, and a label saying "this is still
                    // water" has to beat opacity on every layer or the rule only half works.
                    if (isWater && labels != null && fBits != null)
                    {
                        // Topmost Front-family label wins (Front, Front2 ...; AlwaysFront folds
                        // into the same carve channel and its labels count the same way).
                        byte[]? flbl = null;
                        if (fronts != null)
                            foreach (var fl in fronts)
                            {
                                byte[]? l2 = labels.Get(fl, tx, ty);
                                if (l2 != null) flbl = l2;
                            }
                        if (always != null)
                            foreach (var al in always)
                            {
                                byte[]? l2 = labels.Get(al, tx, ty);
                                if (l2 != null) flbl = l2;
                            }
                        if (flbl != null)
                        {
                            var (fb2, fW, fI, fF, fL) = WaterBitsFromLabels(flbl);
                            if (fW + fI + fF + fL > 0)
                            {
                                var carveF = (bool[])fBits.Clone();
                                for (int p = 0; p < 256; p++)
                                    if (fb2[p]) carveF[p] = false;
                                _tileCarveFBuf[idx] = carveF;
                                iceN += fI; flowN += fF; lavaN += fL;
                                if (fI > 0 || fL > 0 || fF > 0) AddSubTypePixels(flbl, ref icePx, ref lavaPx, ref flowPx);
                                job.AnyLabeled = true;
                                fCount = 0;                 // labelled liquid is never a structure
                                frontLabeledLiquid = true;
                            }
                        }
                    }
                    // KEEP = the per-pixel carve, and it is the only thing that stops a water tile
                    // covering all 256 of its texels. It was read from the Back family alone, which
                    // silently does nothing wherever the liquid was painted on an overlay instead.
                    // Measured at the town fountain, tile (27,24): isWaterTile is FALSE and Back is
                    // plain Stone, but the FRONT label carries 59 water + 39 flow, and 98 liquid
                    // pixels clear SurfaceMap's overlay bar of 48 - so the tile is declared Water,
                    // the gather fills every texel, and with no Back label there is no carve to put
                    // any of it back. Label said 98, mask shipped 206.
                    //
                    // The rule this subsystem is held to is that the game matches the labeler pixel
                    // for pixel, so: if ANY layer painted this tile, the union of what those labels
                    // call liquid IS the water, and everything else is carved. Only a tile nobody
                    // painted falls back to the whole-tile flag.
                    if (isWater && labels != null)
                    {
                        bool[]? union = null;
                        void Union(xTile.Layers.Layer? layer)
                        {
                            byte[]? l = labels.Get(layer, tx, ty);
                            if (l == null)
                                return;
                            union ??= new bool[256];
                            for (int p = 0; p < 256; p++)
                            {
                                byte c = l[p];
                                if (c == 1 || c == 9 || c == 10 || c == 11 || c == 14 || c == 255)
                                    union[p] = true;
                            }
                        }
                        if (backs != null) foreach (var l in backs) Union(l);
                        if (blds != null) foreach (var l in blds) Union(l);
                        if (fronts != null) foreach (var l in fronts) Union(l);
                        if (always != null) foreach (var l in always) Union(l);
                        if (union != null)
                            keep = union;
                    }
                    _tileKeepBuf![idx] = keep;
                    _tileIceBitsBuf![idx] = icePx;
                    _tileLavaBitsBuf![idx] = lavaPx;
                    _tileFlowBitsBuf![idx] = flowPx;
                    // Ice / flowing win over each other by pixel count; a plain-water majority
                    // keeps normal behaviour. Ice → reflection but no ripple (mask alpha 0);
                    // flowing → ripple but no reflection (scrubbed from the march channel).
                    _tileIceBuf[idx] = iceN > 0 && iceN >= flowN && iceN >= lavaN;
                    _tileFlowBuf[idx] = flowN > 0 && flowN > iceN && flowN >= lavaN;
                    // A volcano location is lava unless a label says this tile is something else.
                    _tileLavaBuf[idx] = (lavaN > 0 && lavaN > iceN && lavaN > flowN)
                        || (locIsLava && iceN == 0 && flowN == 0);
                    // DECK tiles (walkable piers / plank bridges) block as whole tiles too: the
                    // beach plank's art has a painted wet stain that classified as water, punching
                    // a 2-texel channel through the deck — and the ±10 shoreline smoothing then
                    // dragged the anchors of a full tile around it up above the plank (reflection
                    // missing on that side).
                    bool deck = surf != null && surf.GetSurface(tx, ty) == SurfaceClass.Deck;
                    _tileDeckBuf[idx] = deck;
                    _tileBigSolidBuf[idx] = deck || (hasBld && cb.count >= 230 && !bldLabeledLiquid) || fCount >= 230;
                    // A tile whose overlay art is LABELLED liquid has already been resolved per
                    // pixel above: the carve keeps exactly the painted liquid and cuts exactly the
                    // rest. Pass C's whole-tile march scrub must not run on top of that, or the
                    // pixel-accurate waterline we just built is thrown away and the anchor snaps
                    // back to the tile grid. Unlabelled tiles keep the tile-level verdict, so maps
                    // nobody has painted behave exactly as before.
                    _tileLabeledBuf[idx] = bldLabeledLiquid || frontLabeledLiquid;
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
            // (The upload buffer is Pass E's output — a full-map ANCHOR job never gets there,
            // so don't inflate a map-sized Color[] it will never touch.)
            if (!job.AnchorOnly && (_waterPixBuf == null || _waterPixBuf.Length < pcount)) _waterPixBuf = new Color[pcount];
            if (_waterPixBits == null || _waterPixBits.Length < pcount) _waterPixBits = new bool[pcount];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterBoolBuf![idx];
                    bool[]? bits = _tileBitsBuf![idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits[row + px] = isWater || (bits != null && bits[arow + px]);
                    }
                }
            }

            job.WaterAny = job.AnyWater || job.AnyLabeled;
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
            // Label subtraction on true water tiles. It runs on BOTH channels now.
            //
            // It used to touch the effect channel only, on the grounds that "an island in mid-pond
            // must not read as a shoreline, or every reflection in the pond re-anchors on it".
            // The island part is right, but the rule was applied to every carved pixel, so the
            // march channel — the authority on where the waterline is — never saw a single label.
            // A tile the game calls water stayed water edge to edge there, and the reflection
            // geometry anchored on the TILE boundary instead of on the painted edge: the town
            // fountain mirrored a sheet out past its stone lip, and a labelled bank read as
            // water short of where the paint says the water stops.
            //
            // Islands and rims are not the same shape, and connectivity tells them apart without
            // guessing: an island is carved water enclosed by water, a rim is carved water that
            // reaches the outside. So carve march as well, then restore only the enclosed parts.
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    bool[]? keep = _tileKeepBuf![j * tilesW + i];
                    if (keep == null)
                        continue;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            if (!keep[arow + px])
                            {
                                _waterPixBits[row + px] = false;
                                _waterPixBits2[row + px] = false;
                            }
                    }
                }
            }
            RestoreEnclosedMarch(_waterPixBits2, pw, ph);
            // (V4) The anim-region shape test and its waterfall scrub are gone with the colour
            // classifier that fed them: vertical waterfall faces are label class 10's job now
            // (the whole-tile flow/lava march scrub below still runs on labelled tiles).
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
                    _tileLandNearBuf![idx] = landNear;
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
                    // A walk-on DECK breaks the march as a whole tile even when its art carries
                    // painted liquid: a bridge is a horizontal structure and the reflection below
                    // it must hang from its base as ONE line — per-plank carving gives every
                    // column its own edge and the mirror under a fence bridge reads as stripes.
                    // (This deck exception is also what keeps a labelled bridge anchoring at all:
                    // skipping the scrub outright let the march run straight through the bridge
                    // and its reflection vanished.) Other labelled tiles are carved per pixel
                    // below instead of scrubbed whole.
                    bool structTile = _bigSeedBuf[idx] && (_tileDeckBuf![idx] || !_tileLabeledBuf![idx]);
                    bool pixelCarveMarch = _tileLabeledBuf![idx] && !structTile && _bigSeedBuf[idx];
                    // Ground-labelled overlay art breaks the march at its own outline, but only where
                    // the tile touches land. That is the difference between a BANK — whose top edge is
                    // the real waterline, and the reflection has to start below it — and an ISLAND in
                    // mid-pond, which must stay invisible to the march or every reflection in the body
                    // re-anchors on it. Same land-connectivity question the structure test already
                    // asks, answered from the label instead of from opacity.
                    bool groundOverlayMarch = _tileOverlayGroundBuf![idx] && _tileLandNearBuf![idx];
                    bool groundFrontMarch = _tileOverlayGroundFBuf![idx] && _tileLandNearBuf![idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                        {
                            if (structTile)
                                _waterPixBits2![row + px] = false;
                            if (carveB != null && carveB[arow + px])
                            {
                                _waterPixBits[row + px] = false;
                                // Labelled structure art breaks the march at its PAINTED shape
                                // (the carve already had the label's liquid pixels removed), so a
                                // rock rim hangs its reflection from its own outline instead of
                                // either a whole-tile hole or nothing.
                                if (pixelCarveMarch || groundOverlayMarch) _waterPixBits2![row + px] = false;
                            }
                            if (carveF != null && carveF[arow + px])
                            {
                                _waterPixBits[row + px] = false;
                                if (pixelCarveMarch || groundFrontMarch) _waterPixBits2![row + px] = false;
                            }
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
            //
            // PER PIXEL where the label said so. This used to clear all 256 texels of any tile
            // whose flow or lava pixels won the count, which is the same whole-tile verdict that
            // froze half-iced river tiles before per-pixel ice landed. A fountain is the clear
            // case: its jets and its pool share tiles, so the tile voted "flowing" and the POOL
            // lost its reflection too. Ice and lava already carry per-pixel masks; flowing now
            // does as well, so the falling face stays unmirrored and the water beside it does not.
            for (int j = 0; j < tilesH; j++)
                for (int i = 0; i < tilesW; i++)
                {
                    int ti = j * tilesW + i;
                    bool[]? flowB = _tileFlowBitsBuf![ti], lavaB = _tileLavaBitsBuf![ti];
                    bool wholeTile = flowB == null && lavaB == null
                                  && (_tileFlowBuf![ti] || _tileLavaBuf![ti]);
                    if (!wholeTile && flowB == null && lavaB == null)
                        continue;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            if (wholeTile
                                || (flowB != null && flowB[arow + px])
                                || (lavaB != null && lavaB[arow + px]))
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
            DropSpeckComponents(_waterPixBits2!, pw, ph);
            for (int x = 0; x < pw; x++)
            {
                int top = -1;
                for (int y = 0; y <= ph; y++)
                {
                    int p = y * pw + x;
                    if (y < ph && _waterPixBits2![p]) { if (top < 0) top = y; _edgeBuf[p] = (short)top; }
                    else top = -1;
                }
            }

            // P3a — a FULL-MAP anchor job stops here: all it wanted was the speck-dropped
            // march bits. Emit the compact per-column run list and skip E/F entirely.
            if (job.AnchorOnly)
            {
                ExtractAnchorRuns(job, pw, ph);
                return;
            }
            // Window job with a valid location-wide anchor: re-base every run top on the
            // TRUE shoreline. Tops above the window come out negative — Pass E's depth
            // encode keeps counting from the real shore instead of the window edge.
            if (job.Anchor != null)
                OverrideEdgeFromAnchor(job, pw, ph);

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
                        int x0 = Math.Max(0, x - 10), x1 = Math.Min(pw - 1, x + 10);
                        int n = _edgeCnt![x1 + 1] - _edgeCnt[x0];
                        float ts = n > 0 ? (float)(_edgeSum[x1 + 1] - _edgeSum[x0]) / n : t0;
                        ts = MathHelper.Clamp(ts, t0 - 24, t0 + 24);
                        // 2 units per texel saturated at 126 texels, under 8 tiles, so every surface wider than
                        // that had no usable depth past its first few tiles. Half a unit reaches ~31.
                        bch = (byte)MathHelper.Clamp((float)Math.Round((y - ts) * 0.5f), 0f, 252f);
                    }
                    int tileIdx = (y / Sub) * tilesW + (x / Sub);
                    byte effV = eff ? (byte)255 : (byte)0;
                    // Body-size calm: a small pool ripples/glints gentler than an open lake.
                    if (eff) effV = (byte)(effV * _tileCalmBuf![tileIdx] / 255);
                    // ALPHA tags the water TYPE for the shader: 0 = ICE (mirror, no ripple),
                    // 128 = LAVA (slow molten flow + self-glow, no mirror), 255 = normal water.
                    // PER PIXEL where a label said so, falling back to the tile verdict for art
                    // nobody has painted: the type used to be a whole-tile answer, so a tile painted
                    // 184 ice / 72 water froze all 256 and the river wore square patches wherever
                    // the ice met the water. The label knows which pixels are frozen; ask it.
                    int lp = (y % Sub) * Sub + (x % Sub);
                    bool[]? iceB = _tileIceBitsBuf![tileIdx], lavaB = _tileLavaBitsBuf![tileIdx];
                    // Type ladder in ALPHA: 0 ice · 128 lava · 192 FLOWING · 255 plain water.
                    // 192 is new (the long-parked L4 flow tag): the entity mirror needs to tell a
                    // wet-fringe pixel (mirror a body there) from a waterfall face (never), and
                    // both used to ship as 255. 192 still passes the shader's step(0.75) wet-rim
                    // gate — a waterfall's plunge shore does glisten — and stays clear of the
                    // 0.9 plain-water gate the entity layer reads.
                    // Flowing reads per pixel too now, for the same reason ice does: a fountain
                    // tile holding both a jet and open pool used to tag all 256 texels 192, so
                    // the entity mirror refused a body standing in the pool.
                    bool[]? flowB = _tileFlowBitsBuf![tileIdx];
                    byte flowA = (flowB != null ? flowB[lp] : _tileFlowBuf![tileIdx]) ? (byte)192 : (byte)255;
                    byte alpha = iceB != null || lavaB != null
                        ? (iceB != null && iceB[lp] ? (byte)0 : lavaB != null && lavaB[lp] ? (byte)128 : flowA)
                        : _tileIceBuf![tileIdx] ? (byte)0 : _tileLavaBuf![tileIdx] ? (byte)128 : flowA;
                    _waterPixBuf![p] = new Color(effV, march ? 255 : 0, bch, alpha);
                }
            }

            // ---- Pass F — signed distance to the effect shoreline (3-4 chamfer, 1/3-texel
            // units). One field feeds the shader's quantized edge, the foam band and the wet
            // ground rim; encoded 128 + texels*4 → ±31.75 texels (~±2 tiles) of usable range,
            // which is more than any of its consumers ever look at.
            if (_waterSdfBuf == null || _waterSdfBuf.Length < pcount) _waterSdfBuf = new byte[pcount];
            if (_sdfIn == null || _sdfIn.Length < pcount) _sdfIn = new ushort[pcount];
            if (_sdfOut == null || _sdfOut.Length < pcount) _sdfOut = new ushort[pcount];
            Chamfer34(_waterPixBits!, true, _sdfOut, pw, ph);    // distance TO water (outside px)
            Chamfer34(_waterPixBits!, false, _sdfIn, pw, ph);    // distance TO land (inside px)
            for (int p = 0; p < pcount; p++)
            {
                float texels = _waterPixBits![p] ? _sdfIn[p] / 3f : -(_sdfOut[p] / 3f);
                _waterSdfBuf[p] = (byte)MathHelper.Clamp(128f + texels * 4f, 0f, 255f);
            }
        }

        /// <summary>Apply stage - main thread: upload the composed buffers and publish the new
        /// mask identity. Until this runs, the shader keeps the OLD texture + OLD origin
        /// (a consistent pair — the mask content is world-anchored).</summary>
        private void ApplyWaterMask(WaterMaskJob job)
        {
            _lastWaterLoc = job.Loc;
            _lastWaterTx = job.Tx;
            _lastWaterTy = job.Ty;
            _lastWaterTick = Game1.ticks;
            _lastWaterHookVer = job.HookVer;
            _lastWaterLabelVer = job.LabelVer;
            _lastWaterEpoch = job.Epoch;
            _waterAny = job.WaterAny;
            if (!job.WaterAny)
            {
                // The ORIGIN has just moved to this window, so the texture has to move with it.
                // Skipping the upload here used to be safe only because the stage was skipped
                // whenever this flag was false; now the stage belongs to the location, so a mask
                // left holding the last window that DID have water gets sampled at the new
                // window's world coordinates - the last window's water pattern reappearing in
                // blocks on dry sand. A mask must always agree with its own origin.
                ClearWaterMask(job);
                return;
            }

            int tilesW = job.TilesW, tilesH = job.TilesH;
            int count = tilesW * tilesH;
            int pw = tilesW * 16, ph = tilesH * 16;
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
            if (_waterSdf == null || _waterSdf.Width != pw || _waterSdf.Height != ph)
            {
                _waterSdf?.Dispose();
                _waterSdf = new Texture2D(_device, pw, ph, false, SurfaceFormat.Alpha8);
            }
            _waterSdf.SetData(_waterSdfBuf, 0, pw * ph);
            _waterMaskSize = new Vector2(tilesW, tilesH);

            if (MaskView)
                BuildMaskViewTex(pw, ph);
        }

        /// <summary>Publish an EMPTY mask for a window with no water, sized and anchored like any
        /// other, so the shader reads "no water here" instead of the previous window's pattern.
        /// All three textures are cleared together: R and G decide coverage, and the SDF's 128 is
        /// its zero, so leaving a stale distance field behind would still shade a phantom shore.</summary>
        private void ClearWaterMask(WaterMaskJob job)
        {
            int tilesW = job.TilesW, tilesH = job.TilesH;
            int count = tilesW * tilesH;
            int pw = tilesW * 16, ph = tilesH * 16;
            int pcount = pw * ph;

            if (_waterPixBuf == null || _waterPixBuf.Length < pcount) _waterPixBuf = new Color[pcount];
            if (_waterMaskCoreBuf == null || _waterMaskCoreBuf.Length < count) _waterMaskCoreBuf = new Color[count];
            if (_waterSdfBuf == null || _waterSdfBuf.Length < pcount) _waterSdfBuf = new byte[pcount];
            Array.Clear(_waterPixBuf, 0, pcount);
            Array.Clear(_waterMaskCoreBuf, 0, count);
            for (int p = 0; p < pcount; p++) _waterSdfBuf[p] = 128;   // 128 = exactly on the waterline

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
            if (_waterSdf == null || _waterSdf.Width != pw || _waterSdf.Height != ph)
            {
                _waterSdf?.Dispose();
                _waterSdf = new Texture2D(_device, pw, ph, false, SurfaceFormat.Alpha8);
            }
            _waterSdf.SetData(_waterSdfBuf, 0, pcount);
            _waterMaskSize = new Vector2(tilesW, tilesH);
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
