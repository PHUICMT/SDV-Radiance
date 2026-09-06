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
            public Texture2D? WaterSignedDistance;
            public Texture2D? WaterRealShoreDistance;
            public Texture2D? WaterPlungeChurn;
            /// <summary>The pairs' spares travel with their fronts (TextureDoubleBuffer): handing
            /// one screen's front to another screen as a spare would write into a texture the
            /// first screen's draw still reads, which is the very wait the pair removes.</summary>
            public Texture2D? WaterMaskSpare;
            public Texture2D? WaterSignedDistanceSpare;
            public Texture2D? WaterRealShoreDistanceSpare;
            public Texture2D? WaterPlungeChurnSpare;
            /// <summary>Published copy of the composed water flags, for the "is there water near
            /// this sprite" test. A copy rather than the compose buffer itself: that one is written
            /// by a worker thread and belongs to whichever rebuild is running, not to a screen.</summary>
            public bool[]? WaterTilesInMask;
            /// <summary>Which refill of the flags above this screen is holding, so the
            /// summed-area cache can tell two screens' windows apart. Both are refilled in
            /// place, so without this a screen switch reads as no change at all.</summary>
            public int WaterTilesVersion;
            public GameLocation? LastWaterLocation;
            public int LastWaterTileX = int.MinValue, LastWaterTileY = int.MinValue, LastWaterBuildTick = int.MinValue;
            public int LastWaterHookVersion = -1, LastWaterLabelVersion = -1, LastWaterEpoch = -1;
            public bool HasWaterInMask;
            public float WaterInMaskEase;
            public Vector2 WaterMaskTilesPerScreen, WaterMaskWorldTileOffset, WaterMaskPixelSize;

            // ---- occluder grid ----
            public Texture2D? OccluderMask;
            public Texture2D? OccluderMaskSpare;
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
            /// <summary>The occluder mask's two companions. The mask itself was already per
            /// screen; these were not, and a screen whose gate said "nothing has moved, keep what
            /// you built" then read the OTHER screen's window out of them. The bounce light was
            /// computed from a picture of somewhere else on those frames, and since the two gates
            /// open on different ticks it alternated: the flicker that survived every other fix.
            /// A texture and its pair belong to whoever the mask belongs to.</summary>
            public Texture2D? FloodOccluderBase;
            public Texture2D? FloodOccluderBaseSpare;
            public RenderTarget2D?[] FloodOccluderSoft = new RenderTarget2D?[FloodOccluderSoftLevels];
            public bool ShadowsReady;

            // ---- the mirror's scenery cache ----
            public RenderTarget2D? MirrorSceneCache;
            public GameLocation? SceneCacheLocation;
            public int SceneCacheAnchorX, SceneCacheAnchorY, SceneCacheBuiltTick = -1;
            public long SceneAnimStamp = -1;

            // ---- presence fades ----
            public GameLocation? FadeLocation;
            public float FadeWater, FadeCloud, FadeLighting, FadeFlood, FadeTilt;
            /// <summary>Per screen because it follows the location, and two players can be on
            /// opposite sides of a door.</summary>
            public float TiltIndoorEase;

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
            public float FadeWet;

            // ---- the sprite relief's normal buffer ----
            // This buffer is SCREEN SPACE, and it was one buffer for the whole game. The two
            // screens replayed their own sprites into it in turn, and on any frame where a screen
            // recorded no world draw it kept whatever the other camera had just written: the world
            // lit by a stamp of sprites standing somewhere else, flickering in and out as those
            // frames came and went. The file's own comment predicted this for a camera move; two
            // cameras are a camera move on every frame. Reported as the light flickering the
            // moment either player walked.
            public RenderTarget2D? NormalRenderTarget;
            public bool NormalPassReady;
            public Point NormalPassViewport;
            public float ReliefEase;

            // ---- the eased amounts that follow THIS screen's own scene ----
            // Each of these has a target that asks whether the screen is outdoors, or what its
            // own scene is doing. Shared between two screens, one player standing in a room and
            // the other in a field pulled every one of them in opposite directions on alternate
            // frames, and the outdoor half's shafts, fog and building shadows pulsed in time with
            // it. Reported as the sunbeams flickering and moving about as soon as a second player
            // joined, before that player had even come outside.
            public float GodRayAmount, FogDayAmount, FogMistAmount, FadeBuildingShadow;
            public float ToneMapEase, VignetteEase, ChromaticAberrationEase;

            // ---- which lights this screen is showing, and how far each has faded ----
            public Dictionary<int, LightFade> LightRamp = new();
            public HashSet<int> LightChosen = new();
            public GameLocation? LightRampLocation;

            // ---- the cloud mask the sun shafts read back a frame later ----
            // One shared keep between two screens meant screen 0's shafts read screen 1's sky,
            // drawn from a camera eighteen tiles away, every other frame: the beams jumped
            // between the two positions and read as flicker. Each screen keeps its own.
            public RenderTarget2D? CloudMaskKeep;
            public int CloudMaskTick = int.MinValue;
            public Vector2 CloudMaskTileOffset;
            public float CloudMaskStrength, ShaftCloudEase;

            // ---- bounce-light grid ----
            public FloodLightmap Flood = new();
            public RadianceCascades Cascades = new();
            public float CascadeBlend;
            public bool CascadesReady;

            public void Release()
            {
                WaterMask?.Dispose();
                WaterSignedDistance?.Dispose();
                WaterRealShoreDistance?.Dispose();
                WaterPlungeChurn?.Dispose();
                WaterMaskSpare?.Dispose();
                WaterSignedDistanceSpare?.Dispose();
                WaterRealShoreDistanceSpare?.Dispose();
                WaterPlungeChurnSpare?.Dispose();
                OccluderMask?.Dispose();
                OccluderMaskSpare?.Dispose();
                FloodOccluderMask?.Dispose();
                MirrorSceneCache?.Dispose();
                LuminanceTarget?.Dispose();
                CloudMaskKeep?.Dispose();
                NormalRenderTarget?.Dispose();
                FloodOccluderBase?.Dispose();
                FloodOccluderBaseSpare?.Dispose();
                for (int i = 0; i < FloodOccluderSoft.Length; i++) FloodOccluderSoft[i]?.Dispose();
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
            DrawingScreen = this;
            if (screenId == _activeScreenId)
                return;
            if (_activeScreenId >= 0)
            {
                if (!_screenStates.TryGetValue(_activeScreenId, out ScreenState? outgoing))
                    _screenStates[_activeScreenId] = outgoing = new ScreenState();
                // The outgoing screen's state is already in its own object: nothing to copy.
            }
            _activeScreenId = screenId;
            if (!_screenStates.TryGetValue(screenId, out ScreenState? incoming))
            {
                // A brand-new screen starts blank rather than inheriting the other camera's
                // windows: every one of them would be declared out of date on its first look
                // anyway, and a wrong window drawn for one frame is a wrong window on screen.
                _screenStates[screenId] = incoming = new ScreenState();
            }
            _screen = incoming;
            ForgetDepartedScreens();
        }

        // Every per-screen field of the pipeline lives in the active screen's state and is
        // reached through these: a ref property reads and writes the field in place, so a
        // `ref _x` or an `_x ??= ...` at the use sites is unchanged. There is no copying on a
        // screen switch any more, only the swap of _screen below.
        private ScreenState _screen = new();
        private ref Texture2D? _waterMask => ref _screen.WaterMask;
        private ref Texture2D? _waterSignedDistanceTexture => ref _screen.WaterSignedDistance;
        private ref Texture2D? _waterRealShoreDistanceTexture => ref _screen.WaterRealShoreDistance;
        private ref Texture2D? _waterPlungeChurnTexture => ref _screen.WaterPlungeChurn;
        private ref Texture2D? _waterMaskSpare => ref _screen.WaterMaskSpare;
        private ref Texture2D? _waterSignedDistanceSpare => ref _screen.WaterSignedDistanceSpare;
        private ref Texture2D? _waterRealShoreDistanceSpare => ref _screen.WaterRealShoreDistanceSpare;
        private ref Texture2D? _waterPlungeChurnSpare => ref _screen.WaterPlungeChurnSpare;
        private ref bool[]? _waterTilesInMask => ref _screen.WaterTilesInMask;
        private ref int _waterTilesVersion => ref _screen.WaterTilesVersion;
        private ref GameLocation? _lastWaterLocation => ref _screen.LastWaterLocation;
        private ref int _lastWaterTileX => ref _screen.LastWaterTileX;
        private ref int _lastWaterTileY => ref _screen.LastWaterTileY;
        private ref int _lastWaterBuildTick => ref _screen.LastWaterBuildTick;
        private ref int _lastWaterHookVersion => ref _screen.LastWaterHookVersion;
        private ref int _lastWaterLabelVersion => ref _screen.LastWaterLabelVersion;
        private ref int _lastWaterEpoch => ref _screen.LastWaterEpoch;
        private ref bool _hasWaterInMask => ref _screen.HasWaterInMask;
        private ref float _waterInMaskEase => ref _screen.WaterInMaskEase;
        private ref Vector2 _waterMaskTilesPerScreen => ref _screen.WaterMaskTilesPerScreen;
        private ref Vector2 _waterMaskWorldTileOffset => ref _screen.WaterMaskWorldTileOffset;
        private ref Vector2 _waterMaskPixelSize => ref _screen.WaterMaskPixelSize;
        private ref Texture2D? _occluderMask => ref _screen.OccluderMask;
        private ref Texture2D? _occluderMaskSpare => ref _screen.OccluderMaskSpare;
        private ref Color[]? _occluderMaskPixels => ref _screen.OccluderMaskPixels;
        private ref int _occluderTileX => ref _screen.OccluderTileX;
        private ref int _occluderTileY => ref _screen.OccluderTileY;
        private ref int _occluderCacheTick => ref _screen.OccluderCacheTick;
        private ref int _occluderInputsHash => ref _screen.OccluderInputsHash;
        private ref SurfaceMap? _occluderSurfaceMap => ref _screen.OccluderSurfaceMap;
        private ref int _occluderMaskBuildMode => ref _screen.OccluderMaskBuildMode;
        private ref Vector2 _occluderTilesPerScreen => ref _screen.OccluderTilesPerScreen;
        private ref Vector2 _occluderWorldTileOffset => ref _screen.OccluderWorldTileOffset;
        private ref Vector2 _occluderMaskSize => ref _screen.OccluderMaskSize;
        private ref Texture2D? _floodOccluderMask => ref _screen.FloodOccluderMask;
        private ref Color[]? _floodOccluderMaskPixels => ref _screen.FloodOccluderMaskPixels;
        private ref int _floodOccluderTileX => ref _screen.FloodOccluderTileX;
        private ref int _floodOccluderTileY => ref _screen.FloodOccluderTileY;
        private ref int _floodOccluderCacheTick => ref _screen.FloodOccluderCacheTick;
        private ref int _floodOccluderInputsHash => ref _screen.FloodOccluderInputsHash;
        private ref SurfaceMap? _floodOccluderSurfaceMap => ref _screen.FloodOccluderSurfaceMap;
        private ref Vector2 _floodOccluderMaskSize => ref _screen.FloodOccluderMaskSize;
        private ref Texture2D? _floodOccluderBaseTexture => ref _screen.FloodOccluderBase;
        private ref Texture2D? _floodOccluderBaseSpare => ref _screen.FloodOccluderBaseSpare;
        private ref RenderTarget2D?[] _floodOccluderSoft => ref _screen.FloodOccluderSoft;
        private ref bool _shadowsReady => ref _screen.ShadowsReady;
        private ref RenderTarget2D? _mirrorSceneCache => ref _screen.MirrorSceneCache;
        private ref GameLocation? _sceneCacheLocation => ref _screen.SceneCacheLocation;
        private ref int _sceneCacheAnchorX => ref _screen.SceneCacheAnchorX;
        private ref int _sceneCacheAnchorY => ref _screen.SceneCacheAnchorY;
        private ref int _sceneCacheBuiltTick => ref _screen.SceneCacheBuiltTick;
        private ref long _sceneAnimationStamp => ref _screen.SceneAnimStamp;
        private ref GameLocation? _fadeLocation => ref _screen.FadeLocation;
        private ref float _fadeWater => ref _screen.FadeWater;
        private ref float _fadeCloud => ref _screen.FadeCloud;
        private ref float _fadeLighting => ref _screen.FadeLighting;
        private ref float _fadeFlood => ref _screen.FadeFlood;
        private ref float _fadeTilt => ref _screen.FadeTilt;
        private ref float _tiltIndoorEase => ref _screen.TiltIndoorEase;
        private ref RenderTarget2D? _normalRenderTarget => ref _screen.NormalRenderTarget;
        private ref bool _normalPassReady => ref _screen.NormalPassReady;
        private ref Point _normalPassViewport => ref _screen.NormalPassViewport;
        private ref float _reliefEase => ref _screen.ReliefEase;
        private ref float _godRayAmount => ref _screen.GodRayAmount;
        private ref float _fogDayAmount => ref _screen.FogDayAmount;
        private ref float _fogMistAmount => ref _screen.FogMistAmount;
        private ref float _fadeBuildingShadow => ref _screen.FadeBuildingShadow;
        private ref float _toneMapEase => ref _screen.ToneMapEase;
        private ref float _vignetteEase => ref _screen.VignetteEase;
        private ref float _caEase => ref _screen.ChromaticAberrationEase;
        private ref Dictionary<int, LightFade> _lightRamp => ref _screen.LightRamp;
        private ref HashSet<int> _lightChosen => ref _screen.LightChosen;
        private ref GameLocation? _lightRampLocation => ref _screen.LightRampLocation;
        private ref RenderTarget2D? _cloudMaskKeep => ref _screen.CloudMaskKeep;
        private ref int _cloudMaskTick => ref _screen.CloudMaskTick;
        private ref Vector2 _cloudMaskTileOffset => ref _screen.CloudMaskTileOffset;
        private ref float _cloudMaskStrength => ref _screen.CloudMaskStrength;
        private ref float _shaftCloudEase => ref _screen.ShaftCloudEase;
        private ref RenderTarget2D? _luminanceRenderTarget => ref _screen.LuminanceTarget;
        private ref Color[]? _luminancePixels => ref _screen.LuminancePixels;
        private ref bool _isLuminancePrimed => ref _screen.LuminancePrimed;
        private ref GameLocation? _exposureMeterLocation => ref _screen.ExposureMeterLocation;
        private ref float _meteredExposure => ref _screen.MeteredExposure;
        private ref Vector3 _exposureEase => ref _screen.ExposureEase;
        private ref float _roomSaturationEase => ref _screen.RoomSaturationEase;
        private ref float _paneDaylightEase => ref _screen.PaneDaylightEase;
        private ref float _windowDaylightEase => ref _screen.WindowDaylightEase;
        private ref float _windowRoomLightEase => ref _screen.WindowRoomLightEase;
        private ref float _shimmerEase => ref _screen.ShimmerEase;
        private ref float _rainRingsEase => ref _screen.RainRingsEase;
        private ref float _fadeWet => ref _screen.FadeWet;
        private ref FloodLightmap _flood => ref _screen.Flood;
        private ref RadianceCascades _cascades => ref _screen.Cascades;
        private ref float _cascadeBlend => ref _screen.CascadeBlend;
        private ref bool _cascadesReady => ref _screen.CascadesReady;

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
