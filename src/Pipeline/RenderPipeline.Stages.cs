using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - the EFFECT STAGES: one method per post-process pass (cloud shadows,
    /// god rays, bloom, fog, tilt-shift, colour grade, flood GI composite, water, finishing,
    /// classic lighting), plus the small per-frame inputs they read (auto mood, water
    /// dynamics, god-ray light pick, metered exposure).
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>How strongly celestial light is present (cloud shadows scale by this):
        /// 1 in daylight, fading over the last ~40 min before the seasonal dark time, then
        /// moon-phase-scaled at night (a dark night has no light for clouds to block).
        /// WEATHER is applied by the caller (`_cloudWeatherAmount`), which needs an eased value.</summary>
        private static float CloudDayFactor()
        {
            int t = Game1.timeOfDay;
            int trulyDark;
            try { trulyDark = Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { trulyDark = 2000; }
            int mins = (t / 100) * 60 + t % 100;
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            float moon = 0.35f * ShadowRenderer.MoonStrength();
            if (mins >= m1)
                return moon;
            return Math.Max(moon, MathHelper.Clamp((m1 - mins) / 40f, 0f, 1f));
        }

        /// <summary>Night ramp 0→1 over 19:00→21:00 (0 by day). Shared by the night-only
        /// touches: warmer bloom, a touch more vignette, and the automatic blue night mist.</summary>
        private static float NightFactorNow()
        {
            int m = (Game1.timeOfDay / 100) * 60 + Game1.timeOfDay % 100;
            return MathHelper.Clamp((m - 1140) / 120f, 0f, 1f);
        }

        private void RenderCloudShadow(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _cloudShadow!;
            var rtA = _halfResolutionScratchA!;
            var rtB = _halfResolutionScratchB!;

            // The hard straight seam reported after long sessions was a float-precision cliff:
            // Time (and so drift = Time*Speed) grows without bound as the session runs, and once
            // the sin()-hash's input gets large enough, frac()/floor() lose precision at one x
            // line and one y line — reading as a hard "L" seam. Wrap ticks into a bounded range
            // (a multiple of 60 so seconds stay whole) so Time never grows large enough to hit it;
            // the wrap period is long enough (100 minutes) that the one-frame pattern jump at the
            // seam is imperceptible during actual play.
            float wrappedTime = (Determinism.Ticks % 360000) / 60f;

            // Pass 1: generate the cloud-density mask at half-res (WorldOffset uses
            // the full-res dest so the anchor matches the composite step).
            GetParam(effect, "Time")?.SetValue(wrappedTime);
            // OVERCAST reshape (rain / storm / snow, eased in over ~1s). A clear day is crisp
            // banks with real sky between them; a rainy one is one slow heavy ceiling with soft
            // variation in it. Same field, different settings: drift slower, grow the cloud size
            // (Scale is inverse — smaller value, bigger clouds), merge the banks into a couple of
            // masses (Count 0 is 1-2 banks) and cover most of the ground. Strength is handled by
            // _cloudDayFactor, which keeps a fraction of the opacity in this weather.
            float overcast = _cloudOvercastBlend;
            GetParam(effect, "Speed")?.SetValue(config.CloudShadowSpeed * MathHelper.Lerp(1f, 0.6f, overcast));
            GetParam(effect, "Scale")?.SetValue(config.CloudShadowScale * MathHelper.Lerp(1f, 0.65f, overcast));
            GetParam(effect, "Coverage")?.SetValue(MathHelper.Clamp(
                MathHelper.Lerp(config.CloudShadowCoverage, config.CloudShadowCoverage + 0.32f, overcast), 0f, 0.92f));
            GetParam(effect, "Count")?.SetValue(MathHelper.Lerp(config.CloudShadowCount, config.CloudShadowCount * 0.4f, overcast));
            // Small maps: the cluster field spans <1 light/dark cycle across a tiny map, so the
            // whole thing can fall in one dark bank (the "cutscene too dark" report). Boost the
            // cluster frequency by how many times the map fits inside the viewport.
            GetParam(effect, "SmallMapBoost")?.SetValue(SmallMapCloudBoost());
            GetParam(effect, "WorldOffset")?.SetValue(WorldOffset(dest.Width, dest.Height));
            GetParam(effect, "NoiseTexture")?.SetValue(NoiseTex());
            effect.CurrentTechnique = effect.Techniques["Mask"];
            Pass(spriteBatch, source, rtA, effect);

            // Pass 2/3: separable Gaussian blur -> soft, feathered penumbra edges.
            GetParam(effect, "TexelSize")?.SetValue(new Vector2(1f / rtA.Width, 0f));
            effect.CurrentTechnique = effect.Techniques["BlurH"];
            Pass(spriteBatch, rtA, rtB, effect);

            GetParam(effect, "TexelSize")?.SetValue(new Vector2(0f, 1f / rtB.Height));
            effect.CurrentTechnique = effect.Techniques["BlurV"];
            Pass(spriteBatch, rtB, rtA, effect);

            // Pass 4: composite the blurred shadow onto the scene.
            GetParam(effect, "Opacity")?.SetValue(config.CloudShadowOpacity * _cloudDayFactor * _fadeCloud);
            // Day: clouds shade EVERYTHING (white eyes/flowers included — the sun is the
            // light). Night: near-white lamp/fire cores resist the moon-cloud shadow.
            GetParam(effect, "LightProtect")?.SetValue(NightFactorNow());
            GetParam(effect, "ShadowTexture")?.SetValue(rtA);
            effect.CurrentTechnique = effect.Techniques["Composite"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        /// <summary>How many times the current map fits inside the viewport (>=1), clamped.
        /// 1 = map at least as big as the screen; higher = smaller map, more cloud banks.</summary>
        private static float SmallMapCloudBoost()
        {
            var location = Game1.currentLocation;
            var layer = location?.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            if (layer == null)
                return 1f;
            float mapW = Math.Max(1, layer.LayerWidth), mapH = Math.Max(1, layer.LayerHeight);
            float vpW = Game1.viewport.Width / 64f, vpH = Game1.viewport.Height / 64f;
            return MathHelper.Clamp(Math.Max(vpW / mapW, vpH / mapH), 1f, 4f);
        }

        private void RenderGodRays(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _godRays!;
            var rtA = _halfResolutionScratchA!;
            var rtB = _halfResolutionScratchB!;

            // Rays emanate from real in-world light sources (screen UV, so they stay
            // anchored to the scene as the camera pans) — one Bright+Rays pair PER light,
            // beams summed into rtB, composited once. Each lamp owns its beams; nothing
            // travels and nothing dims when another lamp takes over.
            if (_godRayLights.Count == 0)
                return;

            float aspect = Game1.viewport.Width / (float)Math.Max(1, Game1.viewport.Height);

            // Bright pass is GATED to a disk around the real light, so only pixels near THIS
            // light streak into rays — distant bright scenery (flowers, white hair) no longer does.
            // SNOW beats the bar wholesale: a winter field sits above 0.7 luminance edge to edge,
            // so every patch near the sun streaked and the sum read as a soft glow instead of
            // shafts (rays coming off things that are not lights). On snowy outdoor ground the bar rises to
            // just under snow's own brightness — real lamp cores and sun glitter still pass.
            // Eased, not switched: snow starting mid-day would otherwise change the bright bar
            // in one frame and every shaft on screen would step. House rule - if it changes, it fades.
            bool snowy = (Game1.currentSeason == "winter" || Game1.isSnowing) && (Game1.currentLocation?.IsOutdoors ?? false);
            Approach(ref _snowThresholdAmount, snowy ? 1f : 0f, 0.04f);
            if (Math.Abs((snowy ? 1f : 0f) - _snowThresholdAmount) < 0.003f) _snowThresholdAmount = snowy ? 1f : 0f;
            float grThr = MathHelper.Lerp(config.GodRaysThreshold,
                Math.Max(config.GodRaysThreshold, 0.93f), _snowThresholdAmount);
            GetParam(effect, "Threshold")?.SetValue(grThr);
            GetParam(effect, "Aspect")?.SetValue(aspect);
            // Player pixels are not light emitters — same silhouette exclusion as the water.
            var grWho = Game1.player;
            var grMask = ShadowRenderer.PlayerMask;
            var grRect = new Vector4(2f, 2f, -1f, -1f);
            if (grWho != null && grMask != null)
            {
                Rectangle box = grWho.GetBoundingBox();
                // Sprite bottom edge, not the feet-contact line — see the water stage.
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom + grWho.yOffset));
                Vector2 tl = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                grRect = new Vector4(tl.X / dest.Width, tl.Y / dest.Height,
                    (tl.X + ShadowRenderer.PlayerRtW) / dest.Width, (tl.Y + ShadowRenderer.PlayerRtH) / dest.Height);
            }
            GetParam(effect, "PlayerRect")?.SetValue(grRect);
            GetParam(effect, "PlayerMaskTexture")?.SetValue(grMask);
            // NPCs / animals / critters are not light emitters either (same mask the water uses).
            GetParam(effect, "SpriteMaskOn")?.SetValue(SpriteMaskReady && _spriteMaskRenderTarget != null ? 1f : 0f);
            GetParam(effect, "SpriteMaskTexture")?.SetValue(_spriteMaskRenderTarget);
            // With flood GI active, only lit pixels may emit rays (kills rays from bright
            // sprites in unlit corners; lamp glow zones still stream at night).
            bool floodGate = config.FloodLightingEnabled && _flood.Texture != null;
            GetParam(effect, "FloodGate")?.SetValue(floodGate ? 1f : 0f);
            if (floodGate)
            {
                GetParam(effect, "FloodMapTexture")?.SetValue(_flood.Texture);
                GetParam(effect, "FloodTilesPerScreen")?.SetValue(new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f));
                GetParam(effect, "FloodWorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
                GetParam(effect, "FloodMapOrigin")?.SetValue(_flood.Origin);
                GetParam(effect, "FloodMapSize")?.SetValue(_flood.MapSize);
            }
            GetParam(effect, "Density")?.SetValue(config.GodRaysDensity);
            GetParam(effect, "Decay")?.SetValue(config.GodRaysDecay);
            GetParam(effect, "Weight")?.SetValue(0.5f);
            for (int li = 0; li < _godRayLights.Count; li++)
            {
                var (luv, lruv, lamt) = _godRayLights[li];
                GetParam(effect, "LightPos")?.SetValue(luv);
                GetParam(effect, "LightRadius")?.SetValue(lruv);
                GetParam(effect, "LightAmt")?.SetValue(lamt);   // this lamp's own eased presence
                effect.CurrentTechnique = effect.Techniques["Bright"];
                Pass(spriteBatch, source, rtA, effect);
                effect.CurrentTechnique = effect.Techniques["Rays"];
                if (li == 0)
                    Pass(spriteBatch, rtA, rtB, effect);        // first light claims the buffer
                else
                    PassAdd(spriteBatch, rtA, rtB, effect);     // the rest SUM into it
            }

            GetParam(effect, "Intensity")?.SetValue(config.GodRaysIntensity * _godRayAmount);
            GetParam(effect, "RaysTexture")?.SetValue(rtB);
            effect.CurrentTechnique = effect.Techniques["Composite"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        private void RenderBloom(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var bloom = _bloom!;
            var rtA = _halfResolutionScratchA!;
            var rtB = _halfResolutionScratchB!;
            int w = dest.Width, h = dest.Height;

            // At night, bloom blooms more (lower threshold, a bit stronger) and turns warm so
            // lamps/windows glow amber.
            float bloomNight = NightFactorNow();

            GetParam(bloom, "Threshold")?.SetValue(MathHelper.Clamp(config.BloomThreshold - 0.08f * bloomNight, 0f, 1f));
            GetParam(bloom, "TexelSize")?.SetValue(new Vector2(1f / w, 1f / h));
            bloom.CurrentTechnique = bloom.Techniques["BrightPass"];
            Pass(spriteBatch, source, rtA, bloom);

            GetParam(bloom, "TexelSize")?.SetValue(new Vector2(1f / rtA.Width, 0f));
            bloom.CurrentTechnique = bloom.Techniques["BlurHorizontal"];
            Pass(spriteBatch, rtA, rtB, bloom);

            GetParam(bloom, "TexelSize")?.SetValue(new Vector2(0f, 1f / rtB.Height));
            bloom.CurrentTechnique = bloom.Techniques["BlurVertical"];
            Pass(spriteBatch, rtB, rtA, bloom);

            GetParam(bloom, "Intensity")?.SetValue(config.BloomIntensity * (1f + 0.2f * bloomNight));
            GetParam(bloom, "BloomWarm")?.SetValue(bloomNight);
            GetParam(bloom, "BloomTexture")?.SetValue(rtA);
            bloom.CurrentTechnique = bloom.Techniques["Composite"];
            DrawFull(spriteBatch, source, dest, bloom);
        }

        // Baked, TILEABLE 5-octave value-noise fbm. GPU sin()-hash noise has NO precision
        // guarantee (hard seams / faceted blobs on real hardware, varying by vendor) — the
        // standard fix is to precompute the noise on the CPU at full precision once and let
        // the shader just sample it with wrap addressing: seamless over the whole screen,
        // identical on every GPU.
        private Texture2D? _noiseTexture;
        private Texture2D NoiseTex()
        {
            if (_noiseTexture != null)
                return _noiseTexture;
            const int N = 256;
            var acc = new float[N * N];
            float amp = 0.5f, norm = 0f;
            int cells = 4;                       // 4,8,16,32,64 — every octave tiles at 256
            for (int o = 0; o < 5; o++)
            {
                for (int y = 0; y < N; y++)
                {
                    float fy = (float)y / N * cells;
                    int y0 = (int)fy;
                    float ty = fy - y0;
                    ty = ty * ty * ty * (ty * (ty * 6f - 15f) + 10f);
                    for (int x = 0; x < N; x++)
                    {
                        float effect = (float)x / N * cells;
                        int x0 = (int)effect;
                        float tx = effect - x0;
                        tx = tx * tx * tx * (tx * (tx * 6f - 15f) + 10f);
                        float a = NoiseHash(x0, y0, o, cells), b = NoiseHash(x0 + 1, y0, o, cells);
                        float c = NoiseHash(x0, y0 + 1, o, cells), d = NoiseHash(x0 + 1, y0 + 1, o, cells);
                        acc[y * N + x] += amp * MathHelper.Lerp(MathHelper.Lerp(a, b, tx), MathHelper.Lerp(c, d, tx), ty);
                    }
                }
                norm += amp; amp *= 0.5f; cells *= 2;
            }
            var data = new Color[N * N];
            for (int i = 0; i < acc.Length; i++)
            {
                byte v = (byte)MathHelper.Clamp(acc[i] / norm * 255f, 0f, 255f);
                data[i] = new Color(v, v, v, (byte)255);
            }
            _noiseTexture = new Texture2D(_device, N, N);
            _noiseTexture.SetData(data);
            return _noiseTexture;
        }
        private static float NoiseHash(int x, int y, int o, int cells)
        {
            x = ((x % cells) + cells) % cells;   // wrap the lattice → texture tiles perfectly
            y = ((y % cells) + cells) % cells;
            uint h = (uint)(x * 374761393 + y * 668265263 + (o + 1) * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        private void RenderFog(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _fogEffect!;
            // One shader pass renders the blend of two separate effects: DAY fog and NIGHT
            // mist. Both are the same sparse drifting-wisp look (Patchiness 1 — the author
            // liked the night wisps and wants day fog to match); each keeps its own density
            // slider, day also keeps its scale/speed sliders. Amounts crossfade over dusk.
            float total = _fogDayAmount + _fogMistAmount;
            float mistW = total > 0f ? _fogMistAmount / total : 0f;
            GetParam(effect, "Time")?.SetValue(Time());
            GetParam(effect, "Speed")?.SetValue(MathHelper.Lerp(config.FogSpeed, config.FogNightMistSpeed, mistW));
            GetParam(effect, "Scale")?.SetValue(MathHelper.Lerp(config.FogScale, 3.2f, mistW));
            GetParam(effect, "Density")?.SetValue(total);
            GetParam(effect, "Patchiness")?.SetValue(1f);
            GetParam(effect, "Coverage")?.SetValue(MathHelper.Lerp(config.FogCoverage, config.FogNightMistCoverage, mistW));
            GetParam(effect, "TopBias")?.SetValue(config.FogTopBias);
            GetParam(effect, "NoiseTexture")?.SetValue(NoiseTex());
            GetParam(effect, "FogColor")?.SetValue(FogColor());
            GetParam(effect, "WorldOffset")?.SetValue(WorldOffset(dest.Width, dest.Height));
            effect.CurrentTechnique = effect.Techniques["Fog"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        private void RenderTiltShift(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _tiltShift!;
            var rtA = _halfResolutionScratchA!;
            var rtB = _halfResolutionScratchB!;

            // blur at half-res: source(full) -> rtA (H) -> rtB (V)
            GetParam(effect, "TexelSize")?.SetValue(new Vector2(1f / rtA.Width, 0f));
            effect.CurrentTechnique = effect.Techniques["BlurH"];
            Pass(spriteBatch, source, rtA, effect);

            GetParam(effect, "TexelSize")?.SetValue(new Vector2(0f, 1f / rtB.Height));
            effect.CurrentTechnique = effect.Techniques["BlurV"];
            Pass(spriteBatch, rtA, rtB, effect);

            // composite sharp + blurred by vertical position.
            // Config stores intuitive "blur amount" (higher = more blur from that edge);
            // convert to sharp-band edges: more top blur pushes TopEdge down, more
            // bottom blur pulls BottomEdge up.
            GetParam(effect, "TopEdge")?.SetValue(MathHelper.Clamp(config.TiltShiftTopRatio, 0f, 1f) * 0.5f);
            GetParam(effect, "BottomEdge")?.SetValue(1f - MathHelper.Clamp(config.TiltShiftBottomRatio, 0f, 1f) * 0.5f);
            GetParam(effect, "Strength")?.SetValue(config.TiltShiftStrength * _fadeTilt);
            GetParam(effect, "Mode")?.SetValue(config.TiltShiftMode == TiltShiftFocus.Radial ? 1f : 0f);
            GetParam(effect, "Center")?.SetValue(PlayerScreenUV());
            GetParam(effect, "Aspect")?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            GetParam(effect, "RadRadius")?.SetValue(MathHelper.Clamp(config.TiltShiftRadius, 0.05f, 0.9f));
            GetParam(effect, "Feather")?.SetValue(MathHelper.Clamp(config.TiltShiftFeather, 0f, 1f));
            GetParam(effect, "BlurTexture")?.SetValue(rtB);
            effect.CurrentTechnique = effect.Techniques["Composite"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        private void ColorGrade(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _colorGrade!;
            // The stage may run for the BLUE-LIGHT FILTER alone (grading toggled off): the
            // artistic controls go neutral so only the warm eye-comfort shift applies.
            bool gradeOn = config.ColorGradeEnabled;
            float temp = config.ColorGradeTemperature;
            float sat = config.ColorGradeSaturation;
            if (gradeOn && config.ColorGradeAuto)
            {
                ComputeAuto(out float autoTemp, out float autoSatMul);
                temp += autoTemp;
                sat *= autoSatMul;
            }

            // _meteredExposure is measured & eased per frame in UpdateAutoExposure
            // (1.0 when auto is off), so bright scenes dim smoothly with no pop.
            GetParam(effect, "Strength")?.SetValue(gradeOn ? MathHelper.Clamp(config.ColorGradeStrength, 0f, 1f) : 1f);
            GetParam(effect, "Contrast")?.SetValue(gradeOn ? config.ColorGradeContrast : 1f);
            GetParam(effect, "Saturation")?.SetValue(gradeOn ? sat : 1f);
            GetParam(effect, "Temperature")?.SetValue(gradeOn ? MathHelper.Clamp(temp, -1f, 1f) : 0f);
            GetParam(effect, "Brightness")?.SetValue(gradeOn ? config.ColorGradeBrightness * _meteredExposure : 1f);
            GetParam(effect, "ToneMap")?.SetValue(gradeOn && config.ColorGradeToneMap ? 1f : 0f);
            GetParam(effect, "BlueLight")?.SetValue(MathHelper.Clamp(config.BlueLightFilter, 0f, 1f));
            effect.CurrentTechnique = effect.Techniques["ColorGrade"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        private bool _isFloodOcclusionReady;
        private readonly Vector2[] _floodLightPositions = new Vector2[8];
        private readonly Vector4[] _floodLightColors = new Vector4[8];

        private void RenderFloodLight(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _floodEffect!;
            GetParam(effect, "LightMapTexture")?.SetValue(_flood.Texture);
            GetParam(effect, "TilesPerScreen")?.SetValue(new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f));
            GetParam(effect, "WorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            GetParam(effect, "MapOrigin")?.SetValue(_flood.Origin);
            GetParam(effect, "MapSize")?.SetValue(_flood.MapSize);
            GetParam(effect, "Strength")?.SetValue(MathHelper.Clamp(config.FloodLightingStrength, 0f, 1f) * _fadeFlood);
            GetParam(effect, "AmbientFloor")?.SetValue(0.10f);

            // Direct pools with per-light shadows: the brightest 8 on-screen lights (from
            // BuildLightList) + the occluder mask. Direct is scaled DOWN vs the classic
            // lighting stage because the flood map already carries the indirect spill.
            int n = 0;
            for (int i = 0; i < _lightCount && n < 8; i++, n++)
            {
                _floodLightPositions[n] = _lightPositions[i];
                var d = _lightShaderData[i];
                _floodLightColors[n] = new Vector4(d.X * 0.55f, d.Y * 0.55f, d.Z * 0.55f, d.W);
            }
            for (int i = n; i < 8; i++) { _floodLightPositions[i] = Vector2.Zero; _floodLightColors[i] = Vector4.Zero; }
            GetParam(effect, "LightPosArr")?.SetValue(_floodLightPositions);
            GetParam(effect, "LightColArr")?.SetValue(_floodLightColors);
            GetParam(effect, "DirectCount")?.SetValue((float)(_isFloodOcclusionReady ? n : 0));
            GetParam(effect, "Aspect")?.SetValue(dest.Width / (float)Math.Max(1, dest.Height));
            GetParam(effect, "OccluderTexture")?.SetValue(_occluderMask);
            GetParam(effect, "OccOrigin")?.SetValue(new Vector2((float)Math.Floor(Game1.viewport.X / 64f), (float)Math.Floor(Game1.viewport.Y / 64f)));
            GetParam(effect, "OccMapSize")?.SetValue(_occluderMaskSize);
            GetParam(effect, "ShadowStrength")?.SetValue(MathHelper.Clamp(config.FloodShadowStrength, 0f, 1f));

            effect.CurrentTechnique = effect.Techniques["FloodLight"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        private void RenderWater(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _water!;
            // Weather/season drive how agitated the water is: choppier & faster in
            // rain/storm, sluggish in winter; sparkle fades when there's no sun.
            ComputeWaterDynamics(out float strengthMul, out float speedMul, out float sparkleMul);
            // The stage can run for the REFLECTION alone (shimmer toggled off): ripple,
            // sparkle, tint and rim all zero out; the mirror keeps working independently.
            float shimmer = (config.WaterEnabled ? 1f : 0f) * _fadeWater;   // presence fade: never pops in
            // W8: during a cutscene the game draws the event UI (the SKIP button, dialogue)
            // as part of the world frame, so the ripple's pixel DISPLACEMENT bent it over
            // water/lava. Zero the displacement in events (same treatment as CA/tilt-shift) —
            // but keep tint / reflection / sparkle, which don't move pixels, so the water
            // still reads correctly in the cinematic.
            bool eventUp = Game1.eventUp || Game1.CurrentEvent != null;
            float dispGate = eventUp ? 0f : 1f;
            // Indoor water (hot spring, sewer, caves) sits under a ceiling, often in steam:
            // there is no sun to sparkle, no sky to mirror sharply, and the pale pool art
            // blows out under the full outdoor treatment. Calmer waves, faint reflection.
            bool indoors = !(Game1.currentLocation?.IsOutdoors ?? true);
            float inWave = indoors ? 0.6f : 1f;
            float inSpark = indoors ? 0.35f : 1f;
            float inRefl = indoors ? 0.35f : 1f;
            float inTint = indoors ? 0.5f : 1f;
            // Whole-pass presence (see water.effect): the per-term fades below do not reach every
            // term, so the pass held full strength down to a fade of 0.02 and then popped out.
            GetParam(effect, "Presence")?.SetValue(_fadeWater);
            GetParam(effect, "WetRim")?.SetValue(1f);
            GetParam(effect, "Time")?.SetValue(Time());
            GetParam(effect, "Strength")?.SetValue(config.WaterStrength * strengthMul * shimmer * dispGate * inWave);
            GetParam(effect, "Speed")?.SetValue(config.WaterSpeed * speedMul);
            GetParam(effect, "Sparkle")?.SetValue(config.WaterSparkle * sparkleMul * shimmer * inSpark);
            GetParam(effect, "TintAmt")?.SetValue(0.35f * shimmer * inTint);
            GetParam(effect, "ReflectStrength")?.SetValue((config.WaterReflection ? config.WaterReflectStrength : 0f) * _fadeWater * inRefl);
            // Per-frame sprite exclusion mask (ducks, NPCs, critters on the water).
            GetParam(effect, "SpriteMaskOn")?.SetValue(SpriteMaskReady && _spriteMaskRenderTarget != null ? 1f : 0f);
            GetParam(effect, "SpriteMaskTexture")?.SetValue(_spriteMaskRenderTarget);
            // P3b: flipped-entity reflection layer — the mirror's PREFERRED source. Where
            // this RT has content, it is the correct reflection by construction; the
            // screen-space flip only fills in scenery behind it (until P3c replaces that too).
            GetParam(effect, "ReflectRTOn")?.SetValue(ReflectRTReady && _reflectionRenderTarget != null ? 1f : 0f);
            GetParam(effect, "ReflectRTPlayer")?.SetValue(ReflectRTReady && ReflectRTHasPlayer ? 1f : 0f);
            GetParam(effect, "ReflectRTTexture")?.SetValue(_reflectionRenderTarget);
            // P3c: sprite-free scenery source — the mirror reads the map's own pixels, so
            // an excluded sprite can't leave a body-shaped sky hole in the reflection.
            // The raw layer render carries no lighting; ambient rescales it to the scene.
            GetParam(effect, "SceneOn")?.SetValue(SceneRTReady && _mirrorSourceRenderTarget != null && !SceneSourceOff ? 1f : 0f);
            GetParam(effect, "SceneTexture")?.SetValue(_mirrorSourceRenderTarget);
            GetParam(effect, "SceneAmbient")?.SetValue(Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting));
            GetParam(effect, "WaterKind")?.SetValue(WaterKind());
            GetParam(effect, "TilesPerScreen")?.SetValue(_waterMaskTilesPerScreen);
            GetParam(effect, "WorldTileOffset")?.SetValue(_waterMaskWorldTileOffset);
            GetParam(effect, "MaskSize")?.SetValue(_waterMaskPixelSize);
            GetParam(effect, "MaskOrigin")?.SetValue(new Vector2(_lastWaterTileX, _lastWaterTileY));
            GetParam(effect, "MaskTexture")?.SetValue(_waterMask);
            GetParam(effect, "MaskCoreTexture")?.SetValue(_waterMaskCore);
            GetParam(effect, "SdfTexture")?.SetValue(_waterSignedDistanceTexture);
            GetParam(effect, "SparkleDensity")?.SetValue(config.WaterSparkleDensity);
            // Player SILHOUETTE mask (the shadow system's per-frame bake) in buffer UV —
            // ring-tile water effects skip exactly the player's own pixels, so a blue outfit
            // on a pier never ripples while the water right beside them stays animated.
            var who = Game1.player;
            var pmask = ShadowRenderer.PlayerMask;
            var playerRect = new Vector4(2f, 2f, -1f, -1f);   // empty box (never matches)
            if (who != null && pmask != null)
            {
                Rectangle box = who.GetBoundingBox();
                // yOffset is the DRAW-time bob (swimming, jumps) that the collision box never
                // sees — without it the exclusion silhouette floats above the swimmer and the
                // water effect leaves a dead margin over their head.
                // box.Bottom, NOT the shadow's feet-contact line (bottom - 10): this rect has to
                // overlay the DRAWN sprite, and the bake pins the sprite's bottom edge to the
                // anchor. Anchoring 10 px high shifted the whole exclusion up, so a strip of
                // water above the head stopped rippling while the sprite's own feet still did.
                // Same convention the NPC stamps use (StampSprite).
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom + who.yOffset));
                Vector2 tl = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                playerRect = new Vector4(tl.X / dest.Width, tl.Y / dest.Height,
                    (tl.X + ShadowRenderer.PlayerRtW) / dest.Width, (tl.Y + ShadowRenderer.PlayerRtH) / dest.Height);
            }
            GetParam(effect, "PlayerRect")?.SetValue(playerRect);
            GetParam(effect, "PlayerMaskTexture")?.SetValue(pmask);

            // Time-of-day / weather dressing: golden-hour sparkle, star reflections and
            // lamp glimmer after dusk, raindrop rings while raining.
            int tnow = Game1.timeOfDay;
            int mins = ClockMinutes();
            // Golden hour, on the clock and without a cliff. This read the raw HHMM value (so it
            // lurched at every hour boundary) and then cut to zero the instant the clock passed
            // 19:00 - full warmth at 18:50, none at 19:00, in one step, which is the flash of a
            // changed picture at seven in the evening. Ramp it down over the last half hour
            // instead, on minutes, so it arrives at zero having already faded there.
            float sunWarm = 0f;
            if (!Game1.isRaining)
            {
                float dayProgress = MathHelper.Clamp((mins - 12 * 60) / 360f, -1f, 1f);
                sunWarm = MathHelper.Clamp((Math.Abs(dayProgress) - 0.55f) / 0.45f, 0f, 1f);
                sunWarm *= MathHelper.Clamp((19 * 60 - mins) / 30f, 0f, 1f);
            }
            float nightGlow = MathHelper.Clamp((mins - 1140) / 90f, 0f, 1f);   // 19:00 → 20:30
            GetParam(effect, "SunWarm")?.SetValue(sunWarm);
            GetParam(effect, "NightGlow")?.SetValue(nightGlow);
            GetParam(effect, "MoonGlow")?.SetValue(ShadowRenderer.MoonStrength());
            GetParam(effect, "RainAmt")?.SetValue(Game1.isRaining ? 1f : 0f);

            // SKY tint for the mirror's far end and the no-mirror sheen. Water reflects the sky
            // before it reflects anything else; for an orthographic fixed-pitch camera the Fresnel
            // mix is a CONSTANT, so the only things that vary are WHICH source (object vs sky) and
            // the ripple breakup — never strength-by-distance. Stardew has no sky to sample
            // top-down, so it is synthesised from time and weather, then scaled by the lighting
            // stage's ambient so water never stays bright inside a darkened scene.
            Vector3 sky = new(0.62f, 0.78f, 0.96f);                                  // open daylight
            sky = Vector3.Lerp(sky, new Vector3(0.98f, 0.72f, 0.45f), sunWarm);      // golden hour
            sky = Vector3.Lerp(sky, new Vector3(0.08f, 0.12f, 0.28f), nightGlow);    // dusk → night
            if (Game1.isRaining || Game1.isSnowing)
                sky = Vector3.Lerp(sky, new Vector3(0.52f, 0.56f, 0.62f), 0.75f);    // overcast
            if (!(Game1.currentLocation?.IsOutdoors ?? true))
                sky = Vector3.Lerp(sky, new Vector3(0.30f, 0.33f, 0.40f), 0.7f);     // no sky indoors
            sky *= Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting);
            GetParam(effect, "SkyColor")?.SetValue(sky);

            int lc = 0;
            if (nightGlow > 0f && Game1.currentLightSources != null)
            {
                foreach (var kv in Game1.currentLightSources.Values)
                {
                    if (lc >= 8)
                        break;
                    Vector2 sp = Game1.GlobalToLocal(Game1.viewport, kv.position.Value);
                    if (sp.X < -160 || sp.X > dest.Width + 160 || sp.Y < -160 || sp.Y > dest.Height + 160)
                        continue;
                    _waterGlimmerLights[lc++] = new Vector4(sp.X / dest.Width, sp.Y / dest.Height, kv.radius.Value, 0.9f);
                }
            }
            GetParam(effect, "LightCount")?.SetValue((float)lc);
            GetParam(effect, "Lights")?.SetValue(_waterGlimmerLights);

            // Wading: are the player's feet on water pixels? (mask texel = 4 world px)
            // SWIMMING is excluded: half the body is already underwater, so a mirrored
            // silhouette below the feet reads as a glitch, not a reflection — the ripple
            // exclusion (silhouette gate) is what protects the visible half instead.
            float pin = 0f;
            if (who != null && !who.swimming.Value && _waterMaskPixels != null && _waterMask != null)
            {
                Rectangle bb = who.GetBoundingBox();
                int mxp = bb.Center.X / 4 - _lastWaterTileX * 16;
                int myp = (bb.Bottom - 4) / 4 - _lastWaterTileY * 16;
                if (mxp >= 0 && myp >= 0 && mxp < _waterMask.Width && myp < _waterMask.Height
                    && _waterMaskPixels[myp * _waterMask.Width + mxp].R > 100)
                    pin = 1f;
            }
            // Ease the wading state so the under-feet self-reflection fades in/out (~0.3s)
            // instead of popping the moment the feet cross the water edge.
            Approach(ref _pinFadeAmount, pin, 0.12f);
            if (Math.Abs(pin - _pinFadeAmount) < 0.01f) _pinFadeAmount = pin;
            GetParam(effect, "PlayerInWater")?.SetValue(_pinFadeAmount);

            effect.CurrentTechnique = effect.Techniques["Water"];
            DrawFull(spriteBatch, source, dest, effect);
            // Presence enforced outside the shader (see BlendBackSource): the in-shader uniform
            // measured inert, and the wet-rim early return never passes through it anyway.
            BlendBackSource(spriteBatch, source, dest, _fadeWater);
        }

        private void RenderFinishing(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _finishing!;
            GetParam(effect, "VignetteStrength")?.SetValue(config.VignetteEnabled ? config.VignetteStrength : 0f);
            // Map the 0..1 UI value to a tiny UV offset so it stays subtle on pixel art.
            // No CA during events: the SKIP button is drawn inside the world frame and the
            // channel split shreds its text (community report). Vignette stays — it's the
            // cinematic part and doesn't hurt readability.
            bool eventUp = Game1.eventUp || Game1.CurrentEvent != null;
            GetParam(effect, "CAStrength")?.SetValue(config.ChromaticAberrationEnabled && !eventUp ? config.ChromaticAberrationStrength * 0.03f : 0f);
            // A touch more vignette at night — but only as part of the vignette effect
            // itself: with Vignette OFF (e.g. only CA on) the shader must add nothing,
            // or "off" quietly darkens the night screen edges.
            GetParam(effect, "NightAmt")?.SetValue(config.VignetteEnabled ? NightFactorNow() : 0f);
            effect.CurrentTechnique = effect.Techniques["Finishing"];
            DrawFull(spriteBatch, source, dest, effect);
        }

        private void RenderLighting(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _lighting!;
            // Presence fade: ambient darkening eases in from "no change" (white) on appearance.
            GetParam(effect, "AmbientColor")?.SetValue(Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting));
            // Whole-pass presence (see lighting.effect): the light pools are not scaled by the fade.
            GetParam(effect, "Presence")?.SetValue(_fadeLighting);
            GetParam(effect, "Aspect")?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            GetParam(effect, "LightPos")?.SetValue(_lightPositions);
            GetParam(effect, "LightData")?.SetValue(_lightShaderData);
            // Allow pools to slightly exceed 1 so lamps glow a touch; keep it modest.
            GetParam(effect, "Overbright")?.SetValue(1.0f + 0.4f * MathHelper.Clamp(config.LightingBoost, 0f, 2f));
            // Occluder shadows: only when enabled AND a mask was built this frame.
            if (_shadowsReady && _occluderMask != null)
            {
                GetParam(effect, "ShadowStrength")?.SetValue(MathHelper.Clamp(config.LightingShadowStrength, 0f, 1f) * _fadeLighting);
                GetParam(effect, "OccluderTexture")?.SetValue(_occluderMask);
                GetParam(effect, "OccTilesPerScreen")?.SetValue(_occluderTilesPerScreen);
                GetParam(effect, "OccWorldTileOffset")?.SetValue(_occluderWorldTileOffset);
                GetParam(effect, "OccMaskSize")?.SetValue(_occluderMaskSize);
            }
            else
            {
                // Disabled: bind a valid texture and 0 strength so nothing samples garbage.
                GetParam(effect, "ShadowStrength")?.SetValue(0f);
                GetParam(effect, "OccluderTexture")?.SetValue(source);
            }
            effect.CurrentTechnique = effect.Techniques["Lighting"];
            DrawFull(spriteBatch, source, dest, effect);
            // Same out-of-shader presence as the water pass: the light POOLS never rode the
            // fade, so this stage popped its full contribution in and out with the light list.
            BlendBackSource(spriteBatch, source, dest, _fadeLighting);
        }

        /// <summary>Character head anchors (centreX, boxTop), refilled once per light query so the
        /// bubble test below doesn't call GetBoundingBox() again for every (light × character) pair.</summary>
        private static readonly System.Collections.Generic.List<(float cx, float top)> _bubbleAnchors = new();

        private static void FillBubbleAnchors(GameLocation? location)
        {
            _bubbleAnchors.Clear();
            if (location != null)
                foreach (NPC c in ShadowRenderer.CharactersIn(location))
                {
                    if (c == null)
                        continue;
                    Rectangle box = c.GetBoundingBox();
                    _bubbleAnchors.Add((box.Center.X, box.Top));
                }
            var p = Game1.player;
            if (p != null)
            {
                Rectangle box = p.GetBoundingBox();
                _bubbleAnchors.Add((box.Center.X, box.Top));
            }
        }

        /// <summary>A light hovering right above a character's head is almost certainly a
        /// speech-bubble / emote light some mods add (e.g. The Muttering Farmer), not an
        /// environmental light — those shouldn't spawn god rays. Tests against the anchors filled
        /// by <see cref="FillBubbleAnchors"/> (no per-call GetBoundingBox()).</summary>
        private static bool IsCharacterBubble(Vector2 worldPos)
        {
            foreach (var (cx, top) in _bubbleAnchors)
                // above the head (roughly one-to-three tiles up), horizontally centred on them
                if (Math.Abs(worldPos.X - cx) < 40f && worldPos.Y > top - 160f && worldPos.Y < top + 24f)
                    return true;
            return false;
        }

        /// <summary>Ray sources this frame, each with its OWN presence. Every on-screen lamp
        /// gets its own beams: the old single-source pick made the one beam glide across the
        /// screen to the next lamp as you walked, because there was only ever one origin.
        /// <para>
        /// Presence is PER LIGHT and eased, which is what stops the popping. A global fade
        /// cannot express "this lamp is arriving while that one leaves", so a lamp entering
        /// the view switched its beams on in a single frame. Worse where lamps CLUSTER: with
        /// a hard top-N cut, walking past a row of them reshuffled which ones made the cut
        /// and the losers vanished instantly. Now a light that loses its slot just eases out,
        /// and its beams cross-fade with the one that took over.
        /// </para></summary>
        internal const int MaxRayLights = 3;      // how many lights may be ACTIVE at once
        private const int RayRenderSlots = 5;     // + room for the ones still fading out
        private const float RayMergeUV = 0.07f;   // closer than this and beams overlap anyway
        private float _snowThresholdAmount;                // eased snow bright-bar blend (0..1)

        private sealed class RayLight
        {
            public Vector2 Uv;
            public float RadiusUv;
            public float R;          // game radius: the "which lamps matter" ranking
            public float Amt;        // eased presence 0..1
            public bool Seen;        // present on screen this frame
        }

        private readonly Dictionary<string, RayLight> _godRayTracking = new();
        private GameLocation? _godRayTrackingLocation;
        private readonly List<KeyValuePair<string, RayLight>> _godRayTrackingScratch = new();
        private readonly List<(Vector2 uv, float radiusUV, float amt)> _godRayLights = new();

        /// <summary>Refresh the tracked ray lights: geometry, per-light presence, render list.</summary>
        private bool UpdateRayLights()
        {
            if (!ReferenceEquals(Game1.currentLocation, _godRayTrackingLocation))
            {
                _godRayTrackingLocation = Game1.currentLocation;
                _godRayTracking.Clear();   // another map's lamps must not fade out over this one
            }
            foreach (var e in _godRayTracking.Values)
                e.Seen = false;

            var lights = Game1.currentLightSources;
            if (lights != null && lights.Count > 0)
            {
                int vw = Math.Max(1, Game1.viewport.Width);
                int vh = Math.Max(1, Game1.viewport.Height);
                FillBubbleAnchors(Game1.currentLocation);   // once per frame, not per (light × character)

                foreach (var kv in lights)
                {
                    LightSource ls = kv.Value;
                    if (IsCharacterBubble(ls.position.Value))
                        continue; // speech-bubble / emote light — not an environmental ray source
                    float r = ls.radius.Value;
                    if (r <= 0f)
                        continue;
                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    float u = local.X / vw, v = local.Y / vh;
                    if (u < -0.25f || u > 1.25f || v < -0.25f || v > 1.25f)
                        continue; // off-screen

                    if (!_godRayTracking.TryGetValue(kv.Key, out RayLight? e))
                        _godRayTracking[kv.Key] = e = new RayLight();
                    e.Uv = new Vector2(u, v);
                    // radius.Value is ~tiles; on-screen glow ≈ radius*64px. Give the rays a
                    // little more reach than the glow, so only pixels near THIS light streak
                    // (not distant bright scenery like flowers/white hair).
                    e.RadiusUv = MathHelper.Clamp(r * 64f * 2.2f / vh, 0.12f, 0.6f);
                    e.R = r;
                    e.Seen = true;
                }
            }

            // Rank the candidates. A light that is ALREADY lit gets a bonus so it keeps its
            // slot until something is genuinely brighter — without that hysteresis, two lamps
            // of equal radius traded the last slot every few frames and flickered.
            _godRayTrackingScratch.Clear();
            foreach (var kv in _godRayTracking)
                if (kv.Value.Seen)
                    _godRayTrackingScratch.Add(kv);
            if (_godRayTrackingScratch.Count > 1)
                _godRayTrackingScratch.Sort((a, b) =>
                {
                    float sa = a.Value.R + (a.Value.Amt > 0.05f ? 1000f : 0f);
                    float sb2 = b.Value.R + (b.Value.Amt > 0.05f ? 1000f : 0f);
                    return sb2.CompareTo(sa);
                });

            int active = 0;
            for (int i = 0; i < _godRayTrackingScratch.Count; i++)
            {
                RayLight e = _godRayTrackingScratch[i].Value;
                bool on = active < MaxRayLights;
                if (on)
                    // Lamps standing almost on top of each other produce the same beams twice;
                    // let the stronger one carry them and ease the neighbour out. This is what
                    // keeps a row of close lamps from churning slots as the camera moves.
                    for (int j = 0; j < i; j++)
                    {
                        RayLight o = _godRayTrackingScratch[j].Value;
                        if (o.Amt > 0.05f && Vector2.Distance(o.Uv, e.Uv) < RayMergeUV) { on = false; break; }
                    }
                if (on)
                    active++;
                Approach(ref e.Amt, on ? 1f : 0f, 0.07f);   // ~0.6 s in, same out
            }
            // Unseen lights keep fading from their LAST known spot: a lamp scrolling off the
            // edge, or a torch being put out, dims where it stood instead of blinking off.
            foreach (var e in _godRayTracking.Values)
                if (!e.Seen)
                    Approach(ref e.Amt, 0f, 0.07f);

            _godRayTrackingScratch.Clear();
            foreach (var kv in _godRayTracking)
                if (kv.Value.Amt <= 0.004f && !kv.Value.Seen)
                    _godRayTrackingScratch.Add(kv);
            foreach (var kv in _godRayTrackingScratch)
                _godRayTracking.Remove(kv.Key);

            _godRayLights.Clear();
            foreach (var e in _godRayTracking.Values)
                if (e.Amt > 0.01f)
                    _godRayLights.Add((e.Uv, e.RadiusUv, e.Amt));
            if (_godRayLights.Count > RayRenderSlots)
            {
                _godRayLights.Sort((a, b) => b.amt.CompareTo(a.amt));
                _godRayLights.RemoveRange(RayRenderSlots, _godRayLights.Count - RayRenderSlots);
            }
            return _godRayLights.Count > 0;
        }

        // World-anchor for drifting noise (fog/clouds): the offset must be in units of the
        // VISIBLE world span (viewport, world px) — dividing by the render target's screen px
        // made patterns slide against the world when zoom != 100%.
        private static Vector2 WorldOffset(int w, int h) =>
            new(Game1.viewport.X / (float)Math.Max(1, Game1.viewport.Width),
                Game1.viewport.Y / (float)Math.Max(1, Game1.viewport.Height));

        /// <summary>The player's position in screen UV (0..1), for the radial tilt-shift focus.</summary>
        private static Vector2 PlayerScreenUV()
        {
            if (Game1.player == null)
                return new Vector2(0.5f, 0.5f);
            Vector2 world = Game1.player.Position + new Vector2(32f, 32f); // sprite centre-ish
            Vector2 local = Game1.GlobalToLocal(Game1.viewport, world);
            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);
            return new Vector2(local.X / vw, local.Y / vh);
        }

        /// <summary>Game clock as MINUTES since midnight.
        /// <para>
        /// timeOfDay is HHMM, so 1850 + 10 minutes is 1900 - the number jumps 50 for a ten minute
        /// step. Curves that interpolated on the raw value therefore lurched five times their
        /// normal rate at every hour boundary, which is a visible step in a tint that is supposed
        /// to drift. Minutes make an hour worth sixty and the curves continuous.
        /// </para></summary>
        private static int ClockMinutes()
        {
            int t = Game1.timeOfDay;
            return (t / 100) * 60 + t % 100;
        }

        /// <summary>Fog tint by time of day: neutral haze by day, warm at dusk, blue at night.</summary>
        private static Vector3 FogColor()
        {
            int m = ClockMinutes();
            Vector3 day = new(0.72f, 0.76f, 0.82f);
            Vector3 dusk = new(0.85f, 0.68f, 0.55f);
            Vector3 night = new(0.38f, 0.44f, 0.60f);
            const int Dusk = 17 * 60, Late = 19 * 60 + 30, Night = 21 * 60, Dawn = 6 * 60;
            if (m >= Dusk && m < Late) return Vector3.Lerp(day, dusk, (m - Dusk) / (float)(Late - Dusk));
            if (m >= Late && m < Night) return Vector3.Lerp(dusk, night, (m - Late) / (float)(Night - Late));
            if (m >= Night || m < Dawn) return night;
            return day;
        }

        private static void ComputeAuto(out float temp, out float satMul)
        {
            temp = 0f; satMul = 1f;
            int m = ClockMinutes();
            const int Dusk = 17 * 60, Late = 19 * 60 + 30, Night = 21 * 60, Dawn = 6 * 60;
            if (m >= Dusk && m < Late) temp += 0.25f * ((m - Dusk) / (float)(Late - Dusk));
            else if (m >= Late && m < Night) temp += 0.25f - 0.55f * ((m - Late) / (float)(Night - Late));
            else if (m >= Night || m < Dawn) temp -= 0.30f;

            if (Game1.isRaining) { temp -= 0.12f; satMul *= 0.85f; }
            if (Game1.isSnowing) { temp -= 0.15f; satMul *= 0.90f; }
            if (Game1.season == Season.Winter) temp -= 0.08f;
            else if (Game1.season == Season.Summer) temp += 0.05f;
        }

        /// <summary>
        /// Measure the average scene luminance (downsampled to a tiny RT, read a
        /// frame late to avoid a GPU stall) and ease the exposure toward a target
        /// so bright scenes dim smoothly instead of popping. No-op unless auto is on.
        /// </summary>
        private void UpdateAutoExposure(SpriteBatch spriteBatch)
        {
            // Freeze mode PINS this rather than settling it like the other eased amounts. Every
            // other one eases toward a target computed from the scene; this one meters the frame
            // it is about to grade, so its target moves with its own output and there is no fixed
            // point to land on. Held at neutral so a capture is not multiplied by whatever the
            // meter happened to be reading when freeze was switched on.
            if (Determinism.Frozen)
            {
                _meteredExposure = 1f;
                return;
            }
            // Scene brightness drifts slowly — metering every 4th frame halves the risk of a
            // GPU readback sync without changing the (already eased) response visibly.
            if (Game1.ticks % 4 != 0)
                return;
            _luminanceRenderTarget ??= new RenderTarget2D(_device, 32, 32, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _luminancePixels ??= new Color[32 * 32];

            if (_isLuminancePrimed)
            {
                _luminanceRenderTarget.GetData(_luminancePixels);
                float sum = 0f;
                for (int i = 0; i < _luminancePixels.Length; i++)
                {
                    Color c = _luminancePixels[i];
                    sum += (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
                }
                float lum = sum / _luminancePixels.Length;
                // key/lum > 1 brightens, < 1 dims; clamp so it only gently corrects.
                float target = MathHelper.Clamp(0.5f / Math.Max(lum, 0.05f), 0.7f, 1.15f);
                _meteredExposure += (target - _meteredExposure) * 0.04f; // ~0.7s ease
            }

            _device.SetRenderTarget(_luminanceRenderTarget);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            spriteBatch.Draw(_sceneRenderTarget, new Rectangle(0, 0, 32, 32), Color.White);
            spriteBatch.End();
            _isLuminancePrimed = true;
        }

        /// <summary>0 = still water (pond/river/farm), 1 = ocean/beach (big directional swell).</summary>
        private static float WaterKind()
        {
            var location = Game1.currentLocation;
            // Class first: Beach and the outdoor Ginger Island maps are the vanilla oceans
            // (IslandLocation also covers island CAVES, hence the outdoors guard). Names stay
            // as the fallback for custom coastal maps (SVE capes, resort shores ...).
            if (location is StardewValley.Locations.Beach
                || (location is StardewValley.Locations.IslandLocation && location.IsOutdoors))
                return 1f;
            string n = location?.Name ?? "";
            if (n.Contains("Beach") || n.Contains("Island") || n == "Docks")
                return 1f;
            return 0f;
        }

        /// <summary>Weather/season multipliers for ripple strength, speed, and sparkle.</summary>
        private static void ComputeWaterDynamics(out float strength, out float speed, out float sparkle)
        {
            strength = 1f; speed = 1f; sparkle = 1f;

            if (Game1.isLightning) { strength *= 2.0f; speed *= 1.7f; sparkle *= 0.25f; }   // storm
            else if (Game1.isRaining) { strength *= 1.5f; speed *= 1.4f; sparkle *= 0.4f; } // rain: choppy, no sun glints
            if (Game1.isSnowing) { strength *= 0.8f; speed *= 0.7f; sparkle *= 0.5f; }       // sluggish, overcast

            if (Game1.season == Season.Winter) { speed *= 0.8f; sparkle *= 0.8f; }           // cold, calmer
            else if (Game1.season == Season.Summer) sparkle *= 1.2f;                          // bright sun, more glint
        }
    }
}
