//=============================================================================
// lensdrops.fx  -  SDV-Radiance
// Raindrops on the glass that bend the picture behind them. Each drop is a small
// dome of water, and a dome of water is a lens: what it shows is the scene behind
// it, gathered from round its centre, flipped and shrunk, with the rim gone dark
// where the surface turns edge-on and a hard highlight where the sky catches it.
//
// The drop atlas carries the dome, not a picture of a drop: RG the surface normal
// across the glass (bias 0.5), B the water's thickness, A how much of the texel the
// drop covers. The vertex colour carries the drop: R its radius on screen (pixels
// / 128), A how much of it is there (its fade).
// Target: MonoGame OpenGL (Shader Model 3.0).
//=============================================================================

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler2D DropSampler : register(s0);
texture SceneTexture;
sampler2D SceneSampler = sampler_state
{
    Texture = <SceneTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

float4x4 MatrixTransform;
float2 ScreenSize;        // the frame the drops are laid on, in pixels
float Inversion;          // how far across the drop the scene is gathered from (1.8 a bead of rain)
float Highlight;          // the sky's glint on the dome

struct VertexInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
    float2 ScreenUV : TEXCOORD1;
};

PixelInput LensDropsVS(VertexInput input)
{
    PixelInput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color = input.Color;
    output.UV = input.UV;
    output.ScreenUV = input.Position.xy / ScreenSize;
    return output;
}

float4 LensDropsPS(PixelInput input) : SV_TARGET
{
    float4 dome = tex2D(DropSampler, input.UV);
    float coverage = dome.a * input.Color.a;
    clip(coverage - 0.004);
    float2 normal = dome.rg * 2.0 - 1.0;
    float thickness = dome.b;
    float radiusPixels = input.Color.r * 128.0;

    // A ball of water flips what is behind it: the point a little left of centre sees the
    // scene a little right of it, further out the further out it sits.
    float2 gather = -normal * radiusPixels * Inversion / ScreenSize;
    float2 sceneUV = input.ScreenUV + gather;
    // Out of focus: the glass is right at the eye, so what the drop holds is soft.
    float2 spread = 1.5 / ScreenSize;
    float3 scene = tex2D(SceneSampler, sceneUV).rgb * 0.4
                 + tex2D(SceneSampler, sceneUV + float2(spread.x, 0)).rgb * 0.15
                 + tex2D(SceneSampler, sceneUV - float2(spread.x, 0)).rgb * 0.15
                 + tex2D(SceneSampler, sceneUV + float2(0, spread.y)).rgb * 0.15
                 + tex2D(SceneSampler, sceneUV - float2(0, spread.y)).rgb * 0.15;
    // Water bends red a little less than blue, which only shows where the surface is steep:
    // a thin fringe of colour round the rim, the tell of a real lens.
    float2 fringe = normal * (1.0 - thickness) * 2.5 / ScreenSize;
    scene.r = lerp(scene.r, tex2D(SceneSampler, sceneUV + fringe).r, 0.7);
    scene.b = lerp(scene.b, tex2D(SceneSampler, sceneUV - fringe).b, 0.7);

    // The rim turns edge-on and sends the eye off sideways into the glass: dark.
    float rim = 1.0 - pow(1.0 - thickness, 2.0) * 0.8;
    // The dome's own highlight, the sky up and to the left.
    float3 surface = normalize(float3(normal, max(0.05, sqrt(saturate(1.0 - dot(normal, normal))))));
    float lit = saturate(dot(surface, normalize(float3(-0.45, -0.55, 0.70))));
    float glint = (pow(lit, 40.0) + pow(lit, 6.0) * 0.12) * Highlight;
    // And the faint lower crescent where light leaves through the far side.
    float crescent = pow(saturate(dot(surface, normalize(float3(0.25, 0.65, 0.70)))), 10.0) * 0.18 * Highlight;

    // The body of the drop gathers light from a wider patch of sky than the glass beside it,
    // so the middle reads a touch brighter than what surrounds it.
    float3 colour = scene * rim * (1.0 + 0.10 * thickness) + glint + crescent;
    return float4(colour * coverage, coverage);
}

// Where a trickle has run, it has taken the condensation with it: the clear scene, through a
// soft vertical brush, laid back over the misted edge. The brush's alpha (A of the atlas) and
// the vertex fade (A of the colour) say how much of the path is still clear.
float4 WipePS(PixelInput input) : SV_TARGET
{
    float brush = tex2D(DropSampler, input.UV).a * input.Color.a;
    clip(brush - 0.004);
    float3 scene = tex2D(SceneSampler, input.ScreenUV).rgb;
    return float4(scene * brush, brush);
}

technique Wipe
{
    pass Clear
    {
        VertexShader = compile VS_SHADERMODEL LensDropsVS();
        PixelShader = compile PS_SHADERMODEL WipePS();
    }
}

technique LensDrops
{
    pass Drops
    {
        VertexShader = compile VS_SHADERMODEL LensDropsVS();
        PixelShader = compile PS_SHADERMODEL LensDropsPS();
    }
}
