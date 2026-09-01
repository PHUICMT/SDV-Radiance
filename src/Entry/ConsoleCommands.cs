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
    internal static partial class ConsoleCommands
    {
        /// <param name="getConfig">Live config accessor (the instance is replaced on GMCM reset).</param>
        /// <param name="getPipeline">Live pipeline accessor (null until the first frame needs it).</param>
        /// <summary>Kept from registration so the tile report can say what else is installed.
        /// A report that arrives without it costs a round trip roughly every other time.</summary>
        private static IModRegistry? _registry;

        /// <summary>Opens (or closes) the F6 tuner. Held here because the game reads input through
        /// SDL rather than through window messages, so a synthesised keypress never reaches it and
        /// nothing driving the game from outside could open the menu at all.</summary>
        private static Action? _toggleTuner;

        internal static void RegisterAll(IModHelper helper, IMonitor monitor,
            Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline, Action toggleTuner)
        {
            _registry = helper.ModRegistry;
            _toggleTuner = toggleTuner;
            RegisterSceneReports(helper, monitor, getConfig, getPipeline);
            RegisterLiveWatches(helper, monitor, getConfig, getPipeline);
            RegisterLightAndShadowDiagnostics(helper, monitor, getConfig);
            RegisterBufferDiagnostics(helper, monitor, getConfig, getPipeline);
            RegisterCostMeasurements(helper, monitor, getConfig, getPipeline);
            RegisterOverlaysAndSwitches(helper, monitor);
        }

        /// <summary>Ask the mod what it can see: the map dump, the light list, the live config and the report.</summary>
        private static void RegisterSceneReports(IModHelper helper, IMonitor monitor, Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline)
        {
            // Author tool: dumps every location's layers/tiles + sheet art for HF Studio, the
            // browser labeler that produces labels/water-labels.json. Harmless for players (it
            // only runs when typed) and it keeps the whole labeling loop inside this mod.
            helper.ConsoleCommands.Add("radiance_mapdump",
                "Dump every location's layer/tile layout + sheet art to Documents\\HF-Studio\\maps.json for the label editor. "
                + "Add 'all' to also embed tilesheets that no loaded map places: the water-heavy and bridge-heavy art ships "
                + "as bare resource packs with no maps of their own, so walking the maps can never find it. "
                + "Sheet art is written as one PNG per sheet beside maps.json; add 'embed' for the old single inlined file. "
                + "Any other word names the MOD SET being dumped: runs add to the dump rather than replacing it, and a "
                + "location that differs between mod sets is kept once per version with the names of the sets that produce it.",
                (_, args) =>
                {
                    bool all = false, embed = false;
                    string profile = "";
                    foreach (string a in args)
                    {
                        if (a.Equals("all", StringComparison.OrdinalIgnoreCase)) all = true;
                        else if (a.Equals("embed", StringComparison.OrdinalIgnoreCase)) embed = true;
                        else if (profile.Length == 0) profile = a;
                    }
                    MapDump.Run(monitor, helper, allSheets: all, embedArt: embed, profile: profile);
                });
            helper.ConsoleCommands.Add("radiance_artfingerprint",
                "Fingerprint the art behind every labelled tile, so a label can later tell whether the picture it "
                + "was painted on is the one actually loaded. Name the set of art it is looking at, for example "
                + "'radiance_artfingerprint vanilla' or 'radiance_artfingerprint elle-earthy'. Writes "
                + "Documents\\HF-Studio\\fingerprints\\<name>.json.",
                (_, args) =>
                {
                    string label = args.Length > 0 ? string.Join(" ", args) : "unnamed";
                    ArtFingerprintDump.Run(monitor, helper, label);
                });
            helper.ConsoleCommands.Add("radiance_lights",
                "List every active light source in the current location (id, kind, tile, radius, color, distance from player).",
                (_, _) => DumpLights(monitor));
            // Flip any config value LIVE, without a restart and without touching config.json.
            //
            // This exists for one reason: measuring what each effect costs. A config edit needs a
            // game restart, restarts move the machine's baseline by more than a cheap effect
            // costs (two runs an hour apart differed by 0.25 ms in EVERY scene), so per-effect
            // numbers taken across restarts are noise arranged in a table. Toggling in-place
            // keeps every measurement inside one run where the baseline holds still.
            //
            // Deliberately not persisted: the point is A/B, and an A/B that rewrites the
            // player's file has a failure mode where a crash strands them on the B.
            helper.ConsoleCommands.Add("radiance_config",
                "Get or set a config value live, in memory only (config.json is not written). "
                + "'radiance_config' lists all keys, 'radiance_config Key' prints one, "
                + "'radiance_config Key value' sets it. Restart or GMCM-save to discard.",
                (_, args) => LiveConfig(monitor, getConfig(), args));
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
        }

        /// <summary>Traces that print only what CHANGED per frame - the shape of tool that beat three wrong guesses.</summary>
        private static void RegisterLiveWatches(IModHelper helper, IMonitor monitor, Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline)
        {
            helper.ConsoleCommands.Add("radiance_tile",
                "Dump water-related data for the tile under the player, or 'radiance_tile x y' for any tile (layer properties, HF class, isWaterTile, compose flags).",
                (_, args) => DumpTile(s => monitor.Log(s, LogLevel.Info), getPipeline(), getConfig(), args));
            helper.ConsoleCommands.Add("radiance_reliefdraws",
                "Name the sprites the sprite relief is embossing over one tile: 'radiance_reliefdraws' for the "
                + "tile under the player, or 'radiance_reliefdraws x y' for any tile. Prints each recorded draw "
                + "that covers it - sheet name, source cell, alpha and size, and whether it is BEVELLED or "
                + "left FLAT - so a thing that should not be wearing a bevel (water, a glow, an effect) can "
                + "be named instead of guessed at.",
                (_, args) => DumpReliefDraws(s => monitor.Log(s, LogLevel.Info), getPipeline(), args));
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
            helper.ConsoleCommands.Add("radiance_reflectwatch",
                "Trace the entity mirror for the next N frames (default 300) and print only what changes: "
                + "whether the bake ran, how many trees and bodies it stamped, how many creatures drew their own "
                + "mirror and how many came out empty, and the mask window it asked. For a reflection that "
                + "comes and goes: run it, then WALK along the water, and read which number moves.",
                (_, args) =>
                {
                    int frames = args.Length >= 1 && int.TryParse(args[0], out int rf) ? Math.Clamp(rf, 1, 3600) : 300;
                    RenderPipeline.ReflectWatchFrames = frames;
                    monitor.Log($"Watching the entity mirror for {frames} frames. Walk along the water now.", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_lightwatch",
                "Trace the light array for the next N frames (default 60) and print only what changes: "
                + "how many lights were offered versus how many slots exist, which ones entered or left, "
                + "and any whose brightness moved. Stand still where it flickers and run it.",
                (_, args) =>
                {
                    int frames = args.Length >= 1 && int.TryParse(args[0], out int f) ? Math.Clamp(f, 1, 3600) : 60;
                    RenderPipeline.LightWatchFrames = frames;
                    monitor.Log($"Watching the light array for {frames} frames.", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_brightwatch",
                "Trace EVERYTHING that decides how bright the frame is, for the next N frames "
                + "(default 300), printing only what changed: the light array's total, the ambient "
                + "darkness, the metered exposure, the lighting fade, the bounce grid's origin and "
                + "whether the occluder mask is up. radiance_lightwatch watches one of those and "
                + "will happily report that the lights are fine while the picture is pulsing. "
                + "WALK while it runs - standing still is the state in which the fault does not "
                + "happen, which is why a still capture cannot find it.",
                (_, args) =>
                {
                    int frames = args.Length >= 1 && int.TryParse(args[0], out int bf) ? Math.Clamp(bf, 1, 3600) : 300;
                    RenderPipeline.BrightWatchFrames = frames;
                    monitor.Log($"Watching everything that changes the brightness for {frames} frames. Walk now.", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_mark",
                "Write the whole light state of the NEXT frame to the log and capture a dump of it: position, clock, "
                + "every light slot with its ramp and shadow weight, the lights waiting for a slot, the bounce grid origin. "
                + "For the walking flicker: start radiance_lightwatch 3600 and radiance_brightwatch 3600, walk, and mark the "
                + "moment you see it (the author build also binds this to a key).",
                (_, _) => RenderPipeline.MarkPending = ++_markCount);
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
        }

        /// <summary>A/B the bounce grid against a flicker, and count what each caster shadows.</summary>
        private static void RegisterLightAndShadowDiagnostics(IModHelper helper, IMonitor monitor, Func<ModConfig> getConfig)
        {
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
        }

        /// <summary>Paint an internal buffer over the world, or capture every buffer for offline comparison.</summary>
        private static void RegisterBufferDiagnostics(IModHelper helper, IMonitor monitor, Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline)
        {
            helper.ConsoleCommands.Add("radiance_march",
                "List on-screen tiles whose water has effect but no march (ripple without reflection - the orange tiles in the radiance_debug water overlay), worst first.",
                (_, _) => monitor.Log(getPipeline()?.DescribeEffectOnlyTiles() ?? "pipeline not ready", LogLevel.Info));
            helper.ConsoleCommands.Add("radiance_maskdump",
                "Save the water mask and the flood occluder mask to PNG in the temp folder (debug).",
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
                "Show one internal buffer over the world. Channels: off | water | labeldiff | sdf | subtype | sprite | reflect "
                + "| mirror | mirrorsource | flood | normals | lampshadow | emitter | caustic | window | sky. "
                + "normals paints the sprite normal buffer the relief reads, and captions it with the recorded draw count, "
                + "the sway strips and the doubled-sheet redirects of this frame: the one place those counters are shown. "
                + "lampshadow paints the per-light shadow terms themselves, before they touch the picture, which is the only "
                + "way to tell a sawtoothed shadow edge in the terms from one in what they multiply. "
                + "flood paints the GI lightmap a cell at a time, so a light can be switched and its cells watched rather than guessed at. "
                + "emitter paints the lighting pass's answer to 'which pixels ARE a light': RED = treated as the light "
                + "itself and spared the room's dimming, GREEN = close enough to a light but not bright enough in the art to count. "
                + "labeldiff paints the radiance_verify verdict: RED = label says liquid but the mask has none, YELLOW = the mask ripples where the label says solid. "
                + "window paints every pixel the labels call glass in RED, at the depth the reflection is drawn at: red visible = the pane is seen and reaches the screen, "
                + "no red with panes>0 in radiance_report = the map draws its own art over it.",
                (_, args) =>
                {
                    if (args.Length < 1 || !Enum.TryParse(args[0], ignoreCase: true, out DebugOverlayChannel channel))
                    {
                        monitor.Log("usage: radiance_debug off|water|labeldiff|sdf|subtype|sprite|reflect|mirror|mirrorsource|flood|normals|lampshadow|emitter|caustic|window|sky "
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
            helper.ConsoleCommands.Add("radiance_creatures",
                "Where every creature is right now: horses, pets, farm animals and anything a mod adds, with the "
                + "tile to warp to, its frame size and how its shadow is laid down. "
                + "'radiance_creatures all' scans every loaded location, which is the only way to find one you are "
                + "not already standing next to. A wildlife mod's animals wander, so hunting for one by warping "
                + "around a map is how a test session gets spent.",
                (_, args) => monitor.Log(
                    ShadowRenderer.CreatureCensus(args.Length >= 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase),
                        getConfig().DirectionalShadowModel),
                    LogLevel.Info));
            helper.ConsoleCommands.Add("radiance_invincible",
                "Set invincibility ON or OFF and say which it ended up as. The game's own 'debug invincible' TOGGLES, "
                + "so a script that calls it twice quietly hands you back to the monsters, and a test session in the "
                + "mines is not the place to find that out. Usage: radiance_invincible [on|off], default on.",
                (_, args) =>
                {
                    bool wanted = args.Length < 1 || !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
                    Game1.player.temporarilyInvincible = wanted;
                    Game1.player.temporaryInvincibilityTimer = wanted ? -1000000000 : 0;
                    monitor.Log($"invincible = {Game1.player.temporarilyInvincible} (live only; nothing is saved)",
                        LogLevel.Info);
                });
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
            helper.ConsoleCommands.Add("radiance_dumpburst",
                "Capture N CONSECUTIVE finished frames to Documents\\Radiance-Dumps\\<name>\\ as PNGs. "
                + "Built for flicker: at an uncapped frame rate adjacent frames are milliseconds apart, so "
                + "intended animation barely moves between them and anything that differs hard IS the blink. "
                + "Do NOT freeze first — the blink under investigation is live behaviour. "
                + "Usage: radiance_dumpburst <name> [frames=12, max 24] [stride=1]. A stride of N keeps "
                + "every Nth frame, stretching the window to catch a POP instead of a per-frame blink.",
                (_, args) =>
                {
                    string name = args.Length >= 1 ? args[0] : "burst";
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                        name = name.Replace(c, '_');
                    int frames = args.Length >= 2 && int.TryParse(args[1], out int parsed) ? parsed : 12;
                    frames = Math.Clamp(frames, 2, 24);
                    int stride = args.Length >= 3 && int.TryParse(args[2], out int parsedStride) ? parsedStride : 1;
                    stride = Math.Clamp(stride, 1, 64);
                    RenderPipeline.RequestBurst(name, frames, stride);
                    monitor.Log($"radiance_dumpburst: capturing {frames} consecutive frames as '{name}'"
                        + (Determinism.Frozen ? " — WARNING: clock frozen, a blink cannot happen; radiance_freeze off first" : ""),
                        Determinism.Frozen ? LogLevel.Warn : LogLevel.Info);
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
                + "bridge/cliff reflection can be pinned on it in one keystroke. "
                + "(The slice height that used to live here is now the WaterReflectFadeRows setting, so "
                + "'radiance_config WaterReflectFadeRows 8' is the same A/B and it is saveable.)",
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

            helper.ConsoleCommands.Add("radiance_particles",
                "What the particle pool is doing. No args = report. 'test [seconds]' runs a fountain at the "
                + "player that is half drifting petals and half rising sparks, which is the one-glance check "
                + "that BOTH draw paths reached the screen: the petals go through the lighting and the grade "
                + "with the rest of the world, the sparks are added on top of the lighting. 'clear' empties "
                + "the pool. Particles are off by default - switch them on in the config menu or on the F6 "
                + "tuner's Particles tab first, or the fountain has nothing to draw into.",
                (_, args) =>
                {
                    RenderPipeline? pipeline = getPipeline();
                    if (pipeline == null) { monitor.Log("pipeline not ready", LogLevel.Info); return; }
                    if (args.Length >= 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
                    {
                        pipeline.ClearParticles();
                        monitor.Log("Particle pool emptied.", LogLevel.Info);
                        return;
                    }
                    if (args.Length >= 1 && args[0].Equals("test", StringComparison.OrdinalIgnoreCase))
                    {
                        int seconds = args.Length >= 2 && int.TryParse(args[1], out int typed)
                            ? Math.Clamp(typed, 1, 60) : 5;
                        pipeline.StartParticleTest(seconds * 60);
                        monitor.Log(getConfig().ParticlesEnabled
                            ? $"Fountain running at the player for {seconds}s."
                            : $"Fountain running at the player for {seconds}s, but Particles are switched OFF, "
                              + "so nothing will be drawn. Turn them on (F6, Particles tab) and run this again.",
                            LogLevel.Info);
                        return;
                    }
                    monitor.Log(pipeline.ParticleDiag(), LogLevel.Info);
                });

            helper.ConsoleCommands.Add("radiance_rings",
                "Take the player's rings off into the bag, or put them back: radiance_rings off|on. "
                + "A worn glow ring is a light source that follows the camera, which is exactly what a "
                + "shot of a window's reflection does not want: the pane blows out and the reflection is "
                + "the first thing to go. There is no way to do this from the game's own console, and the "
                + "gallery tool cannot click through the inventory screen. Nothing is saved, so the rings "
                + "are back on the next time the save is loaded whatever happens here.",
                (_, args) =>
                {
                    if (Game1.player == null)
                    {
                        monitor.Log("Load a save first.", LogLevel.Warn);
                        return;
                    }
                    string wanted = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    if (wanted is not ("off" or "on"))
                    {
                        monitor.Log("usage: radiance_rings off|on", LogLevel.Info);
                        return;
                    }
                    Farmer who = Game1.player;
                    int moved = 0;
                    if (wanted == "off")
                    {
                        foreach (var slot in new[] { who.leftRing, who.rightRing })
                        {
                            StardewValley.Objects.Ring? ring = slot.Value;
                            if (ring == null)
                                continue;
                            // onUnequip is what removes the light the ring hung on the player. Nulling
                            // the slot without it leaves the glow behind, which is the whole problem.
                            ring.onUnequip(who);
                            slot.Value = null;
                            if (!who.addItemToInventoryBool(ring))
                                monitor.Log("The bag is full, so one ring was dropped from the world.", LogLevel.Warn);
                            moved++;
                        }
                        monitor.Log(moved == 0 ? "No rings were being worn." : $"{moved} ring(s) off.", LogLevel.Info);
                        return;
                    }
                    for (int i = 0; i < who.Items.Count && moved < 2; i++)
                    {
                        if (who.Items[i] is not StardewValley.Objects.Ring ring)
                            continue;
                        var slot = who.leftRing.Value == null ? who.leftRing
                                 : who.rightRing.Value == null ? who.rightRing : null;
                        if (slot == null)
                            break;
                        who.Items[i] = null;
                        slot.Value = ring;
                        ring.onEquip(who);
                        moved++;
                    }
                    monitor.Log(moved == 0 ? "No rings in the bag to put on." : $"{moved} ring(s) on.", LogLevel.Info);
                });

            helper.ConsoleCommands.Add("radiance_aurora",
                "Force tonight's aurora display on or off, or hand it back to the nightly roll: "
                + "radiance_aurora on|off|auto. Only 62 percent of clear winter nights carry a display "
                + "at all, and it builds and dies over a few hours inside the night, so testing it by "
                + "waiting is a lottery. 'on' skips the roll and the ramp and holds it at full. Nothing "
                + "else about the gate changes: it still needs the switch, winter, a clear night "
                + "outdoors and reflections on. Not saved.",
                (_, args) =>
                {
                    string wanted = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    if (wanted is not ("on" or "off" or "auto"))
                    {
                        monitor.Log("usage: radiance_aurora on|off|auto (now: "
                            + (RenderPipeline.AuroraForce > 0 ? "on" : RenderPipeline.AuroraForce < 0 ? "off" : "auto")
                            + ")", LogLevel.Info);
                        return;
                    }
                    RenderPipeline.AuroraForce = wanted == "on" ? 1 : wanted == "off" ? -1 : 0;
                    monitor.Log($"aurora display: {wanted}. The rest of the gate is unchanged - "
                        + "check the sky: line of radiance_report if nothing shows.", LogLevel.Info);
                });

            helper.ConsoleCommands.Add("radiance_star",
                "Bring shooting stars forward to this frame: 'radiance_star' for three, or 'radiance_star n' "
                + "for one to three, spread across the view so at least one crosses open water. A streak only "
                + "exists where the sky does, which is water, so one placed by the player lands on the pier "
                + "they are standing on as often as not. The gate is unchanged: it still needs the switch, a "
                + "clear night outdoors and reflections on, and it reports which of those refused. Each streak "
                + "lasts around a second, and they come in three weights, so run it a few times.",
                (_, args) =>
                {
                    RenderPipeline? pipeline = getPipeline();
                    if (pipeline == null) { monitor.Log("pipeline not ready", LogLevel.Info); return; }
                    ModConfig config = getConfig();
                    GameLocation? location = Game1.currentLocation;
                    string weather = Game1.isLightning ? "storm" : Game1.isRaining ? "rain"
                        : Game1.isSnowing ? "snow" : Game1.isDebrisWeather ? "windy" : "sun";
                    if (!config.ShootingStarsEnabled)
                    { monitor.Log("Shooting stars are switched OFF (F6, Weather tab).", LogLevel.Info); return; }
                    if (!config.WaterReflection)
                    { monitor.Log("Water reflection is OFF, and the sky only exists inside the reflection.", LogLevel.Info); return; }
                    if (!(location?.IsOutdoors ?? false))
                    { monitor.Log("Indoors: there is no sky here to put a star in.", LogLevel.Info); return; }
                    if (weather is not "sun" and not "windy")
                    { monitor.Log($"Weather is {weather}; a star needs a clear sky. Try radiance_weather sun.", LogLevel.Info); return; }
                    if (Game1.timeOfDay < 1930)
                    { monitor.Log($"It is {Game1.getTimeOfDayString(Game1.timeOfDay)}; the sky is not dark enough until about 19:30.", LogLevel.Info); return; }
                    int wanted = args.Length >= 1 && int.TryParse(args[0], out int typedStars)
                        ? Math.Clamp(typedStars, 1, 3) : 3;
                    pipeline.MeteorRequests = wanted;
                    monitor.Log($"{wanted} star(s) requested, spread across the view - watch the open water.", LogLevel.Info);
                });
        }

        /// <summary>What this machine actually pays, per stage and per effect.</summary>
        private static void RegisterCostMeasurements(IModHelper helper, IMonitor monitor, Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline)
        {
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
            helper.ConsoleCommands.Add("radiance_effectcost",
                "Price EVERY effect separately, in this scene, in about thirty seconds. Toggling one effect and "
                + "watching the frame rate cannot answer this: an effect costs a tenth of a millisecond and the "
                + "machine's own drift between two readings is half of one, so the answer is noise. This runs each "
                + "effect seven times per frame and keeps the slope, which lifts the signal clear of the drift, and "
                + "it reads the GPU's clock rather than the CPU's, because fill is what most of these cost. Stand "
                + "somewhere demanding and do not move while it runs.",
                (_, args) =>
                {
                    if (!StardewModdingAPI.Context.IsWorldReady)
                    {
                        monitor.Log("Load a save first — there is nothing being drawn to measure.", LogLevel.Info);
                        return;
                    }
                    var pipeline = getPipeline();
                    int amp = args.Length > 0 && int.TryParse(args[0], out int a) ? a : 6;
                    if (pipeline == null) monitor.Log("Pipeline not ready.", LogLevel.Info);
                    else pipeline.StartEffectCost(getConfig(), amp);
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
            helper.ConsoleCommands.Add("radiance_gldiag",
                "Report the real graphics backend and test whether a GPU timer query can be created, run and read "
                + "back from inside this mod. Every other timer here measures CPU submission, not GPU execution, "
                + "which is why object shadows once read as a rounding error while costing 1.80 ms. Answers whether "
                + "the fix for that is available on this machine.",
                (_, _) => GlDiag(monitor));
        }

        /// <summary>Open the tuner, drive the on-screen HUDs, and flip the author-only switches.</summary>
        private static void RegisterOverlaysAndSwitches(IModHelper helper, IMonitor monitor)
        {
            helper.ConsoleCommands.Add("radiance_weather",
                "Set the CURRENT location context's weather for testing: radiance_weather sun|rain|storm|snow|wind|greenrain. "
                + "Absolute, not a toggle (the game's own 'debug rain' flips). Storm = rain + lightning. Exists because "
                + "vanilla has no debug command at all for snow, storm or wind. Weather is per context, so set it while "
                + "standing in the region you want to test. Not saved; the next day's roll overwrites it.",
                (_, args) =>
                {
                    if (Game1.netWorldState?.Value == null || Game1.player?.currentLocation == null)
                    {
                        monitor.Log("Load a save first.", LogLevel.Warn);
                        return;
                    }
                    string wanted = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    if (wanted is not ("sun" or "rain" or "storm" or "snow" or "wind" or "greenrain"))
                    {
                        monitor.Log("Usage: radiance_weather sun|rain|storm|snow|wind|greenrain", LogLevel.Info);
                        return;
                    }
                    string contextId = Game1.player.currentLocation.GetLocationContextId();
                    StardewValley.Network.LocationWeather weather = Game1.netWorldState.Value.GetWeatherForLocation(contextId);
                    // The weather id is a field of its own beside the flags, and it is what asking
                    // a location what its weather is returns. Writing only the flags left the two
                    // disagreeing: the rain stopped falling while every reader of the id still saw
                    // rain, which is how a test harness set a sunny day and was told it was raining.
                    // Written before the flags in case its setter has an opinion about them.
                    weather.Weather = wanted switch
                    {
                        "rain" => Game1.weather_rain,
                        "storm" => Game1.weather_lightning,
                        "snow" => Game1.weather_snow,
                        "wind" => Game1.weather_debris,
                        "greenrain" => Game1.weather_green_rain,
                        _ => Game1.weather_sunny,
                    };
                    // Clear everything first: the game treats the flags as one-per-day exclusive
                    // (storm being the rain+lightning pair), and IsGreenRain's setter forces
                    // IsRaining back on, so green rain must be cleared before rain is written.
                    weather.IsGreenRain = false;
                    weather.IsRaining = false;
                    weather.IsLightning = false;
                    weather.IsSnowing = false;
                    weather.IsDebrisWeather = false;
                    switch (wanted)
                    {
                        case "rain": weather.IsRaining = true; break;
                        case "storm": weather.IsRaining = true; weather.IsLightning = true; break;
                        case "snow": weather.IsSnowing = true; break;
                        case "wind": weather.IsDebrisWeather = true; break;
                        case "greenrain": weather.IsGreenRain = true; break;
                    }
                    if (contextId == "Default")
                    {
                        // The legacy statics mirror only the Default context; the game refreshes
                        // them once per day, so a mid-day change has to write them by hand or
                        // half the code base keeps seeing yesterday's weather.
                        Game1.isRaining = weather.IsRaining;
                        Game1.isLightning = weather.IsLightning;
                        Game1.isSnowing = weather.IsSnowing;
                        Game1.isDebrisWeather = weather.IsDebrisWeather;
                        Game1.isGreenRain = weather.IsGreenRain;
                    }
                    monitor.Log($"Weather for context '{contextId}': {wanted}. Ambient light follows on the next clock "
                        + "tick; indoor window glow only refreshes when a location is entered.", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_tuner",
                "Open the on-screen tuner, the same panel the tuner hotkey opens (F6 by default). Exists because "
                + "the game takes keyboard input straight from SDL, so a keypress sent by a script never arrives "
                + "and there was no way to reach this menu except by hand. Add part of a tab name to open on "
                + "that tab, for example 'radiance_tuner perf' or 'radiance_tuner water'.",
                (_, args) =>
                {
                    if (args.Length > 0)
                        RadianceTunerMenu.OpenAtTab(args[0]);
                    _toggleTuner?.Invoke();
                });
            helper.ConsoleCommands.Add("radiance_perfhud",
                "Show the cost readout on screen while you play (radiance_perfhud on|off). Same numbers as "
                + "radiance_report, but live, so you can walk into the spot that stutters and watch which line "
                + "moves. It also says when the game window has lost focus, which makes the frame time read far "
                + "worse than it is.",
                (_, args) =>
                {
                    PerfHud.Visible = args.Length > 0
                        ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase) || args[0] == "1"
                        : !PerfHud.Visible;
                    monitor.Log($"Perf readout: {(PerfHud.Visible ? "on" : "off")}", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_gputime",
                "Switch per-stage GPU timing on or off (radiance_gputime on|off). Off by default: it reaches "
                + "around MonoGame into the game's own OpenGL context, which is not a risk worth taking while "
                + "nobody is asking a question. With it on, radiance_report grows a GPU column beside the CPU "
                + "one, and where the two disagree the larger is the real cost.",
                (_, args) =>
                {
                    bool on = args.Length > 0
                        && (args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                            || args[0] == "1" || args[0].Equals("true", StringComparison.OrdinalIgnoreCase));
                    GpuTimer.SetWanted(on);
                    monitor.Log($"GPU timing: {GpuTimer.Status}"
                        + (on ? ". Play for five seconds, then run radiance_report." : ""), LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_autoscale",
                "What the automatic render scale is doing, and a way to make it do it. With no "
                + "argument it prints the controller's state. 'radiance_autoscale budget <ms>' makes it "
                + "believe the frame budget is that short, which turns an ordinary capped frame into an "
                + "overrun and so exercises the real controller on a machine that never misses its own "
                + "budget; 'radiance_autoscale budget off' puts the real one back. The override is never "
                + "saved and does not survive a restart.",
                (_, args) =>
                {
                    if (args.Length >= 2 && args[0].Equals("budget", StringComparison.OrdinalIgnoreCase))
                    {
                        if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                        {
                            RenderPipeline.BudgetOverrideMs = 0;
                            monitor.Log("Frame budget back to the game's own.", LogLevel.Info);
                        }
                        else if (double.TryParse(args[1], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms > 0.1)
                        {
                            RenderPipeline.BudgetOverrideMs = ms;
                            monitor.Log($"Pretending the frame budget is {ms:0.0} ms. "
                                + "Play for a few seconds, then run this again.", LogLevel.Info);
                        }
                        else
                            monitor.Log("Give a number of milliseconds, or 'off'.", LogLevel.Warn);
                    }
                    monitor.Log(RenderPipeline.Current?.DescribeAutoScale() ?? "pipeline not ready", LogLevel.Info);
                });
            helper.ConsoleCommands.Add("radiance_anim",
                "Count this location's animated map tiles and report how fast they actually advance, in ticks. "
                + "The map dump records which tiles animate but not their frame interval, so this is the only way "
                + "to tell whether a cache clocked in ticks can keep up with the art it is mirroring.",
                (_, _) => AnimReport(monitor));
        }

        /// <summary>Console command: read or write one config property on the LIVE instance, by
        /// reflection so a new setting is covered the day it is added rather than when someone
        /// remembers this list exists. Clamp() runs after every set, so the console cannot put a
        /// value out of the range the sliders enforce.</summary>
        private static int _markCount;

        private static void LiveConfig(IMonitor monitor, ModConfig config, string[] args)
        {
            var props = typeof(ModConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (args.Length == 0)
            {
                var text = new System.Text.StringBuilder("Live config values (in-memory; not saved to config.json):\n");
                foreach (var p in props)
                    if (p.CanWrite && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(string) || p.PropertyType.IsEnum))
                        text.AppendLine($"  {p.Name} = {p.GetValue(config)}");
                monitor.Log(text.ToString().TrimEnd(), LogLevel.Info);
                return;
            }
            var prop = Array.Find(props, p => p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            if (prop == null || !prop.CanWrite)
            {
                monitor.Log($"No writable config property named '{args[0]}'. Run radiance_config with no arguments for the list.", LogLevel.Warn);
                return;
            }
            if (args.Length == 1)
            {
                monitor.Log($"{prop.Name} = {prop.GetValue(config)}", LogLevel.Info);
                return;
            }
            try
            {
                object value =
                    prop.PropertyType == typeof(bool) ? bool.Parse(args[1])
                    : prop.PropertyType == typeof(float) ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
                    : prop.PropertyType == typeof(int) ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
                    // Named looks (the reflection style, the camera mode) are enums, and an A/B
                    // between two looks is exactly what this command is for.
                    : prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, args[1], ignoreCase: true)
                    : args[1];
                prop.SetValue(config, value);
                config.Clamp();
                monitor.Log($"{prop.Name} = {prop.GetValue(config)}  (live only; config.json untouched)", LogLevel.Info);
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not set {prop.Name} to '{args[1]}': {ex.Message}", LogLevel.Warn);
            }
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
        internal static void WriteReport(IModHelper helper, IMonitor monitor, RenderPipeline? pipeline, ModConfig config, bool alsoLog = false)
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
                Write("=== the effect chain, one line per full-screen pass ===");
                Write(pipeline?.DescribeStageCost() ?? "pipeline not ready");
                Write("");
                Write(pipeline?.DescribeAutoScale() ?? "pipeline not ready");
                Write("");
                Write(VramTally.Describe());
                Write("");
                // The clock block. Two numbers here decide whether anything ELSE in this report
                // about animation speed or flicker can be read at all, and both used to be
                // invisible: GpuContent counted lost render targets from the day it was written
                // and nothing ever printed the count.
                Write("=== what the lamp shadow march was handed this frame ===");
                Write($"  lamps marching   {RenderPipeline.LastMarchingLamps}"
                    + $"   steps allowed each   {RenderPipeline.LastMarchStepCeiling:0.#}");
                Write("                   The cost of this mod's biggest GPU item is those two numbers");
                Write("                   multiplied. Sharing is on when the second falls as the first");
                Write("                   rises; 12 is the floor and is what every release up to 1.6.2 did.");
                Write("");
                Write("=== the frame clock, and the graphics device under it ===");
                Write($"  frame cap        {(Determinism.CapIsOff ? "OFF right now" : "on right now")}"
                    + $", changed {Determinism.CapChanges} time(s) this session");
                Write("  what that means  a cap that has NEVER lifted means this mod is reading the game's own");
                Write("                   frame counter, and an uncapper that is running would have lifted it.");
                Write("                   Zero lifts plus unlocked frames on screen is the uncapper not doing");
                Write("                   what it says, and nothing below is worth reading until that is settled.");
                Write($"  render targets whose pixels were found gone: {GpuContent.LostCount}");
                Write("                   READ THIS ONLY ON DIRECTX. The check behind it is RenderTarget2D's own");
                Write("                   IsContentLost, and this game's MonoGame build returns a hardcoded false");
                Write("                   from it on the OpenGL backend, which is what Stardew runs here. So a");
                Write("                   zero on this line is the question going unanswered, not an answer: it");
                Write("                   does NOT mean the graphics device was never reset under us.");
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
                // On a phone there is no console to type this from and no easy way to reach a file
                // in app storage, but the SMAPI log uploads to smapi.io in two taps. So when the
                // report is asked for from the settings menu rather than the console, it goes into
                // the log as well, where the reporter can actually get at it.
                if (alsoLog)
                    monitor.Log("--- SDV-Radiance report ---" + Environment.NewLine + text, LogLevel.Info);
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

        /// <summary>What the sprite relief is embossing over one tile, from the last frame the
        /// recorder captured (see <see cref="SpriteDrawRecorder"/>): the draws whose footprint
        /// covers that tile, each named by its sheet.</summary>
        private static void DumpReliefDraws(Action<string> write, RenderPipeline? pipeline, string[]? args = null)
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                write("Load a save first.");
                return;
            }
            Point tile = Game1.player.TilePoint;
            if (args is { Length: >= 2 } && int.TryParse(args[0], out int ax) && int.TryParse(args[1], out int ay))
                tile = new Point(ax, ay);
            var records = SpriteDrawRecorder.Records;
            write($"=== relief draws over tile ({tile.X},{tile.Y}) ===");
            write($"recorded {records.Count} draws last frame ({SpriteDrawRecorder.PatchedOverloads} overloads patched); "
                + "the list is screen-space, as the game's batch received it.");
            if (pipeline != null)
                write($"tree trunks mended last frame: {pipeline.TrunkJoinsMended} "
                    + "(zero with trees on screen means the canopy/trunk pair was not recognised)");
            // BOTH SIDES of the test that decides FLAT or BEVELLED, printed together. Whether a
            // draw is the map's own art is decided by comparing what the map says it paints from
            // against what the draw actually carries, and reading only one of those two lists is
            // how this was misdiagnosed twice: once by assuming the map's name, once by assuming
            // the drawn name. They are both here now, so the mismatch can be seen rather than
            // reasoned about.
            var map = Game1.currentLocation?.Map;
            if (map != null)
            {
                var declared = new System.Collections.Generic.List<string>();
                foreach (xTile.Tiles.TileSheet sheet in map.TileSheets)
                    declared.Add(sheet.ImageSource ?? "<null>");
                write("the map says it paints from: " + string.Join(", ", declared));
            }
            if (records.Count == 0)
            {
                write("Nothing recorded: the relief is off, or faded out. Turn on sprite relief and try again.");
                return;
            }
            // The recorder holds SCREEN pixels; the tile is world, so put the tile on the screen.
            var want = new Rectangle(tile.X * 64 - Game1.viewport.X, tile.Y * 64 - Game1.viewport.Y, 64, 64);
            int listed = 0, skipped = 0;
            foreach (SpriteDrawRecorder.Record record in records)
            {
                Rectangle footprint = record.UsesDestination
                    ? record.Destination
                    : new Rectangle((int)(record.Position.X - record.Origin.X * record.Scale.X),
                        (int)(record.Position.Y - record.Origin.Y * record.Scale.Y),
                        (int)Math.Ceiling(record.Source.Width * record.Scale.X),
                        (int)Math.Ceiling(record.Source.Height * record.Scale.Y));
                if (!footprint.Intersects(want))
                    continue;
                if (listed >= 24)
                {
                    skipped++;
                    continue;
                }
                listed++;
                string sheet = record.Texture.IsDisposed ? "<disposed>"
                    : string.IsNullOrEmpty(record.Texture.Name) ? "<unnamed>" : record.Texture.Name;
                // The whole point of this tool is "is THIS thing wearing a bevel", and listing the
                // draw without saying so answers a different question: a map tile is recorded and
                // replayed like any other, just with a flat normal, so its presence in the list
                // proves nothing either way. Reading the list as if it did cost a wrong "fixed".
                string kind = record.Texture.IsDisposed ? " [disposed]"
                    : pipeline == null ? " [pipeline not ready, cannot say]"
                    : pipeline.ReliefLeavesSheetFlat(record.Texture) ? " [FLAT: no bevel]"
                    : " [BEVELLED]";
                write($"  {sheet}{kind}  cell {record.Source.X},{record.Source.Y} {record.Source.Width}x{record.Source.Height}"
                    + $"  on screen {footprint.X},{footprint.Y} {footprint.Width}x{footprint.Height}"
                    + $"  alpha {record.Alpha:F2} depth {record.Depth:F4}"
                    + (record.Effects == SpriteEffects.None ? "" : $" {record.Effects}"));
            }
            if (skipped > 0)
                write($"  ... and {skipped} more over this tile.");
            if (listed == 0)
                write("  nothing recorded covers that tile.");
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
            WriteSceneHeader(write, location, config, pipeline);
            var surf = SurfaceMap.For(location);
            WriteTileVerdict(write, location, t, surf, pipeline);
            WriteLayerTiles(write, location, t);
            WriteNeighbourhood(write, location, t, surf);

            // Palette of the Back art (top colours by count) — for tuning art classifiers.
            if (!includePalette)
                return;
            WriteBackPalette(write, location, t);
        }

        /// <summary>Game minutes as the clock shows them, hours past midnight included, so an
        /// aurora that runs to 25:10 reads as 25:10 rather than as one in the morning.</summary>
        private static string Clock(float minutes) => $"{(int)(minutes / 60f):00}:{(int)(minutes % 60f):00}";

        /// <summary>Everything needed to reproduce the scene, first, because this output gets
        /// pasted into a bug report by someone who will not be asked a second question.</summary>
        private static void WriteSceneHeader(Action<string> write, GameLocation location, ModConfig config, RenderPipeline? pipeline = null)
        {
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
            // The caustic term, spelled out as the number the shader received and every factor
            // that made it. "I toggled it and saw nothing" is answered here in one line: an
            // uploaded amount of zero with the toggle on names the factor that killed it.
            if (pipeline != null)
                write($"caustics: uploaded={pipeline._causticAmountUploaded:0.000} (toggle={config.WaterCausticsEnabled} "
                    + $"strength={config.WaterCausticsStrength:0.00} ease={pipeline.CausticEase:0.00} shimmer={pipeline.ShimmerEase:0.00} "
                    + $"presence={pipeline.FadeWaterForReport:0.00} daylight={pipeline._causticDaylight:0.00} "
                    + $"weather={pipeline.CausticWeatherMultiplier:0.00} indoors={!location.IsOutdoors} "
                    + $"texture={(pipeline.CausticTextureMissing ? "MISSING" : "loaded")})");
            // The reflected sky, spelled out the same way and for exactly the same reason. The
            // aurora and the shooting star share one gate (switch, outdoors, clear weather, real
            // night, winter for the aurora) and they only exist inside the reflection, so five
            // separate things can each silently be the answer to "I turned it on and saw nothing".
            if (pipeline != null)
                write($"sky: aurora={pipeline.SkyAuroraUploaded:0.000} (toggle={config.AuroraEnabled} "
                    + $"dial={config.AuroraStrength:0.00} winter={Game1.IsWinter} "
                    + $"show={RenderPipeline.SkyAuroraShow:0.00} tonight={RenderPipeline.SkyAuroraTonight} "
                    + $"{Clock(RenderPipeline.SkyAuroraShowStart)}-{Clock(RenderPipeline.SkyAuroraShowEnd)} "
                    + $"force={(RenderPipeline.AuroraForce > 0 ? "on" : RenderPipeline.AuroraForce < 0 ? "off" : "auto")}) star: "
                    + $"envelope={pipeline.SkyMeteorEnvelope:0.000} burning={pipeline.SkyMeteorBurning}/3 (toggle={config.ShootingStarsEnabled} "
                    + $"nextIn={(pipeline.SkyMeteorSecondsToNext < 0 ? "n/a" : pipeline.SkyMeteorSecondsToNext.ToString("0") + "s")}) "
                    + $"gate: clearNight={pipeline.SkyClearNight} outdoors={location.IsOutdoors} "
                    + $"weather={weather} reflection={config.WaterReflection}");
            // The window pass, spelled out the same way and for the same reason. A street where
            // nothing shows in the glass is either a street whose windows nobody has labelled
            // (panes=0) or a street whose windows are drawn over by the map's own front layer
            // (panes>0, onScreen>0, and radiance_debug window paints nothing).
            if (pipeline != null)
                write($"windows: panes={pipeline.WindowPanesInLocation} onScreen={pipeline.WindowPanesOnScreen} "
                    + $"(toggle={config.WindowReflectionEnabled} reflect={pipeline.WindowReflectUploaded:0.000} "
                    + $"sheen={pipeline.WindowSheenUploaded:0.000} glare={pipeline.WindowGlareUploaded:0.000} "
                    + $"street={pipeline.WindowSceneUploaded:0.000} lamps={pipeline.WindowLampGlowUploaded:0.000} lampsDrawn={pipeline.WindowLampsDrawn}/{pipeline.WindowLampsConsidered} "
                    + $"day={config.WindowReflectionStrength:0.00} night={config.WindowReflectionNightStrength:0.00} "
                    + $"sky={config.WindowSheenStrength:0.00} glareDial={config.WindowGlareStrength:0.00} "
                    + $"scene={config.WindowSceneReflectionStrength:0.00} lamp={config.WindowLightGlowStrength:0.00} "
                    + $"sceneSource={(pipeline.SceneRTReady ? "ready" : "not baked")} "
                    + $"glowTexture={(pipeline.GlassGlowTextureMissing ? "MISSING" : "loaded")})");
            // The particle pool, spelled out the same way. "I see no petals" has as many
            // separate causes as "I see nothing in the glass" did: the system off, the presence
            // still lifting, an empty pool, or a pool that is full and entirely off screen.
            if (pipeline != null)
                write($"{pipeline.ParticleDiag()} (toggle={config.ParticlesEnabled} density={config.ParticleDensity:0.00})");
            write(PrecipitationSystem.Diag());
            // Art drawn past the edge of its own sheet: names the pack, so an invisible tree or a
            // single blurred tile can be answered instead of argued about.
            write(ShadowSuppression.DescribeRepairedArt());
            if (pipeline != null)
                write(pipeline.WetWorldDiag(config));
            // WHERE THIS PLACE CAME FROM. "Which map or mod is that bridge from" is the question
            // every water report needs answered and no reporter can answer, because from inside the
            // game a modded map looks exactly like a vanilla one.
            string mapAsset = location.mapPath?.Value ?? "unknown";
            write($"map asset: {mapAsset}  [{AttributeAsset(mapAsset)}]");
            write($"location type: {location.GetType().Name} | context: {location.GetLocationContextId()}"
                + $" | size {location.Map?.Layers[0].LayerWidth}x{location.Map?.Layers[0].LayerHeight}"
                + $" | tilesheets: {location.Map?.TileSheets.Count}");
            ReportRelevantMods(write);
        }

        /// <summary>What the game and this mod each believe about the one tile: its water
        /// properties, its surface class, and the composed mask beside the label.</summary>
        private static void WriteTileVerdict(Action<string> write, GameLocation location, Point t,
                                             SurfaceMap? surf, RenderPipeline? pipeline)
        {
            write($"isWaterTile={location.isWaterTile(t.X, t.Y)} drawnWater={WaterDrawHook.WasDrawn(location, t.X, t.Y)} (hook v{WaterDrawHook.Version})");
            foreach (string prop in new[] { "Water", "WaterSource", "Passable", "Type" })
                foreach (string layer in new[] { "Back", "Buildings" })
                {
                    string? v = location.doesTileHaveProperty(t.X, t.Y, prop, layer);
                    if (v != null)
                        write($"{layer}.{prop} = '{v}'");
                }
            if (surf != null)
                write($"surface={surf.GetSurface(t.X, t.Y)} height={surf.GetHeight(t.X, t.Y)}");
            // Composed mask vs label, side by side — the acceptance test for this subsystem
            // is that the game matches the labeler, so print both from the same tile.
            write(pipeline?.DescribeTileMask(location, t.X, t.Y) ?? "pipeline not ready");
        }

        /// <summary>The tile on every layer the game draws, walked from the map's own layer list
        /// rather than a fixed set of names.</summary>
        private static void WriteLayerTiles(Action<string> write, GameLocation location, Point t)
        {
            // Walk the map's OWN layer list rather than a fixed set of names: a map may carry
            // Back3, Buildings4 or a negative suffix, and naming them here by hand is how this
            // report ended up silent about layers the game draws.
            foreach (var layer in location.map?.Layers ?? (System.Collections.Generic.IEnumerable<xTile.Layers.Layer>)System.Array.Empty<xTile.Layers.Layer>())
            {
                if (!MapLayers.TryGetFamily(layer.Id, out _))
                    continue;
                if (t.X >= layer.LayerWidth || t.Y >= layer.LayerHeight)
                    continue;
                var tile = layer.Tiles[t.X, t.Y];
                if (tile == null)
                    continue;
                bool anim = tile is xTile.Tiles.AnimatedTile;
                // Tile PROPERTIES, because a flip or rotation does not live in the tile index.
                // The .tmx stores it in the gid's top bits, and whichever loader brought the map
                // in has to put it somewhere the index cannot carry. Neither the dump nor this
                // report ever showed it, so "the preview draws this tile unrotated" was a
                // question nothing here could answer. Empty means the tile really is plain.
                string props = "";
                try
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var kv in tile.Properties)
                        parts.Add($"{kv.Key}={kv.Value}");
                    foreach (var kv in tile.TileIndexProperties)
                        parts.Add($"idx:{kv.Key}={kv.Value}");
                    if (parts.Count > 0)
                        props = "  props{" + string.Join(", ", parts) + "}";
                }
                catch { /* a property bag that throws is not worth failing the report over */ }
                // ImageSource (the asset path) is what the labeler keys on; Id is the map-local alias.
                write($"{layer.Id}: sheet={tile.TileSheet?.Id} src={tile.TileSheet?.ImageSource} index={tile.TileIndex} animated={anim}"
                    + $"  [{AttributeAsset(tile.TileSheet?.ImageSource)}]{props}");
            }
        }

        /// <summary>The block around the tile as a picture. Almost every water report is about a
        /// SHAPE, and one tile cannot show a shape.</summary>
        private static void WriteNeighbourhood(Action<string> write, GameLocation location, Point t,
                                               SurfaceMap? surf)
        {
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
        }

        /// <summary>Top colours of the Back art, for tuning art classifiers.</summary>
        private static void WriteBackPalette(Action<string> write, GameLocation location, Point t)
        {
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
