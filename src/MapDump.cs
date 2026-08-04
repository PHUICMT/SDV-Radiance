using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using xTile.Layers;
using xTile.Tiles;

namespace SDVRadiance
{
    /// <summary>
    /// <c>radiance_mapdump</c>: dumps every location's layer/tile layout to <c>mapdump/maps.json</c>
    /// so HF Studio's map mode can recompose the real in-game view and let the user label
    /// tiles in context. Cell encoding: int32 = sheetIndex * 0x100000 + tileIndex, -1 = empty;
    /// each layer's cells are the raw int32 array base64-encoded (little-endian).
    /// </summary>
    internal static class MapDump
    {
        // Set once from ModEntry so out-of-process drivers (AgentBridge, via reflection on
        // "SDVRadiance.MapDump"."RunFromBridge") can trigger a dump without holding our
        // monitor/helper. Radiance's dumper carries fields HF's never will (wgrid/wI/wBld/
        // fishPonds/waterColor), so the bridge must land here, not on HF's RunMapDump.
        internal static IMonitor? BridgeMonitor;
        internal static IModHelper? BridgeHelper;

        public static string? RunFromBridge()
            => BridgeMonitor != null && BridgeHelper != null ? Run(BridgeMonitor, BridgeHelper) : null;

        public static string? Run(IMonitor monitor, IModHelper helper)
        {
            if (!Context.IsWorldReady)
            {
                monitor.Log("Load a save first, then run radiance_mapdump again.", LogLevel.Warn);
                return null;
            }

            var locations = new Dictionary<string, object>();
            var artSources = new Dictionary<string, string>();   // normalized sheet name -> content path
            // Ground-truth water: normalized sheet name -> tile indices the GAME reports as water
            // (isWaterTile / Back "Water" property) anywhere across every loaded location, vanilla
            // or mod. HF Studio uses this to auto-fill water far more accurately than a colour guess.
            var water = new Dictionary<string, HashSet<int>>();
            // Animation frame groups: every frame of an AnimatedTile as "<sheet>:<index>". A
            // waterfall or fountain cycles through several tiles, and the dump can only freeze
            // ONE of them per cell, so labelling in map mode used to mark a single frame and the
            // effect flickered in game. HF Studio uses these groups to fan a label out to the
            // whole cycle.
            var animSigs = new HashSet<string>();
            var animGroups = new List<string[]>();
            Utility.ForEachLocation(loc =>
            {
                try
                {
                    object? entry = DumpLocation(loc, artSources, water, animSigs, animGroups);
                    if (entry != null)
                        locations[loc.NameOrUniqueName] = entry;
                }
                catch (Exception ex)
                {
                    monitor.Log($"mapdump: skipped {loc.NameOrUniqueName}: {ex.Message}", LogLevel.Trace);
                }
                return true;
            }, includeInteriors: true, includeGenerated: false);

            // Embed the ART of every sheet any map actually references (interiors, paths
            // sheets, mod tilesheets ...). HF Studio only ships the 21 core sheets; without
            // this, every other sheet composed as black "no art" holes in map mode.
            //
            // ALL SEASONS: season-variant tilesheets are just separate files (spring_/summer_/
            // fall_/winter_). We load every season's sibling of a referenced sheet directly by
            // path — no need to change the in-game date/season (that would mutate the save and
            // is risky). So one dump, run in any season, yields art for all four.
            var seasons = new[] { "spring", "summer", "fall", "winter" };
            var artPaths = new Dictionary<string, string>();   // normalized name -> content path
            foreach ((string name, string src) in artSources)
            {
                artPaths[name] = src;
                foreach (string se in seasons)
                {
                    if (src.IndexOf(se, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    foreach (string other in seasons)
                    {
                        if (other == se)
                            continue;
                        // swap the season token in both the path and the display name
                        string sibPath = System.Text.RegularExpressions.Regex.Replace(src, se, other, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        string sibName = LabelStore.NormalizeSheet(sibPath);
                        if (!artPaths.ContainsKey(sibName))
                            artPaths[sibName] = sibPath;
                    }
                    break;   // one season token per path is enough
                }
            }

            var art = new Dictionary<string, string>();
            foreach ((string name, string src) in artPaths)
            {
                try
                {
                    var tex = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(src);
                    using var ms = new MemoryStream();
                    tex.SaveAsPng(ms, tex.Width, tex.Height);
                    art[name] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
                catch (Exception ex)
                {
                    monitor.Log($"mapdump: no art for {name} ({src}): {ex.Message}", LogLevel.Trace);
                }
            }

            // Flatten the water ground-truth sets to plain arrays for JSON (membership only, order
            // does not matter). Additive field: older HF Studio builds simply ignore it.
            var waterOut = new Dictionary<string, int[]>();
            foreach (var kv in water)
            {
                var arr = new int[kv.Value.Count];
                kv.Value.CopyTo(arr);
                waterOut[kv.Key] = arr;
            }

            // Where each sheet's art came from (its content path / asset key). The art itself is
            // embedded as base64, which loses any trace of who supplied it, so HF Studio could not
            // group sheets by mod the way it groups locations. Additive field: older builds ignore
            // it, and only sheets we managed to load art for are listed.
            var artSrc = new Dictionary<string, string>();
            foreach ((string name, string src) in artPaths)
            {
                if (art.ContainsKey(name))
                    artSrc[name] = src;
            }

            var doc = new { format = "hf-mapdump-v1", season = Game1.currentSeason, locations, art, artSrc, water = waterOut, animGroups };
            string json = JsonSerializer.Serialize(doc);

            // Primary target: Documents\HF-Studio. The mod folder lives under Program Files,
            // and Chrome's File System Access API refuses to open files in system folders
            // ("contains system files"), which broke HF Studio's auto-load/auto-reload.
            // Documents is user-writable AND FS-Access-pickable, so the dump goes there;
            // the mod-folder copy stays as a fallback for the plain <input> picker.
            string primary;
            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string sdir = Path.Combine(docs, "HF-Studio");
                Directory.CreateDirectory(sdir);
                primary = Path.Combine(sdir, "maps.json");
                File.WriteAllText(primary, json);
                // Season-suffixed copy so four dumps (one per in-game season) can coexist for
                // coverage analysis; HF Studio keeps loading plain maps.json (the latest dump).
                try { File.WriteAllText(Path.Combine(sdir, $"maps-{Game1.currentSeason}.json"), json); }
                catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                primary = "";
                monitor.Log($"mapdump: could not write to Documents ({ex.Message}); mod-folder copy only.", LogLevel.Trace);
            }

            string dir = Path.Combine(helper.DirectoryPath, "mapdump");
            Directory.CreateDirectory(dir);
            string modPath = Path.Combine(dir, "maps.json");
            try { File.WriteAllText(modPath, json); } catch { /* Program Files may be read-only */ }

            monitor.Log($"Dumped {locations.Count} locations + {art.Count} sheet art + {animGroups.Count} animation groups.", LogLevel.Info);
            monitor.Log(primary.Length > 0
                ? $"In HF Studio: click the map-dump button, pick:  {primary}"
                : $"In HF Studio: click the map-dump button, pick:  {modPath}", LogLevel.Info);
            return primary.Length > 0 ? primary : modPath;
        }

        /// <summary>True for the layer names the game draws: the four families, optionally with a
        /// numeric suffix (Back2, Buildings3, AlwaysFront4). Anything else is markers or a
        /// deliberately disabled layer.</summary>
        private static bool IsRenderedLayer(string id)
        {
            foreach (string fam in new[] { "Back", "Buildings", "Front", "AlwaysFront" })
            {
                if (!id.StartsWith(fam, StringComparison.Ordinal))
                    continue;
                string rest = id.Substring(fam.Length);
                bool digitsOnly = true;
                foreach (char ch in rest)
                    if (ch < '0' || ch > '9') { digitsOnly = false; break; }
                if (digitsOnly)
                    return true;   // "AlwaysFront" must win over the "Front" prefix test: check all
            }
            return false;
        }

        /// <summary>Record every frame of an animated tile as "&lt;sheet&gt;:&lt;index&gt;", deduplicated
        /// across the whole dump. A frame's own sheet is registered for art embedding too: a cycle
        /// can step onto a sheet no static cell references, and HF Studio needs its art to paint it.</summary>
        private static void RecordAnim(AnimatedTile anim, Dictionary<string, string> artSources,
                                      HashSet<string> sigs, List<string[]> groups)
        {
            var frames = new List<string>();
            foreach (StaticTile f in anim.TileFrames)
            {
                if (f?.TileSheet == null)
                    continue;
                string sn = LabelStore.NormalizeSheet(f.TileSheet.ImageSource ?? f.TileSheet.Id);
                frames.Add(sn + ":" + f.TileIndex);
                if (!artSources.ContainsKey(sn) && f.TileSheet.ImageSource is { } src)
                    artSources[sn] = src;
            }
            if (frames.Count < 2)
                return;   // a one-frame "animation" has nothing to fan out to
            string sig = string.Join("|", frames);
            if (sigs.Add(sig))
                groups.Add(frames.ToArray());
        }

        private static object? DumpLocation(GameLocation loc, Dictionary<string, string> artSources, Dictionary<string, HashSet<int>> water,
                                           HashSet<string> animSigs, List<string[]> animGroups)
        {
            xTile.Map? map = loc.Map;
            if (map == null || map.TileSheets.Count == 0)
                return null;

            var sheetIndex = new Dictionary<TileSheet, int>();
            var sheets = new List<string>();
            var sheetRefs = new List<TileSheet>();
            foreach (TileSheet ts in map.TileSheets)
            {
                sheetIndex[ts] = sheets.Count;
                sheets.Add(LabelStore.NormalizeSheet(ts.ImageSource ?? ts.Id));
                sheetRefs.Add(ts);
            }
            var used = new bool[sheets.Count];

            var layers = new List<object>();
            foreach (Layer layer in map.Layers)
            {
                // ONLY the families the game actually renders. Everything else is either a
                // logic-only marker layer (Paths, "Light Coordinates", "SVE NPC Spots",
                // "all_Crops", "Swamp Lurk Spots" ...) or a layer the map author DISABLED by
                // renaming it with a suffix ("Buildings-1", "AlwaysFront-1" — the Tiled
                // convention). Dumping them made HF Studio composite art the game never draws,
                // which covered the water underneath and hid its labels.
                if (!IsRenderedLayer(layer.Id))
                    continue;
                int w = layer.LayerWidth, h = layer.LayerHeight;
                if (w <= 0 || h <= 0 || w * h > 4_000_000)
                    continue;
                // The GAME's water flag lives on the Back layer (isWaterTile reads its "Water"
                // property). On that layer, whenever a cell is water we record its tile's
                // (sheet, index) so HF Studio can auto-fill exactly those tiles as class 1.
                bool isBack = layer.Id.Equals("Back", StringComparison.OrdinalIgnoreCase);
                int[] cells = new int[w * h];
                var animCells = new List<int>();   // packed y*w+x of animated cells on THIS layer
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Tile? t = layer.Tiles[x, y];
                        if (t is AnimatedTile anim && anim.TileFrames.Length > 0)
                        {
                            RecordAnim(anim, artSources, animSigs, animGroups);
                            animCells.Add(y * w + x);
                            t = anim.TileFrames[0];   // deterministic: first frame
                        }
                        if (t == null || !sheetIndex.TryGetValue(t.TileSheet, out int si))
                        {
                            cells[y * w + x] = -1;
                            continue;
                        }
                        cells[y * w + x] = si * 0x100000 + t.TileIndex;
                        used[si] = true;
                        any = true;
                        if (isBack && loc.isWaterTile(x, y))
                        {
                            string sn = sheets[si];
                            if (!water.TryGetValue(sn, out HashSet<int>? set))
                                water[sn] = set = new HashSet<int>();
                            set.Add(t.TileIndex);
                        }
                    }
                }
                if (!any)
                    continue;
                byte[] bytes = new byte[cells.Length * 4];
                Buffer.BlockCopy(cells, 0, bytes, 0, bytes.Length);
                layers.Add(new
                {
                    id = layer.Id, w, h, cells = Convert.ToBase64String(bytes),
                    anim = animCells.Count > 0 ? animCells.ToArray() : null,
                });
            }
            if (layers.Count == 0)
                return null;
            for (int i = 0; i < sheets.Count; i++)
                if (used[i] && !artSources.ContainsKey(sheets[i]) && sheetRefs[i].ImageSource is { } src)
                    artSources[sheets[i]] = src;

            // --- V4 ground-truth extras (additive; HF Studio builds that predate them ignore them) ---

            // Per-location water grid straight from the game's own baked array, because the
            // per-sheet 'water' map above cannot express two things: WHERE the water is on this
            // map, and Water="I" tiles (water for gameplay that the game never draws an overlay
            // on — waterfall bases, decorative edges). wgrid = row-major bitmask of isWater;
            // wI = packed y*w+x indices of the invisible subset.
            string? wgrid = null;
            var wI = new List<int>();
            if (loc.waterTiles?.waterTiles is { } wt)
            {
                int ww = wt.GetLength(0), wh = wt.GetLength(1);
                var bits = new byte[(ww * wh + 7) / 8];
                bool anyW = false;
                for (int y = 0; y < wh; y++)
                {
                    for (int x = 0; x < ww; x++)
                    {
                        if (!wt[x, y].isWater)
                            continue;
                        int idx = y * ww + x;
                        bits[idx >> 3] |= (byte)(1 << (idx & 7));
                        anyW = true;
                        if (!wt[x, y].isVisible)
                            wI.Add(idx);
                    }
                }
                if (anyW)
                    wgrid = Convert.ToBase64String(bits);
            }

            // One property sweep, four packed index lists (y*w+x):
            //   wBld  = "Water" on Buildings — fishable-under-bridge, NOT in waterTiles, never mirror
            //   wSrc  = "WaterSource" on Back — watering-can refill, must NOT be treated as water
            //   pBld  = "Passable" on Buildings — bridge planks etc., occluders that stay walkable
            //   noFish= "NoFishing" on Back — water where fishing is off (festival edges etc.)
            var wBld = new List<int>();
            var wSrc = new List<int>();
            var pBld = new List<int>();
            var noFish = new List<int>();
            int mw = map.Layers[0].LayerWidth, mh = map.Layers[0].LayerHeight;
            if (mw > 0 && mh > 0 && mw * mh <= 4_000_000)
            {
                for (int y = 0; y < mh; y++)
                {
                    for (int x = 0; x < mw; x++)
                    {
                        int idx = y * mw + x;
                        if (loc.doesTileHaveProperty(x, y, "Water", "Buildings") != null) wBld.Add(idx);
                        if (loc.doesTileHaveProperty(x, y, "WaterSource", "Back") != null) wSrc.Add(idx);
                        if (loc.doesTileHaveProperty(x, y, "Passable", "Buildings") != null) pBld.Add(idx);
                        if (loc.doesTileHaveProperty(x, y, "NoFishing", "Back") != null) noFish.Add(idx);
                    }
                }
            }

            // Every building = a potential occluder (footprint + sprite height feed the V4
            // reflection height gate). Fish ponds additionally draw their own water
            // (World_Sorted pass, not drawWater) on the interior 3x3 of a 5x5 footprint,
            // tinted per FishPondData — none of it visible to waterTiles.
            var buildings = new List<object>();
            var fishPonds = new List<object>();
            foreach (var b in loc.buildings)
            {
                int srcH = 0;
                try { srcH = b.getSourceRect().Height; } catch { }
                buildings.Add(new
                {
                    type = b.buildingType.Value,
                    x = b.tileX.Value, y = b.tileY.Value,
                    w = b.tilesWide.Value, h = b.tilesHigh.Value,
                    srcH,
                    building = b.daysOfConstructionLeft.Value > 0,
                });
                if (b is StardewValley.Buildings.FishPond fp && fp.daysOfConstructionLeft.Value <= 0)
                {
                    var pc = fp.overrideWaterColor.Value;
                    fishPonds.Add(new
                    {
                        x = fp.tileX.Value, y = fp.tileY.Value,
                        w = fp.tilesWide.Value, h = fp.tilesHigh.Value,
                        color = new[] { (int)pc.R, (int)pc.G, (int)pc.B, (int)pc.A },
                    });
                }
            }

            // Raw map properties (indoorWater, ambient sounds, custom framework flags ...) and
            // the location's own class + per-location season (Ginger Island stays summer).
            var mapProps = new Dictionary<string, string>();
            try
            {
                foreach (var kv in map.Properties)
                    mapProps[kv.Key] = kv.Value?.ToString() ?? "";
            }
            catch { }
            string? locSeason = null;
            try { locSeason = loc.GetSeason().ToString(); } catch { }

            var wc = loc.waterColor.Value;
            var layersAll = new List<string>();
            foreach (Layer l in map.Layers)
                layersAll.Add(l.Id);

            return new
            {
                outdoors = loc.IsOutdoors, sheets, layers,
                cls = loc.GetType().FullName,
                locSeason,
                waterColor = new[] { (int)wc.R, (int)wc.G, (int)wc.B, (int)wc.A },
                indoorWater = loc.HasMapPropertyWithValue("indoorWater"),
                mapProps = mapProps.Count > 0 ? mapProps : null,
                layersAll,
                wgrid,
                wI = wI.Count > 0 ? wI.ToArray() : null,
                wBld = wBld.Count > 0 ? wBld.ToArray() : null,
                wSrc = wSrc.Count > 0 ? wSrc.ToArray() : null,
                pBld = pBld.Count > 0 ? pBld.ToArray() : null,
                noFish = noFish.Count > 0 ? noFish.ToArray() : null,
                buildings = buildings.Count > 0 ? buildings : null,
                fishPonds = fishPonds.Count > 0 ? fishPonds : null,
            };
        }
    }
}
