<p align="center"><img src="https://staticdelivery.nexusmods.com/mods/1303/images/49397/49397-1791090240-1622644431.jpg" alt="SDV-Radiance: shadows, water, light and weather for Stardew Valley" width="100%"></p>

# SDV-Radiance

> Shadows, water, light and weather for **Stardew Valley**: sun and lamp shadows, water
> reflections, lighting that walls block, rain and snow, particles, window reflections, bloom,
> sun shafts, cloud shadows and colour grading, all drawn on the GPU. It's one mod, and every
> effect can be tuned or turned off while you play.

**Framework:** SMAPI 4.x · MonoGame · HLSL
**License:** MIT
**Download:** [Nexus Mods](https://www.nexusmods.com/stardewvalley/mods/49397)
**Website:** [radiance.phu-space.com](https://radiance.phu-space.com), clips of every effect and what's coming next

<p align="center">
<a href="https://ko-fi.com/phuicmt"><img src="https://phuicmt.github.io/SDV-Radiance-media/buttons/kofi.png" alt="Buy me a coffee on Ko-fi" height="48"></a>&nbsp;&nbsp;
<a href="https://github.com/sponsors/PHUICMT"><img src="https://phuicmt.github.io/SDV-Radiance-media/buttons/sponsor.png" alt="Sponsor on GitHub" height="48"></a>
</p>

---

<h2><img src="https://phuicmt.github.io/SDV-Radiance-media/banners/see-it.png" alt="See it" width="100%"></h2>

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/night.webp" alt="The aurora in the sea, a rainbow in waterfall spray, rain on the sea" width="100%"></p>

*Night water: the aurora in the sea, a rainbow in the spray where the falls meet the river, and rain on the sea around the pier.*

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/walk-by-the-water.webp" alt="Walking along the shore with a reflection" width="100%"></p>

*Your reflection follows you along the shore.*

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/lamps.webp" alt="Street lamps, a room full of lamp shadows, lamplight on snow" width="100%"></p>

*Lamps: walking past the street lights, a room where every lamp casts its own shadow, and lamplight on snow.*

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/day.webp" alt="Evening shadows, a flowing river, cloud shadows" width="100%"></p>

*Daytime: long evening shadows on the square, a flowing river, and cloud shadows over the mountain lake.*

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/weather.webp" alt="A storm, snow, and trees in the wind" width="100%"></p>

*Weather: a storm over town, snow, and trees leaning on a windy day.*

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/mod-off-and-on.webp" alt="The mountain lake with the mod off and on" width="100%"></p>

*The mountain lake on a summer afternoon. Mod off on the left of the line, on to the right.*

<h2><img src="https://phuicmt.github.io/SDV-Radiance-media/banners/make-it-yours.png" alt="Make it yours" width="100%"></h2>

<p align="center"><img src="https://phuicmt.github.io/SDV-Radiance-media/readme/tuner.webp" alt="The in-game tuner sliding open" width="100%"></p>

*F6 opens the tuner over the game, and you see every change right away. On a phone, open it from Generic Mod Config Menu: SDV-Radiance, Hotkeys & diagnostics, Open the Radiance tuner.*

<sub>Recorded in game at 1920x1080 with my own settings and mod list: DaisyNiko's Earthy Recolour and Tilesheets, Sharogg's Tilesheets and Stardew Valley Expanded. The pictures are in [PHUICMT/SDV-Radiance-media](https://github.com/PHUICMT/SDV-Radiance-media).</sub>

---

## What it is

SDV-Radiance adds its graphics on top of the picture the game already draws. It takes each
finished frame and runs a chain of shader passes over it on the GPU, and it draws sprite shadows
into the world that lean with the sun. Every effect has its own switch and sliders, and the
in-game tuner shows each change as you make it.

Shadows and reflections are made from what's actually on screen, so custom sprites, outfit mods
and recolours work without any per-mod patches.

## Features

**Lighting & shadows**
- Shadows for characters, animals, trees, objects, crops and map props, cut from each sprite's real outline. They lean and stretch with the sun and fade at dusk
- Moon shadows at night, stronger or weaker with the moon phase and season
- A dark spot under every tree, rock, fence and placed object, whatever the sun is doing (like ambient occlusion). It's set to zero by default
- Indoor shadows from each light (lamps, torches, fireplaces)
- Dynamic lighting that walls block: flat, unlit interiors get darker, and warm light pools around real light sources
- Rooms follow the clock. They're dim at dawn, get brighter through the morning, dim again before dusk and get really dark at night, and the colour goes from cool sky light to neutral, then warm, then blue
- Daylight through windows: each pane puts a patch of sun on the floor that leans with the same sun as the shadows, coloured by hour, season and weather
- Lightning: for a moment after a strike every shadow leans away from the bolt, the mod's own darkening lifts with the game's flash, and a warm afterglow fades out after it
- Up to 48 lights per scene. The nearest and brightest cast their own shadows, and the rest just give light. That's more than a room needs, so walking around never makes one light lose its slot to another
- Radiance cascades: bounce light is traced as rays over a grid of probes instead of spreading like a flood, so light gets around a corner instead of leaking through the wall. It's been the default since 1.7.0, and the older flood can still be picked
- Bounced light takes on the colour of what it hit, so a red barn throws warm light on the ground next to it. Off by default, because it's a style choice and not a fix
- Lamp shadows have real shapes: fences, bushes, boulders and placed objects block lamp light with their own outline, the edge gets softer further from the lamp, and the shadow cuts into the round glow the game draws around the lamp
- The side of a sprite that faces a lamp gets a bright edge in the lamp's colour
- Golden hour: in the first and last hour of sunlight, shadows stretch even longer

**Water**
- Reflections that follow the real shoreline in the art, down to the pixel (banks, trees, bridges, piers)
- You reflect in shallow water while wading. Crab pots, splashes, thrown items and the fishing bobber reflect too
- Ripples, sparkle and shimmer that differ between ponds and the sea and change with weather and season
- Light patterns (caustics) on shallow water, strongest near the shore and fading at night and in bad weather
- Rain leaves rings on the surface, with separate sliders for how many there are, how wide they get and how strongly they show
- A slider for reflection softness, from one sharp sample up to twice the normal spread
- Different kinds of water: ice reflects but doesn't ripple, flowing water and waterfalls look different from still water, and lava glows on its own
- Solid things sticking into the water keep their colour out of it, and the ripple fades out as it gets close to them instead of switching on and off pixel by pixel

**Weather & particles**
- Rain, snow and blowing debris drawn in three layers at three depths, each with its own angle and speed, and splashes where drops land. Green rain has its own look, and on windy days the wind picks up what the season has on the ground where you are
- Lightning bolts you can see on any map, using the game's own bolt art, but not with every rumble
- Raindrops on the edge of the screen that join up, get heavy and run down. In snow they turn to frost, and the edge of the picture mists over around them
- Particles drawn into the world instead of on top of it, so light, weather and colour grading affect them too: dust in a window beam, sparks from a real fire, fireflies on the nights the game has fireflies, blossom and leaves on days when the game has nothing in the air, and sparks circling a glow ring
- Wet ground after rain that dries on its own timer, with lamplight streaking down the wet ground. Off by default, with sliders in both menus
- Heat haze: hot air over lava bends what you see through it, like air over a road in summer

**Sprites & foliage**
- Wind in the trees: tree tops and bushes lean with the same wind as the rain, and gusts cross the map so a row of trees leans one after another instead of all at once. The lean is less than a degree, because pixel art has no pixels in between to bend into
- Leaves catch the light: patches of the canopy brighten and dim like leaves turning over in the wind
- Sprite relief: a lamp or the sun lights the side of a tree, building or fence that faces it, using a normal map made from the sprite itself. Off by default
- Sprites at double resolution: every sheet in use is upscaled 2x on the graphics card, per art family, so dialogue text can stay sharp while the world stays as pixelated as you like. There are two looks, the Scale2x rule and a softer xBR-style one baked onto shared pages. Each has a smoothing slider for the world, characters, portraits, items and menus, and there's a switch for items lying in the world and in the toolbar. All of it is off by default

**Windows & glass**
- Windows reflect you at the window's own height, holding your tool and walking in step. Glass reflects when it's darker behind the pane than in front, so the reflection is clear in daylight and fades after dusk as the room behind it lights up
- The glass also shows a tint of the sky's colour, a soft glare moving across the pane and the street in the lower half. After dark, the lamps outside show as small spots of their own colour in the panes facing them

**Atmosphere & color**
- Sun shafts: daylight through the trees falls in slanted patches on the ground, with their own strength and reach settings. On by default
- The dappled light under trees reaches the greenhouse floor too, broken up by whatever grows there, since the roof is glass and the sun is above it
- Lamp shafts from light sources in the world, rebuilt in 1.7.0 from the same shapes the lamp shadows use, with their own switch. Off by default
- Aurora on clear winter nights: slow green and violet curtains that show in the water too. On by default, and rare
- Shooting stars on clear nights in any season, in the sea as well as the sky
- Cloud shadows moving across the ground, with sliders for count, coverage, size and speed
- Day fog and night mist: two separate drifting effects, each with its own amount, strength and drift speed
- Bloom, tilt-shift depth blur, vignette, chromatic aberration
- Colour grading that sets the mood by time, weather and season, with auto-exposure, six ready-made looks and a slot for your own LUT. Off by default
- A blue light filter for tired eyes, on top of the grading or on its own

**Performance**
- Effect resolution: draw the effects at a fraction of the window size while the world stays full size, with FSR-style sharpening when they're scaled back up
- Four performance presets, and a benchmark that tests your machine and tells you what to set
- `radiance_report` prints what each part of the mod costs on your machine, averaged over the last 300 frames. You don't need to turn on any debug setting first

**Controls**
- Live in-game tuner (**F6** by default) with tabs by category: drag a slider to preview, pick a preset, save and load named looks
- Generic Mod Config Menu support, with a switch and sliders for each effect
- **F7** turns all of it on or off (you can change the key)

### Out of scope, engine limits
- Real ray tracing / RTX: Stardew is 2D sprites, with no 3D geometry, depth or normals
- Path-traced global illumination: there's no 3D scene for light to bounce around in

Everything here is a **screen-space or draw-time approximation** made for a 2D pixel-art game.

## Architecture

Two separate systems that meet at one point:

```
System A: post-processing (RenderPipeline.* + shaders/*.fx)
   Game1 render ──(hook)──> capture the game's active target during RenderedWorld
   └─ shader chain (ping-pong buffers on the GPU), in this order:
        [flood GI] → [lighting] → [water] → [cloud shadows] → [building shadows]
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

During SMAPI's `RenderedWorld` event the pipeline takes whatever render target the game has
bound, and writes the final result back into it. It never rebinds or clears anything the game
owns, and that's what keeps the world from going black. One Harmony postfix on
`ShouldDrawOnBuffer` makes sure a target is bound while effects are on.

**Shaders:** HLSL `.fx` compiled to `.mgfxo` (MonoGame Effect), loaded at runtime and applied with `SpriteBatch.Begin(effect: …)`.

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
4. Launch through SMAPI. Press **F6** for the tuner and **F7** to turn the effects on or off.

The default look is fairly strong. If it's too contrasty for long play, press **F6** and
pick the **Subtle** preset on the first tab, or turn up the blue light filter if your eyes get
tired. Both change live, and you don't need to edit any files.

Updates never overwrite an existing `config.json`, so your settings stay the same after an
update. To get a newer version's defaults, delete it and the mod will write a new one.

Included languages: English, Thai, Simplified Chinese. A Korean translation is available
as a [separate mod](https://www.nexusmods.com/stardewvalley/mods/49448).

## Compatibility

- Stardew Valley 1.6.x, SMAPI 4.0.0+.
- Sprite mods, Fashion Sense and recolours work automatically, since the mod reads the picture the game draws.
- Effects are client-side, so each player sees their own and nobody else needs it installed.
  Split screen and online co-op are supported: other players cast shadows and show up in the
  water, and each screen keeps its own data. Split screen has been tested with two players.
  Online co-op uses the same code but hasn't been tested with two machines.
- If another mod already draws the game's weather, the replacement weather steps aside and
  leaves it alone instead of fighting it for the same slot. With Cloudy Skies and Weather Wonders,
  it steps aside only on days they draw their own weather.
- Global God Rays works alongside it. The two cover similar ground, so turn one of them down.
  DynamicShader works alongside it too.
- Android works through SMAPI for Android. A first install on a phone starts on the Low spec preset,
  and one on built-in graphics (Intel, AMD Radeon built into the processor, Steam Deck, Apple M) on Performance.
- For a predictable look, run only one screen-space lighting or post-processing overlay at a time.

Per-mod test results are in [docs/compatibility.md](docs/compatibility.md).

### Known incompatibilities
- **SpriteMaster**, also released as *"Clear Glasses"* and *"Clear Glasses HD"*
  (`aurpine.ClearGlasses`): **pick one.** Both mods work on the same sprites and render targets.
  Smooth art turns itself off when one of them is installed, but shadows and water can still
  flicker next to Clear Glasses. This mod has its own Smooth art, with a choice of looks per kind
  of art, so you don't need both for that.
- **Clear Monocle is NOT in this group.** It's a fork that keeps only the upscaling, and its
  author added support for this mod on 2026-07-28. Two users have confirmed it works since then.
  Keep it up to date.

## Building

1. Install the .NET SDK (6 or newer, the repo builds against `net6.0`).
2. The project references [`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md), which finds the game folder by itself and deploys the build into `Mods/`.
3. Compile shaders with `mgfxc`, then copy the result into `assets/`:
   ```
   mgfxc shaders/bloom.fx assets/bloom.mgfxo /Profile:OpenGL
   ```
   **Version matters:** the `.mgfxo` format has to match the game's MonoGame build.
   Stardew 1.6.x uses **MonoGame 3.8.0 (build 1641)**, so compile with
   `dotnet tool install -g dotnet-mgfxc --version 3.8.0.1641`. That tool targets
   .NET Core 3.1. If you only have a newer runtime, run it with
   `DOTNET_ROLL_FORWARD=LatestMajor`. A newer `mgfxc` makes a file the game
   rejects with *"This MGFX effect seems to be for a newer release of MonoGame."*
   Compiled `.mgfxo` files are committed under `assets/`.
   **`dotnet build` does not compile shaders.** It only copies the `.mgfxo` files that are
   already there, so if you edit a `.fx` and rebuild, you're still testing the old shader.
4. `dotnet build` copies to `Mods/SDV-Radiance/`. Then launch through SMAPI to test.

If the game folder isn't found automatically, set `<GamePath>` in `SDV-Radiance.csproj` or a `stardewvalley.targets` file.

## Notes

- Each effect is one or more GPU passes, so it costs GPU time. Every effect has an on/off switch
  so you can fit the mod to your machine, and turning one off gives its cost back instead of
  leaving it running in the background.
- Rendering APIs can change between SMAPI and Stardew updates, so expect some maintenance now and then.

## Roadmap

Planned, not released yet:

- Puddles placed from the map data instead of guessed. That's why wet ground is still off by default
- Shadows that stop at whatever stands in front of them. Indoors a shadow can fall across a table or the saloon counter, because it's drawn at one sort depth taken from the caster's feet
- Reflections that shift with the camera like a real mirror image does. I've checked that this is physically right, but it isn't built yet. It's the other half of the reports that a reflection at the beach sits away from the player
- Water that continues under every bridge and pier, not only the ones whose map data allows it today
- Lamp shafts on by default, once a night walk with them on stops showing gaps that aren't there

Done since this list was first written: radiance cascades lighting and normal-mapped sprite
lighting (1.7.0, the first on by default and the second off), built-in sprite sharpening (1.7.0,
off by default), the rebuilt lamp shafts (1.7.0, still off), and per-light shadows for objects
and map props in their own shapes (1.7.0). After that: split screen costing two cameras instead
of four (1.7.5), the softer upscaling look with a slider per art family (1.7.5), lamp shadows
kept between frames instead of traced again, sunlight through trees reaching the greenhouse, and
a dark spot under objects on a slider that starts at zero (1.7.6).

<h2><img src="https://phuicmt.github.io/SDV-Radiance-media/banners/support.png" alt="Support" width="100%"></h2>

<p align="center">Radiance is free and will stay free. If you'd like to support it, a coffee or a sponsorship helps a lot, but it's never expected.</p>

<p align="center">
<a href="https://ko-fi.com/phuicmt"><img src="https://phuicmt.github.io/SDV-Radiance-media/buttons/kofi.png" alt="Buy me a coffee on Ko-fi" height="48"></a>&nbsp;&nbsp;
<a href="https://github.com/sponsors/PHUICMT"><img src="https://phuicmt.github.io/SDV-Radiance-media/buttons/sponsor.png" alt="Sponsor on GitHub" height="48"></a>
</p>

<p align="center">Something look wrong? A bug report with <code>radiance_report</code> run from that spot helps the most.<br><br>
<a href="https://www.nexusmods.com/stardewvalley/mods/49397?tab=bugs"><img src="https://phuicmt.github.io/SDV-Radiance-media/buttons/bugs.png" alt="Report a bug" height="36"></a>
</p>

## License & credits

MIT, see [LICENSE](LICENSE). Credits for third-party frameworks, tools and any reused code are in [CREDITS.md](CREDITS.md).

Translations: Simplified Chinese has been included since 1.2.2, by **Rime961** (1.2.2 through
2.1.3) and **passersby10086** (the 2.2.0 settings). A few newer settings, including the share
codes, still show in English. Thai is by me and complete. Other languages are separate mods on
the Nexus by their own translators: French by [Deovos](https://www.nexusmods.com/stardewvalley/mods/50089), German by
[Neko41](https://www.nexusmods.com/stardewvalley/mods/51482), Japanese by
[tanakakaku3i](https://www.nexusmods.com/stardewvalley/mods/49750), Korean by
[jjongleee](https://www.nexusmods.com/stardewvalley/mods/49448), Simplified Chinese by
[Rubbish404](https://www.nexusmods.com/stardewvalley/mods/49647) and by
[9843177](https://www.nexusmods.com/stardewvalley/mods/52007), Spanish by
[Papaya2](https://www.nexusmods.com/stardewvalley/mods/51510) and Vietnamese by
[VotriValley](https://www.nexusmods.com/stardewvalley/mods/52046).
