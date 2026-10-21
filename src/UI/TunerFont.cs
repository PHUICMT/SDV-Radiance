using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Nunito, the typeface of the F6 menu mock, for the glass panel's Latin text: a glyph atlas
    /// and its numbers (assets/tuner-font.png and .json, made by tools/make-tuner-font.py).
    ///
    /// <para>A string with any character the atlas does not hold is left to the game's own font,
    /// whole, which is how Thai and Chinese keep their shaping: the game's fonts (and the mods that
    /// replace them) already place a Thai vowel over its consonant, and an atlas of our own would
    /// have to learn that.</para>
    /// </summary>
    internal static class TunerFont
    {
        private readonly record struct Glyph(Rectangle Source, float OffsetX, float OffsetY, float Advance);

        private static readonly Dictionary<char, Glyph> _glyphs = [];
        private static Texture2D? _atlas;
        private static float _ascent, _lineHeight;
        private static bool _tried;

        /// <summary>How tall a line of this font is next to a line of the game's small font: a little
        /// smaller, as on the mock, where the menu's type is lighter than the game's.</summary>
        private const float SizeAgainstGameFont = 0.8f;

        private static bool Ready()
        {
            if (_atlas != null && !_atlas.IsDisposed)
                return true;
            if (_tried && _atlas == null)
                return false;
            _tried = true;
            try
            {
                string? folder = RenderPipeline.Current?.ModDirectory;
                if (folder == null)
                    return false;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "assets", "tuner-font.json")));
                JsonElement root = document.RootElement;
                _ascent = root.GetProperty("ascent").GetSingle();
                _lineHeight = root.GetProperty("lineHeight").GetSingle();
                _glyphs.Clear();
                foreach (JsonProperty entry in root.GetProperty("glyphs").EnumerateObject())
                {
                    JsonElement numbers = entry.Value;
                    _glyphs[(char)int.Parse(entry.Name)] = new Glyph(
                        new Rectangle(numbers[0].GetInt32(), numbers[1].GetInt32(), numbers[2].GetInt32(), numbers[3].GetInt32()),
                        numbers[4].GetSingle(), numbers[5].GetSingle(), numbers[6].GetSingle());
                }
                _atlas = RenderPipeline.Current?.LoadTexture("tuner-font.png");
                return _atlas != null;
            }
            catch (Exception exception)
            {
                QuietFailures.Note("tuner font: reading the atlas", exception);
                _atlas = null;
                return false;
            }
        }

        /// <summary>Whether this font can draw every character of the string.</summary>
        internal static bool Covers(string text)
        {
            if (!Ready())
                return false;
            foreach (char character in text)
                if (character != '\n' && character != '\r' && !_glyphs.ContainsKey(character))
                    return false;
            return true;
        }

        /// <summary>From this atlas's pixels to the game font's units, so a scale a caller passes
        /// means the same size in either font.</summary>
        private static float ToGameUnits => Game1.smallFont.LineSpacing * SizeAgainstGameFont / Math.Max(1f, _lineHeight);

        /// <summary>The size of the string in the game font's units (call <see cref="Covers"/> first).</summary>
        internal static Vector2 Measure(string text)
        {
            float widest = 0f, width = 0f;
            int lines = 1;
            foreach (char character in text)
            {
                if (character == '\n')
                {
                    widest = Math.Max(widest, width);
                    width = 0f;
                    lines++;
                    continue;
                }
                if (_glyphs.TryGetValue(character, out Glyph glyph))
                    width += glyph.Advance;
            }
            widest = Math.Max(widest, width);
            return new Vector2(widest, lines * _lineHeight) * ToGameUnits;
        }

        /// <summary>Draw the string with its top left at <paramref name="position"/>, at a scale in
        /// the game font's sense (call <see cref="Covers"/> first).</summary>
        internal static void Draw(SpriteBatch spriteBatch, string text, Vector2 position, Color colour, float scale)
        {
            float pixel = scale * ToGameUnits;
            float penX = position.X, baseline = position.Y + _ascent * pixel;
            foreach (char character in text)
            {
                if (character == '\n')
                {
                    penX = position.X;
                    baseline += _lineHeight * pixel;
                    continue;
                }
                if (!_glyphs.TryGetValue(character, out Glyph glyph))
                    continue;
                if (glyph.Source.Width > 0)
                    spriteBatch.Draw(_atlas, new Vector2(penX + glyph.OffsetX * pixel, baseline + glyph.OffsetY * pixel), glyph.Source,
                        colour, 0f, Vector2.Zero, pixel, SpriteEffects.None, 0f);
                penX += glyph.Advance * pixel;
            }
        }
    }
}
