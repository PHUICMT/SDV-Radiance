using System;
using System.Diagnostics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// What this mod costs per frame, measured all the time rather than only behind a debug flag.
    ///
    /// <para>
    /// This exists because of a report we could not answer. A player on a laptop was losing frames,
    /// had already turned off five effects, and asked what was wrong. Nothing we ship could tell
    /// them, or us, where the milliseconds were going: every probe in this codebase was gated on
    /// DebugLogging, which nobody has on, and the benchmark measures only the full-screen chain
    /// (see RenderPipeline.Bench.cs) so the shadow pass and the per-frame bakes were invisible to
    /// it. The first question of every performance report is "is it even us", and we had no way to
    /// answer it from a file the reporter could send.
    /// </para>
    ///
    /// <para>
    /// Two timestamp reads per part per frame, some tens of nanoseconds each, against a 16.67 ms
    /// budget. That is small enough that always-on is worth more than the cost.
    /// </para>
    ///
    /// <para>
    /// What this measures is CPU submission: the time we spend telling the GPU what to draw. It is
    /// a floor, not the whole cost. Where the GPU's own fill rate is the bound, this reads low and
    /// the benchmark's slope is the honest number. Both are reported, and the report says which is
    /// which rather than leaving a reader to assume.
    /// </para>
    /// </summary>
    internal static class FrameCost
    {
        internal enum Part
        {
            ShadowPrepare = 0,   // ALL the shadow bakes: player silhouette + NPC/animal casters + objects
            ShadowDraw,          // every sprite shadow drawn into the world pass
            GridFlood,           // flood GI lightmap rebuild (tile crossings)
            GridFloodOccluders,  // flood per-light occluder grid
            GridLightOccluders,  // classic lighting occluder mask
            GridWaterMask,       // water mask gather + upload
            SpriteMask,          // water sprite mask bake
            EntityReflection,    // flipped entity layer
            SceneryReflection,   // sprite-free map render for the mirror
            Chain,               // the full-screen effect chain
            Particles,           // the particle pool: one step of the simulation plus both draws
            Precipitation,       // replacement rain/snow: per-screen step plus the weather-slot draw
            WetWorld,            // wetness state + the wet-ground pass
            ReliefNormals,       // sprite relief: replaying the recorded frame with normal maps
        }

        /// <summary>
        /// HOW MUCH WORK, alongside how long it took. A millisecond figure says a frame was
        /// expensive; it cannot say whether the sprite cache is holding or thrashing, and those
        /// need opposite fixes. A scene that bakes twice a frame is warm and the cost is elsewhere;
        /// the same scene baking a hundred times a frame is a cache too small for what is on
        /// screen, which is the shape of the one report that named a mod ("trees and bushes are
        /// unplayably slow with Simple Foliage, fine with the setting off"). The existing warning
        /// for that fires once per location and only past the cap, so a cache sitting just under
        /// it and re-baking all day looked identical to a healthy one.
        /// </summary>
        internal enum Counter
        {
            ObjectBakes = 0,     // object/tile-prop silhouettes baked — one render-target switch each
            CasterBakes,         // character/animal silhouettes baked
            BakeMisses,          // a draw wanted a bake that was not there (or had gone stale)
            BakeEvictions,       // entries thrown out to stay under the cap
            ShadowSprites,       // shadow sprites emitted into the world pass
            // Every SpriteBatch.Draw the vanilla-shadow transpilers route through our shims
            // (Object.draw / Tree.draw / Bush.draw / critters). The shims are a bool test and a
            // ReferenceEquals, nanoseconds each — but they were the last cost in the mod with no
            // needle on it, and "I turned everything off and it still lags" has exactly one
            // suspect left in our code. A count in the report turns that suspicion into
            // arithmetic: N calls times nanoseconds is a number, not a maybe.
            ShimDraws,
            // A sprite too big for the largest bake slot. It can never be baked, so it is not a
            // miss waiting to resolve - it is a sprite that will draw as bands for the rest of the
            // session. Forest read twenty "misses" a frame, dead flat, against a cache at 65 of 464
            // slots with no evictions: every reading said thrash and none of it was.
            BakeTooBig,
            // Every SpriteBatch.Draw the shadow pass itself issues. "Shadow sprites drawn" counts
            // casters; this counts CALLS, and the two differ by up to 9 taps times 6 strips times
            // the casts per character, because a character's soft edge is drawn live while an
            // object's is baked into its pixels. Nothing said how far apart the two numbers were,
            // and the plan to close that gap (bake the blur for characters as well) is worth
            // exactly the size of this number and nothing else.
            ShadowDrawCalls,
            // A farmer silhouette is the most expensive single bake in the mod (FarmerRenderer
            // composes body, clothes, hair and whatever an appearance mod patched in), and on a
            // split screen the same person is wanted by every screen at once.
            FarmerBakes,
            FarmerBakesShared,
            // Every run of one texture MonoGame's batcher hands the card as its own draw call,
            // over the whole frame, and the game's world step alone. A texture change between
            // two consecutive sprites ends a run, so a sprite that is handed a texture of its
            // own (the soft look bakes one target per sprite) is a draw call of its own, and
            // that cost lands inside the game's draw where none of our timers reach. The number
            // that says whether smooth art is paid for in draw calls or in pixels.
            SpriteBatchFlushes,
            WorldSpriteBatchFlushes,
            // MonoGame's own counters, read once a frame off GraphicsDevice.Metrics, which resets
            // itself every frame. Binding a render target whose usage is DiscardContents makes
            // ApplyRenderTargets clear it in full before anything is drawn, and that clear is
            // counted here alongside the ones the mod asks for, so this is the only place the
            // implicit ones are visible at all. A clear is also not just a fill: PlatformClear
            // saves the scissor, depth stencil and blend states, applies its own, clears, then
            // restores all three and dirties them for the next draw.
            RenderTargetClears,
            RenderTargetBinds,
            // Lamp casts drawn with the grounded look (radiance_shadowlook): the corners moved in
            // the batch. Zero means the look is off or the batch internals were not found.
            GroundedCasts,
        }

        private const int PartCount = 14;
        /// <summary>Sized by the enum, not written beside it: a hand-kept copy of a count drifted
        /// once already (GpuTimer's PartCount) and reported four passes as free for weeks.</summary>
        private static readonly int CounterCount = Enum.GetValues(typeof(Counter)).Length;
        private const int WindowFrames = 300;      // five seconds at 60 fps

        private static readonly string[] Names =
        [
            "shadow bakes (player+objects)",
            "shadow draw (all sprites)",
            "grid: flood lightmap",
            "grid: flood occluders",
            "grid: light occluders",
            "grid: water mask",
            "water sprite mask",
            "water entity mirror",
            "water scenery mirror",
            "effect chain",
            "particles",
            "precipitation (rain/snow)",
            "wet world (ground + screen drops)",
            "sprite relief normals",
        ];

        private static readonly double[] _sum = new double[PartCount];
        private static readonly double[] _max = new double[PartCount];
        private static readonly double[] _windowSum = new double[PartCount];
        private static readonly double[] _windowMax = new double[PartCount];
        private static readonly double[] _running = new double[PartCount];   // lifetime, for nesting adjustments
        /// <summary>What each part asked the collector for, in bytes, over the window. The whole
        /// frame's figure is every mod and the game together, and a stall with "GC gen2 +1" beside
        /// it could not say whose garbage it was collecting. A part nested in another is counted
        /// in both, as the chain's rows are.</summary>
        private static readonly long[] _allocatedAtBegin = new long[PartCount];
        private static readonly long[] _allocatedPartSum = new long[PartCount];
        private static readonly long[] _allocatedPartWindowSum = new long[PartCount];
        private static int _frames, _windowFrameCount;

        // ---- the longest frames, one by one ----
        // Every table above is an average and a worst COLUMN: the worst water gather and the worst
        // shadow bake are printed, but nothing says whether they happened in the same frame, or
        // what else that frame was doing, or whether the frame the player felt was one of ours at
        // all. A steady 60 is lost one frame at a time, so the hunt for it needs the frames
        // themselves: for each of the longest since the last report, how long it was, how much of
        // it this mod can account for, which parts, what the caches did, whether the collector
        // ran, and where the player was standing. A frame that is long and mostly NOT ours is as
        // much of an answer as one that is.
        private const int WorstFramesKept = 6;
        private sealed class LongFrame
        {
            public double FrameMs;
            public double OursMs;
            public readonly double[] Parts = new double[PartCount];
            public readonly int[] Counts = new int[8];
            public int Gen0, Gen1, Gen2;
            public long AllocatedBytes;
            public string Location = "";
            public int TimeOfDay;
            public int FramesSinceArrival;
            /// <summary>What the game was doing while the frame ran: loading, warping behind a
            /// fade, sitting in a menu, or playing. A frame of several seconds means one thing
            /// while a save loads and quite another while somebody is walking, and the ledger
            /// could not tell those apart. Written as a short word so the line stays readable.
            /// </summary>
            public string Doing = "";
        }
        private static readonly LongFrame[] _longest = new LongFrame[WorstFramesKept];
        private static int _longestCount;
        private static readonly double[] _thisFrame = new double[PartCount];
        private static readonly int[] _gcLastFrame = new int[3];
        private static string _lastFrameLocation = "";
        private static int _framesSinceArrival = int.MaxValue / 2;
        /// <summary>Frames after a location change that still count as arrival work.</summary>
        private const int ArrivalFrames = 180;

        // ---- garbage collections, per window ----
        // The worst column of every table in this report can be a collection pause wearing a
        // stage's name: whichever bracket is open when the runtime stops the world inherits the
        // wait. Nothing printed the collection counts, so a GC-shaped stutter and a genuinely
        // expensive rebuild were indistinguishable in every hunt to date. Whole-process numbers -
        // the game and every mod allocate into the same heap - which is exactly the number a
        // stutter hunt needs beside the worst frames it is trying to explain.
        private static readonly int[] _gcBase = new int[3];
        private static readonly int[] _gcWindow = new int[3];

        // ---- bytes allocated per frame ----
        // The collection counts say THAT the collector ran, not what fed it. Bytes allocated on the
        // game thread between two frames are the feed, read straight from the runtime. Game thread
        // only, so the game and every mod are in it; a jump that follows one of our toggles is ours.
        private static long _allocatedAtLastFrame;
        private static long _allocatedSum, _allocatedMax, _allocatedWindowSum, _allocatedWindowMax;
        private static int _allocatedFrames, _allocatedWindowFrames;
        private static bool _gcBaseTaken;

        private static readonly string[] CounterNames =
        [
            "object sprite bakes",
            "character sprite bakes",
            "bake misses (wanted, absent)",
            "bake evictions (over cap)",
            "shadow sprites drawn",
            "vanilla-draw shim calls",
            "too big to bake (draws banded)",
            "shadow draw calls (SpriteBatch)",
            "farmer silhouette bakes",
            "farmer bakes shared between screens",
            "sprite batch draw calls (whole frame)",
            "sprite batch draw calls (world step)",
            "render target clears (ours + implicit)",
            "render target binds",
            "grounded lamp casts",
        ];

        static FrameCost()
        {
            // Loud on the first frame rather than quietly printing the wrong name beside the
            // wrong number for the rest of the release.
            if (CounterNames.Length != CounterCount)
                throw new InvalidOperationException(
                    $"FrameCost has {CounterCount} counters and {CounterNames.Length} counter names; add the missing name.");
        }

        private static readonly long[] _countSum = new long[CounterCount];
        private static readonly long[] _countWindowSum = new long[CounterCount];
        private static readonly int[] _countMax = new int[CounterCount];
        private static readonly int[] _countWindowMax = new int[CounterCount];
        private static readonly int[] _countThisFrame = new int[CounterCount];

        /// <summary>Live cache occupancy, in the same report as the churn it explains: a high miss
        /// count against a cache sitting at its cap is thrash, the same misses against a cache with
        /// room to spare are a scene that keeps changing. Written once a frame, not accumulated.</summary>
        private static int _objectCacheSize, _objectCacheCap, _casterCacheSize, _casterCacheCap;

        /// <summary>
        /// Wall-clock time between frames, which is the only number here the player can feel.
        ///
        /// <para>
        /// Everything above measures the time we spend TELLING the graphics card what to draw.
        /// None of it sees the time the card spends drawing, and the shadow pass is exactly where
        /// that gap bites: nine blurred copies of five hundred sprites is a great deal of fill and
        /// almost no submission, so it can cost a third of a frame while every line above reads
        /// small. A report full of small numbers next to a player insisting the game is slow is
        /// how three sessions were spent looking in the wrong place.
        /// </para>
        ///
        /// <para>
        /// So: measure the frame itself, and print the two side by side. If the frame is 25 ms and
        /// we account for 1.5 of it, the mod is not what is eating the frame - unless the frames
        /// got longer when the mod was switched on, which is the comparison worth asking for.
        /// Capped at 60 fps this reads 16.7 ms and says nothing, which is itself the answer: a
        /// machine holding its cap has no problem to find.
        /// </para>
        /// </summary>
        private static long _lastFrameStamp;
        private static double _frameSum, _frameMax, _frameWindowSum, _frameWindowMax;

        /// <summary>
        /// Frames measured while the game window did not have focus.
        ///
        /// <para>
        /// MonoGame sleeps for <c>InactiveSleepTime</c>, twenty milliseconds by default, on every
        /// frame where the window is inactive. A measurement taken across those frames is a
        /// measurement of that sleep: the whole frame reads around 25 ms and the frame rate reads
        /// about 39, while every part of this mod reads exactly what it always does. That is
        /// indistinguishable from a real stall unless something counts it, and it cost one
        /// unexplained result already, on a run driven from a script with the window in the
        /// background. It matters for other people's reports too, since a player alt-tabbing to
        /// copy the file is exactly how the last frames before a report get taken.
        /// </para>
        /// </summary>
        private static int _unfocusedFrames, _unfocusedWindowFrames;

        /// <summary>
        /// Frames that ran longer than a quarter of a second.
        ///
        /// <para>
        /// These used to be dropped where they were measured, on the reasoning that a frame
        /// straddling a load screen is minutes long and would drag the average somewhere no real
        /// frame ever went. That reasoning is still right for the AVERAGE and wrong for everything
        /// else: a player reporting a stall is reporting exactly these frames, and a report that
        /// silently excludes them cannot show the thing it was asked about. trc666's split-screen
        /// report on 2026-09-07 is the case: the stall he described could not have appeared in it.
        /// They are counted here and kept out of the average, and they are still offered to the
        /// ledger of the longest frames, which is where the attribution lives.
        /// </para>
        /// </summary>
        private static int _hugeFrames;
        private static double _hugeFrameMax;
        private const double HugeFrameMilliseconds = 250;

        /// <summary>
        /// How many UPDATES the game ran for each frame it drew, and what this mod spent inside
        /// them.
        ///
        /// <para>
        /// This is the one number that speaks to an input complaint. MonoGame runs a fixed time
        /// step: <c>Game.Tick</c> holds an accumulator and runs
        /// <c>while (_accumulatedElapsedTime >= TargetElapsedTime) DoUpdate(...)</c>, capped at
        /// half a second of catching up, while the platform reads the keyboard and the pad ONCE
        /// per Tick, outside that loop. So a frame that takes 69 ms runs about four updates
        /// against a single reading of the controller: a press is only seen at a frame boundary,
        /// a release likewise, and the farmer commits to walking for the whole of it. That is
        /// exactly what "I press a direction, nothing happens, then it walks on by itself" is,
        /// and nothing in this mod measured it. A player reporting input lag can now say how many
        /// updates their frames are running, which turns the complaint into a number.
        /// </para>
        ///
        /// <para>
        /// Counted on screen 0 only. SMAPI raises the update event once per screen, and the
        /// question here is how often the GAME updated, not how many screens heard about it.
        /// </para>
        /// </summary>
        private static int _ticksThisFrame;
        private static double _tickSum, _tickMax, _tickWindowSum, _tickWindowMax;
        private static int _tickFrames, _tickWindowFrames;
        private static double _ourTickMs, _ourTickSum, _ourTickMax, _ourTickWindowSum, _ourTickWindowMax;
        private static long _lastTickStamp;
        private static double _tickGapMax, _tickGapWindowMax;

        /// <summary>Smoothed frame time for the on-screen readout. The window figures only move
        /// every 300 frames, which is five seconds of a number that is supposed to react.</summary>
        private static double _frameEmaMs;

        /// <summary>The same smoothing over focused frames only. Read by anything that STEERS on
        /// the frame time rather than reporting it - see the note where it is fed.</summary>
        private static double _focusedFrameEmaMs;

        /// <summary>GPU nanoseconds per part, collected three frames late by <see cref="GpuTimer"/>
        /// and folded in here so both columns share one window and one set of rules about which
        /// frames count.</summary>
        private static readonly double[] _gpuSum = new double[PartCount];
        private static readonly double[] _gpuMax = new double[PartCount];
        private static readonly int[] _gpuSamples = new int[PartCount];
        private static readonly double[] _gpuWindowSum = new double[PartCount];
        private static readonly double[] _gpuWindowMax = new double[PartCount];
        private static readonly int[] _gpuWindowSamples = new int[PartCount];

        /// <summary>Whether the game window has focus right now. Wrapped rather than inlined
        /// because it is read once a frame from a static and the game object is not always up.</summary>
        private static bool IsWindowFocused()
        {
            try { return StardewValley.Game1.game1?.IsActive ?? true; }
            catch { return true; }
        }

        // ---- what the on-screen readout reads. No allocation: it is drawn every frame ----

        /// <summary>Short names for the on-screen readout. The long ones are written for a file
        /// somebody reads once; on screen they collided with their own numbers, which is worse than
        /// being vague because a number you cannot read is not a measurement.</summary>
        private static readonly string[] ShortNames =
        [
            "shadow bakes",
            "shadow draw",
            "flood lightmap",
            "flood occluders",
            "light occluders",
            "water mask",
            "sprite mask",
            "entity mirror",
            "scenery mirror",
            "effect chain",
            "particles",
            "precipitation",
            "wet world",
            "relief normals",
        ];

        internal static int PartTotal => PartCount;
        internal static string PartName(int part) => Names[part];
        internal static string PartShortName(int part) => ShortNames[part];
        internal static double SmoothedFrameMs => _frameEmaMs;
        internal static double SmoothedFocusedFrameMs => _focusedFrameEmaMs;
        internal static int UnfocusedFramesInWindow => _windowFrameCount > 0 ? _unfocusedWindowFrames : _unfocusedFrames;
        internal static int FramesInWindow => _windowFrameCount > 0 ? _windowFrameCount : _frames;

        internal static double PartAverageMs(int part)
        {
            int frames = FramesInWindow;
            if (frames <= 0) return 0;
            return (_windowFrameCount > 0 ? _windowSum[part] : _sum[part]) / frames;
        }

        internal static bool TryPartGpuAverageMs(int part, out double milliseconds)
        {
            int samples = _windowFrameCount > 0 ? _gpuWindowSamples[part] : _gpuSamples[part];
            if (samples <= 0) { milliseconds = 0; return false; }
            milliseconds = (_windowFrameCount > 0 ? _gpuWindowSum[part] : _gpuSum[part]) / samples;
            return true;
        }

        internal static long Begin(Part part)
        {
            GpuTimer.MarkBegin((int)part);
            _allocatedAtBegin[(int)part] = GC.GetAllocatedBytesForCurrentThread();
            return Stopwatch.GetTimestamp();
        }

        internal static void Count(Counter counter, int n = 1) => _countThisFrame[(int)counter] += n;

        /// <summary>
        /// Record a counter that is already a running total for this frame, keeping the largest
        /// value seen. MonoGame's own metrics reset in <c>Present</c>, so they climb through the
        /// frame and every read is a partial sum; on a split screen this is read once per screen
        /// and adding those reads would count the earlier screens again.
        /// </summary>
        internal static void CountHighWater(Counter counter, int total)
        {
            if (total > _countThisFrame[(int)counter])
                _countThisFrame[(int)counter] = total;
        }

        /// <summary>Read the graphics device's own per-frame counters. Called from the last hook
        /// of the frame, because they are cleared in <c>Present</c> and count up until then.</summary>
        internal static void NoteDeviceMetrics(Microsoft.Xna.Framework.Graphics.GraphicsDevice device)
        {
            var metrics = device.Metrics;
            CountHighWater(Counter.RenderTargetClears, (int)metrics.ClearCount);
            CountHighWater(Counter.RenderTargetBinds, (int)metrics.TargetCount);
        }

        internal static void CacheOccupancy(int objects, int objectCap, int casters, int casterCap)
        {
            _objectCacheSize = objects; _objectCacheCap = objectCap;
            _casterCacheSize = casters; _casterCacheCap = casterCap;
        }

        /// <summary>Lifetime total for a part, so a caller that ENCLOSES another part can subtract
        /// it and keep the lines addable. Only the grid rebuilds nest, inside the chain.</summary>
        internal static double Running(Part part) => _running[(int)part];

        /// <summary>All four grid parts together, for the chain's nesting subtraction.</summary>
        internal static double RunningGrids()
            => _running[(int)Part.GridFlood] + _running[(int)Part.GridFloodOccluders]
             + _running[(int)Part.GridLightOccluders] + _running[(int)Part.GridWaterMask];

        /// <summary>Close a measurement and return its milliseconds, so a caller that also keeps
        /// its own debug totals does not have to time the same call twice.</summary>
        internal static double End(Part part, long started, double subtractMilliseconds = 0)
        {
            GpuTimer.MarkEnd((int)part);
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency - subtractMilliseconds;
            if (ms < 0) ms = 0;
            int i = (int)part;
            _allocatedPartSum[i] += GC.GetAllocatedBytesForCurrentThread() - _allocatedAtBegin[i];
            _sum[i] += ms;
            _running[i] += ms;
            _thisFrame[i] += ms;
            if (ms > _max[i]) _max[i] = ms;
            return ms;
        }

        /// <summary>One update of the game happened, and this is what this mod spent in it.
        /// Called from the update event on screen 0 only, so what is counted is the game's
        /// updates rather than the screens that heard about them.</summary>
        internal static void NoteUpdateTick(double ourMilliseconds)
        {
            _ticksThisFrame++;
            _ourTickMs += ourMilliseconds;
            long now = Stopwatch.GetTimestamp();
            if (_lastTickStamp != 0)
            {
                double gap = (now - _lastTickStamp) * 1000.0 / Stopwatch.Frequency;
                // The same quarter-second line the frame meter draws: past that it is a load, not
                // a tick that ran late.
                if (gap < HugeFrameMilliseconds && gap > _tickGapMax)
                    _tickGapMax = gap;
            }
            _lastTickStamp = now;
        }

        /// <summary>Offer the frame that just ended to the ledger of the longest. The chain
        /// encloses the grid rebuilds, so they are taken off it here the way the table does.</summary>
        private static void OfferLongFrame(double frameMs, int[] countsThisFrame, long allocatedBytes)
        {
            double ours = 0;
            double grids = 0;
            for (int i = (int)Part.GridFlood; i <= (int)Part.GridWaterMask; i++) grids += _thisFrame[i];
            _thisFrame[(int)Part.Chain] = Math.Max(0, _thisFrame[(int)Part.Chain] - grids);
            for (int i = 0; i < PartCount; i++) ours += _thisFrame[i];
            string location = Game1.currentLocation?.NameOrUniqueName ?? "";
            if (location != _lastFrameLocation)
                _framesSinceArrival = 0;
            else if (_framesSinceArrival < int.MaxValue / 2)
                _framesSinceArrival++;
            _lastFrameLocation = location;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            int d0 = gen0 - _gcLastFrame[0], d1 = gen1 - _gcLastFrame[1], d2 = gen2 - _gcLastFrame[2];
            _gcLastFrame[0] = gen0; _gcLastFrame[1] = gen1; _gcLastFrame[2] = gen2;

            int slot;
            if (_longestCount < WorstFramesKept)
                slot = _longestCount++;
            else
            {
                slot = 0;
                for (int k = 1; k < WorstFramesKept; k++)
                    if (_longest[k].FrameMs < _longest[slot].FrameMs) slot = k;
                if (_longest[slot].FrameMs >= frameMs)
                    return;
            }
            LongFrame f = _longest[slot] ??= new LongFrame();
            f.FrameMs = frameMs;
            f.OursMs = ours;
            Array.Copy(_thisFrame, f.Parts, PartCount);
            Array.Copy(countsThisFrame, f.Counts, Math.Min(countsThisFrame.Length, f.Counts.Length));
            f.Gen0 = d0; f.Gen1 = d1; f.Gen2 = d2;
            f.AllocatedBytes = allocatedBytes;
            f.Location = location;
            f.TimeOfDay = Game1.timeOfDay;
            f.FramesSinceArrival = _framesSinceArrival;
            f.Doing = WhatTheGameWasDoing();
        }

        /// <summary>One word for the state the game was in when a frame ran long. The reason this
        /// exists: the ledger's first multi-second frames on a split screen here all landed while
        /// the second screen was being created or a warp was under a fade, and a line saying only
        /// "Farm 12:00" cannot say so. A stall that a player feels happens while "playing", and
        /// that is the only one worth hunting.</summary>
        private static string WhatTheGameWasDoing()
        {
            // gameMode 6 is loading a save, 0 and 3 are the title and its transitions.
            if (Game1.gameMode != Game1.playingGameMode)
                return "loading";
            if (!Context.IsWorldReady)
                return "not in world";
            if (Game1.isWarping || Game1.globalFade || Game1.fadeToBlack)
                return "warping";
            if (Game1.eventUp || Game1.currentMinigame != null)
                return "cutscene";
            if (Game1.activeClickableMenu != null)
                return "menu";
            return "playing";
        }

        /// <summary>The longest frames since the last report, longest first, each with what this
        /// mod did in it. Printed by radiance_report, and reset by printing.</summary>
        internal static string DescribeLongestFrames()
        {
            var text = new System.Text.StringBuilder();
            if (_longestCount == 0)
            {
                text.AppendLine("the longest frames since the last report: none measured yet");
                return text.ToString();
            }
            text.AppendLine($"the {_longestCount} longest frames since the last report, and what this mod did in each");
            text.AppendLine("(a 60 fps frame is 16.667 ms; 'not ours' is the game, other mods, the driver and the collector;");
            text.AppendLine(" 'arrival+N' is N frames after entering the location, where arrival work is expected):");
            var order = new int[_longestCount];
            for (int k = 0; k < _longestCount; k++) order[k] = k;
            Array.Sort(order, (a, b) => _longest[b].FrameMs.CompareTo(_longest[a].FrameMs));
            foreach (int k in order)
            {
                LongFrame f = _longest[k];
                // the three biggest parts of ours, by name
                var partOrder = new int[PartCount];
                for (int i = 0; i < PartCount; i++) partOrder[i] = i;
                Array.Sort(partOrder, (a, b) => f.Parts[b].CompareTo(f.Parts[a]));
                var parts = new System.Text.StringBuilder();
                for (int n = 0; n < 3; n++)
                {
                    int i = partOrder[n];
                    if (f.Parts[i] < 0.05) break;
                    parts.Append(n == 0 ? "" : ", ").Append(Names[i]).Append(' ').Append(f.Parts[i].ToString("0.00"));
                }
                string counts = "";
                if (f.Counts[(int)Counter.ObjectBakes] + f.Counts[(int)Counter.CasterBakes] > 0)
                    counts += $"  bakes {f.Counts[(int)Counter.ObjectBakes]}+{f.Counts[(int)Counter.CasterBakes]}";
                if (f.Counts[(int)Counter.BakeMisses] > 0) counts += $"  misses {f.Counts[(int)Counter.BakeMisses]}";
                if (f.Counts[(int)Counter.BakeEvictions] > 0) counts += $"  evictions {f.Counts[(int)Counter.BakeEvictions]}";
                string gc = f.Gen0 + f.Gen1 + f.Gen2 > 0 ? $"  GC gen0 +{f.Gen0} gen1 +{f.Gen1} gen2 +{f.Gen2}" : "";
                string where = $"{f.Location} {f.TimeOfDay / 100}:{f.TimeOfDay % 100:00}"
                             + (f.FramesSinceArrival < ArrivalFrames ? $" (arrival+{f.FramesSinceArrival})" : "")
                             + (f.Doing.Length > 0 && f.Doing != "playing" ? $" [{f.Doing}]" : "");
                text.AppendLine($"  {f.FrameMs,7:0.00} ms  {where,-32}  ours {f.OursMs,6:0.00}  not ours {Math.Max(0, f.FrameMs - f.OursMs),6:0.00}"
                              + (parts.Length > 0 ? $"   [{parts}]" : "") + counts + gc
                              + (f.AllocatedBytes >= 1024 ? $"  alloc {f.AllocatedBytes / 1024} KB" : ""));
            }
            _longestCount = 0;
            _hugeFrames = 0;
            _hugeFrameMax = 0;
            return text.ToString();
        }

        /// <summary>Advance the rolling window. Called once per frame, from the first of our
        /// events that runs while the mod is switched on.</summary>
        /// <param name="wallClockFrame">True on the screen whose turn ends a frame the player
        /// actually sees. In split screen this method is called once per SCREEN, so the wall clock
        /// between two calls is half a frame: the report was printing 65 fps for a game running at
        /// 32, and every "of which measured above" percentage was against half a frame. The parts
        /// still fold on every call, so their averages are what this mod costs per frame the
        /// player sees, both screens together; only the clock waits for the first screen.</param>
        internal static void NextFrame(bool wallClockFrame = true)
        {
            // Fold the frame that just ended into the window BEFORE the roll, so the per-frame
            // worst is a real frame's count rather than a running total that only ever grows.
            // The counts are kept aside first: the ledger of the longest frames wants them beside
            // the frame's wall-clock time, which is only known further down.
            var countsOfLastFrame = new int[CounterCount];
            Array.Copy(_countThisFrame, countsOfLastFrame, CounterCount);
            for (int i = 0; i < CounterCount; i++)
            {
                _countSum[i] += _countThisFrame[i];
                if (_countThisFrame[i] > _countMax[i]) _countMax[i] = _countThisFrame[i];
                _countThisFrame[i] = 0;
            }
            // Collect the GPU marks from three frames back, then fold them in. The chain encloses
            // the grid rebuilds on the card exactly as it does on the processor, so the same
            // subtraction is applied here and the two columns stay addable in the same way.
            GpuTimer.NextFrame();
            double nestedGpu = 0;
            for (int i = (int)Part.GridFlood; i <= (int)Part.GridWaterMask; i++)
            {
                if (GpuTimer.TryTakeLastFrame(i, out double nested))
                    nestedGpu += nested;
            }
            for (int i = 0; i < PartCount; i++)
            {
                if (!GpuTimer.TryTakeLastFrame(i, out double gpuMs))
                    continue;
                if (i == (int)Part.Chain)
                    gpuMs = Math.Max(0, gpuMs - nestedGpu);
                _gpuSum[i] += gpuMs;
                _gpuSamples[i]++;
                if (gpuMs > _gpuMax[i]) _gpuMax[i] = gpuMs;
            }

            long now = Stopwatch.GetTimestamp();
            long allocatedNow = GC.GetAllocatedBytesForCurrentThread();
            if (_lastFrameStamp != 0 && wallClockFrame)
            {
                double frameMs = (now - _lastFrameStamp) * 1000.0 / Stopwatch.Frequency;
                long allocatedThisFrame = allocatedNow - _allocatedAtLastFrame;
                // A frame straddling a load screen, an alt-tab or a menu is minutes long and would
                // drag the average somewhere no real frame ever went. Anything past a quarter of a
                // second is one of those, or it is the stall the player is writing to us about, so
                // it stays out of the average and is COUNTED rather than dropped.
                bool focused = IsWindowFocused();
                if (frameMs >= HugeFrameMilliseconds && focused)
                {
                    _hugeFrames++;
                    if (frameMs > _hugeFrameMax) _hugeFrameMax = frameMs;
                    // Still offered to the ledger: the whole point of a frame this long is to see
                    // what was in it.
                    OfferLongFrame(frameMs, countsOfLastFrame, allocatedThisFrame);
                }
                if (frameMs < HugeFrameMilliseconds)
                {
                    if (focused)
                        OfferLongFrame(frameMs, countsOfLastFrame, allocatedThisFrame);
                    _allocatedSum += allocatedThisFrame;
                    if (allocatedThisFrame > _allocatedMax) _allocatedMax = allocatedThisFrame;
                    _allocatedFrames++;
                    _frameSum += frameMs;
                    if (frameMs > _frameMax) _frameMax = frameMs;
                    _frameEmaMs = _frameEmaMs <= 0 ? frameMs : _frameEmaMs * 0.9 + frameMs * 0.1;
                    // The same smoothing over FOCUSED frames only, for anything that steers on it
                    // rather than reports it. MonoGame sleeps 20 ms a frame while the window is
                    // inactive, which reads as 40 fps with nothing wrong; a controller watching the
                    // headline number would take an alt-tab as evidence the machine cannot cope and
                    // ratchet the quality down for a player who was not even looking.
                    if (focused)
                        _focusedFrameEmaMs = _focusedFrameEmaMs <= 0
                            ? frameMs : _focusedFrameEmaMs * 0.9 + frameMs * 0.1;
                }
                // Counted, never discarded. Throwing the frame away would leave an average that
                // silently described a different set of frames than the one it claims to.
                if (!focused)
                    _unfocusedFrames++;
            }
            if (wallClockFrame)
            {
                _lastFrameStamp = now;
                _allocatedAtLastFrame = allocatedNow;
                // Fold the updates that ran for the frame just drawn. A frame that drew without a
                // single update in it is counted too: that is the accumulator not yet full, and
                // dropping it would flatter the average.
                _tickSum += _ticksThisFrame;
                if (_ticksThisFrame > _tickMax) _tickMax = _ticksThisFrame;
                _ourTickSum += _ourTickMs;
                if (_ourTickMs > _ourTickMax) _ourTickMax = _ourTickMs;
                _tickFrames++;
                _ticksThisFrame = 0;
                _ourTickMs = 0;
            }
            Array.Clear(_thisFrame, 0, PartCount);
            if (!_gcBaseTaken)
            {
                for (int g = 0; g < 3; g++) _gcBase[g] = GC.CollectionCount(g);
                _gcBaseTaken = true;
            }
            // The window is counted in frames the player saw, so five seconds is five seconds
            // whether one screen is drawing or two.
            if (!wallClockFrame || ++_frames < WindowFrames)
                return;
            for (int g = 0; g < 3; g++)
            {
                int nowCount = GC.CollectionCount(g);
                _gcWindow[g] = nowCount - _gcBase[g];
                _gcBase[g] = nowCount;
            }
            Array.Copy(_sum, _windowSum, PartCount);
            Array.Copy(_max, _windowMax, PartCount);
            Array.Copy(_allocatedPartSum, _allocatedPartWindowSum, PartCount);
            Array.Clear(_allocatedPartSum, 0, PartCount);
            Array.Copy(_countSum, _countWindowSum, CounterCount);
            Array.Copy(_countMax, _countWindowMax, CounterCount);
            Array.Copy(_gpuSum, _gpuWindowSum, PartCount);
            Array.Copy(_gpuMax, _gpuWindowMax, PartCount);
            Array.Copy(_gpuSamples, _gpuWindowSamples, PartCount);
            _frameWindowSum = _frameSum; _frameWindowMax = _frameMax;
            _allocatedWindowSum = _allocatedSum; _allocatedWindowMax = _allocatedMax;
            _allocatedWindowFrames = _allocatedFrames;
            _allocatedSum = _allocatedMax = 0;
            _allocatedFrames = 0;
            _frameSum = _frameMax = 0;
            _unfocusedWindowFrames = _unfocusedFrames;
            _unfocusedFrames = 0;
            _tickWindowSum = _tickSum; _tickWindowMax = _tickMax; _tickWindowFrames = _tickFrames;
            _ourTickWindowSum = _ourTickSum; _ourTickWindowMax = _ourTickMax;
            _tickGapWindowMax = _tickGapMax;
            _tickSum = _tickMax = _ourTickSum = _ourTickMax = _tickGapMax = 0;
            _tickFrames = 0;
            _windowFrameCount = _frames;
            Array.Clear(_sum, 0, PartCount);
            Array.Clear(_max, 0, PartCount);
            Array.Clear(_countSum, 0, CounterCount);
            Array.Clear(_countMax, 0, CounterCount);
            Array.Clear(_gpuSum, 0, PartCount);
            Array.Clear(_gpuMax, 0, PartCount);
            Array.Clear(_gpuSamples, 0, PartCount);
            _frames = 0;
        }

        /// <summary>Drop every GPU figure, live and windowed, so the report stops showing a column
        /// that is no longer being measured. Called when GPU timing is switched off; the CPU side is
        /// deliberately left alone, since nothing about it changed.</summary>
        internal static void ForgetGpu()
        {
            Array.Clear(_gpuSum, 0, PartCount);
            Array.Clear(_gpuMax, 0, PartCount);
            Array.Clear(_gpuSamples, 0, PartCount);
            Array.Clear(_gpuWindowSum, 0, PartCount);
            Array.Clear(_gpuWindowMax, 0, PartCount);
            Array.Clear(_gpuWindowSamples, 0, PartCount);
        }

        /// <summary>Discard everything measured so far. Used when a measurement would be a lie
        /// about normal play, such as the benchmark's amplified frames.</summary>
        internal static void Reset()
        {
            Array.Clear(_sum, 0, PartCount);
            Array.Clear(_max, 0, PartCount);
            Array.Clear(_windowSum, 0, PartCount);
            Array.Clear(_windowMax, 0, PartCount);
            Array.Clear(_running, 0, PartCount);
            Array.Clear(_allocatedPartSum, 0, PartCount);
            Array.Clear(_allocatedPartWindowSum, 0, PartCount);
            Array.Clear(_countSum, 0, CounterCount);
            Array.Clear(_countWindowSum, 0, CounterCount);
            Array.Clear(_countMax, 0, CounterCount);
            Array.Clear(_countWindowMax, 0, CounterCount);
            Array.Clear(_countThisFrame, 0, CounterCount);
            Array.Clear(_gpuSum, 0, PartCount);
            Array.Clear(_gpuMax, 0, PartCount);
            Array.Clear(_gpuSamples, 0, PartCount);
            Array.Clear(_gpuWindowSum, 0, PartCount);
            Array.Clear(_gpuWindowMax, 0, PartCount);
            Array.Clear(_gpuWindowSamples, 0, PartCount);
            _frameSum = _frameMax = _frameWindowSum = _frameWindowMax = 0;
            _allocatedSum = _allocatedMax = _allocatedWindowSum = _allocatedWindowMax = 0;
            _allocatedFrames = _allocatedWindowFrames = 0;
            _lastFrameStamp = 0;
            _frames = _windowFrameCount = 0;
            Array.Clear(_thisFrame, 0, PartCount);
            _longestCount = 0;
        }

        internal static string Describe()
        {
            // Prefer the last COMPLETE window: a window still filling divides by too few frames
            // for the first seconds of a visit, which is exactly when someone types the command.
            bool complete = _windowFrameCount > 0;
            double[] sum = complete ? _windowSum : _sum;
            double[] max = complete ? _windowMax : _max;
            int frames = complete ? _windowFrameCount : _frames;
            if (frames <= 0)
                return "no frames measured yet. Load a save, play for a few seconds and run this again.";

            double[] gpuSum = complete ? _gpuWindowSum : _gpuSum;
            double[] gpuMax = complete ? _gpuWindowMax : _gpuMax;
            int[] gpuSamples = complete ? _gpuWindowSamples : _gpuSamples;
            bool anyGpu = false;
            for (int i = 0; i < PartCount && !anyGpu; i++) anyGpu = gpuSamples[i] > 0;

            var text = new System.Text.StringBuilder();
            // The focus warning is repeated at the TOP when it applies to a large share of the
            // window. It was printed only beside the whole-frame figure, most of a screen further
            // down, and a player who ran the command from the SMAPI console - which is to say with
            // the game window behind it - had two of their three reports measured that way before
            // the line was noticed. A number that is wrong is worth saying before the numbers, not
            // after them. Below a fifth of the window it stays where it was: a handful of frames
            // lost to an alt-tab does not move an average and a shouted warning about it would
            // teach people to skip the shouting.
            int unfocusedNow = complete ? _unfocusedWindowFrames : _unfocusedFrames;
            if (unfocusedNow * 5 > frames)
            {
                text.AppendLine($"!! {unfocusedNow} of these {frames} frames were drawn with the game window OUT OF FOCUS,");
                text.AppendLine("!! where the game sleeps 20 ms a frame. The WHOLE FRAME figure below is mostly that");
                text.AppendLine("!! sleep and is not worth reading. The per-part numbers are unaffected. Click the");
                text.AppendLine("!! game window, play for ten seconds and run this again before sharing it.");
                text.AppendLine();
            }
            text.AppendLine($"CPU submission time per frame, averaged over the last {frames} frames"
                          + (complete ? "" : " (a partial window)") + ":");
            double total = 0, gpuTotal = 0;
            for (int i = 0; i < PartCount; i++)
            {
                double avg = sum[i] / frames;
                total += avg;
                // A part that never ran is worth a line saying so: "shadow draw 0.00" is the
                // fastest way to see that the setting is off, and half of what a report needs.
                string line = $"  {Names[i],-26} avg {avg,6:0.000} ms   worst {max[i],6:0.000} ms";
                if (anyGpu)
                {
                    // Averaged over the frames whose result actually came back, not over every
                    // frame: a result still in flight is dropped rather than waited for, so dividing
                    // by the window would quietly scale every GPU figure down by the drop rate.
                    if (gpuSamples[i] > 0)
                    {
                        double gpuAvg = gpuSum[i] / gpuSamples[i];
                        gpuTotal += gpuAvg;
                        line += $"   | GPU avg {gpuAvg,6:0.000} ms   worst {gpuMax[i],6:0.000} ms";
                    }
                    else
                    {
                        line += "   | GPU        -";
                    }
                }
                text.AppendLine(line);
            }
            // The parts do not overlap: the chain subtracts the grid rebuilds that run inside it,
            // so the lines add up to the total instead of counting that time twice.
            text.AppendLine($"  {"TOTAL",-26} avg {total,6:0.000} ms   = {total / 16.67 * 100:0.0}% of a 60 fps frame"
                          + (anyGpu ? $"   | GPU avg {gpuTotal,6:0.000} ms" : ""));
            long[] allocatedByPart = complete ? _allocatedPartWindowSum : _allocatedPartSum;
            var asked = new System.Text.StringBuilder();
            for (int i = 0; i < PartCount; i++)
            {
                double kilobytes = allocatedByPart[i] / 1024.0 / frames;
                if (kilobytes >= 0.05)
                    asked.Append(asked.Length == 0 ? "" : " · ").Append($"{Names[i]} {kilobytes:0.0}");
            }
            text.AppendLine($"  memory asked of the collector per frame, KB (the effect chain's includes its grid rows): "
                          + (asked.Length == 0 ? "none" : asked.ToString()));

            double frameAvg = (complete ? _frameWindowSum : _frameSum) / frames;
            double frameWorst = complete ? _frameWindowMax : _frameMax;
            if (frameAvg > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {"WHOLE FRAME (wall clock)",-26} avg {frameAvg,6:0.000} ms   worst {frameWorst,6:0.000} ms"
                              + $"   = {(frameAvg > 0 ? 1000.0 / frameAvg : 0),5:0.0} fps");
                text.AppendLine($"  {"...of which measured above",-26}     {(frameAvg > 0 ? total / frameAvg * 100 : 0),5:0.0}%");
                // Counted since the last report, which is the ledger's own cadence: a stall
                // that happened four windows ago is still the thing being asked about, and a
                // count that rolled away while its frame sat in the ledger below said nothing.
                // How many updates the game ran per frame it drew, which is what an input
                // complaint is really about. See the field's own note for why.
                double tickFramesNow = complete ? _tickWindowFrames : _tickFrames;
                if (tickFramesNow > 0)
                {
                    double tickSumNow = complete ? _tickWindowSum : _tickSum;
                    double tickMaxNow = complete ? _tickWindowMax : _tickMax;
                    double ourTickSumNow = complete ? _ourTickWindowSum : _ourTickSum;
                    double ourTickMaxNow = complete ? _ourTickWindowMax : _ourTickMax;
                    double gapMaxNow = complete ? _tickGapWindowMax : _tickGapMax;
                    text.AppendLine($"  {"updates per frame drawn",-26} avg {tickSumNow / tickFramesNow,6:0.00}"
                                  + $"        worst {tickMaxNow,6:0}"
                                  + "   (above 1 means the game caught up: the pad is read once per frame,");
                    text.AppendLine($"  {"",-26}                            so every extra update replays that one reading)");
                    text.AppendLine($"  {"...of which ours",-26} avg {ourTickSumNow / tickFramesNow,6:0.000} ms"
                                  + $"   worst {ourTickMaxNow,6:0.000} ms   longest gap between updates {gapMaxNow,6:0.0} ms");
                }
                int hugeNow = _hugeFrames;
                double hugeMaxNow = _hugeFrameMax;
                if (hugeNow > 0)
                {
                    // Above the average rather than inside it, because that is what they are:
                    // frames the average was told to ignore. A stall is felt as these.
                    text.AppendLine($"  {"frames over 250 ms",-26}     {hugeNow,5} since the last report"
                                  + $"   worst {hugeMaxNow,7:0.0} ms   (left out of the average above;"
                                  + " each one is in the ledger below)");
                }
                if (frameAvg < 17.2)
                    text.AppendLine("  The frame rate is at its cap here, so this scene has no problem to find.");
                if (complete)
                {
                    // Whole process, not just this mod: everything allocates into one heap and a
                    // collection pauses everyone. A worst frame beside a nonzero gen1 or gen2
                    // count here may be the collector wearing a stage's name.
                    text.AppendLine($"  {"GC collections this window",-26} gen0 {_gcWindow[0]}   gen1 {_gcWindow[1]}   gen2 {_gcWindow[2]}"
                                  + "   (whole process, game and every mod)");
                    if (_allocatedWindowFrames > 0)
                        text.AppendLine($"  {"allocated per frame",-26} avg {_allocatedWindowSum / (double)_allocatedWindowFrames / 1024.0,8:0.0} KB"
                                      + $"   worst {_allocatedWindowMax / 1024.0,8:0.0} KB   (game thread: the game and every mod)");
                }
                int unfocused = complete ? _unfocusedWindowFrames : _unfocusedFrames;
                if (unfocused > 0)
                {
                    text.AppendLine();
                    text.AppendLine($"  READ THE FRAME TIME WITH CARE: {unfocused} of these {frames} frames were drawn");
                    text.AppendLine("  while the game window did not have focus. The game sleeps 20 ms on every one of");
                    text.AppendLine("  those, so the whole-frame figure above is partly that sleep and not work anyone");
                    text.AppendLine("  did. The per-part numbers are unaffected. Click the window and measure again.");
                }
            }
            text.AppendLine();
            if (anyGpu)
            {
                text.AppendLine("The first column is the time spent SUBMITTING work; the GPU column is the time the");
                text.AppendLine("card spent doing it, read back three frames late so asking never stalls anything.");
                text.AppendLine("Where they disagree, the larger one is the cost: a part can be almost free to submit");
                text.AppendLine("and expensive to draw, which is exactly how object shadows once read as a rounding");
                text.AppendLine("error while costing 1.80 ms. A GPU line of '-' means no result came back for that");
                text.AppendLine("part in this window, usually because the part did not run.");
                text.AppendLine("The GPU figures are driver timestamps, and a driver may resolve them at the edge of");
                text.AppendLine("a command batch rather than at the mark: a short pass can then be handed the cost of");
                text.AppendLine("the pass before it (seen on AMD OpenGL, a wet-world row reading the water pass's");
                text.AppendLine("figure with the wet ground switched off). Read the CPU column and the totals first,");
                text.AppendLine("and treat any single GPU row that matches its neighbour to the digit as suspect.");
            }
            else
            {
                text.AppendLine("This is the time spent SUBMITTING work, not the time the GPU spends doing it, so");
                text.AppendLine("treat it as a floor. If these numbers are small and the game still runs slow, the");
                text.AppendLine("bound is fill rate: run the benchmark on the Performance tab, which measures that.");
                text.AppendLine($"GPU timing is {GpuTimer.Status}. Run radiance_gputime on to measure the card itself,");
                text.AppendLine("then play for five seconds and run this again.");
            }
            text.AppendLine("Shadow draw is the part that grows with how much scenery is on screen, so a heavily");
            text.AppendLine("modded map is where it shows. Turning off shadows for objects is the setting for it.");

            long[] counts = complete ? _countWindowSum : _countSum;
            int[] countMax = complete ? _countWindowMax : _countMax;
            text.AppendLine();
            text.AppendLine("Work done per frame, over the same window:");
            for (int i = 0; i < CounterCount; i++)
                text.AppendLine($"  {CounterNames[i],-30} avg {counts[i] / (double)frames,7:0.0}   worst {countMax[i],5}");
            text.AppendLine($"  {"object bake cache",-30}     {_objectCacheSize,5} of {_objectCacheCap} slots");
            text.AppendLine($"  {"character bake cache",-30}     {_casterCacheSize,5} of {_casterCacheCap} slots");
            text.AppendLine();
            text.AppendLine("A bake is a render-target switch, which is expensive whatever the graphics card.");
            text.AppendLine("A warm scene bakes a handful per frame. Steady double or triple digits, with the");
            text.AppendLine("cache pinned at its cap and evictions running, means more distinct sprites are on");
            text.AppendLine("screen than the cache holds and every one of them is re-baked as it scrolls. That");
            text.AppendLine("is what a foliage or map pack with hundreds of variants does, and the setting that");
            text.AppendLine("stops it is shadows for objects.");
            text.AppendLine("'Too big to bake' is a different thing and does not resolve: those sprites are");
            text.AppendLine("larger than the biggest slot, so they draw as a banded gradient rather than a");
            text.AppendLine("silhouette, and they will keep doing so. A steady count there is art, usually from");
            text.AppendLine("a map or foliage pack, drawn at a size the bake slots were not built for.");
            return text.ToString().TrimEnd();
        }
    }
}
