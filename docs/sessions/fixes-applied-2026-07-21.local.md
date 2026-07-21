# 🔧 Fixes Applied — 2026-07-21

> สรุป issues ที่แก้แล้วใน branch `session/2026-07-21-initial-audit`
> ไฟล์นี้เป็น `.local.md` — จะไม่ถูก commit เข้า git (อยู่ใน .gitignore)

---

## ✅ Fix #19: Player Movement Stutter (แม้ปิดทุก Effect)

**Commit:** `fe5de29`

### อาการ
ผู้ใช้รายงานว่าการเคลื่อนที่ของผู้เล่นกระตุก (stutter) แม้จะปิด visual effects ทั้งหมดแล้ว — อาการหายเมื่อปิด SDV-Radiance ทั้ง mod แสดงว่าปัญหาไม่ได้อยู่ที่ shader แต่อยู่ที่ code path หลักของ mod

### Root Cause
`OnUpdateTicked` ใน `ModEntry.cs` ถูกเรียกทุก frame (60fps) และทำงานทุกครั้งแม้ `_config.Enabled == false`:

1. `_camera.Update(_config)` — CameraSmoother ถูกเรียกทุก frame แม้ `CameraMode == Off` (แค่ set `_tracking = false` แต่ก็ยังมี overhead)
2. `SuppressVanillaShadows`, `SuppressVanillaClouds`, `SuppressVanillaObjectShadows` — ถูก evaluate ทุก frame ผ่าน `ShadowRenderer.ShadowsActiveNow()` และ `ShadowRenderer.SunShadowActive()` ซึ่งอ่านค่า time-of-day, weather, location ทุกครั้ง
3. static fields เหล่านี้ถูก Harmony prefix/transpiler อ่านทุก draw call — overhead สะสม

### วิธีแก้
เพิ่ม **early-out guard** ที่บรรทัดแรกของ `OnUpdateTicked`:

```csharp
if (!_config.Enabled)
{
    SuppressVanillaShadows = false;
    SuppressVanillaClouds = false;
    SuppressVanillaObjectShadows = false;
    SuppressVanillaBlobShadows = false;
    SuppressVanillaCritterShadows = false;
    return;  // <-- ข้ามทุกอย่างที่เหลือ
}
```

### ทำไมถึงแก้แบบนี้
- ✅ เมื่อ mod ปิด → ไม่มี overhead เลย — `OnUpdateTicked` กลับทันทีหลังจาก reset static flags
- ✅ static flags ถูกรีเซ็ตเป็น `false` เพื่อให้ Harmony patches คืนค่า vanilla behavior
- ✅ CameraSmoother ไม่ถูกเรียกเลย (ประหยัดทั้ง method call + conditional checks ข้างใน)
- ✅ `_config.Enabled` เป็น boolean check ที่ถูกมาก (nanoseconds) — zero overhead สำหรับคนที่เปิด mod ไว้
- ✅ เดิม `SuppressVanillaClouds` ใช้ `_config.Enabled && _config.SuppressVanillaCloudShadow` — ตอนนี้ใช้แค่ `_config.SuppressVanillaCloudShadow` เพราะ guard ด้านบนจัดการ `_config.Enabled` ไปแล้ว

---

## ✅ Fix #17: Preset System — Save ได้ แต่ Load ไม่ได้

**Commit:** `87b83c7`

### อาการ
ผู้ใช้สามารถ save custom presets ได้ แต่เมื่อกด load กลับมาใช้ — การตั้งค่าไม่เปลี่ยน (โดยเฉพาะพวก water, lighting, shadows, cloud shadow, tilt-shift, chromatic aberration)

### Root Cause
`NamedProfile` class, `CaptureProfile()`, และ `ApplyProfile()` ถูกเขียนตอนเริ่มต้นโปรเจค — ตอนนั้นมีแค่ **5 effects แรก** (Bloom, ColorGrade, GodRays, Fog) — แต่หลังจากนั้นมี effects ใหม่เพิ่มมาอีกมากมายโดยที่ **ไม่มีใครอัปเดตระบบ preset**:

| Effect | มีใน Preset? |
|--------|-------------|
| Bloom | ✅ |
| Color Grade | ✅ |
| God Rays | ✅ (แต่ขาด Threshold, Density, Decay) |
| Fog | ✅ (แต่ขาด Scale, Speed, TopBias) |
| Cloud Shadows | ❌ |
| Tilt-Shift | ❌ |
| Water | ❌ |
| Vignette | ❌ |
| Chromatic Aberration | ❌ |
| Flood GI | ❌ |
| Dynamic Lighting | ❌ |
| Directional Shadows | ❌ |
| Camera | ❌ |

**ผล:** save → ได้แค่ 5 effects แรก / load → apply แค่ 5 effects แรก / effects ที่เหลือใช้ค่าเดิม

### วิธีแก้
1. **ขยาย `NamedProfile`** — เพิ่ม properties ให้ครบทุก effect (จาก 16 → 80+ properties)
2. **ขยาย `CaptureProfile()`** — capture ทุก property ลง NamedProfile
3. **ขยาย `ApplyProfile()`** — apply ทุก property กลับจาก NamedProfile

### ทำไมถึงแก้แบบนี้
- ✅ ครบทุก effect ที่มีใน `ModConfig` — ไม่มี遗漏
- ✅ JSON serialization อัตโนมัติ — เพิ่ม properties ใน C# class → config.json ก็เก็บครบ
- ✅ Backward compatible — properties ใหม่มี default values (`false`, `0f`) → config.json เก่าโหลดได้ไม่มีพัง
- ✅ ใช้ pattern เดิม — ไม่เปลี่ยน architecture, แค่ขยายข้อมูล
- ⚠️ ข้อควรระวัง: ถ้าเพิ่ม effect ใหม่ในอนาคต ต้องอัปเดต `NamedProfile`, `CaptureProfile`, `ApplyProfile` ทั้ง 3 ที่

---

## ✅ Issue #15: Water — ปิด Shimmer แล้ว Reflection หายด้วย

**สถานะ:** 🟢 NOT A BUG — Verified

### ตรวจสอบแล้ว
- `WaterSparkle` (slider) → ควบคุมแค่ specular glints (ประกายแสงบนผิวน้ำ) ใน `RenderWater()` → `P(fx, "Sparkle")`
- `WaterReflection` (toggle) → ควบคุม screen-space reflection ใน `RenderWater()` → `P(fx, "ReflectStrength")`
- ใน shader `water.fx` — `Sparkle` กับ `ReflectStrength` ทำงานแยกกันโดยสิ้นเชิง:
  - `ReflectStrength` → บรรทัด 211-336 (reflection + self-reflection)
  - `Sparkle` → บรรทัด 352-386 (specular glints)
- ใน Tuner UI — แยกเป็นคนละ widget (slider vs toggle)

**สรุป:** ผู้ใช้อาจสับสนระหว่าง "Water Shimmer" (ไม่มี toggle นี้ — `WaterSparkle` เป็น slider) กับ `WaterEnabled` (toggle ปิดน้ำทั้งหมดรวม reflection) หรืออาจเป็น edge case เฉพาะบาง map ที่ทั้ง shimmer และ reflection พังพร้อมกันเพราะ water mask ไม่ถูกสร้าง

---

## 📊 สรุป Progress

| Issue | สถานะ | Commit |
|-------|--------|--------|
| #19 Movement stutter | ✅ Fixed | `fe5de29` |
| #17 Preset save/load | ✅ Fixed | `87b83c7` |
| #15 Shimmer→Reflection | 🟢 Not a bug | — |
| #18 Settings reset | ⏳ Pending | — |
| #14 Chromatic aberration | ⏳ Pending | — |
| #10 God rays speech bubbles | ⏳ Pending | — |
| #8 God rays weather | ⏳ Pending | — |
| #11 Hot spring reflection | ⏳ Pending | — |
| #12 Small water containers | ⏳ Pending | — |
| #13 Water custom maps | ⏳ Pending | — |
| #9 Clear Monocle | ⏳ Pending | — |
| #6, #7 Cloud shadows | ⏳ Pending | — |
| #1, #2, #3 Shadows (P0) | ⏳ Planned | — |
| #4, #5 Nice-to-have | ⏳ Backlog | — |