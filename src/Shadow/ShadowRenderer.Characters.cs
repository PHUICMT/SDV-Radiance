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
    /// ShadowRenderer — CHARACTER shadows: the outdoor sun/moon pass and the indoor/night
    /// per-light pass for NPCs, animals and the player, plus the grounding contact pools
    /// and the light-list helpers they share (WindowGlowing, FireFlicker, LightCast).
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>One long shadow per caster, leaning away from the sun (outdoors, daytime).</summary>
        private void DrawSunShadows(SpriteBatch spriteBatch, GameLocation location, ModConfig config, float strength, float blur)
        {
            ComputeSun(out float rot, out float stretch, out float alpha);
            // Overcast is a dimmer on the sun, not a switch (see OvercastNow): faint, short and
            // soft, with everything keeping its own silhouette instead of the screen reverting to
            // the game's blobs the moment it rains.
            alpha *= strength * MathHelper.Lerp(1f, OvercastAlpha, _overcastBlend);
            if (alpha <= 0.01f)
                return;
            blur += OvercastExtraBlur * _overcastBlend;
            _sunLengthScale = Math.Max(0.1f, config.DirectionalShadowLength)
                            * MathHelper.Lerp(1f, OvercastLength, _overcastBlend);
            CaptureKindTuning(config);
            stretch *= _sunLengthScale;

            if (DiagnosticMonitor != null && _diagnosticFrameCount < 3)
            {
                _diagnosticFrameCount++;
                DiagnosticMonitor.Log($"[shadow] sun: npcs={location.characters.Count}, time={Game1.timeOfDay}, rot={rot:0.00}, stretch={stretch:0.00}, alpha={alpha:0.00}, blur={blur:0.0}", LogLevel.Debug);
            }

            foreach (NPC npc in CharactersIn(location))
            {
                if (npc == null || npc.IsInvisible || ShadowHiddenFor(npc) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnOpenWater(location, npc.TilePoint))   // open water only — surf/shore keeps shadows
                    continue;
                if (IsSeated(npc))
                {
                    // A seated sprite gets a grounding pool and no cast silhouette: the silhouette
                    // is the part that fought the seat (see IsSeated), while a small soft ellipse
                    // cannot land half-way through the bench no matter what the seat's depth is.
                    DrawContactBlob(spriteBatch, SeatedAnchor(npc), npc.GetSpriteWidthForPositioning() * 4f * 0.34f,
                        npc.GetSpriteWidthForPositioning() * 4f * 0.17f, alpha * 0.8f, SeatedDepth(npc), blur);
                    continue;
                }
                DrawNpcShadow(spriteBatch, npc, rot, stretch, alpha, blur);
            }

            foreach (FarmAnimal a in AnimalsIn(location))
            {
                if (a?.Sprite?.Texture == null || OnOpenWater(location, a.TilePoint))
                    continue;
                DrawAnimalShadow(spriteBatch, a, rot, stretch, alpha, blur);
            }

            DrawPlayerShadow(spriteBatch, location, rot, stretch, alpha, blur);
            DrawOtherFarmerSunShadows(spriteBatch, location, rot, stretch, alpha, blur);

            if (config.DirectionalShadowObjects)
            {
                DrawObjectShadows(spriteBatch, location, rot, stretch, alpha, blur);

                // Farm building ENTITIES (coop/barn/house) cast a shape-accurate silhouette from
                // their own texture, anchored at the footprint base — heavily damped/flattened so a
                // tall building's shadow lies beside it instead of swinging up over itself.
                var viewport = Game1.viewport;
                int btx0 = viewport.X / 64 - 12, btx1 = (viewport.X + viewport.Width) / 64 + 4;
                int bty0 = viewport.Y / 64 - 12, bty1 = (viewport.Y + viewport.Height) / 64 + 4;
                foreach (Building bld in location.buildings)
                {
                    if (bld == null || bld.tileX.Value > btx1 || bld.tileX.Value + bld.tilesWide.Value < btx0
                        || bld.tileY.Value > bty1 || bld.tileY.Value + bld.tilesHigh.Value < bty0)
                        continue;
                    // Buildings get a neutral grounding pool: a shape-accurate cast can't lean
                    // up like everything else without swinging the roof over itself, and the
                    // sheared-down bake was rejected visually — see the session notes.
                    DrawBuildingShadow(spriteBatch, bld, alpha * 0.7f, blur);
                }
            }
        }

        private void DrawAnimalShadow(SpriteBatch spriteBatch, FarmAnimal a, float rot, float stretch, float alpha, float blur)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(a.Position.X + a.Sprite.SpriteWidth * 4 / 2f, a.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(a.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            if (_casterBakeCache.TryGetValue((a.Sprite.Texture, a.Sprite.SourceRect), out SpriteBake? baked))
            {
                baked.LastUsedTick = Game1.ticks;
                DrawSoft(spriteBatch, Taps9, baked.Rt, null, feet, Color.White, alpha, rot, baked.FeetInRt,
                    new Vector2(SolidAcrossScale(rot, stretch), stretch), depth, SpriteEffects.None, blur);
                return;
            }
            Rectangle src = a.Sprite.SourceRect;
            DrawBandedGradient(spriteBatch, a.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f * SolidAcrossScale(rot, stretch), 4f * stretch), depth, blur);
        }

        /// <summary>
        /// Indoors / at night: each real point light (torch, lamp, fire, fireplace) casts its
        /// own shadow of every caster, radiating AWAY from that light and fading with distance.
        /// Multiple lights → multiple overlapping shadows, as in real multi-light rooms.
        /// </summary>
        private void DrawLightShadows(SpriteBatch spriteBatch, GameLocation location, ModConfig config, float strength, float blur)
        {
            CollectCastingLights(location);
            TrimLightsToScreenBudget();

            if (DiagnosticMonitor != null && _diagnosticFrameCount < 3)
            {
                _diagnosticFrameCount++;
                DiagnosticMonitor.Log($"[shadow] light path: lights on-screen={_nearbyLightSources.Count}, ambient contact on", LogLevel.Debug);
            }

            _castsPerCaster = Math.Clamp(config.ShadowCastsPerCharacter, ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax);
            _groundForeshortening = config.ShadowGroundForeshortening;
            _characterGroundForeshortening = config.ShadowCharacterGroundForeshortening;
            // The lamp pass captures its own tuning rather than going through CaptureKindTuning, so
            // the shape choice has to be taken here as well or a lamp shadow keeps the sun's answer.
            _shadowModel = config.DirectionalShadowModel;
            float lenCfg = Math.Max(0.1f, config.DirectionalShadowLength);
            float ambAlpha = strength * 0.4f;   // soft grounding pool; directional cast adds on top
            // OUTDOORS AT NIGHT a lamp is the only light on a dark ground, so its cast shadow
            // should read boldly (indoors stays subtle — bright rooms, tuned look). Boost only
            // the directional CAST strength here, not the ambient pool (a dark blob under
            // everyone far from any lamp would look wrong).
            // Eased over ±10 game-minutes around dark - the 1.9x lamp-shadow boost used to
            // land in a single tick, visibly thickening every cast shadow at once.
            float nightBoost = location.IsOutdoors ? GameClock.RampAt(TrulyDark()) : 0f;
            float castStrength = strength * MathHelper.Lerp(1.0f, 1.9f, nightBoost);

            CastNpcShadows(spriteBatch, location, castStrength, lenCfg, ambAlpha, blur);
            CastAnimalShadows(spriteBatch, location, castStrength, lenCfg, ambAlpha, blur);
            CastPlayerShadows(spriteBatch, location, castStrength, lenCfg, ambAlpha, blur);

            // Co-op partners, through the same two branches. They were absent from every caster
            // list this class walks, which is why nobody but you ever cast a shadow indoors.
            DrawOtherFarmerLightShadows(spriteBatch, location, castStrength, lenCfg, ambAlpha, blur);

            // Furniture / big craftables / forage get a light ambient contact pool too (no per-light
            // silhouette — a room full of overlapping cast copies reads as clutter).
            if (config.DirectionalShadowObjects)
                DrawObjectContactShadows(spriteBatch, location, ambAlpha * 0.85f, blur);
        }

        /// <summary>Fill <c>_nearbyLightSources</c> with the lights on screen that may cast:
        /// real point lights and window lights, minus the drifting decorative ones.</summary>
        private void CollectCastingLights(GameLocation location)
        {
            // Build the on-screen light list — may be EMPTY (a room with no lamps/windows). We no
            // longer bail on empty: an always-present ambient CONTACT pool grounds every caster
            // even in a lightless room, and point lights ADD their directional shadow on top.
            _nearbyLightSources.Clear();
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                _activeLightIds.Clear();
                foreach (var kv in lights)
                {
                    LightSource ls = kv.Value;
                    // Cast from real point lights AND window/map lights (a window still throws a
                    // believable shadow across the room). Player-attached lights sit on the player
                    // so they self-cancel in LightCast (dist≈0). Skip nothing by context — except
                    // stale window lights (window removed/dark: glow gone but source lingers).
                    if (!WindowGlowing(location, ls))
                        continue;
                    // Skip DRIFTING decorative lights (fireflies from The Night Lights, sparkle
                    // mods): each one threw its own moving shadow on the player. Neither signal
                    // alone tells a firefly from a real lamp:
                    //   - radius < 1 alone was the ORIGINAL bug: measured in Town, all 92 street
                    //     lights report under 1, so that test threw away every real lamp too and
                    //     a lamplit street cast nothing at all.
                    //   - drift alone (moved since last frame) is not enough either: a firefly
                    //     drifting slower than the pixel threshold below slips through, which is
                    //     exactly the "firefly shadows are back" regression this fixes.
                    // A firefly is BOTH small AND moving. A real lamp is small but PLANTED — its
                    // world position is bit-identical every frame — so requiring both keeps street
                    // lamps (small, static) while still catching slow-drifting decor (small,
                    // moving by any amount, not just past a multi-pixel jump).
                    //
                    // AND "MOVING" IS A PROPERTY OF THE LIGHT, NOT OF THIS FRAME. Asking whether it
                    // moved SINCE THE LAST FRAME put a per-frame quantity in charge of a yes/no that
                    // changes the picture, which is the trap this file already warns about twice for
                    // the ranking and for the flame wobble. A firefly answers "no" constantly: at the
                    // turning point of its wobble its speed is zero, so it travels less than the
                    // threshold, is read as a lamp for that one frame, and casts. On the frame it
                    // spawns there is no previous position at all, so it casts then too. The next
                    // frame it moves again and the shadow is gone.
                    //
                    // One frame of shadow, over and over, on every firefly's own rhythm. Reported as
                    // a faint shadow that BLINKS rather than one that is there, which is the exact
                    // signature: a steady wrong answer looks like a bug in the shadow, an
                    // intermittent one looks like a bug in the test.
                    //
                    // So drift is REMEMBERED. Seen to move once and it is a firefly for as long as it
                    // exists; a lamp whose position never changes is never marked, which is the whole
                    // reason this test beats "radius < 1" (all 92 of Town's street lights are under
                    // that). The other half closes the spawn frame: a small light with no history has
                    // to hold still for a few frames before it may cast at all. A planted lamp clears
                    // that in fifty milliseconds and nobody sees it; a firefly never clears it,
                    // because its first move is measured before the count gets there.
                    bool isWindow = ls.lightContext.Value == LightSource.LightContext.WindowLight;
                    Vector2 lpos = ls.position.Value;
                    _activeLightIds.Add(kv.Key);
                    bool small = ls.radius.Value < FireflyRadiusBound;
                    bool hadHistory = _lightPreviousPositions.TryGetValue(kv.Key, out Vector2 was);
                    if (hadHistory && Vector2.DistanceSquared(was, lpos) > 0.02f)   // ~0.14px — any real movement
                    {
                        _driftingLightIds.Add(kv.Key);
                        _lightSteadyFrames[kv.Key] = 0;
                    }
                    else
                    {
                        _lightSteadyFrames.TryGetValue(kv.Key, out int steady);
                        _lightSteadyFrames[kv.Key] = hadHistory ? Math.Min(FireflySettleFrames, steady + 1) : 0;
                    }
                    _lightPreviousPositions[kv.Key] = lpos;
                    bool firefly = !isWindow && small
                                   && (_driftingLightIds.Contains(kv.Key)
                                       || _lightSteadyFrames[kv.Key] < FireflySettleFrames);
                    if (firefly)
                        continue;
                    Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    // Shadows reach much further than the glow; keep a whole-room-crossing minimum
                    // so a single small window still shadows the far corner. reach is STEADY (no
                    // flicker) — multiplying it by the flame flicker made a caster near the reach
                    // boundary flip cast/no-cast every frame (the "blinking shadow" bug). The
                    // flicker is applied to the shadow's ALPHA instead (see LightCast), so a fire's
                    // cast dances in intensity but never blinks out.
                    float reach = Math.Max(640f, ls.radius.Value * 64f * 4f);
                    if (screen.X < -reach || screen.X > Game1.viewport.Width + reach ||
                        screen.Y < -reach || screen.Y > Game1.viewport.Height + reach)
                        continue;
                    _nearbyLightSources.Add((screen, reach, FireFlicker(ls.position.Value, ls.textureIndex.Value)));
                }
            }
            // NOTE: label-driven lights (window class 12, emissive art class 6) deliberately do
            // NOT feed this list. They were added and reverted: being per-TILE, one painted lamp
            // post is four "lights", and four of them next to the player took the whole six-slot
            // budget from the real lamps — so the cast collapsed to stubs (a shadow is shortest
            // right under its light) and the grounding pool halved itself because the code
            // believed a real cast existed. Feeding them needs a light BUDGET that clusters tiles
            // into sources and ranks by contribution, the way SelectLights does for the shader.
            // The drift memory and the settle counters live and die with the position memory: a
            // firefly mod spawns and retires lights all night, and a set that only ever grew would
            // be a leak as well as a way for a reused id to inherit a stranger's verdict.
            //
            // Through a reused list rather than Where().ToList().ForEach(): that spelling allocates
            // a list, an enumerator, a closure and two delegates EACH TIME, three times over, on a
            // path that runs every frame. This file already carries a note about method-group
            // conversion allocating per call for the same reason.
            DropRetiredLightIds(_lightPreviousPositions);
            DropRetiredLightIds(_driftingLightIds);
            DropRetiredLightIds(_lightSteadyFrames);
        }

        /// <summary>Runaway guard: keep the lights nearest the screen centre when a location
        /// carries an absurd number of them.</summary>
        private void TrimLightsToScreenBudget()
        {
            // A RUNAWAY GUARD, not a look choice. Every light on screen casts from every caster
            // now; this only stops a location carrying an absurd number of lights from turning one
            // frame into thousands of soft draws. It is the same 48 the lighting pass budgets,
            // so the two agree on what "the lights in this scene" means. When it does bite it
            // keeps the lights nearest the screen centre, which is stable frame to frame (the old
            // dictionary-order + break-at-N popped shadows in/out as the light set reordered).
            //
            // A bar of SIX here used to be the whole budget, and it cut by the wrong yardstick:
            // the six nearest the CAMERA were shared by every caster, so walking to one end of
            // Pierre's shop dropped the lights around the people standing at the other end and
            // their cast shadows vanished while they were still in plain sight. Which light
            // matters is a question about the caster, and the honest answer is all of them that
            // reach it — LightCast already drops the rest by distance and by an alpha floor, so
            // geometry does the limiting that an arbitrary count was doing badly.
            if (_nearbyLightSources.Count > ScreenLightBudget)
            {
                Vector2 mid = new(Game1.viewport.Width * 0.5f, Game1.viewport.Height * 0.5f);
                _nearbyLightSources.Sort((lightA, lightB) => Vector2.DistanceSquared(lightA.pos, mid).CompareTo(Vector2.DistanceSquared(lightB.pos, mid)));
                _nearbyLightSources.RemoveRange(ScreenLightBudget, _nearbyLightSources.Count - ScreenLightBudget);
            }
        }

        /// <summary>Every NPC and monster the game is drawing: a grounding pool, plus one cast
        /// silhouette per light that reaches them.</summary>
        private void CastNpcShadows(SpriteBatch spriteBatch, GameLocation location, float castStrength,
                                    float lenCfg, float ambAlpha, float blur)
        {
            foreach (NPC npc in CharactersIn(location))
            {
                if (npc == null || npc.IsInvisible || ShadowHiddenFor(npc) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnOpenWater(location, npc.TilePoint))   // same guard as the sun path (bathhouse, night beach)
                    continue;
                if (IsSeated(npc))
                {
                    float sw = npc.GetSpriteWidthForPositioning() * 4f;
                    DrawContactBlob(spriteBatch, SeatedAnchor(npc), sw * 0.34f, sw * 0.17f,
                        ambAlpha * 0.8f, SeatedDepth(npc), blur);
                    continue;   // pool only — the cast silhouette is what fought the seat
                }
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom - FeetLift));
                float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                float halfW = npc.GetSpriteWidthForPositioning() * 4f * 0.36f;
                GatherCasts(feet, castStrength, lenCfg);
                DrawContactBlob(spriteBatch, feet, halfW, halfW * 0.5f, ambAlpha * (_lightShadowCasts.Count > 0 ? 0.45f : 1f), depth, blur);
                foreach (var (rot, st, a, _) in _lightShadowCasts)
                    DrawNpcShadow(spriteBatch, npc, rot, st, a, blur);
            }
        }

        /// <summary>The farm animals, through the same pool-plus-cast pair.</summary>
        private void CastAnimalShadows(SpriteBatch spriteBatch, GameLocation location, float castStrength,
                                       float lenCfg, float ambAlpha, float blur)
        {
            foreach (FarmAnimal animal in AnimalsIn(location))
            {
                if (animal?.Sprite?.Texture == null)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(animal.Position.X + animal.Sprite.SpriteWidth * 4 / 2f, animal.GetBoundingBox().Bottom));
                float depth = MathHelper.Clamp(animal.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                float halfW = animal.Sprite.SpriteWidth * 4f * 0.36f;
                GatherCasts(feet, castStrength, lenCfg);
                DrawContactBlob(spriteBatch, feet, halfW, halfW * 0.5f, ambAlpha * (_lightShadowCasts.Count > 0 ? 0.45f : 1f), depth, blur);
                foreach (var (rot, st, a, _) in _lightShadowCasts)
                    DrawAnimalShadow(spriteBatch, animal, rot, st, a, blur);
            }
        }

        /// <summary>The local player, through the same two branches the NPCs use: seated gets the
        /// pool alone, standing gets the baked silhouette per light.</summary>
        private void CastPlayerShadows(SpriteBatch spriteBatch, GameLocation location, float castStrength,
                                       float lenCfg, float ambAlpha, float blur)
        {
            // The player, through the same two branches the NPCs above use.
            {
                Farmer sp = Game1.player;
                if (sp != null && sp.currentLocation == location && IsSeated(sp)
                    && !sp.swimming.Value && !sp.isRidingHorse() && !OnOpenWater(location, sp.TilePoint))
                    DrawContactBlob(spriteBatch, SeatedAnchor(sp), 20f, 10f, ambAlpha * 0.8f, SeatedDepth(sp), blur);
            }
            if (_playerReady && _playerRenderTarget != null)
            {
                Farmer who = Game1.player;
                if (who != null && who.currentLocation == location && !who.swimming.Value && !who.isRidingHorse()
                    && !IsSeated(who) && !OnOpenWater(location, who.TilePoint))
                {
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                        new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
                    float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                    GatherCasts(feet, castStrength, lenCfg);
                    DrawContactBlob(spriteBatch, feet, 22f, 11f, ambAlpha * (_lightShadowCasts.Count > 0 ? 0.45f : 1f), depth, blur);
                    foreach (var (rot, st, a, _) in _lightShadowCasts)
                        DrawSoft(spriteBatch, Taps9, _playerRenderTarget, null, feet, Color.White, a, rot, _playerFeetInRenderTarget,
                            new Vector2(1f, st), depth, SpriteEffects.None, blur);
                }
            }
        }

        /// <summary>Draw a soft dark contact pool (grounding shadow) centred at a screen point.</summary>
        private void DrawContactBlob(SpriteBatch spriteBatch, Vector2 feet, float halfW, float halfH, float alpha, float depth, float blur)
        {
            if (_contactBlobTexture == null || alpha <= 0.01f)
                return;
            var origin = new Vector2(32f, 32f);
            var scale = new Vector2(Math.Max(0.01f, halfW * 2f / 64f), Math.Max(0.01f, halfH * 2f / 64f));
            DrawSoft(spriteBatch, Taps5, _contactBlobTexture, null, feet, Color.Black, alpha, 0f, origin, scale, depth, SpriteEffects.None, blur);
        }

        /// <summary>Ambient contact pools under furniture / craftables / forage (indoor & night path).</summary>
        private void DrawObjectContactShadows(SpriteBatch spriteBatch, GameLocation location, float alpha, float blur)
        {
            var viewport = Game1.viewport;
            int tx0 = viewport.X / 64 - 2, tx1 = (viewport.X + viewport.Width) / 64 + 2;
            int ty0 = viewport.Y / 64 - 2, ty1 = (viewport.Y + viewport.Height) / 64 + 2;

            foreach (Furniture f in location.furniture)
            {
                if (f == null || f.isTemporarilyInvisible)
                    continue;
                int type = f.furniture_type.Value;
                if (type == 12 || type == 6 || type == 13 || type == 17)   // rugs / wall-mounted
                    continue;
                Vector2 tile = f.TileLocation;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                Rectangle box = f.boundingBox.Value;
                Vector2 feet = Game1.GlobalToLocal(viewport, new Vector2(box.Center.X, box.Bottom - 6f));
                float depth = MathHelper.Clamp((box.Bottom - 4f) / 10000f - ShadowDepthBias, 0f, 1f);
                DrawContactBlob(spriteBatch, feet, box.Width * 0.5f * 0.8f, 12f, alpha, depth, blur);
            }

            foreach (var kv in location.objects.Pairs)
            {
                Vector2 tile = kv.Key;
                if (tile.X < tx0 || tile.X > tx1 || tile.Y < ty0 || tile.Y > ty1)
                    continue;
                SObject o = kv.Value;
                if (o == null || o.isTemporarilyInvisible)
                    continue;
                float depth = MathHelper.Clamp(((tile.Y + 1f) * 64f) / 10000f + tile.X * 1e-5f - ShadowDepthBias, 0f, 1f);
                if (o.bigCraftable.Value)
                {
                    if (o.Fragility == 2)
                        continue;
                    Vector2 feet = Game1.GlobalToLocal(viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 8f));
                    DrawContactBlob(spriteBatch, feet, 26f, 12f, alpha, depth, blur);
                }
                else if (o.IsSpawnedObject)
                {
                    Vector2 feet = Game1.GlobalToLocal(viewport, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 6f));
                    DrawContactBlob(spriteBatch, feet, 14f, 8f, alpha, depth, blur);
                }
            }
        }

        private readonly System.Collections.Generic.List<(Vector2 pos, float reach, float flick)> _nearbyLightSources = new();
        private readonly System.Collections.Generic.List<(float rot, float st, float a, float distSq)> _lightShadowCasts = new();
        /// <summary>Runaway guard on the candidate lights, matching the lighting pass's own
        /// budget (<see cref="RenderPipeline.MaxLights"/>) so the two agree on what "the lights in
        /// this scene" means. It was 24 after the lighting had already grown to 48, and the gap
        /// was a flicker: the saloon at night carries 34 to 39 lights, so ten of them sat past the
        /// bar and traded places every step the player took, and every NPC one of them lit gained
        /// and lost that cast as it went. Reported as "everyone's shadow flickers when I walk".</summary>
        private const int ScreenLightBudget = RenderPipeline.MaxLights;
        /// <summary>How many shadows one body may cast this frame, nearest light first — read from
        /// <see cref="ModConfig.ShadowCastsPerCharacter"/> once per frame rather than per caster.
        /// Ranking by distance keeps the swap invisible when a nearer light overtakes the last one
        /// in: at the moment they trade places they are the same distance away, so they are drawing
        /// nearly the same shadow.</summary>
        private int _castsPerCaster = ModConfig.ShadowCastsMax;
        /// <summary>How far past the Nth light a cast still fades rather than vanishing, as a
        /// multiple of that light's distance. Wide enough that a light crossing the edge at
        /// walking pace takes several frames to leave.</summary>
        private const float CastFadeBand = 1.35f;
        /// <summary>Ranks one caster's casts by distance to the light. Distance and not alpha:
        /// alpha carries the flame flicker, so ranking by it would let a fire sitting near the cut
        /// flip in and out of the list every frame, which is the blinking this path was tuned to
        /// avoid. Distance between two things that are standing still does not move.</summary>
        private static readonly System.Comparison<(float rot, float st, float a, float distSq)> ByLightDistance
            = (castA, castB) => castA.distSq.CompareTo(castB.distSq);
        // Why the light list ended up the size it did: total offered, then each filter's toll.
        /// <summary>Where each light was last frame, by its id — the drift test's memory. Pruned
        /// to the ids still present so a location full of transient lights cannot grow it.</summary>
        private readonly System.Collections.Generic.Dictionary<string, Vector2> _lightPreviousPositions = new();
        private readonly System.Collections.Generic.HashSet<string> _activeLightIds = new();
        /// <summary>Every light seen to MOVE since it appeared. The drift test's real answer: a
        /// light either is the kind of thing that drifts or it is not, and asking one frame at a
        /// time gave a different answer at every turning point of a firefly's wobble. Pruned with
        /// the position memory, so an id that leaves the location is forgotten and a light that
        /// comes back is judged fresh.</summary>
        private readonly System.Collections.Generic.HashSet<string> _driftingLightIds = new();
        /// <summary>How many consecutive frames each light has held still, capped at the settle
        /// count. A light with no history is at zero, which is what keeps a firefly from casting
        /// on the frame it spawns, before its first movement can be measured.</summary>
        private readonly System.Collections.Generic.Dictionary<string, int> _lightSteadyFrames = new();

        /// <summary>Scratch for <see cref="DropRetiredLightIds"/>: a dictionary cannot be written
        /// while it is being enumerated, so the doomed keys are collected first. Reused, because
        /// this happens every frame.</summary>
        private readonly System.Collections.Generic.List<string> _retiredLightIdScratch = new();

        /// <summary>Forget every id that is no longer among the lights on screen. Cheap when there
        /// is nothing to do, which is the common case: the count test skips it entirely.</summary>
        private void DropRetiredLightIds(System.Collections.Generic.Dictionary<string, Vector2> memory)
        {
            if (memory.Count <= _activeLightIds.Count)
                return;
            _retiredLightIdScratch.Clear();
            foreach (string id in memory.Keys)
                if (!_activeLightIds.Contains(id))
                    _retiredLightIdScratch.Add(id);
            foreach (string id in _retiredLightIdScratch)
                memory.Remove(id);
        }

        private void DropRetiredLightIds(System.Collections.Generic.Dictionary<string, int> memory)
        {
            if (memory.Count <= _activeLightIds.Count)
                return;
            _retiredLightIdScratch.Clear();
            foreach (string id in memory.Keys)
                if (!_activeLightIds.Contains(id))
                    _retiredLightIdScratch.Add(id);
            foreach (string id in _retiredLightIdScratch)
                memory.Remove(id);
        }

        private void DropRetiredLightIds(System.Collections.Generic.HashSet<string> memory)
        {
            if (memory.Count <= _activeLightIds.Count)
                return;
            memory.IntersectWith(_activeLightIds);   // a set can prune itself in one pass
        }
        /// <summary>How long a small light must stand still before it is allowed to cast. Three
        /// frames is fifty milliseconds: a planted lamp clears it on the way in and nobody can see
        /// that it did, and a firefly never clears it because it moves first.</summary>
        private const int FireflySettleFrames = 3;
        /// <summary>One tile. Real lamps (Town's are ~0.6-0.9) sit under this too, so on its own
        /// it would silence half the lamps in the game: it only means anything ANDed with the drift
        /// test above — see the comment at its use site.</summary>
        private const float FireflyRadiusBound = 1.0f;

        /// <summary>Collect this caster's directional casts from every on-screen light into
        /// <see cref="_lightShadowCasts"/>. Gathered BEFORE the grounding pool is drawn: when at least
        /// one light throws a real shadow, the pool drops to a hint (0.45×) — a full pool under
        /// a full cast read as two stacked shadows.</summary>
        private void GatherCasts(Vector2 feet, float strength, float lenCfg)
        {
            _lightShadowCasts.Clear();
            foreach (var (lpos, reach, flick) in _nearbyLightSources)
                if (LightCast(feet, lpos, reach, strength, lenCfg, flick, out float rot, out float st, out float a))
                    _lightShadowCasts.Add((rot, st, a, Vector2.DistanceSquared(feet, lpos)));
            // Keep the lights nearest THIS caster. Every caster asks the question for itself, so a
            // person at the far end of a room keeps the window above them no matter where the
            // camera is, and nobody's shadow depends on how many lights happen to be lit near
            // somebody else.
            if (_lightShadowCasts.Count <= _castsPerCaster)
                return;
            _lightShadowCasts.Sort(ByLightDistance);
            // A SOFT edge on the count, because a hard one pops. Cutting the list at N drops the
            // light that lost its place at whatever strength it happened to have, and walking
            // across a room reorders that list constantly — a shadow would blink out mid-stride.
            //
            // So the count sets a DISTANCE instead of an index: whatever the Nth-nearest light is
            // standing at becomes the edge, everything inside it casts at full strength, and
            // everything beyond fades out across a band and then stops being drawn at all. Two
            // lights trading places are by definition the same distance away at the moment they
            // trade, so they are drawing the same shadow at the same strength, and the swap has
            // nothing left to show. The band also means the extra draws are always the faint ones.
            float cut = (float)Math.Sqrt(_lightShadowCasts[_castsPerCaster - 1].distSq);
            float outer = Math.Max(cut * CastFadeBand, cut + 1f);
            for (int i = _castsPerCaster; i < _lightShadowCasts.Count; i++)
            {
                var cast = _lightShadowCasts[i];
                float fade = MathHelper.Clamp((outer - (float)Math.Sqrt(cast.distSq)) / (outer - cut), 0f, 1f);
                fade = fade * fade * (3f - 2f * fade);
                if (cast.a * fade <= 0.02f)   // the same floor LightCast drops a cast at
                {
                    _lightShadowCasts.RemoveRange(i, _lightShadowCasts.Count - i);
                    break;
                }
                _lightShadowCasts[i] = (cast.rot, cast.st, cast.a * fade, cast.distSq);
            }
        }

        /// <summary>
        /// False for a STALE indoor window light: when a window is removed (decor mods) or goes
        /// dark (night/rain) the game drops its glow sprite from location.lightGlows immediately but
        /// leaves the WindowLight in currentLightSources until the location is re-entered. A
        /// window with no glow isn't emitting. Outdoor window lights are left alone (town windows
        /// at night have no glow sprites).
        /// </summary>
        internal static bool WindowGlowing(GameLocation location, LightSource ls)
        {
            if (location.IsOutdoors || ls.lightContext.Value != LightSource.LightContext.WindowLight)
                return true;
            foreach (Vector2 g in location.lightGlows)
                if (Vector2.DistanceSquared(g, ls.position.Value) < 160f * 160f)
                    return true;
            return false;
        }

        /// <summary>
        /// Slow multi-sine flame flicker (~±8%) for fire-type lights (sconce/fireplace/torch = 4,
        /// cauldron = 5); 1.0 for steady lights. Phase-offset by world position so two fires
        /// never pulse in sync. Applied to the GI seed, the direct pool and the cast shadows,
        /// so the whole room breathes with the flame.
        /// </summary>
        internal static float FireFlicker(Vector2 worldPos, int texIndex)
        {
            if (texIndex != 4 && texIndex != 5)
                return 1f;
            double t = Determinism.Seconds;
            float phase = (worldPos.X * 0.013f + worldPos.Y * 0.007f) % 6.283f;
            float s = (float)(Math.Sin(t * 7.3 + phase) * 0.5 + Math.Sin(t * 12.9 + phase * 1.7) * 0.3
                            + Math.Sin(t * 23.7 + phase * 2.3) * 0.2);
            return 0.92f + 0.08f * s;
        }

        /// <summary>
        /// Where a flame actually IS, given the light the game hung for it.
        ///
        /// <para>
        /// Returns the offset from the light's own position to the fire. Zero for anything that is
        /// not a flame, and zero for a flame the game already put in the right place.
        /// </para>
        ///
        /// <para>
        /// Two fireplaces in the same game disagree about this, which is why the rule is looked up
        /// rather than assumed. Measured with radiance_lights:
        /// </para>
        /// <list type="bullet">
        ///   <item>the saloon's hearth is <c>Saloon_Fireplace_22_17</c> at tile (23.0, 17.0), which
        ///   is the fire's own tile: the map put the light where the fire is;</item>
        ///   <item>the farmhouse's is <c>Furniture_FarmHouse_8_4</c> at tile (8.5, 3.0), a whole
        ///   tile above the furniture it belongs to.</item>
        /// </list>
        ///
        /// <para>
        /// The second one is not arbitrary: <c>Furniture.addLights</c> places its light at
        /// <c>boundingBox.X + 32, boundingBox.Y - 64</c>. The minus sixty-four is why the brightest
        /// part of a farmhouse hearth was the brick above the fire, and the PLUS THIRTY-TWO is the
        /// other half of the same fault: it is the middle of the piece's leftmost tile, not the
        /// middle of the piece, so on anything wider than one tile the glow and the sparks sat off
        /// to the left. Both were reported, separately, in those words.
        /// </para>
        ///
        /// <para>
        /// So the furniture is found by that exact placement rule and its own box answers both
        /// questions: the middle of the fire is the middle of the box, and the flames sit just
        /// above the footprint the box describes. Anything not placed by that rule is left alone,
        /// which is what keeps the saloon's hearth, a campfire and a street lamp where they are.
        /// </para>
        /// </summary>
        internal static Vector2 FlameGlowOffset(GameLocation? location, Vector2 lightPosition, int textureIndex)
        {
            if (location == null || (textureIndex != 4 && textureIndex != 5))
                return Vector2.Zero;
            foreach (Furniture piece in location.furniture)
            {
                Rectangle box = piece.boundingBox.Value;
                // The game's own placement, matched exactly rather than guessed at within a
                // radius: a nearby chair must not be mistaken for the thing that is burning.
                if (Math.Abs(box.X + 32 - lightPosition.X) > 1f || Math.Abs(box.Y - 64 - lightPosition.Y) > 1f)
                    continue;
                return new Vector2(box.Center.X - lightPosition.X,
                                   box.Y - FlameHeightAboveFootprint - lightPosition.Y);
            }
            // A torch the MAP hangs (the mines' wall sconces) lights from its tile's top-left
            // corner while the game draws the flame's glow at the tile's centre. The glow list
            // is the game saying where the fire is, so sparks and shadows go there too; before
            // this the mine's sparks rose half a tile to the left of every sconce.
            // Only a light standing exactly on a tile corner is one of those: a lamp the game
            // hangs at a tile's centre is already where its flame is, and a window's glow in
            // the same tile as a wall lamp must not drag the lamp under the window.
            bool onTileCorner = lightPosition.X % 64f == 0f && lightPosition.Y % 64f == 0f;
            if (onTileCorner && location.lightGlows != null)
            {
                int lightTileX = (int)MathF.Floor(lightPosition.X / 64f);
                int lightTileY = (int)MathF.Floor(lightPosition.Y / 64f);
                foreach (Vector2 glow in location.lightGlows)
                {
                    if ((int)MathF.Floor(glow.X / 64f) == lightTileX
                        && (int)MathF.Floor(glow.Y / 64f) == lightTileY)
                        return glow - lightPosition;
                }
            }
            return Vector2.Zero;
        }

        /// <summary>How far above its own footprint a fireplace's flames burn, in world pixels.
        /// Half a tile: the fire sits in the opening, just off the floor the piece stands on.</summary>
        private const float FlameHeightAboveFootprint = 32f;

        /// <summary>Shadow direction/length/opacity for a caster lit by one point light. False if out of reach.</summary>
        private static bool LightCast(Vector2 feet, Vector2 lightPos, float reach, float strength, float lenCfg, float flick,
            out float rot, out float stretch, out float alpha)
        {
            rot = 0f; stretch = 0f; alpha = 0f;
            Vector2 away = feet - lightPos;
            float dist = away.Length();
            if (dist < 1f || dist > reach)
                return false;
            float prox = 1f - dist / reach;                 // 1 next to the light, 0 at its edge
            // Near a light = bold, far = fainter (linear, with a floor so a medium-distance
            // shadow is still clearly visible — pure prox² made everything but point-blank
            // shadows vanish). flick (flame wobble) rides the ALPHA only, so a fire's cast
            // dances in intensity but never blinks in/out (the reach is steady now).
            // EDGE TAPER: fade to 0 across the last ~18% of reach so the shadow eases out as
            // you walk away instead of popping off at the hard cutoff.
            float edge = MathHelper.Clamp(prox / 0.18f, 0f, 1f);
            edge = edge * edge * (3f - 2f * edge);
            alpha = (0.3f + 0.7f * prox) * 0.6f * strength * flick * edge;
            if (alpha <= 0.02f)
                return false;
            rot = (float)Math.Atan2(away.X, -away.Y);        // point the silhouette away from the light
            // Physically: a lamp is ELEVATED, so standing right under it the light comes from
            // nearly overhead → SHORT shadow; the further away you stand the more grazing the
            // angle → LONGER shadow (like a low sun at dusk). (This was inverted before.)
            stretch = MathHelper.Lerp(1.0f, 0.4f, prox) * lenCfg;
            return true;
        }

        /// <summary>
        /// A character the game is drawing through a SEAT (a chair, a bench, a map seat) instead of
        /// through its own footprint. Its sprite is shifted by a draw offset and sorted at the
        /// seat's depth, and neither shows up in <c>Position</c> or <c>GetBoundingBox()</c> — the
        /// two things every anchor here is built from. Rather than guess the seat's geometry, we
        /// stand down and let vanilla handle the whole character.
        /// </summary>
        /// <remarks>
        /// ONE rule for the player and for NPCs, because a body is a body: the question is never
        /// "is this sitting", it is "has the game drawn this sprite away from its collision box".
        /// <c>drawOffset</c> is the only field that answers it, and it lives on
        /// <see cref="Character"/> so both answer identically.
        /// <para>
        /// Asking <c>Farmer.IsSitting()</c> instead was the mistake that cost the player their
        /// sun shadow. A farmer on a chair is MOVED, not offset - the game puts their Position on
        /// the seat and draws them at it, so drawOffset stays zero and the ordinary silhouette at
        /// the box is already correct. An NPC on a map seat is offset, so it is not. Keying on the
        /// offset gets both right, and fixes a case nobody had reported yet: a player riding the
        /// bus or posed by an event IS offset, and used to get a silhouette in the wrong place.
        /// </para>
        /// The 16 px floor keeps sub-pixel jitter from flipping anyone between the two paths.
        /// <para>
        /// The offset alone turned out to be too broad. Vanilla NPCs have no seat state at all
        /// (only Farmer does), so an offset is the only clue available, and the Squid Fest
        /// fishermen carry exactly the same one as Willy on his boat: drawOffset = (0, 96). They
        /// are not sitting, they are STANDING in the surf, and the pool read as no shadow at all
        /// against the water. SimpleNonVillagerNPC is what separates them: the game sets it on
        /// decorative placed characters (Beach.adjustDerbyFisherman) and not on posed villagers.
        /// Those keep the full silhouette, which lands correctly now that the shadow anchors at
        /// feet + drawOffset rather than at the collision box.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Where the game actually drew the local farmer this frame, in world pixels: the centre
        /// of the body along X and its bottom edge along Y. Farmer.draw does not draw at Position:
        /// it hands FarmerRenderer an origin of (xOffset, (yOffset + 128 - box.Height/2)/4 + 4)
        /// source pixels and the renderer draws at position + origin with that same origin, so the
        /// body lands three origins UP from where the box says, plus the frame's own positionOffset
        /// and xOffset, drawOffset, and the jump offset twice (getLocalPosition adds it, draw adds
        /// it again). Standing, every term but the box is zero and this is box.Bottom. Seated, the
        /// game sets yOffset to -48 and picks a frame with an offset of its own, and the two nearly
        /// cancel: the body sits where the box is. Adding yOffset whole, as the exclusion box and
        /// the mirror stamp did, hung both 48 px above a seated player, a rectangle of dead water
        /// over their head on the beach pier bench.
        /// </summary>
        internal static Vector2 FarmerDrawnAnchor(Farmer who)
        {
            Rectangle box = who.GetBoundingBox();
            FarmerSprite.AnimationFrame? frame = who.FarmerSprite?.CurrentAnimationFrame;
            float frameShiftX = frame.HasValue ? frame.Value.xOffset * 4f : 0f;
            float frameShiftY = frame.HasValue ? frame.Value.positionOffset * 4f : 0f;
            float centreX = box.Center.X + who.drawOffset.X + frameShiftX - 3f * who.xOffset;
            float bottomY = box.Bottom + who.drawOffset.Y + 2f * who.yJumpOffset + frameShiftY - 0.75f * who.yOffset;
            return new Vector2(centreX, bottomY);
        }

        internal static bool IsSeated(Character? c)
        {
            if (c == null || c.drawOffset.LengthSquared() <= 256f)
                return false;
            // No seat art is drawn around a decorative standing NPC, so there is nothing for the
            // silhouette to fight — the one thing the pool exists to avoid.
            return !c.SimpleNonVillagerNPC;
        }

        /// <summary>Screen point under a SEATED character's visible feet: the collision box says
        /// where they would stand, <c>drawOffset</c> says where the game actually drew them, and
        /// only the sum lands on the sprite. Without the offset the pool sat behind the bench,
        /// which is why a sitter looked like it had no shadow at all.</summary>
        private static Vector2 SeatedAnchor(Character c)
        {
            Vector2 off = c.drawOffset;
            return Game1.GlobalToLocal(Game1.viewport, new Vector2(
                c.Position.X + c.GetSpriteWidthForPositioning() * 4 / 2f + off.X,
                c.GetBoundingBox().Bottom - FeetLift + off.Y));
        }

        /// <summary>Sort depth for a seated character's pool. Biased a little further back than the
        /// standing bias so the pool tucks under the seat art instead of painting over its front
        /// edge — the seat is drawn at its own depth and we are not trying to guess it.</summary>
        private static float SeatedDepth(Character c)
            => MathHelper.Clamp(c.StandingPixel.Y / 10000f - ShadowDepthBias * 2f, 0f, 1f);

        /// <summary>The sideways scale that lays an upright frame down as a solid, for the
        /// rotate-and-scale draw the baked frames use. A farm animal is a solid like a tree is: its
        /// width lies across the sun's direction on the ground, and with the ground foreshortened a
        /// sideways shadow lies down instead of standing on its edge. See
        /// <see cref="ShadowProjection.AcrossScaleForRotation"/> for what the draw can and cannot
        /// reproduce of that.</summary>
        private float SolidAcrossScale(float rot, float stretch)
            => ShadowProjection.ForSolid(rot, stretch, _groundForeshortening).AcrossScaleForRotation();

        /// <summary>The same lay-down for a person, the player, another player or an NPC, at the
        /// people's own foreshortening; see <see cref="ModConfig.ShadowCharacterGroundForeshortening"/>
        /// for why they have one.</summary>
        private float CharacterAcrossScale(float rot, float stretch)
            => ShadowProjection.ForSolid(rot, stretch, _characterGroundForeshortening).AcrossScaleForRotation();

        /// <summary>
        /// Whether this character stands up like a person or lies across the ground like an animal.
        ///
        /// <para>People are thin and tall and their shadow is mostly length; a four-legged thing is
        /// wide and low and its shadow is mostly width, which is why farm animals have their own
        /// foreshortening. A horse and a pet are NPCs, so they were taking a person's foreshortening
        /// and their shadows stood up on edge beside them instead of lying down. So did every
        /// creature a wildlife mod adds, now that those cast at all.</para>
        ///
        /// <para>Asked of the frame the game gave the character rather than of a list of class
        /// names, so a mod's crab and a mod's villager each get the right answer without being
        /// named here: a villager's frame is 16 wide by 32 tall, a horse's and a pet's are 32 by 32,
        /// a critter's is square or wider. A stretched sprite (16 by 64, the Squid Fest fishermen)
        /// stays a person, which it is.</para>
        /// </summary>
        private static bool StandsLikeAPerson(NPC npc, ShadowModel model)
        {
            // 1.6 had no such question: every character took the person's foreshortening, horses
            // and pets included. Answering it that way again is the whole of what the 1.6 shapes
            // mean for a creature. Passed in rather than read from a field so the diagnostics,
            // which are static and answer for a config they are handed, cannot drift from the draw.
            if (model == ShadowModel.Classic)
                return true;
            AnimatedSprite? sprite = npc.Sprite;
            if (sprite == null)
                return true;
            return sprite.SpriteHeight > sprite.SpriteWidth;
        }

        /// <summary>
        /// Which way round the game is drawing this character, so the shadow faces the same way.
        ///
        /// <para>A horse has one set of frames and the game MIRRORS them to face the other way
        /// (<c>NPC.draw</c> passes FlipHorizontally when <c>flip</c> is set or the current
        /// animation frame asks for it). The source rectangle is identical either way, so a
        /// silhouette cut from it and drawn unmirrored is a horse facing the wrong direction:
        /// head where the tail is. It reads as the shadow being back to front, which is exactly
        /// what it is.</para>
        ///
        /// <para>Farm animals are not affected and are not asked: they have real frames for each
        /// direction and the game never mirrors them.</para>
        /// </summary>
        private static SpriteEffects SpriteFacing(NPC npc)
        {
            AnimatedSprite? sprite = npc.Sprite;
            bool mirrored = npc.flip
                || (sprite?.CurrentAnimation != null
                    && sprite.currentAnimationIndex < sprite.CurrentAnimation.Count
                    && sprite.CurrentAnimation[sprite.currentAnimationIndex].flip);
            return mirrored ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        }

        private void DrawNpcShadow(SpriteBatch spriteBatch, NPC npc, float rot, float stretch, float alpha, float blur)
        {
            // The collision box is the anchor, with no drawOffset term. A stretched sprite and the
            // offset that goes with it CANCEL: extendSourceRect(0, 32) with tempSpriteHeight = 64
            // and drawOffset = (0, 96) puts the bottom of the PERSON back on
            // GetBoundingBox().Bottom, which is where an ordinary character stands too. Adding the
            // offset here pushed those characters' shadows a tile and a half down the beach.
            Rectangle src = npc.Sprite.SourceRect;
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f,
                    npc.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            // Prefer the baked silhouette (one cohesive image, smoothly faded — same as the
            // player). Bands are the fallback only when the sprite is too big for a slot, which is
            // every stretched one: 64 rows drawn at 4x overflow the bake slot.
            float across = StandsLikeAPerson(npc, _shadowModel) ? CharacterAcrossScale(rot, stretch)
                                                 : SolidAcrossScale(rot, stretch);
            SpriteEffects facing = SpriteFacing(npc);
            if (_casterBakeCache.TryGetValue((npc.Sprite.Texture, src), out SpriteBake? baked))
            {
                baked.LastUsedTick = Game1.ticks;
                // The bake is pinned bottom-CENTRE in its slot and FeetInRt is that centre, so a
                // horizontal flip turns the silhouette about the same axis the game turns the
                // sprite about, and the feet stay where they are.
                DrawSoft(spriteBatch, Taps9, baked.Rt, null, feet, Color.White, alpha, rot, baked.FeetInRt,
                    new Vector2(across, stretch), depth, facing, blur);
                return;
            }
            // Where the feet sit INSIDE the sprite, which is the sprite's bottom edge only when the
            // game has not stretched it. A stretched sprite holds the person in its upper half and
            // water or tackle below, so pivoting the lean at the bottom edge swung the body a
            // couple of tiles clear of the person it belongs to.
            //
            // Read off the game's own placement rather than guessed: NPC.draw pins the sprite at
            // getLocalPosition + (spriteWidth*4/2, boundingBox.Height/2) with origin
            // (SpriteWidth/2, SpriteHeight*3/4), so the sprite's top edge is SpriteHeight*3 screen
            // px above that point, and the feet are however far the anchor is below the top. An
            // ordinary sprite comes out at exactly src.Height, the bottom edge, as before.
            Vector2 gameAnchor = npc.getLocalPosition(Game1.viewport)
                + new Vector2(npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Height / 2f);
            float spriteTop = gameAnchor.Y - npc.Sprite.SpriteHeight * 3f;
            float originY = MathHelper.Clamp((feet.Y - spriteTop) / 4f, 0f, src.Height);
            // Whatever the stretch added BELOW the feet is not part of the character: on the Squid
            // Fest fishermen it is the line and float sitting in the water. Casting it put tackle
            // shadows on the sand beside them. Cropping there leaves the person, and leaves every
            // ordinary sprite untouched because their feet are already the bottom edge.
            if (originY >= 1f && originY < src.Height - 0.5f)
                src = new Rectangle(src.X, src.Y, src.Width, (int)Math.Round(originY));
            DrawBandedGradient(spriteBatch, npc.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, Math.Min(originY, src.Height)),
                alpha, rot, new Vector2(4f * across, 4f * stretch), depth, blur, HeadFade, facing);
        }
    }
}
