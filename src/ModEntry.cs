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
        private readonly CameraSmoother _camera = new();

        /// <summary>
        /// Mirrors <see cref="ModConfig.Enabled"/> for the static Harmony postfix.
        /// When true, the game is forced to render the world into its buffer
        /// (Game1.screen) so we always have a target to capture during RenderedWorld.
        /// </summary>
        internal static bool ForceBufferDraw;

        /// <summary>True only when the mod is on AND at least one implemented effect is switched on.</summary>
        private bool EffectsActive => _config.Enabled && _config.BloomEnabled;

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            ForceBufferDraw = EffectsActive;

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.ShouldDrawOnBuffer)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(ShouldDrawOnBuffer_Postfix)));

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

        /// <summary>Apply the effect chain to the world layer after the game has drawn it.</summary>
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            ForceBufferDraw = EffectsActive; // self-heal: keep the postfix in sync with live config
            if (!EffectsActive)
                return;
            Pipeline.Apply(e.SpriteBatch, _config);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _camera.Update(_config);
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

            // --- Bloom (implemented) ---
            api.AddSectionTitle(this.ModManifest, () => I18n("config.section.bloom"));
            api.AddBoolOption(this.ModManifest, () => _config.BloomEnabled, v => _config.BloomEnabled = v,
                () => I18n("config.bloom.enabled.name"), () => I18n("config.bloom.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.BloomThreshold, v => _config.BloomThreshold = v,
                () => I18n("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.BloomIntensity, v => _config.BloomIntensity = v,
                () => I18n("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);

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
