using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using xTile.Layers;

namespace SDVRadiance
{
    /// <summary>
    /// Hand-painted ground truth for what map art actually IS, per tilesheet tile: 256 bytes of
    /// per-pixel classes (0 ground · 1 water · 9 ice · 10 falling/fast water · 11 lava ·
    /// 12 window). Painted in HF Studio and SHIPPED WITH THIS MOD under <c>labels/</c>.
    ///
    /// Read ONCE at startup and then never touched again: this is versioned data that changes
    /// when the mod updates, not live state, so there is no file watching and no per-frame or
    /// per-second disk work. Editing labels means shipping a new build.
    ///
    /// Labels attach to a TILESHEET tile, not a map coordinate, so one painted tile covers every
    /// place in the game that draws it — which is why a few thousand tiles cover 395 locations.
    ///
    /// Other mods may add their own. A <c>radiance-labels.json</c> inside a mod folder is loaded
    /// after the bundled labels and may only paint sheets that mod actually supplies; see
    /// <see cref="LabelPacks"/> for why ownership is decided by where the art is rather than by
    /// what the pack says. Nothing about the bundled path changes when no pack is installed.
    /// </summary>
    internal sealed class LabelStore
    {
        private readonly Dictionary<string, Dictionary<int, byte[]>> _tilesBySheet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Which file painted each sheet, so a conflict is answerable from a bug report.</summary>
        private readonly Dictionary<string, string> _sourceBySheet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>One line per pack that was loaded or refused, for radiance_report.</summary>
        private readonly List<string> _packReport = new();

        /// <summary>Set once during Entry; null only if construction somehow failed.</summary>
        public static LabelStore? Instance;

        public int SheetCount => _tilesBySheet.Count;
        public int TileCount { get; private set; }
        public bool Any => _tilesBySheet.Count > 0;

        /// <summary>Cache key for consumers. Load-once means this never changes after startup —
        /// it exists so an empty DB (0) is distinguishable from a loaded one (1).</summary>
        public int Version => _tilesBySheet.Count > 0 ? 1 : 0;

        public LabelStore(string dir, IMonitor monitor)
            : this(dir, Array.Empty<LabelPack>(), monitor)
        {
        }

        /// <summary>
        /// The bundled labels first, then each mod's pack in the order it was discovered. Order is
        /// what decides a collision, so it is fixed and reported rather than left to the filesystem.
        /// </summary>
        public LabelStore(string dir, IReadOnlyList<LabelPack> packs, IMonitor monitor)
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try { Load(file, "labels/" + Path.GetFileName(file), owned: null, monitor); }
                    catch (Exception ex) { monitor.Log($"Bad label file {Path.GetFileName(file)}: {ex.Message}", LogLevel.Warn); }
                }
            }

            foreach (LabelPack pack in packs)
            {
                int before = this.TileCount;
                _refusedInPack = 0;
                try
                {
                    Load(pack.FilePath, pack.Describe(), pack.OwnedSheets, monitor);
                }
                catch (Exception ex)
                {
                    // Fail closed: a pack that cannot be read leaves the bundled labels exactly as
                    // they were, because half a pack is worse than none of it.
                    monitor.Log($"Bad label pack {pack.Describe()}: {ex.Message}", LogLevel.Warn);
                    _packReport.Add($"{pack.Describe()}: unreadable ({ex.Message})");
                    continue;
                }

                string producedFor = pack.ProducedFor == null ? "" : $", made for \"{pack.ProducedFor}\"";
                string refused = _refusedInPack == 0
                    ? ""
                    : $", {_refusedInPack} sheet(s) refused as not this mod's own art";
                _packReport.Add($"{pack.Describe()}: {this.TileCount - before:+0;-0;0} tiles{refused}{producedFor}");
                this.PackCount++;
            }
        }

        /// <summary>How many sheets a pack asked to paint that the owning mod does not supply.</summary>
        private int _refusedInPack;

        /// <summary>Label packs from other mods that were loaded. Zero for a normal install.</summary>
        public int PackCount { get; private set; }

        /// <summary>Accepts both HF Studio shapes: export-all {sheets:{name:{tiles}}} and per-sheet {sheet,tiles}.</summary>
        private void Load(string path, string sourceName, IReadOnlySet<string>? owned, IMonitor monitor)
        {
            using FileStream fs = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("sheets", out JsonElement sheets))
            {
                foreach (JsonProperty sheet in sheets.EnumerateObject())
                    LoadSheet(sheet.Name, sheet.Value, sourceName, owned, monitor);
            }
            else if (root.TryGetProperty("sheet", out JsonElement nameEl))
            {
                LoadSheet(nameEl.GetString() ?? "", root, sourceName, owned, monitor);
            }
        }

        private void LoadSheet(string name, JsonElement sheetEl, string sourceName,
                               IReadOnlySet<string>? owned, IMonitor monitor)
        {
            if (string.IsNullOrEmpty(name) || !sheetEl.TryGetProperty("tiles", out JsonElement tiles))
                return;

            // The ownership rule. A pack (owned != null) paints only art its own mod supplies, so
            // that installing somebody's label pack cannot silently repaint vanilla water, or
            // anybody else's, for every player. The bundled labels pass null and may paint anything.
            if (owned != null && !owned.Contains(NormalizeSheet(name)))
            {
                _refusedInPack++;
                monitor.Log($"{sourceName} paints \"{name}\", which is not art that mod supplies. Ignored.", LogLevel.Warn);
                return;
            }
            var map = new Dictionary<int, byte[]>();
            foreach (JsonProperty tile in tiles.EnumerateObject())
            {
                if (!int.TryParse(tile.Name, out int idx))
                    continue;
                byte[] bytes;
                // Anything malformed skips that tile and nothing else. FormatException alone was
                // not enough: a tile whose value is a number or an object throws
                // InvalidOperationException out of GetString, which escaped Load and left the
                // store half-patched, which is the opposite of what the comment below promises.
                // These files come from other people now, so "one bad tile" has to stay one bad
                // tile.
                try { bytes = Convert.FromBase64String(tile.Value.GetString() ?? ""); }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException) { continue; }
                if (bytes.Length == 256)
                    map[idx] = bytes;
            }
            // A whole sheet is replaced, never merged: two files describing one sheet would
            // otherwise blend a stale pass into the current one depending on directory order.
            string key = NormalizeSheet(name);
            if (_tilesBySheet.TryGetValue(key, out var prev))
            {
                TileCount -= prev.Count;
                // Two packs owning one sheet name is a real conflict and the second one wins, which
                // is only defensible if it is said out loud. Replacing the bundled labels is the
                // point of the feature and is reported at Trace instead.
                if (_sourceBySheet.TryGetValue(key, out string? was) && was != sourceName)
                {
                    monitor.Log($"\"{key}\" was painted by {was} and is now painted by {sourceName}.",
                        was.StartsWith("labels/", StringComparison.Ordinal) ? LogLevel.Trace : LogLevel.Warn);
                }
            }
            _tilesBySheet[key] = map;
            _sourceBySheet[key] = sourceName;
            TileCount += map.Count;
        }

        /// <summary>
        /// What painted what, for radiance_report. A user reporting that one mod's water looks wrong
        /// can be asked for this and it names the file, which is the whole reason it is kept.
        /// </summary>
        public string DescribeSources()
        {
            if (this.PackCount == 0 && _packReport.Count == 0)
                return "bundled only";
            var lines = new List<string> { $"bundled + {this.PackCount} pack(s) from other mods:" };
            foreach (string line in _packReport)
                lines.Add("  " + line);
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>"Maps\spring_beach" / "Maps/spring_beach.png" → "spring_beach".</summary>
        internal static string NormalizeSheet(string imageSource)
        {
            string name = imageSource.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name[(slash + 1)..];
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            return name;
        }

        public byte[]? Get(string? imageSource, int tileIndex)
            => imageSource != null
                && _tilesBySheet.TryGetValue(NormalizeSheet(imageSource), out var tiles)
                && tiles.TryGetValue(tileIndex, out byte[]? bytes) ? bytes : null;

        /// <summary>Per-pixel classes for the art a layer draws at a tile, or null if unlabeled.
        /// Takes the Layer directly — the mask gather already holds Back/Buildings/Front, so
        /// looking them up by name once per tile would be pure overhead.</summary>
        public byte[]? Get(Layer? layer, int x, int y)
        {
            if (_tilesBySheet.Count == 0
                || !TryTileArt(layer, x, y, out string? sheet, out int index, out byte orient))
                return null;
            // Labels are painted on the sheet, upright. The map may place the tile mirrored or
            // turned, so the marks have to be turned the same way before they can be compared with
            // anything on screen - otherwise a mirrored waterfall's liquid pixels sit on the wrong
            // side of the tile and the mask disagrees with the art by exactly that reflection.
            byte[]? bytes = Get(sheet, index);
            return bytes == null ? null : MapLayers.Orient(bytes, orient);
        }

        public byte[]? Get(GameLocation? location, int x, int y, string layerName)
            => _tilesBySheet.Count > 0 ? Get(location?.map?.GetLayer(layerName), x, y) : null;

        /// <summary>Frame 0 of an animated tile is the frame labels are keyed to (HF Studio fans a
        /// stroke out to every frame of a cycle, so any frame resolves to the same marks).</summary>
        private static bool TryTileArt(Layer? layer, int x, int y, out string? sheet, out int index)
            => TryTileArt(layer, x, y, out sheet, out index, out _);

        private static bool TryTileArt(Layer? layer, int x, int y, out string? sheet, out int index,
                                       out byte orient)
        {
            sheet = null;
            index = -1;
            orient = 0;
            if (layer == null || x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight)
                return false;
            var t = layer.Tiles[x, y];
            // The turn is on the tile the MAP holds, not on an animation frame inside it.
            orient = MapLayers.Orientation(t);
            if (t is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                t = at.TileFrames[0];
            if (t?.TileSheet == null)
                return false;
            sheet = t.TileSheet.ImageSource;
            index = t.TileIndex;
            return true;
        }
    }
}
