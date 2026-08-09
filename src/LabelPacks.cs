using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Label packs that OTHER mods ship, so an author can label their own art and hand it out with
    /// their own mod instead of waiting for this one to bundle it.
    ///
    /// A pack is a file named radiance-labels.json inside a mod's folder, in the same shape as the
    /// bundled labels. What stops it being a way to repaint anybody's water is the ownership rule:
    ///
    ///   a pack may only paint sheets whose .png exists somewhere inside the mod folder it sits in.
    ///
    /// Ownership is therefore a fact about where the art is, not a claim the pack makes about
    /// itself. A declared owner could simply be written down wrongly, or written down dishonestly;
    /// a file either sits beside the art it paints or it does not. The bundled labels are the only
    /// ones allowed to paint anything, because they are this mod's own ground truth for vanilla art.
    ///
    /// "The mod folder it sits in" means the nearest folder above it holding a manifest.json, which
    /// is the mod or content pack that shipped it, and not the top folder under Mods. The two are
    /// often different: bundles like <c>Mods/[CP] Something/InnerPack/</c> are normal, and taking the
    /// top folder would let one pack in a bundle paint every other pack's art in the same bundle.
    ///
    /// Cost, measured on an install with 9,230 files under Mods: 120 ms for the one recursive search
    /// at GameLaunched, and the art of a mod folder is only enumerated when that mod actually ships
    /// a pack. This started as a search two folders deep, which was cheaper and wrong: 47 of the
    /// manifests on that install are deeper than two, so it would have quietly ignored most bundles.
    /// </summary>
    internal sealed class LabelPack
    {
        public LabelPack(string filePath, string owningModFolder, HashSet<string> ownedSheets, string? producedFor)
        {
            this.FilePath = filePath;
            this.OwningModFolder = owningModFolder;
            this.OwnedSheets = ownedSheets;
            this.ProducedFor = producedFor;
        }

        public string FilePath { get; }

        /// <summary>The owning mod's folder, relative to Mods. Its art is this pack's to paint.</summary>
        public string OwningModFolder { get; }

        /// <summary>Normalized names of every tilesheet the owning mod folder actually contains.</summary>
        public HashSet<string> OwnedSheets { get; }

        /// <summary>What the pack says it was made for. Reported, never trusted for ownership.</summary>
        public string? ProducedFor { get; }

        /// <summary>Short enough for a log line and specific enough to find the file by.</summary>
        public string Describe() => $"Mods/{this.OwningModFolder}/{Path.GetFileName(this.FilePath)}";
    }

    internal static class LabelPacks
    {
        public const string FileName = "radiance-labels.json";

        /// <summary>
        /// The Mods folder this mod is installed in, or null if it is somewhere unrecognisable.
        /// Found by walking up rather than assuming a depth, because a mod can sit inside a
        /// content pack folder inside a mod folder.
        /// </summary>
        public static string? FindModsRoot(string modDirectory)
        {
            string? at = modDirectory;
            while (at != null && !string.Equals(Path.GetFileName(at), "Mods", StringComparison.OrdinalIgnoreCase))
                at = Path.GetDirectoryName(at);
            return at;
        }

        /// <summary>
        /// Every pack that other installed mods ship, in a fixed order so that two players with the
        /// same mods get the same result and a conflict is reproducible rather than a coin toss.
        ///
        /// "Mods (disabled)" is deliberately not searched. SMAPI does not load those mods, so their
        /// art is not in the game, so labels for it would paint nothing and could only collide.
        /// </summary>
        /// <param name="modDirectory">This mod's own folder. The only thing this needs from SMAPI,
        /// which is why it is a path and not an IModHelper: it can then be run without a game.</param>
        public static List<LabelPack> Discover(string modDirectory, IMonitor monitor)
        {
            var found = new List<LabelPack>();
            string? mods = FindModsRoot(modDirectory);
            if (mods == null)
            {
                monitor.Log("Could not find the Mods folder, so no label packs from other mods were looked for.", LogLevel.Trace);
                return found;
            }

            List<string> packFiles;
            try
            {
                packFiles = new List<string>(Directory.EnumerateFiles(mods, FileName, SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not search the Mods folder, so no label packs were loaded: {ex.Message}", LogLevel.Warn);
                return found;
            }
            if (packFiles.Count == 0)
                return found;
            packFiles.Sort(StringComparer.OrdinalIgnoreCase);

            string ownFolder = Path.GetFullPath(modDirectory);
            var ownedByFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string packFile in packFiles)
            {
                string ownerFolder = FindOwningModFolder(packFile, mods);

                // A pack sitting loose in the Mods folder itself belongs to no mod, so there is
                // nothing for it to own. Left alone it resolved to the Mods root and was granted
                // every image in every installed mod, which voids the one rule the whole feature
                // rests on: a pack may only repaint the art its own mod supplies.
                if (string.Equals(Path.GetFullPath(ownerFolder), Path.GetFullPath(mods), StringComparison.OrdinalIgnoreCase))
                {
                    monitor.Log($"Ignoring the label pack at {packFile}: it is not inside a mod folder, "
                              + "so there is no art it can claim to own.", LogLevel.Warn);
                    continue;
                }

                // This mod's own labels come from labels/, by name, so that stays the one way we
                // load ours and a stray pack in our folder cannot become a second source.
                if (string.Equals(Path.GetFullPath(ownerFolder), ownFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Enumerated once per mod folder, and only for mods that actually ship a pack.
                if (!ownedByFolder.TryGetValue(ownerFolder, out HashSet<string>? owned))
                {
                    owned = ReadOwnedSheetNames(ownerFolder, monitor);
                    ownedByFolder[ownerFolder] = owned;
                }

                string describedAs = Path.GetRelativePath(mods, ownerFolder).Replace('\\', '/');
                found.Add(new LabelPack(packFile, describedAs, owned, ReadProducedFor(packFile)));
            }

            return found;
        }

        /// <summary>
        /// The mod or content pack a file belongs to: the nearest folder above it holding a
        /// manifest.json. A pack shipped without one falls back to the top folder under Mods, which
        /// is the widest the answer can ever be and still be one installed thing.
        /// </summary>
        private static string FindOwningModFolder(string packFile, string modsRoot)
        {
            string root = Path.GetFullPath(modsRoot);
            string? at = Path.GetDirectoryName(Path.GetFullPath(packFile));
            string topMost = at ?? root;
            while (at != null && at.Length > root.Length)
            {
                if (File.Exists(Path.Combine(at, "manifest.json")))
                    return at;
                topMost = at;
                at = Path.GetDirectoryName(at);
            }
            return topMost;
        }

        /// <summary>
        /// The normalized name of every .png in a mod folder. Not a claim about which of them are
        /// tilesheets: a portrait cannot be drawn as a map tile anyway, so a label for one is dead
        /// data rather than a danger, and guessing which images are tilesheets from their shape is
        /// how the map dump ended up carrying screenshots.
        /// </summary>
        private static HashSet<string> ReadOwnedSheetNames(string modFolder, IMonitor monitor)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string image in Directory.EnumerateFiles(modFolder, "*.png", SearchOption.AllDirectories))
                    names.Add(LabelStore.NormalizeSheet(image));
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not read the art in Mods/{Path.GetFileName(modFolder)}, so its label pack owns nothing: {ex.Message}", LogLevel.Warn);
            }
            return names;
        }

        /// <summary>
        /// The optional producedFor field, read on its own so a malformed pack is still discovered
        /// and still reported. It is a note for a bug report, not a permission.
        /// </summary>
        private static string? ReadProducedFor(string packFile)
        {
            try
            {
                using FileStream stream = File.OpenRead(packFile);
                using JsonDocument document = JsonDocument.Parse(stream);
                return document.RootElement.TryGetProperty("producedFor", out JsonElement producedFor)
                    ? producedFor.GetString()
                    : null;
            }
            catch (Exception) { return null; }
        }
    }
}
