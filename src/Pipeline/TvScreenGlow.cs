using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The light of a television while it is on.
    ///
    /// <para>The game already lights a TV: while a show plays it adds a light named for the TV's
    /// tile with "_Screen" on the end, and takes it away the moment the show ends. The mod read
    /// that light like any lamp, so the screen threw a warm, candle-coloured pool, the pool and
    /// the shadows it gave the people in front of it switched on and off in one frame, and
    /// nothing about it said "screen". Asked for on Nexus: a TV that lights the room, and people
    /// near it who cast shadows from it.</para>
    ///
    /// <para>So the screen's light is taken over here. Each TV that is on is remembered by its
    /// light's name, with where it stood, so it can fade in when the show starts and fade out
    /// after the game has already removed it. It is the colour of a screen, cool and a little
    /// blue, and it flickers gently the way a picture does. The lamp list and the character
    /// shadows both read it from here instead of from the game's light, so the pool and the
    /// shadows fade together. <see cref="Dial"/> 0 hands the TV back to the game's own light,
    /// exactly as before.</para>
    ///
    /// <para>Kept per screen, because a split screen is two players with two sets of lights, and
    /// forgotten on a change of room so a pool never fades across a doorway.</para>
    /// </summary>
    internal static class TvScreenGlow
    {
        internal sealed class Glow
        {
            public string Key = "";
            public Vector2 World;
            public float Radius;
            public float Ease;
            public int SeenTick;
            public float Phase;
        }

        private sealed class ScreenGlows
        {
            public readonly Dictionary<string, Glow> Glows = [];
            public GameLocation? Location;
            public int SteppedTick = int.MinValue;
        }

        private static readonly Dictionary<int, ScreenGlows> _byScreen = [];
        private static readonly List<string> _gone = [];

        /// <summary>How much the TV's light is a screen's light, 0..1 (see ModConfig.TvScreenGlow).</summary>
        internal static float Dial { get; private set; }

        /// <summary>The colour of a lit screen: cool and a little blue, against the warm lamps.</summary>
        internal static readonly Vector3 ScreenColour = new(0.50f, 0.70f, 1.30f);

        /// <summary>Where the pool is centred, from the game's light: the light sits on the screen,
        /// and a screen lights the floor in front of it.</summary>
        internal static readonly Vector2 PoolOffset = new(0f, 32f);

        private const float FadeInSeconds = 0.35f;
        private const float FadeOutSeconds = 0.6f;

        /// <summary>The game's light for a TV's screen: its name ends in "_Screen" and it uses the
        /// window-light texture.</summary>
        internal static bool IsScreenLight(string key, LightSource light)
            => light.textureIndex.Value == 2 && key.EndsWith("_Screen", StringComparison.Ordinal);

        /// <summary>Whether this screen's TV lights are ours this frame: the dial is up and the
        /// pipeline has stepped them within the last two ticks. False hands them back to the game's
        /// own light, which is also what happens when the lighting pass is not running.</summary>
        internal static bool Live
            => Dial > 0.001f && _byScreen.TryGetValue(Context.ScreenId, out ScreenGlows? screen)
               && Game1.ticks - screen.SteppedTick <= 2;

        internal static IReadOnlyCollection<Glow> OnThisScreen
            => _byScreen.TryGetValue(Context.ScreenId, out ScreenGlows? screen) ? screen.Glows.Values : Array.Empty<Glow>();

        /// <summary>Once a frame per screen, before the lights are gathered: note every TV that is
        /// on and ease each one toward on or off.</summary>
        internal static void Step(ModConfig config, GameLocation? location)
        {
            Dial = MathHelper.Clamp(config.TvScreenGlow, 0f, 1f);
            int screenId = Context.ScreenId;
            if (!_byScreen.TryGetValue(screenId, out ScreenGlows? screen))
            {
                screen = new ScreenGlows();
                _byScreen[screenId] = screen;
                LiveScreens.ForgetDeparted(_byScreen);
            }
            if (!ReferenceEquals(screen.Location, location))
            {
                screen.Glows.Clear();
                screen.Location = location;
            }
            screen.SteppedTick = Game1.ticks;
            int tick = Game1.ticks;
            var lights = Game1.currentLightSources;
            if (lights != null && Dial > 0.001f)
            {
                foreach (var pair in lights)
                {
                    if (!IsScreenLight(pair.Key, pair.Value))
                        continue;
                    if (!screen.Glows.TryGetValue(pair.Key, out Glow? glow))
                    {
                        glow = new Glow { Key = pair.Key };
                        Vector2 at = pair.Value.position.Value;
                        glow.Phase = (at.X * 0.011f + at.Y * 0.017f) % MathHelper.TwoPi;
                        screen.Glows[pair.Key] = glow;
                    }
                    glow.World = pair.Value.position.Value;
                    glow.Radius = pair.Value.radius.Value;
                    glow.SeenTick = tick;
                }
            }
            float seconds = Math.Min(0.1f, (float)(Game1.currentGameTime?.ElapsedGameTime.TotalSeconds ?? 0.0));
            _gone.Clear();
            foreach (var pair in screen.Glows)
            {
                Glow glow = pair.Value;
                bool on = Dial > 0.001f && tick - glow.SeenTick <= 1;
                if (Determinism.Frozen)
                    glow.Ease = on ? 1f : 0f;
                else
                    glow.Ease = on ? Math.Min(1f, glow.Ease + seconds / FadeInSeconds) : Math.Max(0f, glow.Ease - seconds / FadeOutSeconds);
                if (!on && glow.Ease <= 0f)
                    _gone.Add(pair.Key);
            }
            foreach (string key in _gone)
                screen.Glows.Remove(key);
        }

        /// <summary>The picture's flicker: two slow waves out of step, a few per cent either way,
        /// scaled by the dial. Applied after the lamps are ranked, never before (see
        /// RenderPipeline.BuildLightList on why a flickering value must not choose the lights).</summary>
        internal static float Flicker(Glow glow)
        {
            double t = Determinism.Seconds;
            float wave = (float)(Math.Sin(t * 2.3 + glow.Phase) * 0.07 + Math.Sin(t * 5.7 + glow.Phase * 1.7) * 0.05);
            return 1f + Dial * wave;
        }

        /// <summary>One line for radiance_report.</summary>
        internal static string Describe()
        {
            int on = 0, fading = 0;
            foreach (Glow glow in OnThisScreen)
            {
                if (Game1.ticks - glow.SeenTick <= 1) on++;
                else fading++;
            }
            return $"tv screens: dial {Dial:0.00}, {(Live ? "ours" : "the game's light")}, {on} on, {fading} fading out";
        }
    }
}
