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
        private Effect? _lighting;

        // Phase 5 dynamic lighting: per-frame light list read from Game1.currentLightSources.
        private const int MaxLights = 16;
        private readonly Vector2[] _lightPos = new Vector2[MaxLights];
        private readonly Vector4[] _lightData = new Vector4[MaxLights]; // xyz = colour*boost, w = radiusUV
        private int _lightCount;

        private Texture2D? _waterMask;         // PIXEL-accurate water mask (16 texels/tile): true water tiles + the painted
                                               // water inside shore-tile art, minus opaque Buildings/Front art (pier posts,
                                               // bridges, lily pads). Effects end exactly at the real waterline.
        private Texture2D? _waterMaskCore;     // undilated per-TILE mask — true water bodies, used for the reflection's shoreline search
        private Color[]? _waterMaskCoreBuf;
        private bool[]? _waterBoolBuf;         // pre-dilation water flags (see BuildWaterMask)
        private bool[]? _waterBool2Buf;        // scratch for the dilation passes (candidate ring for art classification)
        private Color[]? _waterPixBuf;         // pixel-mask upload buffer (tilesW*16 × tilesH*16)
        private bool[]? _waterPixBits;         // scratch bits for the close/carve passes (effect channel)
        private bool[]? _waterPixBits2;        // march-channel bits (wider close: floats never block)
        private bool[]? _bigCarveBuf;          // per-tile: near-solid Buildings/Front art
        private bool[]? _bigSeedBuf;           // per-tile: near-solid AND connected to land (true structures)
        private short[]? _edgeBuf;             // per-pixel: top row of this column's water run (waterline map)
        private Color[]? _artBuf;              // 16×16 scratch for tile-art reads
        private readonly System.Collections.Generic.Dictionary<string, Texture2D?> _sheetTexCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), bool[]> _waterBitsCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), (bool[] bits, int count)> _solidBitsCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), (bool[] bits, int count)> _puddleBitsCache = new();
        private byte[]? _puddleTileBuf;        // per-tile puddle level: 0 no, 1 weak (rocky variant), 2 strong
        private bool[]? _puddlePixBits;        // per-pixel: came from the puddle classifier (softer effects)
        private int _lastWaterTx = int.MinValue, _lastWaterTy = int.MinValue, _lastWaterTick = int.MinValue;
        private GameLocation? _lastWaterLoc;
        private bool _waterAny;
        private readonly Vector4[] _lightArr = new Vector4[8];   // on-screen lights → water glimmer
        private Vector2 _waterTilesPerScreen, _waterWorldTileOffset, _waterMaskSize;

        private Texture2D? _occluderMask;      // per-tile occluder mask (walls/structures) for shadows
        private Color[]? _occluderMaskBuf;
        private Vector2 _occTilesPerScreen, _occWorldTileOffset, _occMaskSize;
        private bool _shadowsReady;            // true when an occluder mask was built this frame

        private bool _loggedOnce;
        private int _frames, _applied, _skipNoTarget, _sizeChanges;
        private int _lastW = -1, _lastH = -1;
        private Vector2 _lightUV; // screen-UV of the light source god rays emanate from (set per frame)
        private Vector2 _godRayUV; // eased light position so rays glide, not jump
        private float _godRayRadiusUV = 0.25f; // eased light radius (UV) — rays only form near the real light
        private float _godRayAmount; // 0..1 eased presence so rays fade in/out instead of popping
        private float _masterFade;              // 0..1 ease-in of the whole stack when it turns on

        // Reused per-frame stage list + cached stage delegates (see Apply).
        private readonly List<Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>> _stages = new();
        private Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>?
            _dLighting, _dWater, _dCloud, _dGodRays, _dBloom, _dFog, _dGrade, _dTilt, _dFinish, _dFlood;

        // Phase L1 — flood-propagation GI lightmap (see FloodLightmap.cs).
        private Effect? _floodFx;
        private readonly FloodLightmap _flood = new();

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
            _floodFx = LoadEffect("floodlight.mgfxo");
            _finishing = LoadEffect("finishing.mgfxo");
            _lighting = LoadEffect("lighting.mgfxo");
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

        private bool AnyEffectActive(ModConfig c) =>
            (c.FloodLightingEnabled && _floodFx != null)
            || (c.LightingEnabled && _lighting != null)
            || (c.CloudShadowEnabled && _cloudShadow != null)
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

                // Reused list + cached delegates: method-group conversion allocates a new
                // delegate per call, which at 60fps × up to 9 stages is constant GC churn.
                var stages = _stages;
                stages.Clear();
                _dLighting ??= RenderLighting; _dWater ??= RenderWater; _dCloud ??= RenderCloudShadow;
                _dGodRays ??= RenderGodRays; _dBloom ??= RenderBloom; _dFog ??= RenderFog;
                _dGrade ??= ColorGrade; _dTilt ??= RenderTiltShift; _dFinish ??= RenderFinishing;
                _dFlood ??= RenderFloodLight;
                // Lighting first, so everything downstream (bloom/god rays/grade) sees the
                // lit result. FLOOD lighting (occlusion-aware GI lightmap) supersedes the
                // old screen-space lighting stage when enabled — they model the same thing.
                if (config.FloodLightingEnabled && _floodFx != null && _flood.Build(_device, w, h, config))
                {
                    BuildLightList(w, h, config);       // direct-light pools (shader term)
                    _floodOccReady = BuildFloodOccluders(w, h);
                    stages.Add(_dFlood);
                }
                else if (config.LightingEnabled && _lighting != null && BuildLightList(w, h, config))
                {
                    _shadowsReady = config.LightingShadows && BuildOccluderMask(w, h);
                    stages.Add(_dLighting);
                }
                // Water ripple first (only if the current location actually has visible
                // water tiles), so everything downstream sees the refracted result.
                if (config.WaterEnabled && _water != null && BuildWaterMask(w, h)) stages.Add(_dWater);
                // Cloud shadows drift over the ground — outdoors only, and first so later
                // effects (bloom/grade) see the shadowed scene. They are SUNLIGHT (or moonlight)
                // being blocked, so they fade with dusk and at night exist only under a bright
                // moon — never stamped over lamp-lit ground on a dark night.
                _cloudDayFactor = CloudDayFactor();
                if (config.CloudShadowEnabled && _cloudShadow != null && outdoors && _cloudDayFactor > 0.02f)
                    stages.Add(_dCloud);
                // God rays only when there's a real light source on screen (lamp/torch/fire).
                // Fade in/out (and glide the origin) so they never pop instantly when a
                // light scrolls on/off screen.
                if (config.GodRaysEnabled && _godRays != null)
                {
                    bool hasLight = TryGetLightUV(out Vector2 luv, out float lr);
                    if (hasLight)
                    {
                        _godRayUV = _godRayAmount < 0.02f ? luv : Vector2.Lerp(_godRayUV, luv, 0.1f);
                        _godRayRadiusUV = _godRayAmount < 0.02f ? lr : MathHelper.Lerp(_godRayRadiusUV, lr, 0.1f);
                    }
                    _godRayAmount += ((hasLight ? 1f : 0f) - _godRayAmount) * 0.05f; // ~0.5s fade
                    if (_godRayAmount > 0.01f) { _lightUV = _godRayUV; stages.Add(_dGodRays); }
                }
                if (config.BloomEnabled && _bloom != null) stages.Add(_dBloom);
                // Fog is a weak, patchy effect indoors (and covers the black border), so outdoors only.
                if (config.FogEnabled && _fog != null && outdoors) stages.Add(_dFog);
                if (config.ColorGradeEnabled && _colorGrade != null) stages.Add(_dGrade);
                // Tilt-shift (depth-of-field) after grading, so it blurs the graded image.
                if (config.TiltShiftEnabled && _tiltShift != null) stages.Add(_dTilt);
                // Finishing (vignette + chromatic aberration): true camera-lens pass, last.
                if ((config.VignetteEnabled || config.ChromaticAberrationEnabled) && _finishing != null) stages.Add(_dFinish);

                Texture2D current = _sceneRT!;
                for (int i = 0; i < stages.Count; i++)
                {
                    RenderTarget2D dest = i == stages.Count - 1
                        ? target
                        : (ReferenceEquals(current, _fullA) ? _fullB! : _fullA!);
                    stages[i](sb, current, dest, config);
                    current = dest;
                }

                // Every config-enabled stage can still bail at runtime (indoors, no water,
                // no lights, rays faded out). If none ran, the device is still on _sceneRT
                // from the capture — restore the game's target or everything drawn after
                // us this frame lands in our scratch buffer.
                if (stages.Count == 0)
                    _device.SetRenderTarget(target);

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
                // A stage may have thrown between a Begin and its End — close the batch
                // first, or the recovery Begin below throws too (and would escape).
                try { sb.End(); } catch { }
                try
                {
                    _device.SetRenderTarget(target);
                    sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                    if (_sceneRT != null) sb.Draw(_sceneRT, new Rectangle(0, 0, w, h), Color.White);
                    sb.End();
                }
                catch { /* give up this frame */ }
            }
            finally
            {
                // Whatever happened above, the game's own target must be bound before we
                // hand the (reopened) batch back to SMAPI.
                try
                {
                    var bound = _device.GetRenderTargets();
                    if (bound.Length == 0 || !ReferenceEquals(bound[0].RenderTarget, target))
                        _device.SetRenderTarget(target);
                }
                catch { }
            }

            try
            {
                sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            }
            catch (InvalidOperationException)
            {
                // Batch already open (an exotic failure path left it running) — that's the
                // state SMAPI expects anyway, so continue.
            }
        }

        private void MaybeLogDiag(ModConfig config)
        {
            if (_frames < 120) return;
            _monitor.Log($"[diag] over {_frames} frames: applied={_applied}, skipped={_skipNoTarget}, sizeChanges={_sizeChanges}, size={_lastW}x{_lastH}.", LogLevel.Debug);
            _frames = _applied = _skipNoTarget = _sizeChanges = 0;
        }

        // ---- stages --------------------------------------------------------

        private float _cloudDayFactor = 1f;

        /// <summary>How strongly celestial light is present (cloud shadows scale by this):
        /// 1 in daylight, fading over the last ~40 min before the seasonal dark time, then
        /// moon-phase-scaled at night (a dark night has no light for clouds to block).</summary>
        private static float CloudDayFactor()
        {
            int t = Game1.timeOfDay;
            int trulyDark;
            try { trulyDark = Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { trulyDark = 2000; }
            int mins = (t / 100) * 60 + t % 100;
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            float moon = 0.35f * ShadowRenderer.MoonStrength();
            if (mins >= m1)
                return moon;
            return Math.Max(moon, MathHelper.Clamp((m1 - mins) / 40f, 0f, 1f));
        }

        private void RenderCloudShadow(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _cloudShadow!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // The hard straight seam reported after long sessions was a float-precision cliff:
            // Time (and so drift = Time*Speed) grows without bound as the session runs, and once
            // the sin()-hash's input gets large enough, frac()/floor() lose precision at one x
            // line and one y line — reading as a hard "L" seam. Wrap ticks into a bounded range
            // (a multiple of 60 so seconds stay whole) so Time never grows large enough to hit it;
            // the wrap period is long enough (100 minutes) that the one-frame pattern jump at the
            // seam is imperceptible during actual play.
            float wrappedTime = (Game1.ticks % 360000) / 60f;

            // Pass 1: generate the cloud-density mask at half-res (WorldOffset uses
            // the full-res dest so the anchor matches the composite step).
            fx.Parameters["Time"]?.SetValue(wrappedTime);
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
            fx.Parameters["Opacity"]?.SetValue(config.CloudShadowOpacity * _cloudDayFactor);
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

            float aspect = Game1.viewport.Width / (float)Math.Max(1, Game1.viewport.Height);

            // Bright pass is GATED to a disk around the real light, so only pixels near THIS
            // light streak into rays — distant bright scenery (flowers, white hair) no longer does.
            fx.Parameters["Threshold"]?.SetValue(config.GodRaysThreshold);
            fx.Parameters["LightPos"]?.SetValue(lightPos);
            fx.Parameters["LightRadius"]?.SetValue(_godRayRadiusUV);
            fx.Parameters["Aspect"]?.SetValue(aspect);
            // Player pixels are not light emitters — same silhouette exclusion as the water.
            var grWho = Game1.player;
            var grMask = ShadowRenderer.PlayerMask;
            var grRect = new Vector4(2f, 2f, -1f, -1f);
            if (grWho != null && grMask != null)
            {
                Rectangle box = grWho.GetBoundingBox();
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom - 10f));
                Vector2 tl = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                grRect = new Vector4(tl.X / dest.Width, tl.Y / dest.Height,
                    (tl.X + ShadowRenderer.PlayerRtW) / dest.Width, (tl.Y + ShadowRenderer.PlayerRtH) / dest.Height);
            }
            fx.Parameters["PlayerRect"]?.SetValue(grRect);
            fx.Parameters["PlayerMaskTexture"]?.SetValue(grMask);
            // With flood GI active, only lit pixels may emit rays (kills rays from bright
            // sprites in unlit corners; lamp glow zones still stream at night).
            bool floodGate = config.FloodLightingEnabled && _flood.Texture != null;
            fx.Parameters["FloodGate"]?.SetValue(floodGate ? 1f : 0f);
            if (floodGate)
            {
                fx.Parameters["FloodMapTexture"]?.SetValue(_flood.Texture);
                fx.Parameters["FloodTilesPerScreen"]?.SetValue(new Vector2(dest.Width / 64f, dest.Height / 64f));
                fx.Parameters["FloodWorldTileOffset"]?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
                fx.Parameters["FloodMapOrigin"]?.SetValue(_flood.Origin);
                fx.Parameters["FloodMapSize"]?.SetValue(_flood.MapSize);
            }
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
            fx.Parameters["Mode"]?.SetValue(config.TiltShiftMode == TiltShiftFocus.Radial ? 1f : 0f);
            fx.Parameters["Center"]?.SetValue(PlayerScreenUV());
            fx.Parameters["Aspect"]?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            fx.Parameters["RadRadius"]?.SetValue(MathHelper.Clamp(config.TiltShiftRadius, 0.05f, 0.9f));
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

        private bool _floodOccReady;
        private readonly Vector2[] _floodLightPos = new Vector2[8];
        private readonly Vector4[] _floodLightCol = new Vector4[8];

        private void RenderFloodLight(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _floodFx!;
            fx.Parameters["LightMapTexture"]?.SetValue(_flood.Texture);
            fx.Parameters["TilesPerScreen"]?.SetValue(new Vector2(dest.Width / 64f, dest.Height / 64f));
            fx.Parameters["WorldTileOffset"]?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            fx.Parameters["MapOrigin"]?.SetValue(_flood.Origin);
            fx.Parameters["MapSize"]?.SetValue(_flood.MapSize);
            fx.Parameters["Strength"]?.SetValue(MathHelper.Clamp(config.FloodLightingStrength, 0f, 1f));
            fx.Parameters["AmbientFloor"]?.SetValue(0.10f);

            // Direct pools with per-light shadows: the brightest 8 on-screen lights (from
            // BuildLightList) + the occluder mask. Direct is scaled DOWN vs the classic
            // lighting stage because the flood map already carries the indirect spill.
            int n = 0;
            for (int i = 0; i < _lightCount && n < 8; i++, n++)
            {
                _floodLightPos[n] = _lightPos[i];
                var d = _lightData[i];
                _floodLightCol[n] = new Vector4(d.X * 0.55f, d.Y * 0.55f, d.Z * 0.55f, d.W);
            }
            for (int i = n; i < 8; i++) { _floodLightPos[i] = Vector2.Zero; _floodLightCol[i] = Vector4.Zero; }
            fx.Parameters["LightPosArr"]?.SetValue(_floodLightPos);
            fx.Parameters["LightColArr"]?.SetValue(_floodLightCol);
            fx.Parameters["DirectCount"]?.SetValue((float)(_floodOccReady ? n : 0));
            fx.Parameters["Aspect"]?.SetValue(dest.Width / (float)Math.Max(1, dest.Height));
            fx.Parameters["OccluderTexture"]?.SetValue(_occluderMask);
            fx.Parameters["OccOrigin"]?.SetValue(new Vector2((float)Math.Floor(Game1.viewport.X / 64f), (float)Math.Floor(Game1.viewport.Y / 64f)));
            fx.Parameters["OccMapSize"]?.SetValue(_occMaskSize);
            fx.Parameters["ShadowStrength"]?.SetValue(MathHelper.Clamp(config.FloodShadowStrength, 0f, 1f));

            fx.CurrentTechnique = fx.Techniques["FloodLight"];
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
            fx.Parameters["ReflectStrength"]?.SetValue(config.WaterReflection ? config.WaterReflectStrength : 0f);
            fx.Parameters["WaterKind"]?.SetValue(WaterKind());
            fx.Parameters["TilesPerScreen"]?.SetValue(_waterTilesPerScreen);
            fx.Parameters["WorldTileOffset"]?.SetValue(_waterWorldTileOffset);
            fx.Parameters["MaskSize"]?.SetValue(_waterMaskSize);
            fx.Parameters["MaskTexture"]?.SetValue(_waterMask);
            fx.Parameters["MaskCoreTexture"]?.SetValue(_waterMaskCore);
            fx.Parameters["SparkleDensity"]?.SetValue(config.WaterSparkleDensity);
            // Player SILHOUETTE mask (the shadow system's per-frame bake) in buffer UV —
            // ring-tile water effects skip exactly the player's own pixels, so a blue outfit
            // on a pier never ripples while the water right beside them stays animated.
            var who = Game1.player;
            var pmask = ShadowRenderer.PlayerMask;
            var playerRect = new Vector4(2f, 2f, -1f, -1f);   // empty box (never matches)
            if (who != null && pmask != null)
            {
                Rectangle box = who.GetBoundingBox();
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom - 10f));
                Vector2 tl = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                playerRect = new Vector4(tl.X / dest.Width, tl.Y / dest.Height,
                    (tl.X + ShadowRenderer.PlayerRtW) / dest.Width, (tl.Y + ShadowRenderer.PlayerRtH) / dest.Height);
            }
            fx.Parameters["PlayerRect"]?.SetValue(playerRect);
            fx.Parameters["PlayerMaskTexture"]?.SetValue(pmask);

            // Time-of-day / weather dressing: golden-hour sparkle, star reflections and
            // lamp glimmer after dusk, raindrop rings while raining.
            int tnow = Game1.timeOfDay;
            int mins = (tnow / 100) * 60 + tnow % 100;
            float sunWarm = 0f;
            if (!Game1.isRaining && tnow < 1900)
            {
                float dd = MathHelper.Clamp((tnow - 1200) / 600f, -1f, 1f);
                sunWarm = MathHelper.Clamp((Math.Abs(dd) - 0.55f) / 0.45f, 0f, 1f);
            }
            float nightGlow = MathHelper.Clamp((mins - 1140) / 90f, 0f, 1f);   // 19:00 → 20:30
            fx.Parameters["SunWarm"]?.SetValue(sunWarm);
            fx.Parameters["NightGlow"]?.SetValue(nightGlow);
            fx.Parameters["MoonGlow"]?.SetValue(ShadowRenderer.MoonStrength());
            fx.Parameters["RainAmt"]?.SetValue(Game1.isRaining ? 1f : 0f);

            int lc = 0;
            if (nightGlow > 0f && Game1.currentLightSources != null)
            {
                foreach (var kv in Game1.currentLightSources.Values)
                {
                    if (lc >= 8)
                        break;
                    Vector2 sp = Game1.GlobalToLocal(Game1.viewport, kv.position.Value);
                    if (sp.X < -160 || sp.X > dest.Width + 160 || sp.Y < -160 || sp.Y > dest.Height + 160)
                        continue;
                    _lightArr[lc++] = new Vector4(sp.X / dest.Width, sp.Y / dest.Height, kv.radius.Value, 0.9f);
                }
            }
            fx.Parameters["LightCount"]?.SetValue((float)lc);
            fx.Parameters["Lights"]?.SetValue(_lightArr);

            // Wading: are the player's feet on water pixels? (mask texel = 4 world px)
            float pin = 0f;
            if (who != null && _waterPixBuf != null && _waterMask != null)
            {
                Rectangle bb = who.GetBoundingBox();
                int mxp = bb.Center.X / 4 - _lastWaterTx * 16;
                int myp = (bb.Bottom - 4) / 4 - _lastWaterTy * 16;
                if (mxp >= 0 && myp >= 0 && mxp < _waterMask.Width && myp < _waterMask.Height
                    && _waterPixBuf[myp * _waterMask.Width + mxp].R > 100)
                    pin = 1f;
            }
            fx.Parameters["PlayerInWater"]?.SetValue(pin);

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

        private void RenderLighting(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _lighting!;
            fx.Parameters["AmbientColor"]?.SetValue(ComputeLightingAmbient(config));
            fx.Parameters["Aspect"]?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            fx.Parameters["LightPos"]?.SetValue(_lightPos);
            fx.Parameters["LightData"]?.SetValue(_lightData);
            // Allow pools to slightly exceed 1 so lamps glow a touch; keep it modest.
            fx.Parameters["Overbright"]?.SetValue(1.0f + 0.4f * MathHelper.Clamp(config.LightingBoost, 0f, 2f));
            // Occluder shadows: only when enabled AND a mask was built this frame.
            if (_shadowsReady && _occluderMask != null)
            {
                fx.Parameters["ShadowStrength"]?.SetValue(MathHelper.Clamp(config.LightingShadowStrength, 0f, 1f));
                fx.Parameters["OccluderTexture"]?.SetValue(_occluderMask);
                fx.Parameters["OccTilesPerScreen"]?.SetValue(_occTilesPerScreen);
                fx.Parameters["OccWorldTileOffset"]?.SetValue(_occWorldTileOffset);
                fx.Parameters["OccMaskSize"]?.SetValue(_occMaskSize);
            }
            else
            {
                // Disabled: bind a valid texture and 0 strength so nothing samples garbage.
                fx.Parameters["ShadowStrength"]?.SetValue(0f);
                fx.Parameters["OccluderTexture"]?.SetValue(source);
            }
            fx.CurrentTechnique = fx.Techniques["Lighting"];
            DrawFull(sb, source, dest, fx);
        }

        /// <summary>
        /// Read the on-screen light sources into the shader arrays. Returns false
        /// (skipping the lighting stage) only when there's nothing to do — i.e. no
        /// lights AND no ambient darkening to apply this frame.
        /// </summary>
        private bool BuildLightList(int w, int h, ModConfig config)
        {
            _lightCount = 0;
            for (int i = 0; i < MaxLights; i++) { _lightPos[i] = Vector2.Zero; _lightData[i] = Vector4.Zero; }

            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);

            // Warm tint for the light pools (candle-orange at Warmth=1).
            float warmth = MathHelper.Clamp(config.LightingWarmth, 0f, 1f);
            Vector3 warm = Vector3.Lerp(Vector3.One, new Vector3(1.0f, 0.78f, 0.5f), warmth);
            float boost = MathHelper.Clamp(config.LightingBoost, 0f, 2f);
            float radiusScale = MathHelper.Clamp(config.LightingRadiusScale, 0.2f, 3f);

            var lights = Game1.currentLightSources;
            if (lights != null && lights.Count > 0)
            {
                GameLocation? lloc = Game1.currentLocation;
                foreach (var kv in lights)
                {
                    if (_lightCount >= MaxLights)
                        break;

                    LightSource ls = kv.Value;
                    if (lloc != null && !ShadowRenderer.WindowGlowing(lloc, ls))
                        continue;   // stale/dark window light — not emitting
                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    float u = local.X / vw;
                    float v = local.Y / vh;

                    // Light reach ≈ radius*256 world px (matches the game's own cull box);
                    // convert to UV height units so the shader draws a round pool.
                    float radiusUv = ls.radius.Value * 256f / vh * radiusScale;
                    if (u < -radiusUv * 2f || u > 1f + radiusUv * 2f || v < -radiusUv * 2f || v > 1f + radiusUv * 2f)
                        continue; // fully off-screen

                    // Vanilla stores light colour as the INVERSE (Black = full bright
                    // white light), so invert to get the visible glow colour.
                    Color c = ls.color.Value;
                    Vector3 glow = new(1f - c.R / 255f, 1f - c.G / 255f, 1f - c.B / 255f);
                    if (glow.LengthSquared() < 0.01f)
                        glow = Vector3.One; // pure-white source stored as black-ish
                    // Two-tone: indoor windows are daylight (cool) — everything else warm; fire
                    // lights breathe with a slow flame flicker.
                    bool coolDaylight = lloc != null && !lloc.IsOutdoors
                        && ls.lightContext.Value == LightSource.LightContext.WindowLight;
                    Vector3 tone = coolDaylight
                        ? Vector3.Lerp(Vector3.One, new Vector3(0.82f, 0.92f, 1.12f), warmth)
                        : warm;
                    glow *= tone * boost * ShadowRenderer.FireFlicker(ls.position.Value, ls.textureIndex.Value);

                    _lightPos[_lightCount] = new Vector2(u, v);
                    _lightData[_lightCount] = new Vector4(glow, Math.Max(0.02f, radiusUv));
                    _lightCount++;
                }
            }

            // Run the stage if we have lights, or if we're darkening a flat interior
            // (so the room actually gets darker even with no lamps in view).
            bool darkening = ComputeLightingAmbient(config) != Vector3.One;

            // Diagnose the "fireplace/lamp casts a shadow but emits no visible light pool" report:
            // our pools only lift a DARKENED base, so if a room has lights yet isn't being
            // darkened (non-white ambient), the pools are invisible. Log that case once.
            if (config.DebugLogging && !_loggedLightDiag && _lightCount > 0)
            {
                _loggedLightDiag = true;
                _monitor.Log($"[light] loc={Game1.currentLocation?.Name} outdoors={Game1.currentLocation?.IsOutdoors} " +
                             $"ambient={Game1.ambientLight} darkening={darkening} lights={_lightCount} " +
                             (darkening ? "(pools should show)" : "-> NOT darkening, so light pools won't be visible"), LogLevel.Debug);
            }

            return _lightCount > 0 || darkening;
        }

        private bool _loggedLightDiag;

        /// <summary>
        /// The per-pixel ambient multiplier for unlit areas. We only darken flat-bright
        /// interiors that the game leaves unlit (its own lightmap isn't drawn there);
        /// outdoors, mines, and scripted-dark rooms already get vanilla lighting, so we
        /// return white there to avoid double-darkening.
        /// </summary>
        private static Vector3 ComputeLightingAmbient(ModConfig config)
        {
            bool outdoors = Game1.currentLocation?.IsOutdoors ?? false;
            bool vanillaLit = outdoors
                || Game1.currentLocation is StardewValley.Locations.MineShaft
                || !Game1.ambientLight.Equals(Color.White);
            if (vanillaLit)
                return Vector3.One;

            float dark = MathHelper.Clamp(config.LightingIndoorDarkness, 0f, 0.95f);
            int t = Game1.timeOfDay;
            if (t >= 1900 || t < 600)
                dark = MathHelper.Clamp(dark + config.LightingNightDarkness, 0f, 0.95f);

            // Cool moonlight-ish tint for the darkened room.
            Vector3 darkTint = new(0.45f, 0.48f, 0.62f);
            return Vector3.Lerp(Vector3.One, darkTint, dark);
        }

        /// <summary>
        /// Build a per-tile occluder mask for the visible area: a tile blocks light if
        /// the map's "Buildings" layer has a tile there (walls / built structures).
        /// Aligned to the viewport exactly like the water mask. Returns false (skipping
        /// shadows) when there are no occluders on screen.
        /// </summary>
        private bool BuildOccluderMask(int w, int h)
        {
            GameLocation? loc = Game1.currentLocation;
            var layer = loc?.map?.GetLayer("Buildings");
            if (loc == null || layer == null)
                return false;

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTileX = (int)Math.Floor(vx / 64f);
            int startTileY = (int)Math.Floor(vy / 64f);
            int tilesW = Math.Max(1, w / 64 + 2);
            int tilesH = Math.Max(1, h / 64 + 2);
            int count = tilesW * tilesH;
            int lw = layer.LayerWidth, lh = layer.LayerHeight;

            if (_occluderMaskBuf == null || _occluderMaskBuf.Length < count)
                _occluderMaskBuf = new Color[count];

            bool any = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool occ = tx >= 0 && ty >= 0 && tx < lw && ty < lh && layer.Tiles[tx, ty] != null;
                    if (occ) any = true;
                    _occluderMaskBuf[j * tilesW + i] = occ ? Color.White : Color.Transparent;
                }
            }

            if (!any)
                return false;

            if (_occluderMask == null || _occluderMask.Width != tilesW || _occluderMask.Height != tilesH)
            {
                _occluderMask?.Dispose();
                _occluderMask = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _occluderMask.SetData(_occluderMaskBuf, 0, count);

            _occTilesPerScreen = new Vector2(w / 64f, h / 64f);
            _occWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _occMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }

        /// <summary>
        /// Occluder mask for FLOOD lighting's per-light shadows — richer than the classic
        /// Buildings-layer mask: Height Framework walls/buildings (fallback: Buildings layer),
        /// tree trunks, resource clumps, bushes, and characters/animals, each with an occlusion
        /// WEIGHT in the red channel (entities are partial blockers → softer shadows).
        /// </summary>
        private bool BuildFloodOccluders(int w, int h)
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null)
                return false;
            var layer = loc.map?.GetLayer("Buildings");

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTileX = (int)Math.Floor(vx / 64f);
            int startTileY = (int)Math.Floor(vy / 64f);
            int tilesW = Math.Max(1, w / 64 + 2);
            int tilesH = Math.Max(1, h / 64 + 2);
            int count = tilesW * tilesH;

            if (_occluderMaskBuf == null || _occluderMaskBuf.Length < count)
                _occluderMaskBuf = new Color[count];

            var hf = ShadowRenderer.Height;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool solid;
                    if (hf != null)
                    {
                        // Walls/roofs block lamp light; decks (piers/bridges, height 1 but open)
                        // and water don't.
                        try { int cls = hf.GetSurfaceAt(loc, tx, ty); solid = cls == 2 || cls == 3; }
                        catch { hf = null; solid = false; }
                    }
                    else
                    {
                        solid = layer != null && tx >= 0 && ty >= 0 && tx < layer.LayerWidth && ty < layer.LayerHeight
                            && layer.Tiles[tx, ty] != null;
                    }
                    byte v = solid ? (byte)255 : (byte)0;
                    _occluderMaskBuf[j * tilesW + i] = new Color(v, v, v, (byte)255);
                }
            }

            void Stamp(int tx, int ty, byte strength)
            {
                int i = tx - startTileX, j = ty - startTileY;
                if (i < 0 || i >= tilesW || j < 0 || j >= tilesH)
                    return;
                int idx = j * tilesW + i;
                if (_occluderMaskBuf[idx].R < strength)
                    _occluderMaskBuf[idx] = new Color(strength, strength, strength, (byte)255);
            }

            foreach (var kv in loc.terrainFeatures.Pairs)
            {
                switch (kv.Value)
                {
                    case StardewValley.TerrainFeatures.Tree t when t.growthStage.Value >= 5:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, 215);
                        break;
                    case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, 215);
                        break;
                    case StardewValley.TerrainFeatures.Bush:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, 150);
                        break;
                }
            }
            foreach (var ltf in loc.largeTerrainFeatures)
            {
                if (ltf is StardewValley.TerrainFeatures.Bush b)
                    Stamp((int)b.Tile.X, (int)b.Tile.Y, 150);
            }
            foreach (var clump in loc.resourceClumps)
            {
                if (clump == null) continue;
                for (int cy = 0; cy < clump.height.Value; cy++)
                    for (int cx = 0; cx < clump.width.Value; cx++)
                        Stamp((int)clump.Tile.X + cx, (int)clump.Tile.Y + cy, 200);
            }
            // Characters/animals/the player are NOT stamped: their shadows are owned by the
            // sprite silhouette pass — stamping them here too gave everyone standing near a
            // lamp a second blurry dark blotch on top of their cast shadow.

            if (_occluderMask == null || _occluderMask.Width != tilesW || _occluderMask.Height != tilesH)
            {
                _occluderMask?.Dispose();
                _occluderMask = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _occluderMask.SetData(_occluderMaskBuf, 0, count);
            _occWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _occMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }

        /// <summary>Resolve the 16×16 source art of a map tile (first frame for animated tiles).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D tex, out Rectangle src)
        {
            tex = null!;
            src = default;
            if (layer == null || tx < 0 || ty < 0 || tx >= layer.LayerWidth || ty >= layer.LayerHeight)
                return false;
            var t = layer.Tiles[tx, ty];
            if (t is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                t = at.TileFrames[0];
            if (t?.TileSheet == null)
                return false;
            if (!_sheetTexCache.TryGetValue(t.TileSheet.ImageSource, out Texture2D? sheet))
            {
                try { sheet = Game1.content.Load<Texture2D>(t.TileSheet.ImageSource); }
                catch { sheet = null; }
                _sheetTexCache[t.TileSheet.ImageSource] = sheet;
            }
            if (sheet == null)
                return false;
            var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
            if (ib.Width != 16 || ib.Height != 16)
                return false;
            tex = sheet;
            src = new Rectangle(ib.X, ib.Y, 16, 16);
            return true;
        }

        /// <summary>Painted-water test for a single art pixel: blue-dominant or teal/foam.
        /// Matches the shader's colour gates, but runs on the STATIC source art (stable,
        /// classify once per tile art) instead of the composited frame.</summary>
        private static bool WaterColor(Color c)
        {
            if (c.A < 200)
                return false;
            if (c.B > c.R + 14 && c.B + 10 >= c.G) return true;   // blue water
            if (c.G > c.R + 10 && c.B > c.R + 6) return true;     // teal / foam / shallow edge
            return false;
        }

        /// <summary>16×16 painted-water classification of one tile art, cached per (texture, rect).</summary>
        private bool[] ClassifyBits(Texture2D tex, Rectangle src, bool water)
        {
            var key = (tex, src);
            if (_waterBitsCache.TryGetValue(key, out bool[]? bits))
                return bits;
            bits = new bool[256];
            _artBuf ??= new Color[256];
            try
            {
                tex.GetData(0, src, _artBuf, 0, 256);
                for (int p = 0; p < 256; p++)
                    bits[p] = WaterColor(_artBuf[p]);
            }
            catch { /* leave all-false */ }
            _waterBitsCache[key] = bits;
            return bits;
        }

        /// <summary>16×16 puddle classification of one tile art, cached: flat BLUE-GREY pixels
        /// (low saturation, blue at least a nudge over red, mid brightness) — the look of the
        /// walkable shallow pools that are plain ground in map data. Warm-grey stone, sand and
        /// grass all fail one of the gates.</summary>
        private (bool[] bits, int count) PuddleBits(Texture2D tex, Rectangle src)
        {
            var key = (tex, src);
            if (_puddleBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0;
            _artBuf ??= new Color[256];
            try
            {
                tex.GetData(0, src, _artBuf, 0, 256);
                for (int p = 0; p < 256; p++)
                {
                    Color c = _artBuf[p];
                    int maxc = Math.Max(c.R, Math.Max(c.G, c.B));
                    int minc = Math.Min(c.R, Math.Min(c.G, c.B));
                    // Measured from the island dig-site pool art (palette: (163,177,165),
                    // (144,157,158), (153,163,162), (112,134,141) — grey-GREEN, R always the
                    // lowest channel, B only +2..+29 over R). Guards against false positives:
                    // sand/warm stone are R-dominant, grass has B far below G, pure-neutral
                    // concrete/stone (B==R) fails the +2, dark cave floors fail brightness.
                    bool puddleish = c.A >= 200
                        && maxc - minc <= 34          // flat / unsaturated
                        && c.B >= c.R + 2             // cool tint (never true for warm ground)
                        && c.G >= c.R                 // R is the lowest channel
                        && maxc >= 55 && maxc <= 200; // mid brightness (not shadow, not foam)
                    if (bits[p] = puddleish)
                        n++;
                }
            }
            catch { /* leave all-false */ }
            entry = (bits, n);
            _puddleBitsCache[key] = entry;
            return entry;
        }

        /// <summary>16×16 opacity bits + opaque-pixel count of one tile art, cached — used to
        /// carve piers/bridges/pads out of the water mask (count decides march-blocking).</summary>
        private (bool[] bits, int count) SolidBits(Texture2D tex, Rectangle src)
        {
            var key = (tex, src);
            if (_solidBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0;
            _artBuf ??= new Color[256];
            try
            {
                tex.GetData(0, src, _artBuf, 0, 256);
                for (int p = 0; p < 256; p++)
                    if (bits[p] = _artBuf[p].A >= 128)
                        n++;
            }
            catch { /* leave all-false */ }
            entry = (bits, n);
            _solidBitsCache[key] = entry;
            return entry;
        }

        /// <summary>8-way one-tile dilation of a tile flag grid (src → dst).</summary>
        private static void Dilate8(bool[] src, bool[] dst, int tilesW, int tilesH)
        {
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool l = i > 0, r = i < tilesW - 1, u = j > 0, d = j < tilesH - 1;
                    dst[idx] = src[idx]
                        || (l && src[idx - 1]) || (r && src[idx + 1])
                        || (u && src[idx - tilesW]) || (d && src[idx + tilesW])
                        || (l && u && src[idx - tilesW - 1]) || (r && u && src[idx - tilesW + 1])
                        || (l && d && src[idx + tilesW - 1]) || (r && d && src[idx + tilesW + 1]);
                }
            }
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

            // The mask content is TILE-ANCHORED (sub-tile camera scroll is handled by the
            // WorldTileOffset shader param), so it only changes when the view crosses a tile
            // boundary — rebuilding the pixel grid every frame was a walking-stutter tax.
            if (_waterMask != null && loc == _lastWaterLoc && startTileX == _lastWaterTx && startTileY == _lastWaterTy
                && _waterMask.Width == tilesW * 16 && Game1.ticks - _lastWaterTick < 20)
            {
                _waterTilesPerScreen = new Vector2(w / 64f, h / 64f);
                _waterWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
                _waterMaskSize = new Vector2(tilesW, tilesH);
                return _waterAny;
            }
            _lastWaterLoc = loc;
            _lastWaterTx = startTileX;
            _lastWaterTy = startTileY;
            _lastWaterTick = Game1.ticks;

            // Height Framework (when present) classifies the actual water SURFACE: ponds and
            // beach tide pools count as water (they reflect too), while pier/bridge DECKS over
            // water do not (no reflection painted onto planks). Fall back to isWaterTile.
            var hf = ShadowRenderer.Height;
            if (_waterBoolBuf == null || _waterBoolBuf.Length < count)
                _waterBoolBuf = new bool[count];
            bool any = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool water;
                    try { water = hf != null ? hf.IsWaterSurface(loc, tx, ty) : loc.isWaterTile(tx, ty); }
                    catch { hf = null; water = loc.isWaterTile(tx, ty); }
                    // Walkable shallow pools (island dig site tide pools) aren't Water tiles,
                    // but they refill the watering can → "WaterSource" marks them as real water.
                    if (!water && loc.doesTileHaveProperty(tx, ty, "WaterSource", "Back") != null)
                        water = true;
                    if (water) any = true;
                    _waterBoolBuf[j * tilesW + i] = water;
                }
            }

            // CORE mask first (undilated): the reflection's shoreline search must see bridges,
            // piers and banks as land — the dilated mask swallowed any land strip ≤4 tiles
            // wide (a bridge between two water bodies), which killed their reflections.
            if (_waterMaskCoreBuf == null || _waterMaskCoreBuf.Length < count)
                _waterMaskCoreBuf = new Color[count];
            for (int idx = 0; idx < count; idx++)
                _waterMaskCoreBuf[idx] = _waterBoolBuf[idx] ? Color.White : Color.Transparent;

            // NOTE: no early-out on "no real water" here — walk-through puddles (art-classified
            // below) count as water too; a dig site with the ocean scrolled off-screen used to
            // shut the whole stage off and every pool went dead at once.

            // CANDIDATE ring: dilate three tiles (shore art + beach surf zone). These tiles are
            // NOT marked water — they only nominate their ART for per-pixel classification below,
            // so the final mask never spills a box past the painted waterline.
            if (_waterBool2Buf == null || _waterBool2Buf.Length < count)
                _waterBool2Buf = new bool[count];
            Dilate8(_waterBoolBuf, _waterBool2Buf, tilesW, tilesH);
            Dilate8(_waterBool2Buf, _waterBoolBuf, tilesW, tilesH);
            Dilate8(_waterBoolBuf, _waterBool2Buf, tilesW, tilesH);

            // ---- PIXEL-accurate mask (16 texels per tile = the art's own resolution) ----
            // True water tiles fill solid; candidate shore tiles contribute only the pixels of
            // their Back-layer art that are painted as water (classified ONCE per tile art and
            // cached); opaque Buildings/Front art (pier posts, bridges, lily pads, canopies)
            // carves holes so things standing in the water block the effect.
            const int Sub = 16;
            int pw = tilesW * Sub, ph = tilesH * Sub;
            int pcount = pw * ph;
            if (_waterPixBuf == null || _waterPixBuf.Length < pcount)
                _waterPixBuf = new Color[pcount];
            var back = loc.map?.GetLayer("Back");
            var bld = loc.map?.GetLayer("Buildings");
            var front = loc.map?.GetLayer("Front");
            if (_waterPixBits == null || _waterPixBits.Length < pcount)
                _waterPixBits = new bool[pcount];
            // Pass A — raw water pixels (true tiles solid, shore tiles by art classification).
            if (_puddleTileBuf == null || _puddleTileBuf.Length < count)
                _puddleTileBuf = new byte[count];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    bool isWater = _waterMaskCoreBuf![idx].R > 0;
                    int tx = startTileX + i, ty = startTileY + j;
                    bool[]? bits = null;
                    byte puddle = 0;
                    if (!isWater && TryTileArt(back, tx, ty, out var btex, out var bsrc))
                    {
                        if (_waterBool2Buf[idx])
                            bits = ClassifyBits(btex, bsrc, water: true);
                        // Walkable shallow pools (island dig site) are plain GROUND in map data —
                        // recognise them by their ART: mostly flat blue-grey pixels. Rocky/pebbled
                        // pool variants only reach ~30-55% coverage → "weak" tier, accepted when
                        // surrounded by enough other pool tiles. OUTDOORS only: grey-blue interior
                        // floors (mines) must never classify as water.
                        if (loc.IsOutdoors)
                        {
                            int pc = PuddleBits(btex, bsrc).count;
                            puddle = pc >= 140 ? (byte)2 : pc >= 80 ? (byte)1 : (byte)0;
                        }
                    }
                    _puddleTileBuf[idx] = puddle;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            _waterPixBits[row + px] = isWater || (bits != null && bits[arow + px]);
                    }
                }
            }
            // Puddle merge — strong tiles need ≥1 puddle neighbour, weak (rocky-variant) tiles
            // need ≥2 (pools span multiple tiles; a lone grey-blue tile must not turn to water).
            if (_puddlePixBits == null || _puddlePixBits.Length < pcount)
                _puddlePixBits = new bool[pcount];
            Array.Clear(_puddlePixBits, 0, pcount);
            bool anyPuddle = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    if (_puddleTileBuf[idx] == 0)
                        continue;
                    int buddies = ((i > 0 && _puddleTileBuf[idx - 1] > 0) ? 1 : 0)
                                + ((i < tilesW - 1 && _puddleTileBuf[idx + 1] > 0) ? 1 : 0)
                                + ((j > 0 && _puddleTileBuf[idx - tilesW] > 0) ? 1 : 0)
                                + ((j < tilesH - 1 && _puddleTileBuf[idx + tilesW] > 0) ? 1 : 0);
                    if (buddies < (_puddleTileBuf[idx] == 2 ? 1 : 2))
                        continue;
                    int tx = startTileX + i, ty = startTileY + j;
                    if (!TryTileArt(back, tx, ty, out var ptex, out var psrc))
                        continue;
                    anyPuddle = true;
                    bool[] pbits = PuddleBits(ptex, psrc).bits;
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                            if (pbits[arow + px])
                            {
                                _waterPixBits[row + px] = true;
                                _puddlePixBits[row + px] = true;
                            }
                    }
                }
            }
            _waterAny = any || anyPuddle;
            if (!_waterAny)
                return false;
            // Pass B — vertical CLOSE (fill gaps that have water above AND below), two widths:
            //   effect bits: ≤4 texels — heals the dark shading slit the shore art paints
            //                along the waterline without swallowing real land.
            //   march bits:  ≤12 texels (~0.75 tile) — anything painted INSIDE a water body
            //                (surf foam bands, starfish, sand flecks) must not read as a
            //                shoreline, or reflections re-anchor below it and shift down.
            //                Bridges/decks are ≥1 tile thick, so they still block.
            void CloseVertical(bool[] bits, int maxGap)
            {
                for (int x = 0; x < pw; x++)
                {
                    int last = -99;
                    for (int y = 0; y < ph; y++)
                    {
                        if (!bits[y * pw + x])
                            continue;
                        if (y - last > 1 && y - last <= maxGap + 1)
                            for (int k = last + 1; k < y; k++)
                                bits[k * pw + x] = true;
                        last = y;
                    }
                }
            }
            if (_waterPixBits2 == null || _waterPixBits2.Length < pcount)
                _waterPixBits2 = new bool[pcount];
            Array.Copy(_waterPixBits, _waterPixBits2, pcount);
            CloseVertical(_waterPixBits, 4);
            CloseVertical(_waterPixBits2, 12);
            // Structure test for the MARCH channel: near-solid art (≥90% opaque) that is
            // CONNECTED TO LAND. A bridge or pier always touches a bank; a clump of lily pads
            // dense enough to fill its tile still floats in open water — opacity alone let pad
            // clusters re-anchor reflections below them. Connectivity: seed near-solid tiles
            // that touch a non-water tile (or the screen edge — the structure may continue
            // off-screen), then grow the seed through adjacent near-solid tiles.
            if (_bigCarveBuf == null || _bigCarveBuf.Length < count) _bigCarveBuf = new bool[count];
            if (_bigSeedBuf == null || _bigSeedBuf.Length < count) _bigSeedBuf = new bool[count];
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    int tx = startTileX + i, ty = startTileY + j;
                    bool big = (TryTileArt(bld, tx, ty, out var t1, out var s1) && SolidBits(t1, s1).count >= 230)
                            || (TryTileArt(front, tx, ty, out var t2, out var s2) && SolidBits(t2, s2).count >= 230);
                    _bigCarveBuf[idx] = big;
                    bool landNear = i == 0 || i == tilesW - 1 || j == 0 || j == tilesH - 1
                        || !(_waterMaskCoreBuf![idx - 1].R > 0) || !(_waterMaskCoreBuf[idx + 1].R > 0)
                        || !(_waterMaskCoreBuf[idx - tilesW].R > 0) || !(_waterMaskCoreBuf[idx + tilesW].R > 0);
                    _bigSeedBuf[idx] = big && landNear;
                }
            }
            for (int sweep = 0; sweep < 2; sweep++)
            {
                for (int idx = 0; idx < count; idx++)                       // forward
                    if (_bigCarveBuf[idx] && !_bigSeedBuf[idx] &&
                        ((idx % tilesW > 0 && _bigSeedBuf[idx - 1]) || (idx >= tilesW && _bigSeedBuf[idx - tilesW])))
                        _bigSeedBuf[idx] = true;
                for (int idx = count - 1; idx >= 0; idx--)                  // backward
                    if (_bigCarveBuf[idx] && !_bigSeedBuf[idx] &&
                        ((idx % tilesW < tilesW - 1 && _bigSeedBuf[idx + 1]) || (idx + tilesW < count && _bigSeedBuf[idx + tilesW])))
                        _bigSeedBuf[idx] = true;
            }

            // ARCH FILL: a bridge's arch openings sit BETWEEN structure tiles in the same row.
            // Fill gaps ≤3 tiles between two structure tiles when the gap tile itself carries
            // Buildings/Front art (arch rims do; open water between two separate piers doesn't)
            // — the structure becomes ONE solid block with a level base, so every column's
            // reflection anchors on the same row, like a real bridge mirrored in water.
            for (int j = 0; j < tilesH; j++)
            {
                int lastStruct = -99;
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    if (!_bigSeedBuf[idx])
                        continue;
                    if (i - lastStruct > 1 && i - lastStruct <= 4)
                    {
                        for (int k = lastStruct + 1; k < i; k++)
                        {
                            int kx = startTileX + k, ky = startTileY + j;
                            if (TryTileArt(bld, kx, ky, out _, out _) || TryTileArt(front, kx, ky, out _, out _))
                                _bigSeedBuf[j * tilesW + k] = true;
                        }
                    }
                    lastStruct = i;
                }
            }

            // Pass C — carve opaque Buildings/Front art and emit two channels:
            //   R = EFFECT mask: carve everything opaque (no ripple/mirror ON posts, pads, bridges).
            //   G = MARCH mask: carve only land-connected structures (see above).
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    int tx = startTileX + i, ty = startTileY + j;
                    (bool[] bits, int count)? carveB = TryTileArt(bld, tx, ty, out var t1, out var s1) ? SolidBits(t1, s1) : null;
                    (bool[] bits, int count)? carveF = TryTileArt(front, tx, ty, out var t2, out var s2) ? SolidBits(t2, s2) : null;
                    // A structure tile blocks the march as a WHOLE tile (arch openings included):
                    // per-pixel carving gave each column its own edge and the mirror stepped.
                    bool structTile = _bigSeedBuf[idx];
                    for (int py = 0; py < Sub; py++)
                    {
                        int row = (j * Sub + py) * pw + i * Sub;
                        int arow = py * Sub;
                        for (int px = 0; px < Sub; px++)
                        {
                            if (structTile)
                                _waterPixBits2![row + px] = false;
                            if (carveB is { } cb && cb.bits[arow + px]) _waterPixBits[row + px] = false;
                            if (carveF is { } cf && cf.bits[arow + px]) _waterPixBits[row + px] = false;
                        }
                    }
                }
            }

            // Pass D — WATERLINE HEIGHT-MAP: per column, remember the top row of each
            // contiguous march-water run (= that pixel's shoreline).
            if (_edgeBuf == null || _edgeBuf.Length < pcount)
                _edgeBuf = new short[pcount];
            for (int x = 0; x < pw; x++)
            {
                int top = -1;
                for (int y = 0; y < ph; y++)
                {
                    int p = y * pw + x;
                    if (_waterPixBits2![p]) { if (top < 0) top = y; _edgeBuf[p] = (short)top; }
                    else top = -1;
                }
            }

            // Pass E — smooth the shoreline HORIZONTALLY (±10 texels, ignoring neighbours whose
            // edge differs >1.5 tiles = another body/structure) and emit. Stepped diagonal banks
            // become a continuous slope, so a reflection is no longer sliced into offset blocks —
            // the shader reads this distance (B, half-texel units) instead of marching.
            for (int y = 0; y < ph; y++)
            {
                int rowBase = y * pw;
                for (int x = 0; x < pw; x++)
                {
                    int p = rowBase + x;
                    bool eff = _waterPixBits[p];
                    bool march = _waterPixBits2![p];
                    byte bch = 255;
                    if (march)
                    {
                        int t0 = _edgeBuf[p];
                        int acc = 0, n = 0;
                        int x0 = Math.Max(0, x - 10), x1 = Math.Min(pw - 1, x + 10);
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            int q = rowBase + xx;
                            if (_waterPixBits2[q] && Math.Abs(_edgeBuf[q] - t0) <= 24) { acc += _edgeBuf[q]; n++; }
                        }
                        float ts = n > 0 ? (float)acc / n : t0;
                        bch = (byte)MathHelper.Clamp((float)Math.Round((y - ts) * 2f), 0f, 252f);
                    }
                    // Shallow puddles get a SOFTER mask value: every effect (ripple, sparkle,
                    // mirror) scales with it, so a walk-through pool shimmers gently instead of
                    // sparkling like open water.
                    byte effV = !eff ? (byte)0 : _puddlePixBits![p] ? (byte)205 : (byte)255;
                    _waterPixBuf[p] = new Color(effV, march ? 255 : 0, bch, 255);
                }
            }
            if (_waterMask == null || _waterMask.Width != pw || _waterMask.Height != ph)
            {
                _waterMask?.Dispose();
                _waterMask = new Texture2D(_device, pw, ph, false, SurfaceFormat.Color);
            }
            _waterMask.SetData(_waterPixBuf, 0, pcount);
            if (_waterMaskCore == null || _waterMaskCore.Width != tilesW || _waterMaskCore.Height != tilesH)
            {
                _waterMaskCore?.Dispose();
                _waterMaskCore = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _waterMaskCore.SetData(_waterMaskCoreBuf, 0, count);

            _waterTilesPerScreen = new Vector2(w / 64f, h / 64f);
            _waterWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _waterMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }

        // ---- helpers -------------------------------------------------------

        private static float Time() => Game1.ticks / 60f;

        /// <summary>Screen-UV + UV-radius of the largest-radius real light source currently on screen, if any.</summary>
        private static bool TryGetLightUV(out Vector2 uv, out float radiusUV)
        {
            uv = Vector2.Zero;
            radiusUV = 0.25f;
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
                if (r > best)
                {
                    best = r;
                    uv = new Vector2(u, v);
                    // radius.Value is ~tiles; on-screen glow ≈ radius*64px. Give the rays a little
                    // more reach than the glow, so only pixels near THIS light streak (not distant
                    // bright scenery like flowers/white hair).
                    radiusUV = MathHelper.Clamp(r * 64f * 2.2f / vh, 0.12f, 0.6f);
                }
            }
            return best > 0f;
        }

        private static Vector2 WorldOffset(int w, int h) =>
            new(Game1.viewport.X / (float)Math.Max(1, w), Game1.viewport.Y / (float)Math.Max(1, h));

        /// <summary>The player's position in screen UV (0..1), for the radial tilt-shift focus.</summary>
        private static Vector2 PlayerScreenUV()
        {
            if (Game1.player == null)
                return new Vector2(0.5f, 0.5f);
            Vector2 world = Game1.player.Position + new Vector2(32f, 32f); // sprite centre-ish
            Vector2 local = Game1.GlobalToLocal(Game1.viewport, world);
            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);
            return new Vector2(local.X / vw, local.Y / vh);
        }

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

        /// <summary>Debug: save the water masks to PNG (R=effect, G=march, B=edge distance).</summary>
        public string DumpMasks(string dir)
        {
            if (_waterMask == null)
                return "no water mask built (stand near water first)";
            string p1 = System.IO.Path.Combine(dir, "radiance-watermask.png");
            using (var fs = System.IO.File.Create(p1))
                _waterMask.SaveAsPng(fs, _waterMask.Width, _waterMask.Height);
            if (_waterMaskCore != null)
            {
                string p2 = System.IO.Path.Combine(dir, "radiance-watercore.png");
                using (var fs = System.IO.File.Create(p2))
                    _waterMaskCore.SaveAsPng(fs, _waterMaskCore.Width, _waterMaskCore.Height);
            }
            return $"saved {p1} (origin tile {_lastWaterTx},{_lastWaterTy}, player tile {Game1.player?.TilePoint})";
        }

        public void Dispose()
        {
            _sceneRT?.Dispose(); _fullA?.Dispose(); _fullB?.Dispose(); _rtA?.Dispose(); _rtB?.Dispose(); _waterMask?.Dispose(); _waterMaskCore?.Dispose(); _occluderMask?.Dispose(); _lumRT?.Dispose();
            _bloom?.Dispose(); _colorGrade?.Dispose(); _godRays?.Dispose(); _fog?.Dispose(); _cloudShadow?.Dispose(); _tiltShift?.Dispose();
            _water?.Dispose(); _finishing?.Dispose(); _lighting?.Dispose(); _floodFx?.Dispose(); _flood.Dispose();
            _sceneRT = _fullA = _fullB = _rtA = _rtB = null;
            _waterMask = null; _waterMaskCore = null; _occluderMask = null; _lumRT = null;
            _bloom = _colorGrade = _godRays = _fog = _cloudShadow = _tiltShift = _water = _finishing = _lighting = _floodFx = null;
        }
    }
}
