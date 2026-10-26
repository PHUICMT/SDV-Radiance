using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Where the particle pool joins the frame.
    ///
    /// <para>
    /// Two places, and they are two places for one reason: everything drawn before the lighting
    /// stage is multiplied by the lightmap. That is exactly right for a petal, which should go
    /// dark at dusk with the rest of the world, and exactly wrong for a spark, which is the thing
    /// making the light. So the ambient group goes into the game's own frame before the chain
    /// reads it, and the emissive group is added on top of whichever lighting stage ran last,
    /// into the same target that stage just wrote. No extra buffer and no extra pass either way.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Which lighting stage carries the emissive group this frame. Whichever of the
        /// two runs LAST, so the sparks are added after every multiply rather than between two of
        /// them: during a crossfade both stages are live and the classic one reads the flood
        /// one's output.</summary>
        private enum EmissiveParticleHost { None, Flood, Classic }

        // The pool itself and the location it belongs to are per screen: RenderPipeline.Screens.cs.
        private float _fadeParticles;
        private EmissiveParticleHost _emissiveParticleHost;
        /// <summary>Set while the stage list is built: the water stage runs this frame, so the
        /// emissive group waits for it instead of riding on the lighting stage. Drawn before the
        /// water, a spark over lava or a firefly over a pond was painted over by the surface the
        /// water pass laid on every water pixel; the volcano's sparks were never seen at all
        /// (18 Oct 2026, "the lava has no sparks"). The water pass comes after both lighting
        /// stages, so a group drawn on its result is still past the lighting multiply.</summary>
        private bool _emissiveAfterWater;
        /// <summary>Window width the chain buffers are a scaled copy of, so a draw into one of
        /// them can work out its own scale from its target.</summary>
        private int _particleWindowWidth = 1;
        /// <summary>Where this screen's viewport sits in the window. Zero on one screen, the right
        /// half's origin on the second screen of a split.</summary>
        private Vector2 _particleScreenOffset;
        private int _particleAmbientDrawn, _particleEmissiveDrawn;
        /// <summary>The surface wave a leaf or a petal is bent by, built once per frame from the
        /// same numbers the water pass is about to be given, so the draw helpers do not each need
        /// the config passed down. Same reason <see cref="_mirrorSliceRows"/> is kept this way.</summary>
        private ParticleSystem.SurfaceWave _particleSurfaceWave;

        internal int ParticlesLive => _particles?.LiveCount ?? 0;
        internal int ParticleAmbientDrawn => _particleAmbientDrawn;
        internal int ParticleEmissiveDrawn => _particleEmissiveDrawn;
        internal int ParticleSpawnsRefused => _particles?.SpawnsRefused ?? 0;
        internal float ParticlePresence => _fadeParticles;
        internal bool ParticleTestRunning => _particles?.TestFountainRunning ?? false;

        /// <summary>Start the debug fountain at the player (radiance_particles test).</summary>
        internal void StartParticleTest(int ticks)
        {
            _particles ??= new ParticleSystem(_device);
            _particles.StartTestFountain(ticks);
        }

        internal void ClearParticles()
        {
            _particles?.Clear();
            ForgetEmitterCarry();
        }

        /// <summary>radiance_particles whirlwind / tumbleweed: start one on the next tick,
        /// whatever the weather or the place, so it can be looked at (the switch must be on).</summary>
        internal void ForceWhirlwind() => _forceWhirlwind = true;
        internal void ForceTumbleweed() => _forceTumbleweed = true;

        /// <summary>Throw away every emitter's fraction of a particle.
        ///
        /// <para>ONE LIST, because there were two and both were short. Each emitter keeps the
        /// fraction it has not spawned yet, and five of them (festive lights, mist, steam, lava and
        /// chimney sparks) were in neither list, so a warp carried their leftovers into a map that
        /// had never seen them. Adding an emitter means adding a line here, once.</para></summary>
        private void ForgetEmitterCarry()
        {
            _dustSpawnCarry = _emberSpawnCarry = _fireflySpawnCarry = _blossomSpawnCarry = 0f;
            _ringSparkleCarry = _festiveSpawnCarry = _chimneySparkCarry = 0f;
            _mistSpawnCarry = _steamSpawnCarry = _lavaSpawnCarry = 0f;
            _whirlwinds.Clear();
            _whirlwindDebrisCarry = 0f;
            _previousPlayerPositionKnown = false;
        }

        /// <summary>
        /// Step the pool and draw the ambient group into the game's own frame.
        ///
        /// <para>Called after the stage list is built and before the chain captures the frame, so
        /// the particles drawn here are part of the picture every stage works on. The stage list
        /// is what says which lighting stage exists this frame, which is what decides where the
        /// emissive group will be added later.</para>
        /// </summary>
        private void UpdateAndDrawAmbientParticles(SpriteBatch spriteBatch, ModConfig config, int windowWidth)
        {
            bool wanted = config.Enabled && config.ParticlesEnabled;
            _fadeParticles = wanted ? Ease01(_fadeParticles) : Ease0(_fadeParticles);
            _particleAmbientDrawn = _particleEmissiveDrawn = 0;
            if (_particles == null && !wanted)
                return;
            _particles ??= new ParticleSystem(_device);

            // A warp is the one place a hard reset is right: the particles belonged to the old
            // map, the new one has never seen them, and the game's own fade to black covers it.
            if (!ReferenceEquals(Game1.currentLocation, _particleLocation))
            {
                _particleLocation = Game1.currentLocation;
                _particles.Clear();
                ForgetEmitterCarry();
            }

            _particleWindowWidth = Math.Max(1, windowWidth);
            _particleScreenOffset = new Vector2(_device.Viewport.X, _device.Viewport.Y);
            // The water's own wave, in the water's own units, so a leaf crossing the shoreline is
            // bent by one field the whole way. The ripple is written in UV, so its amplitude is
            // turned into screen pixels here; 0.0025 is water.fx's own pond constant.
            float waveAmount = Math.Clamp(config.ParticlePetalsFlutter, 0f, 1f) * _fadeParticles;
            var rippleUv = new Vector2(config.WaterStrength * 0.0025f);
            // The water pass's own clock, phase for phase, so the two waves are one wave.
            float waveTime = Determinism.ShaderSeconds * config.WaterSpeed;
            _particleSurfaceWave = new ParticleSystem.SurfaceWave(waveAmount, waveTime,
                new Vector2(rippleUv.X * Game1.viewport.Width, rippleUv.Y * Game1.viewport.Height));
            _emissiveParticleHost = _fadeLighting > FadeGone ? EmissiveParticleHost.Classic
                : _fadeFlood > FadeGone ? EmissiveParticleHost.Flood
                : EmissiveParticleHost.None;

            long started = FrameCost.Begin(FrameCost.Part.Particles);
            // The rainbow's presence settles here, every frame and frozen or not, because its
            // dial is a setting and a frozen capture has to land on it (see the snow glint).
            UpdateRainbowPresence(config);
            // Frozen means frozen: the harness compares captures byte for byte, and a pool that
            // keeps stepping under it makes every comparison after this one worthless.
            // Spawning is tied to the STEP, not to this call: a split screen runs the pipeline
            // twice a frame from two cameras, and an emitter that fired on each call would put
            // twice as much in the world for the second player's benefit.
            if (!Determinism.Frozen
                && _particles.AdvanceTo(Game1.ticks, wanted ? config.ParticleDensity : 0f)
                && wanted)
                SpawnParticlesForTick(config);
            if (_fadeParticles > FadeGone && SomethingInTheParticlePass())
            {
                _ambientParticleLight = NightOnParticles(config, drawnUnderTheGamesLight: false);
                _particleAmbientDrawn = DrawParticleGroup(spriteBatch, emissive: false, Vector2.Zero, 1f);
                // With neither lighting stage running there is no multiply to survive, so the
                // emissive group rides along with the ambient one rather than not being drawn.
                if (_emissiveParticleHost == EmissiveParticleHost.None)
                    _particleEmissiveDrawn = DrawParticleGroup(spriteBatch, emissive: true, Vector2.Zero, 1f);
            }
            FrameCost.End(FrameCost.Part.Particles, started);
        }

        /// <summary>
        /// Add the emissive group on top of a lighting stage's result, in the target that stage
        /// has just written and still has bound.
        ///
        /// <para>The time lands on the Particles line, and also inside this stage's own cost. Two
        /// meters over the same microseconds is worth more here than a stage cost that quietly
        /// hides work somebody else did: the per-stage table is for finding a slow pass, and the
        /// Particles line is for answering whether the particles are the reason a frame got
        /// longer.</para>
        /// </summary>
        private void DrawEmissiveParticlesOnLighting(SpriteBatch spriteBatch, RenderTarget2D dest,
                                                     EmissiveParticleHost host)
        {
            if (_particles == null || _emissiveParticleHost != host || _emissiveAfterWater
                || _fadeParticles <= FadeGone || !SomethingInTheParticlePass())
                return;
            long started = FrameCost.Begin(FrameCost.Part.Particles);
            float pixelScale = dest.Width / (float)_particleWindowWidth;
            _particleEmissiveDrawn += DrawParticleGroup(spriteBatch, emissive: true, _particleScreenOffset, pixelScale);
            FrameCost.End(FrameCost.Part.Particles, started);
        }

        /// <summary>The emissive group on the water stage's result, for the frames that defer it
        /// there (see <see cref="_emissiveAfterWater"/>). The water stage's target is bound again
        /// the way BlendBackSource binds it.</summary>
        private void DrawEmissiveParticlesAfterWater(SpriteBatch spriteBatch, RenderTarget2D dest)
        {
            if (_particles == null || !_emissiveAfterWater || _emissiveParticleHost == EmissiveParticleHost.None
                || _fadeParticles <= FadeGone || !SomethingInTheParticlePass())
                return;
            long started = FrameCost.Begin(FrameCost.Part.Particles);
            _device.SetRenderTarget(dest);
            float pixelScale = dest.Width / (float)_particleWindowWidth;
            _particleEmissiveDrawn += DrawParticleGroup(spriteBatch, emissive: true, _particleScreenOffset, pixelScale);
            FrameCost.End(FrameCost.Part.Particles, started);
        }

        /// <summary>
        /// Whether the particle pass has anything to draw: a live particle, or a waterfall's
        /// rainbow, which rides in the same batch but is not a particle in the pool.
        /// </summary>
        /// <remarks>
        /// The pass used to ask only the pool. A map screenshot draws on a pool of its own that never
        /// steps (its clock is frozen so every chunk shows one moment), so the pool was empty and the
        /// rainbow, found by the scan as usual, was never drawn (reported with a picture of the
        /// forest falls by the author).
        /// </remarks>
        private bool SomethingInTheParticlePass()
            => _particles!.LiveCount > 0 || (_rainbowEase > FadeGone && _mistFeet.Count > 0);

        /// <summary>How many ground particles went into the game's own batch this frame.</summary>
        internal int GroundParticlesDrawn { get; private set; }

        /// <summary>
        /// Draw the particles that lie on the ground into the batch the game has open on its
        /// sorted world, so the things standing on it cover them. See
        /// <see cref="ParticleSystem.DrawGround"/> for why they cannot go in the pool's own pass.
        /// </summary>
        internal void DrawGroundParticles(SpriteBatch spriteBatch, ModConfig config)
        {
            GroundParticlesDrawn = 0;
            if (_particles == null || !config.ParticlesEnabled || _fadeParticles <= FadeGone)
                return;
            GroundParticlesDrawn = _particles.DrawGround(spriteBatch, _fadeParticles, NightOnParticles(config, drawnUnderTheGamesLight: true));
        }

        /// <summary>Particles are cut off at the map's edge. On an outdoor map smaller than the window
        /// the game draws black around it, and dust, petals and motes spawned across the screen went on
        /// drifting over that black. Reported with a picture of the bus stop zoomed out. Indoors this was
        /// never seen only because no indoor emitter spawns across the whole screen.</summary>
        private static readonly RasterizerState ParticlesClippedToMap = new() { CullMode = CullMode.None, ScissorTestEnable = true };

        private int DrawParticleGroup(SpriteBatch spriteBatch, bool emissive, Vector2 screenOffset, float pixelScale)
        {
            GraphicsDevice device = spriteBatch.GraphicsDevice;
            Rectangle previousScissor = device.ScissorRectangle;
            Vector2 mapTiles = MapTilesOf(Game1.currentLocation);
            var mapOnTarget = new Rectangle(
                (int)MathF.Floor((screenOffset.X - Game1.viewport.X) * pixelScale),
                (int)MathF.Floor((screenOffset.Y - Game1.viewport.Y) * pixelScale),
                (int)MathF.Ceiling(mapTiles.X * Game1.tileSize * pixelScale),
                (int)MathF.Ceiling(mapTiles.Y * Game1.tileSize * pixelScale));
            device.ScissorRectangle = Rectangle.Intersect(mapOnTarget, device.Viewport.Bounds);
            spriteBatch.Begin(SpriteSortMode.Deferred,
                emissive ? ParticleSystem.PremultipliedAdditive : BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, ParticlesClippedToMap);
            int drawn = _particles!.Draw(spriteBatch, emissive, _fadeParticles, screenOffset, pixelScale,
                emissive ? ParticleSystem.WorldLight.Untouched : _ambientParticleLight, _particleSurfaceWave);
            // The rainbows ride with the emissive group: sunlight in spray adds to the frame the
            // way a spark does, and this batch already has the blend for it.
            bool rainbowsBehindSprites = emissive && RainbowsCanHideBehindSprites();
            if (emissive && !rainbowsBehindSprites)
                drawn += DrawWaterfallRainbows(spriteBatch, screenOffset, pixelScale);
            spriteBatch.End();
            if (!emissive)
            {
                // The game's own leaf art, point sampled the way the game draws it.
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                    SamplerState.PointClamp, DepthStencilState.None, ParticlesClippedToMap);
                drawn += _particles.DrawGameArt(spriteBatch, _fadeParticles, screenOffset, pixelScale, _ambientParticleLight);
                spriteBatch.End();
            }
            if (rainbowsBehindSprites)
                drawn += DrawRainbowsBehindSprites(spriteBatch, screenOffset, pixelScale);
            device.ScissorRectangle = previousScissor;
            return drawn;
        }

        private RenderTarget2D? _rainbowScratch;

        /// <summary>Takes away what the sprite mask covers, in proportion to its coverage.</summary>
        private static readonly BlendState EraseWhereMasked = new()
        {
            ColorSourceBlend = Blend.Zero, ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.Zero, AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };

        private bool RainbowsCanHideBehindSprites()
            => _rainbowEase > FadeGone && SpriteMaskReady && _spriteMaskRenderTarget != null && !_spriteMaskRenderTarget.IsDisposed;

        /// <summary>
        /// The arches drawn on their own, with whatever stands in front of the water taken out of them,
        /// then added to the frame.
        /// </summary>
        /// <remarks>
        /// The emissive group goes on after the world is drawn, so a tree on the island between two
        /// falls had the bow painted across its leaves (reported with a picture by the author). The
        /// water already keeps a per-frame mask of everything standing on, in or over it, trees'
        /// canopies included, so its ripple leaves them alone; the bow is cut by the same mask. Only
        /// while a bow shows: on any other frame this is not reached.
        /// </remarks>
        private int DrawRainbowsBehindSprites(SpriteBatch spriteBatch, Vector2 screenOffset, float pixelScale)
        {
            GraphicsDevice device = spriteBatch.GraphicsDevice;
            RenderTargetBinding[] bound = device.GetRenderTargets();
            if (bound.Length == 0 || bound[0].RenderTarget is not RenderTarget2D dest)
                return 0;
            if (_rainbowScratch == null || _rainbowScratch.IsDisposed || _rainbowScratch.Width != dest.Width || _rainbowScratch.Height != dest.Height)
            {
                _rainbowScratch?.Dispose();
                _rainbowScratch = VramTally.Track(new RenderTarget2D(device, dest.Width, dest.Height, false,
                    SurfaceFormat.Color, DepthFormat.None), "rainbow scratch");
            }
            Rectangle scissor = device.ScissorRectangle;
            device.SetRenderTarget(_rainbowScratch);
            device.Clear(Color.Transparent);
            device.ScissorRectangle = scissor;
            spriteBatch.Begin(SpriteSortMode.Deferred, ParticleSystem.PremultipliedAdditive,
                SamplerState.LinearClamp, DepthStencilState.None, ParticlesClippedToMap);
            int drawn = DrawWaterfallRainbows(spriteBatch, screenOffset, pixelScale);
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, EraseWhereMasked, SamplerState.LinearClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            spriteBatch.Draw(_spriteMaskRenderTarget!, _rainbowScratch.Bounds, Color.White);
            spriteBatch.End();
            device.SetRenderTargets(bound);
            spriteBatch.Begin(SpriteSortMode.Deferred, ParticleSystem.PremultipliedAdditive,
                SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            spriteBatch.Draw(_rainbowScratch, dest.Bounds, Color.White);
            spriteBatch.End();
            return drawn;
        }

        /// <summary>
        /// How dark the world already is around a particle, as a multiplier on its colour. The
        /// particles themselves now take the game's own darkening (<see cref="NightOnParticles"/>);
        /// this curve stays for the mist, the sky and the old-look dial.
        ///
        /// <para>
        /// The game darkens the whole frame for the time of day BEFORE handing it to us: weather,
        /// then the lightmap, then this mod. A leaf drawn where we draw it has missed that
        /// entirely, so without this it sits at its full daylight colour over a night field, which
        /// is exactly how it looked.
        /// </para>
        ///
        /// <para>
        /// Worked out from the clock rather than read off <c>Game1.outdoorLight</c>, after that
        /// value was tried in both directions and was wrong in both: taken one way every leaf went
        /// black at noon, taken the other every leaf glowed at midnight. Whatever that colour means
        /// it does not mean "how dark the frame is", and a number nobody can explain is not a
        /// number to build on. These are the curves the rest of the mod already dims by, so the
        /// leaf now goes dark on the same schedule as the sky it is under.
        /// </para>
        ///
        /// <para>
        /// The emissive group never sees this. A spark is its own light and the night has no
        /// business taking it away.
        /// </para>
        /// </summary>
        private ParticleSystem.WorldLight _ambientParticleLight = ParticleSystem.WorldLight.Untouched;

        /// <summary>
        /// The night on the ambient particles. Those drawn into the game's own sorted world are
        /// darkened by the game's lightmap like the ground they lie on, so they need nothing more;
        /// those drawn over the finished frame get the same taking away the game did to it (see
        /// <see cref="ParticleSystem.WorldLight"/>).
        /// <para><see cref="ModConfig.ParticleOldNightShade"/> brings back the clock curve every
        /// release used before, on both, which is darker than the ground at night.</para>
        /// <para>Outdoors only. The taking away leaves the lamps out, which outdoors is a few pools
        /// at night; indoors the particles are the dust in a window's light and the motes by a
        /// lamp, standing exactly where the lights the estimate leaves out are, so it dimmed the
        /// dust in Pierre's sunbeams by day (0.15 to 0.22 taken off, against nothing before).
        /// Indoors keeps the clock curve it always had.</para>
        /// </summary>
        private static ParticleSystem.WorldLight NightOnParticles(ModConfig config, bool drawnUnderTheGamesLight)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null || !location.IsOutdoors)
                return new ParticleSystem.WorldLight(Vector3.Zero, AmbientLightOnParticles());
            float oldShade = Math.Clamp(config.ParticleOldNightShade, 0f, 1f);
            Vector3 multiply = Vector3.Lerp(Vector3.One, AmbientLightOnParticles(), oldShade);
            Vector3 takenAway = drawnUnderTheGamesLight
                ? Vector3.Zero
                : PrecipitationSystem.GameLightTakenAway(location) * (1f - oldShade);
            return new ParticleSystem.WorldLight(takenAway, multiply);
        }

        private static Vector3 AmbientLightOnParticles()
        {
            var (sunWarm, nightGlow) = TimeOfDayAmounts();
            Vector3 light = Vector3.Lerp(Vector3.One, new Vector3(1.0f, 0.86f, 0.68f), sunWarm);
            light = Vector3.Lerp(light, new Vector3(0.20f, 0.24f, 0.42f), nightGlow);
            if (LocalSky.IsRaining || LocalSky.IsSnowing)
                light = Vector3.Lerp(light, new Vector3(0.60f, 0.64f, 0.70f), 0.5f);
            return light;
        }

        /// <summary>What the pool is doing, for radiance_particles and radiance_report. Written to
        /// separate the several ways "I see nothing" can be true: the system off, the presence
        /// still fading in, an empty pool, a full pool nobody can see, or particles drawn into a
        /// target that never reached the screen.</summary>
        /// <summary>The feet as world tiles, so "mist in the wrong place" is a coordinate to
        /// stand on instead of a guess.</summary>
        private string MistFeetTiles()
        {
            if (_mistFeet.Count == 0)
                return "";
            var parts = new System.Text.StringBuilder("[");
            for (int i = 0; i < _mistFeet.Count; i++)
            {
                if (i > 0) parts.Append(' ');
                parts.Append((int)(_mistFeet[i].X / 64f)).Append(',').Append((int)(_mistFeet[i].Y / 64f));
            }
            return parts.Append(']').ToString();
        }

        internal string ParticleDiag()
        {
            if (_particles == null)
                return "particles: never started (the system builds itself the first time it is switched on)";
            string host = _emissiveParticleHost switch
            {
                EmissiveParticleHost.Flood => "flood lighting",
                EmissiveParticleHost.Classic => "classic lighting",
                _ => "none (drawn with the ambient group)",
            };
            return $"particles: whirlwinds={WhirlwindsDescribed()} tumbleweedRuns={_tumbleweedLastRuns} forced={_forceTumbleweed} live={_particles.LiveCount}/{ParticleSystem.Capacity} presence={_fadeParticles:0.000} rainbows={RainbowsDrawn}{RainbowArchesDescribed()} rainbowPresence={_rainbowEase:0.00} "
                 + $"drawn ambient={_particleAmbientDrawn} emissive={_particleEmissiveDrawn} "
                 + $"refused={_particles.SpawnsRefused} atlas={(_particles.AtlasReady ? "built" : "MISSING")} "
                 + $"dustWindows={_dustWindowsLit} emberFires={_emberFiresLit} (biggest {ParticleEmberBiggestFire}) "
                 + $"mistFeet={_mistFeet.Count}{MistFeetTiles()} steamTiles={_steamTiles.Count} lavaTiles={_lavaTiles.Count} "
                 + $"fireflies={(_firefliesFlying ? "flying" : "not tonight")} "
                 + $"blossom={(_blossomFalling ? "falling" : "not today")} "
                 + $"ringSparkles={(_ringSparkling ? "on" : "no ring")} footDustLifted={_footDustPuffs} onGround={GroundParticlesDrawn} glowLights={ParticleGlowLights} (strongest weight {ParticleGlowStrongestWeight:0.0} opening {ParticleGlowStrongestAmount:0.000}) chimneys={ChimneysFound} {ChimneyTilesText} festiveBulbs={ParticleFestiveLightsLit} carried[{ParticleCarriedFlame}] "
                 + $"emissiveHost={host} test={(_particles.TestFountainRunning ? "running" : "off")} "
                 + $"night: gameLighting={Game1.drawLighting} outdoorLight={Game1.outdoorLight} ambientLight={Game1.ambientLight} "
                 + $"takenAway={_ambientParticleLight.TakenAway.X:0.000},{_ambientParticleLight.TakenAway.Y:0.000},{_ambientParticleLight.TakenAway.Z:0.000} "
                 + $"multiply={_ambientParticleLight.Multiply.X:0.000},{_ambientParticleLight.Multiply.Y:0.000},{_ambientParticleLight.Multiply.Z:0.000}";
        }
    }
}
