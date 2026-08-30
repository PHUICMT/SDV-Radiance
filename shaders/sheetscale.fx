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

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 At(float2 centre, float dx, float dy)
{
    return tex2D(SheetSampler, centre + float2(dx, dy) * TexelSize);
}

bool Same(float4 a, float4 b)
{
    return all(abs(a - b) < 0.004);
}

float4 SheetScalePS(PixelInput input) : SV_TARGET
{
    // Which source texel this output texel belongs to, and which of its four corners it is.
    float2 targetTexel = floor(input.UV * TargetSize);
    float2 sourceTexel = floor(targetTexel * 0.5);
    float2 corner = targetTexel - sourceTexel * 2.0;     // (0,0) top-left ... (1,1) bottom-right
    float2 centre = (sourceTexel + 0.5) * TexelSize;

    float4 P = At(centre, 0.0, 0.0);
    float4 A = At(centre, 0.0, -1.0);   // up
    float4 B = At(centre, 1.0, 0.0);    // right
    float4 C = At(centre, -1.0, 0.0);   // left
    float4 D = At(centre, 0.0, 1.0);    // down

    float4 result = P;
    if (corner.x < 0.5 && corner.y < 0.5)          // top-left: between C and A
        result = (Same(C, A) && !Same(C, D) && !Same(A, B)) ? A : P;
    else if (corner.x >= 0.5 && corner.y < 0.5)    // top-right: between A and B
        result = (Same(A, B) && !Same(A, C) && !Same(B, D)) ? B : P;
    else if (corner.x < 0.5 && corner.y >= 0.5)    // bottom-left: between D and C
        result = (Same(D, C) && !Same(D, B) && !Same(C, A)) ? C : P;
    else                                           // bottom-right: between B and D
        result = (Same(B, D) && !Same(B, A) && !Same(D, C)) ? D : P;
    return lerp(P, result, Smoothness);
}

technique SheetScale { pass P0 { PixelShader = compile PS_SHADERMODEL SheetScalePS(); } }
