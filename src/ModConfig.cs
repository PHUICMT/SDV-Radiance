using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace SDVRadiance
{
    /// <summary>Camera behaviour. Supersample2x is reserved for a future phase.</summary>
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
    /// Phase 0 only wires up the master switch + the effect toggles as scaffolding;
    /// individual effects are implemented in later phases.
    /// </summary>
    public sealed class ModConfig
    {
        /// <summary>Master switch for the whole post-processing pipeline.</summary>
        public bool Enabled { get; set; } = true;

        // --- Phase 1: Bloom ---
        public bool BloomEnabled { get; set; } = false;
        public float BloomThreshold { get; set; } = 0.82f;
        public float BloomIntensity { get; set; } = 0.3f;

        // --- Phase 2: Color grade ---
        public bool ColorGradeEnabled { get; set; } = false;
        public float ColorGradeStrength { get; set; } = 1f;
        public float ColorGradeContrast { get; set; } = 1.12f;
        public float ColorGradeSaturation { get; set; } = 1.2f;
        public float ColorGradeTemperature { get; set; } = 0f;
        public float ColorGradeBrightness { get; set; } = 1f;
        public bool ColorGradeToneMap { get; set; } = false;
        /// <summary>Auto-shift temperature/saturation by time of day, weather, and season.</summary>
        public bool ColorGradeAuto { get; set; } = true;

        // --- Phase 2b: God rays ---
        public bool GodRaysEnabled { get; set; } = false;
        public float GodRaysIntensity { get; set; } = 0.4f;
        public float GodRaysThreshold { get; set; } = 0.7f;
        public float GodRaysDensity { get; set; } = 0.6f;
        public float GodRaysDecay { get; set; } = 0.96f;

        // --- Phase 2b: Volumetric fog ---
        public bool FogEnabled { get; set; } = false;
        public float FogDensity { get; set; } = 0.18f;
        public float FogScale { get; set; } = 2.5f;
        public float FogSpeed { get; set; } = 0.02f;
        public float FogTopBias { get; set; } = 0.5f;

        // --- Phase 3: DynamicShader parity ---
        public bool CloudShadowEnabled { get; set; } = false;
        public float CloudShadowScale { get; set; } = 2.0f;
        public float CloudShadowSpeed { get; set; } = 0.01f;
        public float CloudShadowOpacity { get; set; } = 0.3f;
        public float CloudShadowCoverage { get; set; } = 0.45f;

        public bool TiltShiftEnabled { get; set; } = false;
        public TiltShiftFocus TiltShiftMode { get; set; } = TiltShiftFocus.Bands;
        public float TiltShiftTopRatio { get; set; } = 0.7f;    // top blur amount (0 = none … 1 = up to middle)
        public float TiltShiftBottomRatio { get; set; } = 0.7f; // bottom blur amount (0 = none … 1 = up to middle)
        public float TiltShiftStrength { get; set; } = 1f;
        public float TiltShiftRadius { get; set; } = 0.3f;      // radial mode: size of the sharp circle around the player

        // --- Phase 4: Water + finishing ---
        public bool WaterEnabled { get; set; } = false;
        public float WaterStrength { get; set; } = 0.5f;   // ripple amplitude
        public float WaterSpeed { get; set; } = 1.0f;      // ripple animation speed
        public float WaterSparkle { get; set; } = 0.25f;   // specular glint intensity
        public bool WaterReflection { get; set; } = false; // screen-space reflection on water
        public float WaterReflectStrength { get; set; } = 0.3f;

        public bool VignetteEnabled { get; set; } = false;
        public float VignetteStrength { get; set; } = 0.25f;

        public bool ChromaticAberrationEnabled { get; set; } = false;
        public float ChromaticAberrationStrength { get; set; } = 0.3f; // 0..1 UI scale (scaled to a tiny UV offset)

        // --- Phase 5: Dynamic 2D lighting ---
        /// <summary>Darken flat/unlit areas and pool light around real light sources.</summary>
        public bool LightingEnabled { get; set; } = false;
        /// <summary>How dark interiors get (vanilla leaves them flat-bright). 0 = none, 1 = very dark.</summary>
        public float LightingIndoorDarkness { get; set; } = 0.5f;
        /// <summary>Extra darkening at night where we own the lighting. 0 = none.</summary>
        public float LightingNightDarkness { get; set; } = 0.35f;
        /// <summary>Warmth of the light pools (0 = neutral white, 1 = candle-orange).</summary>
        public float LightingWarmth { get; set; } = 0.35f;
        /// <summary>Scale the on-screen radius of every light pool.</summary>
        public float LightingRadiusScale { get; set; } = 1.0f;
        /// <summary>Brightness of the light pools added back over the darkened scene.</summary>
        public float LightingBoost { get; set; } = 1.0f;
        /// <summary>Cast hard-edge shadows from tall/solid tiles that block light.</summary>
        public bool LightingShadows { get; set; } = false;
        /// <summary>How dark occluder shadows are. 0 = none, 1 = full.</summary>
        public float LightingShadowStrength { get; set; } = 0.5f;

        // --- Phase 5b: directional sprite shadows (sun-cast, sheared silhouettes) ---
        /// <summary>Cast directional shadows from sprites (NPCs, later player/objects), by sun angle.</summary>
        public bool DirectionalShadowsEnabled { get; set; } = false;
        /// <summary>Opacity of the directional shadows. 0 = none, 1 = full.</summary>
        public float DirectionalShadowStrength { get; set; } = 1.0f;
        /// <summary>Length multiplier for the cast shadow (1 = default sun-driven length).</summary>
        public float DirectionalShadowLength { get; set; } = 1.0f;
        /// <summary>Edge softness of the shadow, in pixels (0 = crisp).</summary>
        public float DirectionalShadowBlur { get; set; } = 2.0f;

        // --- Camera (independent of the post-processing pipeline) ---
        /// <summary>Which camera behaviour to use. Off = vanilla snap.</summary>
        public CameraMode CameraMode { get; set; } = CameraMode.Off;
        /// <summary>Per-tick follow factor while moving (0.05 = very smooth/laggy, 1.0 = instant). Smooth mode only.</summary>
        public float CameraFollowSpeed { get; set; } = 0.3f;

        // --- Hotkeys ---
        /// <summary>Toggle the whole post-processing stack on/off (for quick A/B compare).</summary>
        public KeybindList ToggleKey { get; set; } = new(SButton.F7);
        /// <summary>Open the live tuner overlay.</summary>
        public KeybindList TunerKey { get; set; } = new(SButton.F8);

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
