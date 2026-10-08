using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The mod's effects inside the game's whole-map screenshot, seamless from chunk to chunk.
    /// </summary>
    /// <remarks>
    /// <para>The game takes a map screenshot by drawing the location one 2048-pixel chunk at a time
    /// at zoom 1 and stitching the chunks into one picture (Game1.takeMapScreenshot), all inside
    /// one update. Until now the mod stood aside for it, because everything it draws is built
    /// around one camera: run as it is, every chunk would light, blur and reflect only what lies
    /// inside its own square, and the picture would show a seam at every chunk border.</para>
    /// <para>So each chunk is drawn PADDED: the camera is widened by <see cref="PadPixels"/> on every
    /// side, the whole frame (the game's world and this mod's chain) is drawn into a larger
    /// target, and only the middle is handed back to the game. Light from a lamp across the
    /// border, a reflection reaching up from below it, a shadow falling over it, a blur crossing
    /// it: each is worked out from the real surroundings, the same way in both chunks, so the
    /// borders do not show. Each chunk is drawn twice and the first is thrown away, so caches
    /// that fill on one draw and are used on the next (object shadow bakes, the cloud mask) are
    /// ready for the one that is kept.</para>
    /// <para>The shot runs on a pipeline state of its own (screen <see cref="ShotScreenId"/>), so
    /// none of the live camera's windows or fades are disturbed, with the render clock frozen so
    /// every chunk shows the same moment, and with the effects that belong to a screen rather
    /// than to the world switched off: a vignette, chromatic aberration, the tilt-shift band,
    /// drops on the glass and the fog's darkening toward the top of the screen would otherwise
    /// repeat in every chunk. The exposure is the one the live view was using.</para>
    /// <para>Off by default (Effects in map screenshots): the picture a player gets from the
    /// screenshot button is not something an update changes unasked.</para>
    /// </remarks>
    internal static class MapScreenshotEffects
    {
        /// <summary>Extra world pixels drawn on every side of a chunk. Covers the water mirror's
        /// reach upward (768), sun shadows at the default dials, building shadows and lamp pools.
        /// A multiple of 64, so chunks stay aligned to tiles.</summary>
        internal static int PadPixels = 768;
        /// <summary>The pipeline and shadow state the shot draws on; released on the next switch
        /// back, like a split screen's departed player.</summary>
        internal const int ShotScreenId = 64;

        /// <summary>From the setting, every frame.</summary>
        internal static bool Enabled;
        /// <summary>True from the start of a map screenshot to its end, while effects are on.</summary>
        internal static bool Active { get; private set; }
        /// <summary>The live view's size when the shot began, for anything sized against the screen.</summary>
        internal static int LiveViewWidth { get; private set; } = 1;
        internal static int LiveViewHeight { get; private set; } = 1;
        /// <summary>The settings the shot draws with: the live ones, minus the screen-space looks.</summary>
        internal static ModConfig? ShotConfig { get; private set; }

        private static Func<RenderPipeline?>? _pipeline;
        private static Func<ModConfig>? _config;
        private static IMonitor? _monitor;
        private static MethodInfo? _allocateLightmap;
        private static MethodInfo? _draw;
        private static RenderTarget2D? _padded;
        private static bool _inPaddedDraw;
        private static bool _wasFrozen;
        private static int _chunks;

        internal static void Install(Harmony harmony, IMonitor monitor, Func<RenderPipeline?> pipeline, Func<ModConfig> config)
        {
            _monitor = monitor;
            _pipeline = pipeline;
            _config = config;
            _allocateLightmap = AccessTools.Method(typeof(Game1), "allocateLightmap", [typeof(int), typeof(int)]);
            _draw = AccessTools.Method(typeof(Game1), "_draw", [typeof(GameTime), typeof(RenderTarget2D)]);
            MethodInfo? take = AccessTools.Method(typeof(Game1), "takeMapScreenshot",
                [typeof(GameLocation), typeof(float), typeof(string), typeof(Action)]);
            if (_allocateLightmap == null || _draw == null || take == null)
            {
                monitor.Log("Map screenshots will stay the game's own: this game build does not take them the way the mod expects.", LogLevel.Trace);
                return;
            }
            try
            {
                harmony.Patch(take, prefix: new HarmonyMethod(typeof(MapScreenshotEffects), nameof(TakeMapScreenshot_Prefix)),
                    finalizer: new HarmonyMethod(typeof(MapScreenshotEffects), nameof(TakeMapScreenshot_Finalizer)));
                harmony.Patch(_draw, prefix: new HarmonyMethod(typeof(MapScreenshotEffects), nameof(Draw_Prefix)));
            }
            catch (Exception exception)
            {
                monitor.Log($"Map screenshots will stay the game's own: {exception.Message}", LogLevel.Trace);
            }
        }

        /// <summary>The screen id the renderers should use: the shot's own while a shot is taken.</summary>
        internal static int ScreenId(int liveScreenId) => Active ? ShotScreenId : liveScreenId;

        private static void TakeMapScreenshot_Prefix()
        {
            RenderPipeline? pipeline = _pipeline?.Invoke();
            if (!Enabled || pipeline == null || _config == null)
                return;
            ShotConfig = ScreenFreeCopy(_config());
            LiveViewWidth = Math.Max(1, Game1.viewport.Width);
            LiveViewHeight = Math.Max(1, Game1.viewport.Height);
            _wasFrozen = Determinism.Frozen;
            if (!_wasFrozen)
                Determinism.Freeze();
            pipeline.BeginMapShot(ShotScreenId);
            _chunks = 0;
            Active = true;
        }

        private static Exception? TakeMapScreenshot_Finalizer(Exception? __exception)
        {
            if (!Active)
                return __exception;
            Active = false;
            ShotConfig = null;
            _pipeline?.Invoke()?.EndMapShot();
            if (!_wasFrozen)
                Determinism.Thaw();
            _padded?.Dispose();
            _padded = null;
            _monitor?.Log($"Map screenshot drawn with the mod's effects: {_chunks} chunks, each padded by {PadPixels} px.", LogLevel.Trace);
            return __exception;
        }

        /// <summary>One chunk of the shot: draw it padded, twice, and hand back the middle.</summary>
        private static bool Draw_Prefix(Game1 __instance, GameTime gameTime, RenderTarget2D target_screen)
        {
            if (!Active || _inPaddedDraw || !__instance.takingMapScreenshot || target_screen == null)
                return true;
            GraphicsDevice device = Game1.graphics.GraphicsDevice;
            xTile.Dimensions.Rectangle chunk = Game1.viewport;
            int width = chunk.Width + 2 * PadPixels, height = chunk.Height + 2 * PadPixels;
            if (!TextureLimit.Fits(width, height))
                return true;
            if (_padded == null || _padded.Width != width || _padded.Height != height)
            {
                _padded?.Dispose();
                // PreserveContents: the game and the chain bind it again and again within one draw.
                _padded = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0,
                    RenderTargetUsage.PreserveContents);
            }
            try
            {
                _allocateLightmap!.Invoke(null, [width, height]);
                Game1.viewport = new xTile.Dimensions.Rectangle(chunk.X - PadPixels, chunk.Y - PadPixels, width, height);
                _inPaddedDraw = true;
                for (int pass = 0; pass < 2; pass++)
                    _draw!.Invoke(__instance, [gameTime, _padded]);
            }
            finally
            {
                _inPaddedDraw = false;
                Game1.viewport = chunk;
            }
            device.SetRenderTarget(target_screen);
            device.Clear(Color.Black);
            SpriteBatch batch = Game1.spriteBatch;
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            batch.Draw(_padded, Vector2.Zero, new Rectangle(PadPixels, PadPixels, chunk.Width, chunk.Height), Color.White);
            batch.End();
            _chunks++;
            return false;
        }

        /// <summary>The live settings without the looks that belong to a screen rather than to the world.</summary>
        private static ModConfig ScreenFreeCopy(ModConfig live)
        {
            var copy = (ModConfig)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null)!;
            copy.VignetteStrength = 0f;
            copy.ChromaticAberrationStrength = 0f;
            copy.TiltShiftEnabled = false;
            copy.WetWorldLensDrops = false;
            copy.FogTopBias = 0f;
            return copy;
        }
    }
}
