using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - PER-EFFECT COST. What each individual effect costs on this machine, in
    /// this scene, measured properly.
    ///
    /// <para>
    /// The obvious way to do this is to toggle an effect off, look at the frame time, and
    /// subtract. That was tried, from a python harness, and it does not work: after the shadow
    /// work the farm scene runs at 400 fps, so a frame is 2.5 ms and the machine's own drift
    /// between two consecutive measurements is 0.2 to 0.5 ms. The effects being measured cost
    /// 0.05 to 0.4 ms. Every row came back inside the noise, and the ones that did not were
    /// noise wearing a plausible number. Measuring the difference of two large, wobbling
    /// quantities to find a small one is the wrong shape of experiment, and no amount of extra
    /// baselines fixes it.
    /// </para>
    ///
    /// <para>
    /// So do what the resolution benchmark next door already does: AMPLIFY. Run the chain (and
    /// the shadow pass) several extra times into scratch, throw the pictures away, and keep the
    /// slope. The effect under test now costs seven times what it costs in a real frame while
    /// the machine's drift stays exactly the same size, which turns a 0.1 ms signal buried in
    /// 0.4 ms of noise into a 0.7 ms signal in the same 0.4 ms. Divide by seven at the end.
    /// </para>
    ///
    /// <para>
    /// It also reads the GPU's own wall clock rather than frame time, because most of what is
    /// being measured here is fill, and fill is precisely what CPU-side timing cannot see. That
    /// lesson cost this project a day: object shadows were half the frame while every probe we
    /// shipped reported them as a rounding error.
    /// </para>
    ///
    /// <para>Runs in one pass, in-game, in about half a minute. Nothing is written to
    /// config.json: every value is put back as it was found, including on an early exit.</para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Effects to price, by config property name. Booleans are flipped away from
        /// whatever the player has (so an effect they run with OFF is measured by turning it ON);
        /// floats named here are measured against zero.</summary>
        private static readonly (string Key, string Label)[] EffectCostKeys =
        {
            ("BloomEnabled",               "bloom"),
            ("ColorGradeEnabled",          "colour grade"),
            ("GodRaysEnabled",             "god rays"),
            ("FogEnabled",                 "fog"),
            ("CloudShadowEnabled",         "cloud shadows"),
            ("TiltShiftEnabled",           "tilt shift"),
            ("VignetteEnabled",            "vignette"),
            ("ChromaticAberrationEnabled", "chromatic aberration"),
            ("WaterEnabled",               "water ripple"),
            ("WaterReflection",            "water reflection"),
            ("LightingEnabled",            "lighting"),
            ("FloodLightingEnabled",       "flood GI"),
            ("LightingShadows",            "light shadows"),
            ("DirectionalShadowsEnabled",  "directional shadows"),
            ("DirectionalShadowObjects",   "object shadows"),
            ("DirectionalShadowBlur",      "shadow blur"),
            ("WindowEffectsEnabled",       "window effects"),
        };

        internal static bool EffectCostRunning;
        internal static readonly List<string> EffectCostSummary = new();

        /// <summary>
        /// Settle time before a half is sampled, in SECONDS of wall clock rather than frames.
        ///
        /// <para>
        /// It was frames, and that produced a ghost. Flipping an effect back on leaves its systems
        /// rebuilding for several game TICKS - the flood lightmap and its occluder grids are gated
        /// on ticks and content, not on frames - while the sweep, running uncapped at four hundred
        /// frames a second, treated twelve frames as enough and started measuring 30 ms later. The
        /// recovery landed in the NEXT effect's baseline, so whatever followed flood GI in the
        /// list was charged for flood GI coming back.
        /// </para>
        ///
        /// <para>
        /// That is how "light shadows" came to be the most expensive effect in the mod at 0.19 ms,
        /// four times over, in every scene. It cost 0.19 in a lamp-lit town at night and 0.19 in a
        /// small shop; it did not move when the shadow march was shortened, halved, or stripped of
        /// its texture fetches entirely. It was flood GI's number wearing its neighbour's name -
        /// note that flood GI measured MINUS 0.20, the same size with the other sign. Moved to the
        /// front of the list, the same setting measures 0.001 ms.
        /// </para>
        ///
        /// <para>Half a second is about thirty ticks, comfortably past the tick-gated rebuilds,
        /// and independent of how fast the machine happens to be drawing.</para>
        /// </summary>
        private const double EcSettleSeconds = 0.5;
        private const int EcSampleFrames = 24;

        private int _ecIndex, _ecHalf, _ecFrame, _ecSamples;
        private double _ecHalfStarted;
        private double _ecAccumulator, _ecPendingBase;
        private object? _ecSavedValue;
        private bool _ecSavedProbe;
        private float _ecSavedScale;
        private readonly List<(string Label, double Cost, bool MeasuredByTurningOn)> _ecResults = new();

        /// <summary>Arm the per-effect sweep.</summary>
        public void StartEffectCost(ModConfig config, int amplify = 6)
        {
            if (EffectCostRunning || BenchRunning)
            {
                _monitor.Log("A measurement is already running.", LogLevel.Info);
                return;
            }
            if (!config.Enabled)
            {
                _monitor.Log("Radiance is switched off, so there is nothing to measure.", LogLevel.Warn);
                return;
            }
            BenchAmplify = Math.Max(1, amplify);
            EffectCostRunning = true;
            _ecSavedProbe = GpuProbe;
            _ecSavedScale = config.RenderScale;
            GpuProbe = true;
            _ecIndex = _ecHalf = _ecFrame = _ecSamples = 0;
            _ecAccumulator = _ecPendingBase = 0;
            _ecSavedValue = null;
            _ecResults.Clear();
            EffectCostSummary.Clear();
            _monitor.Log($"Pricing {EffectCostKeys.Length} effects at {Game1.currentLocation?.Name} by amplified slope - "
                + "about thirty seconds. The picture will stutter and flicker; that is the measurement, not a fault. "
                + "Stand somewhere demanding and do not move.", LogLevel.Info);
        }

        private static System.Reflection.PropertyInfo? EcProp(string key)
            => typeof(ModConfig).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        /// <summary>Flip a value away from what it is, and say whether the flip turned it ON.
        /// Returns false if the property cannot be measured (missing, or a float already zero).</summary>
        private static bool EcFlip(ModConfig config, string key, out object? saved, out bool turnedOn)
        {
            saved = null; turnedOn = false;
            var prop = EcProp(key);
            if (prop == null || !prop.CanWrite || !prop.CanRead)
                return false;
            saved = prop.GetValue(config);
            if (saved is bool b)
            {
                prop.SetValue(config, !b);
                turnedOn = !b;
                return true;
            }
            if (saved is float f)
            {
                if (f <= 0f) return false;   // nothing to flip against
                prop.SetValue(config, 0f);
                return true;
            }
            return false;
        }

        private static void EcRestore(ModConfig config, string key, object? saved)
        {
            if (saved == null) return;
            try { EcProp(key)?.SetValue(config, saved); } catch { }
        }

        /// <summary>One sweep frame, called with the GPU's measured time for the frame just drawn.</summary>
        private void EffectCostTick(ModConfig config, double gpuMilliseconds)
        {
            // Both amplifiers on for every effect, so the divisor is the same whether the effect
            // under test lives in the full-screen chain or in the shadow pass. An effect in
            // neither simply comes out at zero, which is a true answer.
            _benchExtraChains = BenchAmplify;
            BenchExtraShadowRuns = BenchAmplify;

            if (_ecIndex >= EffectCostKeys.Length)
            {
                FinishEffectCost(config);
                return;
            }
            (string key, _) = EffectCostKeys[_ecIndex];

            // Entering the flipped half: flip once, on the first frame of it.
            if (_ecHalf == 1 && _ecFrame == 0 && _ecSavedValue == null)
            {
                if (!EcFlip(config, key, out _ecSavedValue, out bool on))
                {
                    // Nothing to measure here. Record it as skipped and move on rather than
                    // silently dropping a row - a missing line reads as "free".
                    _ecResults.Add((EffectCostKeys[_ecIndex].Label + " (not measurable)", 0, false));
                    NextEffect(config, key);
                    return;
                }
                _ecFlipTurnedOn = on;
            }

            _ecFrame++;
            if (_ecFrame == 1)
                _ecHalfStarted = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            if (now - _ecHalfStarted < EcSettleSeconds)
                return;
            _ecAccumulator += gpuMilliseconds;
            _ecSamples++;
            if (_ecSamples < EcSampleFrames)
                return;

            double avg = _ecAccumulator / _ecSamples;
            _ecFrame = 0; _ecAccumulator = 0; _ecSamples = 0;

            if (_ecHalf == 0)
            {
                _ecPendingBase = avg;
                _ecHalf = 1;
                return;
            }

            // Positive always means "running this effect makes the frame longer by this much".
            double delta = _ecFlipTurnedOn ? avg - _ecPendingBase : _ecPendingBase - avg;
            _ecResults.Add((EffectCostKeys[_ecIndex].Label, delta / (BenchAmplify + 1), _ecFlipTurnedOn));
            NextEffect(config, key);
        }

        private bool _ecFlipTurnedOn;

        private void NextEffect(ModConfig config, string key)
        {
            EcRestore(config, key, _ecSavedValue);
            _ecSavedValue = null;
            _ecFlipTurnedOn = false;
            _ecIndex++;
            _ecHalf = 0;
            _ecFrame = 0;
            _ecAccumulator = 0;
            _ecSamples = 0;
        }

        private void FinishEffectCost(ModConfig config)
        {
            _benchExtraChains = 0;
            BenchExtraShadowRuns = 0;
            GpuProbe = _ecSavedProbe;
            config.RenderScale = _ecSavedScale;
            EffectCostRunning = false;
            // The amplified frames were nothing like normal play; leaving them in the rolling
            // meter would make the next report lie for five seconds.
            FrameCost.Reset();

            _ecResults.Sort((a, b) => b.Cost.CompareTo(a.Cost));
            EffectCostSummary.Clear();
            EffectCostSummary.Add($"Per-effect GPU cost at {Game1.currentLocation?.Name}, "
                + $"{Game1.timeOfDay:0000}, measured by amplified slope (x{BenchAmplify + 1}):");
            foreach (var (label, cost, byOn) in _ecResults)
                EffectCostSummary.Add($"  {label,-24} {cost,7:0.000} ms{(byOn ? "   (measured by turning it ON)" : "")}");
            EffectCostSummary.Add("");
            EffectCostSummary.Add("Amplification is what makes these readable: each effect runs seven times per");
            EffectCostSummary.Add("frame here, so its cost clears the machine's own drift, and the total is divided");
            EffectCostSummary.Add("back down. Anything reading near zero genuinely costs near nothing IN THIS SCENE -");
            EffectCostSummary.Add("water in a scene with no water is free, and so is a shadow nothing casts.");
            foreach (string line in EffectCostSummary)
                _monitor.Log(line, LogLevel.Info);
        }
    }
}
