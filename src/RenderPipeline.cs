using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Runs the post-processing effect chain on the game's own render target.
    ///
    /// Unlike a naive "bind my own RenderTarget in Display.Rendering" approach
    /// (which clobbers Stardew's internal layer buffers — Game1.screen / the
    /// lightmap — and renders the world black), this captures whatever target
    /// the game currently has bound (<c>GraphicsDevice.GetRenderTargets()[0]</c>),
    /// copies it into a scratch buffer, then draws it back into that SAME target
    /// through the effect. Nothing the game owns is rebound or cleared.
    ///
    /// Called from <see cref="StardewModdingAPI.Events.IDisplayEvents.RenderedWorld"/>,
    /// so effects apply to the world layer only (the HUD is drawn afterwards and
    /// is left untouched — you don't want bloom on the UI).
    /// </summary>
    internal sealed class RenderPipeline : IDisposable
    {
        private readonly IMonitor _monitor;
        private readonly GraphicsDevice _device;
        private readonly string _modDir;

        private RenderTarget2D? _sceneRT;   // full-res copy of the captured frame
        private RenderTarget2D? _fullRT;    // full-res scratch for chaining stages
        private RenderTarget2D? _rtA;       // half-res scratch
        private RenderTarget2D? _rtB;       // half-res scratch
        private Effect? _bloom;
        private Effect? _colorGrade;

        private bool _loggedOnce;

        // Diagnostics (only touched when DebugLogging is on).
        private int _frames, _applied, _skipNoTarget, _sizeChanges;
        private int _lastW = -1, _lastH = -1;

        public RenderPipeline(GraphicsDevice device, IMonitor monitor, string modDir)
        {
            _device = device;
            _monitor = monitor;
            _modDir = modDir;
            LoadEffects();
        }

        private void LoadEffects()
        {
            _bloom = LoadEffect("bloom.mgfxo");
            _colorGrade = LoadEffect("colorgrade.mgfxo");
        }

        private Effect? LoadEffect(string file)
        {
            try
            {
                string path = Path.Combine(_modDir, "assets", file);
                if (File.Exists(path))
                {
                    var fx = new Effect(_device, File.ReadAllBytes(path));
                    _monitor.Log($"Loaded {file}.", LogLevel.Trace);
                    return fx;
                }
                _monitor.Log($"{file} not found at {path}; that effect is disabled.", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to load {file} (that effect is disabled): {ex.Message}", LogLevel.Warn);
            }
            return null;
        }

        public bool BloomAvailable => _bloom != null;

        /// <summary>True when at least one implemented effect is switched on and loaded.</summary>
        private bool AnyEffectActive(ModConfig config) =>
            (config.BloomEnabled && _bloom != null)
            || (config.ColorGradeEnabled && _colorGrade != null);

        private void EnsureTargets(int w, int h, SurfaceFormat format)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);

            if (_sceneRT != null && _sceneRT.Width == w && _sceneRT.Height == h && _sceneRT.Format == format)
                return;

            _sceneRT?.Dispose();
            _fullRT?.Dispose();
            _rtA?.Dispose();
            _rtB?.Dispose();

            _sceneRT = new RenderTarget2D(_device, w, h, false, format, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _fullRT = new RenderTarget2D(_device, w, h, false, format, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

            int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
            _rtA = new RenderTarget2D(_device, hw, hh, false, format, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _rtB = new RenderTarget2D(_device, hw, hh, false, format, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        }

        /// <summary>
        /// Post-process the current world layer in place.
        /// <paramref name="sb"/> is SMAPI's open sprite batch for the event; we flush it,
        /// run our passes, then reopen it with SMAPI's own parameters so SMAPI's trailing
        /// <c>End()</c> stays balanced.
        /// </summary>
        public void Apply(SpriteBatch sb, ModConfig config)
        {
            if (!AnyEffectActive(config))
                return;

            if (config.DebugLogging)
                _frames++;

            RenderTargetBinding[] bindings = _device.GetRenderTargets();
            if (bindings.Length == 0 || bindings[0].RenderTarget is not RenderTarget2D target)
            {
                if (config.DebugLogging)
                {
                    _skipNoTarget++;
                    MaybeLogDiag(config);
                }
                return; // drawing straight to the back buffer — nothing safe to capture this frame
            }

            int w = target.Width, h = target.Height;
            EnsureTargets(w, h, target.Format);

            if (config.DebugLogging)
            {
                _applied++;
                if (w != _lastW || h != _lastH)
                {
                    if (_lastW != -1) _sizeChanges++;
                    _lastW = w; _lastH = h;
                }
                if (!_loggedOnce)
                {
                    _monitor.Log($"Post-process {w}x{h} on world target, format={target.Format}.", LogLevel.Debug);
                    _loggedOnce = true;
                }
                MaybeLogDiag(config);
            }

            // Flush SMAPI's pending world draws into `target`.
            sb.End();

            try
            {
                // 1) Copy the live world (target) into _sceneRT so we can sample it while
                //    `target` is unbound.
                _device.SetRenderTarget(_sceneRT);
                sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                sb.Draw(target, new Rectangle(0, 0, w, h), Color.White);
                sb.End();

                // 2) Effect chain. Each stage reads `current` and writes the next buffer;
                //    the final active stage writes straight back into `target`.
                bool doBloom = config.BloomEnabled && _bloom != null;
                bool doGrade = config.ColorGradeEnabled && _colorGrade != null;

                Texture2D current = _sceneRT!;
                if (doBloom)
                {
                    RenderTarget2D dest = doGrade ? _fullRT! : target;
                    RenderBloom(sb, current, dest, config);
                    current = dest;
                }
                if (doGrade)
                    ColorGrade(sb, current, target, config);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Post-process failed, leaving frame unmodified this frame: {ex.Message}", LogLevel.Warn);
                // Best effort: make sure `target` still holds the original world.
                try
                {
                    _device.SetRenderTarget(target);
                    sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                    if (_sceneRT != null)
                        sb.Draw(_sceneRT, new Rectangle(0, 0, w, h), Color.White);
                    sb.End();
                }
                catch { /* give up on this frame */ }
            }

            // Reopen the batch exactly as SMAPI had it, so its trailing End() is balanced.
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        }

        /// <summary>Every ~120 frames, log how consistently the effect actually applied (flicker diagnosis).</summary>
        private void MaybeLogDiag(ModConfig config)
        {
            if (_frames < 120)
                return;
            _monitor.Log($"[diag] over {_frames} frames: applied={_applied}, skipped(no target)={_skipNoTarget}, sizeChanges={_sizeChanges}, lastSize={_lastW}x{_lastH}.", LogLevel.Debug);
            _frames = _applied = _skipNoTarget = _sizeChanges = 0;
        }

        /// <summary>bright pass → H blur → V blur → additive composite of <paramref name="source"/> into <paramref name="dest"/>.</summary>
        private void RenderBloom(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var bloom = _bloom!;
            var rtA = _rtA!;
            var rtB = _rtB!;
            int w = dest.Width, h = dest.Height;

            // 1) bright pass + Karis downsample: source (full) -> rtA (half).
            //    TexelSize here is the SOURCE (full-res) texel, for the 4-tap box offsets.
            bloom.Parameters["Threshold"]?.SetValue(config.BloomThreshold);
            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(1f / w, 1f / h));
            bloom.CurrentTechnique = bloom.Techniques["BrightPass"];
            Pass(sb, source, rtA, bloom);

            // 2) horizontal blur: rtA -> rtB
            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(1f / rtA.Width, 0f));
            bloom.CurrentTechnique = bloom.Techniques["BlurHorizontal"];
            Pass(sb, rtA, rtB, bloom);

            // 3) vertical blur: rtB -> rtA
            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(0f, 1f / rtB.Height));
            bloom.CurrentTechnique = bloom.Techniques["BlurVertical"];
            Pass(sb, rtB, rtA, bloom);

            // 4) composite: source + Intensity * blur(rtA) -> dest
            bloom.Parameters["Intensity"]?.SetValue(config.BloomIntensity);
            bloom.Parameters["BloomTexture"]?.SetValue(rtA);
            bloom.CurrentTechnique = bloom.Techniques["Composite"];
            _device.SetRenderTarget(dest);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, bloom);
            sb.Draw(source, new Rectangle(0, 0, w, h), Color.White);
            sb.End();
        }

        /// <summary>Single-pass parametric color grade of <paramref name="source"/> into <paramref name="dest"/>.</summary>
        private void ColorGrade(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _colorGrade!;

            float temp = config.ColorGradeTemperature;
            float sat = config.ColorGradeSaturation;
            if (config.ColorGradeAuto)
            {
                ComputeAuto(out float autoTemp, out float autoSatMul);
                temp += autoTemp;
                sat *= autoSatMul;
            }

            fx.Parameters["Strength"]?.SetValue(MathHelper.Clamp(config.ColorGradeStrength, 0f, 1f));
            fx.Parameters["Contrast"]?.SetValue(config.ColorGradeContrast);
            fx.Parameters["Saturation"]?.SetValue(sat);
            fx.Parameters["Temperature"]?.SetValue(MathHelper.Clamp(temp, -1f, 1f));
            fx.Parameters["Brightness"]?.SetValue(config.ColorGradeBrightness);
            fx.Parameters["ToneMap"]?.SetValue(config.ColorGradeToneMap ? 1f : 0f);
            fx.CurrentTechnique = fx.Techniques["ColorGrade"];

            _device.SetRenderTarget(dest);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, fx);
            sb.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            sb.End();
        }

        /// <summary>Auto mood grade: warm at sunset, cool at night, cooler/desaturated in rain/snow, seasonal bias.</summary>
        private static void ComputeAuto(out float temp, out float satMul)
        {
            temp = 0f;
            satMul = 1f;

            int t = Game1.timeOfDay; // 600 = 6am … 2600 = 2am
            if (t >= 1700 && t < 1930)
                temp += 0.25f * ((t - 1700) / 230f);              // dusk: ramp warm
            else if (t >= 1930 && t < 2100)
                temp += 0.25f - 0.55f * ((t - 1930) / 170f);      // warm → cool
            else if (t >= 2100 || t < 600)
                temp -= 0.30f;                                    // night: cool/blue

            if (Game1.isRaining) { temp -= 0.12f; satMul *= 0.85f; }
            if (Game1.isSnowing) { temp -= 0.15f; satMul *= 0.90f; }

            if (Game1.season == Season.Winter) temp -= 0.08f;
            else if (Game1.season == Season.Summer) temp += 0.05f;
        }

        /// <summary>Draw <paramref name="source"/> into <paramref name="dest"/> through <paramref name="effect"/>.</summary>
        private void Pass(SpriteBatch sb, Texture2D source, RenderTarget2D dest, Effect effect)
        {
            _device.SetRenderTarget(dest);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            sb.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            sb.End();
        }

        public void Dispose()
        {
            _sceneRT?.Dispose();
            _fullRT?.Dispose();
            _rtA?.Dispose();
            _rtB?.Dispose();
            _bloom?.Dispose();
            _colorGrade?.Dispose();
            _sceneRT = _fullRT = _rtA = _rtB = null;
            _bloom = _colorGrade = null;
        }
    }
}
