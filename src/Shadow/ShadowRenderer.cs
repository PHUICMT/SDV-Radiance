using System;
using System.Collections.Generic;
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
        internal static IMonitor? DiagnosticMonitor;

        /// <summary>The mod's own monitor, set once at startup and never gated by a setting.
        /// <see cref="DiagnosticMonitor"/> only exists while diagnostic logging is turned on, so
        /// anything routed exclusively through it is invisible by default. That is right for the
        /// per-frame chatter and wrong for a failure that silently disables a feature: a co-op
        /// partner's shadow went missing with nothing in the log, on a machine that had logging
        /// off, and there was no way to tell that from the feature never having run.</summary>
        internal static IMonitor? SharedMonitor;

        /// <summary>True while the benchmark is re-running this pass to measure it. Anything that
        /// advances once per frame must not advance once per call while this is set.</summary>
        internal static bool BenchmarkAmplifying;

        /// <summary>Last compose's "any water on screen" answer, published by the pipeline. Gates
        /// the player COLOUR bake, whose only reader is the water reflection.</summary>
        internal static bool WaterOnScreen;

        /// <summary>True when a mod that animates the player's appearance independently of the
        /// body frame is installed (Fashion Sense hair sway and the like). Only then is the
        /// periodic re-bake of an unchanged pose worth paying for.</summary>
        internal static bool PlayerAccessoriesAnimate;

        private int _diagnosticFrameCount;
        private bool _errorLogged;

        // The player's silhouette is rendered to this offscreen target during RenderingWorld,
        // then drawn back (flattened + leaned) into the World_Sorted batch. FarmerRenderer only
        // supports a uniform scale, so the RT is the only way to squash the player vertically.
        private RenderTarget2D? _playerRenderTarget;
        /// <summary>The player's silhouette LAID DOWN by the sun: the upright bake drawn through
        /// the sun's projection about the feet, soft edge stamped in, so the daylight shadow is
        /// one unrotated stamp carrying the skew a rotate-and-scale draw cannot (see
        /// LayDownPlayerSun). The upright bake stays as it is for the water and the lamps.</summary>
        private RenderTarget2D? _playerSunRenderTarget;
        private Vector2 _playerSunFeet;
        private float _playerSunUnbake = 1f;
        private Rectangle _playerSunContent;
        private ShadowProjection _playerSunProjection;
        private float _playerSunBlur = -1f;
        private bool _playerSunFresh;
        private (int frame, int facing, Rectangle sourceRect) _playerSunSignature = (-1, -1, default);
        private float _playerSunContactHardness = -1f, _playerSunPenumbraStretch = -1f;
        /// <summary>The square every laid-down farmer silhouette is made in. A person is 64 by
        /// 128 upright and at the longest the dials allow lies down to several times that; the
        /// fit ladder in LayDownSilhouette scales down to make the extremes fit.</summary>
        internal const int PlayerSunRtSize = 256;
        private RenderTarget2D? _playerSunBlurScratch;
        /// <summary>This frame's sun for the character passes, captured during the bakes so a
        /// lay-down made then and a draw made later agree (see CaptureCharacterSun).</summary>
        private bool _characterSunLive;
        private float _characterSunRotation, _characterSunStretch, _characterSunBlur;
        private SpriteBatch? _renderTargetSpriteBatch;
        private Texture2D? _gradientTexture;
        /// <summary>Soft radial disc for indoor/ambient CONTACT shadows (a grounding pool under a caster).</summary>
        private Texture2D? _contactBlobTexture;
        /// <summary>Feet→tip fade that reaches EXACTLY zero, for map-tile props: their art often has
        /// a long straight top edge (fence rails), and any residual tip alpha reads as a hard line.</summary>
        private Texture2D? _propGradientTexture;
        private Vector2 _playerFeetInRenderTarget;
        private bool _playerReady;
        private bool _playerColorFresh;  // the COLOUR twin holds the current pose (it is skipped without water)
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
        private RenderTarget2D? _playerColorRenderTarget;

        /// <summary>
        /// The colour every sky-cast shadow is drawn in this frame; the bake's own alpha
        /// premultiplies it at draw time. Black is what every release before 1.7.7 drew and what
        /// the dial gives at 0. A real shadow is not black: it is the ground lit by whatever the
        /// sun is not, which outdoors is the sky (blue), under rain cloud is the cloud (grey), at
        /// the day's edges is the opposite of the sun's gold (violet), and in a room is the bounce
        /// off wood and lamps (warm). The dial scales that fill. The bakes never change, so nothing
        /// is baked again when the hour turns; only the colour they are drawn in does.
        /// </summary>
        internal static Color ShadowInk { get; private set; } = Color.Black;
        /// <summary>The same ink as a vector, for the cloud and building shadow shader.</summary>
        internal static Vector3 ShadowInkVector { get; private set; }
        private static readonly Dictionary<int, (Vector3 ink, int tick)> _inkPerScreen = new();

        /// <summary>
        /// Settle this frame's ink for the screen about to draw. Eased over a couple of seconds
        /// so a doorway or a cloud bank does not switch every shadow's colour on one frame, and
        /// pinned to its target while a capture is frozen so a dump is repeatable. Per screen,
        /// because two screens can stand in two locations with two fills.
        /// </summary>
        internal static void UpdateShadowInk(ModConfig config, int screenId)
        {
            float dial = MathHelper.Clamp(config.ShadowTint, 0f, 1f);
            Vector3 target = dial <= 0f ? Vector3.Zero : ShadowInkTarget() * dial;
            if (!_inkPerScreen.TryGetValue(screenId, out (Vector3 ink, int tick) state) || Determinism.Frozen)
                state = (target, Game1.ticks);
            else if (Game1.ticks != state.tick)
                state = (state.ink + (target - state.ink) * 0.04f, Game1.ticks);
            _inkPerScreen[screenId] = state;
            ShadowInkVector = state.ink;
            ShadowInk = new Color(state.ink);
        }

        /// <summary>What fills a shadow here and now, at full dial. See <see cref="ShadowInk"/>.</summary>
        private static Vector3 ShadowInkTarget()
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null || !location.IsOutdoors)
                return new Vector3(0.22f, 0.14f, 0.09f);
            bool raining = location.IsRainingHere();
            Vector3 fill = location.IsSnowingHere() || LocalSky.Season == Season.Winter
                ? new Vector3(0.18f, 0.25f, 0.45f)
                : new Vector3(0.16f, 0.22f, 0.40f);
            if (raining)
                fill = new Vector3(0.16f, 0.16f, 0.19f);
            float minutes = GameClock.MinutesNow();
            // The same low-sun edge ComputeSun stretches the shadows by: a golden sun leaves its
            // opposite in the shade. Rain has no gold to oppose.
            float sunSkyOffset = SunSkyOffsetAt(minutes);
            float lowSunEdge = sunSkyOffset * sunSkyOffset * sunSkyOffset * sunSkyOffset;
            if (!raining)
                fill = Vector3.Lerp(fill, new Vector3(0.30f, 0.15f, 0.36f), lowSunEdge);
            int trulyDark = TrulyDark();
            float trulyDarkMinutes = (trulyDark / 100) * 60 + trulyDark % 100;
            float night = MathHelper.Clamp((minutes - (trulyDarkMinutes - 90f)) / 90f, 0f, 1f);
            return Vector3.Lerp(fill, new Vector3(0.05f, 0.07f, 0.18f), night);
        }

        /// <summary>One report line: the dial, what is being drawn, and where it is heading.</summary>
        internal static string DescribeInk(ModConfig config)
        {
            Color ink = ShadowInk;
            Vector3 target = ShadowInkTarget() * MathHelper.Clamp(config.ShadowTint, 0f, 1f);
            return $"shadow ink: ShadowTint {config.ShadowTint:0.00} drawing rgb({ink.R},{ink.G},{ink.B}) "
                + $"toward rgb({(int)(target.X * 255f)},{(int)(target.Y * 255f)},{(int)(target.Z * 255f)})";
        }
        /// <summary>Opacity at the far tip (head end) relative to the feet, for the gradient fade.</summary>
        private const float HeadFade = 0.05f;

        // NPCs and animals are baked to pooled offscreen targets too (same as the player), so
        // their shadow is one cohesive silhouette with a smooth feet→head fade — no stepped
        // horizontal bands. A fixed slot size fits any character/animal sprite at 4× scale;
        // sprites bigger than a slot fall back to the banded path.
        private const int CasterRtW = 160;
        private const int CasterRtH = 224;
        /// <summary>Every caster slot ever allocated. Nothing reads it back except the over-cap
        /// diagnostic (it is the honest VRAM number); leases come from the free list.</summary>
        private readonly System.Collections.Generic.List<RenderTarget2D> _casterRenderTargetPool = new();
        /// <summary>Slots an evicted entry handed back, waiting to be leased again.</summary>
        private readonly System.Collections.Generic.List<RenderTarget2D> _casterFreeTargets = new();
        // PERSISTENT cache — keyed by (texture, source rect), i.e. the sprite FRAME, so every
        // NPC/animal sharing a frame shares one bake and warm frames cost a dictionary hit
        // instead of a render-target switch. Upright silhouettes carry no sun angle, so entries
        // stay valid indefinitely; the cache is only capped (see PreparePlayer).
        private readonly System.Collections.Generic.Dictionary<(Texture2D texture, Rectangle sourceRect), SpriteBake> _casterBakeCache = new();

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
        private const int ObjectRtW = 400;
        private const int ObjectRtH = TallestColumnPx + 8 + 8;   // column + feet margin + max blur (5, rounded up)

        /// <summary>
        /// Slot sizes, smallest first. One size fits nobody: a crop is 64 pixels of shadow in a
        /// 400x456 slot, which is three percent of it, and the other ninety-seven were being both
        /// allocated and (until the content rect landed) rasterized. A farm at a wide zoom held
        /// 134 slots against a cap of 128 - over cap, evicting - for 211 MB of graphics memory
        /// whose real content would fit in a fraction of that.
        ///
        /// <para>Bucketing by size instead of forcing every sprite into the largest one is what
        /// makes both numbers better at once: MORE sprites fit (a tripled effective cap) for LESS
        /// memory, because the many small ones stop paying tree prices. The classes are coarse on
        /// purpose - three pools reuse well, a pool per exact size would fragment.</para>
        /// </summary>
        private static readonly (int W, int H, int Cap)[] ObjectSlotClasses =
        {
            (128, 160, 320),     // crops, grass, forage, small craftables  -  0.08 MB each
            (256, 288, 96),      // bushes, furniture, medium props         -  0.29 MB each
            (ObjectRtW, ObjectRtH, 48) // trees, buildings, tall tile columns     -  0.74 MB each
        };

        /// <summary>
        /// The tallest silhouette the largest class must take, and why it is that number.
        ///
        /// <para>
        /// A stacked map-tile column runs up to seven tiles (448 px). The old single slot was 456
        /// tall and the fit test compared the bare column height against 456 minus the 8 px feet
        /// margin, so a seven-tile column fit by exactly zero pixels. Adding the blur bleed to the
        /// fit test - correctly, since the blur really does push outward - then refused those
        /// columns, and they fell back to banded shadows: seven of them per frame on a farm,
        /// caught by the miss counter, which is the only reason it was noticed at all.
        /// </para>
        ///
        /// <para>So the largest class is sized from what has to fit rather than the other way
        /// round: 448 of column, 8 of feet margin, and the widest blur the slider allows.</para>
        /// </summary>
        private const int TallestColumnPx = 448;
        /// <summary>One slot-sized scratch per size class for the bake-time blur, which stamps the
        /// silhouette aside and back again (see SpriteBake.BakedBlur). The radius itself travels with
        /// the request rather than living here, so two kinds of caster can be softened differently
        /// in the same frame.</summary>
        private readonly RenderTarget2D?[] _objectBlurScratches = new RenderTarget2D?[3];
        /// <summary>The same scratch for the character slots, which are one size of their own.</summary>
        private RenderTarget2D? _casterBlurScratch;
        /// <summary>Every slot ever allocated, and the idle ones ready to lease again, PER SIZE
        /// CLASS. A free small slot cannot serve a tree, so one shared free list would hand back
        /// a target the caller cannot use.</summary>
        private readonly System.Collections.Generic.List<RenderTarget2D>[] _objectRenderTargetPools =
            { new(), new(), new() };
        private readonly System.Collections.Generic.List<RenderTarget2D>[] _objectFreeTargetsByClass =
            { new(), new(), new() };
        private readonly System.Collections.Generic.Dictionary<(Texture2D texture, Rectangle sourceRect, SpriteEffects effect), SpriteBake> _bakedObjectCache = new();
        /// <summary>Sprites the DRAW pass wanted and found unbaked, to bake next frame. This is
        /// what lets the bake pass skip its full enumeration on a warm frame: instead of walking
        /// every on-screen tile a second time to discover nothing is missing, it bakes exactly
        /// what the draw pass reported missing, which on a still screen is nothing at all.
        /// Value carries the bake inputs recorded at draw time (the shear is per-CALLER, damped
        /// by sprite type, so it cannot be recomputed globally).</summary>
        private readonly System.Collections.Generic.Dictionary<(Texture2D texture, Rectangle sourceRect, SpriteEffects effect), ObjectBakeRequest> _objectBakeQueue = new();

        /// <summary>
        /// What a caster IS, as far as its shadow is concerned: a flat card standing on its bottom
        /// edge, or a solid standing on its footprint. The two cast different shapes from the same
        /// sprite and the sun angle alone cannot tell them apart.
        /// </summary>
        /// <remarks>
        /// A fence, a sign, a gate and a painted map wall are cards: the art is the object's one face
        /// and its shadow is that face laid along the ground, every column still rooted where it
        /// stands. A person, an animal, a tree, a bush, a crop and a machine are solids: the sun sees
        /// them from the side, so what lands on the ground is the silhouette laid along the sun's
        /// direction, its width running ACROSS that direction. Decided by the game's own class for
        /// each thing, never by sprite or name, so anything a mod adds through those classes is
        /// covered the same way.
        /// </remarks>
        internal enum ShadowGeometry { Solid, Card }

        /// <summary>
        /// The affine that lays a silhouette on the ground, about its feet: where one source pixel
        /// of WIDTH lands (across) and where one source pixel of HEIGHT lands (along), both in
        /// screen pixels per source pixel.
        /// </summary>
        /// <remarks>
        /// <para>Along is the sun: a column of height h lands h times this away from the feet, and
        /// it is the same for both geometries, so the tip of every shadow agrees with every other
        /// about where the sun is.</para>
        /// <para>Across is where the geometries part. A card keeps its width level on the screen,
        /// which is the shear this mod always baked. A solid's width lies on the ground at right
        /// angles to the sun's direction, and the ground is seen at a slant, so its across vector
        /// is the ground's perpendicular put back on screen: level when the shadow points straight
        /// up the screen, nearly vertical and foreshortened when it points sideways. With no
        /// foreshortening at all a solid's projection is exactly a rotation, which is what
        /// characters were always drawn with; the foreshortening is what makes a sideways shadow
        /// lie down instead of standing on its edge.</para>
        /// </remarks>
        internal readonly struct ShadowProjection
        {
            public readonly float AcrossX, AcrossY, AlongX, AlongY;

            public ShadowProjection(float acrossX, float acrossY, float alongX, float alongY)
            {
                AcrossX = acrossX;
                AcrossY = acrossY;
                AlongX = alongX;
                AlongY = alongY;
            }

            /// <summary>The screen offset of one source pixel of height: up the screen at noon,
            /// swung by the lean, as long as the stretch.</summary>
            private static Vector2 Along(float rotation, float stretch)
                => new((float)Math.Sin(rotation) * stretch, -(float)Math.Cos(rotation) * stretch);

            public static ShadowProjection ForCard(float rotation, float stretch)
            {
                Vector2 along = Along(rotation, stretch);
                return new ShadowProjection(1f, 0f, along.X, along.Y);
            }

            public static ShadowProjection ForSolid(float rotation, float stretch, float groundForeshortening)
            {
                float k = Math.Max(0.05f, groundForeshortening);
                Vector2 along = Along(rotation, stretch);
                // The ground's perpendicular to the sun's direction, measured ON the ground (screen
                // y un-squashed by k to get there), then put back on screen (squashed again).
                //
                float groundX = (float)Math.Cos(rotation) / k, groundY = (float)Math.Sin(rotation);
                float length = (float)Math.Sqrt(groundX * groundX + groundY * groundY);
                float acrossX = groundX / length, acrossY = groundY * k / length;
                // Past a quarter turn the cosine changes sign and the width swings round to the
                // other side, so an asymmetric caster's shadow comes out MIRRORED whenever the sun
                // is on the far side of the screen. Faithful for a real solid, whose far side
                // really would be facing us by then, and wrong for what we have, which is a card
                // cut from a front view: there is no back of a barrel in the sprite to project, so
                // projecting one shows a side nobody drew.
                //
                // The WHOLE vector turns round, never just its x. Turning only the x leaves the
                // width pointing the same way as the length at a hundred and twenty degrees, and a
                // parallelogram whose two sides point the same way has no area: the shadow flattens
                // to a line and disappears. Turning both keeps the area identical to what it always
                // was at every angle, and only takes the mirror off.
                if (acrossX < 0f)
                {
                    acrossX = -acrossX;
                    acrossY = -acrossY;
                }
                return new ShadowProjection(acrossX, acrossY, along.X, along.Y);
            }

            /// <summary>Where a source offset from the feet lands. <paramref name="dy"/> is the
            /// sprite's own y, so it is negative above the feet, which is why along is subtracted.</summary>
            public Vector2 Apply(float dx, float dy)
                => new(dx * AcrossX - dy * AlongX, dx * AcrossY - dy * AlongY);

            /// <summary>The same map as a SpriteBatch transform about a pivot, the feet.</summary>
            public Matrix About(Vector2 pivot)
                => Matrix.CreateTranslation(-pivot.X, -pivot.Y, 0f)
                 * new Matrix(AcrossX, AcrossY, 0f, 0f, -AlongX, -AlongY, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f)
                 * Matrix.CreateTranslation(pivot.X, pivot.Y, 0f);

            /// <summary>The rectangle a sprite covers once laid down, relative to its feet: the four
            /// corners mapped and bounded. The origin is where the feet sit inside the sprite.</summary>
            public void Bounds(float width, float height, float originX, float originY,
                out float left, out float right, out float top, out float bottom)
            {
                Vector2 a = Apply(-originX, -originY), b = Apply(width - originX, -originY);
                Vector2 c = Apply(-originX, height - originY), d = Apply(width - originX, height - originY);
                left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
                right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
                top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
                bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            }

            /// <summary>How far, in source pixels, the farthest point of a sprite this size moves
            /// between this projection and another: the re-bake test.</summary>
            public float Drift(ShadowProjection other, float width, float height)
            {
                float across = Math.Abs(AcrossX - other.AcrossX) + Math.Abs(AcrossY - other.AcrossY);
                float along = Math.Abs(AlongX - other.AlongX) + Math.Abs(AlongY - other.AlongY);
                return across * width * 0.5f + along * height;
            }

            /// <summary>This projection of a sprite that is itself turned about its feet by
            /// <paramref name="radians"/> first, in SpriteBatch's sense: what a tree's shadow does
            /// while the tree is being shaken. Composed into the two vectors rather than added to
            /// the sun's lean, because a lean turns the whole shadow while a turned caster only
            /// moves its far end, by the sine of the turn across and the cosine along.</summary>
            public ShadowProjection Rotated(float radians)
            {
                if (radians == 0f)
                    return this;
                float cos = (float)Math.Cos(radians), sin = (float)Math.Sin(radians);
                return new ShadowProjection(
                    cos * AcrossX - sin * AlongX, cos * AcrossY - sin * AlongY,
                    sin * AcrossX + cos * AlongX, sin * AcrossY + cos * AlongY);
            }

            public bool Same(ShadowProjection other)
                => AcrossX == other.AcrossX && AcrossY == other.AcrossY && AlongX == other.AlongX && AlongY == other.AlongY;

            /// <summary>The sideways scale of the rotate-and-scale draw a SpriteBatch can do directly
            /// that comes closest to this projection: the along vector exactly, and the across
            /// vector's part at right angles to it. What is dropped is the skew between the two,
            /// which is NOT small: at the tip of a person's shadow it measures twenty-one pixels
            /// at the author's own foreshortening and a sixty-degree sun. Only the lamp path draws
            /// this way now, because one upright bake has to serve every lamp in a room, each
            /// leaning it its own way; the sun's cast of a person is laid down through the
            /// projection itself, skew and all (see DrawNpcShadow and LayDownPlayerSun).</summary>
            /// <remarks>The sign is REAL and has to be spent, not dropped: past a quarter turn the
            /// width wants to point the other way round the shadow's own axis. A SpriteBatch
            /// cannot take a negative scale (it winds the quad backwards and the game's batch
            /// throws it away), so a caller hands the sign to
            /// <see cref="LaidDownWidth"/> and gets a flip back.</remarks>
            public float AcrossScaleForRotation()
            {
                float alongLength = (float)Math.Sqrt(AlongX * AlongX + AlongY * AlongY);
                if (alongLength < 1e-4f)
                    return 1f;
                // The unit at right angles to along, on the side the sprite's width points.
                float nx = -AlongY / alongLength, ny = AlongX / alongLength;
                return AcrossX * nx + AcrossY * ny;
            }
        }

        /// <summary>What the draw pass needs baked, recorded at draw time because the projection is
        /// per-caller (damped by sprite type, card or solid by class) and cannot be recomputed
        /// globally.</summary>
        private sealed class ObjectBakeRequest
        {
            public Vector2 BaseOrigin;
            /// <summary>How a sprite is laid down, across and along.</summary>
            public ShadowProjection Projection;
            /// <summary>The soft edge this caster's kind asked for, in screen pixels. Carried
            /// with the request because a bake queued this frame may not run until a later one,
            /// by which time a single shared field would be describing some other kind.</summary>
            public float Blur;
            /// <summary>This piece's share of the whole caster's height, from the feet up, for a
            /// caster cast in more than one piece. Carried for the same reason the blur is: a
            /// queued bake must lay down the same slice of the fade the draw pass asked for, or a
            /// tree's trunk comes back at a twentieth of its strength where the canopy starts at
            /// full. 1 for anything cast whole.</summary>
            public float FadeShareFromFeet = 1f;
            /// <summary>Set only for a stacked MAP-TILE column, which is several tiles drawn one
            /// above another and so has no single source rect. Copied out of the scan's scratch
            /// arrays at request time, so the bake can be replayed a frame later without redoing
            /// the scan that found them.</summary>
            public Rectangle[]? ColumnSources;
            public int[]? ColumnLevels;
            /// <summary>How the map turns each of those sources; without it a queued re-bake
            /// would replay the column unturned and the shadow shape would not match the art.</summary>
            public byte[]? ColumnOrients;
        }
        private bool _isBakingObjects;
        private GraphicsDevice? _objectGraphicsDevice;
        private GameLocation? _objectBakeLocation;

        /// <summary>For radiance_screenwatch: how many whole-map object bake walks ran since the
        /// last watched call, and what started the latest. The walk is meant to run once on arriving
        /// in a location; a split-screen report measured it at 27 ms a frame on average, so something
        /// is starting it again and again, and the reason is the thing to read.</summary>
        // Counted per screen: one static counter was read by whichever screen logged next, so the
        // farmhand arriving on the mountain was printed as the host walking in Town.
        private static readonly int[] _arrivalWalksByScreen = new int[4];
        private static readonly string[] _arrivalReasonByScreen = { "-", "-", "-", "-" };

        internal static int ArrivalWalksFor(int screenId)
            => screenId >= 0 && screenId < _arrivalWalksByScreen.Length ? _arrivalWalksByScreen[screenId] : 0;

        internal static string ArrivalReasonFor(int screenId)
            => screenId >= 0 && screenId < _arrivalReasonByScreen.Length ? _arrivalReasonByScreen[screenId] : "-";

        internal static void ForgetArrivalWalks(int screenId)
        {
            if (screenId >= 0 && screenId < _arrivalWalksByScreen.Length)
                _arrivalWalksByScreen[screenId] = 0;
        }
        private string? _arrivalWalkReason;
        /// <summary>Last location the over-cap bake warning was logged for: once per location, not per frame.</summary>
        private GameLocation? _objectCapLoggedLocation;

        /// <summary>
        /// One cached silhouette: the pooled target holding its pixels, where the caster's feet sit
        /// inside that target, the frame it was last drawn on, and (objects only) the sun lean that
        /// is baked into those pixels.
        ///
        /// <para>The tick is what makes eviction possible at all. Both caches used to answer "too
        /// many entries" with <c>Clear()</c>, which on a map that simply HAS more distinct sprites
        /// than the cap re-baked the whole screen every single frame — the cache turned into a cost
        /// rather than a saving, precisely on the heavily modded installs it was there to protect.
        /// Knowing when each entry was last wanted turns that into dropping the coldest few.</para>
        /// </summary>
        private sealed class SpriteBake
        {
            public RenderTarget2D Rt = null!;
            public Vector2 FeetInRt;
            public int LastUsedTick;
            /// <summary>The projection a sprite's pixels were laid down with, so the sun moving off
            /// it, or the geometry changing under it, re-bakes.</summary>
            public ShadowProjection BakedProjection;
            /// <summary>Edge softness baked into the pixels, in pixels of the player's blur
            /// setting. Object shadows used to buy their soft edge per FRAME - five translucent
            /// copies of every silhouette, every frame, which on a mature farm at noon was ~2,900
            /// draws and 0.7 ms of pure fill. Paying the same taps ONCE, here, when the sprite
            /// bakes, leaves the per-frame cost at one draw and the picture the same. Tracked per
            /// entry so a moved blur slider re-bakes gradually through the existing stale queue
            /// instead of all at once.</summary>
            public float BakedBlur = -1f;
            /// <summary>The contact-hardness the soft edge in these pixels was shaped by. Tracked
            /// beside the radius for exactly the same reason: the dial changes how the blur varies
            /// ALONG the shadow without changing the radius at all, so a cache that only watches
            /// the radius keeps handing back the old shape and the dial looks dead.</summary>
            public float BakedContactHardness = -1f;
            /// <summary>The penumbra-shape dial these pixels were stamped with, tracked beside
            /// the hardness and for the same reason.</summary>
            public float BakedPenumbraStretch = -1f;
            /// <summary>The part of the slot that holds shadow (see ContentBounds). Drawing with
            /// this as the source rect instead of the whole slot is what stops the card blending
            /// hundreds of thousands of transparent pixels per shadow. Empty means an entry from
            /// before this field existed: draw the full slot and let the blur-mismatch check
            /// re-bake it into shape.</summary>
            public Rectangle Content;
            /// <summary>Which slot-size pool this target came from, so eviction hands it back to
            /// the right free list. A small slot returned to the large pool is a target nobody can
            /// use for what the pool promises.</summary>
            public int SlotClass;
            /// <summary>Screen pixels of silhouette stored in one slot texel. Four is one texel per
            /// screen pixel and what everything that fits a slot gets; a sprite too big for the
            /// largest slot at four bakes at two or one instead, and the draw scales the slot back
            /// up by 4/this. Defaulted rather than required so the character caches, which are
            /// always 4×, need not say so.</summary>
            public float BakedScale = 4f;
        }

        /// <summary>Distinct object silhouettes kept alive. Slots are 400×456 (~0.73 MB each), so
        /// this is the VRAM ceiling: 128 slots is about 93 MB.</summary>
        /// <summary>Sprites all three pools can hold together, for the report and the over-cap
        /// warning. The real limits are per class (see ObjectSlotClasses); this is their sum.</summary>
        private static int ObjectBakeCapTotal
        {
            get { int n = 0; foreach (var c in ObjectSlotClasses) n += c.Cap * LiveScreens; return n; }
        }

        /// <summary>How many screens are drawing. The bake caps are sized for what one viewport
        /// can see; a split screen has two viewports, and with the caps left at one screen's
        /// worth the two took turns evicting each other's sprites: 8.7 evictions and 8.9 bakes a
        /// frame on a farm, four milliseconds of the shadow row, measured. Each class keeps one
        /// screen's cap per live screen.</summary>
        private static int LiveScreens => Math.Max(1, GameRunner.instance?.gameInstances?.Count ?? 1);

        private static int ObjectClassCap(int slotClass) => ObjectSlotClasses[slotClass].Cap * LiveScreens;
        /// <summary>Distinct character/animal frames kept alive. Slots are 160×224 (~0.14 MB).</summary>
        private const int CasterBakeCap = 192;
        /// <summary>An eviction pass goes this far under the cap, so it is not re-triggered on the
        /// very next frame by a single new sprite scrolling in.</summary>
        private const float EvictHeadroom = 0.85f;
        /// <summary>A sprite drawn within this many ticks is on screen NOW; evicting it buys a
        /// banded stand-in and an immediate re-bake, which is the thrash being replaced. Only a
        /// cache that has run away to twice its cap stops respecting this.</summary>
        private const int HotBakeTicks = 8;
        private readonly System.Collections.Generic.List<(Texture2D texture, Rectangle sourceRect)> _casterEvictScratch = new();
        private readonly System.Collections.Generic.List<(Texture2D texture, Rectangle sourceRect, SpriteEffects effect)> _objectEvictScratch = new();
        /// <summary>Pose the player RT was last baked with — identical pose skips the re-bake.</summary>
        private (int frame, int facing, Rectangle sourceRect) _playerBakeSignature = (-1, -1, default);
        /// <summary>Whose silhouette the live player bake holds, so another screen can borrow it
        /// rather than bake the same person again (ShadowRenderer.Farmers).</summary>
        private long _playerBakeFarmerId;

        // Multiply only the destination ALPHA by the source alpha (RGB untouched): dst.a *= src.a.
        // Used to bake the feet→head opacity gradient onto the silhouette.
        /// <summary>
        /// Straight sum, with no premultiply on the way in.
        ///
        /// <para>
        /// The bake-time blur needs the MEAN of nine shifted copies, which is nine draws at a
        /// ninth of the weight each, added together. BlendState.Additive cannot express that: it
        /// multiplies the source by its own alpha before adding, so a tap tinted to one ninth
        /// contributed alpha squared over eighty-one, and nine of those come to a ninth of the
        /// silhouette instead of all of it. Every object shadow in the game came out at eleven
        /// per cent opacity - which reads, on screen, as no shadow at all.
        /// </para>
        ///
        /// <para>With One/One the tint is the only weight, and nine ninths is one.</para>
        /// </summary>
        private static readonly BlendState SumTaps = new()
        {
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
        };

        // Multiply EVERY channel of the bake by the source's alpha: dst = dst * src.a. The bake is
        // a premultiplied white shape by the time this runs (see Whiten), so fading its opacity
        // has to fade its colour by the same amount or the two come apart and the shape draws
        // brighter than its own alpha allows. On the black bakes of every release before 1.7.7
        // the colour channels were zero, and zero times anything is the same zero.
        private static readonly BlendState MultiplyAlpha = new()
        {
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.SourceAlpha,
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.SourceAlpha,
        };

        // Colour := alpha, drawn with a solid white source: dst.rgb = dst.a. A silhouette is a
        // shape and an opacity and nothing else, so whatever was drawn into the bake, the black
        // tint the game honours or the colours an appearance mod paints through its own patches
        // (Fashion Sense ignored the tint, and a white dress cast a white shadow), only its shape
        // survives. It survives WHITE and premultiplied rather than black, because a white shape
        // takes whatever colour it is drawn in and a black one can only ever darken: this is what
        // lets the ink change with the hour without a single bake being made again.
        private static readonly BlendState Whiten = new()
        {
            ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
            ColorSourceBlend = Blend.DestinationAlpha,
            ColorDestinationBlend = Blend.Zero,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One,
        };

        private static Texture2D? _solidWhiteTexture;

        /// <summary>
        /// Turn what was just drawn into the bound bake into a shape and nothing else: every
        /// texel's colour becomes its own alpha (see <see cref="Whiten"/>). Runs once per bake,
        /// between the sprite draw and the feet-to-head fade, so the fade scales a shape that is
        /// already white.
        /// </summary>
        private void WhitenBake(GraphicsDevice graphicsDevice, Rectangle bounds)
        {
            if (_solidWhiteTexture == null || _solidWhiteTexture.IsDisposed)
            {
                _solidWhiteTexture = new Texture2D(graphicsDevice, 1, 1);
                _solidWhiteTexture.SetData(new[] { Color.White });
            }
            _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, Whiten, SamplerState.PointClamp);
            _renderTargetSpriteBatch.Draw(_solidWhiteTexture, bounds, Color.White);
            _renderTargetSpriteBatch.End();
        }

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

        /// <summary>1 = the sun path owns the frame, 0 = the per-light path does, in between = both
        /// are drawing at their share while dusk (or a doorway) crosses over. Starts at the sun so
        /// a daylight load does not fade in from nothing.</summary>
        private float _sunBlend = 1f;

        /// <summary>~1 s to cross over at 60 fps. Long enough to read as the light changing rather
        /// than as the shadows being replaced.</summary>
        private const float SunBlendRate = 0.02f;

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
        internal static System.Collections.Generic.IEnumerable<NPC> CharactersIn(GameLocation location)
        {
            Event? ev = Game1.CurrentEvent;
            bool residentsDrawn = !location.shouldHideCharacters()
                && !(Game1.eventUp && (ev == null || !ev.showWorldCharacters));
            if (residentsDrawn)
                foreach (NPC npc in location.characters)
                    yield return npc;
            if (ev?.actors != null)
                foreach (NPC npc in ev.actors)
                    if (!ev.ShouldHideCharacter(npc))
                        yield return npc;
            // A RIDDEN horse is not in location.characters at all: Horse's mount code removes it
            // from the list and the rider draws it instead (Farmer.draw calls mount.draw). So this
            // list, which is every caster the pass walks, could not see it, and the rider is
            // skipped on the grounds that the horse's shadow covers them. Riding therefore took
            // every shadow off the screen, horse and rider both. Added here rather than at each
            // call site so the sun pass, the per-light pass, the bake and the report all agree.
            foreach (Farmer who in location.farmers)
                if (who?.mount != null)
                    yield return who.mount;
        }

        /// <summary>
        /// Whether this character's <c>HideShadow</c> flag should stop us casting.
        ///
        /// <para>
        /// Usually yes: the game sets it on characters that must not have one at all, such as an
        /// NPC laying down at the end of a route (<c>NPC.cs</c>).
        /// </para>
        ///
        /// <para>
        /// But the flag is also set for reasons that are about the VANILLA shadow specifically, a
        /// round blob that cannot follow a sprite drawn away from its own tile. Those reasons do
        /// not carry over to a silhouette cut from the sprite and anchored at the DRAWN position,
        /// and honouring them left the Squid Fest fishermen as the only people on the beach with no
        /// shadow while everyone beside them had one. <c>Beach.adjustDerbyFisherman</c> is the
        /// clearest case: it sets <c>drawOffset = (0, 96)</c>, <c>shouldShadowBeOffset</c> and
        /// <c>HideShadow</c> together, which is the game saying "the blob would land in the wrong
        /// place", not "this thing casts no shadow".
        /// </para>
        ///
        /// Each exception below names the game code that sets the flag, so the list can be checked
        /// against the game rather than argued about.
        /// </summary>
        private static bool ShadowHiddenFor(NPC npc)
        {
            // Horse.cs sets HideShadow in its constructor and then draws no shadow of its own, so a
            // horse has none in vanilla at all. That is a decision about the BLOB: the sprite is two
            // tiles wide and stands well away from its own tile, which one round patch cannot follow.
            // A silhouette cut from the sprite can, and it has to, because the rider is skipped on
            // the grounds that "the horse's shadow covers them" — which was only ever true if the
            // horse had one. Riding therefore removed every shadow from the player, reported as the
            // shadow disappearing the moment you mount.
            if (!npc.HideShadow || npc is Pet || npc is Horse)
                return false;
            // A creature from another mod that hides the vanilla blob only to paint a blob of its
            // own (Custom Companions, for every companion) wants a shadow: it gets ours, and the
            // shim its draw runs through swallows the one it paints. Known by the draw itself, not
            // by the class name, so a companion configured with no shadow keeps none.
            if (ShadowSuppression.SelfShadowedCharacterTypes.Contains(npc.GetType()))
                return false;
            // NPC.cs, end-of-route behaviour: a standing silhouette over a sleeping sprite is worse
            // than no shadow, so this is the one HideShadow that really means none.
            if (npc.layingDown)
                return true;
            // Beach.adjustDerbyFisherman and friends: decorative standing NPCs drawn with an offset.
            if (npc.SimpleNonVillagerNPC)
                return false;
            // Event.AddTemporaryActor: flag set from sprite width alone (>= 32).
            if (npc.EventActor && (npc.Sprite?.SpriteWidth ?? 0) >= 32)
                return false;
            // Every reason above is a piece of the GAME's own code, and the flag means what that
            // code meant by it. A class that ships in another mod's assembly cannot be read the
            // same way: there the flag is set to stop the game painting its round blob, because
            // the mod means to paint its own. Custom Companions sets it on every companion and
            // then paints a blob only for the ones its content pack marked EnableShadow, which
            // defaults to false — so the crabs and ducks of a wildlife pack hid the game's shadow,
            // painted none of their own, and were the only creatures in the location with nothing
            // under them. Reported twice by different players.
            //
            // SelfShadowedCharacterTypes above cannot answer this on its own: it is filled by
            // WATCHING a class paint a blob, so a class that never paints one never lands in it,
            // and which companion happens to walk on screen first decides the answer for a whole
            // session.
            if (npc.GetType().Assembly != typeof(NPC).Assembly)
                return false;
            return true;
        }

        /// <summary>All farm animals in a location — including Marnie's paddock cows, which live in
        /// Forest.marniesLivestock rather than location.animals (they had no shadow otherwise).</summary>
        private static System.Collections.Generic.IEnumerable<FarmAnimal> AnimalsIn(GameLocation location)
        {
            foreach (FarmAnimal a in location.animals.Values)
                yield return a;
            if (location is StardewValley.Locations.Forest forest)
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
            GameLocation? location = Game1.currentLocation;
            if (location == null || !location.IsOutdoors
                || location.IsRainingHere() || location.IsSnowingHere() || location.IsLightningHere())
                return 0f;
            float phase = 1f - Math.Abs(Game1.dayOfMonth - 14.5f) / 13.5f;
            float season = LocalSky.Season switch
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
            GameLocation? location = Game1.currentLocation;
            if (location == null || !location.IsOutdoors)
                return false;
            int t = Game1.timeOfDay;
            return t >= 600 && t < TrulyDark();   // day/dusk = sun cast; after dark → per-light path
        }

        /// <summary>
        /// How overcast it is: 1 in rain, snow or a storm, 0 under a clear sky.
        ///
        /// <para>
        /// Weather used to switch the sun path OFF, and that did more than remove the long shadows.
        /// The same test drives whether the game's own blob shadows are suppressed, so a rainy day
        /// handed every tree, bush and critter back to vanilla: the whole screen visibly reverted
        /// to the shadows the mod exists to replace, which is how it was reported.
        /// </para>
        ///
        /// <para>
        /// An overcast sky does not remove shadows, it makes them soft, short and faint, and that
        /// is a DIMMER on the sun path rather than a switch. Everything keeps its own silhouette,
        /// the vanilla blobs stay suppressed, and nothing on screen changes kind when it starts
        /// raining.
        /// </para>
        /// </summary>
        private static float OvercastNow()
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return 0f;
            if (location.IsRainingHere() || location.IsLightningHere())
                return 1f;
            return location.IsSnowingHere() ? SnowOvercast : 0f;
        }

        /// <summary>How much of the full dimmer a SNOWFALL is worth.
        /// <para>A snowy sky is not a rain cloud. It is bright, and the ground under it is a
        /// reflector, so a shadow in falling snow is soft and short but nowhere near as faint.
        /// At the full dimmer, a shadow on pale stone came out below what an eye can find, and
        /// the report was that the sun shadow disappears when it snows. Half the dimmer keeps
        /// the softening and gives the shadow back.</para></summary>
        private const float SnowOvercast = 0.5f;

        /// <summary>What a shadow keeps of its strength, length and edge under a full overcast.
        /// Faint and short and soft: the light is coming from the whole sky, not from a point.</summary>
        private const float OvercastAlpha = 0.42f;
        private const float OvercastLength = 0.5f;
        private const float OvercastExtraBlur = 1.6f;
        /// <summary>Eased, because weather can turn mid-day and a shadow may not pop.</summary>
        private float _overcastBlend;

        /// <summary>
        /// Where the sun is in the sky right now, for anything that needs to point AT it rather
        /// than away from it.
        ///
        /// <para>
        /// <paramref name="lean"/> is the same angle the shadows lean by, and a shadow leans away
        /// from its light, so the sun is on the opposite side of the screen from wherever a shadow
        /// is pointing. <paramref name="height"/> is 0 on the horizon and 1 overhead.
        /// </para>
        ///
        /// <para>
        /// False when there is no direct sun to point at: indoors, before six, after true dark, or
        /// under an overcast sky. The shadow pass keeps casting under overcast, softly, because a
        /// bright sky still throws one; a SHAFT of light is a different thing and needs the sun
        /// itself.
        /// </para>
        /// </summary>
        /// <param name="glassRoofCounts">Whether a glass-roofed interior (see <see cref="UnderAGlassRoof"/>)
        /// counts as under the sun. Only the dappled sunlight asks for that; the cast shadows keep
        /// their indoor path there.</param>
        /// <summary>An interior the sun shines straight into. The game files the greenhouse as an
        /// interior (no sky, no weather drawn), but its roof is glass: the sun that dapples the farm
        /// stands over it too, and so does the overcast that takes the dapple away.
        ///
        /// <para>
        /// The NAME is what is asked, not <c>IsGreenhouse</c>. That flag reads as architecture and
        /// is not: every one of the game's own uses of it is about farming, never about light. It
        /// makes seeds ignore the season, keeps crops alive through a season change, stops hoed
        /// dirt decaying, allows trees to be planted and holds their art at spring. And it is set
        /// by a single map property, with no glass anywhere in the test, so a content pack that
        /// wants year-round crops in a cave or a cellar sets it there and means nothing at all
        /// about the roof. Reading it as a glass roof put daylight in those rooms; reported by
        /// Elacro on Nexus, 2026-09-09.
        /// </para>
        ///
        /// <para>
        /// So a place is under glass when it says so in its name. The game's own greenhouse is
        /// named "Greenhouse" exactly, and a content pack's greenhouse carries the word, which is
        /// a claim about the building rather than about what grows in it. The cost of asking this
        /// way is a glass building named something else entirely, which keeps the indoor light it
        /// had before 1.7.6.
        /// </para>
        /// </summary>
        internal static bool UnderAGlassRoof(GameLocation? location)
            => location != null && !location.IsOutdoors
               && location.Name != null
               && location.Name.IndexOf("Greenhouse", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Where the sun stands for everything that is LIGHT rather than shadow: the direction its
        /// light travels across the screen, and how high it is.
        ///
        /// <para>
        /// This used to hand back a bare number that its callers used as a SLOPE, pairing it with a
        /// hard-coded 1 to make a direction. The number is an angle in radians, and an angle is not
        /// a slope: at the day's edges the two part company by nearly seventeen degrees, so the
        /// light came in at a shallower angle than the sun it was supposed to be describing. A
        /// direction is returned instead, and it is built the way the shadows build theirs.
        /// </para>
        ///
        /// <para>
        /// It also never saw the sun's bearing, so turning the sun moved the shadows and left every
        /// beam of light standing where it was. It has its own bearing now, on the same scale as
        /// the shadows': EQUAL NUMBERS MEAN ONE SUN. They ship a half-turn apart because that is
        /// where both halves of this mod have stood since they were written, the light coming from
        /// above the picture and the shadows running away from a sun below it. Setting the shadows'
        /// dial to match this one is what makes the whole scene agree.
        /// </para>
        /// </summary>
        /// <param name="lightTravel">Unit vector: the way the light moves across the screen, which
        /// is also the way a shadow runs.</param>
        /// <param name="height">1 with the sun overhead, 0 at the edges of the day.</param>
        internal static bool SunInSky(out Vector2 lightTravel, out float height, bool glassRoofCounts = false)
        {
            lightTravel = new Vector2(0f, 1f);
            height = 0f;
            GameLocation? location = Game1.currentLocation;
            if (location == null || !(location.IsOutdoors || (glassRoofCounts && UnderAGlassRoof(location)))
                || location.IsRainingHere() || location.IsSnowingHere() || location.IsLightningHere())
                return false;
            float mins = GameClock.MinutesNow();
            if (mins < 720f - HalfDayMinutesNow() || mins >= TrulyDarkMinutes())
                return false;
            float sky = SunSkyOffsetAt(mins);
            float angle = SunSwingRadians * sky + SunlightBearingRadiansNow;
            lightTravel = new Vector2((float)Math.Sin(angle), -(float)Math.Cos(angle));
            height = 1f - Math.Abs(sky);
            return true;
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
        public void DrawInto(SpriteBatch spriteBatch, ModConfig config)
        {
            if (!ShouldCast(config))
                return;
            if (_renderDepth > 0)
            {
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log("[shadow] DrawInto re-entered — skipping nested call", LogLevel.Warn); }
                return;
            }

            BeginCasterCensus();
            GameLocation location = Game1.currentLocation;
            float strength = MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            float blur = Math.Max(0f, config.DirectionalShadowBlur);
            if (strength <= 0.01f)
                return;
            RefreshBuildingFootprints(location);

            // Dusk is a CROSS-FADE, not a switch. The two paths model the same thing from different
            // sources, and swapping them on the frame SunCasts() flips took every shadow on screen
            // from one direction to another in one frame. House rule: if it changes, it fades.
            // Both paths run while the blend is in transit, each at its share of the strength, so
            // the sun's long shadow thins out as the lamp's grows in.
            _overcastBlend += (OvercastNow() - _overcastBlend) * SunBlendRate;
            if (Math.Abs(OvercastNow() - _overcastBlend) < 0.004f)
                _overcastBlend = OvercastNow();
            // A frozen capture pins both cross-fades at their target, as it pins every other fade:
            // two dumps of one frozen scene taken while a blend was still in transit differed by a
            // level or two along every shadow edge, which is drift of ours, not the game's.
            _overcastBlend = Determinism.Settle(_overcastBlend, OvercastNow());
            float sunTarget = SunCasts() ? 1f : 0f;
            // The benchmark calls this several extra times per frame to measure it. Advancing the
            // dusk cross-fade once per CALL rather than once per frame would run it at seven times
            // speed for the length of the run, so the repeats read the blend without moving it.
            if (!BenchmarkAmplifying)
            {
                _sunBlend += (sunTarget - _sunBlend) * SunBlendRate;
                if (Math.Abs(sunTarget - _sunBlend) < 0.004f)
                    _sunBlend = sunTarget;
                _sunBlend = Determinism.Settle(_sunBlend, sunTarget);
            }

            _renderDepth++;
            // Our shadows are lighting, not bodies: keep them out of the sprite-relief replay
            // (see SpriteDrawRecorder.SuppressRecording for what happened when they got in).
            SpriteDrawRecorder.SuppressRecording = true;
            // And keep them off the doubled sheets for the same reason. A silhouette is stamped
            // black and then blurred, so the smoothed diagonal the upscaler pays for is thrown
            // away a moment later. This batch IS Game1.spriteBatch, which is the only thing the
            // upscaler checks, so without this every shadow read four times the texels it needs.
            // Saved and restored rather than set and cleared: more than one pass suspends this,
            // and a nested one clearing the flag on its way out would hand the rest of the outer
            // pass back to the upscaler without anybody asking for it.
            bool upscalerWasSuspended = SheetUpscaler.SuspendedForOwnDraw;
            SheetUpscaler.SuspendedForOwnDraw = true;
            try
            {
                if (_sunBlend > 0.004f)
                    DrawSunShadows(spriteBatch, location, config, strength * _sunBlend, blur);
                if (_sunBlend < 0.996f)
                    DrawLightShadows(spriteBatch, location, config, strength * (1f - _sunBlend), blur);   // indoors / night → per light source
            }
            catch (Exception ex)
            {
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] draw threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                _renderDepth--;
                SpriteDrawRecorder.SuppressRecording = false;
                SheetUpscaler.SuspendedForOwnDraw = upscalerWasSuspended;
            }
        }
    }
}
