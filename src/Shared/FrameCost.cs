using System;
using System.Diagnostics;

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
        }

        private const int PartCount = 10;
        private const int CounterCount = 6;
        private const int WindowFrames = 300;      // five seconds at 60 fps

        private static readonly string[] Names =
        {
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
        };

        private static readonly double[] _sum = new double[PartCount];
        private static readonly double[] _max = new double[PartCount];
        private static readonly double[] _windowSum = new double[PartCount];
        private static readonly double[] _windowMax = new double[PartCount];
        private static readonly double[] _running = new double[PartCount];   // lifetime, for nesting adjustments
        private static int _frames, _windowFrameCount;

        private static readonly string[] CounterNames =
        {
            "object sprite bakes",
            "character sprite bakes",
            "bake misses (wanted, absent)",
            "bake evictions (over cap)",
            "shadow sprites drawn",
            "vanilla-draw shim calls",
        };

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

        internal static long Begin() => Stopwatch.GetTimestamp();

        internal static void Count(Counter counter, int n = 1) => _countThisFrame[(int)counter] += n;

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
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency - subtractMilliseconds;
            if (ms < 0) ms = 0;
            int i = (int)part;
            _sum[i] += ms;
            _running[i] += ms;
            if (ms > _max[i]) _max[i] = ms;
            return ms;
        }

        /// <summary>Advance the rolling window. Called once per frame, from the first of our
        /// events that runs while the mod is switched on.</summary>
        internal static void NextFrame()
        {
            // Fold the frame that just ended into the window BEFORE the roll, so the per-frame
            // worst is a real frame's count rather than a running total that only ever grows.
            for (int i = 0; i < CounterCount; i++)
            {
                _countSum[i] += _countThisFrame[i];
                if (_countThisFrame[i] > _countMax[i]) _countMax[i] = _countThisFrame[i];
                _countThisFrame[i] = 0;
            }
            long now = Stopwatch.GetTimestamp();
            if (_lastFrameStamp != 0)
            {
                double frameMs = (now - _lastFrameStamp) * 1000.0 / Stopwatch.Frequency;
                // A frame straddling a load screen, an alt-tab or a menu is minutes long and would
                // drag the average somewhere no real frame ever went. Anything past a quarter of a
                // second is one of those, not a slow frame.
                if (frameMs < 250)
                {
                    _frameSum += frameMs;
                    if (frameMs > _frameMax) _frameMax = frameMs;
                }
            }
            _lastFrameStamp = now;
            if (++_frames < WindowFrames)
                return;
            Array.Copy(_sum, _windowSum, PartCount);
            Array.Copy(_max, _windowMax, PartCount);
            Array.Copy(_countSum, _countWindowSum, CounterCount);
            Array.Copy(_countMax, _countWindowMax, CounterCount);
            _frameWindowSum = _frameSum; _frameWindowMax = _frameMax;
            _frameSum = _frameMax = 0;
            _windowFrameCount = _frames;
            Array.Clear(_sum, 0, PartCount);
            Array.Clear(_max, 0, PartCount);
            Array.Clear(_countSum, 0, CounterCount);
            Array.Clear(_countMax, 0, CounterCount);
            _frames = 0;
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
            Array.Clear(_countSum, 0, CounterCount);
            Array.Clear(_countWindowSum, 0, CounterCount);
            Array.Clear(_countMax, 0, CounterCount);
            Array.Clear(_countWindowMax, 0, CounterCount);
            Array.Clear(_countThisFrame, 0, CounterCount);
            _frameSum = _frameMax = _frameWindowSum = _frameWindowMax = 0;
            _lastFrameStamp = 0;
            _frames = _windowFrameCount = 0;
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

            var text = new System.Text.StringBuilder();
            text.AppendLine($"CPU submission time per frame, averaged over the last {frames} frames"
                          + (complete ? "" : " (a partial window)") + ":");
            double total = 0;
            for (int i = 0; i < PartCount; i++)
            {
                double avg = sum[i] / frames;
                total += avg;
                // A part that never ran is worth a line saying so: "shadow draw 0.00" is the
                // fastest way to see that the setting is off, and half of what a report needs.
                text.AppendLine($"  {Names[i],-26} avg {avg,6:0.000} ms   worst {max[i],6:0.000} ms");
            }
            // The parts do not overlap: the chain subtracts the grid rebuilds that run inside it,
            // so the lines add up to the total instead of counting that time twice.
            text.AppendLine($"  {"TOTAL",-26} avg {total,6:0.000} ms   = {total / 16.67 * 100:0.0}% of a 60 fps frame");

            double frameAvg = (complete ? _frameWindowSum : _frameSum) / frames;
            double frameWorst = complete ? _frameWindowMax : _frameMax;
            if (frameAvg > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {"WHOLE FRAME (wall clock)",-26} avg {frameAvg,6:0.000} ms   worst {frameWorst,6:0.000} ms"
                              + $"   = {(frameAvg > 0 ? 1000.0 / frameAvg : 0),5:0.0} fps");
                text.AppendLine($"  {"...of which measured above",-26}     {(frameAvg > 0 ? total / frameAvg * 100 : 0),5:0.0}%");
                if (frameAvg < 17.2)
                    text.AppendLine("  The frame rate is at its cap here, so this scene has no problem to find.");
            }
            text.AppendLine();
            text.AppendLine("This is the time spent SUBMITTING work, not the time the GPU spends doing it, so");
            text.AppendLine("treat it as a floor. If these numbers are small and the game still runs slow, the");
            text.AppendLine("bound is fill rate: run the benchmark on the Performance tab, which measures that.");
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
            return text.ToString().TrimEnd();
        }
    }
}
