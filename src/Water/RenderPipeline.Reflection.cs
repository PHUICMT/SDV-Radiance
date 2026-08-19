using System;
using System.Collections.Generic;
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
        /// <summary>Source rows per slice of a mirrored sprite, taken from the config once per
        /// build so the stamp helpers do not each need it passed down. See
        /// <see cref="ModConfig.WaterReflectFadeRows"/> for what it trades and what it measured.</summary>
        private int _mirrorSliceRows = 4;

        private RenderTarget2D? _reflectionRenderTarget;
        internal bool ReflectRTReady;
        internal bool ReflectRTHasPlayer;   // player stamped this frame → the shader retires
                                            // its wading-silhouette fallback

        /// <summary>Bake the flipped-entity reflection layer for this frame. Called from
        /// Display.RenderingWorld right after the sprite mask bake (the only safe spot
        /// for render-target swaps).</summary>
        public void BakeWaterReflection(ModConfig config)
        {
            long t0 = FrameCost.Begin(FrameCost.Part.EntityReflection);
            BakeWaterReflectionCore(config);
            double ms = FrameCost.End(FrameCost.Part.EntityReflection, t0);
            if (_timingOn) AccumulateBuildMilliseconds(5, ms);
        }

        /// <summary>
        /// Stamp one farmer's colour bake into the mirror, flipped below their feet. The bake pins
        /// the feet at (RtW/2, RtH-8), so flipped that anchor is 8px from the TOP; the sprite is
        /// positioned so the flipped feet meet it. Sliced into 16-row bands to get the same
        /// feet-to-head fade every other body in here is given.
        /// </summary>
        /// <summary>Scratch target the held tool is drawn into before it is mirrored. Small: it
        /// only has to hold one swing around one body.</summary>
        private RenderTarget2D? _toolMirrorRenderTarget;
        private const int ToolRtSize = 192;
        /// <summary>Where the player's feet ended up inside that target, so the mirror hangs from
        /// the same line a body does.</summary>
        private Vector2 _toolFeetInRenderTarget;

        /// <summary>
        /// Draw the tool the player is swinging into a scratch target, upright, positioned so the
        /// feet land at a known point.
        ///
        /// <para>
        /// This is the second half of a bug reported as two symptoms with one root: "when swinging
        /// a tool where the tool crosses water, the tool renders under the water" (fixed in 1.5.6
        /// by giving the tool its own pixels in the sprite mask) and "when using a tool with your
        /// reflection showing, the tool isn't in the reflection", which is this.
        /// </para>
        ///
        /// <para>
        /// It cannot come from the player's colour bake: that target is 96x176 and holds a
        /// FarmerRenderer draw, and a swung axe reaches well outside it. It also cannot be drawn
        /// straight into the mirror through a flip matrix, tempting as that is, because every
        /// other body in there fades from the feet to the head and one that does not read as a
        /// sticker pasted on the water. That exact mistake was made once already, with a
        /// butterfly. So it goes to a target first and is then stamped through the same banded
        /// fade as everything else.
        /// </para>
        ///
        /// <para>
        /// Game1.drawTool renders through Game1.spriteBatch rather than the batch it is handed,
        /// and FishingRod.draw reaches for it too, so the global is pointed at ours for the
        /// duration and restored no matter what happens in between. The transform maps the
        /// player's on-screen position onto the target, which is what makes the tool line up with
        /// the body it belongs to.
        /// </para>
        /// </summary>
        private bool BakeHeldToolForMirror()
        {
            Farmer? who = Game1.player;
            if (who == null || who.swimming.Value || who.CurrentTool == null)
                return false;
            bool timingCast = who.CurrentTool is StardewValley.Tools.FishingRod rod && rod.isTimingCast;
            if (!who.UsingTool && !timingCast)
                return false;

            _toolMirrorRenderTarget ??= VramTally.Track(new RenderTarget2D(_device, ToolRtSize, ToolRtSize, false,
                SurfaceFormat.Color, DepthFormat.None), "tool mirror");
            var box = who.GetBoundingBox();
            float feetY = box.Bottom - 10f + who.yOffset;
            Vector2 feetOnScreen = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, feetY));
            // Feet at the bottom centre of the target, so a swing above and to either side has room.
            _toolFeetInRenderTarget = new Vector2(ToolRtSize / 2f, ToolRtSize - 24f);
            Matrix toTarget = Matrix.CreateTranslation(
                _toolFeetInRenderTarget.X - feetOnScreen.X, _toolFeetInRenderTarget.Y - feetOnScreen.Y, 0f);

            var batch = _spriteMaskSpriteBatch!;
            var gameBatch = Game1.spriteBatch;
            try
            {
                _device.SetRenderTarget(_toolMirrorRenderTarget);
                _device.Clear(Color.Transparent);
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                    null, null, null, toTarget);
                Game1.spriteBatch = batch;
                if (who.CurrentTool is StardewValley.Tools.FishingRod heldRod)
                    heldRod.draw(batch);
                Game1.drawTool(who);
                batch.End();
                return true;
            }
            catch
            {
                try { batch.End(); } catch { }
                return false;
            }
            finally { Game1.spriteBatch = gameBatch; }
        }

        /// <summary>Mirror the baked tool below the player's feet, through the same banded fade
        /// every other body here uses.</summary>
        private void StampToolBake(SpriteBatch spriteBatch, Farmer who)
        {
            var bake = _toolMirrorRenderTarget;
            if (bake == null)
                return;
            Rectangle box = who.GetBoundingBox();
            float feetY = box.Bottom - 10f + who.yOffset;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, feetY));
            float depth = StampDepth(feetY);
            const int bandHeight = 16;
            int bands = ToolRtSize / bandHeight;
            for (int i = 0; i < bands; i++)
            {
                var srcR = new Rectangle(0, (int)_toolFeetInRenderTarget.Y - (i + 1) * bandHeight,
                    ToolRtSize, bandHeight);
                if (srcR.Y < 0)
                {
                    srcR.Height += srcR.Y;
                    srcR.Y = 0;
                    if (srcR.Height <= 0)
                        break;
                }
                float a = MathHelper.Lerp(1f, ReflHeadFade, (i + 0.5f) / bands);
                spriteBatch.Draw(bake, feet + new Vector2(-ToolRtSize / 2f, i * bandHeight * MirrorSquash),
                    srcR, Color.White * a, 0f, Vector2.Zero, new Vector2(1f, MirrorSquash),
                    SpriteEffects.FlipVertically, depth);
            }
        }

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

        private void BakeWaterReflectionCore(ModConfig config)
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
                _reflectionRenderTarget = VramTally.Track(new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None), "entity mirror");
            }
            _spriteMaskSpriteBatch ??= new SpriteBatch(_device);
            // Before the mirror target is bound, because this one writes to a target of its own.
            bool toolBaked = BakeHeldToolForMirror();

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

                MirrorFarmers(spriteBatch, toolBaked);
                MirrorCharacters(spriteBatch, location);
                MirrorAnimalsAndCritters(spriteBatch, location);

                // How far from the water a piece of SCENERY may stand and still be mirrored. The
                // mirror is stamped in four-row slices to get its head fade, so one tree canopy is
                // twenty-four draws and one tuft of grass is twenty: a wooded shore runs past a
                // thousand draws a frame. Reach is the only dial that cuts that in proportion while
                // leaving everything still mirrored looking exactly as it did.
                _mirrorSliceRows = Math.Clamp(config.WaterReflectFadeRows, 4, 16);
                float reachScale = MathHelper.Clamp(config.WaterReflectReach, 0.2f, 1f);
                int plantReach = Math.Max(1, (int)Math.Round(7 * reachScale));
                int buildingReach = Math.Max(1, (int)Math.Round(9 * reachScale));

                MirrorPlants(spriteBatch, location, plantReach);
                MirrorBuildings(spriteBatch, location, buildingReach);

                spriteBatch.End();
                ReflectRTReady = true;
            }
            finally
            {
                _device.SetRenderTargets(prev);
            }
        }

        /// <summary>Mirror the farmers: you from the colour bake, plus every other player in
        /// co-op through the same stamp.</summary>
        private void MirrorFarmers(SpriteBatch spriteBatch, bool toolBaked)
        {
            // Player — the colour bake, flipped below the feet. Swimming is skipped:
            // half the body is underwater, a full mirrored silhouette reads as a glitch.
            var who = Game1.player;
            var pcol = ShadowRenderer.PlayerColor;
            if (who != null && pcol != null && !who.swimming.Value)
            {
                StampFarmerBake(spriteBatch, pcol, who);
                if (toolBaked)
                    StampToolBake(spriteBatch, who);
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
        }

        /// <summary>Mirror whoever the game is drawing as a character this frame: residents
        /// normally, an event's cast during a cutscene.</summary>
        private void MirrorCharacters(SpriteBatch spriteBatch, GameLocation location)
        {
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
        }

        /// <summary>Mirror farm animals and critters, both through the one stamp every body
        /// uses, so a butterfly fades from the water like everything else.</summary>
        private void MirrorAnimalsAndCritters(SpriteBatch spriteBatch, GameLocation location)
        {
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
        }

        /// <summary>Mirror the planted scenery: trees, fruit trees, grass, and bushes from both
        /// lists the game keeps them in.</summary>
        private void MirrorPlants(SpriteBatch spriteBatch, GameLocation location, int plantReach)
        {
            // Trees / fruit trees / bushes: sprites, not map art — the scenery re-render
            // (P3c) can't see them, so their reflections are built here, flipped around
            // the trunk/stem base. Same tile-walk culling as the sprite mask.
            var viewport = Game1.viewport;
            var tfDict = location.terrainFeatures;
            int ctx0 = (int)Math.Floor((viewport.X - 256) / 64f), ctx1 = (int)Math.Floor((viewport.X + viewport.Width + 256) / 64f);
            int cty0 = (int)Math.Floor((viewport.Y - 512) / 64f), cty1 = (int)Math.Floor((viewport.Y + viewport.Height + 768) / 64f);
            // Same sweep, same gate, same narrowing as the sprite mask: outside the water's own
            // box grown by the reach below, WaterWithinTiles cannot answer yes, so those tiles
            // were only ever visited to be turned away.
            if (ClampWalkToWater(4, plantReach, ref ctx0, ref ctx1, ref cty0, ref cty1))
            for (int cvY = cty0; cvY <= cty1; cvY++)
            for (int cvX = ctx0; cvX <= ctx1; cvX++)
            {
                Vector2 tile = new(cvX, cvY);
                if (!tfDict.TryGetValue(tile, out var tf))
                    continue;
                // A tree's mirror hangs BELOW its trunk and a crown is six tiles tall, then
                // stretched by MirrorSquash — so the reach downward has to be the largest of
                // any stamp here. Centred four tiles under the base with slack on both sides.
                if (!WaterWithinTiles(cvX, cvY + 4, plantReach))
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
                        StampBushReflection(spriteBatch, bush, tile);
                        break;
                    // Grass, which grows right down to the bank on most maps and had no
                    // mirror at all. Named in the report next to the modded trees.
                    case StardewValley.TerrainFeatures.Grass grass when grass.texture?.Value != null:
                        StampGrassReflection(spriteBatch, grass, tile);
                        break;
                }
            }

            // The BIG bushes live in a different list. terrainFeatures is tile-keyed and holds
            // the small stuff; the decorative bushes a map places, and everything a content pack
            // adds as scenery, are largeTerrainFeatures. Only the first list was walked, so a
            // planted bush reflected and the bush beside it - identical to look at - did not.
            foreach (var ltf in location.largeTerrainFeatures)
            {
                if (ltf is not StardewValley.TerrainFeatures.Bush lbush || lbush.sourceRect.Value.IsEmpty)
                    continue;
                Vector2 ltile = lbush.Tile;
                if (!WaterWithinTiles((int)ltile.X, (int)ltile.Y + 4, plantReach))
                    continue;
                StampBushReflection(spriteBatch, lbush, ltile);
            }
        }

        /// <summary>Mirror the buildings, which no other pass here can see: they are drawn from
        /// their own texture, not from the map layers and not as a sprite.</summary>
        private void MirrorBuildings(SpriteBatch spriteBatch, GameLocation location, int buildingReach)
        {
            // Buildings. They are entities drawn from their own texture, so neither the scenery
            // re-render (map layers only) nor any stamp above could see them: a coop built at
            // the edge of a pond mirrored the GROUND it stands on and nothing else. Reported
            // against Build Anywhere, where putting a shed on the shore is the whole point, and
            // it is just as wrong on a vanilla farm pond.
            //
            // Building.draw pins the art's bottom-LEFT corner at
            // (tileX*64, (tileY + tilesHigh)*64) + DrawOffset*4, at scale 4, so the base line
            // and the centre both come off that. A building under construction or in the middle
            // of being moved is not drawn, so it must not be mirrored either.
            foreach (var bld in location.buildings)
            {
                if (bld?.texture?.Value == null || bld.isMoving || bld.daysOfConstructionLeft.Value > 0)
                    continue;
                Rectangle bsrcRect = bld.getSourceRect();
                if (bsrcRect.IsEmpty)
                    continue;
                Vector2 bOffset = (bld.GetData()?.DrawOffset ?? Vector2.Zero) * 4f;
                float bBaseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f + bOffset.Y;
                float bCentreX = bld.tileX.Value * 64f + bOffset.X + bsrcRect.Width * 2f;
                // Reaches further than anything else here: a barn is six source tiles tall and
                // the mirror stretches that again, so the water it can land on is a long way down.
                if (!WaterWithinTiles((int)(bCentreX / 64f), (int)(bBaseY / 64f) + 5, buildingReach))
                    continue;
                StampFlippedAt(spriteBatch, bld.texture.Value, bsrcRect, bCentreX, bBaseY, 0);
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
            int hs = Math.Max(1, _mirrorSliceRows);        // source rows per slice
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

        /// <summary>One bush, mirrored. Bush.draw puts the frame's bottom-centre at
        /// (tile.X*64 + (effectiveSize+1)*32, (tile.Y+1)*64), which is the same whether the game
        /// filed it under terrainFeatures or largeTerrainFeatures.</summary>
        private void StampBushReflection(SpriteBatch spriteBatch, StardewValley.TerrainFeatures.Bush bush, Vector2 tile)
        {
            var bsrc = bush.sourceRect.Value;
            int eff = bush.size.Value switch { 3 => 0, 4 => 1, _ => bush.size.Value };
            spriteBatch.Draw(StardewValley.TerrainFeatures.Bush.texture.Value,
                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + (eff + 1) * 32f, (tile.Y + 1) * 64f)),
                bsrc, Color.White, 0f, new Vector2(bsrc.Width / 2f, 0f), 4f,
                SpriteEffects.FlipVertically | (bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                StampDepth((tile.Y + 1) * 64f));
        }

        /// <summary>
        /// One tuft of grass, mirrored blade by blade, from the game's own layout (see
        /// <see cref="GrassArt"/>). Blade i is anchored at its ground contact, so the mirror hangs
        /// from there and the two and a half source rows below that point stay out of it.
        /// <para>
        /// The shake is deliberately left out: a fraction of a degree on a fifteen pixel blade,
        /// seen through a squashed rippling surface, is not worth a field read per tuft per frame.
        /// </para>
        /// </summary>
        private void StampGrassReflection(SpriteBatch spriteBatch, StardewValley.TerrainFeatures.Grass grass, Vector2 tile)
        {
            if (!GrassArt.TryRead(grass, out int blades, out int[] which, out int[] ox, out int[] oy))
                return;
            Texture2D texture = grass.texture.Value;
            for (int i = 0; i < blades; i++)
            {
                Vector2 at = GrassArt.BladeAt(tile, i, ox, oy);
                StampFlippedAt(spriteBatch, texture, GrassArt.BladeSource(grass, i, which), at.X, at.Y, 3);
            }
        }

        /// <summary>BackToFront layer depth from the caster's TRUE feet row: bigger feet Y
        /// = closer to the camera = drawn later = wins reflection overlaps.</summary>
        private static float StampDepth(float feetWorldY) =>
            MathHelper.Clamp(1f - feetWorldY / 65536f, 0.001f, 1f);

        // ---- P3c-lite: clean scenery source for the screen-space mirror ----

        private RenderTarget2D? _mirrorSourceRenderTarget;
        internal bool SceneRTReady;
        /// <summary>The share of the mirror source that lies ABOVE the screen, so the shader can
        /// turn a screen coordinate into a source one. 0 means the source is screen-sized.</summary>
        internal float MirrorSourceTopPad;
        /// <summary>The same for each side of it.</summary>
        internal float MirrorSourceSidePad;

        /// <summary>Re-render the map's own layers (Back/Buildings/Front families, numbered
        /// variants included — DR issue #48) into a sprite-free source for the mirror.
        /// Excluding a sprite from the composed screen used to leave a player-shaped SKY
        /// hole in the scenery's reflection; sampling a source that never contained the
        /// sprite shows the true map pixels behind them instead. Same RenderingWorld slot
        /// as the other bakes (render-target swaps are safe there).</summary>
        public void BakeSceneryReflection()
        {
            long t0 = FrameCost.Begin(FrameCost.Part.SceneryReflection);
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

        /// <summary>Consecutive frames with no water on screen and nothing wanting a mirror.</summary>
        private int _waterIdleFrames;

        /// <summary>
        /// Hand back the water render targets when there is no water to use them on.
        ///
        /// <para>
        /// These three are the largest single allocations in the mod after the shadow pool: the
        /// mirror scene cache is the screen plus a guard band (17.8 MB measured), the mirror
        /// source is the screen plus twelve tiles of headroom (13.8 MB), the entity mirror and
        /// sprite mask are a screen each (6.3 MB apiece). Together they are a third of the
        /// mod's memory, and until now they survived walking indoors, switching water off, and
        /// disabling the mod outright - measured at 130.7 MB held with every effect off, not one
        /// byte of it returned.
        /// </para>
        ///
        /// <para>They cost nothing to rebuild compared to a screen of shadow bakes (one frame of
        /// re-render), so the idle delay here can be short.</para>
        /// </summary>
        internal void ReleaseIdleWaterTargets(bool wanted)
        {
            const int IdleTicksBeforeRelease = 300;       // five seconds at the game's 60 Hz tick
            if (wanted)
            {
                _waterIdleFrames = 0;
                return;
            }
            if (_mirrorSceneCache == null && _mirrorSourceRenderTarget == null
                && _reflectionRenderTarget == null && _spriteMaskRenderTarget == null)
                return;
            if (++_waterIdleFrames < IdleTicksBeforeRelease)
                return;
            _waterIdleFrames = 0;

            try { _mirrorSceneCache?.Dispose(); } catch { }
            try { _mirrorSourceRenderTarget?.Dispose(); } catch { }
            try { _reflectionRenderTarget?.Dispose(); } catch { }
            try { _spriteMaskRenderTarget?.Dispose(); } catch { }
            _mirrorSceneCache = null;
            _mirrorSourceRenderTarget = null;
            _reflectionRenderTarget = null;
            _spriteMaskRenderTarget = null;
            // Split screen keeps a saved copy of these fields per camera and restores them when
            // the screen changes, so nulling the live field is not enough: screen 0's state would
            // hand the disposed target straight back on the next BeginScreen. Single screen never
            // hits it (BeginScreen returns early when the id has not changed), which is exactly
            // the kind of hole that ships and then only breaks for the people using co-op.
            foreach (var st in _screenStates.Values)
            {
                st.MirrorSceneCache = null;
                st.SceneCacheLocation = null;
            }
            // The scene cache's validity test starts with "is the target there", so nulling it is
            // enough to invalidate; the location stamp goes too so a return to the same map
            // cannot match against a cache that no longer exists.
            SceneRTReady = false;
            _sceneCacheLocation = null;
        }
        private GameLocation? _sceneCacheLocation;
        private int _sceneCacheAnchorX, _sceneCacheAnchorY;   // world px of the cache's top-left
        private int _sceneCacheBuiltTick = -1;
        private const int SceneCachePadPx = 128;              // 2 tiles of camera drift per side
        private const int SceneCacheTtlTicks = 6;             // animated-tile refresh (~100 ms)

        /// <summary>
        /// Where the animated tiles are on the layers the mirror draws.
        ///
        /// <para>
        /// The cache is world-anchored and only needs rebuilding when the camera leaves its guard
        /// band. The timer exists for one thing: map art that animates, a waterfall being the case
        /// it was written for, which would otherwise sit frozen in the reflection. Paying for that
        /// with a full walk of every layer, six times a second, under a player who has not moved,
        /// is the wrong price: the walk covers the whole padded window and the art that moves is a
        /// handful of tiles.
        /// </para>
        ///
        /// <para>
        /// So the positions are found once and the refresh redraws only those. An empty list also
        /// answers the other half of it: a map with nothing animated has no reason to expire the
        /// cache at all, and most interiors are that.
        /// </para>
        ///
        /// <para>
        /// Worth knowing either way: <c>radiance_anim</c> measured Forest and Town at an animation
        /// interval of exactly 6.0 ticks, which is exactly this TTL. Two periodic things at the
        /// same period with no phase lock do not track each other, they beat.
        /// </para>
        /// </summary>
        private readonly List<Point> _sceneAnimatedTiles = new();
        private GameLocation? _sceneAnimatedFor;
        private int _sceneAnimatedEpoch = -1;
        /// <summary>Set when the map could not be read. Falls back to the old whole-map rebuild
        /// rather than quietly showing frozen art.</summary>
        private bool _sceneAnimatedUnknown;

        /// <summary>Walk the mirrored layers once per location and record where the animated tiles
        /// are. Re-run when the map is re-patched under us, which is what MaskEpoch tracks.</summary>
        private List<Point> AnimatedMirrorTiles(GameLocation location)
        {
            if (ReferenceEquals(_sceneAnimatedFor, location) && _sceneAnimatedEpoch == MaskEpoch)
                return _sceneAnimatedTiles;
            _sceneAnimatedFor = location;
            _sceneAnimatedEpoch = MaskEpoch;
            _sceneAnimatedTiles.Clear();
            _sceneAnimatedUnknown = false;
            var seen = new HashSet<Point>();
            try
            {
                foreach (var layer in MapLayers.RenderedLayers(location.map, topToBottom: false))
                {
                    if (!MapLayers.TryGetFamily(layer.Id, out string fam) || fam == "AlwaysFront")
                        continue;
                    for (int ty = 0; ty < layer.LayerHeight; ty++)
                    for (int tx = 0; tx < layer.LayerWidth; tx++)
                    {
                        if (layer.Tiles[tx, ty] is xTile.Tiles.AnimatedTile && seen.Add(new Point(tx, ty)))
                            _sceneAnimatedTiles.Add(new Point(tx, ty));
                    }
                }
            }
            catch { _sceneAnimatedUnknown = true; }
            return _sceneAnimatedTiles;
        }

        /// <summary>
        /// Redraw just the animated tiles into the standing cache.
        ///
        /// <para>
        /// Each one is blacked out and then rebuilt from every mirrored layer in the same
        /// bottom-up order the full bake uses, which is what makes it equivalent rather than
        /// merely similar: a tile that is animated on Front still has its Back and Buildings
        /// neighbours underneath it, and painting only the animated layer would leave the previous
        /// frame of it showing through.
        /// </para>
        /// </summary>
        private void RefreshAnimatedTilesIntoCache(GameLocation location, SpriteBatch spriteBatch,
            List<Point> tiles, int cacheW, int cacheH)
        {
            var dd = Game1.mapDisplayDevice;
            _device.SetRenderTarget(_mirrorSceneCache);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            foreach (Point t in tiles)
            {
                int px = t.X * 64 - _sceneCacheAnchorX, py = t.Y * 64 - _sceneCacheAnchorY;
                if (px + 64 <= 0 || py + 64 <= 0 || px >= cacheW || py >= cacheH)
                    continue;
                spriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle(px, py, 64, 64), Color.Black);
            }
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            dd.BeginScene(spriteBatch);
            foreach (var layer in MapLayers.RenderedLayers(location.map, topToBottom: false))
            {
                if (!MapLayers.TryGetFamily(layer.Id, out string fam) || fam == "AlwaysFront")
                    continue;
                foreach (Point t in tiles)
                {
                    if (t.X >= layer.LayerWidth || t.Y >= layer.LayerHeight)
                        continue;
                    var tile = layer.Tiles[t.X, t.Y];
                    if (tile == null)
                        continue;
                    int px = t.X * 64 - _sceneCacheAnchorX, py = t.Y * 64 - _sceneCacheAnchorY;
                    if (px + 64 <= 0 || py + 64 <= 0 || px >= cacheW || py >= cacheH)
                        continue;
                    dd.DrawTile(tile, new xTile.Dimensions.Location(px, py), 0f);
                }
            }
            dd.EndScene();
            spriteBatch.End();
            _sceneCacheBuiltTick = Game1.ticks;
        }

        /// <summary>
        /// How far ABOVE the screen the mirror is allowed to read, in world pixels.
        ///
        /// <para>
        /// A reflection is the picture from above the waterline, flipped. That picture came from
        /// the screen, so when the waterline sat near the TOP of the screen there was nothing above
        /// it to mirror and the water below the bank stayed bare. Walking north brought the bank
        /// down the screen, the things standing on it came into view, and their reflection appeared
        /// with them. Reported in those words: no reflections when you are away, some as you get
        /// closer, all of it when you are at the edge, and "IRL there is no such thing as half
        /// reflections".
        /// </para>
        ///
        /// <para>
        /// The map layers are already re-rendered into a world-anchored cache with a guard band, so
        /// the pixels above the screen are a matter of asking for more of that cache rather than of
        /// drawing anything new.
        /// </para>
        ///
        /// <para>
        /// TWELVE TILES IS THE WHOLE OF IT, and the shader says so rather than taste. The mirror
        /// reads its source at 1.25 units above the waterline per unit of depth below it, and it
        /// has already dissolved into sky by nine tiles of depth, so the deepest pixel that can
        /// still show a reflection reads 9 x 1.25 = 11.25 tiles above the waterline. The waterline
        /// itself is always somewhere on the screen, so the furthest the mirror can ever reach past
        /// the top edge is those 11.25 tiles - at the worst case of a shoreline sitting exactly on
        /// the top row. Anything past twelve is buffer nobody samples.
        /// </para>
        ///
        /// <para>
        /// This buys the SCENERY only. Trees, buildings and people are stamped into their own
        /// screen-sized layer and still cannot be mirrored from above the screen edge.
        /// </para>
        /// </summary>
        private const int MirrorTopReachPx = 768;

        /// <summary>
        /// How far past the LEFT and RIGHT edges of the screen the mirror may read, in world pixels.
        ///
        /// <para>
        /// Sideways the mirror barely moves - the sample is the same column plus a few pixels of
        /// ripple - so the reason for a side band is not reach but the OFF-SCREEN FADE. A mirrored
        /// sample landing outside the source used to be faded out rather than clamped, because
        /// clamping smears the edge column across the water; that fade is 6% of the picture, which
        /// is about a tile and a quarter of dimmed reflection down each side of the screen at all
        /// times. With real pixels out there the fade has nothing to hide and stops firing.
        /// </para>
        ///
        /// <para>
        /// Three tiles is comfortably past both the 6% band and the widest ripple offset
        /// (ripple.x * 3 is a fraction of a tile), and it costs one more tile of cache either side.
        /// </para>
        /// </summary>
        private const int MirrorSideReachPx = 192;

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
            // The mirror source is TALLER than the screen: the extra rows sit above it, which is
            // the only direction a reflection ever reads. See MirrorTopReachPx.
            int sourceW = w + 2 * MirrorSideReachPx, sourceH = h + MirrorTopReachPx;
            if (_mirrorSourceRenderTarget == null || _mirrorSourceRenderTarget.Width != sourceW || _mirrorSourceRenderTarget.Height != sourceH)
            {
                _mirrorSourceRenderTarget?.Dispose();
                _mirrorSourceRenderTarget = VramTally.Track(new RenderTarget2D(_device, sourceW, sourceH, false, SurfaceFormat.Color, DepthFormat.None), "mirror source");
            }
            MirrorSourceTopPad = MirrorTopReachPx / (float)sourceH;
            MirrorSourceSidePad = MirrorSideReachPx / (float)sourceW;
            _spriteMaskSpriteBatch ??= new SpriteBatch(_device);

            int vpX = Game1.viewport.X, vpY = Game1.viewport.Y;
            // The region the blit needs is the screen plus the reach around it; the guard band is
            // the slack around THAT, so the walk still only re-runs when the camera leaves it.
            int wantX = vpX - MirrorSideReachPx, wantY = vpY - MirrorTopReachPx;
            int cacheW = sourceW + 2 * SceneCachePadPx, cacheH = sourceH + 2 * SceneCachePadPx;
            // The clock is no longer part of this. What invalidates the whole cache is the camera
            // leaving the band it was baked for, which is the only thing that can put unbaked
            // ground on screen; stale animation is handled below by redrawing the tiles that
            // animate, not by throwing the map away and walking it again.
            //
            // A map this cannot read keeps the old behaviour, because a frozen waterfall in the
            // reflection is a worse failure than a wasted rebuild.
            List<Point> animated = AnimatedMirrorTiles(location);
            bool timeExpired = Game1.ticks - _sceneCacheBuiltTick >= SceneCacheTtlTicks;
            bool cacheValid = _mirrorSceneCache != null
                && ReferenceEquals(_sceneCacheLocation, location)
                && _mirrorSceneCache.Width == cacheW && _mirrorSceneCache.Height == cacheH
                && !(_sceneAnimatedUnknown && timeExpired)
                && wantX >= _sceneCacheAnchorX && wantY >= _sceneCacheAnchorY
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
                        _mirrorSceneCache = VramTally.Track(new RenderTarget2D(_device, cacheW, cacheH, false, SurfaceFormat.Color,
                            DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "mirror scene cache");
                    }
                    _sceneCacheLocation = location;
                    _sceneCacheAnchorX = wantX - SceneCachePadPx;
                    _sceneCacheAnchorY = wantY - SceneCachePadPx;
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
                    // Bottom-to-top by the one shared sort key, then drawn family-by-family.
                    // AlwaysFront is deliberately out (weather + translucent shadow washes), so it
                    // is filtered after the sort — a map that declares "Front2" before "Front" or
                    // numbers its layers oddly now composes the mirror the same way the labeler
                    // and the mask do, instead of whichever way the declaration order happened to
                    // fall. The actual layer.Draw is the game's own rasteriser, so orientation is
                    // still the game's, this only fixes the ORDER.
                    foreach (var l in MapLayers.RenderedLayers(location.map, topToBottom: false))
                    {
                        if (MapLayers.TryGetFamily(l.Id, out string fam) && fam != "AlwaysFront")
                            l.Draw(dd, paddedViewport, xTile.Dimensions.Location.Origin, false, 4);
                    }
                    dd.EndScene();
                    spriteBatch.End();
                }
                else if (timeExpired && animated.Count > 0)
                {
                    // The whole reason the timer existed, now costing what it is worth: a handful
                    // of tiles rather than every layer of the padded window.
                    RefreshAnimatedTilesIntoCache(location, spriteBatch, animated, cacheW, cacheH);
                }

                // Screen-aligned mirror source = one quad from the cache, shifted by the
                // camera delta since the cache was anchored.
                _device.SetRenderTarget(_mirrorSourceRenderTarget);
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                spriteBatch.Draw(_mirrorSceneCache, new Vector2(_sceneCacheAnchorX - wantX, _sceneCacheAnchorY - wantY), Color.White);
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
