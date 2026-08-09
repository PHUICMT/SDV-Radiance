using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Eased "weighted follow" camera. The game snaps <see cref="Game1.viewport"/>
    /// to the player every tick; we instead keep a float camera and lerp it toward
    /// that target, writing the rounded result back — so the view eases into motion
    /// rather than stepping rigidly. Runs on UpdateTicked (after the game centred
    /// the viewport), so this only changes what gets drawn, not game logic.
    ///
    /// Safety: while the game controls the camera itself (cutscenes/festivals via
    /// viewportFreeze, events, map screenshots) we stand down and resync, and on a
    /// large jump (warp, waking up) we snap instead of sliding across the map.
    /// </summary>
    internal sealed class CameraSmoother
    {
        private float _smoothedViewportX, _smoothedViewportY;
        private bool _isTracking;

        public void Update(ModConfig config)
        {
            if (config.CameraMode != CameraMode.Smooth || !Context.IsWorldReady)
            {
                _isTracking = false;
                return;
            }

            // Split screen stands the smoothing down. There is ONE of these and there are two
            // cameras, so the eased position it keeps between ticks belongs to whichever screen
            // updated last, and writing it back moves the other player's view as well: both halves
            // drift toward a point between them. Per-screen easing is the real fix and it is not
            // free, so until then two rigid cameras beat two cameras pulling on each other.
            if (Context.IsSplitScreen)
            {
                Resync();
                return;
            }

            // The game owns the camera in these states — don't fight it.
            if (Game1.viewportFreeze || Game1.eventUp || Game1.currentLocation is null
                || (Game1.game1?.takingMapScreenshot ?? false))
            {
                Resync();
                return;
            }

            float tx = Game1.viewport.X;
            float ty = Game1.viewport.Y;

            if (!_isTracking)
            {
                _smoothedViewportX = tx; _smoothedViewportY = ty;
                _isTracking = true;
                return;
            }

            // Snap on big jumps (warp / teleport / wake-up) so we don't ease across the whole map.
            if (Math.Abs(tx - _smoothedViewportX) > Game1.viewport.Width * 0.75f
                || Math.Abs(ty - _smoothedViewportY) > Game1.viewport.Height * 0.75f)
            {
                _smoothedViewportX = tx; _smoothedViewportY = ty;
            }
            else
            {
                float k = MathHelper.Clamp(config.CameraFollowSpeed, 0.05f, 1f);
                _smoothedViewportX += (tx - _smoothedViewportX) * k;
                _smoothedViewportY += (ty - _smoothedViewportY) * k;

                // Deadzone: snap the last couple of pixels so it settles crisply, no crawl.
                if (Math.Abs(tx - _smoothedViewportX) < 2f) _smoothedViewportX = tx;
                if (Math.Abs(ty - _smoothedViewportY) < 2f) _smoothedViewportY = ty;
            }

            Game1.viewport.X = (int)Math.Round(_smoothedViewportX);
            Game1.viewport.Y = (int)Math.Round(_smoothedViewportY);
        }

        private void Resync()
        {
            _smoothedViewportX = Game1.viewport.X;
            _smoothedViewportY = Game1.viewport.Y;
            _isTracking = true;
        }
    }
}
