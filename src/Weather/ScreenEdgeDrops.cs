using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Water clinging to the edges of the screen while it rains, frost feathers in a snowfall -
    /// and never, ever the middle of the picture. The full-screen rain-on-lens look was rejected
    /// for good ("edge drops only" is a settled decision): the middle of the frame is where the
    /// game is, and nothing of ours may sit on it.
    ///
    /// <para>
    /// A drop on glass, photographed from the lit side, is a dark RIM around a nearly clear
    /// interior that brightens toward its bottom (the refracted sky arrives upside-down) with
    /// one hard specular dot. The texture is built exactly like that, so no shader is needed for
    /// the look. Sizes are mixed the way the reference photos mix them - mostly small, a few
    /// fat ones - and now and then a RUNNER breaks loose down a side band, accelerating and
    /// wobbling like a real trickle, leaving a trail of tiny droplets that dry behind it.
    /// </para>
    ///
    /// <para>
    /// Everything fades both ways, holds still under the harness freeze, and hides during
    /// events so no drop ever sits on the SKIP button.
    /// </para>
    /// </summary>
    internal static class ScreenEdgeDrops
    {
        /// <summary>How far in from each edge the band reaches, as a share of the screen's
        /// smaller side. The settled ruling: gone 8% in.</summary>
        private const float EdgeBandShare = 0.08f;
        private const int RainDropsPerScreen = 34;
        private const int FrostFeathersPerScreen = 22;
        private const int TrailDropletsPerScreen = 48;
        private const float PresenceSecondsIn = 6f;
        /// <summary>Rain drops dry off the glass faster than the ground dries (~7 s real is the
        /// research's ten game-minutes); frost takes twice that to melt.</summary>
        private const float RainPresenceSecondsOut = 7f;
        private const float FrostPresenceSecondsOut = 14f;
        private const float FadeGone = 0.004f;

        /// <summary>How far the breath of haze reaches in from an edge, as a share of the
        /// screen's smaller side. Wider than the drops' own band: the drops are objects with
        /// outlines and must stay out of the way, while the haze is a veil that has to arrive
        /// from nowhere or it reads as a border drawn around the game.</summary>
        private const float RainHazeBandShare = 0.11f;
        private const float FrostHazeBandShare = 0.16f;
        private const float RainHazeStrength = 0.20f;
        private const float FrostHazeStrength = 0.38f;
        /// <summary>Condensation is the air's colour, not water's; frost is its own white.</summary>
        private static readonly Color RainHazeTint = new(198, 214, 228);
        private static readonly Color FrostHazeTint = new(238, 246, 255);
        private const int HazeAlongResolution = 128;
        private const int HazeDepthResolution = 48;

        private struct EdgeDrop
        {
            internal Vector2 Position01;      // in 0..1 screen space, so resizes cost nothing
            internal float SizeShare;         // of the screen's smaller side
            internal float Alpha;
            internal float AgeSeconds;
            internal float LifeSeconds;
            /// <summary>Which silhouette in the atlas. A circle is what a drop looks like in a
            /// diagram, not on glass: real ones sag, lean and dent against the surface.</summary>
            internal byte Shape;
            /// <summary>Height against width. Water on vertical glass hangs longer than it is
            /// wide, and the heavier it is the longer it hangs.</summary>
            internal float Stretch;
        }

        private struct TrailDroplet
        {
            internal Vector2 Position01;
            internal float SizeShare;
            internal float Alpha;             // fades to zero; inactive at 0
        }

        /// <summary>A trickle running down one of the SIDE bands (the top band's runners would
        /// leave the band as they fell, and the band is the law). It accelerates like water
        /// gathering weight, meanders a little, and sheds droplets behind itself.</summary>
        private struct Runner
        {
            internal bool Active;
            internal Vector2 Position01;
            internal float FallPerSecond01;
            internal float WobblePhase;
            internal float AnchorX01;
            internal float SecondsToNextDroplet;
        }

        private sealed class ScreenDrops
        {
            internal readonly EdgeDrop[] Rain = new EdgeDrop[RainDropsPerScreen];
            internal readonly EdgeDrop[] Frost = new EdgeDrop[FrostFeathersPerScreen];
            internal readonly TrailDroplet[] Trail = new TrailDroplet[TrailDropletsPerScreen];
            internal Runner LeftRunner, RightRunner;
            internal float SecondsToNextRunner = 4f;
            internal int NextTrailSlot;
            internal readonly Random Random;
            internal float RainPresence, FrostPresence;
            internal bool Seeded;
            internal ScreenDrops(int screenId) { Random = new Random(20260820 + screenId * 71); }
        }

        private static readonly Dictionary<int, ScreenDrops> _screens = new();
        private static Texture2D? _dropTexture;
        private static Texture2D? _frostTexture;
        /// <summary>The haze, built once in both orientations rather than rotated at draw time:
        /// a square texture stretched to a wide screen would carry a band twice as deep along
        /// the top as down the sides, and the eye reads that as a mistake immediately.</summary>
        private static Texture2D? _rainHazeAcross, _rainHazeDown;
        private static Texture2D? _frostHazeAcross, _frostHazeDown;

        /// <summary>Whether the drops have anything to do right now: the switch is on, we are
        /// outdoors, and there is weather to catch on the glass. The frame path asks this before
        /// deciding the whole pipeline can idle, so it has to be cheap and it has to be false on
        /// a dry day, or the mod would never idle again just because this ships on.</summary>
        internal static bool WantedNow(ModConfig config)
        {
            GameLocation? location = Game1.currentLocation;
            if (!config.Enabled || !config.WetWorldLensDrops || location is not { IsOutdoors: true })
                return false;
            return location.IsRainingHere() || location.IsSnowingHere();
        }

        /// <summary>Step and draw this screen's edge band. Called after the chain has finished
        /// its frame, in the same slot as the lightning afterglow.</summary>
        internal static void Draw(SpriteBatch spriteBatch, ModConfig config, int width, int height)
        {
            GameLocation? location = Game1.currentLocation;
            // The drops used to hang off the wet GROUND's switch, which is now hidden and off.
            // They are a different thing seen from a different place, so they carry their own.
            bool featureOn = config.Enabled && config.WetWorldLensDrops
                && location is { IsOutdoors: true } && !Game1.eventUp;
            bool rainWanted = featureOn && location!.IsRainingHere();
            bool frostWanted = featureOn && location!.IsSnowingHere();

            int screenId = StardewModdingAPI.Context.ScreenId;
            if (!_screens.TryGetValue(screenId, out ScreenDrops? screen))
            {
                if (!rainWanted && !frostWanted)
                    return;
                screen = new ScreenDrops(screenId);
                _screens[screenId] = screen;
                LiveScreens.ForgetDeparted(_screens);   // see the note in PrecipitationSystem
            }

            float dt = Determinism.Frozen ? 0f
                : (float)(Game1.currentGameTime?.ElapsedGameTime.TotalSeconds ?? 0.0);
            dt = Math.Min(dt, 0.1f);
            screen.RainPresence = Approach(screen.RainPresence, rainWanted ? 1f : 0f,
                dt / (rainWanted ? PresenceSecondsIn : RainPresenceSecondsOut));
            screen.FrostPresence = Approach(screen.FrostPresence, frostWanted ? 1f : 0f,
                dt / (frostWanted ? PresenceSecondsIn : FrostPresenceSecondsOut));
            if (screen.RainPresence <= FadeGone && screen.FrostPresence <= FadeGone)
                return;

            long started = FrameCost.Begin(FrameCost.Part.WetWorld);
            EnsureTextures();
            if (!screen.Seeded)
            {
                screen.Seeded = true;
                for (int i = 0; i < screen.Rain.Length; i++) Reseed(ref screen.Rain[i], screen.Random);
                for (int i = 0; i < screen.Frost.Length; i++) Reseed(ref screen.Frost[i], screen.Random);
            }
            StepBand(screen.Rain, screen.Random, dt);
            StepBand(screen.Frost, screen.Random, dt);
            StepRunners(screen, dt, rainWanted);

            float smallerSide = Math.Min(width, height) * config.WetWorldLensDropSize;
            MergeTouchingDrops(screen, width, height, smallerSide);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            // The veil goes down first: drops sit ON the glass, and the haze is the glass.
            float hazeDial = config.WetWorldEdgeHaze;
            if (screen.RainPresence > FadeGone)
                DrawHaze(spriteBatch, _rainHazeAcross, _rainHazeDown, RainHazeTint,
                    RainHazeStrength * screen.RainPresence * hazeDial, RainHazeBandShare, width, height, smallerSide);
            if (screen.FrostPresence > FadeGone)
                DrawHaze(spriteBatch, _frostHazeAcross, _frostHazeDown, FrostHazeTint,
                    FrostHazeStrength * screen.FrostPresence * hazeDial, FrostHazeBandShare, width, height, smallerSide);
            if (screen.RainPresence > FadeGone && _dropTexture != null)
            {
                DrawTrail(spriteBatch, screen, width, height, smallerSide);
                DrawBand(spriteBatch, _dropTexture, screen.Rain, screen.RainPresence,
                    width, height, smallerSide, Color.White);
                DrawRunner(spriteBatch, screen.LeftRunner, screen.RainPresence, width, height, smallerSide);
                DrawRunner(spriteBatch, screen.RightRunner, screen.RainPresence, width, height, smallerSide);
            }
            if (screen.FrostPresence > FadeGone && _frostTexture != null)
                DrawBand(spriteBatch, _frostTexture, screen.Frost, screen.FrostPresence,
                    width, height, smallerSide, new Color(225, 240, 255));
            spriteBatch.End();
            FrameCost.End(FrameCost.Part.WetWorld, started);
        }

        private static float Approach(float current, float target, float amount)
            => current + (target - current) * Math.Clamp(amount, 0f, 1f);

        /// <summary>A spot inside the edge band and outside the picture's middle: pick an edge,
        /// a position along it, and a depth no further in than the band allows. Sizes are mixed
        /// the way glass mixes them: mostly small beads, a few heavy drops.</summary>
        private static void Reseed(ref EdgeDrop drop, Random random)
        {
            int edge = random.Next(4);
            // Pulled toward the ends of the edge: frost and condensation both start where two
            // edges meet, because that is where the glass is coldest and least disturbed.
            float along = (float)random.NextDouble();
            along = along < 0.5f ? along * along * 2f : 1f - (1f - along) * (1f - along) * 2f;
            float depth = EdgeBandShare * (0.15f + 0.85f * (float)random.NextDouble());
            drop.Position01 = edge switch
            {
                0 => new Vector2(depth, along),          // left
                1 => new Vector2(1f - depth, along),     // right
                2 => new Vector2(along, depth),          // top
                _ => new Vector2(along, 1f - depth),     // bottom
            };
            double sizeRoll = random.NextDouble();
            drop.SizeShare = sizeRoll < 0.50 ? 0.007f + 0.006f * (float)random.NextDouble()
                : sizeRoll < 0.85 ? 0.013f + 0.009f * (float)random.NextDouble()
                : 0.022f + 0.014f * (float)random.NextDouble();
            drop.Alpha = 0.55f + 0.30f * (float)random.NextDouble();
            drop.AgeSeconds = 0f;
            drop.LifeSeconds = 22f + 26f * (float)random.NextDouble();
            drop.Shape = (byte)random.Next(DropShapeCount);
            // The big ones hang longest: weight is what pulls a bead out of round.
            float weight = Math.Clamp((drop.SizeShare - 0.007f) / 0.029f, 0f, 1f);
            drop.Stretch = 1.02f + 0.30f * weight + 0.12f * (float)random.NextDouble();
        }

        private static void StepBand(EdgeDrop[] band, Random random, float dt)
        {
            for (int i = 0; i < band.Length; i++)
            {
                band[i].AgeSeconds += dt;
                if (band[i].AgeSeconds >= band[i].LifeSeconds)
                    Reseed(ref band[i], random);
            }
        }

        /// <summary>
        /// Two drops that touch become one drop, the way they do on real glass.
        ///
        /// <para>Water does not stack: the moment two beads meet, surface tension pulls them into
        /// a single larger one within a frame or two. Drawing them as two overlapping sprites was
        /// the tell that these were stamps rather than water. The survivor keeps its ground and
        /// takes the other's volume (area conserved, so the merged bead is bigger but not by the
        /// sum of the radii), leans toward where the other one sat, and lives longer for being
        /// heavier. The absorbed one is reborn somewhere else along the band.</para>
        ///
        /// <para>And a bead that grows past what the glass can hold breaks loose and runs, which
        /// is where the side-band runners really come from.</para>
        /// </summary>
        private static void MergeTouchingDrops(ScreenDrops screen, int width, int height, float smallerSide)
        {
            EdgeDrop[] band = screen.Rain;
            for (int i = 0; i < band.Length; i++)
            {
                // A drop still fading in has not really landed yet; merging it would pop.
                if (band[i].AgeSeconds < 0.4f)
                    continue;
                for (int j = i + 1; j < band.Length; j++)
                {
                    if (band[j].AgeSeconds < 0.4f)
                        continue;
                    float acrossX = (band[i].Position01.X - band[j].Position01.X) * width;
                    float acrossY = (band[i].Position01.Y - band[j].Position01.Y) * height;
                    float radiusI = band[i].SizeShare * smallerSide * 0.5f;
                    float radiusJ = band[j].SizeShare * smallerSide * 0.5f;
                    float touching = (radiusI + radiusJ) * 0.85f;
                    if (acrossX * acrossX + acrossY * acrossY > touching * touching)
                        continue;

                    int keep = radiusI >= radiusJ ? i : j;
                    int gone = keep == i ? j : i;
                    float keptSize = band[keep].SizeShare, goneSize = band[gone].SizeShare;
                    // Area, not radius: two equal beads make one about 1.4x across, not 2x.
                    band[keep].SizeShare = Math.Min(0.048f,
                        MathF.Sqrt(keptSize * keptSize + goneSize * goneSize));
                    float pull = goneSize / (keptSize + goneSize) * 0.5f;
                    band[keep].Position01 += (band[gone].Position01 - band[keep].Position01) * pull;
                    band[keep].LifeSeconds += 6f;
                    band[keep].Stretch = Math.Min(1.6f, band[keep].Stretch + 0.06f);
                    Reseed(ref band[gone], screen.Random);

                    // Too heavy for the glass: it breaks loose down whichever side it is on.
                    if (band[keep].SizeShare > 0.034f)
                        TryReleaseRunner(screen, ref band[keep]);
                    if (keep == i)
                        continue;
                    break;   // this slot is now a fresh drop; leave it alone this frame
                }
            }

            // A trickle sweeps up whatever it runs through and gets heavier for it, which is
            // why a real runner accelerates all the way down instead of coasting.
            AbsorbIntoRunner(screen, ref screen.LeftRunner, width, height, smallerSide);
            AbsorbIntoRunner(screen, ref screen.RightRunner, width, height, smallerSide);
        }

        private static void AbsorbIntoRunner(ScreenDrops screen, ref Runner runner,
                                             int width, int height, float smallerSide)
        {
            if (!runner.Active)
                return;
            float runnerRadius = 0.014f * smallerSide * 0.5f;
            for (int i = 0; i < screen.Rain.Length; i++)
            {
                float acrossX = (runner.Position01.X - screen.Rain[i].Position01.X) * width;
                float acrossY = (runner.Position01.Y - screen.Rain[i].Position01.Y) * height;
                float reach = runnerRadius + screen.Rain[i].SizeShare * smallerSide * 0.5f;
                if (acrossX * acrossX + acrossY * acrossY > reach * reach)
                    continue;
                runner.FallPerSecond01 = Math.Min(0.28f, runner.FallPerSecond01 + 0.012f);
                Reseed(ref screen.Rain[i], screen.Random);
            }
        }

        /// <summary>Turn an overgrown bead into a running trickle, if its side is free.</summary>
        private static void TryReleaseRunner(ScreenDrops screen, ref EdgeDrop drop)
        {
            bool leftSide = drop.Position01.X < 0.5f;
            // Only the side bands run: a runner released from the top or bottom band would
            // leave the band on its way down, and the band is the law.
            if (drop.Position01.X > EdgeBandShare && drop.Position01.X < 1f - EdgeBandShare)
                return;
            ref Runner slot = ref (leftSide ? ref screen.LeftRunner : ref screen.RightRunner);
            if (slot.Active)
                return;
            slot.Active = true;
            slot.AnchorX01 = drop.Position01.X;
            slot.Position01 = drop.Position01;
            slot.FallPerSecond01 = 0.03f;
            slot.WobblePhase = (float)(screen.Random.NextDouble() * Math.PI * 2);
            slot.SecondsToNextDroplet = 0f;
            Reseed(ref drop, screen.Random);
        }

        /// <summary>Spawn, accelerate and wobble the two side-band runners, shedding trail
        /// droplets as they go. One per side at most: a window with three simultaneous
        /// trickles reads as a car wash.</summary>
        private static void StepRunners(ScreenDrops screen, float dt, bool raining)
        {
            if (raining)
            {
                screen.SecondsToNextRunner -= dt;
                if (screen.SecondsToNextRunner <= 0f)
                {
                    screen.SecondsToNextRunner = 3f + 6f * (float)screen.Random.NextDouble();
                    bool leftSide = screen.Random.Next(2) == 0;
                    ref Runner slot = ref (leftSide ? ref screen.LeftRunner : ref screen.RightRunner);
                    if (!slot.Active)
                    {
                        float depth = EdgeBandShare * (0.2f + 0.7f * (float)screen.Random.NextDouble());
                        slot.Active = true;
                        slot.AnchorX01 = leftSide ? depth : 1f - depth;
                        slot.Position01 = new Vector2(slot.AnchorX01, -0.02f);
                        slot.FallPerSecond01 = 0.035f + 0.02f * (float)screen.Random.NextDouble();
                        slot.WobblePhase = (float)(screen.Random.NextDouble() * Math.PI * 2);
                        slot.SecondsToNextDroplet = 0f;
                    }
                }
            }
            StepRunner(ref screen.LeftRunner, screen, dt);
            StepRunner(ref screen.RightRunner, screen, dt);
            for (int i = 0; i < screen.Trail.Length; i++)
                if (screen.Trail[i].Alpha > 0f)
                    screen.Trail[i].Alpha = Math.Max(0f, screen.Trail[i].Alpha - dt / 5f);
        }

        private static void StepRunner(ref Runner runner, ScreenDrops screen, float dt)
        {
            if (!runner.Active)
                return;
            runner.FallPerSecond01 = Math.Min(0.24f, runner.FallPerSecond01 + 0.05f * dt);
            runner.WobblePhase += 2.6f * dt;
            runner.Position01.Y += runner.FallPerSecond01 * dt;
            runner.Position01.X = runner.AnchorX01 + MathF.Sin(runner.WobblePhase) * 0.004f;
            runner.SecondsToNextDroplet -= dt;
            if (runner.SecondsToNextDroplet <= 0f)
            {
                runner.SecondsToNextDroplet = 0.05f + 0.05f * (float)screen.Random.NextDouble();
                ref TrailDroplet droplet = ref screen.Trail[screen.NextTrailSlot];
                screen.NextTrailSlot = (screen.NextTrailSlot + 1) % screen.Trail.Length;
                droplet.Position01 = runner.Position01;
                droplet.SizeShare = 0.004f + 0.004f * (float)screen.Random.NextDouble();
                droplet.Alpha = 0.45f;
            }
            if (runner.Position01.Y > 1.03f)
                runner.Active = false;
        }

        private static void DrawTrail(SpriteBatch spriteBatch, ScreenDrops screen,
                                      int width, int height, float smallerSide)
        {
            if (_dropTexture == null)
                return;
            var origin = new Vector2(DropCellSize / 2f, _dropTexture.Height / 2f);
            var cell = new Rectangle(0, 0, DropCellSize, _dropTexture.Height);
            for (int i = 0; i < screen.Trail.Length; i++)
            {
                ref TrailDroplet droplet = ref screen.Trail[i];
                if (droplet.Alpha <= 0.01f)
                    continue;
                float scale = droplet.SizeShare * smallerSide / DropCellSize;
                spriteBatch.Draw(_dropTexture,
                    new Vector2(droplet.Position01.X * width, droplet.Position01.Y * height),
                    cell, Color.White * (droplet.Alpha * screen.RainPresence), 0f, origin,
                    scale, SpriteEffects.None, 1f);
            }
        }

        private static void DrawRunner(SpriteBatch spriteBatch, in Runner runner, float presence,
                                       int width, int height, float smallerSide)
        {
            if (!runner.Active || _dropTexture == null)
                return;
            var origin = new Vector2(DropCellSize / 2f, _dropTexture.Height / 2f);
            var cell = new Rectangle(DropCellSize, 0, DropCellSize, _dropTexture.Height);
            float scale = 0.014f * smallerSide / DropCellSize;
            // Stretched along its fall: a moving trickle-head, not a bead at rest.
            spriteBatch.Draw(_dropTexture,
                new Vector2(runner.Position01.X * width, runner.Position01.Y * height),
                cell, Color.White * (0.85f * presence), 0f, origin,
                new Vector2(scale * 0.85f, scale * 1.55f), SpriteEffects.None, 1f);
        }

        /// <summary>
        /// Four strips of veil, one per edge, each measured in PIXELS from its own edge so the
        /// band is the same depth all the way round. The corners are covered twice and come out
        /// heavier for it, which is where condensation and frost really do gather.
        /// </summary>
        private static void DrawHaze(SpriteBatch spriteBatch, Texture2D? across, Texture2D? down,
                                     Color tint, float strength, float bandShare,
                                     int width, int height, float smallerSide)
        {
            if (across == null || down == null || strength <= 0.004f)
                return;
            int band = Math.Max(2, (int)(bandShare * smallerSide));
            Color colour = tint * strength;
            spriteBatch.Draw(down, new Rectangle(0, 0, width, band), null, colour,
                0f, Vector2.Zero, SpriteEffects.None, 1f);
            spriteBatch.Draw(down, new Rectangle(0, height - band, width, band), null, colour,
                0f, Vector2.Zero, SpriteEffects.FlipVertically, 1f);
            spriteBatch.Draw(across, new Rectangle(0, 0, band, height), null, colour,
                0f, Vector2.Zero, SpriteEffects.None, 1f);
            spriteBatch.Draw(across, new Rectangle(width - band, 0, band, height), null, colour,
                0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 1f);
        }

        /// <summary>Value noise on a small grid, so the veil is uneven the way breath on cold
        /// glass is uneven rather than an airbrushed gradient.</summary>
        private static float HazeNoise(float x, float y, int seed)
        {
            int cellX = (int)MathF.Floor(x), cellY = (int)MathF.Floor(y);
            float fx = x - cellX, fy = y - cellY;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float corner00 = HazeHash(cellX, cellY, seed), corner10 = HazeHash(cellX + 1, cellY, seed);
            float corner01 = HazeHash(cellX, cellY + 1, seed), corner11 = HazeHash(cellX + 1, cellY + 1, seed);
            return (corner00 * (1f - fx) + corner10 * fx) * (1f - fy)
                 + (corner01 * (1f - fx) + corner11 * fx) * fy;
        }

        private static float HazeHash(int x, int y, int seed)
        {
            int hash = x * 374761393 + y * 668265263 + seed * 1274126177;
            hash = (hash ^ (hash >> 13)) * 1274126177;
            return ((hash ^ (hash >> 16)) & 0xFFFF) / 65535f;
        }

        /// <summary>Build one orientation of a veil: depth away from the edge on one axis, the
        /// run along the edge on the other.</summary>
        private static Texture2D BuildHaze(GraphicsDevice device, bool alongIsHorizontal,
                                           float crystalSharpness, int seed)
        {
            int wide = alongIsHorizontal ? HazeAlongResolution : HazeDepthResolution;
            int tall = alongIsHorizontal ? HazeDepthResolution : HazeAlongResolution;
            var pixels = new Color[wide * tall];
            for (int y = 0; y < tall; y++)
            {
                for (int x = 0; x < wide; x++)
                {
                    float depth = (alongIsHorizontal ? (y + 0.5f) / tall : (x + 0.5f) / wide);
                    float along = (alongIsHorizontal ? (x + 0.5f) / wide : (y + 0.5f) / tall);
                    // Thick at the very edge, gone well before the band ends, so nothing about
                    // this has a line where it stops.
                    float falloff = 1f - depth;
                    falloff = falloff * falloff * falloff;
                    float broad = HazeNoise(along * 5f, depth * 2.5f, seed);
                    float fine = HazeNoise(along * 17f, depth * 9f, seed + 7);
                    float texture = 0.55f + 0.45f * (broad * 0.7f + fine * 0.3f);
                    // Frost creeps: high sharpness turns the soft veil into fingers reaching in.
                    float creep = MathF.Pow(Math.Clamp(broad * 1.35f, 0f, 1f), 1f + crystalSharpness * 3f);
                    float alpha = falloff * (texture * (1f - crystalSharpness) + creep * crystalSharpness);
                    byte level = (byte)(Math.Clamp(alpha, 0f, 1f) * 255f);
                    pixels[y * wide + x] = new Color(level, level, level, level);
                }
            }
            var built = new Texture2D(device, wide, tall, false, SurfaceFormat.Color);
            built.SetData(pixels);
            return built;
        }

        private static void DrawBand(SpriteBatch spriteBatch, Texture2D texture, EdgeDrop[] band,
                                     float presence, int width, int height, float smallerSide, Color tint)
        {
            // The drop atlas holds several silhouettes side by side; the frost sheet is one cell.
            bool atlas = ReferenceEquals(texture, _dropTexture);
            int cellWidth = atlas ? DropCellSize : texture.Width;
            var origin = new Vector2(cellWidth / 2f, texture.Height / 2f);
            for (int i = 0; i < band.Length; i++)
            {
                ref EdgeDrop drop = ref band[i];
                // Each drop breathes in over its first second and out over its last two, so the
                // slow churn of the band never pops a bead into existence.
                float ownFade = Math.Min(Math.Min(drop.AgeSeconds, 1f),
                    Math.Clamp((drop.LifeSeconds - drop.AgeSeconds) / 2f, 0f, 1f));
                float alpha = drop.Alpha * ownFade * presence;
                if (alpha <= 0.01f)
                    continue;
                float scale = drop.SizeShare * smallerSide / cellWidth;
                Rectangle? source = atlas
                    ? new Rectangle(drop.Shape * DropCellSize, 0, DropCellSize, texture.Height)
                    : null;
                float stretch = atlas ? drop.Stretch : 1f;
                spriteBatch.Draw(texture,
                    new Vector2(drop.Position01.X * width, drop.Position01.Y * height),
                    source, tint * alpha, 0f, origin,
                    new Vector2(scale, scale * stretch), SpriteEffects.None, 1f);
            }
        }

        private const int DropShapeCount = 6;
        private const int DropCellSize = 48;

        /// <summary>
        /// How far the outline sits from the centre at this angle, as a share of the cell.
        ///
        /// <para>A circle is what a drop looks like in a diagram. On glass gravity pulls the
        /// bottom into a heavier lobe, surface tension pins the top into a narrower shoulder,
        /// and the contact patch dents the sides unevenly - which is why the round beads read as
        /// bubbles rather than water. Two low harmonics do the sag and the taper, two higher ones
        /// break the symmetry, and the phase per shape makes six different drops out of one
        /// formula rather than six hand-drawn sprites.</para>
        ///
        /// <para>Angle is screen-space: 0 points right, +pi/2 points DOWN.</para>
        /// </summary>
        private static float DropOutlineRadius(int shape, float angle)
        {
            float phase = shape * 1.73f;
            float sag = 0.09f + 0.035f * (shape % 3);
            float outline = 1f
                + sag * MathF.Sin(angle)                                  // heavier below
                - 0.085f * MathF.Cos(2f * angle)                          // longer than wide
                + 0.075f * MathF.Sin(3f * angle + phase)                  // one shoulder fuller
                + 0.040f * MathF.Sin(5f * angle + phase * 2.3f)           // a small dent
                + 0.045f * MathF.Cos(angle + phase * 0.7f);               // leaning
            return outline * 0.80f;
        }

        private static void EnsureTextures()
        {
            if (_dropTexture != null && !_dropTexture.IsDisposed)
                return;
            GraphicsDevice device = Game1.graphics.GraphicsDevice;

            // Several drop silhouettes side by side, all lit the way the reference photos read:
            // a dark rim, a nearly clear interior that brightens toward the BOTTOM (the refracted
            // sky arrives upside-down), and one hard specular dot up and to the left. The outline
            // is deliberately NOT a circle - see DropOutlineRadius.
            int atlasWidth = DropCellSize * DropShapeCount;
            var dropPixels = new Color[atlasWidth * DropCellSize];
            for (int shape = 0; shape < DropShapeCount; shape++)
            {
                for (int y = 0; y < DropCellSize; y++)
                {
                    for (int x = 0; x < DropCellSize; x++)
                    {
                        float dx = (x + 0.5f) / DropCellSize - 0.5f;
                        float dy = (y + 0.5f) / DropCellSize - 0.5f;
                        float radius = MathF.Sqrt(dx * dx + dy * dy) * 2f;
                        if (radius < 1e-4f)
                            radius = 1e-4f;
                        float angle = MathF.Atan2(dy, dx);
                        float outline = DropOutlineRadius(shape, angle);
                        float acrossShape = radius / outline;
                        if (acrossShape > 1f)
                            continue;
                        float rim = MathF.Exp(-MathF.Pow((acrossShape - 0.86f) / 0.12f, 2f));
                        float interior = Math.Clamp((0.82f - acrossShape) * 12f, 0f, 1f);
                        float bottomLight = Math.Clamp(dy / outline * 2f + 0.5f, 0f, 1f);
                        float hx = dx + 0.16f * outline, hy = dy + 0.18f * outline;
                        float speck = Math.Max(0f, 1f - MathF.Sqrt(hx * hx + hy * hy) * 7f);
                        speck *= speck;

                        float rimAlpha = rim * 0.62f;
                        float interiorAlpha = interior * 0.26f;
                        float alpha = Math.Min(1f, rimAlpha + interiorAlpha + speck * 0.9f);
                        float level = 0.10f * rimAlpha
                            + (0.28f + 0.55f * bottomLight) * interiorAlpha
                            + 1.0f * speck * 0.9f;
                        level = Math.Min(level, alpha);
                        dropPixels[y * atlasWidth + shape * DropCellSize + x] = new Color(level, level, level, alpha);
                    }
                }
            }
            _dropTexture = new Texture2D(device, atlasWidth, DropCellSize, false, SurfaceFormat.Color);
            _dropTexture.SetData(dropPixels);

            // A frost feather: six thin arms with a soft fade toward the tips.
            const int frostSize = 40;
            var frostPixels = new Color[frostSize * frostSize];
            for (int y = 0; y < frostSize; y++)
            {
                for (int x = 0; x < frostSize; x++)
                {
                    float dx = (x + 0.5f) / frostSize - 0.5f;
                    float dy = (y + 0.5f) / frostSize - 0.5f;
                    float radius = MathF.Sqrt(dx * dx + dy * dy) * 2f;
                    if (radius > 1f || radius < 0.02f)
                        continue;
                    float angle = MathF.Atan2(dy, dx);
                    float arm = MathF.Abs(((angle / (MathF.PI / 3f)) % 1f + 1f) % 1f - 0.5f) * (MathF.PI / 3f);
                    float armWidth = 0.10f * (1f - radius * 0.6f);
                    float onArm = Math.Max(0f, 1f - arm / armWidth);
                    float alpha = onArm * (1f - radius) * 0.8f;
                    frostPixels[y * frostSize + x] = new Color(alpha, alpha, alpha, alpha);
                }
            }
            _frostTexture = new Texture2D(device, frostSize, frostSize, false, SurfaceFormat.Color);
            _frostTexture.SetData(frostPixels);

            // Rain leaves a soft breath on the glass; frost reaches in with fingers.
            _rainHazeDown = BuildHaze(device, alongIsHorizontal: true, crystalSharpness: 0.15f, seed: 11);
            _rainHazeAcross = BuildHaze(device, alongIsHorizontal: false, crystalSharpness: 0.15f, seed: 23);
            _frostHazeDown = BuildHaze(device, alongIsHorizontal: true, crystalSharpness: 0.70f, seed: 41);
            _frostHazeAcross = BuildHaze(device, alongIsHorizontal: false, crystalSharpness: 0.70f, seed: 59);
        }
    }
}
