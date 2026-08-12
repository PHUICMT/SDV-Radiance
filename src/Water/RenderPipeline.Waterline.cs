using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Frames left on the water-side trace. The counterpart of the light watch, and written for
        /// the same reason it was: "the water flashes when someone walks" is a report about TIME,
        /// and every diagnostic we had was a snapshot of one frame. A snapshot cannot show a flash.
        ///
        /// <para>What it is looking for is the shape of that report. Walking rebuilds the mask,
        /// a rebuild bumps the epoch, and the epoch is what the full-map waterline anchor is keyed
        /// to - so the anchor goes stale the moment the player moves far enough, and the rebuild
        /// that would replace it is deliberately refused while the player is moving. Between those
        /// two the reflection is composed against a window-local shoreline instead of the map's
        /// own, which is a different answer for the same water. If that is what is happening, the
        /// trace shows the epoch bump and the anchor going stale on the frames the player reports
        /// a flash, and shows them returning to normal a moment after they stand still.</para>
        /// </summary>
        internal static int WaterWatchFrames;
        private int _watchWaterEpoch = int.MinValue, _watchWaterTileX = int.MinValue, _watchWaterTileY = int.MinValue;
        private bool _watchWaterAnchor, _watchWaterInMask, _watchWaterJob;
        private int _watchWaterMaskWidth, _watchWaterMaskHeight;

        /// <summary>
        /// The last few dozen things that changed on the water side, kept whether anyone asked or
        /// not.
        ///
        /// <para>Because the report is a snapshot and a flash is not. The reporter types ONE
        /// command, after the thing has already happened, and telling them to start a trace first
        /// and reproduce it on cue is asking for work nobody does: the report arrives with a
        /// screenshot and nothing else, which is the whole reason radiance_report exists. So the
        /// recording runs all the time - it is a handful of comparisons and a string only on the
        /// frames something actually moved - and typing the command afterwards is enough to see
        /// what the last few seconds looked like.</para>
        /// </summary>
        private readonly List<string> _waterLog = new();
        /// <summary>Reused, never re-allocated: see the note in ReportWaterWatch.</summary>
        private readonly System.Text.StringBuilder _waterChangeText = new();
        private const int WaterLogMax = 48;
        private GameLocation? _waterStatsLocation;
        private int _waterWindowMoves, _waterEpochBumps, _waterAnchorBuilds, _waterPresenceFlips, _waterStatsFrames;
        private int _waterSurfaceResizes;

        private void NoteWater(string what)
        {
            if (_waterLog.Count >= WaterLogMax)
                _waterLog.RemoveAt(0);
            _waterLog.Add($"t+{_waterStatsFrames,-6} {what}");
        }

        /// <summary>Record what changed on the water side this frame, and stream it to the console
        /// too while a watch is running.</summary>
        private void ReportWaterWatch()
        {
            var location = Game1.currentLocation;
            if (!ReferenceEquals(location, _waterStatsLocation))
            {
                _waterStatsLocation = location;
                _waterLog.Clear();
                _waterWindowMoves = _waterEpochBumps = _waterAnchorBuilds = _waterPresenceFlips = 0;
                _waterSurfaceResizes = 0;
                _waterStatsFrames = 0;
                _watchWaterEpoch = int.MinValue;
                _watchWaterTileX = _watchWaterTileY = int.MinValue;
                _watchWaterMaskWidth = _watchWaterMaskHeight = 0;
            }
            _waterStatsFrames++;

            bool anchor = location != null && AnchorFresh(location);
            bool job = _pendingWaterMaskJob != null;
            bool first = _watchWaterEpoch == int.MinValue;

            // Comparisons first, text second. This runs on EVERY frame and the overwhelming
            // majority of them have nothing to say, so the frames that say nothing must not
            // allocate: a per-frame StringBuilder is not slow on average, it is a steady drip of
            // garbage, and garbage shows up as a hitch rather than as a lower average - which is
            // the one shape of cost this mod is least allowed to add.
            bool invalidated = !first && MaskEpoch != _watchWaterEpoch;
            bool windowMoved = !first && (_lastWaterTileX != _watchWaterTileX || _lastWaterTileY != _watchWaterTileY);
            bool anchorChanged = !first && anchor != _watchWaterAnchor;
            bool presenceChanged = !first && _hasWaterInMask != _watchWaterInMask;
            bool jobChanged = !first && job != _watchWaterJob;
            // The surface is sized from the view, so resizing the window resizes it. That was
            // invisible here, which mattered: a crash and a misalignment both traced back to the
            // moment the mask changed size, and the report could not say whether it ever had.
            int maskWidth = _waterMask?.Width ?? 0, maskHeight = _waterMask?.Height ?? 0;
            bool resized = !first && maskWidth > 0
                           && (maskWidth != _watchWaterMaskWidth || maskHeight != _watchWaterMaskHeight);
            if (resized) _waterSurfaceResizes++;
            if (invalidated) _waterEpochBumps++;
            if (windowMoved) _waterWindowMoves++;
            if (anchorChanged && anchor) _waterAnchorBuilds++;
            if (presenceChanged) _waterPresenceFlips++;

            var changes = _waterChangeText;
            changes.Clear();
            if (invalidated) changes.Append($"  SURFACE THROWN AWAY: {MaskEpochReason}");
            if (windowMoved) changes.Append("  camera moved, surface rebuilt");
            if (anchorChanged)
                changes.Append(anchor
                    ? "  ANCHOR READY (the shoreline is the map's own now)"
                    : "  ANCHOR LOST (the shoreline is a guess from the screen edge again)");
            if (presenceChanged)
                changes.Append(_hasWaterInMask ? "  water entered the window" : "  water left the window");
            if (jobChanged) changes.Append(job ? "  rebuild started" : "  rebuild landed");
            if (resized)
                changes.Append($"  SURFACE RESIZED {_watchWaterMaskWidth}x{_watchWaterMaskHeight} -> {maskWidth}x{maskHeight}"
                             + " (the window or the zoom changed)");

            _watchWaterMaskWidth = maskWidth;
            _watchWaterMaskHeight = maskHeight;
            _watchWaterEpoch = MaskEpoch;
            _watchWaterTileX = _lastWaterTileX;
            _watchWaterTileY = _lastWaterTileY;
            _watchWaterAnchor = anchor;
            _watchWaterInMask = _hasWaterInMask;
            _watchWaterJob = job;

            if (changes.Length > 0)
                NoteWater($"origin=({_lastWaterTileX},{_lastWaterTileY}) anchor={(anchor ? "map" : "window-local")} "
                        + $"moving={(Game1.player?.isMoving() == true ? 1 : 0)}{changes}");

            if (WaterWatchFrames <= 0)
                return;
            WaterWatchFrames--;
            _monitor.Log($"[waterwatch] epoch={MaskEpoch} origin=({_lastWaterTileX},{_lastWaterTileY}) "
                       + $"anchor={(anchor ? "map" : "window-local")} ease={_waterInMaskEase:0.00} "
                       + $"moving={Game1.player?.isMoving() == true} restFrames={_waterlineFreshFrameCount}"
                       + (changes.Length > 0 ? changes.ToString() : "  steady"), LogLevel.Info);
        }

        /// <summary>
        /// The water side of the report: how the shoreline is being decided right now, how hard
        /// this player's view is making the mask work, and what actually happened in the seconds
        /// before they typed the command.
        /// </summary>
        internal string DescribeWaterHistory()
        {
            var sb = new System.Text.StringBuilder();
            var location = Game1.currentLocation;
            bool anchor = location != null && AnchorFresh(location);

            // How much slack the mask window has over the viewport, and how often that slack buys a
            // skipped rebuild. Worth stating in tiles rather than pixels: zoom and UI scale both
            // move it, and the rate is the thing a report from a 4K player and a report from a
            // 1080p player differ by. Read the real target rather than recomputing the formula, so
            // this line cannot go stale the way it did when the padding last changed.
            float viewTilesX = Game1.viewport.Width / 64f, viewTilesY = Game1.viewport.Height / 64f;
            int maskTilesX = _waterMask != null ? _waterMask.Width / 16 : (int)viewTilesX + 2 * MaskPadSideTiles;
            int maskTilesY = _waterMask != null ? _waterMask.Height / 16 : (int)viewTilesY + MaskPadTopTiles + MaskPadBottomTiles;
            sb.AppendLine($"your view covers {viewTilesX:0.0}x{viewTilesY:0.0} tiles, and the water surface is worked "
                        + $"out for {maskTilesX}x{maskTilesY} tiles around it. The extra is walking slack: it is "
                        + "rebuilt when the view reaches the edge of what was built, not every time you cross a tile.");
            // Said plainly, because "GUESSED FROM THE SCREEN EDGE" in a room with no water in it
            // reads as the fault the reporter came to report, and it is not one.
            sb.AppendLine("shoreline right now: " + (!_hasWaterInMask
                ? "not decided — there is no water in view here, so nothing below is a problem"
                : anchor ? "the map's own (stable)" : "GUESSED FROM THE SCREEN EDGE"));
            if (!anchor && _hasWaterInMask)
                sb.AppendLine("    -> the map-wide shoreline is only ever built while standing still, so walking "
                            + "in without pausing means every rebuild re-decides where the water's edge is. If the "
                            + "water changes as you walk and settles when you stop, this line is why.");
            if (_waterlineAnchorFailedForLocation && _waterlineFailedLocation == location)
                sb.AppendLine("    -> and it FAILED here, so it will not be retried for this location");
            // Written as sentences on purpose. This is read by the person filing the report, and
            // "invalidations by the world" meant nothing to anyone who had not written the code.
            sb.AppendLine($"since you arrived here ({_waterStatsFrames} frames):");
            sb.AppendLine($"    the surface was rebuilt {_waterWindowMoves} times because the camera moved");
            sb.AppendLine($"    it was thrown away {_waterEpochBumps} times because something reloaded the map under you");
            sb.AppendLine($"    the map-wide shoreline finished building {_waterAnchorBuilds} times");
            sb.AppendLine($"    water came into or left your view {_waterPresenceFlips} times");
            sb.AppendLine($"    it changed size {_waterSurfaceResizes} times because the window or the zoom changed");
            sb.AppendLine("what changed, most recent last (t+ is frames since you arrived here):");
            if (_waterLog.Count == 0)
                sb.AppendLine("    nothing has changed since you arrived, which means the surface is not being rebuilt at all");
            else
                foreach (string line in _waterLog)
                    sb.AppendLine("    " + line);
            return sb.ToString().TrimEnd();
        }

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
            newWaterMaskJob.ScreenId = _activeScreenId;
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
