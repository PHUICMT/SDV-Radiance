using System;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// The compiled shaders asked to run at full precision on phones.
    /// </summary>
    /// <remarks>
    /// <para>The shader compiler writes <c>precision mediump float;</c> at the top of every pixel
    /// shader for OpenGL ES, and a phone's graphics chip takes that as sixteen-bit floats: past
    /// 1,024 a half pixel is gone, past 2,048 the step is two, past 65,504 the number is infinite.
    /// A desktop driver ignores the line, so none of it ever showed here. On a phone it is the
    /// cause of every way the effects come apart there: the black stripes Sharp zoom drew, the
    /// water's random cells overflowing into black, world positions on a big map rounded to four
    /// and eight pixels, the shader clocks stepping by half a second before the morning is out
    /// (all listed in the 8 October audit).</para>
    /// <para>So before an effect is built on Android, each of those lines is rewritten to
    /// <c>precision highp   float;</c>, the same number of bytes, so the compiled file keeps its
    /// layout and its stored lengths. Only on OpenGL ES 3 or later, where full precision in a
    /// pixel shader is required of every chip; on anything older, or when the driver cannot be
    /// asked, the shaders stay exactly as compiled. The price is the faster half-precision
    /// arithmetic some chips have, which an effect that reads the wrong pixel was not getting
    /// the benefit of anyway. The vertex shaders are full precision already, and the desktop
    /// never reaches any of this.</para>
    /// </remarks>
    internal static class ShaderPrecision
    {
        private static readonly byte[] _halfPrecision = "precision mediump float;"u8.ToArray();
        private static readonly byte[] _fullPrecision = "precision highp   float;"u8.ToArray();
        private static bool? _raise;

        /// <summary>Whether shaders on this device are raised to full precision.</summary>
        internal static bool Raised(IMonitor monitor)
        {
            if (_raise is bool known)
                return known;
            bool raise = false;
            if (Constants.TargetPlatform == GamePlatform.Android)
            {
                int? major = PlatformReport.AskTheDriver(0x821B);   // GL_MAJOR_VERSION, OpenGL ES 3 and up only
                raise = major >= 3;
                monitor.Log(raise
                    ? $"shaders: raised to full precision (OpenGL ES {major}), so effects read the right pixel on this phone."
                    : $"shaders: left at the half precision they were compiled with (OpenGL ES version {major?.ToString() ?? "unknown"}); "
                      + "some effects may show blocks or black patches on this device. Please report this line.",
                    LogLevel.Info);
            }
            _raise = raise;
            return raise;
        }

        /// <summary>The compiled effect, with its pixel shaders at full precision where <see cref="Raised"/> says so.</summary>
        internal static byte[] Prepare(byte[] compiled, IMonitor monitor)
        {
            if (!Raised(monitor))
                return compiled;
            byte[] prepared = (byte[])compiled.Clone();
            int at = 0;
            while (true)
            {
                int found = prepared.AsSpan(at).IndexOf(_halfPrecision);
                if (found < 0)
                    return prepared;
                at += found;
                _fullPrecision.CopyTo(prepared, at);
                at += _fullPrecision.Length;
            }
        }
    }
}
