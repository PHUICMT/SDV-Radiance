# Changelog

All notable changes to SDV-Radiance. Older releases are documented on the Nexus page.

## 1.7.1

### Fixed

- **A column of dead water stood over a modded animal that was emoting.** The balloon over a
  character's head was masked out of the water with a box measured from the collision box, three
  tiles up and two tall, which is about right over a villager and nowhere near a duck. The
  character already draws its own balloon when it stamps itself into that mask, from its own
  sprite height and whatever offset its pack asked for, so the guessed box is now only the
  fallback for a character that could not draw itself.

- **The same box, for a farmer sitting down.** A seat, a bus ride or an event pose moves where the
  game draws somebody without moving the collision box it is drawn from. The villagers already had
  that correction and the two farmer paths beside them did not, so your own balloon could ripple
  while you sat on a bench by the water.

- **The diagnostic report and the debug overlay tell you more.** Which lighting model ran and what
  it found is in the report file now, not only in an on-screen caption, and the four debug overlay
  channels that existed without being listed anywhere (`flood`, `normals`, `lampshadow`,
  `mirrorsource`) are offered by name in the console help. The mod also writes what it can see of
  your machine into the SMAPI log at startup: platform, graphics adapter, profile, whether the
  float render targets the newer lighting needs exist here, and whether four known pixels survive
  being written to a texture and read back. If you play somewhere unusual, that last line is worth
  more to a bug report than any screenshot.

### Changed

- **Doubled sheets no longer pay for the shadows.** A shadow silhouette is stamped in flat black
  and then blurred, so the smoothed diagonal the doubling buys is thrown away a moment later. The
  shadow pass draws through the game's own batch, which is the only thing the upscaler was
  checking, so every shadow was reading four times the texels it needed. Measured against 1.7.0 in
  the same frozen scene with doubling on, the shadow draw fell from 0.055 ms to 0.037 ms at Town
  after dark and from 0.089 ms to 0.075 ms at Town in daylight. Shadows look the same whether doubling is on or off, which is
  the point. This also makes the Performance tab honest: its benchmark ran on a batch of its own
  and so never saw the redirect, and reported a shadow cost lower than the one a real frame paid.

- **The water's exclusion mask stopped paying for it too.** That mask is a coverage shape, read for
  where it is opaque and nothing else, so a smoothed diagonal buys it nothing. Most of it was
  already clear; the parts where a critter, an NPC, a farm animal or an above-head scroll draws
  ITSELF were not, because those have to render through the game's own batch and that is what the
  doubling decides by.

- **The diagnostic report says which lighting model ran.** The GI line the debug overlay draws
  is in the report file too now: the model, the probe grid, and how many light seeds it found.
  A player on a platform with no console could not reach any of that before.

### For translators

**Nothing to do for this release.** No keys were added, removed or reworded: `i18n/default.json`
still holds the same 813 keys it did at 1.7.0, and Chinese and Thai are both complete against it.
Everything 1.7.1 changes is either a fix with no words attached or a diagnostic that only ever
writes to the SMAPI log, which is English by design so that a log can be read by whoever is asked
to look at it.


## 1.7.0

### Added

- **Wind in the trees.** Tree tops and bushes lean with the wind, the same wind the rain already
  leans with, so a storm tips them further and a calm day
  barely moves them. Each tree tips as one piece about the point where its canopy meets its trunk,
  a fraction of a degree, the same motion the game gives a tree you shake; a gust front sweeps
  downwind across the map, so a row of trees leans one after another rather than all together, and
  each tree keeps a rhythm of its own on top of that. Three dials on the weather page: how far, how
  fast, and how long a gust is. Shadows keep their shape, and the reflection in the water leans
  with the tree it belongs to, including the shake of a tree being chopped.

- **Sprites at twice the texels (off by default).** Every sprite sheet in use is doubled on the
  graphics card by the Scale2x rule, which turns a pixel staircase into a diagonal without
  inventing colours, and each draw is redirected to the doubled sheet at half the scale, so two
  texels stand where the game put one. The sheets themselves are never touched, so nothing that
  reads them (labels, waterlines, bakes) changes. What a texture upscaler mod does to every sheet
  at load, done only for the sheets on screen; the largest content-pack sheets are left alone.
  Its own "Smooth art" page: the switch, a smoothing dial (0 is the game's own pixels, 1 the full
  Scale2x rounding, baked into the sheets so the dial costs nothing while you play), and four
  family switches so the world, the characters and animals, the portraits, and the menus and
  dialogue lettering each smooth or stay crisp on their own.

- **Sprite relief (off by default).** Lamps and the sun now light the side of a tree, a building,
  a character or a placed thing that faces them a little more, and the side that faces away a
  little less. The relief is read from each sprite sheet's own outline and painted shading, made
  once per sheet on the graphics card, and applied as a lean around the flat answer, so the art's
  own lighting is never shaded twice and bare ground does not change. Needs the flood GI lighting;
  one switch and two dials on the lighting page. It costs a second draw of the world's sprites and
  up to 192 MB of sheet maps, which is why it is a taste to switch on.

- **Radiance cascades, the GI model 1.7.0 defaults to.** The flood lightmap can now be computed
  on the GPU as light travelling instead of by the CPU sweep: every probe casts rays that stop at
  whatever they meet, in four cascades that share the far field between neighbours, so shade under
  a canopy and a lamp's spill round a corner follow the shapes in the way, at two probes per tile
  instead of one cell. Same lights, same occluders, same composite. The lighting page has a
  two-button choice, "Classic flood" and "Radiance cascades", and switching cross-fades. Needs a
  16-bit-colour render target; a device without one keeps the flood.
  It leads not because it is cheaper on average - at rest the two measure the same - but because
  the flood's rebuild lands inside one frame: walking a lit town, the flood's worst single frame
  came to 2.33 ms against the cascades' 0.26 ms, and a stutter is what a player feels. The flood
  is still one button away for anyone who prefers its picture.

- **Bounced light takes the colour of what it bounced off (off by default).** A red barn now throws
  a little red on the ground beside it, and a green field lifts what stands in it toward green. The
  bounce field has always been stripped of most of its hue on purpose, because every seed in it is
  the same warm colour and one hue multiplied over a whole screen is a dye rather than lighting.
  The hue bounced light actually carries is the hue of the surfaces around it, which is a different
  colour at every pixel and so cannot wash the frame that way. Read from the couple of tiles around
  each pixel and applied as hue only, so nothing gets brighter or darker than it was; one dial on
  the lighting page, 0 being exactly the picture that shipped.

- **Rim light from lamps.** The edge of a sprite that faces a lamp now catches a bright fringe in
  that lamp's own colour, so a person or a tree standing near a light reads as being lit from that
  side rather than merely standing next to it. Two lamps of different colours light their own edges
  of the same tree. It is added over the picture rather than mixed into it, which is the point: the
  sprite relief can only make a side lighter or darker than the art already is, and an outline the
  artist drew near black stays near black however it leans. Reads from the same sheet normal maps
  the relief builds, so it needs the relief on; one dial on the lighting page.

- **Leaves catch the light.** Patches of canopy brighten and dim the way leaf faces flip in wind,
  travelling through the crown - which is what wind in leaves actually looks like at sprite scale:
  glitter, not geometry. Brightness only, so the art itself never moves and cannot tear, the lesson
  both the tree sway and the water's waves taught this release. It reads the green of the leaves, so
  a fall canopy shows it less, and it rides the sprite relief's own buffers, so it needs the relief
  on; one dial on the lighting page beside the relief's others.

- **Shooting stars.** Now and then on a clear night, any season, a streak crosses the sky the water
  reflects and is gone in under a second. Like the aurora it lives only in the water, where the sky
  appears; one switch on the weather page. One arrives within the first twenty seconds of a clear
  night by the water and roughly every half minute to a minute after that; the first build waited
  up to two minutes between streaks, each under a second long, in a spot picked at random that as
  often as not was dry ground where the sky does not appear, which is a feature almost nobody would
  ever have seen. Up to three cross at once, in three weights: most are faint and quick, a few
  ordinary, and about one in twelve a heavy one that burns wider, longer and warmer. They
  sometimes arrive in a cluster of two or three within a second, because a streak every half
  minute on the dot reads as a scripted event rather than a sky. `radiance_star` brings three
  forward on demand, spread across the view.

- **Aurora on clear winter nights.** Slow curtains of green and violet drift across the sky the
  water reflects. Only the water shows them, because the water is the only place this camera ever
  sees the sky, which is also what makes it read as a reflection rather than a filter. Clear
  winter nights only, eased in and out with dusk and the weather; a switch and a strength dial on
  the weather page. The dial exists because the first build had none and the shipped constant was
  arithmetic that had never been checked against a screen: open water contributes about a quarter
  of the sky to what you see, and the curtain's own falloff stood at full height almost nowhere, so
  a typical curtain arrived at roughly five values out of 255 on night water. Worse, both the
  aurora and the shooting star were mixed into the reflection, and the reflection is gated on how
  bright the water already is, which is right for mirroring a lit bank and exactly wrong for the
  sky's own light: dark water is where an aurora shows. They are now added over the finished
  water instead of mixed into it, the curtains are broader, and there is a dial.
  What makes a curtain read as an aurora turned out to be shape rather than strength: it is
  narrow across and long along, it snakes, and it is combed into fine rays down its length. The
  first version summed two slow sines, which has none of those and drew a smooth hump that read
  as green haze on the water. The curtains are ribbons now, layered two deep, combed into rays,
  and coloured across their width the way a real one is: green through the core, teal at the
  shoulders, violet out at the fringe.
  A display is an EVENT rather than a fixture: a roll taken once per night decides whether tonight
  carries one at all, when it starts and how long it runs, so some clear winter nights have none
  and the ones that do build and die over a few hours. It surges the way a real one does, a
  brightening running along a curtain and dying, and it lights more than the water: the sand and
  the rocks beside the sea take the colour of the sky, and a window pane in a street reflects it,
  both as hue only so nothing gets a step brighter than it was.

- **Golden hour.** In the first and last hours of the sun, every shadow stretches further still,
  the way a low sun really throws them: characters, trees, placed things and the patch of daylight
  a window lays on the floor all agree, because they all ask the same sun. The middle of the day
  never changes. One dial on the shadows page, off by default - a taste to turn up; 0 is exactly
  the old geometry.

- **Colored light glows on its own.** Bloom used to ask one question, "is this pixel bright?",
  which treats a lit window pane and a white wall the same. It now also asks "does it shine with a
  color?": saturated bright pixels, the lit panes, lava, flames and crystals, may glow below the
  normal threshold, while gray pixels never qualify however bright, so snow and white walls stay
  exactly as quiet as before. One dial on the bloom page, "Colored-light glow"; 0 restores the old
  behavior.

- **Lamp shafts, rebuilt, still off by default.** The beams a lamp, torch or fire throws are now
  cut the way the sun's have been since 1.6.1: from what actually stands beside the light. The
  flood pass already marches each light's ray against the occluder mask to shadow its pool; two
  probes beside that path now ask whether a wall, a doorway frame or a tree blocks the light
  next door, and where one does, the open side gets a beam. Open floor beside open floor is
  evenly lit and shows nothing, which is the physics. The old lamp rays were a bright-pass, a
  streak drawn out of any pixel bright enough near a lamp, which made every pale sprite a light
  source and is why they shipped switched off for a year. Nothing but the scene's own occluders
  can make a beam now, so the Known issue that kept them off is closed and the `godrays.fx`
  shader is gone. The strength dial is the one setting left on the lamp side; the threshold,
  density and ray-length dials described the streak and have nothing to shape any more. They
  stay off by default: on a walk with a glow ring the beams kept finding "gaps" in ordinary
  streets, and the shadows carry the scene without them, so they are a taste to switch on. The
  strength dial is reset to 1 once by the config migration, because the old value meant an
  additive gain and would draw shafts nobody can see.
- **Waterfall mist, hot-spring steam and lava sparks.** Three particle emitters that read the
  same painted labels the water does. A fall is a vertical run of strongly flowing tiles, and
  the mist rises from where each run lands, so a two-tier fall puffs at both plunges. Steam
  drifts up off hot-spring tiles, and the volcano's lava throws sparks that rise and fall back.
  Each has its own switch, amount and size on the particles page.
- **Fences, bushes and boulders shadow lamp light as their own shapes.** The occluder mask the
  light shadows and both kinds of shaft read from held one texel per tile, so a fence was a
  solid square and a torch behind it lit the far side evenly. It now holds four texels per tile
  and the game's own art draws the shapes into it: a fence picks its piece from its neighbours,
  a bush its season and size, a boulder its sheet. A torch behind a fence throws a comb of light
  between the pickets and a bush leaves a leafy edge. Walls and tree trunks are unchanged. Some
  players prefer the rounder pools, so it is a switch on the lighting page, **Shadows shaped by
  fences and bushes**, on by default; the two looks cross-fade when it is flipped.
- **Placed things block lamp light.** Kegs, chests, machines, scarecrows, signs and floor
  furniture stand in a lamp's light as the shapes they are, so the barrel beside a torch throws
  a shadow at night the way it does at noon; until now a lamp saw straight through all of them
  while the sun did not. Each blocks by its footprint on the ground, with its sprite over it so
  its own face keeps the light; a weed, a twig or a stone blocks by its sprite alone. A thing
  that is itself a light never shadows itself. **Shadows from placed things** on the lighting
  page, on by default; it costs nothing measurable, since the mask is rebuilt only when something
  is placed, picked up or lit, and only the tiles in view are asked.
- **The shadow march steps one mask texel at a time** (eight to a tile, up to 48 steps), instead
  of a fixed sixteen. With a fixed count the gap between steps grew with the ray until it was
  wider than a plant or a post, and some rays hit it while their neighbours passed between two
  steps: painted raw, a fan of plates with seams between them; on screen, a saw-toothed edge that
  crawled as the light moved. **Shadow edge softness** is now a real blur read from soft copies of
  the mask (a half, a quarter, an eighth), decided once per ray where it first meets something, so
  0 is a crisp edge, 1 about a quarter of a tile of penumbra a tile and a half out, and 2 up to a
  whole tile with the far pickets of a fence thinning as a penumbra thins them.
- **Lamp shadows cut into the game's own glow.** The game paints every lamp as a round glow
  before the mod runs, so the per-light shadow could only shade what the mod added and a pool
  stayed round behind a tree trunk. Where a light's ray is blocked, part of the game's glow now
  goes with it, by a new dial on the lighting page, **Shadow cuts into the glow** (0 keeps the
  round pools, 1 cuts through; ships at 0.76, the value that was tuned in). **Shadow edge
  softness** ships at 1.45 for the same reason.
- **Lamp shadows soften with distance.** A shadow's edge is now a ramp whose width grows with
  how far the light has travelled past the thing that cut it, the way a real lamp's width shows
  in its shadows: hard right beside a fence post, soft a few tiles on. **Shadow edge softness**
  on the lighting page sets how much (0 hard, 2 twice the default).
- **Heat haze.** Hot air over lava bends the picture seen through it, the way air over a summer
  road does, from a per-tile heat grid built from the lava labels and the volcano itself. It
  fades out round the player so the sprite the eye follows never swims, and it leaves hot
  springs alone: their air is wet and the steam already says hot. Its own switch and strength
  live at the end of the fog page.

- **How much tilt-shift is kept indoors.** The blur reads height on screen as distance, which is
  true outdoors, where the top of the frame is most of a map away. A room is a few tiles deep from
  the far wall to the floor at your feet, so the same band ratios reach furniture that is barely
  further back than you are. The new dial, on the lens page of both menus, shortens that reach
  without touching what happens outside: 1 is the picture that has always shipped and stays the
  default, 0 keeps a room evenly sharp.

- **Daylight strength for windows.** The daylight a window draws into a room, the lit pane, the
  beam and the patch of sun on the floor, has one dial now, on the windows page in the settings
  menu and the tuner. 1 is the look that shipped. Asked for by a player whose farmhouse window
  looked right while the two big windows of a villager's kitchen blew out to white, and whose only
  remedy was the master switch, which also took the farmhouse's morning light with it. The light a
  window adds to the room's own lighting is separate and does not move with the dial.

### Changed

- **Shadow shapes are a choice, named by version.** Two things about a shadow's shape changed in
  1.7 and neither had a dial: a placed thing's shadow now stands on the row its art really ends on
  instead of hanging from its cell, and a horse, a pet or a wildlife mod's creature lies down
  across the ground instead of standing up on edge the way a person does. Each moved every shadow
  of its kind at once, so the shadows page now opens the way the water page does, with two buttons:
  Shadows 1.7 or Shadows 1.6. Shape only. Everything 1.7 fixed is fixed under both, so creatures
  from other mods still cast, riding still leaves you a shadow, and a horse still faces the way it
  is drawn: none of those was a look anybody chose.

- **A look is named by the version it shipped in.** The water choice read "1.6.2 water" against
  "Classic": one side carried a patch digit nobody needs in order to pick a look, and the other
  carried no version at all, so a player deciding between them had to already know which release
  "Classic" meant. Both sides are now the two-part version the look shipped in, Water 1.6 against
  Water 1.5, and the GI choice is simply Flood against Radiance cascades, which are techniques
  rather than versions and no longer pretend otherwise. Names only; nothing about the water or the
  lightmap changed.

- **The tuner hides what cannot work.** A dial under a switch that is off used to sit there greyed
  out; now it is not on the page at all, and it comes back the moment the switch goes on. Every
  dial's owner was checked against the code that reads it, not against what sounded right: the
  blue-light filter stays visible with the colour grade off because the finishing pass applies it
  either way; the tilt-shift radius shows only for the radial focus and the top and bottom ratios
  only for the bands; a particle's amount and size go with that particle's own switch; the sun
  shafts hang off the sun switch alone. The lighting page is regrouped into the darkness dials,
  the shadows lamps throw, and the bounced light, so the lamp-shadow softness dials sit next to
  the lamp-shadow switch instead of a whole GI block away from it.

### Fixed

- **Riding took every shadow off the screen.** Getting on a horse hands it to the rider: the game
  takes the horse out of the location's character list and the rider draws it instead. The shadow
  pass reads that list, so it could not see the horse, and it skips the rider because the horse's
  shadow covers them. Between the two, a mounted player and their horse crossed a sunlit field
  with nothing under either of them. The mount is now part of the caster list wherever it is
  drawn from.

- **A horse's shadow faced the wrong way.** A horse has one set of frames and the game mirrors
  them to face the other way, so a silhouette cut from the frame and drawn unmirrored was a horse
  facing backwards: head where the tail should be. Anything the game mirrors now has its shadow
  mirrored with it. Farm animals are unaffected, because they have real frames for each direction.

- **A horse's shadow stood up on edge beside it.** Shadows are laid down on the ground by two
  different amounts: a person is thin and tall, so their shadow is mostly length, while a
  four-legged animal is wide and low and its shadow is mostly width. Horses and pets are NPCs in
  the game's own code, so they were taking the person's amount and their shadow leaned up beside
  them instead of lying across the ground. Which one a character gets is now read from the frame
  the game gave it, so a mod's crab and a mod's villager each get the right one without either
  being named anywhere.

- **Creatures from wildlife and companion mods had no shadow.** A mod that adds its own creatures
  usually tells the game not to draw its round shadow blob, because it means to draw one of its
  own, scaled to the creature. Custom Companions does this for every companion and then draws a
  blob only for the ones its content pack asked for, which most packs leave off. We were reading
  that flag the way the game's own code means it, "this thing has no shadow", so a pack's crabs,
  ducks and deer stood on the ground with nothing under them while every other creature beside
  them cast one. Outside the game's own code the flag now means what the mod meant by it, and
  those creatures cast like anything else. A creature laying down still casts nothing.

- **A tree looked like two pieces stacked, with sprite relief on.** A tree is drawn as two
  sprites the artist lined up by hand: a canopy whose trunk art stops part way down, and a
  separate trunk piece that takes over below it. The relief works out its shading for each sprite
  on its own, so the row where the canopy's art stops was shaded like the edge of a real object,
  and that shading landed across the trunk as a dark line. The trunk keeps the canopy's shading
  through the join now. It gives up a little of its own depth for it, which is the right trade:
  a trunk without relief still reads as a trunk, and one cut in half does not.

- **A dark seam around tiles, in every language but English.** Sprite relief never bevels the
  map's own tiles, because the shading is worked out for a whole sheet at once and at a tile's
  border it reads the next tile along, which is unrelated art. The test for "is this the map's
  own art" missed a sheet the game had swapped for a translated one, so on a game running in
  Thai, Chinese, Japanese or Russian the seam came back on any tile the game happens to draw
  among the sprites: the town fountain's jets were where it showed most. It also depended on the
  season, because a translation pack replaces the sheets it has and leaves the rest, so summer
  could look right while fall did not. Relief ships off by default, so this only ever showed for
  someone who turned it on.

- **The desert oasis rippled straight over the palms.** The pond there is water the game never
  animates, so this mod's labels are what bring it to life - but the near-water test that decides
  which sprites need carving out of the ripple only knew the game's own water, answered "no water
  anywhere in the desert", and skipped every palm. The test now reads the same composed answer the
  effect itself uses, so anything standing in labelled water is carved like anything standing in a
  lake. The lower trunk is also carved now: a tree is drawn in two pieces and only the top one was
  being excluded, which left the base of a palm underwater even once its crown was safe.

- **A moving cut across the trunk of a swaying tree.** The wind tipped the top piece of a tree and
  not the base piece the game draws with it, and on tree art that keeps most of its trunk in the
  top piece the boundary crossed the trunk in mid-air and slid side to side as the tree leaned.
  Both pieces now tip together about the same point at the roots, so the tree is rigid and the
  seam cannot open. A chopped stump still stands still.

- **Colour banding in fog, the colour grade and the vignette.** A slow gradient cannot survive an
  8-bit frame without stepping, and those steps read as bands across a fog wisp, a graded sky or
  the corner falloff. The water pass has dithered its own writes since 1.6.2; the fog, grade,
  fused tail and finishing passes now do the same - a sub-level triangular dither, static across
  frames, far too small to see as texture. No settings; it is correctness, not a look.

- **A strip of dead water hugged the bank of a forest stream.** Where the map draws bank art over
  its own water, the labels for that art say "none of this is liquid", and the rule that a
  labelled tile is described by its labels took that as the answer for the whole tile: five tiles
  of a two-tile stream shipped with no effect at all, a flat vanilla strip beside water wearing
  everything. The art in question is a quarter to a half transparent and the water under it is the
  game's own, which is why it only shows up where a recolour has repainted the sheet the water art
  came from, since that is where the label describing the water itself goes unread. An overlay
  saying "not liquid" no longer speaks for the ground beneath it: the overlay is still carved by
  its own opacity, per pixel, and a label painted on the ground itself is obeyed as before.
- **A narrow stream wore the river beside it as a pale sheet.** Where a stream runs two tiles from
  a river, the mirror's source, reaching up from a pixel three tiles down, landed on the river's
  surface and brought it back across dry land: a washed-out band over the stream's near half that
  read as a bite taken out of the water. The mask already measured whether a source was water, but
  as a vertical average, and a two-tile bank sits inside that average. The mirror now asks the map
  itself, one answer per tile: a water source resolves to sky within a tile, flat ground within
  one to two and a half, a deck within one and a half to three and a half for its posts and rails,
  and a wall, a roof or a cliff keeps the reach it had. A river's far bank, a bridge and whoever
  stands on one are untouched, and everything here rides the reflection reach dial as before.
  **`radiance_debug mirrorsource`** paints what the mirror is reading over the water itself: red
  where its source is flat ground, green where its source is water, blue for how much of it has
  already given way to the sky.
- **A creature from another mod cast no shadow and kept its round blob.** Custom Companions
  tells the game not to draw a shadow for its companions and paints a round one itself, and to
  the shadow pass "draw no shadow" meant the creature wanted none, so a companion stood in the
  sun with a blob under it while everything beside it cast a real one. A creature that paints
  its own blob wants a shadow: it now casts like any villager, and the blob it paints is dropped
  while ours are casting. Told by the draw itself, not by the mod's name, so a companion set up to
  have no shadow keeps none, and any other mod that shadows its creatures this way is covered.
- **A fish pond either mirrored the lake behind it or had no water at all.** A built pond is
  three rows of water inside a raised stone wall, and the mirror read it like any pond: whatever
  stands above the far edge appears below it, as deep as it is tall. Two tiles past the wall on
  many farms is the lake, so the bottom row of a pond carried a slice of the lake, cut off where
  the pond ended, over water the fish had coloured red or green. Water behind a pond's wall now
  resolves to sky the moment the mirror meets it, the way the wide rivers already do a few tiles
  out; the trees, the fences and whoever stands behind the pond still appear in it, and so does
  the pond's own far wall, which stood where the mirror used to show the ground under the rim.
  And on a farm whose dirt carries a label, the label's "this is ground" carved the whole pond
  away, so whether a pond rippled at all depended on whether somebody had labelled the ground
  under it. The pond's water is the building's, drawn over the map, so its tiles no longer read
  the map; the surface pass sees a pond as water inside a low wall rather than a five-by-five
  block of wall, so a lamp beside it lights the water and the sun's dapple no longer treats it
  as canopy; a pond stops counting as a still puddle, since the rule that calms a nine-tile pool
  had its ripple, glints and reflections at half strength; and the netting frame a pond hangs
  behind itself keeps the ripple off the net. One more, from 1.6.2: when the water learned to keep
  off the art a location draws for itself, it kept off the pond's own water too, since a pond is
  drawn by its location and everything it paints, bed, water, net and fish, was read as art over
  water. A pond on 1.6.2 had no effect at all. What a pond paints as water is water now; its
  rim, net, sign and bucket are still kept dry. And the water reaches the stones: the game paints
  a pond's water half a tile in under its rim on every side, and the mask stopped at the tile
  line, which left a strip of untouched water along the wall.
- **A lamp standing inside something put its own light out.** The occluder mask stamps a
  building's whole footprint solid, and the farmhouse porch is part of that footprint, so a glow
  ring worn while standing on the boards sent every one of its rays out through an occluder: the
  shadow took the game's own glow with it and the pool went out as the player stepped up. A light
  whose own tile is blocked no longer shadows itself, and it throws no lamp shafts either. The
  same footprint was also shadowing the house it belongs to, since the game draws a building's
  face and roof over the tiles north of the ones it stands on: the boards went dark stepping off
  the porch and lit again stepping back on. A wall now keeps the light that reaches its face,
  while the ground in front of it still takes the shadow. And nothing the farmer can walk on
  blocks a lamp any more, whatever the surface map calls it: the porch is roof by class, since it
  sits under the overhang, so stepping onto the boards used to switch off every shadow the
  carried light cast and stepping down switched them back on. Measured over the ground both
  frames share, the picture moved 6.63 crossing that line where the game's own moved 0.05; it
  now moves 0.14.
- **The rain's slant dial did almost nothing at its top end.** It multiplied the wind the game
  reports, and on a quiet rainy day the game reports almost none, so three times almost nothing
  was still rain falling straight down. Above 1 it now brings a wind of its own, and the rain
  leans on a still day too. At 1 and below it is unchanged, so a save that never touched it falls
  exactly as it did.
- **A farm building let lamp light straight through it.** The rule that a walkable tile cannot
  be the inside of a wall asked the map, and a coop or a barn is not in the map: it stands on
  grass the map calls passable. Every farm building fell out of the mask the day that rule
  landed. A building now answers for itself by its own collision map, which keeps the farmhouse
  porch and every doorway open.
- **A glow ring threw black wedges at half past six in the morning.** The shadow's carve took
  its full share of the scene at every hour, though outdoors by day the game paints no glow for
  a ring at all. Both shadow terms now follow the tint the game paints the outdoors with: none at
  noon, a third under rain, full at night. Indoors the game draws its lamps at every hour and so
  do their shadows.
- **Stumps, boulders and logs cast no lamp shadow.** A vanilla clump names no texture of its own,
  and asking the content manager for a null name threw and was silently caught. They cast now,
  from the object sheet, and saplings at the bush and small-tree stages cast a thinner post.
- **A lit gap sat between a thing and its shadow.** The fade beside the pixel that once kept a
  wall's lit face from going dark covered a third of a tile of every ray, and pixelOpen keeps the
  wall's face now, so it is a sliver.
- **Sun dapple came out as tile squares.** The sun shafts read the same mask the lamp shadows do,
  and that mask now carries a solid footprint for every keg, post and weed, which the shaft march
  took for canopy. The sun reads the tile grid alone: dapple is what a canopy does to sunlight.
- **The occluder mask was rebuilt on every frame.** When it became a render target at four texels
  per tile, the test that decides whether the cached one still fits kept comparing its width to
  the number of TILES, which it can never equal, so every fence, bush and tree trunk was redrawn
  sixty times a second to answer a question whose answer had not changed. Measured at 0.33 ms per
  frame on the beach and 0.57 on a fenced farm, all of it given back.
- **Sparks and shadows from the mines' wall torches sat half a tile to the left.** The game
  lights a mine sconce from the top-left corner of its tile but draws the flame's glow at the
  tile's centre; the mod now follows the game's own glow list, so embers rise from the flame and
  the torch's shadows and pool are cast from it. Hearths and fireplaces were already right and
  do not move.
- **A glow ring taken off and put back on never cast shadows again.** The eight lights that get
  a shadow ray were the first eight in the light array, and array slots stay with a light for
  its whole stay, so "first eight" meant "arrived earliest". A re-equipped ring came back as a
  new light in a late slot behind twenty street lamps, some of them off screen, and its shadow
  weight stayed at zero until the map changed. The tier is chosen by rank now, eased as before.
- **Lamp shadows reached into ground the game shows as night.** A light's pool grew with the
  game's radius without limit, so a glow ring's pool spanned five screens and every occluder on
  screen cut a wedge into the dark; and a shadow kept its full contrast wherever the pool still
  reached at all. The pool reach is capped at 1.2 screen heights, the shadow's contrast now fades
  with the pool, and the cut into the game's own glow follows the pool's core, so a shadow lives
  inside its light and thins to nothing at the edge of it. With the shadow thinning like that,
  the old per-light shadow strength of 0.7 reads as barely there, so it ships at 1 and an
  untouched 0.7 is moved to 1 once by the config migration.
- **Night mist rows greyed out when day fog was off.** The two fogs are separate switches, and
  the tuner's night mist rows now follow their own.
- **Character shadows flickered while walking through a room with more than 24 lights.** The
  shadow pass kept the 24 lights nearest the screen centre, a number copied from the lighting
  pass before that pass grew to 48; the saloon at night carries up to 39, so a dozen of them
  traded places with every step and every NPC one of them lit gained and lost that cast. The
  shadow pass now reads the lighting pass's own budget.
- **The sea reflects the beach right up to the tide line again.** 1.6.2 labelled the surf
  crests on the beach and island sheets as falling water so they would neither sway nor mirror,
  and the water beside them went on mirroring the sand, which left a band of plain water colour
  between the beach and its reflection. The crests are painted as water again, so the reflection
  runs up to the foam the way it did in 1.5.3.
- **A rectangle of flat water hung over a player sitting on a bench by the water.** The patch of
  water the ripple and the mirror leave alone so the player's own body never ripples was placed
  from the collision box plus the game's draw-time bob, and a seated player carries a bob of 48
  pixels that the game itself mostly cancels with the sitting frame. The patch stood that far
  above the seated body: a tile of dead water over the head on the beach pier bench, reported
  with a picture. The patch and the player's own reflection now hang from where the game drew
  the body this frame, and a seated player, who is on a bench and not in the water, gets no
  patch at all.
- **A fruit tree sapling cast no shadow.** A fruit tree still growing, from the day it is planted
  to the day before it bears, was skipped by the shadow pass and the game paints no shadow of
  its own under it either, so a young orchard stood on a lit lawn with nothing under it while
  the wild sapling beside it cast one. It now casts from its own art like a wild sapling does,
  with the short lean a bush takes.
- **Everything a location drew was being carved out of the water a frame late.** 1.6.2 began
  recording what a location paints for itself, so the boat at Ginger Island could stay out of
  the ripple. The recorder wrapped the game's own location draw as well, which is where every
  villager, animal, critter, tree, placed object, piece of debris and puff of smoke is drawn, and
  all of it was stamped into the water mask at the previous frame's position and at the mask's
  own resolution. Chimney smoke drifting over a lake came out as blocks, and a bird, a falling
  leaf or a fish's splash crossing a river cut a hole through the water and the reflection under
  it as it went. The recorder now pauses for the game's own draw and keeps only what the
  location itself paints, which is what it was for.
- **A creature that drew its own mirror while standing off the screen could lose that frame of
  its reflection for the rest of the session.** The game draws no character more than two tiles
  off the screen, and the mirror asks creatures to draw themselves from a window that reaches
  further than that, so one standing above the view came out empty. That emptiness was remembered
  against its sheet and animation frame, and every creature of its kind then had no reflection on
  that frame of its walk, which read as blinking. An empty bake is no longer remembered, and the
  body falls back to the built stamp for that frame instead of vanishing.
- **`radiance_weather` set the weather only halfway.** It wrote the rain and snow flags but not
  the weather id beside them, so the rain stopped falling while anything that asks a location
  what its weather is kept answering the old one. Both are written now.

### Removed

- The lamp-ray bright-pass (`godrays.fx`) and its three dials: light threshold, ray density
  and ray length. See Added.

### For translators

- Wind in the trees (weather): 14 new keys - `config.weather.foliagesway.name`/`.tooltip`, `config.weather.foliageswaystrength.name`/`.tooltip`, `tuner.foliagesway`, `tuner.foliageswaystrength`, `help.foliagesway`, `help.foliageswaystrength`, the two weather-page headings `tuner.section.foliagesway` and `tuner.section.sky`, and `config.weather.foliagesway<part>.name`/`.tooltip` for `speed` and `gustspan`.
- Lighting page headings (tuner): 2 new keys - `tuner.section.lampshadows`, `tuner.section.gi`.
- Sprites at twice the texels (its own "Smooth art" page): 16 new keys - `config.sheetupscale.name`/`.tooltip`, `help.sheetupscale`, `tuner.tab.smoothing`, `tuner.desc.smoothing`, `tuner.section.smoothingfamilies`, and `config.sheetupscale<part>.name`/`.tooltip` for the four `smoothness`, `world`, `characters`, `portraits`, `interface` settings (10 keys).

- Leaf shimmer (lighting): 4 new keys - `config.lighting.leafshimmer.name`/`.tooltip`, `tuner.leafshimmer`, `help.leafshimmer`.
- Colour bleed (lighting): 4 new keys - `config.lighting.colourbleed.name`/`.tooltip`, `tuner.colourbleed`, `help.colourbleed`.
- Rim light (lighting): 4 new keys - `config.lighting.reliefrim.name`/`.tooltip`, `tuner.reliefrim`, `help.reliefrim`.
- Sprite relief (lighting): 12 new keys - `config.lighting.relief.name`/`.tooltip`, `config.lighting.reliefstrength.name`/`.tooltip`, `config.lighting.reliefsun.name`/`.tooltip`, `tuner.relief`, `tuner.reliefstrength`, `tuner.reliefsun`, `help.relief`, `help.reliefstrength`, `help.reliefsun`.

- GI model (lighting): 8 new keys - `config.lighting.gimodel.name`/`.tooltip`/`.flood`/`.cascades`, `tuner.gimodel.flood`/`.cascades`, `help.gimodel.flood`/`.cascades`.

- Shooting stars (weather): 4 new keys - `config.weather.shootingstars.name`/`.tooltip`, `tuner.shootingstars`, `help.shootingstars`.
- Aurora strength (weather): 4 new keys - `config.weather.aurorastrength.name`/`.tooltip`, `tuner.aurorastrength`, `help.aurorastrength`.

- Aurora (weather): 4 new keys - `config.weather.aurora.name`/`.tooltip`, `tuner.aurora`, `help.aurora`.

- Golden hour (shadows): 4 new keys - `config.shadows.goldenhour.name`/`.tooltip`, `tuner.goldenhour`, `help.goldenhour`.

- Shadow shapes (shadows): 9 new keys - `config.shadows.model.name`/`.tooltip`/`.modern`/`.classic`, `tuner.shadowmodel`/`.modern`/`.classic`, `help.shadowmodel.modern`/`.classic`. The two `.modern`/`.classic` labels are the version names "Shadows 1.7" and "Shadows 1.6"; keep the version numbers as digits and translate only the word beside them.

- **Changed meaning, 6 existing keys.** The two model choices are now named by version, so these
  read differently even though the setting behind each is untouched:
  `tuner.watermodel.modern` and `config.water.model.modern` are "Water 1.6" (was "1.6.2 water");
  `tuner.watermodel.classic` and `config.water.model.classic` are "Water 1.5" (was "Classic");
  `tuner.gimodel.flood` and `config.lighting.gimodel.flood` are "Flood" (was "Classic flood").
  `config.water.model.tooltip` and `config.lighting.gimodel.tooltip` name those buttons and were
  reworded to match.

- Tilt-shift indoors (lens): 4 new keys - `config.tiltshift.indoor.name`/`.tooltip`, `tuner.tiltindoor`, `help.tiltindoor`.

- Colored-light glow (bloom): 4 new keys - `config.bloom.emissiveboost.name`/`.tooltip`, `tuner.bloomemissiveboost`, `help.bloomemissiveboost`.

**Particles: 15 new keys.** For each of `waterfallmist`, `hotspringsteam` and `lavasparks`,
the five keys `config.particles.<name>.name`, `config.particles.<name>.tooltip`,
`tuner.section.particle<name>`, `tuner.particle<name>` and `help.particle<name>`. "Mist" is the
fine spray a waterfall throws where it lands; "steam" is the visible vapour over hot water;
"sparks" are the embers a lava surface throws up.

**Rain slant: 2 reworded keys.** `config.precipitation.rainslant.tooltip` and
`help.precipitationrainslant`. The dial is no longer only a multiplier on the game's wind: above
1 it brings a wind of its own, so the text says "how hard the rain leans" rather than "how much of
the wind the rain feels". The name keys are unchanged.

**Placed things: 4 new keys.** `config.lighting.props.name`, `config.lighting.props.tooltip`,
`tuner.lightprops` and `help.lightprops`. "Placed things" means what the player has put down:
kegs, chests, machines, signs, floor furniture. Not map scenery, which was already covered.

**Shadow softness: 4 new keys.** `config.lighting.shadowsoftness.name`, `config.lighting.shadowsoftness.tooltip`,
`tuner.lightshadowsoftness` and `help.lightshadowsoftness`. "Edge softness" is how blurred the
shadow's border is, not how dark the shadow is.

**Shadow depth: 4 new keys.** `config.lighting.shadowcarve.name`, `config.lighting.shadowcarve.tooltip`,
`tuner.lightshadowcarve` and `help.lightshadowcarve`. The game draws each lamp as a round glow before
the mod runs; this dial is how much of that glow a shadow removes, so "cuts into the glow" is literal.

**Shadow shapes: 4 new keys.** `config.lighting.silhouettes.name`, `config.lighting.silhouettes.tooltip`,
`tuner.lightsilhouettes` and `help.lightsilhouettes`. A "comb of light" is the row of bright
stripes a lamp throws through the gaps of a fence, like sunlight through a picket fence.

**Heat haze: 9 new keys.** `config.heathaze.name`, `config.heathaze.tooltip`,
`config.heathaze.strength.name`, `config.heathaze.strength.tooltip`, `tuner.section.heathaze`,
`tuner.heathaze`, `help.heathaze`, `tuner.heathazestrength` and `help.heathazestrength`. "Heat
haze" is the shimmer of hot air; the player-facing strength label is "How far it bends".

**Window daylight: 4 new keys.** `config.lighting.windowdaylightstrength.name`,
`config.lighting.windowdaylightstrength.tooltip`, `tuner.windowdaylightstrength` and
`help.windowdaylightstrength`. "Daylight strength" is how bright the light through an indoor
window is drawn (the lit pane, the beam, the sun on the floor); it is not the room's own light.

**Lamp shafts: 12 keys removed, 2 reworded.** Removed, because the dials they named are gone:
`config.godrays.threshold.name`, `config.godrays.density.name`, `config.godrays.sectionboth`,
`config.godrays.decay.name`, `config.godrays.decay.tooltip`, `tuner.godraysthreshold`,
`tuner.godraysdensity`, `tuner.section.godraysboth`, `tuner.godraysdecay`,
`help.godraysthreshold`, `help.godraysdensity` and `help.godraysdecay`. Reworded, and worth
re-translating rather than keeping: `config.godrays.enabled.tooltip` and `help.godrays` now
describe beams cut from the occluders beside a lamp (doorways, window frames, a tree by a
street lamp) and say the effect needs the flood lighting on; it is still off by default.
The Chinese file keeps the old wording for these two until it is updated.

**Heat haze tooltip reworded once already:** `config.heathaze.tooltip` says the haze spares
the player and that hot springs steam rather than shimmer. Translate the current English.

## 1.6.2

### Added

- **A new water.** The water page now opens with one choice, **1.6.2 water** or **Classic**,
  and shows only the chosen water's own dials. Classic is the water of every release up to
  1.6.1, untouched, with its three looks and their distortion and banding underneath it, so a
  player who liked it keeps it. The 1.6.2 water is a different mirror, built from what water
  and a camera actually do rather than a fourth pair of numbers for the old one. The image is moved by a field of three
  travelling ripple octaves, the slow wide one dominant, instead of by the surface's single
  sine, because what the eye reads as liquid is that spectrum of movement, not the accuracy of
  the image (one sine reads as jelly). The movement is anchored at the contact line: nothing
  moves at the waterline and it grows over the first tiles of depth, which is what keeps a
  reflection standing on the thing casting it instead of drifting beside it like a sticker.
  Reflected people and reflected scenery read the same field at the same amplitude, where the
  classic looks have always moved them by different amounts. With depth the reflection gives
  way to the water's own colour, sharp under the far bank and thinning toward you, its contrast
  folds toward a mid tone (light reflects darker, dark reflects lighter, as a photograph of a
  lake shows), it loses a little saturation, it can be drawn longer than a flat mirror
  would (Vertical stretch, 1 by default), and it answers the camera: the image skews with its place on the screen the way an
  image under the surface does for a camera that is not straight overhead, so the reflection
  shifts against the ground as you walk instead of being a static flip.

  Its nine settings, Wobble, Choppiness, Parallax, Depth fade, Vertical stretch, Edge
  softness (the ripples' bands cut a sloping reflected edge into teeth; this melts their tips),
  Waterfall churn, Churn reach and Fade before the lip,
  are in the settings menu under their own heading and in the tuner under its button. Softness, depth and
  reach apply to both waters.

- **The pool under a waterfall is churn, not a mirror.** The water at the foot of a fall is
  full of air and torn up; it reflects nothing there and settles back over the next few
  tiles. The mirror used to run right up to the foam, and what it showed was the cliff and
  the falling column above, a flat grey sheet laid across the pool. The mask build now
  measures how far below the nearest falling face each texel of water sits, and the 1.6.2
  water lets its reflection go by that distance: none at the foot, back in full three tiles
  down by default, the churned water itself a little paler and milkier than the pool around
  it, and a body standing in the plunge loses most of its reflection the same way. Two
  settings, **Waterfall churn** (how fully the mirror gives way) and **Churn reach** (how many
  tiles it takes to come back). The same field holds how far above a fall's lip each texel of
  the stream sits, and the stream's reflection now lets go over the last stretch before the
  edge, half a tile by default (**Fade before the lip**), instead of stopping on the one pixel
  row where the face begins. The classic water is untouched.

- **A falling leaf is bent by the water's own wave.** Petals and leaves already bent as they
  crossed a pond, because the surface bends everything drawn over it, and that turned out to be
  the nicest thing about them. It is no longer only over water. The leaf is drawn in eight bands
  and each band asks the same ripple field the water pass asks, at its own place in the world and
  off the same clock, so the wave runs along the leaf rather than shifting it as a block, and a
  leaf drifting across the shoreline is bent by one continuous wave the whole way instead of
  changing character at the water's edge. The bands turn with the leaf, so one lying on its side
  bends along the way it is pointing.

  One setting, **Flutter**, under Blossom and leaves in the settings menu and the tuner: 1 bends a
  leaf exactly as much as the water does, and 0 is the flat fall of every release before this one.
  It ships at 0.6. Where a leaf goes and how fast it falls are untouched. Only petals and leaves
  bend; sparks, motes and fireflies are points of light with no face for a wave to run along, and
  they are drawn exactly as they were.

- **Storms, and which way the weather falls.** Three settings in the weather page. A storm's
  rain is now thicker than plain rain, by a chosen amount (**Storm density**, 1.6 times by
  default, eased in and out with the storm itself); **Rain slant** sets how far the wind
  leans the rain and its streaks; and **Petal fall angle** does the same for the leaves and
  petals the wind carries, which used to share the rain's number.

- **The tuner shows what is chosen.** Every row of choices, the look presets, your saved
  looks, the quality presets, the water and the classic water's look, now draws the one in
  effect as a gold box with a dark rim. A saved look stays lit only while the live settings
  are still exactly what it holds. The quality presets remember which one was picked last.

- **Water is dithered before it is written.** A reflection is made of slow gradients, and eight
  bits cannot hold a slow gradient without steps: those steps were the colour banding reported
  on water, and a band edge is also where a surface flickers. One LSB of triangular
  interleaved-gradient noise at the end of the water pass, static across frames, for every
  look. It is correctness rather than a look, so it has no setting.

- **Ground foreshortening for people.** The solid projection below lays a person down at the
  ground's own flatness, and a person is a thin figure: at 0.58 a sixteen-texel sprite came out
  as a thread at dawn, thinner than the figure casting it reads. People, the player, other
  players and every NPC, now have their own number, in the settings menu and the tuner beside
  the general one. It ships at 1, which lays a person down at their full width, the way
  characters were drawn in every release before this one; lower it to bring them nearer to the
  trees. Farm animals are bulky and follow the general setting.

### Changed

- **A solid thing's shadow lies down the way a solid thing's shadow does.** Every shadow this
  mod casts is a sprite laid on the ground by the sun, and until now every sprite was laid down
  as if it were a flat card standing on its bottom edge: the card's width stays level on the
  screen and only its height leans away from the sun. That is exactly right for a fence, a gate
  or a sign, whose art is the object's one face. It is wrong for a bush, a tree, a crop or a
  person, which the sun sees from the side, not from where the camera stands. What lands on the
  ground behind a bush is the bush's silhouette lying ALONG the sun's direction with its width
  running across that direction, and the ground itself is seen at a slant, so that width is
  foreshortened the way everything lying on the ground is.

  Each caster is now laid down as what it is. Fences, gates, signs and props painted into the
  map keep the card projection. Everything that stands on a footprint, people and animals,
  trees, bushes, crops, grass, forage, machines and furniture, gets the solid one. The tip of
  every shadow lands where it always did, because that is the sun and it is the same for both,
  so nothing points a different way; what changes is the shape between the feet and the tip. A
  crop at a low sun used to be a wide smear lying across the light; it is now a shape lying
  along it, because that is the shape a crop's shadow has. Nothing is narrowed or squeezed to
  get there: the width is the sprite's own width, lying where the ground puts it.

  Decided by the game's own class for each thing, never by its sprite or its name, so anything
  a mod adds through those classes is laid down the same way.

  One new setting, **Ground foreshortening**: how much flatter than wide a circle drawn on the
  ground looks. 1 is a ground seen from straight above, where a sideways shadow stands on its
  edge, which is how characters were always drawn and why a dawn shadow could read as someone
  lying down rather than as a shadow. The default is 0.58, which is not a taste: the oval the
  game itself draws under every character is 12 texels wide and 7 tall, and 7 over 12 is the
  one statement the art makes about how flat its ground is.

- **Less garbage per frame.** An audit pass over the whole pipeline removed the small
  allocations that ran every frame: the twice-a-frame query for the bound render target, the
  reflection pass's two gather lists on every frame a creature is near water, and the tuner
  measuring every label's width on every frame it is open (it now measures each string once).
  None of it changes a pixel; it is work the garbage collector no longer has to clean up
  behind the mod.

### Fixed

- **The boat at Ginger Island, and the parrots over it, are not water.** A boat drawn at a dock
  had the ripple running over its hull, and a parrot flying past was warped with the sea below
  it. Neither is a tile, a building or a terrain feature: the location keeps them in fields of
  its own and paints them itself, so nothing the water could read knew they were there. Every
  location that draws something of its own is now watched while it does it, and whatever it
  paints keeps its own shape out of the effect. This covers Willy's boat as well, reported since
  1.3.0. A boat that a map places as ordinary tiles is a separate case and still needs a label:
  the pirate ships in East Scarp and a boat in Stardew Meadows are not covered yet.

- **A creature that swims below the surface no longer has a reflection on it.** A duck floats
  and must be mirrored; a jellyfish is under the water and must not be, and the reflection pass
  had no way to tell them apart, so modded sea life was reflected off the water it was inside.
  A creature that declares itself underwater is now believed and left out. Anything that says
  nothing keeps the reflection it always had.

- **The surf that runs up a beach no longer sways or reflects.** A shoreline crest is drawn by
  the map on its own frames, and the horizontal ripple was swinging it while the water below
  mirrored it back. The crests on the beach and island sheets are labelled as falling water,
  which takes no surface wave and mirrors nothing.

- **Window reflections come back on repainted buildings.** The art guard added in 1.6.1 stopped
  glass being reflected in the wrong place on a repainted sheet, but it could not put it in the
  right one, so a town running several art packs at once had quiet windows. Labels painted
  against those packs' own pictures now ship with the mod: on the tested profile, panes on
  screen at Pierre's went from 3 to 11 and the tiles the guard had to refuse from 63 to 1.

- **A creature's reflection is drawn by the creature, not guessed at.** A modded animal is
  usually a character that draws itself its own way, with its own origin, its own scale and its
  own offsets, and the mirror was rebuilding all of that from a collision box that knows none of
  it. The reflection came out beside the animal instead of under it, and stayed behind when it
  swam; it was reported about the ducks of SH's Wild Animals, and the sprite mask had already been
  fixed the same way in 1.6.1, so this is the half that was missed.

  Any character whose draw is its own is now asked to draw its own mirror. Where its body ends is
  read from that drawing rather than assumed, once per creature, sheet and animation frame, so the
  reflection turns over on the line where the animal meets the water. Measured at the forest pond,
  a duck's reflection sat 33 px below the duck before and touches it now.

  Nothing here names a mod: the test is whether the character draws itself, so a creature from a
  mod that does not exist yet comes out right for the same reason. Up to sixteen bodies near water
  at once, after which the rest fall back to the old built stamp and the log says how many.

  `radiance_reflect` now lists the characters near water and says, for each, whether its mirror is
  drawn or predicted.

  A creature can still sit a few pixels above its own reflection, and where it does, the reason is
  in its art: the mirror turns the image over on the lowest solid row the creature drew, and some
  sprites paint splash or feet below the line the eye reads as the waterline. There is no way to
  tell those apart from a tail or a pair of legs without guessing, and guessing there would cut the
  legs off somebody else, so the few pixels stay.

- **A farm animal's reflection is drawn by the animal too.** The fix above covered the creatures
  that come from mods and left the farm's own animals on the guessed path, where the mirror was
  rebuilt from a sheet rectangle at a fixed size. That path cannot see anything the animal decides
  for itself: a baby is drawn smaller than its frame, a duck in the water is drawn with its
  underside cut away and a splash beneath it, and an animal in a hat is wearing one. The
  reflections were the wrong size, and a paddling duck was mirrored as a whole duck standing on
  the pond. Every farm animal now draws its own mirror, on the same frame and by the same
  question the water mask has been asking them since 1.6.1, and its reflection turns over on the
  line where its body really ends. `radiance_reflect` lists the animals near water with the same
  drawn-or-predicted answer it gives for characters.

- **A reflection ends on a waterfall's painted lip, not on a line above it.** The falling
  face of a waterfall takes the mirror away from the water it covers, and it used to take it
  from every row the face touches across the whole tile, which at the top of a fall cut the
  stream's reflection off on a straight line a tile above the lip: the spray painted above
  the edge was enough to claim the rows under it. Each column is now read for what the fall
  does there. Where the face starts inside the tile the mirror runs down to the face's own
  first row, which is the painted edge; where the fall comes in from the tile above it is
  scrubbed from the top as before, and the water beside the fall at the foot is still the
  churn it always was. The foam at the foot keeps its painted bottom edge the same way.

### Removed

- **Lean clarity.** It squeezed the smallest shadows across the sun so that their direction
  would read. That was standing in for the geometry above: a seedling's shadow lies along the
  sun because that is where a seedling's shadow lies, not because it was narrowed, and with the
  projection right there is nothing left for the squeeze to do. Gone from the config file, the
  settings menu and the tuner; a `ShadowLeanClarity` line in an existing config.json is ignored
  and dropped the next time the file is saved.

### For translators

**The new water adds 49 keys and rewords 2.** The choice of water: `tuner.watermodel`,
`tuner.watermodel.modern`, `tuner.watermodel.classic`, `help.watermodel.modern`,
`help.watermodel.classic`, `config.water.model.name`, `config.water.model.tooltip`,
`config.water.model.modern` and `config.water.model.classic`; the two headings
`config.water.classic.title` and `.tooltip`, `config.water.modern.title` and `.tooltip`; and
for each of the 1.6.2 water's nine settings, `wobble`, `choppiness`, `parallax`, `fresnel`,
`stretch`, `edgesoftness`, `plungechurn`, `plungereach` and `lipfade`, the four keys `config.water.modern<name>.name`, `config.water.modern<name>.tooltip`,
`tuner.watermodern<name>` and `help.watermodern<name>`. The reworded two each gained one
sentence at the end, so an existing translation can be kept and added to:
`config.water.reflstyle.tooltip` now says it applies to the classic water only, and
`config.water.reflectdepth.tooltip` now names what 0.1 of the depth dial leaves, since the dial
reaches that far in this release. "Modern"
and "fresnel" are key names only: the player-facing words are "1.6.2 water" and "Depth fade",
and "Parallax" may be carried as the loan word or as "the image moves with the camera".
"Plunge" is the pool a waterfall lands in and "churn" is that water torn up and full of air;
the player-facing words are "Waterfall churn" and "Churn reach". The "lip" is the edge a
stream goes over to become the fall.

**The creature-reflection fix and the waterfall lip work add no keys.** They change what is drawn
and what `radiance_reflect` prints, and the console is not translated.

**Particles: 4 new keys.** `config.particles.petalsflutter.name`, `.tooltip`,
`tuner.particlepetalsflutter` and `help.particlepetalsflutter`. "Flutter" is the leaf bending as
it turns in the air, not the path it takes: the setting changes the shape only.

**Weather: 12 new keys.** For each of `stormdensity`, `rainslant` and `windslant`, the four
keys `config.precipitation.<name>.name`, `config.precipitation.<name>.tooltip`,
`tuner.precipitation<name>` and `help.precipitation<name>`. "Wind slant" is the angle the
petals and leaves fall at, not the wind's own direction: the player-facing word is "Petal
fall angle".

**Shadows: 8 new keys, 4 removed.** The removed ones are `config.shadows.leanclarity.name`,
`config.shadows.leanclarity.tooltip`, `tuner.shadowleanclarity` and `help.shadowleanclarity`.
The new ones take their places: `config.shadows.groundforeshortening.name` and `.tooltip`,
`tuner.shadowgroundforeshortening` and `help.shadowgroundforeshortening`, and the people's own
set beside them, `config.shadows.charactergroundforeshortening.name` and `.tooltip`,
`tuner.shadowcharactergroundforeshortening` and `help.shadowcharactergroundforeshortening`.
The word to carry is that this is about how flat the GROUND looks, not about the shadow's
length or darkness: a circle on the ground drawn as an oval, and how much shorter than wide that
oval is. The ground's own tooltip does not list a person among what it shapes; it points at the
people's setting instead.

## 1.6.1

### Added

- **A label is only used on the art it was painted on.** This mod ships hand-painted labels
  saying which tile of which tilesheet is water, glass, a mirror, a roof, and those labels are
  painted on one picture. An art pack can replace that picture and leave the tile where it was,
  and the label goes on describing art that is not there any more. Reported twice as bright
  rectangles around doors and windows on buildings that have neither: the pane the label knew
  about had been painted over by a building pack, and a reflection was still being drawn in it.

  Before a glass label is used now, the picture the game is actually drawing is compared against
  a fingerprint of the picture the label was painted on, and where the two disagree the glass is
  taken back out.

  Glass and nothing else, which was decided on a measurement rather than out of caution. Taking a
  single recolour out of an otherwise identical profile changed the art under 11,216 of 20,202
  labelled tiles, and 4,703 of those carry LIQUID labels, which is 82% of every liquid label that
  ships. Liquid labels are what correct the water colour gate, so refusing them because a recolour
  is installed would bring the rectangles-around-water reports straight back. A refused glass
  label is a quiet pane and nothing else. A tile whose label carries no glass is never
  fingerprinted at all, so the ordinary case costs nothing.

  Two kinds of label are kept whatever the art says. One is the blank label, which adds nothing
  and can only take something away: those are the snow vetoes that stop Four Corners reading as
  water in winter, and dropping one would put an effect back rather than remove one. The other is
  a label on art that cannot be read at all, because there is no reading to disagree with.

  Three passes ship: the base game's own art, and two over the author's map mods. On art with no
  fingerprint you lose window reflections on those sheets and nothing else changes.

- **A label can be painted for art this mod does not ship against.** Where a pack repaints a
  window without moving it, a label painted on that pack's picture can be tied to it and used
  outright wherever that art is loaded, in preference to the guard: somebody looked at this
  picture and said where its glass is, which is better evidence than a hash saying the shipped
  label does not apply. Several fingerprints may share one painted label, because a pack with
  four palettes draws the same window four times over. For one town sheet that is the difference
  between painting 78 tiles and painting 312.

  No variants ship in this release. The machinery is here so that a pack's own labels can be
  added later without a code change, and `radiance_report` names any variant that matched,
  because a variant that never matches looks exactly like one that was never installed.

- **The bundled labels carry a week of painting.** The pack had not been rebuilt since 14 August
  and 69 sheets that had been painted were not in it at all. It goes from 128 sheets to 197 and
  from 29,358 tiles to 42,881, most of it water and windows on maps from other mods.

- **Shadow length and softness, per kind of thing.** The ceilings that decide how far a shadow may
  reach were always set per kind, because a tree may not reach as far as a person does: its canopy
  is drawn well above the trunk that actually casts, so the full sun would tear the shadow off its
  own tree. Those ceilings were constants, though, which meant a player who wanted their shadows
  back the way an earlier version drew them had nothing to turn. They are settings now, six of
  them, for trees, saplings and stumps, bushes, crops, grass, and forage/fences/machines. The
  overall **Shadow length** slider still multiplies all six, so nothing has to be touched to make
  everything shorter at once.

  Alongside them, six softness multipliers on the overall **Edge softness**, which did not exist
  before: the blur was one number for everything on the screen. A blur radius is measured in
  pixels, so the same number is a soft edge on a short shadow and a hard one on a long shadow, and
  short things generally want more of it than tall ones.

- **A shadow narrows across the sun, so its lean is the thing you see.** A shadow's tip has
  always landed at the sun's angle, and for a person that is what you read, because a person leans
  further than they are wide. A crop is about as wide as it is tall, so the same lean moves its top
  by less than its own width and the shadow comes out as a flat smear lying ACROSS the sun rather
  than along it, at the same tip angle as everything else. Making crop shadows shorter made that
  worse, because the lean shrank and the plant's width did not.

  The arithmetic is blunt about this: a shadow's bounding box can sit at the sun's own angle only
  when the shadow has no width at all. A person's shadow looks right not because its box matches
  but because it is a long thin bar, and a bar carries its direction in its own shape. So the test
  is that shape: how far a shadow reaches compared with its own width.

  This applies to the smallest casters only, a single tile of sprite or less, which in practice
  means seeds, sprouts and saplings. Their whole shadow is a handful of pixels and its own width
  is enough to hide the angle. Everything bigger is left exactly as it was, because a stump, a
  crop, a bush or a canopy already casts a shape you can read and narrowing it only makes it
  worse. At noon nothing is narrowed at all, since there is no direction to show. One slider,
  **Lean clarity**, from off to full.

- **Lean, per kind of thing.** How far a shadow leans away from its caster, as a fraction of the
  sun's own angle, for the same six kinds. Everything defaults to 1, which is the sun itself and
  is the only setting at which a shadow points where the light says it should.

  It exists because length and lean are not interchangeable and only one of them was reachable.
  The ceiling decides how FAR a shadow reaches; the lean decides its SHAPE. At six in the morning
  a crop capped at 0.55 lands its tip 9.9 pixels sideways and 4.8 down at full lean, and 6.8 by
  8.6 at 0.6. Same ceiling, and only the second one reads as a plant standing on soil rather than
  hovering beside its own shadow. Nothing about the length could produce the second picture.

  Set below 1 a caster no longer agrees with the sun, and the people standing next to it will
  point somewhere else. That is a real cost and it is why the default is 1.

- **How deep a reflection reaches into the water is a setting, and so is how much of the scene
  reflects at all.** The depth bound ran 5 to 9 tiles through 1.5.3. It was raised to 9 to 16 when
  the mirror learned to read twelve tiles above the frame, because until then the middle of any
  river or lake carried no reflection and read as flat paint. That is right for open water, where
  a cliff really is that tall, and long for a stream a tile or two across, where the mirrored bank
  runs on for more water than there is.

  One dial moves both halves of the bound together, the general one and the shallower one that
  applies when the mirrored source is itself water. Moving only the first would let a river's own
  surface out-reach the bank above it, which is the streaking the two were balanced against. 1 is
  the shipped depth; about 0.55 is 1.5.3's.

  Reflection **reach**, which decides how much of the scene is mirrored at all, has been in the
  config file since 1.5.6 and in no menu. It is in both now. A setting nobody can find is a
  setting that does not exist.

### Changed

- **A tuner control that cannot do anything now looks like it.** Every slider sat at full
  strength whether or not the thing it belongs to was switched on. Untick shadows and the eight
  shadow sliders stayed bright and draggable: the value moved, nothing on the screen did, and the
  only way to find that out was to try it and wonder what you had missed. Rows dim when what they
  need is off, and they refuse the mouse as well, so a drag cannot start on one and a click
  cannot flip it.

  Dimmed rather than hidden, deliberately. Hiding re-flows everything below it on every toggle,
  and a list that jumps under your hand while you are using it is harder to work with than one
  that greys a row where it stands.

- **Crop and sapling shadows go back to roughly the length they had in 1.5.3.** Two changes in
  1.5.4 pushed the same way without either knowing about the other. One raised the crop ceiling
  from 0.55 to 1.0 so a tall dead plant's shadow would clear the plant instead of landing on it.
  The other stopped damping the lean of every short caster to 0.6 of the sun's angle, which on its
  own widened their sideways reach by about half, and that was the thing the raised ceiling had
  been meant to fix. Together they over-shot, and dense planting came out as a field of long
  parallel diagonals. Crops go back to 0.55 and saplings to 0.52, which with the un-damped lean
  still reaches further sideways than 1.5.3 ever managed, and both default to 1.6x the overall
  edge softness. Every one of these is a slider now, so the longer look is one drag away.

### Fixed

- **The water effect ran over the palm fronds around the desert oasis.** Reported as a tree
  being drawn over by water, and asked about as whether the tree came from another mod. It did
  not, and it never had to. The game picks between three columns of the same rectangle when it
  draws a tree's canopy: one for a tree carrying seed or one not yet shaken today, one for a
  mossy tree, and one for everything else. Three places here drew a tree and all three took the
  first column unconditionally: its shadow, its reflection, and the stencil that keeps the water
  effect off it. A desert palm holding a coconut is exactly the first case, so it was being
  stencilled with the shape of a palm holding nothing and the water ran over the fronds the wrong
  shape left uncovered. Its shadow and its reflection were the wrong shape too, quietly, wherever
  the same conditions held, which means a mossy tree has cast the wrong shadow since the game
  added moss. One helper now answers the question the game answers, and all three ask it.

- **Animals from companion mods rippled like the water they were standing in.** Reported twice
  about the aquatic animals in a Custom Companions pack. A companion is a villager that draws
  itself with its own origin, its own rotation and a scale taken per animal from its own model.
  The water stencil was rebuilding all of that from a bounding box and a source rectangle, which
  knows about none of it, so the shape landed near the animal rather than on it and the ripple
  ran across whatever it missed. Villagers and farm animals are asked to draw themselves into the
  stencil now, exactly as the small wildlife already was, with the old hand-built shape kept only
  for when that fails. A villager comes out the same either way; anything that positions itself
  differently only comes out right this way.

- **`radiance_shadows` reported numbers the renderer had stopped using.** The geometry table
  printed a crop ceiling of 0.55 and an object ceiling of 0.5 for two releases after the draw pass
  moved both to 1.0, because the values were copied into the diagnostic by hand. The one tool for
  answering "why is that shadow that long" was describing code that no longer ran. It and the draw
  pass now read the same settings, and it prints the softness multiplier as well.

- **Silhouettes baked on arrival in a location took the wrong edge.** The full bake that runs when
  you walk into a new map passed a blur of zero and got away with it only because the bake read a
  separate copy of the setting. Found while making the blur per-kind, which would have turned it
  into a screen of crisp-edged shadows for as long as those bakes lived.

### Diagnostics

- `radiance_report` says how many labels were refused because the art under them had changed,
  which sheet they were on, and which installed content packs declare that they repaint that
  sheet, read from each pack's own manifest. "My reflections are missing" and "I am running an
  art mod this has no labels for" are the same sentence, and nobody should have to work that out
  unaided. Names never decide anything: what is drawn is settled by the fingerprint. They are
  there to explain the decision.
- `radiance_artfingerprint <name>` takes a fingerprint pass over the art currently loaded and
  writes it out, which is how the shipped passes were made and how a variant for someone else's
  art can be made.

### Translations

- **Chinese is complete for 1.6.0**, 557 of 557 keys, sent in by Rime961. No key the mod uses
  falls back to English and nothing is a copy of the English. The 58 keys 1.6.1 adds are not
  translated yet and fall back until they are.

### Known issues

- **Reflections at the beach can sit away from what casts them.** Reported with the shore in
  view, where a reflection follows the player but not to the place the player is standing. The
  shore is the one piece of water whose edge is a slope rather than a line, and the reflection
  is anchored on feet, so the two disagree by however far the slope runs. Not diagnosed further
  than that yet.

- **A fish pond, or a pond built on the farm, reflects in pieces.** Reported as reflections that
  are cut off partway. A built pond is not map art and carries no painted label, so what it is
  has to be worked out from the object rather than looked up, and that path does not yet cover
  the whole of one.

- **The water surface can flicker, and show bands of colour.** Reported against 1.6.0 with the
  reflection on, and since seen here as well, so it is a real thing and not a machine of its own.
  What causes it has not been worked out yet and no attempt at it is in this release.

- **A mossy tree can still be invisible in winter.** Unchanged from 1.6.0 and unchanged in what
  is known about it: the game asks for a sprite column past the right edge of the winter tree
  sheet and the card returns a transparent edge pixel. The canopy fix in this release makes the
  shadow and the reflection of a mossy tree the right shape, which is a different half of the
  same sheet layout and does not make the tree come back.

### For translators

**58 new keys, nothing removed, nothing whose meaning changed.** They are two groups of the same
shape, one heading plus six labels each, and the six labels are the same six words in both groups.

- **Length per kind** (`config.shadows.perkind.title`, `config.shadows.perkind.tooltip`,
  `config.shadows.length.trees.name`, `.smalltrees.name`, `.bushes.name`, `.crops.name`,
  `.grass.name`, `.objects.name`)
- **Softness per kind** (`config.shadows.softness.title`, `config.shadows.softness.tooltip`,
  `config.shadows.softness.trees.name`, `.smalltrees.name`, `.bushes.name`, `.crops.name`,
  `.grass.name`, `.objects.name`)
- **The same controls on the F6 tuner** (`tuner.shadowperkind`, `tuner.shadowsoftperkind`,
  `tuner.shadowlength.trees` through `.objects`, `tuner.shadowsoftness.trees` through `.objects`).
  These are the short forms: the tuner column is narrower than the settings menu, so
  `config.shadows.length.objects.name` reads "Forage, fences & machines" while
  `tuner.shadowlength.objects` is just "Forage & machines". Both may be shortened further if your
  language needs the room; neither is used anywhere else.

- **Lean clarity** (`config.shadows.leanclarity.name`, `config.shadows.leanclarity.tooltip`,
  `tuner.shadowleanclarity`, `help.shadowleanclarity`). The word to carry is that this is about
  what the eye reads, not about the angle being wrong: the angle is already the same for
  everything, and this makes short wide things show it.

- **Lean per kind** (`config.shadows.lean.title`, `config.shadows.lean.tooltip`,
  `config.shadows.lean.trees.name` through `.objects.name`, `tuner.shadowleanperkind`,
  `tuner.shadowlean.trees` through `.objects`, `help.shadowlean`). The six labels are the same
  six words the other two groups use.

- **Water** (`config.water.reflectdepth.name` and `.tooltip`, `config.water.reflectreach.name`
  and `.tooltip`, `tuner.reflectdepth`, `tuner.reflectreach`, `help.reflectdepth`,
  `help.reflectreach`). Depth is how far DOWN a reflection carries; reach is how much of the
  scene reflects at all. Two different questions that both sound like "how much reflection", so
  the two need to read differently in your language as well.

`smalltrees` means saplings, seedlings, bush-stage growth and stumps, which the game draws as
trees but which are short. `objects` covers anything standing on its own tile at its own height:
forage on the ground, fences, signs, torches, kegs, machines.

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
