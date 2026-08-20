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

        private ParticleSystem? _particles;
        private float _fadeParticles;
        private GameLocation? _particleLocation;
        private EmissiveParticleHost _emissiveParticleHost;
        /// <summary>Window width the chain buffers are a scaled copy of, so a draw into one of
        /// them can work out its own scale from its target.</summary>
        private int _particleWindowWidth = 1;
        /// <summary>Where this screen's viewport sits in the window. Zero on one screen, the right
        /// half's origin on the second screen of a split.</summary>
        private Vector2 _particleScreenOffset;
        private int _particleAmbientDrawn, _particleEmissiveDrawn;

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
            _dustSpawnCarry = _emberSpawnCarry = _fireflySpawnCarry = _blossomSpawnCarry = 0f;
            _ringSparkleCarry = 0f;
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
                _dustSpawnCarry = _emberSpawnCarry = _fireflySpawnCarry = _blossomSpawnCarry = 0f;
                _ringSparkleCarry = 0f;
                _previousPlayerPositionKnown = false;
            }

            _particleWindowWidth = Math.Max(1, windowWidth);
            _particleScreenOffset = new Vector2(_device.Viewport.X, _device.Viewport.Y);
            _emissiveParticleHost = _fadeLighting > FadeGone ? EmissiveParticleHost.Classic
                : _fadeFlood > FadeGone ? EmissiveParticleHost.Flood
                : EmissiveParticleHost.None;

            long started = FrameCost.Begin(FrameCost.Part.Particles);
            // Frozen means frozen: the harness compares captures byte for byte, and a pool that
            // keeps stepping under it makes every comparison after this one worthless.
            // Spawning is tied to the STEP, not to this call: a split screen runs the pipeline
            // twice a frame from two cameras, and an emitter that fired on each call would put
            // twice as much in the world for the second player's benefit.
            if (!Determinism.Frozen
                && _particles.AdvanceTo(Game1.ticks, wanted ? config.ParticleDensity : 0f)
                && wanted)
                SpawnParticlesForTick(config);
            if (_fadeParticles > FadeGone && _particles.LiveCount > 0)
            {
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
            if (_particles == null || _emissiveParticleHost != host
                || _fadeParticles <= FadeGone || _particles.LiveCount == 0)
                return;
            long started = FrameCost.Begin(FrameCost.Part.Particles);
            float pixelScale = dest.Width / (float)_particleWindowWidth;
            _particleEmissiveDrawn += DrawParticleGroup(spriteBatch, emissive: true, _particleScreenOffset, pixelScale);
            FrameCost.End(FrameCost.Part.Particles, started);
        }

        private int DrawParticleGroup(SpriteBatch spriteBatch, bool emissive, Vector2 screenOffset, float pixelScale)
        {
            spriteBatch.Begin(SpriteSortMode.Deferred,
                emissive ? ParticleSystem.PremultipliedAdditive : BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            int drawn = _particles!.Draw(spriteBatch, emissive, _fadeParticles, screenOffset, pixelScale,
                emissive ? Vector3.One : AmbientLightOnParticles());
            spriteBatch.End();
            return drawn;
        }

        /// <summary>
        /// How dark the world already is around a particle, as a multiplier on its colour.
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
        private static Vector3 AmbientLightOnParticles()
        {
            var (sunWarm, nightGlow) = TimeOfDayAmounts();
            Vector3 light = Vector3.Lerp(Vector3.One, new Vector3(1.0f, 0.86f, 0.68f), sunWarm);
            light = Vector3.Lerp(light, new Vector3(0.20f, 0.24f, 0.42f), nightGlow);
            if (Game1.isRaining || Game1.isSnowing)
                light = Vector3.Lerp(light, new Vector3(0.60f, 0.64f, 0.70f), 0.5f);
            return light;
        }

        /// <summary>What the pool is doing, for radiance_particles and radiance_report. Written to
        /// separate the several ways "I see nothing" can be true: the system off, the presence
        /// still fading in, an empty pool, a full pool nobody can see, or particles drawn into a
        /// target that never reached the screen.</summary>
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
            return $"particles: live={_particles.LiveCount}/{ParticleSystem.Capacity} presence={_fadeParticles:0.000} "
                 + $"drawn ambient={_particleAmbientDrawn} emissive={_particleEmissiveDrawn} "
                 + $"refused={_particles.SpawnsRefused} atlas={(_particles.AtlasReady ? "built" : "MISSING")} "
                 + $"dustWindows={_dustWindowsLit} emberFires={_emberFiresLit} (biggest {ParticleEmberBiggestFire}) "
                 + $"fireflies={(_firefliesFlying ? "flying" : "not tonight")} "
                 + $"blossom={(_blossomFalling ? "falling" : "not today")} "
                 + $"ringSparkles={(_ringSparkling ? "on" : "no ring")} carried[{ParticleCarriedFlame}] "
                 + $"emissiveHost={host} test={(_particles.TestFountainRunning ? "running" : "off")}";
        }
    }
}
