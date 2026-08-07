# Changelog

All notable changes to SDV-Radiance. Older releases are documented on the Nexus page.

## 1.5.3

### Translations

- Chinese brought fully up to date by Rime961, who sent it in unprompted after noticing the text
  had changed (thank you). It now covers all 273 keys, including everything added across 1.5.0,
  1.5.1 and 1.5.2: the window settings, the performance tab and its benchmark, every tuner tab
  description, and the shadows-per-character setting.

### Fixed

- A light you carry no longer goes dark while you walk. Reported with a glow ring, and it was
  every carried light: a lantern, a horse's lamp, the ring. Each light is given a name so that the
  next frame can tell it is the same light and not a new arrival, and that name was where the light
  was standing. For a lamp post or a window that is exactly right. For something in your pocket it
  is nonsense: eight world pixels is about two frames of walking, so a carried light was handed a
  new name twice a second, and every time it got one the pipeline saw a stranger arriving and
  started it from nothing to fade it in. It never finished, because two frames later it was a
  stranger again. So it sat at a twentieth of its brightness for as long as you were moving and
  only came up once you stood still, which is precisely how it was described. Anything the game
  gives a name of its own now keeps it, moving or not. This was not new in 1.5.2, but 1.5.2 made
  the fade in nearly three times slower, which is what took it from a flaw nobody had mentioned to
  something two people reported within hours of each other.
- Rooms light up fully again instead of only near you. A map lights a room by repeating a light:
  the vanilla saloon has sixty four of them, all the same, laid a couple of tiles apart, and a
  single one of those already reaches about six and a half tiles, so their pools sit almost exactly
  on top of each other. It is not sixty four lamps, it is one even wash painted by repetition. The
  shader has twenty four places, so feeding it sixty four meant most of a room went without one no
  matter how carefully the twenty four were chosen. Neighbouring map lights of the same colour and
  size are now drawn as one wider light centred on the group, which is close to the sum of what
  they drew and brings that saloon down to about twenty five. Only the map's own lights are
  merged: anything you carry, place or light keeps its own identity.
- Which lights get drawn in a crowded room is no longer a lottery. The shader has room for
  twenty four at a time, and choosing between them went by how bright a light is, how far it
  reaches, and whether it is on screen. That works where lights differ. The vanilla saloon carries
  sixty four map lights, all the same brightness and the same radius, laid a couple of tiles apart
  to light the room evenly, so every one of them scored exactly the same and the choice fell
  through to a tie-break on where the light happens to stand. The set was redrawn every time the
  camera moved. That is the pool that switches on beside you as you walk, and on a wide window it
  is a whole corner of a room left dark while a lamp on the far wall holds a place. How near the
  middle of the screen a light is now counts too, so the ones that lose are the far ones nobody is
  looking at, and walking slides the order along instead of shuffling it. An edge light is still
  worth a third of a middle one, so nothing is refused while there is room.
- Lights leaving the array fade out instead of vanishing. Entering was always a fade and leaving
  never was, so in a room offering more lights than the array holds, the last places changed hands
  constantly and every handover was a pool blinking out. 1.5.2 answered that by fading the last
  places in proportion to how far each scored clear of the best light that had just missed out,
  which is a number recomputed every frame from the whole scene. Where the lights in a scene are
  alike, which a street of identical lamps is, that puts all of them inside the same band and dims
  them as a group. Leaving is now simply the mirror of entering: each light fades out over its own
  frames at its own last position, and nothing another light does can move it. The slots are filled
  from what is actually lit rather than from the ranking, so a light handing its place over
  crossfades with the one taking it.
- Walking up to a lamp, its pool fades in from nothing instead of arriving half lit. A light that
  had earned a place in the array but was still waiting for the one it was replacing to finish
  fading was allowed to brighten while it waited, so the first half of its fade in happened before
  anything was on screen and the pool appeared at about forty percent. Brightening now only happens
  on the frames a light is actually being drawn.
- A fire looks like a fire again in a room the mod has darkened. Everything on screen was scaled
  by how dark the room was decided to be, and a flame is not a surface the room's light falls on,
  it is where the light comes from, so scaling it by the room made no sense: the hearth was dimmed
  and then had its colour drained on top, and came out a muddy brown smear with none of the near
  white it is actually drawn with. Glass already had an exemption from this for the same reason,
  and the flame now has the same one. It applies only to pixels that are both nearly on top of a
  light and bright in the original art, so the boards in front of the fire still take the room's
  colour, and a dark floor under a lamp is not mistaken for a lamp.
- A hearth keeps lighting the floor after the sun comes up. Its circle of light on the boards was
  scaled by how much the room had been dimmed, so as a room filled with morning light the circle
  faded to nothing while the fire was plainly still burning. A fire does not stop lighting the
  floor because it is daytime. It now keeps a share of it in any room with windows, and stays at
  exactly zero outdoors and in caves, where a pool of light at noon would be wrong.
- Water no longer flickers because an unrelated mod reloaded an unrelated map. Any map asset being
  reloaded threw the whole water surface away and rebuilt it, whether or not it had anything to do
  with where you were standing. On a modded install that is a steady drip of rebuilds coming from
  maps you are nowhere near, under a player who has not moved, which is a flash of water with no
  cause anyone could point at. Only a reload of the map you are actually standing on can do it now.
  Found by adding the diagnostic below and then reading our own report: a station map belonging to
  another mod was reloading while the player sat indoors.

### Changed

- New default lighting values, for NEW installs only. An existing config.json is never touched, so
  nothing changes for anyone already playing. The old defaults had a flaw worth stating plainly:
  the shader multiplies the scene by the lightmap and that multiply is clamped, so it can only
  darken, and the one term that adds light needs the lightmap to pass full brightness before it
  does anything at all. At the old brightness a lamp's pool reached about half of that, so for
  everyone running the defaults that term was doing nothing, ever. Lamps could make a floor less
  dark and never bright, which is the "night is dark and the lit places are dark too" that several
  reports described from different angles. Brightness, pool size and bounce strength are all up,
  night darkening and shadow strength are down to match. If you preferred the old look, the tuner
  (F6) has every one of these on a slider.
- A new install now says Cinematic in the look dropdown and ships the Cinematic numbers, again for
  NEW installs only. It used to say Custom, meaning hand-tuned, in an install nobody had tuned, and
  the bloom it shipped was more than double what that preset actually asks for. Contrast goes back
  to the preset's own 1.15, which 1.5.0 had softened to 1.10 on the basis that it read as punchy;
  that note was made when a lamp could not brighten anything and contrast was doing all the work,
  which is no longer true. Nothing applies a preset when the game loads, so this changes a label
  and two numbers, not your settings.

### Diagnostics

- `radiance_report` now answers the two questions that reports about water and about indoor light
  keep raising and that no screenshot can settle. For water it keeps a running record of the last
  few dozen things that changed on the surface, so running the command AFTER seeing something wrong
  is enough to show what led up to it, along with how often the surface is being rebuilt, how wide
  your view is in tiles, whether the shoreline is the map's own or a guess, and which mod last made
  the game reload a map. For indoor light it says whether the room counts as having windows, how
  many window lights the game actually published, how much the room was dimmed, and what a lamp's
  pool on the floor is worth. "The light from my window is gone" has three unrelated causes that
  look identical on screen, and one of them is not a fault at all.
- A failure inside the effect chain now writes the whole error, once per session, instead of a
  one-line message repeated every frame. The message alone named neither the pass nor the reason,
  which meant a report of it could not be acted on.
- New console command `radiance_waterwatch`, which prints what changes on the water surface frame
  by frame while you walk. Console only, so no new text to translate.

### For translators

No new or changed keys in this release.

## 1.5.2

### Added

- A setting for how many shadows one character casts indoors and after dark, on the Shadows tab
  and in the config menu. It is a look control and a performance control at once: each shadow is a
  full soft silhouette drawn for that character, so one costs a third of three in a room full of
  lamps, and past about three the shadows stop reading as a body lit from a few directions and
  start reading as a smudge. The default is three. The performance presets set it too: three on
  Quality, two on Balanced, one on Performance. The sun outdoors is one light and is not affected.

### For translators

Three new keys, no changes of meaning. English and Thai are filled in; the rest fall back to
English until translated.

- `tuner.shadowcasts`
- `config.shadows.casts.name`, `config.shadows.casts.tooltip`

### Fixed

- Water no longer stops dead in a straight line beside a bridge. A tile counted as decking as soon
  as a quarter of it was planking, which is the right bar for "is there something to walk on here"
  and far too low for "is this tile still water": a parapet or a plank end clipping the edge of a
  water tile took the whole tile out of the water, all 256 pixels of it. A tile that is mostly
  water now stays water, and the planking on it is cut away pixel by pixel further down the
  pipeline, which was always happening and was achieving nothing because the tile had already been
  thrown away. Whether this clears the bridge to the mines depends on which tilesheets are
  installed, so that report stays open until the person who filed it says otherwise.
- The bounce lighting no longer flickers along with a fire. A hearth's flame wobble was multiplied
  into the bounce grid, which is a CPU sweep that cannot afford to run every frame, so the wobble
  got sampled at the rebuild rate and held in between: the bounce moved in steps while the direct
  pool around the same fire moved smoothly, and two rates beating against each other is what read
  as the floor around a lamp flashing. The bounce is light that has crossed the room and come back
  off a wall, which is the half that should not be snapping anyway. The flame still breathes where
  you can see it happening, in the pool it casts and the shadows it throws.
- Lamps and window light no longer pulse in a busy room. The saloon offers seventy-two lights for
  twenty-four slots, so the scores deciding which ones get in sit very close together, and a fire's
  eight percent wobble was one of the things being scored. That was enough to reorder the list
  around the cut, and the lights near it swung between full brightness and nothing on the flame's
  own cycle: one hearth quietly breathing, half the room's lamps pulsing. Flicker now changes how
  bright a light is and never which lights exist.
- A light that loses its place in a busy room fades out instead of blinking off, and one arriving
  takes about a third of a second rather than an eighth, which is long enough to read as a light
  coming on rather than a light being switched on.
- People standing across a room from you keep their shadows. Every character was sharing one set
  of six lights, picked for being nearest the middle of the screen, so walking to one end of a shop
  dropped the lights around the people at the other end and their shadows vanished while they were
  still in plain sight. Which light matters is a question about the person casting the shadow, so
  each of them now answers it for themselves, and the count sets a distance rather than a place in
  a queue, so a light crossing that distance fades out instead of blinking.

### Known issues

- **Split-screen is not supported yet, and online co-op only shadows your own farmer.** Both come
  from the same gap: the mod keeps one set of camera-shaped working data, and a second screen is a
  second camera looking somewhere else. Traced this release rather than guessed at. On a split
  screen the first player's half is close to correct while the second player's water effects are
  cut to whatever rectangle the first player's camera happened to cover, and neither farmer's
  shadow is reliable. In online co-op the other players cast no shadow and no reflection at all,
  because the list of things that cast one never included them. Being worked on; `radiance_report`
  and the console command `radiance_screenwatch` collect what is needed if you can reproduce it.

## 1.5.1

### Added

- A master switch for window effects, covering everything the mod does with a window: daylight
  coming into a room, and the warm glow on house windows outdoors after dark. The outdoor half had
  never had a switch of its own. Someone running a dedicated window mod can now turn all of it off
  in one click and keep the rest, and the street dims down over about a second rather than every
  lit house snapping dark at once.
- Indoor window daylight is now two switches instead of one, split where a second window mod
  actually collides. "Window beam and glass" is the visible half: the lit pane, the beam and the
  patch of sun on the floor, all of which a dedicated window mod draws too. "Window light in the
  room" is the half it cannot do, because painting a beam over the picture does not tell the
  lighting where the daylight came from. Running both mods, the sensible setting is theirs for the
  beam and ours for the room, and that is what you get now instead of having to switch one off.
- Radiance now steps aside on its own when Dynamic Windows is installed: on the first launch it
  turns off its own beam and glass, says so in the log, and leaves the room lighting alone. It
  records that it has done this once, so turning the beam back on sticks.

### For translators

Eight new keys, no changes of meaning. English and Thai are filled in; the rest fall back to
English until translated.

- `tuner.windoweffects`, `tuner.windowbeam`, `tuner.windowroomlight`
- `config.lighting.windoweffects.name`, `config.lighting.windoweffects.tooltip`
- `config.lighting.windowbeam.name`, `config.lighting.windowbeam.tooltip`
- `config.lighting.windowroomlight.name`, `config.lighting.windowroomlight.tooltip`

### Performance

- The flood lighting map rebuilds when one of its inputs changes, instead of twenty times a second
  regardless. Standing still in a scene with no fire on screen, it used to redo about a thousand
  tile lookups and three full-window sweeps at a fixed clock and produce the same texture every
  time; measured, that was the second most expensive thing the mod did, in every scene. It still
  rebuilds instantly when you cross a tile, a light appears or goes out, a window fades, or the
  ambient tint moves, and a scene with a hearth or torch on screen keeps the fast clock so the
  flicker stays alive.
- The object-shadow scene walk no longer runs twice a frame. Preparing the shadows used to
  re-enumerate every on-screen tile, object, furniture piece and critter just to confirm their
  silhouettes were already baked, then the draw pass walked it all again to draw them. The draw
  pass now reports anything it found unbaked and the next preparation bakes exactly that list,
  which while you stand still is nothing at all. A full walk still happens when it means
  something: the sun angle moving on, or a new area.
- The lamp occluder grid (what blocks per-light shadows) now rebuilds the same way: crossing a
  tile, chopping a tree, breaking a clump or placing a building rebuilds it at once, and otherwise
  it coasts. It had been redoing roughly nine hundred tile lookups twenty times a second for a
  grid that nothing per-frame ever changes.
- The player's shadow silhouette is no longer re-baked several times a second while nothing about
  it changed. The periodic refresh exists for mods that animate hair and accessories on their own
  clock, so it now runs only when such a mod (Fashion Sense) is actually installed; everyone else
  re-bakes only when the pose changes. Measured before the change, this bake was the single most
  expensive thing the mod did per frame, ahead of drawing every shadow on screen.
- The full-colour twin of that bake, which exists only so the water reflection can mirror the
  player, is skipped whenever no water is on screen. Indoors and on dry maps that halves the bake
  again; the moment water scrolls into view it is rebuilt before the mirror reads it.

### Changed

- The benchmark on the Performance tab now measures the shadow pass as well as the effects. It
  could only ever see the full-screen effect chain, so on a heavily modded game it could report
  that the machine had room to spare while shadows were the thing eating the frame. Shadow cost is
  now counted against the same budget the recommendation comes from, and when shadows are most of
  what the mod costs you, the result says so and names the setting that helps, rather than
  suggesting an effect resolution that would not.
- `radiance_report` now includes what the mod costs per frame, broken down by part, without
  needing any debug setting turned on first. The grid rebuilds report as four separate lines
  (flood lightmap, flood occluders, light occluders, water mask), so a stutter can be pinned on
  the one that owns it instead of on the group.

### Fixed

- Small pools no longer change how hard they ripple as you walk past them. A body of water is
  given gentler waves when it is small, and how small it was got measured against the edge of the
  area the mask covers, which travels with the camera. A tide pool near the edge of the screen
  therefore counted as full size, and its ripple and its glints doubled in a single frame as you
  walked toward it and halved again on the way back. The size is now measured over the whole map,
  so it is the same wherever you are standing.

## 1.5.0

### Added

- Interiors now follow the time of day. A farmhouse used to look the same at six in the morning
  as at noon, and the same again at midnight. Rooms are dim when you wake, fill in through the
  morning, sink again before dark and are genuinely dark at night. The colour moves with the hour
  as well: cool while the room is still lit by open sky rather than by the sun, neutral in the
  middle of the day, warm before dusk, and blue at night. Only rooms with windows are affected.
  Caves, the mines and the volcano are untouched.
- Daylight comes through the windows. Each pane lays a patch of sunlight across the floor that
  leans with the same sun your shadows follow, so it stretches long and low in the morning,
  shortens toward noon and swings the other way in the evening. Its colour and strength follow the
  hour, the season and the weather. The glass itself is lit from outside rather than by the room,
  so it stays bright while the room around it is dark.
- A fire lights the room it is in. A hearth or a lamp in a darkened interior now lays a real
  circle of light on the boards in front of it, flickering with the flame and blocked by walls
  like any other light.
- Effect resolution, with sharpening. The effects can be computed at a fraction of your window
  size while the game world stays full size, which costs far less GPU work. The image is sharpened
  as it scales back up, and the sharpening has its own slider.
- Quality presets and a benchmark. Three one-click presets, and a button that measures your
  machine for about ten seconds and tells you what to set. Both live on a new Performance tab.

### Changed

- Up to 24 lights can light a scene at once, raised from 16. The nearest and brightest still cast
  their own shadows; the rest add their pools of light. A town at night with many lamps is
  noticeably better lit than before.
- The F6 tuner is larger and scales with your window, every tab carries a one line description of
  what it does, and the tabs have icons. Performance and Diagnostics have their own tabs.
- The Generic Mod Config Menu pages are reorganised to match the F6 tabs, so the two describe the
  mod the same way.
- One command now writes a whole bug report for you. Stand where something looks wrong and type
  `radiance_report` in the SMAPI console: no coordinates, no arguments. It writes
  `Documents\Radiance-Dumps\radiance-report.txt` with the versions, the tile you are on and a small
  map of the ones around it marking water, bridge decks, walls and ground, the time, season and
  weather, which effects you had switched on, the label check for everything on screen, and the
  installed mods that could be involved, with known-incompatible ones flagged. Attach that file and
  there is nothing else to type. Almost every water report is about a shape rather than one tile,
  and "which map or mod is that bridge from" was usually the one thing missing.
- The stock colour grade is a little softer out of the box, with contrast moving from 1.15 to 1.10.
  The most common note from people who liked the look was that they turned the contrast down before
  settling in for a long session, so the shipped starting point now sits halfway to the Subtle
  preset. This only changes new installs. If you already play with Radiance, your own value is
  written in your config and is left exactly as it is.

### Fixed

- Light pools no longer blink on and off as you walk through a room with several windows or lamps,
  such as a shop. Ranking the lights ran too late to matter, so a scene between nine and sixteen
  lights handed the shader whichever ones the game happened to list first, and taking a single
  step reshuffled them. Lights are now ranked properly, keep a stable order, and a new one fades
  in rather than appearing whole.
- The picture no longer jumps in brightness as you walk past water. A leftover effect meant to
  darken the last few pixels of ground at a waterline was instead dimming almost the whole screen
  by about 4% whenever water was anywhere nearby: on the beach it touched 99% of the frame, sand
  far from the sea, the cabin, the boat and the player included. Because it switched on and off
  with whether water was near you at all, walking past a river or a pond changed the brightness of
  everything you could see. It has been removed. Measured at the coordinates a reporter gave from
  their own game, the jump went from 4.1% to 0.2%.
- Much less of the long-standing "lighting spontaneously gets dimmer and brighter as I walk
  around". Besides the above, a light was cut from the picture at a fixed distance past the edge
  of the screen, and whatever it was still contributing went with it in a single frame. Its
  contribution now tapers to nothing as it travels off the edge, so there is nothing left to lose
  when it goes. Measured on a scripted walk through town, brightness discontinuities went from
  twenty seven in twenty seconds to none. That report has been open a long time and had more than
  one cause; this is not being called closed until the people who raised it say so.
- The water pass now fades out of the frame instead of being dropped from it when water leaves
  the area, so switching it off costs nothing visible.
- The player and everything else on screen no longer ripple along with the water when the effect
  resolution is lowered.
- The last effect you switch off finishes its fade instead of cutting out on the final frame.
- Windows no longer stay lit at midnight. The glass is deliberately held out of the room dimming,
  because a bright white pane multiplied by a dark room turns a murky grey and reads as dirty
  rather than as a window. That exemption did not follow the sun, though: after dark the room went
  dark around a window that was still as bright as noon. It now fades with the daylight outside, so
  the glass is the brightest thing in the room by day and dark with the room at night.

### Performance

- Reflections of scenery are cached and reprojected as the camera moves instead of being redrawn
  every frame, which removes about three quarters of that work.
- The water pass is skipped entirely when no water is on screen.
- Colour grade and vignette run as a single pass where the result is identical, one less
  full-screen pass per frame.
- Measured on the release build across eight scenes at 1707x960: a farm costs 0.22 ms per frame,
  water scenes 0.28 to 0.29 ms, and the heaviest scene measured, a town at night full of lamps,
  0.33 ms. That is about 2% of the frame budget at 60 fps. Lowering the effect resolution to 0.75
  takes roughly a third off, and 0.5 takes roughly half.

### For translators

Twenty eight new keys, and one whose meaning changed.

New:

- `config.section.lens`, `config.section.water`, `config.section.perf`
- `config.renderscale.name`, `config.renderscale.tooltip`
- `config.rendersharpness.name`, `config.rendersharpness.tooltip`
- `config.perfpreset.section`, `config.perfpreset.quality`, `config.perfpreset.balanced`,
  `config.perfpreset.performance`
- `config.bench.section`, `config.bench.run`, `config.bench.running`, `config.bench.apply`
- `tuner.desc.looks`, `tuner.desc.perf`, `tuner.desc.colorgrade`, `tuner.desc.bloom`,
  `tuner.desc.lens`, `tuner.desc.lighting`, `tuner.desc.shadows`, `tuner.desc.godrays`,
  `tuner.desc.water`, `tuner.desc.cloudshadow`, `tuner.desc.fog`, `tuner.desc.camera`,
  `tuner.desc.debug`

Meaning changed:

- `config.section.finishing` was "Water & finishing" and is now "Vignette & chromatic aberration".
  Water moved to its own section, so the old wording no longer describes the page.

## 1.4.1

### Fixed

- Fishing gear no longer waves with the water. The cast power meter, the rod itself, the line and
  the floating bobber are all drawn in the world layer, so the ripple bent them like anything else
  over water, and judging a max cast was a guess.
- An NPC fishing at a festival had its reflection start at the rod tip instead of the feet. The
  ice fishing pose is drawn from a frame four tiles tall whose lower half is the rod reaching over
  the water, and the mirror anchored to the bottom of the frame rather than the boots. The same
  fix removes the disembodied mirrored head that a bystander standing tiles from the shore could
  cast into the water.
- Time no longer changes the picture in steps. The game clock advances in ten minute ticks, and
  everything driven by it lurched once per tick: fog tint, night warmth, golden hour, window glow,
  lamp dimming, and the sun and moon shadow angle. All of it now glides through the tick. Hard
  boundaries became ramps too: rooms ease into their night darkness around 19:00 instead of
  snapping, the stronger outdoor night shadows arrive gradually, and moon shadows fade in over the
  first half hour of true dark.
- Toggles fade. Rain rings on water, the ripple pausing during a cutscene, and the water shimmer,
  vignette, chromatic aberration, tone map and tilt-shift mode switches all ease over a fraction
  of a second instead of flipping the frame.

### Performance

- Turning every water setting off now actually stops the water work. The water surface map was
  still being rebuilt on every camera tile crossing even with all water features disabled, which
  showed up as stutter near water on machines that had disabled water for performance.

### Added

- Diagnostics tab in the F6 tuner with the Debug logging toggle, so the [diag] and [perf] timing
  lines can be flipped on mid-session without opening Generic Mod Config Menu or config.json.

### Translations

- Chinese translation updated by Rime961, synced to the current text (thank you!).

### Known issues

- Some scenes may still step slightly brighter or darker while walking. This could not be
  reproduced on 1.4.x; if you still see it, a report with the spot helps.

### For translators

- No new or changed i18n keys in this release.

## 1.4.0

### Fixed

- Water reflections stopped short of the shore. A tile holding both bank art and water lost its
  reflection completely, so every shoreline, bridge arch and pier edge had a dead strip where the
  water met land. Reflections now hang from the art's own outline instead of the tile boundary, and
  a character standing at the water's edge is mirrored from the edge rather than a tile out.
- Waterfalls had no water effect at all, in any season. Falling water is drawn on a layer the
  surface pass never read, so a waterfall and the pool at its base were treated as dry rock.
- Standing in water removed your shadow. A body wading casts a shadow across the surface, and the
  old rule also made the shadow pop in and out as you crossed into deeper water.
- Bridges showed an outline in the rain. The gaps in a bridge's railing and its own painted shadow
  were being rippled as water seen through the planks.
- The last pixel of water along every shoreline rendered at a sixth strength, which read as a dim
  band that flickered like notches as the water level bobbed.
- The player's mirrored image froze in one pose for anyone playing with directional shadows turned
  off, because the reflection draws from a bake that only the shadow pass refreshed.
- A strip of water above the player's head stopped rippling: the exclusion silhouette that keeps
  the ripple off your sprite was anchored 10 pixels too high.
- Beach tide pools rippled without reflecting. The rock rim around a pool was being read as a
  bridge deck, and decks block reflections by design.
- Crab pots left the water notched beside them instead of around them, and cast a shadow that
  matched neither their shape nor their position.
- Water under a bridge arch, and the strip of water north of a bridge parapet, both lost their
  reflection to the bridge's tile.

### Changed

- Rain, storm and snow no longer remove cloud shadows. They now keep a softened overcast layer:
  fewer, larger, slower banks at reduced strength, which reads as a heavy ceiling instead of the
  effect looking broken.

### Performance

- Sprites, canopies and placed objects with no water within a few tiles are no longer stamped into
  the water masks every frame. On a map with water in one corner this was a screenful of draw calls
  per frame spent on sprites nowhere near it.

### Added

- Console commands for diagnosing water problems: `radiance_verify` scores the mask against the
  painted labels for everything on screen, `radiance_march` lists tiles that ripple without a
  reflection, `radiance_tile x y` prints one tile's full story, and `radiance_debug <channel>`
  overlays the mask, the label difference, the reflection channel and more.

### Known issues

- Rivers and lake edges outside winter still miss water where the painted labels have gaps.
- Some scenes still step slightly brighter or darker as you walk.
- God rays remain off by default while the effect is rebuilt.

### For translators

No new keys, and no meaning changes. Nothing to do for this release.

## 1.3.3

### Fixed
- Cutscenes still cast shadows for a few things the 1.3.2 fix missed: small objects standing
  where a scripted character walks (the game hides these too, not just townspeople), and
  furniture during events outside the Farm and farmhouse.
- Fishing event extras (Squid Fest, the winter fishing derby) had no shadow at all, and once
  fixed their shadow floated away from their feet with the tackle drawn as part of it. All three
  are fixed: they now cast a shadow anchored correctly, without the fishing line.
- The event SKIP button could be covered by the water effect, and briefly showed the effect
  underneath it at the moment it was pressed.
- Shadows switching between sunlight and lamplight (dusk, or walking indoors) now fade over
  about a second instead of snapping to the new direction in one frame.

### Known issues
- Bridges show an outline in the rain. Fix planned for 1.4.0.
- Some scenes still step slightly brighter or darker as you walk.

### For translators

No new keys, and no meaning changes. Nothing to do for this release.

## 1.3.2

### Fixed
- Cutscenes showed shadows for townspeople who were not in the scene. During an event the game
  draws only the characters the scene needs, and the shadow pass was still casting for everyone
  who lives on the map. The same characters were also being mirrored in water and cut out of the
  water surface. Reported by a player on Nexus.

### Known issues
- Bridges show an outline in the rain. Fix planned for 1.4.0.
- Some scenes still step slightly brighter or darker as you walk.

### For translators

No new keys, and no meaning changes. Nothing to do for this release.

## 1.3.1

### Fixed
- Entering Galdoran Crimson Badlands (Stardew Valley Expanded) froze the game for around
  40 seconds. The desert was affected by the same problem, more briefly. Maps whose tilesheet
  crossed an internal size limit were being read back one tile at a time instead of once.
- Willy's boat carried the water ripple at the Fish Shop dock and in the Boat Tunnel, and the
  reflection could climb onto the hull. Nothing drawn in front of water takes a water effect now.
- Objects sitting in water (a sea urchin in a pond, for example) rippled with the surface.
- Bridges and piers counted as open water underneath, so anything standing on one lost its shadow.

### Changed
- God rays are off by default. The effect currently treats bright surfaces as light sources, so
  large pale sprites such as festival banners blow out to white. It is being rebuilt for 1.4.0.
  If you had it on and want it back, turn it on again in the config or with F6 — this change only
  touches the default for new installs and is applied once for existing ones.
- Removed the "Min light size for shadows" option. It no longer controlled anything.

### Known issues
- Bridges show an outline in the rain. Fix planned for 1.4.0.
- Some scenes still step slightly brighter or darker as you walk. Reduced in this version but
  not resolved.

### For translators

No new keys. No meaning changes — god rays turning off by default is a config change, not a
wording change, so `config.godrays.enabled.name`/`.tooltip` stay as they are.

Removed (delete these from your language file, they are no longer read):
`config.shadows.minlightradius.name`, `config.shadows.minlightradius.tooltip`, `tuner.minlightradius`

## 1.3.0

Water coverage no longer comes from guessing at pixel colours: it comes from the game's own
water data plus a hand-painted label set that ships with the mod. Winter is where that shows
most, because snow passes every blue-dominance test ever written. Alongside it, painted
labels now drive light: shop signs and forge fires glow, windows light up after dusk, and
glass stops blocking lamplight.

### Fixed
- Water in winter no longer spills onto snow, and no longer leaves flat unaffected blocks along
  a bank. Coverage used to be refined by a colour test, and snow beats that test edge to edge,
  so the mask both crept up snowbanks and had to be trimmed back by hand map by map. Coverage is
  now the game's water data refined by labels only, with no colour test left in the pass.
- A character sitting on a bench or chair is no longer drawn behind it. Props painted into the
  map redraw their own tile above their cast shadow so the shadow does not darken the prop, and
  that redraw sorted a full tile in front of anyone sitting on the tile. Placed seat furniture
  had the same conflict. Both now sort under a body on their tile.
- Reflections of NPCs and animals sat ten pixels lower than the player's, and a seated NPC
  reflected where it was not drawn. Every body now uses one anchor.
- Butterflies, birds and falling leaves reflected at full strength while characters faded with
  depth, and left a hole in the water where their own reflection landed on land. They go through
  the same reflection path as everyone else now.
- God rays no longer stream off things that are not lights: snowy ground, a pale NPC beside a
  lamp, a white fence. Sprites are excluded outright, and on snow the brightness bar rises to
  just under snow's own.
- Walking between lamps no longer drags one set of rays across the screen to the next lamp. Every
  light on screen has its own beams now, up to three at once, each fading in and out on its own.
- Speech bubbles and emotes over water are no longer rippled and tinted with it. They are drawn
  inside the world layer, so they needed the same exclusion sprites get.
- Lamp pools and god rays no longer read as bright at midday. Both sink to about a third through
  the middle of the day and return by early morning and late afternoon. Indoor lamps and night
  are unchanged.
- Effects no longer pop. Every effect eased in when it appeared but was cut in a single frame when
  it stopped, so switching one off, stepping indoors, opting a room out of the water effect, or a
  cutscene starting all snapped. Presence fades both ways now, and the two lighting models
  cross-fade instead of leaving the room briefly unlit.
- Bodies mirror on the wet fringe of a beach as well as on open water, so someone standing on the
  tide line keeps a whole reflection instead of a detached lower half.
- The bathhouse pool gets water effects. The game never declares it as water, so without a label
  it had none.

### Added
- Painted light sources. A labelled glowing surface emits light with the colour read from its own
  art, so a lit shop sign glows in its own colour and a forge glows orange, with no per-object setup.
- Window lights, outdoors. Labelled windows glow after dusk and go dark at their own bedtime, so a
  street dims house by house rather than all at once. Daylight coming IN through a window is not
  switched on yet.
- Clear glass. A labelled pane no longer blocks light, so a lamp beside a display case, a shop
  door or a fish tank lights what is behind the glass instead of stopping at it. Glass reflecting
  what stands in front of it is a separate piece of work and is not in this release.
- Hot water, treated as water everywhere. Painted on the bathhouse pool, which the game never
  declares as water at all. The class is there for modded hot springs too, but none are painted yet.
- Stone and plank bridges are recognised without a label: a narrow strip of non-water with water
  on both sides is a bridge. Neither of the older tests could see a stone one.
- The label set shipping with the mod now covers 113 painted tilesheets: 5,534 water tiles, 3,141
  flowing, 1,809 glass, 1,379 window, 1,356 light source, 114 mirror, 103 deck, 102 ice, 57 lava,
  18 hot.

### Changed
- Reflections of characters reach further into the water and fade with depth below the feet,
  strongest at the feet. A body at the water's edge reads clearly; one standing back from it fades
  out instead of leaving a fragment floating.
- Waterfalls are tagged as flowing water in the mask, so a body never prints on a waterfall face
  while the ripple stays.

### For translators
No i18n keys were added, removed or changed in this release.

## 1.2.3

Never released as its own file. Everything below shipped inside 1.3.0, so players moved from
1.2.2 straight to 1.3.0 and the 1.3.0 release notes repeat these entries.

### Fixed
- Plank bridges, piers and boardwalks no longer draw over the character standing on them, and no longer appear as a second copy of themselves offset to one side. A bridge you walk on top of was being treated as a standing prop like a fence, because it is not open water and its art has gaps between the planks. It got a fence's leaning shadow, plus a redraw of its own tile on top of that shadow, and both landed on the tile the character was standing on. Any Buildings tile the map marks as walk-on-top is now excluded.
- Cloud shadows no longer drift across the ground during rain, storms and snow. An overcast sky has no direct sunlight left for a cloud to block, so the shadow banks should not be there at all. God rays and the night mist already stepped aside in this weather; cloud shadows never did. They fade out and back in over about a second, so a mod that changes the weather mid-day will not make them pop.

### Changed
- Clear Monocle is no longer listed as incompatible. Its author shipped explicit support for Radiance, confirmed by two users. Make sure Clear Monocle is up to date.

### For translators
No i18n keys were added, removed or changed in this release.

## 1.2.2

### Fixed
- Beaches no longer show rectangular blocks of water effect over the sand. The mask covers a wave tile as a square, but the sand painted inside it is not, and a floor on the colour test meant that sand still took most of the ripple and reflection. Pixels that are clearly dry land (warm and saturated: sand, tilled soil, dirt paths, bare wood) are now excluded. This also stops the effect spilling onto sand as a wave retreats, ripple appearing over a crop field, and tree shadows reflecting onto the beach instead of the water. Lava is exempt, and murky green or grey water keeps its effect.

### Changed
- Lava in the volcano and the Caldera now renders as lava rather than as water: a slow molten flow with its own glow, and no mirror reflection. Molten rock does not reflect the scene above it the way water does. Previously this only happened on maps someone had labelled by hand.
- The default cloud-shadow opacity is lowered from 0.61 to 0.45; the old default read as almost black. An existing config keeps whatever you already set.
- "Height Framework not installed" is no longer printed as a console notice. It is an optional integration, so the line now says so and only appears in trace logs.

### Added
- Simplified Chinese translation by rime961.

### For translators
No i18n keys were added, removed or changed in this release.

## 1.2.1

### Fixed
- Water and lava ripple no longer bends the on-screen UI during cutscenes. The event SKIP button and dialogue drawn over water stayed put; only the ripple's pixel displacement is paused during an event (the reflection, tint and sparkle still show).
- Tiny drifting lights from other mods (fireflies in JP's The Night Lights, sparkle lights) no longer cast endless moving shadows on the player. A new "Min light size for shadows" control gates shadow casting by light size; the lights' glow is unaffected and windows always cast.
- Global God Rays no longer fades its rays under a cloud shadow that Radiance has hidden. When Radiance suppresses the vanilla drifting clouds it now removes them outright, so other mods stop reacting to a shadow that no longer renders.

### Added
- Blue-light / eye-comfort filter (Color grade): a warm shift that cuts blue and lifts red a touch, like a night-light mode. Works even with color grading turned off.

### Changed
- The F6 tuner is redesigned into a tabbed layout: category tabs on the left, that category's controls on the right, with a constant panel height and smooth scrolling. Every setting is now available in the tuner (some were previously only in GMCM).

### For translators
New i18n keys:
- `config.colorgrade.bluelight.name`, `config.colorgrade.bluelight.tooltip`
- `config.shadows.minlightradius.name`, `config.shadows.minlightradius.tooltip`
- `tuner.tab.looks`, `tuner.tab.lens`, `tuner.tab.fog`
- `tuner.tonemap`, `tuner.bluelight`, `tuner.bloomthreshold`, `tuner.minlightradius`
- `tuner.godraysthreshold`, `tuner.godraysdensity`, `tuner.cloudhidevanilla`
- `tuner.watersparkledensity`, `tuner.waterindoors`

Changed (text changed, re-translate):
- `tuner.floodgi`, `config.lighting.flood.name` (dropped the "(new)" suffix)
