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
        /// <summary>
        /// One mask pixel from the CPU copy, in mask-pixel coordinates, or null when that pixel is
        /// not inside the buffer as it currently stands.
        ///
        /// Every reader must come through here. The two that did not both checked their coordinates
        /// against <c>_waterMask</c>, the GPU texture, and then indexed <c>_waterMaskPixels</c>, the
        /// CPU array, which are two different objects that are only usually the same size. That
        /// crashed a player on 1.5.3 with an index outside the bounds of the array, in the same
        /// frame the game reported the window changing size. It is the same mistake as the light
        /// cluster crash: a bound taken from one object and used on another is not a bound.
        /// </summary>
        /// <remarks>
        /// Known limit on a split screen: the pixels belong to whichever rebuild ran last, which
        /// may be another screen's, so the wading test can read the wrong screen's water for a few
        /// frames after a swap. Making the buffer per-screen was tried and is WRONG: the worker
        /// thread writes into this field while it composes, so swapping it mid-flight sent one
        /// screen's compose into the other screen's array. That showed up immediately as one pond
        /// losing its effects, the reflection appearing on the other player's half, and the screen
        /// that did have water flickering. The real fix is for a rebuild to own its own buffer,
        /// which is more than a release-day change.
        /// </remarks>
        private Color? ReadWaterMaskPixel(int maskPixelX, int maskPixelY)
        {
            Color[]? pixels = _waterMaskPixels;
            Texture2D? mask = _waterMask;
            if (pixels == null || mask == null)
                return null;
            if (maskPixelX < 0 || maskPixelY < 0 || maskPixelX >= mask.Width || maskPixelY >= mask.Height)
                return null;
            int index = maskPixelY * mask.Width + maskPixelX;
            return index < pixels.Length ? pixels[index] : null;
        }

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

        /// <summary>How far past the view the water surface is worked out, in tiles. The sides and
        /// the bottom are the walking slack (see BuildWaterMask); the TOP is also what a column's
        /// waterline anchor needs, because a shoreline scrolling just past the top edge must keep
        /// its world-anchored run top instead of re-basing on the mask's own first row - which made
        /// a whole reflection vanish in one step as the player walked away from it.
        ///
        /// <para>The top is also how far the MIRROR reads: the shader asks the mask whether a
        /// mirrored source is itself water, and with the mask ending six tiles up and the mirror
        /// reading twelve, the six tiles between were answered by the mask's clamped edge row,
        /// which moves with the window. The window now keeps the whole reach covered AT ALL
        /// TIMES, not only on the frame it is built: <see cref="MaskTopCoverTiles"/> rows above
        /// the view are guaranteed, and the slack beyond them is what the walk spends before a
        /// rebuild. Covering the reach only when built was tried first, and a reflection that
        /// reached past the window's top came and went every twelve tiles of walking.</para></summary>
        private const int MaskPadSideTiles = 4;
        /// <summary>Rows above the view the mask must always hold: the mirror's reach plus the
        /// one-tile fade the shader applies at the mask's top edge.</summary>
        private const int MaskTopCoverTiles = MirrorTopReachPx / 64 + 1;
        private const int MaskTopSlackTiles = 5;
        private const int MaskPadTopTiles = MaskTopCoverTiles + MaskTopSlackTiles;
        private const int MaskPadBottomTiles = 4;

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
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return false;

            // Bulk-read this location's tilesheets on entry, so every first-touch GPU readback
            // lands here (during the fade-covered location change) rather than hitching mid-walk.
            PrewarmSheetPixels(location);

            (int startTileX, int startTileY, int tilesW, int tilesH) = ChooseMaskWindow(location);

            if (PollPendingMaskJob(location, startTileX, startTileY, tilesW, tilesH))
                return _hasWaterInMask;
            if (CurrentMaskStillFits(location, startTileX, startTileY, tilesW, tilesH))
                return _hasWaterInMask;

            StartWaterMaskRebuild(location, startTileX, startTileY, tilesW, tilesH);
            return _hasWaterInMask;   // old mask renders this frame; the swap lands when compose does
        }

        /// <summary>Where the mask window sits this frame, and the camera-follow params that go
        /// with whatever mask is currently bound. Keeps the existing window while the view still
        /// fits inside its padding, which is what turns a rebuild per tile into one every few.</summary>
        private (int startTileX, int startTileY, int tilesW, int tilesH) ChooseMaskWindow(GameLocation location)
        {
            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            // The window is PADDED past the viewport: 2 tiles left/right, 4 above. A
            // column's waterline anchor (Pass D run-top) must stay WORLD-anchored while
            // its shoreline scrolls just past the screen edge — anchored at the mask's
            // own first row instead, the whole reflection re-based and vanished in ONE
            // step as the player walked away, rather than fading out.
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed
            // out — parts of the screen simply had no water mask (no ripple/reflection).
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + 2 * MaskPadSideTiles);
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + MaskPadTopTiles + MaskPadBottomTiles);
            int startTileX = (int)Math.Floor(vx / 64f) - MaskPadSideTiles;
            int startTileY = (int)Math.Floor(vy / 64f) - MaskPadTopTiles;

            // KEEP THE WINDOW WE ALREADY BUILT while the view still fits inside it.
            //
            // The padding above exists so the mask covers a little more than the screen, and that
            // slack used to be spent on nothing: the origin was recomputed from the camera every
            // frame, so crossing a single tile boundary moved it by one tile and rebuilt the whole
            // surface. Walking therefore paid a full gather PER TILE, and the gather is a
            // main-thread cost that a player measured at 11 ms on a busy map. That is the hitch
            // reported crossing from town onto the beach, where the walk enters a screenful of
            // water and every step re-reads it.
            //
            // The mask content is world-anchored and the shader is told the real origin
            // (MaskOrigin), so a window that is off-centre is already correct to draw from. Only
            // re-anchor when the view actually reaches an edge, which turns a rebuild every tile
            // into a rebuild every few.
            if (_waterMask != null && location == _lastWaterLocation
                && _waterMask.Width == tilesW * 16 && _waterMask.Height == tilesH * 16)
            {
                int viewLeft = (int)Math.Floor(vx / 64f), viewTop = (int)Math.Floor(vy / 64f);
                int viewRight = (int)Math.Floor((vx + Game1.viewport.Width) / 64f);
                int viewBottom = (int)Math.Floor((vy + Game1.viewport.Height) / 64f);
                // Above the view the window must keep the mirror's whole reach, not merely the
                // view itself: the rows the mirror reads are up there, and a window that let the
                // view walk up to its own top edge answered "is that source water" from its
                // clamped edge row for the last twelve tiles of every walk north.
                if (viewLeft >= _lastWaterTileX && viewRight <= _lastWaterTileX + tilesW - 1
                    && viewTop - MaskTopCoverTiles >= _lastWaterTileY && viewBottom <= _lastWaterTileY + tilesH - 1)
                {
                    startTileX = _lastWaterTileX;
                    startTileY = _lastWaterTileY;
                }
            }

            // Camera-follow params are valid for WHATEVER mask is currently bound (old or
            // new) — the mask content is tile-anchored; sub-tile scroll lives here.
            _waterMaskTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
            _waterMaskWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            return (startTileX, startTileY, tilesW, tilesH);
        }

        /// <summary>Deal with a compose that is already running. True means this frame is done:
        /// either the job belongs to another screen, or it is still going, or it just landed.</summary>
        private bool PollPendingMaskJob(GameLocation location, int startTileX, int startTileY,
                                        int tilesW, int tilesH)
        {
            // Poll the in-flight compose FIRST: apply it if it finished and still matches
            // the wanted window; keep showing the old mask while it runs; discard it if
            // the camera crossed again mid-compose (fall through to a fresh gather).
            // A rebuild belonging to a screen that has since left can never be claimed: nothing
            // will match its id again, so it would hold the one slot for the rest of the session
            // and every remaining screen would keep deferring to it. The water surface would stop
            // rebuilding for everybody the moment a split-screen player dropped out.
            if (_pendingWaterMaskJob is { } orphan && orphan.ScreenId != _activeScreenId
                && !ScreenStillExists(orphan.ScreenId))
                _pendingWaterMaskJob = null;

            if (_pendingWaterMaskJob is { } job)
            {
                // Another screen's rebuild: leave it alone, keep showing this screen's own mask,
                // and do not start a second one. One rebuild at a time is what lets the gather and
                // compose buffers be shared without locks.
                if (job.ScreenId != _activeScreenId)
                    return true;
                if (!job.Done)
                    return true;
                if (job.ApplyTexturesDone == 0)
                    NoteWaterRebuildCost(compose: job.ComposeDurationMilliseconds);
                if (job.AnchorOnly)
                {
                    // P3a: publish the location-wide waterline anchor and shrink the
                    // map-sized scratch back down. The window mask was fresh when this
                    // was kicked; fall through so a camera move still rebuilds it now.
                    _pendingWaterMaskJob = null;
                    ConsumeAnchorJob(job);
                }
                else if (job.Failed)
                {
                    _pendingWaterMaskJob = null;
                    if (!_waterMaskJobFailureLogged) { _monitor.Log("Water mask compose failed once; rebuilding synchronously.", LogLevel.Warn); _waterMaskJobFailureLogged = true; }
                }
                else if (job.Location == location && job.StartTileX == startTileX && job.StartTileY == startTileY
                    && job.TileWidth == tilesW && job.TileHeight == tilesH)
                {
                    // One texture per frame; the job stays pending, and the old mask stays up,
                    // until the last one swaps the pairs (RenderPipeline.WaterMask.Apply.cs).
                    long applyStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool applied = ApplyWaterMaskStep(job);
                    NoteWaterRebuildCost(apply: (System.Diagnostics.Stopwatch.GetTimestamp() - applyStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    if (applied)
                        _pendingWaterMaskJob = null;
                    return true;
                }
                else
                {
                    // The camera crossed again while this one composed or uploaded: drop it and
                    // gather the window that is wanted now. A half-written spare is never shown.
                    _pendingWaterMaskJob = null;
                }
            }
            return false;
        }

        /// <summary>True when the mask we already hold is still the right one, so nothing needs
        /// rebuilding at all.</summary>
        private bool CurrentMaskStillFits(GameLocation location, int startTileX, int startTileY,
                                          int tilesW, int tilesH)
        {
            // The mask content is TILE-ANCHORED (sub-tile camera scroll is handled by the
            // WorldTileOffset shader param), so it only changes when the view crosses a tile
            // boundary — rebuilding the pixel grid every frame was a walking-stutter tax.
            // The 10 s safety refresh only exists to pick up rare map mutations (a bridge
            // built, ice melting); everything routine invalidates via location/origin keys,
            // and world EVENTS (a fish pond placed, a map re-patched) bump MaskEpoch so the
            // change lands on the next frame instead of up to 10 s late.
            if (_waterMask != null && location == _lastWaterLocation && startTileX == _lastWaterTileX && startTileY == _lastWaterTileY
                && _lastWaterHookVersion == WaterDrawHook.Version
                && _lastWaterLabelVersion == CurrentLabelVersion()
                && _lastWaterEpoch == MaskEpoch
                // Height as well as width. Checking only the width let a window that changed
                // height alone take this path and then report the NEW height to the shader as
                // MaskSize, for a mask that had been built at the old one, so the water sat
                // misaligned until the next rebuild up to ten seconds later.
                && _waterMask.Width == tilesW * 16 && _waterMask.Height == tilesH * 16
                && Game1.ticks - _lastWaterBuildTick < 600)
            {
                _waterMaskPixelSize = new Vector2(tilesW, tilesH);
                // The window is fresh and no job is in flight — the cheap moment to build
                // this location's full-map waterline anchor if it doesn't have one yet.
                if (_hasWaterInMask)
                    MaybeKickAnchorJob(location);
                return true;
            }
            return false;
        }

        // The three halves of a water mask rebuild, timed apart. The report's `grid: water mask`
        // row brackets the gather and the apply together and shows a 2.4 ms worst frame on a farm
        // walk; which half that is decides whether the fix is a map-wide gather cache or a sliced
        // upload, and until these existed the question could only be argued.
        private double _waterGatherWorstScroll, _waterGatherWorstArrival, _waterGatherWorstRefresh, _waterApplyWorst, _waterComposeWorst;
        private int _waterGatherCount, _waterGatherArrivalCount, _waterGatherRefreshCount, _waterApplyCount;
        private double _waterGatherSum, _waterGatherArrivalSum, _waterGatherRefreshSum, _waterApplySum;

        /// <summary>Which kind of rebuild a gather was, so the report can keep the three apart: a
        /// scroll copies from the map memory, a refresh asks the window afresh, an arrival has
        /// nothing remembered yet.</summary>
        private enum GatherKind { Scroll, Refresh, Arrival }

        private void NoteWaterRebuildCost(double gather = -1, GatherKind kind = GatherKind.Scroll, double apply = -1, double compose = -1)
        {
            if (gather >= 0)
            {
                switch (kind)
                {
                    case GatherKind.Scroll:
                        _waterGatherWorstScroll = Math.Max(_waterGatherWorstScroll, gather);
                        _waterGatherSum += gather; _waterGatherCount++;
                        break;
                    case GatherKind.Refresh:
                        _waterGatherWorstRefresh = Math.Max(_waterGatherWorstRefresh, gather);
                        _waterGatherRefreshSum += gather; _waterGatherRefreshCount++;
                        break;
                    default:
                        _waterGatherWorstArrival = Math.Max(_waterGatherWorstArrival, gather);
                        _waterGatherArrivalSum += gather; _waterGatherArrivalCount++;
                        break;
                }
            }
            if (apply >= 0) { _waterApplyWorst = Math.Max(_waterApplyWorst, apply); _waterApplySum += apply; _waterApplyCount++; }
            if (compose >= 0) _waterComposeWorst = Math.Max(_waterComposeWorst, compose);
        }

        /// <summary>The rebuild halves since the last report, then reset.</summary>
        internal string DescribeWaterRebuildCost()
        {
            string text = "water mask rebuild, main thread, since the last report:\n"
                + $"  gather on a scroll (copied from the map memory)  {_waterGatherCount} time(s)  avg {(_waterGatherCount > 0 ? _waterGatherSum / _waterGatherCount : 0):0.000} ms  worst {_waterGatherWorstScroll:0.000} ms\n"
                + $"  gather on the 10 s refresh (copied where the map's tiles are unchanged)  {_waterGatherRefreshCount} time(s)  avg {(_waterGatherRefreshCount > 0 ? _waterGatherRefreshSum / _waterGatherRefreshCount : 0):0.000} ms  worst {_waterGatherWorstRefresh:0.000} ms\n"
                + $"  gather on arrival (nothing remembered yet)  {_waterGatherArrivalCount} time(s)  avg {(_waterGatherArrivalCount > 0 ? _waterGatherArrivalSum / _waterGatherArrivalCount : 0):0.000} ms  worst {_waterGatherWorstArrival:0.000} ms\n"
                + $"  apply (one of the four textures per frame)  {_waterApplyCount} time(s)  avg {(_waterApplyCount > 0 ? _waterApplySum / _waterApplyCount : 0):0.000} ms"
                + $"  worst {_waterApplyWorst:0.000} ms\n"
                + $"  compose, on the worker thread, worst {_waterComposeWorst:0.000} ms (not on the frame)\n"
                + DescribeGatherCache();
            _waterGatherWorstScroll = _waterGatherWorstArrival = _waterGatherWorstRefresh = _waterApplyWorst = _waterComposeWorst = 0;
            _waterGatherCount = _waterGatherArrivalCount = _waterGatherRefreshCount = _waterApplyCount = 0;
            _waterGatherSum = _waterGatherArrivalSum = _waterGatherRefreshSum = _waterApplySum = 0;
            return text;
        }

        /// <summary>Gather the window on this thread, then compose it on a task. The old mask keeps
        /// rendering until the new one lands.</summary>
        private void StartWaterMaskRebuild(GameLocation location, int startTileX, int startTileY,
                                           int tilesW, int tilesH)
        {
            // The ten second safety refresh (CurrentMaskStillFits) is here to notice a map that
            // changed under us without saying so. The gather notices that per tile now, by the
            // identity of the tile objects the map holds (GatherCache.cs), so the refresh is an
            // ordinary rebuild; it is still counted apart so the report can tell the two kinds.
            GatherKind kind = GatherKind.Scroll;
            if (location != _lastWaterLocation)
                kind = GatherKind.Arrival;
            else if (Game1.ticks - _lastWaterBuildTick >= 600)
                kind = GatherKind.Refresh;
            long gatherStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var newWaterMaskJob = GatherWaterMask(location, startTileX, startTileY, tilesW, tilesH);
            newWaterMaskJob.ScreenId = _activeScreenId;
            double gatherDurationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - gatherStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            NoteWaterRebuildCost(gather: gatherDurationMilliseconds, kind);
            if (gatherDurationMilliseconds > 8)
                _monitor.Log($"[diag] water gather={gatherDurationMilliseconds:0.0}ms ({(location == _lastWaterLocation ? "scroll" : "location change")})", LogLevel.Debug);

            // On a LOCATION change the old mask is another map's content — turn the stage
            // off until the new compose lands (1-2 frames, hidden inside the warp fade).
            // A same-map scroll/zoom keeps rendering the old mask: its content is
            // world-anchored, so the old origin+size still map correctly.
            if (location != _lastWaterLocation || _waterMask == null)
                _hasWaterInMask = false;
            ShadowRenderer.WaterOnScreen = _hasWaterInMask;

            // The gather already knows, on this thread, whether the window it just read contains
            // water — but `_hasWaterInMask` was only ever updated when a COMPOSE landed, and a compose
            // is discarded whenever the view moved while it ran. Walk continuously and no job ever
            // matches on completion, so the flag keeps whatever it held the last time the player
            // stood still: the same tile measured wAny=1 on one pass and wAny=0 on another, which
            // is what took the stage in and out and read as the picture stepping brighter/darker.
            // Turn ON from the fresh gather immediately; leave turning OFF to a completed compose,
            // which also knows about label-only water. Lingering costs nothing (there is no water
            // on screen to affect), while dropping out early is exactly the visible fault.
            if (newWaterMaskJob.AnyWater)
                _hasWaterInMask = true;
            // Keep the bake gate's copy current from every write site, not just the compose:
            // a stale FALSE here is the visible direction (water arrives, the mirror has no
            // player in it until the flag catches up).
            ShadowRenderer.WaterOnScreen = _hasWaterInMask;

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
        }

        /// <summary>Is there any water within <paramref name="radiusTiles"/> of this tile in the
        /// current mask window? Both the sprite mask and the reflection RT stamp EVERY body, tree
        /// and placed object on screen, but only the ones whose pixels can actually meet water
        /// change anything: on a map with water in one corner that is a screenful of draw calls
        /// per frame spent on sprites nowhere near it. Unknown state answers yes, so a missing
        /// mask never silently drops an exclusion.
        ///
        /// <para>
        /// Answered from a SUMMED-AREA TABLE over the window's water flags, which makes the test
        /// four array reads whatever the radius is. The scan it replaces was the right shape for
        /// the handful of characters it was written for, and the wrong one by the time every tree,
        /// bush, grass tuft, building and placed object on screen asked the same question: the
        /// widest caller (a building, radius 9) walks a 19x19 block, and on a map where the answer
        /// is NO - which is the common case, and the case the gate exists to make cheap - it walks
        /// all 361 cells before saying so. Hundreds of callers times hundreds of cells is where a
        /// large part of the entity mirror's third of a millisecond of pure CPU was going.
        /// </para>
        ///
        /// <para>The table is rebuilt when the mask window is, not per frame, and the answer it
        /// gives is the same answer cell for cell - this changes what the test costs and nothing
        /// about what it decides.</para></summary>
        /// <summary>One texel per tile of the whole location: how tall the map itself is there, for
        /// the mirror to ask about the thing it is about to reflect. 0 flat ground (and void), 64 a
        /// deck, 128 water (the mirror has a rule of its own for that), 255 a wall, a roof or glass.
        /// Rebuilt only when the location's surface map is a different object, so it costs a walk of
        /// the tile grid once per location and a dictionary lookup per frame.</summary>
        private Texture2D? _surfaceClassTexture;
        private SurfaceMap? _surfaceClassSource;
        /// <summary>Bound when there is no surface map: the water value, which the mirror treats as
        /// nothing special. An unbound slot samples 0, which is ground, and would end every mirror
        /// a tile down.</summary>
        private Texture2D? _neutralSurfaceTexture;

        private Texture2D SurfaceClassTextureFor(GameLocation? location)
        {
            var surf = SurfaceMap.For(location);
            if (surf == null || surf.Width <= 0 || surf.Height <= 0)
            {
                if (_neutralSurfaceTexture == null || _neutralSurfaceTexture.IsDisposed)
                {
                    _neutralSurfaceTexture = new Texture2D(_device, 1, 1, false, SurfaceFormat.Alpha8);
                    _neutralSurfaceTexture.SetData(new byte[] { 128 });
                }
                return _neutralSurfaceTexture;
            }
            if (ReferenceEquals(surf, _surfaceClassSource) && _surfaceClassTexture != null && !_surfaceClassTexture.IsDisposed)
                return _surfaceClassTexture;
            int width = surf.Width, height = surf.Height;
            if (_surfaceClassTexture == null || _surfaceClassTexture.IsDisposed
                || _surfaceClassTexture.Width != width || _surfaceClassTexture.Height != height)
            {
                _surfaceClassTexture?.Dispose();
                _surfaceClassTexture = new Texture2D(_device, width, height, false, SurfaceFormat.Alpha8);
            }
            var texels = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    texels[y * width + x] = surf.GetSurface(x, y) switch
                    {
                        SurfaceClass.Ground => (byte)0,
                        SurfaceClass.Void => (byte)0,
                        SurfaceClass.Deck => (byte)64,
                        SurfaceClass.Water => (byte)128,
                        _ => (byte)255,
                    };
            _surfaceClassTexture.SetData(texels);
            _surfaceClassSource = surf;
            return _surfaceClassTexture;
        }

        private bool WaterWithinTiles(int tileX, int tileY, int radiusTiles)
        {
            // Wet puddles mirror entities anywhere on the map, so while they are live the
            // near-water cull must answer yes everywhere - it was written when water was the
            // only thing a reflection could land on.
            if (_wetPuddleMirrorWanted)
                return true;
            bool[]? flags = _waterTilesInMask;
            if (flags == null || _waterMask == null)
                return true;
            int tilesW = _waterMask.Width / 16, tilesH = _waterMask.Height / 16;
            if (tilesW <= 0 || tilesH <= 0 || flags.Length < tilesW * tilesH)
                return true;
            int cx = tileX - _lastWaterTileX, cy = tileY - _lastWaterTileY;
            int x0 = Math.Max(0, cx - radiusTiles), x1 = Math.Min(tilesW - 1, cx + radiusTiles);
            int y0 = Math.Max(0, cy - radiusTiles), y1 = Math.Min(tilesH - 1, cy + radiusTiles);
            if (x1 < x0 || y1 < y0)
                return false;

            int[]? sums = WaterTilePrefix(flags, tilesW, tilesH);
            if (sums != null)
            {
                int stride = tilesW + 1;
                int total = sums[(y1 + 1) * stride + (x1 + 1)] - sums[y0 * stride + (x1 + 1)]
                          - sums[(y1 + 1) * stride + x0] + sums[y0 * stride + x0];
                return total > 0;
            }

            for (int y = y0; y <= y1; y++)
            {
                int row = y * tilesW;
                for (int x = x0; x <= x1; x++)
                    if (flags[row + x])
                        return true;
            }
            return false;
        }

        private int[]? _waterTilePrefix;
        private int _waterTilePrefixVersion = -1;
        private bool[]? _waterTilePrefixSource;
        private int _waterTilePrefixWidth, _waterTilePrefixHeight;
        /// <summary>The water's bounding box within the window, in window tiles, built with the
        /// table below. x1 &lt; x0 means the window holds no water at all.</summary>
        private int _waterBoxX0, _waterBoxX1, _waterBoxY0, _waterBoxY1;

        /// <summary>
        /// Narrow a viewport-wide tile walk to the tiles that could possibly pass
        /// <see cref="WaterWithinTiles"/> with the given downward offset and radius.
        ///
        /// <para>
        /// Both the sprite mask and the entity mirror sweep every tile the camera can see looking
        /// for terrain features, and ask about water once per feature they find. On a 1280x720
        /// window that sweep is around nine hundred tile lookups a frame, on a map where the water
        /// may be a pond in one corner - and each lookup goes through the game's net-field
        /// dictionary, which is not a plain one. The per-feature question is now cheap; the sweep
        /// that leads to it was still paid in full.
        /// </para>
        ///
        /// <para>
        /// Water lives inside a box, so the answer is only ever yes inside that box grown by the
        /// radius (and shifted by the offset the caller looks down by). Outside it the old code
        /// walked, looked up, asked and was told no. This is the same set of tiles, reached without
        /// visiting the ones that were always going to fail.
        /// </para>
        /// </summary>
        /// <returns>False when nothing in the walk can qualify, so the caller can skip it whole.</returns>
        private bool ClampWalkToWater(int downOffsetTiles, int radiusTiles,
            ref int x0, ref int x1, ref int y0, ref int y1)
        {
            bool[]? flags = _waterTilesInMask;
            if (flags == null || _waterMask == null)
                return true;    // unknown answers yes, exactly as the per-tile test does
            int tilesW = _waterMask.Width / 16, tilesH = _waterMask.Height / 16;
            if (tilesW <= 0 || tilesH <= 0 || flags.Length < tilesW * tilesH)
                return true;
            if (WaterTilePrefix(flags, tilesW, tilesH) == null)
                return true;
            if (_waterBoxX1 < _waterBoxX0)
                return false;   // no water in the window: nothing in the walk can pass

            int bx0 = _waterBoxX0 + _lastWaterTileX, bx1 = _waterBoxX1 + _lastWaterTileX;
            int by0 = _waterBoxY0 + _lastWaterTileY, by1 = _waterBoxY1 + _lastWaterTileY;
            x0 = Math.Max(x0, bx0 - radiusTiles);
            x1 = Math.Min(x1, bx1 + radiusTiles);
            y0 = Math.Max(y0, by0 - radiusTiles - downOffsetTiles);
            y1 = Math.Min(y1, by1 + radiusTiles - downOffsetTiles);
            return x1 >= x0 && y1 >= y0;
        }

        /// <summary>The window's flags as a summed-area table, built on demand and reused until the
        /// flags change. Version-keyed rather than reference-keyed because the flag array is
        /// refilled in place, and split screen hands a different window back per screen - both of
        /// which a reference test would call unchanged.</summary>
        private int[]? WaterTilePrefix(bool[] flags, int tilesW, int tilesH)
        {
            if (_waterTilePrefix != null && _waterTilePrefixVersion == _waterTilesVersion
                && ReferenceEquals(_waterTilePrefixSource, flags)
                && _waterTilePrefixWidth == tilesW && _waterTilePrefixHeight == tilesH)
                return _waterTilePrefix;

            int stride = tilesW + 1, need = stride * (tilesH + 1);
            if (_waterTilePrefix == null || _waterTilePrefix.Length < need)
                _waterTilePrefix = new int[need];
            int[] sums = _waterTilePrefix;
            Array.Clear(sums, 0, need);
            // The bounding box comes free with the scan, and the tile walks that consume it would
            // otherwise need their own pass over the same flags to find it.
            _waterBoxX0 = tilesW; _waterBoxX1 = -1;
            _waterBoxY0 = tilesH; _waterBoxY1 = -1;
            for (int y = 0; y < tilesH; y++)
            {
                int src = y * tilesW, row = (y + 1) * stride, above = y * stride;
                int running = 0;
                for (int x = 0; x < tilesW; x++)
                {
                    if (flags[src + x])
                    {
                        running++;
                        if (x < _waterBoxX0) _waterBoxX0 = x;
                        if (x > _waterBoxX1) _waterBoxX1 = x;
                        if (y < _waterBoxY0) _waterBoxY0 = y;
                        if (y > _waterBoxY1) _waterBoxY1 = y;
                    }
                    sums[row + x + 1] = sums[above + x + 1] + running;
                }
            }
            _waterTilePrefixVersion = _waterTilesVersion;
            _waterTilePrefixSource = flags;
            _waterTilePrefixWidth = tilesW;
            _waterTilePrefixHeight = tilesH;
            return sums;
        }


        // ---- helpers -------------------------------------------------------

        // Wrapped like the cloud shadow's Time: unbounded seconds eventually push the
        // shader noise hashes past float/sin precision, which reads as hard axis-aligned
        // seams. 100-minute period, multiple of 60 so whole seconds stay whole.
        private static float Time() => (Determinism.Ticks % 360000) / 60f;

        /// <summary>Debug: save the water masks to PNG (R=effect, G=march, B=edge distance).</summary>
        public string DumpMasks(string dir)
        {
            var report = new System.Text.StringBuilder();
            // The flood occluder mask too: whether a fence landed in it as pickets or as a block is
            // a question a screenshot of the lit scene answers badly and this file answers at once.
            if (_floodOccluderMask != null)
            {
                string occluderPath = System.IO.Path.Combine(dir, "radiance-occluders.png");
                using (var fs = System.IO.File.Create(occluderPath))
                    _floodOccluderMask.SaveAsPng(fs, _floodOccluderMask.Width, _floodOccluderMask.Height);
                report.Append($"saved {occluderPath} ({_floodOccluderMask.Width}x{_floodOccluderMask.Height}, "
                            + $"first tile {_floodOccluderTileX},{_floodOccluderTileY}, {FloodOccSubdivision} texels per tile, alpha = occlusion); ");
                // The shadow march reads this mask at coarser mip levels for its penumbra, and
                // whether those levels hold anything is a question only the levels can answer: a
                // target whose chain was never filled samples as its base level, and the softness
                // dial does nothing at all. Level 1 is written beside the base with its mean alpha.
                if (_floodOccluderMask is RenderTarget2D mipped && mipped.LevelCount > 1)
                {
                    int width1 = Math.Max(1, mipped.Width / 2), height1 = Math.Max(1, mipped.Height / 2);
                    var level1 = new Color[width1 * height1];
                    mipped.GetData(1, null, level1, 0, level1.Length);
                    double alphaSum = 0;
                    foreach (Color texel in level1) alphaSum += texel.A;
                    string mipPath = System.IO.Path.Combine(dir, "radiance-occluders-mip1.png");
                    using (var mipTexture = new Texture2D(_device, width1, height1))
                    {
                        mipTexture.SetData(level1);
                        using var mipStream = System.IO.File.Create(mipPath);
                        mipTexture.SaveAsPng(mipStream, width1, height1);
                    }
                    report.Append($"mip levels {mipped.LevelCount}, level 1 mean alpha {alphaSum / level1.Length:0.0} saved {mipPath}; ");
                }
                else
                    report.Append("mask has no mip chain (the penumbra reads its own soft copies); ");
            }
            if (_waterMask == null)
                return report + "no water mask built (stand near water first)";
            string p1 = System.IO.Path.Combine(dir, "radiance-watermask.png");
            using (var fs = System.IO.File.Create(p1))
                _waterMask.SaveAsPng(fs, _waterMask.Width, _waterMask.Height);
            return report + $"saved {p1} (origin tile {_lastWaterTileX},{_lastWaterTileY}, player tile {Game1.player?.TilePoint})";
        }
    }
}
