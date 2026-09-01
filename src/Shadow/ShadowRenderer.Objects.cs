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
    /// craftable/critter, each of which the game hands us as an entity we can ask for its sprite.
    /// EmitObject routes every one of them through the shared bake cache.
    /// Props painted into the map instead of placed as entities live in ShadowRenderer.TileProps.cs.
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>
        /// One entry point for object shadows: during the BAKE pass (RenderingWorld) it renders the
        /// sprite+gradient to a pooled RT keyed by the SPRITE (texture+src+flip — every identical
        /// crop/tree/bush shares one bake); during the COMPOSITE pass it draws that baked RT leaning
        /// by the sun (smooth, no bands). Falls back to <see cref="DrawBandedGradient"/> only when
        /// the sprite is too big for a slot or wasn't baked.
        /// </summary>
        /// <param name="groundAnchorWorldY">The caster's own contact row in world pixels. Given
        /// one, the shadow is cut along its length and each piece sorted at the depth of the floor
        /// row it lies on (see <see cref="ShadowPieceDepth"/>) rather than all of it at
        /// <paramref name="depth"/>. Only buildings ask for this: everything else on this path
        /// carries a per-column tie-break inside its depth that a world Y cannot hold.</param>
        private void EmitObject(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, float stretch, float depth, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None,
            ShadowGeometry geometry = ShadowGeometry.Solid, float? groundAnchorWorldY = null,
            Color? shadowColor = null)
        {
            var key = (texture, src, effects);
            // The lean is baked into the pixels as the projection that lays this caster down: a
            // card keeps its width level on the screen (the shear this always was, and what a
            // fence's shadow is), a solid lays its width across the sun's direction on the ground,
            // foreshortened the way the ground is. The tip of a column of any height lands in the
            // same place under both, so the two never disagree about where the sun is. See
            // ShadowProjection for the geometry and why a solid's width has to lie down.
            ShadowProjection projection = geometry == ShadowGeometry.Card
                ? ShadowProjection.ForCard(rot, stretch)
                : ShadowProjection.ForSolid(rot, stretch, _groundForeshortening);
            if (_isBakingObjects)
            {
                if (_objectGraphicsDevice != null && !_bakedObjectCache.ContainsKey(key)
                    && BakeObjectSprite(_objectGraphicsDevice, texture, src, baseOrigin, effects, projection, blur, out RenderTarget2D rt, out Vector2 feetInRT))
                    _bakedObjectCache[key] = new SpriteBake { Rt = rt, FeetInRt = feetInRT, BakedProjection = projection, BakedBlur = blur, Content = _lastBakeContent, SlotClass = _lastBakeClass, BakedScale = _lastBakeScale, LastUsedTick = Game1.ticks };
                return;
            }
            if (_bakedObjectCache.TryGetValue(key, out SpriteBake? bakedEntry))
            {
                bakedEntry.LastUsedTick = Game1.ticks;
                // The lean is in the PIXELS, so the sun walking across the sky makes every bake
                // gradually wrong. That used to be answered by throwing the whole cache away
                // whenever a rounded sun angle changed, which on a continuous clock happens about
                // twice a second: a hundred-sprite screen re-baked a hundred times a second, in
                // bursts, all day. Now each sprite is judged on its own error: how far its
                // farthest pixel has moved between the projection in the pixels and the one the
                // sun asks for now, in screen pixels. A tall tree earns a re-bake every second or
                // so and a small crop goes minutes without one, which is both correct and an order
                // of magnitude less work than the old sweep.
                if ((projection.Drift(bakedEntry.BakedProjection, src.Width, src.Height) * 4f > ShearRefreshPixels
                        || Math.Abs(blur - bakedEntry.BakedBlur) > 0.3f)
                    && _objectBakeQueue.Count < ObjectBakeQueueCap)
                    _objectBakeQueue[key] = new ObjectBakeRequest { BaseOrigin = baseOrigin, Projection = projection, Blur = blur };
                FrameCost.Count(FrameCost.Counter.ShadowSprites);
                // ONE draw of ONLY the content: the soft edge is in the baked pixels (see
                // SpriteBake.BakedBlur) and the source rect stops the card blending the slot's
                // acres of transparent padding (see ContentBounds). Origin re-anchors because a
                // source rect makes the draw's coordinates content-relative.
                Rectangle content = bakedEntry.Content.IsEmpty ? new Rectangle(0, 0, bakedEntry.Rt.Width, bakedEntry.Rt.Height) : bakedEntry.Content;
                // One slot texel is BakedScale screen pixels of silhouette, so the draw undoes the
                // bake's scale. Everything that fits a slot bakes at 4 and this is 1, exactly as
                // before; only the sprites that used to draw as bands come back magnified.
                float unbake = 4f / bakedEntry.BakedScale;
                Vector2 bakedOrigin = bakedEntry.FeetInRt - new Vector2(content.X, content.Y);
                // The bake already holds a black, soft-edged silhouette, so it is tinted WHITE to
                // come out as itself. A mask caller wants the same pixels read as coverage, which
                // is the same white: the two agree, and only the banded fallback has to be told.
                if (groundAnchorWorldY is float bakedAnchor)
                    DrawSoftGrounded(spriteBatch, Taps9, bakedEntry.Rt, content, feet, Color.White, alpha, 0f,
                        bakedOrigin, new Vector2(unbake, unbake), bakedAnchor, SpriteEffects.None, 0f);
                else
                    DrawSoft(spriteBatch, Taps9, bakedEntry.Rt, content, feet,
                        Color.White, alpha, 0f, bakedOrigin,
                        new Vector2(unbake, unbake), depth, SpriteEffects.None, 0f);
            }
            else
            {
                // A sprite too big for the largest slot can never be baked, so asking for it is a
                // request that fails every frame for the rest of the session. Forest reported
                // twenty bake misses per frame, dead flat - not twenty this frame and eleven the
                // next, the SAME twenty forever - with the cache at 65 of 464 slots and no
                // evictions. That is not a cache under pressure, which is what a miss count is
                // there to catch; it is the same handful of sprites being queued, attempted,
                // refused and redrawn as bands, over and over.
                //
                // The fit test is a pure function of the sprite and the sun, so it can be asked
                // here for nothing instead of being discovered inside a bake that then throws its
                // work away. It also un-asks itself: the need grows with the lean, so a sprite too
                // big at a low sun fits again as the sun rises, with no state to go stale.
                bool tooBig = !ObjectBakeCouldFit(src, baseOrigin, projection, blur);
                // Named here, once each. The refusal used to be logged inside the bake, which the
                // pre-check now means these sprites never reach: the counter would say twenty and
                // nothing anywhere would say WHICH twenty.
                if (tooBig)
                    NoteOversize(src, baseOrigin, projection);
                if (!tooBig && _objectBakeQueue.Count < ObjectBakeQueueCap)
                    _objectBakeQueue[key] = new ObjectBakeRequest { BaseOrigin = baseOrigin, Projection = projection, Blur = blur };
                // Counted here rather than at the queue insert: the queue is a dictionary keyed by
                // sprite, so two misses of the SAME sprite in one frame collapse into one entry and
                // the count would under-report exactly the case it exists to catch. A miss is a
                // drawing that came out as bands instead of a silhouette, and every one of them is
                // worth knowing about.
                //
                // Counted apart from the misses, because they need opposite readings. A miss says
                // wait, the picture is about to improve; this says it is not going to, and the two
                // were indistinguishable in the report while one of them was almost all of it.
                FrameCost.Count(tooBig ? FrameCost.Counter.BakeTooBig : FrameCost.Counter.BakeMisses);
                FrameCost.Count(FrameCost.Counter.ShadowSprites);
                // groundSorted: false — this is a sort depth, not a world row. See the parameter's
                // note: an object's depth carries a per-column tie-break that a world Y cannot
                // hold, so the object path keeps one depth for the whole shadow for now.
                DrawBandedGradient(spriteBatch, texture, src, feet, baseOrigin, alpha, rot,
                    new Vector2(4f, 4f * stretch), groundAnchorWorldY ?? depth, blur, headFade, effects,
                    groundSorted: groundAnchorWorldY.HasValue, shadowColor: shadowColor);
            }
        }

        /// <summary>How far the baked lean may drift from the true one, at the sprite's tip, before
        /// it is worth a re-bake. Under a pixel and a half nothing is visible; the whole point is
        /// that this is measured per sprite instead of assumed for all of them.</summary>
        private const float ShearRefreshPixels = 1.5f;
        /// <summary>Sprites the bake pass will be asked for in one frame. Misses and stale leans
        /// share it; misses are served first because a miss is a visibly different drawing.</summary>
        private const int ObjectBakeQueueCap = 128;
        /// <summary>Stale leans re-baked per frame. A screen full of trees crossing the threshold
        /// together must not turn into one long frame; a couple of frames of a lean that is two
        /// pixels off is not something anyone can see.</summary>
        private const int MaxShearRefreshesPerFrame = 12;

        /// <summary>Bake a sprite (black + feet→head gradient) to a pooled object RT, laid down by
        /// its projection about the feet point so the sun's lean is in the pixels.
        /// Returns false (→ banded fallback) only if it fits no slot at any bake scale.</summary>
        private bool BakeObjectSprite(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle src, Vector2 baseOrigin,
            SpriteEffects effects, ShadowProjection projection, float blurPx, out RenderTarget2D rt, out Vector2 feetInRT,
            RenderTarget2D? into = null)
        {
            rt = null!;
            feetInRT = default;
            if (texture == null || src.IsEmpty)
                return false;
            // The bake-time blur stamps the silhouette shifted by up to the blur radius in every
            // direction, so the fit test keeps that much slack or the soft edge clips at the slot.
            // A refresh re-renders the slot the entry already owns and must keep its size; a first
            // bake takes the smallest class the silhouette fits, which is what stops a crop from
            // being handed a tree's slot.
            if (!ChooseBakeFit(src, baseOrigin, projection, blurPx, into,
                               out int slotClass, out float scale, out float blurTexels,
                               out float left, out float right, out float top, out float bottom))
            {
                NoteOversize(src, baseOrigin, projection);
                return false;   // nothing fits, at any scale (a refresh: the lean grew past its own slot)
            }
            float spriteWidth = src.Width * scale, spriteHeight = src.Height * scale;
            _lastBakeClass = slotClass;
            _lastBakeScale = scale;
            rt = into ?? RentObjectRT(graphicsDevice, slotClass);
            // The feet go wherever the laid-down silhouette, blur and all, sits inside the slot. A
            // sideways shadow has as much to one side of its feet as the other, and a solid's near
            // edge dips below the feet row, so neither the slot's centre column nor its bottom row
            // can be assumed the way they were when only a shear was baked.
            feetInRT = new Vector2(
                (float)Math.Round(rt.Width * 0.5f - (left + right) * 0.5f),
                (float)Math.Round(rt.Height - bottom - blurTexels - 1f));
            var bakeScale = new Vector2(scale, scale);
            Vector2 pos = feetInRT - baseOrigin * bakeScale;  // so baseOrigin maps to the feet point
            Matrix lean = projection.About(feetInRT);
            try
            {
                graphicsDevice.SetRenderTarget(rt);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                _renderTargetSpriteBatch.Draw(texture, pos, src, Color.Black, 0f, Vector2.Zero, bakeScale, effects, 0f);
                _renderTargetSpriteBatch.End();
                // Continuous feet(full)→head(faint) gradient over the sprite's vertical extent,
                // laid down by the same projection so it follows the silhouette wherever that put
                // it. Drawn upright over the sprite it would fade the wrong rows now that rows no
                // longer stay where the sprite had them.
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                _renderTargetSpriteBatch.Draw(_gradientTexture!, pos, null, Color.White, 0f, Vector2.Zero,
                    new Vector2(spriteWidth / _gradientTexture!.Width, spriteHeight / _gradientTexture.Height), SpriteEffects.None, 0f);
                _renderTargetSpriteBatch.End();
                BlurSlotInPlace(graphicsDevice, rt, blurTexels);
                _lastBakeContent = ContentBounds(feetInRT, left, right, top, bottom, blurTexels, rt.Width, rt.Height);
                FrameCost.Count(FrameCost.Counter.ObjectBakes);
                return true;
            }
            catch
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                // Only a lease taken here is given back. A refresh was handed a slot the cache
                // entry still owns, and returning that to the free list would let two entries be
                // drawn from one target.
                if (into == null)
                {
                    _objectFreeTargetsByClass[slotClass].Add(rt);
                    rt = null!;
                }
                return false;
            }
        }

        /// <summary>Bake exactly what the draw pass reported missing or stale — the warm-frame
        /// counterpart of the full enumeration. Each entry carries the origin and the damped,
        /// per-type shear recorded at draw time, so the result is byte-identical to what the
        /// full walk would have produced for the same sprite.</summary>
        private void BakeQueuedObjectSprites(GraphicsDevice graphicsDevice)
        {
            // MISSES first, and unbudgeted: a sprite with no bake at all is drawing as bands,
            // which is a different picture, not a slightly older one.
            foreach (var kv in _objectBakeQueue)
            {
                var key = kv.Key;
                ObjectBakeRequest req = kv.Value;
                if (_bakedObjectCache.ContainsKey(key))
                    continue;
                if (BakeRequest(graphicsDevice, key, req, null, out RenderTarget2D rt, out Vector2 feetInRT))
                    _bakedObjectCache[key] = new SpriteBake { Rt = rt, FeetInRt = feetInRT, BakedShear = req.Shear, BakedProjection = req.Projection, BakedBlur = req.Blur, Content = _lastBakeContent, SlotClass = _lastBakeClass, BakedScale = _lastBakeScale, LastUsedTick = Game1.ticks };
            }

            // Then the leans the sun has moved off, re-rendered into the slot each entry already
            // owns, up to the per-frame budget. Whatever does not fit gets asked for again next
            // frame, because the error that queued it is still there.
            int refreshBudget = MaxShearRefreshesPerFrame;
            foreach (var kv in _objectBakeQueue)
            {
                if (refreshBudget <= 0)
                    break;
                var key = kv.Key;
                ObjectBakeRequest req = kv.Value;
                if (!_bakedObjectCache.TryGetValue(key, out SpriteBake? stale)
                    || (stale.BakedBlur == req.Blur
                        && (req.ColumnSources != null ? stale.BakedShear == req.Shear
                                                      : stale.BakedProjection.Same(req.Projection))))
                    continue;
                if (BakeRequest(graphicsDevice, key, req, stale.Rt, out _, out Vector2 refreshedFeet))
                {
                    stale.FeetInRt = refreshedFeet;
                    stale.BakedShear = req.Shear;
                    stale.BakedProjection = req.Projection;
                    stale.BakedBlur = req.Blur;
                    stale.Content = _lastBakeContent;
                    stale.SlotClass = _lastBakeClass;
                    stale.BakedScale = _lastBakeScale;
                    refreshBudget--;
                }
                else
                {
                    // The lean grew until the sheared sprite no longer fits a slot. Keeping the
                    // old pixels would freeze that shadow at whatever angle it last fit at, so
                    // hand the slot back and let the draw path fall to bands, which has no such
                    // limit. Only reachable at a very low sun on a sprite near the slot width.
                    _objectFreeTargetsByClass[stale.SlotClass].Add(stale.Rt);
                    _bakedObjectCache.Remove(key);
                }
            }
        }

        /// <summary>
        /// The rectangle of a slot that actually holds shadow, computed from the same geometry
        /// the bake just drew with. Everything outside it is transparent padding - and until this
        /// existed, every one of those pixels was rasterized anyway, per shadow, per frame: the
        /// cached slots were drawn with a NULL source rect, so a 3-tile crop shadow submitted the
        /// full 400x456 slot to the card and alpha blending obligingly read and wrote 180
        /// thousand pixels to show nine thousand. Five hundred shadows made that a hundred
        /// million pixels a frame of blending nothing over nothing, which is exactly the
        /// fill-without-submission signature the frame clock kept showing and the CPU probes
        /// kept not. (Factorio's shadow-trimming write-up, FFF-227, is the same lesson on the
        /// same kind of sprite.)
        ///
        /// <para>Analytic, not measured from pixels: the sprite lands at a known position, the
        /// shear slides rows sideways by a known amount that is largest at the top row, and the
        /// blur pushes everything outward by its radius. The union of those is the content, no
        /// GPU readback required. Clamped to the slot, snapped outward to whole pixels.</para>
        /// </summary>
        private static Rectangle ContentBounds(Vector2 pos, float widthPx, float heightPx, Vector2 feetInRT, float shear, float blurPx, int slotW, int slotH)
        {
            float topShift = shear * (pos.Y - feetInRT.Y);   // sideways slide of the top row
            float left = Math.Min(pos.X, pos.X + topShift) - blurPx;
            float right = Math.Max(pos.X + widthPx, pos.X + widthPx + topShift) + blurPx;
            float top = pos.Y - blurPx;
            float bottom = Math.Max(feetInRT.Y, pos.Y + heightPx) + blurPx;
            int x0 = Math.Max(0, (int)left), y0 = Math.Max(0, (int)top);
            int x1 = Math.Min(slotW, (int)Math.Ceiling(right)), y1 = Math.Min(slotH, (int)Math.Ceiling(bottom));
            return x1 <= x0 || y1 <= y0 ? new Rectangle(0, 0, slotW, slotH) : new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>The same, for a sprite laid down by a projection: the bounds the fit test
        /// already worked out, placed at the feet and pushed out by the blur.</summary>
        private static Rectangle ContentBounds(Vector2 feetInRT, float left, float right, float top, float bottom, float blurPx, int slotW, int slotH)
        {
            int x0 = Math.Max(0, (int)Math.Floor(feetInRT.X + left - blurPx));
            int y0 = Math.Max(0, (int)Math.Floor(feetInRT.Y + top - blurPx));
            int x1 = Math.Min(slotW, (int)Math.Ceiling(feetInRT.X + right + blurPx));
            int y1 = Math.Min(slotH, (int)Math.Ceiling(feetInRT.Y + bottom + blurPx));
            return x1 <= x0 || y1 <= y0 ? new Rectangle(0, 0, slotW, slotH) : new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>What the last successful bake computed as its content rect, for the caller
        /// that stores the cache entry. Single-threaded by construction (bakes run on the main
        /// thread inside RenderingWorld), so a field is safe where an out-param would have to be
        /// threaded through both funnels and the request plumbing.</summary>
        private Rectangle _lastBakeContent = new(0, 0, ObjectRtW, ObjectRtH);
        private int _lastBakeClass;
        private float _lastBakeScale = 4f;

        /// <summary>Which pool a target came from, recovered from its size. Cheaper and less
        /// error-prone than threading the class through every refresh path.</summary>
        private static int ClassOfSlot(RenderTarget2D rt)
        {
            for (int i = 0; i < ObjectSlotClasses.Length; i++)
                if (rt.Width == ObjectSlotClasses[i].W && rt.Height == ObjectSlotClasses[i].H)
                    return i;
            return ObjectSlotClasses.Length - 1;
        }

        /// <summary>
        /// Soften a freshly baked slot IN PLACE: copy it aside, then stamp the copy back nine
        /// times, each shifted by the blur radius and carrying a ninth of the weight. Additive
        /// blending makes that the true mean of nine shifted copies - the core where all nine
        /// land stays solid, the rim fades over the blur radius - which is a cleaner gradient
        /// than the per-frame alpha-compositing trick this replaces, and it runs once per BAKE
        /// (a handful per second on a warm screen) instead of five times per sprite per frame.
        /// </summary>
        private void BlurSlotInPlace(GraphicsDevice graphicsDevice, RenderTarget2D rt, float blurTexels)
        {
            if (blurTexels <= 0f)
                return;
            int cls = ClassOfSlot(rt);
            _objectBlurScratches[cls] ??= VramTally.Track(new RenderTarget2D(graphicsDevice, rt.Width, rt.Height,
                false, SurfaceFormat.Color, DepthFormat.None), "object blur scratch");
            RenderTarget2D scratch = _objectBlurScratches[cls]!;
            graphicsDevice.SetRenderTarget(scratch);
            graphicsDevice.Clear(Color.Transparent);
            _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            _renderTargetSpriteBatch.Draw(rt, Vector2.Zero, Color.White);
            _renderTargetSpriteBatch.End();
            graphicsDevice.SetRenderTarget(rt);
            graphicsDevice.Clear(Color.Transparent);
            _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, SumTaps, SamplerState.LinearClamp);
            Color weight = Color.White * (1f / Taps9.Length);
            foreach (Vector2 tap in Taps9)
                _renderTargetSpriteBatch.Draw(scratch, tap * blurTexels, weight);
            _renderTargetSpriteBatch.End();
        }

        /// <summary>Run one queued request, whichever of the two kinds of bake it is.</summary>
        private bool BakeRequest(GraphicsDevice graphicsDevice, (Texture2D texture, Rectangle src, SpriteEffects effect) key,
            ObjectBakeRequest req, RenderTarget2D? into, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            if (req.ColumnSources != null && req.ColumnLevels != null)
                return BakeTileColumn(graphicsDevice, key.texture, req.ColumnSources, req.ColumnLevels,
                    req.ColumnOrients, req.ColumnSources.Length, req.Shear, req.Blur, out rt, out feetInRT, into);
            return BakeObjectSprite(graphicsDevice, key.texture, key.src, req.BaseOrigin, key.effect,
                req.Projection, req.Blur, out rt, out feetInRT, into);
        }

        /// <summary>Shear about a pivot row: x' = x + k·(y − pivot.Y), y unchanged — the horizontal
        /// slide grows with height above the feet, which is exactly a cast-shadow lean.</summary>
        private static Matrix ShearAbout(Vector2 pivot, float shearAmount)
        {
            return Matrix.CreateTranslation(-pivot.X, -pivot.Y, 0f)
                 * new Matrix(1f, 0f, 0f, 0f, shearAmount, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f)
                 * Matrix.CreateTranslation(pivot.X, pivot.Y, 0f);
        }

        /// <summary>The player's shadow-length setting, remembered when the sun is computed so the
        /// object pass can reach it. Both callers of <see cref="DrawObjectShadows"/> set it.</summary>
        private float _sunLengthScale = 1f;

        /// <summary>How much the ground is foreshortened on screen, this pass; see
        /// <see cref="ModConfig.ShadowGroundForeshortening"/>.</summary>
        private float _groundForeshortening = 0.58f;

        /// <summary>The people's own, this pass; see
        /// <see cref="ModConfig.ShadowCharacterGroundForeshortening"/>.</summary>
        private float _characterGroundForeshortening = 1f;

        /// <summary>
        /// The source rect a tree's canopy is really drawn from, which is not always the first
        /// column of its sheet.
        /// </summary>
        /// <remarks>
        /// Tree.draw picks between three columns of the same 48x96 rect: the plain one, the one at
        /// x=48 for a tree carrying seed or not yet shaken today, and the one at x=96 for a mossy
        /// tree. Everything here that draws a tree - its shadow, its reflection, and the stencil
        /// that keeps the water effect off it - took the first column unconditionally, so a desert
        /// palm holding a coconut was masked with the shape of a palm holding nothing. Reported as
        /// the water effect running over the palm fronds around the oasis, which is exactly what a
        /// mis-shaped stencil looks like from above.
        /// </remarks>
        internal static Rectangle TreeCanopySourceRect(Tree tree)
        {
            Rectangle rect = Tree.treeTopSourceRect;
            var data = tree.GetData();
            rect.X = tree.hasMoss.Value ? 96
                   : (data != null
                      && ((data.UseAlternateSpriteWhenSeedReady && tree.hasSeed.Value)
                          || (data.UseAlternateSpriteWhenNotShaken && !tree.wasShakenToday.Value)))
                     ? 48
                     : 0;
            return rect;
        }

        /// <summary>The kinds of caster that carry their own shadow length and softness. The split
        /// is the one the draw pass already made when the numbers were constants; naming it is what
        /// lets a player reach them.</summary>
        internal enum ShadowKind { Trees, SmallTrees, Bushes, Crops, Grass, Objects, Buildings }

        /// <summary>Per-kind length ceilings and softness multipliers, read from config once per
        /// pass rather than per caster. Sized from the enum so adding a kind cannot leave a hole.</summary>
        private readonly float[] _kindLengthCaps = new float[Enum.GetValues<ShadowKind>().Length];
        private readonly float[] _kindSoftness = new float[Enum.GetValues<ShadowKind>().Length];
        private readonly float[] _kindLean = new float[Enum.GetValues<ShadowKind>().Length];

        /// <summary>Which setting holds this kind's length ceiling. The one place the mapping is
        /// written down: the draw pass and the diagnostic both come here, so a report can no longer
        /// quote a number the renderer stopped using.</summary>
        internal static float LengthCapFor(ModConfig config, ShadowKind kind) => kind switch
        {
            ShadowKind.Trees => config.ShadowLengthTrees,
            ShadowKind.SmallTrees => config.ShadowLengthSmallTrees,
            ShadowKind.Bushes => config.ShadowLengthBushes,
            ShadowKind.Crops => config.ShadowLengthCrops,
            ShadowKind.Grass => config.ShadowLengthGrass,
            ShadowKind.Buildings => config.ShadowLengthBuildings,
            _ => config.ShadowLengthObjects,
        };

        /// <summary>Which setting holds this kind's lean, as a fraction of the sun's angle.</summary>
        internal static float LeanFor(ModConfig config, ShadowKind kind) => kind switch
        {
            ShadowKind.Trees => config.ShadowLeanTrees,
            ShadowKind.SmallTrees => config.ShadowLeanSmallTrees,
            ShadowKind.Bushes => config.ShadowLeanBushes,
            ShadowKind.Crops => config.ShadowLeanCrops,
            ShadowKind.Grass => config.ShadowLeanGrass,
            ShadowKind.Buildings => config.ShadowLeanBuildings,
            _ => config.ShadowLeanObjects,
        };

        /// <summary>Which setting holds this kind's softness multiplier.</summary>
        internal static float SoftnessFor(ModConfig config, ShadowKind kind) => kind switch
        {
            ShadowKind.Trees => config.ShadowSoftnessTrees,
            ShadowKind.SmallTrees => config.ShadowSoftnessSmallTrees,
            ShadowKind.Bushes => config.ShadowSoftnessBushes,
            ShadowKind.Crops => config.ShadowSoftnessCrops,
            ShadowKind.Grass => config.ShadowSoftnessGrass,
            ShadowKind.Buildings => config.ShadowSoftnessBuildings,
            _ => config.ShadowSoftnessObjects,
        };

        /// <summary>Take this frame's per-kind settings, so the draw pass reads an array rather than
        /// walking a switch per caster. Called wherever <see cref="_sunLengthScale"/> is set, which
        /// is both entries into the object pass.</summary>
        private void CaptureKindTuning(ModConfig config)
        {
            foreach (ShadowKind kind in Enum.GetValues<ShadowKind>())
            {
                _kindLengthCaps[(int)kind] = LengthCapFor(config, kind);
                _kindSoftness[(int)kind] = SoftnessFor(config, kind);
                _kindLean[(int)kind] = LeanFor(config, kind);
            }
            _groundForeshortening = config.ShadowGroundForeshortening;
            _characterGroundForeshortening = config.ShadowCharacterGroundForeshortening;
            _shadowModel = config.DirectionalShadowModel;
        }

        /// <summary>Whether this kind casts at all this frame: its own length dial above zero.
        ///
        /// <para>A shadow with no length is no shadow, so the bottom of each kind's dial is where
        /// that kind is switched off, and the check sits BEFORE the work rather than inside the
        /// draw. That is the whole point of it. On a farm screen measured 2026-09-01, 158 of the
        /// 200 casters the object pass drew were tufts of grass, and object shadows cost 0.252 ms
        /// of processor time against a noise floor of 0.005. There was no way to spend less of
        /// that without giving up every object shadow on the map: the per-kind dials are length,
        /// softness and lean, and all three are appearance, so a player who turned them all the
        /// way down - which is exactly what was reported - changed how the grass shadows looked
        /// and not how many were drawn.</para>
        ///
        /// <para>Nothing pops. The dial's old floor was 0.05, where a shadow is already a few
        /// pixels of smudge under the thing casting it, so the last step down to nothing is
        /// smaller than any step before it.</para>
        /// </summary>
        private bool KindCasts(ShadowKind kind) => _kindLengthCaps[(int)kind] > 0f;

        /// <summary>Which shape this frame's shadows are. Read once per pass beside the rest of the
        /// per-frame tuning, so the draw never reaches for the config.</summary>
        private ShadowModel _shadowModel = ShadowModel.Modern;

        /// <summary>True while the 1.6 shapes are chosen.</summary>
        private bool ClassicShadowShapes => _shadowModel == ShadowModel.Classic;

        /// <summary>This caster's own reach: its kind's ceiling, scaled by the overall length slider.
        ///
        /// <para>
        /// The ceiling is the ONLY lever that may be pulled to stop a canopy cast detaching from its
        /// own trunk. Two constants used to damp the tree's and the props' sun ANGLE instead, at 0.38
        /// and 0.6 of the character angle, and that is a different sun for each of them. The geometry
        /// says so plainly: the silhouette is sheared by <c>-sin(rot)&#183;stretch</c> sideways and
        /// <c>cos(rot)&#183;stretch</c> upward, so the tip lands at an angle of exactly <c>-rot</c>
        /// and the stretch cancels out of it. Shortening a shadow leaves its direction alone; damping
        /// the angle moves the sun.
        /// </para>
        ///
        /// <para>
        /// Reported, correctly, as two suns: at six in the morning the player's shadow pointed one
        /// way and every tree's pointed another, and the reporter had measured the difference in
        /// clock hours before writing in. Everything the sun casts takes one angle, and these
        /// ceilings decide only how far each thing reaches.
        /// </para></summary>
        private float LengthOf(float stretch, ShadowKind kind) => LengthCap(stretch, _kindLengthCaps[(int)kind]);

        /// <summary>This caster's own soft edge: the overall blur times its kind's softness.
        ///
        /// <para>A blur radius is in screen pixels, so the same number is a soft edge on a short
        /// shadow and a hard one on a long one. That is why raising the crop ceiling in 1.5.4 read
        /// as "sharper" without anything about the blur changing: the same five pixels were now
        /// sitting on a shadow nearly twice as long.</para></summary>
        private float SoftnessOf(float blur, ShadowKind kind) => blur * _kindSoftness[(int)kind];

        /// <summary>This caster's own lean. 1 is the sun; less is a shorter, more upright shadow
        /// that no longer points where the sun says, which is a look rather than a correction.
        ///
        /// <para>Length and lean are not interchangeable. The ceiling decides how far a shadow
        /// reaches, the lean decides its shape: at a sun 64 degrees off vertical, a crop capped at
        /// 0.55 puts its tip 9.9 px sideways and 4.8 px down at full lean, and 6.8 by 8.6 at 0.6.
        /// Same ceiling, and only the second one reads as a plant standing on soil.</para></summary>
        private float LeanOf(float rot, ShadowKind kind) => rot * _kindLean[(int)kind];

        /// <summary>
        /// How far a shadow of this KIND may reach, as a fraction of the sprite's own height.
        ///
        /// <para>
        /// The caps are what keep a tree from throwing a shadow across the whole screen and a
        /// fence from looking like a flagpole, and they were absolute: the sun's own stretch runs
        /// past 0.4 for most of the day, so a small object sat pinned at its cap from mid-morning
        /// to mid-afternoon and the length slider did nothing at all for anything but people. The
        /// report was that benches, lightning rods and fences have no shadow worth the name "despite
        /// player shadow sits at value 1.0", which is exactly what a dead slider looks like.
        /// </para>
        ///
        /// <para>
        /// Scaling the cap by the setting keeps every proportion the caps were chosen for and hands
        /// the length back to the player: at the default 1.18 a fence reaches a little further than
        /// it used to, and at the top of the slider everything on screen casts a long evening
        /// shadow together.
        /// </para>
        /// </summary>
        private float LengthCap(float stretch, float cap) => Math.Min(stretch, cap * _sunLengthScale);

        /// <summary>Sprites refused a slot because nothing is big enough, logged once each. A
        /// refusal means that shadow draws as bands instead of a silhouette for as long as it is
        /// on screen, so a steady miss count is a picture problem before it is a speed one, and
        /// the report showing "7 misses a frame" with no way to ask WHICH seven is a diagnostic
        /// that only tells you to start guessing.</summary>
        private readonly System.Collections.Generic.HashSet<Rectangle> _oversizeLogged = new();

        private readonly System.Collections.Generic.HashSet<string> _columnRefusalLogged = new();

        private void NoteColumnRefusal(string why)
        {
            if (DiagnosticMonitor == null || !_columnRefusalLogged.Add(why))
                return;
            DiagnosticMonitor.Log($"[shadow] tile column not baked: {why} - it draws banded.", LogLevel.Debug);
        }

        private void NoteOversize(Rectangle src, Vector2 baseOrigin, ShadowProjection projection)
        {
            if (DiagnosticMonitor == null || !_oversizeLogged.Add(src))
                return;
            float coarsest = BakeScales[^1];
            projection.Bounds(src.Width * coarsest, src.Height * coarsest, baseOrigin.X * coarsest, baseOrigin.Y * coarsest,
                out float left, out float right, out float top, out float bottom);
            float needW = right - left;
            float needH = bottom - top;
            DiagnosticMonitor.Log($"[shadow] sprite {src.Width}x{src.Height} needs {needW:0}x{needH:0} even baked at "
                + $"{coarsest:0}x - larger than the biggest slot ({ObjectSlotClasses[^1].W}x{ObjectSlotClasses[^1].H}), "
                + "so it draws banded.", LogLevel.Debug);
        }

        /// <summary>
        /// Screen pixels of silhouette per slot texel, best first. Four is one texel per screen
        /// pixel, which is what every sprite that fits a slot has always been given.
        ///
        /// <para>The coarser two exist for the sprites that fit nothing. Forest had twenty of
        /// them, and a sprite the largest slot cannot take at 4× was not a sprite without a
        /// shadow - it was a sprite drawing the BANDED fallback, permanently: seven flat steps of
        /// opacity where every one of its neighbours has a smooth gradient, for the rest of the
        /// session, since the fit test is the same every frame. Halving the bake resolution
        /// quarters the slot a silhouette needs, and a shadow is a blurred black shape, so its
        /// edge at half resolution is a thing nobody can point at. The banding was.</para>
        ///
        /// <para>Only reached after the largest slot has refused, so nothing that fits today
        /// changes: 4× is tried first and produces exactly the pixels it always did.</para>
        /// </summary>
        private static readonly float[] BakeScales = { 4f, 2f, 1f };

        /// <summary>
        /// Pick the bake scale and slot for a silhouette: the finest scale that fits anything,
        /// and at that scale the smallest class that takes it. Returns false only when even the
        /// coarsest scale overflows the largest slot, which is the caller's cue to fall back to
        /// the banded draw.
        ///
        /// <para>A refresh (<paramref name="into"/> set) must keep the slot the entry already
        /// owns, so the class is fixed and only the scale is free - which is what lets a lean
        /// that has grown past the slot drop a scale instead of losing its bake.</para>
        ///
        /// <para>The blur is in SCREEN pixels but stamped in slot texels, so it scales with
        /// everything else; at 4× that multiplier is exactly one and the arithmetic is the
        /// arithmetic this has always done.</para>
        /// </summary>
        private bool ChooseBakeFit(float sourceW, float sourceH, float shear, float blurPx, RenderTarget2D? into,
            out int slotClass, out float scale, out float blurTexels)
        {
            if (sourceW > 0f && sourceH > 0f)
            {
                foreach (float s in BakeScales)
                {
                    float texelBlur = blurPx * (s / 4f);
                    float spriteWidth = sourceW * s, spriteHeight = sourceH * s;
                    float needW = spriteWidth + Math.Abs(shear) * spriteHeight + 2f * texelBlur;
                    float needH = spriteHeight + texelBlur;
                    int cls = into != null ? ClassOfSlot(into) : ObjectSlotClassFor(needW, needH);
                    if (cls < 0)
                        continue;
                    if (into != null && (needW > into.Width || needH > into.Height - 8f))
                        continue;
                    slotClass = cls;
                    scale = s;
                    blurTexels = texelBlur;
                    return true;
                }
            }
            slotClass = -1;
            scale = 0f;
            blurTexels = 0f;
            return false;
        }

        /// <summary>The same question for a sprite laid down by a projection. The laid-down
        /// bounds come back with the answer, because the bake places the feet from them and the
        /// content rect is read off them, and they are not worth computing twice.</summary>
        private bool ChooseBakeFit(Rectangle src, Vector2 baseOrigin, ShadowProjection projection, float blurPx, RenderTarget2D? into,
            out int slotClass, out float scale, out float blurTexels,
            out float left, out float right, out float top, out float bottom)
        {
            left = right = top = bottom = 0f;
            if (!src.IsEmpty)
            {
                foreach (float s in BakeScales)
                {
                    float texelBlur = blurPx * (s / 4f);
                    projection.Bounds(src.Width * s, src.Height * s, baseOrigin.X * s, baseOrigin.Y * s,
                        out left, out right, out top, out bottom);
                    float needW = right - left + 2f * texelBlur;
                    float needH = bottom - top + 2f * texelBlur + 1f;
                    int cls = into != null ? ClassOfSlot(into) : ObjectSlotClassFor(needW, needH);
                    if (cls < 0)
                        continue;
                    if (into != null && (needW > into.Width || needH > into.Height - 8f))
                        continue;
                    slotClass = cls;
                    scale = s;
                    blurTexels = texelBlur;
                    return true;
                }
            }
            slotClass = -1;
            scale = 0f;
            blurTexels = 0f;
            return false;
        }

        /// <summary>Would a bake of this sprite, laid down like this, fit any slot at any scale? The
        /// same arithmetic <see cref="BakeObjectSprite"/> does before it commits to anything, asked
        /// by the draw path so a hopeless request is never made rather than being made and refused
        /// every frame.</summary>
        private bool ObjectBakeCouldFit(Rectangle src, Vector2 baseOrigin, ShadowProjection projection, float blurPx)
            => ChooseBakeFit(src, baseOrigin, projection, blurPx, null, out _, out _, out _, out _, out _, out _, out _);

        /// <summary>The smallest slot class that fits a silhouette of this size, or -1 if even the
        /// largest cannot take it.</summary>
        private static int ObjectSlotClassFor(float neededW, float neededH)
        {
            for (int i = 0; i < ObjectSlotClasses.Length; i++)
                if (neededW <= ObjectSlotClasses[i].W && neededH <= ObjectSlotClasses[i].H - 8f)
                    return i;
            return -1;
        }

        /// <summary>Lease a slot of the given class, reusing a returned one when there is one.</summary>
        private RenderTarget2D RentObjectRT(GraphicsDevice graphicsDevice, int slotClass)
        {
            var free = _objectFreeTargetsByClass[slotClass];
            if (free.Count > 0)
            {
                RenderTarget2D reused = free[^1];
                free.RemoveAt(free.Count - 1);
                return reused;
            }
            (int w, int h, _) = ObjectSlotClasses[slotClass];
            // PreserveContents: these slots are CACHED across frames now (see PreparePlayer) —
            // the default DiscardContents decays into garbage after later target swaps.
            var renderTarget = VramTally.Track(new RenderTarget2D(graphicsDevice, w, h, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents),
                $"object bake slots {w}x{h}");
            _objectRenderTargetPools[slotClass].Add(renderTarget);
            return renderTarget;
        }

        /// <summary>Total slots held across every class, for the over-cap diagnostic.</summary>
        private int ObjectSlotsAllocated()
        {
            int n = 0;
            foreach (var pool in _objectRenderTargetPools) n += pool.Count;
            return n;
        }

        private void DrawObjectShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha, float blur)
        {
            var viewport = Game1.viewport;
            int tileX0 = viewport.X / 64 - 3, tileX1 = (viewport.X + viewport.Width) / 64 + 3;
            int tileY0 = viewport.Y / 64 - 3, tileY1 = (viewport.Y + viewport.Height) / 64 + 8; // extra bottom margin for tall trees

            CastTerrainFeatureShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);
            CastLargeTerrainShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);

            // What an EVENT stops drawing. Trees, bushes, crops and large terrain features are drawn
            // through the whole cutscene, but ground objects and furniture are not, so casting for
            // them left shadows lying on bare ground with nothing above them (reported at a beach
            // cutscene). Each test below is GameLocation.draw's own, and they are all different from
            // each other, which is why they are three flags and not one:
            //   objects    (!eventUp || currentEvent.showGroundObjects)
            //   furniture  (!eventUp || Farm || FarmHouse)
            //   clumps     only the Woods is gated, and by showGroundObjects
            bool eventUp = Game1.eventUp;
            bool showGround = location.currentEvent != null && location.currentEvent.showGroundObjects;
            bool objectsDrawn = !eventUp || showGround;
            bool furnitureDrawn = !eventUp || location is Farm || location is StardewValley.Locations.FarmHouse;
            bool clumpsDrawn = !(location is StardewValley.Locations.Woods && eventUp && !showGround);

            if (clumpsDrawn)
                CastResourceClumpShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);
            if (objectsDrawn)
                CastPlacedObjectShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);
            if (furnitureDrawn)
                CastFurnitureShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);
            CastCritterShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);

            // Map-drawn props (street lamps, signs, poles…) aren't entities at all — they're tile
            // columns painted on the map. Cast their shadow from the actual tile art.
            DrawTilePropShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);

            // Building shadows via the sprite-lean path stay DISABLED (leaning a whole-building
            // sprite projects it up over itself). Their real ground projection is done separately
            // in DrawHeightShadows using Height Framework data — see DrawSunShadows.
        }

        /// <summary>Trees, fruit trees, bushes, crops and grass: the tile-keyed terrain features, looked up
        /// over the on-screen range rather than walked in full.</summary>
        private void CastTerrainFeatureShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha,
            float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            // Scan the ON-SCREEN tile range and look each tile up, instead of enumerating EVERY
            // terrain feature in the location and culling per item: a mature farm has thousands of
            // crops, so the old full walk was O(all crops) ×2 passes ×60 fps. terrainFeatures is a
            // tile-keyed dictionary, so a viewport-bounded lookup is O(visible tiles) and flat as
            // the farm fills in.
            var tfDict = location.terrainFeatures;
            for (int ftY = tileY0; ftY <= tileY1; ftY++)
            for (int ftX = tileX0; ftX <= tileX1; ftX++)
            {
                Vector2 tile = new(ftX, ftY);
                if (!tfDict.TryGetValue(tile, out var tf))
                    continue;
                switch (tf)
                {
                    // Tall sprites swing away from their base under the full character lean
                    // (the canopy shadow detaches from the trunk) — damp the lean for them.
                    // Trees are tall → damp the lean so the canopy shadow stays rooted at the
                    // trunk (its vanilla contact blob is kept to fill the base). Bushes are
                    // short → full lean, matching the character direction, blob suppressed.
                    // The game's own gate: a tree being chopped is a stump AND falling, and it is
                    // drawn whole for the length of that fall, so a canopy shadow that stops at
                    // the axe stroke leaves the toppling tree casting a sapling's stub.
                    case Tree tree when tree.growthStage.Value >= 5 && (!tree.stump.Value || tree.falling.Value) && tree.texture?.Value != null && KindCasts(ShadowKind.Trees):
                        DrawTreeShadow(spriteBatch, tree, tile, LeanOf(rot, ShadowKind.Trees), LengthOf(stretch, ShadowKind.Trees), alpha,
                            SoftnessOf(blur, ShadowKind.Trees));
                        break;
                    // Everything else the game still DRAWS as a tree: seeds, sprouts, saplings,
                    // bush-stage growth and stumps. They are short, so they take the full lean a
                    // bush does rather than the damped canopy lean above.
                    case Tree small when small.texture?.Value != null && KindCasts(ShadowKind.SmallTrees):
                        DrawSmallTreeShadow(spriteBatch, small, tile, LeanOf(rot, ShadowKind.SmallTrees), LengthOf(stretch, ShadowKind.SmallTrees), alpha,
                            SoftnessOf(blur, ShadowKind.SmallTrees));
                        break;
                    case FruitTree ft when ft.growthStage.Value >= 4 && (!ft.stump.Value || ft.falling.Value) && ft.texture != null && KindCasts(ShadowKind.Trees):
                        DrawFruitTreeShadow(spriteBatch, ft, tile, LeanOf(rot, ShadowKind.Trees), LengthOf(stretch, ShadowKind.Trees), alpha,
                            SoftnessOf(blur, ShadowKind.Trees));
                        break;
                    // A fruit tree still growing (stages 0 to 3) is short, so it takes the full
                    // lean a bush and a wild sapling take.
                    case FruitTree sapling when sapling.growthStage.Value < 4 && sapling.texture != null && KindCasts(ShadowKind.SmallTrees):
                        DrawFruitTreeSaplingShadow(spriteBatch, sapling, tile, LeanOf(rot, ShadowKind.SmallTrees), LengthOf(stretch, ShadowKind.SmallTrees), alpha,
                            SoftnessOf(blur, ShadowKind.SmallTrees));
                        break;
                    case Bush bush when KindCasts(ShadowKind.Bushes):
                        DrawBushShadow(spriteBatch, bush, LeanOf(rot, ShadowKind.Bushes), LengthOf(stretch, ShadowKind.Bushes), alpha,
                            SoftnessOf(blur, ShadowKind.Bushes));
                        break;
                    // DEAD crops cast too. They were excluded, and a withered plant is still a
                    // plant standing on the soil: the game keeps drawing it, from art it keeps
                    // current, until someone scythes it. A field the player let die read as
                    // painted onto the ground while the scarecrow two tiles away stood on it,
                    // which is how it was reported - "the little plants have no shadow", with a
                    // picture of a dead crop row. Nothing else here needed changing: the crop
                    // keeps its texture, its source rect, its draw position and its flip through
                    // dying, so the same call handles it.
                    case HoeDirt { crop: { } crop } hd when !crop.forageCrop.Value && !crop.IsErrorCrop() && KindCasts(ShadowKind.Crops):
                        DrawCropShadow(spriteBatch, crop, tile, LeanOf(rot, ShadowKind.Crops), LengthOf(stretch, ShadowKind.Crops), alpha,
                            SoftnessOf(blur, ShadowKind.Crops));
                        break;
                    // Grass. It stands on the ground like everything else here and was the only
                    // thing on a meadow not casting, which reads as the grass being printed on the
                    // dirt while the fence beside it stands on it.
                    case StardewValley.TerrainFeatures.Grass grass when grass.texture?.Value != null && KindCasts(ShadowKind.Grass):
                        DrawGrassShadow(spriteBatch, grass, tile, LeanOf(rot, ShadowKind.Grass), LengthOf(stretch, ShadowKind.Grass), alpha,
                            SoftnessOf(blur, ShadowKind.Grass));
                        break;
                }
            }
        }

        /// <summary>The decorative bushes a map places, which live in their own list rather than the
        /// tile-keyed one.</summary>
        private void CastLargeTerrainShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha,
            float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            foreach (var ltf in location.largeTerrainFeatures)
            {
                Vector2 ltile = ltf?.Tile ?? Vector2.Zero;
                if (ltf == null || ltile.X < tileX0 || ltile.X > tileX1 || ltile.Y < tileY0 || ltile.Y > tileY1)
                    continue;
                if (ltf is Bush bush && KindCasts(ShadowKind.Bushes))
                    DrawBushShadow(spriteBatch, bush, LeanOf(rot, ShadowKind.Bushes), LengthOf(stretch, ShadowKind.Bushes), alpha,
                        SoftnessOf(blur, ShadowKind.Bushes));
            }
        }

        /// <summary>Boulders, stumps and logs.</summary>
        private void CastResourceClumpShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha,
            float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            foreach (ResourceClump clump in location.resourceClumps)
            {
                if (clump == null)
                    continue;
                Vector2 tile = clump.Tile;
                if (tile.X < tileX0 || tile.X > tileX1 || tile.Y < tileY0 || tile.Y > tileY1)
                    continue;
                DrawResourceClumpShadow(spriteBatch, clump, rot, stretch, alpha, blur);
            }
        }

        /// <summary>Placed objects: machines, fences, torches, decor and forage, each through the caster
        /// that suits how the game draws it.</summary>
        private void CastPlacedObjectShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha,
            float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            // Every caster in here is the Objects kind, so its dial being at zero skips the walk
            // rather than each draw inside it.
            if (!KindCasts(ShadowKind.Objects))
                return;
            // Same viewport-bounded lookup for placed objects (machines, fences, decor): objects is
            // tile-keyed too, so we never walk the whole placed-object set to find the on-screen few.
            var objDict = location.objects;
            for (int obY = tileY0; obY <= tileY1; obY++)
            for (int obX = tileX0; obX <= tileX1; obX++)
            {
                Vector2 tile = new(obX, obY);
                if (!objDict.TryGetValue(tile, out SObject o) || o == null || o.isTemporarilyInvisible)
                    continue;
                // showGroundObjects is only the first gate. Object.draw has a SECOND one: during an
                // event, a small object standing where a character walks is not drawn at all
                // (`!Game1.CurrentEvent.isTileWalkedOn(x, y)`), so the scene does not have items
                // poking through it. A clam two tiles along the row Sam walks at Squid Fest is
                // hidden by that rule, and its shadow was the mark left lying on empty snow.
                // Craftables are exempt in the game's code, so they are exempt here too.
                if (!o.bigCraftable.Value && Game1.eventUp
                    && (Game1.CurrentEvent?.isTileWalkedOn(obX, obY) ?? false))
                    continue;
                // A CRAB POT floats. The generic caster below draws the item's INVENTORY sprite
                // anchored to the tile's ground line, and a pot is drawn a tile higher than that,
                // from different art, bobbing on the swell — so its shadow came out the wrong
                // shape in the wrong place, sitting on open water beside the pot. Nothing
                // floating should throw a hard leaning silhouette onto the surface anyway.
                if (o is StardewValley.Objects.CrabPot)
                    continue;
                if (o.bigCraftable.Value)
                {
                    if (o.Fragility == 2)
                        continue;
                    // A keg or a machine stands on the ground at its own height, so it takes the
                    // same sun a person does unless ShadowLengthObjects is turned down.
                    DrawBigCraftableShadow(spriteBatch, o, tile, LeanOf(rot, ShadowKind.Objects), LengthOf(stretch, ShadowKind.Objects), alpha,
                        SoftnessOf(blur, ShadowKind.Objects));
                }
                else if (o.IsSpawnedObject)
                {
                    // Small forage lying on the ground (beach shells, mushrooms, coral…). Short,
                    // strongly-damped shadow.
                    DrawSmallObjectShadow(spriteBatch, o, tile, LeanOf(rot, ShadowKind.Objects), LengthOf(stretch, ShadowKind.Objects), alpha,
                        SoftnessOf(blur, ShadowKind.Objects));
                }
                else if (!o.isPassable() && o.QualifiedItemId != "(O)590" && o.QualifiedItemId != "(O)SeedSpot")
                {
                    // Everything else that stands on its tile (fences, signs, torches, kegs-as-object,
                    // decor…) gets a real leaning silhouette too — drawn generically from the item's
                    // own sprite via ItemRegistry, so no per-type method is needed. Skip flat passable
                    // items and the ground-mark spots (artifact / seed) that shouldn't cast.
                    DrawGenericObjectShadow(spriteBatch, o, tile, LeanOf(rot, ShadowKind.Objects), LengthOf(stretch, ShadowKind.Objects), alpha,
                        SoftnessOf(blur, ShadowKind.Objects));
                }
            }
        }

        /// <summary>Furniture, minus the kinds that hang on a wall or lie flat on the floor.</summary>
        private void CastFurnitureShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha,
            float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            foreach (Furniture f in location.furniture)
            {
                if (f == null || f.isTemporarilyInvisible)
                    continue;
                int type = f.furniture_type.Value;
                // Skip rugs (12) and wall-mounted furniture (6 window, 13 wall, 17 painting).
                if (type == 12 || type == 6 || type == 13 || type == 17)
                    continue;
                Vector2 tile = f.TileLocation;
                if (tile.X < tileX0 || tile.X > tileX1 || tile.Y < tileY0 || tile.Y > tileY1)
                    continue;
                DrawFurnitureShadow(spriteBatch, f, type, rot, stretch, alpha, blur);
            }
        }

        /// <summary>Critters, fading with flight height exactly as their vanilla blob does.</summary>
        private void CastCritterShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha,
            float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            // Critters (birds, squirrels, butterflies, bunnies…) — replace their vanilla blob with
            // the same leaning silhouette as everything else, faded out with flight height exactly
            // like the vanilla blob so airborne critters keep a faint grounded shadow.
            var critters = location.critters;
            if (critters != null)
            {
                foreach (var c in critters)
                {
                    if (c == null || c is StardewValley.BellsAndWhistles.Cloud || c.sprite?.Texture == null)
                        continue;
                    float fly = Math.Min(1f, Math.Abs((c.yJumpOffset + c.yOffset) / 64f));
                    float ca = alpha * (1f - fly);
                    if (!_isBakingObjects && ca <= 0.02f)
                        continue;
                    Vector2 wpos = c.position;
                    // Squirrel.draw sits a full tile LOWER than the base Critter convention
                    // (sprite offset −64 vs −128; its vanilla blob is at position+60) — match
                    // it or the shadow floats a tile above the squirrel.
                    if (c is StardewValley.BellsAndWhistles.Squirrel)
                        wpos.Y += 60f;
                    int ctx = (int)(wpos.X / 64f), cty = (int)(wpos.Y / 64f);
                    if (ctx < tileX0 || ctx > tileX1 || cty < tileY0 || cty > tileY1 || OnOpenWater(location, new Point(ctx, cty)))
                        continue;   // seagulls on the surf line keep their shadow; open water doesn't
                    Rectangle src = c.sprite.SourceRect;
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport, wpos + new Vector2(0f, -2f));
                    float depth = MathHelper.Clamp((wpos.Y - 1f) / 10000f, 0f, 1f);
                    EmitObject(spriteBatch, c.sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                        ca, rot, LengthCap(stretch, 0.45f), depth, blur, ObjectHeadFade,
                        c.flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
                }
            }
        }


        /// <summary>
        /// Generic silhouette for ANY tile-placed object, drawn from the item's own sprite
        /// (ItemRegistry) — the type-agnostic caster that means we don't hand-write a method per
        /// object kind. Anchored bottom-centre at the tile's ground line; height comes from the
        /// sprite itself, so a 16- or 32-tall item both sit right.
        /// </summary>
        private void DrawGenericObjectShadow(SpriteBatch spriteBatch, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            if (!TryItemArt(o.QualifiedItemId, out Texture2D texture, out Rectangle src) || src.IsEmpty)
                return;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 6f));
            float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            EmitObject(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, SpriteEffects.None, GeometryOf(o));
        }

        /// <summary>What a placed object is for its shadow: a fence, a gate or a sign is a flat
        /// face standing on its edge, and everything else placed on a tile stands on a footprint.
        /// By the game's own class, so a mod's fence is a fence.</summary>
        private static ShadowGeometry GeometryOf(SObject o)
            => o is Fence || o is Sign ? ShadowGeometry.Card : ShadowGeometry.Solid;

        /// <summary>
        /// A building gets the contact pool at its footprint base AND, since 1.7.2, the shape of
        /// its own roof laid on the ground.
        ///
        /// <para>
        /// The shape is a CARD, not a solid. A solid lays its width down across the sun's
        /// direction, which is right for a tree or a person, both of which have a thickness to
        /// lie down; a building is standing walls, and rotating its width onto the ground swings
        /// the near corners BELOW the footprint line, putting a piece of the shadow in front of
        /// the building it belongs to. A card keeps the base edge on the ground line and shears
        /// only what is above it, so nothing can come out in front.
        /// </para>
        /// </summary>
        private void DrawBuildingShadow(SpriteBatch spriteBatch, Building bld, float rot, float stretch,
                                        float alpha, float blur, bool castShape)
        {
            float footprintWidth = bld.tilesWide.Value * 64f;
            float baseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f;   // footprint bottom = ground line
            float footprintCentreX = (bld.tileX.Value + bld.tilesWide.Value / 2f) * 64f;
            Vector2 footprintFeet = Game1.GlobalToLocal(Game1.viewport, new Vector2(footprintCentreX, baseY - 10f));
            float depth = MathHelper.Clamp(baseY / 10000f - ShadowDepthBias, 0f, 1f);
            // The pool stays under every building whether or not it casts a shape. It is what
            // grounds the footprint, and it is the whole answer on an overcast day and at every
            // hour the sun is not casting.
            DrawContactBlob(spriteBatch, footprintFeet, footprintWidth * 0.5f * 0.85f, 24f, alpha, depth, blur);

            Texture2D? texture = null;
            try { texture = bld.texture?.Value; } catch { /* a content pack can throw while loading its art */ }
            if (!castShape || texture == null || bld.isUnderConstruction())
                return;
            Rectangle src = bld.getSourceRect();
            if (src.Width <= 0 || src.Height <= 0)
                return;
            // The building's art is drawn with its bottom on the footprint's ground line and its
            // left edge on the footprint's left, at 4x. A barn's roof overhangs its own footprint,
            // so the silhouette has to hang from the ART's centre and not from the footprint's, or
            // a wide roof's shadow comes out shifted by half the overhang.
            float artCentreX = bld.tileX.Value * 64f + src.Width * 2f;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(artCentreX, baseY));
            // Ground-sorted from the building's TOP row, not its base, and the difference between
            // those two is the whole reason a building's shape could not be cast before.
            //
            // The game gives a building a sort depth BELOW its own footprint base. Hang the shadow
            // at the base and all of it lands in front of the building, laying the roof back
            // across itself, which is what got the shape refused the first time. Ground-sorting
            // from the base fixes only half: the far pieces drop behind the building, and the
            // pieces near the base keep a depth the building still cannot cover, so a band stays
            // across the porch. Measured on the farmhouse, that band survived the first sort.
            //
            // Anchored at the top row instead, every piece is below anything the building can be
            // drawn at, so none of it can land on the building. The pieces this under-states are
            // the ones lying between the base and the top, which is exactly the ground the
            // building itself is standing on and hiding.
            EmitObject(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, LeanOf(rot, ShadowKind.Buildings), LengthOf(stretch, ShadowKind.Buildings), depth,
                SoftnessOf(blur, ShadowKind.Buildings), ObjectHeadFade, SpriteEffects.None, ShadowGeometry.Card,
                groundAnchorWorldY: bld.tileY.Value * 64f);
        }

        /// <summary>
        /// A tuft of grass: one shadow at the tuft's own centre, from the game's own layout
        /// (see <see cref="GrassArt"/>).
        /// <para>
        /// The centre is the mean of the blade anchors rather than the tile centre, because a tuft
        /// is up to four 15x20 sprites at jittered spots and the middle of the TILE can sit under
        /// none of them. One frame stands in for all the blades, so a whole meadow shares a handful
        /// of bakes: the source rect varies only with the weed variant and the season offset.
        /// </para>
        /// <para>
        /// The cap is the shortest of any caster here. Grass is a hand's breadth tall; a shadow the
        /// length of a fence post's would read as a shrub.
        /// </para>
        /// </summary>
        private void DrawGrassShadow(SpriteBatch spriteBatch, StardewValley.TerrainFeatures.Grass grass, Vector2 tile,
            float rot, float stretch, float alpha, float blur)
        {
            // ONE shadow per tuft, not one per blade. The per-blade version was the single
            // largest sprite-count driver in the mod: up to four EmitObject calls per grass tile
            // put a meadow at four times the draws, four times the bake-cache keys (the cache
            // sits within a handful of slots of its cap on an ordinary profile), and four
            // times the fill - for four near-identical smudges stacked within a few pixels of
            // each other, drawn at 0.62 alpha each precisely BECAUSE stacking four of them
            // approached opaque. One silhouette at the tuft's own center, at full strength,
            // is the same dark patch for a quarter of everything.
            if (!GrassArt.TryRead(grass, out int blades, out int[] which, out int[] ox, out int[] oy))
                return;
            if (blades <= 0)
                return;
            Texture2D texture = grass.texture.Value;
            float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            // Anchor at the mean of the blade anchors, so the shadow sits where the tuft
            // actually leans rather than at the geometric tile center.
            Vector2 at = Vector2.Zero;
            for (int i = 0; i < blades; i++)
                at += GrassArt.BladeAt(tile, i, ox, oy);
            at /= blades;
            EmitObject(spriteBatch, texture, GrassArt.BladeShadowSource(grass, 0, which),
                Game1.GlobalToLocal(Game1.viewport, at), GrassArt.BladeOrigin,
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        /// <summary>Small forage lying on the ground (16x16) — a short leaning silhouette to ground it.</summary>
        private void DrawSmallObjectShadow(SpriteBatch spriteBatch, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            if (!TryItemArt(o.QualifiedItemId, out Texture2D texture, out Rectangle src))
                return;
            // Forage rests near the tile's bottom edge; small lift so the shadow base meets the item.
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 12f));
            float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            EmitObject(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, SpriteEffects.None, GeometryOf(o));
        }

        /// <summary>How wide and how tall a mass of opaque Buildings art may be and still be a
        /// THING standing on the ground rather than the ground itself. A cactus, a post, a
        /// signboard, a crate are one or two tiles either way; a cliff face or a house wall is
        /// not. This is the test that opacity was standing in for, badly.</summary>
        private const int MaxPropSpan = 2;

        /// <summary>Lift the character/animal feet anchor a touch so the shadow base sits at the
        /// visual feet rather than a few px below (the bounding-box bottom overshoots).</summary>
        private const float FeetLift = 10f;
        /// <summary>
        /// Objects use the same strong feet→tip fade as characters: a DARK base grounds the
        /// shadow (the earlier gentle/uniform fade read as floaty — the fix was a darker base,
        /// not a flatter gradient).
        /// </summary>
        private const float ObjectHeadFade = HeadFade;

        private void DrawBigCraftableShadow(SpriteBatch spriteBatch, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            if (!TryItemArt(o.QualifiedItemId, out Texture2D texture, out Rectangle src))
                return;
            // Where the game puts the foot. Object.draw builds a destination rectangle from
            // (x*64, y*64 - 64) that is 128 tall, so the art's bottom edge lands exactly on
            // (tile.Y + 1) * 64. The base used to be lifted 20px above that, on the theory that a
            // barrel rests a little high inside its cell, and on a tall thin thing - a lightning
            // rod, a post, a signboard - the lift is plainly a strip of lit ground between the
            // object's foot and the start of its own shadow. The silhouette is sheared and
            // squashed about its own base, so the base lands wherever this anchor is put: the
            // strip was the lift, exactly.
            //
            // Anything that really does sit high in its cell has transparent rows at the bottom of
            // its art, and the silhouette is cut from that art, so its own alpha decides where the
            // dark begins. That is what the lift was reaching for, applied to every item at once.
            //
            // Measured rather than argued: 20, 10 and 0 shot from one spot at one clock with the
            // cloud shadow turned OFF and the mod rebuilt between each. The first comparison of
            // this said 0 looked worse and it was wrong - the cloud had drifted across the rods
            // between the two frames, which darkens the ground and weakens every shadow on it.
            //
            // The 1.6 shapes keep the lift and the cell, because that pair is what every release
            // up to 1.6 drew and some players will have made their peace with it.
            float lift = ClassicShadowShapes ? 20f : 0f;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - lift));
            float depth = MathHelper.Clamp(Math.Max(0f, ((tile.Y + 1f) * 64f - 24f) / 10000f) + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            // Pivot on the row the ART ends on, not on the cell's bottom edge. Both stand the
            // object on the ground; only this one puts the shadow's contact point where the
            // object's own base is, and the difference shows on anything that leaves empty rows
            // under itself inside its cell.
            float pivotRow = ClassicShadowShapes ? src.Height : ArtFootRow(texture, src);
            EmitObject(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, pivotRow),
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


        private void DrawCropShadow(SpriteBatch spriteBatch, Crop crop, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            Texture2D texture = crop.DrawnCropTexture;
            if (texture == null || crop.sourceRect.IsEmpty)
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
            SpriteEffects effect = crop.flip.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            EmitObject(spriteBatch, texture, crop.sourceRect, feet, CropOrigin,
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effect);
        }

        private void DrawFurnitureShadow(SpriteBatch spriteBatch, Furniture f, int type, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = f.sourceRect.Value;
            if (src.IsEmpty)
                return;
            // Furniture keeps its own (animated) sourceRect; only the texture resolution is cached.
            if (!TryItemArt(f.QualifiedItemId, out Texture2D texture, out _))
                return;
            // Anchor at the footprint's bottom-centre (drawPosition is protected; the bounding
            // box bottom matches the sprite's ground line for floor furniture).
            Rectangle box = f.boundingBox.Value;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom - 30f));
            // A SEAT is the one kind of furniture a body occupies, so its shadow has to sort a
            // clear step below anything sitting on it. At box.Bottom - 8 the two depths were
            // within a rounding error of each other and the order was a coin flip: the bench's
            // own dark silhouette landed over the sitter's legs, which reads exactly like the
            // body clipping through the bench (reported for the player, and the likeliest cause
            // of the same report about NPCs). One tile of depth is plenty - the shadow still
            // draws over the ground, it just can never win against a body at the same row.
            bool seat = type is 0 or 1 or 2 or 3;   // chair / bench / couch / armchair
            float depth = MathHelper.Clamp((box.Bottom - (seat ? 72f : 8f)) / 10000f - ShadowDepthBias, 0f, 1f);
            EmitObject(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur);
        }

        // ItemRegistry.GetDataOrErrorItem parses the qualified id and walks the item-data registry;
        // doing it per on-screen object ×2 passes ×60fps is wasted work when the resolved sprite is
        // static. Cache (texture, sourceRect) per QualifiedItemId, cleared when the season rolls over
        // (a few items swap art by season).
        private readonly System.Collections.Generic.Dictionary<string, (Texture2D? texture, Rectangle src)> _itemArtCache = new();
        private string _itemArtSeason = "";

        /// <summary>The row a sprite cell's art actually ENDS on, counted from the cell's top, so a
        /// shadow can be pivoted on the object's own base rather than on the cell's bottom edge.
        ///
        /// <para>A cell is a fixed box and the art inside it need not fill it: a lightning rod, a
        /// sign, a scarecrow all leave empty rows under their base. Pivoting on the cell puts the
        /// shadow's contact point in that empty space, which on the ground reads as the shadow
        /// sitting below the thing that casts it. This asks the art where it stops instead, which
        /// is the same answer the trunk seam needed earlier the same day: read the alpha, do not
        /// assume the box.</para>
        ///
        /// <para>One readback per distinct piece of art, cached for the session; a 16x32 cell is
        /// two thousand pixels. Falls back to the cell height, the old behaviour, if the texture
        /// cannot be read.</para>
        /// </summary>
        private float ArtFootRow(Texture2D texture, Rectangle src)
        {
            var key = (texture, src);
            if (_artFootRow.TryGetValue(key, out float known))
                return known;
            float foot = src.Height;
            try
            {
                if (src.Width > 0 && src.Height > 0 && !texture.IsDisposed
                    && src.Right <= texture.Width && src.Bottom <= texture.Height)
                {
                    var pixels = new Color[src.Width * src.Height];
                    texture.GetData(0, src, pixels, 0, pixels.Length);
                    for (int row = src.Height - 1; row >= 0; row--)
                    {
                        bool opaque = false;
                        for (int column = 0; column < src.Width; column++)
                            if (pixels[row * src.Width + column].A > 8) { opaque = true; break; }
                        if (opaque) { foot = row + 1; break; }
                    }
                }
            }
            catch (Exception)
            {
                foot = src.Height;
            }
            _artFootRow[key] = foot;
            return foot;
        }

        private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle), float> _artFootRow = new();

        private bool TryItemArt(string qualifiedId, out Texture2D texture, out Rectangle src)
        {
            string season = Game1.currentSeason ?? "";
            if (season != _itemArtSeason) { _itemArtCache.Clear(); _itemArtSeason = season; }
            if (!_itemArtCache.TryGetValue(qualifiedId, out var e))
            {
                var data = ItemRegistry.GetDataOrErrorItem(qualifiedId);
                e = (data.GetTexture(), data.GetSourceRect());
                _itemArtCache[qualifiedId] = e;
            }
            texture = e.texture!;
            src = e.src;
            return e.texture != null;
        }

        // ContentManager.Load is cached but still does per-call path normalization + a
        // dictionary lookup — too much for every clump every frame. Cache per texture name.
        private readonly System.Collections.Generic.Dictionary<string, Texture2D> _textureCache = new();

        private Texture2D? LoadCached(string? name)
        {
            if (name == null)
                return Game1.objectSpriteSheet;
            if (!_textureCache.TryGetValue(name, out Texture2D? texture))
            {
                try { texture = Game1.content.Load<Texture2D>(name); }
                catch { texture = null!; }
                _textureCache[name] = texture!;
            }
            return texture;
        }

        private void DrawResourceClumpShadow(SpriteBatch spriteBatch, ResourceClump clump, float rot, float stretch, float alpha, float blur)
        {
            Texture2D? texture = LoadCached(clump.textureName.Value);
            if (texture == null)
                return;
            Rectangle src = Game1.getSourceRectForStandardTileSheet(texture, clump.parentSheetIndex.Value, 16, 16);
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
            EmitObject(spriteBatch, texture, src, feet, baseOrigin, alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        private void DrawTreeShadow(SpriteBatch spriteBatch, Tree tree, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = TreeCanopySourceRect(tree);             // 48x96, in whichever column this tree is drawn from
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f));
            float depth = MathHelper.Clamp((tree.getBoundingBox().Bottom + 2f) / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            SpriteEffects effects = tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            // THE TRUNK, first, because the canopy is only half the tree.
            //
            // Tree.draw puts the trunk on screen as its own crop - stumpSourceRect, 16x32, bottom
            // on the same line the canopy is anchored to - and the canopy rect's bottom rows are
            // empty precisely because that is where the trunk would have been. Casting the canopy
            // alone therefore threw a shadow that begins a tile and a half up the tree. A gentle
            // lean landed it close enough to the base to read as attached; the moment the lean
            // became the true one it slid clear and the shadow came away from the tree it belongs
            // to, which is what the old angle damping was really hiding.
            //
            // A shadow that starts where the wood meets the ground stays attached at any sun angle,
            // which is the honest fix rather than shortening the lean until the seam is covered.
            Rectangle trunk = Tree.stumpSourceRect;                  // (32,96,16,32)
            EmitObject(spriteBatch, tree.texture.Value, trunk, feet, new Vector2(trunk.Width / 2f, trunk.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
            // Tree canopy draws with origin (24, 96); fade about the trunk base.
            EmitObject(spriteBatch, tree.texture.Value, src, feet, new Vector2(24f, 96f),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
        }

        /// <summary>
        /// A tree the game does NOT draw as a grown canopy: seed, sprout, sapling, bush-stage
        /// growth, or a stump. There is no stage threshold here on purpose — growth stage was never
        /// the question. It only ever stood in for "is this the 48x96 canopy rect", and a desert
        /// palm reports stage 18 while a stage-2 palm is still a real object standing on real sand.
        /// Anything the game draws gets a shadow; the stage only picks WHICH art to cast from.
        /// </summary>
        private void DrawSmallTreeShadow(SpriteBatch spriteBatch, Tree tree, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            // Tree.draw's own rects for the pre-canopy stages, on the shared tree sheet.
            Rectangle src = tree.stump.Value
                ? new Rectangle(32, 96, 16, 32)
                : tree.growthStage.Value switch
                {
                    0 => new Rectangle(32, 128, 16, 16),   // seed
                    1 => new Rectangle(0, 128, 16, 16),    // sprout
                    2 => new Rectangle(16, 128, 16, 16),   // sapling
                    _ => new Rectangle(0, 96, 16, 32),     // bush stage (3-4)
                };
            // Anchor the sprite's BOTTOM at the tile's bottom edge rather than reproducing
            // vanilla's pin/origin pair per stage: a shadow belongs where the art meets the
            // ground, and deriving that from the tile is what keeps every stage consistent
            // (the same reasoning as the bush anchor above).
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1) * 64f));
            float depth = MathHelper.Clamp((tree.getBoundingBox().Bottom + 2f) / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            SpriteEffects effect = tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            EmitObject(spriteBatch, tree.texture.Value, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effect);
        }

        /// <summary>
        /// A fruit tree the game still draws as a sapling, stages 0 to 3. It cast nothing here, and
        /// the game paints no blob under it either, so a young orchard stood on a lit lawn with no
        /// shadow at all beside a wild sapling that had one. FruitTree.draw's own rects, 48x80 per
        /// stage, hung from the point FruitTree.draw hangs them from: bottom-centre at
        /// (tile*64 + 32, tile*64 + 48), shifted by the same sine of the tile column the game
        /// shifts the sapling by, so the shadow stands where the sapling does.
        /// </summary>
        private void DrawFruitTreeSaplingShadow(SpriteBatch spriteBatch, FruitTree sapling, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            int row = sapling.GetSpriteRowNumber();
            int column = sapling.growthStage.Value switch { 0 => 0, 1 => 48, 2 => 96, _ => 144 };
            var src = new Rectangle(column, row * 5 * 16, 48, 80);
            float sway = (float)Math.Max(-8.0, Math.Min(64.0, Math.Sin((double)(tile.X * 200f) / (Math.PI * 2.0)) * -16.0)) / 2f;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f + sway, tile.Y * 64f + 48f + sway));
            float depth = MathHelper.Clamp(sapling.getBoundingBox().Bottom / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            SpriteEffects effects = sapling.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            EmitObject(spriteBatch, sapling.texture, src, feet, new Vector2(24f, 80f),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
        }

        private void DrawFruitTreeShadow(SpriteBatch spriteBatch, FruitTree ft, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            // Mature fruit-tree canopy (FruitTree.draw): 48x64 foliage rect, drawn at
            // (tile*64 + 32, tile*64 + 64) with origin (24, 80).
            int season = Game1.GetSeasonIndexForLocation(ft.Location);
            int row = ft.GetSpriteRowNumber();
            var src = new Rectangle((12 + season * 3) * 16, row * 5 * 16, 48, 64);
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f));
            float depth = MathHelper.Clamp(ft.getBoundingBox().Bottom / 10000f - (float)tile.X / 1000000f - ShadowDepthBias, 0f, 1f);
            SpriteEffects effects = ft.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            // Its trunk is a separate crop too (FruitTree.draw: 48x32 at x=384, origin (24,32)),
            // and it was missing for the same reason and with the same result.
            EmitObject(spriteBatch, ft.texture, new Rectangle(384, row * 5 * 16 + 48, 48, 32), feet,
                new Vector2(24f, 32f), alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
            EmitObject(spriteBatch, ft.texture, src, feet, new Vector2(24f, 80f),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
        }

        private void DrawBushShadow(SpriteBatch spriteBatch, Bush bush, float rot, float stretch, float alpha, float blur)
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
            SpriteEffects effect = bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            EmitObject(spriteBatch, Bush.texture.Value, src, feet, baseOrigin,
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effect);
        }

        private void DrawPlayerShadow(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch, float alpha, float blur)
        {
            // Seated: _playerReady is deliberately false (the silhouette's anchor cannot
            // describe a sitter), so without a pool here sitting down silently REMOVES the
            // player's shadow — the sun path has no ambient blob to fall back on. Same
            // grounding pool the seated NPCs get, at the position the game actually drew us.
            // Offset away from their box (the bus, an event pose): the silhouette's anchor cannot
            // describe that, so the player takes the same grounding pool a seated NPC gets. A
            // farmer on a chair is NOT offset (see IsSeated) and keeps the silhouette below.
            Farmer sp = Game1.player;
            if (sp != null && sp.currentLocation == location && IsSeated(sp))
            {
                if (!sp.swimming.Value && !sp.isRidingHorse() && !OnOpenWater(location, sp.TilePoint))
                    DrawContactBlob(spriteBatch, SeatedAnchor(sp), 20f, 10f, alpha * 0.8f, SeatedDepth(sp), blur);
                return;
            }
            if (!_playerReady || _playerRenderTarget == null)
                return;

            Farmer who = Game1.player;
            if (OnOpenWater(location, who.TilePoint))   // open water only — surf/shore keeps the shadow
                return;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
            // The baked silhouette is one cohesive image — flatten it vertically and lean it
            // about the feet as a single unit (no per-layer fragmenting), softened at the edges,
            // and sorted in strips against the floor it lies on, exactly as an NPC's is. Parity is
            // the rule here: one shadow going behind a fence while the other crossed it would be
            // the player and the villagers standing in different worlds.
            DrawSoftGrounded(spriteBatch, Taps9, _playerRenderTarget, null, feet, Color.White, alpha, rot,
                _playerFeetInRenderTarget, new Vector2(CharacterAcrossScale(rot, stretch), stretch),
                who.StandingPixel.Y, SpriteEffects.None, blur);
        }
    }
}