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

        /// <summary>Whether the weather here lands as liquid water: raining, and not while it is
        /// also snowing.
        ///
        /// <para>
        /// The game sets one weather at a time, so it never has to choose; a weather mod can set
        /// several flags at once and they do. Weather Wonders' blizzard, which Cloudy Skies draws,
        /// declares raining, snowing and windy together, and every part of this mod that wanted
        /// rain read the rain flag on its own, so a snowstorm arrived with raindrops running down
        /// the screen, puddles under the falling snow and raindrop rings on the water.
        /// </para>
        /// <para>
        /// Everything that asks what the weather LEAVES behind asks this. What FALLS still honours
        /// both flags, because rain and snow together is a weather a pack can mean.
        /// </para></summary>
        internal static bool RainLandsOn(GameLocation? location) =>
            location is not null && location.IsRainingHere() && !location.IsSnowingHere();

        /// <inheritdoc cref="RainLandsOn"/>
        internal static bool RainLandsHere => RainLandsOn(Game1.currentLocation);
    }
}
