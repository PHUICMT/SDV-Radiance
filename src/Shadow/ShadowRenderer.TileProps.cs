using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer — shadows for props painted INTO THE MAP, which is a different problem from
    /// object shadows and shares nothing with them but the bake pool. There is no game entity to ask:
    /// a lamp post is a Buildings-layer base tile with Front-layer tiles stacked above it, and the
    /// only way to know a column is a free-standing prop rather than a house roof or a tree canopy
    /// is to read the tile art and measure it. <see cref="ClassifyTileProp"/> is that measurement,
    /// cached per location-day; the rest of this file is the column bake and the draw that uses it.
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>
        /// Shadows for props painted INTO the map (street lamps, signposts, poles…): a Buildings-layer
        /// base tile with Front-layer tiles stacked above it. Only 1-tile-wide, free-standing columns
        /// cast — wider Front regions are house roofs/tree canopies, which must not (a leaning
        /// house-wall shadow is exactly the artifact we removed). The silhouette is baked from the
        /// column's REAL tile art, so whatever the prop looks like, its shadow matches.
        /// </summary>
        private void DrawTilePropShadows(SpriteBatch spriteBatch, GameLocation location, float rotation, float stretch,
            float alpha, float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            var frontLayer = location.map?.GetLayer("Front");
            var alwaysFrontLayer = location.map?.GetLayer("AlwaysFront");
            var buildingsLayer = location.map?.GetLayer("Buildings");
            if (frontLayer == null || buildingsLayer == null)
                return;
            int mapWidth = Math.Min(frontLayer.LayerWidth, buildingsLayer.LayerWidth), mapHeight = Math.Min(frontLayer.LayerHeight, buildingsLayer.LayerHeight);
            tileX0 = Math.Max(0, tileX0); tileX1 = Math.Min(mapWidth - 1, tileX1);
            tileY0 = Math.Max(1, tileY0); tileY1 = Math.Min(mapHeight - 1, tileY1);

            float propRotation = rotation;
            float propStretch = LengthCap(stretch, 0.6f);
            float shear = -(float)Math.Sin(propRotation) * propStretch;
            float shearScaleY = Math.Max(0.15f, propStretch * (float)Math.Cos(propRotation));

            // Near-player prop diagnostics (DebugLogging): every ~3s log why Buildings tiles within
            // 4 tiles of the player do or don't cast — the quick way to see why a fence stays bare.
            bool propDiagnosticsDue = DiagnosticMonitor != null && !_isBakingObjects && Game1.ticks % 600 == 0;
            Point playerTile = Game1.player?.TilePoint ?? default;
            void NoteNearPlayer(int tileX, int tileY, string why)
            {
                if (propDiagnosticsDue && Math.Abs(tileX - playerTile.X) <= 4 && Math.Abs(tileY - playerTile.Y) <= 4)
                    DiagnosticMonitor!.Log($"[shadow] prop({tileX},{tileY}) {why}", LogLevel.Debug);
            }

            // Which way the shadow leans decides which neighbouring column the wall guard has to
            // look at, so it is the one part of the classification that cannot be answered without
            // the sun. All three of its possible answers are cached instead.
            int leanDirection = shear > 0.01f ? -1 : (shear < -0.01f ? 1 : 0);

            // Everything else here is a question about the MAP ART: which sheet a tile is on, how
            // opaque it is, what stands beside and above it, whether the game calls it passable.
            // None of that changes while you are standing there, and all of it was being worked out
            // again for every tile on screen, in two passes, sixty times a second. Now it is worked
            // out once per tile and kept until the map itself changes.
            if (!SDVRadiance.LiveScreens.SamePlace(location, _propCacheLocation) || !SDVRadiance.LiveScreens.SameMapSize(location.map, _propCacheMap)
                || Game1.Date.TotalDays != _propCacheDay)
            {
                _propCache.Clear();
                _propCacheLocation = location;
                _propCacheMap = location.map;
                _propCacheDay = Game1.Date.TotalDays;
            }

            for (int y = tileY0; y <= tileY1; y++)
            {
                for (int x = tileX0; x <= tileX1; x++)
                {
                    int cell = y * mapWidth + x;
                    if (!_propCache.TryGetValue(cell, out TilePropCast? cast))
                        _propCache[cell] = cast = ClassifyTileProp(location, buildingsLayer, frontLayer, alwaysFrontLayer, x, y, mapWidth, mapHeight);
                    if (!cast.Casts)
                    {
                        if (cast.Note != null)
                            NoteNearPlayer(x, y, cast.Note);
                        continue;
                    }
                    if (cast.BlockedNorth || (leanDirection < 0 ? cast.BlockedWest : leanDirection > 0 && cast.BlockedEast))
                    {
                        NoteNearPlayer(x, y, "skip: wall to the north (lean would paint onto it)");
                        continue;
                    }
                    if (cast.Note != null)
                        NoteNearPlayer(x, y, cast.Note);

                    var key = cast.Key;
                    Texture2D texture = cast.Texture;
                    int count = cast.Sources.Length;
                    if (_isBakingObjects)
                    {
                        if (_objectGraphicsDevice != null && !_bakedObjectCache.ContainsKey(key)
                            && BakeTileColumn(_objectGraphicsDevice, texture, cast.Sources, cast.Levels, cast.Orients, count, shear, blur,
                                out RenderTarget2D renderTarget, out Vector2 feetInRenderTarget))
                            // A tile column is 16 px wide and as many tiles tall as the prop: its lean already carries
                            // further than its width, so there is nothing for the narrowing to fix here.
                            _bakedObjectCache[key] = new SpriteBake { Rt = renderTarget, FeetInRt = feetInRenderTarget, BakedShear = shear, BakedBlur = blur, Content = _lastBakeContent, SlotClass = _lastBakeClass, BakedScale = _lastBakeScale, LastUsedTick = Game1.ticks };
                        continue;
                    }
                    if (!_bakedObjectCache.TryGetValue(key, out SpriteBake? bakedEntry))
                    {
                        // A prop the bake pass has not seen yet (this map arrived after the last
                        // full walk, or its slot was evicted). The classification already holds
                        // the column, so the request is just a reference to it.
                        //
                        // Unless it can never be baked at all, which is a request that fails for
                        // the rest of the session and reads as ordinary cache churn while it does.
                        // The sprite path stopped making those; this one had gone on making them.
                        if (!ChooseBakeFit(16f, cast.Height * 16f, shear, blur, null, out _, out _, out _))
                        {
                            FrameCost.Count(FrameCost.Counter.BakeTooBig);
                            continue;
                        }
                        FrameCost.Count(FrameCost.Counter.BakeMisses);
                        QueueTileColumnBake(key, cast, shear, blur);
                        continue;
                    }
                    FrameCost.Count(FrameCost.Counter.ShadowSprites);
                    bakedEntry.LastUsedTick = Game1.ticks;
                    // Same per-sprite staleness rule as EmitObject: the lean lives in the pixels, so
                    // the column earns a re-bake once the sun has moved its tip a pixel and a half.
                    if (Math.Abs(shear - bakedEntry.BakedShear) * (cast.Height + 1) * 64f > ShearRefreshPixels
                        || Math.Abs(blur - bakedEntry.BakedBlur) > 0.3f)
                        QueueTileColumnBake(key, cast, shear, blur);
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f + 32f, (y + 1f) * 64f - 2f));
                    // A body ON this tile (someone sitting on a map bench, standing against a
                    // fence) sorts at roughly y*64/10000 - a full tile BELOW this prop's normal
                    // (y+1)*64 depth. Both the cast and the base redraw below therefore won over
                    // the body and painted the bench across the sitter: proven by A/B, the exact
                    // "we clip through the chair" report, and almost certainly the original
                    // graveyard-bench one too (that bench is map art, so no furniture or
                    // character-side change could ever have reached it). Sort from the prop's OWN
                    // row when someone is there, so the body always wins.
                    bool bodyHere = false;
                    try
                    {
                        var tileVector = new Vector2(x, y);
                        bodyHere = location.isCharacterAtTile(tileVector) != null
                            || (Game1.player != null && Game1.player.currentLocation == location
                                && Game1.player.TilePoint.X == x && Game1.player.TilePoint.Y == y);
                    }
                    catch { }
                    float rowY = bodyHere ? y * 64f : (y + 1f) * 64f;
                    float depth = MathHelper.Clamp(rowY / 10000f + x * 1e-5f - ShadowDepthBias, 0f, 1f);
                    Rectangle propContent = bakedEntry.Content.IsEmpty ? new Rectangle(0, 0, bakedEntry.Rt.Width, bakedEntry.Rt.Height) : bakedEntry.Content;
                    float unbake = 4f / bakedEntry.BakedScale;   // 1 unless the lean forced a coarser bake
                    DrawSoft(spriteBatch, Taps9, bakedEntry.Rt, propContent,
                        feet, Color.White, alpha, 0f, bakedEntry.FeetInRt - new Vector2(propContent.X, propContent.Y),
                        new Vector2(unbake, unbake * shearScaleY), depth, SpriteEffects.None, 0f);
                    // Redraw the base tile OVER its own shadow: the map layer painted before this
                    // batch, so without this the near end of the cast darkens the prop itself
                    // (the "shadow on the lamp post" complaint). Front-stack tiles need no redraw —
                    // the Front layer paints after us anyway.
                    DrawOrientedTile(spriteBatch, texture, cast.BaseSrc,
                        Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f, y * 64f)), 4f,
                        cast.BaseOrient, Color.White, Math.Min(1f, depth + 5e-4f));
                }
            }
        }

        /// <summary>
        /// Decide, once, whether the map art at one tile is a free-standing prop that should cast,
        /// and if so what its column is made of. Everything it reads is fixed for the map, so the
        /// answer is kept until the map changes underneath it (see the cache in the caller).
        /// </summary>
        private TilePropCast ClassifyTileProp(GameLocation location, xTile.Layers.Layer buildingsLayer,
            xTile.Layers.Layer frontLayer, xTile.Layers.Layer? alwaysFrontLayer, int x, int y, int mapWidth, int mapHeight)
        {
            // Reasons are only worth building when someone is reading them.
            TilePropCast NoCast(string why) => new() { Note = DiagnosticMonitor != null ? why : null };

            // Some maps paint the pole top on AlwaysFront instead of Front — treat them as one layer.
            xTile.Tiles.Tile? FrontTileAt(int tileX, int tileY)
            {
                var frontTile = frontLayer.Tiles[tileX, tileY];
                if (frontTile == null && alwaysFrontLayer != null && tileX < alwaysFrontLayer.LayerWidth && tileY < alwaysFrontLayer.LayerHeight)
                    frontTile = alwaysFrontLayer.Tiles[tileX, tileY];
                return frontTile;
            }

            var baseTile = buildingsLayer.Tiles[x, y];
            if (baseTile == null)
                return new TilePropCast();
            // A prop base is a Buildings tile. Front art on the SAME row is normal for
            // fences (their upper half is painted there so the player walks behind it) —
            // it joins the silhouette as a level-0 overlay rather than disqualifying the
            // cell. Animated tiles are skipped — a frozen frame would cast a lie.
            if (baseTile is xTile.Tiles.AnimatedTile || baseTile.TileSheet == null)
                return NoCast(baseTile is xTile.Tiles.AnimatedTile ? "skip: animated tile" : "skip: no tilesheet");
            Texture2D? texture = LoadCached(baseTile.TileSheet.ImageSource);
            if (texture == null)
                return new TilePropCast();
            var baseImageBounds = baseTile.TileSheet.GetTileImageBounds(baseTile.TileIndex);
            var baseSourceRect = new Rectangle(baseImageBounds.X, baseImageBounds.Y, baseImageBounds.Width, baseImageBounds.Height);
            // How the MAP places this tile. A .tmx keeps mirroring and rotation in the gid, which
            // the loader cannot put in the tile index, so it arrives as the @Flip/@Rotation
            // properties MapLayers.Orientation decodes. Nothing on this path ever read them: the
            // base redraw below painted an UNTURNED copy over the game's turned one, which on a map
            // that uses them is art in the wrong orientation appearing wherever a prop was found.
            // Reported with an on/off screenshot on a farm whose .tmx turns 2,798 cells.
            byte baseOrientation = MapLayers.Orientation(baseTile);
            float coverage = TileCoverage(texture, baseSourceRect);

            // Fences paint their upper half on Front at the SAME row (so the player can
            // walk behind them). Fold that art into the prop: classification uses the
            // union coverage, and the silhouette gets it as a level-0 overlay. When the
            // Buildings tile is a bare INVISIBLE collision tile (cov≈0) under Front-drawn
            // art on another sheet, the Front art IS the prop — adopt its sheet instead.
            Rectangle? sameRowOverlay = null;
            int sameRowTileIndex = 0, baseTileIndex = baseTile.TileIndex;
            byte sameRowOrientation = 0;
            {
                var sameRowTile = FrontTileAt(x, y);
                if (sameRowTile != null && sameRowTile is not xTile.Tiles.AnimatedTile && sameRowTile.TileSheet != null
                    && LoadCached(sameRowTile.TileSheet.ImageSource) is { } sameRowTexture)
                {
                    var sameRowImageBounds = sameRowTile.TileSheet.GetTileImageBounds(sameRowTile.TileIndex);
                    var sameRowSourceRect = new Rectangle(sameRowImageBounds.X, sameRowImageBounds.Y, sameRowImageBounds.Width, sameRowImageBounds.Height);
                    if (ReferenceEquals(sameRowTexture, texture))
                    {
                        sameRowOverlay = sameRowSourceRect;
                        sameRowTileIndex = sameRowTile.TileIndex;
                        sameRowOrientation = MapLayers.Orientation(sameRowTile);
                        coverage = Math.Max(coverage, TileCoverage(texture, sameRowSourceRect));
                    }
                    else if (coverage < 0.04f)
                    {
                        texture = sameRowTexture;
                        baseSourceRect = sameRowSourceRect;
                        baseTileIndex = sameRowTile.TileIndex;
                        baseOrientation = MapLayers.Orientation(sameRowTile);   // the Front art IS the prop now
                        coverage = TileCoverage(sameRowTexture, sameRowSourceRect);
                    }
                }
            }

            // Is this a thing standing on the ground, or the ground itself?
            //
            // Coverage alone cannot answer that, and treating it as if it could is what
            // left every desert cactus bare: a cactus reads 0.97 opaque, exactly like the
            // cliff behind it, because BOTH are solid art — one is just small. What
            // actually separates them is SPAN. A prop is a narrow island of map art;
            // terrain is a mass. So coverage now only rejects art too faint to be
            // anything (a bare collision tile), and span decides the rest.
            //
            // Measured on the Buildings layer in both axes, because one axis is not
            // enough on its own: a cliff's bottom row is wide, but a one-tile-wide
            // vertical spur of that same cliff is not.
            bool propByCoverage = coverage >= 0.04f && coverage <= 0.95f;
            int spanWidth = 1, spanHeight = 1;
            if (!propByCoverage && coverage > 0.04f)
            {
                for (int scanX = x - 1; scanX >= 0 && spanWidth <= MaxPropSpan && buildingsLayer.Tiles[scanX, y] != null; scanX--) spanWidth++;
                for (int scanX = x + 1; scanX < mapWidth && spanWidth <= MaxPropSpan && buildingsLayer.Tiles[scanX, y] != null; scanX++) spanWidth++;
                for (int scanY = y - 1; scanY >= 0 && spanHeight <= MaxPropSpan && buildingsLayer.Tiles[x, scanY] != null; scanY--) spanHeight++;
                for (int scanY = y + 1; scanY < mapHeight && spanHeight <= MaxPropSpan && buildingsLayer.Tiles[x, scanY] != null; scanY++) spanHeight++;
            }
            bool propBySpan = !propByCoverage && coverage > 0.04f && spanWidth <= MaxPropSpan && spanHeight <= MaxPropSpan;
            if (!propByCoverage && !propBySpan)
                return NoCast($"skip: cov={coverage:0.00} span={spanWidth}x{spanHeight} → not a prop");
            // A "prop" sitting ON opaque art below is wall decor — a window halfway up a
            // house wall must not cast.
            if (OpaqueMapTile(buildingsLayer, x, y + 1, mapHeight))
                return NoCast("skip: sits on opaque art (wall decor)");
            // A transparent tile BESIDE opaque art is the fringe of a big structure (the
            // truck's edge tiles, awning ends…), not a free-standing prop — its lone-column
            // cast reads as a stray dark line. Real fences/posts never hug opaque art.
            if (propByCoverage && (OpaqueMapTile(buildingsLayer, x - 1, y, mapHeight) || OpaqueMapTile(buildingsLayer, x + 1, y, mapHeight)))
                return NoCast("skip: opaque neighbour beside (structure fringe)");
            // Skip only when the prop itself (or the tile its lean lands on) is open WATER
            // SURFACE — pier decks over water are solid ground (Height Framework separates
            // deck from water), so dock ropes / mooring posts / lanterns cast onto the pier.
            // Also check BELOW the base: a pier post's baked shadow pools onto the water
            // under the dock, fighting the screen-space mirror (the water already reflects
            // the post — a ground-shadow smear on top reads as a ghost double).
            if (OnWater(location, new Point(x, y)) || OnWater(location, new Point(x, y - 1))
                || OnWater(location, new Point(x, y + 1)))
                return NoCast("skip: on/over open water");
            // A PASSABLE Buildings tile is the game's own word for "you walk on top of
            // this": a plank bridge, a pier deck, a boardwalk. That is a horizontal
            // SURFACE, not a standing prop, and it must never reach the cast below.
            //
            // A log bridge otherwise sails through every gate above — it is not open
            // water (it has a Buildings tile, so the OnWater fallback says no), and its
            // art has gaps between the planks, so its coverage lands in the same 0.04–0.95
            // band as a picket fence. It then gets a fence's treatment: a sheared
            // silhouette leaning up-screen, plus the base tile REDRAWN opaque on top of
            // that shadow at depth + 5e-4. Both land on the tile a character standing on
            // the bridge occupies, so the planks are drawn over their legs and the sheared
            // copy sits offset beside the real bridge. Reported as "character texture is
            // covered" and "texture misaligned", and as players sinking into bridges.
            if (location.doesTileHaveProperty(x, y, "Passable", "Buildings") != null)
                return NoCast("skip: passable Buildings tile (walk-on deck / bridge)");

            // Gather the column bottom→top: the base tile, its same-row Front overlay
            // (level 0 too), then any Front stack above (level = tiles above the base).
            // Orientation is part of the KEY, not just of the drawing. The bake cache is keyed by
            // what the silhouette looks like, and the same tile index mirrored is a different
            // silhouette: without this a turned tile and a plain one shared one baked shadow, and
            // whichever baked first decided the shape for both.
            _tileColumnSourceRects[0] = baseSourceRect;
            _tileColumnLevels[0] = 0;
            _tileColumnOrients[0] = baseOrientation;
            int count = 1, levels = 1, keyHash = (17 * 31 + baseTileIndex) * 31 + baseOrientation;
            if (sameRowOverlay is Rectangle overlaySourceRect)
            {
                _tileColumnSourceRects[count] = overlaySourceRect;
                _tileColumnOrients[count] = sameRowOrientation;
                _tileColumnLevels[count++] = 0;
                keyHash = (keyHash * 31 + sameRowTileIndex) * 31 + sameRowOrientation;
            }
            for (int levelsUp = 1; count < _tileColumnSourceRects.Length && y - levelsUp >= 0; levelsUp++)
            {
                var frontTile = FrontTileAt(x, y - levelsUp);
                if (frontTile == null || frontTile is xTile.Tiles.AnimatedTile || frontTile.TileSheet == null
                    || !ReferenceEquals(LoadCached(frontTile.TileSheet.ImageSource), texture))
                    break;
                var imageBounds = frontTile.TileSheet.GetTileImageBounds(frontTile.TileIndex);
                _tileColumnSourceRects[count] = new Rectangle(imageBounds.X, imageBounds.Y, imageBounds.Width, imageBounds.Height);
                _tileColumnOrients[count] = MapLayers.Orientation(frontTile);
                _tileColumnLevels[count++] = levelsUp;
                levels = levelsUp + 1;
                keyHash = (keyHash * 31 + frontTile.TileIndex) * 31 + _tileColumnOrients[count - 1];
            }

            // Wall guard, scaled to the prop's height: the up-lean cast occupies the tiles
            // north of the base (and one column toward the lean side) — if any of those is
            // opaque wall art, the shadow would paint onto the wall ("through the house").
            // All three lean directions are answered here so the draw pass can just pick one.
            var result = new TilePropCast
            {
                Casts = true,
                Texture = texture,
                BaseSrc = baseSourceRect,
                Height = levels,
                BaseOrient = baseOrientation,
                Sources = new Rectangle[count],
                Levels = new int[count],
                Orients = new byte[count],
                Key = (texture, new Rectangle(keyHash, count, -1, -1), SpriteEffects.None),   // width −1 can never collide with a real source rect
                Note = DiagnosticMonitor != null ? $"cast: col={count} cov={coverage:0.00}" : null,
            };
            Array.Copy(_tileColumnSourceRects, result.Sources, count);
            Array.Copy(_tileColumnLevels, result.Levels, count);
            Array.Copy(_tileColumnOrients, result.Orients, count);
            for (int i = 1; i <= levels; i++)
            {
                result.BlockedNorth |= OpaqueMapTile(buildingsLayer, x, y - i, mapHeight);
                result.BlockedWest |= OpaqueMapTile(buildingsLayer, x - 1, y - i, mapHeight);
                result.BlockedEast |= OpaqueMapTile(buildingsLayer, x + 1, y - i, mapHeight);
            }
            return result;
        }

        /// <summary>
        /// Draw one map tile the way the MAP places it, mirrored and/or turned per its
        /// @Flip/@Rotation (decoded into a byte by <see cref="MapLayers.Orientation"/>: bit 2 is a
        /// horizontal mirror applied BEFORE bits 0-1 quarter turns clockwise).
        ///
        /// <para>SpriteBatch mirrors the source and then rotates about the origin, which is the
        /// same order, so the turn is a rotation about the tile's CENTRE and the position moves
        /// from the tile's corner to its centre to match. A plain tile - the overwhelming majority
        /// - takes the corner path unchanged, so nothing about the common case moves.</para>
        /// </summary>
        private static void DrawOrientedTile(SpriteBatch spriteBatch, Texture2D texture, Rectangle sourceRect,
            Vector2 topLeft, float scale, byte orientation, Color colour, float depth)
        {
            FrameCost.Count(FrameCost.Counter.ShadowDrawCalls);
            if (orientation == 0)
            {
                spriteBatch.Draw(texture, topLeft, sourceRect, colour, 0f, Vector2.Zero, scale, SpriteEffects.None, depth);
                return;
            }
            Vector2 centre = topLeft + new Vector2(sourceRect.Width * scale * 0.5f, sourceRect.Height * scale * 0.5f);
            spriteBatch.Draw(texture, centre, sourceRect, colour, (orientation & 3) * MathHelper.PiOver2,
                new Vector2(sourceRect.Width * 0.5f, sourceRect.Height * 0.5f), scale,
                (orientation & 4) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, depth);
        }

        /// <summary>True when a Buildings tile exists at (x,y) and its art is essentially opaque
        /// (terrain/wall art, not a see-through prop). Out-of-range or empty → false.</summary>
        private bool OpaqueMapTile(xTile.Layers.Layer buildingsLayer, int x, int y, int mapHeight)
        {
            if (y < 0 || y >= mapHeight || x < 0 || x >= buildingsLayer.LayerWidth)
                return false;
            var tile = buildingsLayer.Tiles[x, y];
            if (tile == null || tile.TileSheet == null)
                return false;
            if (tile is xTile.Tiles.AnimatedTile)
                return true;   // animated map art next to a prop → treat as solid, don't cast
            Texture2D? texture = LoadCached(tile.TileSheet.ImageSource);
            if (texture == null)
                return true;
            var imageBounds = tile.TileSheet.GetTileImageBounds(tile.TileIndex);
            // Same bound as the propByCoverage cast test (0.95). With a LOWER bound here, dense fence
            // tiles at cov 0.91–0.95 counted as "walls" and poisoned their own neighbours —
            // every tile of the row skipped as "structure fringe" and no fence ever cast.
            return TileCoverage(texture, new Rectangle(imageBounds.X, imageBounds.Y, imageBounds.Width, imageBounds.Height)) > 0.95f;
        }

        /// <summary>Fraction of a tile's art that is opaque (alpha > 48). Sampled once per
        /// (sheet, rect) and cached — this is the "look at the actual image" prop test.</summary>
        private readonly System.Collections.Generic.Dictionary<(Texture2D texture, Rectangle sourceRect), float> _tileCoverageCache = new();
        private Color[] _tileCoveragePixels = new Color[1024];
        // Whole-tilesheet pixel cache: reading each prop tile with its own texture.GetData is a separate
        // GPU readback (pipeline flush); walking into a prop-heavy screen fired a burst of them in one
        // frame. Read each sheet back ONCE, then count coverage from the CPU array (zero GPU work).
        private readonly System.Collections.Generic.Dictionary<Texture2D, Color[]?> _tilesheetCoveragePixels = new();
        // Refusal bound for absurd sheets only — see the note on RenderPipeline.SheetPixCap. The
        // old 8 Mpx ceiling landed under real modded tilesheets, and the fallback beneath it is a
        // GPU readback per tile.
        private const int CoverageSheetCap = 64_000_000;

        private Color[]? CoverageSheetPixels(Texture2D texture)
        {
            if (_tilesheetCoveragePixels.TryGetValue(texture, out Color[]? sheetPixels))
                return sheetPixels;
            // One readback for the sheet, not one per strip of 512 rows: see SheetReadback for
            // why the strips were costing what they were written to save.
            sheetPixels = SheetReadback.Read(texture, CoverageSheetCap, "sheet: shadow tile coverage");
            _tilesheetCoveragePixels[texture] = sheetPixels;
            return sheetPixels;
        }

        private float TileCoverage(Texture2D texture, Rectangle sourceRect)
        {
            if (_tileCoverageCache.TryGetValue((texture, sourceRect), out float coverage))
                return coverage;
            int pixelCount = sourceRect.Width * sourceRect.Height;
            if (pixelCount <= 0 || sourceRect.X < 0 || sourceRect.Y < 0 || sourceRect.Right > texture.Width || sourceRect.Bottom > texture.Height)
                return _tileCoverageCache[(texture, sourceRect)] = 1f;
            int solid = 0;
            Color[]? sheet = CoverageSheetPixels(texture);
            if (sheet != null)
            {
                int sheetWidth = texture.Width;
                for (int row = 0; row < sourceRect.Height; row++)
                {
                    int rowStart = (sourceRect.Y + row) * sheetWidth + sourceRect.X;
                    for (int column = 0; column < sourceRect.Width; column++)
                        if (sheet[rowStart + column].A > 48) solid++;
                }
            }
            else
            {
                if (_tileCoveragePixels.Length < pixelCount)
                    _tileCoveragePixels = new Color[pixelCount];
                try { texture.GetData(0, sourceRect, _tileCoveragePixels, 0, pixelCount); }
                catch { return _tileCoverageCache[(texture, sourceRect)] = 1f; }
                for (int i = 0; i < pixelCount; i++)
                    if (_tileCoveragePixels[i].A > 48) solid++;
            }
            return _tileCoverageCache[(texture, sourceRect)] = (float)solid / pixelCount;
        }

        /// <summary>Record a map-tile column for the next bake pass. The classification owns the
        /// column arrays and outlives the frame, so the request just points at them.</summary>
        private void QueueTileColumnBake((Texture2D texture, Rectangle sourceRect, SpriteEffects effect) key, TilePropCast cast, float shear, float blurPixels)
        {
            if (_objectBakeQueue.Count >= ObjectBakeQueueCap || cast.Sources.Length == 0)
                return;
            _objectBakeQueue[key] = new ObjectBakeRequest { Shear = shear, Blur = blurPixels, ColumnSources = cast.Sources, ColumnLevels = cast.Levels, ColumnOrients = cast.Orients };
        }

        /// <summary>
        /// What the map art at one tile means for shadows: whether it is a free-standing prop at
        /// all, and if so which tiles make up its column and which directions the lean is walled
        /// off in. Fixed for a given map, so it is worked out once per tile rather than per frame.
        /// </summary>
        private sealed class TilePropCast
        {
            public bool Casts;
            /// <summary>Why not, for the near-player diagnostic. Only filled when it will be read.</summary>
            public string? Note;
            public Texture2D Texture = null!;
            public Rectangle BaseSrc;
            /// <summary>Tiles the column occupies above its base row, for the lean-drift test.</summary>
            public int Height;
            public Rectangle[] Sources = System.Array.Empty<Rectangle>();
            public int[] Levels = System.Array.Empty<int>();
            /// <summary>How the map turns each source, one byte per entry (see MapLayers.Orientation).</summary>
            public byte[] Orients = System.Array.Empty<byte>();
            /// <summary>How the map turns the BASE tile, for the redraw that puts it back on top.</summary>
            public byte BaseOrient;
            public (Texture2D texture, Rectangle sourceRect, SpriteEffects effect) Key;
            /// <summary>Is there opaque art where the cast would land, leaning each of the three
            /// ways the sun can take it? Answered here because the sun is the only part of this
            /// that changes, and it changes between three fixed choices.</summary>
            public bool BlockedNorth, BlockedWest, BlockedEast;
        }

        private readonly System.Collections.Generic.Dictionary<int, TilePropCast> _propCache = new();
        private GameLocation? _propCacheLocation;
        private xTile.Map? _propCacheMap;
        /// <summary>Day the classification was taken on. Map art is edited by content packs at the
        /// day boundary (seasonal sheets, festival layouts) and buildings finish overnight, so a
        /// new day is the one moment the answers can change without the map object being replaced.</summary>
        private int _propCacheDay = -1;

        /// <summary>Per-column tile source rects, filled by the scan then baked.</summary>
        private readonly Rectangle[] _tileColumnSourceRects = new Rectangle[7];
        /// <summary>Height level (tiles above the base row) for each entry of <see cref="_tileColumnSourceRects"/> —
        /// a same-row Front overlay shares level 0 with the base tile.</summary>
        private readonly int[] _tileColumnLevels = new int[7];
        /// <summary>Per-column tile orientations, alongside the source rects.</summary>
        private readonly byte[] _tileColumnOrients = new byte[7];

        /// <summary>Bake a stacked tile column (black + feet→tip gradient, sun lean pre-baked as a
        /// shear about the feet row) into a pooled object RT. The sources and their heights are
        /// passed in rather than read from the scan's scratch arrays, so a queued re-bake a frame
        /// later replays the same column without redoing the scan that found it.</summary>
        private bool BakeTileColumn(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle[] sources, int[] tileLevels,
            byte[]? orientations, int count, float shear, float blurPixels, out RenderTarget2D renderTarget, out Vector2 feetInRenderTarget, RenderTarget2D? into = null)
        {
            renderTarget = null!;
            feetInRenderTarget = default;
            int levels = 0;
            for (int i = 0; i < count; i++)
                levels = Math.Max(levels, tileLevels[i] + 1);
            // Map art is 16 px a tile, so the column's own size is levels of that; the bake scale
            // turns it into slot texels exactly as it does for a sprite. This path used to be the
            // one place a refusal still happened INSIDE the bake: a column too wide for its slot
            // (which the lean alone can do, at the top of the shadow-length slider) was refused,
            // never cached, and so re-queued, re-attempted and re-refused every frame for the rest
            // of the session - the very waste the sprite path stopped paying, left in the half
            // nobody looked at. The ladder ends it the same way, and one fit test replaces two that
            // disagreed about whether the blur counts.
            const float tileSource = 16f;
            if (count <= 0 || !ChooseBakeFit(tileSource, levels * tileSource, shear, blurPixels, into,
                                             out int columnSlotClass, out float scale, out float blurTexels))
            {
                NoteColumnRefusal($"{levels}-tile column with shear {shear:0.00} fits no slot at any bake scale");
                return false;
            }
            float tileTexels = tileSource * scale;
            float columnHeight = levels * tileTexels;
            _lastBakeClass = columnSlotClass;
            _lastBakeScale = scale;
            renderTarget = into ?? RentObjectRT(graphicsDevice, columnSlotClass);
            feetInRenderTarget = new Vector2(renderTarget.Width / 2f, renderTarget.Height - 8f);
            Matrix lean = ShearAbout(feetInRenderTarget, shear);
            try
            {
                graphicsDevice.SetRenderTarget(renderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                for (int i = 0; i < count; i++)
                    DrawOrientedTile(_renderTargetSpriteBatch, texture, sources[i],
                        new Vector2(feetInRenderTarget.X - tileTexels * 0.5f, feetInRenderTarget.Y - tileTexels * (tileLevels[i] + 1)),
                        scale, orientations != null && i < orientations.Length ? orientations[i] : (byte)0, Color.Black, 0f);
                _renderTargetSpriteBatch.End();
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_propGradientTexture!, new Rectangle(0, (int)(feetInRenderTarget.Y - columnHeight), renderTarget.Width, (int)columnHeight), Color.White);
                _renderTargetSpriteBatch.End();
                BlurSlotInPlace(graphicsDevice, renderTarget, blurTexels);
                _lastBakeContent = ContentBounds(new Vector2(feetInRenderTarget.X - tileTexels * 0.5f, feetInRenderTarget.Y - columnHeight),
                    tileTexels, columnHeight, feetInRenderTarget, shear, blurTexels, renderTarget.Width, renderTarget.Height);
                FrameCost.Count(FrameCost.Counter.ObjectBakes);
                return true;
            }
            catch
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                return false;
            }
        }
    }
}
