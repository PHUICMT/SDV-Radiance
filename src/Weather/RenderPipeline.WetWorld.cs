using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace SDVRadiance
{
    /// <summary>
    /// How wet the world is, kept on the game's own clock.
    ///
    /// <para>
    /// The game remembers exactly one thing about rain: whether it rained yesterday. There is no
    /// "it stopped forty minutes ago", so the drying ground has to carry its own clock. One
    /// scalar per location context (the Desert's context never rains, so its entry never rises -
    /// no special case), advanced on GAME minutes rather than frames so a menu or a pause does
    /// not dry the ground, rising over ~30 game-minutes of rain and drying over ~120 after.
    /// </para>
    ///
    /// <para>
    /// Snow deliberately does not wet: frozen ground does not glisten, and winter's look already
    /// has its own machinery. Green rain wets automatically, because the game keeps IsRaining
    /// true underneath it.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Full soak after ~30 game-minutes of rain (visible within the first minute).</summary>
        private const float WetRiseGameMinutes = 30f;
        /// <summary>Dry over ~120 game-minutes - noticed as a state, gone before it reads as a bug.</summary>
        private const float WetDecayGameMinutes = 120f;
        /// <summary>The morning after a rainy day starts half-wet (the game's own flag survives
        /// the save, so this needs no storage of ours).</summary>
        private const float MorningAfterWetness = 0.5f;
        /// <summary>Standing water fills slower than the ground darkens and dries slowest of all
        /// (Lagarde: puddles outlive the general wetness, which is what the street looks like
        /// an hour after the rain stops).</summary>
        private const float PuddleRiseGameMinutes = 45f;
        private const float PuddleDecayGameMinutes = 200f;
        /// <summary>How fast wet ground and puddles go once snow lies on the ground (winter outdoors):
        /// a short, visible fade rather than the hours of ordinary drying.</summary>
        private const float SnowCoversGameMinutes = 10f;
        private const float MorningAfterPuddles = 0.6f;

        /// <summary>Standing water per location context, beside the wetness.</summary>
        private static readonly Dictionary<string, float> _puddlesByContext = [];

        /// <summary>Wetness per location context id. Shared world truth, deliberately NOT
        /// per-screen: both halves of a split screen stand in the same world.</summary>
        private static readonly Dictionary<string, float> _wetnessByContext = [];
        private static float _wetnessLastMinutes = -1f;
        private static int _wetnessSteppedTick = -1;
        private static int _wetnessSeededDay = -1;

        /// <summary>This screen's presence fade for the wet pass (saved in ScreenState). The
        /// wetness scalar is the slow world truth; this is the ~0.5 s screen-side ease that
        /// covers config toggles and warping indoors, so nothing pops.</summary>
        // moved to ScreenState (see RenderPipeline.Screens.cs)

        /// <summary>The wetness of the context the player is standing in, 0..1.</summary>
        internal static float WetnessNow
        {
            get
            {
                string? contextId = Game1.currentLocation?.GetLocationContextId();
                return contextId != null && _wetnessByContext.TryGetValue(contextId, out float wetness)
                    ? wetness : 0f;
            }
        }

        /// <summary>How much standing water there is where the player stands, 0..1: its own slow
        /// clock (see <see cref="PuddleRiseGameMinutes"/>).</summary>
        internal static float PuddleAmountNow
        {
            get
            {
                string? contextId = Game1.currentLocation?.GetLocationContextId();
                return contextId != null && _puddlesByContext.TryGetValue(contextId, out float level) ? level : 0f;
            }
        }

        /// <summary>radiance_wet: set the wetness and the standing water where the player stands.</summary>
        internal static void SetWetnessHere(float wetness, float puddles)
        {
            string? contextId = Game1.currentLocation?.GetLocationContextId();
            if (contextId == null)
                return;
            _wetnessByContext[contextId] = Math.Clamp(wetness, 0f, 1f);
            _puddlesByContext[contextId] = Math.Clamp(puddles, 0f, 1f);
        }

        /// <summary>The shine on wet ground: it goes before the darkening does (Lagarde: specular
        /// fades faster than diffuse darkening, so drying ground turns matte while still dark).</summary>
        internal static float GlossNow
        {
            get
            {
                float wetness = WetnessNow;
                float share = Math.Clamp((wetness - 0.3f) / 0.7f, 0f, 1f);
                return share * share * (3f - 2f * share);
            }
        }

        /// <summary>
        /// Advance the current context's wetness. Once per tick, on game minutes.
        ///
        /// <para>Only the context being played advances; one that was left hours ago catches up
        /// on re-entry using its weather NOW as the stand-in for the gap, which is right whenever
        /// weather is per-day (always, outside debug commands) and merely approximate for the
        /// frame a debug flip happens off-context.</para>
        /// </summary>
        private static void AdvanceWetness()
        {
            if (SharedTicks.Now == _wetnessSteppedTick || Determinism.Frozen)
                return;
            _wetnessSteppedTick = SharedTicks.Now;

            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return;
            string contextId = location.GetLocationContextId();
            _wetnessByContext.TryGetValue(contextId, out float wetness);
            _puddlesByContext.TryGetValue(contextId, out float puddles);
            // Winter ground outdoors is under snow: no puddles and no wet shine on it. Asked of the
            // location's own season, so Ginger Island (always summer) keeps its rain. What was wet
            // before the snow (the last rain of fall, kept overnight) goes over a short spell rather
            // than the slow drying, so day one of winter does not open on puddles; never at once.
            bool snowOnTheGround = location.IsOutdoors && location.GetSeason() == Season.Winter;

            // The morning after a rainy day starts half-wet. Seeded once per day, before the
            // delta step, so day one of sunshine still begins with drying ground.
            if (_wetnessSeededDay != Game1.Date.TotalDays)
            {
                _wetnessSeededDay = Game1.Date.TotalDays;
                _wetnessLastMinutes = -1f;
                if (Game1.wasRainingYesterday && !snowOnTheGround)
                {
                    wetness = Math.Max(wetness, MorningAfterWetness);
                    puddles = Math.Max(puddles, MorningAfterPuddles);
                }
            }

            float minutes = GameClock.MinutesNow();
            float elapsed = _wetnessLastMinutes < 0f ? 0f : minutes - _wetnessLastMinutes;
            _wetnessLastMinutes = minutes;
            if (elapsed < 0f)
                elapsed = 0f;   // clock wound backwards (debug time) - hold rather than guess
            elapsed = Math.Min(elapsed, 60f);

            bool rainingHere = LocalSky.RainLandsOn(location) && !snowOnTheGround;
            // under snow the ground also dries with the real seconds, so a wound or paused clock
            // cannot hold puddles on it
            float snowDrying = snowOnTheGround ? Math.Max(elapsed, (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds) : 0f;
            wetness = rainingHere
                ? Math.Min(1f, wetness + elapsed / WetRiseGameMinutes)
                : Math.Max(0f, wetness - (snowOnTheGround ? snowDrying / SnowCoversGameMinutes : elapsed / WetDecayGameMinutes));
            _wetnessByContext[contextId] = wetness;
            puddles = rainingHere
                ? Math.Min(1f, puddles + elapsed / PuddleRiseGameMinutes)
                : Math.Max(0f, puddles - (snowOnTheGround ? snowDrying / SnowCoversGameMinutes : elapsed / PuddleDecayGameMinutes));
            _puddlesByContext[contextId] = puddles;
        }

        // ---- the wet pass -----------------------------------------------------------------------

        /// <summary>Set at GameLaunched. Dynamic Reflections owns puddles when it is installed;
        /// our dampness and streaks have no counterpart there and stay on.</summary>
        internal static bool DynamicReflectionsPresent;

        private Effect? _wetEffect;
        private Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>? _wetStageDelegate;
        /// <summary>True while the wet pass wants the flipped-entity mirror baked even where no
        /// water is on screen: puddles are on, the ground is wet enough to hold one, and Dynamic
        /// Reflections is not already doing this job. Read by the entity-mirror culls, which were
        /// written when "near water" was the only reason to mirror anybody.</summary>
        private bool _wetPuddleMirrorWanted;
        internal bool WetWorldWantsEntityMirror => _wetPuddleMirrorWanted;
        /// <summary>Puddles mirror whatever stands behind them straight from the frame, walking up
        /// the screen to its base: buildings, trees and people alike, where the flipped-entity
        /// layer had only sprites. Up to 48 steps of three reads per puddle pixel, which a phone's
        /// half-precision GPU is not given; it keeps the entity layer.</summary>
        private static bool WetPuddlesMirrorTheScene => StardewModdingAPI.Constants.TargetPlatform != StardewModdingAPI.GamePlatform.Android;
        /// <summary>True while the wet pass draws at all. Its puddles and lamp streaks lie on the
        /// ground, so every sprite on screen has to be in the sprite mask to stand in front of
        /// them, not only the ones near water the mask was written for.</summary>
        private bool _wetWantsSpriteMask;
        internal bool WetWorldWantsSpriteMask => _wetWantsSpriteMask;
        /// <summary>Either wet reason to walk the whole screen instead of the water's box.</summary>
        private bool WetWantsEverySprite => _wetPuddleMirrorWanted || _wetWantsSpriteMask;

        // Everything from here to the texture pair's resting half belongs to one screen:
        // RenderPipeline.Screens.cs says why.
        private ref Texture2D? _wetSuitabilityTexture => ref _screen.WetSuitabilityTexture;
        private ref GameLocation? _wetSuitabilityLocation => ref _screen.WetSuitabilityLocation;
        private ref Vector2 _wetSuitabilityMapTiles => ref _screen.WetSuitabilityMapTiles;
        /// <summary>What the suitability was built against: placing or picking up an object
        /// changes which tiles may pool, so a changed count rebuilds the texture.</summary>
        private ref int _wetSuitabilityObjectCount => ref _screen.WetSuitabilityObjectCount;
        private ref int _wetSuitabilityFurnitureCount => ref _screen.WetSuitabilityFurnitureCount;
        /// <summary>The map's own answer, one byte per tile, before any placed thing is vetoed:
        /// what the ground is and whether art covers it. Built once per location (and again when
        /// its SurfaceMap is replaced, which is what a building placed or removed does), because
        /// nothing a player can place changes it, and it is the expensive half: every tile of
        /// the map asked twice through the map's property lookup. Picking up one stone used to
        /// pay for that whole walk again.</summary>
        private ref byte[]? _wetGroundCells => ref _screen.WetGroundCells;
        private ref GameLocation? _wetGroundCellsLocation => ref _screen.WetGroundCellsLocation;
        private ref SurfaceMap? _wetGroundCellsSurface => ref _screen.WetGroundCellsSurface;
        /// <summary>The ground cells with the placed things vetoed: what the texture holds.
        /// Reused between rebuilds; only its contents change.</summary>
        private ref byte[]? _wetSuitabilityCells => ref _screen.WetSuitabilityCells;
        /// <summary>What was last handed to the card. Most count changes do not move a single
        /// texel: a chest placed on a wooden floor, a torch on grass, anything on a tile that was
        /// never going to pool. Comparing five kilobytes costs microseconds and the upload it
        /// skips was the expensive half of the rebuild here.</summary>
        private ref byte[]? _wetSuitabilityUploaded => ref _screen.WetSuitabilityUploaded;
        /// <summary>The texture pair's resting half (<see cref="TextureDoubleBuffer"/>), so a
        /// rebuild never writes into the texture the card may still be reading.</summary>
        private ref Texture2D? _wetSuitabilitySpare => ref _screen.WetSuitabilitySpare;

        /// <summary>
        /// Draw the wet-ground pass: source in, dampened scene out. Sits after water (puddles on
        /// the frame the ripple just left) and before cloud shadows (overcast shade lands on
        /// already-wet ground), so bloom and the grade both see the wet look.
        /// </summary>
        private void RenderWetWorld(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            long started = FrameCost.Begin(FrameCost.Part.WetWorld);
            var effect = _wetEffect!;
            EnsureWetSuitability();
            GetParam(effect, "TileTexture")?.SetValue(_wetSuitabilityTexture);
            GetParam(effect, "RoomTexture")?.SetValue(_wetRoomTexture);
            GetParam(effect, "MapSizeTiles")?.SetValue(_wetSuitabilityMapTiles);
            // Fresh viewport mapping of its own: the water mask's copy is only refreshed while
            // the water machinery runs, and this pass must work on a lake-less farm in the rain.
            GetParam(effect, "TilesPerScreen")?.SetValue(new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f));
            GetParam(effect, "WorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            bool sdfUsable = _waterSignedDistanceTexture is { IsDisposed: false };
            GetParam(effect, "SdfValid")?.SetValue(sdfUsable ? 1f : 0f);
            if (sdfUsable)
            {
                GetParam(effect, "SdfTexture")?.SetValue(_waterSignedDistanceTexture);
                GetParam(effect, "MaskOrigin")?.SetValue(new Vector2(_lastWaterTileX, _lastWaterTileY));
                GetParam(effect, "MaskSize")?.SetValue(_waterMaskPixelSize);
            }
            GetParam(effect, "Wetness")?.SetValue(WetnessNow);
            GetParam(effect, "Gloss")?.SetValue(GlossNow);
            GetParam(effect, "PuddleLevel")?.SetValue(PuddleAmountNow);
            // How hard it is raining now, eased so a storm arriving or the rain stopping never
            // switches the rings and the broken streaks on or off in one frame.
            float rainTarget = LocalSky.RainLandsHere ? (Game1.isLightning ? 1f : 0.65f) : 0f;
            Approach(ref _wetRainEase, rainTarget, 0.02f);
            GetParam(effect, "Rain")?.SetValue(_wetRainEase);
            GetParam(effect, "Strength")?.SetValue(config.WetWorldStrength);
            // Dynamic Reflections draws puddle sprites of its own on the same tile rule; two
            // sets of pools on one plaza reads as a bug in whichever mod the player blames.
            GetParam(effect, "PuddleCoverage")?.SetValue(DynamicReflectionsPresent ? 0f : config.WetWorldPuddles);
            GetParam(effect, "PuddleMirror")?.SetValue(config.WetWorldPuddleReflection);
            GetParam(effect, "StreakAmount")?.SetValue(config.WetWorldLightStreaks);
            GetParam(effect, "StreakStone")?.SetValue(config.WetWorldLightStreakStone);
            GetParam(effect, "StreakRipples")?.SetValue(config.WetWorldLightStreakRipples);
            GetParam(effect, "DryLampLight")?.SetValue(DryLampLightWanted(config) ? config.WetWorldDryLampLight : 0f);
            GetParam(effect, "SceneMirror")?.SetValue(WetPuddlesMirrorTheScene ? 1f : 0f);
            GetParam(effect, "PixelLook")?.SetValue(config.WetWorldPuddleLook == WetPuddleLook.Pixel ? 1f : 0f);
            bool spriteMaskUsable = SpriteMaskReady && _spriteMaskRenderTarget is { IsDisposed: false };
            GetParam(effect, "SpriteMaskOn")?.SetValue(spriteMaskUsable ? 1f : 0f);
            if (spriteMaskUsable)
                GetParam(effect, "SpriteMaskTexture")?.SetValue(_spriteMaskRenderTarget);
            // The player is not in that mask; the water reads their own bake, and so does this.
            SetPlayerExclusionParams(effect, Game1.player);
            var (sunWarm, nightGlow) = TimeOfDayAmounts();
            Vector3 skyTint = SynthesisedSkyColour(sunWarm, nightGlow)
                * Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting);
            GetParam(effect, "SkyTint")?.SetValue(skyTint);
            GetParam(effect, "NightGlow")?.SetValue(nightGlow);
            float seconds = (float)(Determinism.Seconds % 3600.0);
            GetParam(effect, "Seconds")?.SetValue(seconds);
            // The clouds in the puddles drift with the wind, a few art pixels a second.
            float windArtPixels = PrecipitationSystem.WindPixelsPerSecond / 4f;
            GetParam(effect, "CloudDrift")?.SetValue(new Vector2(seconds * (2f + windArtPixels), seconds * 0.6f));
            // A rainy day is dark enough for the lamps and a glow ring to show on wet ground, a
            // little less than night does.
            float streakLight = Math.Max(nightGlow, 0.8f * _wetRainEase);
            GetParam(effect, "StreakLight")?.SetValue(streakLight);
            DrawWetStreaks(spriteBatch, dest, streakLight, config);
            GetParam(effect, "StreakTexture")?.SetValue(_wetStreakTarget);
            bool mirrorUsable = ReflectRTReady && _reflectionRenderTarget is { IsDisposed: false };
            GetParam(effect, "ReflectOn")?.SetValue(mirrorUsable ? 1f : 0f);
            if (mirrorUsable)
            {
                GetParam(effect, "ReflectTexture")?.SetValue(_reflectionRenderTarget);
                GetParam(effect, "MirrorTint")?.SetValue(new Vector3(0.80f, 0.86f, 0.96f));
            }
            // Bone dry, only the lamps' dry streak can show: the pass that draws just that (the whole
            // wet look run for a dry night was 0.3 ms a frame on a fast card).
            bool boneDry = WetnessNow <= 0f && PuddleAmountNow <= 0f && _wetRainEase <= 0f;
            effect.CurrentTechnique = (boneDry ? effect.Techniques["WetDryLamps"] : null) ?? effect.Techniques["WetWorld"];
            DrawFull(spriteBatch, source, dest, effect);
            BlendBackSource(spriteBatch, source, dest, _fadeWet);
            FrameCost.End(FrameCost.Part.WetWorld, started);
        }

        private float _wetRainEase;

        /// <summary>Whether the lamps leave their faint streak and pool on dry ground here and now:
        /// after dark, and not on snow, which the wet pass leaves alone.</summary>
        private static bool DryLampLightWanted(ModConfig config)
        {
            GameLocation? location = Game1.currentLocation;
            return config.WetWorldDryLampLight > 0f && TimeOfDayAmounts().NightGlow > 0f && location != null
                && !(location.IsOutdoors && location.GetSeason() == Season.Winter);
        }
        private int _wetLightCount;

        /// <summary>
        /// The lamps that leave a streak on the wet ground: up to eight lit lights near the screen,
        /// in world tiles, with the colour they glow (the game stores a light's colour inverted).
        /// </summary>
        /// <summary>
        /// Draw every lit light's streak into a picture of its own, one soft sprite each, for the
        /// wet pass to lay on the ground where it is wet. Any number of lights: a lamp costs what
        /// its streak covers. The shader once held eight in an array, and the ninth lamp on a
        /// plaza of fifteen threw nothing, while sixteen would have cost four times the eight.
        /// </summary>
        private void DrawWetStreaks(SpriteBatch spriteBatch, RenderTarget2D dest, float streakLight, ModConfig config)
        {
            int width = Math.Max(1, dest.Width / 2), height = Math.Max(1, dest.Height / 2);
            if (_wetStreakTarget == null || _wetStreakTarget.IsDisposed || _wetStreakTarget.Width != width || _wetStreakTarget.Height != height)
            {
                _wetStreakTarget?.Dispose();
                _wetStreakTarget = VramTally.Track(new RenderTarget2D(_device, width, height, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.DiscardContents), "wet ground lamp streaks");
            }
            // Rebuilt only when the glow dial moves: a 128 by 64 picture.
            if (_wetStreakGradient == null || _wetStreakGradient.IsDisposed || _wetStreakGradientGlow != config.WetWorldLightStreakGlow
                || _wetStreakGradientTaper != config.WetWorldLightStreakTaper)
            {
                _wetStreakGradient?.Dispose();
                _wetStreakGradient = BuildStreakGradient(_device, config.WetWorldLightStreakGlow, config.WetWorldLightStreakTaper);
                _wetStreakGradientGlow = config.WetWorldLightStreakGlow;
                _wetStreakGradientTaper = config.WetWorldLightStreakTaper;
            }
            _wetPoolGradient ??= BuildPoolGradient(_device);
            CollectWetLights(streakLight);
            _device.SetRenderTarget(_wetStreakTarget);
            _device.Clear(Color.Transparent);
            if (_wetLightCandidates.Count > 0)
            {
                float pixelsPerTile = width / (Game1.viewport.Width / 64f);
                Vector2 viewTile = new(Game1.viewport.X / 64f, Game1.viewport.Y / 64f);
                float angle = MathHelper.ToRadians(config.WetWorldLightStreakAngle);
                Vector2 direction = new(MathF.Sin(angle), MathF.Cos(angle));
                // Longer in harder rain, as the drops smear the reflection down the street.
                float naturalTiles = (1.5f + 2.2f * _wetRainEase) * config.WetWorldLightStreakLength;
                const float AcrossTiles = 0.16f * StreakGradientSpan * 2f;   // the gradient spans StreakGradientSpan widths either side
                spriteBatch.Begin(SpriteSortMode.Deferred, StreakAddBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
                foreach (var candidate in _wetLightCandidates)
                {
                    Vector4 light = candidate.Light;
                    float lengthTiles = naturalTiles * light.Z;
                    // A carried light's streak is already bright at the feet the body covers, so
                    // its fade-in starts a little above them.
                    Vector2 footTile = new Vector2(light.X, light.Y + light.W) - (light.W < 0.5f ? direction * (0.1f * lengthTiles) : Vector2.Zero);
                    Vector2 at = (footTile - viewTile) * pixelsPerTile;
                    Vector3 tint = candidate.Colour;
                    // A white street lamp warmed toward sodium orange; a light that already has a
                    // colour keeps it, in proportion to how much colour it has.
                    if (light.W > 0.5f && config.WetWorldLightStreakWarmth > 0f)
                    {
                        float brightest = Math.Max(tint.X, Math.Max(tint.Y, tint.Z));
                        float saturation = brightest > 0f ? (brightest - Math.Min(tint.X, Math.Min(tint.Y, tint.Z))) / brightest : 0f;
                        tint = Vector3.Lerp(tint, tint * new Vector3(1f, 0.74f, 0.44f), config.WetWorldLightStreakWarmth * (1f - saturation));
                    }
                    float lum = Vector3.Dot(tint, new Vector3(0.299f, 0.587f, 0.114f));
                    tint = Vector3.Max(Vector3.Lerp(new Vector3(lum), tint, 1.35f), Vector3.Zero) * 0.9f;   // saturated, the way a wet street shows a lamp
                    spriteBatch.Draw(_wetStreakGradient, at, null, new Color(tint), -angle,
                        new Vector2(StreakGradientWidth / 2f, 0f),
                        new Vector2(AcrossTiles * pixelsPerTile / StreakGradientWidth, lengthTiles * pixelsPerTile / StreakGradientHeight),
                        SpriteEffects.None, 0f);
                    // The sheen round a lamp's foot, where it shines down on the wet street: a low
                    // ellipse, flattened as the ground is seen at an angle. Not for a carried light,
                    // whose pool would sit under the body.
                    if (light.W > 0.5f && config.WetWorldLightPool > 0f)
                    {
                        const float PoolAcrossTiles = 2.2f, PoolDownTiles = 1.1f;
                        spriteBatch.Draw(_wetPoolGradient, at, null, new Color(tint * (0.55f * config.WetWorldLightPool)), 0f,
                            new Vector2(PoolGradientSize / 2f, PoolGradientSize / 2f),
                            new Vector2(PoolAcrossTiles * pixelsPerTile / PoolGradientSize, PoolDownTiles * pixelsPerTile / PoolGradientSize),
                            SpriteEffects.None, 0f);
                    }
                }
                spriteBatch.End();
            }
            _wetLightCount = _wetLightCandidates.Count;
        }

        private RenderTarget2D? _wetStreakTarget;
        private Texture2D? _wetStreakGradient;
        private float _wetStreakGradientGlow = -1f;
        private float _wetStreakGradientTaper = -1f;
        private Texture2D? _wetPoolGradient;
        private const int PoolGradientSize = 64;

        /// <summary>A soft round pool of light, brightest at the middle and gone by the rim.</summary>
        private static Texture2D BuildPoolGradient(GraphicsDevice device)
        {
            var pixels = new Color[PoolGradientSize * PoolGradientSize];
            for (int y = 0; y < PoolGradientSize; y++)
                for (int x = 0; x < PoolGradientSize; x++)
                {
                    float dx = (x + 0.5f) / PoolGradientSize * 2f - 1f, dy = (y + 0.5f) / PoolGradientSize * 2f - 1f;
                    float distance = MathF.Sqrt(dx * dx + dy * dy);
                    float value = MathF.Pow(Math.Clamp(1f - distance, 0f, 1f), 2.2f);
                    byte level = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
                    pixels[y * PoolGradientSize + x] = new Color(level, level, level, level);
                }
            var texture = new Texture2D(device, PoolGradientSize, PoolGradientSize, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            return texture;
        }
        private const int StreakGradientWidth = 128, StreakGradientHeight = 64;
        /// <summary>How many core widths the picture reaches either side, room for the glow.</summary>
        private const float StreakGradientSpan = 7f;
        private static readonly BlendState StreakAddBlend = new()
        {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One,
        };

        /// <summary>One streak, lying down the texture: a bright line across, growing in at the top
        /// (the lamp's foot) and fading to nothing at the bottom, inside a soft glow. Wet stone is
        /// rough, so a lamp shows on it as a smear of light round the sharp reflection, wider and
        /// fainter, that starts at the foot and lingers further down the street.</summary>
        private static Texture2D BuildStreakGradient(GraphicsDevice device, float glow, float taper)
        {
            var pixels = new Color[StreakGradientWidth * StreakGradientHeight];
            for (int y = 0; y < StreakGradientHeight; y++)
            {
                float along = (y + 0.5f) / StreakGradientHeight;
                float coreLengthwise = Math.Clamp(along / 0.1f, 0f, 1f) * (1f - along);
                float glowLengthwise = Math.Clamp(along / 0.04f, 0f, 1f) * MathF.Pow(1f - along, 0.6f);
                for (int x = 0; x < StreakGradientWidth; x++)
                {
                    float across = ((x + 0.5f) / StreakGradientWidth * 2f - 1f) * StreakGradientSpan;
                    // Taper: a bright spot at the foot, the line opening out and fading as it runs.
                    float opening = 1f + taper * 1.6f * along;
                    float acrossOpened = across / opening;
                    float core = MathF.Exp(-acrossOpened * acrossOpened) * coreLengthwise / (1f + taper * 0.6f * along);
                    float footAlong = along / 0.07f;
                    core += taper * 0.8f * MathF.Exp(-footAlong * footAlong - across * across * 0.8f);
                    float spread = across / 3.2f;
                    float halo = 0.42f * glow * MathF.Exp(-spread * spread) * glowLengthwise;
                    float value = Math.Min(1f, core + halo);
                    byte level = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
                    pixels[y * StreakGradientWidth + x] = new Color(level, level, level, level);
                }
            }
            var texture = new Texture2D(device, StreakGradientWidth, StreakGradientHeight, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            return texture;
        }

        /// <summary>How far below a light the ground it stands on begins, in tiles. A street lamp's
        /// light sits in its head, and the post runs down two tiles of art below it; the streak
        /// belongs where the post meets the street, not halfway up it. Walks down through the
        /// tiles that carry art over the ground (the post's Buildings and Front tiles).</summary>
        private static float TilesDownToGround(GameLocation location, Vector2 lightPixels)
        {
            int tileX = (int)(lightPixels.X / 64f), tileY = (int)(lightPixels.Y / 64f);
            float withinTile = lightPixels.Y / 64f - tileY;
            int below = 0;
            for (int step = 0; step <= 3; step++)
            {
                int y = tileY + step;
                bool art = location.getTileIndexAt(tileX, y, "Buildings") >= 0 || location.getTileIndexAt(tileX, y, "Front") >= 0;
                if (!art)
                    break;
                below = step + 1;
            }
            // No post found (a lantern on a table, a torch on the ground): a tile, as before.
            return below == 0 ? 1f : below - withinTile;
        }

        private static bool IsBeachArt(GameLocation location, int x, int y)
        {
            xTile.Layers.Layer? back = location.map?.GetLayer("Back");
            if (back == null || x >= back.LayerWidth || y >= back.LayerHeight)
                return false;
            string? source = back.Tiles[x, y]?.TileSheet?.ImageSource;
            return source != null && source.Contains("beach", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasArtOn(GameLocation location, string layerId, int x, int y)
        {
            xTile.Layers.Layer? layer = location.map?.GetLayer(layerId);
            return layer != null && x < layer.LayerWidth && y < layer.LayerHeight && layer.Tiles[x, y] != null;
        }

        /// <summary>The lit lights near the screen whose streaks can land on it.</summary>
        private void CollectWetLights(float nightGlow)
        {
            GameLocation? location = Game1.currentLocation;
            _wetLightCandidates.Clear();
            if (nightGlow <= 0f || location == null || Game1.currentLightSources == null)
                return;
            float left = Game1.viewport.X - 256f, right = Game1.viewport.X + Game1.viewport.Width + 256f;
            float top = Game1.viewport.Y - 384f, bottom = Game1.viewport.Y + Game1.viewport.Height + 64f;
            Farmer player = Game1.player;
            Vector2 playerFeet = player.StandingPixel.ToVector2();
            foreach (var pair in Game1.currentLightSources)
            {
                LightSource light = pair.Value;
                Vector2 position = light.position.Value;
                if (position.X < left || position.X > right || position.Y < top || position.Y > bottom)
                    continue;
                if (!ShadowRenderer.WindowGlowing(location, light))
                    continue;
                Color stored = light.color.Value;
                var glow = new Vector3(1f - stored.R / 255f, 1f - stored.G / 255f, 1f - stored.B / 255f);
                if (glow.LengthSquared() < 0.01f)
                    glow = Vector3.One;
                // A bigger light throws a longer streak, within reason: a glow ring is radius 10.
                float lengthScale = Math.Clamp(0.6f + 0.25f * light.radius.Value, 0.6f, 1.4f);
                // A lamp stands about a tile above the ground its reflection starts on. A light
                // somebody carries is on them: the reflection starts at their feet, and for your
                // own light the feet are known exactly rather than read off the light's offset.
                bool carried = FloodLightmap.IsCarriedLight(pair.Key, light);
                float lift = carried ? 0f : TilesDownToGround(location, position);
                if (carried)
                {
                    lift = 0f;
                    if (light.PlayerID == player.UniqueMultiplayerID)
                        position = playerFeet;
                }
                float priority = carried ? -1f : Vector2.DistanceSquared(position, playerFeet);
                _wetLightCandidates.Add((priority, new Vector4(position.X / 64f, position.Y / 64f, lengthScale, lift), glow));
            }
            _wetLightCandidates.Sort(NearestLightFirst);
        }

        private readonly List<(float Priority, Vector4 Light, Vector3 Colour)> _wetLightCandidates = [];
        private static readonly Comparison<(float Priority, Vector4 Light, Vector3 Colour)> NearestLightFirst =
            (first, second) => first.Priority.CompareTo(second.Priority);

        /// <summary>The puddle room field (Alpha8, <see cref="RoomSamplesPerTile"/> to a tile): how far
        /// each point lies inside ground that can hold water, in tiles / 8.</summary>
        private ref Texture2D? _wetRoomTexture => ref _screen.WetRoomTexture;
        private ref byte[]? _wetRoomCells => ref _screen.WetRoomCells;
        private const int RoomSamplesPerTile = 4;

        /// <summary>
        /// One texel per tile for the whole map (Color): R how porous, G how glossy, B whether it can
        /// hold a puddle, A how much rain reaches it. The map's half (<see cref="BuildWetGroundCells"/>)
        /// is built once per location from the SurfaceMap's classes plus the map's own vocabulary;
        /// placed things, farm buildings and the shelter of trees are laid on top whenever their
        /// counts change. The puddle room field is worked out from the final B channel.
        /// </summary>
        private void EnsureWetSuitability()
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return;
            int buildingCount = location.buildings.Count + location.resourceClumps.Count * 100;
            int featureCount = location.terrainFeatures.Count();
            if (ReferenceEquals(location, _wetSuitabilityLocation)
                && _wetSuitabilityTexture is { IsDisposed: false } && _wetRoomTexture is { IsDisposed: false }
                && location.objects.Length == _wetSuitabilityObjectCount
                && location.furniture.Count == _wetSuitabilityFurnitureCount
                && buildingCount + featureCount * 1000 == _wetSuitabilityShelterCount)
                return;
            SurfaceMap? surface = SurfaceMap.For(location);
            if (surface == null)
                return;
            long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            int width = surface.Width, height = surface.Height;
            if (_wetGroundCells == null || _wetGroundCells.Length != width * height * 4
                || !ReferenceEquals(location, _wetGroundCellsLocation) || !ReferenceEquals(surface, _wetGroundCellsSurface))
            {
                _wetGroundCells = BuildWetGroundCells(location, surface);
                _wetGroundCellsLocation = location;
                _wetGroundCellsSurface = surface;
                phaseStart = PhaseCost.NoteSince("wet ground: map cells (every tile asked)", phaseStart);
            }
            _wetSuitabilityLocation = location;
            _wetSuitabilityObjectCount = location.objects.Length;
            _wetSuitabilityFurnitureCount = location.furniture.Count;
            _wetSuitabilityShelterCount = buildingCount + featureCount * 1000;
            if (_wetSuitabilityCells == null || _wetSuitabilityCells.Length != _wetGroundCells.Length)
                _wetSuitabilityCells = new byte[_wetGroundCells.Length];
            byte[] cells = _wetSuitabilityCells;
            Buffer.BlockCopy(_wetGroundCells, 0, cells, 0, cells.Length);
            void Dry(int tileX, int tileY)
            {
                if (tileX >= 0 && tileY >= 0 && tileX < width && tileY < height)
                {
                    int index = (tileY * width + tileX) * 4;
                    cells[index] = cells[index + 1] = cells[index + 2] = cells[index + 3] = 0;
                }
            }
            // Placed things keep a pool off their tile (a pool painted across a keg or a fence
            // reads as a leak in the world) but not the rain: a stone, a weed or a twig gets as
            // wet as the grass round it, and drying their tiles left a checkerboard of light
            // squares across a farm full of debris.
            foreach (var pair in location.objects.Pairs)
            {
                int tileX = (int)pair.Key.X, tileY = (int)pair.Key.Y;
                if (tileX >= 0 && tileY >= 0 && tileX < width && tileY < height)
                    cells[(tileY * width + tileX) * 4 + 2] = 0;
            }
            foreach (StardewValley.Objects.Furniture piece in location.furniture)
            {
                Rectangle box = piece.boundingBox.Value;
                for (int tileY = box.Top / 64; tileY <= (box.Bottom - 1) / 64; tileY++)
                    for (int tileX = box.Left / 64; tileX <= (box.Right - 1) / 64; tileX++)
                        Dry(tileX, tileY);
            }
            // A farm building is a sprite, not map tiles: its footprint and every row of roof and
            // wall the sprite stands up over, or the coop's roof collected puddles.
            foreach (StardewValley.Buildings.Building building in location.buildings)
            {
                int spriteRows = building.tilesHigh.Value;
                try
                {
                    spriteRows = Math.Max(spriteRows, (int)Math.Ceiling(building.getSourceRect().Height * 4f / 64f));
                }
                catch (Exception)
                {
                    // A modded building that cannot report its art: the footprint alone.
                }
                int bottom = building.tileY.Value + building.tilesHigh.Value;
                for (int tileY = bottom - spriteRows; tileY < bottom; tileY++)
                    for (int tileX = building.tileX.Value; tileX < building.tileX.Value + building.tilesWide.Value; tileX++)
                        Dry(tileX, tileY);
            }
            // The farm's grass is a terrain feature on dirt tiles, and a crop stands in hoed dirt:
            // neither holds a pool, and grass takes the rain as grass does.
            foreach (var pair in location.terrainFeatures.Pairs)
            {
                int featureX = (int)pair.Key.X, featureY = (int)pair.Key.Y;
                if (featureX < 0 || featureY < 0 || featureX >= width || featureY >= height)
                    continue;
                int featureIndex = (featureY * width + featureX) * 4;
                if (pair.Value is Grass)
                {
                    cells[featureIndex] = Math.Min(cells[featureIndex], (byte)150);
                    cells[featureIndex + 1] = Math.Min(cells[featureIndex + 1], (byte)90);
                    cells[featureIndex + 2] = 0;
                }
                else if (pair.Value is HoeDirt { crop: not null })
                    cells[featureIndex + 2] = 0;
            }
            foreach (LargeTerrainFeature feature in location.largeTerrainFeatures)
            {
                int bushX = (int)feature.Tile.X, bushY = (int)feature.Tile.Y;
                for (int tileX = bushX; tileX <= bushX + 2; tileX++)
                    if (tileX >= 0 && bushY >= 0 && tileX < width && bushY < height)
                        cells[(bushY * width + tileX) * 4 + 2] = 0;
            }
            // A stump, a boulder or a log stands on the ground over its whole footprint: no pool
            // under it, or the water was laid across the stump as if it were floor.
            foreach (StardewValley.TerrainFeatures.ResourceClump clump in location.resourceClumps)
            {
                int clumpX = (int)clump.Tile.X, clumpY = (int)clump.Tile.Y;
                for (int tileY = clumpY; tileY < clumpY + clump.height.Value; tileY++)
                    for (int tileX = clumpX; tileX < clumpX + clump.width.Value; tileX++)
                        if (tileX >= 0 && tileY >= 0 && tileX < width && tileY < height)
                            cells[(tileY * width + tileX) * 4 + 2] = 0;
            }
            // The ground beside a tree's trunk is sheltered by the canopy: wet a little, never pooling.
            foreach (var pair in location.terrainFeatures.Pairs)
            {
                bool grownTree = pair.Value is Tree { growthStage.Value: >= 5 } or FruitTree { growthStage.Value: >= 4 };
                if (!grownTree)
                    continue;
                int treeX = (int)pair.Key.X, treeY = (int)pair.Key.Y;
                for (int tileX = treeX - 1; tileX <= treeX + 1; tileX++)
                {
                    if (tileX < 0 || tileX >= width || treeY < 0 || treeY >= height)
                        continue;
                    int index = (treeY * width + tileX) * 4;
                    cells[index + 2] = 0;
                    cells[index + 3] = (byte)Math.Min((int)cells[index + 3], 90);
                }
            }
            bool changed = _wetSuitabilityUploaded == null || _wetSuitabilityUploaded.Length != cells.Length
                || _wetSuitabilityTexture is not { IsDisposed: false } || _wetRoomTexture is not { IsDisposed: false }
                || !cells.AsSpan().SequenceEqual(_wetSuitabilityUploaded);
            if (!changed)
            {
                PhaseCost.NoteSince("wet ground: placed things laid on, nothing moved", phaseStart);
                return;
            }
            _wetSuitabilityTexture = TextureDoubleBuffer.UploadIntoSpare(_device, ref _wetSuitabilitySpare,
                _wetSuitabilityTexture, width, height, SurfaceFormat.Color, null, cells, cells.Length);
            if (_wetSuitabilityUploaded == null || _wetSuitabilityUploaded.Length != cells.Length)
                _wetSuitabilityUploaded = new byte[cells.Length];
            Buffer.BlockCopy(cells, 0, _wetSuitabilityUploaded, 0, cells.Length);
            _wetSuitabilityMapTiles = new Vector2(width, height);
            BuildPuddleRoom(cells, width, height);
            PhaseCost.NoteSince("wet ground: placed things laid on + room field + upload", phaseStart);
        }

        private ref int _wetSuitabilityShelterCount => ref _screen.WetSuitabilityShelterCount;

        /// <summary>
        /// How far each point lies inside ground that can hold water, a few samples to a tile: a
        /// two-pass chamfer distance to the nearest sample that cannot (grass, a wall, a fence, a
        /// roof's footprint, a placed thing). Puddles form only where there is room, so a pool can
        /// never press against any of those, and the field is continuous, so its edges do not
        /// follow the tile grid.
        /// </summary>
        private void BuildPuddleRoom(byte[] cells, int width, int height)
        {
            int roomWidth = width * RoomSamplesPerTile, roomHeight = height * RoomSamplesPerTile;
            var distance = new float[roomWidth * roomHeight];
            const float Far = 1e6f;
            for (int y = 0; y < roomHeight; y++)
                for (int x = 0; x < roomWidth; x++)
                {
                    int tile = ((y / RoomSamplesPerTile) * width + x / RoomSamplesPerTile) * 4;
                    distance[y * roomWidth + x] = cells[tile + 2] > 127 ? Far : 0f;
                }
            const float Straight = 1f, Diagonal = 1.4142f;
            for (int y = 0; y < roomHeight; y++)
                for (int x = 0; x < roomWidth; x++)
                {
                    int index = y * roomWidth + x;
                    float best = distance[index];
                    if (best == 0f) continue;
                    if (x > 0) best = Math.Min(best, distance[index - 1] + Straight);
                    if (y > 0)
                    {
                        best = Math.Min(best, distance[index - roomWidth] + Straight);
                        if (x > 0) best = Math.Min(best, distance[index - roomWidth - 1] + Diagonal);
                        if (x < roomWidth - 1) best = Math.Min(best, distance[index - roomWidth + 1] + Diagonal);
                    }
                    distance[index] = best;
                }
            for (int y = roomHeight - 1; y >= 0; y--)
                for (int x = roomWidth - 1; x >= 0; x--)
                {
                    int index = y * roomWidth + x;
                    float best = distance[index];
                    if (best == 0f) continue;
                    if (x < roomWidth - 1) best = Math.Min(best, distance[index + 1] + Straight);
                    if (y < roomHeight - 1)
                    {
                        best = Math.Min(best, distance[index + roomWidth] + Straight);
                        if (x < roomWidth - 1) best = Math.Min(best, distance[index + roomWidth + 1] + Diagonal);
                        if (x > 0) best = Math.Min(best, distance[index + roomWidth - 1] + Diagonal);
                    }
                    distance[index] = best;
                }
            // Color, not Alpha8: MonoGame's OpenGL backend uploads Alpha8 as luminance, so a
            // shader reading its alpha always gets 1, which here meant room everywhere and pools
            // on every roof. The value goes in red.
            if (_wetRoomCells == null || _wetRoomCells.Length != distance.Length * 4)
                _wetRoomCells = new byte[distance.Length * 4];
            byte[] room = _wetRoomCells;
            for (int i = 0; i < distance.Length; i++)
            {
                // Samples to tiles, then tiles / 8 into a byte; the edge sample itself is half a step in.
                float tiles = distance[i] >= Far ? 8f : Math.Max(0f, distance[i] - 0.5f) / RoomSamplesPerTile;
                byte value = (byte)Math.Clamp((int)Math.Round(tiles / 8f * 255f), 0, 255);
                room[i * 4] = room[i * 4 + 1] = room[i * 4 + 2] = value;
                room[i * 4 + 3] = 255;
            }
            _wetRoomTexture?.Dispose();
            _wetRoomTexture = VramTally.Track(new Texture2D(_device, roomWidth, roomHeight, false, SurfaceFormat.Color), "wet puddle room");
            _wetRoomTexture.SetData(room);
        }

        /// <summary>The map's half of the suitability, four bytes a tile (see <see cref="EnsureWetSuitability"/>),
        /// with any tile that upper-layer art covers left dry. Asks every tile of the map, so it is
        /// built once per location and kept.</summary>
        private static byte[] BuildWetGroundCells(GameLocation location, SurfaceMap surface)
        {
            int width = surface.Width, height = surface.Height;
            var cells = new byte[width * height * 4];
            // Every layer that draws OVER the ground, by enumeration rather than by name:
            // modded maps (SVE and friends) ship extra layers like Buildings2 and Front2, and a
            // veto that only knew the three vanilla names left pools sitting on their fences.
            var upperLayers = new List<xTile.Layers.Layer>();
            if (location.map?.Layers != null)
                foreach (xTile.Layers.Layer layer in location.map.Layers)
                    if (layer.Id is not "Back" and not "Paths")
                        upperLayers.Add(layer);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    SurfaceClass surfaceClass = surface.GetSurface(x, y);
                    if (surfaceClass is not SurfaceClass.Ground and not SurfaceClass.Deck)
                        continue;
                    // The tile under a roof or a canopy is often honest stone or dirt on the Back
                    // layer, but the PIXELS at this spot belong to whatever the upper layers drew
                    // - a pool sitting on the Saloon's roof was the first screenshot of this
                    // feature. Any upper-layer art vetoes the tile, and ground under a roof stays
                    // dry, which is what rain does. A deck is itself on an upper layer, so it is
                    // let through: it is the wood that gets wet.
                    bool coveredByArt = false;
                    if (surfaceClass == SurfaceClass.Ground)
                    {
                        foreach (xTile.Layers.Layer layer in upperLayers)
                        {
                            if (x < layer.LayerWidth && y < layer.LayerHeight && layer.Tiles[x, y] != null)
                            {
                                coveredByArt = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // A deck's own planks are upper-layer art, but a wall standing on the deck is
                        // too: Willy's shop sits on the pier, and its front took the wet sheen and a
                        // lamp streak as if the wall were floor. The planks are walked on and the
                        // wall is not, and anything on the Front layers is above the deck either way.
                        coveredByArt = !location.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport)
                            || HasArtOn(location, "Front", x, y) || HasArtOn(location, "AlwaysFront", x, y);
                    }
                    if (coveredByArt)
                        continue;
                    // How the surface takes the rain (Lagarde, porosity): dirt and stone darken the
                    // most and hold puddles; sand darkens as much and holds none; grass darkens less and the water sinks between the
                    // blades; wood and decks hardly darken but shine.
                    byte porosity, gloss;
                    bool pools = false;
                    string? groundType = surfaceClass == SurfaceClass.Deck ? "Wood" : location.doesTileHaveProperty(x, y, "Type", "Back");
                    // The beach's sand is typed Dirt in the game's own map (it is dug like dirt), so
                    // the art it is painted from has to say it: a beach sheet's diggable ground is sand.
                    bool beachSand = groundType is "Dirt" && IsBeachArt(location, x, y);
                    if (groundType is "Sand" || beachSand)
                    {
                        // Sand drinks the rain: it darkens all over and stays matte, and the water
                        // is gone into it before a pool can stand. It is diggable too, so it has to
                        // be asked before the rule below gives it puddles.
                        porosity = 255; gloss = 50;
                    }
                    else if (groundType is "Dirt" or "Stone" || (surfaceClass == SurfaceClass.Ground && location.doesTileHaveProperty(x, y, "Diggable", "Back") != null))
                    {
                        porosity = 255; gloss = 200; pools = true;
                    }
                    else if (groundType is "Wood")
                    {
                        porosity = 60; gloss = 230;
                    }
                    else
                    {
                        porosity = 150; gloss = 90;
                    }
                    int index = (y * width + x) * 4;
                    cells[index] = porosity;
                    cells[index + 1] = gloss;
                    cells[index + 2] = pools ? (byte)255 : (byte)0;
                    cells[index + 3] = 255;
                }
            }
            return cells;
        }

        /// <summary>One line for radiance_report: the timeline and the gate in one place, because
        /// "the ground is not wet" has four causes that look identical from the screen.</summary>
        internal string WetWorldDiag(ModConfig config)
        {
            string contextId = Game1.currentLocation?.GetLocationContextId() ?? "?";
            bool raining = Game1.currentLocation?.IsRainingHere() ?? false;
            bool snowing = Game1.currentLocation?.IsSnowingHere() ?? false;
            return $"wet world: toggle={config.WetWorldEnabled} wetness={WetnessNow:0.000} gloss={GlossNow:0.000} puddle={PuddleAmountNow:0.000} rain={_wetRainEase:0.00} "
                 + $"presence={_fadeWet:0.000} streakLights={_wetLightCount} context={contextId} raining={raining} snowing={snowing} "
                 + $"outdoors={Game1.currentLocation?.IsOutdoors ?? false} "
                 + $"waterRainRings={_rainRingsEase:0.000} "
                 + $"mirror[wanted={_wetPuddleMirrorWanted} baked={ReflectRTReady} waterInMask={_hasWaterInMask}] "
                 + $"(rises over {WetRiseGameMinutes:0} game-min of rain, dries over {WetDecayGameMinutes:0})";
        }
    }
}
