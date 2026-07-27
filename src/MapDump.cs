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
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Tile? t = layer.Tiles[x, y];
                        if (t is AnimatedTile anim && anim.TileFrames.Length > 0)
                        {
                            RecordAnim(anim, artSources, animSigs, animGroups);
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
                layers.Add(new { id = layer.Id, w, h, cells = Convert.ToBase64String(bytes) });
            }
            if (layers.Count == 0)
                return null;
            for (int i = 0; i < sheets.Count; i++)
                if (used[i] && !artSources.ContainsKey(sheets[i]) && sheetRefs[i].ImageSource is { } src)
                    artSources[sheets[i]] = src;
            return new { outdoors = loc.IsOutdoors, sheets, layers };
        }
    }
}
