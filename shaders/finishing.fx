//=============================================================================
// finishing.fx  —  SDV-Radiance
// Camera-lens finishing pass: vignette (darkened edges) + chromatic aberration
// (radial R/B channel split). Runs last, on the fully graded image.
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

float VignetteStrength; // 0 = off .. ~1 = strong edge darkening
float CAStrength;       // chromatic-aberration UV offset scale (already small, e.g. 0..0.03)

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 FinishPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float2 dir = uv - 0.5;
    float dist = length(dir);

    // Chromatic aberration: split R/B outward from the center, growing with
    // distance so the frame stays crisp in the middle (like a real lens).
    float2 offset = dir * CAStrength * dist;
    float r = tex2D(SourceSampler, uv + offset).r;
    float4 mid = tex2D(SourceSampler, uv);
    float b = tex2D(SourceSampler, uv - offset).b;
    float3 col = float3(r, mid.g, b);

    // Vignette: smooth radial falloff, no darkening until past the mid-radius.
    float vig = VignetteStrength * smoothstep(0.35, 0.80, dist);
    col *= (1.0 - vig);

    return float4(col, mid.a);
}

technique Finishing { pass P0 { PixelShader = compile PS_SHADERMODEL FinishPS(); } }
