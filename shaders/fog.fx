//=============================================================================
// fog.fx  —  SDV-Radiance
// Screen-space volumetric fog: drifting fbm mist (world-anchored) blended
// toward a fog colour, with a gentle vertical bias.
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

// Baked tileable fbm from C# (CPU-precision). Wrap addressing = seamless infinite
// coverage; no runtime hash → no GPU sin() precision seams, identical on every card.
texture NoiseTexture;
sampler2D NoiseSampler = sampler_state
{
    Texture = <NoiseTexture>;
    AddressU = Wrap;
    AddressV = Wrap;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
};

float Time;          // seconds
float Speed;         // drift speed
float Scale;         // mist feature size
float Density;       // overall opacity (0..1)
float3 FogColor;     // fog tint
float TopBias;       // extra fog toward the top of the screen (0..1)
float Patchiness;    // 0 = classic even blanket · 1 = sparse drifting wisps with clear gaps
float Coverage;      // 0..1 how MUCH of the frame the wisps occupy (amount, not opacity)
float2 WorldOffset;  // world-anchor
float2 ScreenPixels; // viewport size in pixels, for the dither's pixel grid

// The lightmap the lamps already painted (see floodlight.fx, whose samplers and mapping these
// are), read once per misty pixel so a wisp drifting past a lamp takes that lamp's light. Two
// maps and a blend for the same reason floodlight.fx has two: the flood sweep and the cascades
// cross-fade, and at 0 or 1 the other map is never touched.
texture LightMapTexture;
sampler2D LightMapSampler = sampler_state
{
    Texture = <LightMapTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
texture LightMap2Texture;
sampler2D LightMap2Sampler = sampler_state
{
    Texture = <LightMap2Texture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float2 TilesPerScreen;   // buffer size in world tiles
float2 WorldTileOffset;  // viewport origin in world tiles, continuous
float2 MapOrigin;        // world tile coordinate of the lightmap's (0,0) cell
float2 MapSize;          // lightmap size in cells
float2 Map2Origin;
float2 Map2Size;
float LightMapBlend;     // 0 = LightMapTexture only, 1 = LightMap2Texture only
float3 SkyLevel;         // what an open cell holds with no lamp near it: the sky the GI seeded
float LampGlow;          // 0..1 how much of the light above the sky the wisps take (0 = never read)

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

static const float2x2 M = float2x2(0.80, 0.60, -0.60, 0.80);

// The light a wisp may take: unchanged up to a typical street lamp (0.3 over the sky; the
// brightest measured, 0.4, keeps 0.37), then easing toward at most this much more. The ceiling
// is what keeps a lit wisp a wisp: the mist is already about 0.43 bright, so anything much past
// 0.3 on top of it reads as white.
static const float LampExcessKnee = 0.3;
static const float LampExcessRoom = 0.12;

// Two drifting layers of the baked fbm, second rotated + differently scaled so the
// pattern evolves organically instead of sliding as one rigid sheet.
float fbm(float2 p)
{
    float n1 = tex2D(NoiseSampler, p * 0.11 + float2(Time * Speed, Time * Speed * 0.2)).r;
    float n2 = tex2D(NoiseSampler, mul(M, p) * 0.23 + float2(-Time * Speed * 0.6, Time * Speed * 0.13) + 0.37).r;
    return n1 * 0.65 + n2 * 0.35;
}


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

float4 FogPS(PixelInput input) : SV_TARGET
{
    float2 p = (input.UV + WorldOffset) * Scale;
    float n = fbm(p);

    // fbm covers the whole frame (mean ~0.5), which reads as an even film. Patchiness
    // carves it into separate drifting wisps: only the denser cores survive, the rest
    // clears out completely. Coverage moves the survival threshold — how much of the
    // frame gets wisps — independently of Density (their opacity). Thresholds are
    // calibrated to the two-layer blend's narrower value range (~0.25..0.75).
    float lo = 0.70 - 0.5 * saturate(Coverage);
    float wisps = smoothstep(lo, lo + 0.22, n) * 0.9;
    n = lerp(n, wisps, saturate(Patchiness));

    // Slightly more mist toward the top of the screen.
    float grad = 1.0 + TopBias * (1.0 - input.UV.y);
    float f = saturate(n * Density * grad);

    float4 c = tex2D(SourceSampler, input.UV);
    // A wisp is lit by whatever stands in it. The lightmap says how much light is here above
    // the sky's own level; that excess, in the lamp's colour, is added to the mist's colour and
    // only shows where the mist is (f gates it below). With LampGlow at 0 the sum is FogColor
    // plus nothing, which is the old mist to the bit, and the reads are skipped.
    float3 mist = FogColor;
    [branch]
    if (LampGlow > 0.0)
    {
        float2 worldTile = input.UV * TilesPerScreen + WorldTileOffset;
        // tex2Dlod: no gradient, so the branch around it stays legal in ps_3_0.
        float3 light = tex2Dlod(LightMapSampler, float4((worldTile - MapOrigin) / MapSize, 0.0, 0.0)).rgb * 2.0;
        [branch]
        if (LightMapBlend > 0.0)
            light = lerp(light, tex2Dlod(LightMap2Sampler, float4((worldTile - Map2Origin) / Map2Size, 0.0, 0.0)).rgb * 2.0, LightMapBlend);
        // Measured at Town 22:00 on the cascades map (8 Sep): luminance times two sits at 0.56
        // for the median cell, 0.95 at the 90th percentile and 1.07 at the brightest, against a
        // night sky near 0.6, so a lamp's excess is 0.3 to 0.4 at most. Times 1.5 that lets the
        // dial's top land a wisp core at about 0.6 above the mist blue, and the default half of
        // that; without the gain the whole dial lived inside a few levels.
        float3 excess = max(light - SkyLevel * 1.05, 0.0);
        // Past what a street lamp gives, the excess bends over and stops at a ceiling. A light
        // the player carries (the fairy trinket's is a full white radius 2, far over a lamp)
        // read as an excess of several, and a wisp drifting through it went solid white: a
        // blinding ball that came and went with the mist. Everything up to the knee is the tuned
        // lamp glow to the bit.
        float3 overKnee = max(excess - LampExcessKnee, 0.0);
        excess = min(excess, LampExcessKnee) + LampExcessRoom * (1.0 - exp(-overKnee / LampExcessRoom));
        mist += excess * (LampGlow * 1.5);
    }
    float3 fogged = lerp(c.rgb, mist, f);
    // Gated by the fog's own contribution, so a clear pixel stays the exact source pixel.
    fogged += DitherLsb(input.UV) * (1.0 / 255.0) * saturate(f * 12.0);
    return float4(fogged, c.a);
}

technique Fog { pass P0 { PixelShader = compile PS_SHADERMODEL FogPS(); } }
