using Microsoft.Xna.Framework;

namespace SDVRadiance
{
    /// <summary>
    /// A short number standing for the ART of one tile, so a label painted on one picture can say
    /// whether it is still looking at that picture.
    ///
    /// <para>
    /// Labels attach to a tilesheet NAME and a tile index, and an art mod that repaints a sheet
    /// keeps both of those. Elle's Town Buildings writes 43 patches straight into
    /// <c>Maps/{{season}}_town</c>, so 78 of the 86 window tiles painted on the town sheet end up
    /// describing art that is no longer there, and the reflection lands where the base game's
    /// window used to be. That was reported as "the window reflections are applied to the vanilla
    /// buildings and don't match up", and it is not a bug in the reflection: the name cannot tell
    /// two pictures apart. The pixels can.
    /// </para>
    ///
    /// <para>
    /// Hashed from the SAME <see cref="Color"/>[256] the water mask reads, which comes out of
    /// <c>Texture2D.GetData</c> and is therefore premultiplied alpha, whatever the graphics card
    /// happens to be holding. Reading it any other way is the one thing that can make the two
    /// sides disagree for ever: <c>Texture2D.SaveAsPng</c> un-premultiplies on the way out, so a
    /// fingerprint taken from a dumped PNG could never match one taken in the game. Both the
    /// generator and the lookup go through GetData for exactly that reason.
    /// </para>
    /// </summary>
    internal static class ArtFingerprint
    {
        private const ulong OffsetBasis = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        /// <summary>
        /// FNV-1a, 64 bit, over the 256 pixels packed red, green, blue, alpha.
        /// </summary>
        /// <remarks>
        /// Not a security hash and it does not need to be: it only has to separate one piece of
        /// art from another, and 64 bits does that with room to spare for the few thousand tiles
        /// anybody ever labels. A fully transparent tile hashes the same as every other fully
        /// transparent tile, which is correct - blank art IS the same art.
        /// </remarks>
        internal static ulong OfTilePixels(Color[] pixels)
        {
            ulong hash = OffsetBasis;
            int count = pixels.Length < 256 ? pixels.Length : 256;
            for (int i = 0; i < count; i++)
            {
                Color pixel = pixels[i];
                hash = (hash ^ pixel.R) * Prime;
                hash = (hash ^ pixel.G) * Prime;
                hash = (hash ^ pixel.B) * Prime;
                hash = (hash ^ pixel.A) * Prime;
            }
            return hash;
        }

        /// <summary>The written form, in label files and in the log: sixteen lower-case hex
        /// digits, fixed width so a column of them lines up and a truncated one is obvious.</summary>
        internal static string ToText(ulong fingerprint) => fingerprint.ToString("x16");
    }
}
