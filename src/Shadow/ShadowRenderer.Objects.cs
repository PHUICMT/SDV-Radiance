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
        private void EmitObj(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, float stretch, float depth, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None)
        {
            var key = (texture, src, effects);
            // The lean is baked as a SHEAR about the feet row (not a rotation): a wide sprite
            // rotated about its feet dips one bottom corner below the ground line, so bushes,
            // benches and lamp heads "drooped down-left". Shearing keeps the whole bottom edge
            // on the ground. Tip position matches the old rotated look exactly:
            //   shear = −sin(rot)·stretch (sideways per px of height), sy = cos(rot)·stretch.
            float shear = -(float)Math.Sin(rot) * stretch;
            float shearScaleY = Math.Max(0.15f, stretch * (float)Math.Cos(rot));
            if (_isBakingObjects)
            {
                if (_objectGraphicsDevice != null && !_bakedObjectCache.ContainsKey(key)
                    && BakeObjSprite(_objectGraphicsDevice, texture, src, baseOrigin, effects, shear, out RenderTarget2D rt, out Vector2 feetInRT))
                    _bakedObjectCache[key] = new SpriteBake { Rt = rt, FeetInRt = feetInRT, BakedShear = shear, BakedBlur = _bakeBlurPx, Content = _lastBakeContent, SlotClass = _lastBakeClass, LastUsedTick = Game1.ticks };
                return;
            }
            if (_bakedObjectCache.TryGetValue(key, out SpriteBake? bakedEntry))
            {
                bakedEntry.LastUsedTick = Game1.ticks;
                // The lean is in the PIXELS, so the sun walking across the sky makes every bake
                // gradually wrong. That used to be answered by throwing the whole cache away
                // whenever a rounded sun angle changed, which on a continuous clock happens about
                // twice a second: a hundred-sprite screen re-baked a hundred times a second, in
                // bursts, all day. Now each sprite is judged on its own error. The shear is a
                // sideways slide per pixel of height, so the tip moves by shear × the sprite's
                // on-screen height: a tall tree earns a re-bake every second or so and a small
                // crop goes minutes without one, which is both correct and an order of magnitude
                // less work than the old sweep.
                if ((Math.Abs(shear - bakedEntry.BakedShear) * src.Height * 4f > ShearRefreshPixels
                        || Math.Abs(_bakeBlurPx - bakedEntry.BakedBlur) > 0.3f)
                    && _objectBakeQueue.Count < ObjectBakeQueueCap)
                    _objectBakeQueue[key] = new ObjectBakeRequest { BaseOrigin = baseOrigin, Shear = shear };
                FrameCost.Count(FrameCost.Counter.ShadowSprites);
                // ONE draw of ONLY the content: the soft edge is in the baked pixels (see
                // SpriteBake.BakedBlur) and the source rect stops the card blending the slot's
                // acres of transparent padding (see ContentBounds). Origin re-anchors because a
                // source rect makes the draw's coordinates content-relative.
                Rectangle content = bakedEntry.Content.IsEmpty ? new Rectangle(0, 0, bakedEntry.Rt.Width, bakedEntry.Rt.Height) : bakedEntry.Content;
                DrawSoft(spriteBatch, Taps9, bakedEntry.Rt, content, feet,
                    Color.White, alpha, 0f, bakedEntry.FeetInRt - new Vector2(content.X, content.Y),
                    new Vector2(1f, shearScaleY), depth, SpriteEffects.None, 0f);
            }
            else
            {
                // Ask the NEXT bake pass for exactly this sprite, so the banded stand-in below
                // lasts one frame (a sprite scrolling in, a machine changing frame) instead of
                // waiting for something else to trigger a full enumeration.
                if (_objectBakeQueue.Count < ObjectBakeQueueCap)
                    _objectBakeQueue[key] = new ObjectBakeRequest { BaseOrigin = baseOrigin, Shear = shear };
                // Counted here rather than at the queue insert: the queue is a dictionary keyed by
                // sprite, so two misses of the SAME sprite in one frame collapse into one entry and
                // the count would under-report exactly the case it exists to catch. A miss is a
                // drawing that came out as bands instead of a silhouette, and every one of them is
                // worth knowing about.
                FrameCost.Count(FrameCost.Counter.BakeMisses);
                FrameCost.Count(FrameCost.Counter.ShadowSprites);
                DrawBandedGradient(spriteBatch, texture, src, feet, baseOrigin, alpha, rot,
                    new Vector2(4f, 4f * stretch), depth, blur, headFade, effects);
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

        /// <summary>Bake a sprite (black + feet→head gradient) to a pooled object RT, its baseOrigin
        /// pinned at the RT's feet point and the sun lean pre-baked as a shear about that row
        /// (x' = x + shear·(y − feetY): bottom edge stays put, higher rows slide sideways).
        /// Returns false (→ banded fallback) if it won't fit a slot.</summary>
        private bool BakeObjSprite(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle src, Vector2 baseOrigin,
            SpriteEffects effects, float shear, out RenderTarget2D rt, out Vector2 feetInRT, RenderTarget2D? into = null)
        {
            rt = null!;
            feetInRT = default;
            if (texture == null || src.IsEmpty)
                return false;
            float spriteWidth = src.Width * 4f, spriteHeight = src.Height * 4f;
            // The bake-time blur stamps the silhouette shifted by up to the blur radius in every
            // direction, so the fit test keeps that much slack or the soft edge clips at the slot.
            float needW = spriteWidth + Math.Abs(shear) * spriteHeight + 2f * _bakeBlurPx;
            float needH = spriteHeight + _bakeBlurPx;
            // A refresh re-renders the slot the entry already owns and must keep its size; a first
            // bake takes the smallest class the silhouette fits, which is what stops a crop from
            // being handed a tree's slot.
            int slotClass = into != null ? ClassOfSlot(into) : ObjSlotClassFor(needW, needH);
            if (slotClass < 0)
            {
                NoteOversize(src, needW, needH);
                return false;
            }
            if (into != null && (needW > into.Width || needH > into.Height - 8f))
                return false;   // the lean grew past the slot it already owns
            _lastBakeClass = slotClass;
            rt = into ?? RentObjRT(graphicsDevice, slotClass);
            feetInRT = new Vector2(rt.Width / 2f, rt.Height - 8f);
            Vector2 pos = feetInRT - baseOrigin * 4f;      // so baseOrigin maps to the feet point
            Matrix lean = ShearAbout(feetInRT, shear);
            try
            {
                graphicsDevice.SetRenderTarget(rt);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                _renderTargetSpriteBatch.Draw(texture, pos, src, Color.Black, 0f, Vector2.Zero, 4f, effects, 0f);
                _renderTargetSpriteBatch.End();
                // Continuous feet(full)→head(faint) gradient over the sprite's vertical extent.
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_gradientTexture!, new Rectangle(0, (int)pos.Y, rt.Width, (int)spriteHeight), Color.White);
                _renderTargetSpriteBatch.End();
                BlurSlotInPlace(graphicsDevice, rt);
                _lastBakeContent = ContentBounds(pos, spriteWidth, spriteHeight, feetInRT, shear, _bakeBlurPx, rt.Width, rt.Height);
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
                    _bakedObjectCache[key] = new SpriteBake { Rt = rt, FeetInRt = feetInRT, BakedShear = req.Shear, BakedBlur = _bakeBlurPx, Content = _lastBakeContent, SlotClass = _lastBakeClass, LastUsedTick = Game1.ticks };
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
                if (!_bakedObjectCache.TryGetValue(key, out SpriteBake? stale) || stale.BakedShear == req.Shear)
                    continue;
                if (BakeRequest(graphicsDevice, key, req, stale.Rt, out _, out Vector2 refreshedFeet))
                {
                    stale.FeetInRt = refreshedFeet;
                    stale.BakedShear = req.Shear;
                    stale.BakedBlur = _bakeBlurPx;
                    stale.Content = _lastBakeContent;
                    stale.SlotClass = _lastBakeClass;
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

        /// <summary>What the last successful bake computed as its content rect, for the caller
        /// that stores the cache entry. Single-threaded by construction (bakes run on the main
        /// thread inside RenderingWorld), so a field is safe where an out-param would have to be
        /// threaded through both funnels and the request plumbing.</summary>
        private Rectangle _lastBakeContent = new(0, 0, ObjRtW, ObjRtH);
        private int _lastBakeClass;

        /// <summary>Which pool a target came from, recovered from its size. Cheaper and less
        /// error-prone than threading the class through every refresh path.</summary>
        private static int ClassOfSlot(RenderTarget2D rt)
        {
            for (int i = 0; i < ObjSlotClasses.Length; i++)
                if (rt.Width == ObjSlotClasses[i].W && rt.Height == ObjSlotClasses[i].H)
                    return i;
            return ObjSlotClasses.Length - 1;
        }

        /// <summary>
        /// Soften a freshly baked slot IN PLACE: copy it aside, then stamp the copy back nine
        /// times, each shifted by the blur radius and carrying a ninth of the weight. Additive
        /// blending makes that the true mean of nine shifted copies - the core where all nine
        /// land stays solid, the rim fades over the blur radius - which is a cleaner gradient
        /// than the per-frame alpha-compositing trick this replaces, and it runs once per BAKE
        /// (a handful per second on a warm screen) instead of five times per sprite per frame.
        /// </summary>
        private void BlurSlotInPlace(GraphicsDevice graphicsDevice, RenderTarget2D rt)
        {
            if (_bakeBlurPx <= 0f)
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
                _renderTargetSpriteBatch.Draw(scratch, tap * _bakeBlurPx, weight);
            _renderTargetSpriteBatch.End();
        }

        /// <summary>Run one queued request, whichever of the two kinds of bake it is.</summary>
        private bool BakeRequest(GraphicsDevice graphicsDevice, (Texture2D texture, Rectangle src, SpriteEffects effect) key,
            ObjectBakeRequest req, RenderTarget2D? into, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            if (req.ColumnSources != null && req.ColumnLevels != null)
                return BakeTileColumn(graphicsDevice, key.texture, req.ColumnSources, req.ColumnLevels,
                    req.ColumnSources.Length, req.Shear, out rt, out feetInRT, into);
            return BakeObjSprite(graphicsDevice, key.texture, key.src, req.BaseOrigin, key.effect,
                req.Shear, out rt, out feetInRT, into);
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

        private void NoteOversize(Rectangle src, float needW, float needH)
        {
            if (DiagnosticMonitor == null || !_oversizeLogged.Add(src))
                return;
            DiagnosticMonitor.Log($"[shadow] sprite {src.Width}x{src.Height} needs a {needW:0}x{needH:0} slot - "
                + $"larger than the biggest ({ObjSlotClasses[^1].W}x{ObjSlotClasses[^1].H}), so it draws banded.", LogLevel.Debug);
        }

        /// <summary>The smallest slot class that fits a silhouette of this size, or -1 if even the
        /// largest cannot take it (the caller falls back to the banded draw, as it always did for
        /// oversized sprites).</summary>
        private static int ObjSlotClassFor(float neededW, float neededH)
        {
            for (int i = 0; i < ObjSlotClasses.Length; i++)
                if (neededW <= ObjSlotClasses[i].W && neededH <= ObjSlotClasses[i].H - 8f)
                    return i;
            return -1;
        }

        /// <summary>Lease a slot of the given class, reusing a returned one when there is one.</summary>
        private RenderTarget2D RentObjRT(GraphicsDevice graphicsDevice, int slotClass)
        {
            var free = _objectFreeTargetsByClass[slotClass];
            if (free.Count > 0)
            {
                RenderTarget2D reused = free[^1];
                free.RemoveAt(free.Count - 1);
                return reused;
            }
            (int w, int h, _) = ObjSlotClasses[slotClass];
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
                    case Tree tree when tree.growthStage.Value >= 5 && !tree.stump.Value && tree.texture?.Value != null:
                        DrawTreeShadow(spriteBatch, tree, tile, rot, LengthCap(stretch, TreeStretchMax), alpha, blur);
                        break;
                    // Everything else the game still DRAWS as a tree: seeds, sprouts, saplings,
                    // bush-stage growth and stumps. They are short, so they take the full lean a
                    // bush does rather than the damped canopy lean above.
                    case Tree small when small.texture?.Value != null:
                        DrawSmallTreeShadow(spriteBatch, small, tile, rot, LengthCap(stretch, 0.8f), alpha, blur);
                        break;
                    case FruitTree ft when ft.growthStage.Value >= 4 && !ft.stump.Value && ft.texture != null:
                        DrawFruitTreeShadow(spriteBatch, ft, tile, rot, LengthCap(stretch, TreeStretchMax), alpha, blur);
                        break;
                    case Bush bush:
                        DrawBushShadow(spriteBatch, bush, rot, LengthCap(stretch, 0.8f), alpha, blur);
                        break;
                    // DEAD crops cast too. They were excluded, and a withered plant is still a
                    // plant standing on the soil: the game keeps drawing it, from art it keeps
                    // current, until someone scythes it. A field the player let die read as
                    // painted onto the ground while the scarecrow two tiles away stood on it,
                    // which is how it was reported - "the little plants have no shadow", with a
                    // picture of a dead crop row. Nothing else here needed changing: the crop
                    // keeps its texture, its source rect, its draw position and its flip through
                    // dying, so the same call handles it.
                    case HoeDirt { crop: { } crop } hd when !crop.forageCrop.Value && !crop.IsErrorCrop():
                        DrawCropShadow(spriteBatch, crop, tile, rot, LengthCap(stretch, StandingStretchMax), alpha, blur);
                        break;
                    // Grass. It stands on the ground like everything else here and was the only
                    // thing on a meadow not casting, which reads as the grass being printed on the
                    // dirt while the fence beside it stands on it.
                    case StardewValley.TerrainFeatures.Grass grass when grass.texture?.Value != null:
                        DrawGrassShadow(spriteBatch, grass, tile, rot, LengthCap(stretch, 0.35f), alpha, blur);
                        break;
                }
            }

            foreach (var ltf in location.largeTerrainFeatures)
            {
                Vector2 ltile = ltf?.Tile ?? Vector2.Zero;
                if (ltf == null || ltile.X < tileX0 || ltile.X > tileX1 || ltile.Y < tileY0 || ltile.Y > tileY1)
                    continue;
                if (ltf is Bush bush)
                    DrawBushShadow(spriteBatch, bush, rot, LengthCap(stretch, 0.8f), alpha, blur);
            }

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
            foreach (ResourceClump clump in location.resourceClumps)
            {
                if (clump == null)
                    continue;
                Vector2 tile = clump.Tile;
                if (tile.X < tileX0 || tile.X > tileX1 || tile.Y < tileY0 || tile.Y > tileY1)
                    continue;
                DrawResourceClumpShadow(spriteBatch, clump, rot, stretch, alpha, blur);
            }

            // Same viewport-bounded lookup for placed objects (machines, fences, decor): objects is
            // tile-keyed too, so we never walk the whole placed-object set to find the on-screen few.
            var objDict = location.objects;
            if (objectsDrawn)
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
                    // same sun a person does (see StandingStretchMax).
                    DrawBigCraftableShadow(spriteBatch, o, tile, rot, LengthCap(stretch, StandingStretchMax), alpha, blur);
                }
                else if (o.IsSpawnedObject)
                {
                    // Small forage lying on the ground (beach shells, mushrooms, coral…). Short,
                    // strongly-damped shadow.
                    DrawSmallObjectShadow(spriteBatch, o, tile, rot, LengthCap(stretch, StandingStretchMax), alpha, blur);
                }
                else if (!o.isPassable() && o.QualifiedItemId != "(O)590" && o.QualifiedItemId != "(O)SeedSpot")
                {
                    // Everything else that stands on its tile (fences, signs, torches, kegs-as-object,
                    // decor…) gets a real leaning silhouette too — drawn generically from the item's
                    // own sprite via ItemRegistry, so no per-type method is needed. Skip flat passable
                    // items and the ground-mark spots (artifact / seed) that shouldn't cast.
                    DrawGenericObjectShadow(spriteBatch, o, tile, rot, LengthCap(stretch, StandingStretchMax), alpha, blur);
                }
            }

            if (furnitureDrawn)
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
                    EmitObj(spriteBatch, c.sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                        ca, rot, LengthCap(stretch, 0.45f), depth, blur, ObjectHeadFade,
                        c.flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
                }
            }

            // Map-drawn props (street lamps, signs, poles…) aren't entities at all — they're tile
            // columns painted on the map. Cast their shadow from the actual tile art.
            DrawTilePropShadows(spriteBatch, location, rot, stretch, alpha, blur, tileX0, tileX1, tileY0, tileY1);

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
        private void DrawTilePropShadows(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch,
            float alpha, float blur, int tileX0, int tileX1, int tileY0, int tileY1)
        {
            var front = location.map?.GetLayer("Front");
            var always = location.map?.GetLayer("AlwaysFront");
            var bldg = location.map?.GetLayer("Buildings");
            if (front == null || bldg == null)
                return;
            int W = Math.Min(front.LayerWidth, bldg.LayerWidth), H = Math.Min(front.LayerHeight, bldg.LayerHeight);
            tileX0 = Math.Max(0, tileX0); tileX1 = Math.Min(W - 1, tileX1);
            tileY0 = Math.Max(1, tileY0); tileY1 = Math.Min(H - 1, tileY1);

            float rotD = rot;
            float stD = LengthCap(stretch, 0.6f);
            float shear = -(float)Math.Sin(rotD) * stD;
            float shearScaleY = Math.Max(0.15f, stD * (float)Math.Cos(rotD));

            // Near-player prop diagnostics (DebugLogging): every ~3s log why Buildings tiles within
            // 4 tiles of the player do or don't cast — the quick way to see why a fence stays bare.
            bool pdiag = DiagnosticMonitor != null && !_isBakingObjects && Game1.ticks % 600 == 0;
            Point ppt = Game1.player?.TilePoint ?? default;
            void PD(int xx, int yy, string why)
            {
                if (pdiag && Math.Abs(xx - ppt.X) <= 4 && Math.Abs(yy - ppt.Y) <= 4)
                    DiagnosticMonitor!.Log($"[shadow] prop({xx},{yy}) {why}", LogLevel.Debug);
            }

            // Which way the shadow leans decides which neighbouring column the wall guard has to
            // look at, so it is the one part of the classification that cannot be answered without
            // the sun. All three of its possible answers are cached instead.
            int leanDir = shear > 0.01f ? -1 : (shear < -0.01f ? 1 : 0);

            // Everything else here is a question about the MAP ART: which sheet a tile is on, how
            // opaque it is, what stands beside and above it, whether the game calls it passable.
            // None of that changes while you are standing there, and all of it was being worked out
            // again for every tile on screen, in two passes, sixty times a second. Now it is worked
            // out once per tile and kept until the map itself changes.
            if (!ReferenceEquals(location, _propCacheLocation) || !ReferenceEquals(location.map, _propCacheMap)
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
                    int cell = y * W + x;
                    if (!_propCache.TryGetValue(cell, out TilePropCast? cast))
                        _propCache[cell] = cast = ClassifyTileProp(location, bldg, front, always, x, y, W, H);
                    if (!cast.Casts)
                    {
                        if (cast.Note != null)
                            PD(x, y, cast.Note);
                        continue;
                    }
                    if (cast.BlockedNorth || (leanDir < 0 ? cast.BlockedWest : leanDir > 0 && cast.BlockedEast))
                    {
                        PD(x, y, "skip: wall to the north (lean would paint onto it)");
                        continue;
                    }
                    if (cast.Note != null)
                        PD(x, y, cast.Note);

                    var key = cast.Key;
                    Texture2D texture = cast.Texture;
                    int count = cast.Sources.Length;
                    if (_isBakingObjects)
                    {
                        if (_objectGraphicsDevice != null && !_bakedObjectCache.ContainsKey(key)
                            && BakeTileColumn(_objectGraphicsDevice, texture, cast.Sources, cast.Levels, count, shear,
                                out RenderTarget2D rt, out Vector2 fInRT))
                            _bakedObjectCache[key] = new SpriteBake { Rt = rt, FeetInRt = fInRT, BakedShear = shear, BakedBlur = _bakeBlurPx, Content = _lastBakeContent, SlotClass = _lastBakeClass, LastUsedTick = Game1.ticks };
                        continue;
                    }
                    if (!_bakedObjectCache.TryGetValue(key, out SpriteBake? bakedEntry))
                    {
                        // A prop the bake pass has not seen yet (this map arrived after the last
                        // full walk, or its slot was evicted). The classification already holds
                        // the column, so the request is just a reference to it.
                        FrameCost.Count(FrameCost.Counter.BakeMisses);
                        QueueTileColumnBake(key, cast, shear);
                        continue;
                    }
                    FrameCost.Count(FrameCost.Counter.ShadowSprites);
                    bakedEntry.LastUsedTick = Game1.ticks;
                    // Same per-sprite staleness rule as EmitObj: the lean lives in the pixels, so
                    // the column earns a re-bake once the sun has moved its tip a pixel and a half.
                    if (Math.Abs(shear - bakedEntry.BakedShear) * (cast.Height + 1) * 64f > ShearRefreshPixels
                        || Math.Abs(_bakeBlurPx - bakedEntry.BakedBlur) > 0.3f)
                        QueueTileColumnBake(key, cast, shear);
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
                        var tileV = new Vector2(x, y);
                        bodyHere = location.isCharacterAtTile(tileV) != null
                            || (Game1.player != null && Game1.player.currentLocation == location
                                && Game1.player.TilePoint.X == x && Game1.player.TilePoint.Y == y);
                    }
                    catch { }
                    float rowY = bodyHere ? y * 64f : (y + 1f) * 64f;
                    float depth = MathHelper.Clamp(rowY / 10000f + x * 1e-5f - ShadowDepthBias, 0f, 1f);
                    Rectangle propContent = bakedEntry.Content.IsEmpty ? new Rectangle(0, 0, bakedEntry.Rt.Width, bakedEntry.Rt.Height) : bakedEntry.Content;
                    DrawSoft(spriteBatch, Taps9, bakedEntry.Rt, propContent,
                        feet, Color.White, alpha, 0f, bakedEntry.FeetInRt - new Vector2(propContent.X, propContent.Y),
                        new Vector2(1f, shearScaleY), depth, SpriteEffects.None, 0f);
                    // Redraw the base tile OVER its own shadow: the map layer painted before this
                    // batch, so without this the near end of the cast darkens the prop itself
                    // (the "shadow on the lamp post" complaint). Front-stack tiles need no redraw —
                    // the Front layer paints after us anyway.
                    spriteBatch.Draw(texture, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f, y * 64f)), cast.BaseSrc,
                        Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, Math.Min(1f, depth + 5e-4f));
                }
            }
        }

        /// <summary>
        /// Decide, once, whether the map art at one tile is a free-standing prop that should cast,
        /// and if so what its column is made of. Everything it reads is fixed for the map, so the
        /// answer is kept until the map changes underneath it (see the cache in the caller).
        /// </summary>
        private TilePropCast ClassifyTileProp(GameLocation location, xTile.Layers.Layer bldg,
            xTile.Layers.Layer front, xTile.Layers.Layer? always, int x, int y, int W, int H)
        {
            // Reasons are only worth building when someone is reading them.
            TilePropCast NoCast(string why) => new() { Note = DiagnosticMonitor != null ? why : null };

            // Some maps paint the pole top on AlwaysFront instead of Front — treat them as one layer.
            xTile.Tiles.Tile? Ft(int xx, int yy)
            {
                var t = front.Tiles[xx, yy];
                if (t == null && always != null && xx < always.LayerWidth && yy < always.LayerHeight)
                    t = always.Tiles[xx, yy];
                return t;
            }

            var bt = bldg.Tiles[x, y];
            if (bt == null)
                return new TilePropCast();
            // A prop base is a Buildings tile. Front art on the SAME row is normal for
            // fences (their upper half is painted there so the player walks behind it) —
            // it joins the silhouette as a level-0 overlay rather than disqualifying the
            // cell. Animated tiles are skipped — a frozen frame would cast a lie.
            if (bt is xTile.Tiles.AnimatedTile || bt.TileSheet == null)
                return NoCast(bt is xTile.Tiles.AnimatedTile ? "skip: animated tile" : "skip: no tilesheet");
            Texture2D? texture = LoadCached(bt.TileSheet.ImageSource);
            if (texture == null)
                return new TilePropCast();
            var ibB = bt.TileSheet.GetTileImageBounds(bt.TileIndex);
            var baseSrc = new Rectangle(ibB.X, ibB.Y, ibB.Width, ibB.Height);
            float cov = TileCoverage(texture, baseSrc);

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
                    if (ReferenceEquals(stex, texture))
                    {
                        sameSrc = srcS;
                        sameIdx = st.TileIndex;
                        cov = Math.Max(cov, TileCoverage(texture, srcS));
                    }
                    else if (cov < 0.04f)
                    {
                        texture = stex;
                        baseSrc = srcS;
                        baseIdx = st.TileIndex;
                        cov = TileCoverage(stex, srcS);
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
            bool aProp = cov >= 0.04f && cov <= 0.95f;
            int spanW = 1, spanH = 1;
            if (!aProp && cov > 0.04f)
            {
                for (int i = x - 1; i >= 0 && spanW <= MaxPropSpan && bldg.Tiles[i, y] != null; i--) spanW++;
                for (int i = x + 1; i < W && spanW <= MaxPropSpan && bldg.Tiles[i, y] != null; i++) spanW++;
                for (int j = y - 1; j >= 0 && spanH <= MaxPropSpan && bldg.Tiles[x, j] != null; j--) spanH++;
                for (int j = y + 1; j < H && spanH <= MaxPropSpan && bldg.Tiles[x, j] != null; j++) spanH++;
            }
            bool bProp = !aProp && cov > 0.04f && spanW <= MaxPropSpan && spanH <= MaxPropSpan;
            if (!aProp && !bProp)
                return NoCast($"skip: cov={cov:0.00} span={spanW}x{spanH} → not a prop");
            // A "prop" sitting ON opaque art below is wall decor — a window halfway up a
            // house wall must not cast.
            if (OpaqueMapTile(bldg, x, y + 1, H))
                return NoCast("skip: sits on opaque art (wall decor)");
            // A transparent tile BESIDE opaque art is the fringe of a big structure (the
            // truck's edge tiles, awning ends…), not a free-standing prop — its lone-column
            // cast reads as a stray dark line. Real fences/posts never hug opaque art.
            if (aProp && (OpaqueMapTile(bldg, x - 1, y, H) || OpaqueMapTile(bldg, x + 1, y, H)))
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
            _tileColumnSourceRects[0] = baseSrc;
            _tileColumnLevels[0] = 0;
            int count = 1, levels = 1, keyHash = 17 * 31 + baseIdx;
            if (sameSrc is Rectangle sr)
            {
                _tileColumnSourceRects[count] = sr;
                _tileColumnLevels[count++] = 0;
                keyHash = keyHash * 31 + sameIdx;
            }
            for (int i = 1; count < _tileColumnSourceRects.Length && y - i >= 0; i++)
            {
                var t = Ft(x, y - i);
                if (t == null || t is xTile.Tiles.AnimatedTile || t.TileSheet == null
                    || !ReferenceEquals(LoadCached(t.TileSheet.ImageSource), texture))
                    break;
                var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
                _tileColumnSourceRects[count] = new Rectangle(ib.X, ib.Y, ib.Width, ib.Height);
                _tileColumnLevels[count++] = i;
                levels = i + 1;
                keyHash = keyHash * 31 + t.TileIndex;
            }

            // Wall guard, scaled to the prop's height: the up-lean cast occupies the tiles
            // north of the base (and one column toward the lean side) — if any of those is
            // opaque wall art, the shadow would paint onto the wall ("through the house").
            // All three lean directions are answered here so the draw pass can just pick one.
            var result = new TilePropCast
            {
                Casts = true,
                Texture = texture,
                BaseSrc = baseSrc,
                Height = levels,
                Sources = new Rectangle[count],
                Levels = new int[count],
                Key = (texture, new Rectangle(keyHash, count, -1, -1), SpriteEffects.None),   // width −1 can never collide with a real source rect
                Note = DiagnosticMonitor != null ? $"cast: col={count} cov={cov:0.00}" : null,
            };
            Array.Copy(_tileColumnSourceRects, result.Sources, count);
            Array.Copy(_tileColumnLevels, result.Levels, count);
            for (int i = 1; i <= levels; i++)
            {
                result.BlockedNorth |= OpaqueMapTile(bldg, x, y - i, H);
                result.BlockedWest |= OpaqueMapTile(bldg, x - 1, y - i, H);
                result.BlockedEast |= OpaqueMapTile(bldg, x + 1, y - i, H);
            }
            return result;
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
            Texture2D? texture = LoadCached(t.TileSheet.ImageSource);
            if (texture == null)
                return true;
            var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
            // Same bound as the aProp cast test (0.95). With a LOWER bound here, dense fence
            // tiles at cov 0.91–0.95 counted as "walls" and poisoned their own neighbours —
            // every tile of the row skipped as "structure fringe" and no fence ever cast.
            return TileCoverage(texture, new Rectangle(ib.X, ib.Y, ib.Width, ib.Height)) > 0.95f;
        }

        /// <summary>Fraction of a tile's art that is opaque (alpha > 48). Sampled once per
        /// (sheet, rect) and cached — this is the "look at the actual image" prop test.</summary>
        private readonly System.Collections.Generic.Dictionary<(Texture2D texture, Rectangle src), float> _tileCovCache = new();
        private Color[] _tileCoveragePixels = new Color[1024];
        // Whole-tilesheet pixel cache: reading each prop tile with its own texture.GetData is a separate
        // GPU readback (pipeline flush); walking into a prop-heavy screen fired a burst of them in one
        // frame. Read each sheet back ONCE, then count coverage from the CPU array (zero GPU work).
        private readonly System.Collections.Generic.Dictionary<Texture2D, Color[]?> _tilesheetCoveragePixels = new();
        // Refusal bound for absurd sheets only — see the note on RenderPipeline.SheetPixCap. The
        // old 8 Mpx ceiling landed under real modded tilesheets, and the fallback beneath it is a
        // GPU readback per tile.
        private const int CovSheetCap = 64_000_000;
        private const int CovStripRows = 512;

        private Color[]? CovSheetPixels(Texture2D texture)
        {
            if (_tilesheetCoveragePixels.TryGetValue(texture, out Color[]? px))
                return px;
            long n = (long)texture.Width * texture.Height;
            if (n <= CovSheetCap)
            {
                try
                {
                    px = new Color[n];
                    for (int y0 = 0; y0 < texture.Height; y0 += CovStripRows)
                    {
                        int rows = Math.Min(CovStripRows, texture.Height - y0);
                        texture.GetData(0, new Rectangle(0, y0, texture.Width, rows),
                            px, y0 * texture.Width, rows * texture.Width);
                    }
                }
                catch { px = null; }
            }
            _tilesheetCoveragePixels[texture] = px;
            return px;
        }

        private float TileCoverage(Texture2D texture, Rectangle src)
        {
            if (_tileCovCache.TryGetValue((texture, src), out float cov))
                return cov;
            int len = src.Width * src.Height;
            if (len <= 0 || src.X < 0 || src.Y < 0 || src.Right > texture.Width || src.Bottom > texture.Height)
                return _tileCovCache[(texture, src)] = 1f;
            int solid = 0;
            Color[]? sheet = CovSheetPixels(texture);
            if (sheet != null)
            {
                int tw = texture.Width;
                for (int row = 0; row < src.Height; row++)
                {
                    int soff = (src.Y + row) * tw + src.X;
                    for (int c = 0; c < src.Width; c++)
                        if (sheet[soff + c].A > 48) solid++;
                }
            }
            else
            {
                if (_tileCoveragePixels.Length < len)
                    _tileCoveragePixels = new Color[len];
                try { texture.GetData(0, src, _tileCoveragePixels, 0, len); }
                catch { return _tileCovCache[(texture, src)] = 1f; }
                for (int i = 0; i < len; i++)
                    if (_tileCoveragePixels[i].A > 48) solid++;
            }
            return _tileCovCache[(texture, src)] = (float)solid / len;
        }

        /// <summary>Record a map-tile column for the next bake pass. The classification owns the
        /// column arrays and outlives the frame, so the request just points at them.</summary>
        private void QueueTileColumnBake((Texture2D texture, Rectangle src, SpriteEffects effect) key, TilePropCast cast, float shear)
        {
            if (_objectBakeQueue.Count >= ObjectBakeQueueCap || cast.Sources.Length == 0)
                return;
            _objectBakeQueue[key] = new ObjectBakeRequest { Shear = shear, ColumnSources = cast.Sources, ColumnLevels = cast.Levels };
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
            public (Texture2D texture, Rectangle src, SpriteEffects effect) Key;
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

        /// <summary>Bake a stacked tile column (black + feet→tip gradient, sun lean pre-baked as a
        /// shear about the feet row) into a pooled object RT. The sources and their heights are
        /// passed in rather than read from the scan's scratch arrays, so a queued re-bake a frame
        /// later replays the same column without redoing the scan that found it.</summary>
        private bool BakeTileColumn(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle[] sources, int[] tileLevels,
            int count, float shear, out RenderTarget2D renderTarget, out Vector2 feetInRT, RenderTarget2D? into = null)
        {
            renderTarget = null!;
            feetInRT = default;
            int levels = 0;
            for (int i = 0; i < count; i++)
                levels = Math.Max(levels, tileLevels[i] + 1);
            float columnHeight = levels * 64f;
            int colClass = into != null ? ClassOfSlot(into)
                : ObjSlotClassFor(64f + Math.Abs(shear) * columnHeight + 2f * _bakeBlurPx, columnHeight + _bakeBlurPx);
            if (colClass < 0)
            {
                NoteColumnRefusal($"no slot fits a {columnHeight:0}px column with shear {shear:0.00}");
                return false;
            }
            _lastBakeClass = colClass;
            if (count <= 0 || columnHeight > ObjSlotClasses[colClass].H - 8f
                || 64f + Math.Abs(shear) * columnHeight > ObjSlotClasses[colClass].W)
            {
                NoteColumnRefusal($"column {columnHeight:0}px shear {shear:0.00} count {count} "
                    + $"refused by class {colClass} ({ObjSlotClasses[colClass].W}x{ObjSlotClasses[colClass].H})");
                return false;
            }
            renderTarget = into ?? RentObjRT(graphicsDevice, colClass);
            feetInRT = new Vector2(renderTarget.Width / 2f, renderTarget.Height - 8f);
            Matrix lean = ShearAbout(feetInRT, shear);
            try
            {
                graphicsDevice.SetRenderTarget(renderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, lean);
                for (int i = 0; i < count; i++)
                    _renderTargetSpriteBatch.Draw(texture, new Vector2(feetInRT.X - 32f, feetInRT.Y - 64f * (tileLevels[i] + 1)),
                        sources[i], Color.Black, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                _renderTargetSpriteBatch.End();
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_propGradientTexture!, new Rectangle(0, (int)(feetInRT.Y - columnHeight), renderTarget.Width, (int)columnHeight), Color.White);
                _renderTargetSpriteBatch.End();
                BlurSlotInPlace(graphicsDevice, renderTarget);
                _lastBakeContent = ContentBounds(new Vector2(feetInRT.X - 32f, feetInRT.Y - columnHeight),
                    64f, columnHeight, feetInRT, shear, _bakeBlurPx, renderTarget.Width, renderTarget.Height);
                FrameCost.Count(FrameCost.Counter.ObjectBakes);
                return true;
            }
            catch
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                return false;
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
            EmitObj(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        /// <summary>
        /// Buildings are too tall for an upright silhouette (it juts up over the building itself),
        /// so they get a soft contact POOL at the footprint base instead — grounds the building
        /// without overlapping it or ghosting. Shape-accurate isn't achievable for tall map/entity
        /// structures with these 2D techniques; a grounding pool is the clean compromise.
        /// </summary>
        private void DrawBuildingShadow(SpriteBatch spriteBatch, Building bld, float alpha, float blur)
        {
            float w = bld.tilesWide.Value * 64f;
            float baseX = (bld.tileX.Value + bld.tilesWide.Value / 2f) * 64f;
            float baseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f;   // footprint bottom = ground line
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(baseX, baseY - 10f));
            float depth = MathHelper.Clamp(baseY / 10000f - ShadowDepthBias, 0f, 1f);
            DrawContactBlob(spriteBatch, feet, w * 0.5f * 0.85f, 24f, alpha, depth, blur);
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
            // largest sprite-count driver in the mod: up to four EmitObj calls per grass tile
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
            EmitObj(spriteBatch, texture, GrassArt.BladeShadowSource(grass, 0, which),
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
            EmitObj(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        /// <summary>How wide and how tall a mass of opaque Buildings art may be and still be a
        /// THING standing on the ground rather than the ground itself. A cactus, a post, a
        /// signboard, a crate are one or two tiles either way; a cliff face or a house wall is
        /// not. This is the test that opacity was standing in for, badly.</summary>
        private const int MaxPropSpan = 2;

        /// <summary>
        /// How long a tree's shadow may get, as a fraction of the trunk-to-crown height.
        ///
        /// <para>
        /// This is the ONLY lever that may be pulled to stop a canopy cast detaching from its own
        /// trunk. Two constants used to damp the tree's and the props' sun ANGLE instead — 0.38 and
        /// 0.6 of the character angle — and that is a different sun for each of them. The geometry
        /// says so plainly: the silhouette is sheared by <c>-sin(rot)·stretch</c> sideways and
        /// <c>cos(rot)·stretch</c> upward, so the tip lands at an angle of exactly <c>-rot</c> and
        /// the stretch cancels out of it. Shortening a shadow leaves its direction alone; damping
        /// the angle moves the sun.
        /// </para>
        ///
        /// <para>
        /// Reported, correctly, as two suns: at six in the morning the player's shadow pointed one
        /// way and every tree's pointed another, and the reporter had measured the difference in
        /// clock hours before writing in. Everything the sun casts now takes one angle, and the
        /// caps below decide only how far each thing reaches.
        /// </para>
        /// </summary>
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

        private void DrawBigCraftableShadow(SpriteBatch spriteBatch, SObject o, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            if (!TryItemArt(o.QualifiedItemId, out Texture2D texture, out Rectangle src))
                return;
            // Big craftables sit ON their tile; the barrel/machine visually rests a bit above the
            // tile's bottom edge, so anchor the shadow's dark base slightly up from (tile.Y+1)*64.
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 20f));
            float depth = MathHelper.Clamp(Math.Max(0f, ((tile.Y + 1f) * 64f - 24f) / 10000f) + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
            EmitObj(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
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

        /// <summary>
        /// How long the shadow of a thing STANDING on the ground may get, as a fraction of its
        /// own height. Crops, forage, fences, signs, kegs, machines.
        ///
        /// <para>
        /// A shadow has to clear the thing casting it or it is not read as a shadow. The lean
        /// slides the silhouette sideways by <c>sin(rot)·stretch</c> per pixel of height, so at a
        /// sun 50 degrees off vertical a plant twelve pixels tall needed a stretch near 0.85 for
        /// its shadow to escape its own eight-pixel half-width. The old ceiling of 0.55 put it at
        /// 0.51, and the shadow of every dead crop landed ON the plant. Reported as exactly that:
        /// the shadow is on top of the plant, it should be behind it, with an A/B screenshot.
        /// Forage (0.4) and fences (0.5) were the same failure at other numbers, and were reported
        /// too: "a hint of shadow but nothing deserving to be called one".
        /// </para>
        ///
        /// <para>
        /// A standing sprite's height IS the thing's height, so there was never a reason for any
        /// of these to take a shorter sun than a person does. The ceilings that remain are for
        /// sprites whose height is not a height: a tree's canopy, a bush's mass, a painted-on map
        /// prop's tile column.
        /// </para>
        /// </summary>
        private const float StandingStretchMax = 1.0f;

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
            EmitObj(spriteBatch, texture, crop.sourceRect, feet, CropOrigin,
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
            EmitObj(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur);
        }

        // ItemRegistry.GetDataOrErrorItem parses the qualified id and walks the item-data registry;
        // doing it per on-screen object ×2 passes ×60fps is wasted work when the resolved sprite is
        // static. Cache (texture, sourceRect) per QualifiedItemId, cleared when the season rolls over
        // (a few items swap art by season).
        private readonly System.Collections.Generic.Dictionary<string, (Texture2D? texture, Rectangle src)> _itemArtCache = new();
        private string _itemArtSeason = "";

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
            EmitObj(spriteBatch, texture, src, feet, baseOrigin, alpha, rot, stretch, depth, blur, ObjectHeadFade);
        }

        private void DrawTreeShadow(SpriteBatch spriteBatch, Tree tree, Vector2 tile, float rot, float stretch, float alpha, float blur)
        {
            Rectangle src = Tree.treeTopSourceRect;                 // (0,0,48,96) standard canopy
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
            EmitObj(spriteBatch, tree.texture.Value, trunk, feet, new Vector2(trunk.Width / 2f, trunk.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
            // Tree canopy draws with origin (24, 96); fade about the trunk base.
            EmitObj(spriteBatch, tree.texture.Value, src, feet, new Vector2(24f, 96f),
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
            EmitObj(spriteBatch, tree.texture.Value, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, stretch, depth, blur, ObjectHeadFade, effect);
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
            EmitObj(spriteBatch, ft.texture, new Rectangle(384, row * 5 * 16 + 48, 48, 32), feet,
                new Vector2(24f, 32f), alpha, rot, stretch, depth, blur, ObjectHeadFade, effects);
            EmitObj(spriteBatch, ft.texture, src, feet, new Vector2(24f, 80f),
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
            EmitObj(spriteBatch, Bush.texture.Value, src, feet, baseOrigin,
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
            float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);

            // The baked silhouette is one cohesive image — flatten it vertically and lean it
            // about the feet as a single unit (no per-layer fragmenting), softened at the edges.
            DrawSoft(spriteBatch, Taps9, _playerRenderTarget, null, feet, Color.White, alpha, rot, _playerFeetInRenderTarget,
                new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
        }
    }
}