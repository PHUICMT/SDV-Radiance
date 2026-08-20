using System;
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
            if (Game1.ticks == _updatedTick)
                return;
            _updatedTick = Game1.ticks;

            float flash = Game1.flashAlpha;
            bool rising = flash > _lastFlashAlpha + 0.05f;
            _lastFlashAlpha = flash;
            if (Determinism.Frozen)
                return;

            const float dt = 1f / 60f;
            bool wanted = config.Enabled && config.LightningEffectsEnabled;
            bool visibleFlashesAllowed = Game1.options?.screenFlash ?? true;

            if (rising && wanted)
            {
                // Seeded from the tick so a strike picks one lean and every consumer this frame
                // sees the same bolt, both split-screen halves included. The stamp above is what
                // makes that true; it used to be asserted here and nothing enforced it.
                var strikeRandom = new Random(unchecked(Game1.ticks * 747796405));
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
