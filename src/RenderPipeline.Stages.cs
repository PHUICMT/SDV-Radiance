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
        /// moon-phase-scaled at night (a dark night has no light for clouds to block).</summary>
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

        private void RenderCloudShadow(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _cloudShadow!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // The hard straight seam reported after long sessions was a float-precision cliff:
            // Time (and so drift = Time*Speed) grows without bound as the session runs, and once
            // the sin()-hash's input gets large enough, frac()/floor() lose precision at one x
            // line and one y line — reading as a hard "L" seam. Wrap ticks into a bounded range
            // (a multiple of 60 so seconds stay whole) so Time never grows large enough to hit it;
            // the wrap period is long enough (100 minutes) that the one-frame pattern jump at the
            // seam is imperceptible during actual play.
            float wrappedTime = (Game1.ticks % 360000) / 60f;

            // Pass 1: generate the cloud-density mask at half-res (WorldOffset uses
            // the full-res dest so the anchor matches the composite step).
            P(fx, "Time")?.SetValue(wrappedTime);
            P(fx, "Speed")?.SetValue(config.CloudShadowSpeed);
            P(fx, "Scale")?.SetValue(config.CloudShadowScale);
            P(fx, "Coverage")?.SetValue(config.CloudShadowCoverage);
            P(fx, "WorldOffset")?.SetValue(WorldOffset(dest.Width, dest.Height));
            fx.CurrentTechnique = fx.Techniques["Mask"];
            Pass(sb, source, rtA, fx);

            // Pass 2/3: separable Gaussian blur -> soft, feathered penumbra edges.
            P(fx, "TexelSize")?.SetValue(new Vector2(1f / rtA.Width, 0f));
            fx.CurrentTechnique = fx.Techniques["BlurH"];
            Pass(sb, rtA, rtB, fx);

            P(fx, "TexelSize")?.SetValue(new Vector2(0f, 1f / rtB.Height));
            fx.CurrentTechnique = fx.Techniques["BlurV"];
            Pass(sb, rtB, rtA, fx);

            // Pass 4: composite the blurred shadow onto the scene.
            P(fx, "Opacity")?.SetValue(config.CloudShadowOpacity * _cloudDayFactor);
            P(fx, "ShadowTexture")?.SetValue(rtA);
            fx.CurrentTechnique = fx.Techniques["Composite"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderGodRays(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _godRays!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // Rays emanate from a real in-world light source (converted to screen UV,
            // so they stay anchored to the scene as the camera pans).
            var lightPos = _lightUV;

            float aspect = Game1.viewport.Width / (float)Math.Max(1, Game1.viewport.Height);

            // Bright pass is GATED to a disk around the real light, so only pixels near THIS
            // light streak into rays — distant bright scenery (flowers, white hair) no longer does.
            P(fx, "Threshold")?.SetValue(config.GodRaysThreshold);
            P(fx, "LightPos")?.SetValue(lightPos);
            P(fx, "LightRadius")?.SetValue(_godRayRadiusUV);
            P(fx, "Aspect")?.SetValue(aspect);
            // Player pixels are not light emitters — same silhouette exclusion as the water.
            var grWho = Game1.player;
            var grMask = ShadowRenderer.PlayerMask;
            var grRect = new Vector4(2f, 2f, -1f, -1f);
            if (grWho != null && grMask != null)
            {
                Rectangle box = grWho.GetBoundingBox();
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom - 10f));
                Vector2 tl = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                grRect = new Vector4(tl.X / dest.Width, tl.Y / dest.Height,
                    (tl.X + ShadowRenderer.PlayerRtW) / dest.Width, (tl.Y + ShadowRenderer.PlayerRtH) / dest.Height);
            }
            P(fx, "PlayerRect")?.SetValue(grRect);
            P(fx, "PlayerMaskTexture")?.SetValue(grMask);
            // With flood GI active, only lit pixels may emit rays (kills rays from bright
            // sprites in unlit corners; lamp glow zones still stream at night).
            bool floodGate = config.FloodLightingEnabled && _flood.Texture != null;
            P(fx, "FloodGate")?.SetValue(floodGate ? 1f : 0f);
            if (floodGate)
            {
                P(fx, "FloodMapTexture")?.SetValue(_flood.Texture);
                P(fx, "FloodTilesPerScreen")?.SetValue(new Vector2(dest.Width / 64f, dest.Height / 64f));
                P(fx, "FloodWorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
                P(fx, "FloodMapOrigin")?.SetValue(_flood.Origin);
                P(fx, "FloodMapSize")?.SetValue(_flood.MapSize);
            }
            fx.CurrentTechnique = fx.Techniques["Bright"];
            Pass(sb, source, rtA, fx);

            P(fx, "LightPos")?.SetValue(lightPos);
            P(fx, "Density")?.SetValue(config.GodRaysDensity);
            P(fx, "Decay")?.SetValue(config.GodRaysDecay);
            P(fx, "Weight")?.SetValue(0.5f);
            fx.CurrentTechnique = fx.Techniques["Rays"];
            Pass(sb, rtA, rtB, fx);

            P(fx, "Intensity")?.SetValue(config.GodRaysIntensity * _godRayAmount);
            P(fx, "RaysTexture")?.SetValue(rtB);
            fx.CurrentTechnique = fx.Techniques["Composite"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderBloom(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var bloom = _bloom!;
            var rtA = _rtA!;
            var rtB = _rtB!;
            int w = dest.Width, h = dest.Height;

            P(bloom, "Threshold")?.SetValue(config.BloomThreshold);
            P(bloom, "TexelSize")?.SetValue(new Vector2(1f / w, 1f / h));
            bloom.CurrentTechnique = bloom.Techniques["BrightPass"];
            Pass(sb, source, rtA, bloom);

            P(bloom, "TexelSize")?.SetValue(new Vector2(1f / rtA.Width, 0f));
            bloom.CurrentTechnique = bloom.Techniques["BlurHorizontal"];
            Pass(sb, rtA, rtB, bloom);

            P(bloom, "TexelSize")?.SetValue(new Vector2(0f, 1f / rtB.Height));
            bloom.CurrentTechnique = bloom.Techniques["BlurVertical"];
            Pass(sb, rtB, rtA, bloom);

            P(bloom, "Intensity")?.SetValue(config.BloomIntensity);
            P(bloom, "BloomTexture")?.SetValue(rtA);
            bloom.CurrentTechnique = bloom.Techniques["Composite"];
            DrawFull(sb, source, dest, bloom);
        }

        private void RenderFog(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _fog!;
            P(fx, "Time")?.SetValue(Time());
            P(fx, "Speed")?.SetValue(config.FogSpeed);
            P(fx, "Scale")?.SetValue(config.FogScale);
            P(fx, "Density")?.SetValue(config.FogDensity);
            P(fx, "TopBias")?.SetValue(config.FogTopBias);
            P(fx, "FogColor")?.SetValue(FogColor());
            P(fx, "WorldOffset")?.SetValue(WorldOffset(dest.Width, dest.Height));
            fx.CurrentTechnique = fx.Techniques["Fog"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderTiltShift(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _tiltShift!;
            var rtA = _rtA!;
            var rtB = _rtB!;

            // blur at half-res: source(full) -> rtA (H) -> rtB (V)
            P(fx, "TexelSize")?.SetValue(new Vector2(1f / rtA.Width, 0f));
            fx.CurrentTechnique = fx.Techniques["BlurH"];
            Pass(sb, source, rtA, fx);

            P(fx, "TexelSize")?.SetValue(new Vector2(0f, 1f / rtB.Height));
            fx.CurrentTechnique = fx.Techniques["BlurV"];
            Pass(sb, rtA, rtB, fx);

            // composite sharp + blurred by vertical position.
            // Config stores intuitive "blur amount" (higher = more blur from that edge);
            // convert to sharp-band edges: more top blur pushes TopEdge down, more
            // bottom blur pulls BottomEdge up.
            P(fx, "TopEdge")?.SetValue(MathHelper.Clamp(config.TiltShiftTopRatio, 0f, 1f) * 0.5f);
            P(fx, "BottomEdge")?.SetValue(1f - MathHelper.Clamp(config.TiltShiftBottomRatio, 0f, 1f) * 0.5f);
            P(fx, "Strength")?.SetValue(config.TiltShiftStrength);
            P(fx, "Mode")?.SetValue(config.TiltShiftMode == TiltShiftFocus.Radial ? 1f : 0f);
            P(fx, "Center")?.SetValue(PlayerScreenUV());
            P(fx, "Aspect")?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            P(fx, "RadRadius")?.SetValue(MathHelper.Clamp(config.TiltShiftRadius, 0.05f, 0.9f));
            P(fx, "BlurTexture")?.SetValue(rtB);
            fx.CurrentTechnique = fx.Techniques["Composite"];
            DrawFull(sb, source, dest, fx);
        }

        private void ColorGrade(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _colorGrade!;
            float temp = config.ColorGradeTemperature;
            float sat = config.ColorGradeSaturation;
            if (config.ColorGradeAuto)
            {
                ComputeAuto(out float autoTemp, out float autoSatMul);
                temp += autoTemp;
                sat *= autoSatMul;
            }

            // _meteredExposure is measured & eased per frame in UpdateAutoExposure
            // (1.0 when auto is off), so bright scenes dim smoothly with no pop.
            P(fx, "Strength")?.SetValue(MathHelper.Clamp(config.ColorGradeStrength, 0f, 1f));
            P(fx, "Contrast")?.SetValue(config.ColorGradeContrast);
            P(fx, "Saturation")?.SetValue(sat);
            P(fx, "Temperature")?.SetValue(MathHelper.Clamp(temp, -1f, 1f));
            P(fx, "Brightness")?.SetValue(config.ColorGradeBrightness * _meteredExposure);
            P(fx, "ToneMap")?.SetValue(config.ColorGradeToneMap ? 1f : 0f);
            fx.CurrentTechnique = fx.Techniques["ColorGrade"];
            DrawFull(sb, source, dest, fx);
        }

        private bool _floodOccReady;
        private readonly Vector2[] _floodLightPos = new Vector2[8];
        private readonly Vector4[] _floodLightCol = new Vector4[8];

        private void RenderFloodLight(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _floodFx!;
            P(fx, "LightMapTexture")?.SetValue(_flood.Texture);
            P(fx, "TilesPerScreen")?.SetValue(new Vector2(dest.Width / 64f, dest.Height / 64f));
            P(fx, "WorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            P(fx, "MapOrigin")?.SetValue(_flood.Origin);
            P(fx, "MapSize")?.SetValue(_flood.MapSize);
            P(fx, "Strength")?.SetValue(MathHelper.Clamp(config.FloodLightingStrength, 0f, 1f));
            P(fx, "AmbientFloor")?.SetValue(0.10f);

            // Direct pools with per-light shadows: the brightest 8 on-screen lights (from
            // BuildLightList) + the occluder mask. Direct is scaled DOWN vs the classic
            // lighting stage because the flood map already carries the indirect spill.
            int n = 0;
            for (int i = 0; i < _lightCount && n < 8; i++, n++)
            {
                _floodLightPos[n] = _lightPos[i];
                var d = _lightData[i];
                _floodLightCol[n] = new Vector4(d.X * 0.55f, d.Y * 0.55f, d.Z * 0.55f, d.W);
            }
            for (int i = n; i < 8; i++) { _floodLightPos[i] = Vector2.Zero; _floodLightCol[i] = Vector4.Zero; }
            P(fx, "LightPosArr")?.SetValue(_floodLightPos);
            P(fx, "LightColArr")?.SetValue(_floodLightCol);
            P(fx, "DirectCount")?.SetValue((float)(_floodOccReady ? n : 0));
            P(fx, "Aspect")?.SetValue(dest.Width / (float)Math.Max(1, dest.Height));
            P(fx, "OccluderTexture")?.SetValue(_occluderMask);
            P(fx, "OccOrigin")?.SetValue(new Vector2((float)Math.Floor(Game1.viewport.X / 64f), (float)Math.Floor(Game1.viewport.Y / 64f)));
            P(fx, "OccMapSize")?.SetValue(_occMaskSize);
            P(fx, "ShadowStrength")?.SetValue(MathHelper.Clamp(config.FloodShadowStrength, 0f, 1f));

            fx.CurrentTechnique = fx.Techniques["FloodLight"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderWater(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _water!;
            // Weather/season drive how agitated the water is: choppier & faster in
            // rain/storm, sluggish in winter; sparkle fades when there's no sun.
            ComputeWaterDynamics(out float strengthMul, out float speedMul, out float sparkleMul);
            P(fx, "Time")?.SetValue(Time());
            P(fx, "Strength")?.SetValue(config.WaterStrength * strengthMul);
            P(fx, "Speed")?.SetValue(config.WaterSpeed * speedMul);
            P(fx, "Sparkle")?.SetValue(config.WaterSparkle * sparkleMul);
            P(fx, "ReflectStrength")?.SetValue(config.WaterReflection ? config.WaterReflectStrength : 0f);
            P(fx, "WaterKind")?.SetValue(WaterKind());
            P(fx, "TilesPerScreen")?.SetValue(_waterTilesPerScreen);
            P(fx, "WorldTileOffset")?.SetValue(_waterWorldTileOffset);
            P(fx, "MaskSize")?.SetValue(_waterMaskSize);
            P(fx, "MaskOrigin")?.SetValue(new Vector2(_lastWaterTx, _lastWaterTy));
            P(fx, "MaskTexture")?.SetValue(_waterMask);
            P(fx, "MaskCoreTexture")?.SetValue(_waterMaskCore);
            P(fx, "SparkleDensity")?.SetValue(config.WaterSparkleDensity);
            // Player SILHOUETTE mask (the shadow system's per-frame bake) in buffer UV —
            // ring-tile water effects skip exactly the player's own pixels, so a blue outfit
            // on a pier never ripples while the water right beside them stays animated.
            var who = Game1.player;
            var pmask = ShadowRenderer.PlayerMask;
            var playerRect = new Vector4(2f, 2f, -1f, -1f);   // empty box (never matches)
            if (who != null && pmask != null)
            {
                Rectangle box = who.GetBoundingBox();
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom - 10f));
                Vector2 tl = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                playerRect = new Vector4(tl.X / dest.Width, tl.Y / dest.Height,
                    (tl.X + ShadowRenderer.PlayerRtW) / dest.Width, (tl.Y + ShadowRenderer.PlayerRtH) / dest.Height);
            }
            P(fx, "PlayerRect")?.SetValue(playerRect);
            P(fx, "PlayerMaskTexture")?.SetValue(pmask);

            // Time-of-day / weather dressing: golden-hour sparkle, star reflections and
            // lamp glimmer after dusk, raindrop rings while raining.
            int tnow = Game1.timeOfDay;
            int mins = (tnow / 100) * 60 + tnow % 100;
            float sunWarm = 0f;
            if (!Game1.isRaining && tnow < 1900)
            {
                float dd = MathHelper.Clamp((tnow - 1200) / 600f, -1f, 1f);
                sunWarm = MathHelper.Clamp((Math.Abs(dd) - 0.55f) / 0.45f, 0f, 1f);
            }
            float nightGlow = MathHelper.Clamp((mins - 1140) / 90f, 0f, 1f);   // 19:00 → 20:30
            P(fx, "SunWarm")?.SetValue(sunWarm);
            P(fx, "NightGlow")?.SetValue(nightGlow);
            P(fx, "MoonGlow")?.SetValue(ShadowRenderer.MoonStrength());
            P(fx, "RainAmt")?.SetValue(Game1.isRaining ? 1f : 0f);

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
                    _lightArr[lc++] = new Vector4(sp.X / dest.Width, sp.Y / dest.Height, kv.radius.Value, 0.9f);
                }
            }
            P(fx, "LightCount")?.SetValue((float)lc);
            P(fx, "Lights")?.SetValue(_lightArr);

            // Wading: are the player's feet on water pixels? (mask texel = 4 world px)
            float pin = 0f;
            if (who != null && _waterPixBuf != null && _waterMask != null)
            {
                Rectangle bb = who.GetBoundingBox();
                int mxp = bb.Center.X / 4 - _lastWaterTx * 16;
                int myp = (bb.Bottom - 4) / 4 - _lastWaterTy * 16;
                if (mxp >= 0 && myp >= 0 && mxp < _waterMask.Width && myp < _waterMask.Height
                    && _waterPixBuf[myp * _waterMask.Width + mxp].R > 100)
                    pin = 1f;
            }
            P(fx, "PlayerInWater")?.SetValue(pin);

            fx.CurrentTechnique = fx.Techniques["Water"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderFinishing(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _finishing!;
            P(fx, "VignetteStrength")?.SetValue(config.VignetteEnabled ? config.VignetteStrength : 0f);
            // Map the 0..1 UI value to a tiny UV offset so it stays subtle on pixel art.
            P(fx, "CAStrength")?.SetValue(config.ChromaticAberrationEnabled ? config.ChromaticAberrationStrength * 0.03f : 0f);
            fx.CurrentTechnique = fx.Techniques["Finishing"];
            DrawFull(sb, source, dest, fx);
        }

        private void RenderLighting(SpriteBatch sb, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var fx = _lighting!;
            P(fx, "AmbientColor")?.SetValue(ComputeLightingAmbient(config));
            P(fx, "Aspect")?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            P(fx, "LightPos")?.SetValue(_lightPos);
            P(fx, "LightData")?.SetValue(_lightData);
            // Allow pools to slightly exceed 1 so lamps glow a touch; keep it modest.
            P(fx, "Overbright")?.SetValue(1.0f + 0.4f * MathHelper.Clamp(config.LightingBoost, 0f, 2f));
            // Occluder shadows: only when enabled AND a mask was built this frame.
            if (_shadowsReady && _occluderMask != null)
            {
                P(fx, "ShadowStrength")?.SetValue(MathHelper.Clamp(config.LightingShadowStrength, 0f, 1f));
                P(fx, "OccluderTexture")?.SetValue(_occluderMask);
                P(fx, "OccTilesPerScreen")?.SetValue(_occTilesPerScreen);
                P(fx, "OccWorldTileOffset")?.SetValue(_occWorldTileOffset);
                P(fx, "OccMaskSize")?.SetValue(_occMaskSize);
            }
            else
            {
                // Disabled: bind a valid texture and 0 strength so nothing samples garbage.
                P(fx, "ShadowStrength")?.SetValue(0f);
                P(fx, "OccluderTexture")?.SetValue(source);
            }
            fx.CurrentTechnique = fx.Techniques["Lighting"];
            DrawFull(sb, source, dest, fx);
        }

        /// <summary>Screen-UV + UV-radius of the largest-radius real light source currently on screen, if any.</summary>
        private static bool TryGetLightUV(out Vector2 uv, out float radiusUV)
        {
            uv = Vector2.Zero;
            radiusUV = 0.25f;
            var lights = Game1.currentLightSources;
            if (lights == null || lights.Count == 0)
                return false;

            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);
            float best = -1f;

            foreach (var kv in lights)
            {
                LightSource ls = kv.Value;
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                float u = local.X / vw;
                float v = local.Y / vh;
                if (u < -0.25f || u > 1.25f || v < -0.25f || v > 1.25f)
                    continue; // off-screen

                float r = ls.radius.Value;
                if (r > best)
                {
                    best = r;
                    uv = new Vector2(u, v);
                    // radius.Value is ~tiles; on-screen glow ≈ radius*64px. Give the rays a little
                    // more reach than the glow, so only pixels near THIS light streak (not distant
                    // bright scenery like flowers/white hair).
                    radiusUV = MathHelper.Clamp(r * 64f * 2.2f / vh, 0.12f, 0.6f);
                }
            }
            return best > 0f;
        }

        private static Vector2 WorldOffset(int w, int h) =>
            new(Game1.viewport.X / (float)Math.Max(1, w), Game1.viewport.Y / (float)Math.Max(1, h));

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

        /// <summary>Fog tint by time of day: neutral haze by day, warm at dusk, blue at night.</summary>
        private static Vector3 FogColor()
        {
            int t = Game1.timeOfDay;
            Vector3 day = new(0.72f, 0.76f, 0.82f);
            Vector3 dusk = new(0.85f, 0.68f, 0.55f);
            Vector3 night = new(0.38f, 0.44f, 0.60f);
            if (t >= 1700 && t < 1930) return Vector3.Lerp(day, dusk, (t - 1700) / 230f);
            if (t >= 1930 && t < 2100) return Vector3.Lerp(dusk, night, (t - 1930) / 170f);
            if (t >= 2100 || t < 600) return night;
            return day;
        }

        private static void ComputeAuto(out float temp, out float satMul)
        {
            temp = 0f; satMul = 1f;
            int t = Game1.timeOfDay;
            if (t >= 1700 && t < 1930) temp += 0.25f * ((t - 1700) / 230f);
            else if (t >= 1930 && t < 2100) { float k = (t - 1930) / 170f; temp += 0.25f - 0.59f * k; satMul *= 1f - 0.18f * k; }
            // Deep night: cool AND desaturated. Full saturation kept the warm reddish
            // ground/paths glaring against the cool dark — a calmer, bluer, lower-sat night
            // reads as moonlit rather than eye-straining.
            else if (t >= 2100 || t < 600) { temp -= 0.45f; satMul *= 0.62f; }

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
        private void UpdateAutoExposure(SpriteBatch sb)
        {
            // Scene brightness drifts slowly — metering every 4th frame halves the risk of a
            // GPU readback sync without changing the (already eased) response visibly.
            if (Game1.ticks % 4 != 0)
                return;
            _lumRT ??= new RenderTarget2D(_device, 32, 32, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _lumBuf ??= new Color[32 * 32];

            if (_lumPrimed)
            {
                _lumRT.GetData(_lumBuf);
                float sum = 0f;
                for (int i = 0; i < _lumBuf.Length; i++)
                {
                    Color c = _lumBuf[i];
                    sum += (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
                }
                float lum = sum / _lumBuf.Length;
                // key/lum > 1 brightens, < 1 dims; clamp so it only gently corrects.
                float target = MathHelper.Clamp(0.5f / Math.Max(lum, 0.05f), 0.7f, 1.15f);
                _meteredExposure += (target - _meteredExposure) * 0.04f; // ~0.7s ease
            }

            _device.SetRenderTarget(_lumRT);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            sb.Draw(_sceneRT, new Rectangle(0, 0, 32, 32), Color.White);
            sb.End();
            _lumPrimed = true;
        }

        /// <summary>0 = still water (pond/river/farm), 1 = ocean/beach (big directional swell).</summary>
        private static float WaterKind()
        {
            string n = Game1.currentLocation?.Name ?? "";
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
