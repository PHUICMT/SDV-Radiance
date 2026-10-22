using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SDVRadiance
{
    /// <summary>
    /// The tuner's motion: every value that eases (a switch's knob, a hover's light, the rail's
    /// marker, a group folding) lives here under a name, not on the row that draws it. The menu
    /// builds its rows again on every click, so a value kept on a row would start over each time
    /// and nothing would ever move; kept by name, a switch flipped by a click still slides.
    /// </summary>
    internal static class TunerMotion
    {
        private static readonly Dictionary<string, float> Values = new(StringComparer.Ordinal);
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _lastFrame;

        /// <summary>Seconds since the last drawn frame, at most a tenth so a hitch never jumps.</summary>
        internal static float FrameSeconds { get; private set; }

        /// <summary>Called once at the start of each draw of the menu.</summary>
        internal static void BeginFrame()
        {
            double now = Clock.Elapsed.TotalSeconds;
            FrameSeconds = (float)Math.Clamp(now - _lastFrame, 0.0, 0.1);
            _lastFrame = now;
        }

        /// <summary>
        /// Follow <paramref name="target"/> smoothly: a little over two thirds of the way in
        /// <paramref name="seconds"/>, and settled in about three times that. The first time a
        /// name is asked for it starts at the target, so nothing slides in on opening the menu.
        /// </summary>
        internal static float Follow(string name, float target, float seconds = 0.08f)
        {
            if (!Values.TryGetValue(name, out float value))
            {
                Values[name] = target;
                return target;
            }
            float keep = seconds <= 0f ? 0f : (float)Math.Exp(-FrameSeconds / seconds);
            value = target + (value - target) * keep;
            if (Math.Abs(value - target) < 0.002f * Math.Max(1f, Math.Abs(target)))
                value = target;
            Values[name] = value;
            return value;
        }

        /// <summary>Start a name at a value of the caller's choosing, for a move that begins now.</summary>
        internal static void Set(string name, float value) => Values[name] = value;

        /// <summary>Whether anything is still on its way somewhere, for code that must wait for it.</summary>
        internal static bool Settled(string name, float target)
            => !Values.TryGetValue(name, out float value) || Math.Abs(value - target) < 0.002f * Math.Max(1f, Math.Abs(target));
    }
}
