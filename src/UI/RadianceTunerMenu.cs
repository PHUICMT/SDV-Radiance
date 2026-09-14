using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>
    /// On-screen tuner overlay, TAB-RAIL layout: a column of category tabs on the left,
    /// the selected category's controls on the right. Only one category is on screen at a
    /// time, so the panel stays short no matter how many settings exist. Occupies the right
    /// side so the world stays visible and updates live as you drag. Localized; opened with
    /// the tuner hotkey.
    /// </summary>
    internal sealed class RadianceTunerMenu : IClickableMenu
    {
        // Base (smallest) layout. Everything below scales up from here so the panel keeps its
        // share of a big window instead of shrinking into a corner of it - and so a long Thai
        // tab label has room to sit at full size rather than being squeezed to fit.
        // Wide enough for a full-length label NEXT TO an icon. At the old width the icon ate
        // the room the text needed, and the shrink-to-fit quietly dropped the longest label to
        // half the size of its neighbours rather than overflowing - legible in the code,
        // obviously wrong on screen.
        private const int BaseRailWidth = 196;
        private const int BaseContentWidth = 430;
        private const int PanelWidth = BaseRailWidth + BaseContentWidth;
        private const int BaseHeaderHeight = 52;
        private const int BaseFooterHeight = 40;

        private int RailWidth = BaseRailWidth;
        private int ContentWidth = BaseContentWidth;
        private int HeaderHeight = BaseHeaderHeight;
        private int FooterHeight = BaseFooterHeight;
        /// <summary>1 at the base size, up to 1.6 on a large window. Multiplies every row
        /// height, box and text size so the whole panel grows together.</summary>
        private float _uiScale = 1f;
        private int Scaled(int basePixels) => (int)Math.Round(basePixels * _uiScale);

        /// <summary>The heights every row in this panel is built from, before the ui scale. They
        /// were spelled as bare numbers at eleven places that build a row by hand, while the
        /// Toggle and Slider helpers beside them used the scaled versions, so at a ui scale of
        /// 1.6 those eleven rows drew at base size and lapped over the scaled rows around them.
        /// RowHeight is the row itself, RowPitch the step to the next one.</summary>
        private const int RowHeightBase = 38, RowPitchBase = 44, ButtonHeightBase = 40, ChipPitchBase = 46, SectionGapBase = 52;

        private int RowHeight => Scaled(RowHeightBase);
        private int RowPitch => Scaled(RowPitchBase);
        private int ButtonHeight => Scaled(ButtonHeightBase);
        private const int BodyPadding = 12;   // breathing room at the top/bottom of the scrolling content
        private const int NaturalTabPitch = 54;   // rail button spacing, now always honoured
        /// <summary>How wide the rail's own scrollbar is, when the rail has more tabs than
        /// the window can show at once.</summary>
        private const int RailBarWidth = 4;
        private static readonly Rectangle DeleteSource = new(192, 256, 64, 64); // red X in mouseCursors
        private static readonly RasterizerState _scissorRaster = new() { ScissorTestEnable = true, CullMode = CullMode.None };

        private readonly ModConfig _config;
        private readonly Func<string, string> _translate;
        private readonly Action _onChange;
        private readonly Action _onSave;

        private readonly List<TunerSlider> _sliders = new();
        private readonly List<TunerCompass> _compasses = new();
        private readonly List<TunerToggle> _toggles = new();
        private readonly List<TunerTextButton> _buttons = new();   // content-area buttons (scroll with content)
        private readonly List<TunerChip> _chips = new();
        private readonly List<(string text, int y)> _sectionTitles = new();
        /// <summary>Read-only lines. Supplied per draw rather than baked at layout time, so a
        /// running measurement can count up without rebuilding the menu underneath it.</summary>
        private readonly List<(Func<string> text, int y, int height)> _infoLines = new();
        /// <summary>
        /// A plain-language note per control row, shown while the pointer rests on it.
        ///
        /// <para>
        /// Asked for in exactly these words: bloom, vignette, aberration and GI lighting "is
        /// confusing for the ordinary player who has no idea about them". A name is not an
        /// explanation, and a settings screen full of names the player has to look up elsewhere
        /// is a settings screen they turn off instead of tuning.
        /// </para>
        ///
        /// <para>
        /// The rectangles are in CONTENT coordinates, the same as every other row here, so the
        /// scroll offset is taken off the pointer rather than added to a hundred rectangles.
        /// </para>
        /// </summary>
        private readonly List<(Rectangle row, string text)> _help = new();
        private string? _hoverText;

        /// <summary>The hover note with line breaks put in, and what it was made from.
        ///
        /// <para>The game's hover box measures whatever string it is handed and never breaks one:
        /// it lays the whole note out as a single line and then slides the box left until its
        /// right edge is on screen, so a note longer than the window starts somewhere off the left
        /// of it. Every note here is a sentence or three on purpose - the whole reason they exist
        /// is that a name is not an explanation - so they are exactly the strings that overflow.</para>
        ///
        /// <para>Kept rather than rebuilt each frame because the hover runs at sixty frames a
        /// second and the wrap allocates a string.</para></summary>
        private string? _hoverTextWrapped;
        private string? _hoverTextWrappedFrom;
        private int _hoverTextWrappedWidth;
        private int _seenBenchStamp = -1;

        /// <summary>Tab icons: one 16x16 cell per tab, in tab order (assets/tuner-icons.png,
        /// generated by tools/make-tuner-icons.py). Loaded once and kept - a menu that
        /// reopens constantly must not re-read a file each time.</summary>
        private static Texture2D? _icons;
        private static bool _iconsTried;
        private const int IconSize = 16;
        private float _iconScale;
        private readonly List<(TunerTextButton button, int tabIndex)> _tabRailButtons = new();  // fixed, never scroll
        private TunerSlider? _dragging;
        /// <summary>A dial keeps the drag once it has it, exactly as a track does, so the pointer
        /// can leave the circle and keep turning it instead of snapping back at the edge.</summary>
        private TunerCompass? _draggingCompass;

        /// <summary>Every tab, in the order the rail shows them. STATIC, and the only list of
        /// them: the console names a tab before any menu exists, so a second hand-written copy
        /// lived in OpenAtTab, and a tab added to one list and not the other sent
        /// "radiance_tuner water" to whichever tab the shift landed on. The build step takes the
        /// menu as an argument, which is what lets the table be static while the builders stay
        /// instance methods. The active tab is remembered across reopens.</summary>
        private static readonly (string Key, string DescriptionKey, Action<RadianceTunerMenu> Build)[] Tabs =
        {
            // Ordered the way a game's video settings are: the two global answers first
            // ("make it look right", "make it run"), then the detail grouped by family -
            // camera/film, then light, then the world - and the troubleshooting switch
            // last. The old order was the order the effects happened to be built in, which
            // left the quality control that everyone needs sitting eleventh.
            ("tuner.tab.looks", "tuner.desc.looks", menu => menu.BuildLooks()),
            ("config.section.perf", "tuner.desc.perf", menu => menu.BuildPerformance()),
            ("tuner.section.colorgrade", "tuner.desc.colorgrade", menu => menu.BuildColorGrade()),
            ("tuner.section.bloom", "tuner.desc.bloom", menu => menu.BuildBloom()),
            ("tuner.tab.lens", "tuner.desc.lens", menu => menu.BuildLens()),
            ("tuner.tab.smoothing", "tuner.desc.smoothing", menu => menu.BuildSmoothing()),
            ("tuner.section.lighting", "tuner.desc.lighting", menu => menu.BuildLighting()),
            ("tuner.section.windows", "tuner.desc.windows", menu => menu.BuildWindows()),
            ("tuner.section.shadows", "tuner.desc.shadows", menu => menu.BuildShadows()),
            ("tuner.section.godrays", "tuner.desc.godrays", menu => menu.BuildGodRays()),
            ("tuner.section.water", "tuner.desc.water", menu => menu.BuildWater()),
            ("tuner.section.cloudshadow", "tuner.desc.cloudshadow", menu => menu.BuildCloud()),
            ("tuner.tab.fog", "tuner.desc.fog", menu => menu.BuildFog()),
            ("config.section.weather", "tuner.desc.weather", menu => menu.BuildWeather()),
            ("config.section.particles", "tuner.desc.particles", menu => menu.BuildParticles()),
            ("config.section.camera", "tuner.desc.camera", menu => menu.BuildCamera()),
            ("config.section.debug", "tuner.desc.debug", menu => menu.BuildDiagnostics()),
        };
        private static int _lastTab;

        /// <summary>Which tab the next open should land on, by the KEY of the tab rather than by
        /// its position, so inserting a tab does not silently repoint every caller. Used by the
        /// console command: the game takes input from SDL, so nothing outside the process can
        /// click a tab, and checking a change on one meant asking a person to do it.</summary>
        internal static void OpenAtTab(string keyFragment)
        {
            for (int i = 0; i < Tabs.Length; i++)
            {
                if (Tabs[i].Key.IndexOf(keyFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _lastTab = i;
                    return;
                }
            }
        }
        private int _activeTab;

        private int _scroll, _maxScroll, _bodyTop, _bodyBottom, _hintY, _contentX;
        /// <summary>First tab shown in the rail, and how far it may be pushed. Counted in TABS
        /// rather than pixels so a button can never be left half off the bottom of the frame.</summary>
        private int _railScroll, _maxRailScroll;
        // content-column layout cursor (build helpers advance it)
        private int _contentCursorX, _contentCursorY, _contentColumnWidth;

        public RadianceTunerMenu(ModConfig config, Func<string, string> translate, Action onChange, Action onSave)
            : base(0, 0, PanelWidth, 0, showUpperRightCloseButton: true)
        {
            _config = config;
            _translate = translate;
            _onChange = onChange;
            _onSave = onSave;
            _activeTab = Math.Clamp(_lastTab, 0, Tabs.Length - 1);
            Reflow();
        }

        private void Reopen() => Game1.activeClickableMenu = new RadianceTunerMenu(_config, _translate, _onChange, _onSave);

        // ---- content build helpers (append to lists, advance _contentCursorY) ----
        private void Section(string key)
        {
            // A heading over rows that are all hidden is a heading over nothing.
            if (_rowsEnabledWhen != null && !_rowsEnabledWhen())
                return;
            _sectionTitles.Add((_translate(key), _contentCursorY)); _contentCursorY += Scaled(30);
        }
        /// <summary>The condition every row built from here on has to meet to be live. A tab sets it
        /// once around the block its master switch owns, instead of every row repeating it, and
        /// clears it after. Rows that pass their own condition ignore this.</summary>
        private Func<bool>? _rowsEnabledWhen;

        /// <summary>Everything between here and <see cref="EndDependsOn"/> is dead while this is false.</summary>
        private void DependsOn(Func<bool> condition) => _rowsEnabledWhen = condition;
        private void EndDependsOn() => _rowsEnabledWhen = null;

        private void Info(Func<string> text) { _infoLines.Add((text, _contentCursorY, Scaled(22))); _contentCursorY += Scaled(26); }

        /// <summary>A sentence or three, wrapped to the column at reading size. Info lines shrink
        /// to fit one row, which is right for a bench figure and wrong for a tab's description:
        /// the weather tab's Thai description came out a hairline nobody could read. The wrap is
        /// done once here, at the scale the line is drawn at, so the row is as tall as it needs.</summary>
        private void Paragraph(string text)
        {
            float textScale = 0.72f * _uiScale;
            string wrapped = Game1.parseText(text, Game1.smallFont, (int)(_contentColumnWidth / textScale));
            int height = (int)Math.Ceiling(TunerText.Measure(wrapped).Y * textScale);
            _infoLines.Add((() => wrapped, _contentCursorY, height));
            _contentCursorY += height + Scaled(4);
        }
        /// <summary>Rows whose condition is false are not built at all - the section collapses
        /// instead of greying out. Asked for in exactly these words: a slider that does nothing
        /// should not be on screen. Every toggle click rebuilds the tab (see receiveLeftClick),
        /// so the rows a switch owns appear and disappear with it; the Enabled dim survives only
        /// as the fallback for a condition that changes without a rebuild.</summary>
        private void Toggle(string key, Func<bool> getValue, Action<bool> setValue, string? help = null, Func<bool>? enabledWhen = null)
        {
            Func<bool>? rowEnabledWhen = enabledWhen ?? _rowsEnabledWhen;
            if (rowEnabledWhen != null && !rowEnabledWhen())
                return;
            var row = new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(38));
            _toggles.Add(new TunerToggle(_translate(key), row, getValue, setValue) { TextScale = _uiScale, Enabled = rowEnabledWhen });
            Help(row, help);
            _contentCursorY += Scaled(44);
        }
        /// <summary>A labelled slider row. <paramref name="step"/> is the smallest move it makes,
        /// and defaults to a hundredth, which suits a dial that runs 0 to 1 or wider; a dial whose
        /// whole range is a fraction of that has to say so or it gets a handful of positions.</summary>
        private void Slider(string key, float min, float max, Func<float> getValue, Action<float> setValue,
            string? help = null, Func<bool>? enabledWhen = null, float step = 0.01f)
        {
            Func<bool>? rowEnabledWhen = enabledWhen ?? _rowsEnabledWhen;
            if (rowEnabledWhen != null && !rowEnabledWhen())
                return;
            _sliders.Add(new TunerSlider(_translate(key), _contentCursorX, _contentCursorY, _contentColumnWidth, min, max, getValue, setValue, Scaled(26), Scaled(20))
                { TextScale = _uiScale, Enabled = rowEnabledWhen, Step = step });
            // The label sits above the track, so the hover area is the whole row, not the bar.
            Help(new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(50)), help);
            _contentCursorY += Scaled(50);
        }

        /// <summary>A round bearing dial. Taller than a slider because it is a picture of the
        /// answer rather than a number, which is what makes it worth the room.</summary>
        private void Compass(string key, Func<float> getDegrees, Action<float> setDegrees, string? help = null, Func<bool>? enabledWhen = null)
        {
            Func<bool>? rowEnabledWhen = enabledWhen ?? _rowsEnabledWhen;
            if (rowEnabledWhen != null && !rowEnabledWhen())
                return;
            var compass = new TunerCompass(_translate(key), _contentCursorX, _contentCursorY, _contentColumnWidth,
                Scaled(26), Scaled(120), getDegrees, setDegrees)
            { TextScale = _uiScale, Enabled = rowEnabledWhen };
            _compasses.Add(compass);
            Help(compass.Row, help);
            _contentCursorY += compass.Row.Height + Scaled(10);
        }

        /// <summary>Register the plain-language note for the row just laid out. Only rows that
        /// were given a key get one, so there is no guessing at whether a translation exists.</summary>
        private void Help(Rectangle row, string? key)
        {
            if (key != null)
                _help.Add((row, _translate(key)));
        }
        private TunerTextButton Button(string label, Rectangle bounds, Action onClick)
        {
            var button = new TunerTextButton(label, bounds, onClick) { TextScale = _uiScale };
            _buttons.Add(button);
            return button;
        }

        private void Reflow()
        {
            int viewportWidth = Game1.uiViewport.Width;
            int viewportHeight = Game1.uiViewport.Height;

            // Take a share of the window rather than a fixed number of pixels: at the base size
            // this panel is a small box on a large display, and the rail is too narrow for a
            // full-length Thai label. Height matters as much as width here, since the rail has
            // to fit twelve tabs.
            _uiScale = Math.Clamp(Math.Min(viewportWidth / 1600f, viewportHeight / 900f), 1f, 1.6f);
            RailWidth = Scaled(BaseRailWidth);
            ContentWidth = Scaled(BaseContentWidth);
            HeaderHeight = Scaled(BaseHeaderHeight);
            FooterHeight = Scaled(BaseFooterHeight);

            width = RailWidth + ContentWidth;
            xPositionOnScreen = viewportWidth - width - Scaled(24);
            yPositionOnScreen = Scaled(20);

            _sliders.Clear(); _compasses.Clear(); _toggles.Clear(); _buttons.Clear(); _chips.Clear(); _sectionTitles.Clear(); _tabRailButtons.Clear(); _infoLines.Clear(); _help.Clear();
            _rowsEnabledWhen = null;   // a tab's dependency must not survive into the next one

            int contentTop = yPositionOnScreen + HeaderHeight;

            // ---- left rail: one button per tab, scrolled when there are more than fit ----
            // The rail is what sets the panel height, so its pitch has to be settled FIRST.
            // The pitch used to be SQUEEZED to make every tab fit at once, which worked at
            // twelve tabs and stopped working at fifteen: every button got shorter, the icons
            // shrank with them, and the rail turned into a stack of thin slivers. A list too
            // long for its window is what scrolling is for, so the pitch is fixed now and the
            // rail carries whatever it can, one whole tab at a time.
            int maxBody = (viewportHeight - Scaled(40)) - HeaderHeight - FooterHeight;
            int tabPitch = Scaled(NaturalTabPitch);
            int railVisibleTabs = Math.Max(1, Math.Min(Tabs.Length, maxBody / tabPitch));
            _maxRailScroll = Tabs.Length - railVisibleTabs;
            // Never leave the chosen tab off the end of what is showing: opening the menu on a
            // tab near the bottom, or being sent to one by name, has to bring it into view.
            _railScroll = Math.Clamp(_railScroll, 0, _maxRailScroll);
            if (_activeTab < _railScroll)
                _railScroll = _activeTab;
            else if (_activeTab >= _railScroll + railVisibleTabs)
                _railScroll = _activeTab - railVisibleTabs + 1;

            if (!_iconsTried)
            {
                _iconsTried = true;
                _icons = RenderPipeline.Current?.LoadTexture("tuner-icons.png");
            }

            int railButtonX = xPositionOnScreen + Scaled(12);
            int railButtonWidth = RailWidth - Scaled(20);
            // Tie the icon to the button it sits in: when a short window squeezes the pitch,
            // a fixed icon size would poke out of the top and bottom of its own button.
            _iconScale = _icons != null ? Math.Min(2f * _uiScale, (tabPitch - Scaled(10)) / (float)IconSize) : 0f;
            int iconInset = _icons != null ? (int)(IconSize * _iconScale) + Scaled(10) : 0;
            // Only the tabs on screen become buttons at all, so drawing and clicking are both
            // clipped to the rail by construction rather than by a second bounds test.
            for (int i = _railScroll; i < _railScroll + railVisibleTabs; i++)
            {
                int tabIndex = i;
                var rect = new Rectangle(railButtonX, contentTop + (i - _railScroll) * tabPitch, railButtonWidth, tabPitch - Scaled(4));
                _tabRailButtons.Add((new TunerTextButton(_translate(Tabs[i].Key), rect, () =>
                {
                    _activeTab = tabIndex; _lastTab = tabIndex; _scroll = 0; Reflow();
                })
                { TextScale = _uiScale, LeftInset = iconInset }, i));
            }

            // ---- right content column: only the active tab ----
            _contentX = xPositionOnScreen + RailWidth;
            _contentCursorX = _contentX + Scaled(16);
            _contentColumnWidth = ContentWidth - Scaled(40);
            _contentCursorY = contentTop + BodyPadding;
            // One line saying what this tab is for, before anything else on it. A column of
            // sliders assumes the reader already knows which effect they belong to.
            Paragraph(_translate(Tabs[_activeTab].DescriptionKey));
            _contentCursorY += Scaled(6);
            Tabs[_activeTab].Build(this);
            int contentHeight = _contentCursorY - (contentTop + BodyPadding);

            // CONSISTENT panel height across tabs: the frame is sized to the tab rail, capped
            // to the view. Switching tabs never resizes the panel; a tab whose content is
            // taller than the frame scrolls inside it (mouse wheel) instead of growing it.
            int bodyHeight = railVisibleTabs * tabPitch;
            height = HeaderHeight + bodyHeight + FooterHeight;

            _bodyTop = contentTop + Scaled(BodyPadding);
            _bodyBottom = contentTop + bodyHeight - Scaled(BodyPadding);
            _hintY = yPositionOnScreen + height - Scaled(30);
            _maxScroll = Math.Max(0, contentHeight - (_bodyBottom - _bodyTop));
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            upperRightCloseButton.bounds.X = xPositionOnScreen + width - Scaled(40);
            upperRightCloseButton.bounds.Y = yPositionOnScreen - Scaled(8);
        }

        // ================= per-tab content =================

        private void BuildLooks()
        {
            // Preset buttons (4 across).
            (LookPreset preset, string key)[] presets =
            {
                (LookPreset.Off, "off"), (LookPreset.Subtle, "subtle"),
                (LookPreset.Cinematic, "cinematic"), (LookPreset.Vibrant, "vibrant")
            };
            int presetButtonWidth = (_contentColumnWidth - 18) / 4;
            for (int i = 0; i < presets.Length; i++)
            {
                var (preset, key) = presets[i];
                var rect = new Rectangle(_contentCursorX + i * (presetButtonWidth + 6), _contentCursorY, presetButtonWidth, RowPitch);
                var presetButton = new TunerTextButton(_translate($"config.preset.{key}"), rect, () =>
                {
                    // Record WHICH look was picked, not only its numbers. Without this the
                    // settings menu still read "Custom" after a preset was chosen here, so the
                    // two menus disagreed about a thing the player had just done.
                    _config.ActivePreset = preset;
                    _config.ApplyPreset(preset); _onChange(); _onSave(); Reflow();
                });
                presetButton.IsChosen = () => _config.ActivePreset == preset;
                _buttons.Add(presetButton);
            }
            _contentCursorY += 56;

            Section("tuner.mylooks");
            int chipX = _contentCursorX;
            foreach (var profile in _config.SavedProfiles)
            {
                int chipWidth = Math.Min(160, 44 + (int)(Game1.smallFont.MeasureString(profile.Name).X * 0.7f));
                if (chipX + chipWidth > _contentCursorX + _contentColumnWidth - 100) { chipX = _contentCursorX; _contentCursorY += Scaled(ChipPitchBase); }
                var rect = new Rectangle(chipX, _contentCursorY, chipWidth, ButtonHeight);
                var captured = profile;
                var load = new TunerTextButton(profile.Name, rect, () => { _config.ApplyProfile(captured); _onChange(); _onSave(); Reflow(); });
                // Lit while the live settings are still exactly what this look holds, so the
                // panel says which saved look is in effect; move any slider and it goes out.
                load.IsChosen = () => _config.MatchesProfile(captured);
                _chips.Add(new TunerChip
                {
                    Load = load,
                    Delete = new Rectangle(rect.Right - 14, rect.Y - 6, 24, 24),
                    Profile = captured
                });
                chipX += chipWidth + 12;
            }
            _buttons.Add(new TunerTextButton(_translate("tuner.save"), new Rectangle(_contentCursorX + _contentColumnWidth - 96, _contentCursorY, 96, ButtonHeight), PromptSaveProfile));
            _contentCursorY += Scaled(SectionGapBase);

            Toggle("tuner.master", () => _config.Enabled, value => _config.Enabled = value, "help.master");
        }

        /// <summary>The colour looks, as a row of buttons, with the strength of the chosen one.
        ///
        /// <para>Here and not only in GMCM because a look is judged by eye: this panel leaves the
        /// scene visible, and a config menu does not. Looks found in the player's own folder are
        /// listed after the ones that ship, and when there are none the row is just the shipped
        /// set, so nothing appears for a case that is almost everyone's.</para>
        /// </summary>
        /// <summary>
        /// What a look is called on the button, by the same rule the GMCM dropdown uses.
        ///
        /// <para>The buttons showed their file names, so every language read "warm-film" while
        /// the settings menu next door read "Warm film" in that language. Reported by a
        /// translator who had already written the keys and could not work out why nothing
        /// consumed them: they were consumed, just not here.</para>
        ///
        /// <para>A look the player dropped in the folder themselves keeps its file name, marked
        /// as theirs. Only they know what it is, and there is no key to translate.</para>
        /// </summary>
        private string LutLabel(string look)
        {
            if (look.Length == 0)
                return _translate("config.colorgrade.lut.none");
            return Array.IndexOf(ModConfig.ShippedLuts, look) >= 0
                ? _translate($"config.colorgrade.lut.{look}")
                : $"{look} ({_translate("config.colorgrade.lut.yours")})";
        }

        private void BuildLutPicker()
        {
            Section("tuner.lut");
            string[] looks = ModConfig.ShippedLuts.Concat(LutCatalog.Discover()).ToArray();
            int buttonX = _contentCursorX;
            foreach (string look in looks)
            {
                string label = LutLabel(look);
                int buttonWidth = Math.Min(180, 28 + (int)(Game1.smallFont.MeasureString(label).X * 0.7f));
                if (buttonX + buttonWidth > _contentCursorX + _contentColumnWidth) { buttonX = _contentCursorX; _contentCursorY += Scaled(44); }
                string chosen = look;
                var button = Button(label, new Rectangle(buttonX, _contentCursorY, buttonWidth, Scaled(38)), () =>
                {
                    _config.ColorGradeLut = chosen;
                    // Choosing a look with the strength at zero would do nothing at all and read
                    // as the look being broken. Zero is where the slider lands after picking None,
                    // so it is a state a player arrives at without meaning to.
                    if (chosen.Length > 0 && _config.ColorGradeLutAmount <= 0f)
                        _config.ColorGradeLutAmount = 1f;
                    _onChange(); _onSave(); Reflow();
                });
                button.IsChosen = () => string.Equals(_config.ColorGradeLut, chosen, StringComparison.OrdinalIgnoreCase);
                buttonX += buttonWidth + Scaled(8);
            }
            _contentCursorY += Scaled(46);
            Slider("tuner.lutamount", 0f, 1f, () => _config.ColorGradeLutAmount,
                value => _config.ColorGradeLutAmount = value, "help.lutamount");
        }

        private void BuildColorGrade()
        {
            Toggle("tuner.colorgrade", () => _config.ColorGradeEnabled, value => _config.ColorGradeEnabled = value, "help.colorgrade");
            // The whole grade dies with its switch (the stage gates on it), so every row here
            // hides with it - EXCEPT the blue-light filter at the bottom, which the finishing
            // pass applies whether the grade runs or not, so it must stay on screen.
            if (_config.ColorGradeEnabled)
                BuildLutPicker();
            DependsOn(() => _config.ColorGradeEnabled);
            Toggle("tuner.automood", () => _config.ColorGradeAuto, value => _config.ColorGradeAuto = value, "help.automood");
            Slider("tuner.strength", 0f, 1f, () => _config.ColorGradeStrength, value => _config.ColorGradeStrength = value);
            Slider("tuner.contrast", 0.5f, 1.5f, () => _config.ColorGradeContrast, value => _config.ColorGradeContrast = value, "help.contrast");
            Slider("tuner.saturation", 0f, 2f, () => _config.ColorGradeSaturation, value => _config.ColorGradeSaturation = value, "help.saturation");
            Slider("tuner.temperature", -1f, 1f, () => _config.ColorGradeTemperature, value => _config.ColorGradeTemperature = value, "help.temperature");
            Slider("tuner.brightness", 0.5f, 1.5f, () => _config.ColorGradeBrightness, value => _config.ColorGradeBrightness = value);
            Toggle("tuner.tonemap", () => _config.ColorGradeToneMap, value => _config.ColorGradeToneMap = value, "help.tonemap");
            EndDependsOn();
            Slider("tuner.bluelight", 0f, 1f, () => _config.BlueLightFilter, value => _config.BlueLightFilter = value, "help.bluelight");
        }

        private void BuildBloom()
        {
            Toggle("tuner.bloom", () => _config.BloomEnabled, value => _config.BloomEnabled = value, "help.bloom");
            // Bloom's own dials do nothing while bloom is off.
            DependsOn(() => _config.BloomEnabled);
            Slider("tuner.intensity", 0f, 2f, () => _config.BloomIntensity, value => _config.BloomIntensity = value);
            Slider("tuner.bloomthreshold", 0f, 1f, () => _config.BloomThreshold, value => _config.BloomThreshold = value, "help.bloomthreshold");
            Slider("tuner.bloomemissiveboost", 0f, 1f, () => _config.BloomEmissiveBoost, value => _config.BloomEmissiveBoost = value, "help.bloomemissiveboost");
            EndDependsOn();
        }

        /// <summary>The seven kinds of caster, each carrying the three dials that belong to it.
        /// Grouped by the thing rather than by the dial: tuning one building used to mean three
        /// sliders eight rows apart in three separate blocks of seven. The name key is the one the
        /// length block already used, so no kind had to be renamed or retranslated.</summary>
        private static readonly (string NameKey,
            Func<ModConfig, float> GetLength, Action<ModConfig, float> SetLength,
            Func<ModConfig, float> GetSoftness, Action<ModConfig, float> SetSoftness,
            Func<ModConfig, float> GetLean, Action<ModConfig, float> SetLean)[] ShadowKinds =
        {
            ("tuner.shadowlength.trees",
                config => config.ShadowLengthTrees,        (config, value) => config.ShadowLengthTrees = value,
                config => config.ShadowSoftnessTrees,      (config, value) => config.ShadowSoftnessTrees = value,
                config => config.ShadowLeanTrees,          (config, value) => config.ShadowLeanTrees = value),
            ("tuner.shadowlength.smalltrees",
                config => config.ShadowLengthSmallTrees,   (config, value) => config.ShadowLengthSmallTrees = value,
                config => config.ShadowSoftnessSmallTrees, (config, value) => config.ShadowSoftnessSmallTrees = value,
                config => config.ShadowLeanSmallTrees,     (config, value) => config.ShadowLeanSmallTrees = value),
            ("tuner.shadowlength.bushes",
                config => config.ShadowLengthBushes,       (config, value) => config.ShadowLengthBushes = value,
                config => config.ShadowSoftnessBushes,     (config, value) => config.ShadowSoftnessBushes = value,
                config => config.ShadowLeanBushes,         (config, value) => config.ShadowLeanBushes = value),
            ("tuner.shadowlength.crops",
                config => config.ShadowLengthCrops,        (config, value) => config.ShadowLengthCrops = value,
                config => config.ShadowSoftnessCrops,      (config, value) => config.ShadowSoftnessCrops = value,
                config => config.ShadowLeanCrops,          (config, value) => config.ShadowLeanCrops = value),
            ("tuner.shadowlength.grass",
                config => config.ShadowLengthGrass,        (config, value) => config.ShadowLengthGrass = value,
                config => config.ShadowSoftnessGrass,      (config, value) => config.ShadowSoftnessGrass = value,
                config => config.ShadowLeanGrass,          (config, value) => config.ShadowLeanGrass = value),
            ("tuner.shadowlength.objects",
                config => config.ShadowLengthObjects,      (config, value) => config.ShadowLengthObjects = value,
                config => config.ShadowSoftnessObjects,    (config, value) => config.ShadowSoftnessObjects = value,
                config => config.ShadowLeanObjects,        (config, value) => config.ShadowLeanObjects = value),
            ("tuner.shadowlength.buildings",
                config => config.ShadowLengthBuildings,    (config, value) => config.ShadowLengthBuildings = value,
                config => config.ShadowSoftnessBuildings,  (config, value) => config.ShadowSoftnessBuildings = value,
                config => config.ShadowLeanBuildings,      (config, value) => config.ShadowLeanBuildings = value),
        };

        /// <summary>Which kind the shadow tab is showing dials for. Static so it survives closing
        /// the menu, because the whole point is to change one kind, go and look at it in the game,
        /// and come back to the same kind rather than hunting for it again. Not saved to config:
        /// it is a place in a menu, not a setting.</summary>
        private static int _shadowKindIndex;

        private void BuildShadows()
        {
            Toggle("tuner.shadows", () => _config.DirectionalShadowsEnabled, value => _config.DirectionalShadowsEnabled = value, "help.shadows");
            // Nothing below does anything while the shadows themselves are off.
            DependsOn(() => _config.DirectionalShadowsEnabled);
            // Which shapes, before any dial, the same way the water page opens with which water.
            // Two buttons named by the version each look shipped in, with the one in use lit.
            Section("tuner.shadowmodel");
            if (_config.DirectionalShadowsEnabled)
            {
                (ShadowModel model, string key)[] shadowModels =
                {
                    (ShadowModel.Modern, "modern"),
                    (ShadowModel.Classic, "classic"),
                };
                int buttonWidth = (_contentColumnWidth - 6 * (shadowModels.Length - 1)) / shadowModels.Length;
                for (int i = 0; i < shadowModels.Length; i++)
                {
                    var (model, key) = shadowModels[i];
                    var rect = new Rectangle(_contentCursorX + i * (buttonWidth + 6), _contentCursorY, buttonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.shadowmodel.{key}"), rect, () =>
                    {
                        _config.DirectionalShadowModel = model; _onChange(); _onSave(); Reflow();
                    });
                    button.IsChosen = () => _config.DirectionalShadowModel == model;
                    Help(rect, $"help.shadowmodel.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            Slider("tuner.shadowstrength", 0f, 1f, () => _config.DirectionalShadowStrength, value => _config.DirectionalShadowStrength = value);
            Slider("tuner.shadowlength", 0.2f, 2f, () => _config.DirectionalShadowLength, value => _config.DirectionalShadowLength = value, "help.shadowlength");
            Slider("tuner.goldenhour", 0f, 1f, () => _config.GoldenHourStrength, value => _config.GoldenHourStrength = value, "help.goldenhour");
            Slider("tuner.sunseason", 0f, 1f, () => _config.SunSeasonStrength, value => _config.SunSeasonStrength = value, "help.sunseason");
            Compass("tuner.shadowsunbearing", () => _config.ShadowSunBearing, value => _config.ShadowSunBearing = value, "help.shadowsunbearing");
            Compass("tuner.sunlightbearing", () => _config.SunlightBearing, value => _config.SunlightBearing = value, "help.sunlightbearing");
            Slider("tuner.shadowtint", 0f, 1f, () => _config.ShadowTint, value => _config.ShadowTint = value, "help.shadowtint");
            Slider("tuner.shadowblur", 0f, 5f, () => _config.DirectionalShadowBlur, value => _config.DirectionalShadowBlur = value, "help.shadowblur");
            Slider("tuner.shadowcontacthardness", 0f, 1f, () => _config.ShadowContactHardness, value => _config.ShadowContactHardness = value, "help.shadowcontacthardness");
            Slider("tuner.shadowpenumbrastretch", 0f, 1f, () => _config.ShadowPenumbraStretch, value => _config.ShadowPenumbraStretch = value, "help.shadowpenumbrastretch");
            Slider("tuner.shadowcasts", ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax,
                () => _config.ShadowCastsPerCharacter,
                value => _config.ShadowCastsPerCharacter = (int)MathF.Round(value), "help.shadowcasts");
            Toggle("tuner.shadowplayer", () => _config.DirectionalShadowPlayer, value => _config.DirectionalShadowPlayer = value, "help.shadowplayer");
            Toggle("tuner.shadowvillagers", () => _config.DirectionalShadowVillagers, value => _config.DirectionalShadowVillagers = value, "help.shadowvillagers");
            Toggle("tuner.shadowfarmanimals", () => _config.DirectionalShadowFarmAnimals, value => _config.DirectionalShadowFarmAnimals = value, "help.shadowfarmanimals");
            Toggle("tuner.shadowcreatures", () => _config.DirectionalShadowCreatures, value => _config.DirectionalShadowCreatures = value, "help.shadowcreatures");
            Toggle("tuner.shadowobjects", () => _config.DirectionalShadowObjects, value => _config.DirectionalShadowObjects = value, "help.shadowobjects");
            Slider("tuner.contactshadow", 0f, 1f, () => _config.ContactShadowStrength, value => _config.ContactShadowStrength = value, "help.contactshadow");
            Slider("tuner.contactshadowpeople", 0f, 1f, () => _config.ContactShadowPeopleStrength, value => _config.ContactShadowPeopleStrength = value, "help.contactshadowpeople");
            Toggle("tuner.shadowbuildings", () => _config.DirectionalShadowBuildings, value => _config.DirectionalShadowBuildings = value, "help.shadowbuildings");
            Slider("tuner.shadowgroundforeshortening", ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax,
                () => _config.ShadowGroundForeshortening, value => _config.ShadowGroundForeshortening = value, "help.shadowgroundforeshortening");
            Slider("tuner.shadowcharactergroundforeshortening", ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax,
                () => _config.ShadowCharacterGroundForeshortening, value => _config.ShadowCharacterGroundForeshortening = value, "help.shadowcharactergroundforeshortening");
            // One kind at a time. Pick the thing, and its three dials sit together under the
            // picker, instead of the page carrying all twenty-one at once with a building's three
            // eight rows apart in three different blocks.
            Section("tuner.shadowperkind");
            if (_config.DirectionalShadowsEnabled)
            {
                const int kindColumns = 2;
                int kindButtonWidth = (_contentColumnWidth - 6 * (kindColumns - 1)) / kindColumns;
                for (int i = 0; i < ShadowKinds.Length; i++)
                {
                    int column = i % kindColumns, row = i / kindColumns;
                    // A kind left alone on the last row takes the whole width rather than sitting
                    // beside a gap.
                    bool isLastAndAlone = i == ShadowKinds.Length - 1 && column == 0;
                    var rect = new Rectangle(_contentCursorX + column * (kindButtonWidth + 6),
                        _contentCursorY + row * Scaled(46),
                        isLastAndAlone ? _contentColumnWidth : kindButtonWidth, Scaled(40));
                    int chosenIndex = i;
                    var kindButton = Button(_translate(ShadowKinds[i].NameKey), rect, () =>
                    {
                        // Nothing is saved by picking a kind: this moves the page, not a value.
                        _shadowKindIndex = chosenIndex; Reflow();
                    });
                    kindButton.IsChosen = () => _shadowKindIndex == chosenIndex;
                }
                _contentCursorY += (ShadowKinds.Length + kindColumns - 1) / kindColumns * Scaled(46) + Scaled(6);
            }
            // Clamped rather than trusted: the field is static and outlives any one menu, so a
            // shorter list in a later version would otherwise index off the end.
            var shadowKind = ShadowKinds[Math.Clamp(_shadowKindIndex, 0, ShadowKinds.Length - 1)];
            Slider("tuner.shadowkind.length", ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax,
                () => shadowKind.GetLength(_config), value => shadowKind.SetLength(_config, value));
            Slider("tuner.shadowkind.softness", ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax,
                () => shadowKind.GetSoftness(_config), value => shadowKind.SetSoftness(_config, value));
            Slider("tuner.shadowkind.lean", ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax,
                () => shadowKind.GetLean(_config), value => shadowKind.SetLean(_config, value), "help.shadowlean");
            EndDependsOn();
        }

        private void BuildLighting()
        {
            Toggle("tuner.lighting", () => _config.LightingEnabled, value => _config.LightingEnabled = value, "help.lighting");
            // Every light dial below belongs to the lighting pass. Grouped by family - the
            // darkness dials, then the shadows lamps throw, then the bounced light - because
            // the old order had the lamp-shadow softness dials sitting a whole GI block away
            // from the lamp-shadow switch that owns them.
            DependsOn(() => _config.LightingEnabled);
            Slider("tuner.lightindoor", 0f, 0.95f, () => _config.LightingIndoorDarkness, value => _config.LightingIndoorDarkness = value, "help.lightindoor");
            Slider("tuner.lightnight", 0f, 0.95f, () => _config.LightingNightDarkness, value => _config.LightingNightDarkness = value, "help.lightnight");
            Slider("tuner.lightmorning", 0f, 0.95f, () => _config.LightingMorningDarkness, value => _config.LightingMorningDarkness = value, "help.lightmorning");
            Slider("tuner.lightindoorcolour", 0f, 1f, () => _config.LightingIndoorColourWalk, value => _config.LightingIndoorColourWalk = value, "help.lightindoorcolour");
            Slider("tuner.lightmorningcool", 0f, 1f, () => _config.LightingMorningClearSkyCool, value => _config.LightingMorningClearSkyCool = value, "help.lightmorningcool");
            Slider("tuner.lightwarmth", 0f, 1f, () => _config.LightingWarmth, value => _config.LightingWarmth = value, "help.lightwarmth");
            Slider("tuner.lightboost", 0f, 2f, () => _config.LightingBoost, value => _config.LightingBoost = value, "help.lightboost");
            Slider("tuner.lightradius", 0.2f, 3f, () => _config.LightingRadiusScale, value => _config.LightingRadiusScale = value, "help.lightradius");
            Section("tuner.section.lampshadows");
            Toggle("tuner.lightshadows", () => _config.LightingShadows, value => _config.LightingShadows = value, "help.lightshadows");
            DependsOn(() => _config.LightingEnabled && _config.LightingShadows);
            Slider("tuner.lightshadowstrength", 0f, 1f, () => _config.LightingShadowStrength, value => _config.LightingShadowStrength = value);
            Toggle("tuner.lightsilhouettes", () => _config.LightShadowSilhouettes, value => _config.LightShadowSilhouettes = value, "help.lightsilhouettes");
            Toggle("tuner.lightprops", () => _config.LightShadowProps, value => _config.LightShadowProps = value, "help.lightprops");
            Slider("tuner.lightshadowcarve", 0f, 1f, () => _config.LightShadowCarve, value => _config.LightShadowCarve = value, "help.lightshadowcarve");
            Slider("tuner.lightshadowsoftness", 0f, 2f, () => _config.LightShadowSoftness, value => _config.LightShadowSoftness = value, "help.lightshadowsoftness");
            Slider("tuner.lightshadowdetail", 0f, 1f, () => _config.LightShadowDetail, value => _config.LightShadowDetail = value, "help.lightshadowdetail");
            Toggle("tuner.lightshadowshared", () => _config.LightShadowDetailShared, value => _config.LightShadowDetailShared = value, "help.lightshadowshared");
            Toggle("tuner.lightshadowsharp", () => _config.LightShadowSharpEdges, value => _config.LightShadowSharpEdges = value, "help.lightshadowsharp");
            Toggle("tuner.lightshadowcache", () => _config.LightShadowMarchCache, value => _config.LightShadowMarchCache = value, "help.lightshadowcache");
            Slider("tuner.wateredsoil", 0f, 1f, () => _config.WateredSoilSparkle, value => _config.WateredSoilSparkle = value, "help.wateredsoil");
            DependsOn(() => _config.LightingEnabled);
            Section("tuner.section.gi");
            Toggle("tuner.floodgi", () => _config.FloodLightingEnabled, value => _config.FloodLightingEnabled = value, "help.floodgi");
            DependsOn(() => _config.LightingEnabled && _config.FloodLightingEnabled);
            if (_config.LightingEnabled && _config.FloodLightingEnabled)
            {
                // Which model computes the GI map: two buttons, the one in use lit (see WaterReflectModel).
                (GiModel model, string key)[] giModels = { (GiModel.Flood, "flood"), (GiModel.Cascades, "cascades") };
                int giButtonWidth = (_contentColumnWidth - 6 * (giModels.Length - 1)) / giModels.Length;
                for (int giIndex = 0; giIndex < giModels.Length; giIndex++)
                {
                    var (model, key) = giModels[giIndex];
                    var rect = new Rectangle(_contentCursorX + giIndex * (giButtonWidth + 6), _contentCursorY, giButtonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.gimodel.{key}"), rect, () => { _config.FloodGiModel = model; _onChange(); _onSave(); });
                    button.IsChosen = () => _config.FloodGiModel == model;
                    Help(rect, $"help.gimodel.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            Slider("tuner.floodstrength", 0f, 1.5f, () => _config.FloodLightingStrength, value => _config.FloodLightingStrength = value, "help.floodstrength");
            Slider("tuner.floodshadow", 0f, 1f, () => _config.FloodShadowStrength, value => _config.FloodShadowStrength = value, "help.floodshadow");
            Slider("tuner.colourbleed", 0f, 1f, () => _config.FloodColourBleed, value => _config.FloodColourBleed = value, "help.colourbleed");
            Toggle("tuner.relief", () => _config.SpriteReliefEnabled, value => _config.SpriteReliefEnabled = value, "help.relief");
            Toggle("tuner.reliefhalfres", () => _config.SpriteReliefHalfResolution, value => _config.SpriteReliefHalfResolution = value, "help.reliefhalfres",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Slider("tuner.reliefstrength", 0f, 1f, () => _config.SpriteReliefStrength, value => _config.SpriteReliefStrength = value, "help.reliefstrength",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Slider("tuner.reliefsun", 0f, 1f, () => _config.SpriteReliefSun, value => _config.SpriteReliefSun = value, "help.reliefsun",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Slider("tuner.reliefrim", 0f, 1f, () => _config.SpriteReliefRim, value => _config.SpriteReliefRim = value, "help.reliefrim",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Slider("tuner.leafshimmer", 0f, 1f, () => _config.SpriteReliefLeafShimmer, value => _config.SpriteReliefLeafShimmer = value, "help.leafshimmer",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            EndDependsOn();
        }

        /// <summary>Everything the mod does with a window, on its own tab: the daylight it lets in,
        /// the beam you can see, the glow after dusk, and the people in the glass by day.</summary>
        private void BuildWindows()
        {
            Section("tuner.section.windowlight");
            Toggle("tuner.windoweffects", () => _config.WindowEffectsEnabled, value => _config.WindowEffectsEnabled = value, "help.windoweffects");
            // The beam and the daylight it lays on the floor belong to the window-light master;
            // the glass rows below belong to the reflection switch. Two families, two gates.
            DependsOn(() => _config.WindowEffectsEnabled);
            Slider("tuner.windowopensnight", 0f, 1f, () => _config.WindowGlowOpensNight, value => _config.WindowGlowOpensNight = value, "help.windowopensnight");
            Slider("tuner.lamphalo", 0f, 1f, () => _config.LampHalo, value => _config.LampHalo = value, "help.lamphalo");
            Slider("tuner.aquariumripple", 0f, 1f, () => _config.AquariumRipple, value => _config.AquariumRipple = value, "help.aquariumripple");
            Toggle("tuner.windowbeam", () => _config.WindowBeamEnabled, value => _config.WindowBeamEnabled = value, "help.windowbeam");
            Slider("tuner.windowdaylightstrength", 0f, 2f, () => _config.WindowDaylightStrength,
                value => _config.WindowDaylightStrength = value, "help.windowdaylightstrength");
            Slider("tuner.windowdaylightelsewhere", 0f, 2f, () => _config.WindowDaylightStrengthElsewhere,
                value => _config.WindowDaylightStrengthElsewhere = value, "help.windowdaylightelsewhere");
            EndDependsOn();
            Section("tuner.section.windowreflection");
            Toggle("tuner.windowreflection", () => _config.WindowReflectionEnabled, value => _config.WindowReflectionEnabled = value, "help.windowreflection");
            DependsOn(() => _config.WindowReflectionEnabled);
            Toggle("tuner.windowreflectionindoors", () => _config.WindowReflectionIndoors, value => _config.WindowReflectionIndoors = value, "help.windowreflectionindoors");
            Slider("tuner.windowreflectionstrength", 0f, 2f, () => _config.WindowReflectionStrength,
                value => _config.WindowReflectionStrength = value, "help.windowreflectionstrength");
            Slider("tuner.windowreflectionnight", 0f, 2f, () => _config.WindowReflectionNightStrength,
                value => _config.WindowReflectionNightStrength = value, "help.windowreflectionnight");
            Slider("tuner.windowsheen", 0f, 2f, () => _config.WindowSheenStrength,
                value => _config.WindowSheenStrength = value, "help.windowsheen");
            Slider("tuner.windowscene", 0f, 2f, () => _config.WindowSceneReflectionStrength,
                value => _config.WindowSceneReflectionStrength = value, "help.windowscene");
            Slider("tuner.windowglare", 0f, 2f, () => _config.WindowGlareStrength,
                value => _config.WindowGlareStrength = value, "help.windowglare");
            Slider("tuner.windowlightglow", 0f, 2f, () => _config.WindowLightGlowStrength,
                value => _config.WindowLightGlowStrength = value, "help.windowlightglow");
            EndDependsOn();
            // The beam switches itself off when a mod that draws its own is installed, and until
            // now it did that in the startup log only. On screen it read as a feature that simply
            // does not work, with a switch that appears to do nothing when you turn it back on and
            // reopen the menu. Say who took it, where the switch is.
            if (!string.IsNullOrEmpty(_config.WindowCompatAppliedFor) && !_config.WindowBeamEnabled)
                Paragraph(_translate("tuner.windowcompat"));
        }

        private void BuildGodRays()
        {
            Section("tuner.section.godrayslamps");
            Toggle("tuner.godrays", () => _config.GodRaysEnabled, value => _config.GodRaysEnabled = value, "help.godrays");
            // The strength dial needs the shafts on.
            DependsOn(() => _config.GodRaysEnabled);
            Slider("tuner.godraysintensity", 0f, 2f, () => _config.GodRaysIntensity, value => _config.GodRaysIntensity = value);
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.godrayssun");
            Toggle("tuner.godrayssun", () => _config.GodRaysSun, value => _config.GodRaysSun = value, "help.godrayssun");
            // The sun switch stands alone by design (see SetSunShaftParams) - its dials hang
            // off it, not off the lamp master above.
            DependsOn(() => _config.GodRaysSun);
            Slider("tuner.godrayssunintensity", 0f, 1.5f, () => _config.GodRaysSunIntensity,
                value => _config.GodRaysSunIntensity = value, "help.godrayssunintensity");
            Slider("tuner.godrayssunreach", 0.1f, 1f, () => _config.GodRaysSunReach,
                value => _config.GodRaysSunReach = value, "help.godrayssunreach");
            Toggle("tuner.godrayssunglassroof", () => _config.GodRaysSunGlassRoof,
                value => _config.GodRaysSunGlassRoof = value, "help.godrayssunglassroof");
            EndDependsOn();
        }

        private void BuildCloud()
        {
            Toggle("tuner.cloudshadow", () => _config.CloudShadowEnabled, value => _config.CloudShadowEnabled = value, "help.cloudshadow");
            // Cloud shadow settings need cloud shadows.
            DependsOn(() => _config.CloudShadowEnabled);
            Toggle("tuner.cloudhidevanilla", () => _config.SuppressVanillaCloudShadow, value => _config.SuppressVanillaCloudShadow = value);
            Slider("tuner.cloudcoverage", 0.1f, 0.9f, () => _config.CloudShadowCoverage, value => _config.CloudShadowCoverage = value, "help.cloudcoverage");
            Slider("tuner.cloudcount", 0f, 1f, () => _config.CloudShadowCount, value => _config.CloudShadowCount = value, "help.cloudcount");
            Slider("tuner.cloudopacity", 0f, 0.7f, () => _config.CloudShadowOpacity, value => _config.CloudShadowOpacity = value);
            // A sixteenth of the range per step, matching the other menu's interval for it.
            Slider("tuner.cloudspeed", 0f, 0.06f, () => _config.CloudShadowSpeed, value => _config.CloudShadowSpeed = value, step: 0.005f);
            Slider("tuner.cloudscale", 1f, 5f, () => _config.CloudShadowScale, value => _config.CloudShadowScale = value, "help.cloudscale");
            EndDependsOn();
            // Outside the dependency on purpose: the afternoon before rain also takes a little
            // warmth out of the light, which it still does with the cloud shadows switched off.
            Slider("tuner.stormwarning", 0f, 1f, () => _config.StormWarningStrength, value => _config.StormWarningStrength = value, "help.stormwarning");
        }

        private void BuildFog()
        {
            Section("tuner.section.fog");
            Toggle("tuner.fog", () => _config.FogEnabled, value => _config.FogEnabled = value, "help.fog");
            // Day fog's dials need day fog - and ONLY those. The night mist is a separate
            // effect with a separate toggle on the render side, and one DependsOn wrapped
            // around the whole tab dimmed the night rows whenever the DAY fog was off, which
            // read as "night mist is off" while it kept drawing every night.
            DependsOn(() => _config.FogEnabled);
            Slider("tuner.fogcoverage", 0f, 1f, () => _config.FogCoverage, value => _config.FogCoverage = value);
            Slider("tuner.fogdensity", 0f, 1f, () => _config.FogDensity, value => _config.FogDensity = value);
            Slider("tuner.fogspeed", 0f, 0.1f, () => _config.FogSpeed, value => _config.FogSpeed = value, step: 0.005f);
            Slider("tuner.fogscale", 1f, 8f, () => _config.FogScale, value => _config.FogScale = value, "help.fogscale");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.fognight");
            Toggle("tuner.fognightmist", () => _config.FogNightMist, value => _config.FogNightMist = value, "help.fognightmist");
            DependsOn(() => _config.FogNightMist);
            Slider("tuner.fognightmistcoverage", 0f, 1f, () => _config.FogNightMistCoverage, value => _config.FogNightMistCoverage = value);
            Slider("tuner.fognightmistdensity", 0f, 1f, () => _config.FogNightMistDensity, value => _config.FogNightMistDensity = value);
            Slider("tuner.minefogmist", 0f, 1f, () => _config.MineFogMist, value => _config.MineFogMist = value, "help.minefogmist");
            Slider("tuner.fognightmistlampglow", 0f, 1f, () => _config.FogNightMistLampGlow, value => _config.FogNightMistLampGlow = value, "help.fognightmistlampglow");
            Slider("tuner.fognightmistspeed", 0f, 0.1f, () => _config.FogNightMistSpeed, value => _config.FogNightMistSpeed = value);
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.fogboth");
            // Shared by both fogs, so it goes grey only when neither is on.
            DependsOn(() => _config.FogEnabled || _config.FogNightMist);
            Slider("tuner.fogtopbias", 0f, 1f, () => _config.FogTopBias,
                value => _config.FogTopBias = value, "help.fogtopbias");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.heathaze");
            Toggle("tuner.heathaze", () => _config.HeatHazeEnabled, value => _config.HeatHazeEnabled = value, "help.heathaze");
            DependsOn(() => _config.HeatHazeEnabled);
            Slider("tuner.heathazestrength", 0f, 2f, () => _config.HeatHazeStrength,
                value => _config.HeatHazeStrength = value, "help.heathazestrength");
            EndDependsOn();
        }

        private void BuildWeather()
        {
            Section("tuner.section.foliagesway");
            Toggle("tuner.foliagesway", () => _config.FoliageSwayEnabled, value => _config.FoliageSwayEnabled = value, "help.foliagesway");
            DependsOn(() => _config.FoliageSwayEnabled);
            Slider("tuner.foliageswaystrength", 0f, 2f, () => _config.FoliageSwayStrength, value => _config.FoliageSwayStrength = value, "help.foliageswaystrength");
            Slider("config.weather.foliageswayspeed.name", 0.25f, 2f, () => _config.FoliageSwaySpeed,
                value => _config.FoliageSwaySpeed = value, "config.weather.foliageswayspeed.tooltip");
            Slider("config.weather.foliageswaygustspan.name", 4f, 40f, () => _config.FoliageSwayGustSpan,
                value => _config.FoliageSwayGustSpan = value, "config.weather.foliageswaygustspan.tooltip");
            Toggle("tuner.foliageswaycrops", () => _config.FoliageSwayCrops, value => _config.FoliageSwayCrops = value, "help.foliageswaycrops");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.sky");
            Toggle("tuner.precipitation", () => _config.PrecipitationEnabled, value => _config.PrecipitationEnabled = value, "help.precipitation");
            Slider("tuner.snowglint", 0f, 1f, () => _config.SnowGlintStrength, value => _config.SnowGlintStrength = value, "help.snowglint");
            Toggle("tuner.aurora", () => _config.AuroraEnabled, value => _config.AuroraEnabled = value, "help.aurora");
            Slider("tuner.aurorastrength", 0f, 2f, () => _config.AuroraStrength,
                value => _config.AuroraStrength = value, "help.aurorastrength",
                () => _config.AuroraEnabled);
            Toggle("tuner.shootingstars", () => _config.ShootingStarsEnabled, value => _config.ShootingStarsEnabled = value, "help.shootingstars");
            // Each kind of weather has its own switch under the precipitation master, and its
            // dials hang off BOTH (PrecipitationSystem asks the master and the kind together).
            if (_config.PrecipitationEnabled)
                _contentCursorY += 12;
            DependsOn(() => _config.PrecipitationEnabled);
            Section("tuner.section.precipitationrain");
            Toggle("tuner.precipitationrain", () => _config.PrecipitationRain, value => _config.PrecipitationRain = value, "help.precipitationrain");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationRain);
            Slider("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationRainDensity,
                value => _config.PrecipitationRainDensity = value, "help.precipitationdensity");
            Slider("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationRainSize,
                value => _config.PrecipitationRainSize = value, "help.precipitationsize");
            Slider("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationRainOpacity,
                value => _config.PrecipitationRainOpacity = value, "help.precipitationopacity");
            Slider("tuner.precipitationstormdensity", 1f, 3f, () => _config.PrecipitationStormDensity,
                value => _config.PrecipitationStormDensity = value, "help.precipitationstormdensity");
            Slider("tuner.precipitationrainslant", 0f, 3f, () => _config.PrecipitationRainSlant,
                value => _config.PrecipitationRainSlant = value, "help.precipitationrainslant");
            DependsOn(() => _config.PrecipitationEnabled);
            if (_config.PrecipitationEnabled)
                _contentCursorY += 12;
            Section("tuner.section.precipitationsnow");
            Toggle("tuner.precipitationsnow", () => _config.PrecipitationSnow, value => _config.PrecipitationSnow = value, "help.precipitationsnow");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationSnow);
            Slider("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationSnowDensity,
                value => _config.PrecipitationSnowDensity = value, "help.precipitationdensity");
            Slider("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationSnowSize,
                value => _config.PrecipitationSnowSize = value, "help.precipitationsize");
            Slider("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationSnowOpacity,
                value => _config.PrecipitationSnowOpacity = value, "help.precipitationopacity");
            DependsOn(() => _config.PrecipitationEnabled);
            if (_config.PrecipitationEnabled)
                _contentCursorY += 12;
            Section("tuner.section.precipitationwind");
            Toggle("tuner.precipitationwind", () => _config.PrecipitationWind, value => _config.PrecipitationWind = value, "help.precipitationwind");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationWind);
            Slider("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationWindDensity,
                value => _config.PrecipitationWindDensity = value, "help.precipitationdensity");
            Slider("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationWindSize,
                value => _config.PrecipitationWindSize = value, "help.precipitationsize");
            Slider("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationWindOpacity,
                value => _config.PrecipitationWindOpacity = value, "help.precipitationopacity");
            Slider("tuner.precipitationwindslant", 0.25f, 3f, () => _config.PrecipitationWindSlant,
                value => _config.PrecipitationWindSlant = value, "help.precipitationwindslant");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.lightning");
            Toggle("tuner.lightning", () => _config.LightningEffectsEnabled, value => _config.LightningEffectsEnabled = value, "help.lightning");
            Toggle("tuner.lightningbolts", () => _config.LightningBoltsEnabled, value => _config.LightningBoltsEnabled = value, "help.lightningbolts",
                () => _config.LightningEffectsEnabled);
            _contentCursorY += 12;
            // The wet GROUND is not offered here. It is written and it works, but where
            // standing water may honestly lie is a question about the map and on a modded map
            // the answer was sometimes a roof. Until that is decided from the map rather than
            // guessed at, the whole of it stays off and out of the way; radiance_config still
            // reaches WetWorldEnabled for anyone who wants to look at it.
            Section("tuner.section.screendrops");
            Toggle("tuner.wetworldlensdrops", () => _config.WetWorldLensDrops, value => _config.WetWorldLensDrops = value, "help.wetworldlensdrops");
            // The edge haze is drawn by the same pass as the drops (ScreenEdgeDrops), so it
            // goes with the drops switch too, not only the drop size.
            DependsOn(() => _config.WetWorldLensDrops);
            Slider("tuner.wetworldlensdropsize", 0.5f, 2f, () => _config.WetWorldLensDropSize,
                value => _config.WetWorldLensDropSize = value, "help.wetworldlensdropsize");
            Slider("tuner.wetworldedgehaze", 0f, 2f, () => _config.WetWorldEdgeHaze,
                value => _config.WetWorldEdgeHaze = value, "help.wetworldedgehaze");
            EndDependsOn();
        }

        private void BuildParticles()
        {
            Toggle("tuner.particles", () => _config.ParticlesEnabled, value => _config.ParticlesEnabled = value, "help.particles");
            // Every particle kind below is off with the master switch.
            DependsOn(() => _config.ParticlesEnabled);
            Slider("tuner.particledensity", 0.25f, 2f, () => _config.ParticleDensity,
                value => _config.ParticleDensity = value, "help.particledensity");
            Emitter("dust", () => _config.ParticleDust, value => _config.ParticleDust = value,
                () => _config.ParticleDustAmount, value => _config.ParticleDustAmount = value,
                () => _config.ParticleDustSize, value => _config.ParticleDustSize = value);
            Emitter("embers", () => _config.ParticleEmbers, value => _config.ParticleEmbers = value,
                () => _config.ParticleEmbersAmount, value => _config.ParticleEmbersAmount = value,
                () => _config.ParticleEmbersSize, value => _config.ParticleEmbersSize = value);
            Emitter("fireflies", () => _config.ParticleFireflies, value => _config.ParticleFireflies = value,
                () => _config.ParticleFirefliesAmount, value => _config.ParticleFirefliesAmount = value,
                () => _config.ParticleFirefliesSize, value => _config.ParticleFirefliesSize = value);
            Emitter("petals", () => _config.ParticlePetals, value => _config.ParticlePetals = value,
                () => _config.ParticlePetalsAmount, value => _config.ParticlePetalsAmount = value,
                () => _config.ParticlePetalsSize, value => _config.ParticlePetalsSize = value);
            // Only the flat things buckle, so this belongs to the petals and not to the whole set.
            Slider("tuner.particlepetalsflutter", 0f, 1f, () => _config.ParticlePetalsFlutter,
                value => _config.ParticlePetalsFlutter = value, "help.particlepetalsflutter",
                () => _config.ParticlesEnabled && _config.ParticlePetals);
            Emitter("ringsparkles", () => _config.ParticleRingSparkles, value => _config.ParticleRingSparkles = value,
                () => _config.ParticleRingSparklesAmount, value => _config.ParticleRingSparklesAmount = value,
                () => _config.ParticleRingSparklesSize, value => _config.ParticleRingSparklesSize = value);
            Emitter("footdust", () => _config.ParticleFootDust, value => _config.ParticleFootDust = value,
                () => _config.ParticleFootDustAmount, value => _config.ParticleFootDustAmount = value,
                () => _config.ParticleFootDustSize, value => _config.ParticleFootDustSize = value);
            Emitter("festivelights", () => _config.ParticleFestiveLights, value => _config.ParticleFestiveLights = value,
                () => _config.ParticleFestiveLightsAmount, value => _config.ParticleFestiveLightsAmount = value,
                () => _config.ParticleFestiveLightsSize, value => _config.ParticleFestiveLightsSize = value);
            Emitter("chimney", () => _config.ParticleChimney, value => _config.ParticleChimney = value,
                () => _config.ParticleChimneyAmount, value => _config.ParticleChimneyAmount = value,
                () => _config.ParticleChimneySize, value => _config.ParticleChimneySize = value);
            // Not one emitter's setting: it belongs to every glowing particle at once, so it sits
            // on its own under them rather than inside any of their groups.
            Slider("tuner.particleglowlight", 0f, 1f, () => _config.ParticleGlowLight,
                value => _config.ParticleGlowLight = value, "help.particleglowlight",
                () => _config.ParticlesEnabled);
            Emitter("waterfallmist", () => _config.ParticleWaterfallMist, value => _config.ParticleWaterfallMist = value,
                () => _config.ParticleWaterfallMistAmount, value => _config.ParticleWaterfallMistAmount = value,
                () => _config.ParticleWaterfallMistSize, value => _config.ParticleWaterfallMistSize = value);
            Toggle("tuner.waterfallrainbowsun", () => _config.WaterfallRainbowFollowsSun, value => _config.WaterfallRainbowFollowsSun = value, "help.waterfallrainbowsun");
            Slider("tuner.waterfallrainbow", 0f, 1f, () => _config.WaterfallRainbowStrength,
                value => _config.WaterfallRainbowStrength = value, "help.waterfallrainbow",
                () => _config.ParticlesEnabled && _config.ParticleWaterfallMist);
            Emitter("hotspringsteam", () => _config.ParticleHotSpringSteam, value => _config.ParticleHotSpringSteam = value,
                () => _config.ParticleHotSpringSteamAmount, value => _config.ParticleHotSpringSteamAmount = value,
                () => _config.ParticleHotSpringSteamSize, value => _config.ParticleHotSpringSteamSize = value);
            Emitter("lavasparks", () => _config.ParticleLavaSparks, value => _config.ParticleLavaSparks = value,
                () => _config.ParticleLavaSparksAmount, value => _config.ParticleLavaSparksAmount = value,
                () => _config.ParticleLavaSparksSize, value => _config.ParticleLavaSparksSize = value);
            EndDependsOn();
        }

        /// <summary>One emitter's block: its own heading, its own switch, and its own amount and
        /// size. Every emitter has the same three, so the ones that come after this are one line
        /// each rather than another block that has to be kept in step by hand.</summary>
        private void Emitter(string emitter, Func<bool> getOn, Action<bool> setOn,
                             Func<float> getAmount, Action<float> setAmount,
                             Func<float> getSize, Action<float> setSize)
        {
            Func<bool>? master = _rowsEnabledWhen;
            if (master == null || master())
                _contentCursorY += 12;
            Section($"tuner.section.particle{emitter}");
            Toggle($"tuner.particle{emitter}", getOn, setOn, $"help.particle{emitter}");
            // Amount and size ask the emitter's own switch as well as the master (every emitter
            // multiplies both in), so they hide with either.
            Func<bool> emitterOn = () => (master == null || master()) && getOn();
            Slider("tuner.particleamount", 0f, 2f, getAmount, setAmount, "help.particleamount", emitterOn);
            Slider("tuner.particlesize", 0.5f, 2f, getSize, setSize, "help.particlesize", emitterOn);
        }

        private void BuildWater()
        {
            Toggle("tuner.water", () => _config.WaterEnabled, value => _config.WaterEnabled = value, "help.water");
            // The whole water tab hangs off the water effect itself.
            DependsOn(() => _config.WaterEnabled);
            Slider("tuner.waterstrength", 0f, 2f, () => _config.WaterStrength, value => _config.WaterStrength = value, "help.waterstrength");
            Slider("tuner.watersparkle", 0f, 1f, () => _config.WaterSparkle, value => _config.WaterSparkle = value, "help.watersparkle");
            Slider("tuner.watersparkledensity", 0.2f, 2f, () => _config.WaterSparkleDensity, value => _config.WaterSparkleDensity = value);
            Toggle("tuner.watersparklecloud", () => _config.WaterSparkleCloudShade, value => _config.WaterSparkleCloudShade = value, "help.watersparklecloud");
            Slider("tuner.waterglitterpath", 0f, 1f, () => _config.WaterGlitterPath, value => _config.WaterGlitterPath = value, "help.waterglitterpath");
            Toggle("tuner.watercaustics", () => _config.WaterCausticsEnabled, value => _config.WaterCausticsEnabled = value, "help.watercaustics");
            Slider("tuner.watercausticsstrength", 0f, 1f, () => _config.WaterCausticsStrength, value => _config.WaterCausticsStrength = value, null,
                () => _config.WaterEnabled && _config.WaterCausticsEnabled);
            Slider("tuner.waterspeed", 0f, 3f, () => _config.WaterSpeed, value => _config.WaterSpeed = value);
            Toggle("tuner.waterreflection", () => _config.WaterReflection, value => _config.WaterReflection = value, "help.waterreflection");
            // Everything from here to the rain rings is the reflection: the model, its dials,
            // blur, depth and reach all draw inside the mirror, so they go with its switch.
            bool reflectionOn = _config.WaterEnabled && _config.WaterReflection;
            DependsOn(() => _config.WaterEnabled && _config.WaterReflection);
            Slider("tuner.waterreflectstrength", 0f, 1f, () => _config.WaterReflectStrength, value => _config.WaterReflectStrength = value);
            // Which water, two buttons, and only then that water's own dials. A dial that does
            // nothing under the water in use is a dial a player moves, sees nothing, and files
            // as broken, so the classic water's three looks and its distortion and banding only
            // appear once the classic water is the one picked.
            Section("tuner.watermodel");
            if (reflectionOn)
            {
                (WaterReflectionModel model, string key)[] waterModels =
                {
                    (WaterReflectionModel.Modern, "modern"),
                    (WaterReflectionModel.Classic, "classic"),
                };
                int modelButtonWidth = (_contentColumnWidth - 6 * (waterModels.Length - 1)) / waterModels.Length;
                for (int i = 0; i < waterModels.Length; i++)
                {
                    var (model, key) = waterModels[i];
                    var rect = new Rectangle(_contentCursorX + i * (modelButtonWidth + 6), _contentCursorY, modelButtonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.watermodel.{key}"), rect, () =>
                    {
                        _config.WaterReflectModel = model; _onChange(); _onSave(); Reflow();
                    });
                    button.IsChosen = () => _config.WaterReflectModel == model;
                    Help(rect, $"help.watermodel.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            if (_config.WaterReflectModel == WaterReflectionModel.Modern)
            {
                Slider("tuner.watermodernwobble", 0f, 2f, () => _config.WaterModernWobble,
                    value => _config.WaterModernWobble = value, "help.watermodernwobble");
                Slider("tuner.watermodernchoppiness", 0f, 1f, () => _config.WaterModernChoppiness,
                    value => _config.WaterModernChoppiness = value, "help.watermodernchoppiness");
                Slider("tuner.watermodernparallax", 0f, 0.3f, () => _config.WaterModernParallax,
                    value => _config.WaterModernParallax = value, "help.watermodernparallax", step: 0.005f);
                Slider("tuner.watermodernfresnel", 0f, 1f, () => _config.WaterModernFresnel,
                    value => _config.WaterModernFresnel = value, "help.watermodernfresnel");
                Slider("tuner.watermodernstretch", 1f, 1.4f, () => _config.WaterModernStretch,
                    value => _config.WaterModernStretch = value, "help.watermodernstretch");
                Slider("tuner.watermodernedgesoftness", 0f, 6f, () => _config.WaterModernEdgeSoftness,
                    value => _config.WaterModernEdgeSoftness = value, "help.watermodernedgesoftness");
                Slider("tuner.watermodernplungechurn", 0f, 1f, () => _config.WaterModernPlungeChurn,
                    value => _config.WaterModernPlungeChurn = value, "help.watermodernplungechurn");
                Slider("tuner.watermodernplungereach", 1f, 6f, () => _config.WaterModernPlungeReach,
                    value => _config.WaterModernPlungeReach = value, "help.watermodernplungereach");
                Slider("tuner.watermodernlipfade", 0f, 1.5f, () => _config.WaterModernLipFade,
                    value => _config.WaterModernLipFade = value, "help.watermodernlipfade");
            }
            else
            {
                // Three named looks rather than another slider: the things a look moves together
                // have no meaning apart, and a picked look is something a player can see the
                // point of without knowing what either number is.
                Section("tuner.reflstyle");
                if (reflectionOn)
                {
                    (WaterReflectionStyle style, string key)[] reflectionStyles =
                    {
                        (WaterReflectionStyle.StillWater, "still"),
                        (WaterReflectionStyle.Natural, "natural"),
                        (WaterReflectionStyle.Choppy, "choppy"),
                    };
                    int styleButtonWidth = (_contentColumnWidth - 6 * (reflectionStyles.Length - 1)) / reflectionStyles.Length;
                    for (int i = 0; i < reflectionStyles.Length; i++)
                    {
                        var (style, key) = reflectionStyles[i];
                        var rect = new Rectangle(_contentCursorX + i * (styleButtonWidth + 6), _contentCursorY, styleButtonWidth, Scaled(40));
                        var button = Button(_translate($"tuner.reflstyle.{key}"), rect, () =>
                        {
                            _config.WaterReflectStyle = style; _onChange(); _onSave(); Reflow();
                        });
                        button.IsChosen = () => _config.WaterReflectStyle == style;
                        Help(rect, $"help.reflstyle.{key}");
                    }
                    _contentCursorY += Scaled(50);
                }
                Slider("tuner.waterreflectdistort", 0f, 1.5f, () => _config.WaterReflectDistort,
                    value => _config.WaterReflectDistort = value, "help.waterreflectdistort");
                Slider("tuner.waterreflectbanding", 0f, 16f, () => _config.WaterReflectBanding,
                    value => _config.WaterReflectBanding = value, "help.waterreflectbanding");
            }
            Slider("tuner.waterreflectblur", 0f, 2f, () => _config.WaterReflectBlur,
                value => _config.WaterReflectBlur = value, "help.waterreflectblur");
            Slider("tuner.reflectdepth", 0.1f, 1.5f, () => _config.WaterReflectDepth,
                value => _config.WaterReflectDepth = value, "help.reflectdepth");
            Slider("tuner.reflectreach", 0.2f, 1f, () => _config.WaterReflectReach,
                value => _config.WaterReflectReach = value, "help.reflectreach");
            DependsOn(() => _config.WaterEnabled);
            if (_config.WaterEnabled)
                _contentCursorY += 12;
            Section("tuner.section.waterrain");
            Slider("tuner.waterrainringdensity", 0f, 2f, () => _config.WaterRainRingDensity,
                value => _config.WaterRainRingDensity = value, "help.waterrainringdensity");
            Slider("tuner.waterrainringsize", 0.4f, 2f, () => _config.WaterRainRingSize,
                value => _config.WaterRainRingSize = value, "help.waterrainringsize");
            Slider("tuner.waterrainringstrength", 0f, 2f, () => _config.WaterRainRingStrength,
                value => _config.WaterRainRingStrength = value, "help.waterrainringstrength");
            Slider("tuner.waterwakerings", 0f, 2f, () => _config.WaterWakeRings,
                value => _config.WaterWakeRings = value, "help.waterwakerings");
            // Greyed out while the rings themselves are off, the way the rainbow rides the mist:
            // a fish spot's rings are drawn by the same surface, so with that at 0 there is
            // nothing for this to move.
            Slider("tuner.waterfishspot", 0f, 2f, () => _config.WaterFishSpotRings,
                value => _config.WaterFishSpotRings = value, "help.waterfishspot",
                () => _config.WaterEnabled && _config.WaterWakeRings > 0f);
            Slider("tuner.waterwind", 0f, 2f, () => _config.WaterWind,
                value => _config.WaterWind = value, "help.waterwind");
            // Reach and fade rows used to sit here, and they were the wrong kind of control for a
            // panel you open to look at something. Both buy frames without changing how the water
            // looks, which is exactly the setting a player moves, sees nothing, and files as
            // broken. The performance preset sets them by name instead - Quality through Low spec
            // - and radiance_config still reaches them for an A/B.
            Toggle("tuner.waterindoors", () => _config.WaterEffectIndoors, value => _config.WaterEffectIndoors = value, "help.waterindoors");

            // Per-room water switch: only in gated building interiors (not outdoors / real level water).
            GameLocation? location = Game1.currentLocation;
            if (_config.WaterEnabled && location != null && !location.IsOutdoors && !RenderPipeline.HasLevelWater(location))
            {
                string key = location.NameOrUniqueName;
                _toggles.Add(new TunerToggle($"{_translate("tuner.waterhere")} · {location.Name}", new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, RowHeight),
                    () => !_config.WaterDisabledLocations.Contains(key),
                    value =>
                    {
                        if (value) _config.WaterDisabledLocations.Remove(key);
                        else if (!_config.WaterDisabledLocations.Contains(key)) _config.WaterDisabledLocations.Add(key);
                    }) { TextScale = _uiScale });
                _contentCursorY += RowPitch;
            }
            EndDependsOn();
        }

        private void BuildLens()
        {
            Section("tuner.section.tiltshift");
            Toggle("tuner.tiltshift", () => _config.TiltShiftEnabled, value => _config.TiltShiftEnabled = value, "help.tiltshift");
            DependsOn(() => _config.TiltShiftEnabled);
            if (_config.TiltShiftEnabled)
            {
                _toggles.Add(new TunerToggle(_translate("tuner.tiltradial"), new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, RowHeight),
                    () => _config.TiltShiftMode == TiltShiftFocus.Radial,
                    value => _config.TiltShiftMode = value ? TiltShiftFocus.Radial : TiltShiftFocus.Bands) { TextScale = _uiScale });
                _contentCursorY += RowPitch;
            }
            // The radius is the radial focus's own; the top and bottom ratios are the bands'.
            // The shader reads one set or the other by mode, so only the set in use is shown.
            Slider("tuner.tiltradius", 0.05f, 0.9f, () => _config.TiltShiftRadius, value => _config.TiltShiftRadius = value, null,
                () => _config.TiltShiftEnabled && _config.TiltShiftMode == TiltShiftFocus.Radial);
            Slider("tuner.tilttop", 0f, 1f, () => _config.TiltShiftTopRatio, value => _config.TiltShiftTopRatio = value, "help.tilttop",
                () => _config.TiltShiftEnabled && _config.TiltShiftMode == TiltShiftFocus.Bands);
            Slider("tuner.tiltbottom", 0f, 1f, () => _config.TiltShiftBottomRatio, value => _config.TiltShiftBottomRatio = value, "help.tiltbottom",
                () => _config.TiltShiftEnabled && _config.TiltShiftMode == TiltShiftFocus.Bands);
            Slider("tuner.tiltfeather", 0f, 1f, () => _config.TiltShiftFeather, value => _config.TiltShiftFeather = value, "help.tiltfeather");
            Slider("tuner.tiltstrength", 0f, 1f, () => _config.TiltShiftStrength, value => _config.TiltShiftStrength = value);
            Slider("tuner.tiltindoor", 0f, 1f, () => _config.TiltShiftIndoorAmount, value => _config.TiltShiftIndoorAmount = value, "help.tiltindoor");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.finishing");
            Toggle("tuner.vignette", () => _config.VignetteEnabled, value => _config.VignetteEnabled = value, "help.vignette");
            Slider("tuner.vignettestrength", 0f, 1f, () => _config.VignetteStrength, value => _config.VignetteStrength = value, null,
                () => _config.VignetteEnabled);
            Toggle("tuner.ca", () => _config.ChromaticAberrationEnabled, value => _config.ChromaticAberrationEnabled = value, "help.ca");
            Slider("tuner.castrength", 0f, 1f, () => _config.ChromaticAberrationStrength, value => _config.ChromaticAberrationStrength = value, null,
                () => _config.ChromaticAberrationEnabled);
        }

        /// <summary>The Scale2x doubling on its own tab: the switch, how far the smoothing goes,
        /// and which of the four art families it touches. Moved out of the performance tab when
        /// it stopped being one switch - it is a look, and it is judged by eye like one.</summary>
        private void BuildSmoothing()
        {
            Toggle("config.sheetupscale.name", () => _config.SheetUpscaleEnabled, value => _config.SheetUpscaleEnabled = value, "help.sheetupscale");
            DependsOn(() => _config.SheetUpscaleEnabled);
            if (_config.SheetUpscaleEnabled)
            {
                // Which look: two buttons, the one in use lit (see the GI model buttons).
                (SheetSmoothingStyle style, string key)[] styles = { (SheetSmoothingStyle.Scale2x, "scale2x"), (SheetSmoothingStyle.Soft4x, "soft4x") };
                int styleButtonWidth = (_contentColumnWidth - 6 * (styles.Length - 1)) / styles.Length;
                for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
                {
                    var (style, key) = styles[styleIndex];
                    var rect = new Rectangle(_contentCursorX + styleIndex * (styleButtonWidth + 6), _contentCursorY, styleButtonWidth, Scaled(40));
                    var button = Button(_translate($"config.sheetupscalestyle.{key}"), rect, () => { _config.SheetUpscaleStyle = style; _onChange(); _onSave(); });
                    button.IsChosen = () => _config.SheetUpscaleStyle == style;
                    Help(rect, $"help.sheetupscalestyle.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            Section("tuner.section.smoothingfamilies");
            Toggle("config.sheetupscaleworld.name", () => _config.SheetUpscaleWorld,
                value => _config.SheetUpscaleWorld = value, "config.sheetupscaleworld.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessWorld,
                value => _config.SheetUpscaleSmoothnessWorld = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleWorld);
            Toggle("config.sheetupscalecharacters.name", () => _config.SheetUpscaleCharacters,
                value => _config.SheetUpscaleCharacters = value, "config.sheetupscalecharacters.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessCharacters,
                value => _config.SheetUpscaleSmoothnessCharacters = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleCharacters);
            Toggle("config.sheetupscaleitems.name", () => _config.SheetUpscaleItems,
                value => _config.SheetUpscaleItems = value, "config.sheetupscaleitems.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessItems,
                value => _config.SheetUpscaleSmoothnessItems = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleItems);
            Toggle("config.sheetupscaleportraits.name", () => _config.SheetUpscalePortraits,
                value => _config.SheetUpscalePortraits = value, "config.sheetupscaleportraits.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessPortraits,
                value => _config.SheetUpscaleSmoothnessPortraits = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscalePortraits);
            Toggle("config.sheetupscaleinterface.name", () => _config.SheetUpscaleInterface,
                value => _config.SheetUpscaleInterface = value, "config.sheetupscaleinterface.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessInterface,
                value => _config.SheetUpscaleSmoothnessInterface = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleInterface);
            EndDependsOn();
        }

        private void BuildPerformance()
        {
            // Quality presets, kept apart from the look presets on the first tab: these change
            // what the picture costs, never what it looks like.
            Section("config.perfpreset.section");
            (PerfPreset preset, string key)[] perfPresets =
            {
                (PerfPreset.Quality, "quality"), (PerfPreset.Balanced, "balanced"),
                (PerfPreset.Performance, "performance"), (PerfPreset.LowSpec, "lowspec")
            };
            int perfButtonWidth = (_contentColumnWidth - 12) / perfPresets.Length;
            for (int i = 0; i < perfPresets.Length; i++)
            {
                var (preset, key) = perfPresets[i];
                var rect = new Rectangle(_contentCursorX + i * (perfButtonWidth + 6), _contentCursorY, perfButtonWidth, RowPitch);
                var perfButton = new TunerTextButton(_translate($"config.perfpreset.{key}"), rect, () =>
                {
                    _config.ApplyPerfPreset(preset); _onChange(); _onSave(); Reflow();
                });
                perfButton.IsChosen = () => _config.ActivePerfPreset == preset;
                _buttons.Add(perfButton);
            }
            _contentCursorY += 56;

            Slider("config.renderscale.name", 0.5f, 1f, () => _config.RenderScale, value => _config.RenderScale = value);
            // Directly under the slider it steers, because it is that slider becoming automatic
            // rather than a separate feature, and because the slider is then read as the ceiling.
            Toggle("config.renderscaleauto.name", () => _config.RenderScaleAuto,
                value => _config.RenderScaleAuto = value, "help.renderscaleauto");
            Slider("config.rendersharpness.name", 0f, 2f, () => _config.RenderSharpness, value => _config.RenderSharpness = value);
            Toggle("config.limitsamplerslots.name", () => _config.LimitSamplerSlots,
                value => _config.LimitSamplerSlots = value, "help.limitsamplerslots");

            // Neither of these is saved to config, and that is deliberate: they are instruments,
            // not settings. A diagnostic overlay that survives a restart is one somebody forgets
            // they left on, and the GPU column reaches into the graphics driver, which is not a
            // state to inherit silently from a session three days ago.
            Section("tuner.section.perfreadout");
            Toggle("tuner.perfhud", () => PerfHud.Visible, value => PerfHud.Visible = value, "help.perfhud");
            Toggle("tuner.gputime", () => GpuTimer.Ready, GpuTimer.SetWanted, "help.gputime");
            if (PerfHud.Visible && !GpuTimer.Ready && GpuTimer.Status != "off")
                Info(() => GpuTimer.Status);

            // Measure this machine instead of guessing at it.
            Section("config.bench.section");
            _buttons.Add(new TunerTextButton(_translate("config.bench.run"),
                new Rectangle(_contentCursorX, _contentCursorY, Math.Min(300, _contentColumnWidth), RowPitch), () =>
                {
                    RenderPipeline.Current?.StartBenchmark(_config);
                    Reflow();
                }));
            _contentCursorY += 54;

            if (RenderPipeline.BenchRunning)
                Info(() => $"{_translate("config.bench.running")} {RenderPipeline.BenchProgress * 100f:0}%");
            foreach (string line in RenderPipeline.BenchSummary)
            {
                string captured = line;
                Info(() => captured);
            }
            if (!RenderPipeline.BenchRunning && RenderPipeline.BenchSummary.Count > 0
                && Math.Abs(RenderPipeline.BenchSuggestedScale - _config.RenderScale) > 0.001f)
            {
                _contentCursorY += 6;
                _buttons.Add(new TunerTextButton(_translate("config.bench.apply"),
                    new Rectangle(_contentCursorX, _contentCursorY, Math.Min(300, _contentColumnWidth), RowPitch), () =>
                    {
                        _config.RenderScale = RenderPipeline.BenchSuggestedScale;
                        _config.Clamp(); _onChange(); _onSave(); Reflow();
                    }));
                _contentCursorY += 54;
            }
        }

        private void BuildCamera()
        {
            // Was reachable from GMCM only, which meant the two menus disagreed about what
            // this mod even contains.
            _toggles.Add(new TunerToggle(_translate("config.camera.mode.smooth"),
                new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(38)),
                () => _config.CameraMode == CameraMode.Smooth,
                value => _config.CameraMode = value ? CameraMode.Smooth : CameraMode.Off)
            { TextScale = _uiScale });
            _contentCursorY += Scaled(44);
            Slider("config.smoothcam.speed.name", 0.05f, 1f, () => _config.CameraFollowSpeed, value => _config.CameraFollowSpeed = value, null,
                () => _config.CameraMode == CameraMode.Smooth);
        }

        private void BuildDiagnostics()
        {
            Toggle("config.debug.name", () => _config.DebugLogging, value => _config.DebugLogging = value);
        }

        // ================= interaction =================

        private void PromptSaveProfile()
        {
            Game1.activeClickableMenu = new TextEntryMenu(_translate("tuner.naming"), "",
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

        private bool Visible(Rectangle contentBounds)
        {
            int top = contentBounds.Y - _scroll;
            int bottom = contentBounds.Bottom - _scroll;
            return bottom > _bodyTop && top < _bodyBottom;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Reflow();
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            _hoverText = null;
            if (y < _bodyTop || y > _bodyBottom || x < _contentX)
                return;
            foreach (var (row, text) in _help)
            {
                if (Visible(row) && row.Contains(x, y + _scroll))
                {
                    _hoverText = text;
                    return;
                }
            }
        }

        /// <summary>The hover note, broken to a width that fits the window. Half the window so the
        /// box never covers the control it is describing, held between a readable measure and a
        /// width no single word can overflow.</summary>
        private string WrappedHoverText()
        {
            int wrapWidth = Math.Clamp(Game1.uiViewport.Width / 2, 320, 640);
            if (!ReferenceEquals(_hoverTextWrappedFrom, _hoverText) || _hoverTextWrappedWidth != wrapWidth)
            {
                _hoverTextWrappedFrom = _hoverText;
                _hoverTextWrappedWidth = wrapWidth;
                _hoverTextWrapped = Game1.parseText(_hoverText, Game1.smallFont, wrapWidth);
            }
            return _hoverTextWrapped ?? _hoverText!;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            // The rail takes the wheel when the pointer is over it. It also takes it when the
            // content has nothing to scroll, so a tall rail is still reachable on a tab whose
            // own column fits: otherwise the wheel would do nothing at all and the tabs below
            // the fold would look unreachable.
            bool overRail = Game1.getMouseX() < xPositionOnScreen + RailWidth;
            if (_maxRailScroll > 0 && (overRail || _maxScroll == 0))
            {
                _railScroll = Math.Clamp(_railScroll - Math.Sign(direction), 0, _maxRailScroll);
                Reflow();
                return;
            }
            if (_maxScroll > 0)
                _scroll = Math.Clamp(_scroll - Math.Sign(direction) * 48, 0, _maxScroll);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // Rail buttons are fixed (no scroll offset) and always clickable.
            foreach (var (button, tabIndex) in _tabRailButtons)
                if (button.Bounds.Contains(x, y)) { if (tabIndex != _activeTab) Game1.playSound("smallSelect"); button.OnClick(); return; }

            if (y >= _bodyTop && y <= _bodyBottom && x >= _contentX)
            {
                foreach (var button in _buttons)
                    if (Visible(button.Bounds) && button.Bounds.Contains(x, y + _scroll)) { Game1.playSound("smallSelect"); button.OnClick(); return; }
                foreach (var chip in _chips)
                {
                    if (Visible(chip.Load.Bounds) && chip.Delete.Contains(x, y + _scroll)) { DeleteChip(chip); return; }
                    if (Visible(chip.Load.Bounds) && chip.Load.Bounds.Contains(x, y + _scroll)) { Game1.playSound("smallSelect"); chip.Load.OnClick(); return; }
                }
                foreach (var toggle in _toggles)
                    // Reflow after every toggle: the rows a switch owns are only built while it
                    // is on, so flipping it has to rebuild the tab. Safe inside the foreach
                    // because the return leaves before the enumerator moves again.
                    if (Visible(toggle.Row) && toggle.Hit(x, y + _scroll)) { toggle.Set(!toggle.Get()); Game1.playSound("drumkit6"); _onChange(); _onSave(); Reflow(); return; }
                foreach (var slider in _sliders)
                    if (Visible(slider.Track) && slider.IsEnabled && slider.Track.Contains(x, y + _scroll)) { _dragging = slider; slider.SetFromX(x); _onChange(); return; }
                foreach (var compass in _compasses)
                    if (Visible(compass.Row) && compass.Hit(x, y + _scroll)) { _draggingCompass = compass; compass.SetFromPoint(x, y + _scroll); _onChange(); return; }
            }
            base.receiveLeftClick(x, y, playSound);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            // A measurement finishing adds result lines and the apply button, so the layout
            // has to be rebuilt once it lands. The running counter itself needs no rebuild:
            // those lines ask for their text every draw.
            if (_seenBenchStamp != RenderPipeline.BenchStamp)
            {
                _seenBenchStamp = RenderPipeline.BenchStamp;
                Reflow();
            }
        }

        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            if (y < _bodyTop || y > _bodyBottom) return;
            foreach (var chip in _chips)
                if (Visible(chip.Load.Bounds) && chip.Load.Bounds.Contains(x, y + _scroll)) { DeleteChip(chip); return; }
        }

        private void DeleteChip(TunerChip chip)
        {
            _config.SavedProfiles.Remove(chip.Profile);
            _onSave(); Game1.playSound("trashcan"); Reflow();
        }

        public override void leftClickHeld(int x, int y)
        {
            if (_dragging != null) { _dragging.SetFromX(x); _onChange(); }
            if (_draggingCompass != null) { _draggingCompass.SetFromPoint(x, y + _scroll); _onChange(); }
        }

        public override void releaseLeftClick(int x, int y)
        {
            if (_dragging != null) { _dragging = null; _onSave(); }
            if (_draggingCompass != null) { _draggingCompass = null; _onSave(); }
            base.releaseLeftClick(x, y);
        }

        protected override void cleanupBeforeExit()
        {
            _onSave();
            base.cleanupBeforeExit();
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            int innerWidth = width - Scaled(56);

            drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, drawShadow: true);
            TunerText.DrawFit(spriteBatch, _translate("tuner.title"), new Vector2(xPositionOnScreen + Scaled(28), yPositionOnScreen + Scaled(22)), innerWidth - Scaled(40), Game1.textColor, _uiScale);

            // Rail divider
            int dividerX = xPositionOnScreen + RailWidth;
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(dividerX, _bodyTop - 6, 2, _bodyBottom - _bodyTop + 12), Color.Black * 0.2f);

            // Rail buttons (active highlighted)
            foreach (var (button, tabIndex) in _tabRailButtons)
            {
                if (tabIndex == _activeTab)
                {
                    // The old highlight was a warm orange wash over a warm orange menu, which
                    // is to say it was invisible: on screen the chosen tab looked exactly like
                    // the fourteen that were not. A DARK wash reads against this panel, and the
                    // bar down the left edge says which one it is even at a glance.
                    spriteBatch.Draw(Game1.staminaRect,
                        new Rectangle(button.Bounds.X - Scaled(4), button.Bounds.Y - Scaled(2), button.Bounds.Width + Scaled(8), button.Bounds.Height + Scaled(4)),
                        new Color(72, 38, 12) * 0.34f);
                    spriteBatch.Draw(Game1.staminaRect,
                        new Rectangle(button.Bounds.X - Scaled(4), button.Bounds.Y - Scaled(2), Scaled(5), button.Bounds.Height + Scaled(4)),
                        new Color(96, 48, 14));
                }
                button.Draw(spriteBatch, 0, tabIndex == _activeTab);
                if (_icons != null && _iconScale > 0f && tabIndex < _icons.Width / IconSize)
                {
                    // Inactive tabs sit back a little so the icon column reads as a list
                    // rather than twelve competing colours.
                    float iconScale = _iconScale;
                    spriteBatch.Draw(_icons,
                        new Vector2(button.Bounds.X + Scaled(8), button.Bounds.Center.Y - IconSize * iconScale / 2f),
                        new Rectangle(tabIndex * IconSize, 0, IconSize, IconSize),
                        tabIndex == _activeTab ? Color.White : Color.White * 0.72f,
                        0f, Vector2.Zero, iconScale, SpriteEffects.None, 0.9f);
                }
            }

            // A rail longer than the window says so, or the tabs past the fold are a secret.
            if (_maxRailScroll > 0)
            {
                int barX = xPositionOnScreen + RailWidth - Scaled(10);
                int trackTop = _bodyTop - Scaled(4), trackHeight = (_bodyBottom - _bodyTop) + Scaled(8);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(barX, trackTop, Scaled(RailBarWidth), trackHeight),
                    new Color(72, 38, 12) * 0.22f);
                int shown = Tabs.Length - _maxRailScroll;
                int thumbHeight = Math.Max(Scaled(16), trackHeight * shown / Math.Max(1, Tabs.Length));
                int thumbTop = trackTop + (trackHeight - thumbHeight) * _railScroll / _maxRailScroll;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(barX, thumbTop, Scaled(RailBarWidth), thumbHeight),
                    new Color(96, 48, 14) * 0.85f);
            }

            // Clip the scrolling content to the body rect so a half-scrolled row can't draw
            // over the header/footer. Requires flushing this batch and reopening one with a
            // scissor-enabled rasterizer, then restoring a normal batch for the rest.
            var device = spriteBatch.GraphicsDevice;
            Rectangle previousScissor = device.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, _scissorRaster);
            device.ScissorRectangle = Rectangle.Intersect(device.Viewport.Bounds,
                new Rectangle(_contentX, _bodyTop, ContentWidth, _bodyBottom - _bodyTop));

            int scrollOffsetY = -_scroll;
            foreach (var (text, rowY) in _sectionTitles)
            {
                var rowBounds = new Rectangle(_contentX + Scaled(16), rowY, _contentColumnWidth, Scaled(24));
                if (Visible(rowBounds)) TunerText.DrawFit(spriteBatch, text, new Vector2(_contentX + Scaled(16), rowY + scrollOffsetY), _contentColumnWidth, Game1.textColor * 0.85f, 0.85f * _uiScale);
            }
            foreach (var (text, rowY, lineHeight) in _infoLines)
            {
                var rowBounds = new Rectangle(_contentX + Scaled(16), rowY, _contentColumnWidth, lineHeight);
                if (Visible(rowBounds)) TunerText.DrawFit(spriteBatch, text(), new Vector2(_contentX + Scaled(16), rowY + scrollOffsetY), _contentColumnWidth, Game1.textColor * 0.7f, 0.72f * _uiScale);
            }
            foreach (var button in _buttons) if (Visible(button.Bounds)) button.Draw(spriteBatch, scrollOffsetY, button.IsChosen?.Invoke() == true);
            foreach (var chip in _chips)
                if (Visible(chip.Load.Bounds))
                {
                    chip.Load.Draw(spriteBatch, scrollOffsetY, chip.Load.IsChosen?.Invoke() == true);
                    spriteBatch.Draw(Game1.mouseCursors, new Rectangle(chip.Delete.X, chip.Delete.Y + scrollOffsetY, chip.Delete.Width, chip.Delete.Height), DeleteSource, Color.White);
                }
            foreach (var toggle in _toggles) if (Visible(toggle.Row)) toggle.Draw(spriteBatch, scrollOffsetY);
            foreach (var slider in _sliders) if (Visible(slider.Track)) slider.Draw(spriteBatch, scrollOffsetY);
            foreach (var compass in _compasses) if (Visible(compass.Row)) compass.Draw(spriteBatch, scrollOffsetY);

            spriteBatch.End();
            device.ScissorRectangle = previousScissor;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullCounterClockwise);

            if (_maxScroll > 0)
            {
                int contentTrackX = xPositionOnScreen + width - 18;
                int contentTrackHeight = _bodyBottom - _bodyTop;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(contentTrackX, _bodyTop, 6, contentTrackHeight), Color.Black * 0.25f);
                int contentThumbHeight = Math.Max(30, (int)(contentTrackHeight * (float)contentTrackHeight / (contentTrackHeight + _maxScroll)));
                int contentThumbY = _bodyTop + (int)((contentTrackHeight - contentThumbHeight) * (_scroll / (float)_maxScroll));
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(contentTrackX, contentThumbY, 6, contentThumbHeight), new Color(196, 130, 66));
            }

            TunerText.DrawFit(spriteBatch, _translate("tuner.hint"), new Vector2(xPositionOnScreen + 28, _hintY), innerWidth, Game1.textColor * 0.7f, 0.8f);

            base.draw(spriteBatch);
            if (!string.IsNullOrEmpty(_hoverText))
                drawHoverText(spriteBatch, WrappedHoverText(), Game1.smallFont);
            drawMouse(spriteBatch);
        }

    }
}
