using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Owns the offscreen render targets and the effect chain. Captures the whole
    /// frame into <see cref="_sceneRT"/>, runs the enabled post-processing passes,
    /// then blits the result to the back buffer. If no effect is active (or the
    /// shader failed to load) it falls back to a straight passthrough blit.
    /// </summary>
    internal sealed class RenderPipeline : IDisposable
    {
        private readonly IMonitor _monitor;
        private readonly GraphicsDevice _device;
        private readonly string _modDir;

        private SpriteBatch _batch;
        private RenderTarget2D? _sceneRT;   // full-res captured frame
        private RenderTarget2D? _rtA;       // half-res scratch
        private RenderTarget2D? _rtB;       // half-res scratch
        private Effect? _bloom;

        private bool _capturing;
        private bool _loggedOnce;

        public RenderPipeline(GraphicsDevice device, IMonitor monitor, string modDir)
        {
            _device = device;
            _monitor = monitor;
            _modDir = modDir;
            _batch = new SpriteBatch(device);
            LoadEffects();
        }

        private void LoadEffects()
        {
            try
            {
                string path = Path.Combine(_modDir, "assets", "bloom.mgfxo");
                if (File.Exists(path))
                {
                    _bloom = new Effect(_device, File.ReadAllBytes(path));
                    _monitor.Log("Loaded bloom.mgfxo.", LogLevel.Trace);
                }
                else
                {
                    _monitor.Log($"bloom.mgfxo not found at {path}; bloom disabled.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                _bloom = null;
                _monitor.Log($"Failed to load bloom shader (bloom disabled): {ex.Message}", LogLevel.Warn);
            }
        }

        public bool BloomAvailable => _bloom != null;

        private void EnsureTargets()
        {
            PresentationParameters pp = _device.PresentationParameters;
            int w = Math.Max(1, pp.BackBufferWidth);
            int h = Math.Max(1, pp.BackBufferHeight);

            if (_sceneRT == null || _sceneRT.Width != w || _sceneRT.Height != h)
            {
                _sceneRT?.Dispose();
                _rtA?.Dispose();
                _rtB?.Dispose();

                _sceneRT = new RenderTarget2D(_device, w, h, false,
                    pp.BackBufferFormat, pp.DepthStencilFormat, 0, RenderTargetUsage.PreserveContents);

                int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
                _rtA = new RenderTarget2D(_device, hw, hh, false, pp.BackBufferFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                _rtB = new RenderTarget2D(_device, hw, hh, false, pp.BackBufferFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
        }

        /// <summary>Redirect the game's rendering into our offscreen scene target.</summary>
        public void BeginCapture(bool debugLog)
        {
            try
            {
                EnsureTargets();
                _device.SetRenderTarget(_sceneRT);
                _device.Clear(Color.Black);
                _capturing = true;

                if (debugLog && !_loggedOnce)
                {
                    _monitor.Log($"Capture {_sceneRT!.Width}x{_sceneRT.Height}, bloom={(_bloom != null ? "loaded" : "off")}.", LogLevel.Debug);
                    _loggedOnce = true;
                }
            }
            catch (Exception ex)
            {
                _capturing = false;
                _monitor.Log($"BeginCapture failed, skipping post-processing this frame: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>Run the enabled effects and present to the back buffer.</summary>
        public void EndCaptureAndPresent(ModConfig config)
        {
            if (!_capturing || _sceneRT == null)
                return;
            _capturing = false;

            try
            {
                bool doBloom = config.BloomEnabled && _bloom != null && _rtA != null && _rtB != null;

                if (doBloom)
                    RenderBloom(config);
                else
                    Blit(_sceneRT, null, BlendState.Opaque, SamplerState.PointClamp, null);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Present failed, falling back to passthrough: {ex.Message}", LogLevel.Warn);
                try { Blit(_sceneRT, null, BlendState.Opaque, SamplerState.PointClamp, null); } catch { /* give up this frame */ }
            }
        }

        private void RenderBloom(ModConfig config)
        {
            var bloom = _bloom!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // 1) bright pass: scene (full) -> rtA (half)
            bloom.Parameters["Threshold"]?.SetValue(config.BloomThreshold);
            bloom.CurrentTechnique = bloom.Techniques["BrightPass"];
            Blit(_sceneRT!, rtA, BlendState.Opaque, SamplerState.LinearClamp, bloom);

            // 2) horizontal blur: rtA -> rtB
            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(1f / rtA.Width, 0f));
            bloom.CurrentTechnique = bloom.Techniques["BlurHorizontal"];
            Blit(rtA, rtB, BlendState.Opaque, SamplerState.LinearClamp, bloom);

            // 3) vertical blur: rtB -> rtA
            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(0f, 1f / rtB.Height));
            bloom.CurrentTechnique = bloom.Techniques["BlurVertical"];
            Blit(rtB, rtA, BlendState.Opaque, SamplerState.LinearClamp, bloom);

            // 4) composite: scene + Intensity * blur(rtA) -> back buffer
            bloom.Parameters["Intensity"]?.SetValue(config.BloomIntensity);
            bloom.Parameters["BloomTexture"]?.SetValue(rtA);
            bloom.CurrentTechnique = bloom.Techniques["Composite"];
            Blit(_sceneRT!, null, BlendState.Opaque, SamplerState.LinearClamp, bloom);
        }

        /// <summary>Draw <paramref name="source"/> onto <paramref name="dest"/> (null = back buffer) through an optional effect.</summary>
        private void Blit(Texture2D source, RenderTarget2D? dest, BlendState blend, SamplerState sampler, Effect? effect)
        {
            _device.SetRenderTarget(dest);
            int w = dest?.Width ?? _device.PresentationParameters.BackBufferWidth;
            int h = dest?.Height ?? _device.PresentationParameters.BackBufferHeight;

            _batch.Begin(SpriteSortMode.Immediate, blend, sampler, DepthStencilState.None, RasterizerState.CullNone, effect);
            _batch.Draw(source, new Rectangle(0, 0, w, h), Color.White);
            _batch.End();
        }

        public void Dispose()
        {
            _sceneRT?.Dispose();
            _rtA?.Dispose();
            _rtB?.Dispose();
            _bloom?.Dispose();
            _batch?.Dispose();
            _sceneRT = _rtA = _rtB = null;
            _bloom = null;
            _batch = null!;
        }
    }
}
