using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// How wet the world is, kept on the game's own clock.
    ///
    /// <para>
    /// The game remembers exactly one thing about rain: whether it rained yesterday. There is no
    /// "it stopped forty minutes ago", so the drying ground has to carry its own clock. One
    /// scalar per location context (the Desert's context never rains, so its entry never rises -
    /// no special case), advanced on GAME minutes rather than frames so a menu or a pause does
    /// not dry the ground, rising over ~30 game-minutes of rain and drying over ~120 after.
    /// </para>
    ///
    /// <para>
    /// Snow deliberately does not wet: frozen ground does not glisten, and winter's look already
    /// has its own machinery. Green rain wets automatically, because the game keeps IsRaining
    /// true underneath it.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Full soak after ~30 game-minutes of rain (visible within the first minute).</summary>
        private const float WetRiseGameMinutes = 30f;
        /// <summary>Dry over ~120 game-minutes - noticed as a state, gone before it reads as a bug.</summary>
        private const float WetDecayGameMinutes = 120f;
        /// <summary>The morning after a rainy day starts half-wet (the game's own flag survives
        /// the save, so this needs no storage of ours).</summary>
        private const float MorningAfterWetness = 0.5f;

        /// <summary>Wetness per location context id. Shared world truth, deliberately NOT
        /// per-screen: both halves of a split screen stand in the same world.</summary>
        private static readonly Dictionary<string, float> _wetnessByContext = new();
        private static float _wetnessLastMinutes = -1f;
        private static int _wetnessSteppedTick = -1;
        private static int _wetnessSeededDay = -1;

        /// <summary>This screen's presence fade for the wet pass (saved in ScreenState). The
        /// wetness scalar is the slow world truth; this is the ~0.5 s screen-side ease that
        /// covers config toggles and warping indoors, so nothing pops.</summary>
        // moved to ScreenState (see RenderPipeline.Screens.cs)

        /// <summary>The wetness of the context the player is standing in, 0..1.</summary>
        internal static float WetnessNow
        {
            get
            {
                string? contextId = Game1.currentLocation?.GetLocationContextId();
                return contextId != null && _wetnessByContext.TryGetValue(contextId, out float wetness)
                    ? wetness : 0f;
            }
        }

        /// <summary>Puddles dry faster than dampness: pools vanish over the top of the decay
        /// while the general dark-damp lingers to the end.</summary>
        internal static float PuddleAmountNow => Math.Clamp(WetnessNow * 1.33f - 0.33f, 0f, 1f);

        /// <summary>
        /// Advance the current context's wetness. Once per tick, on game minutes.
        ///
        /// <para>Only the context being played advances; one that was left hours ago catches up
        /// on re-entry using its weather NOW as the stand-in for the gap, which is right whenever
        /// weather is per-day (always, outside debug commands) and merely approximate for the
        /// frame a debug flip happens off-context.</para>
        /// </summary>
        private static void AdvanceWetness(ModConfig config)
        {
            if (Game1.ticks == _wetnessSteppedTick || Determinism.Frozen)
                return;
            _wetnessSteppedTick = Game1.ticks;

            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return;
            string contextId = location.GetLocationContextId();
            _wetnessByContext.TryGetValue(contextId, out float wetness);

            // The morning after a rainy day starts half-wet. Seeded once per day, before the
            // delta step, so day one of sunshine still begins with drying ground.
            if (_wetnessSeededDay != Game1.Date.TotalDays)
            {
                _wetnessSeededDay = Game1.Date.TotalDays;
                _wetnessLastMinutes = -1f;
                if (Game1.wasRainingYesterday)
                    wetness = Math.Max(wetness, MorningAfterWetness);
            }

            float minutes = GameClock.MinutesNow();
            float elapsed = _wetnessLastMinutes < 0f ? 0f : minutes - _wetnessLastMinutes;
            _wetnessLastMinutes = minutes;
            if (elapsed < 0f)
                elapsed = 0f;   // clock wound backwards (debug time) - hold rather than guess
            elapsed = Math.Min(elapsed, 60f);

            bool rainingHere = location.IsRainingHere();
            wetness = rainingHere
                ? Math.Min(1f, wetness + elapsed / WetRiseGameMinutes)
                : Math.Max(0f, wetness - elapsed / WetDecayGameMinutes);
            _wetnessByContext[contextId] = wetness;
        }

        // ---- the wet pass -----------------------------------------------------------------------

        /// <summary>Set at GameLaunched. Dynamic Reflections owns puddles when it is installed;
        /// our dampness and streaks have no counterpart there and stay on.</summary>
        internal static bool DynamicReflectionsPresent;

        private Effect? _wetEffect;
        private Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>? _wetStageDelegate;
        /// <summary>True while the wet pass wants the flipped-entity mirror baked even where no
        /// water is on screen: puddles are on, the ground is wet enough to hold one, and Dynamic
        /// Reflections is not already doing this job. Read by the entity-mirror culls, which were
        /// written when "near water" was the only reason to mirror anybody.</summary>
        private bool _wetPuddleMirrorWanted;
        internal bool WetWorldWantsEntityMirror => _wetPuddleMirrorWanted;

        private Texture2D? _wetSuitabilityTexture;
        private GameLocation? _wetSuitabilityLocation;
        private Vector2 _wetSuitabilityMapTiles = Vector2.One;
        /// <summary>What the suitability was built against: placing or picking up an object
        /// changes which tiles may pool, so a changed count rebuilds the texture.</summary>
        private int _wetSuitabilityObjectCount = -1, _wetSuitabilityFurnitureCount = -1;

        /// <summary>
        /// Draw the wet-ground pass: source in, dampened scene out. Sits after water (puddles on
        /// the frame the ripple just left) and before cloud shadows (overcast shade lands on
        /// already-wet ground), so bloom and the grade both see the wet look.
        /// </summary>
        private void RenderWetWorld(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            long started = FrameCost.Begin(FrameCost.Part.WetWorld);
            var effect = _wetEffect!;
            EnsureWetSuitability();
            GetParam(effect, "SuitabilityTexture")?.SetValue(_wetSuitabilityTexture);
            GetParam(effect, "MapSizeTiles")?.SetValue(_wetSuitabilityMapTiles);
            // Fresh viewport mapping of its own: the water mask's copy is only refreshed while
            // the water machinery runs, and this pass must work on a lake-less farm in the rain.
            GetParam(effect, "TilesPerScreen")?.SetValue(
                new Vector2(Game1.viewport.Width / 64f, Game1.viewport.Height / 64f));
            GetParam(effect, "WorldTileOffset")?.SetValue(
                new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            bool sdfUsable = _waterSignedDistanceTexture is { IsDisposed: false };
            GetParam(effect, "SdfValid")?.SetValue(sdfUsable ? 1f : 0f);
            if (sdfUsable)
            {
                GetParam(effect, "SdfTexture")?.SetValue(_waterSignedDistanceTexture);
                GetParam(effect, "MaskOrigin")?.SetValue(new Vector2(_lastWaterTileX, _lastWaterTileY));
                GetParam(effect, "MaskSize")?.SetValue(_waterMaskPixelSize);
            }
            GetParam(effect, "Wetness")?.SetValue(WetnessNow);
            GetParam(effect, "Strength")?.SetValue(config.WetWorldStrength);
            // Dynamic Reflections draws puddle sprites of its own on the same tile rule; two
            // sets of pools on one plaza reads as a bug in whichever mod the player blames.
            GetParam(effect, "PuddleCoverage")?.SetValue(DynamicReflectionsPresent ? 0f : config.WetWorldPuddles);
            GetParam(effect, "PuddleWet")?.SetValue(PuddleAmountNow);
            var (sunWarm, nightGlow) = TimeOfDayAmounts();
            Vector3 skyTint = SynthesisedSkyColour(sunWarm, nightGlow)
                * Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting);
            GetParam(effect, "SkyTint")?.SetValue(skyTint);
            // The flipped-entity mirror the water reflection bakes; the culls answer yes
            // everywhere while puddles are live (see WaterWithinTiles), so on a lake-less farm
            // the pools still hold people.
            // The night streaks reuse the water's own glimmer packing: the same eight on-screen
            // lights, the same dusk gate, so a lamp glimmers on the river and smears on the
            // street with one list.
            GetParam(effect, "NightGlow")?.SetValue(nightGlow);
            GetParam(effect, "Aspect")?.SetValue(dest.Height > 0 ? dest.Width / (float)dest.Height : 1f);
            SetGlimmerLights(effect, nightGlow);
            bool mirrorUsable = ReflectRTReady && _reflectionRenderTarget is { IsDisposed: false };
            GetParam(effect, "ReflectOn")?.SetValue(mirrorUsable ? 1f : 0f);
            if (mirrorUsable)
            {
                GetParam(effect, "ReflectTexture")?.SetValue(_reflectionRenderTarget);
                GetParam(effect, "MirrorTint")?.SetValue(new Vector3(0.82f, 0.88f, 0.97f));
            }
            effect.CurrentTechnique = effect.Techniques["WetWorld"];
            DrawFull(spriteBatch, source, dest, effect);
            BlendBackSource(spriteBatch, source, dest, _fadeWet);
            FrameCost.End(FrameCost.Part.WetWorld, started);
        }

        /// <summary>
        /// One texel per tile for the whole map: 0 = never wet, 128 = damp only, 255 = can pool.
        /// Built once per location from the SurfaceMap's classes plus the map's own vocabulary
        /// (Type Dirt/Stone, Diggable) - the same rule Dynamic Reflections proved across vanilla
        /// and SVE. Grass darkens but never pools; decks, water and roofs never even darken.
        /// </summary>
        private void EnsureWetSuitability()
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return;
            if (ReferenceEquals(location, _wetSuitabilityLocation)
                && _wetSuitabilityTexture is { IsDisposed: false }
                && location.objects.Length == _wetSuitabilityObjectCount
                && location.furniture.Count == _wetSuitabilityFurnitureCount)
                return;
            SurfaceMap? surface = SurfaceMap.For(location);
            if (surface == null)
                return;
            _wetSuitabilityLocation = location;
            _wetSuitabilityObjectCount = location.objects.Length;
            _wetSuitabilityFurnitureCount = location.furniture.Count;
            int width = surface.Width, height = surface.Height;
            var cells = new byte[width * height];
            // Every layer that draws OVER the ground, by enumeration rather than by name:
            // modded maps (SVE and friends) ship extra layers like Buildings2 and Front2, and a
            // veto that only knew the three vanilla names left pools sitting on their fences.
            var upperLayers = new List<xTile.Layers.Layer>();
            if (location.map?.Layers != null)
                foreach (xTile.Layers.Layer layer in location.map.Layers)
                    if (layer.Id != "Back" && layer.Id != "Paths")
                        upperLayers.Add(layer);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (surface.GetSurface(x, y) != SurfaceClass.Ground)
                        continue;
                    // The tile under a roof or a canopy is often honest stone or dirt on the Back
                    // layer, but the PIXELS at this spot belong to whatever the upper layers drew
                    // - a pool sitting on the Saloon's roof was the first screenshot of this
                    // feature. Any upper-layer art vetoes the tile; a happy side effect is that
                    // ground under a roof or a tree stays dry, which is what rain does.
                    bool coveredByArt = false;
                    foreach (xTile.Layers.Layer layer in upperLayers)
                    {
                        if (x < layer.LayerWidth && y < layer.LayerHeight && layer.Tiles[x, y] != null)
                        {
                            coveredByArt = true;
                            break;
                        }
                    }
                    if (coveredByArt)
                        continue;
                    string? groundType = location.doesTileHaveProperty(x, y, "Type", "Back");
                    bool puddleable = groundType is "Dirt" or "Stone"
                        || location.doesTileHaveProperty(x, y, "Diggable", "Back") != null;
                    cells[y * width + x] = puddleable ? (byte)255 : (byte)128;
                }
            }
            // Placed things are not map tiles: a fence, a keg or a couch stands ON a stone tile
            // and a pool painted across its sprite reads as a leak in the world. Their tiles go
            // fully dry; the counts above rebuild this when something is placed or picked up.
            foreach (var pair in location.objects.Pairs)
            {
                int tileX = (int)pair.Key.X, tileY = (int)pair.Key.Y;
                if (tileX >= 0 && tileY >= 0 && tileX < width && tileY < height)
                    cells[tileY * width + tileX] = 0;
            }
            foreach (StardewValley.Objects.Furniture piece in location.furniture)
            {
                Rectangle box = piece.boundingBox.Value;
                for (int tileY = box.Top / 64; tileY <= (box.Bottom - 1) / 64; tileY++)
                    for (int tileX = box.Left / 64; tileX <= (box.Right - 1) / 64; tileX++)
                        if (tileX >= 0 && tileY >= 0 && tileX < width && tileY < height)
                            cells[tileY * width + tileX] = 0;
            }
            _wetSuitabilityTexture?.Dispose();
            _wetSuitabilityTexture = new Texture2D(_device, width, height, false, SurfaceFormat.Alpha8);
            _wetSuitabilityTexture.SetData(cells);
            _wetSuitabilityMapTiles = new Vector2(width, height);
        }

        /// <summary>One line for radiance_report: the timeline and the gate in one place, because
        /// "the ground is not wet" has four causes that look identical from the screen.</summary>
        internal string WetWorldDiag(ModConfig config)
        {
            string contextId = Game1.currentLocation?.GetLocationContextId() ?? "?";
            bool raining = Game1.currentLocation?.IsRainingHere() ?? false;
            return $"wet world: toggle={config.WetWorldEnabled} wetness={WetnessNow:0.000} puddle={PuddleAmountNow:0.000} "
                 + $"presence={_fadeWet:0.000} context={contextId} raining={raining} "
                 + $"outdoors={Game1.currentLocation?.IsOutdoors ?? false} "
                 + $"mirror[wanted={_wetPuddleMirrorWanted} baked={ReflectRTReady} waterInMask={_hasWaterInMask}] "
                 + $"(rises over {WetRiseGameMinutes:0} game-min of rain, dries over {WetDecayGameMinutes:0})";
        }
    }
}
