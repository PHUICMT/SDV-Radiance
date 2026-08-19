using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - LETTING THE MOD FIND ITS OWN RENDER SCALE.
    ///
    /// <para>
    /// The render scale is the one setting in this mod with a quadratic effect: half the scale is a
    /// quarter of the pixels through every pass of the chain. It is also the setting nobody in a
    /// performance report has ever mentioned touching. The reports read "it tanks my PC on a larger
    /// map, and yes, I know about the performance option, it does not do much" - from a player who
    /// had already found the presets and still had no idea that the slider under them was the one
    /// that would have helped.
    /// </para>
    ///
    /// <para>
    /// So the mod watches its own frame time and walks the scale down until the frame fits, then
    /// walks it back up when the scene gets easier. What the player sets stays the CEILING: this
    /// only ever asks for less than they chose, never more.
    /// </para>
    ///
    /// <para>
    /// Three things this must not do, each of which is a mistake already made once in this project
    /// and written down:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>React to an alt-tab.</b> MonoGame sleeps 20 ms a frame while the window
    /// is inactive, which reads as 40 fps with nothing wrong at all. Steering on the headline frame
    /// time would ratchet the quality down for somebody who was not even looking at the game, and
    /// they would come back to a blurry one. It steers on the FOCUSED average and holds still
    /// entirely while the window is in the background.</description></item>
    /// <item><description><b>Pump.</b> A controller that reacts to every frame oscillates, and a
    /// resolution that oscillates is worse than one that is simply low. One step at a time, only
    /// after the overrun has held for a second and a half, and then nothing at all for three
    /// seconds while the change takes effect.</description></item>
    /// <item><description><b>Keep paying when it is not helping.</b> A machine bound by its CPU
    /// gets nothing from a smaller buffer, and would otherwise be walked all the way to the floor
    /// for a softer picture and no frames back. Every step down is treated as an experiment: the
    /// frame time before it is remembered, and if the step bought less than two per cent the step
    /// is given back and the controller stands down for a minute before trying again.</description></item>
    /// </list>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        private const float AutoScaleFloor = 0.5f;      // the manual slider's floor, for the same reasons
        private const float AutoScaleStep = 0.1f;
        private const double AutoOverSeconds = 1.5;     // missing the budget this long before stepping down
        private const double AutoUnderSeconds = 10.0;   // comfortable this long before giving any of it back
        private const double AutoHoldSeconds = 3.0;     // after any change, before judging it
        private const double AutoBackoffSeconds = 60.0; // not trying again, after a step that did nothing
        private const double AutoOverBudget = 1.15;     // frame time above this multiple of the target is a miss
        private const double AutoUnderBudget = 1.02;    // at or under this, there is room to give quality back

        /// <summary>
        /// EVERY INTERVAL HERE IS WALL-CLOCK SECONDS, and none of them is a frame count.
        ///
        /// <para>
        /// They were frame counts first, with the comments converting them to seconds at sixty
        /// frames a second - which is a machine, not a unit. Watched with the frame cap lifted, the
        /// game ran at about 350 fps and the whole controller went six times too fast: the three
        /// second settling hold became half a second, the frame time had not finished moving when
        /// the step was judged, and steps that were genuinely helping were read as useless and
        /// handed back. Nine steps down and four given back inside seventy seconds, each one
        /// reallocating six render targets.
        /// </para>
        ///
        /// <para>
        /// A player on a 144 Hz monitor would have got the same fault at less than half the
        /// intensity, which is exactly the kind of thing that never reproduces on the machine it
        /// was written on.
        /// </para>
        /// </summary>
        private float _autoScale = 1f;
        private double _autoOverSeconds, _autoUnderSeconds, _autoHoldLeft, _autoBackoffLeft;
        private long _autoLastStamp;
        private double _autoProbeBeforeMs;              // frame time before the step being judged
        private bool _autoProbePending;
        private int _autoStepsDown, _autoStepsUp, _autoStepsUndone;
        private string _autoWhy = "not running";

        /// <summary>Seconds since this was last asked, with a load screen or an alt-tab clamped out.
        /// Reset whenever the controller stands still, so the pause does not arrive as one huge
        /// tick that satisfies an interval on its own.</summary>
        private double AutoElapsedSeconds()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_autoLastStamp == 0)
            {
                _autoLastStamp = now;
                return 0;
            }
            double seconds = (now - _autoLastStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            _autoLastStamp = now;
            return seconds > 0.25 ? 0 : seconds;
        }

        /// <summary>What the chain should actually work at this frame.</summary>
        /// <param name="ceiling">The player's own setting, already clamped. Never exceeded.</param>
        private float AutoRenderScale(ModConfig config, float ceiling)
        {
            if (!config.RenderScaleAuto)
            {
                _autoScale = ceiling;
                _autoWhy = "off - the render scale is whatever you set it to";
                return ceiling;
            }
            // The benchmark and the per-effect cost probe drive the render scale themselves, and a
            // second hand on the same dial would make both of their numbers meaningless.
            if (BenchRunning || EffectCostRunning || Game1.game1.takingMapScreenshot)
            {
                _autoWhy = "standing aside while a measurement drives the scale itself";
                return ceiling;
            }

            if (_autoScale <= 0f || _autoScale > ceiling)
                _autoScale = ceiling;

            // Unfocused frames are not evidence about this machine. Hold everything still rather
            // than counting them as either comfort or overrun.
            bool focused;
            try { focused = Game1.game1?.IsActive ?? true; }
            catch { focused = true; }
            if (!focused)
            {
                _autoLastStamp = 0;     // do not bank the time spent in the background
                _autoWhy = $"holding at {_autoScale:0.00} - the window is in the background, which fakes a slow frame";
                return _autoScale;
            }

            double budget = TargetFrameMs();
            double ema = FrameCost.SmoothedFocusedFrameMs;
            if (ema <= 0 || budget <= 0)
            {
                _autoLastStamp = 0;
                _autoWhy = "waiting for a frame time to steer on";
                return _autoScale;
            }

            double dt = AutoElapsedSeconds();

            if (_autoHoldLeft > 0)
            {
                _autoHoldLeft -= dt;
                if (_autoHoldLeft <= 0)
                {
                    _autoHoldLeft = 0;
                    JudgeLastStep(ema, ceiling);
                }
                return _autoScale;
            }
            if (_autoBackoffLeft > 0)
            {
                _autoBackoffLeft = Math.Max(0, _autoBackoffLeft - dt);
                _autoWhy = $"at {_autoScale:0.00}, standing down for {_autoBackoffLeft:0}s - the last step down bought nothing, "
                         + "so this machine is bound by something a smaller buffer cannot help";
                return _autoScale;
            }

            if (ema > budget * AutoOverBudget)
            {
                _autoUnderSeconds = 0;
                _autoOverSeconds += dt;
                if (_autoOverSeconds >= AutoOverSeconds && _autoScale > AutoScaleFloor + 0.001f)
                {
                    _autoProbeBeforeMs = ema;
                    _autoProbePending = true;
                    _autoScale = Math.Max(AutoScaleFloor, _autoScale - AutoScaleStep);
                    _autoOverSeconds = 0;
                    _autoHoldLeft = AutoHoldSeconds;
                    _autoStepsDown++;
                }
            }
            else if (ema <= budget * AutoUnderBudget)
            {
                _autoOverSeconds = 0;
                _autoUnderSeconds += dt;
                if (_autoUnderSeconds >= AutoUnderSeconds && _autoScale < ceiling - 0.001f)
                {
                    _autoScale = Math.Min(ceiling, _autoScale + AutoScaleStep);
                    _autoUnderSeconds = 0;
                    _autoHoldLeft = AutoHoldSeconds;
                    _autoProbePending = false;      // giving quality back is not an experiment
                    _autoStepsUp++;
                }
            }
            else
            {
                // In the deadband: neither missing the budget nor comfortably inside it. Let both
                // counters decay rather than clearing them, so a scene that flickers across the
                // line does not have to start its case from nothing each time.
                _autoOverSeconds = Math.Max(0, _autoOverSeconds - dt);
                _autoUnderSeconds = Math.Max(0, _autoUnderSeconds - dt);
            }

            _autoWhy = $"at {_autoScale:0.00} of {ceiling:0.00} - frame {ema:0.0} ms against a {budget:0.0} ms budget";
            return _autoScale;
        }

        /// <summary>Did the last step down actually buy anything? A machine whose bound is its CPU
        /// gets no frames back from a smaller buffer, and would otherwise be walked to the floor for
        /// a softer picture and nothing else.</summary>
        private void JudgeLastStep(double ema, float ceiling)
        {
            if (!_autoProbePending)
                return;
            _autoProbePending = false;
            if (ema > _autoProbeBeforeMs * 0.98)
            {
                _autoScale = Math.Min(ceiling, _autoScale + AutoScaleStep);
                _autoBackoffLeft = AutoBackoffSeconds;
                _autoStepsUndone++;
                _autoOverSeconds = _autoUnderSeconds = 0;
            }
        }

        /// <summary>A budget pretended for testing, in milliseconds, or zero for the real one.
        ///
        /// <para>
        /// A feedback controller that has never been observed reacting is a guess with extra steps,
        /// and this one cannot be observed on a machine that holds 60 fps in every scene: there is
        /// no overrun for it to answer. Telling it the budget is 8 ms makes an ordinary capped
        /// frame an overrun, which exercises the real path - the counters, the step, the hold, and
        /// the judgement that gives a useless step back - rather than a test-only copy of it.
        /// Left reachable because it proves the feature works on somebody else's machine too.
        /// </para></summary>
        internal static double BudgetOverrideMs;

        /// <summary>The frame the game is aiming for. Read from the game rather than assumed,
        /// because a player who has uncapped the frame rate or set a different target is not asking
        /// this to chase 60.</summary>
        private static double TargetFrameMs()
        {
            if (BudgetOverrideMs > 0.1)
                return BudgetOverrideMs;
            try
            {
                var game = Game1.game1;
                if (game != null && game.IsFixedTimeStep && game.TargetElapsedTime.TotalMilliseconds > 0.1)
                    return game.TargetElapsedTime.TotalMilliseconds;
            }
            catch { }
            return 1000.0 / 60.0;
        }

        /// <summary>The auto scale's line in the report: what it settled on, and why.</summary>
        internal string DescribeAutoScale()
            => $"automatic render scale: {_autoWhy}"
             + (BudgetOverrideMs > 0.1
                 ? $"\n  NOTE: the frame budget is being pretended at {BudgetOverrideMs:0.0} ms for a test"
                 : "")
             + $"\n  steps down {_autoStepsDown}, steps back up {_autoStepsUp}, steps given back as useless {_autoStepsUndone}"
             + (_autoStepsUndone > 0
                 ? "\n  a step given back means the smaller buffer did not shorten the frame, so the bound is"
                   + " somewhere a render scale cannot reach - the CPU, or another mod."
                 : "");

        /// <summary>What the on-screen readout shows next to the frame time, or null when the
        /// controller is not doing anything worth a line.</summary>
        internal string? AutoScaleHudLine(ModConfig config)
            => config.RenderScaleAuto ? $"{_autoScale:0.00}" : null;
    }
}
