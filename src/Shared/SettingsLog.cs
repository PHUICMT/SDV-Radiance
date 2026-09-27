using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Reflection;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// One line in the SMAPI log naming every setting that differs from its default, so a log a
    /// player uploads says how their Radiance is set up.
    /// </summary>
    /// <remarks>
    /// <para>A log used to say nothing about the settings at all, and a report like "the water
    /// flickers" then meant guessing whether Smooth art, the reflections or the G.I. were on. Only
    /// the changed settings are written, because most players change a handful and the defaults
    /// are known.</para>
    /// <para>Written when a save loads, and again after the settings change, once the menu that
    /// changed them has closed, so dragging a slider does not write a line for every step. At
    /// TRACE: it is in every uploaded log and stays out of the player's console.</para>
    /// </remarks>
    internal static class SettingsLog
    {
        private static IMonitor? _monitor;
        private static Func<ModConfig>? _config;
        private static bool _changed;
        private static string? _lastWritten;

        internal static void Install(IModHelper helper, IMonitor monitor, Func<ModConfig> config)
        {
            _monitor = monitor;
            _config = config;
            helper.Events.GameLoop.SaveLoaded += (_, _) => Write("save loaded");
            helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) =>
            {
                if (_changed && StardewValley.Game1.activeClickableMenu == null)
                {
                    _changed = false;
                    Write("changed");
                }
            };
        }

        /// <summary>The settings were saved; write them once the menu is closed.</summary>
        internal static void MarkChanged() => _changed = true;

        private static void Write(string when)
        {
            if (_monitor == null || _config == null)
                return;
            string line;
            try
            {
                line = Describe(_config());
            }
            catch (Exception ex)
            {
                _monitor.Log($"settings could not be listed: {ex.Message}", LogLevel.Trace);
                return;
            }
            if (line == _lastWritten)
                return;
            _lastWritten = line;
            _monitor.Log($"settings ({when}): {line}", LogLevel.Trace);
        }

        /// <summary>Every setting that differs from a fresh config, as <c>Name=value</c>.</summary>
        internal static string Describe(ModConfig config)
        {
            var defaults = new ModConfig();
            var changed = new List<string>();
            foreach (PropertyInfo property in typeof(ModConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0
                    || NotASetting.Contains(property.Name))
                    continue;
                string now = Show(property.GetValue(config));
                if (now == Show(property.GetValue(defaults)))
                    continue;
                changed.Add($"{property.Name}={(now.Length > 80 ? now[..77] + "..." : now)}");
            }
            return changed.Count == 0
                ? "all defaults"
                : $"{changed.Count} changed from default: {string.Join(", ", changed)}";
        }

        /// <summary>What the config file also keeps that is not how the game looks: where the F6
        /// menu was left, the file's own version, a one-time compatibility step, and the saved
        /// profiles (the one in use is already in the settings around them).</summary>
        private static readonly HashSet<string> NotASetting = new(StringComparer.Ordinal)
        {
            nameof(ModConfig.ConfigVersion), nameof(ModConfig.TunerFoldedSections), nameof(ModConfig.TunerShowFineTuning),
            nameof(ModConfig.TunerLastTab), nameof(ModConfig.TunerScrollByTab), nameof(ModConfig.WindowCompatAppliedFor),
            nameof(ModConfig.SavedProfiles),
        };

        /// <summary>A value as the log shows it; also what two values are compared by.</summary>
        private static string Show(object? value) => value switch
        {
            null => "null",
            float number => number.ToString("0.###", CultureInfo.InvariantCulture),
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            string text => text,
            IDictionary map => "{" + string.Join(", ", EntriesOf(map)) + "}",
            IEnumerable list => "[" + string.Join(", ", ItemsOf(list)) + "]",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

        private static IEnumerable<string> EntriesOf(IDictionary map)
        {
            foreach (DictionaryEntry entry in map)
                yield return $"{entry.Key}:{Show(entry.Value)}";
        }

        private static IEnumerable<string> ItemsOf(IEnumerable list)
        {
            foreach (object? item in list)
                yield return Show(item);
        }
    }
}
