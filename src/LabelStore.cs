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

        /// <summary>The file name, inside labels/, that carries art fingerprints rather than
        /// labels. Held apart by name because the label loader would otherwise try to read it as
        /// paint and quietly find nothing.</summary>
        internal const string ArtFingerprintFileName = "art-fingerprints.json";

        /// <summary>The file, inside labels/, holding labels painted for art that is NOT the base
        /// game's. Kept apart from the painted DB because HF Studio exports one sheet per name and
        /// has no way to say "this one is for a different picture"; that is what a variant is.</summary>
        internal const string ArtVariantFileName = "art-variants.json";

        /// <summary>One label painted for one specific set of art.</summary>
        private readonly struct LabelVariant
        {
            /// <summary>Every art fingerprint this label was painted for. A list, because a mod
            /// with four palettes repaints a window without moving it: four pictures, one correct
            /// label, and painting it four times would be four chances to paint it differently.</summary>
            public readonly ulong[] Art;
            public readonly byte[] Label;
            public readonly string Source;
            public LabelVariant(ulong[] art, byte[] label, string source)
            { Art = art; Label = label; Source = source; }
        }

        /// <summary>Sheet -> tile -> the labels painted for art other than the shipped one.</summary>
        private readonly Dictionary<string, Dictionary<int, List<LabelVariant>>> _variantsBySheet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many tiles were drawn from a variant rather than the base label, by the
        /// name of whoever painted it. Reported, because a variant that never matches anything is
        /// indistinguishable from one that was never installed.</summary>
        private readonly Dictionary<string, int> _variantHits = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> VariantHits => _variantHits;

        /// <summary>Sheet name -> tile index -> every art fingerprint the shipped label for that
        /// tile was painted on. A tile absent from here is unguarded and behaves as it always
        /// has, so shipping this for one sheet at a time changes nothing about the rest.</summary>
        private readonly Dictionary<string, Dictionary<int, ulong[]>> _artBySheet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The resolved label per (sheet, tile): the shipped one where the art agrees,
        /// a glass-free copy of it where it does not. Memoised because the mask gather asks about
        /// every tile of the map, and the art behind one tile index cannot change without an asset
        /// reload, which clears this.</summary>
        private readonly Dictionary<(string sheet, int index), byte[]> _artVerdict = new();

        /// <summary>How many labelled tiles have had their GLASS taken back out because the art is
        /// not the art it was painted on. Reported by radiance_report: "my window reflections are
        /// missing" and "I am running an art mod this has no labels for" are the same sentence,
        /// and a player cannot be expected to work that out unaided.</summary>
        public int GlassTilesRefusedForChangedArt { get; private set; }

        /// <summary>Which sheets those refusals were on, and how many each. A count on its own
        /// says something is wrong; the sheet names are what let the report go on to name the
        /// packs that repaint them.</summary>
        private readonly Dictionary<string, int> _refusedBySheet = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> RefusedBySheet => _refusedBySheet;

        /// <summary>Reads the fingerprint of the art a map tile actually draws. Supplied by the
        /// render pipeline, which already holds the tilesheet pixels; LabelStore has no business
        /// touching the graphics card itself. Null before the pipeline exists, which is treated as
        /// "cannot tell" rather than as a mismatch.</summary>
        internal delegate bool TileArtFingerprintReader(Layer layer, int x, int y, out ulong fingerprint);
        internal static TileArtFingerprintReader? ArtFingerprintReader;

        /// <summary>Throw away every art verdict, because a mod reloaded a tilesheet and the answer
        /// may have changed. Moves <see cref="Version"/>, which is what makes the window panes and
        /// every other consumer keyed on it rebuild.</summary>
        public void ForgetArtVerdicts()
        {
            if (_artVerdict.Count == 0 && this.GlassTilesRefusedForChangedArt == 0)
                return;
            _artVerdict.Clear();
            _refusedBySheet.Clear();
            _variantHits.Clear();
            this.GlassTilesRefusedForChangedArt = 0;
            _artVerdictGeneration++;
        }

        /// <summary>One line per pack that was loaded or refused, for radiance_report.</summary>
        private readonly List<string> _packReport = new();

        /// <summary>Set once during Entry; null only if construction somehow failed.</summary>
        public static LabelStore? Instance;

        public int SheetCount => _tilesBySheet.Count;
        public int TileCount { get; private set; }
        public bool Any => _tilesBySheet.Count > 0;

        /// <summary>Every sheet name the store holds a label for. Read-only, and read by the
        /// fingerprint generator, which has to go and ask the game for the art behind each one:
        /// the store knows what was painted, only the game knows what is loaded.</summary>
        public IReadOnlyCollection<string> LabelledSheetNames => _tilesBySheet.Keys;

        /// <summary>The tile indices painted on one sheet, or null when the sheet is unknown.</summary>
        public IReadOnlyCollection<int>? LabelledTileIndices(string sheetName)
            => _tilesBySheet.TryGetValue(NormalizeSheet(sheetName), out var tiles) ? tiles.Keys : null;

        /// <summary>Cache key for consumers. The labels themselves are load-once, but the ART they
        /// were painted on is not: a mod can reload a tilesheet mid-session and change which of
        /// them still apply, so this moves when the art verdicts are thrown away. Zero still means
        /// an empty store, so a caller can tell that apart from a loaded one.</summary>
        public int Version => _tilesBySheet.Count > 0 ? 1 + _artVerdictGeneration : 0;
        private int _artVerdictGeneration;

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
                    if (string.Equals(Path.GetFileName(file), ArtFingerprintFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadArtFingerprints(file, monitor); }
                        catch (Exception ex) { monitor.Log($"Bad art fingerprint file: {ex.Message}", LogLevel.Warn); }
                        continue;
                    }
                    if (string.Equals(Path.GetFileName(file), ArtVariantFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadArtVariants(file, monitor); }
                        catch (Exception ex) { monitor.Log($"Bad art variant file: {ex.Message}", LogLevel.Warn); }
                        continue;
                    }
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

        /// <summary>
        /// Read <c>labels/art-fingerprints.json</c>: sheet name, then tile index, then the
        /// fingerprints of the art each shipped label was painted on.
        /// </summary>
        /// <remarks>
        /// A list rather than one value per tile, because one label is right for several pictures
        /// more often than not: the four seasonal town sheets share most of their tiles byte for
        /// byte, and an art mod that repaints one building leaves every other tile exactly as it
        /// found it. A tile listed here with no fingerprint anybody recognises is a tile whose
        /// label will not be handed out, so an empty list is never written.
        /// </remarks>
        private void LoadArtFingerprints(string file, IMonitor monitor)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("sheets", out JsonElement sheets))
                return;
            int tiles = 0;
            foreach (JsonProperty sheet in sheets.EnumerateObject())
            {
                var byTile = new Dictionary<int, ulong[]>();
                foreach (JsonProperty tile in sheet.Value.EnumerateObject())
                {
                    if (!int.TryParse(tile.Name, out int index))
                        continue;
                    var painted = new List<ulong>();
                    if (tile.Value.ValueKind == JsonValueKind.String)
                        AddFingerprint(painted, tile.Value.GetString());
                    else if (tile.Value.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement one in tile.Value.EnumerateArray())
                            AddFingerprint(painted, one.ValueKind == JsonValueKind.String ? one.GetString() : null);
                    if (painted.Count > 0)
                    {
                        byTile[index] = painted.ToArray();
                        tiles++;
                    }
                }
                if (byTile.Count > 0)
                    _artBySheet[NormalizeSheet(sheet.Name)] = byTile;
            }
            monitor.Log($"Art fingerprints loaded for {tiles} tiles across {_artBySheet.Count} sheets.", LogLevel.Trace);
        }

        private static void AddFingerprint(List<ulong> into, string? text)
        {
            if (text != null && ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                                               System.Globalization.CultureInfo.InvariantCulture, out ulong value))
                into.Add(value);
        }

        /// <summary>The three classes this guard has an opinion about: mirror, window and glass.
        /// Everything else a label says is left alone; see <see cref="GuardAgainstChangedArt"/>
        /// for the measurement behind that.</summary>
        private static bool IsGlassClass(byte one) => one == 8 || one == 12 || one == 13;

        /// <summary>
        /// The shipped label for one tile, with its GLASS taken back out if the art on screen is
        /// not the art that label was painted on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Where a pane of glass is cannot be guessed from a name. Elle's Town Buildings repaints
        /// the town sheet in place, so 78 of the 86 window tiles painted there describe art that
        /// is no longer under them, and the reflection appears in a wall. On a mismatch those
        /// pixels go back to class 0, so a repainted building shows no reflection instead of one
        /// in the wrong place.
        /// </para>
        /// <para>
        /// Only the glass, though, and that is a measured decision rather than a cautious one.
        /// Taking a single recolour out of an otherwise identical 103 mod profile on the author's
        /// machine changed the art under 11,216 of 20,202 labelled tiles: every beach tile, 149 of
        /// 151 on the town sheet, the whole night market sheet. Guarding every class would have
        /// dropped 4,703 LIQUID labels, 82% of them, for anybody whose recolour is not the one
        /// this shipped from, and the liquid labels are what correct the colour gate. Losing them
        /// is the rectangles-around-water family of reports coming straight back. Glass is 569 of
        /// the same 11,216 and has no colour gate behind it, so a missing glass label is a quiet
        /// pane and nothing else.
        /// </para>
        /// <para>
        /// Two things are deliberately NOT a mismatch. A tile whose label carries no glass is
        /// never fingerprinted at all, so the common case costs nothing. And art that cannot be
        /// READ has no reading to disagree with, so the label stands and a sheet the graphics card
        /// will not hand back behaves exactly as it did before any of this existed.
        /// </para>
        /// </remarks>
        private byte[] GuardAgainstChangedArt(string sheetName, int index, Layer layer, int x, int y, byte[] label)
        {
            string key = NormalizeSheet(sheetName);
            bool hasVariants = _variantsBySheet.TryGetValue(key, out Dictionary<int, List<LabelVariant>>? variantsHere)
                            && variantsHere.ContainsKey(index);
            // The fast path, and it is nearly every tile: nothing painted for other art, and a
            // label with no glass in it has nothing this guard can take away. No hashing at all.
            if (!hasVariants && (_artBySheet.Count == 0 || !CarriesGlass(label)))
                return label;

            (string, int) memo = (key, index);
            if (_artVerdict.TryGetValue(memo, out byte[]? decided))
                return decided!;

            _artBySheet.TryGetValue(key, out Dictionary<int, ulong[]>? painted);
            ulong[]? wasPaintedOn = null;
            painted?.TryGetValue(index, out wasPaintedOn);
            if (!hasVariants && wasPaintedOn == null)
                return label;      // never fingerprinted, so there is nothing to disagree with

            TileArtFingerprintReader? reader = ArtFingerprintReader;
            if (reader == null || !reader(layer, x, y, out ulong live))
                return label;      // no reading to disagree with; see the remarks above

            // A variant painted FOR this exact art wins outright. It is not a fallback and it is
            // not guessed at: somebody looked at this picture and said where its glass is.
            if (hasVariants)
            {
                foreach (LabelVariant variant in variantsHere![index])
                {
                    if (Array.IndexOf(variant.Art, live) < 0)
                        continue;
                    _artVerdict[memo] = variant.Label;
                    _variantHits[variant.Source] = _variantHits.TryGetValue(variant.Source, out int seen) ? seen + 1 : 1;
                    return variant.Label;
                }
            }

            bool matches = wasPaintedOn != null && Array.IndexOf(wasPaintedOn, live) >= 0;
            byte[] verdict = matches || wasPaintedOn == null ? label : WithoutGlass(label);
            _artVerdict[memo] = verdict;
            if (!ReferenceEquals(verdict, label))
            {
                this.GlassTilesRefusedForChangedArt++;
                _refusedBySheet[key] = _refusedBySheet.TryGetValue(key, out int already) ? already + 1 : 1;
            }
            return verdict;
        }

        /// <summary>
        /// Read <c>labels/art-variants.json</c>: labels painted for art that is not the base
        /// game's, each tied to the fingerprints of the art it was painted for.
        /// </summary>
        /// <remarks>
        /// Shape: <c>sheets[name][tileIndex]</c> is a list of <c>{source, art:[hex...], label}</c>.
        /// Several fingerprints per entry on purpose: a pack with four palettes repaints a window
        /// without moving it, so one painted label is right for all four, and asking somebody to
        /// paint it four times is asking for four slightly different answers.
        /// </remarks>
        private void LoadArtVariants(string file, IMonitor monitor)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("sheets", out JsonElement sheets))
                return;
            int tiles = 0, entries = 0;
            foreach (JsonProperty sheet in sheets.EnumerateObject())
            {
                var byTile = new Dictionary<int, List<LabelVariant>>();
                foreach (JsonProperty tile in sheet.Value.EnumerateObject())
                {
                    if (!int.TryParse(tile.Name, out int index) || tile.Value.ValueKind != JsonValueKind.Array)
                        continue;
                    var list = new List<LabelVariant>();
                    foreach (JsonElement one in tile.Value.EnumerateArray())
                    {
                        if (one.ValueKind != JsonValueKind.Object)
                            continue;
                        var art = new List<ulong>();
                        if (one.TryGetProperty("art", out JsonElement artEl) && artEl.ValueKind == JsonValueKind.Array)
                            foreach (JsonElement hex in artEl.EnumerateArray())
                                AddFingerprint(art, hex.ValueKind == JsonValueKind.String ? hex.GetString() : null);
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(one.TryGetProperty("label", out JsonElement lab) ? lab.GetString() ?? "" : ""); }
                        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { continue; }
                        if (art.Count == 0 || bytes.Length != 256)
                            continue;   // a variant with no art to match, or no label, is dead data
                        string source = one.TryGetProperty("source", out JsonElement src) && src.ValueKind == JsonValueKind.String
                            ? src.GetString() ?? "unnamed" : "unnamed";
                        list.Add(new LabelVariant(art.ToArray(), bytes, source));
                        entries++;
                    }
                    if (list.Count > 0)
                    {
                        byTile[index] = list;
                        tiles++;
                    }
                }
                if (byTile.Count > 0)
                    _variantsBySheet[NormalizeSheet(sheet.Name)] = byTile;
            }
            if (tiles > 0)
                monitor.Log($"Art variants loaded: {entries} label(s) for {tiles} tile(s) across "
                          + $"{_variantsBySheet.Count} sheet(s), for art other than the one this ships against.",
                            LogLevel.Info);
        }

        private static bool CarriesGlass(byte[] label)
        {
            foreach (byte one in label)
                if (IsGlassClass(one))
                    return true;
            return false;
        }

        /// <summary>A copy of the label with every glass pixel returned to plain ground. A copy
        /// rather than an edit: the original is the shipped data and is handed to every other
        /// tile that draws the same art.</summary>
        private static byte[] WithoutGlass(byte[] label)
        {
            var stripped = new byte[label.Length];
            for (int i = 0; i < label.Length; i++)
                stripped[i] = IsGlassClass(label[i]) ? (byte)0 : label[i];
            return stripped;
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
            if (bytes == null || sheet == null || layer == null)
                return null;
            // A label describes a PICTURE, and the picture behind a sheet name can be replaced by
            // another mod without the name or the tile index moving an inch. Glass is the part
            // that cannot survive that; see GuardAgainstChangedArt for why only glass.
            return MapLayers.Orient(GuardAgainstChangedArt(sheet, index, layer, x, y, bytes), orient);
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
