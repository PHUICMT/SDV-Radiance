using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline — reading MAP TILE ART, and the cheap questions asked of it. Everything the
    /// water mask decides about a tile starts here: what art the map placed on it, which of its 256
    /// pixels are opaque, and which of those read as water. All of it is cached, because the
    /// alternative was a GPU readback per tile and that was the single worst thing this mod ever did
    /// on a large map (see <see cref="EnsureSheetPixels"/>: whole sheets are read once on entry).
    ///
    /// Nothing in here knows what a water mask is. It answers questions about pixels; the callers in
    /// RenderPipeline.WaterMask.cs and .Async.cs decide what the answers mean.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Resolve the 16×16 source art of a map tile (first frame for animated tiles).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D texture, out Rectangle src)
            => TryTileArt(layer, tx, ty, out texture, out src, out _, out _);

        /// <summary>As above, also reporting whether the tile is ANIMATED — animation is a strong
        /// "this is water/flowing art" signal (fountains, waterfalls, the beach surf line).</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D texture, out Rectangle src, out bool animated)
            => TryTileArt(layer, tx, ty, out texture, out src, out animated, out _);

        /// <summary>As above, also reporting how the MAP turns the tile (see MapLayers.Orientation).
        /// The source rect points at the upright art on the sheet; anything derived from those
        /// pixels has to be turned by this before it can line up with what the player sees.</summary>
        private bool TryTileArt(xTile.Layers.Layer? layer, int tx, int ty, out Texture2D texture, out Rectangle src, out bool animated, out byte orient)
        {
            texture = null!;
            src = default;
            animated = false;
            orient = 0;
            if (layer == null || tx < 0 || ty < 0 || tx >= layer.LayerWidth || ty >= layer.LayerHeight)
                return false;
            var t = layer.Tiles[tx, ty];
            orient = MapLayers.Orientation(t);
            if (t is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
            {
                t = at.TileFrames[0];
                animated = true;
            }
            if (t?.TileSheet == null)
                return false;
            if (!_tilesheetTextureCache.TryGetValue(t.TileSheet.ImageSource, out Texture2D? sheet))
            {
                try { sheet = Game1.content.Load<Texture2D>(t.TileSheet.ImageSource); }
                catch { sheet = null; }
                _tilesheetTextureCache[t.TileSheet.ImageSource] = sheet;
                // Shadow art ships in its OWN tilesheets, and the name says so. Every shadow sheet
                // in the map dump is called *Shadow(s) / *ShadowTilesheet / *CanopyShadow (15 of
                // them, vanilla and modded), so the sheet identity is a second, colour-independent
                // way to know a tile is a cast shadow and must never carve the water it falls on.
                if (sheet != null
                    && t.TileSheet.ImageSource.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                    _shadowTilesheets.Add(sheet);
            }
            if (sheet == null)
                return false;
            var ib = t.TileSheet.GetTileImageBounds(t.TileIndex);
            if (ib.Width != 16 || ib.Height != 16)
                return false;
            texture = sheet;
            src = new Rectangle(ib.X, ib.Y, 16, 16);
            return true;
        }

        /// <summary>Painted-water test for a single art pixel: blue-dominant or teal/foam.
        /// Matches the shader's colour gates, but runs on the STATIC source art (stable,
        /// classify once per tile art) instead of the composited frame.</summary>
        private static bool WaterColor(Color c)
        {
            if (c.A < 200)
                return false;
            if (c.B > c.R + 14 && c.B + 10 >= c.G) return true;   // blue water
            // Teal / shallow edge — measured against the real tilesheets (2026-07-21, re-measured
            // 2026-07-22): the old loose gate (B > R+6) classified plain GRASS greens and rippled
            // meadows; at G-25 the DARK grass/fern strip tiles (summer avg (23,97,72), spring fern
            // clumps — full 245-256px tiles) still passed at G-B exactly 23-25 and waved on land.
            // Real teal water pixels sit at G-B = 19 on every sheet (beach tide pools, pond edges);
            // the 20-22 band is EMPTY, so G-20 splits them cleanly.
            if (c.G > c.R + 10 && c.B > c.R + 12 && c.B >= c.G - 20) return true;
            return false;
        }

        /// <summary>16×16 painted-water classification of one tile art, cached per (texture, rect,
        /// foam). With <paramref name="foam"/> (animated tiles that touch core water — the surf
        /// line), bright unsaturated wash pixels count as water too: white wave foam fails every
        /// hue gate, which left dead un-effected bands along the tide line.</summary>
        /// <summary>Whole-sheet pixel array for a tilesheet, read back once (main thread) and
        /// cached. Returns null for over-cap sheets (caller falls back to per-region GetData) or
        /// on failure. The single readback replaces one-per-tile readbacks (each a GPU stall).</summary>
        private Color[]? EnsureSheetPixels(Texture2D texture)
        {
            if (_tilesheetPixelCache.TryGetValue(texture, out Color[]? sheet))
                return sheet;
            long px = (long)texture.Width * texture.Height;
            if (px <= SheetPixCap)
            {
                try
                {
                    sheet = new Color[px];
                    // Read in STRIPS rather than one whole-surface GetData. The cap this replaces
                    // existed to bound the driver's staging cost for a huge sheet, and its fallback
                    // was a readback PER TILE — thousands of times more expensive than the
                    // allocation it avoided (a 240x156 map on an 8.64 Mpx sheet spent 43 s in one
                    // gather). Strips keep the staging bounded while still costing one readback per
                    // strip, so size no longer decides between "fast" and "unusable".
                    for (int y0 = 0; y0 < texture.Height; y0 += SheetStripRows)
                    {
                        int rows = Math.Min(SheetStripRows, texture.Height - y0);
                        texture.GetData(0, new Rectangle(0, y0, texture.Width, rows),
                            sheet, y0 * texture.Width, rows * texture.Width);
                    }
                }
                catch { sheet = null; }
            }
            if (sheet == null)
                _monitor.Log($"[water] tilesheet {texture.Width}x{texture.Height} not cached — tile art falls back to per-tile reads", LogLevel.Warn);
            _tilesheetPixelCache[texture] = sheet; // null = absurd size or failed → per-tile fallback (deduped)
            return sheet;
        }

        /// <summary>Read back every tilesheet a location uses, once, on entry — so the first-touch
        /// GPU readbacks all land in the (already synchronous, warp-fade-hidden) location change
        /// instead of hitching mid-walk when you scroll into a region using a fresh sheet.</summary>
        private void PrewarmSheetPixels(GameLocation location)
        {
            if (ReferenceEquals(location, _prewarmedLocation))
                return;
            _prewarmedLocation = location;
            var map = location.map;
            if (map == null)
                return;
            foreach (var ts in map.TileSheets)
            {
                if (!_tilesheetTextureCache.TryGetValue(ts.ImageSource, out Texture2D? texture))
                {
                    try { texture = Game1.content.Load<Texture2D>(ts.ImageSource); }
                    catch { texture = null; }
                    _tilesheetTextureCache[ts.ImageSource] = texture;
                }
                if (texture != null)
                    EnsureSheetPixels(texture);
            }
        }

        /// <summary>Fill <see cref="_tileArtPixels"/> with a tile's 16×16 pixels. Reads from the cached
        /// whole-sheet pixel array (no GPU work) when available, falling back to a per-region
        /// <c>GetData</c> for over-cap sheets. Main-thread only (GPU readback on first sheet touch).</summary>
        private void ReadTileArt(Texture2D texture, Rectangle src)
        {
            _tileArtPixels ??= new Color[256];
            ReadTileArtInto(texture, src, _tileArtPixels);
        }

        /// <summary>The same read, into a buffer the caller owns. Split out because the label
        /// guard's fingerprint read can land in the middle of a gather that is still holding
        /// <see cref="_tileArtPixels"/>, and one shared scratch buffer with two readers is a
        /// corruption that would get blamed on something else entirely.</summary>
        private void ReadTileArtInto(Texture2D texture, Rectangle src, Color[] into)
        {
            Color[]? sheet = EnsureSheetPixels(texture);
            if (sheet != null)
            {
                int tw = texture.Width;
                for (int row = 0; row < 16; row++)
                {
                    int soff = (src.Y + row) * tw + src.X;
                    if (soff < 0 || soff + 16 > sheet.Length) { Array.Clear(into, row * 16, 16); continue; }
                    Array.Copy(sheet, soff, into, row * 16, 16);
                }
            }
            else if (_tileArtCache.TryGetValue((texture, src), out Color[]? tile))
            {
                Array.Copy(tile, into, 256);
            }
            else
            {
                // A refused sheet still gets read at most ONCE per distinct tile: the gather walks
                // every tile of the map, so an undeduped readback here is paid per painted cell,
                // not per piece of art. Bounded so a pathological map cannot grow this without end.
                try { texture.GetData(0, src, into, 0, 256); } catch { Array.Clear(into, 0, 256); }
                if (_tileArtCache.Count < 16_384)
                {
                    var copy = new Color[256];
                    Array.Copy(into, copy, 256);
                    _tileArtCache[(texture, src)] = copy;
                }
            }
        }

        /// <summary>
        /// The fingerprint of the art one map tile actually draws, for the label guard in
        /// <see cref="LabelStore"/>.
        /// </summary>
        /// <remarks>
        /// Reads through the same sheet cache the mask fills on entry, so asking costs a
        /// dictionary hit and 256 multiplies rather than a trip to the graphics card. LabelStore
        /// asks at most once per (sheet, tile index) and remembers the answer, so this runs a few
        /// dozen times on entering a map and then not at all.
        /// </remarks>
        internal bool TryFingerprintTileArt(xTile.Layers.Layer layer, int x, int y,
            out ulong fingerprint)
        {
            fingerprint = 0;
            if (!TryTileArt(layer, x, y, out Texture2D texture, out Rectangle src))
                return false;
            _fingerprintPixels ??= new Color[256];
            ReadTileArtInto(texture, src, _fingerprintPixels);
            fingerprint = ArtFingerprint.OfTilePixels(_fingerprintPixels);
            return true;
        }

        /// <summary>The label guard's own scratch tile. Never shared with the gather's: see
        /// <see cref="ReadTileArtInto"/>.</summary>
        private Color[]? _fingerprintPixels;

        /// <summary>A CAST SHADOW, not a structure: near-black and translucent. Bridges, cliffs and
        /// trees drop these onto the water from the Buildings/Front/AlwaysFront layers, and carving
        /// them punched the shadow's exact silhouette out of the effect channel — but shaded water
        /// is still water and has to keep rippling. Measured over the whole map dump: AlwaysFront
        /// holds 192k distinct tiles of which only 11k are fully opaque and ~99k are exactly this
        /// dark translucent wash, so the rule has to be conservative. Near-black only (art is
        /// premultiplied, so a black wash stays black at any alpha) and grey, so no coloured art
        /// can fall through it.</summary>
        private static bool ShadowWash(Color c)
        {
            if (c.A >= 250)
                return false;                       // fully opaque art is never a wash
            // Brightness alone. A saturation term was tried and only cost recall: measured over
            // the 15 shadow sheets in the map dump it rejected 28% of two of them (SVE's building
            // shadow, IridiumQuarry) for being faintly blue, and at max(rgb) <= 40 a pixel is
            // indistinguishable from black whatever its hue, so hue cannot separate art from wash.
            return Math.Max(c.R, Math.Max(c.G, c.B)) <= 40;
        }

        /// <summary>Textures that came from a tilesheet whose name says "shadow".</summary>
        private readonly HashSet<Texture2D> _shadowTilesheets = new();
        private static readonly bool[] _emptyTileBits = new bool[256];

        /// <summary>16×16 opacity bits + opaque-pixel count of one tile art, cached — used to
        /// carve piers/bridges/pads out of the water mask (count decides march-blocking).
        /// The old "≥60% of the opaque art is water-COLOURED → wave overlay, don't carve"
        /// bail-out is GONE (V4 D1: no colour guessing): it existed to keep unlabelled water
        /// overlays from carving themselves, but it also waved through every blue-ish or
        /// murky-toned STRUCTURE — the SVE crystal boulder in the Mountain lake and the
        /// FarmCave plank both rippled because their art happened to pass a colour test.
        /// Genuine water drawn on overlay layers is protected by its LABEL now (a label's
        /// liquid pixels are removed from the carve); art nobody labelled carves by opacity,
        /// so an unlabelled mod pond at worst goes calm instead of rippling its own rocks.</summary>
        private (bool[] bits, int count) SolidBits(Texture2D texture, Rectangle src)
        {
            var r = OpaqueBits(texture, src);
            return (r.bits, r.count);
        }

        /// <summary>Opacity bits turned the way the map places the tile. The count is unchanged by
        /// a turn, so only the bits move. Caching stays keyed on the upright art: one sheet tile
        /// can appear on a map both plain and mirrored, and both readings come off the same entry.</summary>
        private (bool[] bits, int count) SolidBits(Texture2D texture, Rectangle src, byte orient)
        {
            var r = SolidBits(texture, src);
            return orient == 0 ? r : (MapLayers.Orient(r.bits, orient), r.count);
        }

        private bool[] AnyAlphaBits(Texture2D texture, Rectangle src, byte orient)
            => MapLayers.Orient(AnyAlphaBits(texture, src), orient);

        /// <summary>The same opacity bits WITHOUT the "mostly water-coloured → not a structure"
        /// bail-out. Only for art a label has explicitly called ground: that guess is right for a
        /// wave overlay and wrong for a snow-covered bank ledge, whose pale blue reads as water
        /// (winter_outdoorsTileSheet#211 is 189 of 220 opaque pixels water-coloured, so it carved
        /// nothing at all and the mirror ran straight over the bank).</summary>
        private (bool[] bits, int count, int water) OpaqueBits(Texture2D texture, Rectangle src)
        {
            if (_shadowTilesheets.Contains(texture))
                return (_emptyTileBits, 0, 0);     // a whole sheet of cast shadows carves nothing
            var key = (texture, src);
            if (_tileSolidBitsCache.TryGetValue(key, out var entry))
                return entry;
            var bits = new bool[256];
            int n = 0, w = 0;
            _tileArtPixels ??= new Color[256];
            try
            {
                ReadTileArt(texture, src);
                for (int p = 0; p < 256; p++)
                {
                    if (bits[p] = _tileArtPixels[p].A >= 128 && !ShadowWash(_tileArtPixels[p]))
                    {
                        n++;
                        if (WaterColor(_tileArtPixels[p])) w++;
                    }
                }
            }
            catch { /* leave all-false */ }
            entry = (bits, n, w);
            _tileSolidBitsCache[key] = entry;
            return entry;
        }

        /// <summary>Pixels where this art draws ANYTHING visible (alpha >= 32), shadow washes
        /// INCLUDED. Only for decisions a LABEL has already made, where no heuristic is left to
        /// protect: a waterfall's spray is visibly water far below the 128-opaque bar, and a
        /// bridge's painted shadow is part of the bridge when the label calls it ground. Fully
        /// transparent pixels are still the layer below showing through, so they stay out.</summary>
        private bool[] AnyAlphaBits(Texture2D texture, Rectangle src)
        {
            if (_shadowTilesheets.Contains(texture))
                return _emptyTileBits;
            var key = (texture, src);
            if (_tileAnyAlphaBitsCache.TryGetValue(key, out var bits))
                return bits;
            bits = new bool[256];
            _tileArtPixels ??= new Color[256];
            try
            {
                ReadTileArt(texture, src);
                for (int p = 0; p < 256; p++)
                    bits[p] = _tileArtPixels[p].A >= 32;
            }
            catch { /* leave all-false */ }
            _tileAnyAlphaBitsCache[key] = bits;
            return bits;
        }
    }
}
