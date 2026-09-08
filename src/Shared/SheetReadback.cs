using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// Reads a whole tilesheet back from the card once, for the two callers that answer questions
    /// about art from its pixels.
    /// </summary>
    /// <remarks>
    /// <para>Both callers used to read the sheet in strips of 512 rows, and both said in a comment
    /// that this was to keep the driver's staging cost bounded for a huge sheet. It does the
    /// opposite. <c>Texture2D.PlatformGetData</c> in the game's MonoGame allocates an array of the
    /// WHOLE level and calls <c>glGetTexImage</c> for the whole level whatever rectangle is asked
    /// for, then copies the rectangle out:</para>
    /// <code>
    /// T[] array2 = new T[Math.Max(ActualHeight >> level, 1) * num6];
    /// GL.GetTexImage(TextureTarget.Texture2D, level, glFormat, glType, array2);
    /// </code>
    /// <para>So a sheet read in eight strips costs eight full readbacks and eight allocations of
    /// the whole sheet, each one large enough to go straight to the large object heap. One call
    /// costs one of each. The strip loop was multiplying exactly the thing it was written to
    /// bound.</para>
    /// <para>The readback itself cannot be avoided while the answers come from art the mod does not
    /// own, and it is synchronous: <c>glGetTexImage</c> waits for the card to finish writing the
    /// texture. It happens once per sheet per session, on the frame a location first needs it,
    /// which is the warp spike this is measured in.</para>
    /// </remarks>
    internal static class SheetReadback
    {
        /// <summary>radiance_sheetread strips: read in strips again, so the two can be compared in
        /// one session rather than against another build.</summary>
        internal static bool WholeSheetInOneCall = true;
        /// <summary>Rows per readback on the strip road. Only the comparison uses it now.</summary>
        private const int StripRows = 512;

        /// <summary>
        /// Every pixel of <paramref name="texture"/>, or null if it is larger than
        /// <paramref name="pixelCap"/> or the read fails. Times itself into the report under
        /// <paramref name="label"/>, which must begin with "sheet" to be printed.
        /// </summary>
        internal static Color[]? Read(Texture2D texture, long pixelCap, string label)
        {
            long pixels = (long)texture.Width * texture.Height;
            if (pixels > pixelCap || pixels <= 0)
                return null;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var data = new Color[pixels];
                if (WholeSheetInOneCall)
                {
                    texture.GetData(data);
                }
                else
                {
                    for (int top = 0; top < texture.Height; top += StripRows)
                    {
                        int rows = Math.Min(StripRows, texture.Height - top);
                        texture.GetData(0, new Rectangle(0, top, texture.Width, rows),
                            data, top * texture.Width, rows * texture.Width);
                    }
                }
                PhaseCost.NoteSince(label, start);
                return data;
            }
            catch
            {
                PhaseCost.NoteSince(label + " (failed)", start);
                return null;
            }
        }
    }
}
