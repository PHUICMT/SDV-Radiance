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
