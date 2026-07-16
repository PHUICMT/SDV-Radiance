using System;
using Microsoft.Xna.Framework.Graphics;
using SDVRadiance.Integrations;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace SDVRadiance
{
    /// <summary>
    /// Entry point. Phase 0: hooks the render pipeline and does a passthrough
    /// capture/present to validate the flow, and registers a GMCM config that
    /// exposes the (not-yet-implemented) effect toggles as scaffolding.
    /// </summary>
    public sealed class ModEntry : Mod
    {
        private ModConfig _config = new();
        private RenderPipeline? _pipeline;

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.Display.Rendering += OnRendering;
            helper.Events.Display.Rendered += OnRendered;

            this.Monitor.Log("SDV-Radiance loaded (Phase 0: pipeline skeleton, passthrough).", LogLevel.Info);
        }

        private RenderPipeline Pipeline
        {
            get
            {
                _pipeline ??= new RenderPipeline(Game1_GraphicsDevice, this.Monitor);
                return _pipeline;
            }
        }

        // Convenience accessor; kept as a property so the null-forgiving stays local.
        private static GraphicsDevice Game1_GraphicsDevice =>
            StardewValley.Game1.graphics.GraphicsDevice;

        /// <summary>Redirect the frame into our offscreen target (before the game draws).</summary>
        private void OnRendering(object? sender, RenderingEventArgs e)
        {
            if (!_config.Enabled)
                return;
            Pipeline.BeginCapture(_config.DebugLogging);
        }

        /// <summary>Resolve the offscreen target back to the screen (after the game drew).</summary>
        private void OnRendered(object? sender, RenderedEventArgs e)
        {
            if (!_config.Enabled)
                return;
            Pipeline.EndCaptureAndPresent();
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

            void Save() => this.Helper.WriteConfig(_config);

            api.Register(this.ModManifest, () => _config = new ModConfig(), Save);

            api.AddBoolOption(this.ModManifest, () => _config.Enabled, v => _config.Enabled = v,
                () => I18n("config.enabled.name"), () => I18n("config.enabled.tooltip"));

            // --- Bloom ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.bloom"));
            api.AddBoolOption(this.ModManifest, () => _config.BloomEnabled, v => _config.BloomEnabled = v,
                () => I18n("config.bloom.enabled.name"), () => I18n("config.bloom.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.BloomThreshold, v => _config.BloomThreshold = v,
                () => I18n("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.BloomIntensity, v => _config.BloomIntensity = v,
                () => I18n("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);

            // --- Color grade + fog ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.colorfog"));
            api.AddBoolOption(this.ModManifest, () => _config.ColorGradeEnabled, v => _config.ColorGradeEnabled = v,
                () => I18n("config.colorgrade.enabled.name"), () => I18n("config.colorgrade.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.ColorGradeStrength, v => _config.ColorGradeStrength = v,
                () => I18n("config.colorgrade.strength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(this.ModManifest, () => _config.FogEnabled, v => _config.FogEnabled = v,
                () => I18n("config.fog.enabled.name"), () => I18n("config.fog.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.FogDensity, v => _config.FogDensity = v,
                () => I18n("config.fog.density.name"), null, 0f, 1f, 0.05f);

            // --- DynamicShader parity ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.shadows"));
            api.AddBoolOption(this.ModManifest, () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v,
                () => I18n("config.cloudshadow.enabled.name"), () => I18n("config.cloudshadow.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowCount, v => _config.CloudShadowCount = v,
                () => I18n("config.cloudshadow.count.name"), null, 1, 8, 1);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowOpacity, v => _config.CloudShadowOpacity = v,
                () => I18n("config.cloudshadow.opacity.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(this.ModManifest, () => _config.TiltShiftEnabled, v => _config.TiltShiftEnabled = v,
                () => I18n("config.tiltshift.enabled.name"), () => I18n("config.tiltshift.enabled.tooltip"));

            // --- Water + finishing ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.finishing"));
            api.AddBoolOption(this.ModManifest, () => _config.WaterEnabled, v => _config.WaterEnabled = v,
                () => I18n("config.water.enabled.name"), () => I18n("config.water.enabled.tooltip"));
            api.AddBoolOption(this.ModManifest, () => _config.VignetteEnabled, v => _config.VignetteEnabled = v,
                () => I18n("config.vignette.enabled.name"), () => I18n("config.vignette.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.VignetteStrength, v => _config.VignetteStrength = v,
                () => I18n("config.vignette.strength.name"), null, 0f, 1f, 0.05f);

            // --- Diagnostics ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.debug"));
            api.AddBoolOption(this.ModManifest, () => _config.DebugLogging, v => _config.DebugLogging = v,
                () => I18n("config.debug.name"), () => I18n("config.debug.tooltip"));
        }

        private string I18n(string key) => this.Helper.Translation.Get(key);
    }
}
