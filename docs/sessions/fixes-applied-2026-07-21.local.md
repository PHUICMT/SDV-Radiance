# 🔧 Fixes Applied — 2026-07-21

> สรุป issues ที่แก้แล้วใน branch `session/2026-07-21-initial-audit`
> ไฟล์นี้เป็น `.local.md` — จะไม่ถูก commit เข้า git (อยู่ใน .gitignore)

---

## ✅ Fix #19: Player Movement Stutter (แม้ปิดทุก Effect)

**Commit:** `fe5de29`

### อาการ
ผู้ใช้รายงานว่าการเคลื่อนที่ของผู้เล่นกระตุก (stutter) แม้จะปิด visual effects ทั้งหมดแล้ว — อาการหายเมื่อปิด SDV-Radiance ทั้ง mod

### Root Cause
`OnUpdateTicked` ใน `ModEntry.cs` ถูกเรียกทุก frame (60fps) และทำงานทุกครั้งแม้ `_config.Enabled == false`:
1. `_camera.Update(_config)` — CameraSmoother ถูกเรียกทุก frame
2. `SuppressVanilla*` — ถูก evaluate ทุก frame ผ่าน `ShadowRenderer` methods
3. static fields ถูก Harmony patches อ่านทุก draw call

### วิธีแก้
เพิ่ม early-out guard ที่บรรทัดแรกของ `OnUpdateTicked` — return ทันทีเมื่อ `!_config.Enabled`

### ทำไมถึงแก้แบบนี้
- ✅ Zero overhead เมื่อ mod ปิด
- ✅ static flags รีเซ็ตให้ vanilla behavior
- ✅ CameraSmoother ไม่ถูกเรียกเลย

---

## ✅ Fix #17: Preset System — Save ได้ แต่ Load ไม่ได้

**Commit:** `87b83c7`

### อาการ
Save custom presets ได้ แต่ load แล้วการตั้งค่าไม่เปลี่ยน (water, lighting, shadows, etc.)

### Root Cause
`NamedProfile`/`CaptureProfile`/`ApplyProfile` มีแค่ 5 effects แรก (Bloom, ColorGrade, GodRays, Fog) — effects ที่เพิ่มมาทีหลัง 9 effects ไม่ถูก capture/apply

### วิธีแก้
เติม properties ใน `NamedProfile` ให้ครบ 80+ properties + อัปเดต `CaptureProfile()` และ `ApplyProfile()` ให้ sync กัน

### ทำไมถึงแก้แบบนี้
- ✅ ครบทุก effect
- ✅ Backward compatible — properties ใหม่มี default values
- ⚠️ ถ้าเพิ่ม effect ใหม่ต้องอัปเดต 3 ที่

---

## ✅ Issue #15: Water — ปิด Shimmer แล้ว Reflection หายด้วย

**สถานะ:** 🟢 NOT A BUG — Verified

`WaterSparkle` (slider) กับ `WaterReflection` (toggle) แยกจากกันใน code และ shader — ผู้ใช้อาจสับสนกับ `WaterEnabled` ที่ปิดน้ำทั้งหมด

---

## ✅ Fix #18: Settings Reset หลังเข้า-ออก Location

**Commit:** `91137ae`

### อาการ
Settings reset หลังเข้า-ออก farm cave หรือ location อื่น

### Root Cause
1. ไม่มี `SaveLoaded` handler → config ใน memory ไม่ sync กับ disk
2. ไม่มี `ReturnedToTitle` handler → stale GPU resources
3. GMCM reset (`new ModConfig()`) ไม่ write ลง disk

### วิธีแก้
เพิ่ม `OnSaveLoaded` (reload config + clamp + dispose pipeline) + `OnReturnedToTitle` (cleanup GPU + reset flags)

### ทำไมถึงแก้แบบนี้
- ✅ Config ใน memory = บน disk เสมอ
- ✅ ป้องกัน stale render targets
- ✅ ใช้ SMAPI built-in events

---

## ✅ Fix #14: Chromatic Aberration — เบลอ UI มุมจอ

**Commit:** `fad4fb6`

### อาการ
เปิด CA แล้ว UI elements ตรงมุมจอเบลอ

### Root Cause
`finishing.fx` ใช้ radial displacement ที่แรงสุดตรงมุมจอ — UI อยู่ตรงนั้นพอดี

### วิธีแก้
เพิ่ม `edgeSafe` zone — CA strength fade เหลือ 0 ภายใน 15% ของขอบจอ

### ทำไมถึงแก้แบบนี้
- ✅ `smoothstep` — smooth fade ไม่มีรอยต่อ
- ✅ กลางจอยังได้ CA เต็ม
- ✅ UI อ่านชัด 100%

---

## ✅ Fix #10: God Rays รั่วจาก Speech Bubbles

**Commit:** `168bedc`

### อาการ
God rays ปรากฏจาก speech bubbles (The Muttering Farmer) และ UI ขาวอื่นๆ

### Root Cause
`BrightPS` ใช้แค่ brightness threshold — speech bubble พื้นหลังขาว luminance สูง → ผ่าน → streak rays

### วิธีแก้
เพิ่ม saturation guard — grayscale/white pixels (R≈G≈B) ถูก suppress 85%:
```hlsl
float whiteBias = saturate(1.0 - (maxC - minC) * 6.0);
mask *= 1.0 - whiteBias * 0.85;
```

### ทำไมถึงแก้แบบนี้
- ✅ Real light emitters มีสี → ไม่โดน suppress
- ✅ Speech bubbles ขาว → โดน suppress 85%
- ✅ 0.85 ไม่ใช่ 1.0 — กัน edge case แสงจันทร์ขาว
- ⚠️ ต้อง recompile `godrays.mgfxo`

---

## 📊 สรุป Progress

| Issue | สถานะ | Commit |
|-------|--------|--------|
| #19 Movement stutter | ✅ Fixed | `fe5de29` |
| #17 Preset save/load | ✅ Fixed | `87b83c7` |
| #15 Shimmer→Reflection | 🟢 Not a bug | — |
| #18 Settings reset | ✅ Fixed | `91137ae` |
| #14 Chromatic aberration | ✅ Fixed | `fad4fb6` |
| #10 God rays speech bubbles | ✅ Fixed | `168bedc` |
| #8 God rays weather | ⏳ Pending | — |
| #11 Hot spring reflection | ⏳ Pending | — |
| #12 Small water containers | ⏳ Pending | — |
| #13 Water custom maps | ⏳ Pending | — |
| #9 Clear Monocle | ⏳ Pending | — |
| #6, #7 Cloud shadows | ⏳ Pending | — |
| #1, #2, #3 Shadows (P0) | ⏳ Planned | — |
| #4, #5 Nice-to-have | ⏳ Backlog | — |