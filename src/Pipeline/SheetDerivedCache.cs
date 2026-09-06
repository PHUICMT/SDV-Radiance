using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// A texture derived from a sprite sheet by a shader, one per sheet, made on the GPU the first
    /// time the sheet is asked for and kept under a memory budget. The sprite relief keeps a
    /// normal map per sheet in one of these; the sheet upscaler keeps a doubled sheet in another.
    /// Keyed by the sheet's texture instance and a variant NUMBER, so every distinct way of
    /// deriving one sheet gets its own entry rather than inheriting whichever was baked first.
    /// </summary>
    /// <remarks>
    /// A sheet-sized derivative is what lets every draw keep its own source rectangle: the draw
    /// substitutes the texture and changes nothing else (or scales the rectangle, for the
    /// upscaler). The cost is memory, so there is a budget, a least-recently-used eviction, a
    /// ceiling above which a sheet is refused, and a cap on how many are made in one frame so a
    /// warp does not stall on forty. Counts are kept for the debug caption, which is how "it is
    /// not on my trees" gets answered.
    /// </remarks>
    internal sealed class SheetDerivedCache
    {
        private sealed class Entry
        {
            public RenderTarget2D Target = null!;
            public int LastUsedTick;
            public long Bytes;
        }

        private readonly string _bucket;
        private readonly long _budgetBytes;
        private readonly long _largestInputBytes;
        private readonly int _scale;
        private readonly int _generatePerFrameCap;
        /// <summary>The tick through which the per-frame cap is lifted. See <see cref="AllowBurstThisTick"/>.</summary>
        private int _burstUntilTick = -1;

        /// <summary>
        /// Let this frame generate every sheet it asks for, cap or no cap.
        /// </summary>
        /// <remarks>
        /// The cap exists so a screen that suddenly needs twenty sheets does not spend one long
        /// frame on them; it spreads them over five frames instead, each a little late, which on
        /// arrival at a new map means five frames of sheets switching from blocky to smooth in
        /// front of the player. On a warp the game is showing its fade-to-black, and a long frame
        /// under a black screen is a frame nobody sees, so that is where the whole set is made.
        /// </remarks>
        internal void AllowBurstThisTick() => _burstUntilTick = Game1.ticks + 1;
        /// <summary>Sets the shader's parameters for one sheet: (effect, sheet, variant).</summary>
        private readonly Action<Effect, Texture2D, int> _setParameters;
        private readonly string _technique;

        private readonly Dictionary<(Texture2D Sheet, int Variant), Entry> _entries = new();
        private readonly HashSet<Texture2D> _ownTargets = new();
        private readonly List<(Texture2D Sheet, int Variant)> _evictScratch = new();
        private long _heldBytes;
        private int _generatedThisFrame, _frameTick = -1;
        private SpriteBatch? _spriteBatch;

        /// <summary>A derivation that is more than one draw of one technique, or null for the
        /// one-draw kind. Called with the target already bound and cleared; returns false to refuse.</summary>
        private readonly Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, RenderTarget2D, bool>? _bake;

        internal SheetDerivedCache(string bucket, long budgetBytes, long largestInputBytes, int scale,
            int generatePerFrameCap, string technique, Action<Effect, Texture2D, int> setParameters,
            Func<GraphicsDevice, SpriteBatch, Effect, Texture2D, RenderTarget2D, bool>? bake = null)
        {
            _bucket = bucket;
            _budgetBytes = budgetBytes;
            _largestInputBytes = largestInputBytes;
            _scale = scale;
            _generatePerFrameCap = generatePerFrameCap;
            _technique = technique;
            _setParameters = setParameters;
            _bake = bake;
        }

        /// <summary>Texels of derivative per texel of sheet.</summary>
        internal int Scale => _scale;

        internal int Count => _entries.Count;
        internal long HeldBytes => _heldBytes;
        internal int Refused { get; private set; }
        internal int Evicted { get; private set; }
        internal int Generated { get; private set; }

        /// <summary>Whether <paramref name="texture"/> is one of this cache's own outputs (so a caller
        /// that substitutes textures never derives a derivative).</summary>
        internal bool IsOwnOutput(Texture2D texture) => _ownTargets.Contains(texture);

        /// <summary>The derivative for <paramref name="sheet"/>, generating it if the frame's cap and
        /// the budget allow. Null means "use the sheet as it is this frame".</summary>
        /// <param name="variant">Which derivation of this sheet is wanted. It is part of the KEY,
        /// so every way a sheet can be derived needs its own number: two derivations sharing one
        /// left the first to be baked standing in for the other forever. A map tilesheet is baked
        /// without a bevel and an ordinary sheet with one, and while those two shared a key, a map
        /// tile asked for during the second after a season change - before the list of the map's
        /// own sheets had caught up - baked a bevelled map that then answered every later ask, and
        /// the tile wore an embossed edge until the relief was switched off and on again.</param>
        internal Texture2D? For(GraphicsDevice device, Effect effect, Texture2D sheet, int variant)
        {
            if (sheet.IsDisposed || _ownTargets.Contains(sheet))
                return null;
            var key = (sheet, variant);
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
            if (_generatedThisFrame >= _generatePerFrameCap && Game1.ticks > _burstUntilTick)
                return null;
            long inputBytes = (long)sheet.Width * sheet.Height * 4;
            if (inputBytes > _largestInputBytes)
            {
                Refused++;
                return null;
            }
            long bytes = inputBytes * _scale * _scale;
            EvictToFit(bytes);
            if (_heldBytes + bytes > _budgetBytes)
            {
                Refused++;
                return null;
            }

            RenderTarget2D target;
            try
            {
                target = VramTally.Track(new RenderTarget2D(device, sheet.Width * _scale, sheet.Height * _scale, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), _bucket);
            }
            catch
            {
                Refused++;
                return null;
            }
            _spriteBatch ??= new SpriteBatch(device);
            RenderTargetBinding[] previous = device.GetRenderTargets();
            try
            {
                device.SetRenderTarget(target);
                device.Clear(Color.Transparent);
                if (_bake != null)
                {
                    if (!_bake(device, _spriteBatch, effect, sheet, target))
                        throw new InvalidOperationException("the bake refused");
                }
                else
                {
                    _setParameters(effect, sheet, variant);
                    effect.CurrentTechnique = effect.Techniques[_technique];
                    _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                        DepthStencilState.None, RasterizerState.CullNone, effect);
                    _spriteBatch.Draw(sheet, new Rectangle(0, 0, target.Width, target.Height), Color.White);
                    _spriteBatch.End();
                }
            }
            catch
            {
                try { _spriteBatch.End(); } catch { }
                target.Dispose();
                Refused++;
                return null;
            }
            finally
            {
                if (previous.Length > 0) device.SetRenderTargets(previous);
                else device.SetRenderTarget(null);
            }
            _entries[key] = new Entry { Target = target, LastUsedTick = Game1.ticks, Bytes = bytes };
            _ownTargets.Add(target);
            _heldBytes += bytes;
            _generatedThisFrame++;
            Generated++;
            return target;
        }

        /// <summary>Drop every entry whose source sheet has been disposed (a content patch reloaded
        /// it, Fashion Sense rebuilt it): its key can never be asked for again, so nothing else
        /// would ever free it. Before this sweep those ghosts held their bytes until the budget was
        /// full and every LIVING sheet was refused. Cheap enough to run once a frame.</summary>
        internal void SweepDisposed()
        {
            _evictScratch.Clear();
            foreach (var pair in _entries)
                if (pair.Key.Sheet.IsDisposed || pair.Value.Target.IsDisposed)
                    _evictScratch.Add(pair.Key);
            foreach (var key in _evictScratch)
            {
                Entry entry = _entries[key];
                if (!entry.Target.IsDisposed)
                    entry.Target.Dispose();
                _ownTargets.Remove(entry.Target);
                _heldBytes -= entry.Bytes;
                _entries.Remove(key);
                Evicted++;
            }
        }

        /// <summary>Drop the least recently used derivatives until <paramref name="incoming"/> fits.</summary>
        private void EvictToFit(long incoming)
        {
            if (_heldBytes + incoming <= _budgetBytes)
                return;
            _evictScratch.Clear();
            _evictScratch.AddRange(_entries.Keys);
            _evictScratch.Sort((a, b) => _entries[a].LastUsedTick.CompareTo(_entries[b].LastUsedTick));
            foreach (var key in _evictScratch)
            {
                if (_heldBytes + incoming <= _budgetBytes)
                    break;
                // Never evict what this very frame is still using: it would be regenerated at once.
                if (_entries[key].LastUsedTick == Game1.ticks)
                    continue;
                Entry entry = _entries[key];
                entry.Target.Dispose();
                _ownTargets.Remove(entry.Target);
                _heldBytes -= entry.Bytes;
                _entries.Remove(key);
                Evicted++;
            }
        }

        /// <summary>Give everything back (the feature switched off, or the device is going away).</summary>
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
            => $"{_bucket}: {_entries.Count} sheets, {_heldBytes / (1024.0 * 1024.0):F1} MB held, {Generated} made, {Evicted} evicted, {Refused} refused";
    }
}
