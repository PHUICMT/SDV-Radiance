//=============================================================================
// cloudshadow.fx  —  SDV-Radiance Phase 3 (Phase 4 overhaul)
// Soft drifting cloud shadows. The cloud density is generated into a low-res
// buffer, Gaussian-blurred, then composited onto the scene as a gentle multiply.
// The blur is what gives real, feathered penumbra edges instead of the faceted
// hard contour you get from thresholding noise at full resolution.
// World-anchored so the shadows slide across the map, not the screen.
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

texture ShadowTexture;      // blurred cloud-density mask (for the composite pass)
sampler2D ShadowSampler = sampler_state
{
    Texture = <ShadowTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float Time;          // seconds, for drift
float Speed;         // drift speed
float Scale;         // cloud size (bigger = smaller/denser clouds)
float Opacity;       // how dark the shadows get (0..1)
float Coverage;      // fraction of area shadowed (0..1)
float2 WorldOffset;  // viewport origin (world-anchor), pre-scaled on the CPU
float2 TexelSize;    // blur step (1/width, 0) or (0, 1/height)

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);
static const int TAPS = 5;
static const float W[5] = { 0.227027, 0.194595, 0.121622, 0.054054, 0.016216 };

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float hash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

float vnoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0); // quintic smootherstep (C2)
    float a = hash(i);
    float b = hash(i + float2(1.0, 0.0));
    float c = hash(i + float2(0.0, 1.0));
    float d = hash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Rotate each octave so the value-noise lattice doesn't read as a square grid.
static const float2x2 M = float2x2(0.80, 0.60, -0.60, 0.80);

float fbm(float2 p)
{
    float v = 0.0;
    float amp = 0.5;
    // Non-integer lacunarity + a per-octave shift break the lattice so shapes
    // read as curved/organic rather than faceted diagonals.
    [unroll]
    for (int i = 0; i < 6; i++)
    {
        v += amp * vnoise(p);
        p = mul(M, p) * 2.02 + float2(37.0, 17.0);
        amp *= 0.5;
    }
    return v;
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

    float edge = 1.0 - Coverage;
    float cloud = smoothstep(edge - 0.35, edge + 0.35, n);
    return float4(cloud, cloud, cloud, 1.0);
}

// --- Pass 2/3: separable Gaussian blur (widened for soft penumbra) --------
float4 BlurHPS(PixelInput input) : SV_TARGET
{
    float s = tex2D(SourceSampler, input.UV).r * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 o = float2(TexelSize.x * i * 2.0, 0.0);
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
        float2 o = float2(0.0, TexelSize.y * i * 2.0);
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

    // Only near-white emissive cores (fire, lamps) resist the shadow — a passing
    // cloud shouldn't dim a light source. Kept high so merely-bright surfaces
    // (beach sand, snow) still receive shadow instead of being wrongly protected.
    float lum = dot(c.rgb, LUMA);
    float protect = smoothstep(0.86, 0.99, lum);
    float shade = 1.0 - cloud * Opacity * (1.0 - protect);

    return float4(c.rgb * shade, c.a);
}

technique Mask      { pass P0 { PixelShader = compile PS_SHADERMODEL MaskPS(); } }
technique BlurH     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurHPS(); } }
technique BlurV     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurVPS(); } }
technique Composite { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
