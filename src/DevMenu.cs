using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>
    /// Developer testing menu — accessible via F8 (configurable).
    /// Provides one-click teleports, time/weather/day controls, and
    /// effect toggle shortcuts for quick QA of each feature.
    /// </summary>
    internal sealed class DevMenu : IClickableMenu
    {
        private const int PanelW = 360;
        private const int BtnH = 36;
        private const int BtnW = 320;
        private const int Gap = 6;
        private static readonly Vector2 Centre = new(0.5f, 0.3f);

        private readonly ModConfig _config;
        private readonly Action _onSave;
        private readonly List<Action> _onClose = new();
        private string? _lastAction;

        public DevMenu(ModConfig config, Action onSave)
            : base(0, 0, PanelW, 600, showUpperRightCloseButton: true)
        {
            _config = config;
            _onSave = onSave;
        }

        public override void draw(SpriteBatch b)
        {
            int vw = Game1.uiViewport.Width;
            int vh = Game1.uiViewport.Height;

            xPositionOnScreen = (int)(vw * Centre.X - PanelW / 2f);
            yPositionOnScreen = (int)(vh * Centre.Y);
            height = 600;

            // Semi-transparent backdrop
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 12, yPositionOnScreen - 12,
                PanelW + 24, height + 24, Color.Black * 0.75f);

            int x = xPositionOnScreen + 20;
            int y = yPositionOnScreen + 14;

            // Title
            b.DrawString(Game1.smallFont, "SDV-Radiance Dev Menu", new Vector2(x, y), Color.Cyan);
            y += 28;

            // Last action feedback
            if (_lastAction != null)
            {
                b.DrawString(Game1.smallFont, _lastAction, new Vector2(x, y), Color.Lime);
                y += 22;
            }

            y += 4;

            // Sections
            y = DrawSection(b, "Time Controls", x, ref y, new (string, Action)[]
            {
                ("Sunrise (06:00)", () => SetTime(600)),
                ("Morning   (08:30)", () => SetTime(830)),
                ("Noon      (12:00)", () => SetTime(1200)),
                ("Golden Hr (17:30)", () => SetTime(1730)),
                ("Sunset    (19:00)", () => SetTime(1900)),
                ("Night     (22:00)", () => SetTime(2200)),
                ("Midnight  (00:30)", () => SetTime(30)),
            });

            y = DrawSection(b, "Weather", x, ref y, new (string, Action)[]
            {
                ("Sunny", () => SetWeather(Game1.weather_sunny)),
                ("Rain",  () => SetWeather(Game1.weather_rain)),
                ("Storm", () => SetWeather(Game1.weather_lightning)),
            });

            y = DrawSection(b, "Seasons", x, ref y, new (string, Action)[]
            {
                ("Spring", () => SetSeason("spring")),
                ("Summer", () => SetSeason("summer")),
                ("Fall",   () => SetSeason("fall")),
                ("Winter", () => SetSeason("winter")),
            });

            y = DrawSection(b, "Teleport — Water Tests", x, ref y, new (string, Action)[]
            {
                ("Hot Springs (Railroad)", () => Warp("Railroad", 31, 23)),
                ("Beach (Ocean)", () => Warp("Beach", 48, 11)),
                ("Mountain Lake", () => Warp("Mountain", 35, 5)),
                ("Forest River (South)", () => Warp("Forest", 90, 95)),
                ("Bathhouse (Pool)", () => Warp("Railroad", 15, 22)),
                ("Desert Oasis", () => Warp("Desert", 5, 18)),
                ("Island North (Ocean)", () => Warp("IslandNorth", 23, 26)),
                ("Cindersap Pond", () => Warp("Forest", 95, 45)),
            });

            y = DrawSection(b, "Teleport — Other", x, ref y, new (string, Action)[]
            {
                ("Farm Cave", () => Warp("FarmCave", 4, 5)),
                ("Town Centre", () => Warp("Town", 54, 68)),
                ("Mines Entrance", () => Warp("Mountain", 17, 30)),
                ("Saloon", () => Warp("Saloon", 10, 18)),
            });

            y += 8;

            // Effect toggle bar
            DrawToggleBar(b, x, ref y, "Water", () => _config.WaterEnabled, v => _config.WaterEnabled = v);
            DrawToggleBar(b, x, ref y, "God Rays", () => _config.GodRaysEnabled, v => _config.GodRaysEnabled = v);
            DrawToggleBar(b, x, ref y, "Shadows", () => _config.DirectionalShadowsEnabled, v => _config.DirectionalShadowsEnabled = v);
            DrawToggleBar(b, x, ref y, "Cloud Shadow", () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v);
            DrawToggleBar(b, x, ref y, "Bloom", () => _config.BloomEnabled, v => _config.BloomEnabled = v);
            DrawToggleBar(b, x, ref y, "Fog", () => _config.FogEnabled, v => _config.FogEnabled = v);
            DrawToggleBar(b, x, ref y, "Tilt-Shift", () => _config.TiltShiftEnabled, v => _config.TiltShiftEnabled = v);
            DrawToggleBar(b, x, ref y, "Vignette", () => _config.VignetteEnabled, v => _config.VignetteEnabled = v);
            DrawToggleBar(b, x, ref y, "CA", () => _config.ChromaticAberrationEnabled, v => _config.ChromaticAberrationEnabled = v);

            y += 10;
            if (DrawButton(b, x, ref y, "Save Settings to Disk", Color.Gold))
                _onSave();

            height = Math.Min(y + 30 - yPositionOnScreen, Game1.uiViewport.Height - 60);
        }

        public override void receiveLeftClick(int mouseX, int mouseY, bool playSound = true)
        {
            base.receiveLeftClick(mouseX, mouseY, playSound);

            // Recalculate layout for hit-test
            int vw = Game1.uiViewport.Width;
            int vh = Game1.uiViewport.Height;
            int cx = (int)(vw * Centre.X - PanelW / 2f);
            int cy = (int)(vh * Centre.Y);
            int x = cx + 20;
            int y = cy + 14 + 28; // skip title

            // Scan through sections
            foreach (var section in _sections)
                if (TryHitSection(mouseX, mouseY, x, ref y, section.buttons))
                    return;

            // Toggle bars
            y += 8;
            for (int i = 0; i < _toggles.Count; i++)
            {
                y += 4;
                var rect = new Rectangle(x + 190, y, 126, 26);
                if (rect.Contains(mouseX, mouseY))
                {
                    _toggles[i]();
                    Game1.playSound("drumkit6");
                    _lastAction = $"Toggled effect #{(char)('a' + i)}";
                    return;
                }
                y += 28;
            }

            // Save button
            y += 6;
            if (new Rectangle(x, y, BtnW, BtnH).Contains(mouseX, mouseY))
            {
                _onSave();
                _lastAction = "Settings saved to config.json";
                Game1.playSound("money");
            }
        }

        // -- internal helpers ---------------------------------------------------

        private static int DrawSection(SpriteBatch b, string title, int x, ref int y,
            (string label, Action action)[] buttons)
        {
            y += 4;
            b.DrawString(Game1.smallFont, title, new Vector2(x, y), Color.White);
            y += 20;
            foreach (var (label, action) in buttons)
            {
                if (DrawButton(b, x, ref y, label, Color.Silver))
                    action();
            }
            _sections.Add((buttons, x, y - (buttons.Length * (BtnH + Gap))));
            return y + 4;
        }

        private static bool DrawButton(SpriteBatch b, int x, ref int y, string label, Color tint)
        {
            var rect = new Rectangle(x, y, BtnW, BtnH);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15),
                rect.X, rect.Y, rect.Width, rect.Height,
                rect.Contains(Game1.getMouseX(), Game1.getMouseY()) ? Color.Wheat : tint,
                3f, false);
            b.DrawString(Game1.smallFont, label, new Vector2(x + 8, y + 8), Color.Black * 0.85f);
            y += BtnH + Gap;
            return rect.Contains(Game1.getMouseX(), Game1.getMouseY());
        }

        private static void DrawToggleBar(SpriteBatch b, int x, ref int y, string label,
            Func<bool> getter, Action<bool> setter)
        {
            y += 4;
            b.DrawString(Game1.smallFont, label, new Vector2(x, y + 4), Color.LightGray);

            int bx = x + 190;
            bool on = getter();
            var rect = new Rectangle(bx, y, 126, 26);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(384, 396, 15, 15),
                rect.X, rect.Y, rect.Width, rect.Height,
                on ? Color.Lime : Color.DarkGray, 2f, false);
            b.DrawString(Game1.smallFont, on ? "ON" : "OFF",
                new Vector2(bx + 48, y + 4), on ? Color.Black : Color.White);
            _toggles.Add(() => setter(!on));
            y += 28;
        }

        private static bool TryHitSection(int mx, int my, int x, ref int y,
            (string label, Action action)[] buttons)
        {
            int startY = y;
            y += 24;
            foreach (var (_, action) in buttons)
            {
                if (new Rectangle(x, y, BtnW, BtnH).Contains(mx, my))
                {
                    action();
                    Game1.playSound("select");
                    return true;
                }
                y += BtnH + Gap;
            }
            y += 8;
            return false;
        }

        // Track sections for hit-testing (rebuilt per frame — simple enough)
        private readonly List<((string, Action)[] buttons, int x, int y)> _sections = new();
        private readonly List<Action> _toggles = new();

        // -- dev actions --------------------------------------------------------

        private void SetTime(int time)
        {
            Game1.timeOfDay = time;
            _lastAction = $"Time set to {time:D4}";
        }

        private void SetWeather(int weather)
        {
            Game1.weatherForTomorrow = weather;
            Game1.isRaining = weather != Game1.weather_sunny;
            Game1.isLightning = weather == Game1.weather_lightning;
            _lastAction = $"Weather set to {(weather == Game1.weather_sunny ? "Sunny" : weather == Game1.weather_rain ? "Rain" : "Storm")}";
        }

        private void SetSeason(string season)
        {
            Game1.currentSeason = season;
            _lastAction = $"Season set to {season}";
        }

        private static void Warp(string location, int tileX, int tileY)
        {
            if (Game1.player == null || Game1.currentLocation == null) return;
            Game1.warpFarmer(location, tileX, tileY, false);
        }
    }
}
</content>
<write_to_file>
<content>using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>
    /// Developer testing menu — accessible via F8 (configurable).
    /// Provides one-click teleports, time/weather/day controls, and
    /// effect toggle shortcuts for quick QA of each feature.
    /// </summary>
    internal sealed class DevMenu : IClickableMenu
    {
        private const int PanelW = 360;
        private const int BtnH = 36;
        private const int BtnW = 320;
        private const int Gap = 6;
        private static readonly Vector2 Centre = new(0.5f, 0.3f);

        private readonly ModConfig _config;
        private readonly Action _onSave;
        private string? _lastAction;

        public DevMenu(ModConfig config, Action onSave)
            : base(0, 0, PanelW, 600, showUpperRightCloseButton: true)
        {
            _config = config;
            _onSave = onSave;
        }

        public override void draw(SpriteBatch b)
        {
            int vw = Game1.uiViewport.Width;
            int vh = Game1.uiViewport.Height;

            xPositionOnScreen = (int)(vw * Centre.X - PanelW / 2f);
            yPositionOnScreen = (int)(vh * Centre.Y);
            height = 600;

            // Semi-transparent backdrop
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 12, yPositionOnScreen - 12,
                PanelW + 24, height + 24, Color.Black * 0.75f);

            int x = xPositionOnScreen + 20;
            int y = yPositionOnScreen + 14;

            // Title
            b.DrawString(Game1.smallFont, "SDV-Radiance Dev Menu", new Vector2(x, y), Color.Cyan);
            y += 28;

            // Last action feedback
            if (_lastAction != null)
            {
                b.DrawString(Game1.smallFont, _lastAction, new Vector2(x, y), Color.Lime);
                y += 22;
            }

            y += 4;

            // Sections
            y = DrawSection(b, "Time Controls", x, ref y, new (string, Action)[]
            {
                ("Sunrise (06:00)", () => SetTime(600)),
                ("Morning   (08:30)", () => SetTime(830)),
                ("Noon      (12:00)", () => SetTime(1200)),
                ("Golden Hr (17:30)", () => SetTime(1730)),
                ("Sunset    (19:00)", () => SetTime(1900)),
                ("Night     (22:00)", () => SetTime(2200)),
                ("Midnight  (00:30)", () => SetTime(30)),
            });

            y = DrawSection(b, "Weather", x, ref y, new (string, Action)[]
            {
                ("Sunny", () => SetWeather(Game1.weather_sunny)),
                ("Rain",  () => SetWeather(Game1.weather_rain)),
                ("Storm", () => SetWeather(Game1.weather_lightning)),
            });

            y = DrawSection(b, "Seasons", x, ref y, new (string, Action)[]
            {
                ("Spring", () => SetSeason("spring")),
                ("Summer", () => SetSeason("summer")),
                ("Fall",   () => SetSeason("fall")),
                ("Winter", () => SetSeason("winter")),
            });

            y = DrawSection(b, "Teleport — Water Tests", x, ref y, new (string, Action)[]
            {
                ("Hot Springs (Railroad)", () => Warp("Railroad", 31, 23)),
                ("Beach (Ocean)", () => Warp("Beach", 48, 11)),
                ("Mountain Lake", () => Warp("Mountain", 35, 5)),
                ("Forest River (South)", () => Warp("Forest", 90, 95)),
                ("Bathhouse (Pool)", () => Warp("Railroad", 15, 22)),
                ("Desert Oasis", () => Warp("Desert", 5, 18)),
                ("Island North (Ocean)", () => Warp("IslandNorth", 23, 26)),
                ("Cindersap Pond", () => Warp("Forest", 95, 45)),
            });

            y = DrawSection(b, "Teleport — Other", x, ref y, new (string, Action)[]
            {
                ("Farm Cave", () => Warp("FarmCave", 4, 5)),
                ("Town Centre", () => Warp("Town", 54, 68)),
                ("Mines Entrance", () => Warp("Mountain", 17, 30)),
                ("Saloon", () => Warp("Saloon", 10, 18)),
            });

            y += 8;

            // Effect toggle bar
            DrawToggleBar(b, x, ref y, "Water", () => _config.WaterEnabled, v => _config.WaterEnabled = v);
            DrawToggleBar(b, x, ref y, "God Rays", () => _config.GodRaysEnabled, v => _config.GodRaysEnabled = v);
            DrawToggleBar(b, x, ref y, "Shadows", () => _config.DirectionalShadowsEnabled, v => _config.DirectionalShadowsEnabled = v);
            DrawToggleBar(b, x, ref y, "Cloud Shadow", () => _config.CloudShadowEnabled, v => _config.CloudShadowEnabled = v);
            DrawToggleBar(b, x, ref y, "Bloom", () => _config.BloomEnabled, v => _config.BloomEnabled = v);
            DrawToggleBar(b, x, ref y, "Fog", () => _config.FogEnabled, v => _config.FogEnabled = v);
            DrawToggleBar(b, x, ref y, "Tilt-Shift", () => _config.TiltShiftEnabled, v => _config.TiltShiftEnabled = v);
            DrawToggleBar(b, x, ref y, "Vignette", () => _config.VignetteEnabled, v => _config.VignetteEnabled = v);
            DrawToggleBar(b, x, ref y, "CA", () => _config.ChromaticAberrationEnabled, v => _config.ChromaticAberrationEnabled = v);

            y += 10;
            if (DrawButton(b, x, ref y, "Save Settings to Disk", Color.Gold))
                _onSave();

            height = Math.Min(y + 30 - yPositionOnScreen, Game1.uiViewport.Height - 60);
        }

        public override void receiveLeftClick(int mouseX, int mouseY, bool playSound = true)
        {
            base.receiveLeftClick(mouseX, mouseY, playSound);

            // Recalculate layout for hit-test
            int vw = Game1.uiViewport.Width;
            int vh = Game1.uiViewport.Height;
            int cx = (int)(vw * Centre.X - PanelW / 2f);
            int cy = (int)(vh * Centre.Y);
            int x = cx + 20;
            int y = cy + 14 + 28; // skip title

            // Scan through sections
            foreach (var section in _sections)
                if (TryHitSection(mouseX, mouseY, x, ref y, section.buttons))
                    return;

            // Toggle bars
            y += 8;
            for (int i = 0; i < _toggles.Count; i++)
            {
                y += 4;
                var rect = new Rectangle(x + 190, y, 126, 26);
                if (rect.Contains(mouseX, mouseY))
                {
                    _toggles[i]();
                    Game1.playSound("drumkit6");
                    _lastAction = $"Toggled effect #{(char)('a' + i)}";
                    return;
                }
                y += 28;
            }

            // Save button
            y += 6;
            if (new Rectangle(x, y, BtnW, BtnH).Contains(mouseX, mouseY))
            {
                _onSave();
                _lastAction = "Settings saved to config.json";
                Game1.playSound("money");
            }
        }

        // -- internal helpers ---------------------------------------------------

        private static int DrawSection(SpriteBatch b, string title, int x, ref int y,
            (string label, Action action)[] buttons)
        {
            y += 4;
            b.DrawString(Game1.smallFont, title, new Vector2(x, y), Color.White);
            y += 20;
            foreach (var (label, action) in buttons)
            {
                if (DrawButton(b, x, ref y, label, Color.Silver))
                    action();
            }
            _sections.Add((buttons, x, y - (buttons.Length * (BtnH + Gap))));
            return y + 4;
        }

        private static bool DrawButton(SpriteBatch b, int x, ref int y, string label, Color tint)
        {
            var rect = new Rectangle(x, y, BtnW, BtnH);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15),
                rect.X, rect.Y, rect.Width, rect.Height,
                rect.Contains(Game1.getMouseX(), Game1.getMouseY()) ? Color.Wheat : tint,
                3f, false);
            b.DrawString(Game1.smallFont, label, new Vector2(x + 8, y + 8), Color.Black * 0.85f);
            y += BtnH + Gap;
            return rect.Contains(Game1.getMouseX(), Game1.getMouseY());
        }

        private static void DrawToggleBar(SpriteBatch b, int x, ref int y, string label,
            Func<bool> getter, Action<bool> setter)
        {
            y += 4;
            b.DrawString(Game1.smallFont, label, new Vector2(x, y + 4), Color.LightGray);

            int bx = x + 190;
            bool on = getter();
            var rect = new Rectangle(bx, y, 126, 26);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(384, 396, 15, 15),
                rect.X, rect.Y, rect.Width, rect.Height,
                on ? Color.Lime : Color.DarkGray, 2f, false);
            b.DrawString(Game1.smallFont, on ? "ON" : "OFF",
                new Vector2(bx + 48, y + 4), on ? Color.Black : Color.White);
            _toggles.Add(() => setter(!on));
            y += 28;
        }

        private static bool TryHitSection(int mx, int my, int x, ref int y,
            (string label, Action action)[] buttons)
        {
            int startY = y;
            y += 24;
            foreach (var (_, action) in buttons)
            {
                if (new Rectangle(x, y, BtnW, BtnH).Contains(mx, my))
                {
                    action();
                    Game1.playSound("select");
                    return true;
                }
                y += BtnH + Gap;
            }
            y += 8;
            return false;
        }

        // Track sections for hit-testing (rebuilt per frame — simple enough)
        private readonly List<((string, Action)[] buttons, int x, int y)> _sections = new();
        private readonly List<Action> _toggles = new();

        // -- dev actions --------------------------------------------------------

        private void SetTime(int time)
        {
            Game1.timeOfDay = time;
            _lastAction = $"Time set to {time:D4}";
        }

        private void SetWeather(int weather)
        {
            Game1.weatherForTomorrow = weather;
            Game1.isRaining = weather != Game1.weather_sunny;
            Game1.isLightning = weather == Game1.weather_lightning;
            _lastAction = $"Weather set to {(weather == Game1.weather_sunny ? "Sunny" : weather == Game1.weather_rain ? "Rain" : "Storm")}";
        }

        private void SetSeason(string season)
        {
            Game1.currentSeason = season;
            _lastAction = $"Season set to {season}";
        }

        private static void Warp(string location, int tileX, int tileY)
        {
            if (Game1.player == null || Game1.currentLocation == null) return;
            Game1.warpFarmer(location, tileX, tileY, false);
        }
    }
}