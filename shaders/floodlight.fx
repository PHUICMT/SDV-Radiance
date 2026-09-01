//=============================================================================
// floodlight.fx  â€”  SDV-Radiance
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
// The OTHER GI model's lightmap (see RadianceCascades.cs): the flood sweep and the
// cascades are interchangeable at this composite and a switch between them is a
// cross-fade, so both maps arrive here and LightMapBlend says how much of each. Its
// own origin and size, because the cascades' map has two texels per tile and is padded
// differently. At 0 or 1 the lerp is exact and the other map is never seen.
texture LightMap2Texture;
sampler2D LightMap2Sampler = sampler_state
{
    Texture = <LightMap2Texture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float2 Map2Origin;       // world tile coordinate of the second lightmap's corner
float2 Map2Size;         // second lightmap size in TILES
float LightMapBlend;     // 0 = LightMapTexture only, 1 = LightMap2Texture only

// Occluders (walls, tree trunks, and fences, bushes and boulders as their own silhouettes) at
// four texels per tile, LINEAR-sampled so the
// per-light shadow march below gets soft penumbra edges for free.
texture OccluderTexture;
sampler2D OccluderSampler = sampler_state
{
    Texture = <OccluderTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
// The tile grid UNDER the mask above: walls, roofs and tree canopies, one cell per tile, no
// silhouettes. The sun shafts read this one. Dapple is what a canopy does to sunlight, and the
// silhouette mask now carries a solid footprint for every keg, fence post and weed, which the
// shaft march took for canopy and answered with a bright square beside each of them.
// The mask above, box-filtered to a half, a quarter and an eighth of its size, as three textures
// of their own. They are the penumbra. A mip chain on the mask was tried first and the softness
// dial did nothing anyone could see: through MonoGame's GLSL path a pixel shader's tex2Dlod level
// arrives as a BIAS on the automatic level, and the automatic level of a mask magnified three and
// a half times over the screen is about -2, so every level asked for landed back at the base.
// Separate textures each read at their own base level, and a lerp between neighbours is a
// continuous blur radius that no compiler gets to reinterpret.
texture OccluderSoft1Texture;
texture OccluderSoft2Texture;
texture OccluderSoft3Texture;
sampler2D OccluderSoft1Sampler = sampler_state { Texture = <OccluderSoft1Texture>; MinFilter = Linear; MagFilter = Linear; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };
sampler2D OccluderSoft2Sampler = sampler_state { Texture = <OccluderSoft2Texture>; MinFilter = Linear; MagFilter = Linear; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };
sampler2D OccluderSoft3Sampler = sampler_state { Texture = <OccluderSoft3Texture>; MinFilter = Linear; MagFilter = Linear; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };

texture OccluderBaseTexture;
sampler2D OccluderBaseSampler = sampler_state
{
    Texture = <OccluderBaseTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// The baked tileable fbm the clouds and the fog already share (NoiseTex on the CPU side) -
// GPU sin()-hash noise has no precision guarantee, so every noise field in this mod samples
// this one texture. Here it supplies the dust motes drifting through the sun shafts.
texture NoiseTexture;
sampler2D NoiseSampler = sampler_state
{
    Texture = <NoiseTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Wrap; AddressV = Wrap;
};

// The cloud stage's blurred density mask from LAST frame (this stage runs first, so last
// frame's sky is the freshest one that exists - it drifts a texel a second, nobody can tell).
// The sun shafts read it so a shaft dies under a cloud and blazes at its sunward edge; the
// cloud stage multiplying its shadow over the finished frame gets the dimming half right on
// its own, but it cannot make a beam BRIGHTER at a gap, which is where crepuscular rays live.
texture CloudMaskTexture;
sampler2D CloudMaskSampler = sampler_state
{
    Texture = <CloudMaskTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float CloudCouple;      // 0..1 eased on the CPU; 0 when the kept mask is stale or clouds are off
float2 CloudMaskShift;  // how far the camera moved since the mask was drawn, in screen UV

float2 TilesPerScreen;   // buffer size in world tiles (w/64, h/64)
float2 WorldTileOffset;  // viewport origin in world tiles, continuous
float2 MapOrigin;        // world tile coordinate of the lightmap's (0,0) cell
float2 MapSize;          // lightmap size in cells
float Strength;          // 0..1 how strongly the flood modulates the scene
float AmbientFloor;      // lower bound so nothing ever goes fully black

float2 OccOrigin;        // world tile coordinate of the occluder mask's (0,0) cell
float2 OccMapSize;       // occluder mask size in cells
float4 LightPosArr[8];   // xy = per-light screen UV, z = 1 when this light is an actual flame
float4 LightColArr[8];   // rgb = colour, w = radius in UV (height units)
float DirectCount;       // how many entries are live

// SECOND TIER: more lights, same pools, no shadow ray. What costs real money per pixel
// is the twelve-tap march each light of the first tier fires at the occluder mask, not
// the slot itself - so the ranked leaders keep their shadows and everything behind them
// still gets its circle of light. A row of shop windows now all light the floor; the
// two or three that matter most are the ones that also cast.
// FORTY, not sixteen. The two tiers together are the shader's whole light budget, and a room
// offering more lights than that budget cannot be lit without evicting some: the ranking then
// hands the last slots back and forth as the camera moves, and every handover is a pool fading
// out here and another fading in there - the "it looks like a light just switched on" report,
// walked to and marked frame by frame in the saloon (29-32 candidates against 24 slots) and in
// town at night (30-50). The cure is a budget the ordinary scene never fills, so the array only
// changes at its far edge, where a light's pool has already tapered to nothing. Each slot below
// is skipped in one branch where its pool cannot touch the pixel, so an empty or distant slot
// costs a test and nothing else.
#define SOFT_LIGHTS 40
float4 SoftPosArr[SOFT_LIGHTS];   // xy = UV, z = flame flag
float4 SoftColArr[SOFT_LIGHTS];
float SoftCount;
float Aspect;            // w/h so light pools stay round
float ShadowStrength;    // 0..1 how dark a fully occluded ray gets
// The ceiling on samples ONE shadow ray may take. 12 is what every release up to 1.6.2 did,
// full stop; 48 is one sample per mask texel, which is what stops a ray stepping over a fence
// post. It is a uniform because it is the only cost in this shader paid per lamp on screen,
// and a machine that cannot afford eight lamps at 48 can afford them at 12.
float MarchStepCeiling = 48.0;
float ShadowCarve;       // 0..1 how much of the GAME's own glow goes with it in shadow (see shadowCarve)
// PENUMBRA. A shadow's edge is not one width: an occluder right beside the pixel cuts a hard
// edge, one far from it a soft one, because a lamp is not a point and its width shows in the
// blur the further the light has travelled past the thing that cut it. Each step of the shadow
// march therefore samples a PAIR straddling the ray, spread by how far that step is from the
// pixel, and their average turns the mask's edge into a ramp of that width. 1 = the tuned look,
// 0 = the hard edge the mask itself has, 2 = twice as soft.
float ShadowSoftness;
// 1 = paint the shadow terms instead of the scene (radiance_debug lampshadow): R = deepest
// occlusion any shadowed light met on its ray to this pixel, G = carve, B = mask under the pixel.
float DebugLampShadow;

// SPRITE RELIEF (SheetNormalCache / SpriteDrawRecorder on the CPU side): the world's sprites
// drawn again with each sheet's normal map, so a lamp beside a tree lights the side of the
// tree that faces it. RG = normal xy (bias 0.5), B = z, A = coverage: zero on bare ground and
// on any sprite without a map, and every term below is zero there, so the picture is
// untouched. The terms are a MODULATION round the flat answer, dot(N, L) minus dot(flat, L):
// a flat normal changes nothing, and the art's own painted lighting is never shaded twice.
texture NormalTexture;
sampler2D NormalSampler = sampler_state
{
    Texture = <NormalTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float ReliefStrength;      // the lamps' lean, 0 = off
float ReliefLampHeight;    // how high a lamp hangs above the ground, in screen heights
float ReliefSunStrength;   // the sun's lean, 0 indoors and after dark
float3 ReliefSunDir;       // toward the sun: screen xy, and up
// RIM LIGHT: the bright fringe along the edge of a sprite that faces a lamp, in that lamp's own
// colour. The lean above is a MODULATION - it can only make a side lighter or darker than the
// art already is - and a modulation cannot put light ON an outline, which is the thing that
// makes a figure read as standing in front of a lamp rather than beside one. This is added
// instead, over the lit result, so it survives on a sprite whose edge is already near black.
//
// The edge is read from the same normal buffer: the maps turn outward at a sprite's outline
// (see normals.fx), so what is left of z is how edge-on a pixel is. No silhouette pass of its
// own - the only screen-space sprite masks this mod builds are for water, and they are not
// baked at all when there is no water on screen.
float RimStrength;         // 0 = off; the dial, already multiplied by the relief's own fade
// COLOUR BLEED: how much of the neighbourhood's hue the bounce field carries (0 = off). See
// the block by FieldTint, which is where the reasoning for it is written down.
float ColourBleed;
// LEAF SHIMMER: what leaves in wind look like at sprite scale is not geometry, it is GLITTER -
// patches of canopy catching and losing the light as leaf faces flip. Same reasoning as the
// water's facet gate, and the same reason the tree shear was abandoned: motion carried by
// BRIGHTNESS cannot tear, because no pixel moves. Masked to the relief coverage (the only
// buffer that knows a pixel belongs to a sprite) AND to green-dominant pixels, so a wall or a
// character with a normal map does not twinkle; a fall canopy dims it, which is the honest
// limit of a colour gate.
float LeafShimmer;       // 0 = off; rides the relief ease, so it needs the relief on
float ShimmerClock;      // Determinism seconds, wrapped on the CPU so a frozen capture stands still

// Room exposure: the time-of-day level of a WINDOWED interior. Deliberately its own
// multiplier and NOT folded into Strength â€” Strength is the GI-relief slider players
// tune low (0.1-0.2 is common), and anything routed through it becomes invisible.
// (1,1,1) outdoors, in mines/volcano and in windowless rooms (caves stay untouched).
float3 Exposure;

// Puts back the colour that dimming flattens out, so a dark room reads as cold rather
// than as grey. 1.0 = untouched (outdoors, caves, midday).
float RoomSaturation;

// 1 only in a room this pass is allowed to restyle - a windowed interior - and 0 everywhere
// else. The saturation lift below was written for such a room and was documented as "1.0 =
// untouched" outdoors, which made the whole block an identity ON PAPER and left nothing
// enforcing it. It was not an identity: measured outdoors at 21:00 in the forest, the frame
// arrived at the lift with blue at 0.27 and red and green at zero - a vanilla night lives
// entirely in the blue channel - and left it with every channel under 0.02 and no colour at
// all. That is the whole "the night is pure black" report, and the same block is where a
// saturation boost once drove channels negative and blacked out a fireplace. A pass that is
// only ever meant to touch interiors should be switched off outdoors, not merely handed
// numbers that ought to add up to no change.
float RoomLookOn;

// The least a hearth's circle on the floor is ever worth, however light the room has
// become. Set by the CPU and non-zero ONLY in a room with windows: it must stay zero
// outdoors, or a street lamp gets a pool at noon again, which is a bug we already fixed
// once. See its use below for why the floor has to exist at all.
float HearthFloor;

// Window light shafts: a sheared beam of daylight falling from each visible window,
// leaning with the same sun the shadows use. Positions are the BOTTOM of the pane.
float2 WindowPosArr[6];  // per-window screen UV
float WindowCount;       // how many entries are live
float3 WindowColour;     // daylight colour x strength (premultiplied, eased on CPU)
float4 WindowBeam;       // x = lean (tiles sideways per tile of drop), y = reach (tiles),
                         // z = half-width at the sill (tiles), w = gain
float4 WindowPane;       // x,y = pane half-size (tiles), z = pane centre above the beam
                         // origin (tiles), w = how much the glass itself glows
float PaneDaylight;      // 1 while there is sky light outside, 0 after dark (eased on CPU)

// SUN SHAFTS, the god-ray pass that finally works the way the name promises. The bright-pass
// approach (streak already-bright pixels toward the light) cannot work in a top-down game: there
// is no sky in the frame, so at the beach everything is equally bright and the streaks cancel,
// and under a canopy nothing passes the threshold at all. Both were measured before this was
// written. What a top-down frame DOES have is the occluder mask - every canopy and wall at tile
// resolution - and a shaft of light is exactly the gap in a canopy, so the mask is marched
// toward the sun instead: blocked path = shade, open path beside a blocked one = a shaft.
// Strength is zero unless god rays AND the sun source are both switched on, outdoors, in
// daylight; the branch below makes the whole block free when it is off.
float2 SunShaftDir;      // tile-space direction the light travels (normalised, leaning like the sun)
float3 SunShaftColour;   // the sun's own colour x strength (premultiplied on CPU)
float SunShaftStrength;
float SunShaftDrift;     // slow time drift so the shafts shimmer instead of standing painted
// How far a canopy's dapple stretches, as a scale on every march distance below. 1.0 is the
// tuned look; the CPU maps the density slider so its DEFAULT lands exactly there, and caps the
// top at 1.1 because the occluder mask is only padded 8 tiles past the screen (FloodOccPad) -
// march past the padding and shafts start appearing as you walk, which is a bug already fixed.
float SunShaftReach;
// The fog stage's own eased day-fog amount (0 on a clear day). Fog is what MAKES a light shaft
// visible in air, so the two effects were measurably strangers: a misty morning drew its haze
// and the shafts underneath stayed the same thin clear-day stripes. On a hazy day the bands
// flatten out (light scattered by mist arrives from everywhere, not in crisp lanes) and the
// flat "air" share of the term grows - fog is the one condition where a bigger flat term reads
// as atmosphere rather than as the murk the window beam's air term taught us to fear.
float SunShaftHaze;
// LAMP SHAFTS. The lamps' god rays were a bright-pass for two years: any pixel bright enough
// near a lamp streaked, which made every pale sprite a light and shipped the effect switched
// off. A shaft is not a bright pixel, it is light that got PAST something, so the lamps now
// use exactly the sun's method inside their own pools: the shadow march already knows whether
// this pixel sees its lamp, and two probes beside that path ask whether something blocks the
// light next door. Open floor beside open floor is evenly lit and shows nothing, which is the
// physics; a gap in a wall, a doorway, a tree beside a street lamp throws a beam. Strength is
// the lamp-ray dial times the CPU's presence ease (weather, daylight, the switch), and 0 costs
// nothing past one branch.
float LampShaftStrength;

// NIGHT LIFT, the other half of the night slider. A multiply-based pass can make a night darker
// than the game's but never lighter: the map is clamped at 1, so with the slider at zero this mod
// simply handed the night back to vanilla, which is itself very dark - and "at minimum it is
// still dark" is exactly how that was reported. Film solved this decades ago: shoot the scene
// underexposed and lift it cool ("day for night"). The ground the game already darkened is lifted
// back up before any lamp pool is added, so lamps keep their tuned brightness and the world
// around them becomes readable instead of black. Zero from the slider's default up.
float NightLift;

// PURKINJE NIGHT: how much colour the unlit outdoors gives up after dark (0 = daytime/indoors).
// Eyes at low light see with rods, which barely tell colours apart and lose red first, and every
// filmed or painted night trades on that: the moonlit world runs cool and drained while anything
// on fire keeps its full colour. The desaturation therefore skips pixels a lamp is reaching -
// lampness gates it - which is what makes a torch at night read as an EVENT instead of a texture.
float NightDesat;

// 1 = paint the emitter test over the world instead of the lit scene (radiance_debug emitter).
// Whether a flame is being recognised as a light source, and how much of it, has been reasoned
// about twice now and reasoned wrong both times. This makes it a thing you can look at.
float DebugEmitter;

// Where the "bright enough to be a light" test starts and where it is fully passed.
//
// These were briefly tunable from the console, while the plan was still to fix a dim room by
// deciding which pixels are a light and sparing them. That plan is gone: the room is dimmed with
// a gamma curve now, which pins 1.0 and needs no such decision. What is left of this test only
// keeps a flame from being drained by the room's own desaturation, and it does that well enough
// at these numbers.
//
// The tuning command went with it, because the test was measurably a poor judge: with the emitter
// overlay on, the Saloon's wall lamps came out as "too dark in the art to be a light" in the same
// frame they were the whitest pixels on screen. Shipping a dial that adjusts a judgement we know
// to be wrong is worse than shipping no dial.
// Lowered from (0.40, 0.80): the gate is flame-only by construction (the emitter term carries
// the flame flag), and at the old numbers the dimmer half of a fire's own art - the embers, the
// log ends, the base of the flame - failed it and was dimmed and desaturated with the room.
// Reported as the fire itself looking dull. The floor-under-a-lamp case the gate guards against
// still fails comfortably: floorboards read around 0.25.
static const float EmitterGateLow = 0.30;
static const float EmitterGateHigh = 0.62;

// Where the output shoulder starts bending (see the end of the pixel shader). Below this the pass
// is the identity; above it the top of the range is compressed toward 1.0 instead of being chopped
// there by the 8-bit target.
//
// This was 0.60, on the reasoning that "nothing outdoors and nothing in a dim room changes". That
// reasoning is wrong, and it was wrong everywhere at once: sunlit grass, sand, a wooden floor and a
// white shirt all sit well above 0.60 in their brightest channel, so most of the daylit world was
// inside the compressor. Doing the arithmetic on what it actually shipped: the range 0.70 to 1.00,
// which is where the highlights of nearly every piece of art in this game live, came out as 0.68 to
// 0.80. Thirty points of range squeezed into twelve. Every surface arrives closer to every other
// surface, which reads exactly as reported - a picture that looks dyed, soft, and short of the
// edges it had - and blaming GI for it was reasonable, because GI is the switch that turns this
// line on.
//
// At 0.85 the curve still catches what it was written for. A pixel asking for 1.5, which is what
// the saloon's red channel did before this existed, lands at 0.97 instead of clipping. A pixel at
// 0.90, which is just a lit floorboard, moves by a hundredth. The compressor now works on
// over-range light, which is what it was for, rather than on ordinary art.
static const float ShoulderKnee = 0.85;

// How far a fire's own circle reaches, in TILES. The boards in front of a hearth, not the far
// wall: this is the distance over which the room's colour hands over to the fire's, and a room
// where that reached the corners would simply be a room with no cast at all.
static const float HearthCircleTiles = 4.5;

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
    // permuted 0..15 pattern via a tiny hash â€” close enough to Bayer for dither use
    return frac(i * 0.0625 + frac(i * 0.381966));
}

// Occlusion at a screen-UV point (linear across tiles â†’ soft shadow edges).
// tex2Dlod: no gradient instructions, so the per-light [branch] stays legal in ps_3_0.
float OccAt(float2 p)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - OccOrigin) / OccMapSize;
    return tex2Dlod(OccluderSampler, float4(muv, 0.0, 0.0)).a;
}

// The mask read with a blur of the given radius in mask texels: 1 is the mask itself, 2 the
// half-size copy, 4 the quarter, 8 the eighth, and anything between a blend of its neighbours.
// Two reads per call. The shadow march uses it for both of its needs: a footprint at least as
// wide as the gap between two steps, so nothing thin falls between them, and the penumbra.
float OccAtBlur(float2 p, float radiusTexels)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float4 muv = float4((wt - OccOrigin) / OccMapSize, 0.0, 0.0);
    float k = clamp(log2(max(radiusTexels, 1.0)), 0.0, 3.0);
    float lo, hi;
    if (k < 1.0)      { lo = tex2Dlod(OccluderSampler, muv).a;      hi = tex2Dlod(OccluderSoft1Sampler, muv).a; }
    else if (k < 2.0) { lo = tex2Dlod(OccluderSoft1Sampler, muv).a; hi = tex2Dlod(OccluderSoft2Sampler, muv).a; }
    else              { lo = tex2Dlod(OccluderSoft2Sampler, muv).a; hi = tex2Dlod(OccluderSoft3Sampler, muv).a; }
    return lerp(lo, hi, frac(min(k, 2.999)));
}

float4 FloodPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 src = tex2D(SourceSampler, uv);
    float4 normalSample = tex2D(NormalSampler, uv);
    float3 normalHere = float3(normalSample.rg * 2.0 - 1.0, normalSample.b);
    float reliefCoverage = normalSample.a * step(0.001, ReliefStrength + ReliefSunStrength);
    // The lamps' lean, summed over every pool that reaches this pixel and applied to the LIT
    // RESULT at the end. Applied inside the pools it rode the GI strength dial (0.1 by default)
    // and measured at 0.1/255 in a lamp-lit town: a relief nobody could see.
    float reliefLamps = 0.0;
    // The rim carries a COLOUR, not a scalar: two lamps of different colours on either side of
    // a tree each light their own edge, and summing them as one number would paint both edges
    // with whichever colour the composite happened to pick.
    float3 rimLight = float3(0.0, 0.0, 0.0);

    // Continuous world-tile position â†’ lightmap UV (cell centres at +0.5).
    float2 wt = uv * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - MapOrigin) / MapSize;
    float3 light = tex2D(LightMapSampler, muv).rgb * 2.0;   // stored Ã—0.5 (glow headroom)
    [branch]
    if (LightMapBlend > 0.0)
    {
        float2 muv2 = (wt - Map2Origin) / Map2Size;
        // tex2Dlod: no gradient, so the branch around it stays legal in ps_3_0.
        light = lerp(light, tex2Dlod(LightMap2Sampler, float4(muv2, 0.0, 0.0)).rgb * 2.0, LightMapBlend);
    }

    // DIRECT light with per-light shadows: each real light adds a round pool whose ray
    // from the light to this pixel is marched against the occluder mask â€” walls, trees
    // and characters block it, so every light Ã— every object casts a soft shadow.
    // (The flood map above carries the sky + the lights' INDIRECT spill.)
    float3 direct = float3(0.0, 0.0, 0.0);
    // The same pools carrying only their SHAPE and their HUE, with the brightness
    // sliders divided out. Whether a fire lays a circle of light on the floor in front
    // of it is not a matter of taste; how bright the mod's lamps burn is. Keeping the
    // two apart is what lets a room we darkened still be answered by its own hearth.
    float3 pool = float3(0.0, 0.0, 0.0);
    // How close this pixel is to sitting ON a light rather than under one. Far tighter than
    // the pool - about three quarters of a tile, roughly the size of the sprite that IS the
    // lamp - and taken as a MAX, not a sum, so a room full of lamps does not add up to an
    // exemption everywhere. See the emitter block further down for what it is for.
    // The same pools again, but only the ones that are FIRE. See the give-back term near the
    // end: a fire keeps lighting the floor after the sun comes up, and a wall lamp does not.
    float3 firePool = float3(0.0, 0.0, 0.0);
    float emitter = 0.0;
    // How much of a FIRE's own circle falls on this pixel. Measured in TILES rather than as a
    // share of the light's radius, because the radius is no guide: the game hands a farmhouse
    // fireplace radius 2.5, which this pass turns into a pool twenty six tiles across, and any
    // fraction of that is still most of a house. See the use below the exposure for what it is
    // for - it is NOT another way to make things brighter.
    float hearthLit = 0.0;
    // The lamps' beams, summed over the shadowed tier. See LampShaftStrength.
    float3 lampShaft = float3(0.0, 0.0, 0.0);
    // How much of some light's pool is blocked at this pixel, the strongest light winning. The
    // game's own glow is in src and cannot be marched: a pool this pass shadows behind a trunk
    // still showed the round glow the game painted there, so the shadow only dimmed what the
    // mod had added and the pool stayed round. This takes part of that glow back.
    float shadowCarve = 0.0;
    float occDebug = 0.0;
    [unroll]
    for (int li = 0; li < 8; li++)
    {
        float on = step((float)li + 0.5, DirectCount);
        float2 lp = LightPosArr[li].xy;
        float4 lc = LightColArr[li];
        float2 dvec = uv - lp;
        dvec.x *= Aspect;
        float dist = length(dvec);
        float att = saturate(1.0 - dist / max(lc.w, 0.02));
        // Softer than a pure square: the mid-pool stays brighter so the light reads as a
        // wide, diffuse glow that fades out gently, instead of a small hot dot.
        att = att * (0.55 + 0.45 * att);
        [branch]
        if (on * att > 0.004)
        {
            // How much of this light's own shadow to apply. Only the first eight lights get a
            // shadow ray at all, and which eight changes as the camera moves, so switching it on
            // and off at the boundary made a pool flip between shadowed and flat in a single
            // frame - reported as a flicker while walking, in town and indoors alike, and traced
            // to here after every other stage was cleared by switching it off and finding the
            // flicker still there. The weight is eased on the CPU and rides in .w, so a light
            // arriving in this tier starts identical to the tier below it and grows a shadow
            // instead of gaining one.
            float shadowW = LightPosArr[li].w;
            float occ = 0.0;
            // THE MARCH STEPS ONE MASK TEXEL AT A TIME. With a fixed count of steps the gap
            // between them grows with the ray, and once it is wider than the thing in the way -
            // a plant, a post, a keg - some rays hit it and their neighbours pass between two
            // steps and miss. Painted raw (radiance_debug lampshadow) that is a fan of plates
            // with dark seams between them at the radii where one step after another first
            // reaches the occluder; in the picture it is a saw-toothed edge that crawls as the
            // light moves. Eight texels to a tile (FloodOccSubdivision), up to 48 steps, and
            // past 48 the read climbs the mip chain so each footprint still spans its step.
            // The penumbra is a mip read too: the further a step is from the pixel, the wider a
            // lamp's own width smears the edge, and a coarser level IS that smear. It replaces
            // five taps across the ray, which could only ever give the edge a handful of levels.
            float distTiles = dist * TilesPerScreen.y;
            float distTexels = distTiles * 8.0;
            float stepCount = clamp(ceil(distTexels), 8.0, max(8.0, MarchStepCeiling));
            float stepTexels = distTexels / stepCount;
            // The blur is decided ONCE per ray, where the ray first meets something. Left to vary
            // step by step, the steps on the occluder's far side (nearer the pixel, so less blur)
            // read it sharp and won the max() every time, and the softness dial did nothing.
            float lockedRadius = -1.0;
            [loop]
            for (int s = 1; s <= (int)stepCount; s++)
            {
                float f = s / stepCount;
                // Samples right beside the light and right beside the pixel are faded out, in
                // TILES rather than as a fraction of the ray, and only a hand's breadth of each:
                // selfOpen and pixelOpen (below) do the rest, and a longer fade left a lit gap
                // between every keg and its shadow.
                float fromLightTiles = f * distTiles;
                float fromPixelTiles = (1.0 - f) * distTiles;
                float wgt = smoothstep(0.10, 0.40, fromLightTiles) * smoothstep(0.0, 0.08, fromPixelTiles);
                float2 onRay = lerp(lp, uv, f);
                // At the dial's 1 a step a tile and a half from the pixel reads at a three-texel
                // blur, the width a lamp's own body smears an edge that far out; at 2 six texels;
                // the ceiling is a whole tile. Wider blur also thins a fence's pickets with
                // distance, which is what a penumbra does to a comb.
                float penumbraTexels = (1.0 - f) * distTiles * 2.0 * ShadowSoftness;
                float radiusHere = max(stepTexels, penumbraTexels);
                float radius = lockedRadius >= 0.0 ? lockedRadius : radiusHere;
                float blocked = OccAtBlur(onRay, radius);
                if (lockedRadius < 0.0 && blocked > 0.2)
                    lockedRadius = radius;
                occ = max(occ, blocked * wgt);
            }
            // A light standing INSIDE something does not get to shadow itself. The farmhouse
            // porch is part of the building's footprint and the mask stamps that footprint
            // solid, so a ring worn while standing on it sent every ray out through an occluder:
            // the carve took the game's own glow with it and the pool went out as the player
            // stepped up onto the boards. The bright core of that pool measured 251 on the path
            // and 132 on the porch.
            //
            // ENCLOSED is the question, and a POINT sample cannot tell it from ADJACENT. A light
            // the player carries sits at the middle of the tile they stand on, so walking into a
            // stove or a keg puts a stamped-solid cell half a tile away: read at one point, the
            // mask goes 0.00 to 0.90 within a quarter of a tile, and since this term multiplies
            // the light's whole accumulated occlusion it took EVERY shadow that lamp cast with
            // it, on every side, the instant you touched anything. Reported as the shadows
            // disappearing on contact while wearing a glow ring, and measured from a pair of
            // captures a single tile apart.
            //
            // A tile-wide read separates the two cases cleanly, because it is the difference
            // between them: inside a footprint every direction is solid and it reads near 1;
            // beside a stove one direction is and it reads about a half. Measured on the mask
            // from that capture, walking the light down the column: 0.06, 0.34, 0.47, 0.61,
            // 0.80, 1.00. So the ends sit where only the enclosed end of that walk crosses.
            //
            // And it never reaches zero. A term that decides rather than fades is what made a
            // quarter tile of movement switch a room's shadows off; the floor keeps a pressed-in
            // light most of its shadows and still spares the porch its pool.
            float selfOpen = lerp(1.0, 0.15, smoothstep(0.55, 0.95, OccAtBlur(lp, 8.0)));
            // And a wall is not shadowed by its own footprint. The mask stamps a building solid
            // across its tiles, but the game draws that building's face and roof OVER the tiles
            // north of them, so a lamp in front of the farmhouse threw the house's shadow across
            // the house itself: the boards went dark as the player stepped off the porch and lit
            // again as they stepped back on. A pixel that IS an occluder keeps the light that
            // reaches its face; the ground in front of it still takes the shadow.
            float pixelOpen = 1.0 - smoothstep(0.35, 0.85, OccAt(uv));
            occ *= selfOpen * pixelOpen;
            // A shadow lives inside its light's reach and thins with it: the contrast of the
            // shadow falls with the pool (att, again) so it is gone where the pool is gone, and
            // ground the game shows as night never gets a wedge cut into it.
            float shadowHere = occ * ShadowStrength * shadowW * att;
            float lit01 = att * (1.0 - shadowHere);
            // Relief: the side of a sprite that faces this lamp leans toward its light, the far
            // side away, round the flat answer (see NormalTexture), as much as the pool reaches.
            [branch]
            if (reliefCoverage > 0.001)
            {
                float3 toLamp = normalize(float3(-dvec.x, -dvec.y, ReliefLampHeight));
                reliefLamps += (dot(normalHere, toLamp) - toLamp.z) * lit01;
            }
            // BLACKBODY WALK, flames only: real firelight is not one colour, it is a gradient -
            // near white at the source, gold a step out, deep warm at the tail. One flat orange
            // over the whole pool is most of why a fire reads as a painted circle instead of a
            // thing that is burning. The core third of the pool walks the lamp's own colour
            // toward white-hot; the tail keeps the colour untouched, so the reach of the pool
            // and everything tuned against it stays exactly where it was.
            float coreT = saturate(1.0 - dist / max(lc.w * 0.30, 0.01));
            coreT = coreT * coreT * LightPosArr[li].z;
            float3 lcol = lerp(lc.rgb, float3(1.06, 0.98, 0.82) * max(max(lc.r, lc.g), lc.b), coreT);
            direct += lcol * lit01;
            // The rim, in this lamp's walked colour and inside this lamp's reach, so a fringe
            // can never be brighter or wider than the pool that is supposed to be casting it.
            [branch]
            if (reliefCoverage > 0.001 && RimStrength > 0.001)
            {
                float3 toLampRim = normalize(float3(-dvec.x, -dvec.y, ReliefLampHeight));
                // How edge-on this pixel is, cubed. Cubed rather than raw because the maps give
                // a gentle slope right across a sprite's interior and a raw term lit the whole
                // face, which is the lean's job; the cube leaves all but the outermost pixels
                // alone. And only the side actually turned toward the lamp.
                float edgeOn = saturate(1.0 - normalHere.z);
                float facing = saturate(dot(normalHere.xy, toLampRim.xy));
                rimLight += lcol * (edgeOn * edgeOn * edgeOn * facing * lit01);
            }
            // Lamp shaft (see LampShaftStrength). Two more rays from the same lamp, to a point a
            // little to each side of this pixel, marched like the shadow ray above with fewer
            // steps and the same fades (a lamp in a wall must not count its own wall). Blocked
            // beside and open here is the edge of a shadow, and that is where a beam is seen:
            // the middle of an open pool has nothing next to it and shows nothing. Bands turn
            // slowly round the lamp, seeded by where it stands so two lamps never breathe in
            // step, and the ring keeps the beam off the sprite that IS the lamp.
            [branch]
            if (LampShaftStrength > 0.004)
            {
                // A gap, not a wall: with ONE blocked side counting, every wall grew a bright band
                // along its lit face (light grazing a counter read as the counter glowing). A
                // beam needs something on BOTH sides within a tile of the ray, and the pair's
                // WEAKER side is what counts. A wider pair was tried for the space between two
                // trees and it made an alley three tiles wide, open all round the player, throw
                // streaks: too far apart to read as a gap, so it is not one here either.
                float2 acrossPixels = normalize(float2(-dvec.y, dvec.x));
                float2 acrossUv = float2(acrossPixels.x / Aspect, acrossPixels.y) / TilesPerScreen.y;
                float2 uvNearL = uv + acrossUv * 0.9, uvNearR = uv - acrossUv * 0.9;
                float occNearL = 0.0, occNearR = 0.0;
                [unroll]
                for (int t = 1; t <= 5; t++)
                {
                    float ft = t / 5.0;
                    float wgtT = smoothstep(0.06, 0.28, ft) * smoothstep(1.02, 0.86, ft);
                    occNearL = max(occNearL, OccAt(lerp(lp, uvNearL, ft)) * wgtT);
                    occNearR = max(occNearR, OccAt(lerp(lp, uvNearR, ft)) * wgtT);
                }
                float gap = min(occNearL, occNearR);
                // Never on the thing that blocks: a roof beside the path is "open" to the march
                // (the lit-side-of-a-wall fade) and took the beam across its tiles.
                float beamEdge = saturate((gap - occ) * 1.6) * (1.0 - OccAt(uv));
                float2 lampTile = lp * TilesPerScreen + WorldTileOffset;
                float beamAngle = atan2(dvec.y, dvec.x);
                // Narrow bright rays with dark air between, not a gentle swell: a soft band read as
                // the pool getting warmer, and only the rays read as light with structure.
                float beamBand = pow(0.5 + 0.5 * sin(beamAngle * 9.0 + SunShaftDrift * 0.7 + dot(lampTile, float2(2.3, 4.1))), 2.5);
                // A beam is seen against dark air, so it lives in the outer half of the pool:
                // right round the lamp everything is lit and a streak there reads as a fault.
                float beamRing = smoothstep(0.5, 1.3, dist * TilesPerScreen.y)
                               * smoothstep(0.30, 0.60, dist / max(lc.w, 0.02));
                // A beam needs an OPEN path: through leaves at half occlusion the pool still
                // glows a little, a beam must not, or a hedge sprays streaks out its far side.
                float openPath = saturate(1.0 - 2.0 * occ);
                // A lamp INSIDE something throws no shafts either (selfOpen, above). Without this
                // the self-shadow cancel worked backwards here: a beam is (gap - occ), so taking
                // occ away from a ring worn on the farmhouse porch turned the whole footprint into
                // one wide gap and the house wore a crown of streaks at dawn.
                lampShaft += lcol * (att * openPath * beamEdge * beamBand * beamRing * shadowW * selfOpen);
            }
            // A HEARTH IS A CIRCLE ON THE FLOOR, NOT A WASH OVER THE ROOM. The reach above
            // is deliberately generous so a single lamp can light a street; borrowing it
            // for the push-back term spread the fire's warmth into every corner, which
            // swallowed the room's own colour whole - a morning meant to be cool blue came
            // out the same orange as noon. Tighter reach, squared falloff: bright on the
            // boards in front of the fire, gone by the far wall.
            // MAX, not a sum. Each light's pool is its hue at full strength, so adding them
            // means a room with eight lamps asks for eight times one lamp's warmth and every
            // channel but blue clips: the saloon came out uniformly orange at every hour, and
            // it had been doing that since at least 1.5.1. Taking the strongest pool at each
            // pixel keeps a single hearth's circle exactly as it was, and stops a row of wall
            // lamps from adding up into a wash. Same reason the emitter term below uses max.
            float attP = saturate(1.0 - dist / max(lc.w * 0.6, 0.02));
            // The carve follows the game's own glow, which sits in the core of the pool and is
            // gone long before the pool's gentle tail: the tighter pool radius, squared. Out where
            // the tail still lights a little there is no glow left to take, so nothing is taken,
            // and ground the game shows as night stays exactly as dark as the game made it.
            // The game's glow falls off about linearly to its edge, which is near the pool's
            // tighter radius, so the carve follows attP itself; squaring it, and thinning it by
            // att again, left a quarter of the glow at most to take and the shadows read as faint
            // at every dial.
            shadowCarve = max(shadowCarve, attP * occ * shadowW);
            occDebug = max(occDebug, occ * shadowW);
            float peak = max(max(lc.r, lc.g), max(lc.b, 0.0001));
            float3 poolHere = (lc.rgb / peak) * (attP * attP) * (1.0 - shadowHere);
            pool = max(pool, poolHere);
            firePool = max(firePool, poolHere * LightPosArr[li].z);
            float attE = saturate(1.0 - dist / max(lc.w * 0.12, 0.004));
            emitter = max(emitter, attE * attE * LightPosArr[li].z);
            float attH = saturate(1.0 - dist * TilesPerScreen.y / HearthCircleTiles);
            hearthLit = max(hearthLit, attH * attH * (1.0 - shadowHere) * LightPosArr[li].z);
        }
    }
    // Second tier: pools only, no ray. Same maths as above with the march left out.
    [unroll]
    for (int si = 0; si < SOFT_LIGHTS; si++)
    {
        float son = step((float)si + 0.5, SoftCount);
        float4 sc = SoftColArr[si];
        float2 sdv = uv - SoftPosArr[si].xy;
        sdv.x *= Aspect;
        float sdist = length(sdv);
        float sa = saturate(1.0 - sdist / max(sc.w, 0.02));
        sa = sa * (0.55 + 0.45 * sa) * son;
        // Every term below is zero when the pool does not reach this pixel (the hearth circle
        // is inside every fire's reach, and only fires have one), so the branch is exact and an
        // empty slot costs a distance test. Without it forty slots would price like forty lights.
        [branch]
        if (sa <= 0.0)
            continue;
        // Same blackbody walk as the shadowed tier above.
        float softCoreT = saturate(1.0 - sdist / max(sc.w * 0.30, 0.01));
        softCoreT = softCoreT * softCoreT * SoftPosArr[si].z;
        [branch]
        if (reliefCoverage > 0.001)
        {
            float3 toSoftLamp = normalize(float3(-sdv.x, -sdv.y, ReliefLampHeight));
            reliefLamps += (dot(normalHere, toSoftLamp) - toSoftLamp.z) * sa;
        }
        direct += lerp(sc.rgb, float3(1.06, 0.98, 0.82) * max(max(sc.r, sc.g), sc.b), softCoreT) * sa;
        float saP = saturate(1.0 - sdist / max(sc.w * 0.6, 0.02));
        float speak = max(max(sc.r, sc.g), max(sc.b, 0.0001));
        float3 softPoolHere = (sc.rgb / speak) * (saP * saP * son);
        pool = max(pool, softPoolHere);
        firePool = max(firePool, softPoolHere * SoftPosArr[si].z);
        float saE = saturate(1.0 - sdist / max(sc.w * 0.12, 0.004));
        emitter = max(emitter, saE * saE * son * SoftPosArr[si].z);
        float saH = saturate(1.0 - sdist * TilesPerScreen.y / HearthCircleTiles);
        hearthLit = max(hearthLit, saH * saH * son * SoftPosArr[si].z);
    }

    // THE BOUNCE FIELD SAYS HOW MUCH LIGHT REACHES A PIXEL. IT DOES NOT GET TO SAY WHAT
    // COLOUR THE ROOM IS.
    //
    // Every seed in the flood grid is warm - lamps and fires are (1.00, 0.83, 0.58), and that
    // is the colour the sweeps then carry into every cell they reach. The field is multiplied
    // over the WHOLE screen, so a blue channel at 0.58 was taking well over a third out of
    // blue everywhere at once while red kept all of itself. That is a dye, not lighting: it
    // reads as an orange wash, and because every surface is pulled toward the same warm axis
    // the differences BETWEEN surfaces shrink with it, so edges soften and the picture goes
    // smooth. Reported in those words - orange, blurry, object outlines fading - and the
    // measurement in the hearth block below already said the same thing from the other side:
    // red +12%, green +10%, blue DOWN 6% against the same room with the mod off.
    //
    // So the field keeps its brightness and gives up most of its hue. The colour of light in
    // this pass now comes from where light actually has a colour: the direct pools, which are
    // per-light, local, shadowed, and fall off within a few tiles of the lamp that owns them.
    // A hearth still lays a warm circle on the boards in front of it; the far wall stops being
    // painted with the hearth's colour because the hearth is nowhere near it.
    //
    // A quarter of the tint is kept rather than none: bounced light IS tinted by what it
    // bounced off, and at zero a warm room lit by warm lamps read as lit by daylight.
    const float FieldTint = 0.25;
    light = lerp(dot(light, float3(0.299, 0.587, 0.114)).xxx, light, FieldTint);

    // AND WHAT IT PICKED UP ON THE WAY.
    //
    // The line above hands the field back as most of a grey, for the reason written out over
    // it: every seed is the same warm colour, and one hue multiplied over the whole screen is
    // a dye. But the hue bounced light really carries is the hue of the SURFACES it bounced
    // off, and that is a different colour at every pixel, so it can never wash the frame the
    // way one seed colour did - a red barn throws red on the ground beside it and nothing at
    // all on the field behind it.
    //
    // Read as the average of a ring a couple of tiles out, which is about as far as bounced
    // light carries in this lightmap, and divided by its own brightness so it carries HUE
    // ONLY: the field's level, which every other term here is tuned against, is left exactly
    // where it was. The ratio is bounded because a saturated dark neighbour divided by its own
    // luma is a very large number - a pure blue pixel would multiply blue by nearly nine.
    //
    // Not inside an [if]: a forced branch cannot hold a tex2D on this profile, because the
    // sampler needs the screen gradients and those are only defined outside one. The switch
    // is folded into the RADIUS instead - at zero all four taps land on the pixel already
    // read into src, which is the cheapest read a texture unit can do, and the lerp below
    // then returns exactly white. Off costs four cache hits and no branch at all.
    float2 ring = (2.0 / TilesPerScreen) * step(0.001, ColourBleed);
    float3 around = src.rgb
                  + tex2D(SourceSampler, clamp(uv + float2( ring.x, 0.0), 0.0, 1.0)).rgb
                  + tex2D(SourceSampler, clamp(uv + float2(-ring.x, 0.0), 0.0, 1.0)).rgb
                  + tex2D(SourceSampler, clamp(uv + float2(0.0,  ring.y), 0.0, 1.0)).rgb
                  + tex2D(SourceSampler, clamp(uv + float2(0.0, -ring.y), 0.0, 1.0)).rgb;
    around *= 0.2;
    float aroundLuma = max(dot(around, float3(0.299, 0.587, 0.114)), 0.004);
    float3 bleedHue = clamp(around / aroundLuma, 0.5, 1.8);
    light *= lerp(float3(1.0, 1.0, 1.0), bleedHue, ColourBleed);

    // Added AFTER the field is neutralised, so a lamp's own colour survives at full strength.
    light += direct;

    // Ordered dither breaks the bilinear ramps of the low-res map into pixel noise.
    float dith = (Bayer(wt * 16.0) - 0.5) * 0.035;

    float3 mul = saturate(light + AmbientFloor + dith);
    float3 lit = src.rgb * lerp(float3(1.0, 1.0, 1.0), mul, Strength);
    // The relief, over the lit result: the lamps' lean (clamped, so a row of lamps does not
    // add up past what one lamp could do) and the sun's, the same modulation with the sun as
    // the light. Zero wherever the buffer has no coverage, so bare ground is untouched.
    float lean = ReliefStrength * 0.8 * clamp(reliefLamps, -1.0, 1.0)
               + ReliefSunStrength * (dot(normalHere, ReliefSunDir) - ReliefSunDir.z);
    lit *= saturate(1.0 + reliefCoverage * lean);
    // ADDED, not multiplied: the whole point is to put light on an outline that the art may
    // have drawn near black, and a multiply of near black is near black.
    lit += rimLight * (RimStrength * reliefCoverage);
    // See LeafShimmer. Two travelling sines make tile-scale patches drifting through the
    // canopy; up to nine percent either way at the dial's top, which reads as leaves turning
    // rather than a light flashing.
    float leafLike = saturate((src.g - max(src.r, src.b)) * 5.0);
    float shimmer = sin(wt.x * 2.3 + wt.y * 3.1 + ShimmerClock * 2.1)
                  * sin(wt.y * 5.3 - ShimmerClock * 3.4 + wt.x * 0.7);
    lit *= 1.0 + reliefCoverage * leafLike * LeafShimmer * shimmer * 0.09;
    // Carve the game's own glow where a light's ray is blocked (see shadowCarve). The dial is
    // how much of that glow goes: at the default a little over half, so a shadowed patch inside
    // a pool drops toward the night around the pool rather than below it.
    // At the dial's top 95% of the glow goes, never all of it: what is left under the glow is
    // the night the game drew, and that stays.
    lit *= 1.0 - shadowCarve * ShadowStrength * (ShadowCarve * 0.95 * Strength);
    // See the param note. Two shapes of lift failed before this one, each on its own arithmetic.
    // A multiply: the vanilla night ground sits near black, and 1.5 times nearly nothing is
    // nearly nothing. A screen blend, x + moon(1-x): it raises a black pixel to a FLAT value, and
    // a flat value laid over the whole frame is a veil - reported within the hour as "why does
    // the screen look whitish", which is the same lesson the window beam's flat air term taught
    // (a big flat term reads as murk, not as light). What a film print actually does is a GAMMA
    // lift: shadows rise a lot, mids a little, highlights barely, every pixel keeps its own
    // texture because the lift is proportional to what is there, and black stays black. Lamp
    // pools are added after, standing on the lifted ground at full strength.
    //
    // AND IT MUST NEVER BE HANDED A ZERO. pow(x, y) is exp2(y * log2(x)) on every backend this
    // ships to, log2(0) is -Inf, and what comes back out of that is not reliably zero: on the
    // machine this was found on it is a NaN. One NaN channel then reaches the shoulder at the end
    // of this shader, where `peak` is a max over all three and every channel is divided by it -
    // so a single bad channel takes the whole pixel to black.
    //
    // Which is exactly what the picture showed. A farmhouse at nine in the evening came out with
    // its fireplace, its paintings and its bed as solid black cutouts while the wall between them
    // was untouched, and the pixels that died had one thing in common: 93% of them had a blue
    // channel of exactly ZERO in the game's own frame, against 1% of the survivors. Deep brick,
    // dark wood, a night sky in a painting - fully saturated art, which is most of what a
    // fireplace is drawn with. Working the same pixel through this shader by hand gives a dark
    // red; the GPU gave black, and a result that disagrees with the arithmetic is a hint about
    // the machine rather than about the maths.
    //
    // max() away from zero rather than clamping the result, because by then the value is a NaN
    // and NaN survives min, max and saturate alike. The branch is the other half: indoors, in a
    // cave and all day outdoors this lift is exactly zero, so the safest pow is the one that
    // never runs. Guarding it was worth doing regardless of which GPU does what with log2(0).
    [branch]
    if (NightLift > 0.0005)
        lit = pow(max(lit, 1e-4), float3(1.0, 1.0, 1.0) - NightLift.xxx);

    // THE GLASS IS NOT PART OF THE ROOM. A pane is a hole with the sky behind it, so the
    // interior exposure must not touch it: multiplying a bright white pane by a dim
    // morning room turned the glass a flat murky grey, which reads as dirty rather than
    // as a window. Inside the pane the exposure returns to neutral and the daylight
    // colour is added on top, so the glass stays the brightest thing in a dark room.
    float pane = 0.0;
    [unroll]
    for (int pi = 0; pi < 6; pi++)
    {
        float pon = step((float)pi + 0.5, WindowCount);
        float2 pd = (uv - WindowPosArr[pi]) * TilesPerScreen;
        pd.y += WindowPane.z;                    // the beam starts below the pane's centre
        float2 q = pd / max(WindowPane.xy, 0.001);
        float r = saturate(1.0 - dot(q, q));
        pane = max(pane, pon * r * r);
    }
    // ...but only while there is daylight on the other side of it. After dark the pane is a
    // dark rectangle in a dark room, and exempting it from the room's exposure left a window
    // still lit at midnight, brighter than the room it was supposed to be letting light into.
    // The exemption follows the sun; the glow term below already fades with WindowColour.
    float paneLit = pane * PaneDaylight;

    // AND NEITHER IS THE FLAME. Exactly the same argument as the glass above, for the thing
    // at the other end of it: a fire is not a surface the room's light falls on, it is where
    // the light comes from, so scaling it by how dark we decided the room should be has no
    // meaning. It was being dimmed by the exposure and then drained by the room's saturation
    // on top, and a hearth came out a muddy brown smear with none of the near-white the
    // sprite is actually drawn with. Reported as the flames looking dull.
    //
    // Two conditions, because either alone is a bug we have already had. Nearly ON the light,
    // not merely lit by it, or every board in front of the fire stops taking the room's
    // colour with it. AND bright in the source art, or a dark floor tile under a lamp gets
    // exempted too - "bright pixel = light source" on its own is what made god rays stream
    // out of a white-painted sign.
    float srcLum = dot(src.rgb, float3(0.299, 0.587, 0.114));
    float emitterLit = emitter * smoothstep(EmitterGateLow, EmitterGateHigh, srcLum);
    float roomExempt = max(paneLit, emitterLit);

    // Time-of-day room level, applied BEFORE the lamp-glow and window-shaft terms so
    // lamps and daylight beams punch through a dark room instead of dimming with it.
    float3 expo = lerp(Exposure, float3(1.0, 1.0, 1.0), roomExempt);

    // A FIRE'S LIGHT IS NOT THE SKY'S.
    //
    // The cast in Exposure is the colour of whatever is lighting the room, which after dark is
    // the sky on the other side of its windows. That is simply not true of the boards in front
    // of a hearth: those are lit by the fire, and painting them the colour of the night sky is
    // saying the fire is not there. It is also the mechanism behind the report - a fireplace
    // came out a black block because the cast cut 45% of the red out of the one warm thing in
    // the room, and the exemption meant to spare it (bright AND near a light) turned out to
    // cover almost nothing: radiance_debug emitter showed the whole hearth green, which is the
    // overlay's way of saying "near a light, not bright enough in the art to count".
    //
    // So where the fire's own circle lands, the cast walks back to neutral - and ONLY the cast.
    // The room's LEVEL is untouched, because the level is what the darkness sliders mean and a
    // term that could brighten a room is a term that will eventually be found repainting a
    // saloon. Nothing here can make a pixel brighter than the same pixel with no cast at all;
    // it can only stop it being tinted. The visible result is a warm circle on a dim floor,
    // which is what a fire in a dark room looks like, and what was asked for by name.
    float expoGrey = dot(expo, float3(0.299, 0.587, 0.114));
    expo = lerp(expo, float3(expoGrey, expoGrey, expoGrey), saturate(hearthLit));

    // The room is dimmed by a straight MULTIPLY, and 1.5.5 put it back to one after 1.5.4 tried
    // to be cleverer than that. The history is worth keeping, because the clever version is an
    // obvious idea that will occur to somebody again:
    //
    //   A multiply takes the same fraction off every pixel, so the brightest thing in the room
    //   loses the most absolute brightness. At a 39% dim a flame at 0.90 falls to 0.55 while the
    //   boards around it go 0.35 to 0.21, and the fire stops being the brightest thing in the
    //   room, which is the one property that makes a flame read as a flame. That is real, and it
    //   is why 1.5.4 replaced this line with a gamma curve.
    //
    //   The gamma curve pinned 1.0 to 1.0 and bent everything below it. What that means in a room
    //   is: anything already at or above 1.0 ignores the dimming completely. Fine for a flame,
    //   which is a handful of pixels. Not fine for a wall lit through a window, which is most of a
    //   farmhouse in the morning, and that whole wall arrived at full white in a room the mod had
    //   just been asked to darken. The exponent, -log2(level), also ran away: at a level of 0.20
    //   it reaches 3.3 and puts a dark floorboard EIGHT times below the multiply.
    //
    //   Three players reported it within a day of 1.5.4, from both ends at once: one sent a
    //   side-by-side of the same farmhouse showing the blown wall on 1.5.4 against 1.5.1 and
    //   confirmed 1.5.3 was correct, another reported the same overexposure independently, and a
    //   third reported the opposite symptom, a morning room as dark as midnight.
    //
    //   A capped version was written and MEASURED before this revert: exponent capped at 1.3 and
    //   the top of the range on a ceiling that moves with the room. In Pierre's at dawn it made
    //   the blown fraction WORSE, 11.3% to 17.7%, because raising the mid-range lifts everything
    //   toward the clip instead of away from it. The curve table said otherwise; the game did not.
    //
    // So: if this is revisited, the thing to fix is that a flame is dimmed with the room, and the
    // fix has to be measured in Pierre's at dawn and a farmhouse in the morning, not just in a
    // dark saloon where every version looks fine.
    lit *= expo;

    // Applied HERE, before the glass, the hearth and the sunbeam are added, so those
    // three - the only light in the picture that is not room light - keep their own
    // colour and stand out against it: a cold blue room with a warm fire and a gold bar
    // of sun is the whole look. Held off the glass, which is not part of the room.
    [branch]
    if (RoomLookOn > 0.5)
    {
        float sat = clamp(lerp(RoomSaturation, 1.0, saturate(roomExempt)), 0.0, 2.0);
        float roomLum = dot(lit, float3(0.299, 0.587, 0.114));
        // A BOOST MUST NOT BE ABLE TO PUSH A CHANNEL THROUGH ZERO, and clamping the RESULT at
        // zero is not the same thing: a channel that lands at zero is just as black as one that
        // lands below it, which is why the fireplace stayed black after the last clamp. Solve for
        // the strongest boost this pixel can actually take instead. Every channel below the
        // pixel's own luminance is pushed further down by the boost, and the deepest of them
        // reaches zero at sat = luminance / (luminance - channel); past that the pixel starts
        // losing content rather than gaining colour. Taking the smaller of the two leaves the
        // lift at full strength on everything with the headroom for it - which is most of a room
        // - and quietly eases off exactly on the dark, low-chroma surfaces that have none.
        // Never below 1.0, since 1.0 is the identity and is always safe.
        float3 below = max(float3(roomLum, roomLum, roomLum) - lit, 1e-5);
        float satSafe = roomLum / max(max(below.r, below.g), below.b);
        sat = min(sat, max(satSafe, 1.0));
        lit = lerp(float3(roomLum, roomLum, roomLum), lit, sat);
    }
    // A LERP THAT EXTRAPOLATES CAN LAND OUTSIDE ITS OWN ENDPOINTS. sat runs above 1.0 by
    // design (it is a BOOST, not a blend) - measured at 1.22 in the room this was found in,
    // which sounds mild and is not: for a channel already below the pixel's own luminance,
    // pushing 22% further past it is enough to cross zero. A warm, saturated wall only loses
    // its weakest channel that way, and still reads as warm - the wall next to a fireplace
    // in this exact room did exactly that and looked fine. A dark, LOW-saturation surface
    // sitting right at that edge in two or three channels at once does not have that
    // headroom, and lost all of them together: a solid black fireplace in an otherwise
    // correctly lit room, traced pixel-by-pixel to this one line - every other term in this
    // shader only adds, and this was the sole place actually capable of the sign flipping.
    // Clamped rather than re-derived, because the boost itself is doing its job everywhere
    // this doesn't happen; it only needs a floor for the cases it overshoots.
    lit = max(lit, 0.0);

    // CAPPED, so the glow reveals the glass instead of replacing it. Uncapped, a window at
    // midday asked for about 1.6 where the display stops at 1: the panes, the bars between
    // them and a good part of the wall around all arrived at the same value and the window
    // became a white ellipse with no window in it. Reported exactly that way, with the note
    // that mornings were still fine, which is the half of the day the sum stays under the
    // ceiling. Letting it climb to the ceiling and no further keeps every difference the art
    // has below that point, so the bars stay dark and the frame keeps its shape.
    float3 paneGlow = src.rgb * WindowColour * (pane * WindowPane.w);
    lit += min(paneGlow, max(1.0 - lit, 0.0));

    // A FIRE IN A ROOM WE DARKENED HAS TO LOOK LIKE IT IS DOING THE LIGHTING. Every
    // other path a light takes to the screen runs through Strength AND the brightness
    // slider, both of which players tune low for a gentle look - a hearth's pool came
    // out near a tenth of what the eye needs, so the flames burned with no circle of
    // light on the boards in front of them. This term uses the SHAPE-only pool above,
    // scaled by exactly how much WE dimmed the room, so it gives back what was taken and
    // no more: outdoors, in caves and in a room at full daylight the exposure is 1, the
    // term is identically zero, and nothing outside a dim interior changes by a pixel.
    // ...but scaling it by OUR dimming alone means it switches itself off exactly as the
    // room fills with morning light: expo climbs toward 1, dim falls to 0, and the fire
    // stops laying any circle at all on boards it is still clearly lighting. Reported as
    // the hearth being "swallowed by the other effects" at six in the morning, which is
    // the hour it happens at. A fire does not stop lighting the floor because the sun
    // came up. The floor keeps it alive in a lit room and is zero outdoors, so the noon
    // street lamp stays glass.
    // MEASURED THE SAME WAY THE DIMMING WAS APPLIED. This asked an arithmetic MEAN of the three
    // channels how dark the room had been made, while the exposure that made it dark is
    // normalised by LUMINANCE - and the two disagree by a lot on a coloured cast. At nine in the
    // evening the exposure is (0.55, 0.79, 1.52): it carries exactly 0.80 of the room's
    // luminance, so 0.20 was taken and 0.20 is what the give-back is owed. The mean of those
    // three is 1.05, so this line answered ZERO, and the whole pool give-back - the term whose
    // entire job is to let a lit room answer its own dimming - was switched off at precisely the
    // hour it exists for. Outdoors the exposure is (1,1,1) either way, so this stays exactly zero
    // there and nothing outside a windowed interior moves.
    float dim = saturate(1.0 - dot(expo, float3(0.299, 0.587, 0.114)));
    // Scaled by Strength like everything else in this pass. It was not, and that made the GI
    // slider a lie in any room with a few lights in it: the saloon has 66, so `pool` saturates
    // across the whole floor and this line added 1.15 x dim x the pixel's OWN colour - about 59%
    // of it at six in the evening - on top of a room the slider had just been asked to calm down.
    // Halving the slider moved the red channel from +27% over vanilla to +19% and nothing else,
    // which is what sent this hunt through saturation, hue, warmth and the colour grade first.
    //
    // Adding a share of the pixel's own colour is also why the room turns ORANGE rather than
    // merely bright: a wall that is mostly red gets mostly red back, so the gap between the
    // channels widens as it brightens even though the hue never moves. Measured in the saloon at
    // noon against the same room with the mod off: red +12%, green +10%, and blue DOWN 6% - the
    // lightmap multiply darkens all three by about the same, and this line hands red most of it
    // back because red is what the boards already were.
    //
    // Light falling on a surface carries the LIGHT's colour, not the surface's, so the added
    // share is pulled part of the way toward its own luminance rather than being a straight copy
    // of the surface. How far is a judgement, and it was calibrated against the room itself
    // rather than picked: measuring the saloon floor at noon, the game's own saturation there is
    // 0.740, and this term reads 0.769 at zero (the room compounding its own colour, seen as
    // "too orange") against 0.715 at a quarter (below the game's own, seen as "milky"). 0.13
    // lands on 0.740, so the mod brightens the room without touching how colourful it is.
    float3 addTint = lerp(src.rgb, dot(src.rgb, float3(0.299, 0.587, 0.114)).xxx, 0.13);
    // THE FLOOR IS FOR FIRES. It was written for one - a hearth must keep laying a circle on
    // the boards after the sun comes up - and then applied to every pool in the pass, which in
    // a room with sixty-six wall lamps means the floor alone repaints the entire room at noon,
    // in a room the pass had decided needed no darkening at all. Measured in the saloon at
    // 12:10 against the same room with the mod off, and reported as the room being far more
    // orange than the game's own. Every pool still gives back exactly what the exposure took;
    // only a fire keeps a floor under that, which is the case the floor was reasoned about.
    float3 give = addTint * (1.15 * Strength);
    lit += give * (saturate(pool) * dim + saturate(firePool) * max(HearthFloor - dim, 0.0));
    // >1 light (lamp cores) adds a soft warm glow rather than clipping at white.
    lit += addTint * saturate(light - 1.0) * 0.45 * Strength;   // same reasoning as the pool above

    // THE FLAME BURNS ABOVE ITS OWN ART. The exemption above stops a fire being dimmed with the
    // room, which gets it back to exactly the sprite's painted brightness and no further - and a
    // fire is not a lit surface, it is the light, so sitting at the same level as a well-lit wall
    // reads as dull. A modest lift on the emitter pixels puts the flame back on top of everything
    // it lights. Src-modulated so the dark pixels inside the fire art stay dark, and safe against
    // the ceiling because the shoulder below rolls anything this pushes over the knee.
    lit += src.rgb * (emitterLit * 0.30);

    // Window shafts: each pane lays a widening patch of daylight across the boards.
    //
    // Worked in TILES, not UV. UV is not isotropic â€” a screen is far wider than it is
    // tall â€” so shearing UV x against UV y tilted the patch by a factor of the aspect
    // ratio (about 2.5x on a widescreen): a lean meant as 0.35 tiles sideways per tile
    // into the room came out near 0.9, and the patch read as a diagonal ribbon rather
    // than light falling from a window. Tile space has no such trap.
    //
    // The light is almost entirely src-MODULATED â€” it brightens the wood it lands on
    // the way sunlight does â€” with only a whisper of flat "air" term: a bigger flat
    // term painted grey haze over the dark floor and read as murk, not as light.
    float shaft = 0.0;
    [unroll]
    for (int wi = 0; wi < 6; wi++)
    {
        float won = step((float)wi + 0.5, WindowCount);
        float2 wd = (uv - WindowPosArr[wi]) * TilesPerScreen;
        float along = wd.y / max(WindowBeam.y, 0.001);
        float x = wd.x - WindowBeam.x * wd.y;
        // Spreads as it falls: a pane-wide band at the sill opening into a pool.
        float hw = max(WindowBeam.z * (1.0 + 1.1 * saturate(along)), 0.001);
        float t = saturate(abs(x) / hw);
        float across = 1.0 - t * t;
        across *= across;                       // soft shoulders, zero at the edge
        // Brightest just inside the room, thinning out to nothing at the far end. A
        // flat core with a quick edge is what reads as a painted stripe.
        float f = saturate(1.0 - along);
        float len = smoothstep(0.0, 0.22, along) * f * (0.3 + 0.7 * f);
        shaft += won * across * len;
    }
    lit += (src.rgb * 1.2 + 0.03) * WindowColour * (shaft * WindowBeam.w);

    // A flame used to be lifted above its own art here, so it could come out brighter than the
    // room it was lighting. That term is gone, and nothing replaced it, because nothing needs to:
    // the gamma curve leaves a flame at 0.84 where the old multiply put it at 0.55, which is
    // already brighter than anything around it. Lifting on top of that only risked the noon street
    // lamp glowing again, and it depended on the same brightness test that judges lamps wrongly.

    // Sun shafts (see the param block). Three parts, all read from the occluder mask in world
    // tiles so nothing swims when the camera moves:
    //   visibility - march up the light's path; any canopy on it means no direct sun here.
    //   edge       - a shaft is only VISIBLE against shade, so it needs blocked neighbours: in
    //                the open the sun lights everything evenly and there is nothing to see,
    //                which is also the physics (no haze, no beam). Sampled beside the path.
    //   stripe     - two world-anchored sine bands along the sun's direction, drifting slowly,
    //                because parallel light through moving leaves arrives banded, and a shaft
    //                with no structure reads as a brightness bug rather than light.
    [branch]
    if (SunShaftStrength > 0.004)
    {
        float blocked = 0.0;
        [unroll]
        for (int ss = 1; ss <= 8; ss++)
        {
            float2 sp = wt - SunShaftDir * (ss * 0.9 * SunShaftReach);
            blocked = max(blocked, tex2Dlod(OccluderBaseSampler, float4((sp - OccOrigin) / OccMapSize, 0.0, 0.0)).a);
        }
        float visibility = 1.0 - blocked;
        float2 sperp = float2(-SunShaftDir.y, SunShaftDir.x);
        // Two pairs of side samples, near and far, so a shaft extends a few tiles out from the
        // canopy that makes it instead of hugging the trunk: the x4 shape probe showed correct
        // slant and placement but streaks a tile wide, pinned to the trees. All four ride the
        // reach scale with the march above, so a short reach tightens the dapple to the canopy
        // and a long one lets it spill, instead of the two halves disagreeing about distance.
        float2 nb = wt - SunShaftDir * (2.0 * SunShaftReach);
        float occNear = max(
            tex2Dlod(OccluderBaseSampler, float4((nb + sperp * (1.5 * SunShaftReach) - OccOrigin) / OccMapSize, 0.0, 0.0)).a,
            tex2Dlod(OccluderBaseSampler, float4((nb - sperp * (1.5 * SunShaftReach) - OccOrigin) / OccMapSize, 0.0, 0.0)).a);
        float occFar = max(
            tex2Dlod(OccluderBaseSampler, float4((nb + sperp * (3.2 * SunShaftReach) - OccOrigin) / OccMapSize, 0.0, 0.0)).a,
            tex2Dlod(OccluderBaseSampler, float4((nb - sperp * (3.2 * SunShaftReach) - OccOrigin) / OccMapSize, 0.0, 0.0)).a);
        float edge = saturate(max(occNear, occFar * 0.7) * 1.6);
        float perpCoord = dot(wt, sperp);
        // One broad band with a whisper of a second: two deep frequencies multiplied together
        // made patchy cells, and a ray that comes and goes along its own length reads as broken
        // light, not as light through leaves. Reported as exactly that, with "is this on
        // purpose". It was not.
        float stripe = (0.55 + 0.45 * sin(perpCoord * 3.9 + SunShaftDrift))
                     * (0.88 + 0.12 * sin(perpCoord * 9.1 - SunShaftDrift * 1.7));
        // Haze (see the param note): mist scatters the light, so the crisp bands flatten toward
        // an even glow and the flat air share grows. On a clear day (haze 0) this is the identity.
        stripe = lerp(stripe, 0.85, SunShaftHaze * 0.6);
        float shaftAir = 0.10 + 0.18 * SunShaftHaze;
        // Cloud coupling (see the CloudMaskTexture note). Branchless on purpose: with the couple
        // at zero both factors collapse to exactly 1 whatever the sampler holds, so a frame with
        // no kept mask, or a stale one, costs two dead taps and changes nothing.
        float2 cuv = uv + CloudMaskShift;
        float cHere = tex2Dlod(CloudMaskSampler, float4(cuv, 0.0, 0.0)).r;
        float2 cupUv = cuv - (SunShaftDir * 3.0) / TilesPerScreen;   // a few tiles toward the sun
        float cUp = tex2Dlod(CloudMaskSampler, float4(cupUv, 0.0, 0.0)).r;
        // Die under the cloud; blaze where clear ground sits just past a cloud's sunward edge -
        // a gap in the clouds is the same shape as a gap in a canopy, and rays live at gaps.
        float cloudGate = (1.0 - saturate(cHere * 1.2) * CloudCouple)
                        * (1.0 + saturate(cUp - cHere) * (1.8 * CloudCouple));
        // Mostly src-modulated with a whisper of flat "air", same reasoning as the window beam.
        // The 3.0 is the measured gain: at 1.0 the shafts were provably drawn and invisible.
        lit += (src.rgb * 0.85 + shaftAir) * SunShaftColour * (visibility * edge * stripe * cloudGate * SunShaftStrength * 3.0);
        // DUST MOTES. A shaft with nothing floating in it reads as a projection on the ground;
        // what sells the air is the dust drifting through the beam, visible only while it is
        // inside one. Two scales of the shared fbm multiplied and thresholded leave sparse
        // specks - the product only clears the bar where both fields peak - drifting on the
        // same slow clock as the stripes. Confined to the shaft (visibility AND edge, the same
        // gates as the beam itself) so open ground and full shade stay clean, and weighted
        // toward the bright bands, which is where lit dust would actually be.
        float2 mdrift = float2(SunShaftDrift * 0.06, SunShaftDrift * 0.11);
        float mfine   = tex2Dlod(NoiseSampler, float4(wt * 1.31 + mdrift, 0.0, 0.0)).r;
        float mcoarse = tex2Dlod(NoiseSampler, float4(wt * 0.37 - mdrift * 0.6, 0.0, 0.0)).r;
        float motes = smoothstep(0.36, 0.47, mfine * mcoarse);
        lit += SunShaftColour * (motes * visibility * edge * (0.4 + 0.6 * stripe) * cloudGate * SunShaftStrength * 1.2);
    }

    // Lamp shafts, gathered in the shadowed light loop. Mostly src-modulated with a whisper of
    // flat air, the same recipe as the sun's; the gain is what made them visible over a lit
    // pool at the default dial, measured against the same frame with the dial at zero.
    [branch]
    if (LampShaftStrength > 0.004)
        lit += (src.rgb * 0.85 + 0.10) * lampShaft * (LampShaftStrength * 3.2);

    // Purkinje: drain colour from night ground a lamp is NOT reaching (see the param note).
    [branch]
    if (NightDesat > 0.004)
    {
        float lampness = saturate(max(max(direct.r, direct.g), direct.b) * 2.0 + emitter);
        float nsat = 1.0 - NightDesat * (1.0 - lampness);
        float nlum = dot(lit, float3(0.299, 0.587, 0.114));
        lit = lerp(float3(nlum, nlum, nlum), lit, nsat);
    }

    // Debug view, last so it wins: RED = this pixel is treated as being the light itself, GREEN =
    // it is near enough a light but not bright enough in the art to qualify. Reading the two apart
    // says which half of the test is the one failing, which is exactly what could not be worked
    // out by staring at a screenshot of a fireplace.
    lit = lerp(lit, float3(emitterLit, saturate(emitter - emitterLit) * 0.6, 0.0), DebugEmitter);

    // Soft shoulder, because everything above adds and nothing above catches the top.
    //
    // The hearth pool and the over-range lamp glow are both ADDED to the source pixel, so a lit
    // room routinely asks for more than 1.0 and the 8-bit target answers by chopping each channel
    // where it lands. Measured in the saloon at six in the evening: 34.5% of the room had its RED
    // channel pinned at 1.0 while BLUE never reached it once, against 0.4% red with the mod off.
    // A third of the room had therefore lost its texture and its colour balance at the same time,
    // which is what reads as "too strong" and why turning the saturation down did not help - the
    // detail was already gone by then.
    //
    // Rolled off on the BRIGHTEST channel with the other two scaled along it, so the three stay in
    // proportion. Clipping them separately is what turns a warm wooden wall into a flat orange one:
    // red arrives at the ceiling first and stops, green follows, blue keeps climbing, and the hue
    // walks toward the nearest primary as the room gets brighter. Below the knee this is exactly
    // the identity, so a dim room and every outdoor scene are untouched.
    float peak = max(max(lit.r, lit.g), lit.b);
    float over = max(peak - ShoulderKnee, 0.0);
    float rolled = min(peak, ShoulderKnee) + over / (1.0 + over / (1.0 - ShoulderKnee));
    lit *= rolled / max(peak, 1e-4);

    if (DebugLampShadow > 0.5)
        return float4(occDebug, shadowCarve, OccAt(uv), 1.0);
    return float4(lit, src.a);
}

technique FloodLight { pass P0 { PixelShader = compile PS_SHADERMODEL FloodPS(); } }
