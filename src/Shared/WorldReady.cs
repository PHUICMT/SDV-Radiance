using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>Whether there is a world on screen for the mod to draw onto.</summary>
    /// <remarks>
    /// SMAPI's <see cref="Context.IsWorldReady"/> answers that everywhere but one place: a new
    /// farm's welcome. The game holds the date at day 0 from the farm's creation until the
    /// welcome cutscene ends (its last command, <c>end beginGame</c>, is what starts day 1), and
    /// SMAPI counts nothing as ready on day 0. The whole first cutscene of every new farm, Robin
    /// meeting the bus and walking you home, drew with none of the mod: no shadows of ours and
    /// the game's round ones left in place. That stretch is a world like any other, so it counts.
    /// </remarks>
    internal static class WorldReady
    {
        internal static bool Now => Context.IsWorldReady || NewFarmWelcome;

        private static bool NewFarmWelcome =>
            Game1.dayOfMonth == 0
            && Game1.hasLoadedGame
            && Game1.activeClickableMenu is not TitleMenu
            && Game1.CurrentEvent != null
            && Game1.currentLocation != null;
    }
}
