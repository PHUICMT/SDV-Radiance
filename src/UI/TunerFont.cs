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
    /// Nunito, the typeface of the F6 menu mock, for the glass panel's Latin text, and Sarabun for
    /// its Thai: one glyph atlas and its numbers (assets/tuner-font.png and .json, made by
    /// tools/make-tuner-font.py).
    ///
    /// <para>Thai is drawn a cluster at a time, a letter with the marks it carries, from a table
    /// HarfBuzz filled when the atlas was made: the tone mark over a vowel above sits higher than
    /// over a bare letter, marks over po pla, fo fa and fo fan move left, and yo ying and tho than lose
    /// their tails over a vowel below, as Sarabun's own tables say. The game only looks the cluster
    /// up.</para>
    ///
    /// <para>A string with any character or cluster the atlas does not hold is left to the game's
    /// own font, whole, which is how Chinese keeps working.</para>
    /// </summary>
    internal static class TunerFont
    {
        private readonly record struct Glyph(Rectangle Source, float OffsetX, float OffsetY, float Advance);

        /// <summary>One glyph of a laid-out string: where it comes from in the atlas and where its
        /// top left goes, in atlas pixels from the string's top left.</summary>
        private readonly record struct Placed(Rectangle Source, float X, float Y);

        /// <summary>A string laid out once and drawn from then on: a label is drawn every frame,
        /// and finding its Thai clusters every frame would make garbage every frame.</summary>
        private sealed class Laid
        {
            public Placed[] Glyphs = [];
            public float Width;
            public int Lines = 1;
            public float LineHeight;
        }

        private readonly record struct Cluster(float Advance, (Rectangle Source, float X, float Y)[] Glyphs);

        private static readonly Dictionary<char, Glyph> _glyphs = [];
        private static readonly Dictionary<string, Cluster> _thaiClusters = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Laid?> _laid = new(StringComparer.Ordinal);
        private static Texture2D? _atlas;
        private static float _ascent, _lineHeight;
        /// <summary>A line with Thai in it is taller: a tone mark over a vowel above the letter
        /// stands well over a Latin capital. Its baseline drops by the difference.</summary>
        private static float _thaiAscent, _thaiLineHeight;
        private static bool _tried;

        /// <summary>Strings laid out and kept; past this the store starts over, as the measuring
        /// cache in TunerText does, so live diagnostic lines cannot grow it without end.</summary>
        private const int LaidCap = 4096;

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
                ReadThai(root);
                _laid.Clear();
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

        private static void ReadThai(JsonElement root)
        {
            _thaiClusters.Clear();
            _thaiAscent = _ascent;
            _thaiLineHeight = _lineHeight;
            if (!root.TryGetProperty("thai", out JsonElement thai))
                return;
            var sources = new Dictionary<int, Rectangle>();
            foreach (JsonProperty entry in thai.GetProperty("glyphs").EnumerateObject())
            {
                JsonElement numbers = entry.Value;
                sources[int.Parse(entry.Name)] = new Rectangle(numbers[0].GetInt32(), numbers[1].GetInt32(), numbers[2].GetInt32(), numbers[3].GetInt32());
            }
            foreach (JsonProperty entry in thai.GetProperty("clusters").EnumerateObject())
            {
                JsonElement numbers = entry.Value;
                int count = (numbers.GetArrayLength() - 1) / 3;
                var glyphs = new (Rectangle, float, float)[count];
                for (int i = 0; i < count; i++)
                    glyphs[i] = (sources[numbers[1 + i * 3].GetInt32()], numbers[2 + i * 3].GetSingle(), numbers[3 + i * 3].GetSingle());
                _thaiClusters[entry.Name] = new Cluster(numbers[0].GetSingle(), glyphs);
            }
            // The line is set for a letter with a vowel above and one below; a tone mark over a
            // vowel above stands over its top (see make-tuner-font.py), so a label centres on its
            // letters rather than on room kept for the rarest stack.
            _thaiAscent = Math.Max(_ascent, thai.GetProperty("ascent").GetSingle());
            _thaiLineHeight = _thaiAscent + Math.Max(_lineHeight - _ascent, thai.GetProperty("descent").GetSingle());
        }

        private static bool IsThai(char letter) => letter is >= (char)0x0E00 and <= (char)0x0E7F;

        /// <summary>A vowel or tone mark drawn over or under the letter before it.</summary>
        private static bool IsThaiMark(char letter)
            => letter is (char)0x0E31 or (>= (char)0x0E34 and <= (char)0x0E3A) or (>= (char)0x0E47 and <= (char)0x0E4E);

        /// <summary>Lay the string out once, or null when the atlas cannot draw all of it.</summary>
        private static Laid? Layout(string text)
        {
            if (_laid.TryGetValue(text, out Laid? known))
                return known;
            if (_laid.Count > LaidCap)
                _laid.Clear();
            Laid? laid = LayOut(text);
            _laid[text] = laid;
            return laid;
        }

        private static Laid? LayOut(string text)
        {
            bool hasThai = false;
            foreach (char character in text)
                if (IsThai(character)) { hasThai = true; break; }
            float ascent = hasThai ? _thaiAscent : _ascent;
            float lineHeight = hasThai ? _thaiLineHeight : _lineHeight;
            var placed = new List<Placed>(text.Length);
            float penX = 0f, widest = 0f;
            int line = 0;
            for (int i = 0; i < text.Length;)
            {
                char character = text[i];
                float baseline = ascent + line * lineHeight;
                if (character == '\n')
                {
                    widest = Math.Max(widest, penX);
                    penX = 0f;
                    line++;
                    i++;
                    continue;
                }
                if (character == '\r')
                {
                    i++;
                    continue;
                }
                if (IsThai(character))
                {
                    int end = i + 1;
                    while (end < text.Length && IsThaiMark(text[end]))
                        end++;
                    if (end < text.Length && text[end] == (char)0x0E33 && !IsThaiMark(character))
                        end++;
                    if (!_thaiClusters.TryGetValue(text[i..end], out Cluster cluster))
                        return null;
                    foreach ((Rectangle source, float x, float y) in cluster.Glyphs)
                        placed.Add(new Placed(source, penX + x, baseline + y));
                    penX += cluster.Advance;
                    i = end;
                    continue;
                }
                if (!_glyphs.TryGetValue(character, out Glyph glyph))
                    return null;
                if (glyph.Source.Width > 0)
                    placed.Add(new Placed(glyph.Source, penX + glyph.OffsetX, baseline + glyph.OffsetY));
                penX += glyph.Advance;
                i++;
            }
            return new Laid
            {
                Glyphs = [.. placed],
                Width = Math.Max(widest, penX),
                Lines = line + 1,
                LineHeight = lineHeight,
            };
        }

        /// <summary>Whether this font can draw the whole string.</summary>
        internal static bool Covers(string text) => Ready() && Layout(text) != null;

        /// <summary>From this atlas's pixels to the game font's units, so a scale a caller passes
        /// means the same size in either font.</summary>
        private static float ToGameUnits => Game1.smallFont.LineSpacing * SizeAgainstGameFont / Math.Max(1f, _lineHeight);

        /// <summary>The size of the string in the game font's units (call <see cref="Covers"/> first).
        /// <paramref name="remember"/> false lays out a string without keeping it, for the trial
        /// lines of a wrap, which would otherwise fill the store.</summary>
        internal static Vector2 Measure(string text, bool remember = true)
        {
            Laid? laid = remember || _laid.ContainsKey(text) ? Layout(text) : LayOut(text);
            return laid == null ? Vector2.Zero : new Vector2(laid.Width, laid.Lines * laid.LineHeight) * ToGameUnits;
        }

        /// <summary>Draw the string with its top left at <paramref name="position"/>, at a scale in
        /// the game font's sense (call <see cref="Covers"/> first).</summary>
        internal static void Draw(SpriteBatch spriteBatch, string text, Vector2 position, Color colour, float scale)
        {
            Laid? laid = Layout(text);
            if (laid == null)
                return;
            float pixel = scale * ToGameUnits;
            foreach (Placed glyph in laid.Glyphs)
                spriteBatch.Draw(_atlas, new Vector2(position.X + glyph.X * pixel, position.Y + glyph.Y * pixel), glyph.Source,
                    colour, 0f, Vector2.Zero, pixel, SpriteEffects.None, 0f);
        }
    }
}
