using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;

namespace SDVRadiance
{
    /// <summary>
    /// Which way, and how fast, a river's water goes at every tile of its map, traced from the
    /// map's own banks.
    ///
    /// <para>One heading for the whole map was the first cut, and a stream that runs down from its
    /// fall and then bends west still ran straight down across the bend. What a river actually does
    /// is follow its bed from where the water comes in to where it leaves, so that is what is
    /// traced. The water comes in at the falls. It leaves wherever the channel runs off the edge of
    /// the map. In between, the flow is the one a real channel settles into: a potential that is 0
    /// at the falls and 1 where the water leaves, relaxed over the water tiles until every tile is
    /// the average of the water around it (Laplace's equation, with the banks letting nothing
    /// through). Its gradient runs along the channel and turns with every bend, and a dead-end pool
    /// off the side of the stream gets almost none of it, because nothing flows through a pool with
    /// only one way in.</para>
    ///
    /// <para>Its size is what a real river's speed would be, water squeezing through a narrow bed and
    /// idling across a wide one, and taken as it is that looked wrong: the town's river pours into a
    /// lake that leaves the map through five tiles, so nearly the whole drop gathered at that outlet
    /// and the stream above it crawled, piling against its bridges. So the speed is measured against
    /// the river's own middle pace and held between <see cref="SlowestFlowing"/> and
    /// <see cref="NarrowsFastest"/>: a narrows still runs faster and a wide reach slower, but no stretch
    /// of the river crawls and no outlet races. Only the pools the flow passes by rather than
    /// through fade to still.</para>
    ///
    /// <para>A map whose river never reaches an edge (a fall into a closed pool, as in a cave) has
    /// nowhere for the water to go: it moves away from the falls where they land and settles to still
    /// within the rush's reach, instead of running across the whole pool like a river.</para>
    ///
    /// <para>Pure data in, pure data out, so it runs off the draw thread: the relaxation is a few
    /// thousand sweeps over a few thousand tiles, which is milliseconds a frame cannot spare.</para>
    /// </summary>
    internal static class RiverFlowTrace
    {
        internal sealed class Result
        {
            internal int Width;
            internal int Height;
            /// <summary>Flow per tile, row by row, in multiples of the river's pace; zero on land and on
            /// water no fall reaches.</summary>
            internal Vector2[] Flow = [];
            /// <summary>Whether the water from the falls gets to each tile at all.</summary>
            internal bool[] Reached = [];
            /// <summary>How thick the foam is per tile beyond the light scatter every river gets, 0 to 1:
            /// under a fall and fading downstream, and where a narrows runs fast.</summary>
            internal float[] Foam = [];
            internal int WaterTiles;
            internal int ReachedTiles;
            internal int SourceTiles;
            internal int LeavingTiles;
            internal bool TracedToAnEdge;
            internal int RelaxationSweeps;
            internal double Milliseconds;
        }

        /// <summary>How far round a fall its water counts as just arrived, in tiles. The painted fall
        /// sits on the cliff face and its pool starts a tile or two below it.</summary>
        private const int FallReach = 2;

        /// <summary>How far through the water an edge tile has to be from the falls to count as where
        /// the river leaves. A fall at the very top of a map touches the top edge itself, and that
        /// edge is where the water comes in, not out.</summary>
        private const float LeavesAtLeastThisFarFromAFall = 10f;

        private const int MostRelaxationSweeps = 6000;
        private const double SettledChangePerSweep = 1e-6;

        /// <summary>Successive over-relaxation: each sweep overshoots the average by this much, which
        /// settles a long thin channel in hundreds of sweeps instead of tens of thousands.</summary>
        private const double OverRelaxation = 1.9;

        /// <summary>How much longer than the shortest way from a fall to the edge a path may be and still
        /// count as the river's main run, whose slope says what "flowing" is on this map.</summary>
        private const float MainRunLengthShare = 1.25f;

        /// <summary>Below this share of the main run's slope the water slows toward still: the pools the
        /// river passes by rather than through.</summary>
        private const float StillBelowShareOfMainRun = 0.35f;

        /// <summary>How far downstream of a fall its foam lasts, in tiles through the water.</summary>
        private const float FoamReachFromAFallTiles = 10f;

        /// <summary>The foam a narrows adds at the fastest pace, as a share of the foam under a fall.</summary>
        private const float FoamOfTheFastestNarrows = 0.6f;

        /// <summary>The fastest a narrows may run, in multiples of the river's middle pace.</summary>
        private const float NarrowsFastest = 1.5f;

        /// <summary>The slowest a stretch the river runs through may go, in multiples of its middle pace.</summary>
        private const float SlowestFlowing = 0.6f;

        /// <summary>The fastest any water may run, in multiples of the river's middle pace: the rush at the
        /// foot of a fall, or a narrows. The flow map stores up to this and no further (FLOW_MAP_RANGE in
        /// water.fx).</summary>
        internal const float FastestFlow = 2f;

        /// <summary>How fast the water leaves the foot of a fall, in multiples of the middle pace, and how
        /// far through the water it takes to settle back to the river's own pace. A fall drops the river's
        /// water into its pool with speed that the pool has not taken off yet.</summary>
        private const float FallRushPace = 1.8f;
        private const float FallRushReachTiles = 10f;

        internal static Result Trace(int width, int height, bool[] water, IReadOnlyList<Point> falls)
        {
            var stopwatch = Stopwatch.StartNew();
            int tileCount = width * height;
            // Every array is the map's size from the start: a trace that stops early (no fall standing
            // in water) is still read tile by tile, and the empty arrays it used to hand back threw
            // out of the flow map build on every frame, which turned the whole chain off on that map.
            var result = new Result
            {
                Width = width, Height = height,
                Flow = new Vector2[tileCount], Reached = new bool[tileCount], Foam = new float[tileCount],
            };

            var sources = new List<int>();
            var isSource = new bool[tileCount];
            foreach (Point fall in falls)
            {
                for (int y = fall.Y - FallReach; y <= fall.Y + FallReach; y++)
                {
                    for (int x = fall.X - FallReach; x <= fall.X + FallReach; x++)
                    {
                        if (x < 0 || y < 0 || x >= width || y >= height)
                            continue;
                        int index = y * width + x;
                        if (water[index] && !isSource[index])
                        {
                            isSource[index] = true;
                            sources.Add(index);
                        }
                    }
                }
            }
            result.SourceTiles = sources.Count;
            if (sources.Count == 0)
                return Finish(result, stopwatch);

            float[] fromFalls = WaterDistances(width, height, water, sources);

            var leaving = new List<int>();
            var isLeaving = new bool[tileCount];
            for (int index = 0; index < tileCount; index++)
            {
                int x = index % width, y = index / width;
                bool onEdge = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (onEdge && water[index] && !isSource[index] && fromFalls[index] >= LeavesAtLeastThisFarFromAFall
                    && !float.IsPositiveInfinity(fromFalls[index]))
                {
                    isLeaving[index] = true;
                    leaving.Add(index);
                }
            }
            result.LeavingTiles = leaving.Count;

            bool Reached(int index) => water[index] && !float.IsPositiveInfinity(fromFalls[index]);
            for (int index = 0; index < tileCount; index++)
            {
                if (water[index])
                    result.WaterTiles++;
                result.Reached[index] = Reached(index);
                if (result.Reached[index])
                    result.ReachedTiles++;
            }

            if (leaving.Count > 0)
            {
                float[] toLeaving = WaterDistances(width, height, water, leaving);
                var potential = new double[tileCount];
                var free = new List<int>();
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Reached(index))
                        continue;
                    if (isSource[index])
                        potential[index] = 0.0;
                    else if (isLeaving[index])
                        potential[index] = 1.0;
                    else
                    {
                        // Start from the ratio of the two distances, which is already close to the
                        // answer along the main channel, so the relaxation only has to settle the pools.
                        double along = fromFalls[index], left = toLeaving[index];
                        potential[index] = double.IsPositiveInfinity(left) ? 0.0 : along / (along + left);
                        free.Add(index);
                    }
                }

                for (int sweep = 0; sweep < MostRelaxationSweeps; sweep++)
                {
                    double largestChange = 0.0;
                    foreach (int index in free)
                    {
                        int x = index % width, y = index / width;
                        double sum = 0.0;
                        int count = 0;
                        if (x > 0 && Reached(index - 1)) { sum += potential[index - 1]; count++; }
                        if (x < width - 1 && Reached(index + 1)) { sum += potential[index + 1]; count++; }
                        if (y > 0 && Reached(index - width)) { sum += potential[index - width]; count++; }
                        if (y < height - 1 && Reached(index + width)) { sum += potential[index + width]; count++; }
                        if (count == 0)
                            continue;
                        double change = sum / count - potential[index];
                        potential[index] += OverRelaxation * change;
                        largestChange = Math.Max(largestChange, Math.Abs(change));
                    }
                    result.RelaxationSweeps = sweep + 1;
                    if (largestChange < SettledChangePerSweep)
                        break;
                }

                for (int index = 0; index < tileCount; index++)
                {
                    if (Reached(index))
                        result.Flow[index] = Gradient(width, height, index, Reached, i => potential[i]);
                }
                result.TracedToAnEdge = true;
                EvenThePace(result.Flow, tileCount, Reached, fromFalls, toLeaving);
            }
            else
            {
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Reached(index))
                        continue;
                    Vector2 away = Gradient(width, height, index, Reached, i => fromFalls[i]);
                    if (away.LengthSquared() > 0.000001f)
                        away.Normalize();
                    result.Flow[index] = away;
                }
            }

            // Where the water comes in, every tile of the potential is the same 0 and has no slope,
            // so the pool under a fall stood still. Water there is falling, and in this game's art it
            // falls down the screen.
            foreach (int source in sources)
                result.Flow[source] = new Vector2(0f, 1f);
            Smooth(result.Flow, width, height, Reached);

            for (int index = 0; index < tileCount; index++)
            {
                if (!Reached(index))
                    continue;
                float nearAFall = Math.Clamp(1f - fromFalls[index] / FallRushReachTiles, 0f, 1f);
                nearAFall = nearAFall * nearAFall * (3f - 2f * nearAFall);
                // A river carries its water on past the rush. A pool with no way out does not: the
                // water churns where the fall lands and is still beyond it, so there the rush is all
                // the flow there is. A cave pool ran like a river before this.
                result.Flow[index] *= result.TracedToAnEdge
                    ? 1f + (FallRushPace - 1f) * nearAFall
                    : FallRushPace * nearAFall;
            }

            for (int index = 0; index < tileCount; index++)
            {
                if (!Reached(index))
                    continue;
                float underAFall = Math.Clamp(1f - fromFalls[index] / FoamReachFromAFallTiles, 0f, 1f);
                float narrows = Math.Clamp((result.Flow[index].Length() - 1f) / (NarrowsFastest - 1f), 0f, 1f) * FoamOfTheFastestNarrows;
                result.Foam[index] = Math.Max(underAFall * underAFall, narrows);
            }
            for (int index = 0; index < tileCount; index++)
            {
                float length = result.Flow[index].Length();
                if (length > FastestFlow)
                    result.Flow[index] *= FastestFlow / length;
            }
            return Finish(result, stopwatch);
        }

        private static Result Finish(Result result, Stopwatch stopwatch)
        {
            result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>Distance through the water from the nearest seed, in tiles. A diagonal step is
        /// allowed only where both tiles beside it are water, so the distance cannot leak across the
        /// corner of a bank.</summary>
        private static float[] WaterDistances(int width, int height, bool[] water, List<int> seeds)
        {
            var distance = new float[width * height];
            Array.Fill(distance, float.PositiveInfinity);
            var queue = new PriorityQueue<int, float>();
            foreach (int seed in seeds)
            {
                distance[seed] = 0f;
                queue.Enqueue(seed, 0f);
            }
            while (queue.TryDequeue(out int index, out float reachedAt))
            {
                if (reachedAt > distance[index])
                    continue;
                int x = index % width, y = index / width;
                for (int stepY = -1; stepY <= 1; stepY++)
                {
                    for (int stepX = -1; stepX <= 1; stepX++)
                    {
                        if (stepX == 0 && stepY == 0)
                            continue;
                        int nextX = x + stepX, nextY = y + stepY;
                        if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                            continue;
                        int next = nextY * width + nextX;
                        if (!water[next])
                            continue;
                        bool diagonal = stepX != 0 && stepY != 0;
                        if (diagonal && (!water[y * width + nextX] || !water[nextY * width + x]))
                            continue;
                        float through = reachedAt + (diagonal ? 1.41421356f : 1f);
                        if (through < distance[next])
                        {
                            distance[next] = through;
                            queue.Enqueue(next, through);
                        }
                    }
                }
            }
            return distance;
        }

        /// <summary>The slope of a field at one tile, read from whichever of its neighbours are water:
        /// both sides where it can, one side against a bank, none where it is walled in.</summary>
        private static Vector2 Gradient(int width, int height, int index, Func<int, bool> reached, Func<int, double> field)
        {
            int x = index % width, y = index / width;
            double here = field(index);
            return new Vector2(
                (float)Slope(here, x > 0 && reached(index - 1) ? field(index - 1) : null,
                                   x < width - 1 && reached(index + 1) ? field(index + 1) : null),
                (float)Slope(here, y > 0 && reached(index - width) ? field(index - width) : null,
                                   y < height - 1 && reached(index + width) ? field(index + width) : null));
        }

        private static double Slope(double here, double? before, double? after)
        {
            if (before.HasValue && after.HasValue)
                return (after.Value - before.Value) * 0.5;
            if (after.HasValue)
                return after.Value - here;
            if (before.HasValue)
                return here - before.Value;
            return 0.0;
        }

        /// <summary>The river's pace per tile, measured against its main run's middle slope and held
        /// between <see cref="SlowestFlowing"/> and <see cref="NarrowsFastest"/>; below
        /// <see cref="StillBelowShareOfMainRun"/> of that slope it eases down to still (see the class
        /// summary for why the slope's own size is not used as it is).</summary>
        private static void EvenThePace(Vector2[] flow, int tileCount, Func<int, bool> reached, float[] fromFalls, float[] toLeaving)
        {
            float shortestRun = float.PositiveInfinity;
            for (int index = 0; index < tileCount; index++)
                if (reached(index))
                    shortestRun = Math.Min(shortestRun, fromFalls[index] + toLeaving[index]);
            if (float.IsPositiveInfinity(shortestRun))
                return;
            var mainRunSlopes = new List<float>();
            for (int index = 0; index < tileCount; index++)
                if (reached(index) && fromFalls[index] + toLeaving[index] <= shortestRun * MainRunLengthShare)
                    mainRunSlopes.Add(flow[index].Length());
            if (mainRunSlopes.Count == 0)
                return;
            mainRunSlopes.Sort();
            float mainRunSlope = mainRunSlopes[mainRunSlopes.Count / 2];
            if (mainRunSlope <= 0f)
                return;
            for (int index = 0; index < tileCount; index++)
            {
                float slope = flow[index].Length();
                if (slope <= 0f)
                    continue;
                float ofMainRun = slope / mainRunSlope;
                float pace = ofMainRun < StillBelowShareOfMainRun
                    ? SlowestFlowing * ofMainRun / StillBelowShareOfMainRun
                    : Math.Clamp(ofMainRun, SlowestFlowing, NarrowsFastest);
                flow[index] *= pace / slope;
            }
        }

        /// <summary>One pass of averaging over the water around each tile, which takes the stairs a
        /// tile grid leaves in a diagonal bend out of the flow.</summary>
        private static void Smooth(Vector2[] flow, int width, int height, Func<int, bool> reached)
        {
            var smoothed = new Vector2[flow.Length];
            for (int index = 0; index < flow.Length; index++)
            {
                if (!reached(index))
                    continue;
                int x = index % width, y = index / width;
                Vector2 sum = Vector2.Zero;
                int count = 0;
                for (int nearY = Math.Max(0, y - 1); nearY <= Math.Min(height - 1, y + 1); nearY++)
                {
                    for (int nearX = Math.Max(0, x - 1); nearX <= Math.Min(width - 1, x + 1); nearX++)
                    {
                        int near = nearY * width + nearX;
                        if (!reached(near))
                            continue;
                        sum += flow[near];
                        count++;
                    }
                }
                smoothed[index] = sum / count;
            }
            Array.Copy(smoothed, flow, flow.Length);
        }
    }
}
