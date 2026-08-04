# Mod Compatibility Notes

Conflict log from live testing SDV-Radiance alongside other mods. Each entry records
the observed behavior, the cause if known, and the recommended setting. This file
feeds the compatibility section of the Nexus page.

Status legend: **OK** works together · **PARTIAL** works with settings tweaks ·
**CONFLICT** visual/functional clash · **UNTESTED** installed, not yet verified.

## Rendering / lighting mods

| Mod | Status | Notes |
|-----|--------|-------|
| Global God Rays | OK | Confirmed by multiple users over several in-game days (2026-07-21). Radiance hugs in-world lights, GGR does the sun shafts — they stack nicely. Listed as compatible on the Nexus page. |
| Dynamic Reflections (4.0.1) | PARTIAL | User-confirmed working alongside Radiance after tuning (2026-07-22). Two reflection systems still overlap conceptually — recommend keeping only one water/reflection system on for a predictable look (Radiance's Water toggle can be switched off). |
| SpriteMaster / Clear Glasses / Clear Monocle | CONFLICT | Confirmed (2026-07-21/22): world renders solid orange/black on load until a menu forces a redraw; Clear Monocle (xBRZ-only fork) fails the same way. No reliable workaround (Dynamic Shader trick reported, then retracted by users). Documented as incompatible on the Nexus page; built-in upscaling is on the roadmap to remove the need for it. |

## Content / map mods

| Mod | Status | Notes |
|-----|--------|-------|
| Stardew Valley Expanded (1.15.11) | OK | Daily-driven in the author's ~80-mod profile (SVE + CJB + Fashion Sense + more). Some SVE custom water bodies are still missed by the mask — tracked as the water-accuracy known issue, not an SVE-specific clash. |
| Content Patcher recolors | UNTESTED | Recolored water/terrain stresses the water shader's blueness/greyness color gates. Deliberately not tested — noted here so the Nexus page can say so; if a recolor shifts water hue far from vanilla, the reflection/ripple gates may miss some tiles. |

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
