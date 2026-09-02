using System;
using System.Collections.Generic;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - the map-wide memory of what the water gather worked out per tile.
    ///
    /// <para>The gather asks the game the same questions about the same tile every time the
    /// window slides over it: which label is painted there, what art the Buildings and Front
    /// layers hold, which of its pixels that art covers. On the Town river those questions cost
    /// 2.3 ms per rebuild on the main thread, measured on 2026-09-03, and a rebuild lands every
    /// few tiles of walking. None of the answers change while the map, its surface map and its
    /// labels stay the same, so they are kept here once per tile and a later rebuild copies them
    /// instead of asking again. The anchor job, which gathers the whole map once the player
    /// stands still, fills the whole cache in one go; until then each window fills its own tiles
    /// on first sight.</para>
    ///
    /// <para>What stays LIVE, gathered fresh on every rebuild: whether the game calls the tile
    /// water (the draw hook can change that frame to frame), and every tile of a fish pond (the
    /// pond is a building and comes and goes). A cached tile is only reused when the live water
    /// verdict matches the one it was gathered under AND the map still holds the same tile
    /// objects on every layer there (<see cref="TileIdentity"/>), so the copy is exactly what a
    /// fresh gather would have written. A map edited in place, the beach bridge repaired, puts
    /// a new tile object in the layer and that tile is asked again on the next rebuild; the ten
    /// second safety refresh no longer needs to re-ask the whole window for that, which was
    /// 2.7 ms every ten seconds measured on the Town river.</para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>One location's gathered answers, one slot per map tile.</summary>
        private sealed class GatheredTileAnswers
        {
            public GameLocation Location = null!;
            public SurfaceMap? Surface;
            public int LabelVersion;
            public int Epoch;
            public int Width, Height;
            /// <summary>Per tile: <see cref="GatheredFilled"/> and the flag bits below.</summary>
            public ushort[] Flags = Array.Empty<ushort>();
            public bool[]?[] EffectBits = Array.Empty<bool[]?>();
            public bool[]?[] WaterKeepBits = Array.Empty<bool[]?>();
            public bool[]?[] BuildingCarveBits = Array.Empty<bool[]?>();
            public bool[]?[] FrontCarveBits = Array.Empty<bool[]?>();
            public bool[]?[] IceBits = Array.Empty<bool[]?>();
            public bool[]?[] LavaBits = Array.Empty<bool[]?>();
            public bool[]?[] FlowBits = Array.Empty<bool[]?>();
            /// <summary>The <see cref="TileIdentity"/> each answer was gathered under.</summary>
            public int[] Identity = Array.Empty<int>();
            public int FilledCount;
        }

        /// <summary>A fingerprint of the tile objects the map holds at one tile across every
        /// rendered layer: object identity and tile index per layer, folded together. It changes
        /// whenever a layer's tile there is replaced, removed or added, which is what every
        /// in-place map edit does, and it costs a handful of array reads per tile.</summary>
        private static int TileIdentity(TileGatherContext ctx, int tx, int ty)
        {
            unchecked
            {
                int hash = (int)2166136261;
                void Fold(List<xTile.Layers.Layer>? layers)
                {
                    if (layers == null)
                        return;
                    foreach (var layer in layers)
                    {
                        var tile = tx < layer.LayerWidth && ty < layer.LayerHeight ? layer.Tiles[tx, ty] : null;
                        int part = tile == null ? 0
                            : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tile) ^ (tile.TileIndex * (int)0x9E3779B1);
                        hash = (hash ^ part) * 16777619;
                        hash = (hash ^ 1) * 16777619;   // a null tile and no layer must not fold alike
                    }
                }
                Fold(ctx.Backs);
                Fold(ctx.Blds);
                Fold(ctx.Fronts);
                Fold(ctx.Always);
                return hash;
            }
        }

        private const ushort GatheredFilled = 1 << 0;
        private const ushort GatheredIsWater = 1 << 1;
        private const ushort GatheredAnyLabeled = 1 << 2;
        private const ushort GatheredLargeSolid = 1 << 3;
        private const ushort GatheredDeck = 1 << 4;
        private const ushort GatheredLabeledLiquid = 1 << 5;
        private const ushort GatheredHasBuildingArt = 1 << 6;
        private const ushort GatheredBuildingGroundOverlay = 1 << 7;
        private const ushort GatheredFrontGroundOverlay = 1 << 8;
        private const ushort GatheredHasFrontArt = 1 << 9;
        private const ushort GatheredIce = 1 << 10;
        private const ushort GatheredFlow = 1 << 11;
        private const ushort GatheredLava = 1 << 12;

        /// <summary>Console A/B (radiance_gathercache): off gathers every tile fresh, as before.</summary>
        internal static bool GatherCacheEnabled = true;

        /// <summary>One per location, at most two: split screen with a player on each of two maps
        /// alternates between them every frame, and one slot would be thrown away and re-made on
        /// every rebuild. The older of the two goes when a third location arrives.</summary>
        private readonly List<GatheredTileAnswers> _gatheredTilesByLocation = new();
        private int _gatherCacheCopied, _gatherCacheGathered;
        /// <summary>Whole-map gathers by the anchor job since the last report, and their tiles: they
        /// are counted in "asked the game" too, and the line should say so.</summary>
        private int _gatherAnchorFills, _gatherAnchorTiles;

        /// <summary>The cache for this location, emptied when any of its inputs changed.</summary>
        private GatheredTileAnswers? EnsureGatheredTileAnswers(GameLocation location, SurfaceMap? surf)
        {
            var size = location.map?.Layers.Count > 0 ? location.map.Layers[0] : null;
            if (size == null)
                return null;
            int width = size.LayerWidth, height = size.LayerHeight;
            if (width <= 0 || height <= 0)
                return null;
            int labelVersion = CurrentLabelVersion();
            GatheredTileAnswers? answers = null;
            for (int k = 0; k < _gatheredTilesByLocation.Count; k++)
            {
                if (!ReferenceEquals(_gatheredTilesByLocation[k].Location, location))
                    continue;
                answers = _gatheredTilesByLocation[k];
                if (ReferenceEquals(answers.Surface, surf) && answers.LabelVersion == labelVersion
                    && answers.Epoch == MaskEpoch && answers.Width == width && answers.Height == height)
                    return answers;
                _gatheredTilesByLocation.RemoveAt(k);
                break;
            }
            int count = width * height;
            answers = new GatheredTileAnswers
            {
                Location = location, Surface = surf, LabelVersion = labelVersion, Epoch = MaskEpoch,
                Width = width, Height = height,
                Flags = new ushort[count],
                EffectBits = new bool[]?[count], WaterKeepBits = new bool[]?[count],
                BuildingCarveBits = new bool[]?[count], FrontCarveBits = new bool[]?[count],
                IceBits = new bool[]?[count], LavaBits = new bool[]?[count], FlowBits = new bool[]?[count],
                Identity = new int[count],
            };
            if (_gatheredTilesByLocation.Count >= 2)
                _gatheredTilesByLocation.RemoveAt(0);
            _gatheredTilesByLocation.Add(answers);
            return answers;
        }

        /// <summary>Write the cached answers for map tile <paramref name="cell"/> into scratch slot
        /// <paramref name="idx"/>, exactly as <see cref="GatherTile"/> would have.</summary>
        private void CopyGatheredTile(WaterMaskJob job, GatheredTileAnswers answers, int cell, int idx)
        {
            ushort flags = answers.Flags[cell];
            var scratch = _maskScratch;
            scratch.TileEffectBits![idx] = answers.EffectBits[cell];
            scratch.TileWaterKeepBits![idx] = answers.WaterKeepBits[cell];
            scratch.TileBuildingCarveBits![idx] = answers.BuildingCarveBits[cell];
            scratch.TileFrontCarveBits![idx] = answers.FrontCarveBits[cell];
            scratch.TileIceBits![idx] = answers.IceBits[cell];
            scratch.TileLavaBits![idx] = answers.LavaBits[cell];
            scratch.TileFlowBits![idx] = answers.FlowBits[cell];
            scratch.TileLargeSolidFlags![idx] = (flags & GatheredLargeSolid) != 0;
            scratch.TileDeckFlags![idx] = (flags & GatheredDeck) != 0;
            scratch.TileLabeledLiquidFlags![idx] = (flags & GatheredLabeledLiquid) != 0;
            scratch.TileHasBuildingArtFlags![idx] = (flags & GatheredHasBuildingArt) != 0;
            scratch.TileBuildingGroundOverlayFlags![idx] = (flags & GatheredBuildingGroundOverlay) != 0;
            scratch.TileFrontGroundOverlayFlags![idx] = (flags & GatheredFrontGroundOverlay) != 0;
            scratch.TileHasFrontArtFlags![idx] = (flags & GatheredHasFrontArt) != 0;
            scratch.TileIceFlags![idx] = (flags & GatheredIce) != 0;
            scratch.TileFlowFlags![idx] = (flags & GatheredFlow) != 0;
            scratch.TileLavaFlags![idx] = (flags & GatheredLava) != 0;
            if ((flags & GatheredAnyLabeled) != 0)
                job.AnyLabeled = true;
        }

        /// <summary>Remember what <see cref="GatherTile"/> just wrote into scratch slot
        /// <paramref name="idx"/> for map tile <paramref name="cell"/>.</summary>
        private void StoreGatheredTile(GatheredTileAnswers answers, int cell, int idx, bool isWater, bool anyLabeled, int identity)
        {
            answers.Identity[cell] = identity;
            var scratch = _maskScratch;
            ushort flags = GatheredFilled;
            if (isWater) flags |= GatheredIsWater;
            if (anyLabeled) flags |= GatheredAnyLabeled;
            if (scratch.TileLargeSolidFlags![idx]) flags |= GatheredLargeSolid;
            if (scratch.TileDeckFlags![idx]) flags |= GatheredDeck;
            if (scratch.TileLabeledLiquidFlags![idx]) flags |= GatheredLabeledLiquid;
            if (scratch.TileHasBuildingArtFlags![idx]) flags |= GatheredHasBuildingArt;
            if (scratch.TileBuildingGroundOverlayFlags![idx]) flags |= GatheredBuildingGroundOverlay;
            if (scratch.TileFrontGroundOverlayFlags![idx]) flags |= GatheredFrontGroundOverlay;
            if (scratch.TileHasFrontArtFlags![idx]) flags |= GatheredHasFrontArt;
            if (scratch.TileIceFlags![idx]) flags |= GatheredIce;
            if (scratch.TileFlowFlags![idx]) flags |= GatheredFlow;
            if (scratch.TileLavaFlags![idx]) flags |= GatheredLava;
            if ((answers.Flags[cell] & GatheredFilled) == 0)
                answers.FilledCount++;
            answers.Flags[cell] = flags;
            answers.EffectBits[cell] = scratch.TileEffectBits![idx];
            answers.WaterKeepBits[cell] = scratch.TileWaterKeepBits![idx];
            answers.BuildingCarveBits[cell] = scratch.TileBuildingCarveBits![idx];
            answers.FrontCarveBits[cell] = scratch.TileFrontCarveBits![idx];
            answers.IceBits[cell] = scratch.TileIceBits![idx];
            answers.LavaBits[cell] = scratch.TileLavaBits![idx];
            answers.FlowBits[cell] = scratch.TileFlowBits![idx];
        }

        /// <summary>One line for the report: how much of the gather came from memory.</summary>
        private string DescribeGatherCache()
        {
            string held = "no map held";
            foreach (var answers in _gatheredTilesByLocation)
            {
                if (held == "no map held") held = "";
                else held += ", ";
                held += $"{answers.FilledCount} of {answers.Width * answers.Height} tiles of {answers.Location.NameOrUniqueName} held";
            }
            string line = $"  gather cache ({(GatherCacheEnabled ? "on" : "OFF")}): {held}; "
                + $"since the last report {_gatherCacheCopied} tile(s) copied, {_gatherCacheGathered} asked the game "
                + $"(of which {_gatherAnchorTiles} in {_gatherAnchorFills} whole-map fill(s) by the anchor job)";
            _gatherCacheCopied = _gatherCacheGathered = _gatherAnchorFills = _gatherAnchorTiles = 0;
            return line;
        }
    }
}
