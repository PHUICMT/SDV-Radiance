using System;
using System.Collections.Generic;
using System.Text;

namespace SDVRadiance
{
    /// <summary>
    /// Main-thread time per named phase of a rebuild, worst and average since the last report.
    /// </summary>
    /// <remarks>
    /// The per-frame report times a grid rebuild as one row, and one row cannot say which half
    /// of it to move to a worker thread: the water mask's row said 2.4 ms and the fix depended
    /// on whether that was the gather or the upload. This is the same three-number split, kept
    /// per phase name so a build can be cut wherever its author wants to know. Cheap: a
    /// dictionary lookup per phase per rebuild, and rebuilds are seconds apart.
    /// </remarks>
    internal static class PhaseCost
    {
        private sealed class Phase
        {
            public int Count;
            public double Sum, Worst;
        }

        private static readonly Dictionary<string, Phase> _phases = new();
        private static readonly List<string> _order = new();

        /// <summary>Milliseconds since <paramref name="startTimestamp"/> (a Stopwatch timestamp).</summary>
        internal static double MillisecondsSince(long startTimestamp)
            => (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Record one run of <paramref name="name"/> that took <paramref name="milliseconds"/>.</summary>
        internal static void Note(string name, double milliseconds)
        {
            if (!_phases.TryGetValue(name, out Phase? phase))
            {
                phase = new Phase();
                _phases[name] = phase;
                _order.Add(name);
            }
            phase.Count++;
            phase.Sum += milliseconds;
            if (milliseconds > phase.Worst) phase.Worst = milliseconds;
        }

        /// <summary>Time the phase from <paramref name="startTimestamp"/> to now, then return a fresh
        /// timestamp so the next phase can chain from it.</summary>
        internal static long NoteSince(string name, long startTimestamp)
        {
            Note(name, MillisecondsSince(startTimestamp));
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>Every phase whose name starts with <paramref name="prefix"/>, one line each,
        /// then forgotten so the next report starts clean.</summary>
        internal static string Describe(string prefix)
        {
            var text = new StringBuilder();
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                string name = _order[i];
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                Phase phase = _phases[name];
                text.Insert(0, $"  {name,-46} {phase.Count,5} time(s)  avg {(phase.Count > 0 ? phase.Sum / phase.Count : 0):0.000} ms  worst {phase.Worst:0.000} ms\n");
                _phases.Remove(name);
                _order.RemoveAt(i);
            }
            return text.Length == 0 ? $"  (no {prefix} rebuild since the last report)\n" : text.ToString();
        }
    }
}
