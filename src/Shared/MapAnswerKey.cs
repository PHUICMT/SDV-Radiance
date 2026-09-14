using StardewValley;

namespace SDVRadiance
{
    /// <summary>What an answer gathered from a whole map is keyed on: the labels' own version, and
    /// how many times the map under that location has been reloaded.
    ///
    /// <para>Four caches read the map through the labels and keep the result until something can
    /// have changed it: the window lights, the emissive tiles, the window panes and the water
    /// breath sources. All four were keyed on the label version alone, and that number moved on
    /// EVERY asset invalidation of any kind, because the handler behind it forgot every art verdict
    /// whatever had reloaded. A mod that invalidates a data asset on a timer therefore rebuilt all
    /// four every few seconds: reported on a 5800X as a 15 to 17 ms window scan and a 14 to 25 ms
    /// emissive scan in town, and 34.6 and 32.3 ms on a 163x156 farm, for a dictionary of buffs
    /// that cannot change what a tile is made of.</para>
    ///
    /// <para>So the label version now moves only when a reloaded sheet is one the labels actually
    /// hold, and the map half rides here beside it: a map re-patched in place can move a window
    /// without a single label changing its mind, and until now that case was only ever caught by
    /// the accident this pair replaces.</para></summary>
    internal readonly record struct MapAnswerKey(int LabelVersion, int MapReloadCount)
    {
        internal static MapAnswerKey For(GameLocation? location)
            => new(LabelStore.Instance?.Version ?? 0, SurfaceMap.MapReloadCount(location));

        /// <summary>No labels are loaded at all, so a scan driven by labels cannot find anything
        /// and does not need to run. Version zero means an empty store, never a loaded one.</summary>
        internal bool NoLabels => this.LabelVersion == 0;
    }
}
