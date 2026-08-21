using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Which installed content packs say they repaint a given map tilesheet.
    ///
    /// <para>
    /// This does NOT decide anything. What is drawn on a tile is settled by its fingerprint, for
    /// reasons the fingerprint's own notes set out: packs compose, one of them alone has four
    /// palettes and thirty switches, and a patch can be conditional. A name cannot answer a
    /// question about pixels.
    /// </para>
    ///
    /// <para>
    /// What a name CAN do is turn "63 tiles were refused" into something the person reading it can
    /// act on. A report that also says the sheet is claimed by Elle's Town Buildings and a Thai
    /// translation names the suspects, and that is the difference between a bug report I have to
    /// answer with three questions and one I can answer with a fix. Claims are read from each
    /// pack's own content.json, so the pack tells us in its own words.
    /// </para>
    ///
    /// <para>
    /// Read once, lazily, and only when a report asks. Three things it deliberately does not
    /// promise: a patch that is switched off in the pack's config still counts as a claim, a mod
    /// that edits art from C# rather than from content.json is invisible here, and a target
    /// carrying a token this does not know is matched by the plain part of its name.
    /// </para>
    /// </summary>
    internal static class MapArtClaims
    {
        private static Dictionary<string, SortedSet<string>>? _byExactSheet;
        private static List<(string suffix, string pack)>? _bySuffix;

        private static readonly Regex TargetPattern =
            new("\"Target\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly string[] Seasons = { "spring", "summer", "fall", "winter" };

        /// <summary>A pack's content.json can be large; anything past this is somebody's data file,
        /// not a patch list, and reading it would cost more than the answer is worth.</summary>
        private const long MaximumPatchFileBytes = 8 * 1024 * 1024;

        /// <summary>Every installed pack that says it repaints this sheet, or an empty list.</summary>
        public static IReadOnlyCollection<string> WhoPatches(string sheetName, string modDirectory, IMonitor monitor)
        {
            Scan(modDirectory, monitor);
            string wanted = LabelStore.NormalizeSheet(sheetName);
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_byExactSheet!.TryGetValue(wanted, out SortedSet<string>? exact))
                found.UnionWith(exact);
            foreach ((string suffix, string pack) in _bySuffix!)
            {
                if (wanted.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    found.Add(pack);
            }
            return found;
        }

        /// <summary>Throw the scan away, so a reload picks up a pack that was added since.</summary>
        public static void Forget()
        {
            _byExactSheet = null;
            _bySuffix = null;
        }

        private static void Scan(string modDirectory, IMonitor monitor)
        {
            if (_byExactSheet != null)
                return;
            _byExactSheet = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            _bySuffix = new List<(string, string)>();

            string? mods = LabelPacks.FindModsRoot(modDirectory);
            if (mods == null)
                return;
            List<string> patchFiles;
            try
            {
                // Every json in a pack, not only content.json: a pack of any size splits its
                // patches across files and pulls them in with an Include, and the targets then
                // live in the file that was included rather than in the one that included it.
                patchFiles = new List<string>(Directory.EnumerateFiles(mods, "*.json", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not read the Mods folder to see which packs repaint map art: {ex.Message}", LogLevel.Trace);
                return;
            }

            foreach (string file in patchFiles)
            {
                string name = Path.GetFileName(file);
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaximumPatchFileBytes)
                        continue;
                    text = File.ReadAllText(file);
                }
                catch { continue; }
                if (text.IndexOf("\"Target\"", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string pack = PackNameFor(file, mods);
                foreach (Match match in TargetPattern.Matches(text))
                {
                    foreach (string target in match.Groups[1].Value.Split(','))
                    {
                        string trimmed = target.Trim();
                        if (!trimmed.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
                            && !trimmed.StartsWith("Maps\\", StringComparison.OrdinalIgnoreCase))
                            continue;
                        Record(trimmed, pack);
                    }
                }
            }
        }

        private static void Record(string target, string pack)
        {
            string sheet = LabelStore.NormalizeSheet(target);
            int token = sheet.IndexOf("{{", StringComparison.Ordinal);
            if (token < 0)
            {
                Add(sheet, pack);
                return;
            }
            // {{season}} is the one token worth expanding: it is how nearly every pack writes the
            // four seasonal copies of a sheet, and expanding it is the difference between naming
            // the pack that repainted the town and naming nobody.
            int close = sheet.IndexOf("}}", token, StringComparison.Ordinal);
            string inside = close > token ? sheet[(token + 2)..close].Trim() : "";
            if (close > token && inside.Equals("season", StringComparison.OrdinalIgnoreCase))
            {
                string before = sheet[..token], after = sheet[(close + 2)..];
                foreach (string season in Seasons)
                    Add(before + season + after, pack);
                return;
            }
            // Any other token: keep the part after it and match by ending, which is loose on
            // purpose. Naming one pack too many in a report costs a reader a second; naming none
            // costs them the answer.
            string tail = close > token ? sheet[(close + 2)..] : sheet[(token + 2)..];
            if (tail.Length >= 3)
                _bySuffix!.Add((tail, pack));
        }

        private static void Add(string sheet, string pack)
        {
            if (!_byExactSheet!.TryGetValue(sheet, out SortedSet<string>? packs))
                _byExactSheet[sheet] = packs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            packs.Add(pack);
        }

        /// <summary>The folder a patch file belongs to, named the way a player would recognise it:
        /// the nearest folder above holding a manifest.json, which is the pack itself rather than
        /// the category folder somebody filed it under.</summary>
        private static string PackNameFor(string file, string modsRoot)
        {
            string root = Path.GetFullPath(modsRoot);
            string? at = Path.GetDirectoryName(Path.GetFullPath(file));
            while (at != null && at.Length > root.Length)
            {
                if (File.Exists(Path.Combine(at, "manifest.json")))
                    return Path.GetFileName(at);
                at = Path.GetDirectoryName(at);
            }
            return Path.GetFileName(Path.GetDirectoryName(file) ?? "") ;
        }
    }
}
