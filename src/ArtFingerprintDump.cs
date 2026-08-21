using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using xTile.Tiles;

namespace SDVRadiance
{
    /// <summary>
    /// <c>radiance_artfingerprint</c>: writes, for every tile this mod holds a label for, a
    /// fingerprint of the art the game is ACTUALLY drawing there right now.
    ///
    /// <para>
    /// Run it once with the base game's art and once for each art mod worth supporting, and the
    /// files together say which picture each painted label was painted on. See
    /// <see cref="ArtFingerprint"/> for why a label needs that at all, and why the reading has to
    /// come through <c>GetData</c> on both sides.
    /// </para>
    ///
    /// <para>
    /// Sheets are found from the maps that are LOADED, because a map is the only thing that knows
    /// which asset a sheet name really resolves to. Labelled sheets that no loaded map places are
    /// then tried once under <c>Maps/&lt;name&gt;</c>, which is where the base game keeps almost
    /// all of them; anything still missing is listed by name rather than passed over in silence,
    /// so a short run is visible as a short run instead of looking like a complete one.
    /// </para>
    /// </summary>
    internal static class ArtFingerprintDump
    {
        public static string? Run(IMonitor monitor, IModHelper helper, string label)
        {
            if (!Context.IsWorldReady)
            {
                monitor.Log("Load a save first, then run radiance_artfingerprint again.", LogLevel.Warn);
                return null;
            }
            LabelStore? labels = LabelStore.Instance;
            if (labels == null || !labels.Any)
            {
                monitor.Log("No labels are loaded, so there is nothing to fingerprint.", LogLevel.Warn);
                return null;
            }

            // Sheet name -> the asset a loaded map resolves it to. First one wins: the maps agree
            // with each other far more often than they disagree, and when they do disagree the
            // sheet name is being shared by two pieces of art, which is its own known problem.
            var sourceBySheet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tileSheetBySheet = new Dictionary<string, TileSheet>(StringComparer.OrdinalIgnoreCase);
            // The same walk the map dump uses. Game1.locations alone reaches the outdoors and
            // whatever the player has already opened, which would have left every interior sheet
            // to the guessed grid below and made a short run look like a full one.
            Utility.ForEachLocation(location =>
            {
                if (location?.map == null)
                    return true;
                foreach (TileSheet sheet in location.map.TileSheets)
                {
                    if (string.IsNullOrEmpty(sheet.ImageSource))
                        continue;
                    string name = LabelStore.NormalizeSheet(sheet.ImageSource);
                    if (sourceBySheet.ContainsKey(name))
                        continue;
                    sourceBySheet[name] = sheet.ImageSource;
                    tileSheetBySheet[name] = sheet;
                }
                return true;
            }, includeInteriors: true, includeGenerated: false);

            var sheetsOut = new Dictionary<string, object>();
            var missing = new List<string>();
            int tilesHashed = 0, tilesUnreadable = 0;
            var tilePixels = new Color[256];

            foreach (string name in labels.LabelledSheetNames)
            {
                IReadOnlyCollection<int>? indices = labels.LabelledTileIndices(name);
                if (indices == null || indices.Count == 0)
                    continue;

                tileSheetBySheet.TryGetValue(name, out TileSheet? placed);
                string source = sourceBySheet.TryGetValue(name, out string? found) ? found : "Maps/" + name;

                Texture2D texture;
                try { texture = helper.GameContent.Load<Texture2D>(source); }
                catch
                {
                    missing.Add(name);
                    continue;
                }

                var tilesOut = new Dictionary<string, string>();
                foreach (int index in indices)
                {
                    Rectangle bounds;
                    if (placed != null)
                    {
                        try
                        {
                            var imageBounds = placed.GetTileImageBounds(index);
                            bounds = new Rectangle(imageBounds.X, imageBounds.Y, imageBounds.Width, imageBounds.Height);
                        }
                        catch { tilesUnreadable++; continue; }
                    }
                    else
                    {
                        // No loaded map to ask, so assume the plain grid the base game's sheets
                        // use: sixteen-pixel tiles, no margin, no spacing. Wrong for a sheet that
                        // has any, which is why this path only runs when nothing placed the sheet.
                        int perRow = texture.Width / 16;
                        if (perRow <= 0) { tilesUnreadable++; continue; }
                        bounds = new Rectangle(index % perRow * 16, index / perRow * 16, 16, 16);
                    }
                    if (bounds.Width != 16 || bounds.Height != 16
                        || bounds.X < 0 || bounds.Y < 0
                        || bounds.Right > texture.Width || bounds.Bottom > texture.Height)
                    {
                        tilesUnreadable++;
                        continue;
                    }
                    try { texture.GetData(0, bounds, tilePixels, 0, 256); }
                    catch { tilesUnreadable++; continue; }
                    tilesOut[index.ToString()] = ArtFingerprint.ToText(ArtFingerprint.OfTilePixels(tilePixels));
                    tilesHashed++;
                }

                sheetsOut[name] = new Dictionary<string, object>
                {
                    ["source"] = source,
                    ["placedByAMap"] = placed != null,
                    ["sheetPixels"] = new[] { texture.Width, texture.Height },
                    ["tiles"] = tilesOut,
                };
            }

            // Which packs on this machine say they repaint map art. Not part of the decision, and
            // never will be: what a tile draws is settled by its fingerprint. This is here so that
            // months from now a sixteen digit number in the shipped file still has an answer to
            // "why do we accept this one", instead of being a value nobody dares delete.
            var repainters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in sheetsOut.Keys)
                repainters.UnionWith(MapArtClaims.WhoPatches(name, helper.DirectoryPath, monitor));

            var document = new Dictionary<string, object>
            {
                ["format"] = 1,
                ["label"] = label,
                ["takenAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["repaintedBy"] = new List<string>(repainters),
                ["sheets"] = sheetsOut,
            };

            string directory = Path.Combine(MapDump.HfStudioDir(), "fingerprints");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, SafeLabel(label) + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(document,
                new JsonSerializerOptions { WriteIndented = false }));

            monitor.Log($"Fingerprinted {tilesHashed} tiles across {sheetsOut.Count} sheets as \"{label}\".", LogLevel.Info);
            if (tilesUnreadable > 0)
                monitor.Log($"{tilesUnreadable} labelled tiles could not be read: the art is smaller than the "
                          + "label expects, which is the sheet being narrower than the game asks for.", LogLevel.Warn);
            if (missing.Count > 0)
                monitor.Log($"{missing.Count} labelled sheets are not loaded here, so they were not fingerprinted: "
                          + string.Join(", ", missing.Count > 12 ? missing.GetRange(0, 12) : missing)
                          + (missing.Count > 12 ? ", ..." : ""), LogLevel.Info);
            monitor.Log("Written to " + path, LogLevel.Info);
            return path;
        }

        private static string SafeLabel(string label)
        {
            var text = new System.Text.StringBuilder(label.Length);
            foreach (char c in label)
                text.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            string cleaned = text.ToString().Trim(' ', '.');
            return cleaned.Length == 0 ? "unnamed" : cleaned;
        }
    }
}
