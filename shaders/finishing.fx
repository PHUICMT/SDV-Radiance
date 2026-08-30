//=============================================================================
// finishing.fx  —  SDV-Radiance
// Camera-lens finishing pass: vignette (darkened edges) + chromatic aberration
// (radial R/B channel split). Runs last, on the fully graded image.
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

float VignetteStrength; // 0 = off .. ~1 = strong edge darkening
float CAStrength;       // chromatic-aberration UV offset scale (already small, e.g. 0..0.03)
float NightAmt;         // 0 by day .. 1 deep night — a touch more vignette at night

// ---- heat haze -------------------------------------------------------------
// Hot air over lava bends what is seen through it. The heat map
// is a tiny per-tile grid (1 texel = 1 world tile) built from the same painted
// labels the water reads; LINEAR filtering over it is what melts the tile grid
// into a smooth field. The wobble displaces the SOURCE lookup before the lens
// does its own work, so the haze is in the picture the lens then vignettes.
texture HeatMapTexture;
sampler2D HeatMapSampler = sampler_state
{
    Texture = <HeatMapTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float2 TilesPerScreen;     // screen size in world tiles
float2 WorldTileOffset;    // camera position in world tiles
float2 HeatMapOriginTiles; // heat grid's first tile in world tiles
float2 HeatMapSizeTiles;   // heat grid size in tiles
float HeatHazeStrength;    // 0 = off; carries the presence ease so it never pops
float2 PlayerWorldTile;    // the player's middle in world tiles; the haze leaves them alone
float HeatClock;           // seconds; the pipeline stops advancing it when frozen
float2 ScreenPixels;       // viewport size in pixels, for the dither's pixel grid
float3 SkyLightTint = float3(1.0, 1.0, 1.0);  // the colour the SKY is lighting the world
                           // multiply that keeps luminance: white on every ordinary night. An
                           // aurora bright enough to be reflected in the water is bright enough
                           // to land on the sand and the rocks beside it, and a display that
                           // stops dead at the waterline reads as a decal on the water rather
                           // than as something in the sky.

float HeatAt(float2 worldTile)
{
    float2 heatUV = (worldTile - HeatMapOriginTiles) / HeatMapSizeTiles;
    return tex2D(HeatMapSampler, heatUV).r;
}

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// Sub-LSB triangular dither for this pass's 8-bit write: a slow gradient (a fog
// bank, the tone curve, the vignette ramp) cannot survive eight bits without
// stepping, and those steps are the colour banding players report. Interleaved
// gradient noise (Jimenez 2014): three instructions, no fetch; the triangular
// remap hides band EDGES where uniform noise leaves them visible. Static across
// frames on purpose - a pattern that changed per frame would be a shimmer of its
// own. Same decision, same idiom as water.fx; correctness, not a look.
float DitherLsb(float2 uv)
{
    float pixelNoise = frac(52.9829189 * frac(0.06711056 * uv.x * ScreenPixels.x
                                            + 0.00583715 * uv.y * ScreenPixels.y));
    return pixelNoise < 0.5 ? sqrt(2.0 * pixelNoise) - 1.0
                            : 1.0 - sqrt(2.0 - 2.0 * pixelNoise);
}


float4 FinishPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float2 dir = uv - 0.5;
    float dist = length(dir);

    // Heat haze first, so the lens work below reads the already-bent picture.
    // Heat RISES: a pixel shimmers when hot ground sits under it, so the taps
    // ask the tiles below the pixel, strongest just above the surface. The two
    // sines drift at different rates so the wobble never settles into a pattern.
    if (HeatHazeStrength > 0.0001)
    {
        float2 worldTile = uv * TilesPerScreen + WorldTileOffset;
        float heat = HeatAt(worldTile + float2(0.0, 0.30)) * 0.50
                   + HeatAt(worldTile + float2(0.0, 0.85)) * 0.35
                   + HeatAt(worldTile + float2(0.0, 1.40)) * 0.15;
        if (heat > 0.002)
        {
            // The player is not made of hot air: the bend fades to nothing inside a
            // standing-height ellipse round them, so the sprite the eye follows never swims
            // while the ground beside it does.
            float2 fromPlayer = (worldTile - PlayerWorldTile) * float2(1.0, 0.6);
            float playerClear = smoothstep(0.55, 1.0, length(fromPlayer));
            float wave = sin(worldTile.x * 34.0 + HeatClock * 6.5 + worldTile.y * 5.0)
                       * sin(worldTile.y * 21.0 - HeatClock * 4.5);
            float2 texel = 1.0 / (TilesPerScreen * 64.0);
            float bend = wave * heat * HeatHazeStrength * playerClear;
            uv.y += bend * texel.y * 1.6;
            uv.x += bend * texel.x * 0.6;
        }
    }

    // Chromatic aberration: split R/B outward from the center, growing with
    // distance so the frame stays crisp in the middle (like a real lens).
    float2 offset = dir * CAStrength * dist;
    float r = tex2D(SourceSampler, uv + offset).r;
    float4 mid = tex2D(SourceSampler, uv);
    float b = tex2D(SourceSampler, uv - offset).b;
    float3 col = float3(r, mid.g, b);

    // Vignette: smooth radial falloff, no darkening until past the mid-radius.
    // A touch stronger at night to draw the eye inward.
    float vig = (VignetteStrength + NightAmt * 0.12) * smoothstep(0.35, 0.80, dist);
    col *= (1.0 - vig);
    // The sky's own colour on everything under it. It is a multiply rather than an add, and
    // the C# side keeps its luminance at 1, so this shifts hue without lifting or dropping a
    // single stop of brightness - the rule the bounced-light colour already follows, and the
    // reason a whole-frame tint is safe here at all.
    col *= SkyLightTint;
    // Gated by the vignette's own ramp: the frame's centre stays the exact source pixel.
    col += DitherLsb(input.UV) * (1.0 / 255.0) * saturate(vig * 40.0);

    return float4(col, mid.a);
}

technique Finishing { pass P0 { PixelShader = compile PS_SHADERMODEL FinishPS(); } }
