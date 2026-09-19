using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The relief replay without a SpriteBatch: the recorded world sprites become vertices here,
    /// grouped by sheet, in one dynamic vertex buffer, and each sheet is one indexed draw.
    ///
    /// <para>The batch road costs what the batch costs. Handed 2,600 sprites it sorts them by
    /// texture (an <c>Array.Sort</c> over an interface comparison per pair), builds each item's
    /// four vertices into its own array, and flushes a run per change of sheet; measured on
    /// 7 Sep the relief row was 1.2 ms of which "building the batch" was about 0.5 and
    /// "submitting the batch" about 0.5, and the frame on this machine is CPU-bound. The sprites
    /// are already recorded in an array we own, with the sheet each one wants, so grouping them
    /// is a bucket per sheet and building their vertices is the same arithmetic the batch does,
    /// once, into a buffer the card reads directly.</para>
    ///
    /// <para>What must stay exactly as the batch had it: the four corners (position, origin
    /// scaled by scale or by the destination rectangle, rotation about the origin), the texture
    /// coordinates (the source rectangle over the sheet's size, swapped for a flip), the tint
    /// (the record's rank in its red and green, see <see cref="SpriteDrawRecorder.TintFor"/>), the
    /// stand-ins (a substituted normal sheet, or the flat texel for what has none) and the
    /// device state the batch would have set for the pass (opaque, point clamp, the relief depth
    /// test, no culling). The vertex z is zero, as the batch writes it; reliefreplay.fx puts the
    /// rank back into z.</para>
    /// </summary>
    internal sealed class ReliefVertexPath : IDisposable
    {
        private const int VerticesPerQuad = 4, IndicesPerQuad = 6;
        /// <summary>Quads one indexed draw may cover with sixteen-bit indices.</summary>
        private const int QuadsPerDraw = 16000;

        private VertexPositionColorTexture[] _vertices = new VertexPositionColorTexture[4 * 4096];
        private DynamicVertexBuffer? _vertexBuffer;
        private IndexBuffer? _indexBuffer;
        private int _vertexBufferCapacity;

        /// <summary>One bucket per sheet seen this frame, kept between frames so the lists are
        /// not made again; a bucket lists the record indices that draw with its sheet.</summary>
        private readonly Dictionary<Texture2D, int> _bucketOf = new(ReferenceEqualityComparer.Instance);
        private readonly List<Texture2D> _bucketTexture = [];
        private readonly List<List<int>> _bucketRecords = [];
        private readonly List<Texture2D> _dropped = [];

        internal int DrawsLastFrame { get; private set; }
        internal int SpritesLastFrame { get; private set; }
        internal int SheetsLastFrame { get; private set; }
        internal string ClosedBecause { get; private set; } = "";

        /// <summary>
        /// Draw records [<paramref name="from"/>, <paramref name="to"/>) into whatever target is
        /// bound, with <paramref name="effect"/> (its MatrixTransform already set by the caller).
        /// Returns how many sprites were drawn, or -1 when this road could not be taken this frame
        /// (the caller then draws them through the batch as before).
        /// </summary>
        internal int Draw(GraphicsDevice device, Effect effect, IReadOnlyList<SpriteDrawRecorder.Record> records,
            int from, int to, Func<Texture2D, SpriteEffects, Texture2D?> substitute, Texture2D flat,
            DepthStencilState depthState)
        {
            try
            {
                // ---- bucket by sheet ----
                foreach (List<int> bucket in _bucketRecords)
                    bucket.Clear();
                int quadCount = 0;
                for (int recordIndex = from; recordIndex < to && recordIndex < records.Count; recordIndex++)
                {
                    SpriteDrawRecorder.Record record = records[recordIndex];
                    if (record.Texture.IsDisposed || record.Alpha <= 0.002f)
                        continue;
                    if (ReferenceEquals(record.Texture, Game1.shadowTexture))
                        continue;
                    Texture2D sheet = substitute(record.Texture, record.Effects) ?? flat;
                    if (!_bucketOf.TryGetValue(sheet, out int bucketIndex))
                    {
                        bucketIndex = _bucketTexture.Count;
                        _bucketOf[sheet] = bucketIndex;
                        _bucketTexture.Add(sheet);
                        _bucketRecords.Add(new List<int>(64));
                    }
                    _bucketRecords[bucketIndex].Add(recordIndex);
                    quadCount++;
                }
                SpritesLastFrame = quadCount;
                DrawsLastFrame = 0;
                SheetsLastFrame = 0;
                if (quadCount == 0)
                    return 0;

                // ---- vertices, bucket after bucket ----
                if (_vertices.Length < quadCount * VerticesPerQuad)
                    _vertices = new VertexPositionColorTexture[Math.Max(quadCount * VerticesPerQuad, _vertices.Length * 2)];
                int vertexCount = 0;
                for (int bucketIndex = 0; bucketIndex < _bucketTexture.Count; bucketIndex++)
                {
                    List<int> bucket = _bucketRecords[bucketIndex];
                    if (bucket.Count == 0)
                        continue;
                    Texture2D sheet = _bucketTexture[bucketIndex];
                    bool isFlat = ReferenceEquals(sheet, flat);
                    // The RECIPROCAL, not the size: the batch multiplies a source rectangle by
                    // Texture2D.TexelWidth (a stored 1/width), and dividing by the width instead
                    // rounds differently in the last bit, which is enough to move a point sample
                    // across a texel boundary. Measured: it was the last 1,781 pixels of the
                    // 2.09 million between this road and the batch's.
                    float texelWidth = 1f / sheet.Width, texelHeight = 1f / sheet.Height;
                    foreach (int recordIndex in bucket)
                    {
                        SpriteDrawRecorder.Record record = records[recordIndex];
                        Color tint = SpriteDrawRecorder.TintFor(recordIndex, record.Alpha);
                        AppendQuad(ref vertexCount, record, tint, isFlat, texelWidth, texelHeight);
                    }
                }

                // ---- upload, then one draw per sheet ----
                EnsureBuffers(device, vertexCount);
                _vertexBuffer!.SetData(_vertices, 0, vertexCount, SetDataOptions.Discard);
                device.SetVertexBuffer(_vertexBuffer);
                device.Indices = _indexBuffer;
                device.BlendState = BlendState.Opaque;
                device.SamplerStates[0] = SamplerState.PointClamp;
                device.DepthStencilState = depthState;
                device.RasterizerState = RasterizerState.CullNone;
                int baseVertex = 0;
                for (int bucketIndex = 0; bucketIndex < _bucketTexture.Count; bucketIndex++)
                {
                    int quadsInBucket = _bucketRecords[bucketIndex].Count;
                    if (quadsInBucket == 0)
                        continue;
                    Texture2D sheet = _bucketTexture[bucketIndex];
                    int drawnQuads = 0;
                    while (drawnQuads < quadsInBucket)
                    {
                        int quadsThisDraw = Math.Min(QuadsPerDraw, quadsInBucket - drawnQuads);
                        foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                        {
                            pass.Apply();
                            // The pass may have rebound the sampler slot; the sheet goes back after it.
                            device.Textures[0] = sheet;
                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, baseVertex, 0, quadsThisDraw * 2);
                            DrawsLastFrame++;
                        }
                        baseVertex += quadsThisDraw * VerticesPerQuad;
                        drawnQuads += quadsThisDraw;
                    }
                    SheetsLastFrame++;
                }
                device.SetVertexBuffer(null);
                device.Indices = null;
                ForgetDisposedBuckets();
                return quadCount;
            }
            catch (Exception exception)
            {
                ClosedBecause = exception.Message;
                return -1;
            }
        }

        /// <summary>The four corners of one sprite, exactly as SpriteBatch would have laid them
        /// down, appended to the vertex array.</summary>
        private void AppendQuad(ref int vertexCount, in SpriteDrawRecorder.Record record, Color tint, bool isFlat,
            float texelWidth, float texelHeight)
        {
            float x, y, width, height, originX, originY;
            float texelLeft, texelTop, texelRight, texelBottom;
            if (isFlat)
            {
                // The stand-in is one texel: the whole of it, and the origin re-expressed in it.
                texelLeft = 0f; texelTop = 0f; texelRight = 1f; texelBottom = 1f;
                float sourceWidth = Math.Max(1, record.Source.Width), sourceHeight = Math.Max(1, record.Source.Height);
                if (record.UsesDestination)
                {
                    x = record.Destination.X; y = record.Destination.Y;
                    width = record.Destination.Width; height = record.Destination.Height;
                }
                else
                {
                    x = (int)record.Position.X; y = (int)record.Position.Y;
                    width = (float)Math.Ceiling(sourceWidth * record.Scale.X);
                    height = (float)Math.Ceiling(sourceHeight * record.Scale.Y);
                }
                // A destination draw scales the origin by destination over source; the flat
                // stand-in's source is one texel, so the origin in flat texels is origin/source
                // and the scale is the footprint itself.
                originX = record.Origin.X / sourceWidth * width;
                originY = record.Origin.Y / sourceHeight * height;
            }
            else
            {
                Rectangle source = record.Source;
                texelLeft = source.X * texelWidth; texelTop = source.Y * texelHeight;
                texelRight = (source.X + source.Width) * texelWidth; texelBottom = (source.Y + source.Height) * texelHeight;
                if (record.UsesDestination)
                {
                    x = record.Destination.X; y = record.Destination.Y;
                    width = record.Destination.Width; height = record.Destination.Height;
                    // The batch's own two cases: a source rectangle with a side of zero scales
                    // that side of the origin by the destination in TEXELS instead.
                    originX = record.Origin.X * (source.Width == 0 ? width * texelWidth : width / source.Width);
                    originY = record.Origin.Y * (source.Height == 0 ? height * texelHeight : height / source.Height);
                }
                else
                {
                    x = record.Position.X; y = record.Position.Y;
                    width = source.Width * record.Scale.X; height = source.Height * record.Scale.Y;
                    originX = record.Origin.X * record.Scale.X;
                    originY = record.Origin.Y * record.Scale.Y;
                }
            }
            // THE TUCK. This game's MonoGame build insets every sprite's texture coordinates by
            // SpriteBatch.TextureTuckAmount texels (GameRunner sets 0.001) before the flip, and a
            // point sample at an exact texel boundary lands on the other side without it: the
            // first capture of this road differed from the batch's on 17% of the relief buffer,
            // scattered over the detailed sprites (grass, flowers) and nowhere else, which is
            // exactly where a boundary sample sits. Read from the static, so a build that changes
            // the amount changes this too.
            float tuckX = SpriteBatch.TextureTuckAmount * texelWidth;
            float tuckY = SpriteBatch.TextureTuckAmount * texelHeight;
            texelLeft += tuckX; texelRight -= tuckX;
            texelTop += tuckY; texelBottom -= tuckY;
            if ((record.Effects & SpriteEffects.FlipVertically) != 0)
                (texelTop, texelBottom) = (texelBottom, texelTop);
            if ((record.Effects & SpriteEffects.FlipHorizontally) != 0)
                (texelLeft, texelRight) = (texelRight, texelLeft);

            ref VertexPositionColorTexture topLeft = ref _vertices[vertexCount];
            ref VertexPositionColorTexture topRight = ref _vertices[vertexCount + 1];
            ref VertexPositionColorTexture bottomLeft = ref _vertices[vertexCount + 2];
            ref VertexPositionColorTexture bottomRight = ref _vertices[vertexCount + 3];
            vertexCount += VerticesPerQuad;
            if (record.Rotation == 0f)
            {
                float left = x - originX, top = y - originY;
                topLeft.Position = new Vector3(left, top, 0f);
                topRight.Position = new Vector3(left + width, top, 0f);
                bottomLeft.Position = new Vector3(left, top + height, 0f);
                bottomRight.Position = new Vector3(left + width, top + height, 0f);
            }
            else
            {
                float sinRotation = (float)Math.Sin(record.Rotation), cosRotation = (float)Math.Cos(record.Rotation);
                float leftOfOrigin = -originX, aboveOrigin = -originY;
                topLeft.Position = new Vector3(x + leftOfOrigin * cosRotation - aboveOrigin * sinRotation,
                    y + leftOfOrigin * sinRotation + aboveOrigin * cosRotation, 0f);
                topRight.Position = new Vector3(x + (leftOfOrigin + width) * cosRotation - aboveOrigin * sinRotation,
                    y + (leftOfOrigin + width) * sinRotation + aboveOrigin * cosRotation, 0f);
                bottomLeft.Position = new Vector3(x + leftOfOrigin * cosRotation - (aboveOrigin + height) * sinRotation,
                    y + leftOfOrigin * sinRotation + (aboveOrigin + height) * cosRotation, 0f);
                bottomRight.Position = new Vector3(x + (leftOfOrigin + width) * cosRotation - (aboveOrigin + height) * sinRotation,
                    y + (leftOfOrigin + width) * sinRotation + (aboveOrigin + height) * cosRotation, 0f);
            }
            topLeft.Color = tint; topRight.Color = tint; bottomLeft.Color = tint; bottomRight.Color = tint;
            topLeft.TextureCoordinate = new Vector2(texelLeft, texelTop);
            topRight.TextureCoordinate = new Vector2(texelRight, texelTop);
            bottomLeft.TextureCoordinate = new Vector2(texelLeft, texelBottom);
            bottomRight.TextureCoordinate = new Vector2(texelRight, texelBottom);
        }

        private void EnsureBuffers(GraphicsDevice device, int vertexCount)
        {
            if (_vertexBuffer == null || _vertexBuffer.IsDisposed || _vertexBuffer.GraphicsDevice != device
                || _vertexBufferCapacity < vertexCount)
            {
                _vertexBuffer?.Dispose();
                _vertexBufferCapacity = Math.Max(vertexCount, Math.Max(_vertexBufferCapacity * 2, 4 * 4096));
                _vertexBuffer = new DynamicVertexBuffer(device, VertexPositionColorTexture.VertexDeclaration,
                    _vertexBufferCapacity, BufferUsage.WriteOnly);
            }
            if (_indexBuffer == null || _indexBuffer.IsDisposed || _indexBuffer.GraphicsDevice != device)
            {
                _indexBuffer?.Dispose();
                var indices = new short[QuadsPerDraw * IndicesPerQuad];
                for (int quad = 0; quad < QuadsPerDraw; quad++)
                {
                    int firstVertex = quad * VerticesPerQuad, firstIndex = quad * IndicesPerQuad;
                    // The batcher's own pattern: two triangles, top-left, top-right, bottom-left,
                    // then top-right, bottom-right, bottom-left.
                    indices[firstIndex] = (short)firstVertex;
                    indices[firstIndex + 1] = (short)(firstVertex + 1);
                    indices[firstIndex + 2] = (short)(firstVertex + 2);
                    indices[firstIndex + 3] = (short)(firstVertex + 1);
                    indices[firstIndex + 4] = (short)(firstVertex + 3);
                    indices[firstIndex + 5] = (short)(firstVertex + 2);
                }
                _indexBuffer = new IndexBuffer(device, IndexElementSize.SixteenBits, indices.Length, BufferUsage.WriteOnly);
                _indexBuffer.SetData(indices);
            }
        }

        /// <summary>A sheet that was disposed (a content reload) leaves the bucket table, so the
        /// table does not hold dead textures alive and does not grow without end.</summary>
        private void ForgetDisposedBuckets()
        {
            _dropped.Clear();
            foreach (Texture2D sheet in _bucketTexture)
                if (sheet.IsDisposed)
                    _dropped.Add(sheet);
            if (_dropped.Count == 0)
                return;
            // Everything goes, rather than an index-by-index compaction that has to keep three
            // lists in step: the buckets are refilled from the records at the top of the next
            // frame anyway, so the only thing thrown away is their spare capacity.
            _bucketOf.Clear();
            _bucketTexture.Clear();
            _bucketRecords.Clear();
        }

        public void Dispose()
        {
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _vertexBuffer = null;
            _indexBuffer = null;
        }
    }
}
