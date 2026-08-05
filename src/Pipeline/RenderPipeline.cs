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

        private RenderTarget2D? _sceneRenderTarget;   // full-res capture
        private RenderTarget2D? _fullResolutionPingA;     // full-res ping-pong
        private RenderTarget2D? _fullResolutionPingB;     // full-res ping-pong
        private RenderTarget2D? _halfResolutionScratchA;       // half-res scratch
        private RenderTarget2D? _halfResolutionScratchB;       // half-res scratch
        private Effect? _bloom;
        private Effect? _colorGrade;
        private Effect? _godRays;
        private Effect? _fogEffect;
        private Effect? _cloudShadow;
        private Effect? _tiltShift;
        private Effect? _water;
        private Effect? _finishing;
        private Effect? _lighting;

        // Dynamic lighting: per-frame light list read from Game1.currentLightSources.
        private const int MaxLights = 16;
        private readonly Vector2[] _lightPositions = new Vector2[MaxLights];
        private readonly Vector4[] _lightShaderData = new Vector4[MaxLights]; // xyz = colour*boost, w = radiusUV
        private int _lightCount;

        private Texture2D? _waterMask;         // PIXEL-accurate water mask (16 texels/tile): true water tiles + the painted
                                               // water inside shore-tile art, minus opaque Buildings/Front art (pier posts,
                                               // bridges, lily pads). Effects end exactly at the real waterline.
        private Texture2D? _waterMaskCore;     // undilated per-TILE mask — true water bodies, used for the reflection's shoreline search
        private Texture2D? _waterSignedDistanceTexture;          // Alpha8 signed shore distance (Pass F): 128 = waterline, ±4/texel
        private Color[]? _waterMaskCorePixels;
        private bool[]? _waterTileFlags;         // pre-dilation water flags (see BuildWaterMask)
        private Color[]? _waterMaskPixels;         // pixel-mask upload buffer (tilesW*16 × tilesH*16)
        private bool[]? _waterEffectBits;         // scratch bits for the close/carve passes (effect channel)
        private bool[]? _waterMarchBits;        // march-channel bits (wider close: floats never block)
        private bool[]? _tileNearSolidFlags;          // per-tile: near-solid Buildings/Front art
        private bool[]? _tileLandConnectedFlags;           // per-tile: near-solid AND connected to land (true structures)
        private int[]? _structScrubTopScratch;        // per-column art-top scratch for the struct march scrub (16 entries)
        private int[]? _structScrubBottomScratch;     // per-column art-bottom scratch for the struct march scrub (16 entries)
        private short[]? _waterlineTopRowByPixel;             // per-pixel: top row of this column's water run (waterline map)
        private int[]? _waterlineRowPrefixSums;               // per-row prefix sums for the shoreline smoothing window
        private int[]? _waterlineRowSampleCounts;
        private Color[]? _tileArtPixels;              // 16×16 scratch for tile-art reads
        // Whole-tilesheet pixel cache. Reading each 16×16 tile with its own texture.GetData is a
        // separate GPU→CPU readback (a pipeline flush ~1-3 ms EACH, regardless of the tiny size);
        // walking into a fresh forest/town screen touched 100+ new tiles in one gather → a ~300 ms
        // main-thread stall. Reading a sheet ONCE into a CPU array turns that into a single readback
        // per sheet (a forest uses a handful), then every tile samples the array with zero GPU work.
        // Huge sheets (> cap) fall back to per-region GetData so we never allocate a giant array.
        private readonly System.Collections.Generic.Dictionary<Texture2D, Color[]?> _tilesheetPixelCache = new();
        // Refusal bound only for absurd sheets — NOT a performance knob. The old 8 Mpx ceiling sat
        // just under a real SVE tilesheet (2400x3600 = 8.64 Mpx), and being 8% over it swapped one
        // readback per sheet for one per TILE: 43 s inside a single gather on a 240x156 map.
        private const int SheetPixCap = 64_000_000;
        private const int SheetStripRows = 512;    // rows per readback, so staging stays bounded
        /// <summary>Per-tile art for sheets too big to cache whole — so even the refused path costs
        /// one readback per DISTINCT tile instead of one per tile the map happens to paint.</summary>
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), Color[]> _tileArtCache = new();
        private GameLocation? _prewarmedLocation;   // last location whose tilesheets we bulk-read back
        private readonly System.Collections.Generic.Dictionary<string, Texture2D?> _tilesheetTextureCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), (bool[] bits, int count, int water)> _tileSolidBitsCache = new();
        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), bool[]> _tileAnyAlphaBitsCache = new();
        private int _occluderTileX = int.MinValue, _occluderTileY = int.MinValue, _occluderCacheTick = int.MinValue;
        private int _occluderMaskBuildMode;   // which builder last filled _occluderMask: 1 = classic, 2 = flood (they share it + the throttle, and are mutually exclusive per frame)
        private int _lastWaterTileX = int.MinValue, _lastWaterTileY = int.MinValue, _lastWaterBuildTick = int.MinValue;
        private int _lastWaterHookVersion = -1;
        private int _lastWaterLabelVersion = -1;
        private int _lastWaterEpoch = -1;
        /// <summary>Bumped by world events that change where water is without touching any other
        /// cache key: a fish pond placed/moved/removed (its water is in the mask now) or a map
        /// asset re-patched in place (Content Patcher seasonal/conditional edits). The next
        /// BuildWaterMask sees the mismatch and rebuilds immediately instead of within 10 s.</summary>
        internal static int MaskEpoch;

        // Presence fades (0..1): stages ease IN when they (re)appear instead of popping.
        private GameLocation? _fadeLocation;
        private float _fadeWater, _fadeCloud, _fadeLighting, _fadeFlood, _fadeTilt;

        /// <summary>One ease-in step (~0.5 s to full at 60 fps).</summary>
        private static float Ease01(float v) => Determinism.Frozen ? 1f
            : v >= 0.999f ? 1f : Math.Min(1f, v + (1f - v) * 0.10f);


        /// <summary>One ease-OUT step, the mirror of <see cref="Ease01"/> (~0.5 s to gone).
        /// House rule: no effect may ever pop - if something changes, it fades. Every presence
        /// used to ease IN and then get slammed to 0 the frame its stage stopped qualifying,
        /// so switching an effect off, stepping indoors, or walking away from water cut
        /// instantly. A stage keeps rendering while its presence decays, and only leaves the
        /// list once it is actually invisible.</summary>
        private static float Ease0(float v) => Determinism.Frozen ? 0f
            : v <= 0.004f ? 0f : v - v * 0.10f;

        /// <summary>One eased step of <paramref name="v"/> toward <paramref name="target"/>. The
        /// pattern was written out by hand at seven call sites; freeze mode has to land on the
        /// target at every one of them, so they share a step now.</summary>
        private static void Approach(ref float v, float target, float rate) =>
            v = Determinism.Settle(v + (target - v) * rate, target);

        /// <summary>Presence threshold below which a stage is genuinely invisible and may be
        /// dropped from the frame's stage list.</summary>
        private const float FadeGone = 0.004f;
        private GameLocation? _lastWaterLocation;
        private bool _hasWaterInMask;
        private readonly Vector4[] _waterGlimmerLights = new Vector4[8];   // on-screen lights → water glimmer
        private Vector2 _waterMaskTilesPerScreen, _waterMaskWorldTileOffset, _waterMaskPixelSize;

        private Texture2D? _occluderMask;      // per-tile occluder mask (walls/structures) for shadows
        private Color[]? _occluderMaskPixels;
        private Vector2 _occluderTilesPerScreen, _occluderWorldTileOffset, _occluderMaskSize;
        private bool _shadowsReady;            // true when an occluder mask was built this frame

        private bool _loggedOnce;
        private int _frameCount, _appliedFrameCount, _skippedNoTargetCount, _renderTargetResizeCount;
        private readonly System.Diagnostics.Stopwatch _performanceStopwatch = new();
        private double _performanceTotalMilliseconds, _performanceMaxMilliseconds;
        // Per-builder timings (DebugLogging only): the tile-crossing grid rebuilds are the
        // prime stutter suspects, and their cost scales with the zoomed-out viewport.
        private static readonly string[] _buildNames = { "flood", "floodOcc", "occ", "water" };
        private readonly double[] _buildMilliseconds = new double[4];
        private readonly double[] _buildMaxMilliseconds = new double[4];

        private bool TimedBuild(ModConfig config, int idx, Func<bool> fn)
        {
            if (!config.DebugLogging)
                return fn();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool r = fn();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _buildMilliseconds[idx] += ms;
            if (ms > _buildMaxMilliseconds[idx]) _buildMaxMilliseconds[idx] = ms;
            return r;
        }
        /// <summary>True for interiors whose water is part of the level itself, not decoration:
        /// caves/mines/sewer/dungeons AND the bathhouse hot spring. These keep their water even
        /// when WaterEffectIndoors is off — that toggle is meant for house/building interiors with
        /// decorative ponds (custom home mods), not real level water like the mines or the spa.</summary>
        internal static bool HasLevelWater(GameLocation? location)
        {
            if (location == null)
                return false;
            // Ground truth first (decompiled 1.6): every vanilla interior whose water is part
            // of the level is either a known class, has the game's own waterTiles grid
            // (built only for outdoors / `indoorWater` map property / Sewer / Submarine),
            // or declares `indoorWater` itself. Class and data checks can't rot the way
            // name substrings do.
            if (location is StardewValley.Locations.MineShaft or StardewValley.Locations.VolcanoDungeon
                or StardewValley.Locations.Sewer or StardewValley.Locations.Caldera
                or StardewValley.Locations.BathHousePool or StardewValley.Locations.BoatTunnel
                or StardewValley.Locations.Submarine)
                return true;
            if (location.waterTiles != null && !location.IsOutdoors)
                return true;
            try { if (location.HasMapPropertyWithValue("indoorWater")) return true; } catch { }
            // Name fallback for MODDED caves/spas the data can't identify (their water is
            // often bare art, exactly like the vanilla bathhouse pool).
            string n = location.Name ?? "";
            string t = location.GetType().Name;
            static bool NameContains(string s, string k) => s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            return NameContains(n, "Mine") || NameContains(n, "Cave") || NameContains(n, "Volcano") || NameContains(n, "Dungeon")
                || NameContains(n, "Sewer") || NameContains(n, "Caldera") || NameContains(n, "Swamp") || NameContains(n, "BugLand")
                || NameContains(n, "Submarine") || NameContains(n, "Tunnel") || NameContains(n, "Grotto")
                || NameContains(n, "BathHouse") || NameContains(n, "Spa") || NameContains(n, "HotSpring") || NameContains(n, "Bath")
                || NameContains(t, "Mine") || NameContains(t, "Volcano") || NameContains(t, "Dungeon");
        }

        /// <summary>Whether the water effect should run in this location. Outdoors and real level
        /// water (see <see cref="HasLevelWater"/>) always qualify; building interiors qualify only
        /// when the global indoor toggle is on AND the player hasn't opted this room out from the
        /// tuner. Single source of truth shared by the render gate and the FreezeGameWater gate.</summary>
        internal static bool WaterAllowedIn(GameLocation? location, ModConfig config)
        {
            if (location == null || location.IsOutdoors || HasLevelWater(location))
                return true;
            return config.WaterEffectIndoors
                && !config.WaterDisabledLocations.Contains(location.NameOrUniqueName);
        }

        private int _lastViewportWidth = -1, _lastViewportHeight = -1;
        private float _godRayAmount; // 0..1 eased presence so rays fade in/out instead of popping
        private float _masterFade;              // 0..1 ease-in of the whole stack when it turns on

        // Reused per-frame stage list + cached stage delegates (see Apply).
        private readonly List<Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>> _stages = new();
        private Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>?
            _lightingStageDelegate, _waterStageDelegate, _cloudShadowStageDelegate, _godRaysStageDelegate, _bloomStageDelegate, _fogStageDelegate, _colorGradeStageDelegate, _tiltShiftStageDelegate, _finishingStageDelegate, _floodStageDelegate;

        // Flood-propagation GI lightmap (see FloodLightmap.cs).
        private Effect? _floodEffect;
        private readonly FloodLightmap _flood = new();

        // Metered auto-exposure: average the scene each frame (downsampled to a
        // tiny RT, read back a frame late so there's no GPU stall) and ease the
        // exposure toward a target so bright scenes (sand/snow/rooms) dim smoothly.
        private RenderTarget2D? _luminanceRenderTarget;
        private Color[]? _luminancePixels;
        private bool _isLuminancePrimed;

        /// <summary>Eased exposure multiplier from the metering above. It scales the WHOLE frame,
        /// which makes it the first suspect whenever the picture brightens or dims on its own —
        /// captures record it (see RenderPipeline.Dump.cs) and freeze mode pins it at neutral.</summary>
        private float _meteredExposure = 1f;

        public RenderPipeline(GraphicsDevice device, IMonitor monitor, string modDir)
        {
            Current = this;
            _device = device;
            _monitor = monitor;
            _modDir = modDir;
            _bloom = LoadEffect("bloom.mgfxo");
            _colorGrade = LoadEffect("colorgrade.mgfxo");
            _godRays = LoadEffect("godrays.mgfxo");
            _fogEffect = LoadEffect("fog.mgfxo");
            _cloudShadow = LoadEffect("cloudshadow.mgfxo");
            _tiltShift = LoadEffect("tiltshift.mgfxo");
            _water = LoadEffect("water.mgfxo");
            _floodEffect = LoadEffect("floodlight.mgfxo");
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
                    var effect = new Effect(_device, File.ReadAllBytes(path));
                    _monitor.Log($"Loaded {file}.", LogLevel.Trace);
                    return effect;
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
            (c.FloodLightingEnabled && _floodEffect != null)
            || (c.LightingEnabled && _lighting != null)
            || (c.CloudShadowEnabled && _cloudShadow != null)
            || (c.GodRaysEnabled && _godRays != null)
            || (c.BloomEnabled && _bloom != null)
            || ((c.FogEnabled || c.FogNightMist) && _fogEffect != null)
            || ((c.ColorGradeEnabled || c.BlueLightFilter > 0.001f) && _colorGrade != null)
            || (c.TiltShiftEnabled && _tiltShift != null)
            || ((c.WaterEnabled || c.WaterReflection) && _water != null)
            || ((c.VignetteEnabled || c.ChromaticAberrationEnabled) && _finishing != null);

        private void EnsureTargets(int w, int h, SurfaceFormat format)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);

            if (_sceneRenderTarget != null && _sceneRenderTarget.Width == w && _sceneRenderTarget.Height == h && _sceneRenderTarget.Format == format)
                return;

            _sceneRenderTarget?.Dispose(); _fullResolutionPingA?.Dispose(); _fullResolutionPingB?.Dispose(); _halfResolutionScratchA?.Dispose(); _halfResolutionScratchB?.Dispose();

            _sceneRenderTarget = CreateRenderTarget(w, h, format);
            _fullResolutionPingA = CreateRenderTarget(w, h, format);
            _fullResolutionPingB = CreateRenderTarget(w, h, format);
            _halfResolutionScratchA = CreateRenderTarget(Math.Max(1, w / 2), Math.Max(1, h / 2), format);
            _halfResolutionScratchB = CreateRenderTarget(Math.Max(1, w / 2), Math.Max(1, h / 2), format);
        }

        private RenderTarget2D CreateRenderTarget(int w, int h, SurfaceFormat format) =>
            new(_device, w, h, false, format, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        public void Apply(SpriteBatch spriteBatch, ModConfig config)
        {
            if (!AnyEffectActive(config))
            {
                _masterFade = 0f; // reset so the stack fades back in next time it's enabled
                return;
            }

            if (config.DebugLogging) { _frameCount++; _performanceStopwatch.Restart(); }

            RenderTargetBinding[] bindings = _device.GetRenderTargets();
            if (bindings.Length == 0 || bindings[0].RenderTarget is not RenderTarget2D target)
            {
                if (config.DebugLogging) { _skippedNoTargetCount++; MaybeLogDiag(config); }
                return;
            }

            int w = target.Width, h = target.Height;
            EnsureTargets(w, h, target.Format);

            if (config.DebugLogging)
            {
                _appliedFrameCount++;
                if (w != _lastViewportWidth || h != _lastViewportHeight) { if (_lastViewportWidth != -1) _renderTargetResizeCount++; _lastViewportWidth = w; _lastViewportHeight = h; }
                if (!_loggedOnce) { _monitor.Log($"Post-process {w}x{h}, format={target.Format}.", LogLevel.Debug); _loggedOnce = true; }
                MaybeLogDiag(config);
            }

            // Flush SMAPI's pending world draws into `target`.
            spriteBatch.End();

            try
            {
                _device.SetRenderTarget(_sceneRenderTarget);
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                spriteBatch.Draw(target, new Rectangle(0, 0, w, h), Color.White);
                spriteBatch.End();

                // Auto-exposure meters the captured scene and eases the exposure.
                if (config.ColorGradeEnabled && config.ColorGradeAuto)
                    UpdateAutoExposure(spriteBatch);

                // Build the active stage list (fixed order), then run them ping-pong so
                // the last stage writes straight back into the game's target.
                bool outdoors = Game1.currentLocation?.IsOutdoors ?? false;

                // PRESENCE fades: any stage that (re)appears mid-play — a warp, water coming
                // on screen, stepping outdoors, a cutscene ending — eases in over ~0.5 s
                // instead of popping. A stage that is OFF this frame resets to 0 so its next
                // appearance fades again. (God rays and fog have their own eases already.)
                // A WARP is the one place a hard reset is right: the new map's frame has nothing
                // in common with the old one, and the game's own fade-to-black covers it.
                if (!ReferenceEquals(Game1.currentLocation, _fadeLocation))
                {
                    _fadeLocation = Game1.currentLocation;
                    _fadeWater = _fadeCloud = _fadeLighting = _fadeFlood = _fadeTilt = 0f;
                }

                // Reused list + cached delegates: method-group conversion allocates a new
                // delegate per call, which at 60fps × up to 9 stages is constant GC churn.
                var stages = _stages;
                stages.Clear();
                _lightingStageDelegate ??= RenderLighting; _waterStageDelegate ??= RenderWater; _cloudShadowStageDelegate ??= RenderCloudShadow;
                _godRaysStageDelegate ??= RenderGodRays; _bloomStageDelegate ??= RenderBloom; _fogStageDelegate ??= RenderFog;
                _colorGradeStageDelegate ??= ColorGrade; _tiltShiftStageDelegate ??= RenderTiltShift; _finishingStageDelegate ??= RenderFinishing;
                _floodStageDelegate ??= RenderFloodLight;
                // Lighting first, so everything downstream (bloom/god rays/grade) sees the
                // lit result. FLOOD lighting (occlusion-aware GI lightmap) supersedes the
                // old screen-space lighting stage when enabled — they model the same thing.
                // The two lighting models are mutually exclusive, so a config switch is a
                // CROSS-fade: the outgoing one keeps rendering (and keeps its inputs built)
                // until its presence reaches zero, or the room would flash unlit for ~0.5 s.
                bool floodOn = config.FloodLightingEnabled && _floodEffect != null
                    && TimedBuild(config, 0, () => _flood.Build(_device, w, h, config));
                bool classicOn = !floodOn && config.LightingEnabled && _lighting != null;
                if (floodOn)
                {
                    BuildLightList(w, h, config);       // direct-light pools (shader term)
                    _isFloodOcclusionReady = TimedBuild(config, 1, () => BuildFloodOccluders(w, h));
                }
                else if (classicOn)
                    classicOn = BuildLightList(w, h, config);
                if (classicOn)
                    _shadowsReady = config.LightingShadows && TimedBuild(config, 2, () => BuildOccluderMask(w, h));
                _fadeFlood = floodOn ? Ease01(_fadeFlood) : Ease0(_fadeFlood);
                _fadeLighting = classicOn ? Ease01(_fadeLighting) : Ease0(_fadeLighting);
                if (_fadeFlood > FadeGone) stages.Add(_floodStageDelegate);
                if (_fadeLighting > FadeGone) stages.Add(_lightingStageDelegate);
                // Water ripple first (only if the current location actually has visible
                // water tiles), so everything downstream sees the refracted result.
                // Reflection is independent of the shimmer toggle: either switch keeps
                // the stage alive (the other's params are zeroed inside RenderWater).
                bool waterAllowedHere = WaterAllowedIn(Game1.currentLocation, config);
                // The mask is still rebuilt every frame it needs to be; what changed is that its
                // window-local answer no longer decides whether the STAGE exists. That answer flips
                // every few tiles as the player walks, and each flip is a chance for the presence
                // fade to be wrong - which it was, twice, both times reading as a flash near water.
                // The location's answer changes only on a warp, behind the game's own fade.
                // With every water switch off, skip the build entirely: gather+compose ran on each
                // tile crossing for a result nothing rendered (reported as stutter near water with
                // water disabled). Gated on the GLOBAL flags and the fade only - never the
                // window-local answer above - and kept alive through the fade-out so a toggle
                // still decays instead of popping.
                bool waterConfigOn = config.WaterEnabled || config.WaterReflection;
                if ((waterConfigOn && waterAllowedHere) || _fadeWater > FadeGone)
                    TimedBuild(config, 3, () => BuildWaterMask(w, h));
                bool waterOn = waterConfigOn && _water != null && waterAllowedHere
                    && Game1.currentLocation is { } wloc && LocationHasWater(wloc);
                _fadeWater = waterOn ? Ease01(_fadeWater) : Ease0(_fadeWater);
                // Still rendered while fading: the mask is world-anchored, so the last one built
                // stays correct for the decay frames. Switching the water effect off (or opting a
                // room out from the tuner) used to drop the whole surface in one frame.
                if (_fadeWater > FadeGone && _water != null && _waterMask != null) stages.Add(_waterStageDelegate);
                // Cloud shadows drift over the ground — outdoors only, and first so later
                // effects (bloom/grade) see the shadowed scene. They are SUNLIGHT (or moonlight)
                // being blocked, so they fade with dusk and at night exist only under a bright
                // moon — never stamped over lamp-lit ground on a dark night.
                // Rain, storm and snow put a solid overcast between the sun and the ground: no
                // direct beam is left for a cloud to punch a crisp gap in, so the sharp drifting
                // banks of a clear day must not roll across a rainy one. Cutting them entirely
                // read as the effect being broken though (reported as "cloud shadows do not
                // appear"), and a real overcast is not uniform either — it is slow, heavy mass
                // with soft variation. So keep a fraction of the strength and reshape the field:
                // fewer, larger, slower banks covering most of the ground (see RenderCloudShadow,
                // which reads _cloudOvercastBlend). God rays and the night mist still bow out of
                // this weather completely; they are direct-beam effects with nothing to soften to.
                //
                // Eased rather than switched: weather mods (Cloudy Skies) can flip this mid-day, and
                // the presence fade only ramps a stage back IN, so a hard cut here would pop.
                bool cloudOvercast = Game1.isRaining || Game1.isSnowing || Game1.isLightning;
                Approach(ref _cloudWeatherAmount, cloudOvercast ? 0f : 1f, 0.05f);   // ~1s
                _cloudOvercastBlend = 1f - _cloudWeatherAmount;
                _cloudDayFactor = CloudDayFactor() * MathHelper.Lerp(OvercastCloudStrength, 1f, _cloudWeatherAmount);
                bool cloudOn = config.CloudShadowEnabled && _cloudShadow != null && outdoors && _cloudDayFactor > 0.02f;
                _fadeCloud = cloudOn ? Ease01(_fadeCloud) : Ease0(_fadeCloud);
                if (_fadeCloud > FadeGone && _cloudShadow != null) stages.Add(_cloudShadowStageDelegate);
                // God rays only when there's a real light source on screen (lamp/torch/fire).
                // Every on-screen lamp (up to MaxRayLights) is its own beam origin now — the
                // old single pick either glided the one beam across the screen to the next
                // lamp or had to fade through black to jump; per-light origins make both
                // workarounds unnecessary, so the presence ease below only handles weather,
                // daylight and lights entering/leaving the screen.
                if (config.GodRaysEnabled && _godRays != null)
                {
                    bool hasLight = UpdateRayLights();
                    // Rain/snow: the overcast sky kills visible shafts — fade the rays out (and
                    // back in when it clears). Eased through _godRayAmount so it never pops.
                    bool overcast = outdoors && (Game1.isRaining || Game1.isSnowing || Game1.isLightning);
                    // Shafts are a LOW-LIGHT phenomenon: they exist where a bright source beats a
                    // dim surround. At high noon outdoors nothing on screen is dim, so the same
                    // pass read as a wash hanging off every bright object ("a glow, not god rays").
                    // Ride the presence down to 30% at midday and back to full by
                    // 08:00 / 17:00 — golden hour and night keep the full look. Indoors untouched:
                    // a lamp in a dark room is exactly what rays are for.
                    float rayDay = 1f;
                    if (outdoors)
                    {
                        int rm = (Game1.timeOfDay / 100) * 60 + Game1.timeOfDay % 100;
                        rayDay = 1f - 0.7f * (1f - MathHelper.Clamp(Math.Abs(rm - 750) / 270f, 0f, 1f));
                    }
                    float rayTarget = (hasLight && !overcast) ? rayDay : 0f;
                    Approach(ref _godRayAmount, rayTarget, 0.05f); // ~0.5s fade
                    if (_godRayAmount > 0.01f && _godRayLights.Count > 0) stages.Add(_godRaysStageDelegate);
                }
                if (config.BloomEnabled && _bloom != null) stages.Add(_bloomStageDelegate);
                // Fog is a weak, patchy effect indoors (and covers the black border), so outdoors only.
                // DAY fog and NIGHT mist are separate effects with separate toggles: day fog
                // fades out over dusk exactly as the night mist (sparse blue wisps, clear
                // weather only) fades in. Both amounts are EASED so toggling never pops.
                float night = NightFactorNow();
                float dayTarget = (config.FogEnabled && outdoors) ? config.FogDensity * (1f - night) : 0f;
                float mistTarget = (config.FogNightMist && outdoors && !Game1.isRaining && !Game1.isSnowing)
                    ? config.FogNightMistDensity * night : 0f;
                Approach(ref _fogDayAmount, dayTarget, 0.035f);    // ~0.5-1s ease
                Approach(ref _fogMistAmount, mistTarget, 0.035f);
                if (Math.Abs(dayTarget - _fogDayAmount) < 0.003f) _fogDayAmount = dayTarget;
                if (Math.Abs(mistTarget - _fogMistAmount) < 0.003f) _fogMistAmount = mistTarget;
                if ((_fogDayAmount > 0.004f || _fogMistAmount > 0.004f) && _fogEffect != null && outdoors) stages.Add(_fogStageDelegate);
                if ((config.ColorGradeEnabled || config.BlueLightFilter > 0.001f) && _colorGrade != null) stages.Add(_colorGradeStageDelegate);
                // Tilt-shift (depth-of-field) after grading, so it blurs the graded image.
                // NOT during events: the game draws the event UI (SKIP button) as part of the
                // world frame, and the bottom blur band smears it unreadable. Cutscenes keep
                // the rest of the stack (grade/bloom/fog/clouds) for the cinematic look.
                bool eventUp = Game1.eventUp || Game1.CurrentEvent != null;
                bool tiltOn = config.TiltShiftEnabled && _tiltShift != null && !eventUp;
                _fadeTilt = tiltOn ? Ease01(_fadeTilt) : Ease0(_fadeTilt);
                // Kept in the list while it decays, so a cutscene STARTING pulls the blur out
                // smoothly instead of snapping the frame sharp (it already eased back in).
                if (_fadeTilt > FadeGone && _tiltShift != null) stages.Add(_tiltShiftStageDelegate);
                // Finishing (vignette + chromatic aberration): true camera-lens pass, last.
                // (CA is zeroed inside during events — it fringes the SKIP button's text.)
                if ((config.VignetteEnabled || config.ChromaticAberrationEnabled) && _finishing != null) stages.Add(_finishingStageDelegate);

                Texture2D current = _sceneRenderTarget!;
                for (int i = 0; i < stages.Count; i++)
                {
                    RenderTarget2D dest = i == stages.Count - 1
                        ? target
                        : (ReferenceEquals(current, _fullResolutionPingA) ? _fullResolutionPingB! : _fullResolutionPingA!);
                    stages[i](spriteBatch, current, dest, config);
                    current = dest;
                }

                // Every config-enabled stage can still bail at runtime (indoors, no water,
                // no lights, rays faded out). If none ran, the device is still on _sceneRenderTarget
                // from the capture — restore the game's target or everything drawn after
                // us this frame lands in our scratch buffer.
                if (stages.Count == 0)
                    _device.SetRenderTarget(target);

                // Ease the whole stack in: blend the untouched scene back over the
                // result and let it fade out, so effects don't pop on when enabled
                // or after a load. `current` is the game's target at this point.
                _masterFade = Determinism.Settle(Math.Min(1f, _masterFade + 0.045f), 1f);
                if (_masterFade < 1f && stages.Count > 0)
                {
                    _device.SetRenderTarget(target);
                    spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    spriteBatch.Draw(_sceneRenderTarget, new Rectangle(0, 0, w, h), Color.White * (1f - _masterFade));
                    spriteBatch.End();
                }

                RedrawEventSkipButton(spriteBatch, target);

                // Last thing in the frame, so the capture is the finished picture (skip button
                // included). A failed dump must not cost the player their frame, so it carries
                // its own guard.
                if (_pendingDump != null)
                {
                    try { WriteDump(spriteBatch, target, w, h, config); }
                    catch (Exception ex) { _monitor.Log($"radiance_dump failed: {ex.Message}", LogLevel.Warn); }
                    finally { _pendingDump = null; }
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Post-process failed, leaving frame unmodified this frame: {ex.Message}", LogLevel.Warn);
                // A stage may have thrown between a Begin and its End — close the batch
                // first, or the recovery Begin below throws too (and would escape).
                try { spriteBatch.End(); } catch { }
                try
                {
                    _device.SetRenderTarget(target);
                    spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                    if (_sceneRenderTarget != null) spriteBatch.Draw(_sceneRenderTarget, new Rectangle(0, 0, w, h), Color.White);
                    spriteBatch.End();
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
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            }
            catch (InvalidOperationException)
            {
                // Batch already open (an exotic failure path left it running) — that's the
                // state SMAPI expects anyway, so continue.
            }

            if (config.DebugLogging)
            {
                _performanceStopwatch.Stop();
                double ms = _performanceStopwatch.Elapsed.TotalMilliseconds;
                _performanceTotalMilliseconds += ms;
                if (ms > _performanceMaxMilliseconds) _performanceMaxMilliseconds = ms;
            }
        }

        /// <summary>
        /// Draw the event SKIP button again, on top of the finished frame.
        ///
        /// <para>
        /// The button is not UI as far as this pipeline is concerned. The game draws it inside the
        /// WORLD frame (<c>Event.drawAboveAlwaysFrontLayer</c>), which is the image we capture and
        /// post-process, so every stage lands on it: over water it took the ripple's tint and the
        /// reflection, and it read as the surface covering the button. Tilt-shift already carries
        /// an event exception for the same reason, but that only fixed tilt-shift.
        /// </para>
        ///
        /// <para>
        /// Redrawn rather than masked out. Masking would leave a rectangle of untouched water
        /// around the button, which is a more obvious artifact than the tint was. Drawing the
        /// button again costs one sprite on event frames and puts it back exactly as the game
        /// wanted it: nothing is over the UI.
        /// </para>
        ///
        /// The bounds repeat <c>Event.skipBounds()</c> (private, so it is repeated rather than
        /// called) including its safe-area clamp, so a change to the game's layout shows up as the
        /// button being covered again rather than as a copy in the wrong corner.
        /// </summary>
        private void RedrawEventSkipButton(SpriteBatch spriteBatch, RenderTarget2D target)
        {
            Event? ev = Game1.CurrentEvent;
            // Exactly the game's own draw condition, and nothing more. An earlier version also
            // bailed on ev.skipped, which the game does not test: pressing the button set it
            // immediately while the game kept drawing for several more frames, so the tinted copy
            // came back for those frames and the button read as sinking into the scene.
            if (ev == null || !ev.skippable
                || (Game1.options?.SnappyMenus ?? false) || (Game1.game1?.takingMapScreenshot ?? false)
                || Game1.mouseCursors == null)
                return;

            const int Scale = 4;
            var bounds = new Rectangle(Game1.viewport.Width - 22 * Scale - 8, Game1.viewport.Height - 64,
                22 * Scale, 15 * Scale);
            Utility.makeSafe(ref bounds);
            // Hover dim on RGB only: the game multiplies alpha too, which here would let the tinted
            // copy underneath show through the replacement.
            bool hover = bounds.Contains(Game1.getOldMouseX(), Game1.getOldMouseY());
            Color tint = hover ? new Color(0.5f, 0.5f, 0.5f, 1f) : Color.White;

            _device.SetRenderTarget(target);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            spriteBatch.Draw(Game1.mouseCursors, new Vector2(bounds.X, bounds.Y), new Rectangle(205, 406, 22, 15),
                tint, 0f, Vector2.Zero, Scale, SpriteEffects.None, 0f);
            spriteBatch.End();
        }

        private void MaybeLogDiag(ModConfig config)
        {
            if (_frameCount < 120) return;
            var builderReport = new System.Text.StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                if (_buildMilliseconds[i] <= 0.01) continue;
                builderReport.Append($" {_buildNames[i]}={_buildMilliseconds[i]:0.0}ms(max {_buildMaxMilliseconds[i]:0.0})");
                _buildMilliseconds[i] = _buildMaxMilliseconds[i] = 0;
            }
            _monitor.Log($"[diag] over {_frameCount} frames: applied={_appliedFrameCount}, skipped={_skippedNoTargetCount}, sizeChanges={_renderTargetResizeCount}, size={_lastViewportWidth}x{_lastViewportHeight}, "
                + $"apply avg={(_appliedFrameCount > 0 ? _performanceTotalMilliseconds / _appliedFrameCount : 0):0.00}ms max={_performanceMaxMilliseconds:0.00}ms | builders:{(builderReport.Length > 0 ? builderReport.ToString() : " none")}", LogLevel.Debug);
            _frameCount = _appliedFrameCount = _skippedNoTargetCount = _renderTargetResizeCount = 0;
            _performanceTotalMilliseconds = _performanceMaxMilliseconds = 0;
        }

        // ---- stages --------------------------------------------------------

        private float _cloudDayFactor = 1f;
        /// <summary>Eased 1 → 0 while the sky is overcast (rain / storm / snow): no direct sun means
        /// no gaps for a cloud to cast through.</summary>
        private float _cloudWeatherAmount = 1f;
        /// <summary>0 under a clear sky .. 1 fully overcast — the inverse of
        /// <see cref="_cloudWeatherAmount"/>, read by the stage to reshape the cloud field into
        /// slow heavy mass instead of crisp banks.</summary>
        private float _cloudOvercastBlend;
        /// <summary>What is left of the cloud-shadow strength under a full overcast. Not zero:
        /// an overcast sky still varies, and cutting the effect outright reads as a bug.</summary>
        private const float OvercastCloudStrength = 0.45f;
        // Eased effect amounts so nothing pops: day fog / night mist crossfade over time
        // of day AND ease when toggled; wading self-reflection fades at the water edge.
        private float _fogDayAmount, _fogMistAmount, _pinFadeAmount;

        private readonly EffectParamCache _fxParamCache = new();

        private EffectParameter? GetParam(Effect effect, string name) => _fxParamCache.Get(effect, name);

        private void Pass(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, Effect effect)
        {
            _device.SetRenderTarget(dest);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            spriteBatch.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            spriteBatch.End();
        }

        /// <summary>Pass that ADDS onto what the target already holds — the multi-light god-ray
        /// accumulator (each light's beams sum into one buffer instead of overwriting it).</summary>
        private void PassAdd(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, Effect effect)
        {
            _device.SetRenderTarget(dest);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            spriteBatch.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            spriteBatch.End();
        }

        private void DrawFull(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, Effect effect)
        {
            _device.SetRenderTarget(dest);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            spriteBatch.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            spriteBatch.End();
        }

        /// <summary>Whole-pass presence enforced OUTSIDE the shader: after the pass has drawn,
        /// blend the untouched source back over the result at 1-presence.
        /// <para>
        /// The in-shader Presence uniform was measured doing nothing: the water pass held its
        /// full 0.920 gain with the fade at 0.02 (compiled GLSL shows the mix in place, so the
        /// value is not arriving — water.effect sits at the edge of the profile's constant limits,
        /// the same shader that overflows X4505 on the DX profile). water.effect also has two early
        /// RETURNs the tail mix can never cover, one of which darkens shoreline LAND by 8% with
        /// no gate at all. A SpriteBatch blend after the pass covers every term, both exits, and
        /// any register-mapping trouble, because it never enters the shader.
        /// </para>
        /// The weight is carried by the BLEND FACTOR, not by the drawn alpha. AlphaBlend computes
        /// dest = drawn.rgb + dest.rgb*(1 - drawn.a), and drawn.a is the source texture's own alpha
        /// times the tint - which is not 1 across these targets. Where it was low the second term
        /// failed to remove the pass, so the source was ADDED on top of it instead of mixed with
        /// it: measured at presence 0.10 the water pass came out 1.30x brighter than its input,
        /// and since that presence rises and falls as the player walks toward and away from water,
        /// it read as the picture flashing while walking. BlendFactor ignores alpha entirely.</summary>
        private void BlendBackSource(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, float presence)
        {
            if (presence >= 0.999f)
                return;
            _device.SetRenderTarget(dest);
            spriteBatch.Begin(SpriteSortMode.Deferred, LerpBlend(1f - presence), SamplerState.PointClamp);
            spriteBatch.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            spriteBatch.End();
        }

        private readonly Dictionary<int, BlendState> _lerpBlends = new();

        /// <summary>dest = drawn*k + dest*(1-k), with k in the blend factor so the source
        /// texture's alpha never enters the arithmetic. Quantised to 1/255 and cached, because a
        /// BlendState cannot be modified once the device has bound it.</summary>
        private BlendState LerpBlend(float k)
        {
            int q = MathHelper.Clamp((int)Math.Round(k * 255f), 0, 255);
            if (!_lerpBlends.TryGetValue(q, out BlendState? bs))
            {
                float f = q / 255f;
                bs = new BlendState
                {
                    ColorSourceBlend = Blend.BlendFactor,
                    ColorDestinationBlend = Blend.InverseBlendFactor,
                    AlphaSourceBlend = Blend.BlendFactor,
                    AlphaDestinationBlend = Blend.InverseBlendFactor,
                    BlendFactor = new Color(f, f, f, f),
                };
                _lerpBlends[q] = bs;
            }
            return bs;
        }

        public void Dispose()
        {
            _sceneRenderTarget?.Dispose(); _fullResolutionPingA?.Dispose(); _fullResolutionPingB?.Dispose(); _halfResolutionScratchA?.Dispose(); _halfResolutionScratchB?.Dispose(); _waterMask?.Dispose(); _waterMaskCore?.Dispose(); _occluderMask?.Dispose(); _luminanceRenderTarget?.Dispose(); _noiseTexture?.Dispose(); _noiseTexture = null;
            _spriteMaskRenderTarget?.Dispose(); _spriteMaskSpriteBatch?.Dispose();
            _maskDebugTexture?.Dispose(); _maskDebugTexture = null;
            _bloom?.Dispose(); _colorGrade?.Dispose(); _godRays?.Dispose(); _fogEffect?.Dispose(); _cloudShadow?.Dispose(); _tiltShift?.Dispose();
            _water?.Dispose(); _finishing?.Dispose(); _lighting?.Dispose(); _floodEffect?.Dispose(); _flood.Dispose();
            _sceneRenderTarget = _fullResolutionPingA = _fullResolutionPingB = _halfResolutionScratchA = _halfResolutionScratchB = null;
            _waterMask = null; _waterMaskCore = null; _occluderMask = null; _luminanceRenderTarget = null;
            _spriteMaskRenderTarget = null; _spriteMaskSpriteBatch = null;
            _bloom = _colorGrade = _godRays = _fogEffect = _cloudShadow = _tiltShift = _water = _finishing = _lighting = _floodEffect = null;
            _fxParamCache.Clear(); // parameter cache keys pin the disposed Effects otherwise
            _labelDiffTexture?.Dispose(); _labelDiffTexture = null;
            _channelViewTexture?.Dispose(); _channelViewTexture = null;
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}
