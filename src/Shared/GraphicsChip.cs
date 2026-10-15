using System.Text.RegularExpressions;

namespace SDVRadiance
{
    /// <summary>
    /// Tells built-in graphics (the chip inside the processor) from a graphics card, by the name
    /// the driver reports.
    /// </summary>
    /// <remarks>
    /// <para>The mod's cost is almost all in the graphics chip. Measured 12 Oct 2026 with a card held
    /// to a fifteenth of its clock: the mod went from a few milliseconds to three quarters of a
    /// 27-36 ms frame, while the same scenes on a processor cut to four small cores cost the mod
    /// only 1-2 ms. Built-in graphics are where that slow card lives, so a first install there
    /// starts on a lighter preset.</para>
    /// <para>Only names that say so plainly count. Intel's own cards (Arc A and B series) and every
    /// AMD or NVIDIA card are left alone, and so is anything not recognised: a wrong guess here
    /// hands a capable machine a softer picture, which is worse than missing a slow one, since the
    /// slow one can still pick a preset.</para>
    /// </remarks>
    internal static class GraphicsChip
    {
        // Intel: every chip except the Arc cards, which carry a model letter and number.
        private static readonly Regex IntelCard = new(@"\bArc(\(TM\))?\s+[AB]\d", RegexOptions.IgnoreCase);
        // AMD's built-in Radeons: a bare "Radeon Graphics", the Vega and 600M/700M/800M parts, the
        // Steam Deck, and the code names the Linux driver reports for the same chips.
        private static readonly Regex AmdBuiltIn = new(
            @"Radeon(\(TM\))?\s+Graphics|Vega\s*\d+\s+Graphics|Radeon(\(TM\))?\s+\d{3}M\b|Custom GPU|"
            + @"\b(vangogh|renoir|raven|picasso|cezanne|lucienne|barcelo|rembrandt|mendocino|phoenix|hawk ?point|strix)\b",
            RegexOptions.IgnoreCase);
        // A renderer with no graphics chip behind it at all.
        private static readonly Regex Software = new(@"llvmpipe|softpipe|Basic Render|GDI Generic|SwiftShader",
            RegexOptions.IgnoreCase);

        internal static bool IsBuiltIn(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            if (Software.IsMatch(name))
                return true;
            if (name.Contains("Intel", System.StringComparison.OrdinalIgnoreCase))
                return !IntelCard.IsMatch(name);
            if (name.StartsWith("Apple M", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return AmdBuiltIn.IsMatch(name);
        }
    }
}
