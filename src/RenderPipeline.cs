using System;
using System.Collections.Generic;
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
    /// Captures whatever target the game currently has bound
    /// (<c>GraphicsDevice.GetRenderTargets()[0]</c>) during RenderedWorld, runs the
    /// enabled stages through two full-res ping-pong buffers, and draws the result
    /// back into that SAME target. Nothing the game owns is rebound or cleared, so
    /// there's no black-world regression.
    /// </summary>
    internal sealed class RenderPipeline : IDisposable
    {
        private readonly IMonitor _monitor;
        private readonly GraphicsDevice _device;
        private readonly string _modDir;

        private RenderTarget2D? _sceneRT;   // full-res capture
        private RenderTarget2D? _fullA;     // full-res ping-pong
        private RenderTarget2D? _fullB;     // full-res ping-pong
        private RenderTarget2D? _rtA;       // half-res scratch
        private RenderTarget2D? _rtB;       // half-res scratch
        private Effect? _bloom;
        private Effect? _colorGrade;
        private Effect? _godRays;
        private Effect? _fog;
        private Effect? _cloudShadow;
        private Effect? _tiltShift;
        private Effect? _water;
        private Effect? _finishing;

        private Texture2D? _waterMask;         // per-tile water mask, aligned to the viewport
        private Color[]? _waterMaskBuf;
        private Vector2 _waterTilesPerScreen, _waterWorldTileOffset, _waterMaskSize;

        private bool _loggedOnce;
        private int _frames, _applied, _skipNoTarget, _sizeChanges;
        private int _lastW = -1, _lastH = -1;
        private Vector2 _lightUV; // screen-UV of the light source god rays emanate from (set per frame)
        private Vector2 _godRayUV; // eased light position so rays glide, not jump
        private float _godRayAmount; // 0..1 eased presence so rays fade in/out instead of popping
        private float _masterFade;              // 0..1 ease-in of the whole stack when it turns on

        // Metered auto-exposure: average the scene each frame (downsampled to a
        // tiny RT, read back a frame late so there's no GPU stall) and ease the
        // exposure toward a target so bright scenes (sand/snow/rooms) dim smoothly.
        private RenderTarget2D? _lumRT;
        private Color[]? _lumBuf;
        private bool _lumPrimed;
        private float _meteredExposure = 1f;

        public RenderPipeline(GraphicsDevice device, IMonitor monitor, string modDir)
        {
            _device = device;
            _monitor = monitor;
            _modDir = modDir;
            _bloom = LoadEffect("bloom.mgfxo");
            _colorGrade = LoadEffect("colorgrade.mgfxo");
            _godRays = LoadEffect("godrays.mgfxo");
            _fog = LoadEffect("fog.mgfxo");
            _cloudShadow = LoadEffect("cloudshadow.mgfxo");
            _tiltShift = LoadEffect("tiltshift.mgfxo");
            _water = LoadEffect("water.mgfxo");
            _finishing = LoadEffect("finishing.mgfxo");
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

        private bool AnyEffectActive(ModConfig c) =>
            (c.CloudShadowEnabled && _cloudShadow != null)
            || (c.GodRaysEnabled && _godRays != null)
            || (c.BloomEnabled && _bloom != null)
            || (c.FogEnabled && _fog != null)
            || (c.ColorGradeEnabled && _colorGrade != null)
            || (c.TiltShiftEnabled && _tiltShift != null)
            || (c.WaterEnabled && _water != null)
            || ((c.VignetteEnabled || c.ChromaticAberrationEnabled) && _finishing != null);

        private void EnsureTargets(int w, int h, SurfaceFormat format)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);

            if (_sceneRT != null && _sceneRT.Width == w && _sceneRT.Height == h && _sceneRT.Format == format)
                return;

            _sceneRT?.Dispose(); _fullA?.Dispose(); _fullB?.Dispose(); _rtA?.Dispose(); _rtB?.Dispose();

            _sceneRT = NewRT(w, h, format);
            _fullA = NewRT(w, h, format);
            _fullB = NewRT(w, h, format);
            _rtA = NewRT(Math.Max(1, w / 2), Math.Max(1, h / 2), format);
            _rtB = NewRT(Math.Max(1, w / 2), Math.Max(1, h / 2), format);
        }

        private RenderTarget2D NewRT(int w, int h, SurfaceFormat format) =>
            new(_device, w, h, false, format, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        public void Apply(SpriteBatch sb, ModConfig config)
        {
            if (!AnyEffectActive(config))
            {
                _masterFade = 0f; // reset so the stack fades back in next time it's enabled
                return;
            }

            if (config.DebugLogging) _frames++;

            RenderTargetBinding[] bindings = _device.GetRenderTargets();
            if (bindings.Length == 0 || bindings[0].RenderTarget is not RenderTarget2D target)
            {
                if (config.DebugLogging) { _skipNoTarget++; MaybeLogDiag(config); }
                return;
            }

            int w = target.Width, h = target.Height;
            EnsureTargets(w, h, target.Format);

            if (config.DebugLogging)
            {
                _applied++;
                if (w != _lastW || h != _lastH) { if (_lastW != -1) _sizeChanges++; _lastW = w; _lastH = h; }
                if (!_loggedOnce) { _monitor.Log($"Post-process {w}x{h}, format={target.Format}.", LogLevel.Debug); _loggedOnce = true; }
                MaybeLogDiag(config);
            }

            // Flush SMAPI's pending world draws into `target`.
            sb.End();

            try
            {
                _device.SetRenderTarget(_sceneRT);
                sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                sb.Draw(target, new Rectangle(0, 0, w, h), Color.White);
                sb.End();

                // Auto-exposure meters the captured scene and eases the exposure.
                if (config.ColorGradeEnabled && config.ColorGradeAuto)
                    UpdateAutoExposure(sb);

                // Build the active stage list (fixed order), then run them ping-pong so
                // the last stage writes straight back into the game's target.
                bool outdoors = Game1.currentLocation?.IsOutdoors ?? false;

                var stages = new List<Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>>();
                // Water ripple first (only if the current location actually has visible
                // water tiles), so everything downstream sees the refracted result.
                if (config.WaterEnabled && _water != null && BuildWaterMask(w, h)) stages.Add(RenderWater);
                // Cloud shadows drift over the ground — outdoors only, and first so later
                // effects (bloom/grade) see the shadowed scene.
                if (config.CloudShadowEnabled && _cloudShadow != null && outdoors) stages.Add(RenderCloudShadow);
                // God rays only when there's a real light source on screen (lamp/torch/fire).
                // Fade in/out (and glide the origin) so they never pop instantly when a
                // light scrolls on/off screen.
                if (config.GodRaysEnabled && _godRays != null)
                {
                    bool hasLight = TryGetLightUV(out Vector2 luv);
                    if (hasLight)
                        _godRayUV = _godRayAmount < 0.02f ? luv : Vector2.Lerp(_godRayUV, luv, 0.1f);
                    _godRayAmount += ((hasLight ? 1f : 0f) - _godRayAmount) * 0.05f; // ~0.5s fade
                    if (_godRayAmount > 0.01f) { _lightUV = _godRayUV; stages.Add(RenderGodRays); }
                }
                if (config.BloomEnabled && _bloom != null) stages.Add(RenderBloom);
                // Fog is a weak, patchy effect indoors (and covers the black border), so outdoors only.
                if (config.FogEnabled && _fog != null && outdoors) stages.Add(RenderFog);
                if (config.ColorGradeEnabled && _colorGrade != null) stages.Add(ColorGrade);
                // Tilt-shift (depth-of-field) after grading, so it blurs the graded image.
                if (config.TiltShiftEnabled && _tiltShift != null) stages.Add(RenderTiltShift);
                // Finishing (vignette + chromatic aberration): true camera-lens pass, last.
                if ((config.VignetteEnabled || config.ChromaticAberrationEnabled) && _finishing != null) stages.Add(RenderFinishing);

                Texture2D current = _sceneRT!;
                for (int i = 0; i < stages.Count; i++)
                {
                    RenderTarget2D dest = i == stages.Count - 1
                        ? target
                        : (ReferenceEquals(current, _fullA) ? _fullB! : _fullA!);
                    stages[i](sb, current, dest, config);
                    current = dest;
                }

                // Ease the whole stack in: blend the untouched scene back over the
                // result and let it fade out, so effects don't pop on when enabled
                // or after a load. `current` is the game's target at this point.
                _masterFade = Math.Min(1f, _masterFade + 0.045f);
                if (_masterFade < 1f && stages.Count > 0)
                {
                    _device.SetRenderTarget(target);
                    sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    sb.Draw(_sceneRT, new Rectangle(0, 0, w, h), Color.White * (1f - _masterFade));
                    sb.End();
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Post-process failed, leaving frame unmodified this frame: {ex.Message}", LogLevel.Warn);
                try
                {
                    _device.SetRenderTarget(target);
                    sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                    if (_sceneRT != null) sb.Draw(_sceneRT, new Rectangle(0, 0, w, h), Color.White);
                    sb.End();
                }
                catch { /* give up this frame */ }
            }

            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        }

        private void MaybeLogDiag(ModConfig config)
        {
            if (_frames < 120) return;
            _monitor.Log($"[diag] over {_frames} frames: applied={_applied}, skipped={_skipNoTarget}, sizeChanges={_sizeChanges}, size={_lastW}x{_lastH}.", LogLevel.Debug);
            _frames = _applied = _skipNoTarget = _sizeChanges = 0;
        }

        // ---- stages --------------------------------------------------------

        private void RenderCloudShadow(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _cloudShadow!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // Pass 1: generate the cloud-density mask at half-res (WorldOffset uses
            // the full-res dest so the anchor matches the composite step).
            fx.Parameters["Time"]?.SetValue(Time());
            fx.Parameters["Speed"]?.SetValue(config.CloudShadowSpeed);
            fx.Parameters["Scale"]?.SetValue(config.CloudShadowScale);
            fx.Parameters["Coverage"]?.SetValue(config.CloudShadowCoverage);
            fx.Parameters["WorldOffset"]?.SetValue(WorldOffset(dest.Width, dest.Height));
            fx.CurrentTechnique = fx.Techniques["Mask"];
            Pass(sb, source, rtA, fx);

            // Pass 2/3: separable Gaussian blur -> soft, feathered penumbra edges.
            fx.Parameters["TexelSize"]?.SetValue(new Vector2(1f / rtA.Width, 0f));
            fx.CurrentTechnique = fx.Techniques["BlurH"];
            Pass(sb, rtA, rtB, fx);

            fx.Parameters["TexelSize"]?.SetValue(new Vector2(0f, 1f / rtB.Height));
            fx.CurrentTechnique = fx.Techniques["BlurV"];
            Pass(sb, rtB, rtA, fx);

            // Pass 4: composite the blurred shadow onto the scene.
            fx.Parameters["Opacity"]?.SetValue(config.CloudShadowOpacity);
            fx.Parameters["ShadowTexture"]?.SetValue(rtA);
            fx.CurrentTechnique = fx.Techniques["Composite"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderGodRays(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _godRays!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // Rays emanate from a real in-world light source (converted to screen UV,
            // so they stay anchored to the scene as the camera pans).
            var lightPos = _lightUV;

            fx.Parameters["Threshold"]?.SetValue(config.GodRaysThreshold);
            fx.CurrentTechnique = fx.Techniques["Bright"];
            Pass(sb, source, rtA, fx);

            fx.Parameters["LightPos"]?.SetValue(lightPos);
            fx.Parameters["Density"]?.SetValue(config.GodRaysDensity);
            fx.Parameters["Decay"]?.SetValue(config.GodRaysDecay);
            fx.Parameters["Weight"]?.SetValue(0.5f);
            fx.CurrentTechnique = fx.Techniques["Rays"];
            Pass(sb, rtA, rtB, fx);

            fx.Parameters["Intensity"]?.SetValue(config.GodRaysIntensity * _godRayAmount);
            fx.Parameters["RaysTexture"]?.SetValue(rtB);
            fx.CurrentTechnique = fx.Techniques["Composite"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderBloom(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var bloom = _bloom!;
            var rtA = _rtA!;
            var rtB = _rtB!;
            int w = dest.Width, h = dest.Height;

            bloom.Parameters["Threshold"]?.SetValue(config.BloomThreshold);
            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(1f / w, 1f / h));
            bloom.CurrentTechnique = bloom.Techniques["BrightPass"];
            Pass(sb, source, rtA, bloom);

            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(1f / rtA.Width, 0f));
            bloom.CurrentTechnique = bloom.Techniques["BlurHorizontal"];
            Pass(sb, rtA, rtB, bloom);

            bloom.Parameters["TexelSize"]?.SetValue(new Vector2(0f, 1f / rtB.Height));
            bloom.CurrentTechnique = bloom.Techniques["BlurVertical"];
            Pass(sb, rtB, rtA, bloom);

            bloom.Parameters["Intensity"]?.SetValue(config.BloomIntensity);
            bloom.Parameters["BloomTexture"]?.SetValue(rtA);
            bloom.CurrentTechnique = bloom.Techniques["Composite"];
            DrawFull(sb, source, dest, bloom);
        }

        private void RenderFog(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _fog!;
            fx.Parameters["Time"]?.SetValue(Time());
            fx.Parameters["Speed"]?.SetValue(config.FogSpeed);
            fx.Parameters["Scale"]?.SetValue(config.FogScale);
            fx.Parameters["Density"]?.SetValue(config.FogDensity);
            fx.Parameters["TopBias"]?.SetValue(config.FogTopBias);
            fx.Parameters["FogColor"]?.SetValue(FogColor());
            fx.Parameters["WorldOffset"]?.SetValue(WorldOffset(dest.Width, dest.Height));
            fx.CurrentTechnique = fx.Techniques["Fog"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderTiltShift(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _tiltShift!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // blur at half-res: source(full) -> rtA (H) -> rtB (V)
            fx.Parameters["TexelSize"]?.SetValue(new Vector2(1f / rtA.Width, 0f));
            fx.CurrentTechnique = fx.Techniques["BlurH"];
            Pass(sb, source, rtA, fx);

            fx.Parameters["TexelSize"]?.SetValue(new Vector2(0f, 1f / rtB.Height));
            fx.CurrentTechnique = fx.Techniques["BlurV"];
            Pass(sb, rtA, rtB, fx);

            // composite sharp + blurred by vertical position.
            // Config stores intuitive "blur amount" (higher = more blur from that edge);
            // convert to sharp-band edges: more top blur pushes TopEdge down, more
            // bottom blur pulls BottomEdge up.
            fx.Parameters["TopEdge"]?.SetValue(MathHelper.Clamp(config.TiltShiftTopRatio, 0f, 1f) * 0.5f);
            fx.Parameters["BottomEdge"]?.SetValue(1f - MathHelper.Clamp(config.TiltShiftBottomRatio, 0f, 1f) * 0.5f);
            fx.Parameters["Strength"]?.SetValue(config.TiltShiftStrength);
            fx.Parameters["BlurTexture"]?.SetValue(rtB);
            fx.CurrentTechnique = fx.Techniques["Composite"];
            DrawFull(sb, source, dest, fx);
        }

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

            // _meteredExposure is measured & eased per frame in UpdateAutoExposure
            // (1.0 when auto is off), so bright scenes dim smoothly with no pop.
            fx.Parameters["Strength"]?.SetValue(MathHelper.Clamp(config.ColorGradeStrength, 0f, 1f));
            fx.Parameters["Contrast"]?.SetValue(config.ColorGradeContrast);
            fx.Parameters["Saturation"]?.SetValue(sat);
            fx.Parameters["Temperature"]?.SetValue(MathHelper.Clamp(temp, -1f, 1f));
            fx.Parameters["Brightness"]?.SetValue(config.ColorGradeBrightness * _meteredExposure);
            fx.Parameters["ToneMap"]?.SetValue(config.ColorGradeToneMap ? 1f : 0f);
            fx.CurrentTechnique = fx.Techniques["ColorGrade"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderWater(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _water!;
            // Weather/season drive how agitated the water is: choppier & faster in
            // rain/storm, sluggish in winter; sparkle fades when there's no sun.
            ComputeWaterDynamics(out float strengthMul, out float speedMul, out float sparkleMul);
            fx.Parameters["Time"]?.SetValue(Time());
            fx.Parameters["Strength"]?.SetValue(config.WaterStrength * strengthMul);
            fx.Parameters["Speed"]?.SetValue(config.WaterSpeed * speedMul);
            fx.Parameters["Sparkle"]?.SetValue(config.WaterSparkle * sparkleMul);
            fx.Parameters["WaterKind"]?.SetValue(WaterKind());
            fx.Parameters["TilesPerScreen"]?.SetValue(_waterTilesPerScreen);
            fx.Parameters["WorldTileOffset"]?.SetValue(_waterWorldTileOffset);
            fx.Parameters["MaskSize"]?.SetValue(_waterMaskSize);
            fx.Parameters["MaskTexture"]?.SetValue(_waterMask);
            fx.CurrentTechnique = fx.Techniques["Water"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderFinishing(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _finishing!;
            fx.Parameters["VignetteStrength"]?.SetValue(config.VignetteEnabled ? config.VignetteStrength : 0f);
            // Map the 0..1 UI value to a tiny UV offset so it stays subtle on pixel art.
            fx.Parameters["CAStrength"]?.SetValue(config.ChromaticAberrationEnabled ? config.ChromaticAberrationStrength * 0.03f : 0f);
            fx.CurrentTechnique = fx.Techniques["Finishing"];
            DrawFull(sb, source, dest, fx);
        }

        /// <summary>
        /// Build a per-tile water mask for the visible area from the current location,
        /// aligned to the viewport. Returns false (and skips the water stage) when the
        /// location has no water on screen, so we never distort a waterless frame.
        /// </summary>
        private bool BuildWaterMask(int w, int h)
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null)
                return false;

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTileX = (int)Math.Floor(vx / 64f);
            int startTileY = (int)Math.Floor(vy / 64f);
            int tilesW = Math.Max(1, w / 64 + 2);
            int tilesH = Math.Max(1, h / 64 + 2);
            int count = tilesW * tilesH;

            if (_waterMaskBuf == null || _waterMaskBuf.Length < count)
                _waterMaskBuf = new Color[count];

            bool any = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    bool water = loc.isWaterTile(startTileX + i, startTileY + j);
                    if (water) any = true;
                    _waterMaskBuf[j * tilesW + i] = water ? Color.White : Color.Transparent;
                }
            }

            if (!any)
                return false;

            if (_waterMask == null || _waterMask.Width != tilesW || _waterMask.Height != tilesH)
            {
                _waterMask?.Dispose();
                _waterMask = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _waterMask.SetData(_waterMaskBuf, 0, count);

            _waterTilesPerScreen = new Vector2(w / 64f, h / 64f);
            _waterWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _waterMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }

        // ---- helpers -------------------------------------------------------

        private static float Time() => Game1.ticks / 60f;

        /// <summary>Screen-UV of the largest-radius light source currently on screen, if any.</summary>
        private static bool TryGetLightUV(out Vector2 uv)
        {
            uv = Vector2.Zero;
            var lights = Game1.currentLightSources;
            if (lights == null || lights.Count == 0)
                return false;

            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);
            float best = -1f;

            foreach (var kv in lights)
            {
                LightSource ls = kv.Value;
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                float u = local.X / vw;
                float v = local.Y / vh;
                if (u < -0.25f || u > 1.25f || v < -0.25f || v > 1.25f)
                    continue; // off-screen

                float r = ls.radius.Value;
                if (r > best) { best = r; uv = new Vector2(u, v); }
            }
            return best > 0f;
        }

        private static Vector2 WorldOffset(int w, int h) =>
            new(Game1.viewport.X / (float)Math.Max(1, w), Game1.viewport.Y / (float)Math.Max(1, h));

        /// <summary>Fog tint by time of day: neutral haze by day, warm at dusk, blue at night.</summary>
        private static Vector3 FogColor()
        {
            int t = Game1.timeOfDay;
            Vector3 day = new(0.72f, 0.76f, 0.82f);
            Vector3 dusk = new(0.85f, 0.68f, 0.55f);
            Vector3 night = new(0.38f, 0.44f, 0.60f);
            if (t >= 1700 && t < 1930) return Vector3.Lerp(day, dusk, (t - 1700) / 230f);
            if (t >= 1930 && t < 2100) return Vector3.Lerp(dusk, night, (t - 1930) / 170f);
            if (t >= 2100 || t < 600) return night;
            return day;
        }

        private static void ComputeAuto(out float temp, out float satMul)
        {
            temp = 0f; satMul = 1f;
            int t = Game1.timeOfDay;
            if (t >= 1700 && t < 1930) temp += 0.25f * ((t - 1700) / 230f);
            else if (t >= 1930 && t < 2100) temp += 0.25f - 0.55f * ((t - 1930) / 170f);
            else if (t >= 2100 || t < 600) temp -= 0.30f;

            if (Game1.isRaining) { temp -= 0.12f; satMul *= 0.85f; }
            if (Game1.isSnowing) { temp -= 0.15f; satMul *= 0.90f; }
            if (Game1.season == Season.Winter) temp -= 0.08f;
            else if (Game1.season == Season.Summer) temp += 0.05f;
        }

        /// <summary>
        /// Measure the average scene luminance (downsampled to a tiny RT, read a
        /// frame late to avoid a GPU stall) and ease the exposure toward a target
        /// so bright scenes dim smoothly instead of popping. No-op unless auto is on.
        /// </summary>
        private void UpdateAutoExposure(SpriteBatch sb)
        {
            _lumRT ??= new RenderTarget2D(_device, 32, 32, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _lumBuf ??= new Color[32 * 32];

            if (_lumPrimed)
            {
                _lumRT.GetData(_lumBuf);
                float sum = 0f;
                for (int i = 0; i < _lumBuf.Length; i++)
                {
                    Color c = _lumBuf[i];
                    sum += (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
                }
                float lum = sum / _lumBuf.Length;
                // key/lum > 1 brightens, < 1 dims; clamp so it only gently corrects.
                float target = MathHelper.Clamp(0.5f / Math.Max(lum, 0.05f), 0.7f, 1.15f);
                _meteredExposure += (target - _meteredExposure) * 0.04f; // ~0.7s ease
            }

            _device.SetRenderTarget(_lumRT);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            sb.Draw(_sceneRT, new Rectangle(0, 0, 32, 32), Color.White);
            sb.End();
            _lumPrimed = true;
        }

        /// <summary>0 = still water (pond/river/farm), 1 = ocean/beach (big directional swell).</summary>
        private static float WaterKind()
        {
            string n = Game1.currentLocation?.Name ?? "";
            if (n.Contains("Beach") || n.Contains("Island") || n == "Docks")
                return 1f;
            return 0f;
        }

        /// <summary>Weather/season multipliers for ripple strength, speed, and sparkle.</summary>
        private static void ComputeWaterDynamics(out float strength, out float speed, out float sparkle)
        {
            strength = 1f; speed = 1f; sparkle = 1f;

            if (Game1.isLightning) { strength *= 2.0f; speed *= 1.7f; sparkle *= 0.25f; }   // storm
            else if (Game1.isRaining) { strength *= 1.5f; speed *= 1.4f; sparkle *= 0.4f; } // rain: choppy, no sun glints
            if (Game1.isSnowing) { strength *= 0.8f; speed *= 0.7f; sparkle *= 0.5f; }       // sluggish, overcast

            if (Game1.season == Season.Winter) { speed *= 0.8f; sparkle *= 0.8f; }           // cold, calmer
            else if (Game1.season == Season.Summer) sparkle *= 1.2f;                          // bright sun, more glint
        }

        private void Pass(SpriteBatch sb, Texture2D source, RenderTarget2D dest, Effect effect)
        {
            _device.SetRenderTarget(dest);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            sb.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            sb.End();
        }

        private void DrawFull(SpriteBatch sb, Texture2D source, RenderTarget2D dest, Effect effect)
        {
            _device.SetRenderTarget(dest);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            sb.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            sb.End();
        }

        public void Dispose()
        {
            _sceneRT?.Dispose(); _fullA?.Dispose(); _fullB?.Dispose(); _rtA?.Dispose(); _rtB?.Dispose(); _waterMask?.Dispose(); _lumRT?.Dispose();
            _bloom?.Dispose(); _colorGrade?.Dispose(); _godRays?.Dispose(); _fog?.Dispose(); _cloudShadow?.Dispose(); _tiltShift?.Dispose();
            _water?.Dispose(); _finishing?.Dispose();
            _sceneRT = _fullA = _fullB = _rtA = _rtB = null;
            _waterMask = null; _lumRT = null;
            _bloom = _colorGrade = _godRays = _fog = _cloudShadow = _tiltShift = _water = _finishing = null;
        }
    }
}
