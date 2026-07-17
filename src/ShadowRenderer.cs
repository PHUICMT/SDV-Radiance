using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
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
        private int _diagFrames;
        private bool _errLogged;

        // The player's silhouette is rendered to this offscreen target during RenderingWorld,
        // then drawn back (flattened + leaned) into the World_Sorted batch. FarmerRenderer only
        // supports a uniform scale, so the RT is the only way to squash the player vertically.
        private RenderTarget2D? _playerRT;
        private SpriteBatch? _rtBatch;
        private Texture2D? _gradTex;
        private Vector2 _playerFeetInRT;
        private bool _playerReady;
        private const int PlayerRtW = 96;
        private const int PlayerRtH = 176;
        /// <summary>Opacity at the far tip (head end) relative to the feet, for the gradient fade.</summary>
        private const float HeadFade = 0.05f;

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
            if (!ShouldCast(config))
                return false;
            if (SunCasts())
                return true;
            var lights = Game1.currentLightSources;
            return lights != null && lights.Count > 0;
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
                DrawObjectShadows(b, loc, rot, stretch, alpha, blur);
        }

        private void DrawAnimalShadow(SpriteBatch b, FarmAnimal a, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = a.Sprite.SourceRect;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(a.Position.X + a.Sprite.SpriteWidth * 4 / 2f, a.GetBoundingBox().Bottom));
            float depth = MathHelper.Clamp(a.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
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
            var lights = Game1.currentLightSources;
            if (lights == null || lights.Count == 0)
                return;

            _lightBuf.Clear();
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

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] light path: lights on-screen={_lightBuf.Count} (of {lights.Count} total)", LogLevel.Debug);
            }

            if (_lightBuf.Count == 0)
                return;

            float lenCfg = Math.Max(0.1f, config.DirectionalShadowLength);

            foreach (NPC npc in loc.characters)
            {
                if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnWater(loc, npc.TilePoint))   // same guard as the sun path (bathhouse, night beach)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom));
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
                        new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom));
                    float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                    foreach (var (lpos, reach) in _lightBuf)
                        if (LightCast(feet, lpos, reach, strength, lenCfg, out float rot, out float st, out float a))
                            DrawSoft(b, Taps9, _playerRT, null, feet, Color.White, a, rot, _playerFeetInRT,
                                new Vector2(1f, st), depth, SpriteEffects.None, blur);
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
            Rectangle src = npc.Sprite.SourceRect;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom));
            float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            // NPCs are single-texture sprites, so the feet→head opacity gradient is faked with
            // horizontal bands (no per-NPC render target needed), each softened at the edges.
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
                        DrawTreeShadow(b, tree, tile, rot * TallLeanScale, stretch, alpha, blur);
                        break;
                    case Bush bush:
                        DrawBushShadow(b, bush, rot, stretch, alpha, blur);
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
                // Only upright machines/craftables cast; flat 16x16 floor items don't.
                if (o == null || !o.bigCraftable.Value || o.Fragility == 2 || o.isTemporarilyInvisible)
                    continue;
                // Damp the lean (like tall sprites) so a craftable against a wall climbs it less,
                // and cap the length so a small keg/machine's shadow stays near its own footprint
                // instead of stretching a full character-length away.
                DrawBigCraftableShadow(b, o, tile, rot * TallLeanScale, Math.Min(stretch, 0.55f), alpha, blur);
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

            // Building shadows are DISABLED: leaning a whole-building sprite projects it up over
            // itself and its neighbours (a building's real shadow is a ground projection from its
            // footprint on the sun-opposite side — exactly what the height framework will provide).
        }

        /// <summary>Lean damping for tall sprites (trees/bushes/craftables) so the shadow stays rooted at the base.</summary>
        private const float TallLeanScale = 0.6f;
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
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom));
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
            // Clump draws top-left at tile*64, origin zero, scale 4 → base = bottom-centre of the sprite.
            var worldFeet = new Vector2(tile.X * 64f + src.Width * 2f, tile.Y * 64f + src.Height * 4f);
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

        private void DrawBushShadow(SpriteBatch b, Bush bush, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = bush.sourceRect.Value;
            if (src.IsEmpty)
                return;
            Vector2 tile = bush.Tile;
            // Bush.draw position = tile*64 + (size+1)*64/2 == tile*64 + src.Width*2; origin (width/2, 32).
            var worldFeet = new Vector2(tile.X * 64f + src.Width * 2f, (tile.Y + 1) * 64f);
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
                new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom));
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
            if (!ShouldCast(config))
                return;
            Farmer who = Game1.player;
            if (who == null || who.currentLocation != Game1.currentLocation
                || who.swimming.Value || who.isRidingHorse() || who.IsSitting())
                return;

            _playerRT ??= new RenderTarget2D(gd, PlayerRtW, PlayerRtH);
            _rtBatch ??= new SpriteBatch(gd);

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

        /// <summary>1×H alpha ramp: 1.0 at the bottom (feet) fading to <see cref="HeadFade"/> at the top (far tip).</summary>
        private static Texture2D BuildGradient(GraphicsDevice gd)
        {
            var tex = new Texture2D(gd, 1, PlayerRtH);
            var data = new Color[PlayerRtH];
            for (int y = 0; y < PlayerRtH; y++)
            {
                float tBottom = (float)y / (PlayerRtH - 1);      // 0 at top, 1 at bottom
                // Non-linear: stays dark near the feet, fades fast toward the far tip.
                float a = HeadFade + (1f - HeadFade) * (float)Math.Pow(tBottom, 1.8);
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
            float headFade = HeadFade)
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
                    SpriteEffects.None, blur);
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
            // Convert HHMM to real minutes so the fade is smooth across the :50→:00 hour rollover
            // (raw timeOfDay/100 jumped, e.g. 06:50→07:00 = 0.5→1.0, a sudden darkening).
            int t = Game1.timeOfDay;
            int mins = (t / 100) * 60 + (t % 100);
            if (mins < 420) return MathHelper.Clamp((mins - 360) / 60f, 0f, 1f);   // 06:00 → 07:00
            if (mins >= 1080) return MathHelper.Clamp((1140 - mins) / 60f, 0f, 1f); // 18:00 → 19:00
            return 1f;
        }
    }
}
