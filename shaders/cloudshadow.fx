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
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + float2(1.0, 0.0));
    float c = hash(i + float2(0.0, 1.0));
    float d = hash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float fbm(float2 p)
{
    float v = 0.0;
    float amp = 0.5;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        v += amp * vnoise(p);
        p *= 2.0;
        amp *= 0.5;
    }
    return v;
}

float4 CloudPS(PixelInput input) : SV_TARGET
{
    float2 drift = float2(Time * Speed, Time * Speed * 0.35);
    float2 p = (input.UV + WorldOffset) * Scale + drift;
    float n = fbm(p);

    float edge = 1.0 - Coverage;
    float cloud = smoothstep(edge - 0.18, edge + 0.18, n);
    float shade = 1.0 - cloud * Opacity;

    float4 c = tex2D(SourceSampler, input.UV);
    return float4(c.rgb * shade, c.a);
}

technique CloudShadow { pass P0 { PixelShader = compile PS_SHADERMODEL CloudPS(); } }
