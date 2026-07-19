# Mod Compatibility Notes

Conflict log from live testing SDV-Radiance alongside other mods. Each entry records
the observed behavior, the cause if known, and the recommended setting. This file
feeds the compatibility section of the Nexus page.

Status legend: **OK** works together · **PARTIAL** works with settings tweaks ·
**CONFLICT** visual/functional clash · **UNTESTED** installed, not yet verified.

## Rendering / lighting mods

| Mod | Status | Notes |
|-----|--------|-------|
| Dynamic Reflections (4.0.1) | UNTESTED | Competing water-reflection system (sprite-flip per entity vs our whole-scene screen-space mirror). Expect doubled reflections when both enabled — likely recommendation: disable one of the two water systems. |
| SpriteMaster / Clear Glasses | UNTESTED | Texture upscaler; verify RT-baked shadows still key correctly off upscaled textures. |

## Content / map mods

| Mod | Status | Notes |
|-----|--------|-------|
| Stardew Valley Expanded (1.15.11) | UNTESTED | Custom maps stress tile-art shadow classification, water masks, and flood GI on non-vanilla layouts. |
| Content Patcher recolors | UNTESTED | Recolored water/terrain stresses the water shader's blueness/greyness color gates. Not yet installed (Nexus download needed). |

## Cosmetic mods

| Mod | Status | Notes |
|-----|--------|-------|
| Fashion Sense + packs | OK | Player silhouette is RT-baked from the final composed sprite, so custom outfits/hair reflect and cast shadows correctly. |
| Anime portrait/body CP packs | OK | Portrait-only changes; no rendering interaction. |

## How to test a conflict

1. Enable the suspect mod (remove the leading `.` from its folder in `Mods/`).
2. Walk the relevant checklist section (water, lighting, shadows).
3. Record: screenshot, location, time of day, both mods' settings.
4. Update the row here with OK / PARTIAL / CONFLICT + the recommended config.
