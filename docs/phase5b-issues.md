# Phase 5b — Issue punch list (from 2026-07-16 test round)

Work through these one at a time. Status: ✅ done · 🔧 fixed, awaiting in-game confirm · ⏳ not started (feasible) · 🚧 needs decision/effort · ⛔ hard limit (not fixable)

> **Batch-2 + batch-3 update (build-verified, not yet game-tested):**
> - 🔧 A1/A2: sideways lean (rot 1.15) to reduce body overlap + depth bias 1.2e-3
> - 🔧 Sudden darkening at ~7:10: TimeFade now minute-accurate across hour rollover
> - ✅ C1 ResourceClump (stumps/logs/boulders) · ✅ C2 FarmAnimal (pets were already covered as NPCs)
> - ✅ C3 bigCraftable + floor Furniture · ✅ C4 farm Buildings
> - ✅ B1 vanilla tree/bush blob suppressed via Draw-shim transpiler (depth==1E-06 gate)
> - 🔧 E1: diag log added (`[light] ... NOT darkening`) — next test run will show whether the fireplace pool is missing because the room isn't being darkened
> - Height/depth framework: research done, plan in `height-framework-plan.md` (will live in a **separate repo**)

---

## A. Character shadows (sun, outdoors)

- 🚧 **A1 · Direction not settled** — our shadow leans UP-screen; the game's own baked shadows (trees/bushes/blobs) fall DOWN-RIGHT. Vertical flip read as "upside-down" (rejected). Current compromise: upright with a stronger sideways lean (1.15 rad). Untried alternative: pure sideways projection (rotate ~90°) = lying-down shadow, no overlap, not upside-down.
- 🔧 **A2 · Shadow overlapping the sprite** — depth bias raised to 1.2e-3 (clears the farmer's sub-layer depth range) — awaiting confirm.
- ✅ A3 · tip-fade gradient, soft edges, single cohesive silhouette, includes hat/hair/Fashion-Sense layers (player RT bake)
- ✅ A4 · strength up to ~0.9 via slider, dawn/dusk ease-in/out, skipped on water tiles

## B. Tree / bush shadows

- ✅ **B1 · Vanilla blob overlap** — suppressed via Harmony transpiler on `Tree.draw` + `Bush.draw`: all their `SpriteBatch.Draw` calls route through a shim that drops draws at `layerDepth == 1E-06f` (every vanilla tree/bush shadow uses exactly that depth) while our object shadows are active. *Risk note: depends on that depth constant; re-verify on game updates.*
- 🚧 **B2 · Shadow looks detached from the trunk** — the canopy/trunk paints over the shadow's base (higher depth). Inherent to large sprites; can be reduced by shifting the anchor/widening the base, not 100% fixable.
- 🔧 **B3 · Gradient banding** — band count now scales with sprite height (6–18) — awaiting confirm.
- ✅ B4 · "Trees & bushes" toggle in F8 tuner + GMCM.

## C. Other entity shadows

- ✅ C1 · ResourceClump (stump/log/boulder) — no vanilla shadow, upright, easy win
- ✅ C2 · FarmAnimal (`loc.animals`); Pet/cat already covered (Pets are NPCs in `loc.characters`)
- ✅ C3 · bigCraftable objects (machines/kegs/scarecrows; flat 16×16 floor items skipped) + floor Furniture (rugs type 12 and wall types 6/13/17 skipped)
- ✅ C4 · Farm Buildings (coop/barn/cabin; under-construction skipped)

## D. Indoor / night shadows (per light source)

- ✅ D1 · one shadow per (caster, light) pointing away from each light; multiple lights = multiple shadows; opacity/length by proximity; whole-room reach
- 🔧 **D2 · Too dark / climbs walls in bright rooms** — softened + shortened (base 0.5, stretch ≤0.85) — awaiting confirm
- ⛔ **D3 · Shadows climbing walls** — indoor walls are map tiles with no height data → not fully fixable (softening/shortening mitigates; the height framework may help later)

## E. Dynamic lighting (Phase 5 — separate system from shadows)

- 🚧 **E1 · Fireplace/lamp emits no light pool** — hypothesis: our pools only render when the room is being darkened (`ambientLight == White` gate in `ComputeLightingAmbient`); this room may not qualify. Diag log added; may need to relax the gate.
- 🚧 **E2 · Window glow too bright** — our bloom amplifies the game's window glow → lower bloom, or add an option to exclude window light from bloom
- 🔧 **E3 · Rooms too bright** — user-tunable via "Indoor darkness" in F8
- ℹ️ E4 · Window light fading over the day is vanilla behavior (WindowLight fade), not a bug

## F. God rays

- 🔧 **F1 · Masked to real light sources** — bright pass now gated to a disk around a real light (flowers/pale hair should no longer streak) — awaiting confirm near a lamp

## G. New feature ideas (not started)

- ⏳ **G1 · Window light shafts** — rays through windows, angled by time of day, into the room
- ⏳ **G2 · Prettier god rays** (volumetric, time/weather driven)
- ⏳ **G3 · Per-pixel water clip for shadows** (currently only per-tile skip at the caster's feet)

## ⛔ Accepted limits (no mod can fix these without editing maps)

- Decorative town trees / paths / town walls are baked into map tilesheets, not entities → cannot cast per-object shadows
- Shadows don't bend/shorten across elevation changes (water edges, roofs, cliffs) — the game has no per-tile height data. → This is the motivation for the **height/depth framework** (separate repo, see `height-framework-plan.md`)

---

### Suggested order
1. Confirm A1/A2 (direction + overlap) in-game; try the pure-sideways variant if still unsatisfying
2. Confirm B1/B3 (blob suppressed, smooth gradient) and the new C3/C4 shadows
3. E1 fireplace lighting (read diag → relax gate)
4. G1 window shafts, G2 god-ray polish
5. Height framework P0 (separate repo)
