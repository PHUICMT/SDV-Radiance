using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

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
            for (int i = 0; i < MaxLights; i++) { _lightPositions[i] = Vector2.Zero; _lightShaderData[i] = Vector4.Zero; _lightIsFire[i] = 0f; }

            int vw = Math.Max(1, Game1.viewport.Width);
            int vh = Math.Max(1, Game1.viewport.Height);
            _lightAspect = vw / (float)vh;

            // Warm tint for the light pools (candle-orange at Warmth=1).
            float warmth = MathHelper.Clamp(config.LightingWarmth, 0f, 1f);
            Vector3 warm = Vector3.Lerp(Vector3.One, new Vector3(1.0f, 0.78f, 0.5f), warmth);
            float boost = MathHelper.Clamp(config.LightingBoost, 0f, 2f);
            float radiusScale = MathHelper.Clamp(config.LightingRadiusScale, 0.2f, 3f);

            // OUTDOOR lamp pools sink into full daylight the way real lamps do: a street lamp
            // at noon reads as glass, not as a glowing pool (reported: light sources should
            // not be this bright in daylight).
            // 35% at midday, full again by 08:00/17:00 — indoors untouched.
            float dayPool = OutdoorLampDaylightDamping();
            _daylightPoolDamping = dayPool;    // emissive tiles ride the same daylight sink

            GameLocation? lightLocation = Game1.currentLocation;
            long lightStep = ChainStepBegin();
            GatherGameLights(lightLocation);
            ChainStepEnd(ChainStep.LightGather, lightStep);
            lightStep = ChainStepBegin();
            // HOW HARD A FLAME BREATHES DEPENDS ON HOW MANY FLAMES ARE IN THE ROOM.
            //
            // The wobble was written for a hearth, where a room quietly pulsing with one fire is
            // the whole charm of it. The saloon has two dozen wall sconces, and a sconce carries
            // the same texture index as a fireplace, so all of them breathed at eight percent, out
            // of phase with each other, sixty times a second. Measured with radiance_lightwatch
            // standing perfectly still in that room: ten to seventeen of the twenty-four slots
            // changed value on almost every single frame, and it was reported as the lights
            // flickering while walking around, which is where anyone would notice it.
            //
            // Independent wobbles average out rather than pile up, so the amplitude comes down as
            // the square root of how many of them share the screen. One fire keeps every bit of
            // its flicker. Twenty-four keep a fifth of it each, and the room stops shimmering
            // while each fire still moves.
            int flameCount = 0;
            foreach (var g in _gatheredLights)
                if (g.TextureIndex == 4 || g.TextureIndex == 5)
                    flameCount++;
            float flickerShare = 1f / (float)Math.Sqrt(Math.Max(1, flameCount));
            foreach (var src in _gatheredLights)
            {
                if (_lightCandidates.Count >= MaxLightCandidates)
                    break;

                // A fireplace's flames are not where the game hangs its light: see FlameGlowOffset.
                Vector2 glowPosition = src.Position
                    + ShadowRenderer.FlameGlowOffset(lightLocation, src.Position, src.TextureIndex);
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, glowPosition);
                float u = local.X / vw;
                float v = local.Y / vh;

                // Capped: the reach grows with the game's radius, which is right for lamps (1 to
                // 2.5) and absurd past them. A glow ring is radius 10, and 5.5 screen heights of
                // pool with the gentle falloff below lit two thirds of the whole frame from the
                // player's hand, so its per-light shadows drew wedges in ground the game shows
                // as night. The game's own glow for it reaches about two thirds of a screen.
                float radiusUv = Math.Min(src.Radius * LampPoolReachPx / vh * radiusScale, LampPoolReachCapUv);
                if (u < -radiusUv * 2f || u > 1f + radiusUv * 2f || v < -radiusUv * 2f || v > 1f + radiusUv * 2f)
                    continue; // fully off-screen

                // Vanilla stores light colour as the INVERSE (Black = full bright
                // white light), so invert to get the visible glow colour.
                Color c = src.Colour;
                Vector3 glow = new(1f - c.R / 255f, 1f - c.G / 255f, 1f - c.B / 255f);
                if (glow.LengthSquared() < 0.01f)
                    glow = Vector3.One; // pure-white source stored as black-ish
                // Two-tone: indoor windows are daylight (cool) — everything else warm; fire
                // lights breathe with a slow flame flicker.
                bool coolDaylight = lightLocation != null && !lightLocation.IsOutdoors && src.IsWindow;
                Vector3 tone = coolDaylight
                    ? Vector3.Lerp(Vector3.One, new Vector3(0.82f, 0.92f, 1.12f), warmth)
                    : warm;
                glow *= tone * boost * dayPool;
                // The flicker is carried SEPARATELY and applied after the array is chosen.
                // Folding it in here made a flickering quantity decide the ranking, and with
                // a room offering three times as many lights as there are slots the scores
                // sit close enough together that an eight percent wobble reorders the list
                // around the cut. The marginal lights then swung from full to nothing and
                // back on the flame's own cycle: a hearth quietly breathing turned into half
                // the room's lamps pulsing. Exactly the trap already documented in the shadow
                // path, where a flickering reach made casters blink in and out.
                // A fire lights further than a bulb of the same nominal radius: it is taller than
                // a point, it flickers past its own edge, and the eye expects a hearth to own the
                // half of the room in front of it. The game hands both the same radius, so give
                // flames a fifth more reach here rather than asking players to move a global
                // slider that would swell every porch lamp with it.
                bool isFire = src.TextureIndex == 4 || src.TextureIndex == 5;
                if (isFire)
                    radiusUv *= 1.35f;
                AddLightCandidate(new Vector2(u, v), new Vector4(glow, Math.Max(0.02f, radiusUv)),
                    MathHelper.Lerp(1f, ShadowRenderer.FireFlicker(src.Position, src.TextureIndex), flickerShare), src.Id,
                    fire: isFire);
            }

            // LABELED WINDOWS (HF class 12): warm interior glow that fades in at night, added
            // as extra light sources so the existing lighting/flood pipeline lights + occludes
            // them like any lamp. Cached per location so the map scan runs once.
            if (Game1.currentLocation != null)
            {
                ChainStepEnd(ChainStep.LightCandidates, lightStep);
                lightStep = ChainStepBegin();
                EnsureWindowCache(Game1.currentLocation);
                AddWindowLights(vw, vh, boost, config);
                ChainStepEnd(ChainStep.LightWindows, lightStep);
                lightStep = ChainStepBegin();
                EnsureEmissiveCache(Game1.currentLocation);
                AddEmissiveLights(vw, vh, boost);
                ChainStepEnd(ChainStep.LightEmissive, lightStep);
                lightStep = ChainStepBegin();
            }

            ChainStepEnd(ChainStep.LightSelect, lightStep);
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

        /// <summary>
        /// How much of a lamp survives the daylight outdoors, 1 to 0.35 and back.
        /// </summary>
        /// <remarks>A street lamp at noon reads as glass, not as a glowing pool, and it was
        /// reported that way. Indoors nothing is damped. Its own method because the glass returns
        /// the lamps as brightly as the ground shows them, and two copies of this ramp would drift
        /// apart the first time either was tuned.</remarks>
        private static float OutdoorLampDaylightDamping()
        {
            if (!(Game1.currentLocation?.IsOutdoors ?? false))
                return 1f;
            return 1f - 0.65f * (1f - MathHelper.Clamp(Math.Abs(GameClock.MinutesNow() - 750f) / 270f, 0f, 1f));
        }

        /// <summary>One light as the rest of this file sees it, which is not always one light as
        /// the game sees it: a neighbourhood of the map's own evenly spaced lamps arrives here as
        /// a single wider source.</summary>
        private struct GatheredLight
        {
            public Vector2 Position;
            public Color Colour;
            public float Radius;
            public int TextureIndex;
            public bool IsWindow;
            public int Id;
        }

        private readonly List<GatheredLight> _gatheredLights = new();
        /// <summary>Accumulator per neighbourhood while gathering: the box its members occupy, so
        /// the merged light can be centred on them and widened to cover them all.</summary>
        /// <summary>Neighbourhood key to its slot in <see cref="_clusterBoxes"/>. NOT a slot in
        /// <see cref="_gatheredLights"/>: only map lights make a box, every light makes a gathered
        /// entry, so the two lists run at different lengths the moment anything carried or placed
        /// is in the room. Holding one index and using it on both read a stranger's box and then
        /// ran off the end of the list, which a glow ring was enough to trigger. The box carries
        /// the index of the light it belongs to instead.</summary>
        private readonly Dictionary<long, int> _clusterSlotByCell = new();
        private readonly List<(int Light, float MinX, float MinY, float MaxX, float MaxY, float MaxRadius, int Count)> _clusterBoxes = new();

        /// <summary>
        /// Turn the location's light sources into the list this pass will rank, merging the map's
        /// own lamps by neighbourhood on the way through.
        ///
        /// <para>
        /// A vanilla saloon carries SIXTY FOUR map lights, all at radius 1, laid a couple of tiles
        /// apart. Radius 1 already reaches about six and a half tiles, so those pools overlap almost
        /// completely: it is not sixty four lamps, it is one even wash that the map paints by
        /// repeating a light. The shader has twenty four slots. Feeding it sixty four all but
        /// guarantees that most of a room goes without, and no amount of care about WHICH
        /// twenty four win can conjure the other forty back.
        /// </para>
        ///
        /// <para>
        /// Merging is the only thing that adds coverage rather than moving it around. A
        /// neighbourhood becomes one light centred on the box its members occupy and widened to
        /// reach past the farthest of them, which is close to the union of what they drew, and the
        /// saloon comes down from sixty four to about twenty five.
        /// </para>
        ///
        /// <para>
        /// Only MAP lights merge. Anything carried moves, and a moving light merged by position
        /// would change which neighbourhood it belongs to as it walked, renaming itself every few
        /// tiles: the exact fault that made a glow ring dark. Placed lamps and fires keep their own
        /// identity too, because those are things a player put somewhere on purpose.
        /// </para>
        /// </summary>
        private void GatherGameLights(GameLocation? location)
        {
            _gatheredLights.Clear();
            _clusterSlotByCell.Clear();
            _clusterBoxes.Clear();
            var lights = Game1.currentLightSources;
            if (lights == null)
                return;

            const float cellPx = ClusterCellTiles * 64f;
            foreach (var kv in lights)
            {
                LightSource ls = kv.Value;
                if (location != null && !ShadowRenderer.WindowGlowing(location, ls))
                    continue;   // stale/dark window light — not emitting
                Vector2 pos = ls.position.Value;
                Color colour = ls.color.Value;
                float radius = ls.radius.Value;
                bool isWindow = ls.lightContext.Value == LightSource.LightContext.WindowLight;

                if (ls.lightContext.Value != LightSource.LightContext.MapLight)
                {
                    _gatheredLights.Add(new GatheredLight
                    {
                        Position = pos,
                        Colour = colour,
                        Radius = radius,
                        TextureIndex = ls.textureIndex.Value,
                        IsWindow = isWindow,
                        Id = StableLightId(kv.Key.ToString() ?? string.Empty),
                    });
                    continue;
                }

                // The neighbourhood is a fixed grid in WORLD space, not a clustering of whatever
                // happens to be on screen. A light's cell therefore never changes, so a merged
                // light keeps the same name and the same place no matter where the camera is.
                int cellX = (int)Math.Floor(pos.X / cellPx);
                int cellY = (int)Math.Floor(pos.Y / cellPx);
                // Colour and radius join the key so a hearth is never averaged into a wall lamp.
                long key = ((long)(cellX & 0xFFFF) << 48) | ((long)(cellY & 0xFFFF) << 32)
                         | ((long)(colour.R >> 5) << 27) | ((long)(colour.G >> 5) << 22) | ((long)(colour.B >> 5) << 17)
                         | (long)(int)MathHelper.Clamp(radius * 4f, 0f, 255f);

                if (_clusterSlotByCell.TryGetValue(key, out int slot))
                {
                    var box = _clusterBoxes[slot];
                    _clusterBoxes[slot] = (box.Light,
                                           Math.Min(box.MinX, pos.X), Math.Min(box.MinY, pos.Y),
                                           Math.Max(box.MaxX, pos.X), Math.Max(box.MaxY, pos.Y),
                                           Math.Max(box.MaxRadius, radius), box.Count + 1);
                    continue;
                }
                _clusterSlotByCell[key] = _clusterBoxes.Count;
                _clusterBoxes.Add((_gatheredLights.Count, pos.X, pos.Y, pos.X, pos.Y, radius, 1));
                _gatheredLights.Add(new GatheredLight
                {
                    Position = pos,
                    Colour = colour,
                    Radius = radius,
                    TextureIndex = ls.textureIndex.Value,
                    IsWindow = isWindow,
                    // Named after the neighbourhood, not the member that happened to arrive first,
                    // so the merged light is the same light next frame however the game enumerates.
                    Id = ClusterLightId(key),
                });
            }

            // Second pass: centre each merged light on its members and widen it to reach past the
            // farthest one, so the patch it replaces stays covered.
            foreach (var box in _clusterBoxes)
            {
                if (box.Count <= 1)
                    continue;
                float spanX = box.MaxX - box.MinX, spanY = box.MaxY - box.MinY;
                var merged = _gatheredLights[box.Light];
                merged.Position = new Vector2((box.MinX + box.MaxX) * 0.5f, (box.MinY + box.MaxY) * 0.5f);
                merged.Radius = box.MaxRadius
                    + 0.5f * (float)Math.Sqrt(spanX * spanX + spanY * spanY) / LampPoolReachPx;
                _gatheredLights[box.Light] = merged;
            }
        }

        /// <summary>How wide a neighbourhood is, in tiles, when merging the map's own lamps. Six
        /// is comfortably inside the six and a half tiles a single radius-1 pool already reaches,
        /// so every lamp in a neighbourhood still stands inside the light that replaces it.</summary>
        private const float ClusterCellTiles = 6f;

        /// <summary>A merged light's name, from the neighbourhood it stands for. Salted away from
        /// both the position hashes and the per-source names so the three cannot collide.</summary>
        private static int ClusterLightId(long cellKey)
        {
            int id = (int)(cellKey ^ (cellKey >> 32)) ^ 0x2f1e4a7b;
            return id == 0 ? 1 : id;
        }

        /// <summary>Lights collected this frame before the shader's fixed-size array forces a
        /// choice. Bounded so a pathological map cannot make the sort itself the cost.</summary>
        private readonly List<(Vector2 Uv, Vector4 Data, int Id, Vector2 World, float Flick, bool Fire)> _lightCandidates = new();
        private const int MaxLightCandidates = 96;
        /// <summary>The point past which the ranking actually decides something: the flood
        /// gives its first eight a shadow ray and pools the rest, so from eight onward the
        /// order on the list changes what the player sees.</summary>
        private const int ShaderLightSlots = 8;
        /// <summary>Longest pool any one light may lay, in screen heights. See the radius line
        /// in BuildLightList: past this the game's radius stops meaning "how far it lights".</summary>
        private const float LampPoolReachCapUv = 1.2f;

        /// <param name="flick">Per-frame flame wobble, kept OUT of the ranking and multiplied in
        /// only once the array is settled. Steady lights pass 1.</param>
        /// <param name="stableId">The light's own name, when it has one that survives moving. The
        /// game's light sources do: the key the location files them under. Zero means "name it
        /// after where it stands", which is correct for a window or a glowing tile and was wrong
        /// for everything else. See below.</param>
        /// <param name="fire">Whether this is an actual FLAME rather than any other bright thing.
        /// The game already tells us, and has all along, through the texture it picks for the
        /// glow: 4 is the sconce sheet shared by torches, wall lamps and fireplaces, 5 is the
        /// cauldron. The flicker has read it since the beginning; nothing else did, so the shader
        /// was being handed a lamp, a window, a glowing crystal and a hearth as if they were the
        /// same kind of thing. They are not, and the one place it matters is deciding what is
        /// allowed to burn brighter than its own art.</param>
        private void AddLightCandidate(Vector2 uv, Vector4 data, float flick = 1f, int stableId = 0, bool fire = false)
        {
            if (_lightCandidates.Count >= MaxLightCandidates)
                return;
            // Where this light stands in the WORLD, recovered from the viewport. It gives the
            // light a name that survives the camera moving - screen UV cannot, it changes
            // every step and the ranking has to recognise the lamp it chose last frame.
            // Rounded to 8 for the name, so sub-pixel drift cannot rename a light that has
            // not moved.
            //
            // A light that MOVES has no business being named this way, and that is the whole of
            // the glow-ring report. A lamp you are carrying is the same lamp one step later, but
            // eight world pixels is two frames of walking, so it was handed a new name twice a
            // second: the array saw a stranger arriving, started it at nothing, and had it fade in
            // over a third of a second - which it never got, because two frames later it was a
            // stranger again. A carried light therefore sat at a twentieth of its brightness for
            // as long as you were moving and only lit up once you stood still. Anything with a
            // name of its own now says so.
            Vector2 world = new(
                uv.X * Math.Max(1, Game1.viewport.Width) + Game1.viewport.X,
                uv.Y * Math.Max(1, Game1.viewport.Height) + Game1.viewport.Y);
            int wx = (int)Math.Round(world.X / 8f);
            int wy = (int)Math.Round(world.Y / 8f);

            // EDGE TAPER. A light is cut off at a fixed distance past the screen edge, and
            // whatever it was still contributing went with it in a single frame: walking a town
            // changed the live light count 29 times in twenty seconds and the frame's brightness
            // rode along, which is the "lighting gets dimmer and brighter as I move" report.
            //
            // Tying the recovery to a TIMER was the first attempt and it was wrong to look at:
            // a lamp that keeps burning for a third of a second after it should be gone reads as
            // a light dying, not as a light leaving. The fade belongs to the CAMERA, not to a
            // clock. Tapering by how far past the edge the light has travelled means its
            // contribution is already zero by the time the cull takes it, so there is no step to
            // hide; walk back and it returns along exactly the same curve, because the only
            // input is where the light is relative to the view.
            float reach = Math.Max(0.02f, data.W);
            float aspect = Math.Max(1, Game1.viewport.Width) / (float)Math.Max(1, Game1.viewport.Height);
            float dx = Math.Max(0f, Math.Max(-uv.X, uv.X - 1f)) * aspect;   // reach is in height units
            float dy = Math.Max(0f, Math.Max(-uv.Y, uv.Y - 1f));
            float outside = (float)Math.Sqrt(dx * dx + dy * dy);
            // FULL STRENGTH while the light can still reach the screen; the taper is only the
            // band beyond that, where it contributes nothing anyway.
            //
            // This used to fall away from the moment the light crossed the edge, reaching zero at
            // twice its reach - so a lamp sitting exactly one reach outside, which is the last
            // place it can still light the edge of the picture, was contributing HALF of what it
            // should. Walk toward it and that half climbs to whole, which is the lamp getting
            // brighter as you approach it. A lamp bolted to the map is not a lamp being turned up:
            // it lit that corner before you looked and it should be lighting it at full when the
            // corner comes into view.
            //
            // Past its reach the light cannot touch a visible pixel, so the remaining band exists
            // only so the cull is not a step. Nothing visible is being faded there.
            float taper = MathHelper.Clamp(1f - (outside - reach) / Math.Max(0.001f, reach), 0f, 1f);
            taper = taper * taper * (3f - 2f * taper);                      // smooth at both ends
            if (taper <= 0.001f)
                return;
            data = new Vector4(data.X * taper, data.Y * taper, data.Z * taper, data.W);

            _lightCandidates.Add((uv, data, stableId != 0 ? stableId : (wx * 73856093 ^ wy * 19349663), world, flick, fire));
        }

        /// <summary>
        /// Turn the game's own key for a light into a name this pipeline can use. Hashed by hand
        /// rather than with string.GetHashCode, which is salted per process: a frozen capture has
        /// to produce the same bytes on a second run, and the light order is part of that.
        /// </summary>
        /// <remarks>Never returns zero, which is the "this light has no name of its own" sentinel,
        /// and is salted away from the world-position hashes so the two naming schemes cannot
        /// collide and share a fade between a lamp and a window.</remarks>
        private static int StableLightId(string key)
        {
            unchecked
            {
                int h = (int)2166136261;
                foreach (char ch in key)
                    h = (h ^ ch) * 16777619;
                h ^= 0x5bf03635;
                return h == 0 ? 1 : h;
            }
        }

        /// <summary>
        /// True when a light is far enough outside the view that its pool cannot touch a pixel.
        /// <para>
        /// Consistency fix, NOT a cure for the brightness steps. Labelled windows and emissive
        /// tiles were culled at a flat tenth of a screen past the edge whatever their radius,
        /// while the game's own lights were culled at twice their own reach; a wide pool could
        /// therefore be dropped while it was still lighting the screen. Asking the question in
        /// the light's own units makes the three sources agree.
        /// </para>
        /// It was measured against the walk probe and changed nothing: the live light count in
        /// Town still oscillates 11..17 over a twenty second walk, 29 changes, with the frame's
        /// gain moving about 6% across it. Those windows sit near a twentieth of a screen in
        /// radius, so the old margin and this one land in the same place for them. The churn is
        /// real and it is what the "dimmer and brighter as I move" report is made of, but its
        /// cause is that a light leaving the list disappears in ONE FRAME, not where the line is
        /// drawn. Do not re-file this comment as the fix.
        /// </summary>
        private static bool OffScreenBeyondReach(float u, float v, float reach)
        {
            float margin = reach * 2f;
            return u < -margin || u > 1f + margin || v < -margin || v > 1f + margin;
        }

        /// <summary>
        /// How much this light can actually matter to the picture: how bright it is, how far it
        /// reaches, whether that reach lands on screen at all, and how near the middle of the
        /// screen it is.
        ///
        /// <para>
        /// That last term is not a refinement, it is what makes the ranking mean anything in an
        /// ordinary room. The vanilla saloon carries SIXTY FOUR map lights, every one of them at
        /// radius 1 and the same colour, laid out a couple of tiles apart to light the room evenly.
        /// Brightness and reach are therefore the same number for all of them, and the off-screen
        /// taper is 1 for every light that is on screen, so without a distance term all sixty four
        /// scored IDENTICALLY. Which two dozen got a slot came down to the tie-break, which is a
        /// hash of where the light stands: a lottery, redrawn every time the camera moved. That is
        /// the pool that blinks on beside you as you walk, and on a wide window it is whole corners
        /// of a room left unlit while a lamp on the far wall holds a slot.
        /// </para>
        ///
        /// <para>
        /// Nearness is a floor, not a cut: a light at the screen edge is still worth a third of one
        /// in the middle, so when the array has room it keeps its slot. What changes is that the
        /// lights which lose are now the far ones nobody is looking at, and walking slides the
        /// order along instead of reshuffling it.
        /// </para>
        /// </summary>
        private float Relevance(Vector2 uv, Vector4 data)
        {
            float lum = 0.2126f * data.X + 0.7152f * data.Y + 0.0722f * data.Z;
            float reach = Math.Max(0.02f, data.W);
            float dx = Math.Max(0f, Math.Max(-uv.X, uv.X - 1f));
            float dy = Math.Max(0f, Math.Max(-uv.Y, uv.Y - 1f));
            float outside = (float)Math.Sqrt(dx * dx + dy * dy);      // 0 while the centre is on screen
            // Aspect-corrected, so "near the middle" means the same distance sideways as it does
            // up and down. The player sits at the middle of the screen, so this is also "near me".
            float centreX = (uv.X - 0.5f) * _lightAspect;
            float centreY = uv.Y - 0.5f;
            float fromCentre = (float)Math.Sqrt(centreX * centreX + centreY * centreY);
            float near = MathHelper.Lerp(1f, EdgeLightWeight,
                MathHelper.Clamp(fromCentre / CentreFalloffScreens, 0f, 1f));
            return lum * reach * MathHelper.Clamp(1f - outside / reach, 0f, 1f) * near;
        }

        /// <summary>Screen width over height for the frame the lights were measured in.</summary>
        private float _lightAspect = 1f;
        /// <summary>Distance from the middle of the screen, in screen HEIGHTS, at which a light's
        /// ranking has fallen all the way to its floor. About the corner of a widescreen window.</summary>
        private const float CentreFalloffScreens = 0.9f;
        /// <summary>What a light at the very edge of the screen is still worth against one in the
        /// middle. Deliberately not zero: with room in the array an edge light must still get a
        /// slot rather than be refused outright.</summary>
        private const float EdgeLightWeight = 0.3f;

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
        /// <para>
        /// That ranking then went unused in the range where it mattered most. It only ran past
        /// SIXTEEN candidates, the classic path's slot count, while the flood shader - the
        /// default - reads EIGHT. A room with nine to sixteen lights, which is an ordinary
        /// shop with a row of windows, skipped the sort entirely and handed the shader
        /// whichever eight the game's light dictionary happened to enumerate first. Walk a
        /// step, have one light pass the off-screen test, and every index after it shifts:
        /// pools going out here and lighting up over there, several at once. Reported in
        /// Pierre's at dawn, and invisible at midday only because the pools are switched off
        /// in a fully lit room.
        /// </para>
        /// Three things keep the set steady now: the sort runs from the real cap of eight,
        /// equal-scoring lights break their tie on a camera-independent name rather than on
        /// enumeration order, and a light already chosen carries a bonus so a marginally
        /// better newcomer cannot evict it and be evicted back a step later. What still does
        /// change fades both ways over its own frames, and the slots are filled from what is
        /// actually lit rather than from the ranking, so a handover is a crossfade.
        /// </summary>
        private void SelectLights()
        {
            bool sameRoom = ReferenceEquals(Game1.currentLocation, _lightRampLocation);
            if (!sameRoom)
            {
                _lightRamp.Clear();
                _lightChosen.Clear();
                _lightRampLocation = Game1.currentLocation;
            }

            if (_lightCandidates.Count > ShaderLightSlots)
            {
                _lightCandidates.Sort((a, b) =>
                {
                    int byScore = Score(b).CompareTo(Score(a));
                    return byScore != 0 ? byScore : a.Id.CompareTo(b.Id);
                });
            }
            // WHO THE ARRAY WANTS THIS FRAME. Entering was always a fade; leaving was not, so a
            // light that lost its slot simply stopped being written and its pool blinked out in
            // one frame. In a room offering more lights than the shader has slots the last places
            // change hands constantly, and every handover was a blink.
            //
            // The first attempt at that fixed the blink and bought something worse. It faded the
            // last slots by how far clear each light scored of the best LOSER, which is a fraction
            // of a number recomputed from the whole candidate list every frame. Two consequences,
            // both reported within hours of 1.5.2 going out. A room UNDER the cap has no loser, so
            // the whole mechanism was dormant until one more light tipped it over - which is
            // exactly what equipping a glow ring does, and exactly how it was reported. And when
            // the lights in a scene are ALIKE, which a street of identical lamps or a wall of
            // identical windows is, every score sits inside the band at once and they all dim
            // together. Walking moves which light is the best loser, so the whole scene breathed
            // brighter and darker with every step and settled again when you stood still.
            //
            // The lesson is the one already written down for the flame wobble, in mirror image:
            // nothing that moves per frame may decide how bright a light is through the RANKING.
            // So leaving is now the plain mirror of entering - a per-light fade over its own
            // frames, owned by that light, that no other light's score can move.
            _lightWanted.Clear();
            int wantedCount = Math.Min(_lightCandidates.Count, MaxLights);
            for (int i = 0; i < wantedCount; i++)
            {
                var cand = _lightCandidates[i];
                _lightWanted.Add(cand.Id);
                // Keep its place and colour current even on a frame where it gets no slot: the
                // fade it will eventually run has to start from where the light actually is.
                //
                // A light seen for the first time in this room starts at NOTHING, and that is the
                // queue, not an oversight. A newcomer cannot take a slot off a light that is still
                // lit until it has faded up to match it, by which time the one leaving has faded
                // down - which is what makes a handover a crossfade instead of a swap. Starting a
                // camera-revealed light at full was tried on 18 Aug to stop lamps appearing to
                // switch on as you walk up to them, and it made the flicker WORSE, because every
                // light that came into view arrived able to evict a fully lit one immediately:
                //   +790186620(new 0.694)  -790383231(was 0.694)
                // The approaching-lamp problem is the TAPER's to solve, and it is solved there.
                float ramp = _lightRamp.TryGetValue(cand.Id, out LightFade prev)
                    ? prev.Ramp
                    : (sameRoom ? 0f : 1f);
                _lightRamp[cand.Id] = new LightFade { Ramp = ramp, Uv = cand.Uv, Data = cand.Data, Flick = cand.Flick, Fire = cand.Fire };
            }

            // Rank everything that could hold a slot by how bright it is ON SCREEN RIGHT NOW, so a
            // light on its way out at nine tenths outranks the newcomer replacing it at nothing.
            // That is what makes a handover a crossfade rather than a swap: the slot changes hands
            // when the fade has finished, not at the instant the ranking flips.
            //
            // A light that is WANTED but still waiting for a slot is ranked at a floor instead of
            // its true nothing, so that it can take the slot the moment the light leaving it has
            // gone dim, rather than never. The floor is low enough that the one leaving is down to
            // a couple of percent by then, which is not a thing anyone can see going out.
            _lightWrite.Clear();
            foreach (var kv in _lightRamp)
            {
                float lit = _lightWanted.Contains(kv.Key) ? Math.Max(kv.Value.Ramp, WaitingLightFloor) : kv.Value.Ramp;
                // The same margin the WANTED sort gives an incumbent, applied to the sort that
                // hands out the actual slots. There are two rankings here and only one of them
                // was protecting the light already in a slot, so a newcomer could be refused a
                // place on the wanted list and still take the slot on this one.
                //
                // Measured in the Saloon, forty candidate lights for twenty-four slots, walking:
                //   +790186621(new 0.677)   -790514303(was 0.693)
                // A light sitting at sixty-nine percent left the array in one frame so a light at
                // sixty-eight could have its place. Neither was doing anything wrong; they were
                // simply next to each other in a ranking with no hysteresis in it, and a step in
                // either direction flips which one wins. That swap is the flicker people see when
                // they walk through a room with more lamps in it than the shader has slots.
                float rank = lit * Relevance(kv.Value.Uv, kv.Value.Data);
                if (_lightChosen.Contains(kv.Key))
                    rank *= IncumbentMargin;
                _lightWrite.Add((kv.Key, kv.Value, rank));
            }
            if (_lightWrite.Count > MaxLights)
            {
                _lightWrite.Sort((a, b) =>
                {
                    int byRank = b.Rank.CompareTo(a.Rank);
                    return byRank != 0 ? byRank : a.Id.CompareTo(b.Id);
                });
            }

            // Now move each ramp, and here is the rule that matters: a light may only get
            // BRIGHTER on a frame it is actually drawn on.
            //
            // It used to brighten from the moment it was wanted, and a wanted light waits behind
            // whatever is still fading out of the slot it needs. So the first half of its fade in
            // ran while nothing was on screen, and the pool arrived at forty percent and climbed
            // from there. Measured in the saloon: every light entered the array at 0.12 against a
            // settled 0.31, ten of them in one walk, which is the "the pool does not fade in, it
            // just turns on" report. A ramp is how bright this light is on screen, so something
            // that is not on screen cannot have got brighter.
            _lightChosen.Clear();
            _rampDrop.Clear();
            int selectedLightCount = Math.Min(_lightWrite.Count, MaxLights);
            for (int i = 0; i < _lightWrite.Count; i++)
            {
                var (id, fade, _) = _lightWrite[i];
                bool written = i < selectedLightCount;
                if (_lightWanted.Contains(id))
                {
                    if (written)
                        fade.Ramp = Math.Min(1f, fade.Ramp + LightEnterPerFrame);
                    // Otherwise frozen. Waiting in the dark is not the same as getting brighter.
                    _lightRamp[id] = fade;
                }
                else
                {
                    fade.Ramp -= LightLeavePerFrame;
                    if (fade.Ramp <= 0f)
                    {
                        fade.Ramp = 0f;
                        _rampDrop.Add(id);       // gone: it enters from nothing if it comes back
                    }
                    else
                    {
                        _lightRamp[id] = fade;
                    }
                }
                if (!written)
                    continue;
                _lightChosen.Add(id);
                // Flame wobble goes on LAST, after the ranking and the fades have had their say,
                // so a breathing hearth changes how bright it is and never which lights exist.
                float ramp = fade.Ramp * fade.Flick;
                _lightPositions[i] = fade.Uv;
                _lightIsFire[i] = fade.Fire ? 1f : 0f;
                Vector4 d = fade.Data;
                _lightShaderData[i] = new Vector4(d.X * ramp, d.Y * ramp, d.Z * ramp, d.W);
            }
            foreach (int id in _rampDrop)
                _lightRamp.Remove(id);
            _lightCount = selectedLightCount;

            ReportLightWatch(selectedLightCount);

            // Flick is deliberately not read here: the ranking must be steady.
            float Score((Vector2 Uv, Vector4 Data, int Id, Vector2 World, float Flick, bool Fire) c)
            {
                float r = Relevance(c.Uv, c.Data);
                return _lightChosen.Contains(c.Id) ? r * IncumbentMargin : r;
            }
        }

        /// <summary>Frames left to trace the light array for (radiance_lightwatch). Author
        /// diagnostic: it answers "what actually moves between one frame and the next" with
        /// measurements instead of the theory of the day.</summary>
        internal static int LightWatchFrames;
        private readonly Dictionary<int, Vector4> _watchPrevious = new();
        private readonly List<int> _watchGone = new();

        /// <summary>One line per frame naming only what CHANGED: the light count, lights that
        /// entered or left the array, and any light whose contribution moved by more than a
        /// percent. A steady scene prints "steady", and anything that prints instead is the
        /// thing the eye is seeing.</summary>
        private void ReportLightWatch(int selectedLightCount)
        {
            if (LightWatchFrames <= 0)
                return;
            LightWatchFrames--;
            var line = new System.Text.StringBuilder();
            line.Append($"[lightwatch] slots={selectedLightCount}/{MaxLights} candidates={_lightCandidates.Count}");
            _watchGone.Clear();
            _watchGone.AddRange(_watchPrevious.Keys);
            for (int i = 0; i < selectedLightCount; i++)
            {
                // From the WRITE list, not the candidate list. A slot can hold a light that is no
                // longer a candidate at all, because it is fading out after walking off screen,
                // and past that point the two lists have neither the same order nor the same length.
                int id = _lightWrite[i].Id;
                Vector4 now = _lightShaderData[i];
                _watchGone.Remove(id);
                if (!_watchPrevious.TryGetValue(id, out Vector4 was))
                    line.Append($"  +{id}(new {now.X:0.000})");
                else if (Math.Abs(now.X - was.X) > 0.01f * Math.Max(0.05f, was.X))
                    line.Append($"  {id}:{was.X:0.000}->{now.X:0.000}");
                _watchPrevious[id] = now;
            }
            foreach (int id in _watchGone)
            {
                line.Append($"  -{id}(was {_watchPrevious[id].X:0.000})");
                _watchPrevious.Remove(id);
            }
            _monitor.Log(line.Length > 60 ? line.ToString() : line.Append("  steady").ToString(), LogLevel.Info);
            if (LightWatchFrames == 0)
                _watchPrevious.Clear();
        }

        /// <summary>How much of its brightness a light gains per frame while entering the array.
        /// It was 0.12, which is eight frames, and eight frames is not a fade anyone reads as one:
        /// walking up to a lamp, its pool arrived in an eighth of a second and the eye called that
        /// switching on. Twenty-two frames is about a third of a second, slow enough to be seen
        /// happening and short enough that nothing feels like it is lagging behind the player.</summary>
        private const float LightEnterPerFrame = 0.045f;
        /// <summary>And the mirror of it for a light that has lost its slot. Slightly quicker than
        /// entering, so a handover frees the slot for the newcomer rather than the pair of them
        /// sitting in the array for a third of a second each.</summary>
        private const float LightLeavePerFrame = 0.06f;
        /// <summary>What a light that is wanted but still waiting for a slot counts as, for the
        /// purpose of taking one. Its real brightness is nothing, and nothing would never outrank
        /// anything, so it would wait for ever; this lets it claim the slot once the light leaving
        /// has faded to about two percent, which is well under what an eye picks up going out.</summary>
        private const float WaitingLightFloor = 0.02f;

        /// <summary>How much better a newcomer has to be before it takes a slot off the
        /// light already in it. Shared by BOTH rankings - which list a light is wanted on,
        /// and which lights get the slots - because protecting an incumbent on one of them
        /// and not the other is the same as not protecting it at all.</summary>
        private const float IncumbentMargin = 1.3f;

        /// <summary>A light the shader is being told about: where it is, what it looks like, and
        /// how far through its fade it is. Kept for lights that have LOST their slot as well as
        /// for chosen ones, because a light on its way out still has to be drawn — at its last
        /// known place, since it may already be off screen or out of the candidate list.</summary>
        private struct LightFade
        {
            public float Ramp;
            public Vector2 Uv;
            public Vector4 Data;
            public float Flick;
            public bool Fire;
        }

        /// <summary>How lit each light is on THIS screen, and which lights hold the shader's
        /// slots on it. Per screen (swapped in ScreenState), because the two cameras want
        /// different lights: with one shared set, every frame screen 0 ramped its lights up and
        /// screen 1 ramped the same lights down again for not being on its half, and the pools
        /// pulsed and changed places between the two answers. Reported as the light flickering
        /// and jumping about the moment a second player joined.</summary>
        // moved to ScreenState (see RenderPipeline.Screens.cs)
        // moved to ScreenState (see RenderPipeline.Screens.cs)
        private readonly HashSet<int> _lightWanted = new();
        private readonly List<(int Id, LightFade Fade, float Rank)> _lightWrite = new();
        private readonly List<int> _rampDrop = new();
        // moved to ScreenState (see RenderPipeline.Screens.cs)

        // ---- labeled-window glow (HF class 12) ----
        private GameLocation? _windowCacheLocation;
        private int _windowLabelVersion = -1;
        private readonly List<Vector2> _windowTiles = new();   // world-px centres of window tiles

        /// <summary>The window and emissive scans, kept for the few locations in play rather than
        /// for the last one asked about.
        ///
        /// <para>Both scans walk the whole map, every drawn layer, every tile, and they were held
        /// in a single slot keyed by "the location I last scanned". That is right for one screen
        /// and wrong for two: the screens take turns, so each one's scan replaced the other's and
        /// both rescanned every frame. Measured on a farm with two screens, both outdoors: 2.15 ms
        /// a frame for the windows and 2.13 for the emissive tiles, against 0.008 each with one
        /// screen, and the SMAPI log carried 2,726 whole-map scans in a single run. What a scan
        /// finds does not depend on the camera at all, so the answers are simply kept per
        /// location, and a screen finds the other screen's work waiting for it.</para>
        ///
        /// <para>Four locations, dropped oldest first: two screens in two rooms is two, and the
        /// spare pair covers walking between them without paying for a rescan on the way back.</para></summary>
        private const int LocationScanCacheSlots = 4;
        private readonly Dictionary<GameLocation, (int Version, List<Vector2> Tiles)> _windowTilesByLocation = new();
        private readonly Dictionary<GameLocation, (int Version, List<(Vector2 Pos, Vector3 Col, float Amt)> Tiles)> _emissiveTilesByLocation = new();
        private readonly List<GameLocation> _scanCacheDropScratch = new();

        /// <summary>Keep the cache small: the oldest entries go when it outgrows its slots. The
        /// dictionary preserves insertion order well enough for this, and a wrong guess costs one
        /// rescan.</summary>
        private void TrimScanCache<TValue>(Dictionary<GameLocation, TValue> cache)
        {
            if (cache.Count <= LocationScanCacheSlots)
                return;
            _scanCacheDropScratch.Clear();
            int drop = cache.Count - LocationScanCacheSlots;
            foreach (var key in cache.Keys)
            {
                if (_scanCacheDropScratch.Count >= drop)
                    break;
                _scanCacheDropScratch.Add(key);
            }
            foreach (var key in _scanCacheDropScratch)
                cache.Remove(key);
        }
        // Every drawn layer, TOP to BOTTOM (Front wins over Buildings over Back), from the shared
        // sort key. It used to be the three bare names, which missed Back2 / negative-suffix /
        // numbered layers outright, and ran in declaration order — the labeler and the mask now
        // both read the same order this scan does.
        private static List<xTile.Layers.Layer> WindowLayersTopToBottom(xTile.Map? map)
            => MapLayers.RenderedLayers(map, topToBottom: true);

        /// <summary>Scan the whole map ONCE per location (or when labels reload) for window
        /// tiles, caching their world-pixel centres. Cheap enough as a one-off.</summary>
        private void EnsureWindowCache(GameLocation location)
        {
            var labels = LabelStore.Instance;
            int ver = labels?.Version ?? 0;
            if (LiveScreens.SamePlace(location, _windowCacheLocation) && ver == _windowLabelVersion)
                return;
            _windowCacheLocation = location; _windowLabelVersion = ver; _windowTiles.Clear();
            // Another screen may have scanned this very room already this frame.
            if (_windowTilesByLocation.TryGetValue(location, out var remembered) && remembered.Version == ver)
            {
                _windowTiles.AddRange(remembered.Tiles);
                return;
            }
            var map = location.map;
            var layer = map != null && map.Layers.Count > 0 ? map.Layers[0] : null;
            // Windows are 100% label-driven: no labels loaded (version 0 = empty DB) means no window
            // can exist, so skip the whole-map scan entirely. Without this we paid a w×h×3-layer scan
            // on every location change even though it could never find anything.
            if (labels == null || layer == null || map == null || ver == 0)
                return;
            int w = layer.LayerWidth, h = layer.LayerHeight;
            _monitor.Log($"[location] window scan start: {location.NameOrUniqueName} {w}x{h}", LogLevel.Trace);
            var windowScanStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Resolve every drawn layer once instead of per tile: this is a w×h walk over however
            // many drawn layers the map carries, top to bottom.
            var winLayers = WindowLayersTopToBottom(map).ToArray();
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
            _windowTilesByLocation[location] = (ver, new List<Vector2>(_windowTiles));
            TrimScanCache(_windowTilesByLocation);
            _monitor.Log($"[location] window scan done: {_windowTiles.Count} tiles in {windowScanStopwatch.Elapsed.TotalMilliseconds:0.0}ms", LogLevel.Trace);
        }

        /// <summary>Add on-screen window tiles as lights. OUTDOORS (a house exterior): warm lamp
        /// glow that switches on after dusk and off at a per-window "bedtime" (houses go dark as
        /// the night wears on). INDOORS: the opposite — cool daylight pours IN through the window
        /// by day and fades to nothing at night.</summary>
        /// <summary>Eased twin of the window master switch, so turning windows off dims the street
        /// down instead of snapping every lit house dark in one frame.</summary>
        private float _windowEffectsEase = 1f;

        private void AddWindowLights(int vw, int vh, float boost, ModConfig config)
        {
            if (_windowTiles.Count == 0)
                return;
            float windowEffectsTarget = config.WindowEffectsEnabled ? 1f : 0f;
            _windowEffectsEase = Determinism.Settle(
                MathHelper.Lerp(_windowEffectsEase, windowEffectsTarget, 0.03f), windowEffectsTarget);
            if (_windowEffectsEase < 0.02f)
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
                amt *= _windowEffectsEase;
                if (amt < 0.02f)
                    continue;
                if (_lightCandidates.Count >= MaxLightCandidates)
                    continue;
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, wp);
                float u = local.X / vw, v = local.Y / vh;
                float reach = Math.Max(0.02f, outdoors ? radiusOut : radiusIn);
                if (OffScreenBeyondReach(u, v, reach))
                    continue;
                AddLightCandidate(new Vector2(u, v), new Vector4(col * amt * b, reach));
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
        private const int EmitMinPixels = 6;    // below this it is a stray dab, not a light
        // Same lesson as the window scan: every drawn layer, top to bottom, never the three bare
        // names. A map carrying emissive art on Back2 or a negative-suffix layer must light it.
        private static List<xTile.Layers.Layer> EmissiveLayersTopToBottom(xTile.Map? map)
            => MapLayers.RenderedLayers(map, topToBottom: true);

        private void EnsureEmissiveCache(GameLocation location)
        {
            var labels = LabelStore.Instance;
            int ver = labels?.Version ?? 0;
            if (LiveScreens.SamePlace(location, _emissiveCacheLocation) && ver == _emissiveLabelVersion)
                return;
            _emissiveCacheLocation = location; _emissiveLabelVersion = ver; _emissiveTiles.Clear();
            if (location != null && _emissiveTilesByLocation.TryGetValue(location, out var rememberedEmissive)
                && rememberedEmissive.Version == ver)
            {
                _emissiveTiles.AddRange(rememberedEmissive.Tiles);
                return;
            }
            var layer0 = location?.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            if (labels == null || layer0 == null || ver == 0 || location == null)
                return;

            int w = layer0.LayerWidth, h = layer0.LayerHeight;
            // Heaviest of the location-entry walks: every labelled candidate tile also reads its
            // ART, which is a GPU readback the first time a tilesheet is touched.
            _monitor.Log($"[location] emissive scan start: {location.NameOrUniqueName} {w}x{h}", LogLevel.Trace);
            var emissiveScanStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var emitLayers = EmissiveLayersTopToBottom(location.map).ToArray();

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
            _emissiveTilesByLocation[location] = (ver, new List<(Vector2, Vector3, float)>(_emissiveTiles));
            TrimScanCache(_emissiveTilesByLocation);
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
                float reach = Math.Max(0.02f, radius * (0.6f + 0.4f * amt));
                if (OffScreenBeyondReach(u, v, reach))
                    continue;
                AddLightCandidate(new Vector2(u, v), new Vector4(col * a * bst, reach));
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
            // The game dims indoor ambient for the NIGHT; we must not pile darkening on top of
            // that or a room goes black. A daytime storm only tints ambient slightly, and the
            // morning/afternoon dim is ours to keep - so honour the game's ambient only when it
            // is actually night, not whenever a weather tint drifts off white (1115938: clear
            // morning dark, stormy morning flat-bright, the inverse of daylight).
            bool itIsNight = GameClock.MinutesNow() >= ShadowRenderer.TrulyDarkMinutes() - 60f;
            bool vanillaLit = outdoors
                || Game1.currentLocation is StardewValley.Locations.MineShaft
                || (itIsNight && !Game1.ambientLight.Equals(Color.White));
            if (vanillaLit)
                return Vector3.One;

            float dark = MathHelper.Clamp(config.LightingIndoorDarkness, 0f, 0.95f);
            // Evening was a hard flip at 19:00 (rooms snapped darker on the tick), now eased
            // over +-10 game-minutes.
            //
            // Morning is the half that never existed. The old dawn term ended at 06:10 - ten
            // minutes after the player wakes - so a room was at full daytime brightness before
            // anyone had walked across it, which is also the one time of day when the sun is
            // lowest. It now holds through 06:00 and lifts over the next two hours, so waking
            // up happens in a dim room that brightens while the morning gets going (asked for
            // on Nexus, against Gentle Night Lighting as the reference).
            // The morning share became its own slider (LightingMorningDarkness, default 0.25 =
            // the historical quarter) so the waking darkness can be tuned. The sun is up at six,
            // just low, so the default keeps "dim is the ask, dark is a bug report".
            float morningRamp = config.LightingMorningDarkness * (1f - GameClock.RampAt(700, 60f));
            float nightRamp = Math.Max(GameClock.RampAt(1900), morningRamp);
            dark = MathHelper.Clamp(dark + config.LightingNightDarkness * nightRamp, 0f, 0.95f);

            // Cool moonlight-ish tint for the darkened room.
            Vector3 darkTint = new(0.45f, 0.48f, 0.62f);
            // A lightning flash lifts our darkening toward none for as long as the game's own
            // flash lasts, so the scene answers the same white the player just saw.
            return LightningEffects.LiftAmbient(Vector3.Lerp(Vector3.One, darkTint, dark));
        }

        /// <summary>
        /// Build a per-tile occluder mask for the visible area: a tile blocks light if
        /// the map's "Buildings" layer has a tile there (walls / built structures).
        /// Aligned to the viewport exactly like the water mask. Returns false (skipping
        /// shadows) when there are no occluders on screen.
        /// </summary>
        /// <summary>The pixels last handed to the GPU, so an unchanged mask is not sent again.</summary>
        private Color[]? _occluderMaskUploaded;
        private int _occluderUploadedCount;

        private bool SameOccluderContent(int count)
        {
            if (_occluderMaskUploaded == null || _occluderUploadedCount != count)
                return false;
            for (int i = 0; i < count; i++)
                if (_occluderMaskPixels![i] != _occluderMaskUploaded[i])
                    return false;
            return true;
        }

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

            bool sizeChanged = _occluderMask == null || _occluderMask.Width != tilesW || _occluderMask.Height != tilesH;
            // UPLOAD ONLY WHAT CHANGED. The throttle above stops the grid being rebuilt more than
            // every third tick, but it did not stop the RESULT being pushed to the card, so a
            // player standing still re-uploaded an identical mask twenty times a second. That
            // upload is a GPU-side cost with no CPU-side signature, which is how it hid: the
            // build timer reads a few microseconds and the setting still prices at 0.19 ms, the
            // most expensive thing left in the mod.
            //
            // The flood path already learned this and gates on content; the classic path never
            // got the same treatment. Comparing the bytes costs a walk of a grid we have just
            // walked anyway, against a texture transfer and whatever the driver does to a
            // resource the GPU may still be reading.
            //
            // And when it does upload, it uploads into the pair's spare rather than into the
            // texture the card may still be sampling, which is the other half of that same wait
            // (TextureDoubleBuffer).
            if (sizeChanged || !SameOccluderContent(count))
            {
                _occluderMask = TextureDoubleBuffer.UploadIntoSpare(_device, ref _occluderMaskSpare, _occluderMask,
                    tilesW, tilesH, SurfaceFormat.Color, "light occluder mask", _occluderMaskPixels, count);
                if (_occluderMaskUploaded == null || _occluderMaskUploaded.Length < count)
                    _occluderMaskUploaded = new Color[count];
                Array.Copy(_occluderMaskPixels, _occluderMaskUploaded, count);
                _occluderUploadedCount = count;
            }
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
        /// <summary>How far past the screen the FLOOD occluder mask reaches, in tiles. The sun
        /// shaft march walks up to eight tiles toward the sun, and with a screen-sized mask
        /// everything it needed was off the edge: a canopy's shafts sprang into being as the
        /// canopy scrolled into the mask, which read as light that follows the player around.
        /// Clouds do not do that, and neither should sun. Same fix as the water mirror's source
        /// padding, same reason.</summary>
        private const int FloodOccPad = 8;

        /// <summary>Texels per tile in the flood occluder mask. One texel per tile made every fence,
        /// bush and boulder a solid square, and a lamp behind a fence lit the far side evenly where
        /// it should throw a comb of light between the pickets. Four per tile is enough for a picket
        /// (a fence post is four game pixels, one texel) and keeps the mask at roughly 200 x 130.</summary>
        // EIGHT, from four. At four a plant's footprint was a 4x4 block and the bilinear read of a
        // block's diagonal edge is scalloped, one scallop per texel, sixteen world pixels wide -
        // and a shadow wedge traces that edge, so its own edge came out as a saw that no amount
        // of tap spread or step count could smooth (both were tried, at softness 0 and 2). Twice
        // the texels halves the scallop; the LOD floor in floodlight.fx softens what is left.
        // Must match the texels-per-tile constant in floodlight.fx's shadow march.
        private const int FloodOccSubdivision = 8;
        /// <summary>How much of the light each kind of silhouette stops. Fences are thin wood, but a
        /// picket blocks all of what lands on it; a bush is leaves and lets a little through, but only
        /// a little: at 0.6 a hedge sprayed light out its far side, which was the first thing seen; a
        /// boulder is a boulder. The tile stamps the switch falls back to keep the old 150 and 200.</summary>
        private const float FenceOccluderShare = 1.0f;
        private const float BushOccluderShare = 0.9f;
        private const float ClumpOccluderShare = 0.8f;
        /// <summary>Kegs, chests, machines, signs and floor furniture: a solid body a lamp cannot
        /// see through, a shade under a fence because their sprites carry soft edges the fence's
        /// pickets do not.</summary>
        private const float PropOccluderShare = 0.9f;

        /// <summary>Where a sprite's BASE begins and ends across its own source rect, in source
        /// pixels, so the solid block under a placed thing is as wide as the thing rather than as
        /// wide as the tile it stands on.
        ///
        /// <para>Read from the bottom of the picture only. The base is what rests on the floor,
        /// and it is what a lamp's ray meets: a lamp on a table reads the tabletop as the thing in
        /// the way and a lamp on the floor reads the legs, so the widest row anywhere in the
        /// sprite is the wrong answer for both.</para>
        ///
        /// <para>Cached per texture and rect, like the shadow pass's own foot-row lookup: this is
        /// a readback, and a farm holds thousands of objects drawn from a handful of pictures.</para>
        /// </summary>
        private readonly Dictionary<(Texture2D, Rectangle), (int Left, int Right)> _artBaseSpan = new();

        /// <summary>
        /// Read the base span of every distinct piece of placed art in the location now, on the
        /// warp frame, so no readback is left to happen the first time a thing scrolls into view.
        /// </summary>
        /// <remarks>
        /// <see cref="ArtBaseSpan"/> is a <c>GetData</c>, which on this backend makes the CPU wait
        /// for the card. It is cached per picture, so a farm pays it once per KIND of thing rather
        /// than once per thing, but that once used to land mid-stride: walk into a new part of the
        /// farm and every kind first seen there stalled the frame it appeared in, which is the
        /// "stutter while working the farm" shape. A location is a few dozen kinds at most, and the
        /// game is showing black while this runs.
        /// </remarks>
        private void PrewarmArtBaseSpans(GameLocation? location)
        {
            if (location?.objects == null)
                return;
            int read = 0;
            try
            {
                foreach (var pair in location.objects.Pairs)
                {
                    SObject placed = pair.Value;
                    if (placed == null || placed is Fence || placed is CrabPot || placed.IsSpawnedObject)
                        continue;
                    if (!placed.bigCraftable.Value && placed.isPassable())
                        continue;
                    if (placed.IsWeeds() || placed.IsTwig() || placed.IsBreakableStone())
                        continue;
                    if (!TryPlacedArt(placed.QualifiedItemId, out Texture2D? art, out Rectangle source) || art == null || source.IsEmpty)
                        continue;
                    if (_artBaseSpan.ContainsKey((art, source)))
                        continue;
                    ArtBaseSpan(art, source);
                    read++;
                }
            }
            catch (Exception ex)
            {
                // A warm-up must never be the thing that breaks a warp; the lazy path still works.
                _monitor.Log($"art base prewarm stopped early: {ex.Message}", LogLevel.Trace);
            }
            if (read > 0)
                _monitor.Log($"[diag] art base spans read on arrival: {read} kind(s) in {location.NameOrUniqueName}", LogLevel.Trace);
        }

        private (int Left, int Right) ArtBaseSpan(Texture2D texture, Rectangle source)
        {
            var key = (texture, source);
            if (_artBaseSpan.TryGetValue(key, out var known))
                return known;
            // The whole cell, which is what the old code assumed for everything.
            var span = (Left: 0, Right: source.Width);
            try
            {
                if (source.Width > 0 && source.Height > 0 && !texture.IsDisposed
                    && source.Right <= texture.Width && source.Bottom <= texture.Height)
                {
                    var pixels = new Color[source.Width * source.Height];
                    texture.GetData(0, source, pixels, 0, pixels.Length);
                    // The bottom quarter, and never fewer than four rows: enough of the base to
                    // catch both legs of a table, little enough that a wide top does not decide it.
                    int rows = Math.Max(4, source.Height / 4);
                    int first = source.Width, last = -1;
                    for (int row = source.Height - rows; row < source.Height; row++)
                    {
                        if (row < 0)
                            continue;
                        for (int column = 0; column < source.Width; column++)
                            if (pixels[row * source.Width + column].A > 8)
                            {
                                if (column < first) first = column;
                                if (column > last) last = column;
                            }
                    }
                    // A base that read as empty is a picture this cannot speak for, so it keeps
                    // the whole cell rather than shrinking to nothing and letting light through.
                    if (last >= first)
                        span = (first, last + 1);
                }
            }
            catch (Exception)
            {
                span = (0, source.Width);
            }
            _artBaseSpan[key] = span;
            return span;
        }
        /// <summary>The tile-resolution grid (walls, tree trunks) that the silhouettes are drawn over.</summary>
        // moved to ScreenState (see RenderPipeline.Screens.cs)
        // moved to ScreenState (see RenderPipeline.Screens.cs)   // its pair - see TextureDoubleBuffer
        private SpriteBatch? _floodOccluderSpriteBatch;
        /// <summary>Additive with a per-tier factor: a wall under a bush stays a wall (the sum
        /// saturates at 1), and a bush over open ground is exactly its share.</summary>
        private static BlendState OccluderShareBlend(float share) => new()
        {
            ColorSourceBlend = Blend.BlendFactor, AlphaSourceBlend = Blend.BlendFactor,
            ColorDestinationBlend = Blend.One, AlphaDestinationBlend = Blend.One,
            BlendFactor = new Color(share, share, share, share),
        };
        /// <summary>Blend states are immutable once used, so one is kept per quantised share; the
        /// share moves only while the silhouette switch is fading, so this holds a few dozen at most.</summary>
        private readonly Dictionary<int, BlendState> _occluderBlendByShare = new();
        private BlendState OccluderBlendFor(float share)
        {
            int key = Math.Clamp((int)MathF.Round(share * 255f), 0, 255);
            if (!_occluderBlendByShare.TryGetValue(key, out BlendState? blend))
                _occluderBlendByShare[key] = blend = OccluderShareBlend(key / 255f);
            return blend;
        }
        /// <summary>0 = the 1.6.2 look (fences, bushes and boulders as whole tiles, rounder pools),
        /// 1 = their own silhouettes. Eased, and the mask is rebuilt as it moves, so the switch
        /// cross-fades the two instead of snapping every shadow in the room.</summary>
        private float _occluderShapesEase = 1f;
        /// <summary>The placed-things switch, eased the same way: 0 = kegs, chests and furniture
        /// let lamp light straight through, 1 = they stand in it as the sprites they are.</summary>
        private float _occluderPropsEase = 1f;

        /// <summary>Item art by qualified id, the way <see cref="ShadowRenderer"/> keeps its own:
        /// the registry walk is not free and a farm asks about the same keg a hundred times.
        /// Cleared with the season, since a few items swap art with it.</summary>
        private readonly Dictionary<string, (Texture2D? texture, Rectangle source)> _placedArtCache = new();
        private string _placedArtSeason = "";

        private bool TryPlacedArt(string qualifiedId, out Texture2D? texture, out Rectangle source)
        {
            string season = Game1.currentSeason ?? "";
            if (season != _placedArtSeason)
            {
                _placedArtCache.Clear();
                _placedArtSeason = season;
            }
            if (!_placedArtCache.TryGetValue(qualifiedId, out var entry))
            {
                try
                {
                    var data = ItemRegistry.GetDataOrErrorItem(qualifiedId);
                    entry = (data.GetTexture(), data.GetSourceRect());
                }
                catch
                {
                    entry = (null, Rectangle.Empty);
                }
                _placedArtCache[qualifiedId] = entry;
            }
            texture = entry.texture;
            source = entry.source;
            return texture != null;
        }

        /// <summary>Where every non-window light stands, in world pixels, for the one question the
        /// prop pass asks: is this thing a lamp? Answered from the game's own light list rather
        /// than from a field on the object, so a mod's torch is a torch too.</summary>
        private readonly List<Vector2> _occluderLightPositions = new();

        private void GatherOccluderLightPositions()
        {
            _occluderLightPositions.Clear();
            var lights = Game1.currentLightSources;
            if (lights == null)
                return;
            foreach (var kv in lights)
            {
                if (kv.Value.lightContext.Value == LightSource.LightContext.WindowLight)
                    continue;
                _occluderLightPositions.Add(kv.Value.position.Value);
            }
        }

        private bool LightStandsIn(Rectangle worldBox)
        {
            foreach (var position in _occluderLightPositions)
                if (worldBox.Contains((int)position.X, (int)position.Y))
                    return true;
            return false;
        }

        /// <summary>The mask box-filtered to a half, a quarter and an eighth: the shadow march's
        /// penumbra (OccAtBlur in floodlight.fx). Its own textures rather than a mip chain, because
        /// the level a pixel shader asks tex2Dlod for reaches the GPU as a bias through MonoGame's
        /// GLSL path and the softness dial did nothing.</summary>
        internal const int FloodOccluderSoftLevels = 3;
        // moved to ScreenState (see RenderPipeline.Screens.cs)

        private bool BuildFloodOccluders(int w, int h, ModConfig config)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return false;
            var layer = location.map?.GetLayer("Buildings");

            int vx = Game1.viewport.X;
            int vy = Game1.viewport.Y;
            int startTileX = (int)Math.Floor(vx / 64f) - FloodOccPad;
            int startTileY = (int)Math.Floor(vy / 64f) - FloodOccPad;
            // Viewport-based (world px): w/64 is screen px and undercounts tiles when zoomed out.
            int tilesW = Math.Max(1, Game1.viewport.Width / 64 + 2) + FloodOccPad * 2;
            int tilesH = Math.Max(1, Game1.viewport.Height / 64 + 2) + FloodOccPad * 2;
            int count = tilesW * tilesH;

            // Rebuild on an input change, not on a clock — the flood lightmap's fix, applied
            // here after the split report showed this line inheriting its crown. The old comment
            // justified the 3-tick refresh with "moving NPC stamps", but characters are
            // deliberately NOT stamped into this grid any more (see below), so nothing in it
            // moves per frame. What actually changes it: crossing a tile, a terrain feature or
            // clump appearing/vanishing (the counts below), a building placed or removed (a new
            // SurfaceMap identity), or a growth stage ticking over — which happens at day start
            // behind the save fade, and is what the lazy once-a-second fallback is for.
            long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var surf = SurfaceMap.For(location);
            Approach(ref _occluderShapesEase, config.LightShadowSilhouettes ? 1f : 0f, 0.05f);
            int shapesStep = (int)MathF.Round(_occluderShapesEase * 32f);
            Approach(ref _occluderPropsEase, config.LightShadowProps ? 1f : 0f, 0.05f);
            int propsStep = (int)MathF.Round(_occluderPropsEase * 32f);
            int occluderInputsHash;
            unchecked
            {
                occluderInputsHash = 17;
                occluderInputsHash = occluderInputsHash * 31 + location.terrainFeatures.Count();
                occluderInputsHash = occluderInputsHash * 31 + location.largeTerrainFeatures.Count;
                occluderInputsHash = occluderInputsHash * 31 + location.resourceClumps.Count;
                occluderInputsHash = occluderInputsHash * 31 + CountFences(location);
                occluderInputsHash = occluderInputsHash * 31 + shapesStep;
                // Placing or picking up anything, and a torch lit or put out, both move the mask.
                occluderInputsHash = occluderInputsHash * 31 + location.objects.Count();
                occluderInputsHash = occluderInputsHash * 31 + location.furniture.Count;
                occluderInputsHash = occluderInputsHash * 31 + (Game1.currentLightSources?.Count ?? 0);
                occluderInputsHash = occluderInputsHash * 31 + propsStep;
            }
            if (_floodOccluderMask != null && startTileX == _floodOccluderTileX && startTileY == _floodOccluderTileY
                // Texels, not tiles. The mask became a render target at FloodOccSubdivision texels
                // per tile when silhouettes arrived, and this test kept comparing its width against
                // the TILE count, which it can never equal - so the cache never hit and the whole
                // mask, sprite draws and all, was rebuilt on every frame instead of on a change.
                // Measured at 0.33 ms per frame on the beach and 0.57 on a fenced farm.
                && _floodOccluderMask.Width == tilesW * FloodOccSubdivision
                && ReferenceEquals(surf, _floodOccluderSurfaceMap) && occluderInputsHash == _floodOccluderInputsHash
                && Game1.ticks - _floodOccluderCacheTick < 60)
            {
                _floodOccluderMaskSize = new Vector2(tilesW, tilesH);
                return true;
            }
            phaseStart = PhaseCost.NoteSince("flood occluders: gate (counts + fences)", phaseStart);
            _floodOccluderTileX = startTileX;
            _floodOccluderTileY = startTileY;
            _floodOccluderCacheTick = Game1.ticks;
            _floodOccluderSurfaceMap = surf;
            _floodOccluderInputsHash = occluderInputsHash;

            if (_floodOccluderMaskPixels == null || _floodOccluderMaskPixels.Length < count)
                _floodOccluderMaskPixels = new Color[count];
            // The map's own answer for every tile, asked once per map and kept (see
            // EnsureFloodSolidBase). This loop used to ask the game three questions per tile,
            // fifteen hundred tiles, once a second and on every tile crossing, and that was the
            // 1.27 ms worst frame this grid showed on a farm walk.
            EnsureFloodSolidBase(location, surf, layer);
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int tx = startTileX + i, ty = startTileY + j;
                    bool solid;
                    if (_floodSolidBase != null && tx >= 0 && ty >= 0 && tx < _floodSolidBaseWidth && ty < _floodSolidBaseHeight)
                    {
                        solid = _floodSolidBase[ty * _floodSolidBaseWidth + tx] != 0;
                    }
                    else if (surf != null)
                    {
                        // Walls/roofs block lamp light; decks (piers/bridges, height 1 but open)
                        // and water don't.
                        //
                        // Neither does anything the farmer can WALK on, whatever the surface map
                        // calls it. The farmhouse porch is roof by class - it sits under the
                        // overhang - so the mask stamped the boards solid, and a light carried
                        // onto them was a light standing inside a wall. Stepping up onto the
                        // porch switched every shadow that light cast off, and stepping back down
                        // switched them on: measured over the ground both frames share, the
                        // picture moved 6.63 where the game's own moved 0.05.
                        //
                        // And a farm BUILDING blocks by its own collision map, asked directly. A coop
                        // or a barn is not in the map at all - it is placed on grass the map calls
                        // passable - so the walkable rule above alone struck every farm building out
                        // of the mask the day it landed, and a ring at the coop door lit the ground
                        // straight through the coop. The building's own map keeps the farmhouse
                        // porch open (it is passable there too), so this does not undo that fix.
                        solid = (surf.BlocksLight(tx, ty) && !CanWalkOn(location, tx, ty))
                             || BuildingBlocks(location, tx, ty);
                    }
                    else
                    {
                        solid = layer != null && tx >= 0 && ty >= 0 && tx < layer.LayerWidth && ty < layer.LayerHeight
                            && layer.Tiles[tx, ty] != null;
                    }
                    byte v = solid ? (byte)255 : (byte)0;
                    _floodOccluderMaskPixels[j * tilesW + i] = new Color(v, v, v, v);
                }
            }

            // A walkable slit INSIDE a structure - a farmhouse doorway column, the open band a
            // building's collision map leaves across its middle - is enclosed by wall on both
            // sides, so its probes go near-black and paint a smudge onto the facade art drawn
            // over those tiles. Flag such cells in G, which every shader reads nothing from
            // except the cascade resolve's facade lift: rays still march through the slit
            // unchanged, so a light carried onto a porch keeps working. Runs before the tree
            // and clump stamps so only real walls count as enclosure.
            for (int j = 0; j < tilesH; j++)
            {
                for (int i = 0; i < tilesW; i++)
                {
                    int idx = j * tilesW + i;
                    if (_floodOccluderMaskPixels[idx].R == 255)
                        continue;
                    bool SolidAt(int x, int y) => x >= 0 && x < tilesW && y >= 0 && y < tilesH
                        && _floodOccluderMaskPixels[y * tilesW + x].R == 255;
                    bool above = SolidAt(i, j - 1) || SolidAt(i, j - 2);
                    bool below = SolidAt(i, j + 1) || SolidAt(i, j + 2);
                    bool left = SolidAt(i - 1, j) || SolidAt(i - 2, j);
                    bool right = SolidAt(i + 1, j) || SolidAt(i + 2, j);
                    if ((above && below) || (left && right))
                    {
                        Color cell = _floodOccluderMaskPixels[idx];
                        _floodOccluderMaskPixels[idx] = new Color(cell.R, (byte)255, cell.B, cell.A);
                    }
                }
            }

            void Stamp(int tx, int ty, byte strength)
            {
                int i = tx - startTileX, j = ty - startTileY;
                if (i < 0 || i >= tilesW || j < 0 || j >= tilesH)
                    return;
                int idx = j * tilesW + i;
                if (_floodOccluderMaskPixels[idx].R < strength)
                    _floodOccluderMaskPixels[idx] = new Color(strength, strength, strength, strength);
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
                    // Bushes and clumps are drawn as their own silhouettes below; the tile stamp
                    // is what they fade back to when the silhouette switch is off.
                    case StardewValley.TerrainFeatures.Bush:
                        Stamp((int)kv.Key.X, (int)kv.Key.Y, (byte)(150f * (1f - _occluderShapesEase)));
                        break;
                }
            }
            foreach (var ltf in location.largeTerrainFeatures)
            {
                if (ltf is StardewValley.TerrainFeatures.Bush b)
                    Stamp((int)b.Tile.X, (int)b.Tile.Y, (byte)(150f * (1f - _occluderShapesEase)));
            }
            foreach (var clump in location.resourceClumps)
            {
                if (clump == null) continue;
                for (int cy = 0; cy < clump.height.Value; cy++)
                    for (int cx = 0; cx < clump.width.Value; cx++)
                        Stamp((int)clump.Tile.X + cx, (int)clump.Tile.Y + cy, (byte)(200f * (1f - _occluderShapesEase)));
            }
            // Characters/animals/the player are NOT stamped: their shadows are owned by the
            // sprite silhouette pass — stamping them here too gave everyone standing near a
            // lamp a second blurry dark blotch on top of their cast shadow.
            phaseStart = PhaseCost.NoteSince("flood occluders: window copy + tile stamps", phaseStart);

            // The grid above at tile resolution, then everything with a real silhouette drawn over
            // it at FloodOccSubdivision texels per tile, by the game's own art and placement.
            // Into the pair's spare, never into the texture the silhouette pass may still be
            // reading from the previous window (TextureDoubleBuffer).
            _floodOccluderBaseTexture = TextureDoubleBuffer.UploadIntoSpare(_device, ref _floodOccluderBaseSpare,
                _floodOccluderBaseTexture, tilesW, tilesH, SurfaceFormat.Color, "flood occluder mask", _floodOccluderMaskPixels, count);
            phaseStart = PhaseCost.NoteSince("flood occluders: base upload", phaseStart);
            int maskW = tilesW * FloodOccSubdivision, maskH = tilesH * FloodOccSubdivision;
            if (_floodOccluderMask is not RenderTarget2D maskTarget || maskTarget.Width != maskW || maskTarget.Height != maskH)
            {
                _floodOccluderMask?.Dispose();
                maskTarget = VramTally.Track(new RenderTarget2D(_device, maskW, maskH, false, SurfaceFormat.Color, DepthFormat.None), "flood occluder mask");
                _floodOccluderMask = maskTarget;
            }
            DrawOccluderSilhouettes(maskTarget, location, startTileX, startTileY, tilesW, tilesH);
            phaseStart = PhaseCost.NoteSince("flood occluders: silhouettes (sprite draws)", phaseStart);
            BuildSoftOccluderLevels(maskTarget);
            PhaseCost.NoteSince("flood occluders: soft levels (GPU blur)", phaseStart);
            _floodOccluderMaskSize = new Vector2(tilesW, tilesH);
            return true;
        }

        /// <summary>Run the radiance cascades over the flood occluder window just built (see
        /// <see cref="RadianceCascades"/>): the mask and its softened copies are what the rays march.</summary>
        private bool BuildCascades(ModConfig config)
        {
            if (_floodOccluderMask is not RenderTarget2D mask || _floodOccluderBaseTexture == null || _cascadesEffect == null)
                return false;
            return _cascades.Build(_device, _monitor, _cascadesEffect, _flood, config, mask, _floodOccluderSoft,
                _floodOccluderBaseTexture, _floodOccluderTileX, _floodOccluderTileY,
                (int)_floodOccluderMaskSize.X, (int)_floodOccluderMaskSize.Y, _floodOccluderCacheTick);
        }

        /// <summary>Whether the world draw should be recorded this frame: the relief is on, or it is
        /// still fading out and needs a buffer to fade with.</summary>
        internal bool WantsSpriteRecording(ModConfig config)
            => config.Enabled && ((config.SpriteReliefEnabled && config.FloodLightingEnabled) || _reliefEase > FadeGone);

        /// <summary>The textures the current map paints its tiles from, as instances rather than by
        /// name: the display device loads a tilesheet through the same content manager, so the
        /// instance is the same one the draws carry, and two sheets that share a file name in
        /// different folders cannot be confused for each other. Rebuilt when the map changes and
        /// once a second besides, because a content patch reloads assets under a map that never
        /// changed and the old instances would go on being matched.</summary>
        private readonly HashSet<Texture2D> _mapTileSheetTextures = new();
        /// <summary>The same sheets by NAME, as a second way in. Asking the content manager for
        /// "Maps/fall_town" on a game running in Thai hands back the base asset, while the display
        /// device drew from "Maps/fall_town.th" - a different instance of a translated sheet, which
        /// the set above cannot match. So a whole season's tiles wore the bevel the fix removed,
        /// and only the seasons whose sheet the translation pack had not replaced looked right,
        /// which is why this came back as "summer was fine".</summary>
        private readonly HashSet<string> _mapTileSheetNames = new(StringComparer.OrdinalIgnoreCase);
        private xTile.Map? _mapTileSheetSource;
        private int _mapTileSheetTick = -1000;
        private Texture2D? _lastSheetAsked;
        private bool _lastSheetWasMapTile;

        /// <summary>A drawn sheet's name with any locale suffix taken off: the game appends the
        /// language to a translated asset ("Maps/fall_town.th"), and the map still calls it by the
        /// base name. Anything after the LAST dot that is short and has no slash in it is a locale
        /// tag, not part of a path.</summary>
        private static string WithoutLocaleSuffix(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot < name.Length - 6)
                return name;
            return name.IndexOf('/', dot) >= 0 || name.IndexOf('\\', dot) >= 0 ? name : name.Substring(0, dot);
        }

        /// <summary>
        /// Whether this sheet is one the current map paints its tiles from. Such a draw gets no
        /// relief: a normal map is baked for a WHOLE sheet, so at a tile cell's border the bevel's
        /// Sobel reads the NEXT CELL of the sheet, which is unrelated art. Measured indoors, that
        /// invented a lean of 91 of 128 across a wall's tile seam while the tile's own inside was
        /// flat - an embossed line along every drawn tile edge, and a dark band under an animated
        /// water tile that read as a shadow it should never have cast. The game draws only a
        /// handful of tiles through the sorted batch (a fountain's water, a sorted Buildings tile),
        /// so the tiles that DID get a bevel were the arbitrary few, which is the other half of why
        /// it looked wrong. FLAT and not skipped: leaving the draw out stopped the tile covering
        /// what stands behind it in the normal buffer, and a farmer walking behind a building had
        /// their own bevelled outline show through the wall.
        /// </summary>
        /// <summary>Whether the relief pass will leave this sheet FLAT rather than bevel it, which
        /// is the question radiance_reliefdraws exists to answer. A render target is flat because
        /// it has no sheet to read a normal from; a map tilesheet is flat because a bevel baked for
        /// a whole sheet reads the next cell along at a tile's border.</summary>
        internal bool ReliefLeavesSheetFlat(Texture2D sheet)
            => sheet is RenderTarget2D || DrawnFromMapTileSheet(sheet);

        private bool DrawnFromMapTileSheet(Texture2D sheet)
        {
            if (ReferenceEquals(sheet, _lastSheetAsked))
                return _lastSheetWasMapTile;
            xTile.Map? map = Game1.currentLocation?.Map;
            if (map == null)
                return false;
            if (!ReferenceEquals(map, _mapTileSheetSource) || Game1.ticks - _mapTileSheetTick > 60)
            {
                _mapTileSheetSource = map;
                _mapTileSheetTick = Game1.ticks;
                _mapTileSheetTextures.Clear();
                _mapTileSheetNames.Clear();
                foreach (xTile.Tiles.TileSheet tileSheet in map.TileSheets)
                {
                    if (string.IsNullOrEmpty(tileSheet.ImageSource))
                        continue;
                    _mapTileSheetNames.Add(LabelStore.NormalizeSheet(tileSheet.ImageSource));
                    try
                    {
                        Texture2D loaded = Game1.content.Load<Texture2D>(tileSheet.ImageSource);
                        if (!loaded.IsDisposed)
                            _mapTileSheetTextures.Add(loaded);
                    }
                    catch
                    {
                        // A tilesheet the content manager will not hand over is one this pass
                        // cannot recognise; its tiles keep the behaviour they had.
                    }
                }
                _lastSheetAsked = null;
            }
            bool isMapTile = _mapTileSheetTextures.Contains(sheet);
            if (!isMapTile && !string.IsNullOrEmpty(sheet.Name))
                isMapTile = _mapTileSheetNames.Contains(LabelStore.NormalizeSheet(WithoutLocaleSuffix(sheet.Name)));
            _lastSheetAsked = sheet;
            _lastSheetWasMapTile = isMapTile;
            return isMapTile;
        }

        /// <summary>
        /// Put the tree's trunk back together in the normal buffer.
        ///
        /// <para>A tree is TWO sprites the artist lined up by hand: the canopy, whose trunk art
        /// stops part way down the block (row 81 of the oak's 96), and a separate trunk piece that
        /// takes over below it and is drawn UNDER the canopy where the two overlap. The relief
        /// works out its shading per sprite from that sprite's own outline, so the row where the
        /// canopy's art stops is shaded like the edge of a real object - and that shading lands
        /// across the trunk as a dark line. Reported as the tree splitting in two, and it goes away
        /// the moment sprite relief is switched off, which is what named it.</para>
        ///
        /// <para>The trunk piece is redrawn here, last, with the sheet's FLAT normal map: its own
        /// alpha decides where it lands, so nothing beside the trunk is touched, and where it
        /// covers the canopy's shaded edge that edge stops existing. The trunk loses its own bevel,
        /// which is the price: a trunk with no relief reads as a trunk, and a trunk cut in half
        /// does not. The pair is found in the RECORDED draws rather than rebuilt from the tree's
        /// tile, so a modded tree drawn the same way is mended the same way.</para>
        /// </summary>
        private void FlattenTreeTrunkJoins()
        {
            var records = SpriteDrawRecorder.Records;
            TrunkJoinsMended = 0;
            if (records.Count == 0 || _normalsEffect == null)
                return;
            _normalSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            try
            {
                for (int i = 0; i < records.Count; i++)
                {
                    SpriteDrawRecorder.Record trunk = records[i];
                    if (trunk.UsesDestination || trunk.Texture.IsDisposed
                        || trunk.Source.Width != TreeTrunkPieceWidth || trunk.Source.Height != TreeTrunkPieceHeight)
                        continue;
                    if (!TryFindCanopyFor(records, i, trunk))
                        continue;
                    Texture2D? flat = _sheetNormals.For(_device, _normalsEffect, trunk.Texture, NormalBakeFlat);
                    if (flat == null)
                        continue;
                    // The trunk's OWN placement, whatever it ended up being. The wind rewrites this
                    // draw to turn it about the tree's base, which moves the origin off zero and the
                    // position onto the canopy's anchor, so anything rebuilt here from the tree's
                    // geometry would land somewhere the trunk is not. Substituting only the texture
                    // cannot miss.
                    _normalSpriteBatch.Draw(flat, trunk.Position, trunk.Source, Color.White * trunk.Alpha,
                        trunk.Rotation, trunk.Origin, trunk.Scale, trunk.Effects, 0f);
                    TrunkJoinsMended++;
                }
            }
            catch
            {
                // A mended trunk is a nicety; a thrown pass would cost the whole relief.
            }
            finally
            {
                _normalSpriteBatch.End();
            }
        }

        /// <summary>How many tree trunks were mended in the normal buffer last frame. Printed by
        /// radiance_reliefdraws: a fix whose match never fires looks exactly like a fix that did
        /// nothing, and the first version of this one matched on an origin the wind pass had
        /// already rewritten, so it never fired at all and there was no way to see that.</summary>
        internal int TrunkJoinsMended { get; private set; }

        /// <summary>The trunk piece the game draws below a tree's canopy (Tree.stumpSourceRect).</summary>
        private const int TreeTrunkPieceWidth = 16;
        private const int TreeTrunkPieceHeight = 32;

        /// <summary>Where a recorded draw's art actually starts on screen. Position alone is not
        /// that: the origin is subtracted from it first, and this mod's own wind pass rewrites a
        /// trunk's position AND origin together to turn it about the tree's base. The corner they
        /// describe between them does not move, so it is what the pair below is matched on.</summary>
        private static Vector2 DrawnTopLeft(in SpriteDrawRecorder.Record record)
            => record.Position - record.Origin * record.Scale;

        /// <summary>Whether this trunk piece belongs to a canopy drawn just before it: the same
        /// sheet, a 48x96 block, and the placement Tree.draw gives them both - the trunk's art
        /// starts 32 across and 128 up from the canopy's anchor, give or take the few pixels a
        /// shaken tree wobbles by. Searched backwards, because the canopy is drawn first.</summary>
        private static bool TryFindCanopyFor(System.Collections.Generic.IReadOnlyList<SpriteDrawRecorder.Record> records,
            int trunkIndex, in SpriteDrawRecorder.Record trunk)
        {
            Vector2 trunkCorner = DrawnTopLeft(trunk);
            for (int j = trunkIndex - 1; j >= 0 && j >= trunkIndex - 8; j--)
            {
                SpriteDrawRecorder.Record candidate = records[j];
                if (candidate.UsesDestination
                    || !ReferenceEquals(candidate.Texture, trunk.Texture)
                    || candidate.Source.Width != StardewValley.TerrainFeatures.Tree.treeTopSourceRect.Width
                    || candidate.Source.Height != StardewValley.TerrainFeatures.Tree.treeTopSourceRect.Height)
                    continue;
                // The canopy hangs from its anchor: origin (24,96) at (tile*64+32, tile*64+64).
                Vector2 anchor = candidate.Position;
                if (Math.Abs(trunkCorner.Y - (anchor.Y - 128f)) < 2f
                    && Math.Abs(trunkCorner.X - (anchor.X - 32f)) <= 5f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Replay this frame's recorded world draw with each sheet's normal map in place of its art
        /// (see <see cref="SpriteDrawRecorder"/>, <see cref="SheetNormalCache"/>), into a buffer the
        /// flood shader reads for the relief terms. Straight-alpha blend, because the encoded normal
        /// must not be scaled by coverage. Leaves <paramref name="target"/> bound again.
        /// </summary>
        /// <summary>The sheets the replay gave a bevel to, this frame and the last completed one,
        /// for the report (see DescribeRelief).</summary>
        private readonly HashSet<string> _bevelledSheetsThisFrame = new();
        private List<string> _bevelledSheetsLastFrame = new();

        private void RenderNormalPass(ModConfig config, RenderTarget2D target)
        {
            bool wanted = config.SpriteReliefEnabled && config.FloodLightingEnabled && _normalsEffect != null;
            Approach(ref _reliefEase, wanted ? 1f : 0f, 0.1f);
            if (_reliefEase <= FadeGone)
            {
                _normalPassReady = false;
                // Switched off and faded: the maps go back to the card. They return on demand.
                if (!wanted && _sheetNormals.Count > 0)
                    _sheetNormals.Clear();
                return;
            }
            // Entries whose sheet a content patch reloaded can never be asked for again by their
            // old key: without this sweep they held their bytes forever, the budget filled with
            // ghosts, every new sheet was refused, and the relief flickered as sprites fell in
            // and out of the flat stand-in.
            _sheetNormals.SweepDisposed();
            _bevelledSheetsThisFrame.Clear();
            if (SpriteDrawRecorder.Records.Count == 0)
            {
                // No world draw was recorded this frame (a menu, a transition). Keeping the last
                // frame's buffer beats blinking the relief off for one frame - but only while the
                // camera has not moved, because this buffer is SCREEN space. Held across a camera
                // move it lights the world with a stamp of where things used to be, and because
                // the frames that record nothing come and go, that stamp flickers on and off. It
                // showed at the town fountain, whose animated tiles are among the handful the game
                // draws through the sorted batch at all, and it went away when the relief was
                // switched off and on again, which is the signature of held state rather than a
                // wrong decision. Where it cannot be trusted, hand back a flat buffer: no relief
                // for a frame is a far smaller error than relief in the wrong place.
                if (_normalPassReady && _normalRenderTarget != null
                    && (_normalPassViewport.X != Game1.viewport.X || _normalPassViewport.Y != Game1.viewport.Y))
                {
                    RenderTargetBinding[] was = _device.GetRenderTargets();
                    _device.SetRenderTarget(_normalRenderTarget);
                    _device.Clear(new Color(128, 128, 255, 0));
                    _device.SetRenderTargets(was);
                    _normalPassViewport = new Point(Game1.viewport.X, Game1.viewport.Y);
                }
                return;
            }
            int w = target.Width, h = target.Height;
            if (_normalRenderTarget == null || _normalRenderTarget.Width != w || _normalRenderTarget.Height != h)
            {
                _normalPassReady = false;
                _normalRenderTarget?.Dispose();
                // PreserveContents: read on frames whose replay was skipped (see above), which is
                // a cross-frame read - rule 7.
                _normalRenderTarget = VramTally.Track(new RenderTarget2D(_device, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "sprite normals");
            }
            _normalSpriteBatch ??= new SpriteBatch(_device);
            // Its own slot: this pass borrowed GridLightOccluders through the whole first relief
            // round, so every bench table until 2026-08-27 shows the replay's cost wearing the
            // occluder grid's name.
            long t0 = FrameCost.Begin(FrameCost.Part.ReliefNormals);
            try
            {
                _device.SetRenderTarget(_normalRenderTarget);
                _device.Clear(new Color(128, 128, 255, 0));
                // FrontToBack with the recorded depths, the order the game drew them in.
                _normalSpriteBatch.Begin(SpriteSortMode.FrontToBack, BlendState.NonPremultiplied, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone);
                if (_flatNormalTexture == null || _flatNormalTexture.IsDisposed)
                {
                    _flatNormalTexture = new Texture2D(_device, 1, 1, false, SurfaceFormat.Color);
                    _flatNormalTexture.SetData(new[] { new Color(128, 128, 255, 255) });
                }
                Texture2D flat = _flatNormalTexture;
                Effect normals = _normalsEffect!;
                _normalPassDrawn = SpriteDrawRecorder.Replay(_normalSpriteBatch,
                    // A sheet that is itself a render target is a COMPOSED picture (Fashion
                    // Sense builds the farmer that way) and a fresh instance can arrive any
                    // frame: deriving a map from each one churned the cache without end. The
                    // flat stand-in carries those sprites instead.
                    (sheet, effects) =>
                    {
                        if (sheet is RenderTarget2D)
                            return null;
                        // A map's own tilesheet is baked FLAT (see _bakeSheetFlat): it must cover
                        // what stands behind it without wearing a bevel of its own. Mirroring a
                        // flat map changes nothing, so they share one entry.
                        _bakeSheetFlat = DrawnFromMapTileSheet(sheet);
                        bool flipped = !_bakeSheetFlat && (effects & SpriteEffects.FlipHorizontally) != 0;
                        // Three derivations, three keys. Flat used to share the unflipped key, so
                        // whichever was baked first stood in for the other from then on.
                        int variant = _bakeSheetFlat ? NormalBakeFlat : flipped ? NormalBakeMirrored : NormalBakeBevelled;
                        if (variant != NormalBakeFlat)
                            _bevelledSheetsThisFrame.Add(sheet.Name ?? "(unnamed sheet)");
                        Texture2D? map = _sheetNormals.For(_device, normals, sheet, variant);
                        _bakeSheetFlat = false;
                        return map;
                    }, flat);
                _normalSpriteBatch.End();
                // The map's front layers, in their own batch so they land ON TOP whatever depth
                // they were recorded with - which is the order the game drew them, and what makes
                // a farmer standing behind a building stop showing through its wall.
                _normalSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone);
                _normalPassDrawn += SpriteDrawRecorder.ReplayFront(_normalSpriteBatch,
                    (sheet, effects) =>
                    {
                        if (sheet is RenderTarget2D)
                            return null;
                        _bakeSheetFlat = DrawnFromMapTileSheet(sheet);
                        bool flipped = !_bakeSheetFlat && (effects & SpriteEffects.FlipHorizontally) != 0;
                        // Three derivations, three keys. Flat used to share the unflipped key, so
                        // whichever was baked first stood in for the other from then on.
                        int variant = _bakeSheetFlat ? NormalBakeFlat : flipped ? NormalBakeMirrored : NormalBakeBevelled;
                        if (variant != NormalBakeFlat)
                            _bevelledSheetsThisFrame.Add(sheet.Name ?? "(unnamed sheet)");
                        Texture2D? map = _sheetNormals.For(_device, normals, sheet, variant);
                        _bakeSheetFlat = false;
                        return map;
                    }, flat);
                _normalSpriteBatch.End();
                FlattenTreeTrunkJoins();
                _bevelledSheetsLastFrame = new List<string>(_bevelledSheetsThisFrame);
                _normalPassReady = true;
                // Where this screen-space buffer was drawn from, so a later frame that cannot
                // redraw it can tell whether it is still looking at the same view.
                _normalPassViewport = new Point(Game1.viewport.X, Game1.viewport.Y);
            }
            catch (Exception ex)
            {
                // A half-drawn buffer is worse than none: only a completed replay is shown.
                _normalPassReady = false;
                try { _normalSpriteBatch.End(); } catch { }
                if (config.DebugLogging)
                    _monitor.Log($"sprite normal pass failed: {ex.Message}", LogLevel.Debug);
            }
            finally
            {
                _device.SetRenderTarget(target);
                FrameCost.End(FrameCost.Part.ReliefNormals, t0);
            }
        }

        /// <summary>Whether a placed building stands on this tile with a solid part of itself (its
        /// collision map's solid cells), so a coop blocks and a porch or a door does not.</summary>
        /// <summary>Per tile of the whole map, whether the map itself blocks a lamp there: the
        /// surface class, whether the farmer can walk on it, and the buildings' collision maps.
        /// Gathered once per (map, surface map, building count) and read by every window rebuild.</summary>
        private byte[]? _floodSolidBase;
        private int _floodSolidBaseWidth, _floodSolidBaseHeight;
        private GameLocation? _floodSolidBaseLocation;
        private SurfaceMap? _floodSolidBaseSurface;
        private int _floodSolidBaseBuildingCount = -1;

        /// <summary>
        /// Refresh the map-wide solid base when its inputs changed; otherwise nothing.
        /// </summary>
        /// <remarks>
        /// The three questions asked per tile here are map questions: the surface class comes
        /// from the surface map, walkability from the map's own layers and properties, and a
        /// building blocks by its collision map. None of them move when a chest is placed or a
        /// tree grows - those are stamped over the base afterwards, as before - so the answers
        /// hold for as long as the map, its surface map and its building list do. Asking them
        /// once for the whole map on arrival, under the warp fade, costs a few milliseconds
        /// once; asking them for every tile of the window once a second and on every tile
        /// crossing was the grid's worst frame.
        /// </remarks>
        private void EnsureFloodSolidBase(GameLocation location, SurfaceMap? surf, xTile.Layers.Layer? layer)
        {
            int buildingCount = location.buildings?.Count ?? 0;
            xTile.Layers.Layer? size = location.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            if (size == null)
            {
                _floodSolidBase = null;
                _floodSolidBaseLocation = null;
                return;
            }
            int width = size.LayerWidth, height = size.LayerHeight;
            // The other screen's copy of this map has its own SurfaceMap object too; same place
            // and same size is the same answer (LiveScreens.SamePlace).
            bool sameSurface = ReferenceEquals(surf, _floodSolidBaseSurface)
                || (surf != null && _floodSolidBaseSurface != null && surf.Width == _floodSolidBaseSurface.Width && surf.Height == _floodSolidBaseSurface.Height);
            if (_floodSolidBase != null && LiveScreens.SamePlace(location, _floodSolidBaseLocation)
                && sameSurface && buildingCount == _floodSolidBaseBuildingCount
                && width == _floodSolidBaseWidth && height == _floodSolidBaseHeight)
                return;
            if (_floodSolidBase == null || _floodSolidBase.Length != width * height)
                _floodSolidBase = new byte[width * height];
            _floodSolidBaseWidth = width;
            _floodSolidBaseHeight = height;
            _floodSolidBaseLocation = location;
            _floodSolidBaseSurface = surf;
            _floodSolidBaseBuildingCount = buildingCount;
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    bool solid;
                    if (surf != null)
                        solid = (surf.BlocksLight(tx, ty) && !CanWalkOn(location, tx, ty)) || BuildingBlocks(location, tx, ty);
                    else
                        solid = layer != null && tx < layer.LayerWidth && ty < layer.LayerHeight && layer.Tiles[tx, ty] != null;
                    _floodSolidBase[ty * width + tx] = solid ? (byte)1 : (byte)0;
                }
            }
        }

        private static bool BuildingBlocks(GameLocation location, int x, int y)
        {
            var buildings = location.buildings;
            if (buildings == null || buildings.Count == 0)
                return false;
            var tile = new Vector2(x, y);
            foreach (var building in buildings)
            {
                if (building == null)
                    continue;
                // A fish pond is water inside a knee-high kerb; nothing about it stops a lamp.
                if (building is StardewValley.Buildings.FishPond)
                    continue;
                // Cheap reject on the footprint before the collision map is consulted.
                if (x < building.tileX.Value || x >= building.tileX.Value + building.tilesWide.Value
                    || y < building.tileY.Value || y >= building.tileY.Value + building.tilesHigh.Value)
                    continue;
                // intersects() is the game's own walkability answer for a building: it consults
                // the collision map, so a porch cell or a doorway marked open does not collide.
                if (building.occupiesTile(tile) && building.intersects(new Rectangle(x * 64 + 16, y * 64 + 16, 32, 32)))
                    return true;
            }
            return false;
        }

        /// <summary>Whether the farmer can walk onto this tile, by the game's own answer. A tile
        /// they can stand on cannot be the inside of a wall, so it must not block a lamp.</summary>
        private static bool CanWalkOn(GameLocation location, int x, int y)
        {
            try
            {
                return location.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport);
            }
            catch
            {
                // Asked about a tile off its own edge a location can throw; that is not walkable.
                return false;
            }
        }

        /// <summary>A clump's sheet by name, the object sheet when it has none (every vanilla stump,
        /// boulder and log), cached because the content manager's lookup is not free per clump.</summary>
        private readonly Dictionary<string, Texture2D?> _clumpTextureCache = new();

        private Texture2D? ClumpTexture(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return Game1.objectSpriteSheet;
            // A content patch reloading the sheet disposes the instance this cache still holds;
            // handing that out draws garbage or throws. Reload it under the same name instead.
            if (!_clumpTextureCache.TryGetValue(name, out Texture2D? texture) || (texture?.IsDisposed ?? false))
            {
                try { texture = Game1.content.Load<Texture2D>(name); }
                catch { texture = null; }
                _clumpTextureCache[name] = texture;
            }
            return texture;
        }

        private void BuildSoftOccluderLevels(RenderTarget2D mask)
        {
            RenderTargetBinding[] previous = _device.GetRenderTargets();
            _floodOccluderSpriteBatch ??= new SpriteBatch(_device);
            try
            {
                Texture2D source = mask;
                for (int level = 0; level < _floodOccluderSoft.Length; level++)
                {
                    int width = Math.Max(1, mask.Width >> (level + 1));
                    int height = Math.Max(1, mask.Height >> (level + 1));
                    RenderTarget2D? target = _floodOccluderSoft[level];
                    if (target == null || target.Width != width || target.Height != height)
                    {
                        target?.Dispose();
                        target = VramTally.Track(new RenderTarget2D(_device, width, height, false, SurfaceFormat.Color, DepthFormat.None), "flood occluder mask");
                        _floodOccluderSoft[level] = target;
                    }
                    _device.SetRenderTarget(target);
                    _device.Clear(Color.Transparent);
                    // Half the size with a linear read is a 2x2 box filter; each level doubles the blur.
                    _floodOccluderSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                    _floodOccluderSpriteBatch.Draw(source, new Rectangle(0, 0, width, height), Color.White);
                    _floodOccluderSpriteBatch.End();
                    source = target;
                }
            }
            finally
            {
                _device.SetRenderTargets(previous);
            }
        }

        private static int CountFences(GameLocation location)
        {
            int fences = 0;
            foreach (var pair in location.objects.Pairs)
                if (pair.Value is Fence)
                    fences++;
            return fences;
        }

        /// <summary>
        /// Fences, bushes and boulders into the occluder mask as the shapes they are, over the
        /// tile grid. The game's own draw code places each one (a fence picks its piece from its
        /// neighbours, a bush its season and size), so the sprite batch carries a transform that
        /// turns the screen pixels those calls produce into mask texels, and the mask holds the
        /// picket gaps a lamp's comb of light comes through. Only alpha is read from it.
        /// </summary>
        private void DrawOccluderSilhouettes(RenderTarget2D target, GameLocation location, int startTileX, int startTileY, int tilesW, int tilesH)
        {
            RenderTargetBinding[] previous = _device.GetRenderTargets();
            _floodOccluderSpriteBatch ??= new SpriteBatch(_device);
            SpriteBatch spriteBatch = _floodOccluderSpriteBatch;
            try
            {
                _device.SetRenderTarget(target);
                _device.Clear(Color.Transparent);
                // LINEAR, not point: the tile grid used to be the whole mask and the shader's
                // linear sampler melted each block over a tile. Scaled up point-sampled it kept
                // hard 4x4 blocks that the sun shafts read as a grid of squares on the ground.
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                spriteBatch.Draw(_floodOccluderBaseTexture!, new Rectangle(0, 0, target.Width, target.Height), Color.White);
                spriteBatch.End();
                if (_occluderShapesEase <= 0.004f)
                    return;

                // The game draws at GlobalToLocal(viewport): put the viewport back, move to the
                // mask's first tile, and shrink 64 world pixels to FloodOccSubdivision texels.
                float toTexel = FloodOccSubdivision / 64f;
                Matrix toMask = Matrix.CreateTranslation(Game1.viewport.X - startTileX * 64f, Game1.viewport.Y - startTileY * 64f, 0f)
                              * Matrix.CreateScale(toTexel, toTexel, 1f);
                int lastTileX = startTileX + tilesW, lastTileY = startTileY + tilesH;

                // A picket is four game pixels, one texel here, and the LINEAR sampler halves a
                // one-texel line before the shadow march ever sees it: a fence cast half a shadow.
                // Drawn three times half a texel apart it is two texels wide and casts a whole one.
                for (int pass = -1; pass <= 1; pass++)
                {
                    Matrix nudged = Matrix.CreateTranslation(pass * 0.5f / toTexel, 0f, 0f) * toMask;
                    spriteBatch.Begin(SpriteSortMode.Deferred, OccluderBlendFor(FenceOccluderShare * _occluderShapesEase), SamplerState.PointClamp, null, null, null, nudged);
                    foreach (var pair in location.objects.Pairs)
                    {
                        if (pair.Value is not Fence fence)
                            continue;
                        int tileX = (int)pair.Key.X, tileY = (int)pair.Key.Y;
                        if (tileX < startTileX || tileX >= lastTileX || tileY < startTileY || tileY >= lastTileY)
                            continue;
                        try { fence.draw(spriteBatch, tileX, tileY, 1f); }
                        catch { /* a mod's fence draw threw; it casts no occlusion this build */ }
                    }
                    spriteBatch.End();
                }

                spriteBatch.Begin(SpriteSortMode.Deferred, OccluderBlendFor(BushOccluderShare * _occluderShapesEase), SamplerState.PointClamp, null, null, null, toMask);
                foreach (var pair in location.terrainFeatures.Pairs)
                {
                    if (pair.Value is StardewValley.TerrainFeatures.Bush bush && !bush.sourceRect.Value.IsEmpty)
                        StampBush(spriteBatch, bush, pair.Key);
                }
                foreach (var large in location.largeTerrainFeatures)
                {
                    if (large is StardewValley.TerrainFeatures.Bush bush && !bush.sourceRect.Value.IsEmpty)
                        StampBush(spriteBatch, bush, bush.Tile);
                }
                spriteBatch.End();

                // Tree TRUNKS as solid posts on top of their tile stamp. The stamp is one texel
                // that the linear scale-up melts over two tiles, peaking at 0.84 in the middle, so
                // a lamp beside a tree cast a shadow nobody could find. A trunk is a post about
                // two thirds of a tile wide standing on its tile; that is what blocks a lamp.
                spriteBatch.Begin(SpriteSortMode.Deferred, OccluderBlendFor(_occluderShapesEase), SamplerState.PointClamp, null, null, null, toMask);
                foreach (var pair in location.terrainFeatures.Pairs)
                {
                    // A grown tree is a post two thirds of a tile wide; a sapling at the bush and
                    // small-tree stages (3 and 4) is a thinner, shorter one. Seeds and sprouts
                    // (0 to 2) are a hand high and cast nothing worth a texel.
                    int stage = pair.Value switch
                    {
                        StardewValley.TerrainFeatures.Tree tree => tree.growthStage.Value,
                        StardewValley.TerrainFeatures.FruitTree fruitTree => fruitTree.growthStage.Value + 1,
                        _ => -1,
                    };
                    if (stage < 3)
                        continue;
                    Rectangle trunk = stage >= 5
                        ? new Rectangle((int)(pair.Key.X * 64f) + 12, (int)(pair.Key.Y * 64f) - 16, 40, 80)
                        : new Rectangle((int)(pair.Key.X * 64f) + 20, (int)(pair.Key.Y * 64f) + 8, 24, 56);
                    trunk.Offset(-Game1.viewport.X, -Game1.viewport.Y);
                    spriteBatch.Draw(Game1.staminaRect, trunk, Color.White);
                }
                spriteBatch.End();

                spriteBatch.Begin(SpriteSortMode.Deferred, OccluderBlendFor(ClumpOccluderShare * _occluderShapesEase), SamplerState.PointClamp, null, null, null, toMask);
                foreach (var clump in location.resourceClumps)
                {
                    if (clump == null)
                        continue;
                    // A vanilla clump carries no texture name: it lives on the object sheet, and
                    // asking the content manager for a null name threw, was caught, and the stump
                    // silently cast nothing, along with every boulder and log on the farm.
                    Texture2D? texture = ClumpTexture(clump.textureName.Value);
                    if (texture == null)
                        continue;
                    Rectangle source = Game1.getSourceRectForStandardTileSheet(texture, clump.parentSheetIndex.Value, 16, 16);
                    source.Width = clump.width.Value * 16;
                    source.Height = clump.height.Value * 16;
                    // A clump draws its top-left at its tile, scale 4 (ResourceClump.draw).
                    spriteBatch.Draw(texture, Game1.GlobalToLocal(Game1.viewport, clump.Tile * 64f), source, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                }
                spriteBatch.End();

                // PLACED THINGS: kegs, chests, machines, scarecrows, signs and floor furniture, each
                // as the sprite the game draws for it, so a keg stands in the light as a keg and a
                // bench as a bench. Until this pass a lamp saw straight through all of them while
                // the sun did not, so the same barrel threw a shadow at noon and none at night.
                // Under its own switch. Two rules carry over from the sun's object shadows (forage
                // lies flat, a passable object is not a body) and one is this pass's own: a thing
                // that IS a light casts no occlusion, or a torch would stand inside its own shadow
                // and put its own pool out. Only the tiles inside the mask are asked about, by
                // tile, the way the sun's pass does it; a built-up farm holds thousands of objects
                // and walking them all was the wrong side of this cost.
                float propShare = PropOccluderShare * _occluderShapesEase * _occluderPropsEase;
                if (propShare > 0.004f)
                {
                    GatherOccluderLightPositions();
                    spriteBatch.Begin(SpriteSortMode.Deferred, OccluderBlendFor(propShare), SamplerState.PointClamp, null, null, null, toMask);
                    var placedObjects = location.objects;
                    for (int tileY = startTileY; tileY < lastTileY; tileY++)
                    {
                        for (int tileX = startTileX; tileX < lastTileX; tileX++)
                        {
                            var tile = new Vector2(tileX, tileY);
                            if (!placedObjects.TryGetValue(tile, out SObject placed) || placed == null || placed.isTemporarilyInvisible)
                                continue;
                            if (placed is Fence || placed is CrabPot || placed.IsSpawnedObject)
                                continue;
                            // Ground litter is not a body: a weed, a twig or a stone is a hand high,
                            // and stamping each one solid filled a farm's mask with tile squares. It
                            // gets its sprite alone below, so a tuft still throws a tuft's shadow.
                            bool litter = placed.IsWeeds() || placed.IsTwig() || placed.IsBreakableStone();
                            if (!placed.bigCraftable.Value && placed.isPassable())
                                continue;
                            if (LightStandsIn(new Rectangle(tileX * 64, tileY * 64, 64, 64)))
                                continue;
                            if (!TryPlacedArt(placed.QualifiedItemId, out Texture2D? art, out Rectangle source) || art == null || source.IsEmpty)
                                continue;
                            // The FOOTPRINT blocks, solid: what stands between a lamp and the floor
                            // is the body on the ground, not the holes in its picture. Drawn from
                            // the sprite alone, a table's legs let the light through in fingers
                            // that fanned out across the room behind it. Then the sprite over it,
                            // so the thing's own face counts as occluder and keeps its light (see
                            // pixelOpen in floodlight.fx) instead of standing in its own shadow.
                            //
                            // AS WIDE AS THE THING, NOT AS WIDE AS ITS TILE. This filled the whole
                            // 64 square whatever stood on it, so a keg two thirds of a tile across
                            // blocked a lamp with a square, and the square is what a player saw:
                            // "there is a visible box-shaped light around the machine, the light
                            // doesn't blend or spread naturally". Taking the span of the sprite's
                            // own base keeps every reason the solid block exists - the gaps between
                            // a table's legs are still closed, because a span is filled, not traced
                            // - and stops the block claiming ground the object never stood on.
                            if (!litter)
                            {
                                var (baseLeft, baseRight) = ArtBaseSpan(art, source);
                                var footprint = new Rectangle(tileX * 64 + baseLeft * 4, tileY * 64,
                                                              Math.Max(4, (baseRight - baseLeft) * 4), 64);
                                footprint.Offset(-Game1.viewport.X, -Game1.viewport.Y);
                                spriteBatch.Draw(Game1.staminaRect, footprint, Color.White);
                            }
                            // Object.draw: a big craftable's 16x32 cell is drawn from the tile above
                            // its own, a small object's 16x16 cell fills its tile, both at scale 4.
                            var at = new Vector2(tileX * 64f, placed.bigCraftable.Value ? tileY * 64f - 64f : tileY * 64f);
                            spriteBatch.Draw(art, Game1.GlobalToLocal(Game1.viewport, at), source, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                        }
                    }
                    foreach (Furniture furniture in location.furniture)
                    {
                        if (furniture == null || furniture.isTemporarilyInvisible)
                            continue;
                        int kind = furniture.furniture_type.Value;
                        // Rugs lie flat; windows, wall pieces and paintings hang on the wall.
                        if (kind == 12 || kind == 6 || kind == 13 || kind == 17)
                            continue;
                        Vector2 tile = furniture.TileLocation;
                        if (tile.X < startTileX || tile.X >= lastTileX || tile.Y < startTileY || tile.Y >= lastTileY)
                            continue;
                        Rectangle box = furniture.boundingBox.Value;
                        if (LightStandsIn(box))
                            continue;
                        Rectangle source = furniture.sourceRect.Value;
                        if (source.IsEmpty || !TryPlacedArt(furniture.QualifiedItemId, out Texture2D? art, out _) || art == null)
                            continue;
                        // Footprint first, solid, for the same reason as above; the bounding box IS
                        // the footprint for floor furniture.
                        Rectangle footprint = box;
                        footprint.Offset(-Game1.viewport.X, -Game1.viewport.Y);
                        spriteBatch.Draw(Game1.staminaRect, footprint, Color.White);
                        // Furniture.draw: the sprite's bottom edge rests on the bounding box's.
                        var at = new Vector2(box.X, box.Bottom - source.Height * 4f);
                        spriteBatch.Draw(art, Game1.GlobalToLocal(Game1.viewport, at), source, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
                    }
                    spriteBatch.End();
                }
            }
            finally
            {
                _device.SetRenderTargets(previous);
            }
        }
    }
}
