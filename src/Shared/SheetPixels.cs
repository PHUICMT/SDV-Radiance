using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// The one place a whole sheet's pixels are held after being read back from the card, for every
    /// part of the mod that answers questions about art it does not own.
    /// </summary>
    /// <remarks>
    /// <para><c>Texture2D.GetData(level, rect, ...)</c> on this build never reads the rectangle. Its
    /// decompiled body allocates an array the size of the WHOLE mip level, calls
    /// <c>glGetTexImage</c> for the whole level, and copies the requested rows out. So the answer
    /// to "read this sheet once and sample the array" is not an optimisation, it is the only shape
    /// that does not pay for the whole sheet per question.</para>
    /// <para>Three parts of the mod had worked that out separately and each kept its own copy: the
    /// water mask's tile art, the shadow pass's tile coverage, and the object shadow's question
    /// about where its art stops. Measured on 8 September in one arrival at Town: fourteen reads
    /// under "water tile art" and ten under "shadow tile coverage", of the same map's tilesheets,
    /// about 19 ms of main thread and two copies of every sheet held for the session. Each cache
    /// was right on its own and they were wrong together.</para>
    /// <para>The label a caller passes is only for the report, so the timing rows still say who
    /// asked first. A sheet read under one label is served to every other label for free, and
    /// <see cref="SharedHits"/> counts exactly that, which is what proves the merge did something.
    /// </para>
    /// </remarks>
    internal static class SheetPixels
    {
        /// <summary>radiance_sheetpixels off: nobody holds a sheet, every caller falls back to its
        /// own road, so the two can be compared inside one session.</summary>
        internal static bool Enabled = true;

        /// <summary>Refusal bound for absurd sheets, NOT a performance knob. Inherited from the two
        /// caches this replaces, which both used it: an 8 Mpx ceiling once sat just under a real
        /// SVE tilesheet (2400x3600 = 8.64 Mpx) and being 8% over it swapped one readback per sheet
        /// for one per TILE, which was 43 seconds inside a single gather.</summary>
        internal const int PixelCap = 64_000_000;

        /// <summary>Null value = this sheet was refused, so callers fall back and do not ask
        /// again. Keyed by texture reference, which is what the game keeps alive while the art is
        /// loaded.</summary>
        private static readonly Dictionary<Texture2D, Color[]?> _heldSheets = [];

        /// <summary>Which label first read each sheet, so a hit from another label can be counted
        /// as a readback this merge avoided.</summary>
        private static readonly Dictionary<Texture2D, string> _readUnder = [];

        private static long _pixelsHeld;

        internal static int SheetsRead;
        internal static int SheetsRefused;
        internal static int SharedHits;
        internal static int RectanglesServedFromMemory;
        internal static int RectanglesReadFromCard;

        internal static long PixelsHeld => _pixelsHeld;

        /// <summary>Every held sheet, for the report that describes what the cache is holding.</summary>
        internal static IEnumerable<KeyValuePair<Texture2D, Color[]?>> Entries => _heldSheets;

        /// <summary>
        /// Every pixel of <paramref name="texture"/>, or null when it is over <see cref="PixelCap"/>
        /// or the read failed, in which case the caller falls back to reading rectangles.
        /// <paramref name="label"/> names the caller in the report and must begin with "sheet".
        /// </summary>
        internal static Color[]? WholeSheet(Texture2D texture, string label)
        {
            if (!Enabled || texture.IsDisposed)
                return null;
            if (_heldSheets.TryGetValue(texture, out Color[]? known))
            {
                if (known != null && _readUnder.TryGetValue(texture, out string? first) && first != label)
                    SharedHits++;
                return known;
            }

            Color[]? whole = SheetReadback.Read(texture, PixelCap, label);
            _heldSheets[texture] = whole;
            if (whole != null)
            {
                _readUnder[texture] = label;
                _pixelsHeld += whole.Length;
                SheetsRead++;
            }
            else
            {
                SheetsRefused++;
            }
            return whole;
        }

        /// <summary>
        /// Fill <paramref name="into"/> with the pixels of <paramref name="source"/>. Throws
        /// whatever <c>GetData</c> throws when the sheet is not held, so callers keep the exception
        /// behaviour they had.
        /// </summary>
        internal static void Read(Texture2D texture, Rectangle source, Color[] into)
        {
            Color[]? sheet = WholeSheet(texture, "sheet: art pixels");
            if (sheet == null)
            {
                RectanglesReadFromCard++;
                texture.GetData(0, source, into, 0, source.Width * source.Height);
                return;
            }

            int sheetWidth = texture.Width;
            for (int row = 0; row < source.Height; row++)
            {
                int readFrom = (source.Y + row) * sheetWidth + source.X;
                int writeTo = row * source.Width;
                if (readFrom < 0 || readFrom + source.Width > sheet.Length)
                {
                    System.Array.Clear(into, writeTo, source.Width);
                    continue;
                }
                System.Array.Copy(sheet, readFrom, into, writeTo, source.Width);
            }
            RectanglesServedFromMemory++;
        }

        /// <summary>Drop every held sheet. Called when the save is left, because the textures these
        /// pixels were read from are unloaded then and the answers stop meaning anything.</summary>
        internal static void Forget()
        {
            _heldSheets.Clear();
            _readUnder.Clear();
            _pixelsHeld = 0;
        }

        /// <summary>One line for the report.</summary>
        internal static string Describe()
        {
            return $"sheet pixels: {(Enabled ? "held" : "OFF")} — {SheetsRead} sheets read, "
                + $"{SharedHits} hits by a part that did not read it, {SheetsRefused} refused, "
                + $"{RectanglesServedFromMemory} rectangles from memory, "
                + $"{RectanglesReadFromCard} from the card, "
                + $"{_pixelsHeld / 1_000_000.0:0.0} M pixels held";
        }
    }
}
