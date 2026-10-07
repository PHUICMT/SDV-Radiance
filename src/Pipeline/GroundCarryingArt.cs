using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Sprites whose art is opaque all the way round their source rectangle: art that carries its
    /// own ground or wall with it and is laid over the map, the way the Mountain landslide, the
    /// rocks over the railroad and the Joja front over the Community Center are.
    /// </summary>
    /// <remarks>
    /// <para>The relief reads a sprite's bevel off its alpha edge, from a normal map made of the
    /// whole sheet at once (normals.fx). For an ordinary sprite the edge is its silhouette. For one
    /// of these the art never ends inside the rectangle, so the only edge the bake finds is the
    /// rectangle itself, read against whatever sits beside it on the sheet: empty sheet in Cursors.
    /// The landslide wore a raised box exactly the size of its rectangle, and the whole mod switched
    /// off, or only the relief, made it go.</para>
    /// <para>So such a rectangle is found once, and its stretch of the sheet's normal map is made
    /// again with the neighbours it may read pulled in to the rectangle itself. The painted
    /// shading inside it is kept; only the edge that was never there goes.</para>
    /// <para>Found on the update tick, never in the draw: deciding needs the sheet's pixels, and a
    /// readback in the draw waits for everything the card has been given. Only rectangles at least
    /// <see cref="SmallestSide"/> texels on each side are asked about, which leaves out the people,
    /// items and tiles that make up nearly every recorded sprite. A sheet another part of the mod
    /// already holds is answered from that copy; any other is read once per batch of new rectangles,
    /// one sheet per notice, and not kept.</para>
    /// </remarks>
    internal static class GroundCarryingArt
    {
        /// <summary>A rectangle narrower or shorter than this is not asked about. The art this is
        /// for is large (the landslide is 48 by 80 texels, the Joja front 174 by 50).</summary>
        private const int SmallestSide = 24;

        /// <summary>How often the last frame's sprites are looked through for new rectangles.</summary>
        private const int NoticePeriodTicks = 10;

        /// <summary>Every rectangle asked about so far, per sheet, and the answer. Weak on the sheet,
        /// so a sheet a content patch replaces takes its answers with it.</summary>
        private static readonly ConditionalWeakTable<Texture2D, Dictionary<Rectangle, bool>> _answers = [];

        /// <summary>The rectangles that carry their own ground, per sheet: what the draw reads, and
        /// empty for nearly every sheet.</summary>
        private static readonly ConditionalWeakTable<Texture2D, List<Rectangle>> _carrying = [];

        /// <summary>How long a sheet that has to be read gathers rectangles first. An animated
        /// sprite shows a new frame every few hundred milliseconds, and reading its sheet the moment
        /// the first frame was seen read it again for every frame after: 232 reads over four maps.
        /// Three seconds sees a walk or a sway through.</summary>
        private const int GatherTicksBeforeRead = 180;

        /// <summary>When each waiting sheet was first seen with a rectangle not yet decided.</summary>
        private static readonly Dictionary<Texture2D, int> _waitingSince = new(ReferenceEqualityComparer.Instance);

        /// <summary>Rectangles seen but not yet decided, per sheet.</summary>
        private static readonly Dictionary<Texture2D, HashSet<Rectangle>> _waiting = new(ReferenceEqualityComparer.Instance);

        /// <summary>Which rectangles have been made again on each derived normal map. Weak on the
        /// map: a map the cache evicts or rebuilds starts with none, and they are made again on it.</summary>
        private static readonly ConditionalWeakTable<Texture2D, HashSet<Rectangle>> _rederivedOn = [];

        /// <summary>radiance_groundart off: every normal map stays as the whole-sheet bake made it,
        /// for the A/B.</summary>
        internal static bool Enabled = true;

        internal static int SheetsRead;
        internal static int RectanglesFound;

        /// <summary>Look through the sprites the last frame drew for rectangles not asked about yet,
        /// and decide one sheet's worth. On the update tick.</summary>
        internal static void Notice()
        {
            if (!Enabled || Game1.ticks % NoticePeriodTicks != 0)
                return;
            IReadOnlyList<SpriteDrawRecorder.Record> records = SpriteDrawRecorder.Records;
            for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                SpriteDrawRecorder.Record record = records[recordIndex];
                Rectangle source = record.Source;
                if (source.Width < SmallestSide || source.Height < SmallestSide)
                    continue;
                Texture2D sheet = record.Texture;
                if (sheet is RenderTarget2D || sheet.IsDisposed)
                    continue;
                if (_answers.TryGetValue(sheet, out Dictionary<Rectangle, bool>? known) && known.ContainsKey(source))
                    continue;
                if (!_waiting.TryGetValue(sheet, out HashSet<Rectangle>? waiting))
                {
                    _waiting[sheet] = waiting = [];
                    _waitingSince[sheet] = Game1.ticks;
                }
                waiting.Add(source);
            }
            // Every sheet another part of the mod already holds is decided now, for nothing; of the
            // rest, one is read.
            _decidedScratch.Clear();
            Texture2D? toRead = null;
            foreach (KeyValuePair<Texture2D, HashSet<Rectangle>> pair in _waiting)
            {
                Color[]? held = SheetPixels.IfHeld(pair.Key);
                if (held != null)
                {
                    Decide(pair.Key, pair.Value, held);
                    _decidedScratch.Add(pair.Key);
                }
                else if (toRead == null && Game1.ticks - _waitingSince[pair.Key] >= GatherTicksBeforeRead)
                {
                    toRead = pair.Key;
                }
            }
            if (toRead != null)
            {
                Color[]? pixels = toRead.IsDisposed ? null : SheetReadback.Read(toRead, SheetPixels.PixelCap, "sheet: art carrying its own ground");
                SheetsRead++;
                Decide(toRead, _waiting[toRead], pixels);
                _decidedScratch.Add(toRead);
            }
            foreach (Texture2D decided in _decidedScratch)
            {
                _waiting.Remove(decided);
                _waitingSince.Remove(decided);
            }
        }

        private static readonly List<Texture2D> _decidedScratch = [];

        /// <summary>Forget the answers for every sheet a content patch reloaded in place: its art,
        /// and so its edges, may be different now. Its normal maps are dropped by their own cache.</summary>
        internal static int ForgetReloaded()
        {
            var reloaded = new List<Texture2D>();
            foreach (KeyValuePair<Texture2D, Dictionary<Rectangle, bool>> pair in _answers)
                if (ArtReloads.WasReloaded(pair.Key))
                    reloaded.Add(pair.Key);
            foreach (Texture2D sheet in reloaded)
            {
                _answers.Remove(sheet);
                _carrying.Remove(sheet);
            }
            return reloaded.Count;
        }

        private static void Decide(Texture2D sheet, HashSet<Rectangle> rectangles, Color[]? pixels)
        {
            Dictionary<Rectangle, bool> answers = _answers.GetValue(sheet, _ => []);
            foreach (Rectangle rectangle in rectangles)
            {
                bool carries = pixels != null && OpaqueAllRound(pixels, sheet.Width, sheet.Height, rectangle);
                answers[rectangle] = carries;
                if (!carries)
                    continue;
                _carrying.GetValue(sheet, _ => []).Add(rectangle);
                RectanglesFound++;
            }
        }

        /// <summary>Whether every texel on the rectangle's four edges is fully opaque.</summary>
        private static bool OpaqueAllRound(Color[] pixels, int sheetWidth, int sheetHeight, Rectangle rectangle)
        {
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Right > sheetWidth || rectangle.Bottom > sheetHeight)
                return false;
            int top = rectangle.Y * sheetWidth, bottom = (rectangle.Bottom - 1) * sheetWidth;
            for (int x = rectangle.X; x < rectangle.Right; x++)
                if (pixels[top + x].A != 255 || pixels[bottom + x].A != 255)
                    return false;
            for (int y = rectangle.Y; y < rectangle.Bottom; y++)
                if (pixels[y * sheetWidth + rectangle.X].A != 255 || pixels[y * sheetWidth + rectangle.Right - 1].A != 255)
                    return false;
            return true;
        }

        /// <summary>
        /// Make sure every ground-carrying rectangle of <paramref name="sheet"/> has been made again
        /// on <paramref name="normalMap"/>, its derived map for <paramref name="variant"/>. Called
        /// in the draw for each sprite, so the common answer, a sheet with none, is one lookup.
        /// </summary>
        internal static void Rederive(GraphicsDevice device, Effect normals, SheetDerivedCache cache,
            Texture2D sheet, int variant, Texture2D normalMap)
        {
            if (!Enabled || !_carrying.TryGetValue(sheet, out List<Rectangle>? rectangles))
                return;
            HashSet<Rectangle> done = _rederivedOn.GetValue(normalMap, _ => []);
            if (done.Count == rectangles.Count)
                return;
            foreach (Rectangle rectangle in rectangles)
            {
                if (done.Contains(rectangle))
                    continue;
                var bounds = new Vector4(
                    (rectangle.X + 0.5f) / sheet.Width, (rectangle.Y + 0.5f) / sheet.Height,
                    (rectangle.Right - 0.5f) / sheet.Width, (rectangle.Bottom - 0.5f) / sheet.Height);
                if (cache.Rederive(device, normals, sheet, variant, rectangle,
                        effect => effect.Parameters["NeighbourBounds"]?.SetValue(bounds),
                        effect => effect.Parameters["NeighbourBounds"]?.SetValue(new Vector4(0f, 0f, 1f, 1f))))
                    done.Add(rectangle);
            }
        }

        /// <summary>One line for radiance_report.</summary>
        internal static string Describe()
            => $"art carrying its own ground: {RectanglesFound} rectangle(s) found over {SheetsRead} sheet read(s){(Enabled ? "" : ", switched OFF")}";
    }
}
