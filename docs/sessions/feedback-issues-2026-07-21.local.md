# 📝 User Feedback & Issues — 2026-07-21

> จาก comment ผู้ใช้บน Nexus Mods หลัง deploy mod
> ไฟล์นี้เป็น `.local.md` — จะไม่ถูก commit เข้า git (อยู่ใน .gitignore)

---

## 🔴 Issue 1: Shadows อ่อนเกินไป — ไม่เด่นชัดเท่าคู่แข่ง

**ปัญหา:** เงา (shadows) ของ SDV-Radiance ดูจาง/เบลอเกินไป (subtle, mostly ignorable shadowy silhouettes) เมื่อเทียบกับ mod คู่แข่ง **Dynamic Shader** (Nexus #40775) ที่ทำเฉพาะเงาภายนอกแต่ทำได้เด่นชัดและ immersive กว่ามาก

**สิ่งที่ต้องทำ:**
- [ ] ศึกษา Dynamic Shader mod ว่าทำ shadow rendering อย่างไรให้เด่นชัดกว่า
- [ ] ปรับ shadow intensity/opacity/darkness ให้ปรับแต่งได้
- [ ] ปรับ shadow softness/blur radius ให้ผู้ใช้ควบคุมได้
- [ ] เทียบผลลัพธ์ side-by-side กับ Dynamic Shader

---

## 🔴 Issue 2: Shadow Maps ไม่ครอบคลุม — ใช้ได้เฉพาะ vanilla maps

**ปัญหา:** Shadow maps ถูก hardcode สำหรับ vanilla maps เท่านั้น เมื่อเล่นกับ mods ขยายแผนที่เช่น **SVE (Stardew Valley Expanded)** และ **RSV (Ridgeside Village)** ตำแหน่ง buildings เปลี่ยนไป ทำให้:
- อาคารใน town ที่ถูกย้ายตำแหน่งโดย SVE ไม่มีเงา
- objects ที่อยู่นอก shadow map ที่กำหนดไว้ไม่มีเงา
- ผู้เล่นสังเกตเห็นความไม่สม่ำเสมอ → immersion breaking

**สิ่งที่ต้องทำ:**
- [ ] รองรับ SVE (Stardew Valley Expanded) shadow maps
- [ ] รองรับ RSV (Ridgeside Village) shadow maps
- [ ] ออกแบบระบบ shadow map แบบ data-driven (JSON) แทน hardcode
- [ ] สร้างไฟล์ JSON template ให้ผู้เล่นแก้ไข/เพิ่ม shadow map coordinates เองได้
- [ ] เขียนเอกสารอธิบายวิธีสร้าง custom shadow maps
- [ ] ให้ผู้เล่นใช้ Debug mod coordinates มาปรับแต่ง shadow maps ได้

---

## 🔴 Issue 3: ไม่มีระบบ Custom Shadow Maps แบบ Player-Editable

**ปัญหา:** ไม่มีไฟล์ configuration/JSON ที่ผู้เล่นสามารถอ่านและแก้ไขเพื่อเพิ่ม shadow map coordinates สำหรับ mod maps เองได้

**สิ่งที่ต้องทำ:**
- [ ] ออกแบบ schema สำหรับ shadow map data files (JSON)
- [ ] สร้าง `shadow-maps/` โฟลเดอร์พร้อมไฟล์ template
- [ ] เขียน documentation วิธีเพิ่ม custom shadow maps
- [ ] รองรับการโหลด shadow maps จากหลายไฟล์ (vanilla + SVE + RSV + custom)
- [ ] สร้างระบบให้ผู้เล่นแชร์ custom shadow maps กลับมาให้ main branch

---

## 🟡 Issue 4: Water Reflections — อาจหนัก CPU/GPU เกินไป

**ปัญหา:** ผู้ใช้กังวลว่า water reflections อาจใช้ทรัพยากร CPU/GPU มากเกินไป (เทียบกับ Minecraft Java shaders ที่ทำให้พัดลมดัง) — แต่ชมว่ามี on/off switches ให้ปิดได้

**สถานะ:** ✅ มี on/off switches อยู่แล้ว — แต่ควรตรวจสอบ performance เพิ่มเติม

**สิ่งที่ต้องทำ:**
- [ ] Profile performance ของ water shader
- [ ] เพิ่ม quality levels (Low/Medium/High) สำหรับ water reflections
- [ ] ปรับ resolution ของ water render target ได้
- [ ] เพิ่มตัวเลือก "Performance Mode" สำหรับเครื่องสเปคต่ำ

---

## 🟡 Issue 5: Color Grading — เสนอ Blue Light Filter

**ปัญหา:** ผู้ใช้ชอบ vanilla colors แต่แนะนำให้เพิ่ม **blue light filter** (ตัวกรองแสงสีฟ้า) สำหรับถนอมสายตา

**สิ่งที่ต้องทำ:**
- [ ] เพิ่ม Blue Light Filter mode ใน color grading
- [ ] ปรับ warmth/color temperature ได้
- [ ] Schedule-based (เปิดอัตโนมัติตอนกลางคืน)
- [ ] แยกเป็น independent toggle จาก color grading หลัก

---

## 🟡 Issue 6: Cloud Shadows — ขาด Fine-Tune Controls

**ปัญหา:** เมื่อเทียบกับ Dynamic Shader mod ที่มีตัวควบคุมขนาดและจำนวนเมฆ SDV-Radiance ยังขาดการปรับแต่งแบบละเอียด

**สิ่งที่ต้องทำ:**
- [ ] เพิ่ม Cloud Size control
- [ ] เพิ่ม Cloud Count/Density control
- [ ] เพิ่ม Cloud Speed control
- [ ] เพิ่ม Cloud Opacity/Darkness control

---

## 🟡 Issue 7: Cloud Shadows — ปัญหาบน Maps ขนาดเล็ก

**ปัญหา:** บน maps ที่มีขนาดเล็ก (เช่น 1/4 ของจอ) เมฆจะรวมตัวกันที่จุดเดียวทำให้เกิดเงามืดมาก — ทำลาย immersion และทำให้ cutscene ดูไม่ได้

**สิ่งที่ต้องทำ:**
- [ ] Scale cloud shadow noise/texture ตามขนาด map
- [ ] Clamp cloud shadow intensity บน maps ขนาดเล็ก
- [ ] ปรับ cloud distribution ให้กระจายตัวดีขึ้นบน maps ทุกขนาด
- [ ] ทดสอบกับ maps หลายขนาด (full screen, half, quarter)

---

## 🔴 Issue 8: God Rays — ไม่ปิดตอนสภาพอากาศเลวร้ายหรือใต้เมฆ

**ปัญหา:** ผู้ใช้ถามว่า mod ปิด god rays ตอนฝนตก/พายุ หรือตอนอยู่ใต้เงาเมฆหรือไม่ — god rays ควรตอบสนองต่อสภาพอากาศและตำแหน่งเมฆ

**สิ่งที่ต้องทำ:**
- [ ] ตรวจสอบว่า god rays ปิด/ลดความเข้มตอนฝนตกหรือไม่
- [ ] ตรวจสอบว่า god rays ปิด/ลดความเข้มตอนอยู่ใต้ cloud shadows หรือไม่
- [ ] God rays intensity ควรแปรผันตาม weather (sunny > cloudy > rainy > stormy)
- [ ] God rays ควร fade เมื่อ cloud shadow coverage สูง

---

## 🔴 Issue 9: Compatibility — ขัดแย้งกับ Clear Monocle / Clear Glasses

**ปัญหา:** เมื่อใช้ร่วมกับ **Clear Monocle** (fork ของ Sprite Master/Clear Glasses ที่ใช้ xBRZ) และ **Dynamic Shader** พร้อมกัน — เกิดปัญหา "orange screen" (จอส้ม) ซึ่งน่าจะเกี่ยวข้องกับ shadow system

**สิ่งที่ต้องทำ:**
- [ ] ทดสอบ compatibility กับ Clear Monocle
- [ ] ทดสอบ compatibility กับ Sprite Master / Clear Glasses
- [ ] ตรวจสอบ render target conflicts ระหว่าง shadow pass กับ xBRZ upscaling
- [ ] Debug "orange screen" issue — อาจเกิดจาก render target ไม่ถูก clear หรือ blend state clash
- [ ] เพิ่ม compatibility mode หรือ auto-detect conflicting mods

---

## 🔴 Issue 10: God Rays — รั่วจาก Speech Bubbles (The Muttering Farmer)

**ปัญหา:** God rays ปรากฏจาก speech bubbles ที่เพิ่มโดย mod **The Muttering Farmer** — god rays ควรกรองเฉพาะ light sources ที่เป็นแสงจริง ไม่ใช่ UI elements

**สิ่งที่ต้องทำ:**
- [ ] ตรวจสอบ source detection logic ใน god rays — ปัจจุบัน detect จาก brightness/luminance อย่างเดียวหรือไม่
- [ ] เพิ่ม filtering ให้ ignore UI layer elements (speech bubbles, text, HUD)
- [ ] แยก render layer สำหรับ god rays source detection (exclude UI layer)
- [ ] ทดสอบกับ The Muttering Farmer mod

---

## 🔴 Issue 11: Water — Reflection ใน Hot Spring ไม่ทำงาน + Sprite บิดเบี้ยว

**ปัญหา:** เมื่อเข้าบ่อน้ำร้อน (hot spring):
1. Player reflection ไม่แสดง
2. Sprite ของผู้เล่นบิดเบี้ยวไปตามการไหลของน้ำ

**สิ่งที่ต้องทำ:**
- [ ] ตรวจสอบ water mask detection ใน hot spring area
- [ ] ตรวจสอบว่า hot spring tiles ถูก mark เป็น water tiles หรือไม่
- [ ] Debug sprite distortion — อาจเกิดจาก water displacement shader ทำงานผิดพลาดบน tiles ที่ไม่ควรเป็น water
- [ ] ทดสอบกับทุก hot spring/bathhouse locations

---

## 🔴 Issue 12: Water — ภาชนะน้ำขนาดเล็กกว่า 1 Tile บิดเบี้ยว

**ปัญหา:** ใน water bodies ที่เล็กกว่า 1 tile (เช่น troughs/sinks — รางน้ำ/อ่างล้าง) ตัวภาชนะเองบิดเบี้ยวไปกับผิวน้ำ

**สิ่งที่ต้องทำ:**
- [ ] เพิ่ม minimum tile size threshold สำหรับ water effect
- [ ] ตรวจสอบ water mask — อาจ include tiles ที่ไม่ใช่ water จริง (เช่น edges ของภาชนะ)
- [ ] ปรับ water shader ให้ไม่ distort objects ที่อยู่ติดกับ water tiles ขนาดเล็ก
- [ ] ทดสอบกับ troughs, sinks, และ water containers ขนาดเล็กทุกประเภท

---

## 🔴 Issue 13: Water — ไม่ทำงานบน Custom Maps

**ปัญหา:** Water effects ไม่แสดงบน water bodies ใน custom maps:
- **Lnh's Fantasy Farm Cave**
- **Immersive Farm 2 Remastered**

**สิ่งที่ต้องทำ:**
- [ ] ตรวจสอบว่า water tile detection ใช้ tile properties จาก vanilla เท่านั้นหรือไม่
- [ ] รองรับ custom map tile properties สำหรับ water detection
- [ ] ทดสอบกับ custom farm maps ยอดนิยม
- [ ] อาจต้องใช้ fallback detection (สีฟ้า + animation) แทน tile properties สำหรับ custom maps
- [ ] สร้างระบบให้ผู้ใช้ระบุ water areas บน custom maps เองได้ (JSON config)

---

## 🔴 Issue 14: Chromatic Aberration — ทำให้จอเบลอโดยเฉพาะ 4 มุม

**ปัญหา:** เมื่อเปิด Chromatic Aberration หน้าจอจะเบลอ โดยเฉพาะบริเวณ 4 มุม — ความแรงของ effect มากเกินไป

**สิ่งที่ต้องทำ:**
- [ ] ปรับ chromatic aberration strength ให้มีค่า default ที่เหมาะสม
- [ ] เพิ่ม intensity slider ใน tuner/config
- [ ] ตรวจสอบว่า CA shader ใช้ screen-space distance ถูกต้องหรือไม่
- [ ] ลด falloff ที่มุมจอ — อาจใช้ radial falloff ที่ aggressive เกินไป
- [ ] ทดสอบกับความละเอียดจอหลายขนาด

---

## 🔴 Issue 15: Water — ปิด Shimmer แล้ว Reflection หายด้วย

**ปัญหา:** เมื่อปิด "Water Shimmer" effect — "Reflection" effect ก็หายไปด้วย (ทั้งที่ควรเป็นอิสระจากกัน)

**สิ่งที่ต้องทำ:**
- [ ] ตรวจสอบ code — shimmer กับ reflection อาจใช้ render pass หรือ toggle ร่วมกัน
- [ ] แยก shimmer toggle กับ reflection toggle ให้เป็นอิสระจากกัน
- [ ] ทดสอบทุก combination: shimmer on/off + reflection on/off

---

## 📊 สรุป Priority (Updated)

| Priority | Issue # | Issue | Impact |
|----------|---------|-------|--------|
| 🔴 P0 | #1 | Shadow intensity/visibility | ผู้ใช้ไม่เปลี่ยนมาใช้เพราะเงาไม่เด่น |
| 🔴 P0 | #2 | SVE/RSV shadow map support | ผู้ใช้กลุ่มใหญ่ (SVE+RSV) ไม่มีเงา |
| 🔴 P0 | #3 | Data-driven shadow maps (JSON) | เปิดให้ community ช่วยสร้าง shadow maps |
| 🔴 P0 | #11 | Water: Hot spring reflection broken + sprite distortion | Bug — ฟีเจอร์พัง |
| 🔴 P0 | #12 | Water: Small containers (<1 tile) distort | Bug — ทำลาย assets |
| 🔴 P0 | #13 | Water: ไม่ทำงานบน custom maps | ผู้ใช้ custom maps ไม่ได้ใช้ฟีเจอร์ |
| 🔴 P0 | #14 | Chromatic aberration เบลอเกินไป | ทำให้เกมเล่นไม่ได้ |
| 🔴 P0 | #15 | Shimmer toggle ปิด reflection ด้วย | Bug — logic error |
| 🔴 P0 | #9 | Compatibility: Clear Monocle orange screen | Conflict กับ mod ยอดนิยม |
| 🔴 P0 | #10 | God rays รั่วจาก speech bubbles | God rays ผิดที่ |
| 🟡 P1 | #6 | Cloud shadow fine-tune controls | เพิ่มความยืดหยุ่น |
| 🟡 P1 | #7 | Cloud shadow on small maps | Bug ที่ทำลาย immersion |
| 🟡 P1 | #8 | God rays ไม่ตอบสนองต่อสภาพอากาศ | Immersion breaking |
| 🟡 P2 | #4 | Water performance profiles | กังวลเรื่องประสิทธิภาพ |
| 🟢 P3 | #5 | Blue light filter | Nice-to-have |

---

## 🔗 Reference

- **Dynamic Shader mod:** https://www.nexusmods.com/stardewvalley/mods/40775
- **SVE:** Stardew Valley Expanded
- **RSV:** Ridgeside Village
- **Clear Monocle:** Fork of Sprite Master/Clear Glasses (xBRZ only)
- **The Muttering Farmer:** Mod ที่เพิ่ม speech bubbles
- **Lnh's Fantasy Farm Cave:** Custom farm map
- **Immersive Farm 2 Remastered:** Custom farm map
- **SMAPI Log:** https://smapi.io/log/38be472d9cf64806ab0b33dda441508d