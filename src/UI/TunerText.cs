using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>Text helpers shared by the tuner menu and its widgets.</summary>
    internal static class TunerText
    {
        /// <summary>Sizes already measured, so a menu full of labels asks the font once per string
        /// instead of once per frame. Keyed on the font too: switching language swaps
        /// <c>Game1.smallFont</c> for one with different metrics, and stale widths would misplace
        /// every label until restart.</summary>
        private static readonly Dictionary<string, Vector2> _measuredSizes = [];
        private static SpriteFont? _measuredFont;
        /// <summary>Whether the cached sizes are the glass panel's font or the game's.</summary>
        private static bool _measuredGlass;
        /// <summary>Live diagnostic lines (bench timings and the like) mint new strings while they
        /// run; past this the cache starts over rather than remembering every one it ever saw.</summary>
        private const int MeasuredSizesCap = 4096;

        /// <summary>The glass panel draws Latin text in the mock's typeface (see <see cref="TunerFont"/>).</summary>
        private static bool OwnFont(string text) => TunerTheme.Glass && TunerFont.Covers(text);

        /// <summary>What this string measures in the font it will be drawn in, remembered.</summary>
        internal static Vector2 Measure(string text)
        {
            SpriteFont font = Game1.smallFont;
            if (!ReferenceEquals(font, _measuredFont) || _measuredGlass != TunerTheme.Glass || _measuredSizes.Count > MeasuredSizesCap)
            {
                _measuredSizes.Clear();
                _measuredFont = font;
                _measuredGlass = TunerTheme.Glass;
            }
            if (!_measuredSizes.TryGetValue(text, out Vector2 size))
            {
                size = OwnFont(text) ? TunerFont.Measure(text) : font.MeasureString(text);
                _measuredSizes[text] = size;
            }
            return size;
        }

        /// <summary>Draw a string at a scale: in the glass panel's typeface when it can, with no
        /// shadow (the mock has none), otherwise in the game's font with its shadow.</summary>
        internal static void Draw(SpriteBatch spriteBatch, string text, Vector2 position, Color colour, float scale, float shadowIntensity = 1f)
        {
            if (OwnFont(text))
                TunerFont.Draw(spriteBatch, text, position, colour, scale);
            else
                Utility.drawTextWithShadow(spriteBatch, text, Game1.smallFont, position, colour, scale,
                    shadowIntensity: TunerTheme.Glass ? 0f : shadowIntensity);
        }

        /// <summary>Draw a label shrunk-to-fit so long translations never overflow their row.</summary>
        internal static void DrawFit(SpriteBatch spriteBatch, string text, Vector2 pos, float maxWidth, Color color, float maxScale)
        {
            float measuredWidth = Measure(text).X;
            float scale = Math.Min(maxScale, maxWidth / Math.Max(1f, measuredWidth));
            Draw(spriteBatch, text, pos, color, scale);
        }

        /// <summary>Break a string into lines no wider than <paramref name="width"/> at
        /// <paramref name="scale"/>, in the font it will be drawn in, at the spaces. The game's own
        /// wrap measures with its font, which is the wrong width for the glass panel's, and it cuts
        /// Thai at any letter, splitting a word down the middle; Thai is written with a space
        /// between phrases, so those are where it breaks here. Only a piece with no space in it that
        /// is wider than the line (a long Thai phrase, Chinese) is cut between letters.</summary>
        /// <summary>A Thai vowel or tone mark drawn above or below the letter before it (U+0E31,
        /// U+0E34 to U+0E3A, U+0E47 to U+0E4E), or the sara am that ends a syllable (U+0E33): a line
        /// never starts with one.</summary>
        private static bool SitsOnTheLetterBefore(char letter)
            => letter is (char)0x0E31 or (char)0x0E33 or (>= (char)0x0E34 and <= (char)0x0E3A) or (>= (char)0x0E47 and <= (char)0x0E4E);

        internal static string Wrap(string text, float width, float scale)
        {
            bool ownFont = OwnFont(text);
            // Measured in the font the whole text is drawn in, and not remembered: every trial line
            // would otherwise fill the cache.
            Vector2 SizeOf(string piece) => ownFont ? TunerFont.Measure(piece) : Game1.smallFont.MeasureString(piece);
            var wrapped = new StringBuilder();
            foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            {
                if (wrapped.Length > 0)
                    wrapped.Append('\n');
                var line = new StringBuilder();
                foreach (string word in paragraph.Split(' '))
                {
                    if (SizeOf(word).X * scale > width)
                    {
                        // A piece wider than the line on its own: a long Thai phrase, or Chinese,
                        // which has no spaces. The game's wrap only breaks at spaces when the
                        // language comes from a mod (Thai does), so it is cut here letter by letter,
                        // never in front of a mark that sits on the letter before it.
                        if (line.Length > 0)
                            wrapped.Append(line).Append('\n');
                        line.Clear();
                        int start = 0;
                        for (int end = 1; end <= word.Length; end++)
                        {
                            if (end < word.Length && SitsOnTheLetterBefore(word[end]))
                                continue;
                            if (end - start > 1 && SizeOf(word[start..end]).X * scale > width)
                            {
                                int cut = end - 1;
                                while (cut > start + 1 && SitsOnTheLetterBefore(word[cut]))
                                    cut--;
                                wrapped.Append(word, start, cut - start).Append('\n');
                                start = cut;
                            }
                        }
                        line.Append(word, start, word.Length - start);
                        continue;
                    }
                    string tried = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && SizeOf(tried).X * scale > width)
                    {
                        wrapped.Append(line).Append('\n');
                        line.Clear().Append(word);
                    }
                    else
                        line.Clear().Append(tried);
                }
                wrapped.Append(line);
            }
            return wrapped.ToString();
        }
    }
}
