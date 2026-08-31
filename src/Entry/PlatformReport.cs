using System;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// What this mod needs to know about the machine it landed on, written into the SMAPI log at
    /// startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every diagnostic this mod has is a console command, and a console is exactly what the
    /// platforms we hear the least from do not have. An Android player reported the water in one
    /// town looking wrong and attached a log, and the log was the only channel: it proved the mod
    /// loads there, that all thirteen shaders compiled, and that SMAPI had rewritten
    /// <c>Texture2D.GetData</c> underneath us. Nothing in it said what this mod could see of the
    /// device, because nothing here had ever written that down.
    /// </para>
    /// <para>
    /// So this runs once, costs a few milliseconds, and goes in at INFO where a shared log will
    /// carry it. It answers the questions that would otherwise be a round trip each: which
    /// renderer, how big the frame is against the window, whether the float targets the cascades
    /// need exist here, and whether a pixel written to a texture reads back as the same pixel.
    /// </para>
    /// </remarks>
    internal static class PlatformReport
    {
        internal static void WriteOnce(IMonitor monitor, GraphicsDevice? device)
        {
            try
            {
                monitor.Log($"platform: {Constants.TargetPlatform}, {Environment.OSVersion}, "
                    + $"{(Environment.Is64BitProcess ? "64" : "32")}-bit, .NET {Environment.Version}", LogLevel.Info);
                if (device == null)
                {
                    monitor.Log("graphics: no device yet at startup, so nothing about it can be reported.", LogLevel.Info);
                    return;
                }
                var presentation = device.PresentationParameters;
                monitor.Log($"graphics: {device.Adapter?.Description}, profile {device.GraphicsProfile}, "
                    + $"back buffer {presentation?.BackBufferWidth}x{presentation?.BackBufferHeight} "
                    + $"{presentation?.BackBufferFormat}", LogLevel.Info);
                monitor.Log($"render targets: {DescribeFormat(device, SurfaceFormat.Color)}, "
                    + $"{DescribeFormat(device, SurfaceFormat.HalfVector4)} "
                    + "(the second one is what the radiance cascades need; without it the mod keeps "
                    + "the flood model and nothing is lost but the newer lighting).", LogLevel.Info);
                monitor.Log(ReadbackVerdict(device), ReadbackWorks(device) ? LogLevel.Info : LogLevel.Warn);
            }
            catch (Exception ex)
            {
                // A report about the machine must never be the thing that stops the mod loading on it.
                monitor.Log($"could not describe this machine: {ex.Message}", LogLevel.Debug);
            }
        }

        private static string DescribeFormat(GraphicsDevice device, SurfaceFormat format)
        {
            try
            {
                using var probe = new RenderTarget2D(device, 4, 4, false, format, DepthFormat.None);
                return $"{format} yes";
            }
            catch (Exception ex)
            {
                return $"{format} NO ({ex.GetType().Name})";
            }
        }

        /// <summary>
        /// Write four known pixels into a texture and read them back.
        /// </summary>
        /// <remarks>
        /// This mod decides which tiles are water, and which sheet a modded pack repainted, by
        /// reading sheet pixels back with <c>Texture2D.GetData</c>. SMAPI's Android build rewrites
        /// that method, and a rewrite that returns anything other than what was written would not
        /// throw, would not warn, and would show up as the wrong tiles being treated as water in
        /// one town and not another. That is indistinguishable from a labelling bug from the
        /// outside, so it gets a straight answer here instead of a hunt later.
        /// </remarks>
        private static bool ReadbackWorks(GraphicsDevice device)
        {
            try
            {
                var written = new[]
                {
                    new Microsoft.Xna.Framework.Color(1, 2, 3, 255),
                    new Microsoft.Xna.Framework.Color(255, 0, 128, 64),
                    new Microsoft.Xna.Framework.Color(0, 255, 0, 0),
                    new Microsoft.Xna.Framework.Color(17, 34, 51, 68),
                };
                using var texture = new Texture2D(device, 2, 2);
                texture.SetData(written);
                var read = new Microsoft.Xna.Framework.Color[4];
                texture.GetData(read);
                for (int i = 0; i < written.Length; i++)
                    if (read[i] != written[i])
                        return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadbackVerdict(GraphicsDevice device)
            => ReadbackWorks(device)
                ? "texture readback: 4 of 4 pixels came back as written, so the water labels and the "
                  + "art fingerprints can trust what they read."
                : "texture readback: pixels did NOT come back as written. Water labelling and art "
                  + "fingerprinting both read sheets this way, so tiles may be treated as water that "
                  + "are not, or the other way round. Please report this line.";
    }
}
