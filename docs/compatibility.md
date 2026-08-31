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
| Any mod that draws its own weather | OK by design | Radiance replaces the game's rain, snow and windblown debris by patching the same draw. If another mod has already claimed that draw, or if ours throws once, Radiance stands down for the rest of the session and leaves the game's own weather alone rather than fighting for the slot. `radiance_report` says which of the two is drawing, on the precipitation line. The replacement also has its own switch, so keeping the other mod's weather and everything else of ours needs no special handling. |
| SpriteMaster / Clear Glasses / Clear Glasses HD | CONFLICT | **Two separate symptoms, both confirmed by A/B.** (1) 2026-07-21/22: world renders solid orange/black on load until a menu forces a redraw. (2) 2026-08-11: patches of the water surface lose their mirror entirely while you walk, a block at a time, and the water flickers as the camera scrolls. Removing Clear Glasses fixes it outright; nothing in Radiance's own settings does. No reliable workaround (Dynamic Shader trick reported, then retracted by users). A user on Clear Glasses HD (2026-07-24) also reported the screen zooming in hard or blacking out when walking near god rays; turning off the God rays effect alone avoided it without disabling the whole mod. Documented as incompatible on the Nexus page; built-in upscaling is on the roadmap to remove the need for it. |

## Content / map mods

| Mod | Status | Notes |
|-----|--------|-------|
| Stardew Valley Expanded (1.15.11) | OK | Daily-driven in the author's ~80-mod profile (SVE + CJB + Fashion Sense + more). Some SVE custom water bodies are still missed by the mask — tracked as the water-accuracy known issue, not an SVE-specific clash. |
| Cape Stardew | PARTIAL | Reported (2026-07-25) as showing rectangles on the beach. Part of the wider beach/shoreline artefact several users hit in 1.2.1, not unique to this map; tracked with the water-accuracy work. |
| Custom farm maps with bridges (e.g. Kisaa's Mystwood Homestead) | PARTIAL | Reported (2026-07-24): a rectangle appears on bridge planks where a modded bridge spans water. Bridges should read as land over water; modded bridge art currently misses that carve-out. **Improved 2026-08-20:** only opaque pixels of a bridge are carved now, so the plank keeps its own colour out of the water, and the ripple fades out as it approaches anything solid instead of deciding per pixel whether to move, which is what produced the crawling outline along an edge. What is still map-dependent is whether the water carries underneath a given bridge at all. |
| Mods that repaint a vanilla tilesheet in place (Elle's Town Buildings, most recolours) | PARTIAL | Radiance's hand-painted labels attach to a tilesheet NAME and a tile index, and a Content Patcher `EditImage` keeps both while replacing what is under them. Elle's Town Buildings writes 43 patches into `Maps/{{season}}_town`, which lands on 92 of the 151 labelled tiles there and on 78 of the 86 that carry a window, so the reflection appeared where the base game's window used to be. Reported 2026-08-20 as window reflections being applied to the vanilla buildings and not matching up. Radiance now fingerprints the art behind each labelled tile and only uses a label's GLASS where the art is the art it was painted on, so a repainted building shows no reflection rather than one in the wrong place; `radiance_report` says how many tiles that affected on your setup. Liquid labels are deliberately left unguarded: measured on the author's machine, taking one recolour out of an otherwise identical 103-mod profile changed the art under 11,216 of 20,202 labelled tiles, and dropping their liquid labels too would hand the water mask back to colour alone, which is where the rectangles-around-water reports came from. |
| Content Patcher recolors | PARTIAL | Recolored water/terrain stresses the water shader's blueness/greyness color gates. Two 2026-07 reports point at this: colors reading washed out with a recolor installed, and ripple appearing over a crop field (watered tilled soil is blue-tinted and may pass the water gate). Tracked as the water-accuracy work; a recolor that shifts water hue far from vanilla can also make the mask miss tiles. |
| ReShade / NightShade / Natural ReShade | PARTIAL | Both are post-processing layers, so their color passes stack with Radiance's. Reported (2026-07-25) as looking washed out with Radiance colour grading on top of Natural ReShade. Nothing breaks; turn Radiance's colour grading off, or lower one side, if the look doubles up. |

## Cosmetic mods

| Mod | Status | Notes |
|-----|--------|-------|
| Fashion Sense + packs | OK | Player silhouette is RT-baked from the final composed sprite, so custom outfits/hair reflect and cast shadows correctly. |
| Anime portrait/body CP packs | OK | Portrait-only changes; no rendering interaction. |

## Creature and character mods

Added 2026-08-31 from the 1.6.1 to 1.7.0 report round. Everything here was our fault rather than
the other mod's, except where the row says otherwise.

| Mod | Status | Notes |
|-----|--------|-------|
| Custom Companions (framework) and its packs | OK **since 1.7.0** | A creature a companion or wildlife mod adds got no shadow at all, and the round blob its own framework hides stayed hidden, so the creature had nothing under it. Reported by two people before it was looked at properly. Fixed `88aaa2f`, shipped 1.7.0. Custom Companions 5.1.0 is installed in the author's profile and reproduces it, but **the fix has still never been checked there by eye**. |
| SH's Wild Animals | OK **since 1.7.0** | Two separate faults, both ours. The ducks wobbled with the ripples until 1.6.1, when creatures began drawing themselves into the water mask (Kaishale, Whystardewvalley, ghi3038). The shadows were missing entirely until 1.7.0; it is a Custom Companions pack, so it is the row above (Batcers, 28/8). |
| Em's Horses | OK **since 1.7.0** | A horse has one set of frames and the game mirrors them, so a silhouette cut from the source rectangle and drawn unmirrored was a horse facing backwards, head where the tail is. It also stood up on edge beside the horse instead of lying down. Both fixed in 1.7.0, along with pets and anything else built low and wide. Batcers' screenshots of "sprites look weird" draw the horse identically in the shot he called broken and the one he called fine, so that half is still an open question to him. |
| Scale Up Unofficial (HD sprites) | PARTIAL | Reported 2026-08-17 (Glarthon): shadows sit offset from the sprite. Known class rather than a bug: a mod that draws sprites at a different scale moves the feet anchor every shadow hangs from, and the anchor is read from the game's own placement. No patch planned. |
| Controller Zoom | UNTESTED, suspected | The long-standing "black screen / zoomed in hard" report (razegaming00000, 1.2.1) is blamed on Clear Monocle and SpriteMaster on the Nexus page, but N1Zenma reproduced it on 2026-08-14 only with Controller Zoom installed. Needs one confirmation that Controller Zoom alone does it, then the known-issue text on the description page is wrong and should be rewritten. |
| Alternative Textures | OK | A crash reported after **uninstalling** Radiance traced to Alternative Textures and Harmony, not to anything of ours. |

## How to test a conflict

1. Enable the suspect mod (remove the leading `.` from its folder in `Mods/`).
2. Walk the relevant checklist section (water, lighting, shadows).
3. Record: screenshot, location, time of day, both mods' settings.
4. Update the row here with OK / PARTIAL / CONFLICT + the recommended config.
