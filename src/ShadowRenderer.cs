using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace SDVRadiance
{
    /// <summary>
    /// Directional sprite shadows. Draws a leaning, flattened, dark copy
    /// of each caster's sprite (authentic silhouette, not a blob), pinned at the feet
    /// and leaning away from the sun.
    ///
    /// Drawn INTO the game's own <c>World_Sorted</c> batch (SpriteSortMode.FrontToBack)
    /// at a layerDepth just under the caster, so the shadow sits correctly BEHIND the
    /// sprite and is depth-sorted against trees/objects (over ground, under sprites).
    /// Because we draw into the game's open batch we can't use a shear transform, so the
    /// lean is a rotation about the feet plus a vertical squash — sortable per-sprite.
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>Optional diagnostics sink; when set (config.DebugLogging), the first few draws + any error are logged once.</summary>
        internal static IMonitor? Diag;

        private int _diagFrames;
        private bool _errLogged;

        // The player's silhouette is rendered to this offscreen target during RenderingWorld,
        // then drawn back (flattened + leaned) into the World_Sorted batch. FarmerRenderer only
        // supports a uniform scale, so the RT is the only way to squash the player vertically.
        private RenderTarget2D? _playerRT;
        private SpriteBatch? _rtBatch;
        private Texture2D? _gradTex;
        /// <summary>Soft radial disc for indoor/ambient CONTACT shadows (a grounding pool under a caster).</summary>
        private Texture2D? _blobTex;
        /// <summary>Feet→tip fade that reaches EXACTLY zero, for map-tile props: their art often has
        /// a long straight top edge (fence rails), and any residual tip alpha reads as a hard line.</summary>
        private Texture2D? _propGradTex;
        private Vector2 _playerFeetInRT;
        private bool _playerReady;
        private bool _playerMaskFresh;   // the RT holds the current pose (reuse gate); _playerReady
                                         // additionally means "cast a shadow" and drops while swimming
        internal const int PlayerRtW = 96;
        internal const int PlayerRtH = 176;

        /// <summary>The player's baked silhouette RT for THIS frame (null when not baked) —
        /// the water shader uses it to exclude exactly the player's own pixels (not a box)
        /// from ring-tile water effects.</summary>
        internal static Texture2D? PlayerMask;
        /// <summary>The player's FULL-COLOUR bake (same pose/geometry as <see cref="PlayerMask"/>,
        /// no colour scrub, no head fade) — the water reflection RT flips this below the feet.
        /// Whatever appearance mods drew is what gets reflected.</summary>
        internal static Texture2D? PlayerColor;
        private RenderTarget2D? _playerColorRT;
        /// <summary>Opacity at the far tip (head end) relative to the feet, for the gradient fade.</summary>
        private const float HeadFade = 0.05f;

        // NPCs and animals are baked to pooled offscreen targets too (same as the player), so
        // their shadow is one cohesive silhouette with a smooth feet→head fade — no stepped
        // horizontal bands. A fixed slot size fits any character/animal sprite at 4× scale;
        // sprites bigger than a slot fall back to the banded path.
        private const int CasterRtW = 160;
        private const int CasterRtH = 224;
        private readonly System.Collections.Generic.List<RenderTarget2D> _casterPool = new();
        private int _casterUsed;
        // PERSISTENT cache — keyed by (texture, source rect), i.e. the sprite FRAME, so every
        // NPC/animal sharing a frame shares one bake and warm frames cost a dictionary hit
        // instead of a render-target switch. Upright silhouettes carry no sun angle, so entries
        // stay valid indefinitely; the cache is only capped (see PreparePlayer).
        private readonly System.Collections.Generic.Dictionary<(Texture2D tex, Rectangle src), (RenderTarget2D rt, Vector2 feetInRT)> _casterBakes = new();

        // Objects (trees/bushes/clumps/furniture/craftables/crops/…) bake to pooled RTs with a
        // continuous gradient too — same smooth path as characters, no stepped bands. Slots are large
        // (objects are big); the enumeration runs once as a BAKE pass (RenderingWorld) then again as
        // a COMPOSITE pass (World_Sorted). Keyed by SPRITE (texture+src+flip), not instance, so a
        // field of 100 identical crops or 20 same-season oaks costs ONE bake — that dedup is what
        // makes baking everything (crops included) affordable. Sprites bigger than a slot fall back
        // to the banded path. Slots are wide because the silhouette is baked pre-SHEARED (lean
        // baked in): a wide sprite ROTATED about its feet dips one bottom corner under the ground
        // line (the "bush shadow droops down-left" artifact); a shear keeps the whole bottom edge
        // glued to the ground, so baked objects composite with NO rotation at all.
        private const int ObjRtW = 400;
        private const int ObjRtH = 456;
        private readonly System.Collections.Generic.List<RenderTarget2D> _objPool = new();
        private int _objUsed;
        private readonly System.Collections.Generic.Dictionary<(Texture2D tex, Rectangle src, SpriteEffects fx), (RenderTarget2D rt, Vector2 feetInRT)> _bakedObjMap = new();
        private bool _objBaking;
        private GraphicsDevice? _objGd;
        /// <summary>Sun angle the object cache was baked at (shear is baked in) — cache clears when it changes.</summary>
        private long _objShearKey = long.MinValue;
        private GameLocation? _objBakeLoc;
        /// <summary>Last location the over-cap bake warning was logged for: once per location, not per frame.</summary>
        private GameLocation? _objCapLoggedLoc;
        /// <summary>Pose the player RT was last baked with — identical pose skips the re-bake.</summary>
        private (int frame, int facing, Rectangle src) _playerBakeSig = (-1, -1, default);

        // Multiply only the destination ALPHA by the source alpha (RGB untouched): dst.a *= src.a.
        // Used to bake the feet→head opacity gradient onto the silhouette.
        private static readonly BlendState MultiplyAlpha = new()
        {
            ColorWriteChannels = ColorWriteChannels.Alpha,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.SourceAlpha,
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.One,
        };

        // Zero every RGB channel, leave alpha as-is: dst.rgb = 0. A silhouette is shape+opacity
        // only — this scrubs any colour that slipped into the bake (Fashion Sense draws its
        // clothing layers through its own patches and ignores the black tint we pass, so a
        // white dress otherwise became a white "shadow").
        private static readonly BlendState ZeroColor = new()
        {
            ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.Zero,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One,
        };

        /// <summary>Master gate: shadows enabled and a location is loaded. Cutscenes/festivals
        /// cast too — their actors come from CurrentEvent.actors (see CharactersIn).</summary>
        internal static bool ShouldCast(ModConfig config)
        {
            if (!config.Enabled || !config.DirectionalShadowsEnabled)
                return false;
            // IsWorldReady (not the old !eventUp): cutscenes cast, but the half-initialized
            // frames during save load / return-to-title never enter the shadow paths.
            return StardewModdingAPI.Context.IsWorldReady && Game1.currentLocation != null;
        }

        // Re-entrancy latch: if a patched draw call ever re-enters our render entry points
        // (an appearance mod calling back into the game's draw while we're baking), bail and
        // log ONCE instead of recursing until the stack dies.
        private static int _renderDepth;

        /// <summary>
        /// Every character the game is DRAWING right now — the only ones an effect may key off.
        ///
        /// <para>
        /// Normally that is the location's residents. During a cutscene it is not: the event
        /// supplies its own cast in <c>CurrentEvent.actors</c> and the residents standing around
        /// the map are not drawn at all. Reading both lists put shadows (and reflections, and
        /// water-ripple exclusions) under people who were nowhere on screen — reported on Nexus
        /// as "npc shadows everywhere that's not supposed to be included in the event cutscene".
        /// </para>
        ///
        /// <para>
        /// The test below is <c>GameLocation.drawCharacters</c>'s own, not a guess at it. Two
        /// details are worth knowing because both are easy to get wrong from intuition:
        /// FESTIVALS are not an exception (the townspeople at a festival are event actors, not
        /// residents — <c>showWorldCharacters</c> is false there and only two vanilla events set
        /// it), and an event can hide part of its OWN cast, which <c>Event.draw</c> honours with
        /// <c>ShouldHideCharacter</c>.
        /// </para>
        ///
        /// Shared with the reflection and water-mask passes, which had the same two-list bug for
        /// the same reason.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<NPC> CharactersIn(GameLocation loc)
        {
            Event? ev = Game1.CurrentEvent;
            bool residentsDrawn = !loc.shouldHideCharacters()
                && !(Game1.eventUp && (ev == null || !ev.showWorldCharacters));
            if (residentsDrawn)
                foreach (NPC npc in loc.characters)
                    yield return npc;
            if (ev?.actors != null)
                foreach (NPC npc in ev.actors)
                    if (!ev.ShouldHideCharacter(npc))
                        yield return npc;
        }

        /// <summary>All farm animals in a location — including Marnie's paddock cows, which live in
        /// Forest.marniesLivestock rather than location.animals (they had no shadow otherwise).</summary>
        private static System.Collections.Generic.IEnumerable<FarmAnimal> AnimalsIn(GameLocation loc)
        {
            foreach (FarmAnimal a in loc.animals.Values)
                yield return a;
            if (loc is StardewValley.Locations.Forest forest)
                foreach (FarmAnimal a in forest.marniesLivestock)
                    yield return a;
        }

        /// <summary>Seasonal dark time, safe fallback.</summary>
        private static int TrulyDark()
        {
            try { return Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { return 2000; }
        }

        /// <summary>
        /// Moonlight 0..1 for tonight: SDV's 28-day month maps to one synthetic lunar cycle
        /// (day 1/28 = new moon, day 14-15 = full), seasons scale clarity (winter's cold
        /// clear sky is brightest), and any precipitation means overcast → no moon.
        /// </summary>
        internal static float MoonStrength()
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !loc.IsOutdoors || Game1.isRaining || Game1.isSnowing || Game1.isLightning)
                return 0f;
            float phase = 1f - Math.Abs(Game1.dayOfMonth - 14.5f) / 13.5f;
            float season = Game1.season switch
            {
                Season.Winter => 1.15f,
                Season.Fall => 1.0f,
                Season.Spring => 0.9f,
                _ => 0.85f,
            };
            return MathHelper.Clamp(phase * season, 0f, 1f);
        }

        /// <summary>Sun conditions: outdoors, clear weather → one long celestial shadow. The
        /// dusk cutoff follows the game's own seasonal dark time (summer sun sets late). After
        /// true dark the sun path ENDS and night falls to the per-light path instead — town
        /// lamps/torches then cast their own crisp shadows (a faint moon-directional shadow was
        /// invisible on the dark ground AND suppressed the lamp shadows, so nights looked
        /// shadowless). Moonlight still lifts the ambient/water via MoonStrength elsewhere.</summary>
        private static bool SunCasts()
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !loc.IsOutdoors || Game1.isRaining || Game1.isSnowing)
                return false;
            int t = Game1.timeOfDay;
            return t >= 600 && t < TrulyDark();   // day/dusk = sun cast; after dark → per-light path
        }

        /// <summary>True when the outdoor sun shadow is active.</summary>
        internal static bool SunShadowActive(ModConfig config) => ShouldCast(config) && SunCasts();

        /// <summary>
        /// True when our shadows are actually being drawn this frame (sun outdoors, or at least
        /// one light indoors/at night) — drives suppression of the vanilla blob shadow so it
        /// isn't drawn on top of our directional ones.
        /// </summary>
        internal static bool ShadowsActiveNow(ModConfig config)
        {
            // Both draw paths always produce a shadow now: the sun path outdoors, and the light
            // path everywhere else (an ambient contact pool even in a lightless room). So whenever
            // we're allowed to cast, suppress the vanilla blob to avoid a doubled shadow.
            return ShouldCast(config);
        }

        /// <summary>Draw all caster shadows into the game's open World_Sorted batch.</summary>
        public void DrawInto(SpriteBatch b, ModConfig config)
        {
            if (!ShouldCast(config))
                return;
            if (_renderDepth > 0)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log("[shadow] DrawInto re-entered — skipping nested call", LogLevel.Warn); }
                return;
            }

            GameLocation loc = Game1.currentLocation;
            float strength = MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            float blur = Math.Max(0f, config.DirectionalShadowBlur);
            if (strength <= 0.01f)
                return;

            _renderDepth++;
            try
            {
                if (SunCasts())
                    DrawSunShadows(b, loc, config, strength, blur);
                else
                    DrawLightShadows(b, loc, config, strength, blur);   // indoors / night → per light source
            }
            catch (Exception ex)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] draw threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                _renderDepth--;
            }
        }
    }
}
