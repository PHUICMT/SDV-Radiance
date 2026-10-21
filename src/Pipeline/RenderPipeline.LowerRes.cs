using System;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - THE SOFT PARTS AT A LOWER RESOLUTION.
    ///
    /// <para>
    /// Five pieces of the chain are soft by nature and were worked out at more pixels than they can
    /// show: the cloud shadow's density mask, the bloom's bright pass and blur, the tilt-shift's
    /// blurred copy (all three at a half of the frame), and the flood's light, at the full frame.
    /// With <see cref="ModConfig.LighterSoftEffects"/> the first three run at a quarter, their blur
    /// reaching exactly as far across the screen as before, and the light is worked out on a half
    /// size copy of the frame and laid over the full one as a ratio (floodapply.fx), so every edge
    /// of the art stays as sharp as it was and only the light itself is computed coarser.
    /// </para>
    ///
    /// <para>
    /// Measured 15 Oct 2026 with the graphics card clocked down to a weak laptop's speed, the
    /// chain's own GPU time in one launch: farm at noon 12.2 to 8.7 ms, forest 12.4 to 6.9, town at
    /// night 11.9 to 7.7, the saloon at night 12.0 to 4.5. Window grabs in eight scenes differed
    /// from the full road by 0.1 to 1.8 levels of 255 on average, the same as two grabs of the full
    /// road a few seconds apart, and the author could not tell them apart on a side-by-side page.
    /// A camera-step crawl test and a walking flicker test found nothing either.
    /// </para>
    ///
    /// <para>
    /// The fifth, from 17 Oct 2026: the classic lighting (the one that runs with the flood off) is
    /// worked out at half size and multiplied into the full frame (lighting.fx LightTerm and
    /// LightApply). It needs no ratio: the classic light is a plain multiply of the frame.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>radiance_lowres: forces the lower road on or off for an A/B, or null to follow
        /// the setting.</summary>
        internal static bool? SoftEffectsForced;

        private RenderTarget2D? _quarterScratchA, _quarterScratchB;
        private RenderTarget2D? _floodHalfScene, _floodHalfLit;
        private RenderTarget2D? _classicHalfLight;
        private Effect? _floodApply;
        private bool _floodApplyTried;

        /// <summary>Whether this frame takes the lower road: always, never, or as the frame-time
        /// controller has decided (<see cref="RenderPipeline"/> AutoScale, its first step).</summary>
        private bool SoftEffectsLowered(ModConfig config)
        {
            if (SoftEffectsForced is bool forced)
                return forced;
            // A map screenshot is a picture to keep, worked out in full like its render scale.
            if (MapScreenshotEffects.Active)
                return false;
            return config.LighterSoftEffects switch
            {
                LighterSoftEffects.Always => true,
                LighterSoftEffects.WhenSlow => _autoSoftEffectsLowered,
                _ => false,
            };
        }

        /// <summary>The flood at half size, then the ratio over the full frame. False when it
        /// cannot run (the caller then draws the flood as usual).</summary>
        private bool DrawFloodAtHalf(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, Effect effect)
        {
            if (!_floodApplyTried)
            {
                _floodApplyTried = true;
                _floodApply = LoadEffect("floodapply.mgfxo");
            }
            if (_floodApply == null)
                return false;
            EnsureSoftEffectTargets(destination.Width, destination.Height, destination.Format);
            DrawFull(spriteBatch, source, _floodHalfScene!, null!);
            DrawFull(spriteBatch, _floodHalfScene!, _floodHalfLit!, effect);
            _floodApply.Parameters["LitHalfTexture"]?.SetValue(_floodHalfLit);
            _floodApply.Parameters["SceneHalfTexture"]?.SetValue(_floodHalfScene);
            _floodApply.CurrentTechnique = _floodApply.Techniques["Apply"];
            DrawFull(spriteBatch, source, destination, _floodApply);
            return true;
        }

        /// <summary>The classic lighting at half size: the light alone into a half-size target, then
        /// multiplied into the full frame. False when it cannot run (a lighting.mgfxo from before
        /// these techniques), and the caller then draws the full pass.</summary>
            // The classic pass's cost is the walls marched between each pixel and every lamp that
            // reaches it, 13 taps a lamp and 20-40 lamps in the saloon at night: 0.40 ms of an RTX
            // 5080 against the flood's 0.155, and about 5 ms more than the flood on a card held to
            // a fifteenth of its clock. The light has no full-resolution input (no art, no normals),
            // only the lamp list and a per-tile wall mask, so it loses nothing at half size.
        private bool DrawClassicLightAtHalf(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, Effect effect)
        {
            EffectTechnique? lightTerm = effect.Techniques["LightTerm"], lightApply = effect.Techniques["LightApply"];
            if (lightTerm == null || lightApply == null)
                return false;
            int width = Math.Max(1, destination.Width / 2), height = Math.Max(1, destination.Height / 2);
            if (_classicHalfLight == null || _classicHalfLight.IsDisposed || _classicHalfLight.Width != width
                || _classicHalfLight.Height != height || _classicHalfLight.Format != destination.Format)
            {
                _classicHalfLight?.Dispose();
                _classicHalfLight = CreateRenderTarget(width, height, destination.Format);
            }
            effect.CurrentTechnique = lightTerm;
            DrawFull(spriteBatch, source, _classicHalfLight, effect);
            effect.Parameters["LightTermTexture"]?.SetValue(_classicHalfLight);
            effect.CurrentTechnique = lightApply;
            DrawFull(spriteBatch, source, destination, effect);
            return true;
        }

        /// <summary>The lighter road's own targets for a chain of this size: the flood's half-size pair
        /// and the quarter pair. Nothing is made when they already fit.</summary>
        private void EnsureSoftEffectTargets(int chainWidth, int chainHeight, SurfaceFormat format)
        {
            int width = Math.Max(1, chainWidth / 2), height = Math.Max(1, chainHeight / 2);
            if (_floodHalfScene == null || _floodHalfScene.IsDisposed || _floodHalfScene.Width != width
                || _floodHalfScene.Height != height || _floodHalfScene.Format != format)
            {
                _floodHalfScene?.Dispose();
                _floodHalfLit?.Dispose();
                _floodHalfScene = CreateRenderTarget(width, height, format);
                _floodHalfLit = CreateRenderTarget(width, height, format);
            }
            if (_halfResolutionScratchA != null)
                QuarterScratch();
        }

        /// <summary>A pair of quarter-frame scratch targets beside the half-frame pair, sized from it.</summary>
        private (RenderTarget2D A, RenderTarget2D B) QuarterScratch()
        {
            var half = _halfResolutionScratchA!;
            int width = Math.Max(1, half.Width / 2), height = Math.Max(1, half.Height / 2);
            if (_quarterScratchA == null || _quarterScratchA.IsDisposed || _quarterScratchA.Width != width
                || _quarterScratchA.Height != height || _quarterScratchA.Format != half.Format)
            {
                _quarterScratchA?.Dispose();
                _quarterScratchB?.Dispose();
                _quarterScratchA = CreateRenderTarget(width, height, half.Format);
                _quarterScratchB = CreateRenderTarget(width, height, half.Format);
            }
            return (_quarterScratchA, _quarterScratchB!);
        }

        /// <summary>Gives back the lower road's own targets with the rest of the chain's.</summary>
        private void ReleaseSoftEffectTargets()
        {
            _quarterScratchA?.Dispose(); _quarterScratchB?.Dispose();
            _floodHalfScene?.Dispose(); _floodHalfLit?.Dispose(); _classicHalfLight?.Dispose();
            _quarterScratchA = _quarterScratchB = _floodHalfScene = _floodHalfLit = _classicHalfLight = null;
        }
    }
}
