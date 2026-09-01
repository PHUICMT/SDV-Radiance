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
            Func<string, string> i18n, Func<ModConfig> config, Action<ModConfig> replaceConfig,
            Action refreshForceBufferDraw, Func<RenderPipeline?> getPipeline)
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

            RegisterLandingPage(api, manifest, i18n, config, monitor, refreshForceBufferDraw);
            RegisterBloomPage(api, manifest, i18n, config);
            RegisterColourGradePage(api, manifest, i18n, config, LutCatalog.Discover());
            RegisterGodRaysPage(api, manifest, i18n, config);
            RegisterFogPage(api, manifest, i18n, config);
            RegisterWeatherPage(api, manifest, i18n, config);
            RegisterParticlesPage(api, manifest, i18n, config);
            RegisterCloudShadowPage(api, manifest, i18n, config);
            RegisterLensPage(api, manifest, i18n, config);
            RegisterWaterPage(api, manifest, i18n, config);
            RegisterLightingPage(api, manifest, i18n, config);
            RegisterWindowsPage(api, manifest, i18n, config);
            RegisterShadowsPage(api, manifest, i18n, config);
            RegisterCameraPage(api, manifest, i18n, config);
            RegisterSmoothingPage(api, manifest, i18n, config);
            RegisterPerformancePage(api, manifest, i18n, config);
            RegisterMiscPage(api, manifest, i18n, config, helper, monitor, getPipeline);
        }

        /// <summary>Master switch, the one-click look preset, and the links to every other page.</summary>
        private static void RegisterLandingPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config, IMonitor monitor, Action refreshForceBufferDraw)
        {
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
            api.AddPageLink(manifest, "smoothing", () => i18n("tuner.tab.smoothing"));
            api.AddPageLink(manifest, "lighting", () => i18n("config.section.lighting"));
            api.AddPageLink(manifest, "windows", () => i18n("config.section.windows"));
            api.AddPageLink(manifest, "shadows", () => i18n("config.section.shadows"));
            api.AddPageLink(manifest, "godrays", () => i18n("config.section.godrays"));
            api.AddPageLink(manifest, "water", () => i18n("config.section.water"));
            api.AddPageLink(manifest, "cloudshadow", () => i18n("config.section.cloudshadow"));
            api.AddPageLink(manifest, "fog", () => i18n("config.section.fog"));
            api.AddPageLink(manifest, "weather", () => i18n("config.section.weather"));
            api.AddPageLink(manifest, "particles", () => i18n("config.section.particles"));
            api.AddPageLink(manifest, "camera", () => i18n("config.section.camera"));
            api.AddPageLink(manifest, "misc", () => i18n("config.section.misc"));

            // --- Bloom (implemented) ---
        }

        /// <summary>Bloom.</summary>
        private static void RegisterBloomPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "bloom", () => i18n("config.section.bloom"));
            api.AddBoolOption(manifest, () => config().BloomEnabled, v => config().BloomEnabled = v,
                () => i18n("config.bloom.enabled.name"), () => i18n("config.bloom.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().BloomThreshold, v => config().BloomThreshold = v,
                () => i18n("config.bloom.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().BloomIntensity, v => config().BloomIntensity = v,
                () => i18n("config.bloom.intensity.name"), null, 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().BloomEmissiveBoost, v => config().BloomEmissiveBoost = v,
                () => i18n("config.bloom.emissiveboost.name"),
                () => i18n("config.bloom.emissiveboost.tooltip"), 0f, 1f, 0.05f);

            // --- Color grading (implemented) ---
        }

        /// <summary>Colour grading, tonemapping and the blue-light filter.</summary>
        /// <param name="userLuts">Looks found in assets/luts that did not ship with the mod. Empty
        /// for almost everyone, and when it is empty the dropdown is exactly what it always was.</param>
        private static void RegisterColourGradePage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config, string[] userLuts)
        {
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
            var mine = new HashSet<string>(userLuts, StringComparer.OrdinalIgnoreCase);
            api.AddTextOption(manifest,
                () => config().ColorGradeLut,
                v => config().ColorGradeLut = v ?? "",
                () => i18n("config.colorgrade.lut.name"), () => i18n("config.colorgrade.lut.tooltip"),
                choices.ToArray(),
                v => v.Length == 0 ? i18n("config.colorgrade.lut.none")
                     : mine.Contains(v) ? $"{v} ({i18n("config.colorgrade.lut.yours")})"
                     : Array.IndexOf(ModConfig.ShippedLuts, v) >= 0 ? i18n($"config.colorgrade.lut.{v}")
                     : $"{v} ({i18n("config.colorgrade.lut.missing")})");
            api.AddNumberOption(manifest, () => config().ColorGradeLutAmount, v => config().ColorGradeLutAmount = v,
                () => i18n("config.colorgrade.lutamount.name"), () => i18n("config.colorgrade.lutamount.tooltip"), 0f, 1f, 0.05f);

            api.AddNumberOption(manifest, () => config().BlueLightFilter, v => config().BlueLightFilter = v,
                () => i18n("config.colorgrade.bluelight.name"), () => i18n("config.colorgrade.bluelight.tooltip"), 0f, 1f, 0.05f);

            // --- God rays (implemented) ---
        }

        /// <summary>God rays. Ships off by default.</summary>
        private static void RegisterGodRaysPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "godrays", () => i18n("config.section.godrays"));
            api.AddSectionTitle(manifest, () => i18n("config.godrays.sectionlamps"));
            api.AddBoolOption(manifest, () => config().GodRaysEnabled, v => config().GodRaysEnabled = v,
                () => i18n("config.godrays.enabled.name"), () => i18n("config.godrays.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().GodRaysIntensity, v => config().GodRaysIntensity = v,
                () => i18n("config.godrays.intensity.name"), null, 0f, 2f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.godrays.sectionsun"));
            api.AddBoolOption(manifest, () => config().GodRaysSun, v => config().GodRaysSun = v,
                () => i18n("config.godrays.sun.name"), () => i18n("config.godrays.sun.tooltip"));
            api.AddNumberOption(manifest, () => config().GodRaysSunIntensity, v => config().GodRaysSunIntensity = v,
                () => i18n("config.godrays.sunintensity.name"), () => i18n("config.godrays.sunintensity.tooltip"), 0f, 1.5f, 0.05f);
            api.AddNumberOption(manifest, () => config().GodRaysSunReach, v => config().GodRaysSunReach = v,
                () => i18n("config.godrays.sunreach.name"), () => i18n("config.godrays.sunreach.tooltip"), 0.1f, 1f, 0.05f);

            // --- Volumetric fog (implemented) ---
        }

        /// <summary>Fog, and the separate night mist that runs on the same machinery.</summary>
        private static void RegisterFogPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
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
            api.AddSectionTitle(manifest, () => i18n("config.fog.sectionboth"));
            api.AddNumberOption(manifest, () => config().FogTopBias, v => config().FogTopBias = v,
                () => i18n("config.fog.topbias.name"), () => i18n("config.fog.topbias.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FogNightMistSpeed, v => config().FogNightMistSpeed = v,
                () => i18n("config.fog.nightmistspeed.name"), null, 0f, 0.1f, 0.002f);
            api.AddSectionTitle(manifest, () => i18n("config.heathaze.name"));
            api.AddBoolOption(manifest, () => config().HeatHazeEnabled, v => config().HeatHazeEnabled = v,
                () => i18n("config.heathaze.name"), () => i18n("config.heathaze.tooltip"));
            api.AddNumberOption(manifest, () => config().HeatHazeStrength, v => config().HeatHazeStrength = v,
                () => i18n("config.heathaze.strength.name"), () => i18n("config.heathaze.strength.tooltip"), 0f, 2f, 0.05f);

            // --- Cloud shadows (implemented) ---
        }

        /// <summary>Weather: the replacement rain and snow, drawn in the game's own weather slot.</summary>
        private static void RegisterWeatherPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "weather", () => i18n("config.section.weather"));
            api.AddBoolOption(manifest, () => config().AuroraEnabled, v => config().AuroraEnabled = v,
                () => i18n("config.weather.aurora.name"), () => i18n("config.weather.aurora.tooltip"));
            api.AddNumberOption(manifest, () => config().AuroraStrength, v => config().AuroraStrength = v,
                () => i18n("config.weather.aurorastrength.name"), () => i18n("config.weather.aurorastrength.tooltip"), 0f, 2f, 0.1f);
            api.AddBoolOption(manifest, () => config().ShootingStarsEnabled, v => config().ShootingStarsEnabled = v,
                () => i18n("config.weather.shootingstars.name"), () => i18n("config.weather.shootingstars.tooltip"));
            api.AddBoolOption(manifest, () => config().FoliageSwayEnabled, v => config().FoliageSwayEnabled = v,
                () => i18n("config.weather.foliagesway.name"), () => i18n("config.weather.foliagesway.tooltip"));
            api.AddNumberOption(manifest, () => config().FoliageSwayStrength, v => config().FoliageSwayStrength = v,
                () => i18n("config.weather.foliageswaystrength.name"), () => i18n("config.weather.foliageswaystrength.tooltip"), 0f, 2f, 0.1f);
            api.AddNumberOption(manifest, () => config().FoliageSwaySpeed, v => config().FoliageSwaySpeed = v,
                () => i18n("config.weather.foliageswayspeed.name"), () => i18n("config.weather.foliageswayspeed.tooltip"), 0.25f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().FoliageSwayGustSpan, v => config().FoliageSwayGustSpan = v,
                () => i18n("config.weather.foliageswaygustspan.name"), () => i18n("config.weather.foliageswaygustspan.tooltip"), 4f, 40f, 1f);
            api.AddBoolOption(manifest, () => config().PrecipitationEnabled, v => config().PrecipitationEnabled = v,
                () => i18n("config.precipitation.enabled.name"), () => i18n("config.precipitation.enabled.tooltip"));
            api.AddSectionTitle(manifest, () => i18n("config.precipitation.rain.name"));
            api.AddBoolOption(manifest, () => config().PrecipitationRain, v => config().PrecipitationRain = v,
                () => i18n("config.precipitation.rain.name"), () => i18n("config.precipitation.rain.tooltip"));
            api.AddNumberOption(manifest, () => config().PrecipitationRainDensity, v => config().PrecipitationRainDensity = v,
                () => i18n("config.precipitation.density.name"), () => i18n("config.precipitation.density.tooltip"), 0.25f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationRainSize, v => config().PrecipitationRainSize = v,
                () => i18n("config.precipitation.size.name"), () => i18n("config.precipitation.size.tooltip"), 0.5f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationRainOpacity, v => config().PrecipitationRainOpacity = v,
                () => i18n("config.precipitation.opacity.name"), () => i18n("config.precipitation.opacity.tooltip"), 0.25f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationStormDensity, v => config().PrecipitationStormDensity = v,
                () => i18n("config.precipitation.stormdensity.name"), () => i18n("config.precipitation.stormdensity.tooltip"), 1f, 3f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationRainSlant, v => config().PrecipitationRainSlant = v,
                () => i18n("config.precipitation.rainslant.name"), () => i18n("config.precipitation.rainslant.tooltip"), 0f, 3f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.precipitation.snow.name"));
            api.AddBoolOption(manifest, () => config().PrecipitationSnow, v => config().PrecipitationSnow = v,
                () => i18n("config.precipitation.snow.name"), () => i18n("config.precipitation.snow.tooltip"));
            api.AddNumberOption(manifest, () => config().PrecipitationSnowDensity, v => config().PrecipitationSnowDensity = v,
                () => i18n("config.precipitation.density.name"), () => i18n("config.precipitation.density.tooltip"), 0.25f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationSnowSize, v => config().PrecipitationSnowSize = v,
                () => i18n("config.precipitation.size.name"), () => i18n("config.precipitation.size.tooltip"), 0.5f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationSnowOpacity, v => config().PrecipitationSnowOpacity = v,
                () => i18n("config.precipitation.opacity.name"), () => i18n("config.precipitation.opacity.tooltip"), 0.25f, 2f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.precipitation.wind.name"));
            api.AddBoolOption(manifest, () => config().PrecipitationWind, v => config().PrecipitationWind = v,
                () => i18n("config.precipitation.wind.name"), () => i18n("config.precipitation.wind.tooltip"));
            api.AddNumberOption(manifest, () => config().PrecipitationWindDensity, v => config().PrecipitationWindDensity = v,
                () => i18n("config.precipitation.density.name"), () => i18n("config.precipitation.density.tooltip"), 0.25f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationWindSize, v => config().PrecipitationWindSize = v,
                () => i18n("config.precipitation.size.name"), () => i18n("config.precipitation.size.tooltip"), 0.5f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationWindOpacity, v => config().PrecipitationWindOpacity = v,
                () => i18n("config.precipitation.opacity.name"), () => i18n("config.precipitation.opacity.tooltip"), 0.25f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().PrecipitationWindSlant, v => config().PrecipitationWindSlant = v,
                () => i18n("config.precipitation.windslant.name"), () => i18n("config.precipitation.windslant.tooltip"), 0.25f, 3f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.lightning.name"));
            api.AddBoolOption(manifest, () => config().LightningEffectsEnabled, v => config().LightningEffectsEnabled = v,
                () => i18n("config.lightning.name"), () => i18n("config.lightning.tooltip"));
            api.AddBoolOption(manifest, () => config().LightningBoltsEnabled, v => config().LightningBoltsEnabled = v,
                () => i18n("config.lightningbolts.name"), () => i18n("config.lightningbolts.tooltip"));
            // See the note in the tuner: the wet GROUND is off and out of both menus until its
            // puddles can be placed from the map rather than guessed at.
            api.AddSectionTitle(manifest, () => i18n("config.wetworld.sectiondrops"));
            api.AddBoolOption(manifest, () => config().WetWorldLensDrops, v => config().WetWorldLensDrops = v,
                () => i18n("config.wetworld.lensdrops.name"), () => i18n("config.wetworld.lensdrops.tooltip"));
            api.AddNumberOption(manifest, () => config().WetWorldLensDropSize, v => config().WetWorldLensDropSize = v,
                () => i18n("config.wetworld.lensdropsize.name"), () => i18n("config.wetworld.lensdropsize.tooltip"), 0.5f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WetWorldEdgeHaze, v => config().WetWorldEdgeHaze = v,
                () => i18n("config.wetworld.edgehaze.name"), () => i18n("config.wetworld.edgehaze.tooltip"), 0f, 2f, 0.05f);
        }

        /// <summary>Particles: the pool that drifts, rises and glows in the world itself.</summary>
        private static void RegisterParticlesPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "particles", () => i18n("config.section.particles"));
            api.AddBoolOption(manifest, () => config().ParticlesEnabled, v => config().ParticlesEnabled = v,
                () => i18n("config.particles.enabled.name"), () => i18n("config.particles.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().ParticleDensity, v => config().ParticleDensity = v,
                () => i18n("config.particles.density.name"), () => i18n("config.particles.density.tooltip"), 0.25f, 2f, 0.05f);

            AddParticleEmitter(api, manifest, i18n, "dust",
                () => config().ParticleDust, v => config().ParticleDust = v,
                () => config().ParticleDustAmount, v => config().ParticleDustAmount = v,
                () => config().ParticleDustSize, v => config().ParticleDustSize = v);
            AddParticleEmitter(api, manifest, i18n, "embers",
                () => config().ParticleEmbers, v => config().ParticleEmbers = v,
                () => config().ParticleEmbersAmount, v => config().ParticleEmbersAmount = v,
                () => config().ParticleEmbersSize, v => config().ParticleEmbersSize = v);
            AddParticleEmitter(api, manifest, i18n, "fireflies",
                () => config().ParticleFireflies, v => config().ParticleFireflies = v,
                () => config().ParticleFirefliesAmount, v => config().ParticleFirefliesAmount = v,
                () => config().ParticleFirefliesSize, v => config().ParticleFirefliesSize = v);
            AddParticleEmitter(api, manifest, i18n, "petals",
                () => config().ParticlePetals, v => config().ParticlePetals = v,
                () => config().ParticlePetalsAmount, v => config().ParticlePetalsAmount = v,
                () => config().ParticlePetalsSize, v => config().ParticlePetalsSize = v);
            // Only the flat things buckle, so this one setting sits with them rather than with the
            // particles as a whole.
            api.AddNumberOption(manifest, () => config().ParticlePetalsFlutter, v => config().ParticlePetalsFlutter = v,
                () => i18n("config.particles.petalsflutter.name"), () => i18n("config.particles.petalsflutter.tooltip"),
                0f, 1f, 0.05f);
            AddParticleEmitter(api, manifest, i18n, "ringsparkles",
                () => config().ParticleRingSparkles, v => config().ParticleRingSparkles = v,
                () => config().ParticleRingSparklesAmount, v => config().ParticleRingSparklesAmount = v,
                () => config().ParticleRingSparklesSize, v => config().ParticleRingSparklesSize = v);
            AddParticleEmitter(api, manifest, i18n, "waterfallmist",
                () => config().ParticleWaterfallMist, v => config().ParticleWaterfallMist = v,
                () => config().ParticleWaterfallMistAmount, v => config().ParticleWaterfallMistAmount = v,
                () => config().ParticleWaterfallMistSize, v => config().ParticleWaterfallMistSize = v);
            AddParticleEmitter(api, manifest, i18n, "hotspringsteam",
                () => config().ParticleHotSpringSteam, v => config().ParticleHotSpringSteam = v,
                () => config().ParticleHotSpringSteamAmount, v => config().ParticleHotSpringSteamAmount = v,
                () => config().ParticleHotSpringSteamSize, v => config().ParticleHotSpringSteamSize = v);
            AddParticleEmitter(api, manifest, i18n, "lavasparks",
                () => config().ParticleLavaSparks, v => config().ParticleLavaSparks = v,
                () => config().ParticleLavaSparksAmount, v => config().ParticleLavaSparksAmount = v,
                () => config().ParticleLavaSparksSize, v => config().ParticleLavaSparksSize = v);

            // --- Cloud shadows (implemented) ---
        }

        /// <summary>One emitter's three settings: whether it runs, how much of it there is, and
        /// how big each piece is. Every emitter gets the same three, so adding one is a call here
        /// rather than another dozen lines that have to agree with the other dozen.
        /// <para>The amount and size labels are shared across emitters on purpose: they mean
        /// exactly the same thing every time, and a translator should not be asked to write "how
        /// many" six times.</para></summary>
        private static void AddParticleEmitter(IGenericModConfigMenuApi api, IManifest manifest,
            Func<string, string> i18n, string emitter,
            Func<bool> getOn, Action<bool> setOn,
            Func<float> getAmount, Action<float> setAmount,
            Func<float> getSize, Action<float> setSize)
        {
            api.AddSectionTitle(manifest, () => i18n($"config.particles.{emitter}.name"));
            api.AddBoolOption(manifest, getOn, setOn,
                () => i18n($"config.particles.{emitter}.name"), () => i18n($"config.particles.{emitter}.tooltip"));
            api.AddNumberOption(manifest, getAmount, setAmount,
                () => i18n("config.particles.amount.name"), () => i18n("config.particles.amount.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, getSize, setSize,
                () => i18n("config.particles.size.name"), () => i18n("config.particles.size.tooltip"), 0.5f, 2f, 0.05f);
        }

        /// <summary>Cloud shadows, including hiding the vanilla ones.</summary>
        private static void RegisterCloudShadowPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
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
        }

        /// <summary>Lens effects: tilt shift, vignette, chromatic aberration.</summary>
        private static void RegisterLensPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
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
            api.AddNumberOption(manifest, () => config().TiltShiftIndoorAmount, v => config().TiltShiftIndoorAmount = v,
                () => i18n("config.tiltshift.indoor.name"), () => i18n("config.tiltshift.indoor.tooltip"), 0f, 1f, 0.05f);
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
        }

        /// <summary>Water surface and reflections.</summary>
        private static void RegisterWaterPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
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
            api.AddBoolOption(manifest, () => config().WaterCausticsEnabled, v => config().WaterCausticsEnabled = v,
                () => i18n("config.water.caustics.name"), () => i18n("config.water.caustics.tooltip"));
            api.AddNumberOption(manifest, () => config().WaterCausticsStrength, v => config().WaterCausticsStrength = v,
                () => i18n("config.water.causticsstrength.name"), null, 0f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().WaterReflection, v => config().WaterReflection = v,
                () => i18n("config.water.reflection.name"), () => i18n("config.water.reflection.tooltip"));
            api.AddNumberOption(manifest, () => config().WaterReflectStrength, v => config().WaterReflectStrength = v,
                () => i18n("config.water.reflectstrength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterReflectDistort, v => config().WaterReflectDistort = v,
                () => i18n("config.water.reflectdistort.name"), () => i18n("config.water.reflectdistort.tooltip"),
                0f, 1.5f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterReflectBanding, v => config().WaterReflectBanding = v,
                () => i18n("config.water.reflectbanding.name"), () => i18n("config.water.reflectbanding.tooltip"),
                0f, 16f, 1f);
            api.AddNumberOption(manifest, () => config().WaterReflectBlur, v => config().WaterReflectBlur = v,
                () => i18n("config.water.reflectblur.name"), () => i18n("config.water.reflectblur.tooltip"),
                0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterReflectDepth, v => config().WaterReflectDepth = v,
                () => i18n("config.water.reflectdepth.name"), () => i18n("config.water.reflectdepth.tooltip"),
                0.1f, 1.5f, 0.05f);
            // Reach has been in the config file since 1.5.6 and in no menu, which is the same as
            // not existing for almost everybody who might want it.
            api.AddNumberOption(manifest, () => config().WaterReflectReach, v => config().WaterReflectReach = v,
                () => i18n("config.water.reflectreach.name"), () => i18n("config.water.reflectreach.tooltip"),
                0.2f, 1f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.water.sectionrain"));
            api.AddNumberOption(manifest, () => config().WaterRainRingDensity, v => config().WaterRainRingDensity = v,
                () => i18n("config.water.rainringdensity.name"), () => i18n("config.water.rainringdensity.tooltip"),
                0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterRainRingSize, v => config().WaterRainRingSize = v,
                () => i18n("config.water.rainringsize.name"), () => i18n("config.water.rainringsize.tooltip"),
                0.4f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterRainRingStrength, v => config().WaterRainRingStrength = v,
                () => i18n("config.water.rainringstrength.name"), () => i18n("config.water.rainringstrength.tooltip"),
                0f, 2f, 0.05f);
            // Reflection REACH and FADE ROWS are not offered here. They buy frames, they do not
            // change how anything looks, and the performance preset already sets both: a player
            // who moves them sees nothing happen and concludes the mod is broken. The settings
            // still exist for radiance_config, which is where an A/B belongs.
            // Which water, then the classic water's look. This menu cannot hide rows by the
            // water in use the way the tuner does, so each water's dials sit under a heading
            // that says when they apply.
            api.AddTextOption(manifest,
                () => config().WaterReflectModel.ToString(),
                v => config().WaterReflectModel = Enum.TryParse<WaterReflectionModel>(v, out var model) ? model : WaterReflectionModel.Modern,
                () => i18n("config.water.model.name"), () => i18n("config.water.model.tooltip"),
                new[] { nameof(WaterReflectionModel.Modern), nameof(WaterReflectionModel.Classic) },
                v => i18n($"config.water.model.{v.ToLowerInvariant()}"));
            api.AddSectionTitle(manifest, () => i18n("config.water.classic.title"), () => i18n("config.water.classic.tooltip"));
            api.AddTextOption(manifest,
                () => config().WaterReflectStyle.ToString(),
                v => config().WaterReflectStyle = Enum.TryParse<WaterReflectionStyle>(v, out var rs) ? rs : WaterReflectionStyle.Natural,
                () => i18n("config.water.reflstyle.name"), () => i18n("config.water.reflstyle.tooltip"),
                new[] { "StillWater", "Natural", "Choppy" });
            api.AddSectionTitle(manifest, () => i18n("config.water.modern.title"), () => i18n("config.water.modern.tooltip"));
            api.AddNumberOption(manifest, () => config().WaterModernWobble, v => config().WaterModernWobble = v,
                () => i18n("config.water.modernwobble.name"), () => i18n("config.water.modernwobble.tooltip"),
                0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterModernChoppiness, v => config().WaterModernChoppiness = v,
                () => i18n("config.water.modernchoppiness.name"), () => i18n("config.water.modernchoppiness.tooltip"),
                0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterModernParallax, v => config().WaterModernParallax = v,
                () => i18n("config.water.modernparallax.name"), () => i18n("config.water.modernparallax.tooltip"),
                0f, 0.3f, 0.01f);
            api.AddNumberOption(manifest, () => config().WaterModernFresnel, v => config().WaterModernFresnel = v,
                () => i18n("config.water.modernfresnel.name"), () => i18n("config.water.modernfresnel.tooltip"),
                0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterModernStretch, v => config().WaterModernStretch = v,
                () => i18n("config.water.modernstretch.name"), () => i18n("config.water.modernstretch.tooltip"),
                1f, 1.4f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterModernEdgeSoftness, v => config().WaterModernEdgeSoftness = v,
                () => i18n("config.water.modernedgesoftness.name"), () => i18n("config.water.modernedgesoftness.tooltip"),
                0f, 6f, 0.25f);
            api.AddNumberOption(manifest, () => config().WaterModernPlungeChurn, v => config().WaterModernPlungeChurn = v,
                () => i18n("config.water.modernplungechurn.name"), () => i18n("config.water.modernplungechurn.tooltip"),
                0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().WaterModernPlungeReach, v => config().WaterModernPlungeReach = v,
                () => i18n("config.water.modernplungereach.name"), () => i18n("config.water.modernplungereach.tooltip"),
                1f, 6f, 0.5f);
            api.AddNumberOption(manifest, () => config().WaterModernLipFade, v => config().WaterModernLipFade = v,
                () => i18n("config.water.modernlipfade.name"), () => i18n("config.water.modernlipfade.tooltip"),
                0f, 1.5f, 0.05f);
            api.AddBoolOption(manifest, () => config().WaterEffectIndoors, v => config().WaterEffectIndoors = v,
                () => i18n("config.water.indoors.name"), () => i18n("config.water.indoors.tooltip"));

            // --- Dynamic lighting (implemented) ---
        }

        /// <summary>The flood grid, the light pools and the window effects.</summary>
        private static void RegisterLightingPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "lighting", () => i18n("config.section.lighting"));
            api.AddBoolOption(manifest, () => config().FloodLightingEnabled, v => config().FloodLightingEnabled = v,
                () => i18n("config.lighting.flood.name"), () => i18n("config.lighting.flood.tooltip"));
            api.AddTextOption(manifest,
                () => config().FloodGiModel.ToString(),
                v => config().FloodGiModel = Enum.TryParse<GiModel>(v, out var model) ? model : GiModel.Flood,
                () => i18n("config.lighting.gimodel.name"), () => i18n("config.lighting.gimodel.tooltip"),
                new[] { nameof(GiModel.Flood), nameof(GiModel.Cascades) },
                v => i18n($"config.lighting.gimodel.{v.ToLowerInvariant()}"));
            api.AddBoolOption(manifest, () => config().SpriteReliefEnabled, v => config().SpriteReliefEnabled = v,
                () => i18n("config.lighting.relief.name"), () => i18n("config.lighting.relief.tooltip"));
            api.AddNumberOption(manifest, () => config().SpriteReliefStrength, v => config().SpriteReliefStrength = v,
                () => i18n("config.lighting.reliefstrength.name"), () => i18n("config.lighting.reliefstrength.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().SpriteReliefSun, v => config().SpriteReliefSun = v,
                () => i18n("config.lighting.reliefsun.name"), () => i18n("config.lighting.reliefsun.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().SpriteReliefRim, v => config().SpriteReliefRim = v,
                () => i18n("config.lighting.reliefrim.name"), () => i18n("config.lighting.reliefrim.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().SpriteReliefLeafShimmer, v => config().SpriteReliefLeafShimmer = v,
                () => i18n("config.lighting.leafshimmer.name"), () => i18n("config.lighting.leafshimmer.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FloodLightingStrength, v => config().FloodLightingStrength = v,
                () => i18n("config.lighting.floodstrength.name"), () => i18n("config.lighting.floodstrength.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FloodColourBleed, v => config().FloodColourBleed = v,
                () => i18n("config.lighting.colourbleed.name"), () => i18n("config.lighting.colourbleed.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().FloodShadowStrength, v => config().FloodShadowStrength = v,
                () => i18n("config.lighting.floodshadow.name"), () => i18n("config.lighting.floodshadow.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightShadowCarve, v => config().LightShadowCarve = v,
                () => i18n("config.lighting.shadowcarve.name"), () => i18n("config.lighting.shadowcarve.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightShadowSoftness, v => config().LightShadowSoftness = v,
                () => i18n("config.lighting.shadowsoftness.name"), () => i18n("config.lighting.shadowsoftness.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightShadowDetail, v => config().LightShadowDetail = v,
                () => i18n("config.lighting.shadowdetail.name"), () => i18n("config.lighting.shadowdetail.tooltip"), 0f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().LightShadowDetailShared, v => config().LightShadowDetailShared = v,
                () => i18n("config.lighting.shadowshared.name"), () => i18n("config.lighting.shadowshared.tooltip"));
            api.AddBoolOption(manifest, () => config().LightingEnabled, v => config().LightingEnabled = v,
                () => i18n("config.lighting.enabled.name"), () => i18n("config.lighting.enabled.tooltip"));
            api.AddNumberOption(manifest, () => config().LightingIndoorDarkness, v => config().LightingIndoorDarkness = v,
                () => i18n("config.lighting.indoor.name"), () => i18n("config.lighting.indoor.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingNightDarkness, v => config().LightingNightDarkness = v,
                () => i18n("config.lighting.night.name"), () => i18n("config.lighting.night.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingMorningDarkness, v => config().LightingMorningDarkness = v,
                () => i18n("config.lighting.morning.name"), () => i18n("config.lighting.morning.tooltip"), 0f, 0.95f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingIndoorColourWalk, v => config().LightingIndoorColourWalk = v,
                () => i18n("config.lighting.indoorcolour.name"), () => i18n("config.lighting.indoorcolour.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().LightingMorningClearSkyCool, v => config().LightingMorningClearSkyCool = v,
                () => i18n("config.lighting.morningcool.name"), () => i18n("config.lighting.morningcool.tooltip"), 0f, 1f, 0.05f);
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
            api.AddBoolOption(manifest, () => config().LightShadowSilhouettes, v => config().LightShadowSilhouettes = v,
                () => i18n("config.lighting.silhouettes.name"), () => i18n("config.lighting.silhouettes.tooltip"));
            api.AddBoolOption(manifest, () => config().LightShadowProps, v => config().LightShadowProps = v,
                () => i18n("config.lighting.props.name"), () => i18n("config.lighting.props.tooltip"));

            // --- Directional sprite shadows ---
        }

        /// <summary>Directional sprite shadows.</summary>
        /// <summary>Everything the mod does with a window, on one page: the daylight it lets in,
        /// the beam you can see, the glow after dusk, and the people in the glass by day. It lived
        /// inside Lighting, where four window rows among fifteen lighting rows were easy to miss
        /// and hard to explain as one thing.</summary>
        private static void RegisterWindowsPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "windows", () => i18n("config.section.windows"));
            // Two things a window does, kept apart on the page: the light it lets through, and
            // the picture it returns.
            api.AddSectionTitle(manifest, () => i18n("config.windows.sectionlight"));
            api.AddBoolOption(manifest, () => config().WindowEffectsEnabled, v => config().WindowEffectsEnabled = v,
                () => i18n("config.lighting.windoweffects.name"), () => i18n("config.lighting.windoweffects.tooltip"));
            api.AddBoolOption(manifest, () => config().WindowBeamEnabled, v => config().WindowBeamEnabled = v,
                () => i18n("config.lighting.windowbeam.name"), () => i18n("config.lighting.windowbeam.tooltip"));
            api.AddNumberOption(manifest, () => config().WindowDaylightStrength, v => config().WindowDaylightStrength = v,
                () => i18n("config.lighting.windowdaylightstrength.name"),
                () => i18n("config.lighting.windowdaylightstrength.tooltip"), 0f, 2f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.windows.sectionreflection"));
            api.AddBoolOption(manifest, () => config().WindowReflectionEnabled, v => config().WindowReflectionEnabled = v,
                () => i18n("config.lighting.windowreflection.name"), () => i18n("config.lighting.windowreflection.tooltip"));
            api.AddNumberOption(manifest, () => config().WindowReflectionStrength, v => config().WindowReflectionStrength = v,
                () => i18n("config.lighting.windowreflectionstrength.name"),
                () => i18n("config.lighting.windowreflectionstrength.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WindowReflectionNightStrength, v => config().WindowReflectionNightStrength = v,
                () => i18n("config.lighting.windowreflectionnight.name"),
                () => i18n("config.lighting.windowreflectionnight.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WindowSheenStrength, v => config().WindowSheenStrength = v,
                () => i18n("config.lighting.windowsheen.name"),
                () => i18n("config.lighting.windowsheen.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WindowSceneReflectionStrength, v => config().WindowSceneReflectionStrength = v,
                () => i18n("config.lighting.windowscene.name"),
                () => i18n("config.lighting.windowscene.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WindowGlareStrength, v => config().WindowGlareStrength = v,
                () => i18n("config.lighting.windowglare.name"),
                () => i18n("config.lighting.windowglare.tooltip"), 0f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().WindowLightGlowStrength, v => config().WindowLightGlowStrength = v,
                () => i18n("config.lighting.windowlightglow.name"),
                () => i18n("config.lighting.windowlightglow.tooltip"), 0f, 2f, 0.05f);
        }

        /// <summary>One slider per caster kind, named by the shared key pattern
        /// <c>config.shadows.{family}.{kind}.name</c>. The three per-kind families (length,
        /// softness, lean) are eighteen sliders of exactly this shape; the table keeps a kind
        /// from being added to one family and forgotten in another.</summary>
        /// <summary>One caster kind's three dials under its own heading. GMCM cannot hide rows
        /// behind a picker the way the tuner tab does, so the flat list is regrouped instead: the
        /// kind is the heading and its length, softness and lean follow it, rather than three
        /// blocks of seven with a building's three dials eight rows apart.</summary>
        private static void AddKindDials(IGenericModConfigMenuApi api, IManifest manifest,
            Func<string, string> i18n, string kind,
            Func<float> getLength, Action<float> setLength,
            Func<float> getSoftness, Action<float> setSoftness,
            Func<float> getLean, Action<float> setLean)
        {
            // The heading reuses the name the length block already carried, so no kind was
            // renamed and no translator has to look at this again.
            api.AddSectionTitle(manifest, () => i18n($"config.shadows.length.{kind}.name"));
            api.AddNumberOption(manifest, getLength, setLength,
                () => i18n("tuner.shadowkind.length"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddNumberOption(manifest, getSoftness, setSoftness,
                () => i18n("tuner.shadowkind.softness"), () => i18n("config.shadows.softness.tooltip"),
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddNumberOption(manifest, getLean, setLean,
                () => i18n("tuner.shadowkind.lean"), () => i18n("config.shadows.lean.tooltip"),
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
        }

        private static void RegisterShadowsPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "shadows", () => i18n("config.section.shadows"));
            api.AddBoolOption(manifest, () => config().DirectionalShadowsEnabled, v => config().DirectionalShadowsEnabled = v,
                () => i18n("config.shadows.enabled.name"), () => i18n("config.shadows.enabled.tooltip"));
            // Which shapes, named by the version each shipped in, exactly as the water is.
            api.AddTextOption(manifest,
                () => config().DirectionalShadowModel.ToString(),
                v => config().DirectionalShadowModel = Enum.TryParse<ShadowModel>(v, out var model) ? model : ShadowModel.Modern,
                () => i18n("config.shadows.model.name"), () => i18n("config.shadows.model.tooltip"),
                new[] { nameof(ShadowModel.Modern), nameof(ShadowModel.Classic) },
                v => i18n($"config.shadows.model.{v.ToLowerInvariant()}"));
            api.AddNumberOption(manifest, () => config().DirectionalShadowStrength, v => config().DirectionalShadowStrength = v,
                () => i18n("config.shadows.strength.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().DirectionalShadowLength, v => config().DirectionalShadowLength = v,
                () => i18n("config.shadows.length.name"), null, 0.2f, 2f, 0.05f);
            api.AddNumberOption(manifest, () => config().GoldenHourStrength, v => config().GoldenHourStrength = v,
                () => i18n("config.shadows.goldenhour.name"),
                () => i18n("config.shadows.goldenhour.tooltip"), 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().DirectionalShadowBlur, v => config().DirectionalShadowBlur = v,
                () => i18n("config.shadows.blur.name"), null, 0f, 5f, 0.5f);
            api.AddBoolOption(manifest, () => config().DirectionalShadowObjects, v => config().DirectionalShadowObjects = v,
                () => i18n("config.shadows.objects.name"), () => i18n("config.shadows.objects.tooltip"));
            api.AddBoolOption(manifest, () => config().DirectionalShadowBuildings, v => config().DirectionalShadowBuildings = v,
                () => i18n("config.shadows.buildings.name"), () => i18n("config.shadows.buildings.tooltip"));
            api.AddNumberOption(manifest, () => config().ShadowGroundForeshortening, v => config().ShadowGroundForeshortening = v,
                () => i18n("config.shadows.groundforeshortening.name"), () => i18n("config.shadows.groundforeshortening.tooltip"),
                ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowCharacterGroundForeshortening, v => config().ShadowCharacterGroundForeshortening = v,
                () => i18n("config.shadows.charactergroundforeshortening.name"), () => i18n("config.shadows.charactergroundforeshortening.tooltip"),
                ModConfig.ShadowGroundForeshorteningMin, ModConfig.ShadowGroundForeshorteningMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowCastsPerCharacter, v => config().ShadowCastsPerCharacter = v,
                () => i18n("config.shadows.casts.name"), () => i18n("config.shadows.casts.tooltip"),
                ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax, 1);

            // Per kind, grouped by the kind rather than by the dial. The overall length and
            // softness above still multiply these, so a player who only wants everything shorter
            // never has to come down here at all.
            api.AddSectionTitle(manifest, () => i18n("config.shadows.perkind.title"),
                () => i18n("config.shadows.perkind.tooltip"));
            AddKindDials(api, manifest, i18n, "trees",
                () => config().ShadowLengthTrees, v => config().ShadowLengthTrees = v,
                () => config().ShadowSoftnessTrees, v => config().ShadowSoftnessTrees = v,
                () => config().ShadowLeanTrees, v => config().ShadowLeanTrees = v);
            AddKindDials(api, manifest, i18n, "smalltrees",
                () => config().ShadowLengthSmallTrees, v => config().ShadowLengthSmallTrees = v,
                () => config().ShadowSoftnessSmallTrees, v => config().ShadowSoftnessSmallTrees = v,
                () => config().ShadowLeanSmallTrees, v => config().ShadowLeanSmallTrees = v);
            AddKindDials(api, manifest, i18n, "bushes",
                () => config().ShadowLengthBushes, v => config().ShadowLengthBushes = v,
                () => config().ShadowSoftnessBushes, v => config().ShadowSoftnessBushes = v,
                () => config().ShadowLeanBushes, v => config().ShadowLeanBushes = v);
            AddKindDials(api, manifest, i18n, "crops",
                () => config().ShadowLengthCrops, v => config().ShadowLengthCrops = v,
                () => config().ShadowSoftnessCrops, v => config().ShadowSoftnessCrops = v,
                () => config().ShadowLeanCrops, v => config().ShadowLeanCrops = v);
            AddKindDials(api, manifest, i18n, "grass",
                () => config().ShadowLengthGrass, v => config().ShadowLengthGrass = v,
                () => config().ShadowSoftnessGrass, v => config().ShadowSoftnessGrass = v,
                () => config().ShadowLeanGrass, v => config().ShadowLeanGrass = v);
            AddKindDials(api, manifest, i18n, "objects",
                () => config().ShadowLengthObjects, v => config().ShadowLengthObjects = v,
                () => config().ShadowSoftnessObjects, v => config().ShadowSoftnessObjects = v,
                () => config().ShadowLeanObjects, v => config().ShadowLeanObjects = v);
            AddKindDials(api, manifest, i18n, "buildings",
                () => config().ShadowLengthBuildings, v => config().ShadowLengthBuildings = v,
                () => config().ShadowSoftnessBuildings, v => config().ShadowSoftnessBuildings = v,
                () => config().ShadowLeanBuildings, v => config().ShadowLeanBuildings = v);

            // --- Camera (implemented) ---
        }

        /// <summary>Camera smoothing.</summary>
        private static void RegisterCameraPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
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
        }

        /// <summary>Render scale and sharpening.</summary>
        private static void RegisterPerformancePage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "perf", () => i18n("config.section.perf"));
            api.AddNumberOption(manifest, () => config().RenderScale, v => config().RenderScale = v,
                () => i18n("config.renderscale.name"), () => i18n("config.renderscale.tooltip"), 0.5f, 1f, 0.05f);
            api.AddBoolOption(manifest, () => config().RenderScaleAuto, v => config().RenderScaleAuto = v,
                () => i18n("config.renderscaleauto.name"), () => i18n("config.renderscaleauto.tooltip"));
            api.AddNumberOption(manifest, () => config().RenderSharpness, v => config().RenderSharpness = v,
                () => i18n("config.rendersharpness.name"), () => i18n("config.rendersharpness.tooltip"), 0f, 2f, 0.1f);

            // --- Misc page: hotkeys + diagnostics + roadmap ---
        }

        /// <summary>The Scale2x doubling: how far it goes and which art families it touches.
        /// Its own page, mirroring the tuner's own tab, because it stopped being one switch.</summary>
        private static void RegisterSmoothingPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "smoothing", () => i18n("tuner.tab.smoothing"));
            api.AddBoolOption(manifest, () => config().SheetUpscaleEnabled, v => config().SheetUpscaleEnabled = v,
                () => i18n("config.sheetupscale.name"), () => i18n("config.sheetupscale.tooltip"));
            api.AddNumberOption(manifest, () => config().SheetUpscaleSmoothness, v => config().SheetUpscaleSmoothness = v,
                () => i18n("config.sheetupscalesmoothness.name"), () => i18n("config.sheetupscalesmoothness.tooltip"), 0f, 1f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("tuner.section.smoothingfamilies"));
            api.AddBoolOption(manifest, () => config().SheetUpscaleWorld, v => config().SheetUpscaleWorld = v,
                () => i18n("config.sheetupscaleworld.name"), () => i18n("config.sheetupscaleworld.tooltip"));
            api.AddBoolOption(manifest, () => config().SheetUpscaleCharacters, v => config().SheetUpscaleCharacters = v,
                () => i18n("config.sheetupscalecharacters.name"), () => i18n("config.sheetupscalecharacters.tooltip"));
            api.AddBoolOption(manifest, () => config().SheetUpscalePortraits, v => config().SheetUpscalePortraits = v,
                () => i18n("config.sheetupscaleportraits.name"), () => i18n("config.sheetupscaleportraits.tooltip"));
            api.AddBoolOption(manifest, () => config().SheetUpscaleInterface, v => config().SheetUpscaleInterface = v,
                () => i18n("config.sheetupscaleinterface.name"), () => i18n("config.sheetupscaleinterface.tooltip"));
        }

        /// <summary>Hotkeys, the debug switches, and the roadmap section.</summary>
        private static void RegisterMiscPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config, IModHelper helper, IMonitor monitor, Func<RenderPipeline?> getPipeline)
        {
            api.AddPage(manifest, "misc", () => i18n("config.section.misc"));
            api.AddSectionTitle(manifest, () => i18n("config.section.hotkeys"));
            api.AddKeybindList(manifest, () => config().ToggleKey, v => config().ToggleKey = v,
                () => i18n("config.togglekey.name"), () => i18n("config.togglekey.tooltip"));
            api.AddKeybindList(manifest, () => config().TunerKey, v => config().TunerKey = v,
                () => i18n("config.tunerkey.name"), () => i18n("config.tunerkey.tooltip"));

            // --- Diagnostics ---
            //
            // Everything here also exists as a console command, and on a phone the console does not
            // exist: SMAPI on Android has no command line and no keyboard to open the tuner with
            // either. That leaves this menu and config.json as the whole reachable surface, so the
            // three diagnostics a reporter is ever asked for are duplicated into it. It is not only
            // for phones - plenty of people on a desktop have never opened the SMAPI console.
            api.AddSectionTitle(manifest, () => i18n("config.section.debug"));
            api.AddBoolOption(manifest, () => config().DebugLogging, v => config().DebugLogging = v,
                () => i18n("config.debug.name"), () => i18n("config.debug.tooltip"));
            api.AddBoolOption(manifest, () => PerfHud.Visible, v => PerfHud.Visible = v,
                () => i18n("tuner.perfhud"), () => i18n("help.perfhud"));
            api.AddBoolOption(manifest, () => GpuTimer.Ready, GpuTimer.SetWanted,
                () => i18n("tuner.gputime"), () => i18n("help.gputime"));
            // A tick box rather than a button, because the API we bind has no button. It reads back
            // as unticked immediately, which is right: it is an action, not a state.
            api.AddBoolOption(manifest,
                () => false,
                v => { if (v) ConsoleCommands.WriteReport(helper, monitor, getPipeline(), config(), alsoLog: true); },
                () => i18n("config.report.name"), () => i18n("config.report.tooltip"));

            // --- Not yet implemented: shown as a roadmap so options don't imply working features ---
            api.AddSectionTitle(manifest, () => i18n("config.section.wip"));
            api.AddParagraph(manifest, () => i18n("config.wip.text"));
        }
    }
}
