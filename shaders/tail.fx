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
float2 ScreenPixels; // viewport size in pixels, for the dither's pixel grid

// --- finishing params (identical semantics to finishing.fx, minus CA) ---
float VignetteStrength; // 0 = off .. ~1 = strong edge darkening
float3 SkyLightTint = float3(1.0, 1.0, 1.0);  // the colour the sky is lighting the world
                        // with, luminance-normalised on the C# side. Mirrors finishing.fx.
float NightAmt;         // 0 by day .. 1 deep night — a touch more vignette at night

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

// ---- 3D LUT ---------------------------------------------------------------
// A 32x32x32 cube unrolled into a 1024x32 strip: 32 slices side by side, one per blue level,
// each slice 32x32 with red across and green down. That is what every LUT tool exports, so an
// artist's own file drops straight in.
//
// The horizontal and vertical filtering is the hardware's (the sampler is set to linear from
// C#); only the blue axis is interpolated here, between the two neighbouring slices. Both taps
// are inset by half a texel, because a linear tap at a slice's edge would otherwise reach into
// the NEXT slice and read a completely unrelated blue level - the classic LUT seam.
texture LutTexture;
sampler2D LutSampler = sampler_state
{
    Texture = <LutTexture>;
    // LINEAR, unlike every other sampler in this chain. A LUT is a lookup along three continuous
    // axes; reading it with point sampling would quantise the whole picture to 32 levels per
    // channel, which is visible banding across any sky.
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// NOT `register(s1)` with the texture bound from C#: DrawFull calls SetRenderTarget, which
// unbinds the texture slots, so a hand-bound slot 1 was empty by the time the shader ran and
// every pixel sampled black. Declaring it as a texture parameter lets MonoGame own the slot and
// bind it with the draw, which is how BlurTexture and BloomTexture already work here.
float LutAmount;    // 0 = LUT off entirely (the shipped default)

static const float LUT_N = 32.0;

float3 SampleLut(float3 c)
{
    c = saturate(c);
    float slice = c.b * (LUT_N - 1.0);
    float s0 = floor(slice);
    float f  = slice - s0;
    float s1 = min(s0 + 1.0, LUT_N - 1.0);

    float u = (c.r * (LUT_N - 1.0) + 0.5) / (LUT_N * LUT_N);
    float v = (c.g * (LUT_N - 1.0) + 0.5) / LUT_N;
    float sliceU = 1.0 / LUT_N;

    float3 a = tex2D(LutSampler, float2(u + s0 * sliceU, v)).rgb;
    float3 b = tex2D(LutSampler, float2(u + s1 * sliceU, v)).rgb;
    return lerp(a, b, f);
}

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


// Sub-LSB triangular dither for this pass's 8-bit write: a slow gradient (a fog
// bank, the tone curve, the vignette ramp) cannot survive eight bits without
// stepping, and those steps are the colour banding players report. Interleaved
// gradient noise (Jimenez 2014): three instructions, no fetch; the triangular
// remap hides band EDGES where uniform noise leaves them visible. Static across
// frames on purpose - a pattern that changed per frame would be a shimmer of its
// own. Same decision, same idiom as water.fx; correctness, not a look.
float DitherLsb(float2 uv)
{
    float pixelNoise = frac(52.9829189 * frac(0.06711056 * uv.x * ScreenPixels.x
                                            + 0.00583715 * uv.y * ScreenPixels.y));
    return pixelNoise < 0.5 ? sqrt(2.0 * pixelNoise) - 1.0
                            : 1.0 - sqrt(2.0 - 2.0 * pixelNoise);
}

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

        // Contrast on LUMINANCE with a shadow toe - kept in step with colorgrade.fx, which this
        // pass exists to reproduce bit for bit. It had fallen behind once already: colorgrade
        // moved to luminance contrast (so contrast stops inflating saturation) and this copy
        // kept the per-channel line, so the fused frames and the CA frames graded differently.
        // The toe matters more than parity: a pivot line crosses zero at Contrast > 1, and on
        // luminance that zero multiplies all three channels - half an outdoor night went pure
        // black. See colorgrade.fx for the numbers.
        float preLum = dot(col, LUMA);
        float postLum = saturate((preLum - 0.5) * Contrast + 0.5);
        postLum = lerp(preLum, postLum, smoothstep(0.0, 0.25, preLum));
        col *= postLum / max(preLum, 1e-4);

        float lum = dot(col, LUMA);
        col = lerp(lum.xxx, col, Saturation);        // saturation

        col = saturate(col);

        float3 outc = lerp(src.rgb, col, saturate(Strength));

        // The LUT is the LAST artistic step and sits AFTER the parametric grade, so the sliders keep
        // meaning exactly what they meant and the LUT is a look laid over the result rather than a
        // replacement for them. It stays ahead of the blue-light filter, which is eye comfort rather
        // than art and has to survive whatever look is chosen.
        if (LutAmount > 0.0)
            outc = lerp(outc, SampleLut(outc), saturate(LutAmount));

        outc *= float3(1.0 + BlueLight * 0.06, 1.0 - BlueLight * 0.06, 1.0 - BlueLight * 0.28);
        outc += DitherLsb(input.UV) * (1.0 / 255.0);   // mirrors colorgrade.fx exactly

        // The 8-bit render target that used to sit between the grade pass and the
        // finishing pass, reproduced exactly so the fused chain stays bit-identical.
        graded = floor(saturate(outc) * 255.0 + 0.5) / 255.0;
    }

    // --- finishing.fx FinishPS vignette half, verbatim (CA handled by fallback) ---
    float2 dir = input.UV - 0.5;
    float dist = length(dir);
    float vig = (VignetteStrength + NightAmt * 0.12) * smoothstep(0.35, 0.80, dist);
    graded *= (1.0 - vig);
    graded *= SkyLightTint;
    graded += DitherLsb(input.UV) * (1.0 / 255.0) * saturate(vig * 40.0);   // mirrors finishing.fx exactly

    return float4(graded, src.a);
}

technique Tail { pass P0 { PixelShader = compile PS_SHADERMODEL TailPS(); } }
