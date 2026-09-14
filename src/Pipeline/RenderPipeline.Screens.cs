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
            /// <summary>Counts up on every rebuild of the flood occluder mask, so a cache built
            /// from the mask can tell a rebuild that kept the window's origin from no rebuild.</summary>
            public int FloodOccluderGeneration;
            public int FloodOccluderGenerationTick = int.MinValue;

            // ---- the lamp shadow march window (RenderPipeline.MarchWindow.cs) ----
            public RenderTarget2D? MarchWindowA, MarchWindowB;
            public int MarchWindowTileX = int.MinValue, MarchWindowTileY = int.MinValue;
            public int MarchWindowOccluderGeneration = -1;
            public float MarchWindowStepCeiling = -1f, MarchWindowSoftness = -1f;
            public bool[] MarchChannelOn = new bool[FloodShadowedLights];
            public int[] MarchChannelId = new int[FloodShadowedLights];
            public Vector2[] MarchChannelTile = new Vector2[FloodShadowedLights];
            public float[] MarchChannelReach = new float[FloodShadowedLights];

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
            /// <summary>A reading has been written into this screen's probe and is waiting to be
            /// read back on the game's update tick; the tick it was written on decides when.</summary>
            public bool ExposureReadPending;
            public int ExposureWrittenTick;
            public GameLocation? ExposureMeterLocation;
            /// <summary>What the last reading said the exposure should be. The meter sets it a few
            /// times a second; the draw eases toward it every frame.</summary>
            public float ExposureTarget = 1f;
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
            // The sun shafts, the heat haze, the wading reflection, the displacement gate and the
            // window reflection follow this screen's scene too: whether it is under the sun, near a
            // heat source, standing in water, in an event, or somewhere the glass reflects. Shared,
            // two screens pulled them toward two targets on alternate frames.
            public float ShaftStrengthEase;
            public Vector2 ShaftDirectionEase = new(0f, 1f);
            public Vector3 ShaftColourEase;
            public float HeatHazeEase, WadingEase, DisplacementGateEase = 1f, WindowReflectEase;

            // ---- which lights this screen is showing, and how far each has faded ----
            public Dictionary<int, LightFade> LightRamp = new();
            public HashSet<int> LightChosen = new();
            public GameLocation? LightRampLocation;

            // ---- whether this screen's water mask holds water (ShadowRenderer.WaterOnScreen) ----
            // One static flag, written by whichever screen's water mask rebuilt last and read by the
            // other screen's shadow bakes and by the release of the water targets: a player walking
            // away from water released the targets of the one still standing beside it.
            public bool WaterOnScreen;

            // ---- the cloud mask the sun shafts read back a frame later ----
            // One shared keep between two screens meant screen 0's shafts read screen 1's sky,
            // drawn from a camera eighteen tiles away, every other frame: the beams jumped
            // between the two positions and read as flicker. Each screen keeps its own.
            public RenderTarget2D? CloudMaskKeep;
            public int CloudMaskTick = int.MinValue;
            public Vector2 CloudMaskTileOffset;
            public float CloudMaskStrength, ShaftCloudEase, SparkleCloudEase, SnowGlintEase, RainbowEase, WateredSparkleEase;
            // ---- the water's glitter following the sun (see water.fx SunAxis) ----
            public float GlitterPathEase;
            public Vector2 GlitterSunAxis = new(0f, 1f);

            // ---- answers about this screen's map, compared by instance ----
            // A farmhand screen holds its own copy of every location, map and surface grid, so a
            // single answer checked with ReferenceEquals was worked out again at every screen
            // switch, whether or not the two players stood in the same place.
            //
            // The wet ground's map cells ask every tile of the map twice through the map's
            // property lookup and upload a texture: in rain, a split screen paid that on both
            // screens every frame.
            public Texture2D? WetSuitabilityTexture;
            public Texture2D? WetSuitabilitySpare;
            public GameLocation? WetSuitabilityLocation;
            public Vector2 WetSuitabilityMapTiles = Vector2.One;
            public int WetSuitabilityObjectCount = -1, WetSuitabilityFurnitureCount = -1;
            public byte[]? WetGroundCells;
            public GameLocation? WetGroundCellsLocation;
            public SurfaceMap? WetGroundCellsSurface;
            public byte[]? WetSuitabilityCells;
            public byte[]? WetSuitabilityUploaded;
            /// <summary>Where this screen's exposure eases last snapped. Shared, it read as a warp
            /// at every screen switch, so in split screen the room's exposure, its window daylight
            /// and its saturation snapped every frame and never eased.</summary>
            public GameLocation? ExposureLocation;
            public GameLocation? LocationWaterLocation;
            public bool LocationHasWater;
            public GameLocation? PrewarmedLocation;
            /// <summary>Water body sizes, flood-filled over the whole map on every rebuild whose
            /// screen differed from the last one's.</summary>
            public SurfaceMap? BodySizeSourceSurfaceMap;
            public int BodySizeEpoch = -1;
            public int[]? BodyTileCounts;
            public int BodyGridWidth, BodyGridHeight;
            public GameLocation? BreathTileCacheLocation;
            public MapAnswerKey BreathTileCacheKey = new(-1, -1);
            public int BreathTileCacheWidth, BreathTileCacheHeight;
            public byte[] BreathTileFlow = System.Array.Empty<byte>();
            public byte[] BreathTileHot = System.Array.Empty<byte>();
            public byte[] BreathTileLava = System.Array.Empty<byte>();
            public bool[] BreathTileScanned = System.Array.Empty<bool>();

            // ---- the map-wide waterline anchor (RenderPipeline.Waterline.cs) ----
            // One anchor for the game held one location's shoreline. Two screens in two places
            // replaced each other's with every gather that finished, so each went on to gather its
            // whole map again: 109 whole-map gathers finished in one split-screen report, with both
            // players standing still, where one screen finishes one per visit.
            public WaterlineAnchor? WaterlineAnchorData;
            /// <summary>Consecutive frames this screen's window mask was fresh.</summary>
            public int WaterlineFreshFrameCount;
            /// <summary>One shot: do not retry a failed anchor for this location.</summary>
            public bool WaterlineAnchorFailedForLocation;
            public GameLocation? WaterlineFailedLocation;

            // ---- this screen's particles ----
            // The pool is in world pixels of one location, and it was one pool for the game. A
            // farmhand screen holds its own copy of every location, so each screen switch read as
            // a warp and emptied it, and the pool was stepped by two tick counters a thousand
            // apart, so the few steps it kept aged everything out at once: live=0 of 512 on a
            // split screen in Town, where one screen shows 54.
            public ParticleSystem? Particles;
            public GameLocation? ParticleLocation;
            /// <summary>Where this screen's player was on the previous simulated tick, for their
            /// speed. Taken from the position rather than from the game's movement-speed field,
            /// which is a number about how fast they COULD move and says nothing about which way.
            /// Shared, it was the distance between the two players.</summary>
            public Vector2 PreviousPlayerPosition;
            public bool PreviousPlayerPositionKnown;
            public Vector2 PlayerVelocity;

            // ---- the tilesheets this screen's map paints from (DrawnFromMapTileSheet) ----
            // One slot keyed on the map instance, and a farmhand screen holds its own copy of
            // every map, so it was rebuilt at every screen switch.
            public HashSet<Texture2D> MapTileSheetTextures = new();
            public HashSet<string> MapTileSheetNames = new(System.StringComparer.OrdinalIgnoreCase);
            public xTile.Map? MapTileSheetSource;
            public int MapTileSheetTick = -1000;
            public List<string> MapTileSheetImageSources = new();
            public Texture2D? LastSheetAsked;
            public bool LastSheetWasMapTile;

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
                MarchWindowA?.Dispose();
                MarchWindowB?.Dispose();
                Particles?.Dispose();
                WetSuitabilityTexture?.Dispose();
                WetSuitabilitySpare?.Dispose();
                Flood.Dispose();
                // The cascades hold two HalfVector4 targets, a lightmap and two emitter textures.
                // Only the flood grid was released here, so a co-op player leaving a split screen
                // left all five behind: the pipeline's own Dispose frees the cascades of the
                // ACTIVE screen and nobody freed anyone else's.
                Cascades.Dispose();
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
            ShadowRenderer.WaterOnScreen = incoming.WaterOnScreen;
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
        private ref int _floodOccluderGeneration => ref _screen.FloodOccluderGeneration;
        private ref int _floodOccluderGenerationTick => ref _screen.FloodOccluderGenerationTick;
        private ref RenderTarget2D? _marchWindowA => ref _screen.MarchWindowA;
        private ref RenderTarget2D? _marchWindowB => ref _screen.MarchWindowB;
        private ref int _marchWindowTileX => ref _screen.MarchWindowTileX;
        private ref int _marchWindowTileY => ref _screen.MarchWindowTileY;
        private ref int _marchWindowOccluderGeneration => ref _screen.MarchWindowOccluderGeneration;
        private ref float _marchWindowStepCeiling => ref _screen.MarchWindowStepCeiling;
        private ref float _marchWindowSoftness => ref _screen.MarchWindowSoftness;
        private bool[] _marchChannelOn => _screen.MarchChannelOn;
        private int[] _marchChannelId => _screen.MarchChannelId;
        private Vector2[] _marchChannelTile => _screen.MarchChannelTile;
        private float[] _marchChannelReach => _screen.MarchChannelReach;
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
        private ref float _shaftStrengthEase => ref _screen.ShaftStrengthEase;
        private ref Vector2 _shaftDirectionEase => ref _screen.ShaftDirectionEase;
        private ref Vector3 _shaftColourEase => ref _screen.ShaftColourEase;
        private ref float _heatHazeEase => ref _screen.HeatHazeEase;
        private ref float _wadingEase => ref _screen.WadingEase;
        private ref float _displacementGateEase => ref _screen.DisplacementGateEase;
        private ref float _windowReflectEase => ref _screen.WindowReflectEase;
        private ref Dictionary<int, LightFade> _lightRamp => ref _screen.LightRamp;
        private ref HashSet<int> _lightChosen => ref _screen.LightChosen;
        private ref GameLocation? _lightRampLocation => ref _screen.LightRampLocation;
        private ref RenderTarget2D? _cloudMaskKeep => ref _screen.CloudMaskKeep;
        private ref int _cloudMaskTick => ref _screen.CloudMaskTick;
        private ref Vector2 _cloudMaskTileOffset => ref _screen.CloudMaskTileOffset;
        private ref float _cloudMaskStrength => ref _screen.CloudMaskStrength;
        private ref float _shaftCloudEase => ref _screen.ShaftCloudEase;
        private ref float _sparkleCloudEase => ref _screen.SparkleCloudEase;
        private ref float _glitterPathEase => ref _screen.GlitterPathEase;
        private ref Vector2 _glitterSunAxis => ref _screen.GlitterSunAxis;
        private ref float _snowGlintEase => ref _screen.SnowGlintEase;
        private ref float _rainbowEase => ref _screen.RainbowEase;
        private ref float _wateredSparkleEase => ref _screen.WateredSparkleEase;
        private ref RenderTarget2D? _luminanceRenderTarget => ref _screen.LuminanceTarget;
        private ref Color[]? _luminancePixels => ref _screen.LuminancePixels;
        private ref bool _exposureReadPending => ref _screen.ExposureReadPending;
        private ref int _exposureWrittenTick => ref _screen.ExposureWrittenTick;
        private ref float _exposureTarget => ref _screen.ExposureTarget;
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
        private ref WaterlineAnchor? _waterlineAnchorData => ref _screen.WaterlineAnchorData;
        private ref int _waterlineFreshFrameCount => ref _screen.WaterlineFreshFrameCount;
        private ref bool _waterlineAnchorFailedForLocation => ref _screen.WaterlineAnchorFailedForLocation;
        private ref GameLocation? _waterlineFailedLocation => ref _screen.WaterlineFailedLocation;
        private ref ParticleSystem? _particles => ref _screen.Particles;
        private ref GameLocation? _particleLocation => ref _screen.ParticleLocation;
        private ref Vector2 _previousPlayerPosition => ref _screen.PreviousPlayerPosition;
        private ref bool _previousPlayerPositionKnown => ref _screen.PreviousPlayerPositionKnown;
        private ref Vector2 _playerVelocity => ref _screen.PlayerVelocity;
        private HashSet<Texture2D> _mapTileSheetTextures => _screen.MapTileSheetTextures;
        private HashSet<string> _mapTileSheetNames => _screen.MapTileSheetNames;
        private ref xTile.Map? _mapTileSheetSource => ref _screen.MapTileSheetSource;
        private ref int _mapTileSheetTick => ref _screen.MapTileSheetTick;
        private List<string> _mapTileSheetImageSources => _screen.MapTileSheetImageSources;
        private ref Texture2D? _lastSheetAsked => ref _screen.LastSheetAsked;
        private ref bool _lastSheetWasMapTile => ref _screen.LastSheetWasMapTile;

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
                ForgetEaseClock(id);
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
