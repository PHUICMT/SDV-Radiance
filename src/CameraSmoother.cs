using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The smooth camera. The game places <see cref="Game1.viewport"/> on the player every tick;
    /// this keeps, per screen, how far the view sits off that place, and writes the game's place
    /// plus that offset back. Runs on UpdateTicked, after the game placed the view, so it only
    /// changes what is drawn and where the cursor points, never game logic.
    ///
    /// <para>The offset is what is smoothed, not the view. While the player moves, the view
    /// trails by a little, in proportion to the speed (<see cref="TrailSeconds"/>); when they stop,
    /// the trail comes home. The offset eases toward that target (a first-order ease, by the
    /// clock), so it eases in when a walk starts and out when it stops, and it can never pass the
    /// middle and come back: no sway. A critically damped spring was tried first and, carrying its
    /// own speed, went past the middle by a few pixels after a stop and swung back. And the offset
    /// is written whole-pixel on top of the game's own place, so the player's sprite moves on
    /// screen only when the offset itself changes a pixel. A view smoothed on its own, rounded
    /// apart from the player, left the two a pixel out of step on alternate frames, a shimmer that
    /// read as the camera wobbling.</para>
    ///
    /// <para>Two versions before this one. A chase that eased the whole view kept gliding after
    /// every stop and swayed; a soft window that only ever moved with the player never swayed but
    /// was the game's locked camera for the whole of a walk, and read as not smooth at all.</para>
    ///
    /// <para>The game works out its next view from its own target (Game1.currentViewportTarget),
    /// not from the viewport written here, so this never feeds back into where the game wants the
    /// camera. Stood down while the game drives the camera (cutscenes and festivals, a frozen view,
    /// map screenshots) and set, not slid, on a change of place, a change of window or zoom, and
    /// any jump of most of a screen.</para>
    /// </summary>
    internal sealed class CameraSmoother
    {
        /// <summary>One screen's camera. Two screens are two cameras: a single eased position
        /// shared between them made both halves drift toward a point between the players.</summary>
        private sealed class ScreenCamera
        {
            public float OffsetX, OffsetY;
            public float GameSpeedX, GameSpeedY;
            public float GameX, GameY;
            public bool Tracking;
            public GameLocation? Place;
            public int Width, Height;
        }

        private readonly Dictionary<int, ScreenCamera> _byScreen = [];

        internal static readonly bool Available = true;

        /// <summary>
        /// The most the view may sit off the player UP or DOWN, in pixels.
        /// </summary>
        /// <remarks>The toolbar moves to the top of the screen the moment the player's feet are more
        /// than 64 pixels below its middle (Toolbar.draw, no margin either way). A camera trailing a
        /// player walking down by more than that put the feet past the line, and walking diagonally
        /// along it flipped the toolbar between the top and the bottom several times a second:
        /// reported on Nexus (potatothecat), and why this camera was switched off for a while.</remarks>
        private const float MostOffVertical = 40f;
        /// <summary>The most the view may sit off the player sideways. Nothing on screen reads the
        /// player's place across, so this only keeps them well inside the screen.</summary>
        private const float MostOffSideways = 96f;
        /// <summary>How far behind the player the view trails at the smoothest setting, as seconds
        /// of the player's own speed. The follow setting scales it down to nothing at 1, which is
        /// the game's own locked camera.</summary>
        private const float TrailSecondsAtSmoothest = 0.2f;
        /// <summary>How long the offset takes to settle on its target, about: the ease's time
        /// constant. Short enough that a stop is over in about a third of a second.</summary>
        private const float SettleSeconds = 0.12f;
        /// <summary>How quickly the measured speed of the game's view follows the real one. The
        /// game's view moves in whole pixels, so its speed read tick by tick jumps about; smoothed
        /// over this long it is the walking speed.</summary>
        private const float SpeedSmoothingSeconds = 0.08f;
        /// <summary>The share of a screen past which a move is a jump (a warp the game did not
        /// announce, waking up, a teleport) and is set rather than slid.</summary>
        private const float JumpShareOfScreen = 0.75f;

        private static float TrailSeconds(ModConfig config)
            => TrailSecondsAtSmoothest * (1f - MathHelper.Clamp(config.CameraFollowSpeed, 0.05f, 1f));

        public void Update(ModConfig config)
        {
            int screenId = Context.ScreenId;
            if (!_byScreen.TryGetValue(screenId, out ScreenCamera? camera))
            {
                camera = new ScreenCamera();
                _byScreen[screenId] = camera;
                LiveScreens.ForgetDeparted(_byScreen);
            }
            if (!Available || config.CameraMode != CameraMode.Smooth || !Context.IsWorldReady)
            {
                camera.Tracking = false;
                return;
            }

            // The game owns the camera in these states: follow it exactly and resume from there.
            if (Game1.viewportFreeze || Game1.eventUp || Game1.currentLocation is null
                || (Game1.game1?.takingMapScreenshot ?? false))
            {
                Resync(camera);
                return;
            }

            float gameX = Game1.viewport.X;
            float gameY = Game1.viewport.Y;
            bool placeChanged = !LiveScreens.SamePlace(camera.Place, Game1.currentLocation);
            bool sizeChanged = camera.Width != Game1.viewport.Width || camera.Height != Game1.viewport.Height;
            bool jumped = Math.Abs(gameX - camera.GameX) > Game1.viewport.Width * JumpShareOfScreen
                || Math.Abs(gameY - camera.GameY) > Game1.viewport.Height * JumpShareOfScreen;
            if (!camera.Tracking || placeChanged || sizeChanged || jumped)
            {
                Resync(camera);
                return;
            }

            float seconds = MathHelper.Clamp((float)(Game1.currentGameTime?.ElapsedGameTime.TotalSeconds ?? 1.0 / 60.0), 1f / 240f, 1f / 20f);
            // The game's view's own speed, smoothed: what the player is doing, in pixels a second.
            float speedShare = 1f - MathF.Exp(-seconds / SpeedSmoothingSeconds);
            camera.GameSpeedX += ((gameX - camera.GameX) / seconds - camera.GameSpeedX) * speedShare;
            camera.GameSpeedY += ((gameY - camera.GameY) / seconds - camera.GameSpeedY) * speedShare;
            camera.GameX = gameX;
            camera.GameY = gameY;

            // Trail the way the player is going, in proportion to the speed; home when they stop.
            float trail = TrailSeconds(config);
            float targetX = MathHelper.Clamp(-camera.GameSpeedX * trail, -MostOffSideways, MostOffSideways);
            float targetY = MathHelper.Clamp(-camera.GameSpeedY * trail, -MostOffVertical, MostOffVertical);
            float settle = 1f - MathF.Exp(-seconds / SettleSeconds);
            camera.OffsetX += (targetX - camera.OffsetX) * settle;
            camera.OffsetY += (targetY - camera.OffsetY) * settle;
            camera.OffsetX = MathHelper.Clamp(camera.OffsetX, -MostOffSideways, MostOffSideways);
            camera.OffsetY = MathHelper.Clamp(camera.OffsetY, -MostOffVertical, MostOffVertical);

            Game1.viewport.X = (int)gameX + (int)MathF.Round(camera.OffsetX);
            Game1.viewport.Y = (int)gameY + (int)MathF.Round(camera.OffsetY);
            LastOffX = Game1.viewport.X - gameX;
            LastOffY = Game1.viewport.Y - gameY;
            MostOffSeenY = Math.Max(MostOffSeenY, Math.Abs(LastOffY));
        }

        /// <summary>For radiance_report: how far the view sits off the game's place right now, and
        /// the most it has been off up or down since the report last asked.</summary>
        internal static float LastOffX, LastOffY, MostOffSeenY;

        internal static string Describe(ModConfig config)
        {
            string line = $"camera: {config.CameraMode}{(Available ? "" : " (stood down)")}, follow {config.CameraFollowSpeed:0.00} "
                + $"(trails {TrailSeconds(config) * 1000f:0} ms of the walk), off the game's place ({LastOffX:0},{LastOffY:0}) px, "
                + $"most up or down since last report {MostOffSeenY:0} px (the toolbar flips past 64)";
            MostOffSeenY = 0f;
            return line;
        }

        private static void Resync(ScreenCamera camera)
        {
            camera.OffsetX = camera.OffsetY = 0f;
            camera.GameSpeedX = camera.GameSpeedY = 0f;
            camera.GameX = Game1.viewport.X;
            camera.GameY = Game1.viewport.Y;
            camera.Place = Game1.currentLocation;
            camera.Width = Game1.viewport.Width;
            camera.Height = Game1.viewport.Height;
            camera.Tracking = true;
            LastOffX = LastOffY = 0f;
        }
    }
}
