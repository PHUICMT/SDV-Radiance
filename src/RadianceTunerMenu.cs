using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>
    /// On-screen tuner overlay. Occupies a panel on the right so the world stays
    /// visible on the left and updates live as you drag (the shader re-applies every
    /// frame via RenderedWorld while this menu is open). Scrolls if content exceeds
    /// the screen. Localized via the injected translator. Opened with the tuner hotkey.
    /// </summary>
    internal sealed class RadianceTunerMenu : IClickableMenu
    {
        private const int PanelWidth = 480;
        private const int HeaderH = 52;
        private const int FooterH = 44;
        private static readonly Rectangle DeleteSource = new(192, 256, 64, 64); // red X in mouseCursors

        private readonly ModConfig _config;
        private readonly Func<string, string> _t;
        private readonly Action _onChange;
        private readonly Action _onSave;

        private readonly List<Slider> _sliders = new();
        private readonly List<Toggle> _toggles = new();
        private readonly List<TextButton> _buttons = new();
        private readonly List<Chip> _chips = new();
        private readonly List<(string text, int y)> _sectionTitles = new();
        private Slider? _dragging;

        private int _scroll, _maxScroll, _bodyTop, _bodyBottom, _hintY;

        public RadianceTunerMenu(ModConfig config, Func<string, string> translate, Action onChange, Action onSave)
            : base(0, 0, PanelWidth, 0, showUpperRightCloseButton: true)
        {
            _config = config;
            _t = translate;
            _onChange = onChange;
            _onSave = onSave;
            Reflow();
        }

        private void Reopen() => Game1.activeClickableMenu = new RadianceTunerMenu(_config, _t, _onChange, _onSave);

        private sealed class Chip
        {
            public TextButton Load = null!;
            public Rectangle Delete;
            public NamedProfile Profile = null!;
        }

        private void Reflow()
        {
            int vw = Game1.uiViewport.Width;
            int vh = Game1.uiViewport.Height;

            width = PanelWidth;
            xPositionOnScreen = vw - width - 24;
            yPositionOnScreen = 20;

            _sliders.Clear(); _toggles.Clear(); _buttons.Clear(); _chips.Clear(); _sectionTitles.Clear();

            int x = xPositionOnScreen + 28;
            int innerW = width - 56;
            int cy0 = yPositionOnScreen + HeaderH;
            int y = cy0;

            // Built-in presets (localized label, enum action).
            (LookPreset preset, string key)[] presets =
            {
                (LookPreset.Off, "off"), (LookPreset.Subtle, "subtle"),
                (LookPreset.Cinematic, "cinematic"), (LookPreset.Vibrant, "vibrant")
            };
            int bw = (innerW - 18) / 4;
            for (int i = 0; i < presets.Length; i++)
            {
                var (preset, key) = presets[i];
                var rect = new Rectangle(x + i * (bw + 6), y, bw, 44);
                _buttons.Add(new TextButton(_t($"config.preset.{key}"), rect, () =>
                {
                    _config.ApplyPreset(preset);
                    _onChange(); _onSave(); Reflow();
                }));
            }
            y += 56;

            // Saved custom looks.
            _sectionTitles.Add((_t("tuner.mylooks"), y)); y += 28;
            int chipX = x;
            foreach (var prof in _config.SavedProfiles)
            {
                int cw = Math.Min(160, 44 + (int)(Game1.smallFont.MeasureString(prof.Name).X * 0.7f));
                if (chipX + cw > x + innerW - 100) { chipX = x; y += 46; }
                var rect = new Rectangle(chipX, y, cw, 40);
                var captured = prof;
                _chips.Add(new Chip
                {
                    Load = new TextButton(prof.Name, rect, () => { _config.ApplyProfile(captured); _onChange(); _onSave(); Reflow(); }),
                    Delete = new Rectangle(rect.Right - 14, rect.Y - 6, 24, 24),
                    Profile = captured
                });
                chipX += cw + 12;
            }
            _buttons.Add(new TextButton(_t("tuner.save"), new Rectangle(x + innerW - 96, y, 96, 40), PromptSaveProfile));
            y += 52;

            _toggles.Add(new Toggle(_t("tuner.master"), new Rectangle(x, y, innerW, 38), () => _config.Enabled, v => _config.Enabled = v));
            y += 44;

            y += 30; _sectionTitles.Add((_t("tuner.section.bloom"), y - 28));
            _toggles.Add(new Toggle(_t("tuner.bloom"), new Rectangle(x, y, innerW, 38), () => _config.BloomEnabled, v => _config.BloomEnabled = v));
            y += 44;
            _sliders.Add(new Slider(_t("tuner.intensity"), x, y, innerW, 0f, 2f, () => _config.BloomIntensity, v => _config.BloomIntensity = v));
            y += 50;

            y += 30; _sectionTitles.Add((_t("tuner.section.colorgrade"), y - 28));
            _toggles.Add(new Toggle(_t("tuner.colorgrade"), new Rectangle(x, y, innerW, 38), () => _config.ColorGradeEnabled, v => _config.ColorGradeEnabled = v));
            y += 44;
            _toggles.Add(new Toggle(_t("tuner.automood"), new Rectangle(x, y, innerW, 38), () => _config.ColorGradeAuto, v => _config.ColorGradeAuto = v));
            y += 44;
            _sliders.Add(new Slider(_t("tuner.strength"), x, y, innerW, 0f, 1f, () => _config.ColorGradeStrength, v => _config.ColorGradeStrength = v)); y += 50;
            _sliders.Add(new Slider(_t("tuner.contrast"), x, y, innerW, 0.5f, 1.5f, () => _config.ColorGradeContrast, v => _config.ColorGradeContrast = v)); y += 50;
            _sliders.Add(new Slider(_t("tuner.saturation"), x, y, innerW, 0f, 2f, () => _config.ColorGradeSaturation, v => _config.ColorGradeSaturation = v)); y += 50;
            _sliders.Add(new Slider(_t("tuner.temperature"), x, y, innerW, -1f, 1f, () => _config.ColorGradeTemperature, v => _config.ColorGradeTemperature = v)); y += 50;
            _sliders.Add(new Slider(_t("tuner.brightness"), x, y, innerW, 0.5f, 1.5f, () => _config.ColorGradeBrightness, v => _config.ColorGradeBrightness = v)); y += 50;

            int contentHeight = y - cy0;
            int maxH = vh - 40;
            height = Math.Min(HeaderH + contentHeight + FooterH, maxH);

            _bodyTop = yPositionOnScreen + HeaderH;
            _bodyBottom = yPositionOnScreen + height - FooterH;
            _hintY = yPositionOnScreen + height - 34;
            _maxScroll = Math.Max(0, contentHeight - (_bodyBottom - _bodyTop));
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            upperRightCloseButton.bounds.X = xPositionOnScreen + width - 40;
            upperRightCloseButton.bounds.Y = yPositionOnScreen - 8;
        }

        private void PromptSaveProfile()
        {
            Game1.activeClickableMenu = new TextEntryMenu(_t("tuner.naming"), "",
                onDone: name =>
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _config.SavedProfiles.Add(_config.CaptureProfile(name.Trim()));
                        _onSave();
                    }
                    Reopen();
                },
                onCancel: Reopen);
        }

        private bool Visible(Rectangle contentRect)
        {
            int top = contentRect.Y - _scroll;
            int bottom = contentRect.Bottom - _scroll;
            return bottom > _bodyTop && top < _bodyBottom;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            if (_maxScroll > 0)
                _scroll = Math.Clamp(_scroll - Math.Sign(direction) * 48, 0, _maxScroll);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (y >= _bodyTop && y <= _bodyBottom)
            {
                foreach (var b in _buttons)
                    if (Visible(b.Bounds) && b.Bounds.Contains(x, y + _scroll)) { Game1.playSound("smallSelect"); b.OnClick(); return; }
                foreach (var c in _chips)
                {
                    if (Visible(c.Load.Bounds) && c.Delete.Contains(x, y + _scroll)) { DeleteChip(c); return; }
                    if (Visible(c.Load.Bounds) && c.Load.Bounds.Contains(x, y + _scroll)) { Game1.playSound("smallSelect"); c.Load.OnClick(); return; }
                }
                foreach (var t in _toggles)
                    if (Visible(t.Row) && t.Hit(x, y + _scroll)) { t.Set(!t.Get()); Game1.playSound("drumkit6"); _onChange(); _onSave(); return; }
                foreach (var s in _sliders)
                    if (Visible(s.Track) && s.Track.Contains(x, y + _scroll)) { _dragging = s; s.SetFromX(x); _onChange(); return; }
            }
            base.receiveLeftClick(x, y, playSound);
        }

        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            if (y < _bodyTop || y > _bodyBottom) return;
            foreach (var c in _chips)
                if (Visible(c.Load.Bounds) && c.Load.Bounds.Contains(x, y + _scroll)) { DeleteChip(c); return; }
        }

        private void DeleteChip(Chip c)
        {
            _config.SavedProfiles.Remove(c.Profile);
            _onSave(); Game1.playSound("trashcan"); Reflow();
        }

        public override void leftClickHeld(int x, int y)
        {
            if (_dragging != null) { _dragging.SetFromX(x); _onChange(); }
        }

        public override void releaseLeftClick(int x, int y)
        {
            if (_dragging != null) { _dragging = null; _onSave(); }
            base.releaseLeftClick(x, y);
        }

        protected override void cleanupBeforeExit()
        {
            _onSave();
            base.cleanupBeforeExit();
        }

        public override void draw(SpriteBatch b)
        {
            int innerW = width - 56;

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, drawShadow: true);
            DrawFit(b, _t("tuner.title"), new Vector2(xPositionOnScreen + 28, yPositionOnScreen + 22), innerW - 40, Game1.textColor, 1f);

            int dy = -_scroll;

            foreach (var (text, cy) in _sectionTitles)
            {
                var r = new Rectangle(xPositionOnScreen + 28, cy, innerW, 24);
                if (Visible(r)) DrawFit(b, text, new Vector2(xPositionOnScreen + 28, cy + dy), innerW, Game1.textColor * 0.85f, 0.85f);
            }
            foreach (var btn in _buttons) if (Visible(btn.Bounds)) btn.Draw(b, dy);
            foreach (var c in _chips)
                if (Visible(c.Load.Bounds))
                {
                    c.Load.Draw(b, dy);
                    b.Draw(Game1.mouseCursors, new Rectangle(c.Delete.X, c.Delete.Y + dy, c.Delete.Width, c.Delete.Height), DeleteSource, Color.White);
                }
            foreach (var t in _toggles) if (Visible(t.Row)) t.Draw(b, dy);
            foreach (var s in _sliders) if (Visible(s.Track)) s.Draw(b, dy);

            if (_maxScroll > 0)
            {
                int trackX = xPositionOnScreen + width - 20;
                int trackH = _bodyBottom - _bodyTop;
                b.Draw(Game1.staminaRect, new Rectangle(trackX, _bodyTop, 6, trackH), Color.Black * 0.25f);
                int barH = Math.Max(30, (int)(trackH * (float)trackH / (trackH + _maxScroll)));
                int barY = _bodyTop + (int)((trackH - barH) * (_scroll / (float)_maxScroll));
                b.Draw(Game1.staminaRect, new Rectangle(trackX, barY, 6, barH), new Color(196, 130, 66));
            }

            DrawFit(b, _t("tuner.hint"), new Vector2(xPositionOnScreen + 28, _hintY), innerW, Game1.textColor * 0.7f, 0.8f);

            base.draw(b);
            drawMouse(b);
        }

        private static void DrawFit(SpriteBatch b, string text, Vector2 pos, float maxWidth, Color color, float maxScale)
        {
            float m = Game1.smallFont.MeasureString(text).X;
            float scale = Math.Min(maxScale, maxWidth / Math.Max(1f, m));
            Utility.drawTextWithShadow(b, text, Game1.smallFont, pos, color, scale);
        }

        // ---- widgets (content-space rects; Draw takes a scroll offset) ----

        private sealed class Slider
        {
            private readonly string _label;
            private readonly float _min, _max;
            private readonly Func<float> _get;
            private readonly Action<float> _set;
            public Rectangle Track;

            public Slider(string label, int x, int y, int w, float min, float max, Func<float> get, Action<float> set)
            {
                _label = label; _min = min; _max = max; _get = get; _set = set;
                Track = new Rectangle(x, y + 26, w, 20);
            }

            public void SetFromX(int mx)
            {
                float t = MathHelper.Clamp((mx - Track.X) / (float)Track.Width, 0f, 1f);
                _set((float)Math.Round((_min + t * (_max - _min)) / 0.01f) * 0.01f);
            }

            public void Draw(SpriteBatch b, int dy)
            {
                float v = _get();
                DrawFit(b, _label, new Vector2(Track.X, Track.Y - 26 + dy), Track.Width - 70, Game1.textColor, 0.9f);
                string val = v.ToString("0.00");
                Vector2 vs = Game1.smallFont.MeasureString(val) * 0.9f;
                Utility.drawTextWithShadow(b, val, Game1.smallFont, new Vector2(Track.Right - vs.X, Track.Y - 26 + dy), Game1.textColor * 0.8f, 0.9f);

                var track = new Rectangle(Track.X, Track.Y + dy, Track.Width, Track.Height);
                b.Draw(Game1.staminaRect, track, Color.Black * 0.35f);
                float t = (v - _min) / (_max - _min);
                b.Draw(Game1.staminaRect, new Rectangle(track.X, track.Y, (int)(t * track.Width), track.Height), new Color(196, 130, 66));
                b.Draw(Game1.staminaRect, new Rectangle(track.X + (int)(t * track.Width) - 6, track.Y - 4, 12, track.Height + 8), Color.White);
            }
        }

        private sealed class Toggle
        {
            private static readonly Rectangle Unchecked = new(227, 425, 9, 9);
            private static readonly Rectangle Checked = new(236, 425, 9, 9);
            private readonly string _label;
            public readonly Rectangle Row;
            public readonly Func<bool> Get;
            public readonly Action<bool> Set;

            public Toggle(string label, Rectangle row, Func<bool> get, Action<bool> set)
            {
                _label = label; Row = row; Get = get; Set = set;
            }

            public bool Hit(int x, int y) => new Rectangle(Row.X, Row.Y, Row.Width, 36).Contains(x, y);

            public void Draw(SpriteBatch b, int dy)
            {
                b.Draw(Game1.mouseCursors, new Vector2(Row.X, Row.Y + dy), Get() ? Checked : Unchecked,
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
                DrawFit(b, _label, new Vector2(Row.X + 48, Row.Y + 6 + dy), Row.Width - 56, Game1.textColor, 0.9f);
            }
        }

        private sealed class TextButton
        {
            private readonly string _label;
            public readonly Rectangle Bounds;
            public readonly Action OnClick;

            public TextButton(string label, Rectangle bounds, Action onClick)
            {
                _label = label; Bounds = bounds; OnClick = onClick;
            }

            public void Draw(SpriteBatch b, int dy)
            {
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    Bounds.X, Bounds.Y + dy, Bounds.Width, Bounds.Height, Color.White, 1f, drawShadow: false);
                Vector2 m = Game1.smallFont.MeasureString(_label);
                float scale = Math.Min(0.9f, (Bounds.Width - 24) / Math.Max(1f, m.X));
                Vector2 s = m * scale;
                Utility.drawTextWithShadow(b, _label, Game1.smallFont,
                    new Vector2(Bounds.Center.X - s.X / 2f, Bounds.Center.Y - s.Y / 2f + dy), Game1.textColor, scale);
            }
        }
    }
}
