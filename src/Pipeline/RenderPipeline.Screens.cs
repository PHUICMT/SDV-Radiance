using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline — SPLIT SCREEN. One pipeline serves every screen, and almost all of it can:
    /// the screens draw one after another, each one running the whole chain start to finish before
    /// the next begins, so the intermediate render targets are simply reused. Duplicating the
    /// pipeline per screen would double the video memory for nothing.
    ///
    /// <para>
    /// What cannot be shared is anything remembered BETWEEN frames that was built around where a
    /// camera was pointing. There were four: the water mask window, the occluder grid, the mirror's
    /// scenery cache, and the bounce-light grid. Each is keyed by the tile its window starts at, so
    /// with two cameras twenty tiles apart every one of them decided it was out of date, rebuilt for
    /// the screen that asked, and was immediately declared out of date again by the other. The water
    /// mask never landed at all: a rebuild takes a few frames on a worker thread, and it was being
    /// invalidated before it could finish, every time, which is why the second screen showed no
    /// reflections and no water effects while the first was mostly fine.
    /// </para>
    ///
    /// <para>
    /// Each screen now keeps its own copy of exactly those, swapped into place when that screen's
    /// turn comes round. The presence fades live here too: two screens sharing one set of fades
    /// meant walking one player away from water faded the water out on the other player's half.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Everything the pipeline remembers between frames that belongs to one camera.</summary>
        private sealed class ScreenState
        {
            // ---- water mask window ----
            public Texture2D? WaterMask;
            public Texture2D? WaterMaskCore;
            public Texture2D? WaterSignedDistance;
            /// <summary>Published copy of the composed water flags, for the "is there water near
            /// this sprite" test. A copy rather than the compose buffer itself: that one is written
            /// by a worker thread and belongs to whichever rebuild is running, not to a screen.</summary>
            public bool[]? WaterTilesInMask;
            public GameLocation? LastWaterLocation;
            public int LastWaterTileX = int.MinValue, LastWaterTileY = int.MinValue, LastWaterBuildTick = int.MinValue;
            public int LastWaterHookVersion = -1, LastWaterLabelVersion = -1, LastWaterEpoch = -1;
            public bool HasWaterInMask;
            public float WaterInMaskEase;
            public Vector2 WaterMaskTilesPerScreen, WaterMaskWorldTileOffset, WaterMaskPixelSize;

            // ---- occluder grid ----
            public Texture2D? OccluderMask;
            public Color[]? OccluderMaskPixels;
            public int OccluderTileX = int.MinValue, OccluderTileY = int.MinValue, OccluderCacheTick = int.MinValue;
            public int OccluderInputsHash;
            public SurfaceMap? OccluderSurfaceMap;
            public int OccluderMaskBuildMode;
            public Vector2 OccluderTilesPerScreen, OccluderWorldTileOffset, OccluderMaskSize;
            // FLOOD's own mask state, kept apart from classic's above (see _floodOccluderMask).
            public Texture2D? FloodOccluderMask;
            public Color[]? FloodOccluderMaskPixels;
            public int FloodOccluderTileX = int.MinValue, FloodOccluderTileY = int.MinValue, FloodOccluderCacheTick = int.MinValue;
            public int FloodOccluderInputsHash;
            public SurfaceMap? FloodOccluderSurfaceMap;
            public Vector2 FloodOccluderMaskSize;
            public bool ShadowsReady;

            // ---- the mirror's scenery cache ----
            public RenderTarget2D? MirrorSceneCache;
            public GameLocation? SceneCacheLocation;
            public int SceneCacheAnchorX, SceneCacheAnchorY, SceneCacheBuiltTick = -1;

            // ---- presence fades ----
            public GameLocation? FadeLocation;
            public float FadeWater, FadeCloud, FadeLighting, FadeFlood, FadeTilt;

            // ---- auto-exposure meter, and the eases that follow the room ----
            // Two screens in two rooms dragged one exposure between two targets, which scales the
            // WHOLE frame: the picture on both halves breathing brighter and darker together.
            public RenderTarget2D? LuminanceTarget;
            public Color[]? LuminancePixels;
            public bool LuminancePrimed;
            public GameLocation? ExposureMeterLocation;
            public float MeteredExposure = 1f;
            public Vector3 ExposureEase = Vector3.One;
            public float RoomSaturationEase = 1f;
            public float PaneDaylightEase, WindowDaylightEase, WindowRoomLightEase;
            public float ShimmerEase, RainRingsEase;

            // ---- bounce-light grid ----
            public FloodLightmap Flood = new();

            public void Release()
            {
                WaterMask?.Dispose();
                WaterMaskCore?.Dispose();
                WaterSignedDistance?.Dispose();
                OccluderMask?.Dispose();
                FloodOccluderMask?.Dispose();
                MirrorSceneCache?.Dispose();
                LuminanceTarget?.Dispose();
                Flood.Dispose();
            }
        }

        private readonly Dictionary<int, ScreenState> _screenStates = new();
        /// <summary>Which screen's state is loaded into the fields right now. -1 before the first
        /// swap, which is also the single-screen case until a second screen ever appears.</summary>
        private int _activeScreenId = -1;

        /// <summary>
        /// Hand the pipeline over to one screen. Called at the top of that screen's turn, from both
        /// the pre-draw and post-draw events, because either can be the first thing a frame does.
        /// A no-op in single player, where the id never changes.
        /// </summary>
        internal void BeginScreen(int screenId)
        {
            if (screenId == _activeScreenId)
                return;
            if (_activeScreenId >= 0)
            {
                if (!_screenStates.TryGetValue(_activeScreenId, out ScreenState? outgoing))
                    _screenStates[_activeScreenId] = outgoing = new ScreenState();
                SaveScreenState(outgoing);
            }
            _activeScreenId = screenId;
            if (!_screenStates.TryGetValue(screenId, out ScreenState? incoming))
            {
                // A brand-new screen starts blank rather than inheriting the other camera's
                // windows: every one of them would be declared out of date on its first look
                // anyway, and a wrong window drawn for one frame is a wrong window on screen.
                _screenStates[screenId] = incoming = new ScreenState();
            }
            LoadScreenState(incoming);
            ForgetDepartedScreens();
        }

        private void SaveScreenState(ScreenState s)
        {
            s.WaterMask = _waterMask;
            s.WaterMaskCore = _waterMaskCore;
            s.WaterSignedDistance = _waterSignedDistanceTexture;
            s.WaterTilesInMask = _waterTilesInMask;
            s.LastWaterLocation = _lastWaterLocation;
            s.LastWaterTileX = _lastWaterTileX;
            s.LastWaterTileY = _lastWaterTileY;
            s.LastWaterBuildTick = _lastWaterBuildTick;
            s.LastWaterHookVersion = _lastWaterHookVersion;
            s.LastWaterLabelVersion = _lastWaterLabelVersion;
            s.LastWaterEpoch = _lastWaterEpoch;
            s.HasWaterInMask = _hasWaterInMask;
            s.WaterInMaskEase = _waterInMaskEase;
            s.WaterMaskTilesPerScreen = _waterMaskTilesPerScreen;
            s.WaterMaskWorldTileOffset = _waterMaskWorldTileOffset;
            s.WaterMaskPixelSize = _waterMaskPixelSize;

            s.OccluderMask = _occluderMask;
            s.OccluderMaskPixels = _occluderMaskPixels;
            s.OccluderTileX = _occluderTileX;
            s.OccluderTileY = _occluderTileY;
            s.OccluderCacheTick = _occluderCacheTick;
            s.OccluderInputsHash = _occluderInputsHash;
            s.OccluderSurfaceMap = _occluderSurfaceMap;
            s.OccluderMaskBuildMode = _occluderMaskBuildMode;
            s.OccluderTilesPerScreen = _occluderTilesPerScreen;
            s.OccluderWorldTileOffset = _occluderWorldTileOffset;
            s.OccluderMaskSize = _occluderMaskSize;
            s.FloodOccluderMask = _floodOccluderMask;
            s.FloodOccluderMaskPixels = _floodOccluderMaskPixels;
            s.FloodOccluderTileX = _floodOccluderTileX;
            s.FloodOccluderTileY = _floodOccluderTileY;
            s.FloodOccluderCacheTick = _floodOccluderCacheTick;
            s.FloodOccluderInputsHash = _floodOccluderInputsHash;
            s.FloodOccluderSurfaceMap = _floodOccluderSurfaceMap;
            s.FloodOccluderMaskSize = _floodOccluderMaskSize;
            s.ShadowsReady = _shadowsReady;

            s.MirrorSceneCache = _mirrorSceneCache;
            s.SceneCacheLocation = _sceneCacheLocation;
            s.SceneCacheAnchorX = _sceneCacheAnchorX;
            s.SceneCacheAnchorY = _sceneCacheAnchorY;
            s.SceneCacheBuiltTick = _sceneCacheBuiltTick;

            s.FadeLocation = _fadeLocation;
            s.FadeWater = _fadeWater;
            s.FadeCloud = _fadeCloud;
            s.FadeLighting = _fadeLighting;
            s.FadeFlood = _fadeFlood;
            s.FadeTilt = _fadeTilt;

            s.LuminanceTarget = _luminanceRenderTarget;
            s.LuminancePixels = _luminancePixels;
            s.LuminancePrimed = _isLuminancePrimed;
            s.ExposureMeterLocation = _exposureMeterLocation;
            s.MeteredExposure = _meteredExposure;
            s.ExposureEase = _exposureEase;
            s.RoomSaturationEase = _roomSaturationEase;
            s.PaneDaylightEase = _paneDaylightEase;
            s.WindowDaylightEase = _windowDaylightEase;
            s.WindowRoomLightEase = _windowRoomLightEase;
            s.ShimmerEase = _shimmerEase;
            s.RainRingsEase = _rainRingsEase;

            s.Flood = _flood;
        }

        private void LoadScreenState(ScreenState s)
        {
            _waterMask = s.WaterMask;
            _waterMaskCore = s.WaterMaskCore;
            _waterSignedDistanceTexture = s.WaterSignedDistance;
            _waterTilesInMask = s.WaterTilesInMask;
            _lastWaterLocation = s.LastWaterLocation;
            _lastWaterTileX = s.LastWaterTileX;
            _lastWaterTileY = s.LastWaterTileY;
            _lastWaterBuildTick = s.LastWaterBuildTick;
            _lastWaterHookVersion = s.LastWaterHookVersion;
            _lastWaterLabelVersion = s.LastWaterLabelVersion;
            _lastWaterEpoch = s.LastWaterEpoch;
            _hasWaterInMask = s.HasWaterInMask;
            _waterInMaskEase = s.WaterInMaskEase;
            _waterMaskTilesPerScreen = s.WaterMaskTilesPerScreen;
            _waterMaskWorldTileOffset = s.WaterMaskWorldTileOffset;
            _waterMaskPixelSize = s.WaterMaskPixelSize;

            _occluderMask = s.OccluderMask;
            _occluderMaskPixels = s.OccluderMaskPixels;
            _occluderTileX = s.OccluderTileX;
            _occluderTileY = s.OccluderTileY;
            _occluderCacheTick = s.OccluderCacheTick;
            _occluderInputsHash = s.OccluderInputsHash;
            _occluderSurfaceMap = s.OccluderSurfaceMap;
            _occluderMaskBuildMode = s.OccluderMaskBuildMode;
            _occluderTilesPerScreen = s.OccluderTilesPerScreen;
            _occluderWorldTileOffset = s.OccluderWorldTileOffset;
            _occluderMaskSize = s.OccluderMaskSize;
            _floodOccluderMask = s.FloodOccluderMask;
            _floodOccluderMaskPixels = s.FloodOccluderMaskPixels;
            _floodOccluderTileX = s.FloodOccluderTileX;
            _floodOccluderTileY = s.FloodOccluderTileY;
            _floodOccluderCacheTick = s.FloodOccluderCacheTick;
            _floodOccluderInputsHash = s.FloodOccluderInputsHash;
            _floodOccluderSurfaceMap = s.FloodOccluderSurfaceMap;
            _floodOccluderMaskSize = s.FloodOccluderMaskSize;
            _shadowsReady = s.ShadowsReady;

            _mirrorSceneCache = s.MirrorSceneCache;
            _sceneCacheLocation = s.SceneCacheLocation;
            _sceneCacheAnchorX = s.SceneCacheAnchorX;
            _sceneCacheAnchorY = s.SceneCacheAnchorY;
            _sceneCacheBuiltTick = s.SceneCacheBuiltTick;

            _fadeLocation = s.FadeLocation;
            _fadeWater = s.FadeWater;
            _fadeCloud = s.FadeCloud;
            _fadeLighting = s.FadeLighting;
            _fadeFlood = s.FadeFlood;
            _fadeTilt = s.FadeTilt;

            _luminanceRenderTarget = s.LuminanceTarget;
            _luminancePixels = s.LuminancePixels;
            _isLuminancePrimed = s.LuminancePrimed;
            _exposureMeterLocation = s.ExposureMeterLocation;
            _meteredExposure = s.MeteredExposure;
            _exposureEase = s.ExposureEase;
            _roomSaturationEase = s.RoomSaturationEase;
            _paneDaylightEase = s.PaneDaylightEase;
            _windowDaylightEase = s.WindowDaylightEase;
            _windowRoomLightEase = s.WindowRoomLightEase;
            _shimmerEase = s.ShimmerEase;
            _rainRingsEase = s.RainRingsEase;

            _flood = s.Flood;

            // The player colour bake runs before this screen's chain gets a look at the frame and
            // gates on this flag. It used to hold whichever screen answered last, so a player on a
            // shore next to a player in a cave got no reflection of themselves.
            ShadowRenderer.WaterOnScreen = _hasWaterInMask;
        }

        /// <summary>Is this screen still being drawn? Screens are numbered from zero with no gaps,
        /// so anything at or past the count has left.</summary>
        private static bool ScreenStillExists(int screenId)
            => screenId >= 0 && screenId < (GameRunner.instance?.gameInstances?.Count ?? 1);

        /// <summary>Give back the video memory of screens that have left. A departed player's mask
        /// and scenery cache are several megabytes each and nothing will ever read them again.</summary>
        private void ForgetDepartedScreens()
        {
            int live = GameRunner.instance?.gameInstances?.Count ?? 1;
            if (_screenStates.Count <= live)
                return;
            _departedScreens.Clear();
            foreach (var kv in _screenStates)
            {
                if (kv.Key >= live && kv.Key != _activeScreenId)
                    _departedScreens.Add(kv.Key);
            }
            foreach (int id in _departedScreens)
            {
                _screenStates[id].Release();
                _screenStates.Remove(id);
            }
        }

        private readonly List<int> _departedScreens = new();

        /// <summary>Drop every screen's kept state. Used when the pipeline itself goes away.</summary>
        private void ReleaseScreenStates()
        {
            foreach (var kv in _screenStates)
            {
                if (kv.Key != _activeScreenId)
                    kv.Value.Release();
            }
            _screenStates.Clear();
            _activeScreenId = -1;
        }
    }
}
