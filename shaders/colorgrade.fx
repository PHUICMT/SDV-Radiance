//=============================================================================
// colorgrade.fx  —  SDV-Radiance
// Parametric color grading: temperature, brightness, contrast, saturation,
// optional ACES filmic tone mapping, blended back by Strength.
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

float Strength;     // 0..1 blend between original and graded
float Contrast;     // ~0.5..1.5 (1 = neutral), pivoted around mid-grey
float Saturation;   // 0..2 (1 = neutral)
float Temperature;  // -1..1 (+ = warmer, - = cooler)
float Brightness;   // ~0.5..1.5 multiplier (1 = neutral)
float ToneMap;      // >0.5 = apply ACES filmic tone mapping

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// Narkowicz ACES filmic tone-mapping approximation (expects linear HDR input).
float3 ACESFilm(float3 x)
{
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

// Approximate sRGB <-> linear (gamma 2.2). Cheap and good enough for grading.
float3 ToLinear(float3 c) { return pow(saturate(c), 2.2); }
float3 ToSRGB(float3 c)   { return pow(saturate(c), 1.0 / 2.2); }

float4 GradePS(PixelInput input) : SV_TARGET
{
    float4 src = tex2D(SourceSampler, input.UV);

    // --- linear space: physically-meaningful ops (exposure, white balance) ---
    float3 lin = ToLinear(src.rgb);

    lin *= Brightness; // exposure

    // Temperature: warm boosts red / cuts blue (channel gains, in linear).
    lin *= float3(1.0 + Temperature * 0.15, 1.0 + Temperature * 0.03, 1.0 - Temperature * 0.15);

    // Highlight rolloff: gently compress only the values above a knee so bright
    // areas (sand, snow, bloom) keep detail instead of clipping to flat white.
    // Shadows and midtones are untouched, so it tames blowout without going muddy.
    float3 over = max(lin - 0.65, 0.0);
    lin = min(lin, 0.65) + over / (1.0 + over * 0.9);

    // Optional filmic tone map — only useful once exposure pushes values >1.
    // (The SDV frame is already LDR, so this is off by default to avoid a muddy
    // double tone-map.)
    if (ToneMap > 0.5)
        lin = ACESFilm(lin);

    // --- back to gamma space: perceptual ops (contrast, saturation) ---
    float3 col = ToSRGB(lin);

    col = (col - 0.5) * Contrast + 0.5;          // contrast, pivoted at mid-grey

    float lum = dot(col, LUMA);
    col = lerp(lum.xxx, col, Saturation);        // saturation

    col = saturate(col);

    // Blend back toward the original by (1 - Strength).
    float3 outc = lerp(src.rgb, col, saturate(Strength));
    return float4(outc, src.a);
}

technique ColorGrade { pass P0 { PixelShader = compile PS_SHADERMODEL GradePS(); } }
