using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline: a river's water going somewhere, along its own bed.
    ///
    /// A map is a river when it paints falling water: the falls are painted in the labels rather
    /// than held in the surface grid, so they come from a whole-map walk, sliced a few rows per
    /// frame like the window and emissive walks beside it. Once the falls are known the river is
    /// traced off the draw thread (<see cref="RiverFlowTrace"/>) into a flow map, one texel per
    /// tile, and the water shader carries its surface along it (FlowTexture in water.fx).
    ///
    /// Only rivers. The first cut also brought a sea's swell in toward its land and gave every map
    /// one heading; the author looked at both and kept neither: the sea read better as it was, and
    /// one heading ran a stream straight across its own bend. A map with no fall painted on it is
    /// the water of every earlier release, to the pixel.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>How fast a river runs where its flow map reads 1, with the dial at 1, in world
        /// tiles per animation second. A tile is about a metre of the valley (a villager stands two
        /// tiles tall) and a brook runs at half a metre to a metre a second, so the default dial of
        /// 0.6 lands at 0.72. The first cut ran at a sixth of that and lost to every ripple still
        /// travelling on its own.</summary>
        private const float FullRiverPaceTilesPerSecond = 1.2f;

        /// <summary>Seconds for the river to come in or go out, for the dial and for a doorway. The
        /// house rule is that nothing pops.</summary>
        private const float CurrentEaseSeconds = 0.8f;

        /// <summary>Seconds per carry cycle of the first flowing rivers; renewal shortens it.</summary>
        private const float FirstCarryPeriodSeconds = 1.6f;

        /// <summary>How much faster a river runs in rain, and in a thunderstorm on top of that, at the
        /// swell dial's 1. A real river rises over hours; a player standing on a bridge while the
        /// storm comes in should see it within the minute, so it rises over <see cref="RiverSwellEaseSeconds"/>.</summary>
        private const float RainSwellShare = 0.4f;
        private const float StormSwellShare = 0.4f;
        private const float RiverSwellEaseSeconds = 6f;

        /// <summary>Where the falling water is on each map, one sliced walk per location.</summary>
        private readonly Dictionary<GameLocation, WholeMapAnswer<Vector2>> _fallingWaterByLocation = [];
        private Action<GameLocation, int, List<Vector2>>? _fallingWaterRowScanner;

        /// <summary>How much of a tile has to be painted as falling water before it counts as a
        /// fall. A quarter of it: the spray at the foot of a fall is painted on its neighbours too.</summary>
        private const int FallingPixelsPerTile = 64;

        /// <summary>How much open water a fall has to stand in before it is taken as a river's.
        ///
        /// <para>A fountain jet carries the same painted class as a waterfall - water.fx names
        /// both in the one comment - and a jet standing in a basin says nothing about a river. A
        /// fall that belongs to one has a river around it.</para></summary>
        private const int FallingWaterNeighbours = 14;
        private const int FallingWaterNeighbourReach = 3;

        /// <summary>The layers a fall can be drawn on, topmost art last.</summary>
        private static readonly string[] FallingLayers =
            ["Back", "Back2", "Buildings", "Buildings2", "Front", "Front2", "AlwaysFront"];

        /// <summary>One map's traced river: the trace while it runs, its answer, and the flow map made
        /// from it. Keyed by name, and remade when the map is reloaded or its falls change.</summary>
        private sealed class TracedRiver
        {
            internal int Reloads;
            internal int Falls;
            internal Task<RiverFlowTrace.Result>? Tracing;
            internal RiverFlowTrace.Result? Result;
            internal Texture2D? FlowMap;
            /// <summary>The bank drag the flow map was built with (see WaterRiverBankDrag).</summary>
            internal float FlowMapBankDrag = -1f;
            internal string? Failure;
            internal long LastUsedTick;
            /// <summary>The river this one is being traced to replace, still flowing until the new
            /// trace lands (see <see cref="StillFlowingHere"/>).</summary>
            internal TracedRiver? Replacing;
            /// <summary>The surface grid the trace was run on, kept for the report's map of it.</summary>
            internal SurfaceClass[] Surfaces = [];
            internal int Width;
        }

        private readonly Dictionary<string, TracedRiver> _riversByLocation = [];

        /// <summary>A few maps' worth, so split screen with the two players on two rivers does not
        /// retrace every frame.</summary>
        private const int RiversKept = 4;

        /// <summary>One flat texel for every map without a river, so the sampler is never left
        /// holding whatever the last pass bound there.</summary>
        private Texture2D? _stillFlowMap;

        /// <summary>What the last reading found, for radiance_report: "no river" has several causes
        /// and they look identical from the water.</summary>
        private string _lastRiverReading = "not read yet";

        /// <summary>Ease the river in or out. Called from the water stage, which runs once per
        /// screen, so the tick stamp holds it to one pass.</summary>
        private void UpdateWaterCurrent(ModConfig config)
        {
            int tick = Determinism.Ticks;
            if (tick == _currentTick)
                return;
            int elapsedTicks = _currentTick < 0 ? 1 : Math.Clamp(tick - _currentTick, 0, WindMaxCatchUpTicks);
            _currentTick = tick;
            if (elapsedTicks <= 0)
                return;
            float ease = Math.Clamp(elapsedTicks / 60f / CurrentEaseSeconds, 0f, 1f);

            GameLocation? location = Game1.currentLocation;
            // Switched off, nothing is looked for or traced; the river already in view eases out.
            TracedRiver? river = config.WaterRiverFlowEnabled || _riverHoldEased > 0f ? RiverFor(location) : null;
            float dial = config.WaterRiverFlowEnabled ? Math.Max(0f, config.WaterCurrent) : 0f;
            float holdTarget = river?.FlowMap != null && dial > 0.01f ? 1f : 0f;
            _riverHoldEased += (holdTarget - _riverHoldEased) * ease;
            if (holdTarget == 0f && _riverHoldEased < 0.0005f)
                _riverHoldEased = 0f;
            // Snow wins over rain here as it does for the rings and the wet ground (LocalSky).
            bool raining = location != null && LocalSky.RainLandsOn(location);
            bool storming = raining && (location?.IsLightningHere() ?? false);
            float swellTarget = 1f + Math.Max(0f, config.WaterRiverRainSwell)
                                   * ((raining ? RainSwellShare : 0f) + (storming ? StormSwellShare : 0f));
            float swellEase = Math.Clamp(elapsedTicks / 60f / RiverSwellEaseSeconds, 0f, 1f);
            _riverSwellEased += (swellTarget - _riverSwellEased) * swellEase;
            _riverPaceEased += (FullRiverPaceTilesPerSecond * dial * _riverSwellEased - _riverPaceEased) * ease;

            if (location != null)
                HarmonyPatcher.SetGameWaterScroll(location, GameWaterScrollHere(river));
        }

        /// <summary>Hand the shader this screen's flow map and how much of the river is in.</summary>
        private void SetWaterCurrentParams(Effect effect, ModConfig config)
        {
            // The foam is part of the surface's motion, so the shimmer switch takes it with the
            // ripples and the sparkle; with shimmer off and the reflection on, flecks rode a river
            // that had otherwise gone still.
            GetParam(effect, "RiverFoam")?.SetValue(Math.Max(0f, config.WaterRiverFoam) * _shimmerEase * _fadeWater);
            TracedRiver? river = config.WaterRiverFlowEnabled || _riverHoldEased > 0f ? RiverFor(Game1.currentLocation) : null;
            _stillFlowMap ??= StillFlowMap();
            bool traced = river?.FlowMap != null && river.Result != null;
            GetParam(effect, "FlowTexture")?.SetValue(traced ? river!.FlowMap : _stillFlowMap);
            GetParam(effect, "FlowMapSize")?.SetValue(traced
                ? new Vector2(river!.Result!.Width, river.Result.Height)
                : Vector2.One);
            // How the river reads as water (see "WATER, NOT SYRUP" in water.fx). The fine detail is
            // carried at the ripple speed; the big ripples take a share of that carry that lands them
            // back at the river's own pace at 1 and hangs them further back as the ripples speed up.
            float rippleSpeed = Math.Clamp(config.WaterRiverRippleSpeed, 1f, 3f);
            GetParam(effect, "FlowPace")?.SetValue(_riverPaceEased * rippleSpeed);
            GetParam(effect, "RiverCoarseShare")?.SetValue(MathHelper.Lerp(1f, 0.6f, Math.Clamp(rippleSpeed - 1f, 0f, 1f)) / rippleSpeed);
            float renew = Math.Clamp(config.WaterRiverRenew, 0f, 1f);
            GetParam(effect, "RiverPeriod")?.SetValue(FirstCarryPeriodSeconds / (1f + renew));
            GetParam(effect, "RiverRenew")?.SetValue(renew);
            GetParam(effect, "RiverSwirl")?.SetValue(Math.Clamp(config.WaterRiverSwirl, 0f, 1f));
            GetParam(effect, "RiverFoamStretch")?.SetValue(Math.Clamp(config.WaterRiverFoamStreak, 1f, 5f));
            GetParam(effect, "RiverGlintLife")?.SetValue(Math.Clamp(config.WaterRiverGlitter, 0f, 1f));
            GetParam(effect, "RiverPixelStep")?.SetValue(config.WaterRiverPixelStep ? 1f : 0f);
            GetParam(effect, "RiverWaves")?.SetValue(Math.Clamp(config.WaterRiverWaves, 0f, 2f) * _shimmerEase * _fadeWater);
            GetParam(effect, "RiverHold")?.SetValue(traced ? _riverHoldEased : 0f);
        }

        private Texture2D StillFlowMap()
        {
            var texture = new Texture2D(_device, 1, 1, false, SurfaceFormat.Color);
            texture.SetData([new Color(128, 128, 0, 255)]);
            return texture;
        }

        /// <summary>How the game's own water texture should scroll here, as a multiple of the way it
        /// always has. The game slides every water tile up the screen, a seventh of a tile a second,
        /// under everything this shader draws, and on a river running down the screen it was the one
        /// thing still going uphill. It can only scroll up or down, so it follows the river on screen:
        /// the flow of every river tile in view, averaged. Reversed where the water comes down,
        /// stopped where it runs across, left alone where it goes up. Only the pace of the scroll
        /// changes, never where it is, so walking along a bend never makes the texture jump.
        ///
        /// <para>The first cut read the one tile in the middle of the screen, which is where the
        /// player stands, which is on the bank: the river beside them scrolled uphill.</para></summary>
        private float GameWaterScrollHere(TracedRiver? river)
        {
            if (_riverHoldEased <= 0f || river?.Result == null)
                return 1f;
            RiverFlowTrace.Result trace = river.Result;
            int left = Math.Max(0, Game1.viewport.X / Game1.tileSize);
            int top = Math.Max(0, Game1.viewport.Y / Game1.tileSize);
            int right = Math.Min(trace.Width - 1, (Game1.viewport.X + Game1.viewport.Width) / Game1.tileSize);
            int bottom = Math.Min(trace.Height - 1, (Game1.viewport.Y + Game1.viewport.Height) / Game1.tileSize);
            Vector2 sum = Vector2.Zero;
            int riverTiles = 0;
            for (int tileY = top; tileY <= bottom; tileY++)
            {
                for (int tileX = left; tileX <= right; tileX++)
                {
                    int index = tileY * trace.Width + tileX;
                    if (!trace.Reached[index])
                        continue;
                    sum += trace.Flow[index];
                    riverTiles++;
                }
            }
            if (riverTiles == 0)
                return 1f;
            Vector2 flow = sum / riverTiles;
            float length = flow.Length();
            if (length < 0.0001f)
                return 1f;
            float shown = Math.Min(1f, length);
            return 1f - _riverHoldEased * (shown + flow.Y / length * shown);
        }

        /// <summary>This map's traced river, starting the trace when the falls are known and making the
        /// flow map when it finishes. Null where there is no river, or none yet.</summary>
        private TracedRiver? RiverFor(GameLocation? location)
        {
            if (location == null)
                return null;
            List<Vector2>? falls = FallingWaterTiles(location);
            if (falls == null)
            {
                _lastRiverReading = "the falls are still being looked for";
                return StillFlowingHere(location);
            }
            if (falls.Count == 0)
            {
                _lastRiverReading = "no fall painted on this map: still water, as it always was";
                return null;
            }

            string name = location.NameOrUniqueName ?? "";
            int reloads = SurfaceMap.MapReloadCount(location);
            if (!_riversByLocation.TryGetValue(name, out TracedRiver? river)
                || river.Reloads != reloads || river.Falls != falls.Count)
            {
                TracedRiver? previous = river;
                river = StartTrace(location, falls, reloads);
                if (river == null)
                {
                    _lastRiverReading = "no surface grid for this map yet";
                    return StillFlowingHere(location);
                }
                // The river already flowing keeps flowing while the new one is traced, then hands
                // over; one still waiting on its own trace passes on what it was waiting in front of.
                if (previous?.FlowMap != null)
                {
                    ForgetReplaced(previous);
                    river.Replacing = previous;
                }
                else if (previous != null)
                {
                    river.Replacing = previous.Replacing;
                    previous.Replacing = null;
                }
                // Stamped before the trim: a new entry still at tick 0 was always the oldest, so
                // the fifth river map in a day was evicted as soon as it was added and traced
                // again on every call, and never flowed.
                river.LastUsedTick = Determinism.Ticks;
                _riversByLocation[name] = river;
                ForgetOldRivers(keep: name);
            }
            river.LastUsedTick = Determinism.Ticks;

            if (river.Result == null && river.Failure == null && river.Tracing is { IsCompleted: true } done)
            {
                if (done.IsCompletedSuccessfully)
                    river.Result = done.Result;
                else
                    river.Failure = done.Exception?.GetBaseException().Message ?? "the trace stopped";
            }
            if (river.Failure != null)
            {
                _lastRiverReading = $"the trace failed ({river.Failure}): still water";
                ForgetReplaced(river);
                return null;
            }
            if (river.Result == null)
            {
                _lastRiverReading = $"{falls.Count} falling tiles, tracing the river";
                return river.Replacing ?? river;
            }
            float bankDrag = Math.Clamp(_lastConfig?.WaterRiverBankDrag ?? 0f, 0f, 1f);
            if (river.FlowMap != null && river.FlowMapBankDrag != bankDrag)
            {
                river.FlowMap.Dispose();
                river.FlowMap = null;
            }
            if (river.FlowMap == null)
            {
                river.FlowMap = BuildFlowMap(river.Result, bankDrag);
                river.FlowMapBankDrag = bankDrag;
            }
            ForgetReplaced(river);

            RiverFlowTrace.Result trace = river.Result;
            _lastRiverReading = $"{falls.Count} falling tiles, the river reaches {trace.ReachedTiles} of {trace.WaterTiles} water tiles, "
                              + $"{trace.SourceTiles} tiles where the water comes in, "
                              + (trace.TracedToAnEdge
                                  ? $"{trace.LeavingTiles} where it leaves the map, traced between them in {trace.RelaxationSweeps} sweeps"
                                  : "nowhere it leaves the map, so it runs away from the falls")
                              + $", {trace.Milliseconds:F1} ms off the draw thread";
            return river;
        }

        private static TracedRiver? StartTrace(GameLocation location, List<Vector2> falls, int reloads)
        {
            SurfaceMap? surface = SurfaceMap.For(location);
            if (surface == null)
                return null;
            // Copied here, on the thread that owns the map; the trace itself touches nothing of the game's.
            // A bridge counts as river: the grid calls its deck a deck, and without this the town's
            // river stopped at its first bridge and everything below it stood still. Only the copy the
            // trace runs on; the map letters below still show the grid as it is.
            int width = surface.Width, height = surface.Height;
            var water = new bool[width * height];
            var surfaces = new SurfaceClass[width * height];
            for (int tileY = 0; tileY < height; tileY++)
            {
                for (int tileX = 0; tileX < width; tileX++)
                {
                    surfaces[tileY * width + tileX] = surface.GetSurface(tileX, tileY);
                    water[tileY * width + tileX] = surface.IsWater(tileX, tileY)
                        || CarriesTheRiver(surface, tileX, tileY);
                }
            }
            var fallTiles = falls.ConvertAll(tile => new Point((int)tile.X, (int)tile.Y));
            return new TracedRiver
            {
                Reloads = reloads,
                Falls = falls.Count,
                Surfaces = surfaces,
                Width = width,
                Tracing = Task.Run(() => RiverFlowTrace.Trace(width, height, water, fallTiles)),
            };
        }

        /// <summary>The thickest a bridge may be, in tiles, counting every row of it that is not water.
        /// The town's widest crossing is five: a deck along each side and three tiles of ground between.</summary>
        private const int BridgeThicknessTiles = 6;

        /// <summary>Whether a tile that is not water is part of a bridge the river runs under: it lies on
        /// a run of land at most <see cref="BridgeThicknessTiles"/> thick, across or along, with water at
        /// both ends and a deck somewhere in the run.
        ///
        /// <para>A deck alone was the first rule and the town's lower bridges broke it: the grid calls
        /// their top row a deck and their bottom row ground, so a rule that looked for water past the
        /// deck met ground first and the river stopped at the bridge. A spit of bank between two pools
        /// has no deck in it and stays land.</para></summary>
        private static bool CarriesTheRiver(SurfaceMap surface, int tileX, int tileY)
        {
            bool RunIsABridge(int stepX, int stepY)
            {
                bool deck = surface.GetSurface(tileX, tileY) == SurfaceClass.Deck;
                int thickness = 1;
                foreach (int direction in (int[])[-1, 1])
                {
                    bool waterAtThisEnd = false;
                    for (int step = 1; step <= BridgeThicknessTiles; step++)
                    {
                        SurfaceClass there = surface.GetSurface(tileX + stepX * step * direction, tileY + stepY * step * direction);
                        if (there == SurfaceClass.Water)
                        {
                            waterAtThisEnd = true;
                            break;
                        }
                        deck |= there == SurfaceClass.Deck;
                        if (++thickness > BridgeThicknessTiles)
                            return false;
                    }
                    if (!waterAtThisEnd)
                        return false;
                }
                return deck;
            }
            return RunIsABridge(0, 1) || RunIsABridge(1, 0);
        }

        /// <summary>The river last traced on this map, while its falls are looked for again or its
        /// surface grid is being rebuilt. Answering "no river" for those frames dropped the shader's
        /// hold to nothing at once: a content pack re-patching a sheet or the map snapped the river to
        /// still water and back.</summary>
        private TracedRiver? StillFlowingHere(GameLocation location)
        {
            if (!_riversByLocation.TryGetValue(location.NameOrUniqueName ?? "", out TracedRiver? river))
                return null;
            TracedRiver? flowing = river.FlowMap != null && river.Result != null ? river : river.Replacing;
            if (flowing != null)
                river.LastUsedTick = Determinism.Ticks;
            return flowing;
        }

        private static void ForgetReplaced(TracedRiver river)
        {
            river.Replacing?.FlowMap?.Dispose();
            river.Replacing = null;
        }

        private void ForgetOldRivers(string keep)
        {
            while (_riversByLocation.Count > RiversKept)
            {
                string? oldest = null;
                long oldestTick = long.MaxValue;
                foreach ((string name, TracedRiver river) in _riversByLocation)
                {
                    if (name != keep && river.LastUsedTick < oldestTick)
                    {
                        oldestTick = river.LastUsedTick;
                        oldest = name;
                    }
                }
                if (oldest == null)
                    return;
                _riversByLocation[oldest].FlowMap?.Dispose();
                ForgetReplaced(_riversByLocation[oldest]);
                _riversByLocation.Remove(oldest);
            }
        }

        /// <summary>The trace as a texture the shader can read with linear filtering, so a bend is a
        /// curve rather than a staircase and the flow eases off toward each bank. Stored as flow over
        /// FastestFlow, around a middle grey that means still; blue is the foam (RiverFoam in water.fx).</summary>
        /// <param name="bankDrag">How much slower the water beside a bank runs (0..1): a tile's flow is
        /// scaled by how far it is from the nearest tile the river does not reach, full two tiles out
        /// and 35% at the edge, blended by the dial. Baked here rather than in the shader, which has
        /// no room left and would work out the same answer every pixel of every frame.</param>
        private Texture2D BuildFlowMap(RiverFlowTrace.Result trace, float bankDrag)
        {
            float[]? fromBank = bankDrag > 0f ? TilesFromBank(trace) : null;
            var texels = new Color[trace.Width * trace.Height];
            for (int index = 0; index < texels.Length; index++)
            {
                Vector2 flow = trace.Flow[index] / RiverFlowTrace.FastestFlow;
                if (fromBank != null)
                {
                    float ramp = Math.Clamp((fromBank[index] - 0.5f) / 1.5f, 0f, 1f);
                    flow *= MathHelper.Lerp(1f, MathHelper.Lerp(0.35f, 1f, ramp * ramp * (3f - 2f * ramp)), bankDrag);
                }
                texels[index] = new Color(
                    (byte)Math.Clamp(MathF.Round((0.5f + 0.5f * flow.X) * 255f), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round((0.5f + 0.5f * flow.Y) * 255f), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round(trace.Foam[index] * 255f), 0f, 255f), (byte)255);
            }
            var texture = new Texture2D(_device, trace.Width, trace.Height, false, SurfaceFormat.Color);
            texture.SetData(texels);
            return texture;
        }

        /// <summary>Tiles from each tile to the nearest one the river does not reach, by a two-pass
        /// chamfer (1 straight, 1.4 diagonal). The map edge does not count as a bank: a river leaves
        /// the map there at full pace.</summary>
        private static float[] TilesFromBank(RiverFlowTrace.Result trace)
        {
            int width = trace.Width, height = trace.Height;
            var distance = new float[width * height];
            for (int index = 0; index < distance.Length; index++)
                distance[index] = trace.Reached.Length > index && trace.Reached[index] ? float.MaxValue : 0f;
            void Relax(int index, int x, int y, float step)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                    return;
                float through = distance[y * width + x] + step;
                if (through < distance[index])
                    distance[index] = through;
            }
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    Relax(index, x - 1, y, 1f);
                    Relax(index, x, y - 1, 1f);
                    Relax(index, x - 1, y - 1, 1.4f);
                    Relax(index, x + 1, y - 1, 1.4f);
                }
            for (int y = height - 1; y >= 0; y--)
                for (int x = width - 1; x >= 0; x--)
                {
                    int index = y * width + x;
                    Relax(index, x + 1, y, 1f);
                    Relax(index, x, y + 1, 1f);
                    Relax(index, x + 1, y + 1, 1.4f);
                    Relax(index, x - 1, y + 1, 1.4f);
                }
            return distance;
        }

        private void DisposeRiverFlowMaps()
        {
            foreach (TracedRiver river in _riversByLocation.Values)
            {
                river.FlowMap?.Dispose();
                ForgetReplaced(river);
            }
            _riversByLocation.Clear();
            _stillFlowMap?.Dispose();
            _stillFlowMap = null;
        }

        /// <summary>This map's falling water tiles, or null while the sliced walk that finds them is
        /// still running. The walk serves nothing until it has covered the whole map, because a river
        /// traced from half its falls would run the wrong way from the other half.</summary>
        private List<Vector2>? FallingWaterTiles(GameLocation location)
        {
            var map = location.map;
            var firstLayer = map != null && map.Layers.Count > 0 ? map.Layers[0] : null;
            if (LabelStore.Instance == null || firstLayer == null)
                return [];
            MapAnswerKey answerKey = MapAnswerKey.For(location);
            if (answerKey.NoLabels)
                return [];     // no label set loaded: no fall can be known
            WholeMapAnswer<Vector2> answer = AnswerFor(_fallingWaterByLocation, location);
            WholeMapScan.Advance(answer, location, answerKey, firstLayer.LayerHeight, _mapScanBudget,
                                 _fallingWaterRowScanner ??= ScanFallingWaterRow);
            return answer.Key == answerKey ? answer.Found : null;
        }

        /// <summary>One map row's worth of the waterfall walk: the tiles whose art is painted as
        /// falling water. The count is taken across every drawn layer, because a fall is often a
        /// Front-layer sheet standing over a Back-layer pool.</summary>
        private static void ScanFallingWaterRow(GameLocation location, int tileY, List<Vector2> into)
        {
            var labels = LabelStore.Instance;
            var map = location.map;
            if (labels == null || map == null || map.Layers.Count == 0)
                return;
            int mapTilesWide = map.Layers[0].LayerWidth;
            for (int tileX = 0; tileX < mapTilesWide; tileX++)
            {
                int fallingPixels = 0;
                foreach (string layerName in FallingLayers)
                {
                    byte[]? classes = labels.Get(location, tileX, tileY, layerName);
                    if (classes == null)
                        continue;
                    for (int pixelIndex = 0; pixelIndex < classes.Length; pixelIndex++)
                        if (classes[pixelIndex] == LabelClass.Flowing)
                            fallingPixels++;
                    if (fallingPixels >= FallingPixelsPerTile)
                        break;
                }
                if (fallingPixels >= FallingPixelsPerTile && StandsInOpenWater(location, tileX, tileY))
                    into.Add(new Vector2(tileX, tileY));
            }
        }

        /// <summary>Whether a fall has a body of water around it rather than a basin: what tells a
        /// river's waterfall from a fountain (see <see cref="FallingWaterNeighbours"/>).</summary>
        private static bool StandsInOpenWater(GameLocation location, int tileX, int tileY)
        {
            SurfaceMap? surface = SurfaceMap.For(location);
            if (surface == null)
                return false;
            int waterAround = 0;
            for (int y = tileY - FallingWaterNeighbourReach; y <= tileY + FallingWaterNeighbourReach; y++)
                for (int x = tileX - FallingWaterNeighbourReach; x <= tileX + FallingWaterNeighbourReach; x++)
                    if (surface.IsWater(x, y))
                        waterAround++;
            return waterAround >= FallingWaterNeighbours;
        }

        /// <summary>The waterfall walk again, for radiance_report only, with every stage of its
        /// sieve counted apart: a map with no fall found looks the same whether no art on it is
        /// painted as falling, the painted tiles are too thin to count, or they stand in no water.
        /// A few tiles from each stage are named so the spot can be walked to.</summary>
        private static string DescribeFallingWaterSieve(GameLocation? location)
        {
            var labels = LabelStore.Instance;
            var map = location?.map;
            if (location == null || labels == null || map == null || map.Layers.Count == 0)
                return "fall sieve: nothing to walk";
            int mapTilesWide = map.Layers[0].LayerWidth, mapTilesHigh = map.Layers[0].LayerHeight;
            var paintedAtAll = new List<Point>();
            var thickEnough = new List<Point>();
            var inOpenWater = new List<Point>();
            for (int tileY = 0; tileY < mapTilesHigh; tileY++)
            {
                for (int tileX = 0; tileX < mapTilesWide; tileX++)
                {
                    int fallingPixels = 0;
                    foreach (string layerName in FallingLayers)
                    {
                        byte[]? classes = labels.Get(location, tileX, tileY, layerName);
                        if (classes == null)
                            continue;
                        for (int pixelIndex = 0; pixelIndex < classes.Length; pixelIndex++)
                            if (classes[pixelIndex] == LabelClass.Flowing)
                                fallingPixels++;
                    }
                    if (fallingPixels == 0)
                        continue;
                    paintedAtAll.Add(new Point(tileX, tileY));
                    if (fallingPixels < FallingPixelsPerTile)
                        continue;
                    thickEnough.Add(new Point(tileX, tileY));
                    if (StandsInOpenWater(location, tileX, tileY))
                        inOpenWater.Add(new Point(tileX, tileY));
                }
            }
            static string Some(List<Point> tiles) =>
                tiles.Count == 0 ? "" : " e.g. " + string.Join(" ", tiles.GetRange(0, Math.Min(4, tiles.Count)).ConvertAll(t => $"{t.X},{t.Y}"));
            return $"fall sieve: {paintedAtAll.Count} tiles carry falling paint{Some(paintedAtAll)}; "
                 + $"{thickEnough.Count} have a quarter tile of it{Some(thickEnough)}; "
                 + $"{inOpenWater.Count} stand in open water{Some(inOpenWater)}";
        }

        /// <summary>The traced river as a map of letters, one per tile, written beside the report:
        /// where a river stops short, the letter at the break says what the grid thinks is there.
        /// ~ water the river reaches, w water it does not, = a deck it crosses, d a deck it does
        /// not, . ground, # wall, ^ roof, o void, g glass, F a fall.</summary>
        private static void WriteRiverMap(TracedRiver river, List<Vector2>? falls, string folder)
        {
            if (river.Result == null || river.Surfaces.Length == 0)
                return;
            RiverFlowTrace.Result trace = river.Result;
            var fallTiles = new HashSet<int>();
            if (falls != null)
                foreach (Vector2 fall in falls)
                    fallTiles.Add((int)fall.Y * trace.Width + (int)fall.X);
            var text = new System.Text.StringBuilder();
            text.AppendLine($"{Game1.currentLocation?.NameOrUniqueName} {trace.Width}x{trace.Height}, "
                          + $"reached {trace.ReachedTiles} of {trace.WaterTiles} water tiles; columns every 10 tiles below");
            for (int tileY = 0; tileY < trace.Height; tileY++)
            {
                text.Append($"{tileY,3} ");
                for (int tileX = 0; tileX < trace.Width; tileX++)
                {
                    int index = tileY * trace.Width + tileX;
                    bool reached = trace.Reached.Length > index && trace.Reached[index];
                    text.Append(fallTiles.Contains(index) ? 'F' : river.Surfaces[index] switch
                    {
                        SurfaceClass.Water => reached ? '~' : 'w',
                        SurfaceClass.Deck => reached ? '=' : 'd',
                        SurfaceClass.Wall => '#',
                        SurfaceClass.Roof => '^',
                        SurfaceClass.Void => 'o',
                        SurfaceClass.Glass => 'g',
                        _ => '.',
                    });
                }
                text.AppendLine();
            }
            text.AppendLine();
            text.AppendLine("pace per tile: 0 still, 6 the river's middle pace, 9 at 1.5x or faster, blank where no river runs");
            for (int tileY = 0; tileY < trace.Height; tileY++)
            {
                text.Append($"{tileY,3} ");
                for (int tileX = 0; tileX < trace.Width; tileX++)
                {
                    int index = tileY * trace.Width + tileX;
                    bool reached = trace.Reached.Length > index && trace.Reached[index];
                    text.Append(reached ? (char)('0' + Math.Clamp((int)MathF.Round(trace.Flow[index].Length() * 6f), 0, 9)) : ' ');
                }
                text.AppendLine();
            }
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "radiance-river-map.txt"), text.ToString());
        }

        /// <summary>The current block of radiance_report.</summary>
        private string DescribeWaterCurrent()
        {
            GameLocation? location = Game1.currentLocation;
            TracedRiver? river = RiverFor(location);
            if (river != null && location != null)
            {
                try
                {
                    WriteRiverMap(river, FallingWaterTiles(location),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Radiance-Dumps"));
                }
                catch (Exception error)
                {
                    _lastRiverReading += $" (the river map could not be written: {error.Message})";
                }
            }
            string underPlayer = "none";
            if (river?.Result != null && Game1.player != null)
            {
                Point tile = Game1.player.TilePoint;
                RiverFlowTrace.Result trace = river.Result;
                if (tile.X >= 0 && tile.Y >= 0 && tile.X < trace.Width && tile.Y < trace.Height)
                {
                    Vector2 flow = trace.Flow[tile.Y * trace.Width + tile.X];
                    underPlayer = $"{flow.X:F2},{flow.Y:F2} at {tile.X},{tile.Y}";
                }
            }
            return $"{(_lastConfig?.WaterRiverFlowEnabled ?? true ? "" : "rivers switched off, ")}[{_lastRiverReading}], "
                 + $"hold {_riverHoldEased:F2} (1 = the waves stand and only the river carries them), "
                 + $"pace {_riverPaceEased:F2} tiles/s (weather swell x{_riverSwellEased:F2}), flow at the player's tile {underPlayer}, "
                 + $"game water scroll x{GameWaterScrollHere(river):F2} (1 = up the screen as the game draws it, -1 = down), "
                 + $"dial {(_lastConfig == null ? 0f : _lastConfig.WaterCurrent):F2}, "
                 + $"[{DescribeFallingWaterSieve(location)}]"
                 + DescribeRiverPerScreen();
        }

        /// <summary>Split screen only: every screen's own river hold and pace, and the game water
        /// scroll set for each location. The line above is the screen the report was typed on, and
        /// "the second player's river does not flow" cannot be answered from that alone.</summary>
        private string DescribeRiverPerScreen()
        {
            if (_screenStates.Count < 2)
                return "";
            var screens = new List<string>();
            foreach ((int screenId, ScreenState state) in _screenStates)
                screens.Add($"screen {screenId} hold {state.RiverHoldEase:F2} pace {state.RiverPaceEase:F2}");
            return $", per screen: {string.Join("; ", screens)}; game water scroll by location: {HarmonyPatcher.DescribeGameWaterScroll()}";
        }
    }
}
