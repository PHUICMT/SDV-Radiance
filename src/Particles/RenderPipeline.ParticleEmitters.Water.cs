using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using xTile.Layers;

namespace SDVRadiance
{
    /// <summary>
    /// The particles water and heat give off: mist at the foot of a waterfall, steam over a hot
    /// spring, sparks off lava.
    ///
    /// <para>
    /// All three read the same painted labels the water mask reads, so their sources cost nothing
    /// to set up: a tile whose art is class 10 falling water, 14 hot water or 11 lava IS the
    /// emitter, on any map from any mod, with nothing to configure. The label file even predicted
    /// this: the class 14 comment has said "steam comes in v2" since the class was added.
    /// </para>
    ///
    /// <para>
    /// The one judgement call is what counts as a WATERFALL. Class 10 marks everything that flows,
    /// and since 1.6.2 that includes the surf running up a beach, which must not smoke. A fall is
    /// vertical: a column of flowing tiles at least <see cref="MistShortestFall"/> tall, and the
    /// mist belongs at the bottom tile of that column, where the water lands. Surf is one tile
    /// tall everywhere, so the same test that finds every fall skips every beach.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Flowing tiles stacked in one column before it is called a fall. Two catches
        /// short cascades; the cost of three is that a two-tile trickle stays dry, and the cost of
        /// two would be mist on any pair of stacked flow labels, which some map edges have.</summary>
        private const int MistShortestFall = 3;

        /// <summary>How far above the screen top the scan reaches, in tiles, so a fall whose lip
        /// is off screen still counts the rows that make it a fall.</summary>
        private const int MistScanAboveScreen = 5;

        /// <summary>Sources one screen may run at once, each kind. The pool is shared with every
        /// other emitter, and a volcano floor is made of lava; uncapped it would spend the whole
        /// pool on itself.</summary>
        private const int MistFootLimit = 8;
        private const int SteamTileLimit = 12;
        private const int LavaTileLimit = 16;

        /// <summary>Labelled pixels of the class in a tile before the tile is a source. A few
        /// stray pixels are an edge the label ran over, not a spring.</summary>
        private const int BreathClassPixelFloor = 32;

        /// <summary>A fall's run may CONTINUE through a tile this thinly labelled, though such a
        /// tile never counts toward the height. Waterfall art thins toward its foot - foam, spray,
        /// translucency - and the town's repainted fall is labelled five flowing pixels on its
        /// edge tiles. Cutting the run at the strong threshold put the foot mid-column, and a
        /// white puff on white falling water is invisible; the foot belongs where the art ends.</summary>
        private const int MistRunContinuePixelFloor = 8;

        private const float MistPuffsPerFootPerSecond = 8f;
        private const float SteamWispsPerTilePerSecond = 0.9f;
        private const float LavaSparksPerTilePerSecond = 1.0f;

        /// <summary>Mist and steam are AMBIENT: they are wet air the world's light falls on, so
        /// they take the night, the lamp pools and the grade like any map pixel. Sparks are
        /// EMISSIVE for the same reason embers are: a spark is its own light.</summary>
        private static readonly Color MistTint = new(0.86f, 0.92f, 0.96f);
        private static readonly Color SteamTint = new(0.94f, 0.95f, 0.97f);
        private static readonly Vector3 LavaSparkColour = new(1.0f, 0.58f, 0.20f);

        /// <summary>The heat grid the finishing pass bends the air with: one texel per world
        /// tile of the scan window, red = hot ground (labelled hot or lava, or the volcano's
        /// own). Built here because the scan is already looking at every tile; the haze must not
        /// depend on the particle switch, so the pipeline calls the scan itself each frame too
        /// (it early-outs unless the camera crossed a tile).</summary>
        private Microsoft.Xna.Framework.Graphics.Texture2D? _heatMapTexture;
        private Color[] _heatGridCells = Array.Empty<Color>();
        private Vector2 _heatMapOriginTiles, _heatMapSizeTiles;
        private bool _heatOnScreen;

        internal bool HeatOnScreen => _heatOnScreen;

        /// <summary>Where each screen's camera was when its scan last ran. The window a scan
        /// covers is the camera's, so this one is per screen on purpose: with a single slot the
        /// two screens' crossings undid each other and the scan ran twice a frame forever
        /// (0.34 ms a frame against 0.003 with one screen, measured on a farm).</summary>
        private readonly Dictionary<int, (GameLocation? Location, int TileX, int TileY, int LabelVersion)> _breathScanByScreen = new();
        private GameLocation? _breathScanLocation;
        private int _breathScanTileX = int.MinValue, _breathScanTileY = int.MinValue;
        private int _breathScanLabelVersion = -1;

        /// <summary>What the scan learned about each tile of the current map, kept for the
        /// location: how many pixels flow, run hot, are molten. A tile is asked of the labels
        /// once per location and label version, and every window after that reads bytes.
        ///
        /// <para>The scan used to ask the labels afresh for every tile of the window each time
        /// the camera crossed a tile: four layers, a dictionary walk and an orient per layer,
        /// about 2.5 ms for a window. Walking paid it every tile, and a split screen paid it
        /// twice a frame, because the two cameras took turns and each one's crossing undid the
        /// other's early-out. The report's "stage list + builders" step read 5.2 ms with two
        /// screens on one farm against 0.14 for one. The window still decides what is on
        /// screen; only the label reads are remembered.</para></summary>
        private GameLocation? _breathTileCacheLocation;
        private int _breathTileCacheLabelVersion = -1;
        private int _breathTileCacheWidth, _breathTileCacheHeight;
        private byte[] _breathTileFlow = Array.Empty<byte>();
        private byte[] _breathTileHot = Array.Empty<byte>();
        private byte[] _breathTileLava = Array.Empty<byte>();
        private bool[] _breathTileScanned = Array.Empty<bool>();

        private void EnsureBreathTileCache(GameLocation location, int labelVersion)
        {
            if (ReferenceEquals(location, _breathTileCacheLocation) && labelVersion == _breathTileCacheLabelVersion)
                return;
            int width = 0, height = 0;
            foreach (Layer layer in _breathScanLayers)
            {
                width = Math.Max(width, layer.LayerWidth);
                height = Math.Max(height, layer.LayerHeight);
            }
            _breathTileCacheLocation = location;
            _breathTileCacheLabelVersion = labelVersion;
            _breathTileCacheWidth = width;
            _breathTileCacheHeight = height;
            int tiles = width * height;
            if (_breathTileScanned.Length < tiles)
            {
                _breathTileFlow = new byte[tiles];
                _breathTileHot = new byte[tiles];
                _breathTileLava = new byte[tiles];
                _breathTileScanned = new bool[tiles];
            }
            else
            {
                Array.Clear(_breathTileScanned, 0, tiles);
            }
        }

        /// <summary>The three pixel counts for one tile, from the cache when it has them and from
        /// the labels once when it does not. Tiles outside the map are asked directly, which
        /// answers zero.</summary>
        private void BreathClassesAt(LabelStore? labels, int x, int y, out int flowing, out int hot, out int lava)
        {
            flowing = hot = lava = 0;
            bool inside = x >= 0 && y >= 0 && x < _breathTileCacheWidth && y < _breathTileCacheHeight;
            int index = inside ? y * _breathTileCacheWidth + x : -1;
            if (inside && _breathTileScanned[index])
            {
                flowing = _breathTileFlow[index];
                hot = _breathTileHot[index];
                lava = _breathTileLava[index];
                return;
            }
            for (int layerIndex = 0; layerIndex < _breathScanLayers.Count; layerIndex++)
                CountBreathClasses(labels?.Get(_breathScanLayers[layerIndex], x, y), ref flowing, ref hot, ref lava);
            if (!inside)
                return;
            _breathTileFlow[index] = (byte)Math.Min(255, flowing);
            _breathTileHot[index] = (byte)Math.Min(255, hot);
            _breathTileLava[index] = (byte)Math.Min(255, lava);
            _breathTileScanned[index] = true;
        }
        private readonly List<Vector2> _mistFeet = new();
        private readonly List<Vector2> _steamTiles = new();
        private readonly List<Vector2> _lavaTiles = new();
        /// <summary>Flow pixel counts for the scan window (clamped to 255), reused across scans.
        /// Indexed column-major so a column's run check walks it contiguously.</summary>
        private byte[] _breathFlowColumnCounts = Array.Empty<byte>();
        private readonly List<Layer> _breathScanLayers = new();
        private float _mistSpawnCarry, _steamSpawnCarry, _lavaSpawnCarry;

        internal int ParticleMistFeet => _mistFeet.Count;
        internal int ParticleSteamTiles => _steamTiles.Count;
        internal int ParticleLavaTiles => _lavaTiles.Count;

        /// <summary>Refresh the source lists when the camera has crossed a tile, the map changed,
        /// or the labels were reloaded. Between those, spawning reads the lists and touches no
        /// label at all: LabelStore answers cost a dictionary walk and an orient per tile, which
        /// is fine on a tile crossing and is not fine sixty times a second.</summary>
        private void ScanWaterBreathSources()
        {
            GameLocation? location = Game1.currentLocation;
            int cameraTileX = Game1.viewport.X / 64, cameraTileY = Game1.viewport.Y / 64;
            int labelVersion = LabelStore.Instance?.Version ?? 0;
            int screenId = _activeScreenId;
            if (_breathScanByScreen.TryGetValue(screenId, out var last)
                && ReferenceEquals(location, last.Location)
                && cameraTileX == last.TileX && cameraTileY == last.TileY
                && labelVersion == last.LabelVersion)
                return;
            _breathScanByScreen[screenId] = (location, cameraTileX, cameraTileY, labelVersion);
            _breathScanLocation = location;
            _breathScanTileX = cameraTileX;
            _breathScanTileY = cameraTileY;
            _breathScanLabelVersion = labelVersion;
            _mistFeet.Clear();
            _steamTiles.Clear();
            _lavaTiles.Clear();
            _heatOnScreen = false;
            if (location?.map == null)
                return;

            LabelStore? labels = LabelStore.Instance;
            // Every layer that can carry the art, the way the mask gathers them: waterfalls in
            // particular animate on AlwaysFront on many maps, and a scan of the named three read
            // mistFeet=0 under a fall the mask knew perfectly well was falling.
            _breathScanLayers.Clear();
            foreach (Layer layer in location.map.Layers)
                if (MapLayers.BelongsToFamily(layer.Id, "Back") || MapLayers.BelongsToFamily(layer.Id, "Buildings")
                    || MapLayers.BelongsToFamily(layer.Id, "Front") || MapLayers.BelongsToFamily(layer.Id, "AlwaysFront"))
                    _breathScanLayers.Add(layer);
            EnsureBreathTileCache(location, labelVersion);

            int firstTileX = Math.Max(0, cameraTileX - 1);
            int firstTileY = Math.Max(0, cameraTileY - MistScanAboveScreen);
            int lastTileX = cameraTileX + Game1.viewport.Width / 64 + 1;
            int lastTileY = cameraTileY + Game1.viewport.Height / 64 + 1;
            int columns = lastTileX - firstTileX + 1;
            int rows = lastTileY - firstTileY + 1;
            if (columns <= 0 || rows <= 0)
                return;
            if (_breathFlowColumnCounts.Length < columns * rows)
                _breathFlowColumnCounts = new byte[columns * rows];
            Array.Clear(_breathFlowColumnCounts, 0, columns * rows);
            if (_heatGridCells.Length < columns * rows)
                _heatGridCells = new Color[columns * rows];
            Array.Clear(_heatGridCells, 0, columns * rows);

            // The volcano's lava is not labelled: the game draws it with the water machinery and
            // the mask tags the whole location instead, so the sparks follow the same rule.
            string locationName = location.NameOrUniqueName ?? location.Name ?? "";
            bool wholeLocationIsLava = location is StardewValley.Locations.VolcanoDungeon
                || locationName.Contains("Caldera", StringComparison.OrdinalIgnoreCase)
                || locationName.Contains("Volcano", StringComparison.OrdinalIgnoreCase)
                || (location is StardewValley.Locations.MineShaft mine && mine.getMineArea() == 80);

            for (int x = firstTileX; x <= lastTileX; x++)
            {
                for (int y = firstTileY; y <= lastTileY; y++)
                {
                    BreathClassesAt(labels, x, y, out int flowingPixels, out int hotPixels, out int lavaPixels);
                    _breathFlowColumnCounts[(x - firstTileX) * rows + (y - firstTileY)]
                        = (byte)Math.Min(255, flowingPixels);
                    if (hotPixels >= BreathClassPixelFloor && _steamTiles.Count < SteamTileLimit)
                        _steamTiles.Add(new Vector2(x * 64f + 32f, y * 64f + 32f));
                    bool moltenHere = lavaPixels >= BreathClassPixelFloor
                        || (wholeLocationIsLava && location.waterTiles != null
                            && x < location.waterTiles.waterTiles.GetLength(0)
                            && y < location.waterTiles.waterTiles.GetLength(1)
                            && location.waterTiles[x, y]);
                    if (moltenHere && _lavaTiles.Count < LavaTileLimit)
                        _lavaTiles.Add(new Vector2(x * 64f + 32f, y * 64f + 32f));
                    // The haze grid takes every molten tile, past the particle caps: the shimmer
                    // covering half a lava floor but not the other half would read as a fault.
                    // Hot springs are left out on purpose: their air is wet and the steam already
                    // says hot, and a shimmer over the bathhouse read as the picture breaking.
                    if (moltenHere)
                    {
                        _heatGridCells[(y - firstTileY) * columns + (x - firstTileX)] = Color.White;
                        _heatOnScreen = true;
                    }
                }
            }

            // A fall is a vertical run of STRONGLY flowing tiles, and the mist belongs where
            // that run lands: the first tile below it, which on a two-tier fall is the upper
            // plunge, not the bottom of everything labelled flowing (the first version put the
            // puff on the lower shelf, and the report was "why is it down there"). One thin tile
            // inside a fall does not end it - the town fall's repaint labels a mid tile five
            // pixels - but a thin tile followed by another non-strong tile is the landing. Each
            // tier tall enough earns its own foot.
            for (int column = 0; column < columns && _mistFeet.Count < MistFootLimit; column++)
            {
                int strongRun = 0;
                for (int row = 0; row <= rows; row++)
                {
                    int flowingPixels = row < rows ? _breathFlowColumnCounts[column * rows + row] : 0;
                    if (flowingPixels >= BreathClassPixelFloor)
                    {
                        strongRun++;
                        continue;
                    }
                    int flowingBelow = row + 1 < rows ? _breathFlowColumnCounts[column * rows + row + 1] : 0;
                    if (strongRun > 0 && flowingPixels >= MistRunContinuePixelFloor
                        && flowingBelow >= BreathClassPixelFloor)
                        continue;   // a thin tile inside the fall, with the fall carrying on below
                    if (strongRun >= MistShortestFall && _mistFeet.Count < MistFootLimit)
                        _mistFeet.Add(new Vector2((firstTileX + column) * 64f + 32f,
                                                  (firstTileY + row) * 64f + 16f));
                    strongRun = 0;
                }
            }

            UploadHeatMap(columns, rows, firstTileX, firstTileY);
        }

        /// <summary>Hand the scan's heat cells to the GPU. Recreated only when the window size
        /// changes; a scan that found no heat still uploads, so the texture the shader samples
        /// is never a stale window's.</summary>
        private void UploadHeatMap(int columns, int rows, int firstTileX, int firstTileY)
        {
            if (_heatMapTexture == null || _heatMapTexture.Width != columns || _heatMapTexture.Height != rows)
            {
                _heatMapTexture?.Dispose();
                _heatMapTexture = new Microsoft.Xna.Framework.Graphics.Texture2D(_device, columns, rows);
            }
            _heatMapTexture.SetData(_heatGridCells, 0, columns * rows);
            _heatMapOriginTiles = new Vector2(firstTileX, firstTileY);
            _heatMapSizeTiles = new Vector2(columns, rows);
        }

        private static void CountBreathClasses(byte[]? classes, ref int flowing, ref int hot, ref int lava)
        {
            if (classes == null)
                return;
            for (int p = 0; p < classes.Length; p++)
            {
                byte c = classes[p];
                if (c == 10) flowing++;
                else if (c == 14) hot++;
                else if (c == 11) lava++;
            }
        }

        /// <summary>Wet air thrown up where a waterfall lands.</summary>
        private void SpawnWaterfallMist(ModConfig config)
        {
            if (_particles == null || !config.ParticleWaterfallMist || _mistFeet.Count == 0)
                return;
            float rate = MistPuffsPerFootPerSecond * _mistFeet.Count
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleWaterfallMistAmount);
            _mistSpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_mistSpawnCarry, 12);
            if (toSpawn <= 0)
                return;
            _mistSpawnCarry -= toSpawn;
            ParticleSystem pool = _particles;
            float sizeScale = Math.Max(0.1f, config.ParticleWaterfallMistSize);
            for (int i = 0; i < toSpawn; i++)
            {
                Vector2 foot = _mistFeet[(int)(pool.RandomUnit() * _mistFeet.Count) % _mistFeet.Count];
                var position = new Vector2(foot.X + pool.RandomBetween(-26f, 26f),
                                           foot.Y + pool.RandomBetween(-8f, 10f));
                // Thrown up and outward off the impact, then dragged to a hang; the drag is what
                // makes it read as air catching spray rather than as bubbles leaving a straw.
                var velocity = new Vector2(pool.RandomBetween(-10f, 10f), pool.RandomBetween(-18f, -5f));
                // Mote, by the user's eye after seeing both: SoftGlow read as glowing puffs
                // where spray should be thin wet air. The Mote's faintness is countered by the
                // size, the alpha and the rate rather than by the cell.
                pool.Spawn(ParticleSystem.AtlasCell.Mote, position, velocity,
                    lifetimeSeconds: pool.RandomBetween(1.4f, 2.6f),
                    sizePixels: pool.RandomBetween(18f, 30f) * sizeScale,
                    tint: MistTint * pool.RandomBetween(0.45f, 0.62f), emissive: false,
                    dragPerSecond: 0.5f,
                    swayPixelsPerSecond: pool.RandomBetween(6f, 14f),
                    swayPerSecond: pool.RandomBetween(0.5f, 1.2f));
            }
        }

        /// <summary>Steam standing over water labelled hot (class 14): the bathhouse pool, the
        /// island's hidden spring, any modded onsen that carries the label.</summary>
        private void SpawnHotSpringSteam(ModConfig config)
        {
            if (_particles == null || !config.ParticleHotSpringSteam || _steamTiles.Count == 0)
                return;
            float rate = SteamWispsPerTilePerSecond * _steamTiles.Count
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleHotSpringSteamAmount);
            _steamSpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_steamSpawnCarry, 8);
            if (toSpawn <= 0)
                return;
            _steamSpawnCarry -= toSpawn;
            ParticleSystem pool = _particles;
            float sizeScale = Math.Max(0.1f, config.ParticleHotSpringSteamSize);
            for (int i = 0; i < toSpawn; i++)
            {
                Vector2 tile = _steamTiles[(int)(pool.RandomUnit() * _steamTiles.Count) % _steamTiles.Count];
                var position = new Vector2(tile.X + pool.RandomBetween(-22f, 22f),
                                           tile.Y + pool.RandomBetween(-16f, 16f));
                // Slower and longer-lived than the mist: steam rises off still water rather than
                // being thrown, so it drifts where mist billows.
                var velocity = new Vector2(pool.RandomBetween(-3f, 3f), pool.RandomBetween(-13f, -6f));
                pool.Spawn(ParticleSystem.AtlasCell.Mote, position, velocity,
                    lifetimeSeconds: pool.RandomBetween(2.2f, 3.8f),
                    sizePixels: pool.RandomBetween(16f, 26f) * sizeScale,
                    tint: SteamTint * pool.RandomBetween(0.25f, 0.38f), emissive: false,
                    dragPerSecond: 0.3f,
                    swayPixelsPerSecond: pool.RandomBetween(10f, 20f),
                    swayPerSecond: pool.RandomBetween(0.4f, 0.9f));
            }
        }

        /// <summary>Sparks popping off lava, labelled (class 11) or the volcano's own.</summary>
        private void SpawnLavaSparks(ModConfig config)
        {
            if (_particles == null || !config.ParticleLavaSparks || _lavaTiles.Count == 0)
                return;
            float rate = LavaSparksPerTilePerSecond * _lavaTiles.Count
                       * Math.Max(0f, config.ParticleDensity) * Math.Max(0f, config.ParticleLavaSparksAmount);
            _lavaSpawnCarry += rate / 60f;
            int toSpawn = Math.Min((int)_lavaSpawnCarry, 8);
            if (toSpawn <= 0)
                return;
            _lavaSpawnCarry -= toSpawn;
            ParticleSystem pool = _particles;
            float sizeScale = Math.Max(0.1f, config.ParticleLavaSparksSize);
            for (int i = 0; i < toSpawn; i++)
            {
                Vector2 tile = _lavaTiles[(int)(pool.RandomUnit() * _lavaTiles.Count) % _lavaTiles.Count];
                var position = new Vector2(tile.X + pool.RandomBetween(-24f, 24f),
                                           tile.Y + pool.RandomBetween(-12f, 12f));
                // A pop, not a drift: fast up, pulled straight back down, dead before it lands.
                var velocity = new Vector2(pool.RandomBetween(-14f, 14f), pool.RandomBetween(-70f, -30f));
                float shade = pool.RandomBetween(0.75f, 1f);
                var tint = new Color(LavaSparkColour.X * shade, LavaSparkColour.Y * shade, LavaSparkColour.Z * shade);
                pool.Spawn(ParticleSystem.AtlasCell.Spark, position, velocity,
                    lifetimeSeconds: pool.RandomBetween(0.7f, 1.3f),
                    sizePixels: pool.RandomBetween(3f, 6f) * sizeScale,
                    tint: tint, emissive: true,
                    fallPixelsPerSecondSquared: 90f,
                    rotationPerSecond: pool.RandomBetween(-2f, 2f));
            }
        }
    }
}
