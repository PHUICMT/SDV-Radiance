//=============================================================================
// cascades.fx  -  SDV-Radiance
// Radiance Cascades: the flood GI lightmap computed on the GPU instead of by the
// CPU sweep in FloodLightmap.cs. The picture the lightmap paints is the same kind
// of thing (sky and lamps arriving at every tile, shade where something stands in
// the way) but it is computed as light TRAVELLING: every probe casts rays, a ray
// stops at the first thing it meets, and what it saw there is what the probe adds
// up. Shade under a canopy is rays that met the canopy; a lamp's spill round a
// corner is the rays from the far side that could still see the lamp.
//
// The trick that makes it affordable is the cascade. A probe close to a wall needs
// fine angular detail only for what is NEAR it; far things subtend small angles and
// change slowly across the map, so they can be shared between neighbouring probes.
// Cascade 0 is a dense probe grid casting few short rays; each cascade above it has
// half as many probes per axis, four times as many rays each, and a ray interval
// four times as long, starting where the previous one ended. A ray that reaches
// the end of its interval without meeting anything continues in the cascade above
// - four of its rays, averaged, from the four probes around it, bilinear - so every
// probe sees to the edge of the map at the angular resolution the distance earns.
//
// Layout of one cascade texture (all cascades share one size): the texture is cut
// into a grid of DIRECTION blocks, D x D of them (D = 2^(i+1), so 4, 16, 64, 256
// rays), and each block holds every probe of that cascade at its grid position.
// Direction-major so that "the four child rays of this ray in the cascade above"
// are four blocks of the upper texture, each read once with LINEAR filtering,
// which is the bilinear-over-four-probes the merge needs for free.
//
// Everything is in WORLD TILES. Probes sit on the tile grid (two per tile at
// cascade 0), so rebuilding the map at a new camera origin reads the same in the
// world, which is what keeps a tile crossing from stepping the light.
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

// The SpriteBatch binds its own texture here; nothing below reads it.
sampler2D SourceSampler : register(s0);

// The cascade above this one, already merged with everything above it.
texture UpperTexture;
sampler2D UpperSampler = sampler_state
{
    Texture = <UpperTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// The occluder mask at the level this cascade marches: the full eight-texel mask
// for the short near rays, and the flood's box-filtered halves for the long far
// ones, so a step never straddles a fence it should have met. Alpha = occlusion.
texture OccluderTexture;
sampler2D OccluderSampler = sampler_state
{
    Texture = <OccluderTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// What emits: the flood's own light seeds (lamps, fires, lit windows, the columns
// they spill), one texel per tile, stored x EmitterTexScale. LINEAR, so a lamp is
// a soft mound about a tile across rather than a point a ray could pass between.
texture EmitterTexture;
sampler2D EmitterSampler = sampler_state
{
    Texture = <EmitterTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// The tile grid under the mask (walls, roofs, canopies, one cell per tile): the
// resolve lifts those cells, because the pixels DRAWN there are facades in daylight.
texture BaseTexture;
sampler2D BaseSampler = sampler_state
{
    Texture = <BaseTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// Cascade 0, for the resolve.
texture Cascade0Texture;
sampler2D Cascade0Sampler = sampler_state
{
    Texture = <Cascade0Texture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float2 WindowOriginTiles;   // world tile of the window's top-left corner
float2 WindowSizeTiles;     // the window (occluder mask, emitters) in tiles
float2 ProbeGrid0;          // probes per axis at cascade 0
float2 CascadeTexSize;      // texels of one cascade texture (2 x ProbeGrid0)
float ProbeSpacingTiles0;   // tiles between cascade-0 probes (0.5)
float CascadeIndex;         // which cascade this pass computes
float CascadeCount;         // how many there are
float IntervalStartTiles;   // where this cascade's rays begin
float IntervalEndTiles;     // and where they stop
float StepTiles;            // march step, matched to the occluder level's texel
float3 MissRadiance;        // the sky (or a room's air), arriving from ABOVE: see SkyCascade
float3 HitRadiance;         // what the face of an occluder gives back (zero above SkyCascade)
// The cascade at whose end the sky arrives. In a top-down world the sky is overhead, not at
// the horizon: a wall forty tiles away shades nothing here, a wall next door shades this
// spot. So the sky is added to whatever transmittance is left at the end of THIS cascade's
// interval (2.5 tiles at 1), and the cascades above carry only the lamps. Rays that met a
// face within that reach return HitRadiance instead, which is the contact shade.
float SkyCascade;
float EmitterGain;          // how much an emitter's seed is worth to a ray that meets it
float EmitterTexScale;      // undo the emitter texture's storage scale
float OutputScale;          // the lightmap's storage scale (floodlight.fx reads x 1/OutputScale)
float3 LiftRadiance;        // the least a solid tile's facade is lit

static const float TAU = 6.28318530718;

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// One ray. Returns rgb = the radiance met along the interval, a = the transmittance
// left over for the cascade above (1 = met nothing).
float4 MarchRay(float2 probeTile, float2 dir)
{
    float3 radiance = float3(0.0, 0.0, 0.0);
    float transmittance = 1.0;
    float t = IntervalStartTiles + 0.5 * StepTiles;
    [loop]
    for (int s = 0; s < 64; s++)
    {
        if (t >= IntervalEndTiles || transmittance < 0.01)
            break;
        float2 p = probeTile + dir * t;
        float2 muv = (p - WindowOriginTiles) / WindowSizeTiles;
        // Past the window there is nothing to meet: the ray continues as open air.
        if (muv.x < 0.0 || muv.y < 0.0 || muv.x > 1.0 || muv.y > 1.0)
            break;
        float4 muv4 = float4(muv, 0.0, 0.0);
        // A lamp is LIGHT, not a thing in the way: the ray picks up the seed it passes
        // through, per tile of path, and keeps going. Treating the seed as a hit (first
        // attempt) put a dark ring round every lamp: a ray grazing the soft edge of the
        // seed took a little light and lost the whole sky behind it.
        float3 emit = tex2Dlod(EmitterSampler, muv4).rgb * EmitterTexScale;
        radiance += transmittance * emit * (EmitterGain * StepTiles);
        // The occluder. Two thirds opaque blocks outright; the softened levels and the
        // mask's own half-shares (a bush, a clump) let a little through.
        float occ = tex2Dlod(OccluderSampler, muv4).a;
        float occHit = saturate((occ - 0.12) / 0.55);
        radiance += transmittance * occHit * HitRadiance;
        transmittance *= 1.0 - occHit;
        t += StepTiles;
    }
    return float4(radiance, transmittance);
}

float4 CascadePS(PixelInput input) : SV_TARGET
{
    float2 texel = floor(input.UV * CascadeTexSize);
    float dirsPerAxis = exp2(CascadeIndex + 1.0);
    float2 probeGrid = floor(ProbeGrid0 / exp2(CascadeIndex) + 0.5);
    float2 block = floor(texel / probeGrid);
    float2 probe = texel - block * probeGrid;
    float dirIndex = block.y * dirsPerAxis + block.x;
    float rayCount = dirsPerAxis * dirsPerAxis;
    float angle = (dirIndex + 0.5) * TAU / rayCount;
    float2 dir = float2(cos(angle), sin(angle));
    float spacing = ProbeSpacingTiles0 * exp2(CascadeIndex);
    float2 probeTile = WindowOriginTiles + (probe + 0.5) * spacing;

    float4 ray = MarchRay(probeTile, dir);
    float3 radiance = ray.rgb;
    float transmittance = ray.a;

    // The sky comes down on whatever is still open at the end of the near field (see
    // SkyCascade); it does not use the transmittance up, because the cascades above
    // still have lamps to deliver along the same open path.
    if (abs(CascadeIndex - SkyCascade) < 0.5)
        radiance += transmittance * MissRadiance;
    [branch]
    if (transmittance > 0.001)
    {
        [branch]
        if (CascadeIndex < CascadeCount - 1.0)
        {
            // Continue in the cascade above: this ray's four children, each read at
            // this probe's position among the upper probes with LINEAR filtering,
            // which is the bilinear over the four surrounding probes. The position is
            // clamped half a texel inside the block so the filter never reads a
            // neighbouring direction's probes.
            float upperDirsPerAxis = dirsPerAxis * 2.0;
            float2 upperGrid = floor(probeGrid * 0.5 + 0.5);
            float2 upperProbeTexel = clamp((probe + 0.5) * 0.5, float2(0.5, 0.5), upperGrid - 0.5);
            float3 upper = float3(0.0, 0.0, 0.0);
            [unroll]
            for (int k = 0; k < 4; k++)
            {
                float upperDir = dirIndex * 4.0 + (float)k;
                float2 upperBlock = float2(fmod(upperDir, upperDirsPerAxis), floor(upperDir / upperDirsPerAxis));
                float2 upperTexel = upperBlock * upperGrid + upperProbeTexel;
                upper += tex2Dlod(UpperSampler, float4(upperTexel / CascadeTexSize, 0.0, 0.0)).rgb;
            }
            radiance += transmittance * upper * 0.25;
        }
        transmittance = 0.0;
    }
    return float4(radiance, transmittance);
}

// Cascade 0 to the lightmap: one texel per probe, the average of its four rays (each
// already carrying everything the cascades above saw in its quarter of the circle).
float4 ResolvePS(PixelInput input) : SV_TARGET
{
    float2 probe = floor(input.UV * ProbeGrid0);
    float3 irradiance = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int k = 0; k < 4; k++)
    {
        float2 block = float2(fmod((float)k, 2.0), floor((float)k / 2.0));
        float2 texel = block * ProbeGrid0 + probe + 0.5;
        irradiance += tex2Dlod(Cascade0Sampler, float4(texel / CascadeTexSize, 0.0, 0.0)).rgb;
    }
    irradiance *= 0.25;
    // A wall or a roof is an elevated surface in a top-down view: the probe inside it
    // models the ground it stands on, but the pixels drawn there are its face and its
    // roof in the open, so they are lifted to the sky's level. Same rule as the flood.
    //
    // And not only the tile-grid solids: a probe standing INSIDE a drawn silhouette (a
    // fence picket, a tree trunk, a placed keg) meets its own occluder at the first step
    // and comes back at the face value - which then multiplies the occluder's OWN art,
    // so every object wore its own shade like paint (measured: fence tiles at sky x 0.5
    // while the flood held them at sky x 0.93). The full mask says "inside something";
    // the gaps between pickets stay unlifted, so the comb of shade on the ground behind
    // a fence survives.
    float2 probeTile = WindowOriginTiles + (probe + 0.5) * ProbeSpacingTiles0;
    float2 baseUv = (probeTile - WindowOriginTiles) / WindowSizeTiles;
    // Alpha is the tile-grid solidity the rays march. GREEN carries the CPU's "enclosed
    // open cell" flag: a walkable slit inside a structure (a doorway column, the open
    // band a collision map leaves mid-building) whose probes would otherwise go near
    // black and smudge the facade art drawn over them. Lift those like the facade; the
    // rays never read G, so light still passes through the slit.
    float4 baseSample = tex2Dlod(BaseSampler, float4(baseUv, 0.0, 0.0));
    float solid = max(baseSample.a, baseSample.g);
    // Half a mask texel off the probe centre: the centre itself lands EXACTLY on a texel
    // boundary (texel 4p+2), where the linear filter balances two texels on a knife edge.
    float2 maskUv = baseUv + 0.5 / (WindowSizeTiles * 8.0);
    float insideSilhouette = tex2Dlod(OccluderSampler, float4(maskUv, 0.0, 0.0)).a;
    // A RAMP, not a verdict: with a hard `> 0.6` the roof-edge and fence-edge probes sat on
    // the threshold and the lift FLICKERED - the whole cascade look seemed to switch off for
    // a moment and come back. Edge coverage now buys a graded share of the facade lift, so a
    // wobble the size of a rounding error moves the answer by a rounding error.
    float inside = smoothstep(0.35, 0.8, max(solid, insideSilhouette));
    irradiance = lerp(irradiance, max(irradiance, LiftRadiance), inside);
    return float4(irradiance * OutputScale, 1.0);
}

technique Cascade { pass P0 { PixelShader = compile PS_SHADERMODEL CascadePS(); } }
technique Resolve { pass P0 { PixelShader = compile PS_SHADERMODEL ResolvePS(); } }
