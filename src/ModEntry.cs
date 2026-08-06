using System;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Entry point — the whole frame at a glance. Post-processes the world layer via
    /// SMAPI's RenderedWorld event, capturing the game's own active render target
    /// (never binding our own):
    /// <list type="bullet">
    /// <item><see cref="OnRenderingWorld"/> — pre-frame bakes (player silhouette, water sprite mask, reflection layers)</item>
    /// <item><see cref="OnRenderingStep"/> — inject sprite shadows into the game's sorted world batch</item>
    /// <item><see cref="OnRenderedWorld"/> — run the post-process effect chain (see RenderPipeline.Apply)</item>
    /// <item><see cref="OnUpdateTicked"/> — refresh the vanilla-shadow suppression gates</item>
    /// </list>
    /// Harmony patches live in <see cref="HarmonyPatcher"/>, console commands in
    /// <see cref="ConsoleCommands"/>, GMCM pages in <see cref="GmcmRegistration"/>.
    /// </summary>
    public sealed class ModEntry : Mod
    {
        private ModConfig _config = new();
        private Harmony? _harmony;
        private RenderPipeline? _pipeline;
        private ShadowRenderer? _shadows;
        private readonly CameraSmoother _camera = new();

        /// <summary>Mod version, for anything that has to record which build produced it (dumps).</summary>
        internal static string SVersion = "?";

        /// <summary>True only when the mod is on AND at least one implemented effect is switched on.</summary>
        private bool EffectsActive => _config.Enabled &&
            (_config.BloomEnabled || _config.ColorGradeEnabled || _config.GodRaysEnabled
             || _config.FogEnabled || _config.FogNightMist || _config.CloudShadowEnabled || _config.TiltShiftEnabled
             || _config.WaterEnabled || _config.WaterReflection
             || _config.VignetteEnabled || _config.ChromaticAberrationEnabled
             || _config.LightingEnabled || _config.FloodLightingEnabled
             || _config.BlueLightFilter > 0.001f);

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            ApplyConfigMigrations(helper);
            _config.Clamp();

            SVersion = this.ModManifest.Version.ToString();
            HarmonyPatcher.ForceBufferDraw = EffectsActive;
            HarmonyPatcher.FreezeGameWater = _config.Enabled && _config.WaterEnabled;
            WaterDrawHook.Enabled = _config.Enabled && (_config.WaterEnabled || _config.WaterReflection);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.Events.Display.RenderingWorld += OnRenderingWorld;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.RenderingStep += OnRenderingStep;

            // Surface grids are inferred per location and cached for the visit. A save load means
            // a whole new world, and placing/removing a farm building changes a map in place.
            helper.Events.GameLoop.SaveLoaded += (_, _) => SurfaceMap.Clear();
            helper.Events.World.BuildingListChanged += (_, e) =>
            {
                SurfaceMap.Invalidate(e.Location);
                // Fish-pond water lives in the mask now — a pond placed/moved/removed must
                // show up on the next frame, not on the 10 s safety refresh.
                RenderPipeline.MaskEpoch++;
            };
            // A map re-patched in place (Content Patcher seasonal/conditional edits reload the
            // live location's map) can move the water itself. Invalidate both the surface grid
            // and the mask when any Maps/* asset reloads.
            helper.Events.Content.AssetsInvalidated += (_, e) =>
            {
                foreach (var name in e.Names)
                {
                    if (name.Name.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
                        || name.Name.StartsWith("Maps\\", StringComparison.OrdinalIgnoreCase))
                    {
                        SurfaceMap.Clear();
                        RenderPipeline.MaskEpoch++;
                        break;
                    }
                }
            };

            SurfaceMap.DiagnosticMonitor = this.Monitor;
            MapDump.BridgeMonitor = this.Monitor;
            MapDump.BridgeHelper = helper;
            ConsoleCommands.RegisterAll(helper, this.Monitor, () => _config, () => _pipeline);

            _harmony = new Harmony(this.ModManifest.UniqueID);
            HarmonyPatcher.InstallAll(_harmony, this.Monitor);

            this.Monitor.Log("SDV-Radiance loaded (world post-processing via RenderedWorld).", LogLevel.Info);

            // Local dev harness: src/DevMenu.local.cs is git-excluded, so it only exists on the
            // author's machine; it additionally requires a dev.local.flag file in the mod folder.
            // Reflection keeps this call harmless when neither is present (i.e. every release).
            if (System.IO.File.Exists(System.IO.Path.Combine(helper.DirectoryPath, "dev.local.flag")))
                Type.GetType("SDVRadiance.DevMenuLoader")
                    ?.GetMethod("Init")
                    ?.Invoke(null, new object[] { helper, this.Monitor, (Func<ModConfig>)(() => _config) });
        }

        /// <summary>One-time config fixes for users upgrading from older versions.</summary>
        private void ApplyConfigMigrations(IModHelper helper)
        {
            // 1.3.1: god rays off. Changing the DEFAULT only reaches new installs — everyone
            // already playing has GodRaysEnabled written in their config.json, which is exactly
            // the group reporting blown-out white sprites. So switch it off once, record that we
            // did, and never touch their choice again.
            if (_config.ConfigVersion < 1)
            {
                _config.ConfigVersion = 1;
                if (_config.GodRaysEnabled)
                {
                    _config.GodRaysEnabled = false;
                    this.Monitor.Log("God rays switched off: the effect treats bright surfaces as light sources, so pale sprites blow out. "
                                   + "It is being rebuilt for 1.4.0 — re-enable it in the config or with F6 if you want it back.", LogLevel.Info);
                }
                helper.WriteConfig(_config);
            }
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

        /// <summary>Apply the effect chain to the world layer after the game has drawn it.</summary>
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            // Self-heal: keep the postfix in sync with live config. A pending capture holds the
            // buffer open too, because the vanilla half of a before/after pair is taken with the
            // whole stack off, and with no buffer bound there is nothing to read back.
            HarmonyPatcher.ForceBufferDraw = EffectsActive || RenderPipeline.DumpPending;
            // Only freeze the game's own water frame-cycle where we actually render ripple this
            // frame — otherwise decorative indoor water (with the effect turned off there) would
            // sit frozen instead of playing its normal vanilla animation.
            bool waterHere = RenderPipeline.WaterAllowedIn(Game1.currentLocation, _config);
            HarmonyPatcher.FreezeGameWater = _config.Enabled && _config.WaterEnabled && waterHere;
            WaterDrawHook.Enabled = _config.Enabled && (_config.WaterEnabled || _config.WaterReflection);
            if (!EffectsActive && !RenderPipeline.DumpPending)
                return;
            Pipeline.Apply(e.SpriteBatch, _config);
            if (RenderPipeline.DebugChannel != DebugOverlayChannel.Off)
                Pipeline.DrawDebugOverlay(e.SpriteBatch);
        }

        /// <summary>
        /// Bake the player's silhouette to an offscreen target before the world batches open
        /// (a render-target swap is only safe here, not mid-batch).
        /// </summary>
        private void OnRenderingWorld(object? sender, RenderingWorldEventArgs e)
        {
            if (!_config.Enabled)
                return;

            // Player silhouette + colour bake FIRST: the reflection below stamps the player
            // from it, so baking afterwards mirrored last frame's pose. It also has to run
            // when only the reflection needs it — see PreparePlayer, where the shadow-only
            // toggle used to freeze the mirrored player at whatever pose was baked last.
            bool prepPlayer = _config.DirectionalShadowsEnabled
                || (_config.WaterReflection && Context.IsWorldReady);
            if (prepPlayer)
            {
                _shadows ??= new ShadowRenderer();
                ShadowRenderer.DiagnosticMonitor = _config.DebugLogging ? this.Monitor : null;
                if (_config.DebugLogging)
                {
                    _performanceStopwatch.Restart();
                    _shadows.PreparePlayer(Game1_GraphicsDevice, _config);
                    _performanceStopwatch.Stop();
                    _prepareMilliseconds += _performanceStopwatch.Elapsed.TotalMilliseconds;
                }
                else
                    _shadows.PreparePlayer(Game1_GraphicsDevice, _config);
            }

            // Per-frame water sprite mask (ducks/NPCs/critters on water must not ripple).
            // Baked here because a render-target swap is only safe before the world batches open.
            if ((_config.WaterEnabled || _config.WaterReflection) && Context.IsWorldReady)
            {
                _pipeline?.BakeWaterSpriteMask();
                // P3b: flipped-entity reflection layer (player/NPCs/animals/trees), built
                // by construction instead of trusting whatever sits above on screen.
                if (_config.WaterReflection)
                {
                    _pipeline?.BakeWaterReflection();
                    // P3c: sprite-free map render — the mirror's source, so an excluded
                    // sprite shows the true map pixels behind it instead of a sky hole.
                    _pipeline?.BakeSceneryReflection();
                }
            }
        }

        // Perf probes (DebugLogging only): where the frame time actually goes, so stutter
        // reports can be pinned to a subsystem instead of guessed at.
        private readonly System.Diagnostics.Stopwatch _performanceStopwatch = new();
        private double _prepareMilliseconds, _drawMilliseconds, _maxDrawMilliseconds;
        private int _performanceFrameCount;

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
            ShadowRenderer.DiagnosticMonitor = _config.DebugLogging ? this.Monitor : null;
            if (_config.DebugLogging)
            {
                _performanceStopwatch.Restart();
                _shadows.DrawInto(e.SpriteBatch, _config);
                _performanceStopwatch.Stop();
                double ms = _performanceStopwatch.Elapsed.TotalMilliseconds;
                _drawMilliseconds += ms;
                if (ms > _maxDrawMilliseconds) _maxDrawMilliseconds = ms;
                if (++_performanceFrameCount >= 300)
                {
                    this.Monitor.Log($"[perf] shadows over {_performanceFrameCount} frames: prepare avg={_prepareMilliseconds / _performanceFrameCount:0.00}ms, "
                        + $"draw avg={_drawMilliseconds / _performanceFrameCount:0.00}ms max={_maxDrawMilliseconds:0.00}ms.", LogLevel.Debug);
                    _prepareMilliseconds = _drawMilliseconds = _maxDrawMilliseconds = 0; _performanceFrameCount = 0;
                }
            }
            else
                _shadows.DrawInto(e.SpriteBatch, _config);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _camera.Update(_config);
            ShadowSuppression.SuppressVanillaShadows = ShadowRenderer.ShadowsActiveNow(_config);
            // Suppress the BUSH blob (fixed-direction, fights our cast); the TREE blob is kept
            // (not patched) as a base anchor under the canopy.
            // Only hide the vanilla drifting cloud when OUR cloud shadow is actually on —
            // otherwise turning Cloud Shadows off silently removed vanilla clouds too.
            ShadowSuppression.SuppressVanillaClouds = _config.Enabled && _config.SuppressVanillaCloudShadow && _config.CloudShadowEnabled;
            // GGR interop: skipping the Cloud critter's DRAW (Cloud_Draw_Prefix) leaves it in
            // location.critters, so Global God Rays still dims its rays "under" the now-invisible
            // cloud shadow. Remove the Cloud critters outright while we suppress them, so nothing
            // downstream reacts to a shadow that no longer renders. (Our own cloud shadows come
            // from the CloudShadow shader stage, not this critter, so nothing of ours is lost.)
            if (ShadowSuppression.SuppressVanillaClouds && Context.IsWorldReady)
                Game1.currentLocation?.critters?.RemoveAll(c => c is StardewValley.BellsAndWhistles.Cloud);
            ShadowSuppression.SuppressVanillaObjectShadows = _config.DirectionalShadowObjects && ShadowRenderer.SunShadowActive(_config);
            // Big-craftable blobs are replaced in BOTH paths (sun directional + indoor/night contact),
            // so gate on ShadowsActiveNow, not just the sun path.
            ShadowSuppression.SuppressVanillaBlobShadows = _config.DirectionalShadowObjects && ShadowRenderer.ShadowsActiveNow(_config);
            // Sun path only: our critter silhouettes draw only under the sun, so on rainy days the
            // vanilla critter blob stays (better a blob than no shadow at all).
            ShadowSuppression.SuppressVanillaCritterShadows = _config.DirectionalShadowObjects && ShadowRenderer.SunShadowActive(_config);
        }

        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (_config.ToggleKey.JustPressed())
            {
                _config.Enabled = !_config.Enabled;
                HarmonyPatcher.ForceBufferDraw = EffectsActive;
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
                        onChange: () => HarmonyPatcher.ForceBufferDraw = EffectsActive,
                        onSave: () => this.Helper.WriteConfig(_config));
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            GmcmRegistration.Register(this.Helper, this.ModManifest, this.Monitor, this.I18n,
                config: () => _config,
                replaceConfig: fresh => _config = fresh,
                refreshForceBufferDraw: () => HarmonyPatcher.ForceBufferDraw = EffectsActive);

            // Hand-painted liquid ground truth, shipped in labels/ and read ONCE. It is versioned
            // data, not live state: it changes when this mod updates, so there is no file watching.
            LabelStore.Instance = new LabelStore(
                System.IO.Path.Combine(this.Helper.DirectoryPath, "labels"), this.Monitor);
            if (LabelStore.Instance.Any)
                this.Monitor.Log($"Water labels loaded: {LabelStore.Instance.SheetCount} sheets, "
                    + $"{LabelStore.Instance.TileCount} tiles.", LogLevel.Info);
            else
                this.Monitor.Log("No water labels found in labels/ — falling back to colour classification.", LogLevel.Warn);

            // Draw-call-accurate water discovery: patch drawWaterTile on GameLocation AND every
            // loaded override (mod location classes included) — hence GameLaunched, not Entry.
            WaterDrawHook.Install(_harmony!, this.Monitor);
        }

        private string I18n(string key) => this.Helper.Translation.Get(key);
    }
}
