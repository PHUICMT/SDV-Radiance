using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// A round dial bound to one bearing in degrees: drag anywhere in the circle to put the sun
    /// on that side of the screen.
    ///
    /// <para>
    /// The tuner's other two controls are a switch and a straight track, and neither can say this.
    /// A bearing has no ends: nudging a slider off its right edge should come back on the left,
    /// and a track that stops at 359 puts a seam in the middle of the one thing the control is
    /// for. It is also the rare setting whose value IS a picture, so the dial shows the answer
    /// rather than a number: a mark where the sun stands, and a line out of the middle showing
    /// which way the shadows will fall, which is what the player is actually choosing.
    /// </para>
    /// </summary>
    internal sealed class TunerCompass
    {
        private readonly string _label;
        private readonly Func<float> _getDegrees;
        private readonly Action<float> _setDegrees;

        /// <summary>The whole row, label included, in content space. The menu scrolls it.</summary>
        public Rectangle Row;
        /// <summary>The circle the mouse is measured against, inside <see cref="Row"/>.</summary>
        public Rectangle Dial;
        public float TextScale = 1f;
        public Func<bool>? Enabled;
        public bool IsEnabled => Enabled == null || Enabled();

        /// <summary>What this dial reads under a fresh config, or null when it has none to compare
        /// against (see the slider's). Compared as bearings, so 359 and 1 are two degrees apart.</summary>
        public float? DefaultValue { get; init; }
        public bool DiffersFromDefault
        {
            get
            {
                if (!DefaultValue.HasValue)
                    return false;
                float apart = Math.Abs(_getDegrees() - DefaultValue.Value) % 360f;
                return Math.Min(apart, 360f - apart) > 0.5f;
            }
        }
        public void ResetToDefault()
        {
            if (DefaultValue.HasValue && IsEnabled)
                _setDegrees(DefaultValue.Value);
        }

        public TunerCompass(string label, int x, int y, int width, int labelHeight, int dialSize,
                            Func<float> getDegrees, Action<float> setDegrees)
        {
            _label = label;
            _getDegrees = getDegrees;
            _setDegrees = setDegrees;
            Row = new Rectangle(x, y, width, labelHeight + dialSize);
            // Centred in the column rather than left-aligned: a circle hard against the left edge
            // of a panel of straight tracks reads as something that fell off the row.
            Dial = new Rectangle(x + (width - dialSize) / 2, y + labelHeight, dialSize, dialSize);
        }

        public bool Hit(int x, int y)
        {
            if (!IsEnabled)
                return false;
            // The whole square, not just the inscribed circle: a drag that wanders into a corner
            // should keep turning the dial rather than letting go of it.
            return Dial.Contains(x, y);
        }

        public void SetFromPoint(int x, int y)
        {
            if (!IsEnabled)
                return;
            float dx = x - (Dial.X + Dial.Width * 0.5f);
            float dy = y - (Dial.Y + Dial.Height * 0.5f);
            if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f)
                return;     // dead centre names no direction
            // Bearing 0 is the sun straight DOWN the screen, toward the viewer, and it runs
            // clockwise from there, which is the same way the shadow angle runs. On a screen
            // whose y points down, clockwise from down is toward the left, hence the negated x.
            float degrees = MathHelper.ToDegrees((float)Math.Atan2(-dx, dy));
            _setDegrees((float)Math.Round((degrees % 360f + 360f) % 360f));
        }

        /// <summary>Where a bearing sits on the dial: the direction the SUN is, as a screen
        /// offset from the middle. The shadow is the other way, which is the whole point.</summary>
        private static Vector2 SunOffset(float degrees)
        {
            float radians = MathHelper.ToRadians(degrees);
            return new Vector2(-(float)Math.Sin(radians), (float)Math.Cos(radians));
        }

        /// <summary>One straight bar of the dial's artwork. Everything here is drawn from the
        /// game's own white pixel, so the control needs no art of its own and inherits whatever
        /// recolour the player is running.</summary>
        private static void Bar(SpriteBatch spriteBatch, Vector2 from, Vector2 to, float thickness, Color colour)
        {
            Vector2 span = to - from;
            float length = span.Length();
            if (length < 0.5f)
                return;
            spriteBatch.Draw(Game1.staminaRect, from, null, colour, (float)Math.Atan2(span.Y, span.X),
                new Vector2(0f, 0.5f), new Vector2(length, thickness), SpriteEffects.None, 0f);
        }

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            float fade = IsEnabled ? 1f : 0.35f;
            float textScale = 0.9f * TextScale;
            float degrees = _getDegrees();

            TunerText.DrawFit(spriteBatch, _label, new Vector2(Row.X, Row.Y + dy),
                Row.Width - (int)(70 * TextScale), Game1.textColor * fade, textScale);
            string reading = $"{degrees:0}°";
            Vector2 readingSize = TunerText.Measure(reading) * textScale;
            Utility.drawTextWithShadow(spriteBatch, reading, Game1.smallFont,
                new Vector2(Row.Right - readingSize.X, Row.Y + dy), Game1.textColor * 0.8f * fade, textScale);

            var centre = new Vector2(Dial.X + Dial.Width * 0.5f, Dial.Y + Dial.Height * 0.5f + dy);
            float radius = Dial.Width * 0.5f - 4f;

            // The ring, as short chords around the circle. Forty of them is smooth at every size
            // the panel is drawn at, and it costs no texture.
            const int RingSegments = 40;
            var previous = centre + new Vector2(radius, 0f);
            for (int i = 1; i <= RingSegments; i++)
            {
                float angle = MathHelper.TwoPi * i / RingSegments;
                var point = centre + new Vector2((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius);
                Bar(spriteBatch, previous, point, 2f, Color.Black * 0.35f * fade);
                previous = point;
            }
            // Four ticks, one per side of the screen, so a player who wants the sun squarely
            // behind or beside them can find it without reading the number.
            for (int quarter = 0; quarter < 4; quarter++)
            {
                Vector2 direction = SunOffset(quarter * 90f);
                Bar(spriteBatch, centre + direction * (radius - 6f), centre + direction * radius, 2f, Color.Black * 0.3f * fade);
            }

            Vector2 sun = SunOffset(degrees);
            // The shadow first, so the sun mark sits on top of it where the two meet: a line from
            // the middle running AWAY from the sun, which is where this setting will actually put
            // every shadow on the screen.
            Bar(spriteBatch, centre, centre - sun * (radius - 5f), 4f, new Color(64, 64, 72) * 0.85f * fade);
            // Then the sun: a small block on the ring, in the warm tone the rest of the panel
            // uses for the live part of a control.
            Vector2 sunPoint = centre + sun * (radius - 5f);
            spriteBatch.Draw(Game1.staminaRect,
                new Rectangle((int)(sunPoint.X - 6f), (int)(sunPoint.Y - 6f), 12, 12),
                new Color(238, 190, 92) * fade);
        }
    }
}
