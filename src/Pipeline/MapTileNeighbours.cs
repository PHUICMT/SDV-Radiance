using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using xTile.Display;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace SDVRadiance
{
    /// <summary>
    /// Which tiles surround the map tile the game is drawing right now, so the soft look can bake
    /// a map tile together with its neighbours instead of alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A map is drawn one 16-pixel tile at a time, and the soft look baked each of them from its
    /// own rectangle of the tilesheet with every read clamped to it (see SoftSpriteCache, for the
    /// dark frame that reading the sheet's next tile gave a sprite). For a sprite that is right.
    /// For a map it is not: the tile beside this one in the MAP is what continues the rock, the
    /// bush or the cliff, and a kernel that cannot see it rounds every tile on its own and stops at
    /// its edge. Across a bush built of four tiles that is a square seam on the tile grid, one side
    /// smoothed and the other not - the plates the author kept seeing on rocks and hedges, and
    /// exactly what the pictures of Way Back Pelican Town's hot spring showed on 23 September.
    /// </para>
    /// <para>
    /// So while xTile draws a layer, this remembers the layer and where it is being drawn, and for
    /// each tile works out which eight tiles of the same layer sit round it. The bake draws them
    /// round the tile before the kernel runs, so the kernel and the soften both read the real
    /// continuation of the art across the tile edge, and the gutter the page keeps round the tile
    /// holds the neighbour's pixels rather than the tile's own edge repeated. Two tiles that agree
    /// at their shared edge in the map then agree on the page too.
    /// </para>
    /// <para>
    /// The neighbourhood is part of the cache key, interned to a number, so a tile that appears in
    /// two surroundings is two bakes and a tile in the same surroundings anywhere is one. A tile
    /// that is rotated or flipped (SMAPI's @Rotation and @Flip), or has such a neighbour, is baked
    /// alone as before, since its pixels are not where the sheet has them.
    /// </para>
    /// </remarks>
    internal static partial class MapTileNeighbours
    {
        /// <summary>The eight tiles round one map tile, as the sheet texture and the rectangle each
        /// is drawn from, or no texture where the layer has no tile. Compared by value.</summary>
        internal sealed class Neighbourhood : IEquatable<Neighbourhood>
        {
            internal readonly Texture2D?[] Sheets = new Texture2D?[8];
            internal readonly Rectangle[] Sources = new Rectangle[8];
            /// <summary>Whether the tile is on a ground layer, where a straight cut along its edge
            /// is painted into the map and is blended out (see SheetTileSoften).</summary>
            internal bool OnGround;
            /// <summary>Which of the eight came from the tile's own layer rather than a layer that
            /// carries its art on: an overlay's edge with nothing of its own past it is where the
            /// overlay itself stops.</summary>
            internal readonly bool[] OwnLayer = new bool[8];
            /// <summary>Which of the eight is never seen as its sheet has it: a water tile on the
            /// ground, which the game paints its animated water over (and a bank's overlay over
            /// that). Its art is not what shows past the edge, so the bake repeats the tile's own
            /// edge there instead and the tile-line blend does not reach across.</summary>
            internal readonly bool[] Hidden = new bool[8];
            private int _hash;

            internal void Seal()
            {
                var hash = new HashCode();
                for (int i = 0; i < 8; i++)
                {
                    hash.Add(Sheets[i] == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Sheets[i]!));
                    hash.Add(Sources[i]);
                    hash.Add(OwnLayer[i]);
                    hash.Add(Hidden[i]);
                }
                hash.Add(OnGround);
                _hash = hash.ToHashCode();
            }

            internal Neighbourhood Copy()
            {
                var copy = new Neighbourhood();
                Array.Copy(Sheets, copy.Sheets, 8);
                Array.Copy(Sources, copy.Sources, 8);
                copy.OnGround = OnGround;
                Array.Copy(OwnLayer, copy.OwnLayer, 8);
                Array.Copy(Hidden, copy.Hidden, 8);
                copy._hash = _hash;
                return copy;
            }

            public bool Equals(Neighbourhood? other)
            {
                if (other == null || other._hash != _hash || other.OnGround != OnGround)
                    return false;
                for (int i = 0; i < 8; i++)
                    if (!ReferenceEquals(Sheets[i], other.Sheets[i]) || Sources[i] != other.Sources[i] || OwnLayer[i] != other.OwnLayer[i]
                        || Hidden[i] != other.Hidden[i])
                        return false;
                return true;
            }

            public override bool Equals(object? obj) => Equals(obj as Neighbourhood);
            public override int GetHashCode() => _hash;
        }

        /// <summary>Where each of the eight sits, in tiles, in the order <see cref="Neighbourhood"/>
        /// holds them: the row above left to right, then left and right, then the row below.</summary>
        internal static readonly Point[] Offsets =
        [
            new(-1, -1), new(0, -1), new(1, -1),
            new(-1, 0), new(1, 0),
            new(-1, 1), new(0, 1), new(1, 1),
        ];

        /// <summary>radiance_softneighbours: off bakes every map tile alone again, for A/B.</summary>
        internal static bool Enabled = true;

        /// <summary>Interned neighbourhoods. Past this many the next ones are baked alone rather than
        /// held, so a map edited every frame cannot grow the table without end.</summary>
        private const int MostNeighbourhoods = 60000;

        private static Layer? _layer;
        private static xTile.Dimensions.Rectangle _mapViewport;
        private static xTile.Dimensions.Location _displayOffset;
        private static int _tilePixels;
        private static Tile? _tile;
        private static int _tileX, _tileY;
        private static XnaDisplayDevice? _device;
        private static AccessTools.FieldRef<XnaDisplayDevice, Dictionary<TileSheet, Texture2D>>? _texturesOf;
        private static readonly Neighbourhood _scratch = new();
        private static readonly Dictionary<Neighbourhood, int> _idOf = [];
        private static readonly List<Neighbourhood> _byId = [null!];

        /// <summary>How many tiles were drawn with their neighbours and how many alone since the
        /// last report, for radiance_report.</summary>
        internal static int WithNeighbours, Alone;
        /// <summary>Why the tiles baked alone were, for radiance_report: not the layer's sheet texture,
        /// not the tile's rectangle, turned, a neighbour that could not be read, the table full.</summary>
        private static int _aloneOtherTexture, _aloneOtherRectangle, _aloneTurned, _aloneNeighbour, _aloneFull;
        private static string _lastMismatch = "";
        internal static int Interned => _byId.Count - 1;

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            try
            {
                _texturesOf = AccessTools.FieldRefAccess<XnaDisplayDevice, Dictionary<TileSheet, Texture2D>>("m_tileSheetTextures");
                var layerDraw = AccessTools.Method(typeof(Layer), nameof(Layer.Draw),
                    [typeof(IDisplayDevice), typeof(xTile.Dimensions.Rectangle), typeof(xTile.Dimensions.Location),
                        typeof(bool), typeof(int), typeof(float)]);
                var drawTile = AccessTools.Method(typeof(XnaDisplayDevice), nameof(XnaDisplayDevice.DrawTile));
                if (layerDraw == null || drawTile == null)
                {
                    monitor.Log("xTile's Layer.Draw or DrawTile was not found; the soft look bakes each map tile alone.", LogLevel.Trace);
                    return;
                }
                harmony.Patch(layerDraw,
                    prefix: new HarmonyMethod(typeof(MapTileNeighbours), nameof(LayerDraw_Prefix)),
                    finalizer: new HarmonyMethod(typeof(MapTileNeighbours), nameof(LayerDraw_Finalizer)));
                harmony.Patch(drawTile,
                    prefix: new HarmonyMethod(typeof(MapTileNeighbours), nameof(DrawTile_Prefix)),
                    finalizer: new HarmonyMethod(typeof(MapTileNeighbours), nameof(DrawTile_Finalizer)));
            }
            catch (Exception ex)
            {
                _texturesOf = null;
                monitor.Log($"Could not watch xTile's map drawing ({ex.GetType().Name}: {ex.Message}); the soft look bakes each map tile alone.", LogLevel.Trace);
            }
        }

        private static void LayerDraw_Prefix(Layer __instance, xTile.Dimensions.Rectangle mapViewport,
            xTile.Dimensions.Location displayOffset, bool wrapAround, int pixelZoom, out Layer? __state)
        {
            __state = _layer;
            // A wrapped layer places its tiles by another rule; it is drawn alone as before.
            if (wrapAround || pixelZoom <= 0)
            {
                _layer = null;
                return;
            }
            _layer = __instance;
            _mapViewport = mapViewport;
            _displayOffset = displayOffset;
            _tilePixels = pixelZoom * 16;
        }

        private static Exception? LayerDraw_Finalizer(Exception? __exception, Layer? __state)
        {
            _layer = __state;
            return __exception;
        }

        private static void DrawTile_Prefix(XnaDisplayDevice __instance, Tile tile, xTile.Dimensions.Location location)
        {
            _tile = null;
            Layer? layer = _layer;
            if (layer == null || tile == null || !ReferenceEquals(tile.Layer, layer) || _tilePixels <= 0)
                return;
            // Layer.DrawNormal places tile k at displayOffset + k * tile - mapViewport, so the tile
            // is found from where it is drawn; checked against the layer, never trusted.
            int x = FloorDivide(location.X - _displayOffset.X + _mapViewport.X, _tilePixels);
            int y = FloorDivide(location.Y - _displayOffset.Y + _mapViewport.Y, _tilePixels);
            if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight || !ReferenceEquals(layer.Tiles[x, y], tile))
                return;
            _tile = tile;
            _tileX = x;
            _tileY = y;
            _device = __instance;
            NoteOutcome("drawn, but the smoothing never saw the draw");
        }

        private static Exception? DrawTile_Finalizer(Exception? __exception)
        {
            _tile = null;
            return __exception;
        }

        /// <summary>What became of each map tile drawn while a radiance_drawsat question is open:
        /// drawn by xTile, then what the smoothing did with it. Keyed by layer and cell.</summary>
        private static readonly Dictionary<(string layer, int x, int y), string> _outcomes = [];
        internal static bool Recording;

        /// <summary>The smoothing's verdict on the map tile being drawn right now, if any.</summary>
        internal static void NoteOutcome(string outcome)
        {
            if (!Recording || _tile == null || _layer == null)
                return;
            // The redirected draw goes on through the game's inner Draw overload, whose prefix sees
            // our own page at scale 1 and would write over the verdict it came from.
            var key = (_layer.Id, _tileX, _tileY);
            if (_outcomes.TryGetValue(key, out string? already) && already.StartsWith("SOFT", StringComparison.Ordinal)
                && !outcome.StartsWith("drawn,", StringComparison.Ordinal))
                return;
            _outcomes[key] = outcome;
        }

        internal static string OutcomeAt(string layer, int x, int y)
            => _outcomes.TryGetValue((layer, x, y), out string? outcome) ? outcome : "not drawn through xTile's DrawTile this frame";

        internal static void ClearOutcomes() => _outcomes.Clear();

        /// <summary>
        /// While this mod draws a map tile itself, outside xTile's layer draw, treat the draw as
        /// that tile of that layer: the soft look then bakes it with the same neighbours the map's
        /// own draw of it had, so the copy lands exactly on what is under it. Returns what to hand
        /// to <see cref="EndOwnTileDraw"/>.
        /// </summary>
        /// <remarks>The shadow pass is the case: it paints a prop's base tile again over the
        /// prop's own shadow, and that copy went on raw, a crisp square at the foot of every
        /// fence end, tree and post while the tile under it was smoothed.</remarks>
        internal static (Layer? layer, Tile? tile, int x, int y) BeginOwnTileDraw(Layer layer, int x, int y)
        {
            var saved = (_layer, _tile, _tileX, _tileY);
            Tile? tile = x >= 0 && y >= 0 && x < layer.LayerWidth && y < layer.LayerHeight ? layer.Tiles[x, y] : null;
            if (tile == null || Game1MapDevice() is not XnaDisplayDevice device)
                return saved;
            _layer = layer;
            _tile = tile;
            _tileX = x;
            _tileY = y;
            _device = device;
            return saved;
        }

        internal static void EndOwnTileDraw((Layer? layer, Tile? tile, int x, int y) saved)
        {
            _layer = saved.layer;
            _tile = saved.tile;
            _tileX = saved.x;
            _tileY = saved.y;
        }

        private static IDisplayDevice? Game1MapDevice() => StardewValley.Game1.mapDisplayDevice;

        private static int FloorDivide(int value, int divisor)
            => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

        /// <summary>
        /// The neighbourhood of the map tile being drawn, when this draw is that tile: its number
        /// (1 and up) and the neighbours themselves. Zero when the draw is not a map tile, when it
        /// or a neighbour is turned, or when the table is full; the tile is then baked alone.
        /// </summary>
        internal static int NeighbourhoodOf(Texture2D texture, Rectangle source, out Neighbourhood? neighbourhood)
        {
            neighbourhood = null;
            Tile? tile = _tile;
            XnaDisplayDevice? device = _device;
            Layer? layer = _layer;
            if (!Enabled || tile == null || device == null || layer == null || _texturesOf == null)
                return 0;
            Dictionary<TileSheet, Texture2D> textures = _texturesOf(device);
            if (!textures.TryGetValue(tile.TileSheet, out Texture2D? own) || !ReferenceEquals(own, texture))
            {
                Alone++;
                _aloneOtherTexture++;
                if (Recording)
                    _lastMismatch = $"drawn {texture.Name} {source}, tile {tile.TileSheet?.Id} {(own == null ? "not loaded" : own.Name)}";
                return 0;
            }
            if (source != Bounds(tile))
            {
                Alone++;
                _aloneOtherRectangle++;
                if (Recording)
                    _lastMismatch = $"drawn {texture.Name} {source}, tile bounds {Bounds(tile)}";
                return 0;
            }
            // Working the neighbourhood out is a walk of eight cells, their layers, their
            // properties and their art, and it was done for every map tile on every frame: about
            // 6 ms a frame in Town with 4,000 tiles drawn, measured on 25/9 by switching it off. The
            // answer only changes when the map does, so each cell keeps its number and a signature
            // of the tiles it was worked out from, and is worked out again only when they differ.
            NeighbourGrid grid = _neighbourGrids.GetValue(layer, drawn => new NeighbourGrid(drawn.LayerWidth, drawn.LayerHeight));
            if (grid.Generation != _mapAnswersGeneration)
                grid.Reset(_mapAnswersGeneration);
            int cell = _tileY * grid.Width + _tileX;
            int signature = SignatureAround(layer, tile, _tileX, _tileY);
            if ((uint)cell < (uint)grid.Numbers.Length && grid.Numbers[cell] != 0 && grid.Signatures[cell] == signature)
            {
                int known = grid.Numbers[cell];
                if (known < 0)
                {
                    Alone++;
                    return 0;
                }
                neighbourhood = _byId[known];
                WithNeighbours++;
                return known;
            }
            int number = WorkOutNeighbourhood(layer, tile, textures);
            if ((uint)cell < (uint)grid.Numbers.Length)
            {
                grid.Numbers[cell] = number == 0 ? -1 : number;
                grid.Signatures[cell] = signature;
            }
            if (number == 0)
                return 0;
            neighbourhood = _byId[number];
            return number;
        }

        /// <summary>The tiles a cell's neighbourhood is worked out from, as one number: the tile
        /// itself and the eight round it on its own layer, each by which tile object it is and which
        /// art it shows (an animated tile by the object alone, so the water beside a bank does not
        /// count as a change on every frame of the water).</summary>
        private static int SignatureAround(Layer layer, Tile tile, int tileX, int tileY)
        {
            var hash = new HashCode();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tile));
            for (int i = 0; i < 8; i++)
            {
                int x = tileX + Offsets[i].X, y = tileY + Offsets[i].Y;
                Tile? beside = x >= 0 && y >= 0 && x < layer.LayerWidth && y < layer.LayerHeight ? layer.Tiles[x, y] : null;
                if (beside == null)
                {
                    hash.Add(0);
                    continue;
                }
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(beside));
                // An animated tile by the object alone: its frames do not change, and TileFrames
                // hands back a new copy of them every time it is read.
                if (beside is not AnimatedTile)
                    hash.Add(beside.TileIndex);
            }
            return hash.ToHashCode();
        }

        /// <summary>Each cell's neighbourhood number for one layer, 0 not yet worked out and -1 for
        /// a tile baked alone, with the signature of the tiles it was worked out from.</summary>
        private sealed class NeighbourGrid
        {
            internal readonly int Width;
            internal readonly int[] Numbers;
            internal readonly int[] Signatures;
            internal int Generation;

            internal NeighbourGrid(int width, int height)
            {
                Width = width;
                Numbers = new int[width * height];
                Signatures = new int[width * height];
                Generation = _mapAnswersGeneration;
            }

            internal void Reset(int generation)
            {
                Array.Clear(Numbers);
                Generation = generation;
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Layer, NeighbourGrid> _neighbourGrids = [];

        /// <summary>The neighbourhood of the tile being drawn, worked out from the map: its number,
        /// or 0 when it has to be baked alone.</summary>
        private static int WorkOutNeighbourhood(Layer layer, Tile tile, Dictionary<TileSheet, Texture2D> textures)
        {
            if (Turned(tile))
            {
                Alone++;
                _aloneTurned++;
                return 0;
            }
            bool ground = IsGround(layer.Id);
            for (int i = 0; i < 8; i++)
            {
                int x = _tileX + Offsets[i].X, y = _tileY + Offsets[i].Y;
                Tile? onOwnLayer = x >= 0 && y >= 0 && x < layer.LayerWidth && y < layer.LayerHeight ? layer.Tiles[x, y] : null;
                _scratch.OwnLayer[i] = onOwnLayer != null;
                _scratch.Hidden[i] = false;
                Tile? beside = onOwnLayer ?? (x >= 0 && y >= 0 && x < layer.LayerWidth && y < layer.LayerHeight ? OnSiblingLayer(layer, x, y) : null);
                if (beside == null)
                {
                    _scratch.Sheets[i] = null;
                    _scratch.Sources[i] = Rectangle.Empty;
                    continue;
                }
                if (Turned(beside))
                {
                    Alone++;
                    _aloneTurned++;
                    return 0;
                }
                // An animated neighbour is read at its first frame, so the water beside a bank
                // does not make the bank a new bake on every frame of the water.
                Tile still = beside is AnimatedTile animated && animated.TileFrames.Length > 0 ? animated.TileFrames[0] : beside;
                if (!textures.TryGetValue(still.TileSheet, out Texture2D? sheet) || sheet.IsDisposed)
                {
                    Alone++;
                    _aloneNeighbour++;
                    return 0;
                }
                _scratch.Sheets[i] = sheet;
                _scratch.Sources[i] = Bounds(still);
                // Found on 23/9 by the Forest river: a dirt tile's neighbour on Back was the water
                // tile, grey-blue on the sheet, and the blend pulled that into the dirt as a line
                // down the tile edge, while what shows there is the game's water under a bank.
                // And found on 25/9 beside the cliff in Town: the grass there is painted on Buildings
                // over a darker Back tile, and the blend pulled that hidden Back tile into the plain
                // grass beside it, a dark line along every tile edge of the overlay.
                _scratch.Hidden[i] = ground && (IsWater(beside) || CoveredFromAbove(layer, x, y, Offsets[i], textures));
            }
            _scratch.OnGround = ground;
            _scratch.Seal();
            if (!_idOf.TryGetValue(_scratch, out int id))
            {
                if (_byId.Count > MostNeighbourhoods)
                {
                    Alone++;
                    _aloneFull++;
                    return 0;
                }
                Neighbourhood kept = _scratch.Copy();
                id = _byId.Count;
                _byId.Add(kept);
                _idOf[kept] = id;
            }
            WithNeighbours++;
            return id;
        }

        /// <summary>Whether SMAPI's display device will draw this tile rotated or flipped. Read as
        /// that device reads it, by value: a map loaded from .tmx carries both properties on every
        /// tile, at zero, and asking only whether they exist called every tile of every mod map
        /// turned and baked all of them alone.</summary>
        /// <summary>The tile at this cell on the nearest layer that carries on this one's art, when
        /// this layer has none there.</summary>
        /// <remarks>Maps split one picture across layers all the time: a big bush has its lower half
        /// on Buildings, drawn behind the player, and its upper half on Front, drawn over them. Seen
        /// from either half the other is not a neighbour on the same layer, and the half's edge was
        /// repeated there instead, which left a line across the middle of every such bush - the
        /// author's pictures of 23 September. The ground layers only continue each other; the upper
        /// ones (Buildings, Front, AlwaysFront and their numbered copies) are continued by each other
        /// first and then by the ground under them. The nearest in the map's drawing order is asked
        /// first.</remarks>
        private static Tile? OnSiblingLayer(Layer layer, int x, int y)
        {
            Layer[] siblings = SiblingsOf(layer);
            foreach (Layer sibling in siblings)
            {
                if (x >= sibling.LayerWidth || y >= sibling.LayerHeight)
                    continue;
                Tile? found = sibling.Tiles[x, y];
                if (found != null)
                    return found;
            }
            return null;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Layer, Layer[]> _siblingsOf = [];

        private static Layer[] SiblingsOf(Layer layer)
        {
            if (_siblingsOf.TryGetValue(layer, out Layer[]? known))
                return known;
            var found = new List<(int distance, Layer layer)>();
            if (layer.Map?.Layers is IList<Layer> all)
            {
                int own = all.IndexOf(layer);
                bool ground = IsGround(layer.Id);
                for (int i = 0; i < all.Count; i++)
                {
                    Layer other = all[i];
                    if (i == own || other.TileWidth != layer.TileWidth || other.TileHeight != layer.TileHeight)
                        continue;
                    // The ground only ever continues the ground. An upper layer is continued first
                    // by its own kind and then by the ground under it, which is what shows past its
                    // edge: a bush whose tiles stop at a tile line with grass beyond had its edge
                    // faded into nothing along that line, a ruler-straight cut under every bush.
                    if (ground && !IsGround(other.Id))
                        continue;
                    int distance = Math.Abs(i - own) + (IsGround(other.Id) != ground ? 1000 : 0);
                    found.Add((distance, other));
                }
            }
            found.Sort((first, second) => first.distance.CompareTo(second.distance));
            Layer[] siblings = [.. found.ConvertAll(pair => pair.layer)];
            _siblingsOf.AddOrUpdate(layer, siblings);
            return siblings;
        }

        /// <summary>Whether the game draws its own water over this tile: the Water property, on the
        /// tile or on its index in the sheet, as GameLocation.isWaterTile reads it.</summary>
        private static bool IsWater(Tile tile)
        {
            return tile.Properties.ContainsKey("Water") || tile.TileIndexProperties.ContainsKey("Water");
        }

        private static bool IsGround(string? id) => id != null && id.StartsWith("Back", StringComparison.OrdinalIgnoreCase);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Layer, Layer[]> _layersAboveGround = [];

        /// <summary>Every layer the game draws over this one, in the game's own order
        /// (MapLayers.CompositeRank): a numbered ground layer over Back, as well as Buildings,
        /// Front and AlwaysFront and their numbered copies. Paths and any other layer the game
        /// never draws are left out.</summary>
        /// <remarks>The numbered ground layers were missed at first. Maps paint grass and paths on
        /// Back2 over a Back tile of another colour just as they do on Buildings, and on the
        /// Mountain's Back2 grass the blend drew the same dark line along the tile edge.</remarks>
        private static Layer[] LayersAbove(Layer ground)
        {
            if (_layersAboveGround.TryGetValue(ground, out Layer[]? known))
                return known;
            var found = new List<Layer>();
            int groundRank = MapLayers.CompositeRank(ground.Id);
            if (ground.Map?.Layers is IList<Layer> all)
                foreach (Layer other in all)
                    if (other.Visible && MapLayers.CompositeRank(other.Id) > groundRank
                        && other.TileWidth == ground.TileWidth && other.TileHeight == ground.TileHeight)
                        found.Add(other);
            Layer[] layers = [.. found];
            _layersAboveGround.AddOrUpdate(ground, layers);
            return layers;
        }

        /// <summary>How many source pixels in from the edge an overlay has to be solid to hide the
        /// ground under it from the tile beside: as far as the tile-line blend reads across, two
        /// source pixels at its widest.</summary>
        private const int CoverDepth = 2;

        /// <summary>
        /// Whether the ground at this neighbouring cell is hidden, along the edge it shares with the
        /// tile being baked, under solid tiles on the layers the game draws over it. What shows past
        /// that edge is then the overlay, not the ground tile the bake would read.
        /// </summary>
        /// <remarks>
        /// <para>Only a solid edge counts. A post or a bush base standing on the cell with ground
        /// showing round it leaves that ground in view, and the blend across to it is still right:
        /// that is the painted shadow under a bush the blend was made for.</para>
        /// <para>The layers are laid over one another: an indoor wall is often a Back2, a Back3 and a
        /// Buildings tile at one cell, none of them solid along the edge alone and solid together.</para>
        /// </remarks>
        private static bool CoveredFromAbove(Layer ground, int x, int y, Point offset, Dictionary<TileSheet, Texture2D> textures)
        {
            // Asked for every ground tile on screen and all eight of its neighbours on every frame,
            // about map art that does not change while you stand there: 0.2 to 0.4 ms a frame
            // worked out afresh, so it is worked out once per cell and side and kept.
            CoverGrid grid = _coverGrids.GetValue(ground, layer => new CoverGrid(layer.LayerWidth, layer.LayerHeight));
            if (grid.Generation != _mapAnswersGeneration)
                grid.Reset(_mapAnswersGeneration);
            int slot = (y * grid.Width + x) * 9 + (offset.Y + 1) * 3 + offset.X + 1;
            if ((uint)slot >= (uint)grid.Answers.Length)
                return WorkOutCoveredFromAbove(ground, x, y, offset, textures);
            byte answer = grid.Answers[slot];
            if (answer != 0)
                return answer == 2;
            bool covered = WorkOutCoveredFromAbove(ground, x, y, offset, textures);
            grid.Answers[slot] = covered ? (byte)2 : (byte)1;
            return covered;
        }

        /// <summary>The cover answers for one ground layer, per cell and per side it is seen from:
        /// 0 not yet asked, 1 open, 2 covered.</summary>
        private sealed class CoverGrid
        {
            internal readonly int Width;
            internal readonly byte[] Answers;
            internal int Generation;

            internal CoverGrid(int width, int height)
            {
                Width = width;
                Answers = new byte[width * height * 9];
                Generation = _mapAnswersGeneration;
            }

            internal void Reset(int generation)
            {
                Array.Clear(Answers);
                Generation = generation;
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Layer, CoverGrid> _coverGrids = [];
        private static int _mapAnswersGeneration;

        /// <summary>Forget every answer kept per cell, the neighbourhoods and the cover: the map may
        /// have been edited in place (a day's map changes, a sheet reloaded), which leaves the layer
        /// the same object with other tiles.</summary>
        internal static void ForgetMapAnswers() => _mapAnswersGeneration++;

        private static bool WorkOutCoveredFromAbove(Layer ground, int x, int y, Point offset, Dictionary<TileSheet, Texture2D> textures)
        {
            int count = 0;
            var stack = new HashCode();
            foreach (Layer above in LayersAbove(ground))
            {
                if (x >= above.LayerWidth || y >= above.LayerHeight)
                    continue;
                Tile? overlay = above.Tiles[x, y];
                if (overlay == null || Turned(overlay))
                    continue;
                Tile still = overlay is AnimatedTile animated && animated.TileFrames.Length > 0 ? animated.TileFrames[0] : overlay;
                if (!textures.TryGetValue(still.TileSheet, out Texture2D? sheet) || sheet.IsDisposed)
                    continue;
                Rectangle source = Bounds(still);
                if (EdgeCover(sheet, source, offset, null))
                    return true;
                if (count < _coverStack.Length)
                    _coverStack[count] = (sheet, source);
                count++;
                stack.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sheet));
                stack.Add(source);
            }
            if (count < 2 || count > _coverStack.Length)
                return false;
            var key = (stack.ToHashCode(), count, offset);
            if (_solidStacks.TryGetValue(key, out bool known))
                return known;
            // The edge strip a side neighbour turns toward the tile, or the corner a corner one does.
            float[] through = new float[(offset.X == 0 ? 16 : CoverDepth) * (offset.Y == 0 ? 16 : CoverDepth)];
            Array.Fill(through, 1f);
            for (int i = 0; i < count; i++)
                EdgeCover(_coverStack[i].sheet, _coverStack[i].source, offset, through);
            int solidTexels = 0;
            foreach (float share in through)
                if (share <= 1f - SolidAlpha / 255f)
                    solidTexels++;
            bool solid = solidTexels * 10 >= through.Length * 9;
            if (_solidStacks.Count > 20000)
                _solidStacks.Clear();
            _solidStacks[key] = solid;
            return solid;
        }

        /// <summary>A texel this opaque, out of 255, counts as solid.</summary>
        private const int SolidAlpha = 230;

        private static readonly (Texture2D sheet, Rectangle source)[] _coverStack = new (Texture2D, Rectangle)[8];
        private static readonly Dictionary<(int stack, int count, Point offset), bool> _solidStacks = [];
        private static readonly Dictionary<(Texture2D sheet, Rectangle source, Point offset), bool> _solidEdges = [];

        /// <summary>
        /// Whether the overlay's texels along the edge that faces back toward
        /// <paramref name="offset"/>'s origin are solid, nearly all of them, CoverDepth deep. With
        /// <paramref name="through"/> it instead multiplies into each entry how much of what is
        /// under that texel the overlay lets through, for a stack of overlays laid together.
        /// </summary>
        private static bool EdgeCover(Texture2D sheet, Rectangle source, Point offset, float[]? through)
        {
            var key = (sheet, source, offset);
            if (through == null && _solidEdges.TryGetValue(key, out bool known))
                return known;
            bool solid = false;
            Color[]? pixels = SheetPixels.WholeSheet(sheet, "sheet tile cover");
            if (pixels != null && source.Right <= sheet.Width && source.Bottom <= sheet.Height)
            {
                // The neighbour lies at offset from the baked tile, so the edge it turns toward
                // that tile is on its far side from the offset: its left columns for a neighbour
                // to the right, its top rows for one below, both for a corner.
                int fromColumn = offset.X < 0 ? source.Width - CoverDepth : 0;
                int toColumn = offset.X > 0 ? CoverDepth : source.Width;
                int fromRow = offset.Y < 0 ? source.Height - CoverDepth : 0;
                int toRow = offset.Y > 0 ? CoverDepth : source.Height;
                int total = 0, opaque = 0;
                for (int row = fromRow; row < toRow; row++)
                    for (int column = fromColumn; column < toColumn; column++)
                    {
                        byte alpha = pixels[(source.Y + row) * sheet.Width + source.X + column].A;
                        if (through != null && total < through.Length)
                            through[total] *= 1f - alpha / 255f;
                        total++;
                        if (alpha >= SolidAlpha)
                            opaque++;
                    }
                solid = total > 0 && opaque * 10 >= total * 9;
            }
            if (through != null)
                return solid;
            if (_solidEdges.Count > 20000)
                _solidEdges.Clear();
            _solidEdges[key] = solid;
            return solid;
        }

        private static bool Turned(Tile tile)
        {
            var properties = tile.Properties;
            if (properties.TryGetValue("@Rotation", out var rotation) && WholeNumberOf(rotation) % 360 != 0)
                return true;
            return properties.TryGetValue("@Flip", out var flip) && WholeNumberOf(flip) != 0;
        }

        /// <summary>A tile property read as a whole number, 0 when it is not one. Asked nine times
        /// for every map tile drawn, and PropertyValue's string conversion is a ToString of the
        /// value it holds: for the numbers a .tmx map stores, a new string on every ask.</summary>
        private static int WholeNumberOf(PropertyValue value)
        {
            if (value.Type == typeof(int))
                return value;
            return value.Type == typeof(string) && int.TryParse((string)value, out int parsed) ? parsed : 0;
        }

        private static Rectangle Bounds(Tile tile)
        {
            xTile.Dimensions.Rectangle bounds = tile.TileSheet.GetTileImageBounds(tile.TileIndex);
            return new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        /// <summary>Forget every neighbourhood, with the bakes that were keyed by them.</summary>
        internal static void Clear()
        {
            ForgetMapAnswers();
            _idOf.Clear();
            _byId.RemoveRange(1, _byId.Count - 1);
        }

        /// <summary>The eight neighbours a neighbourhood number stands for: which sheet and rectangle
        /// each was drawn from into the bake. For radiance_softneighbours show.</summary>
        internal static string DescribeNeighbourhood(int id)
        {
            if (id <= 0 || id >= _byId.Count)
                return $"no neighbourhood {id} (there are {_byId.Count - 1})";
            Neighbourhood held = _byId[id];
            var text = new System.Text.StringBuilder($"neighbourhood {id}, {(held.OnGround ? "ground" : "upper layer")}:");
            for (int i = 0; i < 8; i++)
                text.Append(Environment.NewLine + $"    {Offsets[i].X,2},{Offsets[i].Y,2}: "
                    + (held.Sheets[i] == null ? "nothing" : $"{held.Sheets[i]!.Name} {held.Sources[i]}")
                    + (held.OwnLayer[i] ? "" : " (from another layer)"));
            return text.ToString();
        }

        internal static string Describe()
        {
            string line = $"    map tiles baked with their neighbours: {WithNeighbours} draw(s), alone {Alone}, "
                        + $"{Interned} distinct surroundings held ({(Enabled ? "on" : "off, radiance_softneighbours")}); "
                        + $"alone because: another texture {_aloneOtherTexture}, another rectangle {_aloneOtherRectangle}, "
                        + $"turned {_aloneTurned}, unreadable neighbour {_aloneNeighbour}, table full {_aloneFull}"
                        + (_lastMismatch.Length > 0 ? $"; last: {_lastMismatch}" : "");
            WithNeighbours = 0;
            Alone = 0;
            _aloneOtherTexture = _aloneOtherRectangle = _aloneTurned = _aloneNeighbour = _aloneFull = 0;
            return line;
        }
    }
}
