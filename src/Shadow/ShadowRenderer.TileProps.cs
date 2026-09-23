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
            // A post, a sign and a fence are flat faces standing on their edge, so their width
            // stays level on the screen and keeps its size whatever the sun does: ForCard. What
            // this replaces was a shear and a vertical squash worked out here by hand, with a
            // floor under the squash. The floor was put there to stop a shadow vanishing when the
            // sun stood square to the post, and while the sun could only ever be above the screen
            // nobody could see what it did the rest of the way round: it held the column at a
            // fifteenth of its height while the lean ran on to full, which is a sliver leaning
            // hard, and it is what a lamp post's shadow had become by mid afternoon.
            // ForSolid, not ForCard. A card keeps its width flat along the screen, which reads
            // right while the sun stays near the top of it and has no area left at all once the
            // sun is square to the post: width and length both lie along the screen's x and the
            // parallelogram closes up. A solid's width is always at right angles to its own
            // length, so there is a shadow at every angle of the circle the compass can reach.
            ShadowProjection projection = ShadowProjection.ForSolid(propRotation, propStretch, _groundForeshortening);
            // How much of the column's height survives as screen height, and WHICH WAY it goes.
            // A sun more than a quarter turn from the top of the screen throws the shadow toward
            // the viewer, and the cosine turns negative to say so. Clamping that sign away was the
            // one thing left in the shadow pass that could not describe the lower half of the sky:
            // the lamp post's shadow stood up the screen while every tree beside it lay down
            // toward the viewer, in the same light. Only the LENGTH is floored now, so a sun at
            // exactly a quarter turn still leaves a readable sliver instead of nothing.
            //
            // The sign is spent as a vertical FLIP rather than a negative scale. A negative
            // scale.Y makes SpriteBatch build the quad inside out (it multiplies the source
            // height by the scale to get the quad's height), and this draw goes into the game's
            // own batch, whose rasteriser culls by winding. A flip only swaps the texture
            // coordinates, so the quad stays wound the way it was.
            // Only the SIGN of the lean is read here now: how far a prop's shadow is laid over is
            // decided by the projection, in the bake. The length this used to work out was left
            // over from the draw-time squash and had not been read since the map columns started
            // going through ShadowProjection like every other caster.
            bool propPointsDownScreen = propStretch * (float)Math.Cos(propRotation) < 0f;

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
            // The lean's own sign, read off the projection now that it owns the lay-down: a
            // source pixel one above the feet lands AlongX to the side, so that is which way the
            // shadow runs and which neighbour the wall guard has to look at.
            int leanDirection = projection.AlongX < -0.01f ? -1 : (projection.AlongX > 0.01f ? 1 : 0);

            // Everything else here is a question about the MAP ART: which sheet a tile is on, how
            // opaque it is, what stands beside and above it, whether the game calls it passable.
            // None of that changes while you are standing there, and all of it was being worked out
            // again for every tile on screen, in two passes, sixty times a second. Now it is worked
            // out once per tile and kept until the map itself changes.
            _propCache = PropCacheFor(location);

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
                    if (!cast.PairChecked)
                        PairWithRight(location, buildingsLayer, frontLayer, alwaysFrontLayer, cast, x, y, mapWidth, mapHeight);
                    if (cast.ShadowByLeft)
                    {
                        // The left half cast this tile's shadow with its own; this tile only goes
                        // back on top of it.
                        if (!_isBakingObjects)
                            RedrawPropBase(spriteBatch, location, buildingsLayer, cast, x, y);
                        continue;
                    }
                    TilePropCast shadowCast = cast.Pair ?? cast;
                    // The northern wall only stands in the way while the shadow actually runs
                    // north. With the sun past a quarter turn the cast goes the other way, and
                    // holding it back for a wall behind it would delete a shadow for no reason.
                    if ((shadowCast.BlockedNorth && !propPointsDownScreen)
                        || (leanDirection < 0 ? shadowCast.BlockedWest : leanDirection > 0 && shadowCast.BlockedEast))
                    {
                        NoteNearPlayer(x, y, "skip: wall in the way (the lean would paint onto it)");
                        continue;
                    }
                    if (cast.Note != null)
                        NoteNearPlayer(x, y, cast.Note);

                    var key = shadowCast.Key;
                    Texture2D texture = shadowCast.Texture;
                    int count = shadowCast.Sources.Length;
                    float columnsWide = shadowCast.Columns;
                    if (_isBakingObjects)
                    {
                        if (_objectGraphicsDevice != null && !_bakedObjectCache.ContainsKey(key)
                            && BakeTileColumn(_objectGraphicsDevice, texture, shadowCast.Sources, shadowCast.Levels, shadowCast.Orients, count, projection, blur,
                                out RenderTarget2D renderTarget, out Vector2 feetInRenderTarget, null, shadowCast.ColumnOf))
                            // A tile column is 16 px wide and as many tiles tall as the prop: its lean already carries
                            // further than its width, so there is nothing for the narrowing to fix here.
                            _bakedObjectCache[key] = NewObjectBake(_objectGraphicsDevice, renderTarget, feetInRenderTarget, projection, blur);
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
                        if (!ObjectBakeCouldFit(new Rectangle(0, 0, (int)(16 * columnsWide), shadowCast.Height * 16), new Vector2(8f * columnsWide, shadowCast.Height * 16f), projection, blur))
                        {
                            FrameCost.Count(FrameCost.Counter.BakeTooBig);
                            continue;
                        }
                        FrameCost.Count(FrameCost.Counter.BakeMisses);
                        QueueTileColumnBake(key, shadowCast, projection, blur);
                        continue;
                    }
                    FrameCost.Count(FrameCost.Counter.ShadowSprites);
                    bakedEntry.LastUsedTick = SharedTicks.Now;
                    // The same staleness rule every other caster uses, now that a column is laid
                    // down by the same projection: how far its farthest pixel has moved between the
                    // lay-down in the pixels and the one the sun asks for.
                    if (projection.Drift(bakedEntry.BakedProjection, 16f * columnsWide, shadowCast.Height * 16f) * 4f > ShearRefreshPixels
                        || Math.Abs(blur - bakedEntry.BakedBlur) > 0.3f
                        || bakedEntry.BakedContactHardness != ContactHardnessNow || bakedEntry.BakedDepth != BakeDepthNow
                        || bakedEntry.BakedPenumbraStretch != PenumbraStretchNow)
                        QueueTileColumnBake(key, shadowCast, projection, blur);
                    // A pair is pinned at the middle of its two tiles, as its column was baked.
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f + 32f * columnsWide, (y + 1f) * 64f - 2f));
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
                    // The column is baked upright with its lean already sheared in, and the feet
                    // row named by FeetInRt. Flipping samples the slot bottom-to-top, so the feet
                    // row only stays under the post if the origin is measured from the other end
                    // of the content; the sideways lean rides along untouched, which is what a
                    // shadow falling the other way should do.
                    Vector2 propOrigin = bakedEntry.FeetInRt - new Vector2(propContent.X, propContent.Y);
                    if (propPointsDownScreen)
                        propOrigin.Y = propContent.Height - propOrigin.Y;
                    DrawSoft(spriteBatch, Taps9, bakedEntry.Rt, propContent,
                        feet, ShadowInk, alpha, 0f, propOrigin,
                        new Vector2(unbake, unbake), depth, SpriteEffects.None, 0f);
                    // Redraw the base tile OVER its own shadow: the map layer painted before this
                    // batch, so without this the near end of the cast darkens the prop itself
                    // (the "shadow on the lamp post" complaint). Front-stack tiles need no redraw —
                    // the Front layer paints after us anyway.
                    // It is the MAP's art, so it goes through the smoothing like the map's own draw
                    // of it, with that draw's neighbours: the shadow pass holds the smoothing back
                    // for its silhouettes, and this copy went on raw over the smoothed tile, a crisp
                    // square at the foot of every fence end, tree and post.
                    bool upscalerWasSuspended = SheetUpscaler.SuspendedForOwnDraw;
                    SheetUpscaler.SuspendedForOwnDraw = false;
                    var drawingTile = MapTileNeighbours.BeginOwnTileDraw(buildingsLayer, x, y);
                    try
                    {
                        DrawOrientedTile(spriteBatch, texture, cast.BaseSrc,
                            Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f, y * 64f)), 4f,
                            cast.BaseOrient, Color.White, Math.Min(1f, depth + 5e-4f));
                    }
                    finally
                    {
                        MapTileNeighbours.EndOwnTileDraw(drawingTile);
                        SheetUpscaler.SuspendedForOwnDraw = upscalerWasSuspended;
                    }
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
            bool propByCoverage = coverage is >= 0.04f and <= 0.95f;
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
            // A see-through tile is a prop by its coverage (a picket, a post), but only while it
            // stands apart. One that is part of a mass of map art wider AND taller than a prop
            // can be is that mass's ragged edge: the leaves round a big bush, the fringe of a
            // cliff. Cast alone, each of them laid down a square shadow of its own, and the bush
            // read as tiles with a shadow each - the author's pictures of 23 September, asking
            // whether we could not tell it was one bush. A fence is one tile tall and keeps its
            // posts' shadows.
            if (propByCoverage)
            {
                int massWidth = 1, massHeight = 1;
                for (int scanX = x - 1; scanX >= 0 && massWidth <= MaxPropSpan && buildingsLayer.Tiles[scanX, y] != null; scanX--) massWidth++;
                for (int scanX = x + 1; scanX < mapWidth && massWidth <= MaxPropSpan && buildingsLayer.Tiles[scanX, y] != null; scanX++) massWidth++;
                for (int scanY = y - 1; scanY >= 0 && massHeight <= MaxPropSpan && buildingsLayer.Tiles[x, scanY] != null; scanY--) massHeight++;
                for (int scanY = y + 1; scanY < mapHeight && massHeight <= MaxPropSpan && buildingsLayer.Tiles[x, scanY] != null; scanY++) massHeight++;
                if (massWidth > MaxPropSpan && massHeight > MaxPropSpan)
                    return NoCast($"skip: cov={coverage:0.00} in a mass {massWidth}x{massHeight}+ → the edge of something bigger, not a prop");
            }
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
                SolidIsland = propBySpan,
                ColumnOf = new int[count],
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
        /// Two solid prop tiles side by side on one row are halves of one thing (a bush, a cactus
        /// two tiles wide) and cast as one: the left tile's column takes the right tile's sources
        /// as its second column, and the right tile only redraws its base.
        /// </summary>
        /// <remarks>Cast apart, each half laid down its own square shadow with its own soft rim,
        /// and where the two rims met there was a line down the middle of the shadow: a bush that
        /// read as tiles, each with a shadow of its own. Reported with a picture on 23 September.
        /// Only solid islands pair; a fence is a row of see-through posts and each post keeps its
        /// own shadow. The span rule already holds an island to two tiles wide.</remarks>
        private void PairWithRight(GameLocation location, xTile.Layers.Layer buildingsLayer, xTile.Layers.Layer frontLayer,
            xTile.Layers.Layer? alwaysFrontLayer, TilePropCast cast, int x, int y, int mapWidth, int mapHeight)
        {
            cast.PairChecked = true;
            if (!cast.SolidIsland || cast.ShadowByLeft)
                return;
            // Whichever half the camera reaches first, the pair comes out the same: a tile whose
            // left neighbour is a solid half of the same art is a right half, whatever order the
            // two are drawn in.
            if (x > 0)
            {
                TilePropCast left = ClassifiedAt(location, buildingsLayer, frontLayer, alwaysFrontLayer, x - 1, y, mapWidth, mapHeight);
                if (left.Casts && left.SolidIsland && ReferenceEquals(left.Texture, cast.Texture) && !left.ShadowByLeft)
                {
                    if (!left.PairChecked)
                        PairWithRight(location, buildingsLayer, frontLayer, alwaysFrontLayer, left, x - 1, y, mapWidth, mapHeight);
                    return;
                }
            }
            if (x + 1 >= mapWidth)
                return;
            TilePropCast right = ClassifiedAt(location, buildingsLayer, frontLayer, alwaysFrontLayer, x + 1, y, mapWidth, mapHeight);
            if (!right.Casts || !right.SolidIsland || right.ShadowByLeft || !ReferenceEquals(right.Texture, cast.Texture))
                return;
            right.PairChecked = true;
            right.ShadowByLeft = true;
            int count = cast.Sources.Length + right.Sources.Length;
            var pair = new TilePropCast
            {
                Casts = true,
                Texture = cast.Texture,
                BaseSrc = cast.BaseSrc,
                BaseOrient = cast.BaseOrient,
                Height = Math.Max(cast.Height, right.Height),
                Columns = 2,
                Sources = [.. cast.Sources, .. right.Sources],
                Levels = [.. cast.Levels, .. right.Levels],
                Orients = [.. cast.Orients, .. right.Orients],
                ColumnOf = new int[count],
                SolidIsland = true,
                BlockedNorth = cast.BlockedNorth || right.BlockedNorth,
                BlockedWest = cast.BlockedWest,
                BlockedEast = right.BlockedEast,
                // Height -2 marks a pair, so it can never share a baked shadow with a single column.
                Key = (cast.Texture, new Rectangle(cast.Key.sourceRect.X * 31 + right.Key.sourceRect.X, count, -1, -2), SpriteEffects.None),
            };
            for (int i = cast.Sources.Length; i < count; i++)
                pair.ColumnOf[i] = 1;
            cast.Pair = pair;
        }

        private TilePropCast ClassifiedAt(GameLocation location, xTile.Layers.Layer buildingsLayer, xTile.Layers.Layer frontLayer,
            xTile.Layers.Layer? alwaysFrontLayer, int x, int y, int mapWidth, int mapHeight)
        {
            int cell = y * mapWidth + x;
            if (!_propCache.TryGetValue(cell, out TilePropCast? cast))
                _propCache[cell] = cast = ClassifyTileProp(location, buildingsLayer, frontLayer, alwaysFrontLayer, x, y, mapWidth, mapHeight);
            return cast;
        }

        /// <summary>The right half of a pair going back on top of the shadow its left half cast,
        /// the same way the left half's own base goes back (see the draw loop).</summary>
        private void RedrawPropBase(SpriteBatch spriteBatch, GameLocation location, xTile.Layers.Layer buildingsLayer, TilePropCast cast, int x, int y)
        {
            bool bodyHere = false;
            try
            {
                bodyHere = location.isCharacterAtTile(new Vector2(x, y)) != null
                    || (Game1.player != null && Game1.player.currentLocation == location
                        && Game1.player.TilePoint.X == x && Game1.player.TilePoint.Y == y);
            }
            catch { }
            float rowY = bodyHere ? y * 64f : (y + 1f) * 64f;
            float depth = MathHelper.Clamp(rowY / 10000f + x * 1e-5f - ShadowDepthBias, 0f, 1f);
            bool upscalerWasSuspended = SheetUpscaler.SuspendedForOwnDraw;
            SheetUpscaler.SuspendedForOwnDraw = false;
            var drawingTile = MapTileNeighbours.BeginOwnTileDraw(buildingsLayer, x, y);
            try
            {
                DrawOrientedTile(spriteBatch, cast.Texture, cast.BaseSrc,
                    Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f, y * 64f)), 4f,
                    cast.BaseOrient, Color.White, Math.Min(1f, depth + 5e-4f));
            }
            finally
            {
                MapTileNeighbours.EndOwnTileDraw(drawingTile);
                SheetUpscaler.SuspendedForOwnDraw = upscalerWasSuspended;
            }
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
        private readonly System.Collections.Generic.Dictionary<(Texture2D texture, Rectangle sourceRect), float> _tileCoverageCache = [];
        private Color[] _tileCoveragePixels = new Color[1024];
        // The whole-tilesheet pixel cache lives in SheetPixels now, shared with the water mask,
        // which asks these same sheets. Reading each prop tile with its own texture.GetData is a
        // separate GPU readback (pipeline flush); walking into a prop-heavy screen fired a burst of
        // them in one frame. Sheets over SheetPixels.PixelCap still fall back to a read per tile.

        private Color[]? CoverageSheetPixels(Texture2D texture)
        {
            // Held by SheetPixels, shared with the water mask, which asks the same tilesheets.
            // Before they shared, a map's sheets were read back twice and held twice.
            return SheetPixels.WholeSheet(texture, "sheet: shadow tile coverage");
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
        private void QueueTileColumnBake((Texture2D texture, Rectangle sourceRect, SpriteEffects effect) key, TilePropCast cast,
            ShadowProjection projection, float blurPixels)
        {
            if (_objectBakeQueue.Count >= ObjectBakeQueueCap || cast.Sources.Length == 0)
                return;
            _objectBakeQueue[key] = new ObjectBakeRequest { Projection = projection, Blur = blurPixels,
                ColumnSources = cast.Sources, ColumnLevels = cast.Levels, ColumnOrients = cast.Orients,
                ColumnOf = cast.Columns > 1 ? cast.ColumnOf : null };
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
            public Rectangle[] Sources = [];
            public int[] Levels = [];
            /// <summary>How the map turns each source, one byte per entry (see MapLayers.Orientation).</summary>
            public byte[] Orients = [];
            /// <summary>How the map turns the BASE tile, for the redraw that puts it back on top.</summary>
            public byte BaseOrient;
            public (Texture2D texture, Rectangle sourceRect, SpriteEffects effect) Key;
            /// <summary>Is there opaque art where the cast would land, leaning each of the three
            /// ways the sun can take it? Answered here because the sun is the only part of this
            /// that changes, and it changes between three fixed choices.</summary>
            public bool BlockedNorth, BlockedWest, BlockedEast;
            /// <summary>A solid island of map art (a bush, a cactus) rather than a see-through prop
            /// such as a fence: only these pair with the tile beside them.</summary>
            public bool SolidIsland;
            /// <summary>How many tiles wide the column is, and which of them each source is in.</summary>
            public int Columns = 1;
            public int[] ColumnOf = [];
            /// <summary>The column this tile casts as one piece with the tile to its right, when
            /// the two are halves of one prop; see <see cref="PairWithRight"/>.</summary>
            public TilePropCast? Pair;
            /// <summary>This tile is the right half of a pair: its shadow is cast by the left half,
            /// and it only redraws its own base.</summary>
            public bool ShadowByLeft;
            public bool PairChecked;
        }

        /// <summary>The classifications this call reads: the set kept for the place being drawn.</summary>
        private System.Collections.Generic.Dictionary<int, TilePropCast> _propCache = [];

        /// <summary>One place's tile classifications, and what they were taken for.</summary>
        private sealed class PropCacheForPlace
        {
            internal readonly System.Collections.Generic.Dictionary<int, TilePropCast> Casts = [];
            internal xTile.Map? Map;
            /// <summary>Day the classification was taken on. Map art is edited by content packs at
            /// the day boundary (seasonal sheets, festival layouts) and buildings finish overnight,
            /// so a new day is the one moment the answers can change without the map being replaced.</summary>
            internal int Day = -1;
            internal long LastAskedFor;
        }

        /// <summary>The classifications kept per place, a few places.
        ///
        /// <para>It was one set, thrown away whenever the place asked for was not the place it held.
        /// Two screens in two different places took turns, so every call cleared it and classified
        /// every tile on screen again, for both screens, every frame: the work the cache exists to
        /// save, done twice as often as with no cache at all. Found 13/9 beside the solid-tile
        /// texture of the shadow patch, which had the same one slot.</para></summary>
        private readonly System.Collections.Generic.Dictionary<string, PropCacheForPlace> _propCacheByPlace = [];
        private long _propCacheAsks;
        private const int PropCachePlacesKept = 4;

        private System.Collections.Generic.Dictionary<int, TilePropCast> PropCacheFor(GameLocation location)
        {
            string place = location.NameOrUniqueName;
            if (!_propCacheByPlace.TryGetValue(place, out PropCacheForPlace? kept))
                _propCacheByPlace[place] = kept = new PropCacheForPlace();
            kept.LastAskedFor = ++_propCacheAsks;
            if (!SDVRadiance.LiveScreens.SameMapSize(location.map, kept.Map) || Game1.Date.TotalDays != kept.Day)
            {
                kept.Casts.Clear();
                kept.Map = location.map;
                kept.Day = Game1.Date.TotalDays;
            }
            while (_propCacheByPlace.Count > PropCachePlacesKept)
            {
                string? leastWanted = null;
                long oldest = long.MaxValue;
                foreach (var pair in _propCacheByPlace)
                    if (pair.Value.LastAskedFor < oldest)
                    {
                        oldest = pair.Value.LastAskedFor;
                        leastWanted = pair.Key;
                    }
                if (leastWanted == null)
                    break;
                _propCacheByPlace.Remove(leastWanted);
            }
            return kept.Casts;
        }

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
            byte[]? orientations, int count, ShadowProjection projection, float blurPixels,
            out RenderTarget2D renderTarget, out Vector2 feetInRenderTarget, RenderTarget2D? into = null, int[]? columnOf = null)
        {
            renderTarget = null!;
            feetInRenderTarget = default;
            int levels = 0, columns = 1;
            for (int i = 0; i < count; i++)
            {
                levels = Math.Max(levels, tileLevels[i] + 1);
                if (columnOf != null && i < columnOf.Length)
                    columns = Math.Max(columns, columnOf[i] + 1);
            }
            // Map art is 16 px a tile, so the column's own size is levels of that; the bake scale
            // turns it into slot texels exactly as it does for a sprite. This path used to be the
            // one place a refusal still happened INSIDE the bake: a column too wide for its slot
            // (which the lean alone can do, at the top of the shadow-length slider) was refused,
            // never cached, and so re-queued, re-attempted and re-refused every frame for the rest
            // of the session - the very waste the sprite path stopped paying, left in the half
            // nobody looked at. The ladder ends it the same way, and one fit test replaces two that
            // disagreed about whether the blur counts.
            const float tileSource = 16f;
            // A column is a sprite like any other now: sixteen wide, as many tiles tall as it has,
            // pinned at the middle of its bottom edge. Laid down by the same ShadowProjection every
            // other caster uses, so it answers to the same rules and there is one place left in the
            // mod that decides what the sun does to a silhouette.
            // A pair (see PairWithRight) is as many tiles wide as it has columns, pinned the same way.
            var columnRect = new Rectangle(0, 0, (int)(columns * tileSource), (int)(levels * tileSource));
            var columnOrigin = new Vector2(columns * tileSource * 0.5f, levels * tileSource);
            if (count <= 0 || !ChooseBakeFit(columnRect, columnOrigin, projection, blurPixels, into,
                                             out int columnSlotClass, out float scale, out float blurTexels, out float rimTexels,
                                             out float left, out float right, out float top, out float bottom))
            {
                NoteColumnRefusal($"{levels}-tile column laid {projection.AlongX:0.00},{projection.AlongY:0.00} fits no slot at any bake scale");
                return false;
            }
            float tileTexels = tileSource * scale;
            float columnHeight = levels * tileTexels;
            _lastBakeClass = columnSlotClass;
            _lastBakeScale = scale;
            renderTarget = into ?? ObjectBakeScratch(graphicsDevice, columnSlotClass);
            feetInRenderTarget = new Vector2(
                (float)Math.Round(renderTarget.Width * 0.5f - (left + right) * 0.5f),
                (float)Math.Round(renderTarget.Height - bottom - rimTexels - 1f));
            Matrix lean = projection.About(feetInRenderTarget);
            try
            {
                graphicsDevice.SetRenderTarget(renderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                for (int i = 0; i < count; i++)
                {
                    int column = columnOf != null && i < columnOf.Length ? columnOf[i] : 0;
                    DrawOrientedTile(_renderTargetSpriteBatch, texture, sources[i],
                        new Vector2(feetInRenderTarget.X - tileTexels * columns * 0.5f + tileTexels * column, feetInRenderTarget.Y - tileTexels * (tileLevels[i] + 1)),
                        scale, orientations != null && i < orientations.Length ? orientations[i] : (byte)0, Color.Black, 0f);
                }
                _renderTargetSpriteBatch.End();
                WhitenBake(graphicsDevice, renderTarget.Bounds);
                // The fade rides the same matrix as the silhouette. Drawn upright over a slot the
                // projection has already laid down, it would fade rows the column no longer has.
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                _renderTargetSpriteBatch.Draw(_propGradientTexture!,
                    new Rectangle((int)(feetInRenderTarget.X - tileTexels * columns * 0.5f), (int)(feetInRenderTarget.Y - columnHeight),
                                  (int)(tileTexels * columns), (int)columnHeight), Color.White);
                _renderTargetSpriteBatch.End();
                // Screen pixels in the slot now, so the rim is stamped the way a sprite's is, held
                // to a third of each of the column's own two extents.
                float alongPerHeight = (float)Math.Sqrt(projection.AlongX * projection.AlongX + projection.AlongY * projection.AlongY);
                float acrossPerWidth = (float)Math.Sqrt(projection.AcrossX * projection.AcrossX + projection.AcrossY * projection.AcrossY);
                float rimRoot = (float)Math.Sqrt(PenumbraElongation(alongPerHeight));
                var columnRim = new Vector2(
                    PenumbraHeldToShadow(blurTexels / rimRoot, tileTexels * columns * acrossPerWidth),
                    PenumbraHeldToShadow(blurTexels * rimRoot, columnHeight * alongPerHeight));
                BlurSlotInPlace(graphicsDevice, renderTarget, blurTexels, feetInRenderTarget,
                    new Vector2(projection.AlongX, projection.AlongY), alongPerHeight, columnRim);
                _lastBakeContent = ContentBounds(feetInRenderTarget, left, right, top, bottom, rimTexels, renderTarget.Width, renderTarget.Height);
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
