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
    /// <para>A river can also run INTO a fall: the one through the Forest comes in from the town and
    /// goes over the falls by the hat shop. The water above a fall's lip is traced on its own, from
    /// where it comes onto the map down to the lip, so it is not mistaken for water the fall has
    /// just dropped and sent back up the river.</para>
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
            /// <summary>Water above a fall's lip: the river on its way to the fall, which runs into it.</summary>
            internal int UpstreamTiles;
            /// <summary>Where that river comes onto the map, at its edge.</summary>
            internal int ArrivingTiles;
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

            // Where each fall's run of falling tiles starts, per column: the lip the water goes over.
            var isFall = new bool[tileCount];
            foreach (Point fall in falls)
                if (fall.X >= 0 && fall.Y >= 0 && fall.X < width && fall.Y < height)
                    isFall[fall.Y * width + fall.X] = true;
            int LipRowOf(Point fall)
            {
                int y = fall.Y;
                while (y > 0 && isFall[(y - 1) * width + fall.X])
                    y--;
                return y;
            }

            var sources = new List<int>();
            var isSource = new bool[tileCount];
            // Only from the lip down, when the fall has a river above it. The water ABOVE the lip
            // is that river on its way to the fall, not water the fall has just dropped; counted as
            // arrived, the river that runs into the Forest's falls from the town ran back up toward
            // the town (pkmn418, Nexus, October 2026). A fall with no river above it (one coming in
            // at the map's edge, a fountain in its basin) keeps its whole reach as before.
            void FindSources(bool[]? riverAbove)
            {
                sources.Clear();
                Array.Clear(isSource);
                foreach (Point fall in falls)
                {
                    int lipRow = LipRowOf(fall);
                    bool cut = riverAbove == null
                        || (lipRow > 0 && fall.X >= 0 && fall.X < width && riverAbove[(lipRow - 1) * width + fall.X]);
                    for (int y = cut ? Math.Max(fall.Y - FallReach, lipRow) : fall.Y - FallReach; y <= fall.Y + FallReach; y++)
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
            }
            FindSources(null);
            if (sources.Count == 0)
                return Finish(result, stopwatch);

            // The lips: water standing just above where a fall begins, which is where the river
            // above it goes over. Everything that reaches a lip without passing the water below a
            // fall is that river: it runs INTO the fall, from wherever it comes onto the map.
            var lips = new List<int>();
            var isLip = new bool[tileCount];
            foreach (Point fall in falls)
            {
                if (fall.Y < 1 || fall.X < 0 || fall.X >= width || fall.Y >= height || LipRowOf(fall) != fall.Y)
                    continue;
                int above = (fall.Y - 1) * width + fall.X;
                if (water[above] && !isSource[above] && !isLip[above])
                {
                    isLip[above] = true;
                    lips.Add(above);
                }
            }
            var upstreamWater = new bool[tileCount];
            var isUpstream = new bool[tileCount];
            float[] toLip = [];
            if (lips.Count > 0)
            {
                for (int index = 0; index < tileCount; index++)
                    upstreamWater[index] = water[index] && !isSource[index];
                toLip = WaterDistances(width, height, upstreamWater, lips);
                for (int index = 0; index < tileCount; index++)
                    isUpstream[index] = upstreamWater[index] && !float.IsPositiveInfinity(toLip[index]);
                KeepOnlyWaterAboveItsFalls(width, height, isUpstream, lips, isLip, isFall);
                // Falls whose lip turned out to have no river above it take their full reach back.
                FindSources(isLip);
                for (int index = 0; index < tileCount; index++)
                {
                    if (!isSource[index])
                        continue;
                    isUpstream[index] = false;
                    isLip[index] = false;
                }
                lips.RemoveAll(lip => !isLip[lip]);
                for (int index = 0; index < tileCount; index++)
                    upstreamWater[index] = isUpstream[index];
                toLip = WaterDistances(width, height, upstreamWater, lips);
                for (int index = 0; index < tileCount; index++)
                {
                    isUpstream[index] &= !float.IsPositiveInfinity(toLip[index]);
                    upstreamWater[index] = isUpstream[index];
                }
            }
            result.SourceTiles = sources.Count;

            var seeds = new List<int>(sources);
            seeds.AddRange(lips);
            float[] fromFalls = WaterDistances(width, height, water, seeds);

            var leaving = new List<int>();
            var isLeaving = new bool[tileCount];
            var arriving = new List<int>();
            var isArriving = new bool[tileCount];
            for (int index = 0; index < tileCount; index++)
            {
                int x = index % width, y = index / width;
                bool onEdge = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (!onEdge || !water[index] || isSource[index] || float.IsPositiveInfinity(fromFalls[index]))
                    continue;
                if (isUpstream[index])
                {
                    if (!isLip[index])
                    {
                        isArriving[index] = true;
                        arriving.Add(index);
                    }
                }
                else if (fromFalls[index] >= LeavesAtLeastThisFarFromAFall)
                {
                    isLeaving[index] = true;
                    leaving.Add(index);
                }
            }
            result.LeavingTiles = leaving.Count;
            result.ArrivingTiles = arriving.Count;

            bool Reached(int index) => water[index] && !float.IsPositiveInfinity(fromFalls[index]);
            // Below the falls and above them are traced apart: the two only meet at a lip, where one
            // goes over the other, and averaged together the river above would be pulled back by the
            // pool below.
            bool Below(int index) => Reached(index) && !isUpstream[index];
            bool Above(int index) => isUpstream[index];
            for (int index = 0; index < tileCount; index++)
            {
                if (water[index])
                    result.WaterTiles++;
                result.Reached[index] = Reached(index);
                if (result.Reached[index])
                    result.ReachedTiles++;
                if (isUpstream[index])
                    result.UpstreamTiles++;
            }

            var closedPool = new bool[tileCount];
            if (leaving.Count > 0)
            {
                var belowWater = new bool[tileCount];
                for (int index = 0; index < tileCount; index++)
                    belowWater[index] = water[index] && !isUpstream[index];
                float[] toLeaving = WaterDistances(width, height, belowWater, leaving);
                var potential = new double[tileCount];
                var free = new List<int>();
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Below(index))
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

                result.RelaxationSweeps = Relax(potential, free, width, height, Below);

                for (int index = 0; index < tileCount; index++)
                {
                    if (Below(index))
                        result.Flow[index] = Gradient(width, height, index, Below, i => potential[i]);
                }
                result.TracedToAnEdge = true;
                EvenThePace(result.Flow, tileCount, Below, fromFalls, toLeaving);
                // Water below a fall that cannot reach any way off the map (a pool, or a short
                // stub leaving by an edge too near its fall to count) has no slope in the potential
                // and stood still; it runs away from the fall and settles, as a pool does.
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Below(index) || !float.IsPositiveInfinity(toLeaving[index]))
                        continue;
                    closedPool[index] = true;
                    Vector2 away = Gradient(width, height, index, Below, i => fromFalls[i]);
                    if (away.LengthSquared() > 0.000001f)
                        away.Normalize();
                    result.Flow[index] = away;
                }
            }
            else
            {
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Below(index))
                        continue;
                    Vector2 away = Gradient(width, height, index, Below, i => fromFalls[i]);
                    if (away.LengthSquared() > 0.000001f)
                        away.Normalize();
                    result.Flow[index] = away;
                }
            }

            // The river above a fall: 0 where it comes onto the map, 1 at the lips, relaxed the same
            // way, so it runs from the edge it enters by down to the fall it goes over.
            if (arriving.Count > 0)
            {
                float[] fromArriving = WaterDistances(width, height, upstreamWater, arriving);
                var potential = new double[tileCount];
                var free = new List<int>();
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Above(index))
                        continue;
                    if (isLip[index])
                        potential[index] = 1.0;
                    else if (isArriving[index])
                        potential[index] = 0.0;
                    else
                    {
                        double along = fromArriving[index], left = toLip[index];
                        potential[index] = double.IsPositiveInfinity(along) ? 1.0 : along / (along + left);
                        free.Add(index);
                    }
                }
                result.RelaxationSweeps = Math.Max(result.RelaxationSweeps, Relax(potential, free, width, height, Above));
                for (int index = 0; index < tileCount; index++)
                {
                    if (Above(index))
                        result.Flow[index] = Gradient(width, height, index, Above, i => potential[i]);
                }
                EvenThePace(result.Flow, tileCount, Above, fromArriving, toLip);
            }
            {
                // Water above a fall with no way onto the map (a spring, a stream between two falls,
                // or a fall at the map's top edge with a row of water over it) simply heads for the
                // lip. Relaxed with nowhere to start from, such a stretch settles flat and stands still.
                float[] fromArriving = arriving.Count > 0 ? WaterDistances(width, height, upstreamWater, arriving) : [];
                for (int index = 0; index < tileCount; index++)
                {
                    if (!Above(index) || (arriving.Count > 0 && !float.IsPositiveInfinity(fromArriving[index])))
                        continue;
                    Vector2 toward = -Gradient(width, height, index, Above, i => toLip[i]);
                    if (toward.LengthSquared() > 0.000001f)
                        toward.Normalize();
                    result.Flow[index] = toward;
                }
            }
            // At the lip the water is going over, and in this game's art a fall runs down the screen.
            foreach (int lip in lips)
                result.Flow[lip] = new Vector2(0f, 1f);

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
                // Water above a fall always has a way out: over the lip. A stream between two falls
                // with no edge to leave by stood still in its middle otherwise.
                result.Flow[index] *= isUpstream[index] || (result.TracedToAnEdge && !closedPool[index])
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

        /// <summary>
        /// A stretch of water that reaches a lip is the river going over it only if all of it lies
        /// above the bottom of the fall. A fountain is painted as a fall standing in its own basin,
        /// and the basin's water north of the jet reaches round its sides to the south: that is one
        /// pool the jet stands in, and it keeps running away from the jet as it always did.
        /// </summary>
        private static void KeepOnlyWaterAboveItsFalls(int width, int height, bool[] isUpstream, List<int> lips, bool[] isLip,
            bool[] isFall)
        {
            int tileCount = width * height;
            var stretchOf = new int[tileCount];
            Array.Fill(stretchOf, -1);
            var lowestFallRow = new List<int>();
            var deepestRow = new List<int>();
            var queue = new Queue<int>();
            foreach (int lip in lips)
            {
                if (stretchOf[lip] >= 0)
                    continue;
                int stretch = lowestFallRow.Count;
                lowestFallRow.Add(-1);
                deepestRow.Add(-1);
                stretchOf[lip] = stretch;
                queue.Enqueue(lip);
                while (queue.TryDequeue(out int index))
                {
                    int x = index % width, y = index / width;
                    deepestRow[stretch] = Math.Max(deepestRow[stretch], y);
                    if (isLip[index])
                    {
                        int fallRow = y + 1;
                        while (fallRow + 1 < height && isFall[(fallRow + 1) * width + x])
                            fallRow++;
                        lowestFallRow[stretch] = Math.Max(lowestFallRow[stretch], fallRow);
                    }
                    if (x > 0) Visit(index - 1);
                    if (x < width - 1) Visit(index + 1);
                    if (y > 0) Visit(index - width);
                    if (y < height - 1) Visit(index + width);
                }

                void Visit(int next)
                {
                    if (isUpstream[next] && stretchOf[next] < 0)
                    {
                        stretchOf[next] = stretch;
                        queue.Enqueue(next);
                    }
                }
            }
            bool anyDropped = false;
            for (int index = 0; index < tileCount; index++)
            {
                if (!isUpstream[index])
                    continue;
                int stretch = stretchOf[index];
                if (stretch < 0 || deepestRow[stretch] > lowestFallRow[stretch])
                {
                    isUpstream[index] = false;
                    anyDropped = true;
                }
            }
            if (!anyDropped)
                return;
            for (int index = 0; index < tileCount; index++)
                if (isLip[index] && !isUpstream[index])
                    isLip[index] = false;
        }

        /// <summary>Settle a potential over one stretch of water until every free tile is the average
        /// of its neighbours in the same stretch; returns the sweeps it took.</summary>
        private static int Relax(double[] potential, List<int> free, int width, int height, Func<int, bool> inStretch)
        {
            int sweeps = 0;
            for (int sweep = 0; sweep < MostRelaxationSweeps; sweep++)
            {
                double largestChange = 0.0;
                foreach (int index in free)
                {
                    int x = index % width, y = index / width;
                    double sum = 0.0;
                    int count = 0;
                    if (x > 0 && inStretch(index - 1)) { sum += potential[index - 1]; count++; }
                    if (x < width - 1 && inStretch(index + 1)) { sum += potential[index + 1]; count++; }
                    if (y > 0 && inStretch(index - width)) { sum += potential[index - width]; count++; }
                    if (y < height - 1 && inStretch(index + width)) { sum += potential[index + width]; count++; }
                    if (count == 0)
                        continue;
                    double change = sum / count - potential[index];
                    potential[index] += OverRelaxation * change;
                    largestChange = Math.Max(largestChange, Math.Abs(change));
                }
                sweeps = sweep + 1;
                if (largestChange < SettledChangePerSweep)
                    break;
            }
            return sweeps;
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
