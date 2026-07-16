//=============================================================================
// cloudshadow.fx  —  SDV-Radiance Phase 3
// Procedural drifting cloud shadows (fbm value noise), world-anchored so they
// slide across the map rather than sticking to the screen.
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

float Time;          // seconds, for drift
float Speed;         // drift speed
float Scale;         // cloud size (bigger = smaller/denser clouds)
float Opacity;       // how dark the shadows get (0..1)
float Coverage;      // fraction of area shadowed (0..1)
float2 WorldOffset;  // viewport origin (world-anchor), pre-scaled on the CPU

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

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

// Rotate + scale each octave so the value-noise lattice doesn't read as a
// square grid — gives organic, cloud-like shapes instead of blocks.
static const float2x2 M = float2x2(0.80, 0.60, -0.60, 0.80);

float fbm(float2 p)
{
    float v = 0.0;
    float amp = 0.5;
    [unroll]
    for (int i = 0; i < 5; i++)
    {
        v += amp * vnoise(p);
        p = mul(M, p) * 2.0;
        amp *= 0.5;
    }
    return v;
}

float4 CloudPS(PixelInput input) : SV_TARGET
{
    float2 drift = float2(Time * Speed, Time * Speed * 0.35);
    float2 p = (input.UV + WorldOffset) * Scale + drift;

    // Two-level domain warp: bend the sample coords by fbm (twice) so the
    // clouds get fluffy, swirly, non-repeating shapes with no straight/faceted
    // edges (like Minecraft shader-mod clouds) instead of plain noise blobs.
    float2 warp1 = float2(fbm(p + float2(1.7, 9.2)), fbm(p + float2(8.3, 2.8)));
    float2 warp2 = float2(fbm(p + 3.0 * warp1 + float2(4.1, 1.9)),
                          fbm(p + 3.0 * warp1 + float2(2.3, 7.4)));
    float n = fbm(p + 2.4 * warp2);

    // Wide, very soft threshold so cloud edges feather out (no hard contour).
    float edge = 1.0 - Coverage;
    float cloud = smoothstep(edge - 0.4, edge + 0.4, n);

    float4 c = tex2D(SourceSampler, input.UV);

    // Bright / emissive areas (fire, lamps, highlights) resist the cloud shadow —
    // a passing cloud shouldn't dim a light source.
    float lum = dot(c.rgb, LUMA);
    float protect = smoothstep(0.62, 0.92, lum);
    float shade = 1.0 - cloud * Opacity * (1.0 - protect);

    return float4(c.rgb * shade, c.a);
}

technique CloudShadow { pass P0 { PixelShader = compile PS_SHADERMODEL CloudPS(); } }
