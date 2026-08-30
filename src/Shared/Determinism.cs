using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Author tool: pins every input that keeps moving while the player stands still, so the
    /// same spot renders the same bytes twice. Off unless typed (<c>radiance_freeze</c>).
    ///
    /// <para>
    /// Nothing here is about the game being random — it isn't. The frame moves because the
    /// pipeline is TEMPORAL in two separate ways, and both have to stop before two captures can
    /// be compared byte for byte:
    /// </para>
    /// <list type="number">
    ///   <item>animation phase — every noise/ripple/drift term is driven by a clock (see
    ///   <see cref="Ticks"/> and <see cref="Seconds"/>), so a capture one frame later is a
    ///   different picture of the same scene;</item>
    ///   <item>eased state — presence fades, the fog/ray crossfades and the metered exposure
    ///   all converge over ~0.5-1 s, so the same scene renders differently depending on how the
    ///   player ARRIVED there (walked in, warped in, toggled the effect). <see cref="Settle"/>
    ///   lands those on their target in one frame instead.</item>
    /// </list>
    ///
    /// <para>
    /// Auto-exposure is pinned rather than settled: it meters the frame it is grading, so its
    /// "target" is a feedback loop with no fixed point to settle onto. See
    /// <c>UpdateAutoExposure</c>.
    /// </para>
    ///
    /// What this deliberately does NOT freeze: game logic. Nothing here writes to the game —
    /// characters keep walking, water keeps its own vanilla animation, the clock keeps running.
    /// A capture is reproducible for as long as the SCENE is unchanged, which is what a
    /// before/after comparison of a refactor needs.
    /// </summary>
    internal static class Determinism
    {
        /// <summary>True while the render clock and every eased amount are pinned.</summary>
        internal static bool Frozen;

        /// <summary>
        /// The tick every freeze pins to. A CONSTANT, not "the tick freeze was typed at": two
        /// runs of the same scene reach the freeze at different tick counts (loading a save is
        /// not a fixed number of frames), and pinning the live tick gave every animated term a
        /// different phase per run — the first tour compared 34% of the frame "different" with
        /// every input buffer identical, purely from ripple/cloud/fog phase. One shared constant
        /// makes the frozen picture the same picture in every run.
        /// </summary>
        internal const int CanonicalTick = 108000;   // 30 min at 60 fps, mid wrap — arbitrary but fixed

        /// <summary>Tick count every animated stage reads while frozen (always <see cref="CanonicalTick"/>).</summary>
        internal static int PinnedTicks;

        /// <summary>The tick count the render stages animate from.</summary>
        internal static int Ticks => Frozen ? PinnedTicks : Game1.ticks;

        /// <summary>Elapsed seconds for stages that animate off the game timer rather than ticks.
        /// Derived from <see cref="PinnedTicks"/> while frozen so both clocks agree.</summary>
        internal static double Seconds => Frozen
            ? PinnedTicks / 60.0
            : (Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0);

        /// <summary>Value an eased amount should take this frame: the target itself while frozen,
        /// otherwise whatever the ease computed. Keeps the easing arithmetic in one place at each
        /// call site instead of scattering `if (Frozen)` through the stages.</summary>
        internal static float Settle(float eased, float target) => Frozen ? target : eased;

        /// <summary>Vector form of <see cref="Settle(float, float)"/>. Exists because the eased
        /// colours (metered exposure, window daylight, light shafts) are Vector3, and without an
        /// overload they were simply left out of freeze - which is what happened.</summary>
        internal static Microsoft.Xna.Framework.Vector3 Settle(
            Microsoft.Xna.Framework.Vector3 eased, Microsoft.Xna.Framework.Vector3 target)
            => Frozen ? target : eased;

        /// <summary>As above for a direction (the light shafts ease theirs).</summary>
        internal static Microsoft.Xna.Framework.Vector2 Settle(
            Microsoft.Xna.Framework.Vector2 eased, Microsoft.Xna.Framework.Vector2 target)
            => Frozen ? target : eased;

        /// <summary>
        /// Hold the GAME clock still while frozen. Called every tick; does nothing unless frozen.
        ///
        /// <para>This is the one place the author tool writes to the game, and it is here because
        /// without it two captures of the same spot are not comparable and the harness cannot
        /// certify anything. Ten in-game minutes pass every seven real seconds, so a capture and
        /// its recheck land in different ten-minute blocks and the verifier refuses the pair -
        /// which it should, because the light really did change between them.</para>
        ///
        /// <para>Re-setting <c>timeOfDay</c> before each capture is NOT enough, and that was tried:
        /// setting the time does not reset <c>gameTimeInterval</c>, so a block that was nearly
        /// over rolls straight past the value just written. It also leaves the sub-block position
        /// itself unpinned, and that position is an input to the light nothing records - captures
        /// whose metadata matched on all 164 fields still differed on every lightmap cell.</para>
        ///
        /// <para>Pinning the interval at 0 fixes both: the clock cannot roll over, and every
        /// capture is taken at the same point within the block.</para>
        /// </summary>
        internal static void HoldGameClock()
        {
            if (Frozen)
                Game1.gameTimeInterval = 0;
        }

        /// <summary>Hold every farmer's eyes open while frozen. Farmer.Update blinks them on a random
        /// timer of the game's own, and two dumps of one frozen frame differed by the 42 pixels of a
        /// blink. Written at draw time, so the game's own timer keeps running and nothing is lost when
        /// the clock thaws.</summary>
        internal static void HoldFarmerEyesOpenForDraw()
        {
            if (!Frozen)
                return;
            foreach (Farmer who in Game1.getAllFarmers())
            {
                who.currentEyes = 0;
                who.blinkTimer = 0;
            }
        }

        /// <summary>
        /// Hold the game's DRAW-TIME clock still while frozen. Called at the start of every drawn
        /// frame; does nothing unless frozen.
        ///
        /// <para>The game animates some things from <see cref="Game1.currentGameTime"/> rather than
        /// from a tick counter: a placed campfire picks its flame frame from TotalGameTime, and so
        /// do a few other lit objects. That clock is not ours, so a frozen capture of a beach with a
        /// campfire on it differed from the next by the flame (309 pixels, the harness gate's last
        /// hole on 2026-08-26). Replacing the value for the rest of this tick's draw pins it at the
        /// same canonical second every freeze pins to; the game's own update writes a fresh one
        /// before the next tick runs, so nothing that measures elapsed time in update sees this.</para>
        /// </summary>
        internal static void HoldGameTimeForDraw()
        {
            if (!Frozen || Game1.currentGameTime == null)
                return;
            var pinned = System.TimeSpan.FromTicks(PinnedTicks * (System.TimeSpan.TicksPerSecond / 60));
            Game1.currentGameTime = new Microsoft.Xna.Framework.GameTime(pinned, Game1.currentGameTime.ElapsedGameTime);
        }

        /// <summary>Pin the clock at <see cref="CanonicalTick"/>. Returns the pinned tick.</summary>
        internal static int Freeze()
        {
            PinnedTicks = CanonicalTick;
            Frozen = true;
            return PinnedTicks;
        }

        internal static void Thaw() => Frozen = false;
    }
}
