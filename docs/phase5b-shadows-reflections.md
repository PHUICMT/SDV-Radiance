# Phase 5b — Directional shadows + character water reflections (design & progress)

Two new **draw-time sprite-redraw** features (NOT screen-space post-process). Goal:
match/beat the reference mods (directional-shadow mod + Dynamic Reflections) and
apply to **characters** too.

## Verified game APIs (decompiled 1.6.15)

`StardewValley.FarmerRenderer.draw` overloads:
- `draw(SpriteBatch b, Farmer who, int whichFrame, Vector2 position, float layerDepth = 1f, bool flip = false)` — simplest; `flip` is HORIZONTAL only.
- `draw(SpriteBatch b, FarmerSprite.AnimationFrame animationFrame, int currentFrame, Rectangle sourceRect, Vector2 position, Vector2 origin, float layerDepth, int facingDirection, Color overrideColor, float rotation, float scale, Farmer who)` — full; **`overrideColor`** lets us draw the whole farmer as a dark silhouette (shadow) or tinted/translucent (reflection).
- Access `who.FarmerRenderer`, `who.FarmerSprite.CurrentFrame`, `who.FarmerSprite.CurrentAnimationFrame`, `who.FacingDirection`.

Decompiled source cached at (scratch, not committed):
`…/scratchpad/decomp/StardewValley.FarmerRenderer.decompiled.cs`

NPCs / simple sprites (easy): `npc.Sprite.Texture`, `npc.Sprite.SourceRect`, `npc.Position`.
Trees/objects: `TerrainFeature`/`Object` expose `.draw` but their texture+sourceRect are less uniform — do these last.

## Key mechanisms
- **Transform the whole multi-layer farmer at once:** wrap the `FarmerRenderer.draw` call in a `SpriteBatch.Begin(SpriteSortMode.Deferred, blend, SamplerState.PointClamp, null, RasterizerState.CullNone, null, transformMatrix: M)` where `M` is the game's view matrix composed with our shear (shadow) or vertical-flip-about-feet (reflection).
- **Shear/skew matrix** (shadow, perspective, not rotation): a matrix that offsets X proportional to (baseY - y), i.e. the top of the sprite leans toward the sun direction while the feet stay pinned. Sun direction/length from `Game1.timeOfDay` (long low shadows AM/PM, short at noon, none at night/indoors); soften/hide under `Game1.isRaining`/`isSnowing`.
- **Under-sprite draw order:** draw shadows in SMAPI `Display.RenderingWorld` (fires BEFORE the world is drawn) so world sprites paint on top → shadows sit under objects. Reflections: on water but under piers — trickier; likely also RenderingWorld with water-mask clipping (reuse `BuildWaterMask`/`isWaterTile`).
- **Soft edges + gradient fade:** render all shadow silhouettes to an offscreen RT, separable Gaussian blur (reuse bloom/tiltshift blur), fade alpha with distance from the caster's base, composite under the world.

## Honest hard parts
- Redrawing the farmer with all layers + a transform: doable via the full `draw` overload + a Begin transformMatrix, but fiddly (need current frame/anim/facing each frame).
- Compositing shadows strictly UNDER sprites from a post-process is NOT possible (post-process only has the final frame); must draw during/before world render (RenderingWorld) — hence the draw-time approach.
- Reflection translucency/tint on the farmer needs the `overrideColor` overload (alpha) or draw-to-RT then composite at low alpha.

## Phased build order (small, committed steps — env drops sessions, keep commits frequent)
1. **NPC directional shadow** (easiest sprite) — shear + dark silhouette in RenderingWorld, time-based direction. Get one visible win.
2. **Player directional shadow** — via full FarmerRenderer.draw + shear matrix, overrideColor black.
3. **Blur + gradient fade** — shadows → RT → Gaussian → composite (soft edges).
4. **Weather/time polish** — length/opacity by timeOfDay + weather; none at night/indoors.
5. **Player water reflection** — vertical-flip FarmerRenderer.draw into water below feet, translucent, water-mask clipped; ripple from existing water stage.
6. **NPC/object reflections** + config/GMCM/tuner + i18n.

## Status
- [ ] not started (design saved). Next: step 1 (NPC shadow) in a new `src/ShadowRenderer.cs`, hook `Display.RenderingWorld` in ModEntry.
