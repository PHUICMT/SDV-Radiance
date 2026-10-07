using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SDVRadiance
{
    /// <summary>What a code carries. The letter is part of the code, so a release that predates a
    /// scope refuses it by name instead of reading it as something it is not.</summary>
    internal enum ShareCodeScope
    {
        /// <summary>The look: everything that changes what the game looks like.</summary>
        Look,

        /// <summary>The look and the performance settings with it, for handing over a whole
        /// setup rather than a look.</summary>
        Everything,
    }

    /// <summary>What one entry of a code asks for: the setting, what it holds now, and what the
    /// code would put there. Built against a live config so it can be shown before it is used.</summary>
    internal sealed class ShareCodeChange
    {
        internal string PropertyName { get; init; } = "";
        internal string OldValue { get; init; } = "";
        internal string NewValue { get; init; } = "";

        /// <summary>Whether this one costs frames rather than changing the picture, so it can be
        /// shown apart from the look with a word about what it does.</summary>
        internal bool IsPerformance { get; init; }

        /// <summary>Not in the code: the sender had it as a fresh install does, so it goes back to
        /// that. Kept apart so the list can say so.</summary>
        internal bool ReturnsToDefault { get; init; }
    }

    /// <summary>A code that has been read and checked but not used yet.</summary>
    internal sealed class ShareCodeReading
    {
        internal List<ShareCodeChange> Changes { get; } = [];

        internal ShareCodeScope Scope { get; set; }

        /// <summary>Settings in the code that this release cannot use: a number a newer release
        /// gave out, or a value it cannot make sense of. Counted rather than dropped in silence, so
        /// the person is told how many are being stepped over.</summary>
        internal int SettingsThisReleaseCannotUse { get; set; }

        /// <summary>A colour table the code asks for that is not on this machine. The rest of the
        /// code still applies; this one setting is left alone, because pointing the grade at a file
        /// that is not there would turn the colours off rather than change them. Empty when there
        /// is no such trouble.</summary>
        internal string ColourTableNotHere { get; set; } = "";
    }

    /// <summary>
    /// A look as a short piece of text, so one player can hand their settings to another.
    ///
    /// <para>Three promises shape the format. It is SHORT, so only settings that differ from a
    /// fresh install travel, a setting that is simply not its default spends no bytes on saying
    /// so, and a colour table travels as its place in the shipped list rather than as its name.
    /// It SURVIVES UPDATES in both directions, so every setting travels under a number that is
    /// assigned once and never reused, and every entry says how long it is, so a release that
    /// meets a setting it has never heard of steps over it and uses the rest. And it is sometimes
    /// TYPED BY HAND, so the alphabet holds no letter that can be mistaken for a digit and the
    /// last character catches a slip.</para>
    /// </summary>
    internal static class ShareCode
    {
        /// <summary>Marks the text as ours and says which format it is. The scope letter follows.</summary>
        internal const string Family = "RAD1";
        private const char LookLetter = 'L';
        private const char EverythingLetter = 'F';
        private const int GroupSize = 5;

        /// <summary>Crockford's alphabet: no I, L, O or U, so nothing reads as 1 or 0 and no word
        /// spells itself by accident. Reading takes either case and forgives I, L and O.</summary>
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>
        /// How long an entry's value is. The kind lives in the entry's first byte, so the LENGTH of
        /// an entry is known even when the setting is not: that is what lets an older release step
        /// over a newer setting cleanly.
        ///
        /// <para><see cref="KindNoBytes"/> is kept for a value that needs no bytes at all; no setting
        /// uses it today. A switch once did, meaning "not its default", and that meaning broke the
        /// day a release changed a default, so a switch now carries its position in a byte.</para>
        /// </summary>
        private const int KindOneByte = 0;
        private const int KindTwoBytes = 1;
        private const int KindNoBytes = 2;
        private const int KindCounted = 3;

        /// <summary>The largest value a hundredths byte can carry, and the largest a signed
        /// hundredths pair can. The pair reaches past 327, which is what puts a compass bearing in
        /// two bytes instead of four.</summary>
        private const float OneByteCeiling = 2.55f;
        private const float TwoByteCeiling = 327.67f;

        private const bool Look = false;
        private const bool Performance = true;

        /// <summary>
        /// Every setting a code can carry, the number it travels under, and whether it belongs to
        /// the look or to the performance group.
        ///
        /// <para>THE NUMBERS ARE PROMISES. Once a setting has one it keeps it forever, a number is
        /// never given to a second setting, and a setting that is retired leaves its number empty
        /// rather than handing it on. Renaming the property is safe because nameof moves with it;
        /// changing what a setting MEANS is not, and that case retires the number and takes a fresh
        /// one at the end. The first thirty two cost one byte on the wire instead of two, which is
        /// why the dials people actually trade are at the front.</para>
        ///
        /// <para>tools/sharecode/check.py fails the build when a setting has no number here, so a
        /// setting added later cannot be forgotten until somebody's code quietly loses it.</para>
        /// </summary>
        private static readonly (int Id, string Property, bool IsPerformance)[] Registry =
        [
            (0, nameof(ModConfig.BloomEnabled), Look),
            (1, nameof(ModConfig.BloomThreshold), Look),
            (2, nameof(ModConfig.BloomIntensity), Look),
            (3, nameof(ModConfig.BloomEmissiveBoost), Look),
            (4, nameof(ModConfig.ColorGradeEnabled), Look),
            (5, nameof(ModConfig.ColorGradeAuto), Look),
            (6, nameof(ModConfig.ColorGradeToneMap), Look),
            (7, nameof(ModConfig.ColorGradeStrength), Look),
            (8, nameof(ModConfig.ColorGradeContrast), Look),
            (9, nameof(ModConfig.ColorGradeSaturation), Look),
            (10, nameof(ModConfig.ColorGradeTemperature), Look),
            (11, nameof(ModConfig.ColorGradeBrightness), Look),
            (12, nameof(ModConfig.ColorGradeLut), Look),
            (13, nameof(ModConfig.ColorGradeLutAmount), Look),
            (14, nameof(ModConfig.FogEnabled), Look),
            (15, nameof(ModConfig.FogDensity), Look),
            (16, nameof(ModConfig.FogCoverage), Look),
            (17, nameof(ModConfig.FogNightMist), Look),
            (18, nameof(ModConfig.FogNightMistDensity), Look),
            (19, nameof(ModConfig.GodRaysEnabled), Look),
            (20, nameof(ModConfig.GodRaysIntensity), Look),
            (21, nameof(ModConfig.GodRaysSun), Look),
            (22, nameof(ModConfig.GodRaysSunIntensity), Look),
            (23, nameof(ModConfig.VignetteEnabled), Look),
            (24, nameof(ModConfig.VignetteStrength), Look),
            (25, nameof(ModConfig.ChromaticAberrationEnabled), Look),
            (26, nameof(ModConfig.ChromaticAberrationStrength), Look),
            (27, nameof(ModConfig.TiltShiftEnabled), Look),
            (28, nameof(ModConfig.TiltShiftStrength), Look),
            (29, nameof(ModConfig.BlueLightFilter), Look),
            (30, nameof(ModConfig.LightingIndoorDarkness), Look),
            (31, nameof(ModConfig.LightingNightDarkness), Look),
            (32, nameof(ModConfig.GodRaysSunReach), Look),
            (33, nameof(ModConfig.GodRaysSunGlassRoof), Look),
            (34, nameof(ModConfig.FogNightMistLampGlow), Look),
            (35, nameof(ModConfig.FogNightMistCoverage), Look),
            (36, nameof(ModConfig.FogNightMistSpeed), Look),
            (37, nameof(ModConfig.MineFogMist), Look),
            (38, nameof(ModConfig.FogScale), Look),
            (39, nameof(ModConfig.FogSpeed), Look),
            (40, nameof(ModConfig.FogTopBias), Look),
            (41, nameof(ModConfig.CloudShadowEnabled), Look),
            (42, nameof(ModConfig.SuppressVanillaCloudShadow), Look),
            (43, nameof(ModConfig.CloudShadowScale), Look),
            (44, nameof(ModConfig.CloudShadowCount), Look),
            (45, nameof(ModConfig.CloudShadowSpeed), Look),
            (46, nameof(ModConfig.CloudShadowOpacity), Look),
            (47, nameof(ModConfig.CloudShadowCoverage), Look),
            (48, nameof(ModConfig.StormWarningStrength), Look),
            (49, nameof(ModConfig.TiltShiftMode), Look),
            (50, nameof(ModConfig.TiltShiftTopRatio), Look),
            (51, nameof(ModConfig.TiltShiftBottomRatio), Look),
            (52, nameof(ModConfig.TiltShiftRadius), Look),
            (53, nameof(ModConfig.TiltShiftFeather), Look),
            (54, nameof(ModConfig.TiltShiftIndoorAmount), Look),
            (55, nameof(ModConfig.WaterEnabled), Look),
            (56, nameof(ModConfig.WaterStrength), Look),
            (57, nameof(ModConfig.WaterSpeed), Look),
            (58, nameof(ModConfig.WaterSparkle), Look),
            (59, nameof(ModConfig.WaterSparkleDensity), Look),
            (60, nameof(ModConfig.WaterSparkleCloudShade), Look),
            (61, nameof(ModConfig.WaterGlitterPath), Look),
            (62, nameof(ModConfig.WaterCausticsEnabled), Look),
            (63, nameof(ModConfig.WaterCausticsStrength), Look),
            (64, nameof(ModConfig.WaterReflection), Look),
            (65, nameof(ModConfig.WaterReflectStrength), Look),
            (66, nameof(ModConfig.WaterReflectModel), Look),
            (67, nameof(ModConfig.WaterReflectStyle), Look),
            (68, nameof(ModConfig.WaterReflectFadeRows), Look),
            (69, nameof(ModConfig.WaterReflectReach), Look),
            (70, nameof(ModConfig.WaterReflectDepth), Look),
            (71, nameof(ModConfig.WaterReflectBanding), Look),
            (72, nameof(ModConfig.WaterReflectDistort), Look),
            (73, nameof(ModConfig.WaterReflectBlur), Look),
            (74, nameof(ModConfig.WaterModernWobble), Look),
            (75, nameof(ModConfig.WaterModernChoppiness), Look),
            (76, nameof(ModConfig.WaterModernParallax), Look),
            (77, nameof(ModConfig.WaterModernFresnel), Look),
            (78, nameof(ModConfig.WaterModernStretch), Look),
            (79, nameof(ModConfig.WaterModernEdgeSoftness), Look),
            (80, nameof(ModConfig.WaterModernPlungeChurn), Look),
            (81, nameof(ModConfig.WaterModernPlungeReach), Look),
            (82, nameof(ModConfig.WaterModernLipFade), Look),
            (83, nameof(ModConfig.WaterRainRingDensity), Look),
            (84, nameof(ModConfig.WaterRainRingSize), Look),
            (85, nameof(ModConfig.WaterRainRingStrength), Look),
            (86, nameof(ModConfig.WaterWakeRings), Look),
            (87, nameof(ModConfig.WaterFishSpotRings), Look),
            (88, nameof(ModConfig.WaterWind), Look),
            (89, nameof(ModConfig.WaterRiverFlowEnabled), Look),
            (90, nameof(ModConfig.WaterCurrent), Look),
            (91, nameof(ModConfig.WaterSeaWaves), Look),
            (92, nameof(ModConfig.WaterRiverRainSwell), Look),
            (93, nameof(ModConfig.WaterRiverFoam), Look),
            (94, nameof(ModConfig.WaterRiverRippleSpeed), Look),
            (95, nameof(ModConfig.WaterRiverRenew), Look),
            (96, nameof(ModConfig.WaterRiverBankDrag), Look),
            (97, nameof(ModConfig.WaterRiverSwirl), Look),
            (98, nameof(ModConfig.WaterRiverGlitter), Look),
            (99, nameof(ModConfig.WaterRiverFoamStreak), Look),
            (100, nameof(ModConfig.WaterRiverWaves), Look),
            (101, nameof(ModConfig.WaterRiverPixelStep), Look),
            (102, nameof(ModConfig.WaterEffectIndoors), Look),
            (103, nameof(ModConfig.FloodLightingEnabled), Look),
            (104, nameof(ModConfig.FloodGiModel), Look),
            (105, nameof(ModConfig.SpriteReliefEnabled), Look),
            (106, nameof(ModConfig.SpriteReliefStrength), Look),
            (107, nameof(ModConfig.SpriteReliefHalfResolution), Look),
            (108, nameof(ModConfig.SpriteReliefSun), Look),
            (109, nameof(ModConfig.SpriteReliefRim), Look),
            (110, nameof(ModConfig.SpriteReliefLeafShimmer), Look),
            (111, nameof(ModConfig.FoliageSwayEnabled), Look),
            (112, nameof(ModConfig.FoliageSwayStrength), Look),
            (113, nameof(ModConfig.FoliageSwaySpeed), Look),
            (114, nameof(ModConfig.FoliageSwayGustSpan), Look),
            (115, nameof(ModConfig.FoliageSwayCrops), Look),
            (116, nameof(ModConfig.SheetUpscaleEnabled), Look),
            (117, nameof(ModConfig.SheetUpscaleStyle), Look),
            (118, nameof(ModConfig.SheetUpscaleSmoothness), Look),
            (119, nameof(ModConfig.SheetUpscaleSmoothnessWorld), Look),
            (120, nameof(ModConfig.SheetUpscaleSmoothnessCharacters), Look),
            (121, nameof(ModConfig.SheetUpscaleSmoothnessPortraits), Look),
            (122, nameof(ModConfig.SheetUpscaleSmoothnessItems), Look),
            (123, nameof(ModConfig.SheetUpscaleSmoothnessInterface), Look),
            (124, nameof(ModConfig.SheetUpscaleWorld), Look),
            (125, nameof(ModConfig.SheetUpscaleCharacters), Look),
            (126, nameof(ModConfig.SheetUpscalePortraits), Look),
            (127, nameof(ModConfig.SheetUpscaleItems), Look),
            (128, nameof(ModConfig.SheetUpscaleInterface), Look),
            (129, nameof(ModConfig.FloodLightingStrength), Look),
            (130, nameof(ModConfig.FloodShadowStrength), Look),
            (131, nameof(ModConfig.FloodColourBleed), Look),
            (132, nameof(ModConfig.LightShadowCarve), Look),
            (133, nameof(ModConfig.LightShadowSoftness), Look),
            (134, nameof(ModConfig.LightShadowDetail), Look),
            (135, nameof(ModConfig.LightShadowDetailShared), Look),
            (136, nameof(ModConfig.LightShadowSharpEdges), Look),
            (137, nameof(ModConfig.LightShadowMarchCache), Look),
            (138, nameof(ModConfig.WateredSoilSparkle), Look),
            (139, nameof(ModConfig.WindowEffectsEnabled), Look),
            (140, nameof(ModConfig.WindowGlowOpensNight), Look),
            (141, nameof(ModConfig.LampHalo), Look),
            (142, nameof(ModConfig.AquariumRipple), Look),
            (143, nameof(ModConfig.TvScreenGlow), Look),
            (144, nameof(ModConfig.WindowBeamEnabled), Look),
            (145, nameof(ModConfig.WindowDaylightStrength), Look),
            (146, nameof(ModConfig.WindowDaylightStrengthElsewhere), Look),
            (147, nameof(ModConfig.WindowReflectionEnabled), Look),
            (148, nameof(ModConfig.WindowReflectionIndoors), Look),
            (149, nameof(ModConfig.WindowReflectionStrength), Look),
            (150, nameof(ModConfig.WindowReflectionNightStrength), Look),
            (151, nameof(ModConfig.WindowSheenStrength), Look),
            (152, nameof(ModConfig.WindowGlareStrength), Look),
            (153, nameof(ModConfig.WindowVehicleGlassStrength), Look),
            (154, nameof(ModConfig.WindowSceneReflectionStrength), Look),
            (155, nameof(ModConfig.WindowLightGlowStrength), Look),
            (156, nameof(ModConfig.ParticlesEnabled), Look),
            (157, nameof(ModConfig.ParticleDensity), Look),
            (158, nameof(ModConfig.ParticleDust), Look),
            (159, nameof(ModConfig.ParticleDustAmount), Look),
            (160, nameof(ModConfig.ParticleDustSize), Look),
            (161, nameof(ModConfig.ParticleEmbers), Look),
            (162, nameof(ModConfig.ParticleEmbersAmount), Look),
            (163, nameof(ModConfig.ParticleEmbersSize), Look),
            (164, nameof(ModConfig.HeatHazeEnabled), Look),
            (165, nameof(ModConfig.HeatHazeStrength), Look),
            (166, nameof(ModConfig.ParticleWaterfallMist), Look),
            (167, nameof(ModConfig.ParticleWaterfallMistAmount), Look),
            (168, nameof(ModConfig.ParticleWaterfallMistSize), Look),
            (169, nameof(ModConfig.WaterfallRainbowStrength), Look),
            (170, nameof(ModConfig.WaterfallRainbowFollowsSun), Look),
            (171, nameof(ModConfig.ParticleHotSpringSteam), Look),
            (172, nameof(ModConfig.ParticleHotSpringSteamAmount), Look),
            (173, nameof(ModConfig.ParticleHotSpringSteamSize), Look),
            (174, nameof(ModConfig.ParticleLavaSparks), Look),
            (175, nameof(ModConfig.ParticleLavaSparksAmount), Look),
            (176, nameof(ModConfig.ParticleLavaSparksSize), Look),
            (177, nameof(ModConfig.ParticleFireflies), Look),
            (178, nameof(ModConfig.ParticleFirefliesAmount), Look),
            (179, nameof(ModConfig.ParticleFirefliesSize), Look),
            (180, nameof(ModConfig.ParticlePetals), Look),
            (181, nameof(ModConfig.ParticlePetalsAmount), Look),
            (182, nameof(ModConfig.ParticlePetalsSize), Look),
            (183, nameof(ModConfig.ParticlePetalsFlutter), Look),
            (184, nameof(ModConfig.ParticleRingSparkles), Look),
            (185, nameof(ModConfig.ParticleRingSparklesAmount), Look),
            (186, nameof(ModConfig.ParticleRingSparklesSize), Look),
            (187, nameof(ModConfig.ParticleFootDust), Look),
            (188, nameof(ModConfig.ParticleFootDustAmount), Look),
            (189, nameof(ModConfig.ParticleFootDustSize), Look),
            (190, nameof(ModConfig.ParticleFestiveLights), Look),
            (191, nameof(ModConfig.ParticleFestiveLightsAmount), Look),
            (192, nameof(ModConfig.ParticleFestiveLightsSize), Look),
            (193, nameof(ModConfig.ParticleChimney), Look),
            (194, nameof(ModConfig.ParticleChimneyAmount), Look),
            (195, nameof(ModConfig.ParticleChimneySize), Look),
            (196, nameof(ModConfig.ParticleGlowLight), Look),
            (197, nameof(ModConfig.PrecipitationEnabled), Look),
            (198, nameof(ModConfig.SnowGlintStrength), Look),
            (199, nameof(ModConfig.AuroraEnabled), Look),
            (200, nameof(ModConfig.AuroraStrength), Look),
            (201, nameof(ModConfig.ShootingStarsEnabled), Look),
            (202, nameof(ModConfig.PrecipitationRain), Look),
            (203, nameof(ModConfig.PrecipitationSnow), Look),
            (204, nameof(ModConfig.PrecipitationRainDensity), Look),
            (205, nameof(ModConfig.PrecipitationRainSize), Look),
            (206, nameof(ModConfig.PrecipitationRainOpacity), Look),
            (207, nameof(ModConfig.PrecipitationSnowDensity), Look),
            (208, nameof(ModConfig.PrecipitationSnowSize), Look),
            (209, nameof(ModConfig.PrecipitationSnowOpacity), Look),
            (210, nameof(ModConfig.PrecipitationWind), Look),
            (211, nameof(ModConfig.PrecipitationWindDensity), Look),
            (212, nameof(ModConfig.PrecipitationWindSize), Look),
            (213, nameof(ModConfig.PrecipitationWindOpacity), Look),
            (214, nameof(ModConfig.PrecipitationStormDensity), Look),
            (215, nameof(ModConfig.PrecipitationRainSlant), Look),
            (216, nameof(ModConfig.PrecipitationWindSlant), Look),
            (217, nameof(ModConfig.LightningEffectsEnabled), Look),
            (218, nameof(ModConfig.LightningBoltsEnabled), Look),
            (219, nameof(ModConfig.WetWorldEnabled), Look),
            (220, nameof(ModConfig.WetWorldStrength), Look),
            (221, nameof(ModConfig.WetWorldPuddles), Look),
            (222, nameof(ModConfig.WetWorldLensDrops), Look),
            (223, nameof(ModConfig.WetWorldLensDropSize), Look),
            (224, nameof(ModConfig.WetWorldEdgeHaze), Look),
            (225, nameof(ModConfig.LightingEnabled), Look),
            (226, nameof(ModConfig.LightingIndoorColourWalk), Look),
            (227, nameof(ModConfig.LightingMorningClearSkyCool), Look),
            (228, nameof(ModConfig.LightingMorningDarkness), Look),
            (229, nameof(ModConfig.LightingWarmth), Look),
            (230, nameof(ModConfig.LightingRadiusScale), Look),
            (231, nameof(ModConfig.LightingBoost), Look),
            (232, nameof(ModConfig.LightingShadows), Look),
            (233, nameof(ModConfig.LightingShadowStrength), Look),
            (234, nameof(ModConfig.LightShadowSilhouettes), Look),
            (235, nameof(ModConfig.LightShadowProps), Look),
            (236, nameof(ModConfig.DirectionalShadowModel), Look),
            (237, nameof(ModConfig.DirectionalShadowsEnabled), Look),
            (238, nameof(ModConfig.DirectionalShadowPlayer), Look),
            (239, nameof(ModConfig.DirectionalShadowVillagers), Look),
            (240, nameof(ModConfig.DirectionalShadowFarmAnimals), Look),
            (241, nameof(ModConfig.DirectionalShadowCreatures), Look),
            (242, nameof(ModConfig.DirectionalShadowStrength), Look),
            (243, nameof(ModConfig.DirectionalShadowLength), Look),
            (244, nameof(ModConfig.GoldenHourStrength), Look),
            (245, nameof(ModConfig.SunSeasonStrength), Look),
            (246, nameof(ModConfig.ShadowSunBearing), Look),
            (247, nameof(ModConfig.SunlightBearing), Look),
            (248, nameof(ModConfig.ShadowContactHardness), Look),
            (249, nameof(ModConfig.ShadowPenumbraStretch), Look),
            (250, nameof(ModConfig.ShadowTint), Look),
            (251, nameof(ModConfig.DirectionalShadowBlur), Look),
            (252, nameof(ModConfig.DirectionalShadowObjects), Look),
            (253, nameof(ModConfig.ContactShadowStrength), Look),
            (254, nameof(ModConfig.ContactShadowPeopleStrength), Look),
            (255, nameof(ModConfig.DirectionalShadowBuildings), Look),
            (256, nameof(ModConfig.ShadowGroundForeshortening), Look),
            (257, nameof(ModConfig.ShadowCharacterGroundForeshortening), Look),
            (258, nameof(ModConfig.ShadowLengthTrees), Look),
            (259, nameof(ModConfig.ShadowLengthSmallTrees), Look),
            (260, nameof(ModConfig.ShadowLengthBushes), Look),
            (261, nameof(ModConfig.ShadowLengthCrops), Look),
            (262, nameof(ModConfig.ShadowLengthGrass), Look),
            (263, nameof(ModConfig.ShadowLengthObjects), Look),
            (264, nameof(ModConfig.ShadowLengthBuildings), Look),
            (265, nameof(ModConfig.ShadowSoftnessTrees), Look),
            (266, nameof(ModConfig.ShadowSoftnessSmallTrees), Look),
            (267, nameof(ModConfig.ShadowSoftnessBushes), Look),
            (268, nameof(ModConfig.ShadowSoftnessCrops), Look),
            (269, nameof(ModConfig.ShadowSoftnessGrass), Look),
            (270, nameof(ModConfig.ShadowSoftnessObjects), Look),
            (271, nameof(ModConfig.ShadowSoftnessBuildings), Look),
            (272, nameof(ModConfig.ShadowLeanTrees), Look),
            (273, nameof(ModConfig.ShadowLeanSmallTrees), Look),
            (274, nameof(ModConfig.ShadowLeanBushes), Look),
            (275, nameof(ModConfig.ShadowLeanCrops), Look),
            (276, nameof(ModConfig.ShadowLeanGrass), Look),
            (277, nameof(ModConfig.ShadowLeanObjects), Look),
            (278, nameof(ModConfig.ShadowLeanBuildings), Look),
            (279, nameof(ModConfig.ShadowCastsPerCharacter), Look),
            (280, nameof(ModConfig.CameraMode), Look),
            (281, nameof(ModConfig.CameraFollowSpeed), Look),

            // The performance group, which a look code leaves behind and a whole-setup code
            // carries. It is shown to the person receiving it as its own group, with a word
            // about what it does, because this is the one group that can make a game feel
            // WORSE rather than look different.
            (282, nameof(ModConfig.RenderScale), Performance),
            (283, nameof(ModConfig.RenderScaleAuto), Performance),
            (284, nameof(ModConfig.RenderSharpness), Performance),
            (285, nameof(ModConfig.LimitSamplerSlots), Performance),

            // Settings added after the registry was first written, in the order they got a number.
            (286, nameof(ModConfig.FoliageSwayGrass), Look),
            (287, nameof(ModConfig.GrassSmoothShake), Look),
            (288, nameof(ModConfig.ZoomAreaFilter), Look),
            (289, nameof(ModConfig.SheetUpscaleSteadyRead), Look),
            (290, nameof(ModConfig.SheetUpscaleGradientSmoothing), Look),
            (291, nameof(ModConfig.FloodCarriedLightsBounce), Look),
            (292, nameof(ModConfig.ShadowGroundedLook), Look),
            (293, nameof(ModConfig.ShadowGroundedDepth), Look),
            (294, nameof(ModConfig.ShadowCarriedLightsCast), Look),
            (295, nameof(ModConfig.SheetUpscaleSoftKernel), Look),
            (296, nameof(ModConfig.SheetUpscaleKernelWorld), Look),
            (297, nameof(ModConfig.SheetUpscaleKernelCharacters), Look),
            (298, nameof(ModConfig.SheetUpscaleKernelPortraits), Look),
            (299, nameof(ModConfig.SheetUpscaleKernelItems), Look),
            (300, nameof(ModConfig.SheetUpscaleKernelInterface), Look),
            (301, nameof(ModConfig.WaterNightStars), Look),
            (302, nameof(ModConfig.WaterSparkleByDay), Look),
            (303, nameof(ModConfig.WaterSparkleAtNight), Look),
            (304, nameof(ModConfig.ShadowCreatureLength), Look),
            (305, nameof(ModConfig.ShadowCreatureSoftness), Look),
            (306, nameof(ModConfig.GameWeatherPlainTint), Look),
            (307, nameof(ModConfig.FogMorningOnly), Look),
            (308, nameof(ModConfig.FogMorningLiftHour), Look),
            (309, nameof(ModConfig.WetWorldLensDropLiveliness), Look),
            (310, nameof(ModConfig.WetWorldLensDropsRefract), Look),
            (311, nameof(ModConfig.WetWorldLensDropSpread), Look),
            (312, nameof(ModConfig.WindySpellsEnabled), Look),
            (313, nameof(ModConfig.WindySpellsStrength), Look),
            (314, nameof(ModConfig.WaterSeaRainSwell), Look),
        ];

        private static Dictionary<int, (string Property, bool IsPerformance)>? _settingById;
        private static Dictionary<string, PropertyInfo>? _propertyByName;
        private static ModConfig? _freshInstall;

        private static void Prepare()
        {
            if (_settingById != null)
                return;
            var settingById = new Dictionary<int, (string, bool)>(Registry.Length);
            foreach ((int id, string property, bool isPerformance) in Registry)
                settingById[id] = (property, isPerformance);
            var propertyByName = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (PropertyInfo property in typeof(ModConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                propertyByName[property.Name] = property;
            _settingById = settingById;
            _propertyByName = propertyByName;
            _freshInstall = new ModConfig();
        }

        /// <summary>The settings this release can carry, in the order they travel. The tuner reads
        /// it so a code's changes can be shown grouped the way the menu groups them.</summary>
        internal static IReadOnlyList<(int Id, string Property, bool IsPerformance)> KnownSettings => Registry;

        /// <summary>The mod's folder, where config.json lives. Set at launch.</summary>
        internal static string ConfigFolder { get; set; } = "";

        /// <summary>Copy config.json aside as config.before-code.json, just before a kept code
        /// is written over it. The last way back, behind the Undo row and the chip, for the day
        /// something goes wrong with the whole file rather than with one look.</summary>
        internal static void KeepConfigCopy()
        {
            try
            {
                string config = System.IO.Path.Combine(ConfigFolder, "config.json");
                if (ConfigFolder.Length > 0 && System.IO.File.Exists(config))
                    System.IO.File.Copy(config, System.IO.Path.Combine(ConfigFolder, "config.before-code.json"), overwrite: true);
            }
            catch (Exception)
            {
                // A copy that cannot be made leaves the chip and the Undo row, which is most of it.
            }
        }

        /// <summary>How a value is written down: the same spelling a saved look uses, so the two
        /// halves of the mod never disagree about what "0.35" means.</summary>
        internal static string Spell(object? value)
            => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        internal static string Prefix(ShareCodeScope scope)
            => Family + ScopeLetter(scope);

        private static char ScopeLetter(ShareCodeScope scope)
            => scope == ShareCodeScope.Everything ? EverythingLetter : LookLetter;

        /// <summary>The code for everything in this config that differs from a fresh install.</summary>
        internal static string Write(ModConfig config, ShareCodeScope scope = ShareCodeScope.Look)
            => Write(config, scope, out _);

        /// <summary>The same, and how many settings it carries, so nobody has to read the code
        /// back to count them.</summary>
        internal static string Write(ModConfig config, ShareCodeScope scope, out int settingsCarried)
        {
            Prepare();
            settingsCarried = 0;
            var payload = new List<byte>(256);
            foreach ((int id, string property, bool isPerformance) in Registry)
            {
                if (isPerformance && scope != ShareCodeScope.Everything)
                    continue;
                if (!_propertyByName!.TryGetValue(property, out PropertyInfo? info))
                    continue;
                object? mine = info.GetValue(config);
                object? fresh = info.GetValue(_freshInstall);
                // Compared the way reading compares, not as text: a slider dragged back to where
                // it started holds 0.59999996 against a default of 0.6, and as text that travelled
                // as a change, so the code grew and the count on screen disagreed with it.
                if (SameValue(info.PropertyType, Spell(mine), Spell(fresh)))
                    continue;
                int before = payload.Count;
                WriteEntry(payload, id, info.PropertyType, mine);
                if (payload.Count > before)
                    settingsCarried++;
            }
            var bytes = new List<byte>(payload.Count + 3);
            WriteLength(bytes, payload.Count);
            bytes.AddRange(payload);
            bytes.Add(CheckByte(bytes, ScopeLetter(scope)));
            return Prefix(scope) + "-" + InGroups(ToBase32(bytes));
        }

        private static void WriteEntry(List<byte> payload, int id, Type type, object? value)
        {
            if (type == typeof(bool))
            {
                // The position itself, not "the other one". Written as "not the default", a code
                // turned every switch it carried the wrong way the day a release changed that
                // switch's default, with nothing to say so. One byte buys a meaning that holds.
                WriteHead(payload, id, KindOneByte);
                payload.Add((bool)value! ? (byte)1 : (byte)0);
                return;
            }
            if (type == typeof(string))
            {
                // A shipped colour table travels as its place in that list, which is one byte
                // instead of a dozen. A table the player brought themselves has no place in it, so
                // that one travels as its name, the only way the other end could find the file.
                int shipped = Array.FindIndex(ModConfig.ShippedLuts,
                    name => string.Equals(name, (string?)value ?? "", StringComparison.OrdinalIgnoreCase));
                if (shipped is >= 0 and <= 255)
                {
                    WriteHead(payload, id, KindOneByte);
                    payload.Add((byte)shipped);
                    return;
                }
                byte[] text = Encoding.UTF8.GetBytes((string?)value ?? "");
                if (text.Length > 255)
                    return;
                WriteHead(payload, id, KindCounted);
                payload.Add((byte)text.Length);
                payload.AddRange(text);
                return;
            }
            if (type == typeof(float))
            {
                float number = (float)value!;
                // The narrowest form that loses nothing, chosen from the VALUE and never from a
                // table of each dial's range: a range written down twice is a range that drifts
                // apart, and a code read against the drifted copy decodes to a number nobody sent.
                float hundredths = (float)Math.Round(number * 100f);
                bool exact = Math.Abs((hundredths / 100f) - number) < 0.0001f;
                if (exact && number >= 0f && number <= OneByteCeiling)
                {
                    WriteHead(payload, id, KindOneByte);
                    payload.Add((byte)hundredths);
                    return;
                }
                if (exact && Math.Abs(number) <= TwoByteCeiling)
                {
                    WriteHead(payload, id, KindTwoBytes);
                    AddShort(payload, (short)hundredths);
                    return;
                }
                WriteHead(payload, id, KindCounted);
                payload.Add(4);
                payload.AddRange(BitConverter.GetBytes(number));
                return;
            }
            int whole = type.IsEnum ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : (int)value!;
            if (whole is >= 0 and <= 255)
            {
                WriteHead(payload, id, KindOneByte);
                payload.Add((byte)whole);
                return;
            }
            if (whole is >= short.MinValue and <= short.MaxValue)
            {
                WriteHead(payload, id, KindTwoBytes);
                AddShort(payload, (short)whole);
                return;
            }
            WriteHead(payload, id, KindCounted);
            payload.Add(4);
            payload.AddRange(BitConverter.GetBytes(whole));
        }

        /// <summary>The entry's first byte carries the kind and the low five bits of the number; a
        /// number too big for that sets the top bit and spends a second byte. Kind first means the
        /// length can be read without knowing the number at all.</summary>
        private static void WriteHead(List<byte> payload, int id, int kind)
        {
            if (id < 32)
            {
                payload.Add((byte)((kind << 5) | id));
                return;
            }
            payload.Add((byte)(0x80 | (kind << 5) | (id & 0x1F)));
            payload.Add((byte)(id >> 5));
        }

        private static void AddShort(List<byte> payload, short value)
        {
            payload.Add((byte)(value & 0xFF));
            payload.Add((byte)((value >> 8) & 0xFF));
        }

        /// <summary>How many bytes of settings follow: one byte while that is enough, which is
        /// every code anybody will actually paste, and two for the rest.</summary>
        private static void WriteLength(List<byte> bytes, int length)
        {
            if (length < 128)
            {
                bytes.Add((byte)length);
                return;
            }
            bytes.Add((byte)(0x80 | (length & 0x7F)));
            bytes.Add((byte)(length >> 7));
        }

        /// <summary>Read a code against a live config, so every change can be shown as what it is
        /// now and what it would become. Nothing is applied here.</summary>
        internal static bool TryRead(string typed, ModConfig against, out ShareCodeReading reading, out string problem)
        {
            Prepare();
            reading = new ShareCodeReading();
            problem = "";
            string cleaned = Tidy(typed);
            if (!cleaned.StartsWith(Family, StringComparison.Ordinal))
            {
                problem = cleaned.StartsWith("RAD", StringComparison.Ordinal) ? "scope" : "notours";
                return false;
            }
            if (cleaned.Length <= Family.Length)
            {
                problem = "scope";
                return false;
            }
            char letter = cleaned[Family.Length];
            if (letter is not LookLetter and not EverythingLetter)
            {
                problem = "scope";
                return false;
            }
            reading.Scope = letter == EverythingLetter ? ShareCodeScope.Everything : ShareCodeScope.Look;
            if (!TryFromBase32(cleaned[(Family.Length + 1)..], out byte[] bytes) || bytes.Length < 2)
            {
                problem = "checksum";
                return false;
            }
            if (bytes[^1] != CheckByte(new ArraySegment<byte>(bytes, 0, bytes.Length - 1), letter))
            {
                problem = "checksum";
                return false;
            }
            int at = 0;
            int length = bytes[at++] & 0x7F;
            if ((bytes[0] & 0x80) != 0)
            {
                if (at >= bytes.Length)
                {
                    problem = "checksum";
                    return false;
                }
                length |= bytes[at++] << 7;
            }
            int end = at + length;
            if (end > bytes.Length - 1)
            {
                problem = "checksum";
                return false;
            }
            var carried = new HashSet<string>(StringComparer.Ordinal);
            while (at < end)
            {
                byte head = bytes[at++];
                int kind = (head >> 5) & 0x03;
                int id = head & 0x1F;
                if ((head & 0x80) != 0)
                {
                    if (at >= end)
                    {
                        problem = "checksum";
                        return false;
                    }
                    id |= bytes[at++] << 5;
                }
                int size = kind switch
                {
                    KindOneByte => 1,
                    KindTwoBytes => 2,
                    KindNoBytes => 0,
                    _ => at < end ? bytes[at] + 1 : -1,
                };
                if (size < 0 || at + size > end)
                {
                    problem = "checksum";
                    return false;
                }
                if (_settingById!.TryGetValue(id, out (string Property, bool IsPerformance) setting)
                    && _propertyByName!.TryGetValue(setting.Property, out PropertyInfo? info)
                    && TryValue(info, kind, bytes, at, size, out string wanted))
                {
                    carried.Add(setting.Property);
                    string have = Spell(info.GetValue(against));
                    if (info.PropertyType == typeof(string) && !ColourTableIsHere(wanted))
                    {
                        reading.ColourTableNotHere = wanted;
                    }
                    else if (!SameValue(info.PropertyType, have, wanted))
                    {
                        reading.Changes.Add(new ShareCodeChange
                        {
                            PropertyName = setting.Property,
                            OldValue = have,
                            NewValue = wanted,
                            IsPerformance = setting.IsPerformance,
                        });
                    }
                }
                else
                {
                    reading.SettingsThisReleaseCannotUse++;
                }
                at += size;
            }
            AddWhatReturnsToDefault(reading, against, carried);
            return true;
        }

        /// <summary>
        /// Every setting the code's scope covers and the code does not carry goes back to how a
        /// fresh install has it, because that is how the sender had it.
        ///
        /// <para>A code holds only what the sender changed, and reading it used to touch only
        /// those. The receiver's own changes then survived underneath: somebody with bloom off and
        /// heavy fog who tried on a look that only moved the grade kept their bloom off and their
        /// fog, and the picture looked nothing like the sender's. A look code leaves the
        /// performance group alone either way, since it never carries it.</para>
        /// </summary>
        private static void AddWhatReturnsToDefault(ShareCodeReading reading, ModConfig against, HashSet<string> carried)
        {
            foreach ((int _, string property, bool isPerformance) in Registry)
            {
                if (carried.Contains(property) || (isPerformance && reading.Scope != ShareCodeScope.Everything))
                    continue;
                if (!_propertyByName!.TryGetValue(property, out PropertyInfo? info))
                    continue;
                string have = Spell(info.GetValue(against));
                string fresh = Spell(info.GetValue(_freshInstall));
                if (SameValue(info.PropertyType, have, fresh))
                    continue;
                reading.Changes.Add(new ShareCodeChange
                {
                    PropertyName = property,
                    OldValue = have,
                    NewValue = fresh,
                    IsPerformance = isPerformance,
                    ReturnsToDefault = true,
                });
            }
        }

        private static bool TryValue(PropertyInfo info, int kind, byte[] bytes, int at, int size, out string spelled)
        {
            spelled = "";
            Type type = info.PropertyType;
            if (type == typeof(bool))
            {
                if (kind != KindOneByte || bytes[at] > 1)
                    return false;
                spelled = Spell(bytes[at] == 1);
                return true;
            }
            if (type == typeof(string))
            {
                if (kind == KindOneByte)
                {
                    int shipped = bytes[at];
                    if (shipped >= ModConfig.ShippedLuts.Length)
                        return false;      // a colour table a newer release ships and this one does not
                    spelled = ModConfig.ShippedLuts[shipped];
                    return true;
                }
                if (kind != KindCounted)
                    return false;
                spelled = Encoding.UTF8.GetString(bytes, at + 1, size - 1);
                return true;
            }
            if (type == typeof(float))
            {
                if (kind == KindCounted)
                {
                    if (size != 5)
                        return false;
                    spelled = Spell(BitConverter.ToSingle(bytes, at + 1));
                    return true;
                }
                if (kind == KindNoBytes)
                    return false;
                float number = kind == KindOneByte
                    ? bytes[at] / 100f
                    : (short)(bytes[at] | (bytes[at + 1] << 8)) / 100f;
                spelled = Spell(number);
                return true;
            }
            int whole;
            if (kind == KindCounted)
            {
                if (size != 5)
                    return false;
                whole = BitConverter.ToInt32(bytes, at + 1);
            }
            else if (kind == KindOneByte)
            {
                whole = bytes[at];
            }
            else if (kind == KindTwoBytes)
            {
                whole = (short)(bytes[at] | (bytes[at + 1] << 8));
            }
            else
            {
                return false;
            }
            if (type.IsEnum)
            {
                if (!Enum.IsDefined(type, whole))
                    return false;
                spelled = Spell(Enum.ToObject(type, whole));
                return true;
            }
            spelled = Spell(whole);
            return type == typeof(int);
        }

        /// <summary>Put a read code's changes into the config. Every entry was checked while it was
        /// read, so this cannot half-apply a code that was going to be refused.</summary>
        internal static void Apply(ShareCodeReading reading, ModConfig config)
        {
            Prepare();
            foreach (ShareCodeChange change in reading.Changes)
            {
                if (!_propertyByName!.TryGetValue(change.PropertyName, out PropertyInfo? info))
                    continue;
                try
                {
                    Type type = info.PropertyType;
                    object value = type == typeof(string) ? change.NewValue
                        : type.IsEnum ? Enum.Parse(type, change.NewValue)
                        : type == typeof(bool) ? bool.Parse(change.NewValue)
                        : type == typeof(int) ? int.Parse(change.NewValue, CultureInfo.InvariantCulture)
                        : float.Parse(change.NewValue, CultureInfo.InvariantCulture);
                    info.SetValue(config, value);
                }
                catch
                {
                    // One unreadable value is one setting left alone, never a half-written config.
                }
            }
            config.Clamp();
        }

        /// <summary>
        /// Whether two written-down values are the same setting, rather than the same characters.
        ///
        /// <para>A dial that has been through the sliders holds 0.59999996 where the menu says 0.6,
        /// and the code carries the 0.6 the person meant. Comparing the two as text calls that a
        /// change, and a code read against a tuned install then claimed to move thirty seven
        /// settings when it moved one: the list a person is asked to approve has to hold the
        /// changes and nothing else, or they stop reading it. The allowance is the one the writer
        /// already works to, so a difference the format can express is never swallowed.</para>
        /// </summary>
        private static bool SameValue(Type type, string have, string wanted)
        {
            if (type == typeof(float)
                && float.TryParse(have, NumberStyles.Float, CultureInfo.InvariantCulture, out float mine)
                && float.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out float theirs))
            {
                return Math.Abs(mine - theirs) < 0.0001f;
            }
            return string.Equals(have, wanted, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a colour table the code names can actually be found here.
        ///
        /// <para>A table that shipped with the mod is always here. One the player brought
        /// themselves is only here if its file is: a look built on somebody's own table must not
        /// point this install's grade at a file it does not have, which would take the colour off
        /// rather than change it. The name still travels, because the name is the only thing this
        /// end can hold the folder up against, and the only thing worth telling somebody who wants
        /// the look the sender had.</para>
        ///
        /// <para>Outside the game, in an author tool or a test, the folders do not describe any
        /// player's install, so nothing is concluded from them.</para>
        /// </summary>
        private static bool ColourTableIsHere(string name)
        {
            if (name.Length == 0)
                return true;
            foreach (string shipped in ModConfig.ShippedLuts)
            {
                if (string.Equals(shipped, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!LutCatalog.CanSeeWhatIsInstalled)
                return true;
            foreach (string found in LutCatalog.Discover())
            {
                if (string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Everything that is not a code character goes: people paste out of chat windows
        /// that wrap lines, out of forum posts with a space on the end, and off a photograph of a
        /// screen. I, L and O read as 1, 1 and 0, the way Crockford's alphabet intends.</summary>
        private static string Tidy(string typed)
        {
            var kept = new StringBuilder(typed.Length);
            foreach (char letter in typed)
            {
                if (char.IsWhiteSpace(letter) || letter == '-' || letter == '_')
                    continue;
                kept.Append(char.ToUpperInvariant(letter));
            }
            // The same forgiveness in the prefix as in the body: "RADI" and "RADL" copied off a
            // photograph are RAD1, and were refused as a format from a newer release.
            if (kept.Length >= Family.Length && kept[0] == 'R' && kept[1] == 'A' && kept[2] == 'D' && kept[3] is 'I' or 'L')
                kept[3] = '1';
            return kept.ToString();
        }

        private static string InGroups(string code)
        {
            var built = new StringBuilder(code.Length + (code.Length / GroupSize));
            for (int at = 0; at < code.Length; at += GroupSize)
            {
                if (at > 0)
                    built.Append('-');
                built.Append(code, at, Math.Min(GroupSize, code.Length - at));
            }
            return built.ToString();
        }

        private static string ToBase32(IReadOnlyList<byte> bytes)
        {
            var built = new StringBuilder((bytes.Count * 8 / 5) + 1);
            int held = 0, bits = 0;
            foreach (byte value in bytes)
            {
                held = (held << 8) | value;
                bits += 8;
                while (bits >= 5)
                {
                    built.Append(Alphabet[(held >> (bits - 5)) & 0x1F]);
                    bits -= 5;
                }
            }
            if (bits > 0)
                built.Append(Alphabet[(held << (5 - bits)) & 0x1F]);
            return built.ToString();
        }

        private static bool TryFromBase32(string code, out byte[] bytes)
        {
            var built = new List<byte>((code.Length * 5 / 8) + 1);
            int held = 0, bits = 0;
            foreach (char letter in code)
            {
                char read = letter switch { 'I' or 'L' => '1', 'O' => '0', _ => letter };
                int value = Alphabet.IndexOf(read, StringComparison.Ordinal);
                if (value < 0)
                {
                    bytes = [];
                    return false;
                }
                held = (held << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    built.Add((byte)((held >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            bytes = [.. built];
            return true;
        }

        /// <summary>One character at the end that a slip cannot survive. The multiply is what makes
        /// it notice two characters swapped round, which a plain sum does not. The scope letter is
        /// part of it: outside it, a look code typed with an F for its L read as a whole setup and
        /// was believed.</summary>
        private static byte CheckByte(IReadOnlyList<byte> bytes, char scopeLetter)
        {
            int running = ((17 * 31) + scopeLetter) & 0xFF;
            foreach (byte value in bytes)
                running = ((running * 31) + value) & 0xFF;
            return (byte)running;
        }
    }
}
