using Microsoft.Xna.Framework;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Continuous in-game clock. Game1.timeOfDay is HHMM advancing in 10-minute steps,
    /// so any visual computed from it directly JUMPS once per tick (fog tint, night
    /// warmth, shadow angles - the whole frame lurched five times an hour). The fraction
    /// of the current tick lives in Game1.gameTimeInterval; folding it in makes every
    /// time-driven curve glide instead.
    /// </summary>
    internal static class GameClock
    {
        /// <summary>The host screen's reading of the current ten minutes, left here for the others.
        ///
        /// <para>Split screen runs one Game1 per screen, and only the host's advances
        /// gameTimeInterval. Measured 13/9 with two screens in Town: the host read 5552 to 6784 ms
        /// into the tick while the farmhand screen read 0 throughout, so its clock stood still for
        /// ten game minutes and then jumped, up to 9.69 minutes behind the host's. Anything shared
        /// between screens and keyed on this clock was asked for two answers in turn. The object
        /// shadow bakes are shared, so every tree re-baked back and forth between two sun angles:
        /// the shadows flickered, and 14 re-bakes a frame went on while nobody moved.</para>
        ///
        /// <para>These statics are one copy for the whole process, unlike the game's own, which are
        /// swapped per screen. The host writes its fraction every time it reads the clock and a
        /// screen that is not the host, on the same ten minutes, reads the host's instead of its
        /// own. A farmhand playing over the network has no host in its process and keeps its own
        /// reading, as before.</para></summary>
        private static int _hostTimeOfDay = -1;
        private static float _hostFraction;

        /// <summary>This process's own last reading, for a farmhand over the network, which has no
        /// host in its process to read from. Only used to hold the clock still through a pause.</summary>
        private static int _ownTimeOfDay = -1;
        private static float _ownFraction;

        /// <summary>Minutes since midnight as a continuous float (e.g. 1855 -> 1135.42).
        ///
        /// <para>A NEGATIVE gameTimeInterval is a pause, not the start of the ten minutes. Pause in
        /// Multiplayer Revived stops time, in single player too, by writing -100 there every tick
        /// and putting the real value back when the pause ends. Clamped to zero, every menu opened
        /// and every horse mounted put this clock back to the start of the ten minutes and the sun
        /// with it, so the sun shafts jumped backwards and came forward again on closing (AlienFreek,
        /// Nexus, 18 Oct 2026, with a video). Through a pause the last real reading of the same ten
        /// minutes is held instead.</para></summary>
        public static float MinutesNow()
        {
            int t = Game1.timeOfDay;
            float mins = t / 100 * 60 + t % 100;
            float tickMs = Game1.realMilliSecondsPerGameTenMinutes
                + (Game1.currentLocation?.ExtraMillisecondsPerInGameMinute ?? 0) * 10f;
            if (tickMs < 1f) tickMs = 1f;
            int interval = Game1.gameTimeInterval;
            bool paused = interval < 0;
            float frac = MathHelper.Clamp(interval / tickMs, 0f, 1f);
            if (Game1.IsMasterGame)
            {
                if (paused && _hostTimeOfDay == t)
                    frac = _hostFraction;
                _hostTimeOfDay = t;
                _hostFraction = frac;
            }
            else if (_hostTimeOfDay == t)
            {
                frac = _hostFraction;
            }
            else
            {
                if (paused && _ownTimeOfDay == t)
                    frac = _ownFraction;
                _ownTimeOfDay = t;
                _ownFraction = frac;
            }
            return mins + frac * 10f;
        }

        /// <summary>0..1 ramp centred on an HHMM boundary, easing over ±<paramref name="halfWidthMinutes"/>
        /// game-minutes - the drop-in replacement for a hard `timeOfDay >= boundary` gate.</summary>
        public static float RampAt(int boundaryHhmm, float halfWidthMinutes = 10f)
        {
            float b = boundaryHhmm / 100 * 60 + boundaryHhmm % 100;
            return MathHelper.Clamp((MinutesNow() - b + halfWidthMinutes) / (2f * halfWidthMinutes), 0f, 1f);
        }
    }
}
