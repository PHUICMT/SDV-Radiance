using System;
using System.Collections.Generic;
using System.Diagnostics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>How much of a frame the whole-map walks may spend between them. Shared, so that
    /// three scans coming due on the same frame cost one slice and not three.
    ///
    /// <para>Four milliseconds is a quarter of a sixty-frame budget, which finishes a 163x156 farm
    /// in about a tenth of a second: the walk that was reported at 34.6 ms in one lump, and at 41.4
    /// and 47.3 ms on entering that map, now arrives over ten or so frames with the previous
    /// answer still on screen the whole time. A bigger slice would finish sooner and be felt; a
    /// smaller one would leave a newly reloaded map lit by the old map's windows for longer than a
    /// player would forgive.</para></summary>
    internal sealed class MapScanBudget
    {
        private const double MillisecondsPerFrame = 4.0;
        private readonly Stopwatch _spentThisFrame = new();

        /// <summary>Hand the scans a fresh slice. Called once at the top of a frame's work.</summary>
        internal void BeginFrame() => _spentThisFrame.Reset();

        internal bool Exhausted => _spentThisFrame.Elapsed.TotalMilliseconds >= MillisecondsPerFrame;

        internal void Resume() => _spentThisFrame.Start();

        internal void Pause() => _spentThisFrame.Stop();
    }

    /// <summary>One location's answer to a whole-map question, and the walk that is building the
    /// next one. <see cref="Found"/> stays readable and correct for the key it was gathered for
    /// while <see cref="Building"/> fills up behind it, which is what lets the walk be spread over
    /// frames without the room going dark in the middle of it.</summary>
    internal sealed class WholeMapAnswer<TFound>
    {
        /// <summary>What <see cref="Found"/> is the answer to. The sentinel matches no real key, so
        /// a fresh object always asks for a walk.</summary>
        internal MapAnswerKey Key = new(-1, -1);
        internal readonly List<TFound> Found = [];

        internal MapAnswerKey BuildingKey = new(-1, -1);
        internal readonly List<TFound> Building = [];

        /// <summary>The next map row the walk owes, or -1 when no walk is in flight.</summary>
        internal int NextRow = -1;

        /// <summary>What the walk that produced <see cref="Found"/> cost, all its slices added up,
        /// and how many frames it was spread over. Both are for the log line: a scan that reports
        /// only its last slice would read as free and hide the very thing worth watching.</summary>
        internal double ScanMilliseconds;
        internal int ScanFrames;

        /// <summary>When this answer was last asked for. The caches hold a handful of locations
        /// and drop the one nobody has wanted for longest, which needs a stamp rather than a guess
        /// at what a dictionary will hand back first.</summary>
        internal long LastAskedFor;
    }

    /// <summary>A walk over every row of a map, spread across frames.
    ///
    /// <para>Three of these run: the window lights, the emissive tiles and the window panes. Each
    /// is a walk over w by h tiles times every drawn layer, and each used to happen in one frame.
    /// On a 163x156 farm that was reported as 41.4 and 47.3 ms on entering the map and 34.6 and
    /// 32.3 ms again whenever a pack re-patched a tile sheet the map draws from. A rebuild is
    /// correct in both cases; doing it between two frames of a game running at sixty is not.</para></summary>
    internal static class WholeMapScan
    {
        private static long _asksSoFar;

        /// <summary>The next value for <see cref="WholeMapAnswer{TFound}.LastAskedFor"/>. One
        /// counter for every scan, so that "least recently asked for" compares across them.</summary>
        internal static long NextAskStamp() => ++_asksSoFar;

        /// <summary>Give one location's walk whatever is left of this frame's slice. Returns true
        /// on the frame the answer is republished, which is where a caller does any finishing work
        /// of its own.</summary>
        internal static bool Advance<TFound>(WholeMapAnswer<TFound> answer, GameLocation location,
                                             MapAnswerKey key, int mapTilesHigh, MapScanBudget budget,
                                             Action<GameLocation, int, List<TFound>> scanRow)
        {
            if (answer.Key == key)
                return false;
            if (answer.NextRow < 0 || answer.BuildingKey != key)
            {
                answer.BuildingKey = key;
                answer.Building.Clear();
                answer.NextRow = 0;
                answer.ScanMilliseconds = 0;
                answer.ScanFrames = 0;
            }
            answer.ScanFrames++;
            long startedAt = Stopwatch.GetTimestamp();
            budget.Resume();
            try
            {
                // At least one row every frame, whatever the other scans have already spent: a
                // scan that can be starved by the two beside it never finishes at all.
                while (answer.NextRow < mapTilesHigh)
                {
                    scanRow(location, answer.NextRow, answer.Building);
                    answer.NextRow++;
                    if (budget.Exhausted)
                        break;
                }
            }
            finally
            {
                budget.Pause();
                answer.ScanMilliseconds += (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            }

            if (answer.NextRow < mapTilesHigh)
                return false;
            answer.Found.Clear();
            answer.Found.AddRange(answer.Building);
            answer.Building.Clear();
            answer.Key = key;
            answer.NextRow = -1;
            return true;
        }
    }
}
