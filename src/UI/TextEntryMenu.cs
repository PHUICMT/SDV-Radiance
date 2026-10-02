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
        private readonly ClickableTextureComponent _okButton = null!;
        private readonly ClickableTextureComponent _cancelButton = null!;
        private bool _closing;

        /// <param name="longText">True for text longer than the box, a share code: the game's box
        /// otherwise drops every character past its width, and a code pasted into it arrived cut
        /// short. The dialog is wider too, so an ordinary code fits whole.</param>
        public TextEntryMenu(string title, string initial, Action<string> onDone, Action onCancel, bool longText = false)
            : base(0, 0, longText ? Math.Min(1000, Game1.uiViewport.Width - 64) : 640, 210, showUpperRightCloseButton: false)
        {
            _titleText = title;
            _onComplete = onDone;
            _onCancelled = onCancel;

            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            Texture2D frame = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
            _textBox = longText ? new LongTextBox(frame, Game1.smallFont, Game1.textColor)
                                : new TextBox(frame, null, Game1.smallFont, Game1.textColor);
            _textBox.X = xPositionOnScreen + 32;
            _textBox.Y = yPositionOnScreen + 96;
            _textBox.Width = width - 210;
            _textBox.limitWidth = !longText;
            _textBox.Text = initial ?? "";
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

        /// <summary>
        /// A box whose text stays inside its frame however long it grows.
        /// </summary>
        /// <remarks>The game's box shows the end of a long text, but it trims that end to the
        /// box's whole width and then draws it from 16 pixels in, so the last characters and the
        /// caret ran over the frame on the right (seen with a share code). This one trims to the
        /// room inside the frame.</remarks>
        private sealed class LongTextBox : TextBox
        {
            private readonly Texture2D _frame;
            private readonly SpriteFont _textFont;
            private readonly Color _textColour;

            public LongTextBox(Texture2D frame, SpriteFont font, Color colour)
                : base(frame, null, font, colour)
            {
                _frame = frame;
                _textFont = font;
                _textColour = colour;
            }

            public override void Draw(SpriteBatch spriteBatch, bool drawShadow = true)
            {
                spriteBatch.Draw(_frame, new Rectangle(X, Y, 16, Height), new Rectangle(0, 0, 16, Height), Color.White);
                spriteBatch.Draw(_frame, new Rectangle(X + 16, Y, Width - 32, Height), new Rectangle(16, 0, 4, Height), Color.White);
                spriteBatch.Draw(_frame, new Rectangle(X + Width - 16, Y, 16, Height),
                    new Rectangle(_frame.Bounds.Width - 16, 0, 16, Height), Color.White);
                string shown = Text;
                int room = Width - 40;
                while (shown.Length > 0 && _textFont.MeasureString(shown).X > room)
                    shown = shown[1..];
                Utility.drawTextWithShadow(spriteBatch, shown, _textFont, new Vector2(X + 16, Y + 12), _textColour);
                bool caretShowing = Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000.0 >= 500.0;
                if (caretShowing && Selected)
                {
                    int caretX = X + 16 + (int)_textFont.MeasureString(shown).X + 2;
                    spriteBatch.Draw(Game1.staminaRect, new Rectangle(caretX, Y + 8, 4, 32), _textColour);
                }
            }
        }
    }
}
