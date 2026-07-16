# Visual techniques roadmap — research (2026-07-17)

Candidate techniques to push realism/beauty further, ranked by (visual impact ÷ effort)
for this mod's architecture (MonoGame SpriteBatch + HLSL SM3.0 post-process chain +
World_Sorted draw injection + Harmony). All must preserve crisp pixel art.

## Top 8 shortlist

1. **Night window light spill (S) — best ratio.** Warm light pools projected from lit
   building windows onto exterior ground (trapezoid-skewed, window-shaped falloff).
   Reuses our existing light-pool renderer; window tiles detectable (cf. Nexus 35690
   Dynamic Windows). The single most iconic night cue in 2D games.
2. **Golden-hour pass (S).** At low sun: stretch directional shadows 2–4×, tint shadows
   cool blue while lit areas go warm, warm rim on sprite top edges. We already have every
   ingredient (sun-angle shadows + parametric grade) — mostly parameter animation +
   one shadow-tint uniform. Fires twice daily.
3. **Blue-noise/Bayer dither pass (S).** Final-stage sub-LSB noise kills banding in
   fog/god-ray/grade gradients (effective ~10-bit in LDR); ordered dither also reads
   period-authentic. One texture fetch in SM3.0.
4. **Wet world pass (M).** Rain darkens/saturates ground (wet albedo), noise-masked
   puddles accumulate, flipped faded sprite reflections in wet areas, vertical specular
   streaks under lights. Extends our water SSR onto a screen-space wetness mask.
   (Dynamic Reflections mod ships puddles — differentiate via lighting integration +
   compat toggle.)
5. **2D Radiance Cascades GI (L) — the flagship.** JFA distance field from occluders +
   emitters → cascade raymarch → merge. Real bounce light, colored ambient, soft area
   shadows, noise-free (Path of Exile 2 technique; radiance.wiki, GM Shaders tutorial).
   SM3.0 feasible but hard (dynamic-loop instruction limits → short march steps, more
   passes; ping-pong RGBA16F; half-res field multiplied under sprites keeps pixels crisp).
   Nothing in the SDV ecosystem has GI — strongest long-term differentiator.
6. **Auto-generated normal-mapped sprite lighting (L).** Generate normal maps per
   tilesheet on load (Sobel + silhouette inflation; cache to disk), two-sampler
   SpriteBatch Effect gives per-pixel N·L from our dynamic lights — the Sea of Stars
   look. Hard part: texture-pairing registry + generation quality (Sobel alone looks
   embossed; see arXiv 2212.09692, Laigter, SpriteIlluminator).
7. **Shader wind sway for foliage (M).** Pivot-weighted UV displacement on trees/bushes
   (displacement ∝ height above base), texel-snapped to stay crisp — we already transpile
   Tree.draw/Bush.draw, so the hook exists. Sync to the cloud-shadow wind vector.
   Existing mods (Wind Effects, Waving Grass) are CPU shake / grass-only.
8. **Lighting-integrated ambient particles (S–M).** Dust motes/pollen/fireflies as GPU
   point sprites — motes visible only inside god-ray shafts, fireflies register as real
   lights in our engine. Existing particle mods are lighting-unaware; integration is the edge.

## Remainder (ranked)

9. **Snow accumulation (M)** — top-edge detect + growing noise threshold during snow;
   share the edge-detect pass with wet-world. Winter-only payoff.
10. **LUT grading + film emulation (S)** — bake the parametric grade into a 32³ LUT
    (2D-unwrapped, SM3.0-safe); single fetch per pixel + user-importable film LUTs
    (community multiplier). Keep parametric as the LUT baker. (kosmonaut's MonoGame
    color-grading post is directly our stack.)
11. **Screen-space contact AO (M)** — soft darkening where sprites meet ground; partially
    redundant with our shadows, more valuable alongside RC GI (#5).
12. **Emissive auto-extraction (S)** — luminance+saturation heuristic marks emissive
    pixels (lava/forge/sconces) feeding bloom + light engine; becomes emitter input for #5.
13. **Heat shimmer (S)** — noise UV distortion near fires / summer-noon; sub-texel or
    texel-quantized amplitude.
14. **Palette-aware grading (M)** — snap graded output toward scene palette hues; niche.
15. **Rain-on-lens droplets — SKIP** — implies a 3D camera lens, breaks the top-down
    pixel-art frame; wet-ground (#4) delivers the weather signal in-world.

## HD-2D (Octopath) transferability

Not transferable without asset changes: 3D environment geometry, billboard DoF, PBR.
What transfers: the layering recipe (fog + bloom + tilt-shift + particles + dynamic
sprite shadows) — we already ship most; the shortlist fills the gaps (GI bounce,
normal-lit sprites, motion).

## Ecosystem note

Dynamic Shader (Nexus 40775, early access) is converging on this territory (GPU sun
shadows; bloom/fog/reflections planned). **#5 (RC GI) and #6 (normal-mapped sprites) are
the two nobody in the SDV space has** — strongest long-term differentiators. #1–#3 are
the quickest visible wins for the next release.

(Se the research agent output for the full source list: radiance.wiki, GM Shaders RC
tutorial, tmpvar PoC, arXiv 2212.09692, Laigter, kosmonaut, Anisoptera dithering, etc.)
