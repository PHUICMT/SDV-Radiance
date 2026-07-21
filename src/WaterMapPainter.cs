using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    internal static class WaterMapPainter
    {
        internal static readonly Dictionary<string, WaterOverrides> Overrides = new(StringComparer.OrdinalIgnoreCase);
        private static string _savePath = "";
        private static bool ShiftHeld => Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift);
        private static Texture2D? _pixel;
        private static string _status = "";

        public static void Init(string modDir) { _savePath = Path.Combine(modDir, "water-overrides.json"); Load(); }

        public static void Load()
        {
            if (string.IsNullOrEmpty(_savePath)) return;
            try { if (File.Exists(_savePath)) { var dict = JsonSerializer.Deserialize<Dictionary<string, WaterOverrides>>(File.ReadAllText(_savePath)); if (dict != null) { Overrides.Clear(); foreach (var kv in dict) Overrides[kv.Key] = kv.Value; } } }
            catch (Exception ex) { _status = $"Failed to load water overrides: {ex.Message}"; }
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(_savePath)) return;
            try { File.WriteAllText(_savePath, JsonSerializer.Serialize(Overrides, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { _status = $"Failed to save: {ex.Message}"; }
        }

        public static (bool? isWater, bool isOverride) GetOverride(GameLocation loc, int tx, int ty)
        {
            if (loc == null) return (null, false);
            if (Overrides.TryGetValue(loc.NameOrUniqueName, out var ov))
            {
                if (ov.WaterList.Contains((tx, ty))) return (true, true);
                if (ov.DryList.Contains((tx, ty))) return (false, true);
            }
            return (null, false);
        }

        public static void Draw(SpriteBatch sb)
        {
            if (!WaterMaskOverlay.Visible || Game1.currentLocation == null) return;
            _pixel ??= WaterMaskOverlay._pixel ?? MakePixel(sb.GraphicsDevice);
            var loc = Game1.currentLocation;
            if (!Overrides.TryGetValue(loc.NameOrUniqueName, out var ov)) ov = new WaterOverrides();

            int vx = Game1.viewport.X, vy = Game1.viewport.Y;
            int mx = Game1.getMouseX() + vx, my = Game1.getMouseY() + vy;
            int hoverTx = mx / 64, hoverTy = my / 64;
            bool painting = WaterMaskOverlay.Visible && ShiftHeld;

            if (painting && Game1.activeClickableMenu == null)
            {
                bool left = Microsoft.Xna.Framework.Input.Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
                bool right = Microsoft.Xna.Framework.Input.Mouse.GetState().RightButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
                if ((left || right) && hoverTx >= 0 && hoverTy >= 0)
                {
                    if (left) { ov.DryList.Remove((hoverTx, hoverTy)); ov.WaterList.Add((hoverTx, hoverTy)); }
                    else if (right) { ov.WaterList.Remove((hoverTx, hoverTy)); ov.DryList.Add((hoverTx, hoverTy)); }
                    Overrides[loc.NameOrUniqueName] = ov;
                }
            }

            foreach (var (tx, ty) in ov.WaterList) { int sx = tx * 64 - vx, sy = ty * 64 - vy; if (sx > -64 && sx < Game1.viewport.Width + 64 && sy > -64 && sy < Game1.viewport.Height + 64) { sb.Draw(_pixel, new Rectangle(sx, sy, 64, 64), Color.Cyan * 0.35f); sb.Draw(_pixel, new Rectangle(sx, sy, 64, 2), Color.Cyan); sb.Draw(_pixel, new Rectangle(sx, sy, 2, 64), Color.Cyan); } }
            foreach (var (tx, ty) in ov.DryList) { int sx = tx * 64 - vx, sy = ty * 64 - vy; if (sx > -64 && sx < Game1.viewport.Width + 64 && sy > -64 && sy < Game1.viewport.Height + 64) { sb.Draw(_pixel, new Rectangle(sx, sy, 64, 64), Color.Red * 0.2f); for (int d = 0; d < 64; d += 4) { sb.Draw(_pixel, new Rectangle(sx + d, sy + d, 2, 2), Color.Red * 0.5f); sb.Draw(_pixel, new Rectangle(sx + 62 - d, sy + d, 2, 2), Color.Red * 0.5f); } } }

            if (painting) { int csx = hoverTx * 64 - vx, csy = hoverTy * 64 - vy; sb.Draw(_pixel, new Rectangle(csx - 2, csy - 2, 68, 4), Color.Yellow); sb.Draw(_pixel, new Rectangle(csx - 2, csy + 62, 68, 4), Color.Yellow); sb.Draw(_pixel, new Rectangle(csx - 2, csy, 4, 64), Color.Yellow); sb.Draw(_pixel, new Rectangle(csx + 62, csy, 4, 64), Color.Yellow); }

            string modeText = painting ? $"PAINT MODE — LMB=Water | RMB=Dry | ({hoverTx},{hoverTy})" : $"VIEW MODE — Hold Shift to paint | W:{ov.WaterList.Count} D:{ov.DryList.Count}";
            var size = Game1.smallFont.MeasureString(modeText);
            sb.Draw(_pixel, new Rectangle(10, 60, (int)size.X + 16, (int)size.Y + 8), Color.Black * 0.7f);
            sb.DrawString(Game1.smallFont, modeText, new Vector2(18, 64), painting ? Color.Yellow : Color.White);
        }

        private static Texture2D MakePixel(GraphicsDevice device) { var t = new Texture2D(device, 1, 1); t.SetData(new[] { Color.White }); return t; }
    }

    internal sealed class WaterOverrides
    {
        public HashSet<(int x, int y)> WaterList { get; set; } = new();
        public HashSet<(int x, int y)> DryList { get; set; } = new();
        internal int AllOverridesCount => WaterList.Count + DryList.Count;
    }
}