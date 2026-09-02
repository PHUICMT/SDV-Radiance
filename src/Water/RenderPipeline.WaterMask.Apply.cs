using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - the APPLY stage of a water mask rebuild, spread over three frames.
    ///
    /// <para>A finished compose is four textures' worth of pixels (mask, signed distance, real
    /// shore distance, fall distance; about 2.6 MB for a window at zoom 0.75), and uploading them
    /// in one frame cost 0.8 ms of that frame on the Town river, measured 2026-09-03, half of what
    /// a rebuild spent on the main thread once the gather was cached. The upload goes into the
    /// spare of each pair, which nothing reads until the swap, so nothing requires it to land in
    /// one frame: the two large Color textures take a frame each and the two small Alpha8 ones
    /// share a third. The old mask and its origin stay published, a consistent pair, until the
    /// last step, when all four pairs swap together and the origin moves with them. Every texel
    /// still arrives exactly once, in the same bytes; only the frame it arrives in moves.</para>
    ///
    /// <para>Whole textures, never row bands. Bands were tried first (128 rows per frame) and
    /// measured WORSE: 0.46 ms per band against 0.77 ms for the whole window, five bands to a
    /// window. A SetData with a rectangle is a glTexSubImage2D, and the driver waits for every
    /// queued read of that texture before it lets the copy start; a SetData of the whole texture
    /// is a glTexImage2D, which the driver satisfies with fresh storage and no wait. The spare
    /// was the front two frames ago and the card may still be reading it, so only the whole-texture
    /// road is free of the wait.</para>
    ///
    /// <para>The job stays pending while its textures upload, which is what keeps the next gather
    /// from overwriting the composed buffers underneath the upload. A window that moves on
    /// mid-apply is discarded like one that moves on mid-compose; a half-uploaded set of spares
    /// is never shown and the next apply overwrites all of it.</para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Console A/B (radiance_applyspread): off uploads all four textures in one frame, as before.</summary>
        internal static bool ApplySpreadEnabled = true;

        /// <summary>Upload the next of <paramref name="job"/>'s composed textures into its spare; on
        /// the last one, swap every pair and publish the new mask identity. True when the job is applied.</summary>
        private bool ApplyWaterMaskStep(WaterMaskJob job)
        {
            int tilesW = job.TileWidth, tilesH = job.TileHeight;
            int pw = tilesW * 16, ph = tilesH * 16;
            int pcount = pw * ph;

            if (job.ApplyTexturesDone == 0 && !job.WaterAny)
                FillEmptyWaterMask(job, pcount);

            // The two ! carry the invariant the one-frame upload always relied on: a job does not
            // reach Apply without the compose pass having filled both buffers.
            int step = job.ApplyTexturesDone;
            do
            {
                switch (step)
                {
                    case 0:
                        TextureDoubleBuffer.EnsureSpare(_device, ref _waterMaskSpare, pw, ph, SurfaceFormat.Color, "water mask");
                        _waterMaskSpare!.SetData(_waterMaskPixels!, 0, pcount);
                        break;
                    case 1:
                        if (_maskScratch.PlungeChurnPixels != null)
                        {
                            TextureDoubleBuffer.EnsureSpare(_device, ref _waterPlungeChurnSpare, pw, ph, SurfaceFormat.Color, null);
                            _waterPlungeChurnSpare!.SetData(_maskScratch.PlungeChurnPixels, 0, pcount * FallDistanceBytesPerTexel);
                        }
                        break;
                    default:
                        TextureDoubleBuffer.EnsureSpare(_device, ref _waterSignedDistanceSpare, pw, ph, SurfaceFormat.Alpha8, null);
                        _waterSignedDistanceSpare!.SetData(_maskScratch.WaterSignedDistancePixels!, 0, pcount);
                        if (_maskScratch.RealShoreDistancePixels != null)
                        {
                            TextureDoubleBuffer.EnsureSpare(_device, ref _waterRealShoreDistanceSpare, pw, ph, SurfaceFormat.Alpha8, null);
                            _waterRealShoreDistanceSpare!.SetData(_maskScratch.RealShoreDistancePixels, 0, pcount);
                        }
                        break;
                }
                step++;
            }
            while (!ApplySpreadEnabled && step < 3);
            job.ApplyTexturesDone = step;
            if (step < 3)
                return false;

            PublishWaterMask(job, pw, ph);
            return true;
        }

        /// <summary>The last texture landed: swap every pair to the freshly written texture and move
        /// the mask identity, origin and near-water flags to the new window, all in one frame.</summary>
        private void PublishWaterMask(WaterMaskJob job, int pw, int ph)
        {
            int tilesW = job.TileWidth, tilesH = job.TileHeight;
            int count = tilesW * tilesH;

            _lastWaterLocation = job.Location;
            _lastWaterTileX = job.StartTileX;
            _lastWaterTileY = job.StartTileY;
            _lastWaterBuildTick = Game1.ticks;
            _lastWaterHookVersion = job.WaterDrawHookVersion;
            _lastWaterLabelVersion = job.LabelVersion;
            _lastWaterEpoch = job.Epoch;
            _hasWaterInMask = job.WaterAny;
            // Published for the player colour bake, which runs before this pipeline gets a look
            // at the frame. One compose late is fine: its reader gates on the same flag.
            ShadowRenderer.WaterOnScreen = job.WaterAny;

            if (job.WaterAny)
            {
                // Take this screen's own copy of the water flags before the next rebuild starts
                // overwriting the shared gather buffer with somebody else's window. The composed
                // verdict is preferred: it also carries water that only a label brought in (the
                // desert oasis), which the gather flags alone never see - and the near-water gates
                // reading this array culled every sprite stamp there, so the ripple ran over the
                // palms. The gather flags stay as the fallback for a job that stopped before Pass D.
                bool[]? nearWaterFlags = job.TileHasEffectWaterFlags ?? _waterTileFlags;
                if (nearWaterFlags != null && nearWaterFlags.Length >= count)
                {
                    if (_waterTilesInMask == null || _waterTilesInMask.Length < count)
                        _waterTilesInMask = new bool[count];
                    Array.Copy(nearWaterFlags, _waterTilesInMask, count);
                    _waterTilesVersion++;
                }
            }
            else if (_waterTilesInMask != null)
            {
                // No water in this window, so nothing is near any: the "is there water by this
                // sprite" test must agree with the textures it is cleared alongside.
                Array.Clear(_waterTilesInMask, 0, Math.Min(count, _waterTilesInMask.Length));
                _waterTilesVersion++;
            }

            // Every upload went into the pair's spare, never into the texture the card may still
            // be reading: SetData on an in-use texture makes the driver wait out every queued draw
            // that samples it, which is where this window's 300x worst frames were going. Same
            // pixels either way; only the wait goes. See TextureDoubleBuffer.
            TextureDoubleBuffer.Swap(ref _waterMask, ref _waterMaskSpare);
            TextureDoubleBuffer.Swap(ref _waterSignedDistanceTexture, ref _waterSignedDistanceSpare);
            if (_maskScratch.RealShoreDistancePixels != null)
                TextureDoubleBuffer.Swap(ref _waterRealShoreDistanceTexture, ref _waterRealShoreDistanceSpare);
            if (_maskScratch.PlungeChurnPixels != null)
                TextureDoubleBuffer.Swap(ref _waterPlungeChurnTexture, ref _waterPlungeChurnSpare);
            _waterMaskPixelSize = new Vector2(tilesW, tilesH);

            if (!job.WaterAny)
                return;
            if (MaskView)
                BuildMaskViewTex(pw, ph);
            // Keep the label-verdict overlay in step with the mask it judges: a rebuild on a
            // tile crossing would otherwise leave yesterday's verdict floating over new water.
            if (DebugChannel == DebugOverlayChannel.LabelDiff)
                VerifyLabels(Game1.currentLocation, worstToList: 0);
        }

        /// <summary>Fill the buffers with an EMPTY mask for a window with no water, sized and
        /// anchored like any other, so the shader reads "no water here" instead of the previous
        /// window's pattern once it lands. The ORIGIN moves to this window with the publish, so
        /// the texture has to move with it: a mask must always agree with its own origin. All
        /// four fields are cleared together: R and G decide coverage, and the SDF's 128 is its
        /// zero, so leaving a stale distance field behind would still shade a phantom shore.</summary>
        private void FillEmptyWaterMask(WaterMaskJob job, int pcount)
        {
            if (_waterMaskPixels == null || _waterMaskPixels.Length < pcount) _waterMaskPixels = new Color[pcount];
            if (_maskScratch.WaterSignedDistancePixels == null || _maskScratch.WaterSignedDistancePixels.Length < pcount) _maskScratch.WaterSignedDistancePixels = new byte[pcount];
            if (_maskScratch.RealShoreDistancePixels == null || _maskScratch.RealShoreDistancePixels.Length < pcount) _maskScratch.RealShoreDistancePixels = new byte[pcount];
            Array.Clear(_waterMaskPixels, 0, pcount);
            // 0 = as far from water as this encoding can say. It used to be 128, which means
            // "exactly on the waterline", so a window with NO WATER IN IT told the shader that
            // every single pixel was standing at the water's edge - and the wet-rim term, whose
            // whole job is to darken the last few texels of land before the water, then had
            // licence to darken the entire screen. Nothing is near water here; say so.
            Array.Clear(_maskScratch.WaterSignedDistancePixels, 0, pcount);
            Array.Clear(_maskScratch.RealShoreDistancePixels, 0, pcount);
            // No water, so nothing is near a fall: the fall-distance field reads "far" everywhere.
            if (_maskScratch.PlungeChurnPixels == null || _maskScratch.PlungeChurnPixels.Length < pcount * FallDistanceBytesPerTexel)
                _maskScratch.PlungeChurnPixels = new byte[pcount * FallDistanceBytesPerTexel];
            FillFallDistanceFar(_maskScratch.PlungeChurnPixels, 0, pcount);
        }
    }
}
