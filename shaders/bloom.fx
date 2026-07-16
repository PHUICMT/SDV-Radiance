//=============================================================================
// bloom.fx  —  SDV-Radiance Phase 1
// Bright-pass extraction, separable Gaussian blur, additive composite.
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

// SpriteBatch binds the drawn texture to sampler register s0.
sampler2D SourceSampler : register(s0);

// Blurred bloom target, sampled during the composite pass.
texture BloomTexture;
sampler2D BloomSampler = sampler_state
{
    Texture = <BloomTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

float Threshold;      // luminance cutoff for the bright-pass (0..1)
float Intensity;      // how strongly bloom is added back (0..2)
float2 TexelSize;     // (1/width, 1/height) of the blur source, for tap offsets

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// 9-tap Gaussian weights (normalized).
static const int   TAPS = 5;
static const float W[5] = { 0.227027, 0.194595, 0.121622, 0.054054, 0.016216 };

//-----------------------------------------------------------------------------
// Bright pass: keep only pixels brighter than Threshold (soft knee).
//-----------------------------------------------------------------------------
float4 BrightPassPS(PixelInput input) : SV_TARGET
{
    float4 c = tex2D(SourceSampler, input.UV);
    float lum = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
    float knee = smoothstep(Threshold, Threshold + 0.15, lum);
    return float4(c.rgb * knee, 1.0);
}

//-----------------------------------------------------------------------------
// Separable Gaussian blur (horizontal / vertical).
//-----------------------------------------------------------------------------
float4 BlurHorizontalPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 off = float2(TexelSize.x * i, 0.0);
        sum += tex2D(SourceSampler, input.UV + off).rgb * W[i];
        sum += tex2D(SourceSampler, input.UV - off).rgb * W[i];
    }
    return float4(sum, 1.0);
}

float4 BlurVerticalPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 off = float2(0.0, TexelSize.y * i);
        sum += tex2D(SourceSampler, input.UV + off).rgb * W[i];
        sum += tex2D(SourceSampler, input.UV - off).rgb * W[i];
    }
    return float4(sum, 1.0);
}

//-----------------------------------------------------------------------------
// Composite: original scene + Intensity * blurred bloom.
//-----------------------------------------------------------------------------
float4 CompositePS(PixelInput input) : SV_TARGET
{
    float4 scene = tex2D(SourceSampler, input.UV);
    float3 bloom = tex2D(BloomSampler, input.UV).rgb;
    return float4(scene.rgb + bloom * Intensity, scene.a);
}

technique BrightPass    { pass P0 { PixelShader = compile PS_SHADERMODEL BrightPassPS(); } }
technique BlurHorizontal{ pass P0 { PixelShader = compile PS_SHADERMODEL BlurHorizontalPS(); } }
technique BlurVertical  { pass P0 { PixelShader = compile PS_SHADERMODEL BlurVerticalPS(); } }
technique Composite     { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
