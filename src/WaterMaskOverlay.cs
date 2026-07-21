using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// In-game water mask debug overlay. When active, draws coloured squares over
    /// every tile in the viewport to show what the water detection system sees:
    ///   🔵 Blue      = core water (confidence 90-100)
    ///   🟡 Yellow    = shore ring / dilated zone
    ///   🟠 Orange    = low confidence water (50-70, art-classified)
    ///   🔴 Red       = false positive (art-only, <50)
    ///   ⚫ No colour  = dry land
    /// 
    /// Hovering a tile shows its detection details.
    /// </summary>
    internal static class WaterMaskOverlay
    {
        public static bool Visible;

        internal static Texture2D? _pixel;
        private static string _hoverInfo = "";
        private static Point _lastHover = new(-1, -1);

        /// <summary>Draw the debug overlay after the world is rendered.</summary>
        public static void Draw(SpriteBatch sb)
        {
            if (!Visible || Game1.currentLocation == null)
                return;

            _pixel ??= MakePixel(sb.GraphicsDevice);

            var loc = Game1.currentLocation;
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
            _hoverInfo = "";

            var hf = ShadowRenderer.Height;

            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTx + i;
                    int ty = startTy + j;
                    int sx = tx * 64 - vx;
                    int sy = ty * 64 - vy;

                    // Determine water status using the same multi-source logic
                    (bool isWater, byte confidence, string source) = ClassifyTile(loc, hf, tx, ty);

                    Color tint;
                    if (!isWater)
                    {
                        tint = Color.Transparent;
                    }
                    else if (confidence >= 90)
                    {
                        tint = Color.Blue * 0.35f;   // core water — high confidence
                    }
                    else if (confidence >= 70)
                    {
                        tint = Color.Yellow * 0.35f;  // medium confidence
                    }
                    else if (confidence >= 50)
                    {
                        tint = Color.Orange * 0.35f;  // low confidence (art only)
                    }
                    else
                    {
                        tint = Color.Red * 0.25f;     // very low confidence
                    }

                    if (tint != Color.Transparent)
                    {
                        sb.Draw(_pixel, new Rectangle(sx, sy, 64, 64), tint);
                        // Draw tile border
                        sb.Draw(_pixel, new Rectangle(sx, sy, 64, 1), Color.White * 0.3f);
                        sb.Draw(_pixel, new Rectangle(sx, sy, 1, 64), Color.White * 0.3f);
                        sb.Draw(_pixel, new Rectangle(sx, sy + 63, 64, 1), Color.White * 0.3f);
                        sb.Draw(_pixel, new Rectangle(sx + 63, sy, 1, 64), Color.White * 0.3f);
                    }

                    // Hover info
                    if (tx == hoverTx && ty == hoverTy && isWater)
                    {
                        _hoverInfo = $"Tile ({tx},{ty}): WATER [{source}] confidence={confidence}%";
                    }
                    else if (tx == hoverTx && ty == hoverTy && !isWater)
                    {
                        _hoverInfo = $"Tile ({tx},{ty}): DRY";
                        if (loc.isWaterTile(tx, ty))
                            _hoverInfo += " (isWaterTile=true but overridden)";
                    }
                }
            }

            // Draw legend
            int legX = 12;
            int legY = Game1.viewport.Height - 140;
            DrawLegend(sb, legX, ref legY, Color.Blue * 0.7f, "Core water (waterTiles/HF/isWaterTile)");
            DrawLegend(sb, legX, ref legY, Color.Yellow * 0.7f, "Shore ring (WaterSource, 70%)");
            DrawLegend(sb, legX, ref legY, Color.Orange * 0.7f, "Art-classified (50-70%)");
            DrawLegend(sb, legX, ref legY, Color.Red * 0.6f, "Low confidence (<50%)");

            // Hover info bar
            if (!string.IsNullOrEmpty(_hoverInfo))
            {
                var size = Game1.smallFont.MeasureString(_hoverInfo);
                sb.Draw(_pixel, new Rectangle(10, 10, (int)size.X + 16, (int)size.Y + 8), Color.Black * 0.7f);
                sb.DrawString(Game1.smallFont, _hoverInfo, new Vector2(18, 14), Color.White);
            }
        }

        private static void DrawLegend(SpriteBatch sb, int x, ref int y, Color c, string text)
        {
            sb.Draw(_pixel, new Rectangle(x, y, 18, 14), c);
            sb.DrawString(Game1.smallFont, text, new Vector2(x + 24, y), Color.White * 0.85f);
            y += 18;
        }

        /// <summary>Classify a tile using the same 5-source system as BuildWaterMask.</summary>
        private static (bool water, byte confidence, string source) ClassifyTile(
            GameLocation loc, Integrations.IHeightFrameworkApi? hf, int tx, int ty)
        {
            // Source 1: waterTiles dict
            if (loc.waterTiles != null && loc.waterTiles.ContainsKey(new Point(tx, ty)))
                return (true, 100, "waterTiles dict");

            // Source 2: Height Framework
            if (hf != null)
            {
                try
                {
                    if (hf.IsWaterSurface(loc, tx, ty))
                        return (true, 100, "HF IsWaterSurface");
                }
                catch { hf = null; }
            }

            // Source 3: isWaterTile
            if (loc.isWaterTile(tx, ty))
                return (true, 90, "isWaterTile()");

            // Source 4: WaterSource property
            if (loc.doesTileHaveProperty(tx, ty, "WaterSource", "Back") != null)
                return (true, 80, "WaterSource prop");

            return (false, 0, "");
        }

        private static Texture2D MakePixel(GraphicsDevice device)
        {
            var t = new Texture2D(device, 1, 1);
            t.SetData(new[] { Color.White });
            return t;
        }
    }
}