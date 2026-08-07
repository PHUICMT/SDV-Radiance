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
        /// <summary>Frames left to trace the per-screen render pass for (radiance_screenwatch).
        /// Counts CALLS, not frames, so on a split screen sixty is thirty frames of two.</summary>
        internal static int ScreenWatchFrames;
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

        /// <summary>Mods that draw daylight through windows themselves. Ours and theirs read the
        /// same thing (the game's own window light) and draw over the same floor, so both running
        /// at full strength means two beams and two patches of sun.</summary>
        private static readonly string[] WindowDrawingModIds = { "Esoterick.DynamicWindows" };

        /// <summary>
        /// Hand the VISIBLE half of window daylight to a mod that specialises in it, once, on the
        /// first launch where that mod is present.
        ///
        /// <para>
        /// Only the beam and the glass: the room's own light through a window is not something a
        /// mod drawing sprites over the picture can do, so that half stays on and the two end up
        /// complementing each other rather than competing. The choice is recorded so a player who
        /// turns the beam back on keeps it, instead of us overruling them at every launch.
        /// </para>
        /// </summary>
        private void StepAsideForWindowMods(IModHelper helper)
        {
            if (!string.IsNullOrEmpty(_config.WindowCompatAppliedFor))
                return;
            foreach (string id in WindowDrawingModIds)
            {
                if (!helper.ModRegistry.IsLoaded(id))
                    continue;
                _config.WindowCompatAppliedFor = id;
                if (_config.WindowBeamEnabled)
                {
                    _config.WindowBeamEnabled = false;
                    this.Monitor.Log($"{id} is installed and draws its own light through windows, so Radiance's "
                        + "window beam and lit glass are off to avoid drawing them twice. The room still lights up "
                        + "from the window. Turn 'Window beam and glass' back on in the config if you want ours instead.",
                        LogLevel.Info);
                }
                helper.WriteConfig(_config);
                return;
            }
        }

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            ApplyConfigMigrations(helper);
            _config.Clamp();
            StepAsideForWindowMods(helper);

            // Fashion Sense animates hair/accessory layers independently of the body frame, so
            // with it installed the player bake refreshes on a heartbeat even when the pose has
            // not changed. Without it, an unchanged pose is an unchanged silhouette and the
            // heartbeat is pure cost, so it only runs when this is true.
            ShadowRenderer.PlayerAccessoriesAnimate = helper.ModRegistry.IsLoaded("PeacefulEnd.FashionSense");

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
                RenderPipeline.MaskEpochReason = "a building was added or removed";
            };
            // A map re-patched in place (Content Patcher seasonal/conditional edits reload the
            // live location's map) can move the water itself, so the cached surface grids have to
            // go. That part is cheap: a grid is rebuilt when its location is next entered.
            //
            // Throwing away the WATER MASK is not cheap, and it used to happen here for ANY
            // Maps/* asset, whether or not it had anything to do with where the player was
            // standing. On a modded install that is a steady drip of invalidations from maps the
            // player is nowhere near - Maps/Pathoschild.CentralStation reloading while the player
            // sits indoors was the one that gave this away - and every one of them rebuilt the
            // surface from scratch under a player who had not moved. That is a flash of water with
            // no cause the player could ever point at, which is what the report says it looks like.
            //
            // The mask only ever holds the CURRENT location, and changing location rebuilds it
            // regardless, so the only reload that can invalidate it is a reload of the map the
            // player is standing on.
            helper.Events.Content.AssetsInvalidated += (_, e) =>
            {
                string? here = Game1.currentLocation?.mapPath?.Value?.Replace('\\', '/');
                bool anyMap = false;
                foreach (var name in e.Names)
                {
                    if (!name.Name.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
                        && !name.Name.StartsWith("Maps\\", StringComparison.OrdinalIgnoreCase))
                        continue;
                    anyMap = true;
                    // Every Maps/* name is checked, not just the first: a single invalidation can
                    // carry a batch, and the player's own map is not always at the front of it.
                    if (here != null && string.Equals(here, name.Name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        RenderPipeline.MaskEpoch++;
                        RenderPipeline.MaskEpochReason = "a mod reloaded the map you are standing on (" + name.Name + ")";
                        break;
                    }
                }
                if (anyMap)
                    SurfaceMap.Clear();
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
            // SPLIT SCREEN TRACE (radiance_screenwatch). This handler runs once per SCREEN per
            // frame, and every expensive cache below it reuses its work while the camera has not
            // moved. With two cameras taking turns, each pass moves the origin the next pass is
            // about to test, so the reuse test can never pass and an async rebuild can never
            // land. Logged from here because here is the one place that knows which screen asked.
            bool watching = ScreenWatchFrames > 0;
            if (watching)
                ScreenWatchFrames--;
            if (!EffectsActive && !RenderPipeline.DumpPending)
            {
                if (watching)
                    this.Monitor.Log($"[screenwatch] screen={Context.ScreenId} SKIPPED (effects not active)", LogLevel.Info);
                return;
            }
            Pipeline.Apply(e.SpriteBatch, _config);
            // Logged AFTER the pass so the frame size is this screen's, not the previous screen's.
            if (watching)
                this.Monitor.Log($"[screenwatch] screen={Context.ScreenId} location={Game1.currentLocation?.NameOrUniqueName} "
                    + Pipeline.DescribeCameraKeyedCaches(), LogLevel.Info);
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
            FrameCost.NextFrame();
            if (prepPlayer)
            {
                _shadows ??= new ShadowRenderer();
                ShadowRenderer.DiagnosticMonitor = _config.DebugLogging ? this.Monitor : null;
                long t0 = FrameCost.Begin();
                _shadows.PreparePlayer(Game1_GraphicsDevice, _config);
                double ms = FrameCost.End(FrameCost.Part.ShadowPrepare, t0);
                if (_config.DebugLogging) _prepareMilliseconds += ms;
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

            if (RenderPipeline.BenchExtraShadowRuns > 0)
                RunBenchmarkShadowRepeats(RenderPipeline.BenchExtraShadowRuns);
            else if (_benchmarkRenderTarget != null && !RenderPipeline.BenchRunning)
            {
                // A full-window target is megabytes. It exists for ten seconds every time somebody
                // presses the benchmark button, and there is no reason to keep it after that.
                _benchmarkRenderTarget.Dispose();
                _benchmarkRenderTarget = null;
            }
        }

        // Scratch surface for the benchmark's extra shadow passes. Sized to the window: the pass
        // draws in screen space, so anything smaller would measure less fill than it really costs.
        private SpriteBatch? _benchmarkSpriteBatch;
        private RenderTarget2D? _benchmarkRenderTarget;

        /// <summary>
        /// Run the shadow pass a few extra times into a scratch target so the benchmark can take
        /// its slope. Nothing drawn here reaches the screen.
        ///
        /// <para>
        /// It has to happen in this event because a render-target swap is only safe before the
        /// world batches open, and it has to be a scratch target because drawing the shadows again
        /// into the real one would stack them and darken the picture for the length of the run.
        /// </para>
        ///
        /// <para>
        /// The repeats find every per-caster bake already warm, so what they measure is the
        /// recurring per-frame draw rather than the one-off bakes. That is the honest thing to
        /// report as a per-frame cost, and it is also the number that grows with a heavy mod list.
        /// A benchmark must never cost the player their frame, so the whole thing is guarded.
        /// </para>
        /// </summary>
        private void RunBenchmarkShadowRepeats(int runs)
        {
            if (_shadows == null || !_config.DirectionalShadowsEnabled)
                return;
            try
            {
                var device = Game1_GraphicsDevice;
                int w = Math.Max(1, device.PresentationParameters.BackBufferWidth);
                int h = Math.Max(1, device.PresentationParameters.BackBufferHeight);
                if (_benchmarkRenderTarget == null || _benchmarkRenderTarget.Width != w || _benchmarkRenderTarget.Height != h)
                {
                    _benchmarkRenderTarget?.Dispose();
                    _benchmarkRenderTarget = new RenderTarget2D(device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
                }
                _benchmarkSpriteBatch ??= new SpriteBatch(device);

                var previous = device.GetRenderTargets();
                ShadowRenderer.BenchmarkAmplifying = true;
                try
                {
                    device.SetRenderTarget(_benchmarkRenderTarget);
                    for (int i = 0; i < runs; i++)
                    {
                        _benchmarkSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                        _shadows.DrawInto(_benchmarkSpriteBatch, _config);
                        _benchmarkSpriteBatch.End();
                    }
                }
                finally
                {
                    ShadowRenderer.BenchmarkAmplifying = false;
                    device.SetRenderTargets(previous);
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"[bench] shadow measurement skipped: {ex.Message}", LogLevel.Trace);
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
            long t0 = FrameCost.Begin();
            _shadows.DrawInto(e.SpriteBatch, _config);
            double ms = FrameCost.End(FrameCost.Part.ShadowDraw, t0);
            if (_config.DebugLogging)
            {
                _drawMilliseconds += ms;
                if (ms > _maxDrawMilliseconds) _maxDrawMilliseconds = ms;
                if (++_performanceFrameCount >= 300)
                {
                    this.Monitor.Log($"[perf] shadows over {_performanceFrameCount} frames: prepare avg={_prepareMilliseconds / _performanceFrameCount:0.00}ms, "
                        + $"draw avg={_drawMilliseconds / _performanceFrameCount:0.00}ms max={_maxDrawMilliseconds:0.00}ms.", LogLevel.Debug);
                    _prepareMilliseconds = _drawMilliseconds = _maxDrawMilliseconds = 0; _performanceFrameCount = 0;
                }
            }
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
