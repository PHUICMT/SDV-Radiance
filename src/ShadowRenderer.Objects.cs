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
    /// ShadowRenderer — OBJECT shadows: one silhouette per tree/bush/crop/clump/furniture/
    /// craftable/critter and per map-tile prop (fences, posts, pier deco — classified from
    /// the actual tile art). EmitObj routes each sprite through the shared bake cache;
    /// DrawTilePropShadows is the map-art classifier.
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
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
                    // Also check BELOW the base: a pier post's baked shadow pools onto the water
                    // under the dock, fighting the screen-space mirror (the water already reflects
                    // the post — a ground-shadow smear on top reads as a ghost double).
                    if (OnWater(loc, new Point(x, y)) || OnWater(loc, new Point(x, y - 1))
                        || OnWater(loc, new Point(x, y + 1)))
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
    }
}
