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

        /// <summary>The tick count the render stages animate from.
        ///
        /// <para>SIXTY OF THESE IS A SECOND, and every caller spends it that way
        /// (<c>(Ticks % 360000) / 60f</c>). That holds because the game runs a fixed timestep, so
        /// its own frame counter and the wall clock are the same counter. An uncapper breaks the
        /// equality rather than the counter: UltraSmooth and its kind set
        /// <c>IsFixedTimeStep = false</c>, after which <see cref="Game1.ticks"/> counts FRAMES, and
        /// at 144 fps every ripple, cloud, flame and heat shimmer in this mod runs two and a half
        /// times too fast. The author of UltraSmooth found this and patches the property from
        /// outside to a stopwatch, which is a kindness this mod should not need: the contract is
        /// ours to keep, the patch is pinned to a name we are free to rename, and a player running
        /// any OTHER uncapper gets no such favour.</para>
        ///
        /// <para>ONCE THE CAP HAS COME OFF, THIS CLOCK NEVER GOES BACK. The first version handed
        /// over to elapsed time when the cap lifted and back to the frame counter when it returned,
        /// which jumps: the game's counter raced ahead the whole time the cap was off. That was
        /// written down as acceptable on the assumption a player toggles the mode rarely.
        /// UltraSmooth toggles it FOR them, dropping to the capped step for every cutscene and
        /// lifting it again on the way out, so the jump landed on every scene transition, which is
        /// exactly where a flicker was being reported. Now the count is ours from the first moment
        /// the cap lifts and stays ours, advancing at sixty a second of real time in either mode,
        /// so neither direction moves anything.</para>
        ///
        /// <para>Until the cap has ever lifted this is <see cref="Game1.ticks"/> and nothing else.
        /// That is every ordinary session and every harness run, and the baselines depend on it:
        /// the game's counter and the wall clock have different origins, so deriving from time
        /// from the start would shift the phase of every animated term by a constant.</para>
        /// </summary>
        internal static int Ticks => Frozen ? PinnedTicks
            : _clockIsOurs ? (int)_ourTicks
            : Game1.ticks;

        /// <summary>True once an uncapper has lifted the frame cap at least once this session.
        /// One way: see the note above about jumping back.</summary>
        private static bool _clockIsOurs;
        /// <summary>Our own count of sixtieths, seeded from the game's at the moment of handover.</summary>
        private static double _ourTicks;
        private static double _lastSecondsSeen;
        /// <summary>Which of the game's ticks this last advanced on. The update event fires once
        /// per SCREEN, and without this a split screen would run the world at twice the speed.</summary>
        private static int _advancedOnTick = -1;

        /// <summary>Advance our own clock, and notice an uncapper lifting the cap for the first
        /// time. Called every update tick.</summary>
        internal static void FollowTheGamesTimeStep()
        {
            if (_advancedOnTick == Game1.ticks)
                return;
            _advancedOnTick = Game1.ticks;
            double secondsNow = Seconds;
            bool cappedNow = Game1.game1?.IsFixedTimeStep ?? true;
            // Counted on every EDGE, not once. The first version only counted the handover, so it
            // could read 0 or 1 for the rest of the session and could not answer the question it
            // was written for: how often does the uncapper change its mind. It turns out to be
            // every cutscene, which is what made the old jumping handover a flicker.
            if (cappedNow != _wasCapped)
            {
                _wasCapped = cappedNow;
                CapChanges++;
            }
            if (!_clockIsOurs)
            {
                if (cappedNow)
                {
                    _lastSecondsSeen = secondsNow;
                    return;
                }
                // Seeded where the two clocks still agree, so lifting the cap moves nothing.
                _clockIsOurs = true;
                _ourTicks = Game1.ticks;
            }
            double elapsed = secondsNow - _lastSecondsSeen;
            _lastSecondsSeen = secondsNow;
            // A load, a long pause or an alt-tab must not fast-forward the whole world when the
            // game comes back; a second is far longer than any real frame and far shorter than
            // any stall worth replaying.
            if (elapsed > 0.0 && elapsed < 1.0)
                _ourTicks += elapsed * 60.0;
        }

        /// <summary>How many times the frame cap has gone on or off this session. Reported by
        /// radiance_report: a zero here while somebody insists their frames are unlocked means the
        /// uncapper is not doing what either of us thinks it is, and nothing else in the report
        /// about the clock can be read until that is settled. A number that climbs while somebody
        /// plays is the uncapper toggling the mode for them, which UltraSmooth does around every
        /// cutscene.</summary>
        internal static int CapChanges;
        private static bool _wasCapped = true;

        /// <summary>Whether the frame cap is off right now, for the report.</summary>
        internal static bool CapIsOff => !(Game1.game1?.IsFixedTimeStep ?? true);

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
