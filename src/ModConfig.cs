using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace SDVRadiance
{
    /// <summary>Camera behaviour.</summary>
    public enum CameraMode
    {
        Off,
        Smooth
    }

    /// <summary>Tilt-shift focus shape.</summary>
    public enum TiltShiftFocus
    {
        Bands,   // sharp middle band, blur top & bottom
        Radial   // sharp circle around the player, blur outward
    }

    /// <summary>Quick look presets applied to the whole effect stack.</summary>
    public enum LookPreset
    {
        Custom,
        Off,
        Subtle,
        Cinematic,
        Vibrant
    }

    /// <summary>A user-saved look: a named snapshot of the effect settings.</summary>
    public sealed class NamedProfile
    {
        public string Name { get; set; } = "";
        public bool BloomEnabled { get; set; }
        public float BloomThreshold { get; set; }
        public float BloomIntensity { get; set; }
        public bool ColorGradeEnabled { get; set; }
        public bool ColorGradeAuto { get; set; }
        public bool ColorGradeToneMap { get; set; }
        public float ColorGradeStrength { get; set; }
        public float ColorGradeContrast { get; set; }
        public float ColorGradeSaturation { get; set; }
        public float ColorGradeTemperature { get; set; }
        public float ColorGradeBrightness { get; set; }
        public bool GodRaysEnabled { get; set; }
        public float GodRaysIntensity { get; set; }
        public bool FogEnabled { get; set; }
        public float FogDensity { get; set; }
    }

    /// <summary>
    /// User-facing configuration. Serialized to config.json and edited via GMCM.
    /// A master switch plus a per-effect toggle and sliders for each effect.
    /// </summary>
    public sealed class ModConfig
    {
        /// <summary>Master switch for the whole post-processing pipeline.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The quick-look preset last chosen from the menu's top dropdown (Custom = hand-tuned).</summary>
        public LookPreset ActivePreset { get; set; } = LookPreset.Custom;

        // --- Bloom ---
        public bool BloomEnabled { get; set; } = true;
        public float BloomThreshold { get; set; } = 0.72f;
        public float BloomIntensity { get; set; } = 0.76f;

        // --- Color grade ---
        public bool ColorGradeEnabled { get; set; } = true;
        public float ColorGradeStrength { get; set; } = 1f;
        public float ColorGradeContrast { get; set; } = 1.15f;
        public float ColorGradeSaturation { get; set; } = 1.05f;
        public float ColorGradeTemperature { get; set; } = 0.05f;
        public float ColorGradeBrightness { get; set; } = 1f;
        public bool ColorGradeToneMap { get; set; } = false;
        /// <summary>Auto-shift temperature/saturation by time of day, weather, and season.</summary>
        public bool ColorGradeAuto { get; set; } = true;

        // --- God rays ---
        public bool GodRaysEnabled { get; set; } = true;
        public float GodRaysIntensity { get; set; } = 0.68f;
        public float GodRaysThreshold { get; set; } = 0.7f;
        public float GodRaysDensity { get; set; } = 0.6f;
        public float GodRaysDecay { get; set; } = 0.96f;

        // --- Volumetric fog ---
        public bool FogEnabled { get; set; } = false;
        public float FogDensity { get; set; } = 0.13f;
        public float FogScale { get; set; } = 3.0f;
        public float FogSpeed { get; set; } = 0.02f;
        public float FogTopBias { get; set; } = 0.5f;

        // --- Cloud shadows ---
        public bool CloudShadowEnabled { get; set; } = true;
        /// <summary>Hide the vanilla drifting <c>Cloud</c> critter shadow (so only our cloud shadow shows).</summary>
        public bool SuppressVanillaCloudShadow { get; set; } = true;
        public float CloudShadowScale { get; set; } = 1.0f;
        public float CloudShadowSpeed { get; set; } = 0.04f;
        public float CloudShadowOpacity { get; set; } = 0.61f;
        public float CloudShadowCoverage { get; set; } = 0.43f;

        public bool TiltShiftEnabled { get; set; } = true;
        public TiltShiftFocus TiltShiftMode { get; set; } = TiltShiftFocus.Bands;
        public float TiltShiftTopRatio { get; set; } = 0.3f;    // top blur amount (0 = none … 1 = up to middle)
        public float TiltShiftBottomRatio { get; set; } = 0.3f; // bottom blur amount (0 = none … 1 = up to middle)
        public float TiltShiftStrength { get; set; } = 0.9f;
        public float TiltShiftRadius { get; set; } = 0.85f;     // radial mode: size of the sharp circle around the player

        // --- Water + finishing ---
        public bool WaterEnabled { get; set; } = true;
        public float WaterStrength { get; set; } = 0.6f;   // ripple amplitude
        public float WaterSpeed { get; set; } = 0.78f;     // ripple animation speed
        public float WaterSparkle { get; set; } = 0.51f;   // specular glint intensity
        public float WaterSparkleDensity { get; set; } = 0.7f; // glint count/size (1 = old look)
        public bool WaterReflection { get; set; } = true;  // screen-space reflection on water
        public float WaterReflectStrength { get; set; } = 0.71f;

        public bool VignetteEnabled { get; set; } = true;
        public float VignetteStrength { get; set; } = 0.25f;

        public bool ChromaticAberrationEnabled { get; set; } = true;
        public float ChromaticAberrationStrength { get; set; } = 0.3f; // 0..1 UI scale (scaled to a tiny UV offset)

        // --- Dynamic 2D lighting ---
        /// <summary>Flood-propagation GI lightmap (occlusion-aware ambient, shade under
        /// canopies, coloured lamp pools). Supersedes LightingEnabled when on.</summary>
        public bool FloodLightingEnabled { get; set; } = true;
        /// <summary>How strongly the flood lightmap modulates the scene (0..1).</summary>
        public float FloodLightingStrength { get; set; } = 0.54f;
        /// <summary>How dark a fully occluded per-light ray gets (0 = no shadows, 1 = black).</summary>
        public float FloodShadowStrength { get; set; } = 0.79f;
        /// <summary>Darken flat/unlit areas and pool light around real light sources.</summary>
        public bool LightingEnabled { get; set; } = true;
        /// <summary>How dark interiors get (vanilla leaves them flat-bright). 0 = none, 1 = very dark.</summary>
        public float LightingIndoorDarkness { get; set; } = 0.64f;
        /// <summary>Extra darkening at night where we own the lighting. 0 = none.</summary>
        public float LightingNightDarkness { get; set; } = 0.68f;
        /// <summary>Warmth of the light pools (0 = neutral white, 1 = candle-orange).</summary>
        public float LightingWarmth { get; set; } = 0.62f;
        /// <summary>Scale the on-screen radius of every light pool.</summary>
        public float LightingRadiusScale { get; set; } = 0.54f;
        /// <summary>Brightness of the light pools added back over the darkened scene.</summary>
        public float LightingBoost { get; set; } = 0.27f;
        /// <summary>Cast hard-edge shadows from tall/solid tiles that block light.</summary>
        public bool LightingShadows { get; set; } = true;
        /// <summary>How dark occluder shadows are. 0 = none, 1 = full.</summary>
        public float LightingShadowStrength { get; set; } = 0.59f;

        // --- Directional sprite shadows (sun-cast, sheared silhouettes) ---
        /// <summary>Cast directional shadows from sprites (NPCs, later player/objects), by sun angle.</summary>
        public bool DirectionalShadowsEnabled { get; set; } = true;
        /// <summary>Opacity of the directional shadows. 0 = none, 1 = full.</summary>
        public float DirectionalShadowStrength { get; set; } = 0.83f;
        /// <summary>Length multiplier for the cast shadow (1 = default sun-driven length).</summary>
        public float DirectionalShadowLength { get; set; } = 1.18f;
        /// <summary>Edge softness of the shadow, in pixels (0 = crisp).</summary>
        public float DirectionalShadowBlur { get; set; } = 4.0f;
        /// <summary>Also cast directional shadows from trees and bushes (not just characters).</summary>
        public bool DirectionalShadowObjects { get; set; } = true;

        /// <summary>
        /// Normalize every numeric field to its supported range. GMCM sliders only protect
        /// values entered through the UI — hand-edited config.json flows straight into the
        /// shaders (e.g. GodRaysDecay > 1 grows exponentially into an additive white-out).
        /// Called after ReadConfig and on every GMCM save.
        /// </summary>
        public void Clamp()
        {
            static float C(float v, float lo, float hi) => float.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);

            BloomThreshold = C(BloomThreshold, 0f, 1f);
            BloomIntensity = C(BloomIntensity, 0f, 2f);
            ColorGradeStrength = C(ColorGradeStrength, 0f, 1f);
            ColorGradeContrast = C(ColorGradeContrast, 0.5f, 1.5f);
            ColorGradeSaturation = C(ColorGradeSaturation, 0f, 2f);
            ColorGradeTemperature = C(ColorGradeTemperature, -1f, 1f);
            ColorGradeBrightness = C(ColorGradeBrightness, 0.5f, 1.5f);
            GodRaysIntensity = C(GodRaysIntensity, 0f, 1.5f);
            GodRaysThreshold = C(GodRaysThreshold, 0f, 1f);
            GodRaysDensity = C(GodRaysDensity, 0.1f, 1f);
            GodRaysDecay = C(GodRaysDecay, 0.5f, 0.99f);
            FogDensity = C(FogDensity, 0f, 1f);
            FogScale = C(FogScale, 1f, 8f);
            FogSpeed = C(FogSpeed, 0f, 0.1f);
            FogTopBias = C(FogTopBias, 0f, 1f);
            CloudShadowOpacity = C(CloudShadowOpacity, 0f, 0.7f);
            CloudShadowCoverage = C(CloudShadowCoverage, 0.1f, 0.9f);
            CloudShadowScale = C(CloudShadowScale, 1f, 5f);
            CloudShadowSpeed = C(CloudShadowSpeed, 0f, 0.1f);
            TiltShiftStrength = C(TiltShiftStrength, 0f, 1f);
            TiltShiftRadius = C(TiltShiftRadius, 0.05f, 0.9f);
            TiltShiftTopRatio = C(TiltShiftTopRatio, 0f, 1f);
            TiltShiftBottomRatio = C(TiltShiftBottomRatio, 0f, 1f);
            WaterStrength = C(WaterStrength, 0f, 2f);
            WaterSpeed = C(WaterSpeed, 0f, 3f);
            WaterSparkle = C(WaterSparkle, 0f, 1f);
            WaterSparkleDensity = C(WaterSparkleDensity, 0.2f, 2f);
            WaterReflectStrength = C(WaterReflectStrength, 0f, 1f);
            VignetteStrength = C(VignetteStrength, 0f, 1f);
            ChromaticAberrationStrength = C(ChromaticAberrationStrength, 0f, 1f);
            FloodLightingStrength = C(FloodLightingStrength, 0f, 1f);
            FloodShadowStrength = C(FloodShadowStrength, 0f, 1f);
            LightingIndoorDarkness = C(LightingIndoorDarkness, 0f, 0.95f);
            LightingNightDarkness = C(LightingNightDarkness, 0f, 0.95f);
            LightingWarmth = C(LightingWarmth, 0f, 1f);
            LightingBoost = C(LightingBoost, 0f, 2f);
            LightingRadiusScale = C(LightingRadiusScale, 0.2f, 3f);
            LightingShadowStrength = C(LightingShadowStrength, 0f, 1f);
            DirectionalShadowStrength = C(DirectionalShadowStrength, 0f, 1f);
            DirectionalShadowLength = C(DirectionalShadowLength, 0.2f, 2f);
            DirectionalShadowBlur = C(DirectionalShadowBlur, 0f, 5f);
            CameraFollowSpeed = C(CameraFollowSpeed, 0.05f, 1f);
        }

        // --- Camera (independent of the post-processing pipeline) ---
        /// <summary>Which camera behaviour to use. Off = vanilla snap.</summary>
        public CameraMode CameraMode { get; set; } = CameraMode.Off;
        /// <summary>Per-tick follow factor while moving (0.05 = very smooth/laggy, 1.0 = instant). Smooth mode only.</summary>
        public float CameraFollowSpeed { get; set; } = 0.3f;

        // --- Hotkeys ---
        /// <summary>Toggle the whole post-processing stack on/off (for quick A/B compare).</summary>
        public KeybindList ToggleKey { get; set; } = new(SButton.F7);
        /// <summary>Open the live tuner overlay. F6: F8/F9 belong to Fashion Sense's outfit
        /// tools in the wild, and colliding with the most popular cosmetic mod hurts.</summary>
        public KeybindList TunerKey { get; set; } = new(SButton.F6);

        // --- Saved custom looks ---
        public List<NamedProfile> SavedProfiles { get; set; } = new();

        // --- Diagnostics ---
        /// <summary>Log per-frame pipeline info once, to help debug the render hook.</summary>
        public bool DebugLogging { get; set; } = false;

        /// <summary>Snapshot the current effect settings into a named profile.</summary>
        public NamedProfile CaptureProfile(string name) => new()
        {
            Name = name,
            BloomEnabled = BloomEnabled,
            BloomThreshold = BloomThreshold,
            BloomIntensity = BloomIntensity,
            ColorGradeEnabled = ColorGradeEnabled,
            ColorGradeAuto = ColorGradeAuto,
            ColorGradeToneMap = ColorGradeToneMap,
            ColorGradeStrength = ColorGradeStrength,
            ColorGradeContrast = ColorGradeContrast,
            ColorGradeSaturation = ColorGradeSaturation,
            ColorGradeTemperature = ColorGradeTemperature,
            ColorGradeBrightness = ColorGradeBrightness,
            GodRaysEnabled = GodRaysEnabled,
            GodRaysIntensity = GodRaysIntensity,
            FogEnabled = FogEnabled,
            FogDensity = FogDensity
        };

        /// <summary>Load a saved profile's settings into the live config.</summary>
        public void ApplyProfile(NamedProfile p)
        {
            BloomEnabled = p.BloomEnabled;
            BloomThreshold = p.BloomThreshold;
            BloomIntensity = p.BloomIntensity;
            ColorGradeEnabled = p.ColorGradeEnabled;
            ColorGradeAuto = p.ColorGradeAuto;
            ColorGradeToneMap = p.ColorGradeToneMap;
            ColorGradeStrength = p.ColorGradeStrength;
            ColorGradeContrast = p.ColorGradeContrast;
            ColorGradeSaturation = p.ColorGradeSaturation;
            ColorGradeTemperature = p.ColorGradeTemperature;
            ColorGradeBrightness = p.ColorGradeBrightness;
            GodRaysEnabled = p.GodRaysEnabled;
            GodRaysIntensity = p.GodRaysIntensity;
            FogEnabled = p.FogEnabled;
            FogDensity = p.FogDensity;
        }

        /// <summary>Apply a quick look preset by overwriting the effect fields.</summary>
        public void ApplyPreset(LookPreset preset)
        {
            switch (preset)
            {
                case LookPreset.Off:
                    BloomEnabled = false;
                    ColorGradeEnabled = false;
                    break;

                case LookPreset.Subtle:
                    BloomEnabled = false;
                    ColorGradeEnabled = true;
                    ColorGradeAuto = true;
                    ColorGradeStrength = 0.6f;
                    ColorGradeContrast = 1.06f;
                    ColorGradeSaturation = 1.08f;
                    ColorGradeTemperature = 0f;
                    ColorGradeBrightness = 1f;
                    ColorGradeToneMap = false;
                    break;

                case LookPreset.Cinematic:
                    BloomEnabled = true;
                    BloomThreshold = 0.72f;
                    BloomIntensity = 0.35f;
                    ColorGradeEnabled = true;
                    ColorGradeAuto = true;
                    ColorGradeStrength = 1f;
                    ColorGradeContrast = 1.15f;
                    ColorGradeSaturation = 1.05f;
                    ColorGradeTemperature = 0.05f;
                    ColorGradeBrightness = 1f;
                    ColorGradeToneMap = false;
                    break;

                case LookPreset.Vibrant:
                    BloomEnabled = true;
                    BloomThreshold = 0.7f;
                    BloomIntensity = 0.45f;
                    ColorGradeEnabled = true;
                    ColorGradeAuto = true;
                    ColorGradeStrength = 1f;
                    ColorGradeContrast = 1.15f;
                    ColorGradeSaturation = 1.35f;
                    ColorGradeTemperature = 0f;
                    ColorGradeBrightness = 1.03f;
                    ColorGradeToneMap = false;
                    break;

                case LookPreset.Custom:
                default:
                    break;
            }
        }
    }
}
