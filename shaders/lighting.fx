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

#define MAX_LIGHTS 48

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
float  Presence;              // 0..1 whole-pass presence fade (see the tail of LightingPS)
int    LightCount;            // how many entries of the arrays are live; the loop stops there
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
// THE STEP COUNT IS NOT WHERE THE COST IS. Measured, twice, so nobody spends the afternoon
// on it again: the "light shadows" setting prices at 0.19 ms, more than every other effect
// combined, and it looks exactly like a march cost - fourteen texture fetches per light per
// pixel, for every light reaching that pixel. It is not. Sizing the loop by the distance in
// mask tiles (three or four steps for the common close light) changed nothing; halving it to
// a flat seven changed nothing either. Both readings came back inside the noise on three
// scenes. The target here is ps_4_0_level_9_1, where the compiler flattens this loop whatever
// it is written as, and the samples are hitting a tiny per-tile texture that lives in cache.
// The money is elsewhere - look at what turning the setting OFF skips on the C# side.
float ShadowFactor(float2 uv, float2 lightUv)
{
    if (ShadowStrength <= 0.001)
        return 1.0;

    const int STEPS = 14;
    float2 delta = (lightUv - uv) / STEPS;
    float occ = 0.0;
    // A real loop with tex2Dlod (no gradients inside dynamic flow), so the LIGHT loop outside
    // can stay unrolled at forty-eight without running out of temporaries. The other way round -
    // light loop dynamic, march unrolled - compiled, and cost 3 ms: every array read became a
    // dynamically indexed uniform, which the driver spills. Constant light indices are the
    // cheap half; the march is the half that can afford a counter.
    [loop]
    for (int s = 2; s <= STEPS; s++)   // start past the pixel to avoid self-shadow
    {
        float2 p = uv + delta * s;
        float nearLight = smoothstep(0.0, 0.07, distance(p, lightUv)); // 0 at the light
        occ = max(occ, tex2Dlod(OccluderSampler, float4(OccUV(p), 0.0, 0.0)).r * nearLight);
    }
    return 1.0 - occ * ShadowStrength;
}

float4 LightingPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 src = tex2D(SourceSampler, uv);

    float3 accum = AmbientColor;

    // Unrolled, so every LightPos[i] / LightData[i] read is a constant register: as a real loop
    // the same code priced at 2 to 3 ms in every scene measured (dynamically indexed uniforms
    // spill), and the classic pass runs whenever flood GI is switched off. Unrolling forty-eight
    // with the march unrolled too overflowed the temporaries (X4505), so the march is the loop.
    // LightCount still bounds the work: slots past it hold radius 0 and leave in one compare.
    [unroll]
    for (int i = 0; i < MAX_LIGHTS; i++)
    {
        if (i >= LightCount)
            break;
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
    // Whole-pass presence: AmbientColor and ShadowStrength already ride the fade, but the light
    // POOLS (LightData) never did, so this stage kept painting them at full strength all the way
    // down and then vanished in one frame when it left the stage list. Fade the finished result
    // back to the untouched pixel so every term - pools included - reaches zero before that.
    lit = lerp(src.rgb, lit, Presence);
    return float4(lit, src.a);
}

technique Lighting { pass P0 { PixelShader = compile PS_SHADERMODEL LightingPS(); } }
