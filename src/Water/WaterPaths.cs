using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.FloorsAndPaths;
using StardewValley.TerrainFeatures;

namespace SDVRadiance
{
    /// <summary>
    /// Paths a player lays that are made to look like water, such as Stream Paths (Nexus 37017).
    ///
    /// <para>To the game they are floor, the same as a stone path: no water tile, nothing to fish,
    /// so this mod gave them no water effect. An old version took the blue art for water by its
    /// colour, and missed the edges in squares that ran over the flower beds beside it. Here the
    /// path's own tile is the water, one tile at a time, and a path counts when its data gives it
    /// the game's wading footstep, which is how a pack says "this is water to walk through". No
    /// pack is named, so any other mod that makes water paths the same way is covered too.</para>
    /// </summary>
    internal static class WaterPaths
    {
        private const string WadingFootstep = "waterSlosh";

        private static readonly ConditionalWeakTable<GameLocation, HashSet<Vector2>> TilesByLocation = [];

        /// <summary>The tiles of this location that hold a water path. Built on first use and kept
        /// until a path is laid or lifted there.</summary>
        internal static HashSet<Vector2> For(GameLocation location)
        {
            if (TilesByLocation.TryGetValue(location, out HashSet<Vector2>? tiles))
                return tiles;
            tiles = [];
            foreach (KeyValuePair<Vector2, TerrainFeature> pair in location.terrainFeatures.Pairs)
                if (IsWaterPath(pair.Value))
                    tiles.Add(pair.Key);
            TilesByLocation.Add(location, tiles);
            return tiles;
        }

        /// <summary>Whether a change to a location's terrain features laid or lifted a water path.
        /// Forgets that location's tiles when it did, so the next mask is built from the new ones.</summary>
        internal static bool Changed(TerrainFeatureListChangedEventArgs change)
        {
            bool touched = false;
            foreach (KeyValuePair<Vector2, TerrainFeature> pair in change.Added)
                touched |= IsWaterPath(pair.Value);
            foreach (KeyValuePair<Vector2, TerrainFeature> pair in change.Removed)
                touched |= IsWaterPath(pair.Value);
            if (touched)
                TilesByLocation.Remove(change.Location);
            return touched;
        }

        private static bool IsWaterPath(TerrainFeature feature) =>
            feature is Flooring flooring && flooring.GetData()?.FootstepSound == WadingFootstep;

        /// <summary>Which of the tile's 16 by 16 art pixels are water, from the path's own picture as
        /// the game draws it on this tile. Null when the tile holds no water path or its art cannot be
        /// read. Main thread only: the first read of a sheet copies it off the card.</summary>
        /// <remarks>The map under a path is grass or dirt, so reading the map's art, as the mask
        /// does for every other tile, found water in only some of a path's tiles and drew the
        /// effect in squares. A pack draws its water with a pale rim and a faint shadow round it;
        /// only the opaque pixels darker than the rim are water, so the effect follows the shape
        /// the pack drew.</remarks>
        internal static bool[]? WaterBits(GameLocation location, int tileX, int tileY)
        {
            if (!location.terrainFeatures.TryGetValue(new Vector2(tileX, tileY), out TerrainFeature? feature)
                || feature is not Flooring flooring || !IsWaterPath(flooring))
                return null;
            FloorPathData? data = flooring.GetData();
            Texture2D? texture = flooring.GetTexture();
            Color[]? sheet = texture == null ? null : SheetPixels(texture);
            if (data == null || sheet == null)
                return null;
            Point corner = flooring.GetTextureCorner();
            byte neighbours = NeighbourMask(flooring);
            int piece = data.ConnectType == FloorPathConnectType.Random && Flooring.drawGuideList != null
                ? Flooring.drawGuideList[flooring.whichView.Value % Flooring.drawGuideList.Count]
                : Flooring.drawGuide.TryGetValue((byte)(neighbours & 0xF), out int guided) ? guided : 0;

            var pixels = new Color[16 * 16];
            Lay(pixels, sheet, texture!.Width, texture.Height,
                new Rectangle(corner.X + piece * 16 % 256, piece / 16 * 16 + corner.Y, 16, 16), 0, 0);

            // The corner pieces the game lays over a tile where the path turns, at the same places
            // and from the same parts of the sheet as Flooring.draw.
            if (data.ConnectType is FloorPathConnectType.CornerDecorated or FloorPathConnectType.Default)
            {
                int size = data.CornerSize;
                int lowerRow = data.ConnectType == FloorPathConnectType.CornerDecorated ? 16 - size : 12;
                if ((neighbours & 9) == 9 && (neighbours & 0x20) == 0)
                    Lay(pixels, sheet, texture.Width, texture.Height, new Rectangle(64 - size + corner.X, 48 - size + corner.Y, size, size), 0, 0);
                if ((neighbours & 3) == 3 && (neighbours & 0x10) == 0)
                    Lay(pixels, sheet, texture.Width, texture.Height, new Rectangle(16 + corner.X, 48 - size + corner.Y, size, size), 16 - size, 0);
                if ((neighbours & 6) == 6 && (neighbours & 0x40) == 0)
                    Lay(pixels, sheet, texture.Width, texture.Height, new Rectangle(16 + corner.X, corner.Y, size, size), 16 - size, lowerRow);
                if ((neighbours & 0xC) == 12 && (neighbours & 0x80) == 0)
                    Lay(pixels, sheet, texture.Width, texture.Height, new Rectangle(64 - size + corner.X, corner.Y, size, size), 0, 16 - size);
            }

            var bits = new bool[16 * 16];
            bool any = false;
            for (int i = 0; i < bits.Length; i++)
            {
                Color pixel = pixels[i];
                float brightness = 0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B;
                bits[i] = pixel.A >= 200 && brightness < PaleRimBrightness;
                any |= bits[i];
            }
            return any ? bits : null;
        }

        /// <summary>Brighter than this, an opaque pixel of a water path is its pale rim, not water.
        /// Stream Paths' rim reads 212 and up, its deepest water 140 and its lightest ripple 184.</summary>
        private const float PaleRimBrightness = 200f;

        private static void Lay(Color[] pixels, Color[] sheet, int sheetWidth, int sheetHeight, Rectangle source, int toX, int toY)
        {
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                {
                    int sheetX = source.X + x, sheetY = source.Y + y, tileX = toX + x, tileY = toY + y;
                    if (sheetX < 0 || sheetY < 0 || sheetX >= sheetWidth || sheetY >= sheetHeight || tileX >= 16 || tileY >= 16)
                        continue;
                    Color laid = sheet[sheetY * sheetWidth + sheetX];
                    if (laid.A > 0)
                        pixels[tileY * 16 + tileX] = laid;
                }
        }

        private static readonly ConditionalWeakTable<Texture2D, Color[]> PixelsBySheet = [];

        private static Color[]? SheetPixels(Texture2D texture)
        {
            if (PixelsBySheet.TryGetValue(texture, out Color[]? pixels))
                return pixels;
            try
            {
                pixels = new Color[texture.Width * texture.Height];
                texture.GetData(pixels);
            }
            catch (Exception)
            {
                return null;
            }
            PixelsBySheet.Add(texture, pixels);
            return pixels;
        }

        private static readonly FieldInfo? NeighbourMaskField =
            typeof(Flooring).GetField("neighborMask", BindingFlags.Instance | BindingFlags.NonPublic);

        private static byte NeighbourMask(Flooring flooring) =>
            NeighbourMaskField?.GetValue(flooring) is byte mask ? mask : (byte)0;
    }
}
