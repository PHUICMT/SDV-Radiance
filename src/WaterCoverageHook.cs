using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// EXPERIMENT (P0 water-coverage rework, phase 1): isolate the game's water draw into an
    /// off-screen RT so we get the EXACT per-pixel water+foam coverage the game paints this
    /// frame (follows the animated wave; never leaks onto sand by construction).
    ///
    /// Wraps <c>GameLocation.drawWater(SpriteBatch)</c>: prefix redirects the active batch to
    /// <see cref="CovRT"/>, postfix composites it back onto the scene so the picture is
    /// unchanged, and keeps the RT as the coverage source.
    ///
    /// GATED by <see cref="Active"/> (default OFF) — when off the hook is a single early return,
    /// so normal rendering is untouched. Turned on only for diagnosis (radiance_covview /
    /// AgentBridge). When on and <see cref="Overlay"/> is set, the captured coverage is also
    /// tinted over the scene so we can SEE whether it follows the real waterline.
    /// </summary>
    internal static class WaterCoverageHook
    {
        /// <summary>Master gate. Off = hook does nothing (normal draw).</summary>
        internal static bool Active;
        /// <summary>When Active, also tint the captured coverage over the scene (diagnosis).</summary>
        internal static bool Overlay = true;
        /// <summary>Set true to save CovRT to a PNG on the next captured frame (raw coverage, no scene).</summary>
        internal static bool DumpNext;
        /// <summary>Where the dump lands (read it to see the exact captured coverage).</summary>
        internal static readonly string DumpPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "radiance-covrt.png");

        /// <summary>Last captured coverage (screen-sized, alpha>0 = water pixel this frame).</summary>
        internal static RenderTarget2D? CovRT { get; private set; }

        private static GraphicsDevice? _device;
        private static RenderTargetBinding[]? _saved;
        private static bool _inside;

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            var mi = AccessTools.Method(typeof(GameLocation), nameof(GameLocation.drawWater), new[] { typeof(SpriteBatch) });
            if (mi == null) { monitor.Log("WaterCoverageHook: drawWater(SpriteBatch) not found.", LogLevel.Warn); return; }
            harmony.Patch(mi,
                prefix: new HarmonyMethod(typeof(WaterCoverageHook), nameof(Pre)),
                postfix: new HarmonyMethod(typeof(WaterCoverageHook), nameof(Post)));
            monitor.Log("WaterCoverageHook installed (experimental, gated by Active).", LogLevel.Trace);
        }

        // Return true => run the original drawWater (into our RT when we redirected).
        private static bool Pre(GameLocation __instance, SpriteBatch b)
        {
            if (!Active || _inside || !ReferenceEquals(__instance, Game1.currentLocation))
                return true;
            try
            {
                _device = Game1.graphics.GraphicsDevice;
                var pp = _device.PresentationParameters;
                if (CovRT == null || CovRT.Width != pp.BackBufferWidth || CovRT.Height != pp.BackBufferHeight)
                {
                    CovRT?.Dispose();
                    CovRT = new RenderTarget2D(_device, pp.BackBufferWidth, pp.BackBufferHeight,
                        false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                }
                _saved = _device.GetRenderTargets();
                b.End();
                _device.SetRenderTarget(CovRT);
                _device.Clear(Color.Transparent);
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                _inside = true;
            }
            catch { _inside = false; }
            return true;
        }

        private static void Post(SpriteBatch b)
        {
            if (!_inside)
                return;
            _inside = false;
            try
            {
                b.End();
                if (_saved != null && _saved.Length > 0) _device!.SetRenderTargets(_saved);
                else _device!.SetRenderTarget(null);
                // *** RISK: these Begin params must match the game's world batch, or layers
                // drawn after water render wrong. Verify by screenshot; adjust if the scene breaks.
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                b.Draw(CovRT, Vector2.Zero, Color.White);                 // composite water back onto the scene
                if (Overlay)
                    b.Draw(CovRT, Vector2.Zero, new Color(255, 0, 255) * 0.5f);   // magenta tint = coverage shape
                // leave b begun; the game continues drawing later layers with it.

                if (DumpNext && CovRT != null)
                {
                    DumpNext = false;
                    try
                    {
                        using var fs = System.IO.File.Create(DumpPath);
                        CovRT.SaveAsPng(fs, CovRT.Width, CovRT.Height);   // raw captured coverage, isolated
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
