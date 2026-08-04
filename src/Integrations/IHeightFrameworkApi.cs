using StardewValley;

namespace SDVRadiance.Integrations
{
    /// <summary>
    /// Local COPY of Height Framework's public API surface (phuicmt.HeightFramework). SMAPI proxies
    /// the real implementation onto this interface when we call
    /// <c>ModRegistry.GetApi&lt;IHeightFrameworkApi&gt;(...)</c>, so the member signatures must match
    /// the provider's exactly. Surface class is an int: 0 Ground, 1 Water, 2 Wall, 3 Roof, 4 Deck, 5 Void.
    /// Keep in sync with SDV-HeightFramework/src/IHeightFrameworkApi.cs.
    /// </summary>
    public interface IHeightFrameworkApi
    {
        int GetSurfaceAt(GameLocation location, int tileX, int tileY);
        int GetHeightAt(GameLocation location, int tileX, int tileY);
        bool IsOccluder(GameLocation location, int tileX, int tileY);
        bool IsWaterSurface(GameLocation location, int tileX, int tileY);
    }
}
