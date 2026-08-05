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
        /// <summary>Minutes since midnight as a continuous float (e.g. 1855 -> 1135.42).</summary>
        public static float MinutesNow()
        {
            int t = Game1.timeOfDay;
            float mins = t / 100 * 60 + t % 100;
            float tickMs = Game1.realMilliSecondsPerGameTenMinutes
                + (Game1.currentLocation?.ExtraMillisecondsPerInGameMinute ?? 0) * 10f;
            if (tickMs < 1f) tickMs = 1f;
            float frac = MathHelper.Clamp(Game1.gameTimeInterval / tickMs, 0f, 1f);
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
