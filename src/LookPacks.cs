using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>One look another mod offers: a name, a line about it, and a share code.</summary>
    internal sealed record PackLook(string Name, string Description, string Code, string ModName, string FilePath);

    /// <summary>
    /// Looks other mods offer the player, found in a <c>radiance-looks.json</c> beside their
    /// manifest and listed in F6 under the Looks tab.
    ///
    /// <para>
    /// Asked for by the author on 17 Oct 2026, when settings mods and collections began to ship
    /// Radiance's settings by writing over its config.json: that throws away the player's own
    /// settings and saved looks, and the next update of either mod can undo the other. A look pack
    /// replaces the overwrite with an offer. Nothing in it is applied until the player picks it,
    /// and picking it is the same try-on as pasting a code: the list of what changes, then Keep or
    /// Put mine back.
    /// </para>
    ///
    /// <para>
    /// The look itself is a share code (RAD1L... from "Copy look", or RAD1F... from "Copy
    /// everything"), so an author makes one by setting the look up in F6 and copying it, and the
    /// code's own rules carry over: settings a release does not know are stepped over, settings it
    /// knows and the code leaves out go back to a fresh install's values.
    /// </para>
    ///
    /// The file:
    /// <code>
    /// { "looks": [ { "name": "Golden autumn", "description": "Warm and soft.", "code": "RAD1L-..." } ] }
    /// </code>
    /// </summary>
    internal static class LookPacks
    {
        public const string FileName = "radiance-looks.json";
        /// <summary>A name longer than this does not fit on a button in F6.</summary>
        private const int LongestName = 40;
        /// <summary>More than this from one mod is a list nobody will read in a settings panel.</summary>
        private const int MostLooksPerMod = 12;

        /// <summary>Every usable look from every mod, read once when the game starts.</summary>
        public static IReadOnlyList<PackLook> Found { get; private set; } = [];
        /// <summary>One line per file for radiance_report: what it gave and what it could not.</summary>
        public static IReadOnlyList<string> Notes { get; private set; } = [];

        public static void Load(string modDirectory, ModConfig config, IMonitor monitor)
        {
            var looks = new List<PackLook>();
            var notes = new List<string>();
            string? mods = LabelPacks.FindModsRoot(modDirectory);
            if (mods == null)
            {
                Found = looks; Notes = notes;
                return;
            }
            List<string> files;
            try
            {
                files = [.. Directory.EnumerateFiles(mods, FileName, SearchOption.AllDirectories)
                    .Where(file => !Path.GetFullPath(file).StartsWith(Path.GetFullPath(modDirectory), StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception problem)
            {
                monitor.Log($"Could not look for {FileName} files: {problem.Message}", LogLevel.Warn);
                Found = looks; Notes = notes;
                return;
            }
            foreach (string file in files)
                ReadOne(file, mods, config, monitor, looks, notes);
            Found = looks; Notes = notes;
            if (looks.Count > 0)
                monitor.Log($"Looks from other mods: {looks.Count} ({string.Join(", ", looks.Select(look => look.ModName).Distinct())}).", LogLevel.Info);
        }

        private static void ReadOne(string file, string mods, ModConfig config, IMonitor monitor, List<PackLook> looks, List<string> notes)
        {
            string modFolder = LabelPacks.FindOwningModFolder(file, mods);
            string modName = ReadModName(modFolder) ?? Path.GetFileName(modFolder);
            int given = 0, refused = 0;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!document.RootElement.TryGetProperty("looks", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                {
                    monitor.Log($"{modName}: {FileName} has no \"looks\" list, so it offers nothing.", LogLevel.Warn);
                    notes.Add($"{modName}: no \"looks\" list");
                    return;
                }
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    string name = Text(entry, "name").Trim();
                    string description = Text(entry, "description").Trim();
                    string code = Text(entry, "code").Trim();
                    string? why = null;
                    if (given >= MostLooksPerMod)
                        why = $"more than {MostLooksPerMod} looks from one mod";
                    else if (name.Length == 0)
                        why = "a look with no name";
                    else if (name.Length > LongestName)
                        why = $"\"{name}\" is longer than {LongestName} characters";
                    else if (!ShareCode.TryRead(code, config, out _, out string problem))
                        why = $"\"{name}\" has a code this release cannot read ({problem})";
                    if (why != null)
                    {
                        monitor.Log($"{modName}: {why}; left out.", LogLevel.Warn);
                        refused++;
                        continue;
                    }
                    looks.Add(new PackLook(name, description, code, modName, file));
                    given++;
                }
            }
            catch (Exception problem)
            {
                monitor.Log($"{modName}: {FileName} could not be read ({problem.Message}); it offers nothing.", LogLevel.Warn);
                notes.Add($"{modName}: unreadable ({problem.Message})");
                return;
            }
            notes.Add($"{modName}: {given} look(s)" + (refused > 0 ? $", {refused} left out (see the log)" : ""));
        }

        private static string Text(JsonElement entry, string property)
            => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static string? ReadModName(string modFolder)
        {
            try
            {
                string manifest = Path.Combine(modFolder, "manifest.json");
                if (!File.Exists(manifest))
                    return null;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return document.RootElement.TryGetProperty("Name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
