# 🔧 Fixes Applied — 2026-07-21

> สรุป issues ที่แก้แล้วใน branch `session/2026-07-21-initial-audit`
> ไฟล์นี้เป็น `.local.md` — จะไม่ถูก commit เข้า git

---

## ✅ Fix #19: Player Movement Stutter
**Commit:** `fe5de29` | **File:** `src/ModEntry.cs`
**Root Cause:** `OnUpdateTicked` รันทุก frame แม้ `_config.Enabled == false`
**Fix:** Early-out guard — return ทันทีเมื่อ mod ปิด พร้อม reset static flags

---

## ✅ Fix #17: Preset Save/Load Incomplete
**Commit:** `87b83c7` | **File:** `src/ModConfig.cs`
**Root Cause:** `NamedProfile`/`CaptureProfile`/`ApplyProfile` มีแค่ 5 effects แรก — 9 effects หลังขาดหมด
**Fix:** เติม NamedProfile ให้ครบ 80+ properties + sync Capture/Apply ทั้งหมด

---

## ✅ Issue #15: Shimmer→Reflection
**Status:** 🟢 NOT A BUG — `WaterSparkle` (slider) กับ `WaterReflection` (toggle) แยกจากกันโดยสิ้นเชิง

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
**Recompiled:** `finishing.mgfxo` ✅ (mgfxc 3.8.5, OpenGL profile)

---

## ✅ Fix #10: God Rays from Speech Bubbles
**Commit:** `168bedc` | **File:** `shaders/godrays.fx`
**Root Cause:** BrightPS ใช้แค่ brightness threshold — speech bubble ขาวผ่าน → streak rays
**Fix:** Saturation guard — grayscale/white pixels (R≈G≈B) โดน suppress 85%
**Recompiled:** `godrays.mgfxo` ✅ (mgfxc 3.8.5, OpenGL profile)

---

## ✅ Fix #11: Player Sprite Distorted in Water
**Commit:** `6cc9ac5` | **File:** `shaders/water.fx`
**Root Cause:** `ringGate = lerp(1.0 - inPlayer, 1.0, coreTile)` — player ถูก exclude แค่ shore ring
**Fix:** `ringGate = 1.0 - inPlayer` — player pixels ถูก exclude เสมอทุกพื้นที่น้ำ

---

## ✅ Fix #12: Small Containers (Troughs/Sinks) Distort
**Commit:** `b93d9e6` | **File:** `shaders/water.fx`
**Root Cause:** Trough/sink เป็น tile เดียว → water shader ทำงานเต็มที่ → ตัวภาชนะบิด
**Fix:** Small-water-body guard — sample core mask ที่ ±1 tile ถ้า <2 neighbors → damp 85%

---

## ✅ Fix #13: Water Detection on Custom Maps
**Commit:** `6435688` | **File:** `src/RenderPipeline.WaterMask.cs`
**Root Cause:** Custom maps ไม่มี `Water` tile property → `isWaterTile()` return false → ไม่มี water effects
**Fix:** Art-based fallback — ถ้า Back art มีน้ำ ≥50% → ถือเป็น water tile (outdoors only)

---

## ✅ Fix: Water Effect Spills onto Non-Water Objects Near Shore
**Commit:** `0371b81` | **File:** `shaders/water.fx`
**Root Cause:** Dilated shore ring (3-tile) + floor 0.75 → non-water objects near shore ripple
**Fix:** Adaptive floor — `lerp(0.30, 0.70, coreSoft)` — shore ring ต้องเป็นน้ำจริงถึง ripple
**Recompiled:** `water.mgfxo` ✅ (mgfxc 3.8.5, OpenGL profile)

---

## 🛠 New Feature: DevMenu (F10)
**Commit:** `3cdf416` | **Files:** `src/DevMenu.cs`, `src/ModEntry.cs`, `src/ModConfig.cs`
| Section | Controls |
|---------|----------|
| ⏰ Time | 7 ปุ่ม: Sunrise (06:00) → Midnight (00:30) |
| 🌦 Weather | Sunny / Rain / Storm |
| 🌱 Season | Spring / Summer / Fall / Winter |
| 🏊 Water Tests | 8 warp: Hot Springs, Beach, Mountain Lake, Forest River, Bathhouse, Desert, Island North, Cindersap Pond |
| 🗺 Other | Farm Cave, Town, Mines, Saloon |
| 🎛 Toggles | 9 effects (Water, God Rays, Shadows, Cloud, Bloom, Fog, Tilt-Shift, Vignette, CA) |
| 💾 | Save Settings to Disk |

---

## 🔧 Tooling
- **Hotkey:** DevMenu เปลี่ยนจาก F8 → F10 (commit `d780d26`) — กันชนกับ Fashion Sense
- **mgfxc:** อัปเกรด 3.8.0.1641 → 3.8.5 (fix .NET Core 3.1 dependency)
- **git:** เปลี่ยน `core.pager` จาก `delta` → `cat` (แก้ git commit ค้าง)
- **git-quick.local.ps1:** Script สำหรับ git add+commit เร็ว ไม่ค้าง (`GIT_PAGER=cat` + `--no-verify`)

---

## 📊 Progress Summary

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
| — | Water spills on non-water | ✅ Fixed + recompile | `0371b81` |
| — | DevMenu tooling | ✅ Added | `3cdf416` |
| — | DevMenu F8→F10 | ✅ Fixed | `d780d26` |
| #9 | Clear Monocle orange screen | ⏳ Deferred | — |
| #8 | God rays weather | ⏳ Pending | — |
| #6,#7 | Cloud shadows | ⏳ Pending | — |
| #1-3 | Shadows architecture | ⏳ Planned | — |

## 🔑 Key Commands
```
F6    → Radiance Tuner (ปรับแต่ง effects)
F7    → Master Toggle (เปิด/ปิด mod)
F10   → Dev Menu (teleport, time, weather, test)