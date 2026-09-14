using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline — the wind, carried into the water.
    ///
    /// Everything else in the weather already leans with one shared wind: the rain slants along
    /// it, the tree crowns and the grass sway with it, the leaves ride it. The water was the one
    /// surface that knew nothing about it and rippled the same way on a still morning as in a
    /// gale. This half of the feature is the bookkeeping: it reads that same wind, carries the
    /// surface pattern downwind a little further every tick, and hands the shader how far it has
    /// been carried and how hard it is blowing. Nothing here touches a pixel.
    ///
    /// The drift is world state, not per-screen state, so it is stepped exactly once per tick
    /// however many split screens ask for it, the same way the wake rings are. While the harness
    /// is frozen the tick never changes and the drift holds, which is what makes a frozen frame
    /// the same frame twice.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>The wind speed that counts as full, in the shared wind's own units (buffer
        /// pixels per second). The same figure the foliage sway calls full, so a gale that has the
        /// trees at full lean has the water at full drift.</summary>
        private const float FullWindPixelsPerSecond = 200f;

        /// <summary>How fast the surface pattern travels downwind at full wind and the dial at 1,
        /// in world tiles per second. The ripple's own crests already run at about 0.98 tiles a
        /// second (a phase speed of 4.0 radians per second over 4.1 radians per tile), so a little
        /// under half of that reads as water leaning with the wind rather than as a current.</summary>
        private const float FullWindDriftTilesPerSecond = 0.45f;

        /// <summary>How much of the drift runs across the wind as well as along it. The ripple is
        /// two crossing wave families, one running along each axis, and a drift on one axis alone
        /// leaves half the surface standing still.</summary>
        private const float WindDriftCrossShare = 0.4f;

        /// <summary>Where the drift is folded back to the start, in world tiles. Every frequency in
        /// the ripple is a tenth (6.3, 4.1, 5.7, 4.7, 2.1, 1.4 radians per tile) and a tenth of
        /// this is exactly two pi, so the pattern lands back on itself and the fold cannot be seen.
        /// The cat's paw noise is scaled to fold on a whole cell for the same reason (see
        /// WIND_PATCH_SCALE in water.fx). Folding at all is what keeps the numbers the shader takes
        /// a sine of small enough to stay smooth after an evening of play.</summary>
        private const float WindDriftWrapTiles = 62.831853f;

        /// <summary>Seconds for the strength to reach a new setting. The shared wind itself already
        /// drifts rather than snaps; this is here for the dial, which can go from 0 to 2 in one
        /// click, and for the house rule that nothing pops.</summary>
        private const float WindEaseSeconds = 0.5f;

        /// <summary>Ticks of catching up the drift will do in one step. A day change or a long
        /// menu leaves a gap that is not time the water spent being blown across.</summary>
        private const int WindMaxCatchUpTicks = 6;

        /// <summary>How far the surface pattern has been carried, in world tiles, folded into
        /// [0, WindDriftWrapTiles). Kept as doubles because it is a running sum: a float's step
        /// gets coarse enough near the top of the range to swallow a single tick's worth.</summary>
        private double _windDriftColumnTiles;
        private double _windDriftRowTiles;
        private int _windDriftTick = -1;

        /// <summary>How hard it is blowing, 0 to 1, eased. The DIAL is deliberately not folded in
        /// here: this is the weather's half, stepped once a tick, and the dial is multiplied on at
        /// the moment the shader is handed the value. That way turning the dial answers on the
        /// frame it is turned even while the harness has the ticks pinned, which is what makes an
        /// A/B of this feature possible at all.</summary>
        private float _windStrengthEased;

        /// <summary>Carry the surface a little further downwind. Called from the water stage, which
        /// runs once per screen, so the tick stamp holds it to one pass per tick.</summary>
        private void UpdateWaterWind(ModConfig config)
        {
            int tick = Determinism.Ticks;
            if (tick == _windDriftTick)
                return;
            int elapsedTicks = _windDriftTick < 0 ? 1 : Math.Clamp(tick - _windDriftTick, 0, WindMaxCatchUpTicks);
            _windDriftTick = tick;
            if (elapsedTicks <= 0)
                return;
            float elapsedSeconds = elapsedTicks / 60f;

            float wind = PrecipitationSystem.WindPixelsPerSecond;
            float windShare = Math.Min(1f, Math.Abs(wind) / FullWindPixelsPerSecond);
            float windSign = wind < 0f ? -1f : 1f;
            _windStrengthEased += (windShare - _windStrengthEased) * Math.Clamp(elapsedSeconds / WindEaseSeconds, 0f, 1f);
            if (_windStrengthEased < 0.0005f)
                _windStrengthEased = 0f;

            // The dial sets the pace, never the place: the sum below is a position, so a dial
            // turned down slows the surface to a stop where it stands rather than sliding it back
            // to where a windless water would have been sitting.
            double alongWind = FullWindDriftTilesPerSecond * windSign
                             * Math.Max(0f, config.WaterWind) * _windStrengthEased * elapsedSeconds;
            _windDriftColumnTiles = WrapDrift(_windDriftColumnTiles + alongWind);
            _windDriftRowTiles = WrapDrift(_windDriftRowTiles + alongWind * WindDriftCrossShare);
        }

        private static double WrapDrift(double tiles)
            => tiles - WindDriftWrapTiles * Math.Floor(tiles / WindDriftWrapTiles);

        /// <summary>Hand the shader how far the surface has been carried and how hard it blows.</summary>
        private void SetWaterWindParams(Effect effect, ModConfig config)
        {
            GetParam(effect, "WindDrift")?.SetValue(new Vector2((float)_windDriftColumnTiles, (float)_windDriftRowTiles));
            GetParam(effect, "WindAmount")?.SetValue(WaterWindAmount(config));
        }

        /// <summary>The dial times how hard it blows: what the gusts and the glitter answer to.</summary>
        private float WaterWindAmount(ModConfig config)
            => Math.Max(0f, config.WaterWind) * _windStrengthEased;

        /// <summary>The wind block of radiance_report.</summary>
        private string DescribeWaterWind()
            => $"{PrecipitationSystem.WindPixelsPerSecond:F0} px/s, blowing {_windStrengthEased:F2}, "
             + $"amount {(_lastConfig == null ? 0f : WaterWindAmount(_lastConfig)):F2}, "
             + $"drift {_windDriftColumnTiles:F2},{_windDriftRowTiles:F2} of {WindDriftWrapTiles:F1} tiles";
    }
}
