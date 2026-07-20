//=============================================================================
// floodlight.fx  —  SDV-Radiance
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

// Occluders (walls / trees / characters) at tile resolution, LINEAR-sampled so the
// per-light shadow march below gets soft penumbra edges for free.
texture OccluderTexture;
sampler2D OccluderSampler = sampler_state
{
    Texture = <OccluderTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float2 TilesPerScreen;   // buffer size in world tiles (w/64, h/64)
float2 WorldTileOffset;  // viewport origin in world tiles, continuous
float2 MapOrigin;        // world tile coordinate of the lightmap's (0,0) cell
float2 MapSize;          // lightmap size in cells
float Strength;          // 0..1 how strongly the flood modulates the scene
float AmbientFloor;      // lower bound so nothing ever goes fully black

float2 OccOrigin;        // world tile coordinate of the occluder mask's (0,0) cell
float2 OccMapSize;       // occluder mask size in cells
float2 LightPosArr[8];   // per-light screen UV
float4 LightColArr[8];   // rgb = colour, w = radius in UV (height units)
float DirectCount;       // how many entries are live
float Aspect;            // w/h so light pools stay round
float ShadowStrength;    // 0..1 how dark a fully occluded ray gets

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

// Occlusion at a screen-UV point (linear across tiles → soft shadow edges).
// tex2Dlod: no gradient instructions, so the per-light [branch] stays legal in ps_3_0.
float OccAt(float2 p)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - OccOrigin) / OccMapSize;
    return tex2Dlod(OccluderSampler, float4(muv, 0.0, 0.0)).r;
}

float4 FloodPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 src = tex2D(SourceSampler, uv);

    // Continuous world-tile position → lightmap UV (cell centres at +0.5).
    float2 wt = uv * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - MapOrigin) / MapSize;
    float3 light = tex2D(LightMapSampler, muv).rgb * 2.0;   // stored ×0.5 (glow headroom)

    // DIRECT light with per-light shadows: each real light adds a round pool whose ray
    // from the light to this pixel is marched against the occluder mask — walls, trees
    // and characters block it, so every light × every object casts a soft shadow.
    // (The flood map above carries the sky + the lights' INDIRECT spill.)
    float3 direct = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int li = 0; li < 8; li++)
    {
        float on = step((float)li + 0.5, DirectCount);
        float2 lp = LightPosArr[li];
        float4 lc = LightColArr[li];
        float2 dvec = uv - lp;
        dvec.x *= Aspect;
        float att = saturate(1.0 - length(dvec) / max(lc.w, 0.02));
        att *= att;
        [branch]
        if (on * att > 0.004)
        {
            float occ = 0.0;
            [unroll]
            for (int s = 1; s <= 12; s++)
            {
                float f = s / 12.0;
                // Fade samples near the light (a lamp sitting ON a wall tile must not
                // shadow its own glow) and near the pixel (the lit side of a wall).
                float wgt = smoothstep(0.06, 0.28, f) * smoothstep(1.02, 0.86, f);
                occ = max(occ, OccAt(lerp(lp, uv, f)) * wgt);
            }
            direct += lc.rgb * att * (1.0 - occ * ShadowStrength);
        }
    }
    light += direct;

    // Ordered dither breaks the bilinear ramps of the low-res map into pixel noise.
    float dith = (Bayer(wt * 16.0) - 0.5) * 0.035;

    float3 mul = saturate(light + AmbientFloor + dith);
    float3 lit = src.rgb * lerp(float3(1.0, 1.0, 1.0), mul, Strength);
    // >1 light (lamp cores) adds a soft warm glow rather than clipping at white.
    lit += src.rgb * saturate(light - 1.0) * 0.45 * Strength;

    return float4(lit, src.a);
}

technique FloodLight { pass P0 { PixelShader = compile PS_SHADERMODEL FloodPS(); } }
