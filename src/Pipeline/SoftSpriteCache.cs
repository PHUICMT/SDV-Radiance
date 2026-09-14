using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The soft look's sprites, each baked from its own source rectangle alone, kept on shared
    /// pages of two thousand texels a side.
    ///
    /// <para>The soft look was first baked per SHEET, like the doubling, and every sprite came
    /// out with a faint dark frame around its cell: the kernel reads two pixels past a texel and
    /// the tent one texel past it, and on a sheet where the cells touch (the grass, the trees,
    /// the tile sheets) those pixels belong to the next sprite. A dark neighbour became a dark
    /// edge on a sprite that has none. So each bake reads its own rectangle and nothing outside
    /// it (the shader clamps every read to the rectangle).</para>
    ///
    /// <para>Then each sprite was kept as a target of its own, and that had a price nobody's timer
    /// could see: MonoGame's batcher ends a draw call every time the texture changes between two
    /// consecutive sprites, and the game sorts its world front to back, so a sprite with a texture
    /// of its own is a draw call of its own. Measured on a 3440-wide window at 75 % zoom on the
    /// farm, 4,859 draw calls in the world step against 2,772 with one texture per sheet, and two
    /// milliseconds a frame between them. So the sprites now share PAGES: a sprite is baked into
    /// a rectangle of whichever page has room, and consecutive sprites draw from one texture again
    /// whatever sheet they came from, which is fewer texture changes than the game's own sheets
    /// give it. The bake is the same bake; only where it lands changed. (Pages sized to each
    /// sheet were tried first and held 180 MB for one farm, since a big sheet's page is sixteen
    /// megabytes whether three sprites or three hundred are on it; shared pages hold the same
    /// farm in two.)</para>
    ///
    /// <para>Each sprite sits inside a two-texel gutter that repeats its edge texels, so the
    /// linear read the soft look draws with (see SheetUpscaler, _linearForSoftSheets) sees at the
    /// sprite's edge exactly what a clamped target of its own showed it: its own edge, never the
    /// neighbour and never the page's transparent ground. A sprite that fills its cell (grass,
    /// a bush, a floor tile) would otherwise wear a hairline seam.</para>
    ///
    /// <para>The key is the sheet instance and the rectangle. A sheet reloaded by a content patch
    /// is a new instance; its sprites are forgotten when the old one is disposed, the same rule
    /// the doubled sheets follow, and their room on the page is given back only when the page
    /// goes. Pages are dropped whole, least recently drawn first, when the budget is reached, or
    /// when nothing drawn is left on them; the sprites on a dropped page are baked again as they
    /// are drawn.</para>
    /// </summary>
    internal sealed class SoftSpriteCache
    {
        /// <summary>One page of sprites, filled shelf by shelf, never reused in place.</summary>
        private sealed class Page
        {
            public RenderTarget2D Target = null!;
            public readonly List<Shelf> Shelves = new();
            public readonly List<(Texture2D sheet, Rectangle rect, int variant)> Keys = new();
            public int NextShelfY;
            public int LastUsedTick;
            public long Bytes;
        }

        /// <summary>A row of the page: sprites of about one height, placed left to right.</summary>
        private sealed class Shelf
        {
            public int Y, Height, NextX;
        }

        private readonly struct Entry
        {
            public Entry(Page page, Rectangle rect) { Page = page; Rect = rect; }
            public readonly Page Page;
            /// <summary>The sprite's own texels on the page, without the gutter.</summary>
            public readonly Rectangle Rect;
        }

        /// <summary>Texels of the page repeated round every sprite, for the linear read.</summary>
        internal const int Gutter = 2;
        /// <summary>A page is a thousand texels a side, four megabytes, and a sprite too big for
        /// one gets a page of twice that. A page costs its whole size whether three sprites or
        /// three hundred are on it, so the smaller page is what keeps the last one, always
        /// half empty, from costing sixteen megabytes: measured over ten locations, 144 MB of
        /// pages at the larger size against the 60-odd megabytes the sprites themselves need.</summary>
        private const int PageSide = 1024;
        private const int LargePageSide = 2048;

        private readonly Dictionary<(Texture2D sheet, Rectangle rect, int variant), Entry> _entries = new();
        private readonly List<Page> _pages = new();
        private readonly HashSet<Texture2D> _ownTargets = new();
        private readonly List<Page> _sweepScratch = new();
        private readonly string _bucket;
        private readonly long _budgetBytes;
        private readonly int _largestSpriteSide;
        private readonly int _generatePerFrameCap;
        private readonly int _scale;
        private readonly Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, Rectangle, RenderTarget2D, Rectangle, bool> _bake;
        private long _heldBytes;
        private int _generatedThisFrame, _frameTick = -1;
        private SpriteBatch? _spriteBatch;

        internal SoftSpriteCache(string bucket, long budgetBytes, int largestSpriteSide, int scale, int generatePerFrameCap,
            Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, Rectangle, RenderTarget2D, Rectangle, bool> bake)
        {
            _bucket = bucket;
            _budgetBytes = budgetBytes;
            _largestSpriteSide = largestSpriteSide;
            _scale = scale;
            _generatePerFrameCap = generatePerFrameCap;
            _bake = bake;
        }

        internal int Count => _entries.Count;
        internal int PageCount => _pages.Count;
        internal int Scale => _scale;
        internal int Refused { get; private set; }
        internal int Evicted { get; private set; }
        internal int Generated { get; private set; }
        /// <summary>A sprite bigger than this in either direction never gets a soft copy.</summary>
        internal int LargestSpriteSide => _largestSpriteSide;
        /// <summary>How many may be baked in one frame; the rest wait, drawn sharp meanwhile.</summary>
        internal int GeneratePerFrameCap => _generatePerFrameCap;

        internal bool IsOwnOutput(Texture2D texture) => _ownTargets.Contains(texture);

        /// <summary>The page holding the soft sprite for this rectangle of this sheet, and where
        /// on the page it sits, made if the frame's cap and the budget allow. False means "draw
        /// the sheet as it is this frame".</summary>
        internal bool TryGet(GraphicsDevice device, Effect effect, Texture2D sheet, Rectangle rect, int variant,
            out Texture2D page, out Rectangle placed)
        {
            page = null!;
            placed = default;
            if (sheet.IsDisposed || _ownTargets.Contains(sheet))
                return false;
            var key = (sheet, rect, variant);
            if (_entries.TryGetValue(key, out Entry entry))
            {
                if (entry.Page.Target.IsDisposed)
                {
                    DropPage(entry.Page);
                }
                else
                {
                    entry.Page.LastUsedTick = SharedTicks.Now;
                    page = entry.Page.Target;
                    placed = entry.Rect;
                    return true;
                }
            }
            if (_frameTick != SharedTicks.Now)
            {
                _frameTick = SharedTicks.Now;
                _generatedThisFrame = 0;
            }
            if (_generatedThisFrame >= _generatePerFrameCap)
                return false;
            if (rect.Width <= 0 || rect.Height <= 0 || rect.Width > _largestSpriteSide || rect.Height > _largestSpriteSide)
            {
                Refused++;
                return false;
            }
            int paddedWidth = rect.Width * _scale + 2 * Gutter, paddedHeight = rect.Height * _scale + 2 * Gutter;
            if (paddedWidth > LargePageSide || paddedHeight > LargePageSide)
            {
                Refused++;
                return false;
            }
            Page? home = null;
            Rectangle paddedRect = default;
            foreach (Page candidate in _pages)
            {
                if (TryPlace(candidate, paddedWidth, paddedHeight, out paddedRect))
                {
                    home = candidate;
                    break;
                }
            }
            if (home == null)
            {
                home = OpenPage(device, paddedWidth, paddedHeight);
                if (home == null || !TryPlace(home, paddedWidth, paddedHeight, out paddedRect))
                {
                    Refused++;
                    return false;
                }
            }
            _spriteBatch ??= new SpriteBatch(device);
            RenderTargetBinding[] previous = device.GetRenderTargets();
            bool made;
            try
            {
                made = _bake(device, _spriteBatch, effect, sheet, rect, home.Target, paddedRect);
            }
            catch
            {
                made = false;
            }
            finally
            {
                if (previous.Length > 0) device.SetRenderTargets(previous);
                else device.SetRenderTarget(null);
            }
            if (!made)
            {
                // The rectangle stays claimed and blank; a failed bake is not tried again this frame.
                Refused++;
                return false;
            }
            var inner = new Rectangle(paddedRect.X + Gutter, paddedRect.Y + Gutter, rect.Width * _scale, rect.Height * _scale);
            _entries[key] = new Entry(home, inner);
            home.Keys.Add(key);
            home.LastUsedTick = SharedTicks.Now;
            _generatedThisFrame++;
            Generated++;
            page = home.Target;
            placed = inner;
            return true;
        }

        /// <summary>A rectangle of the page for a sprite this size, on a shelf of about its
        /// height or on a new shelf below the last; false when the page is full.</summary>
        private static bool TryPlace(Page page, int width, int height, out Rectangle rect)
        {
            rect = default;
            int pageWidth = page.Target.Width, pageHeight = page.Target.Height;
            if (width > pageWidth || height > pageHeight)
                return false;
            foreach (Shelf shelf in page.Shelves)
            {
                // A shelf takes sprites no taller than it and at least half its height, so the
                // tall shelf a tree opened does not fill up with grass at a quarter of the space.
                if (height > shelf.Height || height * 2 < shelf.Height || shelf.NextX + width > pageWidth)
                    continue;
                rect = new Rectangle(shelf.NextX, shelf.Y, width, height);
                shelf.NextX += width;
                return true;
            }
            if (page.NextShelfY + height > pageHeight)
                return false;
            var opened = new Shelf { Y = page.NextShelfY, Height = height, NextX = width };
            page.Shelves.Add(opened);
            page.NextShelfY += height;
            rect = new Rectangle(0, opened.Y, width, height);
            return true;
        }

        /// <summary>A new page, cleared to transparent, the small size unless this sprite needs
        /// the big one. Drops the least recently drawn pages first if the budget needs it.</summary>
        private Page? OpenPage(GraphicsDevice device, int atLeastWidth, int atLeastHeight)
        {
            int side = atLeastWidth > PageSide || atLeastHeight > PageSide ? LargePageSide : PageSide;
            int width = side, height = side;
            long bytes = (long)width * height * 4;
            EvictToFit(bytes);
            if (_heldBytes + bytes > _budgetBytes)
                return null;
            RenderTarget2D target;
            try
            {
                // PreserveContents: the page is read for as long as its sprites are drawn and
                // written again for every sprite that joins it - a cross-frame target, rule 7.
                target = VramTally.Track(new RenderTarget2D(device, width, height, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), _bucket);
            }
            catch
            {
                return null;
            }
            RenderTargetBinding[] previous = device.GetRenderTargets();
            try
            {
                device.SetRenderTarget(target);
                device.Clear(Color.Transparent);
            }
            finally
            {
                if (previous.Length > 0) device.SetRenderTargets(previous);
                else device.SetRenderTarget(null);
            }
            var page = new Page { Target = target, Bytes = bytes, LastUsedTick = SharedTicks.Now };
            _pages.Add(page);
            _ownTargets.Add(target);
            _heldBytes += bytes;
            return page;
        }

        /// <summary>Forget every sprite whose sheet has been disposed (a content patch reloaded
        /// it), and drop a page that has lost its target or its last sprite.</summary>
        internal void SweepDisposed()
        {
            _sweepScratch.Clear();
            foreach (Page page in _pages)
            {
                for (int i = page.Keys.Count - 1; i >= 0; i--)
                {
                    if (!page.Keys[i].sheet.IsDisposed)
                        continue;
                    _entries.Remove(page.Keys[i]);
                    page.Keys.RemoveAt(i);
                    Evicted++;
                }
                if (page.Target.IsDisposed || page.Keys.Count == 0)
                    _sweepScratch.Add(page);
            }
            foreach (Page page in _sweepScratch)
                DropPage(page);
        }

        private void DropPage(Page page)
        {
            foreach (var key in page.Keys)
                _entries.Remove(key);
            Evicted += page.Keys.Count;
            page.Keys.Clear();
            if (!page.Target.IsDisposed)
                page.Target.Dispose();
            _ownTargets.Remove(page.Target);
            _heldBytes -= page.Bytes;
            _pages.Remove(page);
        }

        /// <summary>Drop the least recently drawn pages until <paramref name="incoming"/> fits.</summary>
        private void EvictToFit(long incoming)
        {
            while (_heldBytes + incoming > _budgetBytes && _pages.Count > 0)
            {
                Page? oldest = null;
                foreach (Page page in _pages)
                    if (oldest == null || page.LastUsedTick < oldest.LastUsedTick)
                        oldest = page;
                DropPage(oldest!);
            }
        }

        internal void Clear()
        {
            foreach (Page page in _pages)
                if (!page.Target.IsDisposed)
                    page.Target.Dispose();
            _pages.Clear();
            _entries.Clear();
            _ownTargets.Clear();
            _heldBytes = 0;
        }

        internal void Dispose()
        {
            Clear();
            _spriteBatch?.Dispose(); _spriteBatch = null;
        }

        internal string Describe()
            => $"{_bucket}: {_entries.Count} sprites on {_pages.Count} pages, {_heldBytes / (1024.0 * 1024.0):F1} MB held, {Generated} made, {Evicted} evicted, {Refused} refused";
    }
}
