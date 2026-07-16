//=============================================================================
// lighting.fx  —  SDV-Radiance Phase 5 (dynamic 2D lighting)
// A parallel lighting pass done in screen space. The scene is multiplied by a
// per-pixel light accumulation: a dark ambient base plus a smooth radial pool
// for every real in-world light source (Game1.currentLightSources), so dark
// areas read dark and lit areas read bright — with soft falloff the vanilla
// 1/4-res lightmap can't give. Optional hard-edge occluder shadows ray-march a
// screen-space occluder mask toward each light.
//
// The C# side gates AmbientColor by context so we never double-darken what the
// game's own lightmap already handles (mainly: we own interiors, where vanilla
// draws no lightmap at all and everything is flat-bright).
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

#define MAX_LIGHTS 16

sampler2D SourceSampler : register(s0);

// Screen-space occluder mask (r = 1 where a tall/solid tile blocks light).
// Point-sampled so the tile grid stays crisp. Only consulted when ShadowStrength > 0.
texture OccluderTexture;
sampler2D OccluderSampler = sampler_state
{
    Texture = <OccluderTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float3 AmbientColor;          // per-pixel multiplier for unlit areas (1,1,1 = no darkening)
float  Aspect;                // width / height, so light pools are round not oval
int    LightCount;            // number of active lights (<= MAX_LIGHTS)
float2 LightPos[MAX_LIGHTS];  // light centre in screen UV (0..1)
float4 LightData[MAX_LIGHTS]; // xyz = light colour * boost, w = radius (UV, height units)
float  ShadowStrength;        // 0 = no shadows; 1 = full hard occluder shadows
float  Overbright;            // max light accumulation (>1 allows glow near lamps)

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// March from the pixel toward the light; if any step hits an occluder, the
// pixel is in shadow. Cheap fixed step count — good enough for soft-ish 2D.
float ShadowFactor(float2 uv, float2 lightUv)
{
    if (ShadowStrength <= 0.001)
        return 1.0;

    const int STEPS = 12;
    float2 delta = (lightUv - uv) / STEPS;
    float2 p = uv;
    float occ = 0.0;
    [unroll]
    for (int s = 0; s < STEPS; s++)
    {
        p += delta;
        occ += tex2D(OccluderSampler, p).r;
    }
    // Any hit along the ray darkens; scale by ShadowStrength so it's tunable.
    float shadowed = saturate(occ) * ShadowStrength;
    return 1.0 - shadowed;
}

float4 LightingPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 src = tex2D(SourceSampler, uv);

    float3 accum = AmbientColor;

    [unroll]
    for (int i = 0; i < MAX_LIGHTS; i++)
    {
        float radius = LightData[i].w;
        if (radius <= 0.0)
            continue;

        float2 d = uv - LightPos[i];
        d.x *= Aspect;                       // round pools on a wide screen
        float dist = length(d);

        float atten = 1.0 - smoothstep(0.0, radius, dist);
        atten *= atten;                      // softer, more natural rolloff
        if (atten <= 0.001)
            continue;

        atten *= ShadowFactor(uv, LightPos[i]);
        accum += LightData[i].xyz * atten;
    }

    accum = min(accum, Overbright.xxx);
    float3 lit = src.rgb * accum;
    return float4(saturate(lit), src.a);
}

technique Lighting { pass P0 { PixelShader = compile PS_SHADERMODEL LightingPS(); } }
