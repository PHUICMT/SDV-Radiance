# Mod Compatibility Notes

Conflict log from live testing SDV-Radiance alongside other mods. Each entry records
the observed behavior, the cause if known, and the recommended setting. This file
feeds the compatibility section of the Nexus page.

Status legend: **OK** works together · **PARTIAL** works with settings tweaks ·
**CONFLICT** visual/functional clash · **UNTESTED** installed, not yet verified.

## Rendering / lighting mods

| Mod | Status | Notes |
|-----|--------|-------|
| Global God Rays | OK | Confirmed by multiple users over several in-game days (2026-07-21). Radiance hugs in-world lights, GGR does the sun shafts, so they stack nicely. Listed as compatible on the Nexus page. As of 1.2.1, when Radiance hides the vanilla drifting clouds it removes them fully, so GGR no longer fades its rays under a cloud shadow that is no longer drawn. |
| Dynamic Reflections (4.0.1) | PARTIAL | User-confirmed working alongside Radiance after tuning (2026-07-22). Two reflection systems still overlap conceptually — recommend keeping only one water/reflection system on for a predictable look (Radiance's Water toggle can be switched off). |
| Dynamic Windows | OK | User-confirmed (Es0terick, 2026-07-24). Radiance's lighting makes the window light effect pop, and the shadow tracks the added light source nicely. They stack well. |
| NightShade | UNTESTED | Asked about (truelyblue, 2026-07-24). Both are client-side post-processing layers, so they would run on top of each other — expect the two color/lighting passes to compound. Should not break anything; tune one down if the look doubles up. |
| Clear Monocle | OK | **Fixed by its own author.** ThaleTheGreat shipped explicit Radiance support on 2026-07-28; two users confirmed it (one had been getting a blank light-blue screen on entering a cutscene, and reports it works beautifully after the update). Make sure Clear Monocle is up to date. |
| SpriteMaster / Clear Glasses / Clear Glasses HD | CONFLICT | Confirmed (2026-07-21/22): world renders solid orange/black on load until a menu forces a redraw. No reliable workaround (Dynamic Shader trick reported, then retracted by users). A user on Clear Glasses HD (2026-07-24) also reported the screen zooming in hard or blacking out when walking near god rays; turning off the God rays effect alone avoided it without disabling the whole mod. Documented as incompatible on the Nexus page; built-in upscaling is on the roadmap to remove the need for it. |

## Content / map mods

| Mod | Status | Notes |
|-----|--------|-------|
| Stardew Valley Expanded (1.15.11) | OK | Daily-driven in the author's ~80-mod profile (SVE + CJB + Fashion Sense + more). Some SVE custom water bodies are still missed by the mask — tracked as the water-accuracy known issue, not an SVE-specific clash. |
| Cape Stardew | PARTIAL | Reported (2026-07-25) as showing rectangles on the beach. Part of the wider beach/shoreline artefact several users hit in 1.2.1, not unique to this map; tracked with the water-accuracy work. |
| Custom farm maps with bridges (e.g. Kisaa's Mystwood Homestead) | PARTIAL | Reported (2026-07-24): a rectangle appears on bridge planks where a modded bridge spans water. Bridges should read as land over water; modded bridge art currently misses that carve-out. |
| Content Patcher recolors | PARTIAL | Recolored water/terrain stresses the water shader's blueness/greyness color gates. Two 2026-07 reports point at this: colors reading washed out with a recolor installed, and ripple appearing over a crop field (watered tilled soil is blue-tinted and may pass the water gate). Tracked as the water-accuracy work; a recolor that shifts water hue far from vanilla can also make the mask miss tiles. |
| ReShade / NightShade / Natural ReShade | PARTIAL | Both are post-processing layers, so their color passes stack with Radiance's. Reported (2026-07-25) as looking washed out with Radiance colour grading on top of Natural ReShade. Nothing breaks; turn Radiance's colour grading off, or lower one side, if the look doubles up. |

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
