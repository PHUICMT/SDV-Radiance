using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>Text helpers shared by the tuner menu and its widgets.</summary>
    internal static class TunerText
    {
        /// <summary>Sizes already measured, so a menu full of labels asks the font once per string
        /// instead of once per frame. Keyed on the font too: switching language swaps
        /// <c>Game1.smallFont</c> for one with different metrics, and stale widths would misplace
        /// every label until restart.</summary>
        private static readonly Dictionary<string, Vector2> _measuredSizes = new();
        private static SpriteFont? _measuredFont;
        /// <summary>Live diagnostic lines (bench timings and the like) mint new strings while they
        /// run; past this the cache starts over rather than remembering every one it ever saw.</summary>
        private const int MeasuredSizesCap = 4096;

        /// <summary>What <c>Game1.smallFont</c> would measure for this string, remembered.</summary>
        internal static Vector2 Measure(string text)
        {
            SpriteFont font = Game1.smallFont;
            if (!ReferenceEquals(font, _measuredFont) || _measuredSizes.Count > MeasuredSizesCap)
            {
                _measuredSizes.Clear();
                _measuredFont = font;
            }
            if (!_measuredSizes.TryGetValue(text, out Vector2 size))
            {
                size = font.MeasureString(text);
                _measuredSizes[text] = size;
            }
            return size;
        }

        /// <summary>Draw a label shrunk-to-fit so long translations never overflow their row.</summary>
        internal static void DrawFit(SpriteBatch spriteBatch, string text, Vector2 pos, float maxWidth, Color color, float maxScale)
        {
            float measuredWidth = Measure(text).X;
            float scale = Math.Min(maxScale, maxWidth / Math.Max(1f, measuredWidth));
            Utility.drawTextWithShadow(spriteBatch, text, Game1.smallFont, pos, color, scale);
        }
    }
}
