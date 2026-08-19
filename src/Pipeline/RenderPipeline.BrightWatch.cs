using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Per-frame trace of EVERYTHING that can change how bright the picture is, printing only
    /// what moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// radiance_lightwatch answers one question - what happened to the light array - and it
    /// answered it well enough to find a real bug. It also said "the lights are fine" on a build
    /// where the flicker was still there, which is true and useless at the same time: the light
    /// array is one of at least five things that decide the brightness of a frame, and a trace
    /// that watches one of them can only ever clear that one.
    /// </para>
    /// <para>
    /// The others are all camera-dependent by construction, which is exactly why walking is when
    /// people see the fault and standing still is when it goes away: the light taper is measured
    /// from the edge of the screen, the ranking that picks which lights get slots is scored on
    /// screen position, the bounce grid is keyed to a camera origin that jumps as you cross a
    /// tile, the auto exposure meters the visible frame, and the darkness the whole pass is
    /// multiplied by eases on its own clock.
    /// </para>
    /// <para>
    /// So watch all of them on one line, per frame, and print a column only when it actually
    /// changed. Ten seconds of walking then names the culprit instead of eliminating one suspect
    /// per session. Every earlier attempt today measured a still frame and could not have seen
    /// this: freeze-and-dump compares two settled pictures, and settled is the state in which the
    /// fault does not exist.
    /// </para>
    /// </remarks>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Frames left to trace (radiance_brightwatch). Author diagnostic.</summary>
        internal static int BrightWatchFrames;

        private float _bwLightSum = float.NaN;
        private int _bwLightCount = -1;
        private Vector3 _bwAmbient = new(float.NaN);
        private float _bwExposure = float.NaN;
        private float _bwFadeLighting = float.NaN;
        private Vector2 _bwFloodOrigin = new(float.NaN);
        private bool _bwShadowsReady;
        private bool _bwPrimed;

        /// <summary>Called once per composed frame while a trace is running.</summary>
        /// <remarks>Reads state rather than measuring the frame: a readback of the picture would
        /// cost a GPU sync every frame and would only tell us THAT it changed, where these
        /// columns tell us WHICH part changed, which is the actual question.</remarks>
        private void ReportBrightWatch(ModConfig config)
        {
            if (BrightWatchFrames <= 0)
                return;
            BrightWatchFrames--;

            float lightSum = 0f;
            for (int i = 0; i < _lightCount && i < _lightShaderData.Length; i++)
                lightSum += _lightShaderData[i].X + _lightShaderData[i].Y + _lightShaderData[i].Z;
            Vector3 ambient = ComputeLightingAmbient(config);
            Vector2 floodOrigin = _flood.Origin;

            var line = new System.Text.StringBuilder("[brightwatch]");
            bool any = false;

            // A change worth naming is one that could be seen. Percentages rather than absolutes,
            // because these quantities have wildly different scales and a flat threshold would
            // either drown the line in exposure noise or hide the light array entirely.
            if (!_bwPrimed || Moved(lightSum, _bwLightSum) || _lightCount != _bwLightCount)
            {
                line.Append($"  lights={_lightCount}:{lightSum:0.000}");
                if (_bwPrimed)
                    line.Append($"(was {_bwLightCount}:{_bwLightSum:0.000})");
                any = true;
            }
            if (!_bwPrimed || Moved(ambient.X, _bwAmbient.X) || Moved(ambient.Y, _bwAmbient.Y)
                || Moved(ambient.Z, _bwAmbient.Z))
            {
                line.Append($"  ambient=({ambient.X:0.000},{ambient.Y:0.000},{ambient.Z:0.000})");
                any = true;
            }
            if (!_bwPrimed || Moved(_meteredExposure, _bwExposure))
            {
                line.Append($"  exposure={_meteredExposure:0.0000}");
                if (_bwPrimed)
                    line.Append($"(was {_bwExposure:0.0000})");
                any = true;
            }
            if (!_bwPrimed || Moved(_fadeLighting, _bwFadeLighting))
            {
                line.Append($"  lightingFade={_fadeLighting:0.000}");
                any = true;
            }
            if (!_bwPrimed || floodOrigin != _bwFloodOrigin)
            {
                line.Append($"  floodOrigin={floodOrigin.X:0},{floodOrigin.Y:0}");
                if (_bwPrimed)
                    line.Append(" REBUILT");
                any = true;
            }
            if (!_bwPrimed || _shadowsReady != _bwShadowsReady)
            {
                line.Append($"  occluderMask={(_shadowsReady ? "on" : "OFF")}");
                any = true;
            }

            _bwLightSum = lightSum;
            _bwLightCount = _lightCount;
            _bwAmbient = ambient;
            _bwExposure = _meteredExposure;
            _bwFadeLighting = _fadeLighting;
            _bwFloodOrigin = floodOrigin;
            _bwShadowsReady = _shadowsReady;
            _bwPrimed = true;

            _monitor.Log(any ? line.ToString() : "[brightwatch]  steady", LogLevel.Info);
        }

        /// <summary>Changed by more than half a percent of where it was.</summary>
        private static bool Moved(float now, float was)
            => Math.Abs(now - was) > 0.005f * Math.Max(0.02f, Math.Abs(was));

        /// <summary>A mark is pending: the next composed frame writes its whole light state to the
        /// log and captures a dump. Set from radiance_mark or the author hotkey; the value is the
        /// mark's number so several marks in one walk can be told apart.</summary>
        internal static int MarkPending;

        /// <summary>
        /// Everything the light path decided for THIS frame, in one block, on demand.
        /// </summary>
        /// <remarks>
        /// The watches print what changed. That is the right tool while a fault is being looked
        /// for, and the wrong one once a person is standing in it: they need the frame they are
        /// looking at, whole. The person walks, sees the pulse, presses the key, and the log then
        /// carries the position, the clock, every slot of the light array with its ramp and its
        /// shadow weight, which lights were wanted but waiting for a slot and which were fading
        /// out, and the bounce grid's origin - the same frame radiance_dump captures alongside.
        /// The watches, if running, keep printing around it, so the mark is a bookmark in a trace
        /// as well as a snapshot.
        /// </remarks>
        private void ReportMark(ModConfig config)
        {
            if (MarkPending <= 0)
                return;
            int n = MarkPending;
            MarkPending = 0;
            var loc = Game1.currentLocation;
            var vp = Game1.viewport;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[MARK {n}] {loc?.NameOrUniqueName} tile={Game1.player?.TilePoint.X},{Game1.player?.TilePoint.Y} "
                + $"viewport=({vp.X},{vp.Y} {vp.Width}x{vp.Height}) time={Game1.timeOfDay} tick={Game1.ticks} "
                + $"outdoors={loc?.IsOutdoors} weather={(Game1.isRaining ? "rain" : "clear")}");
            Vector3 ambient = ComputeLightingAmbient(config);
            sb.AppendLine($"  slots={_lightCount}/{MaxLights} candidates={_lightCandidates.Count} "
                + $"floodOrigin={_flood.Origin.X:0},{_flood.Origin.Y:0} floodOcc={_floodOccluderTileX},{_floodOccluderTileY} "
                + $"fadeFlood={_fadeFlood:0.00} fadeLighting={_fadeLighting:0.00} exposure={_meteredExposure:0.000} "
                + $"ambient=({ambient.X:0.00},{ambient.Y:0.00},{ambient.Z:0.00}) shadowsReady={_shadowsReady}");
            for (int i = 0; i < _lightCount && i < _lightWrite.Count; i++)
            {
                var (id, fade, rank) = _lightWrite[i];
                bool wanted = _lightWanted.Contains(id);
                sb.AppendLine($"  slot{i,2} id={id,12} uv=({fade.Uv.X:0.000},{fade.Uv.Y:0.000}) ramp={fade.Ramp:0.00} "
                    + $"col=({fade.Data.X:0.00},{fade.Data.Y:0.00},{fade.Data.Z:0.00}) reach={fade.Data.W:0.000} "
                    + $"rank={rank:0.000} shadowW={FloodShadowWeight(id):0.00}{(fade.Fire ? " fire" : "")}{(wanted ? "" : " LEAVING")}");
            }
            for (int i = _lightCount; i < _lightWrite.Count; i++)
            {
                var (id, fade, rank) = _lightWrite[i];
                sb.AppendLine($"  wait   id={id,12} uv=({fade.Uv.X:0.000},{fade.Uv.Y:0.000}) ramp={fade.Ramp:0.00} rank={rank:0.000}"
                    + (_lightWanted.Contains(id) ? " WANTED, no slot" : " dropping"));
            }
            _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Alert);
            RequestDump($"mark{n}");
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"Radiance mark {n} logged"));
        }
    }
}
