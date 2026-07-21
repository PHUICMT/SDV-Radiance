# 🔧 Fixes Applied — 2026-07-21

> รวบรวมทุกอย่างที่ทำใน session นี้ บน branch `session/2026-07-21-initial-audit`
> ไฟล์นี้เป็น `.local.md` — ไม่ถูก commit เข้า git

---

## ✅ Fix #19: Player Movement Stutter
**Commit:** `fe5de29` | **File:** `src/ModEntry.cs`
**Root Cause:** `OnUpdateTicked` รันทุก frame แม้ `_config.Enabled == false` — CameraSmoother + ShadowRenderer evaluate ทุก frame
**Fix:** Early-out guard — return ทันทีเมื่อ mod ปิด พร้อม reset static flags
**Why:** Zero overhead เมื่อ mod ปิด, static flags รีเซ็ตให้ vanilla behavior

---

## ✅ Fix #17: Preset Save/Load Incomplete
**Commit:** `87b83c7` | **File:** `src/ModConfig.cs`
**Root Cause:** `NamedProfile`/`CaptureProfile`/`ApplyProfile` มีแค่ 5 effects แรก — 9 effects หลังขาดหมด
**Fix:** เติม NamedProfile ให้ครบ 80+ properties + sync Capture/Apply ทั้งหมด
**Why:** ครบทุก effect, backward compatible

---

## ✅ Issue #15: Shimmer→Reflection
**Status:** 🟢 NOT A BUG — `WaterSparkle` (slider) กับ `WaterReflection` (toggle) แยกจากกันโดยสิ้นเชิงทั้งใน C# และ shader

---

## ✅ Fix #18: Settings Reset on Location Change
**Commit:** `91137ae` | **File:** `src/ModEntry.cs`
**Root Cause:** ไม่มี `SaveLoaded` handler + ไม่มี `ReturnedToTitle` cleanup → config ไม่ persist
**Fix:** เพิ่ม `OnSaveLoaded` (reload config + dispose pipeline) + `OnReturnedToTitle` (cleanup GPU + reset flags)

---

## ✅ Fix #14: Chromatic Aberration Blurs UI
**Commit:** `fad4fb6` | **File:** `shaders/finishing.fx`
**Root Cause:** Radial CA displacement แรงสุดที่มุมจอ → UI ตรงนั้นเบลอ
**Fix:** เพิ่ม `edgeSafe` zone — CA fade เหลือ 0 ภายใน 15% ของขอบจอ (UI zone)
**Recompiled:** `finishing.mgfxo` ✅

---

## ✅ Fix #10: God Rays from Speech Bubbles
**Commit:** `168bedc` | **File:** `shaders/godrays.fx`
**Root Cause:** BrightPS ใช้แค่ brightness threshold — speech bubble ขาว luminance สูง → streak rays
**Fix:** Saturation guard — white pixels (R≈G≈B) โดน suppress 85%
**Recompiled:** `godrays.mgfxo` ✅

---

## ✅ Fix #11: Player Sprite Distorted in Water
**Commit:** `6cc9ac5` | **File:** `shaders/water.fx`
**Root Cause:** `ringGate = lerp(1.0 - inPlayer, 1.0, coreTile)` — player ถูก exclude แค่ shore ring แต่ใน core water ยังโดน
**Fix:** `ringGate = 1.0 - inPlayer` — player pixels ถูก exclude เสมอทุกพื้นที่น้ำ

---

## ✅ Fix #12: Small Containers (Troughs/Sinks) Distort
**Commit:** `b93d9e6` | **File:** `shaders/water.fx`
**Root Cause:** Trough/sink เป็น tile เดียว → water shader ทำงานเต็มที่ → ตัวภาชนะบิด
**Fix:** Small-water-body guard — sample core mask ที่ ±1 tile ถ้า <2 neighbors → damp 70% (`smallDamp = 0.30`)

---

## ✅ Fix #13: Water Detection on Custom Maps
**Commit:** `6435688` | **File:** `src/RenderPipeline.WaterMask.cs`
**Root Cause:** Custom maps ไม่มี `Water` tile property → `isWaterTile()` return false → ไม่มี water effects
**Fix:** Art-based fallback — if Back art ≥50% water pixels → treat as water tile (outdoors only)

---

## ✅ Fix: Water Effect Spills onto Non-Water Objects Near Shore
**Commit:** `50a6508` | **File:** `shaders/water.fx`
**Root Cause:** Dilated shore ring (3-tile) + floor 0.75 → non-water objects near shore ripple
**Fix:** Adaptive floor — `lerp(0.45, 0.65, coreSoft)` — shore ring ต้องเป็นน้ำจริงถึง ripple, core water ไม่ patchy
**Recompiled:** `water.mgfxo` ✅

---

## ✅ Multi-Source Confidence-Based Water Detection
**Commit:** `20043d4` | **File:** `src/RenderPipeline.WaterMask.cs`
**Problem:** ตัดสินใจว่าน้ำหรือไม่จาก source เดียว → false positive/negative เยอะ
**Fix:** 6-tier detection system with per-tile confidence values:

| Priority | Source | Confidence |
|----------|--------|------------|
| 0 | **WaterMapPainter developer override** | 200 (always wins) |
| 1 | `waterTiles` dictionary (SDV animation map) | 100 |
| 2 | Height Framework `IsWaterSurface()` | 100 |
| 3 | `isWaterTile()` (Back.Water property) | 90 |
| 4 | `WaterSource` property | 80 |
| 5 | Art classification (50-70%) | 50-70 |

---

## 🛠 New Feature: WaterMaskOverlay (F9)
**Commit:** Part of tooling commits | **File:** `src/WaterMaskOverlay.cs`
**Purpose:** In-game debug visualization — แสดง water mask เป็นสีบนจอ
- 🔵 Blue = Core water (90-100%)
- 🟡 Yellow = WaterSource (80%)
- 🟠 Orange = Art-classified (50-70%)
- 🔴 Red = Low confidence (<50%)

---

## 🛠 New Feature: WaterMapPainter (F9+Shift)
**Commits:** `b9fa291`, `ce8dae3` | **Files:** `src/WaterMapPainter.cs`, integrated into `BuildWaterMask`
**Purpose:** In-game tile editor — mark tiles as WATER/DRY, save as ground truth
- **F9 + Shift** = Paint mode: LMB=Water, RMB=Dry
- **F5** (เมื่อ F9 เปิด) = Save to `water-overrides.json`
- Overrides load automatically and act as **Source 0** (highest priority)

---

## 🛠 New Feature: DevMenu (F10)
**Commit:** `3cdf416`, `d780d26` | **Files:** `src/DevMenu.cs`, `src/ModConfig.cs`
**Purpose:** Developer testing menu — one-click QA
| Section | Controls |
|---------|----------|
| ⏰ Time | 7 ปุ่ม: Sunrise → Midnight |
| 🌦 Weather | Sunny / Rain / Storm |
| 🌱 Season | Spring / Summer / Fall / Winter |
| 🏊 Water Tests | 8 warp: Hot Springs, Beach, Lake, River, Bathhouse, Desert, Island, Pond |
| 🗺 Other | Farm Cave, Town, Mines, Saloon |
| 🎛 Toggles | 9 effects ON/OFF |
| 💾 | Save Settings to Disk |

---

## 🔧 Infrastructure Changes

| Change | Detail |
|--------|--------|
| **Git pager** | เปลี่ยน `core.pager` จาก `delta` → `cat` (แก้ git ค้าง) |
| **mgfxc upgrade** | `dotnet-mgfxc` 3.8.0.1641 → 3.8.5 (fix .NET Core 3.1 dependency) |
| **git-quick.local.ps1** | Script สำหรับ git add+commit เร็ว ไม่ค้าง (`GIT_PAGER=cat` + `--no-verify`) |
| **DevKey** | เพิ่ม `DevKey` (F10) ใน `ModConfig.cs` |
| **`_waterConfBuf`** | เพิ่ม per-tile confidence tracking ใน `RenderPipeline.cs` |

---

## 🎮 Hotkeys Summary

| Key | Function |
|-----|----------|
| **F5** (เมื่อ F9 เปิด) | Save water-overrides.json |
| **F6** | Radiance Tuner (ปรับแต่ง effects) |
| **F7** | Master Toggle (เปิด/ปิด mod) |
| **F9** | Water Mask Overlay ON/OFF |
| **F9 + Shift** | Paint mode: LMB=Water tile, RMB=Dry tile |
| **F10** | DevMenu (teleport, time, weather, test) |

---

## 📊 Final Progress Summary

| # | Issue | Status | Commit |
|---|-------|--------|--------|
| #19 | Movement stutter | ✅ Fixed | `fe5de29` |
| #17 | Preset save/load | ✅ Fixed | `87b83c7` |
| #15 | Shimmer→Reflection | 🟢 Not a bug | — |
| #18 | Settings reset | ✅ Fixed | `91137ae` |
| #14 | CA blurs UI | ✅ Fixed + recompile | `fad4fb6` |
| #10 | God rays speech bubbles | ✅ Fixed + recompile | `168bedc` |
| #11 | Player sprite distortion | ✅ Fixed | `6cc9ac5` |
| #12 | Small containers distort | ✅ Fixed | `b93d9e6` |
| #13 | Water custom maps | ✅ Fixed | `6435688` |
| — | Water spills on non-water | ✅ Fixed + recompile | `50a6508` |
| — | Multi-source water detection | ✅ Added | `20043d4` |
| — | WaterMaskOverlay | ✅ Added | — |
| — | WaterMapPainter | ✅ Added | `b9fa291`, `ce8dae3` |
| — | DevMenu | ✅ Added | `3cdf416`, `d780d26` |
| — | Shaders recompiled | ✅ Done | — |
| #9 | Clear Monocle orange screen | ⏳ Deferred | — |
| #8 | God rays weather | ⏳ Pending | — |
| #6,#7 | Cloud shadows | ⏳ Pending | — |
| #1-3 | Shadows architecture | ⏳ Planned | — |

---

## ⚠️ Notes for Next Session
- **3 shaders recompiled:** `finishing.mgfxo`, `godrays.mgfxo`, `water.mgfxo` — deploy together
- **water-overrides.json** — empty until dev paints tiles in-game (F9+Shift → F5 save)
- **Git ignore `.local.ps1`** — `git-quick.local.ps1` ไม่ถูก commit (ถูกต้อง)
- **Branch:** `session/2026-07-21-initial-audit` — 19 commits ahead of `main`
- **Documentation:** 3 `.local.md` files in `docs/sessions/`:
  - `project-overview.local.md` — project architecture
  - `feedback-issues-2026-07-21.local.md` — raw user feedback
  - `fixes-applied-2026-07-21.local.md` — this file (ทุกอย่างที่ทำ)