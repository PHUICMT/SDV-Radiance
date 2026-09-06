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
        private const int BaseHeaderH = 52;
        private const int BaseFooterH = 40;

        private int RailWidth = BaseRailWidth;
        private int ContentWidth = BaseContentWidth;
        private int HeaderH = BaseHeaderH;
        private int FooterH = BaseFooterH;
        /// <summary>1 at the base size, up to 1.6 on a large window. Multiplies every row
        /// height, box and text size so the whole panel grows together.</summary>
        private float _ui = 1f;
        private int S(int v) => (int)Math.Round(v * _ui);
        private const int BodyPad = 12;   // breathing room at the top/bottom of the scrolling content
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
        private readonly List<(TunerTextButton btn, int idx)> _tabRailButtons = new();  // fixed, never scroll
        private TunerSlider? _dragging;

        // Tabs: (label key, one-line description key, content builder). Remembered across reopens.
        private readonly (string key, string desc, Action build)[] _tabDefinitions;
        private static int _lastTab;

        /// <summary>Which tab the next open should land on, by the KEY of the tab rather than by
        /// its position, so inserting a tab does not silently repoint every caller. Used by the
        /// console command: the game takes input from SDL, so nothing outside the process can
        /// click a tab, and checking a change on one meant asking a person to do it.</summary>
        internal static void OpenAtTab(string keyFragment)
        {
            string[] keys =
            {
                "tuner.tab.looks", "config.section.perf", "tuner.section.colorgrade",
                "tuner.section.bloom", "tuner.tab.lens", "tuner.tab.smoothing", "tuner.section.lighting",
                "tuner.section.windows", "tuner.section.shadows", "tuner.section.godrays", "tuner.section.water",
                "tuner.section.cloudshadow", "tuner.tab.fog", "config.section.weather",
                "config.section.particles", "config.section.camera", "config.section.debug",
            };
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i].IndexOf(keyFragment, StringComparison.OrdinalIgnoreCase) >= 0)
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
            _tabDefinitions = new (string, string, Action)[]
            {
                // Ordered the way a game's video settings are: the two global answers first
                // ("make it look right", "make it run"), then the detail grouped by family -
                // camera/film, then light, then the world - and the troubleshooting switch
                // last. The old order was the order the effects happened to be built in, which
                // left the quality control that everyone needs sitting eleventh.
                ("tuner.tab.looks",       "tuner.desc.looks",      BuildLooks),
                ("config.section.perf",   "tuner.desc.perf",       BuildPerformance),
                ("tuner.section.colorgrade", "tuner.desc.colorgrade", BuildColorGrade),
                ("tuner.section.bloom",   "tuner.desc.bloom",      BuildBloom),
                ("tuner.tab.lens",        "tuner.desc.lens",       BuildLens),
                ("tuner.tab.smoothing",   "tuner.desc.smoothing",  BuildSmoothing),
                ("tuner.section.lighting", "tuner.desc.lighting",  BuildLighting),
                ("tuner.section.windows", "tuner.desc.windows",    BuildWindows),
                ("tuner.section.shadows", "tuner.desc.shadows",    BuildShadows),
                ("tuner.section.godrays", "tuner.desc.godrays",    BuildGodRays),
                ("tuner.section.water",   "tuner.desc.water",      BuildWater),
                ("tuner.section.cloudshadow", "tuner.desc.cloudshadow", BuildCloud),
                ("tuner.tab.fog",         "tuner.desc.fog",        BuildFog),
                ("config.section.weather", "tuner.desc.weather",   BuildWeather),
                ("config.section.particles", "tuner.desc.particles", BuildParticles),
                ("config.section.camera", "tuner.desc.camera",     BuildCamera),
                ("config.section.debug",  "tuner.desc.debug",      BuildDiagnostics),
            };
            _activeTab = Math.Clamp(_lastTab, 0, _tabDefinitions.Length - 1);
            Reflow();
        }

        private void Reopen() => Game1.activeClickableMenu = new RadianceTunerMenu(_config, _translate, _onChange, _onSave);

        // ---- content build helpers (append to lists, advance _contentCursorY) ----
        private void Section(string key)
        {
            // A heading over rows that are all hidden is a heading over nothing.
            if (_rowsEnabledWhen != null && !_rowsEnabledWhen())
                return;
            _sectionTitles.Add((_translate(key), _contentCursorY)); _contentCursorY += S(30);
        }
        /// <summary>The condition every row built from here on has to meet to be live. A tab sets it
        /// once around the block its master switch owns, instead of every row repeating it, and
        /// clears it after. Rows that pass their own condition ignore this.</summary>
        private Func<bool>? _rowsEnabledWhen;

        /// <summary>Everything between here and <see cref="EndDependsOn"/> is dead while this is false.</summary>
        private void DependsOn(Func<bool> condition) => _rowsEnabledWhen = condition;
        private void EndDependsOn() => _rowsEnabledWhen = null;

        private void Info(Func<string> text) { _infoLines.Add((text, _contentCursorY, S(22))); _contentCursorY += S(26); }

        /// <summary>A sentence or three, wrapped to the column at reading size. Info lines shrink
        /// to fit one row, which is right for a bench figure and wrong for a tab's description:
        /// the weather tab's Thai description came out a hairline nobody could read. The wrap is
        /// done once here, at the scale the line is drawn at, so the row is as tall as it needs.</summary>
        private void Paragraph(string text)
        {
            float textScale = 0.72f * _ui;
            string wrapped = Game1.parseText(text, Game1.smallFont, (int)(_contentColumnWidth / textScale));
            int height = (int)Math.Ceiling(TunerText.Measure(wrapped).Y * textScale);
            _infoLines.Add((() => wrapped, _contentCursorY, height));
            _contentCursorY += height + S(4);
        }
        /// <summary>Rows whose condition is false are not built at all - the section collapses
        /// instead of greying out. Asked for in exactly these words: a slider that does nothing
        /// should not be on screen. Every toggle click rebuilds the tab (see receiveLeftClick),
        /// so the rows a switch owns appear and disappear with it; the Enabled dim survives only
        /// as the fallback for a condition that changes without a rebuild.</summary>
        private void Tog(string key, Func<bool> g, Action<bool> s, string? help = null, Func<bool>? enabledWhen = null)
        {
            Func<bool>? live = enabledWhen ?? _rowsEnabledWhen;
            if (live != null && !live())
                return;
            var row = new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, S(38));
            _toggles.Add(new TunerToggle(_translate(key), row, g, s) { TextScale = _ui, Enabled = live });
            Help(row, help);
            _contentCursorY += S(44);
        }
        private void Sld(string key, float min, float max, Func<float> g, Action<float> s, string? help = null, Func<bool>? enabledWhen = null)
        {
            Func<bool>? live = enabledWhen ?? _rowsEnabledWhen;
            if (live != null && !live())
                return;
            _sliders.Add(new TunerSlider(_translate(key), _contentCursorX, _contentCursorY, _contentColumnWidth, min, max, g, s, S(26), S(20))
                { TextScale = _ui, Enabled = live });
            // The label sits above the track, so the hover area is the whole row, not the bar.
            Help(new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, S(50)), help);
            _contentCursorY += S(50);
        }

        /// <summary>Register the plain-language note for the row just laid out. Only rows that
        /// were given a key get one, so there is no guessing at whether a translation exists.</summary>
        private void Help(Rectangle row, string? key)
        {
            if (key != null)
                _help.Add((row, _translate(key)));
        }
        private TunerTextButton Btn(string label, Rectangle bounds, Action onClick)
        {
            var b = new TunerTextButton(label, bounds, onClick) { TextScale = _ui };
            _buttons.Add(b);
            return b;
        }

        private void Reflow()
        {
            int vw = Game1.uiViewport.Width;
            int vh = Game1.uiViewport.Height;

            // Take a share of the window rather than a fixed number of pixels: at the base size
            // this panel is a small box on a large display, and the rail is too narrow for a
            // full-length Thai label. Height matters as much as width here, since the rail has
            // to fit twelve tabs.
            _ui = Math.Clamp(Math.Min(vw / 1600f, vh / 900f), 1f, 1.6f);
            RailWidth = S(BaseRailWidth);
            ContentWidth = S(BaseContentWidth);
            HeaderH = S(BaseHeaderH);
            FooterH = S(BaseFooterH);

            width = RailWidth + ContentWidth;
            xPositionOnScreen = vw - width - S(24);
            yPositionOnScreen = S(20);

            _sliders.Clear(); _toggles.Clear(); _buttons.Clear(); _chips.Clear(); _sectionTitles.Clear(); _tabRailButtons.Clear(); _infoLines.Clear(); _help.Clear();
            _rowsEnabledWhen = null;   // a tab's dependency must not survive into the next one

            int contentTop = yPositionOnScreen + HeaderH;

            // ---- left rail: one button per tab, scrolled when there are more than fit ----
            // The rail is what sets the panel height, so its pitch has to be settled FIRST.
            // The pitch used to be SQUEEZED to make every tab fit at once, which worked at
            // twelve tabs and stopped working at fifteen: every button got shorter, the icons
            // shrank with them, and the rail turned into a stack of thin slivers. A list too
            // long for its window is what scrolling is for, so the pitch is fixed now and the
            // rail carries whatever it can, one whole tab at a time.
            int maxBody = (vh - S(40)) - HeaderH - FooterH;
            int tabPitch = S(NaturalTabPitch);
            int railVisibleTabs = Math.Max(1, Math.Min(_tabDefinitions.Length, maxBody / tabPitch));
            _maxRailScroll = _tabDefinitions.Length - railVisibleTabs;
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

            int railX = xPositionOnScreen + S(12);
            int railW = RailWidth - S(20);
            // Tie the icon to the button it sits in: when a short window squeezes the pitch,
            // a fixed icon size would poke out of the top and bottom of its own button.
            _iconScale = _icons != null ? Math.Min(2f * _ui, (tabPitch - S(10)) / (float)IconSize) : 0f;
            int iconInset = _icons != null ? (int)(IconSize * _iconScale) + S(10) : 0;
            // Only the tabs on screen become buttons at all, so drawing and clicking are both
            // clipped to the rail by construction rather than by a second bounds test.
            for (int i = _railScroll; i < _railScroll + railVisibleTabs; i++)
            {
                int idx = i;
                var rect = new Rectangle(railX, contentTop + (i - _railScroll) * tabPitch, railW, tabPitch - S(4));
                _tabRailButtons.Add((new TunerTextButton(_translate(_tabDefinitions[i].key), rect, () =>
                {
                    _activeTab = idx; _lastTab = idx; _scroll = 0; Reflow();
                })
                { TextScale = _ui, LeftInset = iconInset }, i));
            }

            // ---- right content column: only the active tab ----
            _contentX = xPositionOnScreen + RailWidth;
            _contentCursorX = _contentX + S(16);
            _contentColumnWidth = ContentWidth - S(40);
            _contentCursorY = contentTop + BodyPad;
            // One line saying what this tab is for, before anything else on it. A column of
            // sliders assumes the reader already knows which effect they belong to.
            Paragraph(_translate(_tabDefinitions[_activeTab].desc));
            _contentCursorY += S(6);
            _tabDefinitions[_activeTab].build();
            int contentHeight = _contentCursorY - (contentTop + BodyPad);

            // CONSISTENT panel height across tabs: the frame is sized to the tab rail, capped
            // to the view. Switching tabs never resizes the panel; a tab whose content is
            // taller than the frame scrolls inside it (mouse wheel) instead of growing it.
            int bodyHeight = railVisibleTabs * tabPitch;
            height = HeaderH + bodyHeight + FooterH;

            _bodyTop = contentTop + S(BodyPad);
            _bodyBottom = contentTop + bodyHeight - S(BodyPad);
            _hintY = yPositionOnScreen + height - S(30);
            _maxScroll = Math.Max(0, contentHeight - (_bodyBottom - _bodyTop));
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            upperRightCloseButton.bounds.X = xPositionOnScreen + width - S(40);
            upperRightCloseButton.bounds.Y = yPositionOnScreen - S(8);
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
            int bw = (_contentColumnWidth - 18) / 4;
            for (int i = 0; i < presets.Length; i++)
            {
                var (preset, key) = presets[i];
                var rect = new Rectangle(_contentCursorX + i * (bw + 6), _contentCursorY, bw, 44);
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
            foreach (var prof in _config.SavedProfiles)
            {
                int cw = Math.Min(160, 44 + (int)(Game1.smallFont.MeasureString(prof.Name).X * 0.7f));
                if (chipX + cw > _contentCursorX + _contentColumnWidth - 100) { chipX = _contentCursorX; _contentCursorY += 46; }
                var rect = new Rectangle(chipX, _contentCursorY, cw, 40);
                var captured = prof;
                var load = new TunerTextButton(prof.Name, rect, () => { _config.ApplyProfile(captured); _onChange(); _onSave(); Reflow(); });
                // Lit while the live settings are still exactly what this look holds, so the
                // panel says which saved look is in effect; move any slider and it goes out.
                load.IsChosen = () => _config.MatchesProfile(captured);
                _chips.Add(new TunerChip
                {
                    Load = load,
                    Delete = new Rectangle(rect.Right - 14, rect.Y - 6, 24, 24),
                    Profile = captured
                });
                chipX += cw + 12;
            }
            _buttons.Add(new TunerTextButton(_translate("tuner.save"), new Rectangle(_contentCursorX + _contentColumnWidth - 96, _contentCursorY, 96, 40), PromptSaveProfile));
            _contentCursorY += 52;

            Tog("tuner.master", () => _config.Enabled, v => _config.Enabled = v, "help.master");
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
            int x = _contentCursorX;
            foreach (string look in looks)
            {
                string label = LutLabel(look);
                int w = Math.Min(180, 28 + (int)(Game1.smallFont.MeasureString(label).X * 0.7f));
                if (x + w > _contentCursorX + _contentColumnWidth) { x = _contentCursorX; _contentCursorY += S(44); }
                string chosen = look;
                var button = Btn(label, new Rectangle(x, _contentCursorY, w, S(38)), () =>
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
                x += w + S(8);
            }
            _contentCursorY += S(46);
            Sld("tuner.lutamount", 0f, 1f, () => _config.ColorGradeLutAmount,
                v => _config.ColorGradeLutAmount = v, "help.lutamount");
        }

        private void BuildColorGrade()
        {
            Tog("tuner.colorgrade", () => _config.ColorGradeEnabled, v => _config.ColorGradeEnabled = v, "help.colorgrade");
            // The whole grade dies with its switch (the stage gates on it), so every row here
            // hides with it - EXCEPT the blue-light filter at the bottom, which the finishing
            // pass applies whether the grade runs or not, so it must stay on screen.
            if (_config.ColorGradeEnabled)
                BuildLutPicker();
            DependsOn(() => _config.ColorGradeEnabled);
            Tog("tuner.automood", () => _config.ColorGradeAuto, v => _config.ColorGradeAuto = v, "help.automood");
            Sld("tuner.strength", 0f, 1f, () => _config.ColorGradeStrength, v => _config.ColorGradeStrength = v);
            Sld("tuner.contrast", 0.5f, 1.5f, () => _config.ColorGradeContrast, v => _config.ColorGradeContrast = v, "help.contrast");
            Sld("tuner.saturation", 0f, 2f, () => _config.ColorGradeSaturation, v => _config.ColorGradeSaturation = v, "help.saturation");
            Sld("tuner.temperature", -1f, 1f, () => _config.ColorGradeTemperature, v => _config.ColorGradeTemperature = v, "help.temperature");
            Sld("tuner.brightness", 0.5f, 1.5f, () => _config.ColorGradeBrightness, v => _config.ColorGradeBrightness = v);
            Tog("tuner.tonemap", () => _config.ColorGradeToneMap, v => _config.ColorGradeToneMap = v, "help.tonemap");
            EndDependsOn();
            Sld("tuner.bluelight", 0f, 1f, () => _config.BlueLightFilter, v => _config.BlueLightFilter = v, "help.bluelight");
        }

        private void BuildBloom()
        {
            Tog("tuner.bloom", () => _config.BloomEnabled, v => _config.BloomEnabled = v, "help.bloom");
            // Bloom's own dials do nothing while bloom is off.
            DependsOn(() => _config.BloomEnabled);
            Sld("tuner.intensity", 0f, 2f, () => _config.BloomIntensity, v => _config.BloomIntensity = v);
            Sld("tuner.bloomthreshold", 0f, 1f, () => _config.BloomThreshold, v => _config.BloomThreshold = v, "help.bloomthreshold");
            Sld("tuner.bloomemissiveboost", 0f, 1f, () => _config.BloomEmissiveBoost, v => _config.BloomEmissiveBoost = v, "help.bloomemissiveboost");
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
                c => c.ShadowLengthTrees,        (c, v) => c.ShadowLengthTrees = v,
                c => c.ShadowSoftnessTrees,      (c, v) => c.ShadowSoftnessTrees = v,
                c => c.ShadowLeanTrees,          (c, v) => c.ShadowLeanTrees = v),
            ("tuner.shadowlength.smalltrees",
                c => c.ShadowLengthSmallTrees,   (c, v) => c.ShadowLengthSmallTrees = v,
                c => c.ShadowSoftnessSmallTrees, (c, v) => c.ShadowSoftnessSmallTrees = v,
                c => c.ShadowLeanSmallTrees,     (c, v) => c.ShadowLeanSmallTrees = v),
            ("tuner.shadowlength.bushes",
                c => c.ShadowLengthBushes,       (c, v) => c.ShadowLengthBushes = v,
                c => c.ShadowSoftnessBushes,     (c, v) => c.ShadowSoftnessBushes = v,
                c => c.ShadowLeanBushes,         (c, v) => c.ShadowLeanBushes = v),
            ("tuner.shadowlength.crops",
                c => c.ShadowLengthCrops,        (c, v) => c.ShadowLengthCrops = v,
                c => c.ShadowSoftnessCrops,      (c, v) => c.ShadowSoftnessCrops = v,
                c => c.ShadowLeanCrops,          (c, v) => c.ShadowLeanCrops = v),
            ("tuner.shadowlength.grass",
                c => c.ShadowLengthGrass,        (c, v) => c.ShadowLengthGrass = v,
                c => c.ShadowSoftnessGrass,      (c, v) => c.ShadowSoftnessGrass = v,
                c => c.ShadowLeanGrass,          (c, v) => c.ShadowLeanGrass = v),
            ("tuner.shadowlength.objects",
                c => c.ShadowLengthObjects,      (c, v) => c.ShadowLengthObjects = v,
                c => c.ShadowSoftnessObjects,    (c, v) => c.ShadowSoftnessObjects = v,
                c => c.ShadowLeanObjects,        (c, v) => c.ShadowLeanObjects = v),
            ("tuner.shadowlength.buildings",
                c => c.ShadowLengthBuildings,    (c, v) => c.ShadowLengthBuildings = v,
                c => c.ShadowSoftnessBuildings,  (c, v) => c.ShadowSoftnessBuildings = v,
                c => c.ShadowLeanBuildings,      (c, v) => c.ShadowLeanBuildings = v),
        };

        /// <summary>Which kind the shadow tab is showing dials for. Static so it survives closing
        /// the menu, because the whole point is to change one kind, go and look at it in the game,
        /// and come back to the same kind rather than hunting for it again. Not saved to config:
        /// it is a place in a menu, not a setting.</summary>
        private static int _shadowKindIndex;

        private void BuildShadows()
        {
            Tog("tuner.shadows", () => _config.DirectionalShadowsEnabled, v => _config.DirectionalShadowsEnabled = v, "help.shadows");
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
                    var rect = new Rectangle(_contentCursorX + i * (buttonWidth + 6), _contentCursorY, buttonWidth, S(40));
                    var button = Btn(_translate($"tuner.shadowmodel.{key}"), rect, () =>
                    {
                        _config.DirectionalShadowModel = model; _onChange(); _onSave(); Reflow();
                    });
                    button.IsChosen = () => _config.DirectionalShadowModel == model;
                    Help(rect, $"help.shadowmodel.{key}");
                }
                _contentCursorY += S(50);
            }
            Sld("tuner.shadowstrength", 0f, 1f, () => _config.DirectionalShadowStrength, v => _config.DirectionalShadowStrength = v);
            Sld("tuner.shadowlength", 0.2f, 2f, () => _config.DirectionalShadowLength, v => _config.DirectionalShadowLength = v, "help.shadowlength");
            Sld("tuner.goldenhour", 0f, 1f, () => _config.GoldenHourStrength, v => _config.GoldenHourStrength = v, "help.goldenhour");
            Sld("tuner.shadowblur", 0f, 5f, () => _config.DirectionalShadowBlur, v => _config.DirectionalShadowBlur = v, "help.shadowblur");
            Sld("tuner.shadowcasts", ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax,
                () => _config.ShadowCastsPerCharacter,
                v => _config.ShadowCastsPerCharacter = (int)MathF.Round(v), "help.shadowcasts");
            Tog("tuner.shadowobjects", () => _config.DirectionalShadowObjects, v => _config.DirectionalShadowObjects = v, "help.shadowobjects");
            Tog("tuner.shadowbuildings", () => _config.DirectionalShadowBuildings, v => _config.DirectionalShadowBuildings = v, "help.shadowbuildings");
            Sld("tuner.shadowgroundforeshortening", ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax,
                () => _config.ShadowGroundForeshortening, v => _config.ShadowGroundForeshortening = v, "help.shadowgroundforeshortening");
            Sld("tuner.shadowcharactergroundforeshortening", ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax,
                () => _config.ShadowCharacterGroundForeshortening, v => _config.ShadowCharacterGroundForeshortening = v, "help.shadowcharactergroundforeshortening");
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
                        _contentCursorY + row * S(46),
                        isLastAndAlone ? _contentColumnWidth : kindButtonWidth, S(40));
                    int chosenIndex = i;
                    var kindButton = Btn(_translate(ShadowKinds[i].NameKey), rect, () =>
                    {
                        // Nothing is saved by picking a kind: this moves the page, not a value.
                        _shadowKindIndex = chosenIndex; Reflow();
                    });
                    kindButton.IsChosen = () => _shadowKindIndex == chosenIndex;
                }
                _contentCursorY += (ShadowKinds.Length + kindColumns - 1) / kindColumns * S(46) + S(6);
            }
            // Clamped rather than trusted: the field is static and outlives any one menu, so a
            // shorter list in a later version would otherwise index off the end.
            var shadowKind = ShadowKinds[Math.Clamp(_shadowKindIndex, 0, ShadowKinds.Length - 1)];
            Sld("tuner.shadowkind.length", ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax,
                () => shadowKind.GetLength(_config), v => shadowKind.SetLength(_config, v));
            Sld("tuner.shadowkind.softness", ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax,
                () => shadowKind.GetSoftness(_config), v => shadowKind.SetSoftness(_config, v));
            Sld("tuner.shadowkind.lean", ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax,
                () => shadowKind.GetLean(_config), v => shadowKind.SetLean(_config, v), "help.shadowlean");
            EndDependsOn();
        }

        private void BuildLighting()
        {
            Tog("tuner.lighting", () => _config.LightingEnabled, v => _config.LightingEnabled = v, "help.lighting");
            // Every light dial below belongs to the lighting pass. Grouped by family - the
            // darkness dials, then the shadows lamps throw, then the bounced light - because
            // the old order had the lamp-shadow softness dials sitting a whole GI block away
            // from the lamp-shadow switch that owns them.
            DependsOn(() => _config.LightingEnabled);
            Sld("tuner.lightindoor", 0f, 0.95f, () => _config.LightingIndoorDarkness, v => _config.LightingIndoorDarkness = v, "help.lightindoor");
            Sld("tuner.lightnight", 0f, 0.95f, () => _config.LightingNightDarkness, v => _config.LightingNightDarkness = v, "help.lightnight");
            Sld("tuner.lightmorning", 0f, 0.95f, () => _config.LightingMorningDarkness, v => _config.LightingMorningDarkness = v, "help.lightmorning");
            Sld("tuner.lightindoorcolour", 0f, 1f, () => _config.LightingIndoorColourWalk, v => _config.LightingIndoorColourWalk = v, "help.lightindoorcolour");
            Sld("tuner.lightmorningcool", 0f, 1f, () => _config.LightingMorningClearSkyCool, v => _config.LightingMorningClearSkyCool = v, "help.lightmorningcool");
            Sld("tuner.lightwarmth", 0f, 1f, () => _config.LightingWarmth, v => _config.LightingWarmth = v, "help.lightwarmth");
            Sld("tuner.lightboost", 0f, 2f, () => _config.LightingBoost, v => _config.LightingBoost = v, "help.lightboost");
            Sld("tuner.lightradius", 0.2f, 3f, () => _config.LightingRadiusScale, v => _config.LightingRadiusScale = v, "help.lightradius");
            Section("tuner.section.lampshadows");
            Tog("tuner.lightshadows", () => _config.LightingShadows, v => _config.LightingShadows = v, "help.lightshadows");
            DependsOn(() => _config.LightingEnabled && _config.LightingShadows);
            Sld("tuner.lightshadowstrength", 0f, 1f, () => _config.LightingShadowStrength, v => _config.LightingShadowStrength = v);
            Tog("tuner.lightsilhouettes", () => _config.LightShadowSilhouettes, v => _config.LightShadowSilhouettes = v, "help.lightsilhouettes");
            Tog("tuner.lightprops", () => _config.LightShadowProps, v => _config.LightShadowProps = v, "help.lightprops");
            Sld("tuner.lightshadowcarve", 0f, 1f, () => _config.LightShadowCarve, v => _config.LightShadowCarve = v, "help.lightshadowcarve");
            Sld("tuner.lightshadowsoftness", 0f, 2f, () => _config.LightShadowSoftness, v => _config.LightShadowSoftness = v, "help.lightshadowsoftness");
            Sld("tuner.lightshadowdetail", 0f, 1f, () => _config.LightShadowDetail, v => _config.LightShadowDetail = v, "help.lightshadowdetail");
            Tog("tuner.lightshadowshared", () => _config.LightShadowDetailShared, v => _config.LightShadowDetailShared = v, "help.lightshadowshared");
            Tog("tuner.lightshadowsharp", () => _config.LightShadowSharpEdges, v => _config.LightShadowSharpEdges = v, "help.lightshadowsharp");
            DependsOn(() => _config.LightingEnabled);
            Section("tuner.section.gi");
            Tog("tuner.floodgi", () => _config.FloodLightingEnabled, v => _config.FloodLightingEnabled = v, "help.floodgi");
            DependsOn(() => _config.LightingEnabled && _config.FloodLightingEnabled);
            if (_config.LightingEnabled && _config.FloodLightingEnabled)
            {
                // Which model computes the GI map: two buttons, the one in use lit (see WaterReflectModel).
                (GiModel model, string key)[] giModels = { (GiModel.Flood, "flood"), (GiModel.Cascades, "cascades") };
                int giButtonWidth = (_contentColumnWidth - 6 * (giModels.Length - 1)) / giModels.Length;
                for (int giIndex = 0; giIndex < giModels.Length; giIndex++)
                {
                    var (model, key) = giModels[giIndex];
                    var rect = new Rectangle(_contentCursorX + giIndex * (giButtonWidth + 6), _contentCursorY, giButtonWidth, S(40));
                    var btn = Btn(_translate($"tuner.gimodel.{key}"), rect, () => { _config.FloodGiModel = model; _onChange(); _onSave(); });
                    btn.IsChosen = () => _config.FloodGiModel == model;
                    Help(rect, $"help.gimodel.{key}");
                }
                _contentCursorY += S(50);
            }
            Sld("tuner.floodstrength", 0f, 1.5f, () => _config.FloodLightingStrength, v => _config.FloodLightingStrength = v, "help.floodstrength");
            Sld("tuner.floodshadow", 0f, 1f, () => _config.FloodShadowStrength, v => _config.FloodShadowStrength = v, "help.floodshadow");
            Sld("tuner.colourbleed", 0f, 1f, () => _config.FloodColourBleed, v => _config.FloodColourBleed = v, "help.colourbleed");
            Tog("tuner.relief", () => _config.SpriteReliefEnabled, v => _config.SpriteReliefEnabled = v, "help.relief");
            Sld("tuner.reliefstrength", 0f, 1f, () => _config.SpriteReliefStrength, v => _config.SpriteReliefStrength = v, "help.reliefstrength",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Sld("tuner.reliefsun", 0f, 1f, () => _config.SpriteReliefSun, v => _config.SpriteReliefSun = v, "help.reliefsun",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Sld("tuner.reliefrim", 0f, 1f, () => _config.SpriteReliefRim, v => _config.SpriteReliefRim = v, "help.reliefrim",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            Sld("tuner.leafshimmer", 0f, 1f, () => _config.SpriteReliefLeafShimmer, v => _config.SpriteReliefLeafShimmer = v, "help.leafshimmer",
                () => _config.LightingEnabled && _config.FloodLightingEnabled && _config.SpriteReliefEnabled);
            EndDependsOn();
        }

        /// <summary>Everything the mod does with a window, on its own tab: the daylight it lets in,
        /// the beam you can see, the glow after dusk, and the people in the glass by day.</summary>
        private void BuildWindows()
        {
            Section("tuner.section.windowlight");
            Tog("tuner.windoweffects", () => _config.WindowEffectsEnabled, v => _config.WindowEffectsEnabled = v, "help.windoweffects");
            // The beam and the daylight it lays on the floor belong to the window-light master;
            // the glass rows below belong to the reflection switch. Two families, two gates.
            DependsOn(() => _config.WindowEffectsEnabled);
            Tog("tuner.windowbeam", () => _config.WindowBeamEnabled, v => _config.WindowBeamEnabled = v, "help.windowbeam");
            Sld("tuner.windowdaylightstrength", 0f, 2f, () => _config.WindowDaylightStrength,
                v => _config.WindowDaylightStrength = v, "help.windowdaylightstrength");
            Sld("tuner.windowdaylightelsewhere", 0f, 2f, () => _config.WindowDaylightStrengthElsewhere,
                v => _config.WindowDaylightStrengthElsewhere = v, "help.windowdaylightelsewhere");
            EndDependsOn();
            Section("tuner.section.windowreflection");
            Tog("tuner.windowreflection", () => _config.WindowReflectionEnabled, v => _config.WindowReflectionEnabled = v, "help.windowreflection");
            DependsOn(() => _config.WindowReflectionEnabled);
            Sld("tuner.windowreflectionstrength", 0f, 2f, () => _config.WindowReflectionStrength,
                v => _config.WindowReflectionStrength = v, "help.windowreflectionstrength");
            Sld("tuner.windowreflectionnight", 0f, 2f, () => _config.WindowReflectionNightStrength,
                v => _config.WindowReflectionNightStrength = v, "help.windowreflectionnight");
            Sld("tuner.windowsheen", 0f, 2f, () => _config.WindowSheenStrength,
                v => _config.WindowSheenStrength = v, "help.windowsheen");
            Sld("tuner.windowscene", 0f, 2f, () => _config.WindowSceneReflectionStrength,
                v => _config.WindowSceneReflectionStrength = v, "help.windowscene");
            Sld("tuner.windowglare", 0f, 2f, () => _config.WindowGlareStrength,
                v => _config.WindowGlareStrength = v, "help.windowglare");
            Sld("tuner.windowlightglow", 0f, 2f, () => _config.WindowLightGlowStrength,
                v => _config.WindowLightGlowStrength = v, "help.windowlightglow");
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
            Tog("tuner.godrays", () => _config.GodRaysEnabled, v => _config.GodRaysEnabled = v, "help.godrays");
            // The strength dial needs the shafts on.
            DependsOn(() => _config.GodRaysEnabled);
            Sld("tuner.godraysintensity", 0f, 2f, () => _config.GodRaysIntensity, v => _config.GodRaysIntensity = v);
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.godrayssun");
            Tog("tuner.godrayssun", () => _config.GodRaysSun, v => _config.GodRaysSun = v, "help.godrayssun");
            // The sun switch stands alone by design (see SetSunShaftParams) - its dials hang
            // off it, not off the lamp master above.
            DependsOn(() => _config.GodRaysSun);
            Sld("tuner.godrayssunintensity", 0f, 1.5f, () => _config.GodRaysSunIntensity,
                v => _config.GodRaysSunIntensity = v, "help.godrayssunintensity");
            Sld("tuner.godrayssunreach", 0.1f, 1f, () => _config.GodRaysSunReach,
                v => _config.GodRaysSunReach = v, "help.godrayssunreach");
            EndDependsOn();
        }

        private void BuildCloud()
        {
            Tog("tuner.cloudshadow", () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v, "help.cloudshadow");
            // Cloud shadow settings need cloud shadows.
            DependsOn(() => _config.CloudShadowEnabled);
            Tog("tuner.cloudhidevanilla", () => _config.SuppressVanillaCloudShadow, v => _config.SuppressVanillaCloudShadow = v);
            Sld("tuner.cloudcoverage", 0.1f, 0.9f, () => _config.CloudShadowCoverage, v => _config.CloudShadowCoverage = v, "help.cloudcoverage");
            Sld("tuner.cloudcount", 0f, 1f, () => _config.CloudShadowCount, v => _config.CloudShadowCount = v, "help.cloudcount");
            Sld("tuner.cloudopacity", 0f, 0.7f, () => _config.CloudShadowOpacity, v => _config.CloudShadowOpacity = v);
            Sld("tuner.cloudspeed", 0f, 0.06f, () => _config.CloudShadowSpeed, v => _config.CloudShadowSpeed = v);
            Sld("tuner.cloudscale", 1f, 5f, () => _config.CloudShadowScale, v => _config.CloudShadowScale = v, "help.cloudscale");
            EndDependsOn();
        }

        private void BuildFog()
        {
            Section("tuner.section.fog");
            Tog("tuner.fog", () => _config.FogEnabled, v => _config.FogEnabled = v, "help.fog");
            // Day fog's dials need day fog - and ONLY those. The night mist is a separate
            // effect with a separate toggle on the render side, and one DependsOn wrapped
            // around the whole tab dimmed the night rows whenever the DAY fog was off, which
            // read as "night mist is off" while it kept drawing every night.
            DependsOn(() => _config.FogEnabled);
            Sld("tuner.fogcoverage", 0f, 1f, () => _config.FogCoverage, v => _config.FogCoverage = v);
            Sld("tuner.fogdensity", 0f, 1f, () => _config.FogDensity, v => _config.FogDensity = v);
            Sld("tuner.fogspeed", 0f, 0.1f, () => _config.FogSpeed, v => _config.FogSpeed = v);
            Sld("tuner.fogscale", 1f, 8f, () => _config.FogScale, v => _config.FogScale = v, "help.fogscale");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.fognight");
            Tog("tuner.fognightmist", () => _config.FogNightMist, v => _config.FogNightMist = v, "help.fognightmist");
            DependsOn(() => _config.FogNightMist);
            Sld("tuner.fognightmistcoverage", 0f, 1f, () => _config.FogNightMistCoverage, v => _config.FogNightMistCoverage = v);
            Sld("tuner.fognightmistdensity", 0f, 1f, () => _config.FogNightMistDensity, v => _config.FogNightMistDensity = v);
            Sld("tuner.fognightmistspeed", 0f, 0.1f, () => _config.FogNightMistSpeed, v => _config.FogNightMistSpeed = v);
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.fogboth");
            // Shared by both fogs, so it goes grey only when neither is on.
            DependsOn(() => _config.FogEnabled || _config.FogNightMist);
            Sld("tuner.fogtopbias", 0f, 1f, () => _config.FogTopBias,
                v => _config.FogTopBias = v, "help.fogtopbias");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.heathaze");
            Tog("tuner.heathaze", () => _config.HeatHazeEnabled, v => _config.HeatHazeEnabled = v, "help.heathaze");
            DependsOn(() => _config.HeatHazeEnabled);
            Sld("tuner.heathazestrength", 0f, 2f, () => _config.HeatHazeStrength,
                v => _config.HeatHazeStrength = v, "help.heathazestrength");
            EndDependsOn();
        }

        private void BuildWeather()
        {
            Section("tuner.section.foliagesway");
            Tog("tuner.foliagesway", () => _config.FoliageSwayEnabled, v => _config.FoliageSwayEnabled = v, "help.foliagesway");
            DependsOn(() => _config.FoliageSwayEnabled);
            Sld("tuner.foliageswaystrength", 0f, 2f, () => _config.FoliageSwayStrength, v => _config.FoliageSwayStrength = v, "help.foliageswaystrength");
            Sld("config.weather.foliageswayspeed.name", 0.25f, 2f, () => _config.FoliageSwaySpeed,
                v => _config.FoliageSwaySpeed = v, "config.weather.foliageswayspeed.tooltip");
            Sld("config.weather.foliageswaygustspan.name", 4f, 40f, () => _config.FoliageSwayGustSpan,
                v => _config.FoliageSwayGustSpan = v, "config.weather.foliageswaygustspan.tooltip");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.sky");
            Tog("tuner.precipitation", () => _config.PrecipitationEnabled, v => _config.PrecipitationEnabled = v, "help.precipitation");
            Tog("tuner.aurora", () => _config.AuroraEnabled, v => _config.AuroraEnabled = v, "help.aurora");
            Sld("tuner.aurorastrength", 0f, 2f, () => _config.AuroraStrength,
                v => _config.AuroraStrength = v, "help.aurorastrength",
                () => _config.AuroraEnabled);
            Tog("tuner.shootingstars", () => _config.ShootingStarsEnabled, v => _config.ShootingStarsEnabled = v, "help.shootingstars");
            // Each kind of weather has its own switch under the precipitation master, and its
            // dials hang off BOTH (PrecipitationSystem asks the master and the kind together).
            if (_config.PrecipitationEnabled)
                _contentCursorY += 12;
            DependsOn(() => _config.PrecipitationEnabled);
            Section("tuner.section.precipitationrain");
            Tog("tuner.precipitationrain", () => _config.PrecipitationRain, v => _config.PrecipitationRain = v, "help.precipitationrain");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationRain);
            Sld("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationRainDensity,
                v => _config.PrecipitationRainDensity = v, "help.precipitationdensity");
            Sld("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationRainSize,
                v => _config.PrecipitationRainSize = v, "help.precipitationsize");
            Sld("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationRainOpacity,
                v => _config.PrecipitationRainOpacity = v, "help.precipitationopacity");
            Sld("tuner.precipitationstormdensity", 1f, 3f, () => _config.PrecipitationStormDensity,
                v => _config.PrecipitationStormDensity = v, "help.precipitationstormdensity");
            Sld("tuner.precipitationrainslant", 0f, 3f, () => _config.PrecipitationRainSlant,
                v => _config.PrecipitationRainSlant = v, "help.precipitationrainslant");
            DependsOn(() => _config.PrecipitationEnabled);
            if (_config.PrecipitationEnabled)
                _contentCursorY += 12;
            Section("tuner.section.precipitationsnow");
            Tog("tuner.precipitationsnow", () => _config.PrecipitationSnow, v => _config.PrecipitationSnow = v, "help.precipitationsnow");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationSnow);
            Sld("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationSnowDensity,
                v => _config.PrecipitationSnowDensity = v, "help.precipitationdensity");
            Sld("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationSnowSize,
                v => _config.PrecipitationSnowSize = v, "help.precipitationsize");
            Sld("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationSnowOpacity,
                v => _config.PrecipitationSnowOpacity = v, "help.precipitationopacity");
            DependsOn(() => _config.PrecipitationEnabled);
            if (_config.PrecipitationEnabled)
                _contentCursorY += 12;
            Section("tuner.section.precipitationwind");
            Tog("tuner.precipitationwind", () => _config.PrecipitationWind, v => _config.PrecipitationWind = v, "help.precipitationwind");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationWind);
            Sld("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationWindDensity,
                v => _config.PrecipitationWindDensity = v, "help.precipitationdensity");
            Sld("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationWindSize,
                v => _config.PrecipitationWindSize = v, "help.precipitationsize");
            Sld("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationWindOpacity,
                v => _config.PrecipitationWindOpacity = v, "help.precipitationopacity");
            Sld("tuner.precipitationwindslant", 0.25f, 3f, () => _config.PrecipitationWindSlant,
                v => _config.PrecipitationWindSlant = v, "help.precipitationwindslant");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.lightning");
            Tog("tuner.lightning", () => _config.LightningEffectsEnabled, v => _config.LightningEffectsEnabled = v, "help.lightning");
            Tog("tuner.lightningbolts", () => _config.LightningBoltsEnabled, v => _config.LightningBoltsEnabled = v, "help.lightningbolts",
                () => _config.LightningEffectsEnabled);
            _contentCursorY += 12;
            // The wet GROUND is not offered here. It is written and it works, but where
            // standing water may honestly lie is a question about the map and on a modded map
            // the answer was sometimes a roof. Until that is decided from the map rather than
            // guessed at, the whole of it stays off and out of the way; radiance_config still
            // reaches WetWorldEnabled for anyone who wants to look at it.
            Section("tuner.section.screendrops");
            Tog("tuner.wetworldlensdrops", () => _config.WetWorldLensDrops, v => _config.WetWorldLensDrops = v, "help.wetworldlensdrops");
            // The edge haze is drawn by the same pass as the drops (ScreenEdgeDrops), so it
            // goes with the drops switch too, not only the drop size.
            DependsOn(() => _config.WetWorldLensDrops);
            Sld("tuner.wetworldlensdropsize", 0.5f, 2f, () => _config.WetWorldLensDropSize,
                v => _config.WetWorldLensDropSize = v, "help.wetworldlensdropsize");
            Sld("tuner.wetworldedgehaze", 0f, 2f, () => _config.WetWorldEdgeHaze,
                v => _config.WetWorldEdgeHaze = v, "help.wetworldedgehaze");
            EndDependsOn();
        }

        private void BuildParticles()
        {
            Tog("tuner.particles", () => _config.ParticlesEnabled, v => _config.ParticlesEnabled = v, "help.particles");
            // Every particle kind below is off with the master switch.
            DependsOn(() => _config.ParticlesEnabled);
            Sld("tuner.particledensity", 0.25f, 2f, () => _config.ParticleDensity,
                v => _config.ParticleDensity = v, "help.particledensity");
            Emitter("dust", () => _config.ParticleDust, v => _config.ParticleDust = v,
                () => _config.ParticleDustAmount, v => _config.ParticleDustAmount = v,
                () => _config.ParticleDustSize, v => _config.ParticleDustSize = v);
            Emitter("embers", () => _config.ParticleEmbers, v => _config.ParticleEmbers = v,
                () => _config.ParticleEmbersAmount, v => _config.ParticleEmbersAmount = v,
                () => _config.ParticleEmbersSize, v => _config.ParticleEmbersSize = v);
            Emitter("fireflies", () => _config.ParticleFireflies, v => _config.ParticleFireflies = v,
                () => _config.ParticleFirefliesAmount, v => _config.ParticleFirefliesAmount = v,
                () => _config.ParticleFirefliesSize, v => _config.ParticleFirefliesSize = v);
            Emitter("petals", () => _config.ParticlePetals, v => _config.ParticlePetals = v,
                () => _config.ParticlePetalsAmount, v => _config.ParticlePetalsAmount = v,
                () => _config.ParticlePetalsSize, v => _config.ParticlePetalsSize = v);
            // Only the flat things buckle, so this belongs to the petals and not to the whole set.
            Sld("tuner.particlepetalsflutter", 0f, 1f, () => _config.ParticlePetalsFlutter,
                v => _config.ParticlePetalsFlutter = v, "help.particlepetalsflutter",
                () => _config.ParticlesEnabled && _config.ParticlePetals);
            Emitter("ringsparkles", () => _config.ParticleRingSparkles, v => _config.ParticleRingSparkles = v,
                () => _config.ParticleRingSparklesAmount, v => _config.ParticleRingSparklesAmount = v,
                () => _config.ParticleRingSparklesSize, v => _config.ParticleRingSparklesSize = v);
            Emitter("waterfallmist", () => _config.ParticleWaterfallMist, v => _config.ParticleWaterfallMist = v,
                () => _config.ParticleWaterfallMistAmount, v => _config.ParticleWaterfallMistAmount = v,
                () => _config.ParticleWaterfallMistSize, v => _config.ParticleWaterfallMistSize = v);
            Emitter("hotspringsteam", () => _config.ParticleHotSpringSteam, v => _config.ParticleHotSpringSteam = v,
                () => _config.ParticleHotSpringSteamAmount, v => _config.ParticleHotSpringSteamAmount = v,
                () => _config.ParticleHotSpringSteamSize, v => _config.ParticleHotSpringSteamSize = v);
            Emitter("lavasparks", () => _config.ParticleLavaSparks, v => _config.ParticleLavaSparks = v,
                () => _config.ParticleLavaSparksAmount, v => _config.ParticleLavaSparksAmount = v,
                () => _config.ParticleLavaSparksSize, v => _config.ParticleLavaSparksSize = v);
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
            Tog($"tuner.particle{emitter}", getOn, setOn, $"help.particle{emitter}");
            // Amount and size ask the emitter's own switch as well as the master (every emitter
            // multiplies both in), so they hide with either.
            Func<bool> emitterOn = () => (master == null || master()) && getOn();
            Sld("tuner.particleamount", 0f, 2f, getAmount, setAmount, "help.particleamount", emitterOn);
            Sld("tuner.particlesize", 0.5f, 2f, getSize, setSize, "help.particlesize", emitterOn);
        }

        private void BuildWater()
        {
            Tog("tuner.water", () => _config.WaterEnabled, v => _config.WaterEnabled = v, "help.water");
            // The whole water tab hangs off the water effect itself.
            DependsOn(() => _config.WaterEnabled);
            Sld("tuner.waterstrength", 0f, 2f, () => _config.WaterStrength, v => _config.WaterStrength = v, "help.waterstrength");
            Sld("tuner.watersparkle", 0f, 1f, () => _config.WaterSparkle, v => _config.WaterSparkle = v, "help.watersparkle");
            Sld("tuner.watersparkledensity", 0.2f, 2f, () => _config.WaterSparkleDensity, v => _config.WaterSparkleDensity = v);
            Tog("tuner.watercaustics", () => _config.WaterCausticsEnabled, v => _config.WaterCausticsEnabled = v, "help.watercaustics");
            Sld("tuner.watercausticsstrength", 0f, 1f, () => _config.WaterCausticsStrength, v => _config.WaterCausticsStrength = v, null,
                () => _config.WaterEnabled && _config.WaterCausticsEnabled);
            Sld("tuner.waterspeed", 0f, 3f, () => _config.WaterSpeed, v => _config.WaterSpeed = v);
            Tog("tuner.waterreflection", () => _config.WaterReflection, v => _config.WaterReflection = v, "help.waterreflection");
            // Everything from here to the rain rings is the reflection: the model, its dials,
            // blur, depth and reach all draw inside the mirror, so they go with its switch.
            bool reflectionOn = _config.WaterEnabled && _config.WaterReflection;
            DependsOn(() => _config.WaterEnabled && _config.WaterReflection);
            Sld("tuner.waterreflectstrength", 0f, 1f, () => _config.WaterReflectStrength, v => _config.WaterReflectStrength = v);
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
                int mw = (_contentColumnWidth - 6 * (waterModels.Length - 1)) / waterModels.Length;
                for (int i = 0; i < waterModels.Length; i++)
                {
                    var (model, key) = waterModels[i];
                    var rect = new Rectangle(_contentCursorX + i * (mw + 6), _contentCursorY, mw, S(40));
                    var btn = Btn(_translate($"tuner.watermodel.{key}"), rect, () =>
                    {
                        _config.WaterReflectModel = model; _onChange(); _onSave(); Reflow();
                    });
                    btn.IsChosen = () => _config.WaterReflectModel == model;
                    Help(rect, $"help.watermodel.{key}");
                }
                _contentCursorY += S(50);
            }
            if (_config.WaterReflectModel == WaterReflectionModel.Modern)
            {
                Sld("tuner.watermodernwobble", 0f, 2f, () => _config.WaterModernWobble,
                    v => _config.WaterModernWobble = v, "help.watermodernwobble");
                Sld("tuner.watermodernchoppiness", 0f, 1f, () => _config.WaterModernChoppiness,
                    v => _config.WaterModernChoppiness = v, "help.watermodernchoppiness");
                Sld("tuner.watermodernparallax", 0f, 0.3f, () => _config.WaterModernParallax,
                    v => _config.WaterModernParallax = v, "help.watermodernparallax");
                Sld("tuner.watermodernfresnel", 0f, 1f, () => _config.WaterModernFresnel,
                    v => _config.WaterModernFresnel = v, "help.watermodernfresnel");
                Sld("tuner.watermodernstretch", 1f, 1.4f, () => _config.WaterModernStretch,
                    v => _config.WaterModernStretch = v, "help.watermodernstretch");
                Sld("tuner.watermodernedgesoftness", 0f, 6f, () => _config.WaterModernEdgeSoftness,
                    v => _config.WaterModernEdgeSoftness = v, "help.watermodernedgesoftness");
                Sld("tuner.watermodernplungechurn", 0f, 1f, () => _config.WaterModernPlungeChurn,
                    v => _config.WaterModernPlungeChurn = v, "help.watermodernplungechurn");
                Sld("tuner.watermodernplungereach", 1f, 6f, () => _config.WaterModernPlungeReach,
                    v => _config.WaterModernPlungeReach = v, "help.watermodernplungereach");
                Sld("tuner.watermodernlipfade", 0f, 1.5f, () => _config.WaterModernLipFade,
                    v => _config.WaterModernLipFade = v, "help.watermodernlipfade");
            }
            else
            {
                // Three named looks rather than another slider: the things a look moves together
                // have no meaning apart, and a picked look is something a player can see the
                // point of without knowing what either number is.
                Section("tuner.reflstyle");
                if (reflectionOn)
                {
                    (WaterReflectionStyle style, string key)[] reflStyles =
                    {
                        (WaterReflectionStyle.StillWater, "still"),
                        (WaterReflectionStyle.Natural, "natural"),
                        (WaterReflectionStyle.Choppy, "choppy"),
                    };
                    int rw = (_contentColumnWidth - 6 * (reflStyles.Length - 1)) / reflStyles.Length;
                    for (int i = 0; i < reflStyles.Length; i++)
                    {
                        var (style, key) = reflStyles[i];
                        var rect = new Rectangle(_contentCursorX + i * (rw + 6), _contentCursorY, rw, S(40));
                        var btn = Btn(_translate($"tuner.reflstyle.{key}"), rect, () =>
                        {
                            _config.WaterReflectStyle = style; _onChange(); _onSave(); Reflow();
                        });
                        btn.IsChosen = () => _config.WaterReflectStyle == style;
                        Help(rect, $"help.reflstyle.{key}");
                    }
                    _contentCursorY += S(50);
                }
                Sld("tuner.waterreflectdistort", 0f, 1.5f, () => _config.WaterReflectDistort,
                    v => _config.WaterReflectDistort = v, "help.waterreflectdistort");
                Sld("tuner.waterreflectbanding", 0f, 16f, () => _config.WaterReflectBanding,
                    v => _config.WaterReflectBanding = v, "help.waterreflectbanding");
            }
            Sld("tuner.waterreflectblur", 0f, 2f, () => _config.WaterReflectBlur,
                v => _config.WaterReflectBlur = v, "help.waterreflectblur");
            Sld("tuner.reflectdepth", 0.1f, 1.5f, () => _config.WaterReflectDepth,
                v => _config.WaterReflectDepth = v, "help.reflectdepth");
            Sld("tuner.reflectreach", 0.2f, 1f, () => _config.WaterReflectReach,
                v => _config.WaterReflectReach = v, "help.reflectreach");
            DependsOn(() => _config.WaterEnabled);
            if (_config.WaterEnabled)
                _contentCursorY += 12;
            Section("tuner.section.waterrain");
            Sld("tuner.waterrainringdensity", 0f, 2f, () => _config.WaterRainRingDensity,
                v => _config.WaterRainRingDensity = v, "help.waterrainringdensity");
            Sld("tuner.waterrainringsize", 0.4f, 2f, () => _config.WaterRainRingSize,
                v => _config.WaterRainRingSize = v, "help.waterrainringsize");
            Sld("tuner.waterrainringstrength", 0f, 2f, () => _config.WaterRainRingStrength,
                v => _config.WaterRainRingStrength = v, "help.waterrainringstrength");
            // Reach and fade rows used to sit here, and they were the wrong kind of control for a
            // panel you open to look at something. Both buy frames without changing how the water
            // looks, which is exactly the setting a player moves, sees nothing, and files as
            // broken. The performance preset sets them by name instead - Quality through Low spec
            // - and radiance_config still reaches them for an A/B.
            Tog("tuner.waterindoors", () => _config.WaterEffectIndoors, v => _config.WaterEffectIndoors = v, "help.waterindoors");

            // Per-room water switch: only in gated building interiors (not outdoors / real level water).
            GameLocation? here = Game1.currentLocation;
            if (_config.WaterEnabled && here != null && !here.IsOutdoors && !RenderPipeline.HasLevelWater(here))
            {
                string key = here.NameOrUniqueName;
                _toggles.Add(new TunerToggle($"{_translate("tuner.waterhere")} · {here.Name}", new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, 38),
                    () => !_config.WaterDisabledLocations.Contains(key),
                    v =>
                    {
                        if (v) _config.WaterDisabledLocations.Remove(key);
                        else if (!_config.WaterDisabledLocations.Contains(key)) _config.WaterDisabledLocations.Add(key);
                    }));
                _contentCursorY += 44;
            }
            EndDependsOn();
        }

        private void BuildLens()
        {
            Section("tuner.section.tiltshift");
            Tog("tuner.tiltshift", () => _config.TiltShiftEnabled, v => _config.TiltShiftEnabled = v, "help.tiltshift");
            DependsOn(() => _config.TiltShiftEnabled);
            if (_config.TiltShiftEnabled)
            {
                _toggles.Add(new TunerToggle(_translate("tuner.tiltradial"), new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, 38),
                    () => _config.TiltShiftMode == TiltShiftFocus.Radial,
                    v => _config.TiltShiftMode = v ? TiltShiftFocus.Radial : TiltShiftFocus.Bands));
                _contentCursorY += 44;
            }
            // The radius is the radial focus's own; the top and bottom ratios are the bands'.
            // The shader reads one set or the other by mode, so only the set in use is shown.
            Sld("tuner.tiltradius", 0.05f, 0.9f, () => _config.TiltShiftRadius, v => _config.TiltShiftRadius = v, null,
                () => _config.TiltShiftEnabled && _config.TiltShiftMode == TiltShiftFocus.Radial);
            Sld("tuner.tilttop", 0f, 1f, () => _config.TiltShiftTopRatio, v => _config.TiltShiftTopRatio = v, "help.tilttop",
                () => _config.TiltShiftEnabled && _config.TiltShiftMode == TiltShiftFocus.Bands);
            Sld("tuner.tiltbottom", 0f, 1f, () => _config.TiltShiftBottomRatio, v => _config.TiltShiftBottomRatio = v, "help.tiltbottom",
                () => _config.TiltShiftEnabled && _config.TiltShiftMode == TiltShiftFocus.Bands);
            Sld("tuner.tiltfeather", 0f, 1f, () => _config.TiltShiftFeather, v => _config.TiltShiftFeather = v, "help.tiltfeather");
            Sld("tuner.tiltstrength", 0f, 1f, () => _config.TiltShiftStrength, v => _config.TiltShiftStrength = v);
            Sld("tuner.tiltindoor", 0f, 1f, () => _config.TiltShiftIndoorAmount, v => _config.TiltShiftIndoorAmount = v, "help.tiltindoor");
            EndDependsOn();
            _contentCursorY += 12;
            Section("tuner.section.finishing");
            Tog("tuner.vignette", () => _config.VignetteEnabled, v => _config.VignetteEnabled = v, "help.vignette");
            Sld("tuner.vignettestrength", 0f, 1f, () => _config.VignetteStrength, v => _config.VignetteStrength = v, null,
                () => _config.VignetteEnabled);
            Tog("tuner.ca", () => _config.ChromaticAberrationEnabled, v => _config.ChromaticAberrationEnabled = v, "help.ca");
            Sld("tuner.castrength", 0f, 1f, () => _config.ChromaticAberrationStrength, v => _config.ChromaticAberrationStrength = v, null,
                () => _config.ChromaticAberrationEnabled);
        }

        /// <summary>The Scale2x doubling on its own tab: the switch, how far the smoothing goes,
        /// and which of the four art families it touches. Moved out of the performance tab when
        /// it stopped being one switch - it is a look, and it is judged by eye like one.</summary>
        private void BuildSmoothing()
        {
            Tog("config.sheetupscale.name", () => _config.SheetUpscaleEnabled, v => _config.SheetUpscaleEnabled = v, "help.sheetupscale");
            DependsOn(() => _config.SheetUpscaleEnabled);
            if (_config.SheetUpscaleEnabled)
            {
                // Which look: two buttons, the one in use lit (see the GI model buttons).
                (SheetSmoothingStyle style, string key)[] styles = { (SheetSmoothingStyle.Scale2x, "scale2x"), (SheetSmoothingStyle.Soft4x, "soft4x") };
                int styleButtonWidth = (_contentColumnWidth - 6 * (styles.Length - 1)) / styles.Length;
                for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
                {
                    var (style, key) = styles[styleIndex];
                    var rect = new Rectangle(_contentCursorX + styleIndex * (styleButtonWidth + 6), _contentCursorY, styleButtonWidth, S(40));
                    var btn = Btn(_translate($"config.sheetupscalestyle.{key}"), rect, () => { _config.SheetUpscaleStyle = style; _onChange(); _onSave(); });
                    btn.IsChosen = () => _config.SheetUpscaleStyle == style;
                    Help(rect, $"help.sheetupscalestyle.{key}");
                }
                _contentCursorY += S(50);
            }
            Section("tuner.section.smoothingfamilies");
            Tog("config.sheetupscaleworld.name", () => _config.SheetUpscaleWorld,
                v => _config.SheetUpscaleWorld = v, "config.sheetupscaleworld.tooltip");
            Sld("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessWorld,
                v => _config.SheetUpscaleSmoothnessWorld = v, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleWorld);
            Tog("config.sheetupscalecharacters.name", () => _config.SheetUpscaleCharacters,
                v => _config.SheetUpscaleCharacters = v, "config.sheetupscalecharacters.tooltip");
            Sld("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessCharacters,
                v => _config.SheetUpscaleSmoothnessCharacters = v, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleCharacters);
            Tog("config.sheetupscaleitems.name", () => _config.SheetUpscaleItems,
                v => _config.SheetUpscaleItems = v, "config.sheetupscaleitems.tooltip");
            Sld("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessItems,
                v => _config.SheetUpscaleSmoothnessItems = v, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleItems);
            Tog("config.sheetupscaleportraits.name", () => _config.SheetUpscalePortraits,
                v => _config.SheetUpscalePortraits = v, "config.sheetupscaleportraits.tooltip");
            Sld("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessPortraits,
                v => _config.SheetUpscaleSmoothnessPortraits = v, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscalePortraits);
            Tog("config.sheetupscaleinterface.name", () => _config.SheetUpscaleInterface,
                v => _config.SheetUpscaleInterface = v, "config.sheetupscaleinterface.tooltip");
            Sld("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessInterface,
                v => _config.SheetUpscaleSmoothnessInterface = v, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleInterface);
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
            int pw = (_contentColumnWidth - 12) / perfPresets.Length;
            for (int i = 0; i < perfPresets.Length; i++)
            {
                var (preset, key) = perfPresets[i];
                var rect = new Rectangle(_contentCursorX + i * (pw + 6), _contentCursorY, pw, 44);
                var perfButton = new TunerTextButton(_translate($"config.perfpreset.{key}"), rect, () =>
                {
                    _config.ApplyPerfPreset(preset); _onChange(); _onSave(); Reflow();
                });
                perfButton.IsChosen = () => _config.ActivePerfPreset == preset;
                _buttons.Add(perfButton);
            }
            _contentCursorY += 56;

            Sld("config.renderscale.name", 0.5f, 1f, () => _config.RenderScale, v => _config.RenderScale = v);
            // Directly under the slider it steers, because it is that slider becoming automatic
            // rather than a separate feature, and because the slider is then read as the ceiling.
            Tog("config.renderscaleauto.name", () => _config.RenderScaleAuto,
                v => _config.RenderScaleAuto = v, "help.renderscaleauto");
            Sld("config.rendersharpness.name", 0f, 2f, () => _config.RenderSharpness, v => _config.RenderSharpness = v);

            // Neither of these is saved to config, and that is deliberate: they are instruments,
            // not settings. A diagnostic overlay that survives a restart is one somebody forgets
            // they left on, and the GPU column reaches into the graphics driver, which is not a
            // state to inherit silently from a session three days ago.
            Section("tuner.section.perfreadout");
            Tog("tuner.perfhud", () => PerfHud.Visible, v => PerfHud.Visible = v, "help.perfhud");
            Tog("tuner.gputime", () => GpuTimer.Ready, GpuTimer.SetWanted, "help.gputime");
            if (PerfHud.Visible && !GpuTimer.Ready && GpuTimer.Status != "off")
                Info(() => GpuTimer.Status);

            // Measure this machine instead of guessing at it.
            Section("config.bench.section");
            _buttons.Add(new TunerTextButton(_translate("config.bench.run"),
                new Rectangle(_contentCursorX, _contentCursorY, Math.Min(300, _contentColumnWidth), 44), () =>
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
                    new Rectangle(_contentCursorX, _contentCursorY, Math.Min(300, _contentColumnWidth), 44), () =>
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
                new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, S(38)),
                () => _config.CameraMode == CameraMode.Smooth,
                v => _config.CameraMode = v ? CameraMode.Smooth : CameraMode.Off)
            { TextScale = _ui });
            _contentCursorY += S(44);
            Sld("config.smoothcam.speed.name", 0.05f, 1f, () => _config.CameraFollowSpeed, v => _config.CameraFollowSpeed = v, null,
                () => _config.CameraMode == CameraMode.Smooth);
        }

        private void BuildDiagnostics()
        {
            Tog("config.debug.name", () => _config.DebugLogging, v => _config.DebugLogging = v);
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

        private bool Visible(Rectangle contentRect)
        {
            int top = contentRect.Y - _scroll;
            int bottom = contentRect.Bottom - _scroll;
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
            foreach (var (btn, idx) in _tabRailButtons)
                if (btn.Bounds.Contains(x, y)) { if (idx != _activeTab) Game1.playSound("smallSelect"); btn.OnClick(); return; }

            if (y >= _bodyTop && y <= _bodyBottom && x >= _contentX)
            {
                foreach (var button in _buttons)
                    if (Visible(button.Bounds) && button.Bounds.Contains(x, y + _scroll)) { Game1.playSound("smallSelect"); button.OnClick(); return; }
                foreach (var c in _chips)
                {
                    if (Visible(c.Load.Bounds) && c.Delete.Contains(x, y + _scroll)) { DeleteChip(c); return; }
                    if (Visible(c.Load.Bounds) && c.Load.Bounds.Contains(x, y + _scroll)) { Game1.playSound("smallSelect"); c.Load.OnClick(); return; }
                }
                foreach (var t in _toggles)
                    // Reflow after every toggle: the rows a switch owns are only built while it
                    // is on, so flipping it has to rebuild the tab. Safe inside the foreach
                    // because the return leaves before the enumerator moves again.
                    if (Visible(t.Row) && t.Hit(x, y + _scroll)) { t.Set(!t.Get()); Game1.playSound("drumkit6"); _onChange(); _onSave(); Reflow(); return; }
                foreach (var s in _sliders)
                    if (Visible(s.Track) && s.IsEnabled && s.Track.Contains(x, y + _scroll)) { _dragging = s; s.SetFromX(x); _onChange(); return; }
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
            foreach (var c in _chips)
                if (Visible(c.Load.Bounds) && c.Load.Bounds.Contains(x, y + _scroll)) { DeleteChip(c); return; }
        }

        private void DeleteChip(TunerChip c)
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

        public override void draw(SpriteBatch spriteBatch)
        {
            int innerW = width - S(56);

            drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, drawShadow: true);
            TunerText.DrawFit(spriteBatch, _translate("tuner.title"), new Vector2(xPositionOnScreen + S(28), yPositionOnScreen + S(22)), innerW - S(40), Game1.textColor, _ui);

            // Rail divider
            int divX = xPositionOnScreen + RailWidth;
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(divX, _bodyTop - 6, 2, _bodyBottom - _bodyTop + 12), Color.Black * 0.2f);

            // Rail buttons (active highlighted)
            foreach (var (btn, idx) in _tabRailButtons)
            {
                if (idx == _activeTab)
                {
                    // The old highlight was a warm orange wash over a warm orange menu, which
                    // is to say it was invisible: on screen the chosen tab looked exactly like
                    // the fourteen that were not. A DARK wash reads against this panel, and the
                    // bar down the left edge says which one it is even at a glance.
                    spriteBatch.Draw(Game1.staminaRect,
                        new Rectangle(btn.Bounds.X - S(4), btn.Bounds.Y - S(2), btn.Bounds.Width + S(8), btn.Bounds.Height + S(4)),
                        new Color(72, 38, 12) * 0.34f);
                    spriteBatch.Draw(Game1.staminaRect,
                        new Rectangle(btn.Bounds.X - S(4), btn.Bounds.Y - S(2), S(5), btn.Bounds.Height + S(4)),
                        new Color(96, 48, 14));
                }
                btn.Draw(spriteBatch, 0, idx == _activeTab);
                if (_icons != null && _iconScale > 0f && idx < _icons.Width / IconSize)
                {
                    // Inactive tabs sit back a little so the icon column reads as a list
                    // rather than twelve competing colours.
                    float iconScale = _iconScale;
                    spriteBatch.Draw(_icons,
                        new Vector2(btn.Bounds.X + S(8), btn.Bounds.Center.Y - IconSize * iconScale / 2f),
                        new Rectangle(idx * IconSize, 0, IconSize, IconSize),
                        idx == _activeTab ? Color.White : Color.White * 0.72f,
                        0f, Vector2.Zero, iconScale, SpriteEffects.None, 0.9f);
                }
            }

            // A rail longer than the window says so, or the tabs past the fold are a secret.
            if (_maxRailScroll > 0)
            {
                int barX = xPositionOnScreen + RailWidth - S(10);
                int trackTop = _bodyTop - S(4), trackHeight = (_bodyBottom - _bodyTop) + S(8);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(barX, trackTop, S(RailBarWidth), trackHeight),
                    new Color(72, 38, 12) * 0.22f);
                int shown = _tabDefinitions.Length - _maxRailScroll;
                int thumbHeight = Math.Max(S(16), trackHeight * shown / Math.Max(1, _tabDefinitions.Length));
                int thumbTop = trackTop + (trackHeight - thumbHeight) * _railScroll / _maxRailScroll;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(barX, thumbTop, S(RailBarWidth), thumbHeight),
                    new Color(96, 48, 14) * 0.85f);
            }

            // Clip the scrolling content to the body rect so a half-scrolled row can't draw
            // over the header/footer. Requires flushing this batch and reopening one with a
            // scissor-enabled rasterizer, then restoring a normal batch for the rest.
            var device = spriteBatch.GraphicsDevice;
            Rectangle prevScissor = device.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, _scissorRaster);
            device.ScissorRectangle = Rectangle.Intersect(device.Viewport.Bounds,
                new Rectangle(_contentX, _bodyTop, ContentWidth, _bodyBottom - _bodyTop));

            int dy = -_scroll;
            foreach (var (text, cy) in _sectionTitles)
            {
                var r = new Rectangle(_contentX + S(16), cy, _contentColumnWidth, S(24));
                if (Visible(r)) TunerText.DrawFit(spriteBatch, text, new Vector2(_contentX + S(16), cy + dy), _contentColumnWidth, Game1.textColor * 0.85f, 0.85f * _ui);
            }
            foreach (var (text, cy, lineHeight) in _infoLines)
            {
                var r = new Rectangle(_contentX + S(16), cy, _contentColumnWidth, lineHeight);
                if (Visible(r)) TunerText.DrawFit(spriteBatch, text(), new Vector2(_contentX + S(16), cy + dy), _contentColumnWidth, Game1.textColor * 0.7f, 0.72f * _ui);
            }
            foreach (var btn in _buttons) if (Visible(btn.Bounds)) btn.Draw(spriteBatch, dy, btn.IsChosen?.Invoke() == true);
            foreach (var c in _chips)
                if (Visible(c.Load.Bounds))
                {
                    c.Load.Draw(spriteBatch, dy, c.Load.IsChosen?.Invoke() == true);
                    spriteBatch.Draw(Game1.mouseCursors, new Rectangle(c.Delete.X, c.Delete.Y + dy, c.Delete.Width, c.Delete.Height), DeleteSource, Color.White);
                }
            foreach (var t in _toggles) if (Visible(t.Row)) t.Draw(spriteBatch, dy);
            foreach (var s in _sliders) if (Visible(s.Track)) s.Draw(spriteBatch, dy);

            spriteBatch.End();
            device.ScissorRectangle = prevScissor;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullCounterClockwise);

            if (_maxScroll > 0)
            {
                int trackX = xPositionOnScreen + width - 18;
                int trackH = _bodyBottom - _bodyTop;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(trackX, _bodyTop, 6, trackH), Color.Black * 0.25f);
                int barH = Math.Max(30, (int)(trackH * (float)trackH / (trackH + _maxScroll)));
                int barY = _bodyTop + (int)((trackH - barH) * (_scroll / (float)_maxScroll));
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(trackX, barY, 6, barH), new Color(196, 130, 66));
            }

            TunerText.DrawFit(spriteBatch, _translate("tuner.hint"), new Vector2(xPositionOnScreen + 28, _hintY), innerW, Game1.textColor * 0.7f, 0.8f);

            base.draw(spriteBatch);
            if (!string.IsNullOrEmpty(_hoverText))
                drawHoverText(spriteBatch, WrappedHoverText(), Game1.smallFont);
            drawMouse(spriteBatch);
        }

    }
}
