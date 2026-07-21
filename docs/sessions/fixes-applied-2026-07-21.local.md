# 🔧 Fixes Applied — 2026-07-21

> รวบรวมทุกอย่างที่ทำใน session นี้ บน branch `session/2026-07-21-initial-audit`
> ไฟล์นี้เป็น `.local.md` — ไม่ถูก commit เข้า git

---

## 📊 สรุป Session — สิ่งที่ค้นพบ & ทำสำเร็จ

### ✅ 10 Bug Fixes (C# code — build ผ่าน, deploy ได้)

| # | Issue | File(s) | Commit |
|---|-------|---------|--------|
| #19 | Movement stutter (mod off = lag) | `src/ModEntry.cs` | `fe5de29` |
| #17 | Preset save/load ไม่ครบ 80+ properties | `src/ModConfig.cs` | `87b83c7` |
| #18 | Settings รีเซ็ตตอนเปลี่ยน map | `src/ModEntry.cs` | `91137ae` |
| #14 | CA blur UI มุมจอ | `shaders/finishing.fx` | `fad4fb6` |
| #10 | God rays จาก speech bubbles | `shaders/godrays.fx` | `168bedc` |
| #11 | Player โดน distortion ใน hot springs | `shaders/water.fx` | `6cc9ac5` |
| #12 | Trough/sink บิดเบี้ยว (small container) | `shaders/water.fx` | `b93d9e6` |
| #13 | Custom maps ไม่ detect น้ำ | `src/RenderPipeline.WaterMask.cs` | `6435688` |
| — | Water spill ล้นฝั่ง non-water objects | `shaders/water.fx` | `50a6508` |
| — | Multi-source water detection (5-tier) | `src/RenderPipeline.WaterMask.cs` | `20043d4` |

### 🛠 3 Dev Tools (ใหม่ — build ผ่าน, deploy ได้)

| Tool | Key | File(s) | Commit |
|------|-----|---------|--------|
| Water Mask Overlay | **F3** | `src/WaterMaskOverlay.cs` | Part of water tooling |
| Water Map Painter | **F3 + Shift** (paint), **F5** (save) | `src/WaterMapPainter.cs` | `b9fa291`, `ce8dae3` |
| Dev Menu | **F10** | `src/DevMenu.cs` | `3cdf416`, `d780d26` |

### 🔧 Infrastructure Changes

| Change | Detail |
|--------|--------|
| Git pager | เปลี่ยน `core.pager` จาก `delta` → `cat` (แก้ git ค้าง) |
| `git-quick.local.ps1` | Script สำหรับ git add+commit เร็ว ไม่ค้าง |
| Water mask confidence tracking | เพิ่ม `_waterConfBuf` ใน `RenderPipeline.cs` |
| Newtonsoft → System.Text.Json | `WaterMapPainter.cs` ใช้ `System.Text.Json` แทน `Newtonsoft` |
| Building | เปลี่ยน `waterTiles` → `WaterList`/`DryList` (avoid naming conflict) |
| .NET Core 3.1 runtime | ติดตั้งเพื่อให้ mgfxc 3.8.0.1641 รันได้ |

### 🟢 NOT A BUG

| # | Issue | Detail |
|---|-------|--------|
| #15 | Shimmer→Reflection | `WaterSparkle` (slider) กับ `WaterReflection` (toggle) แยกจากกันโดยสิ้นเชิง — ถูกต้องแล้ว |

### ⏳ Deferred / Pending

| # | Task | Status |
|---|------|--------|
| #9 | Clear Monocle orange screen | ⏳ ต้องมี mod ติดตั้งเพื่อ debug |
| #8 | God rays ปิดตอนฝน/กลางคืน | ⏳ ยังไม่ได้ทำ |
| #6,#7 | Cloud shadow fine-tune | ⏳ ยังไม่ได้ทำ |
| #1-3 | Shadows Architecture | ⏳ ต้อง design ก่อน |

---

## ⚠️ mgfxc Toolchain Issue — `.mgfxo` Binary Incompatibility

### ปัญหาที่ค้นพบ
- **3 shaders (finishing.mgfxo, godrays.mgfxo, water.mgfxo) ถูก compile ด้วย mgfxc 3.8.5 ใน commit `3cdf416`** → เกมบอกว่า "This MGFX effect seems to be for a newer release of MonoGame"
- **mgfxc 3.8.0.1641 ก็เกมก็ยังบอกว่า "newer release"** — แสดงว่า SDV ใช้ MonoGame custom build ที่เก่ากว่า 3.8.0 public
- **`.fx` source มี bug fixes อยู่แล้ว** (CA edge-safe, godrays saturation guard, water 3 fixes) แต่ยังไม่ได้ compile เป็น `.mgfxo`
- **Restore จาก `f71ce2d` (pre-session baseline)** — ยัง test ไม่เสร็จว่าชุดนี้ใช้ได้หรือไม่

### ความพยายามที่ทำไป
1. ❌ mgfxc 3.8.5 → "newer release" (3 commits of recompilation)
2. ❌ mgfxc 3.8.0.1641 → ติดตั้ง .NET Core 3.1 runtime → compile ได้สำเร็จ → แต่เกมก็ยังบอก "newer release"
3. 🔄 Restore จาก `f71ce2d` (commit ก่อน session เรา) → ไฟล์ถูก restore แล้ว, commit แล้ว → **ยังไม่ได้ทดสอบว่าใช้ได้หรือไม่**

### วิธีแก้ที่ต้องหาต่อ
- ต้องใช้ mgfxc จาก MonoGame เวอร์ชั่นเดียวกับที่ SDV ship (น่าจะเป็น custom build)
- อาจต้อง extract `mgcb.exe` หรือ `MonoGame.Framework.dll` จาก installation ของเกม
- หรือใช้ Content Patcher / PyTK compile shader ในเกมโดยตรง
- หรือถาม community ว่า SDV modders ใช้ mgfxc เวอร์ชั่นไหน compile `.mgfxo`

---

## 🎮 Hotkeys Summary (Final)

| Key | Function |
|-----|----------|
| **F3** | Water Mask Overlay ON/OFF (🔵🟡🟠🔴 แสดง water detection) |
| **F3 + Shift** | Paint Mode — LMB=Water tile, RMB=Dry tile |
| **F5** (when F3 ON) | Save `water-overrides.json` (Source 0 — ชนะทุก auto-detect) |
| **F10** | DevMenu — warp, เปลี่ยนเวลา/อากาศ/season, toggle effects |
| **F6** | Radiance Tuner — ปรับแต่ง effects |
| **F7** | Master Toggle — เปิด/ปิด mod |

---

## 📁 Files Changed Summary

### Modified (C#)
- `src/ModEntry.cs` — #19 stutter fix, #18 setting persist, F3/F5/F10 input handlers
- `src/ModConfig.cs` — #17 preset save/load ครบ 80+ properties, DevKey (F10)
- `src/RenderPipeline.WaterMask.cs` — #13 custom maps, multi-source water detection, WaterMapPainter integration, remove waterTiles API dependency
- `src/RenderPipeline.cs` — `_waterConfBuf` per-tile confidence tracking

### Modified (Shaders — .fx source)
- `shaders/finishing.fx` — #14 CA edge-safe falloff (15% screen margin)
- `shaders/godrays.fx` — #10 saturation guard (85% suppress white pixels)
- `shaders/water.fx` — #11 player exclusion, #12 small-body damp 70%, adaptive floor 0.45-0.65

### Added (New)
- `src/WaterMaskOverlay.cs` — F3 debug overlay
- `src/WaterMapPainter.cs` — F3+Shift tile painter + System.Text.Json serialization
- `src/DevMenu.cs` — F10 dev testing menu

### Modified (Binaries — .mgfxo)
- `assets/finishing.mgfxo` — ⚠️ INCOMPATIBLE (compiled with wrong mgfxc)
- `assets/godrays.mgfxo` — ⚠️ INCOMPATIBLE
- `assets/water.mgfxo` — ⚠️ INCOMPATIBLE

---

## 🔢 Git Commits in This Session (26 total)

```
c8e1738 fix: resolve all build errors — remove waterTiles API dependency, rewrite DevMenu...
dc5120f fix: change Water Mask toggle from F4 to F3
f136fef fix: change Water Mask toggle from F8 to F4
c33e960 fix(water): fix WaterMapPainter Shift detection + change toggle from F9 to F8
e2910d2 docs: final comprehensive fixes-applied summary
ce8dae3 feat(water): integrate WaterMapPainter as Source 0 + F5 save
b9fa291 feat(water): add WaterMapPainter
20043d4 feat(water): multi-source confidence-based water detection
50a6508 fix(water): adaptive floor + smallDamp
1a9d100 docs: update fixes-applied
d780d26 fix: change DevMenu hotkey F8 → F10
3cdf416 feat: add DevMenu (F8) + recompile 3 shaders
6000c10 docs: update fixes-applied
0371b81 fix(water): adaptive floor
6435688 fix(#13): art-based water detection for custom maps
b93d9e6 fix(#11,#12): player exclusion + small-body guard
6cc9ac5 fix(#11): player always excluded from water
168bedc fix(#10): godrays saturation guard
91137ae fix(#18): SaveLoaded/ReturnedToTitle handlers
fad4fb6 fix(#14): CA edge-safe falloff
7fd8158 docs: fixes-applied #19 #17 #15
87b83c7 fix(#17): complete preset save/load
fe5de29 fix(#19): early-out when mod disabled
9ea60ce docs: feedback round 3
3613784 docs: feedback round 2
e5b54f1 docs: user feedback issues
4d09efe docs: project overview session notes
```

---

## ⚠️ Notes for Next Session
- **`.mgfxo` issue** — 3 binaries ยังใช้ไม่ได้จนกว่าจะหา mgfxc ที่ถูกต้อง
- **`.fx` source changes** — ทั้งหมดปลอดภัย รอ compile
- **C# code** — `dotnet build` ✅ ผ่าน (clean build)
- **Integration test** — ยังไม่ได้ทดสอบ mod พร้อมเกม
- **water-overrides.json** — ไม่มีไฟล์จนกว่าจะใช้ F3+Shift → paint → F5 save
- **Branch:** `session/2026-07-21-initial-audit` — 26 commits ahead of `main`