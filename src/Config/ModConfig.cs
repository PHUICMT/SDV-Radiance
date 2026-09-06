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
        Choppy,
        /// <summary>Never a look of its own any more. For one build between 1.6.1 and 1.6.2 the
        /// new water was a fourth entry here; it is <see cref="WaterReflectionModel.Modern"/>
        /// now, and a config written by that build still parses because this member exists.
        /// <see cref="ModConfig.Clamp"/> turns it into Modern plus Natural.</summary>
        Realistic
    }

    /// <summary>
    /// Which water the mirror is: the one that shipped through 1.6.1, or the one built for 1.6.2.
    /// </summary>
    /// <remarks>
    /// The classic water keeps every line of its maths and its three looks
    /// (<see cref="WaterReflectionStyle"/>), so a player who liked it keeps it. The modern one is
    /// a different mirror: the image is moved by a travelling field of ripples rather than by the
    /// surface's sine, anchored at the waterline and breaking further out, folded in contrast and
    /// pulled toward the water's own colour with depth, stretched a little, and answering the
    /// camera's place on the screen. Each has dials of its own; the menus show the chosen one's.
    /// </remarks>
    public enum WaterReflectionModel
    {
        /// <summary>The 1.6.2 water.</summary>
        Modern,
        /// <summary>The water of every release up to 1.6.1, with its three looks.</summary>
        Classic
    }

    /// <summary>
    /// Which shape a cast shadow is: the one built for 1.7, or the one of 1.6.
    /// </summary>
    /// <remarks>
    /// Two things about a shadow's shape changed in 1.7 and neither had a dial of its own, so a
    /// player who preferred what they had could not ask for it back. A placed thing's shadow now
    /// stands on the row its art really ends on instead of hanging from its cell, and a
    /// four-legged creature lies down across the ground instead of standing up on edge the way a
    /// person does. Each moved every shadow of its kind at once, which is the sort of change that
    /// earns a switch.
    ///
    /// SHAPE only. What 1.7 fixed stays fixed in both: creatures from other mods still cast,
    /// riding still leaves you a shadow, and a horse still faces the way it is drawn. None of
    /// those was a look anybody chose.
    /// </remarks>
    public enum ShadowModel
    {
        /// <summary>The 1.7 shapes.</summary>
        Modern,
        /// <summary>The shapes of every release up to 1.6.</summary>
        Classic
    }

    /// <summary>Which model computes the flood GI lightmap. Both read the same lights and the same
    /// occluders and hand floodlight.fx the same kind of texture; a switch cross-fades between them.</summary>
    /// <summary>What the smoothed sheets look like. Both are baked once per sheet on the card.</summary>
    public enum SheetSmoothingStyle
    {
        /// <summary>The 1.7 look: twice the texels by the Scale2x rule, corners rounded, every
        /// edge still a pixel edge.</summary>
        Scale2x,
        /// <summary>Four times the texels (Scale2x twice over) with the edges spread by a quarter
        /// of a pixel: the soft, rounded look a texture-upscaler mod gives the art. Sixteen times a
        /// sheet's bytes, so only the small sheets get it; the large ones stay doubled.</summary>
        Soft4x,
    }

    public enum GiModel
    {
        /// <summary>The CPU sweep of every release so far: light floods tile by tile with a per-cell
        /// decay, then a blur stands in for the bounce.</summary>
        Flood,
        /// <summary>Radiance cascades on the GPU: probes cast rays that stop at what they meet, in
        /// cascades that share the far field between neighbours. Two probes per tile.</summary>
        Cascades
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
        Performance,
        /// <summary>
        /// For a machine that cannot hold its frame rate on Performance: a phone, an old laptop,
        /// an install already carrying two hundred other mods.
        ///
        /// <para>
        /// Performance keeps reflections because they are the reason to run this mod at all. This
        /// one gives them up, along with the ray marches, and keeps only what costs a full-screen
        /// pass or less: bloom, colour grading and the surface shimmer. The result still does not
        /// look like vanilla, which is the point of having it rather than telling somebody to
        /// uninstall.
        /// </para>
        /// </summary>
        LowSpec
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
        public string ColorGradeLut { get; set; } = "";
        public float ColorGradeLutAmount { get; set; }
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
        /// <summary>The quality preset picked last, or none. Only so the tuner can highlight it;
        /// nothing reads it to decide how anything runs.</summary>
        public PerfPreset? ActivePerfPreset { get; set; }

        /// <summary>Resolution the EFFECT chain runs at, as a fraction of the window. 1 = native.
        /// The game still draws the world at full size; only our passes work on a smaller image
        /// and the finished frame is scaled back up, so the saving is quadratic (0.5 = a quarter
        /// of the fill cost). Point sampling both ways keeps the pixel art hard-edged: the art is
        /// already magnified ~4x on screen, so at 0.5 a texel still covers less than one game
        /// pixel and the blocks come back intact. Values between the two are a genuine resample -
        /// softer, and the block grid can shimmer while the camera moves.</summary>
        public float RenderScale { get; set; } = 1.0f;

        /// <summary>Let the mod lower the render scale by itself when the frame is not keeping up,
        /// and give it back when it is. <see cref="RenderScale"/> stays the ceiling: this only ever
        /// asks for less than was chosen.
        ///
        /// <para>Off by default, and turned on by the Performance and Low spec presets. This is the
        /// one setting with a quadratic effect and the one nobody in a performance report has
        /// mentioned finding, so the presets aimed at slow machines now reach for it on their
        /// behalf. See RenderPipeline.AutoScale.cs for what it will and will not do.</para></summary>
        public bool RenderScaleAuto { get; set; }

        /// <summary>How much of the upscale sharpening to apply, as a multiple of the tuned
        /// amount: 0 turns it off (plain bilinear stretch), 1 is the measured default, and the
        /// slider goes past that for anyone who likes it crisper. The tuned amount already
        /// rises as <see cref="RenderScale"/> falls, since a smaller buffer needs more help.
        /// Only does anything while the scale is below 1 — it lives inside the upscale, and at
        /// native resolution there is no upscale to sharpen.</summary>
        public float RenderSharpness { get; set; } = 1.0f;

        // --- Bloom ---
        public bool BloomEnabled { get; set; } = true;
        public float BloomThreshold { get; set; } = 0.72f;
        /// <remarks>0.35, not the 0.76 it shipped with: at more than double the Cinematic preset's
        /// own value, a fresh install did not look like the preset it claimed to be.</remarks>
        public float BloomIntensity { get; set; } = 0.35f;
        /// <remarks>Only saturated bright pixels qualify - a grey never does, however
        /// bright, which is the lesson the old god rays paid for.</remarks>
        public float BloomEmissiveBoost { get; set; } = 0.6f;

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
        // Stays OFF, as it has since the looks shipped. 1.5.7 put "off by default, so nothing
        // changes until you ask for it" in writing, and adopting the author's own config as the
        // shipped set swept this on with 67 other values. The colour of somebody else's game is
        // not a default to change quietly.
        public bool ColorGradeEnabled { get; set; } = false;
        public float ColorGradeStrength { get; set; } = 1.0f;
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
        public float ColorGradeContrast { get; set; } = 1.15f;
        public float ColorGradeSaturation { get; set; } = 1.05f;
        public float ColorGradeTemperature { get; set; } = 0.05f;
        public float ColorGradeBrightness { get; set; } = 1.0f;
        public bool ColorGradeToneMap { get; set; } = false;
        /// <summary>The LUT files that ship with the mod, in the order the dropdown offers them.
        /// An empty first entry is "no LUT". Anything else found in <c>assets/luts</c> is taken to
        /// be the player's own and offered after these - see <see cref="LutCatalog"/> - so this
        /// list is what SHIPPED, not the limit of what can be chosen.</summary>
        public static readonly string[] ShippedLuts =
        {
            "", "warm-film", "verdant", "autumn-gold", "moonlit", "cool-night", "washed-linen", "identity",
        };

        /// <summary>
        /// A colour lookup table laid over the finished grade: the name of a PNG in
        /// <c>assets/luts/</c>, without the extension. Empty means no LUT, which is the default,
        /// so nobody's picture changes until they ask for one.
        /// <para>
        /// The file is a 32x32x32 cube unrolled into a 1024x32 strip - what every LUT tool
        /// exports - so a player can drop their own in beside the seven that ship, one of which is
        /// <c>identity</c>: it changes nothing, and exists to prove the sampling is right.
        /// </para>
        /// </summary>
        public string ColorGradeLut { get; set; } = "";
        /// <summary>How much of the LUT to apply, 0..1. The sliders keep their meaning either
        /// way: the LUT is laid over the graded result, not swapped in for it.</summary>
        public float ColorGradeLutAmount { get; set; } = 1f;
        /// <summary>Auto-shift temperature/saturation by time of day, weather, and season.</summary>
        public bool ColorGradeAuto { get; set; } = true;
        /// <summary>Blue-light / eye-comfort filter: 0 = off .. 1 = strong warm shift (cuts blue,
        /// lifts red a touch). Applied on top of grading and independent of it, so it works even
        /// with color grading turned off.</summary>
        public float BlueLightFilter { get; set; } = 0.0f;

        /// <summary>Bumped when a release has to CHANGE a setting an existing config already
        /// holds. An old config has no such field and lands on 0, so the migration in ModEntry
        /// runs exactly once and then records that it did.</summary>
        public int ConfigVersion { get; set; }

        // --- God rays ---
        // Lamp shafts: beams a lamp throws through whatever stands beside its light, drawn inside
        // the flood pass from the occluder mask (floodlight.fx, LampShaftStrength). Off from 1.3.1
        // to 1.6.2 while they were a bright-pass that made every pale sprite a light. Rebuilt from
        // the occluders in 1.7.0 so that fault is gone, and still off: on a walk with a glow ring
        // the beams kept finding "gaps" in ordinary streets, and the author's call was that the
        // shadows carry the scene and the beams are a taste. Switch it on to try them.
        public bool GodRaysEnabled { get; set; }
        /// <summary>How strong the lamp shafts are. 1 is the tuned look; the old bright-pass dial
        /// meant something else entirely (an additive gain), so the migration resets it once.</summary>
        public float GodRaysIntensity { get; set; } = 1.0f;
        /// <summary>Let the SUN be a ray source, not only lamps and fires. See SetSunShaftParams.</summary>
        public bool GodRaysSun { get; set; } = true;
        /// <summary>How strong the sun's shafts are.
        /// <para>Its own dial rather than the lamp rays'. The two share a word and nothing else:
        /// a lamp ray is a streak drawn out of a bright pixel, a sun shaft is a march through the
        /// occluders of a canopy, and they land in different parts of the day. Sharing one slider
        /// meant that dimming the lamps at night dimmed the morning through the trees as well.
        /// The default is what the shared slider's default gave, so nothing moves until it is
        /// asked to.</para></summary>
        public float GodRaysSunIntensity { get; set; } = 0.68f;
        /// <summary>How far the sun's dapple reaches from the canopy that cuts it.</summary>
        public float GodRaysSunReach { get; set; } = 0.6f;

        // --- Volumetric fog ---
        public bool FogEnabled { get; set; } = false;
        /// <summary>Automatic subtle blue mist after dusk (outdoors, clear weather). Used to run
        /// implicitly whenever any effect was on — now opt-in so Fog OFF really means off.</summary>
        public bool FogNightMist { get; set; } = true;
        /// <summary>How thick the night-mist wisps get at deep night (0..1).</summary>
        public float FogNightMistDensity { get; set; } = 0.6f;
        public float FogDensity { get; set; } = 0.5f;   // wisp OPACITY (how strong each wisp tints)
        /// <summary>How much of the frame the day-fog wisps occupy (amount, not opacity).</summary>
        public float FogCoverage { get; set; } = 0.2f;
        /// <summary>How much of the frame the night-mist wisps occupy.</summary>
        public float FogNightMistCoverage { get; set; } = 0.25f;
        /// <summary>Night-mist drift speed.</summary>
        public float FogNightMistSpeed { get; set; } = 0.01f;
        public float FogScale { get; set; } = 3.0f;
        public float FogSpeed { get; set; } = 0.02f;
        public float FogTopBias { get; set; } = 0.5f;

        // --- Cloud shadows ---
        public bool CloudShadowEnabled { get; set; } = true;
        /// <summary>Hide the vanilla drifting <c>Cloud</c> critter shadow (so only our cloud shadow shows).</summary>
        public bool SuppressVanillaCloudShadow { get; set; } = true;
        public float CloudShadowScale { get; set; } = 1.0f;
        /// <summary>How many separate cloud banks share the screen (cluster frequency, 0..1).</summary>
        public float CloudShadowCount { get; set; } = 0.61f;
        public float CloudShadowSpeed { get; set; } = 0.03f;
        /// <summary>Default kept well under the 0.7 cap: 0.61 read as near-black to players.</summary>
        public float CloudShadowOpacity { get; set; } = 0.34f;
        public float CloudShadowCoverage { get; set; } = 0.38f;

        public bool TiltShiftEnabled { get; set; } = true;
        public TiltShiftFocus TiltShiftMode { get; set; } = TiltShiftFocus.Bands;
        public float TiltShiftTopRatio { get; set; } = 0.3f;    // top blur amount (0 = none … 1 = up to middle)
        public float TiltShiftBottomRatio { get; set; } = 0.3f; // bottom blur amount (0 = none … 1 = up to middle)
        public float TiltShiftStrength { get; set; } = 0.9f;
        public float TiltShiftRadius { get; set; } = 0.63f;     // radial mode: size of the sharp circle around the player
        public float TiltShiftFeather { get; set; } = 0.35f;    // softness of the sharp→blur edge (0 = crisp, 1 = very gradual)
        /// <summary>How much of the blur is kept indoors (0 = none, 1 = the same as outdoors).
        /// The bands read screen height as distance, which holds outdoors, where the top of the
        /// screen really is half a map away. A room is a few tiles deep from wall to floor, so
        /// the same ratios blur furniture standing barely further back than your own feet.
        /// The default is 1 on purpose: the argument above is geometry, not a measurement, and
        /// nothing has yet been looked at in game, so this ships as a control and changes no
        /// picture until somebody moves it.</summary>
        public float TiltShiftIndoorAmount { get; set; } = 1f;

        // --- Water + finishing ---
        public bool WaterEnabled { get; set; } = true;
        // Half of what shipped through 1.5.7, not the quarter the author's own config had been
        // sitting at. That 0.15 was a workaround for the ripple crawling along a bridge edge, and
        // the crawl is fixed, so the workaround should not have shipped as the value everyone gets.
        public float WaterStrength { get; set; } = 0.3f;   // ripple amplitude
        public float WaterSpeed { get; set; } = 0.81f;     // ripple animation speed
        public float WaterSparkle { get; set; } = 0.24f;   // specular glint intensity
        public float WaterSparkleDensity { get; set; } = 0.5f; // glint count/size (1 = old look)
        public bool WaterCausticsEnabled { get; set; } = true;  // the light net on shallow beds
        public float WaterCausticsStrength { get; set; } = 0.15f;
        public bool WaterReflection { get; set; } = true;  // screen-space reflection on water
        public float WaterReflectStrength { get; set; } = 0.51f;
        /// <summary>Which water the mirror is, the 1.6.2 one or the classic one; see
        /// <see cref="WaterReflectionModel"/>.</summary>
        public WaterReflectionModel WaterReflectModel { get; set; } = WaterReflectionModel.Modern;
        /// <summary>Which of the classic water's three looks is in use (see WaterReflectionStyle).
        /// Read only while <see cref="WaterReflectModel"/> is Classic.</summary>
        public WaterReflectionStyle WaterReflectStyle { get; set; } = WaterReflectionStyle.Natural;
        /// <summary>
        /// How tall a band of the reflection shares one sideways displacement, in world pixels.
        /// Zero shears every row on its own.
        ///
        /// <para>
        /// The ripple pushes the reflection sideways by an amount that depends on the row. Rounding
        /// that row to a step makes a band of pixels move together and then jump at the boundary,
        /// which is the drawn-water look pixel art usually wants, and is also what cuts a reflected
        /// building into horizontal slices sliding over each other. Which of those two descriptions
        /// you use is a matter of taste, so it is a setting: 0 for a surface that bends, 4 for the
        /// banding this mod shipped with through 1.5.6, more for a coarser stagger.
        /// </para>
        /// </summary>
        /// <summary>How far from the water a piece of SCENERY may stand and still be mirrored, as
        /// a fraction of the full reach. 1 = everything that can reach the surface; lower keeps the
        /// things at the water's edge and drops the distant ones.
        ///
        /// <para>
        /// This is the reflection's cost dial, and it exists because the only control we shipped was
        /// a switch. The mirror is stamped in four-source-row slices so the head fade can be drawn
        /// at all, which makes a tree canopy twenty-four draws and a tuft of grass twenty: a wooded
        /// shore is over a thousand draws a frame, and it measured as a third of a millisecond of
        /// pure CPU submission. Cutting the reach cuts entities, which cuts slices in proportion,
        /// and everything still mirrored looks exactly as it did.
        /// </para>
        ///
        /// <para>People, animals and critters are NOT scaled by this. They are few, they are what
        /// anyone looks at, and they are not where the cost is.</para></summary>
        /// <summary>Source rows per slice of a mirrored sprite: 4 is the smoothest fade and the
        /// most work, 16 is a visibly stepped one for about a third less.
        ///
        /// <para>
        /// A reflection is drawn in slices because that is how the fade toward its far end is
        /// produced: each slice carries its own alpha. Measured at two wooded shores, going from
        /// four rows to eight took 31-37% off the mirror, and going on to sixteen took almost
        /// nothing more - so about a third of this pass is the draw count and the rest is the
        /// per-entity work behind it. Eight is where the trade is, not sixteen.
        /// </para>
        ///
        /// <para>This is the cheaper of the two reflection dials to accept: it loses no reflection,
        /// only the smoothness of the gradient, where <see cref="WaterReflectReach"/> removes
        /// distant ones outright. They compose.</para></summary>
        public int WaterReflectFadeRows { get; set; } = 8;

        public float WaterReflectReach { get; set; } = 0.53f;
        /// <summary>
        /// How deep a scene reflection reaches into the water before it resolves to sky, as a
        /// multiplier on the bound the shader uses.
        ///
        /// <para>
        /// The bound ran 5 to 9 tiles through 1.5.3 and was raised to 9 to 16 when the mirror
        /// learned to read twelve tiles above the frame: before that the middle of any river or
        /// lake carried no reflection at all and read as flat paint. It is right for open water
        /// and long for a stream a tile or two across, where sixteen tiles of mirrored cliff is
        /// more water than there is.
        /// </para>
        ///
        /// <para>1 is the shipped bound; about 0.56 is the 1.5.3 one. The floor is 0.1, where a
        /// reflection is a hand's width of bank against the shore and everything past it is sky:
        /// the shader holds that floor too, so the bound can never reach zero and take the
        /// reflection with it.</para>
        /// </summary>
        public float WaterReflectDepth { get; set; } = 1.0f;

        public float WaterReflectBanding { get; set; } = 5.87f;
        /// <summary>
        /// How much the reflection is allowed to be distorted at all, scaling BOTH sources of it.
        /// At zero the reflection is a flat mirror.
        ///
        /// <para>
        /// The scenery's reflection is pushed about by two separate things: the wave shears it
        /// sideways one row at a time, and the ripple displaces the sample again. The named looks
        /// only ever touched the second, which is why Still Water could never reach a flat mirror
        /// however far it was turned down. This scales both, so the whole axis runs from a perfect
        /// mirror to more than the surface's own movement.
        /// </para>
        ///
        /// <para>
        /// Deliberately NOT the same control as ripple strength: how rough the water looks and how
        /// broken its reflection is were one number once, and separating them is the whole point of
        /// the named looks. Turning this to zero leaves the surface rippling and sparkling exactly
        /// as it did; only the image held in it stops moving.
        /// </para>
        /// </summary>
        public float WaterReflectDistort { get; set; } = 0.22f;
        /// <summary>How soft the reflection is, as a multiple of the softening the mod ships
        /// with. 1 is exactly that; 0 is a single crisp tap; 2 is twice the spread. It scales the
        /// depth-driven haze of the scenery mirror and the filtering of reflected bodies together,
        /// so a person and the tree behind them stay one surface at any setting.</summary>
        public float WaterReflectBlur { get; set; } = 2.0f;

        // --- The 1.6.2 water's own settings (WaterReflectionModel.Modern) ---
        // None of these do anything under the classic water, and the classic water's
        // distortion and banding do nothing under this one: the tuner shows whichever set the
        // chosen water can use. The classic water keeps every line of its maths.
        /// <summary>How far the travelling field may displace the image. 1 is about four world
        /// pixels up and down at the far end, a third of that sideways; 0 is a still image that still obeys the contact anchor.</summary>
        public float WaterModernWobble { get; set; } = 1.0f;
        /// <summary>How much of the two finer, faster octaves joins the slow wide one: 0 is a
        /// glassy pond with a single slow swell, 1 is a surface broken by wind.</summary>
        public float WaterModernChoppiness { get; set; } = 0.35f;
        /// <summary>How much the image slides with its place on the screen, the way a virtual
        /// image under the surface does for a camera that is not straight overhead. 0 is the
        /// flat screen-space flip the classic looks use.</summary>
        public float WaterModernParallax { get; set; } = 0.08f;
        /// <summary>How strongly the reflection gives way to the water's own colour with depth
        /// and folds its contrast toward a mid tone. 0 keeps the classic looks' plain tint.</summary>
        public float WaterModernFresnel { get; set; } = 0.7f;
        /// <summary>Vertical elongation of the reflected scene. Rough water draws reflections
        /// long; 1 is the plain oblique mirror, the same length as the classic water, and the
        /// default, because a tall bank's reflection already reads as long enough.</summary>
        public float WaterModernStretch { get; set; } = 1.0f;
        /// <summary>How far, in world pixels, the 1.6.2 water also samples its reflection to
        /// either side where the field is live. The field's bands cut a sloping reflected edge
        /// into teeth; this melts their tips while the bands still read. 0 is off.</summary>
        public float WaterModernEdgeSoftness { get; set; } = 2.5f;
        /// <summary>How fully the 1.6.2 water's reflection gives way to churned water under a
        /// waterfall. The pool at the foot of a fall is full of air and torn up and mirrors
        /// nothing there; it settles back over the reach below. 0 keeps the mirror right up to
        /// the foam.</summary>
        public float WaterModernPlungeChurn { get; set; } = 0.85f;
        /// <summary>How many tiles below the foot of a fall the churn runs before the mirror is
        /// back in full.</summary>
        public float WaterModernPlungeReach { get; set; } = 3f;
        /// <summary>How many tiles above the top of a fall the stream's mirror lets go over,
        /// so the reflection ends softly before the lip instead of on the lip's own texel.
        /// 0 ends it sharp.</summary>
        public float WaterModernLipFade { get; set; } = 0.5f;
        /// <summary>How many rings the rain strikes into open water, against the number the
        /// weather brings on its own. Below 1 fewer places on the surface take their turn;
        /// above it they all do and the pattern tightens.</summary>
        public float WaterRainRingDensity { get; set; } = 1.07f;
        /// <summary>How wide one ring grows before it dies.</summary>
        public float WaterRainRingSize { get; set; } = 1.11f;
        /// <summary>How plainly the rings and the bright point each drop lands on show.</summary>
        public float WaterRainRingStrength { get; set; } = 1.35f;
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
        public float VignetteStrength { get; set; } = 0.08f;

        public bool ChromaticAberrationEnabled { get; set; } = true;
        public float ChromaticAberrationStrength { get; set; } = 0.15f; // 0..1 UI scale (scaled to a tiny UV offset)

        // --- Dynamic 2D lighting ---
        /// <summary>Flood-propagation GI lightmap (occlusion-aware ambient, shade under
        /// canopies, coloured lamp pools). Supersedes LightingEnabled when on.</summary>
        public bool FloodLightingEnabled { get; set; } = true;
        /// <summary>Which model computes the GI lightmap (see <see cref="GiModel"/>). Cascades is the
        /// default from 1.7.0: at rest the two cost the same, but the flood's rebuild lands in one
        /// frame, and measured on a walk its worst frame was 2.33 ms against the cascades' 0.26 ms.
        /// Flood is still there, and is what a card without 16-bit colour targets falls back to.</summary>
        public GiModel FloodGiModel { get; set; } = GiModel.Cascades;
        /// <summary>Sprite relief: the world's sprites lit by each lamp and by the sun according to a
        /// normal map made from their own sheets (see SheetNormalCache). Off until it has been looked at;
        /// it also costs a second draw of the world's sprites and up to 192 MB of sheet maps.</summary>
        public bool SpriteReliefEnabled { get; set; } = false;
        /// <summary>How far a lamp's light leans across a sprite's relief (0..1).</summary>
        public float SpriteReliefStrength { get; set; } = 0.5f;
        /// <summary>The sun's share of the relief by day, outdoors (0..1).</summary>
        public float SpriteReliefSun { get; set; } = 0.35f;
        /// <summary>The bright fringe a lamp lays along the edge of a sprite facing it, in that
        /// lamp's own colour (0..1). Rides the same normal buffer as the relief, so it needs the
        /// relief on; unlike the lean it is ADDED, so it shows on an edge the art drew dark.</summary>
        public float SpriteReliefRim { get; set; } = 0.35f;
        /// <summary>Leaves in wind, as light: patches of canopy catch and lose the light the way
        /// leaf faces flip, travelling through the crown. Brightness only, so it cannot tear the
        /// art the way moving rows did; green-dominant pixels with relief coverage, so it needs
        /// the relief on and dims on a fall canopy (0..1).</summary>
        public float SpriteReliefLeafShimmer { get; set; } = 0.35f;
        /// <summary>Wind in the foliage: tree canopies and bushes tipping with the wind (see
        /// FoliageSway). On by default - it is one draw per sprite, the same one the game was
        /// making anyway, and the motion is a fraction of a degree.</summary>
        public bool FoliageSwayEnabled { get; set; } = true;
        /// <summary>How far the crown leans: 1 is under a pixel on a calm day and two in a storm (0..2).</summary>
        public float FoliageSwayStrength { get; set; } = 1f;
        /// <summary>Tempo of every sway motion at once; 1 is a big tree's natural pace (0.25..2).</summary>
        public float FoliageSwaySpeed { get; set; } = 1f;
        /// <summary>How many tiles one gust spans as it sweeps downwind across the map (4..40).</summary>
        public float FoliageSwayGustSpan { get; set; } = 14f;
        /// <summary>Sprites drawn from sheets doubled on the graphics card by the Scale2x rule (see
        /// SheetUpscaler): two texels where the game put one. Off until it has been looked at.</summary>
        public bool SheetUpscaleEnabled { get; set; } = false;
        /// <summary>Which look the smoothing has: the 1.7 doubling, or the soft four-times sheets.</summary>
        public SheetSmoothingStyle SheetUpscaleStyle { get; set; } = SheetSmoothingStyle.Scale2x;
        /// <summary>The single smoothing dial of 1.7.0 to 1.7.4. Read once by the ConfigVersion 4
        /// migration, which copies it into the five dials below, and not used after that.</summary>
        public float SheetUpscaleSmoothness { get; set; } = 1f;
        /// <summary>How far the doubled sheets go toward the smoothed art, one dial per art family:
        /// 0 keeps the game's own pixels, 1 is the full Scale2x corner rounding. Split because a
        /// player may want the world rounded and the faces left alone, or the other way. Baked into
        /// the sheets, so moving one re-makes that family's sheets once and costs nothing per frame
        /// after that (0..1).</summary>
        public float SheetUpscaleSmoothnessWorld { get; set; } = 1f;
        public float SheetUpscaleSmoothnessCharacters { get; set; } = 1f;
        public float SheetUpscaleSmoothnessPortraits { get; set; } = 1f;
        public float SheetUpscaleSmoothnessItems { get; set; } = 1f;
        public float SheetUpscaleSmoothnessInterface { get; set; } = 1f;
        /// <summary>Which art the doubling touches, split because smoothing is a taste per family:
        /// the world's sprites read well doubled while a portrait or the dialogue lettering may
        /// not. Portraits and characters are named by their sheet's content path; everything drawn
        /// in the game's UI mode (menus, dialogue, the HUD and the items shown in them) is the
        /// interface; items lying in the world are named by their sheet; the rest is the world.</summary>
        public bool SheetUpscaleWorld { get; set; } = true;
        public bool SheetUpscaleCharacters { get; set; } = true;
        public bool SheetUpscalePortraits { get; set; } = true;
        public bool SheetUpscaleItems { get; set; } = true;
        public bool SheetUpscaleInterface { get; set; } = false;
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
        public float FloodLightingStrength { get; set; } = 0.1f;
        /// <summary>How dark a fully occluded per-light ray gets (0 = no shadows, 1 = black).</summary>
        /// <remarks>Was 0.7 through 1.6.2, lowered when a near-black occluded ray beside a bright
        /// pool was all contrast and no shape. 1.7.0 rebuilt the shadow (it thins with the pool, cuts
        /// into the game's own glow, softens with distance) and at 0.7 the user pulled every dial to
        /// the top to see one, and called the top "beautiful". So the top is the default.</remarks>
        public float FloodShadowStrength { get; set; } = 1.0f;
        /// <summary>How much of the surrounding surfaces' colour the bounce field carries (0..1).
        /// Off by default: it tints the whole picture, and this mod's rule is that anything which
        /// does that is a taste the player turns on rather than one they have to discover and
        /// turn off.</summary>
        public float FloodColourBleed { get; set; } = 0f;
        /// <summary>How much of the GAME's own lamp glow is taken back where a light's ray is blocked.
        /// The game paints every lamp as a round glow before this mod runs; the per-light shadow
        /// above only shades what the mod adds, so without this a pool stayed round behind a trunk.
        /// 0 = leave the game's glow alone (pools stay round), 1 = remove it fully in shadow.</summary>
        public float LightShadowCarve { get; set; } = 0.76f;
        /// <summary>How soft a lamp shadow's edge gets with distance from what cast it. 1 is the
        /// tuned look, 0 the hard edge of the mask itself, 2 twice as soft.</summary>
        public float LightShadowSoftness { get; set; } = 1.45f;
        /// <summary>How finely a lamp's shadow is traced, as the ceiling on how many samples one
        /// ray may take. This is the one dial in this mod whose price is paid PER LAMP ON SCREEN,
        /// so it is the one that matters in a place with many of them.
        ///
        /// <para>Up to 1.6.2 a ray took twelve samples, full stop. 1.7.0 changed it to one sample
        /// per mask texel so that a ray could not step over a fence post and miss it, which is up
        /// to forty-eight - four times the reads, on every lamp, on every pixel that lamp reaches.
        /// The picture is better for it and the cost went somewhere: this machine could not
        /// measure it, and two players on weaker ones reported the release as slower with no new
        /// switch to turn off, because the change rode along inside a commit named for the
        /// feature beside it.</para>
        ///
        /// <para>0 is the twelve samples of 1.6.2 and its cost with it. 1 is the forty-eight of
        /// 1.7. The default stays at 1 because that is what shipped and what the look was tuned
        /// against; this exists so nobody has to choose between the mod and their frame rate, and
        /// so the next report can name a number instead of a feeling.</para></summary>
        public float LightShadowDetail { get; set; } = 1f;
        /// <summary>Share the lamp shadow detail between the lamps lighting the same place, rather
        /// than giving every one of them the full trace.
        ///
        /// <para>What a shadow ray costs is how many steps it takes. Stopping a ray early once it
        /// is fully blocked was tried and saved nothing measurable, so fewer steps is the only
        /// lever there is. The number of lamps marching at a pixel is what runs away: a saloon at
        /// night costs more than a lit street with fewer full-screen passes, because in a small
        /// room every shadowed lamp reaches every pixel and each one marches the whole way.</para>
        ///
        /// <para>Two lamps keep the whole dial; past that they share it, down to the twelve
        /// samples every release up to 1.6.2 took. That floor is the point: a room full of lamps
        /// can never trace coarser than a release nobody complained about, and detail is given up
        /// exactly where it is hardest to see, because a shadow edge under eight lamps is read
        /// against seven other lamps' light.</para></summary>
        public bool LightShadowDetailShared { get; set; } = true;
        /// <summary>Trace every lamp's shadow ray from every pixel, for the crispest edge, instead
        /// of tracing at half resolution and reading the answer back.
        ///
        /// <para>The ray is walked from the lamp to the pixel, asking what stands in the way, and
        /// it is the one cost this lighting pays per lamp per pixel: eight lamps on screen means
        /// eight rays at every pixel. Walking them at half resolution is a quarter as many, and
        /// the pass measures 0.168 ms against 0.228 in town at night, 0.244 against 0.424 in the
        /// saloon where all eight lamps reach every pixel.</para>
        ///
        /// <para>What it was thought to cost was the edge: a frozen saloon frame compared both
        /// ways moved 61% of its pixels, mean 6 of 255, up to 128, and that was read as the
        /// bilinear read-back softening the shadow. It was a bug (MarchBase in floodlight.fx):
        /// lamps four to seven marched from another lamp's position. With it fixed, the same
        /// comparison at three town spots at dawn moves 0.00% of pixels past 24 of 255. Off by
        /// default; the switch stays for anyone who can see a difference this cannot find.</para></summary>
        public bool LightShadowSharpEdges { get; set; }
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
        /// <summary>How bright the daylight through an indoor window is drawn: the lit pane, the
        /// beam and the patch of sun on the floor together. 1 is the shipped look. Asked for by a
        /// player whose farmhouse window looked right while the two big windows of a villager's
        /// kitchen blew out to white, and whose only remedy was the master switch, which also took
        /// the farmhouse's morning with it. The light a window adds to the room's own lighting is
        /// separate and does not move with this.</summary>
        public float WindowDaylightStrength { get; set; } = 1f;
        /// <summary>The same dial for every interior that is not the player's own house: shops,
        /// villagers' homes, the saloon. Asked for on 2026-08-25 with screenshots of a farmhouse
        /// that read right beside a villager's home that blew out to white: one dial could only
        /// fix one of them. 1 is the shipped look, the same as the dial above.</summary>
        public float WindowDaylightStrengthElsewhere { get; set; } = 1f;
        /// <summary>People walking past a window show faintly in its glass by day. Glass reflects
        /// when what is behind it is darker than what is in front, so this is the daytime twin of
        /// the night glow and fades out as the glow fades in; no setting can make a window do
        /// both. Outdoors only for now.</summary>
        public bool WindowReflectionEnabled { get; set; } = true;
        /// <summary>How strong the image in the glass is by day, as a multiple of the built-in
        /// ladder (a mirror full, glass a third, a house window a fifth).</summary>
        /// <remarks>The five window numbers here are not round because they are not guesses: they
        /// are where the author's own hands left the sliders after an evening of looking at Pierre's
        /// front and the clinic next to it, in a modded town at seven in the morning and at ten at
        /// night, rounded only to the nearest step the config menu can reach.</remarks>
        public float WindowReflectionStrength { get; set; } = 1.35f;
        /// <summary>The same, after dusk. Separate because the two are different pictures: by day
        /// the glass has a dark room behind it and returns the street plainly, and at night it is
        /// lit from inside and keeps only a suggestion. The mod eases between them on the same
        /// ramp the window glow rides.</summary>
        public float WindowReflectionNightStrength { get; set; } = 0.4f;
        /// <summary>How much sky the glass itself holds: the faint wash of sky colour over a pane
        /// and the diagonal streaks across it, which are there whether or not anyone is walking
        /// past. Separate from the reflection because it answers a different complaint: a window
        /// with nobody near it read as a hole in a wall.</summary>
        public float WindowSheenStrength { get; set; } = 0.61f;
        /// <summary>The soft blot of light that travels across a pane as it crosses the screen.
        /// Its own dial rather than part of the wash above, because the wash is what the pane is
        /// holding and this is what it is catching: one is still and one moves.</summary>
        public float WindowGlareStrength { get; set; } = 0.57f;
        /// <summary>How brightly the street in front of a pane stands in its glass. Reads the same
        /// sprite-free map render the water mirror uses, so it costs a strip of an image that is
        /// already being made rather than a second one.</summary>
        public float WindowSceneReflectionStrength { get; set; } = 1.5f;
        /// <summary>How brightly the lamps of the street stand in the glass after dark. A separate
        /// dial from the daytime picture because it is a separate picture: by day a pane returns
        /// the person in front of it, by night it returns the lights.</summary>
        public float WindowLightGlowStrength { get; set; } = 0.5f;
        /// <summary>Which mod, if any, we already stepped aside for once. Stops the compatibility
        /// default from being reapplied on every launch and overriding a deliberate choice.</summary>
        public string WindowCompatAppliedFor { get; set; } = "";

        // --- Particles ---
        /// <summary>The master switch for everything the particle pool draws.
        /// <para>Off for its first release, the way the colour looks shipped: this adds things to
        /// the picture that were never there, and a player who updates a mod should not find their
        /// game changed until they ask for it.</para></summary>
        public bool ParticlesEnabled { get; set; } = true;
        /// <summary>The master amount, over every emitter at once. A look control and a cost
        /// control together, and the first thing to turn down on a machine that is struggling;
        /// each emitter then has its own amount on top of this one.
        /// <para>Every emitter follows the same three settings: whether it runs, how much of it
        /// there is, and how big each piece is. They multiply this master rather than replacing
        /// it, so one dial still turns everything down at once.</para></summary>
        public float ParticleDensity { get; set; } = 1.05f;

        /// <summary>Dust hanging in the daylight that comes through a window. Indoors only:
        /// outdoors the sun shafts draw motes of their own.</summary>
        public bool ParticleDust { get; set; } = true;
        /// <summary>How much dust, against the amount the beam would carry on its own.</summary>
        public float ParticleDustAmount { get; set; } = 1.4f;
        /// <summary>How big one mote is, against its own size. Dust is the one thing here whose
        /// right size is a matter of taste rather than of what it is: a speck reads as air, a
        /// larger one reads as a room somebody has not swept.</summary>
        public float ParticleDustSize { get; set; } = 0.82f;

        /// <summary>Sparks rising off anything the game calls a flame: a hearth, a brazier, a
        /// torch, a wall sconce.</summary>
        public bool ParticleEmbers { get; set; } = true;
        /// <summary>How many sparks a fire throws, against the number it throws on its own.</summary>
        public float ParticleEmbersAmount { get; set; } = 1.15f;
        /// <summary>How big one spark is, against its own size.</summary>
        public float ParticleEmbersSize { get; set; } = 1.15f;

        /// <summary>Hot air over lava and hot springs bends what is seen through it, the way
        /// air over a summer road does. Found from the same labels the water reads.</summary>
        public bool HeatHazeEnabled { get; set; } = true;
        /// <summary>How far the hot air bends the picture, against its own amount.</summary>
        public float HeatHazeStrength { get; set; } = 1.0f;

        /// <summary>Mist thrown up where a waterfall lands, found from the same painted labels
        /// the water mask reads (flowing tiles stacked into a column).</summary>
        public bool ParticleWaterfallMist { get; set; } = true;
        /// <summary>How much mist a fall throws, against the amount it throws on its own.</summary>
        public float ParticleWaterfallMistAmount { get; set; } = 1.0f;
        /// <summary>How big one puff is, against its own size.</summary>
        public float ParticleWaterfallMistSize { get; set; } = 1.0f;

        /// <summary>Steam standing over water labelled hot: the bathhouse pool, a modded onsen.</summary>
        public bool ParticleHotSpringSteam { get; set; } = true;
        /// <summary>How much steam the water gives off, against its own amount.</summary>
        public float ParticleHotSpringSteamAmount { get; set; } = 1.0f;
        /// <summary>How big one wisp is, against its own size.</summary>
        public float ParticleHotSpringSteamSize { get; set; } = 1.0f;

        /// <summary>Sparks popping off lava, labelled or the volcano's own.</summary>
        public bool ParticleLavaSparks { get; set; } = true;
        /// <summary>How many sparks the lava throws, against its own number.</summary>
        public float ParticleLavaSparksAmount { get; set; } = 1.0f;
        /// <summary>How big one spark is, against its own size.</summary>
        public float ParticleLavaSparksSize { get; set; } = 1.0f;

        /// <summary>Fireflies over a field on a summer night, on top of the game's own.</summary>
        public bool ParticleFireflies { get; set; } = true;
        /// <summary>How many are in the air, against the number that fly on their own.</summary>
        public float ParticleFirefliesAmount { get; set; } = 1.0f;
        /// <summary>How big one is, against its own size.</summary>
        public float ParticleFirefliesSize { get; set; } = 1.0f;

        /// <summary>Blossom on the wind in spring, leaves in summer and autumn. Outdoors, and on
        /// the calm days the game's own debris weather leaves empty.
        /// <para>Off even when particles are on, unlike the other three. This is the one that adds
        /// something to open ground the whole time you are looking at it, rather than to a beam or
        /// a fire you have to be standing near, so it is the one most likely to be a change
        /// somebody did not want. The others are switched on inside a system that is itself off
        /// until asked for.</para></summary>
        public bool ParticlePetals { get; set; } = true;
        /// <summary>How much is in the air, against the amount that blows on its own.</summary>
        public float ParticlePetalsAmount { get; set; } = 1.0f;
        /// <summary>How big one petal or leaf is, against its own size.</summary>
        public float ParticlePetalsSize { get; set; } = 1.0f;
        /// <summary>How much a falling leaf or petal buckles as it turns through the air. A thin
        /// thing does not fall flat, and one crossing our own water surface already bends because
        /// the surface bends it; this carries the same bend everywhere. 0 is the flat fall of every
        /// release before this one.</summary>
        public float ParticlePetalsFlutter { get; set; } = 0.6f;

        /// <summary>Sparks turning around a player wearing a glow ring, so the light it casts has
        /// somewhere visible to have come from.</summary>
        public bool ParticleRingSparkles { get; set; } = true;
        /// <summary>How many turn around them, against the number that do on their own.</summary>
        public float ParticleRingSparklesAmount { get; set; } = 1.0f;
        /// <summary>How big one is, against its own size.</summary>
        public float ParticleRingSparklesSize { get; set; } = 1.0f;
        // --- Precipitation (replacement rain and snow) ---
        /// <summary>Draw rain and snow ourselves instead of letting the game draw them.
        /// <para>Off for its first release, like the particles: rain is something every player has
        /// looked at for hundreds of hours, and swapping it under them without being asked is not
        /// this mod's way. On, rain becomes layered streaks with wind and splashes, and snow
        /// becomes drifting flakes instead of a scrolling tiled texture.</para></summary>
        public bool PrecipitationEnabled { get; set; } = true;
        /// <summary>Aurora curtains in the water's reflected sky on clear winter nights.</summary>
        public bool AuroraEnabled { get; set; } = true;
        /// <summary>How brightly the aurora curtains show, 0 to 2, 1 being the shipped look.
        /// <para>It ships with a dial because the first build had none and the curtains landed at
        /// about five values out of 255 on night water, which is a number nobody can see. What
        /// reaches the screen is this dial times the curtain's own falloff times the quarter of
        /// itself the sky contributes to open water, so the honest range is wide.</para></summary>
        public float AuroraStrength { get; set; } = 1f;
        /// <summary>A shooting star now and then in the water's reflected sky on clear nights.</summary>
        public bool ShootingStarsEnabled { get; set; } = true;
        /// <summary>Replace the rain, green rain included (same streaks, shifted lime and heavier).</summary>
        public bool PrecipitationRain { get; set; } = true;
        /// <summary>Replace the snow.</summary>
        public bool PrecipitationSnow { get; set; } = true;
        /// <summary>How much rain is in the air, against the amount a storm brings on its own.</summary>
        public float PrecipitationRainDensity { get; set; } = 1.09f;
        /// <summary>How big one streak is, against its own size.</summary>
        public float PrecipitationRainSize { get; set; } = 1.0f;
        /// <summary>How strongly the rain shows, against its own weight. Amount, size and
        /// visibility are three different complaints: too few, too small, too faint.</summary>
        public float PrecipitationRainOpacity { get; set; } = 1.0f;
        /// <summary>How much snow is in the air, against the amount a snowfall brings on its own.</summary>
        public float PrecipitationSnowDensity { get; set; } = 1.48f;
        /// <summary>How big one flake is, against its own size.</summary>
        public float PrecipitationSnowSize { get; set; } = 1.08f;
        /// <summary>How strongly the snow shows, against its own weight.</summary>
        public float PrecipitationSnowOpacity { get; set; } = 1.1f;
        /// <summary>Replace the wind-day debris: blossom in spring, leaves the rest of the year,
        /// in three depth layers that tumble and ride the wind, instead of the game's flat
        /// fluttering chunks.</summary>
        public bool PrecipitationWind { get; set; } = true;
        /// <summary>How much is on the wind, against the amount a windy day blows on its own.</summary>
        public float PrecipitationWindDensity { get; set; } = 1.0f;
        /// <summary>How big one leaf or petal is, against its own size.</summary>
        public float PrecipitationWindSize { get; set; } = 1.0f;
        /// <summary>How strongly the leaves show, against their own weight.</summary>
        public float PrecipitationWindOpacity { get; set; } = 1.0f;

        /// <summary>How much heavier a thunderstorm's rain falls than plain rain: the number of
        /// drops in the air, multiplied. The game itself draws one rain for every weather; this
        /// is ours. 1 makes a storm look like rain.</summary>
        public float PrecipitationStormDensity { get; set; } = 1.6f;
        /// <summary>How hard the rain leans. It tilts the streaks and carries them sideways by the
        /// same amount, so the angle they are drawn at is the angle they really travel at. 1 is the
        /// wind as the game reports it and 0 is dead vertical; above 1 the rain brings a wind of its
        /// own, so it leans on a still day too rather than multiplying a wind that is not there.</summary>
        public float PrecipitationRainSlant { get; set; } = 1f;
        /// <summary>How steeply wind-blown petals and leaves come down. It scales how fast they
        /// sink while the wind carries them along, so higher is a steeper path and lower a
        /// flatter one. 1 is the shipped fall.</summary>
        public float PrecipitationWindSlant { get; set; } = 1f;
        /// <summary>The scene answering a lightning strike: shadows key toward the bolt for a
        /// blink, the mod's own darkening lifts with the game's flash, and a short warm afterglow
        /// follows. On by default, unlike the precipitation switch above: it adds nothing new to
        /// the picture, it makes light the player already saw behave like light.</summary>
        public bool LightningEffectsEnabled { get; set; } = true;
        /// <summary>A visible bolt with the flash, on any map. The game only draws one on the
        /// Farm and only when a rod or crop was actually hit; everywhere else a storm is a white
        /// screen and a sound. Uses the game's own bolt art, so it cannot look out of place.</summary>
        public bool LightningBoltsEnabled { get; set; } = true;

        // --- Wet world (ground that remembers the rain) ---
        /// <summary>Rain leaves the world wet: ground darkens and saturates while it rains and
        /// for a while after, puddles gather on dirt and stone, and at night lamps smear down
        /// the wet ground. Off by default; the Cinematic preset switches it on.</summary>
        public bool WetWorldEnabled { get; set; } = false;
        /// <summary>How wet everything reads at full soak. 0 = invisible, 1 = fresh downpour.</summary>
        public float WetWorldStrength { get; set; } = 0.45f;
        /// <summary>How much of the suitable ground pools. 0 = damp but no standing water.
        /// <para>Ships at zero: placement needs per-map art truth (which pixels are really open
        /// ground) that tile properties alone cannot give on modded maps - pools kept landing on
        /// fences and roofs in the first hour of testing. The dampness, the night streaks and
        /// the lens drops are the wet look; the pools return when they can be placed honestly.</para></summary>
        public float WetWorldPuddles { get; set; } = 0.0f;
        /// <summary>A few drops clinging to the edges of the screen while it rains (frost in a
        /// snowfall), never the middle. Gone shortly after the weather stops.</summary>
        public bool WetWorldLensDrops { get; set; } = true;
        /// <summary>How big the drops on the glass are, against their own size.</summary>
        public float WetWorldLensDropSize { get; set; } = 1.25f;
        /// <summary>The misted breath along the screen edge in rain, and the frost creeping in
        /// during a snowfall, against their own strength. Zero leaves the drops with clear glass
        /// around them.</summary>
        public float WetWorldEdgeHaze { get; set; } = 0.94f;

        /// <summary>Darken flat/unlit areas and pool light around real light sources.</summary>
        public bool LightingEnabled { get; set; } = true;
        /// <summary>How dark interiors get (vanilla leaves them flat-bright). 0 = none, 1 = very dark.</summary>
        public float LightingIndoorDarkness { get; set; } = 0.65f;
        /// <summary>Extra darkening at night where we own the lighting. 0 = none.</summary>
        public float LightingNightDarkness { get; set; } = 0.25f;
        /// <summary>How far an interior's COLOUR follows the hour, against the full walk this mod
        /// paints: cool from open sky before the sun is properly up, neutral in the middle of the
        /// day, warm in the hour before dark, blue again at night.
        ///
        /// <para>The brightness curve is not this dial and never moves with it - that is the two
        /// darkness sliders above. This is only how much the room is TINTED, and it exists because
        /// it had no dial at all until somebody woke up in a room they read as cold and blue and
        /// found nothing on any page that would take the blue out. What they reached for instead
        /// was the GI strength, which does move it, and which also lights the whole outdoors, so
        /// the room came right and the fields blew out.</para>
        ///
        /// <para>1 is the walk every release so far has painted and stays the default. 0 leaves an
        /// interior the colour the game drew it, still dimmed by the hour.</para></summary>
        public float LightingIndoorColourWalk { get; set; } = 1f;
        /// <summary>How much of the morning's cool cast a room keeps when the sky is CLEAR, as a
        /// fraction of the cast an overcast morning gets.
        ///
        /// <para>The cool cast argues that a room early on is lit by the sky rather than by the
        /// sun, and that the sky is blue. That holds on an overcast morning and not on a clear one:
        /// the sun is up by 6:00 in this game, it is low, and low sun is the warmest light of the
        /// day. Every release up to now painted the same cool morning whatever the weather was
        /// doing, and the player who reported it said so without meaning to, describing a room that
        /// read cold and blue on waking "except on rainy days" - the one morning of the two where
        /// the old cast was right.</para>
        ///
        /// <para>1 is that old behaviour, a clear morning as cool as a rainy one, and it is here so
        /// anyone who preferred it can have it back. 0 leaves a clear morning with no cool cast at
        /// all. Rain and storms are unaffected at every value: they always get the full cast, which
        /// is the one this dial does not touch.</para></summary>
        public float LightingMorningClearSkyCool { get; set; } = 0.35f;
        /// <summary>How much of the night darkening carries into the early morning (before 7:00).
        /// 0 wakes in a bright room; higher = darker mornings. The historical look used 0.25.</summary>
        public float LightingMorningDarkness { get; set; } = 0.25f;
        /// <summary>Warmth of the light pools (0 = neutral white, 1 = candle-orange).</summary>
        public float LightingWarmth { get; set; } = 0.33f;
        /// <summary>Scale the on-screen radius of every light pool.</summary>
        public float LightingRadiusScale { get; set; } = 0.9f;
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
        public float LightingBoost { get; set; } = 0.35f;
        /// <summary>Cast hard-edge shadows from tall/solid tiles that block light.</summary>
        public bool LightingShadows { get; set; } = true;
        /// <summary>How dark occluder shadows are. 0 = none, 1 = full.</summary>
        public float LightingShadowStrength { get; set; } = 0.62f;
        /// <summary>Fences, bushes and boulders block lamp light as their own silhouettes (a comb of
        /// light through the pickets) rather than as whole tiles. Off is the 1.6.2 look: rounder
        /// pools, blockier shadows. A taste, so it is a switch.
        ///
        /// <para>OFF by default from 1.7.3, along with <see cref="LightShadowProps"/>. Both shipped
        /// on in 1.7.0 on the strength of costing nothing measurable, which was true of the seven
        /// scenes they were measured in and is not a claim about anyone else's machine. The shape
        /// of the work is a march per light against every occluder near it, so its price is the
        /// number of lamps on screen times the number of things standing beside them - and every
        /// scene in that set was a town, a beach or a quiet farm. The first report from outside
        /// was a farm at 6:20 with several hundred crops, a lot of sprinklers and a dozen lit
        /// torches, holding 60 fps on 1.6.2 and about 40 now, with both of these on and everything
        /// else the same.</para>
        ///
        /// <para>That report is not proof these two are the cause, and it is not being treated as
        /// proof: it is the reason a default that has never been measured on a weak machine should
        /// not be the one every new install gets. Whoever wants the look turns it on and sees it
        /// immediately. Nobody has to work out why their farm got slower.</para></summary>
        public bool LightShadowSilhouettes { get; set; }
        /// <summary>Kegs, chests, machines, signs and floor furniture block lamp light as the sprites
        /// they are. Off, a lamp sees straight through them while the sun does not.
        ///
        /// <para>OFF by default from 1.7.3. Same reasoning as <see cref="LightShadowSilhouettes"/>
        /// above, and this is the heavier of the two on a working farm: a fence is a fence, but a
        /// sprinkler, a keg and a scarecrow are everywhere somebody has been playing a while.</para></summary>
        public bool LightShadowProps { get; set; }

        // --- Directional sprite shadows (sun-cast, sheared silhouettes) ---
        /// <summary>Which shape a cast shadow is. See <see cref="ShadowModel"/>: 1.7 stands a placed
        /// thing's shadow on its art and lays a four-legged creature's down; 1.6 hangs the first from
        /// its cell and stands the second up like a person.</summary>
        public ShadowModel DirectionalShadowModel { get; set; } = ShadowModel.Modern;
        /// <summary>Cast directional shadows from sprites (NPCs, later player/objects), by sun angle.</summary>
        public bool DirectionalShadowsEnabled { get; set; } = true;
        /// <summary>Opacity of the directional shadows. 0 = none, 1 = full.</summary>
        public float DirectionalShadowStrength { get; set; } = 0.7f;
        /// <summary>Length multiplier for the cast shadow (1 = default sun-driven length).</summary>
        public float DirectionalShadowLength { get; set; } = 1.0f;
        /// <summary>Extra stretch at the day's edges only (quartic in the sun offset).</summary>
        public float GoldenHourStrength { get; set; } = 0f;
        /// <summary>Edge softness of the shadow, in pixels (0 = crisp).</summary>
        public float DirectionalShadowBlur { get; set; } = 5.0f;
        /// <summary>Also cast directional shadows from trees and bushes (not just characters).</summary>
        public bool DirectionalShadowObjects { get; set; } = true;
        /// <summary>Give a building the shape of its own shadow instead of a pool under it.
        ///
        /// <para>A building is the tallest thing on a farm and its shadow is the largest single
        /// shape the sun draws there, so this is the one caster whose shadow is a feature of the
        /// scene rather than a detail of a sprite. It rides on the object switch above: with
        /// objects off, nothing casts.</para></summary>
        public bool DirectionalShadowBuildings { get; set; } = true;
        /// <summary>
        /// How much the ground is foreshortened on screen: a circle drawn on the ground is an
        /// oval this many times as tall as it is wide. 1 is a ground seen from straight above,
        /// where nothing lies down. The default is the game's own answer: the oval it draws under
        /// every character is 12 texels wide and 7 tall, read off <c>Game1.shadowTexture</c> in
        /// game, and 7/12 is 0.58.
        /// </summary>
        /// <remarks>
        /// It shapes the shadow of a SOLID thing and nothing else. The tip of a shadow is where
        /// the sun puts it whatever this is; this says how the width of a tree, a bush, a crop or
        /// a person lies on the ground once the sun has laid it across its own direction. With the
        /// shadow pointing up the screen the width is simply the width; pointing sideways, the
        /// width runs up and down the screen and is this much of itself. A fence or a sign has no
        /// width to lay down and is not touched. See <c>ShadowProjection</c>.
        /// </remarks>
        public float ShadowGroundForeshortening { get; set; } = 0.58f;

        /// <summary>
        /// The same oval, for people: the player, other players and every NPC. 1 lays a person
        /// down at their full width, which is how characters were drawn before the ground had a
        /// foreshortening at all.
        /// </summary>
        /// <remarks>
        /// The ground has one flatness, so in principle one number should do. A person is the
        /// exception the eye makes: a sixteen-texel figure laid down at the ground's 0.58 came out
        /// as a thread at dawn and read as thinner than the figure casting it, so people get their
        /// own. Farm animals are bulky and stay on <see cref="ShadowGroundForeshortening"/>.
        /// </remarks>
        public float ShadowCharacterGroundForeshortening { get; set; } = 1f;

        // --- How long and how soft each kind of caster's shadow is ---
        //
        // How far a shadow may reach, as a fraction of the caster's own height, before the sun
        // angle alone would take it further. DirectionalShadowLength multiplies all of them, so
        // one slider still moves everything and these say how the kinds sit relative to each other.
        //
        // These were constants until 1.6.1 and the numbers here are the ones that shipped, with
        // two exceptions noted on their own lines. A ceiling exists wherever a sprite's height is
        // not a real height: a tree's canopy, a bush's mass, a painted-on map prop. Things that
        // genuinely stand on the ground at their own height take the same sun a person does.

        /// <summary>Mature trees and fruit trees. Low because a canopy is drawn well above the
        /// trunk that actually casts, so the full sun would detach the shadow from its own tree.</summary>
        public float ShadowLengthTrees { get; set; } = 0.6f;
        /// <summary>Seeds, sprouts, saplings, bush-stage growth and stumps.
        ///
        /// <para>Shipped at 0.8 with the lean damped to 0.6 of the sun's angle. Un-damping the lean
        /// (1.5.4) widened the sideways reach of the same ceiling by about half, which is most of
        /// what "shadows are longer and sharper than 1.5.3, dense planting reads as diagonal
        /// clutter" was describing. 0.52 is the ceiling that puts the sideways reach back where
        /// 1.5.3 had it at a mid-morning sun.</para></summary>
        public float ShadowLengthSmallTrees { get; set; } = 0.52f;
        /// <summary>Bushes, both the terrain kind and the ones a map places. A bush is mostly
        /// mass rather than height, so its sprite over-states what is standing there.</summary>
        public float ShadowLengthBushes { get; set; } = 0.8f;
        /// <summary>Crops, living and dead. Was 0.55, raised to 1.0 in 1.5.4 so a tall dead plant's
        /// shadow would clear the plant instead of landing on it, at the same time as the lean
        /// stopped being damped. Both changes pushed the same way and the pair over-shot; 0.55 is
        /// the 1.5.3 ceiling, which with the un-damped lean still reaches further sideways than
        /// 1.5.3 ever did.</summary>
        public float ShadowLengthCrops { get; set; } = 0.55f;
        /// <summary>Grass tufts. Short: a tuft is a few pixels of blade over a wide footprint.</summary>
        public float ShadowLengthGrass { get; set; } = 0.35f;
        /// <summary>Forage, fences, signs, torches, kegs, machines - anything standing on its tile
        /// at its own height, which is why this one is not capped below a person's.</summary>
        public float ShadowLengthObjects { get; set; } = 1.0f;
        /// <summary>Barns, coops, sheds, the greenhouse and the farmhouse.
        ///
        /// <para>A building's shadow is not drawn among the sprites: it goes into a coverage mask
        /// and the effect chain multiplies the picture down through it, so NOTHING hides any of
        /// it. That is the whole difference from every other kind here, and it is why this number
        /// is low. A caster in the sort loses whatever part of its shadow falls behind something;
        /// a building loses nothing, so the same number reads several times longer.</para>
        ///
        /// <para>1.05 is where the author's own eye put it after walking the farm, replacing the
        /// 0.45 this shipped as while it was still a guess. The guess was low by more than half,
        /// and the reasoning above is why: a shadow nothing can hide reads longer than the same
        /// number does on a caster in the sort, so the number was pulled down twice for the same
        /// effect. 1.05 sits just above forage and machines at 1.00, which is the neighbour it was
        /// judged against, and a building is the taller thing. Still a look rather than a measured
        /// answer, which is why the dial is in the config, in GMCM and on the shadows page of
        /// F6.</para></summary>
        public float ShadowLengthBuildings { get; set; } = 1.05f;

        // Softness per kind, as a multiplier on DirectionalShadowBlur. A blur radius is in pixels,
        // so the same radius reads as a soft edge on a short shadow and a hard one on a long
        // shadow; these let the short things stay soft without blurring the tall ones into smudges.

        /// <summary>Softness of tree and fruit-tree shadows, times the overall blur.</summary>
        public float ShadowSoftnessTrees { get; set; } = 1.0f;
        /// <summary>Softness of sapling and stump shadows, times the overall blur.</summary>
        public float ShadowSoftnessSmallTrees { get; set; } = 1.6f;
        /// <summary>Softness of bush shadows, times the overall blur.</summary>
        public float ShadowSoftnessBushes { get; set; } = 1.0f;
        /// <summary>Softness of crop shadows, times the overall blur.</summary>
        public float ShadowSoftnessCrops { get; set; } = 1.6f;
        /// <summary>Softness of grass shadows, times the overall blur.</summary>
        public float ShadowSoftnessGrass { get; set; } = 1.0f;
        /// <summary>Softness of shadows from forage, fences and machines, times the overall blur.</summary>
        public float ShadowSoftnessObjects { get; set; } = 1.0f;
        /// <summary>Softness of a building's shadow, times the overall blur. Softer than the rest:
        /// the further a shadow's edge is from what casts it the less sharp it is, and a roof line
        /// is the furthest edge on the farm from the ground it lands on.</summary>
        public float ShadowSoftnessBuildings { get; set; } = 1.4f;

        // How far each kind LEANS, as a fraction of the sun's angle. 1 is the sun itself and is
        // the default for everything, because a shadow that leans less has moved the sun for its
        // caster alone: at six in the morning a damped tree pointed one way while the player
        // beside it pointed another, and that was reported as two suns, measured in clock hours.
        //
        // It is a setting because the same geometry is what made 1.5.3's crop shadows read as
        // planted rather than floating, and no length can substitute for it. Length decides how
        // far a shadow reaches; the lean decides its SHAPE. At 06:10 a crop capped at 0.55 lands
        // its tip 9.9 px sideways and 4.8 px down at full lean, and 6.8 by 8.6 at 0.6 - the same
        // ceiling, a completely different picture, and the second one is what was asked for.
        //
        // Below 1 a caster no longer agrees with the sun. That is the whole cost, and it is real.
        /// <summary>How far tree and fruit-tree shadows lean, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanTrees { get; set; } = 1.0f;
        /// <summary>How far sapling and stump shadows lean, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanSmallTrees { get; set; } = 1.0f;
        /// <summary>How far bush shadows lean, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanBushes { get; set; } = 1.0f;
        /// <summary>How far crop shadows lean, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanCrops { get; set; } = 1.0f;
        /// <summary>How far grass shadows lean, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanGrass { get; set; } = 1.0f;
        /// <summary>How far shadows from forage, fences and machines lean, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanObjects { get; set; } = 1.0f;
        /// <summary>How far a building's shadow leans, as a fraction of the sun's own angle.</summary>
        public float ShadowLeanBuildings { get; set; } = 1.0f;

        /// <summary>A shadow shorter than this is not a shadow, so nothing is allowed to reach zero
        /// by way of a length slider; turn the whole feature off instead.</summary>
        /// <summary>Zero, and zero means this kind does not cast at all: a shadow with no
        /// length is no shadow, and switching one kind off had no other home. The floor was
        /// 0.05, where a shadow is already a smudge a few pixels wide under the thing casting
        /// it, so the last step down is the smallest one on the dial.</summary>
        public const float ShadowKindLengthMin = 0f;
        /// <summary>Twice a person's own height, which is as far as a low sun takes anything.</summary>
        public const float ShadowKindLengthMax = 2f;
        /// <summary>Zero softness is a crisp edge, which is a look some people want.</summary>
        public const float ShadowKindSoftnessMin = 0f;
        /// <summary>Double the overall blur. Past this the silhouette stops reading as a shape.</summary>
        public const float ShadowKindSoftnessMax = 2f;
        /// <summary>A shadow that leans this much less than the sun still reads as the same sun.
        /// Lower and it is plainly its own; that is allowed, and it is what 1.5.3 did.</summary>
        public const float ShadowKindLeanMin = 0.2f;
        /// <summary>The sun's own angle. Nothing leans further than the light does.</summary>
        public const float ShadowKindLeanMax = 1f;
        /// <summary>A circle on the ground drawn a quarter as tall as it is wide: flatter than any
        /// view the art implies, and past it a sideways shadow is a line.</summary>
        public const float ShadowGroundForeshorteningMin = 0.25f;
        /// <summary>The ground seen from straight above, where nothing lies down.</summary>
        public const float ShadowGroundForeshorteningMax = 1f;
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

        /// <summary>
        /// Normalize every numeric field to its supported range. GMCM sliders only protect
        /// values entered through the UI — hand-edited config.json flows straight into the
        /// shaders (a lamp-shaft strength of 50 is fifty times the tuned beam, a white-out).
        /// Called after ReadConfig and on every GMCM save.
        /// </summary>
        public void Clamp()
        {
            static float ClampToRange(float v, float lo, float hi) => float.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);

            RenderScale = ClampToRange(RenderScale, 0.5f, 1f);
            RenderSharpness = ClampToRange(RenderSharpness, 0f, 2f);
            BloomThreshold = ClampToRange(BloomThreshold, 0f, 1f);
            BloomIntensity = ClampToRange(BloomIntensity, 0f, 2f);
            BloomEmissiveBoost = ClampToRange(BloomEmissiveBoost, 0f, 1f);
            ColorGradeStrength = ClampToRange(ColorGradeStrength, 0f, 1f);
            ColorGradeContrast = ClampToRange(ColorGradeContrast, 0.5f, 1.5f);
            ColorGradeSaturation = ClampToRange(ColorGradeSaturation, 0f, 2f);
            ColorGradeTemperature = ClampToRange(ColorGradeTemperature, -1f, 1f);
            ColorGradeLutAmount = ClampToRange(ColorGradeLutAmount, 0f, 1f);
            ColorGradeLut = ColorGradeLut ?? "";
            ColorGradeBrightness = ClampToRange(ColorGradeBrightness, 0.5f, 1.5f);
            GodRaysIntensity = ClampToRange(GodRaysIntensity, 0f, 2f);
            GodRaysSunIntensity = ClampToRange(GodRaysSunIntensity, 0f, 1.5f);
            GodRaysSunReach = ClampToRange(GodRaysSunReach, 0.1f, 1f);
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
            TiltShiftIndoorAmount = ClampToRange(TiltShiftIndoorAmount, 0f, 1f);
            WaterStrength = ClampToRange(WaterStrength, 0f, 2f);
            WaterSpeed = ClampToRange(WaterSpeed, 0f, 3f);
            WaterSparkle = ClampToRange(WaterSparkle, 0f, 1f);
            WaterSparkleDensity = ClampToRange(WaterSparkleDensity, 0.2f, 2f);
            WaterCausticsStrength = ClampToRange(WaterCausticsStrength, 0f, 1f);
            WaterReflectStrength = ClampToRange(WaterReflectStrength, 0f, 1f);
            WaterReflectBanding = ClampToRange(WaterReflectBanding, 0f, 16f);
            WaterReflectReach = ClampToRange(WaterReflectReach, 0.2f, 1f);
            WaterReflectDepth = ClampToRange(WaterReflectDepth, 0.1f, 1.5f);
            WaterReflectFadeRows = Math.Clamp(WaterReflectFadeRows, 4, 16);
            WaterReflectDistort = ClampToRange(WaterReflectDistort, 0f, 1.5f);
            WaterReflectBlur = ClampToRange(WaterReflectBlur, 0f, 2f);
            // The one build that had the new water as a fourth classic look: carry it over.
            if (WaterReflectStyle == WaterReflectionStyle.Realistic)
            {
                WaterReflectStyle = WaterReflectionStyle.Natural;
                WaterReflectModel = WaterReflectionModel.Modern;
            }
            WaterModernWobble = ClampToRange(WaterModernWobble, 0f, 2f);
            WaterModernChoppiness = ClampToRange(WaterModernChoppiness, 0f, 1f);
            WaterModernParallax = ClampToRange(WaterModernParallax, 0f, 0.3f);
            WaterModernFresnel = ClampToRange(WaterModernFresnel, 0f, 1f);
            WaterModernStretch = ClampToRange(WaterModernStretch, 1f, 1.4f);
            WaterModernEdgeSoftness = ClampToRange(WaterModernEdgeSoftness, 0f, 6f);
            WaterModernPlungeChurn = ClampToRange(WaterModernPlungeChurn, 0f, 1f);
            WaterModernPlungeReach = ClampToRange(WaterModernPlungeReach, 1f, 6f);
            WaterModernLipFade = ClampToRange(WaterModernLipFade, 0f, 1.5f);
            WaterRainRingDensity = ClampToRange(WaterRainRingDensity, 0f, 2f);
            WaterRainRingSize = ClampToRange(WaterRainRingSize, 0.4f, 2f);
            WaterRainRingStrength = ClampToRange(WaterRainRingStrength, 0f, 2f);
            WindowReflectionStrength = ClampToRange(WindowReflectionStrength, 0f, 2f);
            WindowReflectionNightStrength = ClampToRange(WindowReflectionNightStrength, 0f, 2f);
            WindowSheenStrength = ClampToRange(WindowSheenStrength, 0f, 2f);
            WindowGlareStrength = ClampToRange(WindowGlareStrength, 0f, 2f);
            WindowSceneReflectionStrength = ClampToRange(WindowSceneReflectionStrength, 0f, 2f);
            WindowLightGlowStrength = ClampToRange(WindowLightGlowStrength, 0f, 2f);
            WindowDaylightStrength = ClampToRange(WindowDaylightStrength, 0f, 2f);
            WindowDaylightStrengthElsewhere = ClampToRange(WindowDaylightStrengthElsewhere, 0f, 2f);
            ParticleDensity = ClampToRange(ParticleDensity, 0.25f, 2f);
            ParticleDustAmount = ClampToRange(ParticleDustAmount, 0f, 2f);
            ParticleDustSize = ClampToRange(ParticleDustSize, 0.5f, 2f);
            ParticleEmbersAmount = ClampToRange(ParticleEmbersAmount, 0f, 2f);
            ParticleEmbersSize = ClampToRange(ParticleEmbersSize, 0.5f, 2f);
            HeatHazeStrength = ClampToRange(HeatHazeStrength, 0f, 2f);
            ParticleWaterfallMistAmount = ClampToRange(ParticleWaterfallMistAmount, 0f, 2f);
            ParticleWaterfallMistSize = ClampToRange(ParticleWaterfallMistSize, 0.5f, 2f);
            ParticleHotSpringSteamAmount = ClampToRange(ParticleHotSpringSteamAmount, 0f, 2f);
            ParticleHotSpringSteamSize = ClampToRange(ParticleHotSpringSteamSize, 0.5f, 2f);
            ParticleLavaSparksAmount = ClampToRange(ParticleLavaSparksAmount, 0f, 2f);
            ParticleLavaSparksSize = ClampToRange(ParticleLavaSparksSize, 0.5f, 2f);
            ParticleFirefliesAmount = ClampToRange(ParticleFirefliesAmount, 0f, 2f);
            ParticleFirefliesSize = ClampToRange(ParticleFirefliesSize, 0.5f, 2f);
            ParticlePetalsAmount = ClampToRange(ParticlePetalsAmount, 0f, 2f);
            ParticlePetalsSize = ClampToRange(ParticlePetalsSize, 0.5f, 2f);
            ParticlePetalsFlutter = ClampToRange(ParticlePetalsFlutter, 0f, 1f);
            ParticleRingSparklesAmount = ClampToRange(ParticleRingSparklesAmount, 0f, 2f);
            ParticleRingSparklesSize = ClampToRange(ParticleRingSparklesSize, 0.5f, 2f);
            PrecipitationRainDensity = ClampToRange(PrecipitationRainDensity, 0.25f, 2f);
            PrecipitationSnowDensity = ClampToRange(PrecipitationSnowDensity, 0.25f, 2f);
            PrecipitationWindDensity = ClampToRange(PrecipitationWindDensity, 0.25f, 2f);
            PrecipitationRainSize = ClampToRange(PrecipitationRainSize, 0.5f, 2f);
            PrecipitationSnowSize = ClampToRange(PrecipitationSnowSize, 0.5f, 2f);
            PrecipitationWindSize = ClampToRange(PrecipitationWindSize, 0.5f, 2f);
            PrecipitationRainOpacity = ClampToRange(PrecipitationRainOpacity, 0.25f, 2f);
            PrecipitationSnowOpacity = ClampToRange(PrecipitationSnowOpacity, 0.25f, 2f);
            PrecipitationWindOpacity = ClampToRange(PrecipitationWindOpacity, 0.25f, 2f);
            PrecipitationStormDensity = ClampToRange(PrecipitationStormDensity, 1f, 3f);
            PrecipitationRainSlant = ClampToRange(PrecipitationRainSlant, 0f, 3f);
            PrecipitationWindSlant = ClampToRange(PrecipitationWindSlant, 0.25f, 3f);
            WetWorldStrength = ClampToRange(WetWorldStrength, 0f, 1f);
            WetWorldPuddles = ClampToRange(WetWorldPuddles, 0f, 1f);
            WetWorldLensDropSize = ClampToRange(WetWorldLensDropSize, 0.5f, 2f);
            WetWorldEdgeHaze = ClampToRange(WetWorldEdgeHaze, 0f, 2f);
            VignetteStrength = ClampToRange(VignetteStrength, 0f, 1f);
            ChromaticAberrationStrength = ClampToRange(ChromaticAberrationStrength, 0f, 1f);
            BlueLightFilter = ClampToRange(BlueLightFilter, 0f, 1f);
            FloodLightingStrength = ClampToRange(FloodLightingStrength, 0f, 1f);
            FloodShadowStrength = ClampToRange(FloodShadowStrength, 0f, 1f);
            FloodColourBleed = ClampToRange(FloodColourBleed, 0f, 1f);
            SpriteReliefStrength = ClampToRange(SpriteReliefStrength, 0f, 1f);
            SpriteReliefSun = ClampToRange(SpriteReliefSun, 0f, 1f);
            SpriteReliefRim = ClampToRange(SpriteReliefRim, 0f, 1f);
            SpriteReliefLeafShimmer = ClampToRange(SpriteReliefLeafShimmer, 0f, 1f);
            SheetUpscaleSmoothness = ClampToRange(SheetUpscaleSmoothness, 0f, 1f);
            SheetUpscaleSmoothnessWorld = ClampToRange(SheetUpscaleSmoothnessWorld, 0f, 1f);
            SheetUpscaleSmoothnessCharacters = ClampToRange(SheetUpscaleSmoothnessCharacters, 0f, 1f);
            SheetUpscaleSmoothnessPortraits = ClampToRange(SheetUpscaleSmoothnessPortraits, 0f, 1f);
            SheetUpscaleSmoothnessItems = ClampToRange(SheetUpscaleSmoothnessItems, 0f, 1f);
            SheetUpscaleSmoothnessInterface = ClampToRange(SheetUpscaleSmoothnessInterface, 0f, 1f);
            FoliageSwayStrength = ClampToRange(FoliageSwayStrength, 0f, 2f);
            FoliageSwaySpeed = ClampToRange(FoliageSwaySpeed, 0.25f, 2f);
            FoliageSwayGustSpan = ClampToRange(FoliageSwayGustSpan, 4f, 40f);
            LightShadowCarve = ClampToRange(LightShadowCarve, 0f, 1f);
            LightShadowSoftness = ClampToRange(LightShadowSoftness, 0f, 2f);
            LightShadowDetail = ClampToRange(LightShadowDetail, 0f, 1f);
            LightingIndoorDarkness = ClampToRange(LightingIndoorDarkness, 0f, 0.95f);
            LightingNightDarkness = ClampToRange(LightingNightDarkness, 0f, 0.95f);
            LightingIndoorColourWalk = ClampToRange(LightingIndoorColourWalk, 0f, 1f);
            LightingMorningClearSkyCool = ClampToRange(LightingMorningClearSkyCool, 0f, 1f);
            LightingWarmth = ClampToRange(LightingWarmth, 0f, 1f);
            LightingBoost = ClampToRange(LightingBoost, 0f, 2f);
            LightingRadiusScale = ClampToRange(LightingRadiusScale, 0.2f, 3f);
            LightingShadowStrength = ClampToRange(LightingShadowStrength, 0f, 1f);
            DirectionalShadowStrength = ClampToRange(DirectionalShadowStrength, 0f, 1f);
            DirectionalShadowLength = ClampToRange(DirectionalShadowLength, 0.2f, 2f);
            GoldenHourStrength = ClampToRange(GoldenHourStrength, 0f, 1f);
            AuroraStrength = ClampToRange(AuroraStrength, 0f, 2f);
            DirectionalShadowBlur = ClampToRange(DirectionalShadowBlur, 0f, 5f);
            ShadowGroundForeshortening = ClampToRange(ShadowGroundForeshortening, ShadowGroundForeshorteningMin, ShadowGroundForeshorteningMax);
            ShadowCharacterGroundForeshortening = ClampToRange(ShadowCharacterGroundForeshortening, ShadowGroundForeshorteningMin, ShadowGroundForeshorteningMax);
            ShadowLengthTrees = ClampToRange(ShadowLengthTrees, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowLengthSmallTrees = ClampToRange(ShadowLengthSmallTrees, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowLengthBushes = ClampToRange(ShadowLengthBushes, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowLengthCrops = ClampToRange(ShadowLengthCrops, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowLengthGrass = ClampToRange(ShadowLengthGrass, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowLengthObjects = ClampToRange(ShadowLengthObjects, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowLengthBuildings = ClampToRange(ShadowLengthBuildings, ShadowKindLengthMin, ShadowKindLengthMax);
            ShadowSoftnessTrees = ClampToRange(ShadowSoftnessTrees, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowSoftnessSmallTrees = ClampToRange(ShadowSoftnessSmallTrees, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowSoftnessBushes = ClampToRange(ShadowSoftnessBushes, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowSoftnessCrops = ClampToRange(ShadowSoftnessCrops, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowSoftnessGrass = ClampToRange(ShadowSoftnessGrass, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowSoftnessObjects = ClampToRange(ShadowSoftnessObjects, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowSoftnessBuildings = ClampToRange(ShadowSoftnessBuildings, ShadowKindSoftnessMin, ShadowKindSoftnessMax);
            ShadowLeanTrees = ClampToRange(ShadowLeanTrees, ShadowKindLeanMin, ShadowKindLeanMax);
            ShadowLeanSmallTrees = ClampToRange(ShadowLeanSmallTrees, ShadowKindLeanMin, ShadowKindLeanMax);
            ShadowLeanBushes = ClampToRange(ShadowLeanBushes, ShadowKindLeanMin, ShadowKindLeanMax);
            ShadowLeanCrops = ClampToRange(ShadowLeanCrops, ShadowKindLeanMin, ShadowKindLeanMax);
            ShadowLeanGrass = ClampToRange(ShadowLeanGrass, ShadowKindLeanMin, ShadowKindLeanMax);
            ShadowLeanObjects = ClampToRange(ShadowLeanObjects, ShadowKindLeanMin, ShadowKindLeanMax);
            ShadowLeanBuildings = ClampToRange(ShadowLeanBuildings, ShadowKindLeanMin, ShadowKindLeanMax);
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
        /// <summary>Ask what drew the pixel under the mouse (the console command radiance_drawsat,
        /// without the console). Unbound by default: it exists for the day something on screen
        /// looks wrong, and a key that does nothing the rest of the time should not be taken from
        /// the player. The answer goes to the SMAPI console and the log.</summary>
        public KeybindList InspectDrawKey { get; set; } = new();

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

        /// <summary>Whether the live config is still exactly what this profile holds, so the
        /// tuner can show which saved look is the one in effect. Every value the profile
        /// recorded must match; a profile from 1.0.0 that recorded nothing matches nothing.</summary>
        public bool MatchesProfile(NamedProfile p)
        {
            if (p.Values is not { Count: > 0 })
                return false;
            foreach (PropertyInfo prop in TunableProps())
            {
                if (!p.Values.TryGetValue(prop.Name, out string? raw) || string.IsNullOrEmpty(raw))
                    continue;
                string live = Convert.ToString(prop.GetValue(this), CultureInfo.InvariantCulture) ?? "";
                if (!string.Equals(live, raw, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
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
            // Remembered so the tuner can show which one was picked, the way ActivePreset does
            // for the looks. It says which was picked last, not that nothing moved since.
            ActivePerfPreset = preset;
            switch (preset)
            {
                case PerfPreset.Quality:
                    RenderScale = 1f;
                    // Quality means what it says: nothing here lowers itself behind your back.
                    RenderScaleAuto = false;
                    WaterReflectReach = 1f;
                    WaterReflectFadeRows = 4;
                    TiltShiftEnabled = true;
                    ChromaticAberrationEnabled = true;
                    FloodLightingEnabled = true;
                    WaterReflection = true;
                    DirectionalShadowObjects = true;
                    ShadowCastsPerCharacter = 3;
                    // The one thing the default lowers on everybody's behalf, so the preset whose
                    // promise is that nothing lowers itself is where it goes back up.
                    LightShadowSharpEdges = true;
                    break;

                case PerfPreset.Balanced:
                    LightShadowSharpEdges = false;
                    // No preset lowers the render scale any more. Measured 2026-09-06 at 720p:
                    // 0.75 saved 0.05 ms of the chain's 0.38 and 0.5 saved 0.10, while the round
                    // trip cost every sprite drawn at a scale the resample does not divide, which
                    // is what six "the world went blurry" reports were. The slider is still there
                    // for anyone who wants the trade; a preset makes it on nobody's behalf.
                    RenderScale = 1f;
                    RenderScaleAuto = false;
                    // Every reflection still there, just fading in eight-row steps instead of
                    // four: measured at 31-37% off the reflection pass for nothing lost but the
                    // smoothness of a gradient, which is the best cost-to-look trade in the mod.
                    WaterReflectReach = 1f;
                    WaterReflectFadeRows = 8;
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
                    LightShadowSharpEdges = false;
                    // Full size here too (see Balanced): what this preset saves, it saves by
                    // switching work off, not by drawing the world through a smaller buffer.
                    RenderScale = 1f;
                    RenderScaleAuto = false;
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
                    // Reflections stay: they are the reason to run this mod, and they are now a
                    // dial rather than a switch. Half reach drops the scenery standing well back
                    // from the water and keeps what is at its edge, which is what anyone is
                    // actually looking at; people and animals are never cut by reach at all.
                    WaterReflection = true;
                    WaterReflectReach = 0.5f;
                    WaterReflectFadeRows = 8;
                    break;

                case PerfPreset.LowSpec:
                    LightShadowSharpEdges = false;
                    RenderScale = 1f;
                    RenderScaleAuto = false;
                    TiltShiftEnabled = false;
                    ChromaticAberrationEnabled = false;
                    FloodLightingEnabled = false;
                    LightingEnabled = true;
                    DirectionalShadowObjects = false;
                    ShadowCastsPerCharacter = 1;
                    // The three Performance keeps and this one cannot. Each is a per-light or
                    // per-pixel march rather than a single pass, so each is priced by how much of
                    // the screen it covers, which is the wrong shape of cost for a weak machine.
                    GodRaysEnabled = false;
                    GodRaysSun = false;
                    // REFLECTIONS STAY ON HERE TOO, at the shortest reach and the coarser fade.
                    //
                    // They used to be switched off, and switching them off is what prompted this
                    // whole line of work: it is the reason most people install this mod, and the
                    // preset most likely to be chosen by somebody having trouble was the one that
                    // threw it away. There was no middle setting to reach for because the only
                    // control was a switch.
                    //
                    // There is one now, and this is what it costs. At the shortest reach only the
                    // scenery standing AT the water still mirrors, and people and animals are
                    // never cut by reach at all - so what is left is what anyone looks at. It is
                    // not free: this preset now pays somewhere around 0.15 ms of submission it
                    // used to pay nothing for. That is the trade, and it is a judgement rather
                    // than a measurement, so it is written down as one.
                    WaterReflection = true;
                    WaterReflectReach = 0.2f;
                    WaterReflectFadeRows = 8;
                    // What is left is a full-screen pass each, and between them they are most of
                    // what makes the picture look different from vanilla: bright things glow, the
                    // palette has a time of day, and water moves.
                    BloomEnabled = true;
                    ColorGradeEnabled = true;
                    WaterEnabled = true;
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
                    // The wet ground used to be switched on here, on the grounds that it is the
                    // cinematic look's signature. It has since been taken out of both menus
                    // until its puddles can be placed from the map rather than guessed at, and a
                    // preset that quietly switches on something a player cannot then find the
                    // switch for is worse than one that leaves it alone.
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
