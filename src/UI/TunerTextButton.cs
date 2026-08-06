using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>A boxed clickable text button (content-space rect; Draw takes the menu's
    /// scroll offset; rail buttons pass dy=0 because the tab rail never scrolls).</summary>
    internal sealed class TunerTextButton
    {
        private readonly string _label;
        public readonly Rectangle Bounds;
        public readonly Action OnClick;
        /// <summary>Text grows with the panel on a large window (see the tuner's layout scale),
        /// or the label stays tiny in a box that got bigger around it.</summary>
        public float TextScale = 1f;
        /// <summary>Space reserved on the left for an icon, so the label centres in what is
        /// left rather than underneath it.</summary>
        public int LeftInset;

        public TunerTextButton(string label, Rectangle bounds, Action onClick)
        {
            _label = label; Bounds = bounds; OnClick = onClick;
        }

        public void Draw(SpriteBatch spriteBatch, int dy, bool active = false)
        {
            IClickableMenu.drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                Bounds.X, Bounds.Y + dy, Bounds.Width, Bounds.Height, active ? new Color(255, 240, 200) : Color.White, 1f, drawShadow: false);
            Vector2 m = Game1.smallFont.MeasureString(_label);
            int textLeft = Bounds.X + LeftInset;
            int textWidth = Bounds.Width - LeftInset;
            float scale = Math.Min(0.9f * TextScale, (textWidth - 16) / Math.Max(1f, m.X));
            // Centre on a STANDARD cap height, not the label's own measured height: Thai
            // strings measure taller (tone marks/upper vowels) which pushed text up off
            // centre. A fixed reference keeps EN and TH visually centred the same way.
            float refH = Game1.smallFont.MeasureString("A").Y * scale;
            Utility.drawTextWithShadow(spriteBatch, _label, Game1.smallFont,
                new Vector2(textLeft + (textWidth - m.X * scale) / 2f, Bounds.Center.Y - refH / 2f + dy + 2f), Game1.textColor, scale);
        }
    }
}
