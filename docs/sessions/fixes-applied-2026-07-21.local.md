# 🔧 Fixes Applied — 2026-07-21

> สรุป issues ที่แก้แล้วใน branch `session/2026-07-21-initial-audit`
> ไฟล์นี้เป็น `.local.md` — จะไม่ถูก commit เข้า git

---

## ✅ Fix #19: Player Movement Stutter

**Commit:** `fe5de29` | **File:** `src/ModEntry.cs`

### Root Cause
`OnUpdateTicked` รันทุก frame แม้ `_config.Enabled == false` — CameraSmoother + ShadowRenderer evaluate ทุก frame

### Fix
Early-out guard — return ทันทีเมื่อ mod ปิด พร้อม reset static flags

---

## ✅ Fix #17: Preset Save/Load Incomplete

**Commit:** `87b83c7` | **File:** `src/ModConfig.cs`

### Root Cause
`NamedProfile`/`CaptureProfile`/`ApplyProfile` มีแค่ 5 effects แรก (Bloom, ColorGrade, GodRays, Fog) — 9 effects หลังขาดหมด

### Fix
เติม NamedProfile ให้ครบ 80+ properties + sync Capture/Apply ทั้งหมด

---

## ✅ Issue #15: Shimmer→Reflection

**Status:** 🟢 NOT A BUG — `WaterSparkle` (slider) กับ `WaterReflection` (toggle) แยกจากกันโดยสิ้นเชิง

---

## ✅ Fix #18: Settings Reset on Location Change

**Commit:** `91137ae` | **File:** `src/ModEntry.cs`

### Root Cause
ไม่มี `SaveLoaded` handler → config ไม่ sync กับ disk + ไม่มี `ReturnedToTitle` cleanup

### Fix
เพิ่ม `OnSaveLoaded` (reload config + dispose pipeline) + `OnReturnedToTitle` (cleanup GPU)

---

## ✅ Fix #14: Chromatic Aberration Blurs UI

**Commit:** `fad4fb6` | **File:** `shaders/finishing.fx`

### Root Cause
Radial CA displacement แรงสุดที่มุมจอ → UI ตรงนั้นเบลอ

### Fix
เพิ่ม `edgeSafe` zone — CA fade เหลือ 0 ภายใน 15% ของขอบจอ (UI zone)

---

## ✅ Fix #10: God Rays from Speech Bubbles

**Commit:** `168bedc` | **File:** `shaders/godrays.fx`

### Root Cause
BrightPS ใช้แค่ brightness threshold — speech bubble ขาวผ่าน → streak rays

### Fix
Saturation guard — grayscale/white pixels (R≈G≈B) โดน suppress 85%

---

## ✅ Fix #11: Player Sprite Distorted in Water

**Commit:** `6cc9ac5` | **File:** `shaders/water.fx`

### Root Cause
`ringGate = lerp(1.0 - inPlayer, 1.0, coreTile)` — player ถูก exclude แค่ใน shore ring (coreTile==0) แต่ในน้ำจริง (coreTile==1) ยังโดน distortion

### Fix
`ringGate = 1.0 - inPlayer` — player pixels ถูก exclude จาก water effects เสมอ ไม่ว่า coreTile จะเป็นอะไร

---

## ✅ Fix #12: Small Containers (Troughs/Sinks) Distort

**Commit:** `b93d9e6` | **File:** `shaders/water.fx`

### Root Cause
Trough/sink เป็น tile เดียวที่ถูก mark เป็น water → neighbor tiles แห้งหมด → water shader ทำงานเต็มที่บนภาชนะเล็กๆ → ตัวภาชนะบิดเบี้ยว

### Fix
Small-water-body guard — sample core mask ที่ ±1 tile offset ถ้า <2 neighbors → damp effect 85% (`smallDamp = 0.15`)

---

## ✅ Fix #13: Water Detection on Custom Maps

**Commit:** `6435688` | **File:** `src/RenderPipeline.WaterMask.cs`

### Root Cause
Custom maps (Fantasy Farm Cave, Immersive Farm 2) ไม่มี `Water` tile property → `isWaterTile()` return false → water mask ว่าง → ไม่มี water effects

### Fix
Art-based fallback — ถ้า Back art มีน้ำ ≥50% ของ opaque pixels → ถือเป็น water tile (outdoors only)

---

## ✅ Fix: Water Effect Spills onto Non-Water Objects Near Shore

**Commit:** `0371b81` | **File:** `shaders/water.fx`

### Root Cause
Dilated shore ring (3-tile) ทำให้ tiles ที่ไม่ใช่น้ำถูก mark ใน water mask → floor `0.75` ทำให้ของสีฟ้า/เทาใกล้ฝั่ง ripple

### Fix
Adaptive floor: `lerp(0.30, 0.70, coreSoft)` — core water ได้ 0.70 (ไม่ patchy), shore ring ได้ 0.30 (ต้องเป็นน้ำจริงถึง ripple)

---

## 📊 Progress Summary

| # | Issue | Status | Commit |
|---|-------|--------|--------|
| #19 | Movement stutter | ✅ Fixed | `fe5de29` |
| #17 | Preset save/load | ✅ Fixed | `87b83c7` |
| #15 | Shimmer→Reflection | 🟢 Not a bug | — |
| #18 | Settings reset | ✅ Fixed | `91137ae` |
| #14 | CA blurs UI | ✅ Fixed | `fad4fb6` |
| #10 | God rays speech bubbles | ✅ Fixed | `168bedc` |
| #11 | Player sprite distortion | ✅ Fixed | `6cc9ac5` |
| #12 | Small containers distort | ✅ Fixed | `b93d9e6` |
| #13 | Water custom maps | ✅ Fixed | `6435688` |
| — | Water spills on non-water | ✅ Fixed | `0371b81` |
| #9 | Clear Monocle orange screen | ⏳ Deferred | — |
| #8 | God rays weather | ⏳ Pending | — |
| #6,#7 | Cloud shadows | ⏳ Pending | — |
| #1-3 | Shadows architecture | ⏳ Planned | — |

**⚠️ Shaders ที่แก้ต้อง recompile .mgfxo:** `finishing.fx`, `godrays.fx`, `water.fx`