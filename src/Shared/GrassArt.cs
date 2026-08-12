using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley.TerrainFeatures;

namespace SDVRadiance
{
    /// <summary>
    /// Where a tuft of grass actually puts its blades.
    ///
    /// <para>
    /// Grass is not one sprite. <c>Grass.draw</c> lays down up to four separate 15x20 blades at
    /// jittered spots inside the tile, and the jitter lives in private arrays that are rolled once
    /// when the tuft is created. Anything that has to agree with what the player sees - the water
    /// mirror, the sun shadow - has to read those arrays rather than approximate them, because both
    /// of those things are drawn immediately beside the grass itself, where an approximation reads
    /// as a fault rather than as a simplification.
    /// </para>
    ///
    /// <para>
    /// One place owns the layout so the mirror and the shadow cannot drift apart later. The Harmony
    /// field accessors are built once and then cost a direct field read.
    /// </para>
    /// </summary>
    internal static class GrassArt
    {
        private static readonly AccessTools.FieldRef<Grass, int[]> WhichWeed
            = AccessTools.FieldRefAccess<Grass, int[]>("whichWeed");
        private static readonly AccessTools.FieldRef<Grass, int[]> OffsetX
            = AccessTools.FieldRefAccess<Grass, int[]>("offset3");
        private static readonly AccessTools.FieldRef<Grass, int[]> OffsetY
            = AccessTools.FieldRefAccess<Grass, int[]>("offset4");

        /// <summary>A blade's source frame is 15x20 on the grass sheet.</summary>
        internal const int BladeWidth = 15;
        internal const int BladeHeight = 20;

        /// <summary>Grass.draw's origin: 7.5, 17.5. Two and a half source rows sit BELOW it, so the
        /// blade's contact with the ground is what the anchor point marks and the rows under it hang
        /// past the ground line the way a leaf does.</summary>
        internal static readonly Vector2 BladeOrigin = new(BladeWidth / 2f, 17.5f);

        /// <summary>How many blades this tuft draws, and their jitter, or false if the game's own
        /// layout could not be read (a future version renaming a field must cost the effect, never
        /// a wrong picture).</summary>
        internal static bool TryRead(Grass grass, out int blades, out int[] which, out int[] offsetX, out int[] offsetY)
        {
            blades = 0;
            which = offsetX = offsetY = System.Array.Empty<int>();
            int[]? w = Read(WhichWeed, grass), ox = Read(OffsetX, grass), oy = Read(OffsetY, grass);
            if (w == null || ox == null || oy == null)
                return false;
            blades = System.Math.Min(System.Math.Min(4, grass.numberOfWeeds.Value),
                                     System.Math.Min(w.Length, System.Math.Min(ox.Length, oy.Length)));
            which = w; offsetX = ox; offsetY = oy;
            return blades > 0;
        }

        private static int[]? Read(AccessTools.FieldRef<Grass, int[]> field, Grass grass)
        {
            try { return field(grass); }
            catch { return null; }
        }

        /// <summary>Blade i's anchor in world pixels. Grass.draw:
        /// tile*64 + (i%2 * 32 + offset3[i]*4 - 4 + 30, i/2 * 32 + offset4[i]*4 + 40).</summary>
        internal static Vector2 BladeAt(Vector2 tile, int i, int[] offsetX, int[] offsetY) =>
            tile * 64f + new Vector2(i % 2 * 32 + offsetX[i] * 4 - 4 + 30, i / 2 * 32 + offsetY[i] * 4 + 40);

        /// <summary>Blade i's source frame on the tuft's own sheet.</summary>
        internal static Microsoft.Xna.Framework.Rectangle BladeSource(Grass grass, int i, int[] which) =>
            new(which[i] * BladeWidth, grass.grassSourceOffset.Value, BladeWidth, BladeHeight);

        /// <summary>
        /// The same frame with everything below the ground line trimmed off, for the SHADOW.
        ///
        /// <para>
        /// A shadow is cast by what stands above the ground, and those last two and a half rows are
        /// the widest part of the blade. Cast, they landed BELOW the anchor: the vertical squash
        /// pushes them toward the feet but never past them, so they piled into a couple of rows
        /// right under the tuft, at full strength (the feet-to-head fade starts fading only
        /// upward), four blades deep. That is the near-black pool that was reported twice in one
        /// morning, once as "too dark at the base" and once as "the shadow is in front of the
        /// plant instead of behind it" - one artifact, seen from two sides.
        /// </para>
        ///
        /// <para>The mirror still uses the whole blade: a reflection is a mirror image, not a cast.</para>
        /// </summary>
        internal static Microsoft.Xna.Framework.Rectangle BladeShadowSource(Grass grass, int i, int[] which) =>
            new(which[i] * BladeWidth, grass.grassSourceOffset.Value, BladeWidth, (int)BladeOrigin.Y + 1);
    }
}
