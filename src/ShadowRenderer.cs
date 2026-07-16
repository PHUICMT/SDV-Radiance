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

        // The player's silhouette is rendered to this offscreen target during RenderingWorld,
        // then drawn back (flattened + leaned) into the World_Sorted batch. FarmerRenderer only
        // supports a uniform scale, so the RT is the only way to squash the player vertically.
        private RenderTarget2D? _playerRT;
        private SpriteBatch? _rtBatch;
        private Texture2D? _gradTex;
        private Vector2 _playerFeetInRT;
        private bool _playerReady;
        private const int PlayerRtW = 96;
        private const int PlayerRtH = 176;
        /// <summary>Opacity at the far tip (head end) relative to the feet, for the gradient fade.</summary>
        private const float HeadFade = 0.05f;

        // Multiply only the destination ALPHA by the source alpha (RGB untouched): dst.a *= src.a.
        // Used to bake the feet→head opacity gradient onto the silhouette.
        private static readonly BlendState MultiplyAlpha = new()
        {
            ColorWriteChannels = ColorWriteChannels.Alpha,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.SourceAlpha,
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.One,
        };

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

            ComputeSun(out float rot, out float stretch, out float alpha);
            alpha *= MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            if (alpha <= 0.01f)
                return;

            GameLocation loc = Game1.currentLocation;

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] World_Sorted inject: npcs={loc.characters.Count}, time={Game1.timeOfDay}, rot={rot:0.00}, stretch={stretch:0.00}, alpha={alpha:0.00}", LogLevel.Debug);
            }

            try
            {
                foreach (NPC npc in loc.characters)
                {
                    if (npc == null || npc.IsInvisible || npc.HideShadow || npc.swimming.Value || npc.Sprite?.Texture == null)
                        continue;
                    DrawNpcShadow(b, npc, rot, stretch, alpha);
                }

                DrawPlayerShadow(b, rot, stretch, alpha);
            }
            catch (Exception ex)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] draw threw: {ex}", LogLevel.Warn); }
            }
        }

        private void DrawNpcShadow(SpriteBatch b, NPC npc, float rot, float stretch, float alpha)
        {
            Rectangle src = npc.Sprite.SourceRect;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom));
            // Origin at the bottom-centre so rotation + length stretch pivot at the feet.
            Vector2 origin = new Vector2(src.Width / 2f, src.Height);
            float depth = MathHelper.Clamp(npc.GetBoundingBox().Bottom / 10000f - ShadowDepthBias, 0f, 1f);
            DrawSoft(b, npc.Sprite.Texture, src, feet, Color.Black, alpha, rot, origin,
                new Vector2(4f, 4f * stretch), depth, SpriteEffects.None);
        }

        private void DrawPlayerShadow(SpriteBatch b, float rot, float stretch, float alpha)
        {
            if (!_playerReady || _playerRT == null)
                return;

            Farmer who = Game1.player;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom));
            float depth = MathHelper.Clamp(who.GetBoundingBox().Bottom / 10000f - ShadowDepthBias, 0f, 1f);

            // The baked silhouette is one cohesive image — flatten it vertically and lean it
            // about the feet as a single unit (no per-layer fragmenting), softened at the edges.
            DrawSoft(b, _playerRT, null, feet, Color.White, alpha, rot, _playerFeetInRT,
                new Vector2(1f, stretch), depth, SpriteEffects.None);
        }

        /// <summary>
        /// Render the player's full silhouette (all FarmerRenderer layers, so hats / hair /
        /// Fashion-Sense outfits are included) to an offscreen target, upright and black.
        /// Called during RenderingWorld, before the world batches open, so a render-target
        /// swap is safe. The lean/squash/soften happen later when this is composited.
        /// </summary>
        public void PreparePlayer(GraphicsDevice gd, ModConfig config)
        {
            _playerReady = false;
            if (!ShouldCast(config))
                return;
            Farmer who = Game1.player;
            if (who == null || who.currentLocation != Game1.currentLocation
                || who.swimming.Value || who.isRidingHorse() || who.IsSitting())
                return;

            _playerRT ??= new RenderTarget2D(gd, PlayerRtW, PlayerRtH);
            _rtBatch ??= new SpriteBatch(gd);

            Rectangle src = who.FarmerSprite.SourceRect;
            float w = src.Width * 4f, h = src.Height * 4f;
            Vector2 pos = new Vector2((PlayerRtW - w) / 2f, PlayerRtH - h - 8f);
            _playerFeetInRT = new Vector2(PlayerRtW / 2f, PlayerRtH - 8f);

            RenderTargetBinding[] prev = gd.GetRenderTargets();
            try
            {
                gd.SetRenderTarget(_playerRT);
                gd.Clear(Color.Transparent);
                _rtBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_rtBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                    src, pos, Vector2.Zero, 0f, who.FacingDirection, Color.Black, 0f, 1f, who);
                _rtBatch.End();

                // Fade the silhouette's opacity from the feet (full) to the head/far tip (faint),
                // so the stretched far end reads as a soft penumbra rather than a hard clone.
                _gradTex ??= BuildGradient(gd);
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
                _rtBatch.End();
                _playerReady = true;
            }
            catch (Exception ex)
            {
                try { _rtBatch.End(); } catch { }
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] player RT prep threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                gd.SetRenderTargets(prev);
            }
        }

        /// <summary>1×H alpha ramp: 1.0 at the bottom (feet) fading to <see cref="HeadFade"/> at the top (far tip).</summary>
        private static Texture2D BuildGradient(GraphicsDevice gd)
        {
            var tex = new Texture2D(gd, 1, PlayerRtH);
            var data = new Color[PlayerRtH];
            for (int y = 0; y < PlayerRtH; y++)
            {
                float tBottom = (float)y / (PlayerRtH - 1);      // 0 at top, 1 at bottom
                // Non-linear: stays dark near the feet, fades fast toward the far tip.
                float a = HeadFade + (1f - HeadFade) * (float)Math.Pow(tBottom, 1.8);
                data[y] = new Color(255, 255, 255, (int)(a * 255f));
            }
            tex.SetData(data);
            return tex;
        }

        // Small disc of taps → cheap soft edge. Weighted so overlapping translucent copies
        // reach the target opacity at the core while feathering the rim.
        private static readonly Vector2[] Taps =
        {
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
            new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f),
        };
        private const float BlurPixels = 2f;

        private static void DrawSoft(SpriteBatch b, Texture2D tex, Rectangle? src, Vector2 pos,
            Color baseColor, float alpha, float rot, Vector2 origin, Vector2 scale, float depth,
            SpriteEffects effects)
        {
            // Per-tap alpha so 1-(1-a)^N ≈ target alpha at the fully-covered core.
            float a = 1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), 1f / Taps.Length);
            Color c = baseColor * a;
            foreach (Vector2 t in Taps)
                b.Draw(tex, pos + t * BlurPixels, src, c, rot, origin, scale, effects, depth);
        }

        /// <summary>How far under the caster (in sort depth) the shadow sits. ~1px of Y equivalent.</summary>
        private const float ShadowDepthBias = 1e-4f;

        /// <summary>Sun angle → shadow lean (radians), length stretch, and base opacity.</summary>
        private static void ComputeSun(out float rot, out float stretch, out float alpha)
        {
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float d = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            rot = 0.8f * d;                                      // <0 morning lean, >0 evening lean
            stretch = MathHelper.Lerp(0.3f, 1.2f, Math.Abs(d));  // stretched LONG when the sun is low
            alpha = 0.55f;                                       // opacity at the feet (fades toward the tip)
        }
    }
}
