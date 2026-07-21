using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    internal sealed class DevMenu : IClickableMenu
    {
        private const int PanelW = 360, BtnH = 36, BtnW = 320, Gap = 6;
        private static readonly Vector2 Centre = new(0.5f, 0.3f);
        private readonly ModConfig _config;
        private readonly Action _onSave;
        private string? _lastAction;
        private readonly List<Action> _toggles = new();

        public DevMenu(ModConfig config, Action onSave) : base(0, 0, PanelW, 600, showUpperRightCloseButton: true)
        { _config = config; _onSave = onSave; }

        public override void draw(SpriteBatch b)
        {
            int vw = Game1.uiViewport.Width, vh = Game1.uiViewport.Height;
            xPositionOnScreen = (int)(vw * Centre.X - PanelW / 2f);
            yPositionOnScreen = (int)(vh * Centre.Y);
            height = 600;
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 12, yPositionOnScreen - 12, PanelW + 24, height + 24, Color.Black * 0.75f);
            int x = xPositionOnScreen + 20, y = yPositionOnScreen + 14;
            b.DrawString(Game1.smallFont, "SDV-Radiance Dev Menu", new Vector2(x, y), Color.Cyan);
            y += 28;
            if (_lastAction != null) { b.DrawString(Game1.smallFont, _lastAction, new Vector2(x, y), Color.Lime); y += 22; }
            y += 4;

            y = DrawBtnList(b, "Time Controls", x, ref y, new[] { "Sunrise (06:00)", "Morning (08:30)", "Noon (12:00)", "Golden Hr (17:30)", "Sunset (19:00)", "Night (22:00)", "Midnight (00:30)" }, new Action[] { () => SetTime(600), () => SetTime(830), () => SetTime(1200), () => SetTime(1730), () => SetTime(1900), () => SetTime(2200), () => SetTime(30) });
            y = DrawBtnList(b, "Weather", x, ref y, new[] { "Sunny", "Rain", "Storm" }, new Action[] { () => SetWeather(Game1.weather_sunny), () => SetWeather(Game1.weather_rain), () => SetWeather(Game1.weather_lightning) });
            y = DrawBtnList(b, "Seasons", x, ref y, new[] { "Spring", "Summer", "Fall", "Winter" }, new Action[] { () => SetSeason("spring"), () => SetSeason("summer"), () => SetSeason("fall"), () => SetSeason("winter") });
            y = DrawBtnList(b, "Water Test Warps", x, ref y, new[] { "Hot Springs", "Beach", "Mountain Lake", "Forest River", "Bathhouse", "Desert Oasis", "Island North", "Cindersap Pond" }, new Action[] { () => Warp("Railroad",31,23), () => Warp("Beach",48,11), () => Warp("Mountain",35,5), () => Warp("Forest",90,95), () => Warp("Railroad",15,22), () => Warp("Desert",5,18), () => Warp("IslandNorth",23,26), () => Warp("Forest",95,45) });
            y = DrawBtnList(b, "Other Warps", x, ref y, new[] { "Farm Cave", "Town Centre", "Mines Entrance", "Saloon" }, new Action[] { () => Warp("FarmCave",4,5), () => Warp("Town",54,68), () => Warp("Mountain",17,30), () => Warp("Saloon",10,18) });
            y += 8;
            DrawToggle(b, x, ref y, "Water", () => _config.WaterEnabled, v => _config.WaterEnabled = v);
            DrawToggle(b, x, ref y, "God Rays", () => _config.GodRaysEnabled, v => _config.GodRaysEnabled = v);
            DrawToggle(b, x, ref y, "Shadows", () => _config.DirectionalShadowsEnabled, v => _config.DirectionalShadowsEnabled = v);
            DrawToggle(b, x, ref y, "Cloud", () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v);
            DrawToggle(b, x, ref y, "Bloom", () => _config.BloomEnabled, v => _config.BloomEnabled = v);
            DrawToggle(b, x, ref y, "Fog", () => _config.FogEnabled, v => _config.FogEnabled = v);
            DrawToggle(b, x, ref y, "Tilt-Shift", () => _config.TiltShiftEnabled, v => _config.TiltShiftEnabled = v);
            DrawToggle(b, x, ref y, "Vignette", () => _config.VignetteEnabled, v => _config.VignetteEnabled = v);
            DrawToggle(b, x, ref y, "CA", () => _config.ChromaticAberrationEnabled, v => _config.ChromaticAberrationEnabled = v);
            y += 10;
            if (DrawBtn(b, x, ref y, "Save to Disk", Color.Gold)) _onSave();
            height = Math.Min(y + 30 - yPositionOnScreen, Game1.uiViewport.Height - 60);
        }

        public override void receiveLeftClick(int mx, int my, bool playSound = true)
        {
            base.receiveLeftClick(mx, my, playSound);
            int cx = (int)(Game1.uiViewport.Width * Centre.X - PanelW / 2f), cy = (int)(Game1.uiViewport.Height * Centre.Y);
            int x = cx + 20, y = cy + 42;
            // Buttons
            foreach (var btn in _buttons) { if (new Rectangle(x, y, BtnW, BtnH).Contains(mx, my)) { btn(); Game1.playSound("select"); return; } y += BtnH + Gap; }
            y += 4;
            // Toggles
            for (int i = 0; i < _toggles.Count; i++) { y += 4; if (new Rectangle(x + 190, y, 126, 26).Contains(mx, my)) { _toggles[i](); Game1.playSound("drumkit6"); return; } y += 28; }
            y += 6;
            // Save
            if (new Rectangle(x, y, BtnW, BtnH).Contains(mx, my)) { _onSave(); _lastAction = "Saved"; Game1.playSound("money"); }
        }

        private readonly List<Action> _buttons = new();
        private int DrawBtnList(SpriteBatch b, string title, int x, ref int y, string[] labels, Action[] actions)
        {
            y += 4; b.DrawString(Game1.smallFont, title, new Vector2(x, y), Color.White); y += 20;
            for (int i = 0; i < labels.Length; i++) { var a = actions[i]; if (DrawBtn(b, x, ref y, labels[i], Color.Silver)) a(); _buttons.Add(a); }
            return y + 4;
        }

        private static bool DrawBtn(SpriteBatch b, int x, ref int y, string label, Color tint)
        {
            var r = new Rectangle(x, y, BtnW, BtnH);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), r.X, r.Y, r.Width, r.Height, r.Contains(Game1.getMouseX(), Game1.getMouseY()) ? Color.Wheat : tint, 3f, false);
            b.DrawString(Game1.smallFont, label, new Vector2(x + 8, y + 8), Color.Black * 0.85f);
            y += BtnH + Gap;
            return r.Contains(Game1.getMouseX(), Game1.getMouseY());
        }

        private void DrawToggle(SpriteBatch b, int x, ref int y, string label, Func<bool> get, Action<bool> set)
        {
            y += 4; b.DrawString(Game1.smallFont, label, new Vector2(x, y + 4), Color.LightGray);
            bool on = get();
            var r = new Rectangle(x + 190, y, 126, 26);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), r.X, r.Y, r.Width, r.Height, on ? Color.Lime : Color.DarkGray, 2f, false);
            b.DrawString(Game1.smallFont, on ? "ON" : "OFF", new Vector2(x + 238, y + 4), on ? Color.Black : Color.White);
            _toggles.Add(() => set(!on));
            y += 28;
        }

        private void SetTime(int t) { Game1.timeOfDay = t; _lastAction = $"Time: {t:D4}"; }
        private void SetWeather(string w) { Game1.weatherForTomorrow = w; Game1.isRaining = w != "sunny"; Game1.isLightning = w == "storm"; _lastAction = $"Weather: {w}"; }
        private void SetSeason(string s) { Game1.currentSeason = s; _lastAction = $"Season: {s}"; }
        private static void Warp(string loc, int tx, int ty) { Game1.warpFarmer(loc, tx, ty, false); }
    }
}