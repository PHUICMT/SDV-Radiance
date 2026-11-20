using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - WIDE GLOW, the second layer of the bloom. The tight blur is a lamp's own
    /// halo; this is the light it throws into the air round it, as wide as a quarter of the screen
    /// and falling off slowly, the soft haze HD-2D games put round every light. Built from the
    /// bloom's own bright pass, so it glows only where the bloom already does.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>The bright pass at a half, a quarter and an eighth of the bloom's own size.
        /// After the climb back up the first one holds all three levels summed.</summary>
        private readonly RenderTarget2D?[] _wideGlowLevels = new RenderTarget2D?[3];

        /// <summary>The smallest level's texel, in bright-pass texels: three halvings.</summary>
        private const int WideGridStepTexels = 8;

        /// <summary>Shrink the bright pass held in <paramref name="brightPass"/> three times, then grow
        /// it back adding each level onto the one above. Returns the finished layer, or null when the
        /// shader in use has no wide glow (an old bloom.mgfxo).</summary>
        /// <param name="brightGridRemainder">What <see cref="PixelsPastWorldGrid"/> gave the bright
        /// pass, in source pixels: the layer is read back shifted by it too.</param>
        private RenderTarget2D? BuildWideGlow(SpriteBatch spriteBatch, Effect bloom, RenderTarget2D brightPass, Vector2 brightGridRemainder, float sourcePixelsPerTexel)
        {
            EffectTechnique? down = bloom.Techniques["WideDown"], up = bloom.Techniques["WideUp"];
            if (down == null || up == null)
                return null;
            EnsureWideGlowLevels(brightPass);

            // The levels' texels stay fixed on the world. On a grid fixed to the screen a walk moves
            // the picture a texel and a half of the first level each frame, so every other frame it
            // falls between texels, and the colour test after the shrink keeps a different amount
            // of the light: the whole glow dimmed and came back at the frame rate. The grid moves
            // in steps of the smallest level's texel instead, and the layer is drawn back shifted
            // by what the steps left over. Levels are a step wider than the picture to hold that.
            Vector2 picture = new(brightPass.Width, brightPass.Height);
            Vector2 grid = new(_wideGlowLevels[0]!.Width * 2, _wideGlowLevels[0]!.Height * 2);
            Vector2 shift = WideGridShift(brightPass);
            Vector4 shrinkMap = new(grid.X / picture.X, grid.Y / picture.Y, -shift.X / picture.X, -shift.Y / picture.Y);
            Vector2 readShift = shift + brightGridRemainder / sourcePixelsPerTexel;
            GetParam(bloom, "WideReadMap")?.SetValue(new Vector4(picture.X / grid.X, picture.Y / grid.Y, readShift.X / grid.X, readShift.Y / grid.Y));

            Texture2D source = brightPass;
            bloom.CurrentTechnique = down;
            foreach (var level in _wideGlowLevels)
            {
                bool firstShrink = source == brightPass;
                GetParam(bloom, "TexelSize")?.SetValue(new Vector2(1f / source.Width, 1f / source.Height));
                GetParam(bloom, "WideFromLights")?.SetValue(firstShrink ? 1f : 0f);
                GetParam(bloom, "WideShrinkMap")?.SetValue(firstShrink ? shrinkMap : new Vector4(1f, 1f, 0f, 0f));
                Pass(spriteBatch, source, level!, bloom);
                source = level!;
            }

            bloom.CurrentTechnique = up;
            for (int i = _wideGlowLevels.Length - 1; i > 0; i--)
            {
                var smaller = _wideGlowLevels[i]!;
                GetParam(bloom, "TexelSize")?.SetValue(new Vector2(1f / smaller.Width, 1f / smaller.Height));
                PassAdd(spriteBatch, smaller, _wideGlowLevels[i - 1]!, bloom);
            }
            return _wideGlowLevels[0];
        }

        /// <summary>How many bright-pass texels the camera sits past the last step of the world-fixed
        /// grid. Nothing while a map picture is taken, which moves the camera its own way.</summary>
        private static Vector2 WideGridShift(RenderTarget2D brightPass)
        {
            if (MapScreenshotEffects.Active || Game1.viewport.Width <= 0)
                return Vector2.Zero;
            float texelsPerViewPixel = brightPass.Width / (float)Game1.viewport.Width;
            int cameraX = (int)Math.Floor(Game1.viewport.X * texelsPerViewPixel);
            int cameraY = (int)Math.Floor(Game1.viewport.Y * texelsPerViewPixel);
            return new Vector2(WrapToWideGridStep(cameraX), WrapToWideGridStep(cameraY));
        }

        private static int WrapToWideGridStep(int texels)
        {
            int wrapped = texels % WideGridStepTexels;
            return wrapped < 0 ? wrapped + WideGridStepTexels : wrapped;
        }

        private void EnsureWideGlowLevels(RenderTarget2D brightPass)
        {
            // A whole number of steps, so each level halves exactly and the grids stay in line,
            // with at least one step to spare past the picture for the shift.
            int width = (brightPass.Width / WideGridStepTexels + 2) * WideGridStepTexels;
            int height = (brightPass.Height / WideGridStepTexels + 2) * WideGridStepTexels;
            for (int i = 0; i < _wideGlowLevels.Length; i++)
            {
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
                var level = _wideGlowLevels[i];
                if (level != null && !level.IsDisposed && level.Width == width && level.Height == height && level.Format == brightPass.Format)
                    continue;
                level?.Dispose();
                _wideGlowLevels[i] = CreateRenderTarget(width, height, brightPass.Format);
            }
        }

        private void ReleaseWideGlowLevels()
        {
            for (int i = 0; i < _wideGlowLevels.Length; i++)
            {
                _wideGlowLevels[i]?.Dispose();
                _wideGlowLevels[i] = null;
            }
        }
    }
}
