# Changelog

All notable changes to SDV-Radiance. Older releases are documented on the Nexus page.

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
