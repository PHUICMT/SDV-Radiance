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

        public bool Hit(int x, int y) => new Rectangle(Row.X, Row.Y, Row.Width, 36).Contains(x, y);

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            spriteBatch.Draw(Game1.mouseCursors, new Vector2(Row.X, Row.Y + dy), Get() ? Checked : Unchecked,
                Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
            TunerText.DrawFit(spriteBatch, _label, new Vector2(Row.X + 48, Row.Y + 6 + dy), Row.Width - 56, Game1.textColor, 0.9f);
        }
    }
}
