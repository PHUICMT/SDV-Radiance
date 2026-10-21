using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Objects;
using xTile.Layers;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - WHERE THE GLASS OF EACH WINDOW ON SCREEN IS, pixel by pixel.
    ///
    /// <para>
    /// The window light used to know a window as a point (the game's WindowLight) and drew the same
    /// soft oval on every pane and the same slanted band on every floor, so the light never showed
    /// the window it came through: a two-pane window glowed as one blob. labels/window-glass.json
    /// says which pixels of a window tile are glass, found by comparing each window's day picture
    /// with its rain or night one (tools/labelops/windowglass.py). Each frame, every tile and every
    /// window furniture on screen that has a glass label paints its glass into a small target, and
    /// floodlight.fx reads that target as the shape of the light: the pane glows where the glass is,
    /// and the beam on the floor carries the bars between the panes.
    /// </para>
    ///
    /// <para>
    /// A label only counts on the art it was cut from: each tile lists the fingerprints of that art,
    /// and a pack that repaints the sheet so the tile no longer matches gets no glass there, the same
    /// guard the water and glass labels have (<see cref="ArtFingerprint"/>).
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>
        /// One labelled tile: the art it was found on, its glass and curtain as the file wrote
        /// them, and its cell in the glass atlas once it has been painted there.
        /// </summary>
        /// <remarks>
        /// The file holds the windows of the game and of 97 mods, most of which a player never has
        /// installed. A tile is unpacked into the atlas only the first time it is drawn, so the
        /// atlas holds the windows this game has shown and nothing else.
        /// </remarks>
        private sealed class WindowGlassTile(HashSet<ulong> art, string glassBits, string sheerBits, string sheerColour, bool sheer, bool knownClear)
        {
            internal HashSet<ulong> Art { get; } = art;
            internal string GlassBits { get; } = glassBits;
            internal string SheerBits { get; } = sheerBits;
            internal string SheerColour { get; } = sheerColour;
            /// <summary>Part of it is a sheer curtain.</summary>
            internal bool Sheer { get; } = sheer;
            /// <summary>A window checked and found to have no glass: nothing to paint, but known,
            /// so its old light goes and no glass light comes.</summary>
            internal bool KnownClear { get; } = knownClear;
            /// <summary>Its cell in the glass atlas, or <see cref="NoAtlasCell"/> until first drawn.</summary>
            internal int AtlasCell = NoAtlasCell;
        }

        private const int NoAtlasCell = -1;
        /// <summary>Cells across the glass atlas: 1024 pixels, within every graphics card's limit.
        /// It was one row of every labelled tile, which with the mods' windows came to nearly 30,000
        /// pixels across, past the 16,384 (or 8,192) many cards can make.</summary>
        private const int AtlasColumns = 64;
        /// <summary>The tiles painted into the atlas so far, in cell order.</summary>
        private readonly List<WindowGlassTile> _windowGlassInAtlas = [];
        /// <summary>Atlases outgrown during a build, disposed at the start of the next one.</summary>
        private readonly List<Texture2D> _windowGlassRetiredAtlases = [];
        private int _windowGlassAtlasRows;
        /// <summary>The label file being read off the game's thread; null before it is asked for.</summary>
        private System.Threading.Tasks.Task<WindowGlassIndex?>? _windowGlassReading;

        /// <summary>What the label file says, read off the game's thread.</summary>
        private sealed class WindowGlassIndex
        {
            internal readonly Dictionary<string, Dictionary<int, WindowGlassTile[]>> BySheet = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<ulong, WindowGlassTile> ByArt = [];
            internal int Tiles;
            internal string? Problem;
        }

        /// <summary>
        /// Per sheet name and tile index, the glass found there, one variant per picture.
        /// </summary>
        /// <remarks>
        /// A sheet name does not name one picture: every Alternative Textures window is a
        /// "texture_1", and a recolour of the game's furniture is another "furniture". Each picture
        /// keeps its own glass and art, and the art on screen picks which one is drawn.
        /// </remarks>
        private readonly Dictionary<string, Dictionary<int, WindowGlassTile[]>> _windowGlassBySheet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every labelled tile again, by the fingerprint of its art alone.
        /// </summary>
        /// <remarks>
        /// A pack's window furniture reaches the game under an asset name the pack chose
        /// ("Mods/someone.pack/Furniture", or a sheet called "1"), which rarely matches the file
        /// the glass was found in and often matches another pack's. The art itself does not lie:
        /// a cell whose pixels hash to a labelled tile's art is that tile, whatever it is called.
        /// Only furniture asks this way; map tiles keep the sheet name, which a map does carry.
        /// </remarks>
        private readonly Dictionary<ulong, WindowGlassTile> _windowGlassByArt = [];

        /// <summary>Per furniture texture and cell, the glass its art matched, or null for none.
        /// Asked once per cell per place: reading a cell's fingerprint reads its pixels.</summary>
        private readonly Dictionary<(Texture2D Texture, Rectangle Cell), WindowGlassTile?> _windowGlassByCell = [];
        private Texture2D? _windowGlassAtlas;
        private RenderTarget2D? _windowGlassTarget;

        /// <summary>
        /// The colour of each sheer curtain, as what it takes out of the light.
        /// </summary>
        /// <remarks>
        /// Premultiplied by how much light the pixel lets through, which is also its alpha: clear
        /// glass is black at full alpha (it takes nothing away), a curtain is one minus its own hue
        /// at <see cref="ModConfig.WindowGlassCurtainLight"/>. Carried down the beam with the light's weight, the
        /// shader divides the colour by the alpha and has the curtain's hue back wherever its light
        /// fell, mixed with clear glass's in proportion where the two overlap.
        /// </remarks>
        private Texture2D? _windowGlassTintAtlas;
        /// <summary>The curtain colour carried down the beam, read beside <see cref="_windowGlassTarget"/>.
        /// Made only once a curtain is painted with <see cref="ModConfig.WindowGlassCurtainColour"/> on.</summary>
        private RenderTarget2D? _windowGlassTintTarget;
        /// <summary>One transparent texel, bound when there is no curtain colour: it reads as none.</summary>
        private Texture2D? _windowGlassNoTint;
        private bool _windowGlassTintPainted;

        /// <summary>Every glass cell painted in the last build, so the curtain colour can be painted
        /// in the same places without asking the map and the furniture again.</summary>
        private readonly List<(Rectangle Destination, int AtlasCell, SpriteEffects Effects)> _windowGlassCellsPainted = [];
        private bool _windowGlassSheerPainted;

        /// <summary>Adds as given: the curtain colour is premultiplied already, and the additive
        /// state would weigh it by its alpha a second time.</summary>
        private static readonly BlendState AddedAsGiven = new()
        {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One,
        };
        /// <summary>The glass alone, painted first; the window glass target is built from it.</summary>
        private RenderTarget2D? _windowGlassPaintTarget;

        /// <summary>How many copies of the glass are laid down the beam: under two target pixels
        /// apart at the longest beam the dial allows. At 24 a slanting morning beam's edges came
        /// out as a staircase of ridges ("the edges look striped", 15 Oct 2026).</summary>
        private const int WindowBeamCopies = 64;

        /// <summary>How much of the world the glass target covers, against the view, on each axis.
        /// It reaches past the view (half a view above it, a quarter to each side) so a window just
        /// off the top of the screen still throws its beam into it: painted from the view alone,
        /// the beam vanished the moment its window left the screen (15 Oct 2026).</summary>
        private const float WindowGlassCover = 1.5f;

        /// <summary>The world pixel the glass target starts at, snapped to the grid of art pixels
        /// (four world pixels), and the share of the view the target covers. Snapped because a
        /// target that followed the camera pixel by pixel resampled every window a little
        /// differently each frame and the beam shimmered as you walked.</summary>
        private Vector2 _windowGlassOriginWorld;
        private float _windowGlassScale = 1f;

        /// <summary>ModConfig.WindowGlassRayLength's default: the length the copies' weights are set at.</summary>
        private const float DefaultBeamLength = 1.2f;
        private SpriteBatch? _windowGlassBatch;
        private bool _windowGlassLoaded;

        /// <summary>Per sheet and tile index, the variant whose art is the art on screen, or null
        /// for none. Asked once per tile per place: reading a tile's fingerprint reads its art.</summary>
        private readonly Dictionary<(string Sheet, int Index), WindowGlassTile?> _windowGlassArtVerdict = [];
        private GameLocation? _windowGlassVerdictLocation;

        /// <summary>The world tiles that had glass painted, from the last build: the game's own
        /// window glow is drawn during the world draw, before the flood stage builds this frame's.</summary>
        private HashSet<Point> _windowGlassTilesShown = [];
        /// <summary>The tiles that had glass painted in this build, without the windows known to
        /// have none: the windows lightning casts shadows from.</summary>
        private readonly HashSet<Point> _windowGlassTilesWithGlass = [];
        private HashSet<Point> _windowGlassTilesBuilding = [];

        /// <summary>How many glass tiles and window furniture were painted last frame, for the report.</summary>
        internal int WindowGlassTilesDrawn { get; private set; }
        internal int WindowGlassRefusedForArt { get; private set; }
        internal int WindowGlassTilesLoaded { get; private set; }

        /// <summary>The target the window light reads, or null when nothing was painted this frame.</summary>
        internal RenderTarget2D? WindowGlassMask { get; private set; }

        /// <summary>radiance_windowglass on|off|auto: force the light shaped by the glass on or off
        /// for an A/B, or null to follow <see cref="ModConfig.WindowLightShape"/>. Eased either way
        /// (<see cref="_windowGlassMixEase"/>).</summary>
        internal static bool? WindowGlassShapeForced;

        /// <summary>Whether the glass shape is wanted now, before any fade.</summary>
        private static bool WindowGlassShapeWanted(ModConfig config)
            => WindowGlassShapeForced ?? config.WindowLightShape == WindowLightShape.Glass;

        /// <summary>Whether the glass has to be painted this frame: wanted, or still fading out.</summary>
        private bool WindowGlassNeeded(ModConfig config)
            => WindowGlassShapeWanted(config) || _windowGlassMixEase > FadeGone;
        private float _windowGlassMixEase;

        /// <summary>Hand the flood shader the glass target and how far to lean on it. Fades both
        /// ways; while it fades out after the glass is gone, the last picture of it is what fades.</summary>
        private void SetWindowGlassParams(Effect effect, ModConfig config)
        {
            float target = WindowGlassShapeWanted(config) && WindowGlassMask != null ? 1f : 0f;
            _windowGlassMixEase = Determinism.Settle(EasedToward(_windowGlassMixEase, target, RoomEaseRate), target);
            if (_windowGlassMixEase > FadeGone && _windowGlassTarget != null && !_windowGlassTarget.IsDisposed)
            {
                GetParam(effect, "WindowGlassTexture")?.SetValue(_windowGlassTarget);
                Texture2D curtainColour;
                if (_windowGlassTintPainted && _windowGlassTintTarget is { IsDisposed: false } tint)
                    curtainColour = tint;
                else
                {
                    if (_windowGlassNoTint == null || _windowGlassNoTint.IsDisposed)
                    {
                        _windowGlassNoTint = new Texture2D(_device, 1, 1, false, SurfaceFormat.Color);
                        _windowGlassNoTint.SetData([Color.Transparent]);
                    }
                    curtainColour = _windowGlassNoTint;
                }
                GetParam(effect, "WindowGlassTintTexture")?.SetValue(curtainColour);
                // The scales turn each dial's 1 into what the author settled on by eye (15 Oct 2026):
                // the rays plainly visible over a dark wall, the floor patch as bright as the old
                // beam's middle, the pane about twice the old oval's glow.
                GetParam(effect, "WindowGlass")?.SetValue(new Vector4(_windowGlassMixEase,
                    MathHelper.Clamp(config.WindowGlassRays, 0f, 2f) * 1.6f,
                    MathHelper.Clamp(config.WindowGlassFloor, 0f, 2f) * 1.2f,
                    MathHelper.Clamp(config.WindowGlassPane, 0f, 2f) * 2.2f));
                // x: the target's cover against the view, zw: where the view starts inside it, in
                // view widths; the shader reads the target at (uv + zw) / x.
                var viewport = Game1.viewport;
                GetParam(effect, "WindowGlassRay")?.SetValue(new Vector4(
                    _windowGlassScale,
                    MathHelper.Clamp(config.WindowGlassRayWhiteness, 0f, 1f),
                    (viewport.X - _windowGlassOriginWorld.X) / Math.Max(1, viewport.Width),
                    (viewport.Y - _windowGlassOriginWorld.Y) / Math.Max(1, viewport.Height)));
            }
            else
            {
                GetParam(effect, "WindowGlass")?.SetValue(Vector4.Zero);
            }
        }

        /// <summary>How much a sheer curtain itself glows, as a share of the light it lets through
        /// (<see cref="ModConfig.WindowGlassCurtainLight"/>). Glowing as much as it let through, the
        /// cloth shone too bright (author, 16 Oct 2026). The atlas carries the glow in red, which is
        /// what the pane glow reads, while green and blue, which the beam and the floor are carried
        /// from, keep the light's share.</summary>
        private const float SheerCurtainGlowShare = 0.4f;

        private float _windowGlassAtlasCurtainLight = -1f;
        private float _windowGlassAtlasColourStrength = -1f;

        /// <summary>A tile's 256 bits as the file packs them (base64, most significant bit first).</summary>
        private static bool[] UnpackTileBits(string packed)
        {
            var bits = new bool[256];
            if (string.IsNullOrEmpty(packed))
                return bits;
            byte[] bytes = Convert.FromBase64String(packed);
            if (bytes.Length < 32)
                return bits;
            for (int pixel = 0; pixel < 256; pixel++)
                bits[pixel] = (bytes[pixel >> 3] & (0x80 >> (pixel & 7))) != 0;
            return bits;
        }

        private static bool AnyBits(string packed)
        {
            if (string.IsNullOrEmpty(packed))
                return false;
            foreach (byte value in Convert.FromBase64String(packed))
                if (value != 0)
                    return true;
            return false;
        }

        /// <summary>What a curtain pixel takes out of the light, premultiplied by what it lets
        /// through (see <see cref="_windowGlassTintAtlasPixels"/>). Its hue only, at full brightness:
        /// the curtain already dims the light by <paramref name="curtainLight"/>, and a dark red
        /// curtain should turn the light red, not dim it twice. A white or grey curtain takes no colour
        /// out, and neither does a pixel the file has no colour for.</summary>
        private static Color CurtainLoss(byte[] colours, int pixel, float curtainLight, float colourStrength)
        {
            float red = 1f, green = 1f, blue = 1f;
            if (colours.Length >= (pixel + 1) * 3)
            {
                float brightest = Math.Max(colours[pixel * 3], Math.Max(colours[pixel * 3 + 1], colours[pixel * 3 + 2]));
                if (brightest > 0f)
                {
                    red = colours[pixel * 3] / brightest;
                    green = colours[pixel * 3 + 1] / brightest;
                    blue = colours[pixel * 3 + 2] / brightest;
                }
            }
            // The colour's strength scales what the curtain takes away: 1 is its own hue, 2 twice as
            // deep (up to taking a channel away whole), 0 none.
            float Taken(float channel) => Math.Clamp((1f - channel) * colourStrength, 0f, 1f) * curtainLight;
            return new Color(Taken(red), Taken(green), Taken(blue), curtainLight);
        }

        /// <summary>One tile's 16 by 16 pixels for the glass atlas and the curtain colour atlas, the
        /// curtains at <paramref name="curtainLight"/>.</summary>
        private static void TilePixels(WindowGlassTile tile, float curtainLight, float colourStrength, Color[] glassPixels, Color[] tintPixels)
        {
            bool[] glass = UnpackTileBits(tile.GlassBits);
            bool[] sheer = UnpackTileBits(tile.SheerBits);
            byte[] colours = string.IsNullOrEmpty(tile.SheerColour) ? [] : Convert.FromBase64String(tile.SheerColour);
            // Opaque at every strength: the glass is painted with additive blending, which weighs by
            // alpha, so a curtain's share in alpha would have been taken off twice.
            byte throughLevel = (byte)(255 * curtainLight);
            var curtainPixel = new Color((byte)(255 * curtainLight * SheerCurtainGlowShare), throughLevel, throughLevel, (byte)255);
            for (int pixel = 0; pixel < 256; pixel++)
            {
                if (glass[pixel])
                {
                    glassPixels[pixel] = Color.White;
                    tintPixels[pixel] = Color.Black;
                }
                else if (sheer[pixel])
                {
                    glassPixels[pixel] = curtainPixel;
                    tintPixels[pixel] = CurtainLoss(colours, pixel, curtainLight, colourStrength);
                }
                else
                {
                    glassPixels[pixel] = Color.Transparent;
                    tintPixels[pixel] = Color.Transparent;
                }
            }
        }

        private static Rectangle AtlasSource(int cell) => new(cell % AtlasColumns * 16, cell / AtlasColumns * 16, 16, 16);

        private readonly Color[] _windowGlassCellPixels = new Color[256];
        private readonly Color[] _windowGlassCellTint = new Color[256];

        /// <summary>
        /// The tile's cell in the glass atlas, painting it there the first time it is asked for;
        /// false for a window known to have no glass.
        /// </summary>
        private bool EnsureAtlasCell(WindowGlassTile tile)
        {
            if (tile.KnownClear)
                return false;
            if (tile.AtlasCell != NoAtlasCell)
                return true;
            if (_windowGlassInAtlas.Count >= _windowGlassAtlasRows * AtlasColumns)
                RemakeWindowGlassAtlas(Math.Max(2, _windowGlassAtlasRows * 2));
            tile.AtlasCell = _windowGlassInAtlas.Count;
            _windowGlassInAtlas.Add(tile);
            TilePixels(tile, _windowGlassAtlasCurtainLight, _windowGlassAtlasColourStrength, _windowGlassCellPixels, _windowGlassCellTint);
            Rectangle cell = AtlasSource(tile.AtlasCell);
            _windowGlassAtlas!.SetData(0, cell, _windowGlassCellPixels, 0, 256);
            _windowGlassTintAtlas!.SetData(0, cell, _windowGlassCellTint, 0, 256);
            return true;
        }

        /// <summary>Make the two atlases at this many rows and paint every tile already in them
        /// again: when they fill, or when a curtain dial moves.</summary>
        private void RemakeWindowGlassAtlas(int rows)
        {
            if (rows != _windowGlassAtlasRows || _windowGlassAtlas == null || _windowGlassAtlas.IsDisposed)
            {
                // Retired, not disposed: this can happen in the middle of a sprite batch that has
                // already drawn from the old ones, and a deferred batch draws at its End.
                if (_windowGlassAtlas is { IsDisposed: false } oldGlass)
                    _windowGlassRetiredAtlases.Add(oldGlass);
                if (_windowGlassTintAtlas is { IsDisposed: false } oldTint)
                    _windowGlassRetiredAtlases.Add(oldTint);
                _windowGlassAtlas = new Texture2D(_device, AtlasColumns * 16, rows * 16, false, SurfaceFormat.Color);
                _windowGlassTintAtlas = new Texture2D(_device, AtlasColumns * 16, rows * 16, false, SurfaceFormat.Color);
                _windowGlassAtlasRows = rows;
            }
            int width = AtlasColumns * 16;
            var glassPixels = new Color[width * rows * 16];
            var tintPixels = new Color[glassPixels.Length];
            foreach (WindowGlassTile tile in _windowGlassInAtlas)
            {
                TilePixels(tile, _windowGlassAtlasCurtainLight, _windowGlassAtlasColourStrength, _windowGlassCellPixels, _windowGlassCellTint);
                Rectangle cell = AtlasSource(tile.AtlasCell);
                for (int pixel = 0; pixel < 256; pixel++)
                {
                    int index = (cell.Y + pixel / 16) * width + cell.X + pixel % 16;
                    glassPixels[index] = _windowGlassCellPixels[pixel];
                    tintPixels[index] = _windowGlassCellTint[pixel];
                }
            }
            _windowGlassAtlas!.SetData(glassPixels);
            _windowGlassTintAtlas!.SetData(tintPixels);
        }

        /// <summary>Read the label file into an index, off the game's thread: the tiles are kept as
        /// the file wrote them and unpacked only when drawn (<see cref="EnsureAtlasCell"/>).</summary>
        private static WindowGlassIndex? ReadWindowGlassFile(string path)
        {
            var index = new WindowGlassIndex();
            if (!File.Exists(path))
                return null;
            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream);
                foreach (JsonProperty sheet in document.RootElement.GetProperty("sheets").EnumerateObject())
                {
                    var tiles = new Dictionary<int, WindowGlassTile[]>();
                    foreach (JsonProperty tile in sheet.Value.GetProperty("tiles").EnumerateObject())
                    {
                        if (!int.TryParse(tile.Name, out int tileIndex))
                            continue;
                        var variants = new List<WindowGlassTile>();
                        IEnumerable<JsonElement> written = tile.Value.TryGetProperty("variants", out JsonElement listed)
                            ? listed.EnumerateArray() : [tile.Value];
                        foreach (JsonElement variant in written)
                        {
                            string glassBits = variant.TryGetProperty("glass", out JsonElement glass) ? glass.GetString() ?? "" : "";
                            string sheerBits = variant.TryGetProperty("sheer", out JsonElement sheer) ? sheer.GetString() ?? "" : "";
                            string sheerColour = variant.TryGetProperty("sheerColour", out JsonElement colours) ? colours.GetString() ?? "" : "";
                            bool hasSheer = AnyBits(sheerBits);
                            var art = new HashSet<ulong>();
                            if (variant.TryGetProperty("art", out JsonElement fingerprints))
                                foreach (JsonElement fingerprint in fingerprints.EnumerateArray())
                                    if (ulong.TryParse(fingerprint.GetString(), System.Globalization.NumberStyles.HexNumber, null, out ulong value))
                                        art.Add(value);
                            var labelled = new WindowGlassTile(art, glassBits, sheerBits, sheerColour, hasSheer, !hasSheer && !AnyBits(glassBits));
                            variants.Add(labelled);
                            foreach (ulong fingerprint in art)
                                index.ByArt.TryAdd(fingerprint, labelled);
                            index.Tiles++;
                        }
                        if (variants.Count > 0)
                            tiles[tileIndex] = [.. variants];
                    }
                    if (tiles.Count > 0)
                        index.BySheet[LabelStore.NormalizeSheet(sheet.Name)] = tiles;
                }
            }
            catch (Exception error)
            {
                index.BySheet.Clear();
                index.ByArt.Clear();
                index.Problem = error.Message;
            }
            return index;
        }

        /// <summary>
        /// Start reading the label file the first time the glass is wanted, and take its answer
        /// once it is in. Nothing is read while the window light shaped by the glass is off.
        /// </summary>
        /// <remarks>
        /// Off the game's thread: the file is most of a megabyte of JSON, and reading it in the
        /// frame a player first walks into a room was a hitch in that frame. Until it is in, the
        /// windows keep the old light, and the change eases in like any other.
        /// </remarks>
        private void LoadWindowGlass()
        {
            if (_windowGlassLoaded)
                return;
            if (_windowGlassReading == null)
            {
                string path = Path.Combine(_modDirectory, "labels", LabelStore.WindowGlassFileName);
                _windowGlassReading = System.Threading.Tasks.Task.Run(() => ReadWindowGlassFile(path));
                return;
            }
            if (!_windowGlassReading.IsCompleted)
                return;
            _windowGlassLoaded = true;
            WindowGlassIndex? index = _windowGlassReading.IsCompletedSuccessfully ? _windowGlassReading.Result : null;
            _windowGlassReading = null;
            if (index == null)
                return;
            if (index.Problem != null)
            {
                _monitor.Log($"Bad window glass file: {index.Problem}", StardewModdingAPI.LogLevel.Warn);
                return;
            }
            foreach (var (sheet, tiles) in index.BySheet)
                _windowGlassBySheet[sheet] = tiles;
            foreach (var (fingerprint, tile) in index.ByArt)
                _windowGlassByArt[fingerprint] = tile;
            WindowGlassTilesLoaded = index.Tiles;
            _monitor.Log($"[windows] {index.Tiles} window glass tiles on {_windowGlassBySheet.Count} sheets", StardewModdingAPI.LogLevel.Trace);
        }

        /// <summary>Paint the glass of every labelled window on screen into the window glass target,
        /// one target pixel to one pixel of art (a quarter of the view in world pixels, whatever
        /// the zoom). Leaves <see cref="WindowGlassMask"/> null when there is none, which is how
        /// the shader knows to keep the old shape.</summary>
        private void BuildWindowGlassMask(ModConfig config)
        {
            WindowGlassMask = null;
            foreach (Texture2D retired in _windowGlassRetiredAtlases)
                retired.Dispose();
            _windowGlassRetiredAtlases.Clear();
            LoadWindowGlass();
            GameLocation? location = Game1.currentLocation;
            if (location?.map == null || _windowGlassBySheet.Count == 0)
                return;
            if (!ReferenceEquals(location, _windowGlassVerdictLocation))
            {
                _windowGlassVerdictLocation = location;
                _windowGlassArtVerdict.Clear();
                _windowGlassByCell.Clear();
            }
            float curtainLight = MathHelper.Clamp(config.WindowGlassCurtainLight, 0f, 1f);
            float colourStrength = MathHelper.Clamp(config.WindowGlassCurtainColourStrength, 0f, 2f);
            if (_windowGlassAtlas == null || _windowGlassAtlas.IsDisposed || _windowGlassTintAtlas == null || _windowGlassTintAtlas.IsDisposed)
            {
                // Lost with the device, or not made yet: what was in it is painted again as it is drawn.
                foreach (WindowGlassTile tile in _windowGlassInAtlas)
                    tile.AtlasCell = NoAtlasCell;
                _windowGlassInAtlas.Clear();
                _windowGlassAtlasCurtainLight = curtainLight;
                _windowGlassAtlasColourStrength = colourStrength;
                RemakeWindowGlassAtlas(2);
            }
            else if (curtainLight != _windowGlassAtlasCurtainLight || colourStrength != _windowGlassAtlasColourStrength)
            {
                _windowGlassAtlasCurtainLight = curtainLight;
                _windowGlassAtlasColourStrength = colourStrength;
                RemakeWindowGlassAtlas(_windowGlassAtlasRows);
            }

            // WHAT THE TARGET COVERS. A room is small: the whole map fits in a target of one pixel
            // per art pixel, and then nothing about it depends on the camera, so it is painted once
            // and read for as long as the room, the sun and the dials stay as they are. A place too
            // big for that covers the view and its margins instead, and is painted again only when
            // the camera crosses a pixel of art.
            xTile.Dimensions.Rectangle viewport = Game1.viewport;
            int viewWidth = Math.Max(1, viewport.Width), viewHeight = Math.Max(1, viewport.Height);
            int mapWidth = 0, mapHeight = 0;
            foreach (Layer layer in location.map.Layers)
            {
                mapWidth = Math.Max(mapWidth, layer.LayerWidth * 64);
                mapHeight = Math.Max(mapHeight, layer.LayerHeight * 64);
            }
            float wholeMapScale = Math.Max(mapWidth / (float)viewWidth, mapHeight / (float)viewHeight);
            bool wholeMap = wholeMapScale <= WholeMapCoverLimit;
            int coveredWidth, coveredHeight;
            if (wholeMap)
            {
                coveredWidth = (int)MathF.Ceiling(viewWidth * wholeMapScale / 4f) * 4;
                _windowGlassScale = coveredWidth / (float)viewWidth;
                coveredHeight = (int)MathF.Ceiling(viewHeight * _windowGlassScale / 4f) * 4;
                _windowGlassOriginWorld = Vector2.Zero;
            }
            else
            {
                coveredWidth = (int)MathF.Ceiling(viewWidth * WindowGlassCover / 4f) * 4;
                _windowGlassScale = coveredWidth / (float)viewWidth;
                coveredHeight = (int)MathF.Ceiling(viewHeight * _windowGlassScale / 4f) * 4;
                _windowGlassOriginWorld = new Vector2(
                    MathF.Floor((viewport.X - viewWidth * (_windowGlassScale - 1f) * 0.5f) / 4f) * 4f,
                    MathF.Floor((viewport.Y - viewHeight * (_windowGlassScale - 1f)) / 4f) * 4f);
            }
            int width = Math.Max(1, coveredWidth / 4), height = Math.Max(1, coveredHeight / 4);

            // Painted again only when something it shows has changed.
            WindowBeamShape(config, out float lean, out float reachTiles, out float lengthDial, out float spreadDial);
            var key = new WindowGlassBuildKey(location, _windowGlassOriginWorld, width, height,
                MathF.Round(lean * 100f), MathF.Round(reachTiles * 20f),
                lengthDial, spreadDial, WindowFurnitureSignature(location),
                _windowGlassArtEpoch, Game1.timeOfDay / 100, config.WindowGlassCurtainColour, curtainLight, colourStrength);
            bool targetsAlive = _windowGlassTarget != null && !_windowGlassTarget.IsDisposed
                                && _windowGlassTarget.Width == width && _windowGlassTarget.Height == height;
            if (targetsAlive && key.Equals(_windowGlassLastBuild))
            {
                WindowGlassBuildsSkipped++;
                if (WindowGlassTilesDrawn > 0 || _windowGlassTilesKnownClear > 0)
                    WindowGlassMask = _windowGlassTarget;
                return;
            }
            _windowGlassLastBuild = key;
            WindowGlassBuilds++;
            WindowGlassTilesDrawn = 0;
            _windowGlassTilesKnownClear = 0;
            WindowGlassRefusedForArt = 0;
            _windowGlassTilesBuilding.Clear();
            _windowGlassTilesWithGlass.Clear();
            _windowGlassCellsPainted.Clear();
            _windowGlassSheerPainted = false;
            _windowGlassTintPainted = false;
            if (!targetsAlive)
            {
                _windowGlassTarget?.Dispose();
                _windowGlassPaintTarget?.Dispose();
                _windowGlassTintTarget?.Dispose();
                _windowGlassTintTarget = null;
                // Half floats for the beam: sixty-four faint copies added in eight bits rounded the
                // tail of the beam into steps.
                _windowGlassTarget = CreateRenderTarget(width, height, SurfaceFormat.HalfVector4);
                _windowGlassPaintTarget = CreateRenderTarget(width, height, SurfaceFormat.Color);
            }
            _windowGlassBatch ??= new SpriteBatch(_device);

            Matrix toTarget = Matrix.CreateTranslation(-_windowGlassOriginWorld.X, -_windowGlassOriginWorld.Y, 0f)
                            * Matrix.CreateScale(0.25f, 0.25f, 1f);
            RenderTargetBinding[] previous = _device.GetRenderTargets();
            try
            {
                _device.SetRenderTarget(_windowGlassPaintTarget);
                _device.Clear(Color.Transparent);
                _windowGlassBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone, null, toTarget);
                PaintMapGlass(location, new Rectangle((int)_windowGlassOriginWorld.X, (int)_windowGlassOriginWorld.Y, coveredWidth, coveredHeight));
                PaintFurnitureGlass(location);
                _windowGlassBatch.End();
                if (WindowGlassTilesDrawn > 0)
                    PaintBeamFromGlass(config, toTarget);
                else if (_windowGlassTilesKnownClear > 0)
                {
                    // Only windows without glass: an empty target, so their old light can still give way.
                    _device.SetRenderTarget(_windowGlassTarget);
                    _device.Clear(Color.Transparent);
                }
            }
            finally
            {
                _device.SetRenderTargets(previous);
            }
            if (WindowGlassTilesDrawn > 0 || _windowGlassTilesKnownClear > 0)
                WindowGlassMask = _windowGlassTarget;
            (_windowGlassTilesShown, _windowGlassTilesBuilding) = (_windowGlassTilesBuilding, _windowGlassTilesShown);
            LightningEffects.SetWindowCentres(location, _windowGlassTilesWithGlass);
        }

        /// <summary>A map up to this many views across, on its longer side, is covered whole.
        /// Every room in the game is well inside it; a map past it covers the view instead.</summary>
        private const float WholeMapCoverLimit = 2f;

        /// <summary>Everything the glass target's picture depends on. Equal to the last build's,
        /// the target is reused as it is.</summary>
        private readonly record struct WindowGlassBuildKey(GameLocation Location, Vector2 Origin, int Width, int Height,
            float LeanHundredths, float ReachTwentieths, float Length, float Spread, int Furniture, int ArtEpoch, int Hour,
            bool CurtainColour, float CurtainLight, float CurtainColourStrength);

        private WindowGlassBuildKey? _windowGlassLastBuild;
        private int _windowGlassArtEpoch;

        /// <summary>How often the glass target was painted, and how often the last one was reused,
        /// since launch: the report's evidence that a still room costs nothing to keep lit.</summary>
        internal int WindowGlassBuilds { get; private set; }
        internal int WindowGlassBuildsSkipped { get; private set; }

        /// <summary>A number that changes when a window furniture is placed, moved, turned or swapped.</summary>
        private static int WindowFurnitureSignature(GameLocation location)
        {
            int signature = 17;
            foreach (Furniture furniture in location.furniture)
            {
                if (furniture?.furniture_type.Value != Furniture.window)
                    continue;
                signature = signature * 31 + furniture.TileLocation.GetHashCode();
                signature = signature * 31 + (furniture.QualifiedItemId?.GetHashCode() ?? 0);
                signature = signature * 31 + furniture.sourceRect.Value.GetHashCode();
                signature = signature * 31 + (furniture.Flipped ? 1 : 0);
            }
            return signature;
        }

        /// <summary>
        /// The glass, then the beam it throws, into the window glass target: red the glass itself,
        /// green the light in the air, blue the patch on the floor.
        /// </summary>
        /// <remarks>
        /// The beam is the glass laid down again and again along the sun's direction, each copy a
        /// little further and fainter, the floor's copies ramping in below the sill the way the
        /// old band did. Done here on a target of one pixel per art pixel, two dozen sprite draws,
        /// because the shader that did it with eight reads per pixel left the rays in specks.
        /// Each copy is drawn twice, a little to each side, wider the further it has come.
        /// </remarks>
        /// <summary>
        /// The beam's slant, reach, length dial and spread dial: the sun's and the player's, or
        /// while a near lightning strike lights the room, the strike's own slant and reach.
        /// </summary>
        /// <remarks>
        /// A bolt is somewhere else in the sky each time, so its light falls somewhere else on
        /// the floor each strike. Only a near strike takes the beam over, and only in a
        /// storm, when the rain has already put the daylight through the glass out, so it never
        /// changes a beam the player is looking at by day.
        /// </remarks>
        /// <summary>How much wider than the day's beam a lightning strike's beam fans out.</summary>
        private const float StrikeBeamFanOut = 1.8f;

        private static void WindowBeamShape(ModConfig config, out float lean, out float reachTiles, out float lengthDial, out float spreadDial)
        {
            if (LightningEffects.WindowStrikeShapesBeam(out lean, out reachTiles))
            {
                // Wider than the day's beam, by the player's own dial: unspread, the panes fell
                // sharp and true, and the author asked for the big strike's light to fan out into
                // the room instead, and then wider still (16 Oct 2026).
                lengthDial = 1f;
                spreadDial = MathHelper.Clamp(config.WindowGlassRaySpread * StrikeBeamFanOut, 0f, 8f);
                return;
            }
            ShadowRenderer.WindowShaft(out lean, out reachTiles);
            lengthDial = MathHelper.Clamp(config.WindowGlassRayLength, 0.5f, 2f);
            spreadDial = MathHelper.Clamp(config.WindowGlassRaySpread, 0f, 8f);
        }

        private void PaintBeamFromGlass(ModConfig config, Matrix toTarget)
        {
            _device.SetRenderTarget(_windowGlassTarget);
            _device.Clear(Color.Transparent);
            WindowBeamShape(config, out float lean, out float reachTiles, out float lengthDial, out float spreadDial);
            float beamTiles = reachTiles * lengthDial;
            const float targetPixelsPerTile = 16f;
            var down = new Vector2(lean, 1f) * beamTiles * targetPixelsPerTile;
            var whole = new Rectangle(0, 0, _windowGlassTarget!.Width, _windowGlassTarget.Height);
            _windowGlassBatch!.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            _windowGlassBatch.Draw(_windowGlassPaintTarget!, whole, new Color(255, 0, 0, 255));
            LayBeamCopies(spreadDial, down, beamTiles, reachTiles, whole, curtainColour: false);
            _windowGlassBatch.End();

            // The curtain's colour, laid down the beam the same way into a target of its own. Only
            // when there is a curtain to colour it: a room of clear glass reads the empty texel.
            if (!config.WindowGlassCurtainColour || !_windowGlassSheerPainted)
                return;
            _device.SetRenderTarget(_windowGlassPaintTarget);
            _device.Clear(Color.Transparent);
            _windowGlassBatch.Begin(SpriteSortMode.Deferred, AddedAsGiven, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, null, toTarget);
            foreach (var (destination, atlasCell, effects) in _windowGlassCellsPainted)
                _windowGlassBatch.Draw(_windowGlassTintAtlas!, destination, AtlasSource(atlasCell),
                    Color.White, 0f, Vector2.Zero, effects, 0f);
            _windowGlassBatch.End();
            if (_windowGlassTintTarget == null || _windowGlassTintTarget.IsDisposed)
                _windowGlassTintTarget = CreateRenderTarget(whole.Width, whole.Height, SurfaceFormat.HalfVector4);
            _device.SetRenderTarget(_windowGlassTintTarget);
            _device.Clear(Color.Transparent);
            _windowGlassBatch.Begin(SpriteSortMode.Deferred, AddedAsGiven, SamplerState.LinearClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            LayBeamCopies(spreadDial, down, beamTiles, reachTiles, whole, curtainColour: true);
            _windowGlassBatch.End();
            _windowGlassTintPainted = true;
        }

        /// <summary>The copies of the painted glass laid down the beam (see
        /// <see cref="PaintBeamFromGlass"/>): the light into green and blue, or with
        /// <paramref name="curtainColour"/> the curtain colour, weighted by all the light at each copy.</summary>
        private void LayBeamCopies(float spreadDial, Vector2 down, float beamTiles, float reachTiles, Rectangle whole, bool curtainColour)
        {
            const float targetPixelsPerTile = 16f;
            for (int copy = 1; copy <= WindowBeamCopies; copy++)
            {
                float along = copy / (float)WindowBeamCopies;
                // Both run the whole length and thin out together. The floor patch used to stop at
                // half the length while the rays went on, and the beam read as two pieces, a bright
                // one and a faint one under it ("it is broken, not one line", 15 Oct 2026). The
                // floor's light still eases in just below the sill so the glass stays the brightest.
                float rayFade = 1f - along;
                float floorFade = MathHelper.SmoothStep(0f, 1f, Math.Min(1f, along / 0.12f)) * (1f - along) * MathF.Sqrt(1f - along);
                // Per copy: the sums over the copies a pane-tall window spans come to about 1,
                // whatever the number of copies, and whatever the length: a longer beam spaces its
                // copies further apart, which thinned the light along it, so the length dial brings
                // it back up (asked for with the spread's own fix, 15 Oct 2026). Against the dial's
                // default, so the default beam is the one the weights were set by.
                float copyShare = 24f / WindowBeamCopies * (beamTiles / (reachTiles * DefaultBeamLength));
                float rayWeight = rayFade * 0.3f * copyShare;
                float floorWeight = floorFade * 0.22f * copyShare;
                var colour = new Color(0f, rayWeight * 0.5f, floorWeight * 0.5f, 1f);
                Vector2 offset = down * along;
                // Wider and softer the further it has come, by the player's dial. Read across the
                // width about every two target pixels (2 to 16 reads), so even the widest spread is
                // a blur and not copies of the window side by side.
                float spread = along * 0.35f * spreadDial * targetPixelsPerTile;
                int reads = Math.Clamp((int)MathF.Ceiling(spread) + 1, 2, 16);
                // Spreading the same light over a wider band thinned it out, and a wide beam faded
                // away early ("once it spreads the light gets shorter", 15 Oct 2026). So the light is
                // brought back up by how much wider the band now is than a pane (about a tile), and
                // a beam reaches as far whatever its spread.
                float widening = (targetPixelsPerTile + 2f * spread) / targetPixelsPerTile;
                float perRead = 2f * widening / reads;
                // The curtain colour goes in every channel and in alpha alike, by the same byte, so
                // the shader's colour over alpha gives the hue back exactly, however faint the copy.
                var share = curtainColour
                    ? new Color(new Vector4((rayWeight + floorWeight) * 0.5f * perRead))
                    : new Color(colour.ToVector4() * new Vector4(perRead, perRead, perRead, 1f));
                for (int across = 0; across < reads; across++)
                {
                    float side = spread * (across * 2f / (reads - 1) - 1f);
                    _windowGlassBatch!.Draw(_windowGlassPaintTarget!, whole.Location.ToVector2() + offset + new Vector2(side, 0f),
                        null, share, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
                }
            }
        }

        /// <summary>Tiles of windows checked and found to have no glass, met in the last build:
        /// nothing is drawn for them, but their tiles count as labelled, so their old light goes
        /// and no glass light comes. Left out of the file, such a window looked like one nobody had
        /// labelled and kept the old light (a boarded window glowing).</summary>
        private int _windowGlassTilesKnownClear;

        private void PaintGlassCell(Rectangle destination, WindowGlassTile glass, SpriteEffects effects)
        {
            if (!EnsureAtlasCell(glass))
            {
                _windowGlassTilesKnownClear++;
                return;
            }
            _windowGlassBatch!.Draw(_windowGlassAtlas!, destination, AtlasSource(glass.AtlasCell),
                Color.White, 0f, Vector2.Zero, effects, 0f);
            _windowGlassCellsPainted.Add((destination, glass.AtlasCell, effects));
            _windowGlassTilesWithGlass.Add(new Point((int)MathF.Floor(destination.Center.X / 64f), (int)MathF.Floor(destination.Center.Y / 64f)));
            _windowGlassSheerPainted |= glass.Sheer;
            WindowGlassTilesDrawn++;
        }

        private void PaintMapGlass(GameLocation location, Rectangle covered)
        {
            int firstX = Math.Max(0, covered.X / 64 - 1), firstY = Math.Max(0, covered.Y / 64 - 1);
            int lastX = covered.Right / 64 + 1, lastY = covered.Bottom / 64 + 1;
            foreach (Layer layer in MapLayers.RenderedLayers(location.map, topToBottom: false))
            {
                // Windows are wall art: Buildings, Front and their numbered layers. Back carries the
                // patch of sunlight the game paints under a window, which is light, not glass.
                if (!MapLayers.BelongsToFamily(layer.Id, "Buildings") && !MapLayers.BelongsToFamily(layer.Id, "Front")
                    && !MapLayers.BelongsToFamily(layer.Id, "AlwaysFront"))
                    continue;
                for (int y = firstY; y <= Math.Min(lastY, layer.LayerHeight - 1); y++)
                {
                    for (int x = firstX; x <= Math.Min(lastX, layer.LayerWidth - 1); x++)
                    {
                        var tile = layer.Tiles[x, y];
                        if (tile?.TileSheet?.ImageSource == null)
                            continue;
                        string sheet = LabelStore.NormalizeSheet(tile.TileSheet.ImageSource);
                        if (!_windowGlassBySheet.TryGetValue(sheet, out var tiles) || !tiles.TryGetValue(tile.TileIndex, out WindowGlassTile[]? variants))
                            continue;
                        WindowGlassTile? glass = GlassMatchingArt(sheet, tile.TileIndex, variants, () =>
                            LabelStore.ArtFingerprintReader is { } reader && reader(layer, x, y, out ulong fingerprint) ? fingerprint : null);
                        if (glass == null)
                            continue;
                        PaintGlassCell(new Rectangle(x * 64, y * 64, 64, 64), glass, SpriteEffects.None);
                        _windowGlassTilesBuilding.Add(new Point(x, y));
                    }
                }
            }
        }

        private void PaintFurnitureGlass(GameLocation location)
        {
            foreach (Furniture furniture in location.furniture)
            {
                if (furniture?.furniture_type.Value != Furniture.window)
                    continue;
                Texture2D? texture;
                try { texture = ItemRegistry.GetDataOrErrorItem(furniture.QualifiedItemId).GetTexture(); }
                catch { continue; }
                if (texture == null || texture.IsDisposed)
                    continue;
                // A pack's own texture can come with no asset name at all; its art still answers.
                string sheet = string.IsNullOrEmpty(texture.Name) ? "" : LabelStore.NormalizeSheet(texture.Name);
                _windowGlassBySheet.TryGetValue(sheet, out var tiles);
                if (tiles == null && _windowGlassByArt.Count == 0)
                    continue;
                Rectangle source = furniture.sourceRect.Value;
                int columns = texture.Width / 16;
                // Where the game draws it (Furniture.draw): the sprite's bottom on the bounding
                // box's bottom, four screen pixels to an art pixel.
                Rectangle box = furniture.boundingBox.Value;
                var origin = new Vector2(furniture.TileLocation.X * 64f, furniture.TileLocation.Y * 64f - (source.Height * 4 - box.Height));
                for (int cellY = 0; cellY < source.Height; cellY += 16)
                {
                    for (int cellX = 0; cellX < source.Width; cellX += 16)
                    {
                        int index = ((source.Y + cellY) / 16) * columns + (source.X + cellX) / 16;
                        var cell = new Rectangle(source.X + cellX, source.Y + cellY, 16, 16);
                        WindowGlassTile[]? named = null;
                        tiles?.TryGetValue(index, out named);
                        WindowGlassTile? glass = FurnitureGlass(texture, cell, named);
                        if (glass == null)
                            continue;
                        int drawnX = furniture.Flipped ? source.Width - 16 - cellX : cellX;
                        PaintGlassCell(new Rectangle((int)origin.X + drawnX * 4, (int)origin.Y + cellY * 4, 64, 64), glass,
                            furniture.Flipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
                        _windowGlassTilesBuilding.Add(new Point(((int)origin.X + drawnX * 4) / 64, ((int)origin.Y + cellY * 4) / 64));
                    }
                }
            }
        }

        /// <summary>The glass of one cell of a furniture texture: the variant of its sheet name and
        /// index whose art it is, or else any labelled tile with that art. Kept per texture, not
        /// per sheet name, since two packs' textures can carry the same name.</summary>
        private WindowGlassTile? FurnitureGlass(Texture2D texture, Rectangle cell, WindowGlassTile[]? named)
        {
            if (_windowGlassByCell.TryGetValue((texture, cell), out WindowGlassTile? known))
            {
                if (known == null && named != null)
                    WindowGlassRefusedForArt++;
                return known;
            }
            ulong? fingerprint = LabelStore.SheetFingerprintReader is { } reader && reader(texture, cell, out ulong read) ? read : null;
            WindowGlassTile? found = named == null ? null : VariantForArt(named, fingerprint);
            if (found == null && fingerprint != null)
                _windowGlassByArt.TryGetValue(fingerprint.Value, out found);
            _windowGlassByCell[(texture, cell)] = found;
            if (found == null && named != null)
                WindowGlassRefusedForArt++;
            return found;
        }

        private WindowGlassTile? GlassMatchingArt(string sheet, int index, WindowGlassTile[] variants, Func<ulong?> readFingerprint)
        {
            if (!_windowGlassArtVerdict.TryGetValue((sheet, index), out WindowGlassTile? known))
            {
                known = VariantForArt(variants, readFingerprint());
                _windowGlassArtVerdict[(sheet, index)] = known;
            }
            if (known == null)
                WindowGlassRefusedForArt++;
            return known;
        }

        /// <summary>The variant whose art is this fingerprint. Cannot tell (no reader yet) is not
        /// a mismatch, the rule every label guard follows, and takes the first.</summary>
        private static WindowGlassTile? VariantForArt(WindowGlassTile[] variants, ulong? fingerprint)
        {
            foreach (WindowGlassTile variant in variants)
                if (variant.Art.Count == 0 || fingerprint == null || variant.Art.Contains(fingerprint.Value))
                    return variant;
            return null;
        }

        /// <summary>
        /// The game's own window glow, faded where the glass now makes the light.
        /// </summary>
        /// <remarks>
        /// <para>
        /// GameLocation.drawLightGlows lays a tall white glow (Cursors 21,1695, 41 by 67, at four
        /// times) over every window by day. Under the bloom it is the big white blur that hid every
        /// window's shape (reported with a picture of Haley's house on 15 Oct 2026: "it doesn't show
        /// the shape at all"), and with the light now coming from the glass it is the same light
        /// twice. So where a glow sits on a window whose glass is labelled, its colour eases out
        /// with the glass-shaped light; a window without a label keeps its glow, and switching the
        /// shape off brings every glow back. Only the colour of the game's own draw is changed.
        /// </para>
        /// </remarks>
        internal static IEnumerable<CodeInstruction> LightGlows_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw),
                [typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float)]);
            var shim = AccessTools.Method(typeof(RenderPipeline), nameof(DrawGameWindowGlow));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(draw))
                    yield return new CodeInstruction(OpCodes.Call, shim) { labels = instruction.labels, blocks = instruction.blocks };
                else
                    yield return instruction;
            }
        }

        public static void DrawGameWindowGlow(SpriteBatch spriteBatch, Texture2D texture, Vector2 position, Rectangle? source,
            Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        {
            float keep = Current?.GameWindowGlowKeep(position) ?? 1f;
            if (keep <= FadeGone)
                return;
            spriteBatch.Draw(texture, position, source, color * keep, rotation, origin, scale, effects, layerDepth);
        }

        private float GameWindowGlowKeep(Vector2 screenPosition)
        {
            if (_windowGlassMixEase <= FadeGone || _windowGlassTilesShown.Count == 0
                || !ReferenceEquals(Game1.currentLocation, _windowGlassVerdictLocation))
                return 1f;
            int tileX = (int)((screenPosition.X + Game1.viewport.X) / 64f);
            int tileY = (int)((screenPosition.Y + Game1.viewport.Y) / 64f);
            // The glow's anchor sits on the window's top tile or a little above it.
            for (int dy = -1; dy <= 2; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (_windowGlassTilesShown.Contains(new Point(tileX + dx, tileY + dy)))
                        return 1f - _windowGlassMixEase;
            return 1f;
        }

        /// <summary>Forget the art verdicts, for when a mod reloads a sheet.</summary>
        internal void ForgetWindowGlassVerdicts()
        {
            _windowGlassArtVerdict.Clear();
            _windowGlassByCell.Clear();
            _windowGlassArtEpoch++;
        }

        /// <summary>radiance_windowglass: the window glass target as a picture, to see what the
        /// window light will be shaped by.</summary>
        internal string DumpWindowGlass(string folder)
        {
            if (_windowGlassTarget == null || _windowGlassTarget.IsDisposed)
                return "no window glass target yet (stand in a windowed room with window effects on)";
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "window-glass.png");
            // The target holds half floats, which SaveAsPng cannot write: read them back and copy
            // them into an ordinary picture first.
            var halves = new Microsoft.Xna.Framework.Graphics.PackedVector.HalfVector4[_windowGlassTarget.Width * _windowGlassTarget.Height];
            _windowGlassTarget.GetData(halves);
            var colours = new Color[halves.Length];
            for (int i = 0; i < halves.Length; i++)
                colours[i] = new Color(halves[i].ToVector4());
            using (var picture = new Texture2D(_device, _windowGlassTarget.Width, _windowGlassTarget.Height))
            using (FileStream stream = File.Create(path))
            {
                picture.SetData(colours);
                picture.SaveAsPng(stream, picture.Width, picture.Height);
            }
            bool shaderTakesIt = _floodEffect != null && GetParam(_floodEffect, "WindowGlass") != null
                                 && GetParam(_floodEffect, "WindowGlassTexture") != null;
            return $"{WindowGlassTilesDrawn} glass tiles painted, {_windowGlassTilesKnownClear} known to have none, {WindowGlassRefusedForArt} refused for changed art, "
                 + $"painted {WindowGlassBuilds} times and reused {WindowGlassBuildsSkipped} times since launch, "
                 + $"{WindowGlassTilesLoaded} labelled in all ({_windowGlassInAtlas.Count} unpacked for this game), mix {_windowGlassMixEase:0.00}, "
                 + $"flood shader {(shaderTakesIt ? "takes the glass" : "HAS NO glass parameters")} -> {path}";
        }

        /// <summary>
        /// Whether the window whose light stands at this world position had its glass painted in
        /// the last build: a painted tile at the light's tile, beside it, or up to two above.
        /// </summary>
        /// <remarks>
        /// The flood shader lets only these windows give up the old pane and beam for the glass's
        /// own. Switching the whole view over left a window nobody labelled with no light of ours
        /// at all whenever a labelled one shared the room.
        /// </remarks>
        private bool WindowGlassLabelledAt(Vector2 lightWorld)
        {
            if (WindowGlassMask == null || _windowGlassTilesShown.Count == 0)
                return false;
            // Across, only the tiles within a little under half a tile of the light: a window one tile
            // wide has its light over its own middle, one two tiles wide between its two. A whole
            // tile either side counted the next window along, and a window nobody labelled lost its
            // light for standing beside one that was.
            int tileY = (int)MathF.Floor(lightWorld.Y / 64f);
            int firstX = (int)MathF.Floor((lightWorld.X - 24f) / 64f), lastX = (int)MathF.Floor((lightWorld.X + 24f) / 64f);
            for (int y = tileY - 2; y <= tileY + 1; y++)
                for (int x = firstX; x <= lastX; x++)
                    if (_windowGlassTilesShown.Contains(new Point(x, y)))
                        return true;
            return false;
        }

        private void ReleaseWindowGlass()
        {
            _windowGlassTarget?.Dispose(); _windowGlassTarget = null;
            _windowGlassPaintTarget?.Dispose(); _windowGlassPaintTarget = null;
            _windowGlassAtlas?.Dispose(); _windowGlassAtlas = null;
            _windowGlassTintAtlas?.Dispose(); _windowGlassTintAtlas = null;
            foreach (WindowGlassTile tile in _windowGlassInAtlas)
                tile.AtlasCell = NoAtlasCell;
            _windowGlassInAtlas.Clear();
            _windowGlassAtlasRows = 0;
            foreach (Texture2D retired in _windowGlassRetiredAtlases)
                retired.Dispose();
            _windowGlassRetiredAtlases.Clear();
            _windowGlassTintTarget?.Dispose(); _windowGlassTintTarget = null;
            _windowGlassNoTint?.Dispose(); _windowGlassNoTint = null;
            _windowGlassTintPainted = false;
            WindowGlassMask = null;
        }
    }
}
