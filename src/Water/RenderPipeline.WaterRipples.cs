using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Tools;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline — the rings that whatever is IN the water leaves behind it: a farmer wading
    /// across a ford, a duck paddling into the pond, a float landing where it was cast.
    ///
    /// The water shader knows nothing about the world, so this half of the feature is a small
    /// tracker: each tick it walks the things that can be in water, asks whether the tile under
    /// each one is water, and drops a ring where one has moved since the last ring it left. A ring
    /// is a world tile, the tick it was struck and how hard; eight of them live at once and the
    /// oldest slot is the next one taken. The shader draws them, so nothing here touches a pixel.
    ///
    /// The rings are world state, not per-screen state, and they are stepped exactly once per tick
    /// however many screens ask for them, which is what the tick stamp below is for: the water
    /// stage runs once per split screen and the world only moves once.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>How many rings can be live at once. Matches WAKE_RING_SLOTS in water.fx.</summary>
        private const int WakeRingSlots = 12;

        /// <summary>How long a ring lives, in ticks. Matches WAKE_RING_LIFE (seconds) in water.fx.</summary>
        private const int WakeRingLifeTicks = 63;

        /// <summary>Ticks between two rings from the same thing. Four a second: a trail has to
        /// overlap to read as a wake, and rings a third of a second apart left still water between
        /// them.</summary>
        private const int WakeRingSpacingTicks = 15;

        /// <summary>World pixels a thing must have moved since the last scan to count as moving.
        /// A duck asleep on a pond does not push water.</summary>
        private const float WakeRingMovedPixels = 0.75f;

        /// <summary>One ring: where it was struck, when, and how hard.</summary>
        private struct WakeRing
        {
            public Vector2 WorldTile;
            public int BornTick;
            public float Strength;
        }

        /// <summary>One thing that can leave rings, remembered between ticks so we can tell whether
        /// it moved and how long ago it last left one. Owner is the game's own object, held by
        /// reference only for identity: a critter has no id to key on.</summary>
        private struct WakeRingMaker
        {
            public object Owner;
            public Vector2 LastPosition;
            public int LastRingTick;
            public int SeenTick;
        }

        private readonly WakeRing[] _wakeRings = new WakeRing[WakeRingSlots];
        private readonly Vector4[] _wakeRingUniform = new Vector4[WakeRingSlots];
        private readonly List<WakeRingMaker> _wakeRingMakers = new();
        /// <summary>Row 28 of the game's animation sheet is its own "something touched the water"
        /// ring: it is added when a float lands, when a fish in a frenzy falls back, when an item
        /// is dropped in, when a farmer steps into the water. Watching for it is how the surface
        /// answers all of those at once without guessing at any of them.</summary>
        private const string GameAnimationSheet = "TileSheets\\animations";
        private const int WaterRingRowTop = 28 * 64;

        private int _wakeRingNextSlot;
        private int _wakeRingsScannedTick = -1;
        private int _bobberRingTick = -1000;
        private int _fishSpotRingTick = -1000;
        private readonly HashSet<TemporaryAnimatedSprite> _splashesSeen = new();
        private HashSet<TemporaryAnimatedSprite> _splashesSeenNow = new();

        /// <summary>How many rings the last frame handed the shader, for the report.</summary>
        private int _wakeRingsLive;

        /// <summary>Walk everything that can be in the water and leave rings where it moved. Called
        /// from the water stage, which runs once per screen, so the tick stamp holds it to one
        /// pass per tick; while the harness is frozen the tick never changes and no ring is
        /// born, which is what makes a frozen frame the same frame twice.</summary>
        private void UpdateWakeRings(ModConfig config)
        {
            if (config.WaterWakeRings <= 0f)
            {
                _wakeRingMakers.Clear();
                return;
            }

            int tick = Determinism.Ticks;
            if (tick == _wakeRingsScannedTick)
                return;
            _wakeRingsScannedTick = tick;

            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return;
            SurfaceMap? surfaces = SurfaceMap.For(location);
            if (surfaces == null)
                return;

            // Farmers, including this one. Swimming pushes more water than wading does: half the
            // body is under the surface rather than a pair of boots.
            foreach (Farmer who in location.farmers)
            {
                if (who == null)
                    continue;
                Vector2 feet = FeetOf(who.GetBoundingBox());
                if (!IsWaterAt(surfaces, feet))
                    continue;
                NoteWakeRingMaker(who, feet, who.swimming.Value ? 1.1f : 0.8f, tick);
            }

            // NPCs. CharactersIn is the same list the shadows and the sprite mask walk, so an
            // event actor crossing a stream leaves rings during a cutscene as well.
            foreach (NPC character in ShadowRenderer.CharactersIn(location))
            {
                // An invisible NPC is one the game will not draw, and a Custom Companions
                // creature flagged AppearUnderwater is painted by that framework from inside the
                // game's own water draw, under the surface: a starfish on the sea bed pushes no
                // water. The swimming gull from the same pack is not flagged, and rings.
                if (character == null || character.IsInvisible || IsDrawnUnderTheWater(character))
                    continue;
                Vector2 feet = FeetOf(character.GetBoundingBox());
                if (!IsWaterAt(surfaces, feet))
                    continue;
                NoteWakeRingMaker(character, feet, character.swimming.Value ? 1.0f : 0.75f, tick);
            }

            // Farm animals: ducks paddle straight into ponds, and a duck is the thing most players
            // will see this on.
            foreach (FarmAnimal animal in location.animals.Values)
            {
                if (animal == null)
                    continue;
                Vector2 feet = FeetOf(animal.GetBoundingBox());
                if (!IsWaterAt(surfaces, feet))
                    continue;
                NoteWakeRingMaker(animal, feet, 0.7f, tick);
            }

            // Critters, and only the gull. Nothing about a critter says which SIDE of the water
            // it is on: a mod's crab walking the sea bed and a gull sitting on the swell are both
            // "a critter at water level, not lifted", and the crab is under the surface it would
            // be ringing. The gull is the one the base game floats on water, so it is the one that
            // rings; a critter in the air is skipped as well, since a bird on the wing pushes
            // nothing. If a mod's own floating creature should ring, it needs a way to say so
            // rather than us guessing from a height.
            if (location.critters != null)
            {
                foreach (Critter critter in location.critters)
                {
                    if (critter is not Seagull)
                        continue;
                    float liftedBy = critter.yJumpOffset + critter.yOffset;
                    if (liftedBy < -6f)
                        continue;
                    Vector2 at = new(critter.position.X, critter.position.Y + liftedBy);
                    if (!IsWaterAt(surfaces, at))
                        continue;
                    NoteWakeRingMaker(critter, at, 0.45f, tick);
                }
            }

            UpdateBobberRings(surfaces, tick);
            UpdateFishSpotRings(location, surfaces, tick, config);
            UpdateSplashRings(location, surfaces);

            // Anything not seen this tick has left the water (or the map): drop it, so walking back
            // in is a fresh arrival and rings again.
            for (int i = _wakeRingMakers.Count - 1; i >= 0; i--)
            {
                if (_wakeRingMakers[i].SeenTick != tick)
                    _wakeRingMakers.RemoveAt(i);
            }
        }

        /// <summary>The float on the end of a cast line while it WAITS. It barely moves, so it
        /// cannot be tracked by movement the way a walking thing is: it rings slowly while it sits
        /// and quickly while a fish is worrying it. The landing itself is not handled here, because
        /// the game draws its own ring for that and <see cref="UpdateSplashRings"/> reads it.</summary>
        private void UpdateBobberRings(SurfaceMap surfaces, int tick)
        {
            if (Game1.player?.CurrentTool is not FishingRod rod || !rod.isFishing)
                return;

            Vector2 bobber = rod.bobber.Value;
            if (rod.castedButBobberStillInAir || !IsWaterAt(surfaces, bobber))
                return;

            int spacing = rod.isNibbling ? 12 : 48;
            float strength = rod.isNibbling ? 0.9f : 0.35f;
            if (tick - _bobberRingTick >= spacing)
            {
                SpawnWakeRing(bobber, strength);
                _bobberRingTick = tick;
            }
        }

        /// <summary>Remember this thing, and leave a ring if it has moved since its last one. A thing
        /// seen for the first time has just entered the water, and stepping in makes a bigger ring
        /// than the walking that follows.</summary>
        private void NoteWakeRingMaker(object owner, Vector2 worldPixel, float strength, int tick)
        {
            for (int i = 0; i < _wakeRingMakers.Count; i++)
            {
                if (!ReferenceEquals(_wakeRingMakers[i].Owner, owner))
                    continue;

                WakeRingMaker maker = _wakeRingMakers[i];
                float movedPixels = Vector2.Distance(maker.LastPosition, worldPixel);
                maker.SeenTick = tick;
                // The interval wanders by up to a third either way, seeded from where the thing
                // stands: rings dropped on an exact beat read as a metronome, and two things
                // walking side by side would drop theirs in step.
                int jitteredSpacing = WakeRingSpacingTicks + (((int)worldPixel.X * 7 + (int)worldPixel.Y * 13 + maker.LastRingTick) % 11) - 5;
                if (movedPixels >= WakeRingMovedPixels && tick - maker.LastRingTick >= jitteredSpacing)
                {
                    SpawnWakeRing(worldPixel, strength);
                    maker.LastRingTick = tick;
                }
                maker.LastPosition = worldPixel;
                _wakeRingMakers[i] = maker;
                return;
            }

            _wakeRingMakers.Add(new WakeRingMaker
            {
                Owner = owner,
                LastPosition = worldPixel,
                LastRingTick = tick,
                SeenTick = tick,
            });
            SpawnWakeRing(worldPixel, strength * 1.5f);
        }

        /// <summary>Take the next slot, oldest first. Twelve holds the trail of nearly three things
        /// walking at once: each leaves four rings a second and a ring lives just over a second.</summary>
        private void SpawnWakeRing(Vector2 worldPixel, float strength)
        {
            _wakeRings[_wakeRingNextSlot] = new WakeRing
            {
                WorldTile = worldPixel / 64f,
                BornTick = Determinism.Ticks,
                Strength = strength,
            };
            _wakeRingNextSlot = (_wakeRingNextSlot + 1) % WakeRingSlots;
        }

        /// <summary>Hand the shader the rings that are still alive, packed to the front of the array
        /// so the count gates the loop.</summary>
        private void SetWakeRingParams(Effect effect, ModConfig config)
        {
            float dial = config.WaterWakeRings;
            int live = 0;
            if (dial > 0f)
            {
                int tick = Determinism.Ticks;
                for (int i = 0; i < WakeRingSlots; i++)
                {
                    WakeRing ring = _wakeRings[i];
                    int ageTicks = tick - ring.BornTick;
                    if (ring.Strength <= 0f || ageTicks < 0 || ageTicks >= WakeRingLifeTicks)
                        continue;
                    _wakeRingUniform[live++] = new Vector4(ring.WorldTile.X, ring.WorldTile.Y, ageTicks / 60f, ring.Strength);
                }
            }
            for (int i = live; i < WakeRingSlots; i++)
                _wakeRingUniform[i] = Vector4.Zero;

            _wakeRingsLive = live;
            GetParam(effect, "WakeRingCount")?.SetValue((float)live);
            GetParam(effect, "WakeRings")?.SetValue(_wakeRingUniform);
            GetParam(effect, "WakeRingStrength")?.SetValue(dial);
        }

        /// <summary>Per NPC type: how to read whether the creature is drawn under the water. Null
        /// entries are types that carry no such flag.</summary>
        private static readonly Dictionary<Type, (FieldInfo model, PropertyInfo appearUnderwater)?> _underwaterFlagByType = new();

        /// <summary>Whether a mod paints this NPC under the water rather than on it. Custom
        /// Companions (the framework behind most creature packs) keeps a CompanionModel in a
        /// field called model with a bool AppearUnderwater; when it is set the framework skips the
        /// NPC's own draw and paints the creature from a drawWater postfix instead, so it lies
        /// under the surface by construction. Read by name through reflection, once per type, so
        /// this mod needs no reference to that one and a framework that follows the same naming
        /// is covered too.</summary>
        private static bool IsDrawnUnderTheWater(NPC character)
        {
            Type type = character.GetType();
            if (!_underwaterFlagByType.TryGetValue(type, out var flag))
            {
                flag = null;
                FieldInfo? modelField = type.GetField("model", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo? appearUnderwater = modelField?.FieldType.GetProperty("AppearUnderwater", BindingFlags.Instance | BindingFlags.Public);
                if (modelField != null && appearUnderwater != null && appearUnderwater.PropertyType == typeof(bool))
                    flag = (modelField, appearUnderwater);
                _underwaterFlagByType[type] = flag;
            }
            if (flag == null)
                return false;
            object? model = flag.Value.model.GetValue(character);
            return model != null && flag.Value.appearUnderwater.GetValue(model) is true;
        }

        /// <summary>The bubbling fish spot's state, for the report: where it is, whether the game
        /// has a frenzy running on it, and how far the tile is from the player.</summary>
        internal string DescribeFishSpot()
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return "no location";
            Point spot = location.fishSplashPoint.Value;
            if (spot.Equals(Point.Zero))
                return "none today";
            string frenzy = string.IsNullOrEmpty(location.fishFrenzyFish.Value)
                ? "no frenzy" : $"FRENZY on {location.fishFrenzyFish.Value}";
            Vector2 playerTile = Game1.player?.Tile ?? Vector2.Zero;
            float away = Vector2.Distance(playerTile, new Vector2(spot.X, spot.Y));
            return $"tile {spot.X},{spot.Y} ({away:F0} tiles away), {frenzy}";
        }

        /// <summary>Who is leaving rings right now, for the report: the game's own type of each
        /// thing and its name, and the assembly the type came from when it is not the game's.
        /// Exists because a starfish from a mod rang while lying on the sea bed, and nothing in
        /// the report could say which list it had come in through.</summary>
        internal string DescribeWakeRingMakers()
        {
            if (_wakeRingMakers.Count == 0)
                return "nothing in the water";
            var parts = new List<string>(_wakeRingMakers.Count);
            foreach (WakeRingMaker maker in _wakeRingMakers)
            {
                object owner = maker.Owner;
                System.Type type = owner.GetType();
                string name = owner switch
                {
                    Farmer farmer => farmer.Name,
                    NPC character => character.Name,
                    FarmAnimal animal => animal.type.Value,
                    _ => "",
                };
                string assembly = type.Assembly.GetName().Name ?? "";
                bool fromTheGame = assembly == "Stardew Valley" || assembly.StartsWith("StardewValley", StringComparison.Ordinal);
                parts.Add(fromTheGame ? $"{type.Name}:{name}" : $"{type.Name}:{name} ({assembly})");
            }
            return string.Join(", ", parts);
        }

        /// <summary>The bubbling patch the game marks with fishSplashPoint. Fish are working the
        /// surface there and the game says so with a looping sprite of white water; until now the
        /// water around it was as still as the rest of the lake. A ring every half second or so,
        /// somewhere in that tile rather than always its middle, and faster and harder while a
        /// frenzy is on. The spot is a tile coordinate the game keeps in a net field, so this needs
        /// no patch of any kind, and Point.Zero is its way of saying there is no spot today.</summary>
        private void UpdateFishSpotRings(GameLocation location, SurfaceMap surfaces, int tick, ModConfig config)
        {
            if (config.WaterFishSpotRings <= 0f)
                return;
            Point spot = location.fishSplashPoint.Value;
            if (spot.Equals(Point.Zero))
                return;

            bool frenzy = !string.IsNullOrEmpty(location.fishFrenzyFish.Value);
            int spacing = frenzy ? 21 : 37;
            if (tick - _fishSpotRingTick < spacing)
                return;
            _fishSpotRingTick = tick;

            // Somewhere in the tile, never twice in the same place. Hashed rather than taken from
            // the game's own random, which is shared with everything the day depends on.
            float acrossTile = HashUnitInterval(spot.X * 7919 + tick) - 0.5f;
            float downTile = HashUnitInterval(spot.Y * 6271 + tick * 31) - 0.5f;
            Vector2 at = new((spot.X + 0.5f + acrossTile * 0.75f) * 64f, (spot.Y + 0.5f + downTile * 0.75f) * 64f);
            if (!IsWaterAt(surfaces, at))
                return;

            float strength = (frenzy ? 0.8f : 0.55f) + HashUnitInterval(tick * 104729 + spot.X) * 0.4f;
            SpawnWakeRing(at, strength * config.WaterFishSpotRings);
        }

        /// <summary>Every ring the game itself draws on the water, turned into a strike on our
        /// surface. The game has one animation for "something touched the water" and adds it
        /// wherever that happens: a float landing, a fish falling back in during a frenzy, an item
        /// dropped in, a farmer stepping in, a fish jumping in a pond. Reading that is better than
        /// working each case out on our own, which is how the float's landing used to be found, and
        /// it costs one walk of a list that is usually a handful long. A ring the game draws over
        /// land (there are such uses) is skipped by the water test.</summary>
        private void UpdateSplashRings(GameLocation location, SurfaceMap surfaces)
        {
            _splashesSeenNow.Clear();
            var temporarySprites = location.temporarySprites;
            if (temporarySprites != null)
            {
                foreach (TemporaryAnimatedSprite sprite in temporarySprites)
                {
                    if (sprite == null || sprite.sourceRect.Y != WaterRingRowTop || sprite.sourceRect.Height != 64
                        || !string.Equals(sprite.textureName, GameAnimationSheet, StringComparison.Ordinal))
                        continue;
                    _splashesSeenNow.Add(sprite);
                    if (_splashesSeen.Contains(sprite))
                        continue;
                    // The sprite's position is the top left of its 64px cell, so the water was
                    // touched in the middle of it.
                    Vector2 at = sprite.position + new Vector2(32f, 32f);
                    if (IsWaterAt(surfaces, at))
                        SpawnWakeRing(at, 1.5f);
                }
            }

            // Swap rather than rebuild, so a tick costs no allocation once both sets have grown.
            _splashesSeen.Clear();
            foreach (TemporaryAnimatedSprite sprite in _splashesSeenNow)
                _splashesSeen.Add(sprite);
        }

        /// <summary>A number between 0 and 1 from a seed, for jitter that must not touch the game's
        /// own random: that one is shared with everything a day is built from.</summary>
        private static float HashUnitInterval(int seed)
        {
            unchecked
            {
                uint value = (uint)seed * 2654435761u;
                value ^= value >> 15;
                value *= 2246822519u;
                value ^= value >> 13;
                return (value & 0xFFFFFFu) / 16777215f;
            }
        }

        /// <summary>Author tool behind radiance_waterring: put a ring on the water where asked, at
        /// whatever age, so its shape can be looked at (and captured while the clock is frozen)
        /// without standing in a river with a rod. Nothing in the mod calls this.</summary>
        internal void SpawnWakeRingAt(Vector2 worldTile, float ageSeconds, float strength)
        {
            _wakeRings[_wakeRingNextSlot] = new WakeRing
            {
                WorldTile = worldTile,
                BornTick = Determinism.Ticks - (int)(ageSeconds * 60f),
                Strength = strength,
            };
            _wakeRingNextSlot = (_wakeRingNextSlot + 1) % WakeRingSlots;
        }

        /// <summary>The water tile nearest the given one, searched outward a dozen tiles, or null
        /// if there is no water that close. Behind "radiance_waterring near": a test ring is only
        /// worth striking on water, and asking the author to read tile numbers off the map first is
        /// how a test gets skipped.</summary>
        internal Vector2? NearestWaterTile(Vector2 fromTile, int reachTiles = 12)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return null;
            SurfaceMap? surfaces = SurfaceMap.For(location);
            if (surfaces == null)
                return null;

            int centreX = (int)fromTile.X, centreY = (int)fromTile.Y;
            for (int ring = 0; ring <= reachTiles; ring++)
            {
                for (int y = centreY - ring; y <= centreY + ring; y++)
                {
                    for (int x = centreX - ring; x <= centreX + ring; x++)
                    {
                        // Only the edge of this square: the inside was searched by a smaller ring.
                        if (ring > 0 && x != centreX - ring && x != centreX + ring && y != centreY - ring && y != centreY + ring)
                            continue;
                        if (surfaces.IsWater(x, y))
                            return new Vector2(x + 0.5f, y + 0.5f);
                    }
                }
            }
            return null;
        }

        /// <summary>Where a thing meets the water: the middle of its feet, a few pixels up from the
        /// bottom of the box so a sprite standing at a shoreline rings on the water it is in rather
        /// than on the sand its box ends over.</summary>
        private static Vector2 FeetOf(Rectangle boundingBox)
            => new(boundingBox.Center.X, boundingBox.Bottom - 8f);

        private static bool IsWaterAt(SurfaceMap surfaces, Vector2 worldPixel)
            => surfaces.IsWater((int)(worldPixel.X / 64f), (int)(worldPixel.Y / 64f));
    }
}
