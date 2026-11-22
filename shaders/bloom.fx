//=============================================================================
// bloom.fx  —  SDV-Radiance
// Bright-pass extraction, separable Gaussian blur, additive composite.
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

// SpriteBatch binds the drawn texture to sampler register s0.
sampler2D SourceSampler : register(s0);

// Blurred bloom target, sampled during the composite pass.
texture BloomTexture;
sampler2D BloomSampler = sampler_state
{
    Texture = <BloomTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

float Threshold;      // luminance cutoff for the bright-pass (0..1)
float Intensity;      // how strongly bloom is added back (0..2)
float2 TexelSize;     // (1/width, 1/height) of the blur source, for tap offsets
float BloomWarm;      // 0 by day .. 1 at night: tint the bloom warm so lamps/windows glow amber
float EmissiveBoost;  // 0..1: how far a saturated bright pixel may glow below Threshold
float WideGlow;       // how much of the wide layer is added beside the tight glow (0 = not built), set on the CPU
float WideFromLights; // 1 on the first shrink only: keep what reads as a light, drop bright grey
float2 BrightGridShift; // the frame's UV past the bright pass's last world-fixed texel: read back by it, read on by the composite
// The lamps' own lightmap (floodlight.fx's samplers and mapping, as fog.fx reads it). At night
// the wide layer keeps only what stands in a lamp's light: with the night darkness turned down,
// pale grass and white flowers were bright enough to pass as lights, and grown to a quarter of
// the screen at the night gain they turned a lit farm into white fog (DaikoharaHajime, 2.5.0).
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
float2 WorldTileOffset;  // viewport origin in world tiles, continuous
float2 MapOrigin;        // world tile coordinate of the lightmap's (0,0) cell
float2 MapSize;          // lightmap size in cells
float2 Map2Origin;
float2 Map2Size;
float LightMapBlend;     // 0 = LightMapTexture only, 1 = LightMap2Texture only
float3 SkyLevel;         // what an open cell holds with no lamp near it
float WideLampsOnly;     // 0 by day or with no lightmap .. 1 at night: the first shrink keeps only lamp-lit light
float4 WideShrinkMap; // xy scale, zw offset: a level's UV to its source's UV, for the first shrink's world-fixed grid
float4 WideReadMap;   // xy scale, zw offset: the screen's UV to the finished layer's UV

// The wide layer: the bright pass shrunk three more times and grown back, summed. Read only by
// the composite, and only while WideGlow is above zero.
texture WideTexture;
sampler2D WideSampler = sampler_state
{
    Texture = <WideTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// 9-tap Gaussian weights (normalized).
static const int   TAPS = 5;
static const float W[5] = { 0.227027, 0.194595, 0.121622, 0.054054, 0.016216 };

// The same kernel read with LINEAR SAMPLING: one bilinear fetch placed BETWEEN two
// neighbouring texels returns their weighted sum, so a pair of taps costs one fetch
// instead of two and the 9-tap blur becomes 5 fetches per axis.
//
// The offsets are not free parameters. For a pair (wa at texel a, wb at texel b=a+1),
// the merged weight is wa+wb and the sample must sit where bilinear reproduces the
// original ratio: offset = (a*wa + b*wb) / (wa + wb). Checked numerically against the
// discrete 9-tap over 300 random samples: max absolute error 4.5e-15, i.e. identical.
//
// This ONLY works where consecutive taps land on ADJACENT texels, which means the pass
// must sample its source 1:1. Both blur passes here do (half-res to half-res). It does
// NOT hold for a pass that downsamples while it blurs, nor for a kernel with a spread
// multiplier, and this file is not the place to copy it from without checking that.
static const float LW[2] = { 0.316217, 0.070270 };   // merged pair weights
static const float LO[2] = { 1.384615, 3.230769 };   // merged pair offsets, in texels

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

// KEEP THE WINDOW'S OWN DETAIL, the bloom's half (the flood pass and the grade have the others).
// The glow of a bright pane is laid over the pane itself as much as round it, and that haze
// alone took a green-blue glass to cream. Inside a window nearly all of it is held back; the halo
// round the window, where the boxes have faded out, is left as it was.
float4 WindowZones[8];      // xy = centre (screen UV), zw = half size (tiles)
float WindowZoneCount;      // how many entries are live
float WindowDetail;         // 0 = off, 1 = inside a window the bloom is mostly held back (eased on CPU)
float2 TilesPerScreen;      // buffer size in world tiles (w/64, h/64)

float WindowZone(float2 uv)
{
    float zone = 0.0;
    [unroll]
    for (int zoneIndex = 0; zoneIndex < 8; zoneIndex++)
    {
        float zoneOn = step((float)zoneIndex + 0.5, WindowZoneCount);
        float2 zoneDelta = (uv - WindowZones[zoneIndex].xy) * TilesPerScreen / max(WindowZones[zoneIndex].zw, 0.01);
        float zoneEdge = saturate(1.0 - max(abs(zoneDelta.x), abs(zoneDelta.y)));
        zone = max(zone, zoneOn * smoothstep(0.0, 0.25, zoneEdge));
    }
    return zone;
}

//-----------------------------------------------------------------------------
// Bright pass (also downsamples full-res -> half-res).
//
// Uses a 4-tap bilinear box with a Karis average (weight each tap by
// 1/(1+luma)) so a lone very-bright pixel can't dominate its output texel.
// This is what stops bloom "fireflies" from pulsing as the camera scrolls
// and bright pixel-art highlights cross texel boundaries. TexelSize is set to
// the SOURCE (full-res) texel size by the pipeline before this pass runs.
//-----------------------------------------------------------------------------
float4 BrightPassPS(PixelInput input) : SV_TARGET
{
    float2 t = TexelSize;
    float2 uv = input.UV - BrightGridShift;
    float3 s0 = tex2D(SourceSampler, uv + float2(-t.x, -t.y)).rgb;
    float3 s1 = tex2D(SourceSampler, uv + float2( t.x, -t.y)).rgb;
    float3 s2 = tex2D(SourceSampler, uv + float2(-t.x,  t.y)).rgb;
    float3 s3 = tex2D(SourceSampler, uv + float2( t.x,  t.y)).rgb;

    float w0 = 1.0 / (1.0 + dot(s0, LUMA));
    float w1 = 1.0 / (1.0 + dot(s1, LUMA));
    float w2 = 1.0 / (1.0 + dot(s2, LUMA));
    float w3 = 1.0 / (1.0 + dot(s3, LUMA));
    float3 c = (s0 * w0 + s1 * w1 + s2 * w2 + s3 * w3) / (w0 + w1 + w2 + w3);

    float lum = dot(c, LUMA);
    // Saturated AND bright reads as a light SOURCE: a lit pane, lava, a flame, a crystal.
    // Bright but grey reads as a lit SURFACE - snow, a white wall - which is exactly the
    // pixel the old god rays wrongly promoted to a light and paid for. Chroma is zero on
    // every grey no matter how bright, so this gate only ever opens for colour.
    float brightest = max(c.r, max(c.g, c.b));
    float chroma = brightest - min(c.r, min(c.g, c.b));
    float saturation = chroma / max(brightest, 1e-4);
    float emissive = smoothstep(0.35, 0.75, saturation) * smoothstep(0.50, 0.95, lum);
    float gate = Threshold - EmissiveBoost * 0.35 * emissive;
    float knee = smoothstep(gate, gate + 0.25, lum);
    return float4(c * knee, 1.0);
}

//-----------------------------------------------------------------------------
// Separable Gaussian blur (horizontal / vertical).
//-----------------------------------------------------------------------------
float4 BlurHorizontalPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 0; i < 2; i++)
    {
        float2 off = float2(TexelSize.x * LO[i], 0.0);
        sum += tex2D(SourceSampler, input.UV + off).rgb * LW[i];
        sum += tex2D(SourceSampler, input.UV - off).rgb * LW[i];
    }
    return float4(sum, 1.0);
}

float4 BlurVerticalPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 0; i < 2; i++)
    {
        float2 off = float2(0.0, TexelSize.y * LO[i]);
        sum += tex2D(SourceSampler, input.UV + off).rgb * LW[i];
        sum += tex2D(SourceSampler, input.UV - off).rgb * LW[i];
    }
    return float4(sum, 1.0);
}

//-----------------------------------------------------------------------------
// Wide glow (the "multi-level" bloom of Call of Duty: Advanced Warfare, Jimenez 2014).
//
// The tight blur above reaches about eight screen pixels, which reads as a lamp's own halo and
// nothing more. A light that lights the air round it spreads much further than that, and a blur
// that wide at half size would cost a kernel a hundred taps long. Instead the bright pass is
// shrunk by half three times with a 13-tap filter, and the small images are grown back one level
// at a time with a 3x3 tent, each added onto the level above. Every level adds a glow twice as wide
// as the one before, so the sum falls off slowly the way light through haze does.
//
// TexelSize is the SOURCE level's texel for both passes. Both read with linear filtering, so a
// tap set between texels averages four; the offsets are in source texels.
//-----------------------------------------------------------------------------
float3 WideTap(float2 uv, float2 texel, float2 offset)
{
    return tex2Dlod(SourceSampler, float4(uv + texel * offset, 0.0, 0.0)).rgb;
}

float4 WideDownPS(PixelInput input) : SV_TARGET
{
    float2 texel = TexelSize;
    float2 uv = input.UV * WideShrinkMap.xy + WideShrinkMap.zw;
    // Thirteen taps as five overlapping 2x2 boxes: one in the middle (weight 0.5) and four at the
    // corners (0.125 each). The overlap is what keeps a bright texel from flickering as the
    // camera moves it across the grid of the smaller image.
    float3 farTopLeft = WideTap(uv, texel, float2(-2.0, -2.0));
    float3 farTop = WideTap(uv, texel, float2(0.0, -2.0));
    float3 farTopRight = WideTap(uv, texel, float2(2.0, -2.0));
    float3 farLeft = WideTap(uv, texel, float2(-2.0, 0.0));
    float3 centre = WideTap(uv, texel, float2(0.0, 0.0));
    float3 farRight = WideTap(uv, texel, float2(2.0, 0.0));
    float3 farBottomLeft = WideTap(uv, texel, float2(-2.0, 2.0));
    float3 farBottom = WideTap(uv, texel, float2(0.0, 2.0));
    float3 farBottomRight = WideTap(uv, texel, float2(2.0, 2.0));
    float3 nearTopLeft = WideTap(uv, texel, float2(-1.0, -1.0));
    float3 nearTopRight = WideTap(uv, texel, float2(1.0, -1.0));
    float3 nearBottomLeft = WideTap(uv, texel, float2(-1.0, 1.0));
    float3 nearBottomRight = WideTap(uv, texel, float2(1.0, 1.0));
    float3 sum = (nearTopLeft + nearTopRight + nearBottomLeft + nearBottomRight) * 0.125
               + (farTopLeft + farTop + farLeft + centre) * 0.03125
               + (farTop + farTopRight + centre + farRight) * 0.03125
               + (farLeft + centre + farBottomLeft + farBottom) * 0.03125
               + (centre + farRight + farBottom + farBottomRight) * 0.03125;
    // Only light feeds the wide layer. A field of snow or a white wall passes the bright pass as
    // readily as a lamp does, and grown to a quarter of the screen and lifted to be seen round a
    // small lamp, it turned a winter farm at noon into white fog. Grey has no colour however bright
    // it is, so the same test the coloured-light glow uses keeps it out: lamps, lit windows, flames
    // and warm sunlit ground stay.
    float brightest = max(sum.r, max(sum.g, sum.b));
    float saturation = (brightest - min(sum.r, min(sum.g, sum.b))) / max(brightest, 1e-4);
    sum *= lerp(1.0, smoothstep(0.15, 0.45, saturation), WideFromLights);
    if (WideLampsOnly > 0.0)
    {
        float2 worldTile = (uv - BrightGridShift) * TilesPerScreen + WorldTileOffset;
        float3 light = tex2Dlod(LightMapSampler, float4((worldTile - MapOrigin) / MapSize, 0.0, 0.0)).rgb * 2.0;
        if (LightMapBlend > 0.0)
            light = lerp(light, tex2Dlod(LightMap2Sampler, float4((worldTile - Map2Origin) / Map2Size, 0.0, 0.0)).rgb * 2.0, LightMapBlend);
        // At night only what stands near a lamp feeds the layer, and only what has the colour of
        // light. The share falls off as the square of the lamp's light (fog.fx measured a street
        // lamp's excess at 0.3 to 0.4 at most), so the haze gathers round the bulb; and the colour
        // test is tighter than by day, because white and pale green flowers inside a lamp's pool
        // pass the day test and turned the pool into white fog. Feeding the lamp's own light
        // instead was tried: the lightmap's broad fields came through as blobs over the town.
        float3 excess = max(light - SkyLevel * 1.05, 0.0);
        float reach = saturate(max(excess.r, max(excess.g, excess.b)) / 0.35);
        float nightKeep = reach * reach * smoothstep(0.35, 0.65, saturation);
        sum *= lerp(1.0, nightKeep, WideLampsOnly);
    }
    return float4(sum, 1.0);
}

// Drawn with additive blending onto the level above, which already holds its own shrunk image.
float4 WideUpPS(PixelInput input) : SV_TARGET
{
    float2 texel = TexelSize;
    float2 uv = input.UV;
    float3 sum = WideTap(uv, texel, float2(0.0, 0.0)) * 4.0;
    sum += (WideTap(uv, texel, float2(-1.0, 0.0)) + WideTap(uv, texel, float2(1.0, 0.0))
          + WideTap(uv, texel, float2(0.0, -1.0)) + WideTap(uv, texel, float2(0.0, 1.0))) * 2.0;
    sum += WideTap(uv, texel, float2(-1.0, -1.0)) + WideTap(uv, texel, float2(1.0, -1.0))
         + WideTap(uv, texel, float2(-1.0, 1.0)) + WideTap(uv, texel, float2(1.0, 1.0));
    return float4(sum / 16.0, 1.0);
}

//-----------------------------------------------------------------------------
// Composite: screen-blend the blurred bloom over the scene. Screen (rather than
// plain additive) asymptotes toward white instead of piling past it, so bright
// areas glow without blowing out into a flat white blob (e.g. indoors).
//-----------------------------------------------------------------------------
float4 CompositePS(PixelInput input) : SV_TARGET
{
    float4 scene = tex2D(SourceSampler, input.UV);
    float3 glow = tex2D(BloomSampler, input.UV + BrightGridShift).rgb;
    [branch] if (WideGlow > 0.001)
        glow += min(tex2Dlod(WideSampler, float4(input.UV * WideReadMap.xy + WideReadMap.zw, 0.0, 0.0)).rgb * WideGlow, 0.6);
    float3 bloom = saturate(glow * Intensity);
    // At night the bloom turns warm/amber so lamp and window glow reads as a cosy halo
    // rather than a neutral haze.
    bloom *= lerp(float3(1.0, 1.0, 1.0), float3(1.10, 1.02, 0.82), BloomWarm);
    [branch] if (WindowDetail > 0.001 && WindowZoneCount > 0.5)
        bloom *= 1.0 - 0.9 * WindowZone(input.UV) * WindowDetail;
    float3 result = 1.0 - (1.0 - scene.rgb) * (1.0 - bloom);
    return float4(result, scene.a);
}

technique BrightPass    { pass P0 { PixelShader = compile PS_SHADERMODEL BrightPassPS(); } }
technique BlurHorizontal{ pass P0 { PixelShader = compile PS_SHADERMODEL BlurHorizontalPS(); } }
technique BlurVertical  { pass P0 { PixelShader = compile PS_SHADERMODEL BlurVerticalPS(); } }
technique Composite     { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
technique WideDown      { pass Down { PixelShader = compile PS_SHADERMODEL WideDownPS(); } }
technique WideUp        { pass Up { PixelShader = compile PS_SHADERMODEL WideUpPS(); } }
