using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The game's zoom drawn with an area filter (ScreenZoomArea in upscale.fx) instead of the
    /// bilinear read Game1.renderScreenBuffer uses.
    /// </summary>
    /// <remarks>
    /// <para>The world is drawn into Game1's screen buffer at its own scale, four buffer pixels to a
    /// pixel of the art, and renderScreenBuffer then draws that buffer onto the window at the zoom
    /// level with LinearClamp. At 75 per cent that is one bilinear sample among four buffer pixels
    /// per window pixel, landing at a phase set by the scale, so a thin line crawls as the camera
    /// moves; above 100 per cent every edge of the art is smeared a window pixel wide. This was
    /// first taken for a flaw of the smoothed sprites, which it is not: the whole picture goes
    /// through it, the game's own art included.</para>
    /// <para>Measured 23/9 on window captures (the harness screenshot reads the buffer before the
    /// zoom and cannot see this): at 150 per cent the fine detail of a Town frame rose from 2.58 to
    /// 3.23; at 75 per cent it was 4.74 against 4.71, the game's own stretch being close to an area
    /// read at that ratio already.</para>
    /// <para>Only the one path is replaced: a single screen drawn on the buffer, no map
    /// screenshot, the zoom between a third and three. Anything else goes to the game as before,
    /// and so does every frame while the setting is off.</para>
    /// <para>Never on Android. The filter works in buffer pixels, and a phone's graphics chip runs
    /// it at half precision, which cannot hold a half pixel past about 1,024: there the overlap
    /// weights collapse to nothing on every other column or row and the screen is striped with
    /// black lines beyond that point (Kitty2272554, Deaw0057 and MUTGAMING on Nexus, all since
    /// 2.2.0, gone at exactly 100 per cent where the filter already stood aside). Run at half
    /// precision off the device, the same arithmetic blacks out about one column in 1.6 from
    /// pixel 1,027 of a 2,400-pixel screen. Even the texture position handed to the shader is
    /// too coarse there, so it cannot be rewritten to fit; and a phone's pixels are too small for
    /// the crawl it fixes to show.</para>
    /// </remarks>
    internal static class ScreenZoomFilter
    {
        /// <summary>From ModConfig.ZoomAreaFilter every frame.</summary>
        internal static bool Enabled;
        /// <summary>The upscale effect, which carries the ScreenZoomArea technique.</summary>
        internal static Effect? Effect;
        /// <summary>Frames drawn through the area filter, for radiance_report.</summary>
        internal static int FramesFiltered;
        /// <summary>Set when a draw through the filter threw: the game draws the zoom itself until it restarts.</summary>
        private static bool _handedBack;
        private static IMonitor? _monitor;

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            _monitor = monitor;
            if (Constants.TargetPlatform == GamePlatform.Android)
            {
                monitor.Log("Sharp zoom stands aside on Android: a phone's graphics chip lacks the precision it needs, "
                    + "and the zoom stays the game's own.", LogLevel.Trace);
                return;
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Game1), "renderScreenBuffer", [typeof(RenderTarget2D)]),
                    prefix: new HarmonyMethod(typeof(ScreenZoomFilter), nameof(RenderScreenBuffer_Prefix)));
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not take over the game's zoom drawing ({ex.GetType().Name}: {ex.Message}); "
                    + "the zoom stays the game's own bilinear stretch.", LogLevel.Warn);
            }
        }

        private static bool RenderScreenBuffer_Prefix(Game1 __instance, RenderTarget2D target_screen)
        {
            if (!Enabled || _handedBack || Effect == null)
                return true;
            float zoom = Game1.options.zoomLevel;
            if (Math.Abs(zoom - 1f) < 0.001f || zoom < 1f / 3f || zoom > 3f)
                return true;
            if (__instance.takingMapScreenshot || LocalMultiplayer.IsLocalMultiplayer() || target_screen == null
                || target_screen.IsContentLost || !__instance.ShouldDrawOnBuffer())
                return true;
            EffectTechnique? technique = Effect.Techniques["ScreenZoomArea"];
            if (technique == null)
                return true;

            SpriteBatch batch = Game1.spriteBatch;
            GraphicsDevice device = Game1.graphics.GraphicsDevice;
            try
            {
                device.SetRenderTarget(null);
                device.Clear(Game1.bgColor);
                Effect.CurrentTechnique = technique;
                Effect.Parameters["SourceSize"]?.SetValue(new Vector2(target_screen.Width, target_screen.Height));
                Effect.Parameters["SourceTexelsPerPixel"]?.SetValue(1f / zoom);
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.Default,
                    RasterizerState.CullNone, Effect);
                batch.Draw(target_screen, Vector2.Zero, target_screen.Bounds, Color.White, 0f, Vector2.Zero, zoom, SpriteEffects.None, 1f);
                batch.End();
                // The interface layer exactly as the game draws it.
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default,
                    RasterizerState.CullNone);
                batch.Draw(__instance.uiScreen, Vector2.Zero, __instance.uiScreen.Bounds, Color.White, 0f, Vector2.Zero,
                    Game1.options.uiScale, SpriteEffects.None, 1f);
                batch.End();
                FramesFiltered++;
                return false;
            }
            catch (Exception ex)
            {
                // Handed back for good: a draw that throws once will throw every frame.
                try { batch.End(); } catch { }
                _handedBack = true;
                _monitor?.Log($"The area-filtered zoom failed ({ex.GetType().Name}: {ex.Message}); the game draws its own zoom until it restarts.", LogLevel.Warn);
                return true;
            }
        }
    }
}
