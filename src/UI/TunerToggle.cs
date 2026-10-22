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

        /// <summary>The row's label key, which is how a hidden switch is remembered.</summary>
        public string Key { get; init; } = "";
        /// <summary>Whether the player may hide this switch (not the mod's own on/off, not the menu's).</summary>
        public bool CanHide { get; init; }
        /// <summary>The switch the whole group hangs off: hidden from the group's heading, not here.</summary>
        public bool OwnsGroup;
        /// <summary>Whether this row shows its own Hide button.</summary>
        public bool OffersHide => CanHide && !OwnsGroup;
        /// <summary>Room the label leaves at its end this draw, for the Hide button under the pointer.</summary>
        public int HideRoom;

        /// <summary>Where the switch itself is drawn on the glass, in content coordinates.</summary>
        public Rectangle GlassSwitch
        {
            get
            {
                int switchWidth = (int)(40 * TextScale), switchHeight = (int)(22 * TextScale);
                return new Rectangle(Row.Right - (int)(8 * TextScale) - switchWidth, Row.Y + (Row.Height - switchHeight) / 2, switchWidth, switchHeight);
            }
        }

        /// <summary>Whether this row can do anything right now. A switch under an effect that is
        /// itself off still took the click and still moved the value, with nothing to show for it.
        /// Null means always on, which is what most rows are.</summary>
        public Func<bool>? Enabled;
        public bool IsEnabled => Enabled == null || Enabled();

        /// <summary>What this row reads under a fresh config, or null when it has none to compare
        /// against (see the slider's).</summary>
        public bool? DefaultValue { get; init; }
        public bool DiffersFromDefault => DefaultValue.HasValue && Get() != DefaultValue.Value;
        public void ResetToDefault()
        {
            if (DefaultValue.HasValue && IsEnabled)
                Set(DefaultValue.Value);
        }

        public bool Hit(int x, int y) => IsEnabled
            && new Rectangle(Row.X, Row.Y, Row.Width, (int)(36 * TextScale)).Contains(x, y);

        /// <summary>Where the glass label ended on the last draw, on screen, for the changed dot
        /// that follows it.</summary>
        public Vector2 LabelEnd { get; private set; }

        public void Draw(SpriteBatch spriteBatch, int dy)
        {
            float box = 4f * TextScale;
            // Dimmed rather than hidden, for the same reason the slider is: a list that re-flows
            // under the hand every time a switch is flipped is harder to use than a greyed row.
            float fade = IsEnabled ? 1f : 0.35f;
            if (TunerTheme.Glass)
            {
                // The label left and a pill switch at the end of the row, as on the glass mock:
                // blue with the knob right when on.
                Rectangle track = GlassSwitch;
                track.Y += dy;
                int switchHeight = track.Height;
                bool on = Get();
                // The knob slides and the track fills with blue rather than jumping (kept by the row's
                // key, since a click builds the row again).
                float slide = Key.Length > 0 ? TunerMotion.Follow("switch:" + Key, on ? 1f : 0f, 0.05f) : on ? 1f : 0f;
                TunerTheme.Plate(spriteBatch, track, switchHeight / 2, Color.Lerp(TunerTheme.Overlay * 0.18f, TunerTheme.Accent, slide) * fade);
                int knob = switchHeight - (int)(4 * TextScale);
                float knobX = MathHelper.Lerp(track.X + switchHeight / 2f, track.Right - switchHeight / 2f, slide);
                TunerTheme.Disc(spriteBatch, new Vector2(knobX, track.Center.Y), knob, Color.White * fade);
                int labelX = Row.X + (int)(10 * TextScale);
                float labelRoom = track.X - labelX - 12 - HideRoom;
                TunerText.DrawFit(spriteBatch, _label, new Vector2(labelX, Row.Y + dy + (Row.Height - 26 * TextScale) / 2f),
                    labelRoom, TunerTheme.Ink * fade, 0.9f * TextScale);
                LabelEnd = new Vector2(labelX + Math.Min(labelRoom, TunerText.Measure(_label).X * 0.9f * TextScale), track.Center.Y);
                return;
            }
            else
                spriteBatch.Draw(Game1.mouseCursors, new Vector2(Row.X, Row.Y + dy), Get() ? Checked : Unchecked,
                    Color.White * fade, 0f, Vector2.Zero, box, SpriteEffects.None, 0.9f);
            int textX = Row.X + (int)(48 * TextScale);
            TunerText.DrawFit(spriteBatch, _label, new Vector2(textX, Row.Y + 6 * TextScale + dy),
                Row.Right - textX - 8 - HideRoom, TunerTheme.Ink * fade, 0.9f * TextScale);
        }
    }
}
