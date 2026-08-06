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

        public bool Hit(int x, int y) => new Rectangle(Row.X, Row.Y, Row.Width, (int)(36 * TextScale)).Contains(x, y);

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            float box = 4f * TextScale;
            spriteBatch.Draw(Game1.mouseCursors, new Vector2(Row.X, Row.Y + dy), Get() ? Checked : Unchecked,
                Color.White, 0f, Vector2.Zero, box, SpriteEffects.None, 0.9f);
            int textX = Row.X + (int)(48 * TextScale);
            TunerText.DrawFit(spriteBatch, _label, new Vector2(textX, Row.Y + 6 * TextScale + dy),
                Row.Right - textX - 8, Game1.textColor, 0.9f * TextScale);
        }
    }
}
