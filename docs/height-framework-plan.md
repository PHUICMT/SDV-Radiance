# Height & Depth Framework — แผน + research (2026-07-16)

เป้าหมาย: framework mod แยก (หรือ module แยกใน Radiance ระยะแรก) ที่ให้ mod อื่น query
**ความสูง/occlusion ต่อ tile** ได้ — แก้ปัญหาที่เงา/เอฟเฟกต์ไม่รู้ว่า tile ไหนคือกำแพง/หลังคา/
สะพาน/ผิวน้ำ (เงาปีนผนัง, ไม่ตกขอบตลิ่ง, พาดผิวน้ำ).

> สถานะ: research เสร็จทั้ง 2 ฝั่ง — design/prior-art + SDV data inventory (ทั้งคู่สรุปในไฟล์นี้).
> พร้อมเริ่ม P0 เมื่อ user ไฟเขียว.

---

## ปัญหาที่ตั้งต้น

SDV เป็น 2.5D painter's-algorithm: sprite เรียงด้วย screen-Y (`depth ≈ Y/10000`) **ไม่มีแกน Z
ในข้อมูลแผนที่เลย** — ไม่มีอะไรบอกว่า "tile นี้คือสะพานยกสูงเหนือน้ำ" หรือ "หลังคาสูง 3 tiles".
ทางแก้ = สร้าง data layer แยกที่เติม Z ที่หายไป (authored หรือ inferred) แล้วเปิดเป็น API.

## Data model ที่แนะนำ (ต่อ location, lazy-build + cache, invalidate ตอน warp/map edit)

```
struct TileHeight {
    sbyte  height;        // 0 = พื้น, >0 ยกสูง (กำแพง/หลังคา/หน้าผา), <0 ต่ำลง (น้ำ/บ่อ)
    byte   occluderTop;   // ความสูงของ occluder ที่ตั้งบน tile นี้ (0 = ไม่มี) — ใช้คำนวณความยาวเงา
    EdgeFlags edges;      // ต่อด้าน: Rise/Drop N/E/S/W (ขอบขึ้น/ตลิ่งลง) เทียบเพื่อนบ้าน
    HeightClass surface;  // Ground | Water | Wall | Roof | Bridge/Pier | Void
    HeightSource source;  // Inferred | TilesheetTag | Authored (ไว้ debug)
}
```
เก็บเป็น flat array (`w*h`) ไม่ใช่ dictionary → sweep เร็ว/cache-friendly. edges precompute.

## Pipeline หา height (priority สูง→ต่ำ, ตัวล่างเป็น fallback เสมอ)

1. **Authored JSON override ต่อแผนที่** (โหลดผ่าน Content Patcher ได้) — แม่นสุด, ทำเฉพาะจุดที่ heuristic พลาด
2. **Tilesheet-index tag DB** — tag ครั้งเดียวต่อ tile index ในชีท ใช้ได้กับทุกแผนที่ที่ใช้ชีทนั้น
   (**คุ้มสุดสำหรับ SDV** เพราะ tile เดิมซ้ำเป็นร้อยแผนที่; รองรับ modded sheets ผ่าน pack)
3. **Heuristic inference จากข้อมูลที่มีอยู่แล้ว** — ใช้ได้ทุกแผนที่ทันทีรวม SVE:
   - Buildings layer (collision) = กำแพง/solid occluder
   - Front / AlwaysFront layer = ของสูง (พุ่มบน, ผนังบน, หลังคา) → height > 0
   - Tile properties: `Water` → ต่ำกว่าพื้น (จุด detect ตลิ่ง/สะพาน), `Passable`, `Type`
   - Building.tilesHigh / terrain-feature bounding boxes → occluder height
   - ทำ height-delta ระหว่างเพื่อนบ้าน → mark Rise/Drop edges

## Public API (`IHeightFrameworkApi` via `Helper.ModRegistry.GetApi<T>`)

```csharp
int   GetHeightAt(GameLocation loc, int tileX, int tileY);
bool  IsOccluder(GameLocation loc, int x, int y, out int topHeight);
bool  IsLedge(GameLocation loc, int fromX, int fromY, int toX, int toY);
HeightClass GetSurface(GameLocation loc, int x, int y);
// helper สำเร็จรูป — คืน shadow polyline ที่ clip/หักตาม heightfield แล้ว:
IReadOnlyList<ShadowSegment> ProjectShadow(loc, casterTile, casterHeight, lightDir2D, lightElevationRad);
event HeightMapChanged;               // warp / map edit
void RegisterProvider(IHeightProvider); // mod อื่น/height pack contribute ข้อมูลได้ (โมเดลเดียวกับ CP)
```
+ **debug overlay** (สไตล์ Data Layers mod) วาด height grid เป็นสี — จำเป็นทั้งตอน validate
heuristic และเป็นเครื่องมือ author.

## คณิตเงาบน heightfield (ให้ `ProjectShadow` ทำแทน consumer)

- **ความยาวเงา** = `heightDiff / tan(sunElevation)` ตาม azimuth ดวงอาทิตย์
- **Clip/หักเงา** ด้วย horizon-march / line-sweep (Sean Barrett, O(N) ต่อ sweep — เร็วพอทำทุกเฟรม
  ในขนาด viewport): เงาไต่ขึ้นหา terrain ที่สูงกว่า, หยุดเมื่อชน occluder สูงกว่า,
  **ตัดจบตรง Drop edge** (ตลิ่ง/ขอบสะพาน) แทนที่จะไหลต่อบนผิวล่าง
- ผิว `Water`/`Void` → ไม่วาดเงา (หรือวาดเป็น reflection แทน)
- แนวคิดยืนยันจาก prior art: Pixel-Height-Map shadows (arXiv 2207.05385), horizon mapping,
  RPG Maker shadow-pen / LDtk IntGrid (โมเดล authoring), Dynamic Shader (Nexus 40775 —
  เจอกำแพงเดียวกับเรา = ยืนยัน gap; **ยังไม่มี SMAPI framework ไหนให้ per-tile height เลย**
  = whitespace ของเรา)

## Phased build plan

- **P0 — Spike/validate:** heuristic inference อย่างเดียว + debug overlay; พิสูจน์ grid ถูกบน
  Farm / Town / Beach (สะพาน!) / หน้าผา. ยังไม่เปิด API.
- **P1 — MVP:** data model + inference + read-only API (`GetHeightAt`/`IsOccluder`/`GetSurface`).
  Radiance ใช้แก้ 2 บั๊กแรงสุดก่อน: เงาพาดน้ำ + ไม่ตกขอบ.
- **P2 — ProjectShadow helper:** pixel-height projection + horizon clip; เงาแดดของ Radiance
  ย้ายมาใช้.
- **P3 — Authoring:** authored JSON per-map + tilesheet tag DB; overlay กลายเป็นเครื่องมือ
  paint/export.
- **P4 — Ecosystem:** RegisterProvider + community height packs (vanilla/SVE/Ridgeside) + docs.

## SDV data inventory (verified จาก decompiled 1.6 — สิ่งที่มีจริงให้ infer)

**ยืนยัน: เกมไม่มี numeric per-tile Z เลย** — "ความสูง" ทั้งหมด emergent จาก Y-depth + layer trick.
Depth scale = `/10000`, 1 tile row = `0.0064`. สิ่งที่อ่านได้:

- **Layer bucketing** (`GameLocation.SortLayers`, จัดตาม prefix ของ Id — modded layer ก็เข้า):
  `Back*` → หลังสุด (depth 0) · `Buildings*` → วาดหลัง sprite ทุกตัว (deferred, ~0) ·
  `Front*` → **sort bias +64 (= +1 tile row) → วาดทับ sprite ที่ยืนแถวเดียวกัน = กลไก "ของสูง" ของเกม** ·
  `AlwaysFront*` → ทับทุกอย่าง · `Paths` = data-only ไม่วาด
- **Tile properties สำคัญ** (อ่าน raw `Layer.Tiles[x,y].Properties` + `.TileIndexProperties`;
  **ห้าม**ใช้ `doesTileHaveProperty` ใน inner loop — มัน iterate buildings+furniture ทุก call):
  - `Buildings` tile ที่ block (ไม่มี `Passable`/`Shadow`) = **wall base / solid occluder** ← สัญญาณหลัก
  - `Passable` บน Buildings = **raised walkable deck (สะพาน/ท่าเรือ!)** — vanilla ใช้ตัวนี้ใน
    `shouldShadowBeDrawnAboveBuildingsLayer` เพื่อยกเงา blob ขึ้น layer บน (ตรรกะเดียวกับที่เราต้องทำ)
  - `Passable` บน Back (TileIndexProperties) = ช่องว่าง/หุบ (ledge marker กลับด้าน)
  - `Water` (Back) = ระนาบต่ำกว่าพื้น; `Type`=`Wood` เหนือ Water = pier decking ชัดเจน
  - `Diggable`/`Type` Dirt|Grass|Stone (ไม่มี Buildings tile) = พื้นราบ height 0 (negative signal ดี)
  - `Shadow` (Buildings) = tile เงา baked ตกแต่ง — ต้อง exclude จาก occluders
  - ไม่มี `Bridge`/`Height`/`Elevation` property ใน vanilla; มีแค่ `SuspensionBridge`/`Farmer.onBridge` เคสเดียว
- **Building**: `tileX/Y/tilesWide/tilesHigh` = footprint (ฐาน occluder);
  ความสูงภาพ = `getSourceRect().Height*4 − tilesHigh*64` (overhang ขึ้นบน); `SortTileOffset` จาก BuildingData
- **Perf precompute**: แผนที่ใหญ่สุด ~120×120 ≈ 14k tiles × ~4 lookups = ไม่กี่ ms, ทำครั้งเดียวตอน
  LocationChanged; invalidate ตอน building constructed/demolished/moved + map reload

**Inference ruleset (ตรงกับที่ vanilla renderer ใช้เอง → เงาจะ agree กับ layering ของเกม):**
1. Buildings-block → occluder สูง ≥1; มี Front tile ต่อเนื่องด้านบน → นับจำนวนแถวเป็นความสูงกำแพง
2. Passable-on-Buildings / Wood-over-Water / onBridge → raised deck
3. Water → ต่ำกว่าพื้น; Diggable/ground Type → height 0
4. Building footprint stamp + overhang height

## การตัดสินใจสำคัญ

- height เป็น **signed** (น้ำ/บ่อเป็น first-class ไม่ใช่ hack)
- inference **เปิดตลอด** → ทุกแผนที่มีข้อมูลตั้งแต่วันแรก ไม่มีแผนที่ "ไม่รองรับ"
- tilesheet tagging คือตัวคูณความแม่นที่คุ้มสุด
- เอาคณิตยากไว้ที่เดียว (`ProjectShadow`) — consumer ไม่ต้อง reimplement
- วางเป็น **shared infrastructure** แบบเดียวกับ Content Patcher/SpaceCore ไม่ใช่ helper เฉพาะ Radiance
