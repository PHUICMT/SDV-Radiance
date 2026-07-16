using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Phase 5b — directional sprite shadows. Draws a sheared, flattened, dark copy
    /// of each caster's own sprite (authentic silhouette, not a blob), pinned at its
    /// feet and leaning away from the sun. Drawn during <c>Display.RenderingWorld</c>
    /// (before the world) so shadows sit UNDER the sprites that cast them.
    ///
    /// Slice 1: NPCs only. Player, blur/fade, and weather polish come next.
    /// </summary>
    internal sealed class ShadowRenderer : IDisposable
    {
        private readonly GraphicsDevice _device;
        private SpriteBatch? _sb;

        public ShadowRenderer(GraphicsDevice device) => _device = device;

        public void Draw(ModConfig config)
        {
            if (!config.Enabled || !config.DirectionalShadowsEnabled)
                return;

            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !loc.IsOutdoors || Game1.eventUp)
                return;
            // Sun-driven: no cast shadow at night, and overcast/precip kills it (soften later).
            if (Game1.timeOfDay >= 1900 || Game1.timeOfDay < 600 || Game1.isRaining || Game1.isSnowing)
                return;

            ComputeSun(out float skew, out float squash, out float alpha);
            alpha *= MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            if (alpha <= 0.01f)
                return;

            _sb ??= new SpriteBatch(_device);

            try
            {
                foreach (NPC npc in loc.characters)
                {
                    if (npc == null || npc.IsInvisible || npc.Sprite?.Texture == null)
                        continue;
                    DrawCasterShadow(npc.Sprite.Texture, npc.Sprite.SourceRect,
                        npc.Position, npc.GetSpriteWidthForPositioning(), npc.GetBoundingBox().Bottom,
                        skew, squash, alpha);
                }
            }
            catch
            {
                // A shadow must never crash the game or leave a batch open.
                try { _sb.End(); } catch { }
            }
        }

        private void DrawCasterShadow(Texture2D tex, Rectangle src, Vector2 worldPos,
            int spriteWidth, int worldFootY, float skew, float squash, float alpha)
        {
            // Feet = bottom-centre of the sprite, world → screen (same anchor the game uses).
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(worldPos.X + spriteWidth * 4 / 2f, worldFootY));
            float feetY = feet.Y;

            // Shear about the feet: x' = x - skew*(y - feetY); y flattened toward the feet
            // by `squash`. Pinning at feetY keeps the base glued to the caster.
            Matrix m = new Matrix(
                1f, 0f, 0f, 0f,
                -skew, squash, 0f, 0f,
                0f, 0f, 1f, 0f,
                skew * feetY, feetY * (1f - squash), 0f, 1f);

            _sb!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, null, m);
            _sb.Draw(tex, feet, src, Color.Black * alpha, 0f,
                new Vector2(src.Width / 2f, src.Height), 4f, SpriteEffects.None, 0f);
            _sb.End();
        }

        /// <summary>Sun angle → shadow skew (lean), squash (flatten), and base opacity.</summary>
        private static void ComputeSun(out float skew, out float squash, out float alpha)
        {
            // Long, low, leaning shadows in the morning/evening; short & steep near noon.
            float d = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            skew = 0.7f * d;                 // <0 morning (lean one way), >0 evening (the other)
            squash = MathHelper.Lerp(0.9f, 0.45f, Math.Abs(d)); // flatter when the sun is low
            alpha = 0.35f;
        }

        public void Dispose()
        {
            _sb?.Dispose();
            _sb = null;
        }
    }
}
