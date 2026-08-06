using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>A labeled drag slider bound to one float config value (content-space rect;
    /// Draw takes the menu's scroll offset).</summary>
    internal sealed class TunerSlider
    {
        private readonly string _label;
        private readonly float _minimum, _maximum;
        private readonly Func<float> _getValue;
        private readonly Action<float> _setValue;
        public Rectangle Track;
        /// <summary>Text grows with the panel on a large window (see the tuner's layout scale).</summary>
        public float TextScale = 1f;

        public TunerSlider(string label, int x, int y, int w, float min, float max, Func<float> get, Action<float> set, int labelHeight = 26, int trackHeight = 20)
        {
            _label = label; _minimum = min; _maximum = max; _getValue = get; _setValue = set;
            _labelHeight = labelHeight;
            Track = new Rectangle(x, y + labelHeight, w, trackHeight);
        }

        private readonly int _labelHeight;

        public void SetFromX(int mx)
        {
            float t = MathHelper.Clamp((mx - Track.X) / (float)Track.Width, 0f, 1f);
            _setValue((float)Math.Round((_minimum + t * (_maximum - _minimum)) / 0.01f) * 0.01f);
        }

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            float v = _getValue();
            float ts = 0.9f * TextScale;
            TunerText.DrawFit(spriteBatch, _label, new Vector2(Track.X, Track.Y - _labelHeight + dy), Track.Width - (int)(70 * TextScale), Game1.textColor, ts);
            string val = v.ToString("0.00");
            Vector2 vs = Game1.smallFont.MeasureString(val) * ts;
            Utility.drawTextWithShadow(spriteBatch, val, Game1.smallFont, new Vector2(Track.Right - vs.X, Track.Y - _labelHeight + dy), Game1.textColor * 0.8f, ts);

            var track = new Rectangle(Track.X, Track.Y + dy, Track.Width, Track.Height);
            spriteBatch.Draw(Game1.staminaRect, track, Color.Black * 0.35f);
            float t = (v - _minimum) / (_maximum - _minimum);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(track.X, track.Y, (int)(t * track.Width), track.Height), new Color(196, 130, 66));
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(track.X + (int)(t * track.Width) - 6, track.Y - 4, 12, track.Height + 8), Color.White);
        }
    }
}
