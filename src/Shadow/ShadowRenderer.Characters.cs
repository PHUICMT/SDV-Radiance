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
                    new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
                return;
            }
            Rectangle src = a.Sprite.SourceRect;
            DrawBandedGradient(spriteBatch, a.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
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
            // frame into thousands of soft draws. It is the same 24 the lighting shader budgets,
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
        /// <summary>Runaway guard on the candidate lights, matching the lighting shader's own
        /// budget so the two agree on what "the lights in this scene" means.</summary>
        private const int ScreenLightBudget = 24;
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
        /// <summary>The old MinShadowLightRadius default. Real lamps (Town's are ~0.6-0.9) sit
        /// under this too, so this bound only matters ANDed with drift above — see the comment
        /// at its use site.</summary>
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
            if (_casterBakeCache.TryGetValue((npc.Sprite.Texture, src), out SpriteBake? baked))
            {
                baked.LastUsedTick = Game1.ticks;
                DrawSoft(spriteBatch, Taps9, baked.Rt, null, feet, Color.White, alpha, rot, baked.FeetInRt,
                    new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
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
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
        }
    }
}
