using System;
using System.Collections.Generic;
using System.Linq;
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
            Func<string, string> translate, Func<ModConfig> config, Action<ModConfig> replaceConfig,
            Action refreshForceBufferDraw, Func<RenderPipeline?> getPipeline)
        {
            var configMenu = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null)
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

            configMenu.Register(manifest, () =>
            {
                monitor.Log("Config reset to defaults via GMCM.", LogLevel.Debug);
                replaceConfig(new ModConfig { ConfigVersion = ModConfig.CurrentConfigVersion });
                refreshForceBufferDraw();
            }, Save);

            RegisterLandingPage(configMenu, manifest, translate, config, monitor, refreshForceBufferDraw);
            RegisterBloomPage(configMenu, manifest, translate, config);
            RegisterColourGradePage(configMenu, manifest, translate, config, LutCatalog.Discover());
            RegisterGodRaysPage(configMenu, manifest, translate, config);
            RegisterFogPage(configMenu, manifest, translate, config);
            RegisterWeatherPage(configMenu, manifest, translate, config);
            RegisterParticlesPage(configMenu, manifest, translate, config);
            RegisterCloudShadowPage(configMenu, manifest, translate, config);
            RegisterLensPage(configMenu, manifest, translate, config);
            RegisterWaterPage(configMenu, manifest, translate, config);
            RegisterLightingPage(configMenu, manifest, translate, config);
            RegisterWindowsPage(configMenu, manifest, translate, config);
            RegisterShadowsPage(configMenu, manifest, translate, config);
            RegisterCameraPage(configMenu, manifest, translate, config);
            RegisterSmoothingPage(configMenu, manifest, translate, config);
            RegisterPerformancePage(configMenu, manifest, translate, config);
            RegisterMiscPage(configMenu, manifest, translate, config, monitor, getPipeline);
        }

        /// <summary>Master switch, the one-click look preset, and the links to every other page.</summary>
        private static void RegisterLandingPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config, IMonitor monitor, Action refreshForceBufferDraw)
        {
            // --- Landing page: master switch, a one-click look preset, and links to each
            // effect's own page so the top level stays short instead of one giant scroll. ---
            configMenu.AddBoolOption(manifest, () => config().Enabled, value => config().Enabled = value,
                () => translate("config.enabled.name"), () => translate("config.enabled.tooltip"));

            configMenu.AddTextOption(manifest,
                () => config().ActivePreset.ToString(),
                value =>
                {
                    if (Enum.TryParse<LookPreset>(value, out var preset))
                    {
                        // GMCM re-fires every option setter on save; only re-stamp the preset
                        // when the dropdown actually changed, or it silently overwrites the
                        // individual settings tuned on the other pages ("my settings reset").
                        bool changed = preset != config().ActivePreset;
                        config().ActivePreset = preset;
                        if (changed && preset != LookPreset.Custom)
                        {
                            monitor.Log($"Preset applied via GMCM: {preset}", LogLevel.Debug);
                            config().ApplyPreset(preset);
                        }
                        refreshForceBufferDraw();
                    }
                },
                () => translate("config.preset.name"), () => translate("config.preset.tooltip"),
                [nameof(LookPreset.Custom), nameof(LookPreset.Subtle), nameof(LookPreset.Cinematic), nameof(LookPreset.Vibrant),
                 nameof(LookPreset.Nocturne), nameof(LookPreset.Off)],
                choice => translate($"config.preset.{choice.ToLowerInvariant()}"));

            configMenu.AddParagraph(manifest, () => translate("config.preset.hint"));

            // Same order as the F6 tuner: how it runs first (the one setting every player has
            // an opinion about), then camera/film, then light, then the world, then the
            // troubleshooting page.
            configMenu.AddPageLink(manifest, "perf", () => translate("config.section.perf"));
            configMenu.AddPageLink(manifest, "colorgrade", () => translate("config.section.colorgrade"));
            configMenu.AddPageLink(manifest, "bloom", () => translate("config.section.bloom"));
            configMenu.AddPageLink(manifest, "lens", () => translate("config.section.lens"));
            configMenu.AddPageLink(manifest, "smoothing", () => translate("tuner.tab.smoothing"));
            configMenu.AddPageLink(manifest, "lighting", () => translate("config.section.lighting"));
            configMenu.AddPageLink(manifest, "windows", () => translate("config.section.windows"));
            configMenu.AddPageLink(manifest, "shadows", () => translate("config.section.shadows"));
            configMenu.AddPageLink(manifest, "godrays", () => translate("config.section.godrays"));
            configMenu.AddPageLink(manifest, "water", () => translate("config.section.water"));
            configMenu.AddPageLink(manifest, "cloudshadow", () => translate("config.section.cloudshadow"));
            configMenu.AddPageLink(manifest, "fog", () => translate("config.section.fog"));
            configMenu.AddPageLink(manifest, "weather", () => translate("config.section.weather"));
            configMenu.AddPageLink(manifest, "particles", () => translate("config.section.particles"));
            configMenu.AddPageLink(manifest, "camera", () => translate("config.section.camera"));
            configMenu.AddPageLink(manifest, "misc", () => translate("config.section.misc"));

            // --- Bloom (implemented) ---
        }

        /// <summary>Bloom.</summary>
        private static void RegisterBloomPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "bloom", () => translate("config.section.bloom"));
            configMenu.AddBoolOption(manifest, () => config().BloomEnabled, value => config().BloomEnabled = value,
                () => translate("config.bloom.enabled.name"), () => translate("config.bloom.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().BloomThreshold, value => config().BloomThreshold = value,
                () => translate("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().BloomIntensity, value => config().BloomIntensity = value,
                () => translate("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().BloomEmissiveBoost, value => config().BloomEmissiveBoost = value,
                () => translate("config.bloom.emissiveboost.name"),
                () => translate("config.bloom.emissiveboost.tooltip"), 0f, 1f, 0.05f);

            // --- Color grading (implemented) ---
        }

        /// <summary>Colour grading, tonemapping and the blue-light filter.</summary>
        /// <param name="userLuts">Looks found in assets/luts that did not ship with the mod. Empty
        /// for almost everyone, and when it is empty the dropdown is exactly what it always was.</param>
        private static void RegisterColourGradePage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config, string[] userLuts)
        {
            configMenu.AddPage(manifest, "colorgrade", () => translate("config.section.colorgrade"));
            configMenu.AddBoolOption(manifest, () => config().ColorGradeEnabled, value => config().ColorGradeEnabled = value,
                () => translate("config.colorgrade.enabled.name"), () => translate("config.colorgrade.enabled.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().ColorGradeAuto, value => config().ColorGradeAuto = value,
                () => translate("config.colorgrade.auto.name"), () => translate("config.colorgrade.auto.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().ColorGradeStrength, value => config().ColorGradeStrength = value,
                () => translate("config.colorgrade.strength.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ColorGradeContrast, value => config().ColorGradeContrast = value,
                () => translate("config.colorgrade.contrast.name"), null, 0.5f, 1.5f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ColorGradeSaturation, value => config().ColorGradeSaturation = value,
                () => translate("config.colorgrade.saturation.name"), null, 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ColorGradeTemperature, value => config().ColorGradeTemperature = value,
                () => translate("config.colorgrade.temperature.name"), () => translate("config.colorgrade.temperature.tooltip"), -1f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ColorGradeBrightness, value => config().ColorGradeBrightness = value,
                () => translate("config.colorgrade.brightness.name"), null, 0.5f, 1.5f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().ColorGradeToneMap, value => config().ColorGradeToneMap = value,
                () => translate("config.colorgrade.tonemap.name"), () => translate("config.colorgrade.tonemap.tooltip"));
            // A LOOK, on top of the sliders rather than instead of them. The list is the files
            // that ship in assets/luts; anyone who drops their own PNG in there can name it in
            // config.json, which the dropdown cannot offer but the shader loads all the same.
            // The shipped looks, then anything the player put in the folder themselves, then -
            // only if it is still missing - whatever config.json currently names. That last case
            // is a look whose file has been deleted or renamed: leaving it out of the list makes
            // GMCM snap the setting to the first entry the moment the page is opened, changing a
            // player's picture because a file was moved.
            string current = config().ColorGradeLut ?? "";
            var choices = new List<string>(ModConfig.ShippedLuts);
            choices.AddRange(userLuts);
            if (!choices.Contains(current, StringComparer.OrdinalIgnoreCase))
                choices.Add(current);
            var userLutNames = new HashSet<string>(userLuts, StringComparer.OrdinalIgnoreCase);
            configMenu.AddTextOption(manifest,
                () => config().ColorGradeLut,
                value => config().ColorGradeLut = value ?? "",
                () => translate("config.colorgrade.lut.name"), () => translate("config.colorgrade.lut.tooltip"),
                [.. choices],
                choice => choice.Length == 0 ? translate("config.colorgrade.lut.none")
                     : userLutNames.Contains(choice) ? $"{choice} ({translate("config.colorgrade.lut.yours")})"
                     : Array.IndexOf(ModConfig.ShippedLuts, choice) >= 0 ? translate($"config.colorgrade.lut.{choice}")
                     : $"{choice} ({translate("config.colorgrade.lut.missing")})");
            configMenu.AddNumberOption(manifest, () => config().ColorGradeLutAmount, value => config().ColorGradeLutAmount = value,
                () => translate("config.colorgrade.lutamount.name"), () => translate("config.colorgrade.lutamount.tooltip"), 0f, 1f, 0.05f);

            configMenu.AddNumberOption(manifest, () => config().BlueLightFilter, value => config().BlueLightFilter = value,
                () => translate("config.colorgrade.bluelight.name"), () => translate("config.colorgrade.bluelight.tooltip"), 0f, 1f, 0.05f);

            // --- God rays (implemented) ---
        }

        /// <summary>God rays. Ships off by default.</summary>
        private static void RegisterGodRaysPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "godrays", () => translate("config.section.godrays"));
            configMenu.AddSectionTitle(manifest, () => translate("config.godrays.sectionlamps"));
            configMenu.AddBoolOption(manifest, () => config().GodRaysEnabled, value => config().GodRaysEnabled = value,
                () => translate("config.godrays.enabled.name"), () => translate("config.godrays.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().GodRaysIntensity, value => config().GodRaysIntensity = value,
                () => translate("config.godrays.intensity.name"), null, 0f, 2f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.godrays.sectionsun"));
            configMenu.AddBoolOption(manifest, () => config().GodRaysSun, value => config().GodRaysSun = value,
                () => translate("config.godrays.sun.name"), () => translate("config.godrays.sun.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().GodRaysSunIntensity, value => config().GodRaysSunIntensity = value,
                () => translate("config.godrays.sunintensity.name"), () => translate("config.godrays.sunintensity.tooltip"), 0f, 1.5f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().GodRaysSunReach, value => config().GodRaysSunReach = value,
                () => translate("config.godrays.sunreach.name"), () => translate("config.godrays.sunreach.tooltip"), 0.1f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().GodRaysSunGlassRoof, value => config().GodRaysSunGlassRoof = value,
                () => translate("config.godrays.sunglassroof.name"), () => translate("config.godrays.sunglassroof.tooltip"));

            // --- Volumetric fog (implemented) ---
        }

        /// <summary>Fog, and the separate night mist that runs on the same machinery.</summary>
        private static void RegisterFogPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "fog", () => translate("config.section.fog"));
            configMenu.AddSectionTitle(manifest, () => translate("config.fog.sectionday"));
            configMenu.AddBoolOption(manifest, () => config().FogEnabled, value => config().FogEnabled = value,
                () => translate("config.fog.enabled.name"), () => translate("config.fog.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().FogCoverage, value => config().FogCoverage = value,
                () => translate("config.fog.coverage.name"), () => translate("config.fog.coverage.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FogDensity, value => config().FogDensity = value,
                () => translate("config.fog.density.name"), () => translate("config.fog.density.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FogScale, value => config().FogScale = value,
                () => translate("config.fog.scale.name"), null, 1f, 8f, 0.5f);
            configMenu.AddNumberOption(manifest, () => config().FogSpeed, value => config().FogSpeed = value,
                () => translate("config.fog.speed.name"), null, 0f, 0.1f, 0.005f);
            configMenu.AddSectionTitle(manifest, () => translate("config.fog.sectionnight"));
            configMenu.AddBoolOption(manifest, () => config().FogNightMist, value => config().FogNightMist = value,
                () => translate("config.fog.nightmist.name"), () => translate("config.fog.nightmist.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().FogNightMistCoverage, value => config().FogNightMistCoverage = value,
                () => translate("config.fog.nightmistcoverage.name"), () => translate("config.fog.coverage.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FogNightMistDensity, value => config().FogNightMistDensity = value,
                () => translate("config.fog.nightmistdensity.name"), () => translate("config.fog.density.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().MineFogMist, value => config().MineFogMist = value,
                () => translate("config.fog.minemist.name"), () => translate("config.fog.minemist.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FogNightMistLampGlow, value => config().FogNightMistLampGlow = value,
                () => translate("config.fog.nightmistlampglow.name"), () => translate("config.fog.nightmistlampglow.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.fog.sectionboth"));
            configMenu.AddNumberOption(manifest, () => config().FogTopBias, value => config().FogTopBias = value,
                () => translate("config.fog.topbias.name"), () => translate("config.fog.topbias.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FogNightMistSpeed, value => config().FogNightMistSpeed = value,
                () => translate("config.fog.nightmistspeed.name"), null, 0f, 0.1f, 0.002f);
            configMenu.AddSectionTitle(manifest, () => translate("config.heathaze.name"));
            configMenu.AddBoolOption(manifest, () => config().HeatHazeEnabled, value => config().HeatHazeEnabled = value,
                () => translate("config.heathaze.name"), () => translate("config.heathaze.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().HeatHazeStrength, value => config().HeatHazeStrength = value,
                () => translate("config.heathaze.strength.name"), () => translate("config.heathaze.strength.tooltip"), 0f, 2f, 0.05f);

            // --- Cloud shadows (implemented) ---
        }

        /// <summary>Weather: the replacement rain and snow, drawn in the game's own weather slot.</summary>
        private static void RegisterWeatherPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "weather", () => translate("config.section.weather"));
            configMenu.AddNumberOption(manifest, () => config().SnowGlintStrength, value => config().SnowGlintStrength = value,
                () => translate("config.weather.snowglint.name"), () => translate("config.weather.snowglint.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().AuroraEnabled, value => config().AuroraEnabled = value,
                () => translate("config.weather.aurora.name"), () => translate("config.weather.aurora.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().AuroraStrength, value => config().AuroraStrength = value,
                () => translate("config.weather.aurorastrength.name"), () => translate("config.weather.aurorastrength.tooltip"), 0f, 2f, 0.1f);
            configMenu.AddBoolOption(manifest, () => config().ShootingStarsEnabled, value => config().ShootingStarsEnabled = value,
                () => translate("config.weather.shootingstars.name"), () => translate("config.weather.shootingstars.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().FoliageSwayEnabled, value => config().FoliageSwayEnabled = value,
                () => translate("config.weather.foliagesway.name"), () => translate("config.weather.foliagesway.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().FoliageSwayStrength, value => config().FoliageSwayStrength = value,
                () => translate("config.weather.foliageswaystrength.name"), () => translate("config.weather.foliageswaystrength.tooltip"), 0f, 2f, 0.1f);
            configMenu.AddNumberOption(manifest, () => config().FoliageSwaySpeed, value => config().FoliageSwaySpeed = value,
                () => translate("config.weather.foliageswayspeed.name"), () => translate("config.weather.foliageswayspeed.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FoliageSwayGustSpan, value => config().FoliageSwayGustSpan = value,
                () => translate("config.weather.foliageswaygustspan.name"), () => translate("config.weather.foliageswaygustspan.tooltip"), 4f, 40f, 1f);
            configMenu.AddBoolOption(manifest, () => config().FoliageSwayCrops, value => config().FoliageSwayCrops = value,
                () => translate("config.weather.foliageswaycrops.name"), () => translate("config.weather.foliageswaycrops.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().FoliageSwayGrass, value => config().FoliageSwayGrass = value,
                () => translate("config.weather.foliageswaygrass.name"), () => translate("config.weather.foliageswaygrass.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().GrassSmoothShake, value => config().GrassSmoothShake = value,
                () => translate("config.weather.grasssmoothshake.name"), () => translate("config.weather.grasssmoothshake.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().PrecipitationEnabled, value => config().PrecipitationEnabled = value,
                () => translate("config.precipitation.enabled.name"), () => translate("config.precipitation.enabled.tooltip"));
            configMenu.AddSectionTitle(manifest, () => translate("config.precipitation.rain.name"));
            configMenu.AddBoolOption(manifest, () => config().PrecipitationRain, value => config().PrecipitationRain = value,
                () => translate("config.precipitation.rain.name"), () => translate("config.precipitation.rain.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().PrecipitationRainDensity, value => config().PrecipitationRainDensity = value,
                () => translate("config.precipitation.density.name"), () => translate("config.precipitation.density.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationRainSize, value => config().PrecipitationRainSize = value,
                () => translate("config.precipitation.size.name"), () => translate("config.precipitation.size.tooltip"), 0.5f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationRainOpacity, value => config().PrecipitationRainOpacity = value,
                () => translate("config.precipitation.opacity.name"), () => translate("config.precipitation.opacity.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationStormDensity, value => config().PrecipitationStormDensity = value,
                () => translate("config.precipitation.stormdensity.name"), () => translate("config.precipitation.stormdensity.tooltip"), 1f, 3f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationRainSlant, value => config().PrecipitationRainSlant = value,
                () => translate("config.precipitation.rainslant.name"), () => translate("config.precipitation.rainslant.tooltip"), 0f, 3f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.precipitation.snow.name"));
            configMenu.AddBoolOption(manifest, () => config().PrecipitationSnow, value => config().PrecipitationSnow = value,
                () => translate("config.precipitation.snow.name"), () => translate("config.precipitation.snow.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().PrecipitationSnowDensity, value => config().PrecipitationSnowDensity = value,
                () => translate("config.precipitation.density.name"), () => translate("config.precipitation.density.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationSnowSize, value => config().PrecipitationSnowSize = value,
                () => translate("config.precipitation.size.name"), () => translate("config.precipitation.size.tooltip"), 0.5f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationSnowOpacity, value => config().PrecipitationSnowOpacity = value,
                () => translate("config.precipitation.opacity.name"), () => translate("config.precipitation.opacity.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.precipitation.wind.name"));
            configMenu.AddBoolOption(manifest, () => config().PrecipitationWind, value => config().PrecipitationWind = value,
                () => translate("config.precipitation.wind.name"), () => translate("config.precipitation.wind.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().PrecipitationWindDensity, value => config().PrecipitationWindDensity = value,
                () => translate("config.precipitation.density.name"), () => translate("config.precipitation.density.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationWindSize, value => config().PrecipitationWindSize = value,
                () => translate("config.precipitation.size.name"), () => translate("config.precipitation.size.tooltip"), 0.5f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationWindOpacity, value => config().PrecipitationWindOpacity = value,
                () => translate("config.precipitation.opacity.name"), () => translate("config.precipitation.opacity.tooltip"), 0.25f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().PrecipitationWindSlant, value => config().PrecipitationWindSlant = value,
                () => translate("config.precipitation.windslant.name"), () => translate("config.precipitation.windslant.tooltip"), 0.25f, 3f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.lightning.name"));
            configMenu.AddBoolOption(manifest, () => config().LightningEffectsEnabled, value => config().LightningEffectsEnabled = value,
                () => translate("config.lightning.name"), () => translate("config.lightning.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().LightningBoltsEnabled, value => config().LightningBoltsEnabled = value,
                () => translate("config.lightningbolts.name"), () => translate("config.lightningbolts.tooltip"));
            // See the note in the tuner: the wet GROUND is off and out of both menus until its
            // puddles can be placed from the map rather than guessed at.
            configMenu.AddSectionTitle(manifest, () => translate("config.wetworld.sectiondrops"));
            configMenu.AddBoolOption(manifest, () => config().WetWorldLensDrops, value => config().WetWorldLensDrops = value,
                () => translate("config.wetworld.lensdrops.name"), () => translate("config.wetworld.lensdrops.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WetWorldLensDropSize, value => config().WetWorldLensDropSize = value,
                () => translate("config.wetworld.lensdropsize.name"), () => translate("config.wetworld.lensdropsize.tooltip"), 0.5f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WetWorldEdgeHaze, value => config().WetWorldEdgeHaze = value,
                () => translate("config.wetworld.edgehaze.name"), () => translate("config.wetworld.edgehaze.tooltip"), 0f, 2f, 0.05f);
        }

        /// <summary>Particles: the pool that drifts, rises and glows in the world itself.</summary>
        private static void RegisterParticlesPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "particles", () => translate("config.section.particles"));
            configMenu.AddBoolOption(manifest, () => config().ParticlesEnabled, value => config().ParticlesEnabled = value,
                () => translate("config.particles.enabled.name"), () => translate("config.particles.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().ParticleDensity, value => config().ParticleDensity = value,
                () => translate("config.particles.density.name"), () => translate("config.particles.density.tooltip"), 0.25f, 2f, 0.05f);

            AddParticleEmitter(configMenu, manifest, translate, "dust",
                () => config().ParticleDust, value => config().ParticleDust = value,
                () => config().ParticleDustAmount, value => config().ParticleDustAmount = value,
                () => config().ParticleDustSize, value => config().ParticleDustSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "embers",
                () => config().ParticleEmbers, value => config().ParticleEmbers = value,
                () => config().ParticleEmbersAmount, value => config().ParticleEmbersAmount = value,
                () => config().ParticleEmbersSize, value => config().ParticleEmbersSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "fireflies",
                () => config().ParticleFireflies, value => config().ParticleFireflies = value,
                () => config().ParticleFirefliesAmount, value => config().ParticleFirefliesAmount = value,
                () => config().ParticleFirefliesSize, value => config().ParticleFirefliesSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "petals",
                () => config().ParticlePetals, value => config().ParticlePetals = value,
                () => config().ParticlePetalsAmount, value => config().ParticlePetalsAmount = value,
                () => config().ParticlePetalsSize, value => config().ParticlePetalsSize = value);
            // Only the flat things buckle, so this one setting sits with them rather than with the
            // particles as a whole.
            configMenu.AddNumberOption(manifest, () => config().ParticlePetalsFlutter, value => config().ParticlePetalsFlutter = value,
                () => translate("config.particles.petalsflutter.name"), () => translate("config.particles.petalsflutter.tooltip"),
                0f, 1f, 0.05f);
            AddParticleEmitter(configMenu, manifest, translate, "ringsparkles",
                () => config().ParticleRingSparkles, value => config().ParticleRingSparkles = value,
                () => config().ParticleRingSparklesAmount, value => config().ParticleRingSparklesAmount = value,
                () => config().ParticleRingSparklesSize, value => config().ParticleRingSparklesSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "footdust",
                () => config().ParticleFootDust, value => config().ParticleFootDust = value,
                () => config().ParticleFootDustAmount, value => config().ParticleFootDustAmount = value,
                () => config().ParticleFootDustSize, value => config().ParticleFootDustSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "festivelights",
                () => config().ParticleFestiveLights, value => config().ParticleFestiveLights = value,
                () => config().ParticleFestiveLightsAmount, value => config().ParticleFestiveLightsAmount = value,
                () => config().ParticleFestiveLightsSize, value => config().ParticleFestiveLightsSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "chimney",
                () => config().ParticleChimney, value => config().ParticleChimney = value,
                () => config().ParticleChimneyAmount, value => config().ParticleChimneyAmount = value,
                () => config().ParticleChimneySize, value => config().ParticleChimneySize = value);
            configMenu.AddNumberOption(manifest, () => config().ParticleGlowLight, value => config().ParticleGlowLight = value,
                () => translate("config.particles.glowlight.name"), () => translate("config.particles.glowlight.tooltip"), 0f, 1f, 0.05f);
            AddParticleEmitter(configMenu, manifest, translate, "waterfallmist",
                () => config().ParticleWaterfallMist, value => config().ParticleWaterfallMist = value,
                () => config().ParticleWaterfallMistAmount, value => config().ParticleWaterfallMistAmount = value,
                () => config().ParticleWaterfallMistSize, value => config().ParticleWaterfallMistSize = value);
            configMenu.AddBoolOption(manifest, () => config().WaterfallRainbowFollowsSun, value => config().WaterfallRainbowFollowsSun = value,
                () => translate("config.particles.waterfallrainbowsun.name"), () => translate("config.particles.waterfallrainbowsun.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterfallRainbowStrength, value => config().WaterfallRainbowStrength = value,
                () => translate("config.particles.waterfallrainbow.name"), () => translate("config.particles.waterfallrainbow.tooltip"), 0f, 1f, 0.05f);
            AddParticleEmitter(configMenu, manifest, translate, "hotspringsteam",
                () => config().ParticleHotSpringSteam, value => config().ParticleHotSpringSteam = value,
                () => config().ParticleHotSpringSteamAmount, value => config().ParticleHotSpringSteamAmount = value,
                () => config().ParticleHotSpringSteamSize, value => config().ParticleHotSpringSteamSize = value);
            AddParticleEmitter(configMenu, manifest, translate, "lavasparks",
                () => config().ParticleLavaSparks, value => config().ParticleLavaSparks = value,
                () => config().ParticleLavaSparksAmount, value => config().ParticleLavaSparksAmount = value,
                () => config().ParticleLavaSparksSize, value => config().ParticleLavaSparksSize = value);

            // --- Cloud shadows (implemented) ---
        }

        /// <summary>One emitter's three settings: whether it runs, how much of it there is, and
        /// how big each piece is. Every emitter gets the same three, so adding one is a call here
        /// rather than another dozen lines that have to agree with the other dozen.
        /// <para>The amount and size labels are shared across emitters on purpose: they mean
        /// exactly the same thing every time, and a translator should not be asked to write "how
        /// many" six times.</para></summary>
        private static void AddParticleEmitter(IGenericModConfigMenuApi configMenu, IManifest manifest,
            Func<string, string> translate, string emitter,
            Func<bool> getOn, Action<bool> setOn,
            Func<float> getAmount, Action<float> setAmount,
            Func<float> getSize, Action<float> setSize)
        {
            configMenu.AddSectionTitle(manifest, () => translate($"config.particles.{emitter}.name"));
            configMenu.AddBoolOption(manifest, getOn, setOn,
                () => translate($"config.particles.{emitter}.name"), () => translate($"config.particles.{emitter}.tooltip"));
            configMenu.AddNumberOption(manifest, getAmount, setAmount,
                () => translate("config.particles.amount.name"), () => translate("config.particles.amount.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, getSize, setSize,
                () => translate("config.particles.size.name"), () => translate("config.particles.size.tooltip"), 0.5f, 2f, 0.05f);
        }

        /// <summary>Cloud shadows, including hiding the vanilla ones.</summary>
        private static void RegisterCloudShadowPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "cloudshadow", () => translate("config.section.cloudshadow"));
            configMenu.AddBoolOption(manifest, () => config().SuppressVanillaCloudShadow, value => config().SuppressVanillaCloudShadow = value,
                () => translate("config.cloudshadow.hidevanilla.name"), () => translate("config.cloudshadow.hidevanilla.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().CloudShadowEnabled, value => config().CloudShadowEnabled = value,
                () => translate("config.cloudshadow.enabled.name"), () => translate("config.cloudshadow.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().CloudShadowCoverage, value => config().CloudShadowCoverage = value,
                () => translate("config.cloudshadow.coverage.name"), null, 0.1f, 0.9f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().CloudShadowCount, value => config().CloudShadowCount = value,
                () => translate("config.cloudshadow.count.name"), () => translate("config.cloudshadow.count.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().CloudShadowOpacity, value => config().CloudShadowOpacity = value,
                () => translate("config.cloudshadow.opacity.name"), null, 0f, 0.7f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().CloudShadowScale, value => config().CloudShadowScale = value,
                () => translate("config.cloudshadow.scale.name"), null, 1f, 5f, 0.5f);
            configMenu.AddNumberOption(manifest, () => config().CloudShadowSpeed, value => config().CloudShadowSpeed = value,
                () => translate("config.cloudshadow.speed.name"), null, 0f, 0.1f, 0.005f);
            configMenu.AddNumberOption(manifest, () => config().StormWarningStrength, value => config().StormWarningStrength = value,
                () => translate("config.cloudshadow.stormwarning.name"), () => translate("config.cloudshadow.stormwarning.tooltip"), 0f, 1f, 0.05f);

            // --- Lens: the camera-glass effects, grouped as the F6 tuner groups them ---
        }

        /// <summary>Lens effects: tilt shift, vignette, chromatic aberration.</summary>
        private static void RegisterLensPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "lens", () => translate("config.section.lens"));
            configMenu.AddBoolOption(manifest, () => config().TiltShiftEnabled, value => config().TiltShiftEnabled = value,
                () => translate("config.tiltshift.enabled.name"), () => translate("config.tiltshift.enabled.tooltip"));
            configMenu.AddTextOption(manifest,
                () => config().TiltShiftMode.ToString(),
                value => config().TiltShiftMode = Enum.TryParse<TiltShiftFocus>(value, out var mode) ? mode : TiltShiftFocus.Bands,
                () => translate("config.tiltshift.mode.name"), () => translate("config.tiltshift.mode.tooltip"),
                [nameof(TiltShiftFocus.Bands), nameof(TiltShiftFocus.Radial)],
                choice => translate($"config.tiltshift.mode.{choice.ToLowerInvariant()}"));
            configMenu.AddNumberOption(manifest, () => config().TiltShiftStrength, value => config().TiltShiftStrength = value,
                () => translate("config.tiltshift.strength.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().TiltShiftRadius, value => config().TiltShiftRadius = value,
                () => translate("config.tiltshift.radius.name"), () => translate("config.tiltshift.radius.tooltip"), 0.05f, 0.9f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().TiltShiftFeather, value => config().TiltShiftFeather = value,
                () => translate("config.tiltshift.feather.name"), () => translate("config.tiltshift.feather.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().TiltShiftTopRatio, value => config().TiltShiftTopRatio = value,
                () => translate("config.tiltshift.top.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().TiltShiftBottomRatio, value => config().TiltShiftBottomRatio = value,
                () => translate("config.tiltshift.bottom.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().TiltShiftIndoorAmount, value => config().TiltShiftIndoorAmount = value,
                () => translate("config.tiltshift.indoor.name"), () => translate("config.tiltshift.indoor.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.section.finishing"));
            configMenu.AddBoolOption(manifest, () => config().VignetteEnabled, value => config().VignetteEnabled = value,
                () => translate("config.vignette.enabled.name"), () => translate("config.vignette.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().VignetteStrength, value => config().VignetteStrength = value,
                () => translate("config.vignette.strength.name"), null, 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().ChromaticAberrationEnabled, value => config().ChromaticAberrationEnabled = value,
                () => translate("config.ca.enabled.name"), () => translate("config.ca.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().ChromaticAberrationStrength, value => config().ChromaticAberrationStrength = value,
                () => translate("config.ca.strength.name"), null, 0f, 1f, 0.05f);

            // --- Water (implemented) ---
        }

        /// <summary>Water surface and reflections.</summary>
        private static void RegisterWaterPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "water", () => translate("config.section.water"));
            configMenu.AddBoolOption(manifest, () => config().WaterEnabled, value => config().WaterEnabled = value,
                () => translate("config.water.enabled.name"), () => translate("config.water.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterStrength, value => config().WaterStrength = value,
                () => translate("config.water.strength.name"), null, 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterSpeed, value => config().WaterSpeed = value,
                () => translate("config.water.speed.name"), null, 0f, 3f, 0.1f);
            configMenu.AddNumberOption(manifest, () => config().WaterSparkle, value => config().WaterSparkle = value,
                () => translate("config.water.sparkle.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterSparkleDensity, value => config().WaterSparkleDensity = value,
                () => translate("config.water.sparkledensity.name"), () => translate("config.water.sparkledensity.tooltip"), 0.2f, 2f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WaterSparkleCloudShade, value => config().WaterSparkleCloudShade = value,
                () => translate("config.water.sparklecloud.name"), () => translate("config.water.sparklecloud.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterGlitterPath, value => config().WaterGlitterPath = value,
                () => translate("config.water.glitterpath.name"), () => translate("config.water.glitterpath.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WaterCausticsEnabled, value => config().WaterCausticsEnabled = value,
                () => translate("config.water.caustics.name"), () => translate("config.water.caustics.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterCausticsStrength, value => config().WaterCausticsStrength = value,
                () => translate("config.water.causticsstrength.name"), null, 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WaterReflection, value => config().WaterReflection = value,
                () => translate("config.water.reflection.name"), () => translate("config.water.reflection.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterReflectStrength, value => config().WaterReflectStrength = value,
                () => translate("config.water.reflectstrength.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterReflectDistort, value => config().WaterReflectDistort = value,
                () => translate("config.water.reflectdistort.name"), () => translate("config.water.reflectdistort.tooltip"),
                0f, 1.5f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterReflectBanding, value => config().WaterReflectBanding = value,
                () => translate("config.water.reflectbanding.name"), () => translate("config.water.reflectbanding.tooltip"),
                0f, 16f, 1f);
            configMenu.AddNumberOption(manifest, () => config().WaterReflectBlur, value => config().WaterReflectBlur = value,
                () => translate("config.water.reflectblur.name"), () => translate("config.water.reflectblur.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterReflectDepth, value => config().WaterReflectDepth = value,
                () => translate("config.water.reflectdepth.name"), () => translate("config.water.reflectdepth.tooltip"),
                0.1f, 1.5f, 0.05f);
            // Reach has been in the config file since 1.5.6 and in no menu, which is the same as
            // not existing for almost everybody who might want it.
            configMenu.AddNumberOption(manifest, () => config().WaterReflectReach, value => config().WaterReflectReach = value,
                () => translate("config.water.reflectreach.name"), () => translate("config.water.reflectreach.tooltip"),
                0.2f, 1f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.water.sectionrain"));
            configMenu.AddNumberOption(manifest, () => config().WaterRainRingDensity, value => config().WaterRainRingDensity = value,
                () => translate("config.water.rainringdensity.name"), () => translate("config.water.rainringdensity.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRainRingSize, value => config().WaterRainRingSize = value,
                () => translate("config.water.rainringsize.name"), () => translate("config.water.rainringsize.tooltip"),
                0.4f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRainRingStrength, value => config().WaterRainRingStrength = value,
                () => translate("config.water.rainringstrength.name"), () => translate("config.water.rainringstrength.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterWakeRings, value => config().WaterWakeRings = value,
                () => translate("config.water.wakerings.name"), () => translate("config.water.wakerings.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterFishSpotRings, value => config().WaterFishSpotRings = value,
                () => translate("config.water.fishspotrings.name"), () => translate("config.water.fishspotrings.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterWind, value => config().WaterWind = value,
                () => translate("config.water.wind.name"), () => translate("config.water.wind.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WaterRiverFlowEnabled, value => config().WaterRiverFlowEnabled = value,
                () => translate("config.water.riverflow.name"), () => translate("config.water.riverflow.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterCurrent, value => config().WaterCurrent = value,
                () => translate("config.water.current.name"), () => translate("config.water.current.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverRainSwell, value => config().WaterRiverRainSwell = value,
                () => translate("config.water.rainswell.name"), () => translate("config.water.rainswell.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverFoam, value => config().WaterRiverFoam = value,
                () => translate("config.water.riverfoam.name"), () => translate("config.water.riverfoam.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverRippleSpeed, value => config().WaterRiverRippleSpeed = value,
                () => translate("config.water.riverripplespeed.name"), () => translate("config.water.riverripplespeed.tooltip"),
                1.0f, 3.0f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverRenew, value => config().WaterRiverRenew = value,
                () => translate("config.water.riverrenew.name"), () => translate("config.water.riverrenew.tooltip"),
                0.0f, 1.0f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverBankDrag, value => config().WaterRiverBankDrag = value,
                () => translate("config.water.riverbankdrag.name"), () => translate("config.water.riverbankdrag.tooltip"),
                0.0f, 1.0f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverSwirl, value => config().WaterRiverSwirl = value,
                () => translate("config.water.riverswirl.name"), () => translate("config.water.riverswirl.tooltip"),
                0.0f, 1.0f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverGlitter, value => config().WaterRiverGlitter = value,
                () => translate("config.water.riverglitter.name"), () => translate("config.water.riverglitter.tooltip"),
                0.0f, 1.0f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverFoamStreak, value => config().WaterRiverFoamStreak = value,
                () => translate("config.water.riverfoamstreak.name"), () => translate("config.water.riverfoamstreak.tooltip"),
                1.0f, 5.0f, 0.1f);
            configMenu.AddNumberOption(manifest, () => config().WaterRiverWaves, value => config().WaterRiverWaves = value,
                () => translate("config.water.riverwaves.name"), () => translate("config.water.riverwaves.tooltip"),
                0.0f, 2.0f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WaterRiverPixelStep, value => config().WaterRiverPixelStep = value,
                () => translate("config.water.riverpixelstep.name"), () => translate("config.water.riverpixelstep.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterSeaWaves, value => config().WaterSeaWaves = value,
                () => translate("config.water.seawaves.name"), () => translate("config.water.seawaves.tooltip"),
                0f, 2f, 0.05f);
            // Reflection REACH and FADE ROWS are not offered here. They buy frames, they do not
            // change how anything looks, and the performance preset already sets both: a player
            // who moves them sees nothing happen and concludes the mod is broken. The settings
            // still exist for radiance_config, which is where an A/B belongs.
            // Which water, then the classic water's look. This menu cannot hide rows by the
            // water in use the way the tuner does, so each water's dials sit under a heading
            // that says when they apply.
            configMenu.AddTextOption(manifest,
                () => config().WaterReflectModel.ToString(),
                value => config().WaterReflectModel = Enum.TryParse<WaterReflectionModel>(value, out var model) ? model : WaterReflectionModel.Modern,
                () => translate("config.water.model.name"), () => translate("config.water.model.tooltip"),
                [nameof(WaterReflectionModel.Modern), nameof(WaterReflectionModel.Classic)],
                choice => translate($"config.water.model.{choice.ToLowerInvariant()}"));
            configMenu.AddSectionTitle(manifest, () => translate("config.water.classic.title"), () => translate("config.water.classic.tooltip"));
            configMenu.AddTextOption(manifest,
                () => config().WaterReflectStyle.ToString(),
                value => config().WaterReflectStyle = Enum.TryParse<WaterReflectionStyle>(value, out var style) ? style : WaterReflectionStyle.Natural,
                () => translate("config.water.reflstyle.name"), () => translate("config.water.reflstyle.tooltip"),
                ["StillWater", "Natural", "Choppy"]);
            configMenu.AddSectionTitle(manifest, () => translate("config.water.modern.title"), () => translate("config.water.modern.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WaterModernWobble, value => config().WaterModernWobble = value,
                () => translate("config.water.modernwobble.name"), () => translate("config.water.modernwobble.tooltip"),
                0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernChoppiness, value => config().WaterModernChoppiness = value,
                () => translate("config.water.modernchoppiness.name"), () => translate("config.water.modernchoppiness.tooltip"),
                0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernParallax, value => config().WaterModernParallax = value,
                () => translate("config.water.modernparallax.name"), () => translate("config.water.modernparallax.tooltip"),
                0f, 0.3f, 0.01f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernFresnel, value => config().WaterModernFresnel = value,
                () => translate("config.water.modernfresnel.name"), () => translate("config.water.modernfresnel.tooltip"),
                0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernStretch, value => config().WaterModernStretch = value,
                () => translate("config.water.modernstretch.name"), () => translate("config.water.modernstretch.tooltip"),
                1f, 1.4f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernEdgeSoftness, value => config().WaterModernEdgeSoftness = value,
                () => translate("config.water.modernedgesoftness.name"), () => translate("config.water.modernedgesoftness.tooltip"),
                0f, 6f, 0.25f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernPlungeChurn, value => config().WaterModernPlungeChurn = value,
                () => translate("config.water.modernplungechurn.name"), () => translate("config.water.modernplungechurn.tooltip"),
                0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernPlungeReach, value => config().WaterModernPlungeReach = value,
                () => translate("config.water.modernplungereach.name"), () => translate("config.water.modernplungereach.tooltip"),
                1f, 6f, 0.5f);
            configMenu.AddNumberOption(manifest, () => config().WaterModernLipFade, value => config().WaterModernLipFade = value,
                () => translate("config.water.modernlipfade.name"), () => translate("config.water.modernlipfade.tooltip"),
                0f, 1.5f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WaterEffectIndoors, value => config().WaterEffectIndoors = value,
                () => translate("config.water.indoors.name"), () => translate("config.water.indoors.tooltip"));
            // --- Dynamic lighting (implemented) ---
        }

        /// <summary>The flood grid, the light pools and the window effects.</summary>
        private static void RegisterLightingPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "lighting", () => translate("config.section.lighting"));
            configMenu.AddBoolOption(manifest, () => config().FloodLightingEnabled, value => config().FloodLightingEnabled = value,
                () => translate("config.lighting.flood.name"), () => translate("config.lighting.flood.tooltip"));
            configMenu.AddTextOption(manifest,
                () => config().FloodGiModel.ToString(),
                value => config().FloodGiModel = Enum.TryParse<GiModel>(value, out var model) ? model : GiModel.Flood,
                () => translate("config.lighting.gimodel.name"), () => translate("config.lighting.gimodel.tooltip"),
                [nameof(GiModel.Flood), nameof(GiModel.Cascades)],
                choice => translate($"config.lighting.gimodel.{choice.ToLowerInvariant()}"));
            configMenu.AddBoolOption(manifest, () => config().SpriteReliefEnabled, value => config().SpriteReliefEnabled = value,
                () => translate("config.lighting.relief.name"), () => translate("config.lighting.relief.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SpriteReliefStrength, value => config().SpriteReliefStrength = value,
                () => translate("config.lighting.reliefstrength.name"), () => translate("config.lighting.reliefstrength.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().SpriteReliefHalfResolution, value => config().SpriteReliefHalfResolution = value,
                () => translate("config.lighting.reliefhalfres.name"), () => translate("config.lighting.reliefhalfres.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SpriteReliefSun, value => config().SpriteReliefSun = value,
                () => translate("config.lighting.reliefsun.name"), () => translate("config.lighting.reliefsun.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().SpriteReliefRim, value => config().SpriteReliefRim = value,
                () => translate("config.lighting.reliefrim.name"), () => translate("config.lighting.reliefrim.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().SpriteReliefLeafShimmer, value => config().SpriteReliefLeafShimmer = value,
                () => translate("config.lighting.leafshimmer.name"), () => translate("config.lighting.leafshimmer.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FloodLightingStrength, value => config().FloodLightingStrength = value,
                () => translate("config.lighting.floodstrength.name"), () => translate("config.lighting.floodstrength.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FloodColourBleed, value => config().FloodColourBleed = value,
                () => translate("config.lighting.colourbleed.name"), () => translate("config.lighting.colourbleed.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().FloodShadowStrength, value => config().FloodShadowStrength = value,
                () => translate("config.lighting.floodshadow.name"), () => translate("config.lighting.floodshadow.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightShadowCarve, value => config().LightShadowCarve = value,
                () => translate("config.lighting.shadowcarve.name"), () => translate("config.lighting.shadowcarve.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightShadowSoftness, value => config().LightShadowSoftness = value,
                () => translate("config.lighting.shadowsoftness.name"), () => translate("config.lighting.shadowsoftness.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightShadowDetail, value => config().LightShadowDetail = value,
                () => translate("config.lighting.shadowdetail.name"), () => translate("config.lighting.shadowdetail.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().LightShadowDetailShared, value => config().LightShadowDetailShared = value,
                () => translate("config.lighting.shadowshared.name"), () => translate("config.lighting.shadowshared.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().LightShadowSharpEdges, value => config().LightShadowSharpEdges = value,
                () => translate("config.lighting.shadowsharp.name"), () => translate("config.lighting.shadowsharp.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().LightShadowMarchCache, value => config().LightShadowMarchCache = value,
                () => translate("config.lighting.shadowcache.name"), () => translate("config.lighting.shadowcache.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WateredSoilSparkle, value => config().WateredSoilSparkle = value,
                () => translate("config.lighting.wateredsoil.name"), () => translate("config.lighting.wateredsoil.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().LightingEnabled, value => config().LightingEnabled = value,
                () => translate("config.lighting.enabled.name"), () => translate("config.lighting.enabled.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().LightingIndoorDarkness, value => config().LightingIndoorDarkness = value,
                () => translate("config.lighting.indoor.name"), () => translate("config.lighting.indoor.tooltip"), 0f, 0.95f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingNightDarkness, value => config().LightingNightDarkness = value,
                () => translate("config.lighting.night.name"), () => translate("config.lighting.night.tooltip"), 0f, 0.95f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingMorningDarkness, value => config().LightingMorningDarkness = value,
                () => translate("config.lighting.morning.name"), () => translate("config.lighting.morning.tooltip"), 0f, 0.95f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingIndoorColourWalk, value => config().LightingIndoorColourWalk = value,
                () => translate("config.lighting.indoorcolour.name"), () => translate("config.lighting.indoorcolour.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingMorningClearSkyCool, value => config().LightingMorningClearSkyCool = value,
                () => translate("config.lighting.morningcool.name"), () => translate("config.lighting.morningcool.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingWarmth, value => config().LightingWarmth = value,
                () => translate("config.lighting.warmth.name"), null, 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingBoost, value => config().LightingBoost = value,
                () => translate("config.lighting.boost.name"), null, 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LightingRadiusScale, value => config().LightingRadiusScale = value,
                () => translate("config.lighting.radius.name"), null, 0.2f, 3f, 0.1f);
            configMenu.AddBoolOption(manifest, () => config().LightingShadows, value => config().LightingShadows = value,
                () => translate("config.lighting.shadows.name"), () => translate("config.lighting.shadows.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().LightingShadowStrength, value => config().LightingShadowStrength = value,
                () => translate("config.lighting.shadowstrength.name"), null, 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().LightShadowSilhouettes, value => config().LightShadowSilhouettes = value,
                () => translate("config.lighting.silhouettes.name"), () => translate("config.lighting.silhouettes.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().LightShadowProps, value => config().LightShadowProps = value,
                () => translate("config.lighting.props.name"), () => translate("config.lighting.props.tooltip"));

            // --- Directional sprite shadows ---
        }

        /// <summary>Directional sprite shadows.</summary>
        /// <summary>Everything the mod does with a window, on one page: the daylight it lets in,
        /// the beam you can see, the glow after dusk, and the people in the glass by day. It lived
        /// inside Lighting, where four window rows among fifteen lighting rows were easy to miss
        /// and hard to explain as one thing.</summary>
        private static void RegisterWindowsPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "windows", () => translate("config.section.windows"));
            // Two things a window does, kept apart on the page: the light it lets through, and
            // the picture it returns.
            configMenu.AddSectionTitle(manifest, () => translate("config.windows.sectionlight"));
            configMenu.AddBoolOption(manifest, () => config().WindowEffectsEnabled, value => config().WindowEffectsEnabled = value,
                () => translate("config.lighting.windoweffects.name"), () => translate("config.lighting.windoweffects.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WindowGlowOpensNight, value => config().WindowGlowOpensNight = value,
                () => translate("config.lighting.windowopensnight.name"), () => translate("config.lighting.windowopensnight.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().LampHalo, value => config().LampHalo = value,
                () => translate("config.lighting.lamphalo.name"), () => translate("config.lighting.lamphalo.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().AquariumRipple, value => config().AquariumRipple = value,
                () => translate("config.lighting.aquariumripple.name"), () => translate("config.lighting.aquariumripple.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().TvScreenGlow, value => config().TvScreenGlow = value,
                () => translate("config.lighting.tvglow.name"), () => translate("config.lighting.tvglow.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().WindowBeamEnabled, value => config().WindowBeamEnabled = value,
                () => translate("config.lighting.windowbeam.name"), () => translate("config.lighting.windowbeam.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WindowDaylightStrength, value => config().WindowDaylightStrength = value,
                () => translate("config.lighting.windowdaylightstrength.name"),
                () => translate("config.lighting.windowdaylightstrength.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowDaylightStrengthElsewhere, value => config().WindowDaylightStrengthElsewhere = value,
                () => translate("config.lighting.windowdaylightelsewhere.name"),
                () => translate("config.lighting.windowdaylightelsewhere.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("config.windows.sectionreflection"));
            configMenu.AddBoolOption(manifest, () => config().WindowReflectionEnabled, value => config().WindowReflectionEnabled = value,
                () => translate("config.lighting.windowreflection.name"), () => translate("config.lighting.windowreflection.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().WindowReflectionIndoors, value => config().WindowReflectionIndoors = value,
                () => translate("config.lighting.windowreflectionindoors.name"), () => translate("config.lighting.windowreflectionindoors.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().WindowReflectionStrength, value => config().WindowReflectionStrength = value,
                () => translate("config.lighting.windowreflectionstrength.name"),
                () => translate("config.lighting.windowreflectionstrength.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowReflectionNightStrength, value => config().WindowReflectionNightStrength = value,
                () => translate("config.lighting.windowreflectionnight.name"),
                () => translate("config.lighting.windowreflectionnight.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowVehicleGlassStrength, value => config().WindowVehicleGlassStrength = value,
                () => translate("config.lighting.windowvehicleglass.name"),
                () => translate("config.lighting.windowvehicleglass.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowSheenStrength, value => config().WindowSheenStrength = value,
                () => translate("config.lighting.windowsheen.name"),
                () => translate("config.lighting.windowsheen.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowSceneReflectionStrength, value => config().WindowSceneReflectionStrength = value,
                () => translate("config.lighting.windowscene.name"),
                () => translate("config.lighting.windowscene.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowGlareStrength, value => config().WindowGlareStrength = value,
                () => translate("config.lighting.windowglare.name"),
                () => translate("config.lighting.windowglare.tooltip"), 0f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().WindowLightGlowStrength, value => config().WindowLightGlowStrength = value,
                () => translate("config.lighting.windowlightglow.name"),
                () => translate("config.lighting.windowlightglow.tooltip"), 0f, 2f, 0.05f);
        }

        /// <summary>One slider per caster kind, named by the shared key pattern
        /// <c>config.shadows.{family}.{kind}.name</c>. The three per-kind families (length,
        /// softness, lean) are eighteen sliders of exactly this shape; the table keeps a kind
        /// from being added to one family and forgotten in another.</summary>
        /// <summary>One caster kind's three dials under its own heading. GMCM cannot hide rows
        /// behind a picker the way the tuner tab does, so the flat list is regrouped instead: the
        /// kind is the heading and its length, softness and lean follow it, rather than three
        /// blocks of seven with a building's three dials eight rows apart.</summary>
        private static void AddKindDials(IGenericModConfigMenuApi configMenu, IManifest manifest,
            Func<string, string> translate, string kind,
            Func<float> getLength, Action<float> setLength,
            Func<float> getSoftness, Action<float> setSoftness,
            Func<float> getLean, Action<float> setLean)
        {
            // The heading reuses the name the length block already carried, so no kind was
            // renamed and no translator has to look at this again.
            configMenu.AddSectionTitle(manifest, () => translate($"config.shadows.length.{kind}.name"));
            configMenu.AddNumberOption(manifest, getLength, setLength,
                () => translate("tuner.shadowkind.length"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            configMenu.AddNumberOption(manifest, getSoftness, setSoftness,
                () => translate("tuner.shadowkind.softness"), () => translate("config.shadows.softness.tooltip"),
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            configMenu.AddNumberOption(manifest, getLean, setLean,
                () => translate("tuner.shadowkind.lean"), () => translate("config.shadows.lean.tooltip"),
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
        }

        private static void RegisterShadowsPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "shadows", () => translate("config.section.shadows"));
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowsEnabled, value => config().DirectionalShadowsEnabled = value,
                () => translate("config.shadows.enabled.name"), () => translate("config.shadows.enabled.tooltip"));
            // Which shapes, named by the version each shipped in, exactly as the water is.
            configMenu.AddTextOption(manifest,
                () => config().DirectionalShadowModel.ToString(),
                value => config().DirectionalShadowModel = Enum.TryParse<ShadowModel>(value, out var model) ? model : ShadowModel.Modern,
                () => translate("config.shadows.model.name"), () => translate("config.shadows.model.tooltip"),
                [nameof(ShadowModel.Modern), nameof(ShadowModel.Classic)],
                choice => translate($"config.shadows.model.{choice.ToLowerInvariant()}"));
            configMenu.AddNumberOption(manifest, () => config().DirectionalShadowStrength, value => config().DirectionalShadowStrength = value,
                () => translate("config.shadows.strength.name"), () => translate("config.shadows.strength.tooltip"), 0f, ModConfig.ShadowStrengthMax, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().DirectionalShadowLength, value => config().DirectionalShadowLength = value,
                () => translate("config.shadows.length.name"), null, 0.2f, 2f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().GoldenHourStrength, value => config().GoldenHourStrength = value,
                () => translate("config.shadows.goldenhour.name"),
                () => translate("config.shadows.goldenhour.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().SunSeasonStrength, value => config().SunSeasonStrength = value,
                () => translate("config.shadows.sunseason.name"),
                () => translate("config.shadows.sunseason.tooltip"), 0f, 1f, 0.05f);
            // Degrees here, a round dial in the mod's own menu: this config screen has no circle
            // to offer, and a number the player can type is better than a track with a seam in it.
            configMenu.AddNumberOption(manifest, () => config().ShadowSunBearing, value => config().ShadowSunBearing = value,
                () => translate("config.shadows.sunbearing.name"),
                () => translate("config.shadows.sunbearing.tooltip"), 0f, 359f, 5f);
            configMenu.AddNumberOption(manifest, () => config().SunlightBearing, value => config().SunlightBearing = value,
                () => translate("config.shadows.sunlightbearing.name"),
                () => translate("config.shadows.sunlightbearing.tooltip"), 0f, 359f, 5f);
            configMenu.AddNumberOption(manifest, () => config().ShadowContactHardness, value => config().ShadowContactHardness = value,
                () => translate("config.shadows.contacthardness.name"),
                () => translate("config.shadows.contacthardness.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ShadowPenumbraStretch, value => config().ShadowPenumbraStretch = value,
                () => translate("config.shadows.penumbrastretch.name"),
                () => translate("config.shadows.penumbrastretch.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ShadowTint, value => config().ShadowTint = value,
                () => translate("config.shadows.tint.name"),
                () => translate("config.shadows.tint.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().DirectionalShadowBlur, value => config().DirectionalShadowBlur = value,
                () => translate("config.shadows.blur.name"), () => translate("help.shadowblur"), 0f, ModConfig.ShadowBlurMax, 0.5f);
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowPlayer, value => config().DirectionalShadowPlayer = value,
                () => translate("config.shadows.player.name"), () => translate("config.shadows.player.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowVillagers, value => config().DirectionalShadowVillagers = value,
                () => translate("config.shadows.villagers.name"), () => translate("config.shadows.villagers.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowFarmAnimals, value => config().DirectionalShadowFarmAnimals = value,
                () => translate("config.shadows.farmanimals.name"), () => translate("config.shadows.farmanimals.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowCreatures, value => config().DirectionalShadowCreatures = value,
                () => translate("config.shadows.creatures.name"), () => translate("config.shadows.creatures.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowObjects, value => config().DirectionalShadowObjects = value,
                () => translate("config.shadows.objects.name"), () => translate("config.shadows.objects.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().ContactShadowStrength, value => config().ContactShadowStrength = value,
                () => translate("config.shadows.contact.name"), () => translate("config.shadows.contact.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ContactShadowPeopleStrength, value => config().ContactShadowPeopleStrength = value,
                () => translate("config.shadows.contactpeople.name"), () => translate("config.shadows.contactpeople.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().DirectionalShadowBuildings, value => config().DirectionalShadowBuildings = value,
                () => translate("config.shadows.buildings.name"), () => translate("config.shadows.buildings.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().ShadowGroundForeshortening, value => config().ShadowGroundForeshortening = value,
                () => translate("config.shadows.groundforeshortening.name"), () => translate("config.shadows.groundforeshortening.tooltip"),
                ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().ShadowGroundedLook, value => config().ShadowGroundedLook = value,
                () => translate("config.shadows.grounded.name"), () => translate("config.shadows.grounded.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().ShadowGroundedDepth, value => config().ShadowGroundedDepth = value,
                () => translate("config.shadows.groundeddepth.name"), () => translate("config.shadows.groundeddepth.tooltip"),
                ModConfig.ShadowGroundedDepthMin, ModConfig.ShadowGroundedDepthMax, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ShadowCharacterGroundForeshortening, value => config().ShadowCharacterGroundForeshortening = value,
                () => translate("config.shadows.charactergroundforeshortening.name"), () => translate("config.shadows.charactergroundforeshortening.tooltip"),
                ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().ShadowCastsPerCharacter, value => config().ShadowCastsPerCharacter = value,
                () => translate("config.shadows.casts.name"), () => translate("config.shadows.casts.tooltip"),
                ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax, 1);

            // Per kind, grouped by the kind rather than by the dial. The overall length and
            // softness above still multiply these, so a player who only wants everything shorter
            // never has to come down here at all.
            configMenu.AddSectionTitle(manifest, () => translate("config.shadows.perkind.title"),
                () => translate("config.shadows.perkind.tooltip"));
            AddKindDials(configMenu, manifest, translate, "trees",
                () => config().ShadowLengthTrees, value => config().ShadowLengthTrees = value,
                () => config().ShadowSoftnessTrees, value => config().ShadowSoftnessTrees = value,
                () => config().ShadowLeanTrees, value => config().ShadowLeanTrees = value);
            AddKindDials(configMenu, manifest, translate, "smalltrees",
                () => config().ShadowLengthSmallTrees, value => config().ShadowLengthSmallTrees = value,
                () => config().ShadowSoftnessSmallTrees, value => config().ShadowSoftnessSmallTrees = value,
                () => config().ShadowLeanSmallTrees, value => config().ShadowLeanSmallTrees = value);
            AddKindDials(configMenu, manifest, translate, "bushes",
                () => config().ShadowLengthBushes, value => config().ShadowLengthBushes = value,
                () => config().ShadowSoftnessBushes, value => config().ShadowSoftnessBushes = value,
                () => config().ShadowLeanBushes, value => config().ShadowLeanBushes = value);
            AddKindDials(configMenu, manifest, translate, "crops",
                () => config().ShadowLengthCrops, value => config().ShadowLengthCrops = value,
                () => config().ShadowSoftnessCrops, value => config().ShadowSoftnessCrops = value,
                () => config().ShadowLeanCrops, value => config().ShadowLeanCrops = value);
            AddKindDials(configMenu, manifest, translate, "grass",
                () => config().ShadowLengthGrass, value => config().ShadowLengthGrass = value,
                () => config().ShadowSoftnessGrass, value => config().ShadowSoftnessGrass = value,
                () => config().ShadowLeanGrass, value => config().ShadowLeanGrass = value);
            AddKindDials(configMenu, manifest, translate, "objects",
                () => config().ShadowLengthObjects, value => config().ShadowLengthObjects = value,
                () => config().ShadowSoftnessObjects, value => config().ShadowSoftnessObjects = value,
                () => config().ShadowLeanObjects, value => config().ShadowLeanObjects = value);
            AddKindDials(configMenu, manifest, translate, "buildings",
                () => config().ShadowLengthBuildings, value => config().ShadowLengthBuildings = value,
                () => config().ShadowSoftnessBuildings, value => config().ShadowSoftnessBuildings = value,
                () => config().ShadowLeanBuildings, value => config().ShadowLeanBuildings = value);

            // --- Camera (implemented) ---
        }

        /// <summary>Camera smoothing.</summary>
        private static void RegisterCameraPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "camera", () => translate("config.section.camera"));
            // GMCM has no greyed-out row, so while the camera is stood down the page says why and
            // offers nothing to change.
            if (!CameraSmoother.Available)
            {
                configMenu.AddParagraph(manifest, () => translate("config.camera.disabled"));
                return;
            }
            configMenu.AddTextOption(manifest,
                () => config().CameraMode.ToString(),
                value => config().CameraMode = Enum.TryParse<CameraMode>(value, out var mode) ? mode : CameraMode.Off,
                () => translate("config.camera.mode.name"), () => translate("config.camera.mode.tooltip"),
                [nameof(CameraMode.Off), nameof(CameraMode.Smooth)],
                choice => translate($"config.camera.mode.{choice.ToLowerInvariant()}"));
            configMenu.AddNumberOption(manifest, () => config().CameraFollowSpeed, value => config().CameraFollowSpeed = value,
                () => translate("config.smoothcam.speed.name"), () => translate("config.smoothcam.speed.tooltip"), 0.05f, 1f, 0.05f);

            // --- Performance page: what the picture costs, kept away from the look settings ---
        }

        /// <summary>Render scale and sharpening.</summary>
        private static void RegisterPerformancePage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "perf", () => translate("config.section.perf"));
            configMenu.AddNumberOption(manifest, () => config().RenderScale, value => config().RenderScale = value,
                () => translate("config.renderscale.name"), () => translate("config.renderscale.tooltip"), 0.5f, 1f, 0.05f);
            configMenu.AddBoolOption(manifest, () => config().RenderScaleAuto, value => config().RenderScaleAuto = value,
                () => translate("config.renderscaleauto.name"), () => translate("config.renderscaleauto.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().RenderSharpness, value => config().RenderSharpness = value,
                () => translate("config.rendersharpness.name"), () => translate("config.rendersharpness.tooltip"), 0f, 2f, 0.1f);
            configMenu.AddBoolOption(manifest, () => config().LimitSamplerSlots, value => config().LimitSamplerSlots = value,
                () => translate("config.limitsamplerslots.name"), () => translate("config.limitsamplerslots.tooltip"));

            // --- Misc page: hotkeys + diagnostics + roadmap ---
        }

        /// <summary>The Scale2x doubling: how far it goes and which art families it touches.
        /// Its own page, mirroring the tuner's own tab, because it stopped being one switch.</summary>
        private static void RegisterSmoothingPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config)
        {
            configMenu.AddPage(manifest, "smoothing", () => translate("tuner.tab.smoothing"));
            configMenu.AddBoolOption(manifest, () => config().ZoomAreaFilter, value => config().ZoomAreaFilter = value,
                () => translate("config.zoomareafilter.name"), () => translate("config.zoomareafilter.tooltip"));
            configMenu.AddBoolOption(manifest, () => config().SheetUpscaleEnabled, value => config().SheetUpscaleEnabled = value,
                () => translate("config.sheetupscale.name"), () => translate("config.sheetupscale.tooltip"));
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleStyle.ToString(),
                value => config().SheetUpscaleStyle = Enum.TryParse<SheetSmoothingStyle>(value, out var style) ? style : SheetSmoothingStyle.Scale2x,
                () => translate("config.sheetupscalestyle.name"), () => translate("config.sheetupscalestyle.tooltip"),
                [nameof(SheetSmoothingStyle.Scale2x), nameof(SheetSmoothingStyle.Soft4x)],
                choice => translate($"config.sheetupscalestyle.{choice.ToLowerInvariant()}"));
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleSoftKernel.ToString(),
                value => config().SheetUpscaleSoftKernel = Enum.TryParse<SoftSmoothingKernel>(value, out var kernel) ? kernel : SoftSmoothingKernel.Xbr,
                () => translate("config.sheetupscalekernel.name"), () => translate("config.sheetupscalekernel.tooltip"),
                [nameof(SoftSmoothingKernel.Xbr), nameof(SoftSmoothingKernel.Mmpx), nameof(SoftSmoothingKernel.MmpxEdgeGuarded), nameof(SoftSmoothingKernel.Epx)],
                choice => translate($"config.sheetupscalekernel.{choice.ToLowerInvariant()}"));
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleSteadyRead, value => config().SheetUpscaleSteadyRead = value,
                () => translate("config.sheetupscalesteady.name"), () => translate("config.sheetupscalesteady.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleGradientSmoothing, value => config().SheetUpscaleGradientSmoothing = value,
                () => translate("config.sheetupscalegradients.name"), () => translate("config.sheetupscalegradients.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddSectionTitle(manifest, () => translate("tuner.section.smoothingfamilies"));
            configMenu.AddBoolOption(manifest, () => config().SheetUpscaleWorld, value => config().SheetUpscaleWorld = value,
                () => translate("config.sheetupscaleworld.name"), () => translate("config.sheetupscaleworld.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleSmoothnessWorld, value => config().SheetUpscaleSmoothnessWorld = value,
                () => translate("config.sheetupscaleworld.name") + ": " + translate("config.sheetupscalesmoothness.name"), () => translate("config.sheetupscalesmoothness.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleKernelWorld.ToString(),
                value => config().SheetUpscaleKernelWorld = Enum.TryParse<FamilyKernelChoice>(value, out var chosenWorld) ? chosenWorld : FamilyKernelChoice.SameAsAll,
                () => translate("config.sheetupscaleworld.name") + ": " + translate("config.sheetupscalekernelfamily.name"), () => translate("config.sheetupscalekernelfamily.tooltip"),
                [nameof(FamilyKernelChoice.SameAsAll), nameof(FamilyKernelChoice.Xbr), nameof(FamilyKernelChoice.Mmpx), nameof(FamilyKernelChoice.MmpxEdgeGuarded), nameof(FamilyKernelChoice.Epx)],
                choice => translate($"config.sheetupscalekernelfamily.{choice.ToLowerInvariant()}"));
            configMenu.AddBoolOption(manifest, () => config().SheetUpscaleCharacters, value => config().SheetUpscaleCharacters = value,
                () => translate("config.sheetupscalecharacters.name"), () => translate("config.sheetupscalecharacters.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleSmoothnessCharacters, value => config().SheetUpscaleSmoothnessCharacters = value,
                () => translate("config.sheetupscalecharacters.name") + ": " + translate("config.sheetupscalesmoothness.name"), () => translate("config.sheetupscalesmoothness.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleKernelCharacters.ToString(),
                value => config().SheetUpscaleKernelCharacters = Enum.TryParse<FamilyKernelChoice>(value, out var chosenCharacters) ? chosenCharacters : FamilyKernelChoice.SameAsAll,
                () => translate("config.sheetupscalecharacters.name") + ": " + translate("config.sheetupscalekernelfamily.name"), () => translate("config.sheetupscalekernelfamily.tooltip"),
                [nameof(FamilyKernelChoice.SameAsAll), nameof(FamilyKernelChoice.Xbr), nameof(FamilyKernelChoice.Mmpx), nameof(FamilyKernelChoice.MmpxEdgeGuarded), nameof(FamilyKernelChoice.Epx)],
                choice => translate($"config.sheetupscalekernelfamily.{choice.ToLowerInvariant()}"));
            configMenu.AddBoolOption(manifest, () => config().SheetUpscaleItems, value => config().SheetUpscaleItems = value,
                () => translate("config.sheetupscaleitems.name"), () => translate("config.sheetupscaleitems.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleSmoothnessItems, value => config().SheetUpscaleSmoothnessItems = value,
                () => translate("config.sheetupscaleitems.name") + ": " + translate("config.sheetupscalesmoothness.name"), () => translate("config.sheetupscalesmoothness.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleKernelItems.ToString(),
                value => config().SheetUpscaleKernelItems = Enum.TryParse<FamilyKernelChoice>(value, out var chosenItems) ? chosenItems : FamilyKernelChoice.SameAsAll,
                () => translate("config.sheetupscaleitems.name") + ": " + translate("config.sheetupscalekernelfamily.name"), () => translate("config.sheetupscalekernelfamily.tooltip"),
                [nameof(FamilyKernelChoice.SameAsAll), nameof(FamilyKernelChoice.Xbr), nameof(FamilyKernelChoice.Mmpx), nameof(FamilyKernelChoice.MmpxEdgeGuarded), nameof(FamilyKernelChoice.Epx)],
                choice => translate($"config.sheetupscalekernelfamily.{choice.ToLowerInvariant()}"));
            configMenu.AddBoolOption(manifest, () => config().SheetUpscalePortraits, value => config().SheetUpscalePortraits = value,
                () => translate("config.sheetupscaleportraits.name"), () => translate("config.sheetupscaleportraits.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleSmoothnessPortraits, value => config().SheetUpscaleSmoothnessPortraits = value,
                () => translate("config.sheetupscaleportraits.name") + ": " + translate("config.sheetupscalesmoothness.name"), () => translate("config.sheetupscalesmoothness.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleKernelPortraits.ToString(),
                value => config().SheetUpscaleKernelPortraits = Enum.TryParse<FamilyKernelChoice>(value, out var chosenPortraits) ? chosenPortraits : FamilyKernelChoice.SameAsAll,
                () => translate("config.sheetupscaleportraits.name") + ": " + translate("config.sheetupscalekernelfamily.name"), () => translate("config.sheetupscalekernelfamily.tooltip"),
                [nameof(FamilyKernelChoice.SameAsAll), nameof(FamilyKernelChoice.Xbr), nameof(FamilyKernelChoice.Mmpx), nameof(FamilyKernelChoice.MmpxEdgeGuarded), nameof(FamilyKernelChoice.Epx)],
                choice => translate($"config.sheetupscalekernelfamily.{choice.ToLowerInvariant()}"));
            configMenu.AddBoolOption(manifest, () => config().SheetUpscaleInterface, value => config().SheetUpscaleInterface = value,
                () => translate("config.sheetupscaleinterface.name"), () => translate("config.sheetupscaleinterface.tooltip"));
            configMenu.AddNumberOption(manifest, () => config().SheetUpscaleSmoothnessInterface, value => config().SheetUpscaleSmoothnessInterface = value,
                () => translate("config.sheetupscaleinterface.name") + ": " + translate("config.sheetupscalesmoothness.name"), () => translate("config.sheetupscalesmoothness.tooltip"), 0f, 1f, 0.05f);
            configMenu.AddTextOption(manifest,
                () => config().SheetUpscaleKernelInterface.ToString(),
                value => config().SheetUpscaleKernelInterface = Enum.TryParse<FamilyKernelChoice>(value, out var chosenInterface) ? chosenInterface : FamilyKernelChoice.SameAsAll,
                () => translate("config.sheetupscaleinterface.name") + ": " + translate("config.sheetupscalekernelfamily.name"), () => translate("config.sheetupscalekernelfamily.tooltip"),
                [nameof(FamilyKernelChoice.SameAsAll), nameof(FamilyKernelChoice.Xbr), nameof(FamilyKernelChoice.Mmpx), nameof(FamilyKernelChoice.MmpxEdgeGuarded), nameof(FamilyKernelChoice.Epx)],
                choice => translate($"config.sheetupscalekernelfamily.{choice.ToLowerInvariant()}"));
        }

        /// <summary>Hotkeys, the debug switches, and the roadmap section.</summary>
        private static void RegisterMiscPage(IGenericModConfigMenuApi configMenu, IManifest manifest, Func<string, string> translate, Func<ModConfig> config, IMonitor monitor, Func<RenderPipeline?> getPipeline)
        {
            configMenu.AddPage(manifest, "misc", () => translate("config.section.misc"));
            configMenu.AddSectionTitle(manifest, () => translate("config.section.hotkeys"));
            configMenu.AddKeybindList(manifest, () => config().ToggleKey, value => config().ToggleKey = value,
                () => translate("config.togglekey.name"), () => translate("config.togglekey.tooltip"));
            configMenu.AddKeybindList(manifest, () => config().TunerKey, value => config().TunerKey = value,
                () => translate("config.tunerkey.name"), () => translate("config.tunerkey.tooltip"));
            configMenu.AddKeybindList(manifest, () => config().InspectDrawKey, value => config().InspectDrawKey = value,
                () => translate("config.inspectdrawkey.name"), () => translate("config.inspectdrawkey.tooltip"));

            // --- Diagnostics ---
            //
            // Everything here also exists as a console command, and on a phone the console does not
            // exist: SMAPI on Android has no command line and no keyboard to open the tuner with
            // either. That leaves this menu and config.json as the whole reachable surface, so the
            // three diagnostics a reporter is ever asked for are duplicated into it. It is not only
            // for phones - plenty of people on a desktop have never opened the SMAPI console.
            configMenu.AddSectionTitle(manifest, () => translate("config.section.debug"));
            configMenu.AddBoolOption(manifest, () => config().DebugLogging, value => config().DebugLogging = value,
                () => translate("config.debug.name"), () => translate("config.debug.tooltip"));
            configMenu.AddBoolOption(manifest, () => PerfHud.Visible, value => PerfHud.Visible = value,
                () => translate("tuner.perfhud"), () => translate("help.perfhud"));
            configMenu.AddBoolOption(manifest, () => GpuTimer.Ready, GpuTimer.SetWanted,
                () => translate("tuner.gputime"), () => translate("help.gputime"));
            // A tick box rather than a button, because the API we bind has no button. It reads back
            // as unticked immediately, which is right: it is an action, not a state.
            configMenu.AddBoolOption(manifest,
                () => false,
                value => { if (value) ConsoleCommands.WriteReport(monitor, getPipeline(), config(), alsoLog: true); },
                () => translate("config.report.name"), () => translate("config.report.tooltip"));
        }
    }
}
