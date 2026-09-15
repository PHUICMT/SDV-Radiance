using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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

        /// <summary>Where the art behind each labelled sheet name comes from. Written by
        /// <c>tools/labelops/modsheets.py --sources</c>.
        ///
        /// <para>This file MUST be named here rather than left to fall through to
        /// <see cref="Load"/>, and the reason is not tidiness. Load replaces a whole sheet and
        /// never merges it, so an unhandled file listing all 262 sheet names with no tiles in
        /// them would have replaced every one of them with nothing: 45,543 painted tiles gone,
        /// reported at Trace. Any future file that lands in this folder and is not labels needs
        /// its own line here for the same reason.</para></summary>
        internal const string SheetSourceFileName = "sheet-sources.json";

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
        /// indistinguishable from one that was never installed.
        ///
        /// <para>A running total of decisions taken since the mod loaded, not of tiles standing on
        /// a variant right now: <see cref="ForgetArtVerdictsFor"/> drops the verdicts for one sheet
        /// at a time and the hits are counted by the pack that painted them, so there is nothing to
        /// subtract. A tile decided twice across a sheet reload is counted twice, which is the
        /// honest reading of "how often was this pack's answer used".</para></summary>
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

        /// <summary>How many labelled tiles have had the art-bound part of their label taken back
        /// out - the glass, the water, the light - because the tile is drawn from a different
        /// DRAWING, not merely a repaint of the same one. Reported by radiance_report: "my window
        /// reflections are missing" and "I am running an art mod this has no labels for" are the
        /// same sentence, and a player cannot be expected to work that out unaided.</summary>
        public int ArtBoundLabelsRefusedForChangedArt { get; private set; }

        /// <summary>Which sheets those refusals were on, and how many each. A count on its own
        /// says something is wrong; the sheet names are what let the report go on to name the
        /// packs that repaint them.</summary>
        private readonly Dictionary<string, int> _refusedBySheet = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> RefusedBySheet => _refusedBySheet;

        /// <summary>Reads the fingerprint of the art a map tile actually draws. Supplied by the
        /// render pipeline, which already holds the tilesheet pixels; LabelStore has no business
        /// touching the graphics card itself. Null before the pipeline exists, which is treated as
        /// "cannot tell" rather than as a mismatch.</summary>
        internal delegate bool TileArtFingerprintReader(Layer layer, int x, int y,
            out ulong fingerprint);
        internal static TileArtFingerprintReader? ArtFingerprintReader;

        /// <summary>The same reading, for art that is not on the tile grid: a furniture sprite, a
        /// placed object, anything drawn from a sheet cell rather than from a map layer. Supplied
        /// by the same pipeline and reading through the same sheet cache.</summary>
        internal delegate bool SheetCellFingerprintReader(Texture2D texture, Rectangle cell,
            out ulong fingerprint);
        internal static SheetCellFingerprintReader? SheetFingerprintReader;

        /// <summary>Reused by <see cref="ForgetArtVerdictsFor"/>. On a modded install an asset is
        /// invalidated every few seconds, so neither of these may allocate.</summary>
        private readonly HashSet<string> _reloadedSheetScratch = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string sheet, int index)> _forgottenVerdictScratch = new();

        /// <summary>Throw away the art verdicts for the sheets a mod has just reloaded, because the
        /// picture those labels were painted for may have changed. Moves <see cref="Version"/>,
        /// which is what makes the window panes and every other consumer keyed on it rebuild, so it
        /// moves only when a verdict was really dropped.
        ///
        /// <para>This used to take no argument and throw away EVERY verdict on EVERY invalidation,
        /// of any asset at all. Three whole-map scans hang off that version, so a mod that
        /// invalidates a data asset on a timer rebuilt all three every few seconds. Reported on a
        /// 5800X: Buff Framework reloading <c>aedenthorn.BuffFramework/dictionary</c> gave a 15 to
        /// 17 ms window scan and a 14 to 25 ms emissive scan in town, and 34.6 and 32.3 ms on a
        /// 163x156 farm, for a dictionary of buffs that cannot change what a tile is made of. A
        /// name no label was ever painted on now costs a hash lookup and nothing else.</para></summary>
        public void ForgetArtVerdictsFor(IEnumerable<string> reloadedAssetNames)
        {
            if (_artVerdict.Count == 0 && this.ArtBoundLabelsRefusedForChangedArt == 0)
                return;
            _reloadedSheetScratch.Clear();
            foreach (string name in reloadedAssetNames)
                _reloadedSheetScratch.Add(NormalizeSheet(SurfaceMap.NormaliseAssetName(name)));
            if (_reloadedSheetScratch.Count == 0)
                return;

            _forgottenVerdictScratch.Clear();
            foreach ((string sheet, int index) memo in _artVerdict.Keys)
                if (_reloadedSheetScratch.Contains(memo.sheet))
                    _forgottenVerdictScratch.Add(memo);
            bool forgotARefusal = false;
            foreach (string sheet in _reloadedSheetScratch)
                if (_refusedBySheet.TryGetValue(sheet, out int refusedOnThisSheet))
                {
                    this.ArtBoundLabelsRefusedForChangedArt -= refusedOnThisSheet;
                    _refusedBySheet.Remove(sheet);
                    forgotARefusal = true;
                }
            if (_forgottenVerdictScratch.Count == 0 && !forgotARefusal)
                return;
            foreach ((string sheet, int index) memo in _forgottenVerdictScratch)
                _artVerdict.Remove(memo);
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
                    if (string.Equals(Path.GetFileName(file), SheetSourceFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadSheetSources(file, monitor); }
                        catch (Exception ex) { monitor.Log($"Bad sheet source file: {ex.Message}", LogLevel.Warn); }
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
                if (!int.TryParse(tile.Name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int idx))
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
                    if (!int.TryParse(tile.Name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index))
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

        /// <summary>sheet -> tile -> hex values, the shape both the fingerprint and the outline
        /// tables use. A tile whose list comes out empty is left out rather than stored empty: an
        /// empty list would read as "painted on no art at all", which refuses everything.</summary>
        private static void ReadHexTable(JsonElement table, Dictionary<string, Dictionary<int, ulong[]>> into)
        {
            foreach (JsonProperty sheet in table.EnumerateObject())
            {
                var byTile = new Dictionary<int, ulong[]>();
                foreach (JsonProperty tile in sheet.Value.EnumerateObject())
                {
                    if (!int.TryParse(tile.Name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index))
                        continue;
                    var values = new List<ulong>();
                    if (tile.Value.ValueKind == JsonValueKind.String)
                        AddFingerprint(values, tile.Value.GetString());
                    else if (tile.Value.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement one in tile.Value.EnumerateArray())
                            AddFingerprint(values, one.ValueKind == JsonValueKind.String ? one.GetString() : null);
                    if (values.Count > 0)
                        byTile[index] = values.ToArray();
                }
                if (byTile.Count > 0)
                    into[NormalizeSheet(sheet.Name)] = byTile;
            }
        }

        private static void AddFingerprint(List<ulong> into, string? text)
        {
            if (text != null && ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                                               System.Globalization.CultureInfo.InvariantCulture, out ulong value))
                into.Add(value);
        }

        /// <summary>Mirror, window and glass: the classes whose whole meaning is a reflection
        /// drawn at an exact place on the tile.</summary>
        private static bool IsGlassClass(byte one) => LabelClass.IsGlassy(one);

        /// <summary>
        /// The classes this guard may take back out. Glass, window and mirror, and only those.
        /// </summary>
        /// <remarks>
        /// This was briefly widened to the liquids and the lit surfaces, on the strength of a
        /// silhouette test that was supposed to make refusing them affordable. The test turned out
        /// to rescue nothing at all, and without it guarding water means taking water off 6,732 of
        /// the corpus's tile-and-picture pairs - a real change to the mod's headline effect, which
        /// needs evidence from a game rather than a rider on a fix to something else.
        ///
        /// Glass is here because a pane of glass IS a place: a reflection drawn where a window no
        /// longer is announces itself, and that report is what this guard was written for. Water
        /// in the wrong place is just as wrong and stays unguarded for now, deliberately and on
        /// the record.
        /// </remarks>
        private static bool IsArtBoundClass(byte one) => IsGlassClass(one);

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
        /// <summary>Where to go and read the picture a label is about to be checked against: a
        /// tile on a map layer, or a cell of a sheet that something draws itself from. Both end at
        /// the same sixteen by sixteen pixels and only the directions to them differ, so the guard
        /// takes one of these rather than one set of parameters per kind of caller.</summary>
        private readonly struct ArtToCheck
        {
            private readonly Layer? _layer;
            private readonly int _tileX;
            private readonly int _tileY;
            private readonly Texture2D? _sheet;
            private readonly Rectangle _cell;

            internal ArtToCheck(Layer layer, int tileX, int tileY)
            {
                _layer = layer; _tileX = tileX; _tileY = tileY; _sheet = null; _cell = default;
            }

            internal ArtToCheck(Texture2D sheet, Rectangle cell)
            {
                _layer = null; _tileX = 0; _tileY = 0; _sheet = sheet; _cell = cell;
            }

            /// <summary>False means "cannot tell", which the guard treats as no disagreement
            /// rather than as a mismatch. A reader that is not wired up yet, a disposed sheet and
            /// a tile with no art all land here.</summary>
            internal bool TryFingerprint(out ulong fingerprint)
            {
                fingerprint = 0;
                if (_layer != null)
                    return ArtFingerprintReader is { } tileReader
                        && tileReader(_layer, _tileX, _tileY, out fingerprint);
                return _sheet != null && SheetFingerprintReader is { } sheetReader
                    && sheetReader(_sheet, _cell, out fingerprint);
            }
        }

        private byte[] GuardAgainstChangedArt(string sheetName, int index, ArtToCheck art, byte[] label)
        {
            string key = NormalizeSheet(sheetName);
            _variantsBySheet.TryGetValue(key, out Dictionary<int, List<LabelVariant>>? variantsForSheet);
            return GuardAgainstChangedArt(key, variantsForSheet, index, art, label);
        }

        /// <summary>The same guard for a caller that has already found the sheet, which is the map
        /// tile path: it holds a <see cref="SheetLabels"/> and must not re-normalise the name or
        /// look the variants up again for every tile of the map.</summary>
        private byte[] GuardAgainstChangedArt(SheetLabels sheetLabels, int index, ArtToCheck art, byte[] label)
        {
            // The fast path, and it is nearly every tile. Same question as the one below, asked of
            // a set worked out once for the sheet rather than by reading the label through: that
            // read was 256 bytes per ask, and the surface build asks around eight times per tile.
            if ((sheetLabels.Variants == null || !sheetLabels.Variants.ContainsKey(index))
                && (_artBySheet.Count == 0 || sheetLabels.ArtBoundTiles?.Contains(index) != true))
                return label;
            return GuardAgainstChangedArt(sheetLabels.Sheet, sheetLabels.Variants, index, art, label);
        }

        private byte[] GuardAgainstChangedArt(string key, Dictionary<int, List<LabelVariant>>? variantsForSheet,
                                              int index, ArtToCheck art, byte[] label)
        {
            bool hasVariants = variantsForSheet != null && variantsForSheet.ContainsKey(index);
            Dictionary<int, List<LabelVariant>>? variantsHere = variantsForSheet;
            // The fast path, and it is nearly every tile: nothing painted for other art, and a
            // label with no glass in it has nothing this guard can take away. No hashing at all.
            if (!hasVariants && (_artBySheet.Count == 0 || !CarriesArtBoundClass(label)))
                return label;

            (string, int) memo = (key, index);
            if (_artVerdict.TryGetValue(memo, out byte[]? decided))
                return decided!;

            _artBySheet.TryGetValue(key, out Dictionary<int, ulong[]>? painted);
            ulong[]? wasPaintedOn = null;
            painted?.TryGetValue(index, out wasPaintedOn);
            if (!hasVariants && wasPaintedOn == null)
                return label;      // never fingerprinted, so there is nothing to disagree with

            if (!art.TryFingerprint(out ulong live))
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

            // The fingerprint decides, on its own. A silhouette tier was tried here and removed:
            // it rescued NOTHING and it let real mismatches through. Every case a "same drawing,
            // new paint" test would have saved is already saved, because artfingerprints.py lists
            // the repaints beside the original - so of the pairs that still disagree, the ones an
            // outline test forgave came to 1,124 and the ones it rescued came to 0. What it did
            // do was pass every redrawn OPAQUE tile, whose silhouette is 256 solid pixels and
            // therefore identical to every other opaque tile ever drawn. That is how a window
            // reflection went on landing on a building Elle's Town Buildings had redrawn: the
            // exact report this guard exists to answer.
            bool matches = wasPaintedOn != null && Array.IndexOf(wasPaintedOn, live) >= 0;
            byte[] verdict = matches || wasPaintedOn == null ? label : WithoutArtBoundClasses(label);
            _artVerdict[memo] = verdict;
            if (!ReferenceEquals(verdict, label))
            {
                this.ArtBoundLabelsRefusedForChangedArt++;
                _refusedBySheet[key] = _refusedBySheet.TryGetValue(key, out int already) ? already + 1 : 1;
            }
            return verdict;
        }

        /// <summary>Where the art behind one labelled sheet name comes from. <paramref name="From"/>
        /// is prose meant to be printed; <paramref name="ModIds"/> is the half another install can
        /// be asked about, since the pack folder names the tool also records are one machine's
        /// filing and mean nothing anywhere else.</summary>
        internal readonly record struct SheetSource(string From, IReadOnlyList<string> ModIds);

        private readonly Dictionary<string, SheetSource> _sheetSource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _modNameById = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Unique id to the name the pack calls itself, for every pack that supplies art
        /// the labels cover. The name a mod gives itself is the only one worth printing: the pack
        /// folders the tool also sees are the author's own filing and never leave that machine.</summary>
        public IReadOnlyDictionary<string, string> ModNameById => _modNameById;

        /// <summary>Provenance for every sheet the shipped labels cover, or empty when the file
        /// is absent - which is not a fault, only an older build's labels folder.</summary>
        public IReadOnlyDictionary<string, SheetSource> SheetSources => _sheetSource;

        /// <summary>
        /// Read <c>labels/sheet-sources.json</c>: which mod supplies the art behind each sheet
        /// name the labels cover.
        /// </summary>
        /// <remarks>
        /// The labels themselves say nothing about where a sheet name comes from, so nothing
        /// could answer which mods this data covers, rank what to paint next by who ships it, or
        /// tell a player whether their expansion is one we know about. The answer is taken from
        /// the files on disk by the tool, not guessed at here.
        /// </remarks>
        private void LoadSheetSources(string file, IMonitor monitor)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            // The id to name table is held once at the top rather than beside every use: 404
            // packs share 262 sheets, and writing each name where it is used more than doubled
            // the file for nothing.
            if (doc.RootElement.TryGetProperty("mods", out JsonElement catalogue)
                && catalogue.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty mod in catalogue.EnumerateObject())
                    if (mod.Value.ValueKind == JsonValueKind.String && mod.Value.GetString() is { Length: > 0 } named)
                        _modNameById[mod.Name] = named;
            }
            if (!doc.RootElement.TryGetProperty("sheets", out JsonElement sheets))
                return;
            foreach (JsonProperty sheet in sheets.EnumerateObject())
            {
                string from = sheet.Value.TryGetProperty("from", out JsonElement said)
                              && said.ValueKind == JsonValueKind.String
                    ? said.GetString() ?? "unknown"
                    : "unknown";
                var ids = new List<string>();
                if (sheet.Value.TryGetProperty("mods", out JsonElement listed)
                    && listed.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement one in listed.EnumerateArray())
                        if (one.ValueKind == JsonValueKind.String && one.GetString() is { Length: > 0 } id)
                            ids.Add(id);
                }
                _sheetSource[NormalizeSheet(sheet.Name)] = new SheetSource(from, ids);
            }
            monitor.Log($"Sheet sources loaded: where the art behind {_sheetSource.Count} labelled "
                        + $"sheet(s) comes from, across {_modNameById.Count} pack(s).", LogLevel.Trace);
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
                    if (!int.TryParse(tile.Name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index) || tile.Value.ValueKind != JsonValueKind.Array)
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

        private static bool CarriesArtBoundClass(byte[] label)
        {
            foreach (byte one in label)
                if (IsArtBoundClass(one))
                    return true;
            return false;
        }

        /// <summary>A copy of the label with every glass pixel returned to plain ground. A copy
        /// rather than an edit: the original is the shipped data and is handed to every other
        /// tile that draws the same art.</summary>
        private static byte[] WithoutArtBoundClasses(byte[] label)
        {
            var stripped = new byte[label.Length];
            for (int i = 0; i < label.Length; i++)
                stripped[i] = IsArtBoundClass(label[i]) ? (byte)0 : label[i];
            return stripped;
        }

        public byte[]? Get(string? imageSource, int tileIndex)
        {
            if (imageSource == null)
                return null;
            // The sheet's normalised name is REMEMBERED per image source. This is asked once per
            // tile of every layer of the whole map on a mask rebuild, tens of thousands of times,
            // and normalising is a Replace and a Substring: two short-lived strings each. A map
            // holds a handful of distinct image sources, so the table stays tiny and the answer
            // never changes for a given source.
            if (!_normalisedSheetNames.TryGetValue(imageSource, out string? sheet))
            {
                sheet = NormalizeSheet(imageSource);
                _normalisedSheetNames[imageSource] = sheet;
            }
            return _tilesBySheet.TryGetValue(sheet, out var tiles)
                   && tiles.TryGetValue(tileIndex, out byte[]? bytes) ? bytes : null;
        }

        /// <summary>Image source as the map spells it, to the sheet name labels are keyed by. A
        /// pure mapping of one string to another, so it never goes stale and needs no clearing;
        /// it is bounded by how many distinct image sources the loaded maps name, which is tens.</summary>
        private readonly Dictionary<string, string> _normalisedSheetNames = new(StringComparer.Ordinal);

        /// <summary>Everything this store knows about one tile sheet, worked out once for the sheet
        /// OBJECT rather than once per tile that draws from it. Null <see cref="Tiles"/> means the
        /// sheet carries no labels at all, which is the answer for most sheets and has to be as
        /// cheap to give as a hit.
        ///
        /// <para>The path used to be two string dictionary lookups per ask, one of them case
        /// insensitive, and the surface build asks up to thirteen times per tile: Pelican Town is
        /// fifteen thousand tiles, so around three hundred and sixty thousand string hashes to
        /// build one grid. Measured by removing the work rather than by guessing at it: taking the
        /// whole map-property half of the classifier away saved 2.8 ms of 26.8, and taking the
        /// pixel counting away made it SLOWER, because without a verdict the walk stops asking
        /// nowhere. What is left is the asking itself.</para></summary>
        private sealed class SheetLabels
        {
            /// <summary>The sheet name the labels are keyed by, normalised once.</summary>
            internal readonly string Sheet;
            internal readonly Dictionary<int, byte[]>? Tiles;
            internal readonly Dictionary<int, List<LabelVariant>>? Variants;

            /// <summary>Which tile indices on this sheet carry a class the art can take back: the
            /// glass, the water, the light. Worked out once for the sheet by reading each of its
            /// labels through, so that the guard on the tile path can answer with one int lookup
            /// instead of reading 256 bytes for every tile of the map that draws from it.</summary>
            internal readonly HashSet<int>? ArtBoundTiles;

            internal SheetLabels(string sheet, Dictionary<int, byte[]>? tiles,
                                 Dictionary<int, List<LabelVariant>>? variants, HashSet<int>? artBoundTiles)
            { this.Sheet = sheet; this.Tiles = tiles; this.Variants = variants; this.ArtBoundTiles = artBoundTiles; }
        }

        /// <summary>Weak, because a map reload builds new tile sheet objects and the old ones must
        /// be free to go with it.</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<xTile.Tiles.TileSheet, SheetLabels> _labelsByTileSheet = new();

        private SheetLabels LabelsFor(xTile.Tiles.TileSheet tileSheet)
        {
            if (_labelsByTileSheet.TryGetValue(tileSheet, out SheetLabels? known))
                return known;
            string sheet = NormalizeSheet(tileSheet.ImageSource ?? "");
            _tilesBySheet.TryGetValue(sheet, out Dictionary<int, byte[]>? tiles);
            _variantsBySheet.TryGetValue(sheet, out Dictionary<int, List<LabelVariant>>? variants);
            HashSet<int>? artBoundTiles = null;
            if (tiles != null)
                foreach (var labelledTile in tiles)
                    if (CarriesArtBoundClass(labelledTile.Value))
                        (artBoundTiles ??= new HashSet<int>()).Add(labelledTile.Key);
            var found = new SheetLabels(sheet, tiles, variants, artBoundTiles);
            _labelsByTileSheet.Add(tileSheet, found);
            return found;
        }

        /// <summary>Per-pixel classes for the art a layer draws at a tile, or null if unlabeled.
        /// Takes the Layer directly — the mask gather already holds Back/Buildings/Front, so
        /// looking them up by name once per tile would be pure overhead.</summary>
        public byte[]? Get(Layer? layer, int x, int y)
        {
            if (_tilesBySheet.Count == 0 || layer == null
                || !TryTileArt(layer, x, y, out xTile.Tiles.TileSheet? tileSheet, out int index,
                               out xTile.Tiles.Tile? mapTile))
                return null;
            SheetLabels sheetLabels = LabelsFor(tileSheet!);
            if (sheetLabels.Tiles == null || !sheetLabels.Tiles.TryGetValue(index, out byte[]? bytes))
                return null;
            // Labels are painted on the sheet, upright. The map may place the tile mirrored or
            // turned, so the marks have to be turned the same way before they can be compared with
            // anything on screen - otherwise a mirrored waterfall's liquid pixels sit on the wrong
            // side of the tile and the mask disagrees with the art by exactly that reflection.
            //
            // The turn is worked out HERE, once the tile is known to carry a label, rather than
            // while the tile is being identified: it is only ever used on a label. Measured on its
            // own it changed nothing, and it is kept because it puts the work where its answer is
            // used rather than on the path every tile of the map walks.
            byte orient = MapLayers.Orientation(mapTile);
            // A label describes a PICTURE, and the picture behind a sheet name can be replaced by
            // another mod without the name or the tile index moving an inch. Glass is the part
            // that cannot survive that; see GuardAgainstChangedArt for why only glass.
            return MapLayers.Orient(
                GuardAgainstChangedArt(sheetLabels, index, new ArtToCheck(layer, x, y), bytes), orient);
        }

        /// <summary>
        /// Per-pixel classes for one sixteen by sixteen cell of a sheet that something draws
        /// itself from - a furniture sprite, a placed object - rather than of a map tile.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The labels were already there. Four furniture sheets carry them (VanillaFurniture alone
        /// has 1,268 pixels of glass and 1,269 of emissive, painted by hand), and nothing could
        /// read them: every other way into this store asks for a map layer and a tile, and a
        /// mirror standing in a bedroom is on neither. That is data somebody looked at and marked
        /// which no code could reach.
        /// </para>
        /// <para>
        /// Same key space as a map tile, so the guard, the variants and the memo all work
        /// unchanged: a sheet is a sheet and a cell index is a cell index whether the thing
        /// drawing it is a map layer or a chair.
        /// </para>
        /// </remarks>
        /// <param name="textureName">The asset name the sheet is loaded under. For an item this is
        /// <c>ParsedItemData.TextureName</c>, which is the name the game itself resolved, so a
        /// content pack that redirects the art is followed rather than guessed at.</param>
        /// <param name="cell">The cell in SHEET pixels. Art taller or wider than one cell is asked
        /// for one cell at a time, which is how it was painted.</param>
        /// <param name="orient">Turn to apply, for art the world places flipped. Zero is upright.</param>
        public byte[]? GetSheetCell(string? textureName, Texture2D? texture, Rectangle cell,
            byte orient = 0)
        {
            if (textureName == null || texture == null || texture.Width <= 0)
                return null;
            int cellsAcross = texture.Width / 16;
            if (cellsAcross <= 0)
                return null;
            int index = cell.Y / 16 * cellsAcross + cell.X / 16;
            byte[]? bytes = Get(textureName, index);
            if (bytes == null)
                return null;
            return MapLayers.Orient(
                GuardAgainstChangedArt(textureName, index, new ArtToCheck(texture, cell), bytes),
                orient);
        }

        public byte[]? Get(GameLocation? location, int x, int y, string layerName)
            => _tilesBySheet.Count > 0 ? Get(location?.map?.GetLayer(layerName), x, y) : null;

        /// <summary>The sheet and cell a map tile draws from, plus the tile the MAP holds, which is
        /// what carries the turn: an animation frame inside it does not.</summary>
        private static bool TryTileArt(Layer? layer, int x, int y, out xTile.Tiles.TileSheet? tileSheet,
                                       out int index, out xTile.Tiles.Tile? mapTile)
        {
            tileSheet = null;
            index = -1;
            mapTile = null;
            if (layer == null || x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight)
                return false;
            var t = layer.Tiles[x, y];
            mapTile = t;
            if (t is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                t = at.TileFrames[0];
            if (t?.TileSheet == null)
                return false;
            tileSheet = t.TileSheet;
            index = t.TileIndex;
            return true;
        }
    }
}
