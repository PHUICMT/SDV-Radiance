using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>
    /// Small centered text-entry dialog with OK and Cancel. Used to name a saved
    /// look. Unlike the vanilla NamingMenu it has an explicit Cancel, and it hands
    /// control back to the caller via the done/cancel callbacks.
    /// </summary>
    internal sealed class TextEntryMenu : IClickableMenu
    {
        private static readonly Rectangle OkSource = new(128, 256, 64, 64);
        private static readonly Rectangle CancelSource = new(192, 256, 64, 64);

        private readonly string _title;
        private readonly Action<string> _onDone;
        private readonly Action _onCancel;
        private readonly TextBox _box;
        private ClickableTextureComponent _okButton = null!;
        private ClickableTextureComponent _cancelButton = null!;
        private bool _closing;

        public TextEntryMenu(string title, string initial, Action<string> onDone, Action onCancel)
            : base(0, 0, 640, 210, showUpperRightCloseButton: false)
        {
            _title = title;
            _onDone = onDone;
            _onCancel = onCancel;

            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _box = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = xPositionOnScreen + 32,
                Y = yPositionOnScreen + 96,
                Width = width - 210,
                Text = initial ?? ""
            };
            Game1.keyboardDispatcher.Subscriber = _box;
            _box.Selected = true;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 162, yPositionOnScreen + 90, 64, 64),
                Game1.mouseCursors, OkSource, 1f);
            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 92, yPositionOnScreen + 90, 64, 64),
                Game1.mouseCursors, CancelSource, 1f);
        }

        private void Done()
        {
            if (_closing) return;
            _closing = true;
            string text = _box.Text;
            Unsubscribe();
            _onDone(text);
        }

        private void Cancel()
        {
            if (_closing) return;
            _closing = true;
            Unsubscribe();
            _onCancel();
        }

        private void Unsubscribe()
        {
            if (Game1.keyboardDispatcher.Subscriber == _box)
                Game1.keyboardDispatcher.Subscriber = null;
            _box.Selected = false;
        }

        /// <summary>
        /// If the menu is closed externally (an event starts, another mod swaps
        /// activeClickableMenu), release the keyboard — otherwise the TextBox keeps
        /// swallowing every keystroke for the rest of the session.
        /// </summary>
        protected override void cleanupBeforeExit()
        {
            Unsubscribe();
            base.cleanupBeforeExit();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_okButton.containsPoint(x, y)) { Game1.playSound("smallSelect"); Done(); return; }
            if (_cancelButton.containsPoint(x, y)) { Game1.playSound("bigDeSelect"); Cancel(); return; }
            _box.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _box;
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Enter) { Done(); return; }
            if (key == Keys.Escape) { Cancel(); return; }
            // Don't call base: it would close the menu on the menu button without our callback.
        }

        public override void performHoverAction(int x, int y)
        {
            _okButton.tryHover(x, y);
            _cancelButton.tryHover(x, y);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, drawShadow: true);

            Utility.drawTextWithShadow(b, _title, Game1.smallFont,
                new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 32), Game1.textColor);

            _box.Draw(b);
            _okButton.draw(b);
            _cancelButton.draw(b);
            drawMouse(b);
        }
    }
}
