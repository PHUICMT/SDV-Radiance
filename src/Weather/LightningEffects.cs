using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The scene's response to a lightning strike. The game's own strike is a flat white overlay
    /// (<c>Game1.flashAlpha</c>, minus 0.1 per frame) and a thunder sound; the world itself does
    /// not react. This adds the reaction, in three parts that all key off the game's own flash:
    ///
    /// <list type="number">
    ///   <item>a shadow key - for a blink every directional shadow leans toward one random
    ///   azimuth at full strength, as if the bolt were the sun;</item>
    ///   <item>a light burst - the mod's own darkening (indoor ambient, flood exposure) lifts
    ///   toward bright by the game's own flash value, so our light and the vanilla overlay decay
    ///   in lockstep with no second timeline to tune;</item>
    ///   <item>an afterglow - a ~200 ms warm luminance lift after the flash ends, the retina
    ///   holding the image, without any actual frame history.</item>
    /// </list>
    ///
    /// <para>
    /// Everything polls <c>Game1.flashAlpha</c> for a rising edge instead of patching the strike
    /// event: it needs no Harmony, and it responds to every white flash the player actually sees
    /// - big strikes, small delayed ones, and event flashes - which is correct, because a scene
    /// that stays dark through a flash the player watched is the bug, whatever fired the flash.
    /// </para>
    ///
    /// <para>
    /// The vanilla accessibility option <c>screenFlash</c> hides the game's white overlay but the
    /// decay still runs. Honoured here the same way: with it off, the afterglow (a visible
    /// strobe) is skipped entirely and the light burst runs at 40% - scene lighting is not a
    /// strobe, but the player who turned flashes off asked for less, not different.
    /// </para>
    /// </summary>
    internal static class LightningEffects
    {
        private const float StrikeKeySeconds = 0.15f;
        private const float AfterglowSeconds = 0.20f;
        /// <summary>How much the flood exposure lifts at full burst.</summary>
        internal const float FloodExposureLift = 0.35f;
        private const float AfterglowPeakAlpha = 0.07f;

        private static float _lastFlashAlpha;
        /// <summary>The tick this last advanced on. See the note at the top of Update: the
        /// update event is raised per screen, and every value here is on a clock.</summary>
        private static int _updatedTick = -1;

        /// <summary>1 on the strike frame, easing to 0 over ~150 ms: the shadow key envelope.</summary>
        internal static float StrikeKey01;
        /// <summary>The azimuth the key leans every shadow toward, in ComputeSun's own radians
        /// convention. Shared static on purpose: both split-screen halves must see one bolt.</summary>
        internal static float StrikeLean;
        /// <summary>The retina term, easing to 0 over ~200 ms.</summary>
        internal static float Afterglow01;
        /// <summary>The game's own flash value gated by our config: the light-burst amount this
        /// frame. Riding flashAlpha directly keeps our burst and the vanilla overlay on the same
        /// decay curve.</summary>
        internal static float Burst01;

        /// <summary>
        /// Lightning seen through the windows of a room during a storm, 0 to 1, for the glass and
        /// the beam: a strike's strokes, each a spike that falls away within a few frames.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The game flashes the screen and plays its thunder only outdoors, so inside a storm was
        /// just a darker room (asked for by the author on 16 Oct 2026: "the room should be dark,
        /// and the lightning come in through the windows"). This flashes on the game's own strikes,
        /// which still fire while the player is indoors, and on distant ones of its own every few
        /// seconds, since the game's come at most once every ten minutes of game time.
        /// </para>
        /// <para>
        /// Shaped as real lightning is (research of 16 Oct 2026, in docs/local/lighting): a flash
        /// is two to four return strokes 40 to 100 ms apart, the first the brightest, a quarter of
        /// them a single one, and some hold lit for a moment after with a slow wobble. A near
        /// strike is a bolt: hard, parallel light that throws the panes sharply onto the floor,
        /// cold white. A far one is lightning in the cloud: no beam, the glass glows and the room
        /// lifts, dimmer and warmer. Only the glass and the beam flicker stroke by stroke; the room
        /// as a whole rises and falls once (<see cref="WindowRoomFlash01"/>), which keeps the flicker
        /// out of the large area the photosensitivity guidelines are about.
        /// </para>
        /// </remarks>
        internal static float WindowFlash01;
        /// <summary>The room's share of the strike: one smooth rise and fall, no flicker.</summary>
        internal static float WindowRoomFlash01;
        /// <summary>After a strike the room looks darker than before for a moment, the eye still
        /// dazzled; 1 at the end of a strike, easing back to 0.</summary>
        internal static float WindowAfterDip01;
        /// <summary>Whether the strike now is a near bolt (a sharp beam) or lightning in the cloud.</summary>
        internal static bool WindowStrikeNear;
        /// <summary>The colour of this strike's light: cold white near, warmer further off.</summary>
        internal static Vector3 WindowFlashColour = new(0.85f, 0.9f, 1.05f);
        /// <summary>Where this strike's light falls: its own slant and reach, a new one each time,
        /// since the bolt is somewhere else in the sky.</summary>
        internal static float WindowStrikeLean, WindowStrikeReachTiles = 3f;

        private static readonly Vector3 NearStrikeColour = new(0.85f, 0.9f, 1.05f);
        private static readonly Vector3 FarStrikeColour = new(1f, 0.93f, 0.84f);
        /// <summary>How fast one stroke falls away, in ticks (e-folding): about 40 ms.</summary>
        private const float StrokeFadeTicks = 2.5f;
        private const float RoomFallSeconds = 0.6f;
        private const float AfterDipSeconds = 1.5f;
        private const float WindowFlashGapMinSeconds = 4f;
        private const float WindowFlashGapMaxSeconds = 14f;
        private const int MostStrokes = 4;
        private static readonly int[] _strokeTicks = new int[MostStrokes];
        private static readonly float[] _strokePeaks = new float[MostStrokes];
        private static int _strokeCount;
        private static int _holdFromTick = -1, _holdUntilTick = -1, _strikeEndTick = -1;
        private static float _holdLevel, _strikePeak;
        private static bool _strikeLive;
        private static int _nextWindowFlashTick = -1;
        private static bool _strikeHeard, _strikeHeardNear;
        private static Farm? _strikeFarm;

        /// <summary>How strongly the windows cast shadows from the strike now: a near bolt as hard
        /// as a lamp beside the caster, lightning in the cloud only faintly, since its light comes
        /// from the whole sky.</summary>
        internal static float WindowShadowStrength => WindowFlash01 * (WindowStrikeNear ? 1f : 0.35f);

        /// <summary>Per screen, the room and the middle of each labelled window's glass in it, in
        /// world pixels: where lightning through the windows casts shadows from.</summary>
        private static readonly Dictionary<int, (GameLocation? Location, List<Vector2> Centres)> _windowCentresByScreen = [];

        /// <summary>The windows of this room the lightning casts from, or none when they belong to
        /// another room.</summary>
        internal static IReadOnlyList<Vector2> WindowCentresOnThisScreen(GameLocation location)
            => _windowCentresByScreen.TryGetValue(StardewModdingAPI.Context.ScreenId, out var held) && ReferenceEquals(held.Location, location)
                ? held.Centres : Array.Empty<Vector2>();

        /// <summary>Note the windows of a room from its painted glass tiles: tiles that touch are one
        /// window, cast from the middle of its glass.</summary>
        internal static void SetWindowCentres(GameLocation? location, IReadOnlyCollection<Point> glassTiles)
        {
            int screenId = StardewModdingAPI.Context.ScreenId;
            if (!_windowCentresByScreen.TryGetValue(screenId, out var held))
            {
                held = (null, []);
                LiveScreens.ForgetDeparted(_windowCentresByScreen);
            }
            held.Centres.Clear();
            var left = new HashSet<Point>(glassTiles);
            var group = new Stack<Point>();
            while (left.Count > 0)
            {
                Point first = default;
                foreach (Point any in left) { first = any; break; }
                left.Remove(first);
                group.Push(first);
                Vector2 sum = Vector2.Zero;
                int count = 0;
                while (group.Count > 0)
                {
                    Point tile = group.Pop();
                    sum += new Vector2(tile.X, tile.Y);
                    count++;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (left.Remove(new Point(tile.X + dx, tile.Y + dy)))
                                group.Push(new Point(tile.X + dx, tile.Y + dy));
                }
                held.Centres.Add((sum / count + new Vector2(0.5f)) * 64f);
            }
            _windowCentresByScreen[screenId] = (location, held.Centres);
        }

        /// <summary>A near strike is lighting the room now: the window beam takes its slant and
        /// reach instead of the sun's.</summary>
        internal static bool WindowStrikeShapesBeam(out float lean, out float reachTiles)
        {
            lean = WindowStrikeLean;
            reachTiles = WindowStrikeReachTiles;
            return _strikeLive && WindowStrikeNear;
        }

        /// <summary>Advance the envelopes; called once per tick from ModEntry. Frozen means
        /// frozen: while the harness is comparing captures nothing here may move.</summary>
        internal static void Update(ModConfig config)
        {
            // Split screen raises the update event once per SCREEN, so this handler runs two to
            // four times a tick. Everything below advances a clock, and a clock advanced once per
            // screen runs that many times fast: with two players the afterglow was over in half
            // the time it is written to last, and the shadow flick with it. The two neighbours
            // in this same handler, the wind and the wetness, each keep a tick stamp for exactly
            // this reason; this one claimed in a comment that it ran once per tick and did not.
            if (SharedTicks.Now == _updatedTick)
                return;
            _updatedTick = SharedTicks.Now;

            float flash = Game1.flashAlpha;
            bool rising = flash > _lastFlashAlpha + 0.05f;
            _lastFlashAlpha = flash;
            if (Determinism.Frozen)
                return;
            UpdateWindowFlash(config);

            const float dt = 1f / 60f;
            bool wanted = config.Enabled && config.LightningEffectsEnabled;
            bool visibleFlashesAllowed = Game1.options?.screenFlash ?? true;
            // The game raises flashAlpha for more than lightning: a warp totem, the return
            // sceptre, a frog or a gem out of a rock, the casino's card game, a firework. Read as
            // a strike, each of them kicked every shadow on the screen to one side and left an
            // afterglow, on a sunny afternoon. Only a storm here makes a rising flash a strike,
            // the same gate the visible bolt already asks. The burst below rides the game's own
            // overlay and stays with it: the screen really is flashing, whatever for.
            bool stormHere = Game1.currentLocation is { } here && Game1.IsLightningHere(here);

            if (rising && wanted && stormHere)
            {
                // Seeded from the tick so a strike picks one lean and every consumer this frame
                // sees the same bolt, both split-screen halves included. The stamp above is what
                // makes that true; it used to be asserted here and nothing enforced it.
                var strikeRandom = new Random(unchecked(SharedTicks.Now * 747796405));
                float boltAcross01 = 0.12f + 0.76f * (float)strikeRandom.NextDouble();
                // The shadows lean AWAY from the bolt, so the flash and the shadow key tell one
                // story: a bolt on the right of the screen throws every shadow to the left.
                StrikeLean = (0.35f + 0.75f * (float)strikeRandom.NextDouble())
                    * (boltAcross01 > 0.5f ? -1f : 1f);
                StrikeKey01 = Math.Min(1f, flash);
                Afterglow01 = Math.Min(1f, flash);
                MaybeDrawBolt(config, strikeRandom, boltAcross01);
            }

            float decayRate = wanted ? 1f : 3f;   // a toggle mid-strike eases out fast, never pops
            StrikeKey01 = Math.Max(0f, StrikeKey01 - dt / StrikeKeySeconds * decayRate);
            Afterglow01 = Math.Max(0f, Afterglow01 - dt / AfterglowSeconds * decayRate);
            Burst01 = wanted
                ? Math.Min(1f, flash) * (visibleFlashesAllowed ? 1f : 0.4f)
                : 0f;
        }

        /// <summary>Advance the window lightning: listen for the game's strikes, keep the distant
        /// ones coming while there is a storm outside this room's windows, and run each strike's
        /// strokes, the room's single rise and fall, and the dazzle after.</summary>
        private static void UpdateWindowFlash(ModConfig config)
        {
            const float dt = 1f / 60f;
            // Only with a world loaded: on the title screen there is no farm, and Game1.getFarm()
            // throws for it, every tick, taking the rest of the tick's update with it (426 errors in
            // fifteen seconds on the title screen, 16 Oct 2026).
            bool wanted = config.Enabled && config.LightningEffectsEnabled && config.LightningThroughWindows
                && StardewModdingAPI.Context.IsWorldReady;
            // Switched off, nothing here runs but the fade of a strike already under way.
            if (wanted)
            {
                // The farm carries the game's strike event, and polls it wherever the player is.
                // Asked by name, which answers null rather than throwing when there is none.
                Farm? farm = Game1.getLocationFromName("Farm") as Farm;
                if (!ReferenceEquals(farm, _strikeFarm))
                {
                    _strikeFarm?.lightningStrikeEvent.onEvent -= HeardStrike;
                    farm?.lightningStrikeEvent.onEvent += HeardStrike;
                    _strikeFarm = farm;
                }
            }
            else if (!_strikeLive && WindowRoomFlash01 <= 0f && WindowAfterDip01 <= 0f)
            {
                _nextWindowFlashTick = -1;
                _strikeHeard = false;
                WindowFlash01 = 0f;
                return;
            }

            GameLocation? here = Game1.currentLocation;
            bool stormOutside = wanted
                && here is { IsOutdoors: false } && Game1.IsLightningHere(here) && FloodLightmap.IsWindowedInterior(here);
            int now = SharedTicks.Now;
            if (!stormOutside)
            {
                _nextWindowFlashTick = -1;
                _strikeHeard = false;
            }
            else
            {
                // Seeded from the tick, like the strike's lean, so every screen sees the same storm.
                var stormRandom = new Random(unchecked(now * 1103515245 + 12345));
                if (_nextWindowFlashTick < 0)
                    _nextWindowFlashTick = now + GapTicks(stormRandom);
                if (!_strikeLive && (_strikeHeard || now >= _nextWindowFlashTick))
                {
                    // The game's own big strikes are near bolts; of the distant ones, about a third.
                    bool near = _strikeHeard ? _strikeHeardNear : stormRandom.NextDouble() < 0.35;
                    StartStrike(now, near, stormRandom);
                    _nextWindowFlashTick = now + GapTicks(stormRandom);
                    _strikeHeard = false;
                }
            }

            float glass = 0f;
            if (_strikeLive)
            {
                for (int stroke = 0; stroke < _strokeCount; stroke++)
                    if (now >= _strokeTicks[stroke])
                        glass = Math.Max(glass, _strokePeaks[stroke] * MathF.Exp(-(now - _strokeTicks[stroke]) / StrokeFadeTicks));
                if (_holdFromTick >= 0 && now >= _holdFromTick)
                {
                    // The channel held lit: a slow wobble, then a quick fall when it lets go.
                    float wobble = 0.85f + 0.15f * MathF.Sin(now * 0.9f);
                    float held = now < _holdUntilTick ? _holdLevel * wobble : _holdLevel * MathF.Exp(-(now - _holdUntilTick) / StrokeFadeTicks);
                    glass = Math.Max(glass, held);
                }
                if (now >= _strikeEndTick)
                {
                    _strikeLive = false;
                    WindowAfterDip01 = Math.Max(WindowAfterDip01, _strikePeak);
                }
            }
            WindowFlash01 = glass;

            // The room: up within a few frames of the first stroke, held through the strike without
            // following its flicker, then down over a little over half a second.
            float roomTarget = _strikeLive ? _strikePeak * (WindowStrikeNear ? 1f : 0.6f) : 0f;
            WindowRoomFlash01 = roomTarget > WindowRoomFlash01
                ? Math.Min(roomTarget, WindowRoomFlash01 + 0.34f)
                : Math.Max(roomTarget, WindowRoomFlash01 - dt / RoomFallSeconds);
            if (!_strikeLive)
                WindowAfterDip01 = Math.Max(0f, WindowAfterDip01 - dt / AfterDipSeconds);
        }

        private static void StartStrike(int now, bool near, Random stormRandom)
        {
            bool flashesAllowed = Game1.options?.screenFlash ?? true;
            WindowStrikeNear = near;
            WindowFlashColour = near ? NearStrikeColour : FarStrikeColour;
            _strikePeak = near ? 0.85f + 0.15f * (float)stormRandom.NextDouble() : 0.35f + 0.3f * (float)stormRandom.NextDouble();
            // With the game's screen flash option off: one soft pulse, no strokes, no hold.
            if (!flashesAllowed)
                _strikePeak *= 0.4f;
            _strokeCount = !flashesAllowed || stormRandom.NextDouble() < 0.25 ? 1 : 2 + stormRandom.Next(MostStrokes - 1);
            int strokeTick = now;
            for (int stroke = 0; stroke < _strokeCount; stroke++)
            {
                _strokeTicks[stroke] = strokeTick;
                _strokePeaks[stroke] = stroke == 0 ? _strikePeak : _strikePeak * (0.45f + 0.4f * (float)stormRandom.NextDouble());
                strokeTick += 3 + stormRandom.Next(4);   // 50 to 100 ms to the next
            }
            int lastStroke = _strokeTicks[_strokeCount - 1];
            if (flashesAllowed && stormRandom.NextDouble() < 0.4)
            {
                _holdFromTick = lastStroke + 1;
                _holdUntilTick = _holdFromTick + 6 + stormRandom.Next(13);   // 0.1 to 0.3 s
                _holdLevel = _strikePeak * (0.35f + 0.25f * (float)stormRandom.NextDouble());
                _strikeEndTick = _holdUntilTick + 10;
            }
            else
            {
                _holdFromTick = _holdUntilTick = -1;
                _strikeEndTick = lastStroke + (flashesAllowed ? 12 : 30);
            }
            // A new place in the sky: the panes fall somewhere else on the floor each time.
            WindowStrikeLean = (float)(stormRandom.NextDouble() * 2.0 - 1.0) * 0.45f;
            WindowStrikeReachTiles = 2.6f + 1.9f * (float)stormRandom.NextDouble();
            _strikeLive = true;
        }

        private static int GapTicks(Random stormRandom)
            => (int)(60f * (WindowFlashGapMinSeconds + (WindowFlashGapMaxSeconds - WindowFlashGapMinSeconds) * (float)stormRandom.NextDouble()));

        private static void HeardStrike(Farm.LightningStrikeEvent strike)
        {
            if (strike.bigFlash || strike.smallFlash)
            {
                _strikeHeard = true;
                _strikeHeardNear = strike.bigFlash;
            }
        }

        /// <summary>
        /// A visible bolt to go with the flash. The game only ever draws one on the Farm, and
        /// only when the strike actually hit a lightning rod or a crop - everywhere else a storm
        /// is a white screen and a sound. This puts the bolt in the sky the player is looking at,
        /// using the game's own art and machinery (Utility.drawLightningBolt stacks the vanilla
        /// bolt sprite from the strike point up past the top of the screen, with its own light
        /// and fade), so it cannot look out of place.
        /// </summary>
        private static void MaybeDrawBolt(ModConfig config, Random strikeRandom, float boltAcross01)
        {
            if (!config.LightningBoltsEnabled)
                return;
            GameLocation? location = Game1.currentLocation;
            if (location is not { IsOutdoors: true } || !Game1.IsLightningHere(location))
                return;   // event flashes and indoor flickers get no bolt, only real storms
            // Not every rumble is overhead: some strikes stay behind the clouds.
            if (strikeRandom.NextDouble() > 0.65)
                return;
            // The game may have just drawn its own bolt (a rod or a crop was hit on this map);
            // a second one a screen-width away would read as two storms.
            foreach (var sprite in location.temporarySprites)
                if (sprite.lightId != null && sprite.lightId.Contains("_LightningBolt_"))
                    return;
            float strikeWorldX = Game1.viewport.X + boltAcross01 * Game1.viewport.Width;
            float strikeWorldY = Game1.viewport.Y
                + (0.55f + 0.35f * (float)strikeRandom.NextDouble()) * Game1.viewport.Height;
            StardewValley.Utility.drawLightningBolt(new Vector2(strikeWorldX, strikeWorldY), location);
        }

        /// <summary>Blend the strike over the computed sun/moon shadow answer. Called from the
        /// single source of shadow lean (ShadowRenderer.ComputeSun), so every bake and draw path
        /// keys together without any other call site changing.</summary>
        internal static void OverrideShadowKey(ref float rotation, ref float stretch, ref float alpha)
        {
            float key = StrikeKey01;
            if (key <= 0f)
                return;
            rotation = MathHelper.Lerp(rotation, StrikeLean, key);
            stretch = MathHelper.Lerp(stretch, 1.25f, key * 0.8f);
            alpha = MathHelper.Lerp(alpha, 0.9f, key);
        }

        /// <summary>Lift a darkened ambient toward no-darkening by the burst. The vanilla white
        /// overlay never reaches inside the mod's own multiply, so without this a storm flash
        /// whitened the sky while the room around a window stayed dim.</summary>
        internal static Vector3 LiftAmbient(Vector3 ambient)
            => Burst01 <= 0f ? ambient : Vector3.Lerp(ambient, Vector3.One, Burst01);

        /// <summary>The ~200 ms warm lift after the flash, drawn over the finished chain. A
        /// scalar and a quad, deliberately not a history render target.</summary>
        internal static void DrawAfterglow(SpriteBatch spriteBatch, int width, int height)
        {
            if (Afterglow01 <= 0.004f || Game1.options?.screenFlash != true || Game1.staminaRect == null)
                return;
            spriteBatch.Begin(SpriteSortMode.Deferred, ParticleSystem.PremultipliedAdditive,
                SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(0, 0, width, height),
                new Color(0.95f, 0.88f, 0.74f) * (Afterglow01 * AfterglowPeakAlpha));
            spriteBatch.End();
        }
    }
}
