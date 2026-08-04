//=============================================================================
// lighting.fx  —  SDV-Radiance (dynamic 2D lighting)
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

// Per-tile occluder mask (r = 1 where a tall/solid tile blocks light), aligned
// to the viewport like the water mask. LINEAR-sampled so the tile grid melts
// into soft penumbra edges. Only consulted when ShadowStrength > 0.
texture OccluderTexture;
sampler2D OccluderSampler = sampler_state
{
    Texture = <OccluderTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float3 AmbientColor;          // per-pixel multiplier for unlit areas (1,1,1 = no darkening)
float  Aspect;                // width / height, so light pools are round not oval
float2 LightPos[MAX_LIGHTS];  // light centre in screen UV (0..1)
float4 LightData[MAX_LIGHTS]; // xyz = light colour * boost, w = radius (UV, height units)
float  ShadowStrength;        // 0 = no shadows; 1 = full occluder shadows
float  Overbright;            // max light accumulation (>1 allows glow near lamps)
float2 OccTilesPerScreen;     // world tiles spanning the buffer (w/64, h/64)
float2 OccWorldTileOffset;    // viewport origin in world tiles (continuous)
float2 OccMaskSize;           // occluder mask size in texels (tiles)

// Map a screen-UV point to the occluder mask's UV (continuous, so LINEAR
// filtering gives smooth gradients across the tile grid).
float2 OccUV(float2 p)
{
    float2 worldTile = p * OccTilesPerScreen + OccWorldTileOffset;
    float2 startTile = floor(OccWorldTileOffset);
    return (worldTile - startTile) / OccMaskSize;
}

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// March from the pixel toward the light through the occluder mask. The closest
// occluder along the ray (max) sets how shadowed the pixel is. Samples very
// near the light are faded out so a light mounted ON an occluder tile (sconce,
// window) doesn't shadow its own glow.
float ShadowFactor(float2 uv, float2 lightUv)
{
    if (ShadowStrength <= 0.001)
        return 1.0;

    const int STEPS = 14;
    float2 delta = (lightUv - uv) / STEPS;
    float occ = 0.0;
    [unroll]
    for (int s = 2; s <= STEPS; s++)   // start past the pixel to avoid self-shadow
    {
        float2 p = uv + delta * s;
        float nearLight = smoothstep(0.0, 0.07, distance(p, lightUv)); // 0 at the light
        occ = max(occ, tex2D(OccluderSampler, OccUV(p)).r * nearLight);
    }
    return 1.0 - occ * ShadowStrength;
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

    // Light only LIFTS a pixel toward full brightness (1.0), never past it: outdoors
    // ambient is already 1 so pools can't out-glow daylight, and indoors a lit spot
    // just reaches normal brightness instead of blowing out. (Overbright kept as a
    // small optional headroom for lamp cores feeding bloom.)
    accum = min(accum, max(1.0, Overbright));
    float3 lit = src.rgb * saturate(accum);
    return float4(lit, src.a);
}

technique Lighting { pass P0 { PixelShader = compile PS_SHADERMODEL LightingPS(); } }
