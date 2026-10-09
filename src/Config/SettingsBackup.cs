using System;
using System.IO;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// A copy of the settings kept outside the mod's folder, so updating the mod cannot take them away.
    /// </summary>
    /// <remarks>
    /// <para>The settings live in config.json inside the mod's folder, and a mod manager updating the
    /// mod (Vortex, from the Nexus page) replaces that whole folder: the player's settings went with
    /// it and came back as the defaults (reported by dulcinee on Nexus). Every write of config.json
    /// now writes the same settings to SMAPI's per-mod data folder as well, which lives with the
    /// game's own data rather than with the mod.</para>
    /// <para>At launch, a missing config.json with a copy on hand means the folder was replaced, and
    /// the copy is put back. A config.json that is there always wins, so a player who edits the file
    /// by hand or pastes an old one back gets exactly what the file says.</para>
    /// </remarks>
    internal static class SettingsBackup
    {
        private const string CopyKey = "config-backup";

        /// <summary>This launch had neither a config.json nor a kept copy: a first install, starting from the defaults.</summary>
        internal static bool FreshInstall { get; private set; }

        /// <summary>The settings for this launch: config.json, or the kept copy when the file is gone.</summary>
        internal static ModConfig Load(IModHelper helper, IMonitor monitor)
        {
            // Asked before ReadConfig, which writes a config.json of defaults when there is none.
            bool configOnDisk = File.Exists(Path.Combine(helper.DirectoryPath, "config.json"));
            if (!configOnDisk)
            {
                ModConfig? kept = ReadCopy(helper);
                if (kept != null)
                {
                    helper.WriteConfig(kept);
                    monitor.Log("config.json was missing, which is what updating through a mod manager does, so your "
                        + "settings were brought back from the copy the mod keeps. To start from the defaults instead, "
                        + $"delete config.json and {CopyKey}.json (in StardewValley/.smapi/mod-data) with the game closed.",
                        LogLevel.Info);
                    return kept;
                }
            }
            FreshInstall = !configOnDisk;
            ModConfig config = helper.ReadConfig<ModConfig>();
            // Kept on every launch, not only on a save: a player who never opens the tuner after this
            // version arrives still has a copy before the next update replaces the folder.
            KeepCopy(helper, config);
            return config;
        }

        /// <summary>Write config.json and the kept copy together. Every save of the settings goes through here.</summary>
        internal static void WriteConfig(IModHelper helper, ModConfig config)
        {
            helper.WriteConfig(config);
            KeepCopy(helper, config);
        }

        private static ModConfig? ReadCopy(IModHelper helper)
        {
            try
            {
                return helper.Data.ReadGlobalData<ModConfig>(CopyKey);
            }
            catch (Exception exception)
            {
                QuietFailures.Note("reading the kept settings", exception);
                return null;
            }
        }

        private static void KeepCopy(IModHelper helper, ModConfig config)
        {
            try
            {
                helper.Data.WriteGlobalData(CopyKey, config);
            }
            catch (Exception exception)
            {
                QuietFailures.Note("keeping a copy of the settings", exception);
            }
        }
    }
}
