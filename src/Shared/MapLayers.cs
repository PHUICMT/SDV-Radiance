using System;
using System.Collections.Generic;

namespace SDVRadiance
{
    /// <summary>Helpers for xTile map-layer identity shared by the water mask and reflection bakes.</summary>
    internal static class MapLayers
    {
        /// <summary>
        /// Whether a layer id belongs to a layer family: "Back", "Back2", "Back37" and "Back-1"
        /// are all the Back family, while "Backdrop" is not — after the family prefix only an
        /// optional minus and then digits may follow.
        /// <para>
        /// The minus used to disqualify a layer, on the belief that "Buildings-1" is the Tiled
        /// convention for a layer its author switched off. In 1.6 it is a real drawn layer that
        /// sits BELOW its family's base, and map packs use it: Gem Sea Shores puts 267 cells on
        /// Beach_West's Buildings-1, including the waterfall pool's rim, and Island West/North/East
        /// and vanilla Beach and Forest carry them too. Every one of those tiles was invisible to
        /// the carve, to the labels and to radiance_tile, so the water mask had no per-pixel truth
        /// there and fell back to a whole-tile verdict — reported as the effect landing in the
        /// wrong place in patches, matching the same gaps in the labeler's preview.
        /// </para>
        /// </summary>
        internal static bool BelongsToFamily(string layerId, string family)
        {
            if (!layerId.StartsWith(family, StringComparison.Ordinal))
                return false;
            int i = family.Length;
            if (i < layerId.Length && layerId[i] == '-')
            {
                if (++i == layerId.Length)
                    return false;      // a bare trailing "-" is not a suffix
            }
            for (; i < layerId.Length; i++)
                if (layerId[i] < '0' || layerId[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// How a tile is turned on the map, as one byte: bit 0-1 = quarter turns clockwise,
        /// bit 2 = mirrored horizontally BEFORE the turn. 0 for a plain tile.
        /// <para>
        /// A .tmx keeps flip and rotation in the gid's top bits; the loader cannot put them in the
        /// tile index, so TMXTile translates them into the tile properties @Flip and @Rotation,
        /// and SMAPI's display device draws from those. The translation is NOT one property per
        /// bit: @Flip is a SpriteEffects value (1 horizontal, 2 VERTICAL), @Rotation can be
        /// NEGATIVE (-90 for the 270 case), and the diagonal bit comes out as flip-2 PLUS a turn.
        /// Reading only @Flip=1 and positive degrees silently dropped three of the seven turned
        /// combinations - 69 cells on Aimon's festival beach alone, including the spring_beach #93
        /// water tiles around the falls bridge, which is why those shores composed "twisted"
        /// while the plain-mirrored waterfall pieces were fine.
        /// </para>
        /// SpriteBatch applies the flip to the source and then rotates, so a vertical flip is a
        /// horizontal mirror plus a half turn in this encoding. Full table, measured against the
        /// .tmx gid bits (H,V,D): H=4, V=6, HV=2, D=7, HD=1, VD=3, HVD=5.
        /// </summary>
        internal static byte Orientation(xTile.Tiles.Tile? tile)
        {
            if (tile == null)
                return 0;
            int turns = 0, flip = 0;
            try
            {
                if (tile.Properties.TryGetValue("@Rotation", out var rot)
                    && int.TryParse(rot.ToString(), out int r))
                {
                    r = ((r % 360) + 360) % 360;      // TMXTile writes -90, never 270
                    if (r is 90 or 180 or 270) turns = r / 90;
                    else if (r is 1 or 2 or 3) turns = r;   // quarter-turn form, seen in the wild
                }
                if (tile.Properties.TryGetValue("@Flip", out var f))
                {
                    string s = f.ToString();
                    if (s is "1" or "true" or "True") flip = 1;
                    else if (s is "2") flip = 2;             // SpriteEffects.FlipVertically
                }
            }
            catch { /* a property bag that throws leaves the tile plain */ }
            if (flip == 2)
            {
                turns = (turns + 2) & 3;                     // vertical = mirror + half turn
                flip = 1;
            }
            return (byte)((flip != 0 ? 4 : 0) | turns);
        }

        /// <summary>Turn a 16x16 per-pixel tile buffer the same way the map turns the tile, so the
        /// bits line up with what the player sees. Returns the input untouched for a plain tile,
        /// which is the overwhelming majority, so the allocation only happens where it matters.</summary>
        internal static T[] Orient<T>(T[] src, byte orient)
        {
            if (orient == 0 || src.Length != 256)
                return src;
            var dst = new T[256];
            bool mirror = (orient & 4) != 0;
            int turns = orient & 3;
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int sx = mirror ? 15 - x : x, sy = y;
                    int rx = sx, ry = sy;
                    for (int k = 0; k < turns; k++)
                    {
                        int nx = 15 - ry, ny = rx;   // one quarter turn clockwise
                        rx = nx; ry = ny;
                    }
                    dst[ry * 16 + rx] = src[y * 16 + x];
                }
            }
            return dst;
        }

        /// <summary>The render family a layer id belongs to, or false for a marker/logic layer.
        /// One place to ask, so a diagnostic and the mask can never disagree about which layers
        /// the game draws. "AlwaysFront" is tested before "Front" because it also starts with it.</summary>
        internal static bool TryGetFamily(string layerId, out string family)
        {
            foreach (string fam in new[] { "AlwaysFront", "Back", "Buildings", "Front" })
                if (BelongsToFamily(layerId, fam)) { family = fam; return true; }
            family = "";
            return false;
        }

        /// <summary>
        /// Single sort key for a drawn layer id: the family's composite block plus the signed
        /// numeric suffix, so "Back-1" sorts UNDER "Back" and "Back2" sorts over it, and every
        /// "AlwaysFront" sits above every "Front". Family ranks are Back 0, Buildings 1, Front 2,
        /// AlwaysFront 3. A 100000-wide block plus a 10000 bias keeps every real layer — negative
        /// suffixes included — strictly positive, so the -1 "not a drawn layer" sentinel can never
        /// collide with a genuine layer name ("Back-1" would otherwise BE -1). Anything that is
        /// not a drawn layer ("Backdrop", a bare trailing "-", a marker layer) returns -1.
        /// <para>
        /// This is the one place the mod decides the game's bottom-to-top order; the labeler sorts
        /// by the same number published in the map dump, so the preview, the mask and the verifier
        /// can never disagree about which layer wins a pixel.
        /// </para>
        /// </summary>
        internal static int CompositeRank(string layerId)
        {
            string[] families = { "AlwaysFront", "Back", "Buildings", "Front" };
            int[] ranks = { 3, 0, 1, 2 };
            for (int f = 0; f < families.Length; f++)
            {
                string fam = families[f];
                if (!layerId.StartsWith(fam, StringComparison.Ordinal))
                    continue;
                string rest = layerId.Substring(fam.Length);
                int sign = 1;
                if (rest.Length > 0 && rest[0] == '-')
                {
                    if (rest.Length == 1)
                        return -1;          // a bare trailing "-" is not a suffix (matches BelongsToFamily)
                    sign = -1;
                    rest = rest.Substring(1);
                }
                int suffix = 0;
                if (rest.Length > 0)
                {
                    foreach (char ch in rest)
                    {
                        if (ch < '0' || ch > '9')
                            return -1;          // "Backdrop": a drawn family, not a drawn suffix
                        suffix = suffix * 10 + (ch - '0');
                    }
                }
                return ranks[f] * 100000 + 10000 + sign * suffix;
            }
            return -1;
        }

        /// <summary>Compare two drawn layers by <see cref="CompositeRank"/>, for sorting one
        /// family's bucket bottom-to-top.</summary>
        internal static int CompareLayerRank(xTile.Layers.Layer a, xTile.Layers.Layer b)
            => CompositeRank(a.Id).CompareTo(CompositeRank(b.Id));

        /// <summary>Every layer the game draws on this map, bottom-to-top (or top-to-bottom) by
        /// <see cref="CompositeRank"/>. Marker/logic layers and disabled-layer ids are dropped.
        /// This is the one list the verifier, the window/emissive scans and the compose all walk,
        /// so a map that numbers its layers oddly can never be read one way by the mask and
        /// another by the tool that checks the mask.</summary>
        internal static List<xTile.Layers.Layer> RenderedLayers(xTile.Map? map, bool topToBottom)
        {
            var pairs = new List<(xTile.Layers.Layer Layer, int Rank)>();
            if (map != null)
            {
                foreach (var l in map.Layers)
                {
                    int r = CompositeRank(l.Id);
                    if (r >= 0)
                        pairs.Add((l, r));
                }
            }
            pairs.Sort((a, b) => topToBottom ? b.Rank.CompareTo(a.Rank) : a.Rank.CompareTo(b.Rank));
            var result = new List<xTile.Layers.Layer>(pairs.Count);
            foreach (var p in pairs)
                result.Add(p.Layer);
            return result;
        }
    }
}
