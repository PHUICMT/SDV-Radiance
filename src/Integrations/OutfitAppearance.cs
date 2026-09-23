using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance.Integrations
{
    /// <summary>
    /// The part of an outfit mod's API the shadow bakes read: a number that moves whenever the
    /// farmer's look changes, whether a piece is animating, and how far its drawing reached. SMAPI
    /// maps these members onto the mod's own interface.
    /// </summary>
    public interface IOutfitAppearanceApi
    {
        /// <summary>A number that moves whenever anything about how the farmer is drawn changes,
        /// an animation frame of a worn piece included.</summary>
        int GetAppearanceVersion(Farmer? target = null);

        /// <summary>Whether a worn piece is stepping through frames on its own right now.</summary>
        bool IsAnimating(Farmer? target = null);

        /// <summary>How far the last drawing of the farmer reached, measured from the top left of the
        /// game's own frame at 64 by 128; false before the outfit mod has drawn them.</summary>
        KeyValuePair<bool, Rectangle> GetDrawnBounds(Farmer? target = null);
    }

    /// <summary>
    /// What the outfit mod says about how a farmer looks, for the shadow bakes. With the outfit mod installed a baked
    /// silhouette is known to be stale the moment its look number moves, which is both sooner than any
    /// timer would notice (a piece switched by the outfit mod's own rules, a frame of animated hair) and later
    /// than a timer would throw it away (a farmer standing still in the same clothes). Without the outfit mod
    /// every answer is the one that changes nothing: a number that never moves and no animation.
    /// </summary>
    internal static class OutfitAppearance
    {
        private const string OutfitModId = "phuicmt.Guise";

        private static IOutfitAppearanceApi? _api;

        /// <summary>Whether the outfit mod is installed and answering.</summary>
        internal static bool Connected => _api != null;

        internal static void Connect(IModRegistry registry, IMonitor monitor)
        {
            if (!registry.IsLoaded(OutfitModId))
                return;

            try
            {
                _api = registry.GetApi<IOutfitAppearanceApi>(OutfitModId);
            }
            catch (System.Exception error)
            {
                monitor.Log($"The outfit mod is installed but its API could not be read, so its farmers are re-baked on the old timer: {error.Message}", LogLevel.Warn);
            }

            if (_api != null)
                monitor.Log("Outfit mod found: player shadows are re-baked when its look number moves instead of on a timer.", LogLevel.Trace);
        }

        /// <summary>The outfit mod's look number for this farmer, or zero when there is none to ask.</summary>
        internal static int VersionOf(Farmer who) => _api?.GetAppearanceVersion(who) ?? 0;

        /// <summary>Whether a piece the outfit mod draws on this farmer is animating now; false without the outfit mod.</summary>
        internal static bool IsAnimating(Farmer who) => _api?.IsAnimating(who) ?? false;

        /// <summary>How far the outfit mod's last drawing of this farmer reached around their own 64 by 128
        /// frame, or null without the outfit mod or before it has drawn them.</summary>
        internal static Rectangle? DrawnBoundsOf(Farmer who) =>
            _api?.GetDrawnBounds(who) is { Key: true } bounds ? bounds.Value : null;
    }
}
