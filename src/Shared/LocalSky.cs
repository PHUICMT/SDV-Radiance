using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The season and the weather where the screen's player is standing.
    ///
    /// <para>
    /// Game1.season and Game1.isRaining describe the valley. Ginger Island is always summer and has
    /// weather of its own, and the desert has its own sky, so reading the valley there put snow
    /// glitter on the island's sand in the valley's winter, an aurora over a tropical sea, and
    /// stopped the island's fireflies whenever the valley was not in summer. The game answers the
    /// question per location (GetSeason and GetWeather follow the location's context), and in the
    /// valley, indoors included, the answer is the same one Game1 gives.
    /// </para>
    /// </summary>
    internal static class LocalSky
    {
        internal static Season Season => Game1.currentLocation?.GetSeason() ?? Game1.season;
        internal static bool IsRaining => Game1.currentLocation?.IsRainingHere() ?? Game1.isRaining;
        internal static bool IsSnowing => Game1.currentLocation?.IsSnowingHere() ?? Game1.isSnowing;
        internal static bool IsLightning => Game1.currentLocation?.IsLightningHere() ?? Game1.isLightning;
    }
}
