using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SDVRadiance
{
    /// <summary>
    /// What colour looks are actually on disk, as opposed to what shipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shader has always loaded a LUT by name, so a player could already drop a file in and
    /// type its name into config.json. What they could not do was find it in the menu: the
    /// dropdown offered a hardcoded list, so their own look was reachable only by hand-editing.
    /// This closes that gap by reading the folder instead of trusting the list.
    /// </para>
    /// <para>
    /// Two folders are read: <c>assets/luts</c> inside the mod, and <c>radiance-luts</c> beside the
    /// save games. Anything in either that is not one of ours is treated as the player's. There is
    /// deliberately no manifest and no naming rule: a file that is there is offered, and a file
    /// that is not is not. If nothing has been added, this returns nothing and the menu looks
    /// exactly as it did before, which is why an empty section never appears.
    /// </para>
    /// <para>
    /// The second folder exists because the first one is not the player's to keep. Updating the
    /// mod by unzipping over the old folder leaves extra files alone, but a mod manager installs
    /// clean and deletes them, and so does anyone who follows the usual advice to remove the old
    /// folder first. A look kept beside the saves survives all three, and survives reinstalling
    /// the game.
    /// </para>
    /// <para>
    /// Scanned once, when the menu is registered. A file added while the game is running is picked
    /// up on the next launch, not immediately: the dropdown's choices are handed to GMCM at
    /// registration and cannot be changed afterwards.
    /// </para>
    /// <para>
    /// NOT a published extension point. This is a place for a player to keep their own file; it is
    /// not a way for one mod to ship a look to another, which needs a content pack and a format,
    /// and a format becomes a promise to everyone who builds on it the day it ships. That decision
    /// belongs with the content-pack design. The two can coexist when it arrives: nothing here has
    /// to move.
    /// </para>
    /// </remarks>
    internal static class LutCatalog
    {
        /// <summary>The mod's own folder. Set once at launch rather than passed down through every
        /// caller: the menu that has to list the looks is several layers from anything holding an
        /// <c>IModHelper</c>, and this is a value that cannot change while the process lives.</summary>
        private static string _modDir = "";

        internal static void Initialise(string modDir) => _modDir = modDir;

        /// <summary>The folder beside the save games, <c>%APPDATA%/StardewValley/radiance-luts</c>.
        /// A look kept here belongs to the player rather than to the mod, so updating the mod - by
        /// hand or through a mod manager that installs clean - cannot take it away. Never created
        /// by us: a folder that exists only to be empty is clutter in someone else's directory.</summary>
        internal static string UserDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "StardewValley", "radiance-luts");
            }
        }

        /// <summary>Where a look of that name lives, or null if no file has it.</summary>
        /// <remarks>The mod's own folder is searched first, so a shipped look cannot be shadowed
        /// by a file that happens to share its name.</remarks>
        internal static string? Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            foreach (string dir in Folders())
            {
                string path = Path.Combine(dir, name + ".png");
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        /// <summary>Every look on disk that did not ship with the mod, in the order offered.</summary>
        internal static string[] Discover()
        {
            var shipped = new HashSet<string>(ModConfig.ShippedLuts, StringComparer.OrdinalIgnoreCase);
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in Folders())
            {
                try
                {
                    if (!Directory.Exists(dir))
                        continue;
                    foreach (string? name in Directory.EnumerateFiles(dir, "*.png")
                                 .Select(Path.GetFileNameWithoutExtension)
                                 .Where(n => !string.IsNullOrWhiteSpace(n))
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                        if (!shipped.Contains(name!) && seen.Add(name!))
                            found.Add(name!);
                }
                catch (Exception)
                {
                    // A look the player cannot select is a smaller problem than a menu that will
                    // not open, and this runs while the config menu is being built. One unreadable
                    // folder must not cost the other one.
                }
            }
            return found.ToArray();
        }

        /// <summary>The folders a look can live in, searched in this order.</summary>
        private static IEnumerable<string> Folders()
        {
            yield return Path.Combine(_modDir, "assets", "luts");
            yield return UserDir;
        }
    }
}
