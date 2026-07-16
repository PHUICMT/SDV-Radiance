using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
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

        /// <summary>Optional diagnostics sink; when set (config.DebugLogging), the first few draws + any error are logged once.</summary>
        internal static IMonitor? Diag;
        private int _diagFrames;
        private bool _errLogged;

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

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] draw path reached: npcs={loc.characters.Count}, time={Game1.timeOfDay}, skew={skew:0.00}, squash={squash:0.00}, alpha={alpha:0.00}", LogLevel.Debug);
            }

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

                DrawPlayerShadow(loc, skew, squash, alpha);
            }
            catch (Exception ex)
            {
                // A shadow must never crash the game or leave a batch open.
                try { _sb.End(); } catch { }
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] draw threw (shadows disabled for now): {ex}", LogLevel.Warn); }
            }
        }

        /// <summary>
        /// The local farmer's own silhouette. Redraws every farmer layer through the
        /// full <see cref="FarmerRenderer"/> with a black <c>overrideColor</c>, all
        /// sheared as one batch — so hair, hat and clothes are part of the shape.
        /// </summary>
        private void DrawPlayerShadow(GameLocation loc, float skew, float squash, float alpha)
        {
            Farmer who = Game1.player;
            if (who == null || who.currentLocation != loc || who.swimming.Value || who.isRidingHorse())
                return;

            float feetY = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(0f, who.GetBoundingBox().Bottom)).Y;
            Matrix m = BuildShearMatrix(feetY, skew, squash);

            // Same origin the game uses in Farmer.draw so the layers line up before shearing.
            Vector2 origin = new Vector2(who.xOffset,
                (who.yOffset + 128f - who.GetBoundingBox().Height / 2f) / 4f + 4f);

            _sb!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, null, m);
            who.FarmerRenderer.draw(_sb, who.FarmerSprite, who.FarmerSprite.SourceRect,
                who.getLocalPosition(Game1.viewport), origin, 0f, Color.Black * alpha, 0f, who);
            _sb.End();
        }

        private void DrawCasterShadow(Texture2D tex, Rectangle src, Vector2 worldPos,
            int spriteWidth, int worldFootY, float skew, float squash, float alpha)
        {
            // Feet = bottom-centre of the sprite, world → screen (same anchor the game uses).
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(worldPos.X + spriteWidth * 4 / 2f, worldFootY));
            Matrix m = BuildShearMatrix(feet.Y, skew, squash);

            _sb!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, null, m);
            _sb.Draw(tex, feet, src, Color.Black * alpha, 0f,
                new Vector2(src.Width / 2f, src.Height), 4f, SpriteEffects.None, 0f);
            _sb.End();
        }

        /// <summary>
        /// Shear about the feet: x' = x - skew*(y - feetY); y flattened toward the feet
        /// by <paramref name="squash"/>. Pinning at feetY keeps the base glued to the caster.
        /// </summary>
        private static Matrix BuildShearMatrix(float feetY, float skew, float squash) => new Matrix(
            1f, 0f, 0f, 0f,
            -skew, squash, 0f, 0f,
            0f, 0f, 1f, 0f,
            skew * feetY, feetY * (1f - squash), 0f, 1f);

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
