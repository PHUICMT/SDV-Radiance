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
        /// <summary>Pool reach in screen pixels per vanilla radius unit. Almost every map light
        /// reports radius==1, which at the old 256 drew a tiny ~2-tile pool that never reached
        /// the ground in front of a storefront; widened to 430 so a single lamp lights a
        /// believable circle. Still scaled by the user's RadiusScale.</summary>
        private const float LampPoolReachPx = 430f;
        /// <summary>Exterior window lamp pool reach (px): reads as a bright pool on the street.</summary>
        private const float WindowPoolExteriorPx = 190f;
        /// <summary>Interior daylight-through-glass pool reach (px): a SOFT, tight wash so it
        /// never blows the window to white (vanilla's window-light already lifts the room).</summary>
        private const float WindowPoolInteriorPx = 100f;
        /// <summary>Emissive-art pool reach (px): tighter than a window — a local pool, not a wash.</summary>
        private const float EmissivePoolReachPx = 130f;

        /// <summary>
        /// Read the on-screen light sources into the shader arrays. Returns false
        /// (skipping the lighting stage) only when there's nothing to do — i.e. no
        /// lights AND no ambient darkening to apply this frame.
        /// </summary>
        private bool BuildLightList(int w, int h, ModConfig config)
        {
            _lightCount = 0;
            _lightCandidates.Clear();
            for (int i = 0; i < MaxLights; i++) { _lightPositions[i] = Vector2.Zero; _lightShaderData[i] = Vector4.Zero; }

            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);

            // Warm tint for the light pools (candle-orange at Warmth=1).
            float warmth = MathHelper.Clamp(config.LightingWarmth, 0f, 1f);
            Vector3 warm = Vector3.Lerp(Vector3.One, new Vector3(1.0f, 0.78f, 0.5f), warmth);
            float boost = MathHelper.Clamp(config.LightingBoost, 0f, 2f);
            float radiusScale = MathHelper.Clamp(config.LightingRadiusScale, 0.2f, 3f);

            // OUTDOOR lamp pools sink into full daylight the way real lamps do: a street lamp
            // at noon reads as glass, not as a glowing pool (reported: light sources should
            // not be this bright in daylight).
            // 35% at midday, full again by 08:00/17:00 — indoors untouched.
            float dayPool = 1f;
            if (Game1.currentLocation?.IsOutdoors ?? false)
                dayPool = 1f - 0.65f * (1f - MathHelper.Clamp(Math.Abs(GameClock.MinutesNow() - 750f) / 270f, 0f, 1f));
            _daylightPoolDamping = dayPool;    // emissive tiles ride the same daylight sink

            var lights = Game1.currentLightSources;
            if (lights != null && lights.Count > 0)
            {
                GameLocation? lightLocation = Game1.currentLocation;
                foreach (var kv in lights)
                {
                    if (_lightCandidates.Count >= MaxLightCandidates)
                        break;

                    LightSource ls = kv.Value;
                    if (lightLocation != null && !ShadowRenderer.WindowGlowing(lightLocation, ls))
                        continue;   // stale/dark window light — not emitting
                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, ls.position.Value);
                    float u = local.X / vw;
                    float v = local.Y / vh;

                    float radiusUv = ls.radius.Value * LampPoolReachPx / vh * radiusScale;
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
                    bool coolDaylight = lightLocation != null && !lightLocation.IsOutdoors
                        && ls.lightContext.Value == LightSource.LightContext.WindowLight;
                    Vector3 tone = coolDaylight
                        ? Vector3.Lerp(Vector3.One, new Vector3(0.82f, 0.92f, 1.12f), warmth)
                        : warm;
                    glow *= tone * boost * dayPool * ShadowRenderer.FireFlicker(ls.position.Value, ls.textureIndex.Value);

                    AddLightCandidate(new Vector2(u, v), new Vector4(glow, Math.Max(0.02f, radiusUv)));
                }
            }

            // LABELED WINDOWS (HF class 12): warm interior glow that fades in at night, added
            // as extra light sources so the existing lighting/flood pipeline lights + occludes
            // them like any lamp. Cached per location so the map scan runs once.
            if (Game1.currentLocation != null)
            {
                EnsureWindowCache(Game1.currentLocation);
                AddWindowLights(vw, vh, boost);
                EnsureEmissiveCache(Game1.currentLocation);
                AddEmissiveLights(vw, vh, boost);
            }

            SelectLights();

            // Run the stage if we have lights, or if we're darkening a flat interior
            // (so the room actually gets darker even with no lamps in view).
            bool darkening = ComputeLightingAmbient(config) != Vector3.One;

            // Diagnose the "fireplace/lamp casts a shadow but emits no visible light pool" report:
            // our pools only lift a DARKENED base, so if a room has lights yet isn't being
            // darkened (non-white ambient), the pools are invisible. Log that case once.
            if (config.DebugLogging && !_loggedLightDiag && _lightCount > 0)
            {
                _loggedLightDiag = true;
                _monitor.Log($"[light] location={Game1.currentLocation?.Name} outdoors={Game1.currentLocation?.IsOutdoors} " +
                             $"ambient={Game1.ambientLight} darkening={darkening} lights={_lightCount} " +
                             (darkening ? "(pools should show)" : "-> NOT darkening, so light pools won't be visible"), LogLevel.Debug);
            }

            return _lightCount > 0 || darkening;
        }

        /// <summary>Lights collected this frame before the shader's fixed-size array forces a
        /// choice. Bounded so a pathological map cannot make the sort itself the cost.</summary>
        private readonly List<(Vector2 Uv, Vector4 Data)> _lightCandidates = new();
        private const int MaxLightCandidates = 96;

        private void AddLightCandidate(Vector2 uv, Vector4 data)
        {
            if (_lightCandidates.Count < MaxLightCandidates)
                _lightCandidates.Add((uv, data));
        }

        /// <summary>How much this light can actually matter to the picture: how bright it is, how
        /// far it reaches, and whether that reach lands on screen at all.</summary>
        private static float Relevance(Vector2 uv, Vector4 data)
        {
            float lum = 0.2126f * data.X + 0.7152f * data.Y + 0.0722f * data.Z;
            float reach = Math.Max(0.02f, data.W);
            float dx = Math.Max(0f, Math.Max(-uv.X, uv.X - 1f));
            float dy = Math.Max(0f, Math.Max(-uv.Y, uv.Y - 1f));
            float outside = (float)Math.Sqrt(dx * dx + dy * dy);      // 0 while the centre is on screen
            return lum * reach * MathHelper.Clamp(1f - outside / reach, 0f, 1f);
        }

        /// <summary>
        /// Fill the shader's light slots with the lights that matter most.
        /// <para>
        /// The slots are a fixed-size uniform array the pixel shader loops over for every pixel,
        /// so a cap is real. What was not real was WHICH lights got cut: the three sources filled
        /// in a fixed order - the game's own lights, then labelled windows, then emissive tiles -
        /// and each simply stopped at the cap. Whatever the location's light dictionary happened
        /// to enumerate first won, so a lamp right in front of the player could lose its slot to
        /// one off-screen, and walking a single tile reshuffled the set and flipped pools on and
        /// off in one frame. That is the flash. It only started biting when the new label set
        /// pushed window and emissive lights past the cap - measured going 4 to 16 while walking.
        /// </para>
        /// Ranking by what a light can contribute means the one that loses its slot is the
        /// faintest or the furthest off-screen, whose pool was invisible anyway.
        /// </summary>
        private void SelectLights()
        {
            if (_lightCandidates.Count > MaxLights)
                _lightCandidates.Sort((a, b) => Relevance(b.Uv, b.Data).CompareTo(Relevance(a.Uv, a.Data)));
            int selectedLightCount = Math.Min(_lightCandidates.Count, MaxLights);
            for (int i = 0; i < selectedLightCount; i++)
            {
                _lightPositions[i] = _lightCandidates[i].Uv;
                _lightShaderData[i] = _lightCandidates[i].Data;
            }
            _lightCount = selectedLightCount;
        }

        // ---- labeled-window glow (HF class 12) ----
        private GameLocation? _windowCacheLocation;
        private int _windowLabelVersion = -1;
        private readonly List<Vector2> _windowTiles = new();   // world-px centres of window tiles
        private static readonly string[] _windowLayerNames = { "Front", "Buildings", "Back" };

        /// <summary>Scan the whole map ONCE per location (or when labels reload) for window
        /// tiles, caching their world-pixel centres. Cheap enough as a one-off.</summary>
        private void EnsureWindowCache(GameLocation location)
        {
            var labels = LabelStore.Instance;
            int ver = labels?.Version ?? 0;
            if (ReferenceEquals(location, _windowCacheLocation) && ver == _windowLabelVersion)
                return;
            _windowCacheLocation = location; _windowLabelVersion = ver; _windowTiles.Clear();
            var layer = location?.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            // Windows are 100% label-driven: no labels loaded (version 0 = empty DB) means no window
            // can exist, so skip the whole-map scan entirely. Without this we paid a w×h×3-layer scan
            // on every location change even though it could never find anything.
            if (labels == null || layer == null || ver == 0)
                return;
            int w = layer.LayerWidth, h = layer.LayerHeight;
            _monitor.Log($"[location] window scan start: {location?.NameOrUniqueName} {w}x{h}", LogLevel.Trace);
            var windowScanStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Resolve each layer once instead of per tile: this is a w×h×3 walk.
            var winLayers = new xTile.Layers.Layer?[_windowLayerNames.Length];
            for (int i = 0; i < _windowLayerNames.Length; i++) winLayers[i] = location?.map?.GetLayer(_windowLayerNames[i]);
            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    foreach (var wl in winLayers)
                    {
                        byte[]? cls = labels.Get(wl, tx, ty);
                        if (cls == null) continue;
                        int windowPixelCount = 0;
                        for (int p = 0; p < 256; p++) if (cls[p] == 12) windowPixelCount++;
                        if (windowPixelCount >= 8) { _windowTiles.Add(new Vector2(tx * 64 + 32, ty * 64 + 32)); break; }
                    }
                }
            windowScanStopwatch.Stop();
            _monitor.Log($"[location] window scan done: {_windowTiles.Count} tiles in {windowScanStopwatch.Elapsed.TotalMilliseconds:0.0}ms", LogLevel.Trace);
        }

        /// <summary>Add on-screen window tiles as lights. OUTDOORS (a house exterior): warm lamp
        /// glow that switches on after dusk and off at a per-window "bedtime" (houses go dark as
        /// the night wears on). INDOORS: the opposite — cool daylight pours IN through the window
        /// by day and fades to nothing at night.</summary>
        private void AddWindowLights(int vw, int vh, float boost)
        {
            if (_windowTiles.Count == 0)
                return;
            bool outdoors = _windowCacheLocation?.IsOutdoors ?? true;
            // PHASE 1 = exterior windows only (getting the night-street look right first).
            // Interior daylight-through-glass is parked for a later phase — the code path
            // below stays so it's a one-line re-enable, but we skip it for now.
            if (!outdoors)
                return;
            float night = NightFactorNow();
            float day = 1f - night;
            if (night < 0.02f)
                return;   // exterior windows only glow after dusk
            float nowMin = GameClock.MinutesNow();
            float b = Math.Max(0.4f, boost);
            float radiusOut = WindowPoolExteriorPx / Math.Max(1, vh);
            float radiusIn = WindowPoolInteriorPx / Math.Max(1, vh);
            Vector3 warm = new(1.0f, 0.72f, 0.42f);          // exterior lamp behind the glass
            Vector3 cool = new(0.80f, 0.88f, 1.05f);         // daylight coming in
            bool rain = Game1.isRaining || Game1.isSnowing;
            foreach (var wp in _windowTiles)
            {
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
                if (_lightCandidates.Count >= MaxLightCandidates)
                    continue;
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, wp);
                float u = local.X / vw, v = local.Y / vh;
                if (u < -0.1f || u > 1.1f || v < -0.1f || v > 1.1f)
                    continue;   // off-screen
                AddLightCandidate(new Vector2(u, v), new Vector4(col * amt * b, Math.Max(0.02f, outdoors ? radiusOut : radiusIn)));
            }
        }

        // ---- labelled EMISSIVE art (class 6, "light source" in the studio) -----------------------
        // A forge, a neon shop sign, a glowing crystal: art that is its OWN light source. Nothing in
        // the game marks these, and no heuristic can tell a painted-orange fire from painted-orange
        // rust, so this is 100% label-driven like windows.
        //
        // The colour is READ OUT OF THE ART, not configured: a Joja sign glows blue, Clint's forge
        // glows orange, a Junimo crystal glows green, with no per-object tuning. Only the pixels the
        // label marks are sampled, which is why the guide says to paint the FLAME and not the whole
        // stove — averaging in the black iron body would wash the light out to grey.
        //
        // Unlike windows there is no night gate: a forge is lit at noon too. It just reads as more
        // at night, so the strength ramps rather than switches.
        private GameLocation? _emissiveCacheLocation;
        private int _emissiveLabelVersion = -1;
        private float _daylightPoolDamping = 1f;   // outdoor midday sink shared by lamp pools and emissive
        private readonly List<(Vector2 Pos, Vector3 Col, float Amt)> _emissiveTiles = new();
        private static readonly string[] _emissiveLayerNames = { "Front", "Buildings", "Back" };
        private const int EmitMinPixels = 6;    // below this it is a stray dab, not a light

        private void EnsureEmissiveCache(GameLocation location)
        {
            var labels = LabelStore.Instance;
            int ver = labels?.Version ?? 0;
            if (ReferenceEquals(location, _emissiveCacheLocation) && ver == _emissiveLabelVersion)
                return;
            _emissiveCacheLocation = location; _emissiveLabelVersion = ver; _emissiveTiles.Clear();
            var layer0 = location?.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            if (labels == null || layer0 == null || ver == 0 || location == null)
                return;

            int w = layer0.LayerWidth, h = layer0.LayerHeight;
            // Heaviest of the location-entry walks: every labelled candidate tile also reads its
            // ART, which is a GPU readback the first time a tilesheet is touched.
            _monitor.Log($"[location] emissive scan start: {location.NameOrUniqueName} {w}x{h}", LogLevel.Trace);
            var emissiveScanStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var emitLayers = new xTile.Layers.Layer?[_emissiveLayerNames.Length];
            for (int i = 0; i < _emissiveLayerNames.Length; i++) emitLayers[i] = location.map.GetLayer(_emissiveLayerNames[i]);

            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    foreach (var el in emitLayers)
                    {
                        byte[]? cls = labels.Get(el, tx, ty);
                        if (cls == null)
                            continue;
                        int emissivePixelCount = 0;
                        for (int p = 0; p < 256; p++) if (cls[p] == 6) emissivePixelCount++;
                        if (emissivePixelCount < EmitMinPixels)
                            continue;
                        if (SampleEmissive(el, tx, ty, cls, emissivePixelCount) is { } lit)
                        {
                            _emissiveTiles.Add((new Vector2(tx * 64 + 32, ty * 64 + 32), lit.Col, lit.Amt));
                            break;      // one light per tile: the topmost layer that carries it wins
                        }
                    }
                }
            emissiveScanStopwatch.Stop();
            _monitor.Log($"[location] emissive scan done: {_emissiveTiles.Count} tiles in {emissiveScanStopwatch.Elapsed.TotalMilliseconds:0.0}ms", LogLevel.Trace);
        }

        /// <summary>Average the ART colour of exactly the pixels the label marked emissive, hue
        /// preserved by normalising to the brightest channel (a dim red ember still emits RED, just
        /// weakly — that "weakly" is the returned amount, not a washed-out colour).</summary>
        private (Vector3 Col, float Amt)? SampleEmissive(xTile.Layers.Layer? layer, int tx, int ty, byte[] cls, int labeledEmissivePixelCount)
        {
            var tile = layer?.Tiles[tx, ty];
            if (tile?.TileSheet == null)
                return null;
            Texture2D? texture;
            try
            {
                string src = tile.TileSheet.ImageSource;
                if (!_tilesheetTextureCache.TryGetValue(src, out texture))
                {
                    try { texture = Game1.content.Load<Texture2D>(src); }
                    catch { texture = null; }
                    _tilesheetTextureCache[src] = texture;
                }
            }
            catch { return null; }
            if (texture == null)
                return null;

            Rectangle bounds;
            try
            {
                var ib = tile.TileSheet.GetTileImageBounds(tile.TileIndex);
                bounds = new Rectangle(ib.X, ib.Y, ib.Width, ib.Height);
            }
            catch { return null; }
            if (bounds.Width != 16 || bounds.Height != 16)
                return null;

            ReadTileArt(texture, bounds);            // fills _tileArtPixels from the cached whole-sheet readback
            var buf = _tileArtPixels;
            if (buf == null)
                return null;

            float r = 0, g = 0, b = 0, lum = 0;
            int sampledPixelCount = 0;
            for (int p = 0; p < 256; p++)
            {
                if (cls[p] != 6)
                    continue;
                Color c = buf[p];
                if (c.A < 40)
                    continue;                    // transparent pixel carries no colour
                r += c.R; g += c.G; b += c.B;
                lum += (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) / 255f;
                sampledPixelCount++;
            }
            if (sampledPixelCount < EmitMinPixels)
                return null;
            r /= sampledPixelCount; g /= sampledPixelCount; b /= sampledPixelCount; lum /= sampledPixelCount;
            float peak = Math.Max(1f, Math.Max(r, Math.Max(g, b)));
            var col = new Vector3(r / peak, g / peak, b / peak);
            // How much light: how bright the art is × how much of the tile glows. A four-pixel
            // pilot light must not shine like a whole forge mouth, so area counts — capped, because
            // a fully emissive tile is not eight times a quarter-emissive one.
            float area = MathHelper.Clamp(labeledEmissivePixelCount / 96f, 0.25f, 1f);
            return (col, MathHelper.Clamp(lum * area, 0f, 1f));
        }

        /// <summary>Add on-screen emissive tiles as lights. Always on — a forge burns at noon — but
        /// ramped so it reads as a glow by day and a real light source at night.</summary>
        private void AddEmissiveLights(int vw, int vh, float boost)
        {
            if (_emissiveTiles.Count == 0)
                return;
            float night = NightFactorNow();
            float scale = (0.45f + 0.55f * night) * _daylightPoolDamping;  // visible by day, dominant after
                                                                   // dark, sunk into full daylight
            float radius = EmissivePoolReachPx / Math.Max(1, vh);
            float bst = Math.Max(0.4f, boost);
            foreach (var (pos, col, amt) in _emissiveTiles)
            {
                float a = amt * scale;
                if (a < 0.02f)
                    continue;
                if (_lightCandidates.Count >= MaxLightCandidates)
                    continue;
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, pos);
                float u = local.X / vw, v = local.Y / vh;
                if (u < -0.1f || u > 1.1f || v < -0.1f || v > 1.1f)
                    continue;
                AddLightCandidate(new Vector2(u, v), new Vector4(col * a * bst, Math.Max(0.02f, radius * (0.6f + 0.4f * amt))));
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
            // Was a hard flip at 19:00 (rooms snapped darker on the tick). Ease over
            // +-10 game-minutes; the pre-dawn branch keeps its old meaning (game time
            // runs 600-2600, so it only matters for mods that stretch the clock).
            float nightRamp = Math.Max(GameClock.RampAt(1900), 1f - GameClock.RampAt(600));
            dark = MathHelper.Clamp(dark + config.LightingNightDarkness * nightRamp, 0f, 0.95f);

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
            GameLocation? location = Game1.currentLocation;
            var layer = location?.map?.GetLayer("Buildings");
            if (location == null || layer == null)
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
            if (_occluderMask != null && _occluderMaskBuildMode == 1 && startTileX == _occluderTileX && startTileY == _occluderTileY
                && _occluderMask.Width == tilesW && _occluderMask.Height == tilesH && Game1.ticks - _occluderCacheTick < 3)
            {
                _occluderTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
                _occluderWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
                _occluderMaskSize = new Vector2(tilesW, tilesH);
                return true;
            }

            if (_occluderMaskPixels == null || _occluderMaskPixels.Length < count)
                _occluderMaskPixels = new Color[count];

            bool hasAnyOccluders = false;
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool occ = tx >= 0 && ty >= 0 && tx < lw && ty < lh && layer.Tiles[tx, ty] != null;
                    if (occ) hasAnyOccluders = true;
                    _occluderMaskPixels[j * tilesW + i] = occ ? Color.White : Color.Transparent;
                }
            }

            if (!hasAnyOccluders)
                return false;

            if (_occluderMask == null || _occluderMask.Width != tilesW || _occluderMask.Height != tilesH)
            {
                _occluderMask?.Dispose();
                _occluderMask = new Texture2D(_device, tilesW, tilesH, false, SurfaceFormat.Color);
            }
            _occluderMask.SetData(_occluderMaskPixels, 0, count);
            _occluderMaskBuildMode = 1;
            _occluderTileX = startTileX;
            _occluderTileY = startTileY;
            _occluderCacheTick = Game1.ticks;

            _occluderTilesPerScreen = new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f);
            _occluderWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _occluderMaskSize = new Vector2(tilesW, tilesH);
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
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return false;
            var layer = location.map?.GetLayer("Buildings");

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
            if (_occluderMask != null && _occluderMaskBuildMode == 2 && startTileX == _occluderTileX && startTileY == _occluderTileY
                && _occluderMask.Width == tilesW && Game1.ticks - _occluderCacheTick < 3)
            {
                _occluderWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
                _occluderMaskSize = new Vector2(tilesW, tilesH);
                return true;
            }
            _occluderMaskBuildMode = 2;
            _occluderTileX = startTileX;
            _occluderTileY = startTileY;
            _occluderCacheTick = Game1.ticks;

            if (_occluderMaskPixels == null || _occluderMaskPixels.Length < count)
                _occluderMaskPixels = new Color[count];

            var surf = SurfaceMap.For(location);
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool solid;
                    if (surf != null)
                    {
                        // Walls/roofs block lamp light; decks (piers/bridges, height 1 but open)
                        // and water don't.
                        solid = surf.BlocksLight(tx, ty);
                    }
                    else
                    {
                        solid = layer != null && tx >= 0 && ty >= 0 && tx < layer.LayerWidth && ty < layer.LayerHeight
                            && layer.Tiles[tx, ty] != null;
                    }
                    byte v = solid ? (byte)255 : (byte)0;
                    _occluderMaskPixels[j * tilesW + i] = new Color(v, v, v, (byte)255);
                }
            }

            void Stamp(int tx, int ty, byte strength)
            {
                int i = tx - startTileX, j = ty - startTileY;
                if (i < 0 || i >= tilesW || j < 0 || j >= tilesH)
                    return;
                int idx = j * tilesW + i;
                if (_occluderMaskPixels[idx].R < strength)
                    _occluderMaskPixels[idx] = new Color(strength, strength, strength, (byte)255);
            }

            foreach (var kv in location.terrainFeatures.Pairs)
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
            foreach (var ltf in location.largeTerrainFeatures)
            {
                if (ltf is StardewValley.TerrainFeatures.Bush b)
                    Stamp((int)b.Tile.X, (int)b.Tile.Y, 150);
            }
            foreach (var clump in location.resourceClumps)
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
            _occluderMask.SetData(_occluderMaskPixels, 0, count);
            _occluderWorldTileOffset = new Vector2(vx / 64f, vy / 64f);
            _occluderMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }
    }
}
