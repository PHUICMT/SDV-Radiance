using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline — the SPRITE mask: a per-frame screen-space stencil of everything standing on,
    /// swimming in, or flying over the water, so the shader leaves those pixels alone. Without it a
    /// duck, an NPC, a speech bubble or a butterfly gets rippled and mirrored along with the water
    /// underneath it.
    ///
    /// Baked in Display.RenderingWorld, the only place a render-target swap is safe. Positions follow
    /// the game's own draw math rather than guessing where a sprite is, which is what the butterfly
    /// and see-through-tree fixes came down to; where a sprite can fade, the stamp fades with it.
    /// This is a different lifetime from the water mask itself: that one is rebuilt on tile crossings
    /// and survives frames, this one is thrown away and redrawn every frame.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        // ---- per-frame sprite mask (things ON the water must not ripple) ----

        private RenderTarget2D? _spriteMaskRenderTarget;
        private SpriteBatch? _spriteMaskSpriteBatch;

        /// <summary>Solid exclusion box in WORLD px, centre-top anchored — bubbles, emotes:
        /// UI riding in the world layer that the water must never warp.</summary>
        /// <summary>Let a character draw its own above-head speech scroll into the mask, so the
        /// exclusion is the shape of the text rather than a rectangle guessed around it. The game
        /// draws these through Game1.spriteBatch rather than the batch it is handed, so the global
        /// is pointed at ours for the duration and restored whatever happens.</summary>
        /// <summary>Let a critter draw ITSELF into the exclusion mask.
        ///
        /// <para>
        /// This used to rebuild the game's placement by hand - source rect times four, minus half
        /// a frame across, minus a frame and a bit up - and every critter type that positions
        /// itself differently was a mismatch waiting to be reported. Butterflies are one: they
        /// were being excluded somewhere near themselves rather than on themselves, so the ripple
        /// ran straight over the butterfly while a butterfly-shaped patch of water beside it held
        /// still.
        /// </para>
        ///
        /// <para>
        /// The same lesson the fishing meter taught, written in this file already: a sprite stamps
        /// itself rather than being guessed at. Critter.draw is public and virtual, so the shape
        /// that lands in the mask is by construction the shape the game drew. It renders through
        /// Game1.spriteBatch rather than the batch it is handed, so the global is pointed at ours
        /// and restored whatever happens.
        /// </para></summary>
        /// <summary>
        /// How opaque a tree's canopy is being drawn RIGHT NOW.
        ///
        /// <para>
        /// The game fades a tree out while the player stands behind it, so you can see yourself
        /// through the leaves. The exclusion mask never knew that: it stamped every canopy at full
        /// opacity, so the water under the leaves was removed from the effect completely, and the
        /// moment the tree went see-through the player was looking straight at a tree-shaped patch
        /// of untouched vanilla water. Reported after living in the mod for a long time, and
        /// obvious once seen - the shape of the missing effect is the shape of the canopy.
        /// </para>
        ///
        /// <para>
        /// Read reflectively because Tree.alpha is a plain field on a class this mod does not own,
        /// and a rename in a game update must cost the fade rather than the whole mask: an
        /// unreadable alpha answers 1, which is exactly the behaviour that shipped until now.
        /// </para></summary>
        private static readonly System.Reflection.FieldInfo? _treeAlphaField =
            HarmonyLib.AccessTools.Field(typeof(StardewValley.TerrainFeatures.Tree), "alpha");

        private static float TreeMaskAlpha(StardewValley.TerrainFeatures.Tree tree)
        {
            try
            {
                if (_treeAlphaField?.GetValue(tree) is float a)
                    return MathHelper.Clamp(a, 0f, 1f);
            }
            catch { }
            return 1f;
        }

        private static void StampCritter(SpriteBatch spriteBatch, StardewValley.BellsAndWhistles.Critter critter)
        {
            var gameBatch = Game1.spriteBatch;
            try
            {
                Game1.spriteBatch = spriteBatch;
                // BOTH, because a critter uses one or the other and never announces which.
                // Critter.draw is the ground path - a rabbit, a squirrel, a crow. Anything that
                // FLIES draws itself in drawAboveFrontLayer instead, so that it passes over the
                // things it flies above, and its draw() does nothing at all. Butterflies are in
                // that second group, which is why calling only draw() left them with no stamp
                // whatsoever - worse than the hand-built one it replaced, which at least landed
                // somewhere. Whichever a critter does not use is a call that draws nothing.
                critter.draw(spriteBatch);
                critter.drawAboveFrontLayer(spriteBatch);
            }
            catch { }
            finally { Game1.spriteBatch = gameBatch; }
        }

        /// <summary>Let a character draw ITSELF into the exclusion mask, the way critters already do.
        ///
        /// <para>
        /// Rebuilding where a character is drawn works for a villager and for nothing else. A
        /// Custom Companions animal is an NPC that overrides draw and puts itself down with its own
        /// origin, its own rotation and a per-companion Scale, none of which a bounding box knows
        /// about, so the stencil landed off the animal and the ripple ran over it. Reported twice
        /// in one day, about ducks on a pond.
        /// </para>
        ///
        /// <para>
        /// This is the third time the same lesson has been paid for here: butterflies, then map
        /// props, now modded creatures. Anything that draws itself should be asked to.
        /// </para></summary>
        private static bool StampCharacterSelf(SpriteBatch spriteBatch, NPC character)
        {
            var gameBatch = Game1.spriteBatch;
            try
            {
                Game1.spriteBatch = spriteBatch;
                character.draw(spriteBatch);
                return true;
            }
            catch { return false; }
            finally { Game1.spriteBatch = gameBatch; }
        }

        /// <summary>The same for a farm animal, which also draws itself.</summary>
        private static bool StampAnimalSelf(SpriteBatch spriteBatch, StardewValley.FarmAnimal animal)
        {
            var gameBatch = Game1.spriteBatch;
            try
            {
                Game1.spriteBatch = spriteBatch;
                animal.draw(spriteBatch);
                return true;
            }
            catch { return false; }
            finally { Game1.spriteBatch = gameBatch; }
        }

        private static void StampAboveHead(SpriteBatch spriteBatch, Character c)
        {
            var gameBatch = Game1.spriteBatch;
            try
            {
                Game1.spriteBatch = spriteBatch;
                c.drawAboveAlwaysFrontLayer(spriteBatch);
            }
            catch { }
            finally { Game1.spriteBatch = gameBatch; }
        }

        private void StampUiBox(SpriteBatch spriteBatch, int cx, int top, int w, int h)
        {
            Vector2 tl = Game1.GlobalToLocal(Game1.viewport, new Vector2(cx - w / 2f, top));
            spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)tl.X, (int)tl.Y, w, h), Color.White);
        }

        // NPC.textAboveHead / textAboveHeadTimer went protected in 1.6 — the bubble mask below
        // needs to know a bubble is showing and how wide its text is, nothing more.
        private static readonly System.Reflection.FieldInfo? _npcTextField =
            HarmonyLib.AccessTools.Field(typeof(NPC), "textAboveHead");
        private static readonly System.Reflection.FieldInfo? _npcTextTimerField =
            HarmonyLib.AccessTools.Field(typeof(NPC), "textAboveHeadTimer");
        internal bool SpriteMaskReady;


        /// <summary>
        /// Bake every sprite that could be standing ON water — NPCs, farm animals
        /// (swimming ducks!), critters — into a screen-space mask, called from
        /// Display.RenderingWorld (the only spot where a render-target swap is safe).
        /// The water shader excludes these pixels from ripple/mirror so sprites never
        /// distort, while the water beside them keeps animating. Positions mirror the
        /// game's own draw math (bottom-centre at the collision box feet).
        /// </summary>
        public void BakeWaterSpriteMask()
        {
            long t0 = FrameCost.Begin(FrameCost.Part.SpriteMask);
            BakeWaterSpriteMaskCore();
            double ms = FrameCost.End(FrameCost.Part.SpriteMask, t0);
            if (_timingOn) AccumulateBuildMilliseconds(4, ms);
        }

        private void BakeWaterSpriteMaskCore()
        {
            // Close off what the location drew for itself last frame before anything reads it.
            LocationDrawHook.BeginFrame();
            SpriteMaskReady = false;
            GameLocation? location = Game1.currentLocation;
            if (location == null || !_hasWaterInMask)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            bool upscalerWasSuspended = SheetUpscaler.SuspendedForOwnDraw;
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_spriteMaskRenderTarget == null || _spriteMaskRenderTarget.Width != w || _spriteMaskRenderTarget.Height != h)
            {
                _spriteMaskRenderTarget?.Dispose();
                _spriteMaskRenderTarget = VramTally.Track(new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None), "water sprite mask");
            }
            _spriteMaskSpriteBatch ??= new SpriteBatch(_device);

            try
            {
                // Nothing stamped here is looked at. It is a coverage shape, read for where it is
                // opaque and nothing else, so the smoothed diagonal a doubled sheet buys is worth
                // nothing to it. Most stamps go through our own batch and were never redirected,
                // but the four that let a sprite draw ITSELF have to point the global at ours for
                // the duration, and the upscaler decides by exactly that global.
                SheetUpscaler.SuspendedForOwnDraw = true;   // saved above, restored below
                _device.SetRenderTarget(_spriteMaskRenderTarget);
                _device.Clear(Color.Transparent);
                var spriteBatch = _spriteMaskSpriteBatch;
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

                StampCharacters(spriteBatch, location);
                StampPlayer(spriteBatch);
                StampOtherFarmers(spriteBatch);
                StampHeldTool(spriteBatch);
                StampAnimals(spriteBatch, location);
                StampCritters(spriteBatch, location);
                StampObjectsOnWater(spriteBatch, location);
                StampTerrainFeatures(spriteBatch, location);
                StampLargeTerrainFeatures(spriteBatch, location);
                StampBuildings(spriteBatch, location);
                StampLocationsOwnArt(spriteBatch, location);
                spriteBatch.End();
                SpriteMaskReady = true;
            }
            finally
            {
                SheetUpscaler.SuspendedForOwnDraw = upscalerWasSuspended;
                _device.SetRenderTargets(prev);
            }
        }

        /// <summary>Whatever the location painted for itself last frame, from the rectangles its
        /// own draw calls used.</summary>
        /// <remarks>
        /// Ginger Island's boat is the case this exists for: it lives in the location's fields, so
        /// no layer, label, entity list or building carries it, and the ripple ran over the hull.
        /// The rectangles come from the game's draw rather than from anything measured here - see
        /// <see cref="LocationDrawHook"/> for why guessing the source rect was not an option. They
        /// are kept in world pixels and brought back to this frame's camera here, so a panning
        /// camera reads the carve on the hull rather than one frame behind it.
        /// </remarks>
        private void StampLocationsOwnArt(SpriteBatch spriteBatch, GameLocation location)
        {
            // The art itself, not a box around it: the mask is read by alpha, so the sea keeps
            // its ripple everywhere the boat's own sprite is transparent. A solid rectangle here
            // cut a visible square out of the water around the mast on the first try.
            foreach (LocationDrawHook.Stamp stamp in LocationDrawHook.Stamps)
            {
                if (!ReferenceEquals(stamp.Owner, location))
                    continue;
                if (stamp.Texture == null || stamp.Texture.IsDisposed)
                    continue;
                Vector2 topLeftOnScreen = new(stamp.WorldTopLeft.X - Game1.viewport.X,
                                              stamp.WorldTopLeft.Y - Game1.viewport.Y);
                spriteBatch.Draw(stamp.Texture, topLeftOnScreen, stamp.Source, Color.White, 0f,
                                 Vector2.Zero, stamp.Scale, stamp.Effects, 0f);
            }
        }

        private void StampCharacters(SpriteBatch spriteBatch, GameLocation location)
        {
            // NPCs + monsters: bottom-centre at the collision-box feet, scale 4 —
            // the same anchor the game draws them at (small bob/jump offsets are
            // sub-pixel enough for an exclusion mask).
            foreach (NPC c in ShadowRenderer.CharactersIn(location))
            {
                if (c?.Sprite?.Texture == null || c.IsInvisible)
                    continue;
                // drawOffset is the shift the game applies at DRAW time and never writes back
                // into Position or the collision box — a character on a seat, in the bus, or
                // posed by an event is drawn somewhere its box does not admit to. Without it
                // the exclusion landed off the body: the sprite kept rippling and a hole was
                // punched into clean water beside it.
                Rectangle bb = c.GetBoundingBox();
                Vector2 off = c.drawOffset;
                if (off != Vector2.Zero)
                    bb.Offset((int)off.X, (int)off.Y);
                if (!WaterWithinTiles(bb.Center.X / 64, bb.Bottom / 64, 3))
                    continue;
                // Ask it to draw itself first, and only fall back to rebuilding its placement if
                // that throws. A villager comes out the same either way; anything that positions
                // itself differently only comes out right this way.
                bool drewItself = StampCharacterSelf(spriteBatch, c);
                if (!drewItself)
                    StampSprite(spriteBatch, c.Sprite.Texture, c.Sprite.SourceRect, bb);
                // A SPEECH BUBBLE is part of the world layer too (drawn above AlwaysFront),
                // so a fisherman chatting over the river had his bubble rippled and tinted
                // like the water behind it. Mask a generous box where vanilla draws it
                // (~3 tiles above the feet, scroll background included); over-covering is
                // harmless — the box only exists for the seconds the bubble does.
                if ((_npcTextTimerField?.GetValue(c) as int? ?? 0) > 0
                    && _npcTextField?.GetValue(c) is string say && say.Length > 0)
                {
                    int tw = (int)(StardewValley.BellsAndWhistles.SpriteText.getWidthOfString(say) * 1.1f) + 64;
                    var world = new Rectangle(bb.Center.X - tw / 2, bb.Top - 260, tw, 176);
                    Vector2 tl = Game1.GlobalToLocal(Game1.viewport, new Vector2(world.X, world.Y));
                    spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)tl.X, (int)tl.Y, world.Width, world.Height), Color.White);
                }
                // Emotes (the thought/exclamation balloon) live in the world layer too, and the
                // character that just drew itself drew its emote as part of that: NPC.draw ends
                // with DrawEmote, which places the balloon from the sprite's OWN height, the
                // pack's EmoteOffset and a per-age and per-gender nudge. This box knew none of
                // that. It sat three tiles over the collision box and 128 tall, which is roughly
                // right over a villager and nowhere near a duck: reported with a picture of a
                // column of dead water standing above two modded birds while their balloon
                // floated in live water below it. So it is what it always should have been, the
                // fallback for a character that could not draw itself, next to the fallback for
                // its body.
                if (!drewItself && c.isEmoting)
                    StampUiBox(spriteBatch, bb.Center.X, bb.Top - 160, 80, 128);
                // The SPEECH bubble is a different mechanism from the emote icon above, and
                // only the icon was ever covered. Reported as "water displacement somehow
                // affects muttering farmer speech bubbles": a line of dialogue floating over
                // a pond rippled along with it. It is drawn as a scroll sized to its text, so
                // it stamps ITSELF rather than being guessed at with a box - the cast meter
                // taught that lesson, where a generous 288x240 rectangle punched a hole in
                // the reflections four tiles wide.
                // Called unconditionally: the timer that decides whether there is anything
                // to draw is protected, and the method already draws nothing when there is
                // not. A virtual call that early-outs is cheaper than reflecting for a field.
                StampAboveHead(spriteBatch, c);
            }
        }

        private void StampPlayer(SpriteBatch spriteBatch)
        {
            // The player's own bubble/emote — their BODY is excluded via PlayerMask, but
            // the balloon floats above the mask's reach.
            var pw = Game1.player;
            if (pw != null)
            {
                if (pw.isEmoting)
                {
                    // Same correction the villagers get above, and for the same reason: the game
                    // draws the balloon from getLocalPosition, which adds drawOffset, while the
                    // collision box this is measured from never hears about it. A farmer sitting
                    // on a bench or posed by an event is drawn somewhere the box does not admit
                    // to, and the exclusion stayed where the box was.
                    Rectangle pbb = pw.GetBoundingBox();
                    Vector2 playerDrawOffset = pw.drawOffset;
                    if (playerDrawOffset != Vector2.Zero)
                        pbb.Offset((int)playerDrawOffset.X, (int)playerDrawOffset.Y);
                    StampUiBox(spriteBatch, pbb.Center.X, pbb.Top - 160, 80, 128);
                }
                // The report said "muttering FARMER speech bubbles", and a farmer is not an
                // NPC: the self-stamp above covers the residents, and the box here only ever
                // covered the emote icon, so the one balloon actually named was the one still
                // rippling. A farmer draws their bubble through the same above-head layer, so
                // the same stamp reaches it - for you and, below, for everyone else in co-op.
                StampAboveHead(spriteBatch, pw);
            }
        }

        private void StampOtherFarmers(SpriteBatch spriteBatch)
        {
            // The OTHER players' bodies. Yours is excluded per pixel by the shader, which
            // reads one texture and can only ever be about one farmer, so in co-op everybody
            // else's legs rippled along with the water they were standing in. Their colour
            // bake is the right stamp for it: it is their exact shape at full opacity, with
            // none of the head fade the shadow silhouette carries.
            foreach (var other in ShadowRenderer.OtherFarmerImages)
            {
                if (other.Colour == null)
                    continue;
                Rectangle obb = other.Who.GetBoundingBox();
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(obb.Center.X, obb.Bottom - 10f + other.Who.yOffset));
                spriteBatch.Draw(other.Colour, feet - new Vector2(ShadowRenderer.PlayerRtW / 2f, ShadowRenderer.PlayerRtH - 8f),
                    Color.White);
                if (other.Who.isEmoting)
                {
                    Vector2 otherDrawOffset = other.Who.drawOffset;
                    if (otherDrawOffset != Vector2.Zero)
                        obb.Offset((int)otherDrawOffset.X, (int)otherDrawOffset.Y);
                    StampUiBox(spriteBatch, obb.Center.X, obb.Top - 160, 80, 128);
                }
                StampAboveHead(spriteBatch, other.Who);
            }
        }

        private void StampHeldTool(SpriteBatch spriteBatch)
        {
            var pw = Game1.player;
            // EVERYTHING THE GAME DRAWS FOR THE PLAYER'S TOOL. All of it lives in the world
            // layer, outside the PlayerMask bake (which is the body only) and outside the
            // sprite stamps, so anything held or swung over water waved with the water under
            // it. Each piece stamps ITSELF, the same way crab pots do: whatever the game
            // draws lands in the exclusion pixel for pixel.
            //
            // Two reports, opposite failures of the same gap, and both are fixed by widening
            // this from "the fishing rod" to "the tool".
            //
            // The cast power meter used to be covered by a generous 288x240 box rather than
            // its own shape, on the reasoning that it only shows for a fraction of a second.
            // Holding the button to charge keeps it up far longer than that, and 288x240 is
            // four and a half tiles by nearly four of SOLID exclusion. This mask is also what
            // holds sprites out of the REFLECTION, so charging a cast punched a square hole in
            // the reflections around the farmer: "when I have the reflection options and water
            // features enabled, and I go fishing, the reflections disappear within a square
            // radius". The meter now stamps its own pixels like everything else here.
            //
            // A swung axe, pickaxe, hoe or watering can had the opposite problem: the stamp
            // ran only for a FishingRod, so no other tool was excluded at all and the water
            // drew straight over it. Reported as "when swinging a tool where the tool crosses
            // water, the tool renders under the water".
            bool timingCast = pw?.CurrentTool is StardewValley.Tools.FishingRod rodTiming
                              && rodTiming.isTimingCast;
            if (pw != null && (pw.UsingTool || timingCast))
            {
                // Game1.drawTool renders through Game1.spriteBatch rather than the batch it is
                // handed, and FishingRod.draw reaches for it too, so point it at the mask batch
                // for the duration and restore it no matter what happens in between.
                var gameBatch = Game1.spriteBatch;
                try
                {
                    Game1.spriteBatch = spriteBatch;
                    // The rod carries the line, the bobber and the cast meter.
                    if (pw.CurrentTool is StardewValley.Tools.FishingRod heldRod)
                        heldRod.draw(spriteBatch);
                    Game1.drawTool(pw);
                }
                catch { }
                finally { Game1.spriteBatch = gameBatch; }
            }
        }

        private void StampAnimals(SpriteBatch spriteBatch, GameLocation location)
        {
            // Farm animals (ducks paddle straight into ponds).
            foreach (var a in location.animals.Values)
            {
                if (a?.Sprite?.Texture == null)
                    continue;
                Rectangle abb = a.GetBoundingBox();
                if (!WaterWithinTiles(abb.Center.X / 64, abb.Bottom / 64, 3))
                    continue;
                // Same as the characters above: a farm animal draws itself, so ask it. A modded
                // animal with its own size or draw offset is stamped where it really is instead of
                // where a bounding box says it should be.
                if (!StampAnimalSelf(spriteBatch, a))
                    StampSprite(spriteBatch, a.Sprite.Texture, a.Sprite.SourceRect, abb);
            }
        }

        private void StampCritters(SpriteBatch spriteBatch, GameLocation location)
        {
            // Critters (seagulls, birds, frogs). Critter.draw puts the frame's bottom edge at
            // position.Y, centred on position.X, and lifts it by the flight offset:
            //   position + (-64, -128 + yJumpOffset + yOffset), scale 4.
            //
            // That -64/-128 is half a 32x32 frame, and the stamp here was written for a 16x16
            // one. Every critter in the game is 32x32 (Critter's own constructor says so), so
            // the exclusion box was pinned a whole 32 px right of the bird and 64 px below it:
            // the seagull itself was left inside the rippling water while a bird-shaped patch
            // of empty sea beside it was held still. That is the "objects above water, such as
            // seagulls, fail to render correctly" report - the bird was being displaced by the
            // water it was sitting on.
            //
            // The offsets come off the SOURCE RECT rather than being written out again, so a
            // mod's critter with a different frame size lands correctly too, and the flight
            // offset is honoured: a gull on the wing is drawn well above its own position and
            // was being excluded at ground level.
            if (location.critters != null)
            {
                foreach (var cr in location.critters)
                {
                    if (cr?.sprite?.Texture == null)
                        continue;
                    // Reach measured from where it is DRAWN, not from where it stands. A
                    // butterfly hovers most of a tile above its own position and a gull on
                    // the wing is further still, so a gate asked at ground level answers
                    // about a patch of map the creature is nowhere near.
                    float drawnY = cr.position.Y + cr.yJumpOffset + cr.yOffset;
                    if (!WaterWithinTiles((int)(cr.position.X / 64f), (int)(drawnY / 64f), 3))
                        continue;
                    StampCritter(spriteBatch, cr);
                }
            }

        }

        private void StampObjectsOnWater(SpriteBatch spriteBatch, GameLocation location)
        {
            // World OBJECTS standing on a water tile: beach forage in a tide pool, a crab pot,
            // anything dropped. They are drawn on top of the water, so the ripple was warping
            // them along with it (reported: a sea urchin in a tide pool rippling like liquid).
            // Objects are tile-keyed, so walk the visible tile range rather than the whole
            // dictionary, the same way the canopy pass below does.
            var vpO = Game1.viewport;
            int otx0 = (int)Math.Floor((vpO.X - 128) / 64f), otx1 = (int)Math.Floor((vpO.X + vpO.Width + 128) / 64f);
            int oty0 = (int)Math.Floor((vpO.Y - 128) / 64f), oty1 = (int)Math.Floor((vpO.Y + vpO.Height + 192) / 64f);
            for (int ovY = oty0; ovY <= oty1; ovY++)
            for (int ovX = otx0; ovX <= otx1; ovX++)
            {
                if (!location.objects.TryGetValue(new Vector2(ovX, ovY), out var obj) || obj == null)
                    continue;
                if (!WaterWithinTiles(ovX, ovY, 2))
                    continue;
                // Let the OBJECT draw itself. Reconstructing the placement here (centre-bottom
                // of the tile, nudged up a third) is right for an ordinary placed item and
                // wrong for anything with its own draw: a CRAB POT sits a tile higher and bobs
                // on the swell, so the hole landed beside the pot instead of on it — water
                // notched next to it, and the flat unrippled patch read as a shadow that did
                // not match. Only this stamp's ALPHA is read, so drawing it in its own colours
                // costs nothing and it is the game's own geometry by construction.
                try { obj.draw(spriteBatch, ovX, ovY, 1f); }
                catch { /* a mod's draw threw — skip this object's exclusion */ }
            }

        }

        private void StampTerrainFeatures(SpriteBatch spriteBatch, GameLocation location)
        {
            // Tree/bush canopies overhanging a pond are SPRITES (terrain features), not
            // map art — Pass C can't carve them, so leaves at the water's edge rippled.
            // Stamp them with the same geometry the shadow baker uses. Walk only the on-screen
            // tile range (+ a canopy margin) and look each tile up, instead of enumerating EVERY
            // terrain feature every frame and culling — the old full walk was O(all crops/trees)
            // per frame on a mature farm.
            var viewport = Game1.viewport;
            var tfDict = location.terrainFeatures;
            int ctx0 = (int)Math.Floor((viewport.X - 256) / 64f), ctx1 = (int)Math.Floor((viewport.X + viewport.Width + 256) / 64f);
            int cty0 = (int)Math.Floor((viewport.Y - 512) / 64f), cty1 = (int)Math.Floor((viewport.Y + viewport.Height + 768) / 64f);
            // Every tile in this sweep asks the same water question below, and outside the
            // water's own box the answer cannot be yes. Narrowing the sweep to that box is the
            // same set of tiles reached without visiting the ones that always fail.
            if (ClampWalkToWater(-3, 6, ref ctx0, ref ctx1, ref cty0, ref cty1))
            for (int cvY = cty0; cvY <= cty1; cvY++)
            for (int cvX = ctx0; cvX <= ctx1; cvX++)
            {
                Vector2 tile = new(cvX, cvY);
                if (!tfDict.TryGetValue(tile, out var tf))
                    continue;
                // A canopy only matters where it overhangs water. A grown tree's crown is
                // 96 source rows — SIX tiles above its trunk — so the search is centred well
                // above the base and reaches far enough to cover the whole crown plus slack.
                // Anything tighter would drop the stamp for a tree whose top overhangs a pond
                // several tiles north of it, and the leaves would ripple.
                if (!WaterWithinTiles(cvX, cvY - 3, 6))
                    continue;
                switch (tf)
                {
                    // Grown tree: canopy (0,0,48,96) at tile*64+(32,64), origin (24,96) — Tree.draw's math.
                    // Stamped at the canopy's CURRENT opacity, which is the whole point of the
                    // alpha reaching the shader at all - see the note on TreeMaskAlpha.
                    // The game's own gate: a tree being chopped is a stump AND falling, and it is
                    // drawn whole for the length of the fall, so refusing every stump left the
                    // ripple running over a toppling tree.
                    case StardewValley.TerrainFeatures.Tree tree when tree.growthStage.Value >= 5 && (!tree.stump.Value || tree.falling.Value) && tree.texture?.Value != null:
                        // The same turn the tree is drawn with, or the carve stays where the tree
                        // was standing: the wind leans it a couple of pixels and a chopping stroke
                        // swings it right over, and the ripple would run across whatever leaned out
                        // of the hole.
                        float treeTurn = tree.shakeRotation + FoliageSway.TiltForTileBase(tile.X, tile.Y);
                        spriteBatch.Draw(tree.texture.Value,
                            Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                            ShadowRenderer.TreeCanopySourceRect(tree), Color.White * TreeMaskAlpha(tree), treeTurn, new Vector2(24f, 96f), 4f,
                            tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                        // The TRUNK is a second draw in Tree.draw (16x32 at tile*64+(0,-64), origin
                        // zero, +96 for moss) and was never stamped: the canopy rect above covers
                        // the tree's top six tiles but the trunk piece is drawn separately, so a
                        // palm planted at the oasis pond kept its lower trunk inside the ripple
                        // while its crown was carved. Same opacity as the canopy for the same reason.
                        spriteBatch.Draw(tree.texture.Value,
                            Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f, tile.Y * 64f - 64f)),
                            new Rectangle(tree.hasMoss.Value ? 128 : 32, 96, 16, 32),
                            Color.White * TreeMaskAlpha(tree), 0f, Vector2.Zero, 4f,
                            tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                        break;
                    // Mature fruit tree: 48x64 seasonal foliage at tile*64+(32,64), origin (24,80).
                    // Same gate and same shake as the wild tree above.
                    case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4 && (!ft.stump.Value || ft.falling.Value) && ft.texture != null:
                        int season = Game1.GetSeasonIndexForLocation(ft.Location);
                        var fsrc = new Rectangle((12 + season * 3) * 16, ft.GetSpriteRowNumber() * 5 * 16, 48, 64);
                        spriteBatch.Draw(ft.texture,
                            Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                            fsrc, Color.White, ft.shakeRotation, new Vector2(24f, 80f), 4f,
                            ft.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                        break;
                    // Bush: bottom-centre = (tile.X*64 + (eff+1)*32, (tile.Y+1)*64) — the shadow baker's anchor.
                    case StardewValley.TerrainFeatures.Bush bush when !bush.sourceRect.Value.IsEmpty:
                        StampBush(spriteBatch, bush, tile);
                        break;
                    // Everything the game still draws as a tree BELOW canopy stage: seeds,
                    // sprouts, saplings, bush-stage growth, stumps. The case above takes only
                    // stage 5 and up, so a sapling growing at the water's edge was left inside
                    // the ripple and the effect ran over the plant. The shadow pass has taken
                    // every stage since it was written, which is the asymmetry to watch for
                    // here: the same object is a THING to one half of the mod and scenery to
                    // the other. Same split that left buildings and the big bushes out.
                    case StardewValley.TerrainFeatures.Tree small when small.texture?.Value != null:
                        StampSmallTree(spriteBatch, small, tile);
                        break;
                    // A planted crop is a plant standing on the soil like any of the above.
                    // Crop.draw puts origin (8,24) at drawPosition, and mirrors the sprite at
                    // random, so both have to be reproduced or the carve lands beside the
                    // plant rather than on it.
                    case StardewValley.TerrainFeatures.HoeDirt { crop: { } crop }
                         when !crop.forageCrop.Value && !crop.IsErrorCrop()
                              && crop.DrawnCropTexture != null && !crop.sourceRect.IsEmpty:
                        spriteBatch.Draw(crop.DrawnCropTexture,
                            Game1.GlobalToLocal(Game1.viewport, crop.drawPosition),
                            crop.sourceRect, Color.White, 0f, new Vector2(8f, 24f), 4f,
                            crop.flip.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                        break;
                }
            }

        }

        private void StampLargeTerrainFeatures(SpriteBatch spriteBatch, GameLocation location)
        {
            // The BIG bushes live in a different list, which is the same split the reflection
            // stamp had to learn: terrainFeatures is tile-keyed and holds the small stuff, while
            // the decorative bushes a map places - and everything a content pack adds as
            // scenery - are largeTerrainFeatures. The mirror was taught both lists and this mask
            // was not, so a big bush overhanging a bank stayed inside the ripple and shimmered
            // like the water it was standing in, while the planted bush beside it, identical to
            // look at, sat still. Same list, same anchor, same cull radius as the mirror.
            foreach (var ltf in location.largeTerrainFeatures)
            {
                if (ltf is not StardewValley.TerrainFeatures.Bush lbush || lbush.sourceRect.Value.IsEmpty)
                    continue;
                Vector2 ltile = lbush.Tile;
                if (!WaterWithinTiles((int)ltile.X, (int)ltile.Y + 4, 7))
                    continue;
                StampBush(spriteBatch, lbush, ltile);
            }

        }

        private void StampBuildings(SpriteBatch spriteBatch, GameLocation location)
        {
            // Buildings. A shed or a coop at the water's edge is an entity drawn from its own
            // texture, so nothing above could see it, and the ripple ran straight through the
            // building: reported as "notice the effect on the water behind" with a before and
            // after shot of placing a coop. The mirror learned buildings and this mask did not,
            // the same split the bushes above had.
            //
            // Building.draw pins the art's bottom-LEFT corner at
            // (tileX*64, (tileY + tilesHigh)*64) + DrawOffset*4 at scale 4, so the top-left the
            // stamp needs is that base line minus the source height. One under construction or
            // mid-move is not drawn, so it must not be masked either.
            foreach (var bld in location.buildings)
            {
                if (bld?.texture?.Value == null || bld.isMoving || bld.daysOfConstructionLeft.Value > 0)
                    continue;
                // FishPond.draw draws its 80x80 rim whatever the data's source rect says; read the same.
                Rectangle bsrcRect = bld is StardewValley.Buildings.FishPond ? FishPondRimSourceRect : bld.getSourceRect();
                if (bsrcRect.IsEmpty)
                    continue;
                Vector2 bOffset = (bld.GetData()?.DrawOffset ?? Vector2.Zero) * 4f;
                float bLeftX = bld.tileX.Value * 64f + bOffset.X;
                float bBaseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f + bOffset.Y;
                // Same reach the mirror uses: a barn is six source tiles tall, so its art covers
                // water a long way above the tile it is filed under.
                if (!WaterWithinTiles((int)((bLeftX + bsrcRect.Width * 2f) / 64f), (int)(bBaseY / 64f) - 3, 9))
                    continue;
                spriteBatch.Draw(bld.texture.Value,
                    Game1.GlobalToLocal(Game1.viewport, new Vector2(bLeftX, bBaseY - bsrcRect.Height * 4f)),
                    bsrcRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                // FishPond.draw hangs a netting frame (80x48 of the sheet, one of three) from two
                // tiles above the pond down to the foot of its top rim row, over whatever stands
                // behind the pond: on a farm where that is the lake, the ripple ran through the net.
                if (bld is StardewValley.Buildings.FishPond pond && pond.nettingStyle.Value < 3)
                {
                    var netting = new Rectangle(80, pond.nettingStyle.Value * 48, 80, 48);
                    spriteBatch.Draw(bld.texture.Value,
                        Game1.GlobalToLocal(Game1.viewport, new Vector2(bld.tileX.Value * 64f, bld.tileY.Value * 64f - 128f)),
                        netting, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                }
            }

        }

        // Bush: bottom-centre = (tile.X*64 + (eff+1)*32, (tile.Y+1)*64), the shadow baker's anchor.
        // Pre-canopy tree stages, from Tree.draw's own source rects (the same table the
        // shadow baker reads). Bottom-centred on the tile's bottom edge rather than
        // reproducing vanilla's per-stage pin: these sprites are 16x16 or 16x32, so at
        // scale 4 the stamp covers its tile, and a carve that covers slightly more than
        // the plant costs nothing while one that covers slightly less leaves the ripple
        // running across a leaf, which is the thing being fixed.
        private void StampSmallTree(SpriteBatch spriteBatch, StardewValley.TerrainFeatures.Tree t, Vector2 at)
        {
            Rectangle src = t.stump.Value
                ? new Rectangle(32, 96, 16, 32)
                : t.growthStage.Value switch
                {
                    0 => new Rectangle(32, 128, 16, 16),   // seed
                    1 => new Rectangle(0, 128, 16, 16),    // sprout
                    2 => new Rectangle(16, 128, 16, 16),   // sapling
                    _ => new Rectangle(0, 96, 16, 32),     // bush stage (3-4)
                };
            spriteBatch.Draw(t.texture.Value,
                Game1.GlobalToLocal(Game1.viewport, new Vector2(at.X * 64f + 32f, (at.Y + 1) * 64f)),
                src, Color.White, 0f, new Vector2(src.Width / 2f, src.Height), 4f,
                t.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        }


        private void StampBush(SpriteBatch spriteBatch, StardewValley.TerrainFeatures.Bush b, Vector2 at)
        {
            var bsrc = b.sourceRect.Value;
            int eff = b.size.Value switch { 3 => 0, 4 => 1, _ => b.size.Value };
            spriteBatch.Draw(StardewValley.TerrainFeatures.Bush.texture.Value,
                Game1.GlobalToLocal(Game1.viewport, new Vector2(at.X * 64f + (eff + 1) * 32f, (at.Y + 1) * 64f)),
                bsrc, Color.White, 0f, new Vector2(bsrc.Width / 2f, bsrc.Height), 4f,
                b.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        }


        private static void StampSprite(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Rectangle bb)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(bb.Center.X, bb.Bottom));
            spriteBatch.Draw(texture, feet, src, Color.White, 0f,
                new Vector2(src.Width / 2f, src.Height), 4f, SpriteEffects.None, 0f);
        }
    }
}
