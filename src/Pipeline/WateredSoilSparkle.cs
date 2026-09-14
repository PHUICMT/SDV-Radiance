using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace SDVRadiance
{
    /// <summary>
    /// Wet dirt sparkles in the sun. The game only darkens a watered tile (HoeDirt.state 1 draws
    /// a darker tile); this draws one small additive sprite over it, from a Harmony postfix on
    /// the dirt's own draw, INTO THE GAME'S BATCH at the dirt's depth.
    ///
    /// <para>Why there and not in the lighting pass. The first build of this read a per-tile mask
    /// in the flood shader, and the flood pass sees only the finished pixel: the trunk, the
    /// stump and the crop standing on a watered tile all got sparkles, because the tile under
    /// them was wet. A sprite drawn a hair above the dirt in the game's sorted batch is covered
    /// by everything the dirt is covered by, for the same reason, and costs one sprite per
    /// watered tile on screen, which the batch does not notice. The cloud shadow and the night
    /// multiply the frame after the world is drawn, so a cloud shades the sparkle and the night
    /// puts it out with no help from here.</para>
    ///
    /// <para>The sparkle is an atlas of frames built once: a tile's worth of texels, a few of
    /// them glinting, each glint on its own phase and pace (the snow's two-hash twinkle, baked).
    /// A tile picks its frame from the frozen-aware clock plus its own offset, and flips the
    /// sprite by its own hash, so neighbouring tiles never twinkle in step.</para>
    /// </summary>
    internal static class WateredSoilSparkle
    {
        /// <summary>Frames in the loop. At one frame per four ticks the loop is a little over
        /// four seconds, long enough that the eye does not find it.</summary>
        private const int FrameCount = 64;
        private const int TicksPerFrame = 4;
        /// <summary>Texels per tile side; the sprite is drawn at the game's scale of four.</summary>
        private const int TexelsPerTile = 16;
        /// <summary>Share of texels that glint at all. Most of them draw a faint peak (see
        /// BuildAtlas), so the share is higher than the shader's was for the same look.</summary>
        private const float GlintingShare = 0.075f;
        /// <summary>A hair above the fertiliser (1.9E-08) and well under the crop.</summary>
        private const float LayerDepth = 2.5E-08f;
        private static readonly Vector3 Tint = new(0.88f, 0.94f, 1.0f);
        /// <summary>The flood shader added its sparkle after the shoulder; this one is in the
        /// frame before the lighting, the shoulder, the cloud and the grade all take their share,
        /// so it starts twice as bright. The receipt is the pair of captures on 8 Sep: at 0.55 the
        /// dial at 1.0 looked like the shader's at 0.5.</summary>
        private const float Gain = 1.0f;

        /// <summary>This screen's eased strength for the frame, 0 = draw nothing. Settled by the
        /// pipeline before the world draws (RenderPipeline.UpdateWateredSoilSparkle).</summary>
        internal static float Strength;
        /// <summary>Watered tiles drawn last frame, for the report.</summary>
        internal static int TilesDrawnLastFrame { get; private set; }
        private static int _tilesDrawnThisFrame;
        private static int _frameOfCount = -1;

        private static Texture2D? _atlas;

        /// <summary>Close the count for the frame the report reads.</summary>
        internal static void BeginFrame()
        {
            if (_frameOfCount == Game1.ticks)
                return;
            TilesDrawnLastFrame = _tilesDrawnThisFrame;
            _tilesDrawnThisFrame = 0;
            _frameOfCount = Game1.ticks;
        }

        /// <summary>Harmony postfix on <see cref="HoeDirt.DrawOptimized"/>: the game has just
        /// drawn this tile's dirt (and its watered overlay) into dirt_batch.</summary>
        public static void DrawOptimized_Postfix(HoeDirt __instance, SpriteBatch dirt_batch)
        {
            if (Strength <= 0f || dirt_batch == null || __instance.state.Value != 1)
                return;
            Texture2D atlas = _atlas ??= BuildAtlas(Game1.graphics.GraphicsDevice);
            Vector2 tile = __instance.Tile;
            int tileHash = unchecked((int)tile.X * 73856093 ^ (int)tile.Y * 19349663);
            int frameIndex = (Determinism.Ticks / TicksPerFrame + (tileHash & 0x3F)) % FrameCount;
            SpriteEffects flip = (SpriteEffects)((tileHash >> 6) & 3);
            Vector2 position = Game1.GlobalToLocal(Game1.viewport, tile * 64f);
            float sparkleAmount = Strength * Gain;
            // Alpha 0 with colour is additive under the premultiplied AlphaBlend the game draws
            // with: the sparkle adds light and never covers the dirt.
            var sparkleColour = new Color(Tint.X * sparkleAmount, Tint.Y * sparkleAmount, Tint.Z * sparkleAmount, 0f);
            dirt_batch.Draw(atlas, position, new Rectangle(frameIndex * TexelsPerTile, 0, TexelsPerTile, TexelsPerTile),
                sparkleColour, 0f, Vector2.Zero, 4f, flip, LayerDepth);
            _tilesDrawnThisFrame++;
        }

        /// <summary>One row of frames: each glinting texel rises and falls on its own phase and
        /// pace, one or two full swings per loop so the loop closes without a seam.
        ///
        /// <para>No two glints are the same size or the same brightness. Real specular points on
        /// wet ground are a scatter of catchlights at every angle: a field of identical dots at
        /// one brightness reads as a pattern, which is what the author saw in the first capture.
        /// So each one draws its own peak from a third hash, weighted so most are faint and few
        /// are bright, and the bright ones spill onto their neighbours, which is what makes one
        /// glint look larger than the next when every one of them is a single texel.</para>
        /// </summary>
        private static Texture2D BuildAtlas(GraphicsDevice device)
        {
            const int atlasWidth = FrameCount * TexelsPerTile;
            var texels = new Color[atlasWidth * TexelsPerTile];
            var brightnessPerTexel = new float[atlasWidth * TexelsPerTile];
            for (int y = 0; y < TexelsPerTile; y++)
                for (int x = 0; x < TexelsPerTile; x++)
                {
                    float glintPick = Hash(x + 3.3f, y + 9.1f);
                    if (glintPick < 1f - GlintingShare)
                        continue;
                    float phase = Hash(x + 5.5f, y + 1.2f);
                    int swingsPerLoop = phase < 0.5f ? 1 : 2;
                    // Cubed, so a quarter of the glints are over half strength and the rest trail
                    // away to a shimmer that is barely there.
                    float peakHash = Hash(x + 11.7f, y + 4.4f);
                    float peakBrightness = 0.15f + 0.85f * peakHash * peakHash * peakHash;
                    // A bright glint bleeds onto the four texels around it (never off its own
                    // tile), so it reads as a larger catchlight; a faint one stays a single texel.
                    float spillToNeighbours = Math.Max(0f, peakBrightness - 0.55f) * 0.55f;
                    for (int frameIndex = 0; frameIndex < FrameCount; frameIndex++)
                    {
                        double angle = 2.0 * Math.PI * (frameIndex / (double)FrameCount * swingsPerLoop + phase);
                        float twinkle = 0.5f + 0.5f * (float)Math.Sin(angle);
                        float glintBrightness = twinkle * twinkle * peakBrightness;
                        int frameColumn = frameIndex * TexelsPerTile;
                        AddBrightness(brightnessPerTexel, atlasWidth, x, y, frameColumn, glintBrightness);
                        if (spillToNeighbours <= 0f)
                            continue;
                        float spillBrightness = twinkle * twinkle * spillToNeighbours;
                        AddBrightness(brightnessPerTexel, atlasWidth, x - 1, y, frameColumn, spillBrightness);
                        AddBrightness(brightnessPerTexel, atlasWidth, x + 1, y, frameColumn, spillBrightness);
                        AddBrightness(brightnessPerTexel, atlasWidth, x, y - 1, frameColumn, spillBrightness);
                        AddBrightness(brightnessPerTexel, atlasWidth, x, y + 1, frameColumn, spillBrightness);
                    }
                }
            for (int texel = 0; texel < brightnessPerTexel.Length; texel++)
            {
                float finalBrightness = Math.Min(brightnessPerTexel[texel], 1f);
                if (finalBrightness > 0f)
                    texels[texel] = new Color(finalBrightness, finalBrightness, finalBrightness, 0f);
            }
            var atlas = VramTally.Track(new Texture2D(device, atlasWidth, TexelsPerTile, false, SurfaceFormat.Color), "watered soil sparkle");
            atlas.SetData(texels);
            return atlas;
        }

        /// <summary>Add to one texel of one frame, dropping anything outside the tile so a glint
        /// on the edge never bleeds into the tile beside it.</summary>
        private static void AddBrightness(float[] brightnessPerTexel, int atlasWidth, int x, int y, int frameColumn, float amount)
        {
            if (x < 0 || x >= TexelsPerTile || y < 0 || y >= TexelsPerTile)
                return;
            brightnessPerTexel[y * atlasWidth + frameColumn + x] += amount;
        }

        /// <summary>The shader's hash, so the field looks like the snow's.</summary>
        private static float Hash(float x, float y)
        {
            double value = Math.Sin(x * 12.9898 + y * 78.233) * 43758.5453;
            return (float)(value - Math.Floor(value));
        }
    }
}
