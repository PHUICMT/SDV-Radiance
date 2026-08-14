using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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

    /// <summary>
    /// How a reflection sits on the water. The surface's own movement and the reflection's
    /// clarity used to be one number, so calming a choppy mirror meant flattening the ripple
    /// everywhere; on a rainy day, where the game's own weather makes the surface half again as
    /// choppy, the only way to read a reflection was to turn the water off.
    /// </summary>
    public enum WaterReflectionStyle
    {
        /// <summary>Barely moved. The reflection reads like a still pond: the surface keeps its
        /// ripple, the image in it stays legible, and it sits lighter and cooler on the water.</summary>
        StillWater,
        /// <summary>The surface's own movement, unchanged. What the mod has always shipped.</summary>
        Natural,
        /// <summary>Broken up and deeper, for open sea and weather.</summary>
        Choppy
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

    /// <summary>Quality presets, kept deliberately separate from <see cref="LookPreset"/>:
    /// these change what the picture COSTS, never what it looks like. Someone running Cinematic
    /// on a weak machine should not have to give up their look to get frames back.</summary>
    public enum PerfPreset
    {
        /// <summary>Everything on at full resolution.</summary>
        Quality,
        /// <summary>Three quarter resolution and the two lens effects off. The measured sweet
        /// spot: about 44% less fill work, and dropping chromatic aberration also lets the
        /// grade and vignette merge into one pass.</summary>
        Balanced,
        /// <summary>Half resolution and the expensive extras off. Water reflections stay -
        /// they are the point of the mod, and the scenery cache already made them cheap.</summary>
        Performance
    }

    /// <summary>A user-saved look: a named snapshot of the effect settings.</summary>
    public sealed class NamedProfile
    {
        public string Name { get; set; } = "";
        /// <summary>Full snapshot (1.0.1+): every tunable value by property name, culture-invariant.
        /// The explicit fields below are the 1.0.0 format, kept so old chips still load.</summary>
        public Dictionary<string, string>? Values { get; set; }
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
        public bool FogNightMist { get; set; }
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
        /// <remarks>It said Custom while shipping the Cinematic numbers, so the dropdown opened on
        /// "hand-tuned" in an install nobody had tuned. Naming the look it actually is means the
        /// menu tells the truth on first launch, and someone who wanders off it can find their way
        /// back by picking it again. Nothing applies a preset on load, so this is a label, not an
        /// action: an existing config keeps whatever it already says.
        /// <para>
        /// 1.5.5 moves it off Cinematic for the same reason it was moved onto it: the label has to
        /// match what is actually shipping, and the colour grade is off by default now. Custom is
        /// the honest answer for a set of defaults that is nobody's named look - lighting and water
        /// on, the game's own colours untouched. Picking Cinematic from the dropdown still gives
        /// the old look in one click.
        /// </para>
        /// </remarks>
        public LookPreset ActivePreset { get; set; } = LookPreset.Custom;

        /// <summary>Resolution the EFFECT chain runs at, as a fraction of the window. 1 = native.
        /// The game still draws the world at full size; only our passes work on a smaller image
        /// and the finished frame is scaled back up, so the saving is quadratic (0.5 = a quarter
        /// of the fill cost). Point sampling both ways keeps the pixel art hard-edged: the art is
        /// already magnified ~4x on screen, so at 0.5 a texel still covers less than one game
        /// pixel and the blocks come back intact. Values between the two are a genuine resample -
        /// softer, and the block grid can shimmer while the camera moves.</summary>
        public float RenderScale { get; set; } = 1f;

        /// <summary>How much of the upscale sharpening to apply, as a multiple of the tuned
        /// amount: 0 turns it off (plain bilinear stretch), 1 is the measured default, and the
        /// slider goes past that for anyone who likes it crisper. The tuned amount already
        /// rises as <see cref="RenderScale"/> falls, since a smaller buffer needs more help.
        /// Only does anything while the scale is below 1 — it lives inside the upscale, and at
        /// native resolution there is no upscale to sharpen.</summary>
        public float RenderSharpness { get; set; } = 1f;

        // --- Bloom ---
        public bool BloomEnabled { get; set; } = true;
        public float BloomThreshold { get; set; } = 0.72f;
        /// <remarks>0.35, not the 0.76 it shipped with: at more than double the Cinematic preset's
        /// own value, a fresh install did not look like the preset it claimed to be.</remarks>
        public float BloomIntensity { get; set; } = 0.35f;

        // --- Color grade ---
        /// <summary>OFF by default from 1.5.5: the mod ships with the game's own colours.</summary>
        /// <remarks>
        /// This is a grade, which is to say an opinion about how the game should look, and it was
        /// being applied to everybody before they had asked for one. Two players called the picture
        /// too strong in the same week and a third thanked us for pointing out which setting was
        /// softening their sprites, which is three people spending their time undoing a decision
        /// they never made.
        /// <para>
        /// Measured in the saloon, the grade alone lifted saturation from the game's own 0.798 to
        /// 0.956 and was the largest single colour change in the stack. The lighting is what this
        /// mod is for and it stays on; the colour of the game is Concerned Ape's and it goes back
        /// to him. Anyone who wants the graded look has a preset for it in the first dropdown of
        /// the menu, and it is one click.
        /// </para>
        /// </remarks>
        public bool ColorGradeEnabled { get; set; } = false;
        public float ColorGradeStrength { get; set; } = 1f;
        // 1.5.0 took this to 1.10, halfway to Subtle's 1.06: 1.15 read as punchy on first launch
        // and the common note from people who liked the look was that they softened it before a
        // long session. 1.5.3 puts it back to 1.15 as part of shipping the Cinematic preset whole,
        // and the note it was lowered for was made against the OLD lighting, where a lamp could
        // not brighten anything and contrast was carrying the whole picture. If it reads as harsh
        // again now that the lights work, this line is the one to move, not the lighting.
        // 1.5.5 takes it to 1.10. Two players called the picture too strong, and until this
        // release the number was doing more than it said: contrast ran per channel, which pulled
        // the channels apart from each other and lifted saturation by about fifteen percent on
        // top of whatever the saturation control asked for. That side effect is gone now, so the
        // same number would read as more contrast than it used to deliver, and the honest move
        // is to take the number down rather than quietly hand back the difference.
        public float ColorGradeContrast { get; set; } = 1.10f;
        public float ColorGradeSaturation { get; set; } = 1.05f;
        public float ColorGradeTemperature { get; set; } = 0.05f;
        public float ColorGradeBrightness { get; set; } = 1f;
        public bool ColorGradeToneMap { get; set; } = false;
        /// <summary>Auto-shift temperature/saturation by time of day, weather, and season.</summary>
        public bool ColorGradeAuto { get; set; } = true;
        /// <summary>Blue-light / eye-comfort filter: 0 = off .. 1 = strong warm shift (cuts blue,
        /// lifts red a touch). Applied on top of grading and independent of it, so it works even
        /// with color grading turned off.</summary>
        public float BlueLightFilter { get; set; } = 0f;

        /// <summary>Bumped when a release has to CHANGE a setting an existing config already
        /// holds. An old config has no such field and lands on 0, so the migration in ModEntry
        /// runs exactly once and then records that it did.</summary>
        public int ConfigVersion { get; set; }

        // --- God rays ---
        // OFF by default since 1.3.1. The effect keeps bright SURFACES as ray emitters instead of
        // the light source, so any large pale sprite (a festival banner, a chef's whites) becomes
        // a second sun and blows out flat. It is being rebuilt for 1.4.0 to emit from the light
        // and treat the scene as occluders; until then, on by choice, not by default.
        public bool GodRaysEnabled { get; set; }
        public float GodRaysIntensity { get; set; } = 0.68f;
        /// <summary>Let the SUN be a ray source, not only lamps and fires. See UpdateRayLights.</summary>
        public bool GodRaysSun { get; set; } = true;
        public float GodRaysThreshold { get; set; } = 0.7f;
        public float GodRaysDensity { get; set; } = 0.6f;
        public float GodRaysDecay { get; set; } = 0.96f;

        // --- Volumetric fog ---
        public bool FogEnabled { get; set; } = false;
        /// <summary>Automatic subtle blue mist after dusk (outdoors, clear weather). Used to run
        /// implicitly whenever any effect was on — now opt-in so Fog OFF really means off.</summary>
        public bool FogNightMist { get; set; } = false;
        /// <summary>How thick the night-mist wisps get at deep night (0..1).</summary>
        public float FogNightMistDensity { get; set; } = 0.90f;
        public float FogDensity { get; set; } = 0.50f;   // wisp OPACITY (how strong each wisp tints)
        /// <summary>How much of the frame the day-fog wisps occupy (amount, not opacity).</summary>
        public float FogCoverage { get; set; } = 0.20f;
        /// <summary>How much of the frame the night-mist wisps occupy.</summary>
        public float FogNightMistCoverage { get; set; } = 0.25f;
        /// <summary>Night-mist drift speed.</summary>
        public float FogNightMistSpeed { get; set; } = 0.012f;
        public float FogScale { get; set; } = 3.0f;
        public float FogSpeed { get; set; } = 0.02f;
        public float FogTopBias { get; set; } = 0.5f;

        // --- Cloud shadows ---
        public bool CloudShadowEnabled { get; set; } = true;
        /// <summary>Hide the vanilla drifting <c>Cloud</c> critter shadow (so only our cloud shadow shows).</summary>
        public bool SuppressVanillaCloudShadow { get; set; } = true;
        public float CloudShadowScale { get; set; } = 1.0f;
        /// <summary>How many separate cloud banks share the screen (cluster frequency, 0..1).</summary>
        public float CloudShadowCount { get; set; } = 0.5f;
        public float CloudShadowSpeed { get; set; } = 0.04f;
        /// <summary>Default kept well under the 0.7 cap: 0.61 read as near-black to players.</summary>
        public float CloudShadowOpacity { get; set; } = 0.45f;
        public float CloudShadowCoverage { get; set; } = 0.43f;

        public bool TiltShiftEnabled { get; set; } = true;
        public TiltShiftFocus TiltShiftMode { get; set; } = TiltShiftFocus.Bands;
        public float TiltShiftTopRatio { get; set; } = 0.3f;    // top blur amount (0 = none … 1 = up to middle)
        public float TiltShiftBottomRatio { get; set; } = 0.3f; // bottom blur amount (0 = none … 1 = up to middle)
        public float TiltShiftStrength { get; set; } = 0.9f;
        public float TiltShiftRadius { get; set; } = 0.85f;     // radial mode: size of the sharp circle around the player
        public float TiltShiftFeather { get; set; } = 0.35f;    // softness of the sharp→blur edge (0 = crisp, 1 = very gradual)

        // --- Water + finishing ---
        public bool WaterEnabled { get; set; } = true;
        public float WaterStrength { get; set; } = 0.6f;   // ripple amplitude
        public float WaterSpeed { get; set; } = 0.78f;     // ripple animation speed
        public float WaterSparkle { get; set; } = 0.51f;   // specular glint intensity
        public float WaterSparkleDensity { get; set; } = 0.7f; // glint count/size (1 = old look)
        public bool WaterReflection { get; set; } = true;  // screen-space reflection on water
        public float WaterReflectStrength { get; set; } = 0.71f;
        /// <summary>Which of the named reflection looks is in use (see WaterReflectionStyle).</summary>
        public WaterReflectionStyle WaterReflectStyle { get; set; } = WaterReflectionStyle.Natural;
        /// <summary>Apply the water effect inside building interiors (farmhouse, cabins, custom
        /// home mods). Off = skip it there — some house mods have decorative rivers/ponds inside
        /// the user may not want rippling. Real level water ALWAYS keeps the effect regardless of
        /// this — caves, mines, the sewer, dungeons, and the bathhouse hot spring (see
        /// RenderPipeline.HasLevelWater) — since that water is part of the level, not decoration.</summary>
        public bool WaterEffectIndoors { get; set; } = true;
        /// <summary>Building interiors the player has individually opted OUT of the water effect,
        /// by NameOrUniqueName. Toggled per-room from the F6 tuner. Lets a player kill decorative
        /// water from one specific house/interior mod without turning it off everywhere. Only ever
        /// consulted for gate-able interiors (outdoors and level water ignore it).</summary>
        public List<string> WaterDisabledLocations { get; set; } = new();

        public bool VignetteEnabled { get; set; } = true;
        public float VignetteStrength { get; set; } = 0.25f;

        public bool ChromaticAberrationEnabled { get; set; } = true;
        public float ChromaticAberrationStrength { get; set; } = 0.3f; // 0..1 UI scale (scaled to a tiny UV offset)

        // --- Dynamic 2D lighting ---
        /// <summary>Flood-propagation GI lightmap (occlusion-aware ambient, shade under
        /// canopies, coloured lamp pools). Supersedes LightingEnabled when on.</summary>
        public bool FloodLightingEnabled { get; set; } = true;
        /// <summary>How strongly the flood lightmap modulates the scene (0..1).</summary>
        /// <remarks>
        /// 1.5.5 takes it from 0.63 to 0.45. Two players have now said the defaults are too strong,
        /// one of them putting it as being unable to see anything in a farmhouse until they found
        /// the settings. Measured in the saloon at six in the evening, against the same scene with
        /// the mod switched off: this stage alone carried half of a +25% red channel and all of a
        /// +35% rise in brightness, the other stages together accounting for the rest.
        /// <para>
        /// The number was worth more before 1.5.5 than it looks, because the hearth term was not
        /// scaled by it: turning the slider down by half moved the room by a couple of percent and
        /// people reasonably concluded the slider did nothing. It governs the whole stage now, so
        /// a lower default is a real change rather than a cosmetic one.
        /// </para>
        /// See <see cref="LightingBoost"/> for why this whole group was raised in the first place.
        /// </remarks>
        public float FloodLightingStrength { get; set; } = 0.45f;
        /// <summary>How dark a fully occluded per-light ray gets (0 = no shadows, 1 = black).</summary>
        /// <remarks>Lowered with the rest of the group: with the pools finally bright enough to
        /// read, a near-black occluded ray beside them was all contrast and no shape.</remarks>
        public float FloodShadowStrength { get; set; } = 0.74f;
        /// <summary>Gates the VISIBLE window work (the beam, the lit glass, the patch of sun on the
        /// floor) and the warm glow on house windows outdoors at night. It does NOT gate the
        /// daylight a window adds to the room's own lighting - that half answers to
        /// WindowRoomLightEnabled, so a player who turns the flashy effect off still has lit
        /// rooms. Rooms also still follow the time of day; that is not a window effect.</summary>
        public bool WindowEffectsEnabled { get; set; } = true;
        /// <summary>The VISIBLE half of indoor window daylight: the lit glass, the beam leaning out
        /// of it, and the patch of sun it lays on the floor. This is the half a dedicated window mod
        /// draws too (Dynamic Windows ships a shaft sprite and a fill sprite for exactly these), so
        /// it is the half to give away when running one.</summary>
        public bool WindowBeamEnabled { get; set; } = true;
        /// <summary>Which mod, if any, we already stepped aside for once. Stops the compatibility
        /// default from being reapplied on every launch and overriding a deliberate choice.</summary>
        public string WindowCompatAppliedFor { get; set; } = "";
        /// <summary>Darken flat/unlit areas and pool light around real light sources.</summary>
        public bool LightingEnabled { get; set; } = true;
        /// <summary>How dark interiors get (vanilla leaves them flat-bright). 0 = none, 1 = very dark.</summary>
        public float LightingIndoorDarkness { get; set; } = 0.68f;
        /// <summary>Extra darkening at night where we own the lighting. 0 = none.</summary>
        public float LightingNightDarkness { get; set; } = 0.56f;
        /// <summary>How much of the night darkening carries into the early morning (before 7:00).
        /// 0 wakes in a bright room; higher = darker mornings. The historical look used 0.25.</summary>
        public float LightingMorningDarkness { get; set; } = 0.25f;
        /// <summary>Warmth of the light pools (0 = neutral white, 1 = candle-orange).</summary>
        public float LightingWarmth { get; set; } = 0.55f;
        /// <summary>Scale the on-screen radius of every light pool.</summary>
        public float LightingRadiusScale { get; set; } = 1.31f;
        /// <summary>
        /// Brightness of the light pools added back over the darkened scene.
        /// </summary>
        /// <remarks>
        /// Raised from 0.27, which was below the level at which a lamp can brighten anything at
        /// all. The flood shader multiplies the scene by the lightmap CLAMPED AT ONE, so that term
        /// can only ever darken; the single line that adds light needs the lightmap to pass one
        /// first. At 0.27 a pool centre reached about half of that, so the adding term was exactly
        /// zero in every interior, for everyone on the defaults, always. Lamps could make a floor
        /// less dark and never bright, which is precisely the "night is dark and the lit places are
        /// dark too" this group was tuned against. This clears the bar with room to spare.
        /// </remarks>
        public float LightingBoost { get; set; } = 0.89f;
        /// <summary>Cast hard-edge shadows from tall/solid tiles that block light.</summary>
        public bool LightingShadows { get; set; } = true;
        /// <summary>How dark occluder shadows are. 0 = none, 1 = full.</summary>
        public float LightingShadowStrength { get; set; } = 0.4f;

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
        /// <summary>How many shadows one character may cast indoors and after dark, nearest light
        /// first. A look control and a cost control at once: each cast is a full soft silhouette,
        /// so this is a direct multiplier on what characters cost to shadow, and past about three
        /// the shadows stop reading as "lit from a few directions" and start reading as a smudge.
        /// The sun outdoors is one light and is not affected.</summary>
        public int ShadowCastsPerCharacter { get; set; } = 3;
        /// <summary>One shadow: the cheapest setting that still grounds a body.</summary>
        public const int ShadowCastsMin = 1;
        /// <summary>Where it used to sit before it was a setting. Higher reads as a smudge.</summary>
        public const int ShadowCastsMax = 6;
        /// <summary>Minimum light RADIUS (in tiles) for a point light to cast per-light shadows.
        /// Tiny transient lights from other mods — fireflies (JP's The Night Lights), sparkles —
        /// have a sub-1 radius; each moving one threw its own drifting shadow on the player. Lights
        /// below this never cast (their glow is unaffected). Windows always cast regardless.</summary>
        public float MinShadowLightRadius { get; set; } = 1.0f;

        /// <summary>
        /// Normalize every numeric field to its supported range. GMCM sliders only protect
        /// values entered through the UI — hand-edited config.json flows straight into the
        /// shaders (e.g. GodRaysDecay > 1 grows exponentially into an additive white-out).
        /// Called after ReadConfig and on every GMCM save.
        /// </summary>
        public void Clamp()
        {
            static float ClampToRange(float v, float lo, float hi) => float.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);

            RenderScale = ClampToRange(RenderScale, 0.5f, 1f);
            RenderSharpness = ClampToRange(RenderSharpness, 0f, 2f);
            BloomThreshold = ClampToRange(BloomThreshold, 0f, 1f);
            BloomIntensity = ClampToRange(BloomIntensity, 0f, 2f);
            ColorGradeStrength = ClampToRange(ColorGradeStrength, 0f, 1f);
            ColorGradeContrast = ClampToRange(ColorGradeContrast, 0.5f, 1.5f);
            ColorGradeSaturation = ClampToRange(ColorGradeSaturation, 0f, 2f);
            ColorGradeTemperature = ClampToRange(ColorGradeTemperature, -1f, 1f);
            ColorGradeBrightness = ClampToRange(ColorGradeBrightness, 0.5f, 1.5f);
            GodRaysIntensity = ClampToRange(GodRaysIntensity, 0f, 1.5f);
            GodRaysThreshold = ClampToRange(GodRaysThreshold, 0f, 1f);
            GodRaysDensity = ClampToRange(GodRaysDensity, 0.1f, 1f);
            GodRaysDecay = ClampToRange(GodRaysDecay, 0.5f, 0.99f);
            FogDensity = ClampToRange(FogDensity, 0f, 1f);
            FogNightMistDensity = ClampToRange(FogNightMistDensity, 0f, 1f);
            FogCoverage = ClampToRange(FogCoverage, 0f, 1f);
            FogNightMistCoverage = ClampToRange(FogNightMistCoverage, 0f, 1f);
            FogNightMistSpeed = ClampToRange(FogNightMistSpeed, 0f, 0.1f);
            FogScale = ClampToRange(FogScale, 1f, 8f);
            FogSpeed = ClampToRange(FogSpeed, 0f, 0.1f);
            FogTopBias = ClampToRange(FogTopBias, 0f, 1f);
            CloudShadowOpacity = ClampToRange(CloudShadowOpacity, 0f, 0.7f);
            CloudShadowCoverage = ClampToRange(CloudShadowCoverage, 0.1f, 0.9f);
            CloudShadowScale = ClampToRange(CloudShadowScale, 1f, 5f);
            CloudShadowCount = ClampToRange(CloudShadowCount, 0f, 1f);
            CloudShadowSpeed = ClampToRange(CloudShadowSpeed, 0f, 0.1f);
            TiltShiftStrength = ClampToRange(TiltShiftStrength, 0f, 1f);
            TiltShiftRadius = ClampToRange(TiltShiftRadius, 0.05f, 0.9f);
            TiltShiftFeather = ClampToRange(TiltShiftFeather, 0f, 1f);
            TiltShiftTopRatio = ClampToRange(TiltShiftTopRatio, 0f, 1f);
            TiltShiftBottomRatio = ClampToRange(TiltShiftBottomRatio, 0f, 1f);
            WaterStrength = ClampToRange(WaterStrength, 0f, 2f);
            WaterSpeed = ClampToRange(WaterSpeed, 0f, 3f);
            WaterSparkle = ClampToRange(WaterSparkle, 0f, 1f);
            WaterSparkleDensity = ClampToRange(WaterSparkleDensity, 0.2f, 2f);
            WaterReflectStrength = ClampToRange(WaterReflectStrength, 0f, 1f);
            VignetteStrength = ClampToRange(VignetteStrength, 0f, 1f);
            ChromaticAberrationStrength = ClampToRange(ChromaticAberrationStrength, 0f, 1f);
            BlueLightFilter = ClampToRange(BlueLightFilter, 0f, 1f);
            FloodLightingStrength = ClampToRange(FloodLightingStrength, 0f, 1f);
            FloodShadowStrength = ClampToRange(FloodShadowStrength, 0f, 1f);
            LightingIndoorDarkness = ClampToRange(LightingIndoorDarkness, 0f, 0.95f);
            LightingNightDarkness = ClampToRange(LightingNightDarkness, 0f, 0.95f);
            LightingWarmth = ClampToRange(LightingWarmth, 0f, 1f);
            LightingBoost = ClampToRange(LightingBoost, 0f, 2f);
            LightingRadiusScale = ClampToRange(LightingRadiusScale, 0.2f, 3f);
            LightingShadowStrength = ClampToRange(LightingShadowStrength, 0f, 1f);
            DirectionalShadowStrength = ClampToRange(DirectionalShadowStrength, 0f, 1f);
            MinShadowLightRadius = ClampToRange(MinShadowLightRadius, 0f, 3f);
            DirectionalShadowLength = ClampToRange(DirectionalShadowLength, 0.2f, 2f);
            DirectionalShadowBlur = ClampToRange(DirectionalShadowBlur, 0f, 5f);
            ShadowCastsPerCharacter = Math.Clamp(ShadowCastsPerCharacter, ShadowCastsMin, ShadowCastsMax);
            CameraFollowSpeed = ClampToRange(CameraFollowSpeed, 0.05f, 1f);
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
        /// <summary>Every user-tunable property: bool/int/float/enum, excluding the master
        /// switch, debug logging, and the preset bookkeeping itself. Reflection-based so
        /// new effect settings are picked up by saved looks automatically.</summary>
        private static IEnumerable<PropertyInfo> TunableProps()
        {
            foreach (PropertyInfo p in typeof(ModConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite)
                    continue;
                if (p.Name is nameof(Enabled) or nameof(ActivePreset) or nameof(DebugLogging))
                    continue;
                Type t = p.PropertyType;
                if (t == typeof(bool) || t == typeof(int) || t == typeof(float) || t.IsEnum)
                    yield return p;
            }
        }

        /// <summary>Snapshot the full current look (1.0.0 captured only ~a third of the
        /// settings, which read as "loading my preset does nothing" for everything else).</summary>
        public NamedProfile CaptureProfile(string name)
        {
            var prof = new NamedProfile { Name = name, Values = new Dictionary<string, string>() };
            foreach (PropertyInfo p in TunableProps())
                prof.Values[p.Name] = Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture) ?? "";
            return prof;
        }

        /// <summary>Load a saved profile's settings into the live config.</summary>
        public void ApplyProfile(NamedProfile p)
        {
            // Full snapshot (1.0.1+ chips).
            if (p.Values is { Count: > 0 })
            {
                foreach (PropertyInfo prop in TunableProps())
                {
                    if (!p.Values.TryGetValue(prop.Name, out string? raw) || string.IsNullOrEmpty(raw))
                        continue;
                    try
                    {
                        Type t = prop.PropertyType;
                        object val = t.IsEnum ? Enum.Parse(t, raw)
                            : t == typeof(bool) ? bool.Parse(raw)
                            : t == typeof(int) ? int.Parse(raw, CultureInfo.InvariantCulture)
                            : float.Parse(raw, CultureInfo.InvariantCulture);
                        prop.SetValue(this, val);
                    }
                    catch
                    {
                        // value written by a different mod version — skip just that key
                    }
                }
                Clamp();
                return;
            }

            // Legacy 1.0.0 chips (only ever captured the fields below).
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
            FogNightMist = p.FogNightMist;
            FogDensity = p.FogDensity;
            Clamp();
        }

        /// <summary>Apply a quality preset. Touches only what costs frames — never the grade,
        /// the temperature, or any other artistic control — so a chosen look survives it.</summary>
        public void ApplyPerfPreset(PerfPreset preset)
        {
            switch (preset)
            {
                case PerfPreset.Quality:
                    RenderScale = 1f;
                    TiltShiftEnabled = true;
                    ChromaticAberrationEnabled = true;
                    FloodLightingEnabled = true;
                    WaterReflection = true;
                    DirectionalShadowObjects = true;
                    ShadowCastsPerCharacter = 3;
                    break;

                case PerfPreset.Balanced:
                    RenderScale = 0.75f;
                    // Both are lens dressing, and turning CA off also merges the grade and
                    // finishing passes into one (see the tail pass) - two savings for one loss.
                    TiltShiftEnabled = false;
                    ChromaticAberrationEnabled = false;
                    FloodLightingEnabled = true;
                    WaterReflection = true;
                    DirectionalShadowObjects = true;
                    // A body lit from two sides still reads as a lit room; the third shadow is
                    // the one that adds atmosphere rather than information.
                    ShadowCastsPerCharacter = 2;
                    break;

                case PerfPreset.Performance:
                    RenderScale = 0.5f;
                    TiltShiftEnabled = false;
                    ChromaticAberrationEnabled = false;
                    // Flood GI is the pricier of the two lighting models; classic lighting
                    // keeps rooms lit and lamps pooled for a fraction of the work.
                    FloodLightingEnabled = false;
                    LightingEnabled = true;
                    // Per-object shadow bakes scale with how much scenery is on screen, which
                    // is exactly what a weak machine cannot afford. Characters keep theirs.
                    DirectionalShadowObjects = false;
                    // Every extra light a character answers to is another full soft silhouette
                    // drawn for that character. One keeps everyone grounded for a third of the
                    // work of the default, which matters most in exactly the scene that hurts:
                    // a town at night, where the lamps are many and so are the people.
                    ShadowCastsPerCharacter = 1;
                    // Reflections stay: they are the reason to run this mod, and the scenery
                    // cache already took most of their cost away.
                    WaterReflection = true;
                    break;
            }
            Clamp();
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
                    // The LIGHT too, from 1.5.5. Until now this preset only touched bloom and the
                    // colour grade, and measurement says those are the small half: in the saloon
                    // the grade accounts for about +5% on the red channel and the lighting for
                    // +13%, and only the lighting changes the brightness at all. Somebody choosing
                    // "Subtle" and still getting the lighting at full strength has been given the
                    // name of the thing they asked for and not the thing.
                    FloodLightingStrength = 0.30f;
                    LightingBoost = 0.70f;
                    LightingWarmth = 0.35f;
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
