using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Radiance Cascades: the flood GI lightmap computed on the GPU as light travelling, in place
    /// of the CPU sweep in <see cref="FloodLightmap"/>. Same inputs (the flood's own light seeds,
    /// the per-light occluder mask and its softened copies, the tile grid under them), same
    /// output contract (a texture floodlight.fx reads through LightMapTexture/MapOrigin/MapSize,
    /// stored x<see cref="FloodLightmap.TexScale"/>), so the two models are interchangeable at
    /// the composite and can cross-fade. What differs is what the map SAYS: shade is where rays
    /// met something, a lamp's spill round a corner is the rays that could still see it, and
    /// the map is two probes per tile instead of one cell.
    /// </summary>
    /// <remarks>
    /// The maths lives in cascades.fx; this class owns the textures, the rebuild gate and the
    /// pass order. The cascades are drawn top-down (the far cascade first) into two ping-pong
    /// targets, each pass reading the one above, and a final resolve averages cascade 0's rays
    /// into the lightmap. RGBA16F targets, because a lamp's seed sits above 1.0 and the sums
    /// need the headroom; a device that cannot make one refuses the model (see <see cref="Refused"/>)
    /// and the pipeline stays on the flood.
    /// </remarks>
    internal sealed class RadianceCascades
    {
        /// <summary>Four cascades reach 42.5 tiles, past anything on screen at the widest zoom.</summary>
        internal const int CascadeCount = 4;
        /// <summary>Two probes per tile at cascade 0.</summary>
        internal const float ProbeSpacingTiles = 0.5f;
        /// <summary>Cascade 0's rays cover the first half tile; each cascade above covers four times more.</summary>
        internal const float FirstIntervalTiles = 0.5f;
        /// <summary>Emitter seeds are stored x0.25 in an 8-bit texture: the flood's seeds reach ~2.1 outdoors.</summary>
        internal const float EmitterStorageScale = 0.25f;
        /// <summary>How much of the sky an occluder's face gives back to a ray that meets it within the
        /// near field. The flood seeds its solid cells at half the sky; the same half here, so a probe
        /// under a canopy lands where the flood put it and one beside a wall sits between.</summary>
        private const float OutdoorFaceShare = 0.5f;
        /// <summary>The same, in a room: walls are lit by the room's own light.</summary>
        private const float IndoorFaceShare = 0.5f;
        /// <summary>The cascade at whose end the sky arrives (its interval ends at 2.5 tiles): the sky is
        /// overhead, so only what stands within a couple of tiles can shade a spot from it. The cascades
        /// above carry lamps only. See the shader's SkyCascade note.</summary>
        private const int SkyCascade = 1;
        /// <summary>A seed's worth per tile of ray path through it. Tuned against the flood at Town 45,55
        /// at night: the cascades' lamp falls off as 1/distance where the flood's fell as 0.86 per cell,
        /// and this puts the two within reach of each other over the first three tiles.</summary>
        private const float EmitterGain = 2.5f;
        /// <summary>The probe grid is padded to a multiple of this so every cascade divides it evenly.</summary>
        private const int ProbeGridMultiple = 1 << (CascadeCount - 1);
        /// <summary>Frames between rebuilds when nothing the gate can see has changed (the flood's cadence).</summary>
        private const int FallbackCadenceTicks = 20;

        private readonly RenderTarget2D?[] _cascadeTargets = new RenderTarget2D?[2];
        private RenderTarget2D? _lightmap;
        /// <summary>Two, alternated per build: SetData on the texture the card may still be
        /// reading from the previous build waits for it, and a carried light rebuilds every
        /// 4 px of a walk. Same pixels either way - only the wait goes.</summary>
        private readonly Texture2D?[] _emitterTextures = new Texture2D?[2];
        private int _emitterTextureIndex;
        private Color[] _emitterPixels = Array.Empty<Color>();
        private SpriteBatch? _spriteBatch;

        private GameLocation? _lastLocation;
        private int _lastTileX = int.MinValue, _lastTileY = int.MinValue;
        private int _lastTilesW, _lastTilesH;
        private int _lastInputsHash, _lastOccluderTick = int.MinValue, _lastBuildTick = int.MinValue;
        private bool _loggedRefusal;

        /// <summary>True once this device refused an RGBA16F render target: the model is off for the
        /// session and the pipeline keeps the flood. Nothing pops, because the blend never leaves 0.</summary>
        internal bool Refused { get; private set; }
        internal string RefusedReason { get; private set; } = "";

        internal Texture2D? Texture => _lightmap;
        /// <summary>The seeded emitter grid of the last build, for radiance_dump.</summary>
        internal Texture2D? EmitterTexture => _emitterTextures[_emitterTextureIndex];
        /// <summary>World tile coordinate of the lightmap's top-left corner.</summary>
        internal Vector2 Origin;
        /// <summary>Lightmap extent in TILES (the texture holds two texels per tile).</summary>
        internal Vector2 MapSize;
        /// <summary>The last build, for the radiance_debug flood caption.</summary>
        internal string LastReport { get; private set; } = "not built";

        /// <summary>Build (or keep) the lightmap for the occluder mask's window. Returns false when
        /// there is no lightmap to read this frame.</summary>
        internal bool Build(GraphicsDevice device, IMonitor monitor, Effect effect, FloodLightmap flood, ModConfig config,
            Texture2D occluderMask, RenderTarget2D?[] occluderSoft, Texture2D occluderBase,
            int windowTileX, int windowTileY, int tilesW, int tilesH, int occluderBuildTick)
        {
            if (Refused)
                return false;
            GameLocation? location = Game1.currentLocation;
            if (location == null || occluderSoft.Length < CascadeCount - 1)
                return false;
            for (int level = 0; level < CascadeCount - 1; level++)
                if (occluderSoft[level] == null)
                    return false;

            // 4 px, not the flood's 16: a carried light rebuilds this map nearly every frame it
            // moves, so its bilinearly-split seed GLIDES instead of stepping a quarter tile at
            // a time. The build is GPU-side and did not register on the frame timer at 60 Hz.
            int inputsHash = FloodLightmap.HashLightInputs(location, positionPixelsPerHashStep: 4f);
            bool sameWindow = ReferenceEquals(location, _lastLocation)
                && windowTileX == _lastTileX && windowTileY == _lastTileY
                && tilesW == _lastTilesW && tilesH == _lastTilesH && _lightmap != null;
            if (FloodLightmap.RebuildMode == FloodLightmap.RebuildOverride.Freeze && sameWindow)
                return true;
            if (FloodLightmap.RebuildMode != FloodLightmap.RebuildOverride.Every && sameWindow
                && inputsHash == _lastInputsHash && occluderBuildTick == _lastOccluderTick
                && Game1.ticks - _lastBuildTick < FallbackCadenceTicks)
                return true;

            int probesX = RoundUp((int)Math.Ceiling(tilesW / ProbeSpacingTiles), ProbeGridMultiple);
            int probesY = RoundUp((int)Math.Ceiling(tilesH / ProbeSpacingTiles), ProbeGridMultiple);
            int cascadeW = probesX * 2, cascadeH = probesY * 2;
            if (!EnsureTargets(device, monitor, probesX, probesY, cascadeW, cascadeH))
                return false;

            _lastLocation = location;
            _lastTileX = windowTileX; _lastTileY = windowTileY;
            _lastTilesW = tilesW; _lastTilesH = tilesH;
            _lastInputsHash = inputsHash;
            _lastOccluderTick = occluderBuildTick;
            _lastBuildTick = Game1.ticks;

            // The emitters: the flood's own seed passes, run on an empty grid the size of the
            // occluder window, so a lamp, a window and the column it spills are worth exactly what
            // the flood would have seeded them at. The sky is NOT among them - it is what a ray
            // sees when nothing stops it, which is the whole difference between the two models.
            FloodLightmap.SceneSeed scene = FloodLightmap.DescribeScene(location, config);
            var window = new FloodLightmap.TileWindow(windowTileX, windowTileY, tilesW, tilesH);
            int count = tilesW * tilesH;
            if (_emitterPixels.Length < count)
                _emitterPixels = new Color[count];
            int seededCells = flood.SeedEmitters(location, scene, window, _emitterPixels, EmitterStorageScale);
            _emitterTextureIndex ^= 1;
            Texture2D? emitterTexture = _emitterTextures[_emitterTextureIndex];
            if (emitterTexture == null || emitterTexture.Width != tilesW || emitterTexture.Height != tilesH)
            {
                emitterTexture?.Dispose();
                emitterTexture = VramTally.Track(new Texture2D(device, tilesW, tilesH, false, SurfaceFormat.Color), "cascade emitters");
                _emitterTextures[_emitterTextureIndex] = emitterTexture;
            }
            emitterTexture.SetData(_emitterPixels, 0, count);

            Vector3 miss, hit, lift;
            if (scene.VanillaDark)
            {
                // Add-only, as the flood: nothing may come out darker than the flat seed the game
                // already darkened the room to, so a hit is worth the same as open air.
                miss = new Vector3(scene.NightSeed);
                hit = miss;
                lift = miss;
            }
            else
            {
                miss = scene.Sky;
                hit = scene.Sky * (scene.Outdoors ? OutdoorFaceShare : IndoorFaceShare);
                lift = scene.Sky * (scene.Outdoors ? 0.92f : 0.85f);
            }

            _spriteBatch ??= new SpriteBatch(device);
            var originTiles = new Vector2(windowTileX, windowTileY);
            var windowTiles = new Vector2(tilesW, tilesH);
            var probeGrid = new Vector2(probesX, probesY);
            var cascadeSize = new Vector2(cascadeW, cascadeH);
            var previous = device.GetRenderTargets();
            try
            {
                Set(effect, "WindowOriginTiles", originTiles);
                Set(effect, "WindowSizeTiles", windowTiles);
                Set(effect, "ProbeGrid0", probeGrid);
                Set(effect, "CascadeTexSize", cascadeSize);
                Set(effect, "ProbeSpacingTiles0", ProbeSpacingTiles);
                Set(effect, "CascadeCount", (float)CascadeCount);
                Set(effect, "MissRadiance", miss);
                Set(effect, "SkyCascade", (float)SkyCascade);
                Set(effect, "EmitterGain", EmitterGain);
                Set(effect, "EmitterTexScale", 1f / EmitterStorageScale);
                Set(effect, "OutputScale", FloodLightmap.TexScale);
                Set(effect, "LiftRadiance", lift);
                effect.Parameters["EmitterTexture"]?.SetValue(emitterTexture);
                effect.Parameters["BaseTexture"]?.SetValue(occluderBase);

                // Far cascade first, each one reading the one above it.
                for (int cascade = CascadeCount - 1; cascade >= 0; cascade--)
                {
                    RenderTarget2D target = _cascadeTargets[cascade & 1]!;
                    RenderTarget2D? upper = cascade == CascadeCount - 1 ? null : _cascadeTargets[(cascade + 1) & 1];
                    // Cascade 0 marches the full mask a texel at a time; each cascade above reads
                    // the next softer copy at its own texel, so a step never straddles a fence.
                    Texture2D occluderLevel = cascade == 0 ? occluderMask : occluderSoft[cascade - 1]!;
                    float stepTiles = (1 << cascade) / 8f;
                    Set(effect, "CascadeIndex", (float)cascade);
                    Set(effect, "IntervalStartTiles", IntervalStart(cascade));
                    Set(effect, "IntervalEndTiles", IntervalStart(cascade + 1));
                    Set(effect, "StepTiles", stepTiles);
                    // A face gives back light only within the sky's reach; beyond it an occluder
                    // neither adds nor removes sky, it only stands between a probe and a lamp.
                    Set(effect, "HitRadiance", cascade <= SkyCascade ? hit : Vector3.Zero);
                    effect.Parameters["UpperTexture"]?.SetValue(upper);
                    effect.Parameters["OccluderTexture"]?.SetValue(occluderLevel);
                    effect.CurrentTechnique = effect.Techniques["Cascade"];
                    DrawPass(device, effect, target, emitterTexture);
                }
                effect.Parameters["Cascade0Texture"]?.SetValue(_cascadeTargets[0]);
                // The resolve reads the FULL mask (not a soft level) to lift probes standing
                // inside a drawn silhouette. Cascade 0 left exactly that bound, but explicitly:
                effect.Parameters["OccluderTexture"]?.SetValue(occluderMask);
                effect.CurrentTechnique = effect.Techniques["Resolve"];
                DrawPass(device, effect, _lightmap!, emitterTexture);
            }
            finally
            {
                if (previous.Length > 0)
                    device.SetRenderTargets(previous);
                else
                    device.SetRenderTarget(null);
            }
            Origin = originTiles;
            MapSize = probeGrid * ProbeSpacingTiles;
            LastReport = $"cascades: {probesX}x{probesY} probes, {seededCells} emitter cells, "
                + (scene.VanillaDark ? $"add-only seed {scene.NightSeed:F2}" : $"sky ({miss.X:F2},{miss.Y:F2},{miss.Z:F2})");
            return true;
        }

        /// <summary>Where cascade <paramref name="cascade"/>'s rays begin, in tiles: 0, 0.5, 2.5, 10.5, 42.5.</summary>
        private static float IntervalStart(int cascade)
            => FirstIntervalTiles * ((1 << (2 * cascade)) - 1) / 3f;

        private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

        private static void Set(Effect effect, string name, float value) => effect.Parameters[name]?.SetValue(value);
        private static void Set(Effect effect, string name, Vector2 value) => effect.Parameters[name]?.SetValue(value);
        private static void Set(Effect effect, string name, Vector3 value) => effect.Parameters[name]?.SetValue(value);

        private void DrawPass(GraphicsDevice device, Effect effect, RenderTarget2D target, Texture2D source)
        {
            device.SetRenderTarget(target);
            _spriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, effect);
            _spriteBatch.Draw(source, new Rectangle(0, 0, target.Width, target.Height), Color.White);
            _spriteBatch.End();
        }

        private bool EnsureTargets(GraphicsDevice device, IMonitor monitor, int probesX, int probesY, int cascadeW, int cascadeH)
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    RenderTarget2D? target = _cascadeTargets[i];
                    if (target == null || target.Width != cascadeW || target.Height != cascadeH)
                    {
                        target?.Dispose();
                        _cascadeTargets[i] = VramTally.Track(new RenderTarget2D(device, cascadeW, cascadeH, false,
                            SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "radiance cascades");
                    }
                }
                if (_lightmap == null || _lightmap.Width != probesX || _lightmap.Height != probesY)
                {
                    _lightmap?.Dispose();
                    _lightmap = VramTally.Track(new RenderTarget2D(device, probesX, probesY, false,
                        SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "cascade lightmap");
                }
                return true;
            }
            catch (Exception ex)
            {
                Refused = true;
                RefusedReason = ex.Message;
                if (!_loggedRefusal)
                {
                    _loggedRefusal = true;
                    monitor.Log($"Radiance cascades need RGBA16F render targets and this device refused one ({ex.Message}); staying on the flood lightmap.", LogLevel.Warn);
                }
                Dispose();
                return false;
            }
        }

        internal void Dispose()
        {
            for (int i = 0; i < 2; i++) { _cascadeTargets[i]?.Dispose(); _cascadeTargets[i] = null; }
            _lightmap?.Dispose(); _lightmap = null;
            for (int i = 0; i < 2; i++) { _emitterTextures[i]?.Dispose(); _emitterTextures[i] = null; }
            _spriteBatch?.Dispose(); _spriteBatch = null;
            _lastLocation = null;
        }
    }
}
