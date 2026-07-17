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
        private Vector2 _playerFeetInRT;
        private bool _playerReady;
        private const int PlayerRtW = 96;
        private const int PlayerRtH = 176;
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

        /// <summary>Sun conditions: outdoors, daytime, clear weather → one long sun-cast shadow.</summary>
        private static bool SunCasts()
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !loc.IsOutdoors)
                return false;
            return Game1.timeOfDay < 1900 && Game1.timeOfDay >= 600 && !Game1.isRaining && !Game1.isSnowing;
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

            foreach (FarmAnimal a in loc.animals.Values)
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

            foreach (FarmAnimal animal in loc.animals.Values)
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
                        DrawBushShadow(b, bush, rot, stretch, alpha, blur);
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
                    DrawBushShadow(b, bush, rot, stretch, alpha, blur);
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

            // Building shadows via the sprite-lean path stay DISABLED (leaning a whole-building
            // sprite projects it up over itself). Their real ground projection is done separately
            // in DrawHeightShadows using Height Framework data — see DrawSunShadows.
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
            DrawBandedGradient(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade);
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
            DrawBandedGradient(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade);
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
            DrawBandedGradient(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
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
            SpriteEffects fx = crop.flip.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            DrawBandedGradient(b, tex, crop.sourceRect, feet, CropOrigin,
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade, fx);
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
            DrawBandedGradient(b, tex, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
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
            // src.Height*4; the stump/boulder visually rests a bit above that, so lift the anchor.
            var worldFeet = new Vector2(tile.X * 64f + src.Width * 2f, tile.Y * 64f + src.Height * 4f - 40f);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, worldFeet);
            var baseOrigin = new Vector2(src.Width / 2f, src.Height);
            float depth = MathHelper.Clamp((tile.Y + 1f) * 64f / 10000f + tile.X / 100000f - ShadowDepthBias, 0f, 1f);
            DrawBandedGradient(b, tex, src, feet, baseOrigin, alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade);
        }

        private void DrawTreeShadow(SpriteBatch b, Tree tree, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = Tree.treeTopSourceRect;                 // (0,0,48,96) standard canopy
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f));
            float depth = MathHelper.Clamp((tree.getBoundingBox().Bottom + 2f) / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            // Tree canopy draws with origin (24, 96); shear/fade about the trunk base.
            DrawBandedGradient(b, tree.texture.Value, src, feet, new Vector2(24f, 96f),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade);
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
            DrawBandedGradient(b, ft.texture, src, feet, new Vector2(24f, 80f),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade);
        }

        private void DrawBushShadow(SpriteBatch b, Bush bush, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = bush.sourceRect.Value;
            if (src.IsEmpty)
                return;
            Vector2 tile = bush.Tile;
            // Bush.draw pins the sprite (origin y=32) at (tile.Y+1)*64, MINUS 64 for larger bushes
            // — matching that -64 keeps big town bushes' shadow at the base instead of a tile below.
            int sz = bush.size.Value;
            float yOff = (sz > 0 && (!bush.townBush.Value || sz != 1) && sz != 4) ? 64f : 0f;
            var worldFeet = new Vector2(tile.X * 64f + src.Width * 2f, (tile.Y + 1) * 64f - yOff);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, worldFeet);
            var baseOrigin = new Vector2(src.Width / 2f, 32f);
            float depth = MathHelper.Clamp((bush.getBoundingBox().Center.Y + 48f) / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            DrawBandedGradient(b, Bush.texture.Value, src, feet, baseOrigin,
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur, ObjectHeadFade);
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
            _bakedMap.Clear();
            _bldMap.Clear();
            _casterUsed = 0;
            _bldUsed = 0;
            if (!ShouldCast(config))
                return;

            _rtBatch ??= new SpriteBatch(gd);
            _gradTex ??= BuildGradient(gd);
            _bldGradTex ??= BuildGradient(gd, 0.35f);
            _blobTex ??= BuildBlob(gd);

            // Buildings get a SHEARED silhouette baked here (sun path only) so they cast a real
            // shape-accurate shadow that lies beside the footprint instead of a grounding pool.
            if (SunCasts() && config.DirectionalShadowObjects)
                BakeBuildings(gd, Game1.currentLocation, config);

            // Bake NPC + animal silhouettes (single-sprite casters) to pooled targets so their
            // shadows match the player's smooth, cohesive fade instead of stepped bands.
            BakeCasters(gd, Game1.currentLocation);

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
                foreach (FarmAnimal a in loc.animals.Values)
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
            // More bands for taller sprites so the gradient stays smooth (a 96px tree would
            // show hard steps with only a handful of bands); fewer for small NPC sprites.
            int bands = (int)MathHelper.Clamp(src.Height / 6f, 6f, 18f);
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

        /// <summary>Ease the shadow in/out near dawn (06:00–07:00) and dusk (18:00–19:00) so it doesn't pop.</summary>
        private static float TimeFade()
        {
            // The day starts at 06:00 with the player active immediately, so the shadow is full
            // from the start (no dawn ramp — that left the morning shadowless until ~07:00).
            // Only ease OUT toward dusk. Minutes so the fade is smooth across the hour rollover.
            int t = Game1.timeOfDay;
            int mins = (t / 100) * 60 + (t % 100);
            if (mins >= 1080) return MathHelper.Clamp((1140 - mins) / 60f, 0f, 1f); // 18:00 → 19:00
            return 1f;
        }
    }
}
