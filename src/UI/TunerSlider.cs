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

        /// <summary>The row's label key, the name its hover light is kept under.</summary>
        public string Key { get; init; } = "";
        /// <summary>Whether this row can do anything right now. A control whose switch is off, or
        /// whose whole effect is turned off above it, still sat there at full strength and took
        /// the mouse: the value moved, nothing on screen did, and the only way to find that out
        /// was to try. Null means always on, which is what most rows are.</summary>
        public Func<bool>? Enabled;
        public bool IsEnabled => Enabled == null || Enabled();

        /// <summary>What this row reads under a fresh config, or null when it has none to compare
        /// against. The tuner marks a row that differs and puts it back on a right click.</summary>
        public float? DefaultValue { get; init; }
        public bool DiffersFromDefault => DefaultValue.HasValue && Math.Abs(_getValue() - DefaultValue.Value) > Step * 0.5f;
        public void ResetToDefault()
        {
            if (DefaultValue.HasValue && IsEnabled)
                _setValue(DefaultValue.Value);
        }

        /// <summary>The label and the track together, in content space.</summary>
        public Rectangle RowBounds => Inline
            ? new(InlineLabelX, Track.Y - 4, InlineRowRight - InlineLabelX, Track.Height + 8)
            : new(Track.X, Track.Y - _labelHeight, Track.Width, _labelHeight + Track.Height);

        /// <summary>On the glass panel the label sits at the left of the row and the track to its
        /// right, with the value at the far end, as on the glass mock; the track rectangle is then
        /// only the right part of the row.</summary>
        public bool Inline { get; init; }
        public int InlineLabelX { get; init; }
        public int InlineRowRight { get; init; }

        /// <summary>Where the inline label ended on the last draw, on screen, for the changed dot
        /// that follows it.</summary>
        public Vector2 LabelEnd { get; private set; }

        public TunerSlider(string label, int x, int y, int w, float min, float max, Func<float> get, Action<float> set, int labelHeight = 26, int trackHeight = 20)
        {
            _label = label; _minimum = min; _maximum = max; _getValue = get; _setValue = set;
            _labelHeight = labelHeight;
            Track = new Rectangle(x, y + labelHeight, w, trackHeight);
        }

        private readonly int _labelHeight;
        private float _shownValue = float.NaN;
        private string? _shownValueText;

        /// <summary>The smallest move this slider makes. A hundredth suits a dial that runs 0 to 1
        /// or wider, and every slider used it, so the narrow ones were coarse to the point of
        /// being useless: cloud speed runs 0 to 0.06 and had seven positions, fog speed eleven.
        /// A slider whose range is small enough to need a finer step says so when it is built,
        /// the way the same setting already does in the other menu.</summary>
        public float Step { get; init; } = 0.01f;

        public void SetFromX(int mx)
        {
            if (!IsEnabled)
                return;
            float t = MathHelper.Clamp((mx - Track.X) / (float)Track.Width, 0f, 1f);
            _setValue((float)Math.Round((_minimum + t * (_maximum - _minimum)) / Step) * Step);
        }

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            float v = _getValue();
            float ts = 0.9f * TextScale;
            // A dead row is normally not BUILT at all (the tuner skips it and rebuilds the tab
            // on every toggle), so this dim is only the fallback for a condition that goes
            // false without a rebuild in between.
            float fade = IsEnabled ? 1f : 0.35f;
            if (Inline)
            {
                float labelRoom = Track.X - InlineLabelX - 12;
                TunerText.DrawFit(spriteBatch, _label, new Vector2(InlineLabelX, Track.Y - 3 + dy), labelRoom,
                    TunerTheme.Ink * fade, ts);
                LabelEnd = new Vector2(InlineLabelX + Math.Min(labelRoom, TunerText.Measure(_label).X * ts), Track.Center.Y + dy);
                if (v != _shownValue || _shownValueText == null)
                {
                    _shownValue = v;
                    _shownValueText = v.ToString("0.00");
                }
                Vector2 valueSize = TunerText.Measure(_shownValueText) * ts * 0.85f;
                TunerText.Draw(spriteBatch, _shownValueText, new Vector2(InlineRowRight - valueSize.X, Track.Y - 2 + dy), TunerTheme.InkDim * fade, ts * 0.85f, shadowIntensity: 0f);
            }
            else
                TunerText.DrawFit(spriteBatch, _label, new Vector2(Track.X, Track.Y - _labelHeight + dy), Track.Width - (int)(70 * TextScale), TunerTheme.Ink * fade, ts);
            if (Inline)
            {
                DrawTrack(spriteBatch, dy, v, fade);
                return;
            }
            // The text and its width only change when the value does, which is a drag, not a frame.
            if (v != _shownValue || _shownValueText == null)
            {
                _shownValue = v;
                _shownValueText = v.ToString("0.00");
            }
            string val = _shownValueText;
            Vector2 vs = TunerText.Measure(val) * ts;
            TunerText.Draw(spriteBatch, val, new Vector2(Track.Right - vs.X, Track.Y - _labelHeight + dy), TunerTheme.InkDim * fade, ts);
            DrawTrack(spriteBatch, dy, v, fade);
        }

        private void DrawTrack(SpriteBatch spriteBatch, int dy, float v, float fade)
        {
            var track = new Rectangle(Track.X, Track.Y + dy, Track.Width, Track.Height);
            float t = (v - _minimum) / (_maximum - _minimum);
            if (TunerTheme.Glass)
            {
                // A thin rounded track with a white thumb ringed in blue, as on the glass mock.
                int line = Math.Max(4, track.Height / 3);
                var groove = new Rectangle(track.X, track.Center.Y - line / 2, track.Width, line);
                TunerTheme.Plate(spriteBatch, groove, line / 2, TunerTheme.Overlay * 0.18f * fade);
                TunerTheme.Plate(spriteBatch, new Rectangle(groove.X, groove.Y, (int)(t * groove.Width), groove.Height), line / 2, TunerTheme.Accent * fade);
                var thumbCentre = new Vector2(track.X + t * track.Width, track.Center.Y);
                TunerTheme.Disc(spriteBatch, thumbCentre, track.Height + 4, TunerTheme.Accent * 0.35f * fade);
                TunerTheme.Disc(spriteBatch, thumbCentre, track.Height - 2, Color.White * fade);
                return;
            }
            spriteBatch.Draw(Game1.staminaRect, track, Color.Black * 0.35f * fade);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(track.X, track.Y, (int)(t * track.Width), track.Height), new Color(196, 130, 66) * fade);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(track.X + (int)(t * track.Width) - 6, track.Y - 4, 12, track.Height + 8), Color.White * fade);
        }
    }
}
