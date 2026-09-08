using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - THE LAMP SHADOW MARCH WINDOW: the answer to "what stands between this lamp
    /// and this ground" is kept between frames instead of being walked again on every one.
    ///
    /// <para>
    /// The march is the one thing this lighting pays per lamp per pixel, and after the 1.7.3 work
    /// it was still about half of the flood pass in a lit scene. Every frame of it was a repeat:
    /// the ray from a lamp to a patch of ground meets the same fences and the same trees as it did
    /// a frame ago, because nothing that the ray reads changes while you stand still, and while you
    /// walk the only change is the strip of ground that has just come into view. What made it a
    /// repeat that could not be kept was the target: at half resolution of the SCREEN, every texel
    /// changed the ground it stood over the moment the camera moved a pixel.
    /// </para>
    ///
    /// <para>
    /// So the target is anchored to the world instead. It covers the screen plus a tile of margin
    /// on every side, its origin is a whole tile, and its texels sit on the same ground until the
    /// camera crosses a tile boundary. Inside that window the lighting pass reads its shadow back
    /// at the pixel's place in the world (MarchOrigin and MarchSize in floodlight.fx), so scrolling
    /// is a change of offset and nothing more. Each of the eight shadowed lamps owns a channel of
    /// the two targets, and a channel is fired again only when its lamp has moved, changed reach,
    /// or handed the slot to another lamp; the whole window is fired again on a tile crossing, an
    /// occluder rebuild, or a change of the two dials the ray reads. Standing still nothing is
    /// fired at all. The colour write mask is what lets one channel be redrawn while the other
    /// three keep their answer: the target is PreserveContents and the draw touches only the
    /// channels it was asked for.
    /// </para>
    ///
    /// <para>
    /// The same shader serves with the cache off: the window is then the screen exactly, origin
    /// WorldTileOffset and size TilesPerScreen, every channel wanted, so an A/B on the switch
    /// measures the caching alone and nothing else. That road is also what split screen takes,
    /// because one pipeline draws both cameras through one set of targets and a window kept for
    /// one camera would be declared stale by the other on every frame.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Console A/B (radiance_marchcache): null follows the setting; true or false
        /// overrides it for the session and is not saved.</summary>
        internal static bool? MarchCacheOverride;
        /// <summary>The proof switch: keep the window but fire every channel every frame, so a
        /// frozen frame with this on and off can be compared byte for byte. Any difference is a
        /// channel the invalidation missed.</summary>
        internal static bool MarchCacheFireEveryFrame;

        // What the window did, for radiance_report. Counters since launch, so a walk can be read
        // as "one whole re-march a tile crossing" and standing still as "nothing fired".
        internal static bool LastMarchWindowRoad;
        internal static int LastMarchWindowTilesW, LastMarchWindowTilesH, LastMarchWindowTexelsPerTile;
        internal static int LastMarchWindowTileX, LastMarchWindowTileY;
        internal static int LastMarchChannelsFired;
        internal static long MarchWholeRefiresSinceLaunch, MarchChannelFiresSinceLaunch, MarchQuietFramesSinceLaunch;

        /// <summary>How far a lamp may drift before its channel is fired again. The lamp's
        /// position makes a round trip through screen UV, and that wobbles at the sixth decimal;
        /// a hundredth of a tile is under a texel of the mask the ray reads.</summary>
        private const float MarchLampMoveEpsilonTiles = 0.01f;
        private BlendState?[]? _marchWriteMasks;

        /// <summary>Fire the LampMarch passes for this frame, by whichever road applies, and hand
        /// the flood pass the two targets and the window they describe.</summary>
        private void RunLampMarch(SpriteBatch spriteBatch, Texture2D source, Effect effect, ModConfig config)
        {
            var viewport = Game1.viewport;
            var tilesPerScreen = new Vector2(viewport.Width / 64f, viewport.Height / 64f);
            var worldTileOffset = new Vector2(viewport.X / 64f, viewport.Y / 64f);
            bool cacheOn = MarchCacheOverride ?? config.LightShadowMarchCache;
            bool windowRoad = cacheOn && LiveScreens.Count == 1;
            effect.CurrentTechnique = effect.Techniques["LampMarch"];
            if (!windowRoad)
            {
                ReleaseMarchWindow();
                GetParam(effect, "MarchOrigin")?.SetValue(worldTileOffset);
                GetParam(effect, "MarchSize")?.SetValue(tilesPerScreen);
                GetParam(effect, "MarchChannelWanted")?.SetValue(Vector4.One);
                GetParam(effect, "MarchBase")?.SetValue(0f);
                DrawFull(spriteBatch, source, _halfResolutionScratchA!, effect);
                GetParam(effect, "MarchBase")?.SetValue(4f);
                DrawFull(spriteBatch, source, _halfResolutionScratchB!, effect);
                GetParam(effect, "MarchATexture")?.SetValue(_halfResolutionScratchA);
                GetParam(effect, "MarchBTexture")?.SetValue(_halfResolutionScratchB);
                LastMarchWindowRoad = false;
                LastMarchChannelsFired = FloodShadowedLights;
                return;
            }

            // The window: a whole tile before the screen's first tile, and enough tiles past it
            // that the screen's last partial tile and a margin are inside whatever the sub-tile
            // scroll is. Texels per tile match the half-resolution road, so the picture is the
            // same one, only anchored.
            int originTileX = (int)Math.Floor(viewport.X / 64f) - 1;
            int originTileY = (int)Math.Floor(viewport.Y / 64f) - 1;
            int tilesW = (int)Math.Ceiling(tilesPerScreen.X) + 3;
            int tilesH = (int)Math.Ceiling(tilesPerScreen.Y) + 3;
            int texelsPerTile = Math.Max(1, (int)Math.Round(_halfResolutionScratchA!.Width / Math.Max(1f, tilesPerScreen.X)));
            int targetW = tilesW * texelsPerTile, targetH = tilesH * texelsPerTile;
            bool whole = MarchCacheFireEveryFrame;
            if (_marchWindowA == null || _marchWindowB == null || _marchWindowA.Width != targetW || _marchWindowA.Height != targetH
                || _marchWindowA.Format != _halfResolutionScratchA.Format)
            {
                ReleaseMarchWindow();
                _marchWindowA = VramTally.Track(new RenderTarget2D(_device, targetW, targetH, false, _halfResolutionScratchA.Format,
                    DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "lamp shadow march window");
                _marchWindowB = VramTally.Track(new RenderTarget2D(_device, targetW, targetH, false, _halfResolutionScratchA.Format,
                    DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "lamp shadow march window");
                whole = true;
            }
            float softness = MathHelper.Clamp(config.LightShadowSoftness, 0f, 2f);
            if (originTileX != _marchWindowTileX || originTileY != _marchWindowTileY
                || _floodOccluderGeneration != _marchWindowOccluderGeneration
                || LastMarchStepCeiling != _marchWindowStepCeiling || softness != _marchWindowSoftness)
                whole = true;
            _marchWindowTileX = originTileX;
            _marchWindowTileY = originTileY;
            _marchWindowOccluderGeneration = _floodOccluderGeneration;
            _marchWindowStepCeiling = LastMarchStepCeiling;
            _marchWindowSoftness = softness;

            // Per channel: the lamp it holds, where that lamp stands in the world and how far it
            // reaches. A channel past DirectCount is never read by the flood pass, so a lamp
            // leaving costs nothing and its stale answer is harmless.
            int wantedA = 0, wantedB = 0;
            for (int k = 0; k < FloodShadowedLights; k++)
            {
                bool on = k < _floodDirectCount;
                int id = on ? _floodLightIds[k] : 0;
                var tile = on
                    ? new Vector2(_floodLightPositions[k].X * tilesPerScreen.X + worldTileOffset.X,
                                  _floodLightPositions[k].Y * tilesPerScreen.Y + worldTileOffset.Y)
                    : Vector2.Zero;
                float reach = on ? _floodLightColors[k].W : 0f;
                bool wanted = on && (whole || !_marchChannelOn[k] || id != _marchChannelId[k]
                    || Vector2.Distance(tile, _marchChannelTile[k]) > MarchLampMoveEpsilonTiles
                    || Math.Abs(reach - _marchChannelReach[k]) > MarchLampMoveEpsilonTiles);
                _marchChannelOn[k] = on;
                _marchChannelId[k] = id;
                _marchChannelTile[k] = tile;
                _marchChannelReach[k] = reach;
                if (!wanted)
                    continue;
                if (k < 4) wantedA |= 1 << k;
                else wantedB |= 1 << (k - 4);
            }

            GetParam(effect, "MarchOrigin")?.SetValue(new Vector2(originTileX, originTileY));
            GetParam(effect, "MarchSize")?.SetValue(new Vector2(tilesW, tilesH));
            if (wantedA != 0)
            {
                GetParam(effect, "MarchBase")?.SetValue(0f);
                GetParam(effect, "MarchChannelWanted")?.SetValue(ChannelWantedVector(wantedA));
                DrawFull(spriteBatch, source, _marchWindowA, effect, MarchWriteMask(wantedA));
            }
            if (wantedB != 0)
            {
                GetParam(effect, "MarchBase")?.SetValue(4f);
                GetParam(effect, "MarchChannelWanted")?.SetValue(ChannelWantedVector(wantedB));
                DrawFull(spriteBatch, source, _marchWindowB, effect, MarchWriteMask(wantedB));
            }
            GetParam(effect, "MarchATexture")?.SetValue(_marchWindowA);
            GetParam(effect, "MarchBTexture")?.SetValue(_marchWindowB);

            int fired = System.Numerics.BitOperations.PopCount((uint)wantedA) + System.Numerics.BitOperations.PopCount((uint)wantedB);
            LastMarchWindowRoad = true;
            LastMarchWindowTilesW = tilesW; LastMarchWindowTilesH = tilesH; LastMarchWindowTexelsPerTile = texelsPerTile;
            LastMarchWindowTileX = originTileX; LastMarchWindowTileY = originTileY;
            LastMarchChannelsFired = fired;
            MarchChannelFiresSinceLaunch += fired;
            if (whole && fired > 0) MarchWholeRefiresSinceLaunch++;
            if (fired == 0) MarchQuietFramesSinceLaunch++;
        }

        private static Vector4 ChannelWantedVector(int bits) => new(
            (bits & 1) != 0 ? 1f : 0f, (bits & 2) != 0 ? 1f : 0f, (bits & 4) != 0 ? 1f : 0f, (bits & 8) != 0 ? 1f : 0f);

        /// <summary>Opaque, writing only the channels asked for. Sixteen at most, made once each:
        /// a BlendState is sealed the first time it is bound.</summary>
        private BlendState MarchWriteMask(int bits)
        {
            _marchWriteMasks ??= new BlendState?[16];
            return _marchWriteMasks[bits] ??= new BlendState
            {
                Name = $"march write mask {bits}",
                ColorWriteChannels = (ColorWriteChannels)bits,
            };
        }

        private void DrawFull(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, Effect effect, BlendState blend)
        {
            _device.SetRenderTarget(dest);
            spriteBatch.Begin(SpriteSortMode.Deferred, blend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            spriteBatch.Draw(source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
            spriteBatch.End();
        }

        private void ReleaseMarchWindow()
        {
            if (_marchWindowA == null && _marchWindowB == null)
                return;
            _marchWindowA?.Dispose();
            _marchWindowB?.Dispose();
            _marchWindowA = _marchWindowB = null;
            _marchWindowTileX = _marchWindowTileY = int.MinValue;
            for (int k = 0; k < FloodShadowedLights; k++)
                _marchChannelOn[k] = false;
        }

        /// <summary>One line for the report.</summary>
        internal static string DescribeMarchWindow()
        {
            if (!LastMarchWindowRoad)
                return "the screen, marched every frame (the window is off, or this is split screen)";
            return $"world-anchored, {LastMarchWindowTilesW}x{LastMarchWindowTilesH} tiles at {LastMarchWindowTexelsPerTile} texels a tile, "
                + $"origin tile ({LastMarchWindowTileX},{LastMarchWindowTileY}); fired {LastMarchChannelsFired} of {FloodShadowedLights} lamp channels this frame; "
                + $"since launch {MarchWholeRefiresSinceLaunch} whole re-marches, {MarchChannelFiresSinceLaunch} channel fires, "
                + $"{MarchQuietFramesSinceLaunch} frames with nothing to fire";
        }
    }
}
