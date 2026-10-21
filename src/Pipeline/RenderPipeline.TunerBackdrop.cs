using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    internal sealed partial class RenderPipeline
    {
        /// <summary>Set by the tuner while it is open with the glass panel: keep a blurred copy of
        /// the finished world frame for it to show through the panel.</summary>
        internal static bool TunerBackdropWanted;

        /// <summary>The blurred frame behind the glass panel, or null when this frame has none (the
        /// effect chain did not run, or nobody asked). Only the tick it was made on counts, so a
        /// frame the chain sat out never shows an old picture.</summary>
        internal static Texture2D? TunerBackdropNow
            => _tunerBackdropTick == Game1.ticks ? _tunerBackdropSmall : null;

        private static RenderTarget2D? _tunerBackdropQuarter;
        private static RenderTarget2D? _tunerBackdropMiddle;
        private static RenderTarget2D? _tunerBackdropSmall;
        private static int _tunerBackdropTick = -1;

        /// <summary>How far the frame is shrunk, in two steps of this and a last halving (a
        /// thirty-second of the frame), before it is stretched back over the panel: shrinking with
        /// linear filtering and stretching back is the blur. A sixteenth left the scene behind
        /// readable enough to fight the menu's words (author, 21 Oct 2026).</summary>
        private const int TunerBackdropStep = 4;

        /// <summary>Shrink the finished frame into a small target for the tuner's glass panel.
        /// Runs at the end of the chain, so the panel shows the picture with every effect on it.</summary>
        private void KeepTunerBackdrop(SpriteBatch spriteBatch, RenderTarget2D target)
        {
            if (!TunerBackdropWanted)
            {
                if (_tunerBackdropSmall != null)
                {
                    _tunerBackdropQuarter?.Dispose();
                    _tunerBackdropMiddle?.Dispose();
                    _tunerBackdropSmall.Dispose();
                    _tunerBackdropQuarter = null;
                    _tunerBackdropMiddle = null;
                    _tunerBackdropSmall = null;
                }
                return;
            }
            int quarterWidth = Math.Max(1, target.Width / TunerBackdropStep), quarterHeight = Math.Max(1, target.Height / TunerBackdropStep);
            int middleWidth = Math.Max(1, quarterWidth / TunerBackdropStep), middleHeight = Math.Max(1, quarterHeight / TunerBackdropStep);
            int smallWidth = Math.Max(1, middleWidth / 2), smallHeight = Math.Max(1, middleHeight / 2);
            if (_tunerBackdropMiddle == null || _tunerBackdropMiddle.Width != middleWidth || _tunerBackdropMiddle.Height != middleHeight)
            {
                _tunerBackdropMiddle?.Dispose();
                _tunerBackdropMiddle = new RenderTarget2D(_device, middleWidth, middleHeight, false, SurfaceFormat.Color, DepthFormat.None);
            }
            if (_tunerBackdropQuarter == null || _tunerBackdropQuarter.Width != quarterWidth || _tunerBackdropQuarter.Height != quarterHeight)
            {
                _tunerBackdropQuarter?.Dispose();
                _tunerBackdropQuarter = new RenderTarget2D(_device, quarterWidth, quarterHeight, false, SurfaceFormat.Color, DepthFormat.None);
            }
            if (_tunerBackdropSmall == null || _tunerBackdropSmall.Width != smallWidth || _tunerBackdropSmall.Height != smallHeight)
            {
                _tunerBackdropSmall?.Dispose();
                _tunerBackdropSmall = new RenderTarget2D(_device, smallWidth, smallHeight, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _device.SetRenderTarget(_tunerBackdropQuarter);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            spriteBatch.Draw(target, new Rectangle(0, 0, quarterWidth, quarterHeight), Color.White);
            spriteBatch.End();
            _device.SetRenderTarget(_tunerBackdropMiddle);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            spriteBatch.Draw(_tunerBackdropQuarter, new Rectangle(0, 0, middleWidth, middleHeight), Color.White);
            spriteBatch.End();
            _device.SetRenderTarget(_tunerBackdropSmall);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            spriteBatch.Draw(_tunerBackdropMiddle, new Rectangle(0, 0, smallWidth, smallHeight), Color.White);
            spriteBatch.End();
            _device.SetRenderTarget(target);
            _tunerBackdropTick = Game1.ticks;
        }
    }
}
