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
        private void DrawSunShadows(SpriteBatch b, GameLocation loc, ModConfig config, float strength, float blur)
        {
            ComputeSun(out float rot, out float stretch, out float alpha);
            alpha *= strength;
            if (alpha <= 0.01f)
                return;
            stretch *= Math.Max(0.1f, config.DirectionalShadowLength);

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] sun: npcs={loc.characters.Count}, time={Game1.timeOfDay}, rot={rot:0.00}, stretch={stretch:0.00}, alpha={alpha:0.00}, blur={blur:0.0}", LogLevel.Debug);
            }

            foreach (NPC npc in CharactersIn(loc))
            {
                if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnWater(loc, npc.TilePoint))   // don't lay a shadow on the water surface
                    continue;
                DrawNpcShadow(b, npc, rot, stretch, alpha, blur);
            }

            foreach (FarmAnimal a in AnimalsIn(loc))
            {
                if (a?.Sprite?.Texture == null || OnWater(loc, a.TilePoint))
                    continue;
                DrawAnimalShadow(b, a, rot, stretch, alpha, blur);
            }

            DrawPlayerShadow(b, loc, rot, stretch, alpha, blur);

            if (config.DirectionalShadowObjects)
            {
                DrawObjectShadows(b, loc, rot, stretch, alpha, blur);

                // Farm building ENTITIES (coop/barn/house) cast a shape-accurate silhouette from
                // their own texture, anchored at the footprint base — heavily damped/flattened so a
                // tall building's shadow lies beside it instead of swinging up over itself.
                var vp = Game1.viewport;
                int btx0 = vp.X / 64 - 12, btx1 = (vp.X + vp.Width) / 64 + 4;
                int bty0 = vp.Y / 64 - 12, bty1 = (vp.Y + vp.Height) / 64 + 4;
                foreach (Building bld in loc.buildings)
                {
                    if (bld == null || bld.tileX.Value > btx1 || bld.tileX.Value + bld.tilesWide.Value < btx0
                        || bld.tileY.Value > bty1 || bld.tileY.Value + bld.tilesHigh.Value < bty0)
                        continue;
                    // Buildings get a neutral grounding pool: a shape-accurate cast can't lean
                    // up like everything else without swinging the roof over itself, and the
                    // sheared-down bake was rejected visually — see the session notes.
                    DrawBuildingShadow(b, bld, alpha * 0.7f, blur);
                }
            }
        }

        private void DrawAnimalShadow(SpriteBatch b, FarmAnimal a, float rot, float stretch, float alpha, float blur)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(a.Position.X + a.Sprite.SpriteWidth * 4 / 2f, a.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(a.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            if (_casterBakes.TryGetValue((a.Sprite.Texture, a.Sprite.SourceRect), out var baked))
            {
                DrawSoft(b, Taps9, baked.rt, null, feet, Color.White, alpha, rot, baked.feetInRT,
                    new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
                return;
            }
            Rectangle src = a.Sprite.SourceRect;
            DrawBandedGradient(b, a.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
        }

        /// <summary>
        /// Indoors / at night: each real point light (torch, lamp, fire, fireplace) casts its
        /// own shadow of every caster, radiating AWAY from that light and fading with distance.
        /// Multiple lights → multiple overlapping shadows, as in real multi-light rooms.
        /// </summary>
        private void DrawLightShadows(SpriteBatch b, GameLocation loc, ModConfig config, float strength, float blur)
        {
            // Build the on-screen light list — may be EMPTY (a room with no lamps/windows). We no
            // longer bail on empty: an always-present ambient CONTACT pool grounds every caster
            // even in a lightless room, and point lights ADD their directional shadow on top.
            _lightBuf.Clear();
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                foreach (var kv in lights.Values)
                {
                    LightSource ls = kv;
                    // Cast from real point lights AND window/map lights (a window still throws a
                    // believable shadow across the room). Player-attached lights sit on the player
                    // so they self-cancel in LightCast (dist≈0). Skip nothing by context — except
                    // stale window lights (window removed/dark: glow gone but source lingers).
                    if (!WindowGlowing(loc, ls))
                        continue;
                    Vector2 screen = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    // Shadows reach much further than the glow; keep a whole-room-crossing minimum
                    // so a single small window still shadows the far corner.
                    float reach = Math.Max(640f, ls.radius.Value * 64f * 4f);
                    if (screen.X < -reach || screen.X > Game1.viewport.Width + reach ||
                        screen.Y < -reach || screen.Y > Game1.viewport.Height + reach)
                        continue;
                    // Fire-type lights flicker; their cast shadows dance with the flame.
                    _lightBuf.Add((screen, reach * FireFlicker(ls.position.Value, ls.textureIndex.Value)));
                    if (_lightBuf.Count >= 6)
                        break;
                }
            }

            if (Diag != null && _diagFrames < 3)
            {
                _diagFrames++;
                Diag.Log($"[shadow] light path: lights on-screen={_lightBuf.Count}, ambient contact on", LogLevel.Debug);
            }

            float lenCfg = Math.Max(0.1f, config.DirectionalShadowLength);
            float ambAlpha = strength * 0.4f;   // soft grounding pool; directional cast adds on top
            // OUTDOORS AT NIGHT a lamp is the only light on a dark ground, so its cast shadow
            // should read boldly (indoors stays subtle — bright rooms, tuned look). Boost only
            // the directional CAST strength here, not the ambient pool (a dark blob under
            // everyone far from any lamp would look wrong).
            bool outdoorNight = loc.IsOutdoors && Game1.timeOfDay >= TrulyDark();
            float castStrength = strength * (outdoorNight ? 1.9f : 1.0f);

            foreach (NPC npc in CharactersIn(loc))
            {
                if (npc == null || npc.IsInvisible || (npc.HideShadow && !(npc is Pet)) || npc.swimming.Value || npc.Sprite?.Texture == null)
                    continue;
                if (OnWater(loc, npc.TilePoint))   // same guard as the sun path (bathhouse, night beach)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom - FeetLift));
                float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                float halfW = npc.GetSpriteWidthForPositioning() * 4f * 0.36f;
                GatherCasts(feet, castStrength, lenCfg);
                DrawContactBlob(b, feet, halfW, halfW * 0.5f, ambAlpha * (_castBuf.Count > 0 ? 0.45f : 1f), depth, blur);
                foreach (var (rot, st, a) in _castBuf)
                    DrawNpcShadow(b, npc, rot, st, a, blur);
            }

            foreach (FarmAnimal animal in AnimalsIn(loc))
            {
                if (animal?.Sprite?.Texture == null)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(animal.Position.X + animal.Sprite.SpriteWidth * 4 / 2f, animal.GetBoundingBox().Bottom));
                float depth = MathHelper.Clamp(animal.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                float halfW = animal.Sprite.SpriteWidth * 4f * 0.36f;
                GatherCasts(feet, castStrength, lenCfg);
                DrawContactBlob(b, feet, halfW, halfW * 0.5f, ambAlpha * (_castBuf.Count > 0 ? 0.45f : 1f), depth, blur);
                foreach (var (rot, st, a) in _castBuf)
                    DrawAnimalShadow(b, animal, rot, st, a, blur);
            }

            if (_playerReady && _playerRT != null)
            {
                Farmer who = Game1.player;
                if (who != null && who.currentLocation == loc && !who.swimming.Value && !who.isRidingHorse()
                    && !OnWater(loc, who.TilePoint))
                {
                    Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                        new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
                    float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                    GatherCasts(feet, castStrength, lenCfg);
                    DrawContactBlob(b, feet, 22f, 11f, ambAlpha * (_castBuf.Count > 0 ? 0.45f : 1f), depth, blur);
                    foreach (var (rot, st, a) in _castBuf)
                        DrawSoft(b, Taps9, _playerRT, null, feet, Color.White, a, rot, _playerFeetInRT,
                            new Vector2(1f, st), depth, SpriteEffects.None, blur);
                }
            }

            // Furniture / big craftables / forage get a light ambient contact pool too (no per-light
            // silhouette — a room full of overlapping cast copies reads as clutter).
            if (config.DirectionalShadowObjects)
                DrawObjectContactShadows(b, loc, ambAlpha * 0.85f, blur);
        }

        /// <summary>Draw a soft dark contact pool (grounding shadow) centred at a screen point.</summary>
        private void DrawContactBlob(SpriteBatch b, Vector2 feet, float halfW, float halfH, float alpha, float depth, float blur)
        {
            if (_blobTex == null || alpha <= 0.01f)
                return;
            var origin = new Vector2(32f, 32f);
            var scale = new Vector2(Math.Max(0.01f, halfW * 2f / 64f), Math.Max(0.01f, halfH * 2f / 64f));
            DrawSoft(b, Taps5, _blobTex, null, feet, Color.Black, alpha, 0f, origin, scale, depth, SpriteEffects.None, blur);
        }

        /// <summary>Ambient contact pools under furniture / craftables / forage (indoor & night path).</summary>
        private void DrawObjectContactShadows(SpriteBatch b, GameLocation loc, float alpha, float blur)
        {
            var vp = Game1.viewport;
            int tx0 = vp.X / 64 - 2, tx1 = (vp.X + vp.Width) / 64 + 2;
            int ty0 = vp.Y / 64 - 2, ty1 = (vp.Y + vp.Height) / 64 + 2;

            foreach (Furniture f in loc.furniture)
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
                Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(box.Center.X, box.Bottom - 6f));
                float depth = MathHelper.Clamp((box.Bottom - 4f) / 10000f - ShadowDepthBias, 0f, 1f);
                DrawContactBlob(b, feet, box.Width * 0.5f * 0.8f, 12f, alpha, depth, blur);
            }

            foreach (var kv in loc.objects.Pairs)
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
                    Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 8f));
                    DrawContactBlob(b, feet, 26f, 12f, alpha, depth, blur);
                }
                else if (o.IsSpawnedObject)
                {
                    Vector2 feet = Game1.GlobalToLocal(vp, new Vector2(tile.X * 64f + 32f, (tile.Y + 1f) * 64f - 6f));
                    DrawContactBlob(b, feet, 14f, 8f, alpha, depth, blur);
                }
            }
        }

        private readonly System.Collections.Generic.List<(Vector2 pos, float reach)> _lightBuf = new();
        private readonly System.Collections.Generic.List<(float rot, float st, float a)> _castBuf = new();

        /// <summary>Collect this caster's directional casts from every on-screen light into
        /// <see cref="_castBuf"/>. Gathered BEFORE the grounding pool is drawn: when at least
        /// one light throws a real shadow, the pool drops to a hint (0.45×) — a full pool under
        /// a full cast read as two stacked shadows.</summary>
        private void GatherCasts(Vector2 feet, float strength, float lenCfg)
        {
            _castBuf.Clear();
            foreach (var (lpos, reach) in _lightBuf)
                if (LightCast(feet, lpos, reach, strength, lenCfg, out float rot, out float st, out float a))
                    _castBuf.Add((rot, st, a));
        }

        /// <summary>
        /// False for a STALE indoor window light: when a window is removed (decor mods) or goes
        /// dark (night/rain) the game drops its glow sprite from loc.lightGlows immediately but
        /// leaves the WindowLight in currentLightSources until the location is re-entered. A
        /// window with no glow isn't emitting. Outdoor window lights are left alone (town windows
        /// at night have no glow sprites).
        /// </summary>
        internal static bool WindowGlowing(GameLocation loc, LightSource ls)
        {
            if (loc.IsOutdoors || ls.lightContext.Value != LightSource.LightContext.WindowLight)
                return true;
            foreach (Vector2 g in loc.lightGlows)
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
            double t = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
            float phase = (worldPos.X * 0.013f + worldPos.Y * 0.007f) % 6.283f;
            float s = (float)(Math.Sin(t * 7.3 + phase) * 0.5 + Math.Sin(t * 12.9 + phase * 1.7) * 0.3
                            + Math.Sin(t * 23.7 + phase * 2.3) * 0.2);
            return 0.92f + 0.08f * s;
        }

        /// <summary>Shadow direction/length/opacity for a caster lit by one point light. False if out of reach.</summary>
        private static bool LightCast(Vector2 feet, Vector2 lightPos, float reach, float strength, float lenCfg,
            out float rot, out float stretch, out float alpha)
        {
            rot = 0f; stretch = 0f; alpha = 0f;
            Vector2 away = feet - lightPos;
            float dist = away.Length();
            if (dist < 1f || dist > reach)
                return false;
            float prox = 1f - dist / reach;                 // 1 next to the light, 0 at its edge
            // Indoor shadows stay SUBTLE (bright rooms) and shorter, so they read softly and
            // climb the (map-baked) walls less than the bold outdoor sun shadow.
            alpha = 0.5f * (0.5f + 0.5f * prox) * strength;   // near a light = darker, edge = fainter
            if (alpha <= 0.02f)
                return false;
            rot = (float)Math.Atan2(away.X, -away.Y);        // point the silhouette away from the light
            // Physically: a lamp is ELEVATED, so standing right under it the light comes from
            // nearly overhead → SHORT shadow; the further away you stand the more grazing the
            // angle → LONGER shadow (like a low sun at dusk). (This was inverted before.)
            stretch = MathHelper.Lerp(1.0f, 0.4f, prox) * lenCfg;
            return true;
        }

        private void DrawNpcShadow(SpriteBatch b, NPC npc, float rot, float stretch, float alpha, float blur)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                new Vector2(npc.Position.X + npc.GetSpriteWidthForPositioning() * 4 / 2f, npc.GetBoundingBox().Bottom - FeetLift));
            float depth = MathHelper.Clamp(npc.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
            // Prefer the baked silhouette (one cohesive image, smoothly faded — same as the
            // player). Bands are the fallback only when the sprite is too big for a slot.
            if (_casterBakes.TryGetValue((npc.Sprite.Texture, npc.Sprite.SourceRect), out var baked))
            {
                DrawSoft(b, Taps9, baked.rt, null, feet, Color.White, alpha, rot, baked.feetInRT,
                    new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
                return;
            }
            Rectangle src = npc.Sprite.SourceRect;
            DrawBandedGradient(b, npc.Sprite.Texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                alpha, rot, new Vector2(4f, 4f * stretch), depth, blur);
        }
    }
}
