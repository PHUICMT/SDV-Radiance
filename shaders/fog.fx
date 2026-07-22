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

float Time;          // seconds
float Speed;         // drift speed
float Scale;         // mist feature size
float Density;       // overall opacity (0..1)
float3 FogColor;     // fog tint
float TopBias;       // extra fog toward the top of the screen (0..1)
float Patchiness;    // 0 = classic even blanket · 1 = sparse drifting wisps with clear gaps
float Coverage;      // 0..1 how MUCH of the frame the wisps occupy (amount, not opacity)
float2 WorldOffset;  // world-anchor

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

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

float4 FogPS(PixelInput input) : SV_TARGET
{
    float2 p = (input.UV + WorldOffset) * Scale + float2(Time * Speed, Time * Speed * 0.2);
    float n = fbm(p);

    // fbm covers the whole frame (mean ~0.5), which reads as an even film. Patchiness
    // carves it into separate drifting wisps: only the denser cores survive, the rest
    // clears out completely. Coverage moves the survival threshold — how much of the
    // frame gets wisps — independently of Density (their opacity).
    float lo = 0.8 - 0.45 * saturate(Coverage);
    float wisps = smoothstep(lo, lo + 0.3, n) * 0.9;
    n = lerp(n, wisps, saturate(Patchiness));

    // Slightly more mist toward the top of the screen.
    float grad = 1.0 + TopBias * (1.0 - input.UV.y);
    float f = saturate(n * Density * grad);

    float4 c = tex2D(SourceSampler, input.UV);
    return float4(lerp(c.rgb, FogColor, f), c.a);
}

technique Fog { pass P0 { PixelShader = compile PS_SHADERMODEL FogPS(); } }
