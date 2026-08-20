# Changelog

All notable changes to SDV-Radiance. Older releases are documented on the Nexus page.

## 1.6.0

### Added

- **Rain, snow and windblown leaves drawn by this mod instead of the game.** The game draws
  weather as one sheet of identical drops scrolling down the screen at a single speed, which is
  why rain reads as a texture laid over the picture rather than as weather happening in it. This
  draws it as three planes at three depths: the near streaks long, wide and bright, the far ones
  short, thin and faint, each plane leaning and travelling at its own rate, so walking through a
  storm the near rain crosses the screen while the far rain barely moves. Where a drop lands it
  splashes, and the splash is water rather than the bright blue confetti on the game's own sheet.
  Green rain is replaced too, in its own sickly lime with the heavier fall it deserves. Snow
  becomes flakes in three sizes instead of a scrolling texture, and on a windy day the game's
  flat fluttering chunks become blossom in spring, leaves in summer and autumn and white flecks
  in winter, tumbling and riding the same wind the rain leans with, coloured by the season under
  your feet rather than by the calendar alone.

  Rain, snow and wind each have their own switch and their own three dials: **Amount**, **Size**
  and **Visibility**, because too few, too small and too faint are three different complaints and
  one slider cannot answer all three. On by default. If another mod has already claimed the
  game's weather drawing, this one stands down rather than fighting it for the same slot.

- **The scene answers lightning.** A storm used to be a white screen and a noise. Now, for a
  blink after each strike, every shadow in the location kicks over as though the bolt were the
  sun and leans away from the side it came from, this mod's own darkening lifts with the game's
  flash instead of holding the scene down through it, and a short warm afterglow follows the way
  a real strike leaves the air lit for a moment. It reads the game's flash rather than patching
  it, so it works with vanilla rain and does not need the replacement weather above.

  **Visible bolts** are a separate switch. The game only ever draws a bolt on the farm, and only
  when a lightning rod or a crop was actually struck, so a storm anywhere else has thunder and no
  lightning in it. This draws one in the sky on any map, using the game's own bolt art, and not
  on every rumble: some strikes stay behind the clouds. If the game has already drawn its own,
  this adds nothing on top. Both on by default.

- **A wet world after the rain** (off, and not in the menus this release). Ground darkens and its colour deepens while it rains and for
  about two in-game hours after it stops, on a clock the mod keeps itself because the game has no
  notion of a surface that is still drying. Waking up after a rainy day starts the world half
  wet. At night the lamps smear down the wet ground in long streaks the way they do on a real
  street. Off by default.

  **The wet ground is off and out of both menus for now.** It is written and it works, but where
  standing water may honestly lie is a question about the map, and on a modded map the answer was
  sometimes a roof or the top of a fence. Until that can be decided from the map rather than
  guessed at, the whole of it stays out of the way rather than sitting in the menu inviting a
  switch that has a known bad case. `radiance_config WetWorldEnabled true` still reaches it. The
  screen-edge drops below are not part of this and are on their own switch.

- **Drops on the edge of the screen, and breath on the glass.** While it rains a few drops cling
  to the edges of the picture, never the middle of it. They are not circles: gravity drags the
  bottom of a drop down and surface tension holds its shoulders, so each one is a little
  lopsided, and drops that touch merge into one drop of the same total water rather than sitting
  on top of each other. A drop that grows too heavy breaks loose and runs, taking the ones it
  passes with it and speeding up as it collects them. In a snowfall they become frost instead.
  Around them the edge of the screen mists over: condensation in rain, frost creeping in from the
  corners in snow. Both the drop size and the misting have their own dial, and both clear shortly
  after the weather does.

- **Reflections in windows.** Walk past a window and you are in it. Glass reflects when what is
  behind it is darker than what is in front, so the image is plain in daylight and thins to a
  suggestion after dusk as the room lights up behind the pane, and a mirror returns you fully
  where a house window returns a fifth of you. What is reflected is the part of you at the
  window's own height, with the tool in your hand, keeping your stride. Alongside it the glass
  gained the things that make a pane read as glass rather than as a picture of one: a wash of the
  sky's own colour, stronger at the top; a soft blot of glare that travels across the pane as it
  crosses the screen; the street in front of it standing in the lower half and fading out by the
  frame; and after dark the lamps outside, each a small blot of its own colour in the panes
  facing it, fading with distance, never its own glow in its own glass. Windows have their own
  tab in the tuner and their own page in the settings menu, with a switch per effect. On by
  default.

- **Particles living in the world.** Dust hanging in the light through a window, indoors, while
  the window is actually lit. Sparks rising off anything the game treats as a flame, found from
  the furniture that owns the fire rather than guessed at from the sprite, so they leave the
  hearth and not the brick above it. Fireflies over a summer field, added to the ones the game
  already flies and only on the nights the game itself calls firefly nights, so a field never has
  two opinions. Blossom and leaves drifting outdoors on the ordinary days the game leaves the air
  completely empty, thinning to a quarter and riding the game's own wind on the days it does blow
  something. Pale sparks turning around a player wearing a glow ring, left behind where they were
  made rather than carried along, so standing still they circle you and walking they trail out
  behind.

  They are drawn into the world rather than over it, so they take the light, the weather and the
  colour grading like everything else on the map. Every kind has its own switch, amount and size,
  under one overall amount that turns all of them down together. On by default, at the amounts
  this mod was tuned on, and every kind can be switched off on its own without touching the rest.

- **Rain on the water has three dials.** How many places a drop strikes the surface, how wide
  one ring grows before it dies, and how plainly the rings and their impact points stand out.
  They are three separate questions and one slider could not answer them: a shower that reads as
  too busy is not the same complaint as one that reads as too faint. At 0 rings the surface stays
  unbroken in the rain; above 1 every part of the surface takes its turn and the pattern tightens.

- **Caustics on shallow water.** The wobbling net of focused light on the bed of shallow water,
  strongest along the shore where the bed is closest to the surface, fading out at night and in
  bad weather. On by default at a strength set by eye against the water it lies on.

- **Reflection softness.** How much the reflection is blurred with depth is now a slider. 1 is
  exactly what the mod shipped with, so nothing changes until you move it; 0 is a single crisp
  sample and 2 is twice the spread. Reflected people follow the same figure as the reflected
  scenery, so the surface stays one surface.

- **The things that appear and vanish reflect too.** A crab pot standing on the water, the splash
  when something breaks the surface, an item tossed in, and the fishing bobber, which now hangs
  from the float itself rather than from the bottom edge of the frame.

- **Sunlight through a canopy gets its own two dials.** It had been sharing the lamp rays'
  strength and reach, which the two have no business sharing: a lamp ray is a streak drawn out of
  a bright pixel at night, a sun shaft is daylight cut by the trees it passes through. Strength
  and reach are now separate settings, and the god rays page is split into the two things it was
  always doing.

- **Two dials that existed only in config.json are in both menus.** **Ray length** is how far a
  ray reaches before it dies out, from a short stub at the light to a long streak across the
  screen, and **Thicker toward the top** is how much heavier the fog sits at the top of the
  picture than at the bottom, which is what makes it read as distance rather than as something
  around your feet. Both were read every frame by their shaders and reachable only by hand
  editing the file. Each is a shared value rather than one belonging to a section above it: the
  ray falloff is set once for both the lamp streaks and the sunlight through a canopy, and day
  fog and night mist are one shader pass, so each gets a heading of its own saying so.

- **Art that reads past the edge of its own sheet is snapped back inside it.** Stardew 1.6 added
  sprite columns to several sheets, a mossy tree variant among them, and not every sheet the
  game asks one of has it. The game still asks, the graphics card reads
  past the right edge of the sheet, and back comes the clamped edge pixel: a transparent margin,
  so the tree is invisible, or a smear of the last column, which reads as a single tile at a
  quarter of the resolution of its neighbours. Every one of those draws already passed through
  this mod on its way to the screen, so the rectangle is now stepped back by whole columns until
  it lands inside the sheet, which draws the same tree without its moss instead of nothing at
  all. `radiance_report` names every sheet this has happened to, so the pack can be reported to
  its own author rather than guessed at.

### Changed

- **A new install now starts from the settings this mod was actually tuned on**, not from the
  cautious values that were only ever placeholders. Sixty eight defaults moved, including the
  ones that decide the first impression: weather, particles, window effects and caustics all
  start on. **An existing config.json is left completely alone**, which is the usual SMAPI
  behaviour and is deliberate here: nobody's tuned game changes under them on update. To take
  the new set, delete `config.json` and let the mod write a fresh one, or press F6 and pick a
  preset. Two things deliberately did NOT move: the wet ground stays off, see above for why, and
  **colour grading stays off with no look selected**, because 1.5.7 put "off by default, so
  nothing changes until you ask for it" in writing and the colour of somebody's game is not a
  default to change quietly. Both are one switch away.

### Fixed

- Water walled in on every side by drawn art no longer ripples. A pocket of water whose whole
  boundary was taken by the carve is a gap inside something drawn, not a body of water, and the
  ripple moves pixels far enough that a pocket a few texels across fills with whatever is around
  it. Water with land around it is untouched, because land was never carved; so is anything
  reaching the edge of the screen, which continues out of sight. This covers map art as well as
  furniture, which the entry below does not.
- Water no longer ripples inside the slot in a bench. The carve that keeps a piece of furniture
  out of the water effect reads the sprite's own alpha, which is right at its outline and wrong
  inside it: a gap with furniture all the way around it was left as water and the ripple ran
  through it. A hole that reaches the edge of the sprite is still left alone, because that is not
  a hole, it is the space beside the furniture. Only sprites this mod can read the art of are
  affected.
- A bridge across a river keeps a straight edge, and nothing standing in the water puts its own
  colour into the water. The rule that a displaced sample must never land on a solid is not new;
  what it did when it hit one was to stop that pixel moving at all. That is right about colour
  and wrong about motion: a pixel within one wave of an edge froze, its neighbour did not, and
  which of the two a pixel was changed as the wave passed, so the last few pixels along a
  straight edge flickered in a travelling pattern and read as a wavy edge. A wave that meets
  something solid turns back from it rather than stopping dead, so a blocked sample is now taken
  the same distance the other way, where the water is. One rule now covers the map art, the
  sprites and the player alike, so a bridge, a pier post, a boat and someone wading are all
  answered the same way.
- Lightning fades over the right length of time in split screen. The update handler is raised
  once per screen, and everything the lightning response holds is on a clock, so with two
  players the afterglow and the shadow flick were over in half the time they are written to
  last. It keeps a tick stamp now, the way the wind and the ground wetness beside it already
  did.
- Your shadow is visible again while it snows. Overcast weather has always been a dimmer on the
  sun rather than a switch, so a shadow stays soft and short instead of vanishing; snow was
  taking the full dimmer, and on pale ground that put the shadow below what an eye can find. A
  snowy sky is not a rain cloud: it is bright, and the ground under it is a reflector, so snow
  now takes half the dimmer. Rain and lightning are unchanged.
- Weather is asked of the place you are standing rather than of the valley. Three tests in the
  shadow pass read the game statics, which mirror the Default context only, so rain on Ginger
  Island left shadows and moonlight behaving as though the sky were clear.
- A bird flying over water faces the same way in the water. Its sheet holds one direction only
  and the game faces it the other way by flipping it, so a gull taking off to the left flew left
  above the surface and right in the reflection. The mirror turns the picture over, not around.
  Standing birds were right, which is what made it look like a problem with flight.
- The reflection now changes frame when the world does rather than on a clock of its own. It had
  been advancing on its own timer, so a reflected thing could be a frame behind the thing it
  reflected, which showed up as the reflection stuttering while the world moved smoothly.
- A hearth's light sits at the fire. The brightest part of a fireplace was the brick above the
  flames, because the light was placed at the middle of the furniture rather than at the thing
  burning in it, and the sparks left from the same wrong place.
- A street lamp is not a hearth. It was being treated as one, which put its light low and gave it
  a hearth's warmth and flicker; its light is at the bulb now.
- A tuner note too long for the screen started off the left edge of it instead of wrapping.
- The look buttons in the tuner speak the player's language. They were showing the internal name
  of each look while the settings menu next door showed the translated one.
- The two menus no longer disagree about what the player just did. Picking a look in the tuner
  applied its numbers without recording which look it was, so the settings menu still read
  Custom immediately after.
- Two sliders that existed only in the settings menu, cloud size and mist scale, are on the tuner
  too. Every setting is supposed to be in both.

### Removed

- **`MinShadowLightRadius` is gone from config.json.** It used to be the smallest light that was
  allowed to cast a shadow, which is how tiny drifting lights from other mods were stopped from
  throwing their own shadow on you. That test was rewritten to ask whether a light has moved
  recently rather than how big it is, because real lamps are smaller than the old bound and were
  only surviving it by accident. The setting has read nothing since, so moving it did nothing.
  If it is in your config.json it will simply be dropped the next time the file is saved.

### Diagnostics

- `radiance_weather sun|rain|storm|snow|wind|greenrain` sets the weather outright, which the
  game's own debug command cannot do. It is an absolute setting rather than a toggle, so asking
  twice for the same weather is not a way to turn it off.
- `radiance_report` gained a line for the window pass, a line for the wet world, and the
  `art bounds:` line naming any sheet whose art was rescued.

### Translations

- Chinese is up to date through 1.5.7, from Rime961, including a name for the autumn gold look
  chosen by its translator rather than translated from ours.

### Known issues

- **A mossy tree can be invisible in winter.** Knocking the moss off brings it back, which is
  what made it look like a lighting problem to begin with. What is established is the mechanism:
  the game asks for a sprite column that lies past the right edge of the winter tree sheet, and
  the card returns the clamped edge pixel, which on these sheets is transparent. What is not
  established is why it asks. The winter sheets are 48 pixels wide in the vanilla game as well,
  so this is not something an art pack introduced, and it is not something this mod can fix at
  the source. The rescue above turns it from an invisible tree into a tree drawn without its
  moss, which is the most an outside observer of that draw can honestly do. If you can reproduce
  it, `radiance_report` names the sheet, and that line is worth sending on.

### For translators

**186 new keys, nothing removed, and two whose meaning changed.** This is a large release and most of the new keys belong to one of six new
feature groups. Every group follows the same shape the mod already uses: `config.*.name` and
`config.*.tooltip` for the settings menu, `tuner.*` and `help.*` for the same control on the F6
tuner, and `tuner.section.*` for a heading.

- **Weather** (`config.section.weather`, `tuner.desc.weather`, `config.precipitation.*`,
  `tuner.precipitation*`): the master switch, one switch each for rain, snow and windblown
  leaves, and the shared **Amount** / **Size** / **Visibility** trio.
  `config.precipitation.density`, `.size` and `.opacity` are written once and shown under all
  three of rain, snow and wind, so they have to read sensibly for a streak, a flake and a leaf
  alike. The same is true of `tuner.precipitationdensity`, `tuner.precipitationsize` and
  `tuner.precipitationopacity`.
- **Lightning** (`config.lightning.*`, `config.lightningbolts.*`, `tuner.lightning*`): the scene
  response and the visible bolt.
- **Wet world** (`config.wetworld.*`, `tuner.wetworld*`): the switch, wetness strength, puddles,
  screen-edge drops, drop size and the misted edge.
- **Windows** (`config.section.windows`, `config.windows.section*`,
  `config.lighting.window{reflection,reflectionstrength,reflectionnight,sheen,glare,scene,lightglow}.*`,
  `tuner.window*`, `tuner.section.window*`, `tuner.desc.windows`): the reflection and the four
  daylight effects on the glass.
- **Particles** (`config.section.particles`, `config.particles.*`, `tuner.particle*`,
  `tuner.section.particle*`, `tuner.desc.particles`): the master switch, the overall amount, and
  one switch per kind. `config.particles.amount` and `config.particles.size`, and their tuner
  twins `tuner.particleamount` and `tuner.particlesize`, are each written once and reused under
  every kind, so they must not name a particular one.
- **Water** (`config.water.caustics*`, `config.water.reflectblur.*`, `tuner.watercaustics*`,
  `tuner.waterreflectblur`, `help.*`): caustics, its strength, and reflection softness.
- **God rays** (`config.godrays.section*`, `config.godrays.sun*`, `tuner.godrayssun*`): the two
  new sun-shaft dials and the two section headings that split the page.
- **Four loose ends**: `tuner.cloudscale`, `help.cloudscale`, `tuner.fogscale` and
  `help.fogscale` for two sliders that had existed in the settings menu but not on the tuner, and
  `config.godrays.decay.*`, `tuner.godraysdecay`, `help.godraysdecay`, `config.fog.topbias.*`,
  `tuner.fogtopbias` and `help.fogtopbias` for two that had existed in neither. Their two new
  headings, `config.godrays.sectionboth` / `tuner.section.godraysboth` and
  `config.fog.sectionboth` / `tuner.section.fogboth`, both mean "this one applies to both of the
  things above", so a literal "Both kinds" is closer than a repeat of the feature name.

**Two keys changed meaning**, so an existing translation of them is now wrong rather than merely
old: `tuner.windoweffects` and `config.lighting.windoweffects.name` were both "Window effects",
one switch covering everything the mod did with windows. Windows now have several switches, so
that one was narrowed to the daylight and the after-dark glow only, and reads "Window daylight
and glow". Their tooltips are unchanged and were already accurate. Nothing was removed.

## 1.5.7

### Added

- **Looks (colour LUTs).** A finished look laid over the grading sliders rather than instead of
  them: pick one under Color grading and set how strongly it applies. Seven ship with the mod, and
  a 1024x32 LUT strip of your own goes in a `radiance-luts` folder beside your save games, where it
  is picked up on the next launch and listed after them, marked as yours. That folder is yours
  rather than the mod's, so updating Radiance cannot delete what you put there, and neither can a
  mod manager that installs clean. Off by default, so nothing changes until you ask for it.

  Both the look and its strength are on the F6 tuner as well as in the config menu, because a
  colour look is judged by eye and the tuner leaves the scene visible while you change it.

  The looks were designed against measurements of what the game actually puts on screen, not
  against general advice about film. Three of those measurements shaped every one of them:

  - **12% of all pixels are pure black, and 70% of a night interior is.** The first thing a film
    look usually does is lift the shadows, which here would turn most of a dark room grey. Every
    look fades back to no change through the deep shadows, so black stays black.
  - **99% of the picture sits below 237 of 255, and under 1% goes above 240.** A filmic highlight
    rolloff has almost nothing to act on, so the looks work in the midtones, where the picture is.
  - **Blue, cyan and green are 79% of the colour in daylight; red, orange and yellow are 19%.**
    Warming the picture by pushing red would touch a fifth of it, so the warm looks move the blues
    and greens instead. At night the balance inverts (65% of the colour is lamplight red), so the
    cool looks protect the reds rather than draining them the way the textbook says.

### Changed

- A reflected building no longer comes apart into horizontal strips sliding over each other. The
  ripple pushes a reflection sideways by an amount that depended on the row, and the row was
  rounded to a step four pixels tall, so a band of pixels moved together and then jumped at the
  boundary. That was not a side effect of the wave, it was the wave: a staircase cannot shear
  anything smoothly. The shear is now computed per row, so the reflection bends instead of
  breaking. The banding was deliberate once, as a drawn pixel-art look, so it is a setting rather
  than a deletion: **Reflection banding**, 0 for a surface that bends and 4 for exactly what this
  looked like up to 1.5.6.
- The second harmonic of the ripple drops from a period of 6.6 world pixels to 20.1. At one sample
  every four pixels it could never appear as itself and folded into a slow beat that crawled across
  the reflection, which is the streaking that was reported.

- The water behind a see-through tree is water again. Walk behind a tree standing at a pond and
  the game fades it so you can see yourself through the leaves; what showed through was a
  canopy-shaped patch of completely untouched water. The mask that stops leaves rippling was
  stamping every canopy at full strength no matter how faded the tree actually was, and the shader
  read that mask as all-or-nothing. Both halves now carry the opacity through, so a half
  see-through thing hides half the effect.
- A butterfly flying over water no longer ripples with it. The exclusion mask was rebuilding each
  critter's placement by hand instead of letting the sprite draw itself, so the still patch landed
  beside the butterfly rather than on it, and the reach test asked about the ground under it rather
  than the row it flies at.
- The left and right edge of a reflection no longer has a fine sawtooth along it. Where a column of
  water sits inside a shore tile there is no open water directly above it, so the mirror borrows the
  neighbouring column to find the real waterline. That borrow was all or nothing and it switched the
  moment the column crossed a texel, so on a diagonal shore each row switched on a different column
  and the edge stepped in and out by a quarter tile from one row to the next. The borrow now scales
  with how much water is actually there, so the edge follows the shoreline.

### Added

- Reflection reach and reflection fade steps are set by the performance preset now, instead of
  being two sliders of their own. Both buy frames without changing how the water looks, which
  makes them the worst kind of setting to put in front of someone: you move it, nothing happens,
  and you conclude the mod is broken. Quality keeps everything reflected and fades it finely,
  Balanced fades it in coarser steps, Performance halves how far from the water something can
  stand and still be mirrored, and Low spec keeps reflections at all rather than turning them off.
  Both are still in `config.json` and reachable with `radiance_config` for anyone measuring.

- **Reflection distortion**, a slider from a flat mirror to more than the water's own movement. It
  scales both of the things that bend a reflection: the sideways shear from the wave, and the
  displacement from the ripple. The named reflection looks only ever touched the second, which is
  why Still Water could never reach a mirror however far it was turned down. At 0 the image in the
  water is held still while the surface keeps rippling and sparkling, matching the reflection of a
  person, which has never moved. Ripple strength stays a separate control.
- **radiance_perfhud** shows what each part of this mod costs, live, in the corner of the screen,
  and the same switch is on the Performance tab of the tuner. The report already held these
  numbers, but a file cannot answer "what did I just do that made it stutter".
- **radiance_gputime** adds what the graphics card spends beside what the game spends asking it.
  Some effects are cheap to ask for and expensive to draw: the effect chain measures about twice
  its submission cost. Off by default.
- The report and the readout now say when frames were drawn while the game window did not have
  focus. The game sleeps 20 ms on every one of those, which on its own turns a capped 16.7 ms frame
  into 24.6 ms and 40 fps with nothing actually wrong. The per-part numbers are unaffected.
- **radiance_tuner** opens the tuner from the console, optionally on a named tab.
- **Lower it automatically when needed**, under the effect resolution slider. The mod watches its
  own frame time and drops the effect resolution a step when the frame has been missing its budget
  for a second and a half, then gives it back when the scene gets easier. What you set stays the
  ceiling; this only ever asks for less. It is off by default and on in the Performance and Low
  spec presets, because the effect resolution is the one setting here with a quadratic effect and
  the one nobody reporting a slow game has ever mentioned finding.
  It holds still while the window is in the background, where the game sleeps 20 ms a frame and
  every frame looks slow. And a step down that does not actually shorten the frame is given back
  within three seconds, with the controller standing down for a minute afterwards: a machine held
  up by its CPU gets nothing from a smaller buffer, and should not be left with a softer picture
  for it.
- **radiance_autoscale** prints what that controller is doing, and can pretend the frame budget is
  shorter than it is so the whole path can be watched working on a machine that never misses its
  own budget.

### Fixed

- The light that seemed to switch on as you walked up to it, and the pulse of brightness while
  walking through a lit room or a lit street at night, are gone. Both were the same thing. The
  shader had twenty-four light slots and an ordinary scene offers more: the saloon holds about
  thirty lights once its wall lamps are merged, and a town street at night thirty to fifty with
  the house windows counted. The lights that lost the last slots were the big off-screen ones
  whose pools still covered a third of the picture, and walking a few tiles evicted and re-admitted
  them, each time fading a screen-sized pool out and back in over a third of a second. No ranking
  can hide that; only a budget the scene does not fill can. There are forty-eight slots now, and
  the extra ones cost a distance test each rather than a light each. Turning flood GI off, which
  hands lighting to the older per-light pass, costs what it did before: that pass was measured at
  three milliseconds while this was being changed and brought back down before it shipped.
- Map scenery that a map turns - mirrored or rotated tiles, which .tmx maps use freely (one
  farm map turns 2,798 of its cells) - is no longer redrawn the plain way round by the shadow
  pass, which read as tiles going "misaligned and flipped" the moment the mod was switched on. The
  water pass had been reading the tile's orientation for a while; the shadow pass now does too,
  in the visible redraw, in the shape it bakes, and in the cache key that tells two shapes apart.
  Reported from Waterfall Forest Farms; the fix is verified pixel for pixel on this side but the
  reported spot itself has not been reproduced here, so please say if it is still wrong.
- The bounce light of a lamp just past the edge of the screen no longer steps as you walk. The
  bounce grid covers the visible tiles and a margin, and a lamp beyond the margin fed it nothing,
  so the grid changed each time the camera crossed a tile. Every lamp in the location now feeds
  the grid, clamped to its edge with the falloff it would have had crossing the missing tiles, so
  the grid reads the same in the world wherever the camera stands.

### Performance

- The water proximity test, which every tree, bush, grass tuft, building, animal and character on
  screen runs before it draws itself into a reflection or a mask, was walking the block of tiles
  around itself and could look at 361 of them to answer no. It now reads four numbers out of a
  table built once per mask rebuild. Same answer, cell for cell.
- Both of those sweeps also visited every tile the camera could see, around nine hundred a frame,
  most of them nowhere near water. They now start narrowed to the water's own bounding box. The
  sprite mask fell 14 to 24 per cent at two spots; the entity mirror did not move, which says its
  cost is in the stamps rather than in finding them, and that is where the next attempt goes.
- The effect chain no longer copies the game's frame into a buffer of its own before reading it,
  at resolutions where that copy was a duplicate rather than a downscale. Measured, this bought
  almost nothing on the machine it was measured on - it is kept because it is less work for an
  identical picture, and the measurement is written down so nobody budgets for a saving that is
  not there.
- **Reflection reach** and **Reflection fade steps**, two new sliders on the water tab, because
  the only control this mod shipped for its most expensive feature was a switch and the presets
  aimed at slow machines were using it. Reach decides how far from the water a tree, bush, grass
  tuft or building may stand and still be mirrored: measured, the shortest setting took 34% off
  the reflection pass at one wooded shore and 48% at another, while a bare shore with plenty of
  water did not move at all, which is the control saying the cut lands on scenery and nothing
  else. People, animals and critters always reflect at full reach.
  Fade steps is the cheaper of the two to accept. A reflection is drawn in slices so it can fade
  toward its far end, and taller slices mean fewer of them: 8 measured 31 to 37 per cent cheaper
  than the shipped 4 and loses no reflection at all, only the smoothness of the gradient. Past 8
  there is very little left to save, because the draw count turns out to be only about a third of
  what this pass costs. The two stack.
- **Low spec no longer turns reflections off.** It was the preset most likely to be chosen by
  somebody having trouble, and it threw away the reason most people install this mod, because the
  only control was a switch. It now keeps them at the shortest reach with the coarser fade: only
  the scenery standing at the water still mirrors, and people and animals are never cut by reach
  at all. Performance uses half reach, Balanced keeps every reflection and only takes the coarser
  fade. This is not free, and Low spec now pays a little where it used to pay nothing.
- The report now breaks the effect chain into one line per full-screen pass, with the GPU column
  beside the CPU one. That is what said the three cheapest passes were not worth fusing and that a
  third of the chain's time is in the gaps between passes rather than in any of them.

### For translators

Thirty-six new keys:

- `tuner.waterreflectdistort`, `help.waterreflectdistort`, `config.water.reflectdistort.name` and
  `config.water.reflectdistort.tooltip` for the reflection distortion slider
- `tuner.waterreflectbanding`, `help.waterreflectbanding`, `config.water.reflectbanding.name` and
  `config.water.reflectbanding.tooltip` for the banding slider
- `tuner.section.perfreadout`, `tuner.perfhud`, `help.perfhud`, `tuner.gputime` and `help.gputime`
  for the cost readout on the Performance tab
- `config.renderscaleauto.name`, `config.renderscaleauto.tooltip` and `help.renderscaleauto` for
  the automatic effect resolution
- `config.perfpreset.lowspec` for the Low spec performance preset
- `config.report.name` and `config.report.tooltip` for the report button
- `config.colorgrade.lut.name`, `config.colorgrade.lut.tooltip`, `config.colorgrade.lutamount.name`
  and `config.colorgrade.lutamount.tooltip` for the colour LUT controls
- `config.colorgrade.lut.none` and one key per shipped look: `config.colorgrade.lut.warm-film`,
  `.verdant`, `.autumn-gold`, `.moonlit`, `.cool-night`, `.washed-linen`, `.identity`. These are
  the names shown in the dropdown, not file names, so they should read naturally in your language
- `tuner.lut`, `tuner.lutamount` and `help.lutamount` for the same two controls on the F6 tuner.
  The look names themselves are not repeated there: the tuner shows the file name for a look you
  added and reuses the `config.colorgrade.lut.*` names for the ones that ship
- `config.colorgrade.lut.yours` and `config.colorgrade.lut.missing`, two words shown in brackets
  after a look's file name: one marks a look the player added themselves, the other a look named in
  config.json whose file is no longer there. Both sit inside brackets after a name, so short is
  better than descriptive

Nothing was removed, and no key that existed in 1.5.6 changed its meaning. (`config.colorgrade
.lut.tooltip` was reworded once while this version was being written, before it had ever shipped,
so there is nothing to re-check there either.)

## 1.5.6

### Added

- Morning darkness is now a slider, on the lighting tab and in GMCM. The dim morning itself is not
  new: 1.5.5 already held a quarter of the night's darkening through 06:00 and lifted it over the
  next two hours, but the quarter was a constant nobody could reach. It is the same quarter by
  default, so nothing changes unless you move it. Set it to zero for vanilla's fully lit morning,
  or higher if you want the sun coming up to be something you watch happen.

### Performance

Every number below was measured, and where it was measured matters enough to say first, because
a frame rate quoted without its machine is not a claim anyone can check. Two setups, both an
RTX 5080:

- **A 62-mod profile with the frame cap lifted.** This is the benchmark rig. Lifting the cap is
  what turns frame time back into a measurement rather than a report of your monitor, so the
  numbers are large and they isolate what this mod costs. They are not what your game looks like.
- **The author's own 105-mod save, played normally.** This is the honest one. The farm at noon
  runs at 17.3 ms a frame with this mod switched off entirely and 19.2 ms with everything on, so
  the mod costs about 2 ms. In the mines it was 1.6 ms. The worst frame in the sample is the same
  either way, 27.0 ms against 27.1 ms, which says the periodic hitch people report is not this
  mod's doing even though its average cost is real.

The second setup is also worth reading as a warning that has nothing to do with us: going from 62
mods to 105 took the baseline frame, with this mod OFF, from about 3.8 ms to 17.3 ms. If your game
is slow with a large pack installed, this mod is a couple of milliseconds of it.

- Object shadows cost about a fiftieth of what they did. On a crop-dense farm at noon they were
  measured at 1.80 ms per frame, more than half the whole frame, and they now sit inside the noise
  of having them switched off; on the benchmark rig described above the same scene went from
  3.78 ms to 2.53 ms a frame, 265 fps to 396.
  Four things were wrong and each is worth naming, because the shape of the mistake repeats. The
  soft edge was being drawn nine times per shadow per FRAME, when the blur never changes between
  frames, so it moved into the bake and every shadow became a single draw. Each shadow was then
  drawn as its whole storage slot rather than the part of it holding a shadow, sending on the order
  of a hundred million transparent pixels a frame. Objects were still using the round nine-tap
  pattern the player uses, where a five-tap cross is indistinguishable on anything that is not a
  person. And grass cast one shadow per BLADE, up to four per tile, each drawn at reduced opacity
  precisely because four of them stacked; a meadow now costs a quarter of that for the same dark
  patch. Blurring at bake time is the one of these that changes the picture at all, and only by
  making the blur exact instead of approximate.
- The mod gives graphics memory back when its effects are off. Nothing in the mod had ever called
  Dispose, so every render target it had ever built stayed resident: 147.8 MB held on a machine
  where the mod was switched off entirely, and 211.8 MB on a farm at wide zoom. Switching
  everything off now returns all but 0.1 MB, and the wide-zoom farm holds 140.2 MB. Confirmed again
  on the 105-mod save: 129.5 MB held in the mines and 45.8 MB on the farm, both down to 1.2 MB with
  the mod switched off, and the picture is identical when it is switched back on. Two earlier
  attempts at this did nothing at all, both for the same reason, which is now a rule the code
  states: the code that hands a resource back must not sit on a path that the feature wanting the
  resource can skip. It runs on the game tick, the one path none of our own gates can turn off.
- The shadow bake cache holds three and a half times more in less memory. Every baked sprite got a
  400x464 slot whatever its size, so a crop using three percent of one still paid for all of it,
  and an ordinary modded farm sat at 134 entries against a cap of 128, evicting and re-baking
  constantly. Sprites now take one of three slot sizes, each with its own eviction, and the same
  farm holds 464 entries with no evictions at all.
- An unchanged occluder mask is no longer re-sent to the graphics card. The rebuild was already
  throttled, but the result was uploaded regardless, so standing still pushed an identical mask
  twenty times a second.

### Diagnostics

- radiance_verify no longer scores pixels the player cannot see. It read the art opacity of a map
  layer only when that layer also carried a label, so an unlabelled but opaque overlay - a bridge
  deck, a jetty, a rock shelf - stayed invisible to it: the Back label underneath said liquid, the
  mask correctly shipped nothing, and the tile was reported as 256/256 missing water. Those pixels
  are now counted separately as hidden. Measured over six locations the accuracy this reports moves
  from 87.3-95.5% to 99.6-100%, with the false-water count unchanged to the pixel, which is what
  says the mask was right all along and the instrument was wrong.
- radiance_report says how long a frame really took and how much graphics memory the mod is
  holding, broken down by what it is for. Every timing it carried before measured only the work of
  telling the card what to draw, which is why object shadows could be half a frame while every
  number in the report read as a rounding error. If you are reporting a performance problem, this
  is the one to paste.
- radiance_config changes any setting live, without editing config.json, so a suspect can be
  switched off and back on in the same scene instead of across a restart. radiance_effectcost
  prices each effect separately by running its pass repeatedly and keeping the slope.

### Water labels

- The bundled label set gains 2,138 tiles: 155,704 more pixels of water, 119,035 of falling water
  and 59,095 of bridge or jetty deck, over sheets from A_TK's Tilesheets of Misc Stuff,
  crystalinerose's Better Water, the Waterfall Forest maps, and vanilla's own island, beach and
  volcano sheets. The volcano's dungeon was labelled as still water and is now falling water.
  Checked afterwards with radiance_verify at ten places, which score 98.6% to 100% against the
  composed mask.

### Translations

- Updated the bundled Chinese translation to cover every 1.5.5 key, including all the tuner help
  notes and the sun shafts strings (thanks Rime961).

### For translators

**Four new keys, nothing removed and nothing changed in meaning.** They are all the one new
setting, Morning darkness:

- `config.lighting.morning.name` and `config.lighting.morning.tooltip` for the GMCM page
- `tuner.lightmorning` for the row in the on-screen tuner (F6)
- `help.lightmorning` for that row's hover note

A missing `help.` key costs only the hover note, so it is the safe one to leave for later. Thai
already ships all four.

The new diagnostics (`radiance_report`'s frame and memory blocks, `radiance_config`,
`radiance_effectcost`) are console commands, which this mod does not translate: their output is
meant to be pasted into a bug report and read by whoever is diagnosing it.

### Fixed

- Waking up on a stormy day is no longer far brighter than waking up on a clear one. A windowed
  interior in rain or a storm fell back to the flat night seed instead of the daylight one, so the
  room lit itself as if it were a cave with lamps in it, which reads as too bright rather than too
  dark because nothing was tinted by the weather outside. Reported as "waking up in the rain is
  massively brighter than otherwise".
- Morning light inside farmhouses is no longer overexposed. Rooms with windows were seeded at full
  daylight the moment the day began, so first thing in the morning the walls were blown out and
  everything in the room carried the contrast to match. The seed now follows the sun, and how much
  of the night it holds onto is the new Morning darkness setting. Reported against 1.5.4.
- A room's own windows are found by looking at the glow sprites the game is drawing right now,
  rather than by a verdict cached the first time you entered. A room whose windows are added,
  removed or changed by another mod after your first visit kept the old answer for the rest of the
  session.
- Turning window effects off no longer takes the room's daylight with it. The beam, the lit glass
  and the patch on the floor are effects; the light a window puts into a room is lighting, and
  switching the effect off left rooms darker than they should be with no way to get it back.
- A fish pond has water in it again. A pond is a building, and buildings are held out of the water
  mask so their walls do not ripple, so the one building whose entire point is water carved its own
  water away.
- A bridge or a jetty painted on a map's own layers keeps the water off it. A deck drawn on the
  second Buildings layer was not recognised as a deck, so the ripple ran over the planks you walk
  on rather than the river under them.
- Furniture placed on water carves only its own shape. A bed or a table used its whole rectangle,
  so the water went missing in a block around it rather than under it.
- Beach Farm has the ocean swell it should always have had. The farm map was never recognised as a
  coastal one, so its sea got the small ripple a pond gets.
- Map layers whose names end in a negative suffix are drawn layers like any other. Maps that name
  their layers this way had those layers ignored by the mask, the dump and the verifier alike, so
  the art you could see was invisible to everything deciding what was water.
- The mask, the dump, the verifier and the lights now sort a map's layers the same way. They each
  had their own order, so a tile could be judged from a different layer depending on which part of
  the mod was asking.
- Flipped and rotated map tiles are read the way the game draws them. Maps place sheet tiles
  mirrored or turned (Gem Sea Shores' Beach_West alone carries 368 such cells, building the
  waterfall basins out of mirrored pieces), and every reader - the label lookup, the carve's
  opacity bits, the verifier - took the art upright, so the mask disagreed with the picture by
  exactly that reflection wherever a map author turned a tile. All of them now turn with the tile.
- Water no longer dies in a rectangle behind a building placed at the bank. Every building was
  carved out of the water mask as its full bounding box, and most of a building's box is
  transparent - the sky beside a pointed roof, the gaps around a well's frame - so the ripple and
  the waterline vanished in a hard-edged block behind the roof. Reported with a before/after pair
  of placing a coop beside a pond. The carve now follows the sprite's own opaque outline, and the
  box only remains as a fallback when the sprite cannot be read.
- Buildings no longer let the ripple run straight through them. A shed or a coop at the water's
  edge is drawn from its own texture, so the mask that holds sprites out of the water never saw one
  and the shimmer crossed the walls. Reported with a before and after pair of placing a coop beside
  a pond, where the water behind it changed. Same list the mirror already knew about.
- Big decorative bushes no longer shimmer like water. The mask that holds sprites out of the ripple
  walked only the tile-keyed terrain list, so a planted bush at the bank sat still while the map's
  own bush beside it, identical to look at, rippled along with the pond it overhangs. Those bushes
  live in a second list that the reflection stamp was taught about and this mask was not; it now
  walks both, with the same anchor and the same cull radius.
- Water shimmer no longer creeps onto dry land. A grass or dirt tile beside water could be flagged
  as water and light up with the ripple, and bushes standing on it picked up the sparkle. The pass
  that puts a pond's island or a lily pad back after the labels carve it was allowed to promote any
  pixel the flood missed, not only the pixels the carve had removed, so plain land counted as water
  whenever the camera happened to put its open side off-screen. Measured on one tile: the same
  ground read march=256/256 from one standing spot and 0/256 from another with nothing in the world
  changed, which is also why it came and went as you walked.

## 1.5.5

### Added

- Every setting in the on-screen tuner explains itself. Rest the pointer on a row and a plain
  sentence says what it does. Bloom, vignette, aberration and GI were named and never explained,
  which is a settings screen people switch off rather than tune. Three of the notes answer
  questions that have been asked more than once: bounced-light strength is the contrast between a
  lit corner and an unlit one rather than the room's brightness, god rays ship off, and water can
  only mirror what is on your screen.
- Buildings reflect in water. A coop or a shed at the water's edge mirrored the ground it stands on
  and nothing else, because a building is drawn from its own texture and no part of the mirror ever
  looked for one. Noticed with Build Anywhere, where putting a shed on the shore is the point, and
  just as wrong on a vanilla farm pond.
- Grass and the big decorative bushes reflect. Grass grows down to the bank on most maps and had no
  mirror at all, so the first tile of water was empty right where the eye expects the most. The
  bushes a map places live in a different list from the ones you plant, and only one list was
  being read, so two bushes standing side by side reflected differently.

- Shafts of sunlight, on by default, and this time you can see them. The old bright-pass god rays
  could never work in a top-down game: streaking bright pixels toward the sun needs a sky in the
  frame, and there is none. The shafts are now cut from the occluder map instead, the same one the
  per-lamp shadows already march: where a canopy blocks the sun's path there is shade, and where
  light comes through a gap beside it there is a slanting, slowly shimmering shaft, leaning with
  the time of day and gone under an overcast sky. Forests and treelines show them best; open
  ground is lit evenly and shows none, which is also the physics. They are their own switch,
  independent of the lamp shafts, which still ship off.
- The sun's shafts answer the sky and the air, not just the trees. Clouds passing between the sun
  and the ground now kill the shafts underneath and set them blazing at the sunward edge of every
  gap, which is where crepuscular rays actually live; on a misty morning the crisp bands thicken
  into the soft glow scattered light really has, and both fade together when the mist lifts. Dust
  motes drift through the beams, visible only while inside one, because a shaft with nothing
  floating in it reads as a decal on the ground. The god-ray density slider now also sets how far
  the dapple stretches from the canopy that casts it, with its default exactly the tuned look.
- The night has a character, and the night slider owns all of it. Deep settings darken outdoors
  too (the old hardcoded night never listened to the slider), the moonlit ground leans cool at the
  same brightness while lamps and fires keep their full warmth, and the unlit world quietly gives
  up part of its colour the way eyes actually do in the dark, so a torch at night reads as an
  event. Slid low, the night instead LIFTS above vanilla with a film-print gamma curve: shadows
  rise, highlights barely move, nothing goes milky, and at zero the colour is pure vanilla, only
  readable. One slider, one hour-long ramp, no frame where anything switches.
- Firelight is a gradient, not a flat circle. Near white at the source, gold a step out, deep warm
  at the tail, which is what real firelight does and most of why a fire used to read as a painted
  disc. The flame's own art also sits brighter than anything it lights now, and a fire reaches a
  fifth further than a lamp of the same nominal radius. White wall lamps stay white.
- A fire lays a circle of its own colour on the floor in front of it. A dim room is tinted the
  colour of what is lighting it, which after dark is the sky through its windows, and that is
  simply not true of the boards in front of a hearth. Within about four tiles of a fire the room's
  cast now hands over to the fire's own, so a farmhouse morning is a cool room with a warm ring
  around the hearth instead of one flat colour with a fire sitting in it. It changes colour only:
  nothing in it can make a room brighter than the sliders asked for.

### Fixed

- Fireplaces, paintings and dark furniture indoors are no longer solid black cutouts. The night
  lift added this version raises a picture with a curve, and a curve needs a logarithm, and the
  logarithm of zero is not a number. Any pixel with a channel at exactly zero in the game's own
  art came out black in all three, which is deep brick, dark wood and a night sky in a painting,
  and is most of what a fireplace is drawn with. Measured in a farmhouse at nine in the evening:
  three quarters of the hearth was fully black, and is now down to the level of the same room with
  the mod switched off.
- A dim room no longer repaints everything in it blue. The interior colour cast was three times
  the spread of the outdoor night's and reached the screen at three times the strength, so it took
  nearly half the red out of brick, pine and firelight alike. It is now held to what an outdoor
  night is allowed. How dark a room gets has not changed: that is the sliders' job and they still
  own it alone.
- The give-back that lets a lit room answer its own dimming was switched off at night, which is
  the hour it exists for. It measured the dimming as a flat average of the three channels while
  the dimming itself is applied by luminance, and a cool cast puts blue above 1.0, which hid the
  whole thing. `radiance_report` was doing the same sum and printing "dimmed by 0%" for a room a
  fifth darker.
- Fireflies stop flickering a shadow under you. Decorative drifting lights are meant to be left
  out of the shadow pass, and the test asked whether the light had moved since the last frame,
  which a firefly answers no to constantly: at the turning point of its wobble it is standing
  still, so for that one frame it counts as a planted lamp and casts. The next frame it moves
  again and the shadow is gone. One frame of shadow, over and over, on each firefly's own rhythm,
  which is why it read as a faint shadow blinking rather than one that is simply there. Whether a
  light drifts is now remembered for as long as that light exists instead of being re-decided
  every frame. Lamps, torches, windows and fireplaces are untouched, and so is the light the
  fireflies themselves give off.
- A horse casts a shadow, so riding one no longer removes every shadow you had. The game hides a
  horse's shadow and draws none of its own, and the rider was being skipped here on the grounds
  that the horse's shadow covered them. It did not exist. Mounting therefore took away the only
  shadow in the frame that belonged to you.
- One sun instead of one per kind of thing. Trees leaned at 0.38 of the character angle and every
  other object at 0.6, so at six in the morning a player's shadow pointed one way and the trees
  beside them pointed another. It was reported as two suns, with the difference measured in clock
  hours. The damping existed to stop a long canopy shadow detaching from its trunk, and it is the
  wrong lever for that: shortening a shadow leaves its direction alone, while damping the angle
  moves the sun for that one caster. The per-kind length limits are untouched.
- The shadow length setting reaches objects. Each kind of object had a fixed ceiling on how far its
  shadow could reach, and the sun passes that ceiling for most of the daylight hours, so a bench, a
  fence or a lightning rod sat pinned at its limit whatever you set and the slider only ever moved
  people and animals. The limits now scale with the setting, so the whole screen throws a long
  evening shadow together.
- Reflections no longer arrive in halves as you walk towards the water, and no longer fade out down
  the sides of the screen. A reflection is the picture from above the waterline, flipped, and that
  picture came from the screen, so a bank sitting high on the screen had nothing above it to mirror.
  The mirror now reads twelve tiles past the top of the frame and three tiles past each side. Twelve
  is the whole of what it can use rather than a preference: the source sits 1.25 tiles higher per
  tile of depth and the reflection has already dissolved into sky by nine tiles down, so 11.25 tiles
  above a waterline sitting on the very top row is the furthest any pixel can ask for, at any window
  size.
- A wide river or lake reflects its banks instead of reading as flat paint. The reflection was
  dissolved into sky past nine tiles from the water's own upstream bank, which is most of a river
  and most of a lake, so the middle of a body of water carried nothing at all. That bound was set
  when the mirror could only read what was on the screen; with the source now reaching twelve tiles
  past the top of the frame there are real pixels out there, and a cliff or a stand of trees is
  taller than the old bound allowed for. It runs to sixteen tiles now.
- Dead crops cast a shadow. They were excluded, and a withered plant is still a plant standing on
  the soil until someone scythes it. A field the player let die read as painted onto the ground
  while the scarecrow two tiles away stood on it.
- Grass casts a shadow. It stands on the ground like everything else and was the only thing on a
  meadow not casting, which reads as the grass being printed onto the dirt while the fence beside it
  stands on it. A tuft is up to four separate blades at jittered spots inside its tile, so each one
  casts from its own place rather than one silhouette sitting under none of them.
- A crop's shadow clears the crop. The lean slides a silhouette sideways by a fixed amount for
  every pixel of its height, so at a sun fifty degrees off vertical a plant twelve pixels tall needs
  a shadow around 0.85 of its own height before the cast escapes its own half-width. Crops were
  capped at 0.55, which put them at 0.51, and the shadow landed on the plant and read as a dark half
  rather than as a cast. The cap was never earned: a crop sprite's height is the plant's height, so
  it takes the same sun a person does.
- The indoor bounce stops dyeing the room orange. Every seed in the bounce grid is warm, because
  lamps and fires are, and the sweeps carry that colour into every cell they reach; the field is
  then multiplied over the whole screen. Blue was losing better than a third of itself everywhere at
  once while red kept all of its own, which is a dye rather than lighting. It reads as an orange
  wash, and because every surface is pulled toward the same warm axis the differences between
  surfaces shrink with it, so outlines soften and the picture goes smooth. The field now carries
  brightness and gives up most of its hue, and colour comes from the direct pools instead, which are
  per light, local and gone within a few tiles of the lamp that owns them. A hearth still lays a warm
  circle on the boards in front of it; the far wall stops being painted in the hearth's colour.
- Rain no longer hands the screen back to vanilla shadows. Weather switched the whole sun path off,
  and the same test decides whether the game's own blob shadows are suppressed, so a rainy day gave
  every tree, bush and critter back to the shadows this mod exists to replace. An overcast sky does
  not remove shadows, it makes them faint, short and soft, so weather is a dimmer on the sun now
  rather than a switch, eased so a shower starting mid-day cannot pop.
- Tree shadows stay attached to their trees. The game draws a tree's trunk as a separate piece of
  art from its canopy, and only the canopy was casting, so the shadow began a tile and a half up
  the tree. While the lean was damped that landed close enough to the base to look joined; with the
  true sun angle it slid clear and the shadow came away from the tree. Trunks cast now, on both
  ordinary and fruit trees, so the shadow starts where the wood meets the ground at any sun angle.
- The reflection is at full strength wherever it exists. A sample landing outside the picture used
  to be faded rather than clamped, over a band 6% of the screen wide, which left a permanently
  dimmed strip of reflection about a tile and a quarter deep down both sides of every view. The same
  test also measured the BOTTOM edge, which the mirror can never cross because it only ever reads
  upward, so a shoreline low on the screen had its reflection dimmed for nothing at all. With real
  pixels now sitting past the edges the taper is a hairline at the true end of the data.
  This covers the SCENERY. People, trees and buildings are stamped into a separate layer that
  already handles the same case correctly, because those reflections hang straight down from their
  own feet: something standing above the top of the screen lands its visible reflection inside the
  frame on its own, and something off the side has nothing visible to land.
- Seagulls and other critters stop being smeared by the water they sit on. The mask that keeps
  things drawn on water from being displaced by it was written for a 16 pixel frame, and every
  critter in the game is 32, so the exclusion sat a whole 32 pixels right of the bird and 64 below
  it. The gull stayed inside the rippling water while a bird-shaped patch of empty sea beside it
  was held still.
- Zooming out no longer multiplies the work. Zooming out does not shrink the world; the game draws
  the same world pixels into a bigger buffer and scales the whole thing down onto the window. At
  half zoom that is four times the pixels, all of them averaged away before anyone sees them, and
  the effect chain was sized from that buffer. Measured in town, the chain went from 0.17 ms at
  full zoom to 0.45 ms at half; it is now 0.17 ms at both. Nothing is lost, because the game's own
  downscale was about to discard exactly the difference.
- The same fix lands on split screen, where it is larger. The game halves the zoom for a split
  (Options.zoomLevel is baseZoomLevel times a modifier that is 0.5 with more than one screen), so
  each half was drawing its world into a buffer twice its own size on each axis, and the effect
  chain was sized from that: four times the pixels per screen, eight times across two. Each half
  now works at the size of the half. Reported as a split screen dropping to around 20 fps.
- Walking near water costs a third of the rebuilds it did, which split screen feels twice: the two
  halves share one rebuild slot, so a rebuild demanded every tile by two moving players kept that
  slot permanently busy.

- Walking near water stopped costing a full rebuild every step. The water surface is worked out for
  a window larger than the screen, and that slack was spent on nothing: the window followed the
  camera tile for tile, so every step rebuilt all of it on the main thread. One player measured
  their rebuild at 11 ms, which is a dropped frame per step, and reported it as a hitch crossing
  from town onto the beach. Measured after: twelve steps through open water cost four rebuilds
  instead of twelve.
- The tuner says when another mod took the window beam. It switches itself off if a mod that draws
  its own light through windows is installed, and it used to say so in the startup log only, which
  on screen reads as a feature that does not work and a switch that does nothing.

- Everything standing on the ground casts at the person's own length. Forage was capped at 0.4 of
  its height, fences and signs at 0.5, kegs and machines at 0.55, and none of those numbers was
  earned: at a morning sun a 16-pixel shell needs about 0.65 before its shadow escapes its own
  footprint, which is why forage read as having "a hint of a shadow but nothing deserving to be
  called one". The ceilings that remain belong to sprites whose height is not a height: canopies,
  bush masses, props painted into the map.
- A room full of sconces stops shimmering. A wall sconce carries the same texture id as a
  fireplace, so all twenty-four of the saloon's lamps breathed the hearth's eight percent flicker,
  out of phase, sixty times a second - measured standing still: ten to seventeen of the light
  slots changed value on almost every frame. Independent wobbles average out rather than add, so
  the flicker now falls with the square root of how many flames share the screen: one hearth keeps
  every bit of its breathing, a room of two dozen sconces holds still while each flame still moves.
- The saloon at noon stops being orange, from three directions at once. Every light in the game
  was bounced off the walls as one fixed warm colour whatever colour the light actually was, and
  all 66 of that room's lamps are white. The minimum-brightness floor written to keep a hearth
  alive in a sunlit room was applied to every lamp, so the floor alone repainted the room at noon.
  And the output roll-off started compressing at 0.60, where most of the daylit world's art lives,
  squeezing thirty points of highlight range into twelve - which is what read as the whole picture
  being dyed, soft, and short of its edges, indoors and out. Lights now bounce their own colour,
  only fires keep the floor, and the roll-off starts at 0.85, above ordinary art.
- A window at midday is a window again. The glass glow was uncapped and asked for well over the
  display's ceiling, so the panes, the bars between them and part of the wall all arrived at flat
  white and the window became a featureless ellipse. The glow now climbs to the ceiling and no
  further, so every difference the art has below that point survives: the bars stay dark, the
  frame keeps its shape. Mornings were always readable, which is the half of the day the sum
  stayed under the ceiling.
- A tuft of grass no longer sits in a black puddle. The game anchors each blade two and a half
  rows up from the bottom of its frame, so the widest part of the blade is below the ground line.
  Cast as a shadow those rows landed under the anchor and stacked there at full strength, four
  blades deep, which read to one person as a base far too dark and to another as the shadow being
  in front of the plant instead of behind it. They were the same pixels. A blade now casts only
  what stands above the ground, and the blades of one tuft share the strength of a single shadow
  rather than compounding into an almost opaque one.

### Removed

- The "Window light in the room" setting. It moved a single seed in the bounce grid, right beside
  the window beam's own column of seeds, which is stronger and carries its own switch, so with the
  beam on the setting did nothing anyone could see. Two diagnostic reports taken either side of the
  toggle came back with every number in the indoor lighting block identical. Rooms still fill with
  daylight from their windows exactly as before, under the window master switch; that half is
  lighting rather than art, and it is the half no window mod can do for you.

### Changed

- Water offers three looks for its reflection: still, natural and choppy. How rough the surface is
  and how broken the mirror is were the same number, so calming the reflection also flattened the
  water, and rain, which the game itself makes up to twice as choppy, could not be told apart from
  a mill pond by any setting. They are now separate, and the middle setting is what you had.

- The god rays switch now means the LAMP shafts, and says so. Sun shafts (see Added) are their own
  switch beside it, each working without the other: the two effects share nothing but a word, and
  tying the sun to the lamp toggle only meant two clicks to get one effect. Every description of
  both has been rewritten to match what they actually do.

### For translators

60 new keys, 3 removed and 5 changed. (`i18n/default.json` also gains a `//` line, which is a
note to whoever opens the file next and is not a string anyone should translate.)

- **50** are prefixed `help.` (`help.master` through `help.ca`): the hover notes for the on-screen
  tuner, one plain sentence per setting, aimed at a player who has never met the word "aberration".
  A missing one costs only the tip, so these are safe to leave for later.
- **6** are the water reflection looks: `config.water.reflstyle.name`, `config.water.reflstyle.tooltip`,
  `tuner.reflstyle` and the three names `tuner.reflstyle.still`, `tuner.reflstyle.natural`,
  `tuner.reflstyle.choppy`. The three names are read on a small button, so short words win.
- **3** are the sun switch under god rays: `config.godrays.sun.name`, `config.godrays.sun.tooltip`,
  `tuner.godrayssun`.
- **1** is `tuner.windowcompat`, one line shown under the window beam switch when another mod has
  taken it.

**Removed**, and safe to delete: `tuner.windowroomlight`, `config.lighting.windowroomlight.name`
and `config.lighting.windowroomlight.tooltip`. The setting they belonged to is gone.

**Changed**, and needing retranslation: `config.godrays.enabled.tooltip` described sun-following
shafts and now describes lamp shafts with a pointer to the separate sun switch. `tuner.godrays`
is now "Lamp shafts" rather than "God rays", and `tuner.desc.godrays` mentions both sources.
`config.lighting.night.tooltip` now describes the whole night character (outdoors too, cool
ground, brighter-than-vanilla at low values). `config.lighting.windowbeam.tooltip` used to end by
pointing at a setting that no longer exists, and now says the same thing without the pointer.
The `help.` keys for god rays, sun shafts and night darkness were rewritten to match.
`help.godraysdensity` gained a second job: the slider now also sets how far the sun's dapple
stretches from the trees, and the note says so.

Thai (`th.json`) is complete for this release.

## 1.5.4

### Added

- Other players cast shadows. In co-op, online or split screen, everyone but you was skipped: the
  list the game keeps of the other farmers in a location was never read by this mod, so your
  partner stood in full sun with nothing under them and threw nothing from a lamp. They now get the
  same silhouette you do, baked the same way and anchored by the same rule, so a stranger's outfit
  mod shows up in their shadow exactly as yours does in yours.
- Other players reflect in water, and stop rippling their own legs. The mirror only ever drew one
  farmer, and the water's exclusion of a body reads a single picture of a single person, so in
  co-op everybody except you stood over still water with nothing under them while the surface
  wobbled through their boots. They now go through the same stamp yours does, from their own
  full-colour bake, so their outfit appears in the water the way yours does.

### Fixed

- A dim room no longer takes the fire down with it. Darkening a room multiplied every pixel by the
  same fraction, which takes the most from the brightest thing in it: at a typical evening dim a
  flame went from 0.90 to 0.55 while the boards around it went 0.35 to 0.21. The room was correctly
  dark and the fire had stopped being the brightest thing in it, which is the one property that
  makes a flame look like a flame, and is why the hearth was reported as muddy. Rooms are now
  darkened with a curve that leaves white alone and bends everything under it, so the same room
  comes out darker than before while the fire, the lamps and the window light come out brighter.
  Measured in the saloon at ten at night: the middle of the picture fell by a fifth, and the number
  of pixels bright enough to bloom went up by two and a half times. Outdoors, in caves and in the
  mines nothing changes at all, to four decimal places, because none of those are dimmed by us.
- A crash in the water stage. Working out whether your feet are in water checked the coordinates
  against the water picture on the graphics card and then looked them up in a copy held in memory,
  which are two different things that are only usually the same size. A player hit it in the same
  second the game reported the window changing size: the frame was dropped, the effects vanished
  for that one frame, and a red line appeared in the log.
- Water no longer sits out of place after the window changes height. The check that decides whether
  the water surface is still usable compared the width it was asked for but not the height, so
  changing only the height kept the old surface and told the shader the new size for it. The water
  drew shifted away from the actual water until the next rebuild, up to ten seconds later.
- Camera smoothing stands down on a split screen. There is one smoother and there are two cameras,
  so the eased position it kept belonged to whichever screen updated last and writing it back moved
  the other player's view as well: both halves drifted toward a point somewhere between the two
  farmers instead of following either of them. Single player is untouched.
- The settings menu named the wrong key for the tuner. It said F8; the tuner is on F6, and has been
  since the key was picked, because F8 and F9 belong to Fashion Sense in the wild. That tip is the
  one place we tell anyone where the tuner is, so anyone who followed it pressed a key that does
  nothing.
- Split screen works. Reflections, water effects and lighting were mostly missing on the second
  half and came back the moment the other player left, which is exactly what it looked like: one
  camera's worth of memory being fought over by two cameras. Four things are kept from frame to
  frame here, and every one of them is built around where a camera is pointing: the water mask, the
  grid of what blocks light, the mirror's copy of the scenery, and the bounce lighting. With two
  players standing apart, each was rebuilt for whichever screen asked and immediately declared
  out of date by the other. The water mask never landed at all, because building one takes a few
  frames on a background thread and it was being thrown away before it could finish, every single
  time. Each screen now keeps its own. The auto-exposure meter and the fades went the same way, so
  one player walking into a cave no longer dims the other player's half of the screen, and one
  player leaving the shore no longer fades the water out from under the other.
- Your own reflection no longer depends on what the other player can see. Whether the mirrored
  player is drawn was decided by one shared answer to "is there water on screen", written by
  whichever half of the screen was drawn last.

### Performance

- Object shadows no longer re-draw the whole screen twice a second. The lean of a shadow was
  stored in the picture, so as the sun moved the stored pictures went wrong, and the answer was to
  throw all of them away and make them again. On a continuous clock that happened about twice a
  second, all day, every day: on a map with a hundred distinct plants and props that is two hundred
  offscreen redraws a second, arriving in bursts, which is what a stutter is. Each shadow is now
  judged on its own. The lean of a tall tree drifts visibly in about a second and it is remade
  then; a crop's drifts by a hair and it is left alone for minutes; and no more than a dozen are
  remade in any one frame, so a whole avenue of trees coming due together cannot become one long
  frame. Measured on the same town screen, this is roughly a sevenfold cut in that work.
- The shadow caches stop emptying themselves. Both of them answered "too many entries" by deleting
  everything, which on a map that simply has more distinct sprites than the cache holds meant
  deleting and rebuilding the entire screen every single frame. The cache became a cost instead of
  a saving, on precisely the heavily modded installs it exists to protect, which is the likeliest
  explanation for shadows on trees and bushes being reported as unplayable with a large foliage
  pack while everything else was fine. They now drop only the coldest few entries, and never one
  that was on screen a moment ago.
- Shadows for props painted into the map (street lamps, fences, signposts, cacti) stop re-reading
  the map every frame. Deciding whether a tile is a free-standing thing or part of the scenery
  means looking at the tile's picture, at what stands beside and above it, and at what the map says
  about walking on it. That is a question about the map, and the map does not change while you are
  standing in it, but it was being asked again for every tile on screen, sixty times a second. It
  is now asked once per tile and remembered until the map itself changes, which it does at the turn
  of a day or when you go somewhere else.
- Walking between two places no longer rebuilds their shadows each way. A shadow is stored against
  the sprite that casts it, which has nothing to do with which map you are standing on, so leaving
  a map and coming back was throwing away work that was still perfectly good.

Measured on one save, two runs each way, standing on the same tiles at the same time of day: the
work this mod submits per frame fell by about a third on the farm, a little under a half in town,
and about a third in the forest.

### Diagnostics

- `radiance_shadows` can see the other players. Asked why a co-op partner had no shadow, it
  answered with an empty character list, because it only ever walked the villagers. An empty list
  reads as evidence: it says nothing is there to cast, when in fact nothing had been looked at. It
  now lists every farmer the screen should be drawing for and what the shadow pass did with each.
- `radiance_report` can say the water surface changed size. Two faults fixed in this release both
  happened at the moment the window changed shape, and the report had no way to say whether that
  had ever occurred, so a report from someone who hit either one could not confirm or rule out the
  only theory anyone had.
- `radiance_emitter` is gone. It existed to find three numbers by eye while a dim room was still
  going to be fixed by deciding which pixels are a light and sparing them, and that plan has been
  replaced. The test behind it was also measurably a poor judge, so a dial that adjusted it was
  worse than no dial.

### For translators

One changed key, and only its text: **`config.preset.hint`** now says F6 instead of F8, which is
the key the tuner has actually been on. Nothing else moved, and no key was added or removed.

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
