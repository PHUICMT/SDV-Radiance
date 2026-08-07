using System;
using SDVRadiance.Integrations;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Generic Mod Config Menu registration: the landing page (master switch + look preset)
    /// and one page per effect, in the same order as the F6 tuner. Registered once from
    /// ModEntry.OnGameLaunched; a missing GMCM just means config.json editing only.
    /// </summary>
    internal static class GmcmRegistration
    {
        /// <param name="config">Live config accessor (the instance is replaced on GMCM reset).</param>
        /// <param name="replaceConfig">Swap in a fresh config instance (GMCM "reset to defaults").</param>
        /// <param name="refreshForceBufferDraw">Re-sync <see cref="HarmonyPatcher.ForceBufferDraw"/> with live config.</param>
        internal static void Register(IModHelper helper, IManifest manifest, IMonitor monitor,
            Func<string, string> i18n, Func<ModConfig> config, Action<ModConfig> replaceConfig,
            Action refreshForceBufferDraw)
        {
            var api = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api is null)
            {
                monitor.Log("GMCM not installed; config editable via config.json only.", LogLevel.Trace);
                return;
            }

            void Save()
            {
                config().Clamp();
                refreshForceBufferDraw();
                helper.WriteConfig(config());
            }

            api.Register(manifest, () =>
            {
                monitor.Log("Config reset to defaults via GMCM.", LogLevel.Debug);
                replaceConfig(new ModConfig());
                refreshForceBufferDraw();
            }, Save);

            // --- Landing page: master switch, a one-click look preset, and links to each
            // effect's own page so the top level stays short instead of one giant scroll. ---
            api.AddBoolOption(manifest, () => config().Enabled, v => config().Enabled = v,
                () => i18n("config.enabled.name"), () => i18n("config.enabled.tooltip"));

            api.AddTextOption(manifest,
                () => config().ActivePreset.ToString(),
                v =>
                {
                    if (Enum.TryParse<LookPreset>(v, out var p))
                    {
                        // GMCM re-fires every option setter on save; only re-stamp the preset
                        // when the dropdown actually changed, or it silently overwrites the
                        // individual settings tuned on the other pages ("my settings reset").
                        bool changed = p != config().ActivePreset;
                        config().ActivePreset = p;
                        if (changed && p != LookPreset.Custom)
                        {
                            monitor.Log($"Preset applied via GMCM: {p}", LogLevel.Debug);
                            config().ApplyPreset(p);
                        }
                        refreshForceBufferDraw();
                    }
                },
                () => i18n("config.preset.name"), () => i18n("config.preset.tooltip"),
                new[] { nameof(LookPreset.Custom), nameof(LookPreset.Subtle), nameof(LookPreset.Cinematic), nameof(LookPreset.Vibrant), nameof(LookPreset.Off) },
                v => i18n($"config.preset.{v.ToLowerInvariant()}"));

            api.AddParagraph(manifest, () => i18n("config.preset.hint"));

            // Same order as the F6 tuner: how it runs first (the one setting every player has
            // an opinion about), then camera/film, then light, then the world, then the
            // troubleshooting page.
            api.AddPageLink(manifest, "perf", () => i18n("config.section.perf"));
            api.AddPageLink(manifest, "colorgrade", () => i18n("config.section.colorgrade"));
            api.AddPageLink(manifest, "bloom", () => i18n("config.section.bloom"));
            api.AddPageLink(manifest, "lens", () => i18n("config.section.lens"));
            api.AddPageLink(manifest, "lighting", () => i18n("config.section.lighting"));
            api.AddPageLink(manifest, "shadows", () => i18n("config.section.shadows"));
            api.AddPageLink(manifest, "godrays", () => i18n("config.section.godrays"));
            api.AddPageLink(manifest, "water", () => i18n("config.section.water"));
            api.AddPageLink(manifest, "cloudshadow", () => i18n("config.section.cloudshadow"));
            api.AddPageLink(manifest, "fog", () => i18n("config.section.fog"));
            api.AddPageLink(manifest, "camera", () => i18n("config.section.camera"));
            api.AddPageLink(manifest, "misc", () => i18n("config.section.misc"));

            // --- Bloom (implemented) ---
            api.AddPage(manifest, "bloom", () => i18n("config.section.bloom"));
            api.AddBoolOption(manifest, () => config().BloomEnabled, v => config().BloomEnabled = v,
                () => i18n("config.bloom.enabled.name"), () => i18n("config.bloom.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().BloomThreshold, v => config().BloomThreshold = v,
                () => i18n("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().BloomIntensity, v => config().BloomIntensity = v,
                () => i18n("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);

            // --- Color grading (implemented) ---
            api.AddPage(manifest, "colorgrade", () => i18n("config.section.colorgrade"));
            api.AddBoolOption(manifest, () => config().ColorGradeEnabled, v => config().ColorGradeEnabled = v,
                () => i18n("config.colorgrade.enabled.name"), () => i18n("config.colorgrade.enabled.tooltip"));
            api.AddBoolOption(manifest, () => config().ColorGradeAuto, v => config().ColorGradeAuto = v,
                () => i18n("config.colorgrade.auto.name"), () => i18n("config.colorgrade.auto.tooltip"));
            api.AddNumberOption(manifest, () => config().ColorGradeStrength, v => config().ColorGradeStrength = v,
                () => i18n("config.colorgrade.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().ColorGradeContrast, v => config().ColorGradeContrast = v,
                () => i18n("config.colorgrade.contrast.name"), null, 0.5f, 1.5f, 0.05f);
            api.AddNumberOption(manifest, () => config().ColorGradeSaturation, v => config().ColorGradeSaturation = v,
                () => i18n("config.colorgrade.saturation.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().ColorGradeTemperature, v => config().ColorGradeTemperature = v,
                () => i18n("config.colorgrade.temperature.name"), () => i18n("config.colorgrade.temperature.tooltip"), -1f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().ColorGradeBrightness, v => config().ColorGradeBrightness = v,
                () => i18n("config.colorgrade.brightness.name"), null, 0.5f, 1.5f, 0.05f);
            api.AddBoolOption(manifest, () => config().ColorGradeToneMap, v => config().ColorGradeToneMap = v,
                () => i18n("config.colorgrade.tonemap.name"), () => i18n("config.colorgrade.tonemap.tooltip"));
            api.AddNumberOption(manifest, () => config().BlueLightFilter, v => config().BlueLightFilter = v,
                () => i18n("config.colorgrade.bluelight.name"), () => i18n("config.colorgrade.bluelight.tooltip"), 0f, 1f, 0.05f);

            // --- God rays (implemented) ---
            api.AddPage(manifest, "godrays", () => i18n("config.section.godrays"));
            api.AddBoolOption(manifest, () => config().GodRaysEnabled, v => config().GodRaysEnabled = v,
                () => i18n("config.godrays.enabled.name"), () => i18n("config.godrays.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().GodRaysIntensity, v => config().GodRaysIntensity = v,
                () => i18n("config.godrays.intensity.name"), null, 0f, 1.5f, 0.05f);
            api.AddNumberOption(manifest, () => config().GodRaysThreshold, v => config().GodRaysThreshold = v,
                () => i18n("config.godrays.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().GodRaysDensity, v => config().GodRaysDensity = v,
                () => i18n("config.godrays.density.name"), null, 0.1f, 1f, 0.05f);

            // --- Volumetric fog (implemented) ---
            api.AddPage(manifest, "fog", () => i18n("config.section.fog"));
            api.AddSectionTitle(manifest, () => i18n("config.fog.sectionday"));
            api.AddBoolOption(manifest, () => config().FogEnabled, v => config().FogEnabled = v,
                () => i18n("config.fog.enabled.name"), () => i18n("config.fog.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().FogCoverage, v => config().FogCoverage = v,
                () => i18n("config.fog.coverage.name"), () => i18n("config.fog.coverage.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FogDensity, v => config().FogDensity = v,
                () => i18n("config.fog.density.name"), () => i18n("config.fog.density.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FogScale, v => config().FogScale = v,
                () => i18n("config.fog.scale.name"), null, 1f, 8f, 0.5f);
            api.AddNumberOption(manifest, () => config().FogSpeed, v => config().FogSpeed = v,
                () => i18n("config.fog.speed.name"), null, 0f, 0.1f, 0.005f);
            api.AddSectionTitle(manifest, () => i18n("config.fog.sectionnight"));
            api.AddBoolOption(manifest, () => config().FogNightMist, v => config().FogNightMist = v,
                () => i18n("config.fog.nightmist.name"), () => i18n("config.fog.nightmist.tooltip"));
            api.AddNumberOption(manifest, () => config().FogNightMistCoverage, v => config().FogNightMistCoverage = v,
                () => i18n("config.fog.nightmistcoverage.name"), () => i18n("config.fog.coverage.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FogNightMistDensity, v => config().FogNightMistDensity = v,
                () => i18n("config.fog.nightmistdensity.name"), () => i18n("config.fog.density.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FogNightMistSpeed, v => config().FogNightMistSpeed = v,
                () => i18n("config.fog.nightmistspeed.name"), null, 0f, 0.1f, 0.002f);

            // --- Cloud shadows (implemented) ---
            api.AddPage(manifest, "cloudshadow", () => i18n("config.section.cloudshadow"));
            api.AddBoolOption(manifest, () => config().SuppressVanillaCloudShadow, v => config().SuppressVanillaCloudShadow = v,
                () => i18n("config.cloudshadow.hidevanilla.name"), () => i18n("config.cloudshadow.hidevanilla.tooltip"));
            api.AddBoolOption(manifest, () => config().CloudShadowEnabled, v => config().CloudShadowEnabled = v,
                () => i18n("config.cloudshadow.enabled.name"), () => i18n("config.cloudshadow.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().CloudShadowCoverage, v => config().CloudShadowCoverage = v,
                () => i18n("config.cloudshadow.coverage.name"), null, 0.1f, 0.9f, 0.05f);
            api.AddNumberOption(manifest, () => config().CloudShadowCount, v => config().CloudShadowCount = v,
                () => i18n("config.cloudshadow.count.name"), () => i18n("config.cloudshadow.count.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().CloudShadowOpacity, v => config().CloudShadowOpacity = v,
                () => i18n("config.cloudshadow.opacity.name"), null, 0f, 0.7f, 0.05f);
            api.AddNumberOption(manifest, () => config().CloudShadowScale, v => config().CloudShadowScale = v,
                () => i18n("config.cloudshadow.scale.name"), null, 1f, 5f, 0.5f);
            api.AddNumberOption(manifest, () => config().CloudShadowSpeed, v => config().CloudShadowSpeed = v,
                () => i18n("config.cloudshadow.speed.name"), null, 0f, 0.1f, 0.005f);

            // --- Lens: the camera-glass effects, grouped as the F6 tuner groups them ---
            api.AddPage(manifest, "lens", () => i18n("config.section.lens"));
            api.AddBoolOption(manifest, () => config().TiltShiftEnabled, v => config().TiltShiftEnabled = v,
                () => i18n("config.tiltshift.enabled.name"), () => i18n("config.tiltshift.enabled.tooltip"));
            api.AddTextOption(manifest,
                () => config().TiltShiftMode.ToString(),
                v => config().TiltShiftMode = Enum.TryParse<TiltShiftFocus>(v, out var m) ? m : TiltShiftFocus.Bands,
                () => i18n("config.tiltshift.mode.name"), () => i18n("config.tiltshift.mode.tooltip"),
                new[] { nameof(TiltShiftFocus.Bands), nameof(TiltShiftFocus.Radial) },
                v => i18n($"config.tiltshift.mode.{v.ToLowerInvariant()}"));
            api.AddNumberOption(manifest, () => config().TiltShiftStrength, v => config().TiltShiftStrength = v,
                () => i18n("config.tiltshift.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().TiltShiftRadius, v => config().TiltShiftRadius = v,
                () => i18n("config.tiltshift.radius.name"), () => i18n("config.tiltshift.radius.tooltip"), 0.05f, 0.9f, 0.05f);
            api.AddNumberOption(manifest, () => config().TiltShiftFeather, v => config().TiltShiftFeather = v,
                () => i18n("config.tiltshift.feather.name"), () => i18n("config.tiltshift.feather.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().TiltShiftTopRatio, v => config().TiltShiftTopRatio = v,
                () => i18n("config.tiltshift.top.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().TiltShiftBottomRatio, v => config().TiltShiftBottomRatio = v,
                () => i18n("config.tiltshift.bottom.name"), null, 0f, 1f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.section.finishing"));
            api.AddBoolOption(manifest, () => config().VignetteEnabled, v => config().VignetteEnabled = v,
                () => i18n("config.vignette.enabled.name"), () => i18n("config.vignette.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().VignetteStrength, v => config().VignetteStrength = v,
                () => i18n("config.vignette.strength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().ChromaticAberrationEnabled, v => config().ChromaticAberrationEnabled = v,
                () => i18n("config.ca.enabled.name"), () => i18n("config.ca.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().ChromaticAberrationStrength, v => config().ChromaticAberrationStrength = v,
                () => i18n("config.ca.strength.name"), null, 0f, 1f, 0.05f);

            // --- Water (implemented) ---
            api.AddPage(manifest, "water", () => i18n("config.section.water"));
            api.AddBoolOption(manifest, () => config().WaterEnabled, v => config().WaterEnabled = v,
                () => i18n("config.water.enabled.name"), () => i18n("config.water.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().WaterStrength, v => config().WaterStrength = v,
                () => i18n("config.water.strength.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterSpeed, v => config().WaterSpeed = v,
                () => i18n("config.water.speed.name"), null, 0f, 3f, 0.1f);
            api.AddNumberOption(manifest, () => config().WaterSparkle, v => config().WaterSparkle = v,
                () => i18n("config.water.sparkle.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterSparkleDensity, v => config().WaterSparkleDensity = v,
                () => i18n("config.water.sparkledensity.name"), () => i18n("config.water.sparkledensity.tooltip"), 0.2f, 2f, 0.05f);
            api.AddBoolOption(manifest, () => config().WaterReflection, v => config().WaterReflection = v,
                () => i18n("config.water.reflection.name"), () => i18n("config.water.reflection.tooltip"));
            api.AddNumberOption(manifest, () => config().WaterReflectStrength, v => config().WaterReflectStrength = v,
                () => i18n("config.water.reflectstrength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().WaterEffectIndoors, v => config().WaterEffectIndoors = v,
                () => i18n("config.water.indoors.name"), () => i18n("config.water.indoors.tooltip"));

            // --- Dynamic lighting (implemented) ---
            api.AddPage(manifest, "lighting", () => i18n("config.section.lighting"));
            api.AddBoolOption(manifest, () => config().FloodLightingEnabled, v => config().FloodLightingEnabled = v,
                () => i18n("config.lighting.flood.name"), () => i18n("config.lighting.flood.tooltip"));
            api.AddNumberOption(manifest, () => config().FloodLightingStrength, v => config().FloodLightingStrength = v,
                () => i18n("config.lighting.floodstrength.name"), () => i18n("config.lighting.floodstrength.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FloodShadowStrength, v => config().FloodShadowStrength = v,
                () => i18n("config.lighting.floodshadow.name"), () => i18n("config.lighting.floodshadow.tooltip"), 0f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().LightingEnabled, v => config().LightingEnabled = v,
                () => i18n("config.lighting.enabled.name"), () => i18n("config.lighting.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().LightingIndoorDarkness, v => config().LightingIndoorDarkness = v,
                () => i18n("config.lighting.indoor.name"), () => i18n("config.lighting.indoor.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingNightDarkness, v => config().LightingNightDarkness = v,
                () => i18n("config.lighting.night.name"), () => i18n("config.lighting.night.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingWarmth, v => config().LightingWarmth = v,
                () => i18n("config.lighting.warmth.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingBoost, v => config().LightingBoost = v,
                () => i18n("config.lighting.boost.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingRadiusScale, v => config().LightingRadiusScale = v,
                () => i18n("config.lighting.radius.name"), null, 0.2f, 3f, 0.1f);
            api.AddBoolOption(manifest, () => config().LightingShadows, v => config().LightingShadows = v,
                () => i18n("config.lighting.shadows.name"), () => i18n("config.lighting.shadows.tooltip"));
            api.AddNumberOption(manifest, () => config().LightingShadowStrength, v => config().LightingShadowStrength = v,
                () => i18n("config.lighting.shadowstrength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().WindowEffectsEnabled, v => config().WindowEffectsEnabled = v,
                () => i18n("config.lighting.windoweffects.name"), () => i18n("config.lighting.windoweffects.tooltip"));
            api.AddBoolOption(manifest, () => config().WindowBeamEnabled, v => config().WindowBeamEnabled = v,
                () => i18n("config.lighting.windowbeam.name"), () => i18n("config.lighting.windowbeam.tooltip"));
            api.AddBoolOption(manifest, () => config().WindowRoomLightEnabled, v => config().WindowRoomLightEnabled = v,
                () => i18n("config.lighting.windowroomlight.name"), () => i18n("config.lighting.windowroomlight.tooltip"));

            // --- Directional sprite shadows ---
            api.AddPage(manifest, "shadows", () => i18n("config.section.shadows"));
            api.AddBoolOption(manifest, () => config().DirectionalShadowsEnabled, v => config().DirectionalShadowsEnabled = v,
                () => i18n("config.shadows.enabled.name"), () => i18n("config.shadows.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().DirectionalShadowStrength, v => config().DirectionalShadowStrength = v,
                () => i18n("config.shadows.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().DirectionalShadowLength, v => config().DirectionalShadowLength = v,
                () => i18n("config.shadows.length.name"), null, 0.2f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().DirectionalShadowBlur, v => config().DirectionalShadowBlur = v,
                () => i18n("config.shadows.blur.name"), null, 0f, 5f, 0.5f);
            api.AddBoolOption(manifest, () => config().DirectionalShadowObjects, v => config().DirectionalShadowObjects = v,
                () => i18n("config.shadows.objects.name"), () => i18n("config.shadows.objects.tooltip"));

            // --- Camera (implemented) ---
            api.AddPage(manifest, "camera", () => i18n("config.section.camera"));
            api.AddTextOption(manifest,
                () => config().CameraMode.ToString(),
                v => config().CameraMode = Enum.TryParse<CameraMode>(v, out var m) ? m : CameraMode.Off,
                () => i18n("config.camera.mode.name"), () => i18n("config.camera.mode.tooltip"),
                new[] { nameof(CameraMode.Off), nameof(CameraMode.Smooth) },
                v => i18n($"config.camera.mode.{v.ToLowerInvariant()}"));
            api.AddNumberOption(manifest, () => config().CameraFollowSpeed, v => config().CameraFollowSpeed = v,
                () => i18n("config.smoothcam.speed.name"), () => i18n("config.smoothcam.speed.tooltip"), 0.05f, 1f, 0.05f);

            // --- Performance page: what the picture costs, kept away from the look settings ---
            api.AddPage(manifest, "perf", () => i18n("config.section.perf"));
            api.AddNumberOption(manifest, () => config().RenderScale, v => config().RenderScale = v,
                () => i18n("config.renderscale.name"), () => i18n("config.renderscale.tooltip"), 0.5f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().RenderSharpness, v => config().RenderSharpness = v,
                () => i18n("config.rendersharpness.name"), () => i18n("config.rendersharpness.tooltip"), 0f, 2f, 0.1f);

            // --- Misc page: hotkeys + diagnostics + roadmap ---
            api.AddPage(manifest, "misc", () => i18n("config.section.misc"));
            api.AddSectionTitle(manifest, () => i18n("config.section.hotkeys"));
            api.AddKeybindList(manifest, () => config().ToggleKey, v => config().ToggleKey = v,
                () => i18n("config.togglekey.name"), () => i18n("config.togglekey.tooltip"));
            api.AddKeybindList(manifest, () => config().TunerKey, v => config().TunerKey = v,
                () => i18n("config.tunerkey.name"), () => i18n("config.tunerkey.tooltip"));

            // --- Diagnostics ---
            api.AddSectionTitle(manifest, () => i18n("config.section.debug"));
            api.AddBoolOption(manifest, () => config().DebugLogging, v => config().DebugLogging = v,
                () => i18n("config.debug.name"), () => i18n("config.debug.tooltip"));

            // --- Not yet implemented: shown as a roadmap so options don't imply working features ---
            api.AddSectionTitle(manifest, () => i18n("config.section.wip"));
            api.AddParagraph(manifest, () => i18n("config.wip.text"));
        }
    }
}
