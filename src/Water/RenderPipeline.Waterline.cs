using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - LOCATION-WIDE waterline anchor (P3a of the water-V4 rework).
    ///
    /// Pass D used to find each column's shoreline INSIDE the padded window only. A water
    /// body taller than the window clamped its run-top at the window's first row, so the
    /// reflection re-based itself every tile the player walked — the "reflection shrinks
    /// while walking" bug. The fix is one precompute per location: run the same gather +
    /// compose over the WHOLE map once (through the same serialized job queue, while the
    /// camera is resting), keep only a compact per-pixel-column run list (a few tens of
    /// KB), and let the window compose look its run tops up there. A top above the window
    /// simply comes out negative — the depth encode keeps counting from the TRUE shore.
    ///
    /// The full-map job reuses the window job's scratch buffers, which grow to map size;
    /// they are nulled right after the runs are extracted so the next window job
    /// re-allocates at window size.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Per-column water-run intervals for one whole location, in map pixel
        /// space (16 px per tile, origin 0,0). Immutable once published.</summary>
        private sealed class WaterlineAnchor
        {
            public GameLocation Location = null!;
            public int LabelVersion, Epoch, WaterDrawHookVersion;
            public int PixelWidth, PixelHeight;
            public int[] ColumnRunStartIndices = null!;   // length PixelWidth+1; run indices for column x are [ColumnRunStartIndices[x], ColumnRunStartIndices[x+1])
            public short[] RunTopRows = null!;     // run top row (inclusive), sorted per column
            public short[] RunBottomRows = null!;     // run bottom row (exclusive)
        }

        private WaterlineAnchor? _waterlineAnchorData;
        private int _waterlineFreshFrameCount;              // consecutive frames the window mask was fresh
        private bool _waterlineAnchorFailedForLocation;         // one shot: don't retry a failed anchor for this location
        private GameLocation? _waterlineFailedLocation;

        /// <summary>Full-map pixel budget for the anchor precompute. Guards absurd custom
        /// maps: past this the anchor is skipped and Pass D keeps its window-local answer.</summary>
        private const int WlMaxPixels = 24_000_000;   // ~366 MB of bool scratch would be silly

        private bool AnchorFresh(GameLocation location) =>
            _waterlineAnchorData is { } a && a.Location == location
            && a.LabelVersion == CurrentLabelVersion()
            && a.Epoch == MaskEpoch
            && a.WaterDrawHookVersion == WaterDrawHook.Version;

        /// <summary>Consume a finished ANCHOR job on the main thread: publish the compact
        /// run list and give the map-sized scratch buffers back to the GC.</summary>
        private void ConsumeAnchorJob(WaterMaskJob job)
        {
            if (!job.Failed)
            {
                int pw = job.TileWidth * 16;
                _waterlineAnchorData = new WaterlineAnchor
                {
                    Location = job.Location,
                    LabelVersion = job.LabelVersion,
                    Epoch = job.Epoch,
                    WaterDrawHookVersion = job.WaterDrawHookVersion,
                    PixelWidth = pw,
                    PixelHeight = job.TileHeight * 16,
                    // A waterless map composes no runs — publish an empty anchor so the
                    // kick test stops re-gathering it every rest frame.
                    ColumnRunStartIndices = job.AnchorColumnRunStartIndices ?? new int[pw + 1],
                    RunTopRows = job.AnchorRunTopRows ?? Array.Empty<short>(),
                    RunBottomRows = job.AnchorRunBottomRows ?? Array.Empty<short>(),
                };
            }
            else
            {
                _waterlineAnchorFailedForLocation = true;
                _waterlineFailedLocation = job.Location;
                if (!_waterMaskJobFailureLogged)
                {
                    _monitor.Log("Waterline anchor compose failed; reflections fall back to window-local anchors here.", LogLevel.Warn);
                    _waterMaskJobFailureLogged = true;
                }
            }
            FreeOversizedScratch();
        }

        /// <summary>Null every grow-only scratch buffer the full-map compose inflated.
        /// Jobs are serialized, so nothing is reading them; the next window job
        /// re-allocates them at window size.</summary>
        private void FreeOversizedScratch()
        {
            _waterMaskCorePixels = null; _waterTileFlags = null; _waterMaskPixels = null;
            _waterEffectBits = null; _waterMarchBits = null;
            _tileNearSolidFlags = null; _tileLandConnectedFlags = null;
            _waterlineTopRowByPixel = null; _waterlineRowPrefixSums = null; _waterlineRowSampleCounts = null;
            _tileEffectBits = null; _tileWaterKeepBits = null; _tileBuildingCarveBits = null; _tileFrontCarveBits = null;
            _tileLargeSolidFlags = null; _tileDeckFlags = null; _tileLabeledLiquidFlags = null; _tileHasBuildingArtFlags = null;
            _tileBuildingGroundOverlayFlags = null; _tileFrontGroundOverlayFlags = null;
            _tileIceBits = null; _tileLavaBits = null; _tileFlowBits = null;
            _marchOutsideFlags = null; _marchFloodStack = null; _speckVisitedFlags = null; _speckComponentMembers = null;
            _tileNearLandFlags = null; _tileHasFrontArtFlags = null;
            _tileIceFlags = null; _tileFlowFlags = null; _tileLavaFlags = null;
            _tileHasEffectWaterFlags = null; _tileCalmnessValues = null;
            _waterSignedDistancePixels = null; _distanceToLandScratch = null; _distanceToWaterScratch = null;
        }

        /// <summary>Kick the full-map anchor job if this location still needs one and the
        /// moment is cheap: the window mask is fresh, no job is in flight, and the player
        /// is resting (the full-map gather is a one-off ~tens-of-ms main-thread cost we
        /// hide in a stand-still frame, never mid-walk). Returns true when a job was kicked.</summary>
        private bool MaybeKickAnchorJob(GameLocation location)
        {
            if (AnchorFresh(location))
                return false;
            if (_waterlineAnchorFailedForLocation && _waterlineFailedLocation == location)
                return false;
            _waterlineAnchorFailedForLocation = false;
            if (Game1.game1.takingMapScreenshot || Game1.player?.isMoving() == true)
                return false;
            if (++_waterlineFreshFrameCount < 15)
                return false;

            var back = location.map?.GetLayer("Back");
            if (back == null)
                return false;
            int mw = back.LayerWidth, mh = back.LayerHeight;
            if (mw <= 0 || mh <= 0 || (long)mw * mh * 256 > WlMaxPixels)
                return false;

            long gatherStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var newWaterMaskJob = GatherWaterMask(location, 0, 0, mw, mh);
            newWaterMaskJob.AnchorOnly = true;
            double gatherDurationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - gatherStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _monitor.Log($"[diag] waterline-anchor gather {mw}x{mh} = {gatherDurationMilliseconds:0.0}ms", LogLevel.Trace);

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
            _waterlineFreshFrameCount = 0;
            return true;
        }

        /// <summary>Worker-side tail of an ANCHOR compose: turn the full-map march bits
        /// into per-column run intervals. Runs already had Pass D's &lt;6-texel specks
        /// dropped, so this list is exactly what a window's Pass D would have seen.</summary>
        private void ExtractAnchorRuns(WaterMaskJob job, int pw, int ph)
        {
            var colStart = new int[pw + 1];
            var tops = new System.Collections.Generic.List<short>(pw / 2);
            var bots = new System.Collections.Generic.List<short>(pw / 2);
            for (int x = 0; x < pw; x++)
            {
                colStart[x] = tops.Count;
                int top = -1;
                for (int y = 0; y <= ph; y++)
                {
                    bool w = y < ph && _waterMarchBits![y * pw + x];
                    if (w) { if (top < 0) top = y; }
                    else if (top >= 0)
                    {
                        tops.Add((short)top);
                        bots.Add((short)y);
                        top = -1;
                    }
                }
            }
            colStart[pw] = tops.Count;
            job.AnchorColumnRunStartIndices = colStart;
            job.AnchorRunTopRows = tops.ToArray();
            job.AnchorRunBottomRows = bots.ToArray();
        }

        /// <summary>Worker-side, normal window job: replace window-local run tops with the
        /// location-wide anchor's. A pixel whose column run reaches above the window gets a
        /// NEGATIVE top — the depth encode keeps measuring from the true shoreline instead
        /// of re-basing at the window edge. Pixels the (possibly stale) anchor doesn't know
        /// keep their window-local answer, so a just-moved couch never punches a hole.</summary>
        private void OverrideEdgeFromAnchor(WaterMaskJob job, int pw, int ph)
        {
            var wa = job.Anchor!;
            int px0 = job.StartTileX * 16, py0 = job.StartTileY * 16;
            for (int x = 0; x < pw; x++)
            {
                int wx = px0 + x;
                if (wx < 0 || wx >= wa.PixelWidth)
                    continue;
                int r = wa.ColumnRunStartIndices[wx], rEnd = wa.ColumnRunStartIndices[wx + 1];
                if (r == rEnd)
                    continue;
                for (int y = 0; y < ph; y++)
                {
                    int p = y * pw + x;
                    if (!_waterMarchBits![p])
                        continue;
                    int wy = py0 + y;
                    while (r < rEnd && wa.RunBottomRows[r] <= wy) r++;
                    if (r == rEnd)
                        break;   // past the last run in this column
                    if (wa.RunTopRows[r] <= wy)
                        _waterlineTopRowByPixel![p] = (short)(wa.RunTopRows[r] - py0);
                }
            }
        }
    }
}
