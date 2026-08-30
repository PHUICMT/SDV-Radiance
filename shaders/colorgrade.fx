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
float BlueLight;    // 0..1 eye-comfort warm shift, applied AFTER the grade blend (grade-independent)
float2 ScreenPixels; // viewport size in pixels, for the dither's pixel grid

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

    // Contrast on LUMINANCE, with the colour carried along at its own ratio.
    //
    // Per-channel, this was `(col - 0.5) * Contrast + 0.5`, which pushes each channel away from
    // mid-grey by itself and therefore pulls them apart from EACH OTHER: a warm pixel at
    // (0.80, 0.50, 0.30) comes out (0.845, 0.500, 0.270) at a contrast of 1.15, so its
    // saturation has risen even though the saturation control was never touched. Measured in the
    // saloon, the grade alone took HSV saturation from 0.798 to 0.956 while the Saturation
    // setting was 1.05 - a twenty percent lift asked for by a five percent control, with the
    // other fifteen coming from here. Working on luminance and scaling the channels together
    // gives the same tonal contrast and leaves the hue and the saturation exactly where the
    // artist's own controls put them.
    float preLum = dot(col, LUMA);
    float postLum = saturate((preLum - 0.5) * Contrast + 0.5);
    // A pivot contrast line crosses zero: at 1.15 everything below a luminance of 0.065 lands
    // AT zero, exactly. Per channel that was survivable - a dark blue kept its blue while red
    // and green clipped - but on luminance the ratio below multiplies ALL THREE channels by
    // that zero, and half of an outdoor night sits under the cut-off. Measured in the forest
    // at 21:00: vanilla 0.3% pure black, this line 60.5%, and it bisected to exactly this
    // commit's rework. Film curves solve it with a TOE: fade the contrast back to identity
    // through the deepest shadows, so darkness compresses smoothly and never hits the floor.
    postLum = lerp(preLum, postLum, smoothstep(0.0, 0.25, preLum));
    col *= postLum / max(preLum, 1e-4);

    float lum = dot(col, LUMA);
    col = lerp(lum.xxx, col, Saturation);        // saturation

    col = saturate(col);

    // Blend back toward the original by (1 - Strength).
    float3 outc = lerp(src.rgb, col, saturate(Strength));

    // The LUT is the LAST artistic step and sits AFTER the parametric grade, so the sliders keep
    // meaning exactly what they meant and the LUT is a look laid over the result rather than a
    // replacement for them. It stays ahead of the blue-light filter, which is eye comfort rather
    // than art and has to survive whatever look is chosen.
    if (LutAmount > 0.0)
        outc = lerp(outc, SampleLut(outc), saturate(LutAmount));

    // Blue-light / eye-comfort filter: cut blue and lift red a touch. Applied AFTER the
    // grade blend so it is independent of the artistic controls (works with grading off).
    outc *= float3(1.0 + BlueLight * 0.06, 1.0 - BlueLight * 0.06, 1.0 - BlueLight * 0.28);
    outc += DitherLsb(input.UV) * (1.0 / 255.0);
    return float4(saturate(outc), src.a);
}

technique ColorGrade { pass P0 { PixelShader = compile PS_SHADERMODEL GradePS(); } }
