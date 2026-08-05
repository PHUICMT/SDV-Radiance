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
        public void PreparePlayer(GraphicsDevice graphicsDevice, ModConfig config)
        {
            // The water reflection draws the player from PlayerColor, so this bake has to run
            // for a reflection-only setup as well. Gated on the shadow toggle alone, switching
            // directional shadows OFF left PlayerColor holding the last pose it baked, and the
            // mirrored player stopped turning with you (reported on 1.3.3: "the reflection only
            // shows the front view and does not change"). The caster/object bakes below stay
            // shadow-only work, so a reflection-only frame pays for one small render target.
            bool shadowsOn = ShouldCast(config);
            bool reflectionNeedsPlayer = config.Enabled && config.WaterReflection
                && StardewModdingAPI.Context.IsWorldReady && Game1.currentLocation != null;
            if (!shadowsOn && !reflectionNeedsPlayer)
            {
                _playerReady = false;
                _playerMaskFresh = false;
                PlayerMask = null;
                PlayerColor = null;
                return;
            }
            if (_renderDepth > 0)
            {
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log("[shadow] PreparePlayer re-entered — skipping nested call", LogLevel.Warn); }
                return;
            }
            _renderDepth++;
            try
            {
            _renderTargetSpriteBatch ??= new SpriteBatch(graphicsDevice);
            _gradientTexture ??= BuildGradient(graphicsDevice);
            _propGradientTexture ??= BuildGradient(graphicsDevice, 0f);
            _contactBlobTexture ??= BuildBlob(graphicsDevice);

            // ---- Persistent bake caches (the old clear-everything-every-frame here cost
            // 50-150 render-target switches per frame — the single biggest stutter source) ----
            // CHARACTER bakes are upright silhouettes keyed by (texture, frame): valid forever,
            // only capped. OBJECT bakes have the sun lean baked in as a shear: valid until the
            // sun angle ticks (every 10 game minutes) or the location changes.
            if (_casterBakeCache.Count > 192)
            {
                _casterBakeCache.Clear();
                _casterSlotsUsed = 0;
            }

            bool objectsOn = shadowsOn && SunCasts() && config.DirectionalShadowObjects;
            float sunRotation = 0f, sunStretch = 0f;
            if (objectsOn)
            {
                ComputeSun(out sunRotation, out sunStretch, out _);
                sunStretch *= Math.Max(0.1f, config.DirectionalShadowLength);
            }
            long objectShearCacheKey = objectsOn
                ? ((long)Math.Round(sunRotation * 512f) << 20) ^ (long)Math.Round(sunStretch * 512f)
                : long.MinValue;
            bool objectBakeCacheCapExceeded = _bakedObjectCache.Count > 128;   // VRAM cap: slots are 400×456 RTs (~0.7 MB each)
            bool isObjectBakeCacheInvalid = objectShearCacheKey != _objectShearKey
                                || Game1.currentLocation != _objectBakeLocation
                                || objectBakeCacheCapExceeded;
            // The cap is a full Clear(), so a location that stays OVER it re-bakes from scratch every
            // frame and pays back the 50-150 render-target switches the cache exists to avoid. A
            // custom foliage pack multiplies the distinct (texture, frame, flip) bakes, which is the
            // suspected cause of "directional shadows on trees and bushes are unplayably slow with
            // Simple Foliage, fine with the setting off". Say so once per location, with the count:
            // one number in a log decides whether that is what is happening before anything is
            // restructured to evict instead of clear.
            if (objectBakeCacheCapExceeded && DiagnosticMonitor != null && Game1.currentLocation is { } capLoc && capLoc != _objectCapLoggedLocation)
            {
                _objectCapLoggedLocation = capLoc;
                DiagnosticMonitor.Log($"[shadow] object bake cache over cap at {capLoc.NameOrUniqueName}: "
                       + $"{_bakedObjectCache.Count} distinct sprites this frame (cap 128) — the cache is clearing every "
                       + "frame here, so object shadows are re-baking from scratch.", LogLevel.Debug);
            }
            if (isObjectBakeCacheInvalid)
            {
                _bakedObjectCache.Clear();
                _objectSlotsUsed = 0;
                _objectShearKey = objectShearCacheKey;
                _objectBakeLocation = Game1.currentLocation;
            }

            // Bake NPC + animal silhouettes (single-sprite casters) — cheap when warm: cache
            // hits only, no RT switch. Runs every frame so new animation frames bake instantly.
            // Shadow-only: the reflection stamps NPCs from their live sprite, not from a bake.
            if (shadowsOn && Game1.currentLocation is { } casterLocation)
                BakeCasters(graphicsDevice, casterLocation);

            // Bake OBJECT silhouettes (trees/bushes/clumps/furniture/craftables/…) by running the
            // object enumeration in BAKE mode. Composited later in DrawObjectShadows. Runs every
            // frame so sprites entering the view bake instantly (a 15-tick heartbeat was tried —
            // its cache-miss frames drew the banded fallback, reading as line artifacts) — but a
            // WARM frame is dictionary hits only, no RT switches, which is where the cost was.
            if (objectsOn && Game1.currentLocation is { } objectLocation)
            {
                _isBakingObjects = true;
                _objectGraphicsDevice = graphicsDevice;
                RenderTargetBinding[] objPrev = graphicsDevice.GetRenderTargets();
                try { DrawObjectShadows(_renderTargetSpriteBatch, objectLocation, sunRotation, sunStretch, 0f, 0f); }
                catch (Exception ex) { if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] obj bake threw: {ex}", LogLevel.Warn); } }
                finally { graphicsDevice.SetRenderTargets(objPrev); _isBakingObjects = false; }
            }

            // Sitting still casts (the bake captures the current SEATED animation frame, so the
            // silhouette matches the pose); horseback skips — the horse's own shadow covers the
            // rider. SWIMMING keeps the bake but drops _playerReady: the shadow consumers gate
            // on _playerReady (a swimmer casts no shadow), while the water shader's exclusion
            // gate reads PlayerMask — without it the ripple displacement warped the swimmer's
            // own pixels (the bathhouse "wavy body").
            Farmer who = Game1.player;
            bool swim = who != null && who.swimming.Value;
            if (who == null || who.currentLocation != Game1.currentLocation || who.isRidingHorse())
            {
                _playerReady = false;
                _playerMaskFresh = false;
                PlayerMask = null;
                PlayerColor = null;
                return;
            }

            // PreserveContents is REQUIRED for every persistent bake target: the default
            // DiscardContents only guarantees the pixels until the next target swap/present,
            // which was fine when everything re-baked per frame — cached across frames, the
            // content decayed into garbage (grid-line artifacts all over the map).
            _playerRenderTarget ??= new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

            Rectangle src = who.FarmerSprite.SourceRect;

            // Same pose as the last bake → the RT is still correct, skip the 3-batch redraw.
            // The every-8-frames refresh keeps accessory layers that animate independently of
            // the body frame (Fashion Sense hair sway etc.) fresh without paying every frame.
            var sig = (who.FarmerSprite.CurrentFrame, (int)who.FacingDirection, src);
            if (_playerMaskFresh && sig == _playerBakeSignature && Game1.ticks % 8 != 0)
            {
                _playerReady = !swim && !IsSeated(who);
                PlayerMask = _playerRenderTarget;
                PlayerColor = _playerColorRenderTarget;
                return;
            }
            _playerBakeSignature = sig;

            float w = src.Width * 4f, h = src.Height * 4f;
            Vector2 pos = new Vector2((PlayerRtW - w) / 2f, PlayerRtH - h - 8f);
            _playerFeetInRenderTarget = new Vector2(PlayerRtW / 2f, PlayerRtH - 8f);

            RenderTargetBinding[] prev = graphicsDevice.GetRenderTargets();
            try
            {
                graphicsDevice.SetRenderTarget(_playerRenderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_renderTargetSpriteBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                    src, pos, Vector2.Zero, 0f, who.FacingDirection, Color.Black, 0f, 1f, who);
                _renderTargetSpriteBatch.End();

                // Scrub COLOUR out of the bake (RGB→0, alpha kept): appearance mods (Fashion
                // Sense etc.) draw through their own patches and ignore the black tint above,
                // so without this a white dress cast a white shadow. Works for ANY current or
                // future appearance mod — whatever got drawn, only its shape survives.
                _gradientTexture ??= BuildGradient(graphicsDevice);
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, ZeroColor, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_gradientTexture, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
                _renderTargetSpriteBatch.End();

                // Fade the silhouette's opacity from the feet (full) to the head/far tip (faint),
                // so the stretched far end reads as a soft penumbra rather than a hard clone.
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_gradientTexture, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
                _renderTargetSpriteBatch.End();

                // FULL-COLOUR twin of the bake (no scrub, no head fade) for the water
                // reflection RT: same pose, same feet anchor, whatever appearance mods drew.
                _playerColorRenderTarget ??= new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                graphicsDevice.SetRenderTarget(_playerColorRenderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_renderTargetSpriteBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                    src, pos, Vector2.Zero, 0f, who.FacingDirection, Color.White, 0f, 1f, who);
                _renderTargetSpriteBatch.End();

                _playerMaskFresh = true;
                _playerReady = !swim && !IsSeated(who);
                PlayerMask = _playerRenderTarget;
                PlayerColor = _playerColorRenderTarget;
            }
            catch (Exception ex)
            {
                try { _renderTargetSpriteBatch.End(); } catch { }
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] player RT prep threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                graphicsDevice.SetRenderTargets(prev);
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
        private void BakeCasters(GraphicsDevice graphicsDevice, GameLocation location)
        {
            if (location == null)
                return;
            var viewport = Game1.viewport;
            int tx0 = viewport.X / 64 - 3, tx1 = (viewport.X + viewport.Width) / 64 + 3;
            int ty0 = viewport.Y / 64 - 3, ty1 = (viewport.Y + viewport.Height) / 64 + 3;

            RenderTargetBinding[]? prev = null;   // fetched lazily: only a cache MISS pays for it
            try
            {
                foreach (NPC npc in CharactersIn(location))
                {
                    if (npc == null || npc.IsInvisible || ShadowHiddenFor(npc) || npc.swimming.Value || npc.Sprite?.Texture == null)
                        continue;
                    Point t = npc.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    var key = (npc.Sprite.Texture, npc.Sprite.SourceRect);
                    if (_casterBakeCache.ContainsKey(key))
                        continue;
                    prev ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Item1, key.Item2, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakeCache[key] = (rt, feet);
                }
                foreach (FarmAnimal a in AnimalsIn(location))
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    Point t = a.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    var key = (a.Sprite.Texture, a.Sprite.SourceRect);
                    if (_casterBakeCache.ContainsKey(key))
                        continue;
                    prev ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Item1, key.Item2, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakeCache[key] = (rt, feet);
                }
            }
            catch (Exception ex)
            {
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] caster bake threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                if (prev != null)
                    graphicsDevice.SetRenderTargets(prev);
            }
        }

        /// <summary>
        /// Bake a single sprite to a pooled slot: black silhouette at 4×, pinned bottom-centre,
        /// then a feet→head alpha ramp multiplied on. Returns false (→ banding fallback) if the
        /// sprite is larger than a slot. The caller owns the surrounding render-target swap.
        /// </summary>
        private bool BakeSprite(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle src, out RenderTarget2D rt, out Vector2 feetInRT)
        {
            rt = null!;
            feetInRT = default;
            if (texture == null || src.IsEmpty)
                return false;
            float w = src.Width * 4f, h = src.Height * 4f;
            if (w > CasterRtW || h > CasterRtH - 8f)
                return false;

            rt = RentCasterRT(graphicsDevice);
            var pos = new Vector2((CasterRtW - w) / 2f, CasterRtH - h - 8f);
            feetInRT = new Vector2(CasterRtW / 2f, CasterRtH - 8f);
            try
            {
                graphicsDevice.SetRenderTarget(rt);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(texture, pos, src, Color.Black, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                _renderTargetSpriteBatch.End();

                // Fade only the sprite's vertical extent (full at the feet, faint at the head).
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_gradientTexture!, new Rectangle(0, (int)pos.Y, CasterRtW, (int)h), Color.White);
                _renderTargetSpriteBatch.End();
                return true;
            }
            catch
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                return false;
            }
        }

        /// <summary>Lease the next pooled caster target for this frame (grows the pool on demand).</summary>
        private RenderTarget2D RentCasterRT(GraphicsDevice graphicsDevice)
        {
            if (_casterSlotsUsed < _casterRenderTargetPool.Count)
                return _casterRenderTargetPool[_casterSlotsUsed++];
            var rt = new RenderTarget2D(graphicsDevice, CasterRtW, CasterRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _casterRenderTargetPool.Add(rt);
            _casterSlotsUsed++;
            return rt;
        }

        /// <summary>A 64×64 soft radial disc (white, radial alpha) for ambient contact pools.</summary>
        private static Texture2D BuildBlob(GraphicsDevice graphicsDevice)
        {
            const int N = 64;
            var texture = new Texture2D(graphicsDevice, N, N);
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
            texture.SetData(data);
            return texture;
        }

        /// <summary>1×H alpha ramp: 1.0 at the bottom (feet) fading to <paramref name="headFade"/> at the top (far tip).</summary>
        private static Texture2D BuildGradient(GraphicsDevice graphicsDevice, float headFade = HeadFade)
        {
            var texture = new Texture2D(graphicsDevice, 1, PlayerRtH);
            var data = new Color[PlayerRtH];
            for (int y = 0; y < PlayerRtH; y++)
            {
                float tBottom = (float)y / (PlayerRtH - 1);      // 0 at top, 1 at bottom
                // Non-linear: stays dark near the feet, fades toward the far tip.
                float a = headFade + (1f - headFade) * (float)Math.Pow(tBottom, 1.8);
                data[y] = new Color(255, 255, 255, (int)(a * 255f));
            }
            texture.SetData(data);
            return texture;
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

        private static void DrawSoft(SpriteBatch spriteBatch, Vector2[] taps, Texture2D texture, Rectangle? src, Vector2 pos,
            Color baseColor, float alpha, float rot, Vector2 origin, Vector2 scale, float depth,
            SpriteEffects effects, float blur)
        {
            // No blur → one draw at full alpha (the tap disc would just stack N identical
            // copies on the same pixel, costing N× the draw calls for nothing).
            if (blur <= 0f)
            {
                spriteBatch.Draw(texture, pos, src, baseColor * MathHelper.Clamp(alpha, 0f, 1f), rot, origin, scale, effects, depth);
                return;
            }

            // Per-tap alpha so 1-(1-a)^N ≈ target alpha at the fully-covered core.
            float a = 1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), 1f / taps.Length);
            Color c = baseColor * a;
            foreach (Vector2 t in taps)
                spriteBatch.Draw(texture, pos + t * blur, src, c, rot, origin, scale, effects, depth);
        }

        /// <summary>Number of horizontal bands used to fake the NPC opacity gradient.</summary>
        private const int NpcBands = 7;

        /// <summary>
        /// Draw a single-texture sprite as a shadow with a feet→head opacity gradient, by
        /// slicing it into horizontal bands (each drawn about the shared feet anchor so they
        /// stay aligned under rotation + stretch) and fading each band's alpha toward the tip.
        /// </summary>
        private void DrawBandedGradient(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, Vector2 scale, float depth, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None)
        {
            // The ramp runs from the FEET row up, not from the sprite's bottom edge. On a sprite
            // the game has stretched, the character occupies the upper half and the rest is water
            // or tackle: measuring from the bottom edge handed the person the pale end of the ramp
            // and spent the dark end on empty pixels, so the shadow came out barely visible. The
            // feet row is baseOrigin.Y, which is the bottom edge for every ordinary sprite, so
            // nothing changes for them.
            float feetRow = Math.Max(1f, baseOrigin.Y);
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
                // 0 at the head band, 1 at the feet band. Rows below the feet (a stretched sprite's
                // water half) clamp to 1 rather than running past it.
                float tBottom = MathHelper.Clamp(src.Height * (i + 0.5f) / bands / feetRow, 0f, 1f);
                float ga = headFade + (1f - headFade) * (float)Math.Pow(tBottom, 1.8);
                DrawSoft(spriteBatch, Taps5, texture, band, feet, Color.Black, alpha * ga, rot, origin, scale, depth,
                    effects, blur);
            }
        }

        /// <summary>
        /// How far under the caster (in sort depth) the shadow sits. The farmer draws many
        /// sub-layers spanning a small depth range, so this must clear that whole range to keep
        /// the shadow strictly BEHIND the sprite (else it shows over opaque body pixels).
        /// </summary>
        private const float ShadowDepthBias = 1.2e-3f;

        /// <summary>RETIRED 2026-08-05, kept as the single switch for the rule. Casters on
        /// OPEN water (tile + 4 neighbours all water) used to lose their sun/lamp shadow so
        /// nothing lay "on" the surface — but a body standing in shallow water casts a shadow
        /// across the surface in reality, the skip made a wading player's shadow vanish
        /// outright, and crossing the open-water boundary popped it. Swimming and riding
        /// keep their own gates at the call sites.</summary>
        private static bool OnOpenWater(GameLocation location, Point t) => false;

        private static bool OnWater(GameLocation location, Point tile)
        {
            try
            {
                // The surface grid distinguishes open water from pier/bridge DECKS over water, so
                // it is the robust answer. Fall back to the isWaterTile + no-Buildings-tile
                // heuristic (which approximates the same deck check) if the map isn't ready.
                var surf = SurfaceMap.For(location);
                if (surf != null)
                    return surf.IsWater(tile.X, tile.Y);
                return location.isWaterTile(tile.X, tile.Y)
                    && !location.hasTileAt(tile.X, tile.Y, "Buildings");
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
                float moonProgress = MathHelper.Clamp((mins - m1) / (float)Math.Max(1, 1560 - m1), 0f, 1f);
                float moonSkyOffset = moonProgress * 2f - 1f;
                rot = 1.15f * moonSkyOffset;
                stretch = MathHelper.Lerp(0.3f, 1.1f, Math.Abs(moonSkyOffset));
                alpha = 0.9f * 0.35f * MoonStrength();
                return;
            }
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float sunSkyOffset = MathHelper.Clamp((t - 1200) / 600f, -1f, 1f);
            // Lean more sideways (was 0.8) so the shadow lies to the side of the body instead of
            // straight up over it — reduces the "shadow on the sprite" overlap while staying
            // upright (not the rejected upside-down flip).
            rot = 1.15f * sunSkyOffset;                                     // <0 morning lean-left, >0 evening lean-right
            stretch = MathHelper.Lerp(0.3f, 1.2f, Math.Abs(sunSkyOffset));  // stretched LONG when the sun is low
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
