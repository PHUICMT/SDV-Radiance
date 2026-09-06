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

        /// <summary>A pending "what drew this pixel?" question (radiance_drawsat). Six people have
        /// reported one object looking softer than the world around it, and the only way to answer
        /// which sheet it came from was to guess at the mod list. The list below already holds every
        /// draw the game made, with its texture: the question is just a lookup.</summary>
        private static Point? _askedPoint;
        /// <summary>Whether a question is waiting, so the frame gets recorded even when nothing
        /// else wants recording (the relief pass is off by default).</summary>
        internal static bool WaitingForAnswer => _askedPoint.HasValue;

        internal static void AskWhatDrew(Point screenPoint)
        {
            _askedPoint = screenPoint;
            _flushWatchFrames = 2;
            _flushedWith.Clear();
            _answered.Clear();
        }

        /// <summary>True from the sorted step's start to the front layers' end: a Begin on the
        /// game's batch inside this span is somebody ending the world batch and starting it again
        /// under the world, and whatever sampler they start it with is what every sprite drawn
        /// after them in the frame is read with.</summary>
        internal static bool InWorldStep { get; private set; }
        private static IMonitor? _monitor;
        private static readonly HashSet<string> _restartsSeen = new();
        /// <summary>Frames left in which the sampler each texture run is flushed with is noted,
        /// after a radiance_drawsat question. The runs flush after the answer prints (the batch
        /// is still open at RenderedWorld), so the table prints two frames after the question.</summary>
        private static int _flushWatchFrames;
        internal static bool FlushWatchOpen => _flushWatchFrames > 0;
        private static readonly Dictionary<Texture2D, (SamplerState applied, SamplerState begun)> _flushedWith = new();
        private static readonly List<Texture2D> _answered = new();

        /// <summary>The game's own batch was begun again while the world was being drawn. Named once
        /// per caller and sampler, because the one that matters is the one that is not Point: a
        /// batch begun without a sampler reads LINEAR in MonoGame, and whoever ends the world batch
        /// to draw their own thing and starts it again that way turns every sprite the game draws
        /// after them into a filtered picture, while the map tiles drawn before stay crisp.</summary>
        internal static void NoteWorldBatchRestart(SpriteSortMode sortMode, SamplerState? sampler)
        {
            if (_monitor == null)
                return;
            string filter = sampler == null ? "none given, so MonoGame reads LINEAR" : sampler.Filter.ToString();
            string caller = CallerOfBegin();
            if (!_restartsSeen.Add(caller + "|" + filter + "|" + sortMode))
                return;
            bool filtered = sampler == null || sampler.Filter != TextureFilter.Point;
            _monitor.Log($"world batch restarted mid-frame by {caller}: {sortMode}, sampler {filter}."
                       + (filtered ? " Every sprite the game draws after this point in the frame is filtered, not pixel art." : ""),
                filtered ? LogLevel.Warn : LogLevel.Info);
        }

        /// <summary>Who called SpriteBatch.Begin: the first frames above the patch that belong to
        /// neither Harmony nor MonoGame nor this file, by assembly and method.</summary>
        private static string CallerOfBegin()
        {
            var trace = new System.Diagnostics.StackTrace(1, false);
            var parts = new List<string>(4);
            foreach (System.Diagnostics.StackFrame frame in trace.GetFrames())
            {
                System.Reflection.MethodBase? method = frame.GetMethod();
                Type? type = method?.DeclaringType;
                if (type == null)
                    continue;
                string assembly = type.Assembly.GetName().Name ?? "";
                if (assembly.StartsWith("0Harmony", StringComparison.Ordinal) || assembly.StartsWith("MonoGame", StringComparison.Ordinal)
                    || assembly.StartsWith("System", StringComparison.Ordinal) || type == typeof(SheetUpscaler) || type == typeof(SpriteDrawRecorder))
                    continue;
                parts.Add($"{assembly}:{type.Name}.{method!.Name}");
                if (parts.Count == 4)
                    break;
            }
            return parts.Count == 0 ? "(no caller frames)" : string.Join(" <- ", parts);
        }

        /// <summary>One run of one texture is about to flush in the game's batch: what the device
        /// will read it with, and what the batch asked for at Begin.</summary>
        internal static void NoteFlush(Texture2D texture, SamplerState applied, SamplerState begun)
            => _flushedWith[texture] = (applied, begun);

        private static void PrintFlushTable()
        {
            if (_monitor == null)
                return;
            _monitor.Log("=== how the game's world batch sampled its textures in the frames after the question ===", LogLevel.Info);
            int filtered = 0;
            foreach ((Texture2D texture, (SamplerState applied, SamplerState begun)) in _flushedWith)
            {
                bool point = applied.Filter == TextureFilter.Point;
                bool listed = _answered.Contains(texture);
                if (point && !listed)
                    continue;
                if (!point)
                    filtered++;
                string name = string.IsNullOrEmpty(texture.Name) ? $"(unnamed {texture.Width}x{texture.Height})" : texture.Name;
                _monitor.Log($"  {name}: drawn with {applied.Filter}"
                           + (begun.Filter == applied.Filter ? "" : $" (its batch was begun with {begun.Filter})")
                           + (listed ? "  <-- covers the asked pixel" : ""),
                    point ? LogLevel.Info : LogLevel.Warn);
            }
            _monitor.Log(filtered == 0
                    ? "  every texture in the game's world batch was drawn with Point filtering; a soft sprite is soft in its sheet."
                    : $"  {filtered} texture(s) were drawn FILTERED in a batch meant for pixel art; see any 'world batch restarted' line above for who.",
                filtered == 0 ? LogLevel.Info : LogLevel.Warn);
            _flushedWith.Clear();
            _answered.Clear();
        }

        /// <summary>Answer the pending question from the frame just drawn. Called once the world
        /// (sprites and front layers) is on screen and the list is closed.</summary>
        internal static void AnswerPendingQuestion(IMonitor monitor)
        {
            if (_askedPoint is not Point point)
                return;
            _askedPoint = null;
            if (!HarmonyPatcher.DrawHooksInstalled)
            {
                monitor.Log("radiance_drawsat: the draw hooks are off (radiance_hooks on puts them back), "
                          + "so nothing was recorded to answer with.", LogLevel.Warn);
                return;
            }
            monitor.Log($"=== what drew screen pixel ({point.X},{point.Y}) ===", LogLevel.Info);
            monitor.Log($"{_records.Count} draws recorded this frame ({SortedCount} sprites, then the map's front layers). "
                      + "Listed back to front, so the last line is the one on top.", LogLevel.Info);
            int hits = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                Record record = _records[i];
                Rectangle box = FootprintOf(record);
                if (!box.Contains(point))
                    continue;
                hits++;
                if (record.Texture != null)
                    _answered.Add(record.Texture);
                float perSourcePixel = record.Source.Width > 0 ? box.Width / (float)record.Source.Width : 0f;
                string sheet = string.IsNullOrEmpty(record.Texture?.Name) ? "(unnamed texture)" : record.Texture!.Name;
                string kind = record.Texture is RenderTarget2D ? " [composed on the card, not a file]" : "";
                string layer = i < SortedCount ? "sprite" : "map front layer";
                string alpha = AlphaAt(record, point);
                monitor.Log($"  {layer}: {sheet}{kind}", LogLevel.Info);
                monitor.Log($"      sheet {record.Texture?.Width}x{record.Texture?.Height}, source {record.Source.Width}x{record.Source.Height} at {record.Source.X},{record.Source.Y}"
                          + $" -> {box.Width}x{box.Height} on screen at {box.X},{box.Y}", LogLevel.Info);
                monitor.Log($"      {perSourcePixel:0.###} screen pixels per source pixel"
                          + (Math.Abs(perSourcePixel - 4f) < 0.001f ? " (4 is the game's own pixel art)" : "  <-- NOT the game's 4")
                          + $", opacity {record.Alpha:0.##}, depth {record.Depth:0.####}"
                          + (Math.Abs(record.Rotation) > 0.0001f ? $", rotated {record.Rotation:0.###} rad (the box above ignores the turn)" : "")
                          + alpha, LogLevel.Info);
            }
            if (hits == 0)
                monitor.Log("  nothing the game drew into its sorted world batch covers that pixel. "
                          + "The map's BACK layers are drawn before the batch and are not on this list, "
                          + "so bare ground answers nothing here.", LogLevel.Info);
        }

        /// <summary>Where a recorded draw landed on screen. A destination draw carries its own box;
        /// a position draw is placed by its origin and scale.</summary>
        private static Rectangle FootprintOf(in Record record)
        {
            if (record.UsesDestination)
                return record.Destination;
            float width = record.Source.Width * Math.Abs(record.Scale.X);
            float height = record.Source.Height * Math.Abs(record.Scale.Y);
            float left = record.Position.X - record.Origin.X * record.Scale.X;
            float top = record.Position.Y - record.Origin.Y * record.Scale.Y;
            return new Rectangle((int)Math.Floor(left), (int)Math.Floor(top),
                                 (int)Math.Ceiling(width), (int)Math.Ceiling(height));
        }

        /// <summary>The sheet's own pixel under the asked-for point, so a draw whose quad covers the
        /// point but is transparent there says so rather than reading as the answer.</summary>
        private static string AlphaAt(in Record record, Point point)
        {
            try
            {
                Rectangle box = FootprintOf(record);
                if (record.Texture == null || record.Texture.IsDisposed || box.Width <= 0 || box.Height <= 0)
                    return "";
                int sx = record.Source.X + (int)((point.X - box.X) / (float)box.Width * record.Source.Width);
                int sy = record.Source.Y + (int)((point.Y - box.Y) / (float)box.Height * record.Source.Height);
                if ((record.Effects & SpriteEffects.FlipHorizontally) != 0)
                    sx = record.Source.X + record.Source.Right - 1 - sx;
                if (sx < 0 || sy < 0 || sx >= record.Texture.Width || sy >= record.Texture.Height)
                    return "";
                var one = new Color[1];
                record.Texture.GetData(0, new Rectangle(sx, sy, 1, 1), one, 0, 1);
                return one[0].A < 8 ? ", TRANSPARENT at that pixel" : $", the sheet's pixel there is {one[0].R},{one[0].G},{one[0].B}";
            }
            catch
            {
                // A texture the card will not read back says nothing, which is not an error.
                return "";
            }
        }
        /// <summary>Where the sorted step's own draws end and the map's FRONT layers begin. The
        /// game draws the front layers after the sprites and over them, so they are recorded in
        /// the same list but replayed as a second pass on top (see <see cref="Replay"/>).</summary>
        internal static int SortedCount { get; private set; }

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            // Re-entered by radiance_hooks on, after an off: count the overloads afresh rather
            // than reporting six.
            PatchedOverloads = 0;
            _monitor = monitor;
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
            InWorldStep = true;
            if (_flushWatchFrames > 0 && --_flushWatchFrames == 0)
                PrintFlushTable();
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
            InWorldStep = false;
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
