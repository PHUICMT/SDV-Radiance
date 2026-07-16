using System;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using SDVRadiance.Integrations;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Entry point. Post-processes the world layer via SMAPI's RenderedWorld event,
    /// capturing the game's own active render target (never binding our own), and
    /// registers a GMCM config for the implemented effects.
    /// </summary>
    public sealed class ModEntry : Mod
    {
        private ModConfig _config = new();
        private RenderPipeline? _pipeline;
        private ShadowRenderer? _shadows;
        private readonly CameraSmoother _camera = new();

        /// <summary>
        /// Mirrors <see cref="ModConfig.Enabled"/> for the static Harmony postfix.
        /// When true, the game is forced to render the world into its buffer
        /// (Game1.screen) so we always have a target to capture during RenderedWorld.
        /// </summary>
        internal static bool ForceBufferDraw;

        /// <summary>
        /// When true, the game's jerky water FRAME-cycle (waterAnimationIndex, a
        /// ~5fps 10-frame gif) is pinned so our shader ripple supplies the surface
        /// motion. The smooth 1px vertical scroll (waterPosition) is left running.
        /// </summary>
        internal static bool FreezeGameWater;

        /// <summary>When true, the vanilla blob shadow is skipped (we draw a directional one instead).</summary>
        internal static bool SuppressVanillaShadows;
        private static IMonitor? SMonitor;
        private static bool _loggedFreeze;

        /// <summary>Skip the game's blob shadow while our directional shadow is active.</summary>
        private static bool DrawShadow_Prefix() => !SuppressVanillaShadows;

        /// <summary>True only when the mod is on AND at least one implemented effect is switched on.</summary>
        private bool EffectsActive => _config.Enabled &&
            (_config.BloomEnabled || _config.ColorGradeEnabled || _config.GodRaysEnabled
             || _config.FogEnabled || _config.CloudShadowEnabled || _config.TiltShiftEnabled
             || _config.WaterEnabled || _config.VignetteEnabled || _config.ChromaticAberrationEnabled
             || _config.LightingEnabled);

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            SMonitor = this.Monitor;
            ForceBufferDraw = EffectsActive;
            FreezeGameWater = _config.Enabled && _config.WaterEnabled;

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.Events.Display.RenderingWorld += OnRenderingWorld;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.RenderingStep += OnRenderingStep;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.ShouldDrawOnBuffer)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(ShouldDrawOnBuffer_Postfix)));
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.updateWater)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(UpdateWater_Postfix)));
            // Suppress the vanilla blob shadow while our directional shadow is casting,
            // so casters don't show both. Farmer overrides DrawShadow, so patch both.
            harmony.Patch(
                original: AccessTools.Method(typeof(Character), nameof(Character.DrawShadow)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(DrawShadow_Prefix)));
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.DrawShadow)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(DrawShadow_Prefix)));

            this.Monitor.Log("SDV-Radiance loaded (world post-processing via RenderedWorld).", LogLevel.Info);
        }

        private RenderPipeline Pipeline
        {
            get
            {
                _pipeline ??= new RenderPipeline(Game1_GraphicsDevice, this.Monitor, this.Helper.DirectoryPath);
                return _pipeline;
            }
        }

        private static GraphicsDevice Game1_GraphicsDevice =>
            Game1.graphics.GraphicsDevice;

        /// <summary>Force the game to draw the world into its buffer so a render target is bound during graphics events.</summary>
        private static void ShouldDrawOnBuffer_Postfix(ref bool __result)
        {
            if (ForceBufferDraw && Game1.gameMode == Game1.playingGameMode)
                __result = true;
        }

        /// <summary>
        /// Pin the game's jerky water frame-cycle (waterAnimationIndex) while our
        /// ripple is active. waterPosition (the smooth 1px vertical scroll) is left
        /// running so the water still gently rises and falls.
        /// </summary>
        private static void UpdateWater_Postfix(GameLocation __instance)
        {
            if (!FreezeGameWater)
                return;
            __instance.waterAnimationIndex = 0;
            if (!_loggedFreeze) { SMonitor?.Log("Water frame-cycle frozen (shader ripple active); vertical scroll left running.", LogLevel.Info); _loggedFreeze = true; }
        }

        /// <summary>Apply the effect chain to the world layer after the game has drawn it.</summary>
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            ForceBufferDraw = EffectsActive; // self-heal: keep the postfix in sync with live config
            FreezeGameWater = _config.Enabled && _config.WaterEnabled;
            if (!EffectsActive)
                return;
            Pipeline.Apply(e.SpriteBatch, _config);
        }

        /// <summary>
        /// Bake the player's silhouette to an offscreen target before the world batches open
        /// (a render-target swap is only safe here, not mid-batch).
        /// </summary>
        private void OnRenderingWorld(object? sender, RenderingWorldEventArgs e)
        {
            if (!_config.Enabled || !_config.DirectionalShadowsEnabled)
                return;
            _shadows ??= new ShadowRenderer();
            ShadowRenderer.Diag = _config.DebugLogging ? this.Monitor : null;
            _shadows.PreparePlayer(Game1_GraphicsDevice, _config);
        }

        /// <summary>
        /// Inject sprite shadows into the game's own World_Sorted pass (FrontToBack), so
        /// they depth-sort correctly: over the ground, under trees/objects/sprites.
        /// </summary>
        private void OnRenderingStep(object? sender, RenderingStepEventArgs e)
        {
            if (e.Step != StardewValley.Mods.RenderSteps.World_Sorted)
                return;
            if (!_config.Enabled || !_config.DirectionalShadowsEnabled)
                return;
            _shadows ??= new ShadowRenderer();
            ShadowRenderer.Diag = _config.DebugLogging ? this.Monitor : null;
            _shadows.DrawInto(e.SpriteBatch, _config);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _camera.Update(_config);
            SuppressVanillaShadows = ShadowRenderer.ShouldCast(_config);
        }

        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (_config.ToggleKey.JustPressed())
            {
                _config.Enabled = !_config.Enabled;
                ForceBufferDraw = EffectsActive;
                this.Helper.WriteConfig(_config);
                Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"SDV-Radiance: {(_config.Enabled ? "ON" : "OFF")}"));
            }

            if (_config.TunerKey.JustPressed())
            {
                if (Game1.activeClickableMenu is RadianceTunerMenu tuner)
                    tuner.exitThisMenu();
                else if (Context.IsPlayerFree)
                    Game1.activeClickableMenu = new RadianceTunerMenu(
                        _config,
                        translate: this.I18n,
                        onChange: () => ForceBufferDraw = EffectsActive,
                        onSave: () => this.Helper.WriteConfig(_config));
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RegisterGmcm();
        }

        private void RegisterGmcm()
        {
            var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api is null)
            {
                this.Monitor.Log("GMCM not installed; config editable via config.json only.", LogLevel.Trace);
                return;
            }

            void Save()
            {
                ForceBufferDraw = EffectsActive;
                this.Helper.WriteConfig(_config);
            }

            api.Register(this.ModManifest, () => { _config = new ModConfig(); ForceBufferDraw = EffectsActive; }, Save);

            api.AddBoolOption(this.ModManifest, () => _config.Enabled, v => _config.Enabled = v,
                () => I18n("config.enabled.name"), () => I18n("config.enabled.tooltip"));

            api.AddParagraph(this.ModManifest, () => I18n("config.preset.hint"));

            // --- Bloom (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.bloom"));
            api.AddBoolOption(this.ModManifest, () => _config.BloomEnabled, v => _config.BloomEnabled = v,
                () => I18n("config.bloom.enabled.name"), () => I18n("config.bloom.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.BloomThreshold, v => _config.BloomThreshold = v,
                () => I18n("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.BloomIntensity, v => _config.BloomIntensity = v,
                () => I18n("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);

            // --- Color grading (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.colorgrade"));
            api.AddBoolOption(this.ModManifest, () => _config.ColorGradeEnabled, v => _config.ColorGradeEnabled = v,
                () => I18n("config.colorgrade.enabled.name"), () => I18n("config.colorgrade.enabled.tooltip"));
            api.AddBoolOption(this.ModManifest, () => _config.ColorGradeAuto, v => _config.ColorGradeAuto = v,
                () => I18n("config.colorgrade.auto.name"), () => I18n("config.colorgrade.auto.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.ColorGradeStrength, v => _config.ColorGradeStrength = v,
                () => I18n("config.colorgrade.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.ColorGradeContrast, v => _config.ColorGradeContrast = v,
                () => I18n("config.colorgrade.contrast.name"), null, 0.5f, 1.5f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.ColorGradeSaturation, v => _config.ColorGradeSaturation = v,
                () => I18n("config.colorgrade.saturation.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.ColorGradeTemperature, v => _config.ColorGradeTemperature = v,
                () => I18n("config.colorgrade.temperature.name"), () => I18n("config.colorgrade.temperature.tooltip"), -1f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.ColorGradeBrightness, v => _config.ColorGradeBrightness = v,
                () => I18n("config.colorgrade.brightness.name"), null, 0.5f, 1.5f, 0.05f);
            api.AddBoolOption(this.ModManifest, () => _config.ColorGradeToneMap, v => _config.ColorGradeToneMap = v,
                () => I18n("config.colorgrade.tonemap.name"), () => I18n("config.colorgrade.tonemap.tooltip"));

            // --- God rays (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.godrays"));
            api.AddBoolOption(this.ModManifest, () => _config.GodRaysEnabled, v => _config.GodRaysEnabled = v,
                () => I18n("config.godrays.enabled.name"), () => I18n("config.godrays.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.GodRaysIntensity, v => _config.GodRaysIntensity = v,
                () => I18n("config.godrays.intensity.name"), null, 0f, 1.5f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.GodRaysThreshold, v => _config.GodRaysThreshold = v,
                () => I18n("config.godrays.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.GodRaysDensity, v => _config.GodRaysDensity = v,
                () => I18n("config.godrays.density.name"), null, 0.1f, 1f, 0.05f);

            // --- Volumetric fog (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.fog"));
            api.AddBoolOption(this.ModManifest, () => _config.FogEnabled, v => _config.FogEnabled = v,
                () => I18n("config.fog.enabled.name"), () => I18n("config.fog.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.FogDensity, v => _config.FogDensity = v,
                () => I18n("config.fog.density.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.FogScale, v => _config.FogScale = v,
                () => I18n("config.fog.scale.name"), null, 1f, 8f, 0.5f);
            api.AddNumberOption(this.ModManifest, () => _config.FogSpeed, v => _config.FogSpeed = v,
                () => I18n("config.fog.speed.name"), null, 0f, 0.1f, 0.005f);

            // --- Cloud shadows (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.cloudshadow"));
            api.AddBoolOption(this.ModManifest, () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v,
                () => I18n("config.cloudshadow.enabled.name"), () => I18n("config.cloudshadow.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowOpacity, v => _config.CloudShadowOpacity = v,
                () => I18n("config.cloudshadow.opacity.name"), null, 0f, 0.7f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowCoverage, v => _config.CloudShadowCoverage = v,
                () => I18n("config.cloudshadow.coverage.name"), null, 0.1f, 0.9f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowScale, v => _config.CloudShadowScale = v,
                () => I18n("config.cloudshadow.scale.name"), null, 1f, 5f, 0.5f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowSpeed, v => _config.CloudShadowSpeed = v,
                () => I18n("config.cloudshadow.speed.name"), null, 0f, 0.1f, 0.005f);

            // --- Tilt-shift (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.tiltshift"));
            api.AddBoolOption(this.ModManifest, () => _config.TiltShiftEnabled, v => _config.TiltShiftEnabled = v,
                () => I18n("config.tiltshift.enabled.name"), () => I18n("config.tiltshift.enabled.tooltip"));
            api.AddTextOption(this.ModManifest,
                () => _config.TiltShiftMode.ToString(),
                v => _config.TiltShiftMode = Enum.TryParse<TiltShiftFocus>(v, out var m) ? m : TiltShiftFocus.Bands,
                () => I18n("config.tiltshift.mode.name"), () => I18n("config.tiltshift.mode.tooltip"),
                new[] { nameof(TiltShiftFocus.Bands), nameof(TiltShiftFocus.Radial) },
                v => I18n($"config.tiltshift.mode.{v.ToLowerInvariant()}"));
            api.AddNumberOption(this.ModManifest, () => _config.TiltShiftStrength, v => _config.TiltShiftStrength = v,
                () => I18n("config.tiltshift.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.TiltShiftRadius, v => _config.TiltShiftRadius = v,
                () => I18n("config.tiltshift.radius.name"), () => I18n("config.tiltshift.radius.tooltip"), 0.05f, 0.9f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.TiltShiftTopRatio, v => _config.TiltShiftTopRatio = v,
                () => I18n("config.tiltshift.top.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.TiltShiftBottomRatio, v => _config.TiltShiftBottomRatio = v,
                () => I18n("config.tiltshift.bottom.name"), null, 0f, 1f, 0.05f);

            // --- Water + finishing (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.finishing"));
            api.AddBoolOption(this.ModManifest, () => _config.WaterEnabled, v => _config.WaterEnabled = v,
                () => I18n("config.water.enabled.name"), () => I18n("config.water.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.WaterStrength, v => _config.WaterStrength = v,
                () => I18n("config.water.strength.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.WaterSpeed, v => _config.WaterSpeed = v,
                () => I18n("config.water.speed.name"), null, 0f, 3f, 0.1f);
            api.AddNumberOption(this.ModManifest, () => _config.WaterSparkle, v => _config.WaterSparkle = v,
                () => I18n("config.water.sparkle.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(this.ModManifest, () => _config.WaterReflection, v => _config.WaterReflection = v,
                () => I18n("config.water.reflection.name"), () => I18n("config.water.reflection.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.WaterReflectStrength, v => _config.WaterReflectStrength = v,
                () => I18n("config.water.reflectstrength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(this.ModManifest, () => _config.VignetteEnabled, v => _config.VignetteEnabled = v,
                () => I18n("config.vignette.enabled.name"), () => I18n("config.vignette.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.VignetteStrength, v => _config.VignetteStrength = v,
                () => I18n("config.vignette.strength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(this.ModManifest, () => _config.ChromaticAberrationEnabled, v => _config.ChromaticAberrationEnabled = v,
                () => I18n("config.ca.enabled.name"), () => I18n("config.ca.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.ChromaticAberrationStrength, v => _config.ChromaticAberrationStrength = v,
                () => I18n("config.ca.strength.name"), null, 0f, 1f, 0.05f);

            // --- Dynamic lighting (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.lighting"));
            api.AddBoolOption(this.ModManifest, () => _config.LightingEnabled, v => _config.LightingEnabled = v,
                () => I18n("config.lighting.enabled.name"), () => I18n("config.lighting.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.LightingIndoorDarkness, v => _config.LightingIndoorDarkness = v,
                () => I18n("config.lighting.indoor.name"), () => I18n("config.lighting.indoor.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.LightingNightDarkness, v => _config.LightingNightDarkness = v,
                () => I18n("config.lighting.night.name"), () => I18n("config.lighting.night.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.LightingWarmth, v => _config.LightingWarmth = v,
                () => I18n("config.lighting.warmth.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.LightingBoost, v => _config.LightingBoost = v,
                () => I18n("config.lighting.boost.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.LightingRadiusScale, v => _config.LightingRadiusScale = v,
                () => I18n("config.lighting.radius.name"), null, 0.2f, 3f, 0.1f);
            api.AddBoolOption(this.ModManifest, () => _config.LightingShadows, v => _config.LightingShadows = v,
                () => I18n("config.lighting.shadows.name"), () => I18n("config.lighting.shadows.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.LightingShadowStrength, v => _config.LightingShadowStrength = v,
                () => I18n("config.lighting.shadowstrength.name"), null, 0f, 1f, 0.05f);

            // --- Directional sprite shadows (Phase 5b) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.shadows"));
            api.AddBoolOption(this.ModManifest, () => _config.DirectionalShadowsEnabled, v => _config.DirectionalShadowsEnabled = v,
                () => I18n("config.shadows.enabled.name"), () => I18n("config.shadows.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.DirectionalShadowStrength, v => _config.DirectionalShadowStrength = v,
                () => I18n("config.shadows.strength.name"), null, 0f, 1f, 0.05f);

            // --- Camera (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.camera"));
            api.AddTextOption(this.ModManifest,
                () => _config.CameraMode.ToString(),
                v => _config.CameraMode = Enum.TryParse<CameraMode>(v, out var m) ? m : CameraMode.Off,
                () => I18n("config.camera.mode.name"), () => I18n("config.camera.mode.tooltip"),
                new[] { nameof(CameraMode.Off), nameof(CameraMode.Smooth) },
                v => I18n($"config.camera.mode.{v.ToLowerInvariant()}"));
            api.AddNumberOption(this.ModManifest, () => _config.CameraFollowSpeed, v => _config.CameraFollowSpeed = v,
                () => I18n("config.smoothcam.speed.name"), () => I18n("config.smoothcam.speed.tooltip"), 0.05f, 1f, 0.05f);

            // --- Hotkeys ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.hotkeys"));
            api.AddKeybindList(this.ModManifest, () => _config.ToggleKey, v => _config.ToggleKey = v,
                () => I18n("config.togglekey.name"), () => I18n("config.togglekey.tooltip"));
            api.AddKeybindList(this.ModManifest, () => _config.TunerKey, v => _config.TunerKey = v,
                () => I18n("config.tunerkey.name"), () => I18n("config.tunerkey.tooltip"));

            // --- Diagnostics ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.debug"));
            api.AddBoolOption(this.ModManifest, () => _config.DebugLogging, v => _config.DebugLogging = v,
                () => I18n("config.debug.name"), () => I18n("config.debug.tooltip"));

            // --- Not yet implemented: shown as a roadmap so options don't imply working features ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.wip"));
            api.AddParagraph(this.ModManifest, () => I18n("config.wip.text"));
        }

        private string I18n(string key) => this.Helper.Translation.Get(key);
    }
}
