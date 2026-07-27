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
    /// ShadowRenderer — BAKING and raster utilities: the persistent silhouette caches
    /// (player pose / character frames / object sprites), the render-target pools, and the
    /// low-level soft-draw helpers (gradients, blob, 9-tap disc, banded fallback).
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>
        /// Render the player's full silhouette (all FarmerRenderer layers, so hats / hair /
        /// Fashion-Sense outfits are included) to an offscreen target, upright and black.
        /// Called during RenderingWorld, before the world batches open, so a render-target
        /// swap is safe. The lean/squash/soften happen later when this is composited.
        /// </summary>
        public void PreparePlayer(GraphicsDevice gd, ModConfig config)
        {
            if (!ShouldCast(config))
            {
                _playerReady = false;
                PlayerMask = null;
                return;
            }
            if (_renderDepth > 0)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log("[shadow] PreparePlayer re-entered — skipping nested call", LogLevel.Warn); }
                return;
            }
            _renderDepth++;
            try
            {
            _rtBatch ??= new SpriteBatch(gd);
            _gradTex ??= BuildGradient(gd);
            _propGradTex ??= BuildGradient(gd, 0f);
            _blobTex ??= BuildBlob(gd);

            // ---- Persistent bake caches (the old clear-everything-every-frame here cost
            // 50-150 render-target switches per frame — the single biggest stutter source) ----
            // CHARACTER bakes are upright silhouettes keyed by (texture, frame): valid forever,
            // only capped. OBJECT bakes have the sun lean baked in as a shear: valid until the
            // sun angle ticks (every 10 game minutes) or the location changes.
            if (_casterBakes.Count > 192)
            {
                _casterBakes.Clear();
                _casterUsed = 0;
            }

            bool objectsOn = SunCasts() && config.DirectionalShadowObjects;
            float srot = 0f, sstretch = 0f;
            if (objectsOn)
            {
                ComputeSun(out srot, out sstretch, out _);
                sstretch *= Math.Max(0.1f, config.DirectionalShadowLength);
            }
            long shearKey = objectsOn
                ? ((long)Math.Round(srot * 512f) << 20) ^ (long)Math.Round(sstretch * 512f)
                : long.MinValue;
            bool objCacheInvalid = shearKey != _objShearKey
                                || Game1.currentLocation != _objBakeLoc
                                || _bakedObjMap.Count > 128;   // VRAM cap: slots are 400×456 RTs (~0.7 MB each)
            if (objCacheInvalid)
            {
                _bakedObjMap.Clear();
                _objUsed = 0;
                _objShearKey = shearKey;
                _objBakeLoc = Game1.currentLocation;
            }

            // Bake NPC + animal silhouettes (single-sprite casters) — cheap when warm: cache
            // hits only, no RT switch. Runs every frame so new animation frames bake instantly.
            BakeCasters(gd, Game1.currentLocation);

            // Bake OBJECT silhouettes (trees/bushes/clumps/furniture/craftables/…) by running the
            // object enumeration in BAKE mode. Composited later in DrawObjectShadows. Runs every
            // frame so sprites entering the view bake instantly (a 15-tick heartbeat was tried —
            // its cache-miss frames drew the banded fallback, reading as line artifacts) — but a
            // WARM frame is dictionary hits only, no RT switches, which is where the cost was.
            if (objectsOn)
            {
                _objBaking = true;
                _objGd = gd;
                RenderTargetBinding[] objPrev = gd.GetRenderTargets();
                try { DrawObjectShadows(_rtBatch, Game1.currentLocation, srot, sstretch, 0f, 0f); }
                catch (Exception ex) { if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] obj bake threw: {ex}", LogLevel.Warn); } }
                finally { gd.SetRenderTargets(objPrev); _objBaking = false; }
            }

            // Sitting still casts (the bake captures the current SEATED animation frame, so the
            // silhouette matches the pose); only swimming and horseback skip — the water owns
            // the swimmer's reflection, and the horse's own shadow covers the rider.
            Farmer who = Game1.player;
            if (who == null || who.currentLocation != Game1.currentLocation
                || who.swimming.Value || who.isRidingHorse())
            {
                _playerReady = false;
                PlayerMask = null;
                return;
            }

            // PreserveContents is REQUIRED for every persistent bake target: the default
            // DiscardContents only guarantees the pixels until the next target swap/present,
            // which was fine when everything re-baked per frame — cached across frames, the
            // content decayed into garbage (grid-line artifacts all over the map).
            _playerRT ??= new RenderTarget2D(gd, PlayerRtW, PlayerRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

            Rectangle src = who.FarmerSprite.SourceRect;

            // Same pose as the last bake → the RT is still correct, skip the 3-batch redraw.
            // The every-8-frames refresh keeps accessory layers that animate independently of
            // the body frame (Fashion Sense hair sway etc.) fresh without paying every frame.
            var sig = (who.FarmerSprite.CurrentFrame, (int)who.FacingDirection, src);
            if (_playerReady && sig == _playerBakeSig && Game1.ticks % 8 != 0)
            {
                PlayerMask = _playerRT;
                return;
            }
            _playerBakeSig = sig;

            float w = src.Width * 4f, h = src.Height * 4f;
            Vector2 pos = new Vector2((PlayerRtW - w) / 2f, PlayerRtH - h - 8f);
            _playerFeetInRT = new Vector2(PlayerRtW / 2f, PlayerRtH - 8f);

            RenderTargetBinding[] prev = gd.GetRenderTargets();
            try
            {
                gd.SetRenderTarget(_playerRT);
                gd.Clear(Color.Transparent);
                _rtBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_rtBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                    src, pos, Vector2.Zero, 0f, who.FacingDirection, Color.Black, 0f, 1f, who);
                _rtBatch.End();

                // Scrub COLOUR out of the bake (RGB→0, alpha kept): appearance mods (Fashion
                // Sense etc.) draw through their own patches and ignore the black tint above,
                // so without this a white dress cast a white shadow. Works for ANY current or
                // future appearance mod — whatever got drawn, only its shape survives.
                _gradTex ??= BuildGradient(gd);
                _rtBatch.Begin(SpriteSortMode.Deferred, ZeroColor, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
                _rtBatch.End();

                // Fade the silhouette's opacity from the feet (full) to the head/far tip (faint),
                // so the stretched far end reads as a soft penumbra rather than a hard clone.
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
                _rtBatch.End();
                _playerReady = true;
                PlayerMask = _playerRT;
            }
            catch (Exception ex)
            {
                try { _rtBatch.End(); } catch { }
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] player RT prep threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                gd.SetRenderTargets(prev);
            }
            }
            finally
            {
                _renderDepth--;
            }
        }

        /// <summary>
        /// Ensure every on-screen NPC/animal sprite FRAME has a baked silhouette in the
        /// persistent cache (black + feet→head alpha gradient), so <see cref="DrawNpcShadow"/> /
        /// <see cref="DrawAnimalShadow"/> can composite one smooth image instead of banding.
        /// Runs during RenderingWorld (render-target swaps are safe there). Warm frames are a
        /// dictionary hit — only frames never seen before actually bake.
        /// </summary>
        private void BakeCasters(GraphicsDevice gd, GameLocation loc)
        {
            if (loc == null)
                return;
            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 3, tx1 = (vp.X + vp.Width) / 64 + 3;
            int ty0 = vp.Y / 64 - 3, ty1 = (vp.Y + vp.Height) / 64 + 3;

            RenderTargetBinding[]? prev = null;   // fetched lazily: only a cache MISS pays for it
            try
            {
                foreach (NPC npc in CharactersIn(loc))
                {
                    if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                        continue;
                    Point t = npc.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    var key = (npc.Sprite.Texture, npc.Sprite.SourceRect);
                    if (_casterBakes.ContainsKey(key))
                        continue;
                    prev ??= gd.GetRenderTargets();
                    if (BakeSprite(gd, key.Item1, key.Item2, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakes[key] = (rt, feet);
                }
                foreach (FarmAnimal a in AnimalsIn(loc))
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    Point t = a.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    var key = (a.Sprite.Texture, a.Sprite.SourceRect);
                    if (_casterBakes.ContainsKey(key))
                        continue;
                    prev ??= gd.GetRenderTargets();
                    if (BakeSprite(gd, key.Item1, key.Item2, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakes[key] = (rt, feet);
                }
            }
            catch (Exception ex)
            {
                if (Diag != null && !_errLogged) { _errLogged = true; Diag.Log($"[shadow] caster bake threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                if (prev != null)
                    gd.SetRenderTargets(prev);
            }
        }

        /// <summary>
        /// Bake a single sprite to a pooled slot: black silhouette at 4×, pinned bottom-centre,
        /// then a feet→head alpha ramp multiplied on. Returns false (→ banding fallback) if the
        /// sprite is larger than a slot. The caller owns the surrounding render-target swap.
        /// </summary>
        private bool BakeSprite(GraphicsDevice gd, Texture2D tex, Rectangle src, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            rt = null!;
            feetInRT = default;
            if (tex == null || src.IsEmpty)
                return false;
            float w = src.Width * 4f, h = src.Height * 4f;
            if (w > CasterRtW || h > CasterRtH - 8f)
                return false;

            rt = RentCasterRT(gd);
            var pos = new Vector2((CasterRtW - w) / 2f, CasterRtH - h - 8f);
            feetInRT = new Vector2(CasterRtW / 2f, CasterRtH - 8f);
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);
                _rtBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                _rtBatch.Draw(tex, pos, src, Color.Black, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                _rtBatch.End();

                // Fade only the sprite's vertical extent (full at the feet, faint at the head).
                _rtBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _rtBatch.Draw(_gradTex!, new Rectangle(0, (int)pos.Y, CasterRtW, (int)h), Color.White);
                _rtBatch.End();
                return true;
            }
            catch
            {
                try { _rtBatch!.End(); } catch { }
                return false;
            }
        }

        /// <summary>Lease the next pooled caster target for this frame (grows the pool on demand).</summary>
        private RenderTarget2D RentCasterRT(GraphicsDevice gd)
        {
            if (_casterUsed < _casterPool.Count)
                return _casterPool[_casterUsed++];
            var rt = new RenderTarget2D(gd, CasterRtW, CasterRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _casterPool.Add(rt);
            _casterUsed++;
            return rt;
        }

        /// <summary>A 64×64 soft radial disc (white, radial alpha) for ambient contact pools.</summary>
        private static Texture2D BuildBlob(GraphicsDevice gd)
        {
            const int N = 64;
            var tex = new Texture2D(gd, N, N);
            var data = new Color[N * N];
            float r = N / 2f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    float a = MathHelper.Clamp(1f - dist, 0f, 1f);
                    a *= a;   // soft falloff toward the rim
                    data[y * N + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(a * 255f));
                }
            }
            tex.SetData(data);
            return tex;
        }

        /// <summary>1×H alpha ramp: 1.0 at the bottom (feet) fading to <paramref name="headFade"/> at the top (far tip).</summary>
        private static Texture2D BuildGradient(GraphicsDevice gd, float headFade = HeadFade)
        {
            var tex = new Texture2D(gd, 1, PlayerRtH);
            var data = new Color[PlayerRtH];
            for (int y = 0; y < PlayerRtH; y++)
            {
                float tBottom = (float)y / (PlayerRtH - 1);      // 0 at top, 1 at bottom
                // Non-linear: stays dark near the feet, fades toward the far tip.
                float a = headFade + (1f - headFade) * (float)Math.Pow(tBottom, 1.8);
                data[y] = new Color(255, 255, 255, (int)(a * 255f));
            }
            tex.SetData(data);
            return tex;
        }

        // Discs of offset taps → cheap soft edge. Weighted so overlapping translucent copies
        // reach the target opacity at the core while feathering the rim. The player (one RT
        // draw) can afford 9 taps; NPC bands use the lighter 5 to keep the draw count sane.
        private static readonly Vector2[] Taps9 =
        {
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
            new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f),
        };
        private static readonly Vector2[] Taps5 =
        {
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        };

        private static void DrawSoft(SpriteBatch b, Vector2[] taps, Texture2D tex, Rectangle? src, Vector2 pos,
            Color baseColor, float alpha, float rot, Vector2 origin, Vector2 scale, float depth,
            SpriteEffects effects, float blur)
        {
            // No blur → one draw at full alpha (the tap disc would just stack N identical
            // copies on the same pixel, costing N× the draw calls for nothing).
            if (blur <= 0f)
            {
                b.Draw(tex, pos, src, baseColor * MathHelper.Clamp(alpha, 0f, 1f), rot, origin, scale, effects, depth);
                return;
            }

            // Per-tap alpha so 1-(1-a)^N ≈ target alpha at the fully-covered core.
            float a = 1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), 1f / taps.Length);
            Color c = baseColor * a;
            foreach (Vector2 t in taps)
                b.Draw(tex, pos + t * blur, src, c, rot, origin, scale, effects, depth);
        }

        /// <summary>Number of horizontal bands used to fake the NPC opacity gradient.</summary>
        private const int NpcBands = 7;

        /// <summary>
        /// Draw a single-texture sprite as a shadow with a feet→head opacity gradient, by
        /// slicing it into horizontal bands (each drawn about the shared feet anchor so they
        /// stay aligned under rotation + stretch) and fading each band's alpha toward the tip.
        /// </summary>
        private void DrawBandedGradient(SpriteBatch b, Texture2D tex, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, Vector2 scale, float depth, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None)
        {
            // Band count set by the sprite's SOURCE height (it's drawn ~4× on screen, so a short
            // stump at height/6 showed coarse steps). Finer division → the per-band alpha gradient
            // reads as a smooth ramp, not layers. Capped so tall sprites don't explode the draw count.
            int bands = (int)MathHelper.Clamp(src.Height / 2f, 12f, 28f);
            for (int i = 0; i < bands; i++)
            {
                int y0 = src.Height * i / bands;
                int y1 = src.Height * (i + 1) / bands;
                var band = new Rectangle(src.X, src.Y + y0, src.Width, y1 - y0);
                // Origin so the (virtual) full-sprite ground-anchor row still maps to the feet position.
                var origin = new Vector2(baseOrigin.X, baseOrigin.Y - y0);
                float tBottom = (i + 0.5f) / bands;              // 0 at the head band, 1 at the feet band
                float ga = headFade + (1f - headFade) * (float)Math.Pow(tBottom, 1.8);
                DrawSoft(b, Taps5, tex, band, feet, Color.Black, alpha * ga, rot, origin, scale, depth,
                    effects, blur);
            }
        }

        /// <summary>
        /// How far under the caster (in sort depth) the shadow sits. The farmer draws many
        /// sub-layers spanning a small depth range, so this must clear that whole range to keep
        /// the shadow strictly BEHIND the sprite (else it shows over opaque body pixels).
        /// </summary>
        private const float ShadowDepthBias = 1.2e-3f;

        // NOTE: CHARACTER cast shadows are no longer suppressed over water. Standing ankle deep
        // in a tide pool, walking a plank bridge, a gull crossing the surf — a shadow belongs in
        // all of them, and the old "open water" test could only ever guess which case it was
        // looking at. Swimming casters are still skipped at each call site via swimming.Value.
        //
        // Baked PROP shadows still use this: the screen-space mirror already reflects a mooring
        // post, so pooling its ground shadow on the same water reads as a ghost double.
        private static bool OnWater(GameLocation loc, Point tile)
        {
            try
            {
                // Walkable shallow pools (the island dig site's tide pools) carry WaterSource, not
                // Water: you stand IN them, ankle deep, so a shadow belongs on the pool floor.
                // Labelling them as water is right for the ripple pass and wrong here.
                if (loc.doesTileHaveProperty(tile.X, tile.Y, "Water", "Back") == null
                    && loc.doesTileHaveProperty(tile.X, tile.Y, "WaterSource", "Back") != null)
                    return false;
                var surf = SurfaceMap.For(loc);
                if (surf != null)
                    return surf.IsWater(tile.X, tile.Y);
                return loc.isWaterTile(tile.X, tile.Y)
                    && !loc.hasTileAt(tile.X, tile.Y, "Buildings");
            }
            catch { return false; }
        }


        /// <summary>Sun (or moon, after dark) angle → shadow lean (radians), length stretch,
        /// and base opacity. The moon crosses the sky over the night like the sun does over
        /// the day; its shadows are much fainter and scale with the lunar phase.</summary>
        private static void ComputeSun(out float rot, out float stretch, out float alpha)
        {
            int t = Game1.timeOfDay;
            int trulyDark = TrulyDark();
            if (t >= trulyDark)
            {
                // MOON: track its transit from true dark to 02:00 (day's end), same geometry
                // as the sun. Faint, phase-scaled shadows — full moon in winter is clearest.
                int mins = (t / 100) * 60 + t % 100;
                int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
                float dm = MathHelper.Clamp((mins - m1) / (float)Math.Max(1, 1560 - m1), 0f, 1f);
                float dd = dm * 2f - 1f;
                rot = 1.15f * dd;
                stretch = MathHelper.Lerp(0.3f, 1.1f, Math.Abs(dd));
                alpha = 0.9f * 0.35f * MoonStrength();
                return;
            }
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float d = MathHelper.Clamp((t - 1200) / 600f, -1f, 1f);
            // Lean more sideways (was 0.8) so the shadow lies to the side of the body instead of
            // straight up over it — reduces the "shadow on the sprite" overlap while staying
            // upright (not the rejected upside-down flip).
            rot = 1.15f * d;                                     // <0 morning lean-left, >0 evening lean-right
            stretch = MathHelper.Lerp(0.3f, 1.2f, Math.Abs(d));  // stretched LONG when the sun is low
            alpha = 0.9f * TimeFade();                           // opacity at the feet (× strength; fades toward the tip)
        }

        /// <summary>Ease the shadow out toward dusk so it doesn't pop. Shadows stay at FULL
        /// strength until 40 minutes before the game's seasonal truly-dark time, then fade —
        /// a slow ramp across the whole evening left them invisible while the sun was still
        /// clearly up. No dawn ramp — the day starts at 06:00 with the player active.</summary>
        private static float TimeFade()
        {
            int t = Game1.timeOfDay;
            int mins = (t / 100) * 60 + (t % 100);
            int trulyDark = TrulyDark();
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            if (mins >= m1)
                return 0f;
            return MathHelper.Clamp((m1 - mins) / 40f, 0f, 1f);
        }
    }
}
