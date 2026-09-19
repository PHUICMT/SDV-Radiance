using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using xTile.Layers;

namespace SDVRadiance
{
    /// <summary>
    /// Mist a map paints on an always-front layer: kept out of the water mask, and held until the
    /// water has rippled.
    ///
    /// <para>
    /// Some maps hang their mist on its own always-front layer: Stardew Valley Expanded does it on
    /// rainy days (the MistEffects option), a sheet of translucent tiles over the Forest, the
    /// Railroad, the Mountain and more. The game draws it into the world frame, so it reached the
    /// water pass already mixed into the water under it, and the pass rippled the mist along with
    /// the water. Where the mist is thick the ripples lost their contrast, and the edge of the mist
    /// art read as a line where the water stopped. Worse, the water mask carved the mist out of
    /// the water the way it carves a rock or a bench, by its opaque pixels, so where the mist was
    /// dense the water lost its effect and its waterline altogether (measured on SVE's Forest in
    /// the rain: a tile's effect texels were 256 minus the mist's opaque texels, tile for tile).
    /// The mask gather now leaves mist layers out. And mist is air, the same as falling rain, so
    /// it is drawn the way the rain is: while the water stage runs, the game's draw of a mist layer is
    /// skipped, and the same layer is drawn once onto the water stage's output, flat over the
    /// rippled result. Nothing is drawn twice.
    /// </para>
    ///
    /// <para>
    /// A mist layer is an always-front layer whose every tile comes from a sheet named for mist or
    /// fog. A layer that mixes mist with anything else is left in the game's draw, so a roof or a
    /// canopy is never lifted out of the world. The answer is kept per layer object: a map edit
    /// reloads the map, and the new layers are asked afresh.
    /// </para>
    ///
    /// <para>
    /// The decision is one frame behind, as the rain's is: the stage list that says whether the
    /// water stage runs is built after the world is drawn. So a skipped layer is always drawn by
    /// the chain in the same frame, even on the frame the water stage stops wanting to run (the
    /// pipeline keeps the stage for that frame, at the presence it has, which is a copy once the
    /// fade is gone), and a hold older than two ticks is ignored, so a chain that stops running
    /// never leaves the mist missing for more than the frame it stopped in.
    /// </para>
    /// </summary>
    internal static class MistLayers
    {
        private sealed class ScreenMist
        {
            /// <summary>The tick the pipeline last asked for mist to be held.</summary>
            public int HoldAskedTick = int.MinValue;
            /// <summary>Mist layers the game did not draw this frame, waiting for the chain.</summary>
            public readonly List<Layer> Waiting = [];
            /// <summary>For the report: how many layers the last frame held, and their names.</summary>
            public int LastHeld;
            public string LastHeldNames = "";
        }

        private static readonly Dictionary<int, ScreenMist> _screens = [];
        private static readonly ConditionalWeakTable<Layer, StrongBox<bool>> _isMist = [];
        private static bool _inAlwaysFrontStep;
        private static bool _drawingOurselves;

        /// <summary>Two ticks: a draw can run more often than the game updates, and the hold from the
        /// last frame must still count on this one.</summary>
        private const int HoldLifetimeTicks = 2;

        private static ScreenMist ForThisScreen()
        {
            int screenId = Context.ScreenId;
            if (!_screens.TryGetValue(screenId, out ScreenMist? screen))
            {
                screen = new ScreenMist();
                _screens[screenId] = screen;
                LiveScreens.ForgetDeparted(_screens);
            }
            return screen;
        }

        internal static void AlwaysFrontStepBegins()
        {
            _inAlwaysFrontStep = true;
            if (_screens.TryGetValue(Context.ScreenId, out ScreenMist? screen))
                screen.Waiting.Clear();
        }

        internal static void AlwaysFrontStepEnds() => _inAlwaysFrontStep = false;

        /// <summary>Told by the pipeline while it builds this screen's stage list, exactly when it
        /// tells the rain: whether the water stage wants to run, so the next frame's mist waits.</summary>
        internal static void HoldOnThisScreen(bool waterStageWanted)
        {
            ScreenMist screen = ForThisScreen();
            screen.HoldAskedTick = waterStageWanted ? Game1.ticks : int.MinValue;
        }

        /// <summary>Whether this frame's world draw skipped a mist layer that the chain still owes.</summary>
        internal static bool WaitingOnThisScreen =>
            _screens.TryGetValue(Context.ScreenId, out ScreenMist? screen) && screen.Waiting.Count > 0;

        /// <summary>Prefix on xTile's Layer.Draw: skip a mist layer during the game's always-front
        /// step while the water stage carries it.</summary>
        internal static bool LayerDraw_Prefix(Layer __instance)
        {
            if (!_inAlwaysFrontStep || _drawingOurselves || HarmonyPatcher.GameIsTakingMapScreenshot)
                return true;
            if (!_screens.TryGetValue(Context.ScreenId, out ScreenMist? screen)
                || Game1.ticks - screen.HoldAskedTick > HoldLifetimeTicks
                || Game1.currentLocation?.map is not { } map || __instance.Map != map
                || !IsMist(__instance))
                return true;
            screen.Waiting.Add(__instance);
            return false;
        }

        internal static bool IsMist(Layer layer)
        {
            if (_isMist.TryGetValue(layer, out StrongBox<bool>? known))
                return known.Value;
            bool mist = MapLayers.BelongsToFamily(layer.Id, "AlwaysFront") && EveryTileIsMist(layer);
            _isMist.AddOrUpdate(layer, new StrongBox<bool>(mist));
            return mist;
        }

        private static bool EveryTileIsMist(Layer layer)
        {
            bool anyTile = false;
            var sheetVerdicts = new Dictionary<xTile.Tiles.TileSheet, bool>();
            for (int y = 0; y < layer.LayerHeight; y++)
            {
                for (int x = 0; x < layer.LayerWidth; x++)
                {
                    xTile.Tiles.Tile? tile = layer.Tiles[x, y];
                    if (tile?.TileSheet is not { } sheet)
                        continue;
                    anyTile = true;
                    if (!sheetVerdicts.TryGetValue(sheet, out bool mistSheet))
                    {
                        mistSheet = SheetIsMist(sheet);
                        sheetVerdicts[sheet] = mistSheet;
                    }
                    if (!mistSheet)
                        return false;
                }
            }
            return anyTile;
        }

        private static bool SheetIsMist(xTile.Tiles.TileSheet sheet)
        {
            string name = System.IO.Path.GetFileName(sheet.ImageSource ?? sheet.Id ?? "");
            return name.Contains("mist", StringComparison.OrdinalIgnoreCase)
                || name.Contains("fog", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Draw the held mist onto the water stage's output, the way the game would have
        /// drawn it (texture-sorted, point sampled, the same viewport), scaled to the chain's
        /// buffer. This side of the capture never meets the vanilla lightmap, so the ambient the
        /// rain and the particles use dims it instead.</summary>
        internal static void DrawOntoChain(SpriteBatch spriteBatch, RenderTarget2D destination, int frameWidth, Vector3 ambient)
        {
            if (!_screens.TryGetValue(Context.ScreenId, out ScreenMist? screen))
                return;
            screen.LastHeld = screen.Waiting.Count;
            if (screen.Waiting.Count == 0)
            {
                screen.LastHeldNames = "";
                return;
            }
            screen.LastHeldNames = string.Join(", ", screen.Waiting.ConvertAll(layer => layer.Id));
            float pixelScale = frameWidth > 0 ? destination.Width / (float)frameWidth : 1f;
            var device = Game1.mapDisplayDevice as xTile.Display.XnaDisplayDevice;
            Color previousTint = device?.ModulationColour ?? Color.White;
            _drawingOurselves = true;
            try
            {
                if (device != null)
                    device.ModulationColour = new Color(ambient.X, ambient.Y, ambient.Z, 1f);
                foreach (Layer layer in screen.Waiting)
                {
                    spriteBatch.Begin(SpriteSortMode.Texture, BlendState.AlphaBlend, SamplerState.PointClamp,
                        DepthStencilState.None, RasterizerState.CullNone, null, Matrix.CreateScale(pixelScale));
                    Game1.mapDisplayDevice.BeginScene(spriteBatch);
                    layer.Draw(Game1.mapDisplayDevice, Game1.viewport, xTile.Dimensions.Location.Origin, wrapAround: false, 4, -1f);
                    spriteBatch.End();
                }
            }
            finally
            {
                if (device != null)
                    device.ModulationColour = previousTint;
                Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                _drawingOurselves = false;
                screen.Waiting.Clear();
            }
        }

        /// <summary>One line for radiance_report.</summary>
        internal static string Diag()
        {
            if (!_screens.TryGetValue(Context.ScreenId, out ScreenMist? screen))
                return "map mist: none held (no mist layer drawn on this screen yet)";
            bool holding = Game1.ticks - screen.HoldAskedTick <= HoldLifetimeTicks;
            return screen.LastHeld > 0
                ? $"map mist: {screen.LastHeld} layer(s) held over the rippled water ({screen.LastHeldNames})"
                : $"map mist: none held (water stage {(holding ? "running, no mist layer on this map" : "not running, mist stays in the game's draw")})";
        }
    }
}
