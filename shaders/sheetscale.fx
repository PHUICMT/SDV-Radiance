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
// How much further the soften reaches where the art is busy at the scale of one source pixel
// (speckled leaves, dither, noise; see TextureBusyness), in texels of the four-times sheet. xBR rounds staircases and nothing else, and a
// tent of SoftRadius is a fifth of a source pixel, so a tile painted in single-pixel leaves came out
// exactly as sharp as the game drew it beside tiles that went soft: the patch of hedge the author
// kept circling. Zero is the old soften everywhere.
float DitherRadius;
// How many texels of the four-times sheet one source pixel is: 4.
float SourcePixelTexels;
// The sprite the xBR pass is drawing: x, y, width, height in source pixels. The pass reads
// no texel outside it (see AtWithin), because on a sheet whose cells touch the pixels past a
// sprite's edge are the next sprite, and read as neighbours they put a dark frame round every
// cell. The pass writes a target of exactly this rectangle at four times the texels.
float4 SourceRect;
// Where the xBR pass may READ, in source pixels, when that is more than it writes: a map tile
// baked with its neighbours drawn round it (see MapTileNeighbours) writes its own rectangle and
// one texel more, and reads two past that, into the tiles that continue it. Zero width means the
// reads stay inside SourceRect, as they do for a sprite.
float4 ReadRect;
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
    float4 readable = ReadRect.z > 0.5 ? ReadRect : SourceRect;
    float2 lowest = (readable.xy + 0.5) * TexelSize;
    float2 highest = (readable.xy + readable.zw - 0.5) * TexelSize;
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

// ---------------------------------------------------------------------------------------------
// The other kernels the soft look can be made with. Both double: the bake runs them twice for
// four times the texels, the second pass reading the first's output. Each output texel works out
// which quarter of its source pixel it is and takes that quarter's colour, so they read
// SourceRect / ReadRect exactly as the xBR pass does.

// 0 is MMPX as published. 1 keeps it off the art's edges: a corner is not rounded against the
// transparent surround, and a 45 degree corner only turns where the staircase really carries on.
// Written here from those two ideas, not from anyone's code.
float EdgeGuard;

float4 Near(float2 centre, float offsetX, float offsetY) { return NeighbourWithinSprite(centre, offsetX, offsetY); }

// A pixel as one number the rules can compare: seven bits of each channel and three of alpha,
// twenty-four bits in all, which a float holds exactly. Twenty-odd colours alive at once is more
// than ps_3_0's registers hold, and the rules only ever ask whether two pixels are the same one;
// two colours a single step apart in one channel count as the same, as Same() already has them.
float Key(float4 colour)
{
    float4 steps = floor(colour * float4(127.0, 127.0, 127.0, 7.0) + 0.5);
    return dot(steps, float4(1.0, 128.0, 16384.0, 2097152.0));
}

bool Is(float first, float second) { return first == second; }

// Read without a gradient, so a read inside a branch stays there instead of every read in the
// function being hoisted out of its branch and held at once. The sheets have no mip levels, so
// level zero is the only level there is.
float4 NearAtLevelZero(float2 centre, float offsetX, float offsetY)
{
    float2 sampleUv = centre + float2(offsetX, offsetY) * TexelSize;
    float4 readable = ReadRect.z > 0.5 ? ReadRect : SourceRect;
    float2 lowest = (readable.xy + 0.5) * TexelSize;
    float2 highest = (readable.xy + readable.zw - 0.5) * TexelSize;
    return tex2Dlod(SheetSampler, float4(clamp(sampleUv, lowest, highest), 0.0, 0.0));
}

float KeyNear(float2 centre, float offsetX, float offsetY) { return Key(NearAtLevelZero(centre, offsetX, offsetY)); }

// The MMPX luma: the three channels plus one, weighted by how opaque the pixel is, in byte units.
float MmpxLuma(float4 colour)
{
    return (dot(colour.rgb, float3(255.0, 255.0, 255.0)) + 1.0) * (256.0 - colour.a * 255.0);
}

// Which pixel a quarter takes, as a number, so the rules can pass the choice about without
// carrying colours: 0 the pixel itself, 1 the one above, 2 left, 3 right, 4 below.
static const float TakeE = 0.0;
static const float TakeB = 1.0;
static const float TakeD = 2.0;
static const float TakeF = 3.0;
static const float TakeH = 4.0;

// MMPX, Morgan McGuire and Mara Gagiu, "MMPX Style-Preserving Pixel Art Magnification", Journal
// of Computer Graphics Techniques 10(2), 2021. Copyright 2020 Morgan McGuire and Mara Gagiu, MIT
// license (see CREDITS.md). Ported from their reference implementation: the 1:1 slope rules, the
// intersection rules and the 2:1 slope rules, in their order, each quarter of a source pixel
// taking J, K, L or M. It invents no colour: every output is one of the pixels around it.
float4 SheetMmpxPS(PixelInput input) : SV_TARGET
{
    float2 sourceCoord = SourceRect.xy + input.UV * SourceRect.zw;
    float2 inPixel = frac(sourceCoord);
    float2 centre = (floor(sourceCoord) + 0.5) * TexelSize;

    float4 colourB = NearAtLevelZero(centre, 0.0, -1.0), colourD = NearAtLevelZero(centre, -1.0, 0.0), colourE = NearAtLevelZero(centre, 0.0, 0.0);
    float4 colourF = NearAtLevelZero(centre, 1.0, 0.0), colourH = NearAtLevelZero(centre, 0.0, 1.0);
    float A = KeyNear(centre, -1.0, -1.0), B = Key(colourB), C = KeyNear(centre, 1.0, -1.0);
    float D = Key(colourD), E = Key(colourE), F = Key(colourF);
    float G = KeyNear(centre, -1.0, 1.0), H = Key(colourH), I = KeyNear(centre, 1.0, 1.0);
    float J = TakeE, K = TakeE, L = TakeE, M = TakeE;
    bool guarded = EdgeGuard > 0.5;

    if (!(Is(A, E) && Is(B, E) && Is(C, E) && Is(D, E) && Is(F, E) && Is(G, E) && Is(H, E) && Is(I, E)))
    {
        float P = KeyNear(centre, 0.0, -2.0), Q = KeyNear(centre, -2.0, 0.0), R = KeyNear(centre, 2.0, 0.0), S = KeyNear(centre, 0.0, 2.0);
        float Bl = MmpxLuma(colourB), Dl = MmpxLuma(colourD), El = MmpxLuma(colourE), Fl = MmpxLuma(colourF), Hl = MmpxLuma(colourH);

        // With the guard, E matching the far corner lets a lighter pixel round only when the
        // staircase carries on past that corner, and never across the pixel's own like side.
        bool cornerA = Is(E, A) && (!guarded || (!Is(B, KeyNear(centre, -1.0, -2.0)) && !Is(D, KeyNear(centre, -2.0, -1.0))));
        bool cornerC = Is(E, C) && (!guarded || (!Is(B, KeyNear(centre, 1.0, -2.0)) && !Is(F, KeyNear(centre, 2.0, -1.0))));
        bool cornerG = Is(E, G) && (!guarded || (!Is(D, KeyNear(centre, -2.0, 1.0)) && !Is(H, KeyNear(centre, -1.0, 2.0))));
        bool cornerI = Is(E, I) && (!guarded || (!Is(F, KeyNear(centre, 2.0, 1.0)) && !Is(H, KeyNear(centre, 1.0, 2.0))));
        bool upOpen = !guarded || !Is(E, B);
        bool downOpen = !guarded || !Is(E, H);

        // 1:1 slopes
        if (upOpen && Is(D, B) && !Is(D, H) && !Is(D, F) && (El >= Dl || cornerA) && (Is(E, A) || Is(E, C) || Is(E, G))
            && (El < Dl || !Is(A, D) || !Is(E, P) || !Is(E, Q)))
            J = TakeD;
        if (upOpen && Is(B, F) && !Is(B, D) && !Is(B, H) && (El >= Bl || cornerC) && (Is(E, A) || Is(E, C) || Is(E, I))
            && (El < Bl || !Is(C, B) || !Is(E, P) || !Is(E, R)))
            K = TakeB;
        if (downOpen && Is(H, D) && !Is(H, F) && !Is(H, B) && (El >= Hl || cornerG) && (Is(E, A) || Is(E, G) || Is(E, I))
            && (El < Hl || !Is(G, H) || !Is(E, S) || !Is(E, Q)))
            L = TakeH;
        if (downOpen && Is(F, H) && !Is(F, B) && !Is(F, D) && (El >= Fl || cornerI) && (Is(E, C) || Is(E, G) || Is(E, I))
            && (El < Fl || !Is(I, H) || !Is(E, R) || !Is(E, S)))
            M = TakeF;

        // Intersections. The guard leaves them alone beside the transparent surround.
        if (!guarded || !(colourE.a < 0.004 || colourB.a < 0.004 || colourD.a < 0.004 || colourF.a < 0.004 || colourH.a < 0.004))
        {
            if (!Is(E, F) && Is(E, C) && Is(E, I) && Is(E, D) && Is(E, Q) && Is(F, B) && Is(F, H) && !Is(F, KeyNear(centre, 3.0, 0.0))) { K = TakeF; M = TakeF; }
            if (!Is(E, D) && Is(E, A) && Is(E, G) && Is(E, F) && Is(E, R) && Is(D, B) && Is(D, H) && !Is(D, KeyNear(centre, -3.0, 0.0))) { J = TakeD; L = TakeD; }
            if (!Is(E, H) && Is(E, G) && Is(E, I) && Is(E, B) && Is(E, P) && Is(H, D) && Is(H, F) && !Is(H, KeyNear(centre, 0.0, 3.0))) { L = TakeH; M = TakeH; }
            if (!Is(E, B) && Is(E, A) && Is(E, C) && Is(E, H) && Is(E, S) && Is(B, D) && Is(B, F) && !Is(B, KeyNear(centre, 0.0, -3.0))) { J = TakeB; K = TakeB; }
            if (Bl < El && Is(E, G) && Is(E, H) && Is(E, I) && Is(E, S) && !Is(E, A) && !Is(E, D) && !Is(E, C) && !Is(E, F)) { J = TakeB; K = TakeB; }
            if (Hl < El && Is(E, A) && Is(E, B) && Is(E, C) && Is(E, P) && !Is(E, D) && !Is(E, G) && !Is(E, I) && !Is(E, F)) { L = TakeH; M = TakeH; }
            if (Fl < El && Is(E, A) && Is(E, D) && Is(E, G) && Is(E, Q) && !Is(E, B) && !Is(E, C) && !Is(E, I) && !Is(E, H)) { K = TakeF; M = TakeF; }
            if (Dl < El && Is(E, C) && Is(E, F) && Is(E, I) && Is(E, R) && !Is(E, B) && !Is(E, A) && !Is(E, G) && !Is(E, H)) { J = TakeD; L = TakeD; }
        }

        // 2:1 slopes
        if (!Is(H, B))
        {
            if (!Is(H, A) && !Is(H, E) && !Is(H, C))
            {
                if (Is(H, G) && Is(H, F) && Is(H, R) && !Is(H, D) && !Is(H, KeyNear(centre, 2.0, -1.0))) L = M;
                if (Is(H, I) && Is(H, D) && Is(H, Q) && !Is(H, F) && !Is(H, KeyNear(centre, -2.0, -1.0))) M = L;
            }
            if (!Is(B, I) && !Is(B, G) && !Is(B, E))
            {
                if (Is(B, A) && Is(B, F) && Is(B, R) && !Is(B, D) && !Is(B, KeyNear(centre, 2.0, 1.0))) J = K;
                if (Is(B, C) && Is(B, D) && Is(B, Q) && !Is(B, F) && !Is(B, KeyNear(centre, -2.0, 1.0))) K = J;
            }
        }
        if (!Is(F, D))
        {
            if (!Is(D, I) && !Is(D, E) && !Is(D, C))
            {
                if (Is(D, A) && Is(D, H) && Is(D, S) && !Is(D, B) && !Is(D, KeyNear(centre, 1.0, 2.0))) J = L;
                if (Is(D, G) && Is(D, B) && Is(D, P) && !Is(D, H) && !Is(D, KeyNear(centre, 1.0, -2.0))) L = J;
            }
            if (!Is(F, E) && !Is(F, A) && !Is(F, G))
            {
                if (Is(F, C) && Is(F, H) && Is(F, S) && !Is(F, B) && !Is(F, KeyNear(centre, -1.0, 2.0))) K = M;
                if (Is(F, I) && Is(F, B) && Is(F, P) && !Is(F, H) && !Is(F, KeyNear(centre, -1.0, -2.0))) M = K;
            }
        }
    }

    float taken = inPixel.y < 0.5 ? (inPixel.x < 0.5 ? J : K) : (inPixel.x < 0.5 ? L : M);
    float4 chosen = taken < 0.5 ? colourE : taken < 1.5 ? colourB : taken < 2.5 ? colourD : taken < 3.5 ? colourF : colourH;
    return lerp(colourE, chosen, Smoothness);
}

technique SheetMmpx { pass P0 { PixelShader = compile PS_SHADERMODEL SheetMmpxPS(); } }

// EPX (Scale2x), the 1.7 rule, reading the sprite as the passes above do: a quarter takes the
// colour of the two sides it sits between when they agree with each other and not with the far
// sides. The crispest of the kernels and the one that keeps the art's palette most exactly.
float4 SheetEpxPS(PixelInput input) : SV_TARGET
{
    float2 sourceCoord = SourceRect.xy + input.UV * SourceRect.zw;
    float2 inPixel = frac(sourceCoord);
    float2 centre = (floor(sourceCoord) + 0.5) * TexelSize;
    float4 E = Near(centre, 0.0, 0.0);
    float4 up = Near(centre, 0.0, -1.0), right = Near(centre, 1.0, 0.0), left = Near(centre, -1.0, 0.0), down = Near(centre, 0.0, 1.0);
    float4 result = E;
    if (inPixel.x < 0.5 && inPixel.y < 0.5)
        result = (Same(left, up) && !Same(left, down) && !Same(up, right)) ? up : E;
    else if (inPixel.y < 0.5)
        result = (Same(up, right) && !Same(up, left) && !Same(right, down)) ? right : E;
    else if (inPixel.x < 0.5)
        result = (Same(down, left) && !Same(down, right) && !Same(left, up)) ? left : E;
    else
        result = (Same(right, down) && !Same(right, up) && !Same(down, left)) ? down : E;
    return lerp(E, result, Smoothness);
}

technique SheetEpx { pass P0 { PixelShader = compile PS_SHADERMODEL SheetEpxPS(); } }

// ---------------------------------------------------------------------------------------------
// How the soft pages are READ when the game draws from them (not how they are baked). A soft
// sprite is drawn smaller than it was baked (sixty-four texels onto forty-eight pixels at 75 per
// cent zoom) and at fractional positions whenever the camera glides or the wind leans a tree. One
// bilinear read per pixel then lands on a different part of each texel every frame, and thin
// lines brighten and dim as they move: the shimmer. Four reads spread over the pixel's own
// footprint average that phase away, which is the area filter a texture upscaler gets from its
// mipmaps. The footprint comes from the UV's own screen derivatives, so it is right at any zoom
// and under any rotation.
//
// How far the four reads spread, as a share of one screen pixel: 0 is the plain bilinear read.
float SteadySpread;

float4 SoftPageReadPS(PixelInput input) : SV_TARGET
{
    float2 across = ddx(input.UV) * SteadySpread;
    float2 down = ddy(input.UV) * SteadySpread;
    // A rotated grid, so no row or column of the four lines up with a horizontal or vertical edge.
    float4 sum = tex2D(SheetSampler, input.UV - across * 0.125 - down * 0.375);
    sum += tex2D(SheetSampler, input.UV + across * 0.375 - down * 0.125);
    sum += tex2D(SheetSampler, input.UV + across * 0.125 + down * 0.375);
    sum += tex2D(SheetSampler, input.UV - across * 0.375 + down * 0.125);
    return sum * 0.25 * input.Color;
}

technique SoftPageRead { pass P0 { PixelShader = compile PS_SHADERMODEL SoftPageReadPS(); } }

float Brightness(float4 colour)
{
    return dot(colour.rgb, float3(0.2126, 0.7152, 0.0722)) + 0.5 * colour.a;
}

// How busy the art is here at the scale of one source pixel, 0 to 1: the colour steps between
// neighbouring source pixels on a 3x3 grid round this point, averaged across and down, and the
// smaller of the two taken. Speckled leaves, dither and noise step both ways; a one-pixel outline
// or the edge between two fills steps only across itself, so it keeps its edge.
float BrightnessAt(float2 uv, float2 pixelStep, float column, float row)
{
    return Brightness(tex2D(SheetSampler, uv + float2(column, row) * pixelStep));
}

float TextureBusyness(float2 uv, float2 pixelStep)
{
    float topLeft = BrightnessAt(uv, pixelStep, -1.0, -1.0), top = BrightnessAt(uv, pixelStep, 0.0, -1.0), topRight = BrightnessAt(uv, pixelStep, 1.0, -1.0);
    float left = BrightnessAt(uv, pixelStep, -1.0, 0.0), centre = BrightnessAt(uv, pixelStep, 0.0, 0.0), right = BrightnessAt(uv, pixelStep, 1.0, 0.0);
    float bottomLeft = BrightnessAt(uv, pixelStep, -1.0, 1.0), bottom = BrightnessAt(uv, pixelStep, 0.0, 1.0), bottomRight = BrightnessAt(uv, pixelStep, 1.0, 1.0);
    float across = abs(topLeft - top) + abs(top - topRight) + abs(left - centre) + abs(centre - right)
                 + abs(bottomLeft - bottom) + abs(bottom - bottomRight);
    float down = abs(topLeft - left) + abs(left - bottomLeft) + abs(top - centre) + abs(centre - bottom)
               + abs(topRight - right) + abs(right - bottomRight);
    return saturate((min(across, down) / 6.0 - 0.04) * 10.0);
}

// Gradient smoothing, after the kernel: 0 is off, 1 turns a low step between two shades fully into
// a ramp. Pixel art paints a gradient (a sky, the light across a wall, a shaded trunk) as bands of
// a few close shades, and at four times the texels each band edge is a hard line. Every ring read
// whose brightness is within StepLimit of this texel's joins its average; a read across a real
// edge (an outline, two fills) differs by more and is left out, so edges stay where they are.
// Done after the kernel and not before it, because the rules match exact colours and a smoothed
// source would stop them matching. SpriteMaster calls this deposterizing; written here from that
// idea.
float Deposterize;
static const float StepLimit = 0.075;

float4 SmoothSteps(float2 uv, float4 centre)
{
    if (Deposterize < 0.001)
        return centre;
    // Three quarters of a source pixel out: far enough to reach the next band, not the one after.
    float2 reach = TexelSize * SourcePixelTexels * 0.75;
    float centreBrightness = Brightness(centre);
    float4 sum = centre;
    float total = 1.0;
    [unroll] for (int direction = 0; direction < 8; direction++)
    {
        float angle = direction * 0.785398;
        float4 ringPixel = tex2D(SheetSampler, uv + float2(cos(angle), sin(angle)) * reach);
        float step = abs(Brightness(ringPixel) - centreBrightness);
        float weight = saturate(1.0 - step / StepLimit) * (abs(ringPixel.a - centre.a) < 0.02 ? 1.0 : 0.0);
        sum += ringPixel * weight;
        total += weight;
    }
    return lerp(centre, sum / total, Deposterize);
}

// The centre keeps a quarter, the sides an eighth each, the corners a sixteenth; the tent reaches
// further where the art is busy pixel by pixel (see DitherRadius).
float4 Tent(float2 uv, float4 centrePixel)
{
    float radius = SoftRadius;
    if (DitherRadius > 0.001)
        radius += DitherRadius * TextureBusyness(uv, TexelSize * SourcePixelTexels);
    float2 radiusUv = TexelSize * radius;
    float4 sum = centrePixel * 0.25;
    sum += (tex2D(SheetSampler, uv + float2(radiusUv.x, 0.0)) + tex2D(SheetSampler, uv - float2(radiusUv.x, 0.0))
          + tex2D(SheetSampler, uv + float2(0.0, radiusUv.y)) + tex2D(SheetSampler, uv - float2(0.0, radiusUv.y))) * 0.125;
    sum += (tex2D(SheetSampler, uv + radiusUv) + tex2D(SheetSampler, uv - radiusUv)
          + tex2D(SheetSampler, uv + float2(radiusUv.x, -radiusUv.y)) + tex2D(SheetSampler, uv + float2(-radiusUv.x, radiusUv.y))) * 0.0625;
    return SmoothSteps(uv, sum);
}

float4 SheetSoftenPS(PixelInput input) : SV_TARGET
{
    // The draw's own colour, white but for radiance_softtint.
    return Tent(input.UV, tex2D(SheetSampler, input.UV)) * input.Color;
}

// The soften for a map tile baked with its neighbours (see MapTileNeighbours), which also blends
// straight cuts along the tile's own edges. A map's ground is painted tile by tile, and a shadow
// or a patch of darker grass painted into one tile stops dead at its edge: in the pixel art the
// noise round it hides the line, and once the noise is smoothed away the line is all that is
// left, ruled straight under every bush. Where the colour steps across the tile line, and keeps
// stepping all along it, and both sides are solid, the two sides are blended into one another over
// FeatherTexels. Texture that simply carries on across the line has no step and is left alone,
// and an outline against transparency is not solid on both sides, so it keeps its edge. Both tiles
// work this out from the same pixels, so their halves of the blend meet.
float4 TileRectUv;      // the tile's own texels in the kernel output, x, y, width, height in UV
float FeatherTexels;    // how far from the tile line the blend reaches, in texels (0 = off)
float FeatherCut;       // the step in brightness along the line that counts as a cut
// On an upper layer, the other straight cut a map paints: a see-through overlay, a shadow patch
// laid under a bush a tile at a time, whose tile has nothing beside it. Its edge is faded out over
// FeatherTexels where the art at the edge is see-through and nothing lies past it; an opaque edge,
// a leaf or a post, is an outline and keeps it. 1 on, 0 off.
float FadeSeeThroughEdges;
// Whether the tile's own layer carries on past each edge: left, right, top, bottom, 1 or 0.
float4 OwnSides;

float StepAcross(float2 lineUv, float2 outward, float2 along, float reach, out float solid)
{
    float4 inside = tex2D(SheetSampler, lineUv - outward * reach + along);
    float4 outside = tex2D(SheetSampler, lineUv + outward * reach + along);
    solid = min(inside.a, outside.a);
    return abs(Brightness(inside) - Brightness(outside));
}

float4 SheetTileSoftenPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 centrePixel = tex2D(SheetSampler, uv);
    float4 soft = Tent(uv, centrePixel);
    if (FeatherTexels < 0.5)
        return soft * input.Color;
    float2 local = (uv - TileRectUv.xy) / TexelSize;
    bool ground = FadeSeeThroughEdges < 0.5;
    float2 size = TileRectUv.zw / TexelSize;
    float toLeft = local.x, toRight = size.x - local.x, toTop = local.y, toBottom = size.y - local.y;
    float nearest = min(min(toLeft, toRight), min(toTop, toBottom));
    if (nearest >= FeatherTexels)
        return soft * input.Color;
    float2 outward = float2(0.0, 0.0);
    if (nearest == toLeft) outward = float2(-1.0, 0.0);
    else if (nearest == toRight) outward = float2(1.0, 0.0);
    else if (nearest == toTop) outward = float2(0.0, -1.0);
    else outward = float2(0.0, 1.0);
    float2 along = float2(outward.y, outward.x);
    float2 outwardUv = outward * TexelSize;
    float2 alongUv = along * TexelSize * SourcePixelTexels * 1.5;
    float2 lineUv = uv + outwardUv * nearest;
    float reach = FeatherTexels * 0.5;
    if (!ground)
    {
        float insideAlpha = tex2D(SheetSampler, lineUv - outwardUv * reach).a;
        float ownPast = outward.x < -0.5 ? OwnSides.x : outward.x > 0.5 ? OwnSides.y : outward.y < -0.5 ? OwnSides.z : OwnSides.w;
        float seeThrough = (1.0 - ownPast) * step(0.03, insideAlpha) * step(insideAlpha, 0.85);
        float fade = lerp(1.0, smoothstep(0.0, 1.0, nearest / FeatherTexels), seeThrough);
        return soft * fade * input.Color;
    }
    float solidA, solidB, solidC;
    float cut = min(StepAcross(lineUv, outwardUv, -alongUv, reach, solidA),
                min(StepAcross(lineUv, outwardUv, float2(0.0, 0.0), reach, solidB),
                    StepAcross(lineUv, outwardUv, alongUv, reach, solidC)));
    float solid = step(0.9, min(solidA, min(solidB, solidC)));
    // On the ground OwnSides says which sides may be blended across: not one whose neighbour is
    // water, which the game paints over, so what the bake holds there is not what shows.
    float blendable = outward.x < -0.5 ? OwnSides.x : outward.x > 0.5 ? OwnSides.y : outward.y < -0.5 ? OwnSides.z : OwnSides.w;
    solid *= blendable;
    float weight = saturate((cut - FeatherCut) * 25.0) * (1.0 - nearest / FeatherTexels) * solid;
    // Nine taps across the line with a tent's weights, so the blend is a ramp and not steps.
    float4 blended = centrePixel * 5.0;
    [unroll] for (int tap = 1; tap <= 4; tap++)
    {
        float2 offset = outwardUv * (FeatherTexels * tap * 0.25);
        blended += (tex2D(SheetSampler, uv + offset) + tex2D(SheetSampler, uv - offset)) * (5.0 - tap);
    }
    blended /= 25.0;
    return lerp(soft, blended, weight) * input.Color;
}

technique SheetSoften { pass P0 { PixelShader = compile PS_SHADERMODEL SheetSoftenPS(); } }

technique SheetTileSoften { pass P0 { PixelShader = compile PS_SHADERMODEL SheetTileSoftenPS(); } }
