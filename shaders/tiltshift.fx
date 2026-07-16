//=============================================================================
// tiltshift.fx  —  SDV-Radiance Phase 3
// Tilt-shift depth-of-field: sharp middle band, blurred toward the top and
// bottom of the screen (fake miniature look). Separable Gaussian blur + a
// vertical-position composite between sharp and blurred.
// Target: MonoGame OpenGL (Shader Model 3.0), used as a SpriteBatch effect.
//=============================================================================

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler2D SourceSampler : register(s0);

texture BlurTexture;
sampler2D BlurSampler = sampler_state
{
    Texture = <BlurTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float2 TexelSize;   // blur step
float TopEdge;      // sharp region starts here from the top (0..1)
float BottomEdge;   // sharp region ends here (0..1)
float Strength;     // max blur mix

static const int TAPS = 5;
static const float W[5] = { 0.227027, 0.194595, 0.121622, 0.054054, 0.016216 };

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 BlurHPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 o = float2(TexelSize.x * i, 0.0);
        sum += tex2D(SourceSampler, input.UV + o).rgb * W[i];
        sum += tex2D(SourceSampler, input.UV - o).rgb * W[i];
    }
    return float4(sum, 1.0);
}

float4 BlurVPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 o = float2(0.0, TexelSize.y * i);
        sum += tex2D(SourceSampler, input.UV + o).rgb * W[i];
        sum += tex2D(SourceSampler, input.UV - o).rgb * W[i];
    }
    return float4(sum, 1.0);
}

float4 CompositePS(PixelInput input) : SV_TARGET
{
    float4 sharp = tex2D(SourceSampler, input.UV);
    float3 blur = tex2D(BlurSampler, input.UV).rgb;

    float y = input.UV.y;
    float top = 1.0 - smoothstep(TopEdge - 0.12, TopEdge, y);      // blur above the sharp band
    float bottom = smoothstep(BottomEdge, BottomEdge + 0.12, y);   // blur below it
    float amt = saturate(top + bottom) * Strength;

    return float4(lerp(sharp.rgb, blur, amt), sharp.a);
}

technique BlurH     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurHPS(); } }
technique BlurV     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurVPS(); } }
technique Composite { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
