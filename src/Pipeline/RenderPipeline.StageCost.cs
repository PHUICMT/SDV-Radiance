using System;
using System.Diagnostics;
using System.Text;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - WHAT EACH PASS OF THE CHAIN COSTS, separately.
    ///
    /// <para>
    /// The chain is the most expensive thing this mod asks the graphics card to do (0.34-0.39 ms
    /// against 0.16-0.18 ms of submission, every spot and every hour it was measured). That number
    /// is a sum over six or seven full-screen passes run back to back, and a sum cannot be acted
    /// on: fusing two passes, dropping one, or moving one to half resolution are three different
    /// fixes for three different answers about where inside it the time is.
    /// </para>
    ///
    /// <para>
    /// The per-stage CPU timing that already existed was gated on <c>DebugLogging</c> and printed
    /// into the SMAPI log, which is to say it was invisible to the report a player sends and to
    /// every measurement script here. This window is always collected and always in the report,
    /// for the same reason <see cref="FrameCost"/> is: the first question of a performance report
    /// is which pass, and an answer that requires the reporter to turn something on first is not
    /// an answer anybody ever gets.
    /// </para>
    ///
    /// <para>
    /// The GPU column is only filled while <see cref="GpuTimer"/> is on, and averages over the
    /// frames whose result actually came back rather than over the window, because a dropped
    /// slot means unknown and not zero. The CPU column measures submission and reads near zero
    /// for a pass that is pure fill - which is the whole reason the two are printed side by side.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        private const int StageWindowFrames = 300;      // five seconds at 60 fps, same as FrameCost

        // Sized from the stage-name list, never by hand: the wet stage arrived as index 11
        // against eight of these still sized [11], and the chain threw its way through three
        // frames before the recovery path caught it. A count that exists in one place cannot
        // disagree with itself.
        private readonly double[] _stageCpuAccumulated = new double[_stageNames.Length];
        private readonly double[] _stageGpuAccumulated = new double[_stageNames.Length];
        private readonly int[] _stageCpuFrames = new int[_stageNames.Length];
        private readonly int[] _stageGpuFrames = new int[_stageNames.Length];

        private readonly double[] _stageCpuAverage = new double[_stageNames.Length];
        private readonly double[] _stageGpuAverage = new double[_stageNames.Length];
        private readonly bool[] _stageGpuKnown = new bool[_stageNames.Length];
        private readonly int[] _stageRanFrames = new int[_stageNames.Length];

        /// <summary>How many passes the chain ran, averaged over the window. The count is the
        /// number a fill-bound machine pays for most directly, and it moves with the scene (water
        /// leaving the window, clouds fading out at dusk) rather than with the settings alone.</summary>
        private double _stagePassesPerFrame;
        private int _stagePassAccumulated;
        private int _stageWindowFrames;

        /// <summary>Marks in the slot the stage will report under. Held across the call because
        /// the GPU result arrives frames later and has to be attributed to the right name.</summary>
        private long _stageCpuStart;
        private int _stageMarked = -1;

        private void BeginStageCost(int nameIndex)
        {
            _stageMarked = nameIndex;
            GpuTimer.MarkStageBegin(nameIndex);
            _stageCpuStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Take work that ran inside the open stage's bracket but is not the stage's own
        /// out of its CPU column, by moving the bracket's start forward by exactly that long.</summary>
        /// <remarks>
        /// The precipitation's sky half is drawn inside the water stage on purpose - streaks land
        /// on the rippled result so they hang straight over a river - and it books its own cost
        /// under <see cref="FrameCost.Part.Precipitation"/> as it always has. Until this existed
        /// the same milliseconds were counted a second time in the water ROW of this table, which
        /// is how a performance audit read "the water pass costs ten times more in rain" and spent
        /// a question on a pass that had not changed at all: 0.018 to 0.193 ms in the same scene,
        /// all of it rain streak submission. The GPU column still contains the streaks' draw - the
        /// card timer brackets a whole stage and cannot split one - but the streaks are a few
        /// hundred alpha quads and the CPU column was where the misreading happened.
        /// </remarks>
        private void ExcludeTicksFromOpenStage(long stopwatchTicks)
        {
            if (_stageMarked >= 0)
                _stageCpuStart += stopwatchTicks;
        }

        /// <returns>What the pass took to submit, in milliseconds, for the debug-log accumulators
        /// that already existed. Zero if no pass was open.</returns>
        private double EndStageCost()
        {
            if (_stageMarked < 0)
                return 0;
            double ms = (Stopwatch.GetTimestamp() - _stageCpuStart) * 1000.0 / Stopwatch.Frequency;
            _stageCpuAccumulated[_stageMarked] += ms;
            _stageCpuFrames[_stageMarked]++;
            GpuTimer.MarkStageEnd(_stageMarked);
            _stageMarked = -1;
            return ms;
        }

        /// <summary>Close the window's frame: collect whatever GPU results came back for the frame
        /// that finished three frames ago, and roll the averages over when the window fills.</summary>
        private void EndStageCostFrame(int passesThisFrame)
        {
            if (GpuTimer.Ready)
            {
                for (int i = 0; i < _stageGpuFrames.Length; i++)
                {
                    if (!GpuTimer.TryTakeLastStage(i, out double gpuMs))
                        continue;
                    _stageGpuAccumulated[i] += gpuMs;
                    _stageGpuFrames[i]++;
                }
            }

            _stagePassAccumulated += passesThisFrame;
            if (++_stageWindowFrames < StageWindowFrames)
                return;

            for (int i = 0; i < _stageCpuFrames.Length; i++)
            {
                _stageRanFrames[i] = _stageCpuFrames[i];
                _stageCpuAverage[i] = _stageCpuFrames[i] > 0 ? _stageCpuAccumulated[i] / _stageCpuFrames[i] : 0;
                _stageGpuKnown[i] = _stageGpuFrames[i] > 0;
                _stageGpuAverage[i] = _stageGpuFrames[i] > 0 ? _stageGpuAccumulated[i] / _stageGpuFrames[i] : 0;
                _stageCpuAccumulated[i] = _stageGpuAccumulated[i] = 0;
                _stageCpuFrames[i] = _stageGpuFrames[i] = 0;
            }
            _stagePassesPerFrame = (double)_stagePassAccumulated / _stageWindowFrames;
            _stagePassAccumulated = 0;
            _stageWindowFrames = 0;
        }

        /// <summary>Drop the GPU column when the timer is switched off, so a report taken straight
        /// afterwards does not print stale numbers as current. Same reason as
        /// <see cref="FrameCost.ForgetGpu"/>, and the same mistake it was written to fix.</summary>
        internal void ForgetStageGpu()
        {
            Array.Clear(_stageGpuAccumulated, 0, _stageGpuAccumulated.Length);
            Array.Clear(_stageGpuFrames, 0, _stageGpuFrames.Length);
            Array.Clear(_stageGpuAverage, 0, _stageGpuAverage.Length);
            Array.Clear(_stageGpuKnown, 0, _stageGpuKnown.Length);
        }

        /// <summary>The per-pass block of the report: which passes ran, what each cost to submit,
        /// and what the card spent on it.</summary>
        internal string DescribeStageCost()
        {
            var sb = new StringBuilder();
            if (_stageWindowFrames == 0 && _stagePassesPerFrame <= 0)
                return "no window has closed yet - the chain has not run for five seconds.";

            sb.Append($"the chain ran {_stagePassesPerFrame:0.00} full-screen passes per frame on average, ");
            sb.AppendLine($"at {_lastScaledWidth}x{_lastScaledHeight} (window frame {_frameWidth}x{_frameHeight}).");
            if (!GpuTimer.Ready)
                sb.AppendLine("GPU column is blank: radiance_gputime is off. CPU here is submission only.");
            sb.AppendLine("  pass          cpu submit      gpu           frames it ran in");

            double cpuTotal = 0, gpuTotal = 0;
            bool anyGpu = false;
            for (int i = 0; i < _stageNames.Length; i++)
            {
                if (_stageRanFrames[i] == 0)
                    continue;
                cpuTotal += _stageCpuAverage[i];
                string gpu = "         -";
                if (_stageGpuKnown[i])
                {
                    gpu = $"{_stageGpuAverage[i],7:0.000} ms";
                    gpuTotal += _stageGpuAverage[i];
                    anyGpu = true;
                }
                sb.AppendLine($"  {_stageNames[i],-12} {_stageCpuAverage[i],7:0.000} ms   {gpu}   {_stageRanFrames[i]}/{StageWindowFrames}");
            }
            sb.Append($"  {"all passes",-12} {cpuTotal,7:0.000} ms");
            sb.AppendLine(anyGpu ? $"   {gpuTotal,7:0.000} ms" : "");
            // The chain's own part measures more than its passes do: the capture blit, the target
            // upscale, the builders that run before the list is assembled. The gap between the two
            // is where the fixed overhead of entering the chain lives, and it is worth seeing.
            sb.AppendLine("the chain's own line in the cost table also covers the capture blit, the");
            sb.AppendLine("builders, and the upscale - the difference from the sum above is that overhead.");
            sb.Append(DescribeChainSteps());
            return sb.ToString();
        }
    }
}
