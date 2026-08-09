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
            // "The reflection needs the player" requires water actually on screen, not just the
            // setting: the only reader of PlayerColor early-outs without water, so a farmhouse
            // frame that baked it anyway was doing a second FarmerRenderer draw for nobody. Both
            // sides read the same flag (last compose's answer), so they cannot disagree.
            bool reflectionNeedsPlayer = config.Enabled && config.WaterReflection && WaterOnScreen
                && StardewModdingAPI.Context.IsWorldReady && Game1.currentLocation != null;
            if (!shadowsOn && !reflectionNeedsPlayer)
            {
                _playerReady = false;
                _playerMaskFresh = false;
                _playerColorFresh = false;
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
            // CHARACTER bakes are upright silhouettes keyed by (texture, frame): valid forever.
            // OBJECT bakes have the sun lean baked in as a shear, so they go stale as the sun
            // moves — that is now handled per sprite, by error, and not by throwing the lot away.
            // Both caches EVICT the coldest entries when they outgrow their cap. They used to
            // Clear(), and a map that simply has more distinct sprites than the cap then re-baked
            // its whole screen every frame: the cache became a cost instead of a saving on exactly
            // the mod-heavy installs it exists for.
            EvictColdCasterBakes();
            EvictColdObjectBakes();

            bool objectsOn = shadowsOn && SunCasts() && config.DirectionalShadowObjects;
            float sunRotation = 0f, sunStretch = 0f;
            if (objectsOn)
            {
                ComputeSun(out sunRotation, out sunStretch, out _);
                sunStretch *= Math.Max(0.1f, config.DirectionalShadowLength);
            }
            // A location change no longer clears: the key is (texture, frame, flip), which is not
            // tied to a map, so warping back and forth used to re-bake everything both ways for
            // nothing. It still triggers ONE full enumeration, so a new screen arrives baked
            // instead of spending a frame on banded stand-ins.
            bool locationChanged = Game1.currentLocation != _objectBakeLocation;
            _objectBakeLocation = Game1.currentLocation;

            // Over cap AFTER eviction means the hot set alone does not fit: a foliage pack that
            // multiplies the distinct (texture, frame, flip) bakes is the suspected cause of
            // "directional shadows on trees and bushes are unplayably slow with Simple Foliage,
            // fine with the setting off". Say so once per location, with both numbers.
            if (_bakedObjectCache.Count > ObjectBakeCap && DiagnosticMonitor != null
                && Game1.currentLocation is { } capLoc && capLoc != _objectCapLoggedLocation)
            {
                _objectCapLoggedLocation = capLoc;
                DiagnosticMonitor.Log($"[shadow] object bake cache over cap at {capLoc.NameOrUniqueName}: "
                       + $"{_bakedObjectCache.Count} distinct sprites still hot (cap {ObjectBakeCap}, "
                       + $"{_objectRenderTargetPool.Count} slots allocated) — more sprites are on screen at once "
                       + "than the cache can hold, so some object shadows re-bake as they scroll.", LogLevel.Debug);
            }

            // Bake NPC + animal silhouettes (single-sprite casters) — cheap when warm: cache
            // hits only, no RT switch. Runs every frame so new animation frames bake instantly.
            // Shadow-only: the reflection stamps NPCs from their live sprite, not from a bake.
            if (shadowsOn && Game1.currentLocation is { } casterLocation)
                BakeCasters(graphicsDevice, casterLocation);

            // Bake OBJECT silhouettes (trees/bushes/clumps/furniture/craftables/…). The FULL
            // enumeration — every on-screen tile, every entity list, the tile-art classifier —
            // runs on arrival in a location and never again. On every other frame it used to run
            // anyway, in bake mode, and on a warm frame that is a second complete walk of the
            // scene per frame whose every lookup answers "already baked". The draw pass now
            // reports what it found missing OR stale (see EmitObj), and a warm bake pass does
            // exactly that list, which on a still screen under a still sun is nothing. A
            // brand-new sprite pays one frame of the banded stand-in — at the screen edge it is
            // scrolling in over, not the 15 ticks of it that got the old heartbeat attempt
            // reverted.
            if (objectsOn && Game1.currentLocation is { } objectLocation)
            {
                _isBakingObjects = true;
                _objectGraphicsDevice = graphicsDevice;
                RenderTargetBinding[] objPrev = graphicsDevice.GetRenderTargets();
                try
                {
                    if (locationChanged || _bakedObjectCache.Count == 0)
                        DrawObjectShadows(_renderTargetSpriteBatch, objectLocation, sunRotation, sunStretch, 0f, 0f);
                    else
                        BakeQueuedObjectSprites(graphicsDevice);
                }
                catch (Exception ex) { if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] obj bake threw: {ex}", LogLevel.Warn); } }
                finally { graphicsDevice.SetRenderTargets(objPrev); _isBakingObjects = false; }
            }
            // Either path leaves the queue spent, including anything the refresh budget did not
            // reach. Nothing is lost by that: an entry the sun has moved off is still stale next
            // frame, so the draw pass simply asks again, and dropping the list keeps a request
            // from outliving the shear it was recorded under.
            _objectBakeQueue.Clear();

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
                _playerColorFresh = false;
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
            // the body frame (Fashion Sense hair sway etc.) fresh — but ONLY when such a mod is
            // actually installed. It used to run unconditionally, which meant a player standing
            // perfectly still was re-baked 7.5 times a second on every install, for layers that
            // in a vanilla-appearance game do not exist. This bake measured as the single most
            // expensive part of the mod, ahead of drawing every shadow on screen.
            var sig = (who.FarmerSprite.CurrentFrame, (int)who.FacingDirection, src);
            bool accessoryRefreshDue = PlayerAccessoriesAnimate && Game1.ticks % 8 == 0;
            if (_playerMaskFresh && sig == _playerBakeSignature && !accessoryRefreshDue
                && (!reflectionNeedsPlayer || _playerColorFresh))
            {
                _playerReady = !swim && !IsSeated(who);
                PlayerMask = _playerRenderTarget;
                PlayerColor = _playerColorFresh ? _playerColorRenderTarget : null;
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
                // Skipped when no water is on screen — its one reader is the reflection, which
                // early-outs on the same flag, so the second FarmerRenderer draw was pure waste
                // on every waterless frame. _playerColorFresh is what makes the skip safe: the
                // moment water scrolls back in, the stale-colour pose fails the reuse gate above
                // and this bake runs again, even though the mask half is still current.
                if (reflectionNeedsPlayer)
                {
                    _playerColorRenderTarget ??= new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                        SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    graphicsDevice.SetRenderTarget(_playerColorRenderTarget);
                    graphicsDevice.Clear(Color.Transparent);
                    _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    who.FarmerRenderer.draw(_renderTargetSpriteBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                        src, pos, Vector2.Zero, 0f, who.FacingDirection, Color.White, 0f, 1f, who);
                    _renderTargetSpriteBatch.End();
                }
                _playerColorFresh = reflectionNeedsPlayer;

                _playerMaskFresh = true;
                _playerReady = !swim && !IsSeated(who);
                PlayerMask = _playerRenderTarget;
                PlayerColor = _playerColorFresh ? _playerColorRenderTarget : null;
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
                    // Stamping the tick on a HIT is what keeps eviction honest: this loop already
                    // walks exactly the casters on screen, so "seen here this frame" is the same
                    // question as "is this bake still wanted".
                    if (_casterBakeCache.TryGetValue(key, out SpriteBake? warm))
                    {
                        warm.LastUsedTick = Game1.ticks;
                        continue;
                    }
                    prev ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Item1, key.Item2, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakeCache[key] = new SpriteBake { Rt = rt, FeetInRt = feet, LastUsedTick = Game1.ticks };
                }
                foreach (FarmAnimal a in AnimalsIn(location))
                {
                    if (a?.Sprite?.Texture == null)
                        continue;
                    Point t = a.TilePoint;
                    if (t.X < tx0 || t.X > tx1 || t.Y < ty0 || t.Y > ty1)
                        continue;
                    var key = (a.Sprite.Texture, a.Sprite.SourceRect);
                    if (_casterBakeCache.TryGetValue(key, out SpriteBake? warm))
                    {
                        warm.LastUsedTick = Game1.ticks;
                        continue;
                    }
                    prev ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Item1, key.Item2, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakeCache[key] = new SpriteBake { Rt = rt, FeetInRt = feet, LastUsedTick = Game1.ticks };
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
                // Hand the slot back. Returning false means the caller never stores this target,
                // so without this the lease is lost: the pool still owns the memory but nothing
                // can ever reuse it, and a sprite that fails to bake every frame leaks a slot
                // every frame. Recycling used to happen wholesale when the cache cleared itself,
                // and that is exactly what was removed to stop the cache thrashing.
                _casterFreeTargets.Add(rt);
                rt = null!;
                return false;
            }
        }

        /// <summary>Lease a caster slot: an evicted one if there is one, otherwise a new allocation.</summary>
        private RenderTarget2D RentCasterRT(GraphicsDevice graphicsDevice)
        {
            if (_casterFreeTargets.Count > 0)
            {
                RenderTarget2D reused = _casterFreeTargets[^1];
                _casterFreeTargets.RemoveAt(_casterFreeTargets.Count - 1);
                return reused;
            }
            var rt = new RenderTarget2D(graphicsDevice, CasterRtW, CasterRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _casterRenderTargetPool.Add(rt);
            return rt;
        }

        /// <summary>
        /// Drop the least recently drawn character bakes once the cache outgrows its cap, handing
        /// their targets back to be leased again.
        /// </summary>
        private void EvictColdCasterBakes()
        {
            if (_casterBakeCache.Count <= CasterBakeCap)
                return;
            _casterEvictScratch.Clear();
            int keep = (int)(CasterBakeCap * EvictHeadroom);
            bool desperate = _casterBakeCache.Count > CasterBakeCap * 2;
            int coldBefore = Game1.ticks - HotBakeTicks;
            foreach (var kv in _casterBakeCache)
            {
                if (desperate || kv.Value.LastUsedTick < coldBefore)
                    _casterEvictScratch.Add(kv.Key);
            }
            _casterEvictScratch.Sort((a, b) => _casterBakeCache[a].LastUsedTick.CompareTo(_casterBakeCache[b].LastUsedTick));
            int drop = Math.Min(_casterBakeCache.Count - keep, _casterEvictScratch.Count);
            for (int i = 0; i < drop; i++)
            {
                if (_casterBakeCache.TryGetValue(_casterEvictScratch[i], out SpriteBake? bake))
                {
                    _casterFreeTargets.Add(bake.Rt);
                    _casterBakeCache.Remove(_casterEvictScratch[i]);
                }
            }
        }

        /// <summary>
        /// The same for object bakes. Their slots are five times the size, so this is the one that
        /// decides how much VRAM the mod holds.
        /// </summary>
        private void EvictColdObjectBakes()
        {
            if (_bakedObjectCache.Count <= ObjectBakeCap)
                return;
            _objectEvictScratch.Clear();
            int keep = (int)(ObjectBakeCap * EvictHeadroom);
            bool desperate = _bakedObjectCache.Count > ObjectBakeCap * 2;
            int coldBefore = Game1.ticks - HotBakeTicks;
            foreach (var kv in _bakedObjectCache)
            {
                if (desperate || kv.Value.LastUsedTick < coldBefore)
                    _objectEvictScratch.Add(kv.Key);
            }
            _objectEvictScratch.Sort((a, b) => _bakedObjectCache[a].LastUsedTick.CompareTo(_bakedObjectCache[b].LastUsedTick));
            int drop = Math.Min(_bakedObjectCache.Count - keep, _objectEvictScratch.Count);
            for (int i = 0; i < drop; i++)
            {
                if (_bakedObjectCache.TryGetValue(_objectEvictScratch[i], out SpriteBake? bake))
                {
                    _objectFreeTargets.Add(bake.Rt);
                    _bakedObjectCache.Remove(_objectEvictScratch[i]);
                }
            }
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
            // Continuous minutes: the raw HHMM value made the angle lurch once per tick
            // (and extra hard across hour boundaries, where HHMM skips 40).
            float mins = GameClock.MinutesNow();
            int trulyDark = TrulyDark();
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            if (mins >= m1)
            {
                // MOON: track its transit from true dark to 02:00 (day's end), same geometry
                // as the sun. Faint, phase-scaled shadows — full moon in winter is clearest.
                // Ease in over the first half hour: the sun fade reaches zero AT dark, and
                // the moon used to arrive at full (phase) strength on the very same tick.
                float moonProgress = MathHelper.Clamp((mins - m1) / Math.Max(1f, 1560f - m1), 0f, 1f);
                float moonSkyOffset = moonProgress * 2f - 1f;
                rot = 1.15f * moonSkyOffset;
                stretch = MathHelper.Lerp(0.3f, 1.1f, Math.Abs(moonSkyOffset));
                alpha = 0.9f * 0.35f * MoonStrength() * MathHelper.Clamp((mins - m1) / 30f, 0f, 1f);
                return;
            }
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float sunSkyOffset = MathHelper.Clamp((mins - 720f) / 360f, -1f, 1f);
            // Lean more sideways (was 0.8) so the shadow lies to the side of the body instead of
            // straight up over it — reduces the "shadow on the sprite" overlap while staying
            // upright (not the rejected upside-down flip).
            rot = 1.15f * sunSkyOffset;                                     // <0 morning lean-left, >0 evening lean-right
            stretch = MathHelper.Lerp(0.3f, 1.2f, Math.Abs(sunSkyOffset));  // stretched LONG when the sun is low
            alpha = 0.9f * TimeFade();                           // opacity at the feet (× strength; fades toward the tip)
        }

        /// <summary>
        /// The daylight coming through a window right now: its colour and how strong it is.
        ///
        /// <para>
        /// A window is not a lamp. It is a hole with the sky behind it, so it has to change
        /// through the day and through the year - gold when the sun is low, white at noon,
        /// gold again at dusk, and after dark a faint blue rather than the same daylight it
        /// poured in at midday. That last one is why a farmhouse read as brightly lit at two
        /// in the morning: the seed was a constant, and the game's own "is this window glowing"
        /// test only asks whether the window exists, never what time it is.
        /// </para>
        ///
        /// <para>
        /// Everything that draws light arriving from outside asks this one function, so the
        /// room's ambient, the window seed and the patch on the floor can never disagree about
        /// what time of day it is.
        /// </para>
        /// </summary>
        /// <summary>The game's seasonal nightfall, in minutes since midnight.</summary>
        internal static float TrulyDarkMinutes()
        {
            int t = TrulyDark();
            return (t / 100) * 60 + t % 100;
        }

        internal static void WindowDaylight(out Vector3 colour, out float strength)
        {
            float mins = GameClock.MinutesNow();
            int trulyDark = TrulyDark();

            // The sun is ALREADY up when the player wakes - the game's own outdoor light is at
            // full daylight by 06:00 - so the climb has to be finished shortly after, not
            // starting there. Ramping from 06:00 put this at exactly zero on the stroke of six,
            // which dropped through to the after-dark branch and lit the bedroom with moonlight
            // at sunrise.
            float risen = MathHelper.Clamp((mins - 320f) / 60f, 0f, 1f);   // 05:20 -> 06:20
            float notYetDark = 1f - GameClock.RampAt(trulyDark, 60f);
            float day = Math.Min(risen, notYetDark);

            // Low sun = warm. Squared, so only the real edges of the day go golden and the
            // middle stays daylight-white instead of everything looking like a sunset.
            float lowSun = Math.Abs(MathHelper.Clamp((mins - 720f) / 360f, -1f, 1f));
            Vector3 noon = new(0.86f, 0.93f, 1.06f);
            Vector3 gold = new(1.08f, 0.86f, 0.60f);
            colour = Vector3.Lerp(noon, gold, lowSun * lowSun);

            // The year: winter's sun is low and pale all day and the light is thin; summer is
            // the opposite; autumn light is famously warm.
            (float mul, Vector3 tint) season = Game1.season switch
            {
                Season.Winter => (0.80f, new Vector3(0.93f, 0.98f, 1.10f)),
                Season.Summer => (1.12f, new Vector3(1.03f, 1.00f, 0.95f)),
                Season.Fall => (0.94f, new Vector3(1.06f, 0.98f, 0.90f)),
                _ => (1f, Vector3.One),
            };
            float weather = (Game1.isRaining || Game1.isSnowing || Game1.isLightning) ? 0.62f : 1f;
            if (weather < 1f)
                colour = Vector3.Lerp(colour, new Vector3(0.90f, 0.94f, 1.00f), 0.6f);   // flat overcast

            strength = day * season.mul * weather;
            colour *= season.tint;

            if (strength <= 0.03f)
            {
                // After dark the window is still there - it just shows a night sky. A faint
                // cool pane reads as moonlight; leaving it at zero made rooms look sealed.
                colour = new Vector3(0.52f, 0.62f, 0.95f);
                strength = 0.18f;
            }
        }

        /// <summary>Where that daylight lands on the floor: <paramref name="lean"/> is tiles
        /// sideways per tile into the room and <paramref name="reach"/> how far the patch
        /// carries. Taken from the same sun the shadows use, so a low morning sun throws a long
        /// patch across the boards in the same direction everything else is leaning.</summary>
        internal static void WindowShaft(out float lean, out float reach)
        {
            ComputeSun(out float rot, out float stretch, out float alpha);
            // Far shallower than a cast shadow's rotation. A shadow leans hard because it is
            // measured on the ground away from a standing body; a patch of daylight seen from
            // above mostly just drops into the room. At the shadow's own 0.7 the patch crossed
            // more sideways than it travelled inward, which reads as a diagonal streak laid
            // over the furniture rather than as light coming through the glass.
            lean = MathHelper.Clamp(rot * 0.30f, -0.45f, 0.45f);
            reach = alpha <= 0.01f ? 2.2f : MathHelper.Clamp(2.2f + stretch * 2.5f, 2.2f, 5f);
        }

        /// <summary>Ease the shadow out toward dusk so it doesn't pop. Shadows stay at FULL
        /// strength until 40 minutes before the game's seasonal truly-dark time, then fade —
        /// a slow ramp across the whole evening left them invisible while the sun was still
        /// clearly up. No dawn ramp — the day starts at 06:00 with the player active.</summary>
        private static float TimeFade()
        {
            float mins = GameClock.MinutesNow();
            int trulyDark = TrulyDark();
            int m1 = (trulyDark / 100) * 60 + trulyDark % 100;
            if (mins >= m1)
                return 0f;
            return MathHelper.Clamp((m1 - mins) / 40f, 0f, 1f);
        }
    }
}
