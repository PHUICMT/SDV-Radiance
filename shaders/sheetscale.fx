//=============================================================================
// sheetscale.fx  -  SDV-Radiance
// A sprite sheet at twice its size, made from the sheet itself by the Scale2x
// (EPX) rule: each source texel becomes four, and a corner takes the colour of
// the two neighbours it sits between when those two agree with each other and
// disagree with the far sides - the rule that turns a pixel-art staircase into a
// diagonal without inventing colours that were not in the art. Exact equality on
// the sheet's own colours, alpha included, so the transparent surround of a sprite
// stays transparent and no frame of an animation bleeds into the next.
//
// Drawn at half the scale the game asked for, the result puts two texels where the
// game put one: what a texture upscaler mod does at load, done here on the card and
// only for the sheets in use (see SheetUpscaleCache).
// Target: MonoGame OpenGL (Shader Model 3.0).
//=============================================================================

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler2D SheetSampler : register(s0);

float2 TexelSize;   // 1 / SOURCE sheet size
float2 TargetSize;  // the 2x target in texels
// 0 keeps the source texel (a plain doubling, which draws back to the untouched
// picture), 1 is the full Scale2x corner rounding. Baked into the sheet, so the
// dial costs a re-make of the cache and nothing per frame.
float Smoothness;
// The xBR pass (SheetXbr): how wide the anti-aliased edge is, in SOURCE pixels. A quarter
// is one texel of the four-times sheet it writes, so a diagonal gets a one-texel ramp and
// drawn at the game's 4x that is one screen pixel of softness, the same as a linear filter
// gives a sheet drawn at two pixels a texel. Zero is the hard-edged xBR of the emulator
// shaders.
float EdgeSoftness;
// Two colours closer than this (on the luminance-plus-alpha scale below, 0 to 1.5) count as
// the same for the edge rules, which is what stops a dithered gradient being read as a
// hundred little edges.
float EqualThreshold;
// The soften pass (SheetSoften) that follows xBR for the soft look: a tent over the neighbours
// this many texels out. It stands in for what a texture-upscaler mod gets by drawing a sheet
// six times its size down to four with a linear filter: every colour boundary inside a sprite
// averaged over its neighbours, not only the outlines the kernel rounded. Premultiplied
// alpha, so a transparent surround blends to nothing rather than to black.
float SoftRadius;
// The sprite the xBR pass is drawing: x, y, width, height in source pixels. The pass reads
// no texel outside it (see AtWithin), because on a sheet whose cells touch the pixels past a
// sprite's edge are the next sprite, and read as neighbours they put a dark frame round every
// cell. The pass writes a target of exactly this rectangle at four times the texels.
float4 SourceRect;
// The cell the Scale2x kernel may read within, in SOURCE pixels, or 0 to read the whole sheet.
// A map tilesheet is a grid of 16-pixel tiles that have nothing to do with their neighbours in
// the sheet, and a tile's border texel that took its corner from the tile beside it put a seam
// along every tile edge of a floor. Measured on the vanilla outdoor sheet: 2 to 3 texels on
// more than half of its tiles. Sprite sheets keep 0, since their cells are not all one size.
float CellSize;

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 NeighbourAt(float2 sourceTexel, float offsetX, float offsetY)
{
    float2 texel = sourceTexel + float2(offsetX, offsetY);
    if (CellSize > 0.5)
    {
        float2 cellOrigin = floor(sourceTexel / CellSize) * CellSize;
        texel = clamp(texel, cellOrigin, cellOrigin + CellSize - 1.0);
    }
    return tex2D(SheetSampler, (texel + 0.5) * TexelSize);
}

bool Same(float4 first, float4 second)
{
    return all(abs(first - second) < 0.004);
}

float4 SheetScalePS(PixelInput input) : SV_TARGET
{
    // Which source texel this output texel belongs to, and which of its four corners it is.
    float2 targetTexel = floor(input.UV * TargetSize);
    float2 sourceTexel = floor(targetTexel * 0.5);
    float2 corner = targetTexel - sourceTexel * 2.0;     // (0,0) top-left ... (1,1) bottom-right

    float4 centrePixel = NeighbourAt(sourceTexel, 0.0, 0.0);
    float4 up = NeighbourAt(sourceTexel, 0.0, -1.0);   // up
    float4 right = NeighbourAt(sourceTexel, 1.0, 0.0);    // right
    float4 left = NeighbourAt(sourceTexel, -1.0, 0.0);   // left
    float4 down = NeighbourAt(sourceTexel, 0.0, 1.0);    // down

    float4 result = centrePixel;
    if (corner.x < 0.5 && corner.y < 0.5)          // top-left: between C and A
        result = (Same(left, up) && !Same(left, down) && !Same(up, right)) ? up : centrePixel;
    else if (corner.x >= 0.5 && corner.y < 0.5)    // top-right: between A and B
        result = (Same(up, right) && !Same(up, left) && !Same(right, down)) ? right : centrePixel;
    else if (corner.x < 0.5 && corner.y >= 0.5)    // bottom-left: between D and C
        result = (Same(down, left) && !Same(down, right) && !Same(left, up)) ? left : centrePixel;
    else                                           // bottom-right: between B and D
        result = (Same(right, down) && !Same(right, up) && !Same(down, left)) ? down : centrePixel;
    return lerp(centrePixel, result, Smoothness);
}

technique SheetScale { pass P0 { PixelShader = compile PS_SHADERMODEL SheetScalePS(); } }

// xBR, level 2, written here from the algorithm (Hyllian's xBR: an edge is a diagonal
// between two like corners cutting across two unlike ones; where one is found the output
// texel on the far side of it takes the colour of the nearer corner, and the shallow and
// steep variants tilt it to 30 and 60 degrees). It is what the texture-upscaler mods draw
// with, and the reason their art looks rounded without looking blurred: the picture is made
// of the sheet's own colours, and only the coverage at an edge is a blend.
//
// Every output texel decides for itself: which source pixel it belongs to, where inside it
// it sits (fp), and for each of the four corners of that pixel whether an edge runs past.
// The four corners are worked as one float4 (component x is the bottom-right corner, then
// bottom-left, top-left, top-right), so the rules are written once.
//
// Distances are on luminance plus half the alpha: a sheet is premultiplied, so a transparent
// texel is black with no alpha and an opaque black one sits half a unit away from it, which
// keeps an outline's edge from being read as the same thing as the emptiness beside it.

float Value(float4 colour)
{
    return dot(colour.rgb, float3(0.2126, 0.7152, 0.0722)) + 0.5 * colour.a;
}

float4 ColourDistance(float4 first, float4 second) { return abs(first - second); }

// A neighbour read that never leaves the sprite: past its edge the edge texel repeats, the
// way a clamped sampler treats the edge of a texture.
float4 NeighbourWithinSprite(float2 centre, float offsetX, float offsetY)
{
    float2 sampleUv = centre + float2(offsetX, offsetY) * TexelSize;
    float2 lowest = (SourceRect.xy + 0.5) * TexelSize;
    float2 highest = (SourceRect.xy + SourceRect.zw - 0.5) * TexelSize;
    return tex2D(SheetSampler, clamp(sampleUv, lowest, highest));
}

// df(a,b) + df(a,c) + df(d,e) + df(d,f) + 4 df(g,h): how much the corner "e" and its diagonal
// partner "d" differ from the pixels an edge between them would cut.
float4 WeightedDistance(float4 a, float4 b, float4 c, float4 d, float4 e, float4 f, float4 g, float4 h)
{
    return ColourDistance(a, b) + ColourDistance(a, c) + ColourDistance(d, e) + ColourDistance(d, f) + 4.0 * ColourDistance(g, h);
}

float4 SheetXbrPS(PixelInput input) : SV_TARGET
{
    // The target is the sprite alone, so its UV runs over SourceRect.
    float2 sourceCoord = SourceRect.xy + input.UV * SourceRect.zw;
    float2 positionInPixel = frac(sourceCoord);
    float2 centre = (floor(sourceCoord) + 0.5) * TexelSize;

    //        A1 B1 C1
    //     A0 A  B  C  C4
    //     D0 D  E  F  F4
    //     G0 G  H  I  I4
    //        G5 H5 I5
    float4 A1 = NeighbourWithinSprite(centre, -1.0, -2.0), B1 = NeighbourWithinSprite(centre, 0.0, -2.0), C1 = NeighbourWithinSprite(centre, 1.0, -2.0);
    float4 A0 = NeighbourWithinSprite(centre, -2.0, -1.0), A = NeighbourWithinSprite(centre, -1.0, -1.0), B = NeighbourWithinSprite(centre, 0.0, -1.0), C = NeighbourWithinSprite(centre, 1.0, -1.0), C4 = NeighbourWithinSprite(centre, 2.0, -1.0);
    float4 D0 = NeighbourWithinSprite(centre, -2.0, 0.0),  D = NeighbourWithinSprite(centre, -1.0, 0.0),  E = NeighbourWithinSprite(centre, 0.0, 0.0),  F = NeighbourWithinSprite(centre, 1.0, 0.0),  F4 = NeighbourWithinSprite(centre, 2.0, 0.0);
    float4 G0 = NeighbourWithinSprite(centre, -2.0, 1.0),  G = NeighbourWithinSprite(centre, -1.0, 1.0),  H = NeighbourWithinSprite(centre, 0.0, 1.0),  I = NeighbourWithinSprite(centre, 1.0, 1.0),  I4 = NeighbourWithinSprite(centre, 2.0, 1.0);
    float4 G5 = NeighbourWithinSprite(centre, -1.0, 2.0),  H5 = NeighbourWithinSprite(centre, 0.0, 2.0),  I5 = NeighbourWithinSprite(centre, 1.0, 2.0);

    // The neighbourhood as seen from each corner, one component per corner.
    float4 b  = float4(Value(B), Value(D), Value(H), Value(F));
    float4 c  = float4(Value(C), Value(A), Value(G), Value(I));
    float4 e  = Value(E).xxxx;
    float4 d  = b.yzwx;
    float4 f  = b.wxyz;
    float4 g  = c.zwxy;
    float4 h  = b.zwxy;
    float4 i  = c.wxyz;
    float4 i4 = float4(Value(I4), Value(C1), Value(A0), Value(G5));
    float4 i5 = float4(Value(I5), Value(C4), Value(A1), Value(G0));
    float4 h5 = float4(Value(H5), Value(F4), Value(B1), Value(D0));
    float4 f4 = h5.yzwx;

    // Where inside the pixel this texel is, seen from each corner: the 45 degree line and the
    // shallow (30) and steep (60) ones. The constants are the lines' equations per corner.
    const float4 Line45A = float4(1.0, -1.0, -1.0,  1.0);
    const float4 Line45B = float4(1.0,  1.0, -1.0, -1.0);
    const float4 Line45C = float4(1.5,  0.5, -0.5,  0.5);
    const float4 Line30A = float4(1.0, -1.0, -1.0,  1.0);
    const float4 Line30B = float4(0.5,  2.0, -0.5, -2.0);
    const float4 Line30C = float4(1.0,  1.0, -0.5,  0.0);
    const float4 Line60A = float4(1.0, -1.0, -1.0,  1.0);
    const float4 Line60B = float4(2.0,  0.5, -2.0, -0.5);
    const float4 Line60C = float4(2.0,  0.0, -1.0,  0.5);
    float4 delta = max(EdgeSoftness, 0.0001).xxxx;
    float4 coverage45 = smoothstep(Line45C - delta, Line45C + delta, Line45A * positionInPixel.y + Line45B * positionInPixel.x);
    float4 coverage30 = smoothstep(Line30C - delta, Line30C + delta, Line30A * positionInPixel.y + Line30B * positionInPixel.x);
    float4 coverage60 = smoothstep(Line60C - delta, Line60C + delta, Line60A * positionInPixel.y + Line60B * positionInPixel.x);

    float4 threshold = EqualThreshold.xxxx;
    float4 differentEF = step(threshold, ColourDistance(e, f)), differentEH = step(threshold, ColourDistance(e, h));
    float4 differentEG = step(threshold, ColourDistance(e, g)), differentDG = step(threshold, ColourDistance(d, g));
    float4 differentEC = step(threshold, ColourDistance(e, c)), differentBC = step(threshold, ColourDistance(b, c));
    float4 restrictionLevel1 = differentEF * differentEH;
    float4 restrictionLeft = differentEG * differentDG;
    float4 restrictionUp = differentEC * differentBC;

    // An edge runs past this corner when the pixel and its diagonal partner differ from what
    // the edge would cut MORE than the two corners on the edge differ from their surroundings.
    float4 edge = step(WeightedDistance(e, c, g, i, h5, f4, h, f), WeightedDistance(h, d, i5, f, i4, b, e, i) - 0.0001) * restrictionLevel1;
    float4 edgeLeft = step(2.0 * ColourDistance(f, g), ColourDistance(h, c)) * restrictionLeft * edge;
    float4 edgeUp = step(2.0 * ColourDistance(h, c), ColourDistance(f, g)) * restrictionUp * edge;
    float4 coverage = edge * max(coverage45, max(edgeLeft * coverage30, edgeUp * coverage60));

    // The colour on the far side of the edge is whichever of the two edge corners is nearer
    // in value to this pixel, per corner: (F or H), (B or F), (D or B), (H or D).
    float4 nearerIsF = step(ColourDistance(e, f), ColourDistance(e, h));
    float4 colour0 = lerp(H, F, nearerIsF.x);
    float4 colour1 = lerp(F, B, nearerIsF.y);
    float4 colour2 = lerp(B, D, nearerIsF.z);
    float4 colour3 = lerp(D, H, nearerIsF.w);

    // The corner whose edge covers this texel most wins.
    float4 chosen = colour0;
    float best = coverage.x;
    if (coverage.y > best) { best = coverage.y; chosen = colour1; }
    if (coverage.z > best) { best = coverage.z; chosen = colour2; }
    if (coverage.w > best) { best = coverage.w; chosen = colour3; }
    return lerp(E, chosen, best * Smoothness);
}

technique SheetXbr { pass P0 { PixelShader = compile PS_SHADERMODEL SheetXbrPS(); } }

// The centre keeps a quarter, the sides an eighth each, the corners a sixteenth.
float4 SheetSoftenPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float2 radiusUv = TexelSize * SoftRadius;
    float4 sum = tex2D(SheetSampler, uv) * 0.25;
    sum += (tex2D(SheetSampler, uv + float2(radiusUv.x, 0.0)) + tex2D(SheetSampler, uv - float2(radiusUv.x, 0.0))
          + tex2D(SheetSampler, uv + float2(0.0, radiusUv.y)) + tex2D(SheetSampler, uv - float2(0.0, radiusUv.y))) * 0.125;
    sum += (tex2D(SheetSampler, uv + radiusUv) + tex2D(SheetSampler, uv - radiusUv)
          + tex2D(SheetSampler, uv + float2(radiusUv.x, -radiusUv.y)) + tex2D(SheetSampler, uv + float2(-radiusUv.x, radiusUv.y))) * 0.0625;
    return sum;
}

technique SheetSoften { pass P0 { PixelShader = compile PS_SHADERMODEL SheetSoftenPS(); } }
