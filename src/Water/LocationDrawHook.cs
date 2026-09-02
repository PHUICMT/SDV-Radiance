using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// What a LOCATION draws for itself, recorded from its own draw call so the water can leave it
    /// alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boat at Ginger Island is not a tile, not a building and not a terrain feature. Every
    /// cell under it reads <c>Back.Water = 'T'</c> with plain water art and nothing on any layer
    /// above, because <c>IslandSouth</c> holds the boat in fields of its own - boatPosition,
    /// boatTexture, GetBoatPosition - and paints it during its own draw. So there is no sheet name
    /// and no tile index for a label to be filed under, and the sprite mask, which knows
    /// characters, animals, critters, trees, crops and buildings, had no entry for it either. The
    /// ripple ran straight over the hull: reported twice, as Willy's boat in 1.3.0 and again this
    /// morning with a picture of the island dock.
    /// </para>
    /// <para>
    /// The rectangle is taken from the game's own draw call rather than rebuilt here. WillysBoat is
    /// a 24x24 tile sheet and the boat is some source rect inside it; guessing which, and guessing
    /// where its art meets the surface, is the mistake this codebase has already paid for three
    /// times. So the location's draw is bracketed by a flag, and while that flag is up every
    /// SpriteBatch.Draw records where it put its art. Whatever the location paints for itself is
    /// covered - the boat, and the string lights over it - without this file knowing what any of
    /// them are.
    /// </para>
    /// <para>
    /// The stamps are one frame old by the time the mask uses them: the mask is baked in
    /// RenderingWorld, before the world draws. That is exact for a boat that only moves during the
    /// departure cutscene, and a frame of lag on a moving one is a far smaller error than the
    /// ripple it replaces.
    /// </para>
    /// </remarks>
    internal static class LocationDrawHook
    {
        /// <summary>Live gate, mirrored from config each frame. While false the recording prefix is
        /// a single branch and nothing is kept.</summary>
        internal static bool Enabled;

        /// <summary>How many bracketed draws are on the stack. A COUNT and not a flag: a location's
        /// draw calls its base class's, both are patched, and with a flag the inner call's postfix
        /// lowered it halfway through - so everything the outer one drew after that point, the boat
        /// included, was never recorded and the hull rippled again.</summary>
        private static int _drawDepth;

        /// <summary>How many of the GAME'S OWN location draws are on the stack. GameLocation.draw is
        /// where the game paints everything it already knows about: the characters, the animals,
        /// the critters, the trees, the placed objects, the debris and every temporary sprite. None
        /// of that is a location's own art, and all of it is already stamped by the mask from its
        /// own lists, at this frame's positions. GameLocation declares a draw of its own, so the
        /// bracket above wrapped it like any other, and a location's override calls it in the
        /// middle: for one release everything a location drew was carved from the water a frame
        /// late. Chimney smoke drifting over a lake came out as blocks, and a bird or a falling
        /// leaf crossing a river cut a hole through the reflection under it as it went. While this
        /// is above zero nothing is recorded; the override's own draws, before and after its call
        /// to the base, still are.</summary>
        private static int _baseDrawDepth;

        /// <summary>How many draws of a thing that paints WATER for itself are on the stack. A fish
        /// pond is a building, and a location draws its buildings inside its own draw, so everything
        /// the pond painted - its bed, its water tiles, its net, the fish - was recorded as art over
        /// water and the whole pond went untouched: no ripple, no mirror, the game's water animation
        /// frozen by the freeze patch and nothing put in its place. While this is above zero the
        /// draws that ARE the water (the tinted bed, the animated water tiles, the sparkle strip) are
        /// skipped; the rim, the net, the sign and the bucket are recorded as before.</summary>
        private static int _ownWaterDepth;

        /// <summary>The three draws FishPond.draw makes that are the water itself rather than art
        /// over it: the bed from its own sheet, the animated tiles from the cursors sheet, and the
        /// sparkle strip. Anything else it draws stands on the rim and is carved as art.</summary>
        private static bool IsFishPondWaterDraw(Texture2D texture, Rectangle source)
            => ReferenceEquals(texture, Game1.mouseCursors)
               || source == new Rectangle(0, 80, 80, 80)
               || source == new Rectangle(16, 160, 48, 7);

        /// <summary>One draw the location made for itself, kept whole rather than reduced to a box.
        /// The art's own alpha is what decides where the water stops: a solid rectangle around the
        /// boat cut a visible square out of the sea around its mast and rigging.
        /// The position is WORLD pixels, not screen: the stamps are a frame old by the time the
        /// mask reads them, and a screen-space rectangle re-drawn after the camera has panned put
        /// the carve where the boat used to be on screen, a camera-speed smear off its hull. The
        /// world does not move when the camera does. Each stamp also remembers which location drew
        /// it: in split screen both screens' draws land in one collection, and a stamp must never
        /// carve another location's water.</summary>
        internal readonly struct Stamp
        {
            internal Stamp(Texture2D texture, Rectangle source, Vector2 worldTopLeft, Vector2 scale,
                           SpriteEffects effects, GameLocation? owner)
            {
                this.Texture = texture;
                this.Source = source;
                this.WorldTopLeft = worldTopLeft;
                this.Scale = scale;
                this.Effects = effects;
                this.Owner = owner;
            }

            internal Texture2D Texture { get; }
            internal Rectangle Source { get; }
            internal Vector2 WorldTopLeft { get; }
            internal Vector2 Scale { get; }
            internal SpriteEffects Effects { get; }
            internal GameLocation? Owner { get; }
        }

        /// <summary>What the location drew for itself on the last frame it was asked. Read by the
        /// sprite mask; never added to from anywhere else.</summary>
        internal static readonly List<Stamp> Stamps = new();

        private static readonly List<Stamp> _collecting = new();

        /// <summary>A sprite smaller than this is a bubble, a sparkle or a shadow, not a hull.
        /// Stamping those would carve holes in the water for things that should ripple with it.
        /// </summary>
        private const int SmallestWorthCarving = 24;

        /// <summary>
        /// Bracket the draw of every location type that paints something of its own, and record
        /// what those draws put on screen.
        /// </summary>
        /// <remarks>
        /// Located by shape rather than by a fixed member list: a location that draws itself is
        /// asked, one that does not is skipped, and a game update that renames a method degrades to
        /// a log line instead of a crash. Only types that DECLARE their own draw are patched, so
        /// the hundreds of locations that inherit the base one cost nothing.
        /// </remarks>
        internal static void Apply(Harmony harmony, IMonitor monitor)
        {
            var prefix = new HarmonyMethod(typeof(LocationDrawHook), nameof(LocationDraw_Prefix));
            var postfix = new HarmonyMethod(typeof(LocationDrawHook), nameof(LocationDraw_Postfix));
            // The base class's own draws PAUSE the recording instead of starting it: what they
            // paint is the game's entity lists, not a location's own art (see _baseDrawDepth).
            var basePrefix = new HarmonyMethod(typeof(LocationDrawHook), nameof(BaseDraw_Prefix));
            var basePostfix = new HarmonyMethod(typeof(LocationDrawHook), nameof(BaseDraw_Postfix));
            int patched = 0;
            foreach (Type type in LocationTypesThatDrawThemselves())
            {
                bool isBase = type == typeof(GameLocation);
                foreach (string methodName in new[] { "draw", "drawAboveFrontLayer", "drawAboveAlwaysFrontLayer" })
                {
                    MethodInfo? declared = type.GetMethod(
                        methodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        binder: null, new[] { typeof(SpriteBatch) }, modifiers: null);
                    if (declared == null)
                        continue;
                    try
                    {
                        harmony.Patch(declared,
                            prefix: isBase ? basePrefix : prefix,
                            postfix: isBase ? basePostfix : postfix);
                        if (!isBase)
                            patched++;
                    }
                    catch (Exception ex)
                    {
                        monitor.Log($"Could not bracket {type.FullName}.{methodName}: {ex.Message}", LogLevel.Trace);
                    }
                }
            }
            if (patched == 0)
            {
                // Say so rather than leaving a silent no-op: an unpatched game is the case where
                // the boat still ripples, and that is worth being able to read in a log.
                monitor.Log("No location declares a draw of its own, so nothing a location paints "
                          + "for itself can be kept out of the water.", LogLevel.Trace);
                return;
            }

            if (!InstallDrawRecorders(harmony, monitor))
                return;
            // The one thing a location draws that IS water. Bracketed so its draws are skipped.
            MethodInfo? pondDraw = AccessTools.Method(typeof(StardewValley.Buildings.FishPond), "draw", new[] { typeof(SpriteBatch) });
            if (pondDraw != null)
            {
                try
                {
                    harmony.Patch(pondDraw,
                        prefix: new HarmonyMethod(typeof(LocationDrawHook), nameof(OwnWaterDraw_Prefix)),
                        postfix: new HarmonyMethod(typeof(LocationDrawHook), nameof(OwnWaterDraw_Postfix)));
                }
                catch (Exception ex)
                {
                    monitor.Log($"Could not bracket FishPond.draw: {ex.Message}. A fish pond will carry no water effect.", LogLevel.Trace);
                }
            }
            monitor.Log($"Watching {patched} location draw(s) for art the water must not touch.", LogLevel.Trace);
        }

        /// <summary>
        /// The recorder itself, on SpriteBatch.Draw. Patched once, and it leaves immediately
        /// unless a bracketed draw is running, so the cost outside those few calls is one static
        /// bool. Both scale overloads: which one a location reaches for is its own business, and
        /// missing the one it happens to use would look exactly like the hook not working.
        /// </summary>
        /// <remarks>Its own method so that radiance_hooks can take the SpriteBatch patches off and
        /// put them back without touching the location brackets, which stay installed
        /// throughout.</remarks>
        /// <returns>Whether at least one overload was patched.</returns>
        internal static bool InstallDrawRecorders(Harmony harmony, IMonitor monitor)
        {
            int recorders = 0;
            foreach ((Type scaleType, string handler) in new[]
                     {
                         (typeof(float), nameof(SpriteBatchDraw_Prefix)),
                         (typeof(Vector2), nameof(SpriteBatchDrawVectorScale_Prefix)),
                     })
            {
                MethodInfo? draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), new[]
                {
                    typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color),
                    typeof(float), typeof(Vector2), scaleType, typeof(SpriteEffects), typeof(float),
                });
                if (draw == null)
                    continue;
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(LocationDrawHook), handler));
                recorders++;
            }
            if (recorders == 0)
            {
                monitor.Log("SpriteBatch.Draw has neither scale overload any more, so a location's "
                          + "own art cannot be recorded.", LogLevel.Trace);
                return false;
            }
            return true;
        }

        private static void OwnWaterDraw_Prefix()
        {
            _ownWaterDepth++;
        }

        private static void OwnWaterDraw_Postfix()
        {
            if (_ownWaterDepth > 0)
                _ownWaterDepth--;
        }

        /// <summary>Every loaded type that IS a location and declares a draw of its own.</summary>
        /// <remarks>
        /// A fixed list of three was the first version of this, and it was wrong the same day:
        /// Ginger Island's parrots live in a list on the island's own base class and fly over every
        /// island map, so only the one map named here was covered and the rest still rippled the
        /// birds. A location that draws nothing of its own declares no draw and costs nothing;
        /// mods that add locations get the same treatment as the base game's.
        /// </remarks>
        private static IEnumerable<Type> LocationTypesThatDrawThemselves()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch (Exception) { continue; }
                foreach (Type type in types)
                    if (type != null && typeof(GameLocation).IsAssignableFrom(type))
                        yield return type;
            }
        }

        private static void LocationDraw_Prefix()
        {
            if (!Enabled)
                return;
            _drawDepth++;
        }

        private static void LocationDraw_Postfix()
        {
            if (_drawDepth > 0)
                _drawDepth--;
        }

        private static void BaseDraw_Prefix()
        {
            if (!Enabled)
                return;
            _baseDrawDepth++;
        }

        private static void BaseDraw_Postfix()
        {
            if (_baseDrawDepth > 0)
                _baseDrawDepth--;
        }

        /// <summary>Record what one draw put on screen, art and all.</summary>
        private static void SpriteBatchDraw_Prefix(Texture2D texture, Vector2 position,
                                                   Rectangle? sourceRectangle, Vector2 origin,
                                                   float scale, SpriteEffects effects)
            => Record(texture, position, sourceRectangle, origin, new Vector2(scale, scale), effects);

        /// <inheritdoc cref="SpriteBatchDraw_Prefix"/>
        private static void SpriteBatchDrawVectorScale_Prefix(Texture2D texture, Vector2 position,
                                                              Rectangle? sourceRectangle, Vector2 origin,
                                                              Vector2 scale, SpriteEffects effects)
            => Record(texture, position, sourceRectangle, origin, scale, effects);

        private static void Record(Texture2D texture, Vector2 position, Rectangle? sourceRectangle,
                                   Vector2 origin, Vector2 scale, SpriteEffects effects)
        {
            if (_drawDepth == 0 || _baseDrawDepth > 0 || texture == null || texture.IsDisposed)
                return;
            Rectangle source = sourceRectangle ?? new Rectangle(0, 0, texture.Width, texture.Height);
            if (_ownWaterDepth > 0 && IsFishPondWaterDraw(texture, source))
                return;
            if (source.Width * scale.X < SmallestWorthCarving || source.Height * scale.Y < SmallestWorthCarving)
                return;
            // The draw was made in screen pixels; adding the camera back stores it in world pixels,
            // so the viewport that reads it next frame can be a different one.
            _collecting.Add(new Stamp(texture, source,
                new Vector2(position.X - origin.X * scale.X + Game1.viewport.X,
                            position.Y - origin.Y * scale.Y + Game1.viewport.Y),
                scale, effects, Game1.currentLocation));
        }

        /// <summary>Hand the last frame's collection to the mask and start a new one.</summary>
        /// <remarks>
        /// Publishing at the end of each bracketed draw was wrong, and it took a second report of
        /// the same rippling hull to see why: the game draws a location in several passes - draw,
        /// then drawAboveFrontLayer, then drawAboveAlwaysFrontLayer - so the pass that painted the
        /// boat published it and the next pass, which painted nothing, published over it. What
        /// belongs together is a FRAME, not a pass, so the collection is closed here, once, where
        /// the mask asks for it.
        /// </remarks>
        internal static void BeginFrame()
        {
            Stamps.Clear();
            Stamps.AddRange(_collecting);
            _collecting.Clear();
            _drawDepth = 0;
            _baseDrawDepth = 0;
            _ownWaterDepth = 0;
        }

        /// <summary>Forget everything on a warp: the frame the stamps came from belongs to the
        /// map that was left.</summary>
        internal static void Reset()
        {
            _drawDepth = 0;
            _baseDrawDepth = 0;
            _ownWaterDepth = 0;
            _collecting.Clear();
            Stamps.Clear();
        }
    }
}
