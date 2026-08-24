using System;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer — the "why is there no shadow here" report (<c>radiance_shadows</c>).
    ///
    /// <para>
    /// Written after two fixes aimed from reading the code both missed. A caster reaches the
    /// screen through a chain of independent gates (the draw path, the event's own draw rules,
    /// HideShadow, water, seating), and from a screenshot every one of them looks the same: no
    /// shadow. This prints the chain for every character and object on screen and names the gate
    /// that stopped each one, so the next fix is aimed at a measured cause.
    /// </para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        internal static string Report(ModConfig config, bool wholeMap)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return "no location loaded";

            var report = new StringBuilder();
            Event? ev = Game1.CurrentEvent;
            report.AppendLine($"[shadows] location={location.NameOrUniqueName} outdoors={location.IsOutdoors} time={Game1.timeOfDay} season={Game1.season}");
            report.AppendLine($"[shadows] path={(SunCasts() ? "SUN" : "PER-LIGHT")} shouldCast={ShouldCast(config)} strength={config.DirectionalShadowStrength:0.00} objectsEnabled={config.DirectionalShadowObjects}");
            AppendSunGeometry(report, config);
            // The event flags decide who the game is drawing at all. Every one of them has caught
            // an assumption out at least once, so all of them are printed, not just the relevant one.
            report.AppendLine($"[shadows] eventUp={Game1.eventUp} currentEvent={(ev != null)} isFestival={ev?.isFestival} "
                        + $"showWorldCharacters={ev?.showWorldCharacters} showGroundObjects={location.currentEvent?.showGroundObjects} "
                        + $"actors={ev?.actors?.Count ?? 0} residents={location.characters.Count}");

            var viewport = Game1.viewport;
            int vx0 = viewport.X / 64 - 1, vx1 = (viewport.X + viewport.Width) / 64 + 1;
            int vy0 = viewport.Y / 64 - 1, vy1 = (viewport.Y + viewport.Height) / 64 + 1;

            // The window the report walks. Screen-sized was too tight to trust: a shadow leans, so
            // its caster can sit outside the frame the shadow lands in, and "not in the list" then
            // means nothing. Default reaches well past the frame; `all` drops the bound entirely.
            const int Pad = 20;
            int tx0 = wholeMap ? 0 : vx0 - Pad, tx1 = wholeMap ? location.Map?.Layers[0]?.LayerWidth ?? vx1 : vx1 + Pad;
            int ty0 = wholeMap ? 0 : vy0 - Pad, ty1 = wholeMap ? location.Map?.Layers[0]?.LayerHeight ?? vy1 : vy1 + Pad;
            var w = new ScanWindow(viewport, vx0, vx1, vy0, vy1, tx0, tx1, ty0, ty1);
            report.AppendLine($"[shadows] scanning tiles {tx0},{ty0} to {tx1},{ty1} ({(wholeMap ? "whole map" : $"screen +{Pad} tiles")}); "
                        + "'*' marks something off screen");
            // Where each thing sits ON SCREEN, and what the cursor is pointing at. During a
            // cutscene the player cannot walk onto a suspicious shadow to identify it, so the
            // mouse is the only pointer available - hover the shadow, run the command, compare.
            Vector2 cur = Game1.currentCursorTile;
            report.AppendLine($"[shadows] cursor tile={cur.X},{cur.Y} player tile={Game1.player?.TilePoint} viewport={viewport.X},{viewport.Y}");

            AppendCharacters(report, location, w);
            AppendOtherFarmers(report, location, w);
            AppendEntities(report, location, w);

            return report.ToString().TrimEnd();
        }

        /// <summary>The bounds one report walks, and the four questions every section asks about a
        /// tile. These were local functions, which is why the sections could not be separated.</summary>
        private readonly struct ScanWindow
        {
            public readonly xTile.Dimensions.Rectangle Viewport;
            private readonly int _vx0, _vx1, _vy0, _vy1;
            public readonly int Tx0, Tx1, Ty0, Ty1;

            public ScanWindow(xTile.Dimensions.Rectangle viewport, int vx0, int vx1, int vy0, int vy1,
                              int tx0, int tx1, int ty0, int ty1)
            {
                Viewport = viewport;
                _vx0 = vx0; _vx1 = vx1; _vy0 = vy0; _vy1 = vy1;
                Tx0 = tx0; Tx1 = tx1; Ty0 = ty0; Ty1 = ty1;
            }

            /// <summary>Is the tile inside the frame the player can see?</summary>
            public bool Visible(int x, int y) => x >= _vx0 && x <= _vx1 && y >= _vy0 && y <= _vy1;

            /// <summary>Is the tile inside the window this report walks (wider than the frame: a
            /// shadow leans, so its caster can sit outside the frame the shadow lands in).</summary>
            public bool OnScreen(int x, int y) => x >= Tx0 && x <= Tx1 && y >= Ty0 && y <= Ty1;

            /// <summary>Where a world point sits on screen, so a shadow can be found by hovering it.</summary>
            public string Screen(float wx, float wy)
            {
                Vector2 p = Game1.GlobalToLocal(Viewport, new Vector2(wx, wy));
                return $"screen={(int)p.X},{(int)p.Y}";
            }

            /// <summary>The off-screen marker printed beside each entry.</summary>
            public string Mark(int x, int y) => Visible(x, y) ? " " : "*";
        }

        /// <summary>Every character the pass would walk, and the verdict it would reach.</summary>
        private static void AppendCharacters(StringBuilder report, GameLocation location, ScanWindow w)
        {
            report.AppendLine("[shadows] characters (verdict = what the shadow pass does with it):");
            int shown = 0;
            foreach (NPC npc in CharactersIn(location))
            {
                if (npc == null)
                    continue;
                Point t = npc.TilePoint;
                if (!w.OnScreen(t.X, t.Y))
                    continue;
                shown++;
                string verdict =
                    npc.IsInvisible ? "SKIP invisible"
                    : ShadowHiddenFor(npc) ? "SKIP HideShadow"
                    : npc.swimming.Value ? "SKIP swimming"
                    : npc.Sprite?.Texture == null ? "SKIP no sprite"
                    : OnOpenWater(location, t) ? "SKIP on open water"
                    : IsSeated(npc) ? "contact pool only (seated)"
                    : "CASTS";
                // The numbers behind the anchor, so a shadow that sits away from its owner can be
                // checked against where the game actually draws the sprite instead of by eye.
                // spriteTop/spriteBottom are the sprite's real screen edges (NPC.draw pins the
                // sprite at gameAnchor with origin SpriteHeight*3/4, so the top is 3*SpriteHeight
                // above it); anchorY is where the shadow is pinned. A correct anchor sits on the
                // feet, which for a stretched sprite is well above spriteBottom.
                Vector2 anchorPt = Game1.GlobalToLocal(w.Viewport, new Vector2(
                    npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom));
                Vector2 gameAnchor = npc.getLocalPosition(w.Viewport)
                    + new Vector2(npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Height / 2f);
                float sprTop = gameAnchor.Y - (npc.Sprite?.SpriteHeight ?? 0) * 3f;
                float sprBottom = sprTop + (npc.Sprite?.SourceRect.Height ?? 0) * 4f;
                report.AppendLine($"{w.Mark(t.X, t.Y)} {npc.Name,-16} tile={t.X},{t.Y} anchorY={(int)anchorPt.Y} "
                            + $"spriteTop={(int)sprTop} spriteBottom={(int)sprBottom} "
                            + $"originY={(int)((anchorPt.Y - sprTop) / 4f)}/{npc.Sprite?.SourceRect.Height ?? 0} "
                            + $"spriteH={npc.Sprite?.SpriteHeight ?? 0} srcH={npc.Sprite?.SourceRect.Height ?? 0} "
                            + $"eventActor={npc.EventActor} simpleNonVillager={npc.SimpleNonVillagerNPC} "
                            + $"hideShadow={npc.HideShadow} layingDown={npc.layingDown} drawOffset={npc.drawOffset.X},{npc.drawOffset.Y} "
                            + $"water={OnWater(location, t)} "
                            + $"openWater={OnOpenWater(location, t)} -> {verdict}");
            }
            if (shown == 0)
                report.AppendLine("  (none — if you can see NPCs, they are not in the list the pass reads)");
        }

        /// <summary>The other players: a separate list with a separate bake, which this report did
        /// not know existed until a split screen asked it about a partner with no shadow.</summary>
        private static void AppendOtherFarmers(StringBuilder report, GameLocation location, ScanWindow w)
        {
            // The other players are a separate list with a separate bake, and this report did not
            // know they existed. That is exactly the case it was asked about first: on a split
            // screen the partner had no shadow, and the answer here was an empty character list,
            // which says nothing at all. A diagnostic that cannot see half the casters is worse
            // than no diagnostic, because it reads as evidence.
            report.AppendLine($"[shadows] other farmers on this screen (screen {StardewModdingAPI.Context.ScreenId}, "
                        + $"local player {Game1.player?.UniqueMultiplayerID}):");
            int farmersShown = 0;
            foreach (var (who, hasBake, ready, hasMask, hasColour, colourFresh) in DescribeRemoteFarmers(location))
            {
                farmersShown++;
                Point t = who.TilePoint;
                string verdict =
                    !hasBake ? "SKIP no bake for this farmer yet"
                    : who.FarmerRenderer == null || who.FarmerSprite == null ? "SKIP nothing to draw them from"
                    : who.swimming.Value ? "SKIP swimming"
                    : who.isRidingHorse() ? "SKIP riding (the horse casts)"
                    : OnOpenWater(location, t) ? "SKIP on open water"
                    : IsSeated(who) ? "contact pool only (seated)"
                    : !hasMask ? "SKIP bake has no mask target"
                    : !ready ? "SKIP bake not ready"
                    : "CASTS";
                report.AppendLine($"{w.Mark(t.X, t.Y)} {who.Name,-16} id={who.UniqueMultiplayerID} tile={t.X},{t.Y} "
                            + $"{w.Screen(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom)} "
                            + $"onScreen={w.Visible(t.X, t.Y)} bake={(hasBake ? "yes" : "NO")} "
                            + $"ready={ready} mask={hasMask} colour={hasColour} colourFresh={colourFresh} "
                            + $"-> {verdict}");
            }
            if (farmersShown == 0)
                report.AppendLine("  (none — this screen's own player is excluded by design, so a solo game is empty here)");
            report.AppendLine($"  bakes held: {RemoteFarmerBakeCount}, farmers the game lists here: {location.farmers.Count}, "
                        + $"images published to the water passes this frame: {OtherFarmerImages.Count}");
            report.AppendLine($"  what the last bake pass itself saw: {RemoteFarmerLastPassView}");
            report.AppendLine($"  bake pass entered {RemoteFarmerPreparePasses} time(s) since launch"
                        + (RemoteFarmerPreparePasses == 0
                            ? "  <- IT IS NEVER BEING CALLED, which is a different fault entirely"
                            : RemoteFarmerLastSkip != null ? $", last gave up because: {RemoteFarmerLastSkip}" : ""));
        }

        /// <summary>Everything else that can cast: the event gates that decide what the game draws
        /// at all, then objects, furniture, clumps, plants, animals and critters.</summary>
        private static void AppendEntities(StringBuilder report, GameLocation location, ScanWindow w)
        {
            // Objects are the other half: an orphan shadow means we cast for something the game is
            // not drawing, so the gate value matters as much as the item list.
            bool eventUp = Game1.eventUp;
            bool showGround = location.currentEvent != null && location.currentEvent.showGroundObjects;
            report.AppendLine($"[shadows] object gates: objectsDrawn={(!eventUp || showGround)} "
                        + $"furnitureDrawn={(!eventUp || location is Farm || location is StardewValley.Locations.FarmHouse)} "
                        + $"clumpsDrawn={!(location is StardewValley.Locations.Woods && eventUp && !showGround)}");
            report.AppendLine("[shadows] objects, furniture, clumps, plants, animals and critters:");
            int objs = 0;
            foreach (var kv in location.objects.Pairs)
            {
                Vector2 tile = kv.Key;
                SObject o = kv.Value;
                if (o == null || !w.OnScreen((int)tile.X, (int)tile.Y))
                    continue;
                objs++;
                string kind = o.bigCraftable.Value ? "bigCraftable" : o.IsSpawnedObject ? "forage" : "placed";
                // The second gate in Object.draw, and the one that hid the Squid Fest clam while
                // showGroundObjects said the objects were being drawn.
                bool walked = Game1.eventUp && (Game1.CurrentEvent?.isTileWalkedOn((int)tile.X, (int)tile.Y) ?? false);
                report.AppendLine($"{w.Mark((int)tile.X, (int)tile.Y)} {o.Name,-20} tile={tile.X},{tile.Y} {w.Screen(tile.X * 64 + 32, tile.Y * 64 + 64)} {kind} passable={o.isPassable()} "
                            + $"tempInvisible={o.isTemporarilyInvisible} fragility={o.Fragility} "
                            + $"eventWalkedOn={walked}{(walked ? " -> game hides it, no shadow" : "")}");
            }
            foreach (Furniture f in location.furniture)
            {
                if (f == null || !w.OnScreen((int)f.TileLocation.X, (int)f.TileLocation.Y))
                    continue;
                objs++;
                report.AppendLine($"{w.Mark((int)f.TileLocation.X, (int)f.TileLocation.Y)} {f.Name,-20} tile={f.TileLocation.X},{f.TileLocation.Y} furniture type={f.furniture_type.Value}");
            }
            foreach (ResourceClump c in location.resourceClumps)
            {
                if (c == null || !w.OnScreen((int)c.Tile.X, (int)c.Tile.Y))
                    continue;
                objs++;
                report.AppendLine($"{w.Mark((int)c.Tile.X, (int)c.Tile.Y)} clump {c.parentSheetIndex.Value,-14} tile={c.Tile.X},{c.Tile.Y}");
            }
            foreach (var ltf in location.largeTerrainFeatures)
            {
                if (ltf == null || !w.OnScreen((int)ltf.Tile.X, (int)ltf.Tile.Y))
                    continue;
                objs++;
                report.AppendLine($"{w.Mark((int)ltf.Tile.X, (int)ltf.Tile.Y)} {ltf.GetType().Name,-20} tile={ltf.Tile.X},{ltf.Tile.Y} largeTerrainFeature");
            }
            // terrainFeatures is tile-keyed and can be huge, so it is walked by the visible window.
            for (int y = w.Ty0; y <= w.Ty1; y++)
            for (int x = w.Tx0; x <= w.Tx1; x++)
            {
                if (!location.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf) || tf is Flooring)
                    continue;
                objs++;
                string extra = tf switch
                {
                    Tree tr => $"stage={tr.growthStage.Value} stump={tr.stump.Value}",
                    FruitTree ftr => $"stage={ftr.growthStage.Value} stump={ftr.stump.Value}",
                    HoeDirt { crop: { } cr } => $"crop dead={cr.dead.Value} forage={cr.forageCrop.Value}",
                    _ => "",
                };
                report.AppendLine($"{w.Mark(x, y)} {tf.GetType().Name,-20} tile={x},{y} {extra}");
            }
            // Critters and animals cast too, and neither is in location.objects. A beach in winter has
            // seagulls, which is the obvious candidate for a shadow whose owner is not on the list.
            foreach (FarmAnimal a in AnimalsIn(location))
            {
                if (a == null || !w.OnScreen(a.TilePoint.X, a.TilePoint.Y))
                    continue;
                objs++;
                report.AppendLine($"{w.Mark(a.TilePoint.X, a.TilePoint.Y)} {a.Name,-20} tile={a.TilePoint.X},{a.TilePoint.Y} {w.Screen(a.Position.X, a.GetBoundingBox().Bottom)} animal");
            }
            if (location.critters != null)
                foreach (var c in location.critters)
                {
                    if (c == null)
                        continue;
                    objs++;
                    report.AppendLine($"{w.Mark((int)(c.position.X / 64), (int)(c.position.Y / 64))} {c.GetType().Name,-20} tile={(int)(c.position.X / 64)},{(int)(c.position.Y / 64)} "
                                + $"{w.Screen(c.position.X, c.position.Y)} critter");
                }
            if (objs == 0)
                report.AppendLine("  (none — an orphan shadow here comes from something not in these lists)");
        }

        /// <summary>
        /// The angle every kind of shadow is ACTUALLY drawn at, next to the sun's own.
        ///
        /// <para>
        /// A character is drawn as a rotation of its silhouette; everything else is drawn as a
        /// shear about the feet row plus a vertical squash. Those two are supposed to land on the
        /// same on-screen angle, and reading the code says they do, so when someone reports that a
        /// crop's shadow does not point where a person's does, the code is not what to re-read.
        /// This prints the number each kind ends up at, so the report can be checked against the
        /// screen instead of against an argument. <c>floor</c> marks a kind whose squash hit the
        /// 0.15 clamp, which is the one thing that CAN bend an angle away from the sun's.
        /// </para>
        /// </summary>
        private static void AppendSunGeometry(StringBuilder report, ModConfig config)
        {
            ComputeSun(out float rot, out float stretch, out float _);
            // The drawn value is eased toward this one over about a second, so a report taken in
            // the middle of a shower reads a few percent off. Nothing here turns on that.
            float overcast = OvercastNow();
            float lengthScale = Math.Max(0.1f, config.DirectionalShadowLength)
                              * MathHelper.Lerp(1f, OvercastLength, overcast);
            float sunStretch = stretch * lengthScale;
            const float Deg = 180f / (float)Math.PI;
            report.AppendLine($"[shadows] sun rot={rot * Deg:0.0}deg stretch={sunStretch:0.00} lengthScale={lengthScale:0.00} "
                        + $"overcast={overcast:0.00}");
            report.AppendLine($"[shadows] geometry: character (rotated) angle={rot * Deg:0.0}deg stretch={sunStretch:0.00}");

            // Read from the live per-kind settings rather than repeating them. The numbers here
            // were copied by hand and had drifted: it still printed a crop cap of 0.55 and an
            // object cap of 0.5 long after the draw pass had moved both to 1.0, so the one tool
            // for answering "why is this shadow that long" was answering about other code.
            void Kind(ShadowKind kind)
            {
                float cap = LengthCapFor(config, kind);
                // The lean this kind is drawn at, which is the sun's own only while its lean is 1.
                // Reading rot straight here would have gone on printing one angle for everything
                // while the screen showed several, and that is the report this table exists for.
                float lean = LeanFor(config, kind);
                float kindRot = rot * lean;
                float st = Math.Min(sunStretch, cap * lengthScale);
                float shear = -(float)Math.Sin(kindRot) * st;
                float raw = st * (float)Math.Cos(kindRot);
                float sy = Math.Max(0.15f, raw);
                float angle = (float)Math.Atan2(-shear, sy) * Deg;
                report.AppendLine($"[shadows]   {kind,-12} cap={cap:0.00} soft={SoftnessFor(config, kind):0.00}x lean={lean:0.00}x "
                            + $"stretch={st:0.00} shear={shear:0.000} "
                            + $"squash={sy:0.000}{(raw < 0.15f ? " floor" : "")} angle={angle:0.0}deg");
            }
            foreach (ShadowKind kind in Enum.GetValues<ShadowKind>())
                Kind(kind);
            // What the two geometries make of the same sun: where one pixel of width and one of
            // height land, and how wide and tall a sprite comes out once laid down. The game's own
            // blob is printed beside the foreshortening so the default can be checked against the
            // one oval the art itself commits to.
            float foreshortening = config.ShadowGroundForeshortening;
            float characterForeshortening = config.ShadowCharacterGroundForeshortening;
            Texture2D? blob = Game1.shadowTexture;
            report.AppendLine($"[shadows] ground foreshortening={foreshortening:0.00} for things, {characterForeshortening:0.00} for people"
                        + (blob != null ? $" (the game's own blob is {blob.Width}x{blob.Height})" : ""));
            void Laid(string what, ShadowGeometry geometry, float w, float h, float cap)
            {
                float st = Math.Min(sunStretch, cap * lengthScale);
                ShadowProjection projection = geometry == ShadowGeometry.Card
                    ? ShadowProjection.ForCard(rot, st)
                    : ShadowProjection.ForSolid(rot, st, foreshortening);
                projection.Bounds(w, h, w / 2f, h, out float left, out float right, out float top, out float bottom);
                report.AppendLine($"[shadows]   {what,-12} {geometry,-5} across=({projection.AcrossX:0.00},{projection.AcrossY:0.00}) "
                            + $"along=({projection.AlongX:0.00},{projection.AlongY:0.00}) "
                            + $"laid {right - left:0}x{bottom - top:0}px from {w:0}x{h:0}");
            }
            Laid("a crop", ShadowGeometry.Solid, 16f, 32f, config.ShadowLengthCrops);
            Laid("a bush", ShadowGeometry.Solid, 32f, 32f, config.ShadowLengthBushes);
            Laid("a tree", ShadowGeometry.Solid, 48f, 96f, config.ShadowLengthTrees);
            Laid("a fence", ShadowGeometry.Card, 16f, 32f, config.ShadowLengthObjects);
            // People are not baked through the projection; they are rotated and scaled by the
            // nearest thing to it, so what to print is that scale, and what it would have been on
            // the ground's own number.
            float personAcross = ShadowProjection.ForSolid(rot, sunStretch, characterForeshortening).AcrossScaleForRotation();
            float personAcrossOnGround = ShadowProjection.ForSolid(rot, sunStretch, foreshortening).AcrossScaleForRotation();
            report.AppendLine($"[shadows]   a person     Solid rotate+scale, width x{personAcross:0.00} "
                        + $"(x{personAcrossOnGround:0.00} on the ground's own)");
        }
    }
}
