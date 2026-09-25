using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using xTile.Display;
using xTile.Layers;
using xTile.Tiles;

namespace SDVRadiance
{
    /// <summary>
    /// radiance_softseams: every place on a map where the tile-line blend reads one thing past a
    /// ground tile's edge and the player sees another there, which is what draws a line along the
    /// tile grid. The blend reads the ground tile next door; what shows is every layer the game
    /// draws at that cell, stacked.
    /// </summary>
    internal static partial class MapTileNeighbours
    {
        /// <summary>How far apart, in brightness out of 255, what the blend reads and what shows may
        /// be before the edge is reported.</summary>
        private const int SeamReportThreshold = 24;

        private sealed class SeamTally
        {
            internal int Layers, Sheets, SheetsHeld, TilesRead, EdgesRead, Hidden, GroundOverlay, UpperPartlyCovered, Animated, Other, BesideSprite;
            internal readonly List<(int error, string text)> Worst = [];

            internal void Note(int error, string text)
            {
                Worst.Add((error, text));
                if (Worst.Count > 64)
                {
                    Worst.Sort((first, second) => second.error.CompareTo(first.error));
                    Worst.RemoveRange(32, Worst.Count - 32);
                }
            }
        }

        /// <summary>Scan one place, or every place with <paramref name="everywhere"/>, and log what
        /// was found.</summary>
        internal static void ReportSeams(IMonitor monitor, bool everywhere)
        {
            var places = new List<GameLocation>();
            if (everywhere)
            {
                foreach (GameLocation location in Game1.locations)
                    if (location?.Map != null)
                        places.Add(location);
            }
            else if (Game1.currentLocation?.Map != null)
                places.Add(Game1.currentLocation);
            int total = 0;
            foreach (GameLocation location in places)
            {
                SeamTally tally;
                try
                {
                    tally = ScanSeams(location);
                }
                catch (Exception ex)
                {
                    monitor.Log($"[softseams] {location.NameOrUniqueName}: could not be read ({ex.GetType().Name}: {ex.Message})", LogLevel.Info);
                    continue;
                }
                int open = tally.GroundOverlay + tally.UpperPartlyCovered + tally.Animated + tally.Other;
                total += open;
                if (open == 0 && tally.Hidden == 0 && tally.BesideSprite == 0 && everywhere)
                    continue;
                monitor.Log($"[softseams] {location.NameOrUniqueName}: {tally.Layers} drawn layers, {tally.SheetsHeld}/{tally.Sheets} sheets read, "
                    + $"{tally.TilesRead} ground tiles, {tally.EdgesRead} ground edges blended; "
                    + $"reads what is not seen: ground overlay {tally.GroundOverlay}, partly covered from above {tally.UpperPartlyCovered}, "
                    + $"animated {tally.Animated}, other {tally.Other}; already held back (covered or water) {tally.Hidden}; "
                    + $"beside flooring or tilled soil {tally.BesideSprite}", LogLevel.Info);
                tally.Worst.Sort((first, second) => second.error.CompareTo(first.error));
                for (int i = 0; i < Math.Min(8, tally.Worst.Count); i++)
                    monitor.Log($"[softseams]     {tally.Worst[i].text}", LogLevel.Info);
            }
            monitor.Log($"[softseams] {places.Count} place(s) read, {total} edge(s) where the blend reads what is not seen.", LogLevel.Info);
            if (!everywhere && Game1.currentLocation?.Map != null)
                monitor.Log($"[softseams] the cover check for one screen of ground tiles and their eight neighbours, as the draw asks it every frame: "
                    + $"{TimeCoverCheckOnScreen(Game1.currentLocation.Map):0.000} ms", LogLevel.Info);
        }

        /// <summary>What <see cref="CoveredFromAbove"/> costs a frame here: every ground tile on
        /// screen, all eight neighbours, averaged over repeated passes once its caches are warm.</summary>
        private static double TimeCoverCheckOnScreen(xTile.Map map)
        {
            Dictionary<TileSheet, Texture2D> textures = SheetTexturesFor(map);
            List<Layer> grounds = MapLayers.RenderedLayers(map, topToBottom: false).FindAll(layer => layer.Visible && IsGround(layer.Id));
            int left = Math.Max(0, Game1.viewport.X / 64), top = Math.Max(0, Game1.viewport.Y / 64);
            int right = left + Game1.viewport.Width / 64 + 1, bottom = top + Game1.viewport.Height / 64 + 1;
            const int passes = 20;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int pass = 0; pass <= passes; pass++)
            {
                if (pass == 1)
                    clock.Restart();
                foreach (Layer ground in grounds)
                    for (int y = top; y <= Math.Min(bottom, ground.LayerHeight - 1); y++)
                        for (int x = left; x <= Math.Min(right, ground.LayerWidth - 1); x++)
                        {
                            if (ground.Tiles[x, y] == null)
                                continue;
                            foreach (Point offset in Offsets)
                            {
                                int besideX = x + offset.X, besideY = y + offset.Y;
                                if (besideX >= 0 && besideY >= 0 && besideX < ground.LayerWidth && besideY < ground.LayerHeight)
                                    CoveredFromAbove(ground, besideX, besideY, offset, textures);
                            }
                        }
            }
            return clock.Elapsed.TotalMilliseconds / passes;
        }

        /// <summary>
        /// Every map tile on screen whose neighbourhood number is kept, worked out again from the
        /// map and compared with the kept one: the proof that keeping them changes no bake.
        /// </summary>
        internal static string CheckKeptNeighbourhoods()
        {
            xTile.Map? map = Game1.currentLocation?.Map;
            if (map == null || _texturesOf == null || Game1.mapDisplayDevice is not XnaDisplayDevice device)
                return "no map is being drawn";
            Dictionary<TileSheet, Texture2D> textures = _texturesOf(device);
            int left = Math.Max(0, Game1.viewport.X / 64 - 1), top = Math.Max(0, Game1.viewport.Y / 64 - 1);
            int right = left + Game1.viewport.Width / 64 + 2, bottom = top + Game1.viewport.Height / 64 + 2;
            int kept = 0, same = 0, stale = 0, notKept = 0;
            string first = "";
            var saved = (_layer, _tile, _tileX, _tileY);
            int savedAlone = Alone, savedWith = WithNeighbours;
            try
            {
                foreach (Layer layer in map.Layers)
                {
                    if (!_neighbourGrids.TryGetValue(layer, out NeighbourGrid? grid) || grid.Generation != _mapAnswersGeneration)
                        continue;
                    for (int y = top; y <= Math.Min(bottom, layer.LayerHeight - 1); y++)
                        for (int x = left; x <= Math.Min(right, layer.LayerWidth - 1); x++)
                        {
                            Tile? tile = layer.Tiles[x, y];
                            if (tile == null)
                                continue;
                            int cell = y * grid.Width + x;
                            if (grid.Numbers[cell] == 0 || grid.Signatures[cell] != SignatureAround(layer, tile, x, y))
                            {
                                notKept++;
                                continue;
                            }
                            kept++;
                            _layer = layer;
                            _tile = tile;
                            _tileX = x;
                            _tileY = y;
                            int fresh = WorkOutNeighbourhood(layer, tile, textures);
                            int held = grid.Numbers[cell] < 0 ? 0 : grid.Numbers[cell];
                            if (fresh == held)
                                same++;
                            else
                            {
                                stale++;
                                if (first.Length == 0)
                                    first = $" first at {layer.Id} {x},{y}: kept {held}, worked out {fresh}";
                            }
                        }
                }
            }
            finally
            {
                (_layer, _tile, _tileX, _tileY) = saved;
                Alone = savedAlone;
                WithNeighbours = savedWith;
            }
            return $"kept neighbourhoods on screen: {kept} checked, {same} the same as worked out now, {stale} stale{first}; "
                + $"{notKept} tile(s) not kept yet or changed since";
        }

        private static Dictionary<TileSheet, Texture2D> SheetTexturesFor(xTile.Map map)
        {
            var textures = new Dictionary<TileSheet, Texture2D>();
            Dictionary<TileSheet, Texture2D>? drawn = _texturesOf != null && Game1.mapDisplayDevice is XnaDisplayDevice device
                ? _texturesOf(device) : null;
            foreach (TileSheet sheet in map.TileSheets)
            {
                if (drawn != null && drawn.TryGetValue(sheet, out Texture2D? known) && !known.IsDisposed)
                {
                    textures[sheet] = known;
                    continue;
                }
                try
                {
                    textures[sheet] = Game1.content.Load<Texture2D>(sheet.ImageSource);
                }
                catch (Exception)
                {
                    // A sheet that cannot be loaded leaves its tiles out of the scan.
                }
            }
            return textures;
        }

        private static SeamTally ScanSeams(GameLocation location)
        {
            var tally = new SeamTally();
            xTile.Map map = location.Map;
            Dictionary<TileSheet, Texture2D> textures = SheetTexturesFor(map);
            List<Layer> drawn = MapLayers.RenderedLayers(map, topToBottom: false);
            Layer? baseGround = map.GetLayer("Back");
            drawn.RemoveAll(layer => !layer.Visible || baseGround == null
                || layer.TileWidth != baseGround.TileWidth || layer.TileHeight != baseGround.TileHeight);
            tally.Layers = drawn.Count;
            tally.Sheets = map.TileSheets.Count;
            foreach (Texture2D texture in textures.Values)
                if (SheetPixels.WholeSheet(texture, "sheet seam scan") != null)
                    tally.SheetsHeld++;
            Point[] sides = [new(0, -1), new(-1, 0), new(1, 0), new(0, 1)];
            foreach (Layer ground in drawn)
            {
                if (!IsGround(ground.Id))
                    continue;
                for (int y = 0; y < ground.LayerHeight; y++)
                {
                    for (int x = 0; x < ground.LayerWidth; x++)
                    {
                        Tile? own = ground.Tiles[x, y];
                        if (own == null || Turned(own) || IsWater(own))
                            continue;
                        Tile ownStill = own is AnimatedTile ownAnimated && ownAnimated.TileFrames.Length > 0 ? ownAnimated.TileFrames[0] : own;
                        if (!textures.TryGetValue(ownStill.TileSheet, out Texture2D? ownSheet))
                            continue;
                        tally.TilesRead++;
                        foreach (Point side in sides)
                        {
                            int besideX = x + side.X, besideY = y + side.Y;
                            if (besideX < 0 || besideY < 0 || besideX >= ground.LayerWidth || besideY >= ground.LayerHeight)
                                continue;
                            // The edge of this tile on that side, under anything drawn over it here,
                            // is not seen and neither is a line along it.
                            if (CoveredFromAbove(ground, x, y, new Point(-side.X, -side.Y), textures))
                                continue;
                            Tile? read = ground.Tiles[besideX, besideY] ?? OnSiblingLayer(ground, besideX, besideY);
                            if (read == null || Turned(read))
                                continue;
                            Tile readStill = read is AnimatedTile readAnimated && readAnimated.TileFrames.Length > 0 ? readAnimated.TileFrames[0] : read;
                            if (!textures.TryGetValue(readStill.TileSheet, out Texture2D? readSheet))
                                continue;
                            (float readBrightness, float readCover) = EdgeBrightness(readSheet, Bounds(readStill), side);
                            (float ownBrightness, float ownCover) = EdgeBrightness(ownSheet, Bounds(ownStill), new Point(-side.X, -side.Y));
                            // The blend only runs where both sides are solid and the colour steps.
                            if (readCover < 0.9f || ownCover < 0.9f || Math.Abs(readBrightness - ownBrightness) < 5f)
                                continue;
                            tally.EdgesRead++;
                            if (IsWater(read) || CoveredFromAbove(ground, besideX, besideY, side, textures))
                            {
                                tally.Hidden++;
                                continue;
                            }
                            if (location.terrainFeatures.TryGetValue(new Vector2(besideX, besideY), out TerrainFeature? feature)
                                && feature is Flooring or HoeDirt)
                                tally.BesideSprite++;
                            float shown = ShownBrightness(drawn, textures, besideX, besideY, side, MapLayers.CompositeRank(read.Layer.Id),
                                out bool groundAbove, out bool upperAbove, out float coveredShare);
                            int error = (int)Math.Abs(shown - readBrightness);
                            int flicker = read is AnimatedTile animated ? AnimationSpread(animated, textures, side, readBrightness) : 0;
                            if (error < SeamReportThreshold && flicker < SeamReportThreshold)
                                continue;
                            string kind;
                            if (error >= SeamReportThreshold && groundAbove)
                            {
                                tally.GroundOverlay++;
                                kind = "ground overlay";
                            }
                            else if (error >= SeamReportThreshold && upperAbove)
                            {
                                tally.UpperPartlyCovered++;
                                kind = "partly covered from above";
                            }
                            else if (flicker >= SeamReportThreshold)
                            {
                                tally.Animated++;
                                kind = "animated";
                                error = Math.Max(error, flicker);
                            }
                            else
                            {
                                tally.Other++;
                                kind = "other";
                            }
                            tally.Note(error, $"{x},{y} {ground.Id} toward {besideX},{besideY}: {kind}, reads {readBrightness:0} sees {shown:0} (own edge {ownBrightness:0}), {coveredShare * 100f:0}% covered");
                        }
                    }
                }
            }
            return tally;
        }

        /// <summary>The brightness out of 255 of a tile's texels along one edge, CoverDepth deep,
        /// the edge that faces back toward <paramref name="offset"/>'s origin, and how solid they
        /// are, 0 to 1.</summary>
        private static (float brightness, float cover) EdgeBrightness(Texture2D sheet, Rectangle source, Point offset)
        {
            Color[]? pixels = SheetPixels.WholeSheet(sheet, "sheet seam scan");
            if (pixels == null || source.Right > sheet.Width || source.Bottom > sheet.Height)
                return (0f, 0f);
            float sum = 0f, alpha = 0f;
            int count = 0;
            ForEdgeTexel(source, offset, (column, row) =>
            {
                Color pixel = pixels[(source.Y + row) * sheet.Width + source.X + column];
                sum += 0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B;
                alpha += pixel.A / 255f;
                count++;
            });
            return count == 0 ? (0f, 0f) : (sum / count, alpha / count);
        }

        private static void ForEdgeTexel(Rectangle source, Point offset, Action<int, int> visit)
        {
            int fromColumn = offset.X < 0 ? source.Width - CoverDepth : 0;
            int toColumn = offset.X > 0 ? CoverDepth : source.Width;
            int fromRow = offset.Y < 0 ? source.Height - CoverDepth : 0;
            int toRow = offset.Y > 0 ? CoverDepth : source.Height;
            for (int row = fromRow; row < toRow; row++)
                for (int column = fromColumn; column < toColumn; column++)
                    visit(column, row);
        }

        /// <summary>What shows along that edge of the cell: every drawn layer's tile there, first
        /// frame, laid over one another bottom to top as premultiplied colour.</summary>
        private static float ShownBrightness(List<Layer> drawn, Dictionary<TileSheet, Texture2D> textures, int x, int y, Point offset,
            int readRank, out bool groundAbove, out bool upperAbove, out float coveredShare)
        {
            groundAbove = upperAbove = false;
            var stack = new float[16 * CoverDepth * 3];
            // How much of the read tile each texel still lets through, after the layers over it.
            var through = new float[16 * CoverDepth];
            Array.Fill(through, 1f);
            foreach (Layer layer in drawn)
            {
                if (x >= layer.LayerWidth || y >= layer.LayerHeight)
                    continue;
                Tile? tile = layer.Tiles[x, y];
                if (tile == null || Turned(tile))
                    continue;
                Tile still = tile is AnimatedTile animated && animated.TileFrames.Length > 0 ? animated.TileFrames[0] : tile;
                if (!textures.TryGetValue(still.TileSheet, out Texture2D? sheet))
                    continue;
                Color[]? pixels = SheetPixels.WholeSheet(sheet, "sheet seam scan");
                Rectangle source = Bounds(still);
                if (pixels == null || source.Right > sheet.Width || source.Bottom > sheet.Height)
                    continue;
                bool above = MapLayers.CompositeRank(layer.Id) > readRank;
                if (above && IsGround(layer.Id))
                    groundAbove = true;
                else if (above)
                    upperAbove = true;
                int index = 0;
                ForEdgeTexel(source, offset, (column, row) =>
                {
                    Color pixel = pixels[(source.Y + row) * sheet.Width + source.X + column];
                    float keep = 1f - pixel.A / 255f;
                    if (above)
                        through[index / 3] *= keep;
                    stack[index] = pixel.R + stack[index] * keep;
                    stack[index + 1] = pixel.G + stack[index + 1] * keep;
                    stack[index + 2] = pixel.B + stack[index + 2] * keep;
                    index += 3;
                });
            }
            float sum = 0f;
            int texels = stack.Length / 3;
            float passed = 0f;
            foreach (float share in through)
                passed += share;
            coveredShare = 1f - passed / through.Length;
            for (int i = 0; i < stack.Length; i += 3)
                sum += 0.2126f * stack[i] + 0.7152f * stack[i + 1] + 0.0722f * stack[i + 2];
            return sum / texels;
        }

        /// <summary>How far the frames of an animated tile stray from its first along that edge, the
        /// one the blend reads, in brightness out of 255.</summary>
        private static int AnimationSpread(AnimatedTile animated, Dictionary<TileSheet, Texture2D> textures, Point offset, float first)
        {
            float widest = 0f;
            foreach (StaticTile frame in animated.TileFrames)
            {
                if (!textures.TryGetValue(frame.TileSheet, out Texture2D? sheet))
                    continue;
                (float brightness, _) = EdgeBrightness(sheet, Bounds(frame), offset);
                widest = Math.Max(widest, Math.Abs(brightness - first));
            }
            return (int)widest;
        }
    }
}
