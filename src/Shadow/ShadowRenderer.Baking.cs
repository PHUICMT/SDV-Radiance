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
                _characterSunLive = false;
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
            CaptureCharacterSun(config, shadowsOn);

            // Sitting still casts (the bake captures the current SEATED animation frame, so the
            // silhouette matches the pose); horseback skips — the horse's own shadow covers the
            // rider. SWIMMING keeps the bake but drops _playerReady: the shadow consumers gate
            // on _playerReady (a swimmer casts no shadow), while the water shader's exclusion
            // gate reads PlayerMask — without it the ripple displacement warped the swimmer's
            // own pixels (the bathhouse "wavy body").
            long whoStep = RenderPipeline.ChainStepBegin();
            Farmer who = Game1.player;
            bool swimming = who != null && who.swimming.Value;
            if (who == null || who.currentLocation != Game1.currentLocation || who.isRidingHorse())
            {
                ForgetPlayerBake();
                return;
            }

            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakeWho, whoStep);
            long poseStep = RenderPipeline.ChainStepBegin();
            BakePlayerPose(graphicsDevice, who, swimming, reflectionNeedsPlayer);
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.BakePose, poseStep);
            // The daylight shadow is laid down by the sun's projection, skew and all, from the
            // upright bake the water and the lamps go on reading (see LayDownPlayerSun).
            if (shadowsOn)
                LayDownPlayerSun(graphicsDevice, config);
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
            _playerSunFresh = false;
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
            MakeWantedStackedPools(graphicsDevice);
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
            CaptureCasterSwitches(config);
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
            string previousBakePlace = _objectBakeLocation?.NameOrUniqueName ?? "none";
            _objectBakeLocation = Game1.currentLocation;
            // Named on both sides: the host screen standing still in Town was seen to start this walk
            // twice in sixty calls with "location changed", and only the two names can say from what.
            _arrivalWalkReason = locationChanged
                ? $"location changed {previousBakePlace} -> {Game1.currentLocation?.NameOrUniqueName ?? "none"}"
                : _bakedObjectCache.Count == 0 ? "cache empty" : null;

            // Over cap AFTER eviction means the hot set alone does not fit: a foliage pack that
            // multiplies the distinct (texture, frame, flip) bakes is the suspected cause of
            // "directional shadows on trees and bushes are unplayably slow with Simple Foliage,
            // fine with the setting off". Say so once per location, with both numbers.
            if (_bakedObjectCache.Count > ObjectBakeCapTotal && DiagnosticMonitor != null
                && Game1.currentLocation is { } overCapLocation && overCapLocation != _objectCapLoggedLocation)
            {
                _objectCapLoggedLocation = overCapLocation;
                DiagnosticMonitor.Log($"[shadow] object bake cache over cap at {overCapLocation.NameOrUniqueName}: "
                       + $"{_bakedObjectCache.Count} distinct sprites still hot (cap {ObjectBakeCapTotal}, "
                       + $"{ObjectSlotsAllocated()} slots allocated) — more sprites are on screen at once "
                       + "than the cache can hold, so some object shadows re-bake as they scroll.", LogLevel.Debug);
            }

            // Bake NPC + animal silhouettes (single-sprite casters) — cheap when warm: cache
            // hits only, no RT switch. Runs every frame so new animation frames bake instantly.
            // Shadow-only: the reflection stamps NPCs from their live sprite, not from a bake.
            // Only while a lamp may cast: the sun's shadow of a person is laid down through the
            // object pool now (see DrawNpcShadow), and these upright slots serve the lamps alone.
            long casterStep = RenderPipeline.ChainStepBegin();
            if (shadowsOn && _sunBlend < 0.996f && Game1.currentLocation is { } casterLocation)
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
            // The queue is worked whenever the sun is up at all, objects on or off: a person's
            // daylight shadow bakes through it too (see DrawNpcShadow), and with the object switch
            // off every villager would otherwise stand in a banded stand-in for good.
            bool sunOn = shadowsOn && (SunCasts() || _sunBlend > 0.004f);
            if (sunOn && Game1.currentLocation is { } objectLocation)
            {
                _isBakingObjects = true;
                _objectGraphicsDevice = graphicsDevice;
                RenderTargetBinding[] previousObjectTargets = graphicsDevice.GetRenderTargets();
                try
                {
                    if (objectsOn && (locationChanged || _bakedObjectCache.Count == 0))
                    {
                        int walkingScreen = StardewModdingAPI.Context.ScreenId;
                        if (walkingScreen >= 0 && walkingScreen < _arrivalWalksByScreen.Length)
                        {
                            _arrivalWalksByScreen[walkingScreen]++;
                            _arrivalReasonByScreen[walkingScreen] = _arrivalWalkReason ?? "?";
                        }
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
                catch (Exception exception) { if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] obj bake threw: {exception}", LogLevel.Warn); } }
                finally { graphicsDevice.SetRenderTargets(previousObjectTargets); _isBakingObjects = false; _bakeWholeMap = false; }
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
        private void BakePlayerPose(GraphicsDevice graphicsDevice, Farmer who, bool swimming, bool reflectionNeedsPlayer)
        {
            // PreserveContents is REQUIRED for every persistent bake target: the default
            // DiscardContents only guarantees the pixels until the next target swap/present,
            // which was fine when everything re-baked per frame — cached across frames, the
            // content decayed into garbage (grid-line artifacts all over the map).
            _playerRenderTarget ??= VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "player silhouette");

            Rectangle sourceRect = who.FarmerSprite.SourceRect;
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
            var poseSignature = (who.FarmerSprite.CurrentFrame, who.FacingDirection, sourceRect);
            // Staggered by who it is, so two screens' players do not fall due on the same frame
            // (see the same line in ShadowRenderer.Farmers).
            bool accessoryRefreshDue = PlayerAccessoriesAnimate && !Determinism.Frozen
                                       && (SharedTicks.Now + (int)(who.UniqueMultiplayerID & 7L)) % 8 == 0;
            // Fresh says the pose still matches. Usable says the pixels are still there: a
            // device reset empties a render target without touching any flag this mod keeps.
            if (_playerMaskFresh && poseSignature == _playerBakeSignature && !accessoryRefreshDue
                && GpuContent.Usable(_playerRenderTarget)
                && (!reflectionNeedsPlayer || (_playerColorFresh && GpuContent.Usable(_playerColorRenderTarget))))
            {
                _playerReady = !swimming && !IsSeated(who);
                PlayerMask = _playerRenderTarget;
                PlayerColor = _playerColorFresh ? _playerColorRenderTarget : null;
                return;
            }
            _playerBakeSignature = poseSignature;

            float spriteWidth = sourceRect.Width * 4f, spriteHeight = sourceRect.Height * 4f;
            Vector2 spriteTopLeft = new((PlayerRtW - spriteWidth) / 2f, PlayerRtH - spriteHeight - 8f);
            _playerFeetInRenderTarget = new Vector2(PlayerRtW / 2f, PlayerRtH - 8f);

            RenderTargetBinding[] previousTargets = graphicsDevice.GetRenderTargets();
            try
            {
                graphicsDevice.SetRenderTarget(_playerRenderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_renderTargetSpriteBatch, who.FarmerSprite.CurrentAnimationFrame, who.FarmerSprite.CurrentFrame,
                    sourceRect, spriteTopLeft, Vector2.Zero, 0f, who.FacingDirection, Color.Black, 0f, 1f, who);
                _renderTargetSpriteBatch.End();

                // Keep the SHAPE and nothing else: appearance mods (Fashion Sense etc.) draw
                // through their own patches and ignore the black tint above, so without this a
                // white dress cast a white shadow. Works for ANY current or future appearance mod:
                // whatever got drawn, only its shape survives, white, to take the ink at draw time.
                _gradientTexture ??= BuildGradient(graphicsDevice);
                WhitenBake(graphicsDevice, new Rectangle(0, 0, PlayerRtW, PlayerRtH));

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
                        sourceRect, spriteTopLeft, Vector2.Zero, 0f, who.FacingDirection, Color.White, 0f, 1f, who);
                    _renderTargetSpriteBatch.End();
                }
                _playerColorFresh = reflectionNeedsPlayer;

                _playerMaskFresh = true;
                _playerReady = !swimming && !IsSeated(who);
                PlayerMask = _playerRenderTarget;
                PlayerColor = _playerColorFresh ? _playerColorRenderTarget : null;
            }
            catch (Exception exception)
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] player RT prep threw: {exception}", LogLevel.Warn); }
            }
            finally
            {
                graphicsDevice.SetRenderTargets(previousTargets);
            }
        }

        /// <summary>
        /// This frame's sun as the character passes will draw it: the same numbers
        /// <see cref="DrawSunShadows"/> and the player patch work out, taken once during the bakes
        /// so a silhouette laid down now and the draw made later in the frame agree about the
        /// projection. The cross-fades read here are last frame's, one ease step behind; a frozen
        /// capture settles them, and a sixtieth of a fade is not a picture.
        /// </summary>
        private void CaptureCharacterSun(ModConfig config, bool shadowsOn)
        {
            _characterSunLive = shadowsOn && _sunBlend > 0.004f;
            if (!_characterSunLive)
                return;
            ComputeSun(out _characterSunRotation, out _characterSunStretch, out _);
            _characterSunStretch *= Math.Max(0.1f, config.DirectionalShadowLength)
                                  * MathHelper.Lerp(1f, OvercastLength, _overcastBlend);
            _characterSunBlur = Math.Max(0f, config.DirectionalShadowBlur) + OvercastExtraBlur * _overcastBlend;
            _characterGroundForeshortening = config.ShadowCharacterGroundForeshortening;
        }

        /// <summary>Where the farmer's sprite sits inside an upright player bake: the layout
        /// <see cref="BakePlayerPose"/> and <see cref="BakeFarmerSilhouette"/> both use.</summary>
        private static Rectangle PlayerSpriteInBake(Rectangle sourceRect)
        {
            int width = sourceRect.Width * 4, height = sourceRect.Height * 4;
            return new Rectangle((PlayerRtW - width) / 2, PlayerRtH - height - 8, width, height);
        }

        /// <summary>
        /// Lay the player's upright silhouette down by the sun, into a target of its own, with the
        /// soft edge stamped in (see <see cref="LayDownSilhouette"/>). Made again when the pose
        /// changes, when the sun has moved the shadow's far end by more than a pixel or so, or when
        /// a softness dial moves; otherwise reused frame after frame like the pose bake itself.
        ///
        /// <para>Why a second target rather than the first laid down: the upright one is read by
        /// the water reflection and the sprite mask, which want the person standing up, and by
        /// every lamp, which leans it its own way. The sun is the one light whose direction is
        /// the same for the whole frame, so it is the one that can afford the lay-down.</para>
        /// </summary>
        private void LayDownPlayerSun(GraphicsDevice graphicsDevice, ModConfig config)
        {
            if (!_characterSunLive || !config.DirectionalShadowPlayer || !_playerReady || !_playerMaskFresh || _playerRenderTarget == null)
            {
                _playerSunFresh = false;
                return;
            }
            ShadowProjection projection = ShadowProjection.ForSolid(_characterSunRotation, _characterSunStretch, _characterGroundForeshortening);
            Rectangle sprite = PlayerSpriteInBake(_playerBakeSignature.sourceRect);
            // Drift is in the slot's own texels, which for an upright player bake are screen
            // pixels already, so it meets the refresh threshold as it is.
            if (_playerSunFresh && _playerSunRenderTarget != null && GpuContent.Usable(_playerSunRenderTarget)
                && _playerSunSignature == _playerBakeSignature
                && Math.Abs(_characterSunBlur - _playerSunBlur) <= 0.3f
                && _playerSunContactHardness == ContactHardnessNow && _playerSunPenumbraStretch == PenumbraStretchNow
                && _playerSunBakeDepth == BakeDepthNow
                && projection.Drift(_playerSunProjection, sprite.Width, sprite.Height) <= ShearRefreshPixels)
                return;
            // PreserveContents, like every persistent bake target: it is read back frames later.
            _playerSunRenderTarget ??= VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerSunRtSize, PlayerSunRtSize, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "player sun silhouette");
            _playerSunFresh = LayDownSilhouette(graphicsDevice, _playerRenderTarget, sprite, _playerFeetInRenderTarget, projection,
                _characterSunBlur, _playerSunRenderTarget, out _playerSunFeet, out _playerSunUnbake, out _playerSunContent);
            _playerSunProjection = projection;
            _playerSunBlur = _characterSunBlur;
            _playerSunSignature = _playerBakeSignature;
            _playerSunContactHardness = ContactHardnessNow;
            _playerSunBakeDepth = BakeDepthNow;
            _playerSunPenumbraStretch = PenumbraStretchNow;
        }

        /// <summary>
        /// Lay an upright silhouette down by a projection about its feet, into a square target,
        /// and stamp the soft edge in. The upright bake is drawn through the same matrix every
        /// object's sprite is drawn through, at whatever scale lets the laid-down shape fit, so
        /// what comes out is the shadow's true shape on the ground, skew included, ready to be
        /// stamped with no rotation and one scale. False when it fits at no scale.
        /// </summary>
        /// <param name="sprite">Where the silhouette sits inside the upright bake, in its texels.</param>
        /// <param name="feetInUpright">The feet inside the upright bake.</param>
        /// <param name="unbake">Screen pixels per texel of the laid-down target at draw time.</param>
        private bool LayDownSilhouette(GraphicsDevice graphicsDevice, Texture2D upright, Rectangle sprite, Vector2 feetInUpright,
            ShadowProjection projection, float blurPixels, RenderTarget2D target,
            out Vector2 feetInTarget, out float unbake, out Rectangle content)
        {
            feetInTarget = default;
            unbake = 1f;
            content = Rectangle.Empty;
            float originX = feetInUpright.X - sprite.X, originY = feetInUpright.Y - sprite.Y;
            float alongPerHeight = (float)Math.Sqrt(projection.AlongX * projection.AlongX + projection.AlongY * projection.AlongY);
            float acrossPerWidth = (float)Math.Sqrt(projection.AcrossX * projection.AcrossX + projection.AcrossY * projection.AcrossY);
            float rimGrowth = (float)Math.Sqrt(PenumbraElongation(alongPerHeight));
            projection.Bounds(sprite.Width, sprite.Height, originX, originY, out float left, out float right, out float top, out float bottom);
            // The finest fit that leaves room for the rim on every side and the same eight texels
            // under the feet every other slot keeps.
            float fit = 0f, blurTexels = 0f, rimTexels = 0f;
            foreach (float candidate in LayDownScales)
            {
                float candidateBlur = Math.Max(0f, blurPixels) * candidate;
                float candidateRim = candidateBlur * rimGrowth;
                if ((right - left) * candidate + 2f * candidateRim <= target.Width
                    && (bottom - top) * candidate + 2f * candidateRim + 1f <= target.Height - 8f)
                {
                    fit = candidate;
                    blurTexels = candidateBlur;
                    rimTexels = candidateRim;
                    break;
                }
            }
            if (fit <= 0f)
                return false;
            unbake = 1f / fit;
            left *= fit;
            right *= fit;
            top *= fit;
            bottom *= fit;
            feetInTarget = new Vector2(
                (float)Math.Round(target.Width * 0.5f - (left + right) * 0.5f),
                (float)Math.Round(target.Height - bottom - rimTexels - 1f));
            Matrix lean = projection.About(feetInTarget);
            RenderTargetBinding[] previous = graphicsDevice.GetRenderTargets();
            try
            {
                graphicsDevice.SetRenderTarget(target);
                graphicsDevice.Clear(Color.Transparent);
                // The upright bake is already a white, faded, premultiplied shape, so it is
                // carried across as it is: the projection is applied by the batch and the fit by
                // the draw, both about the feet. Linear sampling, because a silhouette turned
                // through an arbitrary angle at point sampling is a staircase down every edge.
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                    null, RasterizerState.CullNone, null, lean);
                _renderTargetSpriteBatch.Draw(upright, feetInTarget, sprite, Color.White, 0f, new Vector2(originX, originY), fit, SpriteEffects.None, 0f);
                _renderTargetSpriteBatch.End();
                // The rim as an object's is stamped: the sun's disc thrown along the shadow, held
                // to a third of each of the shadow's own two extents.
                var rim = new Vector2(
                    PenumbraHeldToShadow(blurTexels / rimGrowth, sprite.Width * fit * acrossPerWidth),
                    PenumbraHeldToShadow(blurTexels * rimGrowth, sprite.Height * fit * alongPerHeight));
                BlurSlotInPlace(graphicsDevice, target, blurTexels, feetInTarget,
                    new Vector2(projection.AlongX, projection.AlongY), alongPerHeight, rim);
                content = ContentBounds(feetInTarget, left, right, top, bottom, rimTexels, target.Width, target.Height);
                FrameCost.Count(FrameCost.Counter.CasterBakes);
                return true;
            }
            catch (Exception exception)
            {
                try { _renderTargetSpriteBatch!.End(); } catch { }
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] lay-down threw: {exception}", LogLevel.Warn); }
                return false;
            }
            finally
            {
                graphicsDevice.SetRenderTargets(previous);
            }
        }

        /// <summary>The scales a laid-down farmer silhouette is tried at, finest first. A person
        /// at the longest shadow the dials allow lies down to several times their own height,
        /// and the coarsest step here is what still fits a target of <see cref="PlayerSunRtSize"/>.</summary>
        private static readonly float[] LayDownScales = [1f, 0.75f, 0.5f, 0.25f];

        /// <summary>
        /// Ensure every on-screen NPC/animal sprite FRAME has an UPRIGHT baked silhouette in the
        /// persistent cache (black + feet→head alpha gradient), for the lamp path: each lamp leans
        /// and squashes the one bake its own way at draw time. The sun's cast of the same frame is
        /// laid down through the object pool instead (see <see cref="DrawNpcShadow"/>), so this
        /// only runs while a lamp may cast. Runs during RenderingWorld (render-target swaps are
        /// safe there). Warm frames are a dictionary hit — only frames never seen before bake.
        /// </summary>
        private void BakeCasters(GraphicsDevice graphicsDevice, GameLocation location, float blurPixels)
        {
            if (location == null)
                return;
            var viewport = Game1.viewport;
            int tileX0 = viewport.X / 64 - 3, tileX1 = (viewport.X + viewport.Width) / 64 + 3;
            int tileY0 = viewport.Y / 64 - 3, tileY1 = (viewport.Y + viewport.Height) / 64 + 3;

            RenderTargetBinding[]? previousTargets = null;   // fetched lazily: only a cache MISS pays for it
            try
            {
                foreach (NPC npc in CharactersIn(location))
                {
                    if (npc == null || npc.IsInvisible || ShadowHiddenFor(npc) || npc.swimming.Value || npc.Sprite?.Texture == null
                        || !CharacterCasts(npc))
                        continue;
                    Point tile = npc.TilePoint;
                    if (tile.X < tileX0 || tile.X > tileX1 || tile.Y < tileY0 || tile.Y > tileY1)
                        continue;
                    var key = (npc.Sprite.Texture, npc.Sprite.SourceRect);
                    // Stamping the tick on a HIT is what keeps eviction honest: this loop already
                    // walks exactly the casters on screen, so "seen here this frame" is the same
                    // question as "is this bake still wanted".
                    if (_casterBakeCache.TryGetValue(key, out SpriteBake? warm))
                    {
                        warm.LastUsedTick = SharedTicks.Now;
                        RefreshCasterBlur(graphicsDevice, key.Texture, key.SourceRect, warm, blurPixels, ref previousTargets);
                        continue;
                    }
                    previousTargets ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Texture, key.SourceRect, blurPixels, out RenderTarget2D renderTarget, out Vector2 feet))
                        _casterBakeCache[key] = new SpriteBake { Rt = renderTarget, FeetInRt = feet, BakedBlur = blurPixels, BakedContactHardness = ContactHardnessNow, BakedDepth = BakeDepthNow, BakedPenumbraStretch = PenumbraStretchNow, LastUsedTick = SharedTicks.Now };
                }
                foreach (FarmAnimal animal in AnimalsIn(location))
                {
                    if (animal?.Sprite?.Texture == null || !_castFarmAnimals)
                        continue;
                    Point tile = animal.TilePoint;
                    if (tile.X < tileX0 || tile.X > tileX1 || tile.Y < tileY0 || tile.Y > tileY1)
                        continue;
                    var key = (animal.Sprite.Texture, animal.Sprite.SourceRect);
                    if (_casterBakeCache.TryGetValue(key, out SpriteBake? warm))
                    {
                        warm.LastUsedTick = SharedTicks.Now;
                        RefreshCasterBlur(graphicsDevice, key.Texture, key.SourceRect, warm, blurPixels, ref previousTargets);
                        continue;
                    }
                    previousTargets ??= graphicsDevice.GetRenderTargets();
                    if (BakeSprite(graphicsDevice, key.Texture, key.SourceRect, blurPixels, out RenderTarget2D renderTarget, out Vector2 feet))
                        _casterBakeCache[key] = new SpriteBake { Rt = renderTarget, FeetInRt = feet, BakedBlur = blurPixels, BakedContactHardness = ContactHardnessNow, BakedDepth = BakeDepthNow, BakedPenumbraStretch = PenumbraStretchNow, LastUsedTick = SharedTicks.Now };
                }
            }
            catch (Exception exception)
            {
                if (DiagnosticMonitor != null && !_errorLogged) { _errorLogged = true; DiagnosticMonitor.Log($"[shadow] caster bake threw: {exception}", LogLevel.Warn); }
            }
            finally
            {
                if (previousTargets != null)
                    graphicsDevice.SetRenderTargets(previousTargets);
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
        private void RefreshCasterBlur(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle sourceRect, SpriteBake warm,
            float blurPixels, ref RenderTargetBinding[]? previousTargets)
        {
            if (Math.Abs(blurPixels - warm.BakedBlur) <= 0.3f && warm.BakedContactHardness == ContactHardnessNow && warm.BakedDepth == BakeDepthNow
                && warm.BakedPenumbraStretch == PenumbraStretchNow)
                return;
            previousTargets ??= graphicsDevice.GetRenderTargets();
            if (BakeSprite(graphicsDevice, texture, sourceRect, blurPixels, out _, out Vector2 feet, into: warm.Rt))
            {
                warm.FeetInRt = feet;
                warm.BakedBlur = blurPixels;
                warm.BakedContactHardness = ContactHardnessNow;
                warm.BakedDepth = BakeDepthNow;
                warm.BakedPenumbraStretch = PenumbraStretchNow;
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
        private bool BakeSprite(GraphicsDevice graphicsDevice, Texture2D texture, Rectangle sourceRect, float blurPixels,
            out RenderTarget2D renderTarget, out Vector2 feetInRenderTarget, RenderTarget2D? into = null)
        {
            renderTarget = null!;
            feetInRenderTarget = default;
            if (texture == null || sourceRect.IsEmpty)
                return false;
            float spriteWidth = sourceRect.Width * 4f, spriteHeight = sourceRect.Height * 4f;
            float blurTexels = Math.Max(0f, blurPixels);
            // The soft edge spreads the silhouette by the radius on every side; without the slack
            // it clips at the slot wall and the shadow's head comes out with a flat top.
            if (spriteWidth + 2f * blurTexels > CasterRtW || spriteHeight + blurTexels > CasterRtH - 8f)
                return false;

            renderTarget = into ?? RentCasterRT(graphicsDevice);
            var spriteTopLeft = new Vector2((CasterRtW - spriteWidth) / 2f, CasterRtH - spriteHeight - 8f);
            feetInRenderTarget = new Vector2(CasterRtW / 2f, CasterRtH - 8f);
            try
            {
                graphicsDevice.SetRenderTarget(renderTarget);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(texture, spriteTopLeft, sourceRect, Color.Black, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                _renderTargetSpriteBatch.End();
                WhitenBake(graphicsDevice, renderTarget.Bounds);

                // Fade only the sprite's vertical extent (full at the feet, faint at the head).
                _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(_gradientTexture!, new Rectangle(0, (int)spriteTopLeft.Y, CasterRtW, (int)spriteHeight), Color.White);
                _renderTargetSpriteBatch.End();
                // This slot serves the LAMPS: one upright bake, leant and squashed at draw time by
                // each lamp in the room in turn, so the rim is stamped round here and takes what
                // each lamp's draw does to it. The sun's cast of a person no longer comes from
                // here at all: it is laid down through the same projection as every object's,
                // skew and all, and stamped with no rotation (see DrawNpcShadow and
                // LayDownPlayerSun). Inside the slot the shadow runs from the feet toward the
                // head, straight up it.
                BlurSlotInPlace(graphicsDevice, renderTarget, blurTexels, feetInRenderTarget, new Vector2(0f, -1f));
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
                    _casterFreeTargets.Add(renderTarget);
                renderTarget = null!;
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
            var renderTarget = VramTally.Track(new RenderTarget2D(graphicsDevice, CasterRtW, CasterRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "character bake slots");
            _casterRenderTargetPool.Add(renderTarget);
            return renderTarget;
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
            foreach (var free in _objectFreeCellsByClass) free.Clear();
            _casterFreeTargets.Clear();
            int freed = ObjectSlotsAllocated() + _casterRenderTargetPool.Count;
            for (int i = 0; i < _objectBakeScratches.Length; i++)
            {
                try { _objectBakeScratches[i]?.Dispose(); } catch { }
                _objectBakeScratches[i] = null;
            }
            foreach (RenderTarget2D page in _objectAtlasPages)
                try { page.Dispose(); } catch { }
            _objectAtlasPages.Clear();
            _objectCellsOpen = 0;
            _objectSharedPageOpen = false;
            // The pools' stacks may sit on the first shared page, which is gone now.
            _objectPoolStripPage = null;
            ForgetStackedPools();
            foreach (RenderTarget2D renderTarget in _casterRenderTargetPool)
                try { renderTarget.Dispose(); } catch { }
            _casterRenderTargetPool.Clear();
            for (int i = 0; i < _objectBlurScratches.Length; i++)
            {
                try { _objectBlurScratches[i]?.Dispose(); } catch { }
                _objectBlurScratches[i] = null;
            }
            try { _casterBlurScratch?.Dispose(); } catch { }
            _casterBlurScratch = null;
            try { _playerSunBlurScratch?.Dispose(); } catch { }
            _playerSunBlurScratch = null;
            // A full re-enumeration has to happen if the shadows come back, or the draw pass
            // would find every sprite missing and paint a screen of banded stand-ins.
            ForgetObjectBakeLocations();
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
            int coldBefore = SharedTicks.Now - HotBakeTicks;
            foreach (var entry in _casterBakeCache)
            {
                if (desperate || entry.Value.LastUsedTick < coldBefore)
                    _casterEvictScratch.Add(entry.Key);
            }
            _casterEvictScratch.Sort((first, second) => _casterBakeCache[first].LastUsedTick.CompareTo(_casterBakeCache[second].LastUsedTick));
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
            foreach (var entry in _bakedObjectCache)
                if (entry.Value.Slot != null)
                    _objectFreeCellsByClass[entry.Value.SlotClass].Add(entry.Value.Slot);
            _bakedObjectCache.Clear();
            _objectBakeQueue.Clear();
            ForgetObjectBakeLocations();
        }

        private void EvictColdObjectBakes()
        {
            // One walk to count every class, not one walk per class: this runs every frame, and
            // on a farm holding four hundred bakes three walks to learn that nothing is over its
            // cap were most of what the method did.
            Array.Clear(_objectLiveByClass, 0, _objectLiveByClass.Length);
            foreach (var entry in _bakedObjectCache)
                _objectLiveByClass[entry.Value.SlotClass]++;
            for (int slotClass = 0; slotClass < ObjectSlotClasses.Length; slotClass++)
            {
                int cap = ObjectClassCap(slotClass);
                int live = _objectLiveByClass[slotClass];
                if (live <= cap)
                    continue;

                _objectEvictScratch.Clear();
                int keep = (int)(cap * EvictHeadroom);
                bool desperate = live > cap * 2;
                int coldBefore = SharedTicks.Now - HotBakeTicks;
                foreach (var entry in _bakedObjectCache)
                {
                    if (entry.Value.SlotClass != slotClass) continue;
                    if (desperate || entry.Value.LastUsedTick < coldBefore)
                        _objectEvictScratch.Add(entry.Key);
                }
                _objectEvictScratch.Sort((first, second) => _bakedObjectCache[first].LastUsedTick.CompareTo(_bakedObjectCache[second].LastUsedTick));
                int drop = Math.Min(live - keep, _objectEvictScratch.Count);
                for (int i = 0; i < drop; i++)
                {
                    if (_bakedObjectCache.TryGetValue(_objectEvictScratch[i], out SpriteBake? bake))
                    {
                        if (bake.Slot != null)
                            _objectFreeCellsByClass[bake.SlotClass].Add(bake.Slot);
                        _bakedObjectCache.Remove(_objectEvictScratch[i]);
                        FrameCost.Count(FrameCost.Counter.BakeEvictions);
                    }
                }
            }
        }

        /// <summary>A 64×64 soft radial disc (white, radial alpha) for ambient contact pools.</summary>
        private static Texture2D BuildBlob(GraphicsDevice graphicsDevice)
        {
            const int BlobSize = 64;
            var texture = new Texture2D(graphicsDevice, BlobSize, BlobSize);
            var data = new Color[BlobSize * BlobSize];
            float radius = BlobSize / 2f;
            for (int y = 0; y < BlobSize; y++)
            {
                for (int x = 0; x < BlobSize; x++)
                {
                    float offsetX = (x + 0.5f - radius) / radius, offsetY = (y + 0.5f - radius) / radius;
                    float distance = (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
                    float rimAlpha = MathHelper.Clamp(1f - distance, 0f, 1f);
                    rimAlpha *= rimAlpha;   // soft falloff toward the rim
                    // Premultiplied, like every bake (see Whiten). The game's batch blends
                    // src + dst * (1 - src.a), so a texel carrying colour where it has no alpha
                    // ADDS that colour to whatever is under it and takes nothing away. White at
                    // full strength with the alpha doing the shaping made the whole square of the
                    // texture add the ink's colour outside the ellipse: invisible while the ink was
                    // black, and a pale box round every pool once the ink took the sky's colour.
                    byte level = (byte)(rimAlpha * 255f);
                    data[y * BlobSize + x] = new Color(level, level, level, level);
                }
            }
            texture.SetData(data);
            return texture;
        }

        /// <summary>The contact pool's five copies stacked, for one spread: where on the page.</summary>
        private sealed class StackedPool
        {
            public Texture2D Page = null!;
            public Rectangle Place;
            public Vector2 Origin;
        }

        /// <summary>A page of the pools' own, for a frame with no shared object page to put them
        /// on (a room with pools and no object shadows).</summary>
        private Texture2D? _stackedPoolOwnPage;
        private const int StackedPoolOwnPageSide = 1024;
        /// <summary>Where the stacks are being written: the strip of the first shared object page,
        /// or the pools' own page, and that area; the shelf cursors are inside it.</summary>
        private Texture2D? _stackedPoolPage;
        private Rectangle _stackedPoolArea;
        private int _stackedPoolShelfX, _stackedPoolShelfY, _stackedPoolShelfHeight;
        /// <summary>A texel of clear ground round every stack, so a pool's edge never reads its
        /// neighbour's.</summary>
        private const int StackedPoolGutter = 1;

        /// <summary>Stacks by spread, in quarter texels of the 64-texel blob on each axis.</summary>
        private readonly Dictionary<(int, int), StackedPool> _stackedPools = [];
        /// <summary>Spreads asked for inside the game's batch, made before the next frame's draws.</summary>
        private readonly HashSet<(int, int)> _stackedPoolsWanted = [];
        /// <summary>A pool spread wider than this many blob texels is drawn the old way.</summary>
        private const float StackedPoolMaxSpread = 32f;
        /// <summary>Plenty for the handful of pool widths a scene holds; past it the stacks are
        /// dropped and made again as they are next drawn.</summary>
        private const int StackedPoolCap = 160;
        /// <summary>The per-copy opacity the stack is shaped at: the copies' union is not linear in
        /// their opacity, so one shape is exact at one strength. A contact pool is drawn near half
        /// strength, and there the rim matches; at the core the stack matches at every strength.</summary>
        private const float StackedPoolReferenceAlpha = 0.5f;

        private StackedPool? StackedBlobFor(float spreadTexelsX, float spreadTexelsY)
        {
            if (spreadTexelsX > StackedPoolMaxSpread || spreadTexelsY > StackedPoolMaxSpread)
                return null;
            var key = ((int)Math.Round(spreadTexelsX * 4f), (int)Math.Round(spreadTexelsY * 4f));
            if (_stackedPools.TryGetValue(key, out StackedPool? stacked))
                return stacked;
            _stackedPoolsWanted.Add(key);
            return null;
        }

        /// <summary>Make the stacks the last frame asked for. Called where a texture upload is
        /// safe, before the game's world batch opens (see PreparePlayer).</summary>
        private void MakeWantedStackedPools(GraphicsDevice graphicsDevice)
        {
            if (_stackedPoolsWanted.Count == 0 && _stackedPools.Count == 0)
                return;
            // On the shared object page when there is one, so a pool and the shadows round it are
            // one texture; the stacks already made elsewhere are made again there. Asked while any
            // stack exists, not only when one is wanted: pools all made before the shared page
            // opened would otherwise stay on a texture of their own for good.
            Texture2D page;
            Rectangle area;
            if (_objectPoolStripPage is { IsDisposed: false } strip)
            {
                page = strip;
                area = _objectPoolStrip;
            }
            else
            {
                _stackedPoolOwnPage ??= VramTally.Track(new Texture2D(graphicsDevice, StackedPoolOwnPageSide, StackedPoolOwnPageSide), "shadow contact pools");
                page = _stackedPoolOwnPage;
                area = new Rectangle(0, 0, StackedPoolOwnPageSide, StackedPoolOwnPageSide);
            }
            if (!ReferenceEquals(page, _stackedPoolPage))
            {
                ForgetStackedPools();
                _stackedPoolPage = page;
                _stackedPoolArea = area;
                // Moved onto the shared page: the pools' own page holds nothing any more.
                if (!ReferenceEquals(page, _stackedPoolOwnPage) && _stackedPoolOwnPage != null)
                {
                    _stackedPoolOwnPage.Dispose();
                    _stackedPoolOwnPage = null;
                }
            }
            if (_stackedPoolsWanted.Count == 0)
                return;
            if (_stackedPools.Count + _stackedPoolsWanted.Count > StackedPoolCap)
                ForgetStackedPools();
            foreach (var key in _stackedPoolsWanted)
            {
                // A full area keeps what it has: a spread that does not fit is drawn as its five
                // copies, as before. Starting the area again would make the same stacks every
                // frame in a scene with more pool sizes than it holds.
                if (BuildStackedBlob(key.Item1 / 4f, key.Item2 / 4f) is { } made)
                    _stackedPools[key] = made;
            }
            _stackedPoolsWanted.Clear();
        }

        private void ForgetStackedPools()
        {
            _stackedPools.Clear();
            _stackedPoolShelfX = _stackedPoolShelfY = _stackedPoolShelfHeight = 0;
        }

        /// <summary>The blob of <see cref="BuildBlob"/> drawn five times, at its centre and a spread
        /// to each side, the way <see cref="DrawSoft"/> stacks the Taps5 copies, as one premultiplied
        /// texture padded by the spread, written into its place on the page. Normalised so the core,
        /// where every copy covers, is 1. Null when the page has no room left.</summary>
        private StackedPool? BuildStackedBlob(float spreadX, float spreadY)
        {
            const int BlobSize = 64;
            const float Radius = BlobSize / 2f;
            int padX = (int)Math.Ceiling(spreadX), padY = (int)Math.Ceiling(spreadY);
            int width = BlobSize + 2 * padX, height = BlobSize + 2 * padY;
            int placedWidth = width + 2 * StackedPoolGutter, placedHeight = height + 2 * StackedPoolGutter;
            if (_stackedPoolShelfX + placedWidth > _stackedPoolArea.Width)
            {
                _stackedPoolShelfY += _stackedPoolShelfHeight;
                _stackedPoolShelfX = 0;
                _stackedPoolShelfHeight = 0;
            }
            if (_stackedPoolShelfY + placedHeight > _stackedPoolArea.Height || _stackedPoolPage == null)
                return null;
            var place = new Rectangle(_stackedPoolArea.X + _stackedPoolShelfX + StackedPoolGutter,
                _stackedPoolArea.Y + _stackedPoolShelfY + StackedPoolGutter, width, height);
            _stackedPoolShelfX += placedWidth;
            _stackedPoolShelfHeight = Math.Max(_stackedPoolShelfHeight, placedHeight);
            var data = new Color[placedWidth * placedHeight];
            float copyAlpha = 1f - (float)Math.Pow(1f - StackedPoolReferenceAlpha, 1.0 / Taps5.Length);
            float core = 1f - (float)Math.Pow(1f - copyAlpha, Taps5.Length);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float fromCentreX = x + 0.5f - width / 2f, fromCentreY = y + 0.5f - height / 2f;
                    float clear = 1f;
                    foreach (Vector2 tap in Taps5)
                    {
                        float offsetX = (fromCentreX - tap.X * spreadX) / Radius, offsetY = (fromCentreY - tap.Y * spreadY) / Radius;
                        float rimAlpha = MathHelper.Clamp(1f - (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY), 0f, 1f);
                        clear *= 1f - copyAlpha * rimAlpha * rimAlpha;
                    }
                    byte level = (byte)Math.Round(MathHelper.Clamp((1f - clear) / core, 0f, 1f) * 255f);
                    data[(y + StackedPoolGutter) * placedWidth + x + StackedPoolGutter] = new Color(level, level, level, level);
                }
            }
            // The gutter is written too, as clear ground, over whatever an earlier fill left there.
            _stackedPoolPage.SetData(0, new Rectangle(place.X - StackedPoolGutter, place.Y - StackedPoolGutter, placedWidth, placedHeight),
                data, 0, data.Length);
            return new StackedPool { Page = _stackedPoolPage, Place = place, Origin = new Vector2(width / 2f, height / 2f) };
        }

        /// <summary>1×H alpha ramp: 1.0 at the bottom (feet) fading to <paramref name="headFade"/> at the top (far tip).</summary>
        /// <summary>What the one feet-to-tip fade is worth a given share of the way up a caster.
        /// The same curve <see cref="BuildGradient"/> bakes, evaluated on the CPU, so a piece that
        /// covers only the bottom of a caster can be told where its own fade has to stop and the
        /// next piece's has to start. Without a shared answer the two pieces each run the whole
        /// ramp and meet at a step.</summary>
        internal static float FadeAtShareFromFeet(float share01)
            => HeadFade + (1f - HeadFade) * (float)Math.Pow(MathHelper.Clamp(1f - share01, 0f, 1f), 1.8);

        private static Texture2D BuildGradient(GraphicsDevice graphicsDevice, float headFade = HeadFade)
        {
            var texture = new Texture2D(graphicsDevice, 1, PlayerRtH);
            var data = new Color[PlayerRtH];
            for (int y = 0; y < PlayerRtH; y++)
            {
                float bottomFraction = (float)y / (PlayerRtH - 1);      // 0 at top, 1 at bottom
                // Non-linear: stays dark near the feet, fades toward the far tip.
                float rampAlpha = headFade + (1f - headFade) * (float)Math.Pow(bottomFraction, 1.8);
                data[y] = new Color(255, 255, 255, (int)(rampAlpha * 255f));
            }
            texture.SetData(data);
            return texture;
        }

        // Discs of offset taps → cheap soft edge. Weighted so overlapping translucent copies
        // reach the target opacity at the core while feathering the rim. The player (one RT
        // draw) can afford 9 taps; NPC bands use the lighter 5 to keep the draw count sane.
        private static readonly Vector2[] Taps9 =
        [
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
            new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f),
        ];
        private static readonly Vector2[] Taps5 =
        [
            new(0f, 0f), new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        ];
        /// <summary>
        /// How soft the shadow is a given fraction of the way from the caster's feet to the tip,
        /// as a multiple of the full blur radius.
        ///
        /// <para>
        /// The sun is a disc, not a point, so a shadow's edge is a penumbra whose width grows with
        /// the gap between the caster and the ground the shadow lands on. At the contact that gap
        /// is nothing and the edge is sharp; it opens out from there, and it is what tells the eye
        /// the shadow is lying on the ground rather than painted on it. Every release before this
        /// one used ONE radius for the whole length, which is the one shape the real thing never
        /// takes: it rubbed out the narrow near end (a tree's trunk, a post) while leaving the far
        /// end too crisp.
        /// </para>
        ///
        /// <para>
        /// Linear in the distance, which is what the geometry gives: one pixel of penumbra per
        /// hundred-odd pixels of gap, whatever the sun's height. The hardness dial says how much
        /// of that to spend; a floor remains at the contact because the silhouette is drawn from
        /// art four times the size of its texels, and a perfectly hard edge there is a staircase.
        /// </para>
        /// </summary>
        /// <param name="alongShadow01">0 at the feet, 1 at the tip. Values outside are clamped.</param>
        internal static float PenumbraScaleAt(float alongShadow01)
        {
            float hardness = MathHelper.Clamp(ContactHardnessNow, 0f, 1f);
            if (hardness <= 0f)
                return 1f;
            return (1f - hardness) + hardness * MathHelper.Clamp(alongShadow01, 0f, 1f);
        }

        /// <summary>
        /// The blur radius a given point along the shadow gets, never sharper than the art it was
        /// stamped from can express.
        ///
        /// <para>
        /// A silhouette is drawn at four times its own texels, so its edge is a four-step
        /// staircase before anything softens it. Softening by less than about half a source pixel
        /// leaves that staircase standing, and a staircase is what the eye reads as the shadow
        /// being drawn rather than cast - which is the opposite of what a hard contact is for. The
        /// floor never RAISES the softness above what was asked for: a shadow set crisp on purpose
        /// stays crisp end to end.
        /// </para>
        /// </summary>
        /// <param name="blur">The full radius for this shadow, in whatever unit the caller stamps
        /// in: screen pixels for the strip paths, slot texels for a bake.</param>
        /// <param name="alongShadow01">0 at the feet, 1 at the tip.</param>
        internal static float PenumbraRadiusAt(float blur, float alongShadow01)
            => Math.Max(Math.Min(blur, SharpestEdge), blur * PenumbraScaleAt(alongShadow01));

        /// <summary>Half a source pixel of the art a silhouette is stamped from, which is stamped
        /// at four times its texels. See <see cref="PenumbraRadiusAt"/>.</summary>
        private const float SharpestEdge = 2f;

        /// <summary>
        /// A soft edge held to what the shadow it is softening can carry.
        ///
        /// <para>
        /// A shadow laid down sideways is squashed to a third of its width by the ground's own
        /// slant, and a soft edge of the same radius as ever then reaches clean across it from
        /// both sides: the dark middle is eaten from both edges at once and the shadow DISSOLVES
        /// as the sun passes a quarter turn. The blur dial is set for a shadow lying the long way;
        /// nothing warns it when the shadow it is applied to has become a ribbon.
        /// </para>
        ///
        /// <para>
        /// So the radius is capped at a share of the extent it is softening. A third leaves a
        /// third of the width as untouched dark whatever the sun does, which is a shadow; a half
        /// leaves nothing at the middle, which is a smudge.
        /// </para>
        /// </summary>
        /// <param name="extentTexels">How wide the shadow is across the axis this radius softens.</param>
        internal static float PenumbraHeldToShadow(float radiusTexels, float extentTexels)
            => Math.Min(radiusTexels, Math.Max(0.5f, extentTexels * PenumbraShareOfShadow));

        /// <summary>The most of a shadow's own width a soft edge may eat from one side. See
        /// <see cref="PenumbraHeldToShadow"/>.</summary>
        private const float PenumbraShareOfShadow = 1f / 3f;

        /// <summary>
        /// The width scale a SpriteBatch can actually be handed, with the sign spent as a flip.
        ///
        /// <para>
        /// Past a quarter turn a solid's width points the other way round the shadow's own axis,
        /// and that is a real part of the answer rather than a rounding wobble. It cannot be
        /// handed over as a negative scale: SpriteBatch works the quad's size out as the source
        /// size times the scale, so a negative one winds it backwards and the game's own batch
        /// culls it, and the shadow simply disappears. A flip only swaps which end of the texture
        /// each corner samples, which is the same picture and a quad the right way round.
        /// </para>
        ///
        /// <para>
        /// The mirror is exact only because every slot this is used on is pinned bottom-CENTRE:
        /// the origin's mirror image inside the sprite is the origin. A slot pinned anywhere else
        /// would have to mirror its origin too, the way the map-tile columns mirror theirs.
        /// </para>
        /// </summary>
        private static float LaidDownWidth(float across, SpriteEffects effects, out SpriteEffects laidDown)
        {
            laidDown = across < 0f ? effects ^ SpriteEffects.FlipHorizontally : effects;
            return Math.Abs(across);
        }

        /// <summary>The contact-hardness dial, captured once per frame beside the golden hour and
        /// the sun's bearing, and for the same reason: the bake paths are static.</summary>
        internal static float ContactHardnessNow;

        /// <summary>How far past a strength of 1 the baked soft edges are deepened, captured once per
        /// frame for the same reason as <see cref="ContactHardnessNow"/>. 1 below a strength of 1.
        ///
        /// <para>A baked shadow's soft edge lives in the bake's pixels and the bake is drawn in one go,
        /// where ShadowDepthPower takes the draw's opacity a to 1 - (1 - a)^power: the core deepens
        /// and every edge pixel only in proportion. The bake's blur is a sum of nine copies carrying
        /// a ninth each; carrying more makes every edge pixel min(1, depth x what it was), for not
        /// one draw more, and at 1 exactly the old bake.</para>
        ///
        /// <para>The first cut carried the strength itself here AND let the draw raise the opacity,
        /// so a baked shadow was deepened twice: the feet-to-head fade clipped to full over its
        /// lower two thirds at 3, a banded shadow darkened visibly when its bake landed, and the
        /// blur dial moving off 0 changed how dark shadows were. See <see cref="BakeDepthFor"/>.</para></summary>
        internal static float BakeDepthNow = 1f;

        /// <summary>The opacity a shadow is drawn at, at a strength of 1, that <see cref="BakeDepthFor"/>
        /// matches: the building shadow's, and the sun's at full.</summary>
        private const float NominalShadowOpacity = 0.7f;

        /// <summary>The bake depth that, times the draw's own deepening, gives a faint edge pixel what
        /// the strength asks of it. A pixel covered at c by a shadow of opacity a should end up at
        /// 1 - (1 - a c)^power, which for a faint edge is power x a x c; the draw already gives it
        /// (1 - (1 - a)^power) x c, so the bake carries the rest: power x a / (1 - (1 - a)^power).
        /// 1 at a strength of 1, about 2.2 at 3. Deeper pixels saturate, as the exponent itself does.</summary>
        internal static float BakeDepthFor(float strength)
        {
            float power = MathHelper.Clamp(strength, 1f, ModConfig.ShadowStrengthMax);
            float drawn = 1f - MathF.Pow(1f - NominalShadowOpacity, power);
            return power * NominalShadowOpacity / drawn;
        }

        /// <summary>The penumbra-shape dial, captured once per frame for the same reason as
        /// <see cref="ContactHardnessNow"/>. 0 is the round soft edge of every earlier release.</summary>
        internal static float PenumbraStretchNow;

        /// <summary>
        /// How many times longer a shadow's soft edge is ALONG the shadow than it is across, for a
        /// shadow of this length.
        ///
        /// <para>
        /// The sun is a disc, so the soft rim is that disc thrown onto the ground, and a disc
        /// thrown onto a surface at a slant is an ellipse whose long axis is one over the sine of
        /// the angle the light makes with that surface. Writing the shadow's length per unit of
        /// caster height as the cotangent of that angle turns the ratio into the square root of
        /// one plus the length squared, so this number is already contained in the shadow's own
        /// projection and needs nothing tuned by eye. A round rim is what a sun straight overhead
        /// casts, and the sun is never straight overhead here.
        /// </para>
        ///
        /// <para>
        /// The cap is what the bake slots can hold: a slot reserves two blur radii of slack on
        /// each side, and an area-preserving ellipse of this ratio reaches the square root of the
        /// ratio plus its reciprocal, which stays inside two up to about three and three quarters.
        /// The sun in this mod goes to roughly three at the deepest golden hour, so the cap is
        /// headroom rather than a limit that is reached.
        /// </para>
        /// </summary>
        /// <param name="shadowLengthPerHeight">Screen pixels of shadow per source pixel of caster
        /// height: the length of a projection's along vector. 0 means the caller does not know,
        /// and the rim stays round.</param>
        internal static float PenumbraElongation(float shadowLengthPerHeight)
        {
            float dial = MathHelper.Clamp(PenumbraStretchNow, 0f, 1f);
            if (dial <= 0f || shadowLengthPerHeight <= 0f)
                return 1f;
            float physical = (float)Math.Sqrt(1f + shadowLengthPerHeight * shadowLengthPerHeight);
            return MathHelper.Lerp(1f, Math.Min(physical, 3.5f), dial);
        }

        private static void DrawSoft(SpriteBatch spriteBatch, Vector2[] taps, Texture2D texture, Rectangle? sourceRect, Vector2 feet,
            Color baseColor, float alpha, float rotation, Vector2 origin, Vector2 scale, float depth,
            SpriteEffects effects, float blur, float shadowLengthPerHeight = 0f)
        {
            // No blur → one draw at full alpha (the tap disc would just stack N identical
            // copies on the same pixel, costing N× the draw calls for nothing).
            if (blur <= 0f)
            {
                FrameCost.Count(FrameCost.Counter.ShadowDrawCalls);
                spriteBatch.Draw(texture, feet, sourceRect,
                    baseColor * (1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), ShadowDepthPower)),
                    rotation, origin, scale, effects, depth);
                return;
            }

            // Per-tap alpha so 1-(1-a)^N ≈ target alpha at the fully-covered core, deepened by
            // ShadowDepthPower past a strength of 1 (see there): the same stack, fainter or darker copies.
            float tapAlpha = 1f - (float)Math.Pow(1f - MathHelper.Clamp(alpha, 0f, 1f), ShadowDepthPower / taps.Length);
            Color tapColor = baseColor * tapAlpha;
            FrameCost.Count(FrameCost.Counter.ShadowDrawCalls, taps.Length);
            // These offsets are SCREEN pixels, so the rim's shape can be stamped straight into
            // them: no slot to run out of, and nothing cached to go stale. The shadow runs up the
            // sprite and is turned by the same rotation the draw uses, so that is where along is.
            float root = (float)Math.Sqrt(PenumbraElongation(shadowLengthPerHeight));
            if (root <= 1f)
            {
                foreach (Vector2 tap in taps)
                    spriteBatch.Draw(texture, feet + tap * blur, sourceRect, tapColor, rotation, origin, scale, effects, depth);
                return;
            }
            float alongX = (float)Math.Sin(rotation), alongY = -(float)Math.Cos(rotation);
            float alongRadius = blur * root, acrossRadius = blur / root;
            foreach (Vector2 tap in taps)
            {
                float along = (tap.X * alongX + tap.Y * alongY) * alongRadius;
                float across = (tap.Y * alongX - tap.X * alongY) * acrossRadius;
                var offset = new Vector2(along * alongX - across * alongY, along * alongY + across * alongX);
                spriteBatch.Draw(texture, feet + offset, sourceRect, tapColor, rotation, origin, scale, effects, depth);
            }
        }

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
        private static readonly List<Rectangle> BuildingFootprints = [];

        /// <summary>Collect the buildings' footprints for this pass. Cheap: a location owns a
        /// handful, and the list is what lets every strip of every shadow answer "am I lying on a
        /// building" without walking the buildings itself.</summary>
        private static void RefreshBuildingFootprints(GameLocation? location)
        {
            BuildingFootprints.Clear();
            if (location?.buildings == null)
                return;
            foreach (Building building in location.buildings)
            {
                if (building == null)
                    continue;
                BuildingFootprints.Add(new Rectangle(building.tileX.Value * 64, building.tileY.Value * 64,
                    building.tilesWide.Value * 64, building.tilesHigh.Value * 64));
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
            float rotation, float scaleY, float lengthTexels)
        {
            long clipStep = RenderPipeline.ChainStepBegin();
            float distance = WalkToShadowClip(location, feetWorldX, anchorWorldY, rotation, scaleY, lengthTexels);
            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.ShadowClip, clipStep);
            return distance;
        }

        /// <summary>The walk behind <see cref="ShadowClipDistance"/>, kept apart so the wrapper can
        /// time it: it runs for every lit character shadow on every frame and had no row of its own.
        /// </summary>
        private static float WalkToShadowClip(GameLocation? location, float feetWorldX, float anchorWorldY,
            float rotation, float scaleY, float lengthTexels)
        {
            if (location == null)
                return float.MaxValue;
            float leanSin = (float)Math.Sin(rotation), leanCos = (float)Math.Cos(rotation);
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
            bool towardViewer = leanCos < 0f;
            int feetTileX = (int)Math.Floor(feetWorldX / 64f), feetTileY = (int)Math.Floor(anchorWorldY / 64f);
            bool SolidAt(float distance)
            {
                int tileX = (int)Math.Floor((feetWorldX + leanSin * distance) / 64f);
                int tileY = (int)Math.Floor((anchorWorldY - leanCos * distance) / 64f);
                // The tile under a pair of feet is never the thing that cuts their shadow.
                if (tileX == feetTileX && tileY == feetTileY)
                    return false;
                return location.hasTileAt(tileX, tileY, "Buildings")
                    && location.doesTileHaveProperty(tileX, tileY, "Passable", "Buildings") == null;
            }
            float near = -1f, far = -1f, previous = 0f;
            for (float distance = step; distance < lengthPixels; distance += step)
            {
                if (SolidAt(distance))
                {
                    if (near < 0f)
                    {
                        // The edge lies between the last clear sample and this one. Standing
                        // against the counter the feet are a dozen pixels from it, so a sample's
                        // width of slack was a visible spill of shadow onto its front: bisect to
                        // within a pixel of the tile's edge instead.
                        float clear = previous, solid = distance;
                        for (int i = 0; i < 4; i++)
                        {
                            float mid = (clear + solid) * 0.5f;
                            if (SolidAt(mid)) solid = mid; else clear = mid;
                        }
                        near = solid;
                    }
                    far = distance;
                }
                else if (far >= 0f)
                    break;
                previous = distance;
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
        private static void DrawSoftGrounded(SpriteBatch spriteBatch, Vector2[] taps, Texture2D texture, Rectangle? sourceRect,
            Vector2 feet, Color baseColor, float alpha, float rotation, Vector2 origin, Vector2 scale, float anchorWorldY,
            SpriteEffects effects, float blur, bool anchorIsSortDepth = false, float shadowLengthPerHeight = 0f,
            float? laidDownLean = null)
        {
            // A slot laid down by its projection carries the lean in its pixels and is stamped
            // with no rotation, so its rows are screen rows already. What it cannot say is which
            // way the shadow RUNS, which the wall test and the building rule both need, so the
            // caller hands over the lean the pixels were laid down with.
            bool laidDown = laidDownLean.HasValue;
            float lean = laidDownLean ?? rotation;
            float leanCos = (float)Math.Cos(lean), leanSin = (float)Math.Sin(lean);
            // Only the part of the silhouette's length that runs along the screen's Y moves it to
            // another floor row. The sideways lean moves it along the row it is already on, which
            // no sort depth has an opinion about. Signed, because a lamp overhead throws the
            // shadow DOWN the screen and those pieces belong in front of the caster. A laid-down
            // slot's rows are screen rows, one texel each at the draw's scale.
            float upScreenPerTexel = (laidDown ? 1f : leanCos) * scale.Y;
            float feetWorldX = feet.X + Game1.viewport.X;
            Rectangle area = sourceRect ?? new Rectangle(0, 0, texture.Width, texture.Height);
            // Where a solid map tile ends the shadow, the silhouette itself is cut there, from its
            // tip end, BEFORE the strips are decided. Skipping strips alone left the short shadows
            // untouched: a lamp's cast is often under one strip long, took the single-draw path
            // below, and went on through the counter whole.
            // A laid-down slot's reach along the lean is its rows over the cosine; the wall comes
            // back as a distance along the lean and is turned into rows the same way.
            float clipDistance = anchorIsSortDepth ? float.MaxValue
                : ShadowClipDistance(Game1.currentLocation, feetWorldX, anchorWorldY, lean, scale.Y,
                    laidDown ? area.Height / Math.Max(0.2f, Math.Abs(leanCos)) : origin.Y);
            if (clipDistance < float.MaxValue)
            {
                if (laidDown)
                    clipDistance *= Math.Abs(leanCos);
                // The soft edge is drawn as taps offset by the blur radius in every direction, so
                // the silhouette must end a blur's width short of the tile for its softness to end
                // AT the tile rather than a few pixels onto it; and a texel more for the rounding.
                float clipInsideBlur = Math.Max(0f, clipDistance - blur - scale.Y);
                if (laidDown && leanCos < 0f)
                {
                    // The tip lies BELOW the feet in a laid-down slot pointing down the screen, so
                    // the cut comes off the bottom: the rows past the wall are dropped and the
                    // origin, which names the top, stays where it is.
                    int keepBelow = (int)Math.Floor(clipInsideBlur / Math.Max(scale.Y, 0.001f));
                    int keptHeight = (int)Math.Ceiling(origin.Y) + keepBelow;
                    if (keptHeight <= 0)
                        return;
                    if (keptHeight < area.Height)
                        area = new Rectangle(area.X, area.Y, area.Width, keptHeight);
                }
                else
                {
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
            }
            // A laid-down slot has shadow on both sides of the feet when the sun is low behind the
            // caster, so its whole height decides the strips, not only the part above the feet.
            float alongScreenY = laidDown ? area.Height * scale.Y : Math.Abs(origin.Y * upScreenPerTexel);
            int strips = (int)MathHelper.Clamp(alongScreenY / GroundStripPixels, 1f, MaxGroundStrips);
            if (strips <= 1 || area.Height < strips * 2)
            {
                // One strip is a shadow barely longer than its own contact, so it takes the
                // softness of its middle rather than a tip's.
                DrawSoft(spriteBatch, taps, texture, area, feet, baseColor, alpha, rotation, origin, scale,
                    anchorIsSortDepth ? ShadowPieceDepthUnder(anchorWorldY, 0f)
                                      : ShadowPieceDepth(anchorWorldY, 0f), effects, PenumbraRadiusAt(blur, 0.5f), shadowLengthPerHeight);
                return;
            }
            for (int i = 0; i < strips; i++)
            {
                int stripTop = area.Height * i / strips;
                int stripBottom = area.Height * (i + 1) / strips;
                var strip = new Rectangle(area.X, area.Y + stripTop, area.Width, stripBottom - stripTop);
                // The origin has to keep naming the same feet row, so it rises with the strip -
                // the same correction the banded gradient makes for its bands.
                var stripOrigin = new Vector2(origin.X, origin.Y - stripTop);
                float texelsAboveFeet = origin.Y - (stripTop + stripBottom) * 0.5f;
                // Past a solid map tile the shadow is over (see ShadowClipDistance). Either side
                // of the feet counts in a laid-down slot, whose tip may lie below them.
                if ((laidDown ? Math.Abs(texelsAboveFeet) : texelsAboveFeet) * scale.Y > clipDistance)
                    continue;
                float upScreen = texelsAboveFeet * upScreenPerTexel;
                // Where the strip's centre lands sideways, for the building test: the lean moves
                // a piece along its row as well as up the screen. In a laid-down slot the row is
                // known and the lean says how far along it the shadow's own axis has got.
                float sideways = laidDown
                    ? (Math.Abs(leanCos) > 0.05f
                        ? MathHelper.Clamp(upScreen * leanSin / leanCos, -area.Width * scale.X, area.Width * scale.X)
                        : 0f)
                    : texelsAboveFeet * leanSin * scale.Y;
                // Each strip is already a slice at a known distance from the feet, so the penumbra
                // ramp costs nothing here: it is the radius this draw was going to make anyway.
                DrawSoft(spriteBatch, taps, texture, strip, feet, baseColor, alpha, rotation, stripOrigin, scale,
                    anchorIsSortDepth ? ShadowPieceDepthUnder(anchorWorldY, upScreen)
                                      : GroundedPieceDepth(anchorWorldY, upScreen, feetWorldX, sideways), effects,
                    PenumbraRadiusAt(blur, texelsAboveFeet / Math.Max(1f, origin.Y)), shadowLengthPerHeight);
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
        /// <param name="shadowColor">What the bands are stamped in. The frame's <see cref="ShadowInk"/>
        /// on the world (black at dial 0, the sky's fill above it); WHITE when the caller is
        /// filling a coverage mask that a later pass reads as "how much of this pixel is in
        /// shadow", where black would read as nothing at all.</param>
        private void DrawBandedGradient(SpriteBatch spriteBatch, Texture2D texture, Rectangle sourceRect, Vector2 feet,
            Vector2 baseOrigin, float alpha, float rotation, Vector2 scale, float anchorWorldY, float blur,
            float headFade = HeadFade, SpriteEffects effects = SpriteEffects.None,
            bool anchorIsSortDepth = false, Color? shadowColor = null, float shadowLengthPerHeight = 0f)
        {
            Color bandColor = shadowColor ?? ShadowInk;
            // The bands are already cut across the shadow's length, so each one can be sorted at
            // the depth of the floor row it lies on for nothing (see ShadowPieceDepth). Only the
            // part of the lean that runs along the screen's Y changes a band's row, and its sign
            // matters: a lamp overhead lays the shadow down the screen rather than up it.
            float upScreenPerTexel = (float)Math.Cos(rotation) * scale.Y;
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
            int bands = (int)MathHelper.Clamp(sourceRect.Height / 2f, 12f, 28f);
            float feetWorldX = feet.X + Game1.viewport.X;
            float clipDistance = anchorIsSortDepth ? float.MaxValue
                : ShadowClipDistance(Game1.currentLocation, feetWorldX, anchorWorldY, rotation, scale.Y, feetRow);
            for (int i = 0; i < bands; i++)
            {
                int bandTop = sourceRect.Height * i / bands;
                int bandBottom = sourceRect.Height * (i + 1) / bands;
                var band = new Rectangle(sourceRect.X, sourceRect.Y + bandTop, sourceRect.Width, bandBottom - bandTop);
                // Origin so the (virtual) full-sprite ground-anchor row still maps to the feet position.
                var origin = new Vector2(baseOrigin.X, baseOrigin.Y - bandTop);
                // 0 at the head band, 1 at the feet band. Rows below the feet (a stretched sprite's
                // water half) clamp to 1 rather than running past it.
                float bottomFraction = MathHelper.Clamp(sourceRect.Height * (i + 0.5f) / bands / feetRow, 0f, 1f);
                float bandAlpha = headFade + (1f - headFade) * (float)Math.Pow(bottomFraction, 1.8);
                float texelsAboveFeet = baseOrigin.Y - (bandTop + bandBottom) * 0.5f;
                if (texelsAboveFeet * scale.Y > clipDistance)
                    continue;
                float upScreen = texelsAboveFeet * upScreenPerTexel;
                float sideways = texelsAboveFeet * (float)Math.Sin(rotation) * scale.Y;
                float bandDepth = anchorIsSortDepth
                    ? ShadowPieceDepthUnder(anchorWorldY, upScreen)
                    : GroundedPieceDepth(anchorWorldY, upScreen, feetWorldX, sideways);
                DrawSoft(spriteBatch, Taps5, texture, band, feet, bandColor, alpha * bandAlpha, rotation, origin, scale,
                    bandDepth, effects, PenumbraRadiusAt(blur, texelsAboveFeet / feetRow), shadowLengthPerHeight);
            }
        }

        /// <summary>
        /// How far under the caster (in sort depth) the shadow sits. The farmer draws many
        /// sub-layers spanning a small depth range, so this must clear that whole range to keep
        /// the shadow strictly BEHIND the sprite (else it shows over opaque body pixels).
        /// </summary>
        private const float ShadowDepthBias = 1.2e-3f;


        private static bool OnWater(GameLocation location, Point tile)
        {
            try
            {
                // The surface grid distinguishes open water from pier/bridge DECKS over water, so
                // it is the robust answer. Fall back to the isWaterTile + no-Buildings-tile
                // heuristic (which approximates the same deck check) if the map isn't ready.
                var surfaceMap = SurfaceMap.For(location);
                if (surfaceMap != null)
                    return surfaceMap.IsWater(tile.X, tile.Y);
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

        /// <summary>
        /// Which side the sun stands on, in radians clockwise, captured once per frame by ModEntry
        /// for the same reason the golden hour is: <see cref="ComputeSun"/> is static and has no
        /// config within reach. 0 is every earlier release.
        /// </summary>
        /// <remarks>
        /// This is a ROTATION OF THE WHOLE SKY, not a fixed angle for the shadows. The day's swing
        /// is added on top of it, so noon lands where this points and morning and evening still
        /// lean off to either side of that. It is also why anything that wants the time of day
        /// rather than the direction of the light has to take this back off again first: see
        /// <see cref="SunSweepOnly"/>, and the two callers that use it.
        /// </remarks>
        internal static float SunBearingRadiansNow;

        /// <summary>
        /// Which side the sun stands on for everything that is LIGHT: the shafts through the trees,
        /// the lean a sprite is lit from, the daylight through a window, the glitter on the water.
        /// Radians clockwise, on the same scale as <see cref="SunBearingRadiansNow"/>, so equal
        /// numbers mean one sun. It ships a half turn from the shadows' because that is where the
        /// two halves of this mod have stood since they were written. See <see cref="SunInSky"/>.
        /// </summary>
        internal static float SunlightBearingRadiansNow = (float)Math.PI;

        /// <summary>How far the sun swings from noon to either edge of the day, in radians. Shared
        /// so the shadows and the light cannot drift apart in a copied constant.</summary>
        internal const float SunSwingRadians = 1.15f;

        /// <summary>The sun-follows-the-season dial, captured once per frame like the golden hour
        /// and for the same reason: everything that asks where the sun is, is static.</summary>
        internal static float SunSeasonStrengthNow;

        /// <summary>Half the day's length in minutes, by season: the real figures for the fortieth
        /// parallel north (summer 05:30 to 19:30, spring and autumn 06:00 to 18:00, winter 07:20
        /// to 16:40), blended by the dial with the six hours either side of noon every earlier
        /// release used all year round.</summary>
        internal static float HalfDayMinutesNow()
        {
            float seasonal = LocalSky.Season switch { Season.Summer => 420f, Season.Winter => 280f, _ => 360f };
            return MathHelper.Lerp(360f, seasonal, MathHelper.Clamp(SunSeasonStrengthNow, 0f, 1f));
        }

        /// <summary>Where the sun is in its day: -1 at sunrise, 0 at noon, +1 at sunset, held
        /// there beyond. Everything that reads the sun's position asks this one question, so the
        /// season moves the shadows, their colour, the shafts through the trees, the light
        /// through a window and the mist together, and never one without the others.</summary>
        internal static float SunSkyOffsetAt(float minutesNow)
            => MathHelper.Clamp((minutesNow - 720f) / HalfDayMinutesNow(), -1f, 1f);

        /// <summary>
        /// Shadow length per unit of caster height for a sun this far through its day, before the
        /// golden hour and the length dial. At dial 0 the line every earlier release drew, 0.3 at
        /// noon to 1.2 at the day's edges; at dial 1 the cotangent of the sun's height at this
        /// latitude and season, noon at 73 degrees in summer, 50 in spring and autumn, 27 in
        /// winter, falling toward the edges in the same proportion as the old line's 73 to 40. A
        /// winter noon really does throw a shadow six times a summer one's. Held under three, which
        /// is where a winter sunset would otherwise take a person's shadow across half a screen.
        /// </summary>
        internal static float SunStretchAt(float sunSkyOffset)
        {
            float edge = Math.Abs(sunSkyOffset);
            float classic = MathHelper.Lerp(0.3f, 1.2f, edge);
            float dial = MathHelper.Clamp(SunSeasonStrengthNow, 0f, 1f);
            if (dial <= 0f)
                return classic;
            float noonDegrees = LocalSky.Season switch { Season.Summer => 73f, Season.Winter => 27f, _ => 50f };
            float elevationDegrees = MathHelper.Lerp(noonDegrees, noonDegrees * (40f / 73f), edge);
            float seasonal = Math.Min(3f, 1f / (float)Math.Tan(MathHelper.ToRadians(Math.Max(5f, elevationDegrees))));
            return MathHelper.Lerp(classic, seasonal, dial);
        }

        /// <summary>
        /// The part of a sun angle that came from the CLOCK, with the bearing taken back off.
        ///
        /// <para>
        /// Anything that damps or scales the sun's angle has to work on this and add the bearing
        /// back afterwards, because damping the whole angle damps the direction the player chose.
        /// A crop at 0.62 lean, with the sun turned right round to 180, would otherwise point at
        /// 112 degrees: not where the sun says, and not where the player pointed it either, but a
        /// third direction belonging to nobody.
        /// </para>
        /// </summary>
        internal static float SunSweepOnly(float rotation) => rotation - SunBearingRadiansNow;

        private static void ComputeSun(out float rotation, out float stretch, out float alpha)
        {
            // Continuous minutes: the raw HHMM value made the angle lurch once per tick
            // (and extra hard across hour boundaries, where HHMM skips 40).
            float minutesNow = GameClock.MinutesNow();
            int trulyDark = TrulyDark();
            int trulyDarkMinutes = (trulyDark / 100) * 60 + trulyDark % 100;
            if (minutesNow >= trulyDarkMinutes)
            {
                // MOON: track its transit from true dark to 02:00 (day's end), same geometry
                // as the sun. Faint, phase-scaled shadows — full moon in winter is clearest.
                // Ease in over the first half hour: the sun fade reaches zero AT dark, and
                // the moon used to arrive at full (phase) strength on the very same tick.
                float moonProgress = MathHelper.Clamp((minutesNow - trulyDarkMinutes) / Math.Max(1f, 1560f - trulyDarkMinutes), 0f, 1f);
                float moonSkyOffset = moonProgress * 2f - 1f;
                rotation = SunSwingRadians * moonSkyOffset;
                stretch = MathHelper.Lerp(0.3f, 1.1f, Math.Abs(moonSkyOffset));
                alpha = 0.9f * 0.35f * MoonStrength() * MathHelper.Clamp((minutesNow - trulyDarkMinutes) / 30f, 0f, 1f);
                // The moon crosses the same sky, so it takes the same bearing: turning the sun
                // and leaving the moon behind would make the shadows swap sides at nightfall.
                rotation += SunBearingRadiansNow;
                LightningEffects.OverrideShadowKey(ref rotation, ref stretch, ref alpha);
                return;
            }
            // Low sun (dawn/dusk) → long, far-leaning shadow; high sun (noon) → short & upright.
            float sunSkyOffset = SunSkyOffsetAt(minutesNow);
            // Lean more sideways (was 0.8) so the shadow lies to the side of the body instead of
            // straight up over it — reduces the "shadow on the sprite" overlap while staying
            // upright (not the rejected upside-down flip).
            rotation = SunSwingRadians * sunSkyOffset;                           // <0 morning lean-left, >0 evening lean-right
            stretch = SunStretchAt(sunSkyOffset);                              // stretched LONG when the sun is low
            // Golden hour: the true edges of the day stretch further still. Quartic in the
            // offset, so noon and mid-afternoon feel nothing and only a genuinely low sun
            // goes long; every consumer of this method (characters, objects, the window
            // daylight patch) inherits it, which is what keeps the parity rule intact.
            float lowSunEdge = sunSkyOffset * sunSkyOffset * sunSkyOffset * sunSkyOffset;
            stretch *= 1f + GoldenHourStrengthNow * 1.3f * lowSunEdge;
            alpha = 0.9f * TimeFade();                           // opacity at the feet (× strength; fades toward the tip)
            // Where the player put the sun. Added to the day's swing rather than replacing it, so
            // the shadows still travel from one side to the other between dawn and dusk; the
            // bearing only decides where that journey passes through at noon. Before the strike
            // override, because a bolt is its own light in its own place and owes the sun nothing.
            rotation += SunBearingRadiansNow;
            // A lightning strike momentarily overrides both branches: every bake and draw path
            // funnels through this method, so keying it here keys every shadow at once.
            LightningEffects.OverrideShadowKey(ref rotation, ref stretch, ref alpha);
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
            int trulyDark = TrulyDark();
            return (trulyDark / 100) * 60 + trulyDark % 100;
        }

        internal static void WindowDaylight(out Vector3 colour, out float strength)
        {
            float minutesNow = GameClock.MinutesNow();
            int trulyDark = TrulyDark();

            // The sun is ALREADY up when the player wakes - the game's own outdoor light is at
            // full daylight by 06:00 - so the climb has to be finished shortly after, not
            // starting there. Ramping from 06:00 put this at exactly zero on the stroke of six,
            // which dropped through to the after-dark branch and lit the bedroom with moonlight
            // at sunrise.
            float risen = MathHelper.Clamp((minutesNow - 320f) / 60f, 0f, 1f);   // 05:20 -> 06:20
            float notYetDark = 1f - GameClock.RampAt(trulyDark, 60f);
            float day = Math.Min(risen, notYetDark);

            // Low sun = warm. Squared, so only the real edges of the day go golden and the
            // middle stays daylight-white instead of everything looking like a sunset.
            float lowSun = Math.Abs(SunSkyOffsetAt(minutesNow));
            Vector3 noon = new(0.86f, 0.93f, 1.06f);
            Vector3 gold = new(1.08f, 0.86f, 0.60f);
            colour = Vector3.Lerp(noon, gold, lowSun * lowSun);

            // The year: winter's sun is low and pale all day and the light is thin; summer is
            // the opposite; autumn light is famously warm.
            (float multiplier, Vector3 tint) season = LocalSky.Season switch
            {
                Season.Winter => (0.80f, new Vector3(0.93f, 0.98f, 1.10f)),
                Season.Summer => (1.12f, new Vector3(1.03f, 1.00f, 0.95f)),
                Season.Fall => (0.94f, new Vector3(1.06f, 0.98f, 0.90f)),
                _ => (1f, Vector3.One),
            };
            float weather = (LocalSky.IsRaining || LocalSky.IsSnowing || LocalSky.IsLightning) ? 0.62f : 1f;
            if (weather < 1f)
                colour = Vector3.Lerp(colour, new Vector3(0.90f, 0.94f, 1.00f), 0.6f);   // flat overcast

            strength = day * season.multiplier * weather;
            colour *= season.tint;

            if (strength <= 0.03f)
            {
                // After dark the window is still there - it just shows a night sky. A faint
                // cool pane reads as moonlight; leaving it at zero made rooms look sealed.
                //
                // What is behind the glass at night is the MOON, so the pane follows it: nearly
                // nothing on a new moon or under cloud, a little more when the moon is full. At
                // the flat 0.18 it used to carry, a farmhouse window on a moonless night read as
                // a lamp standing outside in the dark (reported with a picture once the rooms
                // beside it were darkened properly). MoonStrength itself answers zero indoors,
                // which is right for the ground outside and wrong for a pane, so the phase is
                // read here.
                float moonPhase = 1f - Math.Abs(Game1.dayOfMonth - 14.5f) / 13.5f;
                float clearSky = LocalSky.IsRaining || LocalSky.IsSnowing || LocalSky.IsLightning ? 0.3f : 1f;
                colour = new Vector3(0.52f, 0.62f, 0.95f);
                strength = (0.04f + 0.10f * MathHelper.Clamp(moonPhase, 0f, 1f)) * clearSky;
            }
        }

        /// <summary>Where that daylight lands on the floor: <paramref name="lean"/> is tiles
        /// sideways per tile into the room and <paramref name="reach"/> how far the patch
        /// carries. Taken from the same sun the shadows use, so a low morning sun throws a long
        /// patch across the boards in the same direction everything else is leaning.</summary>
        internal static void WindowShaft(out float lean, out float reach)
        {
            ComputeSun(out float rotation, out float stretch, out float alpha);
            // Far shallower than a cast shadow's rotation. A shadow leans hard because it is
            // measured on the ground away from a standing body; a patch of daylight seen from
            // above mostly just drops into the room. At the shadow's own 0.7 the patch crossed
            // more sideways than it travelled inward, which reads as a diagonal streak laid
            // over the furniture rather than as light coming through the glass.
            // The SWING only, not the bearing. This lean is a sideways slope handed to three
            // things that all march the light DOWN into the room by construction (the patch on
            // the lightmap, the beam in the shader, the dust in it), so the direction they travel
            // is theirs and not ours to turn. Handing them a beared angle would tilt the patch
            // without moving where the light comes from, which is a slope belonging to nobody.
            // Indoor light gets its own direction the day those three can be told one.
            lean = MathHelper.Clamp(SunSweepOnly(rotation) * 0.30f, -0.45f, 0.45f);
            reach = alpha <= 0.01f ? 2.2f : MathHelper.Clamp(2.2f + stretch * 2.5f, 2.2f, 5f);
        }

        /// <summary>Ease the shadow out toward dusk so it doesn't pop. Shadows stay at FULL
        /// strength until 40 minutes before the game's seasonal truly-dark time, then fade —
        /// a slow ramp across the whole evening left them invisible while the sun was still
        /// clearly up. No dawn ramp — the day starts at 06:00 with the player active.</summary>
        private static float TimeFade()
        {
            float minutesNow = GameClock.MinutesNow();
            int trulyDark = TrulyDark();
            int trulyDarkMinutes = (trulyDark / 100) * 60 + trulyDark % 100;
            if (minutesNow >= trulyDarkMinutes)
                return 0f;
            return MathHelper.Clamp((trulyDarkMinutes - minutesNow) / 40f, 0f, 1f);
        }
    }
}
