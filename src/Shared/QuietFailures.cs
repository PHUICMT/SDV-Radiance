using System;
using System.Collections.Generic;
using System.Text;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// The places where this mod lets something fail without stopping, and says so once.
    /// </summary>
    /// <remarks>
    /// <para>Some calls are allowed to fail: drawing a critter, a held tool or an emote into the
    /// water mask runs the game's and other mods' own draw code, and one that throws must cost that
    /// stamp rather than the frame. Those sites used to catch and say nothing at all, so a report
    /// of something going wrong (a phone striped black, a farmer frozen in place) came with a log
    /// that could not show whether any of them had fired.</para>
    /// <para>Each site is written to the log the first time it fails with a given kind of exception,
    /// as a trace line with the message, and counted every time after. radiance_report lists the
    /// counts. Nothing here costs anything on a frame where nothing fails.</para>
    /// </remarks>
    internal static class QuietFailures
    {
        internal static IMonitor? Monitor;

        private static readonly Dictionary<string, int> _counts = [];

        /// <summary>Note that <paramref name="where"/> failed and carried on.</summary>
        internal static void Note(string where, Exception exception)
        {
            string key = where + " (" + exception.GetType().Name + ")";
            _counts.TryGetValue(key, out int count);
            _counts[key] = count + 1;
            if (count == 0)
                Monitor?.Log($"[carried on] {key}: {exception.Message}", LogLevel.Trace);
        }

        /// <summary>One block for radiance_report.</summary>
        internal static string Describe()
        {
            if (_counts.Count == 0)
                return "carried-on failures: none since the game started";
            var text = new StringBuilder("carried-on failures since the game started (first one of each is in the log as [carried on]):");
            foreach (KeyValuePair<string, int> pair in _counts)
                text.Append("\n  ").Append(pair.Value).Append(" x ").Append(pair.Key);
            return text.ToString();
        }
    }
}
