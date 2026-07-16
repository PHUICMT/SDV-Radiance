using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Owns the offscreen render target and the "capture the frame, then draw it
    /// back to the screen" flow. Phase 0 is a passthrough: the frame is captured
    /// and blitted back unchanged, which validates that the hook works without
    /// altering the image. Later phases insert the EffectStack between capture
    /// and present.
    /// </summary>
    internal sealed class RenderPipeline : IDisposable
    {
        private readonly IMonitor _monitor;
        private readonly GraphicsDevice _device;
        private SpriteBatch _batch;
        private RenderTarget2D? _target;
        private bool _capturing;
        private bool _loggedOnce;

        public RenderPipeline(GraphicsDevice device, IMonitor monitor)
        {
            _device = device;
            _monitor = monitor;
            _batch = new SpriteBatch(device);
        }

        /// <summary>Recreate the render target if the back buffer size changed.</summary>
        private void EnsureTarget()
        {
            PresentationParameters pp = _device.PresentationParameters;
            int w = Math.Max(1, pp.BackBufferWidth);
            int h = Math.Max(1, pp.BackBufferHeight);

            if (_target == null || _target.Width != w || _target.Height != h)
            {
                _target?.Dispose();
                _target = new RenderTarget2D(
                    _device, w, h, false,
                    pp.BackBufferFormat, pp.DepthStencilFormat,
                    0, RenderTargetUsage.PreserveContents);
            }
        }

        /// <summary>
        /// Called before the game renders the frame. Redirects rendering into our
        /// offscreen target so we can post-process it afterwards.
        /// </summary>
        public void BeginCapture(bool debugLog)
        {
            try
            {
                EnsureTarget();
                _device.SetRenderTarget(_target);
                _device.Clear(Color.Black);
                _capturing = true;

                if (debugLog && !_loggedOnce)
                {
                    _monitor.Log($"Pipeline capture started ({_target!.Width}x{_target.Height}).", LogLevel.Debug);
                    _loggedOnce = true;
                }
            }
            catch (Exception ex)
            {
                _capturing = false;
                _monitor.Log($"BeginCapture failed, skipping post-processing this frame: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Called after the game finished rendering into our target. Resolves the
        /// target back to the screen. Phase 0: straight blit (no effect).
        /// </summary>
        public void EndCaptureAndPresent()
        {
            if (!_capturing || _target == null)
                return;

            _capturing = false;
            try
            {
                _device.SetRenderTarget(null);
                _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, null, null, /* effect */ null);
                _batch.Draw(_target, new Rectangle(0, 0, _target.Width, _target.Height), Color.White);
                _batch.End();
            }
            catch (Exception ex)
            {
                _monitor.Log($"EndCaptureAndPresent failed: {ex.Message}", LogLevel.Warn);
            }
        }

        public void Dispose()
        {
            _target?.Dispose();
            _target = null;
            _batch?.Dispose();
            _batch = null!;
        }
    }
}
