using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Spells of strong wind through the day: two or three of them, each rising over twenty game
    /// minutes, blowing for a while and dying away again, laid on top of the wind everything
    /// already shares (the trees, the rain's slant, the snow, the blown leaves, the water's drift,
    /// and the cloud shadows' pace).
    /// </summary>
    /// <remarks>
    /// <para>The day's spells come from the day itself (days played and the farm's id), so every
    /// player in a co-op game and every screen of a split screen gets the same weather, and a
    /// reload brings the same afternoon back. Where the spell stands is read off the continuous
    /// game clock, so it rises a little every frame rather than stepping every ten minutes, and
    /// switching the feature on or off eases over a few seconds instead of snapping.</para>
    /// <para>Off, <see cref="Factor"/> is exactly 1 and <see cref="Amplify"/> hands back the wind it
    /// was given, so nothing that reads the wind changes for a player who never turns it on.
    /// Asked for by Charost on Nexus.</para>
    /// </remarks>
    internal static class WindySpells
    {
        /// <summary>Minutes a spell takes to rise to full, and to die away again.</summary>
        private const float RampMinutes = 20f;
        /// <summary>The wind a spell brings of its own, in screen pixels per second at full, so a
        /// spell blows on a day the game itself calls still.</summary>
        private const float OwnWindPixelsPerSecond = 70f;
        /// <summary>How long switching the feature takes to ease in or out, in seconds.</summary>
        private const float SwitchEaseSeconds = 3f;

        internal static bool Enabled;
        /// <summary>How many times the usual wind a spell blows at its height, 1.25 to 3.</summary>
        internal static float Strength = 2f;

        private static float _factor = 1f;
        private static int _steppedTick = -1;
        private static int _spellsForDay = -1;
        private static readonly (float Start, float End)[] _spells = new (float, float)[3];
        private static int _spellCount;

        /// <summary>The wind's multiplier right now: 1 when calm or switched off.</summary>
        internal static float Factor => _factor;

        /// <summary>A wind made stronger by the spell: the given wind times the factor, and the
        /// spell's own wind added in the direction it already blows (leftward, the game's usual
        /// drift, when it barely blows at all). Returns <paramref name="windPixelsPerSecond"/>
        /// unchanged when there is no spell.</summary>
        internal static float Amplify(float windPixelsPerSecond)
        {
            if (_factor == 1f)
                return windPixelsPerSecond;
            float direction = windPixelsPerSecond > 1f ? 1f : -1f;
            return windPixelsPerSecond * _factor + direction * OwnWindPixelsPerSecond * (_factor - 1f);
        }

        /// <summary>Step the spell once per game tick (the update event fires once per screen).</summary>
        internal static void Update(float seconds)
        {
            if (SharedTicks.Now == _steppedTick)
                return;
            _steppedTick = SharedTicks.Now;
            float target = Enabled ? 1f + (Math.Clamp(Strength, 1.25f, 3f) - 1f) * SpellAmountNow() : 1f;
            if (Determinism.Frozen)
            {
                _factor = target;
                return;
            }
            _factor += (target - _factor) * Math.Clamp(seconds / SwitchEaseSeconds, 0f, 1f);
            if (!Enabled && Math.Abs(_factor - 1f) < 0.001f)
                _factor = 1f;
        }

        /// <summary>How far into a spell the day is right now, 0 to 1.</summary>
        private static float SpellAmountNow()
        {
            int day = (int)Game1.stats.DaysPlayed;
            if (day != _spellsForDay)
                PlanTheDay(day);
            float now = GameClock.MinutesNow();
            float amount = 0f;
            for (int i = 0; i < _spellCount; i++)
            {
                (float start, float end) = _spells[i];
                float rising = MathHelper.Clamp((now - start) / RampMinutes, 0f, 1f);
                float falling = MathHelper.Clamp((end - now) / RampMinutes, 0f, 1f);
                amount = Math.Max(amount, MathHelper.SmoothStep(0f, 1f, Math.Min(rising, falling)));
            }
            return amount;
        }

        private static void PlanTheDay(int day)
        {
            _spellsForDay = day;
            var random = new Random(unchecked(day * 7919 + (int)(Game1.uniqueIDForThisGame % 1_000_003)));
            _spellCount = 2 + random.Next(2);
            for (int i = 0; i < _spellCount; i++)
            {
                // Between seven in the morning and ten at night, forty minutes to two hours each.
                float start = 420f + random.Next(900);
                _spells[i] = (start, start + 40f + random.Next(81));
            }
        }

        /// <summary>One line for radiance_report.</summary>
        internal static string Describe()
        {
            if (!Enabled)
                return "windy spells: off";
            string spells = "";
            for (int i = 0; i < _spellCount; i++)
                spells += $" {Clock(_spells[i].Start)}-{Clock(_spells[i].End)}";
            return $"windy spells: wind x{_factor:0.00} now, at most x{Strength:0.00}; today's spells:{spells}";
        }

        private static string Clock(float minutes) => $"{(int)minutes / 60:00}:{(int)minutes % 60:00}";
    }
}
