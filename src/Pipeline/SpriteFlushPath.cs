using System;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// A cheaper road for every run of one texture the game's sprite batcher hands the card.
    ///
    /// <para>MonoGame 3.8's SpriteBatcher flushes each run through DrawUserIndexedPrimitives, which
    /// on DesktopGL means, per run: unbind both buffers, pin the vertex array and the index array,
    /// hand the card client-side pointers for the three vertex attributes again (the pointers
    /// change with every pin, so nothing is cached), and glDrawElements with client-side indices,
    /// which the driver must walk to learn which vertices to copy. Measured on the farm at 75 %
    /// zoom on a 3440-wide window, a run costs about a microsecond whatever is in it, and a frame
    /// has 4,700 of them: the world step, this mod's relief replay, the interface. Nearly half of
    /// a 10.7 ms frame is that bookkeeping.</para>
    ///
    /// <para>This prefix draws the same run from a dynamic vertex buffer and a fixed index buffer
    /// instead: one orphaning upload of the run's vertices, and a draw whose attribute pointers
    /// the device recognises as the ones it already set. The texture, the effect passes and the
    /// primitive count are exactly the batcher's own. SpriteMaster ships the same idea as its
    /// "Optimize" option and this steps aside when that mod is loaded.</para>
    /// </summary>
    internal static class SpriteFlushPath
    {
        /// <summary>The switch: radiance_flushpath vbo|user. Off means the batcher's own road.</summary>
        internal static bool Enabled;
        /// <summary>Why the road is closed, for the report; empty when it is open.</summary>
        internal static string ClosedBecause = "";
        internal static long RunsTaken;

        private static AccessTools.FieldRef<object, VertexPositionColorTexture[]>? _vertexArrayOf;
        private static AccessTools.FieldRef<object, short[]>? _indexOf;
        private static AccessTools.FieldRef<object, GraphicsDevice>? _deviceOf;
        private static DynamicVertexBuffer? _vertices;
        private static IndexBuffer? _indices;
        private static GraphicsDevice? _buffersDevice;
        private static int _vertexCapacity;
        /// <summary>Where the next run lands in the vertex ring, in vertices. Runs are appended
        /// without overwriting until the ring is full, then the ring is orphaned once and filled
        /// from the top again: one orphaning per few thousand runs rather than one per run, which
        /// was measured slower than the batcher's own road (10.6 against 9.2 ms a frame).</summary>
        private static int _ringOffset;
        /// <summary>Eight full batches of vertices: the ring wraps a few times a frame at most.</summary>
        private const int RingBatches = 8;

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SpriteMaster"))
                {
                    ClosedBecause = "SpriteMaster is loaded and drives this road itself";
                    return;
                }
                Type? batcherType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatcher");
                var flush = batcherType == null ? null : AccessTools.Method(batcherType, "FlushVertexArray");
                if (batcherType == null || flush == null)
                {
                    ClosedBecause = "SpriteBatcher.FlushVertexArray was not found";
                    return;
                }
                _vertexArrayOf = AccessTools.FieldRefAccess<object, VertexPositionColorTexture[]>(AccessTools.Field(batcherType, "_vertexArray"));
                _indexOf = AccessTools.FieldRefAccess<object, short[]>(AccessTools.Field(batcherType, "_index"));
                _deviceOf = AccessTools.FieldRefAccess<object, GraphicsDevice>(AccessTools.Field(batcherType, "_device"));
                // Low priority: the per-texture sampler prefix in SheetUpscaler must have run
                // before the draw is issued here.
                harmony.Patch(flush, prefix: new HarmonyMethod(typeof(SpriteFlushPath), nameof(FlushVertexArray_Prefix)) { priority = Priority.Low });
            }
            catch (Exception ex)
            {
                ClosedBecause = "the patch failed: " + ex.Message;
                monitor.Log($"sprite flush path: {ClosedBecause}", LogLevel.Debug);
            }
        }

        private static bool FlushVertexArray_Prefix(object __instance, int start, int end, Effect? effect, Texture? texture)
        {
            if (!Enabled || start == end || _vertexArrayOf == null || _indexOf == null || _deviceOf == null)
                return true;
            try
            {
                GraphicsDevice device = _deviceOf(__instance);
                VertexPositionColorTexture[] vertexArray = _vertexArrayOf(__instance);
                short[] indexArray = _indexOf(__instance);
                int vertexCount = end - start;
                if (vertexCount <= 0 || vertexCount > vertexArray.Length)
                    return true;
                if (!EnsureBuffers(device, vertexArray.Length, indexArray))
                    return true;
                // The batcher writes every run from the start of its array, so the run is the
                // first vertexCount entries. They are appended to the ring; when they would not
                // fit, the ring is orphaned (Discard) and the run starts it again.
                int stride = VertexPositionColorTexture.VertexDeclaration.VertexStride;
                SetDataOptions options = SetDataOptions.NoOverwrite;
                if (_ringOffset + vertexCount > _vertexCapacity)
                {
                    _ringOffset = 0;
                    options = SetDataOptions.Discard;
                }
                _vertices!.SetData(_ringOffset * stride, vertexArray, 0, vertexCount, stride, options);
                device.SetVertexBuffer(_vertices);
                device.Indices = _indices;
                int primitiveCount = vertexCount / 4 * 2;
                if (effect != null)
                {
                    foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        device.Textures[0] = texture;
                        device.DrawIndexedPrimitives(PrimitiveType.TriangleList, _ringOffset, 0, primitiveCount);
                    }
                }
                else
                {
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, _ringOffset, 0, primitiveCount);
                }
                _ringOffset += vertexCount;
                RunsTaken++;
                return false;
            }
            catch (Exception ex)
            {
                // One failure closes the road for the session; the batcher's own road is always there.
                Enabled = false;
                ClosedBecause = "a draw failed: " + ex.Message;
                return true;
            }
        }

        /// <summary>The two buffers, sized to the batcher's array and refilled when it grows or the
        /// device changes. The index pattern is the batcher's own, copied from its array.</summary>
        private static bool EnsureBuffers(GraphicsDevice device, int vertexCapacity, short[] indexArray)
        {
            if (_vertices != null && !_vertices.IsDisposed && ReferenceEquals(_buffersDevice, device) && _vertexCapacity >= vertexCapacity * RingBatches
                && _indices != null && !_indices.IsDisposed && _indices.IndexCount >= indexArray.Length)
                return true;
            _vertices?.Dispose();
            _indices?.Dispose();
            _vertexCapacity = vertexCapacity * RingBatches;
            _vertices = new DynamicVertexBuffer(device, VertexPositionColorTexture.VertexDeclaration, _vertexCapacity, BufferUsage.WriteOnly);
            _indices = new IndexBuffer(device, IndexElementSize.SixteenBits, indexArray.Length, BufferUsage.WriteOnly);
            _indices.SetData(indexArray);
            _buffersDevice = device;
            _ringOffset = 0;
            return true;
        }

        internal static void Dispose()
        {
            _vertices?.Dispose(); _vertices = null;
            _indices?.Dispose(); _indices = null;
            _buffersDevice = null;
            _vertexCapacity = 0;
            _ringOffset = 0;
        }
    }
}
