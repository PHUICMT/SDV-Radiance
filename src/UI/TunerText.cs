using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>Text helpers shared by the tuner menu and its widgets.</summary>
    internal static class TunerText
    {
        /// <summary>Draw a label shrunk-to-fit so long translations never overflow their row.</summary>
        internal static void DrawFit(SpriteBatch spriteBatch, string text, Vector2 pos, float maxWidth, Color color, float maxScale)
        {
            float measuredWidth = Game1.smallFont.MeasureString(text).X;
            float scale = Math.Min(maxScale, maxWidth / Math.Max(1f, measuredWidth));
            Utility.drawTextWithShadow(spriteBatch, text, Game1.smallFont, pos, color, scale);
        }
    }
}
