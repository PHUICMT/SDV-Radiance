namespace SDVRadiance
{
    /// <summary>Camera behaviour. Supersample2x is reserved for a future phase.</summary>
    public enum CameraMode
    {
        Off,
        Smooth
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
        public float BloomThreshold { get; set; } = 0.75f;
        public float BloomIntensity { get; set; } = 0.5f;

        // --- Phase 2: Color grade + fog ---
        public bool ColorGradeEnabled { get; set; } = false;
        public float ColorGradeStrength { get; set; } = 0.5f;
        public bool FogEnabled { get; set; } = false;
        public float FogDensity { get; set; } = 0.3f;

        // --- Phase 3: DynamicShader parity ---
        public bool CloudShadowEnabled { get; set; } = false;
        public int CloudShadowCount { get; set; } = 3;
        public float CloudShadowScale { get; set; } = 1.0f;
        public float CloudShadowSpeed { get; set; } = 0.5f;
        public float CloudShadowOpacity { get; set; } = 0.3f;

        public bool TiltShiftEnabled { get; set; } = false;
        public float TiltShiftTopRatio { get; set; } = 0.5f;
        public float TiltShiftBottomRatio { get; set; } = 0.5f;

        // --- Phase 4: Water + finishing ---
        public bool WaterEnabled { get; set; } = false;
        public bool VignetteEnabled { get; set; } = false;
        public float VignetteStrength { get; set; } = 0.25f;

        // --- Camera (independent of the post-processing pipeline) ---
        /// <summary>Which camera behaviour to use. Off = vanilla snap.</summary>
        public CameraMode CameraMode { get; set; } = CameraMode.Off;
        /// <summary>Per-tick follow factor while moving (0.05 = very smooth/laggy, 1.0 = instant). Smooth mode only.</summary>
        public float CameraFollowSpeed { get; set; } = 0.3f;

        // --- Diagnostics ---
        /// <summary>Log per-frame pipeline info once, to help debug the render hook.</summary>
        public bool DebugLogging { get; set; } = false;
    }
}
