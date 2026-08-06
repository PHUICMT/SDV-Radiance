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
            public GameLocation Location = null!;
            public int StartTileX, StartTileY, TileWidth, TileHeight, WaterDrawHookVersion, LabelVersion, Epoch;
            public bool AnyWater;              // gather: any true water tile
            public bool AnyLabeled;            // gather: any label-nominated water art
            public bool WaterAny;              // compose: final any-water verdict
            public double ComposeDurationMilliseconds;           // worker-side timing (diag)
            public System.Threading.Tasks.Task? Task;
            public volatile bool Done;
            public volatile bool Failed;
            // P3a — location-wide waterline anchor (RenderPipeline.Waterline.cs):
            public bool AnchorOnly;            // full-map job: stop after Pass D, emit run lists
            public WaterlineAnchor? Anchor;    // window job: fresh anchor to override run tops with
            public int[]? AnchorColumnRunStartIndices;      // AnchorOnly results (worker writes, main consumes)
            public short[]? AnchorRunTopRows;
            public short[]? AnchorRunBottomRows;
        }

        private WaterMaskJob? _pendingWaterMaskJob;
        private bool _waterMaskJobFailureLogged;

        // ---- gathered per-tile inputs (main thread writes, worker reads) ----
        private bool[]?[]? _tileEffectBits;       // effect-channel art classification per tile (null = none)
        private bool[]?[]? _tileWaterKeepBits;       // labelled water tile: pixels to KEEP in the effect channel (null = all)
        private bool[]?[]? _tileBuildingCarveBits;     // Buildings-layer opacity bits (null = no art)
        private bool[]?[]? _tileFrontCarveBits;     // Front-layer opacity bits
        private bool[]? _tileLargeSolidFlags;      // near-solid (>=230/256 opaque) Buildings/Front art
        private bool[]? _tileDeckFlags;          // Height Framework DECK tile
        private bool[]? _tileLabeledLiquidFlags;       // overlay art here is LABELLED liquid: resolved per pixel, skip the tile verdict
        private bool[]? _tileHasBuildingArtFlags;        // any Buildings art at all (arch fill test)
        private bool[]? _tileBuildingGroundOverlayFlags; // Buildings art over water that a label calls ALL ground
        private bool[]? _tileFrontGroundOverlayFlags;// same for Front + every AlwaysFront layer here
        private bool[]?[]? _tileIceBits;    // per-pixel ice from the label (null = use the tile verdict)
        private bool[]?[]? _tileLavaBits;   // per-pixel lava from the label
        private bool[]?[]? _tileFlowBits;   // per-pixel flowing (class 10) from the label
        private bool[]? _tileNearLandFlags;      // this water tile touches a non-water tile (or the mask edge)
        private bool[]? _tileHasFrontArtFlags;
        private bool[]? _tileIceFlags;           // HF label class 9: frozen — reflection, no ripple
        private bool[]? _tileFlowFlags;          // HF label class 10: flowing/waterfall — ripple, no reflection
        private bool[]? _tileLavaFlags;          // HF label class 11: lava — slow molten flow, self-glow, no reflection
        private bool[]? _tileHasEffectWaterFlags;          // per-tile: has any effect-water pixel (for body-size flood fill)
        private byte[]? _tileCalmnessValues;          // per-tile 0..255 wave scale by water-body size (small = calmer)
        private readonly List<(int x0, int y0, int x1, int y1)> _entityCarveWorldRectangles = new();

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
            if (_marchOutsideFlags == null || _marchOutsideFlags.Length < n)
                _marchOutsideFlags = new bool[n];
            var outside = _marchOutsideFlags;
            Array.Clear(outside, 0, n);
            if (_marchFloodStack == null || _marchFloodStack.Length < n)
                _marchFloodStack = new int[n];
            var stack = _marchFloodStack;
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

        private bool[]? _marchOutsideFlags;
        private int[]? _marchFloodStack;
        private bool[]? _speckVisitedFlags;
        private int[]? _speckComponentMembers;

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
            if (_speckVisitedFlags == null || _speckVisitedFlags.Length < n) _speckVisitedFlags = new bool[n];
            if (_marchFloodStack == null || _marchFloodStack.Length < n) _marchFloodStack = new int[n];
            if (_speckComponentMembers == null || _speckComponentMembers.Length < n) _speckComponentMembers = new int[n];
            var seen = _speckVisitedFlags;
            var stack = _marchFloodStack;
            var members = _speckComponentMembers;
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
        internal string DescribeTileMask(GameLocation? location, int tx, int ty)
        {
            Color[]? maskPixels = MaskPixelsForInspection();
            if (maskPixels == null || _waterMask == null)
                return "[mask] no composed mask yet";
            const int Texels = 16;   // mask texels per tile (matches the compose pass)
            int px0 = (tx - _lastWaterTileX) * Texels, py0 = (ty - _lastWaterTileY) * Texels;
            int pw = _waterMask.Width;
            if (px0 < 0 || py0 < 0 || px0 + Texels > pw || py0 + Texels > _waterMask.Height)
                return $"[mask] tile ({tx},{ty}) is outside the mask window (origin {_lastWaterTileX},{_lastWaterTileY})";

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
                    Color c = maskPixels[(py0 + y) * pw + px0 + x];
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

            var report = new System.Text.StringBuilder();
            report.AppendLine($"[mask] tile ({tx},{ty})  effect={eff}/256  march={march}/256{strength}  alpha[{alphaTxt}]");
            // Compose verdicts for this tile from the LAST window job's scratch — the inputs
            // Pass C weighs when it decides whether the march (reflection) channel survives
            // here. structTile true = the whole tile was scrubbed from the march.
            int tilesWInWindow = pw / Texels;
            int tIdx = (ty - _lastWaterTileY) * tilesWInWindow + (tx - _lastWaterTileX);
            if (_tileLandConnectedFlags != null && tIdx >= 0 && tIdx < _tileLandConnectedFlags.Length)
            {
                bool landConnected = _tileLandConnectedFlags[tIdx];
                bool deck = _tileDeckFlags != null && _tileDeckFlags[tIdx];
                bool labeledLiquid = _tileLabeledLiquidFlags != null && _tileLabeledLiquidFlags[tIdx];
                bool structTile = landConnected && (deck || !labeledLiquid);
                bool[]? keepBits = _tileWaterKeepBits?[tIdx];
                bool[]? artBits = _tileEffectBits?[tIdx];
                report.AppendLine(
                    $"[compose] gameWater={(_waterTileFlags != null && _waterTileFlags[tIdx])} structTile={structTile}"
                    + $" (deck={deck} largeSolid={_tileLargeSolidFlags?[tIdx]} landConnected={landConnected} labeledLiquid={labeledLiquid})"
                    + $" nearLand={_tileNearLandFlags?[tIdx]} bldArt={_tileHasBuildingArtFlags?[tIdx]} frontArt={_tileHasFrontArtFlags?[tIdx]}"
                    + $" bldGroundOverlay={_tileBuildingGroundOverlayFlags?[tIdx]} frontGroundOverlay={_tileFrontGroundOverlayFlags?[tIdx]}"
                    + $" ice={_tileIceFlags?[tIdx]} flow={_tileFlowFlags?[tIdx]} lava={_tileLavaFlags?[tIdx]}"
                    + $" keep={(keepBits == null ? "-" : keepBits.Count(b => b).ToString())}/256"
                    + $" artBits={(artBits == null ? "-" : artBits.Count(b => b).ToString())}/256");
            }
            var labels = LabelStore.Instance;
            if (labels == null || !labels.Any)
                return report.Append("[label] no label set loaded").ToString();
            foreach (string layerName in new[] { "Back", "Back2", "Buildings", "Buildings2", "Front", "Front2", "AlwaysFront" })
            {
                byte[]? lbl = labels.Get(location, tx, ty, layerName);
                if (lbl == null)
                    continue;
                var hist = new Dictionary<byte, int>();
                foreach (byte c in lbl) hist[c] = hist.TryGetValue(c, out int n) ? n + 1 : 1;
                report.AppendLine($"[label] {layerName,-11} " + string.Join(" ", hist.OrderBy(kv => kv.Key).Select(kv => $"{ClassName(kv.Key)}:{kv.Value}")));
            }
            return report.ToString().TrimEnd();
        }

        /// <summary>Tiles in the current mask window whose EFFECT pixels lack MARCH (ripple
        /// without reflection) — the tiles the water overlay paints orange, with their
        /// coordinates, so a probe no longer has to guess which tile a dead strip is in.</summary>
        internal string DescribeEffectOnlyTiles(int worstToList = 16)
        {
            Color[]? maskPixels = MaskPixelsForInspection();
            if (maskPixels == null || _waterMask == null)
                return "[march] no composed mask yet";
            const int Texels = 16;
            int pw = _waterMask.Width, ph = _waterMask.Height;
            int tilesW = pw / Texels, tilesH = ph / Texels;
            var tiles = new List<(int tx, int ty, int orange, int eff)>();
            for (int j = 0; j < tilesH; j++)
                for (int i = 0; i < tilesW; i++)
                {
                    int orange = 0, eff = 0;
                    for (int y = 0; y < Texels; y++)
                    {
                        int row = (j * Texels + y) * pw + i * Texels;
                        for (int x = 0; x < Texels; x++)
                        {
                            Color c = maskPixels[row + x];
                            if (c.R > 0) { eff++; if (c.G == 0) orange++; }
                        }
                    }
                    if (orange > 0)
                        tiles.Add((_lastWaterTileX + i, _lastWaterTileY + j, orange, eff));
                }
            if (tiles.Count == 0)
                return "[march] every effect pixel in the window also has march (no orange)";
            long total = 0;
            foreach (var t in tiles) total += t.orange;
            var builderReport = new System.Text.StringBuilder();
            builderReport.AppendLine($"[march] {tiles.Count} tiles carry effect-without-march pixels ({total} px total) — worst first, probe with radiance_tile x y:");
            foreach (var t in tiles.OrderByDescending(t => t.orange).Take(worstToList))
                builderReport.AppendLine($"  tile ({t.tx},{t.ty})  orange={t.orange}/256  effect={t.eff}/256");
            return builderReport.ToString().TrimEnd();
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
        private byte[]? _waterSignedDistancePixels;   // signed shore distance, 128 = waterline, ±4/texel
        private ushort[]? _distanceToLandScratch;       // chamfer scratch: distance to land (inside water)
        private ushort[]? _distanceToWaterScratch;      // chamfer scratch: distance to water (outside)

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

        /// <summary>Close the ENCLOSED holes in a 16x16 art silhouette: a pixel joins the shape
        /// only where art brackets it within <paramref name="maxGap"/> on BOTH axes. A railing's
        /// slots and a plank seam fill; the open water above and below the art keeps its side of
        /// the outline, so the carve never squares off to the tile grid.</summary>
        private static bool[] FillEnclosedHoles(bool[] bits, int maxGap)
        {
            const int N = 16;
            var horizontallyEnclosed = new bool[256];
            for (int y = 0; y < N; y++)
            {
                int last = -99;
                for (int x = 0; x < N; x++)
                {
                    if (!bits[y * N + x]) continue;
                    if (x - last > 1 && x - last <= maxGap + 1)
                        for (int k = last + 1; k < x; k++) horizontallyEnclosed[y * N + k] = true;
                    last = x;
                }
            }
            var filled = (bool[])bits.Clone();
            for (int x = 0; x < N; x++)
            {
                int last = -99;
                for (int y = 0; y < N; y++)
                {
                    if (!bits[y * N + x]) continue;
                    if (y - last > 1 && y - last <= maxGap + 1)
                        for (int k = last + 1; k < y; k++)
                            if (horizontallyEnclosed[k * N + x]) filled[k * N + x] = true;
                    last = y;
                }
            }
            return filled;
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
        private WaterMaskJob GatherWaterMask(GameLocation location, int startTileX, int startTileY, int tilesW, int tilesH)
        {
            int count = tilesW * tilesH;
            var job = new WaterMaskJob
            {
                Location = location, StartTileX = startTileX, StartTileY = startTileY,
                TileWidth = tilesW, TileHeight = tilesH, WaterDrawHookVersion = WaterDrawHook.Version,
                LabelVersion = CurrentLabelVersion(), Epoch = MaskEpoch,
                // Snapshot the location-wide waterline anchor if it is still valid for
                // exactly this identity — the worker reads it lock-free (immutable).
                Anchor = AnchorFresh(location) ? _waterlineAnchorData : null,
            };

            // The surface grid classifies the actual water SURFACE: ponds and beach tide pools
            // count as water (they reflect too), while pier/bridge DECKS over water do not — no
            // reflection is painted onto planks. Built once per location visit.
            var surf = SurfaceMap.For(location);
            // Ground-truth labels ship WITH this mod (labels/), read once at startup — nothing
            // here touches the disk or depends on another mod being installed.
            var labels = LabelStore.Instance;
            if (labels is { Any: false }) labels = null;
            // The Desert never has waterTiles (the game excludes it by class in loadMap): its
            // pond is decorative art the game draws no overlay on, so nothing there is water,
            // whatever the tile properties say.
            bool desert = location is StardewValley.Locations.Desert;
            // Fish ponds draw their own water in the sorted-sprite pass — never in waterTiles,
            // never a Back "Water" property. Their water is the interior of the footprint
            // (the 1-tile rim is masonry, per FishPond.isTileFishable).
            List<Rectangle>? pondRects = null;
            foreach (var b in location.buildings)
            {
                if (b is StardewValley.Buildings.FishPond fp && fp.daysOfConstructionLeft.Value <= 0)
                    (pondRects ??= new()).Add(new Rectangle(
                        fp.tileX.Value + 1, fp.tileY.Value + 1,
                        Math.Max(0, fp.tilesWide.Value - 2), Math.Max(0, fp.tilesHigh.Value - 2)));
            }
            if (_waterTileFlags == null || _waterTileFlags.Length < count) _waterTileFlags = new bool[count];
            bool hasAnyWater = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool water = !desert && (surf != null ? surf.IsWater(tx, ty) : location.isWaterTile(tx, ty));
                    // Draw-call truth: the game DREW water here but the tile data doesn't know it
                    // (a location/mod with custom drawWater logic). Only when isWaterTile is false —
                    // isWaterTile-true tiles keep their pipeline above, so HF's deck-over-water veto
                    // is never overridden by the hook.
                    if (!water && !desert && !location.isWaterTile(tx, ty) && WaterDrawHook.WasDrawn(location, tx, ty))
                        water = true;
                    if (!water && pondRects != null)
                    {
                        foreach (var r in pondRects)
                            if (r.Contains(tx, ty)) { water = true; break; }
                    }
                    if (water) hasAnyWater = true;
                    _waterTileFlags[j * tilesW + i] = water;
                }
            }
            job.AnyWater = hasAnyWater;

            // 1.6 maps can carry SEVERAL layers per family (Back2, Buildings3, Front-less
            // AlwaysFront4 ...), and Dynamic Reflections' issue tracker is full of maps whose
            // water art lives on Back2 (coral-reef beaches). Collect every RENDERED layer per
            // family: the family name plus a digits-only suffix — "Back-1" is the Tiled
            // convention for a DISABLED layer and must stay out (see MapLayers.BelongsToFamily).
            List<xTile.Layers.Layer>? backs = null, blds = null, always = null;
            List<xTile.Layers.Layer>? fronts = null;
            if (location.map != null)
            {
                foreach (var l in location.map.Layers)
                {
                    if (MapLayers.BelongsToFamily(l.Id, "AlwaysFront")) (always ??= new()).Add(l);
                    else if (MapLayers.BelongsToFamily(l.Id, "Back")) (backs ??= new()).Add(l);
                    else if (MapLayers.BelongsToFamily(l.Id, "Buildings")) (blds ??= new()).Add(l);
                    else if (MapLayers.BelongsToFamily(l.Id, "Front")) (fronts ??= new()).Add(l);
                }
            }
            var front = fronts is { Count: > 0 } ? fronts[0] : null;
            // Extra Front layers (Front2 ...) carve exactly like AlwaysFront: over-player art.
            if (fronts is { Count: > 1 })
                for (int k = 1; k < fronts.Count; k++)
                    (always ??= new()).Add(fronts[k]);

            if (_tileEffectBits == null || _tileEffectBits.Length < count) _tileEffectBits = new bool[]?[count];
            if (_tileWaterKeepBits == null || _tileWaterKeepBits.Length < count) _tileWaterKeepBits = new bool[]?[count];
            if (_tileBuildingCarveBits == null || _tileBuildingCarveBits.Length < count) _tileBuildingCarveBits = new bool[]?[count];
            if (_tileFrontCarveBits == null || _tileFrontCarveBits.Length < count) _tileFrontCarveBits = new bool[]?[count];
            if (_tileLargeSolidFlags == null || _tileLargeSolidFlags.Length < count) _tileLargeSolidFlags = new bool[count];
            if (_tileDeckFlags == null || _tileDeckFlags.Length < count) _tileDeckFlags = new bool[count];
            if (_tileLabeledLiquidFlags == null || _tileLabeledLiquidFlags.Length < count) _tileLabeledLiquidFlags = new bool[count];
            if (_tileHasBuildingArtFlags == null || _tileHasBuildingArtFlags.Length < count) _tileHasBuildingArtFlags = new bool[count];
            if (_tileHasFrontArtFlags == null || _tileHasFrontArtFlags.Length < count) _tileHasFrontArtFlags = new bool[count];
            if (_tileBuildingGroundOverlayFlags == null || _tileBuildingGroundOverlayFlags.Length < count) _tileBuildingGroundOverlayFlags = new bool[count];
            if (_tileFrontGroundOverlayFlags == null || _tileFrontGroundOverlayFlags.Length < count) _tileFrontGroundOverlayFlags = new bool[count];
            if (_tileIceBits == null || _tileIceBits.Length < count) _tileIceBits = new bool[]?[count];
            if (_tileLavaBits == null || _tileLavaBits.Length < count) _tileLavaBits = new bool[]?[count];
            if (_tileFlowBits == null || _tileFlowBits.Length < count) _tileFlowBits = new bool[]?[count];
            if (_tileNearLandFlags == null || _tileNearLandFlags.Length < count) _tileNearLandFlags = new bool[count];
            if (_tileIceFlags == null || _tileIceFlags.Length < count) _tileIceFlags = new bool[count];
            if (_tileFlowFlags == null || _tileFlowFlags.Length < count) _tileFlowFlags = new bool[count];
            if (_tileLavaFlags == null || _tileLavaFlags.Length < count) _tileLavaFlags = new bool[count];

            // Volcano interiors hold lava, not water. The lava sub-class (slow molten flow,
            // self-glow, no mirror) otherwise only triggers on painted label class 11, which
            // ships dormant — so vanilla lava rendered as ordinary water, complete with a
            // mirror reflection. Tag it from the location instead so it reads as lava out of
            // the box; a painted label still wins per tile below.
            string locName = location.NameOrUniqueName ?? location.Name ?? "";
            bool locIsLava = location is StardewValley.Locations.VolcanoDungeon
                || locName.Contains("Caldera", StringComparison.OrdinalIgnoreCase)
                || locName.Contains("Volcano", StringComparison.OrdinalIgnoreCase)
                // Mine floors 80-119: the game reuses the water overlay tinted Red*0.8 for lava
                // (decompiled MineShaft.loadLevel) — same machinery, molten look.
                || (location is StardewValley.Locations.MineShaft ms && ms.getMineArea() == 80);

            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterTileFlags[idx];
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
                            if (TryTileArt(bl, tx, ty, out var tb, out var srcRect, out _))
                            {
                                var solid = SolidBits(tb, srcRect);
                                if (!hasBld) { hasBld = true; t1 = tb; s1 = srcRect; cbAcc = solid; }
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
                    _tileEffectBits[idx] = bits;
                    // Water is water whether the GAME flagged the tile or a LABEL painted it.
                    // The overlay-carve rules below all keyed off the game flag alone, so on a
                    // label-water tile they never ran: the Town bridge sits on tiles the game
                    // does not call water, so its painted shadow — a translucent wash SolidBits
                    // deliberately spares, because shaded WATER still ripples — was never carved
                    // even though the label calls those pixels ground. That band rippling under
                    // the planks is the bridge outline players see in the rain.
                    bool waterHere = isWater || bits != null;

                    // Structure / carve inputs (Pass C + the land-connectivity test + arch fill).
                    bool bldLabeledLiquid = false;   // label says the overlay here IS water
                    bool frontLabeledLiquid = false;
                    bool hasFront = TryTileArt(front, tx, ty, out var t2, out var s2);
                    _tileHasBuildingArtFlags[idx] = hasBld;
                    _tileBuildingGroundOverlayFlags![idx] = false;   // buffers are reused frame to frame
                    _tileFrontGroundOverlayFlags![idx] = false;
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
                    // Parallel LOW-alpha union (>= 32): where the front/always art draws anything
                    // visible at all — the gate for the carve LIFT below, so a falls' spray
                    // (far under the 128-opaque bar) still counts as visible water.
                    bool[]? fAnyBits = null;
                    void MergeAny(bool[] add)
                    {
                        if (fAnyBits == null) { fAnyBits = add; return; }
                        var m = new bool[256];
                        for (int p = 0; p < 256; p++) m[p] = fAnyBits[p] || add[p];
                        fAnyBits = m;
                    }
                    int fCount = 0;
                    if (hasFront)
                    {
                        frontArt = true;
                        var cfSolid = SolidBits(t2, s2);
                        fCount = cfSolid.count;
                        bool g = OverlayIsGround(labels, front, tx, ty, waterHere);
                        if (!g) frontAllGround = false;
                        fBits = g ? OpaqueBits(t2, s2).bits : cfSolid.bits;
                        MergeAny(AnyAlphaBits(t2, s2));
                    }
                    // Fold every AlwaysFront layer's opacity into the Front carve channel.
                    if (always != null)
                        foreach (var l in always)
                            if (TryTileArt(l, tx, ty, out var t3, out var s3))
                            {
                                frontArt = true;
                                var ca = SolidBits(t3, s3);
                                bool g = OverlayIsGround(labels, l, tx, ty, waterHere);
                                if (!g) frontAllGround = false;
                                MergeAny(AnyAlphaBits(t3, s3));
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
                    _tileFrontGroundOverlayFlags[idx] = frontArt && frontAllGround;
                    _tileHasFrontArtFlags[idx] = fBits != null;
                    _tileBuildingCarveBits[idx] = hasBld ? cb.bits : null;
                    _tileFrontCarveBits[idx] = fBits;
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
                    if (waterHere && hasBld)
                    {
                        byte[]? gl = bldLbl;
                        if (gl != null && CountLiquid(gl) == 0)
                        {
                            // EVERY visible pixel, shadow wash included: the label has already
                            // ruled that nothing here is liquid, so the "a dark translucent wash
                            // over water is still water" heuristic has nothing left to protect.
                            //
                            // Plus the art's own ENCLOSED holes: a bridge railing is mostly slots,
                            // and the river showing through them is real water, but 66 of 256
                            // texels rippling in thin gaps between the posts is exactly the
                            // "bridge shows an outline in the rain" report. Filling only holes
                            // bracketed by art on both axes keeps the carve on the structure's
                            // real outline — carving the whole tile instead squared the boundary
                            // off to the tile grid (a frame around the bridge) and took the
                            // march with it, which cost the reflection under the span.
                            var groundBits = FillEnclosedHoles(AnyAlphaBits(t1, s1), 8);
                            int groundCount = 0;
                            for (int p = 0; p < 256; p++) if (groundBits[p]) groundCount++;
                            if (groundCount > 0)
                            {
                                _tileBuildingCarveBits[idx] = groundBits;
                                _tileBuildingGroundOverlayFlags![idx] = true;
                            }
                        }
                    }
                    if (waterHere && hasBld && cb.bits != null && !_tileBuildingGroundOverlayFlags![idx])
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
                                _tileBuildingCarveBits[idx] = carve;
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
                    if (isWater && labels != null && (fBits != null || fAnyBits != null))
                    {
                        // Each front-family layer's label answers for ITS OWN art, gated by that
                        // art's visible alpha (>= 32; the 128-opaque bar re-carved a falls'
                        // semi-transparent spray). The old "topmost front-family label wins" let
                        // a cliff-top overhang labelled ground:256 on AlwaysFront steal the slot
                        // from the falls labelled flow:256 on Front beneath it — the falls base
                        // carved to effect 0/256 in every season. A pixel counts as VISIBLE
                        // LIQUID when some layer both paints it liquid and draws art there; the
                        // rock showing through fully transparent pixels stays carved.
                        bool[]? liquidVisible = null;
                        int frontIce = 0, frontFlow = 0, frontLava = 0;
                        void FoldFrontLiquid(xTile.Layers.Layer? layer)
                        {
                            if (layer == null || labels.Get(layer, tx, ty) is not { } lbl2)
                                return;
                            var (lb, lW, lI, lF, lL) = WaterBitsFromLabels(lbl2);
                            if (lW + lI + lF + lL == 0)
                                return;
                            if (!TryTileArt(layer, tx, ty, out var lt, out var ls))
                                return;
                            bool[] vis = AnyAlphaBits(lt, ls);
                            bool any = false;
                            for (int p = 0; p < 256; p++)
                                if (lb[p] && vis[p])
                                {
                                    (liquidVisible ??= new bool[256])[p] = true;
                                    any = true;
                                }
                            if (!any)
                                return;
                            frontIce += lI; frontFlow += lF; frontLava += lL;
                            if (lI > 0 || lL > 0 || lF > 0) AddSubTypePixels(lbl2, ref icePx, ref lavaPx, ref flowPx);
                        }
                        if (fronts != null) foreach (var fl in fronts) FoldFrontLiquid(fl);
                        if (always != null) foreach (var al in always) FoldFrontLiquid(al);
                        if (liquidVisible != null)
                        {
                            if (fBits != null)
                            {
                                var carveF = (bool[])fBits.Clone();
                                for (int p = 0; p < 256; p++)
                                    if (liquidVisible[p]) carveF[p] = false;
                                _tileFrontCarveBits[idx] = carveF;
                            }
                            // The same liquid beats the BUILDINGS carve too: the falls draws
                            // over an opaque cliff/bank on Buildings — hidden art whose opacity
                            // otherwise erases the flow the player actually sees.
                            if (_tileBuildingCarveBits[idx] is { } carveUnder)
                            {
                                var carveB2 = (bool[])carveUnder.Clone();
                                for (int p = 0; p < 256; p++)
                                    if (liquidVisible[p]) carveB2[p] = false;
                                _tileBuildingCarveBits[idx] = carveB2;
                            }
                            iceN += frontIce; flowN += frontFlow; lavaN += frontLava;
                            job.AnyLabeled = true;
                            fCount = 0;                 // labelled liquid is never a structure
                            frontLabeledLiquid = true;
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
                    _tileWaterKeepBits![idx] = keep;
                    _tileIceBits![idx] = icePx;
                    _tileLavaBits![idx] = lavaPx;
                    _tileFlowBits![idx] = flowPx;
                    // Ice / flowing win over each other by pixel count; a plain-water majority
                    // keeps normal behaviour. Ice → reflection but no ripple (mask alpha 0);
                    // flowing → ripple but no reflection (scrubbed from the march channel).
                    _tileIceFlags[idx] = iceN > 0 && iceN >= flowN && iceN >= lavaN;
                    _tileFlowFlags[idx] = flowN > 0 && flowN > iceN && flowN >= lavaN;
                    // A volcano location is lava unless a label says this tile is something else.
                    _tileLavaFlags[idx] = (lavaN > 0 && lavaN > iceN && lavaN > flowN)
                        || (locIsLava && iceN == 0 && flowN == 0);
                    // DECK tiles (walkable piers / plank bridges) block as whole tiles too: the
                    // beach plank's art has a painted wet stain that classified as water, punching
                    // a 2-texel channel through the deck — and the ±10 shoreline smoothing then
                    // dragged the anchors of a full tile around it up above the plank (reflection
                    // missing on that side).
                    bool deck = surf != null && surf.GetSurface(tx, ty) == SurfaceClass.Deck;
                    _tileDeckFlags[idx] = deck;
                    _tileLargeSolidFlags[idx] = deck || (hasBld && cb.count >= 230 && !bldLabeledLiquid) || fCount >= 230;
                    // A tile whose overlay art is LABELLED liquid has already been resolved per
                    // pixel above: the carve keeps exactly the painted liquid and cuts exactly the
                    // rest. Pass C's whole-tile march scrub must not run on top of that, or the
                    // pixel-accurate waterline we just built is thrown away and the anchor snaps
                    // back to the tile grid. Unlabelled tiles keep the tile-level verdict, so maps
                    // nobody has painted behave exactly as before.
                    _tileLabeledLiquidFlags[idx] = bldLabeledLiquid || frontLabeledLiquid;
                }
            }
            // FURNITURE and BUILDING entity rects (Pass C2 inputs). A fish tank's painted
            // water, a well's blue bucket art, a trough — water pixels inside an ENTITY
            // sprite, not a water body. Snapshot their drawn rects here: entity lists are
            // live game state the worker must never touch.
            _entityCarveWorldRectangles.Clear();
            foreach (var f in location.furniture)
            {
                Rectangle bb = f.boundingBox.Value;
                int artH = f.sourceRect.Value.Height * 4;
                _entityCarveWorldRectangles.Add((bb.X, bb.Bottom - Math.Max(artH, bb.Height), bb.Right, bb.Bottom));
            }
            foreach (var b in location.buildings)
            {
                if (b == null)
                    continue;
                int bx = b.tileX.Value * 64, bw2 = b.tilesWide.Value * 64;
                int bottom = (b.tileY.Value + b.tilesHigh.Value) * 64;
                int artH = b.tilesHigh.Value * 64;
                try { int sh = b.getSourceRect().Height * 4; if (sh > 0) artH = Math.Max(artH, sh); }
                catch { /* sprite not ready — footprint only */ }
                _entityCarveWorldRectangles.Add((bx, bottom - artH, bx + bw2, bottom));
            }
            return job;
        }

        /// <summary>Compose stage - the pixel crunching (passes A-E). Pure array work on gathered
        /// data; safe on a worker thread. Jobs are serialized, so the shared scratch
        /// buffers are exclusively this job's while it runs.</summary>
        private void ComposeWaterMask(WaterMaskJob job)
        {
            int tilesW = job.TileWidth, tilesH = job.TileHeight;
            int count = tilesW * tilesH;
            const int Sub = 16;
            int pw = tilesW * Sub, ph = tilesH * Sub;
            int pcount = pw * ph;

            // CORE mask (undilated): the reflection's shoreline search must see bridges,
            // piers and banks as land — the dilated mask swallowed any land strip ≤4 tiles
            // wide (a bridge between two water bodies), which killed their reflections.
            if (_waterMaskCorePixels == null || _waterMaskCorePixels.Length < count)
                _waterMaskCorePixels = new Color[count];
            for (int idx = 0; idx < count; idx++)
                _waterMaskCorePixels[idx] = _waterTileFlags![idx] ? Color.White : Color.Transparent;

            // ---- Pass A — composite: true water tiles solid, classified art per-pixel ----
            // (The upload buffer is Pass E's output — a full-map ANCHOR job never gets there,
            // so don't inflate a map-sized Color[] it will never touch.)
            if (!job.AnchorOnly && (_waterMaskPixels == null || _waterMaskPixels.Length < pcount)) _waterMaskPixels = new Color[pcount];
            if (_waterEffectBits == null || _waterEffectBits.Length < pcount) _waterEffectBits = new bool[pcount];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterTileFlags![idx];
                    bool[]? bits = _tileEffectBits![idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterEffectBits[row + px] = isWater || (bits != null && bits[arow + px]);
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
            if (_waterMarchBits == null || _waterMarchBits.Length < pcount)
                _waterMarchBits = new bool[pcount];
            Array.Copy(_waterEffectBits, _waterMarchBits, pcount);
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
                    bool[]? keep = _tileWaterKeepBits![j * tilesW + i];
                    if (keep == null)
                        continue;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            if (!keep[arow + px])
                            {
                                _waterEffectBits[row + px] = false;
                                _waterMarchBits[row + px] = false;
                            }
                    }
                }
            }
            RestoreEnclosedMarch(_waterMarchBits, pw, ph);
            // (V4) The anim-region shape test and its waterfall scrub are gone with the colour
            // classifier that fed them: vertical waterfall faces are label class 10's job now
            // (the whole-tile flow/lava march scrub below still runs on labelled tiles).
            CloseVertical(_waterEffectBits, 4);
            // March close is SPECK-AWARE: a run shorter than 3 texels only bridges gaps
            // ≤4 (a rim sliver above its slit), never the full 12 — wet-shading specks on
            // the bank otherwise chained into the body below, pulling the column's
            // waterline anchor up onto the bank (the surviving dark dashes).
            for (int x = 0; x < pw; x++)
            {
                int last = -99, runH = 0;
                for (int y = 0; y < ph; y++)
                {
                    if (!_waterMarchBits[y * pw + x])
                        continue;
                    int gap = y - last - 1;
                    if (gap == 0)
                        runH++;
                    else if (gap <= 12 && (gap <= 4 || runH >= 3))
                    {
                        for (int k = last + 1; k < y; k++)
                            _waterMarchBits[k * pw + x] = true;
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
            if (_tileNearSolidFlags == null || _tileNearSolidFlags.Length < count) _tileNearSolidFlags = new bool[count];
            if (_tileLandConnectedFlags == null || _tileLandConnectedFlags.Length < count) _tileLandConnectedFlags = new bool[count];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool big = _tileLargeSolidFlags![idx];
                    _tileNearSolidFlags[idx] = big;
                    bool landNear = i == 0 || i == tilesW - 1 || j == 0 || j == tilesH - 1
                        || !_waterTileFlags![idx - 1] || !_waterTileFlags[idx + 1]
                        || !_waterTileFlags[idx - tilesW] || !_waterTileFlags[idx + tilesW];
                    _tileNearLandFlags![idx] = landNear;
                    // A deck is walkable — land-connected by definition, no seed test needed.
                    _tileLandConnectedFlags[idx] = big && (landNear || _tileDeckFlags![idx]);
                }
            }
            for (int sweep = 0; sweep < 2; sweep++)
            {
                for (int idx = 0; idx < count; idx++)                       // forward
                    if (_tileNearSolidFlags[idx] && !_tileLandConnectedFlags[idx] &&
                        ((idx % tilesW > 0 && _tileLandConnectedFlags[idx - 1]) || (idx >= tilesW && _tileLandConnectedFlags[idx - tilesW])))
                        _tileLandConnectedFlags[idx] = true;
                for (int idx = count - 1; idx >= 0; idx--)                  // backward
                    if (_tileNearSolidFlags[idx] && !_tileLandConnectedFlags[idx] &&
                        ((idx % tilesW < tilesW - 1 && _tileLandConnectedFlags[idx + 1]) || (idx + tilesW < count && _tileLandConnectedFlags[idx + tilesW])))
                        _tileLandConnectedFlags[idx] = true;
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
                    if (!_tileLandConnectedFlags[idx])
                        continue;
                    if (i - lastStruct > 1 && i - lastStruct <= 4)
                    {
                        for (int k = lastStruct + 1; k < i; k++)
                        {
                            int kidx = j * tilesW + k;
                            if (_tileHasBuildingArtFlags![kidx] || _tileHasFrontArtFlags![kidx])
                                _tileLandConnectedFlags[kidx] = true;
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
                    bool[]? carveB = _tileBuildingCarveBits![idx];
                    bool[]? carveF = _tileFrontCarveBits![idx];
                    // A structure tile blocks the march down to its art's own SILHOUETTE, per
                    // column: it used to be scrubbed as the whole tile, which also erased the
                    // WATER sharing the tile (the strip under a bank's lip, the opening under a
                    // bridge arch) — ripple with no reflection there, and an entity reflection
                    // started a whole tile below the shore instead of at the water's edge.
                    // Scrubbing each column down to the structure's bottommost opaque pixel
                    // hangs the reflection from the art's real outline; Pass E's ±10 texel
                    // smoothing levels the column-to-column steps (the original whole-tile rule
                    // predates that smoothing).
                    // DECK tiles take the same extent scrub: the old whole-tile rule also erased
                    // the open water SHARING a pier-edge tile, so the water beside the planks
                    // rippled with no reflection. The failure that once forced whole-tile — plank
                    // alpha noise / a wet stain punching a 2-texel channel through the deck, each
                    // column getting its own edge — cannot recur here, because the scrub spans the
                    // art's full top..bottom extent per column and interior holes never split it.
                    // A tile with no gathered art bits at all (Back-layer planking) still scrubs
                    // whole. Other labelled tiles are carved per pixel below instead.
                    bool structTile = _tileLandConnectedFlags[idx] && (_tileDeckFlags![idx] || !_tileLabeledLiquidFlags![idx]);
                    bool pixelCarveMarch = _tileLabeledLiquidFlags![idx] && !structTile && _tileLandConnectedFlags[idx];
                    // The scrub covers the art's vertical EXTENT per column (topmost..bottommost
                    // opaque pixel): water ABOVE the art keeps its march too — the strip north of
                    // a bridge parapet, whose art sits at the tile's bottom, belongs to the upper
                    // water body and must not lose its reflection to the parapet's tile.
                    int[]? structScrubTopByColumn = null, structScrubBottomByColumn = null;
                    if (structTile && (carveB != null || carveF != null))
                    {
                        structScrubTopByColumn = _structScrubTopScratch ??= new int[Sub];
                        structScrubBottomByColumn = _structScrubBottomScratch ??= new int[Sub];
                        for (int px = 0; px < Sub; px++)
                        {
                            int top = Sub, bottom = -1;   // no art in this column: scrub nothing
                            for (int ay = 0; ay < Sub; ay++)
                            {
                                int a = ay * Sub + px;
                                if ((carveB != null && carveB[a]) || (carveF != null && carveF[a]))
                                {
                                    if (ay < top) top = ay;
                                    bottom = ay;
                                }
                            }
                            structScrubTopByColumn[px] = top;
                            structScrubBottomByColumn[px] = bottom;
                        }
                    }
                    // Ground-labelled overlay art breaks the march at its own outline, but only where
                    // the tile touches land. That is the difference between a BANK — whose top edge is
                    // the real waterline, and the reflection has to start below it — and an ISLAND in
                    // mid-pond, which must stay invisible to the march or every reflection in the body
                    // re-anchors on it. Same land-connectivity question the structure test already
                    // asks, answered from the label instead of from opacity.
                    bool groundOverlayMarch = _tileBuildingGroundOverlayFlags![idx] && _tileNearLandFlags![idx];
                    bool groundFrontMarch = _tileFrontGroundOverlayFlags![idx] && _tileNearLandFlags![idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                        {
                            if (structTile && (structScrubBottomByColumn == null
                                    || (py >= structScrubTopByColumn![px] && py <= structScrubBottomByColumn[px])))
                                _waterMarchBits![row + px] = false;
                            if (carveB != null && carveB[arow + px])
                            {
                                _waterEffectBits[row + px] = false;
                                // Labelled structure art breaks the march at its PAINTED shape
                                // (the carve already had the label's liquid pixels removed), so a
                                // rock rim hangs its reflection from its own outline instead of
                                // either a whole-tile hole or nothing.
                                if (pixelCarveMarch || groundOverlayMarch) _waterMarchBits![row + px] = false;
                            }
                            if (carveF != null && carveF[arow + px])
                            {
                                _waterEffectBits[row + px] = false;
                                if (pixelCarveMarch || groundFrontMarch) _waterMarchBits![row + px] = false;
                            }
                        }
                    }
                }
            }

            // Pass C2 — carve FURNITURE and BUILDING entity rects gathered on the main thread.
            foreach (var (wx0, wy0, wx1, wy1) in _entityCarveWorldRectangles)
            {
                int px0 = Math.Max(0, wx0 / 4 - job.StartTileX * Sub);
                int py0 = Math.Max(0, wy0 / 4 - job.StartTileY * Sub);
                int px1 = Math.Min(pw, wx1 / 4 - job.StartTileX * Sub);
                int py1 = Math.Min(ph, wy1 / 4 - job.StartTileY * Sub);
                for (int y = py0; y < py1; y++)
                {
                    int row = y * pw;
                    for (int x = px0; x < px1; x++)
                    {
                        _waterEffectBits![row + x] = false;
                        _waterMarchBits![row + x] = false;
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
                    bool[]? flowB = _tileFlowBits![ti], lavaB = _tileLavaBits![ti];
                    bool wholeTile = flowB == null && lavaB == null
                                  && (_tileFlowFlags![ti] || _tileLavaFlags![ti]);
                    if (!wholeTile && flowB == null && lavaB == null)
                        continue;
                    // The scrub spans the ROWS the flow/lava covers, across the whole tile — not
                    // only the falling pixels. Water sitting BESIDE a fall, at the same height, is
                    // the plunge churn: it carries no flow label of its own, so per-pixel scrubbing
                    // left it mirroring, its column's run began at the top of the falls tile, and
                    // Pass E's horizontal smoothing then dragged the pool's own shoreline up with
                    // it — the reflection climbed into the waterfall. Water BELOW the band (the
                    // pool sharing the tile) still mirrors, which is what per-pixel was for.
                    int bandTop = Sub, bandBottom = -1;
                    if (!wholeTile)
                    {
                        for (int py = 0; py < Sub && bandTop == Sub; py++)
                            for (int px = 0; px < Sub; px++)
                                if ((flowB != null && flowB[py * Sub + px]) || (lavaB != null && lavaB[py * Sub + px]))
                                { bandTop = py; break; }
                        for (int py = Sub - 1; py >= 0 && bandBottom < 0; py--)
                            for (int px = 0; px < Sub; px++)
                                if ((flowB != null && flowB[py * Sub + px]) || (lavaB != null && lavaB[py * Sub + px]))
                                { bandBottom = py; break; }
                    }
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        bool inBand = py >= bandTop && py <= bandBottom;
                        for (int px = 0; px < Sub; px++)
                            if (wholeTile || inBand)
                                _waterMarchBits![row + px] = false;
                    }
                }

            // Pass D — WATERLINE HEIGHT-MAP: per column, remember the top row of each
            // contiguous march-water run (= that pixel's shoreline). Runs shorter than
            // 6 texels are DROPPED from the march: isolated wet-shading specks in shore
            // art each became a tiny mirror (dist 0) that painted a dark dash onto the
            // bank. Runs cut off by the mask bottom are kept — they continue off-screen.
            if (_waterlineTopRowByPixel == null || _waterlineTopRowByPixel.Length < pcount)
                _waterlineTopRowByPixel = new short[pcount];
            DropSpeckComponents(_waterMarchBits!, pw, ph);
            for (int x = 0; x < pw; x++)
            {
                int top = -1;
                for (int y = 0; y <= ph; y++)
                {
                    int p = y * pw + x;
                    if (y < ph && _waterMarchBits![p]) { if (top < 0) top = y; _waterlineTopRowByPixel[p] = (short)top; }
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
            // TRUE shoreline. RunTopRows above the window come out negative — Pass E's depth
            // encode keeps counting from the real shore instead of the window edge.
            if (job.Anchor != null)
                OverrideEdgeFromAnchor(job, pw, ph);

            // WATER-BODY SIZE → calm factor. A tiny tide pool should barely ripple while an
            // ocean rolls; flood-fill the water TILES (4-connected) and scale each tile's effect
            // value by its body's tile count. Works for heuristic AND labelled water alike — the
            // game "knows it's small" from the connected area, not from colour or a special label.
            if (_tileHasEffectWaterFlags == null || _tileHasEffectWaterFlags.Length < count) _tileHasEffectWaterFlags = new bool[count];
            if (_tileCalmnessValues == null || _tileCalmnessValues.Length < count) _tileCalmnessValues = new byte[count];
            for (int j = 0; j < tilesH; j++)
                for (int i = 0; i < tilesW; i++)
                {
                    bool wet = _waterTileFlags![j * tilesW + i];
                    if (!wet)
                        for (int py = 0; py < Sub && !wet; py++)
                        {
                            int r = (j * Sub + py) * pw + i * Sub;
                            for (int px = 0; px < Sub; px++)
                                if (_waterEffectBits![r + px]) { wet = true; break; }
                        }
                    _tileHasEffectWaterFlags[j * tilesW + i] = wet;
                }
            {
                Span<int> stack = count <= 4096 ? stackalloc int[Math.Min(count, 4096)] : new int[count];
                var seen = new bool[count];
                var member = new List<int>(64);
                for (int start = 0; start < count; start++)
                {
                    if (!_tileHasEffectWaterFlags[start] || seen[start])
                        continue;
                    int sp = 0; stack[sp++] = start; seen[start] = true; member.Clear();
                    while (sp > 0)
                    {
                        int cur = stack[--sp]; member.Add(cur);
                        int cx = cur % tilesW, cy = cur / tilesW;
                        if (cx > 0 && _tileHasEffectWaterFlags[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack[sp++] = cur - 1; }
                        if (cx < tilesW - 1 && _tileHasEffectWaterFlags[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack[sp++] = cur + 1; }
                        if (cy > 0 && _tileHasEffectWaterFlags[cur - tilesW] && !seen[cur - tilesW]) { seen[cur - tilesW] = true; stack[sp++] = cur - tilesW; }
                        if (cy < tilesH - 1 && _tileHasEffectWaterFlags[cur + tilesW] && !seen[cur + tilesW]) { seen[cur + tilesW] = true; stack[sp++] = cur + tilesW; }
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
                        _tileCalmnessValues[idx] = cb;
                }
            }

            // Pass E — smooth the shoreline HORIZONTALLY (±10 texels window) and emit. Stepped
            // diagonal banks become a continuous slope, so a reflection is no longer sliced
            // into offset blocks — the shader reads this distance (B, half-texel units) instead
            // of marching. Uses per-row PREFIX SUMS (O(width) per row, was O(width×21)); the
            // window average is clamped to ±1.5 tiles of the pixel's own edge, which bounds the
            // pull from a different water body sharing the row (the old per-neighbour reject).
            if (_waterlineRowPrefixSums == null || _waterlineRowPrefixSums.Length < pw + 1) { _waterlineRowPrefixSums = new int[pw + 1]; _waterlineRowSampleCounts = new int[pw + 1]; }
            for (int y = 0; y < ph; y++)
            {
                int rowBase = y * pw;
                for (int x = 0; x < pw; x++)
                {
                    int p = rowBase + x;
                    bool v = _waterMarchBits![p];
                    _waterlineRowPrefixSums![x + 1] = _waterlineRowPrefixSums[x] + (v ? _waterlineTopRowByPixel[p] : 0);
                    _waterlineRowSampleCounts![x + 1] = _waterlineRowSampleCounts[x] + (v ? 1 : 0);
                }
                for (int x = 0; x < pw; x++)
                {
                    int p = rowBase + x;
                    bool eff = _waterEffectBits[p];
                    bool march = _waterMarchBits![p];
                    byte bch = 255;
                    if (march)
                    {
                        int t0 = _waterlineTopRowByPixel[p];
                        int x0 = Math.Max(0, x - 10), x1 = Math.Min(pw - 1, x + 10);
                        int n = _waterlineRowSampleCounts![x1 + 1] - _waterlineRowSampleCounts[x0];
                        float ts = n > 0 ? (float)(_waterlineRowPrefixSums[x1 + 1] - _waterlineRowPrefixSums[x0]) / n : t0;
                        ts = MathHelper.Clamp(ts, t0 - 24, t0 + 24);
                        // 2 units per texel saturated at 126 texels, under 8 tiles, so every surface wider than
                        // that had no usable depth past its first few tiles. Half a unit reaches ~31.
                        bch = (byte)MathHelper.Clamp((float)Math.Round((y - ts) * 0.5f), 0f, 252f);
                    }
                    int tileIdx = (y / Sub) * tilesW + (x / Sub);
                    byte effV = eff ? (byte)255 : (byte)0;
                    // Body-size calm: a small pool ripples/glints gentler than an open lake.
                    if (eff) effV = (byte)(effV * _tileCalmnessValues![tileIdx] / 255);
                    // ALPHA tags the water TYPE for the shader: 0 = ICE (mirror, no ripple),
                    // 128 = LAVA (slow molten flow + self-glow, no mirror), 255 = normal water.
                    // PER PIXEL where a label said so, falling back to the tile verdict for art
                    // nobody has painted: the type used to be a whole-tile answer, so a tile painted
                    // 184 ice / 72 water froze all 256 and the river wore square patches wherever
                    // the ice met the water. The label knows which pixels are frozen; ask it.
                    int lp = (y % Sub) * Sub + (x % Sub);
                    bool[]? iceB = _tileIceBits![tileIdx], lavaB = _tileLavaBits![tileIdx];
                    // Type ladder in ALPHA: 0 ice · 128 lava · 192 FLOWING · 255 plain water.
                    // 192 is new (the long-parked L4 flow tag): the entity mirror needs to tell a
                    // wet-fringe pixel (mirror a body there) from a waterfall face (never), and
                    // both used to ship as 255. 192 still passes the shader's step(0.75) wet-rim
                    // gate — a waterfall's plunge shore does glisten — and stays clear of the
                    // 0.9 plain-water gate the entity layer reads.
                    // Flowing reads per pixel too now, for the same reason ice does: a fountain
                    // tile holding both a jet and open pool used to tag all 256 texels 192, so
                    // the entity mirror refused a body standing in the pool.
                    bool[]? flowB = _tileFlowBits![tileIdx];
                    byte flowA = (flowB != null ? flowB[lp] : _tileFlowFlags![tileIdx]) ? (byte)192 : (byte)255;
                    byte alpha = iceB != null || lavaB != null
                        ? (iceB != null && iceB[lp] ? (byte)0 : lavaB != null && lavaB[lp] ? (byte)128 : flowA)
                        : _tileIceFlags![tileIdx] ? (byte)0 : _tileLavaFlags![tileIdx] ? (byte)128 : flowA;
                    _waterMaskPixels![p] = new Color(effV, march ? 255 : 0, bch, alpha);
                }
            }

            // ---- Pass F — signed distance to the effect shoreline (3-4 chamfer, 1/3-texel
            // units). One field feeds the shader's quantized edge, the foam band and the wet
            // ground rim; encoded 128 + texels*4 → ±31.75 texels (~±2 tiles) of usable range,
            // which is more than any of its consumers ever look at.
            if (_waterSignedDistancePixels == null || _waterSignedDistancePixels.Length < pcount) _waterSignedDistancePixels = new byte[pcount];
            if (_distanceToLandScratch == null || _distanceToLandScratch.Length < pcount) _distanceToLandScratch = new ushort[pcount];
            if (_distanceToWaterScratch == null || _distanceToWaterScratch.Length < pcount) _distanceToWaterScratch = new ushort[pcount];
            Chamfer34(_waterEffectBits!, true, _distanceToWaterScratch, pw, ph);    // distance TO water (outside px)
            Chamfer34(_waterEffectBits!, false, _distanceToLandScratch, pw, ph);    // distance TO land (inside px)
            for (int p = 0; p < pcount; p++)
            {
                float texels = _waterEffectBits![p] ? _distanceToLandScratch[p] / 3f : -(_distanceToWaterScratch[p] / 3f);
                _waterSignedDistancePixels[p] = (byte)MathHelper.Clamp(128f + texels * 4f, 0f, 255f);
            }
        }

        /// <summary>Apply stage - main thread: upload the composed buffers and publish the new
        /// mask identity. Until this runs, the shader keeps the OLD texture + OLD origin
        /// (a consistent pair — the mask content is world-anchored).</summary>
        private void ApplyWaterMask(WaterMaskJob job)
        {
            _lastWaterLocation = job.Location;
            _lastWaterTileX = job.StartTileX;
            _lastWaterTileY = job.StartTileY;
            _lastWaterBuildTick = Game1.ticks;
            _lastWaterHookVersion = job.WaterDrawHookVersion;
            _lastWaterLabelVersion = job.LabelVersion;
            _lastWaterEpoch = job.Epoch;
            _hasWaterInMask = job.WaterAny;
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

            int tilesW = job.TileWidth, tilesH = job.TileHeight;
            int count = tilesW * tilesH;
            int pw = tilesW * 16, ph = tilesH * 16;
            if (_waterMask == null || _waterMask.Width != pw || _waterMask.Height != ph)
            {
                _waterMask?.Dispose();
                _waterMask = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _waterMask.SetData(_waterMaskPixels, 0, pw * ph);
            if (_waterMaskCore == null || _waterMaskCore.Width != tilesW || _waterMaskCore.Height != tilesH)
            {
                _waterMaskCore?.Dispose();
                _waterMaskCore = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _waterMaskCore.SetData(_waterMaskCorePixels, 0, count);
            if (_waterSignedDistanceTexture == null || _waterSignedDistanceTexture.Width != pw || _waterSignedDistanceTexture.Height != ph)
            {
                _waterSignedDistanceTexture?.Dispose();
                _waterSignedDistanceTexture = new Texture2D(_device, pw, ph, false, SurfaceFormat.Alpha8);
            }
            _waterSignedDistanceTexture.SetData(_waterSignedDistancePixels, 0, pw * ph);
            _waterMaskPixelSize = new Vector2(tilesW, tilesH);

            if (MaskView)
                BuildMaskViewTex(pw, ph);
            // Keep the label-verdict overlay in step with the mask it judges: a rebuild on a
            // tile crossing would otherwise leave yesterday's verdict floating over new water.
            if (DebugChannel == DebugOverlayChannel.LabelDiff)
                VerifyLabels(Game1.currentLocation, worstToList: 0);
        }

        /// <summary>Publish an EMPTY mask for a window with no water, sized and anchored like any
        /// other, so the shader reads "no water here" instead of the previous window's pattern.
        /// All three textures are cleared together: R and G decide coverage, and the SDF's 128 is
        /// its zero, so leaving a stale distance field behind would still shade a phantom shore.</summary>
        private void ClearWaterMask(WaterMaskJob job)
        {
            int tilesW = job.TileWidth, tilesH = job.TileHeight;
            int count = tilesW * tilesH;
            int pw = tilesW * 16, ph = tilesH * 16;
            int pcount = pw * ph;

            if (_waterMaskPixels == null || _waterMaskPixels.Length < pcount) _waterMaskPixels = new Color[pcount];
            if (_waterMaskCorePixels == null || _waterMaskCorePixels.Length < count) _waterMaskCorePixels = new Color[count];
            if (_waterSignedDistancePixels == null || _waterSignedDistancePixels.Length < pcount) _waterSignedDistancePixels = new byte[pcount];
            Array.Clear(_waterMaskPixels, 0, pcount);
            Array.Clear(_waterMaskCorePixels, 0, count);
            // 0 = as far from water as this encoding can say. It used to be 128, which means
            // "exactly on the waterline", so a window with NO WATER IN IT told the shader that
            // every single pixel was standing at the water's edge - and the wet-rim term, whose
            // whole job is to darken the last few texels of land before the water, then had
            // licence to darken the entire screen. Nothing is near water here; say so.
            for (int p = 0; p < pcount; p++) _waterSignedDistancePixels[p] = 0;

            if (_waterMask == null || _waterMask.Width != pw || _waterMask.Height != ph)
            {
                _waterMask?.Dispose();
                _waterMask = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _waterMask.SetData(_waterMaskPixels, 0, pcount);
            if (_waterMaskCore == null || _waterMaskCore.Width != tilesW || _waterMaskCore.Height != tilesH)
            {
                _waterMaskCore?.Dispose();
                _waterMaskCore = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _waterMaskCore.SetData(_waterMaskCorePixels, 0, count);
            if (_waterSignedDistanceTexture == null || _waterSignedDistanceTexture.Width != pw || _waterSignedDistanceTexture.Height != ph)
            {
                _waterSignedDistanceTexture?.Dispose();
                _waterSignedDistanceTexture = new Texture2D(_device, pw, ph, false, SurfaceFormat.Alpha8);
            }
            _waterSignedDistanceTexture.SetData(_waterSignedDistancePixels, 0, pcount);
            _waterMaskPixelSize = new Vector2(tilesW, tilesH);
        }

        // ---- live debug overlay: what the mask ACTUALLY covers, per pixel ----

        /// <summary>Toggled by the radiance_maskview console command.</summary>
        internal static bool MaskView;
        private Texture2D? _maskDebugTexture;
        private Color[]? _maskDebugPixels;

        /// <summary>Readable recolor of the freshly composed mask (built only while the
        /// overlay is on): cyan = full water effect, orange = effect-only art water
        /// (fountains/puddles, softer), green = march (reflection) shoreline band.</summary>
        private void BuildMaskViewTex(int pw, int ph)
        {
            int pcount = pw * ph;
            if (_maskDebugPixels == null || _maskDebugPixels.Length < pcount)
                _maskDebugPixels = new Color[pcount];
            for (int p = 0; p < pcount; p++)
            {
                Color m = _waterMaskPixels![p];
                bool eff = m.R > 0, march = m.G > 0;
                _maskDebugPixels[p] =
                    eff && march ? new Color(0, m.R, 255) :          // cyan: effect + reflection water
                    eff ? new Color(255, (byte)(m.R / 2), 0) :       // orange: effect-only (soft art water)
                    march ? new Color(0, 220, 60) :                  // green: march-only (rare)
                    Color.Transparent;
                // Bright rim right AT the smoothed waterline (edge distance ~0) — the anchor line.
                if (march && m.B <= 2)
                    _maskDebugPixels[p] = new Color(120, 255, 120);
            }
            if (_maskDebugTexture == null || _maskDebugTexture.Width != pw || _maskDebugTexture.Height != ph)
            {
                _maskDebugTexture?.Dispose();
                _maskDebugTexture = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _maskDebugTexture.SetData(_maskDebugPixels, 0, pcount);
        }

        /// <summary>Draw the overlay into the world batch (RenderedWorld space = world px minus viewport).</summary>
        public void DrawMaskOverlay(SpriteBatch b)
        {
            if (_maskDebugTexture == null || !_hasWaterInMask)
                return;
            var viewport = Game1.viewport;
            var dest = new Rectangle(_lastWaterTileX * 64 - viewport.X, _lastWaterTileY * 64 - viewport.Y,
                _maskDebugTexture.Width * 4, _maskDebugTexture.Height * 4);
            b.Draw(_maskDebugTexture, dest, Color.White * 0.55f);
        }
    }
}
