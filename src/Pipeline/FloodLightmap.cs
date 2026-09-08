using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Flood-propagation lightmap (the Terraria technique): a small CPU grid over
    /// the visible tiles, seeded with per-tile SKY exposure (occluders from the Height
    /// Framework: tiles under buildings/canopies get no direct sky) and the game's REAL point
    /// lights, then swept directionally so light decays through air and dies quickly inside
    /// solids. A light 3×3 blur is folded back in as a fake indirect bounce. The result is
    /// uploaded as a tiny texture and multiplied over the scene with bilinear filtering —
    /// occlusion-aware ambient, shade under canopies, coloured lamp pools and window spill,
    /// all for a fraction of a millisecond of CPU and no fake rays from bright sprites.
    /// </summary>
    internal sealed class FloodLightmap
    {
        /// <summary>Off-screen margin in tiles so lights just outside the view still spill in.</summary>
        private const int PadTiles = 6;
        /// <summary>Per-cell survival factor while sweeping through open ground.</summary>
        private const float AirDecay = 0.86f;
        /// <summary>Per-cell survival through solid/occluding tiles (light dies in ~2-3 tiles).</summary>
        private const float SolidDecay = 0.65f;
        /// <summary>How much of a full overcast a SNOWFALL is worth to the morning's colour.
        /// A snowy sky is bright and the ground under it is a reflector, so it is neither the
        /// blue-grey of rain nor the warm low sun of a clear day. Half, which is the same share
        /// the shadow pass gives a snowfall for the same reason.</summary>
        private const float SnowOvercastShare = 0.5f;

        /// <summary>Sky light an occluded cell still starts with, as a fraction of the open sky.
        /// It used to be zero, which is what a roof does to DIRECT sun but not to the sky as a
        /// whole: a wall in the open still faces a bright hemisphere. Zero also made occlusion a
        /// cliff — one step took a block of cells from full sky to nothing, and the sweeps carried
        /// that into the open ground beside it, so crossing into a built-up stretch of a map
        /// visibly dimmed the screen. Shade, not darkness.</summary>
        private const float OccludedSeed = 0.5f;
        /// <summary>Cell values are stored ×0.5 in the texture so >1 (glow) survives; shader ×2.</summary>
        internal const float StorageScale = 0.5f;

        private int _lastStartTileX = int.MinValue, _lastStartTileY = int.MinValue, _lastBuildTick = int.MinValue;
        private int _lastInputsHash;

        /// <summary>What the rebuild gate is allowed to do. Author diagnostic only, set by
        /// radiance_flood and never persisted.</summary>
        internal enum RebuildOverride { Auto, Every, Freeze }
        /// <summary>Diagnostic override for the rebuild gate. Auto in every normal session.</summary>
        internal static RebuildOverride RebuildMode = RebuildOverride.Auto;
        private GameLocation? _lastBuildLocation;

        /// <summary>
        /// Everything that can change the lightmap's CONTENT while the camera stands still,
        /// folded into one number. The rebuild used to run on a flat 3-tick clock, which is the
        /// right cadence for a flickering hearth and a 20-times-a-second tax everywhere else:
        /// standing in a windowless noon field paid ~1k tile lookups and three full-window CPU
        /// sweeps for a texture that came out identical every time. The measured cost was ~0.25 ms
        /// per frame in every scene, the second most expensive thing the mod did.
        /// <para>
        /// The hash covers the light list (a lamp toggling, a light walking on screen), the eased
        /// window scales (so the sun patch still FADES per frame while a switch is mid-ease), and
        /// the ambient tint (dusk and weather ramps). The game clock is deliberately NOT in it:
        /// it moves every frame, and its effect over the fallback cadence below is far below what
        /// an eye can pick out. Flame flicker is not in it either, and no longer needs to be: the
        /// bounce this grid carries does not flicker at all now (see the seed), so there is nothing
        /// left that wanted a faster clock than a fire once did.
        /// </para>
        /// </summary>
        internal static int HashLightInputs(GameLocation location) => HashLightInputs(location, 16f);

        /// <param name="positionPixelsPerHashStep">How far a light must move before the hash changes
        /// and the map rebuilds. The flood keeps 16 px: its bounce is blurred and a finer clock buys
        /// nothing. The cascades pass 4 px: their map carries a carried light's mound crisply, and at
        /// 16 px the mound stepped a quarter tile at a time behind a walking glow ring - with the
        /// sub-tile seeds it now glides, but only as often as this lets it rebuild.</param>
        internal static int HashLightInputs(GameLocation location, float positionPixelsPerHashStep)
        {
            unchecked
            {
                int hash = 17;
                var lights = Game1.currentLightSources;
                if (lights != null)
                {
                    foreach (var lightSource in lights.Values)
                    {
                        if (!ShadowRenderer.WindowGlowing(location, lightSource))
                            continue;
                        hash = hash * 31 + (int)(lightSource.position.Value.X / positionPixelsPerHashStep);
                        hash = hash * 31 + (int)(lightSource.position.Value.Y / positionPixelsPerHashStep);
                        hash = hash * 31 + (int)(lightSource.radius.Value * 16f);
                        hash = hash * 31 + lightSource.textureIndex.Value;
                    }
                }
                hash = hash * 31 + (int)(WindowPatchScale * 255f);
                hash = hash * 31 + (int)(WindowRoomScale * 255f);
                hash = hash * 31 + Game1.ambientLight.PackedValue.GetHashCode();
                return hash;
            }
        }
        private Vector3[] _lightCells = Array.Empty<Vector3>();
        private Vector3[] _blurredLightCells = Array.Empty<Vector3>();
        /// <summary>Row pass of the separable bounce blur; see where it is filled.</summary>
        private Vector3[] _blurRowScratch = Array.Empty<Vector3>();
        private float[] _lightDecay = Array.Empty<float>();
        private Color[] _lightmapPixels = Array.Empty<Color>();
        private Texture2D? _lightmapTexture;
        private Texture2D? _lightmapTextureSpare;   // its pair - see TextureDoubleBuffer

        internal Texture2D? Texture => _lightmapTexture;
        /// <summary>World tile coordinate of the map's (0,0) cell.</summary>
        internal Vector2 Origin;
        internal Vector2 MapSize;

        /// <summary>The lightmap value at one world tile, scaled back out of the ×0.5 storage.
        /// For the radiance_debug flood caption: flick a light switch and read off whether the
        /// map actually moved instead of trusting the composite to show it.</summary>
        internal string Probe(int tileX, int tileY)
        {
            if (_lightmapTexture == null)
                return "no texture";
            int cellX = tileX - (int)Origin.X;
            int cellY = tileY - (int)Origin.Y;
            if (cellX < 0 || cellY < 0 || cellX >= (int)MapSize.X || cellY >= (int)MapSize.Y)
                return "off-map";
            Color pixel = _lightmapPixels[cellY * (int)MapSize.X + cellX];
            float stored = pixel.R / 255f / StorageScale;   // stored ×StorageScale (0.5); scale back for display
            return stored.ToString("F2");
        }

        /// <summary>
        /// What the location IS, as every seed pass needs to know it: whether the sky is overhead,
        /// whether the game itself renders this place dark, and what an unoccluded cell starts at.
        /// Carried as one value because four separate answers escaping one block is four
        /// out-parameters, and nobody can read those at the call site.
        /// </summary>
        internal readonly struct SceneSeed
        {
            public readonly bool Outdoors;
            /// <summary>Places the game renders dark BY DESIGN (mines, volcano): strictly add-only,
            /// because multiplying on top of vanilla dark read as pitch black.</summary>
            public readonly bool ScriptedDark;
            /// <summary>The game already darkens this location, so the flood may only add light.</summary>
            public readonly bool VanillaDark;
            /// <summary>Flat seed for a vanilla-dark room, driven by the night-darkness slider.</summary>
            public readonly float NightSeed;
            /// <summary>Colour and strength an open cell receives from the sky.</summary>
            public readonly Vector3 Sky;

            public SceneSeed(bool outdoors, bool scriptedDark, bool vanillaDark, float nightSeed, Vector3 sky)
            {
                Outdoors = outdoors;
                ScriptedDark = scriptedDark;
                VanillaDark = vanillaDark;
                NightSeed = nightSeed;
                Sky = sky;
            }
        }

        /// <summary>The tile rectangle this build covers: its top-left world tile and its size in
        /// cells. One parameter in place of the four that were threaded through every phase.</summary>
        internal readonly struct TileWindow
        {
            public readonly int TileX, TileY, TilesWide, TilesHigh;
            public TileWindow(int tileX, int tileY, int tilesWide, int tilesHigh)
                { TileX = tileX; TileY = tileY; TilesWide = tilesWide; TilesHigh = tilesHigh; }
            public int Count => TilesWide * TilesHigh;
        }


        internal bool Build(GraphicsDevice graphicsDevice, int width, int height, ModConfig config)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return false;

            int windowTileX = (int)Math.Floor(Game1.viewport.X / 64f) - PadTiles;
            int windowTileY = (int)Math.Floor(Game1.viewport.Y / 64f) - PadTiles;
            // Window size from the VIEWPORT (world px), never from the render target: screen
            // px / 64 undercounts tiles when zoomed out, and the window edge showed up as a
            // hard rectangle of missing GI in the middle of the screen.
            int tilesWide = Math.Max(1, Game1.viewport.Width / 64 + 2) + PadTiles * 2;
            int tilesHigh = Math.Max(1, Game1.viewport.Height / 64 + 2) + PadTiles * 2;
            int count = tilesWide * tilesHigh;

            // Rebuild when an INPUT changed, not on a clock. A tile crossing or resize always
            // rebuilds; a changed light list, window ease or ambient tint rebuilds (see
            // HashLightInputs); otherwise the fallback cadence only covers what the hash cannot
            // see, which is the game clock's slow drift. A third of a second of that is a fraction
            // of a game-minute, an order of magnitude below anything that reads as a step. Fires
            // used to force this to 3 and no longer do: nothing in this grid flickers.
            int inputsHash = HashLightInputs(location);
            const int cadence = 20;
            // Diagnostic override (radiance_flood). Freeze holds the last grid no matter what, so
            // anything still moving on screen provably is not this grid; Every rebuilds it on every
            // frame, so anything that stops moving provably WAS the rebuild rate rather than the
            // content. Between the two answers there is nothing left to guess about.
            if (RebuildMode == RebuildOverride.Freeze && _lightmapTexture != null
                && ReferenceEquals(location, _lastBuildLocation)
                && _lightmapTexture.Width == tilesWide && _lightmapTexture.Height == tilesHigh)
                return true;
            // The location is part of the identity, not the hash: two maps can put the camera at
            // the same tile with the same lights (none), and at the old 3-tick clock showing the
            // previous map's lightmap for 50 ms was invisible where a third of a second is not.
            if (RebuildMode != RebuildOverride.Every
                && _lightmapTexture != null && ReferenceEquals(location, _lastBuildLocation)
                && windowTileX == _lastStartTileX && windowTileY == _lastStartTileY
                && _lightmapTexture.Width == tilesWide && _lightmapTexture.Height == tilesHigh
                && inputsHash == _lastInputsHash
                && Game1.ticks - _lastBuildTick < cadence)
                return true;
            _lastStartTileX = windowTileX; _lastStartTileY = windowTileY; _lastBuildTick = Game1.ticks;
            _lastInputsHash = inputsHash;
            _lastBuildLocation = location;

            if (_lightCells.Length < count)
            {
                _lightCells = new Vector3[count];
                _blurredLightCells = new Vector3[count];
                _blurRowScratch = new Vector3[count];
                _lightDecay = new float[count];
                _lightmapPixels = new Color[count];
            }

            // Timed in three phases for the report (PhaseCost): the seeds read the game, the
            // sweeps are array work that could run on a worker, the upload is the card's.
            long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            SceneSeed scene = DescribeScene(location, config);
            var window = new TileWindow(windowTileX, windowTileY, tilesWide, tilesHigh);
            SeedSkyExposure(location, scene, window);
            SeedLightSources(location, scene, window, subTileSeeds: false);
            SeedWindowGlows(location, scene, window);
            phaseStart = PhaseCost.NoteSince("flood lightmap: seeds (game questions)", phaseStart);
            FloodSweeps(window);
            BounceBlur(window);
            ComposeLightmapPixels(scene, window);
            phaseStart = PhaseCost.NoteSince("flood lightmap: sweeps + blur + compose", phaseStart);

            // Into the pair's spare, never into the texture the lighting pass may still be
            // reading (TextureDoubleBuffer): this is the grid whose worst frames carried a 2 ms
            // GPU column against a 0.025 ms average.
            _lightmapTexture = TextureDoubleBuffer.UploadIntoSpare(graphicsDevice, ref _lightmapTextureSpare,
                _lightmapTexture, tilesWide, tilesHigh, SurfaceFormat.Color, "flood lightmap", _lightmapPixels, count);
            PhaseCost.NoteSince("flood lightmap: upload", phaseStart);
            Origin = new Vector2(windowTileX, windowTileY);
            MapSize = new Vector2(tilesWide, tilesHigh);
            return true;
        }

        /// <summary>Read the location once: what the seed passes below all need to agree on.</summary>
        internal static SceneSeed DescribeScene(GameLocation location, ModConfig config)
        {
            // ---- Seed pass: sky exposure + per-cell decay from the occluder grid ----
            // The flood is RELATIVE lighting: the game's own day/night & scripted darkness
            // stay in charge of the global level. Locations the game already darkens
            // (mines, volcano, any non-white ambient) run in ADD-ONLY mode — every cell
            // seeds at 1.0 so lamps enrich and cast shadows but nothing gets darker than
            // vanilla (multiplying on top of vanilla dark read as pitch black).
            bool outdoors = location.IsOutdoors;
            // Places the game itself renders dark by design. These are the ones that went pitch
            // black when anything multiplied on top, so they stay strictly add-only.
            bool scriptedDark = !outdoors &&
                (location is StardewValley.Locations.MineShaft || location is StardewValley.Locations.VolcanoDungeon);
            // A storm dims a house's ambient a hair under white, but it is still day: the flat
            // add-only night seed is for places the game keeps dark, not for a daytime weather
            // dip. Gating the ambient term on real night stops a stormy morning from flipping the
            // room to a flat-bright seed while a clear one keeps the (dimmer) daylight curve -
            // the exact inverse of how daylight works, and how "dark on clear, bright on storm"
            // (1115938) was reported.
            bool itIsNight = GameClock.MinutesNow() >= ShadowRenderer.TrulyDarkMinutes() - 60f;
            bool ambientDark = Game1.ambientLight.R < 245 || Game1.ambientLight.G < 245 || Game1.ambientLight.B < 245;
            bool vanillaDark = !outdoors && (scriptedDark || (ambientDark && itIsNight));
            // A HOUSE at midnight is not a mine. The game tints it down a little and then leaves
            // it evenly lit, so the fireplace and the lamps have nothing to stand out against.
            // Add a second, gentle layer there - and let the night-darkness slider drive it,
            // which is the setting players have been given for exactly this and which did
            // nothing at all on this lighting model until now.
            float nightSeed = vanillaDark && !scriptedDark
                ? MathHelper.Clamp(1f - config.LightingNightDarkness * 0.38f, 0.45f, 1f)
                : 1f;
            Vector3 sky = SkyColour(outdoors, config);
            return new SceneSeed(outdoors, scriptedDark, vanillaDark, nightSeed, sky);
        }

        /// <summary>
        /// The light seeds alone - lamps, fires, windows and the columns they spill - on an EMPTY
        /// grid, packed into <paramref name="pixels"/> x <paramref name="storageScale"/> for the
        /// cascades' emitter texture (see RadianceCascades). The sky is deliberately not seeded:
        /// to the cascades it is what a ray sees when nothing stops it, not a thing on the grid.
        /// Returns how many cells carry a seed, for the debug caption.
        /// </summary>
        internal int SeedEmitters(GameLocation location, in SceneSeed scene, in TileWindow window, Color[] pixels, float storageScale)
        {
            int count = window.Count;
            if (_lightCells.Length < count)
            {
                _lightCells = new Vector3[count];
                _blurredLightCells = new Vector3[count];
                _blurRowScratch = new Vector3[count];
                _lightDecay = new float[count];
                _lightmapPixels = new Color[count];
            }

            // ---- Split the seeds into what stood still and what moved. ----
            // Every write in this path is a Vector3.Max, and max commutes exactly, so seeding the
            // still lights once, keeping the grid, and laying the moving lights over the copy is
            // the same result TO THE BIT as re-walking everything - not an approximation. The
            // cache is only reused while every input the still half reads is bit-identical (the
            // signature hashes raw float bits, stricter than the rebuild gate's quantised hash),
            // so a hit cannot differ from a fresh pass. What earns the machinery: a carried glow
            // ring rebuilds this grid every 4 px of a walk, and each rebuild re-walked every lamp
            // and window in town to re-derive a grid that had not changed.
            _movingLightIds.Clear();
            _lightSeedHashesCurrent.Clear();
            if (!ReferenceEquals(location, _staticSeedLocation))
            {
                _staticSeedLocation = location;
                _lightSeedHashesPrevious.Clear();
                _staticSeedValid = false;
            }
            int signature = HashStaticSeedInputs(location, scene, window);
            var lightsNow = Game1.currentLightSources;
            if (lightsNow != null)
            {
                foreach (var pair in lightsNow)
                {
                    LightSource lightSource = pair.Value;
                    string id = pair.Key;
                    int seedHash = LightSeedHash(location, lightSource);
                    _lightSeedHashesCurrent[id] = seedHash;
                    bool moved = !_lightSeedHashesPrevious.TryGetValue(id, out int previousSeedHash) || previousSeedHash != seedHash;
                    // A window light never counts as moving: the window-glow pass reads every
                    // window's position for its covered-check, so a change there has to rebuild
                    // the still half anyway - and windows do not walk.
                    if (moved && lightSource.lightContext.Value != LightSource.LightContext.WindowLight)
                        _movingLightIds.Add(id);
                    else
                        unchecked { signature += seedHash; }   // set-sum: dictionary order must not matter
                }
            }
            (_lightSeedHashesPrevious, _lightSeedHashesCurrent) = (_lightSeedHashesCurrent, _lightSeedHashesPrevious);

            if (_staticSeedCells.Length < count)
            {
                _staticSeedCells = new Vector3[count];
                _staticSeedValid = false;
            }
            if (!_staticSeedValid || signature != _staticSeedSignature)
            {
                // Full price, as every build used to pay: the still lights and the windows.
                // (LastWindowSeed only refreshes here, which is honest: it describes the pass
                // that produced the cached grid.)
                Array.Clear(_lightCells, 0, count);
                SeedLightSources(location, scene, window, subTileSeeds: true, skipIds: _movingLightIds);
                SeedWindowGlows(location, scene, window);
                Array.Copy(_lightCells, _staticSeedCells, count);
                _staticSeedSignature = signature;
                _staticSeedValid = true;
            }
            else
            {
                Array.Copy(_staticSeedCells, _lightCells, count);
            }
            if (_movingLightIds.Count > 0)
                SeedLightSources(location, scene, window, subTileSeeds: true, onlyIds: _movingLightIds);
            // The daylight sink AGAIN, for the cascades only. The seeds already carry it once,
            // calibrated for the flood's blurred half-weight bounce - but the cascades deliver a
            // lamp's light straight to whatever stands near it, and one sink left a carried glow
            // ring repainting fences at one in the afternoon: measured +300 over a 740 sky on the
            // tile beside the player, and a fence's shade fading by 48 from four tiles away, which
            // read as "the shade on things comes and goes as I walk". Squared, midday is 0.12 of
            // full; at night the sink is exactly 1 and a carried lamp still washes the walls it
            // passes, which is the point of the model.
            float daylightSink = scene.Outdoors
                ? 1f - 0.65f * (1f - MathHelper.Clamp(Math.Abs(GameClock.MinutesNow() - 750f) / 270f, 0f, 1f))
                : 1f;
            float packScale = storageScale * daylightSink;
            int seeded = 0;
            for (int cellIndex = 0; cellIndex < count; cellIndex++)
            {
                Vector3 cell = _lightCells[cellIndex];
                if (cell.X > 0.001f || cell.Y > 0.001f || cell.Z > 0.001f)
                    seeded++;
                pixels[cellIndex] = new Color(
                    (byte)MathHelper.Clamp(cell.X * 255f * packScale, 0f, 255f),
                    (byte)MathHelper.Clamp(cell.Y * 255f * packScale, 0f, 255f),
                    (byte)MathHelper.Clamp(cell.Z * 255f * packScale, 0f, 255f), (byte)255);
            }
            return seeded;
        }

        // ---- The still-seed cache (see SeedEmitters) ----
        private Vector3[] _staticSeedCells = Array.Empty<Vector3>();
        private int _staticSeedSignature;
        private bool _staticSeedValid;
        private GameLocation? _staticSeedLocation;
        private Dictionary<string, int> _lightSeedHashesPrevious = new();
        private Dictionary<string, int> _lightSeedHashesCurrent = new();
        private readonly HashSet<string> _movingLightIds = new();

        /// <summary>Raw-bit hash of everything one light contributes to the seed grid, including
        /// whether the glow gate lets it seed at all: a window going dark changes WHICH lights
        /// seed, not any of the numbers on the light.</summary>
        private static int LightSeedHash(GameLocation location, LightSource lightSource)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + lightSource.position.Value.X.GetHashCode();
                hash = hash * 31 + lightSource.position.Value.Y.GetHashCode();
                hash = hash * 31 + lightSource.radius.Value.GetHashCode();
                hash = hash * 31 + lightSource.textureIndex.Value;
                hash = hash * 31 + (int)lightSource.color.Value.PackedValue;
                hash = hash * 31 + (int)lightSource.lightContext.Value;
                hash = hash * 31 + (ShadowRenderer.WindowGlowing(location, lightSource) ? 1 : 0);
                return hash;
            }
        }

        /// <summary>Raw-bit hash of every input the still half of the seed pass reads besides the
        /// lights themselves: the window rectangle, the scene, the clock (midday sink, window
        /// daylight), the weather, and the glow-sprite list some rooms publish their windows as.
        /// Bit-strict on purpose - a cache reused under a changed input would no longer be
        /// identical to a fresh pass, and identical is the whole contract.</summary>
        private static int HashStaticSeedInputs(GameLocation location, in SceneSeed scene, in TileWindow window)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + window.TileX; hash = hash * 31 + window.TileY;
                hash = hash * 31 + window.TilesWide; hash = hash * 31 + window.TilesHigh;
                hash = hash * 31 + ((scene.Outdoors ? 1 : 0) | (scene.ScriptedDark ? 2 : 0) | (scene.VanillaDark ? 4 : 0));
                hash = hash * 31 + scene.NightSeed.GetHashCode();
                hash = hash * 31 + scene.Sky.X.GetHashCode();
                hash = hash * 31 + scene.Sky.Y.GetHashCode();
                hash = hash * 31 + scene.Sky.Z.GetHashCode();
                hash = hash * 31 + WindowPatchScale.GetHashCode();
                hash = hash * 31 + WindowRoomScale.GetHashCode();
                hash = hash * 31 + Game1.ambientLight.PackedValue.GetHashCode();
                hash = hash * 31 + GameClock.MinutesNow().GetHashCode();
                hash = hash * 31 + ((Game1.isRaining ? 1 : 0) | (Game1.isSnowing ? 2 : 0) | (Game1.isLightning ? 4 : 0));
                hash = hash * 31 + (Game1.currentSeason?.GetHashCode() ?? 0);
                hash = hash * 31 + Game1.dayOfMonth;
                if (location.lightGlows is { } glows)
                {
                    hash = hash * 31 + glows.Count;
                    foreach (Vector2 glowPoint in glows)
                    {
                        hash = hash * 31 + glowPoint.X.GetHashCode();
                        hash = hash * 31 + glowPoint.Y.GetHashCode();
                    }
                }
                return hash;
            }
        }

        /// <summary>Sky exposure and per-cell decay, read off the occluder grid.</summary>
        private void SeedSkyExposure(GameLocation location, in SceneSeed scene, in TileWindow window)
        {
            var surf = SurfaceMap.For(location);
            for (int j = 0; j < window.TilesHigh; j++)
            {
                for (int i = 0; i < window.TilesWide; i++)
                {
                    int cellIndex = j * window.TilesWide + i;
                    bool solid = false;
                    // Sky occlusion only makes sense OUTDOORS. Interiors are already under a roof,
                    // and every interior tile carries Front-layer art (upper walls), which the
                    // height classifier reports as Roof — treating those as scene.Sky occluders zeroed
                    // the whole room's lightmap (black scene, then the warm lamp seed flooded it
                    // orange). Indoors, leave every cell open so ambient + lamps light it normally.
                    // Only WALLS and ROOF/canopy block scene.Sky light. Decks (piers, bridges) have
                    // height 1 but are walk-on-top surfaces OPEN to the scene.Sky — treating them as
                    // solid turned the whole beach pier into a giant dark pool. Water is open too.
                    if (surf != null && scene.Outdoors)
                        solid = surf.BlocksLight(window.TileX + i, window.TileY + j);
                    _lightDecay[cellIndex] = solid ? SolidDecay : AirDecay;
                    // Open cells receive direct scene.Sky light; occluded cells only what floods in
                    // from their surroundings → soft shade under trees/buildings for free.
                    _lightCells[cellIndex] = scene.VanillaDark ? new Vector3(scene.NightSeed) : (solid ? scene.Sky * OccludedSeed : scene.Sky);
                }
            }

        }

        /// <summary>
        /// Where a light OUTSIDE the grid seeds, and how much of it arrives: the nearest cell on
        /// the grid's edge, carrying what the sweep would have carried across the missing cells.
        /// </summary>
        /// <remarks>
        /// <para>The grid is the visible tiles plus <see cref="Pad"/>, and a light beyond it used to
        /// be skipped. Every seed then depended on where the camera stood, and the camera moves in
        /// whole tiles: a lamp entering the padding fed nothing one frame and its full seed the
        /// next, or, after the first attempt at this (a fade across the padding), a quarter of it
        /// per tile crossed - which was still a step, and one that now landed on the visible
        /// columns instead of six tiles outside them. Simulated cell for cell before this was
        /// written: in a night town the fade produced a step on eighteen of forty crossings, up to
        /// eleven of 255 on screen, where the plain cut produced one; and seeding every light,
        /// clamped, produced none.</para>
        /// <para>Clamped is exact for the sweep this grid runs. Propagation is a max over
        /// axis-aligned paths with one factor of <see cref="AirDecay"/> per cell, so the value a
        /// light would have handed the edge cell across open ground is its seed times the decay
        /// raised to the Manhattan distance, and the sweep carries on inward from there exactly as
        /// it would have. Nothing about the result depends on where the edge is, which is the
        /// whole point: the grid can be rebuilt at any origin and read the same in the world.
        /// Occluders outside the grid are treated as air, which is the far tail of a pool the
        /// grid never showed at all before.</para>
        /// </remarks>
        private static bool ClampSeed(ref int seedColumn, ref int seedRow, ref float intensity, in TileWindow window)
        {
            int clampedColumn = Math.Clamp(seedColumn, 0, window.TilesWide - 1);
            int clampedRow = Math.Clamp(seedRow, 0, window.TilesHigh - 1);
            int away = Math.Abs(seedColumn - clampedColumn) + Math.Abs(seedRow - clampedRow);
            if (away > 0)
                intensity *= (float)Math.Pow(AirDecay, away);
            seedColumn = clampedColumn;
            seedRow = clampedRow;
            return intensity > 0.002f;
        }

        /// <param name="subTileSeeds">Seed each light into the four cells around its TRUE position,
        /// weighted bilinearly, instead of snapping to one tile. The cascades want this: their map
        /// rebuilds as a carried light moves (the hash reads position at 16 px), and with the seed
        /// snapped to whole tiles the light's mound TELEPORTED a tile at a time - the ground around
        /// a walking player with a glow ring flickered at every tile crossing. The flood keeps the
        /// snap: its bounce is blurred to softness anyway, and its output is a verified baseline.</param>
        /// <param name="onlyIds">Seed only these lights (the moving half of the emitter split).</param>
        /// <param name="skipIds">Seed everything but these (the still half). Both null: everything,
        /// which is what the flood's own build wants.</param>
        private void SeedLightSources(GameLocation location, in SceneSeed scene, in TileWindow window, bool subTileSeeds,
            HashSet<string>? onlyIds = null, HashSet<string>? skipIds = null)
        {
            // ---- Seed the game's real light sources (lamps, torches, fires, windows) ----
            var lights = Game1.currentLightSources;
            if (lights != null)
            {
                foreach (var pair in lights)
                {
                    var lightSource = pair.Value;
                    if (onlyIds != null && !onlyIds.Contains(pair.Key))
                        continue;
                    if (skipIds != null && skipIds.Contains(pair.Key))
                        continue;
                    if (!ShadowRenderer.WindowGlowing(location, lightSource))   // stale/dark window: not emitting
                        continue;
                    // The TRUE cell, which may lie outside the grid; the columns below are laid
                    // from it so their cells stay where they are in the world. The seed itself is
                    // clamped onto the grid, decayed for the distance (see ClampSeed).
                    // The same drop the direct pool takes, or the bounce would sit a tile above
                    // the pool it is supposed to be the bounce of. See ShadowRenderer.FlameGlowOffset.
                    Vector2 glowPosition = lightSource.position.Value
                        + ShadowRenderer.FlameGlowOffset(location, lightSource.position.Value, lightSource.textureIndex.Value);
                    int trueSeedColumn = (int)(glowPosition.X / 64f) - window.TileX;
                    int trueSeedRow = (int)(glowPosition.Y / 64f) - window.TileY;
                    int seedColumn = trueSeedColumn, seedRow = trueSeedRow;
                    // INDIRECT spill (~half strength): the crisp direct pool + its per-light shadows
                    // are computed analytically in floodlight.effect; the flood carries the bounce-like
                    // glow that bends around corners and through doorways. Outdoors it sits above 1.0
                    // so it beats the dimmed night ground; indoors it stays gentle.
                    //
                    // NO FLAME FLICKER HERE, on purpose. This grid is a CPU sweep that cannot afford
                    // to run every frame, so multiplying the seed by the flicker sampled it at the
                    // rebuild rate and held it in between: the bounce moved in 3-frame steps while
                    // the direct pool around the same fire moved smoothly every frame, and the two
                    // rates beating against each other is what read as the floor around a lamp
                    // flashing. Physically the bounce is the half that should NOT snap anyway - it
                    // is light that has crossed the room and come back off a wall. The flame still
                    // breathes where it is visible, in the direct pool (RenderPipeline.Lighting)
                    // and in the shadows it casts, both of which are per-frame and free.
                    float intensity = MathHelper.Clamp(0.55f + 0.30f * lightSource.radius.Value, 0.6f, 1.7f) * (scene.Outdoors ? 1.25f : 0.5f);
                    if (!subTileSeeds && !ClampSeed(ref seedColumn, ref seedRow, ref intensity, window))
                        continue;
                    // The same midday sink the DIRECT pools got ("a street lamp at noon reads as
                    // glass"): these seeds never had it, which went unnoticed while the flat bounce
                    // held the whole outdoor field near 1.28 — every cell glowed a little, so lamp
                    // cells did not stand out. With the bounce weighted (open ground now sits at
                    // exactly scene.Sky), a daylight lantern's >1.0 seed became the only thing feeding
                    // the shader's glow term, and it read as a bright pool at two in the afternoon.
                    // Full strength returns by 08:00/17:00; night and indoors are untouched.
                    if (scene.Outdoors)
                        intensity *= 1f - 0.65f * (1f - MathHelper.Clamp(Math.Abs(GameClock.MinutesNow() - 750f) / 270f, 0f, 1f));
                    // TWO-TONE rooms: an indoor window is DAYLIGHT (cool, slightly blue) while
                    // lamps and fires stay warm — the warm-vs-cool split across a room is what
                    // makes it read as cinematic instead of uniformly orange. Outdoor window
                    // lights (town houses at night) are lamp-lit from inside, so they stay warm.
                    bool coolDaylight = !scene.Outdoors && lightSource.lightContext.Value == LightSource.LightContext.WindowLight;
                    // A LIGHT'S BOUNCE IS THE LIGHT'S OWN COLOUR. This was one fixed warm constant
                    // for every source in the game, which is where the saloon's orange came from and
                    // had been coming from for a long time: all 66 of that room's map lights are
                    // white, and every one of them was being bounced back off the walls as
                    // (1.00, 0.83, 0.58). The note in the interior colour curve saying the lamps were
                    // measured white and therefore innocent was right about the lamps and wrong about
                    // us. Stardew stores a light's colour inverted, the same way the direct pass
                    // already reads it, and the seed is normalised so only the HUE comes from here
                    // and the strength keeps coming from the radius rule above.
                    Color raw = lightSource.color.Value;
                    Vector3 emitted = new(1f - raw.R / 255f, 1f - raw.G / 255f, 1f - raw.B / 255f);
                    float emittedPeak = Math.Max(emitted.X, Math.Max(emitted.Y, emitted.Z));
                    Vector3 seedColor = emittedPeak > 0.02f
                        ? emitted / emittedPeak
                        : new Vector3(1.00f, 0.83f, 0.58f);   // a light with no colour at all: warm, as before
                    if (coolDaylight)
                    {
                        // The scene.Sky is what is on the other side of this window, so it follows the
                        // clock and the calendar instead of being one fixed daylight colour (see
                        // WindowDaylight - that constant is why rooms stayed daylit at 2am).
                        ShadowRenderer.WindowDaylight(out Vector3 sunColour, out float sunStrength);
                        seedColor = sunColour;
                        // ×1.25 so the pool can actually show through the room's exposure (a bare
                        // sunStrength at 0.85 stays under the multiply-only floor and reads as if
                        // nothing happened when the toggle is flicked).
                        intensity *= (1.25f * sunStrength) * WindowRoomScale;
                    }
                    // One seed cell; the bilinear upsample + the 5×5 bounce spread it into a soft
                    // pool. (A wide radial seed disc was tried to force a bigger pool but never read
                    // as wider on the coarse grid — reverted to keep it simple.)
                    if (subTileSeeds)
                    {
                        // The light's TRUE position in cell coordinates (cell centres at +0.5),
                        // split over the four cells around it. A light standing at a tile's centre
                        // (a placed torch) lands whole in its own cell, exactly as the snap put it;
                        // a CARRIED light glides between cells as its owner walks.
                        float cellX = glowPosition.X / 64f - 0.5f - window.TileX;
                        float cellY = glowPosition.Y / 64f - 0.5f - window.TileY;
                        int leftCell = (int)Math.Floor(cellX);
                        int topCell = (int)Math.Floor(cellY);
                        float rightShare = cellX - leftCell;
                        float bottomShare = cellY - topCell;
                        for (int corner = 0; corner < 4; corner++)
                        {
                            int cornerColumn = leftCell + (corner & 1);
                            int cornerRow = topCell + (corner >> 1);
                            float share = ((corner & 1) == 0 ? 1f - rightShare : rightShare)
                                        * ((corner >> 1) == 0 ? 1f - bottomShare : bottomShare);
                            float cornerIntensity = intensity * share;
                            if (!ClampSeed(ref cornerColumn, ref cornerRow, ref cornerIntensity, window))
                                continue;
                            int cornerIndex = cornerRow * window.TilesWide + cornerColumn;
                            _lightCells[cornerIndex] = Vector3.Max(_lightCells[cornerIndex], seedColor * cornerIntensity);
                        }
                    }
                    else
                    {
                        int cellIndex = seedRow * window.TilesWide + seedColumn;
                        _lightCells[cellIndex] = Vector3.Max(_lightCells[cellIndex], seedColor * intensity);
                    }

                    // SUN SHAFT: daylight through a window falls onto the floor below it — seed a
                    // fading column of cool light under the window so (after bilinear + the blur
                    // bounce) a soft bright patch spills across the floorboards.
                    if (coolDaylight)
                    {
                        // Kept below the bloom threshold so the window doesn't bloom into a
                        // glaring white patch (it was ~1.15 = over-bright + bloomed).
                        // The patch LEANS with the sun and stretches when the sun is low, from
                        // the same angle the shadows use - so first thing in the morning it
                        // reaches right across the floorboards, and by noon it is a short pool
                        // directly under the window.
                        ShadowRenderer.WindowShaft(out float lean, out float reach);
                        // The patch takes its OWN switch and not the room's intensity, so the two
                        // halves can be turned off independently of each other.
                        Vector3 shaft = seedColor * (0.72f * WindowPatchScale);
                        int steps = Math.Max(1, (int)Math.Round(reach));
                        for (int step = 1; step <= steps; step++)
                        {
                            int columnRow = trueSeedRow + step;
                            int columnCell = trueSeedColumn + (int)Math.Round(lean * step);
                            if (columnRow >= window.TilesHigh)
                                break;
                            if (columnRow < 0 || columnCell < 0 || columnCell >= window.TilesWide)
                                continue;
                            float falloff = 1.0f - 0.85f * (step / (float)(steps + 1));
                            int columnCellIndex = columnRow * window.TilesWide + columnCell;
                            _lightCells[columnCellIndex] = Vector3.Max(_lightCells[columnCellIndex], shaft * falloff);
                        }
                    }
                    // OUTDOOR lit storefronts/windows at night pour WARM light DOWN onto the
                    // path in front (a saloon's windows lighting the ground). Short fading
                    // column, softened afterwards by the bilinear sample + the wide bounce.
                    else if (scene.Outdoors && lightSource.lightContext.Value == LightSource.LightContext.WindowLight)
                    {
                        var spill = new Vector3(1.00f, 0.84f, 0.60f);
                        for (int step = 1; step <= 4; step++)
                        {
                            int columnRow = trueSeedRow + step;
                            if (columnRow >= window.TilesHigh)
                                break;
                            if (columnRow < 0 || trueSeedColumn < 0 || trueSeedColumn >= window.TilesWide)
                                continue;
                            float falloff = (1.0f - 0.22f * step) * intensity * 2.2f;
                            int columnCellIndex = columnRow * window.TilesWide + trueSeedColumn;
                            _lightCells[columnCellIndex] = Vector3.Max(_lightCells[columnCellIndex], spill * falloff);
                        }
                    }
                }
            }

        }

        /// <summary>Seed window daylight from the room's glow sprites, for the interiors that
        /// publish their windows no other way.</summary>
        private void SeedWindowGlows(GameLocation location, in SceneSeed scene, in TileWindow window)
        {
            var lights = Game1.currentLightSources;
            // ---- Seed window daylight from the room's window glow sprites ----
            // Some interiors publish their windows only as lightGlows (no WindowLight source and
            // no DayTiles property - the vanilla farmhouse is one), so the loop above never
            // touches them. A glow sprite IS the game saying "this window is lit", so seed the
            // same cool daylight there; otherwise the flood leaves the floor beside a real
            // window at bare scene.Sky and it reads as a dark strip in front of the glass.
            int glowCount = location.lightGlows is { } glows ? glows.Count : -1;
            bool glowGate = !scene.Outdoors && !scene.ScriptedDark && WindowRoomScale > 0.01f && glowCount > 0;
            if (glowGate)
            {
                ShadowRenderer.WindowDaylight(out Vector3 sunColour, out float sunStrength);
                int seeded = 0;
                if (sunStrength > 0.03f)
                {
                    foreach (Vector2 glowPoint in location.lightGlows)
                    {
                        int trueSeedColumn = (int)(glowPoint.X / 64f) - window.TileX;
                        int trueSeedRow = (int)(glowPoint.Y / 64f) - window.TileY;
                        int seedColumn = trueSeedColumn, seedRow = trueSeedRow;
                        // Skip any spot a real window light source already covered above.
                        bool covered = false;
                        if (lights != null)
                            foreach (var lightSource in lights.Values)
                                if (lightSource.lightContext.Value == LightSource.LightContext.WindowLight
                                    && Math.Abs((int)(lightSource.position.Value.X / 64f) - (window.TileX + trueSeedColumn)) <= 1
                                    && Math.Abs((int)(lightSource.position.Value.Y / 64f) - (window.TileY + trueSeedRow)) <= 1)
                                { covered = true; break; }
                        if (covered)
                            continue;
                        float glowIntensity = 1.35f * sunStrength * WindowRoomScale;
                        if (!ClampSeed(ref seedColumn, ref seedRow, ref glowIntensity, window))
                            continue;
                        int columnCellIndex = seedRow * window.TilesWide + seedColumn;
                        _lightCells[columnCellIndex] = Vector3.Max(_lightCells[columnCellIndex], sunColour * glowIntensity);
                        // Spread the daylight down the first cells INTO the room, so the patch
                        // reads as light pooling in front of the glass rather than a single-cell
                        // glint that a toggle is easy to miss. Kept at/over 1.0 so it can actually
                        // ADD light - below 1.0 the flood can only darken less, which is why the
                        // old seed read as nothing next to the room's exposure.
                        for (int step = 1; step <= 3; step++)
                        {
                            int columnRow = trueSeedRow + step;
                            if (columnRow >= window.TilesHigh)
                                break;
                            if (columnRow < 0 || trueSeedColumn < 0 || trueSeedColumn >= window.TilesWide)
                                continue;
                            _lightCells[columnRow * window.TilesWide + trueSeedColumn] = Vector3.Max(_lightCells[columnRow * window.TilesWide + trueSeedColumn],
                                sunColour * glowIntensity * (1f - 0.25f * step));
                        }
                        seeded++;
                    }
                }
                LastWindowSeed = $"seed: {seeded} cell / scale={WindowRoomScale:F2} sun={sunStrength:F2} glows={glowCount}";
            }
            else
            {
                LastWindowSeed = $"seed: GATE (out={scene.Outdoors} scripted={scene.ScriptedDark} scale={WindowRoomScale:F2} glows={glowCount})";
            }

        }

        /// <summary>Two rounds of four directional sweeps: the propagation itself.</summary>
        private void FloodSweeps(in TileWindow window)
        {
            // ---- Flood: two rounds of 4 directional sweeps (Terraria-style) ----
            for (int round = 0; round < 2; round++)
            {
                for (int j = 0; j < window.TilesHigh; j++)          // left → right, then right → left
                {
                    Vector3 carry = Vector3.Zero;
                    for (int i = 0; i < window.TilesWide; i++) Propagate(ref carry, j * window.TilesWide + i);
                    carry = Vector3.Zero;
                    for (int i = window.TilesWide - 1; i >= 0; i--) Propagate(ref carry, j * window.TilesWide + i);
                }
                for (int i = 0; i < window.TilesWide; i++)          // top → bottom, then bottom → top
                {
                    Vector3 carry = Vector3.Zero;
                    for (int j = 0; j < window.TilesHigh; j++) Propagate(ref carry, j * window.TilesWide + i);
                    carry = Vector3.Zero;
                    for (int j = window.TilesHigh - 1; j >= 0; j--) Propagate(ref carry, j * window.TilesWide + i);
                }
            }

        }

        /// <summary>The fake indirect bounce, as a separated 5x5 box blur.</summary>
        private void BounceBlur(in TileWindow window)
        {
            // ---- Fake one indirect bounce: 5×5 blur folded back in softly ----
            // 5×5 (was 3×3): a wider bounce spreads each light into a softer, fluffier pool that
            // fades out gradually instead of ending within one tile.
            //
            // SEPARATED into a row pass and a column pass: 25 reads per cell became 10. A box blur
            // separates exactly, and so does THIS one despite clamping at the edges, because the
            // clamped window stays a rectangle - the divisor is validColumns × validRows, a product
            // of one term per axis, so dividing by each axis in its own pass gives the same number.
            // Worth doing where it is: the grid is the viewport in tiles, so it grows quadratically
            // as the player zooms out, which is exactly the case the reports are about.
            for (int j = 0; j < window.TilesHigh; j++)
            {
                int row = j * window.TilesWide;
                for (int i = 0; i < window.TilesWide; i++)
                {
                    int firstColumn = Math.Max(0, i - 2), lastColumn = Math.Min(window.TilesWide - 1, i + 2);
                    var sum = Vector3.Zero;
                    for (int tapColumn = firstColumn; tapColumn <= lastColumn; tapColumn++)
                        sum += _lightCells[row + tapColumn];
                    _blurRowScratch[row + i] = sum / (lastColumn - firstColumn + 1);
                }
            }
            for (int j = 0; j < window.TilesHigh; j++)
            {
                int firstRow = Math.Max(0, j - 2), lastRow = Math.Min(window.TilesHigh - 1, j + 2);
                int row = j * window.TilesWide;
                for (int i = 0; i < window.TilesWide; i++)
                {
                    var sum = Vector3.Zero;
                    for (int tapRow = firstRow; tapRow <= lastRow; tapRow++)
                        sum += _blurRowScratch[tapRow * window.TilesWide + i];
                    _blurredLightCells[row + i] = sum / (lastRow - firstRow + 1);
                }
            }
        }

        /// <summary>Fold the bounce back in, lift elevated surfaces, and pack to bytes.</summary>
        private void ComposeLightmapPixels(in SceneSeed scene, in TileWindow window)
        {
            // Walls/roofs are ELEVATED surfaces in a top-down view: the dark cell value models
            // light blocked at ground level, but the pixels DRAWN there are facades and rooftops
            // in full daylight — lift them to ambient so buildings never render dimmer than the
            // ground they stand on (dark cells still attenuate propagation for the spill/shade).
            Vector3 lift = scene.Sky * (scene.Outdoors ? 0.92f : 0.85f);
            for (int cellIndex = 0; cellIndex < window.Count; cellIndex++)
            {
                // The bounce FILLS SHADE. It used to be a flat add, which put every open outdoor
                // cell at ~1.28 in broad daylight — and floodlight.effect reads anything over 1.0 as a
                // lamp core and adds a glow for it, so open ground got a few percent of extra light
                // it was never meant to have. On a winter beach, where snow is already close to
                // white and most of the screen is open, that pushed the whole field past clipping
                // and the detail in the snow disappeared. Weighting the bounce by how far the cell
                // is BELOW full light leaves open ground at exactly scene.Sky, still lifts real shade,
                // and leaves lamp cells (seeded above 1.0) free to glow as intended.
                Vector3 cell = _lightCells[cellIndex];
                Vector3 headroom = new(
                    MathHelper.Clamp(1f - cell.X, 0f, 1f),
                    MathHelper.Clamp(1f - cell.Y, 0f, 1f),
                    MathHelper.Clamp(1f - cell.Z, 0f, 1f));
                Vector3 composed = cell + _blurredLightCells[cellIndex] * 0.28f * headroom;
                if (_lightDecay[cellIndex] == SolidDecay)
                    composed = Vector3.Max(composed, lift);
                _lightmapPixels[cellIndex] = new Color(
                    (byte)MathHelper.Clamp(composed.X * 255f * StorageScale, 0f, 255f),
                    (byte)MathHelper.Clamp(composed.Y * 255f * StorageScale, 0f, 255f),
                    (byte)MathHelper.Clamp(composed.Z * 255f * StorageScale, 0f, 255f), (byte)255);
            }

        }

        private void Propagate(ref Vector3 carry, int cellIndex)
        {
            float decay = _lightDecay[cellIndex];
            carry *= decay;
            Vector3 cell = _lightCells[cellIndex];
            carry = Vector3.Max(carry, cell);
            _lightCells[cellIndex] = carry;
        }

        /// <summary>Direct-sky seed for open cells — RELATIVE only (the game's own day/night
        /// darkening stays in charge of the global level, so no double-darkening at night):
        /// ~1.0 with a warm golden-hour tint outdoors; interiors use the indoor-darkness
        /// slider (vanilla leaves rooms flat-bright — that darkening is the feature).</summary>
        /// <summary>How far into night the clock is, 0 at an hour before truly-dark, 1 from
        /// truly-dark on. The one ramp every outdoor night term shares, so they arrive together.</summary>
        internal static float NightAmount()
        {
            int timeOfDay = Game1.timeOfDay;
            int trulyDark;
            try { trulyDark = Game1.currentLocation != null ? Game1.getTrulyDarkTime(Game1.currentLocation) : 2000; }
            catch { trulyDark = 2000; }
            int nowMinutes = (timeOfDay / 100) * 60 + timeOfDay % 100;
            int trulyDarkMinutes = (trulyDark / 100) * 60 + trulyDark % 100;
            return MathHelper.Clamp((nowMinutes - (trulyDarkMinutes - 60)) / 60f, 0f, 1f);
        }

        private static Vector3 SkyColour(bool outdoors, ModConfig config)
        {
            if (!outdoors)
            {
                // Interiors have no sky: a flat ambient set by the indoor-darkness slider,
                // with window/lamp seeds carving out the bright areas.
                float ambient = MathHelper.Clamp(1f - config.LightingIndoorDarkness * 0.55f, 0.3f, 1f);
                // A room with windows is lit BY those windows, so its ambient follows the same
                // daylight they do - dim and warm at dawn, full at noon, dim again at dusk.
                // Flat ambient was why every interior read as noon at six in the morning.
                // Night needs no help here: when the game darkens a room itself the caller
                // hands out plain white instead of this (see the vanillaDark branch), so this
                // curve only ever shapes the hours the game leaves flat-bright.
                ShadowRenderer.WindowDaylight(out Vector3 dayColour, out float _);
                // The room FILLS IN later than the window starts pouring light through it, and
                // that gap is the whole effect: at six the sun is low enough to lay a bright
                // patch on the floor while the rest of the room is still last night's dark.
                // Tying the ambient to the window's own strength collapsed that gap - both hit
                // full together and the room read as noon at sunrise. Fill runs 06:20 to 09:00
                // and unwinds over the hour and a half before dark.
                float nowMinutes = GameClock.MinutesNow();
                float darkMinutes = ShadowRenderer.TrulyDarkMinutes();
                float fill = Math.Min(MathHelper.Clamp((nowMinutes - 380f) / 160f, 0f, 1f),
                                      MathHelper.Clamp((darkMinutes - nowMinutes) / 90f, 0f, 1f));
                // The wake floor follows the morning-darkness slider: at its default (0.25) the
                // room wakes at the historical ~0.42; 0 lifts it to a fully bright wake.
                float wakeFloor = MathHelper.Lerp(1f, 0.42f, MathHelper.Clamp(config.LightingMorningDarkness / 0.25f, 0f, 1f));
                ambient = MathHelper.Clamp(ambient * MathHelper.Lerp(wakeFloor, 1f, fill), 0.16f, 1f);
                // ...and takes its colour, so the air in the room agrees with the light coming
                // through the glass rather than staying neutral grey while the patch goes gold.
                return new Vector3(ambient) * Vector3.Lerp(Vector3.One, dayColour, 0.5f);
            }
            float dayProgress = MathHelper.Clamp((GameClock.MinutesNow() - 720f) / 360f, -1f, 1f);
            float warm = MathHelper.Clamp((Math.Abs(dayProgress) - 0.55f) / 0.45f, 0f, 1f);
            Vector3 sky = Vector3.Lerp(new Vector3(1f, 1f, 1f), new Vector3(1.03f, 0.96f, 0.88f), warm);
            if (Game1.isRaining)
                sky *= 0.93f;   // gentle overcast dimming; vanilla already grays rain out

            // MOONLIGHT: after dark, open ground gets a cool lift scaled by the lunar phase
            // (SDV's 28-day month = one synthetic cycle) and season — cells under canopies
            // and buildings receive none, so a full moon paints real moon shade.
            float nightAmount = NightAmount();
            // HOW DARK, AND WHAT COLOUR OF DARK. Two decisions, and both used to be hardcoded.
            //
            // The depth was a bare 0.62 that no setting reached, while the night-darkness
            // slider's own help text promised "how dark the world gets outdoors after
            // nightfall" - a promise the code never kept, because the slider only ever ran
            // indoors. It drives this now, mapped so the default lands exactly on the old
            // 0.62: nobody's night changes until they move the thing that now works.
            //
            // The colour was neutral grey, and neutral dim is why the old night read as muddy
            // rather than as night. Eyes at low light lose red first (the Purkinje shift), and
            // every film and game night trades on it: the unlit world leans blue-cool, the
            // flames stay warm, and that warm-against-cool is the whole picture. Same
            // luminance as before - the tint is normalised - so nothing gets darker by gaining
            // a colour, and the moon lift below still rides on top.
            float nightFloor = MathHelper.Clamp(1f - config.LightingNightDarkness * 0.68f, 0.2f, 1f);
            // The cool cast follows the slider down: at the default it is the full moonlit blue,
            // and at zero it is gone entirely, so someone who slides the night away gets a night
            // that is simply a brighter vanilla rather than this mod's colour opinion at a lower
            // volume. Asked for in exactly those words: low should look like vanilla, only lit.
            Vector3 moonCool = new(0.910f, 1.003f, 1.220f);
            float coolShare = MathHelper.Clamp(config.LightingNightDarkness / 0.56f, 0f, 1f);
            sky *= Vector3.Lerp(Vector3.One, Vector3.Lerp(Vector3.One, moonCool, coolShare) * nightFloor, nightAmount);
            // Full moon lifts the night back up (cool) → a full-moon night is clearly brighter
            // and bluer than a new-moon one.
            if (nightAmount > 0f)
                sky += new Vector3(0.05f, 0.07f, 0.11f) * (ShadowRenderer.MoonStrength() * nightAmount);
            return sky;
        }

        // ---- Windowed-interior detection + time-of-day room exposure ----------------
        // The exposure multiplier only ever applies to rooms that are LIT BY DAYLIGHT,
        // and "has windows" is the test for that. Mines/volcano are excluded outright
        // (scripted darkness, add-only rule), and windowless interiors — the farm cave,
        // the sewer, mod caves — have no DayTiles/NightTiles map entries and no
        // WindowLight sources, so they are never touched.
        private static GameLocation? _windowedCacheLocation;
        private static bool _windowedCached;

        /// <summary>How much of the sun PATCH under a window to seed, 0 to 1 - the visible half,
        /// which a window-art mod draws too. Owned by the flood stage, which eases it from the
        /// setting; this class only multiplies by it.</summary>
        internal static float WindowPatchScale = 1f;
        /// <summary>Live reason the window-glow seed did or did not run this rebuild (for the
        /// radiance_debug flood caption - answers \"is the seed even attempted\" without a log).</summary>
        internal static string LastWindowSeed = "?";
        /// <summary>How much daylight a window contributes to the ROOM's light, 0 to 1. Separate
        /// from the patch because the two are worth different things when another mod is drawing
        /// windows: it can paint a beam, but it cannot make the room's lighting know about it.</summary>
        internal static float WindowRoomScale = 1f;

        internal static bool IsWindowedInterior(GameLocation? location)
        {
            if (location == null || location.IsOutdoors)
                return false;
            if (location is StardewValley.Locations.MineShaft || location is StardewValley.Locations.VolcanoDungeon)
                return false;
            // DayTiles/NightTiles is a MAP property - time independent, safe to cache per visit.
            if (!ReferenceEquals(location, _windowedCacheLocation))
            {
                _windowedCacheLocation = location;
                // Windows are the map's day/night switching tiles — the standard mechanism
                // vanilla AND content-pack interiors use to make panes glow by day and go
                // dark at night — so the property's presence is a time-independent answer
                // (the light sources themselves vanish after dark).
                var mapProperties = location.Map?.Properties;
                _windowedCached = mapProperties != null
                    && (mapProperties.ContainsKey("DayTiles") || mapProperties.ContainsKey("NightTiles"));
            }
            if (_windowedCached)
                return true;
            // The light-source and glow signals change through the day AND with when the room
            // was entered, so they are checked LIVE, never cached: entering a farmhouse at
            // night must not freeze the answer as "no windows" for the whole visit (that
            // freeze is what left the floor beside a real window at bare sky in the morning).
            if (Game1.currentLightSources != null)
                foreach (var lightEntry in Game1.currentLightSources)
                    if (lightEntry.Value.lightContext.Value == LightSource.LightContext.WindowLight)
                        return true;
            return location.lightGlows is { Count: > 0 };
        }

        /// <summary>
        /// The room's light level for the current time of day, as a colour multiplier for
        /// the whole interior. (1,1,1) anywhere this doesn't apply. Vanilla snaps every
        /// interior to flat-bright at 6:00 and holds it there all day; a real room lit by
        /// its windows takes until mid-morning to fill, starts sinking before dusk and is
        /// genuinely dark at night — that difference is this curve. Applied in
        /// floodlight.fx as its own term so the GI-strength slider cannot swallow it.
        /// </summary>
        internal static void IndoorLook(GameLocation? location, ModConfig config,
            out Vector3 exposure, out float saturation)
        {
            exposure = Vector3.One;
            saturation = 1f;
            if (!IsWindowedInterior(location))
                return;

            float nowMinutes = GameClock.MinutesNow();
            float darkMinutes = ShadowRenderer.TrulyDarkMinutes();
            // Full daylight from ~8:50; dimming begins 90 min before truly-dark and
            // bottoms out an hour after. (The SDV clock starts at 6:00, so there is no
            // "before dawn" side — mornings always enter through the 6:00 ramp.)
            float dayFill = Math.Min(
                MathHelper.Clamp((nowMinutes - 360f) / 170f, 0f, 1f),
                MathHelper.Clamp((darkMinutes + 60f - nowMinutes) / 150f, 0f, 1f));
            // How much of the dim is NIGHT (cool, deep) vs twilight (warm, gentler).
            float nightness = MathHelper.Clamp((nowMinutes - (darkMinutes - 20f)) / 80f, 0f, 1f);

            float floorMorning = MathHelper.Clamp(1f - config.LightingIndoorDarkness * 0.78f, 0.2f, 1f);
            float floorNight = MathHelper.Clamp(1f - config.LightingNightDarkness * 0.8f, 0.16f, 1f);
            float level = MathHelper.Lerp(MathHelper.Lerp(floorMorning, floorNight, nightness), 1f, dayFill);
            if (Game1.isRaining || Game1.isLightning || Game1.isSnowing)
                level *= MathHelper.Lerp(0.88f, 1f, 1f - dayFill);   // overcast steals midday, night is dark already

            // COLOUR WALKS THE DAY, and it is not the same walk the brightness takes.
            // Before the sun is properly up a room is lit by open sky rather than by the sun
            // itself, and open sky is blue - so early morning is cool, the middle of the day
            // is neutral, the hours before dark run warm, and night settles back to blue.
            // Each phase is its own slow ramp handing over to the next, so the room is never
            // seen changing colour; it has simply changed by the time you look again.
            //
            // The colour is a MULTIPLIER that dims the warm channels hard and leaves blue
            // almost alone - which is what the eye reads as "lit by open sky". Two earlier
            // shapes both failed, and for opposite reasons worth keeping written down:
            //
            //   A gentle multiply (0.78, 0.90, 1.20) only made the room darker. Orange pine
            //   is about (190,140,90); it has barely any blue for a blue factor to lift, so
            //   red stayed on top and the wood just dimmed.
            //
            //   Mixing every pixel toward one blue-grey did move the hue, but it collapsed
            //   the whole picture onto a single chroma - walls, floor and furniture all
            //   arriving at the same colour - which reads as grey and washed out.
            //
            // Cutting red to a third while blue keeps nearly all of its strength moves each
            // pixel's own balance to blue AND keeps them different from each other, so the
            // room goes cold without going flat.
            Vector3 coolSky = new(0.40f, 0.55f, 1.00f);
            // Softened and started later in 1.5.5. At (1.00, 0.80, 0.55) on a ramp opening 200
            // minutes before dark, a saloon at six in the evening was already half way into the
            // cast, and a room whose art is warm wood to begin with came out uniformly orange:
            // measured median saturation 0.87 against 0.73 at noon in the same room. The lamps
            // were not the cause and never had been - all 66 of the saloon's map lights are
            // white - it was this. The hour before dark still runs warm, which is the point;
            // it no longer starts in the middle of the afternoon.
            Vector3 warmDusk = new(1.00f, 0.90f, 0.76f);
            Vector3 nightSky = new(0.36f, 0.52f, 1.00f);
            float morning = 1f - MathHelper.Clamp((nowMinutes - 360f) / 200f, 0f, 1f);   // 06:00 -> 09:20
            // AND A CLEAR MORNING IS NOT LIT BY OPEN SKY. The argument for the cool cast above is
            // that a room early on is lit by the sky rather than by the sun, and the sky is blue.
            // That is true of an overcast morning and false of a clear one: the sun is up by 6:00
            // in this game, it is low, and low sun is the warmest light of the day. The reporter
            // who raised it said so without meaning to, describing a room that reads cold and blue
            // on waking "except on rainy days" - the one morning of the two where the cast is
            // right. So the cool walk is now the weather's, and a clear morning keeps only a share
            // of it rather than the whole thing.
            //
            // Read from the WEATHER and not from the room, because a room is never raining.
            // ShadowRenderer.OvercastNow answers for the current location, which is right for a
            // shadow outdoors and always zero in here: the first version asked it and made every
            // morning a clear one, rain included. Caught by the rainy picture moving exactly as far
            // as the clear one, which it must not do. These are the same three flags the level line
            // above already reads, so an interior gets one answer about the sky.
            //
            // Only the MORNING. The same reasoning does apply to the warm hour before dark, and it
            // is deliberately not applied there: nobody has reported it, that ramp was already
            // pulled back once in 1.5.5 for being too strong, and two changes to one curve in one
            // release cannot be told apart afterwards by the people who see them.
            //
            // How much a clear morning keeps is the player's, because this moves a look that every
            // release so far has painted the same way. At 1 they get that look back exactly; rain
            // is on the far end of the same lerp and never moves with the dial at all.
            float overcast = Game1.isRaining || Game1.isLightning ? 1f
                : Game1.isSnowing ? SnowOvercastShare : 0f;
            morning *= MathHelper.Lerp(
                MathHelper.Clamp(config.LightingMorningClearSkyCool, 0f, 1f), 1f, overcast);
            float evening = MathHelper.Clamp((nowMinutes - (darkMinutes - 110f)) / 110f, 0f, 1f);
            Vector3 chroma = Vector3.Lerp(Vector3.One, coolSky, morning);
            chroma = Vector3.Lerp(chroma, warmDusk, evening);
            chroma = Vector3.Lerp(chroma, nightSky, nightness);

            // AND A CAST MAY NOT OUTRUN THE ONE THE OUTDOOR NIGHT IS ALLOWED.
            //
            // This chroma is a MULTIPLIER on the finished picture, so the gap between its
            // channels is how hard it pushes a warm surface toward blue - and the numbers above
            // were written as a mood rather than measured against anything. nightSky spans 0.36
            // to 1.00: a blue-to-red ratio of 2.78, which takes 45% of the red out of every warm
            // thing in the room. Brick, pine, and the fire itself, which is the one object in the
            // room that IS the light.
            //
            // The outdoor night is the comparison that settles it, because nobody has ever
            // reported that one: its cast is (0.910, 1.003, 1.220), a ratio of 1.34, and even
            // that reaches a pixel through the GI slider (0.30 by default) because outdoors the
            // tint lives in the light FIELD. This one bypasses the slider on purpose - the room
            // level must not be something the GI slider can swallow - so it arrives at three
            // times the spread and three times the authority, call it nine times the cast.
            // Measured side by side against the same farmhouse with the mod off: a hearth that
            // vanilla draws as orange brick with a glow on the boards came out a black block in
            // a violet room. That is the whole "the fire indoors is black" report.
            //
            // Pulled back toward neutral until the ratio is one an outdoor night would be
            // allowed. The room still reads as cold - the level below is untouched and the level
            // is what "dark" means - it just stops repainting everything in it.
            const float MaxCastRatio = 1.7f;
            float castMinimum = Math.Min(chroma.X, Math.Min(chroma.Y, chroma.Z));
            float castMaximum = Math.Max(chroma.X, Math.Max(chroma.Y, chroma.Z));
            if (castMinimum > 0.0001f && castMaximum > castMinimum * MaxCastRatio)
            {
                // Solve lerp(1, chroma, t) for the t whose ends land exactly on the ratio, so a
                // cast already inside it is untouched and one outside is walked in, not clamped.
                float denominator = (castMaximum - 1f) - MaxCastRatio * (castMinimum - 1f);
                if (Math.Abs(denominator) > 0.0001f)
                    chroma = Vector3.Lerp(Vector3.One, chroma,
                        MathHelper.Clamp((MaxCastRatio - 1f) / denominator, 0f, 1f));
            }

            // AND THE PLAYER GETS TO SAY HOW FAR IT WALKS. Everything above is a mood decided
            // here, with no dial anywhere on any page, and a player who woke up in a room that
            // read to them as cold and blue had nothing to turn down. What they reached for was
            // the GI strength, which does move this, and which also lights the whole outdoors:
            // the room came right and the fields blew out. Applied last, so the ratio guard above
            // keeps its meaning and this only scales what survived it. At 0 the room is the colour
            // the game drew, still dimmed by the hour, because the dim is the darkness sliders'
            // job and not this one's.
            chroma = Vector3.Lerp(Vector3.One, chroma,
                MathHelper.Clamp(config.LightingIndoorColourWalk, 0f, 1f));

            // Brightness and colour must not fight: a strong cast is dark all by itself, so
            // the chroma is rescaled to carry exactly the luminance the curve above asked
            // for. The darkness sliders stay the only thing that decides how dark a room is,
            // whatever colour the hour happens to be.
            float chromaLuminance = 0.299f * chroma.X + 0.587f * chroma.Y + 0.114f * chroma.Z;
            exposure = chroma * (level / Math.Max(chromaLuminance, 0.0001f));

            // Dimming an image flattens its colour as a side effect. A small lift on the way
            // out keeps the deep blues and the wood browns reading as themselves rather than
            // as two shades of grey - the difference between a cold room and a washed-out one.
            float cast = Math.Max(morning, Math.Max(evening, nightness));
            saturation = MathHelper.Lerp(1f, 1.22f, cast);
        }

        internal void Dispose()
        {
            _lightmapTexture?.Dispose();
            _lightmapTexture = null;
            _lightmapTextureSpare?.Dispose();
            _lightmapTextureSpare = null;
        }
    }
}
