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
        private float _x, _y;
        private bool _tracking;

        public void Update(ModConfig config)
        {
            if (config.CameraMode != CameraMode.Smooth || !Context.IsWorldReady)
            {
                _tracking = false;
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

            if (!_tracking)
            {
                _x = tx; _y = ty;
                _tracking = true;
                return;
            }

            // Snap on big jumps (warp / teleport / wake-up) so we don't ease across the whole map.
            if (Math.Abs(tx - _x) > Game1.viewport.Width * 0.75f
                || Math.Abs(ty - _y) > Game1.viewport.Height * 0.75f)
            {
                _x = tx; _y = ty;
            }
            else
            {
                float k = MathHelper.Clamp(config.CameraFollowSpeed, 0.05f, 1f);
                _x += (tx - _x) * k;
                _y += (ty - _y) * k;

                // Deadzone: snap the last couple of pixels so it settles crisply, no crawl.
                if (Math.Abs(tx - _x) < 2f) _x = tx;
                if (Math.Abs(ty - _y) < 2f) _y = ty;
            }

            Game1.viewport.X = (int)Math.Round(_x);
            Game1.viewport.Y = (int)Math.Round(_y);
        }

        private void Resync()
        {
            _x = Game1.viewport.X;
            _y = Game1.viewport.Y;
            _tracking = true;
        }
    }
}
