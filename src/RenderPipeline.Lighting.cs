using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - LIGHT LISTS and OCCLUDER GRIDS: gathers the game's live light
    /// sources into shader arrays, and builds the per-tile occluder masks that the lighting
    /// and flood-GI shaders ray-march for per-light shadows.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>
        /// Read the on-screen light sources into the shader arrays. Returns false
        /// (skipping the lighting stage) only when there's nothing to do — i.e. no
        /// lights AND no ambient darkening to apply this frame.
        /// </summary>
        private bool BuildLightList(int w, int h, ModConfig config)
        {
            _lightCount = 0;
            for (int i = 0; i < MaxLights; i++) { _lightPos[i] = Vector2.Zero; _lightData[i] = Vector4.Zero; }

            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);

            // Warm tint for the light pools (candle-orange at Warmth=1).
            float warmth = MathHelper.Clamp(config.LightingWarmth, 0f, 1f);
            Vector3 warm = Vector3.Lerp(Vector3.One, new Vector3(1.0f, 0.78f, 0.5f), warmth);
            float boost = MathHelper.Clamp(config.LightingBoost, 0f, 2f);
            float radiusScale = MathHelper.Clamp(config.LightingRadiusScale, 0.2f, 3f);

            var lights = Game1.currentLightSources;
            if (lights != null && lights.Count > 0)
            {
                GameLocation? lloc = Game1.currentLocation;
                foreach (var kv in lights)
                {
                    if (_lightCount >= MaxLights)
                        break;

                    LightSource ls = kv.Value;
                    if (lloc != null && !ShadowRenderer.WindowGlowing(lloc, ls))
                        continue;   // stale/dark window light — not emitting
                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    float u = local.X / vw;
                    float v = local.Y / vh;

                    // Pool reach in UV height units. Almost every map light reports radius==1,
                    // which drew a tiny ~2-tile pool that never reached the ground in front of a
                    // storefront; widened (256→430) so a single lamp lights a believable circle
                    // and a shop front is actually lit. Still scaled by the user's RadiusScale.
                    float radiusUv = ls.radius.Value * 430f / vh * radiusScale;
                    if (u < -radiusUv * 2f || u > 1f + radiusUv * 2f || v < -radiusUv * 2f || v > 1f + radiusUv * 2f)
                        continue; // fully off-screen

                    // Vanilla stores light colour as the INVERSE (Black = full bright
                    // white light), so invert to get the visible glow colour.
                    Color c = ls.color.Value;
                    Vector3 glow = new(1f - c.R / 255f, 1f - c.G / 255f, 1f - c.B / 255f);
                    if (glow.LengthSquared() < 0.01f)
                        glow = Vector3.One; // pure-white source stored as black-ish
                    // Two-tone: indoor windows are daylight (cool) — everything else warm; fire
                    // lights breathe with a slow flame flicker.
                    bool coolDaylight = lloc != null && !lloc.IsOutdoors
                        && ls.lightContext.Value == LightSource.LightContext.WindowLight;
                    Vector3 tone = coolDaylight
                        ? Vector3.Lerp(Vector3.One, new Vector3(0.82f, 0.92f, 1.12f), warmth)
                        : warm;
                    glow *= tone * boost * ShadowRenderer.FireFlicker(ls.position.Value, ls.textureIndex.Value);

                    _lightPos[_lightCount] = new Vector2(u, v);
                    _lightData[_lightCount] = new Vector4(glow, Math.Max(0.02f, radiusUv));
                    _lightCount++;
                }
            }

            // LABELED WINDOWS (HF class 12): warm interior glow that fades in at night, added
            // as extra light sources so the existing lighting/flood pipeline lights + occludes
            // them like any lamp. Cached per location so the map scan runs once.
            if (Game1.currentLocation != null)
            {
                EnsureWindowCache(Game1.currentLocation);
                AddWindowLights(vw, vh, boost);
            }

            // Run the stage if we have lights, or if we're darkening a flat interior
            // (so the room actually gets darker even with no lamps in view).
            bool darkening = ComputeLightingAmbient(config) != Vector3.One;

            // Diagnose the "fireplace/lamp casts a shadow but emits no visible light pool" report:
            // our pools only lift a DARKENED base, so if a room has lights yet isn't being
            // darkened (non-white ambient), the pools are invisible. Log that case once.
            if (config.DebugLogging && !_loggedLightDiag && _lightCount > 0)
            {
                _loggedLightDiag = true;
                _monitor.Log($"[light] loc={Game1.currentLocation?.Name} outdoors={Game1.currentLocation?.IsOutdoors} " +
                             $"ambient={Game1.ambientLight} darkening={darkening} lights={_lightCount} " +
                             (darkening ? "(pools should show)" : "-> NOT darkening, so light pools won't be visible"), LogLevel.Debug);
            }

            return _lightCount > 0 || darkening;
        }

        // ---- labeled-window glow (HF class 12) ----
        private GameLocation? _windowLoc;
        private int _windowLabelVer = -1;
        private readonly List<Vector2> _windowTiles = new();   // world-px centres of window tiles
        private static readonly string[] _winLayers = { "Front", "Buildings", "Back" };

        /// <summary>Scan the whole map ONCE per location (or when labels reload) for window
        /// tiles, caching their world-pixel centres. Cheap enough as a one-off.</summary>
        private void EnsureWindowCache(GameLocation loc)
        {
            var labels = LabelStore.Instance;
            int ver = labels?.Version ?? 0;
            if (ReferenceEquals(loc, _windowLoc) && ver == _windowLabelVer)
                return;
            _windowLoc = loc; _windowLabelVer = ver; _windowTiles.Clear();
            var layer = loc?.map?.Layers.Count > 0 ? loc.map.Layers[0] : null;
            // Windows are 100% label-driven: no labels loaded (version 0 = empty DB) means no window
            // can exist, so skip the whole-map scan entirely. Without this we paid a w×h×3-layer scan
            // on every location change even though it could never find anything.
            if (labels == null || layer == null || ver == 0)
                return;
            int w = layer.LayerWidth, h = layer.LayerHeight;
            // Resolve each layer once instead of per tile: this is a w×h×3 walk.
            var winLayers = new xTile.Layers.Layer?[_winLayers.Length];
            for (int i = 0; i < _winLayers.Length; i++) winLayers[i] = loc?.map?.GetLayer(_winLayers[i]);
            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    foreach (var wl in winLayers)
                    {
                        byte[]? cls = labels.Get(wl, tx, ty);
                        if (cls == null) continue;
                        int n = 0;
                        for (int p = 0; p < 256; p++) if (cls[p] == 12) n++;
                        if (n >= 8) { _windowTiles.Add(new Vector2(tx * 64 + 32, ty * 64 + 32)); break; }
                    }
                }
        }

        /// <summary>Add on-screen window tiles as lights. OUTDOORS (a house exterior): warm lamp
        /// glow that switches on after dusk and off at a per-window "bedtime" (houses go dark as
        /// the night wears on). INDOORS: the opposite — cool daylight pours IN through the window
        /// by day and fades to nothing at night.</summary>
        private void AddWindowLights(int vw, int vh, float boost)
        {
            if (_windowTiles.Count == 0)
                return;
            bool outdoors = _windowLoc?.IsOutdoors ?? true;
            // PHASE 1 = exterior windows only (getting the night-street look right first).
            // Interior daylight-through-glass is parked for a later phase — the code path
            // below stays so it's a one-line re-enable, but we skip it for now.
            if (!outdoors)
                return;
            float night = NightFactorNow();
            float day = 1f - night;
            if (night < 0.02f)
                return;   // exterior windows only glow after dusk
            int nowMin = (Game1.timeOfDay / 100) * 60 + Game1.timeOfDay % 100;
            float b = Math.Max(0.4f, boost);
            // exterior lamp reads as a bright pool; interior daylight is a SOFT, tight wash so it
            // never blows the window to white (the vanilla window-light already lifts the room).
            float radiusOut = 190f / Math.Max(1, vh);
            float radiusIn = 100f / Math.Max(1, vh);
            Vector3 warm = new(1.0f, 0.72f, 0.42f);          // exterior lamp behind the glass
            Vector3 cool = new(0.80f, 0.88f, 1.05f);         // daylight coming in
            bool rain = Game1.isRaining || Game1.isSnowing;
            foreach (var wp in _windowTiles)
            {
                if (_lightCount >= MaxLights)
                    break;
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, wp);
                float u = local.X / vw, v = local.Y / vh;
                if (u < -0.1f || u > 1.1f || v < -0.1f || v > 1.1f)
                    continue;   // off-screen

                float amt; Vector3 col;
                if (outdoors)
                {
                    // bedtime is hashed per ~6-tile BLOCK, so all the windows of one house go
                    // dark together but different houses sleep at different times — the street
                    // dims house-by-house, not all at once. Range 21:30–25:00, then a ~1h fade.
                    int cx = ((int)wp.X - 32) / 64 / 6, cy = ((int)wp.Y - 32) / 64 / 6;
                    int hcode = (cx * 73856093) ^ (cy * 19349663);
                    int bedMin = 1290 + (Math.Abs(hcode) % 8) * 30;     // 21:30 … 25:00, 8 steps
                    float bedFade = nowMin <= bedMin ? 1f : MathHelper.Clamp(1f - (nowMin - bedMin) / 60f, 0f, 1f);
                    amt = night * bedFade;
                    col = warm;
                }
                else
                {
                    // soft daylight through the glass — kept low so it never blows the window to
                    // white (vanilla's own window light already lifts the room); dimmer in rain.
                    amt = day * (rain ? 0.28f : 0.45f);
                    col = rain ? new Vector3(0.8f, 0.84f, 0.92f) : cool;
                }
                if (amt < 0.02f)
                    continue;
                _lightPos[_lightCount] = new Vector2(u, v);
                _lightData[_lightCount] = new Vector4(col * amt * b, Math.Max(0.02f, outdoors ? radiusOut : radiusIn));
                _lightCount++;
            }
        }

        private bool _loggedLightDiag;

        /// <summary>
        /// The per-pixel ambient multiplier for unlit areas. We only darken flat-bright
        /// interiors that the game leaves unlit (its own lightmap isn't drawn there);
        /// outdoors, mines, and scripted-dark rooms already get vanilla lighting, so we
        /// return white there to avoid double-darkening.
        /// </summary>
        private static Vector3 ComputeLightingAmbient(ModConfig config)
        {
            bool outdoors = Game1.currentLocation?.IsOutdoors ?? false;
            bool vanillaLit = outdoors
                || Game1.currentLocation is StardewValley.Locations.MineShaft
                || !Game1.ambientLight.Equals(Color.White);
            if (vanillaLit)
                return Vector3.One;

            float dark = MathHelper.Clamp(config.LightingIndoorDarkness, 0f, 0.95f);
            int t = Game1.timeOfDay;
            if (t >= 1900 || t < 600)
                dark = MathHelper.Clamp(dark + config.LightingNightDarkness, 0f, 0.95f);

            // Cool moonlight-ish tint for the darkened room.
            Vector3 darkTint = new(0.45f, 0.48f, 0.62f);
            return Vector3.Lerp(Vector3.One, darkTint, dark);
        }

        /// <summary>
        /// Build a per-tile occluder mask for the visible area: a tile blocks light if
        /// the map's "Buildings" layer has a tile there (walls / built structures).
        /// Aligned to the viewport exactly like the water mask. Returns false (skipping
        /// shadows) when there are no occluders on screen.
        /// </summary>
        private bool BuildOccluderMask(int w, int h)
        {
            GameLocation? loc = Game1.currentLocation;
            var layer = loc?.map?.GetLayer("Buildings");
            if (loc == null || layer == null)
                return false;

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTileX = (int)Math.Floor(vx / 64f);
            int startTileY = (int)Math.Floor(vy / 64f);
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed out.
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + 2);
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + 2);
            int count = tilesW * tilesH;
            int lw = layer.LayerWidth, lh = layer.LayerHeight;

            // Same tile-cross + 3-tick throttle as the flood occluder path (which had it; the classic
            // path rebuilt the grid and re-uploaded the texture every single frame). Mode-gated so a
            // flood↔classic config switch never reuses the other builder's mask content.
            if (_occluderMask != null && _occMaskMode == 1 && startTileX == _occTx && startTileY == _occTy
                && _occluderMask.Width == tilesW && _occluderMask.Height == tilesH && Game1.ticks - _occTick < 3)
            {
                _occTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
                _occWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
                _occMaskSize = new Vector2(tilesW, tilesH);
                return true;
            }

            if (_occluderMaskBuf == null || _occluderMaskBuf.Length < count)
                _occluderMaskBuf = new Color[count];

            bool any = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool occ = tx >= 0 && ty >= 0 && tx < lw && ty < lh && layer.Tiles[tx, ty] != null;
                    if (occ) any = true;
                    _occluderMaskBuf[j * tilesW + i] = occ ? Color.White : Color.Transparent;
                }
            }

            if (!any)
                return false;

            if (_occluderMask == null || _occluderMask.Width != tilesW || _occluderMask.Height != tilesH)
            {
                _occluderMask?.Dispose();
                _occluderMask = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _occluderMask.SetData(_occluderMaskBuf, 0, count);
            _occMaskMode = 1;
            _occTx = startTileX;
            _occTy = startTileY;
            _occTick = Game1.ticks;

            _occTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
            _occWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _occMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }

        /// <summary>
        /// Occluder mask for FLOOD lighting's per-light shadows — richer than the classic
        /// Buildings-layer mask: Height Framework walls/buildings (fallback: Buildings layer),
        /// tree trunks, resource clumps, bushes, and characters/animals, each with an occlusion
        /// WEIGHT in the red channel (entities are partial blockers → softer shadows).
        /// </summary>
        private bool BuildFloodOccluders(int w, int h)
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc == null)
                return false;
            var layer = loc.map?.GetLayer("Buildings");

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTileX = (int)Math.Floor(vx / 64f);
            int startTileY = (int)Math.Floor(vy / 64f);
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed out.
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + 2);
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + 2);
            int count = tilesW * tilesH;

            // Same throttle as the flood lightmap: ~900 cross-mod tile lookups per build is
            // real money, and the occluder grid only shifts when the view crosses a tile (the
            // 3-tick refresh keeps moving NPC stamps fresh enough for a soft shadow).
            if (_occluderMask != null && _occMaskMode == 2 && startTileX == _occTx && startTileY == _occTy
                && _occluderMask.Width == tilesW && Game1.ticks - _occTick < 3)
            {
                _occWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
                _occMaskSize = new Vector2(tilesW, tilesH);
                return true;
            }
            _occMaskMode = 2;
            _occTx = startTileX;
            _occTy = startTileY;
            _occTick = Game1.ticks;

            if (_occluderMaskBuf == null || _occluderMaskBuf.Length < count)
                _occluderMaskBuf = new Color[count];

            var hf = ShadowRenderer.Height;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool solid;
                    if (hf != null)
                    {
                        // Walls/roofs block lamp light; decks (piers/bridges, height 1 but open)
                        // and water don't.
                        try { int cls = hf.GetSurfaceAt(loc, tx, ty); solid = cls == 2 || cls == 3; }
                        catch { hf = null; solid = false; }
                    }
                    else
                    {
                        solid = layer != null && tx >= 0 && ty >= 0 && tx < layer.LayerWidth && ty < layer.LayerHeight
                            && layer.Tiles[tx, ty] != null;
                    }
                    byte v = solid ? (byte)255 : (byte)0;
                    _occluderMaskBuf[j * tilesW + i] = new Color(v, v, v, (byte)255);
                }
            }

            void Stamp(int tx, int ty, byte strength)
            {
                int i = tx - startTileX, j = ty - startTileY;
                if (i < 0 || i >= tilesW || j < 0 || j >= tilesH)
                    return;
                int idx = j * tilesW + i;
                if (_occluderMaskBuf[idx].R < strength)
                    _occluderMaskBuf[idx] = new Color(strength, strength, strength, (byte)255);
            }

            foreach (var kv in loc.terrainFeatures.Pairs)
            {
                switch (kv.Value)
                {
                    case StardewValley.TerrainFeatures.Tree t when t.growthStage.Value >= 5:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, 215);
                        break;
                    case StardewValley.TerrainFeatures.FruitTree ft when ft.growthStage.Value >= 4:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, 215);
                        break;
                    case StardewValley.TerrainFeatures.Bush:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, 150);
                        break;
                }
            }
            foreach (var ltf in loc.largeTerrainFeatures)
            {
                if (ltf is StardewValley.TerrainFeatures.Bush b)
                    Stamp((int)b.Tile.X, (int)b.Tile.Y, 150);
            }
            foreach (var clump in loc.resourceClumps)
            {
                if (clump == null) continue;
                for (int cy = 0; cy < clump.height.Value; cy++)
                    for (int cx = 0; cx < clump.width.Value; cx++)
                        Stamp((int)clump.Tile.X + cx, (int)clump.Tile.Y + cy, 200);
            }
            // Characters/animals/the player are NOT stamped: their shadows are owned by the
            // sprite silhouette pass — stamping them here too gave everyone standing near a
            // lamp a second blurry dark blotch on top of their cast shadow.

            if (_occluderMask == null || _occluderMask.Width != tilesW || _occluderMask.Height != tilesH)
            {
                _occluderMask?.Dispose();
                _occluderMask = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _occluderMask.SetData(_occluderMaskBuf, 0, count);
            _occWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _occMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }
    }
}
