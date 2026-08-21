using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>A labeled checkbox row bound to one bool config value (content-space rect;
    /// Draw takes the menu's scroll offset).</summary>
    internal sealed class TunerToggle
    {
        private static readonly Rectangle Unchecked = new(227, 425, 9, 9);
        private static readonly Rectangle Checked = new(236, 425, 9, 9);
        private readonly string _label;
        public readonly Rectangle Row;
        public readonly Func<bool> Get;
        public readonly Action<bool> Set;

        public TunerToggle(string label, Rectangle row, Func<bool> get, Action<bool> set)
        {
            _label = label; Row = row; Get = get; Set = set;
        }

        /// <summary>Box and text grow with the panel on a large window (see the tuner's layout scale).</summary>
        public float TextScale = 1f;

        /// <summary>Whether this row can do anything right now. A switch under an effect that is
        /// itself off still took the click and still moved the value, with nothing to show for it.
        /// Null means always on, which is what most rows are.</summary>
        public Func<bool>? Enabled;
        public bool IsEnabled => Enabled == null || Enabled();

        public bool Hit(int x, int y) => IsEnabled
            && new Rectangle(Row.X, Row.Y, Row.Width, (int)(36 * TextScale)).Contains(x, y);

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            float box = 4f * TextScale;
            // Dimmed rather than hidden, for the same reason the slider is: a list that re-flows
            // under the hand every time a switch is flipped is harder to use than a greyed row.
            float fade = IsEnabled ? 1f : 0.35f;
            spriteBatch.Draw(Game1.mouseCursors, new Vector2(Row.X, Row.Y + dy), Get() ? Checked : Unchecked,
                Color.White * fade, 0f, Vector2.Zero, box, SpriteEffects.None, 0.9f);
            int textX = Row.X + (int)(48 * TextScale);
            TunerText.DrawFit(spriteBatch, _label, new Vector2(textX, Row.Y + 6 * TextScale + dy),
                Row.Right - textX - 8, Game1.textColor * fade, 0.9f * TextScale);
        }
    }
}
