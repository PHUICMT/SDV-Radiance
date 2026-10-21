using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private const int BaseGlassContentWidth = 620;
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

        /// <summary>The live config. Not readonly for one reason: to learn a row's default, the menu
        /// points this at a fresh config for the length of one read of the row's getter (see
        /// <see cref="DefaultOf{T}"/>), since every getter reads the config through this field.</summary>
        private ModConfig _config;
        private static ModConfig? _freshConfig;
        private readonly Func<string, string> _translate;
        private readonly Action _onChange;
        private readonly Action _onSave;

        private readonly List<TunerSlider> _sliders = [];
        private readonly List<TunerCompass> _compasses = [];
        private readonly List<TunerToggle> _toggles = [];
        private readonly List<TunerTextButton> _buttons = [];   // content-area buttons (scroll with content)
        private readonly List<TunerChip> _chips = [];
        /// <summary>A heading over a group of rows. Clicking it folds the group away, and a folded
        /// group says how many rows it is holding so nothing looks missing.</summary>
        private sealed class SectionHeader(string key, string title, Rectangle row, bool folded)
        {
            public readonly string Key = key;
            public readonly string Title = title;
            public readonly Rectangle Row = row;
            public readonly bool Folded = folded;
            public int HiddenRows;
        }
        private readonly List<SectionHeader> _sectionHeaders = [];

        /// <summary>Whether the rows being built now belong to a group that is not showing: folded by
        /// the player, or fine-tuning while fine-tuning is hidden. Every row helper asks this.</summary>
        private bool _sectionRowsHidden;
        private bool _sectionIsHiddenFineTuning;
        private SectionHeader? _currentSection;
        /// <summary>Rows this tab is keeping back as fine-tuning, for the line that offers them.</summary>
        private int _fineTuningRowsHidden;
        /// <summary>Where the first row after the tab's description lands, so the first heading does
        /// not open with a gap.</summary>
        private int _contentStartY;
        /// <summary>The fine-tuning switch in the header. Fixed: it does not scroll with a tab.</summary>
        private TunerTextButton? _fineTuningButton;
        /// <summary>The header's switch to the other layout, once the player has chosen one.</summary>
        private TunerTextButton? _layoutButton;
        private const int SectionLeadBase = 10, SectionHeaderBase = 32, SectionPitchBase = 38;
        /// <summary>The headings each tab showed the last time it was built, so the fold-all button
        /// can sit above them before this build has reached them. Static so a reopen keeps it.</summary>
        private static readonly Dictionary<int, List<string>> SectionKeysByTab = [];
        /// <summary>Set while the tab is built a second time because its headings changed.</summary>
        private bool _rebuildingForSections;

        /// <summary>The heading under the pointer, lit a little so it reads as something to click.</summary>
        private SectionHeader? _hoveredSection;
        /// <summary>Read-only lines. Supplied per draw rather than baked at layout time, so a
        /// running measurement can count up without rebuilding the menu underneath it.</summary>
        private readonly List<(Func<string> text, int y, int height)> _infoLines = [];
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
        private readonly List<(Rectangle row, string text)> _help = [];
        private string? _hoverText;
        private int _seenBenchStamp = -1;

        /// <summary>Tab icons: one 16x16 cell per tab, in tab order (assets/tuner-icons.png,
        /// generated by tools/make-tuner-icons.py). Loaded once and kept - a menu that
        /// reopens constantly must not re-read a file each time.</summary>
        private static Texture2D? _icons;
        private static bool _iconsTried;
        private const int IconSize = 16;
        private float _iconScale;
        private readonly List<(TunerTextButton button, int tabIndex, int icon)> _tabRailButtons = [];  // fixed, never scroll
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
        [
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
        ];
        private static int _lastTab;

        /// <summary>While a page of the grouped layout is built: the one section of a classic tab
        /// that is wanted. A classic tab's builder runs from the top as it always does, so its
        /// master switch and the dependencies it sets still govern the rows, and every section
        /// but the wanted one is skipped the way a folded one is. Null in the classic layout, where
        /// nothing here changes anything.</summary>
        private HashSet<string>? _sectionFilter;
        /// <summary>The section being built is not the wanted one: its heading, rows, paragraphs
        /// and help are all left out.</summary>
        private bool _filterRejects;

        /// <summary>One section of a classic tab, built alone. A null section is the rows a tab has
        /// before its first heading (usually its master switch), shown under the tab's own name.</summary>
        private void BuildPiece(string tabKey, string? sectionKey)
        {
            int tab = Array.FindIndex(Tabs, candidate => candidate.Key == tabKey);
            if (tab < 0)
                return;
            _rowsEnabledWhen = null;
            EndSection();
            _sectionFilter = [sectionKey ?? tabKey];
            if (sectionKey == null)
            {
                _filterRejects = false;
                Section(tabKey);
            }
            else
                _filterRejects = true;
            try
            {
                _buildingTabKey = tabKey;
                Tabs[tab].Build(this);
            }
            finally
            {
                EndSection();
                _rowsEnabledWhen = null;
                _sectionFilter = null;
                _filterRejects = false;
            }
        }

        /// <summary>The grouped layout: eight categories by what the player sees, each in a few
        /// pages, each page made of sections of the classic tabs. Asked for on 18 Oct 2026, when
        /// seventeen tabs ordered by technique had grown past what anyone could find their way
        /// round (water split over three tabs, light over five). The classic layout stays, and
        /// both are built from the same rows, so a setting can never behave differently in one.
        /// A "*" piece is a whole tab with no filter (Looks, Performance).</summary>
        internal static readonly (string Category, int Icon, string Page, (string Tab, string? Section)[] Pieces)[] GroupedPages =
        [
            ("looks", 0, "looks", [("tuner.tab.looks", LooksPresets)]),
            ("looks", 0, "frommods", [("tuner.tab.looks", LooksFromMods)]),
            ("looks", 0, "share", [("tuner.tab.looks", LooksShare)]),
            ("light", 8, "sun", [("tuner.section.shadows", null), ("tuner.section.shadows", "tuner.shadowmodel"),
                ("tuner.section.shadows", "tuner.section.shadowsun"), ("tuner.section.shadows", "tuner.section.shadowedges"),
                ("tuner.section.shadows", "tuner.section.shadowcasters"), ("tuner.section.shadows", "tuner.section.shadowground"),
                ("tuner.section.shadows", "tuner.shadowperkind")]),
            ("light", 8, "lamps", [("tuner.section.lighting", null), ("tuner.section.lighting", "tuner.section.lightdark"),
                ("tuner.section.lighting", "tuner.section.lampshadows"), ("tuner.section.lighting", "tuner.section.lampshadowdetail"),
                ("tuner.section.godrays", "tuner.section.godrayslamps")]),
            ("light", 8, "bounce", [("tuner.section.lighting", "tuner.section.gi")]),
            ("light", 8, "windows", [("tuner.section.windows", "tuner.section.windowlight"),
                ("tuner.section.windows", "tuner.section.windowreflection"), ("tuner.section.windows", "tuner.section.windowreflectiondetail")]),
            ("light", 8, "glow", [("tuner.section.godrays", "tuner.section.godrayssun"), ("tuner.section.bloom", null)]),
            ("water", 10, "surface", [("tuner.section.water", null), ("tuner.section.water", "tuner.section.watersurface")]),
            ("water", 10, "reflection", [("tuner.section.water", "tuner.section.waterreflection"),
                ("tuner.section.water", "tuner.section.watermodeldetail"), ("tuner.section.water", "tuner.reflstyle")]),
            ("water", 10, "rivers", [("tuner.section.water", "tuner.section.watermotion"), ("tuner.section.water", "tuner.section.riverlook")]),
            ("water", 10, "rain", [("tuner.section.water", "tuner.section.waterrain")]),
            ("water", 10, "falls", [("config.section.particles", "tuner.section.particlewaterfallmist"),
                ("config.section.particles", "tuner.section.particlehotspringsteam")]),
            ("water", 10, "indoors", [("tuner.section.water", "tuner.section.waterindoors")]),
            ("weather", 13, "precip", [("config.section.weather", "tuner.section.precipitationrain"),
                ("config.section.weather", "tuner.section.precipitationsnow"), ("config.section.weather", "tuner.section.precipitationwind")]),
            ("weather", 13, "sky", [("config.section.weather", "tuner.section.sky"), ("tuner.section.cloudshadow", null)]),
            ("weather", 13, "lightning", [("config.section.weather", "tuner.section.lightning")]),
            ("weather", 13, "fog", [("tuner.tab.fog", "tuner.section.fog"), ("tuner.tab.fog", "tuner.section.fognight"),
                ("tuner.tab.fog", "tuner.section.fogboth"), ("tuner.tab.fog", "tuner.section.heathaze")]),
            ("weather", 13, "wet", [("config.section.weather", "tuner.section.wetground")]),
            ("weather", 13, "screen", [("config.section.weather", "tuner.section.screendrops")]),
            ("nature", 14, "plants", [("config.section.weather", "tuner.section.foliagesway")]),
            ("nature", 14, "living", [("config.section.particles", null), ("config.section.particles", "tuner.section.particledust"),
                ("config.section.particles", "tuner.section.particlefireflies"), ("config.section.particles", "tuner.section.particlepetals"),
                ("config.section.particles", "tuner.section.particletumbleweeds"), ("config.section.particles", "tuner.section.particlewhirlwinds"),
                ("config.section.particles", "tuner.section.particleringsparkles"), ("config.section.particles", "tuner.section.particlefootdust"),
                ("config.section.particles", "tuner.section.particlefestivelights")]),
            ("nature", 14, "fire", [("config.section.particles", "tuner.section.particleembers"),
                ("config.section.particles", "tuner.section.particlechimney"), ("config.section.particles", "tuner.section.particlelavasparks")]),
            ("picture", 2, "colour", [("tuner.section.colorgrade", null), ("tuner.section.colorgrade", "tuner.lut"),
                ("tuner.section.colorgrade", "tuner.section.gradetone"), ("tuner.section.colorgrade", "tuner.section.eyecomfort")]),
            ("picture", 2, "lens", [("tuner.tab.lens", "tuner.section.tiltshift"), ("tuner.tab.lens", "tuner.section.finishing")]),
            ("picture", 2, "pixel", [("tuner.tab.smoothing", null), ("tuner.tab.smoothing", "tuner.section.smoothingstyle"),
                ("tuner.tab.smoothing", "tuner.section.smoothingfamilies")]),
            ("picture", 2, "camera", [("config.section.camera", "*")]),
            ("perf", 1, "perf", [("config.section.perf", "*")]),
            ("menu", 16, "menu", [("config.section.debug", "*"), ("tuner.tab.looks", LooksMasterAndReset)]),
        ];

        /// <summary>The parts the Looks tab is made of, which the grouped layout spreads over three
        /// pages of their own and the Menu & help page, as on the glass mock. The classic tab builds
        /// them all, in this order.</summary>
        private const string LooksPresets = "looks:presets", LooksFromMods = "looks:frommods", LooksShare = "looks:share",
            LooksMasterAndReset = "looks:master";

        private static bool IsLooksPart(string? section) => section != null && section.StartsWith("looks:", StringComparison.Ordinal);

        private void BuildLooksPart(string part)
        {
            _rowsEnabledWhen = null;
            EndSection();
            _buildingTabKey = "tuner.tab.looks";
            switch (part)
            {
                case LooksPresets: BuildLooksPresets(); break;
                case LooksFromMods: BuildPackLooks(); break;
                case LooksShare: BuildShare(); break;
                case LooksMasterAndReset: BuildMasterAndReset(); break;
            }
            EndSection();
            _rowsEnabledWhen = null;
        }


        /// <summary>What the player typed in the tuner's search, or null. Static so it survives the
        /// text box replacing the menu and the menu being built again after it.</summary>
        private static string? _searchQuery;

        /// <summary>A row is shown during a search when its name or its help line holds the words,
        /// in the language being played in.</summary>
        private bool MatchesSearch(string key, string? help)
        {
            if (string.IsNullOrWhiteSpace(_searchQuery))
                return true;
            string query = _searchQuery.Trim();
            return _translate(key).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (help != null && _translate(help).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        private void AskForSearch()
        {
            // The glass types into its own field; only the wooden box opens a box to type in.
            if (GlassPanel && !_headerSearch.IsEmpty)
            {
                StartTypingSearch();
                return;
            }
            Game1.activeClickableMenu = new TextEntryMenu(_translate("tuner.search.box"), _searchQuery ?? "",
                onDone: typed =>
                {
                    _searchQuery = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
                    Reopen();
                },
                onCancel: Reopen);
        }

        /// <summary>The search results: every section of every page that has a matching row, under
        /// the place it lives ("Water > Reflection" in the grouped layout, the tab's name in the
        /// classic one). A section with no match leaves no trace: whatever it drew is taken back.</summary>
        private void BuildSearchResults()
        {
            _contentStartY = _contentCursorY;
            Paragraph(_translate("tuner.search.results").Replace("{{query}}", _searchQuery ?? ""));
            int gap = Scaled(8), half = (_contentColumnWidth - gap) / 2;
            void ClearSearch()
            {
                _searchQuery = null;
                _searchTypedSeen = "";
                _searchTyping?.Text = "";
                StopTypingSearch();
                Game1.playSound("smallSelect");
                _scroll = SavedScroll(_activeTab);
                Reflow();
            }
            // On the glass the field in the header is where the search is typed, so only the clear
            // button is needed under the results.
            if (GlassPanel && !_headerSearch.IsEmpty)
                Button(_translate("tuner.search.clear"), new Rectangle(_contentCursorX, _contentCursorY, half, Scaled(36)), ClearSearch);
            else
            {
                Button(_translate("tuner.search.again"), new Rectangle(_contentCursorX, _contentCursorY, half, Scaled(36)),
                    () => { Game1.playSound("smallSelect"); AskForSearch(); });
                Button(_translate("tuner.search.clear"), new Rectangle(_contentCursorX + half + gap, _contentCursorY, half, Scaled(36)), ClearSearch);
            }
            _contentCursorY += Scaled(48);
            int found = 0;
            for (int page = 0; page < GroupedPages.Length; page++)
            {
                foreach ((string tab, string? section) in GroupedPages[page].Pieces)
                {
                    int rowsBefore = _toggles.Count + _sliders.Count + _compasses.Count;
                    var mark = (Sliders: _sliders.Count, Toggles: _toggles.Count, Compasses: _compasses.Count, Buttons: _buttons.Count,
                        Chips: _chips.Count, Headers: _sectionHeaders.Count, Infos: _infoLines.Count, Helps: _help.Count, Y: _contentCursorY);
                    string where = Grouped
                        ? _translate("tuner.cat." + GroupedPages[page].Category) + " > " + _translate("tuner.page." + GroupedPages[page].Page)
                        : _translate(tab);
                    // On the glass the line saying where a result lives sits between the cards, with
                    // room above it: drawn straight under the last row it landed inside that card.
                    if (GlassPanel && found > 0)
                        _contentCursorY += Scaled(12);
                    _searchPathRows.Add(_contentCursorY);
                    Info(() => where);
                    if (section == "*")
                    {
                        _rowsEnabledWhen = null;
                        EndSection();
                        _buildingTabKey = tab;
                        Tabs[Array.FindIndex(Tabs, candidate => candidate.Key == tab)].Build(this);
                        EndSection();
                        _rowsEnabledWhen = null;
                    }
                    else if (IsLooksPart(section))
                        BuildLooksPart(section!);
                    else
                        BuildPiece(tab, section);
                    if (_toggles.Count + _sliders.Count + _compasses.Count > rowsBefore)
                    {
                        found++;
                        continue;
                    }
                    _sliders.RemoveRange(mark.Sliders, _sliders.Count - mark.Sliders);
                    _toggles.RemoveRange(mark.Toggles, _toggles.Count - mark.Toggles);
                    _compasses.RemoveRange(mark.Compasses, _compasses.Count - mark.Compasses);
                    _buttons.RemoveRange(mark.Buttons, _buttons.Count - mark.Buttons);
                    _chips.RemoveRange(mark.Chips, _chips.Count - mark.Chips);
                    _sectionHeaders.RemoveRange(mark.Headers, _sectionHeaders.Count - mark.Headers);
                    _infoLines.RemoveRange(mark.Infos, _infoLines.Count - mark.Infos);
                    _help.RemoveRange(mark.Helps, _help.Count - mark.Helps);
                    _searchPathRows.RemoveAll(row => row >= mark.Y);
                    _contentCursorY = mark.Y;
                }
            }
            if (found == 0)
                Paragraph(_translate("tuner.search.none"));
        }

        /// <summary>Where each "Category > Page" line of the search results is, so a result's card
        /// can end before the next one's line.</summary>
        private readonly List<int> _searchPathRows = [];

        internal const string LayoutGrouped = "Grouped", LayoutClassic = "Classic";
        /// <summary>The grouped layout is on. An empty choice (a player who updated and has not
        /// been asked yet) shows the classic tabs under the question.</summary>
        private bool Grouped => _config.TunerLayout == LayoutGrouped;
        private int PageCount => Grouped ? GroupedPages.Length : Tabs.Length;
        /// <summary>A page's key for the remembered tab and scroll: the classic tab's key, or the
        /// grouped page's, which can never collide with one.</summary>
        private string PageKey(int page) => Grouped ? "tuner.page." + GroupedPages[page].Page : Tabs[page].Key;
        /// <summary>The categories of the grouped layout, in order, each with its first page.</summary>
        private static readonly (string Category, int FirstPage)[] Categories =
        [
            .. GroupedPages.Select((page, index) => (page.Category, index)).GroupBy(entry => entry.Category)
                .Select(group => (group.Key, group.First().index)),
        ];
        /// <summary>The page last open in each category, so going back to a category lands where
        /// it was left rather than on its first page.</summary>
        private static readonly Dictionary<string, int> _lastPageByCategory = [];

        /// <summary>Whether a rail entry is the one in use: the tab itself, or in the grouped
        /// layout the category the open page belongs to.</summary>
        private bool RailActive(int page)
            => _searchQuery == null && (Grouped ? GroupedPages[page].Category == GroupedPages[_activeTab].Category : page == _activeTab);

        private void OpenPage(int page, bool rememberTheOpenPage = true)
        {
            _searchQuery = null;
            if (rememberTheOpenPage)
                RememberScroll();
            _lookAskedToDelete = null;
            _activeTab = page; _lastTab = page; _config.TunerLastTab = PageKey(page);
            if (Grouped)
                _lastPageByCategory[GroupedPages[page].Category] = page;
            _scroll = SavedScroll(page);
            Reflow();
        }

        /// <summary>Switch layout and stay on the same setting as far as the new layout allows: the
        /// first page that holds a section of the tab that was open, or the first page.</summary>
        private void ChooseLayout(string layout)
        {
            string wasTab = Grouped
                ? GroupedPages[_activeTab].Pieces[0].Tab
                : Tabs[_activeTab].Key;
            RememberScroll();
            _config.TunerLayout = layout;
            SectionKeysByTab.Clear();
            int page = Grouped
                ? Math.Max(0, Array.FindIndex(GroupedPages, candidate => candidate.Pieces.Any(piece => piece.Tab == wasTab)))
                : Math.Max(0, Array.FindIndex(Tabs, candidate => candidate.Key == wasTab));
            _railScroll = 0;
            _onSave();
            Game1.playSound("smallSelect");
            // The open page's scroll was kept above, under the old layout's name for it. Keeping it
            // again here read the old page's number as a page of the new layout: a grouped page past
            // the seventeenth has no classic tab, so Classic from there threw, and threw again on
            // every try to close the menu, leaving it stuck on screen.
            OpenPage(page, rememberTheOpenPage: false);
        }

        /// <summary>Build one grouped page: each piece in turn, a "*" piece being a whole tab.</summary>
        private void BuildGroupedPage(int page)
        {
            foreach ((string tab, string? section) in GroupedPages[page].Pieces)
            {
                if (section == "*")
                {
                    _rowsEnabledWhen = null;
                    EndSection();
                    if (_labelRecorder != null)
                        _probeSectionLabel = _translate(tab);
                    _buildingTabKey = tab;
                    Tabs[Array.FindIndex(Tabs, candidate => candidate.Key == tab)].Build(this);
                    EndSection();
                    _rowsEnabledWhen = null;
                }
                else if (IsLooksPart(section))
                    BuildLooksPart(section!);
                else
                    BuildPiece(tab, section);
            }
        }

        /// <summary>radiance_tuner_check: every row of the classic tabs appears in the grouped pages
        /// exactly as often, and nothing else does. Built off screen on a copy of the config with
        /// every switch on and every section open, like the share-code labels.</summary>
        internal static List<string> CheckGroupedCoverage(ModConfig config, Func<string, string> translate)
        {
            var copy = new ModConfig();
            copy.ApplyProfile(config.CaptureProfile(""));
            copy.TunerFoldedSections = [];
            copy.TunerShowFineTuning = true;
            // The classic pass builds the seventeen tabs, which Reflow only does in the classic layout.
            copy.TunerLayout = LayoutClassic;
            foreach (PropertyInfo property in typeof(ModConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.PropertyType == typeof(bool) && property.CanWrite && property.CanRead)
                    property.SetValue(copy, true);
            var probe = new RadianceTunerMenu(copy, translate, () => { }, () => { }, slideIn: false);
            // A tab seen for the first time is built twice, once to learn its headings, and both
            // builds were recorded: the classic count came out doubled whenever the tabs had not
            // been opened yet this run. One pass to learn them first, then the counted one.
            for (int tab = 0; tab < Tabs.Length; tab++)
            {
                probe._activeTab = tab;
                probe.Reflow();
            }
            probe._labelRecorder = [];
            for (int tab = 0; tab < Tabs.Length; tab++)
            {
                probe._activeTab = tab;
                probe._probeSectionLabel = translate(Tabs[tab].Key);
                probe.Reflow();
            }
            var classic = probe._labelRecorder.Select(row => row.Label).ToList();
            probe._labelRecorder = [];
            for (int page = 0; page < GroupedPages.Length; page++)
            {
                probe._probeSectionLabel = "";
                probe.BuildGroupedPage(page);
            }
            var grouped = probe._labelRecorder.Select(row => row.Label).ToList();
            var report = new List<string> { $"classic rows {classic.Count}, grouped rows {grouped.Count}" };
            var left = grouped.GroupBy(label => label).ToDictionary(group => group.Key, group => group.Count());
            foreach (string label in classic)
            {
                if (left.TryGetValue(label, out int count) && count > 0)
                    left[label] = count - 1;
                else
                    report.Add("missing from grouped: " + label);
            }
            foreach ((string label, int count) in left)
                for (int i = 0; i < count; i++)
                    report.Add("extra in grouped: " + label);
            return report;
        }
        /// <summary>Whether <see cref="_lastTab"/> has been set since the game started. Until it has, the tab comes
        /// from config, so the menu opens where it was left even after the game was closed.</summary>
        private static bool _lastTabKnown;

        /// <summary>Which tab the next open should land on, by the KEY of the tab rather than by
        /// its position, so inserting a tab does not silently repoint every caller. Used by the
        /// console command: the game takes input from SDL, so nothing outside the process can
        /// click a tab, and checking a change on one meant asking a person to do it.</summary>
        internal static void OpenAtTab(string keyFragment)
        {
            // "search:words" opens on the search results, for scripts that cannot type into the box.
            if (keyFragment.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                _searchQuery = keyFragment["search:".Length..].Replace('_', ' ');
                return;
            }
            // A fragment naming a grouped page ("page.windows") opens that page in the grouped layout.
            for (int i = 0; i < GroupedPages.Length; i++)
            {
                if (("tuner.page." + GroupedPages[i].Page).Equals(keyFragment, StringComparison.OrdinalIgnoreCase)
                    || ("page." + GroupedPages[i].Page).Equals(keyFragment, StringComparison.OrdinalIgnoreCase))
                {
                    _lastTab = i;
                    _lastTabKnown = true;
                    return;
                }
            }
            for (int i = 0; i < Tabs.Length; i++)
            {
                if (Tabs[i].Key.IndexOf(keyFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _lastTab = i;
                    _lastTabKnown = true;
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

        public RadianceTunerMenu(ModConfig config, Func<string, string> translate, Action onChange, Action onSave,
            bool slideIn = true)
            : base(0, 0, PanelWidth, 0, showUpperRightCloseButton: true)
        {
            _slide = slideIn ? 0f : 1f;
            _config = config;
            _translate = translate;
            _onChange = onChange;
            _onSave = onSave;
            if (slideIn)
                _settingsByCategory = null;
            if (!_lastTabKnown)
            {
                int saved = Enumerable.Range(0, PageCount).FirstOrDefault(page => PageKey(page) == config.TunerLastTab, -1);
                _lastTab = saved >= 0 ? saved : 0;
                _lastTabKnown = true;
            }
            _activeTab = Math.Clamp(_lastTab, 0, PageCount - 1);
            _scroll = SavedScroll(_activeTab);
            Reflow();
        }

        // A rebuild of the menu already on screen, so it does not slide in again.
        private void Reopen() => Game1.activeClickableMenu = new RadianceTunerMenu(_config, _translate, _onChange, _onSave, slideIn: false);

        // ---- sliding in from the nearer side edge on opening, and back out on closing ----

        /// <summary>0 is off the screen's nearer side edge, 1 is in place.</summary>
        private float _slide;
        private bool _closing;
        private const float OpenSlideSeconds = 0.22f;
        private const float CloseSlideSeconds = 0.18f;

        /// <summary>Whether the panel is still on its way in or out. Clicks, the wheel and hover are
        /// ignored until it lands, so nothing is changed by a click aimed at where a row is going to be.</summary>
        private bool Sliding => _closing || _slide < 1f;

        /// <summary>How far to the side of its place the panel is drawn: out past whichever edge of the
        /// screen it sits nearer (the right one, where the panel opens), so it never sweeps across the
        /// view. A cubic ease, so it arrives slowly and leaves slowly at first.</summary>
        private float SlideOffsetX()
        {
            if (_slide >= 1f)
                return 0f;
            float remaining = 1f - Math.Clamp(_slide, 0f, 1f);
            float eased = remaining * remaining * remaining;
            bool nearerRight = xPositionOnScreen + width / 2 > Game1.uiViewport.Width / 2;
            return nearerRight
                ? MathF.Round((Game1.uiViewport.Width - xPositionOnScreen) * eased)
                : -MathF.Round((xPositionOnScreen + width) * eased);
        }

        /// <summary>Close with the slide out. The menu is removed when it has left the screen; F6,
        /// Escape, the menu key and the close button all come here. Anything the game closes on its
        /// own (an event, a warp) still closes at once.</summary>
        internal void SlideClosed()
        {
            if (_closing)
                return;
            _closing = true;
            Game1.playSound("bigDeSelect");
        }

        public override void receiveKeyPress(Microsoft.Xna.Framework.Input.Keys key)
        {
            // While the search field is typed in, the letters are the search, not the game's keys:
            // E would close the menu halfway through "shadow". Enter puts the field down and keeps
            // the results; Escape empties the search first and puts the field down.
            if (TypingSearch)
            {
                if (key == Microsoft.Xna.Framework.Input.Keys.Enter)
                    StopTypingSearch();
                else if (key == Microsoft.Xna.Framework.Input.Keys.Escape)
                {
                    StopTypingSearch();
                    if (_searchQuery != null)
                    {
                        _searchQuery = null;
                        _scroll = SavedScroll(_activeTab);
                        Reflow();
                    }
                }
                return;
            }
            if (key == Microsoft.Xna.Framework.Input.Keys.Escape
                || (key != Microsoft.Xna.Framework.Input.Keys.None && Game1.options.doesInputListContain(Game1.options.menuButton, key)))
            {
                SlideClosed();
                return;
            }
            base.receiveKeyPress(key);
        }

        /// <summary>Where this tab was scrolled to last time, in pixels; Reflow clamps it to the tab as
        /// it is now, so a tab that got shorter (a group folded) never opens past its end.</summary>
        private int SavedScroll(int tab)
            => _config.TunerScrollByTab.TryGetValue(PageKey(tab), out int scrolled) ? Math.Max(0, scrolled) : 0;

        /// <summary>Keep where the open page is scrolled to. A page number the layout does not have
        /// is skipped rather than thrown on: this runs on the way out of the menu, and an error there
        /// leaves the menu on screen with no way to close it.</summary>
        private void RememberScroll()
        {
            if (_activeTab >= 0 && _activeTab < PageCount)
                _config.TunerScrollByTab[PageKey(_activeTab)] = _scroll;
        }

        private void ShowFineTuning()
        {
            _config.TunerShowFineTuning = true;
            _onSave();
            Reflow();
        }

        // ---- content build helpers (append to lists, advance _contentCursorY) ----
        /// <summary>A heading, and the start of a group that runs until the next heading or
        /// <see cref="EndSection"/>. A <paramref name="fineTuning"/> group is the dials most players
        /// never need: it stays out of the way, heading and all, until the header switch shows it.</summary>
        private void Section(string key, bool fineTuning = false)
        {
            if (_sectionFilter != null)
            {
                _filterRejects = !_sectionFilter.Contains(key);
                if (_filterRejects)
                {
                    EndSection();
                    return;
                }
            }
            if (_labelRecorder != null)
                _probeSectionLabel = _translate(key);
            EndSection();
            // A heading over rows that are all hidden is a heading over nothing.
            if (_rowsEnabledWhen != null && !_rowsEnabledWhen())
                return;
            if (fineTuning && !_config.TunerShowFineTuning && _searchQuery == null)
            {
                _sectionRowsHidden = true;
                _sectionIsHiddenFineTuning = true;
                return;
            }
            if (_contentCursorY > _contentStartY)
                _contentCursorY += Scaled(SectionLeadBase);
            var header = new SectionHeader(key, _translate(key),
                new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(SectionHeaderBase)),
                _config.TunerFoldedSections.Contains(key) && _searchQuery == null);
            _sectionHeaders.Add(header);
            _currentSection = header;
            _sectionRowsHidden = header.Folded && _searchQuery == null;
            _contentCursorY += Scaled(SectionPitchBase);
        }

        /// <summary>Close the current group, so the rows after it show whatever state it was in.</summary>
        private void EndSection()
        {
            _sectionRowsHidden = false;
            _sectionIsHiddenFineTuning = false;
            _currentSection = null;
        }

        /// <summary>Whether the row about to be built is held back by its group, counting it there so
        /// a folded heading and the fine-tuning line can say how many rows they hold.</summary>
        private bool HiddenBySection(bool countsAsRow = true)
        {
            if (_filterRejects)
                return true;
            if (!_sectionRowsHidden)
                return false;
            if (countsAsRow)
            {
                if (_sectionIsHiddenFineTuning)
                    _fineTuningRowsHidden++;
                else if (_currentSection != null)
                    _currentSection.HiddenRows++;
            }
            return true;
        }

        /// <summary>What a row reads under a fresh config: its default. Null where the getter will not
        /// answer, and for a getter that does not read the config at all this is just its live value,
        /// which is never marked as changed.</summary>
        private T? DefaultOf<T>(Func<T> read) where T : struct
        {
            ModConfig live = _config;
            _config = _freshConfig ??= new ModConfig();
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                _config = live;
            }
        }
        /// <summary>The condition every row built from here on has to meet to be live. A tab sets it
        /// once around the block its master switch owns, instead of every row repeating it, and
        /// clears it after. Rows that pass their own condition ignore this.</summary>
        private Func<bool>? _rowsEnabledWhen;

        /// <summary>Everything between here and <see cref="EndDependsOn"/> is dead while this is false.</summary>
        private void DependsOn(Func<bool> condition) => _rowsEnabledWhen = condition;
        private void EndDependsOn() => _rowsEnabledWhen = null;

        private void Info(Func<string> text)
        {
            if (HiddenBySection(countsAsRow: false))
                return;
            _infoLines.Add((text, _contentCursorY, Scaled(22))); _contentCursorY += Scaled(26);
        }

        /// <summary>A sentence or three, wrapped to the column at reading size. Info lines shrink
        /// to fit one row, which is right for a bench figure and wrong for a tab's description:
        /// the weather tab's Thai description came out a hairline nobody could read. The wrap is
        /// done once here, at the scale the line is drawn at, so the row is as tall as it needs.</summary>
        private void Paragraph(string text)
        {
            if (HiddenBySection(countsAsRow: false))
                return;
            float textScale = (GlassPanel ? 0.8f : 0.72f) * _uiScale;
            string wrapped = TunerText.Wrap(text, _contentColumnWidth, textScale);
            int height = (int)Math.Ceiling(TunerText.Measure(wrapped).Y * textScale);
            _infoLines.Add((() => wrapped, _contentCursorY, height));
            _contentCursorY += height + Scaled(4);
        }
        /// <summary>Rows whose condition is false are not built at all - the section collapses
        /// instead of greying out. Asked for in exactly these words: a slider that does nothing
        /// should not be on screen. Every toggle click rebuilds the tab (see receiveLeftClick),
        /// so the rows a switch owns appear and disappear with it; the Enabled dim survives only
        /// as the fallback for a condition that changes without a rebuild.</summary>
        /// <param name="savedSetting">False for a switch that is not a setting at all (an instrument
        /// such as the GPU timer): it gets no changed mark and no reset. Its "default" was read from
        /// live state rather than from a fresh config, so it was whatever the instrument happened to
        /// be at the rebuild, and the mark and the reset followed that moment.</param>
        private void Toggle(string key, Func<bool> getValue, Action<bool> setValue, string? help = null, Func<bool>? enabledWhen = null,
            bool savedSetting = true)
        {
            if (!_filterRejects)
                _labelRecorder?.Add((ProbeLabel(key), () => getValue()));
            Func<bool>? rowEnabledWhen = enabledWhen ?? _rowsEnabledWhen;
            if (rowEnabledWhen != null && !rowEnabledWhen())
                return;
            if (HiddenBySection())
                return;
            if (!MatchesSearch(key, help))
                return;
            var row = new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(GlassPanel ? 32 : 38));
            _toggles.Add(new TunerToggle(_translate(key), row, getValue, setValue)
                { TextScale = _uiScale, Enabled = rowEnabledWhen, DefaultValue = savedSetting ? DefaultOf(getValue) : null });
            Help(row, help);
            _contentCursorY += Scaled(GlassPanel ? 34 : 44);
        }
        /// <summary>A labelled slider row. <paramref name="step"/> is the smallest move it makes,
        /// and defaults to a hundredth, which suits a dial that runs 0 to 1 or wider; a dial whose
        /// whole range is a fraction of that has to say so or it gets a handful of positions.</summary>
        private void Slider(string key, float min, float max, Func<float> getValue, Action<float> setValue,
            string? help = null, Func<bool>? enabledWhen = null, float step = 0.01f)
        {
            if (!_filterRejects)
                _labelRecorder?.Add((ProbeLabel(key), () => getValue()));
            Func<bool>? rowEnabledWhen = enabledWhen ?? _rowsEnabledWhen;
            if (rowEnabledWhen != null && !rowEnabledWhen())
                return;
            if (HiddenBySection())
                return;
            if (!MatchesSearch(key, help))
                return;
            if (GlassPanel)
            {
                // One line on the glass: the label left, the track in the right half, the value at
                // the end, as on the glass mock.
                int trackX = _contentCursorX + _contentColumnWidth * 52 / 100;
                int rowRight = _contentCursorX + _contentColumnWidth - Scaled(8);
                int trackWidth = rowRight - Scaled(48) - trackX;
                _sliders.Add(new TunerSlider(_translate(key), trackX, _contentCursorY + Scaled(6), trackWidth, min, max, getValue, setValue, 0, Scaled(18))
                {
                    TextScale = _uiScale, Enabled = rowEnabledWhen, Step = step, DefaultValue = DefaultOf(getValue),
                    Inline = true, InlineLabelX = _contentCursorX + Scaled(10), InlineRowRight = rowRight,
                });
                Help(new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(32)), help);
                _contentCursorY += Scaled(34);
                return;
            }
            _sliders.Add(new TunerSlider(_translate(key), _contentCursorX, _contentCursorY, _contentColumnWidth, min, max, getValue, setValue, Scaled(26), Scaled(20))
                { TextScale = _uiScale, Enabled = rowEnabledWhen, Step = step, DefaultValue = DefaultOf(getValue) });
            // The label sits above the track, so the hover area is the whole row, not the bar.
            Help(new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(50)), help);
            _contentCursorY += Scaled(50);
        }

        /// <summary>A round bearing dial. Taller than a slider because it is a picture of the
        /// answer rather than a number, which is what makes it worth the room.</summary>
        private void Compass(string key, Func<float> getDegrees, Action<float> setDegrees, string? help = null, Func<bool>? enabledWhen = null)
        {
            if (!_filterRejects)
                _labelRecorder?.Add((ProbeLabel(key), () => getDegrees()));
            Func<bool>? rowEnabledWhen = enabledWhen ?? _rowsEnabledWhen;
            if (rowEnabledWhen != null && !rowEnabledWhen())
                return;
            if (HiddenBySection())
                return;
            if (!MatchesSearch(key, help))
                return;
            var compass = new TunerCompass(_translate(key), _contentCursorX, _contentCursorY, _contentColumnWidth,
                Scaled(26), Scaled(120), getDegrees, setDegrees)
            { TextScale = _uiScale, Enabled = rowEnabledWhen, DefaultValue = DefaultOf(getDegrees) };
            _compasses.Add(compass);
            Help(compass.Row, help);
            _contentCursorY += compass.Row.Height + Scaled(10);
        }

        /// <summary>Register the plain-language note for the row just laid out. Only rows that
        /// were given a key get one, so there is no guessing at whether a translation exists.</summary>
        private void Help(Rectangle row, string? key)
        {
            string? picture = PictureFor(_currentSection?.Key ?? _buildingTabKey);
            if (picture != null)
                _pictureOfRow[row] = picture;
            if (key == null)
                return;
            string text = _translate(key);
            if (SuggestedByHelpKey.TryGetValue(key, out string? suggested))
                text += " " + SuggestionText(suggested);
            _help.Add((row, text));
        }

        // ---- the help bar: what the pointed-at setting does, with a picture of its section ----

        /// <summary>The classic tab being built, for the rows that come before its first heading.</summary>
        private string? _buildingTabKey;

        /// <summary>Which picture each row's section has, by the row's rectangle.</summary>
        private readonly Dictionary<Rectangle, string> _pictureOfRow = [];

        /// <summary>The picture of the row or heading under the pointer, or null.</summary>
        private string? _hoverPicture;

        /// <summary>
        /// The pictures, by section: the same moment with the game alone and with this effect on
        /// (assets/tuner-pictures, made by tools/make-tuner-pictures.py from shots taken in game).
        /// A setting's words say what it changes; the pair shows it, which is what the help bar
        /// was asked for on 18 Oct 2026 (the author could not tell which effect a section was
        /// about from its name). A section with no pair shows its words alone.
        /// </summary>
        /// <remarks>Only pairs that show their own section's effect at the help bar's size are listed.
        /// The first set reused a few scenes across many sections (one night street for bloom,
        /// fireflies, lamp rays and the winter tree lights; one lake for sun rays and waterfall mist)
        /// and the author found most of them had nothing to do with the section (21 Oct 2026).</remarks>
        private static readonly Dictionary<string, string> PictureOfSection = new(StringComparer.Ordinal)
        {
            ["tuner.section.colorgrade"] = "colorgrade", ["tuner.lut"] = "colorgrade", ["tuner.section.gradetone"] = "colorgrade",
            ["tuner.section.water"] = "water", ["tuner.section.watersurface"] = "water",
            ["tuner.section.sky"] = "aurora", ["tuner.section.heathaze"] = "heathaze",
            ["tuner.section.particlelavasparks"] = "lava", ["tuner.section.precipitationsnow"] = "snow",
        };

        private static string? PictureFor(string? sectionKey)
            => sectionKey != null && PictureOfSection.TryGetValue(sectionKey, out string? picture) ? picture : null;

        /// <summary>Each pair loaded once on first sight and kept until the game closes; a missing file
        /// is remembered as missing, not looked for again every frame.</summary>
        private static readonly Dictionary<string, (Texture2D? Game, Texture2D? Effect)> _pictures = [];

        private static (Texture2D? Game, Texture2D? Effect) PicturePair(string name)
        {
            if (!_pictures.TryGetValue(name, out var pair))
            {
                pair = (RenderPipeline.Current?.LoadTexture($"tuner-pictures/{name}-a.png"),
                        RenderPipeline.Current?.LoadTexture($"tuner-pictures/{name}-b.png"));
                _pictures[name] = pair;
            }
            return pair;
        }

        private static readonly Dictionary<string, Texture2D?> _presetPictures = [];

        /// <summary>The same spot in the game under each preset, for its card on the glass
        /// (assets/tuner-pictures/preset-*.png, taken in game).</summary>
        private static Texture2D? PresetPicture(string key)
        {
            if (!_presetPictures.TryGetValue(key, out Texture2D? picture))
                _presetPictures[key] = picture = RenderPipeline.Current?.LoadTexture($"tuner-pictures/preset-{key}.png");
            return picture;
        }

        /// <summary>How tall the help bar is, and the gap above it.</summary>
        private int HelpBarHeight => Scaled(128);
        private int HelpBarGap => Scaled(8);

        /// <summary>
        /// The help bar under the list: the words of the setting under the pointer, the line about
        /// putting it back when it is changed, and its section's picture, game alone and with the
        /// effect in turn. It took over from the note that floated by the pointer, which covered the
        /// rows next to the one being read.
        /// </summary>
        private void DrawHelpBar(SpriteBatch spriteBatch)
        {
            // On the glass the bar lines up with the cards above it and is a card itself, with no
            // brown rule along its top: that rule belongs to the wooden box.
            var bar = GlassPanel
                ? new Rectangle(_contentCursorX - Scaled(6), _bodyBottom + HelpBarGap, _contentColumnWidth + Scaled(12), HelpBarHeight)
                : new Rectangle(_contentX, _bodyBottom + HelpBarGap, ContentWidth - Scaled(12), HelpBarHeight);
            if (GlassPanel)
            {
                TunerTheme.Plate(spriteBatch, bar, Scaled(14), Color.Black * (TunerTheme.Light ? 0.06f : 0.22f));
                TunerTheme.PlateRim(spriteBatch, bar, Scaled(14), TunerTheme.Rim);
            }
            else
            {
                spriteBatch.Draw(Game1.staminaRect, bar, TunerTheme.Wash * 0.08f);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(bar.X, bar.Y, bar.Width, Math.Max(1, Scaled(2))), new Color(96, 58, 26) * 0.35f);
            }
            int pad = Scaled(8);
            int textLeft = bar.X + pad;
            string? picture = _hoverPicture;
            var (game, effect) = picture != null ? PicturePair(picture) : (null, null);
            if (game != null && effect != null)
            {
                int pictureHeight = bar.Height - pad * 2;
                int pictureWidth = pictureHeight * 16 / 9;
                var frame = new Rectangle(bar.X + pad, bar.Y + pad, pictureWidth, pictureHeight);
                // Game alone, then with the effect, a second and a half each: the difference is the
                // point, and two pictures side by side at this size would each be too small to see it.
                bool showEffect = (Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0) % 3.0 >= 1.5;
                spriteBatch.Draw(showEffect ? effect : game, frame, Color.White);
                string caption = _translate(showEffect ? "tuner.picture.effect" : "tuner.picture.game");
                float captionScale = (TunerTheme.Glass ? 0.68f : 0.62f) * _uiScale;
                Vector2 captionSize = TunerText.Measure(caption) * captionScale;
                var captionBox = new Rectangle(frame.X, frame.Bottom - (int)captionSize.Y - Scaled(6), (int)captionSize.X + Scaled(10), (int)captionSize.Y + Scaled(6));
                spriteBatch.Draw(Game1.staminaRect, captionBox, Color.Black * 0.55f);
                TunerText.Draw(spriteBatch, caption, new Vector2(captionBox.X + Scaled(5), captionBox.Y + Scaled(3)), Color.White, captionScale, shadowIntensity: 0f);
                textLeft = frame.Right + pad * 2;
            }
            int textWidth = bar.Right - pad - textLeft;
            string words = string.IsNullOrEmpty(_hoverText) ? _translate("tuner.helpbar.idle") : _hoverText!;
            float textScale = (TunerTheme.Glass ? 0.82f : 0.72f) * _uiScale;
            if (!ReferenceEquals(_helpBarWrappedFrom, words) || _helpBarWrappedWidth != textWidth)
            {
                _helpBarWrappedFrom = words;
                _helpBarWrappedWidth = textWidth;
                _helpBarWrapped = TunerText.Wrap(words, textWidth, textScale);
            }
            string[] lines = _helpBarWrapped!.Split('\n');
            // Spaced by the tallest line as drawn: Thai is in the game's font, taller than the glass
            // typeface's "Ag", and the lines of a long Thai note were drawn over each other.
            int lineHeight = (int)(lines.Append("Ag").Max(text => TunerText.Measure(text).Y) * textScale);
            int maxLines = Math.Max(1, (bar.Height - pad * 2) / Math.Max(1, lineHeight));
            for (int line = 0; line < Math.Min(lines.Length, maxLines); line++)
            {
                string text = line == maxLines - 1 && lines.Length > maxLines ? lines[line].TrimEnd() + "..." : lines[line];
                TunerText.Draw(spriteBatch, text, new Vector2(textLeft, bar.Y + pad + line * lineHeight),
                    TunerTheme.Ink * (string.IsNullOrEmpty(_hoverText) ? 0.6f : 1f), textScale, shadowIntensity: 0.4f);
            }
        }

        /// <summary>A thin cross in a box, for closing and deleting on the glass: the mock's own
        /// cross from the icon sheet, smooth at any size, or two bars if the sheet is missing.</summary>
        private static void DrawGlassCross(SpriteBatch spriteBatch, Rectangle box, Color ink)
        {
            if (GlassIcons() is { } icons && GlassIconNamed("close") is int close)
            {
                int iconSize = (int)(Math.Min(box.Width, box.Height) * 0.62f);
                spriteBatch.Draw(icons, new Rectangle(box.Center.X - iconSize / 2, box.Center.Y - iconSize / 2, iconSize, iconSize),
                    GlassIconSource(close, iconSize), ink);
                return;
            }
            int arm = Math.Max(4, Math.Min(box.Width, box.Height) / 3);
            int thickness = Math.Max(2, arm / 4);
            var centre = box.Center.ToVector2();
            foreach (float angle in new[] { MathF.PI / 4f, -MathF.PI / 4f })
                spriteBatch.Draw(Game1.staminaRect, centre, null, ink, angle, new Vector2(0.5f, 0.5f),
                    new Vector2(arm * 2, thickness), SpriteEffects.None, 0f);
        }

        private string? _helpBarWrappedFrom, _helpBarWrapped;
        private int _helpBarWrappedWidth;

        /// <summary>Where the page's content ends, for the last card on the glass.</summary>
        private int _contentEndY;

        /// <summary>The page's name in large type on the glass, and where it sits.</summary>
        private (string Text, int Y)? _pageTitle;

        /// <summary>
        /// On the glass, each group of settings sits on its own card, its heading in gold with a
        /// small picture of the effect at the right, and the page's name above everything in large
        /// type: the layout of the glass mock. The cards are worked out from where the headings
        /// are, each running to just above the next one.
        /// </summary>
        private void DrawGlassCards(SpriteBatch spriteBatch, int scrollOffsetY)
        {
            if (_pageTitle is { } title)
                TunerText.DrawFit(spriteBatch, title.Text, new Vector2(_contentCursorX, title.Y + scrollOffsetY),
                    _contentColumnWidth, TunerTheme.Ink, 1.2f * _uiScale);
            for (int i = 0; i < _sectionHeaders.Count; i++)
            {
                SectionHeader header = _sectionHeaders[i];
                int top = header.Row.Y - Scaled(6);
                int bottom = i + 1 < _sectionHeaders.Count ? _sectionHeaders[i + 1].Row.Y - Scaled(12) : _contentEndY;
                // A search result's card ends before the line naming where the next result lives.
                foreach (int pathRow in _searchPathRows)
                    if (pathRow > header.Row.Y && pathRow - Scaled(4) < bottom)
                        bottom = pathRow - Scaled(4);
                var card = new Rectangle(header.Row.X - Scaled(6), top + scrollOffsetY, header.Row.Width + Scaled(12), Math.Max(header.Row.Height, bottom - top));
                if (card.Bottom < _bodyTop || card.Y > _bodyBottom)
                    continue;
                TunerTheme.Plate(spriteBatch, card, Scaled(14), TunerTheme.Overlay * 0.045f);
                TunerTheme.PlateRim(spriteBatch, card, Scaled(14), TunerTheme.Overlay * 0.07f);
                string? picture = PictureFor(header.Key);
                Texture2D? effect = picture != null ? PicturePair(picture).Effect : null;
                if (effect != null)
                {
                    int thumbHeight = header.Row.Height - Scaled(8);
                    var thumb = new Rectangle(header.Row.Right - Scaled(8) - thumbHeight * 16 / 9, header.Row.Y + Scaled(4) + scrollOffsetY,
                        thumbHeight * 16 / 9, thumbHeight);
                    spriteBatch.Draw(effect, thumb, Color.White * 0.9f);
                }
            }
        }

        /// <summary>
        /// What the author suggests for settings that ship off only because they are new: a new
        /// feature never turns itself on in an update, so a player who updates finds it off and
        /// has no way to tell it was meant to be tried. Asked for by Charost on Nexus (17 Oct 2026).
        /// Keyed by the row's help key; "on" for a switch, a number for a dial, "" for the choice
        /// button that is the suggested one. Taste settings (colour grade, fog, smooth art) are
        /// left out on purpose: there is no right answer to suggest.
        /// </summary>
        private static readonly Dictionary<string, string> SuggestedByHelpKey = new()
        {
            ["help.wetworld"] = "on",
            ["help.wetworlddrylamplight"] = "0.50",
            ["help.lightningwindows"] = "on",
            ["help.windowlightshape.glass"] = "",
            ["help.particletumbleweeds"] = "on",
            ["help.particlewhirlwinds"] = "on",
            ["help.windyspells"] = "on",
            ["help.shadowgrounded"] = "on",
            ["help.contactshadow"] = "1.00",
            ["help.sunseason"] = "0.30",
        };

        private string SuggestionText(string suggested) => suggested switch
        {
            "" => _translate("tuner.suggested.choice"),
            "on" => _translate("tuner.suggested.on"),
            _ => _translate("tuner.suggested.value").Replace("{{value}}", suggested),
        };
        private TunerTextButton Button(string label, Rectangle bounds, Action onClick)
        {
            var button = new TunerTextButton(label, bounds, onClick) { TextScale = _uiScale };
            _buttons.Add(button);
            return button;
        }

        /// <summary>Smooth on the glass, where the typeface and the line icons are drawn scaled;
        /// sharp on the wooden box, where everything is the game's pixel art.</summary>
        private static SamplerState TextSampler => TunerTheme.Glass ? SamplerState.LinearClamp : SamplerState.PointClamp;

        /// <summary>The line icons of the glass mock (assets/tuner-glass-icons.png, made by
        /// tools/make-tuner-glass-icons.py), one 48 px cell each, in this order.</summary>
        private static readonly string[] GlassIconNames =
        [
            "star", "sun", "wave", "cloud", "leaf", "frame", "gauge", "gear", "snow", "wind", "shadow", "person", "tune", "petal",
            "dust", "tumble", "plus", "moon", "lamp", "window", "bounce", "rays", "mirror", "river", "drops", "fall", "house", "bolt",
            "fog", "puddle", "screen", "firefly", "flame", "palette", "lens", "pixel", "camera", "bars", "user", "share",
            "search", "chevron-down", "chevron-right", "close",
        ];

        /// <summary>The sheet holds every icon once per size, a row each, largest first.</summary>
        private static readonly int[] GlassIconSizes = [48, 40, 32, 28, 24, 20, 16];

        /// <summary>An icon's cell in the row nearest the size it is drawn at, from the next size up,
        /// so the graphics card only ever shrinks it a little: a 48 pixel icon squeezed to 20 came
        /// out jagged.</summary>
        internal static Rectangle GlassIconSource(int icon, int drawnSize)
        {
            int rowTop = 0, chosenTop = 0, chosenSize = GlassIconSizes[0];
            foreach (int size in GlassIconSizes)
            {
                if (size >= drawnSize)
                {
                    chosenSize = size;
                    chosenTop = rowTop;
                }
                rowTop += size;
            }
            return new Rectangle(icon * chosenSize, chosenTop, chosenSize, chosenSize);
        }
        private static Texture2D? _glassIcons;
        private static bool _glassIconsTried;

        private static Texture2D? GlassIcons()
        {
            if (!_glassIconsTried)
            {
                _glassIconsTried = true;
                _glassIcons = RenderPipeline.Current?.LoadTexture("tuner-glass-icons.png");
            }
            return _glassIcons;
        }

        /// <summary>Each category's icon on the glass, as on the mock.</summary>
        private static int? GlassIconOf(string category)
        {
            string? name = category switch
            {
                "looks" => "star", "light" => "sun", "water" => "wave", "weather" => "cloud", "nature" => "leaf",
                "picture" => "frame", "perf" => "gauge", "menu" => "gear", _ => null,
            };
            return name == null ? null : GlassIconNamed(name);
        }

        /// <summary>Each page's icon on its chip, as on the mock.</summary>
        private static string? PageIconName(string page) => page switch
        {
            "looks" => "star", "frommods" => "share", "share" => "share", "sun" => "sun", "lamps" => "lamp", "bounce" => "bounce", "windows" => "window", "glow" => "rays",
            "surface" => "wave", "reflection" => "mirror", "rivers" => "river", "rain" => "drops", "falls" => "fall", "indoors" => "house",
            "precip" => "cloud", "sky" => "moon", "lightning" => "bolt", "fog" => "fog", "wet" => "puddle", "screen" => "screen",
            "plants" => "leaf", "living" => "firefly", "fire" => "flame", "colour" => "palette", "lens" => "lens", "pixel" => "pixel",
            "camera" => "camera", "perf" => "gauge", "menu" => "gear", _ => null,
        };

        /// <summary>The icon before a section's heading on the glass, as on the mock; a section not
        /// named here takes its page's icon.</summary>
        private static readonly Dictionary<string, string> SectionIconNames = new()
        {
            ["tuner.section.precipitationrain"] = "drops", ["tuner.section.precipitationsnow"] = "snow", ["tuner.section.precipitationwind"] = "wind",
            ["tuner.section.sky"] = "moon", ["tuner.section.cloudshadow"] = "cloud", ["tuner.section.lightning"] = "bolt",
            ["tuner.section.fog"] = "fog", ["tuner.section.fognight"] = "moon", ["tuner.section.fogboth"] = "tune", ["tuner.section.heathaze"] = "flame",
            ["tuner.section.wetground"] = "puddle", ["tuner.section.screendrops"] = "screen",
            ["tuner.section.shadows"] = "shadow", ["tuner.shadowmodel"] = "shadow", ["tuner.section.shadowsun"] = "sun", ["tuner.section.shadowedges"] = "tune",
            ["tuner.section.shadowcasters"] = "person", ["tuner.section.shadowground"] = "shadow", ["tuner.shadowperkind"] = "tune",
            ["tuner.section.lighting"] = "lamp", ["tuner.section.lightdark"] = "moon", ["tuner.section.lampshadows"] = "shadow",
            ["tuner.section.lampshadowdetail"] = "tune", ["tuner.section.godrayslamps"] = "rays", ["tuner.section.gi"] = "bounce",
            ["tuner.section.windowlight"] = "window", ["tuner.section.windowreflection"] = "mirror", ["tuner.section.windowreflectiondetail"] = "tune",
            ["tuner.section.godrayssun"] = "rays", ["tuner.section.bloom"] = "sun",
            ["tuner.section.water"] = "wave", ["tuner.section.watersurface"] = "wave", ["tuner.section.waterreflection"] = "mirror",
            ["tuner.section.watermodeldetail"] = "tune", ["tuner.reflstyle"] = "mirror", ["tuner.section.watermotion"] = "river",
            ["tuner.section.riverlook"] = "river", ["tuner.section.waterrain"] = "drops", ["tuner.section.particlewaterfallmist"] = "fall",
            ["tuner.section.particlehotspringsteam"] = "fog", ["tuner.section.waterindoors"] = "house",
            ["tuner.section.foliagesway"] = "leaf", ["config.section.particles"] = "tune", ["tuner.section.particledust"] = "dust",
            ["tuner.section.particlefireflies"] = "firefly", ["tuner.section.particlepetals"] = "petal", ["tuner.section.particletumbleweeds"] = "tumble",
            ["tuner.section.particlewhirlwinds"] = "wind", ["tuner.section.particleringsparkles"] = "rays", ["tuner.section.particlefootdust"] = "dust",
            ["tuner.section.particlefestivelights"] = "lamp", ["tuner.section.particleembers"] = "flame", ["tuner.section.particlechimney"] = "house",
            ["tuner.section.particlelavasparks"] = "flame",
        };

        private int? SectionIcon(string key)
            => SectionIconNames.TryGetValue(key, out string? name) ? GlassIconNamed(name)
                : Grouped && _searchQuery == null && PageIconName(GroupedPages[_activeTab].Page) is string pageIcon ? GlassIconNamed(pageIcon)
                : null;

        /// <summary>How many settings each category holds, shown beside it on the glass rail as on
        /// the mock. Counted when the menu opens and when Simple or All is picked, by building every
        /// page in a probe with every group open, the way <see cref="LabelsBySetting"/> reads labels.</summary>
        private static Dictionary<string, int>? _settingsByCategory;
        private static bool _countingSettings;

        private Dictionary<string, int>? SettingsByCategory()
        {
            if (_settingsByCategory != null || _countingSettings)
                return _settingsByCategory;
            _countingSettings = true;
            string? query = _searchQuery;
            _searchQuery = null;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var copy = new ModConfig();
                copy.ApplyProfile(_config.CaptureProfile(""));
                copy.TunerFoldedSections = [];
                // As the player has Simple or All, so switching between them moves the counts.
                copy.TunerShowFineTuning = _config.TunerShowFineTuning;
                copy.TunerLayout = LayoutGrouped;
                // The same panel as the menu on screen: the probe's builds set the shared theme.
                copy.TunerPanel = _config.TunerPanel;
                var probe = new RadianceTunerMenu(copy, _translate, () => { }, () => { }, slideIn: false);
                for (int page = 0; page < GroupedPages.Length; page++)
                {
                    probe._activeTab = page;
                    probe.Reflow();
                    string category = GroupedPages[page].Category;
                    counts[category] = counts.GetValueOrDefault(category) + probe._toggles.Count + probe._sliders.Count + probe._compasses.Count;
                }
            }
            finally
            {
                _searchQuery = query;
                _countingSettings = false;
            }
            return _settingsByCategory = counts;
        }

        private static int? GlassIconNamed(string name)
        {
            int index = Array.IndexOf(GlassIconNames, name);
            return index < 0 ? null : index;
        }

        /// <summary>One side of a two-way switch in the glass header.</summary>
        private readonly record struct PillOption(Rectangle Bounds, string Label, Func<bool> IsChosen, Action Pick);

        /// <summary>The glass header's switches (Simple or All, New menu or Classic), each a whole
        /// pill and its two sides, and the search field beside them, as on the glass mock.</summary>
        private readonly List<(Rectangle Whole, PillOption[] Options)> _headerPills = [];
        private Rectangle _headerSearch;

        private float HeaderTextScale => 0.8f * _uiScale;

        private void BuildGlassHeader()
        {
            int top = yPositionOnScreen + Scaled(14), height = Scaled(34), gap = Scaled(10);
            int right = xPositionOnScreen + width - Scaled(18) - height - gap;
            if (!string.IsNullOrEmpty(_config.TunerLayout))
                right = AddHeaderPill(
                [
                    (_translate("tuner.layout.new"), () => Grouped, () => ChooseLayout(LayoutGrouped)),
                    (_translate("tuner.layout.old"), () => !Grouped, () => ChooseLayout(LayoutClassic)),
                ], right, top, height) - gap;
            right = AddHeaderPill(
            [
                (_translate("tuner.mode.simple"), () => !_config.TunerShowFineTuning, () => SetFineTuning(false)),
                (_translate("tuner.mode.all"), () => _config.TunerShowFineTuning, () => SetFineTuning(true)),
            ], right, top, height) - gap;
            int left = xPositionOnScreen + RailWidth + Scaled(16);
            // Too narrow a window leaves no room for a field worth the name; the search then sits
            // at the top of the page as it does on the wooden box.
            _headerSearch = right - left >= Scaled(140) ? new Rectangle(left, top, right - left, height) : Rectangle.Empty;
        }

        /// <summary>The space between the buttons of a row of choices: wider on the glass, where each
        /// is its own rounded plate and six pixels ran them together.</summary>
        private int ChoiceGap => GlassPanel ? Scaled(10) : 6;

        private int AddHeaderPill((string Label, Func<bool> IsChosen, Action Pick)[] options, int right, int top, int height)
        {
            int padding = Scaled(14), inset = Scaled(3);
            int[] widths = [.. options.Select(option => (int)(TunerText.Measure(option.Label).X * HeaderTextScale) + padding * 2)];
            int left = right - widths.Sum() - inset * 2;
            var pieces = new PillOption[options.Length];
            int x = left + inset;
            for (int i = 0; i < options.Length; i++)
            {
                pieces[i] = new PillOption(new Rectangle(x, top + inset, widths[i], height - inset * 2), options[i].Label, options[i].IsChosen, options[i].Pick);
                x += widths[i];
            }
            _headerPills.Add((new Rectangle(left, top, right - left, height), pieces));
            return left;
        }

        private void SetFineTuning(bool shown)
        {
            _config.TunerShowFineTuning = shown;
            _settingsByCategory = null;
            _onSave();
            Reflow();
        }

        /// <summary>
        /// The glass header's search field takes the typing itself, as on the mock: a click puts the
        /// caret in it, it stretches over the Simple and All switch while it is in use, and every
        /// letter shows its results at once. The game's own text box does the typing (so a Thai
        /// keyboard and pasting work as anywhere else in the game) and is never drawn; the field
        /// draws its text. The wooden box keeps the typing box it always had.
        /// </summary>
        private TextBox? _searchTyping;
        private string _searchTypedSeen = "";
        /// <summary>0 at rest, 1 stretched: eased each frame so the field grows and shrinks.</summary>
        private float _searchStretch;

        private bool TypingSearch => _searchTyping != null && Game1.keyboardDispatcher.Subscriber == _searchTyping;

        private void StartTypingSearch()
        {
            _searchTyping ??= new TextBox(null, null, Game1.smallFont, TunerTheme.Ink) { limitWidth = false };
            _searchTyping.Text = _searchQuery ?? "";
            _searchTypedSeen = _searchTyping.Text;
            _searchTyping.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _searchTyping;
        }

        private void StopTypingSearch()
        {
            if (_searchTyping == null)
                return;
            _searchTyping.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == _searchTyping)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        /// <summary>Run each frame: the typed words become the search the moment they change.</summary>
        private void FollowTypedSearch(float seconds)
        {
            float target = TypingSearch ? 1f : 0f;
            _searchStretch = target > _searchStretch
                ? Math.Min(target, _searchStretch + seconds / 0.18f)
                : Math.Max(target, _searchStretch - seconds / 0.18f);
            if (_searchTyping == null || !TypingSearch || _searchTyping.Text == _searchTypedSeen)
                return;
            _searchTypedSeen = _searchTyping.Text;
            string typed = _searchTypedSeen.Trim();
            _searchQuery = typed.Length == 0 ? null : typed;
            _scroll = 0;
            Reflow();
        }

        /// <summary>The field as drawn and clicked: stretched over the Simple and All switch, eased.</summary>
        private Rectangle SearchFieldNow()
        {
            if (_headerSearch.IsEmpty || _searchStretch <= 0f || _headerPills.Count == 0)
                return _headerSearch;
            int stretchTo = _headerPills[^1].Whole.Right;
            float eased = _searchStretch * _searchStretch * (3f - 2f * _searchStretch);
            return new Rectangle(_headerSearch.X, _headerSearch.Y,
                _headerSearch.Width + (int)((stretchTo - _headerSearch.Right) * eased), _headerSearch.Height);
        }

        private void DrawGlassHeader(SpriteBatch spriteBatch)
        {
            int mouseX = Game1.getMouseX(), mouseY = Game1.getMouseY();
            int rowMiddle = yPositionOnScreen + Scaled(14) + Scaled(34) / 2;
            float brandScale = 1.05f * _uiScale;
            var brandAt = new Vector2(xPositionOnScreen + Scaled(26), rowMiddle - TunerText.Measure("A").Y * brandScale / 2f + 2f);
            TunerText.Draw(spriteBatch, "Radiance", brandAt, TunerTheme.Ink, brandScale);
            float smallScale = 0.7f * _uiScale;
            TunerText.Draw(spriteBatch, "F6", new Vector2(brandAt.X + TunerText.Measure("Radiance").X * brandScale + Scaled(6),
                rowMiddle - TunerText.Measure("A").Y * smallScale / 2f + 2f), TunerTheme.InkFaint, smallScale);

            float capHeight = TunerText.Measure("A").Y * HeaderTextScale;
            Rectangle field = SearchFieldNow();
            for (int pill = 0; pill < _headerPills.Count; pill++)
            {
                var (whole, options) = _headerPills[pill];
                // The Simple and All switch fades under the field while it stretches over it.
                float shown = pill == _headerPills.Count - 1 ? 1f - _searchStretch : 1f;
                if (shown <= 0.02f)
                    continue;
                TunerTheme.Plate(spriteBatch, whole, whole.Height / 2, TunerTheme.Overlay * 0.07f);
                TunerTheme.PlateRim(spriteBatch, whole, whole.Height / 2, TunerTheme.Rim);
                foreach (PillOption option in options)
                {
                    bool chosen = option.IsChosen();
                    if (chosen)
                        TunerTheme.Plate(spriteBatch, option.Bounds, option.Bounds.Height / 2, TunerTheme.Accent);
                    else if (option.Bounds.Contains(mouseX, mouseY))
                        TunerTheme.Plate(spriteBatch, option.Bounds, option.Bounds.Height / 2, TunerTheme.Overlay * 0.08f);
                    float labelWidth = TunerText.Measure(option.Label).X * HeaderTextScale;
                    TunerText.Draw(spriteBatch, option.Label, new Vector2(option.Bounds.Center.X - labelWidth / 2f, option.Bounds.Center.Y - capHeight / 2f + 2f),
                        (chosen ? TunerTheme.AccentInk : TunerTheme.InkDim) * shown, HeaderTextScale);
                }
            }

            if (!field.IsEmpty)
            {
                // Over the pills it stretches across, so the field reads as one solid thing.
                int radius = field.Height / 2;
                bool typing = TypingSearch;
                TunerTheme.Plate(spriteBatch, field, radius, TunerTheme.PanelGlass * (0.9f * _searchStretch));
                TunerTheme.Plate(spriteBatch, field, radius, TunerTheme.Overlay * (typing ? 0.1f : field.Contains(mouseX, mouseY) ? 0.12f : 0.07f));
                TunerTheme.PlateRim(spriteBatch, field, radius, Color.Lerp(TunerTheme.Rim, TunerTheme.Accent, _searchStretch));
                int iconSize = Scaled(18);
                if (GlassIcons() is { } icons && GlassIconNamed("search") is int search)
                    spriteBatch.Draw(icons, new Rectangle(field.X + Scaled(12), field.Center.Y - iconSize / 2, iconSize, iconSize),
                        GlassIconSource(search, iconSize), Color.Lerp(TunerTheme.InkFaint, TunerTheme.Accent, _searchStretch));
                int textX = field.X + Scaled(20) + iconSize;
                float room = field.Right - Scaled(16) - textX;
                string typed = typing ? _searchTyping!.Text : _searchQuery ?? "";
                if (typed.Length == 0)
                    TunerText.DrawFit(spriteBatch, _translate("tuner.search.box"), new Vector2(textX, field.Center.Y - capHeight / 2f + 2f),
                        room, TunerTheme.InkFaint, HeaderTextScale);
                else
                {
                    // The end of a long search stays in view, the way any text field scrolls.
                    string shownText = typed;
                    while (shownText.Length > 1 && TunerText.Measure(shownText).X * HeaderTextScale > room)
                        shownText = shownText[1..];
                    float textHeight = TunerText.Measure(shownText).Y * HeaderTextScale;
                    TunerText.Draw(spriteBatch, shownText, new Vector2(textX, field.Center.Y - Math.Max(capHeight, textHeight) / 2f + 2f), TunerTheme.Ink, HeaderTextScale);
                    typed = shownText;
                }
                bool caretOn = (Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0.0) % 1000.0 < 550.0;
                if (typing && caretOn)
                {
                    float caretX = textX + (typed.Length == 0 ? 0f : TunerText.Measure(typed).X * HeaderTextScale) + Scaled(2);
                    spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)caretX, field.Y + Scaled(8), Math.Max(2, Scaled(2)), field.Height - Scaled(16)), TunerTheme.Accent);
                }
            }
        }

        private void Reflow()
        {
            TunerTheme.Glass = GlassPanel;
            TunerTheme.Light = LightGlass;
            int viewportWidth = Game1.uiViewport.Width;
            int viewportHeight = Game1.uiViewport.Height;

            // Take a share of the window rather than a fixed number of pixels: at the base size
            // this panel is a small box on a large display, and the rail is too narrow for a
            // full-length Thai label. Height matters as much as width here, since the rail has
            // to fit twelve tabs.
            _uiScale = Math.Clamp(Math.Min(viewportWidth / 1600f, viewportHeight / 900f), 1f, 1.6f);
            RailWidth = Scaled(BaseRailWidth);
            // The glass carries the search and two switches in its header, as on the mock, and is
            // wider to make room for them, short of running off a small window.
            ContentWidth = GlassPanel
                ? Math.Min(Scaled(BaseGlassContentWidth), Math.Max(Scaled(BaseContentWidth), viewportWidth - RailWidth - Scaled(48)))
                : Scaled(BaseContentWidth);
            HeaderHeight = Scaled(BaseHeaderHeight);
            FooterHeight = Scaled(BaseFooterHeight);

            width = RailWidth + ContentWidth;
            xPositionOnScreen = viewportWidth - width - Scaled(24);
            yPositionOnScreen = Scaled(20);

            _sliders.Clear(); _compasses.Clear(); _toggles.Clear(); _buttons.Clear(); _chips.Clear(); _sectionHeaders.Clear(); _tabRailButtons.Clear(); _infoLines.Clear(); _help.Clear(); _pictureOfRow.Clear();
            _searchPathRows.Clear();
            _rowsEnabledWhen = null;   // a tab's dependency must not survive into the next one
            EndSection();
            _fineTuningRowsHidden = 0;
            _headerPills.Clear();
            _headerSearch = Rectangle.Empty;
            if (GlassPanel)
                BuildGlassHeader();

            int contentTop = yPositionOnScreen + HeaderHeight;

            // ---- left rail: one button per tab, scrolled when there are more than fit ----
            // The rail is what sets the panel height, so its pitch has to be settled FIRST.
            // The pitch used to be SQUEEZED to make every tab fit at once, which worked at
            // twelve tabs and stopped working at fifteen: every button got shorter, the icons
            // shrank with them, and the rail turned into a stack of thin slivers. A list too
            // long for its window is what scrolling is for, so the pitch is fixed now and the
            // rail carries whatever it can, one whole tab at a time.
            int maxBody = viewportHeight - Scaled(40) - HeaderHeight - FooterHeight;
            int tabPitch = Scaled(NaturalTabPitch);
            // The panel is the height of the classic rail in either layout, so switching layout
            // never resizes it; the grouped rail is eight entries and simply has room to spare.
            int railVisibleTabs = Math.Max(1, Math.Min(Tabs.Length, maxBody / tabPitch));
            int railEntries = Grouped ? Categories.Length : Tabs.Length;
            int railShown = Math.Min(railEntries, railVisibleTabs);
            int activeRailEntry = Grouped ? Array.FindIndex(Categories, entry => entry.Category == GroupedPages[_activeTab].Category) : _activeTab;
            _maxRailScroll = railEntries - railShown;
            // Never leave the chosen tab off the end of what is showing: opening the menu on a
            // tab near the bottom, or being sent to one by name, has to bring it into view.
            _railScroll = Math.Clamp(_railScroll, 0, _maxRailScroll);
            if (activeRailEntry < _railScroll)
                _railScroll = activeRailEntry;
            else if (activeRailEntry >= _railScroll + railShown)
                _railScroll = activeRailEntry - railShown + 1;

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
            for (int i = _railScroll; i < _railScroll + railShown; i++)
            {
                var rect = new Rectangle(railButtonX, contentTop + (i - _railScroll) * tabPitch, railButtonWidth, tabPitch - Scaled(4));
                if (Grouped)
                {
                    (string category, int firstPage) = Categories[i];
                    int target = _lastPageByCategory.TryGetValue(category, out int remembered) ? remembered : firstPage;
                    _tabRailButtons.Add((new TunerTextButton(_translate("tuner.cat." + category), rect, () => OpenPage(target))
                        { TextScale = _uiScale, LeftInset = iconInset, RightInset = GlassPanel ? Scaled(26) : 0, RailEntry = true }, target, GroupedPages[firstPage].Icon));
                }
                else
                {
                    int tabIndex = i;
                    _tabRailButtons.Add((new TunerTextButton(_translate(Tabs[i].Key), rect, () => OpenPage(tabIndex))
                        { TextScale = _uiScale, LeftInset = iconInset, RailEntry = true }, i, i));
                }
            }

            // ---- right content column: only the active tab ----
            _contentX = xPositionOnScreen + RailWidth;
            _contentCursorX = _contentX + Scaled(16);
            _contentColumnWidth = ContentWidth - Scaled(40);
            // The same padding the scrolling column's top edge is placed with (_bodyTop below): the
            // unscaled one started the content above that edge on a large window, and the marks
            // over a Thai first line (the tone mark on the Water page's title) were cut off.
            _contentCursorY = contentTop + Scaled(BodyPadding) + Scaled(4);
            // One line saying what this tab is for, before anything else on it. A column of
            // sliders assumes the reader already knows which effect they belong to.
            PlaceLayoutPrompt();
            // Cleared before a search jumps past it: the page's big title stayed from the page
            // before and was drawn over the results.
            _pageTitle = null;
            if (_searchQuery != null)
                goto Search;
            // The search sits at the top of every page as a wide bar rather than in the header,
            // which had room for two buttons and the title, not three (the title vanished).
            if (_headerSearch.IsEmpty)
            {
                Button(_translate("tuner.search.box"), new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(36)), () =>
                {
                    Game1.playSound("smallSelect");
                    AskForSearch();
                });
                _contentCursorY += Scaled(46);
            }
            // On the glass the page opens with its name in large type over the line about it.
            _pageTitle = null;
            if (GlassPanel)
            {
                _pageTitle = (_translate(Grouped ? "tuner.cat." + GroupedPages[_activeTab].Category : Tabs[_activeTab].Key), _contentCursorY);
                _contentCursorY += Scaled(34);
            }
            Paragraph(_translate(Grouped ? "tuner.cat." + GroupedPages[_activeTab].Category + ".desc" : Tabs[_activeTab].DescriptionKey));
            _contentCursorY += Scaled(6);
            if (Grouped)
                BuildPageChips();
            // Fold or open every group on this tab at once. The headings come from the last build of
            // the tab; a tab seen for the first time is built twice, once to learn them.
            // The glass, like its mock, offers no Fold all, only Open all on a page where a group
            // has been folded, so the way back is never lost.
            if (SectionKeysByTab.TryGetValue(_activeTab, out List<string>? knownSections) && knownSections.Count > 1
                && (!GlassPanel || knownSections.Any(_config.TunerFoldedSections.Contains)))
            {
                bool anyOpen = !GlassPanel && knownSections.Any(key => !_config.TunerFoldedSections.Contains(key));
                string foldLabel = _translate(anyOpen ? "tuner.foldall" : "tuner.unfoldall");
                int foldWidth = (int)(TunerText.Measure(foldLabel).X * 0.9f * _uiScale) + Scaled(32);
                Button(foldLabel, new Rectangle(_contentCursorX + _contentColumnWidth - foldWidth, _contentCursorY, foldWidth, Scaled(34)), () =>
                {
                    foreach (string key in knownSections)
                    {
                        _config.TunerFoldedSections.Remove(key);
                        if (anyOpen)
                            _config.TunerFoldedSections.Add(key);
                    }
                    _onSave();
                    Reflow();
                });
                _contentCursorY += Scaled(GlassPanel ? 48 : 40);
            }
            _contentStartY = _contentCursorY;
            if (Grouped)
                BuildGroupedPage(_activeTab);
            else
            {
                _buildingTabKey = Tabs[_activeTab].Key;
                Tabs[_activeTab].Build(this);
            }
            Search:
            if (_searchQuery != null)
                BuildSearchResults();
            EndSection();
            _rowsEnabledWhen = null;
            var builtSections = _sectionHeaders.Select(header => header.Key).ToList();
            bool firstLook = !SectionKeysByTab.TryGetValue(_activeTab, out List<string>? previous);
            // Only headings that were on screen are remembered, but a folded heading still is one,
            // so folding never drops a group from the list.
            if (firstLook || !previous!.SequenceEqual(builtSections))
            {
                SectionKeysByTab[_activeTab] = builtSections;
                // Built again whenever the headings changed, not only on a first look: the fold-all
                // button above was drawn from the old list, so switching Shadows off left it over a
                // tab that is now one switch, and pressing it folded groups nobody could see, which
                // all came back folded when Shadows went on. Once per change, so a tab whose headings
                // cannot settle cannot spin.
                bool buttonWasRight = firstLook
                    ? builtSections.Count <= 1
                    : previous!.ToHashSet().SetEquals(builtSections);
                if (!buttonWasRight && !_rebuildingForSections)
                {
                    _rebuildingForSections = true;
                    try
                    {
                        Reflow();
                    }
                    finally
                    {
                        _rebuildingForSections = false;
                    }
                    return;
                }
            }
            // The dials this tab is keeping back, offered at the foot of it, so a player who never
            // looks at the header still finds out they exist.
            if (_fineTuningRowsHidden > 0)
            {
                _contentCursorY += Scaled(SectionLeadBase);
                Button(_translate("tuner.finetuning.hidden").Replace("{{count}}", _fineTuningRowsHidden.ToString()),
                    new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, RowHeight), ShowFineTuning);
                _contentCursorY += RowPitch;
            }
            _contentEndY = _contentCursorY;
            int contentHeight = _contentCursorY - (contentTop + BodyPadding);

            // Sized to its own words at reading size. The first cut was a fixed box with the text
            // shrunk to fit it, and the Thai label came out too small to read.
            string fineTuningLabel = _translate(_config.TunerShowFineTuning ? "tuner.finetuning.hide" : "tuner.finetuning.show");
            int fineTuningWidth = Math.Min(width / 2, (int)(TunerText.Measure(fineTuningLabel).X * 0.9f * _uiScale) + Scaled(32));
            _fineTuningButton = new TunerTextButton(fineTuningLabel,
                new Rectangle(xPositionOnScreen + width - Scaled(60) - fineTuningWidth, yPositionOnScreen + Scaled(12), fineTuningWidth, Scaled(36)),
                () => { _config.TunerShowFineTuning = !_config.TunerShowFineTuning; _onSave(); Reflow(); })
            {
                TextScale = _uiScale,
                IsChosen = () => _config.TunerShowFineTuning,
            };
            _layoutButton = null;
            if (!string.IsNullOrEmpty(_config.TunerLayout))
            {
                string layoutLabel = _translate(Grouped ? "tuner.layout.switchtoclassic" : "tuner.layout.switchtogrouped");
                int layoutWidth = Math.Min(width / 3, (int)(TunerText.Measure(layoutLabel).X * 0.9f * _uiScale) + Scaled(32));
                _layoutButton = new TunerTextButton(layoutLabel,
                    new Rectangle(_fineTuningButton.Bounds.X - Scaled(10) - layoutWidth, yPositionOnScreen + Scaled(12), layoutWidth, Scaled(36)),
                    () => ChooseLayout(Grouped ? LayoutClassic : LayoutGrouped))
                { TextScale = _uiScale };
            }
            if (GlassPanel)
            {
                _fineTuningButton = null;
                _layoutButton = null;
            }

            // CONSISTENT panel height across tabs: the frame is sized to the tab rail, capped
            // to the view. Switching tabs never resizes the panel; a tab whose content is
            // taller than the frame scrolls inside it (mouse wheel) instead of growing it.
            int bodyHeight = railVisibleTabs * tabPitch;
            height = HeaderHeight + bodyHeight + FooterHeight;

            _bodyTop = contentTop + Scaled(BodyPadding);
            _bodyBottom = contentTop + bodyHeight - Scaled(BodyPadding) - HelpBarHeight - HelpBarGap;
            _hintY = yPositionOnScreen + height - Scaled(30);
            _maxScroll = Math.Max(0, contentHeight - (_bodyBottom - _bodyTop));
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            // On the glass the close is a round button inside the panel at the end of the header row.
            int closeSize = GlassPanel ? Scaled(34) : 48;
            upperRightCloseButton.bounds = GlassPanel
                ? new Rectangle(xPositionOnScreen + width - Scaled(18) - closeSize, yPositionOnScreen + Scaled(14), closeSize, closeSize)
                : new Rectangle(xPositionOnScreen + width - Scaled(40), yPositionOnScreen - Scaled(8), closeSize, closeSize);
        }

        /// <summary>The pages of the open category, as a row of chips that wraps onto a second line
        /// rather than running off the side: a game has no sideways scroll anyone would think to
        /// try, so a page past the edge would be a page nobody found.</summary>
        private void BuildPageChips()
        {
            string category = GroupedPages[_activeTab].Category;
            int[] pages = [.. Enumerable.Range(0, GroupedPages.Length).Where(page => GroupedPages[page].Category == category)];
            if (pages.Length < 2)
                return;
            int x = _contentCursorX, gap = Scaled(8), rowHeight = Scaled(36);
            Texture2D? icons = GlassPanel ? GlassIcons() : null;
            foreach (int page in pages)
            {
                string label = _translate("tuner.page." + GroupedPages[page].Page);
                int? icon = icons != null && PageIconName(GroupedPages[page].Page) is string iconName ? GlassIconNamed(iconName) : null;
                int iconInset = icon != null ? Scaled(14) + Scaled(16) : 0;
                int chipWidth = (int)(TunerText.Measure(label).X * 0.9f * _uiScale) + Scaled(28) + iconInset;
                if (x > _contentCursorX && x + chipWidth > _contentCursorX + _contentColumnWidth)
                {
                    x = _contentCursorX;
                    _contentCursorY += rowHeight + gap;
                }
                int target = page;
                var chip = Button(label, new Rectangle(x, _contentCursorY, chipWidth, rowHeight), () => { Game1.playSound("smallSelect"); OpenPage(target); });
                chip.IsChosen = () => _activeTab == target;
                chip.Pill = true;
                if (icon is int cell)
                {
                    chip.IconSheet = icons;
                    chip.IconSource = GlassIconSource(cell, Scaled(16));
                    chip.LeftInset = iconInset;
                }
                x += chipWidth + gap;
            }
            _contentCursorY += rowHeight + Scaled(14);
        }

        /// <summary>Asked once, of a player who updated to the release that brought the grouped
        /// layout: which one they want. Their settings are the same either way, and the header has a
        /// switch to change their mind. A first install never sees this; it starts grouped.</summary>
        /// <remarks>
        /// A box over the menu, as on the mock's update question, the first time F6 opens after the
        /// update. The answer is kept in config.json (TunerLayout), so it is never asked again in any
        /// later version; closing F6 without picking asks again next time. The new menu is the glass
        /// and the classic one the wooden box, as on the mock.
        /// </remarks>
        private bool LayoutPromptShown => string.IsNullOrEmpty(_config.TunerLayout);
        private Rectangle _promptBox, _promptNew, _promptOld;
        private string _promptBody = "";

        private void PlaceLayoutPrompt()
        {
            if (!LayoutPromptShown)
                return;
            int boxWidth = Math.Min(Scaled(620), width - Scaled(40));
            int pad = Scaled(26), gap = Scaled(12);
            float bodyScale = 0.85f * _uiScale;
            _promptBody = TunerText.Wrap(_translate("tuner.layout.ask"), boxWidth - pad * 2, bodyScale);
            // The title's own height: Thai is drawn in the game's font, taller than the glass typeface.
            int titleHeight = (int)(TunerText.Measure(_translate("tuner.layout.asktitle")).Y * 1.15f * _uiScale);
            int bodyHeight = (int)(TunerText.Measure(_promptBody).Y * bodyScale);
            int choiceHeight = Scaled(78);
            int boxHeight = pad + titleHeight + Scaled(10) + bodyHeight + Scaled(18) + choiceHeight + pad;
            _promptBox = new Rectangle(xPositionOnScreen + (width - boxWidth) / 2, yPositionOnScreen + Scaled(120), boxWidth, boxHeight);
            int choiceWidth = (boxWidth - pad * 2 - gap) / 2;
            int choiceTop = _promptBox.Bottom - pad - choiceHeight;
            _promptNew = new Rectangle(_promptBox.X + pad, choiceTop, choiceWidth, choiceHeight);
            _promptOld = new Rectangle(_promptNew.Right + gap, choiceTop, choiceWidth, choiceHeight);
        }

        private void PickFromLayoutPrompt(bool grouped) => ChooseLayout(grouped ? LayoutGrouped : LayoutClassic);

        private void DrawLayoutPrompt(SpriteBatch spriteBatch)
        {
            if (!LayoutPromptShown)
                return;
            var panel = new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height);
            if (TunerTheme.Glass)
                TunerTheme.Plate(spriteBatch, panel, Scaled(20), Color.Black * 0.35f);
            else
                spriteBatch.Draw(Game1.staminaRect, panel, Color.Black * 0.35f);
            int radius = Scaled(20);
            if (TunerTheme.Glass)
            {
                TunerTheme.Plate(spriteBatch, _promptBox, radius, TunerTheme.PanelGlass * 0.97f);
                TunerTheme.PlateRim(spriteBatch, _promptBox, radius, TunerTheme.Rim);
            }
            else
                drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    _promptBox.X, _promptBox.Y, _promptBox.Width, _promptBox.Height, Color.White, 1f, drawShadow: true);
            int pad = Scaled(26);
            float titleScale = 1.15f * _uiScale, bodyScale = 0.85f * _uiScale;
            TunerText.DrawFit(spriteBatch, _translate("tuner.layout.asktitle"), new Vector2(_promptBox.X + pad, _promptBox.Y + pad),
                _promptBox.Width - pad * 2, TunerTheme.Ink, titleScale);
            int titleHeight = (int)(TunerText.Measure(_translate("tuner.layout.asktitle")).Y * titleScale);
            TunerText.Draw(spriteBatch, _promptBody, new Vector2(_promptBox.X + pad, _promptBox.Y + pad + titleHeight + Scaled(10)),
                TunerTheme.Glass ? TunerTheme.InkDim : TunerTheme.Ink * 0.85f, bodyScale, shadowIntensity: 0f);
            DrawPromptChoice(spriteBatch, _promptNew, _translate("tuner.layout.grouped"), _translate("tuner.layout.grouped.small"));
            DrawPromptChoice(spriteBatch, _promptOld, _translate("tuner.layout.classic"), _translate("tuner.layout.classic.small"));
        }

        private void DrawPromptChoice(SpriteBatch spriteBatch, Rectangle choice, string label, string note)
        {
            bool hovered = choice.Contains(Game1.getMouseX(), Game1.getMouseY());
            int radius = Scaled(14);
            if (TunerTheme.Glass)
            {
                TunerTheme.Plate(spriteBatch, choice, radius, hovered ? TunerTheme.Accent * 0.14f : TunerTheme.Overlay * 0.07f);
                TunerTheme.PlateRim(spriteBatch, choice, radius, hovered ? TunerTheme.Accent : TunerTheme.Rim);
            }
            else
                drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    choice.X, choice.Y, choice.Width, choice.Height, hovered ? new Color(255, 206, 96) : Color.White, 1f, drawShadow: false);
            float labelScale = 0.95f * _uiScale, noteScale = 0.75f * _uiScale;
            float labelHeight = TunerText.Measure("A").Y * labelScale, noteHeight = TunerText.Measure("A").Y * noteScale;
            float top = choice.Center.Y - (labelHeight + Scaled(4) + noteHeight) / 2f;
            float labelWidth = Math.Min(choice.Width - Scaled(16), TunerText.Measure(label).X * labelScale);
            TunerText.DrawFit(spriteBatch, label, new Vector2(choice.Center.X - labelWidth / 2f, top), choice.Width - Scaled(16), TunerTheme.Ink, labelScale);
            float noteWidth = Math.Min(choice.Width - Scaled(16), TunerText.Measure(note).X * noteScale);
            TunerText.DrawFit(spriteBatch, note, new Vector2(choice.Center.X - noteWidth / 2f, top + labelHeight + Scaled(4)), choice.Width - Scaled(16),
                TunerTheme.Glass ? TunerTheme.InkFaint : TunerTheme.Ink * 0.65f, noteScale);
        }

        // ================= per-tab content =================

        private void BuildLooks()
        {
            BuildLooksPresets();
            BuildPackLooks();
            BuildShare();
            BuildMasterAndReset();
        }

        /// <summary>The presets and the looks the player saved.</summary>
        private void BuildLooksPresets()
        {
            if (_lookBeforeCode != null)
            {
                Paragraph(_translate("tuner.share.kept").Replace("{{count}}", _settingsTheCodeChanged.ToString()));
                Button(_translate("tuner.share.undo"), new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, ButtonHeight), UndoKeptCode);
                _contentCursorY += RowPitch + Scaled(6);
            }

            // Preset buttons, one row across.
            (LookPreset preset, string key)[] presets =
            [
                (LookPreset.Off, "off"), (LookPreset.Subtle, "subtle"),
                (LookPreset.Cinematic, "cinematic"), (LookPreset.Vibrant, "vibrant"),
                (LookPreset.Nocturne, "nocturne")
            ];
            // The grouped page puts the presets in a card of their own, as on the mock.
            if (Grouped)
                Section("tuner.page.looks");
            int presetButtonWidth = (_contentColumnWidth - ChoiceGap * (presets.Length - 1)) / presets.Length;
            // On the glass each preset is a card with the same spot in the game under it, as on the mock.
            bool presetCards = GlassPanel && presets.All(entry => PresetPicture(entry.key) != null);
            int presetHeight = presetCards ? (presetButtonWidth - Scaled(10)) * 9 / 16 + Scaled(10) + Scaled(30) : RowPitch;
            for (int i = 0; i < presets.Length && !HiddenBySection(countsAsRow: false); i++)
            {
                var (preset, key) = presets[i];
                var rect = new Rectangle(_contentCursorX + i * (presetButtonWidth + ChoiceGap), _contentCursorY, presetButtonWidth, presetHeight);
                var presetButton = new TunerTextButton(_translate($"config.preset.{key}"), rect, () =>
                {
                    // Record WHICH look was picked, not only its numbers. Without this the
                    // settings menu still read "Custom" after a preset was chosen here, so the
                    // two menus disagreed about a thing the player had just done.
                    _config.ActivePreset = preset;
                    _config.ApplyPreset(preset); _onChange(); _onSave(); Reflow();
                })
                {
                    IsChosen = () => _config.ActivePreset == preset,
                    Picture = presetCards ? PresetPicture(key) : null,
                    TextScale = _uiScale,
                };
                _buttons.Add(presetButton);
            }
            if (!_sectionRowsHidden)
                _contentCursorY += presetHeight + Scaled(12);

            Section("tuner.mylooks");
            if (!HiddenBySection())
            {
                int chipX = _contentCursorX;
                foreach (var profile in _config.SavedProfiles)
                {
                    string chipName = ShownLookName(profile);
                    // On the glass the delete cross sits inside the chip after the name, as on the mock,
                    // rather than hanging off its corner the way it does on the wooden box.
                    int crossRoom = Scaled(26);
                    int chipWidth = GlassPanel
                        ? Math.Min(Scaled(240), Scaled(36) + crossRoom + (int)(TunerText.Measure(chipName).X * 0.85f * _uiScale))
                        : Math.Min(Scaled(160), Scaled(44) + (int)(Game1.smallFont.MeasureString(chipName).X * 0.7f * _uiScale));
                    if (chipX + chipWidth > _contentCursorX + _contentColumnWidth - 100) { chipX = _contentCursorX; _contentCursorY += Scaled(ChipPitchBase); }
                    var rect = new Rectangle(chipX, _contentCursorY, chipWidth, ButtonHeight);
                    var captured = profile;
                    var load = new TunerTextButton(chipName, rect, () => { _config.ApplyProfile(captured); _onChange(); _onSave(); Reflow(); })
                    {
                        // Lit while the live settings are still exactly what this look holds, so the
                        // panel says which saved look is in effect; move any slider and it goes out.
                        IsChosen = () => _config.MatchesProfile(captured),
                        Faded = captured.MadeBeforeACode,
                        TextScale = _uiScale,
                        RightInset = GlassPanel ? crossRoom : 0,
                        Pill = GlassPanel,
                    };
                    int crossSize = Scaled(20);
                    _chips.Add(new TunerChip
                    {
                        Load = load,
                        Delete = GlassPanel
                            ? new Rectangle(rect.Right - Scaled(8) - crossSize, rect.Center.Y - crossSize / 2, crossSize, crossSize)
                            : new Rectangle(rect.Right - 14, rect.Y - 6, 24, 24),
                        Profile = captured
                    });
                    chipX += chipWidth + 12;
            }
            string saveLabel = _translate(GlassPanel ? "tuner.savelook" : "tuner.save");
            Texture2D? saveIcons = GlassPanel ? GlassIcons() : null;
            int? plus = saveIcons != null ? GlassIconNamed("plus") : null;
            int plusInset = plus != null ? Scaled(14) + Scaled(16) : 0;
            int saveWidth = Math.Max(Scaled(96), (int)(TunerText.Measure(saveLabel).X * 0.9f * _uiScale) + Scaled(32) + plusInset);
            _buttons.Add(new TunerTextButton(saveLabel, new Rectangle(_contentCursorX + _contentColumnWidth - saveWidth, _contentCursorY, saveWidth, ButtonHeight), PromptSaveProfile)
            {
                Dashed = true,
                TextScale = _uiScale,
                IconSheet = plus != null ? saveIcons : null,
                IconSource = plus is int plusCell ? GlassIconSource(plusCell, Scaled(16)) : default,
                LeftInset = plusInset,
            });
            if (_lookAskedToDelete != null && _config.SavedProfiles.Contains(_lookAskedToDelete))
                BuildDeleteLookQuestion(_lookAskedToDelete);
            _contentCursorY += Scaled(SectionGapBase);
            }
            EndSection();

        }

        private void BuildMasterAndReset()
        {
            // Read under the Looks tab's name wherever it is built, so the share-code list and the
            // coverage check name it the same in both layouts.
            if (_labelRecorder != null)
                _probeSectionLabel = _translate("tuner.tab.looks");
            Toggle("tuner.master", () => _config.Enabled, value => _config.Enabled = value, "help.master");
            BuildResetEverything();
        }

        /// <summary>Asked for once before anything is reset; cleared by the cancel button and when the panel closes.</summary>
        private bool _resetAsked;

        /// <summary>
        /// Every setting back to the defaults, behind a second press.
        /// </summary>
        /// <remarks>
        /// It cannot be undone, so the first press only asks: it says what goes and what stays, and
        /// the reset happens on a separate confirm button, next to one that cancels. Saved looks are
        /// kept, so a player who saved theirs first can load it back.
        /// </remarks>
        private void BuildResetEverything()
        {
            var row = new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, ButtonHeight);
            if (!_resetAsked)
            {
                Button(_translate("tuner.reset.all"), row, () => { _resetAsked = true; Game1.playSound("smallSelect"); Reflow(); });
                Help(row, "help.reset.all");
                _contentCursorY += RowPitch;
                return;
            }
            Paragraph(_translate("tuner.reset.ask"));
            int gap = Scaled(10);
            int half = (_contentColumnWidth - gap) / 2;
            Button(_translate("tuner.reset.confirm"), new Rectangle(_contentCursorX, _contentCursorY, half, ButtonHeight), ResetEverything);
            Button(_translate("tuner.reset.cancel"), new Rectangle(_contentCursorX + half + gap, _contentCursorY, half, ButtonHeight),
                () => { _resetAsked = false; Game1.playSound("smallSelect"); Reflow(); });
            _contentCursorY += RowPitch;
        }

        private void ResetEverything()
        {
            _resetAsked = false;
            // A code on trial would otherwise put the look from before it back when the panel closes.
            _codeOnTrial = null;
            _lookBeforeTrial = null;
            _lookOnTrial = null;
            _lookBeforeCode = null;
            _config.ResetToDefaults();
            _config.Clamp();
            _onChange();
            _onSave();
            Game1.playSound("drumkit6");
            Reflow();
        }

        /// <summary>
        /// A look as a short piece of text, under the looks it is made of.
        ///
        /// <para>The code is SHOWN as well as copied, and stays on screen until the panel is
        /// closed. A clipboard is not something a mod can promise: it is another program's to
        /// take, it is gone the moment somebody copies anything else, and on some machines this
        /// game never had one. A line that says "copied" and nothing else is a line that is wrong
        /// on those machines and cannot be recovered from. The code itself can be read off the
        /// screen, and somebody sharing a look on a forum will photograph it as often as they
        /// paste it.</para>
        /// </summary>
        /// <summary>The look from another mod on trial now, by name, or empty: its try-on is shown
        /// under the look's own button rather than in the share section further down.</summary>
        private static string _packLookOnTrial = "";
        /// <summary>The last code problem came from a look in this list, so it is said here.</summary>
        private static bool _codeProblemFromPack;

        /// <summary>
        /// Looks other mods offer (their radiance-looks.json, see <see cref="LookPacks"/>). Picking one
        /// is the same try-on as pasting a code: the list of what changes, then Keep or Put mine back.
        /// Nothing is applied before the player picks, which is the point: a settings mod can offer
        /// its look without writing over this mod's config.json.
        /// </summary>
        private void BuildPackLooks()
        {
            if (LookPacks.Found.Count == 0)
            {
                if (Grouped && _searchQuery == null)
                    Paragraph(_translate("tuner.packlooks.none"));
                return;
            }
            Section("tuner.packlooks");
            if (!HiddenBySection())
            {
                Paragraph(_translate("tuner.packlooks.desc"));
                int chipX = _contentCursorX;
                foreach (PackLook look in LookPacks.Found)
                {
                    int chipWidth = Math.Min(_contentColumnWidth, (int)(TunerText.Measure(look.Name).X * 0.9f * _uiScale) + Scaled(32));
                    if (chipX + chipWidth > _contentCursorX + _contentColumnWidth) { chipX = _contentCursorX; _contentCursorY += Scaled(ChipPitchBase); }
                    var rect = new Rectangle(chipX, _contentCursorY, chipWidth, ButtonHeight);
                    PackLook captured = look;
                    _buttons.Add(new TunerTextButton(look.Name, rect, () =>
                    {
                        TryOnCode(captured.Code);
                        _packLookOnTrial = _codeOnTrial != null ? captured.Name : "";
                        _codeProblemFromPack = _codeOnTrial == null && _codeProblem.Length > 0;
                        Reflow();
                    })
                    {
                        TextScale = _uiScale,
                        IsChosen = () => _codeOnTrial != null && _packLookOnTrial == captured.Name
                    });
                    string from = _translate("tuner.packlooks.from").Replace("{{mod}}", look.ModName);
                    _help.Add((rect, look.Description.Length > 0 ? $"{look.Description} ({from})" : from));
                    chipX += chipWidth + 12;
                }
                _contentCursorY += RowPitch;
                if (_codeOnTrial != null && _packLookOnTrial.Length > 0)
                    BuildCodeOnTrial(_codeOnTrial);
                else if (_codeProblemFromPack && _codeProblem.Length > 0)
                    Paragraph(_translate(_codeProblem).Replace("{{name}}", _codeProblemDetail).Replace("{{count}}", _codeProblemDetail));
                _contentCursorY += Scaled(SectionGapBase);
            }
            EndSection();
        }

        private void BuildShare()
        {
            Section("tuner.share");
            if (!HiddenBySection())
            {
                Paragraph(_translate("tuner.share.desc"));
                int gap = Scaled(10);
                int half = (_contentColumnWidth - gap) / 2;
                Button(_translate("tuner.share.copy"),
                    new Rectangle(_contentCursorX, _contentCursorY, half, ButtonHeight),
                    () => CopyShareCode(ShareCodeScope.Look));
                Help(new Rectangle(_contentCursorX, _contentCursorY, half, ButtonHeight), "help.share.copy");
                Button(_translate("tuner.share.copyall"),
                    new Rectangle(_contentCursorX + half + gap, _contentCursorY, half, ButtonHeight),
                    () => CopyShareCode(ShareCodeScope.Everything));
                Help(new Rectangle(_contentCursorX + half + gap, _contentCursorY, half, ButtonHeight), "help.share.copyall");
                _contentCursorY += RowPitch;
                if (_sharedCode.Length > 0)
                {
                    Paragraph(_translate(_sharedCodeCarriedEverything ? "tuner.share.copiedall" : "tuner.share.copied")
                        .Replace("{{count}}", _sharedCodeSettings.ToString()));
                    // Shown with its groups apart, so the line can wrap: joined by dashes it was one
                    // word the width of the code, and a long one ran off the panel. Reading skips
                    // spaces as it skips dashes, so a code typed off the screen still reads.
                    Paragraph(_sharedCode.Replace('-', ' '));
                }
                else if (_sharedCodeIsEmpty)
                {
                    Paragraph(_translate("tuner.share.nothing"));
                }

                if (_codeOnTrial == null || _packLookOnTrial.Length > 0)
                {
                    var pasteRow = new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, ButtonHeight);
                    Button(_translate("tuner.share.paste"), pasteRow, PromptPasteCode);
                    Help(pasteRow, "help.share.paste");
                    _contentCursorY += RowPitch;
                    if (_codeProblem.Length > 0 && !_codeProblemFromPack)
                        Paragraph(_translate(_codeProblem).Replace("{{name}}", _codeProblemDetail).Replace("{{count}}", _codeProblemDetail));
                }
                else
                {
                    BuildCodeOnTrial(_codeOnTrial);
                }
                _contentCursorY += Scaled(SectionGapBase);
            }
            EndSection();
        }

        /// <summary>
        /// What a code on trial changes, and the two ways out of it.
        ///
        /// <para>The code is already in the live settings, so the world behind the panel is the
        /// preview: the same thing a slider does while it is dragged. Nothing is kept until Keep
        /// is pressed, and closing the panel puts the old look back.</para>
        /// </summary>
        private void BuildCodeOnTrial(ShareCodeReading reading)
        {
            Paragraph(_translate("tuner.share.trial").Replace("{{count}}", reading.Changes.Count.ToString()));
            const int LinesShown = 12;
            for (int i = 0; i < reading.Changes.Count && i < LinesShown; i++)
            {
                ShareCodeChange change = reading.Changes[i];
                string line = $"{SettingLabel(change.PropertyName)}: {ShownValue(change.OldValue)} -> {ShownValue(change.NewValue)}"
                    + (change.ReturnsToDefault ? " " + _translate("tuner.share.todefault") : "");
                Paragraph(change.IsPerformance ? $"{_translate("tuner.share.speed")} {line}" : line);
            }
            if (reading.Changes.Count > LinesShown)
                Paragraph(_translate("tuner.share.more").Replace("{{count}}", (reading.Changes.Count - LinesShown).ToString()));
            if (reading.SettingsThisReleaseCannotUse > 0)
                Paragraph(_translate("tuner.share.newer").Replace("{{count}}", reading.SettingsThisReleaseCannotUse.ToString()));
            if (reading.ColourTableNotHere.Length > 0)
                Paragraph(_translate("tuner.share.lutmissing").Replace("{{name}}", reading.ColourTableNotHere));
            // The footer says Esc saves, which is true of every slider and not of this.
            Paragraph(_translate("tuner.share.closeputsback"));

            int gap = Scaled(10);
            int half = (_contentColumnWidth - gap) / 2;
            Button(_translate("tuner.share.keep"), new Rectangle(_contentCursorX, _contentCursorY, half, ButtonHeight), KeepCodeOnTrial);
            Button(_translate("tuner.share.putback"), new Rectangle(_contentCursorX + half + gap, _contentCursorY, half, ButtonHeight),
                () => { PutBackCodeOnTrial(); Game1.playSound("cancel"); Reflow(); });
            _contentCursorY += RowPitch;
        }

        /// <summary>A setting's name as a reader says it: ColorGradeSaturation is "Color grade
        /// saturation".</summary>
        private string SettingLabel(string property)
        {
            if (LabelsBySetting(_config, _translate).TryGetValue(property, out string? label))
                return label;
            // The colour table is picked from a row of buttons, which have no reader for the probe
            // to find, so it is named here by the heading those buttons sit under.
            if (property == nameof(ModConfig.ColorGradeLut))
                return _translate("tuner.lut");
            string spaced = System.Text.RegularExpressions.Regex.Replace(property, "(?<=[a-z0-9])(?=[A-Z])", " ");
            return spaced.Length > 1 ? spaced[0] + spaced[1..].ToLowerInvariant() : spaced;
        }

        /// <summary>Every row this panel builds, by its label and what it reads, while a probe is
        /// listening (see <see cref="LabelsBySetting"/>). Null the rest of the time.</summary>
        private List<(string Label, Func<object> Read)>? _labelRecorder;
        /// <summary>The heading the probe is under, so a row called only "Size" says whose size.</summary>
        private string _probeSectionLabel = "";

        private string ProbeLabel(string key)
        {
            string row = _translate(key);
            // A switch named after its own section ("Bloom" under "Bloom") says it once.
            return _probeSectionLabel.Length == 0 || row.Contains(_probeSectionLabel, StringComparison.OrdinalIgnoreCase)
                ? row
                : $"{_probeSectionLabel}: {row}";
        }

        private static Dictionary<string, string>? _labelBySetting;
        private static string _labelLanguage = "";
        private static bool _probingLabels;

        /// <summary>
        /// The label each setting wears on its row in this panel, in the language being played in.
        ///
        /// <para>The list a code is tried on with used the setting's name from the code, split
        /// into words: English whatever the game's language, and not the words on the slider the
        /// player would go looking for. Nothing ties a row to a property except the reader it was
        /// built with, so the panel is built once, every tab, on a copy of the config with every
        /// switch on and every section open (so the rows a switch owns are built too), and each
        /// setting is nudged in turn to see which row moves with it. A setting no row reads keeps
        /// the split name. Done once per language, on the first code tried on.</para>
        /// </summary>
        internal static Dictionary<string, string> LabelsBySetting(ModConfig config, Func<string, string> translate)
        {
            string language = LocalizedContentManager.CurrentLanguageCode.ToString();
            if (_labelBySetting != null && _labelLanguage == language)
                return _labelBySetting;
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_probingLabels)
                return found;
            _probingLabels = true;
            try
            {
                var copy = new ModConfig();
                copy.ApplyProfile(config.CaptureProfile(""));
                copy.TunerFoldedSections = [];
                PropertyInfo[] properties = typeof(ModConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (PropertyInfo property in properties)
                    if (property.PropertyType == typeof(bool) && property.CanWrite && property.CanRead)
                        property.SetValue(copy, true);
                var probe = new RadianceTunerMenu(copy, translate, () => { }, () => { }, slideIn: false)
                {
                    _labelRecorder = [],
                };
                for (int tab = 0; tab < Tabs.Length; tab++)
                {
                    probe._activeTab = tab;
                    probe._probeSectionLabel = translate(Tabs[tab].Key);
                    probe.Reflow();
                }
                List<(string Label, Func<object> Read)> rows = probe._labelRecorder;
                var before = new object[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                    before[i] = rows[i].Read();
                foreach ((int _, string name, bool _) in ShareCode.KnownSettings)
                {
                    PropertyInfo? property = Array.Find(properties, candidate => candidate.Name == name);
                    if (property == null)
                        continue;
                    object? was = property.GetValue(copy);
                    object? nudged = was switch
                    {
                        bool on => !on,
                        float number => number + 0.37f,
                        int whole => whole + 1,
                        Enum choice => NextChoice(choice),
                        _ => null,
                    };
                    if (nudged == null)
                        continue;
                    property.SetValue(copy, nudged);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (!Equals(rows[i].Read(), before[i]))
                        {
                            found[name] = rows[i].Label;
                            break;
                        }
                    }
                    property.SetValue(copy, was);
                }
            }
            catch (Exception)
            {
                // A probe that trips leaves the names as they were split; nothing depends on it.
            }
            finally
            {
                _probingLabels = false;
            }
            _labelBySetting = found;
            _labelLanguage = language;
            return found;
        }

        private static object NextChoice(Enum choice)
        {
            Array values = Enum.GetValues(choice.GetType());
            int at = Array.IndexOf(values, choice);
            return values.GetValue((at + 1) % values.Length)!;
        }

        /// <summary>A value as the sliders show it: a dial that has been dragged holds 0.099999994
        /// where the slider says 0.1, and the list printed the long one.</summary>
        private string ShownValue(string value)
            => value.Equals("True", StringComparison.OrdinalIgnoreCase) ? _translate("tuner.share.on")
             : value.Equals("False", StringComparison.OrdinalIgnoreCase) ? _translate("tuner.share.off")
             : value.Length == 0 ? _translate("tuner.share.none")
             : value.Contains('.') && float.TryParse(value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out float number)
                 ? number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
             : value;

        // ---- a code tried on, then kept or put back ----
        // Static, because the panel is built again around the text box (Reopen) and a trial has to
        // outlive that. Cleared when the panel closes for real.

        /// <summary>A code being tried on: already in the live settings and not kept yet.</summary>
        private static ShareCodeReading? _codeOnTrial;
        private static NamedProfile? _lookBeforeTrial;
        private static LookPreset _presetBeforeTrial;
        /// <summary>The look as the code left it, to tell a trial left alone from one worked on.</summary>
        private static NamedProfile? _lookOnTrial;

        /// <summary>The look a kept code replaced, for the Undo row at the top of the tab, until the
        /// panel closes. Somebody who has just pressed the wrong button looks for the way back where
        /// their hand was, not among the chips.</summary>
        private static NamedProfile? _lookBeforeCode;
        private static LookPreset _presetBeforeCode;
        private static int _settingsTheCodeChanged;

        /// <summary>Why the last code was not tried on, as a translation key, or empty.</summary>
        private static string _codeProblem = "";
        /// <summary>The name or number the problem's sentence carries, if it has one.</summary>
        private static string _codeProblemDetail = "";

        /// <summary>How many "before a code" chips are kept; the oldest goes when another is made.</summary>
        private const int BackupChipsKept = 3;

        private void PromptPasteCode()
        {
            // Most people press this having just copied a code, so the box opens with it already
            // in. A wrong guess costs nothing: it can be deleted, and Ctrl+V pastes over it.
            string offered = "";
            try
            {
                string clipboard = "";
                if (DesktopClipboard.GetText(ref clipboard) && clipboard.TrimStart().StartsWith("RAD", StringComparison.OrdinalIgnoreCase))
                    offered = clipboard.Trim();
            }
            catch (Exception)
            {
                // No clipboard on this machine; the box opens empty.
            }
            Game1.activeClickableMenu = new TextEntryMenu(_translate("tuner.share.pasteprompt"), offered,
                onDone: typed => { _packLookOnTrial = ""; _codeProblemFromPack = false; TryOnCode(typed); Reopen(); },
                onCancel: Reopen,
                longText: true);
        }

        private void TryOnCode(string typed)
        {
            _codeProblem = "";
            _codeProblemDetail = "";
            PutBackCodeOnTrial();
            if (string.IsNullOrWhiteSpace(typed))
                return;
            if (!ShareCode.TryRead(typed, _config, out ShareCodeReading reading, out string problem))
            {
                _codeProblem = problem switch
                {
                    "notours" => "tuner.share.bad.notours",
                    "scope" => "tuner.share.bad.scope",
                    _ => "tuner.share.bad.typo",
                };
                Game1.playSound("cancel");
                return;
            }
            if (reading.Changes.Count == 0)
            {
                // Nothing to try on is not the same as "you already have it": the one thing the
                // code asked for may be a colour table this machine lacks, or settings from a newer
                // release, and saying "matches" left the player with no idea what to ask for.
                if (reading.ColourTableNotHere.Length > 0)
                {
                    _codeProblem = "tuner.share.lutmissing";
                    _codeProblemDetail = reading.ColourTableNotHere;
                }
                else if (reading.SettingsThisReleaseCannotUse > 0)
                {
                    _codeProblem = "tuner.share.newer";
                    _codeProblemDetail = reading.SettingsThisReleaseCannotUse.ToString();
                }
                else
                {
                    _codeProblem = "tuner.share.same";
                }
                return;
            }
            // The file aside NOW, while it still holds the look from before the code. Taken at Keep,
            // it held whatever the panel had saved in between: flip one switch during the trial and
            // the "last way back" was a copy of the trial itself.
            ShareCode.KeepConfigCopy();
            _lookBeforeTrial = _config.CaptureProfile("");
            _presetBeforeTrial = _config.ActivePreset;
            ShareCode.Apply(reading, _config);
            _lookOnTrial = _config.CaptureProfile("");
            _codeOnTrial = reading;
            _onChange();
            Game1.playSound("coin");
        }

        /// <summary>Put back the look a code on trial replaced. Nothing was saved, so nothing
        /// else has to be undone.</summary>
        private void PutBackCodeOnTrial()
        {
            if (_codeOnTrial != null && _lookBeforeTrial != null)
            {
                _config.ApplyProfile(_lookBeforeTrial);
                _config.ActivePreset = _presetBeforeTrial;
                _onChange();
            }
            _codeOnTrial = null;
            _lookBeforeTrial = null;
            _lookOnTrial = null;
        }

        /// <summary>The panel closes with a code still on trial. Untouched since it went on, it was
        /// never kept, so the old look comes back. Worked on since, with a chip loaded, a preset
        /// picked or a dial moved, it is the look the player is now making, and putting the old one
        /// back would throw that work away with the footer promising Esc saves.</summary>
        private void CloseTrialWithThePanel()
        {
            if (_codeOnTrial != null && _lookOnTrial != null && !_config.MatchesProfile(_lookOnTrial))
                KeepCodeOnTrial(quietly: true);
            else
                PutBackCodeOnTrial();
        }

        /// <summary>
        /// A saved look's name as the chip shows it.
        /// </summary>
        /// <remarks>
        /// The look kept from before a code is named by the mod, not by the player, and that name was
        /// written in the language the game ran in when it was made: switch the game to another
        /// language and the chip read as empty boxes, because that language's font has none of those
        /// letters. Its name is put together again in today's language, keeping the time it was made.
        /// A name the player typed is theirs and is shown as typed.
        /// </remarks>
        private string ShownLookName(NamedProfile profile)
        {
            if (!profile.MadeBeforeACode)
                return profile.Name;
            System.Text.RegularExpressions.Match time = System.Text.RegularExpressions.Regex.Match(profile.Name, @"\d{1,2}:\d{2}\s*$");
            return time.Success
                ? _translate("tuner.share.backupname").Replace("{{time}}", time.Value.Trim())
                : profile.Name;
        }

        private void KeepCodeOnTrial() => KeepCodeOnTrial(quietly: false);

        private void KeepCodeOnTrial(bool quietly)
        {
            if (_codeOnTrial == null || _lookBeforeTrial == null)
                return;
            // The look it replaces becomes a chip first, so the way back survives the panel closing.
            NamedProfile before = _lookBeforeTrial;
            before.Name = _translate("tuner.share.backupname").Replace("{{time}}", DateTime.Now.ToString("HH:mm"));
            before.MadeBeforeACode = true;
            _config.SavedProfiles.Add(before);
            List<NamedProfile> backups = _config.SavedProfiles.FindAll(profile => profile.MadeBeforeACode);
            for (int i = 0; i < backups.Count - BackupChipsKept; i++)
                _config.SavedProfiles.Remove(backups[i]);
            _lookBeforeCode = before;
            _settingsTheCodeChanged = _codeOnTrial.Changes.Count;
            // The look is now the code's, not the preset button that was lit before it.
            _presetBeforeCode = _presetBeforeTrial;
            _config.ActivePreset = LookPreset.Custom;
            _codeOnTrial = null;
            _lookBeforeTrial = null;
            _lookOnTrial = null;
            _onSave();
            if (quietly)
                return;
            Game1.playSound("newArtifact");
            Reflow();
        }

        private void UndoKeptCode()
        {
            if (_lookBeforeCode == null)
                return;
            _config.ApplyProfile(_lookBeforeCode);
            _config.ActivePreset = _presetBeforeCode;
            _lookBeforeCode = null;
            _onChange();
            _onSave();
            Game1.playSound("drumkit6");
            Reflow();
        }

        /// <summary>The code last copied, kept so the panel can go on showing it. Cleared when the
        /// panel closes: a code belongs to the settings it was taken from, and those move.</summary>
        private string _sharedCode = "";
        private int _sharedCodeSettings;
        private bool _sharedCodeCarriedEverything;
        private bool _sharedCodeIsEmpty;

        private void CopyShareCode(ShareCodeScope scope)
        {
            string code = ShareCode.Write(_config, scope, out int settingsCarried);
            _sharedCodeIsEmpty = settingsCarried == 0;
            _sharedCode = _sharedCodeIsEmpty ? "" : code;
            _sharedCodeSettings = settingsCarried;
            _sharedCodeCarriedEverything = scope == ShareCodeScope.Everything;
            if (!_sharedCodeIsEmpty)
            {
                // The game's own clipboard, the one the co-op invite code is copied with. It can
                // fail quietly on a machine without one, which is the other reason the code is
                // printed on screen rather than only promised to the clipboard.
                try { DesktopClipboard.SetText(code); }
                catch (Exception) { /* no clipboard here; the code is on screen instead */ }
            }
            Game1.playSound(_sharedCodeIsEmpty ? "cancel" : "coin");
            Reflow();
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
            if (!HiddenBySection())
            {
                string[] looks = [.. ModConfig.ShippedLuts, .. LutCatalog.Discover()];
                int buttonX = _contentCursorX;
                foreach (string look in looks)
                {
                    string label = LutLabel(look);
                    int buttonWidth = GlassPanel
                        ? Math.Min(Scaled(240), Scaled(32) + (int)(TunerText.Measure(label).X * 0.85f * _uiScale))
                        : Math.Min(Scaled(180), Scaled(28) + (int)(Game1.smallFont.MeasureString(label).X * 0.7f * _uiScale));
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
            }
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
            Section("tuner.section.gradetone");
            Toggle("tuner.automood", () => _config.ColorGradeAuto, value => _config.ColorGradeAuto = value, "help.automood");
            Slider("tuner.strength", 0f, 1f, () => _config.ColorGradeStrength, value => _config.ColorGradeStrength = value);
            Slider("tuner.contrast", 0.5f, 1.5f, () => _config.ColorGradeContrast, value => _config.ColorGradeContrast = value, "help.contrast");
            Slider("tuner.saturation", 0f, 2f, () => _config.ColorGradeSaturation, value => _config.ColorGradeSaturation = value, "help.saturation");
            Slider("tuner.temperature", -1f, 1f, () => _config.ColorGradeTemperature, value => _config.ColorGradeTemperature = value, "help.temperature");
            Slider("tuner.brightness", 0.5f, 1.5f, () => _config.ColorGradeBrightness, value => _config.ColorGradeBrightness = value);
            Toggle("tuner.tonemap", () => _config.ColorGradeToneMap, value => _config.ColorGradeToneMap = value, "help.tonemap");
            EndDependsOn();
            Section("tuner.section.eyecomfort");
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
        [
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
        ];

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
            if (_config.DirectionalShadowsEnabled && !HiddenBySection())
            {
                (ShadowModel model, string key)[] shadowModels =
                [
                    (ShadowModel.Modern, "modern"),
                    (ShadowModel.Classic, "classic"),
                ];
                int buttonWidth = (_contentColumnWidth - ChoiceGap * (shadowModels.Length - 1)) / shadowModels.Length;
                for (int i = 0; i < shadowModels.Length; i++)
                {
                    var (model, key) = shadowModels[i];
                    var rect = new Rectangle(_contentCursorX + i * (buttonWidth + ChoiceGap), _contentCursorY, buttonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.shadowmodel.{key}"), rect, () =>
                    {
                        _config.DirectionalShadowModel = model; _onChange(); _onSave(); Reflow();
                    });
                    button.IsChosen = () => _config.DirectionalShadowModel == model;
                    Help(rect, $"help.shadowmodel.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            Toggle("tuner.shadowgrounded", () => _config.ShadowGroundedLook, value => _config.ShadowGroundedLook = value, "help.shadowgrounded");
            DependsOn(() => _config.DirectionalShadowsEnabled && _config.ShadowGroundedLook);
            Slider("tuner.shadowgroundeddepth", ModConfig.ShadowGroundedDepthMin, ModConfig.ShadowGroundedDepthMax,
                () => _config.ShadowGroundedDepth, value => _config.ShadowGroundedDepth = value, "help.shadowgroundeddepth");
            DependsOn(() => _config.DirectionalShadowsEnabled);
            Section("tuner.section.shadowsun");
            Slider("tuner.shadowstrength", 0f, ModConfig.ShadowStrengthMax, () => _config.DirectionalShadowStrength,
                value => _config.DirectionalShadowStrength = value, "help.shadowstrength");
            Slider("tuner.shadowlength", 0.2f, 2f, () => _config.DirectionalShadowLength, value => _config.DirectionalShadowLength = value, "help.shadowlength");
            Slider("tuner.goldenhour", 0f, 1f, () => _config.GoldenHourStrength, value => _config.GoldenHourStrength = value, "help.goldenhour");
            Slider("tuner.sunseason", 0f, 1f, () => _config.SunSeasonStrength, value => _config.SunSeasonStrength = value, "help.sunseason");
            Compass("tuner.shadowsunbearing", () => _config.ShadowSunBearing, value => _config.ShadowSunBearing = value, "help.shadowsunbearing");
            Compass("tuner.sunlightbearing", () => _config.SunlightBearing, value => _config.SunlightBearing = value, "help.sunlightbearing");
            Slider("tuner.shadowtint", 0f, 1f, () => _config.ShadowTint, value => _config.ShadowTint = value, "help.shadowtint");
            Section("tuner.section.shadowedges", fineTuning: true);
            Slider("tuner.shadowblur", 0f, ModConfig.ShadowBlurMax, () => _config.DirectionalShadowBlur, value => _config.DirectionalShadowBlur = value, "help.shadowblur");
            Slider("tuner.shadowcontacthardness", 0f, 1f, () => _config.ShadowContactHardness, value => _config.ShadowContactHardness = value, "help.shadowcontacthardness");
            Slider("tuner.shadowpenumbrastretch", 0f, 1f, () => _config.ShadowPenumbraStretch, value => _config.ShadowPenumbraStretch = value, "help.shadowpenumbrastretch");
            Slider("tuner.shadowcasts", ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax,
                () => _config.ShadowCastsPerCharacter,
                value => _config.ShadowCastsPerCharacter = (int)MathF.Round(value), "help.shadowcasts");
            Section("tuner.section.shadowcasters");
            Toggle("tuner.shadowplayer", () => _config.DirectionalShadowPlayer, value => _config.DirectionalShadowPlayer = value, "help.shadowplayer");
            Toggle("tuner.shadowcarried", () => _config.ShadowCarriedLightsCast, value => _config.ShadowCarriedLightsCast = value, "help.shadowcarried");
            Toggle("tuner.shadowvillagers", () => _config.DirectionalShadowVillagers, value => _config.DirectionalShadowVillagers = value, "help.shadowvillagers");
            Toggle("tuner.shadowfarmanimals", () => _config.DirectionalShadowFarmAnimals, value => _config.DirectionalShadowFarmAnimals = value, "help.shadowfarmanimals");
            Toggle("tuner.shadowcreatures", () => _config.DirectionalShadowCreatures, value => _config.DirectionalShadowCreatures = value, "help.shadowcreatures");
            Slider("tuner.shadowcreaturelength", 0.1f, 2f, () => _config.ShadowCreatureLength,
                value => _config.ShadowCreatureLength = value, "help.shadowcreaturelength", () => _config.DirectionalShadowCreatures);
            Slider("tuner.shadowcreaturesoftness", 0f, 3f, () => _config.ShadowCreatureSoftness,
                value => _config.ShadowCreatureSoftness = value, "help.shadowcreaturesoftness", () => _config.DirectionalShadowCreatures);
            Slider("tuner.shadowjunimolength", 0.1f, 2f, () => _config.ShadowJunimoLength,
                value => _config.ShadowJunimoLength = value, "help.shadowjunimolength", () => _config.DirectionalShadowCreatures);
            Toggle("tuner.shadowobjects", () => _config.DirectionalShadowObjects, value => _config.DirectionalShadowObjects = value, "help.shadowobjects");
            Toggle("tuner.shadowfences", () => _config.DirectionalShadowFences, value => _config.DirectionalShadowFences = value, "help.shadowfences");
            Slider("tuner.contactshadow", 0f, 1f, () => _config.ContactShadowStrength, value => _config.ContactShadowStrength = value, "help.contactshadow");
            Slider("tuner.contactshadowpeople", 0f, 1f, () => _config.ContactShadowPeopleStrength, value => _config.ContactShadowPeopleStrength = value, "help.contactshadowpeople");
            Toggle("tuner.shadowbuildings", () => _config.DirectionalShadowBuildings, value => _config.DirectionalShadowBuildings = value, "help.shadowbuildings");
            Section("tuner.section.shadowground", fineTuning: true);
            Slider("tuner.shadowgroundforeshortening", ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax,
                () => _config.ShadowGroundForeshortening, value => _config.ShadowGroundForeshortening = value, "help.shadowgroundforeshortening");
            Slider("tuner.shadowcharactergroundforeshortening", ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax,
                () => _config.ShadowCharacterGroundForeshortening, value => _config.ShadowCharacterGroundForeshortening = value, "help.shadowcharactergroundforeshortening");
            // One kind at a time. Pick the thing, and its three dials sit together under the
            // picker, instead of the page carrying all twenty-one at once with a building's three
            // eight rows apart in three different blocks.
            Section("tuner.shadowperkind", fineTuning: true);
            if (_config.DirectionalShadowsEnabled && !HiddenBySection())
            {
                const int kindColumns = 2;
                int kindButtonWidth = (_contentColumnWidth - ChoiceGap * (kindColumns - 1)) / kindColumns;
                for (int i = 0; i < ShadowKinds.Length; i++)
                {
                    int column = i % kindColumns, row = i / kindColumns;
                    // A kind left alone on the last row takes the whole width rather than sitting
                    // beside a gap.
                    bool isLastAndAlone = i == ShadowKinds.Length - 1 && column == 0;
                    var rect = new Rectangle(_contentCursorX + column * (kindButtonWidth + ChoiceGap),
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
            Section("tuner.section.lightdark");
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
            Section("tuner.section.lampshadowdetail", fineTuning: true);
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
            if (_config.LightingEnabled && _config.FloodLightingEnabled && !HiddenBySection())
            {
                // Which model computes the GI map: two buttons, the one in use lit (see WaterReflectModel).
                (GiModel model, string key)[] giModels = [(GiModel.Flood, "flood"), (GiModel.Cascades, "cascades")];
                int giButtonWidth = (_contentColumnWidth - ChoiceGap * (giModels.Length - 1)) / giModels.Length;
                for (int giIndex = 0; giIndex < giModels.Length; giIndex++)
                {
                    var (model, key) = giModels[giIndex];
                    var rect = new Rectangle(_contentCursorX + giIndex * (giButtonWidth + ChoiceGap), _contentCursorY, giButtonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.gimodel.{key}"), rect, () => { _config.FloodGiModel = model; _onChange(); _onSave(); });
                    button.IsChosen = () => _config.FloodGiModel == model;
                    Help(rect, $"help.gimodel.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            Slider("tuner.floodstrength", 0f, 1f, () => _config.FloodLightingStrength, value => _config.FloodLightingStrength = value, "help.floodstrength");
            Slider("tuner.floodshadow", 0f, 1f, () => _config.FloodShadowStrength, value => _config.FloodShadowStrength = value, "help.floodshadow");
            Slider("tuner.colourbleed", 0f, 1f, () => _config.FloodColourBleed, value => _config.FloodColourBleed = value, "help.colourbleed");
            Toggle("tuner.carriedbounce", () => _config.FloodCarriedLightsBounce, value => _config.FloodCarriedLightsBounce = value, "help.carriedbounce");
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
            Slider("tuner.tvglow", 0f, 1f, () => _config.TvScreenGlow, value => _config.TvScreenGlow = value, "help.tvglow");
            Toggle("tuner.windowbeam", () => _config.WindowBeamEnabled, value => _config.WindowBeamEnabled = value, "help.windowbeam");
            // Whether the window keeps its own detail under the light: two buttons, the one in use lit.
            if (_config.WindowEffectsEnabled && !HiddenBySection())
            {
                Paragraph(_translate("tuner.windowdetail"));
                (WindowLightDetail mode, string key)[] detailModes =
                [
                    (WindowLightDetail.WholeWindow, "wholewindow"),
                    (WindowLightDetail.KeepDetail, "keepdetail"),
                ];
                int buttonWidth = (_contentColumnWidth - ChoiceGap * (detailModes.Length - 1)) / detailModes.Length;
                for (int i = 0; i < detailModes.Length; i++)
                {
                    var (mode, key) = detailModes[i];
                    var rect = new Rectangle(_contentCursorX + i * (buttonWidth + ChoiceGap), _contentCursorY, buttonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.windowdetail.{key}"), rect, () =>
                    {
                        _config.WindowLightDetail = mode; _onChange(); _onSave(); Reflow();
                    });
                    button.IsChosen = () => _config.WindowLightDetail == mode;
                    Help(rect, $"help.windowdetail.{key}");
                }
                _contentCursorY += Scaled(50);
                Paragraph(_translate("tuner.windowlightshape"));
                (WindowLightShape shape, string key)[] shapes =
                [
                    (WindowLightShape.Glow, "glow"),
                    (WindowLightShape.Glass, "glass"),
                ];
                int shapeButtonWidth = (_contentColumnWidth - ChoiceGap * (shapes.Length - 1)) / shapes.Length;
                for (int i = 0; i < shapes.Length; i++)
                {
                    var (shape, key) = shapes[i];
                    var rect = new Rectangle(_contentCursorX + i * (shapeButtonWidth + ChoiceGap), _contentCursorY, shapeButtonWidth, Scaled(40));
                    var button = Button(_translate($"tuner.windowlightshape.{key}"), rect, () =>
                    {
                        _config.WindowLightShape = shape; _onChange(); _onSave(); Reflow();
                    });
                    button.IsChosen = () => _config.WindowLightShape == shape;
                    Help(rect, $"help.windowlightshape.{key}");
                }
                _contentCursorY += Scaled(50);
                if (_config.WindowLightShape == WindowLightShape.Glass)
                {
                    Slider("tuner.windowglassrays", 0f, 2f, () => _config.WindowGlassRays,
                        value => _config.WindowGlassRays = value, "help.windowglassrays");
                    Slider("tuner.windowglassraylength", 0.5f, 2f, () => _config.WindowGlassRayLength,
                        value => _config.WindowGlassRayLength = value, "help.windowglassraylength");
                    Slider("tuner.windowglassraywhiteness", 0f, 1f, () => _config.WindowGlassRayWhiteness,
                        value => _config.WindowGlassRayWhiteness = value, "help.windowglassraywhiteness");
                    Slider("tuner.windowglassrayspread", 0f, 8f, () => _config.WindowGlassRaySpread,
                        value => _config.WindowGlassRaySpread = value, "help.windowglassrayspread");
                    Slider("tuner.windowglassfloor", 0f, 2f, () => _config.WindowGlassFloor,
                        value => _config.WindowGlassFloor = value, "help.windowglassfloor");
                    Slider("tuner.windowglasspane", 0f, 2f, () => _config.WindowGlassPane,
                        value => _config.WindowGlassPane = value, "help.windowglasspane");
                    // Only with a curtained window installed: dials for curtains a game cannot show
                    // were noise (LabelStore.CurtainWindowsInstalled).
                    if (LabelStore.CurtainWindowsInstalled)
                    {
                        Toggle("tuner.windowglasscurtaincolour", () => _config.WindowGlassCurtainColour,
                            value => _config.WindowGlassCurtainColour = value, "help.windowglasscurtaincolour");
                        Slider("tuner.windowglasscurtainlight", 0f, 1f, () => _config.WindowGlassCurtainLight,
                            value => _config.WindowGlassCurtainLight = value, "help.windowglasscurtainlight");
                        Slider("tuner.windowglasscurtaincolourstrength", 0f, 2f, () => _config.WindowGlassCurtainColourStrength,
                            value => _config.WindowGlassCurtainColourStrength = value, "help.windowglasscurtaincolourstrength");
                    }
                }
            }
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
            Slider("tuner.windowvehicleglass", 0f, 1f, () => _config.WindowVehicleGlassStrength,
                value => _config.WindowVehicleGlassStrength = value, "help.windowvehicleglass");
            Section("tuner.section.windowreflectiondetail", fineTuning: true);
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
            EndSection();
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
            // The same range and step as the other menu, and as Clamp.
            Slider("tuner.cloudspeed", 0f, 0.1f, () => _config.CloudShadowSpeed, value => _config.CloudShadowSpeed = value, step: 0.005f);
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
            Toggle("tuner.fogmorningonly", () => _config.FogMorningOnly, value => _config.FogMorningOnly = value, "help.fogmorningonly");
            Slider("tuner.fogmorninglifthour", 7f, 12f, () => _config.FogMorningLiftHour, value => _config.FogMorningLiftHour = value,
                "help.fogmorninglifthour", () => _config.FogMorningOnly, step: 0.5f);
            EndDependsOn();
            Section("tuner.section.fognight");
            Toggle("tuner.fognightmist", () => _config.FogNightMist, value => _config.FogNightMist = value, "help.fognightmist");
            DependsOn(() => _config.FogNightMist);
            Slider("tuner.fognightmistcoverage", 0f, 1f, () => _config.FogNightMistCoverage, value => _config.FogNightMistCoverage = value);
            Slider("tuner.fognightmistdensity", 0f, 1f, () => _config.FogNightMistDensity, value => _config.FogNightMistDensity = value);
            Slider("tuner.minefogmist", 0f, 1f, () => _config.MineFogMist, value => _config.MineFogMist = value, "help.minefogmist");
            Slider("tuner.fognightmistlampglow", 0f, 1f, () => _config.FogNightMistLampGlow, value => _config.FogNightMistLampGlow = value, "help.fognightmistlampglow");
            Slider("tuner.fognightmistspeed", 0f, 0.1f, () => _config.FogNightMistSpeed, value => _config.FogNightMistSpeed = value);
            EndDependsOn();
            Section("tuner.section.fogboth");
            // Shared by both fogs, so it goes grey only when neither is on.
            DependsOn(() => _config.FogEnabled || _config.FogNightMist);
            Slider("tuner.fogtopbias", 0f, 1f, () => _config.FogTopBias,
                value => _config.FogTopBias = value, "help.fogtopbias");
            EndDependsOn();
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
            Toggle("tuner.foliageswaygrass", () => _config.FoliageSwayGrass, value => _config.FoliageSwayGrass = value, "help.foliageswaygrass");
            EndDependsOn();
            Toggle("tuner.grasssmoothshake", () => _config.GrassSmoothShake, value => _config.GrassSmoothShake = value, "help.grasssmoothshake");
            Section("tuner.section.sky");
            Toggle("tuner.precipitation", () => _config.PrecipitationEnabled, value => _config.PrecipitationEnabled = value, "help.precipitation");
            Slider("tuner.snowglint", 0f, 1f, () => _config.SnowGlintStrength, value => _config.SnowGlintStrength = value, "help.snowglint");
            Toggle("tuner.aurora", () => _config.AuroraEnabled, value => _config.AuroraEnabled = value, "help.aurora");
            Slider("tuner.aurorastrength", 0f, 2f, () => _config.AuroraStrength,
                value => _config.AuroraStrength = value, "help.aurorastrength",
                () => _config.AuroraEnabled);
            Toggle("tuner.shootingstars", () => _config.ShootingStarsEnabled, value => _config.ShootingStarsEnabled = value, "help.shootingstars");
            Slider("tuner.shootingstarfrequency", 0.25f, 4f, () => _config.ShootingStarFrequency,
                value => _config.ShootingStarFrequency = value, "help.shootingstarfrequency", () => _config.ShootingStarsEnabled);
            Toggle("tuner.waternightstars", () => _config.WaterNightStars, value => _config.WaterNightStars = value, "help.waternightstars");
            Slider("tuner.waternightstarsamount", 0f, 3f, () => _config.WaterNightStarsAmount,
                value => _config.WaterNightStarsAmount = value, "help.waternightstarsamount", () => _config.WaterNightStars);
            Toggle("tuner.gameweatherplaintint", () => _config.GameWeatherPlainTint, value => _config.GameWeatherPlainTint = value, "help.gameweatherplaintint");
            Toggle("tuner.windyspells", () => _config.WindySpellsEnabled, value => _config.WindySpellsEnabled = value, "help.windyspells");
            Slider("tuner.windyspellsstrength", 1.25f, 3f, () => _config.WindySpellsStrength,
                value => _config.WindySpellsStrength = value, "help.windyspellsstrength", () => _config.WindySpellsEnabled);
            // Each kind of weather has its own switch under the precipitation master, and its
            // dials hang off BOTH (PrecipitationSystem asks the master and the kind together).
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
            Section("tuner.section.precipitationwind");
            Toggle("tuner.precipitationwind", () => _config.PrecipitationWind, value => _config.PrecipitationWind = value, "help.precipitationwind");
            DependsOn(() => _config.PrecipitationEnabled && _config.PrecipitationWind);
            // The same leaf look as the Particles tab: one choice for every leaf on screen, reachable
            // from either place.
            BuildLeafLooks();
            Slider("tuner.precipitationdensity", 0.25f, 2f, () => _config.PrecipitationWindDensity,
                value => _config.PrecipitationWindDensity = value, "help.precipitationdensity");
            Slider("tuner.precipitationsize", 0.5f, 2f, () => _config.PrecipitationWindSize,
                value => _config.PrecipitationWindSize = value, "help.precipitationsize");
            Slider("tuner.precipitationopacity", 0.25f, 2f, () => _config.PrecipitationWindOpacity,
                value => _config.PrecipitationWindOpacity = value, "help.precipitationopacity");
            Slider("tuner.precipitationwindslant", 0.25f, 3f, () => _config.PrecipitationWindSlant,
                value => _config.PrecipitationWindSlant = value, "help.precipitationwindslant");
            EndDependsOn();
            Section("tuner.section.lightning");
            Toggle("tuner.lightning", () => _config.LightningEffectsEnabled, value => _config.LightningEffectsEnabled = value, "help.lightning");
            Toggle("tuner.lightningbolts", () => _config.LightningBoltsEnabled, value => _config.LightningBoltsEnabled = value, "help.lightningbolts",
                () => _config.LightningEffectsEnabled);
            Toggle("tuner.lightningwindows", () => _config.LightningThroughWindows, value => _config.LightningThroughWindows = value, "help.lightningwindows",
                () => _config.LightningEffectsEnabled);
            // Wet ground: offered now that where water may stand is decided from the map (a
            // continuous room field kept off roofs, fences, buildings, grass and placed things)
            // rather than tile by tile, which put pools on a roof on a modded map.
            Section("tuner.section.wetground");
            Toggle("tuner.wetworld", () => _config.WetWorldEnabled, value => _config.WetWorldEnabled = value, "help.wetworld");
            DependsOn(() => _config.WetWorldEnabled);
            Slider("tuner.wetworldstrength", 0f, 1f, () => _config.WetWorldStrength,
                value => _config.WetWorldStrength = value, "help.wetworldstrength");
            Slider("tuner.wetworldpuddles", 0f, 1f, () => _config.WetWorldPuddles,
                value => _config.WetWorldPuddles = value, "help.wetworldpuddles");
            if (_config.WetWorldEnabled && !HiddenBySection())
                BuildWetPuddleLooks();
            Slider("tuner.wetworldpuddlereflection", 0f, 1f, () => _config.WetWorldPuddleReflection,
                value => _config.WetWorldPuddleReflection = value, "help.wetworldpuddlereflection");
            Slider("tuner.wetworldlightstreaks", 0f, 2f, () => _config.WetWorldLightStreaks,
                value => _config.WetWorldLightStreaks = value, "help.wetworldlightstreaks");
            Slider("tuner.wetworldlightstreaklength", 0.3f, 3f, () => _config.WetWorldLightStreakLength,
                value => _config.WetWorldLightStreakLength = value, "help.wetworldlightstreaklength");
            Slider("tuner.wetworldlightstreakangle", -60f, 60f, () => _config.WetWorldLightStreakAngle,
                value => _config.WetWorldLightStreakAngle = value, "help.wetworldlightstreakangle");
            Slider("tuner.wetworldlightstreakglow", 0f, 1f, () => _config.WetWorldLightStreakGlow,
                value => _config.WetWorldLightStreakGlow = value, "help.wetworldlightstreakglow");
            Slider("tuner.wetworldlightstreakstone", 0f, 1f, () => _config.WetWorldLightStreakStone,
                value => _config.WetWorldLightStreakStone = value, "help.wetworldlightstreakstone");
            Slider("tuner.wetworldlightstreakripples", 0f, 1f, () => _config.WetWorldLightStreakRipples,
                value => _config.WetWorldLightStreakRipples = value, "help.wetworldlightstreakripples");
            Slider("tuner.wetworldlightstreakwarmth", 0f, 1f, () => _config.WetWorldLightStreakWarmth,
                value => _config.WetWorldLightStreakWarmth = value, "help.wetworldlightstreakwarmth");
            Slider("tuner.wetworldlightstreaktaper", 0f, 1f, () => _config.WetWorldLightStreakTaper,
                value => _config.WetWorldLightStreakTaper = value, "help.wetworldlightstreaktaper");
            Slider("tuner.wetworldlightpool", 0f, 1f, () => _config.WetWorldLightPool,
                value => _config.WetWorldLightPool = value, "help.wetworldlightpool");
            Slider("tuner.wetworlddrylamplight", 0f, 1f, () => _config.WetWorldDryLampLight,
                value => _config.WetWorldDryLampLight = value, "help.wetworlddrylamplight");
            EndDependsOn();
            Section("tuner.section.screendrops");
            Toggle("tuner.wetworldlensdrops", () => _config.WetWorldLensDrops, value => _config.WetWorldLensDrops = value, "help.wetworldlensdrops");
            // The edge haze is drawn by the same pass as the drops (ScreenEdgeDrops), so it
            // goes with the drops switch too, not only the drop size.
            DependsOn(() => _config.WetWorldLensDrops);
            Slider("tuner.wetworldlensdropsize", 0.5f, 2f, () => _config.WetWorldLensDropSize,
                value => _config.WetWorldLensDropSize = value, "help.wetworldlensdropsize");
            Slider("tuner.wetworldedgehaze", 0f, 2f, () => _config.WetWorldEdgeHaze,
                value => _config.WetWorldEdgeHaze = value, "help.wetworldedgehaze");
            Slider("tuner.wetworldlensdropliveliness", 0f, 1f, () => _config.WetWorldLensDropLiveliness,
                value => _config.WetWorldLensDropLiveliness = value, "help.wetworldlensdropliveliness");
            if (_config.WetWorldLensDrops && !HiddenBySection())
                BuildLensDropLooks();
            Slider("tuner.wetworldlensdropspread", 0f, 1f, () => _config.WetWorldLensDropSpread,
                value => _config.WetWorldLensDropSpread = value, "help.wetworldlensdropspread");
            EndDependsOn();
        }

        /// <summary>
        /// How the drops on the glass are drawn: plain beads, or one of the two lenses, named by the
        /// release each came in, with the one in use lit.
        /// </summary>
        /// <summary>
        /// When the soft parts of the effects are worked out at a lower resolution: three buttons,
        /// the one in use lit.
        /// </summary>
        private void BuildLighterSoftEffects()
        {
            if (HiddenBySection())
                return;
            Paragraph(_translate("tuner.lightersofteffects"));
            (LighterSoftEffects choice, string key)[] choices =
            [
                (LighterSoftEffects.Never, "never"),
                (LighterSoftEffects.WhenSlow, "whenslow"),
                (LighterSoftEffects.Always, "always"),
            ];
            int buttonWidth = (_contentColumnWidth - ChoiceGap * (choices.Length - 1)) / choices.Length;
            for (int i = 0; i < choices.Length; i++)
            {
                var (choice, key) = choices[i];
                var rect = new Rectangle(_contentCursorX + i * (buttonWidth + ChoiceGap), _contentCursorY, buttonWidth, Scaled(40));
                var button = Button(_translate($"tuner.lightersofteffects.{key}"), rect, () =>
                {
                    _config.LighterSoftEffects = choice; _onChange(); _onSave(); Reflow();
                });
                button.IsChosen = () => _config.LighterSoftEffects == choice;
                Help(rect, $"help.lightersofteffects.{key}");
            }
            _contentCursorY += Scaled(50);
        }

        private void BuildLensDropLooks()
        {
            (string key, bool refract, LensDropLook look)[] looks =
            [
                ("plain", false, LensDropLook.Lens227),
                ("lens227", true, LensDropLook.Lens227),
                ("lens231", true, LensDropLook.Lens231),
            ];
            int buttonWidth = (_contentColumnWidth - ChoiceGap * (looks.Length - 1)) / looks.Length;
            for (int i = 0; i < looks.Length; i++)
            {
                var (key, refract, look) = looks[i];
                var rect = new Rectangle(_contentCursorX + i * (buttonWidth + ChoiceGap), _contentCursorY, buttonWidth, Scaled(40));
                var button = Button(_translate($"tuner.lensdroplook.{key}"), rect, () =>
                {
                    _config.WetWorldLensDropsRefract = refract;
                    if (refract)
                        _config.WetWorldLensDropLook = look;
                    _onChange(); _onSave(); Reflow();
                });
                button.IsChosen = () => _config.WetWorldLensDropsRefract == refract
                    && (!refract || _config.WetWorldLensDropLook == look);
                Help(rect, $"help.lensdroplook.{key}");
            }
            _contentCursorY += Scaled(50);
        }

        /// <summary>Realistic or pixel puddles, two buttons with the one in use lit.</summary>
        private void BuildWetPuddleLooks()
        {
            (WetPuddleLook look, string key)[] looks = [(WetPuddleLook.Realistic, "realistic"), (WetPuddleLook.Pixel, "pixel")];
            int buttonWidth = (_contentColumnWidth - ChoiceGap * (looks.Length - 1)) / looks.Length;
            for (int i = 0; i < looks.Length; i++)
            {
                var (look, key) = looks[i];
                var rect = new Rectangle(_contentCursorX + i * (buttonWidth + ChoiceGap), _contentCursorY, buttonWidth, Scaled(40));
                var button = Button(_translate($"tuner.wetpuddlelook.{key}"), rect, () =>
                {
                    _config.WetWorldPuddleLook = look; _onChange(); _onSave(); Reflow();
                });
                button.IsChosen = () => _config.WetWorldPuddleLook == look;
                Help(rect, $"help.wetpuddlelook.{key}");
            }
            _contentCursorY += Scaled(50);
        }

        private void BuildLeafLooks()
        {
            // Under a folded heading, or a section the grouped layout is not showing, the buttons go
            // with the rest of the rows; they used to be drawn regardless.
            if (HiddenBySection(countsAsRow: false))
                return;
            (ParticleLeafLook look, string key)[] looks = [(ParticleLeafLook.Realistic, "realistic"), (ParticleLeafLook.Pixel, "pixel")];
            int buttonWidth = (_contentColumnWidth - ChoiceGap * (looks.Length - 1)) / looks.Length;
            for (int i = 0; i < looks.Length; i++)
            {
                var (look, key) = looks[i];
                var rect = new Rectangle(_contentCursorX + i * (buttonWidth + ChoiceGap), _contentCursorY, buttonWidth, Scaled(40));
                var button = Button(_translate($"tuner.particleleaflook.{key}"), rect, () =>
                {
                    _config.ParticleLeafLook = look; _onChange(); _onSave(); Reflow();
                });
                button.IsChosen = () => _config.ParticleLeafLook == look;
                Help(rect, $"help.particleleaflook.{key}");
            }
            _contentCursorY += Scaled(50);
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
            BuildLeafLooks();
            Emitter("tumbleweeds", () => _config.ParticleTumbleweeds, value => _config.ParticleTumbleweeds = value,
                () => _config.ParticleTumbleweedsAmount, value => _config.ParticleTumbleweedsAmount = value,
                () => _config.ParticleTumbleweedsSize, value => _config.ParticleTumbleweedsSize = value);
            Emitter("whirlwinds", () => _config.ParticleWhirlwinds, value => _config.ParticleWhirlwinds = value,
                () => _config.ParticleWhirlwindsAmount, value => _config.ParticleWhirlwindsAmount = value,
                () => _config.ParticleWhirlwindsSize, value => _config.ParticleWhirlwindsSize = value);
            Slider("tuner.particlewhirlwindleaves", 0f, 2f, () => _config.ParticleWhirlwindLeaves,
                value => _config.ParticleWhirlwindLeaves = value, "help.particlewhirlwindleaves",
                () => _config.ParticlesEnabled && _config.ParticleWhirlwinds);
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
            EndSection();
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
            Section("tuner.section.watersurface");
            Slider("tuner.waterstrength", 0f, 2f, () => _config.WaterStrength, value => _config.WaterStrength = value, "help.waterstrength");
            Slider("tuner.waterspeed", 0f, 3f, () => _config.WaterSpeed, value => _config.WaterSpeed = value);
            Slider("tuner.watersparkle", 0f, 1f, () => _config.WaterSparkle, value => _config.WaterSparkle = value, "help.watersparkle");
            Toggle("tuner.watersparkleday", () => _config.WaterSparkleByDay, value => _config.WaterSparkleByDay = value, "help.watersparkleday");
            Toggle("tuner.watersparklenight", () => _config.WaterSparkleAtNight, value => _config.WaterSparkleAtNight = value, "help.watersparklenight");
            Slider("tuner.watersparkledensity", 0.2f, 2f, () => _config.WaterSparkleDensity, value => _config.WaterSparkleDensity = value);
            Toggle("tuner.watersparklecloud", () => _config.WaterSparkleCloudShade, value => _config.WaterSparkleCloudShade = value, "help.watersparklecloud");
            Slider("tuner.waterglitterpath", 0f, 1f, () => _config.WaterGlitterPath, value => _config.WaterGlitterPath = value, "help.waterglitterpath");
            Toggle("tuner.watercaustics", () => _config.WaterCausticsEnabled, value => _config.WaterCausticsEnabled = value, "help.watercaustics");
            Slider("tuner.watercausticsstrength", 0f, 1f, () => _config.WaterCausticsStrength, value => _config.WaterCausticsStrength = value, null,
                () => _config.WaterEnabled && _config.WaterCausticsEnabled);
            Section("tuner.section.waterreflection");
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
            if (reflectionOn && !HiddenBySection())
            {
                (WaterReflectionModel model, string key)[] waterModels =
                [
                    (WaterReflectionModel.Modern, "modern"),
                    (WaterReflectionModel.Classic, "classic"),
                ];
                int modelButtonWidth = (_contentColumnWidth - ChoiceGap * (waterModels.Length - 1)) / waterModels.Length;
                for (int i = 0; i < waterModels.Length; i++)
                {
                    var (model, key) = waterModels[i];
                    var rect = new Rectangle(_contentCursorX + i * (modelButtonWidth + ChoiceGap), _contentCursorY, modelButtonWidth, Scaled(40));
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
                Section("tuner.section.watermodeldetail", fineTuning: true);
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
                if (reflectionOn && !HiddenBySection())
                {
                    (WaterReflectionStyle style, string key)[] reflectionStyles =
                    [
                        (WaterReflectionStyle.StillWater, "still"),
                        (WaterReflectionStyle.Natural, "natural"),
                        (WaterReflectionStyle.Choppy, "choppy"),
                    ];
                    int styleButtonWidth = (_contentColumnWidth - ChoiceGap * (reflectionStyles.Length - 1)) / reflectionStyles.Length;
                    for (int i = 0; i < reflectionStyles.Length; i++)
                    {
                        var (style, key) = reflectionStyles[i];
                        var rect = new Rectangle(_contentCursorX + i * (styleButtonWidth + ChoiceGap), _contentCursorY, styleButtonWidth, Scaled(40));
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
                Section("tuner.section.watermodeldetail", fineTuning: true);
            }
            Slider("tuner.waterreflectblur", 0f, 2f, () => _config.WaterReflectBlur,
                value => _config.WaterReflectBlur = value, "help.waterreflectblur");
            Slider("tuner.reflectdepth", 0.1f, 1.5f, () => _config.WaterReflectDepth,
                value => _config.WaterReflectDepth = value, "help.reflectdepth");
            Slider("tuner.reflectreach", 0.2f, 1f, () => _config.WaterReflectReach,
                value => _config.WaterReflectReach = value, "help.reflectreach");
            DependsOn(() => _config.WaterEnabled);
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
            Section("tuner.section.watermotion");
            Slider("tuner.waterwind", 0f, 2f, () => _config.WaterWind,
                value => _config.WaterWind = value, "help.waterwind");
            Toggle("tuner.waterriverflow", () => _config.WaterRiverFlowEnabled, value => _config.WaterRiverFlowEnabled = value,
                "help.waterriverflow", () => _config.WaterEnabled);
            Slider("tuner.watercurrent", 0f, 2f, () => _config.WaterCurrent,
                value => _config.WaterCurrent = value, "help.watercurrent",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled);
            Slider("tuner.waterrainswell", 0f, 2f, () => _config.WaterRiverRainSwell,
                value => _config.WaterRiverRainSwell = value, "help.waterrainswell",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f);
            Slider("tuner.waterriverfoam", 0f, 2f, () => _config.WaterRiverFoam,
                value => _config.WaterRiverFoam = value, "help.waterriverfoam",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f);
            Slider("tuner.waterseawaves", 0f, 2f, () => _config.WaterSeaWaves,
                value => _config.WaterSeaWaves = value, "help.waterseawaves");
            Slider("tuner.watersearainswell", 0f, 2f, () => _config.WaterSeaRainSwell,
                value => _config.WaterSeaRainSwell = value, "help.watersearainswell");
            // How the river reads as water: its own group, live only while a river flows.
            Section("tuner.section.riverlook");
            Slider("tuner.waterriverripplespeed", 1.0f, 3.0f, () => _config.WaterRiverRippleSpeed,
                value => _config.WaterRiverRippleSpeed = value, "help.waterriverripplespeed",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.05f);
            Slider("tuner.waterriverrenew", 0.0f, 1.0f, () => _config.WaterRiverRenew,
                value => _config.WaterRiverRenew = value, "help.waterriverrenew",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.05f);
            Slider("tuner.waterriverbankdrag", 0.0f, 1.0f, () => _config.WaterRiverBankDrag,
                value => _config.WaterRiverBankDrag = value, "help.waterriverbankdrag",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.05f);
            Slider("tuner.waterriverswirl", 0.0f, 1.0f, () => _config.WaterRiverSwirl,
                value => _config.WaterRiverSwirl = value, "help.waterriverswirl",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.05f);
            Slider("tuner.waterriverglitter", 0.0f, 1.0f, () => _config.WaterRiverGlitter,
                value => _config.WaterRiverGlitter = value, "help.waterriverglitter",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.05f);
            Slider("tuner.waterriverfoamstreak", 1.0f, 5.0f, () => _config.WaterRiverFoamStreak,
                value => _config.WaterRiverFoamStreak = value, "help.waterriverfoamstreak",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.1f);
            Slider("tuner.waterriverwaves", 0.0f, 2.0f, () => _config.WaterRiverWaves,
                value => _config.WaterRiverWaves = value, "help.waterriverwaves",
                () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f, step: 0.05f);
            Toggle("tuner.waterriverpixelstep", () => _config.WaterRiverPixelStep, value => _config.WaterRiverPixelStep = value,
                "help.waterriverpixelstep", () => _config.WaterEnabled && _config.WaterRiverFlowEnabled && _config.WaterCurrent > 0f);
            // Reach and fade rows used to sit here, and they were the wrong kind of control for a
            // panel you open to look at something. Both buy frames without changing how the water
            // looks, which is exactly the setting a player moves, sees nothing, and files as
            // broken. The performance preset sets them by name instead - Quality through Low spec
            // - and radiance_config still reaches them for an A/B.
            Section("tuner.section.waterindoors");
            Toggle("tuner.waterindoors", () => _config.WaterEffectIndoors, value => _config.WaterEffectIndoors = value, "help.waterindoors");

            // Per-room water switch: only in gated building interiors (not outdoors / real level water).
            GameLocation? location = Game1.currentLocation;
            if (_config.WaterEnabled && location != null && !location.IsOutdoors && !RenderPipeline.HasLevelWater(location) && !HiddenBySection()
                && MatchesSearch("tuner.waterhere", null))
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
            if (_config.TiltShiftEnabled && !HiddenBySection() && MatchesSearch("tuner.tiltradial", null))
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
            // The zoom is drawn for the whole picture, smoothed or not, so it sits above the switch.
            Toggle("config.zoomareafilter.name", () => _config.ZoomAreaFilter, value => _config.ZoomAreaFilter = value, "config.zoomareafilter.tooltip");
            Toggle("config.sheetupscale.name", () => _config.SheetUpscaleEnabled, value => _config.SheetUpscaleEnabled = value, "help.sheetupscale");
            DependsOn(() => _config.SheetUpscaleEnabled);
            Section("tuner.section.smoothingstyle");
            if (_config.SheetUpscaleEnabled && !HiddenBySection())
            {
                // Which look: two buttons, the one in use lit (see the GI model buttons).
                (SheetSmoothingStyle style, string key)[] styles = [(SheetSmoothingStyle.Scale2x, "scale2x"), (SheetSmoothingStyle.Soft4x, "soft4x")];
                int styleButtonWidth = (_contentColumnWidth - ChoiceGap * (styles.Length - 1)) / styles.Length;
                for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
                {
                    var (style, key) = styles[styleIndex];
                    var rect = new Rectangle(_contentCursorX + styleIndex * (styleButtonWidth + ChoiceGap), _contentCursorY, styleButtonWidth, Scaled(40));
                    var button = Button(_translate($"config.sheetupscalestyle.{key}"), rect, () => { _config.SheetUpscaleStyle = style; _onChange(); _onSave(); });
                    button.IsChosen = () => _config.SheetUpscaleStyle == style;
                    Help(rect, $"help.sheetupscalestyle.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            // The rule the soft look is made with, shown only while the soft look is the one in use.
            if (_config.SheetUpscaleEnabled && _config.SheetUpscaleStyle == SheetSmoothingStyle.Soft4x && !HiddenBySection())
            {
                _contentCursorY += Scaled(4);
                (SoftSmoothingKernel kernel, string key)[] kernels =
                [
                    (SoftSmoothingKernel.Xbr, "xbr"), (SoftSmoothingKernel.Mmpx, "mmpx"),
                    (SoftSmoothingKernel.MmpxEdgeGuarded, "mmpxedgeguarded"), (SoftSmoothingKernel.Epx, "epx"),
                ];
                int kernelButtonWidth = (_contentColumnWidth - ChoiceGap * (kernels.Length - 1)) / kernels.Length;
                for (int kernelIndex = 0; kernelIndex < kernels.Length; kernelIndex++)
                {
                    var (kernel, key) = kernels[kernelIndex];
                    var rect = new Rectangle(_contentCursorX + kernelIndex * (kernelButtonWidth + ChoiceGap), _contentCursorY, kernelButtonWidth, Scaled(40));
                    var button = Button(_translate($"config.sheetupscalekernel.{key}"), rect, () => { _config.SheetUpscaleSoftKernel = kernel; _onChange(); _onSave(); });
                    button.IsChosen = () => _config.SheetUpscaleSoftKernel == kernel;
                    Help(rect, $"help.sheetupscalekernel.{key}");
                }
                _contentCursorY += Scaled(50);
            }
            Slider("config.sheetupscalegradients.name", 0f, 1f, () => _config.SheetUpscaleGradientSmoothing,
                value => _config.SheetUpscaleGradientSmoothing = value, "config.sheetupscalegradients.tooltip",
                () => _config.SheetUpscaleEnabled && _config.SheetUpscaleStyle == SheetSmoothingStyle.Soft4x);
            Slider("config.sheetupscalesteady.name", 0f, 1f, () => _config.SheetUpscaleSteadyRead,
                value => _config.SheetUpscaleSteadyRead = value, "config.sheetupscalesteady.tooltip",
                () => _config.SheetUpscaleEnabled && _config.SheetUpscaleStyle == SheetSmoothingStyle.Soft4x);
            Section("tuner.section.smoothingfamilies");
            // Each family's smoothness names the master switch as well as its own: a row's own
            // condition replaces the tab's DependsOn rather than adding to it, so with Smooth art
            // off the family switches went and five sliders all called Smoothness stayed behind.
            Toggle("config.sheetupscaleworld.name", () => _config.SheetUpscaleWorld,
                value => _config.SheetUpscaleWorld = value, "config.sheetupscaleworld.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessWorld,
                value => _config.SheetUpscaleSmoothnessWorld = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleEnabled && _config.SheetUpscaleWorld);
            FamilyKernelButton(() => _config.SheetUpscaleKernelWorld, value => _config.SheetUpscaleKernelWorld = value,
                () => _config.SheetUpscaleWorld);
            Toggle("config.sheetupscalecharacters.name", () => _config.SheetUpscaleCharacters,
                value => _config.SheetUpscaleCharacters = value, "config.sheetupscalecharacters.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessCharacters,
                value => _config.SheetUpscaleSmoothnessCharacters = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleEnabled && _config.SheetUpscaleCharacters);
            FamilyKernelButton(() => _config.SheetUpscaleKernelCharacters, value => _config.SheetUpscaleKernelCharacters = value,
                () => _config.SheetUpscaleCharacters);
            Toggle("config.sheetupscaleitems.name", () => _config.SheetUpscaleItems,
                value => _config.SheetUpscaleItems = value, "config.sheetupscaleitems.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessItems,
                value => _config.SheetUpscaleSmoothnessItems = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleEnabled && _config.SheetUpscaleItems);
            FamilyKernelButton(() => _config.SheetUpscaleKernelItems, value => _config.SheetUpscaleKernelItems = value,
                () => _config.SheetUpscaleItems);
            Toggle("config.sheetupscaleportraits.name", () => _config.SheetUpscalePortraits,
                value => _config.SheetUpscalePortraits = value, "config.sheetupscaleportraits.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessPortraits,
                value => _config.SheetUpscaleSmoothnessPortraits = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleEnabled && _config.SheetUpscalePortraits);
            FamilyKernelButton(() => _config.SheetUpscaleKernelPortraits, value => _config.SheetUpscaleKernelPortraits = value,
                () => _config.SheetUpscalePortraits);
            Toggle("config.sheetupscaleinterface.name", () => _config.SheetUpscaleInterface,
                value => _config.SheetUpscaleInterface = value, "config.sheetupscaleinterface.tooltip");
            Slider("config.sheetupscalesmoothness.name", 0f, 1f, () => _config.SheetUpscaleSmoothnessInterface,
                value => _config.SheetUpscaleSmoothnessInterface = value, "config.sheetupscalesmoothness.tooltip", () => _config.SheetUpscaleEnabled && _config.SheetUpscaleInterface);
            FamilyKernelButton(() => _config.SheetUpscaleKernelInterface, value => _config.SheetUpscaleKernelInterface = value,
                () => _config.SheetUpscaleInterface);
            EndDependsOn();
        }

        /// <summary>One art family's own rule for the soft look, a button that steps through the
        /// choices: the rule chosen for all, then each rule. Shown only while Soft 4x is the look and
        /// the family is smoothed at all.</summary>
        private void FamilyKernelButton(Func<FamilyKernelChoice> getValue, Action<FamilyKernelChoice> setValue, Func<bool> familyOn)
        {
            if (!_config.SheetUpscaleEnabled || _config.SheetUpscaleStyle != SheetSmoothingStyle.Soft4x || !familyOn() || HiddenBySection())
                return;
            FamilyKernelChoice current = getValue();
            string label = _translate("config.sheetupscalekernelfamily.name") + ": "
                         + _translate($"config.sheetupscalekernelfamily.{current.ToString().ToLowerInvariant()}");
            var rect = new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(36));
            Button(label, rect, () =>
            {
                int count = Enum.GetValues(typeof(FamilyKernelChoice)).Length;
                setValue((FamilyKernelChoice)(((int)getValue() + 1) % count));
                _onChange();
                _onSave();
                Reflow();
            });
            Help(rect, "config.sheetupscalekernelfamily.tooltip");
            _contentCursorY += Scaled(44);
        }

        private void BuildPerformance()
        {
            // Quality presets, kept apart from the look presets on the first tab: these change
            // what the picture costs, never what it looks like.
            Section("config.perfpreset.section");
            if (!HiddenBySection())
            {
                (PerfPreset preset, string key)[] perfPresets =
                [
                    (PerfPreset.Quality, "quality"), (PerfPreset.Balanced, "balanced"),
                    (PerfPreset.Performance, "performance"), (PerfPreset.LowSpec, "lowspec")
                ];
                int perfButtonWidth = (_contentColumnWidth - ChoiceGap * (perfPresets.Length - 1)) / perfPresets.Length;
                for (int i = 0; i < perfPresets.Length; i++)
                {
                    var (preset, key) = perfPresets[i];
                    var rect = new Rectangle(_contentCursorX + i * (perfButtonWidth + ChoiceGap), _contentCursorY, perfButtonWidth, RowPitch);
                    var perfButton = new TunerTextButton(_translate($"config.perfpreset.{key}"), rect, () =>
                    {
                        _config.ApplyPerfPreset(preset); _onChange(); _onSave(); Reflow();
                    })
                    {
                        IsChosen = () => _config.ActivePerfPreset == preset,
                        TextScale = _uiScale,
                    };
                    _buttons.Add(perfButton);
            }
            _contentCursorY += RowPitch + Scaled(12);
            }

            Slider("config.renderscale.name", 0.5f, 1f, () => _config.RenderScale, value => _config.RenderScale = value);
            // Directly under the slider it steers, because it is that slider becoming automatic
            // rather than a separate feature, and because the slider is then read as the ceiling.
            Toggle("config.renderscaleauto.name", () => _config.RenderScaleAuto,
                value => _config.RenderScaleAuto = value, "help.renderscaleauto");
            BuildLighterSoftEffects();
            Slider("config.rendersharpness.name", 0f, 2f, () => _config.RenderSharpness, value => _config.RenderSharpness = value);
            Section("tuner.section.perfadvanced", fineTuning: true);
            Toggle("config.limitsamplerslots.name", () => _config.LimitSamplerSlots,
                value => _config.LimitSamplerSlots = value, "help.limitsamplerslots");

            // Neither of these is saved to config, and that is deliberate: they are instruments,
            // not settings. A diagnostic overlay that survives a restart is one somebody forgets
            // they left on, and the GPU column reaches into the graphics driver, which is not a
            // state to inherit silently from a session three days ago.
            Section("tuner.section.perfreadout");
            Toggle("tuner.perfhud", () => PerfHud.Visible, value => PerfHud.Visible = value, "help.perfhud", savedSetting: false);
            Toggle("tuner.gputime", () => GpuTimer.Ready, GpuTimer.SetWanted, "help.gputime", savedSetting: false);
            if (PerfHud.Visible && !GpuTimer.Ready && GpuTimer.Status != "off")
                Info(() => GpuTimer.Status);

            // Measure this machine instead of guessing at it.
            Section("config.bench.section");
            if (HiddenBySection())
                return;
            _buttons.Add(new TunerTextButton(_translate("config.bench.run"),
                new Rectangle(_contentCursorX, _contentCursorY, Math.Min(Scaled(300), _contentColumnWidth), RowPitch), () =>
                {
                    RenderPipeline.Current?.StartBenchmark(_config);
                    Reflow();
                })
                { TextScale = _uiScale });
            _contentCursorY += RowPitch + Scaled(10);

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
                    new Rectangle(_contentCursorX, _contentCursorY, Math.Min(Scaled(300), _contentColumnWidth), RowPitch), () =>
                    {
                        _config.RenderScale = RenderPipeline.BenchSuggestedScale;
                        _config.Clamp(); _onChange(); _onSave(); Reflow();
                    })
                    { TextScale = _uiScale });
                _contentCursorY += RowPitch + Scaled(10);
            }
        }

        private void BuildCamera()
        {
            Toggle("config.mapshot.name", () => _config.MapScreenshotEffects, value => _config.MapScreenshotEffects = value, "config.mapshot.tooltip");
            // Was reachable from GMCM only, which meant the two menus disagreed about what
            // this mod even contains. Stood down for now (see CameraSmoother.Available): the rows stay,
            // greyed, under a line that says why, so nobody looks for a switch that went missing.
            if (!CameraSmoother.Available)
            {
                Paragraph(_translate("config.camera.disabled"));
                _contentCursorY += Scaled(6);
            }
            if (MatchesSearch("config.camera.mode.smooth", "config.camera.mode.tooltip"))
            {
                _toggles.Add(new TunerToggle(_translate("config.camera.mode.smooth"),
                    new Rectangle(_contentCursorX, _contentCursorY, _contentColumnWidth, Scaled(38)),
                    () => _config.CameraMode == CameraMode.Smooth,
                    value => _config.CameraMode = value ? CameraMode.Smooth : CameraMode.Off)
                { TextScale = _uiScale, Enabled = () => CameraSmoother.Available });
                _contentCursorY += Scaled(44);
            }
            Slider("config.smoothcam.speed.name", 0.05f, 1f, () => _config.CameraFollowSpeed, value => _config.CameraFollowSpeed = value, null,
                () => CameraSmoother.Available && _config.CameraMode == CameraMode.Smooth);
        }

        private void BuildDiagnostics()
        {
            if (GlassPanel)
                Toggle("tuner.glasslight", () => LightGlass,
                    value => _config.TunerPanel = value ? PanelGlassLight : PanelGlass, "help.glasslight");
            if (GlassPanel)
                Slider("tuner.glassshade", 0.4f, 0.95f, () => _config.TunerGlassShade, value => _config.TunerGlassShade = value, "help.glassshade");
            Toggle("config.debug.name", () => _config.DebugLogging, value => _config.DebugLogging = value);
        }

        internal const string PanelGlass = "Glass";

        internal const string PanelGlassLight = "GlassLight";

        /// <summary>Two looks, not four: the new menu is the grouped layout on the glass, and Classic is
        /// the seventeen tabs in the wooden box, as they always were. Choosing the layout and the panel
        /// apart was one choice too many (the author, 21 Oct 2026). TunerPanel now only remembers
        /// whether the glass is the pale one.</summary>
        private bool GlassPanel => Grouped;

        private bool LightGlass => Grouped && _config.TunerPanel == PanelGlassLight;

        /// <summary>The panel itself: the game's wooden box, or frosted glass over the world.</summary>
        /// <remarks>
        /// The glass is the finished frame, shrunk sixteen times and stretched back over the panel
        /// (RenderPipeline.KeepTunerBackdrop), under a warm pale wash light enough for the menu's
        /// dark brown ink. With the effects off there is no frame to blur, and the wash alone is
        /// drawn a little thicker, so the panel still reads as glass and the text stays legible.
        /// </remarks>
        private void DrawPanel(SpriteBatch spriteBatch, float slideOffsetX, Matrix? slideMatrix)
        {
            if (!GlassPanel)
            {
                drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, drawShadow: true);
                return;
            }
            var panel = new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height);
            int radius = Scaled(20);
            TunerTheme.Plate(spriteBatch, new Rectangle(panel.X + Scaled(4), panel.Y + Scaled(10), panel.Width, panel.Height), radius, Color.Black * 0.3f);
            Texture2D? backdrop = RenderPipeline.TunerBackdropNow;
            if (backdrop != null)
            {
                int screenWidth = Math.Max(1, Game1.uiViewport.Width), screenHeight = Math.Max(1, Game1.uiViewport.Height);
                Rectangle Seen(Rectangle part) => new(
                    (int)((part.X + slideOffsetX) * backdrop.Width / screenWidth), part.Y * backdrop.Height / screenHeight,
                    Math.Max(1, part.Width * backdrop.Width / screenWidth), Math.Max(1, part.Height * backdrop.Height / screenHeight));
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None,
                    RasterizerState.CullCounterClockwise, null, slideMatrix);
                // The blurred world cut to the panel's round shape, one row at a time across the
                // corners: a picture has no round corners of its own, and a corner tinted without
                // the world under it read as a solid square on the light glass.
                var body = new Rectangle(panel.X, panel.Y + radius, panel.Width, panel.Height - radius * 2);
                spriteBatch.Draw(backdrop, body, Seen(body), Color.White);
                for (int row = 0; row < radius; row++)
                {
                    float rise = radius - row - 0.5f;
                    int inset = (int)MathF.Round(radius - MathF.Sqrt(Math.Max(0f, radius * radius - rise * rise)));
                    foreach (int y in new[] { panel.Y + row, panel.Bottom - 1 - row })
                    {
                        var strip = new Rectangle(panel.X + inset, y, panel.Width - inset * 2, 1);
                        spriteBatch.Draw(backdrop, strip, Seen(strip), Color.White);
                    }
                }
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, TextSampler, DepthStencilState.None,
                    RasterizerState.CullCounterClockwise, null, slideMatrix);
            }
            TunerTheme.Plate(spriteBatch, panel, radius, TunerTheme.PanelGlass * (backdrop != null ? TunerTheme.GlassCover(_config.TunerGlassShade) : 0.94f));
            TunerTheme.PlateRim(spriteBatch, panel, radius, TunerTheme.Rim);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(panel.X + radius, panel.Y + 1, panel.Width - radius * 2, 1), TunerTheme.Overlay * 0.08f);
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
            _hoverPicture = null;
            _hoveredSection = null;
            if (Sliding)
                return;
            if (y < _bodyTop || y > _bodyBottom || x < _contentX)
                return;
            string? help = null;
            foreach (var (row, text) in _help)
            {
                if (Visible(row) && row.Contains(x, y + _scroll))
                {
                    help = text;
                    break;
                }
            }
            foreach (var (row, picture) in _pictureOfRow)
            {
                if (Visible(row) && row.Contains(x, y + _scroll))
                {
                    _hoverPicture = picture;
                    break;
                }
            }
            string? reset = ResetNoteAt(x, y + _scroll);
            _hoveredSection = null;
            foreach (SectionHeader header in _sectionHeaders)
            {
                if (Visible(header.Row) && header.Row.Contains(x, y + _scroll))
                {
                    help = _translate("tuner.section.foldhint");
                    _hoveredSection = header;
                    _hoverPicture = PictureFor(header.Key);
                }
            }
            _hoverText = CombinedHover(help, reset);
        }

        /// <summary>The line a changed row adds to its hover note: that it differs, from what, and
        /// that a right click puts it back.</summary>
        private string? ResetNoteAt(int x, int contentY)
        {
            foreach (TunerSlider slider in _sliders)
                if (Visible(slider.RowBounds) && slider.RowBounds.Contains(x, contentY) && slider.DiffersFromDefault && slider.IsEnabled)
                    return _translate("tuner.resethover").Replace("{{default}}", slider.DefaultValue!.Value.ToString("0.00"));
            foreach (TunerToggle toggle in _toggles)
                if (Visible(toggle.Row) && toggle.Row.Contains(x, contentY) && toggle.DiffersFromDefault && toggle.IsEnabled)
                    return _translate("tuner.resethover.plain");
            foreach (TunerCompass compass in _compasses)
                if (Visible(compass.Row) && compass.Row.Contains(x, contentY) && compass.DiffersFromDefault && compass.IsEnabled)
                    return _translate("tuner.resethover").Replace("{{default}}", compass.DefaultValue!.Value.ToString("0"));
            return null;
        }

        private string? _hoverHelpPart, _hoverResetPart, _hoverCombined;

        /// <summary>The note and the reset line together, kept while neither changes: the hover runs
        /// every frame and the wrap below is keyed on the very same string object.</summary>
        private string? CombinedHover(string? help, string? reset)
        {
            if (reset == null)
                return help;
            if (!ReferenceEquals(help, _hoverHelpPart) || reset != _hoverResetPart)
            {
                _hoverHelpPart = help;
                _hoverResetPart = reset;
                _hoverCombined = help == null ? reset : help + "\n\n" + reset;
            }
            return _hoverCombined;
        }


        public override void receiveScrollWheelAction(int direction)
        {
            if (Sliding)
                return;
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
            {
                _scroll = Math.Clamp(_scroll - Math.Sign(direction) * 48, 0, _maxScroll);
                RememberScroll();
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (Sliding)
                return;
            // The close button slides the panel out too, instead of the base class removing it at once.
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                SlideClosed();
                return;
            }
            if (LayoutPromptShown)
            {
                if (_promptNew.Contains(x, y))
                    PickFromLayoutPrompt(grouped: true);
                else if (_promptOld.Contains(x, y))
                    PickFromLayoutPrompt(grouped: false);
                return;
            }
            if (_fineTuningButton != null && _fineTuningButton.Bounds.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                _fineTuningButton.OnClick();
                return;
            }
            if (_layoutButton != null && _layoutButton.Bounds.Contains(x, y))
            {
                _layoutButton.OnClick();
                return;
            }
            if (SearchFieldNow().Contains(x, y))
            {
                if (!TypingSearch)
                {
                    Game1.playSound("smallSelect");
                    StartTypingSearch();
                }
                return;
            }
            // A click anywhere else puts the field down; the results stay.
            StopTypingSearch();
            // Picking a side builds the menu again, which empties this list: leave at once.
            foreach (var (_, options) in _headerPills)
                foreach (PillOption option in options)
                    if (option.Bounds.Contains(x, y))
                    {
                        if (!option.IsChosen())
                        {
                            Game1.playSound("smallSelect");
                            option.Pick();
                        }
                        return;
                    }

            // Rail buttons are fixed (no scroll offset) and always clickable.
            foreach (var (button, tabIndex, _) in _tabRailButtons)
                if (button.Bounds.Contains(x, y)) { if (!RailActive(tabIndex)) Game1.playSound("smallSelect"); button.OnClick(); return; }

            if (y >= _bodyTop && y <= _bodyBottom && x >= _contentX)
            {
                foreach (SectionHeader header in _sectionHeaders)
                {
                    if (Visible(header.Row) && header.Row.Contains(x, y + _scroll))
                    {
                        if (!_config.TunerFoldedSections.Remove(header.Key))
                            _config.TunerFoldedSections.Add(header.Key);
                        Game1.playSound("shwip");
                        _onSave();
                        Reflow();
                        return;
                    }
                }
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

        /// <summary>The screen size the panel was last laid out for. The game tells a menu when the
        /// window is resized, but not when the zoom or interface scale changes the room a menu
        /// has, so the panel checks every update and lays itself out again when it differs.</summary>
        private Point _laidOutForScreen;

        public override void update(GameTime time)
        {
            base.update(time);
            var screen = new Point(Game1.uiViewport.Width, Game1.uiViewport.Height);
            if (screen != _laidOutForScreen)
            {
                bool first = _laidOutForScreen == Point.Zero;
                _laidOutForScreen = screen;
                if (!first)
                    Reflow();
            }
            float seconds = (float)time.ElapsedGameTime.TotalSeconds;
            FollowTypedSearch(seconds);
            if (_closing)
            {
                _slide -= seconds / CloseSlideSeconds;
                if (_slide <= 0f)
                {
                    _slide = 0f;
                    _closing = false;
                    exitThisMenu(playSound: false);
                    return;
                }
            }
            else if (_slide < 1f)
            {
                _slide = Math.Min(1f, _slide + seconds / OpenSlideSeconds);
            }
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
            if (Sliding)
                return;
            if (y < _bodyTop || y > _bodyBottom) return;
            foreach (var chip in _chips)
                if (Visible(chip.Load.Bounds) && chip.Load.Bounds.Contains(x, y + _scroll)) { DeleteChip(chip); return; }
            // A right click on a changed row puts it back to its default.
            int contentY = y + _scroll;
            foreach (TunerSlider slider in _sliders)
                if (Visible(slider.RowBounds) && slider.RowBounds.Contains(x, contentY) && slider.DiffersFromDefault && slider.IsEnabled)
                {
                    slider.ResetToDefault(); ResetDone(); return;
                }
            foreach (TunerToggle toggle in _toggles)
                if (Visible(toggle.Row) && toggle.Row.Contains(x, contentY) && toggle.DiffersFromDefault && toggle.IsEnabled)
                {
                    toggle.ResetToDefault(); ResetDone(); return;
                }
            foreach (TunerCompass compass in _compasses)
                if (Visible(compass.Row) && compass.Row.Contains(x, contentY) && compass.DiffersFromDefault && compass.IsEnabled)
                {
                    compass.ResetToDefault(); ResetDone(); return;
                }
        }

        private void ResetDone()
        {
            Game1.playSound("drumkit6");
            _onChange();
            _onSave();
            // A switch put back can bring rows with it or take them away.
            Reflow();
        }

        /// <summary>The look the player pressed the red X on, waiting for them to say yes. The X
        /// used to delete at once, with no way back, and it sits a few pixels from the chip that
        /// loads the look: one slip lost a look that may have taken an evening to tune. The question
        /// goes under the chips, the way Reset all asks, and moving to another tab forgets it.</summary>
        private static NamedProfile? _lookAskedToDelete;

        private void DeleteChip(TunerChip chip)
        {
            _lookAskedToDelete = chip.Profile;
            Game1.playSound("smallSelect"); Reflow();
        }

        private void BuildDeleteLookQuestion(NamedProfile look)
        {
            _contentCursorY += RowPitch;
            Paragraph(_translate("tuner.deletelook.ask").Replace("{{name}}", ShownLookName(look)));
            int gap = 8, half = (_contentColumnWidth - gap) / 2;
            Button(_translate("tuner.deletelook.confirm"), new Rectangle(_contentCursorX, _contentCursorY, half, ButtonHeight), () =>
            {
                _config.SavedProfiles.Remove(look);
                _lookAskedToDelete = null;
                _onSave(); Game1.playSound("trashcan"); Reflow();
            });
            Button(_translate("tuner.deletelook.cancel"), new Rectangle(_contentCursorX + half + gap, _contentCursorY, half, ButtonHeight),
                () => { _lookAskedToDelete = null; Game1.playSound("smallSelect"); Reflow(); });
        }

        public override void leftClickHeld(int x, int y)
        {
            if (Sliding)
                return;
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
            CloseOnce();
            base.cleanupBeforeExit();
        }

        /// <summary>The game took the panel away itself: a cutscene started, the player passed out,
        /// something set the active menu without asking. Its setter does not call
        /// <see cref="cleanupBeforeExit"/>, so a code on trial stayed in the live settings with no
        /// chip of the look it replaced, and the next config write (the on and off key) made it
        /// permanent. Called from the menu change event; does nothing if the panel closed itself.</summary>
        internal void ClosedByTheGame() => CloseOnce();

        private bool _closed;

        private void CloseOnce()
        {
            if (_closed)
                return;
            _closed = true;
            // A code describes the settings it was taken from, and those move the moment the panel
            // is open again. Showing yesterday's code beside today's dials would be a lie in text.
            _sharedCode = "";
            _sharedCodeIsEmpty = false;
            CloseTrialWithThePanel();
            _lookBeforeCode = null;
            _codeProblem = "";
            _codeProblemDetail = "";
            // A search is a moment's question; the panel opens on its page again next time.
            _searchQuery = null;
            RenderPipeline.TunerBackdropWanted = false;
            TunerTheme.Glass = false;
            RememberScroll();
            if (_activeTab >= 0 && _activeTab < PageCount)
                _config.TunerLastTab = PageKey(_activeTab);
            StopTypingSearch();
            _onSave();
        }

        /// <summary>A small solid triangle in steps of <paramref name="unit"/>: pointing down for an open
        /// group, pointing right for a folded one.</summary>
        private static void DrawDisclosureArrow(SpriteBatch spriteBatch, int x, int y, int unit, bool folded, float inkStrength)
        {
            var colour = TunerTheme.Ink * inkStrength;
            // On the glass, the mock's smooth chevron in the same square the triangle fills.
            if (TunerTheme.Glass && GlassIcons() is { } icons && GlassIconNamed(folded ? "chevron-right" : "chevron-down") is int chevron)
            {
                int size = unit * 9;
                spriteBatch.Draw(icons, new Rectangle(x + unit * 7 / 2 - size / 2, y + unit * 7 / 2 - size / 2, size, size),
                    GlassIconSource(chevron, size), colour);
                return;
            }
            for (int step = 0; step < 4; step++)
            {
                int length = (7 - step * 2) * unit;
                var bar = folded
                    ? new Rectangle(x + step * unit, y + step * unit, unit, length)
                    : new Rectangle(x + step * unit, y + step * unit, length, unit);
                spriteBatch.Draw(Game1.staminaRect, bar, colour);
            }
        }

        private void DrawScrollShade(SpriteBatch spriteBatch, int edgeY, bool fromTop, float strength)
        {
            if (strength <= 0f)
                return;
            const int bands = 8;
            int bandHeight = Math.Max(1, Scaled(2));
            for (int band = 0; band < bands; band++)
            {
                float fade = (bands - band) / (float)bands;
                int y = fromTop ? edgeY + band * bandHeight : edgeY - (band + 1) * bandHeight;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(_contentX, y, ContentWidth, bandHeight),
                    TunerTheme.Wash * (0.22f * fade * fade * strength));
            }
        }

        private string? _footerSplitFrom;
        private (string Key, string Action)[] _footerParts = [];

        /// <summary>The line of keys at the foot of the glass, spread across the middle with each key
        /// in a small box, as on the mock. The hint's own words are split at the bullets, and each
        /// piece at its colon (or the equals sign an older translation still uses).</summary>
        private void DrawGlassFooter(SpriteBatch spriteBatch)
        {
            string hint = _translate("tuner.hint");
            if (hint != _footerSplitFrom)
            {
                _footerSplitFrom = hint;
                _footerParts = [.. hint.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(piece =>
                {
                    int split = piece.IndexOfAny([':', '=']);
                    return split > 0 ? (piece[..split].Trim(), piece[(split + 1)..].Trim()) : ("", piece);
                })];
            }
            int padding = Scaled(5), keyGap = Scaled(6), partGap = Scaled(16);
            float scale = 0.8f * _uiScale;
            float Total() => _footerParts.Sum(part =>
                (part.Key.Length > 0 ? TunerText.Measure(part.Key).X * scale + padding * 2 + keyGap : 0f) + TunerText.Measure(part.Action).X * scale)
                + partGap * Math.Max(0, _footerParts.Length - 1);
            float total = Total();
            float room = width - Scaled(40);
            if (total > room)
            {
                scale *= room / total;
                total = Total();
            }
            float x = xPositionOnScreen + (width - total) / 2f;
            float middle = yPositionOnScreen + height - Scaled(20);
            // Each piece centred on the row by its own height: Thai is drawn in the game's font, which
            // is taller than the glass typeface, and boxes sized to an English letter left it hanging
            // out of them.
            float Top(string piece) => middle - TunerText.Measure(piece).Y * scale / 2f;
            foreach (var (key, action) in _footerParts)
            {
                if (key.Length > 0)
                {
                    Vector2 keySize = TunerText.Measure(key) * scale;
                    int keyWidth = (int)keySize.X + padding * 2;
                    var keyBox = new Rectangle((int)x, (int)(middle - keySize.Y / 2f - Scaled(3)), keyWidth, (int)keySize.Y + Scaled(6));
                    TunerTheme.Plate(spriteBatch, keyBox, Scaled(5), TunerTheme.Overlay * 0.08f);
                    TunerTheme.PlateRim(spriteBatch, keyBox, Scaled(5), TunerTheme.Rim);
                    TunerText.Draw(spriteBatch, key, new Vector2(x + padding, Top(key) + 1f), TunerTheme.InkDim, scale);
                    x += keyWidth + keyGap;
                }
                TunerText.Draw(spriteBatch, action, new Vector2(x, Top(action) + 1f), TunerTheme.InkFaint, scale);
                x += TunerText.Measure(action).X * scale + partGap;
            }
        }

        /// <summary>
        /// On the glass, the edge where the list runs on past the view gets round corners the size of
        /// a card's, as on the mock, instead of a straight cut through the cards. The corner outside
        /// the curve gets the glass laid back over it, one pixel row at a time: the blurred world and
        /// its tint, read the way <see cref="DrawPanel"/> reads them. Only an edge with something cut
        /// off at it is rounded, so the top of a page that has not been scrolled keeps its corners.
        /// </summary>
        private void RoundTheScrollEdges(SpriteBatch spriteBatch, float slideOffsetX)
        {
            int left = _contentCursorX - Scaled(6), right = _contentCursorX + _contentColumnWidth + Scaled(6);
            int radius = Scaled(14);
            Texture2D? backdrop = RenderPipeline.TunerBackdropNow;
            int screenWidth = Math.Max(1, Game1.uiViewport.Width), screenHeight = Math.Max(1, Game1.uiViewport.Height);
            void Glass(Rectangle part)
            {
                if (part.Width <= 0)
                    return;
                if (backdrop != null)
                {
                    spriteBatch.Draw(backdrop, part, new Rectangle(
                        (int)((part.X + slideOffsetX) * backdrop.Width / screenWidth), part.Y * backdrop.Height / screenHeight,
                        Math.Max(1, part.Width * backdrop.Width / screenWidth), Math.Max(1, part.Height * backdrop.Height / screenHeight)), Color.White);
                    spriteBatch.Draw(Game1.staminaRect, part, TunerTheme.PanelGlass * TunerTheme.GlassCover(_config.TunerGlassShade));
                }
                else
                    spriteBatch.Draw(Game1.staminaRect, part, TunerTheme.PanelGlass * 0.94f);
            }
            for (int row = 0; row < radius; row++)
            {
                float rise = radius - row - 0.5f;
                int inset = (int)MathF.Round(radius - MathF.Sqrt(Math.Max(0f, radius * radius - rise * rise)));
                if (inset <= 0)
                    continue;
                if (_scroll > 0)
                {
                    Glass(new Rectangle(left, _bodyTop + row, inset, 1));
                    Glass(new Rectangle(right - inset, _bodyTop + row, inset, 1));
                }
                if (_scroll < _maxScroll)
                {
                    Glass(new Rectangle(left, _bodyBottom - 1 - row, inset, 1));
                    Glass(new Rectangle(right - inset, _bodyBottom - 1 - row, inset, 1));
                }
            }
        }

        /// <summary>A soft band under the setting row the pointer is on, as on the mock, so it is
        /// plain which row a click or a right-click acts on.</summary>
        private void DrawHoveredRow(SpriteBatch spriteBatch, int scrollOffsetY)
        {
            int mouseX = Game1.getMouseX(), mouseY = Game1.getMouseY();
            if (mouseY < _bodyTop || mouseY > _bodyBottom || mouseX < _contentX)
                return;
            int contentY = mouseY + _scroll;
            Rectangle? row = null;
            foreach (var toggle in _toggles)
                if (toggle.IsEnabled && toggle.Row.Contains(mouseX, contentY)) { row = toggle.Row; break; }
            if (row == null)
                foreach (var slider in _sliders)
                    if (slider.IsEnabled && slider.RowBounds.Contains(mouseX, contentY)) { row = slider.RowBounds; break; }
            if (row is not Rectangle hovered)
                return;
            var band = new Rectangle(_contentCursorX - Scaled(4), hovered.Y + scrollOffsetY - Scaled(2), _contentColumnWidth + Scaled(8), hovered.Height + Scaled(4));
            TunerTheme.Plate(spriteBatch, band, Scaled(8), TunerTheme.Overlay * 0.1f);
        }

        private void DrawChangedDot(SpriteBatch spriteBatch, Vector2 labelEnd)
        {
            int size = Math.Max(4, Scaled(6));
            TunerTheme.Disc(spriteBatch, new Vector2(labelEnd.X + Scaled(6) + size / 2f, labelEnd.Y), size, TunerTheme.Changed);
        }

        private void DrawChangedMark(SpriteBatch spriteBatch, int rowX, int markY)
        {
            int size = Math.Max(4, Scaled(6));
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(rowX - Scaled(11), markY, size, size), TunerTheme.Changed);
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            // While sliding, every batch this draw opens carries the same sideways move, and the one
            // scissor (the scrolling column) moves with it. In place, the batches are the ones this
            // menu always used.
            float slideOffsetX = SlideOffsetX();
            Matrix? slideMatrix = slideOffsetX != 0f ? Matrix.CreateTranslation(slideOffsetX, 0f, 0f) : null;
            TunerTheme.Glass = GlassPanel;
            TunerTheme.Light = LightGlass;
            if (slideMatrix != null || TunerTheme.Glass)
            {
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, TextSampler, DepthStencilState.None,
                    RasterizerState.CullCounterClockwise, null, slideMatrix);
            }
            int innerWidth = width - Scaled(56);

            RenderPipeline.TunerBackdropWanted = GlassPanel;
            TunerTheme.Glass = GlassPanel;
            DrawPanel(spriteBatch, slideOffsetX, slideMatrix);
            if (TunerTheme.Glass)
                DrawGlassHeader(spriteBatch);
            else
            {
                int titleRoom = innerWidth - Scaled(40) - (_fineTuningButton?.Bounds.Width + Scaled(12) ?? 0) - (_layoutButton?.Bounds.Width + Scaled(10) ?? 0);
                TunerText.DrawFit(spriteBatch, _translate("tuner.title"), new Vector2(xPositionOnScreen + Scaled(28), yPositionOnScreen + Scaled(22)), titleRoom, TunerTheme.Ink, _uiScale);
                _fineTuningButton?.Draw(spriteBatch, 0, _fineTuningButton.IsChosen?.Invoke() == true);
                _layoutButton?.Draw(spriteBatch, 0);
            }

            // Rail divider
            int dividerX = xPositionOnScreen + RailWidth;
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(dividerX, _bodyTop - 6, 2, _bodyBottom - _bodyTop + 12), Color.Black * 0.2f);

            // Rail buttons (active highlighted)
            foreach (var (button, tabIndex, icon) in _tabRailButtons)
            {
                if (RailActive(tabIndex))
                {
                    // The old highlight was a warm orange wash over a warm orange menu, which
                    // is to say it was invisible: on screen the chosen tab looked exactly like
                    // the fourteen that were not. A DARK wash reads against this panel, and the
                    // bar down the left edge says which one it is even at a glance.
                    var lit = new Rectangle(button.Bounds.X - Scaled(4), button.Bounds.Y - Scaled(2), button.Bounds.Width + Scaled(8), button.Bounds.Height + Scaled(4));
                    if (TunerTheme.Glass)
                        TunerTheme.Plate(spriteBatch, lit, Scaled(12), TunerTheme.Accent * 0.2f);
                    else
                        spriteBatch.Draw(Game1.staminaRect, lit, TunerTheme.Wash * 0.34f);
                    spriteBatch.Draw(Game1.staminaRect,
                        new Rectangle(button.Bounds.X - Scaled(4), button.Bounds.Y - Scaled(2), Scaled(5), button.Bounds.Height + Scaled(4)),
                        TunerTheme.Marker);
                }
                button.Draw(spriteBatch, 0, RailActive(tabIndex));
                if (TunerTheme.Glass && Grouped && GlassIconOf(GroupedPages[tabIndex].Category) is int glassIcon && GlassIcons() is { } glassIcons)
                {
                    int iconSize = Scaled(22);
                    spriteBatch.Draw(glassIcons, new Rectangle(button.Bounds.X + Scaled(10), button.Bounds.Center.Y - iconSize / 2, iconSize, iconSize),
                        GlassIconSource(glassIcon, iconSize),
                        RailActive(tabIndex) ? TunerTheme.Accent : TunerTheme.InkDim);
                    if (SettingsByCategory() is { } settingsByCategory
                        && settingsByCategory.TryGetValue(GroupedPages[tabIndex].Category, out int settings) && settings > 0)
                    {
                        string count = settings.ToString();
                        float countScale = 0.74f * _uiScale;
                        Vector2 countSize = TunerText.Measure(count) * countScale;
                        TunerText.Draw(spriteBatch, count, new Vector2(button.Bounds.Right - Scaled(8) - countSize.X,
                            button.Bounds.Center.Y - TunerText.Measure("A").Y * countScale / 2f + 2f), TunerTheme.InkFaint, countScale);
                    }
                }
                else if (_icons != null && _iconScale > 0f && icon < _icons.Width / IconSize)
                {
                    // Inactive tabs sit back a little so the icon column reads as a list
                    // rather than twelve competing colours.
                    float iconScale = _iconScale;
                    spriteBatch.Draw(_icons,
                        new Vector2(button.Bounds.X + Scaled(8), button.Bounds.Center.Y - IconSize * iconScale / 2f),
                        new Rectangle(icon * IconSize, 0, IconSize, IconSize),
                        RailActive(tabIndex) ? Color.White : Color.White * 0.72f,
                        0f, Vector2.Zero, iconScale, SpriteEffects.None, 0.9f);
                }
            }

            // A rail longer than the window says so, or the tabs past the fold are a secret.
            if (_maxRailScroll > 0)
            {
                int barX = xPositionOnScreen + RailWidth - Scaled(10);
                int trackTop = _bodyTop - Scaled(4), trackHeight = (_bodyBottom - _bodyTop) + Scaled(8);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(barX, trackTop, Scaled(RailBarWidth), trackHeight),
                    TunerTheme.Wash * 0.22f);
                int railCount = Grouped ? Categories.Length : Tabs.Length;
                int shown = railCount - _maxRailScroll;
                int thumbHeight = Math.Max(Scaled(16), trackHeight * shown / Math.Max(1, railCount));
                int thumbTop = trackTop + (trackHeight - thumbHeight) * _railScroll / _maxRailScroll;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(barX, thumbTop, Scaled(RailBarWidth), thumbHeight),
                    TunerTheme.Marker * 0.85f);
            }

            // Clip the scrolling content to the body rect so a half-scrolled row can't draw
            // over the header/footer. Requires flushing this batch and reopening one with a
            // scissor-enabled rasterizer, then restoring a normal batch for the rest.
            var device = spriteBatch.GraphicsDevice;
            Rectangle previousScissor = device.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, TextSampler, DepthStencilState.None, _scissorRaster,
                null, slideMatrix);
            device.ScissorRectangle = Rectangle.Intersect(device.Viewport.Bounds,
                new Rectangle(_contentX + (int)slideOffsetX, _bodyTop, ContentWidth, _bodyBottom - _bodyTop));

            int scrollOffsetY = -_scroll;
            if (TunerTheme.Glass)
                DrawGlassCards(spriteBatch, scrollOffsetY);
            foreach (SectionHeader header in _sectionHeaders)
            {
                if (!Visible(header.Row))
                    continue;
                // A band the width of the column, an arrow before the title the way every folding
                // list draws one (down while open, pointing at the title while folded), and, while
                // folded, how many rows it is holding in words. The first cut put a minus or a plus
                // and a bare number at the far right, and it did not read as something to click.
                var band = new Rectangle(header.Row.X - Scaled(6), header.Row.Y + scrollOffsetY, header.Row.Width + Scaled(12), header.Row.Height);
                // The heading under the pointer is lit the way the chosen tab in the rail is: a
                // darker wash and a bar down its left edge, so it is plain which one a click folds.
                bool hovered = ReferenceEquals(header, _hoveredSection);
                if (TunerTheme.Glass)
                {
                    // On the glass the heading is the top of its card, so the light takes the card's
                    // own top corners, and the marker is a rounded bar inside it, not a square strip.
                    if (hovered)
                    {
                        var lit = new Rectangle(band.X, band.Y - Scaled(6), band.Width, band.Height + Scaled(6));
                        TunerTheme.Plate(spriteBatch, lit, Scaled(14), TunerTheme.Overlay * 0.07f);
                        int barWidth = Scaled(4);
                        TunerTheme.Plate(spriteBatch, new Rectangle(lit.X + Scaled(5), lit.Y + Scaled(9), barWidth, lit.Height - Scaled(18)),
                            barWidth / 2, TunerTheme.Marker);
                    }
                }
                else
                {
                    spriteBatch.Draw(Game1.staminaRect, band, TunerTheme.Wash * (hovered ? 0.26f : 0.07f));
                    if (hovered)
                        spriteBatch.Draw(Game1.staminaRect, new Rectangle(band.X, band.Y, Scaled(4), band.Height), TunerTheme.Marker);
                }
                float inkStrength = hovered ? 1f : 0.8f;
                int unit = Math.Max(1, Scaled(2));
                int arrowY = band.Center.Y - unit * 3 - unit / 2;
                DrawDisclosureArrow(spriteBatch, header.Row.X, arrowY, unit, header.Folded, inkStrength);
                int titleX = header.Row.X + unit * 7 + Scaled(8);
                int titleY = header.Row.Y + scrollOffsetY + Scaled(4);
                if (TunerTheme.Glass && SectionIcon(header.Key) is int sectionIcon && GlassIcons() is { } sectionIcons)
                {
                    int iconSize = Scaled(15);
                    spriteBatch.Draw(sectionIcons, new Rectangle(titleX, band.Center.Y - iconSize / 2, iconSize, iconSize),
                        GlassIconSource(sectionIcon, iconSize), TunerTheme.Heading * (hovered ? 1f : 0.9f));
                    titleX += iconSize + Scaled(7);
                }
                // Buttons (the presets, the share buttons) are not counted, so a group of only those
                // said "0 hidden"; it says nothing instead.
                string? hiddenNote = header.Folded && header.HiddenRows > 0
                    ? _translate("tuner.section.foldedcount").Replace("{{count}}", header.HiddenRows.ToString())
                    : null;
                float noteScale = 0.72f * _uiScale;
                int noteWidth = hiddenNote == null ? 0 : (int)(TunerText.Measure(hiddenNote).X * noteScale);
                // The glass card's little picture sits at the right end of the heading (DrawGlassCards).
                int pictureRoom = TunerTheme.Glass && PictureFor(header.Key) is string picture && PicturePair(picture).Effect != null
                    ? (header.Row.Height - Scaled(8)) * 16 / 9 + Scaled(16)
                    : 0;
                int noteRight = header.Row.Right - pictureRoom;
                TunerText.DrawFit(spriteBatch, header.Title, new Vector2(titleX, titleY),
                    noteRight - titleX - noteWidth - Scaled(12), (TunerTheme.Glass ? TunerTheme.Heading : TunerTheme.Ink) * (hovered ? 1f : 0.9f), 0.9f * _uiScale);
                if (hiddenNote != null)
                    TunerText.Draw(spriteBatch, hiddenNote, new Vector2(noteRight - noteWidth, titleY + Scaled(3)), TunerTheme.Ink * 0.6f, noteScale);
            }
            foreach (var (text, rowY, lineHeight) in _infoLines)
            {
                var rowBounds = new Rectangle(_contentX + Scaled(16), rowY, _contentColumnWidth, lineHeight);
                if (Visible(rowBounds)) TunerText.DrawFit(spriteBatch, text(), new Vector2(_contentX + Scaled(16), rowY + scrollOffsetY), _contentColumnWidth, TunerTheme.Ink * 0.7f, (TunerTheme.Glass ? 0.8f : 0.72f) * _uiScale);
            }
            foreach (var button in _buttons) if (Visible(button.Bounds)) button.Draw(spriteBatch, scrollOffsetY, button.IsChosen?.Invoke() == true);
            foreach (var chip in _chips)
                if (Visible(chip.Load.Bounds))
                {
                    chip.Load.Draw(spriteBatch, scrollOffsetY, chip.Load.IsChosen?.Invoke() == true);
                    var deleteBounds = new Rectangle(chip.Delete.X, chip.Delete.Y + scrollOffsetY, chip.Delete.Width, chip.Delete.Height);
                    if (TunerTheme.Glass)
                    {
                        // Red under the pointer, as on the mock: this one throws a look away.
                        bool overCross = deleteBounds.Contains(Game1.getMouseX(), Game1.getMouseY());
                        if (overCross)
                            TunerTheme.Plate(spriteBatch, deleteBounds, deleteBounds.Height / 2, new Color(255, 90, 90) * 0.18f);
                        Color crossInk = overCross ? new Color(255, 120, 120)
                            : chip.Load.IsChosen?.Invoke() == true ? TunerTheme.AccentInk : TunerTheme.InkDim;
                        DrawGlassCross(spriteBatch, deleteBounds, crossInk);
                    }
                    else
                        spriteBatch.Draw(Game1.mouseCursors, deleteBounds, DeleteSource, Color.White);
                }
            if (TunerTheme.Glass)
                DrawHoveredRow(spriteBatch, scrollOffsetY);
            foreach (var toggle in _toggles) if (Visible(toggle.Row)) toggle.Draw(spriteBatch, scrollOffsetY);
            foreach (var slider in _sliders) if (Visible(slider.Track)) slider.Draw(spriteBatch, scrollOffsetY);
            foreach (var compass in _compasses) if (Visible(compass.Row)) compass.Draw(spriteBatch, scrollOffsetY);

            // A small mark beside every row that is not at its default, so what has been changed can
            // be found at a glance (right click puts it back). Not on a greyed row: nothing on it can
            // be changed, and the reset its mark offered did nothing. Dragging another dial can grey
            // a row without a rebuild, so this is asked every frame, like the grey itself.
            // On the glass it is a gold dot just after the label, as on the mock.
            foreach (var slider in _sliders)
                if (Visible(slider.RowBounds) && slider.DiffersFromDefault && slider.IsEnabled)
                {
                    if (TunerTheme.Glass && slider.Inline)
                        DrawChangedDot(spriteBatch, slider.LabelEnd);
                    else
                        DrawChangedMark(spriteBatch, slider.RowBounds.X, slider.RowBounds.Y + Scaled(9) + scrollOffsetY);
                }
            foreach (var toggle in _toggles)
                if (Visible(toggle.Row) && toggle.DiffersFromDefault && toggle.IsEnabled)
                {
                    if (TunerTheme.Glass)
                        DrawChangedDot(spriteBatch, toggle.LabelEnd);
                    else
                        DrawChangedMark(spriteBatch, toggle.Row.X, toggle.Row.Y + Scaled(14) + scrollOffsetY);
                }
            foreach (var compass in _compasses)
                if (Visible(compass.Row) && compass.DiffersFromDefault && compass.IsEnabled)
                    DrawChangedMark(spriteBatch, compass.Row.X, compass.Row.Y + Scaled(9) + scrollOffsetY);

            // A soft shade along an edge the content runs past, so a cut-off heading at the top reads
            // as scrolled rather than as a layout fault. It deepens over the first rows of travel
            // instead of appearing at full strength on the first notch.
            // On the glass the rounded edge says the same thing, as on the mock, and the shade only
            // showed as a grey band poking out past the cards.
            if (TunerTheme.Glass)
                RoundTheScrollEdges(spriteBatch, slideOffsetX);
            else
            {
                DrawScrollShade(spriteBatch, _bodyTop, fromTop: true, Math.Min(1f, _scroll / (float)Scaled(48)));
                DrawScrollShade(spriteBatch, _bodyBottom, fromTop: false, Math.Min(1f, (_maxScroll - _scroll) / (float)Scaled(48)));
            }

            spriteBatch.End();
            device.ScissorRectangle = previousScissor;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, TextSampler, DepthStencilState.None, RasterizerState.CullCounterClockwise,
                null, slideMatrix);

            if (_maxScroll > 0)
            {
                int contentTrackX = xPositionOnScreen + width - 18;
                int contentTrackHeight = _bodyBottom - _bodyTop;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(contentTrackX, _bodyTop, 6, contentTrackHeight), Color.Black * 0.25f);
                int contentThumbHeight = Math.Max(30, (int)(contentTrackHeight * (float)contentTrackHeight / (contentTrackHeight + _maxScroll)));
                int contentThumbY = _bodyTop + (int)((contentTrackHeight - contentThumbHeight) * (_scroll / (float)_maxScroll));
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(contentTrackX, contentThumbY, 6, contentThumbHeight), TunerTheme.Fill);
            }

            if (TunerTheme.Glass)
                DrawGlassFooter(spriteBatch);
            else
                TunerText.DrawFit(spriteBatch, _translate("tuner.hint"), new Vector2(xPositionOnScreen + Scaled(28), _hintY), innerWidth, TunerTheme.Ink * 0.7f, 0.8f * _uiScale);
            DrawHelpBar(spriteBatch);
            DrawLayoutPrompt(spriteBatch);

            // The game's red cross on the glass is a piece of the wooden box left behind; the glass
            // draws a round button of its own in the same place, as on the mock.
            if (upperRightCloseButton != null)
            {
                if (TunerTheme.Glass)
                {
                    Rectangle close = upperRightCloseButton.bounds;
                    int size = Math.Min(close.Width, close.Height);
                    var round = new Rectangle(close.Center.X - size / 2, close.Center.Y - size / 2, size, size);
                    bool hovered = round.Contains(Game1.getMouseX(), Game1.getMouseY());
                    TunerTheme.Plate(spriteBatch, round, size / 2, TunerTheme.Overlay * (hovered ? 0.16f : 0.08f));
                    TunerTheme.PlateRim(spriteBatch, round, size / 2, TunerTheme.Rim);
                    DrawGlassCross(spriteBatch, round, TunerTheme.Ink);
                }
            }

            // Hidden only while the base draws it: a hidden button also stops taking clicks.
            bool closeShown = upperRightCloseButton?.visible ?? false;
            if (upperRightCloseButton != null && TunerTheme.Glass)
                upperRightCloseButton.visible = false;
            base.draw(spriteBatch);
            upperRightCloseButton?.visible = closeShown;
            // The pointer and the hover note stay where the mouse is, not where the panel is going.
            if (slideMatrix != null)
            {
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, TextSampler, DepthStencilState.None,
                    RasterizerState.CullCounterClockwise);
            }
            drawMouse(spriteBatch);
        }

    }
}
