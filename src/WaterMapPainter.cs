using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// In-game water map editor. When active (toggle via WaterMaskOverlay.Visible +
    /// hold Shift), left-click marks tiles as WATER, right-click marks as DRY.
    /// Edits are saved per-location to water-overrides.json and loaded on
    /// BuildWaterMask as the highest-priority ground truth.
    /// </summary>
    internal static class WaterMapPainter
    {
        /// <summary>Per-location tile override sets.</summary>
        internal static readonly Dictionary<string, WaterOverrides> Overrides = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Path to the overrides JSON file.</summary>
        private static string _savePath = "";

        /// <summary>Are we in paint mode? (Shift held while overlay is visible).</summary>
        public static bool PaintMode => WaterMaskOverlay.Visible &&
            Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift) ||
            (Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift) && Game1.IsKeyHeld(Microsoft.Xna.Framework.Input.Keys.LeftShift));

        private static Texture2D? _pixel;
        private static string _status = "";

        public static void Init(string modDir)
        {
            _savePath = Path.Combine(modDir, "water-overrides.json");
            Load();
        }

        /// <summary>Load saved overrides from disk.</summary>
        public static void Load()
        {
            if (string.IsNullOrEmpty(_savePath)) return;
            try
            {
                if (File.Exists(_savePath))
                {
                    var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, WaterOverrides>>(
                        File.ReadAllText(_savePath));
                    if (dict != null)
                    {
                        Overrides.Clear();
                        foreach (var kv in dict)
                            Overrides[kv.Key] = kv.Value;
                    }
                }
                _status = $"Loaded {Overrides.Sum(kv => kv.Value.AllOverridesCount)} tile overrides across {Overrides.Count} locations.";
            }
            catch (Exception ex)
            {
                _status = $"Failed to load water overrides: {ex.Message}";
            }
        }

        /// <summary>Save current overrides to disk.</summary>
        public static void Save()
        {
            if (string.IsNullOrEmpty(_savePath)) return;
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(Overrides, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(_savePath, json);
                _status = $"Saved {Overrides.Sum(kv => kv.Value.AllOverridesCount)} tile overrides across {Overrides.Count} locations.";
            }
            catch (Exception ex)
            {
                _status = $"Failed to save: {ex.Message}";
            }
        }

        /// <summary>Get override status for a specific tile in a location.</summary>
        public static (bool? isWater, bool isOverride) GetOverride(GameLocation loc, int tx, int ty)
        {
            if (loc == null) return (null, false);
            string key = loc.NameOrUniqueName;
            if (Overrides.TryGetValue(key, out var ov))
            {
                if (ov.WaterTiles.Contains((tx, ty))) return (true, true);
                if (ov.DryTiles.Contains((tx, ty))) return (false, true);
            }
            return (null, false);
        }

        /// <summary>Draw the painter overlay (extends WaterMaskOverlay with editable tiles).</summary>
        public static void Draw(SpriteBatch sb)
        {
            if (!WaterMaskOverlay.Visible || Game1.currentLocation == null)
                return;

            _pixel ??= WaterMaskOverlay._pixel ?? MakePixel(sb.GraphicsDevice);

            var loc = Game1.currentLocation;
            string locKey = loc.NameOrUniqueName;
            if (!Overrides.TryGetValue(locKey, out var ov))
                ov = new WaterOverrides();

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTx = (int)Math.Floor(vx / 64f);
            int startTy = (int)Math.Floor(vy / 64f);
            int tilesW = Game1.viewport.Width / 64 + 2;
            int tilesH = Game1.viewport.Height / 64 + 2;

            int mx = Game1.getMouseX() + vx;
            int my = Game1.getMouseY() + vy;
            int hoverTx = mx / 64;
            int hoverTy = my / 64;

            bool painting = Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)
                         && Game1.IsKeyHeld(Microsoft.Xna.Framework.Input.Keys.LeftShift);

            // Paint on click
            if (painting && Game1.currentLocation != null && Game1.activeClickableMenu == null)
            {
                bool leftClick = Microsoft.Xna.Framework.Input.Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
                bool rightClick = Microsoft.Xna.Framework.Input.Mouse.GetState().RightButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

                if ((leftClick || rightClick) && hoverTx >= 0 && hoverTy >= 0)
                {
                    if (leftClick)
                    {
                        ov.DryTiles.Remove((hoverTx, hoverTy));
                        if (!ov.WaterTiles.Contains((hoverTx, hoverTy)))
                            ov.WaterTiles.Add((hoverTx, hoverTy));
                    }
                    else if (rightClick)
                    {
                        ov.WaterTiles.Remove((hoverTx, hoverTy));
                        if (!ov.DryTiles.Contains((hoverTx, hoverTy)))
                            ov.DryTiles.Add((hoverTx, hoverTy));
                    }
                    Overrides[locKey] = ov;
                }
            }

            // Draw override markers
            foreach (var (tx, ty) in ov.WaterTiles)
            {
                int sx = tx * 64 - vx;
                int sy = ty * 64 - vy;
                if (sx > -64 && sx < Game1.viewport.Width + 64 && sy > -64 && sy < Game1.viewport.Height + 64)
                {
                    // Bright cyan crosshatch for override-water
                    sb.Draw(_pixel, new Rectangle(sx, sy, 64, 64), Color.Cyan * 0.4f);
                    sb.Draw(_pixel, new Rectangle(sx, sy, 64, 2), Color.Cyan * 0.8f);
                    sb.Draw(_pixel, new Rectangle(sx, sy, 2, 64), Color.Cyan * 0.8f);
                    sb.Draw(_pixel, new Rectangle(sx, sy + 62, 64, 2), Color.Cyan * 0.8f);
                    sb.Draw(_pixel, new Rectangle(sx + 62, sy, 2, 64), Color.Cyan * 0.8f);
                }
            }
            foreach (var (tx, ty) in ov.DryTiles)
            {
                int sx = tx * 64 - vx;
                int sy = ty * 64 - vy;
                if (sx > -64 && sx < Game1.viewport.Width + 64 && sy > -64 && sy < Game1.viewport.Height + 64)
                {
                    // Red X for override-dry
                    sb.Draw(_pixel, new Rectangle(sx, sy, 64, 64), Color.Red * 0.25f);
                    sb.Draw(_pixel, new Rectangle(sx, sy, 64, 2), Color.Red * 0.6f);
                    sb.Draw(_pixel, new Rectangle(sx, sy, 2, 64), Color.Red * 0.6f);
                    // Diagonal cross
                    for (int d = 0; d < 64; d += 4)
                    {
                        sb.Draw(_pixel, new Rectangle(sx + d, sy + d, 2, 2), Color.Red * 0.5f);
                        sb.Draw(_pixel, new Rectangle(sx + 62 - d, sy + d, 2, 2), Color.Red * 0.5f);
                    }
                }
            }

            // Hover cursor
            if (painting)
            {
                int csx = hoverTx * 64 - vx;
                int csy = hoverTy * 64 - vy;
                // Yellow glowing border
                sb.Draw(_pixel, new Rectangle(csx - 2, csy - 2, 68, 4), Color.Yellow * 0.9f);
                sb.Draw(_pixel, new Rectangle(csx - 2, csy + 62, 68, 4), Color.Yellow * 0.9f);
                sb.Draw(_pixel, new Rectangle(csx - 2, csy, 4, 64), Color.Yellow * 0.9f);
                sb.Draw(_pixel, new Rectangle(csx + 62, csy, 4, 64), Color.Yellow * 0.9f);
            }

            // Status bar
            string modeText = painting
                ? $"PAINT MODE — LMB=Water | RMB=Dry | ({hoverTx},{hoverTy})"
                : $"VIEW MODE — Hold Shift to paint | Water:{ov.WaterTiles.Count} Dry:{ov.DryTiles.Count}";
            var size = Game1.smallFont.MeasureString(modeText);
            sb.Draw(_pixel, new Rectangle(10, 60, (int)size.X + 16, (int)size.Y + 8), Color.Black * 0.7f);
            sb.DrawString(Game1.smallFont, modeText, new Vector2(18, 64), painting ? Color.Yellow : Color.White);

            if (!string.IsNullOrEmpty(_status))
            {
                var ss = Game1.smallFont.MeasureString(_status);
                sb.Draw(_pixel, new Rectangle(10, 80 + (int)size.Y + 8, (int)ss.X + 16, (int)ss.Y + 8), Color.Black * 0.7f);
                sb.DrawString(Game1.smallFont, _status, new Vector2(18, 84 + (int)size.Y + 8), Color.Lime);
            }
        }

        /// <summary>Clear overrides for the current location.</summary>
        public static void ClearLocation(GameLocation loc)
        {
            if (loc == null) return;
            Overrides.Remove(loc.NameOrUniqueName);
            _status = $"Cleared overrides for {loc.NameOrUniqueName}";
        }

        private static Texture2D MakePixel(GraphicsDevice device)
        {
            var t = new Texture2D(device, 1, 1);
            t.SetData(new[] { Color.White });
            return t;
        }
    }

    /// <summary>Per-location water override data, serialized to JSON.</summary>
    internal sealed class WaterOverrides
    {
        /// <summary>Tiles that should ALWAYS be treated as water (overrides any auto-detection).</summary>
        public HashSet<(int x, int y)> WaterTiles { get; set; } = new();

        /// <summary>Tiles that should NEVER be treated as water (overrides any auto-detection).</summary>
        public HashSet<(int x, int y)> DryTiles { get; set; } = new();

        /// <summary>Total overrides in this location.</summary>
        internal int AllOverridesCount => WaterTiles.Count + DryTiles.Count;
    }
}