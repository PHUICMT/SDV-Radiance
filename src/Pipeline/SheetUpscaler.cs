using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Sprites drawn from a sheet twice its size: a prefix on the game's SpriteBatch.Draw swaps the
    /// sheet for its Scale2x derivative (sheetscale.fx, kept per sheet by <see cref="SheetDerivedCache"/>),
    /// doubles the source rectangle and halves the scale, so the sprite lands exactly where it was
    /// with two texels where the game put one. What a texture upscaler mod does to every sheet at
    /// load, done here on the card, only for the sheets on screen, and only to the draw: the
    /// sheets themselves are untouched, so everything in this mod that reads sheet pixels back -
    /// the label fingerprints, the waterline map, the shadow bakes - reads what it always read.
    /// </summary>
    /// <remarks>
    /// Only the game's own batch is redirected. The mod's bakes and masks draw into targets sized
    /// for the original art, and a derivative of a derivative is refused by the cache. Off by
    /// default: it is a look, and it holds up to 384 MB of doubled sheets.
    /// </remarks>
    internal static class SheetUpscaler
    {
        /// <summary>Set per frame by the mod from the switch.</summary>
        internal static bool Enabled;
        /// <summary>The five art families, each with its own switch, set per frame from the config.
        /// Portraits, characters and items are named by their sheet's content path; the rest divides
        /// by WHEN the draw happens - the game draws menus, dialogue and the HUD in UI mode.</summary>
        internal static bool WorldEnabled = true;
        internal static bool CharactersEnabled = true;
        internal static bool PortraitsEnabled = true;
        internal static bool ItemsEnabled = true;
        internal static bool InterfaceEnabled;
        /// <summary>The art families, in the order the per-family dials and cache variants use.</summary>
        internal enum ArtFamily { World = 0, Characters = 1, Portraits = 2, Items = 3, Interface = 4 }
        internal const int FamilyCount = 5;
        /// <summary>Which half of the world's art the smoothing may touch. The world is drawn from
        /// two kinds of art that have nothing to do with each other: the map's own tilesheets, laid
        /// on a 16 pixel grid whose every cell is rounded on its own, and everything standing on
        /// them, which arrives one sprite at a time at whatever position it happens to be at. Both
        /// can read as plates and each needs a different fix, but a player looking at the screen can
        /// only say WHERE the plates are. This switch answers WHICH, in one keystroke, without
        /// re-making a single sheet. Console only, not saved.</summary>
        internal enum WorldArtPart { Both = 0, MapTilesOnly = 1, SpritesOnly = 2, Neither = 3 }
        /// <inheritdoc cref="WorldArtPart"/>
        internal static WorldArtPart SmoothedWorldPart = WorldArtPart.Both;
        /// <summary>0 keeps the game's own pixels, 1 is the full Scale2x rounding, one dial per art
        /// family (indexed by ArtFamily). Baked into the doubled sheets, whose cache variant is the
        /// family, so a change re-makes the sheets once instead of costing every frame.</summary>
        internal static readonly float[] SmoothnessByFamily = [1f, 1f, 1f, 1f, 1f];
        private static readonly float[] _bakedSmoothnessByFamily = [1f, 1f, 1f, 1f, 1f];
        /// <summary>The family whose soft sprite is being baked at this moment; the bake, which
        /// runs inside SoftSprites.For, reads that family's dial through it.</summary>
        private static ArtFamily _softBakeFamily;
        /// <summary>The map tiles round the one being baked, when it is a map tile (see
        /// MapTileNeighbours); null bakes the rectangle alone.</summary>
        private static MapTileNeighbours.Neighbourhood? _softBakeNeighbours;

        /// <summary>How many draws of each family took each of the three roads this frame: the
        /// four-times page, the doubled sheet, and the game's own pixels untouched.
        ///
        /// <para>Here because a scene that takes two of those roads at once is what the author
        /// called plates: art beside art, one of them softened and one of them not, and the eye
        /// reads the join as a seam. The counters say whether that is what is happening rather
        /// than leaving it to be argued from a screenshot.</para></summary>
        private static readonly int[] _softDraws = new int[FamilyCount];
        private static readonly int[] _doubledDraws = new int[FamilyCount];
        private static readonly int[] _untouchedDraws = new int[FamilyCount];
        private static readonly int[] _softDrawsLastFrame = new int[FamilyCount];
        private static readonly int[] _doubledDrawsLastFrame = new int[FamilyCount];
        private static readonly int[] _untouchedDrawsLastFrame = new int[FamilyCount];

        /// <summary>The world's three roads again, split by which half of the world's art took them:
        /// the map's tilesheets, and the sprites standing on them. A count for the whole family
        /// cannot tell a smoothed floor under a raw bush from a raw floor under a smoothed one, and
        /// those are different faults with different fixes.</summary>
        private const int RoadSoft = 0, RoadDoubled = 1, RoadRaw = 2, RoadCount = 3;
        private static readonly int[] _mapTileRoads = new int[RoadCount];
        private static readonly int[] _worldSpriteRoads = new int[RoadCount];
        private static readonly int[] _mapTileRoadsLastFrame = new int[RoadCount];
        private static readonly int[] _worldSpriteRoadsLastFrame = new int[RoadCount];

        /// <summary>The draw scales the WORLD's untouched draws came in at, in tenths, so the
        /// counter can say WHY they were left alone rather than only that they were. The world is
        /// the family the plates are on; the others would only make the line longer.</summary>
        private static readonly Dictionary<int, int> _untouchedWorldScales = [];
        private static readonly Dictionary<int, int> _untouchedWorldScalesLastFrame = [];
        private static readonly Dictionary<int, int> _softWorldScales = [];
        private static readonly Dictionary<int, int> _softWorldScalesLastFrame = [];

        /// <summary>Which sheets the world's untouched draws came from, so the counter names the
        /// art that is being left raw beside art that is not.</summary>
        private static readonly Dictionary<string, int> _untouchedWorldSheets = [];
        /// <summary>Map sheets drawn this frame through a batch that is not the game's own, which
        /// the smoothing never sees; for radiance_report.</summary>
        private static readonly Dictionary<string, int> _otherBatchMapSheets = [];
        /// <summary>Who drew through each of those batches, found once per batch from the stack: the
        /// first frames that are neither MonoGame, Harmony nor this upscaler.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpriteBatch, string> _otherBatchCallers = [];
        private static readonly List<string> _otherBatchCallerLines = [];

        private static void NameOtherBatch(SpriteBatch batch)
        {
            if (_otherBatchCallers.TryGetValue(batch, out _) || _otherBatchCallerLines.Count >= 12)
                return;
            var callers = new List<string>();
            foreach (var frame in new System.Diagnostics.StackTrace(2, false).GetFrames())
            {
                var method = frame.GetMethod();
                string type = method?.DeclaringType?.FullName ?? "";
                if (method == null || type.StartsWith("Microsoft.Xna", StringComparison.Ordinal) || type.StartsWith("HarmonyLib", StringComparison.Ordinal)
                    || type.Contains("SheetUpscaler") || type.Contains("DMD<") || method.Name.Contains("DMD<"))
                    continue;
                callers.Add($"{type}.{method.Name}");
                if (callers.Count == 4)
                    break;
            }
            string line = string.Join(" <- ", callers);
            _otherBatchCallers.AddOrUpdate(batch, line);
            _otherBatchCallerLines.Add(line);
        }
        private static readonly Dictionary<string, int> _untouchedWorldSheetsLastFrame = [];

        /// <summary>Whether this frame's draws are written into the scale and sheet tables. Those
        /// tables exist for one line of radiance_report, and filling them was a dictionary write,
        /// and for an unnamed sheet a new string, on every world draw the game made: measured at
        /// 0.3 to 0.4 ms a frame on a 3440-wide window, for a line nobody reads while playing.
        /// One frame in <see cref="NoteEveryFrames"/> says the same thing.</summary>
        private static bool _notingThisFrame = true;
        private static int _framesSinceNoted;
        private const int NoteEveryFrames = 60;

        private static void NoteSheet(Dictionary<string, int> into, Texture2D texture)
        {
            if (!_notingThisFrame)
                return;
            string name = string.IsNullOrEmpty(texture.Name) ? $"(unnamed {texture.Width}x{texture.Height})" : texture.Name;
            into[name] = into.TryGetValue(name, out int seen) ? seen + 1 : 1;
        }

        private static void NoteScale(Dictionary<int, int> into, float drawScale)
        {
            if (!_notingThisFrame)
                return;
            int tenths = (int)Math.Round(drawScale * 10f);
            into[tenths] = into.TryGetValue(tenths, out int seen) ? seen + 1 : 1;
        }

        /// <summary>One half of the world's art, as the three roads it took, with the word MIXED on
        /// a half that is being drawn two ways at once in the same picture.</summary>
        private static string DescribeRoads(int[] roads)
        {
            int soft = roads[RoadSoft], doubled = roads[RoadDoubled], raw = roads[RoadRaw];
            if (soft + doubled + raw == 0)
                return "none drawn";
            string counts = $"soft={soft} doubled={doubled} raw={raw}";
            int roadsTaken = (soft > 0 ? 1 : 0) + (doubled > 0 ? 1 : 0) + (raw > 0 ? 1 : 0);
            return roadsTaken > 1 ? counts + " MIXED" : counts;
        }

        private static string DescribeScales(Dictionary<int, int> scales)
        {
            if (scales.Count == 0)
                return "none";
            var parts = new List<string>();
            foreach (var pair in scales.OrderByDescending(pair => pair.Value))
                parts.Add($"x{pair.Key / 10f:0.0}:{pair.Value}");
            return string.Join(" ", parts);
        }
        /// <summary>Which look the smoothing has (see <see cref="SheetSmoothingStyle"/>), set per
        /// frame from the config; a change hands every held sheet back.</summary>
        internal static SheetSmoothingStyle Style = SheetSmoothingStyle.Scale2x;
        private static SheetSmoothingStyle _bakedStyle = SheetSmoothingStyle.Scale2x;
        /// <summary>Which rule makes the soft sheets. Baked, so a change re-makes them (BeginFrame).</summary>
        internal static SoftSmoothingKernel SoftKernel = SoftSmoothingKernel.Mmpx;
        private static SoftSmoothingKernel _bakedSoftKernel = SoftSmoothingKernel.Xbr;
        /// <summary>Each family's own rule, or SameAsAll. Baked, like the rule for all.</summary>
        internal static readonly FamilyKernelChoice[] KernelByFamily = new FamilyKernelChoice[FamilyCount];
        private static readonly FamilyKernelChoice[] _bakedKernelByFamily = new FamilyKernelChoice[FamilyCount];

        /// <summary>The rule the family being baked is made with.</summary>
        private static SoftSmoothingKernel KernelForBake() => _bakedKernelByFamily[(int)_softBakeFamily] switch
        {
            FamilyKernelChoice.Xbr => SoftSmoothingKernel.Xbr,
            FamilyKernelChoice.Mmpx => SoftSmoothingKernel.Mmpx,
            FamilyKernelChoice.MmpxEdgeGuarded => SoftSmoothingKernel.MmpxEdgeGuarded,
            FamilyKernelChoice.Epx => SoftSmoothingKernel.Epx,
            _ => _bakedSoftKernel,
        };
        /// <summary>Gradient smoothing after the kernel (Deposterize in sheetscale.fx), 0..1. Baked.</summary>
        internal static float SoftDeposterize;
        private static float _bakedSoftDeposterize;
        internal static GraphicsDevice? Device;
        internal static Effect? Effect;
        private const int Scale = 2;
        private const int SoftScale = 4;
        /// <summary>How wide the soft look's anti-aliased edge is, in source pixels: a quarter is one
        /// texel of the four-times sheet, which drawn at the game's 4x is one screen pixel of ramp.
        /// Baked, so a change re-makes the soft sheets (BeginFrame). radiance_softedge sets it live,
        /// for tuning by eye against a texture-upscaler capture.</summary>
        internal static float SoftEdgeSourcePixels = 0.25f;
        private static float _bakedSoftEdge = 0.25f;
        /// <summary>The tent that follows the kernel, in texels of the four-times sheet (see
        /// SheetSoften): what a texture upscaler gets by drawing a bigger sheet down with a linear
        /// filter. Three quarters was chosen beside a Clear Glasses capture of the same items, against
        /// a half (staircases still showing), one (softer than theirs) and one and a half. Baked;
        /// radiance_softblur sets it live for tuning by eye.</summary>
        internal static float SoftBlurTexels = 0.5f;
        /// <summary>How much further the soften reaches where the art alternates pixel by pixel
        /// (dither, speckled leaves), in texels of the four-times sheet; see DitherRadius in
        /// sheetscale.fx. Baked; radiance_softdither sets it live.</summary>
        internal static float SoftDitherTexels = 0f;
        /// <summary>radiance_softtint: every soft bake is written tinted, so a screenshot shows at a
        /// glance which art on screen came from a bake and which the game drew itself. A patch that
        /// stays its own colour beside tinted art is art that never reached the soft look.</summary>
        internal static Color BakeTint = Color.White;
        private static float _bakedSoftBlur = 0.5f;
        private static float _bakedSoftDither = 0f;
        /// <summary>The step in brightness along a tile line, on the shader's luminance-plus-alpha
        /// scale, that counts as a cut painted into the map rather than texture carrying on.</summary>
        internal static float SoftTileFeatherCut = 0.02f;
        /// <summary>Whether the soft sheets are sampled LINEARLY when the game draws them, whatever
        /// sampler the batch was begun with. This is the other half of the texture-upscaler look:
        /// their kernel rounds the outlines, and a linear read of the big sheet is what softens every
        /// colour boundary inside a sprite as well. The batch's own sampler (point, for the game's
        /// pixel art and lettering) is put back for every texture that is not a soft sheet, so
        /// nothing else in the batch goes soft. Done where MonoGame flushes a run of one texture,
        /// which is the only place a sampler can follow the texture rather than the batch.</summary>
        private static bool _linearForSoftSheets;
        /// <summary>Doubled sheets whose latest interface draw this frame was at a size the doubled
        /// texels cannot be read evenly with point sampling (below two screen pixels a doubled texel
        /// and not a whole number), so their run is read LINEARLY instead. The toolbar is the case: the
        /// game draws every item in it at 3.2 screen pixels a texel (Toolbar.draw, scaleSize 0.8,
        /// the held one 0.9), which left every tool and item there exactly as the game drew it
        /// while the same items on the ground were smoothed. Cleared each frame; a draw at an even
        /// size takes its sheet back out, so the inventory's 4x items stay point-read and crisp.</summary>
        private static readonly HashSet<Texture2D> _linearRuns = [];
        /// <summary>Two colours closer than this on the shader's luminance-plus-alpha scale (0 to
        /// 1.5) are the same colour to the edge rules: about a fifth of the way from black to white.</summary>
        private const float SoftEqualThreshold = 0.10f;
        /// <summary>Sheets up to 2048x2048 (16 MB) are doubled; a 4096 content-pack sheet would be 256 MB.</summary>
        internal static readonly SheetDerivedCache Cache = new("upscaled sheets", 384L * 1024 * 1024, 16L * 1024 * 1024, Scale, 4, "SheetScale",
            (effect, sheet, family) =>
            {
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / sheet.Width, 1f / sheet.Height));
                effect.Parameters["TargetSize"]?.SetValue(new Vector2(sheet.Width * Scale, sheet.Height * Scale));
                effect.Parameters["Smoothness"]?.SetValue(_bakedSmoothnessByFamily[family]);
                // A map tilesheet is doubled tile by tile: the kernel reads no texel outside the
                // 16-pixel cell it is rounding, so a floor tile keeps the edge it was painted with
                // rather than a corner borrowed from whatever tile sits beside it in the sheet.
                effect.Parameters["CellSize"]?.SetValue(DrawnAsMapTiles(sheet) ? MapTileSourcePixels : 0f);
            });
        /// <summary>The side of a map tile in source pixels; every Stardew tilesheet is on this grid.</summary>
        private const float MapTileSourcePixels = 16f;
        /// <summary>Whether a sheet is one of the map's own tilesheets, by the name the game gives them.</summary>
        private static bool DrawnAsMapTiles(Texture2D sheet)
        {
            string name = sheet.Name ?? "";
            return name.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Maps\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The same answer, remembered per sheet. A sheet's name never changes, and this is
        /// asked on every world draw the game makes, which is where the family lookup's own comment
        /// says two case-insensitive StartsWith calls a draw were worth 0.3 ms a frame.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, object> _mapTilesBySheet = [];
        private static bool IsMapTileSheet(Texture2D sheet)
        {
            if (_mapTilesBySheet.TryGetValue(sheet, out object? remembered) && remembered is bool known)
                return known;
            bool mapTiles = DrawnAsMapTiles(sheet);
            _mapTilesBySheet.AddOrUpdate(sheet, mapTiles);
            return mapTiles;
        }

        /// <summary>Whether <see cref="SmoothedWorldPart"/> lets this world sheet be smoothed.</summary>
        private static bool WorldPartAllows(Texture2D sheet) => SmoothedWorldPart switch
        {
            WorldArtPart.MapTilesOnly => IsMapTileSheet(sheet),
            WorldArtPart.SpritesOnly => !IsMapTileSheet(sheet),
            WorldArtPart.Neither => false,
            _ => true,
        };

        /// <summary>Count a world draw on the road it took, under the half of the world it came from.</summary>
        private static void NoteWorldRoad(ArtFamily family, Texture2D sheet, int road)
        {
            if (family == ArtFamily.World)
                (IsMapTileSheet(sheet) ? _mapTileRoads : _worldSpriteRoads)[road]++;
        }
        /// <summary>The soft look's sprites, each baked from its own rectangle alone (see
        /// <see cref="SoftSpriteCache"/> for the dark frame that baking whole sheets gave every
        /// sprite) onto pages kept per sheet, so a sheet's sprites still draw from one texture.
        /// Any sheet qualifies, since a sprite is small whatever its sheet is; eight bakes a
        /// frame, each two small passes.</summary>
        internal static readonly SoftSpriteCache SoftSprites = new("soft sprite pages", 192L * 1024 * 1024, 508, SoftScale, 64, SoftSpriteBake);
        internal static int PatchedOverloads { get; private set; }
        /// <summary>Draws redirected this frame, for the debug caption.</summary>
        internal static int RedirectedThisFrame;
        /// <summary>Set while this mod draws something of its own through the game's batch that
        /// does not want a smoothed sheet. Shadow silhouettes are the case: they are stamped in
        /// flat black and then blurred, so the rounded diagonal is thrown away a moment later,
        /// and the only thing left of it is four times the texels read. Measured at town-night
        /// with doubling on, the shadow draw was 0.021 ms before this pass existed and 0.322 ms
        /// after. The batch identity check below cannot tell our draws from the game's, because
        /// world sprites of ours are required to use Game1.spriteBatch, so the caller says so.</summary>
        internal static bool SuspendedForOwnDraw;

        private static bool Active => Enabled && Device != null && Effect != null;

        /// <summary>What a batch was begun with, per batcher, so a flush can put it back. Keyed by
        /// the batcher (SpriteBatch._batcher), which is the object the flush belongs to.</summary>
        private sealed class BatchSampling
        {
            public GraphicsDevice Device = null!;
            public SamplerState Sampler = SamplerState.PointClamp;
            public SamplerState? Applied;
            /// <summary>The batch's own sprite pass, put back after a run read through the steady
            /// read, and whether the batch has an effect of its own (then it is left alone).</summary>
            public EffectPass? SpritePass;
            public bool HasOwnEffect;
            public bool SteadyApplied;
        }
        private static AccessTools.FieldRef<SpriteBatch, EffectPass>? _spritePassOf;
        /// <summary>How the soft pages are read when drawn, as a share of a screen pixel the four
        /// reads spread over (see SoftPageRead in sheetscale.fx); 0 is the plain bilinear read.
        /// From ModConfig.SheetUpscaleSteadyRead every frame.</summary>
        internal static float SteadyReadSpread;
        private static EffectPass? _steadyReadPass;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BatchSampling> _samplingByBatcher = [];
        private static AccessTools.FieldRef<SpriteBatch, object>? _batcherOf;
        private static System.Reflection.FieldInfo? _batcherDevice;
        /// <summary>The batcher behind Game1.spriteBatch, so a flush of the game's batch can be
        /// told from a flush of one of this mod's own.</summary>
        private static object? _gameBatcher;
        /// <summary>Frames left in which every run of the game's Point batch is flushed under a
        /// fresh Point sampler instance, alternating between two, so MonoGame's per-texture memory
        /// of the last sampler applied (glLastSamplerState) never matches and it writes the
        /// texture's GL filter again. radiance_resample sets it. A sprite that comes back crisp was
        /// a texture whose GL filter had drifted to linear behind MonoGame's back.</summary>
        internal static int ResampleFramesLeft;
        private static readonly SamplerState _pointAgainA = new() { Filter = TextureFilter.Point, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, Name = "PointAgainA" };
        private static readonly SamplerState _pointAgainB = new() { Filter = TextureFilter.Point, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, Name = "PointAgainB" };
        private static bool _pointAgainToggle;

        private static void Begin_Postfix(SpriteBatch __instance, SpriteSortMode sortMode, SamplerState? samplerState, Effect? effect)
        {
            if (_batcherOf == null)
                return;
            object batcher = _batcherOf(__instance);
            if (batcher == null)
                return;
            BatchSampling sampling = _samplingByBatcher.GetValue(batcher, b => new BatchSampling
            {
                Device = (_batcherDevice?.GetValue(b) as GraphicsDevice) ?? __instance.GraphicsDevice,
            });
            // MonoGame's own default when none is given is LinearClamp; the game always passes one.
            sampling.Sampler = samplerState ?? SamplerState.LinearClamp;
            sampling.Applied = null;
            sampling.HasOwnEffect = effect != null;
            sampling.SteadyApplied = false;
            if (_spritePassOf != null)
                sampling.SpritePass ??= _spritePassOf(__instance);
            if (ReferenceEquals(__instance, Game1.spriteBatch))
            {
                _gameBatcher = batcher;
                if (SpriteDrawRecorder.InWorldStep)
                    SpriteDrawRecorder.NoteWorldBatchRestart(sortMode, samplerState);
            }
        }

        /// <summary>Before MonoGame draws one run of one texture: a soft sheet reads linearly,
        /// anything else reads as its batch asked.</summary>
        private static void FlushVertexArray_Prefix(object __instance, Texture texture)
        {
            FrameCost.Count(FrameCost.Counter.SpriteBatchFlushes);
            if (ReferenceEquals(__instance, _gameBatcher) && SpriteDrawRecorder.InWorldStep)
                FrameCost.Count(FrameCost.Counter.WorldSpriteBatchFlushes);
            // Leaving early when no feature wants this, and answering from a cached reference
            // instead of the table, were both tried on 2026-09-07 and both measured nothing: 9.64
            // ms against 9.63 with the soft look on, 8.90 against 8.89 with it off, four pairs
            // each. The research that proposed it guessed 0.15 to 0.3 ms. The table lookup is
            // cheaper than five thousand calls a frame makes it sound.
            if (!_samplingByBatcher.TryGetValue(__instance, out BatchSampling? sampling))
                return;
            if (_linearForSoftSheets || _linearRuns.Count > 0)
            {
                bool linear = texture is Texture2D sheet
                    && ((_linearForSoftSheets && SoftSprites.IsOwnOutput(sheet)) || _linearRuns.Contains(sheet));
                SamplerState wanted = linear ? SamplerState.LinearClamp : sampling.Sampler;
                if (!ReferenceEquals(sampling.Applied, wanted))
                {
                    sampling.Device.SamplerStates[0] = wanted;
                    sampling.Applied = wanted;
                }
                // The soft pages are read through the steady read; everything else through the
                // batch's own sprite pass, put back the moment a run of anything else comes. Only
                // on a batch with no effect of its own, whose flush draws with whatever pixel
                // shader is bound. Applying a pass binds its textures too, so the run's own is
                // bound again after it.
                bool steady = linear && SteadyReadSpread > 0.01f && !sampling.HasOwnEffect
                              && sampling.SpritePass != null && texture is Texture2D page && SoftSprites.IsOwnOutput(page);
                if (steady != sampling.SteadyApplied && (steady ? SteadyReadPass() : sampling.SpritePass) is EffectPass pass)
                {
                    if (steady)
                        Effect!.Parameters["SteadySpread"]?.SetValue(SteadyReadSpread);
                    pass.Apply();
                    sampling.Device.Textures[0] = texture;
                    sampling.SteadyApplied = steady;
                }
            }
            // radiance_resample: break MonoGame's per-texture memory so the Point filter is written again.
            if (ResampleFramesLeft > 0 && ReferenceEquals(__instance, _gameBatcher) && sampling.Sampler.Filter == TextureFilter.Point)
            {
                _pointAgainToggle = !_pointAgainToggle;
                sampling.Device.SamplerStates[0] = _pointAgainToggle ? _pointAgainA : _pointAgainB;
                sampling.Applied = null;
            }
            // After a radiance_drawsat question: what this run of the game's batch is read with.
            if (SpriteDrawRecorder.FlushWatchOpen && ReferenceEquals(__instance, _gameBatcher) && texture is Texture2D flushed)
                SpriteDrawRecorder.NoteFlush(flushed, sampling.Device.SamplerStates[0], sampling.Sampler);
        }

        private static EffectPass? SteadyReadPass()
        {
            if (_steadyReadPass == null && Effect != null)
                _steadyReadPass = Effect.Techniques["SoftPageRead"]?.Passes[0];
            return _steadyReadPass;
        }

        /// <summary>After the batcher has drawn its last run: a batch whose last run was a soft
        /// page leaves the steady read bound, and whatever draws next without applying a pass of
        /// its own would be read through it. The sprite pass goes back.</summary>
        private static void DrawBatch_Postfix(object __instance)
        {
            if (_samplingByBatcher.TryGetValue(__instance, out BatchSampling? sampling) && sampling.SteadyApplied)
            {
                sampling.SpritePass?.Apply();
                sampling.SteadyApplied = false;
            }
        }

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            // Re-entered by radiance_hooks on, after an off: count the overloads afresh.
            PatchedOverloads = 0;
            // The sampler that follows the texture (see _linearForSoftSheets): the batch's Begin
            // to learn what it asked for, and the batcher's flush to apply it per texture.
            try
            {
                Type? batcherType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatcher");
                var flush = batcherType == null ? null : AccessTools.Method(batcherType, "FlushVertexArray");
                var begin = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Begin));
                if (batcherType != null && flush != null && begin != null)
                {
                    _batcherOf = AccessTools.FieldRefAccess<SpriteBatch, object>("_batcher");
                    _batcherDevice = AccessTools.Field(batcherType, "_device");
                    harmony.Patch(begin, postfix: new HarmonyMethod(typeof(SheetUpscaler), nameof(Begin_Postfix)));
                    harmony.Patch(flush, prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(FlushVertexArray_Prefix)));
                    var drawBatch = AccessTools.Method(batcherType, "DrawBatch");
                    if (drawBatch != null)
                    {
                        _spritePassOf = AccessTools.FieldRefAccess<SpriteBatch, EffectPass>("_spritePass");
                        harmony.Patch(drawBatch, postfix: new HarmonyMethod(typeof(SheetUpscaler), nameof(DrawBatch_Postfix)));
                    }
                }
                else
                    monitor.Log("SpriteBatcher.FlushVertexArray not found; the soft look will be sampled as the batch is.", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                _batcherOf = null;
                monitor.Log($"Could not patch the sprite batcher's flush ({ex.GetType().Name}: {ex.Message}); the soft look will be sampled as the batch is.", LogLevel.Warn);
            }
            (Type[] signature, string handler)[] overloads =
            [
                (new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawVectorScale_Prefix)),
                (new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawFloatScale_Prefix)),
                (new[] { typeof(Texture2D), typeof(Rectangle), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawDestination_Prefix)),
            ];
            foreach ((Type[] signature, string handler) in overloads)
            {
                var draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), signature);
                if (draw == null)
                {
                    monitor.Log($"SpriteBatch.Draw overload for {handler} not found; sheet upscaling will miss those draws.", LogLevel.Warn);
                    continue;
                }
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(SheetUpscaler), handler));
                PatchedOverloads++;
            }
            FailureMonitor = monitor;
            InstallFarmerDrawWatch(harmony, monitor);
            InstallCursorDrawWatch(harmony, monitor);
            // The two four-argument overloads have bodies of their own in this MonoGame and are
            // not smoothed; watched only, so radiance_drawsat can name a draw that came through them.
            if (!_shortOverloadsWatched)
            {
                var shortDestination = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), [typeof(Texture2D), typeof(Rectangle), typeof(Rectangle?), typeof(Color)]);
                var shortPosition = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), [typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color)]);
                if (shortDestination != null)
                    harmony.Patch(shortDestination, prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(DrawShortDestination_Watch)));
                if (shortPosition != null)
                    harmony.Patch(shortPosition, prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(DrawShortPosition_Watch)));
                var wholeDestination = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), [typeof(Texture2D), typeof(Rectangle), typeof(Color)]);
                var wholePosition = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), [typeof(Texture2D), typeof(Vector2), typeof(Color)]);
                if (wholeDestination != null)
                    harmony.Patch(wholeDestination, prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(DrawWholeDestination_Watch)));
                if (wholePosition != null)
                    harmony.Patch(wholePosition, prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(DrawWholePosition_Watch)));
                _shortOverloadsWatched = true;
            }
        }

        /// <summary>How deep the game is inside one of FarmerRenderer's draws. The player is
        /// composed at draw time out of a body the game builds itself (a runtime texture named
        /// "@FarmerRenderer.baseTexture"), clothes, hair and whatever an outfit mod draws inside the
        /// same call; only the hair and clothes sheets carry a Characters/ name, so the rest used
        /// to be read as world art and followed the World dial. Anything the world family would
        /// have taken while this is above zero is the player, and goes with the characters.</summary>
        private static int _farmerDrawDepth;
        private static bool _farmerDrawWatched;

        private static void InstallFarmerDrawWatch(Harmony harmony, IMonitor monitor)
        {
            if (_farmerDrawWatched)
                return;
            int patched = 0;
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(FarmerRenderer)))
            {
                if (method.IsAbstract || method.IsStatic
                    || method.Name is not ("draw" or "drawHairAndAccesories" or "drawMiniPortrat"))
                    continue;
                try
                {
                    // First, so an outfit mod's own prefix, which draws the outfit, already runs inside.
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(FarmerDraw_Prefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(SheetUpscaler), nameof(FarmerDraw_Finalizer)));
                    patched++;
                }
                catch (Exception ex)
                {
                    monitor.Log($"Could not watch FarmerRenderer.{method.Name} ({ex.GetType().Name}: {ex.Message}); "
                        + "the player's smoothing follows the World dial.", LogLevel.Warn);
                }
            }
            _farmerDrawWatched = patched > 0;
        }

        /// <summary>How deep the game is inside Game1.drawMouseCursor. When the interface scale
        /// differs from the zoom the game leaves ui mode to draw the cursor (and the placement
        /// tile under it), so the cursor off the Cursors sheet read as world art and was rounded
        /// with the World dial: a smeared hand where the player points. Everything drawn in there
        /// is the interface, whatever mode the batch is in.</summary>
        private static int _cursorDrawDepth;
        private static bool _cursorDrawWatched;

        private static void InstallCursorDrawWatch(Harmony harmony, IMonitor monitor)
        {
            if (_cursorDrawWatched)
                return;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.drawMouseCursor)),
                    prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(CursorDraw_Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SheetUpscaler), nameof(CursorDraw_Finalizer)));
                _cursorDrawWatched = true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not watch Game1.drawMouseCursor ({ex.GetType().Name}: {ex.Message}); "
                    + "the cursor may be smoothed with the world when the interface scale differs from the zoom.", LogLevel.Warn);
            }
        }

        private static void CursorDraw_Prefix() => _cursorDrawDepth++;

        private static Exception? CursorDraw_Finalizer(Exception? __exception)
        {
            if (_cursorDrawDepth > 0)
                _cursorDrawDepth--;
            return __exception;
        }

        private static void FarmerDraw_Prefix() => _farmerDrawDepth++;

        private static Exception? FarmerDraw_Finalizer(Exception? __exception)
        {
            if (_farmerDrawDepth > 0)
                _farmerDrawDepth--;
            return __exception;
        }

        /// <summary>One sprite of the soft look, baked: the xBR kernel (see SheetXbr in sheetscale.fx)
        /// over its own rectangle of the sheet to four times its texels into a scratch of the
        /// sprite's size, then the tent from the scratch into the sprite's place on its sheet's
        /// page. The place is the sprite plus its gutter (see SoftSpriteCache.Gutter): the scratch
        /// is read past its own edges with a clamped sampler, so the gutter is the sprite's edge
        /// texels repeated, which is what a target of its own would have shown a linear read.
        /// Baked once per (sheet, rectangle) and kept.</summary>
        /// <summary>Every soft sprite forgotten, and the map neighbourhoods their keys were numbered
        /// by with them: a number reused for other surroundings must never find an old bake.</summary>
        internal static void ClearSoftSprites()
        {
            SoftSprites.Clear();
            MapTileNeighbours.Clear();
            DisposeScratchTargets();
        }

        /// <summary>The margin, in source pixels, drawn round a map tile from its neighbours before
        /// the kernel reads it: xBR reads two texels past the texel it writes.</summary>
        private const int NeighbourMargin = 2;
        private static bool _tileBakeFailureLogged;
        internal static IMonitor? FailureMonitor;
        /// <summary>How many source pixels of neighbour the kernel writes round a map tile: the
        /// tile-line blend reads this far past the line (see SheetTileSoften and
        /// <see cref="SoftTileFeatherTexels"/>), and the page's gutter is taken from it.</summary>
        private const int NeighbourRing = 3;
        /// <summary>How far either side of a map tile's own edge a straight cut painted into the
        /// map is blended out, in texels of the four-times sheet (8 is two source pixels); 0 turns
        /// it off. Baked; radiance_softfeather sets it live.</summary>
        internal static float SoftTileFeatherTexels = 8f;
        private static float _bakedSoftTileFeather = 8f;

        /// <summary>Scratch targets a bake draws through, kept by size and use and handed out again.
        /// Every bake used to make its own two targets and throw them away, and a target made is a
        /// texture and a framebuffer the driver builds from nothing: that was most of the 0.2 to
        /// 0.57 ms a bake measured on 23/9, which let a 1.5 ms frame bake three to seven tiles and
        /// left eleven thousand draws sharp while a view filled in. Map tiles are nearly all one
        /// size, so a handful of these serve almost every bake.</summary>
        private static readonly Dictionary<(int Width, int Height, int Use), RenderTarget2D> _scratchTargets = [];
        /// <summary>More sizes than this and the pool starts again: a scene of odd sprite sizes
        /// must not hold a target for each of them for good.</summary>
        private const int MostScratchTargets = 48;
        private const int ScratchForNeighbours = 0, ScratchForKernel = 1, ScratchForHalfway = 2;

        /// <summary>A cleared scratch target of exactly this size, bound as the render target.</summary>
        private static RenderTarget2D BindScratch(GraphicsDevice device, int width, int height, int use)
        {
            if (!_scratchTargets.TryGetValue((width, height, use), out RenderTarget2D? target)
                || target.IsDisposed || target.IsContentLost)
            {
                if (target != null && !target.IsDisposed)
                    target.Dispose();
                if (_scratchTargets.Count >= MostScratchTargets)
                    DisposeScratchTargets();
                // PreserveContents: it is cleared here and then drawn several times.
                target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color,
                    DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                _scratchTargets[(width, height, use)] = target;
            }
            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);
            return target;
        }

        private static void DisposeScratchTargets()
        {
            foreach (RenderTarget2D target in _scratchTargets.Values)
                target.Dispose();
            _scratchTargets.Clear();
        }

        /// <summary>
        /// The soft look's kernel: <paramref name="written"/> of <paramref name="source"/> at four
        /// times its texels into a scratch target, reading as far as <paramref name="readable"/>
        /// (past the written rectangle only when <paramref name="readsPastWritten"/>, which is a map
        /// tile drawn with its neighbours round it; a sprite reads only itself).
        /// </summary>
        /// <remarks>xBR writes any scale in one pass. MMPX and EPX double, so they run twice: the
        /// whole readable rectangle at two times into a halfway target, then the written part of
        /// that at two times again, reading the halfway target to its edges as the first pass read
        /// the source. The smoothness dial is applied in both passes, so 0 is still the art's own
        /// pixels and 1 the full rule.</remarks>
        private static RenderTarget2D RunSoftKernel(GraphicsDevice device, SpriteBatch batch, Effect effect, Texture2D source,
            Rectangle readable, bool readsPastWritten, Rectangle written)
        {
            int width = written.Width * SoftScale, height = written.Height * SoftScale;
            effect.Parameters["Smoothness"]?.SetValue(_bakedSmoothnessByFamily[(int)_softBakeFamily]);
            SoftSmoothingKernel kernel = KernelForBake();
            if (kernel == SoftSmoothingKernel.Xbr)
            {
                RenderTarget2D output = BindScratch(device, width, height, ScratchForKernel);
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / source.Width, 1f / source.Height));
                effect.Parameters["TargetSize"]?.SetValue(new Vector2(width, height));
                effect.Parameters["SourceRect"]?.SetValue(new Vector4(written.X, written.Y, written.Width, written.Height));
                effect.Parameters["ReadRect"]?.SetValue(readsPastWritten ? new Vector4(readable.X, readable.Y, readable.Width, readable.Height) : Vector4.Zero);
                effect.Parameters["EdgeSoftness"]?.SetValue(SoftEdgeSourcePixels);
                effect.Parameters["EqualThreshold"]?.SetValue(SoftEqualThreshold);
                effect.CurrentTechnique = effect.Techniques["SheetXbr"];
                DrawKernelPass(batch, effect, source, width, height);
                effect.Parameters["ReadRect"]?.SetValue(Vector4.Zero);
                return output;
            }

            string technique = kernel == SoftSmoothingKernel.Epx ? "SheetEpx" : "SheetMmpx";
            effect.Parameters["EdgeGuard"]?.SetValue(kernel == SoftSmoothingKernel.MmpxEdgeGuarded ? 1f : 0f);
            effect.CurrentTechnique = effect.Techniques[technique];

            int halfwayWidth = readable.Width * 2, halfwayHeight = readable.Height * 2;
            RenderTarget2D halfway = BindScratch(device, halfwayWidth, halfwayHeight, ScratchForHalfway);
            effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / source.Width, 1f / source.Height));
            effect.Parameters["TargetSize"]?.SetValue(new Vector2(halfwayWidth, halfwayHeight));
            effect.Parameters["SourceRect"]?.SetValue(new Vector4(readable.X, readable.Y, readable.Width, readable.Height));
            effect.Parameters["ReadRect"]?.SetValue(Vector4.Zero);
            DrawKernelPass(batch, effect, source, halfwayWidth, halfwayHeight);

            RenderTarget2D finished = BindScratch(device, width, height, ScratchForKernel);
            effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / halfwayWidth, 1f / halfwayHeight));
            effect.Parameters["TargetSize"]?.SetValue(new Vector2(width, height));
            effect.Parameters["SourceRect"]?.SetValue(new Vector4((written.X - readable.X) * 2, (written.Y - readable.Y) * 2,
                written.Width * 2, written.Height * 2));
            effect.Parameters["ReadRect"]?.SetValue(new Vector4(0, 0, halfwayWidth, halfwayHeight));
            DrawKernelPass(batch, effect, halfway, width, height);
            effect.Parameters["ReadRect"]?.SetValue(Vector4.Zero);
            return finished;
        }

        private static void DrawKernelPass(SpriteBatch batch, Effect effect, Texture2D source, int width, int height)
        {
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, effect);
            batch.Draw(source, new Rectangle(0, 0, width, height), Color.White);
            batch.End();
        }

        private static bool SoftSpriteBake(GraphicsDevice device, SpriteBatch batch, Effect effect, Texture2D sheet, Rectangle rect,
            RenderTarget2D page, Rectangle placeOnPage)
        {
            if (_softBakeNeighbours != null)
                return SoftTileBake(device, batch, effect, sheet, rect, _softBakeNeighbours, page, placeOnPage);
            try
            {
                int width = rect.Width * SoftScale, height = rect.Height * SoftScale;
                RenderTarget2D kernelOutput = RunSoftKernel(device, batch, effect, sheet, rect, readsPastWritten: false, rect);
                // Onto the page, gutter included: the source rectangle reaches past the scratch
                // by the gutter on every side and the clamped read repeats the edge into it.
                // Opaque, no clear: the page keeps every other sprite on it (PreserveContents).
                // Opaque, no clear: the page keeps every other sprite on it (PreserveContents).
                bool soften = SoftBlurTexels > 0.01f;
                int gutter = SoftSpriteCache.Gutter;
                var readPastEdges = new Rectangle(-gutter, -gutter, width + 2 * gutter, height + 2 * gutter);
                device.SetRenderTarget(page);
                Effect? tent = null;
                if (soften)
                {
                    effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / width, 1f / height));
                    effect.Parameters["SoftRadius"]?.SetValue(SoftBlurTexels);
                    effect.Parameters["Deposterize"]?.SetValue(_bakedSoftDeposterize);
                    effect.Parameters["DitherRadius"]?.SetValue(SoftDitherTexels);
                    effect.Parameters["SourcePixelTexels"]?.SetValue((float)SoftScale);
                    effect.CurrentTechnique = effect.Techniques["SheetSoften"];
                    tent = effect;
                }
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone, tent);
                batch.Draw(kernelOutput, placeOnPage, readPastEdges, BakeTint);
                batch.End();
                return true;
            }
            catch
            {
                try { batch.End(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// A map tile of the soft look, baked with its neighbours: the tile and a
        /// <see cref="NeighbourMargin"/> of each of the eight tiles round it are drawn into a small
        /// scratch as the map has them, the kernel writes the tile and one texel more from that,
        /// and the soften writes the tile onto the page with its gutter taken from the kernel's
        /// extra texel, which is the neighbour's art rather than the tile's own edge repeated.
        /// </summary>
        /// <remarks>An empty neighbour stays transparent in the scratch, which is what the layer
        /// holds there: the art does end at that edge, and the soften treats it as the silhouette
        /// edge it is.</remarks>
        /// <summary>The tile's edge texels stretched over the margin on one side (or its corner texel
        /// over one corner), as a clamped read would have repeated them.</summary>
        private static void RepeatEdgeInto(SpriteBatch batch, Texture2D sheet, Rectangle rect, Point side, int margin)
        {
            int sourceX = side.X < 0 ? rect.X : side.X > 0 ? rect.Right - 1 : rect.X;
            int sourceY = side.Y < 0 ? rect.Y : side.Y > 0 ? rect.Bottom - 1 : rect.Y;
            int sourceWidth = side.X == 0 ? rect.Width : 1, sourceHeight = side.Y == 0 ? rect.Height : 1;
            int destinationX = side.X < 0 ? 0 : side.X > 0 ? margin + rect.Width : margin;
            int destinationY = side.Y < 0 ? 0 : side.Y > 0 ? margin + rect.Height : margin;
            int destinationWidth = side.X == 0 ? rect.Width : margin, destinationHeight = side.Y == 0 ? rect.Height : margin;
            batch.Draw(sheet, new Rectangle(destinationX, destinationY, destinationWidth, destinationHeight),
                new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight), Color.White);
        }

        private static bool SoftTileBake(GraphicsDevice device, SpriteBatch batch, Effect effect, Texture2D sheet, Rectangle rect,
            MapTileNeighbours.Neighbourhood neighbours, RenderTarget2D page, Rectangle placeOnPage)
        {
            try
            {
                int margin = NeighbourMargin + NeighbourRing;
                int aroundWidth = rect.Width + 2 * margin, aroundHeight = rect.Height + 2 * margin;
                RenderTarget2D around = BindScratch(device, aroundWidth, aroundHeight, ScratchForNeighbours);
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone);
                batch.Draw(sheet, new Rectangle(margin, margin, rect.Width, rect.Height), rect, Color.White);
                for (int i = 0; i < 8; i++)
                {
                    Texture2D? besideSheet = neighbours.Sheets[i];
                    Rectangle besideSource = neighbours.Sources[i];
                    Point offset = MapTileNeighbours.Offsets[i];
                    if (neighbours.Hidden[i] && besideSheet != null && !besideSheet.IsDisposed)
                    {
                        RepeatEdgeInto(batch, sheet, rect, offset, margin);
                        continue;
                    }
                    if (besideSheet == null || besideSheet.IsDisposed)
                    {
                        // Nothing there on this layer or on any layer that carries its art on
                        // (MapTileNeighbours looks at those), so the art really ends at this edge
                        // and the margin stays transparent: the kernel rounds the outline there as it
                        // rounds a sprite's. Repeating the tile's own edge into it, which was tried
                        // while the sibling layers were not yet looked at, drew that edge out as a
                        // straight cut, the faint square lines along the bottom of a bush.
                        if (besideSheet != null)
                            RepeatEdgeInto(batch, sheet, rect, offset, margin);
                        continue;
                    }
                    // The neighbour's place in the tile's own coordinates, cut to the margin.
                    int left = offset.X * rect.Width, top = offset.Y * rect.Height;
                    int fromX = Math.Max(left, -margin), toX = Math.Min(left + besideSource.Width, rect.Width + margin);
                    int fromY = Math.Max(top, -margin), toY = Math.Min(top + besideSource.Height, rect.Height + margin);
                    if (toX <= fromX || toY <= fromY)
                        continue;
                    var from = new Rectangle(besideSource.X + fromX - left, besideSource.Y + fromY - top, toX - fromX, toY - fromY);
                    batch.Draw(besideSheet, new Rectangle(fromX + margin, fromY + margin, toX - fromX, toY - fromY), from, Color.White);
                }
                batch.End();

                // The kernel: the tile and one texel round it, at four times, reading into the margin.
                int writtenWidth = rect.Width + 2 * NeighbourRing, writtenHeight = rect.Height + 2 * NeighbourRing;
                int width = writtenWidth * SoftScale, height = writtenHeight * SoftScale;
                RenderTarget2D kernelOutput = RunSoftKernel(device, batch, effect, around, new Rectangle(0, 0, aroundWidth, aroundHeight),
                    readsPastWritten: true, new Rectangle(margin - NeighbourRing, margin - NeighbourRing, writtenWidth, writtenHeight));

                // Onto the page: the tile's four-times texels plus the gutter, which here is the
                // kernel's extra texel of neighbour, not a repeat of the tile's edge.
                int gutter = SoftSpriteCache.Gutter;
                int ringTexels = NeighbourRing * SoftScale;
                var tileWithGutter = new Rectangle(ringTexels - gutter, ringTexels - gutter,
                    rect.Width * SoftScale + 2 * gutter, rect.Height * SoftScale + 2 * gutter);
                device.SetRenderTarget(page);
                Effect? tent = null;
                if (SoftBlurTexels > 0.01f || SoftTileFeatherTexels > 0.5f)
                {
                    effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / width, 1f / height));
                    effect.Parameters["SoftRadius"]?.SetValue(SoftBlurTexels);
                    effect.Parameters["Deposterize"]?.SetValue(_bakedSoftDeposterize);
                    effect.Parameters["DitherRadius"]?.SetValue(SoftDitherTexels);
                    effect.Parameters["SourcePixelTexels"]?.SetValue((float)SoftScale);
                    effect.Parameters["TileRectUv"]?.SetValue(new Vector4(ringTexels / (float)width, ringTexels / (float)height,
                        rect.Width * SoftScale / (float)width, rect.Height * SoftScale / (float)height));
                    // The ground blends a painted cut into its neighbour; an upper layer only fades
                    // a see-through edge with nothing past it, since its opaque edges are outlines.
                    effect.Parameters["FeatherTexels"]?.SetValue(Math.Min(SoftTileFeatherTexels, ringTexels - 2f));
                    effect.Parameters["FadeSeeThroughEdges"]?.SetValue(neighbours.OnGround ? 0f : 1f);
                    // Offsets order: row above, then left and right, then the row below.
                    // On an upper layer: which sides have a tile of its own past them. On the ground:
                    // which sides may be blended across, all but a side hidden under the game's water.
                    effect.Parameters["OwnSides"]?.SetValue(neighbours.OnGround
                        ? new Vector4(neighbours.Hidden[3] ? 0f : 1f, neighbours.Hidden[4] ? 0f : 1f,
                            neighbours.Hidden[1] ? 0f : 1f, neighbours.Hidden[6] ? 0f : 1f)
                        : new Vector4(neighbours.OwnLayer[3] ? 1f : 0f, neighbours.OwnLayer[4] ? 1f : 0f,
                            neighbours.OwnLayer[1] ? 1f : 0f, neighbours.OwnLayer[6] ? 1f : 0f));
                    effect.Parameters["FeatherCut"]?.SetValue(SoftTileFeatherCut);
                    effect.CurrentTechnique = effect.Techniques["SheetTileSoften"];
                    tent = effect;
                }
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone, tent);
                batch.Draw(kernelOutput, placeOnPage, tileWithGutter, BakeTint);
                batch.End();
                return true;
            }
            catch (Exception ex)
            {
                try { batch.End(); } catch { }
                effect.Parameters["ReadRect"]?.SetValue(Vector4.Zero);
                // Said once: a bake that fails for every tile leaves the whole map as the game drew
                // it, which looks exactly like the smoothing being off.
                if (!_tileBakeFailureLogged)
                {
                    _tileBakeFailureLogged = true;
                    FailureMonitor?.Log($"A soft map-tile bake failed ({ex.GetType().Name}: {ex.Message}); those tiles draw as the game drew them.", LogLevel.Warn);
                }
                return false;
            }
        }

        /// <summary>Whether a draw at this many screen pixels per DERIVED texel is worth redirecting.
        /// The doubled sheet wants two, or a whole number (see MinimumDoubledScale). A soft sprite
        /// is already soft, so a dropped row here and there is invisible: one pixel a texel is
        /// enough, which keeps the hover pulse (4x to 4.4x) on the soft sprite the whole way.</summary>
        private static bool DrawnLargeEnough(float pixelsPerTexel, bool soft)
        {
            if (soft)
                return pixelsPerTexel >= 1f - 0.001f;
            if (pixelsPerTexel >= MinimumDoubledScale - 0.001f)
                return true;
            return pixelsPerTexel >= 1f - 0.001f && Math.Abs(pixelsPerTexel - (float)Math.Round(pixelsPerTexel)) < 0.001f;
        }

        /// <summary>The derived texture for this draw, the source rectangle to read it with, and how
        /// many texels it holds per texel of the original; null leaves the draw alone. The soft
        /// look answers with a sprite of its own (the whole target is the sprite); the doubled look
        /// answers with the doubled sheet and the rectangle doubled.</summary>
        private static Texture2D? Derived(SpriteBatch batch, Texture2D texture, Rectangle? sourceRectangle, float drawScale,
            out Rectangle derivedSource, out int factor)
        {
            factor = 1;
            derivedSource = default;
            if (!Active || SuspendedForOwnDraw || texture == null || texture.IsDisposed)
            {
                MapTileNeighbours.NoteOutcome(!Active ? "smoothing off" : SuspendedForOwnDraw ? "held back for one of this mod's own draws" : "no texture");
                return null;
            }
            if (!ReferenceEquals(batch, Game1.spriteBatch))
            {
                // Art the game draws through a batch of its own or another mod's: never smoothed,
                // and until this counter it was invisible, since every other counter here only
                // sees the game's batch. Map sheets only, which is what shows as a square.
                MapTileNeighbours.NoteOutcome("drawn through another batch");
                if (_notingThisFrame && DrawnAsMapTiles(texture))
                {
                    NoteSheet(_otherBatchMapSheets, texture);
                    NameOtherBatch(batch);
                }
                return null;
            }
            // Built only while drawsat is listening: this runs for every sprite the game draws, and
            // an interpolated string is made before the call can decide it is not wanted.
            if (MapTileNeighbours.Recording)
                MapTileNeighbours.NoteOutcome($"reached the smoothing and left as drawn (scale {drawScale:0.##})");
            // Only ART. A render target is a picture of the frame - the game's own screen being
            // presented, this mod's effect chain copying its buffers - and doubling those made
            // 300 MB of copies a frame and smoothed the whole picture ten times over. A texel or
            // two is a colour swatch, not art.
            if (texture is RenderTarget2D || texture.Width < 8 || texture.Height < 8)
                return null;
            ArtFamily family = FamilyOf(texture);
            if (!FamilyEnabled(family))
            {
                _untouchedDraws[(int)family]++;
                NoteWorldRoad(family, texture, RoadRaw);
                return null;
            }
            // The half of the world's art this switch is holding back is left exactly as the game
            // drew it, which is what makes it an A/B: nothing is re-baked and nothing else moves.
            if (family == ArtFamily.World && !WorldPartAllows(texture))
            {
                _untouchedDraws[(int)family]++;
                NoteWorldRoad(family, texture, RoadRaw);
                return null;
            }
            Rectangle source = sourceRectangle ?? texture.Bounds;
            if (Style == SheetSmoothingStyle.Soft4x && DrawnLargeEnough(drawScale / SoftScale, soft: true))
            {
                // A rectangle that reaches past the sheet is drawn by the game with the sheet
                // clamped; it is left to the game rather than baked from pixels that are not there.
                if (source.X >= 0 && source.Y >= 0 && source.Right <= texture.Width && source.Bottom <= texture.Height)
                {
                    _softBakeFamily = family;
                    // A map tile is keyed by its surroundings as well, so the same tile beside other
                    // tiles is another bake; the number sits above the family in the variant.
                    int surroundings = family == ArtFamily.World
                        ? MapTileNeighbours.NeighbourhoodOf(texture, source, out _softBakeNeighbours)
                        : 0;
                    if (surroundings == 0)
                        _softBakeNeighbours = null;
                    if (SoftSprites.TryGet(Device!, Effect!, texture, source, (int)family + surroundings * FamilyCount,
                            out Texture2D page, out Rectangle placed))
                    {
                        derivedSource = placed;
                        factor = SoftScale;
                        if (MapTileNeighbours.Recording)
                            MapTileNeighbours.NoteOutcome(surroundings > 0 ? $"SOFT, baked with neighbourhood {surroundings}" : "SOFT, baked alone");
                        _lastDerived = page;
                        _lastDerivedSource = placed;
                        _softDraws[(int)family]++;
                        NoteWorldRoad(family, texture, RoadSoft);
                        if (family == ArtFamily.World)
                            NoteScale(_softWorldScales, drawScale);
                        return page;
                    }
                }
                // Refused or capped this frame: the doubled sheet stands in, as it did before.
            }
            float pixelsPerDoubledTexel = drawScale / Scale;
            bool evenRead = DrawnLargeEnough(pixelsPerDoubledTexel, soft: false);
            // Not even, but at least one screen pixel a doubled texel, and in the interface: the
            // draw still goes to the doubled sheet and its run is read linearly (see _linearRuns),
            // which is even at any size. Only the interface, where the game draws at such sizes
            // all the time (the toolbar's 3.2x): in the world an odd size is an animation, a slime
            // squashing or a tree shaking, and a sprite that went soft while it moved and crisp
            // when it stopped would be its own report. Below one pixel a texel the sheet would be
            // minified and is left to the game either way.
            bool linearRead = !evenRead && Game1.uiMode && pixelsPerDoubledTexel >= 1f - 0.001f;
            if (!evenRead && !linearRead)
            {
                _untouchedDraws[(int)family]++;
                NoteWorldRoad(family, texture, RoadRaw);
                if (family == ArtFamily.World)
                {
                    NoteScale(_untouchedWorldScales, drawScale);
                    NoteSheet(_untouchedWorldSheets, texture);
                }
                return null;
            }
            Texture2D? doubled = Cache.For(Device!, Effect!, texture, (int)family);
            if (doubled == null)
            {
                _untouchedDraws[(int)family]++;
                NoteWorldRoad(family, texture, RoadRaw);
                return null;
            }
            _doubledDraws[(int)family]++;
            NoteWorldRoad(family, texture, RoadDoubled);
            if (linearRead)
                _linearRuns.Add(doubled);
            else
                _linearRuns.Remove(doubled);
            derivedSource = new Rectangle(source.X * Scale, source.Y * Scale, source.Width * Scale, source.Height * Scale);
            factor = Scale;
            _lastDerived = doubled;
            _lastDerivedSource = derivedSource;
            MapTileNeighbours.NoteOutcome("DOUBLED: the soft bake was refused or capped");
            return doubled;
        }

        /// <summary>Which art family a sheet belongs to. The portrait check comes first because a
        /// portrait is drawn in UI mode too, and it has its own switch and dial precisely so a
        /// player can keep the menus crisp while smoothing the faces, or the other way.
        ///
        /// <para>A SHEET'S NAME NEVER CHANGES, so it is read once. This runs inside the prefix on
        /// the game's own SpriteBatch.Draw, which is thousands of calls a frame, and it was doing
        /// up to nine case-insensitive StartsWith comparisons and a Replace on every one of them.
        /// Only <see cref="Game1.uiMode"/> varies between draws of the same sheet, so what is
        /// remembered is the answer WITHOUT it, and the ui-mode question is asked here.</para></summary>
        private static ArtFamily FamilyOf(Texture2D texture)
        {
            ArtFamily family;
            if (_familyBySheet.TryGetValue(texture, out object? remembered) && remembered is ArtFamily known)
                family = known;
            else
            {
                family = FamilyOfName(texture.Name ?? "");
                _familyBySheet.AddOrUpdate(texture, family);
            }
            // The player's own body and an outfit mod's pieces carry no Characters/ name; drawn
            // inside FarmerRenderer they are the player all the same (see _farmerDrawDepth).
            if (family == ArtFamily.World && _farmerDrawDepth > 0)
                family = ArtFamily.Characters;
            // Everything drawn in UI mode is the interface, items included: a tool in the toolbar
            // or the inventory follows the Menus switch and dial, and the same tool lying on the
            // ground follows Items. The families are named for where the player sees them, and the
            // author chose that over "the same sheet reads the same everywhere" once the toolbar
            // could be smoothed at all (see _linearRuns). Portraits keep their own family in UI
            // mode, which is the whole reason they are asked about first.
            if ((Game1.uiMode || _cursorDrawDepth > 0) && family != ArtFamily.Portraits)
                return ArtFamily.Interface;
            return family;
        }

        /// <summary>The family a sheet belongs to by its name alone, ui mode aside. Keyed by the
        /// texture rather than by the name so a repainted sheet under the same content path is
        /// asked about once as well; the table holds no reference of its own, so a sheet the game
        /// unloads leaves it.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, object> _familyBySheet = [];

        private static ArtFamily FamilyOfName(string name)
        {
            if (name.StartsWith("Portraits", StringComparison.OrdinalIgnoreCase))
                return ArtFamily.Portraits;
            if (name.StartsWith("Characters", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Animals", StringComparison.OrdinalIgnoreCase))
                return ArtFamily.Characters;
            // Items lying in the world are their own family, known by their sheet, so a placed
            // object, tool or piece of furniture can be rounded differently from the terrain.
            if (IsItemSheet(name))
                return ArtFamily.Items;
            return ArtFamily.World;
        }

        /// <summary>Whether the family's switch is on.</summary>
        private static bool FamilyEnabled(ArtFamily family) => family switch
        {
            ArtFamily.Portraits => PortraitsEnabled,
            ArtFamily.Characters => CharactersEnabled,
            ArtFamily.Items => ItemsEnabled,
            ArtFamily.Interface => InterfaceEnabled,
            _ => WorldEnabled,
        };

        /// <summary>The game's own item sheets by content path: objects, the second object sheet,
        /// tools, weapons, big craftables and the furniture sheets. A content pack's own item
        /// sheet under Mods/ is not one of these and counts as the world.</summary>
        private static bool IsItemSheet(string name)
        {
            string path = name.Replace('\\', '/');
            return path.StartsWith("Maps/springobjects", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/Objects_2", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/tools", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/weapons", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/Craftables", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/furniture", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The least a DOUBLED sheet may be drawn at, in screen pixels per doubled texel.
        ///
        /// <para>The game draws its menus with point sampling, and a doubled sheet at scale 2 is
        /// exact under it: every doubled texel is a 2x2 block of pixels, the picture is the
        /// original with its corners rounded. But not everything is drawn at 4x. The toolbar
        /// draws each item at 0.8 or 0.9 of that (3.2x and 3.6x), a quality star and a stack
        /// count at 3x, a small icon here and there at 2x. Halved for the doubled sheet those are
        /// 1.6, 1.8, 1.5 and 1 pixel per texel, and under point sampling a texel is then one
        /// pixel wide or two, with no pattern the eye forgives: a stack count wobbles, a star has
        /// a chunk out of it, the toolbar's items look torn. Vanilla at 3.2x has texels three or
        /// four pixels wide, which nobody notices. This was reported as "the vast majority of items
        /// in the inventory look pixelated and glitched", by two people in one thread, with the
        /// items themselves - drawn at 4x in the grid - measured unchanged.</para>
        ///
        /// <para>At two pixels per doubled texel and above the worst unevenness is two against
        /// three, no worse than the game's own at the same size, so the doubled sheet stays.
        /// Below it the draw is left as the game made it, which is exactly what the player saw
        /// before switching the smoothing on. The pulse an item makes when hovered (4x to 4.4x)
        /// stays above the line, so nothing flips as it grows.</para></summary>
        private const float MinimumDoubledScale = 2f;

        /// <summary>A pixel a radiance_drawsat question is about, in the game's own coordinates, and
        /// every draw through these three overloads that covered it since, whatever batch it went to:
        /// the list the recorder cannot give, since it only sees the game's sorted world step.</summary>
        internal static Point? WatchedPixel;
        internal static readonly List<string> WatchedHits = [];
        internal static int WatchFramesLeft;
        internal static IMonitor? WatchMonitor;

        private static void Watch(SpriteBatch batch, Texture2D texture, Rectangle? source, Vector2 topLeft, Vector2 size, bool redirected)
            => Watch(batch, texture, source, topLeft, size, redirected ? _lastDerived : null, _lastDerivedSource);

        /// <summary>Whether SpaceCore's texture overrides (spacechase0.SpaceCore/TextureOverrides) swap
        /// this rectangle of this texture for other art when it is drawn, read from its own table.</summary>
        private static string SpaceCoreOverrideOf(Texture2D texture, Rectangle? source)
        {
            try
            {
                if (AccessTools.TypeByName("SpaceCore.Patches.SpriteBatchPatcher")?.GetField("packOverrides",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null) is not System.Collections.IDictionary table || texture.Name == null || source is not Rectangle rect || !table.Contains(texture.Name))
                    return "";
                if (table[texture.Name] is not System.Collections.IDictionary byRect)
                    return "";
                if (!byRect.Contains(rect))
                    return $" [SpaceCore overrides {byRect.Count} rectangle(s) of this sheet, not this one]";
                object? data = byRect[rect];
                var sourceTexture = data?.GetType().GetField("sourceTex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(data) as Texture2D;
                object? current = data?.GetType().GetField("sourceRectCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(data);
                return $" [SPACECORE OVERRIDE -> {sourceTexture?.Name ?? "(unnamed)"} {current}]";
            }
            catch (Exception ex)
            {
                return $" [SpaceCore table unreadable: {ex.GetType().Name}]";
            }
        }

        private static Texture2D? _lastDerived;
        private static Rectangle _lastDerivedSource;

        private static void Watch(SpriteBatch batch, Texture2D texture, Rectangle? source, Vector2 topLeft, Vector2 size, Texture2D? derived, Rectangle derivedSource)
        {
            bool redirected = derived != null;
            if (WatchedPixel is not Point pixel || texture == null || WatchedHits.Count > 200)
                return;
            if (pixel.X < topLeft.X || pixel.Y < topLeft.Y || pixel.X >= topLeft.X + size.X || pixel.Y >= topLeft.Y + size.Y)
                return;
            string caller = "";
            if (!redirected)
            {
                var callers = new List<string>();
                foreach (var frame in new System.Diagnostics.StackTrace(2, false).GetFrames())
                {
                    var method = frame.GetMethod();
                    string type = method?.DeclaringType?.FullName ?? "";
                    if (method == null || type.StartsWith("Microsoft.Xna", StringComparison.Ordinal) || type.StartsWith("HarmonyLib", StringComparison.Ordinal)
                        || type.Contains("SheetUpscaler") || method.Name.Contains("DMD<") || method.Name.Contains("_PatchedBy"))
                        continue;
                    callers.Add($"{type}.{method.Name}");
                    if (callers.Count == 3)
                        break;
                }
                caller = " <- " + string.Join(" <- ", callers);
            }
            string name = string.IsNullOrEmpty(texture.Name) ? $"(unnamed {texture.Width}x{texture.Height})" : texture.Name;
            caller += SpaceCoreOverrideOf(texture, source);
            WatchedHits.Add($"{(ReferenceEquals(batch, Game1.spriteBatch) ? "game batch" : "OTHER batch")}: {name} source {source?.ToString() ?? "whole"} "
                + $"over {topLeft.X:0},{topLeft.Y:0} {size.X:0}x{size.Y:0} -> {(derived == null ? "AS DRAWN" : SoftSprites.IsOwnOutput(derived) ? $"soft page {SoftSprites.PageIndexOf(derived)} at {derivedSource}" : $"DOUBLED sheet at {derivedSource}")}{caller}");
        }

        private static bool _shortOverloadsWatched;

        private static void DrawShortDestination_Watch(SpriteBatch __instance, Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle)
        {
            if (WatchedPixel.HasValue && texture != null && !SoftSprites.IsOwnOutput(texture))
                Watch(__instance, texture, sourceRectangle, new Vector2(destinationRectangle.X, destinationRectangle.Y),
                    new Vector2(destinationRectangle.Width, destinationRectangle.Height), false);
        }

        private static void DrawWholeDestination_Watch(SpriteBatch __instance, Texture2D texture, Rectangle destinationRectangle)
        {
            if (WatchedPixel.HasValue && texture != null && !SoftSprites.IsOwnOutput(texture))
                Watch(__instance, texture, null, new Vector2(destinationRectangle.X, destinationRectangle.Y),
                    new Vector2(destinationRectangle.Width, destinationRectangle.Height), false);
        }

        private static void DrawWholePosition_Watch(SpriteBatch __instance, Texture2D texture, Vector2 position)
        {
            if (WatchedPixel.HasValue && texture != null && !SoftSprites.IsOwnOutput(texture))
                Watch(__instance, texture, null, position, new Vector2(texture.Width, texture.Height), false);
        }

        private static void DrawShortPosition_Watch(SpriteBatch __instance, Texture2D texture, Vector2 position, Rectangle? sourceRectangle)
        {
            if (WatchedPixel.HasValue && texture != null && !SoftSprites.IsOwnOutput(texture))
            {
                Rectangle bounds = sourceRectangle ?? texture.Bounds;
                Watch(__instance, texture, sourceRectangle, position, new Vector2(bounds.Width, bounds.Height), false);
            }
        }

        private static void DrawVectorScale_Prefix(SpriteBatch __instance, ref Texture2D texture, Vector2 position, ref Rectangle? sourceRectangle, ref Vector2 origin, ref Vector2 scale)
        {
            Texture2D original = texture;
            Rectangle? originalSource = sourceRectangle;
            Texture2D? derived = Derived(__instance, texture, sourceRectangle, Math.Min(scale.X, scale.Y), out Rectangle derivedSource, out int factor);
            if (WatchedPixel.HasValue && !SoftSprites.IsOwnOutput(original))
            {
                Rectangle bounds = originalSource ?? original.Bounds;
                Watch(__instance, original, originalSource, position - origin * scale, new Vector2(bounds.Width, bounds.Height) * scale, derived != null);
            }
            if (derived == null)
                return;
            sourceRectangle = derivedSource;
            texture = derived;
            // The origin is in source texels, so it scales with them, or every sprite hung from
            // its base (a tree from (24, 96)) slides by half its origin.
            origin *= factor;
            scale /= factor;
            RedirectedThisFrame++;
        }

        private static void DrawFloatScale_Prefix(SpriteBatch __instance, ref Texture2D texture, Vector2 position, ref Rectangle? sourceRectangle, ref Vector2 origin, ref float scale)
        {
            Texture2D original = texture;
            Rectangle? originalSource = sourceRectangle;
            Texture2D? derived = Derived(__instance, texture, sourceRectangle, scale, out Rectangle derivedSource, out int factor);
            if (WatchedPixel.HasValue && original != null && !SoftSprites.IsOwnOutput(original))
            {
                Rectangle bounds = originalSource ?? original.Bounds;
                Watch(__instance, original, originalSource, position - origin * scale, new Vector2(bounds.Width, bounds.Height) * scale, derived != null);
            }
            if (derived == null)
                return;
            sourceRectangle = derivedSource;
            texture = derived;
            origin *= factor;
            scale /= factor;
            RedirectedThisFrame++;
        }

        private static void DrawDestination_Prefix(SpriteBatch __instance, ref Texture2D texture, Rectangle destinationRectangle, ref Rectangle? sourceRectangle, ref Vector2 origin)
        {
            // The scale is implied here: the destination over the source, per axis.
            if (texture == null)
                return;
            Rectangle impliedSource = sourceRectangle ?? texture.Bounds;
            float impliedScale = Math.Min(destinationRectangle.Width / (float)Math.Max(1, impliedSource.Width),
                                          destinationRectangle.Height / (float)Math.Max(1, impliedSource.Height));
            Texture2D originalTexture = texture;
            Rectangle? originalRectangle = sourceRectangle;
            Texture2D? derived = Derived(__instance, texture, sourceRectangle, impliedScale, out Rectangle derivedSource, out int factor);
            if (WatchedPixel.HasValue && !SoftSprites.IsOwnOutput(originalTexture))
                Watch(__instance, originalTexture, originalRectangle, new Vector2(destinationRectangle.X, destinationRectangle.Y),
                    new Vector2(destinationRectangle.Width, destinationRectangle.Height), derived != null);
            if (derived == null)
                return;
            sourceRectangle = derivedSource;
            texture = derived;
            // With a destination rectangle the origin is still in source texels (the batch scales
            // it by destination over source), so it scales too.
            origin *= factor;
            RedirectedThisFrame++;
        }

        /// <summary>Once a frame: reset the counter, sweep reloaded sheets' ghosts, and hand the
        /// sheets back once switched off.</summary>
        internal static void BeginFrame()
        {
            if (WatchedPixel.HasValue && WatchFramesLeft > 0 && --WatchFramesLeft == 0)
            {
                WatchMonitor?.Log($"  draws covering the watched pixel over the next frames, after the answer ({WatchedHits.Count}, repeats folded):", LogLevel.Info);
                foreach (var group in WatchedHits.GroupBy(hit => hit))
                    WatchMonitor?.Log($"      x{group.Count()} {group.Key}", LogLevel.Info);
                WatchedHits.Clear();
                WatchedPixel = null;
            }
            RedirectedThisFrame = 0;
            Array.Copy(_softDraws, _softDrawsLastFrame, FamilyCount);
            Array.Copy(_doubledDraws, _doubledDrawsLastFrame, FamilyCount);
            Array.Copy(_untouchedDraws, _untouchedDrawsLastFrame, FamilyCount);
            Array.Clear(_softDraws, 0, FamilyCount);
            Array.Clear(_doubledDraws, 0, FamilyCount);
            Array.Clear(_untouchedDraws, 0, FamilyCount);
            Array.Copy(_mapTileRoads, _mapTileRoadsLastFrame, RoadCount);
            Array.Copy(_worldSpriteRoads, _worldSpriteRoadsLastFrame, RoadCount);
            Array.Clear(_mapTileRoads, 0, RoadCount);
            Array.Clear(_worldSpriteRoads, 0, RoadCount);
            if (_notingThisFrame)
            {
                _untouchedWorldScalesLastFrame.Clear();
                foreach (var pair in _untouchedWorldScales)
                    _untouchedWorldScalesLastFrame[pair.Key] = pair.Value;
                _untouchedWorldScales.Clear();
                _softWorldScalesLastFrame.Clear();
                foreach (var pair in _softWorldScales)
                    _softWorldScalesLastFrame[pair.Key] = pair.Value;
                _softWorldScales.Clear();
                _untouchedWorldSheetsLastFrame.Clear();
                foreach (var pair in _untouchedWorldSheets)
                    _untouchedWorldSheetsLastFrame[pair.Key] = pair.Value;
                _untouchedWorldSheets.Clear();
            }
            _framesSinceNoted = _notingThisFrame ? 1 : _framesSinceNoted + 1;
            _notingThisFrame = _framesSinceNoted >= NoteEveryFrames;
            _linearRuns.Clear();
            if (ResampleFramesLeft > 0)
                ResampleFramesLeft--;
            _linearForSoftSheets = Enabled && Style == SheetSmoothingStyle.Soft4x && _batcherOf != null;
            bool dialsMoved = false;
            for (int family = 0; family < FamilyCount; family++)
                dialsMoved |= _bakedSmoothnessByFamily[family] != SmoothnessByFamily[family]
                              || _bakedKernelByFamily[family] != KernelByFamily[family];
            if (Enabled && (dialsMoved || _bakedStyle != Style || _bakedSoftKernel != SoftKernel || _bakedSoftDeposterize != SoftDeposterize || _bakedSoftEdge != SoftEdgeSourcePixels || _bakedSoftBlur != SoftBlurTexels
                            || _bakedSoftDither != SoftDitherTexels || _bakedSoftTileFeather != SoftTileFeatherTexels))
            {
                // The dials, the style, the edge width and the tent are baked into the sheets, so
                // every held sheet is at the OLD value: hand them back and let the next draws re-make them.
                Array.Copy(SmoothnessByFamily, _bakedSmoothnessByFamily, FamilyCount);
                Array.Copy(KernelByFamily, _bakedKernelByFamily, FamilyCount);
                _bakedStyle = Style;
                _bakedSoftKernel = SoftKernel;
                _bakedSoftDeposterize = SoftDeposterize;
                _bakedSoftEdge = SoftEdgeSourcePixels;
                _bakedSoftBlur = SoftBlurTexels;
                _bakedSoftDither = SoftDitherTexels;
                _bakedSoftTileFeather = SoftTileFeatherTexels;
                if (Cache.Count > 0)
                    Cache.Clear();
                if (SoftSprites.Count > 0)
                    ClearSoftSprites();
            }
            if (Enabled)
            {
                Cache.SweepDisposed();
                SoftSprites.SweepDisposed();
            }
            else
            {
                if (Cache.Count > 0)
                    Cache.Clear();
                if (SoftSprites.Count > 0)
                    ClearSoftSprites();
            }
        }

        /// <summary>
        /// What the soft look actually managed this frame, which decides whether the picture is
        /// smooth all over or a patchwork.
        /// </summary>
        /// <remarks>
        /// Two ways a sprite ends up drawn raw beside a smoothed neighbour, and until this line
        /// existed neither could be seen from the game. REFUSED: a sprite wider or taller than the
        /// cache's largest side never gets a soft copy at all, so a big piece of art stays sharp
        /// next to small ones for as long as it is on screen. BEHIND: only a handful are baked per
        /// frame, so walking into a new view smooths it over several frames and everything not yet
        /// reached is sharp meanwhile. The first is permanent and the second passes; they look the
        /// same in a screenshot and they need opposite fixes.
        /// </remarks>
        /// <remarks>Reading this line starts its "since the last report" counters over, so two
        /// readings a minute apart are a rate rather than two totals.</remarks>
        internal static string DescribeSoftSprites()
        {
            if (!Enabled || Style != SheetSmoothingStyle.Soft4x)
                return "    soft sprites: the soft look is off, so every sprite draws as the game drew it.";
            string line = $"    soft sprites: {SoftSprites.Count} held on {SoftSprites.PageCount} page(s), "
                 + $"{SoftSprites.Generated} baked since the last report, {SoftSprites.Refused} REFUSED "
                 + $"(too big for a page: over {SoftSprites.LargestSpriteSide} texels a side, so they stay sharp), "
                 + $"{SoftSprites.Evicted} evicted, at most {SoftSprites.GeneratePerFrameCap} baked a frame ({SoftSprites.LargestFrame} in the busiest frame, which only a warp's burst takes past the cap)."
                 + Environment.NewLine
                 + $"    soft sprites evicted, by reason: {SoftSprites.EvictedByBudget} over budget, "
                 + $"{SoftSprites.EvictedBySweep} their sheet was thrown away and built again, "
                 + $"{SoftSprites.EvictedByDeadPage} our own page was lost, {SoftSprites.Adopted} handed straight "
                 + "to the sheet that replaced them instead of being made again. Sheets the sweep keeps taking: "
                 + SoftSprites.DescribeSweptSheets()
                 + Environment.NewLine
                 + $"    soft sprites that drew SHARP because the frame's bakes were spent: {SoftSprites.CappedLastFrame} last frame, "
                 + $"{SoftSprites.Capped} since the last report. That art: " + SoftSprites.DescribeCappedSheets()
                 + Environment.NewLine
                 + $"    soft sprite bake time: {SoftSprites.BakeMillisecondsLastFrame:0.000} ms last frame, "
                 + $"worst {SoftSprites.WorstBakeMilliseconds:0.000} ms, budget {SoftSpriteCache.BakeBudgetMillisecondsPerFrame:0.00} ms a frame "
                 + $"(ceiling {SoftSprites.GeneratePerFrameCap} bakes), longest arrival burst {SoftSprites.BurstFramesTaken} frames, "
                 + $"{(SoftSprites.Generated > 0 ? SoftSprites.BakeMillisecondsSinceReport / SoftSprites.Generated : 0):0.000} ms a bake on average"
                 + Environment.NewLine + MapTileNeighbours.Describe()
                 + Environment.NewLine + DescribeSmoothingRoads();
            SoftSprites.CountersReported();
            return line;
        }

        /// <summary>Which road each family's draws took last frame. A family with draws on two
        /// roads at once is a family drawn at two different smoothnesses in one picture, which is
        /// what a plate is.</summary>
        internal static string DescribeSmoothingRoads()
        {
            string otherBatches = _otherBatchMapSheets.Count == 0 ? "none"
                : string.Join(" · ", _otherBatchMapSheets.OrderByDescending(pair => pair.Value).Take(6).Select(pair => $"{pair.Key}:{pair.Value}"));
            _otherBatchMapSheets.Clear();
            return $"    map art drawn through another batch, never smoothed (one frame in {NoteEveryFrames}): {otherBatches}"
                 + (_otherBatchCallerLines.Count == 0 ? "" : Environment.NewLine + "      drawn by: " + string.Join(Environment.NewLine + "      drawn by: ", _otherBatchCallerLines))
                 + Environment.NewLine + DescribeSmoothingRoadsOnly();
        }

        private static string DescribeSmoothingRoadsOnly()
        {
            var line = new System.Text.StringBuilder("    smoothing roads (last frame): ");
            for (int family = 0; family < FamilyCount; family++)
            {
                int soft = _softDrawsLastFrame[family];
                int doubled = _doubledDrawsLastFrame[family];
                int untouched = _untouchedDrawsLastFrame[family];
                if (soft + doubled + untouched == 0)
                    continue;
                line.Append($"{(ArtFamily)family} soft={soft} doubled={doubled} untouched={untouched}");
                line.Append(soft > 0 && doubled > 0 ? " <- MIXED, this family is drawn two ways; " : "; ");
            }
            line.Append(Environment.NewLine);
            line.Append($"    the world's two halves (last frame): map tiles {DescribeRoads(_mapTileRoadsLastFrame)}"
                      + $" | sprites on them {DescribeRoads(_worldSpriteRoadsLastFrame)}"
                      + $" | radiance_smoothonly is {SmoothedWorldPart}");
            line.Append(Environment.NewLine);
            line.Append($"    world draw scales (one frame, sampled once a second): softened {DescribeScales(_softWorldScalesLastFrame)}"
                      + $" | left alone {DescribeScales(_untouchedWorldScalesLastFrame)}");
            line.Append(Environment.NewLine);
            line.Append("    world art left raw (one frame, sampled once a second): ");
            if (_untouchedWorldSheetsLastFrame.Count == 0)
                line.Append("none");
            else
                line.Append(string.Join(" · ", _untouchedWorldSheetsLastFrame
                    .OrderByDescending(pair => pair.Value).Take(6).Select(pair => $"{pair.Key}:{pair.Value}")));
            return line.ToString();
        }

        internal static void Dispose()
        {
            Cache.Dispose();
            SoftSprites.Dispose();
            MapTileNeighbours.Clear();
        }
    }
}
