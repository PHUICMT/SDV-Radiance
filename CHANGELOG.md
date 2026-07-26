# Changelog

All notable changes to SDV-Radiance. Older releases are documented on the Nexus page.

## 1.2.2

### Fixed
- Beaches no longer show rectangular blocks of water effect over the sand. The mask covers a wave tile as a square, but the sand painted inside it is not, and a floor on the colour test meant that sand still took most of the ripple and reflection. Pixels that are clearly dry land (warm and saturated: sand, tilled soil, dirt paths, bare wood) are now excluded. This also stops the effect spilling onto sand as a wave retreats, ripple appearing over a crop field, and tree shadows reflecting onto the beach instead of the water. Lava is exempt, and murky green or grey water keeps its effect.

### Changed
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
