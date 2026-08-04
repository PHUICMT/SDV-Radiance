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
        private RenderTarget2D? _reflectRT;
        internal bool ReflectRTReady;
        internal bool ReflectRTHasPlayer;   // player stamped this frame → the shader retires
                                            // its wading-silhouette fallback

        /// <summary>Bake the flipped-entity reflection layer for this frame. Called from
        /// Display.RenderingWorld right after the sprite mask bake (the only safe spot
        /// for render-target swaps).</summary>
        public void BakeWaterReflection()
        {
            ReflectRTReady = false;
            ReflectRTHasPlayer = false;
            GameLocation? loc = Game1.currentLocation;
            if (loc == null || !_waterAny || Game1.game1.takingMapScreenshot)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_reflectRT == null || _reflectRT.Width != w || _reflectRT.Height != h)
            {
                _reflectRT?.Dispose();
                _reflectRT = new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _spriteMaskBatch ??= new SpriteBatch(_device);

            try
            {
                _device.SetRenderTarget(_reflectRT);
                _device.Clear(Color.Transparent);
                var sb = _spriteMaskBatch;
                // BackToFront + per-stamp depth from the caster's TRUE feet row: whoever
                // stands in front (bigger feet Y) draws last and wins the overlap — a
                // fixed draw order let a tree's reflection cover the player standing in
                // front of it.
                sb.Begin(SpriteSortMode.BackToFront, BlendState.AlphaBlend, SamplerState.PointClamp);

                // Player — the colour bake, flipped below the feet. Swimming is skipped:
                // half the body is underwater, a full mirrored silhouette reads as a glitch.
                var who = Game1.player;
                var pcol = ShadowRenderer.PlayerColor;
                if (who != null && pcol != null && !who.swimming.Value)
                {
                    Rectangle box = who.GetBoundingBox();
                    float pFeetY = box.Bottom - 10f + who.yOffset;
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, pFeetY));
                    // The bake pins the feet at (RtW/2, RtH-8); flipped, that anchor is 8px
                    // from the TOP. Position so the flipped feet meet the anchor. Sliced into
                    // 16-row bands for the same feet→head fade the NPC stamps get.
                    float pDepth = StampDepth(pFeetY);
                    const int pbh = 16;
                    int pbn = ShadowRenderer.PlayerRtH / pbh;
                    for (int i = 0; i < pbn; i++)
                    {
                        var srcR = new Rectangle(0, ShadowRenderer.PlayerRtH - (i + 1) * pbh,
                            ShadowRenderer.PlayerRtW, pbh);
                        float a = MathHelper.Lerp(1f, ReflHeadFade, (i + 0.5f) / pbn);
                        sb.Draw(pcol, feet + new Vector2(-ShadowRenderer.PlayerRtW / 2f, (i * pbh - 8f) * MirrorSquash),
                            srcR, Color.White * a, 0f, Vector2.Zero, new Vector2(1f, MirrorSquash),
                            SpriteEffects.FlipVertically, pDepth);
                    }
                    ReflectRTHasPlayer = true;
                }

                // NPCs + monsters, bottom-centre at the collision-box feet (same anchor the
                // game and the sprite mask use), flipped to hang downward.
                foreach (NPC c in loc.characters)
                {
                    if (c?.Sprite?.Texture == null || c.IsInvisible || c.swimming.Value)
                        continue;
                    StampFlipped(sb, c.Sprite.Texture, c.Sprite.SourceRect, c.GetBoundingBox(), c.drawOffset);
                }
                // Cutscene actors live in the event, not loc.characters — effects must keep
                // working in cutscenes (house rule), and actors often stand at the water.
                if (Game1.CurrentEvent?.actors != null)
                {
                    foreach (NPC c in Game1.CurrentEvent.actors)
                    {
                        if (c?.Sprite?.Texture == null || c.IsInvisible)
                            continue;
                        StampFlipped(sb, c.Sprite.Texture, c.Sprite.SourceRect, c.GetBoundingBox(), c.drawOffset);
                    }
                }
                // Farm animals.
                foreach (var a in loc.animals.Values)
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    StampFlipped(sb, a.Sprite.Texture, a.Sprite.SourceRect, a.GetBoundingBox());
                }
                // Critters: bottom edge at position.Y, centred on position.X (Critter.draw).
                if (loc.critters != null)
                {
                    foreach (var cr in loc.critters)
                    {
                        if (cr?.sprite?.Texture == null)
                            continue;
                        // Same stamp every body uses (one anchor rule, the same feet->head
                        // fade): a butterfly's reflection was drawn at full opacity by its own
                        // code path while every body faded, so it read as a sticker.
                        Rectangle crs = cr.sprite.SourceRect;
                        var crBox = new Rectangle((int)cr.position.X - crs.Width * 2,
                            (int)cr.position.Y - crs.Height * 4, crs.Width * 4, crs.Height * 4);
                        StampFlipped(sb, cr.sprite.Texture, crs, crBox);
                    }
                }

                // Trees / fruit trees / bushes: sprites, not map art — the scenery re-render
                // (P3c) can't see them, so their reflections are built here, flipped around
                // the trunk/stem base. Same tile-walk culling as the sprite mask.
                var vp = Game1.viewport;
                var tfDict = loc.terrainFeatures;
                int ctx0 = (int)Math.Floor((vp.X - 256) / 64f), ctx1 = (int)Math.Floor((vp.X + vp.Width + 256) / 64f);
                int cty0 = (int)Math.Floor((vp.Y - 512) / 64f), cty1 = (int)Math.Floor((vp.Y + vp.Height + 768) / 64f);
                for (int cvY = cty0; cvY <= cty1; cvY++)
                for (int cvX = ctx0; cvX <= ctx1; cvX++)
                {
                    Vector2 tile = new(cvX, cvY);
                    if (!tfDict.TryGetValue(tile, out var tf))
                        continue;
                    switch (tf)
                    {
                        // Grown tree: canopy 48×96 with the trunk base at tile*64+(32,64).
                        // Flipped: origin moves to the TOP of the source (24, 0).
                        case StardewValley.TerrainFeatures.Tree tree when tree.growthStage.Value >= 5 && !tree.stump.Value && tree.texture?.Value != null:
                            sb.Draw(tree.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                StardewValley.TerrainFeatures.Tree.treeTopSourceRect, Color.White, 0f, new Vector2(24f, 0f), 4f,
                                SpriteEffects.FlipVertically | (tree.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                                StampDepth(tile.Y * 64f + 64f));
                            break;
                        // Mature fruit tree: 48×64 seasonal foliage, base at tile*64+(32,64).
                        case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4 && !ft.stump.Value && ft.texture != null:
                            int season = Game1.GetSeasonIndexForLocation(ft.Location);
                            var fsrc = new Rectangle((12 + season * 3) * 16, ft.GetSpriteRowNumber() * 5 * 16, 48, 64);
                            sb.Draw(ft.texture,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 64f)),
                                fsrc, Color.White, 0f, new Vector2(24f, fsrc.Height - 80f), 4f,
                                SpriteEffects.FlipVertically | (ft.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                                StampDepth(tile.Y * 64f + 64f));
                            break;
                        // Bush: bottom-centre at (tile.X*64 + (eff+1)*32, (tile.Y+1)*64).
                        case StardewValley.TerrainFeatures.Bush bush when !bush.sourceRect.Value.IsEmpty:
                            var bsrc = bush.sourceRect.Value;
                            int eff = bush.size.Value switch { 3 => 0, 4 => 1, _ => bush.size.Value };
                            sb.Draw(StardewValley.TerrainFeatures.Bush.texture.Value,
                                Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + (eff + 1) * 32f, (tile.Y + 1) * 64f)),
                                bsrc, Color.White, 0f, new Vector2(bsrc.Width / 2f, 0f), 4f,
                                SpriteEffects.FlipVertically | (bush.flipped.Value ? SpriteEffects.FlipHorizontally : SpriteEffects.None),
                                StampDepth((tile.Y + 1) * 64f));
                            break;
                    }
                }

                sb.End();
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
        private void StampFlipped(SpriteBatch sb, Texture2D tex, Rectangle src, Rectangle bb, Vector2 drawOffset = default)
        {
            // The SAME feet rule the player's stamp uses: the 10 px lift (a collision box sits a
            // little below the drawn shoes) and the sprite's own draw offset. Without them an NPC
            // mirrored 10 px lower than the player standing beside it, and a seated one mirrored
            // where it was not drawn. House rule: an NPC and the player get identical treatment.
            float feetY = bb.Bottom - 10f + drawOffset.Y;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, new Vector2(bb.Center.X + drawOffset.X, feetY));
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
                sb.Draw(tex, feet + new Vector2(0f, i * hs * scale.Y), srcR, Color.White * a,
                    0f, origin, scale, SpriteEffects.FlipVertically, depth);
            }
        }

        /// <summary>BackToFront layer depth from the caster's TRUE feet row: bigger feet Y
        /// = closer to the camera = drawn later = wins reflection overlaps.</summary>
        private static float StampDepth(float feetWorldY) =>
            MathHelper.Clamp(1f - feetWorldY / 65536f, 0.001f, 1f);

        // ---- P3c-lite: clean scenery source for the screen-space mirror ----

        private RenderTarget2D? _mirrorSrcRT;
        internal bool SceneRTReady;

        /// <summary>Re-render the map's own layers (Back/Buildings/Front families, numbered
        /// variants included — DR issue #48) into a sprite-free source for the mirror.
        /// Excluding a sprite from the composed screen used to leave a player-shaped SKY
        /// hole in the scenery's reflection; sampling a source that never contained the
        /// sprite shows the true map pixels behind them instead. Same RenderingWorld slot
        /// as the other bakes (render-target swaps are safe there).</summary>
        public void BakeSceneryReflection()
        {
            SceneRTReady = false;
            GameLocation? loc = Game1.currentLocation;
            if (loc?.map == null || !_waterAny || Game1.game1.takingMapScreenshot)
                return;

            RenderTargetBinding[] prev = _device.GetRenderTargets();
            int w = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt ? rt.Width : Game1.viewport.Width;
            int h = prev.Length > 0 && prev[0].RenderTarget is RenderTarget2D rt2 ? rt2.Height : Game1.viewport.Height;
            if (w <= 0 || h <= 0)
                return;
            if (_mirrorSrcRT == null || _mirrorSrcRT.Width != w || _mirrorSrcRT.Height != h)
            {
                _mirrorSrcRT?.Dispose();
                _mirrorSrcRT = new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _spriteMaskBatch ??= new SpriteBatch(_device);

            static bool Fam(string id, string fam)
            {
                if (!id.StartsWith(fam, StringComparison.Ordinal))
                    return false;
                for (int k = fam.Length; k < id.Length; k++)
                    if (id[k] < '0' || id[k] > '9') return false;   // "Back-1" = disabled layer
                return true;
            }

            try
            {
                _device.SetRenderTarget(_mirrorSrcRT);
                _device.Clear(Color.Black);
                var sb = _spriteMaskBatch;
                sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                var dd = Game1.mapDisplayDevice;
                dd.BeginScene(sb);
                // Bottom-up families, same order the game composes them. AlwaysFront is
                // deliberately out: it is mostly weather + translucent shadow washes.
                foreach (string fam in _sceneFams)
                {
                    foreach (var l in loc.map.Layers)
                    {
                        if (Fam(l.Id, fam))
                            l.Draw(dd, Game1.viewport, xTile.Dimensions.Location.Origin, false, 4);
                    }
                }
                dd.EndScene();
                sb.End();
                SceneRTReady = true;
            }
            catch (Exception ex)
            {
                try { _spriteMaskBatch!.End(); } catch { }
                if (!_sceneErrLogged) { _sceneErrLogged = true; _monitor.Log($"[water] scenery source bake threw: {ex}", StardewModdingAPI.LogLevel.Warn); }
            }
            finally
            {
                _device.SetRenderTargets(prev);
            }
        }

        private static readonly string[] _sceneFams = { "Back", "Buildings", "Front" };
        private bool _sceneErrLogged;

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
            if (who == null || _waterMask == null || _waterPixBuf == null)
                return "[reflect] no player / no water mask on this map";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[reflect] loc={Game1.currentLocation?.Name} waterAny={_waterAny} maskOrigin=({_lastWaterTx},{_lastWaterTy}) maskPx={_waterMask.Width}x{_waterMask.Height}");
            sb.AppendLine($"[reflect] entityRT ready={ReflectRTReady} hasPlayer={ReflectRTHasPlayer} squash={MirrorSquash} | sceneRT ready={SceneRTReady} forcedOff={SceneSourceOff} | spriteMask ready={SpriteMaskReady}");
            sb.AppendLine($"[reflect] wlAnchor={(_wlAnchor != null ? $"built for {_wlAnchor.Loc?.Name} ({_wlAnchor.PixW}x{_wlAnchor.PixH})" : "none yet")}");

            Rectangle box = who.GetBoundingBox();
            int mpw = _waterMask.Width;
            for (int t = 0; t <= 4; t++)
            {
                int wx = box.Center.X / 4 - _lastWaterTx * 16;
                int wy = (box.Bottom - 4) / 4 - _lastWaterTy * 16 + t * 16;
                if (wx < 0 || wy < 0 || wx >= mpw || wy >= _waterMask.Height)
                { sb.AppendLine($"[reflect] +{t} tile: outside the mask window"); continue; }
                Color m = _waterPixBuf[wy * mpw + wx];
                string kind = m.A < 64 ? "ice" : m.A < 192 ? "lava" : "water";
                sb.AppendLine($"[reflect] +{t} tile below feet: effectR={m.R} marchG={m.G} edgeDistB={m.B} ({m.B * 0.5f:0.0} texels to the waterline) type={kind}"
                            + (m.G == 0 ? "   <- NO entity reflection here (not march water)" : ""));
            }

            var scr = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Bottom));
            int sx = (int)scr.X, sy = (int)scr.Y;
            Vector4 sceneMean = MeanAt(_mirrorSrcRT, sx, sy - 96);
            Vector4 entMean = MeanAt(_reflectRT, sx, sy + 32);
            sb.AppendLine($"[reflect] sceneRT mean 1.5 tiles ABOVE the feet (the mirror's source) = {(sceneMean.X < 0 ? "unreadable" : $"rgb({sceneMean.X:0.00},{sceneMean.Y:0.00},{sceneMean.Z:0.00}) a={sceneMean.W:0.00}")}");
            sb.AppendLine($"[reflect] entityRT mean 0.5 tile BELOW the feet (your own reflection) = {(entMean.X < 0 ? "unreadable" : $"rgb({entMean.X:0.00},{entMean.Y:0.00},{entMean.Z:0.00}) a={entMean.W:0.00}")}");
            sb.AppendLine("[reflect] a near-black sceneRT mean with lit map art on screen = the P3c source is the bug; run 'radiance_reflect scene off' and compare.");
            return sb.ToString().TrimEnd();
        }
    }
}
