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
    /// the doubled sheets follow, and their room on the page is kept as a free slot that the next
    /// sprite of exactly that size takes. That room used to be given back only when the page went,
    /// and a sheet that is rebuilt all the time filled the cache with copies of itself: a mod that
    /// recolours the farmer's pants every tick makes the game throw the farmer's body texture away
    /// and build a new one every frame, and measured in Town that was 150 sprites a second swept
    /// and all 47 pages of the budget held, mostly by farmers nobody could draw any more. The new
    /// copy is the same size as the one it replaces, so it now lands in the old one's slot. Pages
    /// are dropped whole, least recently drawn first, when the budget is reached, or when nothing
    /// drawn is left on them; the sprites on a dropped page are baked again as they are drawn.</para>
    /// </summary>
    internal sealed class SoftSpriteCache
    {
        /// <summary>One page of sprites, filled shelf by shelf, and refilled slot by slot as
        /// sprites whose sheet has gone are swept off it.</summary>
        private sealed class Page
        {
            public RenderTarget2D Target = null!;
            public readonly List<Shelf> Shelves = [];
            /// <summary>Room a swept sprite left, gutter included. The bake writes its whole
            /// rectangle opaque, so a slot needs no clearing before it is used again.</summary>
            public readonly List<Rectangle> FreeSlots = [];
            public readonly List<(Texture2D sheet, Rectangle rect, int variant)> Keys = [];
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
            public Entry(Page page, Rectangle rect, int bakedTick) { Page = page; Rect = rect; BakedTick = bakedTick; }
            public readonly Page Page;
            /// <summary>The sprite's own texels on the page, without the gutter.</summary>
            public readonly Rectangle Rect;
            /// <summary>The tick the bake was made on, for <see cref="RebakeAdoptedAfterTicks"/>.</summary>
            public readonly int BakedTick;
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

        private readonly Dictionary<(Texture2D sheet, Rectangle rect, int variant), Entry> _entries = [];

        /// <summary>The sheet instance that currently owns the bake for a NAMED rectangle, so a
        /// sheet the game throws away and builds again can pick its own bake back up.
        ///
        /// <para>Measured in Town on 2026-09-22: 52,438 of 52,438 evictions were one texture,
        /// <c>@FarmerRenderer.baseTexture</c>, which the game disposes and rebuilds every frame.
        /// That is 99 per cent of every bake this cache did, and at eight bakes a frame it was
        /// taking 4.3 of them, so the trees and bushes of a screen the player had just walked into
        /// were sharing what was left and turning soft at half speed. The pixels were never the
        /// problem: a farmer whose shirt has not changed bakes to exactly the same picture. Only
        /// the KEY had gone, because the key is the texture instance.</para>
        ///
        /// <para>So a bake now outlives the instance that asked for it. The next instance with the
        /// same name and the same rectangle adopts it, and the bake is only made again once it is
        /// <see cref="RebakeAdoptedAfterTicks"/> old, which bounds how stale a changed shirt can
        /// look at half a second and turns sixty bakes a second into two.</para></summary>
        private readonly Dictionary<(string name, Rectangle rect, int variant), Texture2D> _bakedByName = [];
        /// <inheritdoc cref="_bakedByName"/>
        private const int RebakeAdoptedAfterTicks = 30;
        private readonly List<Page> _pages = [];
        private readonly HashSet<Texture2D> _ownTargets = [];
        private readonly List<Page> _sweepScratch = [];
        private readonly string _bucket;
        private readonly long _budgetBytes;
        private readonly int _largestSpriteSide;
        private int _generatePerFrameCap;
        private readonly int _scale;
        private readonly Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, Rectangle, RenderTarget2D, Rectangle, bool> _bake;
        private long _heldBytes;
        private int _generatedThisFrame, _frameTick = -1;
        private SpriteBatch? _spriteBatch;
        private int _burstFramesLeft;
        private bool _burstThisFrame;

        /// <summary>
        /// Let the next few frames bake every sprite they draw, cap or no cap.
        /// </summary>
        /// <remarks>The rule the doubled sheets follow (SheetDerivedCache.AllowBurstThisTick), which
        /// this cache never had. At eight a frame a screen of a few hundred sprites turned soft one
        /// sprite at a time after a warp, each switching from sharp to soft on its own frame, along
        /// every edge: the map rendering in as you enter it. On a warp the game's fade-to-black is
        /// over the picture, so the long frames go where nobody sees them.</remarks>
        internal void AllowBurstThisTick()
        {
            _burstFramesLeft = SheetDerivedCache.BurstFramesOnArrival;
            _burstFramesUsed = 0;
        }

        /// <summary>The most frames a burst may keep itself alive for. A location's first view is a
        /// few hundred sprites; this is the ceiling that stops a pathological map holding the fade
        /// open, not a number anything normally reaches.</summary>
        private const int MostBurstFrames = 120;
        private int _burstFramesUsed;
        /// <summary>The longest burst since the last report, for the line that says whether a
        /// location now arrives finished.</summary>
        internal int BurstFramesTaken { get; private set; }

        /// <summary>Start the counters that the report calls "since the last report" over again.
        /// They used to run from the moment the game started while saying otherwise, which is how a
        /// reading taken to measure a change came back with the same number as the one before it
        /// and looked like the change had done nothing.</summary>
        internal void CountersReported()
        {
            Generated = 0;
            Evicted = 0;
            EvictedByBudget = 0;
            EvictedBySweep = 0;
            EvictedByDeadPage = 0;
            Adopted = 0;
            Refused = 0;
            Capped = 0;
            LargestFrame = 0;
            WorstBakeMilliseconds = 0.0;
            BakeMillisecondsSinceReport = 0.0;
            BurstFramesTaken = 0;
            _sweptSheets.Clear();
            _cappedSheets.Clear();
        }

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
        /// <summary>The three ways a sprite leaves, counted apart, because they mean opposite
        /// things. BUDGET is the cache being too small for the scene. SWEPT is a sheet the game or
        /// another mod threw away and built again, which costs a bake a frame forever and never
        /// settles. DEAD PAGE is our own target being lost under us. Until these were told apart,
        /// a cache holding 20 MB of a 192 MB budget was reporting seventy thousand evictions and
        /// reading like a cache that needed more room.</summary>
        internal int EvictedByBudget { get; private set; }
        /// <inheritdoc cref="EvictedByBudget"/>
        internal int EvictedBySweep { get; private set; }
        /// <inheritdoc cref="EvictedByBudget"/>
        internal int EvictedByDeadPage { get; private set; }
        /// <summary>Which sheets the sweep keeps taking away, so the one that is being rebuilt
        /// every frame can be named instead of guessed at.</summary>
        private readonly Dictionary<string, int> _sweptSheets = [];
        /// <summary>The sheets the sweep has taken most, worst first.</summary>
        internal string DescribeSweptSheets()
        {
            if (_sweptSheets.Count == 0)
                return "none";
            var worst = new List<KeyValuePair<string, int>>(_sweptSheets);
            worst.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < worst.Count && i < 5; i++)
                parts.Add($"{worst[i].Key}:{worst[i].Value}");
            return string.Join(" · ", parts);
        }
        private void NoteSwept(Texture2D sheet)
        {
            string name;
            try
            {
                name = string.IsNullOrEmpty(sheet.Name) ? $"(unnamed {sheet.Width}x{sheet.Height})" : sheet.Name;
            }
            catch (ObjectDisposedException)
            {
                name = "(disposed, no name left)";
            }
            _sweptSheets[name] = _sweptSheets.TryGetValue(name, out int seen) ? seen + 1 : 1;
        }
        internal int Generated { get; private set; }
        /// <summary>The most sprites baked in one frame so far: above the cap only on a burst.</summary>
        internal int LargestFrame { get; private set; }
        /// <summary>A sprite bigger than this in either direction never gets a soft copy.</summary>
        internal int LargestSpriteSide => _largestSpriteSide;
        /// <summary>How many may be baked in one frame; the rest wait, drawn sharp meanwhile.</summary>
        internal int GeneratePerFrameCap
        {
            get => _generatePerFrameCap;
            set => _generatePerFrameCap = Math.Clamp(value, 1, 512);
        }

        internal bool IsOwnOutput(Texture2D texture) => _ownTargets.Contains(texture);

        /// <summary>Whether every held sprite has a slot of its own: no two entries overlapping on a
        /// page, every entry listed on its page and every listed key held. radiance_softcheck.</summary>
        internal string CheckSlots()
        {
            int overlaps = 0, unlisted = 0, orphaned = 0;
            var examples = new List<string>();
            var byPage = new Dictionary<Page, List<((Texture2D sheet, Rectangle rect, int variant) key, Rectangle rect)>>();
            foreach (var pair in _entries)
            {
                if (!byPage.TryGetValue(pair.Value.Page, out var list))
                    byPage[pair.Value.Page] = list = [];
                list.Add((pair.Key, pair.Value.Rect));
                if (!pair.Value.Page.Keys.Contains(pair.Key))
                    unlisted++;
            }
            foreach (Page page in _pages)
                foreach (var key in page.Keys)
                    if (!_entries.TryGetValue(key, out Entry entry) || !ReferenceEquals(entry.Page, page))
                        orphaned++;
            foreach (var pair in byPage)
            {
                var list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        Rectangle first = list[i].rect, second = list[j].rect;
                        first.Inflate(Gutter, Gutter);
                        second.Inflate(Gutter, Gutter);
                        if (!first.Intersects(second))
                            continue;
                        overlaps++;
                        if (examples.Count < 6)
                            examples.Add($"{list[i].key.sheet.Name} {list[i].key.rect} v{list[i].key.variant} at {list[i].rect} and "
                                       + $"{list[j].key.sheet.Name} {list[j].key.rect} v{list[j].key.variant} at {list[j].rect}");
                    }
            }
            return $"{_entries.Count} entries on {_pages.Count} pages: {overlaps} overlapping pair(s), {unlisted} entry(ies) not on their page's list, "
                 + $"{orphaned} listed key(s) with no entry there" + (examples.Count > 0 ? "; e.g. " + string.Join(" | ", examples) : "");
        }

        /// <summary>Which page this is, for radiance_softpages and the drawsat watch; -1 if none.</summary>
        internal int PageIndexOf(Texture2D texture) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(texture);

        /// <summary>Every page written to a PNG, for radiance_softpages. Reads the pages back, so it
        /// runs from the console, between frames, never inside a draw.</summary>
        internal int SavePages(string folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            for (int i = 0; i < _pages.Count; i++)
            {
                RenderTarget2D target = _pages[i].Target;
                if (target.IsDisposed)
                    continue;
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"soft-page-{PageIndexOf(target)}.png"));
                target.SaveAsPng(file, target.Width, target.Height);
            }
            return _pages.Count;
        }

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
                    DropPage(entry.Page, PageDropReason.DeadTarget);
                }
                else
                {
                    entry.Page.LastUsedTick = SharedTicks.Now;
                    page = entry.Page.Target;
                    placed = entry.Rect;
                    return true;
                }
            }
            if (TryAdopt(sheet, rect, variant, key, out page, out placed))
                return true;
            if (_frameTick != SharedTicks.Now)
            {
                _frameTick = SharedTicks.Now;
                bool burstFoundWork = _burstThisFrame && _generatedThisFrame > 0;
                _generatedThisFrame = 0;
                CappedLastFrame = _cappedThisFrame;
                _cappedThisFrame = 0;
                BakeMillisecondsLastFrame = _bakeMillisecondsThisFrame;
                WorstBakeMilliseconds = Math.Max(WorstBakeMilliseconds, _bakeMillisecondsThisFrame);
                _bakeMillisecondsThisFrame = 0.0;
                _burstThisFrame = _burstFramesLeft > 0;
                if (_burstThisFrame)
                    _burstFramesLeft--;
                // A burst that is still finding work has not finished the view it arrived on, and
                // three frames of a screen is three frames of it. Keep going while each burst frame
                // still bakes something, which ends by itself the frame nothing is left, so the
                // location arrives finished rather than filling in while the player walks into it.
                // The game's own fade-to-black is over the picture for every frame of this.
                if (burstFoundWork && _burstFramesUsed < MostBurstFrames)
                {
                    _burstThisFrame = true;
                    _burstFramesUsed++;
                }
                if (!_burstThisFrame)
                    BurstFramesTaken = Math.Max(BurstFramesTaken, _burstFramesUsed);
            }
            bool spent = _burstThisFrame
                ? _bakeMillisecondsThisFrame >= BurstBudgetMillisecondsPerFrame
                : _bakeMillisecondsThisFrame >= BakeBudgetMillisecondsPerFrame || _generatedThisFrame >= _generatePerFrameCap;
            if (spent)
            {
                NoteCapped(sheet);
                return false;
            }
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
            _bakeClock.Restart();
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
                _bakeClock.Stop();
                _bakeMillisecondsThisFrame += _bakeClock.Elapsed.TotalMilliseconds;
                BakeMillisecondsSinceReport += _bakeClock.Elapsed.TotalMilliseconds;
            }
            if (!made)
            {
                // The rectangle stays claimed and blank; a failed bake is not tried again this frame.
                Refused++;
                return false;
            }
            var inner = new Rectangle(paddedRect.X + Gutter, paddedRect.Y + Gutter, rect.Width * _scale, rect.Height * _scale);
            _entries[key] = new Entry(home, inner, SharedTicks.Now);
            if (!string.IsNullOrEmpty(sheet.Name))
                _bakedByName[(sheet.Name, rect, variant)] = sheet;
            home.Keys.Add(key);
            home.LastUsedTick = SharedTicks.Now;
            _generatedThisFrame++;
            Generated++;
            LargestFrame = Math.Max(LargestFrame, _generatedThisFrame);
            page = home.Target;
            placed = inner;
            return true;
        }

        /// <summary>Hand a bake made for a sheet the game has since thrown away to the sheet that
        /// replaced it, when the two share a name and the bake is still young enough to trust.
        /// The bake is not remade, not moved, and costs nothing but the two dictionary writes that
        /// change whose name is on it.</summary>
        private bool TryAdopt(Texture2D sheet, Rectangle rect, int variant,
            (Texture2D sheet, Rectangle rect, int variant) key, out Texture2D page, out Rectangle placed)
        {
            page = null!;
            placed = default;
            if (string.IsNullOrEmpty(sheet.Name))
                return false;
            var nameKey = (sheet.Name, rect, variant);
            if (!_bakedByName.TryGetValue(nameKey, out Texture2D? holder) || ReferenceEquals(holder, sheet))
                return false;
            var holderKey = (holder, rect, variant);
            if (!_entries.TryGetValue(holderKey, out Entry held))
            {
                _bakedByName.Remove(nameKey);
                return false;
            }
            // Only a sheet the game has actually let go of: while both are alive they are two
            // different pictures that happen to share a name, and each keeps its own bake.
            if (!holder.IsDisposed || held.Page.Target.IsDisposed
                || SharedTicks.Now - held.BakedTick > RebakeAdoptedAfterTicks)
                return false;
            _entries.Remove(holderKey);
            int at = held.Page.Keys.IndexOf(holderKey);
            if (at >= 0)
                held.Page.Keys[at] = key;
            else
                held.Page.Keys.Add(key);
            _entries[key] = held;
            _bakedByName[nameKey] = sheet;
            held.Page.LastUsedTick = SharedTicks.Now;
            Adopted++;
            page = held.Page.Target;
            placed = held.Rect;
            return true;
        }

        /// <summary>How many times a bake outlived the sheet that asked for it and was picked up
        /// by its replacement instead of being made again.</summary>
        internal int Adopted { get; private set; }

        /// <summary>Draws that wanted a soft copy, were entitled to one, and drew the game's own
        /// pixels anyway because the frame had already baked its <see cref="GeneratePerFrameCap"/>.
        ///
        /// <para>This is the only way a sprite can be sharp in one frame and soft in the next while
        /// nothing about it changed, which is what the author described: spots that are not smooth
        /// and do not stay in the same place. Every other refusal is stable - too big is always too
        /// big. Until this counter existed the cap was the one road out of TryGet that left no
        /// trace, so a screen full of sprites taking turns to be sharp looked like nothing at
        /// all.</para></summary>
        internal int Capped { get; private set; }
        /// <inheritdoc cref="Capped"/>
        internal int CappedLastFrame { get; private set; }
        private int _cappedThisFrame;

        /// <summary>How long a frame may spend making soft sprites, in milliseconds.
        ///
        /// <para>A count is the wrong unit for this. Eight bakes is eight tiny floor tiles or eight
        /// whole trees, and those are not the same amount of work on any machine; and a count that
        /// is safe on the author's card is either wasteful or a stutter on somebody else's. The
        /// budget is spent in real time instead, so a fast machine fills a new view in a few frames
        /// and a slow one takes longer without ever giving up a frame to it. Clear Glasses reaches
        /// the same answer from the other side: it estimates each resample against the time left in
        /// the frame rather than counting them.</para>
        ///
        /// <para><see cref="GeneratePerFrameCap"/> stays as a ceiling in case a bake is measured as
        /// free, which the driver can make it look like when it queues the work and returns.</para></summary>
        internal static double BakeBudgetMillisecondsPerFrame = 1.5;
        /// <summary>What a frame of an arrival's burst may spend. The burst used to have no limit at
        /// all and baked the whole view in its first frame: 250 to 285 ms measured on arriving at
        /// Custom_AdventurerSummit once map tiles were baked with their neighbours, one frozen frame
        /// under the fade. With a budget the same work spreads over twenty-odd frames of the fade,
        /// since the burst carries on for as long as each frame still finds something to bake.</summary>
        internal static double BurstBudgetMillisecondsPerFrame = 12.0;
        private readonly System.Diagnostics.Stopwatch _bakeClock = new();
        private double _bakeMillisecondsThisFrame;
        /// <summary>What the last frame actually spent making soft sprites.</summary>
        internal double BakeMillisecondsLastFrame { get; private set; }
        /// <summary>Every bake's time added up since the last report; over <see cref="Generated"/>
        /// it is what one bake costs, which a frame's total cannot say.</summary>
        internal double BakeMillisecondsSinceReport { get; private set; }
        /// <summary>The worst frame since the last report, for the stutter question.</summary>
        internal double WorstBakeMilliseconds { get; private set; }
        private readonly Dictionary<string, int> _cappedSheets = [];

        private void NoteCapped(Texture2D sheet)
        {
            Capped++;
            _cappedThisFrame++;
            string name = string.IsNullOrEmpty(sheet.Name) ? $"(unnamed {sheet.Width}x{sheet.Height})" : sheet.Name;
            if (_cappedSheets.Count < 64 || _cappedSheets.ContainsKey(name))
                _cappedSheets[name] = _cappedSheets.TryGetValue(name, out int seen) ? seen + 1 : 1;
        }

        /// <summary>The art that most often drew sharp because the frame's bakes were spent.</summary>
        internal string DescribeCappedSheets()
        {
            if (_cappedSheets.Count == 0)
                return "none";
            var worst = new List<KeyValuePair<string, int>>(_cappedSheets);
            worst.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < worst.Count && i < 6; i++)
                parts.Add($"{worst[i].Key}:{worst[i].Value}");
            return string.Join(" · ", parts);
        }

        /// <summary>A rectangle of the page for a sprite this size, on a shelf of about its
        /// height or on a new shelf below the last; false when the page is full.</summary>
        private static bool TryPlace(Page page, int width, int height, out Rectangle rect)
        {
            rect = default;
            int pageWidth = page.Target.Width, pageHeight = page.Target.Height;
            if (width > pageWidth || height > pageHeight)
                return false;
            // A slot a swept sprite left, when it is exactly this size: the sheet rebuilt every
            // frame hands back the same size it takes, and only an exact fit keeps the slot's
            // bounds the sprite's own bounds for the next sweep.
            for (int i = 0; i < page.FreeSlots.Count; i++)
            {
                Rectangle slot = page.FreeSlots[i];
                if (slot.Width != width || slot.Height != height)
                    continue;
                page.FreeSlots[i] = page.FreeSlots[^1];
                page.FreeSlots.RemoveAt(page.FreeSlots.Count - 1);
                rect = slot;
                return true;
            }
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
                    if (_entries.TryGetValue(page.Keys[i], out Entry gone))
                    {
                        // A young bake whose name nothing else has claimed is waiting for the
                        // sheet that replaced it to adopt it, so its pixels stay and its slot
                        // stays claimed. Sweeping here is what had the farmer's body baked sixty
                        // times a second for a picture that had not changed.
                        if (SharedTicks.Now - gone.BakedTick <= RebakeAdoptedAfterTicks
                            && StillTheNamedHolder(page.Keys[i]))
                            continue;
                        page.FreeSlots.Add(new Rectangle(gone.Rect.X - Gutter, gone.Rect.Y - Gutter,
                            gone.Rect.Width + 2 * Gutter, gone.Rect.Height + 2 * Gutter));
                    }
                    NoteSwept(page.Keys[i].sheet);
                    ForgetName(page.Keys[i]);
                    _entries.Remove(page.Keys[i]);
                    page.Keys.RemoveAt(i);
                    Evicted++;
                    EvictedBySweep++;
                }
                if (page.Target.IsDisposed || page.Keys.Count == 0)
                    _sweepScratch.Add(page);
            }
            foreach (Page page in _sweepScratch)
                DropPage(page, page.Target.IsDisposed ? PageDropReason.DeadTarget : PageDropReason.Emptied);
        }

        /// <summary>Why a whole page is going, which is the same question <see cref="EvictedByBudget"/>
        /// answers for a sprite.</summary>
        private enum PageDropReason { Budget, DeadTarget, Emptied }

        /// <summary>Whether this key is the one <see cref="_bakedByName"/> points at, which is what
        /// makes its bake adoptable rather than rubbish.</summary>
        private bool StillTheNamedHolder((Texture2D sheet, Rectangle rect, int variant) key)
        {
            string name = key.sheet.Name ?? "";
            return name.Length > 0
                && _bakedByName.TryGetValue((name, key.rect, key.variant), out Texture2D? holder)
                && ReferenceEquals(holder, key.sheet);
        }

        /// <summary>Give up this key's claim on its name, so nothing tries to adopt a bake that is
        /// about to be written over.</summary>
        private void ForgetName((Texture2D sheet, Rectangle rect, int variant) key)
        {
            string name = key.sheet.Name ?? "";
            if (name.Length == 0)
                return;
            var nameKey = (name, key.rect, key.variant);
            if (_bakedByName.TryGetValue(nameKey, out Texture2D? holder) && ReferenceEquals(holder, key.sheet))
                _bakedByName.Remove(nameKey);
        }

        private void DropPage(Page page, PageDropReason reason)
        {
            foreach (var key in page.Keys)
            {
                ForgetName(key);
                _entries.Remove(key);
            }
            Evicted += page.Keys.Count;
            switch (reason)
            {
                case PageDropReason.Budget: EvictedByBudget += page.Keys.Count; break;
                case PageDropReason.DeadTarget: EvictedByDeadPage += page.Keys.Count; break;
                default: EvictedBySweep += page.Keys.Count; break;
            }
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
                DropPage(oldest!, PageDropReason.Budget);
            }
        }

        internal void Clear()
        {
            foreach (Page page in _pages)
                if (!page.Target.IsDisposed)
                    page.Target.Dispose();
            _pages.Clear();
            _entries.Clear();
            _bakedByName.Clear();
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
