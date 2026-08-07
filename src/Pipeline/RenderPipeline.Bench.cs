using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Built-in benchmark: what does this mod actually cost on THIS machine, and which
    /// effect resolution should this player use?
    ///
    /// <para>
    /// The naive measurement does not work. Stardew runs a fixed 60 fps timestep, so on any
    /// machine with headroom the GPU has already finished most of the frame by the time we
    /// could ask, and a wall-clock probe reads the leftover queue (~0.5 ms here) rather than
    /// the work. Amplification fixes it: run the effect chain N times in one frame and take
    /// the SLOPE. The per-chain cost falls out of the difference, and the constant overhead
    /// of the probe and of the game's own drawing cancels.
    /// </para>
    ///
    /// <para>
    /// The sweep walks the effect-resolution settings, so the recommendation is measured on
    /// the player's own hardware and scene instead of guessed from a spec sheet.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Extra times the stage chain runs this frame, for the benchmark's slope.
        /// The extra runs render into scratch and are thrown away — only their COST matters.</summary>
        private int _benchExtraChains;

        /// <summary>
        /// Extra times the SHADOW pass runs this frame, read by ModEntry (which owns the shadow
        /// renderer) and drawn into a scratch target so nothing extra reaches the screen.
        /// <para>
        /// The sweep above measures the full-screen chain and nothing else, because amplification
        /// cancels everything that does not repeat with it. The shadow pass runs in a different
        /// event entirely, so it cancelled out too, and the number this benchmark reported was
        /// therefore silent about the one part of the mod whose cost grows with how much scenery
        /// and how many mods are on screen. A player with two hundred content mods could be told
        /// "this machine has room to spare" while shadows were what was eating their frame.
        /// </para>
        /// </summary>
        internal static int BenchExtraShadowRuns;

        private const int BenchWarmupFrames = 20;    // let GPU clocks and the eased fades settle
        private const int BenchSampleFrames = 40;
        private const int BenchAmplify = 6;          // extra chains in the "many" half

        private static readonly float[] BenchScales = { 1f, 0.75f, 0.5f };
        /// <summary>Sweep steps: one/many per effect resolution, then one/many for shadows.</summary>
        private static int BenchTotalSteps => BenchScales.Length * 2 + 2;

        internal static bool BenchRunning;

        /// <summary>Last result, for the tuner to show. A console line is no use to a player
        /// who never opens the console, and this feature exists for exactly those players.</summary>
        internal static readonly List<string> BenchSummary = new();
        internal static float BenchSuggestedScale;
        /// <summary>Bumped when a run finishes, so an open menu knows to rebuild itself.</summary>
        internal static int BenchStamp;
        /// <summary>0..1 while a run is in progress, for the in-menu readout.</summary>
        internal static float BenchProgress;
        private int _benchStep;                      // each scale gets one/many, then shadows get one/many
        private int _benchFrame;
        private double _benchAccumulator;
        private int _benchSamples;
        private float _benchSavedScale;
        private bool _benchSavedProbe;
        private readonly List<(float Scale, double One, double Many)> _benchResults = new();
        private double _benchPendingOne;
        private double _benchShadowMilliseconds = -1;   // < 0 = not measured

        /// <summary>Arm the sweep. Runs for about ten seconds and restores every setting it
        /// touched, including the ones it had to change to measure.</summary>
        public void StartBenchmark(ModConfig config)
        {
            if (BenchRunning)
            {
                _monitor.Log("Benchmark already running.", LogLevel.Info);
                return;
            }
            if (!config.Enabled)
            {
                _monitor.Log("Radiance is switched off, so there is nothing to measure. Turn it on and run this again.", LogLevel.Warn);
                return;
            }
            BenchRunning = true;
            _benchSavedScale = config.RenderScale;
            _benchSavedProbe = GpuProbe;
            GpuProbe = true;
            _benchStep = 0;
            _benchFrame = 0;
            _benchAccumulator = 0;
            _benchSamples = 0;
            _benchPendingOne = 0;
            _benchShadowMilliseconds = -1;
            BenchExtraShadowRuns = 0;
            _benchResults.Clear();
            BenchSummary.Clear();
            BenchProgress = 0f;
            config.RenderScale = BenchScales[0];
            _monitor.Log($"Benchmarking {BenchScales.Length} effect-resolution settings and the shadow pass at {Game1.currentLocation?.Name} — "
                + "about twelve seconds. The picture will flicker between settings; that is the measurement, not a fault.", LogLevel.Info);
        }

        /// <summary>One benchmark frame. Called at the end of Apply, after the probe has taken
        /// this frame's reading.</summary>
        private void BenchTick(ModConfig config, double frameMilliseconds)
        {
            int scaleIndex = _benchStep / 2;
            bool manyHalf = (_benchStep & 1) == 1;
            bool shadowPair = scaleIndex >= BenchScales.Length;
            // The shadow pair runs at the player's own resolution with the chain left alone, so
            // the only thing that differs between its halves is the shadow pass.
            config.RenderScale = shadowPair ? _benchSavedScale : BenchScales[scaleIndex];
            _benchExtraChains = !shadowPair && manyHalf ? BenchAmplify : 0;
            BenchExtraShadowRuns = shadowPair && manyHalf ? BenchAmplify : 0;

            _benchFrame++;
            BenchProgress = Math.Clamp(
                (_benchStep * (BenchWarmupFrames + BenchSampleFrames) + _benchFrame)
                / (float)(BenchTotalSteps * (BenchWarmupFrames + BenchSampleFrames)), 0f, 1f);
            if (_benchFrame <= BenchWarmupFrames)
                return;

            _benchAccumulator += frameMilliseconds;
            _benchSamples++;
            if (_benchSamples < BenchSampleFrames)
                return;

            double avg = _benchAccumulator / _benchSamples;
            if (!manyHalf)
                _benchPendingOne = avg;
            else if (shadowPair)
                _benchShadowMilliseconds = Math.Max(0, (avg - _benchPendingOne) / BenchAmplify);
            else
                _benchResults.Add((BenchScales[scaleIndex], _benchPendingOne, avg));

            _benchFrame = 0;
            _benchAccumulator = 0;
            _benchSamples = 0;
            _benchStep++;

            if (_benchStep < BenchTotalSteps)
                return;

            _benchExtraChains = 0;
            BenchExtraShadowRuns = 0;
            config.RenderScale = _benchSavedScale;
            GpuProbe = _benchSavedProbe;
            BenchRunning = false;
            // The amplified frames were nothing like normal play; leaving them in the rolling
            // meter would make the report that reads it lie for the next five seconds.
            FrameCost.Reset();
            ReportBenchmark(config);
        }

        private void ReportBenchmark(ModConfig config)
        {
            BenchSummary.Clear();
            BenchSummary.Add($"{Game1.currentLocation?.Name} at {_lastViewportWidth}x{_lastViewportHeight}"
                + ((Game1.currentLocation?.IsOutdoors ?? false) ? "" : " (indoors - outdoors with water costs more)"));

            // Shadows are not part of the chain and no effect-resolution setting touches them,
            // so their cost is a constant added to every row below. Counting it against the
            // budget is the whole point: a resolution that fits on its own can still miss the
            // frame once the shadow pass is paid for.
            double shadowMilliseconds = Math.Max(0, _benchShadowMilliseconds);
            double budget = 16.67 / 3.0;

            double smallest = double.MaxValue;
            float recommend = BenchScales[BenchScales.Length - 1];
            foreach (var (scale, one, many) in _benchResults)
            {
                // Slope, not the raw reading: the extra chains are the only difference between
                // the two halves, so everything constant (the game's draw, the probe itself)
                // subtracts out and what is left is one chain.
                double perChain = Math.Max(0, (many - one) / BenchAmplify);
                if (perChain < smallest) smallest = perChain;
                BenchSummary.Add($"resolution {scale:0.00}  =  {perChain:0.00} ms per frame");
                // Highest quality that still leaves the frame comfortable. A third of the
                // budget is the line: past that a weaker GPU, or a busier map than the one
                // being stood on, starts missing frames.
                if (perChain + shadowMilliseconds <= budget)
                    recommend = Math.Max(recommend, scale);
            }

            if (_benchShadowMilliseconds < 0)
                BenchSummary.Add("shadows: not measured");
            else if (shadowMilliseconds < 0.05)
                BenchSummary.Add("shadows  =  under 0.05 ms per frame here");
            else
                BenchSummary.Add($"shadows  =  {shadowMilliseconds:0.00} ms per frame (no resolution setting changes this)");

            if (_benchResults.Count == 0)
                BenchSummary.Add("No samples - was the game minimised?");
            else if (smallest + shadowMilliseconds < 0.05)
            {
                BenchSummary.Add("Below what can be measured here: this machine has room to spare.");
                recommend = 1f;
            }
            BenchSuggestedScale = recommend;
            BenchSummary.Add($"Suggested: {recommend:0.00}"
                + (Math.Abs(recommend - config.RenderScale) < 0.001f ? " - what you already have" : ""));
            // When shadows alone eat the budget, the resolution slider is the wrong dial and
            // saying so is worth more than a number. This is the case a heavily modded install
            // lands in, and the one the benchmark used to be silent about.
            if (shadowMilliseconds > budget * 0.5)
            {
                // Two short lines rather than one long one: the tuner shrinks a line to fit its
                // column, and a sentence this important should not arrive at half size.
                BenchSummary.Add("Shadows, not the effects, are most of what this mod costs here.");
                BenchSummary.Add("Turn off shadows for objects, or use the Performance preset.");
            }
            BenchSummary.Add("Measured where you stand. Try again on a busy outdoor map for the worst case.");
            BenchStamp++;
            BenchProgress = 0f;

            _monitor.Log("[bench] " + string.Join(" | ", BenchSummary), LogLevel.Info);
            try { Game1.addHUDMessage(new HUDMessage($"Radiance: suggested effect resolution {recommend:0.00}", 2)); } catch { }
        }
    }
}
