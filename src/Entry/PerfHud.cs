using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The cost readout, on screen, while you play.
    ///
    /// <para>
    /// <c>radiance_report</c> already holds all of this, but a file is the wrong shape for the
    /// question people actually ask, which is "what did I just do that made it stutter". By the
    /// time a report is written the moment is five seconds gone and averaged away. A readout you
    /// can watch while walking into the spot answers it directly, and it is the only way to catch
    /// something that happens once.
    /// </para>
    ///
    /// <para>
    /// Four rules of layout, all of them from the first version of this being unreadable:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>No fractional text scale.</b> The first attempt drew smallFont at 0.7
    /// and every glyph came out resampled and soft.</description></item>
    /// <item><description><b>smallFont, never tinyFont.</b> tinyFont looked like the answer to that
    /// and is a trap: it carries DIGITS and punctuation only, because the game uses it for stack
    /// counts and calendar numbers. Ask it for letters and they come back from a fallback font at
    /// its own much larger size, so one line ends up in two typefaces at two sizes and the labels
    /// walk through their own numbers.</description></item>
    /// <item><description><b>The panel is measured, not guessed.</b> Column positions come from the
    /// widest string actually being drawn this frame. A constant width was what let the labels and
    /// the numbers collide in the first place.</description></item>
    /// <item><description><b>No text shadow.</b> It exists to hold contrast over the world, which the
    /// panel behind the text already does, and at this size it only muddies the glyphs.</description></item>
    /// <item><description><b>A fixed order, never sorted by cost.</b> A list that reorders itself
    /// every frame cannot be read while anything is moving.</description></item>
    /// </list>
    /// </summary>
    internal static class PerfHud
    {
        internal static bool Visible;

        private static Texture2D? _pixel;

        /// <summary>Colour thresholds against a 60 fps budget of 16.67 ms: about a tenth of the
        /// frame is worth a look, about a quarter is the thing to go and fix.</summary>
        private const double WarnMs = 1.6, BadMs = 4.0;

        private const int Pad = 8;
        private const int Gap = 14;               // between the label and the first number column

        /// <summary>Room for every row the panel can hold: the three headings, one line per part,
        /// and the unfocused-frames warning.
        ///
        /// <para>SIZED FROM THE PART COUNT, never written beside it. These were a hand-typed 16,
        /// which was right when the mod had eleven parts; the wet world and the sprite relief
        /// normals took it to fourteen, and 3 + 14 is one past the end of a sixteen-long array. So
        /// the panel threw IndexOutOfRangeException on its LAST row, on every frame it was asked
        /// to draw - 3,928 times in one player's session, each one formatted into the SMAPI log
        /// until the log was 5.9 MB. The readout meant to show what a frame costs was costing the
        /// frame more than anything it measured. FrameCost already carries this lesson in a
        /// comment of its own ("a hand-kept copy of a count drifted once already"); this is the
        /// same mistake, one file over.</para></summary>
        private static readonly int RowCapacity = FrameCost.PartTotal + 4;

        private static readonly string[] _label = new string[RowCapacity];
        private static readonly string[] _cpu = new string[RowCapacity];
        private static readonly string[] _gpu = new string[RowCapacity];
        private static readonly Color[] _colour = new Color[RowCapacity];

        internal static void Draw(SpriteBatch b)
        {
            if (!Visible)
                return;
            int frames = FrameCost.FramesInWindow;
            if (frames <= 0)
                return;

            var font = Game1.smallFont;
            _pixel ??= CreatePixel(b.GraphicsDevice);

            double frameMs = FrameCost.SmoothedFrameMs;
            double fps = frameMs > 0 ? 1000.0 / frameMs : 0;
            double total = 0;
            bool anyGpu = false;
            for (int i = 0; i < FrameCost.PartTotal; i++)
            {
                total += FrameCost.PartAverageMs(i);
                anyGpu |= FrameCost.TryPartGpuAverageMs(i, out _);
            }
            int unfocused = FrameCost.UnfocusedFramesInWindow;

            // Build every row first, so the panel can be sized to what is actually in it.
            int n = 0;
            // The frame time is the GAME's, not ours, and it is labelled as such: the commonest
            // misreading of any of this is taking the mod's cost for the frame's. Every number
            // carries its unit for the same reason - a column of bare decimals told the author
            // nothing about what it was counting.
            Add(ref n, "whole frame", $"{frameMs:0.00} ms", $"{fps:0.0} fps", Color.White);
            Add(ref n, "this mod", $"{total:0.000} ms",
                $"{(frameMs > 0 ? total / frameMs * 100 : 0):0.0}% of it",
                total > BadMs ? Color.OrangeRed : total > WarnMs ? Color.Gold : Color.LightGreen);
            Add(ref n, "each part below", "cpu ms", anyGpu ? "gpu ms" : "", Color.Gray);
            for (int i = 0; i < FrameCost.PartTotal; i++)
            {
                double ms = FrameCost.PartAverageMs(i);
                string gpu = FrameCost.TryPartGpuAverageMs(i, out double gpuMs) ? $"{gpuMs:0.000}" : "";
                // A part that is switched off is drawn dim rather than dropped: "it reads zero" and
                // "it is not in the list" are different answers, and only one of them tells you
                // whether the setting is doing anything.
                Add(ref n, FrameCost.PartShortName(i), $"{ms:0.000}", gpu,
                    ms <= 0.0005 ? Color.Gray
                    : ms > BadMs ? Color.OrangeRed
                    : ms > WarnMs ? Color.Gold : Color.White);
            }
            if (unfocused > 0)
                Add(ref n, "unfocused", $"{unfocused}/{frames}", "", Color.OrangeRed);

            // The NUMBER columns are measured from a worst-case template, never from this frame's
            // values. Measuring the live strings is what made the panel twitch: "16.66 ms" is wider
            // than "9.66 ms", "60.0 fps" wider than "9.4 fps", so the box breathed every time a
            // digit came or went. Labels are constant strings and can be measured directly.
            float labelW = 0;
            for (int i = 0; i < n; i++)
                labelW = System.Math.Max(labelW, font.MeasureString(_label[i]).X);
            float cpuW = font.MeasureString("000.000 ms").X;
            float gpuW = anyGpu ? font.MeasureString("000.0% of it").X
                                : font.MeasureString("000.0 fps").X;
            float lineHeight = font.MeasureString("0").Y;
            float wantWidth = Pad * 2 + labelW + Gap + cpuW + (gpuW > 0 ? Gap + gpuW : 0);
            float wantHeight = Pad * 2 + n * lineHeight;

            // A half is the only reduction worth taking: it is an exact ratio, so it stays legible,
            // where the 0.7 this started with resampled every glyph into mush.
            var vp = b.GraphicsDevice.Viewport;
            float scale = (wantWidth > vp.Width * 0.5f || wantHeight > vp.Height * 0.75f) ? 0.5f : 1f;

            var panel = new Rectangle(Pad, Pad, (int)(wantWidth * scale), (int)(wantHeight * scale));
            b.Draw(_pixel, panel, Color.Black * 0.78f);

            float cpuRight = panel.X + (Pad + labelW + Gap + cpuW) * scale;
            float gpuRight = panel.Right - Pad * scale;
            float y = panel.Y + Pad * scale;
            for (int i = 0; i < n; i++)
            {
                b.DrawString(font, _label[i], new Vector2(panel.X + Pad * scale, y), _colour[i],
                    0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                Right(b, font, _cpu[i], cpuRight, y, _colour[i], scale);
                if (_gpu[i].Length > 0)
                    Right(b, font, _gpu[i], gpuRight, y, _colour[i], scale);
                y += lineHeight * scale;
            }
        }

        private static void Add(ref int n, string label, string cpu, string gpu, Color colour)
        {
            _label[n] = label; _cpu[n] = cpu; _gpu[n] = gpu; _colour[n] = colour;
            n++;
        }

        private static void Right(SpriteBatch b, SpriteFont font, string text, float right, float y,
            Color colour, float scale)
        {
            b.DrawString(font, text, new Vector2(right - font.MeasureString(text).X * scale, y), colour,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        private static Texture2D CreatePixel(GraphicsDevice device)
        {
            var t = new Texture2D(device, 1, 1);
            t.SetData(new[] { Color.White });
            return t;
        }
    }
}
