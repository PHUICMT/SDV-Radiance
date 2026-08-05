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

        private readonly string _titleText;
        private readonly Action<string> _onComplete;
        private readonly Action _onCancelled;
        private readonly TextBox _textBox;
        private ClickableTextureComponent _okButton = null!;
        private ClickableTextureComponent _cancelButton = null!;
        private bool _closing;

        public TextEntryMenu(string title, string initial, Action<string> onDone, Action onCancel)
            : base(0, 0, 640, 210, showUpperRightCloseButton: false)
        {
            _titleText = title;
            _onComplete = onDone;
            _onCancelled = onCancel;

            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _textBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = xPositionOnScreen + 32,
                Y = yPositionOnScreen + 96,
                Width = width - 210,
                Text = initial ?? ""
            };
            Game1.keyboardDispatcher.Subscriber = _textBox;
            _textBox.Selected = true;

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
            string text = _textBox.Text;
            Unsubscribe();
            _onComplete(text);
        }

        private void Cancel()
        {
            if (_closing) return;
            _closing = true;
            Unsubscribe();
            _onCancelled();
        }

        private void Unsubscribe()
        {
            if (Game1.keyboardDispatcher.Subscriber == _textBox)
                Game1.keyboardDispatcher.Subscriber = null;
            _textBox.Selected = false;
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
            _textBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _textBox;
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

        public override void draw(SpriteBatch spriteBatch)
        {
            spriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);
            drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, drawShadow: true);

            Utility.drawTextWithShadow(spriteBatch, _titleText, Game1.smallFont,
                new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 32), Game1.textColor);

            _textBox.Draw(spriteBatch);
            _okButton.draw(spriteBatch);
            _cancelButton.draw(spriteBatch);
            drawMouse(spriteBatch);
        }
    }
}
