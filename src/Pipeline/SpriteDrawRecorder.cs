using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Remembers every sprite the game drew into its sorted world batch this frame - texture,
    /// source, placement, flip, depth - so the same picture can be drawn again with other
    /// textures. The normal pass (see <see cref="SheetNormalCache"/>) replays it with each
    /// sheet's normal map in place of the art; nothing here decides what the replay is for.
    /// </summary>
    /// <remarks>
    /// <para>Recording rather than redrawing: the game's own draw methods carry the logic that
    /// decides what is on screen and where (growth stages, facing, held items, the wobble of a
    /// tree being shaken), and calling them a second time would pay that logic twice and keep a
    /// list of every class that draws. A prefix on the three full <c>SpriteBatch.Draw</c>
    /// overloads (the short ones call these) sees the result of that logic for free, and only
    /// while the world's sorted step is being drawn and only for the game's own batch, so a
    /// bake this mod draws into a target of its own in the same frame is not on the list.</para>
    /// <para>The list holds what was drawn in SCREEN pixels, as the batch received it, because
    /// the replay happens in the same frame under the same camera.</para>
    /// </remarks>
    internal static class SpriteDrawRecorder
    {
        internal readonly struct Record
        {
            public readonly Texture2D Texture;
            public readonly Rectangle Source;
            /// <summary>Top-left the batch was handed, before the origin is applied.</summary>
            public readonly Vector2 Position;
            /// <summary>Set when the draw gave a destination rectangle instead of a scale.</summary>
            public readonly Rectangle Destination;
            public readonly bool UsesDestination;
            public readonly float Alpha;
            public readonly float Rotation;
            public readonly Vector2 Origin;
            public readonly Vector2 Scale;
            public readonly SpriteEffects Effects;
            public readonly float Depth;

            public Record(Texture2D texture, Rectangle source, Vector2 position, Rectangle destination, bool usesDestination,
                float alpha, float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float depth)
            {
                Texture = texture; Source = source; Position = position; Destination = destination;
                UsesDestination = usesDestination; Alpha = alpha; Rotation = rotation; Origin = origin;
                Scale = scale; Effects = effects; Depth = depth;
            }
        }

        /// <summary>Whether anyone wants this frame recorded. Set before the world draws by whoever
        /// will replay it; false costs one static read per draw call and nothing else.</summary>
        internal static bool Wanted;
        /// <summary>Set while this mod draws its OWN shadows into the world batch (see
        /// ShadowRenderer.DrawInto). A drawn shadow is lighting, not a body: its bakes are render
        /// targets, so the replay's flat stand-in stamped each one's whole quad flat over the
        /// normal buffer, wiping the relief of the wall or ground behind it in a rectangle that
        /// followed the caster.</summary>
        internal static bool SuppressRecording;
        private static bool _recording;
        /// <summary>How deep inside patched Draw overloads the current call is. The overloads
        /// CALL EACH OTHER (float-scale delegates to vector-scale), so one sprite fires a prefix
        /// per level: every draw was recorded twice, and with the sheet upscaler on, the inner
        /// level recorded the swapped doubled target - a render target, so the replay stamped its
        /// flat stand-in over the real normal map at the same depth. Only the outermost level
        /// records; it is also the only level that still sees the original sheet.</summary>
        private static int _drawDepth;
        private static readonly List<Record> _records = new(4096);
        /// <summary>How many draws the last completed frame recorded, for radiance_report.</summary>
        internal static int LastCount { get; private set; }
        internal static int PatchedOverloads { get; private set; }
        internal static IReadOnlyList<Record> Records => _records;
        /// <summary>Where the sorted step's own draws end and the map's FRONT layers begin. The
        /// game draws the front layers after the sprites and over them, so they are recorded in
        /// the same list but replayed as a second pass on top (see <see cref="Replay"/>).</summary>
        internal static int SortedCount { get; private set; }

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            (Type[] signature, string handler)[] overloads =
            {
                (new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawVectorScale_Prefix)),
                (new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawFloatScale_Prefix)),
                (new[] { typeof(Texture2D), typeof(Rectangle), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawDestination_Prefix)),
            };
            foreach ((Type[] signature, string handler) in overloads)
            {
                var draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), signature);
                if (draw == null)
                {
                    monitor.Log($"SpriteBatch.Draw overload for {handler} not found; sprite relief will miss those draws.", LogLevel.Warn);
                    continue;
                }
                // The finalizer, not a postfix, undoes the depth step: it runs on the exception
                // path too, so a draw that throws cannot leave the counter raised for the frame.
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(SpriteDrawRecorder), handler),
                    finalizer: new HarmonyMethod(typeof(SpriteDrawRecorder), nameof(Draw_Finalizer)));
                PatchedOverloads++;
            }
        }

        /// <summary>The world's sorted step is about to draw: start a fresh list if anyone wants one.</summary>
        internal static void BeginWorldSorted()
        {
            _records.Clear();
            SortedCount = 0;
            _drawDepth = 0;
            _recording = Wanted;
        }

        /// <summary>The sorted step has drawn: mark where it ended. Recording CONTINUES, because
        /// the map's front layers are drawn next and they cover the sprites - a farmer walking
        /// behind a building was showing their bevelled outline through the wall, since nothing
        /// covered them in the normal buffer.</summary>
        internal static void EndWorldSorted()
        {
            SortedCount = _records.Count;
        }

        /// <summary>The front layers have drawn: close the list, before the weather does. Rain and
        /// snow are drawn after this and would have stamped the whole screen flat.</summary>
        internal static void EndWorldFront()
        {
            _recording = false;
            LastCount = _records.Count;
        }

        /// <summary>Whether this prefix call should record: only while the world batch is being
        /// recorded, and only at the outermost overload level. Steps the depth when it is the
        /// game's batch, so the finalizer's decrement always pairs with exactly one increment.</summary>
        private static bool StepAndAsk(SpriteBatch batch, Texture2D texture)
        {
            if (!_recording || !ReferenceEquals(batch, Game1.spriteBatch))
                return false;
            return _drawDepth++ == 0 && !SuppressRecording && texture != null;
        }

        private static void Draw_Finalizer(SpriteBatch __instance)
        {
            if (_recording && ReferenceEquals(__instance, Game1.spriteBatch) && _drawDepth > 0)
                _drawDepth--;
        }

        private static void DrawVectorScale_Prefix(SpriteBatch __instance, Texture2D texture, Vector2 position, Rectangle? sourceRectangle,
            Color color, float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
        {
            if (!StepAndAsk(__instance, texture))
                return;
            _records.Add(new Record(texture, sourceRectangle ?? texture.Bounds, position, Rectangle.Empty, false,
                color.A / 255f, rotation, origin, scale, effects, layerDepth));
        }

        private static void DrawFloatScale_Prefix(SpriteBatch __instance, Texture2D texture, Vector2 position, Rectangle? sourceRectangle,
            Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        {
            if (!StepAndAsk(__instance, texture))
                return;
            _records.Add(new Record(texture, sourceRectangle ?? texture.Bounds, position, Rectangle.Empty, false,
                color.A / 255f, rotation, origin, new Vector2(scale, scale), effects, layerDepth));
        }

        private static void DrawDestination_Prefix(SpriteBatch __instance, Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle,
            Color color, float rotation, Vector2 origin, SpriteEffects effects, float layerDepth)
        {
            if (!StepAndAsk(__instance, texture))
                return;
            _records.Add(new Record(texture, sourceRectangle ?? texture.Bounds, Vector2.Zero, destinationRectangle, true,
                color.A / 255f, rotation, origin, Vector2.One, effects, layerDepth));
        }

        /// <summary>
        /// Draw the recorded frame again into <paramref name="batch"/> (already begun, FrontToBack
        /// like the game's), asking <paramref name="substitute"/> for the texture to use in place of
        /// each sheet. A null answer draws <paramref name="flat"/> (a 1x1 stand-in) over the same
        /// footprint instead, so a sprite without a substitute still covers what stands behind it.
        /// </summary>
        internal static int Replay(SpriteBatch batch, Func<Texture2D, SpriteEffects, Texture2D?> substitute, Texture2D flat)
            => Replay(batch, substitute, flat, 0, SortedCount);

        /// <summary>The front-layer half of the list (see <see cref="SortedCount"/>), for the caller
        /// to draw in its own batch after the sorted one, which is the order the game drew them.</summary>
        internal static int ReplayFront(SpriteBatch batch, Func<Texture2D, SpriteEffects, Texture2D?> substitute, Texture2D flat)
            => Replay(batch, substitute, flat, SortedCount, _records.Count);

        private static int Replay(SpriteBatch batch, Func<Texture2D, SpriteEffects, Texture2D?> substitute, Texture2D flat,
            int from, int to)
        {
            int drawn = 0;
            for (int i = from; i < to && i < _records.Count; i++)
            {
                Record record = _records[i];
                if (record.Texture.IsDisposed || record.Alpha <= 0.002f)
                    continue;
                // The game's round blob shadow is drawn under every character and would put a
                // bevel ring round their feet; it has no sides to light.
                if (ReferenceEquals(record.Texture, Game1.shadowTexture))
                    continue;
                // Alpha only: the replay blends straight (NonPremultiplied) colour, and scaling
                // the rgb would bend the normal the encoding carries.
                Color tint = new(255, 255, 255, (int)(record.Alpha * 255f));
                Texture2D? replacement = substitute(record.Texture, record.Effects);
                if (replacement != null)
                {
                    if (record.UsesDestination)
                        batch.Draw(replacement, record.Destination, record.Source, tint, record.Rotation, record.Origin, record.Effects, record.Depth);
                    else
                        batch.Draw(replacement, record.Position, record.Source, tint, record.Rotation, record.Origin, record.Scale, record.Effects, record.Depth);
                }
                else
                {
                    // The stand-in is one texel, so the origin is re-expressed in its own texels.
                    if (record.UsesDestination)
                    {
                        Vector2 originInFlat = new(record.Origin.X / Math.Max(1, record.Source.Width), record.Origin.Y / Math.Max(1, record.Source.Height));
                        batch.Draw(flat, record.Destination, null, tint, record.Rotation, originInFlat, record.Effects, record.Depth);
                    }
                    else
                    {
                        var footprint = new Rectangle((int)record.Position.X, (int)record.Position.Y,
                            (int)Math.Ceiling(record.Source.Width * record.Scale.X), (int)Math.Ceiling(record.Source.Height * record.Scale.Y));
                        Vector2 originInFlat = new(record.Origin.X / Math.Max(1, record.Source.Width), record.Origin.Y / Math.Max(1, record.Source.Height));
                        batch.Draw(flat, footprint, null, tint, record.Rotation, originInFlat, record.Effects, record.Depth);
                    }
                }
                drawn++;
            }
            return drawn;
        }
    }
}
