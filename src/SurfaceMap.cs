using System;
using System.Runtime.CompilerServices;
using StardewModdingAPI;
using StardewValley;
using xTile.Layers;

namespace SDVRadiance
{
    /// <summary>Coarse per-tile surface class. Values are stable: 0 Ground, 1 Water, 2 Wall,
    /// 3 Roof, 4 Deck, 5 Void.</summary>
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

        private static readonly ConditionalWeakTable<GameLocation, SurfaceMap> _locationCache = new();

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
            var c = GetSurface(x, y);
            return c == SurfaceClass.Wall || c == SurfaceClass.Roof;
        }

        // ---- cache ---------------------------------------------------------------------------

        /// <summary>The grid for a location, built on first use. Keyed weakly, so unloaded
        /// locations are collected on their own. Hoist this out of per-tile loops.</summary>
        public static SurfaceMap? For(GameLocation? location)
        {
            if (location == null)
                return null;
            if (_locationCache.TryGetValue(location, out SurfaceMap? map))
                return map;
            // Breadcrumbs, not a perf counter: this is a whole-map walk that runs once when a
            // location is first drawn, and a freeze report can only be pinned to it if the log
            // shows the walk STARTED and never finished. Trace always lands in the SMAPI log
            // file, so a reporter needs no debug switch for it to be there after a hard stop.
            DiagnosticMonitor?.Log($"[location] surface build start: {location.NameOrUniqueName}", LogLevel.Trace);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { map = Build(location); }
            catch (Exception ex) { map = null; DiagnosticMonitor?.Log($"[location] surface build threw: {ex.Message}", LogLevel.Warn); }
            sw.Stop();
            DiagnosticMonitor?.Log($"[location] surface build done: {location.NameOrUniqueName} {map?.Width ?? 0}x{map?.Height ?? 0} in {sw.Elapsed.TotalMilliseconds:0.0}ms", LogLevel.Trace);
            if (map != null)
                _locationCache.Add(location, map);
            return map;
        }

        /// <summary>Optional diagnostics sink (set at startup) — see the breadcrumbs in <see cref="For"/>.</summary>
        internal static IMonitor? DiagnosticMonitor;

        public static void Invalidate(GameLocation? location)
        {
            if (location != null)
                _locationCache.Remove(location);
        }

        /// <summary>Drop everything (save load, or a label reload during development).</summary>
        public static void Clear() => _locationCache.Clear();

        // ---- inference -----------------------------------------------------------------------

        /// <summary>
        /// Tile class from 256 per-pixel labels, or null when the labels aren't decisive.
        /// Extended studio classes fold in: emissive / reflect_floor are Ground, mirror and
        /// window sit in a wall plane. <paramref name="overlay"/> relaxes the water threshold,
        /// because overlay art (a surf wash, a fountain rim) is mostly transparent, so a sparse
        /// patch of labeled water pixels already means something.
        /// </summary>
        private static SurfaceClass? ClassFromLabels(byte[] b, bool overlay)
        {
            int water = 0, deck = 0, wall = 0, roof = 0, ground = 0, glass = 0;
            for (int p = 0; p < 256; p++)
            {
                switch (b[p])
                {
                    case 1: case 9: case 10: case 11: case 14: water++; break;  // water / ice / falling / lava / hot
                    case 2: case 8: wall++; break;                      // wall / mirror (backed: blocks)
                    case 3: roof++; break;
                    case 4: deck++; break;
                    // A WINDOW is a hole in a wall with glass in it, and a glass roof is a
                    // skylight: light goes through both. Folding 12 in with `wall` made a painted
                    // window BLOCK the lamp light it is supposed to let past — a display case in
                    // Pierre's shop threw a hard shadow across the goods behind it.
                    case 12: case 13: glass++; break;
                    case 5: break;                                      // void: never decisive on its own
                    default: ground++; break;                           // 0 ground, 6 emissive, 7 reflect_floor
                }
            }
            // Order matters: a deck plank drawn OVER water has to read Deck, not Water.
            if (deck >= 64) return SurfaceClass.Deck;
            if (wall >= 64) return SurfaceClass.Wall;
            if (glass >= 64) return SurfaceClass.Glass;
            if (water >= (overlay ? 48 : 128)) return SurfaceClass.Water;
            if (roof >= 64) return SurfaceClass.Roof;
            // A window PANE is a small part of its tile — the frame and the wall around it take
            // the rest — so it never reaches the 64-pixel bar above. 8 is the same bar the window
            // LIGHT scan uses (RenderPipeline.Lighting.EnsureWindowCache), so the two agree: a
            // tile bright enough to emit window light is a tile light can pass through.
            if (glass >= 8) return SurfaceClass.Glass;
            if (!overlay && ground >= 192) return SurfaceClass.Ground;
            return null;
        }

        private static SurfaceMap? Build(GameLocation location)
        {
            var map = location.Map;
            if (map == null || map.Layers.Count == 0)
                return null;

            Layer baseLayer = map.Layers[0];
            int w = baseLayer.LayerWidth, h = baseLayer.LayerHeight;
            if (w <= 0 || h <= 0)
                return null;

            Layer? back = map.GetLayer("Back");
            Layer? buildings = map.GetLayer("Buildings");
            Layer? front = map.GetLayer("Front");
            // Layers the canonical trio never covers (SVE puts water art on Back2,
            // vanilla waterfalls live on AlwaysFront). Consulted below ONLY when the trio
            // yields no verdict, so no tile that already classifies changes class.
            Layer? back2 = map.GetLayer("Back2");
            Layer? buildings2 = map.GetLayer("Buildings2");
            Layer? front2 = map.GetLayer("Front2");
            Layer? alwaysFront = map.GetLayer("AlwaysFront");
            Layer? alwaysFront2 = map.GetLayer("AlwaysFront2");
            var labels = LabelStore.Instance;
            if (labels is { Any: false })
                labels = null;

            var sm = new SurfaceMap(w, h);
            // Which tiles a painted label decided. The span pass below must never overrule them
            // (iron rule: a label beats every heuristic, including the ones that run after it).
            bool[] labelled = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    bool hasBuildings = buildings?.Tiles[x, y] != null;
                    bool hasFront = front?.Tiles[x, y] != null;

                    // ---- LABELS FIRST: painted ground truth beats every heuristic below. ----
                    // Buildings decides first (a deck plank or fountain rim sits OVER the Back
                    // tile), then Back, then Front (overhead art).
                    bool anyLabel = false;
                    if (labels != null)
                    {
                        SurfaceClass? lc = null;
                        if (labels.Get(buildings, x, y) is { } bb) { anyLabel = true; lc = ClassFromLabels(bb, overlay: true); }
                        if (lc == null && labels.Get(back, x, y) is { } gb) { anyLabel = true; lc = ClassFromLabels(gb, overlay: false); }
                        if (lc == null && labels.Get(front, x, y) is { } fb) { anyLabel = true; lc = ClassFromLabels(fb, overlay: true); }
                        // Additive fallback to the layers above — a Town waterfall labelled
                        // flow:256 on AlwaysFront was never declared water at all, so its
                        // liquid never reached the mask (the compose already honours these
                        // labels for the carve and sub-type; classification was the gap).
                        if (lc == null && labels.Get(buildings2, x, y) is { } b2) { anyLabel = true; lc = ClassFromLabels(b2, overlay: true); }
                        if (lc == null && labels.Get(back2, x, y) is { } g2) { anyLabel = true; lc = ClassFromLabels(g2, overlay: false); }
                        if (lc == null && labels.Get(front2, x, y) is { } f2) { anyLabel = true; lc = ClassFromLabels(f2, overlay: true); }
                        if (lc == null && labels.Get(alwaysFront, x, y) is { } af) { anyLabel = true; lc = ClassFromLabels(af, overlay: true); }
                        if (lc == null && labels.Get(alwaysFront2, x, y) is { } af2) { anyLabel = true; lc = ClassFromLabels(af2, overlay: true); }
                        // A liquid OVERLAY beats a dry base verdict: a falls' base tile carries
                        // Back "ground" (the cliff) under a Front/AlwaysFront falls labelled
                        // flow, and what the player sees there is falling water — the Ground
                        // verdict blocked the whole tile from ever entering the mask (256/256
                        // missing at every falls base). Only Ground gives way; Deck/Wall/Roof
                        // keep their say, so a plank over water still reads as a deck.
                        if (lc is null or SurfaceClass.Ground)
                        {
                            foreach (var overlayLayer in new[] { front, front2, alwaysFront, alwaysFront2 })
                            {
                                if (overlayLayer == null || labels.Get(overlayLayer, x, y) is not { } ol)
                                    continue;
                                if (ClassFromLabels(ol, overlay: true) == SurfaceClass.Water)
                                {
                                    anyLabel = true;
                                    lc = SurfaceClass.Water;
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
                        if (lc == SurfaceClass.Water && hasBuildings
                            && buildings!.Tiles[x, y] is not xTile.Tiles.AnimatedTile
                            && (location.doesTileHaveProperty(x, y, "Passable", "Buildings") != null
                                || location.doesTileHaveProperty(x, y, "Type", "Buildings") == "Wood"))
                            lc = SurfaceClass.Deck;

                        if (lc is { } decided)
                        {
                            Set(sm, i, decided, decided switch
                            {
                                SurfaceClass.Water => (sbyte)-1,
                                SurfaceClass.Ground => (sbyte)0,
                                SurfaceClass.Void => (sbyte)0,
                                _ => (sbyte)1,
                            });
                            labelled[i] = true;
                            continue;
                        }
                    }
                    // A PAINTED but mixed tile (a tide pool's rock rim: water:99 + ground:157,
                    // decisive for neither) still protects itself from the span pass — the author
                    // told us what it is, and it is not a bridge. SpanDecks promoting these rims
                    // to Deck made the compose scrub the pool's march whole-tile: ripple, no
                    // reflection, on every beach tide pool.
                    if (anyLabel)
                        labelled[i] = true;

                    SurfaceClass cls;
                    sbyte height;
                    bool passableB = hasBuildings && location.doesTileHaveProperty(x, y, "Passable", "Buildings") != null;
                    if (passableB && buildings!.Tiles[x, y] is xTile.Tiles.AnimatedTile && location.isWaterTile(x, y))
                    {
                        // An ANIMATED passable Buildings tile over water IS the water surface —
                        // the beach surf wash. The deck rule below used to call it a pier and ate
                        // the whole tide line. Real decks are static art.
                        cls = SurfaceClass.Water;
                        height = -1;
                    }
                    else if (passableB)
                    {
                        cls = SurfaceClass.Deck;      // walk-on-top raised platform: pier / bridge
                        height = 1;
                    }
                    else if (location.doesTileHaveProperty(x, y, "Type", "Back") == "Wood")
                    {
                        cls = SurfaceClass.Deck;      // Back-layer planking: pier / bridge / porch
                        height = 1;
                    }
                    else if (hasBuildings && location.doesTileHaveProperty(x, y, "Shadow", "Buildings") == null)
                    {
                        cls = SurfaceClass.Wall;      // blocking Buildings tile that isn't decorative shadow art
                        height = 1;
                    }
                    else if (location.isWaterTile(x, y))
                    {
                        cls = SurfaceClass.Water;
                        height = -1;
                    }
                    else if (hasFront)
                    {
                        cls = SurfaceClass.Roof;      // tall overhead art with no blocking base
                        height = 1;
                    }
                    else
                    {
                        cls = SurfaceClass.Ground;
                        height = 0;
                    }

                    Set(sm, i, cls, height);
                }
            }

            SpanDecks(sm, labelled, w, h);
            ThinRoofs(sm, labelled, w, h);

            // Farm buildings (coops, barns, cabins, the farmhouse) are Building ENTITIES, not
            // Buildings-layer tiles, so the per-tile pass misses them. The footprint rows are the
            // solid Wall base; the sprite is usually TALLER than the footprint and the game draws
            // those extra rows above it, so stamp them as Roof or they read as open Ground.
            foreach (var building in location.buildings)
            {
                if (building == null)
                    continue;
                int bx = building.tileX.Value, by = building.tileY.Value;
                int bw = building.tilesWide.Value, bh = building.tilesHigh.Value;
                int spriteRows = bh;
                try
                {
                    int srcH = building.getSourceRect().Height;
                    if (srcH > 0)
                        spriteRows = Math.Max(bh, srcH / 16);
                }
                catch { /* sprite not ready → footprint only */ }

                int roofTop = by - (spriteRows - bh);
                for (int y = roofTop; y < by + bh; y++)
                    for (int x = bx; x < bx + bw; x++)
                    {
                        if (!sm.InBounds(x, y))
                            continue;
                        // The stamp is the sprite's BOUNDING BOX, and a tall barn's box reaches
                        // several rows past its footprint. On Riverland Farm those rows land on the
                        // river behind the building, and stamping them turned real water into Roof:
                        // the water pass reads this grid, so the overlap came out as a clean
                        // rectangle of untouched vanilla river ("a transparent box"). Water under
                        // an overhanging sprite is still water — the sprite carve already keeps the
                        // effect off the building's own pixels, which is the part that has to be
                        // rectangle-free.
                        int i2 = y * w + x;
                        if (sm._surfaceClasses[i2] == SurfaceClass.Water)
                            continue;
                        Set(sm, i2, y >= by ? SurfaceClass.Wall : SurfaceClass.Roof, (sbyte)2);
                    }
            }

            return sm;
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
        private static void SpanDecks(SurfaceMap sm, bool[] labelled, int w, int h)
        {
            bool IsWater(int i) => sm._surfaceClasses[i] == SurfaceClass.Water;

            void Promote(int i)
            {
                if (!labelled[i] && sm._surfaceClasses[i] == SurfaceClass.Ground)
                    Set(sm, i, SurfaceClass.Deck, (sbyte)1);
            }

            // Vertical spans: a bridge crossing a river that runs east-west.
            for (int x = 0; x < w; x++)
            {
                int y = 0;
                while (y < h)
                {
                    if (IsWater(y * w + x)) { y++; continue; }
                    int s = y;
                    while (y < h && !IsWater(y * w + x)) y++;
                    int e = y - 1;
                    if (s > 0 && e < h - 1 && e - s + 1 <= MaxSpanTiles
                        && IsWater((s - 1) * w + x) && IsWater((e + 1) * w + x))
                        for (int yy = s; yy <= e; yy++) Promote(yy * w + x);
                }
            }

            // Horizontal spans: a bridge crossing a river that runs north-south.
            for (int y = 0; y < h; y++)
            {
                int row = y * w, x = 0;
                while (x < w)
                {
                    if (IsWater(row + x)) { x++; continue; }
                    int s = x;
                    while (x < w && !IsWater(row + x)) x++;
                    int e = x - 1;
                    if (s > 0 && e < w - 1 && e - s + 1 <= MaxSpanTiles
                        && IsWater(row + s - 1) && IsWater(row + e + 1))
                        for (int xx = s; xx <= e; xx++) Promote(row + xx);
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
        private static void ThinRoofs(SurfaceMap sm, bool[] labelled, int w, int h)
        {
            var before = (SurfaceClass[])sm._surfaceClasses.Clone();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (labelled[i] || before[i] != SurfaceClass.Roof)
                        continue;

                    int mass = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if ((dx == 0 && dy == 0) || xx < 0 || xx >= w) continue;
                            var c = before[yy * w + xx];
                            if (c == SurfaceClass.Roof || c == SurfaceClass.Wall)
                                mass++;
                        }
                    }
                    if (mass < RoofNeighbours)
                        Set(sm, i, SurfaceClass.Ground, (sbyte)0);
                }
            }
        }

        private static void Set(SurfaceMap sm, int i, SurfaceClass cls, sbyte height)
        {
            sm._surfaceClasses[i] = cls;
            sm._tileHeights[i] = height;
        }
    }
}
