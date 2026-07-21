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

## 📊 สรุป Priority

| Priority | Issue | Impact |
|----------|-------|--------|
| 🔴 P0 | Shadow intensity/visibility | ผู้ใช้ไม่เปลี่ยนมาใช้เพราะเงาไม่เด่น |
| 🔴 P0 | SVE/RSV shadow map support | ผู้ใช้กลุ่มใหญ่ (SVE+RSV) ไม่มีเงา |
| 🔴 P0 | Data-driven shadow maps (JSON) | เปิดให้ community ช่วยสร้าง shadow maps |
| 🟡 P1 | Cloud shadow fine-tune controls | เพิ่มความยืดหยุ่น |
| 🟡 P1 | Cloud shadow on small maps | Bug ที่ทำลาย immersion |
| 🟡 P2 | Water performance profiles | กังวลเรื่องประสิทธิภาพ |
| 🟢 P3 | Blue light filter | Nice-to-have |

---

## 🔗 Reference

- **Dynamic Shader mod:** https://www.nexusmods.com/stardewvalley/mods/40775
- **SVE:** Stardew Valley Expanded
- **RSV:** Ridgeside Village