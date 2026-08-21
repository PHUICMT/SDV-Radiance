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

        /// <param name="profile">The mod set this run is looking at, so a sweep over several of
        /// them can tell whose Town is whose.</param>
        /// <remarks>
        /// ONE method with a default argument, never two overloads. The bridge finds this by
        /// reflection, and <c>Type.GetMethod(name, flags)</c> throws AmbiguousMatchException the
        /// moment a second overload exists: adding one turned every dump into HTTP 500 and a
        /// sweep ran eight passes recording nothing at all. A method that cannot be ambiguous
        /// cannot do that again, whatever the caller asks for.
        /// </remarks>
        public static string? RunFromBridge(bool allSheets, string profile = "")
            => BridgeMonitor != null && BridgeHelper != null
                ? Run(BridgeMonitor, BridgeHelper, allSheets, profile: profile)
                : null;

        /// <param name="allSheets">Also embed tilesheet art that NO loaded map places. Off by
        /// default because it reads every PNG under the Mods folder and roughly doubles the dump.</param>
        /// <param name="embedArt">Write the old single-file dump with every sheet inlined as a
        /// base64 data URI, instead of one PNG per sheet beside it. Only for a labeller build
        /// that has not learned about artPng yet.</param>
        /// <param name="profile">What to call the mod set this run is looking at. A location
        /// that differs between profiles is kept once per version rather than overwritten, and
        /// this is the name recorded against each one. Blank means "unnamed", which still works
        /// but leaves the versions unattributed.</param>
        public static string? Run(IMonitor monitor, IModHelper helper, bool allSheets = false,
                                  bool embedArt = false, string profile = "")
        {
            if (!Context.IsWorldReady)
            {
                monitor.Log("Load a save first, then run radiance_mapdump again.", LogLevel.Warn);
                return null;
            }

            var locations = new Dictionary<string, Dictionary<string, object?>>();
            // Normalized sheet name -> EVERY distinct content path seen under that name, in the
            // order they were met. It used to be one path per name, first one wins, which quietly
            // threw away the fact that different mods ship different art under one file name; the
            // art written for the name was then whichever map happened to load first, and every
            // other map that names the same sheet drew from a picture it was never built against.
            var artSources = new Dictionary<string, List<string>>();
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
                    Dictionary<string, object?>? entry = DumpLocation(location, artSources, water, animSigs, animGroups);
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
            var artPaths = new Dictionary<string, List<string>>();   // normalized name -> every content path
            foreach ((string name, List<string> srcs) in artSources)
            {
                foreach (string src in srcs)
                {
                    AddArtSource(artPaths, name, src);
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
                                AddArtSource(artPaths, sibName, sibPath);
                        }
                        break;   // one season token per path is enough
                    }
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

            // Sheet art goes to its own PNG per sheet, and maps.json only names the files.
            //
            // Embedding every sheet as a base64 data URI made the art 76% of a 240MB maps.json
            // (182MB of it, 108MB for sheets no map even places), and the labeller has to
            // JSON.parse the whole thing before it can show a single map - so opening the tool
            // meant decoding a thousand images nobody had asked for. base64 also inflates a PNG
            // by a third on top of that. Written as files, maps.json drops to the ~58MB of map
            // structure, and the labeller reads a sheet's PNG only when that sheet is opened.
            //
            // The labeller loads them through its Live-JSON directory handle, so the bytes
            // arrive as a File and become a blob URL. That matters: a plain file:// <img> from
            // another folder TAINTS the canvas and getImageData - which reads the labels back -
            // throws SecurityError. A data URI never tainted, which is why the art was embedded
            // in the first place; a blob URL from a handle does not taint either.
            var art = new Dictionary<string, string>();          // legacy embed (embedArt only)
            var artPng = new Dictionary<string, string>();        // name -> "sheets/<file>.png"
            // The same PNG list keyed by the FULL content path instead of the sheet's bare name.
            // Two mods may ship different art under one file name - "spring_outdoorsTileSheet2"
            // exists in the base game, in three recolours and in two foliage packs on this install,
            // at four different sizes - and the bare name cannot tell them apart. A map names the
            // full path of the sheet it places, so this is the lookup that returns the art the map
            // actually draws with, rather than whichever mod happened to be read first.
            var artPngBySrc = new Dictionary<string, string>();
            // What the art turned out to be, in tiles-of-16: name -> [width, height]. A labeller
            // laying tile indices out over the PNG has to divide by the sheet's width, and when the
            // art it holds is a different size from the art the map was built against, every index
            // past the first row lands on the wrong tile. Recorded so that mismatch is visible
            // instead of silently drawing the wrong picture.
            var artDim = new Dictionary<string, int[]>();
            var usedLocationFiles = new HashSet<string>();        // a separate pool: a location and a sheet may share a name
            string sheetDir = Path.Combine(HfStudioDir(), "sheets");
            if (!embedArt)
            {
                try { Directory.CreateDirectory(sheetDir); }
                catch (Exception ex) { monitor.Log($"mapdump: cannot create {sheetDir}: {ex.Message}", LogLevel.Warn); }
            }
            foreach ((string name, List<string> srcs) in artPaths)
            {
                foreach (string src in srcs)
                {
                    try
                    {
                        // Two kinds of source now share this list: an asset key the content pipeline
                        // knows, and a plain file on disk for a sheet nothing has loaded. A rooted path
                        // is never a valid asset key, so the two cannot be confused.
                        bool fromDisk = Path.IsPathRooted(src);
                        var texture = fromDisk ? LoadFromDisk(src) : LoadAsset(helper, src);
                        try
                        {
                            if (embedArt)
                            {
                                using var ms = new MemoryStream();
                                texture.SaveAsPng(ms, texture.Width, texture.Height);
                                art[name] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                            }
                            else
                            {
                                // Through memory rather than straight to the file: the name is
                                // derived from the bytes, so the bytes have to exist first.
                                byte[] png;
                                using (var ms = new MemoryStream())
                                {
                                    texture.SaveAsPng(ms, texture.Width, texture.Height);
                                    png = ms.ToArray();
                                }
                                string file = ArtFileName(name, src, png) + ".png";
                                string full = Path.Combine(sheetDir, file);
                                // Same name means same bytes, so a re-dump of art nothing changed
                                // is a no-op instead of several hundred megabytes of rewriting.
                                if (!File.Exists(full))
                                    File.WriteAllBytes(full, png);
                                // ...but "same name" is case-insensitive here, and the sheet's name
                                // comes from whichever mod loaded it. Two mods spelling one sheet
                                // differently (Lighthouse_TileSheet / Lighthouse_Tilesheet) share the
                                // file and disagree about its name, and the index would then point at
                                // a file that exists under a spelling it does not use. Take the name
                                // the file actually has, so the index is readable somewhere that
                                // cares about case - which the browser reading this corpus does.
                                file = OnDiskName(sheetDir, file);
                                artPngBySrc[src] = "sheets/" + file;
                                // First source wins the bare name, and the first is the one a loaded
                                // map placed: DumpLocation fills artSources before the season siblings
                                // and the disk sweep are added to it. A map that places a DIFFERENT
                                // file under this name resolves through artPngBySrc instead.
                                if (!artPng.ContainsKey(name))
                                {
                                    artPng[name] = "sheets/" + file;
                                    artDim[name] = new[] { texture.Width, texture.Height };
                                }
                            }
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
            }

            // Which FILE each location's sheets turned out to be. The art is content-addressed
            // now, so this is the only lookup that survives a second profile: `artPngBySrc` is
            // keyed by asset path, and every profile patches the same paths. A map that records
            // its own files draws its own art whatever else has been dumped since.
            foreach (Dictionary<string, object?> entry in locations.Values)
            {
                if (entry.TryGetValue("sheetSrc", out object? ss) && ss is List<string?> srcList)
                {
                    var files = new List<string?>(srcList.Count);
                    foreach (string? one in srcList)
                        files.Add(one != null && artPngBySrc.TryGetValue(one, out string? f) ? f : null);
                    entry["sheetArt"] = files;
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
            foreach ((string name, List<string> srcs) in artPaths)
            {
                if (art.ContainsKey(name) || artPng.ContainsKey(name))
                    artSrc[name] = srcs[0];
            }

            // ONE FILE PER LOCATION, and an index that names them.
            //
            // The single file grew to 224 MB of which 222 MB is the locations' cell data, and the
            // labeller has to JSON.parse ALL of it before it can show anything: several seconds of
            // a frozen tab at boot, and a heap big enough that every later allocation risks a
            // garbage-collection pause in the middle of a brush stroke. None of that data is needed
            // until a map is actually opened, and only one map is ever open.
            //
            // So the index carries what the sidebar needs about every location (its name, whether
            // it is outdoors, and the sheets it places - that last one is what "which sheet covers
            // the most maps" is counted from) plus the file to read for the rest. A labeller that
            // predates this reads `hf-mapdump-v2` as unknown and refuses the file, which is the
            // honest failure: the maps really are not in it.
            //
            // ...and the index is CUMULATIVE. A run used to overwrite it, so the dump only ever
            // held what the last profile happened to have loaded: 34 passes over the mod library
            // left 221 locations behind, and a map pack's Town replaced the base game's under the
            // one name Town. A location is kept once per VERSION it actually has - keyed by what
            // it draws and what it draws it with - and every profile that produced that version
            // is listed against it. Identical is identical: a location no pack touches is dumped
            // once and merely gains another name in `from` on every later pass.
            string studioDir = HfStudioDir();
            string profileLabel = string.IsNullOrWhiteSpace(profile) ? "unnamed" : profile.Trim();
            using JsonDocument? previous = ReadExistingDump(studioDir, monitor);
            var index = new Dictionary<string, object?>();
            var alreadyHeld = new Dictionary<string, List<(string Key, string Stamp)>>();
            var profilesSeen = new List<string>();
            if (previous != null)
            {
                JsonElement root = previous.RootElement;
                if (root.TryGetProperty("locations", out JsonElement oldLocations)
                    && oldLocations.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty held in oldLocations.EnumerateObject())
                    {
                        var record = JsonSerializer.Deserialize<Dictionary<string, object?>>(held.Value.GetRawText());
                        if (record == null)
                            continue;
                        index[held.Name] = record;
                        // Its file name is spoken for, whatever this run decides to call anything.
                        if (held.Value.TryGetProperty("file", out JsonElement fileEl) && fileEl.ValueKind == JsonValueKind.String)
                            usedLocationFiles.Add(Path.GetFileNameWithoutExtension(fileEl.GetString() ?? "").ToLowerInvariant());
                        string heldName = held.Value.TryGetProperty("name", out JsonElement ne) && ne.ValueKind == JsonValueKind.String
                            ? ne.GetString()! : held.Name;
                        string heldStamp = held.Value.TryGetProperty("variant", out JsonElement ve) && ve.ValueKind == JsonValueKind.String
                            ? ve.GetString()! : "";
                        if (!alreadyHeld.TryGetValue(heldName, out var versions))
                            alreadyHeld[heldName] = versions = new List<(string, string)>();
                        versions.Add((held.Name, heldStamp));
                    }
                }
                if (root.TryGetProperty("profiles", out JsonElement oldProfiles) && oldProfiles.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement one in oldProfiles.EnumerateArray())
                        if (one.ValueKind == JsonValueKind.String && one.GetString() is string s && !profilesSeen.Contains(s))
                            profilesSeen.Add(s);
                MergeInto(root, "art", art);
                MergeInto(root, "artPng", artPng);
                MergeInto(root, "artPngBySrc", artPngBySrc);
                MergeInto(root, "artSrc", artSrc);
                MergeDimensions(root, artDim);
                MergeWater(root, waterOut);
            }
            if (!profilesSeen.Contains(profileLabel))
                profilesSeen.Add(profileLabel);

            int fresh = 0, samePicture = 0;
            var toWrite = new Dictionary<string, Dictionary<string, object?>>();
            foreach ((string locName, Dictionary<string, object?> entry) in locations)
            {
                string stamp = VariantStamp(entry);
                string? key = null;
                if (alreadyHeld.TryGetValue(locName, out var versions))
                    foreach ((string heldKey, string heldStamp) in versions)
                        if (heldStamp == stamp) { key = heldKey; break; }
                if (key != null)
                {
                    // This exact map is already in the dump. Nothing to write; this profile just
                    // joins the list of the ones that produce it.
                    samePicture++;
                    if (index[key] is Dictionary<string, object?> heldRecord)
                        heldRecord["from"] = WithProfile(heldRecord.TryGetValue("from", out object? f) ? f : null, profileLabel);
                    continue;
                }
                // A name is only free the first time. Every later VERSION of one place carries the
                // stamp of what makes it different, so Town and Town~4b17e2 sit side by side and
                // neither has quietly become the other.
                key = locName;
                for (int n = 2; index.ContainsKey(key); n++)
                    key = n == 2 ? locName + "~" + stamp : locName + "~" + stamp + "-" + n;
                fresh++;
                index[key] = new Dictionary<string, object?>
                {
                    ["name"] = locName,
                    ["variant"] = stamp,
                    ["from"] = new List<string> { profileLabel },
                    ["outdoors"] = entry.TryGetValue("outdoors", out object? o) ? o : null,
                    ["cls"] = entry.TryGetValue("cls", out object? c) ? c : null,
                    ["locSeason"] = entry.TryGetValue("locSeason", out object? ls) ? ls : null,
                    ["sheets"] = entry.TryGetValue("sheets", out object? sh) ? sh : null,
                    // The DISTINCT (sheet, tile) pairs this location draws. The labeller answers
                    // "which maps contain lava" from the tiles a map uses, and that question is
                    // asked about every map at once - it cannot wait for each file to be opened.
                    // Distinct pairs are a fraction of the cell grid (a map repeats a few hundred
                    // tiles across thousands of cells), so this stays in the index while the grid
                    // itself does not.
                    ["used"] = DistinctCells(entry),
                    ["file"] = "maps/" + SafeFileName(key, usedLocationFiles) + ".json",
                };
                toWrite[key] = entry;
            }
            monitor.Log($"mapdump: {profileLabel} contributed {fresh} new map version(s); "
                        + $"{samePicture} were already in the dump. {index.Count} in total.", LogLevel.Info);

            // artPng is additive: a labeller that only knows `art` still works against an
            // embedded dump, and one that knows both prefers the files.
            var doc = new { format = "hf-mapdump-v3", season = Game1.currentSeason, profiles = profilesSeen, locations = index, art, artPng, artPngBySrc, artDim, artSrc, water = waterOut, animGroups };
            string json = JsonSerializer.Serialize(doc);

            // Primary target: Documents\HF-Studio. The mod folder lives under Program Files,
            // and Chrome's File System Access API refuses to open files in system folders
            // ("contains system files"), which broke HF Studio's auto-load/auto-reload.
            // Documents is user-writable AND FS-Access-pickable, so the dump goes there;
            // the mod-folder copy stays as a fallback for the plain <input> picker.
            string primary;
            try
            {
                string sdir = studioDir;
                Directory.CreateDirectory(sdir);
                WriteLocationFiles(sdir, toWrite, index, monitor);
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

            monitor.Log($"Dumped {locations.Count} locations + {art.Count + artPng.Count} sheet art"
                + (artPng.Count > 0 ? $" (as PNG files in {sheetDir})" : " (embedded)")
                + $" + {animGroups.Count} animation groups.", LogLevel.Info);
            monitor.Log(primary.Length > 0
                ? $"In HF Studio: click the map-dump button, pick:  {primary}"
                : $"In HF Studio: click the map-dump button, pick:  {modPath}", LogLevel.Info);
            return primary.Length > 0 ? primary : modPath;
        }

        /// <summary>True for a layer the game draws. The single source of truth is
        /// <see cref="MapLayers.CompositeRank"/>: a negative rank means a marker/logic layer or a
        /// non-numeric suffix, and those stay out. Delegating here (instead of duplicating the
        /// family test) keeps the dump's idea of a drawn layer identical to the mod's - negative
        /// suffixes included, which is how Gem Sea Shores' Buildings-1 (267 cells on Beach_West
        /// alone) stopped being dropped.</summary>
        private static bool IsRenderedLayer(string id) => MapLayers.CompositeRank(id) >= 0;

        /// <summary>
        /// How a tile is turned, as one byte: bit 0-1 = quarter turns clockwise, bit 2 = mirrored
        /// horizontally BEFORE the turn. 0 means a plain tile, which is almost all of them.
        /// <para>
        /// Delegates to <see cref="MapLayers.Orientation"/> so the dump and the mask can never
        /// read a turned tile two different ways. The dump used to keep its own copy of the
        /// property parse, and both copies shared the same blind spots (@Flip=2, @Rotation=-90);
        /// the full TMXTile translation table lives on the shared method.
        /// </para>
        /// </summary>
        private static byte ReadOrientation(Tile tile) => MapLayers.Orientation(tile);

        /// <summary>Record every frame of an animated tile as "&lt;sheet&gt;:&lt;index&gt;", deduplicated
        /// across the whole dump. A frame's own sheet is registered for art embedding too: a cycle
        /// can step onto a sheet no static cell references, and HF Studio needs its art to paint it.</summary>
        private static void RecordAnim(AnimatedTile anim, Dictionary<string, List<string>> artSources,
                                      HashSet<string> sigs, List<string[]> groups)
        {
            var frames = new List<string>();
            foreach (StaticTile f in anim.TileFrames)
            {
                if (f?.TileSheet == null)
                    continue;
                string sn = LabelStore.NormalizeSheet(f.TileSheet.ImageSource ?? f.TileSheet.Id);
                frames.Add(sn + ":" + f.TileIndex);
                if (f.TileSheet.ImageSource is { } src)
                    AddArtSource(artSources, sn, src);
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
        /// <summary>Where the labeller reads its dump from: Documents\HF-Studio. The mod folder
        /// sits under Program Files, and Chrome refuses to hand out a directory handle there.</summary>
        internal static string HfStudioDir()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HF-Studio");

        /// <summary>
        /// A sheet name turned into a file name Windows will accept AND keep distinct.
        /// <para>
        /// Sheet names are case-sensitive and Windows file names are not, so names that differ
        /// only in case land on one file and the last one written wins. That is not a theoretical
        /// clash: 15 pairs collided in a full dump, every one of them genuinely different art -
        /// HxW's "hime_outdoorfurniture_front" is the Muted recolour and "..._Front" is the
        /// Vanilla one, and Sunberry's Machines.png has nothing to do with Lumisteria's
        /// machines.png. A name already taken case-insensitively gets a ~2, ~3 ... suffix; the
        /// map in maps.json carries the real name, so nothing downstream has to guess.
        /// </para>
        /// </summary>
        /// <summary>Record one more content path under a sheet name, keeping discovery order and
        /// never listing the same path twice. The FIRST path recorded for a name is the one a
        /// loaded map placed, and the rest are the season siblings and the disk sweep, so order
        /// here is what decides which art the bare name resolves to.</summary>
        private static void AddArtSource(Dictionary<string, List<string>> sources, string name, string src)
        {
            if (!sources.TryGetValue(name, out List<string>? list))
                sources[name] = list = new List<string>();
            foreach (string had in list)
                if (string.Equals(had, src, StringComparison.OrdinalIgnoreCase))
                    return;
            list.Add(src);
        }

        /// <summary>
        /// The PNG file a sheet's art is written to: its name, plus a short hash of the content
        /// path it came from.
        /// <para>
        /// The hash is what makes two dumps agree. The old name was the sheet name with a ~2, ~3
        /// suffix handed out in the order the sheets happened to be met, so the same art landed in
        /// a different file depending on which mods were loaded, and merging two profiles' dumps
        /// mixed the art up rather than combining it. A name derived from the path alone is the
        /// same in every run, on every profile, whatever order things arrive in - and two genuinely
        /// different files under one sheet name now get one file each instead of overwriting.
        /// </para>
        /// </summary>
        /// <summary>The capitalisation this file really has on disk, given a name that matches it
        /// case-insensitively. Windows answers File.Exists without caring about case, so this is the
        /// only way to learn which spelling actually got written.</summary>
        private static string OnDiskName(string directory, string fileName)
        {
            try
            {
                foreach (string found in Directory.GetFiles(directory, fileName))
                    return Path.GetFileName(found);
            }
            catch (Exception)
            {
                // A directory that cannot be listed is not worth failing a dump over: the name we
                // were given is right on every filesystem but this exact clash.
            }
            return fileName;
        }

        private static string ArtFileName(string name, string src, byte[] png)
        {
            var sb = new System.Text.StringBuilder(name.Length + 18);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            // FNV-1a over the path, lowercased so a case-different spelling of one file agrees
            // with itself. Eight hex digits: collisions are a curiosity here, not a hazard.
            uint hash = 2166136261;
            foreach (char c in src.ToLowerInvariant())
                hash = (hash ^ c) * 16777619;
            // ...and then over the PICTURE. The path alone is not an identity: every profile
            // patches "Maps/spring_town" and every one of them meant a different town, so each
            // run wrote its art over the last run's under one name, and the labeller drew
            // whichever profile had dumped most recently for every map at once. Two mods that
            // ship the SAME bytes still share one file, which is the point - a recolour nobody
            // changed is not a second copy.
            return sb.Append('_').Append(hash.ToString("x8"))
                     .Append('_').Append(FnvOfBytes(png).ToString("x8")).ToString();
        }

        /// <summary>FNV-1a 32 over a byte run. Used for content identity, never for security.</summary>
        private static uint FnvOfBytes(byte[] bytes)
        {
            uint hash = 2166136261;
            foreach (byte b in bytes)
                hash = (hash ^ b) * 16777619;
            return hash;
        }

        /// <summary>
        /// Load a tilesheet by asset key through SMAPI's content view, so the art is what the game
        /// is really drawing rather than what shipped in the box. Content Patcher's recolours and
        /// map packs replace these assets, and reading them any other way hands the labeller the
        /// base game's picture for a sheet the maps were built against at a different size.
        /// </summary>
        private static Microsoft.Xna.Framework.Graphics.Texture2D LoadAsset(IModHelper helper, string src)
        {
            try { return helper.GameContent.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(src); }
            catch { return Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(src); }
        }


        /// <summary>The dump as it stands on disk, so a run can add to it rather than replace it.
        /// Null when there is nothing there yet, which is an ordinary first run.</summary>
        private static JsonDocument? ReadExistingDump(string studioDir, IMonitor monitor)
        {
            string path = Path.Combine(studioDir, "maps.json");
            if (!File.Exists(path))
                return null;
            try { return JsonDocument.Parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                // A dump that cannot be read must not be silently thrown away: it is hours of
                // profile switching, and the copy costs nothing next to losing it.
                string keep = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(path, keep); } catch { /* then it stays and gets overwritten */ }
                monitor.Log($"mapdump: the dump already there could not be read ({ex.Message}); "
                            + $"kept as {Path.GetFileName(keep)} and this run starts fresh.", LogLevel.Warn);
                return null;
            }
        }

        /// <summary>Carry an existing string map forward. What is already there WINS: art files are
        /// named by their contents, so a name that already points somewhere points at the right
        /// bytes, and keeping the first answer means the base game holds the bare names.</summary>
        private static void MergeInto(JsonElement root, string field, Dictionary<string, string> into)
        {
            if (!root.TryGetProperty(field, out JsonElement map) || map.ValueKind != JsonValueKind.Object)
                return;
            foreach (JsonProperty one in map.EnumerateObject())
                if (one.Value.ValueKind == JsonValueKind.String && one.Value.GetString() is string value)
                    into[one.Name] = value;
        }

        /// <inheritdoc cref="MergeInto"/>
        private static void MergeDimensions(JsonElement root, Dictionary<string, int[]> into)
        {
            if (!root.TryGetProperty("artDim", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
                return;
            foreach (JsonProperty one in map.EnumerateObject())
            {
                if (one.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var size = new List<int>(2);
                foreach (JsonElement n in one.Value.EnumerateArray())
                    if (n.TryGetInt32(out int v))
                        size.Add(v);
                if (size.Count == 2)
                    into[one.Name] = size.ToArray();
            }
        }

        /// <summary>Water ground truth is a UNION, not a replacement: a tile the game called water
        /// under one profile is still water, whether or not this profile loaded the map it was in.</summary>
        private static void MergeWater(JsonElement root, Dictionary<string, int[]> into)
        {
            if (!root.TryGetProperty("water", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
                return;
            foreach (JsonProperty one in map.EnumerateObject())
            {
                if (one.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var union = new HashSet<int>(into.TryGetValue(one.Name, out int[]? had) ? had : Array.Empty<int>());
                foreach (JsonElement n in one.Value.EnumerateArray())
                    if (n.TryGetInt32(out int v))
                        union.Add(v);
                var arr = new int[union.Count];
                union.CopyTo(arr);
                Array.Sort(arr);
                into[one.Name] = arr;
            }
        }

        /// <summary>Add this run's profile to a location's list of the profiles that produce it,
        /// without repeating one that is already there.</summary>
        private static List<string> WithProfile(object? existing, string profile)
        {
            var names = new List<string>();
            if (existing is JsonElement el && el.ValueKind == JsonValueKind.Array)
                foreach (JsonElement one in el.EnumerateArray())
                    if (one.ValueKind == JsonValueKind.String && one.GetString() is string s)
                        names.Add(s);
            else if (existing is List<string> had)
                names.AddRange(had);
            if (!names.Contains(profile))
                names.Add(profile);
            return names;
        }

        /// <summary>What makes one version of a location different from another: the cells it
        /// draws, and the art files it draws them from. Two profiles that agree on both are
        /// looking at the same map and it is only stored once.</summary>
        private static string VariantStamp(Dictionary<string, object?> entry)
        {
            ulong hash = 14695981039346656037UL;
            void Feed(string? text)
            {
                if (text != null)
                    foreach (char c in text)
                        hash = (hash ^ c) * 1099511628211UL;
                hash = (hash ^ '\n') * 1099511628211UL;   // a separator, so "ab"+"c" != "a"+"bc"
            }
            if (entry.TryGetValue("layers", out object? layersObj) && layersObj is List<object> layers)
                foreach (object layer in layers)
                    Feed(layer.GetType().GetProperty("cells")?.GetValue(layer) as string);
            if (entry.TryGetValue("sheetArt", out object? artObj) && artObj is List<string?> files)
                foreach (string? one in files)
                    Feed(one);
            return hash.ToString("x16").Substring(0, 6);
        }

        private static string SafeFileName(string name, HashSet<string> used)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            string baseName = sb.ToString();
            string candidate = baseName;
            for (int n = 2; !used.Add(candidate.ToLowerInvariant()); n++)
                candidate = baseName + "~" + n;
            return candidate;
        }

        /// <summary>Every distinct cell value a location draws, across all its rendered layers.
        /// The cell encoding is the dump's own: sheetIndex * 0x100000 + tileIndex, -1 = empty.</summary>
        private static int[] DistinctCells(Dictionary<string, object?> entry)
        {
            var seen = new HashSet<int>();
            if (entry.TryGetValue("layers", out object? lo) && lo is List<object> layers)
                foreach (object layer in layers)
                {
                    // The layer objects are anonymous types built in PackRenderedLayers, so the
                    // cells come back out through reflection rather than a cast.
                    object? cellsObj = layer.GetType().GetProperty("cells")?.GetValue(layer);
                    if (cellsObj is not string b64)
                        continue;
                    byte[] bytes = Convert.FromBase64String(b64);
                    for (int i = 0; i + 3 < bytes.Length; i += 4)
                    {
                        int v = BitConverter.ToInt32(bytes, i);
                        if (v >= 0)
                            seen.Add(v);
                    }
                }
            var arr = new int[seen.Count];
            seen.CopyTo(arr);
            Array.Sort(arr);
            return arr;
        }

        /// <summary>Write one file per location under <c>maps/</c>, named by the index. Whatever the
        /// index says a location's file is called is what gets written, so the two cannot drift.
        /// Best effort per file: one location that fails to write must not lose the other 2,900.</summary>
        private static void WriteLocationFiles(string studioDir, Dictionary<string, Dictionary<string, object?>> locations,
                                               Dictionary<string, object?> index, IMonitor monitor)
        {
            string dir = Path.Combine(studioDir, "maps");
            Directory.CreateDirectory(dir);
            int written = 0, failed = 0;
            foreach ((string name, Dictionary<string, object?> entry) in locations)
            {
                if (index[name] is not Dictionary<string, object?> idx || idx["file"] is not string rel)
                    continue;
                try
                {
                    File.WriteAllText(Path.Combine(studioDir, rel.Replace('/', Path.DirectorySeparatorChar)),
                                      JsonSerializer.Serialize(entry));
                    written++;
                }
                catch (Exception ex)
                {
                    failed++;
                    monitor.Log($"mapdump: could not write {rel}: {ex.Message}", LogLevel.Trace);
                }
            }
            monitor.Log($"mapdump: {written} location files in {dir}"
                        + (failed > 0 ? $" ({failed} failed)" : ""), LogLevel.Trace);
        }

        /// <summary>A PNG the content pipeline has never heard of, read straight off disk.</summary>
        private static Microsoft.Xna.Framework.Graphics.Texture2D LoadFromDisk(string path)
        {
            using var fs = File.OpenRead(path);
            return Microsoft.Xna.Framework.Graphics.Texture2D.FromStream(Game1.graphics.GraphicsDevice, fs);
        }

        private static void AddUnplacedSheetArt(IModHelper helper, IMonitor monitor, Dictionary<string, List<string>> artPaths)
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
                    AddArtSource(artPaths, name, file);
                    added++;
                }
            }
            monitor.Log($"mapdump: {added} tilesheet(s) found on disk that no loaded map places.", LogLevel.Info);
        }

        private static Dictionary<string, object?>? DumpLocation(GameLocation location, Dictionary<string, List<string>> artSources, Dictionary<string, HashSet<int>> water,
                                           HashSet<string> animSigs, List<string[]> animGroups)
        {
            xTile.Map? map = location.Map;
            if (map == null || map.TileSheets.Count == 0)
                return null;

            var sheetIndex = new Dictionary<TileSheet, int>();
            var sheets = new List<string>();
            var sheetRefs = new List<TileSheet>();
            // The full path of each sheet, and how many tiles across and down the MAP believes it
            // is. Both are needed to draw a cell correctly and neither could be recovered from the
            // bare name: the name cannot say which of several same-named files this map places,
            // and the art's own pixel size is not the layout the map indexes against - a recolour
            // that ships a taller sheet than the one a map was built for shifts every index past
            // the first row if the width is read off the picture.
            var sheetSrc = new List<string?>();
            var sheetWH = new List<int[]>();
            foreach (TileSheet ts in map.TileSheets)
            {
                sheetIndex[ts] = sheets.Count;
                sheets.Add(LabelStore.NormalizeSheet(ts.ImageSource ?? ts.Id));
                sheetSrc.Add(ts.ImageSource);
                sheetWH.Add(new[] { ts.SheetWidth, ts.SheetHeight });
                sheetRefs.Add(ts);
            }
            var used = new bool[sheets.Count];

            List<object> layers = PackRenderedLayers(location, map, sheetIndex, sheets, used,
                                                     artSources, water, animSigs, animGroups);
            if (layers.Count == 0)
                return null;
            for (int i = 0; i < sheets.Count; i++)
                if (used[i] && sheetRefs[i].ImageSource is { } src)
                    AddArtSource(artSources, sheets[i], src);

            // --- V4 ground-truth extras (additive; HF Studio builds that predate them ignore them) ---

            // Per-location water grid straight from the game's own baked array, because the
            // per-sheet 'water' map above cannot express two things: WHERE the water is on this
            // map, and Water="I" tiles (water for gameplay that the game never draws an overlay
            // on — waterfall bases, decorative edges). wgrid = row-major bitmask of isWater;
            // wI = packed y*w+x indices of the invisible subset.
            (string? wgrid, List<int> wI) = PackWaterGrid(location);

            // One property sweep, four packed index lists (y*w+x):
            //   wBld  = "Water" on Buildings — fishable-under-bridge, NOT in waterTiles, never mirror
            //   wSrc  = "WaterSource" on Back — watering-can refill, must NOT be treated as water
            //   pBld  = "Passable" on Buildings — bridge planks etc., occluders that stay walkable
            //   noFish= "NoFishing" on Back — water where fishing is off (festival edges etc.)
            (List<int> wBld, List<int> wSrc, List<int> pBld, List<int> noFish) = PackTileProperties(location, map);

            // Every building = a potential occluder (footprint + sprite height feed the V4
            // reflection height gate). Fish ponds additionally draw their own water
            // (World_Sorted pass, not drawWater) on the interior 3x3 of a 5x5 footprint,
            // tinted per FishPondData — none of it visible to waterTiles.
            (List<object> buildings, List<object> fishPonds) = PackBuildings(location);

            // Raw map properties (indoorWater, ambient sounds, custom framework flags ...) and
            // the location's own class + per-location season (Ginger Island stays summer).
            (Dictionary<string, string> mapProps, string? locSeason) = PackMapMetadata(location, map);

            var wc = location.waterColor.Value;
            var layersAll = new List<string>();
            foreach (Layer l in map.Layers)
                layersAll.Add(l.Id);

            // A DICTIONARY rather than an anonymous type, and in the same key order, so the JSON is
            // byte for byte what it was. The split writer has to read `sheets` and `outdoors` back
            // out to build the index, and an anonymous type can only be read by serialising it.
            return new Dictionary<string, object?>
            {
                ["outdoors"] = location.IsOutdoors, ["sheets"] = sheets,
                ["sheetSrc"] = sheetSrc, ["sheetWH"] = sheetWH, ["layers"] = layers,
                ["cls"] = location.GetType().FullName,
                ["locSeason"] = locSeason,
                ["waterColor"] = new[] { (int)wc.R, (int)wc.G, (int)wc.B, (int)wc.A },
                ["indoorWater"] = location.HasMapPropertyWithValue("indoorWater"),
                ["mapProps"] = mapProps.Count > 0 ? mapProps : null,
                ["layersAll"] = layersAll,
                ["wgrid"] = wgrid,
                ["wI"] = wI.Count > 0 ? wI.ToArray() : null,
                ["wBld"] = wBld.Count > 0 ? wBld.ToArray() : null,
                ["wSrc"] = wSrc.Count > 0 ? wSrc.ToArray() : null,
                ["pBld"] = pBld.Count > 0 ? pBld.ToArray() : null,
                ["noFish"] = noFish.Count > 0 ? noFish.ToArray() : null,
                ["buildings"] = buildings.Count > 0 ? buildings : null,
                ["fishPonds"] = fishPonds.Count > 0 ? fishPonds : null,
            };
        }

        /// <summary>Pack every layer the game actually renders into the dump: its cells, its
        /// animated cells, and the per-cell orientation when anything on it is turned.</summary>
        private static List<object> PackRenderedLayers(GameLocation location, xTile.Map map,
                                                       Dictionary<TileSheet, int> sheetIndex, List<string> sheets,
                                                       bool[] used, Dictionary<string, List<string>> artSources,
                                                       Dictionary<string, HashSet<int>> water,
                                                       HashSet<string> animSigs, List<string[]> animGroups)
        {
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
                // Per-cell orientation. A .tmx keeps flip/rotate in the gid's top bits, and the
                // loader that brings the map in cannot put them in the tile index, so they land in
                // the tile's PROPERTY bag as @Flip / @Rotation. Nothing here ever read them, so the
                // dump described a mirrored tile as a plain one and the preview drew the waterfall
                // pieces facing the wrong way - the "HF assembles it wrong" reports. Kept as a
                // SIDE ARRAY rather than packed into the cell int: additive, so a labeler that has
                // not learned about it still reads the map, and no risk to the existing encoding.
                byte[] orient = new byte[w * h];
                bool layerHasOrient = false;
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
                            // The orientation lives on the ANIMATED tile, not on its frames.
                            byte oA = ReadOrientation(t);
                            if (oA != 0) { orient[y * w + x] = oA; layerHasOrient = true; }
                            t = anim.TileFrames[0];   // deterministic: first frame
                        }
                        else if (t != null)
                        {
                            byte o = ReadOrientation(t);
                            if (o != 0) { orient[y * w + x] = o; layerHasOrient = true; }
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
                // fam + ord publish the game's bottom-to-top order with the layer so the labeler
                // stops GUESSING it from the name. ord is the same CompositeRank the mod sorts by,
                // so a map that numbers its layers oddly can never compose one way in the preview
                // and another in the water mask. Both are additive: an older labeler simply
                // ignores them and keeps its own fallback.
                bool hasFam = MapLayers.TryGetFamily(layer.Id, out string fam);
                int ord = MapLayers.CompositeRank(layer.Id);
                layers.Add(new
                {
                    id = layer.Id, fam = hasFam ? fam : null, ord, w, h,
                    cells = Convert.ToBase64String(bytes),
                    anim = animCells.Count > 0 ? animCells.ToArray() : null,
                    // Only when something on this layer is actually turned: most layers add nothing.
                    orient = layerHasOrient ? Convert.ToBase64String(orient) : null,
                });
            }
            return layers;
        }

        /// <summary>The game's own baked water array, as a bitmask plus the indices of the
        /// invisible subset.</summary>
        private static (string? Grid, List<int> Invisible) PackWaterGrid(GameLocation location)
        {
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
            return (wgrid, wI);
        }

        /// <summary>One sweep of the map for the four tile properties the labeler needs, each
        /// as a packed index list.</summary>
        private static (List<int> WaterOnBuildings, List<int> WaterSource, List<int> PassableBuildings, List<int> NoFishing)
            PackTileProperties(GameLocation location, xTile.Map map)
        {
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
            return (wBld, wSrc, pBld, noFish);
        }

        /// <summary>Buildings as occluders, and the fish ponds among them, which draw water of
        /// their own that the game's water array never sees.</summary>
        private static (List<object> All, List<object> FishPonds) PackBuildings(GameLocation location)
        {
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
            return (buildings, fishPonds);
        }

        /// <summary>The raw map properties and the location's own season, both of which can
        /// throw on a malformed map and are worth nothing rather than everything.</summary>
        private static (Dictionary<string, string> Props, string? Season) PackMapMetadata(GameLocation location, xTile.Map map)
        {
            var mapProps = new Dictionary<string, string>();
            try
            {
                foreach (var kv in map.Properties)
                    mapProps[kv.Key] = kv.Value?.ToString() ?? "";
            }
            catch { }
            string? locSeason = null;
            try { locSeason = location.GetSeason().ToString(); } catch { }
            return (mapProps, locSeason);
        }
    }
}
