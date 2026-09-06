using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>Which internal buffer the on-screen debug overlay shows (radiance_debug).</summary>
    internal enum DebugOverlayChannel
    {
        Off,
        Water,      // composed water mask recolor (the old radiance_maskview)
        LabelDiff,  // label vs mask verdict: red = water missing, yellow = false water
        Sdf,        // edge-distance field (B channel view)
        Subtype,    // liquid subtype from mask alpha: water/ice/flow/lava
        Sprite,     // sprite exclusion mask RT
        Reflect,    // flipped-entity reflection RT
        Mirror,     // sprite-free scenery mirror source RT
        Flood,      // the flood GI lightmap texture itself (1 cell = 1 tile) - flick the room-light
                    // toggle and watch the window's cells brighten/darken without guessing from the scene
        Normals,    // the sprite normal buffer the relief reads (RG = xy, B = z, A = coverage)
        // Not a water channel, and painted by floodlight.fx rather than by an overlay texture:
        // which pixels the lighting pass believes ARE a light source rather than lit by one.
        // The question "is the flame being caught at all" has been answered by argument twice and
        // both answers were wrong, so it gets a picture instead.
        Emitter,
        // Also painted by floodlight.fx: the per-light shadow terms themselves, before they touch
        // the picture. R = the deepest occlusion any shadowed light found on its ray to this pixel,
        // G = the carve taken out of the game's own glow, B = the occluder mask under the pixel.
        // A saw-toothed shadow edge, or a gap between a thing and its shadow, is either in these
        // terms or in what they multiply, and a screenshot of the lit scene cannot say which.
        LampShadow,
        // Painted by water.fx: where the caustic term lands and how hard, as pure red on the bed.
        // "I toggled it and saw nothing" cannot be argued with a number alone when the number
        // says 0.41; this shows the shape or shows nothing, and either answer settles it.
        Caustic,
        // Painted by the window pass in world space: every pixel the labels call glass, in red,
        // at the depth the reflection itself is drawn at. It answers the two questions that look
        // identical from the street - "does the mod see a window here at all", which the pane
        // count in radiance_report answers, and "is something drawn over it", which only a
        // picture at the real depth can.
        Window,
        // Painted by water.fx over the water itself: what the MIRROR is reading. R = the source is
        // flat ground, G = the source is water, B = how much of the mirror has already given way
        // to sky there. A reflection that looks like the wrong thing is a question about its
        // SOURCE, and a picture of the scene cannot answer it: the pale sheet over a forest stream
        // was read as a reflected sand path for an hour, and this said water in one frame.
        MirrorSource,
        // Painted by water.fx over the whole frame, before any water gating: the sky glow's own
        // inputs. R = the aurora amount the shader actually received, G = the curtain field's
        // height here, B = the shooting star's envelope. Whether the aurora exists at all has now
        // been answered by argument three times and every answer was wrong, including one built
        // on a report line that proved only what the CPU computed, not what the shader got.
        Sky,
    }

    /// <summary>
    /// RenderPipeline — LABEL VERIFICATION: the acceptance test for the whole label pipeline.
    /// The rule is "the game must match the labeler pixel for pixel"; this scans every labeled
    /// tile in the mask window, resolves what the labels SAY each pixel is (topmost layer with
    /// an opinion wins — same rule the compositor is specified to follow), compares that with
    /// what the composed mask actually holds, and reports every disagreement with exact tiles.
    /// `radiance_debug labeldiff` paints the same verdict over the world.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>The live pipeline, for author tools that only have static access (VerifyTour).</summary>
        internal static RenderPipeline? Current;

        internal static DebugOverlayChannel DebugChannel;

        private Texture2D? _labelDiffTexture;
        private Color[]? _labelDiffPixels;


        private static bool IsLiquidClass(byte c) => c is 1 or 9 or 10 or 11 or 14;

        /// <summary>16x16 opacity bits of the art a layer draws at this tile (first frame for
        /// animated tiles), from the same cached OpaqueBits the compose carve uses — so the
        /// verifier and the mask judge "visible" identically. Null = no art / load failed;
        /// callers treat null as fully opaque (opinion counts).</summary>
        private bool[]? OpaqueBitsForLayerTile(xTile.Layers.Layer? layer, int tx, int ty)
        {
            var tile = layer?.Tiles[tx, ty];
            if (tile == null)
                return null;
            // The map may place this tile mirrored or turned (@Flip/@Rotation). The LABEL buffer
            // this is compared against comes back oriented from LabelStore, so the opacity has to
            // turn the same way or the two disagree by exactly that reflection: measured as FALSE
            // water jumping 18 -> 535 px on Gem Sea Shores, whose maps carry 368 turned cells.
            byte orient = MapLayers.Orientation(tile);
            if (tile is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                tile = at.TileFrames[0];
            if (tile?.TileSheet == null)
                return null;
            try
            {
                var texture = Game1.content.Load<Texture2D>(tile.TileSheet.ImageSource);
                var ib = tile.TileSheet.GetTileImageBounds(tile.TileIndex);
                bool[] bits = OpaqueBits(texture, new Rectangle(ib.X, ib.Y, ib.Width, ib.Height)).bits;
                return MapLayers.Orient(bits, orient);
            }
            catch { return null; }
        }

        private Color[]? _inspectionMaskPixels;

        /// <summary>
        /// CPU copy of the composed mask for inspection tools. The compose path's own copy
        /// (<c>_waterMaskPixels</c>) is a grow-only scratch buffer that the full-map waterline
        /// job deliberately releases when it finishes (see FreeOversizedScratch) — so on any
        /// map where that job ran, the copy is gone and only the GPU texture remains. Author
        /// tools read it back on demand: one GetData per command/tour capture, never per frame.
        /// </summary>
        private Color[]? MaskPixelsForInspection()
        {
            if (_waterMaskPixels != null)
                return _waterMaskPixels;
            if (_waterMask == null)
                return null;
            int count = _waterMask.Width * _waterMask.Height;
            if (_inspectionMaskPixels == null || _inspectionMaskPixels.Length < count)
                _inspectionMaskPixels = new Color[count];
            try { _waterMask.GetData(_inspectionMaskPixels, 0, count); }
            catch { return null; }
            return _inspectionMaskPixels;
        }

        /// <summary>Expected mask evidence per liquid class: water/hot/flow/lava ripple (R),
        /// ice mirrors without rippling (G). Glass (13) is deliberately no-verdict in v1.</summary>
        private static bool MaskAgreesWithLiquid(byte cls, Color m) => cls switch
        {
            9 => m.G > 0 || m.R > 0,   // ice: mirror; some compositions keep a faint R too
            _ => m.R > 0,              // water(1)/flow(10)/lava(11)/hot(14): the effect channel
        };

        /// <summary>
        /// Compare labels against the composed mask over the whole mask window.
        /// Returns a console-ready report and (re)builds the labeldiff overlay texture.
        /// </summary>
        internal string VerifyLabels(GameLocation? location, int worstToList = 12)
        {
            var labels = LabelStore.Instance;
            if (labels == null || !labels.Any)
                return "[verify] no label set loaded";
            Color[]? maskPixels = MaskPixelsForInspection();
            if (location == null || _waterMask == null || maskPixels == null)
                return "[verify] no composed mask yet (stand where water is possible, or wait for the rebuild)";

            const int Texels = 16;
            int tilesW = _waterMask.Width / Texels, tilesH = _waterMask.Height / Texels;
            int pw = _waterMask.Width;
            int pcount = pw * _waterMask.Height;

            // Every layer the game draws, BOTTOM to TOP from the one shared sort key, so this
            // verifier reads the same layer order the compose and the map dump publish. It used to
            // be a fixed eleven-name list, which was blind to Back3 / Buildings3 / AlwaysFront2 and
            // to any negative-suffix layer — the mask saw them, this tool did not, and the verdict
            // disagreed with the effect for a whole map numbering its layers differently.
            var layers = MapLayers.RenderedLayers(location.map, topToBottom: false).ToArray();
            if (layers.Length == 0)
                return "[verify] no rendered layers on this map";

            LabelScanTally tally = ScanLabelWindow(location, labels, maskPixels, layers, tilesW, tilesH, pw, pcount);

            UploadLabelDiffTexture(pcount);
            return BuildVerifyReport(location, tally, tilesW, tilesH, worstToList);
        }

        /// <summary>What one pass of the label scan counted. The fields carry the scan's own
        /// local names so the report below still reads the way it did.</summary>
        private sealed class LabelScanTally
        {
            public long labeledOpinionPixels, agreeLiquid, agreeDry, hiddenPixels;
            public long missingWater, falseWater, glassPixels, glassMirrored, deckSkipped;
            public Dictionary<byte, long> missingByClass = new();
            public List<(int tx, int ty, int miss, int falsePx)> perTile = new();
        }

        /// <summary>Walk every tile of the mask window, resolve what the labels say each pixel is,
        /// and compare that with what the composed mask holds. Paints the labeldiff overlay as it
        /// goes and returns the tally.</summary>
        private LabelScanTally ScanLabelWindow(GameLocation location, LabelStore labels, Color[] maskPixels,
                                               xTile.Layers.Layer[] layers, int tilesW, int tilesH,
                                               int pw, int pcount)
        {
            const int Texels = 16;
            if (_labelDiffPixels == null || _labelDiffPixels.Length < pcount)
                _labelDiffPixels = new Color[pcount];
            Array.Clear(_labelDiffPixels, 0, pcount);
            var tally = new LabelScanTally();
            // Walk-on DECKS are excluded from the tally, not scored. A bridge's railing slots and
            // its painted shadow are see-through, so this rule ("the topmost layer whose art is
            // opaque decides; otherwise the water below shows") calls them water and the mask
            // deliberately does not: the spec for the subsystem is that a bridge never ripples.
            // Scoring them anyway parked a permanent few-hundred-pixel MISSING count on every
            // river map, which is exactly the size of shortfall a real regression would show.
            var deckSurfaces = SurfaceMap.For(location);
            var tileLabelBuf = new byte[layers.Length][];
            var tileOpaqueBuf = new bool[]?[layers.Length];
            for (int ty = 0; ty < tilesH; ty++)
            {
                int worldTy = _lastWaterTileY + ty;
                for (int tx = 0; tx < tilesW; tx++)
                {
                    int worldTx = _lastWaterTileX + tx;
                    bool anyLabel = false;
                    for (int i = 0; i < layers.Length; i++)
                    {
                        tileLabelBuf[i] = labels.Get(layers[i], worldTx, worldTy)!;
                        anyLabel |= tileLabelBuf[i] != null;
                        // Overlay layers only have an OPINION where their art is opaque: the
                        // label paints the full sheet tile, but where the art is transparent the
                        // player sees the layer below — a mostly-transparent animated edge
                        // decoration labelled ground:256 on Buildings called 247 px of real lake
                        // "false water" per tile (the whole mountain-lake 12.7k figure). Back
                        // (i == 0) is the base art and always counts.
                        // Opacity is needed for EVERY overlay, not only the labelled ones. An
                        // unlabelled overlay that is opaque still hides the water under it, and
                        // reading it only when a label existed is what made a bridge deck score as
                        // 256/256 MISSING water: the Back label says liquid, the deck's Front art
                        // covers all 256 texels, the mask correctly ships nothing, and the verifier
                        // called the mask wrong. Measured on the Waterfall Forest crossing, tiles
                        // (33,27) / (32,28) / (33,28): keep=256/256 with carveFront=256/256.
                        tileOpaqueBuf[i] = i > 0
                            ? OpaqueBitsForLayerTile(layers[i], worldTx, worldTy)
                            : null;
                    }
                    if (!anyLabel)
                        continue;
                    if (deckSurfaces != null && deckSurfaces.GetSurface(worldTx, worldTy) == SurfaceClass.Deck)
                    {
                        CountDeckPixels(tally, layers, tileLabelBuf, tileOpaqueBuf);
                        continue;
                    }

                    int px0 = tx * Texels, py0 = ty * Texels;
                    (int tileMiss, int tileFalse) = ScoreTilePixels(tally, layers, tileLabelBuf,
                        tileOpaqueBuf, maskPixels, px0, py0, pw);
                    if (tileMiss > 0 || tileFalse > 0)
                        tally.perTile.Add((worldTx, worldTy, tileMiss, tileFalse));
                }
            }
            return tally;
        }

        /// <summary>Count the labelled pixels on a walk-on deck, which are excluded from the
        /// score rather than judged: a bridge is specified never to ripple.</summary>
        private static void CountDeckPixels(LabelScanTally tally, xTile.Layers.Layer[] layers,
                                            byte[][] tileLabelBuf, bool[]?[] tileOpaqueBuf)
        {
                        for (int p = 0; p < 256; p++)
                        {
                            byte deckCls = 255;
                            for (int i = layers.Length - 1; i >= 0; i--)
                            {
                                byte c = tileLabelBuf[i] != null ? tileLabelBuf[i][p] : (byte)255;
                                if (c == 255) continue;
                                if (tileOpaqueBuf[i] is { } opaque && !opaque[p]) continue;
                                deckCls = c; break;
                            }
                            if (deckCls != 255)
                                tally.deckSkipped++;
                        }
        }

        /// <summary>Score one tile's 256 pixels: topmost layer with an opinion against what the
        /// composed mask holds, painting the labeldiff overlay as it goes.</summary>
        private (int Miss, int False) ScoreTilePixels(LabelScanTally tally, xTile.Layers.Layer[] layers,
                                                      byte[][] tileLabelBuf, bool[]?[] tileOpaqueBuf,
                                                      Color[] maskPixels, int px0, int py0, int pw)
        {
            int tileMiss = 0, tileFalse = 0;
                    for (int p = 0; p < 256; p++)
                    {
                        // Topmost layer with an opinion (255 = unset = no opinion) wins; an
                        // overlay's opinion exists only where its art is opaque (see above).
                        byte cls = 255;
                        bool hiddenByArt = false;
                        for (int i = layers.Length - 1; i >= 0; i--)
                        {
                            // Does this layer's art physically occupy the pixel? Back (i == 0) is
                            // the base and always does.
                            if (i > 0 && !(tileOpaqueBuf[i] is { } opaque && opaque[p]))
                                continue;
                            byte c = tileLabelBuf[i] != null ? tileLabelBuf[i][p] : (byte)255;
                            if (c == 255)
                            {
                                // Opaque art that nobody labelled. Whatever the layers below say,
                                // the player cannot see it, so neither the mask nor the label can
                                // be judged wrong here. Scored separately rather than as MISSING.
                                hiddenByArt = i > 0;
                                break;
                            }
                            cls = c; break;
                        }
                        if (hiddenByArt)
                        {
                            tally.hiddenPixels++;
                            continue;
                        }
                        if (cls == 255)
                            continue;

                        int pi = (py0 + p / 16) * pw + px0 + p % 16;
                        Color m = maskPixels[pi];

                        if (cls == 13) // glass: reflects but is not liquid — informational only in v1
                        {
                            tally.glassPixels++;
                            if (m.G > 0) tally.glassMirrored++;
                            continue;
                        }

                        tally.labeledOpinionPixels++;
                        if (IsLiquidClass(cls))
                        {
                            if (MaskAgreesWithLiquid(cls, m))
                            {
                                tally.agreeLiquid++;
                                _labelDiffPixels![pi] = new Color(0, 160, 0, 140);          // green: liquid ok
                            }
                            else
                            {
                                tally.missingWater++; tileMiss++;
                                tally.missingByClass[cls] = tally.missingByClass.TryGetValue(cls, out long n) ? n + 1 : 1;
                                _labelDiffPixels![pi] = new Color(255, 0, 0, 230);          // red: label says liquid, mask has none
                            }
                        }
                        else
                        {
                            if (m.R > 0)
                            {
                                tally.falseWater++; tileFalse++;
                                _labelDiffPixels![pi] = new Color(255, 220, 0, 230);        // yellow: mask ripples where label says solid/ground
                            }
                            else
                            {
                                tally.agreeDry++;
                                _labelDiffPixels![pi] = new Color(40, 60, 200, 40);         // faint blue: labeled-dry agreement (shows coverage)
                            }
                        }
                    }
            return (tileMiss, tileFalse);
        }

        /// <summary>Turn one scan's tally into the console report.</summary>
        private static string BuildVerifyReport(GameLocation location, LabelScanTally tally,
                                                int tilesW, int tilesH, int worstToList)
        {
            var report = new System.Text.StringBuilder();
            long disagreements = tally.missingWater + tally.falseWater;
            double accuracy = tally.labeledOpinionPixels > 0 ? 100.0 * (tally.labeledOpinionPixels - disagreements) / tally.labeledOpinionPixels : 100.0;
            report.AppendLine($"[verify] {location.NameOrUniqueName}: window {tilesW}x{tilesH} tiles, "
                + $"{tally.labeledOpinionPixels:N0} labeled pixels checked"
                + " (measure STANDING STILL - a rebuild mid-walk can skew mask vs labels by one tile"
                + " and print paired missing/false ghosts)");
            report.AppendLine($"[verify] accuracy {accuracy:0.00}%  —  agree liquid {tally.agreeLiquid:N0}, agree dry {tally.agreeDry:N0}, "
                + $"MISSING water {tally.missingWater:N0}, FALSE water {tally.falseWater:N0}"
                + (tally.glassPixels > 0 ? $", glass {tally.glassPixels:N0} (mirrored {tally.glassMirrored:N0})" : "")
                + (tally.deckSkipped > 0 ? $", deck {tally.deckSkipped:N0} px not scored (bridges never ripple)" : "")
                + (tally.hiddenPixels > 0 ? $", hidden {tally.hiddenPixels:N0} px behind opaque unlabelled art (not scored)" : ""));
            foreach (var kv in tally.missingByClass.OrderByDescending(kv => kv.Value))
                report.AppendLine($"[verify]   missing by class: {ClassName(kv.Key)} = {kv.Value:N0} px");
            if (tally.perTile.Count > 0)
            {
                report.AppendLine($"[verify] {tally.perTile.Count} tiles disagree; worst {Math.Min(worstToList, tally.perTile.Count)} "
                    + "(radiance_tile on them prints the per-layer story):");
                foreach (var t in tally.perTile.OrderByDescending(t => t.miss + t.falsePx).Take(worstToList))
                    report.AppendLine($"[verify]   tile ({t.tx},{t.ty})  missing={t.miss}/256  false={t.falsePx}/256");
                report.AppendLine("[verify] overlay: radiance_debug labeldiff  (red = water missing, yellow = false water)");
            }
            else if (tally.labeledOpinionPixels > 0)
            {
                report.AppendLine("[verify] PASS — every labeled pixel in this window matches the composed mask.");
            }
            else
            {
                report.AppendLine("[verify] no labeled tiles in this window — nothing to check here.");
            }
            return report.ToString().TrimEnd();
        }

        private void UploadLabelDiffTexture(int pcount)
        {
            if (_waterMask == null || _labelDiffPixels == null)
                return;
            if (_labelDiffTexture == null || _labelDiffTexture.Width != _waterMask.Width || _labelDiffTexture.Height != _waterMask.Height)
            {
                _labelDiffTexture?.Dispose();
                _labelDiffTexture = VramTally.Track(new Texture2D(_device, _waterMask.Width, _waterMask.Height, false, SurfaceFormat.Color), "label diff (diagnostic)");
            }
            _labelDiffTexture.SetData(_labelDiffPixels, 0, pcount);
        }

        // ---- multi-channel debug overlay (radiance_debug) --------------------------------------

        private Texture2D? _channelViewTexture;
        private Color[]? _channelViewPixels;

        /// <summary>Draw the active debug channel over the frame (screen space, after the stack).</summary>
        public void DrawDebugOverlay(SpriteBatch spriteBatch)
        {
            switch (DebugChannel)
            {
                case DebugOverlayChannel.Water:
                    DrawMaskOverlay(spriteBatch);
                    break;
                case DebugOverlayChannel.LabelDiff:
                    DrawMaskWindowTexture(spriteBatch, _labelDiffTexture, 0.9f);
                    break;
                case DebugOverlayChannel.Sdf:
                case DebugOverlayChannel.Subtype:
                    BuildChannelView(DebugChannel);
                    DrawMaskWindowTexture(spriteBatch, _channelViewTexture, 0.75f);
                    break;
                case DebugOverlayChannel.Sprite:
                    DrawScreenTexture(spriteBatch, _spriteMaskRenderTarget);
                    break;
                case DebugOverlayChannel.Reflect:
                    DrawScreenTexture(spriteBatch, _reflectionRenderTarget);
                    break;
                case DebugOverlayChannel.Mirror:
                    DrawScreenTexture(spriteBatch, _mirrorSourceRenderTarget);
                    break;
                case DebugOverlayChannel.Normals:
                    DrawScreenTexture(spriteBatch, _normalRenderTarget);
                    break;
                case DebugOverlayChannel.Flood:
                    {
                        // Whichever GI model is showing: the flood grid (1 cell = 1 tile = 64 px) or
                        // the cascades' map (2 probes per tile), each anchored to its own world origin.
                        bool cascades = _cascadeBlend > 0.5f && _cascades.Texture != null;
                        Texture2D? map = cascades ? _cascades.Texture : _flood.Texture;
                        Vector2 mapOrigin = cascades ? _cascades.Origin : _flood.Origin;
                        Vector2 mapSize = cascades ? _cascades.MapSize : _flood.MapSize;
                        if (map != null)
                        {
                            float fx = mapOrigin.X * 64f - Game1.viewport.X;
                            float fy = mapOrigin.Y * 64f - Game1.viewport.Y;
                            var dest = new Rectangle((int)fx, (int)fy, (int)(mapSize.X * 64f), (int)(mapSize.Y * 64f));
                            spriteBatch.Draw(map, dest, Color.White);
                        }
                    }
                    break;
                case DebugOverlayChannel.Emitter:
                case DebugOverlayChannel.Caustic:
                    break;      // the shader already painted it; this just adds the caption
                default:
                    return;
            }
            Utility.drawTextWithShadow(spriteBatch, $"radiance_debug: {DebugChannel}", Game1.smallFont,
                new Vector2(12, 12), Color.White);
            if (DebugChannel == DebugOverlayChannel.Normals)
                Utility.drawTextWithShadow(spriteBatch,
                    $"recorded {SpriteDrawRecorder.LastCount} draws ({SpriteDrawRecorder.PatchedOverloads} overloads patched), replayed {_normalPassDrawn}, ease={_reliefEase:F2}   {_sheetNormals.Describe()}",
                    Game1.smallFont, new Vector2(12, 36), Color.Yellow);
            if (DebugChannel == DebugOverlayChannel.Normals)
                Utility.drawTextWithShadow(spriteBatch,
                    $"sway strips {FoliageSway.StripDrawsThisFrame}   upscaled draws {SheetUpscaler.RedirectedThisFrame} ({SheetUpscaler.PatchedOverloads} overloads)   {SheetUpscaler.Cache.Describe()}   {SheetUpscaler.SoftSprites.Describe()}",
                    Game1.smallFont, new Vector2(12, 60), Color.Yellow);
            if (DebugChannel == DebugOverlayChannel.Flood && Game1.currentLocation != null)
            {
                // Live read-back so toggling a light shows up as a NUMBER, not a hunch.
                string glowProbe = "none";
                foreach (Vector2 g in Game1.currentLocation.lightGlows)
                {
                    int gx = (int)(g.X / 64f), gy = (int)(g.Y / 64f);
                    glowProbe = $"({gx},{gy})={_flood.Probe(gx, gy)}";
                    break;
                }
                var pt = Game1.player.TilePoint;
                Utility.drawTextWithShadow(spriteBatch,
                    $"cell@{pt} = {_flood.Probe(pt.X, pt.Y)}   glow {glowProbe}   "
                    + $"roomLight={FloodLightmap.WindowRoomScale:F2} patch={FloodLightmap.WindowPatchScale:F2}   "
                    + FloodLightmap.LastWindowSeed
                    + $"   blend={_cascadeBlend:F2} {_cascades.LastReport}",
                    Game1.smallFont, new Vector2(12, 36), Color.Yellow);
            }
        }

        private (DebugOverlayChannel channel, long buildTick) _channelViewBuiltFor = (DebugOverlayChannel.Off, -1);

        /// <summary>Recolor of one mask channel, rebuilt only when the mask itself rebuilt
        /// (the CPU copy may need a GPU readback — never pay that per frame).</summary>
        private void BuildChannelView(DebugOverlayChannel channel)
        {
            if (_waterMask == null)
                return;
            if (_channelViewBuiltFor == (channel, _lastWaterBuildTick) && _channelViewTexture != null)
                return;
            Color[]? maskPixels = MaskPixelsForInspection();
            if (maskPixels == null)
                return;
            _channelViewBuiltFor = (channel, _lastWaterBuildTick);
            int pcount = _waterMask.Width * _waterMask.Height;
            if (_channelViewPixels == null || _channelViewPixels.Length < pcount)
                _channelViewPixels = new Color[pcount];
            for (int p = 0; p < pcount; p++)
            {
                Color m = maskPixels[p];
                _channelViewPixels[p] = channel switch
                {
                    // Edge distance: black at the waterline, brighter with distance into the body.
                    DebugOverlayChannel.Sdf => m.G > 0 ? new Color(m.B, m.B, m.B) : Color.Transparent,
                    // Liquid subtype from the alpha encoding the shader reads.
                    DebugOverlayChannel.Subtype => (m.R > 0 || m.G > 0) ? m.A switch
                    {
                        255 => new Color(40, 90, 255),    // water: blue
                        192 => new Color(0, 230, 230),    // flow: cyan
                        128 => new Color(255, 80, 0),     // lava: orange
                        0 => new Color(230, 230, 255),    // ice: near-white
                        _ => new Color(200, 0, 200),      // unexpected encoding: magenta
                    } : Color.Transparent,
                    _ => Color.Transparent,
                };
            }
            if (_channelViewTexture == null || _channelViewTexture.Width != _waterMask.Width || _channelViewTexture.Height != _waterMask.Height)
            {
                _channelViewTexture?.Dispose();
                _channelViewTexture = VramTally.Track(new Texture2D(_device, _waterMask.Width, _waterMask.Height, false, SurfaceFormat.Color), "channel view (diagnostic)");
            }
            _channelViewTexture.SetData(_channelViewPixels, 0, pcount);
        }

        /// <summary>Draw a mask-window texture (16 texels/tile) anchored to its world origin.</summary>
        private void DrawMaskWindowTexture(SpriteBatch spriteBatch, Texture2D? texture, float opacity)
        {
            if (texture == null)
                return;
            var viewport = Game1.viewport;
            var dest = new Rectangle(_lastWaterTileX * 64 - viewport.X, _lastWaterTileY * 64 - viewport.Y,
                texture.Width * 4, texture.Height * 4);
            spriteBatch.Draw(texture, dest, Color.White * opacity);
        }

        /// <summary>Draw a viewport-sized RT 1:1 over the screen.</summary>
        private static void DrawScreenTexture(SpriteBatch spriteBatch, Texture2D? texture)
        {
            if (texture == null)
                return;
            spriteBatch.Draw(texture, new Rectangle(0, 0, texture.Width, texture.Height), Color.White * 0.85f);
        }
    }
}
