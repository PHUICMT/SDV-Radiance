//=============================================================================
// wet.fx  —  SDV-Radiance
// Ground that remembers the rain: damp darkening and saturation on suitable
// ground, plus a shoreline damp band read off the water distance field.
// (Puddles, the puddle mirror and the night light streaks join this file in
// their own steps.)
// Target: MonoGame OpenGL (Shader Model 3.0), used as a SpriteBatch effect.
//
// The old "wet rim" was removed for dimming the whole screen: its gate was the
// mask's CLASS rather than nearness to water, and the distance field's
// "no water here" value was indistinguishable from "right at the edge". Both
// preconditions are fixed now (the sentinel decodes to -32 texels), and this
// pass is gated on a REAL distance existing, which is the condition the
// tombstone in water.fx demanded before any rebirth.
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

// One texel per TILE for the whole map, built on the CPU when a location is
// entered: 0 = never wet (water, walls, roofs, decks, the void), ~0.5 = damp
// only (grass and ordinary ground darken but never pool), 1 = puddleable
// (dirt, stone, anything diggable).
texture SuitabilityTexture;
sampler2D SuitabilitySampler = sampler_state
{
    Texture = <SuitabilityTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = None;
};

// The water shoreline distance field (Alpha8), in the water mask's window.
texture SdfTexture;
sampler2D SdfSampler = sampler_state
{
    Texture = <SdfTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
};

// The flipped-entity mirror the water reflection already bakes: every character and
// prop drawn upside-down anchored at its own feet. Sampling it AT the current pixel
// is a correct feet-anchored reflection by construction - zero new math, and the
// settled feet-anchor decision honoured.
texture ReflectTexture;
sampler2D ReflectSampler = sampler_state
{
    Texture = <ReflectTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
};

float2 TilesPerScreen;   // world tiles spanned by the frame (w/64, h/64)
float2 WorldTileOffset;  // viewport origin in world tiles (fractional)
float2 MapSizeTiles;     // the whole map, in tiles (the suitability texture's span)
float2 MaskOrigin;       // water-mask window origin, world tiles
float2 MaskSize;         // water-mask window size, tiles
float SdfValid;          // 1 when the distance field exists this frame, else 0
float Wetness;           // 0..1 - the world truth, on the game-minute clock
float Strength;          // the config slider
float PuddleCoverage;    // how much of the puddleable ground pools (config, 0 with DR installed)
float PuddleWet;         // the dry-out curve: pools vanish before the dampness does
float3 SkyTint;          // synthesised sky, already scaled by the lighting ambient
float ReflectOn;         // 1 when the entity mirror was baked this frame
float3 MirrorTint;       // cool grade on the puddle mirror, the water family's tint
float LightCount;        // how many of the 8 glimmer slots are filled
float4 Lights[8];        // xy = light UV (frame space) - z = radius - w = strength
float NightGlow;         // the dusk ramp the water's glimmer already gates on
float Aspect;            // frame w/h, so a streak's width is measured in real pixels

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

// One puddle per hash-chosen puddleable tile, anchored to the WORLD tile: stable
// while walking, identical for both split-screen cameras, and never on grass.
// Two overlapping lobes rather than one ellipse: their union makes kidneys,
// figure-eights and lopsided blobs, so no two pools on a street read as the same
// stamp pressed twice.
float PuddleAt(float2 worldTile, float puddleable)
{
    float2 cell = floor(worldTile);
    float keep = step(hash(cell), 0.30 * PuddleCoverage);
    float2 inside = frac(worldTile);
    float2 centreA = float2(0.30 + 0.36 * hash(cell + 7.31), 0.32 + 0.32 * hash(cell + 3.77));
    float radiusA = 0.20 + 0.16 * hash(cell + 11.9);
    float2 offsetA = (inside - centreA) / float2(radiusA, radiusA * 0.72);
    float2 centreB = centreA + float2((hash(cell + 5.13) - 0.5) * 0.36,
                                      (hash(cell + 9.71) - 0.5) * 0.26);
    float radiusB = 0.12 + 0.15 * hash(cell + 2.39);
    float2 offsetB = (inside - centreB) / float2(radiusB, radiusB * 0.84);
    float roundDistance = min(dot(offsetA, offsetA), dot(offsetB, offsetB));
    float pool = 1.0 - smoothstep(0.70, 1.0, roundDistance);
    return pool * keep * puddleable * PuddleWet;
}

float4 WetPS(PixelInput input) : COLOR0
{
    float4 src = tex2D(SourceSampler, input.UV);
    float2 worldTile = input.UV * TilesPerScreen + WorldTileOffset;
    // Aim at the CENTRE of this tile's texel rather than its corner. Point sampling lands on
    // the same texel either way, but a corner sample is one sampler-state change away from
    // reading the neighbouring tile along every edge, which is the kind of half-tile seam that
    // is very hard to recognise once it is on screen.
    float2 suitabilityUV = (floor(worldTile) + 0.5) / MapSizeTiles;
    float suitability = tex2D(SuitabilitySampler, suitabilityUV).a;
    float wetCapable = step(0.25, suitability);

    float wet = Wetness * wetCapable;

    // Shoreline damp: the last five texels of land before the water. Gated on the
    // distance field having a REAL distance here (the no-water sentinel decodes to
    // about -32) and on the pixel being inside the mask window at all - the two
    // gates whose absence made the old rim dim entire beaches.
    float2 maskUV = (worldTile - MaskOrigin) / MaskSize;
    float inWindow = step(0.0, maskUV.x) * step(0.0, maskUV.y)
                   * step(maskUV.x, 1.0) * step(maskUV.y, 1.0);
    float sdfT = (tex2D(SdfSampler, maskUV).a - 0.501961) * 63.75;
    float hasDistance = step(-30.5, sdfT);
    float shoreBand = saturate((sdfT + 5.0) / 5.0)
                    * hasDistance * inWindow * wetCapable * SdfValid;
    // The shore is always a little damp while the pass runs; rain deepens it.
    wet = max(wet, shoreBand * (0.35 + 0.65 * Wetness));

    // Puddles double the dampness under them and fill with a little sky, which is
    // what sells standing water seen straight from above.
    float puddleable = step(0.75, suitability);
    float puddle = PuddleAt(worldTile, puddleable) * step(0.01, Wetness);
    wet = max(wet, puddle);

    // Posterised to four steps so the dampness reads as pixel art, not as a decal.
    float steppedWet = floor(saturate(wet + puddle) * 4.0 + 0.5) * 0.25 * Strength;

    // Wet ground is darker and richer, never bluer: the game already blue-shifts
    // rainy ambient, and piling a tint on top of a tint reads as mud.
    float grey = dot(src.rgb, float3(0.299, 0.587, 0.114));
    float3 deepened = lerp(grey.xxx, src.rgb, 1.0 + 0.14 * steppedWet);
    src.rgb = deepened * (1.0 - 0.055 * steppedWet);
    src.rgb = lerp(src.rgb, SkyTint, puddle * 0.32 * Strength);
    // The upside-down world standing in the pool. The mirror is screen-sized and
    // feet-anchored already, so the current pixel IS the right sample.
    if (ReflectOn > 0.5)
    {
        float4 mirrored = tex2D(ReflectSampler, input.UV);
        src.rgb = lerp(src.rgb, mirrored.rgb * MirrorTint,
                       saturate(mirrored.a) * puddle * 0.38 * Strength);
    }

    // The classic wet-street look: each lamp smears a vertical streak DOWN the wet
    // ground below itself. Eight lights, a handful of multiplies each - gated on
    // dusk (NightGlow) and on the ground actually being wet at this pixel.
    if (NightGlow > 0.01)
    {
        float wetForStreaks = max(steppedWet, puddle * Strength);
        if (wetForStreaks > 0.01)
        {
            float3 streakSum = float3(0.0, 0.0, 0.0);
            for (int i = 0; i < 8; i++)
            {
                if (i >= LightCount) break;
                float2 toLight = input.UV - Lights[i].xy;
                if (toLight.y <= 0.0) continue;              // only BELOW the light
                float across = toLight.x * Aspect;
                float lengthUv = 0.045 + 0.05 * saturate(Lights[i].z * 0.5);
                float widthUv = 0.006;
                float lengthFalloff = saturate(1.0 - toLight.y / lengthUv);
                float widthFalloff = exp(-(across * across) / (widthUv * widthUv));
                streakSum += float3(1.0, 0.85, 0.62)
                           * (lengthFalloff * lengthFalloff * widthFalloff * Lights[i].w);
            }
            src.rgb += streakSum * (0.28 * wetForStreaks * NightGlow);
        }
    }
    return src;
}

technique WetWorld { pass P0 { PixelShader = compile PS_SHADERMODEL WetPS(); } }
