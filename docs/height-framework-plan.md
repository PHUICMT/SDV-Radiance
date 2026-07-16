# Height & Depth Framework — plan + research (2026-07-16)

Goal: a standalone framework mod (**separate repo**; Radiance becomes its first consumer)
that lets any mod query **per-tile height / occlusion** — fixing the class of bugs where
effects don't know which tile is a wall/roof/pier/water surface (shadows climbing walls,
not dropping off ledges, spilling across water).

> Status: research complete on both tracks — design/prior-art survey + verified SDV data
> inventory (both summarized below). Ready to start P0 on user go-ahead.

---

## Problem statement

SDV is a 2.5D painter's-algorithm renderer: sprites sort by screen-Y (`depth ≈ Y/10000`),
and **the map data has no Z axis at all** — nothing says "this tile is a pier raised over
water" or "this roof is 3 tiles up". The fix is a separate data layer that supplies the
missing Z (authored or inferred), exposed through a public API.

## Recommended data model (per location; lazy-built, cached, invalidated on warp/map edit)

```
struct TileHeight {
    sbyte  height;        // 0 = ground, >0 raised (wall/roof/cliff-top), <0 sunken (water/pit)
    byte   occluderTop;   // vertical extent of an occluder standing on this tile (0 = none) — drives shadow length
    EdgeFlags edges;      // per side: Rise/Drop N/E/S/W (upward step / ledge) vs neighbor
    HeightClass surface;  // Ground | Water | Wall | Roof | Bridge/Pier | Void
    HeightSource source;  // Inferred | TilesheetTag | Authored (debugging/trust)
}
```
Stored as a flat array (`w*h`), not a dictionary → fast, cache-friendly sweeps. Edges precomputed.

## Height resolution pipeline (highest priority wins; lowest is always-on fallback)

1. **Authored JSON override per map** (Content Patcher–loadable) — most accurate; only for spots heuristics miss
2. **Tilesheet-index tag DB** — tag a tile index once per sheet, applies to every map using that sheet
   (**highest-leverage option for SDV** — the same tiles recur across hundreds of maps; modded sheets covered via packs)
3. **Heuristic inference from existing data** — works on every map immediately, including SVE:
   - Buildings-layer tile that blocks = wall/solid occluder
   - Front / AlwaysFront layer = tall geometry (canopy, upper wall, roof) → height > 0
   - Tile properties: `Water` → below-ground plane (ledge/pier detection), `Passable`, `Type`
   - `Building.tilesHigh` / terrain-feature bounding boxes → occluder heights
   - Neighbor height deltas → Rise/Drop edge flags

## Public API (`IHeightFrameworkApi` via `Helper.ModRegistry.GetApi<T>`)

```csharp
int   GetHeightAt(GameLocation loc, int tileX, int tileY);
bool  IsOccluder(GameLocation loc, int x, int y, out int topHeight);
bool  IsLedge(GameLocation loc, int fromX, int fromY, int toX, int toY);
HeightClass GetSurface(GameLocation loc, int x, int y);
// done-for-you helper — returns a shadow polyline already clipped/bent over the heightfield:
IReadOnlyList<ShadowSegment> ProjectShadow(loc, casterTile, casterHeight, lightDir2D, lightElevationRad);
event HeightMapChanged;                 // warp / map edit
void RegisterProvider(IHeightProvider); // other mods / height packs contribute data (CP-style)
```
Plus a **debug overlay** (Data Layers–style) rendering the height grid as colors — essential
both for validating inference and as an authoring tool.

## Shadow math on the heightfield (lives inside `ProjectShadow`, not in consumers)

- **Shadow length** = `heightDiff / tan(sunElevation)` along the sun azimuth
- **Clip/bend** via horizon-march / line-sweep (Sean Barrett, O(N) per sweep — cheap enough
  per frame at viewport size): the shadow climbs toward higher terrain, stops at a taller
  occluder, and **terminates at a Drop edge** (ledge/pier rim) instead of continuing on the
  lower surface
- `Water`/`Void` surfaces → no shadow (or a displaced reflection instead)
- Prior art confirming the approach: Pixel-Height-Map shadows (arXiv 2207.05385), horizon
  mapping, RPG Maker shadow-pen / LDtk IntGrid (authoring models), Dynamic Shader (Nexus
  40775 — hits the same wall we do, confirming the gap; **no SMAPI framework provides
  per-tile height today** — that's our whitespace)

## SDV data inventory (verified from decompiled 1.6 — what actually exists to infer from)

**Confirmed: the game has NO numeric per-tile Z anywhere** — all "height" is emergent from
Y-depth + a layer trick. Depth scale = `/10000`, 1 tile row = `0.0064`. What we can read:

- **Layer bucketing** (`GameLocation.SortLayers`, by layer-Id prefix — modded layers included):
  `Back*` → behind everything (depth 0) · `Buildings*` → drawn behind all sprites (deferred, ~0) ·
  `Front*` → **sort bias +64 (= +1 tile row) → draws over sprites standing on that row = the
  game's own "tall things" mechanism** · `AlwaysFront*` → on top of everything · `Paths` = data-only, never drawn
- **Key tile properties** (read raw `Layer.Tiles[x,y].Properties` + `.TileIndexProperties`;
  do **NOT** call `doesTileHaveProperty` in the inner loop — it iterates all buildings+furniture per call):
  - Buildings tile that blocks (no `Passable`/`Shadow`) = **wall base / solid occluder** ← primary signal
  - `Passable` on Buildings = **raised walkable deck (bridge/pier!)** — vanilla uses this in
    `shouldShadowBeDrawnAboveBuildingsLayer` to promote blob shadows (exactly the logic we need)
  - `Passable` on Back (TileIndexProperties) = gap/void (inverse ledge marker)
  - `Water` (Back) = below-ground plane; `Type=Wood` over Water = pier decking, a strong signal
  - `Diggable` / `Type` Dirt|Grass|Stone with no Buildings tile = flat ground, height 0 (good negative signal)
  - `Shadow` (Buildings) = decorative baked-shadow tile — must be excluded from occluders
  - No `Bridge`/`Height`/`Elevation` property exists in vanilla; the only first-class raised-deck
    flag is `SuspensionBridge` / `Farmer.onBridge` (one special case)
- **Buildings**: `tileX/Y/tilesWide/tilesHigh` = footprint (occluder base);
  visual height = `getSourceRect().Height*4 − tilesHigh*64` (upward overhang); `SortTileOffset` from BuildingData
- **Precompute cost**: largest maps ~120×120 ≈ 14k tiles × ~4 lookups = a few ms, once per
  LocationChanged; invalidate on building constructed/demolished/moved + map reload

**Inference ruleset (mirrors what the vanilla renderer itself relies on → our shadows will
agree with the game's own layering):**
1. Buildings-blocking tile → occluder height ≥ 1; contiguous Front tiles above it → count rows = wall height
2. Passable-on-Buildings / Wood-over-Water / onBridge → raised deck
3. Water → below ground; Diggable/ground Type → height 0
4. Building footprint stamp + overhang height

## Phased build plan

- **P0 — Spike/validate:** heuristic inference only + debug overlay; prove the grid looks right
  on Farm / Town / Beach (the pier!) / a cliff map. No public API yet.
- **P1 — MVP:** data model + inference + read-only API (`GetHeightAt`/`IsOccluder`/`GetSurface`).
  Radiance consumes it to fix the two worst bugs first: shadows on water + no ledge drop-off.
- **P2 — ProjectShadow helper:** pixel-height projection + horizon clipping; Radiance's sun
  shadow migrates onto it.
- **P3 — Authoring:** per-map authored JSON + tilesheet tag DB; the overlay becomes a
  paint/export authoring aid.
- **P4 — Ecosystem:** RegisterProvider + community height packs (vanilla/SVE/Ridgeside) + docs.

## Key decisions

- **Separate repo** from SDV-Radiance — positioned as shared infrastructure (like Content
  Patcher/SpaceCore), not a Radiance-only helper; Radiance is consumer #1
- Height is **signed** (water/pits are first-class, not a hack)
- Inference is **always on** → every map has data from day one; no "unsupported map"
- Tilesheet tagging is the accuracy multiplier with the best effort/coverage ratio
- The hard math lives in one place (`ProjectShadow`) — consumers never reimplement it
