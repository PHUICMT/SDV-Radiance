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
        /// <summary>Whether this button is the currently chosen one, for a row of choices where
        /// exactly one is in effect. Asked every frame rather than stamped once: the value can be
        /// changed from GMCM or the console while this menu is open.</summary>
        public Func<bool>? IsChosen;

        public TunerTextButton(string label, Rectangle bounds, Action onClick)
        {
            _label = label; Bounds = bounds; OnClick = onClick;
        }

        public void Draw(SpriteBatch spriteBatch, int dy, bool active = false)
        {
            // The chosen one of a row is unmistakable: a gold box with a darker gold rim drawn
            // just inside it. A faint cream tint was the whole signal once, and it read as
            // nothing on a bright screen, so the player could not tell which water, which look
            // or which preset was in effect.
            IClickableMenu.drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                Bounds.X, Bounds.Y + dy, Bounds.Width, Bounds.Height, active ? new Color(255, 206, 96) : Color.White, 1f, drawShadow: false);
            if (active)
            {
                var rim = new Color(176, 112, 24);
                int inset = 4, thickness = 3;
                var box = new Rectangle(Bounds.X + inset, Bounds.Y + dy + inset, Bounds.Width - inset * 2, Bounds.Height - inset * 2);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, box.Width, thickness), rim);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(box.X, box.Bottom - thickness, box.Width, thickness), rim);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, thickness, box.Height), rim);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(box.Right - thickness, box.Y, thickness, box.Height), rim);
            }
            Vector2 m = TunerText.Measure(_label);
            int textLeft = Bounds.X + LeftInset;
            int textWidth = Bounds.Width - LeftInset;
            float scale = Math.Min(0.9f * TextScale, (textWidth - 16) / Math.Max(1f, m.X));
            // Centre on a STANDARD cap height, not the label's own measured height: Thai
            // strings measure taller (tone marks/upper vowels) which pushed text up off
            // centre. A fixed reference keeps EN and TH visually centred the same way.
            float refH = TunerText.Measure("A").Y * scale;
            Utility.drawTextWithShadow(spriteBatch, _label, Game1.smallFont,
                new Vector2(textLeft + (textWidth - m.X * scale) / 2f, Bounds.Center.Y - refH / 2f + dy + 2f), Game1.textColor, scale);
        }
    }
}
