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
            /// <summary>Which screen asked for this window. Rebuilds stay serialized across ALL
            /// screens (the gather and compose buffers are shared and unlocked, which is only safe
            /// while one rebuild exists at a time), so a finished job has to be able to say whose
            /// it is: the screen that happens to be drawing when it lands may not be the one that
            /// asked, and applying it there would put another camera's window on this camera.</summary>
            public int ScreenId;
            public int StartTileX, StartTileY, TileWidth, TileHeight, WaterDrawHookVersion, LabelVersion, Epoch;
            public bool AnyWater;              // gather: any true water tile
            public bool AnyLabeled;            // gather: any label-nominated water art
            public bool WaterAny;              // compose: final any-water verdict
            /// <summary>Compose: per window tile, game water OR any composed effect pixel. This is
            /// what the near-water gates (sprite mask, entity mirror) are published, because the
            /// gather flags alone miss water that only a label brought in - the desert oasis is
            /// one - and every stamp near it was culled while the ripple ran over the palms.</summary>
            public bool[]? TileHasEffectWaterFlags;
            public double ComposeDurationMilliseconds;           // worker-side timing (diag)
            /// <summary>Apply: how many of the upload steps have run, one texture (or the two small
            /// ones) per frame (RenderPipeline.WaterMask.Apply.cs). The job stays pending until all have.</summary>
            public int ApplyTexturesDone;
            public System.Threading.Tasks.Task? Task;
            public volatile bool Done;
            public volatile bool Failed;
            // P3a — location-wide waterline anchor (RenderPipeline.Waterline.cs):
            public bool AnchorOnly;            // full-map job: stop after Pass D, emit run lists
            public WaterlineAnchor? Anchor;    // window job: fresh anchor to override run tops with
            public int[]? AnchorColumnRunStartIndices;      // AnchorOnly results (worker writes, main consumes)
            public short[]? AnchorRunTopRows;
            public short[]? AnchorRunBottomRows;
            // Location-wide water body sizes for the calm factor (main thread builds, worker reads):
            public int[]? BodyTileCounts;
            public int BodyGridWidth, BodyGridHeight;
            // Built fish ponds in this location, interior tiles only (main thread gathers, worker
            // reads). A pond's tiles read no map art and carry the VESSEL alpha tag.
            public List<Rectangle>? PondRects;
        }

        private WaterMaskJob? _pendingWaterMaskJob;
        private bool _waterMaskJobFailureLogged;

        /// <summary>One rebuild's working memory. See <see cref="WaterMaskScratch"/> for why it is
        /// a single shared instance and why that needs no lock.</summary>
        private readonly WaterMaskScratch _maskScratch = new();

        /// <summary>Mask texels per world tile. Four separate methods each declared their own local
        /// 16, two calling it Sub and two calling it Texels, for a number that must be the same in
        /// all of them or the mask and the shader disagree about where a tile is.</summary>
        private const int MaskTexelsPerTile = 16;

        // ---- gathered per-tile inputs (main thread writes, worker reads) ----
        // Entity rects Pass C2 carves, plus the sprite's opacity when it could be read: bits is
        // art-pixel resolution (w*h of the source rect), null means carve the whole rectangle.
        private readonly List<(int x0, int y0, int x1, int y1, bool[]? opaque, int opaqueWidth, int opaqueHeight)> _entityCarveWorldRectangles = new();
        private readonly Dictionary<(Texture2D texture, Rectangle src), (bool[]? bits, int w, int h)> _entityOpaqueCache = new();

        /// <summary>Opacity of one entity sprite at art-pixel resolution, cached per
        /// (texture, sourceRect). MAIN THREAD ONLY — it reads the GPU texture back. Null on any
        /// failure, which callers treat as "carve the whole rect", the old behaviour.</summary>
        private (bool[]? bits, int w, int h) EntityOpaqueBits(Texture2D texture, Rectangle src)
        {
            var key = (texture, src);
            if (_entityOpaqueCache.TryGetValue(key, out var e))
                return e;
            (bool[]? bits, int w, int h) entry = (null, 0, 0);
            try
            {
                var pixels = new Color[src.Width * src.Height];
                texture.GetData(0, src, pixels, 0, pixels.Length);
                var bits = new bool[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                    bits[i] = pixels[i].A >= 128;
                CarveEnclosedHoles(bits, src.Width, src.Height);
                entry = (bits, src.Width, src.Height);
            }
            catch { /* keep null — whole-rect fallback */ }
            _entityOpaqueCache[key] = entry;
            return entry;
        }

        /// <summary>
        /// Treat a transparent hole with sprite all the way around it as part of the sprite.
        ///
        /// <para>The carve reads a sprite's own alpha, which is right at its outline and wrong
        /// inside it. A bench has a slot between its back and its seat; the slot is transparent,
        /// so it was not carved, so the ripple ran through it, and the reported symptom was water
        /// moving inside a bench standing on a pier. Whatever is behind that slot, it is four
        /// pixels of it seen through furniture, and a wave crossing a gap that narrow reads as a
        /// fault however the map is labelled.</para>
        ///
        /// <para>A hole that reaches the edge of the sprite is left alone, because that is not a
        /// hole: it is the space beside the sprite, and the water there is water. So a four-way
        /// flood from the border through everything transparent separates the two in one sweep,
        /// the same test <see cref="RestoreEnclosedMarch"/> makes about the march channel, and for
        /// the same reason. Whatever the flood does not reach was enclosed and is carved.</para>
        ///
        /// <para>This runs once per (texture, source rect) and its answer is cached with the bits
        /// it edits, so a bench pays for it on the frame it first appears and never again.</para>
        /// </summary>
        private static void CarveEnclosedHoles(bool[] opaque, int width, int height)
        {
            int count = width * height;
            if (count <= 0 || width < 3 || height < 3)
                return;
            var reachedFromOutside = new bool[count];
            var pending = new System.Collections.Generic.Stack<int>();
            void Push(int index)
            {
                if (!opaque[index] && !reachedFromOutside[index])
                {
                    reachedFromOutside[index] = true;
                    pending.Push(index);
                }
            }
            for (int x = 0; x < width; x++)
            {
                Push(x);
                Push((height - 1) * width + x);
            }
            for (int y = 0; y < height; y++)
            {
                Push(y * width);
                Push(y * width + width - 1);
            }
            while (pending.Count > 0)
            {
                int index = pending.Pop();
                int x = index % width, y = index / width;
                if (x > 0) Push(index - 1);
                if (x < width - 1) Push(index + 1);
                if (y > 0) Push(index - width);
                if (y < height - 1) Push(index + width);
            }
            for (int i = 0; i < count; i++)
                if (!opaque[i] && !reachedFromOutside[i])
                    opaque[i] = true;
        }


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
        /// <para>
        /// Only pixels the carve ACTUALLY removed may come back, which is what <paramref name="carved"/>
        /// records. Without that gate the sweep restored every non-march pixel the flood missed,
        /// including plain land that was never water and never carved: a grass pocket with water
        /// above it and a deck to its left reads as enclosed the moment the camera puts its open
        /// side off-screen, so the same world tile measured march=256/256 from one standing spot
        /// and 0/256 from another, and the bushes growing on it picked up the water shimmer.
        /// Enclosure is a window-local test; the set it is allowed to act on must not be.
        /// </para>
        /// </summary>
        private void RestoreEnclosedMarch(bool[] march, bool[] carved, int maskWidth, int maskHeight)
        {
            int texelCount = maskWidth * maskHeight;
            if (_maskScratch.MarchOutsideFlags == null || _maskScratch.MarchOutsideFlags.Length < texelCount)
                _maskScratch.MarchOutsideFlags = new bool[texelCount];
            var outside = _maskScratch.MarchOutsideFlags;
            Array.Clear(outside, 0, texelCount);
            if (_maskScratch.MarchFloodStack == null || _maskScratch.MarchFloodStack.Length < texelCount)
                _maskScratch.MarchFloodStack = new int[texelCount];
            var stack = _maskScratch.MarchFloodStack;
            int stackTop = 0;

            void Seed(int texelIndex)
            {
                if (!march[texelIndex] && !outside[texelIndex]) { outside[texelIndex] = true; stack[stackTop++] = texelIndex; }
            }
            for (int x = 0; x < maskWidth; x++) { Seed(x); Seed((maskHeight - 1) * maskWidth + x); }
            for (int y = 0; y < maskHeight; y++) { Seed(y * maskWidth); Seed(y * maskWidth + maskWidth - 1); }

            while (stackTop > 0)
            {
                int texelIndex = stack[--stackTop];
                int x = texelIndex % maskWidth, y = texelIndex / maskWidth;
                if (x > 0) Seed(texelIndex - 1);
                if (x < maskWidth - 1) Seed(texelIndex + 1);
                if (y > 0) Seed(texelIndex - maskWidth);
                if (y < maskHeight - 1) Seed(texelIndex + maskWidth);
            }

            for (int i = 0; i < texelCount; i++)
                if (carved[i] && !march[i] && !outside[i])
                    march[i] = true;
        }


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
        private void DropSpeckComponents(bool[] march, int maskWidth, int maskHeight)
        {
            int texelCount = maskWidth * maskHeight;
            if (_maskScratch.SpeckVisitedFlags == null || _maskScratch.SpeckVisitedFlags.Length < texelCount) _maskScratch.SpeckVisitedFlags = new bool[texelCount];
            if (_maskScratch.MarchFloodStack == null || _maskScratch.MarchFloodStack.Length < texelCount) _maskScratch.MarchFloodStack = new int[texelCount];
            if (_maskScratch.SpeckComponentMembers == null || _maskScratch.SpeckComponentMembers.Length < texelCount) _maskScratch.SpeckComponentMembers = new int[texelCount];
            var seen = _maskScratch.SpeckVisitedFlags;
            var stack = _maskScratch.MarchFloodStack;
            var members = _maskScratch.SpeckComponentMembers;
            Array.Clear(seen, 0, texelCount);

            for (int start = 0; start < texelCount; start++)
            {
                if (!march[start] || seen[start])
                    continue;
                int stackTop = 0, count = 0;
                bool touchesBorder = false;
                seen[start] = true;
                stack[stackTop++] = start;
                while (stackTop > 0)
                {
                    int texelIndex = stack[--stackTop];
                    members[count++] = texelIndex;
                    int x = texelIndex % maskWidth, y = texelIndex / maskWidth;
                    if (x == 0 || y == 0 || x == maskWidth - 1 || y == maskHeight - 1)
                        touchesBorder = true;
                    if (x > 0 && march[texelIndex - 1] && !seen[texelIndex - 1]) { seen[texelIndex - 1] = true; stack[stackTop++] = texelIndex - 1; }
                    if (x < maskWidth - 1 && march[texelIndex + 1] && !seen[texelIndex + 1]) { seen[texelIndex + 1] = true; stack[stackTop++] = texelIndex + 1; }
                    if (y > 0 && march[texelIndex - maskWidth] && !seen[texelIndex - maskWidth]) { seen[texelIndex - maskWidth] = true; stack[stackTop++] = texelIndex - maskWidth; }
                    if (y < maskHeight - 1 && march[texelIndex + maskWidth] && !seen[texelIndex + maskWidth]) { seen[texelIndex + maskWidth] = true; stack[stackTop++] = texelIndex + maskWidth; }
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
        internal string DescribeTileMask(GameLocation? location, int tileX, int tileY)
        {
            Color[]? maskPixels = MaskPixelsForInspection();
            if (maskPixels == null || _waterMask == null)
                return "[mask] no composed mask yet";
            int px0 = (tileX - _lastWaterTileX) * MaskTexelsPerTile, py0 = (tileY - _lastWaterTileY) * MaskTexelsPerTile;
            int maskWidth = _waterMask.Width;
            if (px0 < 0 || py0 < 0 || px0 + MaskTexelsPerTile > maskWidth || py0 + MaskTexelsPerTile > _waterMask.Height)
                return $"[mask] tile ({tileX},{tileY}) is outside the mask window (origin {_lastWaterTileX},{_lastWaterTileY})";

            // COUNT is not enough: the shader ramps coverage down over the last texels of water
            // (edgeQ), so a band only a few texels wide can be fully inside the mask and still
            // render at a sixth of strength - which looks exactly like no coverage at all. Report
            // the strength as well as the count, so "not covered" and "covered but nearly
            // invisible" stop being the same reading.
            int eff = 0, march = 0, effectSum = 0, effectMin = 255, effectMax = 0;
            var alphas = new Dictionary<byte, int>();
            for (int y = 0; y < MaskTexelsPerTile; y++)
                for (int x = 0; x < MaskTexelsPerTile; x++)
                {
                    Color maskColor = maskPixels[(py0 + y) * maskWidth + px0 + x];
                    if (maskColor.R > 0)
                    {
                        eff++; effectSum += maskColor.R;
                        if (maskColor.R < effectMin) effectMin = maskColor.R;
                        if (maskColor.R > effectMax) effectMax = maskColor.R;
                    }
                    if (maskColor.G > 0) march++;
                    alphas[maskColor.A] = alphas.TryGetValue(maskColor.A, out int n) ? n + 1 : 1;
                }
            string strength = eff > 0 ? $" R avg={effectSum / eff} min={effectMin} max={effectMax}" : "";
            string alphaText = string.Join(" ", alphas.OrderBy(kv => kv.Key)
                .Select(kv => $"{(kv.Key == 0 ? "ice" : kv.Key == 128 ? "lava" : kv.Key == 192 ? "flow" : kv.Key == 255 ? "water" : kv.Key.ToString())}:{kv.Value}"));

            var report = new System.Text.StringBuilder();
            report.AppendLine($"[mask] tile ({tileX},{tileY})  effect={eff}/256  march={march}/256{strength}  alpha[{alphaText}]");
            // Compose verdicts for this tile from the LAST window job's scratch — the inputs
            // Pass C weighs when it decides whether the march (reflection) channel survives
            // here. structTile true = the whole tile was scrubbed from the march.
            int tilesWideInWindow = maskWidth / MaskTexelsPerTile;
            int tIdx = (tileY - _lastWaterTileY) * tilesWideInWindow + (tileX - _lastWaterTileX);
            if (_tileLandConnectedFlags != null && tIdx >= 0 && tIdx < _tileLandConnectedFlags.Length)
            {
                bool landConnected = _tileLandConnectedFlags[tIdx];
                bool deck = _maskScratch.TileDeckFlags != null && _maskScratch.TileDeckFlags[tIdx];
                bool labeledLiquid = _maskScratch.TileLabeledLiquidFlags != null && _maskScratch.TileLabeledLiquidFlags[tIdx];
                bool structTile = landConnected && (deck || !labeledLiquid);
                bool[]? keepBits = _maskScratch.TileWaterKeepBits?[tIdx];
                bool[]? artBits = _maskScratch.TileEffectBits?[tIdx];
                report.AppendLine(
                    $"[compose] gameWater={(_waterTileFlags != null && _waterTileFlags[tIdx])} structTile={structTile}"
                    + $" (deck={deck} largeSolid={_maskScratch.TileLargeSolidFlags?[tIdx]} landConnected={landConnected} labeledLiquid={labeledLiquid})"
                    + $" nearLand={_maskScratch.TileNearLandFlags?[tIdx]} bldArt={_maskScratch.TileHasBuildingArtFlags?[tIdx]} frontArt={_maskScratch.TileHasFrontArtFlags?[tIdx]}"
                    + $" bldGroundOverlay={_maskScratch.TileBuildingGroundOverlayFlags?[tIdx]} frontGroundOverlay={_maskScratch.TileFrontGroundOverlayFlags?[tIdx]}"
                    + $" ice={_maskScratch.TileIceFlags?[tIdx]} flow={_maskScratch.TileFlowFlags?[tIdx]} lava={_maskScratch.TileLavaFlags?[tIdx]}"
                    + $" keep={(keepBits == null ? "-" : keepBits.Count(b => b).ToString())}/256"
                    + $" artBits={(artBits == null ? "-" : artBits.Count(b => b).ToString())}/256"
                    // The two carve sets Pass C actually scrubs with. They separate the only two
                    // ways a structure tile can end up with no water left: "-" on both means there
                    // were no art bits to build a silhouette from and the tile was scrubbed WHOLE,
                    // which is a coverage bug; a high count means the art really is opaque across
                    // the tile and the empty mask is correct. Reading the flags alone cannot tell
                    // those apart, and guessing between them cost this session three wrong causes.
                    + $" carveBld={(_maskScratch.TileBuildingCarveBits?[tIdx] == null ? "-" : _maskScratch.TileBuildingCarveBits[tIdx]!.Count(b => b).ToString())}/256"
                    + $" carveFront={(_maskScratch.TileFrontCarveBits?[tIdx] == null ? "-" : _maskScratch.TileFrontCarveBits[tIdx]!.Count(b => b).ToString())}/256");
                // Body size and the calm factor it produced. Both are properties of the pool, so
                // reading the same numbers from two standing spots is the check that a reported
                // flicker is not this again.
                if (_maskScratch.TileCalmnessValues != null && tIdx < _maskScratch.TileCalmnessValues.Length)
                {
                    int bodyTiles = _bodyTileCounts != null && (uint)tileX < (uint)_bodyGridWidth && (uint)tileY < (uint)_bodyGridHeight
                        ? _bodyTileCounts[tileY * _bodyGridWidth + tileX] : -1;
                    report.AppendLine($"[body] mapBodyTiles={(bodyTiles < 0 ? "n/a" : bodyTiles.ToString())}"
                        + $" calm={_maskScratch.TileCalmnessValues[tIdx] / 255f:0.00} (wave/glint scale; same pool must read the same from anywhere)");
                }
            }
            var labels = LabelStore.Instance;
            if (labels == null || !labels.Any)
                return report.Append("[label] no label set loaded").ToString();
            foreach (string layerName in new[] { "Back", "Back2", "Buildings", "Buildings2", "Front", "Front2", "AlwaysFront" })
            {
                byte[]? backLabel = labels.Get(location, tileX, tileY, layerName);
                if (backLabel == null)
                    continue;
                var histogram = new Dictionary<byte, int>();
                foreach (byte labelClass in backLabel) histogram[labelClass] = histogram.TryGetValue(labelClass, out int n) ? n + 1 : 1;
                report.AppendLine($"[label] {layerName,-11} " + string.Join(" ", histogram.OrderBy(kv => kv.Key).Select(kv => $"{ClassName(kv.Key)}:{kv.Value}")));
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
            int maskWidth = _waterMask.Width, maskHeight = _waterMask.Height;
            int tilesWide = maskWidth / MaskTexelsPerTile, tilesHigh = maskHeight / MaskTexelsPerTile;
            var tiles = new List<(int tileX, int tileY, int orange, int eff)>();
            for (int j = 0; j < tilesHigh; j++)
                for (int i = 0; i < tilesWide; i++)
                {
                    int orange = 0, eff = 0;
                    for (int y = 0; y < MaskTexelsPerTile; y++)
                    {
                        int row = (j * MaskTexelsPerTile + y) * maskWidth + i * MaskTexelsPerTile;
                        for (int x = 0; x < MaskTexelsPerTile; x++)
                        {
                            Color maskColor = maskPixels[row + x];
                            if (maskColor.R > 0) { eff++; if (maskColor.G == 0) orange++; }
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
                builderReport.AppendLine($"  tile ({t.tileX},{t.tileY})  orange={t.orange}/256  effect={t.eff}/256");
            return builderReport.ToString().TrimEnd();
        }

        private static string ClassName(byte labelClass) => labelClass switch
        {
            0 => "ground", 1 => "water", 2 => "wall", 3 => "roof", 4 => "deck", 5 => "void",
            6 => "emissive", 7 => "reflfloor", 8 => "mirror", 9 => "ice", 10 => "flow",
            11 => "lava", 12 => "window", 13 => "glass", 14 => "hot", 255 => "unset",
            _ => labelClass.ToString(),
        };

        private static (bool[] bits, int nWater, int nIce, int nFlow, int nLava) WaterBitsFromLabels(byte[] classes)
        {
            var bits = new bool[256];
            int nW = 0, nI = 0, nF = 0, nL = 0;
            for (int pixelIndex = 0; pixelIndex < 256; pixelIndex++)
            {
                byte labelClass = classes[pixelIndex];
                if (labelClass == 1 || labelClass == 14) { bits[pixelIndex] = true; nW++; }   // 14 = hot spring: water, steam comes in v2
                else if (labelClass == 9) { bits[pixelIndex] = true; nI++; }
                else if (labelClass == 10) { bits[pixelIndex] = true; nF++; }
                else if (labelClass == 11) { bits[pixelIndex] = true; nL++; }   // lava: slow molten flow + self-glow
            }
            return (bits, nW, nI, nF, nL);
        }

        // ---- Pass F (SDF) buffers ----

        /// <summary>Two-pass 3-4 chamfer distance transform. d[p] ≈ 3 × (texel distance from
        /// the nearest texel where <paramref name="src"/> == <paramref name="seed"/>).
        /// Approximate (max ~8% error) but exact enough for a shoreline a few texels wide,
        /// and O(n) — the whole mask window costs well under a millisecond on the worker.</summary>
        private static void Chamfer34(bool[] src, bool seed, ushort[] distance, int maskWidth, int maskHeight)
        {
            const int Unreached = 60000;
            int n = maskWidth * maskHeight;
            for (int texelIndex = 0; texelIndex < n; texelIndex++) distance[texelIndex] = src[texelIndex] == seed ? (ushort)0 : (ushort)Unreached;
            for (int y = 0; y < maskHeight; y++)
            {
                int row = y * maskWidth;
                for (int x = 0; x < maskWidth; x++)
                {
                    int texelIndex = row + x;
                    int best = distance[texelIndex];
                    if (x > 0 && distance[texelIndex - 1] + 3 < best) best = distance[texelIndex - 1] + 3;
                    if (y > 0)
                    {
                        int above = texelIndex - maskWidth;
                        if (distance[above] + 3 < best) best = distance[above] + 3;
                        if (x > 0 && distance[above - 1] + 4 < best) best = distance[above - 1] + 4;
                        if (x < maskWidth - 1 && distance[above + 1] + 4 < best) best = distance[above + 1] + 4;
                    }
                    distance[texelIndex] = (ushort)best;
                }
            }
            for (int y = maskHeight - 1; y >= 0; y--)
            {
                int row = y * maskWidth;
                for (int x = maskWidth - 1; x >= 0; x--)
                {
                    int texelIndex = row + x;
                    int best = distance[texelIndex];
                    if (x < maskWidth - 1 && distance[texelIndex + 1] + 3 < best) best = distance[texelIndex + 1] + 3;
                    if (y < maskHeight - 1)
                    {
                        int below = texelIndex + maskWidth;
                        if (distance[below] + 3 < best) best = distance[below] + 3;
                        if (x > 0 && distance[below - 1] + 4 < best) best = distance[below - 1] + 4;
                        if (x < maskWidth - 1 && distance[below + 1] + 4 < best) best = distance[below + 1] + 4;
                    }
                    distance[texelIndex] = (ushort)best;
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
            for (int pixelIndex = 0; pixelIndex < 256; pixelIndex++)
            {
                byte labelClass = classes[pixelIndex];
                bits[pixelIndex] = labelClass == 1 || labelClass == 9 || labelClass == 10 || labelClass == 11 || labelClass == 14 || labelClass == 255;
            }
            return bits;
        }

        /// <summary>OR the label's ice (9) and lava (11) pixels into the tile's per-pixel sub-type
        /// masks, allocating only when there is something to record.</summary>
        private static void AddSubTypePixels(byte[] classes, ref bool[]? iceBits, ref bool[]? lavaBits, ref bool[]? flowBits)
        {
            for (int pixelIndex = 0; pixelIndex < 256; pixelIndex++)
            {
                byte labelClass = classes[pixelIndex];
                if (labelClass == 9) (iceBits ??= new bool[256])[pixelIndex] = true;
                else if (labelClass == 11) (lavaBits ??= new bool[256])[pixelIndex] = true;
                else if (labelClass == 10) (flowBits ??= new bool[256])[pixelIndex] = true;
            }
        }

        /// <summary>Close the ENCLOSED holes in a 16x16 art silhouette: a pixel joins the shape
        /// only where art brackets it within <paramref name="maxGap"/> on BOTH axes. A railing's
        /// slots and a plank seam fill; the open water above and below the art keeps its side of
        /// the outline, so the carve never squares off to the tile grid.</summary>
        private static bool[] FillEnclosedHoles(bool[] bits, int maxGap)
        {
            const int TileTexels = 16;
            var horizontallyEnclosed = new bool[256];
            for (int y = 0; y < TileTexels; y++)
            {
                int last = -99;
                for (int x = 0; x < TileTexels; x++)
                {
                    if (!bits[y * TileTexels + x]) continue;
                    if (x - last > 1 && x - last <= maxGap + 1)
                        for (int k = last + 1; k < x; k++) horizontallyEnclosed[y * TileTexels + k] = true;
                    last = x;
                }
            }
            var filled = (bool[])bits.Clone();
            for (int x = 0; x < TileTexels; x++)
            {
                int last = -99;
                for (int y = 0; y < TileTexels; y++)
                {
                    if (!bits[y * TileTexels + x]) continue;
                    if (y - last > 1 && y - last <= maxGap + 1)
                        for (int k = last + 1; k < y; k++)
                            if (horizontallyEnclosed[k * TileTexels + x]) filled[k * TileTexels + x] = true;
                    last = y;
                }
            }
            return filled;
        }

        /// <summary>True when a label EXISTS for this overlay tile and calls every pixel ground.
        /// Only meaningful over a water tile, where the art is the thing standing on the water.</summary>
        private static bool OverlayIsGround(LabelStore? labels, xTile.Layers.Layer? layer, int tileX, int tileY, bool isWater)
        {
            if (!isWater || labels == null || layer == null)
                return false;
            byte[]? label = labels.Get(layer, tileX, tileY);
            return label != null && CountLiquid(label) == 0;
        }

        /// <summary>How many of the 256 labels call this pixel liquid. Zero means the author
        /// deliberately said "all ground here", which is a fact, not the absence of one.</summary>
        private static int CountLiquid(byte[] classes)
        {
            int liquidCount = 0;
            for (int pixelIndex = 0; pixelIndex < 256; pixelIndex++)
            {
                byte labelClass = classes[pixelIndex];
                if (labelClass == 1 || labelClass == 9 || labelClass == 10 || labelClass == 11 || labelClass == 14) liquidCount++;
            }
            return liquidCount;
        }

        // ---- location-wide water body sizes (main thread builds, worker reads) ----
        private SurfaceMap? _bodySizeSourceSurfaceMap;
        private int _bodySizeEpoch = -1;
        private int[]? _bodyTileCounts;      // per map tile: how many tiles its water body holds (0 = not water)
        private int _bodyGridWidth, _bodyGridHeight;
        private int[]? _bodySizeFloodStack;

        /// <summary>
        /// How many tiles the water body at each MAP tile holds, for the whole location.
        /// <para>
        /// Wave and glint strength scale down for a small pool, and "small" has to be a property of
        /// the pool. It used to be measured by flood-filling inside the mask window, with any body
        /// touching the window edge counted as full size on the grounds that it probably continued
        /// off-window. The window moves with the camera: walk one tile and a tide pool that had been
        /// wholly inside it starts touching its edge, so the pool's ripple and its glints doubled in
        /// a single frame with no fade, then halved again on the way back. That is the flicker
        /// reported around beach pools, and no amount of fading would have fixed it, because the
        /// input itself was wrong.
        /// </para>
        /// The location's surface grid already answers "is this tile water" for the entire map, so
        /// flood-fill that once per visit instead. The answer is the same wherever the camera is.
        /// Cached on the surface grid's identity plus the mask epoch, which together cover a warp,
        /// a map re-patched in place, and a fish pond appearing or being removed.
        /// </summary>
        private int[]? RefreshLocationBodySizes(SurfaceMap? surfaceMap, List<Rectangle>? pondRects)
        {
            if (surfaceMap == null || surfaceMap.Width <= 0 || surfaceMap.Height <= 0)
            {
                _bodyTileCounts = null; _bodySizeSourceSurfaceMap = null; _bodySizeEpoch = -1;
                return null;
            }
            if (ReferenceEquals(surfaceMap, _bodySizeSourceSurfaceMap) && _bodySizeEpoch == MaskEpoch && _bodyTileCounts != null)
                return _bodyTileCounts;

            int gridWidth = surfaceMap.Width, gridHeight = surfaceMap.Height, n = gridWidth * gridHeight;
            var grid = _bodyTileCounts != null && _bodyTileCounts.Length >= n ? _bodyTileCounts : new int[n];
            Array.Clear(grid, 0, n);
            // -1 marks "water, size not counted yet". Fish ponds join in: they are water the mask
            // draws but the map data has never heard of, and a pond is small enough for the size
            // rule to matter.
            for (int y = 0; y < gridHeight; y++)
                for (int x = 0; x < gridWidth; x++)
                    if (surfaceMap.IsWater(x, y)) grid[y * gridWidth + x] = -1;
            if (pondRects != null)
                foreach (var r in pondRects)
                    for (int y = Math.Max(0, r.Top); y < Math.Min(gridHeight, r.Bottom); y++)
                        for (int x = Math.Max(0, r.Left); x < Math.Min(gridWidth, r.Right); x++)
                            grid[y * gridWidth + x] = -1;

            if (_bodySizeFloodStack == null || _bodySizeFloodStack.Length < n) _bodySizeFloodStack = new int[n];
            var stack = _bodySizeFloodStack;
            var member = new List<int>(256);
            for (int start = 0; start < n; start++)
            {
                if (grid[start] != -1)
                    continue;
                int stackTop = 0; stack[stackTop++] = start; grid[start] = 0; member.Clear();
                while (stackTop > 0)
                {
                    int current = stack[--stackTop]; member.Add(current);
                    int currentX = current % gridWidth, currentY = current / gridWidth;
                    if (currentX > 0 && grid[current - 1] == -1) { grid[current - 1] = 0; stack[stackTop++] = current - 1; }
                    if (currentX < gridWidth - 1 && grid[current + 1] == -1) { grid[current + 1] = 0; stack[stackTop++] = current + 1; }
                    if (currentY > 0 && grid[current - gridWidth] == -1) { grid[current - gridWidth] = 0; stack[stackTop++] = current - gridWidth; }
                    if (currentY < gridHeight - 1 && grid[current + gridWidth] == -1) { grid[current + gridWidth] = 0; stack[stackTop++] = current + gridWidth; }
                }
                int size = member.Count;
                foreach (int tileIndex in member)
                    grid[tileIndex] = size;
            }
            // The calm rule reads a nine-tile body as a still puddle and turns its ripple, its
            // glints and every reflection in it down to about half. A fish pond is nine tiles of
            // water the game animates exactly as it animates the lake, with fish in it, and at half
            // strength it read as untouched vanilla water beside a lake wearing the full effect.
            // It is scored as a body big enough to be calm about nothing.
            if (pondRects != null)
                foreach (var r in pondRects)
                    for (int y = Math.Max(0, r.Top); y < Math.Min(gridHeight, r.Bottom); y++)
                        for (int x = Math.Max(0, r.Left); x < Math.Min(gridWidth, r.Right); x++)
                            grid[y * gridWidth + x] = Math.Max(grid[y * gridWidth + x], 36);

            _bodyTileCounts = grid; _bodyGridWidth = gridWidth; _bodyGridHeight = gridHeight;
            _bodySizeSourceSurfaceMap = surfaceMap; _bodySizeEpoch = MaskEpoch;
            return grid;
        }

        /// <summary>Gather stage - read every game-state dependency into plain arrays.
        /// MUST run on the main thread (content loads, texture GetData via the
        /// classification caches, live entity lists).</summary>
        /// <summary>
        /// Everything the per-tile gather reads that is the same for every tile on the map: the
        /// label pack, the height map, and the map layers it has to look through. Built once
        /// before the sweep, because eight values threaded through a 400-line loop body as
        /// parameters is a signature nobody reads.
        /// </summary>
        private readonly struct TileGatherContext
        {
            public readonly LabelStore? Labels;
            public readonly SurfaceMap? SurfaceMap;
            /// <summary>Back-family layers: the ground itself.</summary>
            public readonly List<xTile.Layers.Layer>? Backs;
            /// <summary>Buildings-family layers: art that stands ON the ground.</summary>
            public readonly List<xTile.Layers.Layer>? BuildingsLayers;
            /// <summary>AlwaysFront, plus every Front layer after the first.</summary>
            public readonly List<xTile.Layers.Layer>? Always;
            /// <summary>Every Front-family layer, for the passes that must union all of them.</summary>
            public readonly List<xTile.Layers.Layer>? Fronts;
            /// <summary>The first Front layer, which the single-layer lookups use.</summary>
            public readonly xTile.Layers.Layer? Front;
            /// <summary>The whole location is lava, so unlabelled liquid there is lava, not water.</summary>
            public readonly bool LocationIsLava;

            public TileGatherContext(LabelStore? labels, SurfaceMap? surfaceMap,
                List<xTile.Layers.Layer>? backLayers, List<xTile.Layers.Layer>? buildingsLayers,
                List<xTile.Layers.Layer>? always, List<xTile.Layers.Layer>? fronts,
                xTile.Layers.Layer? front, bool locationIsLava)
            {
                Labels = labels; SurfaceMap = surfaceMap;
                Backs = backLayers; BuildingsLayers = buildingsLayers; Always = always; Fronts = fronts; Front = front;
                LocationIsLava = locationIsLava;
            }
        }


        /// <summary>A gather between two of its tiles: everything the per-tile step needs, and how
        /// far it has got.
        ///
        /// <para>The window gather runs Begin, every tile, Finish in one call and never sees this
        /// as anything but a local. The whole-map ANCHOR gather is the reason it exists: on a
        /// 156x65 farm that gather is 18 to 23 ms on the main thread, and it was taken in one
        /// frame the moment the player stood still, on the theory that a resting player feels
        /// nothing. In split screen the other player is walking through that frame, and a
        /// 20 ms hitch is a step that does not register. Kept here, the anchor gather is walked
        /// a slice at a time (<see cref="AnchorGatherBudgetMilliseconds"/> per resting frame) and
        /// dispatched when it is done; the tiles it has already answered are in the map memory,
        /// so a gather abandoned halfway costs less to start again.</para></summary>
        private sealed class GatherInProgress
        {
            public WaterMaskJob Job = null!;
            public TileGatherContext Context, PondContext;
            public GatheredTileAnswers? Remembered;
            public List<Rectangle>? PondRects;
            public List<StardewValley.Buildings.FishPond>? Ponds;
            public int NextTileIndex;
            /// <summary>The scratch generation this gather owns; another gather starting bumps it
            /// and this one must not write another tile.</summary>
            public int Generation;
            public int Slices;
            public double TotalMilliseconds, WorstSliceMilliseconds;
        }

        /// <summary>Bumped by every gather that starts. The gather writes the shared scratch
        /// buffers tile by tile, so a gather that is resumed later has to know nobody else wrote
        /// them in between.</summary>
        private int _gatherGeneration;

        private WaterMaskJob GatherWaterMask(GameLocation location, int startTileX, int startTileY, int tilesWide, int tilesHigh)
        {
            GatherInProgress gather = BeginGather(location, startTileX, startTileY, tilesWide, tilesHigh);
            GatherTilesUntil(gather, long.MaxValue);
            return FinishGather(gather);
        }

        /// <summary>The first half of a gather: the job, the water flags for every tile, the layer
        /// lists and the contexts the per-tile step reads. Nothing per tile yet.</summary>
        private GatherInProgress BeginGather(GameLocation location, int startTileX, int startTileY, int tilesWide, int tilesHigh)
        {
            _gatherGeneration++;
            int count = tilesWide * tilesHigh;
            var job = new WaterMaskJob
            {
                Location = location, StartTileX = startTileX, StartTileY = startTileY,
                TileWidth = tilesWide, TileHeight = tilesHigh, WaterDrawHookVersion = WaterDrawHook.Version,
                LabelVersion = CurrentLabelVersion(), Epoch = MaskEpoch,
                // Snapshot the location-wide waterline anchor if it is still valid for
                // exactly this identity — the worker reads it lock-free (immutable).
                Anchor = AnchorFresh(location) ? _waterlineAnchorData : null,
            };

            // The surface grid classifies the actual water SURFACE: ponds and beach tide pools
            // count as water (they reflect too), while pier/bridge DECKS over water do not — no
            // reflection is painted onto planks. Built once per location visit.
            var surfaceMap = SurfaceMap.For(location);
            // Ground-truth labels ship WITH this mod (labels/), read once at startup — nothing
            // here touches the disk or depends on another mod being installed.
            var labels = LabelStore.Instance;
            if (labels is { Any: false }) labels = null;
            // The Desert never has waterTiles (the game excludes it by class in loadMap): its
            // pond is decorative art the game draws no overlay on, so no GAME water lives here,
            // whatever the tile properties say. The pond still enters the mask through its
            // LABELS, like any labelled water - this veto only keeps it off the game-water road.
            bool desert = location is StardewValley.Locations.Desert;
            // Fish ponds draw their own water in the sorted-sprite pass — never in waterTiles,
            // never a Back "Water" property. Their water is the interior of the footprint
            // (the 1-tile rim is masonry, per FishPond.isTileFishable).
            List<Rectangle>? pondRects = null;
            List<StardewValley.Buildings.FishPond>? ponds = null;
            foreach (var building in location.buildings)
            {
                if (building is StardewValley.Buildings.FishPond fishPond && fishPond.daysOfConstructionLeft.Value <= 0)
                {
                    (pondRects ??= new()).Add(new Rectangle(
                        fishPond.tileX.Value + 1, fishPond.tileY.Value + 1,
                        Math.Max(0, fishPond.tilesWide.Value - 2), Math.Max(0, fishPond.tilesHigh.Value - 2)));
                    (ponds ??= new()).Add(fishPond);
                }
            }
            // Body sizes for the calm factor, measured over the whole map rather than the window.
            job.BodyTileCounts = RefreshLocationBodySizes(surfaceMap, pondRects);
            job.PondRects = pondRects;
            job.BodyGridWidth = _bodyGridWidth;
            job.BodyGridHeight = _bodyGridHeight;

            if (_waterTileFlags == null || _waterTileFlags.Length < count) _waterTileFlags = new bool[count];
            bool hasAnyWater = false;
            for (int j = 0; j < tilesHigh; j++)
            {
                for (int i = 0; i < tilesWide; i++)
                {
                    int tileX = startTileX + i, tileY = startTileY + j;
                    bool water = !desert && (surfaceMap != null ? surfaceMap.IsWater(tileX, tileY) : location.isWaterTile(tileX, tileY));
                    // Draw-call truth: the game DREW water here but the tile data doesn't know it
                    // (a location/mod with custom drawWater logic). Only when isWaterTile is false —
                    // isWaterTile-true tiles keep their pipeline above, so HF's deck-over-water veto
                    // is never overridden by the hook.
                    if (!water && !desert && !location.isWaterTile(tileX, tileY) && WaterDrawHook.WasDrawn(location, tileX, tileY))
                        water = true;
                    if (!water && pondRects != null)
                    {
                        foreach (var r in pondRects)
                            if (r.Contains(tileX, tileY)) { water = true; break; }
                    }
                    if (water) hasAnyWater = true;
                    _waterTileFlags[j * tilesWide + i] = water;
                }
            }
            job.AnyWater = hasAnyWater;

            // 1.6 maps can carry SEVERAL layers per family (Back2, Buildings3, Front-less
            // AlwaysFront4 ...), and Dynamic Reflections' issue tracker is full of maps whose
            // water art lives on Back2 (coral-reef beaches). Collect every RENDERED layer per
            // family: the family name plus a digits-only suffix — "Back-1" is the Tiled
            // convention for a DISABLED layer and must stay out (see MapLayers.BelongsToFamily).
            List<xTile.Layers.Layer>? backLayers = null, buildingsLayers = null, always = null;
            List<xTile.Layers.Layer>? fronts = null;
            if (location.map != null)
            {
                foreach (var layer in location.map.Layers)
                {
                    if (MapLayers.BelongsToFamily(layer.Id, "AlwaysFront")) (always ??= new()).Add(layer);
                    else if (MapLayers.BelongsToFamily(layer.Id, "Back")) (backLayers ??= new()).Add(layer);
                    else if (MapLayers.BelongsToFamily(layer.Id, "Buildings")) (buildingsLayers ??= new()).Add(layer);
                    else if (MapLayers.BelongsToFamily(layer.Id, "Front")) (fronts ??= new()).Add(layer);
                }
                // Declaration order is not the draw order everywhere: a map may declare Front2
                // before Front or Back before Back-1. Sort each bucket by the one shared key so
                // "fronts[0] = the lowest Front" stays true, matching the labeler and the dump.
                backLayers?.Sort(MapLayers.CompareLayerRank);
                buildingsLayers?.Sort(MapLayers.CompareLayerRank);
                fronts?.Sort(MapLayers.CompareLayerRank);
                always?.Sort(MapLayers.CompareLayerRank);
            }
            var front = fronts is { Count: > 0 } ? fronts[0] : null;
            // Extra Front layers (Front2 ...) carve exactly like AlwaysFront: over-player art.
            if (fronts is { Count: > 1 })
                for (int k = 1; k < fronts.Count; k++)
                    (always ??= new()).Add(fronts[k]);

            EnsureGatherBuffers(count);

            // Volcano interiors hold lava, not water. The lava sub-class (slow molten flow,
            // self-glow, no mirror) otherwise only triggers on painted label class 11, which
            // ships dormant — so vanilla lava rendered as ordinary water, complete with a
            // mirror reflection. Tag it from the location instead so it reads as lava out of
            // the box; a painted label still wins per tile below.
            string locationName = location.NameOrUniqueName ?? location.Name ?? "";
            bool locationIsLava = location is StardewValley.Locations.VolcanoDungeon
                || locationName.Contains("Caldera", StringComparison.OrdinalIgnoreCase)
                || locationName.Contains("Volcano", StringComparison.OrdinalIgnoreCase)
                // Mine floors 80-119: the game reuses the water overlay tinted Red*0.8 for lava
                // (decompiled MineShaft.loadLevel) — same machinery, molten look.
                || (location is StardewValley.Locations.MineShaft mineShaft && mineShaft.getMineArea() == 80);

            var context = new TileGatherContext(labels, surfaceMap, backLayers, buildingsLayers, always, fronts, front, locationIsLava);
            // A fish pond's water is drawn by the building, over whatever the map has there. The
            // ground under it is ordinary farm dirt or grass, and on most maps that art carries a
            // label that calls all 256 of its pixels ground. The label rule ("what the author
            // painted non-liquid is carved, even on a tile the game calls water", written for a
            // bank ledge painted over a river) then carved the whole pond away: effect 0/256, the
            // pond drawn as the game draws it and nothing of ours on it. Whether a pond had any
            // water at all depended on whether somebody had labelled the dirt under it. Inside a
            // pond there is no map to read, so its tiles are gathered through a context that
            // carries no labels and no layers: the tile fills whole, as the game's own overlay does.
            // The tile is also remembered as a pond tile for Pass E, which tags its alpha VESSEL.
            var pondContext = new TileGatherContext(null, surfaceMap, null, null, null, null, null, locationIsLava);
            if (_maskScratch.TilePondFlags == null || _maskScratch.TilePondFlags.Length < count) _maskScratch.TilePondFlags = new bool[count];

            // The map-wide memory of earlier gathers (RenderPipeline.WaterMask.GatherCache.cs). A
            // tile is copied from it when it was gathered under the same water verdict; anything
            // else, and every pond tile, is asked of the game as before and then remembered.
            GatheredTileAnswers? remembered = GatherCacheEnabled ? EnsureGatheredTileAnswers(location, surfaceMap) : null;
            return new GatherInProgress
            {
                Job = job, Context = context, PondContext = pondContext, Remembered = remembered,
                PondRects = pondRects, Ponds = ponds, Generation = _gatherGeneration,
            };
        }

        /// <summary>The per-tile half of a gather, from where it left off until every tile is done
        /// or the clock reaches <paramref name="deadlineTimestamp"/>. True when the last tile is in.</summary>
        private bool GatherTilesUntil(GatherInProgress gather, long deadlineTimestamp)
        {
            WaterMaskJob job = gather.Job;
            TileGatherContext context = gather.Context, pondContext = gather.PondContext;
            GatheredTileAnswers? remembered = gather.Remembered;
            List<Rectangle>? pondRects = gather.PondRects;
            List<StardewValley.Buildings.FishPond>? ponds = gather.Ponds;
            int tilesWide = job.TileWidth, count = tilesWide * job.TileHeight;
            int startTileX = job.StartTileX, startTileY = job.StartTileY;
            for (int tileIndex = gather.NextTileIndex; tileIndex < count; tileIndex++)
            {
                // The clock is asked once every few tiles, not every tile: a tile is a few
                // microseconds and the timestamp is not free.
                if ((tileIndex & 15) == 0 && tileIndex != gather.NextTileIndex && System.Diagnostics.Stopwatch.GetTimestamp() >= deadlineTimestamp)
                {
                    gather.NextTileIndex = tileIndex;
                    return false;
                }
                int i = tileIndex % tilesWide, j = tileIndex / tilesWide;
                bool isWater = _waterTileFlags![tileIndex];
                int tileX = startTileX + i, tileY = startTileY + j;
                bool inPond = InsideFishPond(pondRects, tileX, tileY);
                // The rim tiles: FishPond.draw paints its water half a tile in under the stones
                // on every side, so the water the player sees is wider than the interior. Those
                // tiles are pond tiles too, with only the texels the game paints water on; the
                // stones over them are carved by the building stamp in the sprite mask.
                var rimOf = inPond ? null : FishPondRimOwning(ponds, tileX, tileY);
                bool pondTile = inPond || rimOf != null;
                _maskScratch.TilePondFlags![tileIndex] = pondTile;
                int cell = remembered != null && !pondTile && tileX >= 0 && tileY >= 0 && tileX < remembered.Width && tileY < remembered.Height
                    ? tileY * remembered.Width + tileX : -1;
                if (cell >= 0)
                {
                    ushort known = remembered!.Flags[cell];
                    int identity = TileIdentity(context, tileX, tileY);
                    if ((known & GatheredFilled) != 0 && ((known & GatheredIsWater) != 0) == isWater
                        && remembered.Identity[cell] == identity)
                    {
                        CopyGatheredTile(job, remembered, cell, tileIndex);
                        _gatherCacheCopied++;
                        continue;
                    }
                    // AnyLabeled is the one thing GatherTile reports on the job rather than per
                    // tile; read this tile's own contribution off it so the memory can replay it.
                    bool labeledBefore = job.AnyLabeled;
                    job.AnyLabeled = false;
                    GatherTile(job, context, tileIndex, tileX, tileY, isWater);
                    StoreGatheredTile(remembered, cell, tileIndex, isWater, job.AnyLabeled, identity);
                    job.AnyLabeled |= labeledBefore;
                    _gatherCacheGathered++;
                    continue;
                }
                GatherTile(job, pondTile ? pondContext : context, tileIndex, tileX, tileY, isWater);
                _gatherCacheGathered++;
                if (rimOf != null)
                    _maskScratch.TileEffectBits![tileIndex] = FishPondWaterBits(rimOf, tileX, tileY);
            }
            gather.NextTileIndex = count;
            return true;
        }

        /// <summary>The last half: the entities standing in the window, then the job is ready
        /// for its compose.</summary>
        private WaterMaskJob FinishGather(GatherInProgress gather)
        {
            GatherEntityCarveRects(gather.Job.Location);
            return gather.Job;
        }

        /// <summary>Everything the gather works out about ONE tile: which of its pixels are
        /// liquid, what art stands on it, and which of that art carves the water back out.
        /// Results land in <see cref="_maskScratch"/> at <paramref name="idx"/>.</summary>
        /// <summary>The pond whose rim ring this tile is, or null. The interior is answered by
        /// <see cref="InsideFishPond"/> first, so a hit here is always a rim tile.</summary>
        private static StardewValley.Buildings.FishPond? FishPondRimOwning(List<StardewValley.Buildings.FishPond>? ponds, int tileX, int tileY)
        {
            if (ponds == null)
                return null;
            foreach (var pond in ponds)
                if (tileX >= pond.tileX.Value && tileX < pond.tileX.Value + pond.tilesWide.Value
                    && tileY >= pond.tileY.Value && tileY < pond.tileY.Value + pond.tilesHigh.Value)
                    return pond;
            return null;
        }

        /// <summary>The water FishPond.draw paints, in world pixels: from half a tile inside the left
        /// edge to half a tile inside the right, from half a tile below the top edge to five pixels
        /// short of the bottom. Read off the game's draw, not guessed from the footprint.</summary>
        private static Rectangle FishPondWaterPixels(StardewValley.Buildings.FishPond pond)
            => new Rectangle(pond.tileX.Value * 64 + 32, pond.tileY.Value * 64 + 32,
                             (pond.tilesWide.Value - 1) * 64, pond.tilesHigh.Value * 64 - 32 - 5);

        /// <summary>Which of a rim tile's 256 texels the pond's water is painted on.</summary>
        private static bool[] FishPondWaterBits(StardewValley.Buildings.FishPond pond, int tileX, int tileY)
        {
            Rectangle water = FishPondWaterPixels(pond);
            const int Texels = MaskTexelsPerTile;
            const int pixelsPerTexel = 64 / Texels;
            var bits = new bool[Texels * Texels];
            for (int texelY = 0; texelY < Texels; texelY++)
                for (int texelX = 0; texelX < Texels; texelX++)
                    bits[texelY * Texels + texelX] = water.Contains(tileX * 64 + texelX * pixelsPerTexel + pixelsPerTexel / 2,
                                                       tileY * 64 + texelY * pixelsPerTexel + pixelsPerTexel / 2);
            return bits;
        }

        private static bool InsideFishPond(List<Rectangle>? pondRects, int tileX, int tileY)
        {
            if (pondRects == null)
                return false;
            foreach (var pond in pondRects)
                if (pond.Contains(tileX, tileY))
                    return true;
            return false;
        }

        private void GatherTile(WaterMaskJob job, TileGatherContext context, int tileIndex, int tileX, int tileY, bool isWater)
        {
            bool[]? bits = null;
            int iceCount = 0, flowCount = 0, lavaCount = 0;   // accumulated across Back + Buildings ctx.Labels
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
            // Per-PIXEL sub-type, collected from the same ctx.Labels as the counts. The counts
            // decide the tile's fallback; these decide each pixel, which is what a
            // half-frozen tile needs: #1269 is 184 ice pixels and 72 water, and a whole-tile
            // verdict froze all 256, so the ripple stopped dead on a tile boundary and the
            // river showed square patches. Null when nothing here is labelled.
            bool[]? iceBits = null, lavaBits = null, flowBits = null;
            if (context.Labels != null && context.Backs != null)
            {
                // Topmost Back-family label wins (Back2 draws over Back).
                byte[]? backLabel = null;
                foreach (var buildingsLayerLoop in context.Backs)
                {
                    byte[]? layerLabelAgain = context.Labels.Get(buildingsLayerLoop, tileX, tileY);
                    if (layerLabelAgain != null) backLabel = layerLabelAgain;
                }
                if (backLabel != null)
                {
                    labeledBack = !isWater;
                    var (labelWaterBits, nW, nI, nF, nL) = WaterBitsFromLabels(backLabel);
                    if (nI > 0 || nL > 0 || nF > 0) AddSubTypePixels(backLabel, ref iceBits, ref lavaBits, ref flowBits);
                    if (isWater)
                    {
                        // Subtract only what the author explicitly painted non-liquid;
                        // unpainted pixels keep the surface, so a half-painted label can
                        // never erase a lake (the old >7-liquid guard is obsolete).
                        keep = KeepBitsFromLabels(backLabel);
                        iceCount += nI; flowCount += nF; lavaCount += nL;
                        job.AnyLabeled = true;
                    }
                    else if (nW + nI + nF + nL > 0)
                    {
                        bits = labelWaterBits;
                        iceCount += nI; flowCount += nF; lavaCount += nL;
                        job.AnyLabeled = true;
                    }
                }
            }
            // V4: no colour classification, ever. Where the game says water and no label
            // refines it, the tile fills whole — exactly the coverage vanilla's own overlay
            // draws, which can never spill onto land the game didn't flood. Per-pixel
            // truth comes from ctx.Labels alone (97% of game-water art is labelled). The old
            // ctx.Surf/anim/puddle colour paths (WaterColor H2/H3, foam H4, PuddleBits H5) are
            // gone: they are what put ripple on snow, sand and grass in every recolor.
            // Buildings family: the first layer with art supplies the primary art
            // (t1/s1 — the label-vs-opacity overrides below key off it); every further
            // layer's opacity is UNIONED into the carve, and the topmost label wins.
            bool hasBuildingsArt = false;
            Texture2D t1 = null!; Rectangle s1 = default; byte o1 = 0;
            (bool[] bits, int count) buildingsCarveUnion = (null!, 0);
            byte[]? buildingsLabel = null;
            if (context.BuildingsLayers != null)
            {
                foreach (var buildingsLayerLoop in context.BuildingsLayers)
                {
                    if (TryTileArt(buildingsLayerLoop, tileX, tileY, out var tb, out var srcRect, out _, out byte bOri))
                    {
                        var solid = SolidBits(tb, srcRect, bOri);
                        if (!hasBuildingsArt) { hasBuildingsArt = true; t1 = tb; s1 = srcRect; o1 = bOri; buildingsCarveUnion = solid; }
                        else if (solid.count > 0)
                        {
                            var merged = new bool[256];
                            for (int texelIndex = 0; texelIndex < 256; texelIndex++) merged[texelIndex] = (buildingsCarveUnion.bits?[texelIndex] ?? false) || solid.bits[texelIndex];
                            buildingsCarveUnion = (merged, Math.Max(buildingsCarveUnion.count, solid.count));
                        }
                    }
                    if (context.Labels != null)
                    {
                        byte[]? layerLabelAgain = context.Labels.Get(buildingsLayerLoop, tileX, tileY);
                        if (layerLabelAgain != null) buildingsLabel = layerLabelAgain;
                    }
                }
            }
            // Buildings-layer overlay water: ctx.Labels only (a labelled fountain rim /
            // ctx.Surf overlay needs no animation).
            bool[]? overlayBits = null;
            if (!isWater)
            {
                byte[]? backLabel = buildingsLabel;
                if (backLabel != null)
                {
                    var (overlayWaterBits, nW, nI, nF, nL) = WaterBitsFromLabels(backLabel);
                    if (nW + nI + nF + nL >= 8)
                    {
                        overlayBits = overlayWaterBits;
                        iceCount += nI; flowCount += nF; lavaCount += nL;
                        if (nI > 0 || nL > 0 || nF > 0) AddSubTypePixels(backLabel, ref iceBits, ref lavaBits, ref flowBits);
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
                    for (int texelIndex = 0; texelIndex < 256; texelIndex++) merged[texelIndex] = bits[texelIndex] || overlayBits[texelIndex];
                    bits = merged;
                }
            }
            _maskScratch.TileEffectBits![tileIndex] = bits;
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
            bool hasFront = TryTileArt(context.Front, tileX, tileY, out var t2, out var s2, out _, out byte fOri);
            _maskScratch.TileHasBuildingArtFlags![tileIndex] = hasBuildingsArt;
            _maskScratch.TileBuildingGroundOverlayFlags![tileIndex] = false;   // buffers are reused frame to frame
            _maskScratch.TileFrontGroundOverlayFlags![tileIndex] = false;
            var cb = buildingsCarveUnion;   // union of every Buildings-family layer's opacity
            // Front and AlwaysFront carve, and the low-alpha union that gates the carve lift.
            BuildFrontCarve(context, tileX, tileY, waterHere, hasFront, t2, s2, fOri,
                            out bool frontArt, out bool frontAllGround, out bool[]? frontCarveBits,
                            out bool[]? frontAnyAlphaBits, out int frontSolidCount);
            // Only when EVERY overlay here is labelled ground: one unlabelled layer, or one
            // that carries liquid, and the march keeps its say (a bridge on Front must still
            // hang a reflection, and that is decided by the deck/structure path).
            _maskScratch.TileFrontGroundOverlayFlags[tileIndex] = frontArt && frontAllGround;
            _maskScratch.TileHasFrontArtFlags![tileIndex] = frontCarveBits != null;
            _maskScratch.TileBuildingCarveBits![tileIndex] = hasBuildingsArt ? cb.bits : null;
            _maskScratch.TileFrontCarveBits![tileIndex] = frontCarveBits;
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
            CarveGroundLabelledOverlay(context, tileIndex, tileX, tileY, waterHere, hasBuildingsArt, buildingsLabel, t1, s1, o1);
            ApplyBuildingLabelOverride(job, context, tileIndex, waterHere, hasBuildingsArt, cb, buildingsLabel,
                                       ref keep, ref iceBits, ref lavaBits, ref flowBits,
                                       ref iceCount, ref flowCount, ref lavaCount, ref bldLabeledLiquid);
            ApplyFrontLabelOverride(job, context, tileIndex, tileX, tileY, isWater, frontCarveBits, frontAnyAlphaBits,
                                    ref iceBits, ref lavaBits, ref flowBits,
                                    ref iceCount, ref flowCount, ref lavaCount,
                                    ref frontSolidCount, ref frontLabeledLiquid);
            UnionPaintedLiquid(context, tileX, tileY, isWater, ref keep);
            _maskScratch.TileWaterKeepBits![tileIndex] = keep;
            _maskScratch.TileIceBits![tileIndex] = iceBits;
            _maskScratch.TileLavaBits![tileIndex] = lavaBits;
            _maskScratch.TileFlowBits![tileIndex] = flowBits;
            // Ice / flowing win over each other by pixel count; a plain-water majority
            // keeps normal behaviour. Ice → reflection but no ripple (mask alpha 0);
            // flowing → ripple but no reflection (scrubbed from the march channel).
            _maskScratch.TileIceFlags![tileIndex] = iceCount > 0 && iceCount >= flowCount && iceCount >= lavaCount;
            _maskScratch.TileFlowFlags![tileIndex] = flowCount > 0 && flowCount > iceCount && flowCount >= lavaCount;
            // A volcano location is lava unless a label says this tile is something else.
            _maskScratch.TileLavaFlags![tileIndex] = (lavaCount > 0 && lavaCount > iceCount && lavaCount > flowCount)
                || (context.LocationIsLava && iceCount == 0 && flowCount == 0);
            // DECK tiles (walkable piers / plank bridges) block as whole tiles too: the
            // beach plank's art has a painted wet stain that classified as water, punching
            // a 2-texel channel through the deck — and the ±10 shoreline smoothing then
            // dragged the anchors of a full tile around it up above the plank (reflection
            // missing on that side).
            bool deck = context.SurfaceMap != null && context.SurfaceMap.GetSurface(tileX, tileY) == SurfaceClass.Deck;
            _maskScratch.TileDeckFlags![tileIndex] = deck;
            _maskScratch.TileLargeSolidFlags![tileIndex] = deck || (hasBuildingsArt && cb.count >= 230 && !bldLabeledLiquid) || frontSolidCount >= 230;
            // A tile whose overlay art is LABELLED liquid has already been resolved per
            // pixel above: the carve keeps exactly the painted liquid and cuts exactly the
            // rest. Pass C's whole-tile march scrub must not run on top of that, or the
            // pixel-accurate waterline we just built is thrown away and the anchor snaps
            // back to the tile grid. Unlabelled tiles keep the tile-level verdict, so maps
            // nobody has painted behave exactly as before.
            _maskScratch.TileLabeledLiquidFlags![tileIndex] = bldLabeledLiquid || frontLabeledLiquid;
        }

        /// <summary>
        /// A label on the Buildings overlay beats that art's opacity. A bridge's cast shadow is
        /// opaque art drawn across the river and Pass C would punch its exact rectangle out of
        /// the effect channel, but a shadow is still water and has to keep rippling.
        /// </summary>
        private void ApplyBuildingLabelOverride(WaterMaskJob job, TileGatherContext context, int tileIndex,
                                                bool waterHere, bool hasBuildingsArt,
                                                (bool[] bits, int count) cb, byte[]? buildingsLabel,
                                                ref bool[]? keep,
                                                ref bool[]? iceBits, ref bool[]? lavaBits, ref bool[]? flowBits,
                                                ref int iceCount, ref int flowCount, ref int lavaCount,
                                                ref bool bldLabeledLiquid)
        {
                if (waterHere && hasBuildingsArt && cb.bits != null && !_maskScratch.TileBuildingGroundOverlayFlags![tileIndex])
                {
                    byte[]? overlayLabel = buildingsLabel;
                    if (overlayLabel != null)
                    {
                        var (overlayWaterBits, oW, oI, oF, oL) = WaterBitsFromLabels(overlayLabel);
                        var k = keep != null ? (bool[])keep.Clone() : null;
                        if (k == null)
                        {
                            k = new bool[256];
                            for (int texelIndex = 0; texelIndex < 256; texelIndex++) k[texelIndex] = true;
                        }
                        for (int texelIndex = 0; texelIndex < 256; texelIndex++)
                            if (cb.bits[texelIndex] && !overlayWaterBits[texelIndex]) k[texelIndex] = false;
                        keep = k;
                        iceCount += oI; flowCount += oF; lavaCount += oL;
                        if (oI > 0 || oL > 0 || oF > 0) AddSubTypePixels(overlayLabel, ref iceBits, ref lavaBits, ref flowBits);
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
                            for (int texelIndex = 0; texelIndex < 256; texelIndex++)
                                if (overlayWaterBits[texelIndex]) carve[texelIndex] = false;
                            _maskScratch.TileBuildingCarveBits![tileIndex] = carve;
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
        }

        /// <summary>
        /// A label on a Front-family layer beats that layer's own opacity: where some layer both
        /// paints a pixel liquid and draws art there, the pixel comes back out of the carve, on
        /// the Buildings channel as well as the Front one.
        /// </summary>
        private void ApplyFrontLabelOverride(WaterMaskJob job, TileGatherContext context, int tileIndex,
                                             int tileX, int tileY, bool isWater, bool[]? frontCarveBits,
                                             bool[]? frontAnyAlphaBits,
                                             ref bool[]? iceBits, ref bool[]? lavaBits, ref bool[]? flowBits,
                                             ref int iceCount, ref int flowCount, ref int lavaCount,
                                             ref int frontSolidCount, ref bool frontLabeledLiquid)
        {
            // FoldFrontLiquid below is a local function, and a local function may not use a ref
            // parameter, so the sub-type buffers travel as plain locals and go back at the end.
            bool[]? ice = iceBits, lava = lavaBits, flow = flowBits;
                // Same override for the FRONT / ALWAYSFRONT carve. Cast shadows and overhang art
                // land there just as often as on Buildings, and a label saying "this is still
                // water" has to beat opacity on every layer or the rule only half works.
                if (isWater && context.Labels != null && (frontCarveBits != null || frontAnyAlphaBits != null))
                {
                    // Each ctx.Front-family layer's label answers for ITS OWN art, gated by that
                    // art's visible alpha (>= 32; the 128-opaque bar re-carved a falls'
                    // semi-transparent spray). The old "topmost ctx.Front-family label wins" let
                    // a cliff-top overhang labelled ground:256 on AlwaysFront steal the slot
                    // from the falls labelled flow:256 on Front beneath it — the falls base
                    // carved to effect 0/256 in every season. A pixel counts as VISIBLE
                    // LIQUID when some layer both paints it liquid and draws art there; the
                    // rock showing through fully transparent pixels stays carved.
                    bool[]? liquidVisible = null;
                    int frontIce = 0, frontFlow = 0, frontLava = 0;
                    void FoldFrontLiquid(xTile.Layers.Layer? layer)
                    {
                        if (layer == null || context.Labels.Get(layer, tileX, tileY) is not { } layerLabel)
                            return;
                        var (labelWaterBits, lW, lI, lF, lL) = WaterBitsFromLabels(layerLabel);
                        if (lW + lI + lF + lL == 0)
                            return;
                        if (!TryTileArt(layer, tileX, tileY, out var lt, out var ls, out _, out byte vOri))
                            return;
                        bool[] visibleBits = AnyAlphaBits(lt, ls, vOri);
                        bool any = false;
                        for (int texelIndex = 0; texelIndex < 256; texelIndex++)
                            if (labelWaterBits[texelIndex] && visibleBits[texelIndex])
                            {
                                (liquidVisible ??= new bool[256])[texelIndex] = true;
                                any = true;
                            }
                        if (!any)
                            return;
                        frontIce += lI; frontFlow += lF; frontLava += lL;
                        if (lI > 0 || lL > 0 || lF > 0) AddSubTypePixels(layerLabel, ref ice, ref lava, ref flow);
                    }
                    if (context.Fronts != null) foreach (var frontLayerLoop in context.Fronts) FoldFrontLiquid(frontLayerLoop);
                    if (context.Always != null) foreach (var alwaysFrontLayerLoop in context.Always) FoldFrontLiquid(alwaysFrontLayerLoop);
                    if (liquidVisible != null)
                    {
                        if (frontCarveBits != null)
                        {
                            var frontCarve = (bool[])frontCarveBits.Clone();
                            for (int texelIndex = 0; texelIndex < 256; texelIndex++)
                                if (liquidVisible[texelIndex]) frontCarve[texelIndex] = false;
                            _maskScratch.TileFrontCarveBits![tileIndex] = frontCarve;
                        }
                        // The same liquid beats the BUILDINGS carve too: the falls draws
                        // over an opaque cliff/bank on Buildings — hidden art whose opacity
                        // otherwise erases the flow the player actually sees.
                        if (_maskScratch.TileBuildingCarveBits![tileIndex] is { } carveUnder)
                        {
                            var buildingsCarveLifted = (bool[])carveUnder.Clone();
                            for (int texelIndex = 0; texelIndex < 256; texelIndex++)
                                if (liquidVisible[texelIndex]) buildingsCarveLifted[texelIndex] = false;
                            _maskScratch.TileBuildingCarveBits[tileIndex] = buildingsCarveLifted;
                        }
                        iceCount += frontIce; flowCount += frontFlow; lavaCount += frontLava;
                        job.AnyLabeled = true;
                        frontSolidCount = 0;                 // labelled liquid is never a structure
                        frontLabeledLiquid = true;
                    }
                }
            iceBits = ice; lavaBits = lava; flowBits = flow;
        }

        /// <summary>
        /// Where ANY layer painted this tile, the union of what those labels call liquid IS the
        /// water and everything else is carved. Only a tile nobody painted falls back to the
        /// whole-tile flag.
        /// </summary>
        private void UnionPaintedLiquid(TileGatherContext context, int tileX, int tileY, bool isWater,
                                        ref bool[]? keep)
        {
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
                // for pixel, so: if ANY layer painted this tile, the union of what those ctx.Labels
                // call liquid IS the water, and everything else is carved. Only a tile nobody
                // painted falls back to the whole-tile flag.
                if (isWater && context.Labels != null)
                {
                    bool[]? union = null;
                    bool anyLiquid = false, groundItself = false;
                    void Union(xTile.Layers.Layer? layer, bool isBack)
                    {
                        byte[]? label = context.Labels.Get(layer, tileX, tileY);
                        if (label == null)
                            return;
                        if (isBack)
                            groundItself = true;
                        union ??= new bool[256];
                        for (int texelIndex = 0; texelIndex < 256; texelIndex++)
                        {
                            byte labelClass = label[texelIndex];
                            if (labelClass == 1 || labelClass == 9 || labelClass == 10 || labelClass == 11 || labelClass == 14 || labelClass == 255)
                            {
                                union[texelIndex] = true;
                                anyLiquid = true;
                            }
                        }
                    }
                    if (context.Backs != null) foreach (var layer in context.Backs) Union(layer, true);
                    if (context.BuildingsLayers != null) foreach (var layer in context.BuildingsLayers) Union(layer, false);
                    if (context.Fronts != null) foreach (var layer in context.Fronts) Union(layer, false);
                    if (context.Always != null) foreach (var layer in context.Always) Union(layer, false);
                    // A tile whose ONLY label is an overlay saying "none of this is liquid" is,
                    // as far as the water beneath is concerned, a tile nobody painted. The bank
                    // art along a forest stream is labelled ground on Buildings and is a quarter
                    // to a half transparent; the ground under it is the game's own water, and its
                    // Back label goes unread wherever a recolour has repainted that sheet. Taken
                    // as the union, the answer was "no liquid anywhere here" and five tiles of a
                    // two-tile stream shipped at effect 0/256: a dead strip of vanilla water
                    // hugging the bank while the water a tile away wore the whole effect. The
                    // overlay is still carved, by its own opacity, a few lines below - which is
                    // the per-pixel answer this rule was reaching for. A BACK label that paints
                    // the ground itself and calls it dry is still obeyed: that one is about the
                    // tile, not about something standing on it.
                    if (union != null && (anyLiquid || groundItself))
                        keep = union;
                }
        }

        /// <summary>
        /// Buildings-family art on a water tile whose label paints NO liquid at all: carve every
        /// visible pixel of it, plus the holes its own outline encloses.
        /// </summary>
        private void CarveGroundLabelledOverlay(TileGatherContext context, int tileIndex, int tileX, int tileY,
                                                bool waterHere, bool hasBuildingsArt, byte[]? buildingsLabel,
                                                Texture2D t1, Rectangle s1, byte o1)
        {
                if (waterHere && hasBuildingsArt)
                {
                    byte[]? groundLabel = buildingsLabel;
                    if (groundLabel != null && CountLiquid(groundLabel) == 0)
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
                        //
                        // EVERY Buildings-family layer's art, not just the first one the
                        // gather saw: t1 is the BOTTOM layer with art, and on Aimon's
                        // festival bridge that is a lone support beam on Buildings-1 while
                        // the planks live on Buildings2 — carving the beam alone left the
                        // whole deck rippling. The branch is rare (labelled zero-liquid
                        // overlay on a water tile), so the re-walk costs nothing measurable.
                        bool[]? visibleUnion = null;
                        if (context.BuildingsLayers != null)
                        {
                            foreach (var buildingsLayerLoop in context.BuildingsLayers)
                            {
                                if (!TryTileArt(buildingsLayerLoop, tileX, tileY, out var tv, out var sv, out _, out byte vOri2))
                                    continue;
                                var layerVisible = AnyAlphaBits(tv, sv, vOri2);
                                if (visibleUnion == null) visibleUnion = layerVisible;
                                else
                                {
                                    var m = new bool[256];
                                    for (int texelIndex = 0; texelIndex < 256; texelIndex++) m[texelIndex] = visibleUnion[texelIndex] || layerVisible[texelIndex];
                                    visibleUnion = m;
                                }
                            }
                        }
                        var groundBits = FillEnclosedHoles(visibleUnion ?? AnyAlphaBits(t1, s1, o1), 8);
                        int groundCount = 0;
                        for (int texelIndex = 0; texelIndex < 256; texelIndex++) if (groundBits[texelIndex]) groundCount++;
                        if (groundCount > 0)
                        {
                            _maskScratch.TileBuildingCarveBits![tileIndex] = groundBits;
                            _maskScratch.TileBuildingGroundOverlayFlags![tileIndex] = true;
                        }
                    }
                }
        }

        /// <summary>
        /// Fold every Front-family and AlwaysFront layer's art into the carve channel for one
        /// tile: what it cuts out of the water (<paramref name="fBits"/>), everywhere it draws
        /// anything at all (<paramref name="fAnyBits"/>), and how solid the biggest piece is
        /// (<paramref name="fCount"/>, which is what decides STRUCTURE further down).
        /// </summary>
        private void BuildFrontCarve(TileGatherContext context, int tileX, int tileY, bool waterHere,
                                     bool hasFront, Texture2D t2, Rectangle s2, byte fOri,
                                     out bool frontArt, out bool frontAllGround, out bool[]? frontCarveBits,
                                     out bool[]? frontAnyAlphaBits, out int frontSolidCount)
        {
                // Ground-labelled overlay art is carved from its OPACITY, not from SolidBits'
                // guess — see OpaqueBits. Snow-covered bush and ledge art on the ctx.Front layers
                // trips the same "mostly water-coloured → must be a wave overlay" bail as the
                // bank ledge did (#31 is 131 water-coloured and carved 0, #32 194 and 0), which
                // is why a snowy bush over the river came out rippling AND mirrored.
                // `fCount` deliberately stays on SolidBits: it decides STRUCTURE, and handing a
                // ledge its full opacity there would scrub whole tiles from the march and put
                // the staircase back along the shoreline.
                frontArt = false; frontAllGround = true;
                frontCarveBits = null;
                // Parallel LOW-alpha union (>= 32): where the ctx.Front/ctx.Always art draws anything
                // visible at all — the gate for the carve LIFT below, so a falls' spray
                // (far under the 128-opaque bar) still counts as visible water.
                frontAnyAlphaBits = null;
                bool[]? any = null;
            void MergeAny(bool[] add)
                {
                    if (any == null) { any = add; return; }
                    var m = new bool[256];
                    for (int texelIndex = 0; texelIndex < 256; texelIndex++) m[texelIndex] = any[texelIndex] || add[texelIndex];
                    any = m;
                }
                frontSolidCount = 0;
                if (hasFront)
                {
                    frontArt = true;
                    var frontSolid = SolidBits(t2, s2, fOri);
                    frontSolidCount = frontSolid.count;
                    bool g = OverlayIsGround(context.Labels, context.Front, tileX, tileY, waterHere);
                    if (!g) frontAllGround = false;
                    frontCarveBits = g ? OpaqueBits(t2, s2).bits : frontSolid.bits;
                    MergeAny(AnyAlphaBits(t2, s2, fOri));
                }
                // Fold every AlwaysFront layer's opacity into the Front carve channel.
                if (context.Always != null)
                    foreach (var layer in context.Always)
                        if (TryTileArt(layer, tileX, tileY, out var t3, out var s3, out _, out byte lOri))
                        {
                            frontArt = true;
                            var layerSolid = SolidBits(t3, s3, lOri);
                            bool g = OverlayIsGround(context.Labels, layer, tileX, tileY, waterHere);
                            if (!g) frontAllGround = false;
                            MergeAny(AnyAlphaBits(t3, s3, lOri));
                            var layerCarveBits = g ? OpaqueBits(t3, s3).bits : layerSolid.bits;
                            int layerCarveCount = g ? OpaqueBits(t3, s3).count : layerSolid.count;
                            if (layerCarveCount == 0)
                                continue;
                            if (frontCarveBits == null) frontCarveBits = layerCarveBits;
                            else
                            {
                                var merged = new bool[256];
                                for (int texelIndex = 0; texelIndex < 256; texelIndex++) merged[texelIndex] = frontCarveBits[texelIndex] || layerCarveBits[texelIndex];
                                frontCarveBits = merged;
                            }
                            frontSolidCount = Math.Max(frontSolidCount, layerSolid.count);
                        }
            frontAnyAlphaBits = any;
        }

        /// <summary>Grow the per-tile gather buffers to this window. They are kept between
        /// rebuilds rather than cleared: every pass writes each cell before reading it.</summary>
        private void EnsureGatherBuffers(int count)
        {
            if (_maskScratch.TileEffectBits == null || _maskScratch.TileEffectBits.Length < count) _maskScratch.TileEffectBits = new bool[]?[count];
            if (_maskScratch.TileWaterKeepBits == null || _maskScratch.TileWaterKeepBits.Length < count) _maskScratch.TileWaterKeepBits = new bool[]?[count];
            if (_maskScratch.TileBuildingCarveBits == null || _maskScratch.TileBuildingCarveBits.Length < count) _maskScratch.TileBuildingCarveBits = new bool[]?[count];
            if (_maskScratch.TileFrontCarveBits == null || _maskScratch.TileFrontCarveBits.Length < count) _maskScratch.TileFrontCarveBits = new bool[]?[count];
            if (_maskScratch.TileLargeSolidFlags == null || _maskScratch.TileLargeSolidFlags.Length < count) _maskScratch.TileLargeSolidFlags = new bool[count];
            if (_maskScratch.TileDeckFlags == null || _maskScratch.TileDeckFlags.Length < count) _maskScratch.TileDeckFlags = new bool[count];
            if (_maskScratch.TileLabeledLiquidFlags == null || _maskScratch.TileLabeledLiquidFlags.Length < count) _maskScratch.TileLabeledLiquidFlags = new bool[count];
            if (_maskScratch.TileHasBuildingArtFlags == null || _maskScratch.TileHasBuildingArtFlags.Length < count) _maskScratch.TileHasBuildingArtFlags = new bool[count];
            if (_maskScratch.TileHasFrontArtFlags == null || _maskScratch.TileHasFrontArtFlags.Length < count) _maskScratch.TileHasFrontArtFlags = new bool[count];
            if (_maskScratch.TileBuildingGroundOverlayFlags == null || _maskScratch.TileBuildingGroundOverlayFlags.Length < count) _maskScratch.TileBuildingGroundOverlayFlags = new bool[count];
            if (_maskScratch.TileFrontGroundOverlayFlags == null || _maskScratch.TileFrontGroundOverlayFlags.Length < count) _maskScratch.TileFrontGroundOverlayFlags = new bool[count];
            if (_maskScratch.TileIceBits == null || _maskScratch.TileIceBits.Length < count) _maskScratch.TileIceBits = new bool[]?[count];
            if (_maskScratch.TileLavaBits == null || _maskScratch.TileLavaBits.Length < count) _maskScratch.TileLavaBits = new bool[]?[count];
            if (_maskScratch.TileFlowBits == null || _maskScratch.TileFlowBits.Length < count) _maskScratch.TileFlowBits = new bool[]?[count];
            if (_maskScratch.TileNearLandFlags == null || _maskScratch.TileNearLandFlags.Length < count) _maskScratch.TileNearLandFlags = new bool[count];
            if (_maskScratch.TileIceFlags == null || _maskScratch.TileIceFlags.Length < count) _maskScratch.TileIceFlags = new bool[count];
            if (_maskScratch.TileFlowFlags == null || _maskScratch.TileFlowFlags.Length < count) _maskScratch.TileFlowFlags = new bool[count];
            if (_maskScratch.TileLavaFlags == null || _maskScratch.TileLavaFlags.Length < count) _maskScratch.TileLavaFlags = new bool[count];
        }

        /// <summary>Snapshot the drawn rects of furniture and buildings for pass C2. Entity
        /// lists are live game state, so this runs on the main thread and the worker only
        /// ever sees the plain rectangles it produces.</summary>
        private void GatherEntityCarveRects(GameLocation location)
        {
            // FURNITURE and BUILDING entity rects (Pass C2 inputs). A fish tank's painted
            // water, a well's blue bucket art, a trough — water pixels inside an ENTITY
            // sprite, not a water body. Snapshot their drawn rects here: entity lists are
            // live game state the worker must never touch.
            _entityCarveWorldRectangles.Clear();
            foreach (var f in location.furniture)
            {
                Rectangle boundingBox = f.boundingBox.Value;
                Rectangle src = f.sourceRect.Value;
                int artHeight = src.Height * 4;
                int top = boundingBox.Bottom - Math.Max(artHeight, boundingBox.Height);
                int left = boundingBox.X, right = boundingBox.Right;
                // Carve the SILHOUETTE, exactly as buildings already do. Furniture passed a bare
                // rectangle, and most of a bed's box is the empty space beside the headboard, so
                // a bed standing in shallow water cut a hard rectangle out of the ripple above
                // and beside itself - straight edges in open water, nowhere near the sprite.
                //
                // Furniture.draw pins the art's LEFT edge at the box's left and its BOTTOM at the
                // box's bottom, at scale 4, so the drawn rect is the source rect times four from
                // that corner. Giving the carve those bounds makes one mask texel one art pixel,
                // which is what the proportional lookup in Pass C2 assumes.
                bool[]? opaque = null; int opaqueWidth = 0, opaqueHeight = 0;
                try
                {
                    var texture = StardewValley.ItemRegistry.GetDataOrErrorItem(f.QualifiedItemId)?.GetTexture();
                    if (texture != null && !src.IsEmpty)
                    {
                        (opaque, opaqueWidth, opaqueHeight) = EntityOpaqueBits(texture, src);
                        if (opaque != null)
                        {
                            left = boundingBox.X;
                            right = left + src.Width * 4;
                            top = boundingBox.Bottom - artHeight;
                        }
                    }
                }
                catch { opaque = null; /* art not resolvable — the box is still better than nothing */ }
                _entityCarveWorldRectangles.Add((left, top, right, boundingBox.Bottom, opaque, opaqueWidth, opaqueHeight));
            }
            foreach (var building in location.buildings)
            {
                if (building == null)
                    continue;
                // A FISH POND is the one building whose sprite IS water. Its interior is marked
                // water in the gather above (FishPond.isTileFishable: everything inside the
                // 1-tile masonry rim), and then this loop carved the whole sprite straight back
                // out again — the two cancelled, so a pond has never shown ripple or reflection
                // even though every other part of the pipeline was ready for it. The rim tiles
                // are not marked water in the first place, so there is nothing here left to
                // carve; skipping the pond entirely is the whole fix.
                if (building is StardewValley.Buildings.FishPond)
                    continue;
                // Carve the building's SILHOUETTE, not its bounding rectangle. The rect kills the
                // water sharing every pixel of the sprite's box, and most of a building's box is
                // transparent: the sky beside a pointed roof, the gaps around a well's frame. A
                // well placed at the pond bank erased the waterline and the ripple in a hard
                // rectangle behind its roof — the reported "water has a notch behind the
                // building", with a before/after pair of placing a coop. The rect stays as the
                // fallback when the sprite cannot be read.
                int buildingLeft = building.tileX.Value * 64, buildingWidth = building.tilesWide.Value * 64;
                int bottom = (building.tileY.Value + building.tilesHigh.Value) * 64;
                int artHeight = building.tilesHigh.Value * 64;
                bool[]? opaque = null; int opaqueWidth = 0, opaqueHeight = 0;
                int left = buildingLeft, right = buildingLeft + buildingWidth;
                try
                {
                    Rectangle buildingSourceRect = building.getSourceRect();
                    if (buildingSourceRect.Height > 0)
                        artHeight = Math.Max(artHeight, buildingSourceRect.Height * 4);
                    var texture = building.texture?.Value;
                    if (texture != null && !buildingSourceRect.IsEmpty)
                    {
                        (opaque, opaqueWidth, opaqueHeight) = EntityOpaqueBits(texture, buildingSourceRect);
                        if (opaque != null)
                        {
                            // Building.draw pins the art's bottom-left at the footprint's bottom
                            // row plus DrawOffset, at scale 4 — the same anchor the mirror and the
                            // sprite mask use, so all three agree on where the sprite is.
                            var drawOffset = (building.GetData()?.DrawOffset ?? Microsoft.Xna.Framework.Vector2.Zero) * 4f;
                            left = (int)(buildingLeft + drawOffset.X);
                            right = left + buildingSourceRect.Width * 4;
                            bottom = (int)(bottom + drawOffset.Y);
                            artHeight = buildingSourceRect.Height * 4;
                        }
                    }
                }
                catch { opaque = null; /* sprite not ready — footprint rect */ }
                _entityCarveWorldRectangles.Add((left, bottom - artHeight, right, bottom, opaque, opaqueWidth, opaqueHeight));
            }
        }

        /// <summary>Compose stage - the pixel crunching (passes A-E). Pure array work on gathered
        /// data; safe on a worker thread. Jobs are serialized, so the shared scratch
        /// buffers are exclusively this job's while it runs.</summary>
        /// <summary>
        /// The worker-thread half of a rebuild: pure array work over what the gather phase wrote
        /// down, never touching Game1 or the location. Each pass is named for what it does and
        /// keeps the commentary that explains why it exists; this method is only their order,
        /// which is the one thing the 535-line version made hard to see.
        /// </summary>
        private void ComposeWaterMask(WaterMaskJob job)
        {
            int tilesWide = job.TileWidth, tilesHigh = job.TileHeight;

            ComposeEffectBits(job, tilesWide, tilesHigh);

            // Nothing below has anything to work on without water, and an anchor-only job stops
            // here by design.
            job.WaterAny = job.AnyWater || job.AnyLabeled;
            if (!job.WaterAny)
                return;

            CloseVerticalGaps(tilesWide, tilesHigh);
            CarveMapArt(tilesWide, tilesHigh);
            CarveEntityRects(job, tilesWide, tilesHigh);
            ClearPocketsInsideArt(tilesWide, tilesHigh);
            // A full-map anchor job is finished inside pass D and must not reach E or F: those
            // write the WINDOW's mask, and letting a map-sized job write it moves the waterline.
            if (!BuildWaterlineHeightMap(job, tilesWide, tilesHigh))
                return;

            // Snapshot the per-tile water verdict for the near-water gates (sprite mask, entity
            // mirror). Those gates used to read the GAME-water gather flags, and water that only
            // a label brought in never set them - the desert oasis is one - so every stamp near
            // it was culled and the ripple ran over the palm trunks. Pass D has just computed
            // game-water OR any composed effect pixel per tile, which is the question the gates
            // actually ask; the job gets its own copy because the scratch array is rewritten by
            // the next rebuild.
            int tileCount = tilesWide * tilesHigh;
            job.TileHasEffectWaterFlags = new bool[tileCount];
            Array.Copy(_maskScratch.TileHasEffectWaterFlags!, job.TileHasEffectWaterFlags, tileCount);

            SmoothShorelineAndEmit(tilesWide, tilesHigh);
            BuildPlungeChurnField(tilesWide, tilesHigh);
            BuildShorelineDistanceField(tilesWide, tilesHigh);
        }

        /// <summary>Six tiles: the farthest a falling face is felt below it, and the scale of the red byte.</summary>
        private const int PlungeChurnRangeTexels = 6 * MaskTexelsPerTile;
        /// <summary>Two tiles: the farthest a falling face is felt above it, and the scale of the green byte.</summary>
        private const int LipApproachRangeTexels = 2 * MaskTexelsPerTile;
        /// <summary>How far to either side of a texel the nearest falling face is looked for.</summary>
        private const int PlungeChurnSideTexels = 24;
        /// <summary>Bytes per texel of the fall-distance texture (Color: red below, green above).</summary>
        private const int FallDistanceBytesPerTexel = 4;

        /// <summary>Pass E2 - how far below, and how far above, a falling face each water texel sits.</summary>
            // Pass E2 — the PLUNGE and the LIP. The pool at the foot of a waterfall is aerated
            // and torn up and mirrors nothing there; it settles back over the next few tiles. And
            // the stream above a fall's lip should let its mirror go gently over the last stretch
            // before the edge rather than on the one texel where the face begins. The shader
            // cannot find the fall from a pixel (the face is above or below it, out of any single
            // tap's reach), so both distances are measured here, once per rebuild, and handed
            // over as two bytes per texel: red is the distance BELOW the nearest face, 0 right
            // under the foam and 255 six tiles away or nowhere near a fall; green is the distance
            // ABOVE the face in this texel's own column, 0 on the lip and 255 two tiles up or
            // with no fall beneath.
            //
            // Down each column, count the rows since the last falling texel above, so only water
            // BELOW a fall is reached for the red byte; up each column, the rows since the last
            // falling texel below, for the green one. Then along each row, take the nearest of
            // the downward counts within a tile and a half to either side as a straight-line
            // distance, which rounds the plunge off under the column instead of cutting it to
            // the column's own width. Rows with nothing in range are skipped, and a window with
            // no falling water at all costs one fill.
        private void BuildPlungeChurnField(int tilesWide, int tilesHigh)
        {
            const int Texels = MaskTexelsPerTile;
            int maskWidth = tilesWide * Texels, maskHeight = tilesHigh * Texels, texelCount = maskWidth * maskHeight;
            if (_maskScratch.PlungeChurnPixels == null || _maskScratch.PlungeChurnPixels.Length < texelCount * FallDistanceBytesPerTexel)
                _maskScratch.PlungeChurnPixels = new byte[texelCount * FallDistanceBytesPerTexel];
            byte[] fallDistancePixels = _maskScratch.PlungeChurnPixels;

            bool anyFall = false;
            for (int ti = 0; ti < tilesWide * tilesHigh && !anyFall; ti++)
                anyFall = _maskScratch.TileFlowBits![ti] != null || _maskScratch.TileFlowFlags![ti];
            if (!anyFall)
            {
                FillFallDistanceFar(fallDistancePixels, 0, texelCount);
                return;
            }

            if (_maskScratch.PlungeRowsSinceFall == null || _maskScratch.PlungeRowsSinceFall.Length < texelCount)
                _maskScratch.PlungeRowsSinceFall = new int[texelCount];
            int[] rowsSinceFall = _maskScratch.PlungeRowsSinceFall;
            const int Far = PlungeChurnRangeTexels + 1;
            for (int x = 0; x < maskWidth; x++)
            {
                int rows = Far;
                for (int y = 0; y < maskHeight; y++)
                {
                    int tileIdx = (y / Texels) * tilesWide + (x / Texels);
                    bool[]? flowBits2 = _maskScratch.TileFlowBits![tileIdx];
                    bool falling = flowBits2 != null ? flowBits2[(y % Texels) * Texels + (x % Texels)] : _maskScratch.TileFlowFlags![tileIdx];
                    rows = falling ? 0 : Math.Min(Far, rows + 1);
                    rowsSinceFall[y * maskWidth + x] = rows;
                }
                int rowsAbove = LipApproachRangeTexels;
                for (int y = maskHeight - 1; y >= 0; y--)
                {
                    int texelIndex = y * maskWidth + x;
                    rowsAbove = rowsSinceFall[texelIndex] == 0 ? 0 : Math.Min(LipApproachRangeTexels, rowsAbove + 1);
                    fallDistancePixels[texelIndex * FallDistanceBytesPerTexel + 1] = _waterEffectBits![texelIndex]
                        ? (byte)(rowsAbove * 255 / LipApproachRangeTexels)
                        : (byte)255;
                }
            }

            for (int y = 0; y < maskHeight; y++)
            {
                int rowBase = y * maskWidth;
                bool anyInRange = false;
                for (int x = 0; x < maskWidth && !anyInRange; x++)
                    anyInRange = rowsSinceFall[rowBase + x] < Far;
                for (int x = 0; x < maskWidth; x++)
                {
                    int texelIndex = rowBase + x;
                    int byteIndex = texelIndex * FallDistanceBytesPerTexel;
                    fallDistancePixels[byteIndex + 2] = 0;
                    fallDistancePixels[byteIndex + 3] = 255;
                    if (!anyInRange || !_waterEffectBits![texelIndex])
                    {
                        fallDistancePixels[byteIndex] = 255;
                        continue;
                    }
                    int nearest = Far * Far;
                    int x0 = Math.Max(0, x - PlungeChurnSideTexels), x1 = Math.Min(maskWidth - 1, x + PlungeChurnSideTexels);
                    for (int sampleX = x0; sampleX <= x1; sampleX++)
                    {
                        int down = rowsSinceFall[rowBase + sampleX];
                        if (down >= Far)
                            continue;
                        int across = sampleX - x;
                        int squared = down * down + across * across;
                        if (squared < nearest)
                            nearest = squared;
                    }
                    float distance = MathF.Sqrt(nearest);
                    fallDistancePixels[byteIndex] = (byte)Math.Min(255f, distance * (255f / PlungeChurnRangeTexels));
                }
            }
        }

        /// <summary>Every texel in the range reads as far from any fall, above and below.</summary>
        private static void FillFallDistanceFar(byte[] fallDistancePixels, int firstTexel, int texelCount)
        {
            int end = (firstTexel + texelCount) * FallDistanceBytesPerTexel;
            for (int byteIndex = firstTexel * FallDistanceBytesPerTexel; byteIndex < end; byteIndex += FallDistanceBytesPerTexel)
            {
                fallDistancePixels[byteIndex] = 255;
                fallDistancePixels[byteIndex + 1] = 255;
                fallDistancePixels[byteIndex + 2] = 0;
                fallDistancePixels[byteIndex + 3] = 255;
            }
        }


        /// <summary>Pass A - composite: true water tiles solid, classified art per pixel.</summary>
            // ---- Pass A — composite: true water tiles solid, classified art per-pixel ----
            // (The upload buffer is Pass E's output — a full-map ANCHOR job never gets there,
            // so don't inflate a map-sized Color[] it will never touch.)
        private void ComposeEffectBits(WaterMaskJob job, int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            int texelCount = tilesWide * tilesHigh * MaskTexelsPerTile * MaskTexelsPerTile;

            if (!job.AnchorOnly && (_waterMaskPixels == null || _waterMaskPixels.Length < texelCount)) _waterMaskPixels = new Color[texelCount];
            if (_waterEffectBits == null || _waterEffectBits.Length < texelCount) _waterEffectBits = new bool[texelCount];
            for (int j = 0; j < tilesHigh; j++)
            {
                for (int i = 0; i < tilesWide; i++)
                {
                    int tileIndex = j * tilesWide + i;
                    bool isWater = _waterTileFlags![tileIndex];
                    bool[]? bits = _maskScratch.TileEffectBits![tileIndex];
                    for (int texelY = 0; texelY < MaskTexelsPerTile; texelY++)
                    {
                        int row = (j * MaskTexelsPerTile + texelY) * maskWidth + i * MaskTexelsPerTile;
                        int artRow = texelY * MaskTexelsPerTile;
                        for (int texelX = 0; texelX < MaskTexelsPerTile; texelX++)
                            _waterEffectBits[row + texelX] = isWater || (bits != null && bits[artRow + texelX]);
                    }
                }
            }
        }

        /// <summary>Pass B - vertical CLOSE, two widths. See the comment inside for why they differ.</summary>
            // Pass B — vertical CLOSE (fill gaps that have water above AND below), two widths:
            //   effect bits: ≤4 texels — heals the dark shading slit the shore art paints
            //                along the waterline without swallowing real land.
            //   march bits:  ≤12 texels (~0.75 tile) — anything painted INSIDE a water body
            //                (surf foam bands, starfish, sand flecks) must not read as a
            //                shoreline, or reflections re-anchor below it and shift down.
            //                Bridges/decks are ≥1 tile thick, so they still block.
        private void CloseVerticalGaps(int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            int maskHeight = tilesHigh * MaskTexelsPerTile;
            int count = tilesWide * tilesHigh;
            int texelCount = tilesWide * tilesHigh * MaskTexelsPerTile * MaskTexelsPerTile;

            void CloseVertical(bool[] bits, int maxGap)
            {
                for (int x = 0; x < maskWidth; x++)
                {
                    int last = -99;
                    for (int y = 0; y < maskHeight; y++)
                    {
                        if (!bits[y * maskWidth + x])
                            continue;
                        if (y - last > 1 && y - last <= maxGap + 1)
                            for (int k = last + 1; k < y; k++)
                                bits[k * maskWidth + x] = true;
                        last = y;
                    }
                }
            }
            SubtractLabelsFromChannels(tilesWide, tilesHigh, maskWidth, maskHeight, texelCount);
            // (V4) The anim-region shape test and its waterfall scrub are gone with the colour
            // classifier that fed them: vertical waterfall faces are label class 10's job now
            // (the whole-tile flow/lava march scrub below still runs on labelled tiles).
            CloseVertical(_waterEffectBits!, 4);
            CloseMarchColumns(maskWidth, maskHeight);
            FlagLandConnectedStructures(tilesWide, tilesHigh, count);
            FillBridgeArches(tilesWide, tilesHigh);
        }

        /// <summary>Subtract the painted labels from BOTH channels on true water tiles, then put
        /// back the enclosed march texels: an island in mid-pond must not read as a shoreline, but a
        /// painted bank must. Connectivity is what tells those two apart.</summary>
        private void SubtractLabelsFromChannels(int tilesWide, int tilesHigh, int maskWidth, int maskHeight, int texelCount)
        {
            if (_waterMarchBits == null || _waterMarchBits.Length < texelCount)
                _waterMarchBits = new bool[texelCount];
            Array.Copy(_waterEffectBits!, _waterMarchBits, texelCount);
            // Which march texels the carve below actually removes. RestoreEnclosedMarch may only
            // put these back; see its remarks for what happened when it could touch anything.
            if (_maskScratch.MarchCarvedBits == null || _maskScratch.MarchCarvedBits.Length < texelCount)
                _maskScratch.MarchCarvedBits = new bool[texelCount];
            Array.Clear(_maskScratch.MarchCarvedBits, 0, texelCount);
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
            for (int j = 0; j < tilesHigh; j++)
            {
                for (int i = 0; i < tilesWide; i++)
                {
                    bool[]? keep = _maskScratch.TileWaterKeepBits![j * tilesWide + i];
                    if (keep == null)
                        continue;
                    for (int texelY = 0; texelY < MaskTexelsPerTile; texelY++)
                    {
                        int row = (j * MaskTexelsPerTile + texelY) * maskWidth + i * MaskTexelsPerTile;
                        int artRow = texelY * MaskTexelsPerTile;
                        for (int texelX = 0; texelX < MaskTexelsPerTile; texelX++)
                            if (!keep[artRow + texelX])
                            {
                                _waterEffectBits![row + texelX] = false;
                                if (_waterMarchBits[row + texelX])
                                    _maskScratch.MarchCarvedBits[row + texelX] = true;
                                _waterMarchBits[row + texelX] = false;
                            }
                    }
                }
            }
            RestoreEnclosedMarch(_waterMarchBits, _maskScratch.MarchCarvedBits, maskWidth, maskHeight);
        }

        /// <summary>The march channel's vertical close, which is SPECK-AWARE: a short run bridges
        /// only a small gap, so wet-shading dashes on the bank cannot chain into the body below and
        /// pull the column's waterline anchor up with them.</summary>
        private void CloseMarchColumns(int maskWidth, int maskHeight)
        {
            // March close is SPECK-AWARE: a run shorter than 3 texels only bridges gaps
            // ≤4 (a rim sliver above its slit), never the full 12 — wet-shading specks on
            // the bank otherwise chained into the body below, pulling the column's
            // waterline anchor up onto the bank (the surviving dark dashes).
            for (int x = 0; x < maskWidth; x++)
            {
                int last = -99, runHeight = 0;
                for (int y = 0; y < maskHeight; y++)
                {
                    if (!_waterMarchBits![y * maskWidth + x])
                        continue;
                    int gap = y - last - 1;
                    if (gap == 0)
                        runHeight++;
                    else if (gap <= 12 && (gap <= 4 || runHeight >= 3))
                    {
                        for (int k = last + 1; k < y; k++)
                            _waterMarchBits![k * maskWidth + x] = true;
                        runHeight += gap + 1;
                    }
                    else
                        runHeight = 1;
                    last = y;
                }
            }
        }

        /// <summary>Which near-solid tiles are STRUCTURE: near-solid art that reaches land. A bridge
        /// always touches a bank; a raft of lily pads dense enough to fill its tile still floats.</summary>
        private void FlagLandConnectedStructures(int tilesWide, int tilesHigh, int count)
        {
            // Structure test for the MARCH channel: near-solid art (≥90% opaque) that is
            // CONNECTED TO LAND. A bridge or pier always touches a bank; a clump of lily pads
            // dense enough to fill its tile still floats in open water — opacity alone let pad
            // clusters re-anchor reflections below them. Connectivity: seed near-solid tiles
            // that touch a non-water tile (or the screen edge — the structure may continue
            // off-screen), then grow the seed through adjacent near-solid tiles.
            if (_tileNearSolidFlags == null || _tileNearSolidFlags.Length < count) _tileNearSolidFlags = new bool[count];
            if (_tileLandConnectedFlags == null || _tileLandConnectedFlags.Length < count) _tileLandConnectedFlags = new bool[count];
            for (int j = 0; j < tilesHigh; j++)
            {
                for (int i = 0; i < tilesWide; i++)
                {
                    int tileIndex = j * tilesWide + i;
                    bool isLargeSolid = _maskScratch.TileLargeSolidFlags![tileIndex];
                    _tileNearSolidFlags[tileIndex] = isLargeSolid;
                    bool landNear = i == 0 || i == tilesWide - 1 || j == 0 || j == tilesHigh - 1
                        || !_waterTileFlags![tileIndex - 1] || !_waterTileFlags[tileIndex + 1]
                        || !_waterTileFlags[tileIndex - tilesWide] || !_waterTileFlags[tileIndex + tilesWide];
                    _maskScratch.TileNearLandFlags![tileIndex] = landNear;
                    // A deck is walkable — land-connected by definition, no seed test needed.
                    _tileLandConnectedFlags[tileIndex] = isLargeSolid && (landNear || _maskScratch.TileDeckFlags![tileIndex]);
                }
            }
            for (int sweep = 0; sweep < 2; sweep++)
            {
                for (int tileIndex = 0; tileIndex < count; tileIndex++)                       // forward
                    if (_tileNearSolidFlags[tileIndex] && !_tileLandConnectedFlags[tileIndex] &&
                        ((tileIndex % tilesWide > 0 && _tileLandConnectedFlags[tileIndex - 1]) || (tileIndex >= tilesWide && _tileLandConnectedFlags[tileIndex - tilesWide])))
                        _tileLandConnectedFlags[tileIndex] = true;
                for (int tileIndex = count - 1; tileIndex >= 0; tileIndex--)                  // backward
                    if (_tileNearSolidFlags[tileIndex] && !_tileLandConnectedFlags[tileIndex] &&
                        ((tileIndex % tilesWide < tilesWide - 1 && _tileLandConnectedFlags[tileIndex + 1]) || (tileIndex + tilesWide < count && _tileLandConnectedFlags[tileIndex + tilesWide])))
                        _tileLandConnectedFlags[tileIndex] = true;
            }
        }

        /// <summary>Close a bridge's arch openings so the whole structure has one level base, and
        /// every column of its reflection anchors on the same row.</summary>
        private void FillBridgeArches(int tilesWide, int tilesHigh)
        {
            // ARCH FILL: a bridge's arch openings sit BETWEEN structure tiles in the same row.
            // Fill gaps ≤3 tiles between two structure tiles when the gap tile itself carries
            // Buildings/Front art (arch rims do; open water between two separate piers doesn't)
            // — the structure becomes ONE solid block with a level base, so every column's
            // reflection anchors on the same row, like a real bridge mirrored in water.
            for (int j = 0; j < tilesHigh; j++)
            {
                int lastStruct = -99;
                for (int i = 0; i < tilesWide; i++)
                {
                    int tileIndex = j * tilesWide + i;
                    if (!_tileLandConnectedFlags![tileIndex])
                        continue;
                    if (i - lastStruct > 1 && i - lastStruct <= 4)
                    {
                        for (int k = lastStruct + 1; k < i; k++)
                        {
                            int kidx = j * tilesWide + k;
                            if (_maskScratch.TileHasBuildingArtFlags![kidx] || _maskScratch.TileHasFrontArtFlags![kidx])
                                _tileLandConnectedFlags![kidx] = true;
                        }
                    }
                    lastStruct = i;
                }
            }
        }

        /// <summary>Pass C - carve opaque Buildings/Front art into the effect and march channels.</summary>
            // Pass C — carve opaque Buildings/Front art and emit two channels:
            //   R = EFFECT mask: carve everything opaque (no ripple/mirror ON posts, pads, bridges).
            //   G = MARCH mask: carve only land-connected structures (see above).
        private void CarveMapArt(int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            // Write down which effect texels the carve takes away, so the pocket pass below can
            // tell water trapped inside drawn art from water that simply has land around it.
            int carveCount = maskWidth * tilesHigh * MaskTexelsPerTile;
            if (_maskScratch.ArtCarvedFlags == null || _maskScratch.ArtCarvedFlags.Length < carveCount)
                _maskScratch.ArtCarvedFlags = new bool[carveCount];
            Array.Clear(_maskScratch.ArtCarvedFlags, 0, carveCount);
            var artCarved = _maskScratch.ArtCarvedFlags;

            for (int j = 0; j < tilesHigh; j++)
            {
                for (int i = 0; i < tilesWide; i++)
                {
                    int tileIndex = j * tilesWide + i;
                    bool[]? buildingsCarve = _maskScratch.TileBuildingCarveBits![tileIndex];
                    bool[]? frontCarve = _maskScratch.TileFrontCarveBits![tileIndex];
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
                    bool structTile = _tileLandConnectedFlags![tileIndex] && (_maskScratch.TileDeckFlags![tileIndex] || !_maskScratch.TileLabeledLiquidFlags![tileIndex]);
                    bool pixelCarveMarch = _maskScratch.TileLabeledLiquidFlags![tileIndex] && !structTile && _tileLandConnectedFlags[tileIndex];
                    // The scrub covers the art's vertical EXTENT per column (topmost..bottommost
                    // opaque pixel): water ABOVE the art keeps its march too — the strip north of
                    // a bridge parapet, whose art sits at the tile's bottom, belongs to the upper
                    // water body and must not lose its reflection to the parapet's tile.
                    int[]? structScrubTopByColumn = null, structScrubBottomByColumn = null;
                    if (structTile && (buildingsCarve != null || frontCarve != null))
                    {
                        structScrubTopByColumn = _structureScrubTopScratch ??= new int[MaskTexelsPerTile];
                        structScrubBottomByColumn = _structureScrubBottomScratch ??= new int[MaskTexelsPerTile];
                        for (int texelX = 0; texelX < MaskTexelsPerTile; texelX++)
                        {
                            int top = MaskTexelsPerTile, bottom = -1;   // no art in this column: scrub nothing
                            for (int ay = 0; ay < MaskTexelsPerTile; ay++)
                            {
                                int a = ay * MaskTexelsPerTile + texelX;
                                if ((buildingsCarve != null && buildingsCarve[a]) || (frontCarve != null && frontCarve[a]))
                                {
                                    if (ay < top) top = ay;
                                    bottom = ay;
                                }
                            }
                            structScrubTopByColumn[texelX] = top;
                            structScrubBottomByColumn[texelX] = bottom;
                        }
                    }
                    // Ground-labelled overlay art breaks the march at its own outline, but only where
                    // the tile touches land. That is the difference between a BANK — whose top edge is
                    // the real waterline, and the reflection has to start below it — and an ISLAND in
                    // mid-pond, which must stay invisible to the march or every reflection in the body
                    // re-anchors on it. Same land-connectivity question the structure test already
                    // asks, answered from the label instead of from opacity.
                    bool groundOverlayMarch = _maskScratch.TileBuildingGroundOverlayFlags![tileIndex] && _maskScratch.TileNearLandFlags![tileIndex];
                    bool groundFrontMarch = _maskScratch.TileFrontGroundOverlayFlags![tileIndex] && _maskScratch.TileNearLandFlags![tileIndex];
                    for (int texelY = 0; texelY < MaskTexelsPerTile; texelY++)
                    {
                        int row = (j * MaskTexelsPerTile + texelY) * maskWidth + i * MaskTexelsPerTile;
                        int artRow = texelY * MaskTexelsPerTile;
                        for (int texelX = 0; texelX < MaskTexelsPerTile; texelX++)
                        {
                            if (structTile && (structScrubBottomByColumn == null
                                    || (texelY >= structScrubTopByColumn![texelX] && texelY <= structScrubBottomByColumn[texelX])))
                                _waterMarchBits![row + texelX] = false;
                            if (buildingsCarve != null && buildingsCarve[artRow + texelX])
                            {
                                _waterEffectBits![row + texelX] = false;
                                artCarved[row + texelX] = true;
                                // Labelled structure art breaks the march at its PAINTED shape
                                // (the carve already had the label's liquid pixels removed), so a
                                // rock rim hangs its reflection from its own outline instead of
                                // either a whole-tile hole or nothing.
                                if (pixelCarveMarch || groundOverlayMarch) _waterMarchBits![row + texelX] = false;
                            }
                            if (frontCarve != null && frontCarve[artRow + texelX])
                            {
                                _waterEffectBits![row + texelX] = false;
                                artCarved[row + texelX] = true;
                                if (pixelCarveMarch || groundFrontMarch) _waterMarchBits![row + texelX] = false;
                            }
                        }
                    }
                }
            }
        }


        /// <summary>How much water a pocket may hold and still be one. Four tiles: a slot across
        /// the back of a bench is a couple of texels tall and a few tiles wide, and nothing that
        /// deserves a ripple of its own is both this small and walled in by art.</summary>
        private const int PocketTexelCap = 4 * MaskTexelsPerTile * MaskTexelsPerTile;

        /// <summary>
        /// Clear the water trapped INSIDE a piece of drawn art.
        ///
        /// <para>The carve reads art opacity, which is right at an outline and wrong inside one.
        /// A bench painted into the Beach map's Buildings layer has a slot between its back and
        /// its seat; the map underneath is open water, so the slot stayed water, and the ripple
        /// animated inside the bench. Measured at Beach (42,33): a water tile carrying the
        /// backrest, effect 96 of 256 texels, and part of that 96 is the slot.</para>
        ///
        /// <para>Displacement is why it reads so badly rather than merely oddly. The ripple moves
        /// pixels a few across, and in a slot two or three texels tall there is no water to move
        /// in from, so what arrives is the bench, and the bench appears to slosh.</para>
        ///
        /// <para>The test has to separate that slot from a small pond, which is also a little
        /// water with something all around it. Connectivity alone cannot: both are enclosed. What
        /// tells them apart is WHAT encloses them. A pond is bounded by texels that were never
        /// water; the slot is bounded by texels that WERE water until art was carved out of them,
        /// which <see cref="WaterMaskScratch.ArtCarvedFlags"/> records. So a pocket is cleared
        /// only when every texel around it was taken by the carve.</para>
        ///
        /// <para>Anything reaching the window border is left alone whatever it is bounded by: it
        /// continues off-screen and its real extent is unknown, the same rule
        /// <see cref="DropSpeckComponents"/> follows. Only the effect channel is touched. The
        /// march channel decides where a waterline is, and a pocket this size has no business
        /// moving one either way.</para>
        /// </summary>
        private void ClearPocketsInsideArt(int tilesWide, int tilesHigh)
        {
            var effect = _waterEffectBits;
            var artCarved = _maskScratch.ArtCarvedFlags;
            if (effect == null || artCarved == null)
                return;
            int maskWidth = tilesWide * MaskTexelsPerTile, maskHeight = tilesHigh * MaskTexelsPerTile;
            int n = maskWidth * maskHeight;
            if (n <= 0 || effect.Length < n || artCarved.Length < n)
                return;
            if (_maskScratch.PocketVisitedFlags == null || _maskScratch.PocketVisitedFlags.Length < n)
                _maskScratch.PocketVisitedFlags = new bool[n];
            // These two are shared with the march passes, which is safe for the same reason the
            // whole scratch is: one rebuild runs at a time and these passes are strictly ordered.
            if (_maskScratch.MarchFloodStack == null || _maskScratch.MarchFloodStack.Length < n)
                _maskScratch.MarchFloodStack = new int[n];
            if (_maskScratch.SpeckComponentMembers == null || _maskScratch.SpeckComponentMembers.Length < n)
                _maskScratch.SpeckComponentMembers = new int[n];
            var visited = _maskScratch.PocketVisitedFlags;
            var stack = _maskScratch.MarchFloodStack;
            var members = _maskScratch.SpeckComponentMembers;
            Array.Clear(visited, 0, n);

            // Everything the border can reach through water continues off-screen. Claim it first
            // so the walk below only ever meets water that is genuinely shut in.
            int top = 0;
            void Seed(int index)
            {
                if (effect[index] && !visited[index])
                {
                    visited[index] = true;
                    stack[top++] = index;
                }
            }
            for (int x = 0; x < maskWidth; x++)
            {
                Seed(x);
                Seed((maskHeight - 1) * maskWidth + x);
            }
            for (int y = 0; y < maskHeight; y++)
            {
                Seed(y * maskWidth);
                Seed(y * maskWidth + maskWidth - 1);
            }
            while (top > 0)
            {
                int index = stack[--top];
                int x = index % maskWidth, y = index / maskWidth;
                if (x > 0) Seed(index - 1);
                if (x < maskWidth - 1) Seed(index + 1);
                if (y > 0) Seed(index - maskWidth);
                if (y < maskHeight - 1) Seed(index + maskWidth);
            }

            for (int start = 0; start < n; start++)
            {
                if (!effect[start] || visited[start])
                    continue;
                int memberCount = 0;
                bool wallsAreAllArt = true;
                visited[start] = true;
                stack[0] = start;
                top = 1;
                while (top > 0)
                {
                    int index = stack[--top];
                    if (memberCount < members.Length)
                        members[memberCount] = index;
                    memberCount++;
                    int x = index % maskWidth, y = index / maskWidth;
                    for (int side = 0; side < 4; side++)
                    {
                        int neighbourX = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                        int neighbourY = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                        if (neighbourX < 0 || neighbourX >= maskWidth || neighbourY < 0 || neighbourY >= maskHeight)
                            continue;               // a border component was claimed above
                        int neighbour = neighbourY * maskWidth + neighbourX;
                        if (effect[neighbour])
                        {
                            if (!visited[neighbour])
                            {
                                visited[neighbour] = true;
                                stack[top++] = neighbour;
                            }
                        }
                        else if (!artCarved[neighbour])
                        {
                            wallsAreAllArt = false; // land, or a label's own carve: leave it alone
                        }
                    }
                }
                if (!wallsAreAllArt || memberCount > PocketTexelCap || memberCount > members.Length)
                    continue;
                for (int i = 0; i < memberCount; i++)
                {
                    effect[members[i]] = false;
                    // The pocket counts as art from here on. It is walled in by art, so if the
                    // real-shore field below did not fill it back in it would read as a little
                    // island of land inside the water and grow a ring of foam around itself.
                    artCarved[members[i]] = true;
                }
            }
        }

        /// <summary>Pass C2 - carve furniture and building entity rects gathered on the main thread.</summary>
            // Pass C2 — carve FURNITURE and BUILDING entity rects gathered on the main thread.
            // Where the sprite's opacity was readable, only its OPAQUE pixels carve: one mask
            // texel is 4 world px and a building draws its art at scale 4, so one texel maps to
            // exactly one art pixel and the sprite's outline lands on the mask 1:1. Transparent
            // parts of the box leave the water (and its waterline) alone.
        private void CarveEntityRects(WaterMaskJob job, int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            int maskHeight = tilesHigh * MaskTexelsPerTile;

            foreach (var (wx0, wy0, wx1, wy1, opaque, opaqueWidth, opaqueHeight) in _entityCarveWorldRectangles)
            {
                int px0 = Math.Max(0, wx0 / 4 - job.StartTileX * MaskTexelsPerTile);
                int py0 = Math.Max(0, wy0 / 4 - job.StartTileY * MaskTexelsPerTile);
                int px1 = Math.Min(maskWidth, wx1 / 4 - job.StartTileX * MaskTexelsPerTile);
                int py1 = Math.Min(maskHeight, wy1 / 4 - job.StartTileY * MaskTexelsPerTile);
                int rw = Math.Max(1, wx1 - wx0), rh = Math.Max(1, wy1 - wy0);
                for (int y = py0; y < py1; y++)
                {
                    int row = y * maskWidth;
                    int ay = opaque == null ? 0 : ((y + job.StartTileY * MaskTexelsPerTile) * 4 - wy0) * opaqueHeight / rh;
                    for (int x = px0; x < px1; x++)
                    {
                        if (opaque != null)
                        {
                            int ax = ((x + job.StartTileX * MaskTexelsPerTile) * 4 - wx0) * opaqueWidth / rw;
                            if ((uint)ax >= (uint)opaqueWidth || (uint)ay >= (uint)opaqueHeight || !opaque[ay * opaqueWidth + ax])
                                continue;
                        }
                        _waterEffectBits![row + x] = false;
                        _waterMarchBits![row + x] = false;
                        if (_maskScratch.ArtCarvedFlags != null)
                            _maskScratch.ArtCarvedFlags[row + x] = true;
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
            for (int j = 0; j < tilesHigh; j++)
                for (int i = 0; i < tilesWide; i++)
                {
                    int ti = j * tilesWide + i;
                    bool[]? flowBits2 = _maskScratch.TileFlowBits![ti], lavaBits2 = _maskScratch.TileLavaBits![ti];
                    bool wholeTile = flowBits2 == null && lavaBits2 == null
                                  && (_maskScratch.TileFlowFlags![ti] || _maskScratch.TileLavaFlags![ti]);
                    if (!wholeTile && flowBits2 == null && lavaBits2 == null)
                        continue;
                    if (wholeTile)
                    {
                        for (int texelY = 0; texelY < MaskTexelsPerTile; texelY++)
                        {
                            int row = (j * MaskTexelsPerTile + texelY) * maskWidth + i * MaskTexelsPerTile;
                            for (int texelX = 0; texelX < MaskTexelsPerTile; texelX++)
                                _waterMarchBits![row + texelX] = false;
                        }
                        continue;
                    }
                    ScrubFallingColumns(j, i, tilesWide, maskWidth, flowBits2, lavaBits2);
                }
        }

        /// <summary>Whether the flow or lava bits of one tile mark the given texel.</summary>
        private static bool LiquidFaceAt(bool[]? flowBits, bool[]? lavaBits, int texel)
            => (flowBits != null && flowBits[texel]) || (lavaBits != null && lavaBits[texel]);

        /// <summary>Pass C3, one tile: take the march channel away from a falling face, column by
        /// column, so the reflection ends on the face's own painted edge.</summary>
            // The scrub used to span the ROWS the flow covers across the whole tile, not only the
            // falling pixels, and that was right at the bottom of a fall and wrong at its top. At
            // the bottom, water sitting BESIDE the fall at the same height is the plunge churn: it
            // carries no flow label of its own, so a pixel-exact scrub left it mirroring, its
            // column's run began at the top of the falls tile, and Pass E's horizontal smoothing
            // then dragged the pool's own shoreline up with it - the reflection climbed into the
            // waterfall. At the TOP of a fall the same row band cut the stream's reflection off on
            // a straight line a tile above the painted lip, which is drawn as a curve: the water
            // above the curve is still the stream's surface and mirrors like the rest of it.
            //
            // So each column is read for what the fall does there. A column the fall enters from
            // the tile above, or whose face starts on the top row, is scrubbed from the top down to
            // where its face ends, so the foam at the foot keeps its painted bottom edge. A column
            // whose face starts lower and runs out of the bottom of the tile is the lip: the scrub
            // starts on the face's first row, which is the curve. A column with no face of its own
            // is churn when the fall comes into this tile from above (the row band, as before) and
            // upstream water when it does not (left alone). A face that starts and ends inside one
            // column with nothing coming from above is a short cascade and is scrubbed as itself.
        private void ScrubFallingColumns(int tileRow, int tileColumn, int tilesWide, int maskWidth, bool[]? flowBits2, bool[]? lavaBits2)
        {
            const int Texels = MaskTexelsPerTile;
            int bandTop = Texels, bandBottom = -1;
            for (int texelY = 0; texelY < Texels && bandTop == Texels; texelY++)
                for (int texelX = 0; texelX < Texels; texelX++)
                    if (LiquidFaceAt(flowBits2, lavaBits2, texelY * Texels + texelX)) { bandTop = texelY; break; }
            for (int texelY = Texels - 1; texelY >= 0 && bandBottom < 0; texelY--)
                for (int texelX = 0; texelX < Texels; texelX++)
                    if (LiquidFaceAt(flowBits2, lavaBits2, texelY * Texels + texelX)) { bandBottom = texelY; break; }

            bool[]? aboveFlowBits = null, aboveLavaBits = null;
            bool aboveWhole = false;
            if (tileRow > 0)
            {
                int aboveIndex = (tileRow - 1) * tilesWide + tileColumn;
                aboveFlowBits = _maskScratch.TileFlowBits![aboveIndex];
                aboveLavaBits = _maskScratch.TileLavaBits![aboveIndex];
                aboveWhole = aboveFlowBits == null && aboveLavaBits == null
                          && (_maskScratch.TileFlowFlags![aboveIndex] || _maskScratch.TileLavaFlags![aboveIndex]);
            }
            Span<bool> fromAbove = stackalloc bool[Texels];
            bool anyFromAbove = false;
            for (int texelX = 0; texelX < Texels; texelX++)
            {
                fromAbove[texelX] = aboveWhole || LiquidFaceAt(aboveFlowBits, aboveLavaBits, (Texels - 1) * Texels + texelX);
                anyFromAbove |= fromAbove[texelX];
            }

            for (int texelX = 0; texelX < Texels; texelX++)
            {
                int first = -1, last = -1;
                for (int texelY = 0; texelY < Texels; texelY++)
                    if (LiquidFaceAt(flowBits2, lavaBits2, texelY * Texels + texelX))
                    {
                        if (first < 0) first = texelY;
                        last = texelY;
                    }
                int scrubTop, scrubBottom;
                if (first < 0)
                {
                    if (!anyFromAbove) continue;
                    scrubTop = bandTop; scrubBottom = bandBottom;
                }
                else if (fromAbove[texelX] || first == 0)
                {
                    scrubTop = 0; scrubBottom = last;
                }
                else if (last == Texels - 1)
                {
                    scrubTop = first; scrubBottom = Texels - 1;
                }
                else if (anyFromAbove)
                {
                    scrubTop = bandTop; scrubBottom = bandBottom;
                }
                else
                {
                    scrubTop = first; scrubBottom = last;
                }
                for (int texelY = scrubTop; texelY <= scrubBottom; texelY++)
                    _waterMarchBits![(tileRow * Texels + texelY) * maskWidth + tileColumn * Texels + texelX] = false;
            }
        }

        /// <summary>Pass D - waterline height map: the top row of each march-water run, per column.</summary>
            // Pass D — WATERLINE HEIGHT-MAP: per column, remember the top row of each
            // contiguous march-water run (= that pixel's shoreline). Runs shorter than
            // 6 texels are DROPPED from the march: isolated wet-shading specks in shore
            // art each became a tiny mirror (dist 0) that painted a dark dash onto the
            // bank. Runs cut off by the mask bottom are kept — they continue off-screen.
        /// <returns>False for a full-map ANCHOR job, which is finished here: passes E and F must
        /// NOT run for it. Before the split this was a bare `return` out of the whole compose, and
        /// turning it into a return out of one pass let E and F run on an anchor job and overwrite
        /// the window's mask. The harness caught it as the waterline moving two texels at the
        /// Mountain lake; nothing on screen looked wrong.</returns>
        private bool BuildWaterlineHeightMap(WaterMaskJob job, int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            int maskHeight = tilesHigh * MaskTexelsPerTile;
            int count = tilesWide * tilesHigh;
            int texelCount = tilesWide * tilesHigh * MaskTexelsPerTile * MaskTexelsPerTile;

            if (_waterlineTopRowByPixel == null || _waterlineTopRowByPixel.Length < texelCount)
                _waterlineTopRowByPixel = new short[texelCount];
            DropSpeckComponents(_waterMarchBits!, maskWidth, maskHeight);
            for (int x = 0; x < maskWidth; x++)
            {
                int top = -1;
                for (int y = 0; y <= maskHeight; y++)
                {
                    int texelIndex = y * maskWidth + x;
                    if (y < maskHeight && _waterMarchBits![texelIndex]) { if (top < 0) top = y; _waterlineTopRowByPixel[texelIndex] = (short)top; }
                    else top = -1;
                }
            }

            // P3a — a FULL-MAP anchor job stops here: all it wanted was the speck-dropped
            // march bits. Emit the compact per-column run list and skip E/F entirely.
            if (job.AnchorOnly)
            {
                ExtractAnchorRuns(job, maskWidth, maskHeight);
                return false;
            }
            // Window job with a valid location-wide anchor: re-base every run top on the
            // TRUE shoreline. RunTopRows above the window come out negative — Pass E's depth
            // encode keeps counting from the real shore instead of the window edge.
            if (job.Anchor != null)
                OverrideEdgeFromAnchor(job, maskWidth, maskHeight);

            // WATER-BODY SIZE → calm factor. A tiny tide pool should barely ripple while an
            // ocean rolls; flood-fill the water TILES (4-connected) and scale each tile's effect
            // value by its body's tile count. Works for heuristic AND labelled water alike — the
            // game "knows it's small" from the connected area, not from colour or a special label.
            if (_maskScratch.TileHasEffectWaterFlags == null || _maskScratch.TileHasEffectWaterFlags.Length < count) _maskScratch.TileHasEffectWaterFlags = new bool[count];
            if (_maskScratch.TileCalmnessValues == null || _maskScratch.TileCalmnessValues.Length < count) _maskScratch.TileCalmnessValues = new byte[count];
            for (int j = 0; j < tilesHigh; j++)
                for (int i = 0; i < tilesWide; i++)
                {
                    bool wet = _waterTileFlags![j * tilesWide + i];
                    if (!wet)
                        for (int texelY = 0; texelY < MaskTexelsPerTile && !wet; texelY++)
                        {
                            int r = (j * MaskTexelsPerTile + texelY) * maskWidth + i * MaskTexelsPerTile;
                            for (int texelX = 0; texelX < MaskTexelsPerTile; texelX++)
                                if (_waterEffectBits![r + texelX]) { wet = true; break; }
                        }
                    _maskScratch.TileHasEffectWaterFlags[j * tilesWide + i] = wet;
                }
            {
                Span<int> stack = count <= 4096 ? stackalloc int[Math.Min(count, 4096)] : new int[count];
                var seen = new bool[count];
                var member = new List<int>(64);
                for (int start = 0; start < count; start++)
                {
                    if (!_maskScratch.TileHasEffectWaterFlags[start] || seen[start])
                        continue;
                    int stackTop = 0; stack[stackTop++] = start; seen[start] = true; member.Clear();
                    while (stackTop > 0)
                    {
                        int current = stack[--stackTop]; member.Add(current);
                        int currentX = current % tilesWide, currentY = current / tilesWide;
                        if (currentX > 0 && _maskScratch.TileHasEffectWaterFlags[current - 1] && !seen[current - 1]) { seen[current - 1] = true; stack[stackTop++] = current - 1; }
                        if (currentX < tilesWide - 1 && _maskScratch.TileHasEffectWaterFlags[current + 1] && !seen[current + 1]) { seen[current + 1] = true; stack[stackTop++] = current + 1; }
                        if (currentY > 0 && _maskScratch.TileHasEffectWaterFlags[current - tilesWide] && !seen[current - tilesWide]) { seen[current - tilesWide] = true; stack[stackTop++] = current - tilesWide; }
                        if (currentY < tilesHigh - 1 && _maskScratch.TileHasEffectWaterFlags[current + tilesWide] && !seen[current + tilesWide]) { seen[current + tilesWide] = true; stack[stackTop++] = current + tilesWide; }
                    }
                    // size → calm: <=3 tiles ~0.5 (a puddle), ramping to full by ~36 tiles (a pond+).
                    // The size comes from the LOCATION-wide body (RefreshLocationBodySizes), so it
                    // does not change as the window scrolls over the same pool. The window's own
                    // count is only a floor, for water the map grid does not know about — a draw
                    // hook's water, say. What it must never be again is the window EDGE: that used
                    // to force full size and stepped a pool's ripple by 2x mid-walk.
                    int bodyTiles = member.Count;
                    if (job.BodyTileCounts is { } bodySizes)
                    {
                        int gridWidth = job.BodyGridWidth, gridHeight = job.BodyGridHeight;
                        foreach (int tileIndex in member)
                        {
                            int mapX = job.StartTileX + tileIndex % tilesWide, mapY = job.StartTileY + tileIndex / tilesWide;
                            if ((uint)mapX < (uint)gridWidth && (uint)mapY < (uint)gridHeight)
                            {
                                int s = bodySizes[mapY * gridWidth + mapX];
                                if (s > bodyTiles) bodyTiles = s;
                            }
                        }
                    }
                    float calm = MathHelper.Clamp(0.5f + (bodyTiles - 3) / 33f * 0.5f, 0.5f, 1f);
                    byte cb = (byte)MathHelper.Clamp(calm * 255f, 0f, 255f);
                    foreach (int tileIndex in member)
                        _maskScratch.TileCalmnessValues[tileIndex] = cb;
                }
            }
            return true;
        }

        /// <summary>Pass E - smooth the shoreline horizontally and emit the mask pixels.</summary>
            // Pass E — smooth the shoreline HORIZONTALLY (±10 texels window) and emit. Stepped
            // diagonal banks become a continuous slope, so a reflection is no longer sliced
            // into offset blocks — the shader reads this distance (B, half-texel units) instead
            // of marching. Uses per-row PREFIX SUMS (O(width) per row, was O(width×21)); the
            // window average is clamped to ±1.5 tiles of the pixel's own edge, which bounds the
            // pull from a different water body sharing the row (the old per-neighbour reject).
        private void SmoothShorelineAndEmit(int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            int maskHeight = tilesHigh * MaskTexelsPerTile;

            if (_waterlineRowPrefixSums == null || _waterlineRowPrefixSums.Length < maskWidth + 1) { _waterlineRowPrefixSums = new int[maskWidth + 1]; _waterlineRowSampleCounts = new int[maskWidth + 1]; }
            for (int y = 0; y < maskHeight; y++)
            {
                int rowBase = y * maskWidth;
                for (int x = 0; x < maskWidth; x++)
                {
                    int texelIndex = rowBase + x;
                    bool v = _waterMarchBits![texelIndex];
                    _waterlineRowPrefixSums![x + 1] = _waterlineRowPrefixSums[x] + (v ? _waterlineTopRowByPixel![texelIndex] : 0);
                    _waterlineRowSampleCounts![x + 1] = _waterlineRowSampleCounts[x] + (v ? 1 : 0);
                }
                for (int x = 0; x < maskWidth; x++)
                {
                    int texelIndex = rowBase + x;
                    bool eff = _waterEffectBits![texelIndex];
                    bool march = _waterMarchBits![texelIndex];
                    byte shoreDepthByte = 255;
                    if (march)
                    {
                        int t0 = _waterlineTopRowByPixel![texelIndex];
                        int x0 = Math.Max(0, x - 10), x1 = Math.Min(maskWidth - 1, x + 10);
                        int sampleCount = _waterlineRowSampleCounts![x1 + 1] - _waterlineRowSampleCounts[x0];
                        float ts = sampleCount > 0 ? (float)(_waterlineRowPrefixSums[x1 + 1] - _waterlineRowPrefixSums[x0]) / sampleCount : t0;
                        ts = MathHelper.Clamp(ts, t0 - 24, t0 + 24);
                        // 2 units per texel saturated at 126 texels, under 8 tiles, so every surface wider than
                        // that had no usable depth past its first few tiles. Half a unit reaches ~31.
                        shoreDepthByte = (byte)MathHelper.Clamp((float)Math.Round((y - ts) * 0.5f), 0f, 252f);
                    }
                    int tileIdx = (y / MaskTexelsPerTile) * tilesWide + (x / MaskTexelsPerTile);
                    byte effectValue = eff ? (byte)255 : (byte)0;
                    // Body-size calm: a small pool ripples/glints gentler than an open lake. A fish
                    // pond is never calm: the game animates it like the lake, and at half strength it
                    // read as untouched vanilla water beside a lake wearing the full effect.
                    bool pondTexel = _maskScratch.TilePondFlags != null && _maskScratch.TilePondFlags[tileIdx];
                    if (eff && !pondTexel) effectValue = (byte)(effectValue * _maskScratch.TileCalmnessValues![tileIdx] / 255);
                    // ALPHA tags the water TYPE for the shader: 0 = ICE (mirror, no ripple),
                    // 128 = LAVA (slow molten flow + self-glow, no mirror), 255 = normal water.
                    // PER PIXEL where a label said so, falling back to the tile verdict for art
                    // nobody has painted: the type used to be a whole-tile answer, so a tile painted
                    // 184 ice / 72 water froze all 256 and the river wore square patches wherever
                    // the ice met the water. The label knows which pixels are frozen; ask it.
                    int texelInTile = (y % MaskTexelsPerTile) * MaskTexelsPerTile + (x % MaskTexelsPerTile);
                    bool[]? iceBits2 = _maskScratch.TileIceBits![tileIdx], lavaBits2 = _maskScratch.TileLavaBits![tileIdx];
                    // Type ladder in ALPHA: 0 ice · 128 lava · 192 FLOWING · 255 plain water.
                    // 192 is new (the long-parked L4 flow tag): the entity mirror needs to tell a
                    // wet-fringe pixel (mirror a body there) from a waterfall face (never), and
                    // both used to ship as 255. 192 still passes the shader's step(0.75) wet-rim
                    // gate — a waterfall's plunge shore does glisten — and stays clear of the
                    // 0.9 plain-water gate the entity layer reads.
                    // Flowing reads per pixel too now, for the same reason ice does: a fountain
                    // tile holding both a jet and open pool used to tag all 256 texels 192, so
                    // the entity mirror refused a body standing in the pool.
                    bool[]? flowBits2 = _maskScratch.TileFlowBits![tileIdx];
                    byte flowAlpha = (flowBits2 != null ? flowBits2[texelInTile] : _maskScratch.TileFlowFlags![tileIdx]) ? (byte)192 : (byte)255;
                    byte alpha = iceBits2 != null || lavaBits2 != null
                        ? (iceBits2 != null && iceBits2[texelInTile] ? (byte)0 : lavaBits2 != null && lavaBits2[texelInTile] ? (byte)128 : flowAlpha)
                        : _maskScratch.TileIceFlags![tileIdx] ? (byte)0 : _maskScratch.TileLavaFlags![tileIdx] ? (byte)128 : flowAlpha;
                    // 240 = VESSEL: plain water inside a built wall (a fish pond). It passes every
                    // plain-water gate the shader has (all sit at or below 0.9), and the mirror reads
                    // it as "a water source behind this wall is sky": two tiles past a pond's wall on
                    // many farms is the lake, and a lake mirrored into a pond was the band players saw.
                    if (alpha == 255 && _maskScratch.TilePondFlags != null && _maskScratch.TilePondFlags[tileIdx])
                        alpha = 240;
                    _waterMaskPixels![texelIndex] = new Color(effectValue, march ? 255 : 0, shoreDepthByte, alpha);
                }
            }
        }

        /// <summary>Pass F - signed distance to the effect shoreline.</summary>
            // ---- Pass F — signed distance to the effect shoreline (3-4 chamfer, 1/3-texel
            // units). One field feeds the shader's quantized edge, the foam band and the wet
            // ground rim; encoded 128 + texels*4 → ±31.75 texels (~±2 tiles) of usable range,
            // which is more than any of its consumers ever look at.
        private void BuildShorelineDistanceField(int tilesWide, int tilesHigh)
        {
            int maskWidth = tilesWide * MaskTexelsPerTile;
            int maskHeight = tilesHigh * MaskTexelsPerTile;
            int texelCount = tilesWide * tilesHigh * MaskTexelsPerTile * MaskTexelsPerTile;

            if (_maskScratch.WaterSignedDistancePixels == null || _maskScratch.WaterSignedDistancePixels.Length < texelCount) _maskScratch.WaterSignedDistancePixels = new byte[texelCount];
            if (_maskScratch.DistanceToLand == null || _maskScratch.DistanceToLand.Length < texelCount) _maskScratch.DistanceToLand = new ushort[texelCount];
            if (_maskScratch.DistanceToWater == null || _maskScratch.DistanceToWater.Length < texelCount) _maskScratch.DistanceToWater = new ushort[texelCount];
            Chamfer34(_waterEffectBits!, true, _maskScratch.DistanceToWater, maskWidth, maskHeight);    // distance TO water (outside px)
            Chamfer34(_waterEffectBits!, false, _maskScratch.DistanceToLand, maskWidth, maskHeight);    // distance TO land (inside px)
            for (int texelIndex = 0; texelIndex < texelCount; texelIndex++)
            {
                float texels = _waterEffectBits![texelIndex] ? _maskScratch.DistanceToLand[texelIndex] / 3f : -(_maskScratch.DistanceToWater[texelIndex] / 3f);
                _maskScratch.WaterSignedDistancePixels[texelIndex] = (byte)MathHelper.Clamp(128f + texels * 4f, 0f, 255f);
            }

            // The SAME field again, measured on the water as it would be with nothing standing
            // in it. Everything the art carve took is put back, so the only edges left are the
            // ones where water meets real land.
            //
            // This exists because a bridge is a hole in the effect mask and a hole has an edge,
            // and the foam band asks nothing except "how far is the nearest edge". So a bridge
            // grew a drifting lap line down both its sides, which is the wavy edge that was
            // reported and which nothing about the ripple was ever going to fix.
            if (_maskScratch.RealShoreWaterBits == null || _maskScratch.RealShoreWaterBits.Length < texelCount) _maskScratch.RealShoreWaterBits = new bool[texelCount];
            if (_maskScratch.RealShoreDistancePixels == null || _maskScratch.RealShoreDistancePixels.Length < texelCount) _maskScratch.RealShoreDistancePixels = new byte[texelCount];
            var artCarved = _maskScratch.ArtCarvedFlags;
            var filled = _maskScratch.RealShoreWaterBits;
            for (int texelIndex = 0; texelIndex < texelCount; texelIndex++)
                filled[texelIndex] = _waterEffectBits![texelIndex] || (artCarved != null && texelIndex < artCarved.Length && artCarved[texelIndex]);
            // The two chamfer buffers are finished with above, so they are reused rather than
            // doubled: one rebuild runs at a time and these two passes are strictly ordered.
            Chamfer34(filled, true, _maskScratch.DistanceToWater, maskWidth, maskHeight);
            Chamfer34(filled, false, _maskScratch.DistanceToLand, maskWidth, maskHeight);
            for (int texelIndex = 0; texelIndex < texelCount; texelIndex++)
            {
                float texels = filled[texelIndex] ? _maskScratch.DistanceToLand[texelIndex] / 3f : -(_maskScratch.DistanceToWater[texelIndex] / 3f);
                _maskScratch.RealShoreDistancePixels[texelIndex] = (byte)MathHelper.Clamp(128f + texels * 4f, 0f, 255f);
            }
        }


        // ---- live debug overlay: what the mask ACTUALLY covers, per pixel ----

        /// <summary>Toggled by the radiance_maskview console command.</summary>
        internal static bool MaskView;
        private Texture2D? _maskDebugTexture;
        private Color[]? _maskDebugPixels;

        /// <summary>Readable recolor of the freshly composed mask (built only while the
        /// overlay is on): cyan = full water effect, orange = effect-only art water
        /// (fountains/puddles, softer), green = march (reflection) shoreline band.</summary>
        private void BuildMaskViewTex(int maskWidth, int maskHeight)
        {
            int texelCount = maskWidth * maskHeight;
            if (_maskDebugPixels == null || _maskDebugPixels.Length < texelCount)
                _maskDebugPixels = new Color[texelCount];
            for (int texelIndex = 0; texelIndex < texelCount; texelIndex++)
            {
                Color m = _waterMaskPixels![texelIndex];
                bool eff = m.R > 0, march = m.G > 0;
                _maskDebugPixels[texelIndex] =
                    eff && march ? new Color(0, m.R, 255) :          // cyan: effect + reflection water
                    eff ? new Color(255, (byte)(m.R / 2), 0) :       // orange: effect-only (soft art water)
                    march ? new Color(0, 220, 60) :                  // green: march-only (rare)
                    Color.Transparent;
                // Bright rim right AT the smoothed waterline (edge distance ~0) — the anchor line.
                if (march && m.B <= 2)
                    _maskDebugPixels[texelIndex] = new Color(120, 255, 120);
            }
            if (_maskDebugTexture == null || _maskDebugTexture.Width != maskWidth || _maskDebugTexture.Height != maskHeight)
            {
                _maskDebugTexture?.Dispose();
                _maskDebugTexture = new Texture2D(_device, maskWidth, maskHeight, false, SurfaceFormat.Color);
            }
            _maskDebugTexture.SetData(_maskDebugPixels, 0, texelCount);
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
