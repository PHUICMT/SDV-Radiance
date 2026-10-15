//=============================================================================
// floodapply.fx  -  SDV-Radiance (EXPERIMENT, 15 Oct 2026)
// The flood pass worked out on a half-size copy of the frame, laid back over the full frame as a
// ratio: how many times brighter or darker the light made each spot, times the sharp picture.
//=============================================================================

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler2D SourceSampler : register(s0);    // the full-size frame before the flood

texture LitHalfTexture;                    // the flood's result on the half-size copy
sampler2D LitHalfSampler = sampler_state
{
    Texture = <LitHalfTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
texture SceneHalfTexture;                  // the half-size copy itself
sampler2D SceneHalfSampler = sampler_state
{
    Texture = <SceneHalfTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// Below this a channel is too dark to say how many times the light scaled it; the floor keeps a
// near-black pixel from turning a small lift into a huge ratio.
static const float RatioFloor = 0.03;

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 ApplyPS(PixelInput input) : SV_TARGET
{
    float4 sharp = tex2D(SourceSampler, input.UV);
    float3 litHalf = tex2D(LitHalfSampler, input.UV).rgb;
    float3 sceneHalf = tex2D(SceneHalfSampler, input.UV).rgb;
    float3 ratio = (litHalf + RatioFloor) / (sceneHalf + RatioFloor);
    float3 lit = (sharp.rgb + RatioFloor) * ratio - RatioFloor;
    return float4(saturate(lit), sharp.a);
}

technique Apply { pass Ratio { PixelShader = compile PS_SHADERMODEL ApplyPS(); } }
