# Credits & third-party attribution

SDV-Radiance is MIT-licensed. It builds on the work of the Stardew Valley
modding community. Where another project's code or approach is reused, it is
listed here with its license.

## Frameworks / tooling
- **SMAPI** by Pathoschild, the modding API. https://smapi.io
- **MonoGame**, the rendering framework. https://www.monogame.net
- **Generic Mod Config Menu** by spacechase0, the in-game config UI (optional dependency). https://www.nexusmods.com/stardewvalley/mods/5098

## Translations
The mod's text changes often while it is still settling, so a translation is not
a one-off gift: every one of these has been resynced against a moving key set. A key
with no translation falls back to English rather than breaking anything, so a language
is never held back waiting for the last few.

Bundled with the mod:
- **Simplified Chinese** by Rime961, who looked after it from 1.2.2 through 2.1.3 and resynced it
  against a moving key set every release in between, more than once before being asked. The
  choice of 贴图 for "sprite" across the whole file was theirs too, made after 2.1.3. The two
  camera lines were corrected by passersby10086 in 2.1.3.
- **Thai** by the author.

Contributed, not bundled:
- **Italian** by 7Kana, sent in during 1.2.1 and covering 193 keys, which was most of the mod
  at the time. It is not bundled because the key set has grown to over eight hundred since, so
  shipping it now would give an Italian player one screen in their language and the rest in
  English. It is kept on the `i18n/italian` branch, and the offer to finish it stands with my
  thanks either way.

Published separately on the Nexus by their authors, with thanks. Six of them now, and every
one was made and uploaded without being asked:
- **French** by Deovos. https://www.nexusmods.com/stardewvalley/mods/50089
- **German** by Neko41. https://www.nexusmods.com/stardewvalley/mods/51482
- **Japanese** by tanakakaku3i. https://www.nexusmods.com/stardewvalley/mods/49750
- **Korean** by jjongleee. https://www.nexusmods.com/stardewvalley/mods/49448
- **Mandarin** by Rubbish404. https://www.nexusmods.com/stardewvalley/mods/49647
- **Spanish** by Papaya2. https://www.nexusmods.com/stardewvalley/mods/51510

## Reference / inspiration (features reimplemented independently)
- **DynamicShader** by Nook. Cloud shadows and tilt-shift are reimplemented from scratch; no code or assets copied. https://www.nexusmods.com/stardewvalley/mods/40775

## Texture upscaling: written here, nothing ported

The doubled sheets and the softer look are this project's own code. Two projects were read as
reference while working out what a sprite upscaler has to get right, and no code or asset from
either was copied; the section is kept so the debt is on the record.

- **Stardew-SpriteMaster** by aurpine, and the original **SpriteMaster** by Ameisen, MIT-licensed.
  - https://github.com/aurpine/Stardew-SpriteMaster
  - https://github.com/ameisen/SpriteMaster

The rules themselves are published algorithms implemented from their descriptions: EPX/Scale2x,
and an xBR-style edge rule for the softer look. Third-party code ported into this mod is listed
below with its copyright notice, as its license requires.

### MMPX (ported)

`SheetMmpxPS` in `shaders/sheetscale.fx` is a port of the MMPX reference implementation, one of
the rules the soft look can be made with. The "edges kept" variant's extra conditions are this
project's own.

- Morgan McGuire and Mara Gagiu, "MMPX Style-Preserving Pixel Art Magnification", Journal of
  Computer Graphics Techniques 10(2), 2021. https://casual-effects.com/research/McGuire2021PixelArt/

```
Copyright 2020 Morgan McGuire & Mara Gagiu.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---
*If you believe your work is used here without proper attribution, please open an issue.*
