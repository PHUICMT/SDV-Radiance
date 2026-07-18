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
    /// Phase 5b — directional sprite shadows. Draws a leaning, flattened, dark copy
    /// of each caster's sprite (authentic silhouette, not a blob), pinned at the feet
    /// and leaning away from the sun.
    ///
    /// Drawn INTO the game's own <c>World_Sorted</c> batch (SpriteSortMode.FrontToBack)
    /// at a layerDepth just under the caster, so the shadow sits correctly BEHIND the
    /// sprite and is depth-sorted against trees/objects (over ground, under sprites).
    /// Because we draw into the game's open batch we can't use a shear transform, so the
    /// lean is a rotation about the feet plus a vertical squash — sortable per-sprite.
    /// </summary>
    internal sealed class ShadowRenderer
    {
        /// <summary>Optional diagnostics sink; when set (config.DebugLogging), the first few draws + any error are logged once.</summary>
        internal static IMonitor? Diag;

        /// <summary>Optional Height Framework API (null if that mod isn't installed); when present it
        /// gives robust per-tile water/deck classification instead of our own tile heuristics.</summary>
        internal static Integrations.IHeightFrameworkApi? Height;
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
        /// <summary>Gentler feet→tip fade for the big building shadows (their tip should stay visible, not vanish).</summary>
        private Texture2D? _bldGradTex;
        /// <summary>Feet→tip fade that reaches EXACTLY zero, for map-tile props: their art often has
        /// a long straight top edge (fence rails), and any residual tip alpha reads as a hard line.</summary>
        private Texture2D? _propGradTex;
        private Vector2 _playerFeetInRT;
        private bool _playerReady;
        internal const int PlayerRtW = 96;
        internal const int PlayerRtH = 176;

        /// <summary>The player's baked silhouette RT for THIS frame (null when not baked) —
        /// the water shader uses it to exclude exactly the player's own pixels (not a box)
        /// from ring-tile water effects.</summary>
        internal static Texture2D? PlayerMask;
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
        private readonly System.Collections.Generic.Dictionary<object, (RenderTarget2D rt, Vector2 feetInRT)> _bakedMap = new();

        // Buildings are too tall to LEAN by rotation (the roof swings over the building). The correct
        // transform is a SHEAR about the base — but transformMatrix is per-batch, so we bake the
        // sheared+flattened silhouette into a pooled RT here (our own batch CAN set a matrix) and
        // then composite it as a plain draw. Bigger slots than characters (buildings are large).
        private const int BldRtW = 1280;
        private const int BldRtH = 640;
        private readonly System.Collections.Generic.List<RenderTarget2D> _bldPool = new();
        private int _bldUsed;
        private readonly System.Collections.Generic.Dictionary<Building, (RenderTarget2D rt, Vector2 feetInRT)> _bldMap = new();
        private bool _bldDumped;

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

        /// <summary>Master gate: shadows enabled and we're in a normal (non-cutscene) location.</summary>
        internal static bool ShouldCast(ModConfig config)
        {
            if (!config.Enabled || !config.DirectionalShadowsEnabled)
                return false;
            return Game1.currentLocation != null && !Game1.eventUp;
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

        /// <summary>Sun conditions: outdoors, daytime, clear weather → one long sun-cast shadow.
        /// The dusk cutoff follows the game's own seasonal dark time (summer sun sets late),
        /// not a fixed hour — shadows stretch long into the evening until true dark.</summary>
        private static bool SunCasts()
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !loc.IsOutdoors)
                return false;
            int trulyDark;
            try { trulyDark = Game1.getTrulyDarkTime(loc); }
            catch { trulyDark = 2000; }
            return Game1.timeOfDay < trulyDark && Game1.timeOfDay >= 600 && !Game1.isRaining && !Game1.isSnowing;
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

            GameLocation loc = Game1.currentLocation;
            float strength = MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            float blur = Math.Max(0f, config.DirectionalShadowBlur);
            if (strength <= 0.01f)
                return;

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
        }

        /// <summary>One long shadow per caster, leaning away from the sun (outdoors, daytime).</summary>
        private void DrawSunShadows(SpriteBatch b, GameLocation loc, ModConfig config, float strength, float blur)
        {
            ComputeSun(out float rot, out float stretch, out float alpha);
            alpha *= strength;
            if (alpha <= 0.01f)
                return;
            stretch *= Math.Max(0.1f, config.DirectionalShadowLength);

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] sun: npcs={loc.characters.Count}, time={Game1.timeOfDay}, rot={rot:0.00}, stretch={stretch:0.00}, alpha={alpha:0.00}, blur={blur:0.0}", LogLevel.Debug);
            }

            foreach (NPC npc in loc.characters)
            {
                if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnWater(loc, npc.TilePoint))   // don't lay a shadow on the water surface
                    continue;
                DrawNpcShadow(b, npc, rot, stretch, alpha, blur);
            }

            foreach (FarmAnimal a in AnimalsIn(loc))
            {
                if (a?.Sprite?.Texture == null || OnWater(loc, a.TilePoint))
                    continue;
                DrawAnimalShadow(b, a, rot, stretch, alpha, blur);
            }

            DrawPlayerShadow(b, loc, rot, stretch, alpha, blur);

            if (config.DirectionalShadowObjects)
            {
                DrawObjectShadows(b, loc, rot, stretch, alpha, blur);

                // Farm building ENTITIES (coop/barn/house) cast a shape-accurate silhouette from
                // their own texture, anchored at the footprint base — heavily damped/flattened so a
                // tall building's shadow lies beside it instead of swinging up over itself.
                var vp = Game1.viewport;
                int btx0 = vp.X / 64 - 12, btx1 = (vp.X + vp.Width) / 64 + 4;
                int bty0 = vp.Y / 64 - 12, bty1 = (vp.Y + vp.Height) / 64 + 4;
                foreach (Building bld in loc.buildings)
                {
                    if (bld == null || bld.tileX.Value > btx1 || bld.tileX.Value + bld.tilesWide.Value < btx0
                        || bld.tileY.Value > bty1 || bld.tileY.Value + bld.tilesHigh.Value < bty0)
                        continue;
                    float baseX = (bld.tileX.Value + bld.tilesWide.Value / 2f) * 64f;
                    float baseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f;
                    Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(baseX, baseY));
                    float bdepth = MathHelper.Clamp(baseY / 10000f - ShadowDepthBias, 0f, 1f);
                    if (_bldMap.TryGetValue(bld, out var bk))
                        // Sheared silhouette already baked → plain composite (no rotation/scale).
                        DrawSoft(b, Taps9, bk.rt, null, feet, Color.White, alpha * 0.62f, 0f, bk.feetInRT,
                            new Vector2(1f, 1f), bdepth, SpriteEffects.None, blur);
                    else
                        DrawBuildingShadow(b, bld, alpha * 0.7f, blur);   // too big to bake → grounding pool
                }
            }
        }

        private void DrawAnimalShadow(SpriteBatch b, FarmAnimal a, float rot, float stretch, float alpha, float blur)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(a.Position.X + a.Sprite.SpriteWidth * 4 / 2f, a.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(a.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            if (_bakedMap.TryGetValue(a, out var baked))
            {
                DrawSoft(b, Taps9, baked.rt, null, feet, Color.White, alpha, rot, baked.feetInRT,
                    new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
                return;
            }
            Rectangle src = a.Sprite.SourceRect;
            DrawBandedGradient(b, a.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
        }

        /// <summary>
        /// Indoors / at night: each real point light (torch, lamp, fire, fireplace) casts its
        /// own shadow of every caster, radiating AWAY from that light and fading with distance.
        /// Multiple lights → multiple overlapping shadows, as in real multi-light rooms.
        /// </summary>
        private void DrawLightShadows(SpriteBatch b, GameLocation loc, ModConfig config, float strength, float blur)
        {
            // Build the on-screen light list — may be EMPTY (a room with no lamps/windows). We no
            // longer bail on empty: an always-present ambient CONTACT pool grounds every caster
            // even in a lightless room, and point lights ADD their directional shadow on top.
            _lightBuf.Clear();
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                foreach (var kv in lights.Values)
                {
                    LightSource ls = kv;
                    // Cast from real point lights AND window/map lights (a window still throws a
                    // believable shadow across the room). Player-attached lights sit on the player
                    // so they self-cancel in LightCast (dist≈0). Skip nothing by context.
                    Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    // Shadows reach much further than the glow; keep a whole-room-crossing minimum
                    // so a single small window still shadows the far corner.
                    float reach = Math.Max(640f, ls.radius.Value * 64f * 4f);
                    if (screen.X < -reach || screen.X > Game1.viewport.Width + reach ||
                        screen.Y < -reach || screen.Y > Game1.viewport.Height + reach)
                        continue;
                    _lightBuf.Add((screen, reach));
                    if (_lightBuf.Count >= 6)
                        break;
                }
            }

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] light path: lights on-screen={_lightBuf.Count}, ambient contact on", LogLevel.Debug);
            }

            float lenCfg = Math.Max(0.1f, config.DirectionalShadowLength);
            float ambAlpha = strength * 0.4f;   // soft grounding pool; directional cast adds on top

            foreach (NPC npc in loc.characters)
            {
                if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnWater(loc, npc.TilePoint))   // same guard as the sun path (bathhouse, night beach)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom - FeetLift));
                float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                float halfW = npc.GetSpriteWidthForPositioning() * 4f * 0.36f;
                DrawContactBlob(b, feet, halfW, halfW * 0.5f, ambAlpha, depth, blur);
                foreach (var (lpos, reach) in _lightBuf)
                    if (LightCast(feet, lpos, reach, strength, lenCfg, out float rot, out float st, out float a))
                        DrawNpcShadow(b, npc, rot, st, a, blur);
            }

            foreach (FarmAnimal animal in AnimalsIn(loc))
            {
                if (animal?.Sprite?.Texture == null)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(animal.Position.X + animal.Sprite.SpriteWidth * 4 / 2f, animal.GetBoundingBox().Bottom));
                float depth = MathHelper.Clamp(animal.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                float halfW = animal.Sprite.SpriteWidth * 4f * 0.36f;
                DrawContactBlob(b, feet, halfW, halfW * 0.5f, ambAlpha, depth, blur);
                foreach (var (lpos, reach) in _lightBuf)
                    if (LightCast(feet, lpos, reach, strength, lenCfg, out float rot, out float st, out float a))
                        DrawAnimalShadow(b, animal, rot, st, a, blur);
            }

            if (_playerReady && _playerRT != null)
            {
                Farmer who = Game1.player;
                if (who != null && who.currentLocation == loc && !who.swimming.Value && !who.isRidingHorse() && !who.IsSitting()
                    && !OnWater(loc, who.TilePoint))
                {
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                        new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
                    float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                    DrawContactBlob(b, feet, 22f, 11f, ambAlpha, depth, blur);
                    foreach (var (lpos, reach) in _lightBuf)
                        if (LightCast(feet, lpos, reach, strength, lenCfg, out float rot, out float st, out float a))
                            DrawSoft(b, Taps9, _playerRT, null, feet, Color.White, a, rot, _playerFeetInRT,
                                new Vector2(1f, st), depth, SpriteEffects.None, blur);
                }
            }

            // Furniture / big craftables / forage get a light ambient contact pool too (no per-light
            // silhouette — a room full of overlapping cast copies reads as clutter).
            if (config.DirectionalShadowObjects)
                DrawObjectContactShadows(b, loc, ambAlpha * 0.85f, blur);
        }

        /// <summary>Draw a soft dark contact pool (grounding shadow) centred at a screen point.</summary>
        private void DrawContactBlob(SpriteBatch b, Vector2 feet, float halfW, float halfH, float alpha, float depth, float blur)
        {
            if (_blobTex == null || alpha <= 0.01f)
                return;
            var origin = new Vector2(32f, 32f);
            var scale = new Vector2(Math.Max(0.01f, halfW * 2f / 64f), Math.Max(0.01f, halfH * 2f / 64f));
            DrawSoft(b, Taps5, _blobTex, null, feet, Color.Black, alpha, 0f, origin, scale, depth, SpriteEffects.None, blur);
        }

        /// <summary>Ambient contact pools under furniture / craftables / forage (indoor & night path).</summary>
        private void DrawObjectContactShadows(SpriteBatch b, GameLocation loc, float alpha, float blur)
        {
            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 2, tx1 = (vp.X + vp.Width) / 64 + 2;
            int ty0 = vp.Y / 64 - 2, ty1 = (vp.Y + vp.Height) / 64 + 2;

            foreach (Furniture f in loc.furniture)
            {
                if (f == null || f.isTemporarilyInvisible)
                    continue;
                int type = f.furniture_type.Value;
                if (type == 12 || type == 6 || type == 13 || type == 17)   // rugs / wall-mounted
                    continue;
                Vector2 tile = f.TileLocation;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                Rectangle box = f.boundingBox.Value;
                Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(box.Center.X, box.Bottom - 6f));
                float depth = MathHelper.Clamp((box.Bottom - 4f) / 10000f - ShadowDepthBias, 0f, 1f);
                DrawContactBlob(b, feet, box.Width * 0.5f * 0.8f, 12f, alpha, depth, blur);
            }

            foreach (var kv in loc.objects.Pairs)
            {
                Vector2 tile = kv.Key;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                SObject o = kv.Value;
                if (o == null || o.isTemporarilyInvisible)
                    continue;
                float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
                if (o.bigCraftable.Value)
                {
                    if (o.Fragility == 2)
                        continue;
                    Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 8f));
                    DrawContactBlob(b, feet, 26f, 12f, alpha, depth, blur);
                }
                else if (o.IsSpawnedObject)
                {
                    Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 6f));
                    DrawContactBlob(b, feet, 14f, 8f, alpha, depth, blur);
                }
            }
        }

        private readonly System.Collections.Generic.List<(Vector2 pos, float reach)> _lightBuf = new();

        /// <summary>Shadow direction/length/opacity for a caster lit by one point light. False if out of reach.</summary>
        private static bool LightCast(Vector2 feet, Vector2 lightPos, float reach, float strength, float lenCfg,
            out float rot, out float stretch, out float alpha)
        {
            rot = 0f; stretch = 0f; alpha = 0f;
            Vector2 away = feet - lightPos;
            float dist = away.Length();
            if (dist < 1f || dist > reach)
                return false;
            float prox = 1f - dist / reach;                 // 1 next to the light, 0 at its edge
            // Indoor shadows stay SUBTLE (bright rooms) and shorter, so they read softly and
            // climb the (map-baked) walls less than the bold outdoor sun shadow.
            alpha = 0.5f * (0.5f + 0.5f * prox) * strength;
            if (alpha <= 0.02f)
                return false;
            rot = (float)Math.Atan2(away.X, -away.Y);        // point the silhouette away from the light
            stretch = MathHelper.Lerp(0.35f, 0.85f, prox) * lenCfg;
            return true;
        }

        private void DrawNpcShadow(SpriteBatch b, NPC npc, float rot, float stretch, float alpha, float blur)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            // Prefer the baked silhouette (one cohesive image, smoothly faded — same as the
            // player). Bands are the fallback only when the sprite is too big for a slot.
            if (_bakedMap.TryGetValue(npc, out var baked))
            {
                DrawSoft(b, Taps9, baked.rt, null, feet, Color.White, alpha, rot, baked.feetInRT,
                    new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
                return;
            }
            Rectangle src = npc.Sprite.SourceRect;
            DrawBandedGradient(b, npc.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
        }

        /// <summary>Trees and bushes cast the same kind of leaning, fading silhouette as characters.</summary>
        /// <summary>
        /// One entry point for object shadows: during the BAKE pass (RenderingWorld) it renders the
        /// sprite+gradient to a pooled RT keyed by the SPRITE (texture+src+flip — every identical
        /// crop/tree/bush shares one bake); during the COMPOSITE pass it draws that baked RT leaning
        /// by the sun (smooth, no bands). Falls back to <see cref="DrawBandedGradient"/> only when
        /// the sprite is too big for a slot or wasn't baked.
        /// </summary>
        private void EmitObj(SpriteBatch b, Texture2D tex, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, float stretch, float depth, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None)
        {
            var key = (tex, src, effects);
            // The lean is baked as a SHEAR about the feet row (not a rotation): a wide sprite
            // rotated about its feet dips one bottom corner below the ground line, so bushes,
            // benches and lamp heads "drooped down-left". Shearing keeps the whole bottom edge
            // on the ground. Tip position matches the old rotated look exactly:
            //   shear = −sin(rot)·stretch (sideways per px of height), sy = cos(rot)·stretch.
            float shear = -(float)Math.Sin(rot) * stretch;
            float sy = Math.Max(0.15f, stretch * (float)Math.Cos(rot));
            if (_objBaking)
            {
                if (_objGd != null && !_bakedObjMap.ContainsKey(key)
                    && BakeObjSprite(_objGd, tex, src, baseOrigin, effects, shear, out RenderTarget2D rt, out Vector2 feetInRT))
                    _bakedObjMap[key] = (rt, feetInRT);
                return;
            }
            if (_bakedObjMap.TryGetValue(key, out var bk))
                DrawSoft(b, Taps9, bk.rt, null, feet, Color.White, alpha, 0f, bk.feetInRT,
                    new Vector2(1f, sy), depth, SpriteEffects.None, blur);
            else
                DrawBandedGradient(b, tex, src, feet, baseOrigin, alpha, rot,
                    new Vector2(4f, 4f * stretch), depth, blur, headFade, effects);
        }

        /// <summary>Bake a sprite (black + feet→head gradient) to a pooled object RT, its baseOrigin
        /// pinned at the RT's feet point and the sun lean pre-baked as a shear about that row
        /// (x' = x + shear·(y − feetY): bottom edge stays put, higher rows slide sideways).
        /// Returns false (→ banded fallback) if it won't fit a slot.</summary>
        private bool BakeObjSprite(GraphicsDevice gd, Texture2D tex, Rectangle src, Vector2 baseOrigin,
            SpriteEffects effects, float shear, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            rt = null!;
            feetInRT = default;
            if (tex == null || src.IsEmpty)
                return false;
            float w = src.Width * 4f, h = src.Height * 4f;
            if (w + Math.Abs(shear) * h > ObjRtW || h > ObjRtH - 8f)
                return false;

            rt = RentObjRT(gd);
            feetInRT = new Vector2(ObjRtW / 2f, ObjRtH - 8f);
            Vector2 pos = feetInRT - baseOrigin * 4f;      // so baseOrigin maps to the feet point
            Matrix lean = ShearAbout(feetInRT, shear);
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);
                _rtBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                _rtBatch.Draw(tex, pos, src, Color.Black, 0f, Vector2.Zero, 4f, effects, 0f);
                _rtBatch.End();
                // Continuous feet(full)→head(faint) gradient over the sprite's vertical extent.
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex!, new Rectangle(0, (int)pos.Y, ObjRtW, (int)h), Color.White);
                _rtBatch.End();
                return true;
            }
            catch
            {
                try { _rtBatch!.End(); } catch { }
                return false;
            }
        }

        /// <summary>Shear about a pivot row: x' = x + k·(y − pivot.Y), y unchanged — the horizontal
        /// slide grows with height above the feet, which is exactly a cast-shadow lean.</summary>
        private static Matrix ShearAbout(Vector2 pivot, float k)
        {
            return Matrix.CreateTranslation(-pivot.X, -pivot.Y, 0f)
                 * new Matrix(1f, 0f, 0f, 0f, k, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f)
                 * Matrix.CreateTranslation(pivot.X, pivot.Y, 0f);
        }

        private RenderTarget2D RentObjRT(GraphicsDevice gd)
        {
            if (_objUsed < _objPool.Count)
                return _objPool[_objUsed++];
            var rt = new RenderTarget2D(gd, ObjRtW, ObjRtH);
            _objPool.Add(rt);
            _objUsed++;
            return rt;
        }

        private void DrawObjectShadows(SpriteBatch b, GameLocation loc, float rot, float stretch, float alpha, float blur)
        {
            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 3, tx1 = (vp.X + vp.Width) / 64 + 3;
            int ty0 = vp.Y / 64 - 3, ty1 = (vp.Y + vp.Height) / 64 + 8; // extra bottom margin for tall trees

            foreach (var kv in loc.terrainFeatures.Pairs)
            {
                Vector2 tile = kv.Key;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                switch (kv.Value)
                {
                    // Tall sprites swing away from their base under the full character lean
                    // (the canopy shadow detaches from the trunk) — damp the lean for them.
                    // Trees are tall → damp the lean so the canopy shadow stays rooted at the
                    // trunk (its vanilla contact blob is kept to fill the base). Bushes are
                    // short → full lean, matching the character direction, blob suppressed.
                    case Tree tree when tree.growthStage.Value >= 5 && !tree.stump.Value && tree.texture?.Value != null:
                        DrawTreeShadow(b, tree, tile, rot * TreeLeanScale, Math.Min(stretch, TreeStretchMax), alpha, blur);
                        break;
                    case FruitTree ft when ft.growthStage.Value >= 4 && !ft.stump.Value && ft.texture != null:
                        DrawFruitTreeShadow(b, ft, tile, rot * TreeLeanScale, Math.Min(stretch, TreeStretchMax), alpha, blur);
                        break;
                    case Bush bush:
                        DrawBushShadow(b, bush, rot * TallLeanScale, Math.Min(stretch, 0.8f), alpha, blur);
                        break;
                    case HoeDirt { crop: { } crop } hd when !crop.dead.Value && !crop.forageCrop.Value && !crop.IsErrorCrop():
                        DrawCropShadow(b, crop, tile, rot * TallLeanScale, Math.Min(stretch, 0.55f), alpha, blur);
                        break;
                }
            }

            foreach (var ltf in loc.largeTerrainFeatures)
            {
                if (ltf is Bush bush)
                {
                    Vector2 tile = bush.Tile;
                    if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                        continue;
                    DrawBushShadow(b, bush, rot * TallLeanScale, Math.Min(stretch, 0.8f), alpha, blur);
                }
            }

            foreach (ResourceClump clump in loc.resourceClumps)
            {
                if (clump == null)
                    continue;
                Vector2 tile = clump.Tile;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                DrawResourceClumpShadow(b, clump, rot, stretch, alpha, blur);
            }

            foreach (var kv in loc.objects.Pairs)
            {
                Vector2 tile = kv.Key;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                SObject o = kv.Value;
                if (o == null || o.isTemporarilyInvisible)
                    continue;
                if (o.bigCraftable.Value)
                {
                    if (o.Fragility == 2)
                        continue;
                    // Damp the lean (like tall sprites) so a craftable against a wall climbs it less,
                    // and cap the length so a small keg/machine's shadow stays near its own footprint
                    // instead of stretching a full character-length away.
                    DrawBigCraftableShadow(b, o, tile, rot * TallLeanScale, Math.Min(stretch, 0.55f), alpha, blur);
                }
                else if (o.IsSpawnedObject)
                {
                    // Small forage lying on the ground (beach shells, mushrooms, coral…). Short,
                    // strongly-damped shadow.
                    DrawSmallObjectShadow(b, o, tile, rot * TallLeanScale, Math.Min(stretch, 0.4f), alpha, blur);
                }
                else if (!o.isPassable() && o.QualifiedItemId != "(O)590" && o.QualifiedItemId != "(O)SeedSpot")
                {
                    // Everything else that stands on its tile (fences, signs, torches, kegs-as-object,
                    // decor…) gets a real leaning silhouette too — drawn generically from the item's
                    // own sprite via ItemRegistry, so no per-type method is needed. Skip flat passable
                    // items and the ground-mark spots (artifact / seed) that shouldn't cast.
                    DrawGenericObjectShadow(b, o, tile, rot * TallLeanScale, Math.Min(stretch, 0.5f), alpha, blur);
                }
            }

            foreach (Furniture f in loc.furniture)
            {
                if (f == null || f.isTemporarilyInvisible)
                    continue;
                int type = f.furniture_type.Value;
                // Skip rugs (12) and wall-mounted furniture (6 window, 13 wall, 17 painting).
                if (type == 12 || type == 6 || type == 13 || type == 17)
                    continue;
                Vector2 tile = f.TileLocation;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                DrawFurnitureShadow(b, f, rot, stretch, alpha, blur);
            }

            // Critters (birds, squirrels, butterflies, bunnies…) — replace their vanilla blob with
            // the same leaning silhouette as everything else, faded out with flight height exactly
            // like the vanilla blob so airborne critters keep a faint grounded shadow.
            var critters = loc.critters;
            if (critters != null)
            {
                foreach (var c in critters)
                {
                    if (c == null || c is StardewValley.BellsAndWhistles.Cloud || c.sprite?.Texture == null)
                        continue;
                    float fly = Math.Min(1f, Math.Abs((c.yJumpOffset + c.yOffset) / 64f));
                    float ca = alpha * (1f - fly);
                    if (!_objBaking && ca <= 0.02f)
                        continue;
                    Vector2 wpos = c.position;
                    // Squirrel.draw sits a full tile LOWER than the base Critter convention
                    // (sprite offset −64 vs −128; its vanilla blob is at position+60) — match
                    // it or the shadow floats a tile above the squirrel.
                    if (c is StardewValley.BellsAndWhistles.Squirrel)
                        wpos.Y += 60f;
                    int ctx = (int)(wpos.X / 64f), cty = (int)(wpos.Y / 64f);
                    if (ctx < tx0 || ctx > tx1 || cty < ty0 || cty > ty1 || OnWater(loc, new Point(ctx, cty)))
                        continue;
                    Rectangle src = c.sprite.SourceRect;
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport, wpos + new Vector2(0f, -2f));
                    float depth = MathHelper.Clamp((wpos.Y - 1f) / 10000f, 0f, 1f);
                    EmitObj(b, c.sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                        ca, rot * TallLeanScale, Math.Min(stretch, 0.45f), depth, blur, ObjectHeadFade,
                        c.flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
                }
            }

            // Map-drawn props (street lamps, signs, poles…) aren't entities at all — they're tile
            // columns painted on the map. Cast their shadow from the actual tile art.
            DrawTilePropShadows(b, loc, rot, stretch, alpha, blur, tx0, tx1, ty0, ty1);

            // Building shadows via the sprite-lean path stay DISABLED (leaning a whole-building
            // sprite projects it up over itself). Their real ground projection is done separately
            // in DrawHeightShadows using Height Framework data — see DrawSunShadows.
        }

        /// <summary>
        /// Shadows for props painted INTO the map (street lamps, signposts, poles…): a Buildings-layer
        /// base tile with Front-layer tiles stacked above it. Only 1-tile-wide, free-standing columns
        /// cast — wider Front regions are house roofs/tree canopies, which must not (a leaning
        /// house-wall shadow is exactly the artifact we removed). The silhouette is baked from the
        /// column's REAL tile art, so whatever the prop looks like, its shadow matches.
        /// </summary>
        private void DrawTilePropShadows(SpriteBatch b, GameLocation loc, float rot, float stretch,
            float alpha, float blur, int tx0, int tx1, int ty0, int ty1)
        {
            var front = loc.map?.GetLayer("Front");
            var always = loc.map?.GetLayer("AlwaysFront");
            var bldg = loc.map?.GetLayer("Buildings");
            if (front == null || bldg == null)
                return;
            int W = Math.Min(front.LayerWidth, bldg.LayerWidth), H = Math.Min(front.LayerHeight, bldg.LayerHeight);
            tx0 = Math.Max(0, tx0); tx1 = Math.Min(W - 1, tx1);
            ty0 = Math.Max(1, ty0); ty1 = Math.Min(H - 1, ty1);

            // Some maps paint the pole top on AlwaysFront instead of Front — treat them as one layer.
            xTile.Tiles.Tile? Ft(int xx, int yy)
            {
                var t = front.Tiles[xx, yy];
                if (t == null && always != null && xx < always.LayerWidth && yy < always.LayerHeight)
                    t = always.Tiles[xx, yy];
                return t;
            }

            float rotD = rot * TallLeanScale;
            float stD = Math.Min(stretch, 0.6f);
            float shear = -(float)Math.Sin(rotD) * stD;
            float sy = Math.Max(0.15f, stD * (float)Math.Cos(rotD));

            // Near-player prop diagnostics (DebugLogging): every ~3s log why Buildings tiles within
            // 4 tiles of the player do or don't cast — the quick way to see why a fence stays bare.
            bool pdiag = Diag != null && !_objBaking && Game1.ticks % 600 == 0;
            Point ppt = Game1.player?.TilePoint ?? default;
            void PD(int xx, int yy, string why)
            {
                if (pdiag && Math.Abs(xx - ppt.X) <= 4 && Math.Abs(yy - ppt.Y) <= 4)
                    Diag!.Log($"[shadow] prop({xx},{yy}) {why}", LogLevel.Debug);
            }

            for (int y = ty0; y <= ty1; y++)
            {
                for (int x = tx0; x <= tx1; x++)
                {
                    var bt = bldg.Tiles[x, y];
                    if (bt == null)
                        continue;
                    // A prop base is a Buildings tile. Front art on the SAME row is normal for
                    // fences (their upper half is painted there so the player walks behind it) —
                    // it joins the silhouette as a level-0 overlay rather than disqualifying the
                    // cell. Animated tiles are skipped — a frozen frame would cast a lie.
                    if (bt is xTile.Tiles.AnimatedTile || bt.TileSheet == null)
                    {
                        PD(x, y, bt is xTile.Tiles.AnimatedTile ? "skip: animated tile" : "skip: no tilesheet");
                        continue;
                    }
                    Texture2D? tex = LoadCached(bt.TileSheet.ImageSource);
                    if (tex == null)
                        continue;
                    var ibB = bt.TileSheet.GetTileImageBounds(bt.TileIndex);
                    var baseSrc = new Rectangle(ibB.X, ibB.Y, ibB.Width, ibB.Height);
                    float cov = TileCoverage(tex, baseSrc);

                    // Fences paint their upper half on Front at the SAME row (so the player can
                    // walk behind them). Fold that art into the prop: classification uses the
                    // union coverage, and the silhouette gets it as a level-0 overlay. When the
                    // Buildings tile is a bare INVISIBLE collision tile (cov≈0) under Front-drawn
                    // art on another sheet, the Front art IS the prop — adopt its sheet instead.
                    Rectangle? sameSrc = null;
                    int sameIdx = 0, baseIdx = bt.TileIndex;
                    {
                        var st = Ft(x, y);
                        if (st != null && st is not xTile.Tiles.AnimatedTile && st.TileSheet != null
                            && LoadCached(st.TileSheet.ImageSource) is { } stex)
                        {
                            var ibS = st.TileSheet.GetTileImageBounds(st.TileIndex);
                            var srcS = new Rectangle(ibS.X, ibS.Y, ibS.Width, ibS.Height);
                            if (ReferenceEquals(stex, tex))
                            {
                                sameSrc = srcS;
                                sameIdx = st.TileIndex;
                                cov = Math.Max(cov, TileCoverage(tex, srcS));
                            }
                            else if (cov < 0.04f)
                            {
                                tex = stex;
                                baseSrc = srcS;
                                baseIdx = st.TileIndex;
                                cov = TileCoverage(stex, srcS);
                            }
                        }
                    }

                    // Read the ART itself: partially transparent art = a free-standing prop (fence,
                    // post, sign, lamp base) → casts. Fully opaque art = terrain/wall (cliff faces,
                    // house walls, path edging) → never casts, EXCEPT the boxed-prop case: an opaque
                    // bottom standing directly on walkable ground with Front art stacked above
                    // (planters, crates) — and only in runs ≤2 tiles so house walls stay excluded.
                    // Cast bound is looser (0.95) than the wall bound (0.90): dense picket-fence
                    // tiles read ~0.9x coverage while real walls sit at ~1.0.
                    bool aProp = cov >= 0.04f && cov <= 0.95f;
                    bool bProp = false;
                    if (!aProp && cov > 0.90f && sameSrc == null && (y + 1 >= H || bldg.Tiles[x, y + 1] == null) && Ft(x, y - 1) != null)
                    {
                        bool BCell(int xx) => xx >= 0 && xx < W && bldg.Tiles[xx, y] != null && Ft(xx, y) == null
                            && Ft(xx, y - 1) != null && (y + 1 >= H || bldg.Tiles[xx, y + 1] == null);
                        int run = 1, l = x - 1, r = x + 1;
                        while (BCell(l)) { run++; l--; }
                        while (BCell(r)) { run++; r++; }
                        bProp = run <= 2;
                    }
                    if (!aProp && !bProp)
                    {
                        PD(x, y, $"skip: cov={cov:0.00} → not a prop");
                        continue;
                    }
                    // A "prop" sitting ON opaque art below is wall decor — a window halfway up a
                    // house wall must not cast.
                    if (OpaqueMapTile(bldg, x, y + 1, H))
                    {
                        PD(x, y, "skip: sits on opaque art (wall decor)");
                        continue;
                    }
                    // A transparent tile BESIDE opaque art is the fringe of a big structure (the
                    // truck's edge tiles, awning ends…), not a free-standing prop — its lone-column
                    // cast reads as a stray dark line. Real fences/posts never hug opaque art.
                    if (aProp && (OpaqueMapTile(bldg, x - 1, y, H) || OpaqueMapTile(bldg, x + 1, y, H)))
                    {
                        PD(x, y, "skip: opaque neighbour beside (structure fringe)");
                        continue;
                    }
                    // Skip only when the prop itself (or the tile its lean lands on) is open WATER
                    // SURFACE — pier decks over water are solid ground (Height Framework separates
                    // deck from water), so dock ropes / mooring posts / lanterns cast onto the pier.
                    if (OnWater(loc, new Point(x, y)) || OnWater(loc, new Point(x, y - 1)))
                    {
                        PD(x, y, "skip: on/over open water");
                        continue;
                    }

                    // Gather the column bottom→top: the base tile, its same-row Front overlay
                    // (level 0 too), then any Front stack above (level = tiles above the base).
                    _colSrcBuf[0] = baseSrc;
                    _colLvlBuf[0] = 0;
                    int count = 1, levels = 1, keyHash = 17 * 31 + baseIdx;
                    if (sameSrc is Rectangle sr)
                    {
                        _colSrcBuf[count] = sr;
                        _colLvlBuf[count++] = 0;
                        keyHash = keyHash * 31 + sameIdx;
                    }
                    for (int i = 1; count < _colSrcBuf.Length && y - i >= 0; i++)
                    {
                        var t = Ft(x, y - i);
                        if (t == null || t is xTile.Tiles.AnimatedTile || t.TileSheet == null
                            || !ReferenceEquals(LoadCached(t.TileSheet.ImageSource), tex))
                            break;
                        var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
                        _colSrcBuf[count] = new Rectangle(ib.X, ib.Y, ib.Width, ib.Height);
                        _colLvlBuf[count++] = i;
                        levels = i + 1;
                        keyHash = keyHash * 31 + t.TileIndex;
                    }

                    // Wall guard, scaled to the prop's height: the up-lean cast occupies the tiles
                    // north of the base (and one column toward the lean side) — if any of those is
                    // opaque wall art, the shadow would paint onto the wall ("through the house").
                    int leanDir = shear > 0.01f ? -1 : (shear < -0.01f ? 1 : 0);
                    bool blocked = false;
                    for (int i = 1; i <= levels && !blocked; i++)
                        blocked = OpaqueMapTile(bldg, x, y - i, H)
                               || (leanDir != 0 && OpaqueMapTile(bldg, x + leanDir, y - i, H));
                    if (blocked)
                    {
                        PD(x, y, "skip: wall to the north (lean would paint onto it)");
                        continue;
                    }
                    PD(x, y, $"cast: col={count} cov={cov:0.00}");

                    // Synthetic sprite key: width −1 can never collide with a real source rect.
                    var key = (tex, new Rectangle(keyHash, count, -1, -1), SpriteEffects.None);
                    if (_objBaking)
                    {
                        if (_objGd != null && !_bakedObjMap.ContainsKey(key)
                            && BakeTileColumn(_objGd, tex, count, shear, out RenderTarget2D rt, out Vector2 fInRT))
                            _bakedObjMap[key] = (rt, fInRT);
                        continue;
                    }
                    if (!_bakedObjMap.TryGetValue(key, out var bk))
                        continue;
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f + 32f, (y + 1f) * 64f - 2f));
                    float depth = MathHelper.Clamp(((y + 1f) * 64f) / 10000f + x * 1e-5f - ShadowDepthBias, 0f, 1f);
                    DrawSoft(b, Taps9, bk.rt, null, feet, Color.White, alpha, 0f, bk.feetInRT,
                        new Vector2(1f, sy), depth, SpriteEffects.None, blur);
                    // Redraw the base tile OVER its own shadow: the map layer painted before this
                    // batch, so without this the near end of the cast darkens the prop itself
                    // (the "shadow on the lamp post" complaint). Front-stack tiles need no redraw —
                    // the Front layer paints after us anyway.
                    b.Draw(tex, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f, y * 64f)), baseSrc,
                        Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, Math.Min(1f, depth + 5e-4f));
                }
            }
        }

        /// <summary>True when a Buildings tile exists at (x,y) and its art is essentially opaque
        /// (terrain/wall art, not a see-through prop). Out-of-range or empty → false.</summary>
        private bool OpaqueMapTile(xTile.Layers.Layer bldg, int x, int y, int H)
        {
            if (y < 0 || y >= H || x < 0 || x >= bldg.LayerWidth)
                return false;
            var t = bldg.Tiles[x, y];
            if (t == null || t.TileSheet == null)
                return false;
            if (t is xTile.Tiles.AnimatedTile)
                return true;   // animated map art next to a prop → treat as solid, don't cast
            Texture2D? tex = LoadCached(t.TileSheet.ImageSource);
            if (tex == null)
                return true;
            var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
            // Same bound as the aProp cast test (0.95). With a LOWER bound here, dense fence
            // tiles at cov 0.91–0.95 counted as "walls" and poisoned their own neighbours —
            // every tile of the row skipped as "structure fringe" and no fence ever cast.
            return TileCoverage(tex, new Rectangle(ib.X, ib.Y, ib.Width, ib.Height)) > 0.95f;
        }

        /// <summary>Fraction of a tile's art that is opaque (alpha &gt; 48). Sampled once per
        /// (sheet, rect) and cached — this is the "look at the actual image" prop test.</summary>
        private readonly System.Collections.Generic.Dictionary<(Texture2D tex, Rectangle src), float> _tileCovCache = new();
        private Color[] _covBuf = new Color[1024];

        private float TileCoverage(Texture2D tex, Rectangle src)
        {
            if (_tileCovCache.TryGetValue((tex, src), out float cov))
                return cov;
            int len = src.Width * src.Height;
            if (len <= 0 || src.X < 0 || src.Y < 0 || src.Right > tex.Width || src.Bottom > tex.Height)
                return _tileCovCache[(tex, src)] = 1f;
            if (_covBuf.Length < len)
                _covBuf = new Color[len];
            try { tex.GetData(0, src, _covBuf, 0, len); }
            catch { return _tileCovCache[(tex, src)] = 1f; }
            int solid = 0;
            for (int i = 0; i < len; i++)
                if (_covBuf[i].A > 48) solid++;
            return _tileCovCache[(tex, src)] = (float)solid / len;
        }

        /// <summary>Per-column tile source rects, filled by the scan then baked.</summary>
        private readonly Rectangle[] _colSrcBuf = new Rectangle[7];
        /// <summary>Height level (tiles above the base row) for each entry of <see cref="_colSrcBuf"/> —
        /// a same-row Front overlay shares level 0 with the base tile.</summary>
        private readonly int[] _colLvlBuf = new int[7];

        /// <summary>Bake a stacked tile column (black + feet→tip gradient, sun lean pre-baked as a
        /// shear about the feet row) into a pooled object RT.
        /// Reads the sources/levels from <see cref="_colSrcBuf"/>/<see cref="_colLvlBuf"/>.</summary>
        private bool BakeTileColumn(GraphicsDevice gd, Texture2D tex, int count, float shear, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            rt = null!;
            feetInRT = default;
            int levels = 0;
            for (int i = 0; i < count; i++)
                levels = Math.Max(levels, _colLvlBuf[i] + 1);
            float h = levels * 64f;
            if (count <= 0 || h > ObjRtH - 8f || 64f + Math.Abs(shear) * h > ObjRtW)
                return false;
            rt = RentObjRT(gd);
            feetInRT = new Vector2(ObjRtW / 2f, ObjRtH - 8f);
            Matrix lean = ShearAbout(feetInRT, shear);
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);
                _rtBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                for (int i = 0; i < count; i++)
                    _rtBatch.Draw(tex, new Vector2(feetInRT.X - 32f, feetInRT.Y - 64f * (_colLvlBuf[i] + 1)),
                        _colSrcBuf[i], Color.Black, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                _rtBatch.End();
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_propGradTex!, new Rectangle(0, (int)(feetInRT.Y - h), ObjRtW, (int)h), Color.White);
                _rtBatch.End();
                return true;
            }
            catch
            {
                try { _rtBatch!.End(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Generic silhouette for ANY tile-placed object, drawn from the item's own sprite
        /// (ItemRegistry) — the type-agnostic caster that means we don't hand-write a method per
        /// object kind. Anchored bottom-centre at the tile's ground line; height comes from the
        /// sprite itself, so a 16- or 32-tall item both sit right.
        /// </summary>
        private void DrawGenericObjectShadow(SpriteBatch b, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            var data = ItemRegistry.GetDataOrErrorItem(o.QualifiedItemId);
            Texture2D tex = data.GetTexture();
            if (tex == null)
                return;
            Rectangle src = data.GetSourceRect();
            if (src.IsEmpty)
                return;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 6f));
            float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            EmitObj(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        /// <summary>
        /// Buildings are too tall for an upright silhouette (it juts up over the building itself),
        /// so they get a soft contact POOL at the footprint base instead — grounds the building
        /// without overlapping it or ghosting. Shape-accurate isn't achievable for tall map/entity
        /// structures with these 2D techniques; a grounding pool is the clean compromise.
        /// </summary>
        private void DrawBuildingShadow(SpriteBatch b, Building bld, float alpha, float blur)
        {
            float w = bld.tilesWide.Value * 64f;
            float baseX = (bld.tileX.Value + bld.tilesWide.Value / 2f) * 64f;
            float baseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f;   // footprint bottom = ground line
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(baseX, baseY - 10f));
            float depth = MathHelper.Clamp(baseY / 10000f - ShadowDepthBias, 0f, 1f);
            DrawContactBlob(b, feet, w * 0.5f * 0.85f, 24f, alpha, depth, blur);
        }

        /// <summary>Small forage lying on the ground (16x16) — a short leaning silhouette to ground it.</summary>
        private void DrawSmallObjectShadow(SpriteBatch b, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            var data = ItemRegistry.GetDataOrErrorItem(o.QualifiedItemId);
            Texture2D tex = data.GetTexture();
            if (tex == null)
                return;
            Rectangle src = data.GetSourceRect();
            // Forage rests near the tile's bottom edge; small lift so the shadow base meets the item.
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 12f));
            float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            EmitObj(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        /// <summary>
        /// Project ground shadows for walls / buildings / ledges from Height Framework data.
        ///
        /// Standard heightfield self-shadowing (a "gather" horizon march): for each visible
        /// ground tile, step toward the sun; if a taller tile rises above the sun ray at that
        /// distance, the ground tile is occluded → shaded. This casts a building's shadow onto
        /// the ground on the sun-opposite side at a length set by the sun height — the durable,
        /// generic replacement for per-building sprite leaning (works for cliffs/piers too).
        /// </summary>
        private void DrawHeightShadows(SpriteBatch b, GameLocation loc, ModConfig config, float baseAlpha, float blur)
        {
            var api = Height;
            if (api == null)
                return;

            // Match the character silhouette's lean EXACTLY so the two systems agree in direction.
            // The character shadow leans by rot = 1.15*d (ComputeSun); the far end of an upright
            // sprite rotated by rot points (-sin rot, -cos rot). Use that same vector as the ground
            // shadow direction, and march the opposite way (toward the sun) to find the occluder.
            float d = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            float low = Math.Abs(d);
            float rot = 1.15f * d;
            var shadowDir = new Vector2(-(float)Math.Sin(rot), -(float)Math.Cos(rot));
            Vector2 sunDir = -shadowDir;

            float maxLen = MathHelper.Lerp(1.5f, 5.5f, low) * Math.Max(0.2f, config.HeightShadowLength); // tiles
            int steps = Math.Max(1, (int)Math.Ceiling(maxLen));
            float slope = 1f / Math.Max(0.6f, maxLen);     // sun-ray height gained per tile of distance
            float alpha = baseAlpha * 0.5f;                // ground shadows read lighter than sprite ones
            if (alpha <= 0.02f)
                return;

            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 1, tx1 = (vp.X + vp.Width) / 64 + 1;
            int ty0 = vp.Y / 64 - 1, ty1 = (vp.Y + vp.Height) / 64 + 1;

            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    // Only cast ONTO flat ground: skip occluder tops (h>0), water, and decks.
                    if (api.GetHeightAt(loc, tx, ty) > 0 || api.GetSurfaceAt(loc, tx, ty) != 0)
                        continue;

                    float shade = 0f;
                    for (int s = 1; s <= steps; s++)
                    {
                        int sx = tx + (int)Math.Round(sunDir.X * s);
                        int sy = ty + (int)Math.Round(sunDir.Y * s);
                        // Only GENUINELY tall occluders cast (buildings are stamped height 2). Map
                        // Front-layer art, pond rims, walls and pier decks are height ≤1 — casting
                        // from those littered flat sand with spurious shadows, so require ≥2.
                        int h = api.GetHeightAt(loc, sx, sy);
                        if (h < 2)
                            continue;
                        float rayH = s * slope;             // how high the sun ray is above the ground here
                        float over = h - rayH;              // occluder pokes above the ray ⇒ shadow
                        if (over > 0f)
                        {
                            // Fade with distance so the shadow tip is soft, and clamp the near core.
                            float k = MathHelper.Clamp(over, 0f, 1f) * (1f - (s - 1) / (float)steps);
                            if (k > shade) shade = k;
                        }
                    }
                    if (shade <= 0.02f)
                        continue;

                    // Draw a SOFT radial pool centred on the tile, ~1.4 tiles wide so neighbouring
                    // shadowed tiles overlap and blend — this is what dissolves the hard 64px grid.
                    Vector2 center = Game1.GlobalToLocal(vp, new Vector2(tx * 64f + 32f, ty * 64f + 32f));
                    float depth = MathHelper.Clamp(((ty + 1f) * 64f) / 10000f - ShadowDepthBias, 0f, 1f);
                    DrawContactBlob(b, center, 46f, 46f, alpha * shade, depth, blur);
                }
            }
        }

        /// <summary>Lean damping for tall sprites (bushes/craftables) so the shadow stays rooted at the base.</summary>
        private const float TallLeanScale = 0.6f;
        /// <summary>Trees lean/stretch the least: the long canopy cast otherwise detaches from the
        /// trunk (and its contact blob), reading as two separate shadows.</summary>
        private const float TreeLeanScale = 0.38f;
        private const float TreeStretchMax = 0.6f;
        /// <summary>Lift the character/animal feet anchor a touch so the shadow base sits at the
        /// visual feet rather than a few px below (the bounding-box bottom overshoots).</summary>
        private const float FeetLift = 10f;
        /// <summary>
        /// Objects use the same strong feet→tip fade as characters: a DARK base grounds the
        /// shadow (the earlier gentle/uniform fade read as floaty — the fix was a darker base,
        /// not a flatter gradient).
        /// </summary>
        private const float ObjectHeadFade = HeadFade;

        private void DrawBigCraftableShadow(SpriteBatch b, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            var data = ItemRegistry.GetDataOrErrorItem(o.QualifiedItemId);
            Texture2D tex = data.GetTexture();
            if (tex == null)
                return;
            Rectangle src = data.GetSourceRect();
            // Big craftables sit ON their tile; the barrel/machine visually rests a bit above the
            // tile's bottom edge, so anchor the shadow's dark base slightly up from (tile.Y+1)*64.
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 20f));
            float depth = MathHelper.Clamp(Math.Max(0f, ((tile.Y + 1f) * 64f - 24f) / 10000f) + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            EmitObj(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur);
        }

        /// <summary>
        /// Crop.draw uses a 16x32 source cell drawn at scale 4 with the game's draw-origin (8,24).
        /// For a SHADOW we pivot/anchor at the cell BOTTOM (8,32) instead — the plant's ground
        /// contact — so the lean swings the plant from its base (not its mid-stem, which read as a
        /// weird direction) and no cell rows fall below the feet (which read as "too low"). The
        /// transparent padding above young growth stages means the shadow shrinks with the plant.
        /// </summary>
        private static readonly Vector2 CropOrigin = new(8f, 32f);

        private void DrawCropShadow(SpriteBatch b, Crop crop, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            Texture2D tex = crop.DrawnCropTexture;
            if (tex == null || crop.sourceRect.IsEmpty)
                return;
            // The game draws origin (8,24) at drawPosition; the cell bottom (y=32) sits at
            // drawPosition.Y + 32 ≈ the tile's bottom edge. Lift the anchor ~12px so the shadow
            // base meets the plant where it roots on the soil mound (sitting at the raw tile
            // bottom read as "too low" and left young sprouts looking detached).
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(crop.drawPosition.X, crop.drawPosition.Y + 20f));
            float depth = MathHelper.Clamp((tile.Y * 64f + 64f) / 10000f + tile.X / 100000f - ShadowDepthBias, 0f, 1f);
            // Crops are randomly mirrored (Crop.flip); match it so an asymmetric sprite's shadow
            // leans the same way its plant does instead of pointing the opposite direction.
            // RT-baked like everything else — the sprite-keyed dedup means a whole field of the
            // same crop/phase shares ONE bake, so this is cheap even with hundreds planted.
            SpriteEffects fx = crop.flip.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            EmitObj(b, tex, crop.sourceRect, feet, CropOrigin,
                alpha, rot, stretch, depth, blur, ObjectHeadFade, fx);
        }

        private void DrawFurnitureShadow(SpriteBatch b, Furniture f, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = f.sourceRect.Value;
            if (src.IsEmpty)
                return;
            var data = ItemRegistry.GetDataOrErrorItem(f.QualifiedItemId);
            Texture2D tex = data.GetTexture();
            if (tex == null)
                return;
            // Anchor at the footprint's bottom-centre (drawPosition is protected; the bounding
            // box bottom matches the sprite's ground line for floor furniture).
            Rectangle box = f.boundingBox.Value;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom - 30f));
            float depth = MathHelper.Clamp((box.Bottom - 8f) / 10000f - ShadowDepthBias, 0f, 1f);
            EmitObj(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur);
        }

        // ContentManager.Load is cached but still does per-call path normalization + a
        // dictionary lookup — too much for every clump every frame. Cache per texture name.
        private readonly System.Collections.Generic.Dictionary<string, Texture2D> _texCache = new();

        private Texture2D? LoadCached(string? name)
        {
            if (name == null)
                return Game1.objectSpriteSheet;
            if (!_texCache.TryGetValue(name, out Texture2D? tex))
            {
                try { tex = Game1.content.Load<Texture2D>(name); }
                catch { tex = null!; }
                _texCache[name] = tex!;
            }
            return tex;
        }

        private void DrawResourceClumpShadow(SpriteBatch b, ResourceClump clump, float rot, float stretch, float alpha, float blur)
        {
            Texture2D? tex = LoadCached(clump.textureName.Value);
            if (tex == null)
                return;
            Rectangle src = Game1.getSourceRectForStandardTileSheet(tex, clump.parentSheetIndex.Value, 16, 16);
            src.Width = clump.width.Value * 16;
            src.Height = clump.height.Value * 16;
            Vector2 tile = clump.Tile;
            // Clump draws top-left at tile*64, origin zero, scale 4 → sprite bottom = tile*64 +
            // src.Height*4; anchor a touch above that (ground contact of the art). The old −40
            // lift was compensation for the rotation-era corner dip — with the shear lean it just
            // sank the shadow's base behind the sprite, so the stump's cast looked partial.
            var worldFeet = new Vector2(tile.X * 64f + src.Width * 2f, tile.Y * 64f + src.Height * 4f - 14f);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, worldFeet);
            var baseOrigin = new Vector2(src.Width / 2f, src.Height);
            float depth = MathHelper.Clamp((tile.Y + 1f) * 64f / 10000f + tile.X / 100000f - ShadowDepthBias, 0f, 1f);
            EmitObj(b, tex, src, feet, baseOrigin, alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        private void DrawTreeShadow(SpriteBatch b, Tree tree, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = Tree.treeTopSourceRect;                 // (0,0,48,96) standard canopy
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f));
            float depth = MathHelper.Clamp((tree.getBoundingBox().Bottom + 2f) / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            // Tree canopy draws with origin (24, 96); fade about the trunk base.
            EmitObj(b, tree.texture.Value, src, feet, new Vector2(24f, 96f),
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        private void DrawFruitTreeShadow(SpriteBatch b, FruitTree ft, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            // Mature fruit-tree canopy (FruitTree.draw): 48x64 foliage rect, drawn at
            // (tile*64 + 32, tile*64 + 64) with origin (24, 80).
            int season = Game1.GetSeasonIndexForLocation(ft.Location);
            int row = ft.GetSpriteRowNumber();
            var src = new Rectangle((12 + season * 3) * 16, row * 5 * 16, 48, 64);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f));
            float depth = MathHelper.Clamp(ft.getBoundingBox().Bottom / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            EmitObj(b, ft.texture, src, feet, new Vector2(24f, 80f),
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        private void DrawBushShadow(SpriteBatch b, Bush bush, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = bush.sourceRect.Value;
            if (src.IsEmpty)
                return;
            Vector2 tile = bush.Tile;
            // Bush.draw pins source (originX,32) at a point whose NET effect (for every size:
            // small/medium/large/tea/walnut) is: sprite bottom-centre = (tile.X*64 + (eff+1)*32,
            // (tile.Y+1)*64). Anchoring at the pin itself (old code) floated 48-tall bushes' shadow
            // a full tile above the ground AND clipped the sprite's bottom rows out of the bake —
            // that was the faint/short bush shadow. Anchor at the true bottom instead.
            int eff = bush.size.Value switch { 3 => 0, 4 => 1, _ => bush.size.Value };
            var worldFeet = new Vector2(tile.X * 64f + (eff + 1) * 32f, (tile.Y + 1) * 64f - 8f);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, worldFeet);
            var baseOrigin = new Vector2(src.Width / 2f, src.Height);
            float depth = MathHelper.Clamp((bush.getBoundingBox().Center.Y + 48f) / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            SpriteEffects fx = bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            EmitObj(b, Bush.texture.Value, src, feet, baseOrigin,
                alpha, rot, stretch, depth, blur, ObjectHeadFade, fx);
        }

        private void DrawPlayerShadow(SpriteBatch b, GameLocation loc, float rot, float stretch, float alpha, float blur)
        {
            if (!_playerReady || _playerRT == null)
                return;

            Farmer who = Game1.player;
            if (OnWater(loc, who.TilePoint))
                return;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);

            // The baked silhouette is one cohesive image — flatten it vertically and lean it
            // about the feet as a single unit (no per-layer fragmenting), softened at the edges.
            DrawSoft(b, Taps9, _playerRT, null, feet, Color.White, alpha, rot, _playerFeetInRT,
                new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
        }

        /// <summary>
        /// Render the player's full silhouette (all FarmerRenderer layers, so hats / hair /
        /// Fashion-Sense outfits are included) to an offscreen target, upright and black.
        /// Called during RenderingWorld, before the world batches open, so a render-target
        /// swap is safe. The lean/squash/soften happen later when this is composited.
        /// </summary>
        public void PreparePlayer(GraphicsDevice gd, ModConfig config)
        {
            _playerReady = false;
            PlayerMask = null;
            _bakedMap.Clear();
            _bldMap.Clear();
            _bakedObjMap.Clear();
            _casterUsed = 0;
            _bldUsed = 0;
            _objUsed = 0;
            if (!ShouldCast(config))
                return;

            _rtBatch ??= new SpriteBatch(gd);
            _gradTex ??= BuildGradient(gd);
            _bldGradTex ??= BuildGradient(gd, 0.35f);
            _propGradTex ??= BuildGradient(gd, 0f);
            _blobTex ??= BuildBlob(gd);

            // Buildings: the shear-down cast conflicted with the upright character/object lean
            // (looked "เพี้ยน"), so buildings fall back to a neutral grounding CONTACT POOL — no
            // direction to clash with the up-lean. (BakeBuildings kept for a future opt-in.)
            // if (SunCasts() && config.DirectionalShadowObjects)
            //     BakeBuildings(gd, Game1.currentLocation, config);

            // Bake NPC + animal silhouettes (single-sprite casters) to pooled targets so their
            // shadows match the player's smooth, cohesive fade instead of stepped bands.
            BakeCasters(gd, Game1.currentLocation);

            // Bake OBJECT silhouettes (trees/bushes/clumps/furniture/craftables/…) the same way, by
            // running the object enumeration once in BAKE mode. Composited later in DrawObjectShadows.
            if (SunCasts() && config.DirectionalShadowObjects)
            {
                // The bake needs the REAL sun angle: the lean is baked into the RT as a shear,
                // so bake and composite must agree on rot/stretch (same frame, same values).
                ComputeSun(out float srot, out float sstretch, out _);
                sstretch *= Math.Max(0.1f, config.DirectionalShadowLength);
                _objBaking = true;
                _objGd = gd;
                RenderTargetBinding[] objPrev = gd.GetRenderTargets();
                try { DrawObjectShadows(_rtBatch, Game1.currentLocation, srot, sstretch, 0f, 0f); }
                catch (Exception ex) { if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] obj bake threw: {ex}", LogLevel.Warn); } }
                finally { gd.SetRenderTargets(objPrev); _objBaking = false; }
            }

            Farmer who = Game1.player;
            if (who == null || who.currentLocation != Game1.currentLocation
                || who.swimming.Value || who.isRidingHorse() || who.IsSitting())
                return;

            _playerRT ??= new RenderTarget2D(gd, PlayerRtW, PlayerRtH);

            Rectangle src = who.FarmerSprite.SourceRect;
            float w = src.Width * 4f, h = src.Height * 4f;
            Vector2 pos = new Vector2((PlayerRtW - w) / 2f, PlayerRtH - h - 8f);
            _playerFeetInRT = new Vector2(PlayerRtW / 2f, PlayerRtH - 8f);

            RenderTargetBinding[] prev = gd.GetRenderTargets();
            try
            {
                gd.SetRenderTarget(_playerRT);
                gd.Clear(Color.Transparent);
                _rtBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_rtBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                    src, pos, Vector2.Zero, 0f, who.FacingDirection, Color.Black, 0f, 1f, who);
                _rtBatch.End();

                // Fade the silhouette's opacity from the feet (full) to the head/far tip (faint),
                // so the stretched far end reads as a soft penumbra rather than a hard clone.
                _gradTex ??= BuildGradient(gd);
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
                _rtBatch.End();
                _playerReady = true;
                PlayerMask = _playerRT;
            }
            catch (Exception ex)
            {
                try { _rtBatch.End(); } catch { }
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] player RT prep threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                gd.SetRenderTargets(prev);
            }
        }

        /// <summary>
        /// Bake every on-screen NPC and animal to its own pooled offscreen target (black
        /// silhouette + feet→head alpha gradient), so <see cref="DrawNpcShadow"/> /
        /// <see cref="DrawAnimalShadow"/> can composite one smooth image instead of banding.
        /// Runs during RenderingWorld, where a render-target swap is safe.
        /// </summary>
        private void BakeCasters(GraphicsDevice gd, GameLocation loc)
        {
            if (loc == null)
                return;
            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 3, tx1 = (vp.X + vp.Width) / 64 + 3;
            int ty0 = vp.Y / 64 - 3, ty1 = (vp.Y + vp.Height) / 64 + 3;

            RenderTargetBinding[] prev = gd.GetRenderTargets();
            try
            {
                foreach (NPC npc in loc.characters)
                {
                    if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                        continue;
                    Point t = npc.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    if (BakeSprite(gd, npc.Sprite.Texture, npc.Sprite.SourceRect, out RenderTarget2D rt, out Vector2 feet))
                        _bakedMap[npc] = (rt, feet);
                }
                foreach (FarmAnimal a in AnimalsIn(loc))
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    Point t = a.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    if (BakeSprite(gd, a.Sprite.Texture, a.Sprite.SourceRect, out RenderTarget2D rt, out Vector2 feet))
                        _bakedMap[a] = (rt, feet);
                }
            }
            catch (Exception ex)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] caster bake threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                gd.SetRenderTargets(prev);
            }
        }

        /// <summary>
        /// Bake a single sprite to a pooled slot: black silhouette at 4×, pinned bottom-centre,
        /// then a feet→head alpha ramp multiplied on. Returns false (→ banding fallback) if the
        /// sprite is larger than a slot. The caller owns the surrounding render-target swap.
        /// </summary>
        private bool BakeSprite(GraphicsDevice gd, Texture2D tex, Rectangle src, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            rt = null!;
            feetInRT = default;
            if (tex == null || src.IsEmpty)
                return false;
            float w = src.Width * 4f, h = src.Height * 4f;
            if (w > CasterRtW || h > CasterRtH - 8f)
                return false;

            rt = RentCasterRT(gd);
            var pos = new Vector2((CasterRtW - w) / 2f, CasterRtH - h - 8f);
            feetInRT = new Vector2(CasterRtW / 2f, CasterRtH - 8f);
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);
                _rtBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                _rtBatch.Draw(tex, pos, src, Color.Black, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                _rtBatch.End();

                // Fade only the sprite's vertical extent (full at the feet, faint at the head).
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex!, new Rectangle(0, (int)pos.Y, CasterRtW, (int)h), Color.White);
                _rtBatch.End();
                return true;
            }
            catch
            {
                try { _rtBatch!.End(); } catch { }
                return false;
            }
        }

        /// <summary>Lease the next pooled caster target for this frame (grows the pool on demand).</summary>
        private RenderTarget2D RentCasterRT(GraphicsDevice gd)
        {
            if (_casterUsed < _casterPool.Count)
                return _casterPool[_casterUsed++];
            var rt = new RenderTarget2D(gd, CasterRtW, CasterRtH);
            _casterPool.Add(rt);
            _casterUsed++;
            return rt;
        }

        /// <summary>
        /// Bake each visible building's silhouette SHEARED about its base into a pooled target, so
        /// the composite is a plain draw and the shadow lies flat beside the building (no roof-over-
        /// self). Shear = horizontal lean by height, squash = flatten to the ground; both from the
        /// sun angle. Runs during RenderingWorld where render-target swaps + a matrix batch are safe.
        /// </summary>
        private void BakeBuildings(GraphicsDevice gd, GameLocation loc, ModConfig config)
        {
            if (loc == null || loc.buildings.Count == 0)
                return;
            float d = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            // A building silhouette projected UPWARD always overlaps the building (that's where the
            // building is). A cast shadow falls onto the GROUND — down-and-to-the-side toward the
            // camera. So map each row's height above the base to a DOWNWARD + sideways offset:
            //   horizontal (lean) = shearX·height   (sign follows the sun, morning→left like chars)
            //   vertical  (toward camera) = downSquash·height  (fullness / how far it lies out)
            float shearX = MathHelper.Clamp(0.6f * d * Math.Max(0.2f, config.HeightShadowLength), -0.9f, 0.9f);
            const float downSquash = 0.55f;

            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 16, tx1 = (vp.X + vp.Width) / 64 + 4;
            int ty0 = vp.Y / 64 - 16, ty1 = (vp.Y + vp.Height) / 64 + 4;

            RenderTargetBinding[] prev = gd.GetRenderTargets();
            try
            {
                foreach (Building bld in loc.buildings)
                {
                    if (bld == null || bld.tileX.Value > tx1 || bld.tileX.Value + bld.tilesWide.Value < tx0
                        || bld.tileY.Value > ty1 || bld.tileY.Value + bld.tilesHigh.Value < ty0)
                        continue;
                    if (BakeBuilding(gd, bld, shearX, downSquash, out RenderTarget2D rt, out Vector2 feet))
                        _bldMap[bld] = (rt, feet);
                }
            }
            catch (Exception ex)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] building bake threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                gd.SetRenderTargets(prev);
            }
        }

        /// <summary>Bake one building's black silhouette projected DOWN onto the ground (base pinned,
        /// rows above the base pushed down+sideways) so it lies in front of the building, never over
        /// it. Returns false (→ contact-pool fallback) when it wouldn't fit a slot.</summary>
        private bool BakeBuilding(GraphicsDevice gd, Building bld, float shearX, float downSquash, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            rt = null!;
            feetInRT = default;
            Texture2D? tex = bld.texture?.Value;
            if (tex == null)
                return false;
            Rectangle src = bld.getSourceRect();
            float hpx = src.Height * 4f * downSquash;          // how far the shadow reaches toward camera
            if (src.IsEmpty || src.Width * 4f > BldRtW || hpx > BldRtH - 32f)
                return false;

            rt = RentBldRT(gd);
            var feet = new Vector2(BldRtW / 2f, 24f);          // base-centre near the slot TOP; shadow drops below
            feetInRT = feet;
            // About the feet: x' = x − shearX·(y−feetY), y' = feetY − downSquash·(y−feetY). A row at
            // height h above the base (y−feetY = −h) lands at (+shearX·h sideways, +downSquash·h down)
            // — i.e. projected down and out onto the ground, so a tall building never covers itself.
            Matrix shear = Matrix.CreateTranslation(-feet.X, -feet.Y, 0f)
                         * new Matrix(1f, 0f, 0f, 0f, -shearX, -downSquash, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f)
                         * Matrix.CreateTranslation(feet.X, feet.Y, 0f);
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);
                // CullNone is REQUIRED: downSquash makes the matrix determinant negative (a vertical
                // flip), which reverses triangle winding — the default cull would drop every pixel.
                _rtBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, shear);
                _rtBatch.Draw(tex, feet, src, Color.Black, 0f, new Vector2(src.Width / 2f, src.Height), 4f, SpriteEffects.None, 0f);
                _rtBatch.End();

                // Fade base(full, at feet)→tip(faint, further down). The shadow region is [feetY, feetY+hpx];
                // the gradient is full at its bottom, so flip it vertically to keep full at the feet end.
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_bldGradTex!, new Rectangle(0, (int)feet.Y, BldRtW, (int)hpx), null, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                _rtBatch.End();

                if (Diag != null && !_bldDumped && src.Width > 40)
                {
                    _bldDumped = true;
                    try
                    {
                        string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "radiance_bld.png");
                        using var fs = System.IO.File.Create(p);
                        rt.SaveAsPng(fs, BldRtW, BldRtH);
                        Diag.Log($"[shadow] bld dump {p} | src={src.Width}x{src.Height} shearX={shearX:0.00} hpx={hpx:0} feet={feet}", LogLevel.Info);
                    }
                    catch (Exception dex) { Diag.Log($"[shadow] bld dump failed: {dex.Message}", LogLevel.Warn); }
                }
                return true;
            }
            catch
            {
                try { _rtBatch!.End(); } catch { }
                return false;
            }
        }

        private RenderTarget2D RentBldRT(GraphicsDevice gd)
        {
            if (_bldUsed < _bldPool.Count)
                return _bldPool[_bldUsed++];
            var rt = new RenderTarget2D(gd, BldRtW, BldRtH);
            _bldPool.Add(rt);
            _bldUsed++;
            return rt;
        }

        /// <summary>A 64×64 soft radial disc (white, radial alpha) for ambient contact pools.</summary>
        private static Texture2D BuildBlob(GraphicsDevice gd)
        {
            const int N = 64;
            var tex = new Texture2D(gd, N, N);
            var data = new Color[N * N];
            float r = N / 2f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    float a = MathHelper.Clamp(1f - dist, 0f, 1f);
                    a *= a;   // soft falloff toward the rim
                    data[y * N + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(a * 255f));
                }
            }
            tex.SetData(data);
            return tex;
        }

        /// <summary>1×H alpha ramp: 1.0 at the bottom (feet) fading to <paramref name="headFade"/> at the top (far tip).</summary>
        private static Texture2D BuildGradient(GraphicsDevice gd, float headFade = HeadFade)
        {
            var tex = new Texture2D(gd, 1, PlayerRtH);
            var data = new Color[PlayerRtH];
            for (int y = 0; y < PlayerRtH; y++)
            {
                float tBottom = (float)y / (PlayerRtH - 1);      // 0 at top, 1 at bottom
                // Non-linear: stays dark near the feet, fades toward the far tip.
                float a = headFade + (1f - headFade) * (float)Math.Pow(tBottom, 1.8);
                data[y] = new Color(255, 255, 255, (int)(a * 255f));
            }
            tex.SetData(data);
            return tex;
        }

        // Discs of offset taps → cheap soft edge. Weighted so overlapping translucent copies
        // reach the target opacity at the core while feathering the rim. The player (one RT
        // draw) can afford 9 taps; NPC bands use the lighter 5 to keep the draw count sane.
        private static readonly Vector2[] Taps9 =
        {
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
            new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f),
        };
        private static readonly Vector2[] Taps5 =
        {
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        };

        private static void DrawSoft(SpriteBatch b, Vector2[] taps, Texture2D tex, Rectangle? src, Vector2 pos,
            Color baseColor, float alpha, float rot, Vector2 origin, Vector2 scale, float depth,
            SpriteEffects effects, float blur)
        {
            // No blur → one draw at full alpha (the tap disc would just stack N identical
            // copies on the same pixel, costing N× the draw calls for nothing).
            if (blur <= 0f)
            {
                b.Draw(tex, pos, src, baseColor * MathHelper.Clamp(alpha, 0f, 1f), rot, origin, scale, effects, depth);
                return;
            }

            // Per-tap alpha so 1-(1-a)^N ≈ target alpha at the fully-covered core.
            float a = 1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), 1f / taps.Length);
            Color c = baseColor * a;
            foreach (Vector2 t in taps)
                b.Draw(tex, pos + t * blur, src, c, rot, origin, scale, effects, depth);
        }

        /// <summary>Number of horizontal bands used to fake the NPC opacity gradient.</summary>
        private const int NpcBands = 7;

        /// <summary>
        /// Draw a single-texture sprite as a shadow with a feet→head opacity gradient, by
        /// slicing it into horizontal bands (each drawn about the shared feet anchor so they
        /// stay aligned under rotation + stretch) and fading each band's alpha toward the tip.
        /// </summary>
        private void DrawBandedGradient(SpriteBatch b, Texture2D tex, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, Vector2 scale, float depth, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None)
        {
            // Band count set by the sprite's SOURCE height (it's drawn ~4× on screen, so a short
            // stump at height/6 showed coarse steps). Finer division → the per-band alpha gradient
            // reads as a smooth ramp, not layers. Capped so tall sprites don't explode the draw count.
            int bands = (int)MathHelper.Clamp(src.Height / 2f, 12f, 28f);
            for (int i = 0; i < bands; i++)
            {
                int y0 = src.Height * i / bands;
                int y1 = src.Height * (i + 1) / bands;
                var band = new Rectangle(src.X, src.Y + y0, src.Width, y1 - y0);
                // Origin so the (virtual) full-sprite ground-anchor row still maps to the feet position.
                var origin = new Vector2(baseOrigin.X, baseOrigin.Y - y0);
                float tBottom = (i + 0.5f) / bands;              // 0 at the head band, 1 at the feet band
                float ga = headFade + (1f - headFade) * (float)Math.Pow(tBottom, 1.8);
                DrawSoft(b, Taps5, tex, band, feet, Color.Black, alpha * ga, rot, origin, scale, depth,
                    effects, blur);
            }
        }

        /// <summary>
        /// How far under the caster (in sort depth) the shadow sits. The farmer draws many
        /// sub-layers spanning a small depth range, so this must clear that whole range to keep
        /// the shadow strictly BEHIND the sprite (else it shows over opaque body pixels).
        /// </summary>
        private const float ShadowDepthBias = 1.2e-3f;

        /// <summary>
        /// True if the caster stands on open water (avoid laying a shadow on the surface).
        /// Pier/bridge decks sit on Buildings-layer tiles OVER water tiles — standing on a
        /// deck is not standing on water, so require the tile to have no Buildings tile.
        /// </summary>
        private static bool OnWater(GameLocation loc, Point tile)
        {
            try
            {
                // Height Framework (if installed) already distinguishes open water from pier/bridge
                // DECKS over water, so its water-surface test is the robust answer. Fall back to the
                // isWaterTile + no-Buildings-tile heuristic (which approximates the same deck check).
                if (Height != null)
                    return Height.IsWaterSurface(loc, tile.X, tile.Y);
                return loc.isWaterTile(tile.X, tile.Y)
                    && !loc.hasTileAt(tile.X, tile.Y, "Buildings");
            }
            catch { return false; }
        }

        /// <summary>Sun angle → shadow lean (radians), length stretch, and base opacity.</summary>
        private static void ComputeSun(out float rot, out float stretch, out float alpha)
        {
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float d = MathHelper.Clamp((Game1.timeOfDay - 1200) / 600f, -1f, 1f);
            // Lean more sideways (was 0.8) so the shadow lies to the side of the body instead of
            // straight up over it — reduces the "shadow on the sprite" overlap while staying
            // upright (not the rejected upside-down flip).
            rot = 1.15f * d;                                     // <0 morning lean-left, >0 evening lean-right
            stretch = MathHelper.Lerp(0.3f, 1.2f, Math.Abs(d));  // stretched LONG when the sun is low
            alpha = 0.9f * TimeFade();                           // opacity at the feet (× strength; fades toward the tip)
        }

        /// <summary>Ease the shadow out toward dusk so it doesn't pop. The fade window follows
        /// the game's SEASONAL dark times (starting-to-get-dark → truly-dark), so summer keeps
        /// long evening shadows while winter fades early. No dawn ramp — the day starts at
        /// 06:00 with the player active immediately.</summary>
        private static float TimeFade()
        {
            int t = Game1.timeOfDay;
            int mins = (t / 100) * 60 + (t % 100);
            int startDark = 1800, trulyDark = 2000;
            try
            {
                GameLocation? loc = Game1.currentLocation;
                if (loc != null)
                {
                    startDark = Game1.getStartingToGetDarkTime(loc);
                    trulyDark = Game1.getTrulyDarkTime(loc);
                }
            }
            catch { }
            int m0 = (startDark / 100) * 60 + startDark % 100;
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            if (mins <= m0) return 1f;
            return MathHelper.Clamp((m1 - mins) / (float)Math.Max(1, m1 - m0), 0f, 1f);
        }
    }
}
