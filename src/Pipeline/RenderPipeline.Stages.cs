using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            int trulyDark;
            try { trulyDark = Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { trulyDark = 2000; }
            float minutesNow = GameClock.MinutesNow();
            int trulyDarkMinutes = (trulyDark / 100) * 60 + trulyDark % 100;
            float moon = 0.35f * ShadowRenderer.MoonStrength();
            if (minutesNow >= trulyDarkMinutes)
                return moon;
            return Math.Max(moon, MathHelper.Clamp((trulyDarkMinutes - minutesNow) / 40f, 0f, 1f));
        }

        /// <summary>Night ramp 0→1 over 19:00→21:00 (0 by day). Shared by the night-only
        /// touches: warmer bloom, a touch more vignette, and the automatic blue night mist.</summary>
        private static float NightFactorNow()
            => MathHelper.Clamp((GameClock.MinutesNow() - 1140) / 120f, 0f, 1f);

        private void RenderCloudShadow(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _cloudShadow!;
            var halfScratchA = _halfResolutionScratchA!;
            var halfScratchB = _halfResolutionScratchB!;

            // The hard straight seam reported after long sessions was a float-precision cliff:
            // Time (and so drift = Time*Speed) grows without bound as the session runs, and once
            // the sin()-hash's input gets large enough, frac()/floor() lose precision at one x
            // line and one y line, reading as a hard "L" seam. Determinism.ShaderSeconds stays
            // bounded, and restarts at the start of a day, behind the black screen.
            float wrappedTime = Determinism.ShaderSeconds;

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
            // The afternoon before rain travels a short way down the same road the overcast takes:
            // more ground under cloud and the banks drawn together into fewer masses. It stops well
            // short of the overcast itself, because the rain has not arrived and the sun is still
            // out; what the eye should read is a sky thickening, not a sky that has closed.
            float stormWarning = _stormWarningEased;
            GetParam(effect, "Speed")?.SetValue(config.CloudShadowSpeed * MathHelper.Lerp(1f, 0.6f, overcast));
            GetParam(effect, "Scale")?.SetValue(config.CloudShadowScale * MathHelper.Lerp(1f, 0.65f, overcast));
            GetParam(effect, "Coverage")?.SetValue(MathHelper.Clamp(
                MathHelper.Lerp(config.CloudShadowCoverage, config.CloudShadowCoverage + 0.32f, overcast)
                + StormWarningExtraCoverage * stormWarning, 0f, 0.92f));
            GetParam(effect, "Count")?.SetValue(MathHelper.Lerp(config.CloudShadowCount, config.CloudShadowCount * 0.4f, overcast)
                * MathHelper.Lerp(1f, 0.7f, stormWarning));
            // Small maps: the cluster field spans <1 light/dark cycle across a tiny map, so the
            // whole thing can fall in one dark bank (the "cutscene too dark" report). Boost the
            // cluster frequency by how many times the map fits inside the viewport.
            GetParam(effect, "SmallMapBoost")?.SetValue(SmallMapCloudBoost());
            GetParam(effect, "WorldOffset")?.SetValue(WorldOffset());
            GetParam(effect, "NoiseTexture")?.SetValue(NoiseTex());
            effect.CurrentTechnique = effect.Techniques["Mask"];
            Pass(spriteBatch, source, halfScratchA, effect);

            // Pass 2/3: separable Gaussian blur -> soft, feathered penumbra edges.
            GetParam(effect, "TexelSize")?.SetValue(new Vector2(1f / halfScratchA.Width, 0f));
            effect.CurrentTechnique = effect.Techniques["BlurH"];
            Pass(spriteBatch, halfScratchA, halfScratchB, effect);

            // The last blur lands in the KEPT target, not the scratch one: god rays and bloom
            // rewrite the scratch buffers later this same frame, and the sun shafts need this
            // mask still intact NEXT frame (see _cloudMaskKeep). Same pass, different address.
            // The keep is per screen (ScreenState): a screen whose keep was made for another
            // frame size, or that has none yet, gets its own here rather than borrowing.
            if (_cloudMaskKeep != null && (_cloudMaskKeep.IsDisposed || _cloudMaskKeep.Width != halfScratchA.Width || _cloudMaskKeep.Height != halfScratchA.Height))
            {
                if (!_cloudMaskKeep.IsDisposed) _cloudMaskKeep.Dispose();
                _cloudMaskKeep = null;
                _cloudMaskTick = int.MinValue;
            }
            _cloudMaskKeep ??= CreateRenderTarget(halfScratchA.Width, halfScratchA.Height, halfScratchA.Format);
            var keep = _cloudMaskKeep;
            GetParam(effect, "TexelSize")?.SetValue(new Vector2(0f, 1f / halfScratchB.Height));
            effect.CurrentTechnique = effect.Techniques["BlurV"];
            Pass(spriteBatch, halfScratchB, keep, effect);

            // Pass 4: composite the blurred shadow onto the scene.
            float cloudOpacity = config.CloudShadowOpacity * _cloudDayFactor * _fadeCloud;
            GetParam(effect, "Opacity")?.SetValue(cloudOpacity);
            // Day: clouds shade EVERYTHING (white eyes/flowers included — the sun is the
            // light). Night: near-white lamp/fire cores resist the moon-cloud shadow.
            GetParam(effect, "LightProtect")?.SetValue(NightFactorNow());
            GetParam(effect, "ShadowInk")?.SetValue(ShadowRenderer.ShadowInkVector);
            GetParam(effect, "ShadowTexture")?.SetValue(keep);
            effect.CurrentTechnique = effect.Techniques["Composite"];
            DrawFull(spriteBatch, source, destination, effect);

            // What the sun shafts will read back next frame, and the facts they need to trust
            // it: when it was drawn, from where, and how dark the sky actually was.
            _cloudMaskTick = Determinism.Ticks;
            _cloudMaskTileOffset = new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f);
            _cloudMaskStrength = cloudOpacity;
        }

        /// <summary>
        /// Multiply the picture down where a building's shadow falls.
        /// </summary>
        /// <remarks>
        /// A building's shadow covers dozens of tiles, which is more than a sprite in the sort can
        /// do anything sensible with: put it over the grass and it goes over the building too, put
        /// it under the building and every tuft of grass punches a hole in it. So it arrives as a
        /// coverage mask (<see cref="ShadowRenderer.BuildingSunShadowMask"/>) and is applied here
        /// as a change in the light, which is what it is. Grass standing in the shadow is darkened
        /// rather than sorted in front of it.
        /// <para>
        /// The cloud shadow's own shader does this job already and does it well: blur the mask into
        /// a penumbra, then multiply. Reused rather than copied, so there is one place where "a
        /// shadow lying over the whole picture" is written down, and no second shader to compile.
        /// </para>
        /// </remarks>
        private void RenderBuildingShadow(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            Effect? effect = _cloudShadow;
            Texture2D? mask = ShadowRenderer.BuildingSunShadowMask;
            if (effect == null || mask == null || !ShadowRenderer.BuildingSunShadowReady)
            {
                // The stage stays listed while its presence eases out, so a frame with no mask has
                // to hand the picture on untouched rather than skip and leave the chain a hole.
                DrawFull(spriteBatch, source, destination, null!);
                return;
            }
            var halfScratchA = _halfResolutionScratchA!;

            // The stamped mask holds its shape in ALPHA - the silhouettes are baked black, and a
            // SpriteBatch tint can only darken, so their colour channels are zero. Everything after
            // this reads red, so move the shape into it first.
            //
            // And that is all that happens to it. The penumbra is already in the mask, put there
            // when it was stamped, because the buildings are cut out of it afterwards: blurring
            // here would spread that cut outwards and leave a bright gap hugging every wall.
            effect.CurrentTechnique = effect.Techniques["AlphaToCoverage"];
            Pass(spriteBatch, mask, halfScratchA, effect);

            GetParam(effect, "Opacity")?.SetValue(BuildingShadowOpacity(config));
            // The sun is the light here, so a building's shadow shades everything under it,
            // white art included. That is the same call the cloud shadow makes by day, and for
            // the same reason: anything allowed to resist punches a hole in the shadow.
            GetParam(effect, "LightProtect")?.SetValue(0f);
            GetParam(effect, "ShadowInk")?.SetValue(ShadowRenderer.ShadowInkVector);
            GetParam(effect, "ShadowTexture")?.SetValue(halfScratchA);
            effect.CurrentTechnique = effect.Techniques["Composite"];
            DrawFull(spriteBatch, source, destination, effect);
        }

        /// <summary>How dark a building's shadow gets, riding the same strength dial every other
        /// shadow rides and its own presence fade.</summary>
        private float BuildingShadowOpacity(ModConfig config)
            => MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f) * 0.7f * _fadeBuildingShadow;

        /// <summary>How many times the current map fits inside the viewport (>=1), clamped.
        /// 1 = map at least as big as the screen; higher = smaller map, more cloud banks.</summary>
        private static float SmallMapCloudBoost()
        {
            var location = Game1.currentLocation;
            var layer = location?.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            if (layer == null)
                return 1f;
            float mapTilesWide = Math.Max(1, layer.LayerWidth), mapTilesTall = Math.Max(1, layer.LayerHeight);
            float viewportTilesWide = Game1.viewport.Width / 64f, viewportTilesTall = Game1.viewport.Height / 64f;
            return MathHelper.Clamp(Math.Max(viewportTilesWide / mapTilesWide, viewportTilesTall / mapTilesTall), 1f, 4f);
        }

        private void RenderBloom(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var bloom = _bloom!;
            var halfScratchA = _halfResolutionScratchA!;
            var halfScratchB = _halfResolutionScratchB!;
            int width = destination.Width, height = destination.Height;

            // At night, bloom blooms more (lower threshold, a bit stronger) and turns warm so
            // lamps/windows glow amber.
            float bloomNight = NightFactorNow();

            GetParam(bloom, "Threshold")?.SetValue(MathHelper.Clamp(config.BloomThreshold - 0.08f * bloomNight, 0f, 1f));
            GetParam(bloom, "TexelSize")?.SetValue(new Vector2(1f / width, 1f / height));
            bloom.CurrentTechnique = bloom.Techniques["BrightPass"];
            Pass(spriteBatch, source, halfScratchA, bloom);

            GetParam(bloom, "TexelSize")?.SetValue(new Vector2(1f / halfScratchA.Width, 0f));
            bloom.CurrentTechnique = bloom.Techniques["BlurHorizontal"];
            Pass(spriteBatch, halfScratchA, halfScratchB, bloom);

            GetParam(bloom, "TexelSize")?.SetValue(new Vector2(0f, 1f / halfScratchB.Height));
            bloom.CurrentTechnique = bloom.Techniques["BlurVertical"];
            Pass(spriteBatch, halfScratchB, halfScratchA, bloom);

            GetParam(bloom, "Intensity")?.SetValue(config.BloomIntensity * (1f + 0.2f * bloomNight));
            GetParam(bloom, "EmissiveBoost")?.SetValue(MathHelper.Clamp(config.BloomEmissiveBoost, 0f, 1f));
            GetParam(bloom, "BloomWarm")?.SetValue(bloomNight);
            GetParam(bloom, "BloomTexture")?.SetValue(halfScratchA);
            bloom.CurrentTechnique = bloom.Techniques["Composite"];
            DrawFull(spriteBatch, source, destination, bloom);
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
            const int NoiseSize = 256;
            var accumulated = new float[NoiseSize * NoiseSize];
            float amplitude = 0.5f, amplitudeSum = 0f;
            int cells = 4;                       // 4,8,16,32,64 — every octave tiles at 256
            for (int octave = 0; octave < 5; octave++)
            {
                for (int y = 0; y < NoiseSize; y++)
                {
                    float latticeY = (float)y / NoiseSize * cells;
                    int cellY = (int)latticeY;
                    float weightY = latticeY - cellY;
                    weightY = weightY * weightY * weightY * (weightY * (weightY * 6f - 15f) + 10f);
                    for (int x = 0; x < NoiseSize; x++)
                    {
                        float latticeX = (float)x / NoiseSize * cells;
                        int cellX = (int)latticeX;
                        float weightX = latticeX - cellX;
                        weightX = weightX * weightX * weightX * (weightX * (weightX * 6f - 15f) + 10f);
                        float cornerTopLeft = NoiseHash(cellX, cellY, octave, cells), cornerTopRight = NoiseHash(cellX + 1, cellY, octave, cells);
                        float cornerBottomLeft = NoiseHash(cellX, cellY + 1, octave, cells), cornerBottomRight = NoiseHash(cellX + 1, cellY + 1, octave, cells);
                        accumulated[y * NoiseSize + x] += amplitude * MathHelper.Lerp(MathHelper.Lerp(cornerTopLeft, cornerTopRight, weightX), MathHelper.Lerp(cornerBottomLeft, cornerBottomRight, weightX), weightY);
                    }
                }
                amplitudeSum += amplitude; amplitude *= 0.5f; cells *= 2;
            }
            var data = new Color[NoiseSize * NoiseSize];
            for (int i = 0; i < accumulated.Length; i++)
            {
                byte grey = (byte)MathHelper.Clamp(accumulated[i] / amplitudeSum * 255f, 0f, 255f);
                data[i] = new Color(grey, grey, grey, (byte)255);
            }
            _noiseTexture = VramTally.Track(new Texture2D(_device, NoiseSize, NoiseSize), "fog noise");
            _noiseTexture.SetData(data);
            return _noiseTexture;
        }
        private static float NoiseHash(int latticeX, int latticeY, int octave, int cells)
        {
            latticeX = ((latticeX % cells) + cells) % cells;   // wrap the lattice → texture tiles perfectly
            latticeY = ((latticeY % cells) + cells) % cells;
            uint hash = (uint)(latticeX * 374761393 + latticeY * 668265263 + (octave + 1) * 2246822519);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash ^ (hash >> 16)) & 0xFFFF) / 65535f;
        }

        private void RenderFog(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _fogEffect!;
            // One shader pass renders the blend of two separate effects: DAY fog and NIGHT
            // mist. Both are the same sparse drifting-wisp look (Patchiness 1 — the author
            // liked the night wisps and wants day fog to match); each keeps its own density
            // slider, day also keeps its scale/speed sliders. Amounts crossfade over dusk.
            float total = _fogDayAmount + _fogMistAmount;
            float mistWeight = total > 0f ? _fogMistAmount / total : 0f;
            GetParam(effect, "Time")?.SetValue(Time());
            GetParam(effect, "Speed")?.SetValue(MathHelper.Lerp(config.FogSpeed, config.FogNightMistSpeed, mistWeight));
            GetParam(effect, "Scale")?.SetValue(MathHelper.Lerp(config.FogScale, 3.2f, mistWeight));
            // Our rain thickens the air a touch: one scalar into the stage that already exists,
            // gated on the replacement being live so vanilla days cannot change by a wisp.
            GetParam(effect, "Density")?.SetValue(total * (1f + 0.15f * PrecipitationSystem.ActiveRainPresence));
            GetParam(effect, "Patchiness")?.SetValue(1f);
            GetParam(effect, "Coverage")?.SetValue(MathHelper.Lerp(config.FogCoverage, config.FogNightMistCoverage, mistWeight));
            GetParam(effect, "TopBias")?.SetValue(config.FogTopBias);
            GetParam(effect, "NoiseTexture")?.SetValue(NoiseTex());
            GetParam(effect, "FogColor")?.SetValue(FogColor());
            GetParam(effect, "WorldOffset")?.SetValue(WorldOffset());
            GetParam(effect, "ScreenPixels")?.SetValue(new Vector2(Game1.viewport.Width, Game1.viewport.Height));
            // The wisps near a lamp take its light, read off the lightmap the lamps already
            // painted. Only the night mist does this (a day fog has the sun, not lamps), and only
            // while a lightmap exists to read; at 0 the shader never touches it.
            bool lightmapOnHand = config.FloodLightingEnabled
                && ((_flood?.Texture) != null || (_cascadesReady && (_cascades?.Texture) != null));
            float lampGlow = lightmapOnHand ? MathHelper.Clamp(config.FogNightMistLampGlow, 0f, 1f) * mistWeight : 0f;
            GetParam(effect, "LampGlow")?.SetValue(lampGlow);
            if (lampGlow > 0f)
            {
                SetFloodMapParams(effect, config);
                GetParam(effect, "SkyLevel")?.SetValue(FloodLightmap.SkyColour(outdoors: true, config));
            }
            effect.CurrentTechnique = effect.Techniques["Fog"];
            DrawFull(spriteBatch, source, destination, effect);
        }

        private void RenderTiltShift(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _tiltShift!;
            var halfScratchA = _halfResolutionScratchA!;
            var halfScratchB = _halfResolutionScratchB!;

            // blur at half-res: source(full) -> rtA (H) -> rtB (V)
            GetParam(effect, "TexelSize")?.SetValue(new Vector2(1f / halfScratchA.Width, 0f));
            effect.CurrentTechnique = effect.Techniques["BlurH"];
            Pass(spriteBatch, source, halfScratchA, effect);

            GetParam(effect, "TexelSize")?.SetValue(new Vector2(0f, 1f / halfScratchB.Height));
            effect.CurrentTechnique = effect.Techniques["BlurV"];
            Pass(spriteBatch, halfScratchA, halfScratchB, effect);

            // composite sharp + blurred by vertical position.
            // Config stores intuitive "blur amount" (higher = more blur from that edge);
            // convert to sharp-band edges: more top blur pushes TopEdge down, more
            // bottom blur pulls BottomEdge up.
            GetParam(effect, "TopEdge")?.SetValue(MathHelper.Clamp(config.TiltShiftTopRatio, 0f, 1f) * 0.5f);
            GetParam(effect, "BottomEdge")?.SetValue(1f - MathHelper.Clamp(config.TiltShiftBottomRatio, 0f, 1f) * 0.5f);
            // Indoors the whole room spans a couple of tiles of depth, so the same blur that
            // reads as distance in a field reads as a smear on the furniture against the far
            // wall. The scale keeps the shape of the effect and shortens its reach.
            float indoorScale = MathHelper.Lerp(1f, MathHelper.Clamp(config.TiltShiftIndoorAmount, 0f, 1f), _tiltIndoorEase);
            GetParam(effect, "Strength")?.SetValue(config.TiltShiftStrength * _fadeTilt * indoorScale);
            Approach(ref _tiltModeEase, config.TiltShiftMode == TiltShiftFocus.Radial ? 1f : 0f, 0.08f);
            GetParam(effect, "Mode")?.SetValue(_tiltModeEase);
            GetParam(effect, "Center")?.SetValue(PlayerScreenUV());
            GetParam(effect, "Aspect")?.SetValue(destination.Height > 0 ? destination.Width / (float)destination.Height : 1f);
            GetParam(effect, "RadRadius")?.SetValue(MathHelper.Clamp(config.TiltShiftRadius, 0.05f, 0.9f));
            GetParam(effect, "Feather")?.SetValue(MathHelper.Clamp(config.TiltShiftFeather, 0f, 1f));
            GetParam(effect, "BlurTexture")?.SetValue(halfScratchB);
            effect.CurrentTechnique = effect.Techniques["Composite"];
            DrawFull(spriteBatch, source, destination, effect);
        }

        // ---- 3D LUT ----------------------------------------------------------------------
        // One LUT is loaded at a time and kept until the name changes: it is a 128 KB texture
        // read off disk, and the grade runs every frame.
        private Texture2D? _lutTexture;
        private string _lutLoaded = "";

        /// <summary>
        /// Hand the configured LUT to the effect and return how strongly to apply it, or 0 when
        /// there is no LUT to apply.
        /// <para>
        /// Through the effect's own texture parameter, NOT by binding a device texture slot by
        /// hand: DrawFull calls SetRenderTarget, which unbinds the slots, so a hand-bound slot was
        /// already empty when the shader ran and every pixel sampled black. The shader's sampler
        /// asks for linear filtering and insets its taps by half a texel, so the filtering never
        /// crosses into the neighbouring blue slice of the strip.
        /// </para>
        /// </summary>
        private float BindLut(Effect effect, ModConfig config)
        {
            string wantedLut = (config.ColorGradeLut ?? "").Trim();
            float amount = MathHelper.Clamp(config.ColorGradeLutAmount, 0f, 1f);
            if (wantedLut.Length == 0 || amount <= 0f)
                return 0f;
            if (!string.Equals(wantedLut, _lutLoaded, StringComparison.OrdinalIgnoreCase) || _lutTexture == null)
            {
                _lutTexture = LoadTextureAt(LutCatalog.Resolve(wantedLut));
                _lutLoaded = wantedLut;
                if (_lutTexture == null)
                    _monitor.Log($"Colour LUT \"{wantedLut}\" not found in assets/luts or {LutCatalog.UserDir} - grading without it.", LogLevel.Warn);
                else if (_lutTexture.Width != 1024 || _lutTexture.Height != 32)
                    _monitor.Log($"Colour LUT \"{wantedLut}\" is {_lutTexture.Width}x{_lutTexture.Height}; "
                                 + "a 32-cube strip is 1024x32. It will be read as if it were one.", LogLevel.Warn);
            }
            if (_lutTexture == null)
                return 0f;
            GetParam(effect, "LutTexture")?.SetValue(_lutTexture);
            return amount;
        }

        private void ColorGrade(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _colorGrade!;
            // The stage may run for the BLUE-LIGHT FILTER alone (grading toggled off): the
            // artistic controls go neutral so only the warm eye-comfort shift applies.
            bool gradeOn = config.ColorGradeEnabled;
            float temperature = config.ColorGradeTemperature;
            float saturation = config.ColorGradeSaturation;
            if (gradeOn && config.ColorGradeAuto)
            {
                ComputeAuto(out float autoTemperature, out float autoSaturationMultiplier);
                temperature += autoTemperature;
                saturation *= autoSaturationMultiplier;
            }

            // _meteredExposure is metered and eased in UpdateAutoExposure while auto is on, and
            // eased back to 1 by the frames that skip it, so bright scenes dim smoothly with no
            // pop and switching the meter off returns the picture to the dial alone.
            GetParam(effect, "Strength")?.SetValue(gradeOn ? MathHelper.Clamp(config.ColorGradeStrength, 0f, 1f) : 1f);
            GetParam(effect, "Contrast")?.SetValue(gradeOn ? config.ColorGradeContrast : 1f);
            GetParam(effect, "Saturation")?.SetValue(gradeOn ? saturation : 1f);
            GetParam(effect, "Temperature")?.SetValue(gradeOn ? MathHelper.Clamp(temperature, -1f, 1f) : 0f);
            GetParam(effect, "Brightness")?.SetValue(gradeOn ? config.ColorGradeBrightness * _meteredExposure : 1f);
            // _toneMapEase advances once per frame in Apply (shared with the fused tail).
            GetParam(effect, "ToneMap")?.SetValue(_toneMapEase);
            GetParam(effect, "BlueLight")?.SetValue(MathHelper.Clamp(config.BlueLightFilter, 0f, 1f));
            GetParam(effect, "ScreenPixels")?.SetValue(new Vector2(Game1.viewport.Width, Game1.viewport.Height));
            GetParam(effect, "LutAmount")?.SetValue(BindLut(effect, config));
            effect.CurrentTechnique = effect.Techniques["ColorGrade"];
            DrawFull(spriteBatch, source, destination, effect);
        }

        /// <summary>Fused grade + vignette tail pass (see tail.fx): the ColorGrade and
        /// Finishing stages in ONE full-screen draw. Selected in Apply only when both are
        /// wanted, CA is dormant and tilt-shift is out of the chain, so the parameter set is
        /// exactly the union of the two stage bodies (grade always on here) minus CA.</summary>
        private void RenderTail(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _tail!;
            bool gradeOn = config.ColorGradeEnabled;
            float temperature = config.ColorGradeTemperature;
            float saturation = config.ColorGradeSaturation;
            if (gradeOn && config.ColorGradeAuto)
            {
                ComputeAuto(out float autoTemperature, out float autoSaturationMultiplier);
                temperature += autoTemperature;
                saturation *= autoSaturationMultiplier;
            }
            // The fused pass does the finishing stage's work, so it carries the sky tint too;
            // without it the world stops taking the aurora's colour whenever the pipeline
            // decides it can fuse.
            GetParam(effect, "SkyLightTint")?.SetValue(SkyLightTintNow(config));
            GetParam(effect, "GradeOn")?.SetValue(1f);
            GetParam(effect, "Strength")?.SetValue(gradeOn ? MathHelper.Clamp(config.ColorGradeStrength, 0f, 1f) : 1f);
            GetParam(effect, "Contrast")?.SetValue(gradeOn ? config.ColorGradeContrast : 1f);
            GetParam(effect, "Saturation")?.SetValue(gradeOn ? saturation : 1f);
            GetParam(effect, "Temperature")?.SetValue(gradeOn ? MathHelper.Clamp(temperature, -1f, 1f) : 0f);
            GetParam(effect, "Brightness")?.SetValue(gradeOn ? config.ColorGradeBrightness * _meteredExposure : 1f);
            GetParam(effect, "ToneMap")?.SetValue(_toneMapEase);
            GetParam(effect, "BlueLight")?.SetValue(MathHelper.Clamp(config.BlueLightFilter, 0f, 1f));
            GetParam(effect, "VignetteStrength")?.SetValue(config.VignetteStrength * _vignetteEase);
            GetParam(effect, "NightAmt")?.SetValue(NightFactorNow() * _vignetteEase);
            GetParam(effect, "ScreenPixels")?.SetValue(new Vector2(Game1.viewport.Width, Game1.viewport.Height));
            GetParam(effect, "LutAmount")?.SetValue(BindLut(effect, config));
            effect.CurrentTechnique = effect.Techniques["Tail"];
            DrawFull(spriteBatch, source, destination, effect);
        }

        // Eased twins of raw on/off drivers (house rule: nothing visible changes in one
        // frame). Structural readiness gates (SpriteMaskOn/ReflectedEntitiesOn/SceneOn) stay
        // binary on purpose - there is no texture to fade until the bake exists - and
        // indoor/outdoor multipliers snap behind the game's own warp fade.
        // _displacementGateEase and _heatHazeEase follow the screen's own scene and live in
        // ScreenState (RenderPipeline.Screens.cs); the tilt mode follows a setting and stays here.
        private float _tiltModeEase;
        // 1 while the camera is in a room, 0 outdoors. Unlike _tiltModeEase,
        // which follows a setting every screen shares, this one follows the location, so in split
        // screen one player can be inside while the other is not: it is saved per screen.
        // The field this describes lives in ScreenState now; see RenderPipeline.Screens.cs.
        private float _auroraAmount;
        /// <summary>The aurora's strength for the glass, which is drawn into the world batch
        /// long before any of our passes and so cannot ask for it itself. Refreshed every
        /// frame by the finishing pass, which always runs.</summary>
        internal static float SkyAuroraGlass;
        /// <summary><c>radiance_aurora</c>: 0 leaves the nightly roll in charge, 1 forces
        /// tonight's display on, -1 forces it off. Testing a feature that only happens on
        /// 62% of clear winter nights is otherwise a lottery, and a tester who draws three
        /// blanks in a row reports it as broken - which is exactly what happened.</summary>
        internal static int AuroraForce;
        internal static float SkyAuroraShow;
        internal static bool SkyAuroraTonight;
        internal static float SkyAuroraShowStart, SkyAuroraShowEnd;
        private static int _auroraShowDay = -1;
        private static bool _auroraShowTonight;
        private static float _auroraShowStart, _auroraShowLength;
        // The shooting star: one streak at a time, timed on Determinism.Seconds so a frozen
        // capture holds it still (or never starts one). Position and direction are in world
        // tiles, matching the water shader's world-anchored sky.
        private double _meteorNextAt = -1;
        /// <summary>How many more streaks are owed in the burst being served. Real meteors do not
        /// arrive on a metronome; one every half minute reads as a scripted event, so a spawn
        /// sometimes brings one or two more within the second.</summary>
        private int _meteorBurstLeft;
        private const int MeteorSlots = 3;
        /// <summary>One streak in flight. Three of them, because a sky that can only hold one at
        /// a time never looks like a sky, and because a faint quick one crossing while a slow
        /// bright one is still burning is most of what makes the two read as different things.
        /// </summary>
        private struct ShootingStar
        {
            public bool Active;
            public double StartedAt;
            public float Seconds;
            public Vector2 StartTile, Direction;
            public float TailTiles, TravelTiles, Width, Brightness;
        }
        private readonly ShootingStar[] _meteors = new ShootingStar[MeteorSlots];
        private readonly Vector4[] _meteorPaths = new Vector4[MeteorSlots];
        private readonly Vector4[] _meteorShapes = new Vector4[MeteorSlots];
        private readonly Random _meteorRandom = new(0x5EED);
        /// <summary>Set by <c>radiance_star</c>: how many streaks to bring forward to this frame,
        /// if the night is clear enough for one at all. Requests are dropped when the gate is
        /// closed, so a request made through a closed gate is answered by the report rather than
        /// by a streak that appears an hour later.</summary>
        internal int MeteorRequests;
        // What the sky gate actually uploaded this frame, for radiance_report. "I turned it on
        // and saw nothing" is answered by these numbers naming the factor that killed it,
        // which is what the caustic line already does one block further up.
        internal float SkyAuroraUploaded;
        internal float SkyMeteorEnvelope;
        internal int SkyMeteorBurning;
        internal double SkyMeteorSecondsToNext = -1;
        internal bool SkyClearNight;

        private bool _isFloodOcclusionReady;
        private const int FloodShadowedLights = 8;
        /// <summary>Must equal SOFT_LIGHTS in shaders/floodlight.fx (recompile it by hand).</summary>
        private const int FloodSoftLights = 40;
        /// <summary>What a direct pool is worth when the flood map is carrying the indirect half
        /// of the same light at FULL strength. At no flood at all it is worth one: the pool is
        /// then the only thing lighting the room and has to carry all of it.</summary>
        private const float FloodDirectShare = 0.55f;
        /// <summary>The least a hearth's circle on the floor is worth in a WINDOWED room, however
        /// much daylight has filled it. Zero everywhere else.</summary>
        private const float HearthLitRoomFloor = 0.35f;

        // xy = screen UV, z = 1 when this light is an actual flame. The z was free: a float2 array
        // still costs a whole register per element, so the flag rides in for nothing.
        private readonly Vector4[] _floodLightPositions = new Vector4[FloodShadowedLights];
        private readonly Vector4[] _floodLightColors = new Vector4[FloodShadowedLights];
        /// <summary>The light id in each shadowed slot, and how many slots are live, so the
        /// march window can tell a lamp that moved from a slot that changed hands.</summary>
        private readonly int[] _floodLightIds = new int[FloodShadowedLights];
        private int _floodDirectCount;
        private readonly Vector4[] _floodSoftPositions = new Vector4[FloodSoftLights];
        private readonly Vector4[] _floodSoftColors = new Vector4[FloodSoftLights];
        private readonly Vector2[] _classicLightPositions = new Vector2[ClassicLightSlots];
        private readonly Vector4[] _classicLightData = new Vector4[ClassicLightSlots];

        // Windowed-interior exposure + window shafts. Eased so a 10-minute clock tick or a
        // weather flip never steps the room in one frame; SNAPPED on location change (house
        // rule: indoor/outdoor multipliers hide behind the game's own warp fade).
        private Vector3 _windowColourEase = Vector3.Zero;
        // How much the glass is allowed to ignore the room's exposure. It is a hole with
        // the sky behind it only while there IS sky light: after dark it is a dark rectangle, and
        // exempting it then left a window still lit at midnight.
        // The field this describes lives in ScreenState now; see RenderPipeline.Screens.cs.
        // Eased twin of the window-beam setting, so switching it off fades the
        // floor patch out instead of deleting it in one frame.
        // The field this describes lives in ScreenState now; see RenderPipeline.Screens.cs.
        // Eased twin of the window room-light setting: the daylight a window contributes
        // to the room's own lighting, which is the half a window-art mod cannot replace.
        // The field this describes lives in ScreenState now; see RenderPipeline.Screens.cs.
        // Per screen, like the eases it snaps: RenderPipeline.Screens.cs.
        private ref GameLocation? _exposureLocation => ref _screen.ExposureLocation;
        /// <summary>How many times each screen's exposure eases snapped, for radiance_screenwatch.
        /// Once per room walked into is right; once per frame is what a shared location check
        /// did to a split screen, and nothing on the screen says which of the two is happening.</summary>
        private readonly int[] _exposureSnapsByScreen = new int[4];
        private readonly Vector2[] _windowShaftPositions = new Vector2[6];

        // What the indoor pass last handed the shader. "The window beam is gone" has three
        // completely different causes that look identical on screen - the room is not classed as
        // windowed, the game published no WindowLight to stand a beam under, or the beam is being
        // drawn but is washed out by everything else - and no screenshot can tell them apart.
        // Recorded rather than recomputed so the report reads the frame that was actually drawn.
        /// <summary>radiance_windowblock: how much of a window beam a wall or a piece of
        /// furniture standing between the pane and the pixel takes off it.
        /// <para>SHIPPED AT ZERO, and that is a decision rather than a default nobody got to.
        /// Blocking the beam is the physically truer answer and it was built, measured and
        /// looked at on 8 September: it costs nothing to run and it changes 2.4% of the frame
        /// at Pierre's. The author looked at both and chose the beam that crosses everything,
        /// because a soft wash of daylight reads as sunlight in a room and a blocked one reads
        /// as patchy. The beam was drawn as a painted stripe on purpose; this is the same
        /// judgement applied again. Kept because it costs nothing to keep and someone may want
        /// the other look: any value between is a partial block.</para></summary>
        internal static float WindowShaftBlocking;

        /// <summary>radiance_contactdepth: how dark the ground goes where something stands
        /// on it, read from the order buffer so it follows the thing's own outline rather
        /// than the ellipse ContactShadowStrength draws from the art's width.</summary>
        internal static float ContactFromDepthStrength;
        /// <summary>How far up the contact shade looks, in texels of the order buffer, which
        /// is half the frame by default. Four texels there is eight screen pixels.</summary>
        internal static float ContactFromDepthReachTexels = 4f;

        private bool _reportedWindowsHere, _reportedWindowBeamOn;
        private bool _reportedInteriorWindowed;                 // layout truth, before the effects master switch
        private float _reportedWindowRoomScale = 1f;            // what the flood actually used for window room light
        private readonly List<Vector2> _reportedWindowGlowPositions = new();   // where the room's glow sprites are
        private int _reportedWindowCount, _reportedWindowLightsSeen, _reportedWindowLightsDark;
        private int _reportedWindowGlows = -1;   // lightGlows count in this location (0 or more)
        private Vector3 _reportedWindowColour, _reportedExposure = Vector3.One;
        private float _reportedRoomSaturation = 1f, _reportedGiStrength;
        /// <summary>Sun shaft term as last handed to the shader, for the report: "no shafts" has
        /// four gates (both switches, outdoors, sun up) and a strength of zero does not say which.</summary>
        internal float _reportedShaftStrength;
        internal float _reportedLampShaftStrength;
        internal Vector2 _reportedShaftDirection;
        // _shaftStrengthEase, _shaftDirectionEase and _shaftColourEase live in ScreenState.
        private float _reportedPaneDaylight, _reportedWindowLean, _reportedWindowReach;
        private float _reportedHearthFloor, _reportedDirectScale;

        /// <summary>
        /// The indoor half of the flood pass, as it was last handed to the shader.
        ///
        /// <para>Written for one question that keeps coming back in different words: "the light
        /// from the window is gone". It has three unrelated causes that produce the same empty
        /// floor, and the only way to tell them apart is to see the numbers - the room is not
        /// classed as a windowed interior at all, the game published no WindowLight for a beam to
        /// hang under, or the beam is being drawn at full strength and something else in the frame
        /// is sitting on top of it. The same block also carries the two knobs that lift a room's
        /// floor, because "too bright/too flat indoors" is answered from exactly here.</para>
        /// </summary>
        private string DescribeIndoorLight()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"flood presence {_fadeFlood:F2} (0 = the numbers below were not applied this frame)");
            report.AppendLine($"    windowed interior: {_reportedWindowsHere}, beam setting on: {_reportedWindowBeamOn}");
            report.AppendLine($"    interior windowed (layout): {_reportedInteriorWindowed}, room-light scale: {_reportedWindowRoomScale:F2}");
            report.Append("    window glows at tile: ");
            if (_reportedWindowGlowPositions.Count == 0)
                report.AppendLine("none");
            else
            {
                for (int i = 0; i < _reportedWindowGlowPositions.Count; i++)
                {
                    Vector2 glow = _reportedWindowGlowPositions[i];
                    report.Append(i > 0 ? ", " : "").Append($"({glow.X / 64f:F0},{glow.Y / 64f:F0})");
                }
                report.AppendLine();
            }
            report.AppendLine($"    window lights: {_reportedWindowLightsSeen} published by the game, "
                        + $"{_reportedWindowLightsDark} not glowing, {_reportedWindowCount} used as beams (max 6)");
            report.AppendLine($"    window glow sprites in this room: {_reportedWindowGlows}");
            if (_reportedWindowsHere && _reportedWindowBeamOn && _reportedWindowCount == 0)
                report.AppendLine("    -> NO BEAM IS POSSIBLE: the room is windowed but nothing in it is emitting "
                            + "window light, so there is nowhere to stand a beam. Two things look like this. A "
                            + "window-art mod that draws glass without a light source is one. The other is not a "
                            + "bug at all: the game only refreshes its window glows when a room is ENTERED, so "
                            + "moving the clock past dawn while already standing inside leaves them at their "
                            + "night state until you walk out and back in.");
            report.AppendLine($"    daylight through the glass: colour ({_reportedWindowColour.X:F2},{_reportedWindowColour.Y:F2},"
                        + $"{_reportedWindowColour.Z:F2}) pane {_reportedPaneDaylight:F2}");
            report.AppendLine($"    beam shape: lean {_reportedWindowLean:F2} tiles sideways per tile down, reach {_reportedWindowReach:F1} tiles");
            // Luminance, the same way the exposure was built and the same way the shader's own
            // give-back reads it. An arithmetic mean answered 0% dimmed for a room measurably
            // dimmed by a fifth, because a cool cast puts blue above 1 and the mean hides the
            // whole thing - a diagnostic that agreed with the bug rather than reporting it.
            float dim = Math.Clamp(1f - (0.299f * _reportedExposure.X + 0.587f * _reportedExposure.Y
                                         + 0.114f * _reportedExposure.Z), 0f, 1f);
            report.AppendLine($"    room exposure ({_reportedExposure.X:F2},{_reportedExposure.Y:F2},{_reportedExposure.Z:F2}) "
                        + $"-> we dimmed this room by {dim:P0}");
            // The two terms behind "the colours look wrong indoors". The exposure above is a
            // COLOUR: its three channels apart is the hour's cast, cool in the morning and warm
            // before dark, and a wide spread on warm wood is what reads as the room losing its
            // own colour. Saturation is the lift that is supposed to answer that, and the GI
            // strength is the separate soft glow laid over the room. Each has its own switch, so
            // naming which one is doing it takes one number rather than one argument.
            float spread = Math.Max(Math.Max(_reportedExposure.X, _reportedExposure.Y), _reportedExposure.Z)
                         - Math.Min(Math.Min(_reportedExposure.X, _reportedExposure.Y), _reportedExposure.Z);
            report.AppendLine($"    hour cast: channels spread {spread:F2} "
                        + $"({(_reportedExposure.Z > _reportedExposure.X ? "cool" : "warm")}), saturation lift {_reportedRoomSaturation:F2}, GI strength {_reportedGiStrength:F2}");
            report.AppendLine($"    light pools give back {1.15f * Math.Max(dim, _reportedHearthFloor):F2}x "
                        + $"(floor {_reportedHearthFloor:F2}), direct pools scaled {_reportedDirectScale:F2}");
            return report.ToString().TrimEnd();
        }

        private void RenderFloodLight(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _floodEffect!;
            float floodCarry = SetFloodMapParams(effect, config);
            SetNightVisionParams(effect, config);
            SetSunShaftParams(effect, config);
            SetCloudCoupling(effect);
            float directScale = SetLightArrays(effect, config, destination, floodCarry);
            SetRoomAndWindowParams(effect, config, directScale);
            SetReliefParams(effect, config);

            // The lamps' shadow rays at half resolution, four lamps to a target, read back by the
            // flood pass below (MarchFromTexture in the shader). Only when a ray could show:
            // the CPU's mirror of the shader's own test says when none can.
            bool marchHalf = !config.LightShadowSharpEdges && !LastMarchSkipped && LastMarchingLamps > 0
                && _isFloodOcclusionReady && _halfResolutionScratchA != null && _halfResolutionScratchB != null;
            if (marchHalf)
            {
                // One technique, the base of the four lamps handed over as a uniform: see
                // MarchBase in floodlight.fx for the constant-packing bug two techniques had.
                // Which road, and which channels, is the march window's decision.
                RunLampMarch(spriteBatch, source, effect, config);
            }
            GetParam(effect, "MarchFromTexture")?.SetValue(marchHalf ? 1f : 0f);
            LastMarchHalfResolution = marchHalf;

            effect.CurrentTechnique = effect.Techniques["FloodLight"];
            DrawFull(spriteBatch, source, destination, effect);
            // After the multiply, in the target it just wrote: a spark is what makes light, so
            // being darkened by the lightmap is exactly backwards for it.
            DrawEmissiveParticlesOnLighting(spriteBatch, destination, EmissiveParticleHost.Flood);
        }

        /// <summary>The sprite relief terms (see RenderNormalPass): the normal buffer, the lamps' lean and the
        /// sun's, all zero unless a buffer was drawn this frame, so the shader's terms vanish exactly.</summary>
        private void SetReliefParams(Effect effect, ModConfig config)
        {
            bool reliefOn = _normalPassReady && _normalRenderTarget != null && _reliefEase > FadeGone;
            float lampRelief = reliefOn ? MathHelper.Clamp(config.SpriteReliefStrength, 0f, 1f) * _reliefEase * _fadeFlood : 0f;
            GetParam(effect, "NormalTexture")?.SetValue(reliefOn ? _normalRenderTarget : null);
            GetParam(effect, "ReliefStrength")?.SetValue(lampRelief);
            // A lamp hangs about a third of a screen above the ground it lights: lower and every
            // sprite beside a lamp is lit only on its very edge, higher and the lean vanishes.
            GetParam(effect, "ReliefLampHeight")?.SetValue(0.35f);
            float sunRelief = 0f;
            Vector3 sunDirection = new(0f, 0f, 1f);
            bool outdoors = Game1.currentLocation?.IsOutdoors ?? false;
            if (reliefOn && outdoors && ShadowRenderer.SunInSky(out Vector2 lightTravel, out float _))
            {
                ShadowRenderer.WindowDaylight(out Vector3 _, out float sunStrength);
                // A sprite is lit from where the sun IS, which is the other end of the way its
                // light travels. The 1.6 lifts it off the ground so the lean reads as a lean and
                // not as a light lying flat against the sprite.
                sunDirection = Vector3.Normalize(new Vector3(-lightTravel.X, -lightTravel.Y, 1.6f));
                sunRelief = MathHelper.Clamp(config.SpriteReliefSun, 0f, 1f) * sunStrength * _reliefEase * _fadeFlood;
            }
            GetParam(effect, "ReliefSunStrength")?.SetValue(sunRelief);
            GetParam(effect, "ReliefSunDirection")?.SetValue(sunDirection);
            // The rim and the shimmer ride the same buffer and the same fades as the lean, so
            // they appear and leave with it rather than hanging on after the relief has gone.
            GetParam(effect, "RimStrength")?.SetValue(
                reliefOn ? MathHelper.Clamp(config.SpriteReliefRim, 0f, 1f) * _reliefEase * _fadeFlood : 0f);
            GetParam(effect, "LeafShimmer")?.SetValue(
                reliefOn ? MathHelper.Clamp(config.SpriteReliefLeafShimmer, 0f, 1f) * _reliefEase * _fadeFlood : 0f);
            GetParam(effect, "ShimmerClock")?.SetValue((float)(Determinism.Seconds % 6283.185));
            // Snow glitter: winter, under the sky, in daylight, not while it snows or rains (no sun
            // then). Eased so a doorway or a cloud front does not switch it on one frame; the
            // shader's branch closes at exactly 0, which is where three seasons sit.
            GameLocation? here = Game1.currentLocation;
            bool snowDay = here != null && here.IsOutdoors && LocalSky.Season == Season.Winter
                && !here.IsSnowingHere() && !here.IsRainingHere();
            float snowGlintTarget = snowDay ? MathHelper.Clamp(config.SnowGlintStrength, 0f, 1f) * (1f - NightFactorNow()) : 0f;
            Approach(ref _snowGlintEase, snowGlintTarget, 0.03f);
            if (_snowGlintEase < 0.003f) _snowGlintEase = 0f;
            GetParam(effect, "SnowGlint")?.SetValue(_snowGlintEase);
        }

        /// <summary>The lightmap itself: which texture, where it sits in the world, and how much of it carries.
        /// Returns that carry - how much the flood carries is exactly what the direct pools discount by.</summary>
        private float SetFloodMapParams(Effect effect, ModConfig config)
        {
            GetParam(effect, "LightMapTexture")?.SetValue(_flood.Texture);
            GetParam(effect, "TilesPerScreen")?.SetValue(new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f));
            GetParam(effect, "WorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            GetParam(effect, "MapOrigin")?.SetValue(_flood.Origin);
            GetParam(effect, "MapSize")?.SetValue(_flood.MapSize);
            // The cascades' map and the cross-fade toward it (see BuildStageList). Once the blend
            // has settled at 1 the flood map stops being rebuilt, so the cascades' texture is handed
            // to BOTH samplers: the lerp is exact at either end and never shows a stale grid.
            bool cascadesShowing = _cascadesReady && _cascades.Texture != null;
            if (cascadesShowing && _cascadeBlend >= 0.999f)
            {
                GetParam(effect, "LightMapTexture")?.SetValue(_cascades.Texture);
                GetParam(effect, "MapOrigin")?.SetValue(_cascades.Origin);
                GetParam(effect, "MapSize")?.SetValue(_cascades.MapSize);
            }
            GetParam(effect, "LightMap2Texture")?.SetValue(cascadesShowing ? _cascades.Texture : _flood.Texture);
            GetParam(effect, "Map2Origin")?.SetValue(cascadesShowing ? _cascades.Origin : _flood.Origin);
            GetParam(effect, "Map2Size")?.SetValue(cascadesShowing ? _cascades.MapSize : _flood.MapSize);
            GetParam(effect, "LightMapBlend")?.SetValue(cascadesShowing ? _cascadeBlend : 0f);
            float floodCarry = MathHelper.Clamp(config.FloodLightingStrength, 0f, 1f) * _fadeFlood;
            GetParam(effect, "Strength")?.SetValue(floodCarry);
            GetParam(effect, "AmbientFloor")?.SetValue(0.10f);
            return floodCarry;
        }

        /// <summary>Purkinje desaturation and the lift half of the night slider. Outdoors only, both on the same
        /// one-hour ramp so there is no frame anyone can point at where they switched on.</summary>
        private void SetNightVisionParams(Effect effect, ModConfig config)
        {
            // Purkinje night desaturation, outdoors only. Scaled by the same night ramp as the
            // ground dim so the two arrive together, and by the night-darkness slider relative to
            // its default so one setting owns the whole character of the night: slid to zero the
            // night keeps every colour, slid deep it goes properly rod-vision. The ramp is an
            // hour of game time, so there is no frame anyone can point at where it switched on.
            bool purkinjeOutdoors = Game1.currentLocation?.IsOutdoors ?? false;
            float purkinje = purkinjeOutdoors
                ? Math.Min(0.45f, 0.35f * FloodLightmap.NightAmount() * (config.LightingNightDarkness / 0.56f))
                : 0f;
            GetParam(effect, "NightDesaturation")?.SetValue(purkinje * _fadeFlood);
            // The brighten half of the night slider (see the shader's NightLift note). Below ~0.32
            // the night is LIFTED above vanilla, cool and readable; at the default and above this
            // is exactly zero and the dim side of the slider rules alone. Same one-hour ramp.
            float nightLift = purkinjeOutdoors
                ? FloodLightmap.NightAmount() * Math.Max(0f, 0.32f - config.LightingNightDarkness) * 1.4f
                : 0f;
            GetParam(effect, "NightLift")?.SetValue(nightLift * _fadeFlood);
        }

        /// <summary>The occluder-marched sun shafts, and the eases that stop every gate on them from popping.</summary>
        private void SetSunShaftParams(Effect effect, ModConfig config)
        {
            // Same read as the night-vision block: a property on the current location,
            // so both blocks ask the game rather than one threading it into the other.
            bool purkinjeOutdoors = Game1.currentLocation?.IsOutdoors ?? false;
            // Sun shafts: the occluder-marched god rays (see the shader's param block for why the
            // bright-pass version could never work top-down). Both switches, under the sky, sun up.
            // Under the sky includes a glass roof: the greenhouse is an interior to the game, but
            // the sun stands over it the way it stands over the farm, and the dappled light on
            // its floor was asked for by name (MyLadySeven, Nexus, 2026-09-04).
            // The glass roof is a place's own fact; whether we act on it is the player's, so the
            // switch is asked here rather than inside UnderAGlassRoof. Off leaves the greenhouse
            // the plain interior it was before 1.7.6.
            bool underTheSun = purkinjeOutdoors
                || (config.GodRaysSunGlassRoof && ShadowRenderer.UnderAGlassRoof(Game1.currentLocation));
            float shaftTarget = 0f;
            Vector2 shaftDirection = _shaftDirectionEase;
            Vector3 shaftColour = _shaftColourEase;
            // The sun switch stands on its own. It lived under the lamp-ray master for a day, and
            // that read as one switch too many: the two effects share nothing but a word - lamp
            // rays are a bright-pass streak, sun shafts are an occluder march - so tying the sun
            // to the lamp toggle only meant two clicks to get one effect.
            if (config.GodRaysSun && underTheSun
                && ShadowRenderer.SunInSky(out Vector2 shaftTravel, out float _,
                    glassRoofCounts: config.GodRaysSunGlassRoof))
            {
                ShadowRenderer.WindowDaylight(out Vector3 sunColour, out float sunStrength);
                shaftDirection = shaftTravel;
                shaftColour = sunColour;
                // The sun's OWN intensity, not the lamp rays'. They shared one for a while and
                // that meant turning the lamps down at night also thinned the morning through the
                // trees, which is two different pictures behind one slider.
                shaftTarget = 0.45f * sunStrength * MathHelper.Clamp(config.GodRaysSunIntensity, 0f, 1.5f) * _fadeFlood;
            }
            // Every gate on the shafts is a hard flip - rain starting, the toggle, a warp - and a
            // hard flip on a whole-screen effect is a pop. Ease over about a second, both ways,
            // per the house rule that every effect fades in both directions. Direction and colour
            // ease with it so a shaft mid-fade cannot snap to a new sun.
            Approach(ref _shaftStrengthEase, shaftTarget, ShaftEaseRate);
            if (Math.Abs(shaftTarget - _shaftStrengthEase) < 0.002f) _shaftStrengthEase = shaftTarget;
            Approach(ref _shaftDirectionEase, shaftDirection, ShaftEaseRate);
            if (_shaftDirectionEase.LengthSquared() > 0.001f) _shaftDirectionEase = Vector2.Normalize(_shaftDirectionEase);
            else _shaftDirectionEase = shaftDirection;
            _shaftDirectionEase = Determinism.Settle(_shaftDirectionEase, shaftDirection);
            Approach(ref _shaftColourEase, shaftColour, ShaftEaseRate);
            float shaftStrength = _shaftStrengthEase;
            // (see ShaftEaseRate for why these three step by the clock rather than by the frame)
            shaftDirection = _shaftDirectionEase;
            shaftColour = _shaftColourEase;
            _reportedShaftStrength = shaftStrength;
            _reportedShaftDirection = shaftDirection;
            GetParam(effect, "SunShaftDirection")?.SetValue(shaftDirection);
            GetParam(effect, "SunShaftColour")?.SetValue(shaftColour);
            GetParam(effect, "SunShaftStrength")?.SetValue(shaftStrength);
            GetParam(effect, "SunShaftDrift")?.SetValue((float)(Determinism.Seconds * 0.35 % 6283.185) );
            // How far the dapple stretches from its canopy, on the sun's own dial. Normalised
            // so the DEFAULT (0.6) is exactly the tuned look - binding the raw slider would have
            // silently shortened every shaft by 40% at defaults - and capped at 1.1 because the
            // occluder mask is padded 8 tiles (FloodOccluderPad): march past the padding and shafts
            // appear as you walk, the exact bug the padding was added to fix.
            GetParam(effect, "SunShaftReach")?.SetValue(MathHelper.Clamp(config.GodRaysSunReach / 0.6f, 0.15f, 1.1f));
            // The fog stage's own eased amount, so a misty morning thickens the shafts in step
            // with the haze it is already drawing, and both fade together when the mist lifts.
            GetParam(effect, "SunShaftHaze")?.SetValue(MathHelper.Clamp(_fogDayAmount, 0f, 1f));
            // The same baked fbm the clouds and fog sample, here for the shaft dust motes.
            GetParam(effect, "NoiseTexture")?.SetValue(NoiseTex());
            // Lamp shafts ride the same pass (see the shader's LampShaftStrength). Their presence
            // is decided with the stage list, eased there by weather and daylight; the dial and
            // the flood's own fade multiply in here so nothing about them can pop.
            float lampShafts = config.GodRaysEnabled
                ? MathHelper.Clamp(config.GodRaysIntensity, 0f, 2f) * _godRayAmount * _fadeFlood
                : 0f;
            _reportedLampShaftStrength = lampShafts;
            GetParam(effect, "LampShaftStrength")?.SetValue(lampShafts);
        }

        /// <summary>Couple the shafts to last frame's cloud mask, refusing it when it is stale or from elsewhere.</summary>
        private void SetCloudCoupling(Effect effect)
        {
            // Cloud coupling: the cloud stage's kept mask from LAST frame (see _cloudMaskKeep),
            // one frame stale by construction since flood runs first. Refused outright when the
            // mask is old (cloud stage off) or the camera jumped more than half a screen since
            // it was drawn (a warp: the kept mask is a picture of somewhere else). The coupling
            // strength eases like every other gate here, so clouds joining or leaving the frame
            // never step the shafts.
            (float cloudCoupleTarget, Vector2 cloudShift) = CloudCouplingNow();
            Approach(ref _shaftCloudEase, cloudCoupleTarget, 0.05f);
            GetParam(effect, "CloudMaskTexture")?.SetValue(_cloudMaskKeep);
            GetParam(effect, "CloudCouple")?.SetValue(_shaftCloudEase);
            GetParam(effect, "CloudMaskShift")?.SetValue(cloudShift);
        }

        /// <summary>
        /// How much the kept cloud mask may be trusted this frame, and where it sits against the
        /// camera now. Shared by every consumer of the mask (the shafts, the water's glitter), so
        /// they refuse it on the same grounds and read it at the same offset.
        /// </summary>
        private (float couple, Vector2 shift) CloudCouplingNow()
        {
            float cloudCoupleTarget = 0f;
            Vector2 cloudShift = Vector2.Zero;
            if (GpuContent.Usable(_cloudMaskKeep) && Determinism.Ticks - _cloudMaskTick <= 2)
            {
                var tilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
                Vector2 shiftTiles = new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f) - _cloudMaskTileOffset;
                cloudShift = shiftTiles / tilesPerScreen;
                if (Math.Abs(cloudShift.X) < 0.5f && Math.Abs(cloudShift.Y) < 0.5f)
                    // 2.2: the mask's opacity is a SHADE strength (0.35 by default), but a cloud
                    // between the sun and the ground cuts the direct beam much harder than it
                    // dims the ground - a faint moon-cloud still gates faintly, a storm fully.
                    cloudCoupleTarget = MathHelper.Clamp(_cloudMaskStrength * 2.2f, 0f, 1f);
                else
                    cloudShift = Vector2.Zero;
            }
            return (cloudCoupleTarget, cloudShift);
        }

        /// <summary>The two tiers of direct pool, and the occluder mask they are shadowed against. Returns the
        /// discount each pool paid, which the report mirrors.</summary>
        /// <summary>What the shadow march was actually handed this frame, and how many lamps were
        /// marching when it was decided. For the report: a setting whose effect nobody can read is
        /// a setting nobody can judge, and three counters written this week were unreadable for
        /// exactly that reason.</summary>
        internal static float LastMarchStepCeiling;
        internal static int LastMarchingLamps;
        /// <summary>How visible the lamps were held to be this frame (1 = night, 0 = white sky),
        /// and the lamp shadow strength that came out of it, for the report.</summary>
        internal static float LastLampVisible = 1f;
        internal static float LastShadowStrengthNow;
        /// <summary>One step of an 8-bit colour: a lamp shadow weaker than this cannot change a
        /// pixel of the target, so the march that would find it is skipped.</summary>
        private const float MarchSkipBelowStrength = 1f / 255f;
        /// <summary>Whether the shader skipped every lamp's shadow march this frame because no
        /// term that reads its result was live: shadows at zero (daylight outdoors), shafts off,
        /// no debug paint. The lamps still light; only the ray is not walked.</summary>
        internal static bool LastMarchSkipped;
        /// <summary>Whether the last flood pass read its rays from the half-resolution targets.
        /// The setting behind it is <see cref="ModConfig.LightShadowSharpEdges"/>, inverted: sharp
        /// edges means every ray is walked from every pixel. This is the report's mirror of what
        /// the frame actually did, which is not the same question when the march is skipped
        /// entirely or the occluder grid is not ready yet.</summary>
        internal static bool LastMarchHalfResolution;

        private float SetLightArrays(Effect effect, ModConfig config, RenderTarget2D destination, float floodCarry)
        {
            // Direct pools: the ranked leaders get the shadow ray, everything behind them
            // still gets its pool.
            // The two tiers together cover the WHOLE ranked list, so no light the ranking
            // kept can fall off the end unseen - which is what made pools blink in and out
            // of a shop full of windows while the entry ramp thought nothing had changed.
            //
            // A pool is dimmed here because the flood map is carrying the indirect half of the
            // same light, and counting it twice would blow the room out. That was a FIXED 0.55,
            // and fixed is wrong: how much the flood carries is exactly what the GI slider sets.
            // Turned down, the flood stops carrying, and the direct pool went on paying a
            // discount for help it was no longer getting. That is the arithmetic behind "night
            // is dark but the lit places are dark too": the darkness sliders were at their
            // defaults while the only thing meant to push back had been quietly cut to a bit
            // over half, with no setting anywhere to undo it. The discount now tracks the help,
            // so a room lit by lamps rather than by bounce gets its lamps at full strength.
            float directScale = MathHelper.Lerp(1f, FloodDirectShare, floodCarry);
            // Which lights are SHADOWED is eased rather than cut at rank eight: see
            // RenderPipeline.FloodShadowFade for why a hard boundary here read as a flicker while
            // walking. The tier is chosen by id, so the same lamp keeps its shadow across frames
            // even when the ranking shuffles around it.
            // Two orders of the same lights: the array's slot order, which the upload below has
            // to follow because the positions and colours sit at those indices, and RANK order,
            // which is what decides who deserves a shadow ray. The tier used to take the first
            // eight of the slot order, and slots are kept stable for a light's whole stay, so
            // "first eight" meant "the eight that arrived earliest": a glow ring taken off and put
            // on again came back as a new light in a late slot behind twenty street lamps, some
            // of them off screen, and never cast a shadow again until the map changed.
            _floodLiveIds.Clear();
            _floodRankedSlots.Clear();
            for (int i = 0; i < _lightCount && i < _lightWrite.Count; i++)
            {
                _floodLiveIds.Add(_lightWrite[i].Id);
                _floodRankedSlots.Add(i);
            }
            _floodByRankThenId ??= (first, second) =>
            {
                int byRank = _lightWrite[second].Rank.CompareTo(_lightWrite[first].Rank);
                return byRank != 0 ? byRank : _lightWrite[first].Id.CompareTo(_lightWrite[second].Id);
            };
            _floodRankedSlots.Sort(_floodByRankThenId);
            _floodRankedIds.Clear();
            foreach (int slot in _floodRankedSlots)
                _floodRankedIds.Add(_lightWrite[slot].Id);
            List<int> shadowed = AdvanceFloodShadowTier(_floodLiveIds, _floodRankedIds);

            int shadowedCount = 0;
            for (int i = 0; i < _lightCount && i < _floodLiveIds.Count && shadowedCount < FloodShadowedLights; i++)
            {
                int slot = shadowed.IndexOf(_floodLiveIds[i]);
                if (slot < 0)
                    continue;
                _floodLightPositions[shadowedCount] = new Vector4(_lightPositions[i].X, _lightPositions[i].Y,
                    _lightIsFire[i], FloodShadowWeight(_floodLiveIds[i]));
                _floodLightIds[shadowedCount] = _floodLiveIds[i];
                var lightData = _lightShaderData[i];
                _floodLightColors[shadowedCount] = new Vector4(lightData.X * directScale, lightData.Y * directScale, lightData.Z * directScale, lightData.W);
                shadowedCount++;
            }
            for (int i = shadowedCount; i < FloodShadowedLights; i++) { _floodLightPositions[i] = Vector4.Zero; _floodLightColors[i] = Vector4.Zero; }
            int softCount = 0;
            for (int i = 0; i < _lightCount && i < _floodLiveIds.Count && softCount < FloodSoftLights; i++)
            {
                // Everything the shadowed tier did not take. A light waiting for a shadowed slot
                // shows here meanwhile, which is what makes its arrival invisible: it is already
                // drawn, and all that changes is that a shadow grows into it.
                if (shadowed.Contains(_floodLiveIds[i]))
                    continue;
                _floodSoftPositions[softCount] = new Vector4(_lightPositions[i].X, _lightPositions[i].Y, _lightIsFire[i], 0f);
                var lightData = _lightShaderData[i];
                _floodSoftColors[softCount] = new Vector4(lightData.X * directScale, lightData.Y * directScale, lightData.Z * directScale, lightData.W);
                softCount++;
            }
            for (int i = softCount; i < FloodSoftLights; i++) { _floodSoftPositions[i] = Vector4.Zero; _floodSoftColors[i] = Vector4.Zero; }
            GetParam(effect, "LightPositions")?.SetValue(_floodLightPositions);
            GetParam(effect, "LightColours")?.SetValue(_floodLightColors);
            // The contact shade reads the order buffer, so it only runs on a frame that has one.
            // With no buffer the strength goes to zero and the shader skips the reads entirely.
            RenderTarget2D? orderBuffer = SpriteRankBuffer;
            GetParam(effect, "RankTexture")?.SetValue((Texture2D?)orderBuffer ?? _flatNormalTexture);
            GetParam(effect, "ContactFromDepth")?.SetValue(
                orderBuffer != null ? MathHelper.Clamp(ContactFromDepthStrength, 0f, 1f) : 0f);
            GetParam(effect, "ContactTexel")?.SetValue(
                orderBuffer != null ? 1f / Math.Max(1, orderBuffer.Height) : 0f);
            GetParam(effect, "ContactReach")?.SetValue(ContactFromDepthReachTexels);
            _floodDirectCount = _isFloodOcclusionReady ? shadowedCount : 0;
            GetParam(effect, "DirectCount")?.SetValue((float)_floodDirectCount);
            GetParam(effect, "SoftLightPositions")?.SetValue(_floodSoftPositions);
            GetParam(effect, "SoftLightColours")?.SetValue(_floodSoftColors);
            GetParam(effect, "SoftCount")?.SetValue((float)softCount);
            GetParam(effect, "Aspect")?.SetValue(destination.Width / (float)Math.Max(1, destination.Height));
            // FLOOD's own mask, own origin, own size fields — see the note on _floodOccluderMask
            // for why these must never be the classic path's shared fields. They used to be, and
            // classic's build runs later in the same frame and always overwrote them, so flood's
            // shader was reading classic's smaller, unpadded, un-softened mask back every frame
            // whenever both lighting systems were on (the shipped default, since flood does not
            // disable classic on its own). Found while chasing a different bug (a solid black
            // fireplace, which turned out to be the saturation lerp elsewhere in this shader) and
            // fixed on sight rather than left as a landmine for whoever hits it next: two systems
            // silently overwriting one shared cache is wrong regardless of what it does today.
            GetParam(effect, "OccluderTexture")?.SetValue(_floodOccluderMask);
            // The tile grid alone, for the sun shafts (see OccluderBaseSampler in the shader).
            GetParam(effect, "OccluderBaseTexture")?.SetValue(_floodOccluderBaseTexture);
            GetParam(effect, "OccluderSoft1Texture")?.SetValue(_floodOccluderSoft[0]);
            GetParam(effect, "OccluderSoft2Texture")?.SetValue(_floodOccluderSoft[1]);
            GetParam(effect, "OccluderSoft3Texture")?.SetValue(_floodOccluderSoft[2]);
            GetParam(effect, "OccluderOrigin")?.SetValue(new Vector2(_floodOccluderTileX, _floodOccluderTileY));
            GetParam(effect, "OccluderMapSize")?.SetValue(_floodOccluderMaskSize);
            // A lamp's shadow is only as visible as the lamp's glow. Outdoors by day the game
            // paints no glow for a ring or a torch, yet the carve went on taking its full share
            // out of the scene, so a plant beside the player threw a black wedge at 6:20 in the
            // morning. Both terms follow the game's own dusk ramp outdoors; indoors the game
            // draws its lamps at every hour and so do their shadows.
            //
            // "How visible is a lamp" is read off the tint the game paints the outdoors with, not
            // off the clock: white at noon, dim under rain, dark at night. A rainy morning is the
            // case the clock got wrong, since the sky is grey and the game already draws its lamps
            // against it, so their shadows should show a little too. Night is what the dial was
            // tuned at, so full darkness maps to 1 and a rainy day lands around a third.
            float lampVisible = 1f;
            if (Game1.currentLocation?.IsOutdoors == true)
            {
                Color paintedDaylight = Game1.outdoorLight;
                float luminance = (0.2126f * paintedDaylight.R + 0.7152f * paintedDaylight.G + 0.0722f * paintedDaylight.B) / 255f;
                float darkness = MathHelper.Clamp((1f - luminance) / 0.85f, 0f, 1f);
                lampVisible = Math.Max(darkness, FloodLightmap.NightAmount());
            }
            float shadowStrengthNow = MathHelper.Clamp(config.FloodShadowStrength, 0f, 1f) * lampVisible;
            // Below one colour step nothing the march finds can reach the picture: a shadow's
            // whole effect is at most ShadowStrength of a pool that is itself scaled by the same
            // daylight. The game's daylight tint is rarely pure white even at noon, so a strength
            // of a few thousandths kept every lamp on the farm marching all day for a darkening
            // the target could not hold. Snapped here, on the CPU, so the shader's own "above
            // zero" test and the report's mirror of it keep meaning what they say.
            if (shadowStrengthNow < MarchSkipBelowStrength)
                shadowStrengthNow = 0f;
            LastLampVisible = lampVisible;
            LastShadowStrengthNow = shadowStrengthNow;
            GetParam(effect, "ShadowStrength")?.SetValue(shadowStrengthNow);
            GetParam(effect, "ShadowCarve")?.SetValue(MathHelper.Clamp(config.LightShadowCarve, 0f, 1f) * lampVisible);
            // The shader's own test, mirrored here so the report can say the march was skipped
            // (see marchWanted in floodlight.fx). The shaft strength is the value the last shaft
            // update handed over, which is this frame's or the one before; a report flag, not a
            // gate, so a frame of easing either way is fine.
            LastMarchSkipped = shadowStrengthNow <= 0f && _reportedLampShaftStrength <= 0.004f
                && DebugChannel != DebugOverlayChannel.LampShadow;
            // 0 on the dial is the twelve samples every release up to 1.6.2 took, 1 is the
            // forty-eight that 1.7 traces with. The mapping lives here so the shader is handed
            // a count and never has to know what a dial is.
            float dialCeiling = MathHelper.Lerp(12f, 48f, MathHelper.Clamp(config.LightShadowDetail, 0f, 1f));
            // AND THE DIAL IS A BUDGET, NOT A PER-LAMP ALLOWANCE, WHEN THIS IS ON.
            //
            // What a shadow ray costs is the number of steps it takes, and nothing else: an
            // attempt to stop a ray early once it was fully blocked saved nothing measurable in
            // any of four scenes, because the weight that fades the ends of every ray means it
            // almost never reaches full block at full weight. So the only thing that makes this
            // cheaper is fewer steps.
            //
            // n is how many lamps will actually march at this pixel, which is already counted
            // above for the shader's own light array. The bill is that count multiplied by the
            // ceiling, and it is the count that runs away: the saloon at night measured 0.896 ms
            // against the town's 0.567 with FEWER full-screen passes, because in a small lit room
            // every one of the eight shadowed lamps reaches every pixel and each one marches.
            //
            // Two lamps keep the whole dial. Past that they share it, down to the twelve samples
            // of 1.6.2, which is the floor by construction: this can never look coarser than a
            // release everybody was happy with. And it gives up detail exactly where detail is
            // hardest to see, since a shadow's edge is read against the other seven lamps' light.
            float marchingLamps = Math.Max(1f, _isFloodOcclusionReady ? shadowedCount : 0);
            float shared = config.LightShadowDetailShared
                ? MathHelper.Clamp(dialCeiling * 2f / marchingLamps, 12f, dialCeiling)
                : dialCeiling;
            GetParam(effect, "MarchStepCeiling")?.SetValue(shared);
            LastMarchStepCeiling = shared;
            LastMarchingLamps = (int)marchingLamps;
            GetParam(effect, "ShadowSoftness")?.SetValue(MathHelper.Clamp(config.LightShadowSoftness, 0f, 2f));
            // Rides the flood's own fade, so switching the GI off takes the tint with it rather
            // than leaving a coloured field over a scene with no lightmap left under it.
            GetParam(effect, "ColourBleed")?.SetValue(MathHelper.Clamp(config.FloodColourBleed, 0f, 1f) * _fadeFlood);
            return directScale;
        }

        /// <summary>Time-of-day room exposure and the window shafts of a windowed interior, and the block that
        /// mirrors all of it into the report fields.</summary>
        private void SetRoomAndWindowParams(Effect effect, ModConfig config, float directScale)
        {
            // ---- Time-of-day room exposure + window shafts (windowed interiors only) ----
            var location = Game1.currentLocation;
            FloodLightmap.IndoorLook(location, config, out Vector3 exposureTarget, out float saturationTarget);
            bool interiorWindowed = FloodLightmap.IsWindowedInterior(location);
            // The master "window effects" toggle gates the VISIBLE half (the beam, the lit glass,
            // the patch on the floor) and the outdoor window glow. The daylight a window adds to
            // the room is lighting, not an effect - turning the flashy effect off must not take
            // the room's light with it - so that half reads interiorWindowed directly and never
            // this master switch. It had its own setting once; that was dropped rather than given
            // a job, so the room light in a windowed interior is simply always on.
            bool windowsHere = interiorWindowed && config.WindowEffectsEnabled;
            bool windowedRoom = windowsHere && config.WindowBeamEnabled;
            ShadowRenderer.WindowDaylight(out Vector3 dayColour, out float dayStrength);
            // The player's own dial on the visible daylight: the glow on the pane, the beam and the
            // floor patch move together, the room's lighting does not (that is lighting, not an
            // effect). The dial does NOT touch PaneDaylight, which is only the pane's exemption
            // from the room's exposure: scaling that too meant that at 0 the room's dimming and
            // its sky cast fell on the glass, and a bright white window came out flat grey, the
            // exact "dirty rather than a window" failure the shader was written to avoid. At 0
            // the pane is the game's own art at neutral exposure, and nothing is added to it.
            // Your own house and everybody else's are two dials: a farmhouse that read right beside
            // a villager's home that blew out could only be fixed on one side with one dial.
            // A cabin is a FarmHouse to the game; the island house is its own class.
            bool playersOwnHome = location is StardewValley.Locations.FarmHouse or StardewValley.Locations.IslandFarmHouse;
            float daylightScale = MathHelper.Clamp(playersOwnHome ? config.WindowDaylightStrength : config.WindowDaylightStrengthElsewhere, 0f, 2f);
            Vector3 windowColourTarget = windowedRoom ? dayColour * (dayStrength * 0.8f * daylightScale) : Vector3.Zero;
            float paneDaylightTarget = windowedRoom ? MathHelper.Clamp(dayStrength * 1.6f, 0f, 1f) : 0f;
            if (!ReferenceEquals(location, _exposureLocation))
            {
                _exposureLocation = location;
                if ((uint)_activeScreenId < (uint)_exposureSnapsByScreen.Length)
                    _exposureSnapsByScreen[_activeScreenId]++;
                _exposureEase = exposureTarget;          // snap behind the warp fade
                _windowColourEase = windowColourTarget;
                _roomSaturationEase = saturationTarget;
                _paneDaylightEase = paneDaylightTarget;
                _windowDaylightEase = windowedRoom ? 1f : 0f;
                _windowRoomLightEase = interiorWindowed ? 1f : 0f;
            }
            else
            {
                // Every one of these goes through Settle, or radiance_freeze does not reach it and
                // two captures of the same room differ by the distance each ease happened to have
                // left to run. At 0.03 a frame that distance is never quite zero, which is exactly
                // what the first harness run measured: the flood lightmap differed on 100% of its
                // cells by 2/255 with the game's own frame byte-identical.
                float windowTarget = windowedRoom ? 1f : 0f;
                float roomTarget = interiorWindowed ? 1f : 0f;
                _exposureEase = Determinism.Settle(
                    EasedToward(_exposureEase, exposureTarget, RoomEaseRate), exposureTarget);
                _windowColourEase = Determinism.Settle(
                    EasedToward(_windowColourEase, windowColourTarget, RoomEaseRate), windowColourTarget);
                _roomSaturationEase = Determinism.Settle(
                    EasedToward(_roomSaturationEase, saturationTarget, RoomEaseRate), saturationTarget);
                _paneDaylightEase = Determinism.Settle(
                    EasedToward(_paneDaylightEase, paneDaylightTarget, RoomEaseRate), paneDaylightTarget);
                _windowDaylightEase = Determinism.Settle(
                    EasedToward(_windowDaylightEase, windowTarget, RoomEaseRate), windowTarget);
                _windowRoomLightEase = Determinism.Settle(
                    EasedToward(_windowRoomLightEase, roomTarget, RoomEaseRate), roomTarget);
            }
            // The lightmap seeds both of its window terms on the CPU, a frame ahead of this, so
            // hand it the EASED switches rather than the switches: turning either off has to fade
            // its light away, not delete it between two frames.
            FloodLightmap.WindowPatchScale = _windowDaylightEase;
            FloodLightmap.WindowRoomScale = _windowRoomLightEase;
            // The stage's own fade still applies: while the flood is easing in/out the
            // exposure walks back to neutral with it, so toggling never steps the room.
            GetParam(effect, "Exposure")?.SetValue(Vector3.Lerp(Vector3.One, _exposureEase, _fadeFlood)
                * (1f + LightningEffects.FloodExposureLift * LightningEffects.Burst01));
            GetParam(effect, "RoomSaturation")?.SetValue(MathHelper.Lerp(1f, _roomSaturationEase, _fadeFlood));
            // ...and the switch that says the saturation lift may run at all. See the shader's
            // RoomLookOn note: handing it a neutral 1.0 outdoors was supposed to make the block an
            // identity and measurably did not, so the room look is now switched off outdoors
            // rather than argued into being harmless there.
            GetParam(effect, "RoomLookOn")?.SetValue(windowsHere ? 1f : 0f);
            // The hearth's give-back used to be scaled by our own dimming alone, so it faded to
            // nothing as a room filled with morning light and the fire stopped lighting boards it
            // was plainly lighting. This floor keeps it alive in a lit ROOM and stays zero
            // outdoors and in caves, where a pool at noon was a bug we already fixed once.
            GetParam(effect, "HearthFloor")?.SetValue(windowedRoom ? HearthLitRoomFloor * _fadeFlood : 0f);

            int windowCount = 0;
            _reportedWindowLightsSeen = 0;
            _reportedWindowLightsDark = 0;
            if (windowedRoom && Game1.currentLightSources != null && location != null)
            {
                int viewportWidth = Math.Max(1, Game1.viewport.Width);
                int viewportHeight = Math.Max(1, Game1.viewport.Height);
                foreach (var lightEntry in Game1.currentLightSources)
                {
                    if (windowCount >= 6)
                        break;
                    var light = lightEntry.Value;
                    if (light.lightContext.Value != LightSource.LightContext.WindowLight)
                        continue;
                    _reportedWindowLightsSeen++;
                    if (!ShadowRenderer.WindowGlowing(location, light))
                    {
                        _reportedWindowLightsDark++;
                        continue;
                    }
                    // Beam origin: just under the pane's centre, so the light visibly
                    // CONNECTS to the glass instead of materialising half a tile below it.
                    Vector2 screenPosition = Game1.GlobalToLocal(Game1.viewport, light.position.Value + new Vector2(0f, 12f));
                    float beamU = screenPosition.X / viewportWidth;
                    float beamV = screenPosition.Y / viewportHeight;
                    if (beamU < -0.3f || beamU > 1.3f || beamV < -0.5f || beamV > 1.2f)
                        continue;   // beam could not land on screen
                    _windowShaftPositions[windowCount++] = new Vector2(beamU, beamV);
                }
            }
            for (int i = windowCount; i < 6; i++)
                _windowShaftPositions[i] = Vector2.Zero;
            // Beam geometry is handed over in TILES — the shader works in tile space, where
            // a sideways lean means the same thing on any aspect ratio.
            ShadowRenderer.WindowShaft(out float lean, out float reachTiles);
            GetParam(effect, "WindowPositions")?.SetValue(_windowShaftPositions);
            GetParam(effect, "WindowCount")?.SetValue((float)windowCount);
            GetParam(effect, "WindowColour")?.SetValue(_windowColourEase * _fadeFlood);
            GetParam(effect, "PaneDaylight")?.SetValue(_paneDaylightEase * _fadeFlood);
            GetParam(effect, "WindowBeam")?.SetValue(new Vector4(lean, reachTiles, 0.9f, 1f));
            // How much a wall between the pane and a pixel takes off that pixel's beam. The
            // beam used to be a shape alone and passed through everything it crossed; this is
            // the A/B for that, and at zero the shader skips the march whole.
            GetParam(effect, "WindowShaftBlock")?.SetValue(WindowShaftBlocking);
            // Pane footprint: a farmhouse window is about a tile across and a tile and a half
            // tall. The z term is the 12px the beam origin sits below the pane's centre,
            // expressed in tiles, so the glass and the beam agree on where the window is.
            GetParam(effect, "WindowPane")?.SetValue(new Vector4(0.55f, 0.8f, 12f / 64f, 0.35f));
            GetParam(effect, "DebugEmitter")?.SetValue(DebugChannel == DebugOverlayChannel.Emitter ? 1f : 0f);
            GetParam(effect, "DebugLampShadow")?.SetValue(DebugChannel == DebugOverlayChannel.LampShadow ? 1f : 0f);

            _reportedWindowsHere = windowsHere;
            _reportedWindowBeamOn = config.WindowBeamEnabled;
            _reportedInteriorWindowed = interiorWindowed;
            _reportedWindowGlows = location?.lightGlows.Count ?? -1;
            // The glow POSITIONS are kept; the sentence about them is built in the report. This
            // is the flood stage, so it runs on every frame of every lit room, and a StringBuilder
            // plus one formatted pair per glow was being thrown away sixty times a second for a
            // line nobody reads until radiance_report asks for it.
            _reportedWindowGlowPositions.Clear();
            if (location != null)
                foreach (Vector2 glow in location.lightGlows)
                    _reportedWindowGlowPositions.Add(glow);
            _reportedWindowRoomScale = FloodLightmap.WindowRoomScale;
            _reportedWindowCount = windowCount;
            _reportedWindowColour = _windowColourEase * _fadeFlood;
            _reportedPaneDaylight = _paneDaylightEase * _fadeFlood;
            _reportedWindowLean = lean;
            _reportedWindowReach = reachTiles;
            _reportedExposure = Vector3.Lerp(Vector3.One, _exposureEase, _fadeFlood);
            _reportedRoomSaturation = MathHelper.Lerp(1f, _roomSaturationEase, _fadeFlood);
            _reportedGiStrength = config.FloodLightingStrength;
            _reportedHearthFloor = windowedRoom ? HearthLitRoomFloor * _fadeFlood : 0f;
            _reportedDirectScale = directScale;
        }

        private void RenderWater(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _water!;
            var who = Game1.player;
            SetWaterRippleParams(effect, config);
            SetMirrorSourceParams(effect);
            SetReflectionStyleParams(effect, config);
            SetPlayerExclusionParams(effect, who);
            (float sunWarm, float nightGlow) = SetTimeOfDayParams(effect, config);
            SetSkyParams(effect, config, sunWarm, nightGlow);
            SetGlimmerLights(effect, nightGlow);
            SetCausticParams(effect, config, nightGlow);
            SetWadingParam(effect, who);

            effect.CurrentTechnique = effect.Techniques["Water"];
            DrawFull(spriteBatch, source, destination, effect);
            // Presence enforced outside the shader (see BlendBackSource): the in-shader uniform
            // measured inert, and the wet-rim early return never passes through it anyway.
            // The blend weight carries BOTH fades: the config toggle's and the one for water
            // scrolling out of the mask window. This is the term that covers every other term in
            // the shader, including its early returns, so folding the window fade in here is what
            // makes the pass leave gradually instead of being cut out from under the frame.
            BlendBackSource(spriteBatch, source, destination, _fadeWater * MathHelper.Clamp(_waterInMaskEase, 0f, 1f));
            // The sky half of the precipitation lands here, on the rippled result, so streaks
            // hang straight over the river instead of waving with it. This side of the capture
            // never meets the vanilla lightmap, so the particles' own ambient dims it instead.
            // It pays its own bill: the time is booked under precipitation (inside the call) and
            // taken back out of this stage's CPU column, which double-counted it and read as the
            // water pass costing ten times more in rain.
            long precipitationStart = Stopwatch.GetTimestamp();
            PrecipitationSystem.DrawSkyForChain(spriteBatch, destination, _frameWidth, AmbientLightOnParticles());
            ExcludeTicksFromOpenStage(Stopwatch.GetTimestamp() - precipitationStart);
        }

        /// <summary>How agitated the surface is this frame: weather, season, the shimmer toggle's ease, the
        /// cutscene displacement gate and the calmer indoor treatment.</summary>
        private void SetWaterRippleParams(Effect effect, ModConfig config)
        {
            // Weather/season drive how agitated the water is: choppier & faster in
            // rain/storm, sluggish in winter; sparkle fades when there's no sun.
            ComputeWaterDynamics(out float strengthMultiplier, out float speedMultiplier, out float sparkleMultiplier, out _causticWeatherMultiplier);
            // The stage can run for the REFLECTION alone (shimmer toggled off): ripple,
            // sparkle, tint and rim all zero out; the mirror keeps working independently.
            // The toggle itself eases too: with the reflection keeping the stage alive,
            // flipping the shimmer switch used to snap every ripple term in one frame.
            Approach(ref _shimmerEase, config.WaterEnabled ? 1f : 0f, 0.08f);
            float shimmer = _shimmerEase * _fadeWater;   // presence fade: never pops in
            // W8: during a cutscene the game draws the event UI (the SKIP button, dialogue)
            // as part of the world frame, so the ripple's pixel DISPLACEMENT bent it over
            // water/lava. Zero the displacement in events (same treatment as CA/tilt-shift) —
            // but keep tint / reflection / sparkle, which don't move pixels, so the water
            // still reads correctly in the cinematic. Eased over ~0.1s: events can start
            // without a screen fade, and the flat-water snap was the tell.
            bool eventUp = Game1.eventUp || Game1.CurrentEvent != null;
            Approach(ref _displacementGateEase, eventUp ? 0f : 1f, 0.15f);
            float displacementGate = _displacementGateEase;
            // Indoor water (hot spring, sewer, caves) sits under a ceiling, often in steam:
            // there is no sun to sparkle, no sky to mirror sharply, and the pale pool art
            // blows out under the full outdoor treatment. Calmer waves, faint reflection.
            bool indoors = !(Game1.currentLocation?.IsOutdoors ?? true);
            float indoorWave = indoors ? 0.6f : 1f;
            float indoorSparkle = indoors ? 0.35f : 1f;
            float indoorReflection = indoors ? 0.35f : 1f;
            float indoorTint = indoors ? 0.5f : 1f;
            // Whole-pass presence (see water.effect): the per-term fades below do not reach every
            // term, so the pass held full strength down to a fade of 0.02 and then popped out.
            GetParam(effect, "Presence")?.SetValue(_fadeWater);
            GetParam(effect, "Time")?.SetValue(Time());
            GetParam(effect, "Strength")?.SetValue(config.WaterStrength * strengthMultiplier * shimmer * displacementGate * indoorWave);
            GetParam(effect, "Speed")?.SetValue(config.WaterSpeed * speedMultiplier);
            GetParam(effect, "Sparkle")?.SetValue(config.WaterSparkle * sparkleMultiplier * shimmer * indoorSparkle);
            // A cloud over the water takes the sun's glitter with it: the same kept mask, the same
            // refusal rules and the same offset the sun shafts use, with its own ease so the two
            // consumers cannot step each other. Off is a target of zero, eased, never a snap.
            (float sparkleCloudTarget, Vector2 sparkleCloudShift) = CloudCouplingNow();
            if (!config.WaterSparkleCloudShade)
                sparkleCloudTarget = 0f;
            Approach(ref _sparkleCloudEase, sparkleCloudTarget, 0.05f);
            GetParam(effect, "CloudMaskTexture")?.SetValue(_cloudMaskKeep);
            GetParam(effect, "CloudCouple")?.SetValue(_sparkleCloudEase);
            GetParam(effect, "CloudMaskShift")?.SetValue(sparkleCloudShift);
            // The glitter follows the sun (see water.fx SunAxis): the same lean the shadows and
            // the sun shafts use, so the glints stretch the way the shadows lie. A low sun
            // stretches them most; a noon sun a third as much. No sun (night, rain, a room) is a
            // target of zero, eased, which is the round glitter of every earlier release.
            float glitterTarget = 0f;
            Vector2 sunAxisTarget = new Vector2(0f, 1f);
            if (ShadowRenderer.SunInSky(out Vector2 sunTravel, out float sunHeight))
            {
                sunAxisTarget = sunTravel;
                glitterTarget = MathHelper.Clamp(config.WaterGlitterPath, 0f, 1f) * (0.35f + 0.65f * (1f - sunHeight));
            }
            Approach(ref _glitterPathEase, glitterTarget, 0.03f);
            Vector2 sunAxisEased = Vector2.Lerp(_glitterSunAxis, sunAxisTarget, 0.05f);
            _glitterSunAxis = Determinism.Settle(sunAxisEased.LengthSquared() > 0.001f ? Vector2.Normalize(sunAxisEased) : sunAxisTarget, sunAxisTarget);
            GetParam(effect, "SunAxis")?.SetValue(_glitterSunAxis);
            GetParam(effect, "GlitterPath")?.SetValue(_glitterPathEase * shimmer);
            GetParam(effect, "TintAmount")?.SetValue(0.35f * shimmer * indoorTint);
            GetParam(effect, "ReflectStrength")?.SetValue((config.WaterReflection ? config.WaterReflectStrength : 0f) * _fadeWater * indoorReflection);
        }

        private float _causticWeatherMultiplier = 1f;
        private float _causticEase;
        /// <summary>What the shader was actually handed this frame, for the report: the one
        /// number that says whether the term is alive without anyone squinting at a lake.</summary>
        internal float _causticAmountUploaded;
        internal float _causticDaylight = 1f;
        internal bool CausticTextureMissing => _causticTextureMissing;
        internal float CausticEase => _causticEase;
        internal float CausticWeatherMultiplier => _causticWeatherMultiplier;
        internal float ShimmerEase => _shimmerEase;
        internal float FadeWaterForReport => _fadeWater;
        private Texture2D? _causticTexture;
        private bool _causticTextureMissing;

        /// <summary>The caustic net on shallow beds: strength folded down to one uniform.</summary>
        /// <remarks>
        /// The shader runs after lighting and has no lightmap, so an ungated additive would glow
        /// in the dark. Night is therefore multiplied out HERE, on the same 90-minute nightGlow
        /// ramp the glimmer lights ride, and the toggle gets its own ease so flipping it in the
        /// tuner fades rather than pops. Indoors is halved, not killed: a hot spring under a roof
        /// still catches lamplight, just not the sun.
        /// </remarks>
        private void SetCausticParams(Effect effect, ModConfig config, float nightGlow)
        {
            Approach(ref _causticEase, config.WaterCausticsEnabled ? 1f : 0f, 0.08f);
            float causticAmount = 0f;
            _causticDaylight = 1f - nightGlow;
            if (_causticEase > 0.001f && !_causticTextureMissing)
            {
                if (_causticTexture == null)
                {
                    _causticTexture = LoadTexture("caustics.png");
                    _causticTextureMissing = _causticTexture == null;
                }
                if (_causticTexture != null)
                {
                    GetParam(effect, "CausticTexture")?.SetValue(_causticTexture);
                    bool indoors = !(Game1.currentLocation?.IsOutdoors ?? true);
                    float daylight = 1f - nightGlow;
                    float indoorSoften = indoors ? 0.5f : 1f;
                    causticAmount = config.WaterCausticsStrength * 0.9f * _causticWeatherMultiplier
                        * _causticEase * _shimmerEase * _fadeWater * daylight * indoorSoften;
                }
            }
            GetParam(effect, "CausticAmount")?.SetValue(causticAmount);
            // One: the net covers the whole surface evenly, by decision (19/8). The shore shelf
            // was tried at several widths and floors and either vanished under the foam band or
            // read as no different from the open water; the shader keeps the shelf math so a
            // future floor below 1 brings it back, but for now even is the look.
            GetParam(effect, "CausticDeepFloor")?.SetValue(1f);
            GetParam(effect, "DebugCaustic")?.SetValue(DebugChannel == DebugOverlayChannel.Caustic ? 1f : 0f);
            GetParam(effect, "DebugMirrorSource")?.SetValue(DebugChannel == DebugOverlayChannel.MirrorSource ? 1f : 0f);
            GetParam(effect, "DebugSky")?.SetValue(DebugChannel == DebugOverlayChannel.Sky ? 1f : 0f);
            _causticAmountUploaded = causticAmount;
        }

        /// <summary>The textures the mirror reads: the sprite exclusion mask, the flipped-entity layer and
        /// the sprite-free scenery source.</summary>
        private void SetMirrorSourceParams(Effect effect)
        {
            // Per-frame sprite exclusion mask (ducks, NPCs, critters on the water).
            GetParam(effect, "SpriteMaskOn")?.SetValue(SpriteMaskReady && _spriteMaskRenderTarget != null ? 1f : 0f);
            GetParam(effect, "SpriteMaskTexture")?.SetValue(_spriteMaskRenderTarget);
            // P3b: flipped-entity reflection layer — the mirror's PREFERRED source. Where
            // this RT has content, it is the correct reflection by construction; the
            // screen-space flip only fills in scenery behind it (until P3c replaces that too).
            GetParam(effect, "ReflectedEntitiesOn")?.SetValue(ReflectRTReady && _reflectionRenderTarget != null ? 1f : 0f);
            GetParam(effect, "ReflectedEntitiesHasPlayer")?.SetValue(ReflectRTReady && ReflectRTHasPlayer ? 1f : 0f);
            GetParam(effect, "ReflectedEntitiesTexture")?.SetValue(_reflectionRenderTarget);
            // P3c: sprite-free scenery source — the mirror reads the map's own pixels, so
            // an excluded sprite can't leave a body-shaped sky hole in the reflection.
            // The raw layer render carries no lighting; ambient rescales it to the scene.
            GetParam(effect, "SceneOn")?.SetValue(SceneRTReady && _mirrorSourceRenderTarget != null && !SceneSourceOff ? 1f : 0f);
            GetParam(effect, "SceneTexture")?.SetValue(_mirrorSourceRenderTarget);
            GetParam(effect, "SceneTopPad")?.SetValue(MirrorSourceTopPad);
            GetParam(effect, "SceneSidePad")?.SetValue(MirrorSourceSidePad);
        }

        /// <summary>The named reflection look, how much it distorts, and the mask textures the shader needs
        /// to find the water at all.</summary>
        private void SetReflectionStyleParams(Effect effect, ModConfig config)
        {
            // The named reflection look. The surface's own movement and how much of it is allowed
            // to displace the MIRROR were one number, so the only way to read a reflection on a
            // rainy day - where the game makes the surface half again as choppy on its own - was
            // to turn the water down everywhere. Two questions, two answers.
            (float reflectionWobble, Vector3 reflectionTint) = config.WaterReflectStyle switch
            {
                WaterReflectionStyle.StillWater => (0.15f, new Vector3(0.80f, 0.86f, 0.96f)),
                WaterReflectionStyle.Choppy     => (1.90f, new Vector3(0.60f, 0.72f, 0.90f)),
                _                               => (1.00f, new Vector3(0.66f, 0.76f, 0.92f)),
            };
            // The 1.6.2 water is a second set of rules in the shader, not a fourth pair of
            // numbers: the classic shear and ripple terms are zeroed and the travelling field,
            // the contact anchor, the parallax and the photographic operators take over. Its
            // five settings of its own reach the shader whatever the water, and do nothing there
            // until ReflectionModel is 1.
            bool realistic = config.WaterReflectModel == WaterReflectionModel.Modern;
            // One amount scaling BOTH halves of the distortion. The named look above chooses the
            // character; this chooses how much of it there is, and at zero the reflection is a flat
            // mirror no matter which look is selected. The wave shear is the half the named looks
            // never touched, which is why none of them could reach a mirror on their own.
            float reflectionDistort = config.WaterReflectDistort;
            GetParam(effect, "MirrorShear")?.SetValue(realistic ? 0f : reflectionDistort);
            GetParam(effect, "ReflectionWobble")?.SetValue(reflectionWobble * config.WaterReflectDistort);
            GetParam(effect, "ReflectionModel")?.SetValue(realistic ? 1f : 0f);
            GetParam(effect, "ReflectionWobbleAmount")?.SetValue(config.WaterModernWobble);
            GetParam(effect, "ReflectionChoppiness")?.SetValue(config.WaterModernChoppiness);
            GetParam(effect, "ReflectionParallax")?.SetValue(config.WaterModernParallax);
            GetParam(effect, "ReflectionFresnel")?.SetValue(config.WaterModernFresnel);
            GetParam(effect, "ReflectionStretch")?.SetValue(config.WaterModernStretch);
            GetParam(effect, "ReflectionEdgeSoftness")?.SetValue(config.WaterModernEdgeSoftness);
            GetParam(effect, "ReflectionPlungeChurn")?.SetValue(config.WaterModernPlungeChurn);
            GetParam(effect, "ReflectionPlungeReach")?.SetValue(config.WaterModernPlungeReach);
            GetParam(effect, "ReflectionLipFade")?.SetValue(config.WaterModernLipFade);
            GetParam(effect, "ReflectionSoftness")?.SetValue(config.WaterReflectBlur);
            GetParam(effect, "ReflectionDepthScale")?.SetValue(config.WaterReflectDepth);
            // Passed as steps per TILE, which is what the shader needs to round with, rather than
            // as the pixel height the setting is written in. Zero means do not round at all.
            GetParam(effect, "ShearSteps")?.SetValue(
                config.WaterReflectBanding > 0.01f ? 64f / config.WaterReflectBanding : 0f);
            GetParam(effect, "ReflectionTint")?.SetValue(reflectionTint);
            GetParam(effect, "SceneAmbient")?.SetValue(Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting));
            GetParam(effect, "WaterKind")?.SetValue(WaterKind());
            GetParam(effect, "TilesPerScreen")?.SetValue(_waterMaskTilesPerScreen);
            GetParam(effect, "WorldTileOffset")?.SetValue(_waterMaskWorldTileOffset);
            GetParam(effect, "MaskSize")?.SetValue(_waterMaskPixelSize);
            GetParam(effect, "MaskOrigin")?.SetValue(new Vector2(_lastWaterTileX, _lastWaterTileY));
            GetParam(effect, "MaskTexture")?.SetValue(_waterMask);
            GetParam(effect, "SdfTexture")?.SetValue(_waterSignedDistanceTexture);
            // Foam reads this one instead: same encoding, but it only has an edge where water
            // meets real land, so a bridge stops growing a shoreline of its own.
            GetParam(effect, "RealShoreSdfTexture")?.SetValue(_waterRealShoreDistanceTexture ?? _waterSignedDistanceTexture);
            // Never unbound: an empty slot samples black, which is "on the lip and right under a
            // fall" everywhere and would take the whole mirror away. One far texel stands in.
            GetParam(effect, "PlungeChurnTexture")?.SetValue(_waterPlungeChurnTexture ?? FallDistanceFarTexture());
            // The mirror asks how tall the map is where its source lands: flat ground shows in the
            // water as a lip, not as a sheet.
            Texture2D surfaceClasses = SurfaceClassTextureFor(Game1.currentLocation);
            GetParam(effect, "SurfaceClassTexture")?.SetValue(surfaceClasses);
            GetParam(effect, "MapTiles")?.SetValue(new Vector2(surfaceClasses.Width, surfaceClasses.Height));
            GetParam(effect, "SparkleDensity")?.SetValue(config.WaterSparkleDensity);
            // Things moving in the water leave rings. The step happens here rather than in the
            // update tick because this is where the water is drawn at all, and it holds itself to
            // one pass per tick however many screens ask.
            UpdateWakeRings(config);
            SetWakeRingParams(effect, config);
            // The same wind the rain and the trees lean with, carried into the surface. Stepped
            // here for the same reason the rings are, and held to one pass per tick.
            UpdateWaterWind(config);
            SetWaterWindParams(effect, config);
        }

        /// <summary>The player's own silhouette, so ring-tile effects skip exactly their pixels.</summary>
        private void SetPlayerExclusionParams(Effect effect, Farmer? who)
        {
            // Player SILHOUETTE mask (the shadow system's per-frame bake) in buffer UV —
            // ring-tile water effects skip exactly the player's own pixels, so a blue outfit
            // on a pier never ripples while the water right beside them stays animated.
            // The COLOUR twin first, and the silhouette only as the fallback. Both are the same
            // pose at the same anchor in the same render target and the shader reads nothing but
            // alpha, so they are interchangeable here - except that the silhouette belongs to the
            // shadow system, which fades it toward the far tip on purpose: full at the feet down
            // to a twentieth at the head. The shader then thresholds at 0.15, and the fade crosses
            // 0.15 about ten pixels BELOW the top of the head, so the crown of a farmer's head was
            // never excluded and rippled with the water it stood beside. The colour bake carries
            // the sprite's own alpha and no fade, which is exactly why every other farmer in
            // co-op was already given it and not this one.
            //
            // It stays a fallback rather than a requirement: the colour twin is only baked when
            // the reflection or a puddle wants it, so with reflections switched off there is
            // nothing to read and the old behaviour is still the right answer. Widening that gate
            // would buy those players the ten pixels for a second FarmerRenderer draw, which is
            // not a trade to make without measuring it on somebody who turned reflections off to
            // go faster.
            var playerMask = ShadowRenderer.PlayerColor ?? ShadowRenderer.PlayerMask;
            var playerBox = new Vector4(2f, 2f, -1f, -1f);   // empty box (never matches)
            // A seated farmer is on a bench, not in the water, and the silhouette that would be
            // laid over them is the standing bake, taller than the body it covers: what it covered
            // over the beach pier bench was a rectangle of dead water above the player's head.
            if (who != null && playerMask != null && !who.IsSitting())
            {
                // The box has to overlay the DRAWN sprite, whose bottom edge the bake pins to the
                // anchor, so the anchor is where the game drew the body this frame and not the
                // collision box: the two differ by the swim bob, a jump, and the frame's own offset
                // (see FarmerDrawnAnchor). Anchoring at the shadow's feet line (bottom - 10) or a
                // whole yOffset above the box both left a strip of dead water over the head.
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport, ShadowRenderer.FarmerDrawnAnchor(who));
                Vector2 topLeft = feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f);
                // Screen px -> UV against the FRAME the game drew, not this pass's target
                // (see _frameWidth): with render scale on they are different sizes.
                playerBox = new Vector4(topLeft.X / _frameWidth, topLeft.Y / _frameHeight,
                    (topLeft.X + ShadowRenderer.PlayerRtW) / _frameWidth, (topLeft.Y + ShadowRenderer.PlayerRtH) / _frameHeight);
            }
            GetParam(effect, "PlayerRect")?.SetValue(playerBox);
            GetParam(effect, "PlayerMaskTexture")?.SetValue(playerMask);
        }

        /// <summary>Golden hour, night glow, moonlight and raindrop rings. Returns the two amounts the sky
        /// tint below is built from.</summary>
        private (float SunWarm, float NightGlow) SetTimeOfDayParams(Effect effect, ModConfig config)
        {
            // Time-of-day / weather dressing: golden-hour sparkle, star reflections and
            // lamp glimmer after dusk, raindrop rings while raining.
            var (sunWarm, nightGlow) = TimeOfDayAmounts();
            GetParam(effect, "SunWarm")?.SetValue(sunWarm);
            GetParam(effect, "NightGlow")?.SetValue(nightGlow);
            GetParam(effect, "MoonGlow")?.SetValue(ShadowRenderer.MoonStrength());
            // Raindrop rings ease in rather than covering the surface the frame a rain
            // totem (or a weather mod) flips the flag.
            // IsRainingHere, not the legacy static: that one mirrors the Default context only,
            // so rain on Ginger Island rang no rings at all while a dry valley rang them.
            bool rainingHere = Game1.currentLocation?.IsRainingHere() ?? false;
            Approach(ref _rainRingsEase, rainingHere ? 1f : 0f, 0.04f);
            GetParam(effect, "RainAmount")?.SetValue(_rainRingsEase);
            GetParam(effect, "RainRingDensity")?.SetValue(config.WaterRainRingDensity);
            GetParam(effect, "RainRingSize")?.SetValue(config.WaterRainRingSize);
            GetParam(effect, "RainRingStrength")?.SetValue(config.WaterRainRingStrength);
            return (sunWarm, nightGlow);
        }

        /// <summary>How much golden hour and how much dusk there is right now, 0 to 1 each. Its own
        /// method because the glass wants the same two numbers with no effect to set them on.</summary>
        private static (float SunWarm, float NightGlow) TimeOfDayAmounts()
        {
            float minutesNow = ClockMinutes();
            // Golden hour, on the clock and without a cliff. This read the raw HHMM value (so it
            // lurched at every hour boundary) and then cut to zero the instant the clock passed
            // 19:00 - full warmth at 18:50, none at 19:00, in one step, which is the flash of a
            // changed picture at seven in the evening. Ramp it down over the last half hour
            // instead, on minutes, so it arrives at zero having already faded there.
            float sunWarm = 0f;
            if (!LocalSky.IsRaining)
            {
                float dayProgress = MathHelper.Clamp((minutesNow - 12 * 60) / 360f, -1f, 1f);
                sunWarm = MathHelper.Clamp((Math.Abs(dayProgress) - 0.55f) / 0.45f, 0f, 1f);
                sunWarm *= MathHelper.Clamp((19 * 60 - minutesNow) / 30f, 0f, 1f);
            }
            float nightGlow = MathHelper.Clamp((minutesNow - 1140) / 90f, 0f, 1f);   // 19:00 → 20:30
            return (sunWarm, nightGlow);
        }

        /// <summary>The synthesised sky the water reflects before it reflects anything else.</summary>
        private void SetSkyParams(Effect effect, ModConfig config, float sunWarm, float nightGlow)
        {
            // SKY tint for the mirror's far end and the no-mirror sheen. Water reflects the sky
            // before it reflects anything else; for an orthographic fixed-pitch camera the Fresnel
            // mix is a CONSTANT, so the only things that vary are WHICH source (object vs sky) and
            // the ripple breakup — never strength-by-distance. Stardew has no sky to sample
            // top-down, so it is synthesised from time and weather, then scaled by the lighting
            // stage's ambient so water never stays bright inside a darkened scene.
            Vector3 sky = SynthesisedSkyColour(sunWarm, nightGlow);
            sky *= Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting);
            GetParam(effect, "SkyColour")?.SetValue(sky);
            // B6: aurora - on a clear winter night the sky is not one flat colour, and the
            // water is the only mirror this camera ever sees the sky in. The whole gate (the
            // switch, winter, outdoors, clear weather, real night) rides ONE eased amount, so
            // dusk arriving or the weather flipping mid-evening never pops the curtains.
            float auroraTarget = config.AuroraEnabled && (LocalSky.Season == Season.Winter)
                && (Game1.currentLocation?.IsOutdoors ?? false)
                && !LocalSky.IsRaining && !LocalSky.IsSnowing && !LocalSky.IsLightning
                ? nightGlow * AuroraShowStrength() : 0f;
            _auroraAmount = Determinism.Settle(
                MathHelper.Lerp(_auroraAmount, auroraTarget, 0.02f), auroraTarget);
            // The dial rides the eased gate rather than the shader, so the whole no-popping
            // argument above still holds and the shader keeps one number to early-out on.
            float auroraUploaded = _auroraAmount * config.AuroraStrength;
            GetParam(effect, "AuroraAmount")?.SetValue(auroraUploaded);
            SkyAuroraUploaded = auroraUploaded;
            UpdateShootingStar(effect, config, nightGlow);
        }

        /// <summary>B6: a shooting star now and then, in the sky the water reflects. Any clear
        /// night, any season; roughly one a minute while the conditions hold, each lasting under a
        /// second. The clock is Determinism.Seconds, so a frozen capture neither starts one nor
        /// advances one, and two dumps of one frozen frame agree.</summary>
        private void UpdateShootingStar(Effect effect, ModConfig config, float nightGlow)
        {
            bool clearNight = config.ShootingStarsEnabled && nightGlow > 0.5f
                && (Game1.currentLocation?.IsOutdoors ?? false)
                && !LocalSky.IsRaining && !LocalSky.IsSnowing && !LocalSky.IsLightning;
            double now = Determinism.Seconds;

            for (int slot = 0; slot < MeteorSlots; slot++)
                if (_meteors[slot].Active && now - _meteors[slot].StartedAt >= _meteors[slot].Seconds)
                    _meteors[slot].Active = false;

            if (!clearNight)
                MeteorRequests = 0;
            if (clearNight && !Determinism.Frozen)
            {
                while (MeteorRequests > 0)
                {
                    int slot = FreeMeteorSlot();
                    if (slot < 0)
                        break;
                    SpawnShootingStar(slot, now, acrossTheView: true);
                    MeteorRequests--;
                }
                if (_meteorNextAt < 0)
                    _meteorNextAt = now + 8 + _meteorRandom.NextDouble() * 14;
                if (now >= _meteorNextAt)
                {
                    int slot = FreeMeteorSlot();
                    if (slot >= 0)
                        SpawnShootingStar(slot, now, acrossTheView: false);
                    if (_meteorBurstLeft > 0)
                    {
                        _meteorBurstLeft--;
                        _meteorNextAt = now + 0.15 + _meteorRandom.NextDouble() * 0.7;
                    }
                    else
                    {
                        _meteorBurstLeft = _meteorRandom.NextDouble() < 0.22 ? 1 + _meteorRandom.Next(2) : 0;
                        _meteorNextAt = now + (_meteorBurstLeft > 0
                            ? 0.2 + _meteorRandom.NextDouble() * 0.8
                            : 22 + _meteorRandom.NextDouble() * 38);
                    }
                }
            }

            float loudest = 0f;
            int burning = 0;
            for (int slot = 0; slot < MeteorSlots; slot++)
            {
                ShootingStar star = _meteors[slot];
                float envelope = 0f;
                Vector2 head = star.StartTile;
                if (star.Active)
                {
                    float progress = (float)Math.Clamp((now - star.StartedAt) / Math.Max(0.05f, star.Seconds), 0.0, 1.0);
                    envelope = (float)Math.Sin(Math.PI * progress);
                    head += star.Direction * (star.TravelTiles * progress);
                    burning++;
                }
                _meteorPaths[slot] = new Vector4(head.X, head.Y, star.Direction.X, star.Direction.Y);
                _meteorShapes[slot] = new Vector4(star.TailTiles, envelope, star.Width, star.Brightness);
                loudest = Math.Max(loudest, envelope);
            }
            GetParam(effect, "Meteors")?.SetValue(_meteorPaths);
            GetParam(effect, "MeteorShapes")?.SetValue(_meteorShapes);
            GetParam(effect, "MeteorAny")?.SetValue(loudest);
            SkyClearNight = clearNight;
            SkyMeteorEnvelope = loudest;
            SkyMeteorBurning = burning;
            SkyMeteorSecondsToNext = clearNight && _meteorNextAt >= 0
                ? Math.Max(0.0, _meteorNextAt - now) : -1;
        }

        /// <summary>A slot with nothing burning in it, or -1 when the sky is already full.</summary>
        private int FreeMeteorSlot()
        {
            for (int slot = 0; slot < MeteorSlots; slot++)
                if (!_meteors[slot].Active)
                    return slot;
            return -1;
        }

        /// <summary>Light one streak. Its weight is rolled here rather than fixed, because a sky
        /// where every meteor is the same size reads as one asset played over and over: most are
        /// faint and quick, a few are ordinary, and about one in twelve is a heavy one that burns
        /// wider, longer and warmer. The shader reads the warmth back off the weight.
        /// <para><paramref name="acrossTheView"/> is the console's request. A streak only exists
        /// where the sky does, which is water, and one placed by the player's feet lands on the
        /// pier they are standing on as often as not; asked-for streaks are spread across the
        /// view and given a longer run so at least one of them crosses open water.</para></summary>
        private void SpawnShootingStar(int slot, double now, bool acrossTheView)
        {
            float tilesAcross = Game1.viewport.Width / 64f, tilesDown = Game1.viewport.Height / 64f;
            float originX = Game1.viewport.X / 64f, originY = Game1.viewport.Y / 64f;
            double weight = _meteorRandom.NextDouble();
            float width, brightness, tailTiles, travelTiles, seconds;
            if (weight < 0.55)
            {
                width = 0.6f; brightness = 0.55f;
                tailTiles = 1.2f + 0.8f * (float)_meteorRandom.NextDouble();
                travelTiles = 3.0f + 2.0f * (float)_meteorRandom.NextDouble();
                seconds = 0.45f + 0.25f * (float)_meteorRandom.NextDouble();
            }
            else if (weight < 0.92)
            {
                width = 1.0f; brightness = 1.0f;
                tailTiles = 2.0f + 1.5f * (float)_meteorRandom.NextDouble();
                travelTiles = 4.5f + 3.0f * (float)_meteorRandom.NextDouble();
                seconds = 0.8f + 0.3f * (float)_meteorRandom.NextDouble();
            }
            else
            {
                width = 1.9f; brightness = 1.8f;
                tailTiles = 4.0f + 2.5f * (float)_meteorRandom.NextDouble();
                travelTiles = 7.0f + 4.0f * (float)_meteorRandom.NextDouble();
                seconds = 1.4f + 0.6f * (float)_meteorRandom.NextDouble();
            }
            float acrossFraction = acrossTheView
                ? 0.05f + 0.90f * (float)_meteorRandom.NextDouble()
                : 0.15f + 0.70f * (float)_meteorRandom.NextDouble();
            float downFraction = acrossTheView
                ? 0.02f + 0.25f * (float)_meteorRandom.NextDouble()
                : 0.10f + 0.60f * (float)_meteorRandom.NextDouble();
            if (acrossTheView)
                travelTiles = Math.Max(travelTiles, tilesDown * 0.8f);
            float angle = MathHelper.ToRadians(20f + 40f * (float)_meteorRandom.NextDouble());
            float side = _meteorRandom.NextDouble() < 0.5 ? -1f : 1f;
            _meteors[slot] = new ShootingStar
            {
                Active = true,
                StartedAt = now,
                Seconds = seconds,
                StartTile = new Vector2(originX + acrossFraction * tilesAcross,
                                        originY + downFraction * tilesDown),
                Direction = new Vector2(side * (float)Math.Cos(angle), (float)Math.Sin(angle)),
                TailTiles = tailTiles,
                TravelTiles = travelTiles,
                Width = width,
                Brightness = brightness,
            };
        }

        /// <summary>How much of tonight's aurora display is up right now, 0 to 1.
        ///
        /// <para>An aurora that is simply on from dusk to dawn on every clear winter night is
        /// wallpaper. A real one is an EVENT: some nights have none at all, and the ones that do
        /// get an hour or three of it that builds and dies. Rolled once per night from the day
        /// number, so every screen in split screen agrees, a frozen capture cannot drift, and
        /// walking in and out of a building does not re-roll the sky.</para></summary>
        private static float AuroraShowStrength()
        {
            int today = (int)(Game1.stats?.DaysPlayed ?? 0u);
            if (today != _auroraShowDay)
            {
                _auroraShowDay = today;
                var nightly = new Random(unchecked(today * 397 + 0x4A5));
                // Rather more than half of clear winter nights carry one. Clear winter nights
                // are themselves uncommon, so a harsher roll than this would make the feature
                // something most players never meet.
                _auroraShowTonight = nightly.NextDouble() < 0.62;
                _auroraShowStart = 1180f + (float)nightly.NextDouble() * 320f;    // 19:40 .. 25:00
                _auroraShowLength = 100f + (float)nightly.NextDouble() * 200f;    // 1h40 .. 5h
            }
            SkyAuroraTonight = _auroraShowTonight || AuroraForce > 0;
            SkyAuroraShowStart = _auroraShowStart;
            SkyAuroraShowEnd = _auroraShowStart + _auroraShowLength;
            if (AuroraForce > 0)
                return SkyAuroraShow = 1f;
            if (AuroraForce < 0 || !_auroraShowTonight)
                return SkyAuroraShow = 0f;
            const float RampMinutes = 35f;
            float minutes = ClockMinutes();
            return SkyAuroraShow = Math.Min(
                MathHelper.Clamp((minutes - _auroraShowStart) / RampMinutes, 0f, 1f),
                MathHelper.Clamp((_auroraShowStart + _auroraShowLength - minutes) / RampMinutes, 0f, 1f));
        }

        /// <summary>The aurora's strength as a pure function of the night, for the passes that
        /// have to know about it but do not run where the water does: the whole-frame sky tint
        /// and the glass. Reading the water pass's own eased number would leave both of them
        /// stale in any scene with no water on screen, which is most of a town.</summary>
        private static float AuroraSceneAmount(ModConfig config)
        {
            if (!config.AuroraEnabled || !(LocalSky.Season == Season.Winter)
                || !(Game1.currentLocation?.IsOutdoors ?? false)
                || LocalSky.IsRaining || LocalSky.IsSnowing || LocalSky.IsLightning)
                return 0f;
            float nightGlow = MathHelper.Clamp((ClockMinutes() - 1140) / 90f, 0f, 1f);
            return nightGlow * AuroraShowStrength() * MathHelper.Clamp(config.AuroraStrength, 0f, 2f);
        }

        /// <summary>The colour the sky is lighting the world with, as a luminance-preserving
        /// multiply. White unless an aurora is up.</summary>
        internal static Vector3 SkyLightTintNow(ModConfig config)
        {
            float amount = Math.Min(1f, AuroraSceneAmount(config));
            SkyAuroraGlass = amount;
            if (amount <= 0.001f)
                return Vector3.One;
            // A green-cyan push of a few percent, normalised so its luminance is exactly 1: the
            // world goes the colour of the sky without going one step brighter or darker. The
            // lesson the bounced-light colour was built on, and the reason the brightness family
            // of bug reports cannot be reopened by this.
            Vector3 tint = new(0.88f, 1.06f, 0.99f);
            float luminance = Vector3.Dot(tint, new Vector3(0.299f, 0.587f, 0.114f));
            tint /= Math.Max(0.001f, luminance);
            return Vector3.Lerp(Vector3.One, tint, amount * 0.85f);
        }

        /// <summary>The colour of the sky itself at this hour and in this weather, before the
        /// lighting stage's ambient is applied. Water takes it dimmed by that ambient; the glass
        /// takes it plain, because a reflection drawn into the world batch is lit with the world.</summary>
        private static Vector3 SynthesisedSkyColour(float sunWarm, float nightGlow)
        {
            Vector3 sky = new(0.62f, 0.78f, 0.96f);                                  // open daylight
            sky = Vector3.Lerp(sky, new Vector3(0.98f, 0.72f, 0.45f), sunWarm);      // golden hour
            sky = Vector3.Lerp(sky, new Vector3(0.08f, 0.12f, 0.28f), nightGlow);    // dusk → night
            if (LocalSky.IsRaining || LocalSky.IsSnowing)
                sky = Vector3.Lerp(sky, new Vector3(0.52f, 0.56f, 0.62f), 0.75f);    // overcast
            if (!(Game1.currentLocation?.IsOutdoors ?? true))
                sky = Vector3.Lerp(sky, new Vector3(0.30f, 0.33f, 0.40f), 0.7f);     // no sky indoors
            return sky;
        }

        /// <summary>Lamp glimmer after dusk: up to eight on-screen lights, in frame UV.</summary>
        private void SetGlimmerLights(Effect effect, float nightGlow)
        {
            int lightCount = 0;
            if (nightGlow > 0f && Game1.currentLightSources != null)
            {
                foreach (var light in Game1.currentLightSources.Values)
                {
                    if (lightCount >= 8)
                        break;
                    Vector2 screenPosition = Game1.GlobalToLocal(Game1.viewport, light.position.Value);
                    // Screen px throughout, so the bounds test and the UV both use the frame
                    // the game drew rather than this pass's (possibly scaled) target.
                    if (screenPosition.X < -160 || screenPosition.X > _frameWidth + 160 || screenPosition.Y < -160 || screenPosition.Y > _frameHeight + 160)
                        continue;
                    _waterGlimmerLights[lightCount++] = new Vector4(screenPosition.X / _frameWidth, screenPosition.Y / _frameHeight, light.radius.Value, 0.9f);
                }
            }
            GetParam(effect, "LightCount")?.SetValue((float)lightCount);
            GetParam(effect, "Lights")?.SetValue(_waterGlimmerLights);
        }

        /// <summary>Wading: whether the player's feet are on water pixels, eased so the self-reflection does
        /// not pop at the edge.</summary>
        private void SetWadingParam(Effect effect, Farmer? who)
        {
            // Wading: are the player's feet on water pixels? (mask texel = 4 world px)
            // SWIMMING is excluded: half the body is already underwater, so a mirrored
            // silhouette below the feet reads as a glitch, not a reflection — the ripple
            // exclusion (silhouette gate) is what protects the visible half instead.
            float wading = 0f;
            if (who != null && !who.swimming.Value)
            {
                Rectangle boundingBox = who.GetBoundingBox();
                Color? underFeet = ReadWaterMaskPixel(boundingBox.Center.X / 4 - _lastWaterTileX * 16,
                                                     (boundingBox.Bottom - 4) / 4 - _lastWaterTileY * 16);
                if (underFeet is { R: > 100 })
                    wading = 1f;
            }
            // Ease the wading state so the under-feet self-reflection fades in/out (~0.3s)
            // instead of popping the moment the feet cross the water edge.
            Approach(ref _wadingEase, wading, 0.12f);
            if (Math.Abs(wading - _wadingEase) < 0.01f) _wadingEase = wading;
            GetParam(effect, "PlayerInWater")?.SetValue(_wadingEase);
        }

        private void RenderFinishing(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _finishing!;
            // Both finishing toggles ease (advanced once per frame in Apply, shared with the
            // fused tail pass): a raw config bool would step straight into the frame.
            GetParam(effect, "VignetteStrength")?.SetValue(config.VignetteStrength * _vignetteEase);
            // Map the 0..1 UI value to a tiny UV offset so it stays subtle on pixel art.
            // No CA during events: the SKIP button is drawn inside the world frame and the
            // channel split shreds its text (community report). Vignette stays — it's the
            // cinematic part and doesn't hurt readability. (The event gate lives in the
            // Apply-side ease update.)
            GetParam(effect, "CAStrength")?.SetValue(config.ChromaticAberrationStrength * 0.03f * _caEase);
            // A touch more vignette at night — but only as part of the vignette effect
            // itself: with Vignette OFF (e.g. only CA on) the shader must add nothing,
            // or "off" quietly darkens the night screen edges.
            GetParam(effect, "NightAmt")?.SetValue(NightFactorNow() * _vignetteEase);
            GetParam(effect, "ScreenPixels")?.SetValue(new Vector2(Game1.viewport.Width, Game1.viewport.Height));
            // Heat haze: hot air over lava bends the picture. Strength carries
            // the presence ease, so switching it off (or walking away from the heat) melts the
            // wobble out instead of snapping the pixels straight.
            GetParam(effect, "SkyLightTint")?.SetValue(SkyLightTintNow(config));
            GetParam(effect, "HeatHazeStrength")?.SetValue(config.HeatHazeStrength * _heatHazeEase);
            if (_heatHazeEase > FadeGone && _heatMapTexture != null)
            {
                GetParam(effect, "HeatMapTexture")?.SetValue(_heatMapTexture);
                GetParam(effect, "HeatMapOriginTiles")?.SetValue(_heatMapOriginTiles);
                GetParam(effect, "HeatMapSizeTiles")?.SetValue(_heatMapSizeTiles);
                GetParam(effect, "TilesPerScreen")?.SetValue(new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f));
                GetParam(effect, "WorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
                GetParam(effect, "HeatClock")?.SetValue(Determinism.ShaderSeconds);
                // The middle of the standing sprite, a little under a tile above the feet.
                Point feet = Game1.player.StandingPixel;
                GetParam(effect, "PlayerWorldTile")?.SetValue(new Vector2(feet.X / 64f, (feet.Y - 56f) / 64f));
            }
            effect.CurrentTechnique = effect.Techniques["Finishing"];
            DrawFull(spriteBatch, source, destination, effect);
        }

        private void RenderLighting(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D destination, ModConfig config)
        {
            var effect = _lighting!;
            // Presence fade: ambient darkening eases in from "no change" (white) on appearance.
            GetParam(effect, "AmbientColor")?.SetValue(Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting));
            // Whole-pass presence (see lighting.effect): the light pools are not scaled by the fade.
            GetParam(effect, "Presence")?.SetValue(_fadeLighting);
            GetParam(effect, "Aspect")?.SetValue(destination.Height > 0 ? destination.Width / (float)destination.Height : 1f);
            // The classic shader's arrays are shorter than the ranked list, so it takes the
            // top of it. The ranking already put the lights that matter most in front.
            Array.Copy(_lightPositions, _classicLightPositions, ClassicLightSlots);
            Array.Copy(_lightShaderData, _classicLightData, ClassicLightSlots);
            for (int i = _lightCount; i < ClassicLightSlots; i++)
            {
                _classicLightPositions[i] = Vector2.Zero;
                _classicLightData[i] = Vector4.Zero;
            }
            GetParam(effect, "LightPos")?.SetValue(_classicLightPositions);
            GetParam(effect, "LightData")?.SetValue(_classicLightData);
            GetParam(effect, "LightCount")?.SetValue(Math.Min(_lightCount, ClassicLightSlots));
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
            DrawFull(spriteBatch, source, destination, effect);
            // Same out-of-shader presence as the water pass: the light POOLS never rode the
            // fade, so this stage popped its full contribution in and out with the light list.
            BlendBackSource(spriteBatch, source, destination, _fadeLighting);
            // Last, after the blend-back as well as after the multiply: this stage runs behind
            // the flood one during a crossfade, so it is the one that owns the sparks.
            DrawEmissiveParticlesOnLighting(spriteBatch, destination, EmissiveParticleHost.Classic);
        }

        // World-anchor for drifting noise (fog/clouds): the offset must be in units of the
        // VISIBLE world span (viewport, world px) — dividing by the render target's screen px
        // made patterns slide against the world when zoom != 100%.
        private static Vector2 WorldOffset() =>
            new(Game1.viewport.X / (float)Math.Max(1, Game1.viewport.Width),
                Game1.viewport.Y / (float)Math.Max(1, Game1.viewport.Height));

        /// <summary>The player's position in screen UV (0..1), for the radial tilt-shift focus.</summary>
        private static Vector2 PlayerScreenUV()
        {
            if (Game1.player == null)
                return new Vector2(0.5f, 0.5f);
            Vector2 world = Game1.player.Position + new Vector2(32f, 32f); // sprite centre-ish
            Vector2 local = Game1.GlobalToLocal(Game1.viewport, world);
            int viewportWidth = Math.Max(1, Game1.viewport.Width);
            int viewportHeight = Math.Max(1, Game1.viewport.Height);
            return new Vector2(local.X / viewportWidth, local.Y / viewportHeight);
        }

        /// <summary>Game clock as MINUTES since midnight.
        /// <para>
        /// timeOfDay is HHMM, so 1850 + 10 minutes is 1900 - the number jumps 50 for a ten minute
        /// step. Curves that interpolated on the raw value therefore lurched five times their
        /// normal rate at every hour boundary, which is a visible step in a tint that is supposed
        /// to drift. Minutes make an hour worth sixty and the curves continuous.
        /// </para></summary>
        private static float ClockMinutes() => GameClock.MinutesNow();

        /// <summary>Fog tint by time of day: neutral haze by day, warm at dusk, blue at night.</summary>
        private static Vector3 FogColor()
        {
            float minutes = ClockMinutes();
            Vector3 day = new(0.72f, 0.76f, 0.82f);
            Vector3 dusk = new(0.85f, 0.68f, 0.55f);
            Vector3 night = new(0.38f, 0.44f, 0.60f);
            const int Dusk = 17 * 60, Late = 19 * 60 + 30, Night = 21 * 60, Dawn = 6 * 60;
            if (minutes >= Dusk && minutes < Late) return Vector3.Lerp(day, dusk, (minutes - Dusk) / (float)(Late - Dusk));
            if (minutes >= Late && minutes < Night) return Vector3.Lerp(dusk, night, (minutes - Late) / (float)(Night - Late));
            if (minutes >= Night || minutes < Dawn) return night;
            return day;
        }

        private static void ComputeAuto(out float temperature, out float saturationMultiplier)
        {
            temperature = 0f; saturationMultiplier = 1f;
            // Every term below describes the SKY: the hour's colour, rain, snow, the season. A
            // room with no window onto any of it was being graded by all four anyway, and the
            // interior lighting stage already walks the room's own colour through the day, so an
            // indoor scene at six in the evening was warmed twice - once by the room and again by
            // a dusk that is not visible from inside it. Measured in the saloon: turning the whole
            // grade off took median saturation from 0.761 to 0.596, the largest single contributor
            // to a room that reads as blasted orange.
            //
            // Weather and season stay outdoors for the same reason a rainy day does not desaturate
            // a cellar. What does still reach an interior is the player's own Temperature and
            // Saturation settings, which are not automatic and are not ours to override.
            bool underSky = Game1.currentLocation?.IsOutdoors ?? true;
            if (!underSky)
                return;

            float minutes = ClockMinutes();
            const int Dusk = 17 * 60, Late = 19 * 60 + 30, Night = 21 * 60, Dawn = 6 * 60;
            if (minutes >= Dusk && minutes < Late) temperature += 0.25f * ((minutes - Dusk) / (float)(Late - Dusk));
            else if (minutes >= Late && minutes < Night) temperature += 0.25f - 0.55f * ((minutes - Late) / (float)(Night - Late));
            else if (minutes >= Night || minutes < Dawn) temperature -= 0.30f;

            if (LocalSky.IsRaining) { temperature -= 0.12f; saturationMultiplier *= 0.85f; }
            if (LocalSky.IsSnowing) { temperature -= 0.15f; saturationMultiplier *= 0.90f; }
            // The afternoon before rain: about half of what the rain itself does, so a player who
            // knows both can tell them apart, and it sits UNDER the dusk warming above rather than
            // replacing it. An evening that is both golden and cooling is exactly what a front
            // coming in over a sunset looks like.
            if (_stormWarningEased > 0.001f)
            {
                temperature -= 0.14f * _stormWarningEased;
                saturationMultiplier *= MathHelper.Lerp(1f, 0.90f, _stormWarningEased);
            }
            if (LocalSky.Season == Season.Winter) temperature -= 0.08f;
            else if (LocalSky.Season == Season.Summer) temperature += 0.05f;
        }

        /// <summary>
        /// Squeeze the scene into a tiny probe for the exposure meter to read. The reading itself
        /// happens on the game's update tick (<see cref="CollectExposureMeter"/>), because asking
        /// the card for it here waits for everything the card has been given. No-op unless auto
        /// is on.
        /// </summary>
        private void UpdateAutoExposure(SpriteBatch spriteBatch, Texture2D scene)
        {
            // Freeze mode PINS this rather than settling it like the other eased amounts. Every
            // other one eases toward a target computed from the scene; this one meters the frame
            // it is about to grade, so its target moves with its own output and there is no fixed
            // point to land on. Held at neutral so a capture is not multiplied by whatever the
            // meter happened to be reading when freeze was switched on.
            if (Determinism.Frozen)
            {
                _meteredExposure = 1f;
                _exposureTarget = 1f;
                return;
            }
            // THE EASE IS FREE AND THE READING IS NOT, so they run at different rates. Easing
            // toward the last reading is arithmetic and happens on every frame, which is also what
            // makes it smooth and frame-rate independent. Asking the card what the scene looks
            // like costs a sync whenever it is asked (see CollectExposureMeter), so it is asked
            // twice a second: scene brightness drifts slowly, the ease takes most of two seconds
            // to travel anyway, and walking into a room snaps rather than eases.
            Approach(ref _meteredExposure, _exposureTarget, MeteredExposureEaseRate);
            if (Game1.ticks % ExposureMeterPeriodTicks != 0)
                return;
            _luminanceRenderTarget ??= VramTally.Track(new RenderTarget2D(_device, LuminanceProbeSize, LuminanceProbeSize, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "luminance probe");
            _luminancePixels ??= new Color[LuminanceProbeSize * LuminanceProbeSize];

            _device.SetRenderTarget(_luminanceRenderTarget);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            spriteBatch.Draw(scene, new Rectangle(0, 0, LuminanceProbeSize, LuminanceProbeSize), Color.White);
            spriteBatch.End();
            // The card is told to draw it; nobody waits here to find out what it says. See
            // CollectExposureMeter, which asks on the game's update tick.
            _exposureReadPending = true;
            _exposureWrittenTick = Game1.ticks;
        }

        /// <summary>How wide the scene is squeezed to before it is metered. A thirty-two square
        /// average is the scene's brightness to well inside the tenth of a level the grade can
        /// act on, and it is four kilobytes to bring back off the card.</summary>
        private const int LuminanceProbeSize = 32;

        /// <summary>How long the probe is left alone before it is read. One tick: it was written
        /// during the last frame's draw, so the read happens on the next update rather than in
        /// the middle of the frame that wrote it.</summary>
        private const int ExposureReadDelayTicks = 1;

        /// <summary>Frames between readings. Every reading costs a sync with the card whenever it
        /// is taken, however old the thing being read is and wherever it is taken from, so the
        /// answer is to take fewer: twice a second against a response that takes most of two
        /// seconds to travel, and a snap rather than an ease whenever the room changes. Measured
        /// on the beach at 21:00: reading every fourth frame inside the draw waited for the card
        /// ten times a second, on the update tick five, and at this rate under one.</summary>
        private const int ExposureMeterPeriodTicks = 30;

        /// <summary>
        /// Read the scene brightness the draw measured, and ease the exposure toward it.
        ///
        /// <para>ON THE GAME'S UPDATE TICK, never in the draw. <c>GetData</c> waits for everything
        /// the card has been given, however old the thing being read is, which is the finding that
        /// closed ghi3038's stutter: the creature mirror's readback fell from 18 ms to 0.6 ms by
        /// moving exactly this way. This one was costing a hundred stalls and a 6 ms worst frame
        /// in a single report window, paid by everybody who turns the colour grade on.</para>
        ///
        /// <para>The screen is looked up by id rather than swapped in, because a tick is not a
        /// draw and the pipeline's own idea of the active screen belongs to whoever drew last.
        /// A screen with nothing pending is a dictionary miss and two comparisons.</para>
        /// </summary>
        internal void CollectExposureMeter()
        {
            if (!_screenStates.TryGetValue(StardewModdingAPI.Context.ScreenId, out ScreenState? screen))
                return;
            if (!screen.ExposureReadPending || screen.LuminanceTarget == null || screen.LuminancePixels == null)
                return;
            if (Game1.ticks - screen.ExposureWrittenTick < ExposureReadDelayTicks)
                return;
            screen.ExposureReadPending = false;
            // A device reset throws a render target's contents away, and metering a wiped probe
            // reads black, which is the largest brightening this meter can ask for. The next
            // draw writes it again.
            if (!GpuContent.Usable(screen.LuminanceTarget))
                return;
            long readStarted = Stopwatch.GetTimestamp();
            screen.LuminanceTarget.GetData(screen.LuminancePixels);
            NoteReadback((Stopwatch.GetTimestamp() - readStarted) * 1000.0 / Stopwatch.Frequency);
            float sum = 0f;
            for (int i = 0; i < screen.LuminancePixels.Length; i++)
            {
                Color pixel = screen.LuminancePixels[i];
                sum += (0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B) / 255f;
            }
            float luminance = sum / screen.LuminancePixels.Length;
            // key/lum > 1 brightens, < 1 dims; clamp so it only gently corrects.
            float target = MathHelper.Clamp(ExposureKey / Math.Max(luminance, ExposureDarkestMetered),
                ExposureDimmestAllowed, ExposureBrightestAllowed);
            // ARRIVING SOMEWHERE IS NOT A CHANGE IN THE LIGHT. The meter carries the last room's
            // reading through the door and then eases to the new one, and the ease is slow on
            // purpose: walking into Town measured a climb from 1.000 to 1.150 taking about ten
            // seconds, which is the whole picture brightening 15% while the player stands still.
            // That is the "the screen darkens or brightens when I enter some areas" report, and it
            // is not a meter doing its job: a real one would already have been exposed for this
            // scene before the fade lifted. Snap on the first reading in a new location, behind
            // the game's own warp fade, and ease only for changes that happen while you are there.
            screen.ExposureTarget = target;
            if (!ReferenceEquals(Game1.currentLocation, screen.ExposureMeterLocation))
            {
                screen.ExposureMeterLocation = Game1.currentLocation;
                screen.MeteredExposure = target;
            }
        }

        /// <summary>The average brightness the meter aims the scene at, as a fraction of white,
        /// and the guard rails either side of what it may ask for. Half-lit is the middle grey a
        /// light meter is built around; the rails keep a correction to a gentle one, because this
        /// number multiplies the whole picture.</summary>
        private const float ExposureKey = 0.5f;
        private const float ExposureDarkestMetered = 0.05f;
        private const float ExposureDimmestAllowed = 0.7f;
        private const float ExposureBrightestAllowed = 1.15f;

        /// <summary>How much of the distance to its target the exposure gives up in one step, as a
        /// per-frame fraction at sixty frames a second, put through <see cref="Approach"/> so it
        /// means the same at any frame rate. This is the rate the meter has always had: it used to
        /// give up a twenty-fifth of the distance on every fourth frame, which is this on every
        /// frame, and about a second and a half to travel.</summary>
        private const float MeteredExposureEaseRate = 0.0101f;

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
            string locationName = location?.Name ?? "";
            if (locationName.Contains("Beach") || locationName.Contains("Island") || locationName == "Docks")
                return 1f;
            // Beach Farm answers to none of the above and is the ocean anyway: its class is Farm,
            // its name is "Farm", and it is not an island — so the swell stopped at the property
            // line and the same sea that rolls a hundred tiles east lay flat here. The MAP it was
            // built from is the tell, and reading that also covers the farm layouts mods derive
            // from Farm_Beach, which no class or location name could ever have caught.
            string mapPath = location?.mapPath?.Value ?? "";
            if (mapPath.Contains("Beach") || mapPath.Contains("Island"))
                return 1f;
            return 0f;
        }

        /// <summary>Weather/season multipliers for ripple strength, speed, and sparkle.</summary>
        private static void ComputeWaterDynamics(out float strength, out float speed, out float sparkle,
            out float caustic)
        {
            strength = 1f; speed = 1f; sparkle = 1f; caustic = 1f;

            // Caustics are FOCUSED sunlight: everything that scatters the sun scatters them
            // harder than it scatters a glint, and a churned storm surface focuses nothing.
            if (LocalSky.IsLightning) { strength *= 2.0f; speed *= 1.7f; sparkle *= 0.25f; caustic *= 0.25f; }   // storm
            else if (LocalSky.IsRaining) { strength *= 1.5f; speed *= 1.4f; sparkle *= 0.4f; caustic *= 0.4f; } // rain: choppy, no sun glints
            if (LocalSky.IsSnowing) { strength *= 0.8f; speed *= 0.7f; sparkle *= 0.5f; caustic *= 0.5f; }       // sluggish, overcast

            if (LocalSky.Season == Season.Winter) { speed *= 0.8f; sparkle *= 0.8f; caustic *= 0.8f; }           // cold, calmer
            else if (LocalSky.Season == Season.Summer) sparkle *= 1.2f;                          // bright sun, more glint
        }
    }
}
