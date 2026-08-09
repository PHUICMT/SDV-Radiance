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
        // Not a water channel, and painted by floodlight.fx rather than by an overlay texture:
        // which pixels the lighting pass believes ARE a light source rather than lit by one.
        // The question "is the flame being caught at all" has been answered by argument twice and
        // both answers were wrong, so it gets a picture instead.
        Emitter,
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

        /// <summary>Layer names checked for labels, BOTTOM to TOP — the resolve walks it backwards.</summary>
        private static readonly string[] LabelLayerNames =
            { "Back", "Back2", "Buildings", "Buildings2", "Front", "Front2", "AlwaysFront" };

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
            if (tile is xTile.Tiles.AnimatedTile at && at.TileFrames is { Length: > 0 })
                tile = at.TileFrames[0];
            if (tile?.TileSheet == null)
                return null;
            try
            {
                var texture = Game1.content.Load<Texture2D>(tile.TileSheet.ImageSource);
                var ib = tile.TileSheet.GetTileImageBounds(tile.TileIndex);
                return OpaqueBits(texture, new Rectangle(ib.X, ib.Y, ib.Width, ib.Height)).bits;
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
            if (_labelDiffPixels == null || _labelDiffPixels.Length < pcount)
                _labelDiffPixels = new Color[pcount];
            Array.Clear(_labelDiffPixels, 0, pcount);

            // Resolve layer objects once — LabelStore.Get(location,...) would re-find them per tile.
            var layers = new xTile.Layers.Layer?[LabelLayerNames.Length];
            for (int i = 0; i < LabelLayerNames.Length; i++)
                layers[i] = location.map?.GetLayer(LabelLayerNames[i]);

            long labeledOpinionPixels = 0, agreeLiquid = 0, agreeDry = 0;
            long missingWater = 0, falseWater = 0;
            long glassPixels = 0, glassMirrored = 0;
            long deckSkipped = 0;
            // Walk-on DECKS are excluded from the tally, not scored. A bridge's railing slots and
            // its painted shadow are see-through, so this rule ("the topmost layer whose art is
            // opaque decides; otherwise the water below shows") calls them water and the mask
            // deliberately does not: the spec for the subsystem is that a bridge never ripples.
            // Scoring them anyway parked a permanent few-hundred-pixel MISSING count on every
            // river map, which is exactly the size of shortfall a real regression would show.
            var deckSurfaces = SurfaceMap.For(location);
            var missingByClass = new Dictionary<byte, long>();
            var perTile = new List<(int tx, int ty, int miss, int falsePx)>();
            var tileLabelBuf = new byte[LabelLayerNames.Length][];
            var tileOpaqueBuf = new bool[]?[LabelLayerNames.Length];

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
                        tileOpaqueBuf[i] = i > 0 && tileLabelBuf[i] != null
                            ? OpaqueBitsForLayerTile(layers[i], worldTx, worldTy)
                            : null;
                    }
                    if (!anyLabel)
                        continue;
                    if (deckSurfaces != null && deckSurfaces.GetSurface(worldTx, worldTy) == SurfaceClass.Deck)
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
                                deckSkipped++;
                        }
                        continue;
                    }

                    int tileMiss = 0, tileFalse = 0;
                    int px0 = tx * Texels, py0 = ty * Texels;
                    for (int p = 0; p < 256; p++)
                    {
                        // Topmost layer with an opinion (255 = unset = no opinion) wins; an
                        // overlay's opinion exists only where its art is opaque (see above).
                        byte cls = 255;
                        for (int i = layers.Length - 1; i >= 0; i--)
                        {
                            byte c = tileLabelBuf[i] != null ? tileLabelBuf[i][p] : (byte)255;
                            if (c == 255) continue;
                            if (tileOpaqueBuf[i] is { } opaque && !opaque[p]) continue;
                            cls = c; break;
                        }
                        if (cls == 255)
                            continue;

                        int pi = (py0 + p / 16) * pw + px0 + p % 16;
                        Color m = maskPixels[pi];

                        if (cls == 13) // glass: reflects but is not liquid — informational only in v1
                        {
                            glassPixels++;
                            if (m.G > 0) glassMirrored++;
                            continue;
                        }

                        labeledOpinionPixels++;
                        if (IsLiquidClass(cls))
                        {
                            if (MaskAgreesWithLiquid(cls, m))
                            {
                                agreeLiquid++;
                                _labelDiffPixels[pi] = new Color(0, 160, 0, 140);          // green: liquid ok
                            }
                            else
                            {
                                missingWater++; tileMiss++;
                                missingByClass[cls] = missingByClass.TryGetValue(cls, out long n) ? n + 1 : 1;
                                _labelDiffPixels[pi] = new Color(255, 0, 0, 230);          // red: label says liquid, mask has none
                            }
                        }
                        else
                        {
                            if (m.R > 0)
                            {
                                falseWater++; tileFalse++;
                                _labelDiffPixels[pi] = new Color(255, 220, 0, 230);        // yellow: mask ripples where label says solid/ground
                            }
                            else
                            {
                                agreeDry++;
                                _labelDiffPixels[pi] = new Color(40, 60, 200, 40);         // faint blue: labeled-dry agreement (shows coverage)
                            }
                        }
                    }
                    if (tileMiss > 0 || tileFalse > 0)
                        perTile.Add((worldTx, worldTy, tileMiss, tileFalse));
                }
            }

            UploadLabelDiffTexture(pcount);

            var report = new System.Text.StringBuilder();
            long disagreements = missingWater + falseWater;
            double accuracy = labeledOpinionPixels > 0 ? 100.0 * (labeledOpinionPixels - disagreements) / labeledOpinionPixels : 100.0;
            report.AppendLine($"[verify] {location.NameOrUniqueName}: window {tilesW}x{tilesH} tiles, "
                + $"{labeledOpinionPixels:N0} labeled pixels checked"
                + " (measure STANDING STILL - a rebuild mid-walk can skew mask vs labels by one tile"
                + " and print paired missing/false ghosts)");
            report.AppendLine($"[verify] accuracy {accuracy:0.00}%  —  agree liquid {agreeLiquid:N0}, agree dry {agreeDry:N0}, "
                + $"MISSING water {missingWater:N0}, FALSE water {falseWater:N0}"
                + (glassPixels > 0 ? $", glass {glassPixels:N0} (mirrored {glassMirrored:N0})" : "")
                + (deckSkipped > 0 ? $", deck {deckSkipped:N0} px not scored (bridges never ripple)" : ""));
            foreach (var kv in missingByClass.OrderByDescending(kv => kv.Value))
                report.AppendLine($"[verify]   missing by class: {ClassName(kv.Key)} = {kv.Value:N0} px");
            if (perTile.Count > 0)
            {
                report.AppendLine($"[verify] {perTile.Count} tiles disagree; worst {Math.Min(worstToList, perTile.Count)} "
                    + "(radiance_tile on them prints the per-layer story):");
                foreach (var t in perTile.OrderByDescending(t => t.miss + t.falsePx).Take(worstToList))
                    report.AppendLine($"[verify]   tile ({t.tx},{t.ty})  missing={t.miss}/256  false={t.falsePx}/256");
                report.AppendLine("[verify] overlay: radiance_debug labeldiff  (red = water missing, yellow = false water)");
            }
            else if (labeledOpinionPixels > 0)
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
                _labelDiffTexture = new Texture2D(_device, _waterMask.Width, _waterMask.Height, false, SurfaceFormat.Color);
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
                case DebugOverlayChannel.Emitter:
                    break;      // floodlight.fx already painted it; this just adds the caption
                default:
                    return;
            }
            Utility.drawTextWithShadow(spriteBatch, $"radiance_debug: {DebugChannel}", Game1.smallFont,
                new Vector2(12, 12), Color.White);
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
                _channelViewTexture = new Texture2D(_device, _waterMask.Width, _waterMask.Height, false, SurfaceFormat.Color);
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
