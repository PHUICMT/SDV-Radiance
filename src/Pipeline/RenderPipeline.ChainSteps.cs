using System;
using System.Diagnostics;
using System.Text;

namespace SDVRadiance
{
    /// <summary>
    /// The effect chain's processor time, split by step.
    ///
    /// <para>The cost table's "effect chain" row is one number for everything between the
    /// capture and the hand-back, and the per-pass table under it accounts only for submitting
    /// the passes. A split-screen report showed the row at 5.3 ms with the passes summing to
    /// 0.08, and a player's at 15.8 with 0.78: the time was somewhere in the chain that no row
    /// named. So each step the chain takes is timed on its own here: the stage list and its
    /// builders, the sprite normal pass, the ambient particles, the capture, the exposure
    /// meter, the passes, the upscale, and the finish. Accumulated since the last report,
    /// average and worst, so the report says which step, not just that it was the chain.</para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        internal enum ChainStep
        {
            StageList,        // AdvanceWetness + BuildStageList: the grid builders run in here
            NormalPass,       // RenderNormalPass: the sprite relief normals
            Particles,        // UpdateAndDrawAmbientParticles
            Capture,          // CaptureSceneForChain: the blit into the chain's own target
            Exposure,         // UpdateAutoExposure: the 32x32 probe and its readback
            Passes,           // RunStageChain: every full-screen pass
            Upscale,          // UpscaleToWindow + FillGainProbe
            Finish,           // FinishFrame, afterglow, drops, the report columns
            // Inside the stage list, on their own rows as well, because that step is where the
            // split screen's time went and its builders each have a grid row already.
            LightList,        // BuildLightList: the lamps gathered into the shader's pools
            WaterWatch,       // ReportWaterWatch
            BreathScan,       // ScanWaterBreathSources: mist, steam and lava sources
            // The shadow bake row, split the same way (timed from ModEntry's pre-draw handler).
            PlayerBake,       // ShadowRenderer.PreparePlayer
            OtherFarmerBakes, // ShadowRenderer.PrepareOtherFarmers
            BuildingMask,     // ShadowRenderer.BuildBuildingSunShadowMask
            // Inside the player bake, which is the biggest row a split screen carries.
            BakeTrim,         // TrimBakeCaches: the eviction walk
            BakeScene,        // RunSceneBakes: every caster and object the camera can see
            BakePose,         // BakePlayerPose: the local player's own silhouette
            PlayerPatch,      // RenderPlayerShadowPatch: the per-pixel cut of the player's shadow
            // Finer still, because on a split screen the four above summed to nothing while the
            // player bake row read three milliseconds: the time was in none of them.
            BakeGates,        // PreparePlayer's entry: ShouldCast, the reflection and puddle gates
            BakeResources,    // EnsureBakeResources
            BakeWho,          // the Game1.player checks between the scene bakes and the pose
            BakeCasters,      // RunSceneBakes: BakeCasters (people and animals)
            BakeObjects,      // RunSceneBakes: the object branch's arrival walk of the whole map
            BakeObjectsQueued,// RunSceneBakes: the queued list on a warm frame
            PatchSolidTiles,  // RenderPlayerShadowPatch: EnsureSolidTiles, the map's solid-tile texture
            // Inside the light list, which is where a split screen's time turned out to be.
            LightGather,      // GatherGameLights
            LightCandidates,  // the per-light candidate loop
            LightWindows,     // EnsureWindowCache + AddWindowLights
            LightEmissive,    // EnsureEmissiveCache + AddEmissiveLights
            LightSelect,      // SelectLights
            Count,
        }

        private static readonly string[] ChainStepNames =
        {
            // In the enum's order, and nothing else: on 2026-09-06 the bake and light groups sat
            // the other way round here, so the split screen's object walk printed as "light:
            // candidates" and a whole evening went to a paradox that was two lists out of step.
            "stage list + builders", "sprite normal pass", "ambient particles", "capture blit",
            "exposure meter", "full-screen passes", "upscale + gain probe", "finish + overlays",
            "  of which: light list", "  of which: water watch", "  of which: breath scan",
            "shadow row: player bake", "shadow row: other farmers", "shadow row: building mask",
            "  bake: cache trim", "  bake: scene casters", "  bake: player pose", "  bake: shadow patch",
            "    bake: gates", "    bake: resources", "    bake: who checks", "    bake: casters",
            "    bake: objects arrival walk", "    bake: objects queued", "    patch: solid tiles",
            "    light: gather", "    light: candidates", "    light: windows", "    light: emissive",
            "    light: select",
        };

        private readonly double[] _chainStepAccumulated = new double[(int)ChainStep.Count];
        private readonly double[] _chainStepWorst = new double[(int)ChainStep.Count];
        private readonly int[] _chainStepFrames = new int[(int)ChainStep.Count];
        private int _chainStepReadbackStalls;
        private double _chainStepReadbackWorst;

        /// <summary>The pipeline the screen being drawn belongs to, so the shadow bakes (which run
        /// from the pre-draw handler, outside this class) can put their own step times here.</summary>
        internal static RenderPipeline? DrawingScreen;

        internal static long ChainStepBegin() => Stopwatch.GetTimestamp();

        internal void ChainStepEnd(ChainStep step, long startedAt)
        {
            double ms = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            int i = (int)step;
            _chainStepAccumulated[i] += ms;
            _chainStepFrames[i]++;
            if (ms > _chainStepWorst[i]) _chainStepWorst[i] = ms;
        }

        /// <summary>A GetData that took longer than a millisecond waited for the card, whatever
        /// the frame-late reading was meant to avoid. Counted so the report can say so.</summary>
        private void NoteReadback(double ms)
        {
            if (ms < 1.0) return;
            _chainStepReadbackStalls++;
            if (ms > _chainStepReadbackWorst) _chainStepReadbackWorst = ms;
        }

        internal string DescribeChainSteps()
        {
            var sb = new StringBuilder();
            sb.AppendLine("the chain's processor time by step, since the last report (avg per call, worst call):");
            double total = 0;
            for (int i = 0; i < (int)ChainStep.Count; i++)
            {
                if (_chainStepFrames[i] == 0) continue;
                double avg = _chainStepAccumulated[i] / _chainStepFrames[i];
                if (i < (int)ChainStep.LightList) total += avg;
                sb.AppendLine($"  {ChainStepNames[i],-24} avg {avg,7:0.000} ms   worst {_chainStepWorst[i],7:0.000} ms   {_chainStepFrames[i]} calls");
            }
            sb.Append($"  {"all steps",-24} avg {total,7:0.000} ms");
            if (_chainStepReadbackStalls > 0)
                sb.Append($"   (exposure readbacks that waited for the card: {_chainStepReadbackStalls}, worst {_chainStepReadbackWorst:0.0} ms)");
            Array.Clear(_chainStepAccumulated, 0, _chainStepAccumulated.Length);
            Array.Clear(_chainStepWorst, 0, _chainStepWorst.Length);
            Array.Clear(_chainStepFrames, 0, _chainStepFrames.Length);
            _chainStepReadbackStalls = 0;
            _chainStepReadbackWorst = 0;
            return sb.ToString();
        }
    }
}
