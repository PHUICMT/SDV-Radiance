using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The soft look's sprites, one small target per (sheet, source rectangle) the game has drawn.
    ///
    /// <para>The soft look was first baked per SHEET, like the doubling, and every sprite came
    /// out with a faint dark frame around its cell: the kernel reads two pixels past a texel and
    /// the tent one texel past it, and on a sheet where the cells touch (the grass, the trees,
    /// the tile sheets) those pixels belong to the next sprite. A dark neighbour became a dark
    /// edge on a sprite that has none. The texture-upscaler mods never had this because they
    /// resample per sprite; so does this. Each bake reads its own rectangle and nothing outside it
    /// (the shader clamps every read to the rectangle), and only the sprites actually drawn are
    /// held: a hundred items at sixteen times their bytes is a few megabytes, where a whole sheet
    /// at sixteen times was fifteen.</para>
    ///
    /// <para>The key is the sheet instance and the rectangle. A sheet reloaded by a content patch
    /// is a new instance and its entries are swept when the old one is disposed, the same rule
    /// the doubled sheets follow.</para>
    /// </summary>
    internal sealed class SoftSpriteCache
    {
        private sealed class Entry
        {
            public RenderTarget2D Target = null!;
            public int LastUsedTick;
            public long Bytes;
        }

        private readonly Dictionary<(Texture2D sheet, Rectangle rect, int variant), Entry> _entries = new();
        private readonly HashSet<Texture2D> _ownTargets = new();
        private readonly List<(Texture2D sheet, Rectangle rect, int variant)> _evictScratch = new();
        private readonly string _bucket;
        private readonly long _budgetBytes;
        private readonly int _largestSpriteSide;
        private readonly int _generatePerFrameCap;
        private readonly int _scale;
        private readonly Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, Rectangle, RenderTarget2D, bool> _bake;
        private long _heldBytes;
        private int _generatedThisFrame, _frameTick = -1;
        private SpriteBatch? _spriteBatch;

        internal SoftSpriteCache(string bucket, long budgetBytes, int largestSpriteSide, int scale, int generatePerFrameCap,
            Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, Rectangle, RenderTarget2D, bool> bake)
        {
            _bucket = bucket;
            _budgetBytes = budgetBytes;
            _largestSpriteSide = largestSpriteSide;
            _scale = scale;
            _generatePerFrameCap = generatePerFrameCap;
            _bake = bake;
        }

        internal int Count => _entries.Count;
        internal int Scale => _scale;
        internal int Refused { get; private set; }
        internal int Evicted { get; private set; }
        internal int Generated { get; private set; }

        internal bool IsOwnOutput(Texture2D texture) => _ownTargets.Contains(texture);

        /// <summary>The soft sprite for this rectangle of this sheet, made if the frame's cap and the
        /// budget allow. Null means "draw the sheet as it is this frame".</summary>
        internal Texture2D? For(GraphicsDevice device, Effect effect, Texture2D sheet, Rectangle rect, int variant = 0)
        {
            if (sheet.IsDisposed || _ownTargets.Contains(sheet))
                return null;
            var key = (sheet, rect, variant);
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                if (entry.Target.IsDisposed)
                {
                    _entries.Remove(key);
                    _ownTargets.Remove(entry.Target);
                    _heldBytes -= entry.Bytes;
                }
                else
                {
                    entry.LastUsedTick = Game1.ticks;
                    return entry.Target;
                }
            }
            if (_frameTick != Game1.ticks)
            {
                _frameTick = Game1.ticks;
                _generatedThisFrame = 0;
            }
            if (_generatedThisFrame >= _generatePerFrameCap)
                return null;
            if (rect.Width <= 0 || rect.Height <= 0 || rect.Width > _largestSpriteSide || rect.Height > _largestSpriteSide)
            {
                Refused++;
                return null;
            }
            long bytes = (long)rect.Width * rect.Height * 4 * _scale * _scale;
            EvictToFit(bytes);
            if (_heldBytes + bytes > _budgetBytes)
            {
                Refused++;
                return null;
            }
            RenderTarget2D target;
            try
            {
                target = VramTally.Track(new RenderTarget2D(device, rect.Width * _scale, rect.Height * _scale, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), _bucket);
            }
            catch
            {
                Refused++;
                return null;
            }
            _spriteBatch ??= new SpriteBatch(device);
            RenderTargetBinding[] previous = device.GetRenderTargets();
            bool made;
            try
            {
                made = _bake(device, _spriteBatch, effect, sheet, rect, target);
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
                target.Dispose();
                Refused++;
                return null;
            }
            _entries[key] = new Entry { Target = target, LastUsedTick = Game1.ticks, Bytes = bytes };
            _ownTargets.Add(target);
            _heldBytes += bytes;
            _generatedThisFrame++;
            Generated++;
            return target;
        }

        /// <summary>Drop every entry whose sheet has been disposed (a content patch reloaded it).</summary>
        internal void SweepDisposed()
        {
            _evictScratch.Clear();
            foreach (var pair in _entries)
                if (pair.Key.sheet.IsDisposed || pair.Value.Target.IsDisposed)
                    _evictScratch.Add(pair.Key);
            foreach (var key in _evictScratch)
                Drop(key);
        }

        private void Drop((Texture2D sheet, Rectangle rect, int variant) key)
        {
            Entry entry = _entries[key];
            if (!entry.Target.IsDisposed)
                entry.Target.Dispose();
            _ownTargets.Remove(entry.Target);
            _heldBytes -= entry.Bytes;
            _entries.Remove(key);
            Evicted++;
        }

        /// <summary>Drop the least recently drawn sprites until <paramref name="incoming"/> fits.</summary>
        private void EvictToFit(long incoming)
        {
            while (_heldBytes + incoming > _budgetBytes && _entries.Count > 0)
            {
                (Texture2D sheet, Rectangle rect, int variant) oldest = default;
                int oldestTick = int.MaxValue;
                foreach (var pair in _entries)
                {
                    if (pair.Value.LastUsedTick < oldestTick)
                    {
                        oldestTick = pair.Value.LastUsedTick;
                        oldest = pair.Key;
                    }
                }
                Drop(oldest);
            }
        }

        internal void Clear()
        {
            foreach (Entry entry in _entries.Values)
                entry.Target.Dispose();
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
            => $"{_bucket}: {_entries.Count} sprites, {_heldBytes / (1024.0 * 1024.0):F1} MB held, {Generated} made, {Evicted} evicted, {Refused} refused";
    }
}
