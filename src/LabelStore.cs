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
    /// </summary>
    internal sealed class LabelStore
    {
        private readonly Dictionary<string, Dictionary<int, byte[]>> _sheets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Set once during Entry; null only if construction somehow failed.</summary>
        public static LabelStore? Instance;

        public int SheetCount => _sheets.Count;
        public int TileCount { get; private set; }
        public bool Any => _sheets.Count > 0;

        /// <summary>Cache key for consumers. Load-once means this never changes after startup —
        /// it exists so an empty DB (0) is distinguishable from a loaded one (1).</summary>
        public int Version => _sheets.Count > 0 ? 1 : 0;

        public LabelStore(string dir, IMonitor monitor)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try { Load(file); }
                catch (Exception ex) { monitor.Log($"Bad label file {Path.GetFileName(file)}: {ex.Message}", LogLevel.Warn); }
            }
        }

        /// <summary>Accepts both HF Studio shapes: export-all {sheets:{name:{tiles}}} and per-sheet {sheet,tiles}.</summary>
        private void Load(string path)
        {
            using FileStream fs = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("sheets", out JsonElement sheets))
            {
                foreach (JsonProperty sheet in sheets.EnumerateObject())
                    LoadSheet(sheet.Name, sheet.Value);
            }
            else if (root.TryGetProperty("sheet", out JsonElement nameEl))
            {
                LoadSheet(nameEl.GetString() ?? "", root);
            }
        }

        private void LoadSheet(string name, JsonElement sheetEl)
        {
            if (string.IsNullOrEmpty(name) || !sheetEl.TryGetProperty("tiles", out JsonElement tiles))
                return;
            var map = new Dictionary<int, byte[]>();
            foreach (JsonProperty tile in tiles.EnumerateObject())
            {
                if (!int.TryParse(tile.Name, out int idx))
                    continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(tile.Value.GetString() ?? ""); }
                catch (FormatException) { continue; }
                if (bytes.Length == 256)
                    map[idx] = bytes;
            }
            // A whole sheet is replaced, never merged: two files describing one sheet would
            // otherwise blend a stale pass into the current one depending on directory order.
            if (_sheets.TryGetValue(NormalizeSheet(name), out var prev))
                TileCount -= prev.Count;
            _sheets[NormalizeSheet(name)] = map;
            TileCount += map.Count;
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
                && _sheets.TryGetValue(NormalizeSheet(imageSource), out var tiles)
                && tiles.TryGetValue(tileIndex, out byte[]? bytes) ? bytes : null;

        /// <summary>Per-pixel classes for the art a layer draws at a tile, or null if unlabeled.
        /// Takes the Layer directly — the mask gather already holds Back/Buildings/Front, so
        /// looking them up by name once per tile would be pure overhead.</summary>
        public byte[]? Get(Layer? layer, int x, int y)
            => _sheets.Count > 0 && TryTileArt(layer, x, y, out string? sheet, out int index)
                ? Get(sheet, index)
                : null;

        public byte[]? Get(GameLocation? loc, int x, int y, string layerName)
            => _sheets.Count > 0 ? Get(loc?.map?.GetLayer(layerName), x, y) : null;

        /// <summary>Frame 0 of an animated tile is the frame labels are keyed to (HF Studio fans a
        /// stroke out to every frame of a cycle, so any frame resolves to the same marks).</summary>
        private static bool TryTileArt(Layer? layer, int x, int y, out string? sheet, out int index)
        {
            sheet = null;
            index = -1;
            if (layer == null || x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight)
                return false;
            var t = layer.Tiles[x, y];
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
