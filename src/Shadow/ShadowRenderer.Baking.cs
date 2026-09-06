using System;
using System.Collections.Generic;
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
            long gateStep = RenderPipeline.ChainStepBegin();
            bool shadowsOn = ShouldCast(config);
            _diagInstance = this;
            // "The reflection needs the player" requires water actually on screen, not just the
            // setting: the only reader of PlayerColor early-outs without water, so a farmhouse
            // frame that baked it anyway was doing a second FarmerRenderer draw for nobody. Both
            // sides read the same flag (last compose's answer), so they cannot disagree.
            // Wet puddles mirror the player anywhere outdoors while the ground can pool; the
            // water-on-screen gate was written when water was the only thing a mirror could
            // land on, and it left every puddle on a riverless screen standing empty.
            bool wetPuddlesNeedPlayer = config.Enabled && config.WetWorldEnabled
                && !RenderPipeline.DynamicReflectionsPresent && config.WetWorldPuddles > 0.01f
                && RenderPipeline.PuddleAmountNow > 0.05f && (Game1.currentLocation?.IsOutdoors ?? false);
            bool reflectionNeedsPlayer = ((config.Enabled && config.WaterReflection && WaterOnScreen) || wetPuddlesNeedPlayer)
                && StardewModdingAPI.Context.IsWorldReady && Game1.currentLocation != null;
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeGates, gateStep);
            if (!shadowsOn && !reflectionNeedsPlayer)
            {
                ForgetPlayerBake();
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
            long resourceStep = RenderPipeline.ChainStepBegin();
            EnsureBakeResources(graphicsDevice);
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeResources, resourceStep);
            long bakeStep = RenderPipeline.ChainStepBegin();
            TrimBakeCaches();
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeTrim, bakeStep);
            bakeStep = RenderPipeline.ChainStepBegin();
            RunSceneBakes(graphicsDevice, config, shadowsOn);
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeScene, bakeStep);

            // Sitting still casts (the bake captures the current SEATED animation frame, so the
            // silhouette matches the pose); horseback skips — the horse's own shadow covers the
            // rider. SWIMMING keeps the bake but drops _playerReady: the shadow consumers gate
            // on _playerReady (a swimmer casts no shadow), while the water shader's exclusion
            // gate reads PlayerMask — without it the ripple displacement warped the swimmer's
            // own pixels (the bathhouse "wavy body").
            long whoStep = RenderPipeline.ChainStepBegin();
            Farmer who = Game1.player;
            bool swim = who != null && who.swimming.Value;
            if (who == null || who.currentLocation != Game1.currentLocation || who.isRidingHorse())
            {
                ForgetPlayerBake();
                return;
            }

            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeWho, whoStep);
            long poseStep = RenderPipeline.ChainStepBegin();
            BakePlayerPose(graphicsDevice, who, swim, reflectionNeedsPlayer);
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakePose, poseStep);
            // With the pose baked, compose every cast of its shadow into the patch, cut by the
            // map, while a render-target swap is still allowed (see ShadowRenderer.PlayerPatch).
            if (shadowsOn)
            {
                long patchStep = RenderPipeline.ChainStepBegin();
                RenderPlayerShadowPatch(graphicsDevice, config);
                RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.PlayerPatch, patchStep);
            }
            }
            finally
            {
                _renderDepth--;
            }
        }

        /// <summary>Forget the player silhouette: nothing may read a target whose pose no
        /// longer matches the farmer on screen.</summary>
        private void ForgetPlayerBake()
        {
            _playerReady = false;
            _playerMaskFresh = false;
            _playerColorFresh = false;
            PlayerMask = null;
            PlayerColor = null;
        }

        /// <summary>Create the one-off drawing kit every bake path shares. Cheap after the
        /// first frame: each field is created once and lives for the mod's lifetime.</summary>
        private void EnsureBakeResources(GraphicsDevice graphicsDevice)
        {
            _renderTargetSpriteBatch ??= new SpriteBatch(graphicsDevice);
            _gradientTexture ??= BuildGradient(graphicsDevice);
            _propGradientTexture ??= BuildGradient(graphicsDevice, 0f);
            _contactBlobTexture ??= BuildBlob(graphicsDevice);
        }

        /// <summary>Drop the coldest bakes when a cache outgrows its cap, then report what
        /// the caches hold going into this frame.</summary>
        private void TrimBakeCaches()
        {
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
            // Report occupancy AFTER eviction: what the caches actually hold going into this
            // frame is the number that pairs with the miss count below it.
            FrameCost.CacheOccupancy(_bakedObjectCache.Count, ObjectBakeCapTotal, _casterBakeCache.Count, CasterBakeCap);
        }

        /// <summary>Bake the scene's silhouettes for this frame: characters every frame,
        /// objects on arrival in a location and then only what the draw pass reported
        /// missing or stale.</summary>
        private void RunSceneBakes(GraphicsDevice graphicsDevice, ModConfig config, bool shadowsOn)
        {
            bool objectsOn = shadowsOn && SunCasts() && config.DirectionalShadowObjects;
            float sunRotation = 0f, sunStretch = 0f;
            if (objectsOn)
            {
                ComputeSun(out sunRotation, out sunStretch, out _);
                _sunLengthScale = Math.Max(0.1f, config.DirectionalShadowLength);
                sunStretch *= _sunLengthScale;
                CaptureKindTuning(config);
            }
            // A location change no longer clears: the key is (texture, frame, flip), which is not
            // tied to a map, so warping back and forth used to re-bake everything both ways for
            // nothing. It still triggers ONE full enumeration, so a new screen arrives baked
            // instead of spending a frame on banded stand-ins.
            if (ForgetObjectBakesRequested)
            {
                ForgetObjectBakesRequested = false;
                ForgetObjectBakes();
            }
            // By place, not by object: the other screen's copy of this map is not an arrival.
            bool locationChanged = !SDVRadiance.LiveScreens.SamePlace(Game1.currentLocation, _objectBakeLocation);
            _objectBakeLocation = Game1.currentLocation;

            // Over cap AFTER eviction means the hot set alone does not fit: a foliage pack that
            // multiplies the distinct (texture, frame, flip) bakes is the suspected cause of
            // "directional shadows on trees and bushes are unplayably slow with Simple Foliage,
            // fine with the setting off". Say so once per location, with both numbers.
            if (_bakedObjectCache.Count > ObjectBakeCapTotal && DiagnosticMonitor != null
                && Game1.currentLocation is { } capLoc && capLoc != _objectCapLoggedLocation)
            {
                _objectCapLoggedLocation = capLoc;
                DiagnosticMonitor.Log($"[shadow] object bake cache over cap at {capLoc.NameOrUniqueName}: "
                       + $"{_bakedObjectCache.Count} distinct sprites still hot (cap {ObjectBakeCapTotal}, "
                       + $"{ObjectSlotsAllocated()} slots allocated) — more sprites are on screen at once "
                       + "than the cache can hold, so some object shadows re-bake as they scroll.", LogLevel.Debug);
            }

            // Bake NPC + animal silhouettes (single-sprite casters) — cheap when warm: cache
            // hits only, no RT switch. Runs every frame so new animation frames bake instantly.
            // Shadow-only: the reflection stamps NPCs from their live sprite, not from a bake.
            long casterStep = RenderPipeline.ChainStepBegin();
            if (shadowsOn && Game1.currentLocation is { } casterLocation)
                BakeCasters(graphicsDevice, casterLocation, CasterBlurBaked ? Math.Max(0f, config.DirectionalShadowBlur) : 0f);
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeCasters, casterStep);

            // Bake OBJECT silhouettes (trees/bushes/clumps/furniture/craftables/…). The FULL
            // enumeration — every on-screen tile, every entity list, the tile-art classifier —
            // runs on arrival in a location and never again. On every other frame it used to run
            // anyway, in bake mode, and on a warm frame that is a second complete walk of the
            // scene per frame whose every lookup answers "already baked". The draw pass now
            // reports what it found missing OR stale (see EmitObject), and a warm bake pass does
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
                    {
                        // The blur is an ARGUMENT now, not a field the bake reads behind the draw
                        // pass's back, so the full enumeration has to hand over the real one. It
                        // passed a zero here for as long as the bake had its own copy, which would
                        // now mean every silhouette baked on arrival in a location came out crisp.
                        //
                        // The whole map, not the screen: this frame is under the warp fade, and a
                        // bake burst here is a burst nobody sees, where the same sprites baked on
                        // first sight while walking were the 10 ms frames a farm walk showed.
                        // Bounded by the cache cap (EmitObject stops at it) and by the map.
                        long arrivalStep = RenderPipeline.ChainStepBegin();
                        int before = _bakedObjectCache.Count;
                        long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                        _bakeWholeMap = WholeMapArrivalBake;
                        DrawObjectShadows(_renderTargetSpriteBatch!, objectLocation, sunRotation, sunStretch, 0f, config.DirectionalShadowBlur);
                        _bakeWholeMap = false;
                        DiagnosticMonitor?.Log($"[diag] object bakes on arrival: {_bakedObjectCache.Count - before} in "
                            + $"{(System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms, "
                            + $"cache {_bakedObjectCache.Count} of {ObjectBakeCapTotal} ({objectLocation.NameOrUniqueName})", LogLevel.Trace);
                        RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeObjects, arrivalStep);
                    }
                    else
                    {
                        long queuedStep = RenderPipeline.ChainStepBegin();
                        BakeQueuedObjectSprites(graphicsDevice);
                        RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeObjectsQueued, queuedStep);
                    }
                }
                catch (Exception ex) { if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] obj bake threw: {ex}", LogLevel.Warn); } }
                finally { graphicsDevice.SetRenderTargets(objPrev); _isBakingObjects = false; _bakeWholeMap = false; }
            }
            // Either path leaves the queue spent, including anything the refresh budget did not
            // reach. Nothing is lost by that: an entry the sun has moved off is still stale next
            // frame, so the draw pass simply asks again, and dropping the list keeps a request
            // from outliving the shear it was recorded under.
            _objectBakeQueue.Clear();
        }

        /// <summary>Render the player's current pose to the persistent silhouette target (and
        /// its full-colour twin when the reflection wants it), reusing the last bake when the
        /// pose has not moved.</summary>
        private void BakePlayerPose(GraphicsDevice graphicsDevice, Farmer who, bool swim, bool reflectionNeedsPlayer)
        {
            // PreserveContents is REQUIRED for every persistent bake target: the default
            // DiscardContents only guarantees the pixels until the next target swap/present,
            // which was fine when everything re-baked per frame — cached across frames, the
            // content decayed into garbage (grid-line artifacts all over the map).
            _playerRenderTarget ??= VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "player silhouette");

            Rectangle src = who.FarmerSprite.SourceRect;
            _playerBakeFarmerId = who.UniqueMultiplayerID;

            // Same pose as the last bake → the RT is still correct, skip the 3-batch redraw.
            // The every-8-frames refresh keeps accessory layers that animate independently of
            // the body frame (Fashion Sense hair sway etc.) fresh — but ONLY when such a mod is
            // actually installed. It used to run unconditionally, which meant a player standing
            // perfectly still was re-baked 7.5 times a second on every install, for layers that
            // in a vanilla-appearance game do not exist. This bake measured as the single most
            // expensive part of the mod, ahead of drawing every shadow on screen.
            //
            // Held back while the author clock is frozen. Freeze exists so the same scene captures
            // to the same bytes twice, and this refresh is a hole straight through it: Game1.ticks
            // keeps counting while frozen, so every eighth frame the player was re-baked and
            // whatever Fashion Sense had animated in the meantime came with it. Three seconds
            // between two captures of one "frozen" scene is about 22 re-bakes, which showed up as
            // a couple of swaying hair pixels reflected into the water - an 8x10 patch of changed
            // colour inside an otherwise byte-identical silhouette, at the same place on every map,
            // which is what a fixed character with fixed hair cycling two phases looks like. It
            // failed the harness gate, so nothing could be verified through it at all.
            var sig = (who.FarmerSprite.CurrentFrame, (int)who.FacingDirection, src);
            // Staggered by who it is, so two screens' players do not fall due on the same frame
            // (see the same line in ShadowRenderer.Farmers).
            bool accessoryRefreshDue = PlayerAccessoriesAnimate && !Determinism.Frozen
                                       && (Game1.ticks + (int)(who.UniqueMultiplayerID & 7L)) % 8 == 0;
            // Fresh says the pose still matches. Usable says the pixels are still there: a
            // device reset empties a render target without touching any flag this mod keeps.
            if (_playerMaskFresh && sig == _playerBakeSignature && !accessoryRefreshDue
                && GpuContent.Usable(_playerRenderTarget)
                && (!reflectionNeedsPlayer || (_playerColorFresh && GpuContent.Usable(_playerColorRenderTarget))))
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
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
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
                    _playerColorRenderTarget ??= VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                        SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "player colour");
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
                try { _renderTargetSpriteBatch!.End(); } catch { }
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] player RT prep threw: {ex}", LogLevel.Warn); }
            }
            finally
            {
                graphicsDevice.SetRenderTargets(prev);
            }
        }

        /// <summary>
        /// Ensure every on-screen NPC/animal sprite FRAME has a baked silhouette in the
        /// persistent cache (black + feet→head alpha gradient), so <see cref="DrawNpcShadow"/> /
        /// <see cref="DrawAnimalShadow"/> can composite one smooth image instead of banding.
        /// Runs during RenderingWorld (render-target swaps are safe there). Warm frames are a
        /// dictionary hit — only frames never seen before actually bake.
        /// </summary>
        private void BakeCasters(GraphicsDevice graphicsDevice, GameLocation location, float blurPx)
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
                        RefreshCasterBlur(graphicsDevice, key.Item1, key.Item2, warm, blurPx, ref prev);
                        continue;
                    }
                    prev ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Item1, key.Item2, blurPx, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakeCache[key] = new SpriteBake { Rt = rt, FeetInRt = feet, BakedBlur = blurPx, LastUsedTick = Game1.ticks };
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
                        RefreshCasterBlur(graphicsDevice, key.Item1, key.Item2, warm, blurPx, ref prev);
                        continue;
                    }
                    prev ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Item1, key.Item2, blurPx, out RenderTarget2D rt, out Vector2 feet))
                        _casterBakeCache[key] = new SpriteBake { Rt = rt, FeetInRt = feet, BakedBlur = blurPx, LastUsedTick = Game1.ticks };
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

        /// <summary>Live switch for the A/B: with it off, character bakes carry no blur and the
        /// draw softens them tap by tap as it did before 1.7.5. radiance_casterblur.</summary>
        internal static bool CasterBlurBaked = true;

        /// <summary>A warm character bake whose softness no longer matches the setting is
        /// re-rendered into the slot it already owns: the blur lives in the pixels now (see
        /// <see cref="BakeSprite"/>), so a changed slider or the A/B switch would otherwise
        /// leave every old bake at its old edge. The same 0.3 px tolerance the object bakes
        /// use; the slider moves in tenths, so a nudge of it re-bakes once and a bake never
        /// chases a value that is settling.</summary>
        private void RefreshCasterBlur(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle src, SpriteBake warm,
            float blurPx, ref RenderTargetBinding[]? prev)
        {
            if (Math.Abs(blurPx - warm.BakedBlur) <= 0.3f)
                return;
            prev ??= graphicsDevice.GetRenderTargets();
            if (BakeSprite(graphicsDevice, texture, src, blurPx, out _, out Vector2 feet, into: warm.Rt))
            {
                warm.FeetInRt = feet;
                warm.BakedBlur = blurPx;
            }
        }

        /// <summary>
        /// Bake a single sprite to a pooled slot: black silhouette at 4×, pinned bottom-centre,
        /// then a feet→head alpha ramp multiplied on, then the shadow's softness stamped into the
        /// pixels (see <see cref="BlurSlotInPlace"/>). Returns false (→ banding fallback) if the
        /// sprite, with room for its soft edge, is larger than a slot. The caller owns the
        /// surrounding render-target swap.
        ///
        /// <para>The blur is baked, not drawn. Until 1.7.5 every strip of every character shadow
        /// was drawn nine times a frame, each copy shifted by the blur radius, which in town at
        /// noon was 737 draw calls for 60 shadows. A slot texel is one screen pixel at the draw's
        /// natural scale, the same as an object slot, so the radius goes in unchanged; the draw
        /// then stretches the soft edge with the silhouette, along the shadow, which is where a
        /// real penumbra widens.</para>
        /// </summary>
        /// <param name="into">A slot the entry already owns, to re-render in place; null leases
        /// one from the pool.</param>
        private bool BakeSprite(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle src, float blurPx,
            out RenderTarget2D rt, out Vector2 feetInRT, RenderTarget2D? into = null)
        {
            rt = null!;
            feetInRT = default;
            if (texture == null || src.IsEmpty)
                return false;
            float w = src.Width * 4f, h = src.Height * 4f;
            float blurTexels = Math.Max(0f, blurPx);
            // The soft edge spreads the silhouette by the radius on every side; without the slack
            // it clips at the slot wall and the shadow's head comes out with a flat top.
            if (w + 2f * blurTexels > CasterRtW || h + blurTexels > CasterRtH - 8f)
                return false;

            rt = into ?? RentCasterRT(graphicsDevice);
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
                BlurSlotInPlace(graphicsDevice, rt, blurTexels);
                FrameCost.Count(FrameCost.Counter.CasterBakes);
                return true;
            }
            catch
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                // Hand the slot back. Returning false means the caller never stores this target,
                // so without this the lease is lost: the pool still owns the memory but nothing
                // can ever reuse it, and a sprite that fails to bake every frame leaks a slot
                // every frame. Recycling used to happen wholesale when the cache cleared itself,
                // and that is exactly what was removed to stop the cache thrashing. A slot that
                // was already owned stays owned.
                if (into == null)
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
            var rt = VramTally.Track(new RenderTarget2D(graphicsDevice, CasterRtW, CasterRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "character bake slots");
            _casterRenderTargetPool.Add(rt);
            return rt;
        }

        /// <summary>Consecutive frames with nothing to bake for. Not a tick count: the release
        /// below has to survive a doorway, a cutscene and a menu without throwing away a screen
        /// of bakes that will be wanted again two seconds later.</summary>
        private int _idleFrames;

        /// <summary>
        /// Give the graphics memory back when the shadows that needed it are switched off.
        ///
        /// <para>
        /// The pools only ever grew. Nothing in this mod disposed a render target, so once a busy
        /// farm had filled the object pool, 123 slots at 400x456 stayed resident for the rest of
        /// the session: measured at 130.7 MB held in total, and measured again with every single
        /// effect switched off, where it was still 130.7 MB. A player who turns the mod's features
        /// off to fix their frame rate keeps paying the memory in full.
        /// </para>
        ///
        /// <para>
        /// That is the one cost shaped like the report we could not explain - "it ran fine before
        /// I installed this" from someone with everything disabled. It takes no time per frame,
        /// so every timer in this mod reads zero, and on a card with little to spare it makes the
        /// driver start evicting textures, which stutters. Holding it while switched off was
        /// never defensible; it simply was not visible until something measured memory instead of
        /// milliseconds.
        /// </para>
        ///
        /// <para>The delay is what makes this safe: bakes are expensive to rebuild, so a brief
        /// pass through a menu or a cutscene must not cost a screenful of them.</para>
        ///
        /// <para>
        /// CALLED FROM OUTSIDE THE FEATURE GATE, and that is the whole point. The first attempt
        /// put this call inside PreparePlayer, which ModEntry skips entirely when directional
        /// shadows are off - so the release never ran in precisely the case it exists for, and
        /// the measurement said so: 85 MB still held after every effect was switched off. Code
        /// that gives a resource back cannot live on the path that is skipped when the feature
        /// that wanted it is switched off.
        /// </para>
        /// </summary>
        internal void ReleaseIdleTargets(bool wanted)
        {
            const int IdleTicksBeforeRelease = 600;       // ten seconds at the game's 60 Hz tick
            if (wanted)
            {
                _idleFrames = 0;
                return;
            }
            if (ObjectSlotsAllocated() == 0 && _casterRenderTargetPool.Count == 0)
                return;
            if (++_idleFrames < IdleTicksBeforeRelease)
                return;
            _idleFrames = 0;

            _bakedObjectCache.Clear();
            _casterBakeCache.Clear();
            _objectBakeQueue.Clear();
            foreach (var free in _objectFreeTargetsByClass) free.Clear();
            _casterFreeTargets.Clear();
            int freed = ObjectSlotsAllocated() + _casterRenderTargetPool.Count;
            foreach (var pool in _objectRenderTargetPools)
            {
                foreach (RenderTarget2D rt in pool)
                    try { rt.Dispose(); } catch { }
                pool.Clear();
            }
            foreach (RenderTarget2D rt in _casterRenderTargetPool)
                try { rt.Dispose(); } catch { }
            _casterRenderTargetPool.Clear();
            for (int i = 0; i < _objectBlurScratches.Length; i++)
            {
                try { _objectBlurScratches[i]?.Dispose(); } catch { }
                _objectBlurScratches[i] = null;
            }
            try { _casterBlurScratch?.Dispose(); } catch { }
            _casterBlurScratch = null;
            // A full re-enumeration has to happen if the shadows come back, or the draw pass
            // would find every sprite missing and paint a screen of banded stand-ins.
            _objectBakeLocation = null;
            DiagnosticMonitor?.Log($"[shadow] released {freed} idle bake targets - shadows have been off for a while.", LogLevel.Debug);
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
                    FrameCost.Count(FrameCost.Counter.BakeEvictions);
                }
            }
        }

        /// <summary>
        /// The same for object bakes, but PER SIZE CLASS.
        ///
        /// <para>
        /// A single total cap was wrong once the slots stopped being one size: a screen of crops
        /// filling the small pool would evict a tree, whose slot is nine times the memory and far
        /// more expensive to rebuild, to make room for something that was never competing for the
        /// same space. Each pool now holds its own line, so a farm full of crops presses only on
        /// the crop-sized pool.
        /// </para>
        ///
        /// <para>The caps together allow 464 sprites where the old single pool allowed 128, and
        /// hold less memory doing it, because the ones that only need a small slot get one.</para>
        /// </summary>
        /// <summary>Live bakes per slot class, refilled by one walk of the cache each frame.</summary>
        private readonly int[] _objectLiveByClass = new int[ObjectSlotClasses.Length];
        /// <summary>Set for the arrival enumeration only: DrawObjectShadows walks the whole map and
        /// EmitObject stops baking at the cache cap. See RunSceneBakes.</summary>
        private bool _bakeWholeMap;

        /// <summary>Console A/B (radiance_mapbake): off makes the arrival enumeration walk the screen
        /// only, as it did before 1.7.4, so the first sight of every other sprite bakes mid-walk.</summary>
        internal static bool WholeMapArrivalBake = true;
        /// <summary>Set by the console; honoured at the top of the next bake pass, on the render
        /// thread, which is the only place the cache may be touched.</summary>
        internal static bool ForgetObjectBakesRequested;

        /// <summary>Drop every object bake and hand its slot back, so the next frame enumerates
        /// the location again from nothing. For the A/B: two walks over the same map are only
        /// comparable when neither starts with the other's bakes.</summary>
        internal void ForgetObjectBakes()
        {
            foreach (var kv in _bakedObjectCache)
                _objectFreeTargetsByClass[kv.Value.SlotClass].Add(kv.Value.Rt);
            _bakedObjectCache.Clear();
            _objectBakeQueue.Clear();
            _objectBakeLocation = null;
        }

        private void EvictColdObjectBakes()
        {
            // One walk to count every class, not one walk per class: this runs every frame, and
            // on a farm holding four hundred bakes three walks to learn that nothing is over its
            // cap were most of what the method did.
            Array.Clear(_objectLiveByClass, 0, _objectLiveByClass.Length);
            foreach (var kv in _bakedObjectCache)
                _objectLiveByClass[kv.Value.SlotClass]++;
            for (int cls = 0; cls < ObjectSlotClasses.Length; cls++)
            {
                int cap = ObjectClassCap(cls);
                int live = _objectLiveByClass[cls];
                if (live <= cap)
                    continue;

                _objectEvictScratch.Clear();
                int keep = (int)(cap * EvictHeadroom);
                bool desperate = live > cap * 2;
                int coldBefore = Game1.ticks - HotBakeTicks;
                foreach (var kv in _bakedObjectCache)
                {
                    if (kv.Value.SlotClass != cls) continue;
                    if (desperate || kv.Value.LastUsedTick < coldBefore)
                        _objectEvictScratch.Add(kv.Key);
                }
                _objectEvictScratch.Sort((a, b) => _bakedObjectCache[a].LastUsedTick.CompareTo(_bakedObjectCache[b].LastUsedTick));
                int drop = Math.Min(live - keep, _objectEvictScratch.Count);
                for (int i = 0; i < drop; i++)
                {
                    if (_bakedObjectCache.TryGetValue(_objectEvictScratch[i], out SpriteBake? bake))
                    {
                        _objectFreeTargetsByClass[bake.SlotClass].Add(bake.Rt);
                        _bakedObjectCache.Remove(_objectEvictScratch[i]);
                        FrameCost.Count(FrameCost.Counter.BakeEvictions);
                    }
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
                FrameCost.Count(FrameCost.Counter.ShadowDrawCalls);
                spriteBatch.Draw(texture, pos, src, baseColor * MathHelper.Clamp(alpha, 0f, 1f), rot, origin, scale, effects, depth);
                return;
            }

            // Per-tap alpha so 1-(1-a)^N ≈ target alpha at the fully-covered core.
            float a = 1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), 1f / taps.Length);
            Color c = baseColor * a;
            FrameCost.Count(FrameCost.Counter.ShadowDrawCalls, taps.Length);
            foreach (Vector2 t in taps)
                spriteBatch.Draw(texture, pos + t * blur, src, c, rot, origin, scale, effects, depth);
        }

        /// <summary>Number of horizontal bands used to fake the NPC opacity gradient.</summary>
        private const int NpcBands = 7;

        /// <summary>
        /// Sort depth for a piece of shadow lying <paramref name="upScreenPixels"/> up the screen
        /// from the caster's feet. The value is SIGNED: a lamp above the caster throws the shadow
        /// down the screen instead, and that piece belongs in front of the caster, not behind.
        /// </summary>
        /// <remarks>
        /// A body and its shadow do not belong at the same depth. A body stands on one tile and is
        /// drawn at that tile's depth; a shadow LIES ON THE FLOOR and runs away across it, so each
        /// part of it belongs at the depth of the floor row it is lying on. Given a single depth
        /// for its whole length, a shadow is sorted as though all of it stood where the caster
        /// stands, and it paints over the table, chair or stool standing between the caster's feet
        /// and the shadow's tip.
        /// <para>
        /// Sorting by the row a thing stands on is the game's own rule, and it works here for the
        /// same reason it works there: a sprite covers screen rows ABOVE its base, so whatever
        /// stands lower down the screen than a patch of floor is what may cover that patch.
        /// </para>
        /// <para>
        /// This cannot help against a counter the MAP paints, which goes down on the Buildings
        /// layer before the sorted batch is even opened: nothing drawn in that batch can get
        /// behind it, at any depth.
        /// </para>
        /// </remarks>
        private static float ShadowPieceDepth(float anchorWorldY, float upScreenPixels)
            => MathHelper.Clamp((anchorWorldY - upScreenPixels) / 10000f - ShadowDepthBias, 0f, 1f);

        /// <summary>
        /// The same rule for a caster that arrives with a finished sort depth instead of a world
        /// row: the piece lying <paramref name="upScreenPixels"/> up the screen from its feet.
        /// </summary>
        /// <remarks>
        /// Objects were left out of the grounded sort when it was written, on the grounds that an
        /// object's depth carries a per-column tie-break (<c>tile.X * 1e-5f</c>, which keeps two
        /// things standing on one row apart) that has no meaning as a world Y and so could not be
        /// rebuilt from one. True, and beside the point: the tie-break never needed rebuilding.
        /// Moving a piece of shadow one row further up the screen subtracts the same amount from
        /// the sort depth whatever that depth was built out of, so subtracting from the depth the
        /// caller already computed carries its tie-break, its bias and any other term it holds
        /// through untouched. A caller that hands over a depth of zero, as the building coverage
        /// mask does because depth means nothing to a mask, still gets zero.
        /// <para>
        /// One strip is 32 screen pixels, which is 3.2e-3 of depth: three hundred times the
        /// tie-break, so the strips order among themselves and the tie-break still does its own
        /// job inside a row.
        /// </para>
        /// </remarks>
        private static float ShadowPieceDepthUnder(float casterSortDepth, float upScreenPixels)
            => MathHelper.Clamp(casterSortDepth - upScreenPixels / 10000f, 0f, 1f);

        /// <summary>The footprints, in world pixels, of every building the current location owns,
        /// refreshed once per shadow pass. Read by <see cref="GroundedPieceDepth"/>.</summary>
        private static readonly List<Rectangle> BuildingFootprints = new();

        /// <summary>Collect the buildings' footprints for this pass. Cheap: a location owns a
        /// handful, and the list is what lets every strip of every shadow answer "am I lying on a
        /// building" without walking the buildings itself.</summary>
        private static void RefreshBuildingFootprints(GameLocation? location)
        {
            BuildingFootprints.Clear();
            if (location?.buildings == null)
                return;
            foreach (Building bld in location.buildings)
            {
                if (bld == null)
                    continue;
                BuildingFootprints.Add(new Rectangle(bld.tileX.Value * 64, bld.tileY.Value * 64,
                    bld.tilesWide.Value * 64, bld.tilesHigh.Value * 64));
            }
        }

        /// <summary>
        /// Sort depth for a piece of a character's shadow: the floor row it lies on, unless that
        /// row is inside a building's footprint, where it is the caster's own row instead.
        /// </summary>
        /// <remarks>
        /// A shadow that runs up the screen onto a table is covered by the table, and the floor
        /// row sort gives exactly that. A shadow that runs up onto a house is a different case:
        /// the house is one sprite many tiles tall, sorted at one row near its base, and a piece
        /// of shadow lying on its porch or climbing its wall is sorted BEHIND that row and
        /// vanishes, while the player standing on the same porch, sorted at their feet, is drawn
        /// over the house as they should be. Measured on the farmhouse porch: the house at
        /// 0.0960, the player at 0.1003, the shadow's strips from 0.0984 down to 0.0925, so every
        /// strip past the first was under the house. Light falling on a wall throws the shadow
        /// onto the wall, so within a building's footprint the shadow takes the caster's row and
        /// is drawn over the building's face, just under the caster. Furniture is not a building
        /// and keeps the floor-row rule.
        /// </remarks>
        /// <summary>
        /// How far along a character's shadow, in screen pixels from the feet, the shadow is cut
        /// off by a solid tile the map paints, or <see cref="float.MaxValue"/> when nothing cuts it.
        /// </summary>
        /// <remarks>
        /// The saloon counter is painted into the map on the Buildings layer, which the game lays
        /// down before the sorted batch opens, so no sort depth can put anything behind it. Sorted
        /// or not, a shadow leaning across it painted the counter top and then the floor and the
        /// stools beyond, as if the counter were not there. A counter is a box: light landing on
        /// it stops at it. So the shadow is walked from the feet outward in world tiles, and the
        /// first solid map tile it meets (a Buildings tile with no Passable property, which is what
        /// a counter, a wall or a shelf is) becomes the end of it, at that run of tiles' far edge.
        /// The pieces lying ON the tiles are kept, because light on a counter top or a wall throws
        /// the shadow onto that surface; only what lies beyond is dropped. Placed things are not
        /// map tiles and are sorted like any sprite, so they are not consulted here.
        /// </remarks>
        private static float ShadowClipDistance(GameLocation? location, float feetWorldX, float anchorWorldY,
            float rot, float scaleY, float lengthTexels)
        {
            if (location == null)
                return float.MaxValue;
            float sin = (float)Math.Sin(rot), cos = (float)Math.Cos(rot);
            float lengthPixels = lengthTexels * scaleY;
            const float step = 8f;
            // Which way the shadow runs decides which edge of the solid tiles ends it. A map
            // tile's visible face points at the viewer, down the screen. A shadow running UP the
            // screen comes from a light on the viewer's side, which lights that face, so the
            // shadow lands on it and the pieces lying on the tiles are kept: this is a shadow
            // climbing the back wall. A shadow running DOWN the screen comes from a light behind
            // the thing, whose visible face is then in its own shade, so the shadow has nowhere
            // to land there and stops at the near edge: this is a shadow meeting the counter from
            // behind the bar, where painting it on the counter's front read as passing through.
            bool towardViewer = cos < 0f;
            int feetTileX = (int)Math.Floor(feetWorldX / 64f), feetTileY = (int)Math.Floor(anchorWorldY / 64f);
            bool SolidAt(float d)
            {
                int tileX = (int)Math.Floor((feetWorldX + sin * d) / 64f);
                int tileY = (int)Math.Floor((anchorWorldY - cos * d) / 64f);
                // The tile under a pair of feet is never the thing that cuts their shadow.
                if (tileX == feetTileX && tileY == feetTileY)
                    return false;
                return location.hasTileAt(tileX, tileY, "Buildings")
                    && location.doesTileHaveProperty(tileX, tileY, "Passable", "Buildings") == null;
            }
            float near = -1f, far = -1f, previous = 0f;
            for (float d = step; d < lengthPixels; d += step)
            {
                if (SolidAt(d))
                {
                    if (near < 0f)
                    {
                        // The edge lies between the last clear sample and this one. Standing
                        // against the counter the feet are a dozen pixels from it, so a sample's
                        // width of slack was a visible spill of shadow onto its front: bisect to
                        // within a pixel of the tile's edge instead.
                        float clear = previous, solid = d;
                        for (int i = 0; i < 4; i++)
                        {
                            float mid = (clear + solid) * 0.5f;
                            if (SolidAt(mid)) solid = mid; else clear = mid;
                        }
                        near = solid;
                    }
                    far = d;
                }
                else if (far >= 0f)
                    break;
                previous = d;
            }
            if (far < 0f)
                return float.MaxValue;
            return towardViewer ? near : far + step * 0.5f;
        }

        private static float GroundedPieceDepth(float anchorWorldY, float upScreenPixels, float feetWorldX, float sidewaysPixels)
        {
            if (upScreenPixels > 0f && BuildingFootprints.Count > 0)
            {
                int worldX = (int)(feetWorldX + sidewaysPixels);
                int worldY = (int)(anchorWorldY - upScreenPixels);
                foreach (Rectangle footprint in BuildingFootprints)
                    if (footprint.Contains(worldX, worldY))
                        return ShadowPieceDepth(anchorWorldY, 0f);
            }
            return ShadowPieceDepth(anchorWorldY, upScreenPixels);
        }

        /// <summary>Screen pixels of shadow per ground strip. A strip is flat in depth, so this is
        /// how far the shadow's sort position is allowed to lag the floor beneath it; half a tile
        /// resolves every piece of furniture, which is the smallest thing a shadow can be behind.</summary>
        private const float GroundStripPixels = 32f;
        /// <summary>Ceiling on the strips. Each one is a draw call times the blur taps, so the
        /// count is bought only where the shadow is long enough to need it: a shadow that does not
        /// reach past the caster's own tile is one strip, exactly as it was before.</summary>
        private const int MaxGroundStrips = 6;

        /// <summary>
        /// Draw a baked silhouette in horizontal strips, each sorted at the depth of the floor row
        /// it lies on (see <see cref="ShadowPieceDepth"/>). A short shadow comes out as one strip,
        /// which is <see cref="DrawSoft"/> unchanged.
        /// </summary>
        /// <param name="anchorWorldY">The caster's contact row in world pixels, or its finished
        /// sort depth when <paramref name="anchorIsSortDepth"/> is set.</param>
        /// <param name="anchorIsSortDepth">Whether the anchor is already a sort depth rather than
        /// a world row. Objects arrive that way; see <see cref="ShadowPieceDepthUnder"/>.</param>
        private static void DrawSoftGrounded(SpriteBatch spriteBatch, Vector2[] taps, Texture2D texture, Rectangle? src,
            Vector2 pos, Color baseColor, float alpha, float rot, Vector2 origin, Vector2 scale, float anchorWorldY,
            SpriteEffects effects, float blur, bool anchorIsSortDepth = false)
        {
            // Only the part of the silhouette's length that runs along the screen's Y moves it to
            // another floor row. The sideways lean moves it along the row it is already on, which
            // no sort depth has an opinion about. Signed, because a lamp overhead throws the
            // shadow DOWN the screen and those pieces belong in front of the caster.
            float upScreenPerTexel = (float)Math.Cos(rot) * scale.Y;
            float feetWorldX = pos.X + Game1.viewport.X;
            Rectangle area = src ?? new Rectangle(0, 0, texture.Width, texture.Height);
            // Where a solid map tile ends the shadow, the silhouette itself is cut there, from its
            // tip end, BEFORE the strips are decided. Skipping strips alone left the short shadows
            // untouched: a lamp's cast is often under one strip long, took the single-draw path
            // below, and went on through the counter whole.
            float clipDistance = anchorIsSortDepth ? float.MaxValue
                : ShadowClipDistance(Game1.currentLocation, feetWorldX, anchorWorldY, rot, scale.Y, origin.Y);
            if (clipDistance < float.MaxValue)
            {
                // The soft edge is drawn as taps offset by the blur radius in every direction, so
                // the silhouette must end a blur's width short of the tile for its softness to end
                // AT the tile rather than a few pixels onto it; and a texel more for the rounding.
                float clipInsideBlur = Math.Max(0f, clipDistance - blur - scale.Y);
                int cut = (int)Math.Ceiling(origin.Y - clipInsideBlur / Math.Max(scale.Y, 0.001f));
                if (cut >= area.Height)
                    return;
                if (cut > 0)
                {
                    area = new Rectangle(area.X, area.Y + cut, area.Width, area.Height - cut);
                    // The origin keeps naming the feet row of what is left.
                    origin.Y -= cut;
                }
            }
            float alongScreenY = Math.Abs(origin.Y * upScreenPerTexel);
            int strips = (int)MathHelper.Clamp(alongScreenY / GroundStripPixels, 1f, MaxGroundStrips);
            if (strips <= 1 || area.Height < strips * 2)
            {
                DrawSoft(spriteBatch, taps, texture, area, pos, baseColor, alpha, rot, origin, scale,
                    anchorIsSortDepth ? ShadowPieceDepthUnder(anchorWorldY, 0f)
                                      : ShadowPieceDepth(anchorWorldY, 0f), effects, blur);
                return;
            }
            for (int i = 0; i < strips; i++)
            {
                int y0 = area.Height * i / strips;
                int y1 = area.Height * (i + 1) / strips;
                var strip = new Rectangle(area.X, area.Y + y0, area.Width, y1 - y0);
                // The origin has to keep naming the same feet row, so it rises with the strip -
                // the same correction the banded gradient makes for its bands.
                var stripOrigin = new Vector2(origin.X, origin.Y - y0);
                float texelsAboveFeet = origin.Y - (y0 + y1) * 0.5f;
                // Past a solid map tile the shadow is over (see ShadowClipDistance).
                if (texelsAboveFeet * scale.Y > clipDistance)
                    continue;
                float upScreen = texelsAboveFeet * upScreenPerTexel;
                // Where the strip's centre lands sideways, for the building test: the lean moves
                // a piece along its row as well as up the screen.
                float sideways = texelsAboveFeet * (float)Math.Sin(rot) * scale.Y;
                DrawSoft(spriteBatch, taps, texture, strip, pos, baseColor, alpha, rot, stripOrigin, scale,
                    anchorIsSortDepth ? ShadowPieceDepthUnder(anchorWorldY, upScreen)
                                      : GroundedPieceDepth(anchorWorldY, upScreen, feetWorldX, sideways), effects, blur);
            }
        }

        /// <summary>
        /// Draw a single-texture sprite as a shadow with a feet→head opacity gradient, by
        /// slicing it into horizontal bands (each drawn about the shared feet anchor so they
        /// stay aligned under rotation + stretch) and fading each band's alpha toward the tip.
        /// </summary>
        /// <param name="anchorWorldY">The caster's own contact row in world pixels, which every
        /// band is sorted relative to. When <paramref name="anchorIsSortDepth"/> is set this is a
        /// finished sort depth instead, and the bands are offset from it by the same amount.</param>
        /// <param name="anchorIsSortDepth">Whether the anchor is a sort depth rather than a world
        /// row. Objects arrive that way, because an object's depth carries a per-column tie-break
        /// that no world Y can hold; see <see cref="ShadowPieceDepthUnder"/> for why that never
        /// stood in the way of grounding them. Every band is sorted at the floor row it lies on
        /// either way.</param>
        /// <param name="shadowColor">What the bands are stamped in. Black on the world, because a
        /// shadow is an absence of light; WHITE when the caller is filling a coverage mask that a
        /// later pass reads as "how much of this pixel is in shadow", where black would read as
        /// nothing at all.</param>
        private void DrawBandedGradient(SpriteBatch spriteBatch, Texture2D texture, Rectangle src, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rot, Vector2 scale, float anchorWorldY, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None,
            bool anchorIsSortDepth = false, Color? shadowColor = null)
        {
            Color bandColor = shadowColor ?? Color.Black;
            // The bands are already cut across the shadow's length, so each one can be sorted at
            // the depth of the floor row it lies on for nothing (see ShadowPieceDepth). Only the
            // part of the lean that runs along the screen's Y changes a band's row, and its sign
            // matters: a lamp overhead lays the shadow down the screen rather than up it.
            float upScreenPerTexel = (float)Math.Cos(rot) * scale.Y;
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
            float feetWorldX = feet.X + Game1.viewport.X;
            float clipDistance = anchorIsSortDepth ? float.MaxValue
                : ShadowClipDistance(Game1.currentLocation, feetWorldX, anchorWorldY, rot, scale.Y, feetRow);
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
                float texelsAboveFeet = baseOrigin.Y - (y0 + y1) * 0.5f;
                if (texelsAboveFeet * scale.Y > clipDistance)
                    continue;
                float upScreen = texelsAboveFeet * upScreenPerTexel;
                float sideways = texelsAboveFeet * (float)Math.Sin(rot) * scale.Y;
                float bandDepth = anchorIsSortDepth
                    ? ShadowPieceDepthUnder(anchorWorldY, upScreen)
                    : GroundedPieceDepth(anchorWorldY, upScreen, feetWorldX, sideways);
                DrawSoft(spriteBatch, Taps5, texture, band, feet, bandColor, alpha * ga, rot, origin, scale,
                    bandDepth, effects, blur);
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
        /// <summary>The golden-hour dial, captured once per frame by ModEntry because
        /// <see cref="ComputeSun"/> is static and has no config within reach.</summary>
        internal static float GoldenHourStrengthNow;

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
                LightningEffects.OverrideShadowKey(ref rot, ref stretch, ref alpha);
                return;
            }
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float sunSkyOffset = MathHelper.Clamp((mins - 720f) / 360f, -1f, 1f);
            // Lean more sideways (was 0.8) so the shadow lies to the side of the body instead of
            // straight up over it — reduces the "shadow on the sprite" overlap while staying
            // upright (not the rejected upside-down flip).
            rot = 1.15f * sunSkyOffset;                                     // <0 morning lean-left, >0 evening lean-right
            stretch = MathHelper.Lerp(0.3f, 1.2f, Math.Abs(sunSkyOffset));  // stretched LONG when the sun is low
            // Golden hour: the true edges of the day stretch further still. Quartic in the
            // offset, so noon and mid-afternoon feel nothing and only a genuinely low sun
            // goes long; every consumer of this method (characters, objects, the window
            // daylight patch) inherits it, which is what keeps the parity rule intact.
            float lowSunEdge = sunSkyOffset * sunSkyOffset * sunSkyOffset * sunSkyOffset;
            stretch *= 1f + GoldenHourStrengthNow * 1.3f * lowSunEdge;
            alpha = 0.9f * TimeFade();                           // opacity at the feet (× strength; fades toward the tip)
            // A lightning strike momentarily overrides both branches: every bake and draw path
            // funnels through this method, so keying it here keys every shadow at once.
            LightningEffects.OverrideShadowKey(ref rot, ref stretch, ref alpha);
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
