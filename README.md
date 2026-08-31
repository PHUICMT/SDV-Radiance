# SDV-Radiance

> A single, configurable graphics suite for **Stardew Valley**: dynamic shadows,
> pixel-accurate water reflections, occlusion-aware lighting, weather drawn as
> weather, particles, window reflections, bloom, sun shafts, cloud shadows and
> cinematic colour grading, all on the GPU. One install, every effect tunable live.

**Framework:** SMAPI 4.x · MonoGame · HLSL
**License:** MIT

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/phuicmt)

---

## What it is

SDV-Radiance is one well-behaved graphics layer for Stardew Valley. It captures
the game's rendered frame and runs a multi-pass shader chain over it on the GPU,
and it draws directional sprite shadows into the world, giving a richer, more
cinematic look while staying fully configurable. Every effect has its own on/off
switch and sliders, live-previewed through an in-game tuner.

Shadows and reflections are generated from what's actually rendered, so custom
sprites, outfit mods, and recolors work automatically, with no per-mod patches.

## Features

**Lighting & shadows**
- Directional shadows for characters, animals, trees, objects, crops, and map props: real sprite silhouettes that lean and stretch with the sun and fade at dusk
- Moonlight shadows at night, scaled by lunar phase and season
- Per-light indoor shadows (lamps, torches, fireplaces)
- Occlusion-aware dynamic lighting: darken flat/unlit interiors, pool warm light around real light sources, blocked by walls
- Interiors that follow the clock: rooms dim at dawn, fill through the morning, sink before dusk and go genuinely dark at night, with the colour moving from cool sky light to neutral to warm to blue
- Daylight through the windows: each pane lays a patch of sun across the floor that leans with the same sun the shadows use, coloured by hour, season and weather
- The scene answers lightning: for a blink after a strike every shadow leans away from the bolt, the mod's own darkening lifts with the game's flash, and a warm afterglow follows it out
- Up to 48 lights per scene; the nearest and brightest cast their own shadows, the rest pool light. The budget is deliberately larger than a room needs so walking never makes one light hand its slot to another
- Radiance cascades: the bounce lighting is computed as rays over a probe grid rather than a spreading flood, so light reaches around a corner instead of leaking through the wall. The default model since 1.7.0, with the older flood still selectable
- Bounced light takes the colour of what it bounced off, so a red barn throws warm light onto the ground beside it. Off by default, it is a look rather than a correction
- Lamps throw shadows with a shape: fences, bushes, boulders and placed things block lamp light as their own silhouette, the shadow's edge softens with distance from the lamp, and the shadow cuts into the round glow the game itself paints
- The edge of a sprite facing a lamp catches a bright fringe in the lamp's own colour
- Golden hour: in the first and last hours of the sun every shadow stretches further still

**Water**
- Pixel-accurate reflections mirrored along the real painted shoreline (banks, trees, bridges, piers)
- Wading self-reflection in shallow water; crab pots, splashes, tossed items and the fishing bobber reflect too
- Ripple, sparkle, and surface shimmer: pond vs. ocean, weather and season aware
- Caustics on shallow water, strongest along the shore, fading at night and in bad weather
- Rain spreads rings across the surface, with separate dials for how many, how wide, and how plainly they show
- Reflection softness is a slider: from a single crisp sample to twice the standard spread
- Water sub-types: ice reflects without rippling, flowing water and waterfalls read differently from calm water, lava glows on its own
- Anything solid jutting into the water keeps its colour out of it, and the ripple fades out as it approaches rather than deciding pixel by pixel whether to move

**Weather & particles**
- Rain, snow and windblown debris drawn as three planes at three depths, each leaning and travelling at its own rate, with splashes where drops land. Green rain gets its own fall, and a windy day carries what the season under your feet actually has on it
- Visible lightning bolts on any map, using the game's own bolt art, and not on every rumble
- Drops on the edge of the screen that merge, grow heavy and run, becoming frost in snow, with the edge of the picture misting over around them
- Particles drawn into the world rather than over it, so they take the light, the weather and the grade: dust in a window beam, sparks off a real fire, fireflies on the game's own firefly nights, blossom and leaves on the days the game leaves the air empty, and sparks turning around a glow ring
- A wet world after the rain, on its own clock, with lamplight smearing down the wet ground. Ships switched off, dials in both menus
- Heat haze: hot air over lava bends the picture seen through it, the way air over a summer road does

**Sprites & foliage**
- Wind in the trees: tree tops and bushes lean with the same wind the rain leans with, and a gust front crosses the map so a row of trees leans one after another rather than all at once. The tilt is a fraction of a degree, because pixel art has no in-between pixels to bend into
- Leaves catch the light: patches of canopy brighten and dim the way leaf faces flip in wind
- Sprite relief: a lamp or the sun lights the side of a tree, a building or a fence that faces it, from a normal map synthesised out of the sprite itself. Off by default
- Sprites at twice the texels: every sheet in use is doubled on the graphics card by the Scale2x rule, per art family, so dialogue text can stay sharp while the trees soften. Off by default, and the sheets themselves are never touched

**Windows & glass**
- Reflections in windows: you at the window's own height, with the tool in your hand, keeping your stride. Glass reflects when what is behind it is darker than what is in front, so the image is plain in daylight and thins after dusk as the room lights up behind the pane
- A wash of the sky's colour, a soft glare travelling across the pane, the street standing in the lower half, and after dark the lamps outside as small blots of their own colour in the panes facing them

**Atmosphere & color**
- Sun shafts: daylight cut by a canopy into slanting patches on the ground, with their own strength and reach. On by default
- Lamp shafts from in-world light sources, rebuilt in 1.7.0 from the same shapes the lamp shadows use, on a separate switch. Ships off
- Aurora on clear winter nights: slow curtains of green and violet that the water carries too. On, and rare
- Shooting stars on a clear night in any season, drawn into the sea as well as the sky
- Cloud shadows drifting across the ground, with count, coverage, size, and speed controls
- Day fog and night mist: two separate wispy drifting effects, each with its own amount, intensity, and drift speed
- Bloom, tilt-shift depth blur, vignette, chromatic aberration
- Cinematic color grading with automatic mood by time / weather / season and metered auto-exposure, plus seven ready-made looks and a slot for a LUT of your own. Ships off
- Blue light eye comfort filter, layered on the grade or used on its own

**Performance**
- Effect resolution: compute the effects at a fraction of the window while the world stays full size, with FSR-style sharpening on the way back up
- Three quality presets, and a benchmark that measures your machine and tells you what to set
- `radiance_report` prints what the mod costs on this machine, per part, averaged over the last 300 frames, with no debug setting to turn on first

**Controls**
- Live in-game tuner (default **F6**), organized into tabs by category: drag to preview, presets, save/load named looks
- Generic Mod Config Menu integration; per-effect toggles and sliders
- Toggle the whole stack with **F7** (rebindable)

### Out of scope, engine limits
- ❌ Real ray tracing / RTX: Stardew is 2D sprites; there is no 3D geometry, depth, or normals
- ❌ Path-traced global illumination: no 3D scene to bounce light in

Everything here is a **screen-space / draw-time approximation** built for a 2D pixel-art game.

## Architecture

Two independent systems joined at one point:

```
System A: post-processing (RenderPipeline.* + shaders/*.fx)
   Game1 render ──(hook)──> capture the game's active target during RenderedWorld
   └─ shader chain (ping-pong buffers on the GPU), in this order:
        [flood GI] → [lighting] → [water] → [cloud shadows] → [rays]
           → [bloom] → [fog] → [colour grade] → [tilt-shift]
           → [finish: vignette / CA] → [tail] → [wet]
   └─ result drawn back into that same target

System B: draw-time shadows (ShadowRenderer.*)
   bake each caster's silhouette to an offscreen target, then draw it leaned +
   flattened into the game's own World_Sorted sprite batch so it depth-sorts
   correctly: over the ground, under sprites/trees.

Drawn into the world rather than over it, so they take everything above:
   particles (ParticleSystem), replacement weather (PrecipitationSystem),
   window reflections (RenderPipeline.WindowReflection).

Link: System B bakes the player's silhouette to a shared mask; System A's water
shader reads it to exclude the player's own pixels from water effects.
```

The pipeline captures whatever target the game has bound during SMAPI's
`RenderedWorld` event and writes the final result back into it. Nothing the game
owns is rebound or cleared, which keeps the world from going black. A single
Harmony postfix on `ShouldDrawOnBuffer` guarantees a target is bound while
effects are active.

**Shaders:** HLSL `.fx` compiled to `.mgfxo` (MonoGame Effect), loaded at runtime, applied via `SpriteBatch.Begin(effect: …)`.

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

The stock look is a strong one. If it reads as too contrasty for a long session, press **F6**
and pick the **Subtle** preset on the first tab, or raise the blue light filter for tired eyes.
Nothing needs editing by hand, and both are live.

An existing `config.json` is never overwritten on update, so a tuned game does not change under
you. To take a newer version's defaults, delete it and let the mod write a fresh one.

Bundled languages: English, Thai, Simplified Chinese. A Korean translation is available
as a [separate mod](https://www.nexusmods.com/stardewvalley/mods/49448).

## Compatibility

- Stardew Valley 1.6.x, SMAPI 4.0.0+.
- Works automatically with sprite mods, Fashion Sense, and recolors (it reads the rendered frame).
- Effects are client-side, so each player sees their own and nobody else needs it installed.
  Split screen and online co-op are supported: other players cast shadows and appear in the
  water, and each screen keeps its own working data. Split screen has been tested with two
  players; the online case shares all of that code but has not been through a session on two
  machines.
- If another mod has already claimed the game's weather drawing, the replacement weather stands
  down and leaves it alone rather than fighting for the same slot.
- Android is not officially supported and is untested by the author, though players have reported it running (with some delay).
- Run only one screen-space/lighting post-processing overlay at a time for a predictable look.

Per-mod test results are in [docs/compatibility.md](docs/compatibility.md).

### Known incompatibilities
- **SpriteMaster**, also distributed as *"Clear Glasses"* and *"Clear Glasses HD"*
  (`aurpine.ClearGlasses`): **not compatible.** SpriteMaster hooks the sprite batch and render
  targets deeply for its texture upscaling and caching; SDV-Radiance captures and post-processes
  the game's render target. Running both makes the world render as a solid colour (orange/black)
  on load until a menu forces a redraw, and patches of the water surface lose their mirror while
  you walk. One player on Clear Glasses HD also reported the view zooming in hard or blacking out
  when walking near god rays; switching the God rays effect off avoided that without disabling
  the rest. Use one or the other. Native high-quality upscaling is on the roadmap so the two are
  eventually one install.
- **Clear Monocle is NOT in this bracket.** It is a fork that keeps only the upscaling, and its
  author shipped explicit support for this mod on 2026-07-28. Two users have confirmed it working
  since. Keep it up to date.

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
   **`dotnet build` does not compile shaders**, it only copies the `.mgfxo` files that are
   already there. Editing a `.fx` and rebuilding tests the old shader.
4. `dotnet build` copies to `Mods/SDV-Radiance/`, then launch via SMAPI to test.

If the game folder isn't auto-detected, set `<GamePath>` in `SDV-Radiance.csproj` or a `stardewvalley.targets` file.

## Notes

- Multi-pass graphics effects cost GPU. Every effect has an on/off switch, so you can dial it to
  your machine, and switching one off gives its cost back rather than leaving it running quietly.
- Render APIs can shift across SMAPI / SDV updates, so expect occasional maintenance.

## Roadmap

Planned directions (not yet shipped):

- Puddles decided from the map rather than guessed at, which is the reason the wet ground still ships off
- A shadow clipped against what stands in front of it. Indoors a shadow crosses a table or the saloon counter, because it is drawn at one sort depth taken from the caster's feet
- Reflections that shift with the camera the way a real mirror image does. Confirmed physically right and not yet built; it is the other half of the reports that a reflection at the beach sits away from the player
- Water carrying under every bridge and pier, not only the ones whose map data makes it possible today
- Lamp shafts on by default, once a night walk with them on stops finding gaps that are not there

Shipped since this list was last written: radiance-cascades global illumination and
normal-mapped sprite lighting (1.7.0, the first on by default and the second off), built-in
sprite sharpening (1.7.0, off by default), the lamp-shaft rebuild (1.7.0, still off), and
per-light shadows for objects and map props (1.7.0, as their own shapes).

## Support / donate

Free and always will be. If it's useful to you, support is appreciated but never required.

- ☕ Ko-fi: https://ko-fi.com/phuicmt

## License & credits

MIT, see [LICENSE](LICENSE). Third-party attribution (frameworks, tooling, and any reused code) is in [CREDITS.md](CREDITS.md).

Translations: Simplified Chinese bundled since 1.2.2 by **Rime961**, complete at 813 of 813 keys
in 1.7.0. Thai by the author, also complete. Separate translation mods on the Nexus, with thanks:
Korean by [jjongleee](https://www.nexusmods.com/stardewvalley/mods/49448), Chinese by
[Rubbish404](https://www.nexusmods.com/stardewvalley/mods/49647).
