using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The people who walk past a window show faintly in its glass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule runs the whole feature: glass reflects when what is behind it is darker than what
    /// is in front. A house window by day has a dark room behind it and a lit street in front, so
    /// it mirrors the street plainly; the same window at night is lit from inside and mostly shows
    /// the room through, so the image thins to a suggestion. The mod already lights windows after
    /// dusk with <c>night</c>, and this fades against it on the same ramp, down to a share rather
    /// than to nothing: a lit pane still returns a little of what stands in front of it, and an
    /// image that disappeared at the stroke of dusk read as the feature breaking.
    /// </para>
    /// <para>
    /// The pane is where the label says it is: classes 12 (window), 13 (glass) and 8 (mirror), each
    /// with its own strength, clipped to the labelled pixels of each tile so a frame around the
    /// glass stays a frame. The image is the body's own current frame, upright, standing on the
    /// pane's bottom edge - the same picture the water gets, anchored to a sill instead of a
    /// waterline, and tinted with the same synthesised sky the water reflects, so the glass turns
    /// gold in the evening with the street rather than staying at noon all day. Only what is drawn as a person is mirrored: the player from the colour bake the
    /// shadow pass already keeps, everyone else from their sprite. Both fade with distance from
    /// the pane, so someone four tiles away is a suggestion and someone at the sill is a
    /// reflection.
    /// </para>
    /// <para>
    /// A pane is drawn as glass whether or not anyone is standing at it: a faint wash of the sky's
    /// colour, more of it at the top, and a soft glare that travels across the pane as it crosses
    /// the screen, and the street in front of it, squashed into the pane from the same sprite-free
    /// map render the water mirror reads. Without that half a window with nobody near it was a hole
    /// in a wall, and the feature only existed while someone walked.
    /// After dark the lamps of the street stand in it too, each a soft blot of its own colour,
    /// which is what a town's windows do at night and what the daytime picture fades away to leave.
    /// All three ADD light rather than mixing with the art (see <see cref="AddedLight"/>): that is
    /// what glass does, and it is the difference between an effect that shows on a shop front and
    /// one that only ever showed on dark cottage windows.
    /// </para>
    /// <para>
    /// Outdoors only for now. Indoors the rule flips (the outside is darker at night than the room
    /// is), and that half waits for the indoor window work.
    /// </para>
    /// </remarks>
    internal sealed partial class RenderPipeline
    {
        /// <summary>One labelled pane on the map: where it is in world pixels and how mirror-like
        /// its class is.</summary>
        private readonly struct WindowPane
        {
            /// <summary>The whole window: the box around every glass tile that touches, which is
            /// where the sill is and what decides whether the window is on screen.</summary>
            public readonly Rectangle WorldRect;
            /// <summary>The glass itself, as the label paints it: one box per run of glass pixels,
            /// rows with the same span merged. A door with two small lights is two boxes; a bus
            /// windshield with a slanted edge is a staircase of them. Clipped to a tile's bounding
            /// box instead, the image ran over the bodywork beside the slant.</summary>
            public readonly Rectangle[] GlassRects;
            public readonly float Strength;
            /// <summary>Tiles between this pane's sill and the ground somebody could stand on
            /// under it. A shop front is one or two; a dormer in a roof is five, and what you can
            /// see in a dormer from the street is sky, not the road you are standing on.</summary>
            public readonly int TilesAboveGround;
            public WindowPane(Rectangle worldRect, Rectangle[] glassRects, float strength, int tilesAboveGround = 0)
            { WorldRect = worldRect; GlassRects = glassRects; Strength = strength; TilesAboveGround = tilesAboveGround; }
        }

        private const byte LabelClassMirror = 8;
        private const byte LabelClassWindow = 12;
        private const byte LabelClassGlass = 13;

        /// <summary>How far below a pane's sill a body still reflects in it, in world pixels.</summary>
        private const float WindowReflectReachPx = 64f * 4f;
        /// <summary>
        /// How far below the sill the image's feet sit.
        /// </summary>
        /// <remarks>
        /// Zero, by decision: the feet go on the sill and the whole body stands in the pane. A
        /// version that dropped the image by the pane's own height was tried, so that a short
        /// pane high on a bus would cut the legs off the way real glass does, and it took a tile
        /// off every shop window as well. The bus is the odd case and the shop fronts are most of
        /// them, so the odd case waits for a rule that can tell the two apart.
        /// </remarks>
        private const float WindowStandingOffsetPx = 0f;

        /// <summary>Weights that turn a colour into how bright it looks, so the tint below can be
        /// recoloured without getting brighter or darker.</summary>
        private static readonly Vector3 GlassTintLuminanceWeights = new(0.2126f, 0.7152f, 0.0722f);
        /// <summary>How bright the glass tint is, whatever colour it happens to be. Brightness is
        /// the day and night sliders' job; the sky is only allowed to choose the colour, so an
        /// evening pane returns a warm picture rather than a dimmer one.</summary>
        private const float GlassTintLuminance = 0.79f;
        /// <summary>How far the tint travels from plain grey toward the sky's own colour. Not all
        /// the way: a window returns the street as well as the sky, and a pane that went fully to
        /// a midnight blue coloured the people in it more than the evening colours the street.</summary>
        private const float GlassSkyShare = 0.75f;

        /// <summary>World pixels per art pixel.</summary>
        private const int WorldPixelsPerArtPixel = 4;
        /// <summary>How much light a pane gains before anything is reflected in it, and how much
        /// more of it near the top, where a window catches the open sky rather than the street.
        /// These are added rather than mixed in (see <see cref="AddedLight"/>), so they read the
        /// same on a dark cottage window and on the pale glass of a shop front.</summary>
        private const float GlassSkyWashAlpha = 0.13f;
        private const float GlassSkyWashTopAlpha = 0.09f;
        /// <summary>How far in front of a pane the glass returns the world, in world pixels. Six
        /// tiles: far enough to reach the building across a street, near enough that the strip is
        /// still on screen for a window in the upper half of it.</summary>
        private const int GlassSceneReachPx = 64 * 6;
        /// <summary>How brightly the street shows in the glass. Faint by nature: a shop window
        /// returns a hint of the road, not a second copy of it. Faint is not invisible, though,
        /// and the first number here landed on invisible.</summary>
        private const float GlassSceneAlpha = 0.55f;
        /// <summary>How far down the walk for ground goes, and between which two heights a pane
        /// stops returning the street. Two tiles is a shop front or a cottage window with a flower
        /// box under it; five is a dormer or a second storey, where the angle that reaches your eye
        /// left the sky rather than the road.</summary>
        private const int HighPaneWalkTiles = 6;
        private const int HighPaneNearTiles = 2;
        private const int HighPaneFarTiles = 5;
        /// <summary>What a high pane keeps of the street, the lamps and the people. Not nothing: a
        /// second-storey window still catches a little, and windows that switched off entirely at
        /// some height would pop as the camera moved past a row of them.</summary>
        private const float HighPaneShare = 0.2f;

        /// <summary>Where in a pane the street starts to show and where it is fully there, as a
        /// share of the pane's height from its top. A reflection standing in a window is strongest
        /// along the sill and gone before the head of the frame: filling the pane to the top read
        /// as the glass having been replaced by a picture of the road.</summary>
        private const float GlassSceneFadeStart = 0.35f;
        private const float GlassSceneFadeFull = 0.80f;
        /// <summary>How many bands the fade is drawn in. The batch has no gradient of its own, so
        /// the strip is cut into slices each with its own amount; eight is past the point where
        /// the steps read as bands rather than as a fade.</summary>
        private const int GlassSceneFadeBands = 8;
        /// <summary>The street sits under the glare and over the sky wash.</summary>
        private const float GlassSceneDepthNudge = 0.000002f;
        /// <summary>How bright the glare is at the middle of the blot, and how much wider than the
        /// pane the blot is. Wider than the pane on purpose: only the middle of it lands on the
        /// glass, so what the pane wears is a smooth gradient rather than a shape with an edge.</summary>
        private const float GlassGlareAlpha = 0.22f;
        private const float GlassGlareSpread = 1.6f;
        /// <summary>How far the glare travels across a pane between the left edge of the screen and
        /// the right, as a share of the pane's own width. This is the whole cue that the glass is
        /// returning light rather than wearing a painted-on highlight.</summary>
        private const float GlassGlareTravel = 0.9f;
        /// <summary>What is left of the sky in the glass after dusk: the pane is lit from inside by
        /// then and the sky behind you has gone.</summary>
        private const float GlassSheenNightShare = 0.3f;
        /// <summary>How much of the class ladder the sky obeys. A house window catches the sky as
        /// well as a shop front does, even though it returns far less of the street, so the ladder
        /// that decides the reflection is flattened here rather than used as it stands.</summary>
        private const float GlassSheenLadderFloor = 0.6f;
        /// <summary>Three layers at one sill need three depths. Equal ones sort in whatever order
        /// the batch's sort happens to leave them in, which can differ between frames.</summary>
        private const float GlassBodyDepthNudge = 0.00001f;
        private const float GlassGlareDepthNudge = 0.00002f;

        /// <summary>How far from a pane a lamp still shows in its glass, in world pixels, measured
        /// from the pane's box in whatever direction the lamp lies.</summary>
        /// <remarks>One reach in every direction, deliberately. It was two - six tiles below the
        /// sill and two above - and the head of a street lamp stands higher above a window's sill
        /// than that, so the lamp right outside the clinic was thrown out by the test before
        /// anything was drawn. A gap to a rectangle has no such corners to get wrong.</remarks>
        private const float GlassGlowReachPx = 64f * 6f;
        /// <summary>How wide a lamp's blot is in the glass, as a share of the pool it casts on the
        /// ground. Much smaller than the pool, and small on purpose: what a window returns of a
        /// street lamp is the LAMP, a small bright thing, not the patch of road it lit. At better
        /// than half the pool a one-tile pane only ever caught the faintest outer edge of the blot,
        /// which is drawn, counted, and invisible.</summary>
        private const float GlassGlowSizeShare = 0.22f;
        /// <summary>How bright the middle of that blot is. Over one on purpose: this is added
        /// before the game multiplies the world by its own night lightmap, and after dark that
        /// multiply is most of a half.</summary>
        private const float GlassGlowAlpha = 1.3f;
        private const float GlassGlowFarScale = 0.5f;
        /// <summary>Lamps sit between the sky wash and the people, all at one sill.</summary>
        private const float GlassGlowDepthNudge = 0.000005f;
        /// <summary>Where the debug overlay's lamps go: above its own red glass, which is drawn at
        /// the glare's depth. Painted at the lamps' real depth they went UNDER that red and the
        /// overlay reported no lamps at all, sending the hunt after the reach test instead.</summary>
        private const float GlassDebugGlowDepthNudge = 0.00004f;

        private Texture2D? _glassGlowTexture;
        private bool _glassGlowTextureMissing;

        /// <summary>What the last drawn frame actually did with windows, for radiance_report. "I
        /// see nothing" has two completely different causes that look the same from the street -
        /// no labelled pane on this map, or a pane that something is drawn over - and neither can
        /// be told from the other by looking.</summary>
        internal int WindowPanesInLocation;
        internal int WindowPanesOnScreen;
        internal float WindowReflectUploaded;
        internal float WindowSheenUploaded;
        internal float WindowGlareUploaded;
        internal float WindowSceneUploaded;
        /// <summary>Set pre-frame: a pane is on screen and wants the sprite-free map render that
        /// the water mirror also reads. The water pass only bakes it when there is water on
        /// screen, and a street full of windows usually has none.</summary>
        internal bool WindowsWantSceneryMirror;
        internal float WindowLampGlowUploaded;
        /// <summary>Blots of lamplight actually drawn into glass last frame. Separates "no lamp is
        /// near enough to any pane" from "drawn, and too faint to see", which the amount alone
        /// cannot: both look like a dial that does nothing.</summary>
        internal int WindowLampsDrawn;
        /// <summary>Lights the glow pass had to choose from. Zero here means the light list was
        /// never gathered, which is a different fault from every light being out of reach.</summary>
        internal int WindowLampsConsidered;
        internal bool GlassGlowTextureMissing => _glassGlowTextureMissing;

        private readonly List<WindowPane> _windowPanes = new();
        private GameLocation? _windowPaneLocation;
        private int _windowPaneLabelVersion = -1;
        private float _windowReflectEase;

        /// <summary>How much smaller the image is at the far end of its reach: glass across a
        /// street is not a mirror held to the face, and a body that shrinks as it walks away reads
        /// as standing in the pane rather than pasted on it.</summary>
        private const float WindowReflectFarScale = 0.55f;

        /// <summary>The player as the glass sees them, baked pre-frame where a render-target
        /// swap is legal. Facing the window they show their face, facing away their back, facing
        /// along it the frame the world draws. Its own bake rather than the shadow pass's colour
        /// twin: that one is only made while water is on screen, so borrowing it had the image
        /// vanish the moment you turned sideways in a street with no water.</summary>
        private RenderTarget2D? _windowPlayerBake;
        private bool _windowPlayerBakeFresh;
        private SpriteBatch? _windowBakeSpriteBatch;
        /// <summary>The held tool was baked this frame (the water mirror's own scratch target,
        /// upright, feet at a known row) and can stand in the glass beside the body.</summary>
        private bool _windowToolBakeFresh;

        /// <summary>The colour the glass hands back at this hour and in this weather: the sky's own
        /// colour, lifted to a fixed brightness and pulled part of the way back toward grey.</summary>
        /// <remarks>It used to be one cool blue for every hour of every day. At noon that is what
        /// glass looks like; at six in the evening the street outside is gold, the wall beside the
        /// window is gold, and the person in the glass was still lit for a midday sky.</remarks>
        private static Vector3 GlassReflectionTint()
        {
            var (sunWarm, nightGlow) = TimeOfDayAmounts();
            Vector3 sky = SynthesisedSkyColour(sunWarm, nightGlow);
            float luminance = Math.Max(0.02f, Vector3.Dot(sky, GlassTintLuminanceWeights));
            Vector3 skyAtGlassBrightness = sky * (GlassTintLuminance / luminance);
            return Vector3.Clamp(
                Vector3.Lerp(new Vector3(GlassTintLuminance), skyAtGlassBrightness, GlassSkyShare),
                Vector3.Zero, Vector3.One);
        }

        /// <summary>Opposite of up is down, of down is up; left and right stay, because a mirror on
        /// the wall in front of you flips front and back, not your left and your right.</summary>
        private static bool TryTurnedFacing(int facing, out int turned)
        {
            turned = facing;
            if (facing == 0) { turned = 2; return true; }
            if (facing == 2) { turned = 0; return true; }
            return false;
        }

        /// <summary>Pre-frame: bake the local player as the glass sees them. Skipped entirely when
        /// no pane is on screen, so a street without windows pays nothing.</summary>
        internal void BakeWindowReflectionPlayer(ModConfig config)
        {
            _windowPlayerBakeFresh = false;
            _windowToolBakeFresh = false;
            WindowsWantSceneryMirror = false;
            var who = Game1.player;
            var location = Game1.currentLocation;
            if (!config.WindowReflectionEnabled || location == null || !location.IsOutdoors)
                return;
            EnsureWindowPaneCache(location);
            if (_windowPanes.Count == 0)
                return;
            var viewport = Game1.viewport;
            var screen = new Rectangle(viewport.X - 64, viewport.Y - 64, viewport.Width + 128, viewport.Height + 128);
            bool anyPaneOnScreen = false;
            foreach (var pane in _windowPanes)
                if (screen.Intersects(pane.WorldRect)) { anyPaneOnScreen = true; break; }
            if (!anyPaneOnScreen)
                return;
            // Asked for BEFORE the player checks below: whether the glass returns the street does
            // not depend on whether there is anybody standing at it, and a swimming player used to
            // take the whole pass out on the way past.
            WindowsWantSceneryMirror = config.WindowSceneReflectionStrength > 0.01f;
            if (who == null || who.swimming.Value || who.FarmerRenderer == null || who.FarmerSprite == null)
                return;
            bool turned = TryTurnedFacing(who.FacingDirection, out int facingForGlass);

            // Facing the glass or away from it: the standing frame of the opposite facing (down
            // is frame 0, up is frame 12 on the farmer sheet, six frames to a row of 32 px).
            // Facing along it: the very frame the world is drawing, animation and all.
            int frame;
            Rectangle source;
            FarmerSprite.AnimationFrame animation;
            if (turned)
            {
                // The same STEP of the walk, on the opposite row: walking and running down use
                // frames 0-2 and up 12-14, so a stride carries across by twelve, bob and all.
                // Anything outside those rows (a tool swing, a special pose) falls back to standing,
                // which is what the glass would see of a pose it has no mirror for.
                var current = who.FarmerSprite.CurrentAnimationFrame;
                int currentFrame = who.FarmerSprite.CurrentFrame;
                if (currentFrame >= 0 && currentFrame <= 2) frame = currentFrame + 12;
                else if (currentFrame >= 12 && currentFrame <= 14) frame = currentFrame - 12;
                else frame = facingForGlass == 2 ? 0 : 12;
                source = new Rectangle(frame % 6 * 16, frame / 6 * 32, 16, 32);
                animation = new FarmerSprite.AnimationFrame(frame, 0, current.positionOffset, false, current.flip);
            }
            else
            {
                frame = who.FarmerSprite.CurrentFrame;
                source = who.FarmerSprite.SourceRect;
                animation = who.FarmerSprite.CurrentAnimationFrame;
            }

            _windowPlayerBake ??= VramTally.Track(new RenderTarget2D(_device, ShadowRenderer.PlayerRtW, ShadowRenderer.PlayerRtH,
                false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "window player");
            _windowBakeSpriteBatch ??= new SpriteBatch(_device);
            var previous = _device.GetRenderTargets();
            try
            {
                float width = source.Width * 4f, height = source.Height * 4f;
                // Same placement as the colour bake: centred, feet eight rows above the bottom.
                var position = new Vector2((ShadowRenderer.PlayerRtW - width) / 2f, ShadowRenderer.PlayerRtH - height - 8f);
                _device.SetRenderTarget(_windowPlayerBake);
                _device.Clear(Color.Transparent);
                _windowBakeSpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                who.FarmerRenderer.draw(_windowBakeSpriteBatch, animation, frame, source, position,
                    Vector2.Zero, 0f, facingForGlass, Color.White, 0f, 1f, who);
                _windowBakeSpriteBatch.End();
                _windowPlayerBakeFresh = true;
                // The tool in hand - a rod held out, an axe mid-swing - through the same bake the
                // water mirror uses, since it already knows how to catch a tool drawn outside the
                // body's own frame. Without it the glass showed a fisher with no rod.
                _spriteMaskSpriteBatch ??= new SpriteBatch(_device);
                _windowToolBakeFresh = BakeHeldToolForMirror();
            }
            catch (Exception ex)
            {
                try { _windowBakeSpriteBatch?.End(); } catch { }
                _monitor.Log($"[window] player bake threw: {ex.Message}", StardewModdingAPI.LogLevel.Trace);
            }
            finally
            {
                _device.SetRenderTargets(previous);
            }
        }

        /// <summary>Scan the map once per location (or when labels reload) for every tile that
        /// carries pane pixels, and remember the pane's box and class.</summary>
        private void EnsureWindowPaneCache(GameLocation location)
        {
            var labels = LabelStore.Instance;
            int version = labels?.Version ?? 0;
            if (ReferenceEquals(location, _windowPaneLocation) && version == _windowPaneLabelVersion)
                return;
            _windowPaneLocation = location;
            _windowPaneLabelVersion = version;
            _windowPanes.Clear();
            var map = location.map;
            var firstLayer = map != null && map.Layers.Count > 0 ? map.Layers[0] : null;
            if (labels == null || firstLayer == null || map == null || version == 0)
                return;
            int width = firstLayer.LayerWidth, height = firstLayer.LayerHeight;
            var layers = MapLayers.RenderedLayers(map, topToBottom: true);
            // Per-tile pane boxes first...
            var tilePanes = new Dictionary<Point, WindowPane>();
            for (int tileY = 0; tileY < height; tileY++)
                for (int tileX = 0; tileX < width; tileX++)
                {
                    foreach (var layer in layers)
                    {
                        byte[]? classes = labels.Get(layer, tileX, tileY);
                        if (classes == null)
                            continue;
                        if (TryPaneBox(classes, out Rectangle paneBox, out float strength))
                        {
                            var glassRect = new Rectangle(tileX * 64 + paneBox.X * 4, tileY * 64 + paneBox.Y * 4,
                                paneBox.Width * 4, paneBox.Height * 4);
                            tilePanes[new Point(tileX, tileY)] = new WindowPane(glassRect,
                                GlassRuns(classes, tileX, tileY).ToArray(), strength);
                            break;
                        }
                    }
                }
            // ...then merged: a shop front two tiles tall is ONE window, and a body standing in it
            // is one image standing on one sill. Drawn per tile, the same person appeared twice at
            // two heights, cut at the tile seam, which is exactly what was reported.
            var merged = new HashSet<Point>();
            var stack = new Stack<Point>();
            foreach (var start in tilePanes.Keys)
            {
                if (merged.Contains(start))
                    continue;
                Rectangle union = tilePanes[start].WorldRect;
                float strength = tilePanes[start].Strength;
                var glassRects = new List<Rectangle>(tilePanes[start].GlassRects);
                stack.Push(start);
                merged.Add(start);
                while (stack.Count > 0)
                {
                    Point here = stack.Pop();
                    foreach (var step in new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
                    {
                        var next = new Point(here.X + step.X, here.Y + step.Y);
                        if (merged.Contains(next) || !tilePanes.TryGetValue(next, out var neighbour))
                            continue;
                        merged.Add(next);
                        stack.Push(next);
                        union = Rectangle.Union(union, neighbour.WorldRect);
                        glassRects.AddRange(neighbour.GlassRects);
                        strength = Math.Max(strength, neighbour.Strength);
                    }
                }
                _windowPanes.Add(new WindowPane(union, glassRects.ToArray(), strength,
                    TilesAboveGround(location, union)));
            }
        }

        /// <summary>How far a pane's sill stands above the ground below it, in tiles: walk down
        /// the middle of the pane until a tile somebody could stand on. Anything further than the
        /// walk goes counts as high, which is the safe answer for a pane over a cliff or a pond.</summary>
        private static int TilesAboveGround(GameLocation location, Rectangle paneWorldRect)
        {
            int tileX = paneWorldRect.Center.X / 64;
            int sillRow = (paneWorldRect.Bottom - 1) / 64;
            for (int step = 1; step <= HighPaneWalkTiles; step++)
            {
                var below = new xTile.Dimensions.Location(tileX, sillRow + step);
                if (location.isTilePassable(below, Game1.viewport))
                    return step;
            }
            return HighPaneWalkTiles;
        }

        /// <summary>One axis of a blot's middle, kept where the glass can show it: the lamp's own
        /// place while the blot still fits inside the pane on that axis, and the middle of the pane
        /// once it does not.</summary>
        private static float KeptOnTheGlass(float lampPlace, float paneStart, float paneEnd, float blotSize)
        {
            if (paneEnd - paneStart <= blotSize)
                return (paneStart + paneEnd) * 0.5f;
            return MathHelper.Clamp(lampPlace, paneStart + blotSize * 0.5f, paneEnd - blotSize * 0.5f);
        }

        /// <summary>How much of what stands in FRONT of a pane it returns, given how high it is.
        /// The sky and the glare are left alone: a dormer sees more sky, not less.</summary>
        private static float GroundShareFor(WindowPane pane)
        {
            float ramp = MathHelper.Clamp(
                (pane.TilesAboveGround - HighPaneNearTiles) / (float)(HighPaneFarTiles - HighPaneNearTiles), 0f, 1f);
            return MathHelper.Lerp(1f, HighPaneShare, ramp * ramp * (3f - 2f * ramp));
        }

        /// <summary>The glass pixels of one tile as world-pixel boxes: each label row's span of
        /// pane pixels is one box four world pixels tall, and consecutive rows with the same span
        /// merge into one taller box, so a plain rectangular pane is a single box and a slanted
        /// windshield a short staircase. A row with two separate spans keeps both.</summary>
        private static List<Rectangle> GlassRuns(byte[] classes, int tileX, int tileY)
        {
            var runs = new List<Rectangle>();
            for (int y = 0; y < 16; y++)
            {
                int x = 0;
                while (x < 16)
                {
                    byte c = classes[y * 16 + x];
                    bool isGlass = c == LabelClassWindow || c == LabelClassGlass || c == LabelClassMirror;
                    if (!isGlass) { x++; continue; }
                    int runStart = x;
                    while (x < 16)
                    {
                        byte d = classes[y * 16 + x];
                        if (d != LabelClassWindow && d != LabelClassGlass && d != LabelClassMirror)
                            break;
                        x++;
                    }
                    var run = new Rectangle(tileX * 64 + runStart * 4, tileY * 64 + y * 4, (x - runStart) * 4, 4);
                    // Same span as the box directly above: grow that box down instead of adding.
                    int last = runs.Count - 1;
                    if (last >= 0 && runs[last].X == run.X && runs[last].Width == run.Width && runs[last].Bottom == run.Y)
                        runs[last] = new Rectangle(runs[last].X, runs[last].Y, runs[last].Width, runs[last].Height + 4);
                    else
                        runs.Add(run);
                }
            }
            return runs;
        }

        /// <summary>The bounding box of the pane pixels in one tile's 16x16 label, and the class
        /// ladder: a mirror is a mirror, glass a third of one, a house window a fifth.</summary>
        private static bool TryPaneBox(byte[] classes, out Rectangle paneBox, out float strength)
        {
            int minX = 16, minY = 16, maxX = -1, maxY = -1;
            int windowCount = 0, glassCount = 0, mirrorCount = 0;
            for (int p = 0; p < 256; p++)
            {
                byte c = classes[p];
                if (c != LabelClassWindow && c != LabelClassGlass && c != LabelClassMirror)
                    continue;
                int x = p & 15, y = p >> 4;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (c == LabelClassWindow) windowCount++;
                else if (c == LabelClassGlass) glassCount++;
                else mirrorCount++;
            }
            paneBox = default;
            strength = 0f;
            int total = windowCount + glassCount + mirrorCount;
            if (total < 8)
                return false;
            paneBox = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            strength = mirrorCount >= glassCount && mirrorCount >= windowCount ? 1.0f
                     : glassCount >= windowCount ? 0.35f
                     : 0.20f;
            return true;
        }

        /// <summary>
        /// Draw the reflections into the game's own SORTED world batch, at the sill's depth, so a
        /// person standing in front of the glass covers their own reflection the way they cover
        /// the wall, and the picture is lit, bloomed and graded with everything else.
        /// </summary>
        /// <remarks>Drawn after the world, over the top of it, the reflection sat ON the player
        /// standing under the window. The sorted batch is the only place the game's own
        /// front-to-back order can put it behind them.</remarks>
        internal void DrawWindowReflections(SpriteBatch spriteBatch, ModConfig config)
        {
            var location = Game1.currentLocation;
            float target = config.WindowReflectionEnabled && location != null && location.IsOutdoors ? 1f : 0f;
            Approach(ref _windowReflectEase, target, 0.06f);
            // The overlay is a diagnostic and answers for the feature when it is off, so it is
            // allowed past the ease that everything else waits behind.
            bool paintPanes = DebugChannel == DebugOverlayChannel.Window;
            WindowPanesInLocation = 0;
            WindowPanesOnScreen = 0;
            WindowReflectUploaded = 0f;
            WindowSheenUploaded = 0f;
            WindowLampGlowUploaded = 0f;
            WindowLampsDrawn = 0;
            WindowLampsConsidered = 0;
            if (location == null || (_windowReflectEase < 0.01f && !paintPanes))
                return;
            EnsureWindowPaneCache(location);
            WindowPanesInLocation = _windowPanes.Count;
            if (_windowPanes.Count == 0)
                return;
            // Day and night are two different pictures and get two dials; the ramp between them is
            // the one the window glow already rides, so the image thins as the pane lights up.
            float dayStrength = MathHelper.Clamp(config.WindowReflectionStrength, 0f, 2f);
            float nightStrength = MathHelper.Clamp(config.WindowReflectionNightStrength, 0f, 2f);
            float reflect = MathHelper.Lerp(dayStrength, nightStrength, NightFactorNow()) * _windowReflectEase;
            Vector3 glassColour = GlassReflectionTint();
            // The glass itself, which is there whether or not anyone is walking past it.
            float sheen = MathHelper.Clamp(config.WindowSheenStrength, 0f, 2f)
                * MathHelper.Lerp(1f, GlassSheenNightShare, NightFactorNow()) * _windowReflectEase;
            // The street in the glass rides the daylight ramp too: after dusk the pane is lit from
            // inside and what it returns of the road goes with the rest of the daytime picture.
            float streetInGlass = MathHelper.Clamp(config.WindowSceneReflectionStrength, 0f, 2f)
                * MathHelper.Lerp(1f, GlassSheenNightShare, NightFactorNow()) * _windowReflectEase;
            // The glare rides the same ramp as the wash: both are the sky on the pane.
            float glare = MathHelper.Clamp(config.WindowGlareStrength, 0f, 2f)
                * MathHelper.Lerp(1f, GlassSheenNightShare, NightFactorNow()) * _windowReflectEase;
            // The glass returns the lamps as brightly as the ground shows them, on the very ramp the
            // lighting stage dims outdoor pools with: full while a lamp is worth something, a third
            // of that at midday. It used to be the night ramp, which is zero until seven in the
            // evening, so the dial did nothing at all for most of a day and was reported as broken
            // twice before this. A lantern carried at noon now shows in the glass it passes.
            float lampGlow = MathHelper.Clamp(config.WindowLightGlowStrength, 0f, 2f)
                * OutdoorLampDaylightDamping() * _windowReflectEase;
            // One round texture serves both: the lamps are it at a lamp's size, the glare is it
            // stretched past the pane's own edges.
            if ((lampGlow > 0.004f || glare > 0.004f) && _glassGlowTexture == null && !_glassGlowTextureMissing)
            {
                _glassGlowTexture = LoadTexture("glass-glow.png");
                _glassGlowTextureMissing = _glassGlowTexture == null;
            }
            if (lampGlow > 0.004f)
                GatherGameLights(location);
            WindowReflectUploaded = reflect;
            WindowSheenUploaded = sheen;
            WindowGlareUploaded = glare;
            WindowSceneUploaded = streetInGlass;
            WindowLampGlowUploaded = lampGlow;
            if (reflect < 0.01f && sheen < 0.004f && glare < 0.004f && streetInGlass < 0.004f
                && lampGlow < 0.004f && !paintPanes)
                return;

            var viewport = Game1.viewport;
            var screen = new Rectangle(viewport.X - 64, viewport.Y - 64, viewport.Width + 128, viewport.Height + 128);
            {
                foreach (var pane in _windowPanes)
                {
                    if (!screen.Intersects(pane.WorldRect))
                        continue;
                    WindowPanesOnScreen++;
                    if (paintPanes)
                    {
                        float paintDepth = Math.Min(1f, PaneDepth(pane) + GlassGlareDepthNudge);
                        foreach (Rectangle glass in pane.GlassRects)
                            FillWorldRect(spriteBatch, glass, Color.Red * 0.65f, paintDepth);
                        // The lamps on top of it, at full strength and in green: where a blot lands
                        // and how big it is cannot be judged from a number, and both were wrong.
                        DrawGlassLightGlows(spriteBatch, pane, 1f, paintGreen: true);
                        continue;
                    }
                    DrawGlassSkyWash(spriteBatch, pane, glassColour, sheen);
                    DrawGlassSceneReflection(spriteBatch, pane, glassColour, streetInGlass);
                    DrawGlassLightGlows(spriteBatch, pane, lampGlow);
                    // The player, from the colour bake the shadow pass keeps: feet sit eight rows
                    // above the bake's bottom edge.
                    // The player, from this frame's own bake.
                    var who = Game1.player;
                    var playerBake = _windowPlayerBakeFresh ? _windowPlayerBake : null;
                    if (reflect < 0.01f)
                        continue;   // the glass still holds sky and lamps; it just returns nobody
                    if (who != null && playerBake != null && !who.swimming.Value)
                    {
                        Rectangle box = who.GetBoundingBox();
                        DrawBodyInPane(spriteBatch, pane, reflect, glassColour, playerBake,
                            new Rectangle(0, 0, ShadowRenderer.PlayerRtW, ShadowRenderer.PlayerRtH - 8),
                            box.Center.X, box.Bottom + who.yOffset, 1f);
                        if (_windowToolBakeFresh && _toolMirrorRenderTarget != null)
                            DrawBodyInPane(spriteBatch, pane, reflect, glassColour, _toolMirrorRenderTarget,
                                new Rectangle(0, 0, ToolRtSize, (int)_toolFeetInRenderTarget.Y),
                                box.Center.X, box.Bottom - 10f + who.yOffset, 1f);
                    }
                    foreach (var other in ShadowRenderer.OtherFarmerImages)
                    {
                        if (other.Colour == null || other.Who == null)
                            continue;
                        Rectangle box = other.Who.GetBoundingBox();
                        DrawBodyInPane(spriteBatch, pane, reflect, glassColour, other.Colour,
                            new Rectangle(0, 0, ShadowRenderer.PlayerRtW, ShadowRenderer.PlayerRtH - 8),
                            box.Center.X, box.Bottom + other.Who.yOffset, 1f);
                    }
                    foreach (NPC character in ShadowRenderer.CharactersIn(location))
                    {
                        if (character?.Sprite?.Texture == null || character.IsInvisible || character.swimming.Value)
                            continue;
                        Rectangle box = character.GetBoundingBox();
                        // Same feet rule the water mirror uses: the boots are the bottom of the
                        // standard 32-row body block at the top of the frame.
                        float drawnTop = character.Position.Y + box.Height / 2f + character.drawOffset.Y
                            + character.yJumpOffset - 3f * character.Sprite.SpriteHeight;
                        float feetY = drawnTop + 4f * Math.Min(character.Sprite.SpriteHeight, 32);
                        var source = TurnedCharacterFrame(character);
                        source.Height = Math.Min(source.Height, 32);
                        DrawBodyInPane(spriteBatch, pane, reflect, glassColour, character.Sprite.Texture,
                            source, box.Center.X + character.drawOffset.X, feetY, 4f);
                    }
                    DrawGlassGlare(spriteBatch, pane, glassColour, glare);
                }
            }
        }

        /// <summary>The character's current frame turned to face the glass's way: a standard
        /// sheet keeps down in frames 0-3, right in 4-7, up in 8-11 and left in 12-15, so facing
        /// up and facing down trade rows and the other two stay. Frames past the standard sixteen
        /// are special animations and are left alone.</summary>
        private static Rectangle TurnedCharacterFrame(NPC character)
        {
            var sprite = character.Sprite;
            Rectangle source = sprite.SourceRect;
            int frame = sprite.CurrentFrame;
            int turnedFrame = frame;
            if (frame >= 0 && frame < 4) turnedFrame = frame + 8;
            else if (frame >= 8 && frame < 12) turnedFrame = frame - 8;
            if (turnedFrame == frame || sprite.Texture == null || sprite.SpriteWidth <= 0 || sprite.SpriteHeight <= 0)
                return source;
            int framesPerRow = Math.Max(1, sprite.Texture.Width / sprite.SpriteWidth);
            return new Rectangle(turnedFrame % framesPerRow * sprite.SpriteWidth,
                turnedFrame / framesPerRow * sprite.SpriteHeight, sprite.SpriteWidth, sprite.SpriteHeight);
        }

        /// <summary>Where a pane's glass sits in the game's own front-to-back order: its sill's
        /// row. The wall behind it is map art and already down, and anyone standing below the sill
        /// sorts in front.</summary>
        private static float PaneDepth(WindowPane pane)
            => MathHelper.Clamp(pane.WorldRect.Bottom / 10000f, 0f, 1f);

        /// <summary>
        /// A colour that ADDS to what is already on the screen rather than mixing with it.
        /// </summary>
        /// <remarks>
        /// The world batch blends premultiplied alpha, which computes <c>dst*(1-srcA) + src</c>.
        /// An alpha of zero therefore leaves <c>dst + src</c>: additive light, inside a batch whose
        /// blend state is not ours to change. Everything the glass itself does is light being added
        /// - sky caught on the pane, a streak of sun, a lamp across the street - and mixing toward
        /// a mid grey instead did nothing at all on the pale glass of a shop front, which is where
        /// it was first looked for and not found.
        /// </remarks>
        private static Color AddedLight(Vector3 colour, float amount)
            => new(colour.X * amount, colour.Y * amount, colour.Z * amount, 0f);

        /// <summary>One rectangle of world, filled flat.</summary>
        private static void FillWorldRect(SpriteBatch spriteBatch, Rectangle worldRect, Color tint, float depth)
        {
            Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(worldRect.X, worldRect.Y));
            var destination = new Rectangle((int)topLeft.X, (int)topLeft.Y, worldRect.Width, worldRect.Height);
            spriteBatch.Draw(Game1.staminaRect, destination, null, tint, 0f, Vector2.Zero, SpriteEffects.None, depth);
        }

        /// <summary>
        /// The sky a pane is holding, under everything else in it: a faint wash of the sky's own
        /// colour over the whole pane, and a little more of it over the top third.
        /// </summary>
        /// <remarks>This is what makes a window read as glass while nobody is walking past it. A
        /// pane of map art with nothing in it is a hole in a wall; a pane that is a shade lighter
        /// than the wall around it, and lighter still at the top, is a window.</remarks>
        private static void DrawGlassSkyWash(SpriteBatch spriteBatch, WindowPane pane, Vector3 glassColour, float amount)
        {
            float share = amount * (GlassSheenLadderFloor + (1f - GlassSheenLadderFloor) * pane.Strength);
            if (share < 0.004f)
                return;
            Color wash = AddedLight(glassColour, GlassSkyWashAlpha * share);
            Color nearerTheSky = AddedLight(glassColour, GlassSkyWashTopAlpha * share);
            float depth = PaneDepth(pane);
            var topThird = new Rectangle(pane.WorldRect.X, pane.WorldRect.Y,
                pane.WorldRect.Width, Math.Max(4, pane.WorldRect.Height / 3));
            foreach (Rectangle glass in pane.GlassRects)
            {
                FillWorldRect(spriteBatch, glass, wash, depth);
                Rectangle upper = Rectangle.Intersect(glass, topThird);
                if (upper.Width > 0 && upper.Height > 0)
                    FillWorldRect(spriteBatch, upper, nearerTheSky, depth);
            }
        }

        /// <summary>
        /// The lamps standing in front of a pane, returned by its glass after dark.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The list is the lighting stage's own gathered one, so a saloon's sixty four map lamps
        /// arrive as the two dozen pools they actually read as rather than as sixty four blots,
        /// and each carries the colour the game gave it. It is gathered again here rather than
        /// borrowed as it stands, because the world draw runs before the lighting stage does and
        /// whichever lighting mode is on decides whether that stage rebuilt the list at all.
        /// </para>
        /// <para>
        /// A window's own glow is skipped: a pane returning the light it is itself emitting is a
        /// pane lit twice.
        /// </para>
        /// </remarks>
        private void DrawGlassLightGlows(SpriteBatch spriteBatch, WindowPane pane, float amount,
            bool paintGreen = false)
        {
            var texture = _glassGlowTexture;
            if (texture == null || amount < 0.004f)
                return;
            float depth = Math.Min(1f,
                PaneDepth(pane) + (paintGreen ? GlassDebugGlowDepthNudge : GlassGlowDepthNudge));
            var source = new Rectangle(0, 0, texture.Width, texture.Height);
            WindowLampsConsidered = _gatheredLights.Count;
            foreach (var light in _gatheredLights)
            {
                if (light.IsWindow)
                    continue;
                // How far the lamp lies outside the pane's own box, in any direction at all.
                float sideways = Math.Max(0f, Math.Max(pane.WorldRect.X - light.Position.X,
                    light.Position.X - pane.WorldRect.Right));
                float updown = Math.Max(0f, Math.Max(pane.WorldRect.Y - light.Position.Y,
                    light.Position.Y - pane.WorldRect.Bottom));
                float gap = (float)Math.Sqrt(sideways * sideways + updown * updown);
                if (gap > GlassGlowReachPx)
                    continue;
                float distance = MathHelper.Clamp(gap / GlassGlowReachPx, 0f, 1f);
                // Vanilla stores a light's colour as the INVERSE (black is a full white light),
                // the same way the lighting stage reads it.
                Vector3 glow = new(1f - light.Colour.R / 255f, 1f - light.Colour.G / 255f,
                    1f - light.Colour.B / 255f);
                if (glow.LengthSquared() < 0.01f)
                    glow = Vector3.One;
                // The flattened ladder, as with the sky and the street: a cottage window returns a
                // street lamp about as well as a shop front does. The ladder used raw left a house
                // window at a fifth of an already faint amount, and the dial read as dead.
                float alpha = GlassGlowAlpha * amount * (1f - distance) * GroundShareFor(pane)
                    * (GlassSheenLadderFloor + (1f - GlassSheenLadderFloor) * pane.Strength);
                if (alpha < 0.004f)
                    continue;
                float size = Math.Max(16f, light.Radius * LampPoolReachPx * GlassGlowSizeShare
                    * MathHelper.Lerp(1f, GlassGlowFarScale, distance));
                // Where the lamp is, PULLED ONTO THE GLASS. A lamp standing beside a window is
                // still returned by that window - its image lies inside the pane, near the edge it
                // stands past - and left at the lamp's own coordinates the blot sat outside the
                // glass entirely, with the pane catching the faintest rim of it. That is drawn, it
                // is counted, and it cannot be seen: exactly what was reported.
                float centreX = KeptOnTheGlass(light.Position.X, pane.WorldRect.X, pane.WorldRect.Right, size);
                float centreY = KeptOnTheGlass(light.Position.Y, pane.WorldRect.Y, pane.WorldRect.Bottom, size);
                var image = new Rectangle((int)Math.Round(centreX - size * 0.5f),
                    (int)Math.Round(centreY - size * 0.5f), (int)size, (int)size);
                Color tint = paintGreen ? new Color(0f, 1f, 0f, 0f) : AddedLight(glow, alpha);
                if (DrawClippedToGlass(spriteBatch, pane, texture, source, image, size / source.Width, tint, depth) > 0)
                    WindowLampsDrawn++;
            }
        }

        /// <summary>
        /// The street in front of a pane, standing in its glass.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The source is the sprite-free map render the water mirror reads, which is the screen
        /// plus a band around it with every map layer drawn and no sprites in it, cached until the
        /// camera leaves its guard band. The strip taken is the world directly in front of the
        /// pane - six tiles of it, below the sill on screen - squashed into the pane and turned
        /// upside down, because a mirror standing upright shows what is near it along its bottom
        /// edge and what is far from it higher up.
        /// </para>
        /// <para>
        /// Blended rather than added, unlike everything else the glass itself does. The others are
        /// flat colours, where adding is the only thing that shows on pale art; this is a PICTURE,
        /// and what makes a picture read as a reflection is its light against its dark. Added, a
        /// road that is bright and nearly even everywhere contributed its average and nothing else:
        /// turning it up only turned the pane whiter, which is what it was reported as. Blended, the
        /// glass takes the road's shape, and darkens where the road is dark - which is what glass
        /// reflecting a street actually looks like.
        /// </para>
        /// <para>
        /// It fills the lower half of the pane and fades out before the head of the frame. Carried
        /// to the top the pane stopped being glass with something in it and became a picture of a
        /// road in a window-shaped hole.
        /// </para>
        /// </remarks>
        private void DrawGlassSceneReflection(SpriteBatch spriteBatch, WindowPane pane, Vector3 glassColour, float amount)
        {
            var scene = _mirrorSourceRenderTarget;
            // The flattened ladder, like the sky and the glare: a house window returns the road it
            // faces about as well as a shop front does. Used raw, its fifth left the street at six
            // percent of itself, which is a number that exists and a picture that does not. Times
            // how high the pane stands: what a dormer returns of a street is nearly nothing.
            float share = amount * GroundShareFor(pane)
                * (GlassSheenLadderFloor + (1f - GlassSheenLadderFloor) * pane.Strength);
            if (scene == null || !SceneRTReady || SceneSourceOff || share < 0.004f)
                return;
            int stripBottom = pane.WorldRect.Bottom + GlassSceneReachPx;
            int sourceOriginX = Game1.viewport.X - MirrorSideReachPx;
            int sourceOriginY = Game1.viewport.Y - MirrorTopReachPx;
            // Premultiplied, with an alpha this time: colour times amount in the channels so the
            // road arrives in the glass's own colour, and the amount in alpha so it replaces that
            // much of the pane rather than piling light on top of it.
            float strength = Math.Min(GlassSceneAlpha * share, 0.95f);
            float depth = Math.Min(1f, PaneDepth(pane) + GlassSceneDepthNudge);
            foreach (Rectangle glass in pane.GlassRects)
            {
                int bandHeight = Math.Max(4, glass.Height / GlassSceneFadeBands);
                for (int bandTop = glass.Y; bandTop < glass.Bottom; bandTop += bandHeight)
                {
                    int bandBottom = Math.Min(bandTop + bandHeight, glass.Bottom);
                    // How far down the pane this band's middle sits, and how much of the street
                    // belongs at that height.
                    float downThePane = ((bandTop + bandBottom) * 0.5f - pane.WorldRect.Y)
                        / pane.WorldRect.Height;
                    float ramp = MathHelper.Clamp(
                        (downThePane - GlassSceneFadeStart) / (GlassSceneFadeFull - GlassSceneFadeStart), 0f, 1f);
                    float fade = ramp * ramp * (3f - 2f * ramp);
                    float bandStrength = strength * fade;
                    if (bandStrength < 0.004f)
                        continue;
                    Color tint = new(glassColour.X * bandStrength, glassColour.Y * bandStrength,
                        glassColour.Z * bandStrength, bandStrength);
                    float nearEdge = (bandTop - pane.WorldRect.Y) / (float)pane.WorldRect.Height;
                    float farEdge = (bandBottom - pane.WorldRect.Y) / (float)pane.WorldRect.Height;
                    int worldTop = (int)Math.Round(stripBottom - farEdge * GlassSceneReachPx);
                    int worldBottom = (int)Math.Round(stripBottom - nearEdge * GlassSceneReachPx);
                    var source = new Rectangle(glass.X - sourceOriginX, worldTop - sourceOriginY,
                        glass.Width, Math.Max(1, worldBottom - worldTop));
                    // The strip can run off the bottom of the source for a pane low on the screen.
                    // Skipping is honest; clamping would smear the source's last row up the glass.
                    if (source.X < 0 || source.Y < 0 || source.Right > scene.Width || source.Bottom > scene.Height)
                        continue;
                    Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(glass.X, bandTop));
                    var destination = new Rectangle((int)topLeft.X, (int)topLeft.Y, glass.Width, bandBottom - bandTop);
                    spriteBatch.Draw(scene, destination, source, tint, 0f, Vector2.Zero,
                        SpriteEffects.FlipVertically, depth);
                }
            }
        }

        /// <summary>
        /// The glare, over everything else in the pane: one soft blot of sky-white, wider than the
        /// pane so only its middle lands on the glass, travelling across it as the pane crosses the
        /// screen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Drawn last and on top because a highlight is on the near face of the glass: under the
        /// reflection it reads as a stripe painted on the wall behind it.
        /// </para>
        /// <para>
        /// This started as two diagonal streaks from a shipped texture, laid along the diagonal the
        /// game's own window art was measured to use. Even lying the same way as the painted ones
        /// they read as a second set of lines on art that already had lines, so the lines are gone
        /// and what is left is the thing lines were standing in for: light on the pane, moving.
        /// A blot has no direction to disagree with any art pack.
        /// </para>
        /// </remarks>
        private void DrawGlassGlare(SpriteBatch spriteBatch, WindowPane pane, Vector3 glassColour, float amount)
        {
            var texture = _glassGlowTexture;
            float share = amount * (GlassSheenLadderFloor + (1f - GlassSheenLadderFloor) * pane.Strength);
            if (texture == null || share < 0.004f)
                return;
            float acrossScreen = MathHelper.Clamp(
                (pane.WorldRect.Center.X - Game1.viewport.X) / Math.Max(1f, Game1.viewport.Width), 0f, 1f);
            float width = pane.WorldRect.Width * GlassGlareSpread;
            float height = pane.WorldRect.Height * GlassGlareSpread;
            float centreX = pane.WorldRect.Center.X + (acrossScreen - 0.5f) * pane.WorldRect.Width * GlassGlareTravel;
            // A little above the middle: light comes from above, and a blot centred in the pane
            // reads as a smudge on it rather than as sky on it.
            float centreY = pane.WorldRect.Y + pane.WorldRect.Height * 0.35f;
            var image = new Rectangle((int)Math.Round(centreX - width * 0.5f), (int)Math.Round(centreY - height * 0.5f),
                (int)width, (int)height);
            // Whiter than the wash: a glancing highlight is the sun's colour more than the sky's.
            Vector3 highlight = Vector3.Lerp(glassColour, Vector3.One, 0.5f);
            Color tint = AddedLight(highlight, GlassGlareAlpha * share);
            float depth = Math.Min(1f, PaneDepth(pane) + GlassGlareDepthNudge);
            var source = new Rectangle(0, 0, texture.Width, texture.Height);
            DrawClippedToGlass(spriteBatch, pane, texture, source, image, width / texture.Width, tint, depth);
        }

        /// <summary>
        /// One body in one pane: the body's frame drawn upright as if standing at the glass, clipped
        /// to the pane's box, faded by how far below the sill the body stands.
        /// </summary>
        private void DrawBodyInPane(SpriteBatch spriteBatch, WindowPane pane, float reflect,
            Vector3 glassColour, Texture2D texture, Rectangle source, float bodyCenterX, float bodyFeetY,
            float scale)
        {
            float below = bodyFeetY - pane.WorldRect.Bottom;
            if (below < -16f || below > WindowReflectReachPx)
                return;
            float distance = MathHelper.Clamp(below / WindowReflectReachPx, 0f, 1f);
            float distanceFade = 1f - distance;
            float alpha = reflect * pane.Strength * distanceFade * GroundShareFor(pane);
            if (alpha < 0.01f)
                return;
            // Smaller as they walk away, feet still on the sill, so the image recedes into the
            // glass instead of sliding over it.
            scale *= MathHelper.Lerp(1f, WindowReflectFarScale, distance);

            // The image in world pixels: feet on the sill, centred on the body.
            float drawnWidth = source.Width * scale, drawnHeight = source.Height * scale;
            float imageFeetY = pane.WorldRect.Bottom + WindowStandingOffsetPx;
            var image = new Rectangle((int)Math.Round(bodyCenterX - drawnWidth / 2f),
                (int)Math.Round(imageFeetY - drawnHeight), (int)drawnWidth, (int)drawnHeight);
            // Dim, and the colour of the sky the window is under, the way glass returns a picture:
            // it never rivals the person in front. Depth is the sill's, a hair in front of the sky
            // wash and a hair behind the streaks.
            var glassTint = new Color(glassColour.X, glassColour.Y, glassColour.Z) * alpha;
            float depth = Math.Min(1f, PaneDepth(pane) + GlassBodyDepthNudge);
            DrawClippedToGlass(spriteBatch, pane, texture, source, image, scale, glassTint, depth);
        }

        /// <summary>
        /// One image standing in a pane, clipped to each run of glass it crosses; the wood between
        /// the pieces and the bodywork beside a slant show nothing.
        /// </summary>
        /// <returns>How many pieces of glass the image actually landed on. Zero means it was
        /// clipped away entirely, which looks exactly like "not drawn" and is worth telling
        /// apart: a counter that counted calls rather than pieces reported fifteen lamps in the
        /// glass while there were none.</returns>
        private static int DrawClippedToGlass(SpriteBatch spriteBatch, WindowPane pane, Texture2D texture,
            Rectangle source, Rectangle image, float scale, Color tint, float depth)
        {
            int drawn = 0;
            foreach (Rectangle glass in pane.GlassRects)
            {
                Rectangle visible = Rectangle.Intersect(image, glass);
                if (visible.Width <= 0 || visible.Height <= 0)
                    continue;
                // Crop the source to the visible part of the image, in source pixels.
                var croppedSource = new Rectangle(
                    source.X + (int)((visible.X - image.X) / scale),
                    source.Y + (int)((visible.Y - image.Y) / scale),
                    Math.Max(1, (int)Math.Ceiling(visible.Width / scale)),
                    Math.Max(1, (int)Math.Ceiling(visible.Height / scale)));
                Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(visible.X, visible.Y));
                var destination = new Rectangle((int)topLeft.X, (int)topLeft.Y, visible.Width, visible.Height);
                spriteBatch.Draw(texture, destination, croppedSource, tint, 0f, Vector2.Zero, SpriteEffects.None, depth);
                drawn++;
            }
            return drawn;
        }
    }
}
