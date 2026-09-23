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
    internal static class MapTileNeighbours
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
            if (Turned(tile))
            {
                Alone++;
                _aloneTurned++;
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
                _scratch.Hidden[i] = ground && IsWater(beside);
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
            neighbourhood = _byId[id];
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
