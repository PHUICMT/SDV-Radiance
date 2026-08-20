using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// What decides that a particle should exist, and where.
    ///
    /// <para>
    /// Every emitter here runs once per simulated tick, never once per screen: a split screen
    /// calls the pipeline twice a frame from two cameras, and an emitter that spawned on each call
    /// would put twice as much in the world for the second player's benefit. The pool says whether
    /// the tick was a new one, and this only runs when it was.
    /// </para>
    ///
    /// <para>
    /// Rates are per second and are allowed to be fractional. Each emitter carries the remainder
    /// forward rather than rounding it, so "five a second" at sixty ticks is five a second and not
    /// sixty or zero.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Motes a second in one window's beam, at density 1 and full daylight. Chosen to
        /// read as air rather than as weather: a beam with a dozen specks drifting in it looks
        /// like a room, one with a hundred looks like a sandstorm.</summary>
        private const float DustMotesPerWindowPerSecond = 7f;

        /// <summary>Windows one room may run motes for. A farmhouse has two; a mod's ballroom can
        /// have thirty, and thirty beams' worth of dust is the whole pool spent in one place.</summary>
        private const int DustWindowLimit = 6;

        /// <summary>How wide the beam is where it leaves the glass, and where it lands, in world
        /// pixels. It spreads a little, the way the shader's own beam does.</summary>
        private const float DustBeamHalfWidthAtPane = 34f;
        private const float DustBeamHalfWidthAtFloor = 58f;

        /// <summary>What is left of the dust at midday, against a low morning or evening sun.
        ///
        /// <para>A sun overhead drops its light almost straight into the room: the beam is short,
        /// steep, and there is very little air between the glass and the floor for anything to
        /// hang in. A low sun lays the same light the long way across the room, and that is when
        /// a beam is a thing you can see rather than a bright patch on the boards. The dust
        /// follows the beam, so it is thickest at the two ends of the day and thin in the middle
        /// of it.</para></summary>
        private const float DustMiddayShare = 0.4f;

        private float _dustSpawnCarry;
        private int _dustWindowsLit;
        /// <summary>Pane centres of the lit windows on screen, refilled each tick and reused, so
        /// finding them costs no allocation at sixty ticks a second.</summary>
        private readonly System.Collections.Generic.List<Vector2> _dustWindowPanes = new();

        internal int ParticleDustWindows => _dustWindowsLit;

        /// <summary>Every emitter, once per simulated tick.</summary>
        private void SpawnParticlesForTick(ModConfig config)
        {
            TrackPlayerVelocity();
            SpawnWindowBeamDust(config);
            SpawnFireEmbers(config);
            SpawnFireflies(config);
            SpawnBlossom(config);
            SpawnRingSparkles(config);
        }

        /// <summary>How fast the player is moving and in which direction, in world pixels a second,
        /// from where they were last tick. A warp is a jump of hundreds of pixels in one tick, so
        /// anything that large is a teleport and not a walk.</summary>
        private void TrackPlayerVelocity()
        {
            Farmer? player = Game1.player;
            if (player == null) { _previousPlayerPositionKnown = false; _playerVelocity = Vector2.Zero; return; }
            Vector2 now = player.Position;
            if (!_previousPlayerPositionKnown)
                _playerVelocity = Vector2.Zero;
            else
            {
                Vector2 step = now - _previousPlayerPosition;
                _playerVelocity = step.LengthSquared() > 64f * 64f ? Vector2.Zero : step * 60f;
            }
            _previousPlayerPosition = now;
            _previousPlayerPositionKnown = true;
        }

        /// <summary>
        /// Dust in the daylight coming through a window.
        ///
        /// <para>
        /// Emissive rather than ambient, which is a deliberate departure from the plan this was
        /// written from. A mote is not a thing the light falls on, it is light being scattered
        /// back at you; that is why you can only see dust where a beam is and why the same air
        /// looks empty everywhere else. Drawn ambient it would be multiplied by the room's own
        /// darkness and disappear exactly where it is supposed to be brightest. The mod's own sun
        /// shafts already draw their motes additively in the shader for the same reason.
        /// </para>
        ///
        /// <para>
        /// Indoors only. Outdoors the shader's sun shafts already carry motes of their own, and
        /// two systems drawing dust into the same beam is one too many.
        /// </para>
        ///
        /// <para>
        /// The gate is the window's own GLOW, not the clock: the game drops a window's glow sprite
        /// the moment it goes dark for night or rain but leaves the light source in place until
        /// the room is re-entered, so anything that reads the light list alone keeps lighting a
        /// window that is plainly dark. <see cref="ShadowRenderer.WindowGlowing"/> is the test
        /// that already knows this, and the beam itself is drawn behind it.
        /// </para>
        /// </summary>
        private void SpawnWindowBeamDust(ModConfig config)
        {
            _dustWindowPanes.Clear();
            _dustWindowsLit = 0;
            GameLocation? location = Game1.currentLocation;
            if (_particles == null || !config.ParticleDust || location == null || location.IsOutdoors
                || !config.WindowEffectsEnabled || !config.WindowBeamEnabled
                || Game1.currentLightSources == null)
                return;

            ShadowRenderer.WindowDaylight(out Vector3 daylightColour, out float daylightStrength);
            ShadowRenderer.WindowShaft(out float lean, out float reachTiles);
            float reachPixels = reachTiles * 64f;

            // Off-screen windows are left out rather than simulated: a mote nobody can see costs a
            // pool slot that a mote somebody can see needed.
            var viewportTopLeft = new Vector2(Game1.viewport.X, Game1.viewport.Y);
            float viewportWidth = Game1.viewport.Width, viewportHeight = Game1.viewport.Height;
            foreach (var pair in Game1.currentLightSources)
            {
                if (_dustWindowPanes.Count >= DustWindowLimit)
                    break;
                LightSource light = pair.Value;
                if (light.lightContext.Value != LightSource.LightContext.WindowLight)
                    continue;
                if (!ShadowRenderer.WindowGlowing(location, light))
                    continue;
                Vector2 paneCentre = light.position.Value + new Vector2(0f, 12f);
                Vector2 fromCamera = paneCentre - viewportTopLeft;
                if (fromCamera.X < -128f || fromCamera.X > viewportWidth + 128f
                    || fromCamera.Y < -128f || fromCamera.Y > viewportHeight + reachPixels)
                    continue;
                _dustWindowPanes.Add(paneCentre);
            }
            _dustWindowsLit = _dustWindowPanes.Count;
            if (_dustWindowsLit == 0)
                return;

            float rate = DustMotesPerWindowPerSecond * _dustWindowsLit
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleDustAmount)
                       * MathHelper.Clamp(daylightStrength, 0f, 1.2f)
                       * MathHelper.Lerp(DustMiddayShare, 1f, SunLowInSkyShare());
            _dustSpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_dustSpawnCarry, DustWindowLimit * 2);
            if (toSpawn <= 0)
                return;
            _dustSpawnCarry -= toSpawn;

            // Which window each mote lands in is drawn at random rather than taken in order. In
            // order, the first window on the list took every mote a slow rate produced and the
            // second beam stayed empty, which is not a rate anyone would have noticed was wrong.
            for (int i = 0; i < toSpawn; i++)
            {
                Vector2 pane = _dustWindowPanes[(int)(_particles.RandomUnit() * _dustWindowsLit) % _dustWindowsLit];
                SpawnOneMoteInBeam(pane, lean, reachPixels, daylightColour, daylightStrength,
                    Math.Max(0.1f, config.ParticleDustSize));
            }
        }

        /// <summary>How low the sun is: 1 at the two ends of the day, 0 with it overhead. The same
        /// curve the window's own daylight colour uses to decide how golden the light is, so the
        /// dust thickens exactly as the light through the glass turns warm.</summary>
        private static float SunLowInSkyShare()
            => Math.Abs(MathHelper.Clamp((GameClock.MinutesNow() - 720f) / 360f, -1f, 1f));

        /// <summary>Sparks a second from a fire of ordinary size, at density 1.
        ///
        /// <para>Started at 3.5 on the reasoning that a hearth throws a few rather than a
        /// fountain, which was true of a hearth and wrong of a torch: three and a half a second
        /// over a life of a second and a half is five specks in the air, and five specks is not a
        /// burning thing. Raising it for the torch then gave the torch a hearth's worth, which is
        /// the same mistake pointing the other way. A fire's share of this is its SIZE now, so one
        /// number covers a hearth and a candle instead of being wrong for one of them.</para></summary>
        private const float EmberSparksPerFirePerSecond = 8f;

        /// <summary>The size a fire has to be to throw the full rate, and how far either side of
        /// that a fire's share is allowed to travel. A torch at 1.25 keeps most of two thirds; the
        /// farmhouse hearth at 2.5 gets a quarter more than the middle. Bounded at both ends so a
        /// modded light with an enormous radius does not empty the pool on its own.</summary>
        private const float EmberOrdinaryFireRadius = 2f;
        private const float EmberSmallestFireShare = 0.45f;
        private const float EmberLargestFireShare = 1.3f;

        /// <summary>Fires one screen may throw sparks from. The saloon has two dozen lights on the
        /// same sheet a fireplace uses, and two dozen ember columns is the whole pool spent on a
        /// room where the eye only ever follows one of them.</summary>
        private const int EmberFireLimit = 4;

        /// <summary>
        /// The smallest light that counts as a fire worth sparking.
        ///
        /// <para>
        /// Everything in this game that glows warm is on one sheet, so the sheet cannot tell a
        /// hearth from a shelf lamp and the light's own SIZE is the only real line there is. These
        /// are the radii, read off radiance_lights rather than picked:
        /// </para>
        /// <list type="bullet">
        ///   <item>a vanilla firefly: 0.40 to 0.50</item>
        ///   <item>the saloon's decorative lamps along the shelves: 1.00</item>
        ///   <item>a torch, held or placed: 1.25</item>
        ///   <item>the saloon's hearth: 2.00, the farmhouse's: 2.50</item>
        /// </list>
        ///
        /// <para>
        /// The gap between a shelf lamp and a torch is where this sits. Set above it, torches were
        /// silent, which is what was reported: a torch is the most obviously burning thing a
        /// player owns. Set below it, the saloon grew sparks over its bottles, and a summer field
        /// would have every firefly throwing embers.
        /// </para>
        /// </summary>
        private const float EmberMinimumRadius = 1.15f;

        /// <summary>How wide a fire is, from how far it lights, in world pixels either side of its
        /// middle. A hearth's flames fill most of a tile; a torch's fill a few pixels, and sparks
        /// spread across a whole tile from a torch would not be coming off the torch.</summary>
        private const float EmberSpreadPerRadius = 8f;
        private const float EmberSpreadMinimum = 7f;
        private const float EmberSpreadMaximum = 30f;

        /// <summary>Biggest light first, so the hearth wins the four slots over anything smaller
        /// standing nearer the top of the dictionary. Cached: a comparison written inline allocates
        /// a delegate every tick.</summary>
        private static readonly System.Comparison<(Vector2 Position, float Radius, bool Carried)> BiggestFireFirst =
            (left, right) => right.Radius.CompareTo(left.Radius);

        /// <summary>The colour a spark leaves a fire. Fixed rather than taken from the light,
        /// because the game stores a light's colour inverted and a hearth's works out grey-blue
        /// when read straight; an ember is the same orange whatever lamp it came off.</summary>
        private static readonly Vector3 EmberColour = new(1.0f, 0.60f, 0.26f);

        /// <summary>How bright one spark is. Higher than the dust: a spark IS the fire, where a
        /// mote is only air catching some light, and it is meant to cross the bloom threshold so
        /// the brightest of them bloom the way real embers glare.</summary>
        private const float EmberBrightness = 0.95f;

        private float _emberSpawnCarry;
        private int _emberFiresLit;
        /// <summary>Where the fires on screen are, how far each one lights, and whether it is
        /// being carried. Refilled each tick and reused.</summary>
        private readonly System.Collections.Generic.List<(Vector2 Position, float Radius, bool Carried)> _emberFires = new();

        /// <summary>
        /// How near a flame has to be to the player to be the one in their hand.
        ///
        /// <para>
        /// Measured, after fifty-two pixels turned out to catch nothing at all: radiance_lights
        /// puts a held torch's light 1.1 tiles from the player, which is seventy. Every step taken
        /// on the lift above was therefore a step on a number that was never once read, and the
        /// diagnostic saying carried[none] with a torch plainly in hand is what finally said so.
        /// </para>
        ///
        /// <para>
        /// Distance alone is not enough at this range, since a torch planted one tile away would
        /// pass it and float its sparks two tiles up in open air. What settles it is the game's own
        /// name for the light: radiance_lights reads back <c>Torch_Held_21708582</c> for the one in
        /// hand and <c>Torch_Farm_69_21</c> for the one in the ground. Testing the ITEM's type
        /// instead was tried and does not work - a torch conjured with <c>debug item 93</c> is a
        /// plain object rather than the Torch class, so the test was false with a torch plainly
        /// alight in the player's hand.
        /// </para>
        /// </summary>
        private const float CarriedFlameReach = 96f;

        /// <summary>What the game calls the light of a flame somebody is holding.</summary>
        private const string CarriedFlameNameMark = "Held";

        /// <summary>How far above its light a CARRIED flame burns, in world pixels.
        ///
        /// <para>A torch in the ground is its own light and the two are in the same place. A torch
        /// in a hand is held up past the head, while the light the game makes for it sits down at
        /// the player's feet where a pool on the ground belongs. So the sparks were leaving from
        /// the middle of the person rather than from the fire above them.</para>
        /// <para>
        /// Small, because the light is not at the feet: it is already up at the torch. The whole
        /// premise this started from was wrong, and the numbers say so. radiance_lights put the
        /// held light at tile (5.1, 8.2) and 1.1 tiles from the player's own position, which is
        /// one tile up and a hand's width across - the game moves the light with the sprite. So
        /// the sparks were never coming from the feet and never needed two tiles of lifting; they
        /// were coming from the middle of the torch, which is what "from the handle" was
        /// describing all along, and the flame is a third of a tile further up.
        /// </para>
        /// </summary>
        private const float CarriedFlameLift = 24f;

        /// <summary>Where the carried flame was put this tick, and where the player was, both in
        /// world pixels. Only for the diagnostic: an offset stepped by eye is an offset somebody
        /// will have to step again for a modded sprite, and the two numbers turn that from
        /// guesswork into arithmetic.</summary>
        private Vector2 _carriedFlameAnchor;
        private bool _carriedFlameThisTick;

        /// <summary>The nearest fire to the player this tick, whether or not it counted as carried.
        /// Without it, "none" means both "there was nothing near you" and "there was something near
        /// you and it did not qualify", which are opposite problems.</summary>
        private string _carriedCandidateName = "none near the player";

        internal string ParticleCarriedFlame => _carriedFlameThisTick
            ? $"flame=({_carriedFlameAnchor.X:0},{_carriedFlameAnchor.Y:0}) "
              + $"player=({Game1.player?.Position.X ?? 0:0},{Game1.player?.Position.Y ?? 0:0}) lift={CarriedFlameLift:0}"
            : $"none, nearest={_carriedCandidateName}";

        /// <summary>How much of the walk a spark leaves the torch with. Not all of it: a spark is
        /// thrown clear and then the air takes the speed back, which is what makes it fall behind
        /// the person carrying it instead of travelling along beside them.</summary>
        private const float CarriedFlameSparkInheritance = 0.75f;

        /// <summary>Where the player was on the previous simulated tick, for the speed above.
        /// Taken from the position rather than from the game's movement-speed field, which is a
        /// number about how fast they COULD move and says nothing about which way.</summary>
        private Vector2 _previousPlayerPosition;
        private bool _previousPlayerPositionKnown;
        private Vector2 _playerVelocity;

        internal int ParticleEmberFires => _emberFiresLit;

        /// <summary>The tile the biggest fire on screen is on, for the diagnostic. Sparks landing
        /// somewhere nobody expected is a question about WHICH light was picked, and no count can
        /// answer that.</summary>
        internal string ParticleEmberBiggestFire => _emberFires.Count == 0
            ? "none"
            : $"tile {(int)(_emberFires[0].Position.X / 64f)},{(int)(_emberFires[0].Position.Y / 64f)} r={_emberFires[0].Radius:0.0}";

        /// <summary>
        /// Sparks rising off a fire.
        ///
        /// <para>
        /// The game already says which of its lights are flames, and has all along, through the
        /// sheet it picks the glow from: 4 is the sconce sheet a torch, a wall lamp and a
        /// fireplace all share, 5 is the cauldron. That is the same test the flame flicker and
        /// the light ranking use, so a fire that breathes is a fire that sparks and there is no
        /// second list to keep in step.
        /// </para>
        ///
        /// <para>
        /// No light of its own. The plan this was written from called for one aggregated light
        /// per ember column, and the fire it would sit on top of already has one, already
        /// flickering on the same curve: a second candidate in the same place would double a
        /// hearth's pool and put a new name into the ranking that two days of work went into
        /// making stable. The sparks are drawn after the lighting and bloom on their own, which
        /// is the whole of what they were for.
        /// </para>
        ///
        /// <para>
        /// Outdoors they sink into daylight on the ramp the lamp pools use, so a torch at noon
        /// is a torch and not a firework.
        /// </para>
        /// </summary>
        private void SpawnFireEmbers(ModConfig config)
        {
            _emberFires.Clear();
            _emberFiresLit = 0;
            if (_particles == null || !config.ParticleEmbers || Game1.currentLightSources == null)
                return;

            bool outdoors = Game1.currentLocation?.IsOutdoors ?? false;
            bool holdingSomething = Game1.player?.ActiveObject != null;
            _carriedCandidateName = "none near the player";
            var viewportTopLeft = new Vector2(Game1.viewport.X, Game1.viewport.Y);
            float viewportWidth = Game1.viewport.Width, viewportHeight = Game1.viewport.Height;
            foreach (var pair in Game1.currentLightSources)
            {
                LightSource light = pair.Value;
                int sheet = light.textureIndex.Value;
                if (sheet != 4 && sheet != 5)
                    continue;
                float radius = light.radius.Value;
                if (radius < EmberMinimumRadius)
                    continue;
                // A light the MAP hangs outdoors is a street lamp, not a fire. It is on the same
                // sheet as a fireplace because the game only has the one, but nothing on a town
                // pavement is burning and sparks over the cobbles read as a fault. Anything a
                // player PLACED outdoors - a campfire, a torch on a fence post - is not a map
                // light and still sparks, which is the half of this that should.
                if (outdoors && light.lightContext.Value == LightSource.LightContext.MapLight)
                    continue;
                // Where the FLAMES are, not where the light hangs: the game hangs a fireplace's
                // light above its own box, which is the same offset the pool is corrected by.
                Vector2 position = light.position.Value
                                 + ShadowRenderer.FlameGlowOffset(Game1.currentLocation, light.position.Value, sheet);
                Vector2 fromCamera = position - viewportTopLeft;
                if (fromCamera.X < -64f || fromCamera.X > viewportWidth + 64f
                    || fromCamera.Y < -64f || fromCamera.Y > viewportHeight + 64f)
                    continue;
                // Measured from the player's own position, which is what radiance_lights measures
                // from. Adding half a tile to it pushed the reference a hand's width past the
                // torch and the test failed by five pixels while the diagnostic printed 1.1 tiles
                // right next to it.
                bool nearPlayer = Game1.player != null
                    && Vector2.Distance(position, Game1.player.Position) < CarriedFlameReach;
                string lightName = pair.Key?.ToString() ?? "";
                if (nearPlayer)
                    _carriedCandidateName = lightName;
                // The game's own name first, and the player having something in hand as a fallback
                // for a light some other mod made and named its own way.
                bool carried = nearPlayer
                    && (lightName.IndexOf(CarriedFlameNameMark, StringComparison.OrdinalIgnoreCase) >= 0
                        || holdingSomething);
                _emberFires.Add((position, radius, carried));
            }
            // Ranked and then cut, rather than cut as they arrive. Taking the first four the
            // dictionary offered put the sparks on the saloon's shelf lamps and left the fireplace
            // across the room cold, which is what was reported.
            if (_emberFires.Count > EmberFireLimit)
            {
                _emberFires.Sort(BiggestFireFirst);
                _emberFires.RemoveRange(EmberFireLimit, _emberFires.Count - EmberFireLimit);
            }
            _emberFiresLit = _emberFires.Count;
            if (_emberFiresLit == 0)
                return;

            // Summed per fire rather than multiplied by the count, because each fire's share of
            // the rate is its own size.
            float fireShare = 0f;
            foreach (var fire in _emberFires)
                fireShare += MathHelper.Clamp(fire.Radius / EmberOrdinaryFireRadius,
                                              EmberSmallestFireShare, EmberLargestFireShare);
            float rate = EmberSparksPerFirePerSecond * fireShare
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleEmbersAmount)
                       * OutdoorLampDaylightDamping();
            _emberSpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_emberSpawnCarry, EmberFireLimit * 2);
            if (toSpawn <= 0)
                return;
            _emberSpawnCarry -= toSpawn;

            float sizeScale = Math.Max(0.1f, config.ParticleEmbersSize);
            _carriedFlameThisTick = false;
            for (int i = 0; i < toSpawn; i++)
            {
                var fire = _emberFires[(int)(_particles.RandomUnit() * _emberFiresLit) % _emberFiresLit];
                Vector2 flame = fire.Carried
                    ? fire.Position - new Vector2(0f, CarriedFlameLift)
                    : fire.Position;
                if (fire.Carried) { _carriedFlameAnchor = flame; _carriedFlameThisTick = true; }
                SpawnOneSparkOverFire(flame, fire.Radius, sizeScale,
                    fire.Carried ? _playerVelocity * CarriedFlameSparkInheritance : Vector2.Zero);
            }
        }

        /// <summary>Sparks a second orbiting somebody wearing a glow ring, at density 1.</summary>
        private const float RingSparklesPerSecond = 7f;

        /// <summary>How far out they turn, in world pixels, and how much of that is used
        /// vertically. The game is drawn from three quarters on, so a circle in the world is an
        /// ellipse on screen and a ring of sparks drawn round is a ring of sparks lying down.</summary>
        private const float RingSparkleNearRadius = 26f;
        private const float RingSparkleFarRadius = 62f;
        private const float RingSparkleVerticalSquash = 0.55f;

        /// <summary>The colour of one. Cool and pale against the warm of every other light in the
        /// game, so a ring reads as enchantment rather than as another small fire.</summary>
        private static readonly Vector3 RingSparkleColour = new(0.70f, 0.88f, 1.0f);
        private const float RingSparkleBrightness = 0.85f;

        /// <summary>The rings whose whole point is that they glow. The iridium band is in the list
        /// because it carries the glow ring's effect along with everything else it does.</summary>
        private static readonly string[] GlowRingIds = { "516", "517", "527" };

        private float _ringSparkleCarry;
        private bool _ringSparkling;

        internal bool ParticleRingSparkling => _ringSparkling;

        /// <summary>
        /// Sparks turning slowly around somebody wearing a glow ring.
        ///
        /// <para>
        /// A glow ring already lights the ground, and it has always looked like a lamp bolted to
        /// the player: the light is there and nothing about the person says where it came from.
        /// This gives it a source to have come from.
        /// </para>
        ///
        /// <para>
        /// They are world-space like everything else in the pool, which is what makes them worth
        /// having: standing still they turn around you, and walking they are left behind in a
        /// trail that fades. Nothing here follows the player, and that is the feature.
        /// </para>
        /// </summary>
        private void SpawnRingSparkles(ModConfig config)
        {
            _ringSparkling = false;
            Farmer? player = Game1.player;
            if (_particles == null || !config.ParticleRingSparkles || player == null)
                return;
            bool wearing = false;
            foreach (string ringId in GlowRingIds)
            {
                if (player.isWearingRing(ringId)) { wearing = true; break; }
            }
            if (!wearing)
                return;
            _ringSparkling = true;

            float rate = RingSparklesPerSecond
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleRingSparklesAmount);
            _ringSparkleCarry += rate / 60f;
            int toSpawn = Math.Min((int)_ringSparkleCarry, 6);
            if (toSpawn <= 0)
                return;
            _ringSparkleCarry -= toSpawn;

            float sizeScale = Math.Max(0.1f, config.ParticleRingSparklesSize);
            Vector2 centre = player.Position + new Vector2(32f, 20f);
            for (int i = 0; i < toSpawn; i++)
                SpawnOneRingSparkle(centre, sizeScale);
        }

        private void SpawnOneRingSparkle(Vector2 centre, float sizeScale)
        {
            ParticleSystem pool = _particles!;
            float angle = pool.RandomUnit() * MathHelper.TwoPi;
            float outward = pool.RandomBetween(RingSparkleNearRadius, RingSparkleFarRadius);
            float across = (float)Math.Cos(angle), down = (float)Math.Sin(angle);
            var position = centre + new Vector2(across * outward, down * outward * RingSparkleVerticalSquash);
            // Along the circle rather than away from it, so they turn instead of scattering.
            float turn = pool.RandomBetween(18f, 42f) * (pool.RandomUnit() < 0.5f ? -1f : 1f);
            var velocity = new Vector2(-down * turn, across * turn * RingSparkleVerticalSquash
                                                    + pool.RandomBetween(-16f, -4f));
            var tint = new Color(RingSparkleColour.X, RingSparkleColour.Y, RingSparkleColour.Z);
            pool.Spawn(ParticleSystem.AtlasCell.SoftGlow, position, velocity,
                lifetimeSeconds: pool.RandomBetween(1.1f, 2.1f),
                sizePixels: pool.RandomBetween(7f, 14f) * sizeScale,
                tint: tint * RingSparkleBrightness, emissive: true,
                dragPerSecond: 0.4f,
                swayPixelsPerSecond: pool.RandomBetween(4f, 14f),
                swayPerSecond: pool.RandomBetween(1.2f, 2.6f));
        }

        private void SpawnOneSparkOverFire(Vector2 fire, float radius, float sizeScale, Vector2 carriedAlong)
        {
            ParticleSystem pool = _particles!;
            // A spark leaves the fire from anywhere across it, so the column is as wide as the
            // flames are. It was a fixed eleven pixels, which is a candle's worth, and over a
            // hearth that reads as a thread of sparks rising out of one point in the middle of a
            // fire three times as wide.
            float spread = MathHelper.Clamp(radius * EmberSpreadPerRadius,
                                            EmberSpreadMinimum, EmberSpreadMaximum);
            // Sparks leave the TOP of a fire, not the middle of it. The anchor is the middle -
            // it is the light's own place, corrected down onto the flames - so the band sits
            // above it. Spawning around the anchor put the saloon's sparks under its hearth.
            var position = new Vector2(fire.X + pool.RandomBetween(-spread, spread),
                                       fire.Y + pool.RandomBetween(-24f, -4f));
            // Up hard, then almost all of that is shed: a spark leaps off the flame and then
            // hangs in the heat above it rather than carrying on into the ceiling.
            // A spark off a carried torch leaves with the walk the torch is on, then sheds it
            // to the drag below, which is what makes it fall behind whoever is holding it rather
            // than travelling along beside them.
            var velocity = new Vector2(pool.RandomBetween(-16f, 16f), pool.RandomBetween(-74f, -40f))
                         + carriedAlong;
            var tint = new Color(EmberColour.X, EmberColour.Y, EmberColour.Z);
            pool.Spawn(ParticleSystem.AtlasCell.Spark, position, velocity,
                lifetimeSeconds: pool.RandomBetween(1.0f, 2.0f),
                sizePixels: pool.RandomBetween(6f, 12f) * sizeScale,
                tint: tint * EmberBrightness, emissive: true,
                fallPixelsPerSecondSquared: 16f,
                dragPerSecond: 0.95f,
                swayPixelsPerSecond: pool.RandomBetween(8f, 20f),
                swayPerSecond: pool.RandomBetween(1.6f, 3.2f));
        }

        /// <summary>Fireflies lit a second, at density 1. Read as a POPULATION rather than a rate:
        /// each one lives a few seconds, so this times that lifetime is roughly how many are in
        /// the air at once.</summary>
        private const float FirefliesLitPerSecond = 7f;

        /// <summary>The colour of one. Vanilla files its own fireflies as purple and the game
        /// stores a light's colour inverted, which comes out as this green: keeping the same
        /// green means ours sit among the game's own without either looking like the odd one.</summary>
        private static readonly Vector3 FireflyColour = new(0.68f, 1.0f, 0.55f);

        private const float FireflyBrightness = 0.75f;

        private float _fireflySpawnCarry;
        private bool _firefliesFlying;

        internal bool ParticleFirefliesFlying => _firefliesFlying;

        /// <summary>
        /// Fireflies over a field on a summer night.
        ///
        /// <para>
        /// The game has its own, and they are not being replaced: <c>GameLocation.addButterflies</c>
        /// spawns <c>Firefly</c> critters on a summer night, and each one creates a real light and
        /// feeds it into this mod's light list already. What is added here is the population and
        /// the look. So the rule for when they fly is the game's own rule, not one of ours, or a
        /// field would have two different opinions about whether it is firefly weather.
        /// </para>
        ///
        /// <para>
        /// The blink is the fade every particle already has, with a life short enough that it
        /// reads as one: a firefly lights, drifts, and goes out, which is the whole of what a
        /// firefly does. Nothing is gated on grass, because the game does not gate its own on it
        /// either.
        /// </para>
        /// </summary>
        private void SpawnFireflies(ModConfig config)
        {
            _firefliesFlying = false;
            GameLocation? location = Game1.currentLocation;
            if (_particles == null || !config.ParticleFireflies || location == null
                || !location.IsOutdoors || Game1.season != Season.Summer || !Game1.isDarkOut(location))
                return;
            _firefliesFlying = true;

            float rate = FirefliesLitPerSecond
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleFirefliesAmount);
            _fireflySpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_fireflySpawnCarry, 8);
            if (toSpawn <= 0)
                return;
            _fireflySpawnCarry -= toSpawn;

            float sizeScale = Math.Max(0.1f, config.ParticleFirefliesSize);
            // Anywhere on screen, with a margin so one can drift in from off the edge rather than
            // every one of them being born where somebody is looking.
            float left = Game1.viewport.X - 96f, top = Game1.viewport.Y - 96f;
            float width = Game1.viewport.Width + 192f, height = Game1.viewport.Height + 192f;
            for (int i = 0; i < toSpawn; i++)
            {
                var position = new Vector2(left + _particles.RandomUnit() * width,
                                           top + _particles.RandomUnit() * height);
                SpawnOneFirefly(position, sizeScale);
            }
        }

        private void SpawnOneFirefly(Vector2 position, float sizeScale)
        {
            ParticleSystem pool = _particles!;
            var velocity = new Vector2(pool.RandomBetween(-14f, 14f), pool.RandomBetween(-10f, 6f));
            var tint = new Color(FireflyColour.X, FireflyColour.Y, FireflyColour.Z);
            pool.Spawn(ParticleSystem.AtlasCell.SoftGlow, position, velocity,
                lifetimeSeconds: pool.RandomBetween(2.4f, 5.0f),
                sizePixels: pool.RandomBetween(11f, 19f) * sizeScale,
                tint: tint * FireflyBrightness, emissive: true,
                dragPerSecond: 0.25f,
                swayPixelsPerSecond: pool.RandomBetween(10f, 26f),
                swayPerSecond: pool.RandomBetween(0.7f, 1.8f));
        }

        /// <summary>Petals or leaves lit a second over the whole screen, at density 1. A
        /// population figure again: this times the lifetime is roughly how many are in the air.</summary>
        private const float BlossomPerSecond = 6f;

        /// <summary>What is left of it while the game is running its OWN wind day. Vanilla puts
        /// sixteen to sixty-four pieces of debris on screen then, and the point of this emitter is
        /// the calm days it shows nothing at all on. Adding a full second population to a day that
        /// already has one is how a nice effect becomes a nuisance.</summary>
        private const float BlossomShareOnWindDays = 0.25f;

        private float _blossomSpawnCarry;
        private bool _blossomFalling;

        internal bool ParticleBlossomFalling => _blossomFalling;

        /// <summary>
        /// Blossom on the wind in spring, leaves in summer and autumn.
        ///
        /// <para>
        /// This is the one emitter that exists because of something the game does NOT do. Vanilla
        /// debris weather only runs on a Wind day: <c>LocationWeather.IsDebrisWeather</c> gates it,
        /// so an ordinary spring afternoon in Pelican Town has nothing in the air at all. That gap
        /// is the whole feature.
        /// </para>
        ///
        /// <para>
        /// On the days vanilla DOES run, this drops to a quarter and moves on the game's own wind
        /// rather than a wind of its own, so the two populations blow together instead of arguing.
        /// <c>WeatherDebris.globalWind</c> is a public static the game keeps for exactly this, and
        /// it carries the autumn gusts as well as the flat breeze.
        /// </para>
        ///
        /// <para>
        /// Ambient, not emissive: a petal is a thing the light falls ON. It is drawn into the
        /// frame before the chain reads it, so it darkens at dusk with the boards under it, takes
        /// the colour grading, and ripples where it crosses water. That is the opposite of the
        /// dust and the sparks, and it is the difference between something in the world and
        /// something on the window.
        /// </para>
        /// </summary>
        private void SpawnBlossom(ModConfig config)
        {
            _blossomFalling = false;
            GameLocation? location = Game1.currentLocation;
            if (_particles == null || !config.ParticlePetals || location == null || !location.IsOutdoors)
                return;
            // Winter's air belongs to the snow, which is its own piece of work.
            ParticleSystem.AtlasCell cell;
            Vector3 colour;
            switch (Game1.season)
            {
                // Darker than the colours these things are usually drawn in. A petal is a thin
                // pale thing seen against a lit street, not a light of its own, and at full
                // brightness it reads as a sticker on the picture. The game's own debris art is
                // in the same register: mid-tone, close to the ground it blows over.
                case Season.Spring:
                    cell = ParticleSystem.AtlasCell.Petal;
                    colour = new Vector3(0.88f, 0.58f, 0.68f);
                    break;
                case Season.Summer:
                    cell = ParticleSystem.AtlasCell.Leaf;
                    colour = new Vector3(0.40f, 0.60f, 0.28f);
                    break;
                case Season.Fall:
                    cell = ParticleSystem.AtlasCell.Leaf;
                    colour = new Vector3(0.72f, 0.40f, 0.17f);
                    break;
                default:
                    return;
            }
            // Rain flattens what is in the air, and a petal drifting through a downpour reads as
            // a mistake rather than as spring.
            if (Game1.isRaining || Game1.isSnowing)
                return;
            _blossomFalling = true;

            float vanillaShare = location.IsDebrisWeatherHere() ? BlossomShareOnWindDays : 1f;
            float rate = BlossomPerSecond * vanillaShare
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticlePetalsAmount);
            _blossomSpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_blossomSpawnCarry, 8);
            if (toSpawn <= 0)
                return;
            _blossomSpawnCarry -= toSpawn;

            // The game's own wind, in its own units of pixels per tick, so the two populations
            // move together on a day when both are showing.
            float windPerSecond = WeatherDebris.globalWind * 60f;
            float sizeScale = Math.Max(0.1f, config.ParticlePetalsSize);
            float left = Game1.viewport.X - 128f, top = Game1.viewport.Y - 128f;
            float width = Game1.viewport.Width + 256f, height = Game1.viewport.Height + 256f;
            for (int i = 0; i < toSpawn; i++)
            {
                var position = new Vector2(left + _particles.RandomUnit() * width,
                                           top + _particles.RandomUnit() * height);
                SpawnOneBlossom(position, cell, colour, windPerSecond, sizeScale);
            }
        }

        private void SpawnOneBlossom(Vector2 position, ParticleSystem.AtlasCell cell, Vector3 colour,
                                     float windPerSecond, float sizeScale)
        {
            ParticleSystem pool = _particles!;
            var velocity = new Vector2(windPerSecond + pool.RandomBetween(-12f, 6f),
                                       pool.RandomBetween(14f, 34f));
            // Each one a little darker or lighter than its neighbours. A drift of identical
            // colour reads as one printed pattern moving across the screen; a spread of them
            // reads as separate things, and it is the cheapest way there is to buy that.
            float shade = pool.RandomBetween(0.72f, 1f);
            var tint = new Color(colour.X * shade, colour.Y * shade, colour.Z * shade);
            pool.Spawn(cell, position, velocity,
                lifetimeSeconds: pool.RandomBetween(5f, 9f),
                sizePixels: pool.RandomBetween(13f, 22f) * sizeScale,
                tint: tint, emissive: false,
                fallPixelsPerSecondSquared: 6f,
                rotationPerSecond: pool.RandomBetween(-1.6f, 1.6f),
                swayPixelsPerSecond: pool.RandomBetween(14f, 34f),
                swayPerSecond: pool.RandomBetween(0.6f, 1.6f));
        }

        private void SpawnOneMoteInBeam(Vector2 paneCentre, float lean, float reachPixels,
                                        Vector3 daylightColour, float daylightStrength, float sizeScale)
        {
            ParticleSystem pool = _particles!;
            float along = pool.RandomUnit();
            float down = along * reachPixels;
            // The lean is tiles sideways per tile inward, which in world pixels is the same
            // number: both are measured in the same 64ths.
            float sideways = down * lean;
            float halfWidth = MathHelper.Lerp(DustBeamHalfWidthAtPane, DustBeamHalfWidthAtFloor, along);
            var position = new Vector2(paneCentre.X + sideways + pool.RandomBetween(-halfWidth, halfWidth),
                                       paneCentre.Y + down);
            // Dust hangs. What movement it has is the room's own air, not gravity, so this is a
            // slow settle with a sway across it rather than a fall.
            var velocity = new Vector2(lean * 5f + pool.RandomBetween(-3f, 3f), pool.RandomBetween(2f, 7f));
            Vector3 lit = daylightColour * MathHelper.Clamp(daylightStrength, 0f, 1.2f);
            var tint = new Color(MathHelper.Clamp(lit.X, 0f, 1f),
                                 MathHelper.Clamp(lit.Y, 0f, 1f),
                                 MathHelper.Clamp(lit.Z, 0f, 1f));
            pool.Spawn(ParticleSystem.AtlasCell.Mote, position, velocity,
                lifetimeSeconds: pool.RandomBetween(3.5f, 6.5f),
                sizePixels: pool.RandomBetween(10f, 18f) * sizeScale,
                tint: tint * DustMoteBrightness, emissive: true,
                dragPerSecond: 0.15f,
                swayPixelsPerSecond: pool.RandomBetween(6f, 16f),
                swayPerSecond: pool.RandomBetween(0.5f, 1.4f));
        }

        /// <summary>How bright one mote is at full daylight. Low on purpose, and lower than it
        /// first looked like it should be: this is ADDED to the room after the lighting, so a
        /// mote over a lit floorboard is the floorboard plus this, and anything much higher
        /// clips to white and reads as snow indoors. It is the dark half of the beam, where the
        /// air is, that a mote is supposed to be seen against.</summary>
        private const float DustMoteBrightness = 0.55f;
    }
}
