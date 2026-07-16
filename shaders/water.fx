//=============================================================================
// water.fx  —  SDV-Radiance Phase 4
// Gentle refraction ripple + specular sparkle applied ONLY to water tiles.
// A per-tile mask (built on the CPU from GameLocation.isWaterTile and aligned
// to the viewport) tells the shader which pixels are water, so nothing else in
// the frame is distorted. The game already animates the water texture, so this
// is deliberately subtle and layers on top.
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

texture MaskTexture;
sampler2D MaskSampler = sampler_state
{
    Texture = <MaskTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None; // linear = soft tile-edge feather
    AddressU = Clamp; AddressV = Clamp;
};

float Time;             // seconds
float Strength;         // ripple amplitude (UV units are scaled inside)
float Speed;            // ripple animation speed
float Sparkle;          // specular glint intensity
float2 TilesPerScreen;  // how many world tiles span the buffer (w/64, h/64)
float2 ViewFrac;        // sub-tile scroll offset of the viewport, in tiles [0..1)
float2 MaskSize;        // mask texture size in texels (tiles)

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 WaterPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;

    // Map screen UV -> "tiles from the top-left visible tile", then to the mask.
    float2 tileCoord = uv * TilesPerScreen + ViewFrac;
    float2 maskUV = tileCoord / MaskSize;
    float water = tex2D(MaskSampler, maskUV).r;

    if (water <= 0.001)
        return tex2D(SourceSampler, uv);

    // Refraction: two crossing sine waves, phase-animated. Amplitude in UV is
    // tiny so it reads as shimmer, not warping.
    float t = Time * Speed;
    float wx = sin(uv.y * 42.0 + t * 6.0) + 0.5 * sin(uv.x * 27.0 - t * 4.0);
    float wy = cos(uv.x * 38.0 - t * 5.0) + 0.5 * cos(uv.y * 31.0 + t * 3.5);
    float2 ripple = float2(wx, wy) * (Strength * 0.0025) * water;

    float4 col = tex2D(SourceSampler, uv + ripple);

    // Specular sparkle: sparse moving glints, only on water, additive.
    float g = sin(uv.x * 130.0 + t * 3.0) * sin(uv.y * 96.0 - t * 2.3);
    float glint = pow(saturate(g), 24.0);
    col.rgb += glint * Sparkle * water;

    return col;
}

technique Water { pass P0 { PixelShader = compile PS_SHADERMODEL WaterPS(); } }
