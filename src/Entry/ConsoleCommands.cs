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
        internal static void RegisterAll(IModHelper helper, IMonitor monitor,
            Func<ModConfig> getConfig, Func<RenderPipeline?> getPipeline)
        {
            // Author tool: dumps every location's layers/tiles + sheet art for HF Studio, the
            // browser labeler that produces labels/water-labels.json. Harmless for players (it
            // only runs when typed) and it keeps the whole labeling loop inside this mod.
            helper.ConsoleCommands.Add("radiance_mapdump",
                "Dump every location's layer/tile layout + sheet art to Documents\\HF-Studio\\maps.json for the label editor.",
                (_, _) => { MapDump.Run(monitor, helper); });
            helper.ConsoleCommands.Add("radiance_lights",
                "List every active light source in the current location (id, kind, tile, radius, color, distance from player).",
                (_, _) => DumpLights(monitor));
            helper.ConsoleCommands.Add("radiance_tile",
                "Dump water-related data for the tile under the player, or 'radiance_tile x y' for any tile (layer properties, HF class, isWaterTile, compose flags).",
                (_, args) => DumpTile(monitor, getPipeline(), args));
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
                "Show one internal buffer over the world. Channels: off | water | labeldiff | sdf | subtype | sprite | reflect | mirror. "
                + "labeldiff paints the radiance_verify verdict: RED = label says liquid but the mask has none, YELLOW = the mask ripples where the label says solid.",
                (_, args) =>
                {
                    if (args.Length < 1 || !Enum.TryParse(args[0], ignoreCase: true, out DebugOverlayChannel channel))
                    {
                        monitor.Log("usage: radiance_debug off|water|labeldiff|sdf|subtype|sprite|reflect|mirror "
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
        private static void DumpTile(IMonitor monitor, RenderPipeline? pipeline, string[]? args = null)
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
            {
                monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }
            var location = Game1.currentLocation;
            var t = Game1.player.TilePoint;
            if (args is { Length: >= 2 } && int.TryParse(args[0], out int ax) && int.TryParse(args[1], out int ay))
                t = new Point(ax, ay);
            monitor.Log($"=== Tile ({t.X},{t.Y}) in {location?.NameOrUniqueName} ===", LogLevel.Info);
            if (location == null) return;
            monitor.Log($"isWaterTile={location.isWaterTile(t.X, t.Y)} drawnWater={WaterDrawHook.WasDrawn(location, t.X, t.Y)} (hook v{WaterDrawHook.Version})", LogLevel.Info);
            foreach (string prop in new[] { "Water", "WaterSource", "Passable", "Type" })
                foreach (string layer in new[] { "Back", "Buildings" })
                {
                    string? v = location.doesTileHaveProperty(t.X, t.Y, prop, layer);
                    if (v != null)
                        monitor.Log($"{layer}.{prop} = '{v}'", LogLevel.Info);
                }
            var surf = SurfaceMap.For(location);
            if (surf != null)
                monitor.Log($"surface={surf.GetSurface(t.X, t.Y)} height={surf.GetHeight(t.X, t.Y)}", LogLevel.Info);
            // Composed mask vs label, side by side — the acceptance test for this subsystem
            // is that the game matches the labeler, so print both from the same tile.
            monitor.Log(pipeline?.DescribeTileMask(location, t.X, t.Y) ?? "pipeline not ready", LogLevel.Info);
            foreach (string layerName in new[] { "Back", "Buildings", "Front", "AlwaysFront", "AlwaysFront2" })
            {
                var layer = location.map?.GetLayer(layerName);
                var tile = layer?.Tiles[t.X, t.Y];
                if (tile == null)
                    continue;
                bool anim = tile is xTile.Tiles.AnimatedTile;
                // ImageSource (the asset path) is what the labeler keys on; Id is the map-local alias.
                monitor.Log($"{layerName}: sheet={tile.TileSheet?.Id} src={tile.TileSheet?.ImageSource} index={tile.TileIndex} animated={anim}", LogLevel.Info);
            }

            // Palette of the Back art (top colours by count) — for tuning art classifiers.
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
                    monitor.Log("Back art palette (top 10):", LogLevel.Info);
                    foreach (var kv in System.Linq.Enumerable.Take(
                        System.Linq.Enumerable.OrderByDescending(groups, g => g.Value), 10))
                        monitor.Log($"    RGBA({kv.Key.R},{kv.Key.G},{kv.Key.B},{kv.Key.A}) x{kv.Value}", LogLevel.Info);
                }
                catch (Exception ex) { monitor.Log("palette read failed: " + ex.Message, LogLevel.Info); }
            }
        }
    }
}
