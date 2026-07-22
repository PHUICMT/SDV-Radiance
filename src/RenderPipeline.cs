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
    internal sealed partial class RenderPipeline : IDisposable
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

        // Dynamic lighting: per-frame light list read from Game1.currentLightSources.
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
        private int[]? _edgeSum;               // per-row prefix sums for the shoreline smoothing window
        private int[]? _edgeCnt;
        private Color[]? _artBuf;              // 16×16 scratch for tile-art reads
        private readonly System.Collections.Generic.Dictionary<string, Texture2D?> _sheetTexCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), bool[]> _waterBitsCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), (bool[] bits, int count)> _solidBitsCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), (bool[] bits, int count)> _puddleBitsCache = new();
        private byte[]? _puddleTileBuf;        // per-tile puddle level: 0 no, 1 weak (rocky variant), 2 strong
        private bool[]? _puddlePixBits;        // per-pixel: came from the puddle classifier (softer effects)
        private int _occTx = int.MinValue, _occTy = int.MinValue, _occTick = int.MinValue;
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
        private readonly System.Diagnostics.Stopwatch _perfSw = new();
        private double _perfTotalMs, _perfMaxMs;
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

        // Flood-propagation GI lightmap (see FloodLightmap.cs).
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
            || ((c.FogEnabled || c.FogNightMist) && _fog != null)
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

            if (config.DebugLogging) { _frames++; _perfSw.Restart(); }

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
                // DAY fog and NIGHT mist are separate effects with separate toggles: day fog
                // fades out over dusk exactly as the night mist (sparse blue wisps, clear
                // weather only) fades in. Both amounts are EASED so toggling never pops.
                float night = NightFactorNow();
                float dayTarget = (config.FogEnabled && outdoors) ? config.FogDensity * (1f - night) : 0f;
                float mistTarget = (config.FogNightMist && outdoors && !Game1.isRaining && !Game1.isSnowing)
                    ? config.FogNightMistDensity * night : 0f;
                _fogDayAmt += (dayTarget - _fogDayAmt) * 0.035f;    // ~0.5–1s ease
                _fogMistAmt += (mistTarget - _fogMistAmt) * 0.035f;
                if (Math.Abs(dayTarget - _fogDayAmt) < 0.003f) _fogDayAmt = dayTarget;
                if (Math.Abs(mistTarget - _fogMistAmt) < 0.003f) _fogMistAmt = mistTarget;
                if ((_fogDayAmt > 0.004f || _fogMistAmt > 0.004f) && _fog != null && outdoors) stages.Add(_dFog);
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

            if (config.DebugLogging)
            {
                _perfSw.Stop();
                double ms = _perfSw.Elapsed.TotalMilliseconds;
                _perfTotalMs += ms;
                if (ms > _perfMaxMs) _perfMaxMs = ms;
            }
        }

        private void MaybeLogDiag(ModConfig config)
        {
            if (_frames < 120) return;
            _monitor.Log($"[diag] over {_frames} frames: applied={_applied}, skipped={_skipNoTarget}, sizeChanges={_sizeChanges}, size={_lastW}x{_lastH}, "
                + $"apply avg={(_applied > 0 ? _perfTotalMs / _applied : 0):0.00}ms max={_perfMaxMs:0.00}ms.", LogLevel.Debug);
            _frames = _applied = _skipNoTarget = _sizeChanges = 0;
            _perfTotalMs = _perfMaxMs = 0;
        }

        // ---- stages --------------------------------------------------------

        private float _cloudDayFactor = 1f;
        // Eased effect amounts so nothing pops: day fog / night mist crossfade over time
        // of day AND ease when toggled; wading self-reflection fades at the water edge.
        private float _fogDayAmt, _fogMistAmt, _pinFade;

        // MonoGame's EffectParameterCollection indexer is a LINEAR scan with string compares,
        // and the stages look parameters up ~100 times per frame — cache the references once
        // per (effect, name) so a warm frame pays a dictionary hash instead.
        private readonly System.Collections.Generic.Dictionary<(Effect fx, string name), EffectParameter?> _fxParamCache = new();

        private EffectParameter? P(Effect fx, string name)
        {
            var key = (fx, name);
            if (!_fxParamCache.TryGetValue(key, out EffectParameter? p))
                _fxParamCache[key] = p = fx.Parameters[name];
            return p;
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
            _sceneRT?.Dispose(); _fullA?.Dispose(); _fullB?.Dispose(); _rtA?.Dispose(); _rtB?.Dispose(); _waterMask?.Dispose(); _waterMaskCore?.Dispose(); _occluderMask?.Dispose(); _lumRT?.Dispose();
            _bloom?.Dispose(); _colorGrade?.Dispose(); _godRays?.Dispose(); _fog?.Dispose(); _cloudShadow?.Dispose(); _tiltShift?.Dispose();
            _water?.Dispose(); _finishing?.Dispose(); _lighting?.Dispose(); _floodFx?.Dispose(); _flood.Dispose();
            _sceneRT = _fullA = _fullB = _rtA = _rtB = null;
            _waterMask = null; _waterMaskCore = null; _occluderMask = null; _lumRT = null;
            _bloom = _colorGrade = _godRays = _fog = _cloudShadow = _tiltShift = _water = _finishing = _lighting = _floodFx = null;
        }
    }
}
