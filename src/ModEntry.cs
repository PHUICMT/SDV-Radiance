using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
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

        /// <summary>When true, vanilla tree/bush baked blob shadows are skipped (our object shadows replace them).</summary>
        internal static bool SuppressVanillaObjectShadows;

        /// <summary>When true, vanilla <see cref="Game1.shadowTexture"/> blob shadows (big craftables) are
        /// skipped. Gated on ShadowsActiveNow so it also covers the indoor/night ambient path.</summary>
        internal static bool SuppressVanillaBlobShadows;
        private static IMonitor? SMonitor;
        private static bool _loggedFreeze;

        /// <summary>Skip the game's blob shadow while our directional shadow is active.</summary>
        private static bool DrawShadow_Prefix() => !SuppressVanillaShadows;

        /// <summary>When true, the vanilla drifting Cloud critter shadow is hidden.</summary>
        internal static bool SuppressVanillaClouds;

        /// <summary>Skip the vanilla <c>Cloud</c> critter's drifting shadow draw.</summary>
        private static bool Cloud_Draw_Prefix() => !SuppressVanillaClouds;

        /// <summary>
        /// Transpiler shim for Tree.draw / Bush.draw: every vanilla tree/bush blob shadow is
        /// drawn at layerDepth exactly 1E-06f (nothing else in those methods uses it), so we
        /// swallow just those draws while our object shadows are active and forward the rest.
        /// </summary>
        public static void Draw_SkipVanillaShadow(SpriteBatch sb, Texture2D tex, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (layerDepth == 1E-06f && SuppressVanillaObjectShadows)
                return;
            sb.Draw(tex, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Shim for Object.draw: drop the vanilla <see cref="Game1.shadowTexture"/> blob (big
        /// craftables draw it at an object-specific depth, so we key on the texture, not layerDepth).</summary>
        public static void Draw_SkipBlobShadow(SpriteBatch sb, Texture2D tex, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaBlobShadows && ReferenceEquals(tex, Game1.shadowTexture))
                return;
            sb.Draw(tex, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>When true, critters' vanilla blob shadows are skipped (our directional critter
        /// shadows replace them — sun path only, so rainy days keep the vanilla blob).</summary>
        internal static bool SuppressVanillaCritterShadows;

        /// <summary>Shim for Critter draw methods: drop only their Game1.shadowTexture blob.</summary>
        public static void Draw_SkipCritterShadow(SpriteBatch sb, Texture2D tex, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaCritterShadows && ReferenceEquals(tex, Game1.shadowTexture))
                return;
            sb.Draw(tex, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Redirect a method's 9-arg SpriteBatch.Draw calls through <paramref name="shimName"/>.</summary>
        private static System.Collections.Generic.IEnumerable<CodeInstruction> RedirectDraws(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions, string shimName)
        {
            var drawMethod = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), new[]
            {
                typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color),
                typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float)
            });
            var shim = AccessTools.Method(typeof(ModEntry), shimName);
            foreach (var ins in instructions)
            {
                if (ins.Calls(drawMethod))
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, shim) { labels = ins.labels, blocks = ins.blocks };
                else
                    yield return ins;
            }
        }

        /// <summary>Tree/Bush: drop the depth==1E-06 blob draws.</summary>
        private static System.Collections.Generic.IEnumerable<CodeInstruction> DrawShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipVanillaShadow));

        /// <summary>Object.draw: drop the Game1.shadowTexture blob draws.</summary>
        private static System.Collections.Generic.IEnumerable<CodeInstruction> BlobShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipBlobShadow));

        /// <summary>Critter draw methods: drop the Game1.shadowTexture blob draws.</summary>
        private static System.Collections.Generic.IEnumerable<CodeInstruction> CritterShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipCritterShadow));

        /// <summary>True only when the mod is on AND at least one implemented effect is switched on.</summary>
        private bool EffectsActive => _config.Enabled &&
            (_config.BloomEnabled || _config.ColorGradeEnabled || _config.GodRaysEnabled
             || _config.FogEnabled || _config.FogNightMist || _config.CloudShadowEnabled || _config.TiltShiftEnabled
             || _config.WaterEnabled || _config.WaterReflection
             || _config.VignetteEnabled || _config.ChromaticAberrationEnabled
             || _config.LightingEnabled || _config.FloodLightingEnabled);

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            _config.Clamp();
            SMonitor = this.Monitor;
            ForceBufferDraw = EffectsActive;
            FreezeGameWater = _config.Enabled && _config.WaterEnabled;

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.ConsoleCommands.Add("radiance_lights",
                "List every active light source in the current location (id, kind, tile, radius, color, distance from player).",
                (cmd, args) => DumpLights());
            helper.ConsoleCommands.Add("radiance_tile",
                "Dump water-related data for the tile under the player (layer properties, HF class, isWaterTile).",
                (cmd, args) => DumpTile());
            helper.ConsoleCommands.Add("radiance_maskdump",
                "Save the water mask textures to PNG in the temp folder (debug).",
                (cmd, args) => this.Monitor.Log(_pipeline?.DumpMasks(System.IO.Path.GetTempPath()) ?? "pipeline not ready", LogLevel.Info));
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
            // Trees and bushes bake their blob shadow inline in draw() at a FIXED direction that
            // fights our directional cast; route their Draw calls through a shim that drops just
            // the depth==1E-06 (shadow) draws while our object shadows are active.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.TerrainFeatures.Tree), nameof(StardewValley.TerrainFeatures.Tree.draw)),
                transpiler: new HarmonyMethod(typeof(ModEntry), nameof(DrawShadow_Transpiler)));
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.TerrainFeatures.Bush), nameof(StardewValley.TerrainFeatures.Bush.draw), new[] { typeof(SpriteBatch) }),
                transpiler: new HarmonyMethod(typeof(ModEntry), nameof(DrawShadow_Transpiler)));
            // Big craftables draw a vanilla Game1.shadowTexture blob in Object.draw(b,x,y,alpha);
            // drop it while our object shadows are active so it doesn't double up.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Object), nameof(StardewValley.Object.draw), new[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                transpiler: new HarmonyMethod(typeof(ModEntry), nameof(BlobShadow_Transpiler)));
            // The vanilla drifting cloud shadow is a Cloud critter drawn in drawAboveFrontLayer;
            // skip it (opt-out) so it doesn't compete with our own cloud-shadow effect.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.BellsAndWhistles.Cloud), nameof(StardewValley.BellsAndWhistles.Cloud.drawAboveFrontLayer), new[] { typeof(SpriteBatch) }),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(Cloud_Draw_Prefix)));
            // Critters draw their own Game1.shadowTexture blob inside draw()/drawAboveFrontLayer()
            // (base class + several overrides). Route every Critter subclass's declared draw
            // methods through a shim that drops just those blob draws while ours are casting.
            foreach (var t in typeof(StardewValley.BellsAndWhistles.Critter).Assembly.GetTypes())
            {
                if (!typeof(StardewValley.BellsAndWhistles.Critter).IsAssignableFrom(t)
                    || t == typeof(StardewValley.BellsAndWhistles.Cloud))
                    continue;
                foreach (string name in new[] { "draw", "drawAboveFrontLayer" })
                {
                    var mi = t.GetMethod(name,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                        null, new[] { typeof(SpriteBatch) }, null);
                    if (mi == null || mi.IsAbstract)
                        continue;
                    try
                    {
                        harmony.Patch(mi, transpiler: new HarmonyMethod(typeof(ModEntry), nameof(CritterShadow_Transpiler)));
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Critter shadow patch skipped for {t.Name}.{name}: {ex.Message}", LogLevel.Trace);
                    }
                }
            }

            this.Monitor.Log("SDV-Radiance loaded (world post-processing via RenderedWorld).", LogLevel.Info);

            // Local dev harness: src/DevMenu.local.cs is git-excluded, so it only exists on the
            // author's machine; it additionally requires a dev.local.flag file in the mod folder.
            // Reflection keeps this call harmless when neither is present (i.e. every release).
            if (System.IO.File.Exists(System.IO.Path.Combine(helper.DirectoryPath, "dev.local.flag")))
                Type.GetType("SDVRadiance.DevMenuLoader")
                    ?.GetMethod("Init")
                    ?.Invoke(null, new object[] { helper, this.Monitor, (Func<ModConfig>)(() => _config) });
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
            if (!_config.Enabled)
                return;

            // Per-frame water sprite mask (ducks/NPCs/critters on water must not ripple).
            // Baked here because a render-target swap is only safe before the world batches open.
            if ((_config.WaterEnabled || _config.WaterReflection) && Context.IsWorldReady)
                _pipeline?.BakeWaterSpriteMask();

            if (!_config.DirectionalShadowsEnabled)
                return;
            _shadows ??= new ShadowRenderer();
            ShadowRenderer.Diag = _config.DebugLogging ? this.Monitor : null;
            if (_config.DebugLogging)
            {
                _perfSw.Restart();
                _shadows.PreparePlayer(Game1_GraphicsDevice, _config);
                _perfSw.Stop();
                _prepMs += _perfSw.Elapsed.TotalMilliseconds;
            }
            else
                _shadows.PreparePlayer(Game1_GraphicsDevice, _config);
        }

        // Perf probes (DebugLogging only): where the frame time actually goes, so stutter
        // reports can be pinned to a subsystem instead of guessed at.
        private readonly System.Diagnostics.Stopwatch _perfSw = new();
        private double _prepMs, _drawMs, _drawMaxMs;
        private int _perfFrames;

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
            if (_config.DebugLogging)
            {
                _perfSw.Restart();
                _shadows.DrawInto(e.SpriteBatch, _config);
                _perfSw.Stop();
                double ms = _perfSw.Elapsed.TotalMilliseconds;
                _drawMs += ms;
                if (ms > _drawMaxMs) _drawMaxMs = ms;
                if (++_perfFrames >= 300)
                {
                    this.Monitor.Log($"[perf] shadows over {_perfFrames} frames: prepare avg={_prepMs / _perfFrames:0.00}ms, "
                        + $"draw avg={_drawMs / _perfFrames:0.00}ms max={_drawMaxMs:0.00}ms.", LogLevel.Debug);
                    _prepMs = _drawMs = _drawMaxMs = 0; _perfFrames = 0;
                }
            }
            else
                _shadows.DrawInto(e.SpriteBatch, _config);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _camera.Update(_config);
            SuppressVanillaShadows = ShadowRenderer.ShadowsActiveNow(_config);
            // Suppress the BUSH blob (fixed-direction, fights our cast); the TREE blob is kept
            // (not patched) as a base anchor under the canopy.
            // Only hide the vanilla drifting cloud when OUR cloud shadow is actually on —
            // otherwise turning Cloud Shadows off silently removed vanilla clouds too.
            SuppressVanillaClouds = _config.Enabled && _config.SuppressVanillaCloudShadow && _config.CloudShadowEnabled;
            SuppressVanillaObjectShadows = _config.DirectionalShadowObjects && ShadowRenderer.SunShadowActive(_config);
            // Big-craftable blobs are replaced in BOTH paths (sun directional + indoor/night contact),
            // so gate on ShadowsActiveNow, not just the sun path.
            SuppressVanillaBlobShadows = _config.DirectionalShadowObjects && ShadowRenderer.ShadowsActiveNow(_config);
            // Sun path only: our critter silhouettes draw only under the sun, so on rainy days the
            // vanilla critter blob stays (better a blob than no shadow at all).
            SuppressVanillaCritterShadows = _config.DirectionalShadowObjects && ShadowRenderer.SunShadowActive(_config);
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

        /// <summary>Console command: dump every light the game currently tracks, so "why does my
        /// room have N shadows" is answerable — each listed light casts its own shadow.</summary>
        private void DumpLights()
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }
            var lights = Game1.currentLightSources;
            var loc = Game1.currentLocation;
            this.Monitor.Log($"=== Lights in {loc?.NameOrUniqueName} ({(lights?.Count ?? 0)} total) ===", LogLevel.Info);
            if (lights == null || lights.Count == 0)
                return;
            Vector2 pfeet = Game1.player.Position;
            int i = 0;
            foreach (var kv in lights)
            {
                var ls = kv.Value;
                Vector2 tile = ls.position.Value / 64f;
                float distTiles = Vector2.Distance(ls.position.Value, pfeet) / 64f;
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                bool onScreen = screen.X > -640 && screen.X < Game1.viewport.Width + 640
                             && screen.Y > -640 && screen.Y < Game1.viewport.Height + 640;
                var c = ls.color.Value;
                this.Monitor.Log(
                    $"[{i++}] id={kv.Key} ctx={ls.lightContext.Value} tex={ls.textureIndex.Value} " +
                    $"tile=({tile.X:0.0},{tile.Y:0.0}) radius={ls.radius.Value:0.00} " +
                    $"color(raw/subtractive)=({c.R},{c.G},{c.B},{c.A}) dist={distTiles:0.0} tiles " +
                    $"onScreen={onScreen}", LogLevel.Info);
            }
            if (loc != null)
            {
                var glows = loc.lightGlows;
                this.Monitor.Log($"--- lightGlows ({glows.Count}) — a WindowLight with no glow nearby is stale and won't cast ---", LogLevel.Info);
                foreach (Vector2 g in glows)
                    this.Monitor.Log($"    glow at tile ({g.X / 64f:0.0},{g.Y / 64f:0.0})", LogLevel.Info);
            }
            this.Monitor.Log("note: shadow pass uses up to 6 on-screen lights; each casts one shadow per character.", LogLevel.Info);
        }

        /// <summary>Console command: dump the tile under the player — for diagnosing why a
        /// spot does or doesn't count as water for the mask.</summary>
        private void DumpTile()
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }
            var loc = Game1.currentLocation;
            var t = Game1.player.TilePoint;
            this.Monitor.Log($"=== Tile ({t.X},{t.Y}) in {loc?.NameOrUniqueName} ===", LogLevel.Info);
            if (loc == null) return;
            this.Monitor.Log($"isWaterTile={loc.isWaterTile(t.X, t.Y)}", LogLevel.Info);
            foreach (string prop in new[] { "Water", "WaterSource", "Passable", "Type" })
                foreach (string layer in new[] { "Back", "Buildings" })
                {
                    string? v = loc.doesTileHaveProperty(t.X, t.Y, prop, layer);
                    if (v != null)
                        this.Monitor.Log($"{layer}.{prop} = '{v}'", LogLevel.Info);
                }
            var hf = ShadowRenderer.Height;
            if (hf != null)
            {
                try { this.Monitor.Log($"HF surface={hf.GetSurfaceAt(loc, t.X, t.Y)} (0G 1W 2Wall 3Roof 4Deck 5Void) height={hf.GetHeightAt(loc, t.X, t.Y)}", LogLevel.Info); }
                catch { this.Monitor.Log("HF API threw", LogLevel.Info); }
            }
            foreach (string layerName in new[] { "Back", "Buildings", "Front", "AlwaysFront", "AlwaysFront2" })
            {
                var layer = loc.map?.GetLayer(layerName);
                var tile = layer?.Tiles[t.X, t.Y];
                if (tile == null)
                    continue;
                bool anim = tile is xTile.Tiles.AnimatedTile;
                // ImageSource (the asset path) is what the labeler keys on; Id is the map-local alias.
                this.Monitor.Log($"{layerName}: sheet={tile.TileSheet?.Id} src={tile.TileSheet?.ImageSource} index={tile.TileIndex} animated={anim}", LogLevel.Info);
            }

            // Palette of the Back art (top colours by count) — for tuning art classifiers.
            var back = loc.map?.GetLayer("Back");
            var bt = back?.Tiles[t.X, t.Y];
            if (bt is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                bt = at.TileFrames[0];
            if (bt?.TileSheet != null)
            {
                try
                {
                    var tex = Game1.content.Load<Texture2D>(bt.TileSheet.ImageSource);
                    var ib = bt.TileSheet.GetTileImageBounds(bt.TileIndex);
                    var buf = new Color[ib.Width * ib.Height];
                    tex.GetData(0, new Rectangle(ib.X, ib.Y, ib.Width, ib.Height), buf, 0, buf.Length);
                    var groups = new System.Collections.Generic.Dictionary<Color, int>();
                    foreach (Color c in buf)
                        groups[c] = groups.TryGetValue(c, out int cn) ? cn + 1 : 1;
                    this.Monitor.Log("Back art palette (top 10):", LogLevel.Info);
                    foreach (var kv in System.Linq.Enumerable.Take(
                        System.Linq.Enumerable.OrderByDescending(groups, g => g.Value), 10))
                        this.Monitor.Log($"    RGBA({kv.Key.R},{kv.Key.G},{kv.Key.B},{kv.Key.A}) x{kv.Value}", LogLevel.Info);
                }
                catch (Exception ex) { this.Monitor.Log("palette read failed: " + ex.Message, LogLevel.Info); }
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RegisterGmcm();

            // Optional Height Framework integration: robust per-tile water/deck/wall classification.
            // Null when that mod isn't installed — the shadow code falls back to its own heuristics.
            var height = this.Helper.ModRegistry.GetApi<Integrations.IHeightFrameworkApi>("phuicmt.HeightFramework");
            ShadowRenderer.Height = height;
            this.Monitor.Log(height != null
                ? "Height Framework detected — using it for water/ledge shadow suppression."
                : "Height Framework not installed — using built-in tile heuristics for shadows.", LogLevel.Info);
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
                _config.Clamp();
                ForceBufferDraw = EffectsActive;
                this.Helper.WriteConfig(_config);
            }

            api.Register(this.ModManifest, () =>
            {
                this.Monitor.Log("Config reset to defaults via GMCM.", LogLevel.Debug);
                _config = new ModConfig();
                ForceBufferDraw = EffectsActive;
            }, Save);

            // --- Landing page: master switch, a one-click look preset, and links to each
            // effect's own page so the top level stays short instead of one giant scroll. ---
            api.AddBoolOption(this.ModManifest, () => _config.Enabled, v => _config.Enabled = v,
                () => I18n("config.enabled.name"), () => I18n("config.enabled.tooltip"));

            api.AddTextOption(this.ModManifest,
                () => _config.ActivePreset.ToString(),
                v =>
                {
                    if (Enum.TryParse<LookPreset>(v, out var p))
                    {
                        // GMCM re-fires every option setter on save; only re-stamp the preset
                        // when the dropdown actually changed, or it silently overwrites the
                        // individual settings tuned on the other pages ("my settings reset").
                        bool changed = p != _config.ActivePreset;
                        _config.ActivePreset = p;
                        if (changed && p != LookPreset.Custom)
                        {
                            this.Monitor.Log($"Preset applied via GMCM: {p}", LogLevel.Debug);
                            _config.ApplyPreset(p);
                        }
                        ForceBufferDraw = EffectsActive;
                    }
                },
                () => I18n("config.preset.name"), () => I18n("config.preset.tooltip"),
                new[] { nameof(LookPreset.Custom), nameof(LookPreset.Subtle), nameof(LookPreset.Cinematic), nameof(LookPreset.Vibrant), nameof(LookPreset.Off) },
                v => I18n($"config.preset.{v.ToLowerInvariant()}"));

            api.AddParagraph(this.ModManifest, () => I18n("config.preset.hint"));

            // Same order as the F6 tuner: tone first, then light/shadow, then ambience, lens last.
            api.AddPageLink(this.ModManifest, "colorgrade", () => I18n("config.section.colorgrade"));
            api.AddPageLink(this.ModManifest, "bloom", () => I18n("config.section.bloom"));
            api.AddPageLink(this.ModManifest, "shadows", () => I18n("config.section.shadows"));
            api.AddPageLink(this.ModManifest, "lighting", () => I18n("config.section.lighting"));
            api.AddPageLink(this.ModManifest, "godrays", () => I18n("config.section.godrays"));
            api.AddPageLink(this.ModManifest, "cloudshadow", () => I18n("config.section.cloudshadow"));
            api.AddPageLink(this.ModManifest, "fog", () => I18n("config.section.fog"));
            api.AddPageLink(this.ModManifest, "tiltshift", () => I18n("config.section.tiltshift"));
            api.AddPageLink(this.ModManifest, "finishing", () => I18n("config.section.finishing"));
            api.AddPageLink(this.ModManifest, "camera", () => I18n("config.section.camera"));
            api.AddPageLink(this.ModManifest, "misc", () => I18n("config.section.misc"));

            // --- Bloom (implemented) ---
            api.AddPage(this.ModManifest, "bloom", () => I18n("config.section.bloom"));
            api.AddBoolOption(this.ModManifest, () => _config.BloomEnabled, v => _config.BloomEnabled = v,
                () => I18n("config.bloom.enabled.name"), () => I18n("config.bloom.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.BloomThreshold, v => _config.BloomThreshold = v,
                () => I18n("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.BloomIntensity, v => _config.BloomIntensity = v,
                () => I18n("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);

            // --- Color grading (implemented) ---
            api.AddPage(this.ModManifest, "colorgrade", () => I18n("config.section.colorgrade"));
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
            api.AddPage(this.ModManifest, "godrays", () => I18n("config.section.godrays"));
            api.AddBoolOption(this.ModManifest, () => _config.GodRaysEnabled, v => _config.GodRaysEnabled = v,
                () => I18n("config.godrays.enabled.name"), () => I18n("config.godrays.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.GodRaysIntensity, v => _config.GodRaysIntensity = v,
                () => I18n("config.godrays.intensity.name"), null, 0f, 1.5f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.GodRaysThreshold, v => _config.GodRaysThreshold = v,
                () => I18n("config.godrays.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.GodRaysDensity, v => _config.GodRaysDensity = v,
                () => I18n("config.godrays.density.name"), null, 0.1f, 1f, 0.05f);

            // --- Volumetric fog (implemented) ---
            api.AddPage(this.ModManifest, "fog", () => I18n("config.section.fog"));
            api.AddSectionTitle(this.ModManifest, () => I18n("config.fog.sectionday"));
            api.AddBoolOption(this.ModManifest, () => _config.FogEnabled, v => _config.FogEnabled = v,
                () => I18n("config.fog.enabled.name"), () => I18n("config.fog.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.FogCoverage, v => _config.FogCoverage = v,
                () => I18n("config.fog.coverage.name"), () => I18n("config.fog.coverage.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.FogDensity, v => _config.FogDensity = v,
                () => I18n("config.fog.density.name"), () => I18n("config.fog.density.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.FogScale, v => _config.FogScale = v,
                () => I18n("config.fog.scale.name"), null, 1f, 8f, 0.5f);
            api.AddNumberOption(this.ModManifest, () => _config.FogSpeed, v => _config.FogSpeed = v,
                () => I18n("config.fog.speed.name"), null, 0f, 0.1f, 0.005f);
            api.AddSectionTitle(this.ModManifest, () => I18n("config.fog.sectionnight"));
            api.AddBoolOption(this.ModManifest, () => _config.FogNightMist, v => _config.FogNightMist = v,
                () => I18n("config.fog.nightmist.name"), () => I18n("config.fog.nightmist.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.FogNightMistCoverage, v => _config.FogNightMistCoverage = v,
                () => I18n("config.fog.nightmistcoverage.name"), () => I18n("config.fog.coverage.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.FogNightMistDensity, v => _config.FogNightMistDensity = v,
                () => I18n("config.fog.nightmistdensity.name"), () => I18n("config.fog.density.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.FogNightMistSpeed, v => _config.FogNightMistSpeed = v,
                () => I18n("config.fog.nightmistspeed.name"), null, 0f, 0.1f, 0.002f);

            // --- Cloud shadows (implemented) ---
            api.AddPage(this.ModManifest, "cloudshadow", () => I18n("config.section.cloudshadow"));
            api.AddBoolOption(this.ModManifest, () => _config.SuppressVanillaCloudShadow, v => _config.SuppressVanillaCloudShadow = v,
                () => I18n("config.cloudshadow.hidevanilla.name"), () => I18n("config.cloudshadow.hidevanilla.tooltip"));
            api.AddBoolOption(this.ModManifest, () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v,
                () => I18n("config.cloudshadow.enabled.name"), () => I18n("config.cloudshadow.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowCoverage, v => _config.CloudShadowCoverage = v,
                () => I18n("config.cloudshadow.coverage.name"), null, 0.1f, 0.9f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowCount, v => _config.CloudShadowCount = v,
                () => I18n("config.cloudshadow.count.name"), () => I18n("config.cloudshadow.count.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowOpacity, v => _config.CloudShadowOpacity = v,
                () => I18n("config.cloudshadow.opacity.name"), null, 0f, 0.7f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowScale, v => _config.CloudShadowScale = v,
                () => I18n("config.cloudshadow.scale.name"), null, 1f, 5f, 0.5f);
            api.AddNumberOption(this.ModManifest, () => _config.CloudShadowSpeed, v => _config.CloudShadowSpeed = v,
                () => I18n("config.cloudshadow.speed.name"), null, 0f, 0.1f, 0.005f);

            // --- Tilt-shift (implemented) ---
            api.AddPage(this.ModManifest, "tiltshift", () => I18n("config.section.tiltshift"));
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
            api.AddPage(this.ModManifest, "finishing", () => I18n("config.section.finishing"));
            api.AddBoolOption(this.ModManifest, () => _config.WaterEnabled, v => _config.WaterEnabled = v,
                () => I18n("config.water.enabled.name"), () => I18n("config.water.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.WaterStrength, v => _config.WaterStrength = v,
                () => I18n("config.water.strength.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.WaterSpeed, v => _config.WaterSpeed = v,
                () => I18n("config.water.speed.name"), null, 0f, 3f, 0.1f);
            api.AddNumberOption(this.ModManifest, () => _config.WaterSparkle, v => _config.WaterSparkle = v,
                () => I18n("config.water.sparkle.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.WaterSparkleDensity, v => _config.WaterSparkleDensity = v,
                () => I18n("config.water.sparkledensity.name"), () => I18n("config.water.sparkledensity.tooltip"), 0.2f, 2f, 0.05f);
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
            api.AddPage(this.ModManifest, "lighting", () => I18n("config.section.lighting"));
            api.AddBoolOption(this.ModManifest, () => _config.FloodLightingEnabled, v => _config.FloodLightingEnabled = v,
                () => I18n("config.lighting.flood.name"), () => I18n("config.lighting.flood.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.FloodLightingStrength, v => _config.FloodLightingStrength = v,
                () => I18n("config.lighting.floodstrength.name"), () => I18n("config.lighting.floodstrength.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.FloodShadowStrength, v => _config.FloodShadowStrength = v,
                () => I18n("config.lighting.floodshadow.name"), () => I18n("config.lighting.floodshadow.tooltip"), 0f, 1f, 0.05f);
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

            // --- Directional sprite shadows ---
            api.AddPage(this.ModManifest, "shadows", () => I18n("config.section.shadows"));
            api.AddBoolOption(this.ModManifest, () => _config.DirectionalShadowsEnabled, v => _config.DirectionalShadowsEnabled = v,
                () => I18n("config.shadows.enabled.name"), () => I18n("config.shadows.enabled.tooltip"));
            api.AddNumberOption(this.ModManifest, () => _config.DirectionalShadowStrength, v => _config.DirectionalShadowStrength = v,
                () => I18n("config.shadows.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.DirectionalShadowLength, v => _config.DirectionalShadowLength = v,
                () => I18n("config.shadows.length.name"), null, 0.2f, 2f, 0.05f);
            api.AddNumberOption(this.ModManifest, () => _config.DirectionalShadowBlur, v => _config.DirectionalShadowBlur = v,
                () => I18n("config.shadows.blur.name"), null, 0f, 5f, 0.5f);
            api.AddBoolOption(this.ModManifest, () => _config.DirectionalShadowObjects, v => _config.DirectionalShadowObjects = v,
                () => I18n("config.shadows.objects.name"), () => I18n("config.shadows.objects.tooltip"));

            // --- Camera (implemented) ---
            api.AddPage(this.ModManifest, "camera", () => I18n("config.section.camera"));
            api.AddTextOption(this.ModManifest,
                () => _config.CameraMode.ToString(),
                v => _config.CameraMode = Enum.TryParse<CameraMode>(v, out var m) ? m : CameraMode.Off,
                () => I18n("config.camera.mode.name"), () => I18n("config.camera.mode.tooltip"),
                new[] { nameof(CameraMode.Off), nameof(CameraMode.Smooth) },
                v => I18n($"config.camera.mode.{v.ToLowerInvariant()}"));
            api.AddNumberOption(this.ModManifest, () => _config.CameraFollowSpeed, v => _config.CameraFollowSpeed = v,
                () => I18n("config.smoothcam.speed.name"), () => I18n("config.smoothcam.speed.tooltip"), 0.05f, 1f, 0.05f);

            // --- Misc page: hotkeys + diagnostics + roadmap ---
            api.AddPage(this.ModManifest, "misc", () => I18n("config.section.misc"));
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
