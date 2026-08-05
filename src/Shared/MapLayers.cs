using System;

namespace SDVRadiance
{
    /// <summary>Helpers for xTile map-layer identity shared by the water mask and reflection bakes.</summary>
    internal static class MapLayers
    {
        /// <summary>
        /// Whether a layer id belongs to a layer family: "Back", "Back2", "Back37" are all the
        /// Back family, while "Back-1" (a disabled layer) and "Backdrop" are not — after the
        /// family prefix only digits may follow.
        /// </summary>
        internal static bool BelongsToFamily(string layerId, string family)
        {
            if (!layerId.StartsWith(family, StringComparison.Ordinal))
                return false;
            for (int i = family.Length; i < layerId.Length; i++)
                if (layerId[i] < '0' || layerId[i] > '9') return false;
            return true;
        }
    }
}
