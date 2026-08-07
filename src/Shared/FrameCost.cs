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

        private const int PartCount = 10;
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

        internal static long Begin() => Stopwatch.GetTimestamp();

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
            if (++_frames < WindowFrames)
                return;
            Array.Copy(_sum, _windowSum, PartCount);
            Array.Copy(_max, _windowMax, PartCount);
            _windowFrameCount = _frames;
            Array.Clear(_sum, 0, PartCount);
            Array.Clear(_max, 0, PartCount);
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
            text.AppendLine();
            text.AppendLine("This is the time spent SUBMITTING work, not the time the GPU spends doing it, so");
            text.AppendLine("treat it as a floor. If these numbers are small and the game still runs slow, the");
            text.AppendLine("bound is fill rate: run the benchmark on the Performance tab, which measures that.");
            text.AppendLine("Shadow draw is the part that grows with how much scenery is on screen, so a heavily");
            text.AppendLine("modded map is where it shows. Turning off shadows for objects is the setting for it.");
            return text.ToString().TrimEnd();
        }
    }
}
