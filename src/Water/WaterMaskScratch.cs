namespace SDVRadiance
{
    /// <summary>
    /// The water-mask builder's working memory: everything the gather phase writes down for the
    /// compose phase to chew on, plus the compose phase's own scratch grids.
    ///
    /// <para>These lived on <c>RenderPipeline</c> as twenty-eight loose private fields, which meant
    /// every one of the fourteen files of that partial class could see and touch them, and a reader
    /// had no way to tell them apart from the pipeline's own state. They are not pipeline state:
    /// they are one rebuild's notes, meaningless outside it.</para>
    ///
    /// <para>ONE INSTANCE, DELIBERATELY, and it needs no locking for the same reason it never did:
    /// rebuild jobs are strictly serialized (a gather only starts when no job is in flight), so
    /// exactly one rebuild is ever reading or writing these. See the file header of
    /// RenderPipeline.WaterMask.Async.cs. Handing each job its own copy would be safer in the
    /// abstract and would allocate twenty-eight arrays per tile crossing, which is the stutter the
    /// async split existed to remove.</para>
    ///
    /// <para>Grown as needed and kept between rebuilds; nothing here is cleared on a location
    /// change, because every pass writes each cell before reading it.</para>
    /// </summary>
    internal sealed class WaterMaskScratch
    {
        // ---- gathered per-tile inputs (main thread writes, worker reads) ----

        /// <summary>Effect-channel art classification per tile (null = none).</summary>
        public bool[]?[]? TileEffectBits;
        /// <summary>Labelled water tile: pixels to KEEP in the effect channel (null = all).</summary>
        public bool[]?[]? TileWaterKeepBits;
        /// <summary>Buildings-layer opacity bits (null = no art).</summary>
        public bool[]?[]? TileBuildingCarveBits;
        /// <summary>Front-layer opacity bits.</summary>
        public bool[]?[]? TileFrontCarveBits;
        /// <summary>Near-solid (&gt;=230/256 opaque) Buildings/Front art.</summary>
        public bool[]? TileLargeSolidFlags;
        /// <summary>Height Framework DECK tile.</summary>
        public bool[]? TileDeckFlags;
        /// <summary>Overlay art here is LABELLED liquid: resolved per pixel, skip the tile verdict.</summary>
        public bool[]? TileLabeledLiquidFlags;
        /// <summary>Any Buildings art at all (arch fill test).</summary>
        public bool[]? TileHasBuildingArtFlags;
        /// <summary>Buildings art over water that a label calls ALL ground.</summary>
        public bool[]? TileBuildingGroundOverlayFlags;
        /// <summary>Same for Front + every AlwaysFront layer here.</summary>
        public bool[]? TileFrontGroundOverlayFlags;
        /// <summary>Per-pixel ice from the label (null = use the tile verdict).</summary>
        public bool[]?[]? TileIceBits;
        /// <summary>Per-pixel lava from the label.</summary>
        public bool[]?[]? TileLavaBits;
        /// <summary>Per-pixel flowing (class 10) from the label.</summary>
        public bool[]?[]? TileFlowBits;
        /// <summary>This water tile touches a non-water tile (or the mask edge).</summary>
        public bool[]? TileNearLandFlags;
        public bool[]? TileHasFrontArtFlags;
        /// <summary>HF label class 9: frozen — reflection, no ripple.</summary>
        public bool[]? TileIceFlags;
        /// <summary>HF label class 10: flowing/waterfall — ripple, no reflection.</summary>
        public bool[]? TileFlowFlags;
        /// <summary>HF label class 11: lava — slow molten flow, self-glow, no reflection.</summary>
        public bool[]? TileLavaFlags;
        /// <summary>Per-tile: has any effect-water pixel (for the body-size flood fill).</summary>
        public bool[]? TileHasEffectWaterFlags;
        /// <summary>Per-tile 0..255 wave scale by water-body size (small = calmer).</summary>
        public byte[]? TileCalmnessValues;

        // ---- march (shoreline reachability) ----

        public bool[]? MarchOutsideFlags;
        public bool[]? MarchCarvedBits;
        public int[]? MarchFloodStack;
        public bool[]? SpeckVisitedFlags;
        public int[]? SpeckComponentMembers;

        // ---- Pass F (SDF) ----

        /// <summary>Signed shore distance, 128 = waterline, ±4 per texel.</summary>
        public byte[]? WaterSignedDistancePixels;
        /// <summary>Chamfer scratch: distance to land (inside water).</summary>
        public ushort[]? DistanceToLand;
        /// <summary>Chamfer scratch: distance to water (outside).</summary>
        public ushort[]? DistanceToWater;
    }
}
