using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using StardewModdingAPI;
using StardewValley;
using xTile.Layers;

namespace SDVRadiance
{
    /// <summary>Coarse per-tile surface class. Values are stable, because they are written into
    /// cached grids: 0 Ground, 1 Water, 2 Wall, 3 Roof, 4 Deck, 5 Void, 6 Glass. Not the same
    /// numbers as a LABEL class (see <see cref="LabelClass"/>), which is what a painted tile
    /// carries; several label classes fold into one surface here.</summary>
    internal enum SurfaceClass : byte
    {
        /// <summary>Flat, walkable ground at reference height 0.</summary>
        Ground,
        /// <summary>Below the ground plane — open water that reflects.</summary>
        Water,
        /// <summary>A solid, blocking structure base (Buildings tile that isn't passable/shadow).</summary>
        Wall,
        /// <summary>Tall overhead art (Front tile) with no blocking base beneath it.</summary>
        Roof,
        /// <summary>A raised WALKABLE surface: pier / bridge deck, usually over water.</summary>
        Deck,
        /// <summary>A hole / off-ledge void.</summary>
        Void,
        /// <summary>A vertical GLASS pane (label class 13): reflects like a mirror but light
        /// passes through it, so a lit room still glows out through the shop front. Mirrors
        /// (class 8) stay <see cref="Wall"/> — they are backed and do block.</summary>
        Glass,
    }

    /// <summary>
    /// Per-location surface grid, inferred once per visit from the map's own signals. The game has
    /// no per-tile Z, so this reads the vanilla renderer's conventions: Buildings is collision and
    /// wall bases, Front is tall overhead art, Passable-on-Buildings marks a raised walkable deck
    /// (pier / plank bridge), and the Water tile property marks the sub-ground plane.
    ///
    /// Painted labels (<see cref="LabelStore"/>) win over every heuristic here.
    ///
    /// This was Height Framework's job. It lives in this mod now so the water and lighting passes
    /// do not depend on a second mod being installed — the classification they need is the same
    /// map data either way, and half the players never had that mod.
    /// </summary>
    internal sealed class SurfaceMap
    {
        public readonly int Width;
        public readonly int Height;
        private readonly sbyte[] _tileHeights;
        private readonly SurfaceClass[] _surfaceClasses;

        private static readonly ConditionalWeakTable<GameLocation, SurfaceMap> _locationCache = [];

        /// <summary>The map object this grid was built from, and how many map overrides its location
        /// had applied at the time. The game changes a map under the SAME location without reloading
        /// any asset: a house upgrade loads a new map onto the farmhouse, and ApplyMapOverride writes
        /// a repaired bridge or a greenhouse straight into the map already there. No invalidation
        /// fires for either, so a grid kept only by location went on describing the old layout for
        /// the rest of the session: the new rooms' walls let lamp light through and the old walls
        /// stopped it in the middle of a room.</summary>
        private xTile.Map? _builtFromMap;
        private int _mapOverridesAtBuild;

        private static readonly HarmonyLib.AccessTools.FieldRef<GameLocation, HashSet<string>>? AppliedMapOverrides = FindAppliedMapOverrides();

        private static HarmonyLib.AccessTools.FieldRef<GameLocation, HashSet<string>>? FindAppliedMapOverrides()
        {
            try { return HarmonyLib.AccessTools.FieldRefAccess<GameLocation, HashSet<string>>("_appliedMapOverrides"); }
            catch { return null; }
        }

        private static int AppliedMapOverrideCount(GameLocation location)
        {
            if (AppliedMapOverrides == null)
                return 0;
            try { return AppliedMapOverrides(location)?.Count ?? 0; }
            catch { return 0; }
        }

        /// <summary>How many times a location's own map asset has been reloaded under it. The
        /// whole-map scans (window lights, emissive tiles, window panes) read the map's LAYERS, so
        /// a map re-patched in place can move a window without a single label changing its mind,
        /// and this is the number that tells them so. See <see cref="MapAnswerKey"/> for why they
        /// need telling separately now.
        ///
        /// <para>A tile sheet the map draws from is deliberately NOT counted here. Repainting a
        /// sheet cannot move a tile; what it can change is whether a label still applies to the
        /// art on it, and that question is the label store's, which answers it per sheet.</para>
        ///
        /// <para>Weak, like the grids beside it: a location the game has let go takes its count
        /// with it, and a location that is asked about before it is ever reloaded reads zero.</para></summary>
        private static readonly ConditionalWeakTable<GameLocation, StrongBox<int>> _mapReloadCount = [];

        public static int MapReloadCount(GameLocation? location)
            => location != null && _mapReloadCount.TryGetValue(location, out StrongBox<int>? count) ? count.Value : 0;

        private static void CountMapReload(GameLocation location)
        {
            if (_mapReloadCount.TryGetValue(location, out StrongBox<int>? count))
                count.Value++;
            else
                _mapReloadCount.Add(location, new StrongBox<int>(1));
        }

        private SurfaceMap(int width, int height)
        {
            Width = width;
            Height = height;
            _tileHeights = new sbyte[width * height];
            _surfaceClasses = new SurfaceClass[width * height];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public sbyte GetHeight(int x, int y) => InBounds(x, y) ? _tileHeights[y * Width + x] : (sbyte)0;

        public SurfaceClass GetSurface(int x, int y)
            => InBounds(x, y) ? _surfaceClasses[y * Width + x] : SurfaceClass.Ground;

        /// <summary>Open water: the surface reflects. A pier/bridge DECK over water is not.</summary>
        public bool IsWater(int x, int y) => GetSurface(x, y) == SurfaceClass.Water;

        /// <summary>Walls and roofs block sky/lamp light. Decks are raised but open to the sky,
        /// and water is open too — treating either as solid turned whole piers into dark pools.</summary>
        public bool BlocksLight(int x, int y)
        {
            var surface = GetSurface(x, y);
            return surface is SurfaceClass.Wall or SurfaceClass.Roof;
        }

        // ---- cache ---------------------------------------------------------------------------

        /// <summary>The grid for a location, built on first use. Keyed weakly, so unloaded
        /// locations are collected on their own. Hoist this out of per-tile loops.</summary>
        public static SurfaceMap? For(GameLocation? location)
        {
            if (location == null)
                return null;
            if (_locationCache.TryGetValue(location, out SurfaceMap? grid))
            {
                if (ReferenceEquals(grid._builtFromMap, location.map)
                    && grid._mapOverridesAtBuild == AppliedMapOverrideCount(location))
                    return grid;
                // The map changed under the location with no asset reloaded (see _builtFromMap).
                // Counted as a reload, so the whole-map scans keyed on that count look again, and the
                // water mask is told when it is the map on screen.
                DiagnosticMonitor?.Log($"[location] map changed in place: {location.NameOrUniqueName}, its surface grid is rebuilt", LogLevel.Trace);
                _locationCache.Remove(location);
                CountMapReload(location);
                if (ReferenceEquals(location, Game1.currentLocation))
                {
                    RenderPipeline.MaskEpoch++;
                    RenderPipeline.MaskEpochReason = "the map changed in place (" + location.NameOrUniqueName + ")";
                    WaterDrawHook.Forget(location);
                }
            }
            // Breadcrumbs, not a perf counter: this is a whole-map walk that runs once when a
            // location is first drawn, and a freeze report can only be pinned to it if the log
            // shows the walk STARTED and never finished. Trace always lands in the SMAPI log
            // file, so a reporter needs no debug switch for it to be there after a hard stop.
            DiagnosticMonitor?.Log($"[location] surface build start: {location.NameOrUniqueName}", LogLevel.Trace);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            SurfaceBuildCost cost = default;
            try { grid = Build(location, out cost); }
            catch (Exception exception) { grid = null; DiagnosticMonitor?.Log($"[location] surface build threw: {exception.Message}", LogLevel.Warn); }
            stopwatch.Stop();
            DiagnosticMonitor?.Log($"[location] surface build done: {location.NameOrUniqueName} {grid?.Width ?? 0}x{grid?.Height ?? 0} in {stopwatch.Elapsed.TotalMilliseconds:0.0}ms ({cost})", LogLevel.Trace);
            if (grid != null)
            {
                // Read after the build: building asks for location.Map, which can itself load the
                // map the location was waiting to switch to.
                grid._builtFromMap = location.map;
                grid._mapOverridesAtBuild = AppliedMapOverrideCount(location);
                _locationCache.Add(location, grid);
            }
            return grid;
        }

        /// <summary>Optional diagnostics sink (set at startup) — see the breadcrumbs in <see cref="For"/>.</summary>
        internal static IMonitor? DiagnosticMonitor;

        /// <summary>What one grid cost, pass by pass. The build is four walks over the map, not
        /// one, and a reader who only has the total cannot tell a slow classifier from a slow
        /// deck span. Free to collect: this runs once per location, not once per frame.</summary>
        private readonly record struct SurfaceBuildCost(double Layers, double Classify,
                                                        double SpanDecks, double ThinRoofs,
                                                        double Footprints)
        {
            public override string ToString()
                => $"layers {Layers:0.0}, classify {Classify:0.0}, decks {SpanDecks:0.0}, "
                 + $"roofs {ThinRoofs:0.0}, footprints {Footprints:0.0}";
        }

        /// <summary>Milliseconds since a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</summary>
        private static double MillisecondsSince(long timestamp)
            => (System.Diagnostics.Stopwatch.GetTimestamp() - timestamp) * 1000.0
             / System.Diagnostics.Stopwatch.Frequency;

        public static void Invalidate(GameLocation? location)
        {
            if (location != null)
                _locationCache.Remove(location);
        }

        /// <summary>Drop everything (save load, or a label reload during development).</summary>
        public static void Clear() => _locationCache.Clear();

        /// <summary>
        /// Drop the grids of every location one of these reloaded assets can have changed: a
        /// location whose map IS the asset, or whose map draws from it as a tile sheet.
        ///
        /// <para>This used to be <see cref="Clear"/> for any name under Maps/. Content Patcher
        /// packs with conditions on the time of day re-patch their assets every few in-game
        /// minutes, and a heavily modded save sees a Maps/* reload every few seconds: the town's
        /// autumn tile sheet, a festival map, a shop interior. Each one threw away the farm's
        /// grid while the player stood on the farm, and everything keyed on that grid's identity
        /// went with it: the flood occluder mask rebuilt, the water gather's map-wide memory was
        /// dropped and re-asked tile by tile, and the whole-map waterline anchor was gathered
        /// again. A split-screen report counted that anchor gather, 18 to 23 ms, "firing
        /// repeatedly on the farm", and this is one of the two reasons it did (the other is in
        /// WaterDrawHook). A tile sheet the current map never draws from cannot change what its
        /// tiles are.</para>
        ///
        /// <para>Names are compared with the game's locale suffix taken off ("Maps/fall_town.th"
        /// is the same sheet as "Maps/fall_town") and either slash accepted, because the map
        /// calls its sheets by the base name and the invalidation carries the translated one.</para>
        /// </summary>
        public static void InvalidateAffectedBy(IEnumerable<string> reloadedAssetNames)
        {
            var reloaded = new List<string>();
            foreach (string name in reloadedAssetNames)
            {
                string normalised = AssetNames.Normalise(name);
                if (normalised.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase))
                    reloaded.Add(normalised);
            }
            if (reloaded.Count == 0)
                return;
            Utility.ForEachLocation(location =>
            {
                DropWhatTheseReloaded(location, reloaded);
                return true;
            });
            // A location the game holds outside its list (a temporary festival map, a mod's
            // instanced interior) is still the one on screen.
            if (Game1.currentLocation is { } here)
                DropWhatTheseReloaded(here, reloaded);
        }

        /// <summary>Throw away this location's grid if one of these assets is its map or a sheet
        /// its map draws from, and count the reload if the asset IS the map.</summary>
        private static void DropWhatTheseReloaded(GameLocation location, List<string> reloaded)
        {
            bool mapItself = LocationMapIsOneOf(location, reloaded);
            if (mapItself)
                CountMapReload(location);
            if (mapItself || LocationDrawsSheetFrom(location, reloaded))
                _locationCache.Remove(location);
        }

        private static bool LocationMapIsOneOf(GameLocation location, List<string> reloaded)
        {
            string? mapPath = location.mapPath?.Value;
            if (mapPath == null)
                return false;
            string mapAssetName = AssetNames.Normalise(mapPath);
            foreach (string name in reloaded)
                if (string.Equals(mapAssetName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool LocationDrawsSheetFrom(GameLocation location, List<string> reloaded)
        {
            var sheets = location.map?.TileSheets;
            if (sheets == null)
                return false;
            foreach (var sheet in sheets)
            {
                string source = AssetNames.Normalise(sheet.ImageSource ?? "");
                foreach (string name in reloaded)
                    if (string.Equals(source, name, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        // ---- inference -----------------------------------------------------------------------

        /// <summary>
        /// Tile class from 256 per-pixel labels, or null when the labels aren't decisive.
        /// Extended studio classes fold in: emissive / reflect_floor are Ground, mirror and
        /// window sit in a wall plane. <paramref name="overlay"/> relaxes the water threshold,
        /// because overlay art (a surf wash, a fountain rim) is mostly transparent, so a sparse
        /// patch of labeled water pixels already means something.
        /// </summary>
        private static SurfaceClass? ClassFromLabels(byte[] classes, bool overlay)
            => ClassFromLabels(classes, overlay, out _);

        /// <inheritdoc cref="ClassFromLabels(byte[], bool)"/>
        /// <param name="deckPixels">How much of the tile the deck labels actually cover. The caller
        /// needs it because a Deck verdict is the one verdict that can be right about the art and
        /// wrong about the tile: see the plank rule in <see cref="Build"/>.</param>
        /// <summary>What one label buffer says, both ways of reading it, counted once. The buffers
        /// are the shipped label data and there are a few thousand of them; the map has fifteen
        /// thousand tiles and asks about several layers of each, so the same 256 bytes were being
        /// read through over and over for an answer that cannot change. Weakly keyed, because a
        /// mirrored or turned tile is handed a fresh copy of its label and that copy must be free
        /// to go.</summary>
        private sealed class LabelVerdict
        {
            internal SurfaceClass? AsSurface;
            internal SurfaceClass? AsOverlay;
            internal int DeckPixels;
        }

        private static readonly ConditionalWeakTable<byte[], LabelVerdict> _verdictByLabel = [];

        private static SurfaceClass? ClassFromLabels(byte[] classes, bool overlay, out int deckPixels)
        {
            if (!_verdictByLabel.TryGetValue(classes, out LabelVerdict? verdict))
            {
                verdict = new LabelVerdict
                {
                    AsSurface = CountLabel(classes, overlay: false, out int deck),
                    AsOverlay = CountLabel(classes, overlay: true, out _),
                    DeckPixels = deck,
                };
                _verdictByLabel.Add(classes, verdict);
            }
            deckPixels = verdict.DeckPixels;
            return overlay ? verdict.AsOverlay : verdict.AsSurface;
        }

        private static SurfaceClass? CountLabel(byte[] classes, bool overlay, out int deckPixels)
        {
            int water = 0, deck = 0, wall = 0, roof = 0, ground = 0, glass = 0;
            for (int pixelIndex = 0; pixelIndex < 256; pixelIndex++)
            {
                switch (classes[pixelIndex])
                {
                    case LabelClass.Water: case LabelClass.Ice: case LabelClass.Flowing:
                    case LabelClass.Lava: case LabelClass.Hot: water++; break;
                    case LabelClass.Wall: case LabelClass.Mirror: wall++; break;   // a mirror is backed: it blocks
                    case LabelClass.Roof: roof++; break;
                    case LabelClass.Deck: deck++; break;
                    // A WINDOW is a hole in a wall with glass in it, and a glass roof is a
                    // skylight: light goes through both. Folding 12 in with `wall` made a painted
                    // window BLOCK the lamp light it is supposed to let past — a display case in
                    // Pierre's shop threw a hard shadow across the goods behind it.
                    case LabelClass.Window: case LabelClass.Glass: glass++; break;
                    case LabelClass.Void: break;                        // never decisive on its own
                    default: ground++; break;                           // ground, emissive, reflect_floor
                }
            }
            deckPixels = deck;
            // Order matters: a deck plank drawn OVER water has to read Deck, not Water.
            if (deck >= QuarterTilePixels) return SurfaceClass.Deck;
            if (wall >= QuarterTilePixels) return SurfaceClass.Wall;
            if (glass >= QuarterTilePixels) return SurfaceClass.Glass;
            if (water >= (overlay ? OverlayWaterPixels : HalfTilePixels)) return SurfaceClass.Water;
            if (roof >= QuarterTilePixels) return SurfaceClass.Roof;
            // A window PANE is a small part of its tile — the frame and the wall around it take
            // the rest — so it never reaches the 64-pixel bar above. 8 is the same bar the window
            // LIGHT scan uses (RenderPipeline.Lighting.EnsureWindowCache), so the two agree: a
            // tile bright enough to emit window light is a tile light can pass through.
            if (glass >= WindowPanePixels) return SurfaceClass.Glass;
            if (!overlay && ground >= MostOfTilePixels) return SurfaceClass.Ground;
            return null;
        }

        /// <summary>How much of a sixteen by sixteen tile a class has to cover to speak for it.
        /// A quarter is the ordinary bar; water painted as an OVERLAY over something else gets a
        /// lower one, because it is a fringe rather than a surface; deciding a tile is plain
        /// ground takes most of it; and a window PANE is a small part of its tile, since the frame
        /// and the wall around it take the rest, so it never reaches the quarter bar. Eight is the
        /// same bar the window LIGHT scan uses, so the two agree: a tile bright enough to emit
        /// window light is a tile light can pass through.</summary>
        private const int QuarterTilePixels = 64;
        private const int OverlayWaterPixels = 48;
        private const int HalfTilePixels = 128;
        private const int MostOfTilePixels = 192;
        internal const int WindowPanePixels = 8;

        /// <summary>Deck coverage at which a plank owns its whole tile rather than clipping it.
        /// Half the tile: below that the water underneath keeps the tile and the plank is carved
        /// per pixel instead.</summary>
        private const int DeckOwnsTile = 128;

        /// <summary>
        /// The layers one map exposes, looked up once. The per-tile sweep consults eight of them
        /// plus the label pack, and eight parameters on a 150-line loop is a signature nobody
        /// reads - the same reason the water gather carries a context rather than a parameter list.
        /// </summary>
        private readonly struct MapLayerSet
        {
            public readonly Layer? Back;
            public readonly Layer? Buildings;
            public readonly Layer? Front;
            /// <summary>Layers the canonical trio never covers: SVE puts water art on Back2, and
            /// vanilla waterfalls live on AlwaysFront.</summary>
            public readonly Layer? Back2;
            public readonly Layer? Front2;
            public readonly Layer? AlwaysFront;
            public readonly Layer? AlwaysFront2;
            /// <summary>EVERY Buildings-family layer, topmost first - a deck plank sits over the
            /// Back tile no matter which numbered layer carries it.</summary>
            public readonly List<Layer> BuildingsTopDown;
            /// <summary>The layers a liquid can be painted ON TOP of a dry base in, in the order
            /// they are consulted. Held, because the tile sweep used to write this list out as a
            /// new array inside the loop: one allocation for every tile of the map.</summary>
            public readonly Layer?[] LiquidOverlays;
            public readonly LabelStore? Labels;

            public MapLayerSet(Layer? back, Layer? buildings, Layer? front, Layer? back2,
                Layer? front2, Layer? alwaysFront, Layer? alwaysFront2,
                List<Layer> buildingsTopDown, LabelStore? labels)
            {
                Back = back; Buildings = buildings; Front = front;
                Back2 = back2; Front2 = front2;
                AlwaysFront = alwaysFront; AlwaysFront2 = alwaysFront2;
                BuildingsTopDown = buildingsTopDown; Labels = labels;
                LiquidOverlays = [front, front2, alwaysFront, alwaysFront2];
            }
        }


        private static SurfaceMap? Build(GameLocation location, out SurfaceBuildCost cost)
        {
            cost = default;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var tileMap = location.Map;
            if (tileMap == null || tileMap.Layers.Count == 0)
                return null;

            Layer baseLayer = tileMap.Layers[0];
            int mapWidth = baseLayer.LayerWidth, mapHeight = baseLayer.LayerHeight;
            if (mapWidth <= 0 || mapHeight <= 0)
                return null;

            Layer? back = tileMap.GetLayer("Back");
            Layer? buildings = tileMap.GetLayer("Buildings");
            Layer? front = tileMap.GetLayer("Front");
            // EVERY Buildings-family layer, topmost first. A deck plank sits OVER the Back tile
            // no matter which numbered layer carries it: Aimon's festival bridge draws its planks
            // on Buildings2 (with only a support beam on Buildings-1), and consulting just the
            // base "Buildings" layer let the Back water label win the tile — the river rippled
            // straight across a bridge whose planks were painted Deck.
            var buildingsTopDown = new List<Layer>();
            foreach (var layer in MapLayers.RenderedLayers(tileMap, topToBottom: true))
                if (MapLayers.BelongsToFamily(layer.Id, "Buildings"))
                    buildingsTopDown.Add(layer);
            // Layers the canonical trio never covers (SVE puts water art on Back2,
            // vanilla waterfalls live on AlwaysFront). Consulted below ONLY when the trio
            // yields no verdict, so no tile that already classifies changes class.
            Layer? back2 = tileMap.GetLayer("Back2");
            Layer? front2 = tileMap.GetLayer("Front2");
            Layer? alwaysFront = tileMap.GetLayer("AlwaysFront");
            Layer? alwaysFront2 = tileMap.GetLayer("AlwaysFront2");
            var labels = LabelStore.Instance;
            if (labels is { Any: false })
                labels = null;

            var grid = new SurfaceMap(mapWidth, mapHeight);
            // Which tiles a painted label decided. The span pass below must never overrule them
            // (iron rule: a label beats every heuristic, including the ones that run after it).
            bool[] labelled = new bool[mapWidth * mapHeight];
            var layers = new MapLayerSet(back, buildings, front, back2, front2,
                alwaysFront, alwaysFront2, buildingsTopDown, labels);
            double layersMilliseconds = MillisecondsSince(startedAt);

            long passStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            ClassifyTiles(grid, labelled, mapWidth, mapHeight, location, layers);
            double classifyMilliseconds = MillisecondsSince(passStartedAt);

            passStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            SpanDecks(grid, labelled, mapWidth, mapHeight);
            double spanDecksMilliseconds = MillisecondsSince(passStartedAt);

            passStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            ThinRoofs(grid, labelled, mapWidth, mapHeight);
            double thinRoofsMilliseconds = MillisecondsSince(passStartedAt);

            passStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            StampBuildingFootprints(grid, location, mapWidth);
            double footprintsMilliseconds = MillisecondsSince(passStartedAt);

            cost = new SurfaceBuildCost(layersMilliseconds, classifyMilliseconds, spanDecksMilliseconds,
                                        thinRoofsMilliseconds, footprintsMilliseconds);
            return grid;
        }

        /// <summary>Read every tile once and give it a class and a height: the classifier itself.
        /// <paramref name="labelled"/> comes back marking the tiles a painted label decided, which
        /// the span passes afterwards must never overrule.</summary>
        private static void ClassifyTiles(SurfaceMap grid, bool[] labelled, int mapWidth, int mapHeight,
            GameLocation location, MapLayerSet layers)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    int tileIndex = y * mapWidth + x;
                    bool hasBuildings = layers.Buildings?.Tiles[x, y] != null;
                    bool hasFront = layers.Front?.Tiles[x, y] != null;

                    // ---- LABELS FIRST: painted ground truth beats every heuristic below. ----
                    // Buildings decides first (a deck plank or fountain rim sits OVER the Back
                    // tile), then Back, then Front (overhead art).
                    SurfaceClass? painted = ResolvePaintedClass(location, layers, x, y, hasBuildings,
                        out bool anyLabel);
                    if (painted is { } decided)
                    {
                            Set(grid, tileIndex, decided, decided switch
                            {
                                SurfaceClass.Water => -1,
                                SurfaceClass.Ground => 0,
                                SurfaceClass.Void => 0,
                                _ => 1,
                            });
                        labelled[tileIndex] = true;
                        continue;
                    }
                    // A PAINTED but mixed tile (a tide pool's rock rim: water:99 + ground:157,
                    // decisive for neither) still protects itself from the span pass — the author
                    // told us what it is, and it is not a bridge. SpanDecks promoting these rims
                    // to Deck made the compose scrub the pool's march whole-tile: ripple, no
                    // reflection, on every beach tide pool.
                    if (anyLabel)
                        labelled[tileIndex] = true;

                    SurfaceClass surfaceClass = ClassifyFromMapProperties(location, layers, x, y, hasBuildings,
                        hasFront, out sbyte height);
                    Set(grid, tileIndex, surfaceClass, height);
                }
            }
        }

        /// <summary>What the PAINTED labels say this tile is, or null when nothing painted has an
        /// opinion. <paramref name="anyLabel"/> comes back true even for a mixed verdict, because a
        /// tile an author painted must still protect itself from the span passes.</summary>
        private static SurfaceClass? ResolvePaintedClass(GameLocation location, MapLayerSet layers,
                                                        int x, int y, bool hasBuildings, out bool anyLabel)
        {
            anyLabel = false;
            if (layers.Labels == null)
                return null;
            SurfaceClass? paintedClass = null;
            // The Buildings family decides first, topmost layer down: the layer the
            // player sees is the layer whose label should answer for the tile.
            foreach (Layer buildingsLayer in layers.BuildingsTopDown)
            {
                if (layers.Labels.Get(buildingsLayer, x, y) is not { } buildingsLabel)
                    continue;
                anyLabel = true;
                paintedClass = ClassFromLabels(buildingsLabel, overlay: true, out int deckPixels);
                // A PLANK THAT ONLY CLIPS ITS TILE must not delete the tile's water.
                // Deck wins at a quarter of the tile, which is the right bar for "is
                // there a walkable surface drawn here" and much too low for "is this
                // tile still water": a bridge parapet or a plank end overlapping the
                // edge of a water tile took the whole tile out of the mask, and the
                // water stopped dead at a straight line beside the bridge (Mountain
                // 46,4 and its neighbours, reported as a bridge outline, 256 of 256
                // pixels missing on tiles the layers.Labels call water end to end).
                //
                // Handing the tile layers.Back to the water it is mostly made of loses
                // nothing, because the planks are carved out again PER PIXEL further
                // down the pipeline by the Buildings opacity carve. The whole-tile
                // Deck verdict is only needed when the deck really does own the tile.
                if (paintedClass == SurfaceClass.Deck && deckPixels < DeckOwnsTile
                    && layers.Labels.Get(layers.Back, x, y) is { } underneath
                    && ClassFromLabels(underneath, overlay: false) == SurfaceClass.Water)
                    paintedClass = SurfaceClass.Water;
                if (paintedClass != null)
                    break;
            }
            if (paintedClass == null && layers.Labels.Get(layers.Back, x, y) is { } backLabel) { anyLabel = true; paintedClass = ClassFromLabels(backLabel, overlay: false); }
            if (paintedClass == null && layers.Labels.Get(layers.Front, x, y) is { } frontLabel) { anyLabel = true; paintedClass = ClassFromLabels(frontLabel, overlay: true); }
            // Additive fallback to the layers above — a Town waterfall labelled
            // flow:256 on AlwaysFront was never declared water at all, so its
            // liquid never reached the mask (the compose already honours these
            // layers.Labels for the carve and sub-type; classification was the gap).
            if (paintedClass == null && layers.Labels.Get(layers.Back2, x, y) is { } back2Label) { anyLabel = true; paintedClass = ClassFromLabels(back2Label, overlay: false); }
            if (paintedClass == null && layers.Labels.Get(layers.Front2, x, y) is { } front2Label) { anyLabel = true; paintedClass = ClassFromLabels(front2Label, overlay: true); }
            if (paintedClass == null && layers.Labels.Get(layers.AlwaysFront, x, y) is { } alwaysFrontLabel) { anyLabel = true; paintedClass = ClassFromLabels(alwaysFrontLabel, overlay: true); }
            if (paintedClass == null && layers.Labels.Get(layers.AlwaysFront2, x, y) is { } alwaysFront2Label) { anyLabel = true; paintedClass = ClassFromLabels(alwaysFront2Label, overlay: true); }
            // A liquid OVERLAY beats a dry base verdict: a falls' base tile carries
            // Back "ground" (the cliff) under a Front/AlwaysFront falls labelled
            // flow, and what the player sees there is falling water — the Ground
            // verdict blocked the whole tile from ever entering the mask (256/256
            // missing at every falls base). Only Ground gives way; Deck/Wall/Roof
            // keep their say, so a plank over water still reads as a deck.
            if (paintedClass is null or SurfaceClass.Ground)
            {
                foreach (var overlayLayer in layers.LiquidOverlays)
                {
                    if (overlayLayer == null || layers.Labels.Get(overlayLayer, x, y) is not { } overlayLabel)
                        continue;
                    if (ClassFromLabels(overlayLabel, overlay: true) == SurfaceClass.Water)
                    {
                        anyLabel = true;
                        paintedClass = SurfaceClass.Water;
                        break;
                    }
                }
            }
            // A DECK is the surface you stand on, even when the tile beneath it is
            // labelled water — and the plank itself often carries no label at all, so
            // the lookup falls through to the Back tile below and answers for the
            // wrong thing. The Beach bridge reads Buildings.Passable=T Type=Wood over
            // Back water:256: standing on it counted as standing on open water, which
            // costs the player their shadow outright, and cut the bridge's own shadow
            // wherever the water under it was open on all sides.
            //
            // The animated case is left alone: an ANIMATED passable Buildings tile
            // over water is the surf wash, which really is the water surface.
            if (paintedClass == SurfaceClass.Water && hasBuildings
                && layers.Buildings!.Tiles[x, y] is not xTile.Tiles.AnimatedTile
                && (location.doesTileHaveProperty(x, y, "Passable", "Buildings") != null
                    || location.doesTileHaveProperty(x, y, "Type", "Buildings") == "Wood"))
                paintedClass = SurfaceClass.Deck;
            return paintedClass;
        }

        /// <summary>No painted opinion: read the tile the way the game does, from its layers and
        /// its map properties.</summary>
        private static SurfaceClass ClassifyFromMapProperties(GameLocation location, MapLayerSet layers,
                                                             int x, int y, bool hasBuildings, bool hasFront,
                                                             out sbyte height)
        {
            SurfaceClass surfaceClass;
            bool passableBuildings = hasBuildings && location.doesTileHaveProperty(x, y, "Passable", "Buildings") != null;
            if (passableBuildings && layers.Buildings!.Tiles[x, y] is xTile.Tiles.AnimatedTile && location.isWaterTile(x, y))
            {
                // An ANIMATED passable Buildings tile over water IS the water surface —
                // the beach surf wash. The deck rule below used to call it a pier and ate
                // the whole tide line. Real decks are static art.
                surfaceClass = SurfaceClass.Water;
                height = -1;
            }
            else if (passableBuildings)
            {
                surfaceClass = SurfaceClass.Deck;      // walk-on-top raised platform: pier / bridge
                height = 1;
            }
            else if (location.doesTileHaveProperty(x, y, "Type", "Back") == "Wood")
            {
                surfaceClass = SurfaceClass.Deck;      // Back-layer planking: pier / bridge / porch
                height = 1;
            }
            else if (hasBuildings && location.doesTileHaveProperty(x, y, "Shadow", "Buildings") == null)
            {
                surfaceClass = SurfaceClass.Wall;      // blocking Buildings tile that isn't decorative shadow art
                height = 1;
            }
            else if (location.isWaterTile(x, y))
            {
                surfaceClass = SurfaceClass.Water;
                height = -1;
            }
            else if (hasFront)
            {
                surfaceClass = SurfaceClass.Roof;      // tall overhead art with no blocking base
                height = 1;
            }
            else
            {
                surfaceClass = SurfaceClass.Ground;
                height = 0;
            }
            return surfaceClass;
        }

        /// <summary>Farm buildings are Building ENTITIES, not Buildings-layer tiles, so the
        /// per-tile pass misses them entirely.</summary>
        private static void StampBuildingFootprints(SurfaceMap grid, GameLocation location, int mapWidth)
        {
            // Farm buildings (coops, barns, cabins, the farmhouse) are Building ENTITIES, not
            // Buildings-layer tiles, so the per-tile pass misses them. The footprint rows are the
            // solid Wall base; the sprite is usually TALLER than the footprint and the game draws
            // those extra rows above it, so stamp them as Roof or they read as open Ground.
            foreach (var building in location.buildings)
            {
                if (building == null)
                    continue;
                // A building Robin has not finished is a frame of scaffolding with the sky through
                // it, so it has no walls to block light and no roof to keep the rain off. Stamped
                // anyway, its whole plan stood in this grid as Wall and Roof, and every pass that
                // reads the grid drew the building that is not there yet: in rain the plan came out
                // as a clean rectangle nobody was wetting, which reads as a ghost of the finished
                // building (reported with a picture by Mokayogi on Nexus). The fish pond below
                // already asked this question; now every building does.
                // The game's own answer to "is anything standing here yet". An UPGRADE is not
                // asked about: a coop on its way to a big coop is still a coop, with walls and a
                // roof, for every one of those days.
                if (building.isUnderConstruction())
                    continue;
                int footprintLeft = building.tileX.Value, footprintTop = building.tileY.Value;
                int footprintWidth = building.tilesWide.Value, footprintHeight = building.tilesHigh.Value;
                // A fish pond is the one building whose footprint is mostly water: a knee-high
                // masonry rim around three by three of it (FishPond.isTileFishable). As a wall it
                // blocked lamp light as a five-by-five block, threw a sun shaft's canopy where
                // there is open sky, and told the water pass the pond was not water. The rim is
                // ground: a kerb that low blocks no lamp and casts through the building pass, and
                // the water pass paints the rim tiles by pixel from the game's own water rectangle.
                // Its sprite never rises above its footprint, so there are no roof rows either.
                if (building is StardewValley.Buildings.FishPond && building.daysOfConstructionLeft.Value <= 0)
                {
                    for (int y = footprintTop; y < footprintTop + footprintHeight; y++)
                        for (int x = footprintLeft; x < footprintLeft + footprintWidth; x++)
                        {
                            if (!grid.InBounds(x, y))
                                continue;
                            bool rim = x == footprintLeft || x == footprintLeft + footprintWidth - 1 || y == footprintTop || y == footprintTop + footprintHeight - 1;
                            if (rim)
                                Set(grid, y * mapWidth + x, SurfaceClass.Ground, 0);
                            else
                                Set(grid, y * mapWidth + x, SurfaceClass.Water, -1);
                        }
                    continue;
                }
                int spriteRows = footprintHeight;
                try
                {
                    int sourceHeight = building.getSourceRect().Height;
                    if (sourceHeight > 0)
                        spriteRows = Math.Max(footprintHeight, sourceHeight / 16);
                }
                catch { /* sprite not ready → footprint only */ }

                int roofTop = footprintTop - (spriteRows - footprintHeight);
                for (int y = roofTop; y < footprintTop + footprintHeight; y++)
                    for (int x = footprintLeft; x < footprintLeft + footprintWidth; x++)
                    {
                        if (!grid.InBounds(x, y))
                            continue;
                        // The stamp is the sprite's BOUNDING BOX, and a tall barn's box reaches
                        // several rows past its footprint. On Riverland Farm those rows land on the
                        // river behind the building, and stamping them turned real water into Roof:
                        // the water pass reads this grid, so the overlap came out as a clean
                        // rectangle of untouched vanilla river ("a transparent box"). Water under
                        // an overhanging sprite is still water — the sprite carve already keeps the
                        // effect off the building's own pixels, which is the part that has to be
                        // rectangle-free.
                        int tileIndex = y * mapWidth + x;
                        if (grid._surfaceClasses[tileIndex] == SurfaceClass.Water)
                            continue;
                        Set(grid, tileIndex, y >= footprintTop ? SurfaceClass.Wall : SurfaceClass.Roof, 2);
                    }
            }
        }

        /// <summary>Longest run of non-water tiles that still counts as a bridge. Town's stone
        /// bridge and Forest's plank bridges are 2 tiles thick; 3 leaves headroom for a wide
        /// parapet. Raising this to 6 starts swallowing the land banks BETWEEN two ponds
        /// (measured on Town: 51 tiles at 3, 116 at 6), so it stays small on purpose.</summary>
        private const int MaxSpanTiles = 3;

        /// <summary>
        /// Promotes a NARROW non-water run that has water on both ends to <see cref="SurfaceClass.Deck"/>.
        /// That shape is a bridge: something you walk over with water on either side.
        /// <para>
        /// The per-tile heuristics can only spot a bridge by its <c>Passable</c> property on
        /// Buildings, or by <c>Type=Wood</c> on Back. A STONE bridge drawn straight onto the Back
        /// layer — Town's, and most mod bridges — matches neither, so it fell through to Ground:
        /// height 0, no body above the water, nothing for the mirror to reflect and nothing to stop
        /// the shoreline search from dragging anchors up onto the deck.
        /// </para>
        /// Only tiles that came out <see cref="SurfaceClass.Ground"/> are touched. A Wall verdict
        /// (a blocking Buildings tile: a dam, a cave wall between two pools) keeps it, so this never
        /// turns a light blocker into an open deck. Painted labels are skipped outright.
        /// </summary>
        private static void SpanDecks(SurfaceMap grid, bool[] labelled, int mapWidth, int mapHeight)
        {
            bool IsWater(int tileIndex) => grid._surfaceClasses[tileIndex] == SurfaceClass.Water;

            void Promote(int tileIndex)
            {
                if (!labelled[tileIndex] && grid._surfaceClasses[tileIndex] == SurfaceClass.Ground)
                    Set(grid, tileIndex, SurfaceClass.Deck, 1);
            }

            // Vertical spans: a bridge crossing a river that runs east-west.
            for (int x = 0; x < mapWidth; x++)
            {
                int y = 0;
                while (y < mapHeight)
                {
                    if (IsWater(y * mapWidth + x)) { y++; continue; }
                    int runStart = y;
                    while (y < mapHeight && !IsWater(y * mapWidth + x)) y++;
                    int runEnd = y - 1;
                    if (runStart > 0 && runEnd < mapHeight - 1 && runEnd - runStart + 1 <= MaxSpanTiles
                        && IsWater((runStart - 1) * mapWidth + x) && IsWater((runEnd + 1) * mapWidth + x))
                        for (int spanY = runStart; spanY <= runEnd; spanY++) Promote(spanY * mapWidth + x);
                }
            }

            // Horizontal spans: a bridge crossing a river that runs north-south.
            for (int y = 0; y < mapHeight; y++)
            {
                int rowStart = y * mapWidth, x = 0;
                while (x < mapWidth)
                {
                    if (IsWater(rowStart + x)) { x++; continue; }
                    int runStart = x;
                    while (x < mapWidth && !IsWater(rowStart + x)) x++;
                    int runEnd = x - 1;
                    if (runStart > 0 && runEnd < mapWidth - 1 && runEnd - runStart + 1 <= MaxSpanTiles
                        && IsWater(rowStart + runStart - 1) && IsWater(rowStart + runEnd + 1))
                        for (int spanX = runStart; spanX <= runEnd; spanX++) Promote(rowStart + spanX);
                }
            }
        }

        /// <summary>How many of the 8 neighbours must also be overhead mass before a Front-only
        /// tile counts as a roof. A building top or a painted canopy is a BLOCK of Front tiles and
        /// clears this easily; a lamppost head, a sign, a fence top or a single tuft of grass drawn
        /// above the player has one or two neighbours at most.</summary>
        private const int RoofNeighbours = 3;

        /// <summary>
        /// Demotes Front-only "roofs" that are too thin to shade anything.
        /// <para>
        /// <see cref="Build"/> calls any tile with Front-layer art a roof, and roofs block light.
        /// That was harmless until this mod stopped asking Height Framework for the classification:
        /// the old call returned nothing for the many players who never installed that mod, so sky
        /// occlusion was effectively OFF for them and went live for everyone in one release. On the
        /// Front layer the rule is far too broad — decorative art that merely draws above the player
        /// became a light blocker, and walking into a stretch of map with a lot of it pulled a block
        /// of unlit cells into the flood window and dimmed the screen.
        /// </para>
        /// Labels and Wall verdicts are never touched, and neighbours are counted on a SNAPSHOT so
        /// the pass cannot cascade a whole roof away one ring at a time.
        /// </summary>
        private static void ThinRoofs(SurfaceMap grid, bool[] labelled, int mapWidth, int mapHeight)
        {
            var before = (SurfaceClass[])grid._surfaceClasses.Clone();

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    int tileIndex = y * mapWidth + x;
                    if (labelled[tileIndex] || before[tileIndex] != SurfaceClass.Roof)
                        continue;

                    int mass = 0;
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        int neighbourY = y + offsetY;
                        if (neighbourY < 0 || neighbourY >= mapHeight) continue;
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            int neighbourX = x + offsetX;
                            if ((offsetX == 0 && offsetY == 0) || neighbourX < 0 || neighbourX >= mapWidth) continue;
                            var neighbourSurface = before[neighbourY * mapWidth + neighbourX];
                            if (neighbourSurface is SurfaceClass.Roof or SurfaceClass.Wall)
                                mass++;
                        }
                    }
                    if (mass < RoofNeighbours)
                        Set(grid, tileIndex, SurfaceClass.Ground, 0);
                }
            }
        }

        private static void Set(SurfaceMap grid, int tileIndex, SurfaceClass surfaceClass, sbyte height)
        {
            grid._surfaceClasses[tileIndex] = surfaceClass;
            grid._tileHeights[tileIndex] = height;
        }
    }
}
