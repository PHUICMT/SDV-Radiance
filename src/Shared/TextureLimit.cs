using System;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// The largest picture the graphics card takes, asked of the driver once.
    /// </summary>
    /// <remarks>
    /// <para>MonoGame does not check a texture's size against the card's limit when it makes one,
    /// and it does not check a render target is complete when it binds it, so a picture past the
    /// limit does not throw: depending on the driver it draws nothing or draws garbage, and the
    /// effect built on it goes wrong with nothing in the log. A desktop card takes 16,384 pixels
    /// a side or more and never meets this; a phone may take only 4,096, and its screens are 2,400
    /// to 3,200 pixels wide before the padding round a reflection or the doubling of an art sheet
    /// (the 8 October phone audit).</para>
    /// <para>So the pictures that grow with the screen or with an art pack ask here first, and the
    /// feature that wanted one too large stands aside instead. When the driver cannot be asked, a
    /// phone is taken at 4,096 and a desktop is not limited.</para>
    /// </remarks>
    internal static class TextureLimit
    {
        private static int _largest;

        /// <summary>The longest side a texture or render target may have on this device.</summary>
        internal static int Largest
        {
            get
            {
                if (_largest > 0)
                    return _largest;
                int? texture = PlatformReport.AskTheDriver(0x0D33);        // GL_MAX_TEXTURE_SIZE
                int? renderBuffer = PlatformReport.AskTheDriver(0x84E8);   // GL_MAX_RENDERBUFFER_SIZE
                int asked = Math.Min(texture ?? int.MaxValue, renderBuffer ?? int.MaxValue);
                _largest = asked != int.MaxValue ? asked
                    : Constants.TargetPlatform == GamePlatform.Android ? 4096 : int.MaxValue;
                return _largest;
            }
        }

        /// <summary>Whether a picture of this size is one the device takes.</summary>
        internal static bool Fits(int width, int height) => width <= Largest && height <= Largest;
    }
}
