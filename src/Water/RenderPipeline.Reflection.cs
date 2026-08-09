using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - ENTITY REFLECTION RT (P3b of the water-V4 rework).
    ///
    /// The mirror used to be a pure screen-space flip: whatever pixels happened to sit
    /// above a water pixel got mirrored into it. That reflects the WRONG thing whenever
    /// the true reflection source is off-screen, hidden behind something, or is an
    /// entity whose feet are not exactly on the waterline. This target holds the part
    /// we can build correctly by construction: every entity drawn UPSIDE-DOWN anchored
    /// at its own ground contact. A sprite's reflection hangs exactly below its feet in
    /// world space, so the shader just samples this RT at the CURRENT pixel — no
    /// waterline math, no self-hits, no hidden-surface errors, and an entity standing
    /// above the screen edge still lands its visible reflection inside the RT.
    ///
    /// Geometry mirrors BakeWaterSpriteMask tile-for-tile (same anchors, same culling);
    /// the player comes from ShadowRenderer.PlayerColor (the full-colour twin of the
    /// silhouette bake), so appearance mods reflect whatever they actually drew.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        private RenderTarget2D? _reflectionRenderTarget;
        internal bool ReflectRTReady;
        internal bool ReflectRTHasPlayer;   // player stamped this frame → the shader retires
                                            // its wading-silhouette fallback

        /// <summary>Bake the flipped-entity reflection layer for this frame. Called from
        /// Display.RenderingWorld right after the sprite mask bake (the only safe spot
        /// for render-target swaps).</summary>
        public void BakeWaterReflection()
        {
            long t0 = FrameCost.Begin();
            BakeWaterReflectionCore();
            double ms = FrameCost.End(FrameCost.Part.EntityReflection, t0);
            if (_timingOn) AccumulateBuildMilliseconds(5, ms);
        }

        /// <summary>
        /// Stamp one farmer's colour bake into the mirror, flipped below their feet. The bake pins
        /// the feet at (RtW/2, RtH-8), so flipped that anchor is 8px from the TOP; the sprite is
        /// positioned so the flipped feet meet it. Sliced into 16-row bands to get the same
        /// feet-to-head fade every other body in here is given.
        /// </summary>
        private void StampFarmerBake(SpriteBatch spriteBatch, Texture2D bake, Farmer who)
        {
            Rectangle box = who.GetBoundingBox();
            float feetY = box.Bottom - 10f + who.yOffset;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, feetY));
            float depth = StampDepth(feetY);
            const int bandHeight = 16;
            int bands = ShadowRenderer.PlayerRtH / bandHeight;
            for (int i = 0; i < bands; i++)
            {
                var srcR = new Rectangle(0, ShadowRenderer.PlayerRtH - (i + 1) * bandHeight,
                    ShadowRenderer.PlayerRtW, bandHeight);
                float a = MathHelper.Lerp(1f, ReflHeadFade, (i + 0.5f) / bands);
                spriteBatch.Draw(bake, feet + new Vector2(-ShadowRenderer.PlayerRtW / 2f, (i * bandHeight - 8f) * MirrorSquash),
                    srcR, Color.White * a, 0f, Vector2.Zero, new Vector2(1f, MirrorSquash),
                    SpriteEffects.FlipVertically, depth);
            }
        }

        private void BakeWaterReflectionCore()
        {
            ReflectRTReady = false;
            ReflectRTHasPlayer = false;
            GameLocation? location = Game1.currentLocation;
            if (location == null || !_hasWaterInMask || Game1.game1.takingMapScreenshot)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_reflectionRenderTarget == null || _reflectionRenderTarget.Width != w || _reflectionRenderTarget.Height != h)
            {
                _reflectionRenderTarget?.Dispose();
                _reflectionRenderTarget = new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _spriteMaskSpriteBatch ??= new SpriteBatch(_device);

            try
            {
                _device.SetRenderTarget(_reflectionRenderTarget);
                _device.Clear(Color.Transparent);
                var spriteBatch = _spriteMaskSpriteBatch;
                // BackToFront + per-stamp depth from the caster's TRUE feet row: whoever
                // stands in front (bigger feet Y) draws last and wins the overlap — a
                // fixed draw order let a tree's reflection cover the player standing in
                // front of it.
                spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.AlphaBlend, SamplerState.PointClamp);

                // Player — the colour bake, flipped below the feet. Swimming is skipped:
                // half the body is underwater, a full mirrored silhouette reads as a glitch.
                var who = Game1.player;
                var pcol = ShadowRenderer.PlayerColor;
                if (who != null && pcol != null && !who.swimming.Value)
                {
                    StampFarmerBake(spriteBatch, pcol, who);
                    ReflectRTHasPlayer = true;
                }

                // The other players, from their own colour bakes, through the same stamp. House
                // rule: a body is a body and only the image differs. Nothing here read them before,
                // so in co-op every farmer but you stood over water with no reflection at all.
                // ReflectRTHasPlayer stays about the LOCAL player: it retires the shader's wading
                // fallback, which is drawn for you and nobody else.
                foreach (var other in ShadowRenderer.OtherFarmerImages)
                {
                    if (other.Colour != null)
                        StampFarmerBake(spriteBatch, other.Colour, other.Who);
                }

                // NPCs + monsters, bottom-centre at the collision-box feet (same anchor the
                // game and the sprite mask use), flipped to hang downward.
                // Whoever the game is drawing — during a cutscene that is the event's cast, NOT the
                // residents (see ShadowRenderer.CharactersIn). Mirroring both lists reflected people
                // who were not on screen.
                // Only bodies whose mirror can land on water: the image hangs DOWNWARD from the
                // feet, so the search reaches below them. On a map with water in one corner this
                // skips a screenful of stamps per frame (same gate the sprite mask uses).
                foreach (NPC c in ShadowRenderer.CharactersIn(location))
                {
                    if (c?.Sprite?.Texture == null || c.IsInvisible || c.swimming.Value)
                        continue;
                    Rectangle cbb = c.GetBoundingBox();
                    if (!WaterWithinTiles(cbb.Center.X / 64, cbb.Bottom / 64 + 2, 4))
                        continue;
                    // Where the game REALLY draws this frame (NPC.draw: anchor at position +
                    // bbHeight/2 + drawOffset, origin at 3/4 of the frame height, scale 4):
                    float drawnTop = c.Position.Y + cbb.Height / 2f + c.drawOffset.Y + c.yJumpOffset
                        - 3f * c.Sprite.SpriteHeight;
                    float drawnBottom = drawnTop + 4f * c.Sprite.SpriteHeight;
                    // The FEET in the art are the bottom of the standard 32-row body block at
                    // the TOP of the frame. Verified against the winter derby actors (16x64
                    // frames, drawOffset 96: the body fills the first 32 rows, the rod and line
                    // over the water fill the rest): this one rule lands on bb.Bottom for a
                    // standard frame, on bb.Bottom + drawOffset for a seated one, and on the
                    // true boot row for the tall festival frames - where bb-based anchoring
                    // sat 1.5 tiles low ("the reflection starts at the rod tip") and a
                    // bystander's far tail painted a disembodied head into the water.
                    float feetWorld = drawnTop + 4f * Math.Min(c.Sprite.SpriteHeight, 32);
                    int belowFeet = Math.Max(0, (int)Math.Round((drawnBottom - feetWorld) / 4f));
                    StampFlippedAt(spriteBatch, c.Sprite.Texture, c.Sprite.SourceRect,
                        cbb.Center.X + c.drawOffset.X, feetWorld - 10f, belowFeet);
                }
                // Farm animals.
                foreach (var a in location.animals.Values)
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    Rectangle abb = a.GetBoundingBox();
                    if (!WaterWithinTiles(abb.Center.X / 64, abb.Bottom / 64 + 2, 4))
                        continue;
                    StampFlipped(spriteBatch, a.Sprite.Texture, a.Sprite.SourceRect, abb);
                }
                // Critters: bottom edge at position.Y, centred on position.X (Critter.draw).
                if (location.critters != null)
                {
                    foreach (var cr in location.critters)
                    {
                        if (cr?.sprite?.Texture == null)
                            continue;
                        // Same stamp every body uses (one anchor rule, the same feet->head
                        // fade): a butterfly's reflection was drawn at full opacity by its own
                        // code path while every body faded, so it read as a sticker.
                        if (!WaterWithinTiles((int)(cr.position.X / 64f), (int)(cr.position.Y / 64f) + 2, 4))
                            continue;
                        Rectangle crs = cr.sprite.SourceRect;
                        var crBox = new Rectangle((int)cr.position.X - crs.Width * 2,
                            (int)cr.position.Y - crs.Height * 4, crs.Width * 4, crs.Height * 4);
                        StampFlipped(spriteBatch, cr.sprite.Texture, crs, crBox);
                    }
                }

                // Trees / fruit trees / bushes: sprites, not map art — the scenery re-render
                // (P3c) can't see them, so their reflections are built here, flipped around
                // the trunk/stem base. Same tile-walk culling as the sprite mask.
                var viewport = Game1.viewport;
                var tfDict = location.terrainFeatures;
                int ctx0 = (int)Math.Floor((viewport.X - 256) / 64f), ctx1 = (int)Math.Floor((viewport.X + viewport.Width + 256) / 64f);
                int cty0 = (int)Math.Floor((viewport.Y - 512) / 64f), cty1 = (int)Math.Floor((viewport.Y + viewport.Height + 768) / 64f);
                for (int cvY = cty0; cvY <= cty1; cvY++)
                for (int cvX = ctx0; cvX <= ctx1; cvX++)
                {
                    Vector2 tile = new(cvX, cvY);
                    if (!tfDict.TryGetValue(tile, out var tf))
                        continue;
                    // A tree's mirror hangs BELOW its trunk and a crown is six tiles tall, then
                    // stretched by MirrorSquash — so the reach downward has to be the largest of
                    // any stamp here. Centred four tiles under the base with slack on both sides.
                    if (!WaterWithinTiles(cvX, cvY + 4, 7))
                        continue;
                    switch (tf)
                    {
                        // Grown tree: canopy 48×96 with the trunk base at tile*64+(32,64).
                        // Flipped: origin moves to the TOP of the source (24, 0).
                        case StardewValley.TerrainFeatures.Tree tree when tree.growthStage.Value >= 5 && !tree.stump.Value && tree.texture?.Value != null:
                            spriteBatch.Draw(tree.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                StardewValley.TerrainFeatures.Tree.treeTopSourceRect, Color.White, 0f, new Vector2(24f, 0f), 4f,
                                SpriteEffects.FlipVertically | (tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                                StampDepth(tile.Y * 64f + 64f));
                            break;
                        // Mature fruit tree: 48×64 seasonal foliage, base at tile*64+(32,64).
                        case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4 && !ft.stump.Value && ft.texture != null:
                            int season = Game1.GetSeasonIndexForLocation(ft.Location);
                            var fsrc = new Rectangle((12 + season * 3) * 16, ft.GetSpriteRowNumber() * 5 * 16, 48, 64);
                            spriteBatch.Draw(ft.texture,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                fsrc, Color.White, 0f, new Vector2(24f, fsrc.Height - 80f), 4f,
                                SpriteEffects.FlipVertically | (ft.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                                StampDepth(tile.Y * 64f + 64f));
                            break;
                        // Bush: bottom-centre at (tile.X*64 + (eff+1)*32, (tile.Y+1)*64).
                        case StardewValley.TerrainFeatures.Bush bush when !bush.sourceRect.Value.IsEmpty:
                            var bsrc = bush.sourceRect.Value;
                            int eff = bush.size.Value switch { 3 => 0, 4 => 1, _ => bush.size.Value };
                            spriteBatch.Draw(StardewValley.TerrainFeatures.Bush.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + (eff + 1) * 32f, (tile.Y + 1) * 64f)),
                                bsrc, Color.White, 0f, new Vector2(bsrc.Width / 2f, 0f), 4f,
                                SpriteEffects.FlipVertically | (bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                                StampDepth((tile.Y + 1) * 64f));
                            break;
                    }
                }

                spriteBatch.End();
                ReflectRTReady = true;
            }
            finally
            {
                _device.SetRenderTargets(prev);
            }
        }

        // The bank-edge / bridge anchor experiments (waterline glide, hang-from-edge,
        // mirror stacking) are all retired by eye-review: every variant traded one
        // artifact for another, and the keeper is the pure feet anchor — visibility
        // comes only from which pixels are water. Do not reintroduce distance rules
        // here; see the reflection-anchor-decision note in the project memory.

        /// <summary>Vertical STRETCH on entity reflections. The anchor never moves — what
        /// changes is how far the mirrored body reaches past the bank it stands on. A
        /// squash (0.8) was tried first and read as "shorter, even less of us": pulling
        /// the body up buries more of it in the bank. Stretching sends it deeper, so the
        /// part that clears the bank and lands on water is bigger — asked for in exactly
        /// those words ("only the tip of the head shows"): at 1.0 a bank strip swallowed all but the head.
        /// 1.25 matches the screen mirror's own depth factor, so a body and the scenery
        /// behind it foreshorten at the same rate.</summary>
        private const float MirrorSquash = 1.25f;

        /// <summary>Opacity at the reflection's deepest end (the head). Full at the feet,
        /// fading with depth — real water does this, and it retires the "floating scrap"
        /// artifact: a body standing a couple of tiles back from the water used to keep
        /// only its clipped deep half, a detached blob drifting below an NPC on the tide
        /// line. Faded to ~this, that scrap all but disappears on its own, while a body
        /// at the edge keeps a strong reflection near the feet. Chosen over a gap-cut
        /// rule (per-column land detection the shader can't see) by the author.</summary>
        private const float ReflHeadFade = 0.32f;   // 0.18 + the shader-side cut stacked too faint

        /// <summary>Flipped twin of StampSprite: bottom-centre anchor becomes top-centre,
        /// the sprite hangs downward from the feet, squashed like the scenery mirror —
        /// drawn in 4-source-row slices so the opacity can fall feet→head (see
        /// <see cref="ReflHeadFade"/>); one draw per slice, same depth, no overlap.</summary>
        private void StampFlipped(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Rectangle bb, Vector2 drawOffset = default)
        {
            // The SAME feet rule the player's stamp uses: the 10 px lift (a collision box sits a
            // little below the drawn shoes) and the sprite's own draw offset. Without them an NPC
            // mirrored 10 px lower than the player standing beside it, and a seated one mirrored
            // where it was not drawn. House rule: an NPC and the player get identical treatment.
            StampFlippedAt(spriteBatch, texture, src, bb.Center.X + drawOffset.X, bb.Bottom - 10f + drawOffset.Y, 0);
        }

        /// <summary>Core of the flipped stamp: explicit feet anchor, plus how many source rows
        /// at the frame's bottom sit BELOW the feet (tall festival frames) and stay out of the
        /// mirror - the flip axis is the feet, those rows live under it.</summary>
        private void StampFlippedAt(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, float centerX, float feetY, int belowFeetRows)
        {
            if (belowFeetRows > 0)
                src.Height = Math.Max(1, src.Height - belowFeetRows);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(centerX, feetY));
            float depth = StampDepth(feetY);
            var origin = new Vector2(src.Width / 2f, 0f);
            var scale = new Vector2(4f, 4f * MirrorSquash);
            const int hs = 4;                              // source rows per slice
            int n = (src.Height + hs - 1) / hs;
            for (int i = 0; i < n; i++)
            {
                int rows = Math.Min(hs, src.Height - i * hs);
                // Full flip shows src's BOTTOM row at the feet, so slice i (downward from the
                // feet) reads the i-th band counted from the sprite's bottom, itself flipped.
                var srcR = new Rectangle(src.X, src.Y + src.Height - i * hs - rows, src.Width, rows);
                float a = MathHelper.Lerp(1f, ReflHeadFade, (i + 0.5f) / n);
                spriteBatch.Draw(texture, feet + new Vector2(0f, i * hs * scale.Y), srcR, Color.White * a,
                    0f, origin, scale, SpriteEffects.FlipVertically, depth);
            }
        }

        /// <summary>BackToFront layer depth from the caster's TRUE feet row: bigger feet Y
        /// = closer to the camera = drawn later = wins reflection overlaps.</summary>
        private static float StampDepth(float feetWorldY) =>
            MathHelper.Clamp(1f - feetWorldY / 65536f, 0.001f, 1f);

        // ---- P3c-lite: clean scenery source for the screen-space mirror ----

        private RenderTarget2D? _mirrorSourceRenderTarget;
        internal bool SceneRTReady;

        /// <summary>Re-render the map's own layers (Back/Buildings/Front families, numbered
        /// variants included — DR issue #48) into a sprite-free source for the mirror.
        /// Excluding a sprite from the composed screen used to leave a player-shaped SKY
        /// hole in the scenery's reflection; sampling a source that never contained the
        /// sprite shows the true map pixels behind them instead. Same RenderingWorld slot
        /// as the other bakes (render-target swaps are safe there).</summary>
        public void BakeSceneryReflection()
        {
            long t0 = FrameCost.Begin();
            BakeSceneryReflectionCore();
            double ms = FrameCost.End(FrameCost.Part.SceneryReflection, t0);
            if (_timingOn) AccumulateBuildMilliseconds(6, ms);
        }

        // P2 (1.5.0): the xTile layer walk was the single most expensive item in the mod and
        // it ran every frame. The camera only ever translates, so the walk now renders into a
        // world-anchored cache with a guard band, and the per-frame cost is one quad blit from
        // the cache into the screen-aligned mirror source (the shader is untouched). The cache
        // rebuilds on a location/size change, when the camera leaves the guard band, on a
        // pending dump (captures must be same-frame exact), and every few ticks so animated
        // map tiles (waterfall art) keep moving in the mirror - at worst their reflection lags
        // by SceneCacheTtlTicks, invisible in a squashed wavy mirror.
        private RenderTarget2D? _mirrorSceneCache;
        private GameLocation? _sceneCacheLocation;
        private int _sceneCacheAnchorX, _sceneCacheAnchorY;   // world px of the cache's top-left
        private int _sceneCacheBuiltTick = -1;
        private const int SceneCachePadPx = 128;              // 2 tiles of camera drift per side
        private const int SceneCacheTtlTicks = 6;             // animated-tile refresh (~100 ms)

        private void BakeSceneryReflectionCore()
        {
            SceneRTReady = false;
            GameLocation? location = Game1.currentLocation;
            if (location?.map == null || !_hasWaterInMask || Game1.game1.takingMapScreenshot)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_mirrorSourceRenderTarget == null || _mirrorSourceRenderTarget.Width != w || _mirrorSourceRenderTarget.Height != h)
            {
                _mirrorSourceRenderTarget?.Dispose();
                _mirrorSourceRenderTarget = new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _spriteMaskSpriteBatch ??= new SpriteBatch(_device);

            int vpX = Game1.viewport.X, vpY = Game1.viewport.Y;
            int cacheW = w + 2 * SceneCachePadPx, cacheH = h + 2 * SceneCachePadPx;
            bool cacheValid = _mirrorSceneCache != null
                && ReferenceEquals(_sceneCacheLocation, location)
                && _mirrorSceneCache.Width == cacheW && _mirrorSceneCache.Height == cacheH
                && Game1.ticks - _sceneCacheBuiltTick < SceneCacheTtlTicks
                && vpX >= _sceneCacheAnchorX && vpY >= _sceneCacheAnchorY
                && vpX + w <= _sceneCacheAnchorX + cacheW && vpY + h <= _sceneCacheAnchorY + cacheH
                && _pendingDump == null;

            try
            {
                var spriteBatch = _spriteMaskSpriteBatch;
                if (!cacheValid)
                {
                    if (_mirrorSceneCache == null || _mirrorSceneCache.Width != cacheW || _mirrorSceneCache.Height != cacheH)
                    {
                        _mirrorSceneCache?.Dispose();
                        // PreserveContents: the whole point is reading it back on later frames.
                        _mirrorSceneCache = new RenderTarget2D(_device, cacheW, cacheH, false, SurfaceFormat.Color,
                            DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    }
                    _sceneCacheLocation = location;
                    _sceneCacheAnchorX = vpX - SceneCachePadPx;
                    _sceneCacheAnchorY = vpY - SceneCachePadPx;
                    _sceneCacheBuiltTick = Game1.ticks;

                    _device.SetRenderTarget(_mirrorSceneCache);
                    _device.Clear(Color.Black);
                    spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    var dd = Game1.mapDisplayDevice;
                    dd.BeginScene(spriteBatch);
                    // Bottom-up families, same order the game composes them. AlwaysFront is
                    // deliberately out: it is mostly weather + translucent shadow washes.
                    // The main target in this event is WORLD-pixel sized, so the padded
                    // viewport maps 1:1 onto the padded cache.
                    var paddedViewport = new xTile.Dimensions.Rectangle(
                        new xTile.Dimensions.Location(_sceneCacheAnchorX, _sceneCacheAnchorY),
                        new xTile.Dimensions.Size(cacheW, cacheH));
                    foreach (string fam in _sceneLayerFamilies)
                    {
                        foreach (var l in location.map.Layers)
                        {
                            if (MapLayers.BelongsToFamily(l.Id, fam))
                                l.Draw(dd, paddedViewport, xTile.Dimensions.Location.Origin, false, 4);
                        }
                    }
                    dd.EndScene();
                    spriteBatch.End();
                }

                // Screen-aligned mirror source = one quad from the cache, shifted by the
                // camera delta since the cache was anchored.
                _device.SetRenderTarget(_mirrorSourceRenderTarget);
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                spriteBatch.Draw(_mirrorSceneCache, new Vector2(_sceneCacheAnchorX - vpX, _sceneCacheAnchorY - vpY), Color.White);
                spriteBatch.End();
                SceneRTReady = true;
            }
            catch (Exception ex)
            {
                try { _spriteMaskSpriteBatch!.End(); } catch { }
                if (!_sceneErrorLogged) { _sceneErrorLogged = true; _monitor.Log($"[water] scenery source bake threw: {ex}", StardewModdingAPI.LogLevel.Warn); }
            }
            finally
            {
                _device.SetRenderTargets(prev);
            }
        }

        private static readonly string[] _sceneLayerFamilies = { "Back", "Buildings", "Front" };
        private bool _sceneErrorLogged;

        /// <summary>A/B switch for the scenery mirror source (radiance_reflect scene on/off).
        /// ON is the shipping default, and it is NOT an experiment: the composed-screen source
        /// has to carve every sprite out of the mirror, which leaves a body-shaped HOLE in the
        /// water wherever someone stands near the bank — the reported "hollow reflection".
        /// The scene bake exists to answer exactly that (the hole must show the map's real
        /// colours instead): the
        /// mirrored area shows the real map art and the entity RT stamps the bodies on top.
        /// Defaulting it off (tried once, to pin the look to 1.2.x) brought the hole straight
        /// back. `radiance_reflect scene off` remains for the Phase-D bridge diagnosis.</summary>
        internal static bool SceneSourceOff;

        // ---- diagnostics: what is each reflection layer actually doing right here? ----

        /// <summary>Mean colour of a small block of a render target around a screen point.
        /// A GPU readback, so console-command only — never per frame.</summary>
        private static Vector4 MeanAt(RenderTarget2D? rt, int cx, int cy, int half = 6)
        {
            if (rt == null)
                return new Vector4(-1f);
            int x0 = Math.Clamp(cx - half, 0, rt.Width - 1), x1 = Math.Clamp(cx + half, 0, rt.Width - 1);
            int y0 = Math.Clamp(cy - half, 0, rt.Height - 1), y1 = Math.Clamp(cy + half, 0, rt.Height - 1);
            int w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
            var buf = new Color[w * h];
            try { rt.GetData(0, new Rectangle(x0, y0, w, h), buf, 0, buf.Length); }
            catch { return new Vector4(-1f); }
            Vector4 sum = Vector4.Zero;
            foreach (var c in buf) sum += c.ToVector4();
            return sum / buf.Length;
        }

        /// <summary>Human-readable report of every input the reflection depends on, sampled
        /// under the player and a few tiles below them. Answers, without guessing: is this
        /// pixel march-water, where does its waterline sit, did each RT bake, and does the
        /// scenery source actually contain pixels (or is the mirror sampling black)?</summary>
        public string ReflectionDiag()
        {
            var who = Game1.player;
            if (who == null || _waterMask == null || _waterMaskPixels == null)
                return "[reflect] no player / no water mask on this map";

            var report = new System.Text.StringBuilder();
            report.AppendLine($"[reflect] location={Game1.currentLocation?.Name} waterAny={_hasWaterInMask} maskOrigin=({_lastWaterTileX},{_lastWaterTileY}) maskPx={_waterMask.Width}x{_waterMask.Height}");
            report.AppendLine($"[reflect] entityRT ready={ReflectRTReady} hasPlayer={ReflectRTHasPlayer} squash={MirrorSquash} | sceneRT ready={SceneRTReady} forcedOff={SceneSourceOff} | spriteMask ready={SpriteMaskReady}");
            report.AppendLine($"[reflect] wlAnchor={(_waterlineAnchorData != null ? $"built for {_waterlineAnchorData.Location?.Name} ({_waterlineAnchorData.PixelWidth}x{_waterlineAnchorData.PixelHeight})" : "none yet")}");

            Rectangle box = who.GetBoundingBox();
            for (int t = 0; t <= 4; t++)
            {
                int wx = box.Center.X / 4 - _lastWaterTileX * 16;
                int wy = (box.Bottom - 4) / 4 - _lastWaterTileY * 16 + t * 16;
                if (ReadWaterMaskPixel(wx, wy) is not Color m)
                { report.AppendLine($"[reflect] +{t} tile: outside the mask window"); continue; }
                string kind = m.A < 64 ? "ice" : m.A < 192 ? "lava" : "water";
                report.AppendLine($"[reflect] +{t} tile below feet: effectR={m.R} marchG={m.G} edgeDistB={m.B} ({m.B * 0.5f:0.0} texels to the waterline) type={kind}"
                            + (m.G == 0 ? "   <- NO entity reflection here (not march water)" : ""));
            }

            var scr = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom));
            int sx = (int)scr.X, sy = (int)scr.Y;
            Vector4 sceneMean = MeanAt(_mirrorSourceRenderTarget, sx, sy - 96);
            Vector4 entMean = MeanAt(_reflectionRenderTarget, sx, sy + 32);
            report.AppendLine($"[reflect] sceneRT mean 1.5 tiles ABOVE the feet (the mirror's source) = {(sceneMean.X < 0 ? "unreadable" : $"rgb({sceneMean.X:0.00},{sceneMean.Y:0.00},{sceneMean.Z:0.00}) a={sceneMean.W:0.00}")}");
            report.AppendLine($"[reflect] entityRT mean 0.5 tile BELOW the feet (your own reflection) = {(entMean.X < 0 ? "unreadable" : $"rgb({entMean.X:0.00},{entMean.Y:0.00},{entMean.Z:0.00}) a={entMean.W:0.00}")}");
            report.AppendLine("[reflect] a near-black sceneRT mean with lit map art on screen = the P3c source is the bug; run 'radiance_reflect scene off' and compare.");
            return report.ToString().TrimEnd();
        }
    }
}
