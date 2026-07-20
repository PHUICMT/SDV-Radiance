using System;
using System.Runtime.CompilerServices;
using StardewValley;
using xTile.Layers;

namespace SDVRadiance.Integrations
{
    // Built-in height/occlusion classification. This was originally a separate mod
    // (phuicmt.HeightFramework) that Radiance consumed over GetApi; it is now folded in so
    // Radiance ships as one mod with no external dependency. The public API surface is still
    // IHeightFrameworkApi, so the consumer code (ShadowRenderer.Height) is unchanged.

    /// <summary>Coarse surface class inferred per tile. Values match IHeightFrameworkApi's int contract.</summary>
    internal enum HeightClass : byte
    {
        /// <summary>Flat, walkable ground at reference height 0.</summary>
        Ground,
        /// <summary>Below the ground plane (open water).</summary>
        Water,
        /// <summary>A solid, blocking structure base (Buildings-layer tile that isn't passable/shadow).</summary>
        Wall,
        /// <summary>Tall overhead art (Front-layer tile) with no blocking base beneath it.</summary>
        Roof,
        /// <summary>A raised walkable surface — pier/bridge deck (passable Buildings tile, often over water).</summary>
        Deck,
        /// <summary>A hole / off-ledge void.</summary>
        Void,
    }

    /// <summary>
    /// A per-location height/occlusion grid, inferred once from the map's layers and tile
    /// properties (heuristics only). Backed by flat arrays for cache-friendly sweeps.
    ///
    /// The game has no numeric per-tile Z; "height" here is inferred from the vanilla
    /// renderer's own signals: the Buildings layer is collision/wall bases, the Front layer
    /// is tall overhead art, Passable-on-Buildings marks raised decks (piers/bridges), and
    /// the Water tile property marks the sub-ground plane.
    /// </summary>
    internal sealed class HeightMap
    {
        public readonly int Width;
        public readonly int Height;
        private readonly sbyte[] _height;
        private readonly HeightClass[] _surface;

        private HeightMap(int width, int height)
        {
            Width = width;
            Height = height;
            _height = new sbyte[width * height];
            _surface = new HeightClass[width * height];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public sbyte GetHeight(int x, int y) => InBounds(x, y) ? _height[y * Width + x] : (sbyte)0;

        public HeightClass GetSurface(int x, int y) => InBounds(x, y) ? _surface[y * Width + x] : HeightClass.Ground;

        /// <summary>Infer a height grid for a location. Returns null if the map isn't ready.</summary>
        public static HeightMap? Build(GameLocation loc)
        {
            if (loc == null)
                return null;
            var map = loc.Map;
            if (map == null || map.Layers.Count == 0)
                return null;

            Layer baseLayer = map.Layers[0];
            int w = baseLayer.LayerWidth;
            int h = baseLayer.LayerHeight;
            if (w <= 0 || h <= 0)
                return null;

            Layer? buildings = map.GetLayer("Buildings");
            Layer? front = map.GetLayer("Front");

            var hm = new HeightMap(w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    bool hasBuildings = buildings?.Tiles[x, y] != null;
                    bool hasFront = front?.Tiles[x, y] != null;

                    HeightClass cls;
                    sbyte height;

                    if (hasBuildings && loc.doesTileHaveProperty(x, y, "Passable", "Buildings") != null)
                    {
                        // A walk-on-top raised platform: pier/bridge deck.
                        cls = HeightClass.Deck;
                        height = 1;
                    }
                    else if (loc.doesTileHaveProperty(x, y, "Type", "Back") == "Wood")
                    {
                        // Wooden decking on the Back layer = pier / bridge / porch planks (raised).
                        cls = HeightClass.Deck;
                        height = 1;
                    }
                    else if (hasBuildings && loc.doesTileHaveProperty(x, y, "Shadow", "Buildings") == null)
                    {
                        // A blocking Buildings tile that isn't a decorative shadow tile = wall/structure base.
                        cls = HeightClass.Wall;
                        height = 1;
                    }
                    else if (loc.isWaterTile(x, y))
                    {
                        cls = HeightClass.Water;
                        height = -1;
                    }
                    else if (hasFront)
                    {
                        // Tall overhead art with no blocking base beneath (tree canopy, upper wall).
                        cls = HeightClass.Roof;
                        height = 1;
                    }
                    else
                    {
                        cls = HeightClass.Ground;
                        height = 0;
                    }

                    _Set(hm, i, cls, height);
                }
            }

            // Farm buildings (coops/barns/cabins/the farmhouse) are Building ENTITIES, not
            // Buildings-layer tiles, so the per-tile pass above misses them. The footprint
            // (tilesHigh rows at the bottom) is the solid Wall base; the building sprite is
            // usually TALLER than its footprint — the extra rows are roof art the game draws
            // above the footprint at tileY - (spriteRows - tilesHigh). Stamp both, or the roof
            // reads as uncoloured Ground.
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
                {
                    for (int x = bx; x < bx + bw; x++)
                    {
                        if (!hm.InBounds(x, y))
                            continue;
                        // Footprint rows → solid Wall base; the taller roof art above → Roof.
                        _Set(hm, y * w + x, y >= by ? HeightClass.Wall : HeightClass.Roof, (sbyte)2);
                    }
                }
            }

            return hm;
        }

        private static void _Set(HeightMap hm, int i, HeightClass cls, sbyte height)
        {
            hm._surface[i] = cls;
            hm._height[i] = height;
        }
    }

    /// <summary>
    /// Builds and caches one <see cref="HeightMap"/> per location, lazily. Keyed weakly on the
    /// location instance so unloaded locations are collected automatically; a location is only
    /// inferred once per visit.
    /// </summary>
    internal sealed class HeightMapCache
    {
        private readonly ConditionalWeakTable<GameLocation, HeightMap> _maps = new();

        public HeightMap? For(GameLocation? loc)
        {
            if (loc == null)
                return null;
            if (_maps.TryGetValue(loc, out HeightMap? map))
                return map;
            map = HeightMap.Build(loc);
            if (map != null)
                _maps.Add(loc, map);
            return map;
        }

        /// <summary>Drop the cached map for a location (e.g. after a building was added/removed).</summary>
        public void Invalidate(GameLocation? loc)
        {
            if (loc != null)
                _maps.Remove(loc);
        }

        /// <summary>Drop every cached map (e.g. on save load).</summary>
        public void Clear() => _maps.Clear();
    }

    /// <summary>
    /// In-process implementation of <see cref="IHeightFrameworkApi"/> (formerly the separate
    /// Height Framework mod). Radiance instantiates this directly instead of resolving an
    /// external mod's API.
    /// </summary>
    internal sealed class HeightProvider : IHeightFrameworkApi
    {
        private readonly HeightMapCache _cache = new();

        public int GetSurfaceAt(GameLocation location, int tileX, int tileY) =>
            (int)(_cache.For(location)?.GetSurface(tileX, tileY) ?? HeightClass.Ground);

        public int GetHeightAt(GameLocation location, int tileX, int tileY) =>
            _cache.For(location)?.GetHeight(tileX, tileY) ?? 0;

        public bool IsOccluder(GameLocation location, int tileX, int tileY) =>
            (_cache.For(location)?.GetSurface(tileX, tileY) ?? HeightClass.Ground) == HeightClass.Wall;

        public bool IsWaterSurface(GameLocation location, int tileX, int tileY) =>
            (_cache.For(location)?.GetSurface(tileX, tileY) ?? HeightClass.Ground) == HeightClass.Water;

        /// <summary>Drop the cached map for a location (call on BuildingListChanged).</summary>
        public void Invalidate(GameLocation? loc) => _cache.Invalidate(loc);

        /// <summary>Drop every cached map (call on SaveLoaded).</summary>
        public void Clear() => _cache.Clear();
    }
}
