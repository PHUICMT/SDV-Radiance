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
        /// <summary>Space kept clear on the right, for the count beside a rail entry on the glass.</summary>
        public int RightInset;
        /// <summary>Whether this button is the currently chosen one, for a row of choices where
        /// exactly one is in effect. Asked every frame rather than stamped once: the value can be
        /// changed from GMCM or the console while this menu is open.</summary>
        public Func<bool>? IsChosen;
        /// <summary>Drawn paler than its neighbours: a look chip the mod saved on its own (the look
        /// before a share code was kept), so a glance tells it from the ones the player named.</summary>
        public bool Faded;

        public TunerTextButton(string label, Rectangle bounds, Action onClick)
        {
            _label = label; Bounds = bounds; OnClick = onClick;
        }

        /// <summary>A rail entry on the glass: no plate at rest, a soft one under the pointer, and
        /// the menu itself draws the chosen one's highlight, as on the glass mock.</summary>
        public bool RailEntry;

        /// <summary>Fully round ends on the glass: the sub-page chips, as on the glass mock.</summary>
        public bool Pill;

        /// <summary>A picture over the label on the glass: a preset's card.</summary>
        public Texture2D? Picture;

        /// <summary>A dashed outline on the glass with no plate: the button that adds a new look.</summary>
        public bool Dashed;

        /// <summary>A line icon drawn on the glass in the room <see cref="LeftInset"/> keeps, tinted
        /// like the label.</summary>
        public Texture2D? IconSheet;
        public Rectangle IconSource;

        public void Draw(SpriteBatch spriteBatch, int dy, bool active = false)
        {
            // The chosen one of a row is unmistakable: a gold box with a darker gold rim drawn
            // just inside it. A faint cream tint was the whole signal once, and it read as
            // nothing on a bright screen, so the player could not tell which water, which look
            // or which preset was in effect.
            if (TunerTheme.Glass)
            {
                var plate = new Rectangle(Bounds.X, Bounds.Y + dy, Bounds.Width, Bounds.Height);
                int radius = Pill ? plate.Height / 2 : Math.Min(14, plate.Height / 3);
                bool hovered = plate.Contains(Game1.getMouseX(), Game1.getMouseY());
                if (Picture != null)
                {
                    // A preset card: its picture over its name, the chosen one ringed in the accent.
                    int cardRadius = Math.Min(12, plate.Height / 6);
                    TunerTheme.Plate(spriteBatch, plate, cardRadius, active ? TunerTheme.Accent * 0.22f : TunerTheme.Overlay * (hovered ? 0.14f : 0.07f));
                    TunerTheme.PlateRim(spriteBatch, plate, cardRadius, active ? TunerTheme.Accent : hovered ? TunerTheme.InkFaint : TunerTheme.Rim);
                    int inset = (int)(5 * TextScale);
                    int pictureWidth = plate.Width - inset * 2;
                    var picture = new Rectangle(plate.X + inset, plate.Y + inset, pictureWidth, pictureWidth * 9 / 16);
                    spriteBatch.Draw(Picture, picture, Color.White * (active || hovered ? 1f : 0.85f));
                    DrawLabel(spriteBatch, dy, active || hovered ? TunerTheme.Ink : TunerTheme.InkDim, 0f, (picture.Bottom + plate.Bottom) / 2f - dy);
                    return;
                }
                if (RailEntry)
                {
                    if (hovered && !active)
                        TunerTheme.Plate(spriteBatch, plate, radius, TunerTheme.Overlay * 0.06f);
                }
                else if (Dashed)
                {
                    if (hovered)
                        TunerTheme.Plate(spriteBatch, plate, radius, TunerTheme.Accent * 0.14f);
                    DrawDashes(spriteBatch, plate, radius, TunerTheme.Accent * 0.7f);
                }
                else
                {
                    // Under the pointer it lifts: a brighter plate and a firmer rim, as on the mock.
                    TunerTheme.Plate(spriteBatch, plate, radius, active ? TunerTheme.Accent : TunerTheme.Overlay * (hovered ? 0.15f : 0.07f));
                    if (!active)
                        TunerTheme.PlateRim(spriteBatch, plate, radius, hovered ? TunerTheme.InkFaint : TunerTheme.Rim);
                }
                // The add button is in the accent, as on the mock: it is the one thing on the page that makes something new.
                Color ink = Dashed ? TunerTheme.Accent
                    : active && !RailEntry ? TunerTheme.AccentInk : active || hovered ? TunerTheme.Ink : TunerTheme.InkDim;
                if (IconSheet != null)
                {
                    int iconSize = (int)(16 * TextScale);
                    spriteBatch.Draw(IconSheet, new Rectangle(plate.X + (int)(14 * TextScale), plate.Center.Y - iconSize / 2, iconSize, iconSize), IconSource, ink);
                }
                DrawLabel(spriteBatch, dy, Faded && !active ? ink * 0.6f : ink, 0f);
                return;
            }
            IClickableMenu.drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                Bounds.X, Bounds.Y + dy, Bounds.Width, Bounds.Height, active ? new Color(255, 206, 96) : Color.White, 1f, drawShadow: false);
            bool pale = Faded && !active;
            // A pale wash over the box, not a see-through box: the box made translucent showed the
            // darker panel behind it and came out heavier than its neighbours, not lighter.
            if (pale)
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(Bounds.X + 4, Bounds.Y + dy + 4, Bounds.Width - 8, Bounds.Height - 8),
                    new Color(255, 240, 210) * 0.55f);
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
            // A solid lighter ink rather than a see-through one, which the text drawing did not honour.
            DrawLabel(spriteBatch, dy, pale ? Color.Lerp(Game1.textColor, new Color(200, 150, 100), 0.55f) : Game1.textColor,
                pale ? 0.3f : 1f);
        }

        /// <summary>A dashed outline along the straight edges with round corners: the add-a-new-one
        /// button of the glass mock.</summary>
        private void DrawDashes(SpriteBatch spriteBatch, Rectangle plate, int radius, Color ink)
        {
            int dash = Math.Max(3, (int)(6 * TextScale)), gap = Math.Max(2, (int)(4 * TextScale)), thickness = Math.Max(1, (int)TextScale);
            for (int x = plate.X + radius; x < plate.Right - radius; x += dash + gap)
            {
                int length = Math.Min(dash, plate.Right - radius - x);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(x, plate.Y, length, thickness), ink);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(x, plate.Bottom - thickness, length, thickness), ink);
            }
            for (int y = plate.Y + radius; y < plate.Bottom - radius; y += dash + gap)
            {
                int length = Math.Min(dash, plate.Bottom - radius - y);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(plate.X, y, thickness, length), ink);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(plate.Right - thickness, y, thickness, length), ink);
            }
            TunerTheme.RimCorners(spriteBatch, plate, radius, ink);
        }

        private void DrawLabel(SpriteBatch spriteBatch, int dy, Color ink, float shadowIntensity, float? centreY = null)
        {
            string label = _label;
            Vector2 m = TunerText.Measure(label);
            int textLeft = Bounds.X + LeftInset;
            int textWidth = Bounds.Width - LeftInset - RightInset;
            float scale = Math.Min(0.9f * TextScale, (textWidth - 16) / Math.Max(1f, m.X));
            // A rail name with its English term in brackets after it shrank to fit the rail until it
            // could not be read; there the name alone is shown, and the page it opens carries the
            // whole of it.
            int bracket = label.IndexOf(" (", StringComparison.Ordinal);
            if (RailEntry && bracket > 0 && scale < 0.75f * 0.9f * TextScale)
            {
                label = label[..bracket];
                m = TunerText.Measure(label);
                scale = Math.Min(0.9f * TextScale, (textWidth - 16) / Math.Max(1f, m.X));
            }
            // Centre on a STANDARD cap height, not the label's own measured height: Thai
            // strings measure taller (tone marks/upper vowels) which pushed text up off
            // centre. A fixed reference keeps EN and TH visually centred the same way.
            float refH = TunerText.Measure("A").Y * scale;
            // A rail entry on the glass reads as a list, its words from the left after the icon.
            float textX = RailEntry && TunerTheme.Glass ? textLeft + 8 * TextScale : textLeft + (textWidth - m.X * scale) / 2f;
            TunerText.Draw(spriteBatch, label, new Vector2(textX, (centreY ?? Bounds.Center.Y) - refH / 2f + dy + 2f),
                ink, scale, shadowIntensity: shadowIntensity);
        }
    }
}
