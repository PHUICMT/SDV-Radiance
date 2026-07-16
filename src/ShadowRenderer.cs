using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Phase 5b — directional sprite shadows. Draws a leaning, flattened, dark copy
    /// of each caster's sprite (authentic silhouette, not a blob), pinned at the feet
    /// and leaning away from the sun.
    ///
    /// Drawn INTO the game's own <c>World_Sorted</c> batch (SpriteSortMode.FrontToBack)
    /// at a layerDepth just under the caster, so the shadow sits correctly BEHIND the
    /// sprite and is depth-sorted against trees/objects (over ground, under sprites).
    /// Because we draw into the game's open batch we can't use a shear transform, so the
    /// lean is a rotation about the feet plus a vertical squash — sortable per-sprite.
    /// </summary>
    internal sealed class ShadowRenderer
    {
        /// <summary>Optional diagnostics sink; when set (config.DebugLogging), the first few draws + any error are logged once.</summary>
        internal static IMonitor? Diag;
        private int _diagFrames;
        private bool _errLogged;

        /// <summary>Would a directional shadow be cast right now? (outdoors, daytime, clear, enabled)</summary>
        internal static bool ShouldCast(ModConfig config)
        {
            if (!config.Enabled || !config.DirectionalShadowsEnabled)
                return false;
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !loc.IsOutdoors || Game1.eventUp)
                return false;
            if (Game1.timeOfDay >= 1900 || Game1.timeOfDay < 600 || Game1.isRaining || Game1.isSnowing)
                return false;
            return true;
        }

        /// <summary>Draw all caster shadows into the game's open World_Sorted batch.</summary>
        public void DrawInto(SpriteBatch b, ModConfig config)
        {
            if (!ShouldCast(config))
                return;

            ComputeSun(out float rot, out float squash, out float alpha);
            alpha *= MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            if (alpha <= 0.01f)
                return;

            GameLocation loc = Game1.currentLocation;

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] World_Sorted inject: npcs={loc.characters.Count}, time={Game1.timeOfDay}, rot={rot:0.00}, squash={squash:0.00}, alpha={alpha:0.00}", LogLevel.Debug);
            }

            try
            {
                foreach (NPC npc in loc.characters)
                {
                    if (npc == null || npc.IsInvisible || npc.HideShadow || npc.swimming.Value || npc.Sprite?.Texture == null)
                        continue;
                    DrawNpcShadow(b, npc, rot, squash, alpha);
                }

                DrawPlayerShadow(b, Game1.player, rot, squash, alpha);
            }
            catch (Exception ex)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] draw threw: {ex}", LogLevel.Warn); }
            }
        }

        private void DrawNpcShadow(SpriteBatch b, NPC npc, float rot, float squash, float alpha)
        {
            Rectangle src = npc.Sprite.SourceRect;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom));
            // Origin at the bottom-centre so rotation + squash pivot at the feet.
            Vector2 origin = new Vector2(src.Width / 2f, src.Height);
            float depth = MathHelper.Clamp(npc.GetBoundingBox().Bottom / 10000f - ShadowDepthBias, 0f, 1f);
            b.Draw(npc.Sprite.Texture, feet, src, Color.Black * alpha, rot, origin,
                new Vector2(4f, 4f * squash), SpriteEffects.None, depth);
        }

        private void DrawPlayerShadow(SpriteBatch b, Farmer who, float rot, float squash, float alpha)
        {
            if (who == null || who.currentLocation != Game1.currentLocation
                || who.swimming.Value || who.isRidingHorse() || who.IsSitting())
                return;

            // Same origin the game uses in Farmer.draw so the layers line up before leaning.
            Vector2 origin = new Vector2(who.xOffset,
                (who.yOffset + 128f - who.GetBoundingBox().Height / 2f) / 4f + 4f);
            float depth = MathHelper.Clamp(who.GetBoundingBox().Bottom / 10000f - ShadowDepthBias, 0f, 1f);

            // FarmerRenderer applies one uniform scale, so the player leans via rotation
            // only (no vertical squash yet — that needs the RT path in the soft-edge slice).
            who.FarmerRenderer.draw(b, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                who.FarmerSprite.SourceRect, who.getLocalPosition(Game1.viewport), origin, depth,
                Color.Black * alpha, rot, 1f, who);
        }

        /// <summary>How far under the caster (in sort depth) the shadow sits. ~1px of Y equivalent.</summary>
        private const float ShadowDepthBias = 1e-4f;

        /// <summary>Sun angle → shadow lean (radians), vertical squash, and base opacity.</summary>
        private static void ComputeSun(out float rot, out float squash, out float alpha)
        {
            // Long, low, leaning shadows in the morning/evening; short & steep near noon.
            float d = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            rot = 0.7f * d;                                      // <0 morning lean, >0 evening lean
            squash = MathHelper.Lerp(0.9f, 0.45f, Math.Abs(d));  // flatter when the sun is low
            alpha = 0.35f;
        }
    }
}
