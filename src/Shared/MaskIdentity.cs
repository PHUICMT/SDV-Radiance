using System;
using System.Runtime.CompilerServices;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>What a water answer was built from: the location, the labels' version, the mask
    /// epoch (world events that change what is water) and the draw hook's version (tiles the game
    /// was first seen drawing water on).
    ///
    /// <para>Four caches outlive the job that built them: the window's water mask, the map-wide
    /// shoreline, a gather part way through, and the per-tile gather answers. Each used to test its
    /// own validity field by field, with a different subset each, which is how a field added to one
    /// test and not the others stays invisible until someone reports what it breaks. They all
    /// compare one of these now; the two that deliberately do not care about newly drawn water say
    /// so by calling <see cref="SameIgnoringDrawnWater"/>.</para>
    ///
    /// <para>The location is compared as an instance, exactly as the <c>==</c> these tests used.
    /// A record would otherwise call <see cref="GameLocation.Equals(GameLocation)"/>, which compares
    /// names, and two instances of one name (a location rebuilt by a mod) are not the same map.</para></summary>
    internal readonly record struct MaskIdentity(GameLocation? Location, int LabelVersion, int Epoch, int WaterDrawHookVersion)
    {
        /// <summary>Matches nothing a real build produces.</summary>
        internal static readonly MaskIdentity None = new(null, -1, -1, -1);

        public bool Equals(MaskIdentity other) =>
            ReferenceEquals(Location, other.Location)
            && LabelVersion == other.LabelVersion
            && Epoch == other.Epoch
            && WaterDrawHookVersion == other.WaterDrawHookVersion;

        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(Location), LabelVersion, Epoch, WaterDrawHookVersion);

        /// <summary>The same location, labels and epoch, whatever the draw hook has seen since.</summary>
        internal bool SameIgnoringDrawnWater(MaskIdentity other) =>
            Equals(other with { WaterDrawHookVersion = WaterDrawHookVersion });
    }
}
