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
            public GameLocation Loc = null!;
            public int LabelVer, Epoch, HookVer;
            public int PixW, PixH;
            public int[] ColStart = null!;   // length PixW+1; run indices for column x are [ColStart[x], ColStart[x+1])
            public short[] Tops = null!;     // run top row (inclusive), sorted per column
            public short[] Bots = null!;     // run bottom row (exclusive)
        }

        private WaterlineAnchor? _wlAnchor;
        private int _wlFreshFrames;              // consecutive frames the window mask was fresh
        private bool _wlAnchorFailedFor;         // one shot: don't retry a failed anchor for this loc
        private GameLocation? _wlFailedLoc;

        /// <summary>Full-map pixel budget for the anchor precompute. Guards absurd custom
        /// maps: past this the anchor is skipped and Pass D keeps its window-local answer.</summary>
        private const int WlMaxPixels = 24_000_000;   // ~366 MB of bool scratch would be silly

        private bool AnchorFresh(GameLocation loc) =>
            _wlAnchor is { } a && a.Loc == loc
            && a.LabelVer == CurrentLabelVersion()
            && a.Epoch == MaskEpoch
            && a.HookVer == WaterDrawHook.Version;

        /// <summary>Consume a finished ANCHOR job on the main thread: publish the compact
        /// run list and give the map-sized scratch buffers back to the GC.</summary>
        private void ConsumeAnchorJob(WaterMaskJob job)
        {
            if (!job.Failed)
            {
                int pw = job.TilesW * 16;
                _wlAnchor = new WaterlineAnchor
                {
                    Loc = job.Loc,
                    LabelVer = job.LabelVer,
                    Epoch = job.Epoch,
                    HookVer = job.HookVer,
                    PixW = pw,
                    PixH = job.TilesH * 16,
                    // A waterless map composes no runs — publish an empty anchor so the
                    // kick test stops re-gathering it every rest frame.
                    ColStart = job.AnchorColStart ?? new int[pw + 1],
                    Tops = job.AnchorTops ?? Array.Empty<short>(),
                    Bots = job.AnchorBots ?? Array.Empty<short>(),
                };
            }
            else
            {
                _wlAnchorFailedFor = true;
                _wlFailedLoc = job.Loc;
                if (!_loggedWaterJobFail)
                {
                    _monitor.Log("Waterline anchor compose failed; reflections fall back to window-local anchors here.", LogLevel.Warn);
                    _loggedWaterJobFail = true;
                }
            }
            FreeOversizedScratch();
        }

        /// <summary>Null every grow-only scratch buffer the full-map compose inflated.
        /// Jobs are serialized, so nothing is reading them; the next window job
        /// re-allocates them at window size.</summary>
        private void FreeOversizedScratch()
        {
            _waterMaskCoreBuf = null; _waterBoolBuf = null; _waterPixBuf = null;
            _waterPixBits = null; _waterPixBits2 = null;
            _bigCarveBuf = null; _bigSeedBuf = null;
            _edgeBuf = null; _edgeSum = null; _edgeCnt = null;
            _tileBitsBuf = null; _tileKeepBuf = null; _tileCarveBBuf = null; _tileCarveFBuf = null;
            _tileBigSolidBuf = null; _tileDeckBuf = null; _tileLabeledBuf = null; _tileHasBldBuf = null;
            _tileOverlayGroundBuf = null; _tileOverlayGroundFBuf = null;
            _tileIceBitsBuf = null; _tileLavaBitsBuf = null;
            _tileLandNearBuf = null; _tileHasFrontBuf = null;
            _tileIceBuf = null; _tileFlowBuf = null; _tileLavaBuf = null;
            _tileWetFlag = null; _tileCalmBuf = null;
            _waterSdfBuf = null; _sdfIn = null; _sdfOut = null;
        }

        /// <summary>Kick the full-map anchor job if this location still needs one and the
        /// moment is cheap: the window mask is fresh, no job is in flight, and the player
        /// is resting (the full-map gather is a one-off ~tens-of-ms main-thread cost we
        /// hide in a stand-still frame, never mid-walk). Returns true when a job was kicked.</summary>
        private bool MaybeKickAnchorJob(GameLocation loc)
        {
            if (AnchorFresh(loc))
                return false;
            if (_wlAnchorFailedFor && _wlFailedLoc == loc)
                return false;
            _wlAnchorFailedFor = false;
            if (Game1.game1.takingMapScreenshot || Game1.player?.isMoving() == true)
                return false;
            if (++_wlFreshFrames < 15)
                return false;

            var back = loc.map?.GetLayer("Back");
            if (back == null)
                return false;
            int mw = back.LayerWidth, mh = back.LayerHeight;
            if (mw <= 0 || mh <= 0 || (long)mw * mh * 256 > WlMaxPixels)
                return false;

            long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var njob = GatherWaterMask(loc, 0, 0, mw, mh);
            njob.AnchorOnly = true;
            double gatherMs = (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _monitor.Log($"[diag] waterline-anchor gather {mw}x{mh} = {gatherMs:0.0}ms", LogLevel.Trace);

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
            _wlFreshFrames = 0;
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
                    bool w = y < ph && _waterPixBits2![y * pw + x];
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
            job.AnchorColStart = colStart;
            job.AnchorTops = tops.ToArray();
            job.AnchorBots = bots.ToArray();
        }

        /// <summary>Worker-side, normal window job: replace window-local run tops with the
        /// location-wide anchor's. A pixel whose column run reaches above the window gets a
        /// NEGATIVE top — the depth encode keeps measuring from the true shoreline instead
        /// of re-basing at the window edge. Pixels the (possibly stale) anchor doesn't know
        /// keep their window-local answer, so a just-moved couch never punches a hole.</summary>
        private void OverrideEdgeFromAnchor(WaterMaskJob job, int pw, int ph)
        {
            var wa = job.Anchor!;
            int px0 = job.Tx * 16, py0 = job.Ty * 16;
            for (int x = 0; x < pw; x++)
            {
                int wx = px0 + x;
                if (wx < 0 || wx >= wa.PixW)
                    continue;
                int r = wa.ColStart[wx], rEnd = wa.ColStart[wx + 1];
                if (r == rEnd)
                    continue;
                for (int y = 0; y < ph; y++)
                {
                    int p = y * pw + x;
                    if (!_waterPixBits2![p])
                        continue;
                    int wy = py0 + y;
                    while (r < rEnd && wa.Bots[r] <= wy) r++;
                    if (r == rEnd)
                        break;   // past the last run in this column
                    if (wa.Tops[r] <= wy)
                        _edgeBuf![p] = (short)(wa.Tops[r] - py0);
                }
            }
        }
    }
}
