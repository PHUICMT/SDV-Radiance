using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Replacement rain and snow, drawn in the game's own weather slot.
    ///
    /// <para>
    /// The game draws precipitation inside <c>Game1.drawWeather</c>: 70 identical rain sprites in
    /// one flat plane, and snow as one 16-frame texture tiled across the whole screen. Both are
    /// drawn before the lightmap and before this mod's effect chain, which is exactly the right
    /// place - they darken at night and blur, bloom and grade with the world rather than floating
    /// on top of it. So the replacement keeps the slot and changes only what is drawn in it: a
    /// Harmony prefix skips the vanilla draw on the days we replace, and a postfix on the same
    /// method draws layered streaks (rain) or drifting flakes (snow) with a shared wind.
    /// </para>
    ///
    /// <para>
    /// Everything is screen-space and per screen, the way vanilla's own drops are: a drop is a
    /// position inside this screen's viewport, shifted every frame by the camera delta so it
    /// stays world-locked while visible, and recycled across the edges. Split screen calls
    /// drawWeather once per screen, so each screen owns a full set of arrays and steps them on
    /// its own draw call.
    /// </para>
    /// </summary>
    internal static class PrecipitationSystem
    {
        /// <summary>Live config accessor, set once from ModEntry (the instance is replaced on
        /// GMCM reset, so a snapshot would go stale).</summary>
        internal static Func<ModConfig>? LiveConfig;

        /// <summary>Log sink, set alongside <see cref="LiveConfig"/> at install time.</summary>
        internal static StardewModdingAPI.IMonitor? Monitor;

        /// <summary>True when some other mod has its own prefix or transpiler on drawWeather.
        /// Two mods fighting over the same draw slot ends with one of them broken and a player
        /// who cannot tell which; yielding costs us a feature and costs them nothing.</summary>
        internal static bool AnotherModOwnsWeatherDraw;

        // ---- rain look -----------------------------------------------------------------------

        /// <summary>Vertical speed of the front layer, in buffer pixels per second. Anchored on
        /// vanilla's measured ~343-457 px/s so the replacement reads as the same storm.</summary>
        private const float BaseFallPixelsPerSecond = 460f;
        /// <summary>Three planes of rain, back to front. The back layer is slow, short and faint;
        /// the front is fast, long and bright. That difference IS the depth.</summary>
        private static readonly float[] RainLayerSpeed = { 0.55f, 0.80f, 1.10f };
        private static readonly float[] RainLayerLengthPixels = { 17f, 27f, 38f };
        private static readonly float[] RainLayerWidthPixels = { 2.4f, 3.0f, 4.2f };
        private static readonly float[] RainLayerAlpha = { 0.30f, 0.46f, 0.66f };
        private static readonly float[] RainLayerShare = { 0.40f, 0.35f, 0.25f };
        /// <summary>One drop per this many viewport pixels at density 1 (~480 drops at 1080p).</summary>
        private const float PixelsPerRainDrop = 4300f;
        private const int MaximumRainDrops = 2400;
        /// <summary>A streak drawn slightly blue-grey rather than white: white reads as sleet.</summary>
        private static readonly Color RainTint = new(205, 220, 235);
        /// <summary>Green rain's streak colour. Vanilla tints its drops LimeGreen and draws each
        /// one twice; ours shift toward this and gain weight instead of doubling the draws.</summary>
        private static readonly Color GreenRainTint = new(120, 225, 80);
        /// <summary>How much heavier a green streak reads than a clear one (the stand-in for
        /// vanilla's double draw).</summary>
        private const float GreenRainAlphaBoost = 0.45f;

        // ---- wind look -----------------------------------------------------------------------

        /// <summary>Blossom in spring, leaves the rest of the year, riding the same shared wind
        /// as the rain slant but much harder - debris IS the wind made visible.</summary>
        private static readonly float[] WindLayerSpeed = { 0.50f, 0.75f, 1.05f };
        private static readonly float[] WindLayerSizePixels = { 5f, 7.5f, 10.5f };
        private static readonly float[] WindLayerAlpha = { 0.50f, 0.70f, 0.90f };
        private static readonly float[] WindLayerShare = { 0.40f, 0.35f, 0.25f };
        private const float PixelsPerWindPiece = 8000f;
        private const int MaximumWindPieces = 900;
        /// <summary>Debris rides the shared wind this much harder than the rain slant does.</summary>
        private const float WindDebrisRideMultiplier = 2.2f;
        private const float WindDebrisSinkPixelsPerSecond = 12f;
        private static readonly Color[] SpringPieceColours = { new(255, 183, 197), new(255, 214, 224), new(250, 242, 246) };
        private static readonly Color[] SummerPieceColours = { new(120, 185, 85), new(148, 205, 105), new(96, 155, 72) };
        private static readonly Color[] FallPieceColours = { new(214, 142, 52), new(192, 104, 44), new(166, 84, 34), new(184, 64, 44) };
        private static readonly Color[] WinterPieceColours = { new(240, 246, 255), new(222, 233, 246) };

        // ---- snow look -----------------------------------------------------------------------

        private static readonly float[] SnowLayerFallSpeed = { 30f, 52f, 84f };
        private static readonly float[] SnowLayerSizePixels = { 7f, 10.5f, 15f };
        private static readonly float[] SnowLayerAlpha = { 0.35f, 0.55f, 0.75f };
        private static readonly float[] SnowLayerShare = { 0.42f, 0.34f, 0.24f };
        private static readonly float[] SnowLayerSwayPixels = { 14f, 10f, 7f };
        private const float PixelsPerSnowFlake = 3400f;
        private const int MaximumSnowFlakes = 1500;

        // ---- shared ---------------------------------------------------------------------------

        /// <summary>Speed while a storm (rain + lightning) is overhead. The count is the
        /// player's, see <see cref="ModConfig.PrecipitationStormDensity"/>.</summary>
        private const float StormSpeedMultiplier = 1.25f;
        private const int MaximumSplashes = 96;
        /// <summary>Vanilla advances a splash frame every 70 ms; keeping the clock keeps the beat
        /// even though the art is now ours.</summary>
        private const float SplashSecondsPerFrame = 0.07f;
        private const int SplashFrameCount = 3;
        private const int SplashCellSize = 32;
        /// <summary>How wide one splash is drawn, in buffer pixels. Half a tile: bigger reads as
        /// a puddle being thrown, smaller vanishes against the ground texture.</summary>
        private const float SplashWidthPixels = 30f;
        /// <summary>Water has no colour of its own; a splash is the light it bends, which on an
        /// overcast day is a colourless grey with the faintest cool lean. The vanilla splash
        /// sprite is painted a saturated blue that belongs to the game's palette rather than to
        /// water, and against a wet grey street it read as confetti.</summary>
        private static readonly Color SplashTint = new(226, 236, 242);
        private static readonly Color GreenRainSplashTint = new(150, 226, 128);
        /// <summary>Presence fades over ~0.45 s each way - config toggles and mid-day weather
        /// flips ease instead of popping, per the house rule.</summary>
        private const float PresenceSecondsToFull = 0.45f;
        private const float FadeGone = 0.004f;

        /// <summary>The shared wind, in horizontal buffer pixels per second, low-pass filtered so
        /// the slant drifts instead of snapping. Shared statics on purpose: both split-screen
        /// halves must lean the same way.</summary>
        private static float _windPixelsPerSecond = -120f;

        private static Texture2D? _streakTexture;
        private static Texture2D? _flakeTexture;

        private struct RainDrop
        {
            internal Vector2 Position;
            /// <summary>Buffer pixels left to fall before this drop lands and splashes.</summary>
            internal float FallRemaining;
            internal float Alpha;
            internal byte Layer;
        }

        private struct SnowFlake
        {
            internal Vector2 Position;
            internal float SwayPhase;
            internal float SwayPerSecond;
            internal float Alpha;
            internal byte Layer;
        }

        private struct WindPiece
        {
            internal Vector2 Position;
            internal float FlutterPhase;
            internal float FlutterPerSecond;
            internal float TumblePhase;
            internal float TumblePerSecond;
            internal float Alpha;
            internal Color Tint;
            internal byte Layer;
        }

        private struct Splash
        {
            internal Vector2 Position;
            internal float AgeSeconds;
            internal bool Active;
            /// <summary>Born under green rain: drawn from the sheet's green frames (+4).</summary>
            internal bool Green;
        }

        private sealed class ScreenPrecipitation
        {
            internal readonly RainDrop[] Rain = new RainDrop[MaximumRainDrops];
            internal readonly SnowFlake[] Snow = new SnowFlake[MaximumSnowFlakes];
            internal readonly WindPiece[] Wind = new WindPiece[MaximumWindPieces];
            internal readonly Splash[] Splashes = new Splash[MaximumSplashes];
            /// <summary>Spring blows blossom; every other season blows leaves.</summary>
            internal bool WindPetals;
            /// <summary>True while the pipeline's water stage carries the sky group this frame.
            /// Rain hanging in the AIR must not ripple with the water under it, so whenever the
            /// water stage runs, the streaks/flakes/pieces are drawn onto its output instead of
            /// into the weather slot the ripple reads from. Splashes stay in the slot - they are
            /// on the ground, and land pixels do not ripple.</summary>
            internal bool SkyDrawDeferred;
            internal int NextSplash;
            internal readonly Random Random;
            internal float Presence;
            internal float StormEase;
            /// <summary>Eases toward 1 under green rain, so a debug flip mid-day shifts the
            /// colour over a second instead of popping every streak lime at once.</summary>
            internal float GreenEase;
            internal int SeededWidth, SeededHeight;
            internal Vector2 PreviousViewport;
            internal bool ViewportKnown;
            internal int LastDrawnRain, LastDrawnSnow, LastDrawnSplashes, LastDrawnWind;

            internal ScreenPrecipitation(int screenId)
            {
                Random = new Random(20260819 + screenId * 101);
            }
        }

        private static readonly Dictionary<int, ScreenPrecipitation> _screens = new();

        // ---- the Harmony pair -----------------------------------------------------------------

        /// <summary>Prefix on Game1.drawWeather: false = skip the vanilla draw this frame.</summary>
        internal static bool DrawWeather_Prefix()
        {
            return !SuppressVanillaThisCall();
        }

        /// <summary>Postfix on Game1.drawWeather: step and draw our precipitation. Runs whether
        /// or not the vanilla body ran, which is what lets a fade-out finish after the gate has
        /// already handed the slot back.</summary>
        internal static void DrawWeather_Postfix(GameTime time)
        {
            try
            {
                StepAndDraw(time);
            }
            catch (Exception exception)
            {
                // A crash inside the game's draw loop takes the whole game down with it. Turning
                // the feature off is strictly better than that, and the log says why it went.
                AnotherModOwnsWeatherDraw = true;
                Monitor?.Log($"Precipitation draw failed and switched itself off: {exception}", StardewModdingAPI.LogLevel.Error);
            }
        }

        /// <summary>Whether this exact call should skip the vanilla weather draw.</summary>
        private static bool SuppressVanillaThisCall()
        {
            if (!ReplacementWanted(out _, out _, out _))
            {
                // Weather the gate no longer wants may still be fading out on screen. Keep
                // vanilla suppressed while OUR picture of the same weather is still visible,
                // or the two rains overlap for half a second; once vanilla itself would draw
                // nothing (the weather really stopped), there is nothing to suppress.
                ScreenPrecipitation? screen = _screens.TryGetValue(CurrentScreenId(), out var s) ? s : null;
                if (screen == null || screen.Presence <= FadeGone)
                    return false;
                GameLocation? location = Game1.currentLocation;
                bool vanillaWouldDraw = location != null && location.IsOutdoors
                    && (location.IsRainingHere() || location.IsSnowingHere()
                        || location.IsDebrisWeatherHere());
                return vanillaWouldDraw;
            }
            return true;
        }

        /// <summary>The one gate: is the replacement asked for, and is this a frame it may own.
        /// Green rain is ours too - it IS rain underneath (the game keeps IsRaining true), just
        /// drawn twice in lime, so the replacement keeps the same drops and shifts their colour
        /// and weight instead. The Summit never draws precipitation at all.</summary>
        private static bool ReplacementWanted(out bool raining, out bool snowing, out bool windy)
        {
            raining = snowing = windy = false;
            ModConfig? config = LiveConfig?.Invoke();
            if (config == null || !config.Enabled || !config.PrecipitationEnabled || AnotherModOwnsWeatherDraw)
                return false;
            if (Game1.game1?.takingMapScreenshot == true)
                return false;   // a map screenshot would show a viewport-sized patch of rain
            GameLocation? location = Game1.currentLocation;
            if (location == null || !location.IsOutdoors || location is StardewValley.Locations.Summit)
                return false;
            // Vanilla stops drawing rain when an event has walked the camera off the map edge;
            // mirrored as the same validity check, not as an effect gate (cutscene-safe rule).
            if (Game1.eventUp && !location.isTileOnMap(new Vector2(Game1.viewport.X / 64, Game1.viewport.Y / 64)))
                return false;
            raining = location.IsRainingHere() && config.PrecipitationRain;
            snowing = location.IsSnowingHere() && config.PrecipitationSnow;
            windy = location.IsDebrisWeatherHere() && !location.ignoreDebrisWeather.Value
                && config.PrecipitationWind;
            return raining || snowing || windy;
        }

        private static int CurrentScreenId() => StardewModdingAPI.Context.ScreenId;

        /// <summary>Told by the pipeline while it builds this screen's stage list: whether the
        /// water stage will run and should carry the sky group (see SkyDrawDeferred). One frame
        /// of lag on transitions, which the presence fades cover.</summary>
        internal static void DeferSkyDrawing(bool deferred)
        {
            if (_screens.TryGetValue(CurrentScreenId(), out ScreenPrecipitation? screen))
                screen.SkyDrawDeferred = deferred;
        }

        /// <summary>Draw the sky group onto the water stage's freshly written output. The scene
        /// under it has already been rippled and presence-blended; the rain lands crisp on top,
        /// scaled to the chain's buffer and dimmed by the same ambient the particles use, since
        /// this side of the capture never meets the vanilla lightmap.</summary>
        internal static void DrawSkyForChain(SpriteBatch spriteBatch, RenderTarget2D dest,
                                             int frameWidth, Vector3 ambient)
        {
            if (!_screens.TryGetValue(CurrentScreenId(), out ScreenPrecipitation? screen)
                || !screen.SkyDrawDeferred || screen.Presence <= FadeGone)
                return;
            long started = FrameCost.Begin(FrameCost.Part.Precipitation);
            float pixelScale = frameWidth > 0 ? dest.Width / (float)frameWidth : 1f;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            DrawSkyGroup(spriteBatch, screen, Math.Max(1, Game1.viewport.Width),
                Math.Max(1, Game1.viewport.Height), pixelScale, ambient);
            spriteBatch.End();
            FrameCost.End(FrameCost.Part.Precipitation, started);
        }

        private static Color Shaded(Color colour, Vector3 ambient) => new(
            (byte)(colour.R * ambient.X), (byte)(colour.G * ambient.Y), (byte)(colour.B * ambient.Z), colour.A);

        // ---- step + draw ------------------------------------------------------------------------

        private static void StepAndDraw(GameTime time)
        {
            bool wanted = ReplacementWanted(out bool raining, out bool snowing, out bool windy);
            int screenId = CurrentScreenId();
            if (!_screens.TryGetValue(screenId, out ScreenPrecipitation? screen))
            {
                if (!wanted)
                    return;
                screen = new ScreenPrecipitation(screenId);
                _screens[screenId] = screen;
                // A player who leaves a split-screen session takes their screen id with them and
                // this dictionary goes on holding their rain. Checked when a screen is ADDED,
                // which is the only moment the set can have changed since the last look.
                LiveScreens.ForgetDeparted(_screens);
            }

            long started = FrameCost.Begin(FrameCost.Part.Precipitation);
            float dt = Determinism.Frozen ? 0f : (float)time.ElapsedGameTime.TotalSeconds;
            dt = Math.Min(dt, 0.1f);

            float presenceTarget = wanted ? 1f : 0f;
            screen.Presence = Approach(screen.Presence, presenceTarget, dt / PresenceSecondsToFull);
            if (Determinism.Frozen)
                screen.Presence = presenceTarget;
            if (screen.Presence <= FadeGone)
            {
                screen.LastDrawnRain = screen.LastDrawnSnow = screen.LastDrawnSplashes = 0;
            screen.LastDrawnWind = 0;
                FrameCost.End(FrameCost.Part.Precipitation, started);
                return;
            }

            ModConfig config = LiveConfig!();
            GameLocation location = Game1.currentLocation;
            int viewportWidth = Math.Max(1, Game1.viewport.Width);
            int viewportHeight = Math.Max(1, Game1.viewport.Height);

            bool storm = raining && Game1.IsLightningHere(location);
            screen.StormEase = Approach(screen.StormEase, storm ? 1f : 0f, dt / 2f);
            bool greenRain = raining && location.IsGreenRainingHere();
            screen.GreenEase = Approach(screen.GreenEase, greenRain ? 1f : 0f, dt / 1f);

            UpdateSharedWind(dt);
            EnsureTextures();
            EnsureSeeded(screen, viewportWidth, viewportHeight);
            ShiftWithCamera(screen, viewportWidth, viewportHeight);

            float stormCount = 1f + (Math.Max(1f, config.PrecipitationStormDensity) - 1f) * screen.StormEase;
            float stormSpeed = 1f + (StormSpeedMultiplier - 1f) * screen.StormEase;
            float area = viewportWidth * (float)viewportHeight;

            int rainTarget = raining
                ? Math.Min(MaximumRainDrops, (int)(area / PixelsPerRainDrop * config.PrecipitationRainDensity * stormCount))
                : 0;
            int snowTarget = snowing
                ? Math.Min(MaximumSnowFlakes, (int)(area / PixelsPerSnowFlake * config.PrecipitationSnowDensity))
                : 0;
            int windTarget = windy
                ? Math.Min(MaximumWindPieces, (int)(area / PixelsPerWindPiece * config.PrecipitationWindDensity))
                : 0;
            screen.WindPetals = location.GetSeason() == Season.Spring;

            StepRain(screen, dt, rainTarget, stormSpeed, config.PrecipitationRainSlant, viewportWidth, viewportHeight, location);
            StepSnow(screen, dt, snowTarget, viewportWidth, viewportHeight);
            StepWind(screen, dt, windTarget, config.PrecipitationWindSlant, viewportWidth, viewportHeight, location);
            StepSplashes(screen, dt);

            DrawAll(screen, viewportWidth, viewportHeight);
            FrameCost.End(FrameCost.Part.Precipitation, started);
        }

        /// <summary>Low-pass the wind toward the game's own debris wind plus a slow sway, so the
        /// slant is alive but never snaps. Runs once per frame (first screen wins the tick).</summary>
        private static int _windSteppedTick = -1;
        private static void UpdateSharedWind(float dt)
        {
            if (Game1.ticks == _windSteppedTick)
                return;
            _windSteppedTick = Game1.ticks;
            float seconds = (float)Determinism.Seconds;
            float target = StardewValley.WeatherDebris.globalWind * 480f
                + MathF.Sin(seconds * 0.29f) * 28f
                + MathF.Sin(seconds * 0.071f) * 40f;
            target = Math.Clamp(target, -260f, 260f);
            _windPixelsPerSecond = Approach(_windPixelsPerSecond, target, dt / 3f);
        }

        private static float Approach(float current, float target, float amount)
            => current + (target - current) * Math.Clamp(amount, 0f, 1f);

        // ---- seeding + camera lock --------------------------------------------------------------

        private static void EnsureSeeded(ScreenPrecipitation screen, int viewportWidth, int viewportHeight)
        {
            // Reseed on first use and when the viewport is a genuinely different shape (window
            // resize, zoom change) - the old positions would bunch into a corner of the new one.
            bool shapeChanged = Math.Abs(screen.SeededWidth - viewportWidth) > viewportWidth / 4
                || Math.Abs(screen.SeededHeight - viewportHeight) > viewportHeight / 4;
            if (screen.SeededWidth != 0 && !shapeChanged)
                return;
            screen.SeededWidth = viewportWidth;
            screen.SeededHeight = viewportHeight;
            Random random = screen.Random;
            for (int i = 0; i < screen.Rain.Length; i++)
            {
                screen.Rain[i].Position = new Vector2(random.Next(-64, viewportWidth + 64), random.Next(-64, viewportHeight + 64));
                screen.Rain[i].FallRemaining = (0.15f + 1.0f * (float)random.NextDouble()) * viewportHeight;
                screen.Rain[i].Layer = PickLayer(random, RainLayerShare);
                screen.Rain[i].Alpha = 0f;
            }
            for (int i = 0; i < screen.Snow.Length; i++)
            {
                screen.Snow[i].Position = new Vector2(random.Next(-64, viewportWidth + 64), random.Next(-64, viewportHeight + 64));
                screen.Snow[i].SwayPhase = (float)(random.NextDouble() * Math.PI * 2);
                screen.Snow[i].SwayPerSecond = 1.6f + 1.5f * (float)random.NextDouble();
                screen.Snow[i].Layer = PickLayer(random, SnowLayerShare);
                screen.Snow[i].Alpha = 0f;
            }
            for (int i = 0; i < screen.Wind.Length; i++)
            {
                screen.Wind[i].Position = new Vector2(random.Next(-64, viewportWidth + 64), random.Next(-64, viewportHeight + 64));
                screen.Wind[i].FlutterPhase = (float)(random.NextDouble() * Math.PI * 2);
                screen.Wind[i].FlutterPerSecond = 1.5f + 2.0f * (float)random.NextDouble();
                screen.Wind[i].TumblePhase = (float)(random.NextDouble() * Math.PI * 2);
                screen.Wind[i].TumblePerSecond = 2.0f + 3.0f * (float)random.NextDouble();
                screen.Wind[i].Layer = PickLayer(random, WindLayerShare);
                screen.Wind[i].Alpha = 0f;
                screen.Wind[i].Tint = Color.White;
            }
        }

        private static byte PickLayer(Random random, float[] shares)
        {
            double roll = random.NextDouble();
            return roll < shares[0] ? (byte)0 : roll < shares[0] + shares[1] ? (byte)1 : (byte)2;
        }

        /// <summary>Keep every particle world-locked while it is on screen: shift by minus the
        /// camera delta, then wrap into a margin beyond the edges, the way vanilla's own drops
        /// recycle. A warp's huge delta just wraps everything - the picture stays full.</summary>
        private static void ShiftWithCamera(ScreenPrecipitation screen, int viewportWidth, int viewportHeight)
        {
            Vector2 viewportNow = new(Game1.viewport.X, Game1.viewport.Y);
            if (!screen.ViewportKnown)
            {
                screen.ViewportKnown = true;
                screen.PreviousViewport = viewportNow;
                return;
            }
            Vector2 delta = viewportNow - screen.PreviousViewport;
            screen.PreviousViewport = viewportNow;
            if (delta == Vector2.Zero)
                return;
            float spanX = viewportWidth + 128;
            float spanY = viewportHeight + 128;
            for (int i = 0; i < screen.Rain.Length; i++)
            {
                screen.Rain[i].Position -= delta;
                screen.Rain[i].Position.X = Wrap(screen.Rain[i].Position.X, -64f, spanX);
                screen.Rain[i].Position.Y = Wrap(screen.Rain[i].Position.Y, -64f, spanY);
            }
            for (int i = 0; i < screen.Snow.Length; i++)
            {
                screen.Snow[i].Position -= delta;
                screen.Snow[i].Position.X = Wrap(screen.Snow[i].Position.X, -64f, spanX);
                screen.Snow[i].Position.Y = Wrap(screen.Snow[i].Position.Y, -64f, spanY);
            }
            for (int i = 0; i < screen.Wind.Length; i++)
            {
                screen.Wind[i].Position -= delta;
                screen.Wind[i].Position.X = Wrap(screen.Wind[i].Position.X, -64f, spanX);
                screen.Wind[i].Position.Y = Wrap(screen.Wind[i].Position.Y, -64f, spanY);
            }
            for (int i = 0; i < screen.Splashes.Length; i++)
                if (screen.Splashes[i].Active)
                    screen.Splashes[i].Position -= delta;
        }

        private static float Wrap(float value, float low, float span)
        {
            float offset = (value - low) % span;
            if (offset < 0f)
                offset += span;
            return low + offset;
        }

        // ---- stepping ---------------------------------------------------------------------------

        private static void StepRain(ScreenPrecipitation screen, float dt, int targetCount,
                                     float stormSpeed, float rainSlant, int viewportWidth, int viewportHeight, GameLocation location)
        {
            SurfaceMap? surface = targetCount > 0 ? SurfaceMap.For(location) : null;
            Random random = screen.Random;
            for (int i = 0; i < screen.Rain.Length; i++)
            {
                ref RainDrop drop = ref screen.Rain[i];
                float alphaTarget = i < targetCount ? RainLayerAlpha[drop.Layer] : 0f;
                drop.Alpha = Approach(drop.Alpha, alphaTarget, dt / 0.3f);
                if (drop.Alpha <= 0.002f && alphaTarget <= 0f)
                    continue;
                float speed = BaseFallPixelsPerSecond * RainLayerSpeed[drop.Layer] * stormSpeed;
                float fall = speed * dt;
                // The slant the player chose is applied to the TRAVEL, and the streak is drawn
                // at the angle of that travel, so a harder slant is rain that really does cross
                // the screen faster rather than a sprite leaning while it falls straight.
                drop.Position.X += _windPixelsPerSecond * rainSlant * RainLayerSpeed[drop.Layer] * dt;
                drop.Position.Y += fall;
                drop.FallRemaining -= fall;
                if (drop.Position.X < -64f) drop.Position.X += viewportWidth + 128;
                else if (drop.Position.X > viewportWidth + 64f) drop.Position.X -= viewportWidth + 128;
                // Strictly beyond the camera wrap's own margin (64): the wrap parks drops that
                // left the top at just past the bottom edge, and a culling line INSIDE that
                // band teleported every one of them straight back to the top - walking down
                // emptied the lower screen of rain until the player stood still.
                bool offBottom = drop.Position.Y > viewportHeight + 72f;
                if (drop.FallRemaining <= 0f || offBottom)
                {
                    // Only the front layer splashes visibly, and only where the world can take
                    // one: the water shader already draws expanding rain rings, so a sprite
                    // splash on the river would double-ring the same drop.
                    if (!offBottom && drop.Layer == 2 && drop.Alpha > 0.1f && surface != null)
                    {
                        int tileX = (int)((Game1.viewport.X + drop.Position.X) / 64f);
                        int tileY = (int)((Game1.viewport.Y + drop.Position.Y) / 64f);
                        SurfaceClass under = surface.GetSurface(tileX, tileY);
                        if (under != SurfaceClass.Water && under != SurfaceClass.Void)
                            SpawnSplash(screen, drop.Position);
                    }
                    // Reborn anywhere on screen, the way vanilla's own drops respawn: rebirth
                    // at the top only starved the bottom whenever the camera chased downward.
                    // Alpha restarts at zero so the newcomer eases in instead of popping mid-air.
                    drop.Position = new Vector2(random.Next(-64, viewportWidth + 64),
                        offBottom ? -random.Next(0, 64) : random.Next(-64, viewportHeight + 64));
                    drop.FallRemaining = (0.35f + 0.85f * (float)random.NextDouble()) * viewportHeight;
                    drop.Alpha = 0f;
                }
            }
        }

        private static void SpawnSplash(ScreenPrecipitation screen, Vector2 position)
        {
            ref Splash splash = ref screen.Splashes[screen.NextSplash];
            screen.NextSplash = (screen.NextSplash + 1) % screen.Splashes.Length;
            splash.Position = position;
            splash.AgeSeconds = 0f;
            splash.Active = true;
            splash.Green = screen.GreenEase > 0.5f;
        }

        private static void StepSplashes(ScreenPrecipitation screen, float dt)
        {
            for (int i = 0; i < screen.Splashes.Length; i++)
            {
                if (!screen.Splashes[i].Active)
                    continue;
                screen.Splashes[i].AgeSeconds += dt;
                if (screen.Splashes[i].AgeSeconds >= SplashSecondsPerFrame * SplashFrameCount)
                    screen.Splashes[i].Active = false;
            }
        }

        private static void StepSnow(ScreenPrecipitation screen, float dt, int targetCount,
                                     int viewportWidth, int viewportHeight)
        {
            Random random = screen.Random;
            for (int i = 0; i < screen.Snow.Length; i++)
            {
                ref SnowFlake flake = ref screen.Snow[i];
                float alphaTarget = i < targetCount ? SnowLayerAlpha[flake.Layer] : 0f;
                flake.Alpha = Approach(flake.Alpha, alphaTarget, dt / 0.6f);
                if (flake.Alpha <= 0.002f && alphaTarget <= 0f)
                    continue;
                flake.SwayPhase += flake.SwayPerSecond * dt;
                flake.Position.Y += SnowLayerFallSpeed[flake.Layer] * dt;
                flake.Position.X += (MathF.Sin(flake.SwayPhase) * SnowLayerSwayPixels[flake.Layer] * flake.SwayPerSecond * 0.5f
                    + _windPixelsPerSecond * 0.30f * (0.5f + 0.5f * SnowLayerFallSpeed[flake.Layer] / SnowLayerFallSpeed[2])) * dt;
                if (flake.Position.X < -64f) flake.Position.X += viewportWidth + 128;
                else if (flake.Position.X > viewportWidth + 64f) flake.Position.X -= viewportWidth + 128;
                if (flake.Position.Y > viewportHeight + 72f)
                    flake.Position = new Vector2(random.Next(-64, viewportWidth + 64), -random.Next(16, 48));
            }
        }

        /// <summary>Leaves and blossom on a wind day. The pieces ride the shared wind hard,
        /// flutter vertically on their own phase, and tumble - the tumble is drawn as a squash
        /// on the sprite's width, the poor man's 3D flip vanilla fakes with its four frames.
        /// A piece that leaves the screen comes back re-coloured from the season's palette, so
        /// warping from autumn to the always-summer island recolours the air within seconds.</summary>
        private static void StepWind(ScreenPrecipitation screen, float dt, int targetCount,
                                     float windSlant, int viewportWidth, int viewportHeight, GameLocation location)
        {
            Random random = screen.Random;
            Color[] palette = location.GetSeason() switch
            {
                Season.Spring => SpringPieceColours,
                Season.Summer => SummerPieceColours,
                Season.Fall => FallPieceColours,
                _ => WinterPieceColours,
            };
            for (int i = 0; i < screen.Wind.Length; i++)
            {
                ref WindPiece piece = ref screen.Wind[i];
                float alphaTarget = i < targetCount ? WindLayerAlpha[piece.Layer] : 0f;
                piece.Alpha = Approach(piece.Alpha, alphaTarget, dt / 0.5f);
                if (piece.Alpha <= 0.002f && alphaTarget <= 0f)
                    continue;
                if (piece.Tint == Color.White)
                    piece.Tint = palette[random.Next(palette.Length)];
                piece.FlutterPhase += piece.FlutterPerSecond * dt;
                piece.TumblePhase += piece.TumblePerSecond * dt;
                float ride = WindLayerSpeed[piece.Layer];
                piece.Position.X += _windPixelsPerSecond * WindDebrisRideMultiplier * ride * dt;
                piece.Position.Y += (MathF.Sin(piece.FlutterPhase) * 26f
                    + WindDebrisSinkPixelsPerSecond * windSlant) * ride * dt;
                bool wrapped = false;
                if (piece.Position.X < -64f) { piece.Position.X += viewportWidth + 128; wrapped = true; }
                else if (piece.Position.X > viewportWidth + 64f) { piece.Position.X -= viewportWidth + 128; wrapped = true; }
                if (piece.Position.Y < -64f) { piece.Position.Y += viewportHeight + 128; wrapped = true; }
                else if (piece.Position.Y > viewportHeight + 64f) { piece.Position.Y -= viewportHeight + 128; wrapped = true; }
                if (wrapped)
                    piece.Tint = palette[random.Next(palette.Length)];
            }
        }

        // ---- drawing ----------------------------------------------------------------------------

        private static void DrawAll(ScreenPrecipitation screen, int viewportWidth, int viewportHeight)
        {
            SpriteBatch spriteBatch = Game1.spriteBatch;
            float presence = screen.Presence;
            screen.LastDrawnRain = screen.LastDrawnSnow = screen.LastDrawnSplashes = 0;
            screen.LastDrawnWind = 0;

            // Streaks and flakes are our own soft-edged art: linear sampling. The splash frames
            // are vanilla pixel art at 4x: point sampling, or they arrive as mush. Two batches.
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            if (!screen.SkyDrawDeferred)
                DrawSkyGroup(spriteBatch, screen, viewportWidth, viewportHeight, 1f, Vector3.One);
            spriteBatch.End();
            DrawSplashes(spriteBatch, screen, presence);
        }

        /// <summary>Streaks, flakes and wind pieces - everything in the AIR. Drawn either into
        /// the weather slot (scale 1, no tint: the vanilla lightmap does the darkening) or onto
        /// the water stage's output (chain scale, particle ambient), never both.</summary>
        private static void DrawSkyGroup(SpriteBatch spriteBatch, ScreenPrecipitation screen,
                                         int viewportWidth, int viewportHeight, float pixelScale, Vector3 ambient)
        {
            float presence = screen.Presence;
            screen.LastDrawnRain = screen.LastDrawnSnow = 0;
            screen.LastDrawnWind = 0;
            // Each weather carries its own size and visibility, the way each particle emitter
            // does: one dial for how big a piece is and one for how much of the picture it is
            // allowed to take, because "too small" and "too faint" are different complaints
            // with different fixes.
            ModConfig? dials = LiveConfig?.Invoke();
            float rainSize = dials?.PrecipitationRainSize ?? 1f;
            float rainOpacity = dials?.PrecipitationRainOpacity ?? 1f;
            float snowSize = dials?.PrecipitationSnowSize ?? 1f;
            float snowOpacity = dials?.PrecipitationSnowOpacity ?? 1f;
            float windSize = dials?.PrecipitationWindSize ?? 1f;
            float windOpacity = dials?.PrecipitationWindOpacity ?? 1f;
            float fallSpeed = BaseFallPixelsPerSecond;
            float streakAngle = MathF.Atan2(-_windPixelsPerSecond * (dials?.PrecipitationRainSlant ?? 1f), fallSpeed);
            if (_streakTexture != null)
            {
                Color streakTint = Shaded(Color.Lerp(RainTint, GreenRainTint, screen.GreenEase), ambient);
                float streakWeight = 1f + GreenRainAlphaBoost * screen.GreenEase;
                Vector2 streakOrigin = new(_streakTexture.Width / 2f, _streakTexture.Height);
                for (int layer = 0; layer < 3; layer++)
                {
                    float lengthScale = RainLayerLengthPixels[layer] / _streakTexture.Height * pixelScale * rainSize;
                    float widthScale = RainLayerWidthPixels[layer] / _streakTexture.Width * pixelScale
                        * MathF.Sqrt(rainSize);   // a streak thickens more slowly than it lengthens
                    for (int i = 0; i < screen.Rain.Length; i++)
                    {
                        ref RainDrop drop = ref screen.Rain[i];
                        if (drop.Layer != layer || drop.Alpha <= 0.01f)
                            continue;
                        if (drop.Position.X < -48f || drop.Position.X > viewportWidth + 48f
                            || drop.Position.Y < -48f || drop.Position.Y > viewportHeight + 48f)
                            continue;
                        // A fixed per-slot jitter so neighbouring streaks stop being clones.
                        float lengthJitter = 0.82f + 0.045f * (i % 9);
                        spriteBatch.Draw(_streakTexture, drop.Position * pixelScale, null,
                            streakTint * Math.Min(1f, drop.Alpha * streakWeight * presence * rainOpacity),
                            streakAngle, streakOrigin,
                            new Vector2(widthScale, lengthScale * lengthJitter), SpriteEffects.None, 1f);
                        screen.LastDrawnRain++;
                    }
                }
            }
            if (_flakeTexture != null)
            {
                // The vanilla accessibility slider for snow visibility keeps working on ours.
                float snowTransparency = Game1.options?.snowTransparency ?? 1f;
                Color flakeTint = Shaded(Color.White, ambient);
                Vector2 flakeOrigin = new(_flakeTexture.Width / 2f, _flakeTexture.Height / 2f);
                for (int layer = 0; layer < 3; layer++)
                {
                    float scale = SnowLayerSizePixels[layer] / _flakeTexture.Width * pixelScale * snowSize;
                    for (int i = 0; i < screen.Snow.Length; i++)
                    {
                        ref SnowFlake flake = ref screen.Snow[i];
                        if (flake.Layer != layer || flake.Alpha <= 0.01f)
                            continue;
                        if (flake.Position.X < -16f || flake.Position.X > viewportWidth + 16f
                            || flake.Position.Y < -16f || flake.Position.Y > viewportHeight + 16f)
                            continue;
                        spriteBatch.Draw(_flakeTexture, flake.Position * pixelScale, null,
                            flakeTint * Math.Min(1f, flake.Alpha * presence * 0.8f * snowTransparency * snowOpacity),
                            0f, flakeOrigin, scale, SpriteEffects.None, 1f);
                        screen.LastDrawnSnow++;
                    }
                }
            }
            Texture2D? pieceTexture = screen.WindPetals ? _petalTexture : _leafTexture;
            if (pieceTexture != null)
            {
                Vector2 pieceOrigin = new(pieceTexture.Width / 2f, pieceTexture.Height / 2f);
                for (int layer = 0; layer < 3; layer++)
                {
                    float scale = WindLayerSizePixels[layer] / pieceTexture.Height * pixelScale * windSize;
                    for (int i = 0; i < screen.Wind.Length; i++)
                    {
                        ref WindPiece piece = ref screen.Wind[i];
                        if (piece.Layer != layer || piece.Alpha <= 0.01f)
                            continue;
                        if (piece.Position.X < -32f || piece.Position.X > viewportWidth + 32f
                            || piece.Position.Y < -32f || piece.Position.Y > viewportHeight + 32f)
                            continue;
                        // The tumble squashes the width: the poor man's 3D flip, in place of
                        // vanilla's four flutter frames.
                        float rotation = MathF.Sin(piece.TumblePhase) * 0.7f;
                        float flip = 0.35f + 0.65f * MathF.Abs(MathF.Cos(piece.TumblePhase * 0.8f));
                        spriteBatch.Draw(pieceTexture, piece.Position * pixelScale, null,
                            Shaded(piece.Tint, ambient) * Math.Min(1f, piece.Alpha * presence * windOpacity),
                            rotation, pieceOrigin,
                            new Vector2(scale * flip, scale), SpriteEffects.None, 1f);
                        screen.LastDrawnWind++;
                    }
                }
            }
        }

        /// <summary>The ground-bound half: splash frames from the vanilla sheet, always in the
        /// weather slot - land pixels do not ripple, so they have nothing to escape.</summary>
        private static void DrawSplashes(SpriteBatch spriteBatch, ScreenPrecipitation screen, float presence)
        {
            if (_splashTexture == null)
                return;
            // Soft-edged art of our own, so linear sampling rather than the point sampling the
            // vanilla sprite needed.
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            float scale = SplashWidthPixels / SplashCellSize;
            var origin = new Vector2(SplashCellSize / 2f, SplashCellSize * 0.62f);
            for (int i = 0; i < screen.Splashes.Length; i++)
            {
                ref Splash splash = ref screen.Splashes[i];
                if (!splash.Active)
                    continue;
                int frame = Math.Min(SplashFrameCount - 1, (int)(splash.AgeSeconds / SplashSecondsPerFrame));
                var cell = new Rectangle(frame * SplashCellSize, 0, SplashCellSize, SplashCellSize);
                spriteBatch.Draw(_splashTexture, splash.Position, cell,
                    (splash.Green ? GreenRainSplashTint : SplashTint) * (0.62f * presence),
                    0f, origin, scale, SpriteEffects.None, 1f);
                screen.LastDrawnSplashes++;
            }
            spriteBatch.End();
        }

        // ---- generated art ----------------------------------------------------------------------

        private static Texture2D? _petalTexture;
        private static Texture2D? _leafTexture;
        private static Texture2D? _splashTexture;

        private static void EnsureTextures()
        {
            if (_streakTexture != null && !_streakTexture.IsDisposed)
                return;
            GraphicsDevice device = Game1.graphics.GraphicsDevice;

            // The wind pieces are white shapes tinted per season at draw time: a rounded petal
            // and a pointed leaf. Premultiplied, like everything else generated here.
            const int petalSize = 20;
            var petalPixels = new Color[petalSize * petalSize];
            for (int y = 0; y < petalSize; y++)
            {
                for (int x = 0; x < petalSize; x++)
                {
                    float px = ((x + 0.5f) / petalSize - 0.5f) / 0.42f;
                    float py = ((y + 0.5f) / petalSize - 0.5f) / 0.32f;
                    float edge = 1f - (px * px + py * py);
                    float alpha = Math.Clamp(edge * 3f, 0f, 1f);
                    byte level = (byte)(alpha * 255f);
                    petalPixels[y * petalSize + x] = new Color(level, level, level, level);
                }
            }
            _petalTexture = new Texture2D(device, petalSize, petalSize, false, SurfaceFormat.Color);
            _petalTexture.SetData(petalPixels);

            const int leafWidth = 32, leafHeight = 18;
            var leafPixels = new Color[leafWidth * leafHeight];
            for (int y = 0; y < leafHeight; y++)
            {
                for (int x = 0; x < leafWidth; x++)
                {
                    float along = (x + 0.5f) / leafWidth * 2f - 1f;          // -1 stem .. +1 tip
                    float across = (y + 0.5f) / leafHeight * 2f - 1f;
                    // Widest just behind the middle, drawn to a point at the tip.
                    float halfWidth = MathF.Sqrt(Math.Max(0f, 1f - along * along))
                        * (0.9f - 0.35f * (along + 1f) / 2f);
                    float edge = halfWidth - MathF.Abs(across);
                    float alpha = Math.Clamp(edge * 4f, 0f, 1f);
                    byte level = (byte)(alpha * 255f);
                    leafPixels[y * leafWidth + x] = new Color(level, level, level, level);
                }
            }
            _leafTexture = new Texture2D(device, leafWidth, leafHeight, false, SurfaceFormat.Color);
            _leafTexture.SetData(leafPixels);

            // A drop landing, in three frames: the crown at the moment of impact, then the ring
            // it leaves widening and thinning until it is gone. Drawn as ELLIPSES squashed to
            // about half height, because the camera looks along the ground rather than down at
            // it, and a circle here reads as a sticker lying on top of the world.
            int splashAtlasWidth = SplashCellSize * SplashFrameCount;
            var splashPixels = new Color[splashAtlasWidth * SplashCellSize];
            float[] ringRadius = { 0.20f, 0.52f, 0.84f };
            float[] ringThickness = { 0.13f, 0.085f, 0.055f };
            float[] ringStrength = { 1.00f, 0.72f, 0.40f };
            for (int frame = 0; frame < SplashFrameCount; frame++)
            {
                for (int y = 0; y < SplashCellSize; y++)
                {
                    for (int x = 0; x < SplashCellSize; x++)
                    {
                        float acrossSplash = (x + 0.5f) / SplashCellSize - 0.5f;
                        // The impact point sits low in the cell: the crown throws water UP.
                        float downSplash = ((y + 0.5f) / SplashCellSize - 0.62f) / 0.52f;
                        float ellipse = MathF.Sqrt(acrossSplash * acrossSplash * 4f + downSplash * downSplash);
                        float ring = MathF.Exp(-MathF.Pow((ellipse - ringRadius[frame]) / ringThickness[frame], 2f));
                        float alpha = ring * ringStrength[frame] * 0.85f;
                        // The crown: a short spike of water standing where the drop struck,
                        // only in the first frame, and a pair of thrown beads in the second.
                        if (frame == 0)
                        {
                            float crown = Math.Max(0f, 1f - MathF.Abs(acrossSplash) * 14f)
                                        * Math.Max(0f, 1f - MathF.Abs(downSplash + 0.35f) * 2.6f);
                            alpha = Math.Max(alpha, crown * 0.9f);
                        }
                        else if (frame == 1)
                        {
                            float beadLeft = Math.Max(0f, 1f - new Vector2(acrossSplash + 0.20f, downSplash + 0.55f).Length() * 9f);
                            float beadRight = Math.Max(0f, 1f - new Vector2(acrossSplash - 0.17f, downSplash + 0.62f).Length() * 10f);
                            alpha = Math.Max(alpha, Math.Max(beadLeft, beadRight) * 0.75f);
                        }
                        byte level = (byte)(Math.Clamp(alpha, 0f, 1f) * 255f);
                        splashPixels[y * splashAtlasWidth + frame * SplashCellSize + x] = new Color(level, level, level, level);
                    }
                }
            }
            _splashTexture = new Texture2D(device, splashAtlasWidth, SplashCellSize, false, SurfaceFormat.Color);
            _splashTexture.SetData(splashPixels);

            const int streakWidth = 12, streakHeight = 96;
            var streakPixels = new Color[streakWidth * streakHeight];
            for (int y = 0; y < streakHeight; y++)
            {
                float along = (y + 0.5f) / streakHeight;
                // Soft at both ends, brightest just above the head (the bottom, where the drop is).
                float lengthShape = MathF.Sin(MathF.PI * MathF.Pow(along, 0.72f));
                for (int x = 0; x < streakWidth; x++)
                {
                    float across = Math.Abs((x + 0.5f) / streakWidth - 0.5f) * 2f;
                    float acrossShape = Math.Max(0f, 1f - across * across);
                    float alpha = lengthShape * acrossShape;
                    byte level = (byte)(alpha * 255f);
                    streakPixels[y * streakWidth + x] = new Color(level, level, level, level);
                }
            }
            _streakTexture = new Texture2D(device, streakWidth, streakHeight, false, SurfaceFormat.Color);
            _streakTexture.SetData(streakPixels);

            const int flakeSize = 24;
            var flakePixels = new Color[flakeSize * flakeSize];
            for (int y = 0; y < flakeSize; y++)
            {
                for (int x = 0; x < flakeSize; x++)
                {
                    float dx = (x + 0.5f) / flakeSize - 0.5f;
                    float dy = (y + 0.5f) / flakeSize - 0.5f;
                    float radius = MathF.Sqrt(dx * dx + dy * dy) * 2f;
                    float falloff = Math.Max(0f, 1f - radius);
                    // A small solid core inside a soft skirt: reads as a flake at 4 px, not a blur.
                    float alpha = falloff * falloff * (0.55f + 0.45f * falloff);
                    byte level = (byte)(alpha * 255f);
                    flakePixels[y * flakeSize + x] = new Color(level, level, level, level);
                }
            }
            _flakeTexture = new Texture2D(device, flakeSize, flakeSize, false, SurfaceFormat.Color);
            _flakeTexture.SetData(flakePixels);
        }

        /// <summary>How much of our rain this screen is showing right now, 0..1, for the effects
        /// that thicken with it (the fog stage breathes up ~15% in our rain). Zero whenever the
        /// replacement is off, so vanilla days cannot change by even one fog wisp.</summary>
        internal static float ActiveRainPresence
        {
            get
            {
                if (!ReplacementWanted(out bool raining, out _, out _) || !raining)
                    return 0f;
                return _screens.TryGetValue(CurrentScreenId(), out ScreenPrecipitation? screen)
                    ? screen.Presence : 0f;
            }
        }

        // ---- diagnostics ------------------------------------------------------------------------

        /// <summary>One line for radiance_report: what the replacement is doing on this screen and
        /// why it might be doing nothing - the gate, the yield flag and the counts all in one place.</summary>
        internal static string Diag()
        {
            ModConfig? config = LiveConfig?.Invoke();
            if (config == null || !config.PrecipitationEnabled)
                return "precipitation: vanilla (replacement not switched on)";
            if (AnotherModOwnsWeatherDraw)
                return "precipitation: yielded (another mod patches drawWeather, or our draw failed once)";
            bool wanted = ReplacementWanted(out bool raining, out bool snowing, out bool windy);
            ScreenPrecipitation? screen = _screens.TryGetValue(CurrentScreenId(), out var s) ? s : null;
            string state = screen == null ? "idle (never drawn on this screen)"
                : $"presence={screen.Presence:0.000} storm={screen.StormEase:0.00} "
                + $"drawn rain={screen.LastDrawnRain} snow={screen.LastDrawnSnow} splashes={screen.LastDrawnSplashes} windPieces={screen.LastDrawnWind}";
            bool greenNow = raining && (Game1.currentLocation?.IsGreenRainingHere() ?? false);
            return $"precipitation: replacing={(wanted ? (greenNow ? "greenrain" : raining ? "rain" : snowing ? "snow" : "wind") : "nothing (gate closed)")} "
                + $"wind={_windPixelsPerSecond:0} px/s {state}";
        }
    }
}
