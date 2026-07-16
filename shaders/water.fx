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
    MinFilter = Point; MagFilter = Point; MipFilter = None; // point = crisp per-tile, no bleed onto land
    AddressU = Clamp; AddressV = Clamp;
};

float Time;             // seconds
float Strength;         // ripple amplitude (UV units are scaled inside)
float Speed;            // ripple animation speed
float Sparkle;          // specular glint intensity
float2 TilesPerScreen;  // how many world tiles span the buffer (w/64, h/64)
float2 WorldTileOffset; // viewport origin in world tiles (viewport.XY / 64), continuous
float2 MaskSize;        // mask texture size in texels (tiles)
float WaterKind;        // 0 = still (pond/river), 1 = ocean/beach (big directional swell)

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

float4 WaterPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;

    // Continuous world-tile coordinate of this pixel (locks the shimmer to the
    // water surface as the camera pans, instead of swimming across the screen).
    float2 worldTile = uv * TilesPerScreen + WorldTileOffset;

    // Point-sample the per-tile mask so the tile grid never bleeds onto land.
    float2 startTile = floor(WorldTileOffset);
    float2 maskUV = (floor(worldTile) - startTile + 0.5) / MaskSize;
    float tileWater = tex2D(MaskSampler, maskUV).r;

    float4 src = tex2D(SourceSampler, uv);
    if (tileWater <= 0.001)
        return src;

    // Refine to the ACTUAL water pixels: the game draws curved banks / rocks
    // inside the square water tiles, so gate on blue-dominant color. This fades
    // the effect off the dirt & rock edges so only the real water ripples.
    float blueness = saturate((src.b - src.r) * 3.0) * saturate((src.b - src.g) * 3.0 + 0.35);
    float water = tileWater * blueness;
    if (water <= 0.002)
        return src;

    // Refraction in WORLD space so the ripple travels with the water. Two
    // profiles blended by WaterKind:
    //  - pond: fine crossing ripples, small & quick (still surface).
    //  - ocean: long directional swell rolling toward shore, big & slow.
    float t = Time * Speed;
    float pwx = sin(worldTile.y * 6.3 + t * 6.0) + 0.5 * sin(worldTile.x * 4.1 - t * 4.0);
    float pwy = cos(worldTile.x * 5.7 - t * 5.0) + 0.5 * cos(worldTile.y * 4.7 + t * 3.5);
    float2 pondRipple = float2(pwx, pwy) * (Strength * 0.0025);

    float swell = sin(worldTile.y * 2.1 + t * 1.6) + 0.35 * sin(worldTile.x * 1.4 - t * 1.0);
    float2 oceanRipple = float2(swell * 0.25, swell) * (Strength * 0.006);

    float2 ripple = lerp(pondRipple, oceanRipple, WaterKind) * water;

    float4 col = tex2D(SourceSampler, uv + ripple);

    // Depth tint: cool + deepen the water for a wetter, more 3D surface.
    float3 tint = col.rgb * float3(0.90, 0.97, 1.12);
    col.rgb = lerp(col.rgb, tint, 0.35 * water);

    // Random drifting glints: split the water into cells with ONE soft glint
    // each, at a random spot that wanders slowly, gliding across the surface and
    // fading gently in/out. Ocean glints are sparser, slower and drift more.
    float sdens = lerp(5.0, 3.0, WaterKind);
    float spulse = lerp(1.1, 0.55, WaterKind);
    float sdrift = lerp(0.05, 0.12, WaterKind);
    float2 sg = (worldTile + float2(t * sdrift, t * sdrift * 0.6)) * sdens;
    float2 cell = floor(sg);
    float2 f = frac(sg);
    float r1 = hash(cell);
    float r2 = hash(cell + float2(19.7, 7.3));
    float2 center = float2(r1, r2) + 0.18 * float2(sin(t * 0.7 + r1 * 6.2831853),
                                                   cos(t * 0.6 + r2 * 6.2831853));
    float d = length(f - center);
    float pulse = 0.5 + 0.5 * sin(t * spulse + r1 * 6.2831853); // 0..1, no hard edges
    float glint = smoothstep(0.24, 0.0, d) * pulse;
    col.rgb += glint * Sparkle * water;

    return col;
}

technique Water { pass P0 { PixelShader = compile PS_SHADERMODEL WaterPS(); } }
