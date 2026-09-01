using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// Upload pixel data into the texture the card is NOT reading, and hand the freshly written
    /// one back as the new front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SetData into a texture the GPU may still be sampling does not overlap with anything: the
    /// driver waits for every queued draw that reads the texture before it lets the copy start,
    /// and the whole frame stands still for the wait. The cost has no CPU-side signature of its
    /// own, so it shows up only in the WORST column of the frame report - the water mask window
    /// averaged 0.008 ms and its worst frame was 2.392, three hundred times the average, on an
    /// upload of about a quarter of a megabyte.
    /// </para>
    /// <para>
    /// The cascades' emitter grid met exactly this and fixed it locally with two textures
    /// alternated per build: same pixels either way, only the wait goes. This is that fix made
    /// available to every mask this mod uploads. The spare is written while the card still owns
    /// the front; the swap is a reference assignment; the texture handed back carries the new
    /// content and nothing ever waited on it.
    /// </para>
    /// <para>
    /// The price is one extra texture per mask, which the VRAM tally reports honestly under the
    /// same bucket. The masks this is for are tile-resolution windows of a few hundred kilobytes;
    /// a pair is still nothing beside one full-resolution render target.
    /// </para>
    /// </remarks>
    internal static class TextureDoubleBuffer
    {
        /// <summary>Write <paramref name="data"/> into the spare of a texture pair and return the
        /// written texture, which becomes the caller's new front. The old front is left untouched
        /// for the card to finish with, and becomes the spare for the next upload.</summary>
        /// <param name="spare">The pair's resting texture. Recreated here whenever the window's
        /// size or format has changed; holds the retired front after the call.</param>
        /// <param name="front">The texture the caller is currently showing. Never written to.</param>
        /// <param name="tallyBucket">VRAM tally bucket, or null for the textures the tally never
        /// counted before this existed - what is tracked stays exactly what was tracked.</param>
        /// <param name="count">Element count for SetData, which is not always width times height:
        /// a byte[] filling a four-byte format passes four elements per texel.</param>
        internal static Texture2D UploadIntoSpare<T>(GraphicsDevice device, ref Texture2D? spare,
            Texture2D? front, int width, int height, SurfaceFormat format, string? tallyBucket,
            T[] data, int count) where T : struct
        {
            if (spare == null || spare.IsDisposed || spare.Width != width || spare.Height != height
                || spare.Format != format)
            {
                spare?.Dispose();
                var made = new Texture2D(device, width, height, false, format);
                spare = tallyBucket != null ? VramTally.Track(made, tallyBucket) : made;
            }
            spare.SetData(data, 0, count);
            Texture2D written = spare;
            // Yesterday's front is tomorrow's spare. A front of the wrong size is kept anyway and
            // dealt with by the size check above on the next upload; disposing it HERE would pull
            // it out from under the draw that is still reading it, which is the very wait this
            // exists to remove, done as a crash instead.
            spare = front != null && !front.IsDisposed ? front : null;
            return written;
        }
    }
}
