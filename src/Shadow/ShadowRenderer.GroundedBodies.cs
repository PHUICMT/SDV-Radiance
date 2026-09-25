using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Companions;
using StardewValley.Monsters;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer, GROUNDED SHADOWS for the bodies that never had one of ours: a rider on a
    /// horse, a flying companion (the fairy trinket, the parrot), a bat. With the grounded look they
    /// cast off the ground by their height, as a jumping body does; without it nothing here draws
    /// and the game's own blobs are left as they are.
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>How much of the game's own blob under a flier is taken away: the grounded look's
        /// share while creatures cast, so the blob fades out as ours fades in rather than swapping.
        /// Read by the flier draw shim (ShadowSuppression.Draw_FadeFlierBlob).</summary>
        internal static float FlierBlobTakenAway;

        /// <summary>How dark a flier's shadow is against a body standing on the ground: a small thing
        /// in the air lets light round itself.</summary>
        private const float FlierShadowShare = 0.7f;

        private static readonly AccessTools.FieldRef<FlyingCompanion, Vector2>? CompanionExtraPositionOf =
            TryCompanionField();

        private static AccessTools.FieldRef<FlyingCompanion, Vector2>? TryCompanionField()
        {
            try
            {
                return AccessTools.FieldRefAccess<FlyingCompanion, Vector2>("extraPosition");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Set once a frame beside the look's own ease.</summary>
        private static void UpdateFlierBlobs(ModConfig config)
            => FlierBlobTakenAway = config.Enabled && config.DirectionalShadowsEnabled && config.DirectionalShadowCreatures
                ? GroundedShare : 0f;

        /// <summary>
        /// The rider on a horse, cast from the player's upright bake off the ground by the saddle's
        /// height, for this one light. The horse casts its own shadow and always has; the rider was
        /// left out of every shadow, so a mounted player's shadow was a riderless horse.
        /// </summary>
        private void DrawRiderShadow(SpriteBatch spriteBatch, NPC npc, float rotation, float stretch, float alpha, float blur)
        {
            float share = GroundedShare;
            if (share <= 0f || npc is not Horse horse || horse.rider is not Farmer rider || !rider.IsLocalPlayer
                || !_playerReady || _playerRenderTarget == null || !_castPlayer)
                return;
            // The rider's drawn feet, and the ground under them: the horse's own contact row.
            Vector2 drawn = FarmerDrawnAnchor(rider);
            float groundY = horse.GetBoundingBox().Bottom - FeetLift;
            float lift = Math.Max(0f, groundY - (drawn.Y - FeetLift));
            Vector2 ground = Game1.GlobalToLocal(Game1.viewport, new Vector2(drawn.X, groundY));
            Vector2 castFeet = ground + LiftShift(lift, rotation, stretch);
            float riderAlpha = alpha * share * LiftFade(lift);
            RenderTarget2D bake = _playerRenderTarget;
            Vector2 feetInBake = _playerFeetInRenderTarget;
            WithGroundedCast(() => DrawSoftGrounded(spriteBatch, Taps9, bake, null, castFeet, ShadowInk, riderAlpha, rotation,
                feetInBake, new Vector2(1f, stretch), horse.StandingPixel.Y, SpriteEffects.None, blur,
                shadowLengthPerHeight: stretch));
        }

        /// <summary>Every flier in this place with a shadow to cast: where the ground under it is, in
        /// the world, and how high it is over that ground.</summary>
        private void CollectFliers(GameLocation location)
        {
            _fliers.Clear();
            foreach (Farmer owner in location.farmers)
            {
                if (owner?.companions == null)
                    continue;
                foreach (Companion companion in owner.companions)
                {
                    if (companion is not FlyingCompanion flier)
                        continue;
                    // The game's own blob sits at Position + the owner's draw offset + the flit's
                    // sideways part, and the sprite height*4 above it less the flit's vertical part.
                    Vector2 extra = CompanionExtraPositionOf != null ? CompanionExtraPositionOf(flier) : Vector2.Zero;
                    Vector2 ground = flier.Position + owner.drawOffset + new Vector2(extra.X, 0f);
                    _fliers.Add((ground, Math.Max(0f, flier.height * 4f - extra.Y), 12f));
                }
            }
            foreach (NPC npc in location.characters)
            {
                // A bat's blob is a tile below where its body is drawn (Bat.drawAboveAllLayers).
                if (npc is Bat bat && !bat.IsInvisible)
                    _fliers.Add((bat.Position + bat.drawOffset + new Vector2(32f, 64f + bat.yJumpOffset), 32f, 14f));
            }
        }

        private readonly System.Collections.Generic.List<(Vector2 Ground, float Lift, float HalfWidth)> _fliers = [];

        /// <summary>The fliers' shadows under the sun: a soft spot on the ground, moved out along
        /// the sun by the flier's height and fainter the higher it is.</summary>
        private void DrawFlierShadowsInSun(SpriteBatch spriteBatch, GameLocation location, float rotation, float stretch, float alpha, float blur)
        {
            if (FlierBlobTakenAway <= 0f)
                return;
            CollectFliers(location);
            foreach (var (ground, lift, halfWidth) in _fliers)
            {
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ground);
                float depth = MathHelper.Clamp(ground.Y / 10000f - ShadowDepthBias, 0f, 1f);
                DrawContactBlob(spriteBatch, screen + LiftShift(lift, rotation, stretch), halfWidth, halfWidth * 0.5f,
                    alpha * FlierShadowShare * FlierBlobTakenAway * LiftFade(lift), depth, blur);
            }
        }

        /// <summary>The same under lamps: one spot per light that reaches the flier, each moved out
        /// along its own light.</summary>
        private void DrawFlierShadowsUnderLamps(SpriteBatch spriteBatch, GameLocation location, float castStrength, float lenCfg, float blur)
        {
            if (FlierBlobTakenAway <= 0f)
                return;
            CollectFliers(location);
            foreach (var (ground, lift, halfWidth) in _fliers)
            {
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ground);
                float depth = MathHelper.Clamp(ground.Y / 10000f - ShadowDepthBias, 0f, 1f);
                GatherCasts(screen, castStrength, lenCfg);
                foreach (var (rotation, st, a, _) in _lightShadowCasts)
                    DrawContactBlob(spriteBatch, screen + LiftShift(lift, rotation, st), halfWidth, halfWidth * 0.5f,
                        a * FlierShadowShare * FlierBlobTakenAway * LiftFade(lift), depth, blur);
            }
        }
    }
}
