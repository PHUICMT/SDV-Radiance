using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// <see cref="Texture2D.SetData{T}(T[], int, int)"/> without the wait at the end of it.
    /// </summary>
    /// <remarks>
    /// <para>The game's MonoGame ends every texture upload with <c>GL.Finish()</c>
    /// (Texture2D.PlatformSetDataBody, both the whole-level and the rectangle path), which holds
    /// the game until the graphics card has finished EVERY draw queued so far, this frame's and
    /// the ones before it. On a fast card that is a fraction of a millisecond and nobody saw it.
    /// On a slow one it is the whole frame: with the card clocked down to a weak laptop's speed
    /// (14 Oct 2026) the radiance cascades' emitter upload, a texture of a few thousand texels,
    /// took 117 ms on average and 181 ms at worst, every time it ran, standing still on the farm
    /// at noon. The double-buffered textures (<see cref="TextureDoubleBuffer"/>) could not help:
    /// the price is the Finish, not the texture.</para>
    /// <para>This makes the same calls in the same order and leaves the Finish out. It is not
    /// needed: glTexImage2D and glTexSubImage2D copy the caller's pixels before they return, so the
    /// array can be reused at once, and every later draw that samples the texture is queued after
    /// the upload on the one context. MonoGame finishes because it allows uploads from another
    /// thread; this is only ever called from the game's own, and anything else goes to SetData.</para>
    /// <para>Anything this cannot do exactly as MonoGame would (another thread, a compressed or
    /// unknown format, a count that is not the whole level, a texture with no GL name, a platform
    /// whose GL table did not resolve) goes to SetData unchanged.</para>
    /// </remarks>
    internal static unsafe class TextureUpload
    {
        private const int Texture2DTarget = 0x0DE1;
        private const int TextureBinding2D = 0x8069;
        private const int UnpackAlignment = 0x0CF5;

        /// <summary>Off sends every upload back through SetData, for an A/B (radiance_upload).</summary>
        internal static bool Enabled = true;
        /// <summary>Uploads made here and uploads handed to SetData, for radiance_report.</summary>
        internal static long DirectUploads, SetDataUploads;
        /// <summary>Why the direct path is not available, or empty when it is.</summary>
        internal static string Unavailable { get; private set; } = "";

        private static bool _resolved;
        private static delegate* unmanaged<int, int, void> _bindTexture;
        private static delegate* unmanaged<int, int, void> _pixelStore;
        private static delegate* unmanaged<int, int*, void> _getIntegerv;
        private static delegate* unmanaged<int, int, int, int, int, int, int, int, IntPtr, void> _texImage2D;
        private static delegate* unmanaged<int, int, int, int, int, int, int, int, IntPtr, void> _texSubImage2D;
        private static FieldInfo? _glTexture, _glInternalFormat, _glFormat, _glType;
        private static Func<bool>? _onGameThread;

        /// <summary>The whole of level 0, as <c>texture.SetData(data, startIndex, count)</c>.</summary>
        internal static void SetData<T>(Texture2D texture, T[] data, int startIndex, int count) where T : struct
        {
            if (!CanUpload(texture, data, count, texture.Width, texture.Height, out int name))
            {
                SetDataUploads++;
                texture.SetData(data, startIndex, count);
                return;
            }
            Upload(texture, name, data, startIndex, wholeLevel: true, Rectangle.Empty);
        }

        /// <summary>One rectangle of level 0, as <c>texture.SetData(0, rect, data, startIndex, count)</c>.</summary>
        internal static void SetData<T>(Texture2D texture, Rectangle rect, T[] data, int startIndex, int count) where T : struct
        {
            if (!CanUpload(texture, data, count, rect.Width, rect.Height, out int name)
                || rect.X < 0 || rect.Y < 0 || rect.Right > texture.Width || rect.Bottom > texture.Height)
            {
                SetDataUploads++;
                texture.SetData(0, rect, data, startIndex, count);
                return;
            }
            Upload(texture, name, data, startIndex, wholeLevel: false, rect);
        }

        private static void Upload<T>(Texture2D texture, int name, T[] data, int startIndex, bool wholeLevel, Rectangle rect) where T : struct
        {
            int internalFormat = Convert.ToInt32(_glInternalFormat!.GetValue(texture));
            int format = Convert.ToInt32(_glFormat!.GetValue(texture));
            int type = Convert.ToInt32(_glType!.GetValue(texture));
            GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr pixels = pinned.AddrOfPinnedObject() + startIndex * Marshal.SizeOf<T>();
                int bound = 0;
                _getIntegerv(TextureBinding2D, &bound);
                if (bound != name)
                    _bindTexture(Texture2DTarget, name);
                // One byte: always correct, whatever the row length. MonoGame sets this before
                // every upload of its own, so nothing relies on the value left behind.
                _pixelStore(UnpackAlignment, 1);
                if (wholeLevel)
                    _texImage2D(Texture2DTarget, 0, internalFormat, texture.Width, texture.Height, 0, format, type, pixels);
                else
                    _texSubImage2D(Texture2DTarget, 0, rect.X, rect.Y, rect.Width, rect.Height, format, type, pixels);
                if (bound != name)
                    _bindTexture(Texture2DTarget, bound);
                DirectUploads++;
            }
            finally
            {
                pinned.Free();
            }
        }

        private static bool CanUpload<T>(Texture2D texture, T[] data, int count, int width, int height, out int name) where T : struct
        {
            name = -1;
            if (!Enabled || !Resolve() || texture.IsDisposed || texture.LevelCount < 1)
                return false;
            if (_onGameThread != null && !_onGameThread())
                return false;
            int bytesPerTexel = BytesPerTexel(texture.Format);
            if (bytesPerTexel == 0 || (long)count * Marshal.SizeOf<T>() != (long)width * height * bytesPerTexel
                || count > data.Length)
                return false;
            name = (int)_glTexture!.GetValue(texture)!;
            return name > 0;
        }

        /// <summary>The uncompressed formats this mod uploads, and their size. Anything else is
        /// left to SetData, which knows every format there is.</summary>
        private static int BytesPerTexel(SurfaceFormat format) => format switch
        {
            SurfaceFormat.Color or SurfaceFormat.Bgra32 or SurfaceFormat.Bgr32 or SurfaceFormat.Rgba1010102
                or SurfaceFormat.Rg32 or SurfaceFormat.Single or SurfaceFormat.HalfVector2 => 4,
            SurfaceFormat.Alpha8 => 1,
            SurfaceFormat.HalfSingle or SurfaceFormat.Bgr565 or SurfaceFormat.Bgra5551 or SurfaceFormat.Bgra4444 => 2,
            SurfaceFormat.HalfVector4 or SurfaceFormat.Rgba64 or SurfaceFormat.Vector2 => 8,
            SurfaceFormat.Vector4 => 16,
            _ => 0,
        };

        private static bool Resolve()
        {
            if (_resolved)
                return Unavailable.Length == 0;
            _resolved = true;
            try
            {
                if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
                {
                    Unavailable = "phones keep SetData";
                    return false;
                }
                Assembly monoGame = typeof(Texture2D).Assembly;
                Type? gl = monoGame.GetType("MonoGame.OpenGL.GL");
                Type? threading = monoGame.GetType("Microsoft.Xna.Framework.Threading");
                if (gl == null)
                {
                    Unavailable = "no MonoGame.OpenGL.GL in this MonoGame";
                    return false;
                }
                IntPtr Pointer(string field)
                {
                    var bound = gl.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as Delegate
                        ?? throw new InvalidOperationException("GL." + field + " is not loaded");
                    return Marshal.GetFunctionPointerForDelegate(bound);
                }
                _bindTexture = (delegate* unmanaged<int, int, void>)Pointer("BindTexture");
                _pixelStore = (delegate* unmanaged<int, int, void>)Pointer("PixelStore");
                _getIntegerv = (delegate* unmanaged<int, int*, void>)Pointer("GetIntegerv");
                _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, IntPtr, void>)Pointer("TexImage2D");
                _texSubImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, IntPtr, void>)Pointer("TexSubImage2D");
                const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _glTexture = typeof(Texture).GetField("glTexture", instance);
                _glInternalFormat = typeof(Texture).GetField("glInternalFormat", instance);
                _glFormat = typeof(Texture).GetField("glFormat", instance);
                _glType = typeof(Texture).GetField("glType", instance);
                if (_glTexture == null || _glInternalFormat == null || _glFormat == null || _glType == null)
                {
                    Unavailable = "Texture has no GL fields in this MonoGame";
                    return false;
                }
                MethodInfo? onUi = threading?.GetMethod("IsOnUIThread", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (onUi == null)
                {
                    Unavailable = "no Threading.IsOnUIThread in this MonoGame";
                    return false;
                }
                _onGameThread = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), onUi);
                return true;
            }
            catch (Exception exception)
            {
                Unavailable = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }
    }
}
