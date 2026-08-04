using System;
using System.Runtime.CompilerServices;
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
        private readonly sbyte[] _height;
        private readonly SurfaceClass[] _surface;

        private static readonly ConditionalWeakTable<GameLocation, SurfaceMap> _cache = new();

        private SurfaceMap(int width, int height)
        {
            Width = width;
            Height = height;
            _height = new sbyte[width * height];
            _surface = new SurfaceClass[width * height];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public sbyte GetHeight(int x, int y) => InBounds(x, y) ? _height[y * Width + x] : (sbyte)0;

        public SurfaceClass GetSurface(int x, int y)
            => InBounds(x, y) ? _surface[y * Width + x] : SurfaceClass.Ground;

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
        public static SurfaceMap? For(GameLocation? loc)
        {
            if (loc == null)
                return null;
            if (_cache.TryGetValue(loc, out SurfaceMap? map))
                return map;
            try { map = Build(loc); }
            catch { map = null; }
            if (map != null)
                _cache.Add(loc, map);
            return map;
        }

        public static void Invalidate(GameLocation? loc)
        {
            if (loc != null)
                _cache.Remove(loc);
        }

        /// <summary>Drop everything (save load, or a label reload during development).</summary>
        public static void Clear() => _cache.Clear();

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

        private static SurfaceMap? Build(GameLocation loc)
        {
            var map = loc.Map;
            if (map == null || map.Layers.Count == 0)
                return null;

            Layer baseLayer = map.Layers[0];
            int w = baseLayer.LayerWidth, h = baseLayer.LayerHeight;
            if (w <= 0 || h <= 0)
                return null;

            Layer? back = map.GetLayer("Back");
            Layer? buildings = map.GetLayer("Buildings");
            Layer? front = map.GetLayer("Front");
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
                    if (labels != null)
                    {
                        SurfaceClass? lc = null;
                        if (labels.Get(buildings, x, y) is { } bb) lc = ClassFromLabels(bb, overlay: true);
                        if (lc == null && labels.Get(back, x, y) is { } gb) lc = ClassFromLabels(gb, overlay: false);
                        if (lc == null && labels.Get(front, x, y) is { } fb) lc = ClassFromLabels(fb, overlay: true);
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

                    SurfaceClass cls;
                    sbyte height;
                    bool passableB = hasBuildings && loc.doesTileHaveProperty(x, y, "Passable", "Buildings") != null;
                    if (passableB && buildings!.Tiles[x, y] is xTile.Tiles.AnimatedTile && loc.isWaterTile(x, y))
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
                    else if (loc.doesTileHaveProperty(x, y, "Type", "Back") == "Wood")
                    {
                        cls = SurfaceClass.Deck;      // Back-layer planking: pier / bridge / porch
                        height = 1;
                    }
                    else if (hasBuildings && loc.doesTileHaveProperty(x, y, "Shadow", "Buildings") == null)
                    {
                        cls = SurfaceClass.Wall;      // blocking Buildings tile that isn't decorative shadow art
                        height = 1;
                    }
                    else if (loc.isWaterTile(x, y))
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

            // Farm buildings (coops, barns, cabins, the farmhouse) are Building ENTITIES, not
            // Buildings-layer tiles, so the per-tile pass misses them. The footprint rows are the
            // solid Wall base; the sprite is usually TALLER than the footprint and the game draws
            // those extra rows above it, so stamp them as Roof or they read as open Ground.
            foreach (var building in loc.buildings)
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
                        if (sm.InBounds(x, y))
                            Set(sm, y * w + x, y >= by ? SurfaceClass.Wall : SurfaceClass.Roof, (sbyte)2);
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
            bool IsWater(int i) => sm._surface[i] == SurfaceClass.Water;

            void Promote(int i)
            {
                if (!labelled[i] && sm._surface[i] == SurfaceClass.Ground)
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

        private static void Set(SurfaceMap sm, int i, SurfaceClass cls, sbyte height)
        {
            sm._surface[i] = cls;
            sm._height[i] = height;
        }
    }
}
