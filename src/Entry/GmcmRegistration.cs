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
                () => i18n("config.godrays.intensity.name"), null, 0f, 1.5f, 0.05f);
            api.AddNumberOption(manifest, () => config().GodRaysThreshold, v => config().GodRaysThreshold = v,
                () => i18n("config.godrays.threshold.name"), null, 0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().GodRaysDensity, v => config().GodRaysDensity = v,
                () => i18n("config.godrays.density.name"), null, 0.1f, 1f, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.godrays.sectionsun"));
            api.AddBoolOption(manifest, () => config().GodRaysSun, v => config().GodRaysSun = v,
                () => i18n("config.godrays.sun.name"), () => i18n("config.godrays.sun.tooltip"));
            api.AddNumberOption(manifest, () => config().GodRaysSunIntensity, v => config().GodRaysSunIntensity = v,
                () => i18n("config.godrays.sunintensity.name"), () => i18n("config.godrays.sunintensity.tooltip"), 0f, 1.5f, 0.05f);
            api.AddNumberOption(manifest, () => config().GodRaysSunReach, v => config().GodRaysSunReach = v,
                () => i18n("config.godrays.sunreach.name"), () => i18n("config.godrays.sunreach.tooltip"), 0.1f, 1f, 0.05f);
            // Falloff is set once for the whole light loop, so it shapes the lamp streaks and the
            // sun's dapple alike. Its own heading rather than a place in either section above,
            // because a shared dial filed under one of them claims to belong to that one.
            api.AddSectionTitle(manifest, () => i18n("config.godrays.sectionboth"));
            api.AddNumberOption(manifest, () => config().GodRaysDecay, v => config().GodRaysDecay = v,
                () => i18n("config.godrays.decay.name"), () => i18n("config.godrays.decay.tooltip"), 0.5f, 0.99f, 0.01f);

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

            // --- Cloud shadows (implemented) ---
        }

        /// <summary>Weather: the replacement rain and snow, drawn in the game's own weather slot.</summary>
        private static void RegisterWeatherPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
            api.AddPage(manifest, "weather", () => i18n("config.section.weather"));
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
            AddParticleEmitter(api, manifest, i18n, "ringsparkles",
                () => config().ParticleRingSparkles, v => config().ParticleRingSparkles = v,
                () => config().ParticleRingSparklesAmount, v => config().ParticleRingSparklesAmount = v,
                () => config().ParticleRingSparklesSize, v => config().ParticleRingSparklesSize = v);

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
                0.3f, 1.5f, 0.05f);
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
            api.AddTextOption(manifest,
                () => config().WaterReflectStyle.ToString(),
                v => config().WaterReflectStyle = Enum.TryParse<WaterReflectionStyle>(v, out var rs) ? rs : WaterReflectionStyle.Natural,
                () => i18n("config.water.reflstyle.name"), () => i18n("config.water.reflstyle.tooltip"),
                new[] { "StillWater", "Natural", "Choppy" });
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
            api.AddNumberOption(manifest, () => config().LightingMorningDarkness, v => config().LightingMorningDarkness = v,
                () => i18n("config.lighting.morning.name"), () => i18n("config.lighting.morning.tooltip"), 0f, 0.95f, 0.05f);
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

        private static void RegisterShadowsPage(IGenericModConfigMenuApi api, IManifest manifest, Func<string, string> i18n, Func<ModConfig> config)
        {
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
            api.AddNumberOption(manifest, () => config().ShadowLeanClarity, v => config().ShadowLeanClarity = v,
                () => i18n("config.shadows.leanclarity.name"), () => i18n("config.shadows.leanclarity.tooltip"),
                0f, 1f, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowCastsPerCharacter, v => config().ShadowCastsPerCharacter = v,
                () => i18n("config.shadows.casts.name"), () => i18n("config.shadows.casts.tooltip"),
                ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax, 1);

            // Per-kind length and softness. The overall two above still multiply these, so a player
            // who only wants everything shorter never has to come down here.
            api.AddSectionTitle(manifest, () => i18n("config.shadows.perkind.title"),
                () => i18n("config.shadows.perkind.tooltip"));
            api.AddNumberOption(manifest, () => config().ShadowLengthTrees, v => config().ShadowLengthTrees = v,
                () => i18n("config.shadows.length.trees.name"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLengthSmallTrees, v => config().ShadowLengthSmallTrees = v,
                () => i18n("config.shadows.length.smalltrees.name"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLengthBushes, v => config().ShadowLengthBushes = v,
                () => i18n("config.shadows.length.bushes.name"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLengthCrops, v => config().ShadowLengthCrops = v,
                () => i18n("config.shadows.length.crops.name"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLengthGrass, v => config().ShadowLengthGrass = v,
                () => i18n("config.shadows.length.grass.name"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLengthObjects, v => config().ShadowLengthObjects = v,
                () => i18n("config.shadows.length.objects.name"), null,
                ModConfig.ShadowKindLengthMin, ModConfig.ShadowKindLengthMax, 0.05f);
            api.AddSectionTitle(manifest, () => i18n("config.shadows.softness.title"),
                () => i18n("config.shadows.softness.tooltip"));
            api.AddNumberOption(manifest, () => config().ShadowSoftnessTrees, v => config().ShadowSoftnessTrees = v,
                () => i18n("config.shadows.softness.trees.name"), null,
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddNumberOption(manifest, () => config().ShadowSoftnessSmallTrees, v => config().ShadowSoftnessSmallTrees = v,
                () => i18n("config.shadows.softness.smalltrees.name"), null,
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddNumberOption(manifest, () => config().ShadowSoftnessBushes, v => config().ShadowSoftnessBushes = v,
                () => i18n("config.shadows.softness.bushes.name"), null,
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddNumberOption(manifest, () => config().ShadowSoftnessCrops, v => config().ShadowSoftnessCrops = v,
                () => i18n("config.shadows.softness.crops.name"), null,
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddNumberOption(manifest, () => config().ShadowSoftnessGrass, v => config().ShadowSoftnessGrass = v,
                () => i18n("config.shadows.softness.grass.name"), null,
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddNumberOption(manifest, () => config().ShadowSoftnessObjects, v => config().ShadowSoftnessObjects = v,
                () => i18n("config.shadows.softness.objects.name"), null,
                ModConfig.ShadowKindSoftnessMin, ModConfig.ShadowKindSoftnessMax, 0.1f);
            api.AddSectionTitle(manifest, () => i18n("config.shadows.lean.title"),
                () => i18n("config.shadows.lean.tooltip"));
            api.AddNumberOption(manifest, () => config().ShadowLeanTrees, v => config().ShadowLeanTrees = v,
                () => i18n("config.shadows.lean.trees.name"), null,
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLeanSmallTrees, v => config().ShadowLeanSmallTrees = v,
                () => i18n("config.shadows.lean.smalltrees.name"), null,
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLeanBushes, v => config().ShadowLeanBushes = v,
                () => i18n("config.shadows.lean.bushes.name"), null,
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLeanCrops, v => config().ShadowLeanCrops = v,
                () => i18n("config.shadows.lean.crops.name"), null,
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLeanGrass, v => config().ShadowLeanGrass = v,
                () => i18n("config.shadows.lean.grass.name"), null,
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);
            api.AddNumberOption(manifest, () => config().ShadowLeanObjects, v => config().ShadowLeanObjects = v,
                () => i18n("config.shadows.lean.objects.name"), null,
                ModConfig.ShadowKindLeanMin, ModConfig.ShadowKindLeanMax, 0.05f);

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
