using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace SDVRadiance
{
    /// <summary>
    /// The battery saver: half the frames drawn while nothing on screen needs them, so a phone or a
    /// laptop runs cooler and lasts longer. The game still updates sixty times a second, so the
    /// clock, the crops and anything walking about keep their speed; only every other picture is
    /// left out, by asking the game loop to skip a draw (<c>GameRunner.instance.SuppressDraw</c>:
    /// Game1 is one screen's wrapper, not the MonoGame Game), which also skips that frame's present,
    /// so the screen keeps the last picture rather than an empty one.
    /// </summary>
    /// <remarks>
    /// <para>"Nothing needs them" is a menu open, or the player standing still for
    /// <see cref="StillTicksBeforeSaving"/> updates with no button pressed. Anything the player is
    /// watching move is left at full speed: fishing, cutscenes, minigames, a screen fade, and this
    /// mod's own tuner, whose previews are the thing being judged. Moving or pressing anything gives
    /// the next frame back at once.</para>
    /// <para>The skip is counted against frames actually drawn, never against update ticks: a slow
    /// frame runs two updates, and skipping on every odd tick would then skip every draw and freeze
    /// the picture. At most one draw is skipped between two that happen.</para>
    /// </remarks>
    internal static class PowerSaver
    {
        /// <summary>Three seconds of standing still at sixty updates a second.</summary>
        private const int StillTicksBeforeSaving = 180;

        private static int _stillTicks;
        private static Vector2 _lastPosition = new(float.NaN);
        private static int _lastViewportX, _lastViewportY;
        private static bool _drawnSinceLastSkip = true;
        private static bool _skippedSinceLastFrame;

        /// <summary>Whether the last update left a draw out.</summary>
        internal static bool Saving { get; private set; }

        /// <summary>Any button, key or touch: the player is doing something.</summary>
        internal static void NoteInput() => _stillTicks = 0;

        /// <summary>A frame was drawn; the next one may be skipped again.</summary>
        internal static void NoteDrawn() => _drawnSinceLastSkip = true;

        /// <summary>Whether a draw was left out since the frame before this one, cleared on reading.
        /// The frame time across a skipped draw is the saver's doing, not the machine's, so the
        /// frame-time average and everything steering on it leave that frame out.</summary>
        internal static bool TakeSkippedDraw()
        {
            bool skipped = _skippedSinceLastFrame;
            _skippedSinceLastFrame = false;
            return skipped;
        }

        /// <summary>Once per update, on the first screen.</summary>
        internal static void Update(ModConfig config)
        {
            Saving = config.Enabled && config.PowerSaver && NothingNeedsTheFrame();
            if (!Saving || !_drawnSinceLastSkip)
                return;
            GameRunner.instance.SuppressDraw();
            _drawnSinceLastSkip = false;
            _skippedSinceLastFrame = true;
        }

        private static bool NothingNeedsTheFrame()
        {
            // Split screen draws every screen in one frame, and one player standing still is not
            // all of them. An uncapped game counts its updates in frames, so a skipped draw there
            // would run the world faster instead of drawing less.
            if (!Context.IsWorldReady || Context.IsSplitScreen || !(Game1.game1?.IsFixedTimeStep ?? false))
                return Restart();
            if (Game1.eventUp || Game1.currentMinigame != null || Game1.globalFade || Game1.isWarping)
                return Restart();
            IClickableMenu? menu = Game1.activeClickableMenu;
            if (menu is BobberBar or RadianceTunerMenu)
                return Restart();
            if (menu != null)
                return true;

            Farmer player = Game1.player;
            bool moved = player.Position != _lastPosition
                || Game1.viewport.X != _lastViewportX || Game1.viewport.Y != _lastViewportY;
            _lastPosition = player.Position;
            _lastViewportX = Game1.viewport.X;
            _lastViewportY = Game1.viewport.Y;
            if (moved || player.UsingTool || player.isEating)
                return Restart();
            if (_stillTicks < StillTicksBeforeSaving)
                _stillTicks++;
            return _stillTicks >= StillTicksBeforeSaving;
        }

        private static bool Restart()
        {
            _stillTicks = 0;
            return false;
        }
    }
}
