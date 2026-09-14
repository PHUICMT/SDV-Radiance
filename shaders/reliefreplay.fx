//=============================================================================
// reliefreplay.fx  -  SDV-Radiance
// The pass that draws every world sprite a second time into the relief buffer,
// grouped by sheet rather than in the game's front-to-back order, so a run of
// one sheet is one draw call instead of a draw call at every change of sheet
// between depth neighbours. Who is in front is decided by the depth buffer, and
// the depth comes from the tint: the game's MonoGame build writes zero into
// every sprite vertex's z whatever layer depth the draw asked for, so the
// sprite's rank in the game's draw order (by layer depth, then the order the
// game drew them) rides in the tint's red and green channels (sixteen bits) and
// this vertex shader puts it back into z. A rank never ties, so the depth test,
// greater-or-equal against a buffer cleared to zero, picks the same winner at
// every overlap on every frame.
// A pixel is written only where the sheet's alpha reaches a half: pixel art
// has no soft edge to blend. A fading sprite's tint alpha scales the coverage
// the pixel keeps rather than deciding whether the pixel exists.
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

// Sprite space (the frame's pixels) to clip space, with the buffer's own scale folded in.
float4x4 MatrixTransform;

struct VertexInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

PixelInput ReliefReplayVS(VertexInput input)
{
    PixelInput output;
    // Red and green are the two bytes of the sprite's rank in the game's draw order,
    // times 65535, high byte first.
    // The two weights are written out rather than left as a division, and the rank is
    // sixteen bits rather than twenty-four, because of how the shader is compiled: mgfxc
    // folds the arithmetic into a constant vector and writes that vector into the GLSL
    // source as decimal literals with six places. A twenty-four bit rank needs the
    // reciprocal of 16777215, which is smaller than a ten-millionth and was written into
    // the shader the card ran as a literal 0.0, so every sprite arrived at the depth test
    // with z multiplied by nothing. The picture that came out of that is in the worklog for
    // 2026-09-07: an overlap won by whichever sprite the texture sort happened to put last,
    // which changed every frame. Sixteen bits gives weights of 0.996109 and 0.003891, which
    // survive six decimal places with room to spare, and sixteen bits is four times the
    // sprites a frame has ever recorded.
    float layerDepth = input.Color.r * 0.99610895 + input.Color.g * 0.00389105;
    float4 position = float4(input.Position.xy, layerDepth, 1.0);
    output.Position = mul(position, MatrixTransform);
    output.Color = input.Color;
    output.UV = input.UV;
    return output;
}

// TWO TARGETS out of the one pass that already draws every world sprite. COLOR0 is the normal
// buffer this pass has always written; COLOR1 is the rank the depth test just decided, which is
// the only place the answer to "which sprite is in FRONT here" exists as a picture. The depth
// buffer itself cannot be read: on this build a render target's depth is a renderbuffer with no
// texture behind it, so the number has to be written as colour or it cannot be sampled at all.
//
// Free in every sense that matters: the same draw, the same sprites, the same depth test, and
// the value is already sitting in input.Color where the vertex shader read it.
struct ReliefTargets
{
    float4 Normal : COLOR0;
    float4 Rank   : COLOR1;
};

ReliefTargets ReliefReplayPS(PixelInput input)
{
    float4 sample = tex2D(SheetSampler, input.UV);
    // The SHEET's alpha decides whether the pixel exists; the tint's alpha (a canopy fading
    // because the farmer stands behind it) only scales the coverage the pixel keeps. Testing
    // the product made a whole canopy drop out of the buffer while it faded past a half and
    // come back as it faded up, and everything behind it flickered.
    clip(sample.a - 0.5);
    // At the silhouette the relief LEANS OUT with the art's own coverage rather than arriving
    // at full strength the moment the pixel is kept. Without this the buffer's edge is binary,
    // and a sprite that moves a fraction of a pixel - a tree leaning in the wind, whose canopy
    // the sway draws as overlapping strips - snapped its whole outline from one texel to the
    // next; the rim light, which is strongest exactly there, turned that into a flicker over
    // every canopy and the grass under it. The soft look made it worse, because its edges are
    // a ramp rather than a step. This is what the blend the replay used to draw with did, with
    // the flat normal standing in for whatever was behind.
    float coverage = sample.a * input.Color.a;
    float3 flatNormal = float3(0.5, 0.5, 1.0);
    ReliefTargets output;
    output.Normal = float4(lerp(flatNormal, sample.rgb, saturate(coverage)), coverage);
    // The rank goes back out in the two bytes it arrived in, so whoever reads it puts it back
    // together with the same two weights the vertex shader used and no third rounding creeps in
    // between. Alpha says a sprite is here at all: the clear cannot be given a colour of its own
    // when two targets share one Clear, so a reader must gate on this and never on the rank.
    output.Rank = float4(input.Color.r, input.Color.g, 0.0, coverage);
    return output;
}

technique ReliefReplay
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL ReliefReplayVS();
        PixelShader = compile PS_SHADERMODEL ReliefReplayPS();
    }
}
