using System;
using System.Text;
using Microsoft.Xna.Framework;
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
            // The event flags decide who the game is drawing at all. Every one of them has caught
            // an assumption out at least once, so all of them are printed, not just the relevant one.
            report.AppendLine($"[shadows] eventUp={Game1.eventUp} currentEvent={(ev != null)} isFestival={ev?.isFestival} "
                        + $"showWorldCharacters={ev?.showWorldCharacters} showGroundObjects={location.currentEvent?.showGroundObjects} "
                        + $"actors={ev?.actors?.Count ?? 0} residents={location.characters.Count}");

            var viewport = Game1.viewport;
            int vx0 = viewport.X / 64 - 1, vx1 = (viewport.X + viewport.Width) / 64 + 1;
            int vy0 = viewport.Y / 64 - 1, vy1 = (viewport.Y + viewport.Height) / 64 + 1;
            bool Visible(int x, int y) => x >= vx0 && x <= vx1 && y >= vy0 && y <= vy1;

            // The window the report walks. Screen-sized was too tight to trust: a shadow leans, so
            // its caster can sit outside the frame the shadow lands in, and "not in the list" then
            // means nothing. Default reaches well past the frame; `all` drops the bound entirely.
            const int Pad = 20;
            int tx0 = wholeMap ? 0 : vx0 - Pad, tx1 = wholeMap ? location.Map?.Layers[0]?.LayerWidth ?? vx1 : vx1 + Pad;
            int ty0 = wholeMap ? 0 : vy0 - Pad, ty1 = wholeMap ? location.Map?.Layers[0]?.LayerHeight ?? vy1 : vy1 + Pad;
            bool OnScreen(int x, int y) => x >= tx0 && x <= tx1 && y >= ty0 && y <= ty1;
            report.AppendLine($"[shadows] scanning tiles {tx0},{ty0} to {tx1},{ty1} ({(wholeMap ? "whole map" : $"screen +{Pad} tiles")}); "
                        + "'*' marks something off screen");
            // Where each thing sits ON SCREEN, and what the cursor is pointing at. During a
            // cutscene the player cannot walk onto a suspicious shadow to identify it, so the
            // mouse is the only pointer available - hover the shadow, run the command, compare.
            string Screen(float wx, float wy)
            {
                Vector2 p = Game1.GlobalToLocal(viewport, new Vector2(wx, wy));
                return $"screen={(int)p.X},{(int)p.Y}";
            }
            string Mark(int x, int y) => Visible(x, y) ? " " : "*";
            Vector2 cur = Game1.currentCursorTile;
            report.AppendLine($"[shadows] cursor tile={cur.X},{cur.Y} player tile={Game1.player?.TilePoint} viewport={viewport.X},{viewport.Y}");

            report.AppendLine("[shadows] characters (verdict = what the shadow pass does with it):");
            int shown = 0;
            foreach (NPC npc in CharactersIn(location))
            {
                if (npc == null)
                    continue;
                Point t = npc.TilePoint;
                if (!OnScreen(t.X, t.Y))
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
                Vector2 anchorPt = Game1.GlobalToLocal(viewport, new Vector2(
                    npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom));
                Vector2 gameAnchor = npc.getLocalPosition(viewport)
                    + new Vector2(npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Height / 2f);
                float sprTop = gameAnchor.Y - (npc.Sprite?.SpriteHeight ?? 0) * 3f;
                float sprBottom = sprTop + (npc.Sprite?.SourceRect.Height ?? 0) * 4f;
                report.AppendLine($"{Mark(t.X, t.Y)} {npc.Name,-16} tile={t.X},{t.Y} anchorY={(int)anchorPt.Y} "
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
                if (o == null || !OnScreen((int)tile.X, (int)tile.Y))
                    continue;
                objs++;
                string kind = o.bigCraftable.Value ? "bigCraftable" : o.IsSpawnedObject ? "forage" : "placed";
                // The second gate in Object.draw, and the one that hid the Squid Fest clam while
                // showGroundObjects said the objects were being drawn.
                bool walked = Game1.eventUp && (Game1.CurrentEvent?.isTileWalkedOn((int)tile.X, (int)tile.Y) ?? false);
                report.AppendLine($"{Mark((int)tile.X, (int)tile.Y)} {o.Name,-20} tile={tile.X},{tile.Y} {Screen(tile.X * 64 + 32, tile.Y * 64 + 64)} {kind} passable={o.isPassable()} "
                            + $"tempInvisible={o.isTemporarilyInvisible} fragility={o.Fragility} "
                            + $"eventWalkedOn={walked}{(walked ? " -> game hides it, no shadow" : "")}");
            }
            foreach (Furniture f in location.furniture)
            {
                if (f == null || !OnScreen((int)f.TileLocation.X, (int)f.TileLocation.Y))
                    continue;
                objs++;
                report.AppendLine($"{Mark((int)f.TileLocation.X, (int)f.TileLocation.Y)} {f.Name,-20} tile={f.TileLocation.X},{f.TileLocation.Y} furniture type={f.furniture_type.Value}");
            }
            foreach (ResourceClump c in location.resourceClumps)
            {
                if (c == null || !OnScreen((int)c.Tile.X, (int)c.Tile.Y))
                    continue;
                objs++;
                report.AppendLine($"{Mark((int)c.Tile.X, (int)c.Tile.Y)} clump {c.parentSheetIndex.Value,-14} tile={c.Tile.X},{c.Tile.Y}");
            }
            foreach (var ltf in location.largeTerrainFeatures)
            {
                if (ltf == null || !OnScreen((int)ltf.Tile.X, (int)ltf.Tile.Y))
                    continue;
                objs++;
                report.AppendLine($"{Mark((int)ltf.Tile.X, (int)ltf.Tile.Y)} {ltf.GetType().Name,-20} tile={ltf.Tile.X},{ltf.Tile.Y} largeTerrainFeature");
            }
            // terrainFeatures is tile-keyed and can be huge, so it is walked by the visible window.
            for (int y = ty0; y <= ty1; y++)
            for (int x = tx0; x <= tx1; x++)
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
                report.AppendLine($"{Mark(x, y)} {tf.GetType().Name,-20} tile={x},{y} {extra}");
            }
            // Critters and animals cast too, and neither is in location.objects. A beach in winter has
            // seagulls, which is the obvious candidate for a shadow whose owner is not on the list.
            foreach (FarmAnimal a in AnimalsIn(location))
            {
                if (a == null || !OnScreen(a.TilePoint.X, a.TilePoint.Y))
                    continue;
                objs++;
                report.AppendLine($"{Mark(a.TilePoint.X, a.TilePoint.Y)} {a.Name,-20} tile={a.TilePoint.X},{a.TilePoint.Y} {Screen(a.Position.X, a.GetBoundingBox().Bottom)} animal");
            }
            if (location.critters != null)
                foreach (var c in location.critters)
                {
                    if (c == null)
                        continue;
                    objs++;
                    report.AppendLine($"{Mark((int)(c.position.X / 64), (int)(c.position.Y / 64))} {c.GetType().Name,-20} tile={(int)(c.position.X / 64)},{(int)(c.position.Y / 64)} "
                                + $"{Screen(c.position.X, c.position.Y)} critter");
                }
            if (objs == 0)
                report.AppendLine("  (none — an orphan shadow here comes from something not in these lists)");

            return report.ToString().TrimEnd();
        }
    }
}
