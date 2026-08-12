using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The radiance_* console commands: author/diagnostic tools only — none of them run
    /// unless typed into the SMAPI console. Registered once from ModEntry.Entry().
    /// </summary>
    internal static class ConsoleCommands
    {
        /// <param name="getConfig">Live config accessor (the instance is replaced on GMCM reset).</param>
        /// <param name="getPipeline">Live pipeline accessor (null until the first frame needs it).</param>
        /// <summary>Kept from registration so the tile report can say what else is installed.
        /// A report that arrives without it costs a round trip roughly every other time.</summary>
        private static IModRegistry? _registry;

        internal static void RegisterAll(IModHelper helper, IMonitor monitor,
            Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline)
        {
            _registry = helper.ModRegistry;
            // Author tool: dumps every location's layers/tiles + sheet art for HF Studio, the
            // browser labeler that produces labels/water-labels.json. Harmless for players (it
            // only runs when typed) and it keeps the whole labeling loop inside this mod.
            helper.ConsoleCommands.Add("radiance_mapdump",
                "Dump every location's layer/tile layout + sheet art to Documents\\HF-Studio\\maps.json for the label editor. "
                + "Add 'all' to also embed tilesheets that no loaded map places: the water-heavy and bridge-heavy art ships "
                + "as bare resource packs with no maps of their own, so walking the maps can never find it.",
                (_, args) => { MapDump.Run(monitor, helper, allSheets: args.Length >= 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase)); });
            helper.ConsoleCommands.Add("radiance_lights",
                "List every active light source in the current location (id, kind, tile, radius, color, distance from player).",
                (_, _) => DumpLights(monitor));
            // ONE COMMAND, NO ARGUMENTS. Everything below this line is a tool for someone who
            // already knows what it does. A player who has just seen something wrong should not
            // have to pick a command, read coordinates off the screen and type them correctly
            // before they can help: they will do none of that, and the report arrives with a
            // screenshot and nothing else. This collects every diagnostic worth having, writes it
            // to a file in Documents, and prints the path so it can be dragged into the report.
            helper.ConsoleCommands.Add("radiance_report",
                "Write everything needed to diagnose what you are looking at to a file you can attach to a bug "
                + "report: versions, scene, your settings, the tile you are standing on and the ones around it, "
                + "the label check for the screen, and the installed mods that could be involved. No arguments, "
                + "just stand where the problem is and run it.",
                (_, _) => WriteReport(helper, monitor, getPipeline(), getConfig()));
            helper.ConsoleCommands.Add("radiance_tile",
                "Dump water-related data for the tile under the player, or 'radiance_tile x y' for any tile (layer properties, HF class, isWaterTile, compose flags).",
                (_, args) => DumpTile(s => monitor.Log(s, LogLevel.Info), getPipeline(), getConfig(), args));
            helper.ConsoleCommands.Add("radiance_screenwatch",
                "Trace the per-screen render pass for the next N calls (default 60). Prints which screen asked, "
                + "whether effects were active for it, and the caches that are keyed to where the camera is. "
                + "For split screen: if the two screens' origins keep swapping and a mask rebuild is always in "
                + "flight, that is one pipeline being pulled between two cameras.",
                (_, args) =>
                {
                    int frames = args.Length >= 1 && int.TryParse(args[0], out int f) ? Math.Clamp(f, 1, 600) : 60;
                    ModEntry.ScreenWatchFrames = frames;
                    monitor.Log($"Watching the render pass for {frames} calls (split screen spends two per frame).", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_lightwatch",
                "Trace the light array for the next N frames (default 60) and print only what changes: "
                + "how many lights were offered versus how many slots exist, which ones entered or left, "
                + "and any whose brightness moved. Stand still where it flickers and run it.",
                (_, args) =>
                {
                    int frames = args.Length >= 1 && int.TryParse(args[0], out int f) ? Math.Clamp(f, 1, 600) : 60;
                    RenderPipeline.LightWatchFrames = frames;
                    monitor.Log($"Watching the light array for {frames} frames.", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_waterwatch",
                "Trace the water surface for the next N frames (default 120) and say what changed each frame: "
                + "when the mask was rebuilt, when the window moved, and when the shoreline the reflection is "
                + "built against switched between the map's own and a window-local guess. If the water flashes "
                + "while you walk, walk past it with this running and the flash will have a line next to it.",
                (_, args) =>
                {
                    int frames = args.Length >= 1 && int.TryParse(args[0], out int f) ? Math.Clamp(f, 1, 600) : 120;
                    RenderPipeline.WaterWatchFrames = frames;
                    monitor.Log($"Watching the water for {frames} frames. Walk past the water now.", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_flood",
                "Flood-GI rebuild A/B for chasing a flicker. 'radiance_flood freeze' holds the current bounce "
                + "grid still (anything that still moves is NOT the flood), 'every' rebuilds it every frame "
                + "(anything that stops moving WAS the rebuild rate), 'auto' restores normal behaviour. "
                + "Not saved, resets when the game restarts.",
                (_, args) =>
                {
                    if (args.Length >= 1)
                        FloodLightmap.RebuildMode = args[0].ToLowerInvariant() switch
                        {
                            "freeze" or "hold" => FloodLightmap.RebuildOverride.Freeze,
                            "every" or "always" => FloodLightmap.RebuildOverride.Every,
                            _ => FloodLightmap.RebuildOverride.Auto,
                        };
                    monitor.Log($"Flood rebuild: {FloodLightmap.RebuildMode}", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_shadowcasts",
                "How many shadows one character may cast, nearest light first (default 3). "
                + "'radiance_shadowcasts 1' for a single clean shadow, higher for a room lit from several "
                + "sides. No argument prints the current value. A look setting - watch the shadow line in "
                + "radiance_report for what each step costs.",
                (_, args) =>
                {
                    if (args.Length >= 1 && int.TryParse(args[0], out int casts))
                    {
                        getConfig().ShadowCastsPerCharacter = casts;
                        getConfig().Clamp();
                    }
                    monitor.Log($"Shadow casts per character: {getConfig().ShadowCastsPerCharacter} (nearest lights first; "
                        + "same setting as the tuner's Shadows tab, but not saved from here)", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_march",
                "List on-screen tiles whose water has effect but no march (ripple without reflection - the orange tiles in the radiance_debug water overlay), worst first.",
                (_, _) => monitor.Log(getPipeline()?.DescribeEffectOnlyTiles() ?? "pipeline not ready", LogLevel.Info));
            helper.ConsoleCommands.Add("radiance_maskdump",
                "Save the water mask textures to PNG in the temp folder (debug).",
                (_, _) => monitor.Log(getPipeline()?.DumpMasks(System.IO.Path.GetTempPath()) ?? "pipeline not ready", LogLevel.Info));
            helper.ConsoleCommands.Add("radiance_maskview",
                "Toggle the live water-mask overlay (cyan = full effect, orange = effect-only art water, green rim = reflection shoreline). Same as: radiance_debug water",
                (_, _) =>
                {
                    RenderPipeline.MaskView = !RenderPipeline.MaskView;
                    RenderPipeline.DebugChannel = RenderPipeline.MaskView ? DebugOverlayChannel.Water : DebugOverlayChannel.Off;
                    monitor.Log($"Water mask overlay: {(RenderPipeline.MaskView ? "ON" : "OFF")} (rebuilds on next tile crossing / within 10s)", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_debug",
                "Show one internal buffer over the world. Channels: off | water | labeldiff | sdf | subtype | sprite | reflect | mirror | emitter. "
                + "emitter paints the lighting pass's answer to 'which pixels ARE a light': RED = treated as the light "
                + "itself and spared the room's dimming, GREEN = close enough to a light but not bright enough in the art to count. "
                + "labeldiff paints the radiance_verify verdict: RED = label says liquid but the mask has none, YELLOW = the mask ripples where the label says solid.",
                (_, args) =>
                {
                    if (args.Length < 1 || !Enum.TryParse(args[0], ignoreCase: true, out DebugOverlayChannel channel))
                    {
                        monitor.Log("usage: radiance_debug off|water|labeldiff|sdf|subtype|sprite|reflect|mirror|emitter "
                            + $"(now: {RenderPipeline.DebugChannel})", LogLevel.Info);
                        return;
                    }
                    RenderPipeline.DebugChannel = channel;
                    RenderPipeline.MaskView = channel == DebugOverlayChannel.Water;   // keeps the recolor rebuilding
                    if (channel == DebugOverlayChannel.LabelDiff)
                        monitor.Log(getPipeline()?.VerifyLabels(Game1.currentLocation) ?? "pipeline not ready", LogLevel.Info);
                    monitor.Log($"debug overlay: {channel}", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_verify",
                "Label-vs-mask acceptance test: compares the hand-painted labels with the composed water mask for every "
                + "labeled tile on screen, pixel for pixel. Reports accuracy, missing/false water, and the worst tiles. "
                + "Pair with 'radiance_debug labeldiff' to SEE the disagreements in the world.",
                (_, _) => monitor.Log(getPipeline()?.VerifyLabels(Game1.currentLocation) ?? "pipeline not ready", LogLevel.Info));
            helper.ConsoleCommands.Add("radiance_shadows",
                "Report every character, object, plant, animal and critter that could cast, and what the shadow "
                + "pass does with each one, plus the event flags that decide who the game is drawing. Reaches 20 "
                + "tiles past the screen ('*' marks anything off screen); 'radiance_shadows all' scans the whole "
                + "map. Use it when something has no shadow, or has one with nothing above it.",
                (_, args) => monitor.Log(
                    ShadowRenderer.Report(getConfig(), args.Length >= 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase)),
                    LogLevel.Info));
            helper.ConsoleCommands.Add("radiance_dump",
                "Capture every buffer this frame (composed frame, water masks, occluders, lightmap, reflection) to "
                + "Documents\\Radiance-Dumps\\<name>\\ for offline comparison. Usage: radiance_dump <name>. "
                + "Run radiance_freeze first or the capture cannot be compared with another run.",
                (_, args) =>
                {
                    string name = args.Length >= 1 ? args[0] : "capture";
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                        name = name.Replace(c, '_');
                    RenderPipeline.RequestDump(name);
                    monitor.Log($"radiance_dump: capturing '{name}' on the next rendered frame"
                        + (Determinism.Frozen ? " (clock frozen)" : " — WARNING: clock is running, run radiance_freeze first"),
                        Determinism.Frozen ? LogLevel.Info : LogLevel.Warn);
                });
            helper.ConsoleCommands.Add("radiance_freeze",
                "Pin the render clock and every eased amount so the same spot renders the same bytes twice "
                + "(what a before/after capture needs). No args toggles; 'on'/'off' set it. Game logic is untouched — "
                + "characters keep walking, so stand still and let the scene settle before capturing.",
                (_, args) =>
                {
                    bool on = args.Length >= 1
                        ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                        : !Determinism.Frozen;
                    if (on)
                        monitor.Log($"Render clock FROZEN at tick {Determinism.Freeze()}: animation, presence fades and auto-exposure are pinned.", LogLevel.Info);
                    else
                    {
                        Determinism.Thaw();
                        monitor.Log("Render clock running again.", LogLevel.Info);
                    }
                });
            helper.ConsoleCommands.Add("radiance_reflect",
                "Reflection diagnostics/A-B. No args = report what each reflection layer is doing under the player. "
                + "'scene on|off' forces the sprite-free scenery mirror source (P3c) on or off, so a missing "
                + "bridge/cliff reflection can be pinned on it in one keystroke.",
                (_, args) =>
                {
                    if (args.Length >= 2 && args[0].Equals("scene", StringComparison.OrdinalIgnoreCase))
                    {
                        RenderPipeline.SceneSourceOff = args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                        monitor.Log($"Scenery mirror source (P3c): {(RenderPipeline.SceneSourceOff ? "FORCED OFF (mirror reads the composed screen)" : "ON")}", LogLevel.Info);
                        return;
                    }
                    monitor.Log(getPipeline()?.ReflectionDiag() ?? "pipeline not ready", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_bench",
                "Measure what this mod costs on THIS machine and suggest an effect resolution. Sweeps the "
                + "settings for about ten seconds (the picture flickers while it does), then prints the cost of "
                + "each and a recommendation. Stand somewhere demanding - an outdoor map with water - for the "
                + "worst case, since it can only measure the scene you are in.",
                (_, _) =>
                {
                    if (!StardewModdingAPI.Context.IsWorldReady)
                    {
                        monitor.Log("Load a save first — there is nothing being drawn to measure.", LogLevel.Info);
                        return;
                    }
                    var pipeline = getPipeline();
                    if (pipeline == null) monitor.Log("Pipeline not ready.", LogLevel.Info);
                    else pipeline.StartBenchmark(getConfig());
                });
            helper.ConsoleCommands.Add("radiance_gpu",
                "Measure real GPU time per frame (needs Debug logging on). Every other timer here counts CPU "
                + "submission, which the driver returns from before the GPU has done anything; this one blocks on "
                + "the finished frame, so it can answer what a setting actually costs. It STALLS the pipeline every "
                + "frame, so it is a measuring tool, not something to leave running.",
                (_, args) =>
                {
                    RenderPipeline.GpuProbe = args.Length == 0 || !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
                    monitor.Log($"GPU wall-clock probe: {(RenderPipeline.GpuProbe ? "ON - watch for [perf] gpu wall-clock lines" : "off")}", LogLevel.Info);
                });
        }

        /// <summary>Console command: dump every light the game currently tracks, so "why does my
        /// room have N shadows" is answerable — each listed light casts its own shadow.</summary>
        private static void DumpLights(IMonitor monitor)
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }
            var lights = Game1.currentLightSources;
            var location = Game1.currentLocation;
            monitor.Log($"=== Lights in {location?.NameOrUniqueName} ({(lights?.Count ?? 0)} total) ===", LogLevel.Info);
            if (lights == null || lights.Count == 0)
                return;
            Vector2 playerFeetPosition = Game1.player.Position;
            int i = 0;
            foreach (var kv in lights)
            {
                var ls = kv.Value;
                Vector2 tile = ls.position.Value / 64f;
                float distTiles = Vector2.Distance(ls.position.Value, playerFeetPosition) / 64f;
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                bool onScreen = screen.X > -640 && screen.X < Game1.viewport.Width + 640
                             && screen.Y > -640 && screen.Y < Game1.viewport.Height + 640;
                var c = ls.color.Value;
                monitor.Log(
                    $"[{i++}] id={kv.Key} ctx={ls.lightContext.Value} texture={ls.textureIndex.Value} " +
                    $"tile=({tile.X:0.0},{tile.Y:0.0}) radius={ls.radius.Value:0.00} " +
                    $"color(raw/subtractive)=({c.R},{c.G},{c.B},{c.A}) dist={distTiles:0.0} tiles " +
                    $"onScreen={onScreen}", LogLevel.Info);
            }
            if (location != null)
            {
                var glows = location.lightGlows;
                monitor.Log($"--- lightGlows ({glows.Count}) — a WindowLight with no glow nearby is stale and won't cast ---", LogLevel.Info);
                foreach (Vector2 g in glows)
                    monitor.Log($"    glow at tile ({g.X / 64f:0.0},{g.Y / 64f:0.0})", LogLevel.Info);
            }
            monitor.Log("note: shadow pass uses up to 6 on-screen lights; each casts one shadow per character.", LogLevel.Info);
        }

        /// <summary>Console command: dump the tile under the player — or any tile given as
        /// "radiance_tile x y" (water tiles can't be stood on) — for diagnosing why a
        /// spot does or doesn't count as water for the mask.</summary>
        /// <summary>
        /// The whole diagnosis in one file. Everything a report needs, gathered without asking the
        /// reporter a single question, written somewhere they can find it and attach it.
        /// </summary>
        private static void WriteReport(IModHelper helper, IMonitor monitor, RenderPipeline? pipeline, ModConfig config)
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                monitor.Log("Load a save first, then stand where the problem is and run this again.", LogLevel.Warn);
                return;
            }
            var text = new System.Text.StringBuilder();
            void Write(string line) => text.AppendLine(line);
            try
            {
                Write("SDV-Radiance report. Attach this whole file to your bug report.");
                Write("Nothing here is personal: it is versions, where you are standing, your graphics");
                Write("settings, and the names of the mods you have installed.");
                Write("");
                Write($"display: window {Game1.graphics?.GraphicsDevice?.PresentationParameters?.BackBufferWidth}"
                    + $"x{Game1.graphics?.GraphicsDevice?.PresentationParameters?.BackBufferHeight}"
                    + $", zoom {Game1.options?.zoomLevel:0.00}, ui scale {Game1.options?.uiScale:0.00}"
                    + $", fullscreen={Game1.options?.fullscreen}");
                Write($"gpu: {Game1.graphics?.GraphicsDevice?.Adapter?.Description}");
                Write("");
                DumpTile(Write, pipeline, config, args: null, includePalette: false);
                Write("");
                Write("=== what the effect chain is actually doing this frame ===");
                Write(pipeline?.DescribeStageState() ?? "pipeline not ready");
                Write("");
                Write("=== what this mod costs per frame ===");
                Write(FrameCost.Describe());
                Write("");
                Write("=== label check for everything on screen ===");
                Write(pipeline?.VerifyLabels(Game1.currentLocation) ?? "pipeline not ready");
                Write("");
                Write("=== how the water surface has been behaving, and what just happened to it ===");
                Write(pipeline?.DescribeWaterHistory() ?? "pipeline not ready");
                Write("");
                Write("=== water with ripple but no reflection, worst first ===");
                Write(pipeline?.DescribeEffectOnlyTiles() ?? "pipeline not ready");
                Write("");
                Write("=== shadows: who could cast, and what the pass did with them ===");
                Write(ShadowRenderer.Report(config, wholeMap: false));
                Write("");
                // The whole config, not the handful printed above. It is a file, it costs nothing,
                // and a setting nobody thought to ask about is exactly the one that explains it.
                Write("=== every setting, verbatim ===");
                try
                {
                    var json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    Write(System.Text.Json.JsonSerializer.Serialize(config, json));
                }
                catch (Exception ex) { Write("could not serialise the config: " + ex.Message); }

                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Radiance-Dumps");
                System.IO.Directory.CreateDirectory(dir);
                // One fixed name rather than a timestamp: the reporter has to FIND this file, and
                // a folder with nine near-identical names is worse than one that is always current.
                string path = System.IO.Path.Combine(dir, "radiance-report.txt");
                System.IO.File.WriteAllText(path, text.ToString());
                monitor.Log($"Report written to: {path}", LogLevel.Alert);
                monitor.Log("Attach that file to your bug report. It already carries your version, location, "
                          + "time, weather, settings and mod list, so there is nothing else to type.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                // Falling back to the console still gets the reporter something to copy.
                monitor.Log("Could not write the report file: " + ex.Message, LogLevel.Warn);
                monitor.Log(text.ToString(), LogLevel.Info);
            }
        }

        /// <summary>
        /// Say which mod an asset path belongs to, where the path is honest enough to say.
        ///
        /// <para>An asset loaded BY a mod carries its unique id in the path, so that case is exact.
        /// An asset a mod EDITED does not: Content Patcher paints over Maps/spring_town in place and
        /// the path still reads Maps/spring_town. So this reports what it knows and says plainly
        /// when it cannot tell, rather than letting "vanilla path" be read as "vanilla art".</para>
        /// </summary>
        private static string AttributeAsset(string? assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "no asset";
            string normalised = assetPath.Replace('\\', '/');
            if (_registry != null)
                foreach (IModInfo info in _registry.GetAll())
                {
                    string id = info.Manifest?.UniqueID ?? "";
                    if (id.Length > 0 && normalised.Contains(id, StringComparison.OrdinalIgnoreCase))
                        return "loaded by: " + (info.Manifest?.Name ?? id);
                }
            if (normalised.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase))
                return "loaded by a mod, id not matched: " + normalised.Split('/')[1];
            return "vanilla asset path, but a content pack may have repainted it in place";
        }

        /// <summary>
        /// Name the installed mods that can plausibly explain what the reporter is looking at.
        ///
        /// <para>Deliberately NOT the whole list. A modded install is eighty entries long, nobody
        /// pastes that, and the SMAPI log already carries it in full. What is missing from a bug
        /// report is the signal: one mod that is known to break this one, another post-processing
        /// layer fighting for the same frame, or the recolour and map packs that explain why the
        /// screenshot does not look like anyone else's game. Those, and a count for context.</para>
        /// </summary>
        private static void ReportRelevantMods(Action<string> write)
        {
            if (_registry == null)
                return;
            // Anything whose art or frame handling can change what a screenshot shows. Matched on
            // the manifest name because content packs are named far more consistently than they
            // are identified: there is no id convention for "this is a recolour".
            (string Pattern, string Why)[] flags =
            {
                // Two separate faults, both measured on this machine: the world rendering solid
                // orange or black on load, and water losing its reflection in patches that shift
                // as you walk. The second one was chased for hours as a water bug before anyone
                // switched SpriteMaster off, so it is worth naming here rather than in a footnote.
                ("spritemaster",  "INCOMPATIBLE: orange/black world on load, and patches of water lose their reflection"),
                ("clearglasses",  "INCOMPATIBLE (SpriteMaster): orange/black world on load, and patches of water lose their reflection"),
                // Clear Monocle is NOT SpriteMaster and has not been incompatible since its author
                // shipped explicit support on 2026-07-28, confirmed by two users. This line used to
                // say otherwise and told everyone running a current copy to remove a mod that works.
                ("clear monocle", "was incompatible before its 2026-07-28 update; current versions are fine"),
                ("reshade",       "another post-processing layer"),
                ("dynamic shader", "another post-processing layer"),
                ("shader",        "another post-processing layer"),
                ("lighting",      "touches light"),
                ("god ray",       "touches light"),
                ("recolo",        "changes world art"),
                ("retextur",      "changes world art"),
                ("tilesheet",     "changes world art"),
                ("water",         "changes water art"),
                ("expanded",      "changes maps"),
                ("overhaul",      "changes maps"),
                ("farm type",     "can replace the farm map"),
                ("farm map",      "can replace the farm map"),
                ("farm cave",     "can replace the farm map"),
            };
            var hits = new System.Collections.Generic.List<string>();
            int total = 0;
            foreach (IModInfo info in _registry.GetAll())
            {
                total++;
                string name = info.Manifest?.Name ?? "";
                string id = info.Manifest?.UniqueID ?? "";
                if (id.StartsWith("PHUICMT", StringComparison.OrdinalIgnoreCase) || name.Contains("Radiance", StringComparison.OrdinalIgnoreCase))
                    continue;
                string haystack = name + " " + id;
                foreach ((string pattern, string why) in flags)
                    if (haystack.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        string line = $"{name} [{why}]";
                        if (!hits.Contains(line))
                            hits.Add(line);
                        break;
                    }
            }
            write($"mods: {total} loaded. Possibly relevant to what you are seeing:");
            if (hits.Count == 0)
            {
                write("    none matched (a vanilla-looking install)");
                return;
            }
            // Capped, but not tightly: a SMAPI mod and its content pack are separate entries with
            // near-identical names, so a real install spends several slots saying the same thing
            // twice. Twelve truncated a genuine list; this is a file, and twenty costs nothing.
            foreach (string hit in hits.GetRange(0, Math.Min(hits.Count, 20)))
                write("    " + hit);
            if (hits.Count > 20)
                write($"    ... and {hits.Count - 20} more (full list is in the SMAPI log)");
        }

        private static void DumpTile(Action<string> write, RenderPipeline? pipeline, ModConfig config, string[]? args = null, bool includePalette = true)
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                write("Load a save first.");
                return;
            }
            var location = Game1.currentLocation;
            var t = Game1.player.TilePoint;
            if (args is { Length: >= 2 } && int.TryParse(args[0], out int ax) && int.TryParse(args[1], out int ay))
                t = new Point(ax, ay);
            write($"=== Tile ({t.X},{t.Y}) in {location?.NameOrUniqueName} ===");
            if (location == null) return;
            // HEADER FIRST, and complete. This output gets pasted into a bug report by someone who
            // will not be asked a second question, so everything needed to reproduce the scene has
            // to be in the paste: which build, which scene, and which of our switches were on.
            // Chasing "which map is this, what season, was reflection even enabled" through a
            // comment thread costs days per report.
            string weather = Game1.isLightning ? "storm" : Game1.isRaining ? "rain"
                : Game1.isSnowing ? "snow" : Game1.isDebrisWeather ? "windy" : "sun";
            write($"mod v{ModEntry.SVersion} | game {Game1.version} | {Game1.season} {Game1.dayOfMonth}, "
                      + $"{Game1.getTimeOfDayString(Game1.timeOfDay)}, {weather} | indoors={!location.IsOutdoors}");
            write($"config: enabled={config.Enabled} water={config.WaterEnabled} reflection={config.WaterReflection} "
                      + $"lighting={config.FloodLightingEnabled} shadows={config.DirectionalShadowsEnabled} "
                      + $"renderScale={config.RenderScale:0.00} labels=v{LabelStore.Instance?.Version ?? 0}");
            // WHERE THIS PLACE CAME FROM. "Which map or mod is that bridge from" is the question
            // every water report needs answered and no reporter can answer, because from inside the
            // game a modded map looks exactly like a vanilla one.
            string mapAsset = location.mapPath?.Value ?? "unknown";
            write($"map asset: {mapAsset}  [{AttributeAsset(mapAsset)}]");
            write($"location type: {location.GetType().Name} | context: {location.GetLocationContextId()}"
                + $" | size {location.Map?.Layers[0].LayerWidth}x{location.Map?.Layers[0].LayerHeight}"
                + $" | tilesheets: {location.Map?.TileSheets.Count}");
            ReportRelevantMods(write);
            write($"isWaterTile={location.isWaterTile(t.X, t.Y)} drawnWater={WaterDrawHook.WasDrawn(location, t.X, t.Y)} (hook v{WaterDrawHook.Version})");
            foreach (string prop in new[] { "Water", "WaterSource", "Passable", "Type" })
                foreach (string layer in new[] { "Back", "Buildings" })
                {
                    string? v = location.doesTileHaveProperty(t.X, t.Y, prop, layer);
                    if (v != null)
                        write($"{layer}.{prop} = '{v}'");
                }
            var surf = SurfaceMap.For(location);
            if (surf != null)
                write($"surface={surf.GetSurface(t.X, t.Y)} height={surf.GetHeight(t.X, t.Y)}");
            // Composed mask vs label, side by side — the acceptance test for this subsystem
            // is that the game matches the labeler, so print both from the same tile.
            write(pipeline?.DescribeTileMask(location, t.X, t.Y) ?? "pipeline not ready");
            foreach (string layerName in new[] { "Back", "Buildings", "Front", "AlwaysFront", "AlwaysFront2" })
            {
                var layer = location.map?.GetLayer(layerName);
                var tile = layer?.Tiles[t.X, t.Y];
                if (tile == null)
                    continue;
                bool anim = tile is xTile.Tiles.AnimatedTile;
                // ImageSource (the asset path) is what the labeler keys on; Id is the map-local alias.
                write($"{layerName}: sheet={tile.TileSheet?.Id} src={tile.TileSheet?.ImageSource} index={tile.TileIndex} animated={anim}"
                    + $"  [{AttributeAsset(tile.TileSheet?.ImageSource)}]");
            }

            // THE NEIGHBOURHOOD, not just the tile. Almost every water report is about a SHAPE:
            // a bridge that reads as a hole, a shoreline that stops early, a pier with a square
            // around it. One tile cannot show a shape, and asking the reporter to run the command
            // nine more times never works. Print the block around it as a picture instead.
            write("neighbourhood (W=water, D=deck/bridge, #=wall, G=glass, ^=roof, o=void, .=ground):");
            {
                var header = new System.Text.StringBuilder("        ");
                for (int x = t.X - 4; x <= t.X + 4; x++)
                    header.Append(x.ToString().PadLeft(5));
                write(header.ToString());
                for (int y = t.Y - 3; y <= t.Y + 3; y++)
                {
                    var row = new System.Text.StringBuilder($"y={y,-6}");
                    for (int x = t.X - 4; x <= t.X + 4; x++)
                    {
                        char c;
                        try
                        {
                            c = surf?.GetSurface(x, y) switch
                            {
                                SurfaceClass.Water => 'W',
                                SurfaceClass.Deck => 'D',
                                SurfaceClass.Wall => '#',
                                SurfaceClass.Glass => 'G',
                                SurfaceClass.Roof => '^',
                                SurfaceClass.Void => 'o',
                                SurfaceClass.Ground => location.isWaterTile(x, y) ? 'w' : '.',
                                _ => '?',
                            };
                        }
                        catch { c = '?'; }
                        // The tile actually asked about is marked so it can be found in the paste.
                        string cell = x == t.X && y == t.Y ? $"[{c}]" : c.ToString();
                        row.Append(cell.PadLeft(5));
                    }
                    write(row.ToString());
                }
                write("(lowercase w = the game calls it water but our surface pass does not, "
                          + "which is the usual signature of art drawn over water)");
            }

            // Palette of the Back art (top colours by count) — for tuning art classifiers.
            if (!includePalette)
                return;
            var back = location.map?.GetLayer("Back");
            var bt = back?.Tiles[t.X, t.Y];
            if (bt is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                bt = at.TileFrames[0];
            if (bt?.TileSheet != null)
            {
                try
                {
                    var texture = Game1.content.Load<Texture2D>(bt.TileSheet.ImageSource);
                    var ib = bt.TileSheet.GetTileImageBounds(bt.TileIndex);
                    var buf = new Color[ib.Width * ib.Height];
                    texture.GetData(0, new Rectangle(ib.X, ib.Y, ib.Width, ib.Height), buf, 0, buf.Length);
                    var groups = new System.Collections.Generic.Dictionary<Color, int>();
                    foreach (Color c in buf)
                        groups[c] = groups.TryGetValue(c, out int cn) ? cn + 1 : 1;
                    write("Back art palette (top 10):");
                    foreach (var kv in System.Linq.Enumerable.Take(
                        System.Linq.Enumerable.OrderByDescending(groups, g => g.Value), 10))
                        write($"    RGBA({kv.Key.R},{kv.Key.G},{kv.Key.B},{kv.Key.A}) x{kv.Value}");
                }
                catch (Exception ex) { write("palette read failed: " + ex.Message); }
            }
        }
    }
}
