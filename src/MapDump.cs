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

        public static string? RunFromBridge(bool allSheets)
            => BridgeMonitor != null && BridgeHelper != null ? Run(BridgeMonitor, BridgeHelper, allSheets) : null;

        /// <param name="allSheets">Also embed tilesheet art that NO loaded map places. Off by
        /// default because it reads every PNG under the Mods folder and roughly doubles the dump.</param>
        public static string? Run(IMonitor monitor, IModHelper helper, bool allSheets = false)
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
            Utility.ForEachLocation(location =>
            {
                try
                {
                    object? entry = DumpLocation(location, artSources, water, animSigs, animGroups);
                    if (entry != null)
                        locations[location.NameOrUniqueName] = entry;
                }
                catch (Exception ex)
                {
                    monitor.Log($"mapdump: skipped {location.NameOrUniqueName}: {ex.Message}", LogLevel.Trace);
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

            // ...and then the sheets NO map places, which is a whole category the dump used to miss
            // entirely. The most water-dense and bridge-dense art in the mod scene ships as bare
            // TILESHEET RESOURCE PACKS - Sharogg's, crystalinerose, AToMS, Alvadea's, Lumisteria -
            // which carry no maps of their own. Their art only reaches a map when some OTHER mod
            // decides to use it, so walking the loaded maps can never find it, and it was invisible
            // to the labeller no matter which mods were switched on. Read off disk instead, since
            // an asset nobody has loaded has no asset name to ask the content pipeline for.
            if (allSheets)
                AddUnplacedSheetArt(helper, monitor, artPaths);

            var art = new Dictionary<string, string>();
            foreach ((string name, string src) in artPaths)
            {
                try
                {
                    // Two kinds of source now share this list: an asset key the content pipeline
                    // knows, and a plain file on disk for a sheet nothing has loaded. A rooted path
                    // is never a valid asset key, so the two cannot be confused.
                    bool fromDisk = Path.IsPathRooted(src);
                    var texture = fromDisk
                        ? LoadFromDisk(src)
                        : Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(src);
                    try
                    {
                        using var ms = new MemoryStream();
                        texture.SaveAsPng(ms, texture.Width, texture.Height);
                        art[name] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                    finally
                    {
                        // Only the ones we opened ourselves. A texture from the content pipeline is
                        // the game's and is still in use; one read off disk here belongs to nobody
                        // else, and `all` reads several hundred of them, so holding every one until
                        // the dump finished was hundreds of megabytes of video memory for nothing.
                        if (fromDisk) texture.Dispose();
                    }
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

        /// <summary>
        /// Find tilesheet PNGs sitting in the Mods folder that no loaded map references, and add
        /// them to the art list by file path.
        ///
        /// <para>Deliberately crude about WHAT a tilesheet is, because being wrong is cheap here: a
        /// sheet that turns out to be a portrait sheet costs one line in a JSON file, while a
        /// waterfall sheet that never appears costs an afternoon of wondering where it went. The
        /// filters are only there to keep the dump from swallowing every character sprite in a
        /// hundred-mod install: at least 128px on both sides, a multiple of 16, and not sitting in
        /// a folder whose name says it is people rather than places.</para>
        ///
        /// <para>Keyed on the FILE name, which is what the label store keys on, and which for a
        /// resource pack is also the asset name the consuming map will use. That is not guaranteed
        /// for every pack, so anything found this way is marked in artSrc as coming from disk.</para>
        /// </summary>
        /// <summary>A PNG the content pipeline has never heard of, read straight off disk.</summary>
        private static Microsoft.Xna.Framework.Graphics.Texture2D LoadFromDisk(string path)
        {
            using var fs = File.OpenRead(path);
            return Microsoft.Xna.Framework.Graphics.Texture2D.FromStream(Game1.graphics.GraphicsDevice, fs);
        }

        private static void AddUnplacedSheetArt(IModHelper helper, IMonitor monitor, Dictionary<string, string> artPaths)
        {
            string? mods = helper.DirectoryPath;
            while (mods != null && !string.Equals(Path.GetFileName(mods), "Mods", StringComparison.OrdinalIgnoreCase))
                mods = Path.GetDirectoryName(mods);
            if (mods == null)
            {
                monitor.Log("mapdump: could not find the Mods folder, so unplaced sheets were skipped.", LogLevel.Warn);
                return;
            }
            string[] roots = { mods, Path.Combine(Path.GetDirectoryName(mods)!, "Mods (disabled)") };
            // The shape test cannot tell a tilesheet from a 1080p screenshot: both are big and
            // 16-aligned. Screenshot folders were 82% of the first all-sheets dump (406MB of the
            // 495MB), all of it from our own dev capture folder, so name them out up front.
            string[] notPlaces = { "portrait", "character", "animals", "fashion", "\\ui", "icon", "emoji",
                                   "hair", "shirt", "pants", "hats", "shoes", "tattoo", "bodies",
                                   "\\shots\\", "screenshot", "\\shot_" };
            int added = 0;
            byte[] head = new byte[24];      // outside the loop: a stackalloc in there is a slow leak
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
                {
                    string lower = file.Replace('/', '\\').ToLowerInvariant();
                    bool skip = false;
                    foreach (string bad in notPlaces)
                        if (lower.Contains(bad)) { skip = true; break; }
                    if (skip)
                        continue;
                    string name = LabelStore.NormalizeSheet(file);
                    if (artPaths.ContainsKey(name))
                        continue;
                    try
                    {
                        // Header only: width and height live at a fixed offset in every PNG, so the
                        // shape test costs 24 bytes rather than decoding a megabyte to reject it.
                        using var fs = File.OpenRead(file);
                        if (fs.Read(head, 0, 24) < 24 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G')
                            continue;
                        int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                        int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                        if (w < 128 || h < 128 || (w & 15) != 0 || (h & 15) != 0)
                            continue;
                    }
                    catch { continue; }
                    artPaths[name] = file;
                    added++;
                }
            }
            monitor.Log($"mapdump: {added} tilesheet(s) found on disk that no loaded map places.", LogLevel.Info);
        }

        private static object? DumpLocation(GameLocation location, Dictionary<string, string> artSources, Dictionary<string, HashSet<int>> water,
                                           HashSet<string> animSigs, List<string[]> animGroups)
        {
            xTile.Map? map = location.Map;
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
                bool layerHasWater = false;
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
                        layerHasWater = true;
                        if (isBack && location.isWaterTile(x, y))
                        {
                            string sn = sheets[si];
                            if (!water.TryGetValue(sn, out HashSet<int>? set))
                                water[sn] = set = new HashSet<int>();
                            set.Add(t.TileIndex);
                        }
                    }
                }
                if (!layerHasWater)
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
            if (location.waterTiles?.waterTiles is { } wt)
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
                        if (location.doesTileHaveProperty(x, y, "Water", "Buildings") != null) wBld.Add(idx);
                        if (location.doesTileHaveProperty(x, y, "WaterSource", "Back") != null) wSrc.Add(idx);
                        if (location.doesTileHaveProperty(x, y, "Passable", "Buildings") != null) pBld.Add(idx);
                        if (location.doesTileHaveProperty(x, y, "NoFishing", "Back") != null) noFish.Add(idx);
                    }
                }
            }

            // Every building = a potential occluder (footprint + sprite height feed the V4
            // reflection height gate). Fish ponds additionally draw their own water
            // (World_Sorted pass, not drawWater) on the interior 3x3 of a 5x5 footprint,
            // tinted per FishPondData — none of it visible to waterTiles.
            var buildings = new List<object>();
            var fishPonds = new List<object>();
            foreach (var b in location.buildings)
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
            try { locSeason = location.GetSeason().ToString(); } catch { }

            var wc = location.waterColor.Value;
            var layersAll = new List<string>();
            foreach (Layer l in map.Layers)
                layersAll.Add(l.Id);

            return new
            {
                outdoors = location.IsOutdoors, sheets, layers,
                cls = location.GetType().FullName,
                locSeason,
                waterColor = new[] { (int)wc.R, (int)wc.G, (int)wc.B, (int)wc.A },
                indoorWater = location.HasMapPropertyWithValue("indoorWater"),
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
