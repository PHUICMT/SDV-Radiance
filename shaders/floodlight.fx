//=============================================================================
// floodlight.fx  —  SDV-Radiance Phase L1
// Composites the CPU flood lightmap (see FloodLightmap.cs) over the scene:
// bilinear-upsampled per-tile RGB light, multiplied with an ambient floor and a
// touch of ordered dither so the low-res map never bands. Light above 1.0 adds
// a gentle warm glow instead of clipping.
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

texture LightMapTexture;
sampler2D LightMapSampler = sampler_state
{
    Texture = <LightMapTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float2 TilesPerScreen;   // buffer size in world tiles (w/64, h/64)
float2 WorldTileOffset;  // viewport origin in world tiles, continuous
float2 MapOrigin;        // world tile coordinate of the lightmap's (0,0) cell
float2 MapSize;          // lightmap size in cells
float Strength;          // 0..1 how strongly the flood modulates the scene
float AmbientFloor;      // lower bound so nothing ever goes fully black

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// 4x4 Bayer matrix threshold for ordered dithering (hides banding of the tiny map).
float Bayer(float2 p)
{
    float2 q = floor(frac(p / 4.0) * 4.0);
    float i = q.y * 4.0 + q.x;
    // permuted 0..15 pattern via a tiny hash — close enough to Bayer for dither use
    return frac(i * 0.0625 + frac(i * 0.381966));
}

float4 FloodPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 src = tex2D(SourceSampler, uv);

    // Continuous world-tile position → lightmap UV (cell centres at +0.5).
    float2 wt = uv * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - MapOrigin) / MapSize;
    float3 light = tex2D(LightMapSampler, muv).rgb * 2.0;   // stored ×0.5 (glow headroom)

    // Ordered dither breaks the bilinear ramps of the low-res map into pixel noise.
    float dith = (Bayer(wt * 16.0) - 0.5) * 0.035;

    float3 mul = saturate(light + AmbientFloor + dith);
    float3 lit = src.rgb * lerp(float3(1.0, 1.0, 1.0), mul, Strength);
    // >1 light (lamp cores) adds a soft warm glow rather than clipping at white.
    lit += src.rgb * saturate(light - 1.0) * 0.45 * Strength;

    return float4(lit, src.a);
}

technique FloodLight { pass P0 { PixelShader = compile PS_SHADERMODEL FloodPS(); } }
