# SDV-Radiance

> A single, configurable post-processing suite for **Stardew Valley** — bloom, color grading, god rays, volumetric fog, cloud shadows, tilt-shift, water shimmer, dynamic lighting, and finishing touches, all on the GPU. Long-term goal: fold in high-quality texture upscaling so one mod covers a whole graphics setup.

**Status:** 🚧 In development — Phases 0–5 done (bloom, color grading, god rays, fog, cloud shadows, tilt-shift, water shimmer, vignette, chromatic aberration, dynamic 2D lighting), with a live in-game tuner and a smooth-camera option. Next: texture upscaling.
**Framework:** SMAPI 4.x · MonoGame · HLSL
**License:** MIT

---

## What it is

SDV-Radiance is one well-behaved post-processing layer for Stardew Valley. It captures the game's rendered frame and runs a multi-pass shader chain over it on the GPU, giving a richer, more cinematic look while staying fully configurable — every effect has its own on/off switch and sliders, live-previewed through an in-game tuner.

The aim is a single clean install with a coherent look, low overhead (one capture, one chain), and no fiddly stacking — plus, eventually, a texture-upscaling stage so it can be the only graphics mod you need.

## Goals & scope

### In scope — GPU screen-space post-processing
- **Bloom** — glow from bright areas (lamps, sun, crystals)
- **Color grading / tone mapping** — palette and mood shifts by time / season / weather
- **God rays** — light shafts from real in-world light sources
- **Volumetric fog** — drifting screen-space mist, tinted by time of day
- **Cloud shadows** — soft animated shadows drifting across the ground
- **Tilt-shift** — top/bottom or radial depth-of-field blur
- **Water shimmer** — refraction ripple + sparkle applied only to water tiles
- **Dynamic 2D lighting** — darken flat/unlit areas and pool warm light around real light sources, with soft falloff
- **Finishing** — vignette + chromatic aberration
- **Texture upscaling** *(final phase — see roadmap)* — high-quality sprite resampling

### Out of scope — engine limits
- ❌ Real ray tracing / RTX — Stardew is 2D sprites; there's no 3D geometry, depth, or normals for RT cores
- ❌ Path-traced global illumination — no 3D scene to bounce light in

Everything here is a **screen-space approximation** built for a 2D pixel-art game.

## Architecture

```
Game1 render
   │
   ├─(hook)→ the whole frame is drawn into the game's own render target
   │
   ├─ Shader chain (multi-pass post-process on GPU, ping-pong buffers):
   │     [Lighting] → [Water] → [Cloud shadows] → [God rays] → [Bloom]
   │        → [Fog] → [Color grade + tone map] → [Tilt-shift] → [Vignette/CA]
   │
   └─ the result is drawn back into that same target
```

The pipeline captures whatever target the game already has bound during SMAPI's
`RenderedWorld` event and writes the final result back into it — nothing the game
owns is rebound or cleared, which keeps the world from going black. A single
Harmony postfix on `ShouldDrawOnBuffer` guarantees a target is bound while effects
are active.

**Shaders:** HLSL `.fx` → compiled to `.mgfxo` (MonoGame Effect) → loaded at runtime → applied via `SpriteBatch.Begin(effect: …)`.

**Config:** Generic Mod Config Menu (GMCM) — per-effect on/off, intensity sliders, and time/season/weather bindings — plus a live on-screen tuner (default **F8**) that previews changes as you drag.

## Tech stack

| Part | Technology |
|------|-----------|
| Language | C# (.NET 6, per SMAPI 4.x) |
| Framework | SMAPI 4.5.2+ · MonoGame (XNA) |
| Shaders | HLSL (Shader Model `4.0_level_9_x`, MonoGame OpenGL profile) |
| Shader build | `mgfxc` (MonoGame effect compiler) |
| Config UI | Generic Mod Config Menu (GMCM) |
| Target game | Stardew Valley 1.6.15 |

## Roadmap

**Phase 0 — Skeleton** ✅
- [x] manifest, `.csproj`, `ModEntry`
- [x] render hook — capture the game's active target (`GetRenderTargets()[0]`) during `RenderedWorld`, run the effect chain, draw back into that same target. No own render-target bind (that caused a black world after sleep); only Harmony patch is a `ShouldDrawOnBuffer` postfix while effects are active.
- [x] GMCM config

**Phase 1 — Bloom** ✅
- [x] bright-pass (Karis-average downsample, flicker-free) → separable Gaussian blur → screen-blend composite
- [x] threshold / intensity sliders

**Phase 2 — Color grading** ✅
- [x] linear-space parametric grade: exposure, white balance (temperature), contrast, saturation, optional ACES filmic tone map, highlight rolloff
- [x] auto mood by time of day / weather / season, with metered auto-exposure

**Phase 2b — God rays + volumetric fog** ✅ *(with 2D caveats)*
- [x] God rays (light shafts) — radial-blur from real in-world light sources (lamps/torches/fire); fade in/out and glide so they never pop when a light scrolls on/off screen
- [x] Volumetric fog — drifting fbm mist, outdoors only, tinted by time of day

**Extras (done)**
- [x] Live in-game **tuner** overlay (default F8): world previews live while dragging, presets + save/load/delete named custom looks, TH/EN localized
- [x] **Smooth camera** option (eased weighted-follow; off by default)
- [x] Hotkeys (rebindable): toggle whole stack (F7), open tuner (F8)

**Phase 3 — Cloud shadows + tilt-shift** ✅
- [x] animated cloud shadows (opacity / coverage / scale / speed), world-anchored, outdoors, fluffy domain-warped shapes with soft feathered edges
- [x] tilt-shift — top/bottom bands or radial focus around the player, with a soft feathered edge

**Phase 4 — Water + finishing** ✅ *(with 2D caveats)*
- [x] Water shimmer — refraction ripple + sparkle applied **only to water tiles** (a per-tile mask built from `GameLocation.isWaterTile`, aligned to the viewport, refined by a blue-dominance test), adapting to weather and season; ocean vs. pond behave differently
- [x] Vignette — smooth radial edge darkening
- [x] Chromatic aberration — subtle radial R/B split (kept small so pixel art stays crisp)

**Phase 5 — Dynamic 2D lighting** ✅ *(core; soft shadows + water reflection in progress)*
- [x] darken flat/unlit interiors that the game leaves fully bright, with a cool ambient tint that deepens at night
- [x] warm light pools with soft falloff around real light sources (`Game1.currentLightSources`), sized from each light's radius, with adjustable warmth / brightness / radius
- [x] context-aware: outdoors, mines, and scripted-dark rooms keep vanilla lighting so nothing double-darkens
- [ ] soft occluder shadows and screen-space water reflection *(in progress)*

**Phase 6 — Texture upscaling** *(final; do when time allows)*
- [ ] high-quality sprite resampling
- Candidate directions: improved edge-directed interpolation, contrast-adaptive sharpening (CAS), optional offline pre-upscale cache
- *This is the largest phase.*

## File layout

```
SDV-Radiance/
├── README.md
├── LICENSE                MIT
├── CREDITS.md             third-party attribution
├── .gitignore
├── .github/FUNDING.yml    donation links
├── manifest.json
├── SDV-Radiance.csproj
├── src/                   C#
│   ├── ModEntry.cs
│   ├── ModConfig.cs
│   ├── RenderPipeline.cs
│   └── ...
├── shaders/               HLSL .fx source
├── assets/                compiled .mgfxo (shipped)
└── i18n/                  default.json / th.json
```

## Building

1. Install the .NET SDK (6+; the repo builds against `net6.0`).
2. The project references [`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md), which auto-detects the game folder and deploys the build into `Mods/`.
3. Compile shaders with `mgfxc`, then copy the result into `assets/`:
   ```
   mgfxc shaders/bloom.fx build/bloom.mgfxo /Profile:OpenGL
   cp build/bloom.mgfxo assets/bloom.mgfxo
   ```
   **Version matters:** the `.mgfxo` format must match the game's MonoGame build.
   Stardew 1.6.15 uses **MonoGame 3.8.0 (build 1641)**, so compile with
   `dotnet tool install -g dotnet-mgfxc --version 3.8.0.1641`. That tool targets
   .NET Core 3.1; if you only have a newer runtime, run it with
   `DOTNET_ROLL_FORWARD=LatestMajor`. A newer `mgfxc` produces a binary the game
   rejects with *"This MGFX effect seems to be for a newer release of MonoGame."*
   Compiled `.mgfxo` files are committed under `assets/`.
4. `dotnet build` → auto-copies to `Mods/SDV-Radiance/` → launch via SMAPI to test.

If the game folder isn't auto-detected, set `<GamePath>` in `SDV-Radiance.csproj` or a `stardewvalley.targets` file.

## Notes

- Multi-pass post-FX costs GPU. Every effect has an on/off switch, so you can dial it to your machine.
- Render APIs can shift across SMAPI / SDV updates — expect occasional maintenance.

## Support / donate

If this mod is useful to you, support is appreciated but never required.

- ☕ Ko-fi: https://ko-fi.com/phuicmt
- 💛 GitHub Sponsors: https://github.com/sponsors/PHUICMT

## License & credits

MIT — see [LICENSE](LICENSE). Third-party attribution (frameworks, tooling, and any reused code) is in [CREDITS.md](CREDITS.md).

## References

- SMAPI rendering events — https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Events
- MonoGame custom effects — https://docs.monogame.net/articles/getting_started/content_pipeline/custom_effects.html

---
*Last updated: 2026-07-16 — Phase 5 (dynamic 2D lighting) core done; Phases 0–5 complete.*
