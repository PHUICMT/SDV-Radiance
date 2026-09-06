using System.Collections.Generic;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Which split screens are still being drawn, and how to stop holding state for the ones
    /// that are not.
    ///
    /// <para>Screens are numbered from zero with no gaps, so the live set is always 0 to
    /// count-1 and anything at or past the count has left. A player who drops out of a
    /// split-screen session takes their screen id with them, and every dictionary keyed by that
    /// id goes on holding whatever it held.</para>
    ///
    /// <para>The render pipeline and the shadow renderer each grew their own copy of this test
    /// when their per-screen state got big enough to matter in video memory. They still carry
    /// theirs, because they are instance methods that also have to release GPU resources and
    /// skip the screen currently being drawn. This is the plain version, for the systems whose
    /// per-screen state is ordinary arrays: nothing to release, nothing to protect, just stop
    /// keeping it.</para>
    /// </summary>
    internal static class LiveScreens
    {
        /// <summary>How many screens are being drawn. One when not in split screen, and one if
        /// the runner is not up yet, which is the safe answer either way.</summary>
        internal static int Count => GameRunner.instance?.gameInstances?.Count ?? 1;

        /// <summary>Is this screen still being drawn?</summary>
        internal static bool StillExists(int screenId) => screenId >= 0 && screenId < Count;

        /// <summary>Whether two location objects are the same place. On a split screen each game
        /// instance holds its own copy of every location (the second screen is a client of the
        /// first), so two screens standing on the same farm compare as different objects, and a
        /// cache that asked ReferenceEquals rebuilt itself at every screen switch. Measured on a
        /// two-screen farm on 2026-09-06: the object bake's whole-map arrival walk (2.2 ms) and the
        /// shadow patch's solid-tile texture (0.8 ms) ran on every frame of both screens.</summary>
        internal static bool SamePlace(GameLocation? a, GameLocation? b)
            => ReferenceEquals(a, b) || (a != null && b != null && a.NameOrUniqueName == b.NameOrUniqueName);

        /// <summary>The companion for a map object: the other screen's copy of a map is a
        /// different object with the same layers, so a cache built from one serves the other as
        /// long as the size is the size it was built for.</summary>
        internal static bool SameMapSize(xTile.Map? a, xTile.Map? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Layers.Count == 0 || b.Layers.Count == 0) return false;
            return a.Layers[0].LayerWidth == b.Layers[0].LayerWidth && a.Layers[0].LayerHeight == b.Layers[0].LayerHeight;
        }

        /// <summary>
        /// Drop the entries belonging to screens that have left.
        ///
        /// <para>The count test up front is what makes this free to call every frame: while
        /// nobody has left there is nothing to walk and nothing to allocate, and a player
        /// leaving is not something that happens often enough to optimise for.</para>
        /// </summary>
        internal static void ForgetDeparted<T>(Dictionary<int, T> byScreen)
        {
            int live = Count;
            if (byScreen.Count <= live)
                return;
            List<int>? departed = null;
            foreach (int screenId in byScreen.Keys)
            {
                if (screenId >= live)
                    (departed ??= new List<int>()).Add(screenId);
            }
            if (departed == null)
                return;
            foreach (int screenId in departed)
                byScreen.Remove(screenId);
        }
    }
}
