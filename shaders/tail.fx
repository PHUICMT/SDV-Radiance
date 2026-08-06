//=============================================================================
// tail.fx  —  SDV-Radiance
// Fused tail pass: color grade + vignette in ONE full-screen draw (1.5.0 perf).
// The math is copied VERBATIM from colorgrade.fx and the vignette half of
// finishing.fx; the floor() between the two blocks emulates the 8-bit render
// target that used to sit between the two passes, so the fused pass reproduces
// the old chain bit for bit. Chromatic aberration is deliberately absent: CA
// samples the graded image at neighbouring UVs, which a fused pass cannot do
// exactly under LinearClamp, so frames with live CA fall back to the separate
// passes (see Apply's stage selection).
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

// --- grade params (identical semantics to colorgrade.fx) ---
float GradeOn;      // >0.5 = run the grade block; else the source passes through untouched
float Strength;     // 0..1 blend between original and graded
float Contrast;     // ~0.5..1.5 (1 = neutral), pivoted around mid-grey
float Saturation;   // 0..2 (1 = neutral)
float Temperature;  // -1..1 (+ = warmer, - = cooler)
float Brightness;   // ~0.5..1.5 multiplier (1 = neutral)
float ToneMap;      // >0.5 = apply ACES filmic tone mapping
float BlueLight;    // 0..1 eye-comfort warm shift, applied AFTER the grade blend

// --- finishing params (identical semantics to finishing.fx, minus CA) ---
float VignetteStrength; // 0 = off .. ~1 = strong edge darkening
float NightAmt;         // 0 by day .. 1 deep night — a touch more vignette at night

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

float4 TailPS(PixelInput input) : SV_TARGET
{
    float4 src = tex2D(SourceSampler, input.UV);
    float3 graded = src.rgb;

    if (GradeOn > 0.5)
    {
        // --- colorgrade.fx GradePS, verbatim ---
        float3 lin = ToLinear(src.rgb);

        lin *= Brightness; // exposure

        // Temperature: warm boosts red / cuts blue (channel gains, in linear).
        lin *= float3(1.0 + Temperature * 0.15, 1.0 + Temperature * 0.03, 1.0 - Temperature * 0.15);

        // Highlight rolloff: gently compress only the values above a knee.
        float3 over = max(lin - 0.65, 0.0);
        lin = min(lin, 0.65) + over / (1.0 + over * 0.9);

        if (ToneMap > 0.5)
            lin = ACESFilm(lin);

        float3 col = ToSRGB(lin);

        col = (col - 0.5) * Contrast + 0.5;          // contrast, pivoted at mid-grey

        float lum = dot(col, LUMA);
        col = lerp(lum.xxx, col, Saturation);        // saturation

        col = saturate(col);

        float3 outc = lerp(src.rgb, col, saturate(Strength));

        outc *= float3(1.0 + BlueLight * 0.06, 1.0 - BlueLight * 0.06, 1.0 - BlueLight * 0.28);

        // The 8-bit render target that used to sit between the grade pass and the
        // finishing pass, reproduced exactly so the fused chain stays bit-identical.
        graded = floor(saturate(outc) * 255.0 + 0.5) / 255.0;
    }

    // --- finishing.fx FinishPS vignette half, verbatim (CA handled by fallback) ---
    float2 dir = input.UV - 0.5;
    float dist = length(dir);
    float vig = (VignetteStrength + NightAmt * 0.12) * smoothstep(0.35, 0.80, dist);
    graded *= (1.0 - vig);

    return float4(graded, src.a);
}

technique Tail { pass P0 { PixelShader = compile PS_SHADERMODEL TailPS(); } }
