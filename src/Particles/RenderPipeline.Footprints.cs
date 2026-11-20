using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Footprints left in snow, sand and wet dirt, fading after a while.
    ///
    /// <para>Prints are kept as a list of stamps, each a handful of art pixels, and drawn into the
    /// game's sorted world batch just under the feet that made them, the way the foot dust is. The
    /// game's own night light and every lighting stage after it fall on them as they fall on the
    /// ground. Nothing is stamped while nobody walks, and with no live print nothing is drawn.</para>
    ///
    /// <para>The look follows what players liked elsewhere and avoids what they did not: prints only
    /// on snow, sand and mud (never on dry grass), a darker hollow with a lighter rim on snow, an
    /// opacity well short of solid, and a fade that drops the print's pixels one by one in an
    /// ordered pattern rather than turning it into a blur.</para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>What a print was pressed into, which sets its colour and how long it lasts.</summary>
        private enum FootprintGround : byte { Snow, Sand, Mud }

        private struct Footprint
        {
            internal Vector2 WorldPosition;
            internal float StampedAtSeconds;
            /// <summary>Width and height of the print in art pixels, already turned to the walk.</summary>
            internal byte WidthPixels, HeightPixels;
            internal FootprintGround Ground;
            /// <summary>Which edge the snow rim is on: true for the top (walking up or down).</summary>
            internal bool RimOnTop;
        }

        /// <summary>The most prints kept at once. The oldest go first, which is what a trail
        /// fading behind somebody should do anyway.</summary>
        private const int FootprintCapacity = 600;
        /// <summary>How long a print lasts at "how long they stay" 1, in seconds of play.</summary>
        private const float SnowPrintSeconds = 90f, SandPrintSeconds = 60f, MudPrintSeconds = 60f;
        /// <summary>Falling snow fills prints in and rain smooths sand and snow, this much faster.</summary>
        private const float FillingWeatherShare = 0.3f;
        /// <summary>How far apart steps fall for somebody farmer sized, in world pixels.</summary>
        private const float FootprintStridePixels = 30f;
        /// <summary>How long a new print takes to show, so a step never pops in.</summary>
        private const float FootprintFadeInSeconds = 0.15f;
        /// <summary>The last share of a print's life over which it fades away.</summary>
        private const float FootprintFadeShare = 0.4f;

        private readonly Footprint[] _footprints = new Footprint[FootprintCapacity];
        private int _footprintNext, _footprintCount;
        private float _footprintClockSeconds;
        private GameLocation? _footprintLocation;
        private int _footprintsStamped;

        /// <summary>Where each walker last left a print, and which foot it was.</summary>
        private sealed class StrideTrack
        {
            internal Vector2 LastStamp;
            internal bool LeftFoot;
        }

        private readonly ConditionalWeakTable<Character, StrideTrack> _strides = [];

        /// <summary>Snow colour of the hollow, a little blue as shade on snow is; its lit rim; sand
        /// pressed dark the way wet sand is; and mud.</summary>
        private static readonly Color SnowHollowColour = new(0x8E, 0xA2, 0xC2);
        private static readonly Color SnowRimColour = new(0xF6, 0xF9, 0xFD);
        private static readonly Color SandPrintColour = new(0x9A, 0x7C, 0x55);
        private static readonly Color MudPrintColour = new(0x3C, 0x2A, 0x1C);
        private const float SnowPrintOpacity = 0.6f, SandPrintOpacity = 0.4f, MudPrintOpacity = 0.45f;

        /// <summary>Bayer 4x4, the order a print's pixels drop out in as it fades.</summary>
        private static readonly float[] FootprintDither =
            [0f, 8f, 2f, 10f, 12f, 4f, 14f, 6f, 3f, 11f, 1f, 9f, 15f, 7f, 13f, 5f];

        internal int FootprintsStamped => _footprintsStamped;
        internal int FootprintsLive => _footprintCount;

        /// <summary>Once per simulated tick: move the clock and stamp a print under anyone whose
        /// stride has carried them far enough since their last one.</summary>
        private void StampFootprints(ModConfig config)
        {
            GameLocation? location = Game1.currentLocation;
            if (!ReferenceEquals(location, _footprintLocation))
            {
                _footprintLocation = location;
                _footprintCount = 0;
                _footprintNext = 0;
            }
            _footprintClockSeconds += 1f / 60f;
            if (!config.Footprints || location == null || !location.IsOutdoors)
                return;

            if (Game1.player != null)
                StepFor(location, Game1.player, Game1.player.GetBoundingBox(), Game1.player.FacingDirection, 2, 3, 1f);
            if (!config.FootprintsEveryone)
                return;
            foreach (NPC villager in ShadowRenderer.CharactersIn(location))
            {
                Rectangle box = villager.GetBoundingBox();
                (byte width, byte height, float stride) = PrintSizeFor(box.Width);
                StepFor(location, villager, box, villager.FacingDirection, width, height, stride);
            }
            foreach (FarmAnimal animal in location.animals.Values)
            {
                Rectangle box = animal.GetBoundingBox();
                (byte width, byte height, float stride) = PrintSizeFor(box.Width);
                StepFor(location, animal, box, animal.FacingDirection, width, height, stride);
            }
        }

        /// <summary>Print size and stride by how wide the walker is: a chicken or a cat leaves one
        /// art pixel close together, a person two across and three along the walk, a horse or a cow
        /// three by two far apart.</summary>
        private static (byte Width, byte Height, float Stride) PrintSizeFor(int boxWidthPixels)
            => boxWidthPixels < 40 ? ((byte)1, (byte)1, 0.6f)
             : boxWidthPixels < 70 ? ((byte)2, (byte)3, 1f)
             : ((byte)3, (byte)2, 1.4f);

        private void StepFor(GameLocation location, Character walker, Rectangle box, int facing,
            byte width, byte height, float strideScale)
        {
            var feet = new Vector2(box.Center.X, box.Bottom - 6f);
            StrideTrack track = _strides.GetValue(walker, static _ => new StrideTrack { LastStamp = new Vector2(float.NaN) });
            if (float.IsNaN(track.LastStamp.X) || Vector2.DistanceSquared(track.LastStamp, feet) > 64f * 64f * 4f)
            {
                // First sight of them, or a jump (a warp, a door): start the trail here.
                track.LastStamp = feet;
                return;
            }
            float stride = FootprintStridePixels * strideScale;
            if (Vector2.DistanceSquared(track.LastStamp, feet) < stride * stride)
                return;
            track.LastStamp = feet;
            track.LeftFoot = !track.LeftFoot;

            FootprintGround? ground = GroundForPrint(location, (int)feet.X / 64, (int)feet.Y / 64);
            if (ground == null)
                return;
            bool walkingUpOrDown = facing is 0 or 2;
            // Left and right feet two art pixels apart across the walk, with one pixel of ground
            // between them, so a trail reads as pairs of feet and not as a dotted line.
            float side = track.LeftFoot ? -8f : 4f;
            Vector2 at = walkingUpOrDown ? feet + new Vector2(side, 0f) : feet + new Vector2(0f, side);
            ref Footprint print = ref _footprints[_footprintNext];
            print.WorldPosition = new Vector2(MathF.Floor(at.X / 4f) * 4f, MathF.Floor(at.Y / 4f) * 4f);
            print.StampedAtSeconds = _footprintClockSeconds;
            print.WidthPixels = walkingUpOrDown ? width : height;
            print.HeightPixels = walkingUpOrDown ? height : width;
            print.Ground = ground.Value;
            print.RimOnTop = walkingUpOrDown;
            _footprintNext = (_footprintNext + 1) % FootprintCapacity;
            _footprintCount = Math.Min(_footprintCount + 1, FootprintCapacity);
            _footprintsStamped++;
        }

        /// <summary>What a step here presses into, or nothing. Winter grass and dirt outdoors are
        /// under snow (the game picks its snowy footstep on winter grass, and its own snow print,
        /// which is gone in under two seconds); sand is sand; dirt takes a print only while it is wet.</summary>
        private FootprintGround? GroundForPrint(GameLocation location, int tileX, int tileY)
        {
            string? type = location.doesTileHaveProperty(tileX, tileY, "Type", "Back");
            bool winter = location.GetSeason() == Season.Winter;
            if (type is "Sand" || (type is "Dirt" && IsSandyPlace(location)))
                return FootprintGround.Sand;
            if (type is "Dirt" or "Grass")
            {
                if (winter)
                    return FootprintGround.Snow;
                bool wet = LocalSky.IsRaining || PuddleAmountNow > 0.25f;
                if (type is "Dirt" && wet)
                    return FootprintGround.Mud;
            }
            return null;
        }

        /// <summary>A beach or a desert, where the game's maps mark their sand "Dirt" (it is the
        /// type the game plays its sandy footstep on).</summary>
        private static bool IsSandyPlace(GameLocation location)
        {
            if (location is StardewValley.Locations.Beach or StardewValley.Locations.Desert)
                return true;
            string name = location.NameOrUniqueName ?? "";
            return name.Contains("Beach", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Desert", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>How long a print on this ground lasts, now. Asked at draw time, so snow starting
        /// to fall fills the prints already there, and a drying path takes its mud prints with it.</summary>
        private float PrintLifeSeconds(FootprintGround ground, ModConfig config)
        {
            float lasting = Math.Max(0.05f, config.FootprintsLasting);
            switch (ground)
            {
                case FootprintGround.Snow:
                    return SnowPrintSeconds * lasting * (LocalSky.IsSnowing || LocalSky.IsRaining ? FillingWeatherShare : 1f);
                case FootprintGround.Sand:
                    return SandPrintSeconds * lasting * (LocalSky.IsRaining ? FillingWeatherShare : 1f);
                default:
                    // Mud keeps its prints only while it is mud.
                    float wetness = LocalSky.IsRaining ? 1f : MathHelper.Clamp(PuddleAmountNow * 2f, 0f, 1f);
                    return MudPrintSeconds * lasting * wetness;
            }
        }

        /// <summary>Draw the live prints into the game's sorted world batch, under the feet.</summary>
        internal void DrawFootprints(SpriteBatch spriteBatch, ModConfig config)
        {
            if (_footprintCount == 0 || !config.Footprints || Game1.staminaRect == null)
                return;
            float strength = MathHelper.Clamp(config.FootprintsStrength, 0f, 2f);
            if (strength <= 0f)
                return;
            float viewLeft = Game1.viewport.X - 16f, viewTop = Game1.viewport.Y - 16f;
            float viewRight = Game1.viewport.X + Game1.viewport.Width + 16f, viewBottom = Game1.viewport.Y + Game1.viewport.Height + 16f;
            for (int i = 0; i < _footprintCount; i++)
            {
                ref Footprint print = ref _footprints[i];
                float age = _footprintClockSeconds - print.StampedAtSeconds;
                float life = PrintLifeSeconds(print.Ground, config);
                if (age >= life)
                    continue;
                Vector2 at = print.WorldPosition;
                if (at.X < viewLeft || at.X > viewRight || at.Y < viewTop || at.Y > viewBottom)
                    continue;
                float rising = MathHelper.Clamp(age / FootprintFadeInSeconds, 0f, 1f);
                float falling = MathHelper.Clamp((life - age) / (life * FootprintFadeShare), 0f, 1f);
                float presence = Math.Min(rising, falling);
                if (presence <= 0.01f)
                    continue;
                (Color colour, float opacity) = print.Ground switch
                {
                    FootprintGround.Snow => (SnowHollowColour, SnowPrintOpacity),
                    FootprintGround.Sand => (SandPrintColour, SandPrintOpacity),
                    _ => (MudPrintColour, MudPrintOpacity),
                };
                float alpha = Math.Min(1f, opacity * strength) * Math.Min(1f, 0.35f + presence);
                // Just under the feet that made it, so the walker stands on it and anything a
                // little further down the screen still covers it.
                float depth = Math.Clamp((at.Y + 4f) / 10000f - 0.00001f, 0f, 1f);
                int artX = (int)(at.X / 4f), artY = (int)(at.Y / 4f);
                for (int y = 0; y < print.HeightPixels; y++)
                    for (int x = 0; x < print.WidthPixels; x++)
                    {
                        float threshold = FootprintDither[((artY + y) & 3) * 4 + ((artX + x) & 3)] / 16f;
                        if (presence <= threshold)
                            continue;
                        Vector2 local = Game1.GlobalToLocal(Game1.viewport, new Vector2(at.X + x * 4f, at.Y + y * 4f));
                        spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)local.X, (int)local.Y, 4, 4), null,
                            colour * alpha, 0f, Vector2.Zero, SpriteEffects.None, depth);
                    }
                // Snow pushed up out of the hollow catches the light along one edge.
                if (print.Ground == FootprintGround.Snow && presence > 0.5f)
                {
                    Vector2 rim = print.RimOnTop ? new Vector2(at.X, at.Y - 4f) : new Vector2(at.X - 4f, at.Y);
                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, rim);
                    int rimWidth = print.RimOnTop ? print.WidthPixels * 4 : 4;
                    int rimHeight = print.RimOnTop ? 4 : print.HeightPixels * 4;
                    spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)local.X, (int)local.Y, rimWidth, rimHeight), null,
                        SnowRimColour * (alpha * 0.6f), 0f, Vector2.Zero, SpriteEffects.None, depth);
                }
            }
        }
    }
}
