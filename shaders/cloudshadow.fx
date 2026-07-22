//=============================================================================
// cloudshadow.fx  —  SDV-Radiance
// Soft drifting cloud shadows. The cloud density is generated into a low-res
// buffer, Gaussian-blurred, then composited onto the scene as a gentle multiply.
// The blur is what gives real, feathered penumbra edges instead of the faceted
// hard contour you get from thresholding noise at full resolution.
// World-anchored so the shadows slide across the map, not the screen.
// Target: MonoGame OpenGL (Shader Model 3.0), used as a SpriteBatch effect.
//
// NOISE: like the fog, the density field samples a tileable fbm texture baked
// once on the CPU at full precision — GPU sin()-hash noise has no precision
// guarantee and produced hard axis-aligned seams / faceted blobs (worse on
// some vendors). Wrap addressing = seamless everywhere; also far cheaper than
// the old 6-octave in-shader fbm called six times per pixel. Time stays
// wrapped on the CPU so the drift offset itself never loses float precision.
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

texture ShadowTexture;      // blurred cloud-density mask (for the composite pass)
sampler2D ShadowSampler = sampler_state
{
    Texture = <ShadowTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float Time;          // seconds, WRAPPED into a bounded range by the CPU (precision safety)
float Speed;         // drift speed
float Scale;         // cloud size (bigger = smaller/denser clouds)
float Opacity;       // how dark the shadows get (0..1)
float Coverage;      // fraction of area shadowed (0..1)
float2 WorldOffset;  // viewport origin (world-anchor), pre-scaled on the CPU
float2 TexelSize;    // blur step (1/width, 0) or (0, 1/height)
float LightProtect;  // 0 by day .. 1 at night: only then do near-white cores resist the shadow
float Count;         // 0..1 how many SEPARATE cloud banks are on screen (cluster frequency)

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);
static const int TAPS = 5;
static const float W[5] = { 0.227027, 0.194595, 0.121622, 0.054054, 0.016216 };

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// Baked tileable fbm from C# (shared with the fog).
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

static const float2x2 M = float2x2(0.80, 0.60, -0.60, 0.80);

// Two rotated layers of the baked fbm — organic, seamless, cheap.
float fbm(float2 p)
{
    float n1 = tex2D(NoiseSampler, p * 0.101).r;
    float n2 = tex2D(NoiseSampler, mul(M, p) * 0.211 + 0.37).r;
    return n1 * 0.65 + n2 * 0.35;
}

// --- Pass 1: cloud-density mask (rendered at low res) ---------------------
float4 MaskPS(PixelInput input) : SV_TARGET
{
    float2 drift = float2(Time * Speed, Time * Speed * 0.35);
    float2 p = (input.UV + WorldOffset) * Scale + drift;

    // Two-level domain warp for fluffy, swirly, non-repeating shapes.
    float2 warp1 = float2(fbm(p + float2(1.7, 9.2)), fbm(p + float2(8.3, 2.8)));
    float2 warp2 = float2(fbm(p + 3.5 * warp1 + float2(4.1, 1.9)),
                          fbm(p + 3.5 * warp1 + float2(2.3, 7.4)));
    float n = fbm(p + 3.5 * warp2);

    // Blend a second, finer octave at a different scale so no single density contour ever
    // runs straight — the sustainable fix for the "hard straight edge" is to never let the
    // threshold cross one clean ridge line.
    float detail = fbm(p * 2.37 + float2(11.3, 5.9));
    n = n * 0.68 + detail * 0.32;

    // The texture-blend field has a narrower value range than the old in-shader fbm —
    // left as-is the threshold ramp spans most of it, so EVERYTHING goes half-cloudy
    // (reads as global dimming). Re-expand the contrast so clear sky and cloud cores
    // separate again.
    n = saturate((n - 0.5) * 2.4 + 0.5);

    // CLUSTERING: a low-frequency layer carves the field into separate cloud BANKS
    // with genuinely clear sky between them. The frequency is normalized by Scale so
    // Count means "how many banks fit on screen" regardless of the size slider:
    // Count 0 ≈ 1-2 big banks, Count 1 ≈ 6+ small ones. (First version multiplied
    // the tiny factor into the already-scaled p — the whole screen fell inside ONE
    // cluster cell, so the slider only slid the pattern instead of adding banks.)
    float clusterFreq = lerp(0.35, 1.6, saturate(Count)) / max(Scale, 0.001);
    float cm = tex2D(NoiseSampler, p * clusterFreq + 0.61).r;
    // Genuinely clear spells between banks are intentional (real skies have them);
    // they drift through in a few in-game hours.
    n *= smoothstep(0.42, 0.60, cm);

    // Coverage must read as AREA (more/fewer cloud patches), not a global dimmer. A narrow ramp
    // keeps genuinely clear sky (cloud=0) next to genuinely shadowed patches (cloud=1); the
    // separable Gaussian blur below is what softens the edges (fluffy penumbra), so this can
    // stay tight without a hard contour. Raising Coverage lowers the threshold → more area.
    float edge = 1.0 - Coverage;
    float cloud = smoothstep(edge - 0.10, edge + 0.10, n);
    // Roll the low end smoothly toward 0 so open sky reads clear (no faint all-over tint) WITHOUT
    // a hard clip — a sharp cutoff makes the low tail pop on/off at the half-res grid as the mask
    // drifts, which reads as flicker. A gamma curve clears the baseline yet stays continuous, so
    // motion is smooth; cloud cores stay strong (raise Opacity if you want them darker).
    cloud = cloud * cloud;
    return float4(cloud, cloud, cloud, 1.0);
}

// --- Pass 2/3: separable Gaussian blur (widened for soft penumbra) --------
float4 BlurHPS(PixelInput input) : SV_TARGET
{
    float s = tex2D(SourceSampler, input.UV).r * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 o = float2(TexelSize.x * i * 3.5, 0.0);
        s += tex2D(SourceSampler, input.UV + o).r * W[i];
        s += tex2D(SourceSampler, input.UV - o).r * W[i];
    }
    return float4(s, s, s, 1.0);
}

float4 BlurVPS(PixelInput input) : SV_TARGET
{
    float s = tex2D(SourceSampler, input.UV).r * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 o = float2(0.0, TexelSize.y * i * 3.5);
        s += tex2D(SourceSampler, input.UV + o).r * W[i];
        s += tex2D(SourceSampler, input.UV - o).r * W[i];
    }
    return float4(s, s, s, 1.0);
}

// --- Pass 4: composite the soft shadow onto the scene ---------------------
float4 CompositePS(PixelInput input) : SV_TARGET
{
    float4 c = tex2D(SourceSampler, input.UV);
    float cloud = tex2D(ShadowSampler, input.UV).r;

    // Near-white cores resist the shadow ONLY AT NIGHT (lamp/fire glow shouldn't dim
    // under a moon cloud). By DAY the sun is the light source: a cloud must shade
    // everything, including white art — eyes, flowers, white walls — or they punch
    // holes in the shadow (community report). LightProtect is the night factor.
    float lum = dot(c.rgb, LUMA);
    float protect = smoothstep(0.955, 0.995, lum) * saturate(LightProtect);
    float shade = 1.0 - cloud * Opacity * (1.0 - protect);

    return float4(c.rgb * shade, c.a);
}

technique Mask      { pass P0 { PixelShader = compile PS_SHADERMODEL MaskPS(); } }
technique BlurH     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurHPS(); } }
technique BlurV     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurVPS(); } }
technique Composite { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
