# 🎮 SDV-Radiance — บทวิเคราะห์โปรเจคแบบละเอียดครบทุกมิติ

> สร้างเมื่อ: 2026-07-21
> ไฟล์นี้เป็น `.local.md` — จะไม่ถูก commit เข้า git (อยู่ใน .gitignore)

---

## 📌 โปรเจคนี้คืออะไร?

**SDV-Radiance** คือ **Graphics Suite (ชุดปรับแต่งกราฟิก)** สำหรับเกม **Stardew Valley** ที่ทำงานผ่าน SMAPI 4.x โดยเป็นระบบ **Multi-Pass Shader Chain** ที่รันทั้งหมดบน GPU ด้วย MonoGame + HLSL จุดประสงค์คือยกระดับภาพกราฟิกของเกมจากกราฟิก 2D แบบ pixel art ให้มีมิติแสงเงา บรรยากาศ และเอฟเฟกต์ที่สมจริงขึ้น โดยไม่เปลี่ยน assets ดั้งเดิมของเกม

---

## 🏗️ โครงสร้างทางเทคนิค (Architecture)

### Layer 1: Entry Point & Orchestration

| ไฟล์ | หน้าที่ |
|------|--------|
| `ModEntry.cs` | จุดเริ่มต้นของ mod — hook เข้า SMAPI events (`RenderedWorld`, `RenderingWorld`, `RenderingStep`) ควบคุม post-processing pipeline และ shadow system, ใช้ Harmony patching ปิด vanilla blob shadows (ตัวละคร, ต้นไม้, พุ่มไม้, objects, critters, เมฆ), ปรับ water animation, บังคับ `ShouldDrawOnBuffer` |
| `ModConfig.cs` | ระบบ config ที่ serialize เป็น `config.json` — มี properties สำหรับทุก effect (bloom, color grade, god rays, fog, cloud shadow, tilt-shift, water, finishing, flood GI, classic lighting, directional shadows), `LookPreset` enum (Subtle, Cinematic, Vibrant, Off), `NamedProfile` สำหรับ save/load looks |
| `CameraSmoother.cs` | Smooth camera interpolation สำหรับ transitions — ทำให้การเคลื่อนกล้องนุ่มนวลขึ้น |

### Layer 2: Rendering Pipeline

| ไฟล์ | หน้าที่ |
|------|--------|
| `RenderPipeline.cs` | ไฟล์หลัก — ควบคุมลำดับการ render targets, จัดการ texture pool, multi-pass rendering, device reset handling |
| `RenderPipeline.Stages.cs` | แตก pass ต่างๆ ออกมา: Bloom, Color Grading, God Rays, Fog, Cloud Shadow, Tilt-Shift, Water, Finishing — แต่ละ stage เป็น method แยก |
| `RenderPipeline.Lighting.cs` | ระบบแสงแบบ dynamic — จัดการ light source, occlusion, attenuation, light color blending |
| `RenderPipeline.WaterMask.cs` | ระบบ water mask — pixel-accurate shoreline detection, wading self-reflection, ripple/sparkle/shimmer |

### Layer 3: Shadow System

| ไฟล์ | หน้าที่ |
|------|--------|
| `ShadowRenderer.cs` | ไฟล์หลัก — จัดการ shadow map generation, directional light alignment (ตามพระอาทิตย์/ดวงจันทร์), shadow filtering |
| `ShadowRenderer.Baking.cs` | ระบบ pre-bake shadows — สำหรับ static objects ที่ไม่เปลี่ยนตำแหน่ง |
| `ShadowRenderer.Characters.cs` | Shadow rendering เฉพาะสำหรับตัวละคร (player, NPCs, animals) |
| `ShadowRenderer.Objects.cs` | Shadow rendering สำหรับ objects (trees, crops, buildings, props) |

### Layer 4: Lighting & Post-Processing

| ไฟล์ | หน้าที่ |
|------|--------|
| `FloodLightmap.cs` | Global Illumination แบบ flood-fill lightmap — จำลองแสงกระจาย (indirect lighting) |
| `RadianceTunerMenu.cs` | In-game tuner (กด F6) — ปรับแต่งทุก effect แบบ real-time |
| `TextEntryMenu.cs` | UI สำหรับพิมพ์ข้อความ — ใช้ใน save/load named profiles |

### Layer 5: Shaders (HLSL)

| ไฟล์ | เทคนิค |
|------|--------|
| `bloom.fx` | Bloom effect — luminance threshold + multi-pass Gaussian blur (downsample + upsample) |
| `cloudshadow.fx` | Cloud shadow projection — scrolling noise-based cloud mask ทาบลงบน world |
| `colorgrade.fx` | Color grading — LUT-based + auto-mood ตามเวลา/สภาพอากาศ/ฤดูกาล |
| `finishing.fx` | Final pass — vignette, chromatic aberration, tone mapping, auto-exposure |
| `floodlight.fx` | Flood light GI — diffuse light propagation จาก lightmap |
| `fog.fx` | Volumetric fog — depth-based fog พร้อมความหนาแน่นแปรผันตามฤดูกาลและเวลา |
| `godrays.fx` | God rays (crepuscular rays) — radial blur จาก light source จริง |
| `lighting.fx` | Dynamic lighting — per-light attenuation, occlusion, normal-based shading |
| `tiltshift.fx` | Tilt-shift depth of field — blur ตาม depth buffer เลียนแบบ lens blur |
| `water.fx` | Water rendering — reflection, ripple, sparkle, surface shimmer, shoreline detection |

---

## 🔧 เทคนิคที่ใช้ (Technical Techniques)

### 1. Shadow System
- **Directional shadows** — เงาจากพระอาทิตย์ที่หมุนตามเวลาในเกม (time-of-day)
- **Moonlight shadows** — เงาจากแสงจันทร์ที่ปรับตาม lunar phase และ season
- **Per-light indoor shadows** — เงาในอาคารจาก光源ภายใน
- **Occlusion-aware** — พิจารณา geometry ที่บังแสง
- **Shadow filtering** — PCF (Percentage-Closer Filtering) หรือเทคนิค soft shadow
- **Pre-baking** — static shadows ถูก bake ไว้ล่วงหน้าเพื่อประสิทธิภาพ
- ใช้ **Harmony patches** เพื่อปิด vanilla blob shadows ทั้งหมด

### 2. Lighting
- **Dynamic lighting** — per-light attenuation, occlusion, normal-based
- **Flood-fill Global Illumination** — lightmap ที่จำลอง indirect/diffuse lighting
- **God rays** — radial blur จาก light source จริง (ไม่ใช่ fake)
- **Auto-exposure** — ปรับความสว่างอัตโนมัติตามสภาพแสงในฉาก

### 3. Water
- **Pixel-accurate reflections** — สะท้อนตามแนวชายฝั่งที่แม่นยำระดับ pixel
- **Wading self-reflection** — ตัวละครสะท้อนในน้ำเมื่อเดินลุย
- **Ripple, sparkle, shimmer** — เอฟเฟกต์ผิวน้ำที่ตอบสนองต่อสภาพอากาศและฤดูกาล

### 4. Atmosphere & Post-Processing
- **Bloom** — luminance threshold + multi-pass Gaussian blur
- **Volumetric fog** — depth-based, density แปรผันตาม season/time
- **Tilt-shift DoF** — depth-based blur เลียนแบบ lens blur
- **Cloud shadows** — scrolling noise-based cloud mask
- **Color grading** — LUT-based + auto-mood (เวลา/อากาศ/ฤดูกาล)
- **Vignette** — ขอบมืด
- **Chromatic aberration** — ขอบสีแยก

### 5. GPU Architecture
- ใช้ **MonoGame** render targets (ไม่ใช่ custom DirectX)
- **Multi-pass rendering** — หลาย render targets ทำงานต่อเนื่องกันเป็น chain
- **Texture pool** — จัดการหน่วยความจำ GPU อย่างมีประสิทธิภาพ
- HLSL shaders คอมไพล์เป็น `.mgfxo` (MonoGame Effect Object)

---

## 🎛️ การควบคุม (Controls)

| ปุ่ม/ช่องทาง | ฟังก์ชัน |
|-------------|----------|
| **F6** | เปิด Radiance Tuner — ปรับแต่งทุก effect แบบ real-time |
| **F7** | Master toggle — เปิด/ปิด mod ทั้งหมด |
| **GMCM** | Generic Mod Config Menu — ตั้งค่าผ่าน UI ของเกม |
| **Presets** | Subtle, Cinematic, Vibrant, Off |
| **Named Profiles** | Save/Load ชุดการตั้งค่าเป็นชื่อที่กำหนดเอง |

---

## 📂 โครงสร้างไฟล์ทั้งหมด

```
SDV-Radiance/
├── .gitattributes
├── .gitignore                  # ignore bin/, obj/, *.local.md, *.local.props, *.local.ps1
├── CREDITS.md
├── LICENSE
├── manifest.json               # SMAPI manifest
├── README.md                   # หน้าเอกสารหลัก
├── SDV-Radiance.csproj         # .NET project file
│
├── assets/                     # Compiled shaders (.mgfxo)
│   ├── bloom.mgfxo
│   ├── cloudshadow.mgfxo
│   ├── colorgrade.mgfxo
│   ├── finishing.mgfxo
│   ├── floodlight.mgfxo
│   ├── fog.mgfxo
│   ├── godrays.mgfxo
│   ├── lighting.mgfxo
│   ├── tiltshift.mgfxo
│   └── water.mgfxo
│
├── docs/                       # เอกสารทางเทคนิค
│   ├── audit-2026-07-17.md
│   ├── compatibility.md
│   ├── height-framework-plan.md
│   ├── phase5b-issues.md
│   ├── phase5b-shadows-reflections.md
│   └── visual-techniques-roadmap.md
│
├── i18n/                       # ข้อความแปลภาษา
│   ├── default.json            # ภาษาอังกฤษ
│   └── th.json                 # ภาษาไทย
│
├── launch/                     # Launch configurations
│
├── shaders/                    # HLSL source code
│   ├── README.md
│   ├── bloom.fx
│   ├── cloudshadow.fx
│   ├── colorgrade.fx
│   ├── finishing.fx
│   ├── floodlight.fx
│   ├── fog.fx
│   ├── godrays.fx
│   ├── lighting.fx
│   ├── tiltshift.fx
│   └── water.fx
│
└── src/                        # C# source code
    ├── CameraSmoother.cs
    ├── FloodLightmap.cs
    ├── ModConfig.cs
    ├── ModEntry.cs
    ├── RadianceTunerMenu.cs
    ├── RenderPipeline.cs
    ├── RenderPipeline.Lighting.cs
    ├── RenderPipeline.Stages.cs
    ├── RenderPipeline.WaterMask.cs
    ├── ShadowRenderer.Baking.cs
    ├── ShadowRenderer.Characters.cs
    ├── ShadowRenderer.cs
    ├── ShadowRenderer.Objects.cs
    ├── TextEntryMenu.cs
    └── Integrations/           # โฟลเดอร์สำหรับ integrations กับ mods อื่น
```

---

## 🧪 Documentation สรุป

| ไฟล์ | เนื้อหา |
|------|--------|
| `docs/visual-techniques-roadmap.md` | แผนงานพัฒนาเทคนิคภาพในอนาคต |
| `docs/audit-2026-07-17.md` | ผลการตรวจสอบ/audit ล่าสุด |
| `docs/compatibility.md` | ความเข้ากันได้กับ mods อื่น |
| `docs/height-framework-plan.md` | แผนงาน height framework (สำหรับ shadow/lighting ที่คำนึงถึงความสูง) |
| `docs/phase5b-issues.md` | ปัญหาที่พบใน Phase 5b |
| `docs/phase5b-shadows-reflections.md` | รายละเอียดเทคนิคเงาและแสงสะท้อนใน Phase 5b |

---

## 🔑 สรุป

**SDV-Radiance** เป็น graphics mod ที่ซับซ้อนและครบวงจรที่สุดตัวหนึ่งสำหรับ Stardew Valley โดยใช้เทคนิค rendering สมัยใหม่ (deferred-style multi-pass, shadow mapping, screen-space effects, volumetric fog, god rays, water simulation) ทั้งหมดรันบน GPU ผ่าน MonoGame/HLSL โดยไม่ใช้ ray tracing หรือ path tracing — ทุกอย่างเป็น screen-space และ rasterization-based techniques ที่ปรับให้เหมาะกับเกม 2D pixel art

### Key Technologies:
- **Platform:** SMAPI 4.x + MonoGame + .NET (C#)
- **Shaders:** HLSL → `.mgfxo` (MonoGame Effect Object)
- **Rendering:** Multi-pass screen-space post-processing chain
- **Shadows:** Directional shadow mapping + PCF filtering + pre-baking
- **Lighting:** Dynamic per-light + flood-fill GI lightmap
- **Water:** Screen-space reflections + shoreline detection
- **Atmosphere:** Volumetric fog, god rays, cloud shadows, bloom, tilt-shift DoF
- **Color:** LUT-based grading + auto-mood (time/weather/season)
- **UI:** In-game tuner (F6), GMCM integration, presets, named profiles