//=============================================================================
// fog.fx  —  SDV-Radiance
// Screen-space volumetric fog: drifting fbm mist (world-anchored) blended
// toward a fog colour, with a gentle vertical bias.
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

// Baked tileable fbm from C# (CPU-precision). Wrap addressing = seamless infinite
// coverage; no runtime hash → no GPU sin() precision seams, identical on every card.
texture NoiseTexture;
sampler2D NoiseSampler = sampler_state
{
    Texture = <NoiseTexture>;
    AddressU = Wrap;
    AddressV = Wrap;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
};

float Time;          // seconds
float Speed;         // drift speed
float Scale;         // mist feature size
float Density;       // overall opacity (0..1)
float3 FogColor;     // fog tint
float TopBias;       // extra fog toward the top of the screen (0..1)
float Patchiness;    // 0 = classic even blanket · 1 = sparse drifting wisps with clear gaps
float Coverage;      // 0..1 how MUCH of the frame the wisps occupy (amount, not opacity)
float2 WorldOffset;  // world-anchor
float2 ScreenPixels; // viewport size in pixels, for the dither's pixel grid

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

static const float2x2 M = float2x2(0.80, 0.60, -0.60, 0.80);

// Two drifting layers of the baked fbm, second rotated + differently scaled so the
// pattern evolves organically instead of sliding as one rigid sheet.
float fbm(float2 p)
{
    float n1 = tex2D(NoiseSampler, p * 0.11 + float2(Time * Speed, Time * Speed * 0.2)).r;
    float n2 = tex2D(NoiseSampler, mul(M, p) * 0.23 + float2(-Time * Speed * 0.6, Time * Speed * 0.13) + 0.37).r;
    return n1 * 0.65 + n2 * 0.35;
}


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

float4 FogPS(PixelInput input) : SV_TARGET
{
    float2 p = (input.UV + WorldOffset) * Scale;
    float n = fbm(p);

    // fbm covers the whole frame (mean ~0.5), which reads as an even film. Patchiness
    // carves it into separate drifting wisps: only the denser cores survive, the rest
    // clears out completely. Coverage moves the survival threshold — how much of the
    // frame gets wisps — independently of Density (their opacity). Thresholds are
    // calibrated to the two-layer blend's narrower value range (~0.25..0.75).
    float lo = 0.70 - 0.5 * saturate(Coverage);
    float wisps = smoothstep(lo, lo + 0.22, n) * 0.9;
    n = lerp(n, wisps, saturate(Patchiness));

    // Slightly more mist toward the top of the screen.
    float grad = 1.0 + TopBias * (1.0 - input.UV.y);
    float f = saturate(n * Density * grad);

    float4 c = tex2D(SourceSampler, input.UV);
    float3 fogged = lerp(c.rgb, FogColor, f);
    // Gated by the fog's own contribution, so a clear pixel stays the exact source pixel.
    fogged += DitherLsb(input.UV) * (1.0 / 255.0) * saturate(f * 12.0);
    return float4(fogged, c.a);
}

technique Fog { pass P0 { PixelShader = compile PS_SHADERMODEL FogPS(); } }
