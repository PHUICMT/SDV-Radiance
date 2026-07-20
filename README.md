# SDV-Radiance

> A single, configurable graphics suite for **Stardew Valley** — dynamic shadows,
> pixel-accurate water reflections, occlusion-aware lighting, bloom, god rays,
> cloud shadows, and cinematic color grading, all on the GPU. One install, every
> effect tunable live.

**Framework:** SMAPI 4.x · MonoGame · HLSL
**License:** MIT

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/phuicmt)

---

## What it is

SDV-Radiance is one well-behaved graphics layer for Stardew Valley. It captures
the game's rendered frame and runs a multi-pass shader chain over it on the GPU,
and it draws directional sprite shadows into the world, giving a richer, more
cinematic look while staying fully configurable — every effect has its own on/off
switch and sliders, live-previewed through an in-game tuner.

Shadows and reflections are generated from what's actually rendered, so custom
sprites, outfit mods, and recolors work automatically — no per-mod patches.

## Features

**Lighting & shadows**
- Directional shadows for characters, animals, trees, objects, crops, and map props — real sprite silhouettes that lean and stretch with the sun and fade at dusk
- Moonlight shadows at night, scaled by lunar phase and season
- Per-light indoor shadows (lamps, torches, fireplaces)
- Occlusion-aware dynamic lighting: darken flat/unlit interiors, pool warm light around real light sources, blocked by walls

**Water**
- Pixel-accurate reflections mirrored along the real painted shoreline (banks, trees, bridges, piers)
- Wading self-reflection in shallow water
- Ripple, sparkle, and surface shimmer — pond vs. ocean, weather/season aware

**Atmosphere & color**
- God rays from real in-world light sources
- Cloud shadows drifting across the ground
- Bloom, volumetric fog, tilt-shift depth blur, vignette, chromatic aberration
- Cinematic color grading with automatic mood by time / weather / season and metered auto-exposure

**Controls**
- Live in-game tuner (default **F6**): drag to preview, presets, save/load named looks
- Generic Mod Config Menu integration; per-effect toggles and sliders
- Toggle the whole stack with **F7** (rebindable)

### Out of scope — engine limits
- ❌ Real ray tracing / RTX — Stardew is 2D sprites; there is no 3D geometry, depth, or normals
- ❌ Path-traced global illumination — no 3D scene to bounce light in

Everything here is a **screen-space / draw-time approximation** built for a 2D pixel-art game.

## Architecture

Two independent systems joined at one point:

```
System A — Post-processing (RenderPipeline.* + shaders/*.fx)
   Game1 render ──(hook)──> capture the game's active target during RenderedWorld
   └─ shader chain (ping-pong buffers on the GPU):
        [Lighting] → [Water] → [Cloud shadows] → [God rays] → [Bloom]
           → [Fog] → [Color grade] → [Tilt-shift] → [Vignette/CA]
   └─ result drawn back into that same target

System B — Draw-time shadows (ShadowRenderer.*)
   bake each caster's silhouette to an offscreen target, then draw it leaned +
   flattened into the game's own World_Sorted sprite batch so it depth-sorts
   correctly: over the ground, under sprites/trees.

Link: System B bakes the player's silhouette to a shared mask; System A's water
shader reads it to exclude the player's own pixels from water effects.
```

The pipeline captures whatever target the game has bound during SMAPI's
`RenderedWorld` event and writes the final result back into it — nothing the game
owns is rebound or cleared, which keeps the world from going black. A single
Harmony postfix on `ShouldDrawOnBuffer` guarantees a target is bound while
effects are active.

**Shaders:** HLSL `.fx` → compiled to `.mgfxo` (MonoGame Effect) → loaded at runtime → applied via `SpriteBatch.Begin(effect: …)`.

## Tech stack

| Part | Technology |
|------|-----------|
| Language | C# (.NET 6, per SMAPI 4.x) |
| Framework | SMAPI 4.x · MonoGame (XNA) |
| Shaders | HLSL (Shader Model `4.0_level_9_x`, MonoGame OpenGL profile) |
| Shader build | `mgfxc` (MonoGame effect compiler) |
| Config UI | Generic Mod Config Menu (GMCM) |
| Target game | Stardew Valley 1.6.x |

## Install (players)

1. Install [SMAPI](https://smapi.io) 4.0.0 or newer.
2. (Recommended) Install [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098).
3. Unzip this mod into your `Stardew Valley/Mods` folder.
4. Launch through SMAPI. Press **F6** for the tuner, **F7** to toggle.

## Compatibility

- Stardew Valley 1.6.x, SMAPI 4.0.0+.
- Works automatically with sprite mods, Fashion Sense, and recolors (it reads the rendered frame).
- Single-player; effects are client-side. Android is not supported.
- Run only one screen-space/lighting post-processing overlay at a time for a predictable look.

## Building

1. Install the .NET SDK (6+; the repo builds against `net6.0`).
2. The project references [`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md), which auto-detects the game folder and deploys the build into `Mods/`.
3. Compile shaders with `mgfxc`, then copy the result into `assets/`:
   ```
   mgfxc shaders/bloom.fx assets/bloom.mgfxo /Profile:OpenGL
   ```
   **Version matters:** the `.mgfxo` format must match the game's MonoGame build.
   Stardew 1.6.x uses **MonoGame 3.8.0 (build 1641)**, so compile with
   `dotnet tool install -g dotnet-mgfxc --version 3.8.0.1641`. That tool targets
   .NET Core 3.1; if you only have a newer runtime, run it with
   `DOTNET_ROLL_FORWARD=LatestMajor`. A newer `mgfxc` produces a binary the game
   rejects with *"This MGFX effect seems to be for a newer release of MonoGame."*
   Compiled `.mgfxo` files are committed under `assets/`.
4. `dotnet build` → auto-copies to `Mods/SDV-Radiance/` → launch via SMAPI to test.

If the game folder isn't auto-detected, set `<GamePath>` in `SDV-Radiance.csproj` or a `stardewvalley.targets` file.

## Notes

- Multi-pass graphics effects cost GPU. Every effect has an on/off switch, so you can dial it to your machine.
- Render APIs can shift across SMAPI / SDV updates — expect occasional maintenance.

## Roadmap

Planned directions (not yet shipped):

- Real-time 2D global illumination (radiance-cascades style)
- Building / elevation shadows (via a companion height framework)
- Normal-mapped sprite lighting
- Wet-world rain surfaces
- High-quality texture upscaling

## Support / donate

Free and always will be. If it's useful to you, support is appreciated but never required.

- ☕ Ko-fi: https://ko-fi.com/phuicmt

## License & credits

MIT — see [LICENSE](LICENSE). Third-party attribution (frameworks, tooling, and any reused code) is in [CREDITS.md](CREDITS.md).
