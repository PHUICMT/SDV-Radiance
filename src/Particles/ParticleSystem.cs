using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The mod's own particles: a fixed pool of world-space sprites, stepped on the processor and
    /// drawn in two groups by <see cref="RenderPipeline"/>.
    ///
    /// <para>
    /// The pool is allocated once and never grows. Every particle lives in WORLD pixels, not
    /// screen pixels, which is the opposite of how the game does its own weather debris
    /// (<c>Game1.updateDebrisWeatherForMovement</c> shifts every item by the camera delta each
    /// frame and wraps it at the screen edge). World space costs nothing extra and means a petal
    /// stays where it was when the camera turns around, which is the whole difference between
    /// something drifting through a place and something painted on the window.
    /// </para>
    ///
    /// <para>
    /// Two groups, because a <see cref="SpriteBatch"/> carries one blend state per Begin and the
    /// two halves need different ones. AMBIENT particles (petals, motes, leaves) are alpha-blended
    /// into the game's own frame before the effect chain reads it, so they are lit, rippled,
    /// bloomed and graded exactly like map pixels. EMISSIVE particles (sparks, fireflies) are
    /// added on top of the lighting stage's own output, because anything drawn before that stage
    /// is multiplied by the lightmap, and a firefly multiplied by a dark field is not a firefly.
    /// </para>
    ///
    /// <para>
    /// Nothing here reads the clock directly. The pipeline hands in the tick, and while
    /// <see cref="Determinism.Frozen"/> is set it stops handing in new ones, so a frozen capture
    /// is the same picture twice. That is not a nicety: the verification harness compares captures
    /// byte for byte, and a system that keeps moving under it makes every future comparison
    /// meaningless.
    /// </para>
    /// </summary>
    internal sealed class ParticleSystem : IDisposable
    {
        /// <summary>Which shape in the atlas a particle draws with.</summary>
        internal enum AtlasCell
        {
            /// <summary>A wide soft halo. Fireflies, lamp motes, anything whose light is the point.</summary>
            SoftGlow = 0,
            /// <summary>A small hot dot with a hard-ish edge. Embers and lava sparks.</summary>
            Spark,
            /// <summary>A faint speck. Dust in a sunbeam.</summary>
            Mote,
            /// <summary>A teardrop, wide at one end and pointed at the other. Cherry blossom.</summary>
            Petal,
            /// <summary>A lens shape, pointed at both ends. Autumn leaves.</summary>
            Leaf,
        }

        /// <summary>
        /// How many particles may exist at once, over every emitter and both groups together.
        ///
        /// <para>This is a hard ceiling rather than a target. The cost of a particle is one quad,
        /// so five hundred of them is noise against a two-million-pixel frame; the cost of an
        /// UNBOUNDED emitter is a report we cannot answer, and this mod already has fourteen of
        /// those about frame rate. A refused spawn is counted, so a scene that keeps hitting the
        /// ceiling says so in the diagnostic instead of quietly thinning out.</para>
        /// </summary>
        internal const int Capacity = 512;

        /// <summary>Ticks the simulation will catch up in one call. A frame lost to a load screen
        /// or an alt-tab must not teleport every particle across the map: past this the pool skips
        /// the missing time rather than integrating it.</summary>
        private const int MaxCatchUpSteps = 4;

        private const float SecondsPerStep = 1f / 60f;

        /// <summary>Share of a particle's life spent fading in, and out. House rule: nothing pops,
        /// and that goes for a single mote as much as for a whole stage.</summary>
        private const float FadeInShare = 0.15f;
        private const float FadeOutShare = 0.35f;

        private const int AtlasCellCount = 5;
        private const int AtlasCellSizePixels = 32;

        /// <summary>
        /// Plain addition for art that is already premultiplied.
        ///
        /// <para><see cref="BlendState.Additive"/> multiplies the source by its own alpha first,
        /// which is right for straight-alpha art and wrong for ours: the atlas has the alpha folded
        /// into the colour already, so multiplying again squares it and the faint end of every fade
        /// disappears. One and One adds what is there.</para>
        /// </summary>
        internal static readonly BlendState PremultipliedAdditive = new()
        {
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            Name = "RadianceParticlesAdditive",
        };

        private struct Particle
        {
            internal Vector2 WorldPosition;
            internal Vector2 WorldVelocity;          // world pixels per second
            internal float AgeSeconds;
            internal float LifetimeSeconds;
            internal float SizePixels;               // drawn width and height, before the render scale
            internal float Rotation;
            internal float RotationPerSecond;
            internal float FallPixelsPerSecondSquared;
            internal float DragPerSecond;            // share of speed shed each second
            // Sideways wander that reverses, so a mote drifts about in the air instead of
            // falling in a straight line. Dust does not fall; it hangs and is pushed around.
            internal float SwayPixelsPerSecond;
            internal float SwayPhase;
            internal float SwayPerSecond;
            internal Color Tint;
            internal AtlasCell Cell;
            internal bool Emissive;
        }

        private readonly Particle[] _pool = new Particle[Capacity];
        private int _liveCount;

        /// <summary>Seeded from a constant rather than from the clock: two runs of the same scene
        /// then spawn the same particles, which is what lets a frozen capture be compared with one
        /// taken in a different session.</summary>
        private readonly Random _random = new(20260819);

        private Texture2D? _atlas;
        private int _lastSimulatedTick = int.MinValue;
        private int _testFountainTicksLeft;

        internal int LiveCount => _liveCount;

        /// <summary>Spawns turned away because the pool was full, since the last report. A scene
        /// that is thinning out because it hit the ceiling looks exactly like a scene with a quiet
        /// emitter until something counts this.</summary>
        internal int SpawnsRefused { get; private set; }

        internal bool AtlasReady => _atlas != null;

        /// <summary>A number from 0 to 1 from the pool's own seeded source. Emitters ask for their
        /// randomness here rather than keeping their own, so one seed reproduces a whole scene.</summary>
        internal float RandomUnit() => (float)_random.NextDouble();

        internal float RandomBetween(float low, float high) => low + (high - low) * (float)_random.NextDouble();

        internal ParticleSystem(GraphicsDevice device)
        {
            _atlas = BuildAtlas(device);
        }

        // ---- the pool ----

        /// <summary>Add one particle. Returns false when the pool is full, which is a refusal and
        /// not an error: the ceiling is the feature.</summary>
        internal bool Spawn(AtlasCell cell, Vector2 worldPosition, Vector2 worldVelocity,
                            float lifetimeSeconds, float sizePixels, Color tint, bool emissive,
                            float fallPixelsPerSecondSquared = 0f, float dragPerSecond = 0f,
                            float rotationPerSecond = 0f,
                            float swayPixelsPerSecond = 0f, float swayPerSecond = 0f)
        {
            if (_liveCount >= Capacity) { SpawnsRefused++; return false; }
            ref Particle particle = ref _pool[_liveCount++];
            particle.WorldPosition = worldPosition;
            particle.WorldVelocity = worldVelocity;
            particle.AgeSeconds = 0f;
            particle.LifetimeSeconds = Math.Max(0.05f, lifetimeSeconds);
            particle.SizePixels = Math.Max(1f, sizePixels);
            particle.Rotation = (float)(_random.NextDouble() * Math.PI * 2.0);
            particle.RotationPerSecond = rotationPerSecond;
            particle.FallPixelsPerSecondSquared = fallPixelsPerSecondSquared;
            particle.DragPerSecond = dragPerSecond;
            particle.SwayPixelsPerSecond = swayPixelsPerSecond;
            particle.SwayPerSecond = swayPerSecond;
            particle.SwayPhase = (float)(_random.NextDouble() * Math.PI * 2.0);
            particle.Tint = tint;
            particle.Cell = cell;
            particle.Emissive = emissive;
            return true;
        }

        internal void Clear()
        {
            _liveCount = 0;
            _testFountainTicksLeft = 0;
        }

        /// <summary>Run the debug fountain at the player for this many ticks. The one emitter that
        /// exists before any real one does, so the two draw paths can be seen working on their own
        /// before anything decides when a petal should appear.</summary>
        internal void StartTestFountain(int ticks) => _testFountainTicksLeft = Math.Max(0, ticks);

        internal bool TestFountainRunning => _testFountainTicksLeft > 0;

        /// <summary>
        /// Step the pool up to <paramref name="tick"/>. Returns false when this tick has already
        /// been stepped, which is what keeps a split screen from simulating twice a frame: the
        /// pipeline runs once per screen, and the world only happens once.
        /// </summary>
        internal bool AdvanceTo(int tick, float spawnDensity)
        {
            if (tick == _lastSimulatedTick)
                return false;
            int steps = _lastSimulatedTick == int.MinValue
                ? 1
                : Math.Clamp(tick - _lastSimulatedTick, 1, MaxCatchUpSteps);
            _lastSimulatedTick = tick;
            for (int i = 0; i < steps; i++)
                Step(spawnDensity);
            return true;
        }

        private void Step(float spawnDensity)
        {
            if (_testFountainTicksLeft > 0)
            {
                _testFountainTicksLeft--;
                SpawnTestFountain(spawnDensity);
            }

            for (int i = 0; i < _liveCount; )
            {
                ref Particle particle = ref _pool[i];
                particle.AgeSeconds += SecondsPerStep;
                if (particle.AgeSeconds >= particle.LifetimeSeconds)
                {
                    // Swap-remove: order carries no meaning here, and compacting the array would
                    // cost a move per survivor every step for nothing.
                    _pool[i] = _pool[--_liveCount];
                    continue;
                }
                particle.WorldVelocity.Y += particle.FallPixelsPerSecondSquared * SecondsPerStep;
                if (particle.DragPerSecond > 0f)
                    particle.WorldVelocity *= Math.Max(0f, 1f - particle.DragPerSecond * SecondsPerStep);
                particle.WorldPosition += particle.WorldVelocity * SecondsPerStep;
                if (particle.SwayPixelsPerSecond > 0f)
                {
                    particle.SwayPhase += particle.SwayPerSecond * SecondsPerStep;
                    particle.WorldPosition.X += (float)Math.Sin(particle.SwayPhase)
                                              * particle.SwayPixelsPerSecond * SecondsPerStep;
                }
                particle.Rotation += particle.RotationPerSecond * SecondsPerStep;
                i++;
            }
        }

        /// <summary>Both groups at once, on purpose: the point of the debug emitter is to show
        /// whether BOTH draw paths reached the screen, and one fountain that is half petals and
        /// half sparks answers that in a single glance.</summary>
        private void SpawnTestFountain(float spawnDensity)
        {
            Farmer? player = Game1.player;
            if (player == null) return;
            Vector2 origin = player.Position + new Vector2(32f, 24f);
            int perTick = Math.Max(1, (int)Math.Round(3f * Math.Max(0.05f, spawnDensity)));
            for (int i = 0; i < perTick; i++)
            {
                float sideways = (float)(_random.NextDouble() * 2.0 - 1.0) * 90f;
                float upward = -200f - (float)_random.NextDouble() * 120f;
                bool emissive = i % 3 == 2;
                if (emissive)
                    Spawn(AtlasCell.Spark, origin, new Vector2(sideways, upward),
                        lifetimeSeconds: 1.4f, sizePixels: 10f,
                        tint: new Color(255, 190, 110), emissive: true,
                        fallPixelsPerSecondSquared: 210f, dragPerSecond: 0.6f);
                else
                    Spawn(AtlasCell.Petal, origin, new Vector2(sideways * 0.7f, upward * 0.8f),
                        lifetimeSeconds: 2.2f, sizePixels: 14f,
                        tint: new Color(255, 205, 225), emissive: false,
                        fallPixelsPerSecondSquared: 170f, dragPerSecond: 0.8f,
                        rotationPerSecond: (float)(_random.NextDouble() * 2.4 - 1.2));
            }
        }

        // ---- drawing ----

        /// <summary>
        /// Draw one group into whatever target and batch the caller has open. Returns how many
        /// sprites actually reached the batch, which is not the same as how many were asked for:
        /// a counter that counts attempts rather than survivors reads full while the screen is
        /// empty, and that has already cost one session.
        /// </summary>
        /// <param name="screenOffsetPixels">Where this screen's viewport sits inside the window.
        /// Zero while drawing into the game's own frame, since the device viewport is already the
        /// split-screen one there; the chain's buffers cover the whole window, so a second screen
        /// has to be pushed across by hand.</param>
        /// <param name="pixelScale">Window pixels to target pixels. Below one whenever the render
        /// scale is, so a particle softens with the scene rather than sitting crisp on top of a
        /// blurred one.</param>
        /// <param name="worldLight">What the game's own lightmap already did to everything around
        /// this particle, which it cannot do to the particle itself: by the time we are handed the
        /// frame that multiply has happened. One for the emissive group, which is supposed to be
        /// its own light and not something the night can take away.</param>
        internal int Draw(SpriteBatch spriteBatch, bool emissive, float systemPresence,
                          Vector2 screenOffsetPixels, float pixelScale, Vector3 worldLight)
        {
            if (_atlas == null || systemPresence <= 0f || _liveCount == 0)
                return 0;
            var viewportTopLeft = new Vector2(Game1.viewport.X, Game1.viewport.Y);
            float viewportWidth = Game1.viewport.Width, viewportHeight = Game1.viewport.Height;
            var origin = new Vector2(AtlasCellSizePixels * 0.5f);
            int drawn = 0;
            for (int i = 0; i < _liveCount; i++)
            {
                ref Particle particle = ref _pool[i];
                if (particle.Emissive != emissive)
                    continue;
                float alpha = Presence(particle) * systemPresence;
                if (alpha <= 0.004f)
                    continue;
                Vector2 fromCamera = particle.WorldPosition - viewportTopLeft;
                float margin = particle.SizePixels;
                if (fromCamera.X < -margin || fromCamera.X > viewportWidth + margin
                    || fromCamera.Y < -margin || fromCamera.Y > viewportHeight + margin)
                    continue;
                // The tint carries the alpha in all four channels because the atlas is
                // premultiplied: fading a premultiplied sprite means fading its colour, not only
                // the channel a straight-alpha sprite would use.
                Color tint = particle.Tint * alpha;
                tint = new Color((byte)(tint.R * worldLight.X), (byte)(tint.G * worldLight.Y),
                                 (byte)(tint.B * worldLight.Z), tint.A);
                float scale = particle.SizePixels / AtlasCellSizePixels * pixelScale;
                spriteBatch.Draw(_atlas, (fromCamera + screenOffsetPixels) * pixelScale,
                    CellSource(particle.Cell), tint, particle.Rotation, origin, scale,
                    SpriteEffects.None, 0f);
                drawn++;
            }
            return drawn;
        }

        internal void ForgetRefusals() => SpawnsRefused = 0;

        private static Rectangle CellSource(AtlasCell cell)
            => new((int)cell * AtlasCellSizePixels, 0, AtlasCellSizePixels, AtlasCellSizePixels);

        /// <summary>How much of a particle is showing: up over the first slice of its life, down
        /// over the last. Both ends, always, so nothing appears or vanishes on a frame boundary.</summary>
        private static float Presence(in Particle particle)
        {
            float share = MathHelper.Clamp(particle.AgeSeconds / particle.LifetimeSeconds, 0f, 1f);
            float rising = MathHelper.Clamp(share / FadeInShare, 0f, 1f);
            float falling = MathHelper.Clamp((1f - share) / FadeOutShare, 0f, 1f);
            return Math.Min(rising, falling);
        }

        // ---- the atlas ----

        /// <summary>
        /// Build the shapes on the processor at load, in white, premultiplied.
        ///
        /// <para>White because every particle is tinted at draw time, and one white shape tinted
        /// five ways is one texture and one batch, where five coloured shapes would be five of
        /// each. The mod already builds its noise and contact-blob textures this way, so nothing
        /// ships as a file that a content pack could half-replace.</para>
        /// </summary>
        private static Texture2D BuildAtlas(GraphicsDevice device)
        {
            int width = AtlasCellCount * AtlasCellSizePixels;
            var pixels = new Color[width * AtlasCellSizePixels];
            for (int cell = 0; cell < AtlasCellCount; cell++)
            {
                for (int y = 0; y < AtlasCellSizePixels; y++)
                {
                    for (int x = 0; x < AtlasCellSizePixels; x++)
                    {
                        // Cell coordinates from -1 to 1, so every shape below is written in the
                        // same space whatever the cell size ends up being.
                        float across = (x + 0.5f) / AtlasCellSizePixels * 2f - 1f;
                        float down = (y + 0.5f) / AtlasCellSizePixels * 2f - 1f;
                        float alpha = (AtlasCell)cell switch
                        {
                            AtlasCell.SoftGlow => SoftGlowAlpha(across, down),
                            AtlasCell.Spark => SparkAlpha(across, down),
                            AtlasCell.Mote => MoteAlpha(across, down),
                            AtlasCell.Petal => PetalAlpha(across, down),
                            _ => LeafAlpha(across, down),
                        };
                        byte level = (byte)MathHelper.Clamp(alpha * 255f, 0f, 255f);
                        pixels[y * width + cell * AtlasCellSizePixels + x] = new Color(level, level, level, level);
                    }
                }
            }
            var texture = new Texture2D(device, width, AtlasCellSizePixels, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            return texture;
        }

        private static float SoftGlowAlpha(float across, float down)
        {
            float radius = (float)Math.Sqrt(across * across + down * down);
            float falloff = Math.Max(0f, 1f - radius);
            // Squared for the body, with a third term that keeps a bright middle. A plain square
            // falloff reads as a smudge; a plain disc reads as a dot with a ring around it.
            return falloff * falloff * (0.35f + 0.65f * falloff);
        }

        private static float SparkAlpha(float across, float down)
        {
            float radius = (float)Math.Sqrt(across * across + down * down) / 0.55f;
            float falloff = Math.Max(0f, 1f - radius);
            return falloff * falloff * falloff;
        }

        private static float MoteAlpha(float across, float down)
        {
            // The lit part is most of the cell, not a third of it. It was 0.38 and the sprite
            // that reached the screen was a quarter of the size it was asked for, which reads as
            // "the dust is too small" and is really the art being mostly empty.
            float radius = (float)Math.Sqrt(across * across + down * down) / 0.62f;
            float falloff = Math.Max(0f, 1f - radius);
            return falloff * falloff * (0.45f + 0.55f * falloff);
        }

        private static float PetalAlpha(float across, float down)
        {
            // A blossom petal seen flat. The shape this replaced tapered straight from a wide end
            // to a point, which is the definition of a triangle, and read as one on screen.
            // Four things separate a petal from that: the sides swell out of a narrow neck
            // instead of running straight down to it, the far end is a blunt round rather than a
            // corner, that round is cleft the way a real sakura petal is, and the whole thing
            // leans, because a petal that is perfectly symmetrical reads as a symbol of a petal.
            float alongPetal = (1f - down) * 0.5f;      // 0 at the neck, 1 at the tip
            if (alongPetal <= 0f) return 0f;
            // The lean is a sideways shift that grows with the square of the distance from the
            // neck, so the petal curves rather than shearing.
            float leanAcross = across - PetalLean * alongPetal * alongPetal;
            // A low power swells the sides out early: at a tenth of the way up the petal is
            // already a third as wide as it will get, so the neck is a neck and not a spike.
            float halfWidth = PetalHalfWidth * (float)Math.Pow(alongPetal, 0.55);
            if (alongPetal > PetalCapStart)
            {
                // The last quarter rounds off. A superellipse rather than a circle, because a
                // circular cap starts narrowing immediately and takes the width with it; this
                // holds the petal wide and then closes over the final tenth.
                float intoCap = (alongPetal - PetalCapStart) / (1f - PetalCapStart);
                halfWidth *= (float)Math.Pow(Math.Max(0f, 1f - Math.Pow(intoCap, 2.0)), 0.42);
            }
            if (halfWidth <= 0.001f) return 0f;
            float body = SoftEdge(1f, 0.62f, Math.Abs(leanAcross) / halfWidth);
            // The cleft, and only in the top of the petal: a soft V, not a slot cut through it.
            float intoNotch = MathHelper.Clamp((alongPetal - PetalNotchStart) / (1f - PetalNotchStart), 0f, 1f);
            if (intoNotch > 0f)
            {
                float notchHalfWidth = 0.42f * halfWidth * intoNotch;
                if (notchHalfWidth > 0.002f)
                    body *= SoftEdge(0f, notchHalfWidth, Math.Abs(leanAcross));
            }
            return body;
        }

        /// <summary>Half the petal at its widest, in cell coordinates that run -1 to 1.</summary>
        private const float PetalHalfWidth = 0.82f;
        /// <summary>How far the tip leans off the neck's centre line. Small on purpose: enough
        /// that no two rotations of the sprite look like the same shape mirrored.</summary>
        private const float PetalLean = 0.18f;
        /// <summary>Where the blunt end starts rounding off, measured from the neck.</summary>
        private const float PetalCapStart = 0.55f;
        /// <summary>Where the cleft in the blunt end begins. It has to sit inside the cap, or the
        /// petal reads as two petals stuck together at the neck.</summary>
        private const float PetalNotchStart = 0.84f;

        private static float LeafAlpha(float across, float down)
        {
            // Pointed at both ends, which is what separates a leaf from a petal at this size.
            float halfWidth = 0.46f * (1f - down * down);
            if (halfWidth <= 0.001f) return 0f;
            float sideways = Math.Abs(across) / halfWidth;
            return SoftEdge(1f, 0.70f, sideways);
        }

        /// <summary>Smooth 0-to-1 ramp between two edges, in either direction. The one place these
        /// shapes get their softness from, so an edge is never a staircase at 32 pixels.</summary>
        private static float SoftEdge(float zeroAt, float oneAt, float value)
        {
            float span = oneAt - zeroAt;
            if (Math.Abs(span) < 0.0001f) return value == zeroAt ? 0f : 1f;
            float t = MathHelper.Clamp((value - zeroAt) / span, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        public void Dispose()
        {
            _atlas?.Dispose();
            _atlas = null;
            _liveCount = 0;
        }
    }
}
