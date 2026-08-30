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
        /// <summary>The tile is the interior of a built fish pond: Pass E tags its alpha VESSEL (240).</summary>
        public bool[]? TilePondFlags;
        /// <summary>HF label class 11: lava — slow molten flow, self-glow, no reflection.</summary>
        public bool[]? TileLavaFlags;
        /// <summary>Per-tile: has any effect-water pixel (for the body-size flood fill).</summary>
        public bool[]? TileHasEffectWaterFlags;
        /// <summary>Per-tile 0..255 wave scale by water-body size (small = calmer).</summary>
        public byte[]? TileCalmnessValues;

        // ---- march (shoreline reachability) ----

        /// <summary>Effect texels the map-art and entity carves actually removed. The pocket
        /// pass may only act on water whose whole boundary sits in here, which is what tells a
        /// gap inside drawn art apart from a small pond with land around it.</summary>
        public bool[]? ArtCarvedFlags;
        /// <summary>Pocket pass: texels the component walk has already reached.</summary>
        public bool[]? PocketVisitedFlags;
        /// <summary>The water as it would be if the art standing in it were not there: the
        /// effect channel with every art carve filled back in. A bridge is a hole in the effect
        /// mask, and a hole has an edge, which is how a bridge came to be treated as a shore.
        /// The distance field built from THIS has an edge only where water meets real land.</summary>
        public bool[]? RealShoreWaterBits;
        /// <summary>Encoded exactly like WaterSignedDistancePixels, but measured to the real
        /// shore only.</summary>
        public byte[]? RealShoreDistancePixels;
        public bool[]? MarchOutsideFlags;
        public bool[]? MarchCarvedBits;
        public int[]? MarchFloodStack;
        public bool[]? SpeckVisitedFlags;
        public int[]? SpeckComponentMembers;

        // ---- Pass E2 (plunge churn) ----

        /// <summary>Four bytes per texel. Red: how far below a falling face the water sits, 0 at
        /// the foot of the fall to 255 six tiles away or nowhere near one. Green: how far above
        /// the face in its own column, 0 on the lip to 255 two tiles up or with no fall below.</summary>
        public byte[]? PlungeChurnPixels;
        /// <summary>Vertical scratch for the same pass: rows since the last falling texel above.</summary>
        public int[]? PlungeRowsSinceFall;

        // ---- Pass F (SDF) ----

        /// <summary>Signed shore distance, 128 = waterline, ±4 per texel.</summary>
        public byte[]? WaterSignedDistancePixels;
        /// <summary>Chamfer scratch: distance to land (inside water).</summary>
        public ushort[]? DistanceToLand;
        /// <summary>Chamfer scratch: distance to water (outside).</summary>
        public ushort[]? DistanceToWater;
    }
}
