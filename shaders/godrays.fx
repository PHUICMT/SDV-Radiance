//=============================================================================
// godrays.fx  —  SDV-Radiance
// Screen-space crepuscular rays (light shafts): bright-pass, then a radial
// blur marching toward the light position, composited additively.
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

texture RaysTexture;
sampler2D RaysSampler = sampler_state
{
    Texture = <RaysTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float Threshold;    // brightness cutoff for the light source
float2 LightPos;    // light position in screen UV (may be off-screen)
float LightRadius;  // UV radius around LightPos within which bright pixels may streak
float Aspect;       // viewport w/h, to make the radius circular in UV space
float Density;      // how far along the ray to march (0..1)
float Decay;        // per-step falloff
float Weight;       // per-step weight
float Intensity;    // final additive strength

// Player silhouette exclusion: sprites are NOT light emitters — without this a bright
// face/hair standing near a lamp streaked rays of its own.
float4 PlayerRect;           // silhouette bounds in screen UV (x0,y0,x1,y1)
texture PlayerMaskTexture;
sampler2D PlayerMaskSampler = sampler_state
{
    Texture = <PlayerMaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// Flood lightmap gate: when the flood GI system is on, only pixels that are actually
// LIT (per the lightmap) may emit rays — a bright sprite in a dark corner cannot.
float FloodGate;             // 0 = off, 1 = gate by the flood lightmap
float2 FloodTilesPerScreen;
float2 FloodWorldTileOffset;
float2 FloodMapOrigin;
float2 FloodMapSize;
texture FloodMapTexture;
sampler2D FloodMapSampler = sampler_state
{
    Texture = <FloodMapTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);
static const int SAMPLES = 16;

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// Keep only bright areas NEAR the real light source — these are what streak. Gating
// to a disk around LightPos stops distant bright scenery (flowers, pale hair, snow)
// from smearing rays toward the light.
float4 BrightPS(PixelInput input) : SV_TARGET
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    float lum = dot(c, LUMA);
    float mask = smoothstep(Threshold, Threshold + 0.1, lum);

    float2 d = input.UV - LightPos;
    d.x *= Aspect;                                   // circular in pixel space
    float within = 1.0 - smoothstep(LightRadius * 0.65, LightRadius, length(d));
    mask *= within;

    // The player's own pixels never emit rays (bright face/hair beside a lamp).
    float2 pmSpan = max(PlayerRect.zw - PlayerRect.xy, float2(1e-4, 1e-4));
    float2 pmuv = (input.UV - PlayerRect.xy) / pmSpan;
    float pmIn = step(0.0, pmuv.x) * step(pmuv.x, 1.0) * step(0.0, pmuv.y) * step(pmuv.y, 1.0);
    mask *= 1.0 - step(0.02, tex2D(PlayerMaskSampler, saturate(pmuv)).a) * pmIn;

    // Only genuinely LIT pixels emit (flood lightmap gate) — daytime open ground is fully
    // lit so nothing changes; at night rays come from lamp glow zones only.
    if (FloodGate > 0.5)
    {
        float2 wt = input.UV * FloodTilesPerScreen + FloodWorldTileOffset;
        float3 fl = tex2D(FloodMapSampler, (wt - FloodMapOrigin) / FloodMapSize).rgb * 2.0;
        float flum = max(fl.r, max(fl.g, fl.b));
        mask *= saturate((flum - 0.45) * 2.2);
    }

    return float4(c * mask, 1.0);
}

// Radial blur toward LightPos over the bright buffer.
float4 RaysPS(PixelInput input) : SV_TARGET
{
    float2 delta = (input.UV - LightPos) * (Density / SAMPLES);
    float2 uv = input.UV;
    float3 col = tex2D(SourceSampler, uv).rgb;
    float illum = 1.0;

    [unroll]
    for (int i = 0; i < SAMPLES; i++)
    {
        uv -= delta;
        illum *= Decay;
        col += tex2D(SourceSampler, uv).rgb * illum * Weight;
    }
    return float4(col / SAMPLES, 1.0);
}

// scene + rays * Intensity.
float4 CompositePS(PixelInput input) : SV_TARGET
{
    float4 scene = tex2D(SourceSampler, input.UV);
    float3 rays = tex2D(RaysSampler, input.UV).rgb;
    return float4(scene.rgb + rays * Intensity, scene.a);
}

technique Bright    { pass P0 { PixelShader = compile PS_SHADERMODEL BrightPS(); } }
technique Rays      { pass P0 { PixelShader = compile PS_SHADERMODEL RaysPS(); } }
technique Composite { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
