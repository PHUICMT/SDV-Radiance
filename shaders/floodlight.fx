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

// SECOND TIER: more lights, same pools, no shadow ray. What costs real money per pixel
// is the twelve-tap march each light of the first tier fires at the occluder mask, not
// the slot itself - so the ranked leaders keep their shadows and everything behind them
// still gets its circle of light. A row of shop windows now all light the floor; the
// two or three that matter most are the ones that also cast.
float2 SoftPosArr[16];
float4 SoftColArr[16];
float SoftCount;
float Aspect;            // w/h so light pools stay round
float ShadowStrength;    // 0..1 how dark a fully occluded ray gets

// Room exposure: the time-of-day level of a WINDOWED interior. Deliberately its own
// multiplier and NOT folded into Strength — Strength is the GI-relief slider players
// tune low (0.1-0.2 is common), and anything routed through it becomes invisible.
// (1,1,1) outdoors, in mines/volcano and in windowless rooms (caves stay untouched).
float3 Exposure;

// Puts back the colour that dimming flattens out, so a dark room reads as cold rather
// than as grey. 1.0 = untouched (outdoors, caves, midday).
float RoomSaturation;

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
    // The same pools carrying only their SHAPE and their HUE, with the brightness
    // sliders divided out. Whether a fire lays a circle of light on the floor in front
    // of it is not a matter of taste; how bright the mod's lamps burn is. Keeping the
    // two apart is what lets a room we darkened still be answered by its own hearth.
    float3 pool = float3(0.0, 0.0, 0.0);
    // How close this pixel is to sitting ON a light rather than under one. Far tighter than
    // the pool - about three quarters of a tile, roughly the size of the sprite that IS the
    // lamp - and taken as a MAX, not a sum, so a room full of lamps does not add up to an
    // exemption everywhere. See the emitter block further down for what it is for.
    float emitter = 0.0;
    [unroll]
    for (int li = 0; li < 8; li++)
    {
        float on = step((float)li + 0.5, DirectCount);
        float2 lp = LightPosArr[li];
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
            float lit01 = att * (1.0 - occ * ShadowStrength);
            direct += lc.rgb * lit01;
            // A HEARTH IS A CIRCLE ON THE FLOOR, NOT A WASH OVER THE ROOM. The reach above
            // is deliberately generous so a single lamp can light a street; borrowing it
            // for the push-back term spread the fire's warmth into every corner, which
            // swallowed the room's own colour whole - a morning meant to be cool blue came
            // out the same orange as noon. Tighter reach, squared falloff: bright on the
            // boards in front of the fire, gone by the far wall.
            float attP = saturate(1.0 - dist / max(lc.w * 0.6, 0.02));
            float peak = max(max(lc.r, lc.g), max(lc.b, 0.0001));
            pool += (lc.rgb / peak) * (attP * attP) * (1.0 - occ * ShadowStrength);
            float attE = saturate(1.0 - dist / max(lc.w * 0.12, 0.004));
            emitter = max(emitter, attE * attE);
        }
    }
    // Second tier: pools only, no ray. Same maths as above with the march left out.
    [unroll]
    for (int si = 0; si < 16; si++)
    {
        float son = step((float)si + 0.5, SoftCount);
        float4 sc = SoftColArr[si];
        float2 sdv = uv - SoftPosArr[si];
        sdv.x *= Aspect;
        float sdist = length(sdv);
        float sa = saturate(1.0 - sdist / max(sc.w, 0.02));
        sa = sa * (0.55 + 0.45 * sa) * son;
        direct += sc.rgb * sa;
        float saP = saturate(1.0 - sdist / max(sc.w * 0.6, 0.02));
        float speak = max(max(sc.r, sc.g), max(sc.b, 0.0001));
        pool += (sc.rgb / speak) * (saP * saP * son);
        float saE = saturate(1.0 - sdist / max(sc.w * 0.12, 0.004));
        emitter = max(emitter, saE * saE * son);
    }

    light += direct;

    // Ordered dither breaks the bilinear ramps of the low-res map into pixel noise.
    float dith = (Bayer(wt * 16.0) - 0.5) * 0.035;

    float3 mul = saturate(light + AmbientFloor + dith);
    float3 lit = src.rgb * lerp(float3(1.0, 1.0, 1.0), mul, Strength);

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
    float emitterLit = emitter * smoothstep(0.40, 0.80, srcLum);
    float roomExempt = max(paneLit, emitterLit);

    // Time-of-day room level, applied BEFORE the lamp-glow and window-shaft terms so
    // lamps and daylight beams punch through a dark room instead of dimming with it.
    float3 expo = lerp(Exposure, float3(1.0, 1.0, 1.0), roomExempt);
    lit *= expo;

    // Applied HERE, before the glass, the hearth and the sunbeam are added, so those
    // three - the only light in the picture that is not room light - keep their own
    // colour and stand out against it: a cold blue room with a warm fire and a gold bar
    // of sun is the whole look. Held off the glass, which is not part of the room.
    float sat = lerp(RoomSaturation, 1.0, roomExempt);
    float roomLum = dot(lit, float3(0.299, 0.587, 0.114));
    lit = lerp(float3(roomLum, roomLum, roomLum), lit, sat);

    lit += src.rgb * WindowColour * (pane * WindowPane.w);

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
    float dim = saturate(1.0 - dot(expo, float3(0.3333, 0.3333, 0.3333)));
    lit += src.rgb * saturate(pool) * (1.15 * max(dim, HearthFloor));
    // >1 light (lamp cores) adds a soft warm glow rather than clipping at white.
    lit += src.rgb * saturate(light - 1.0) * 0.45 * Strength;

    // Window shafts: each pane lays a widening patch of daylight across the boards.
    //
    // Worked in TILES, not UV. UV is not isotropic — a screen is far wider than it is
    // tall — so shearing UV x against UV y tilted the patch by a factor of the aspect
    // ratio (about 2.5x on a widescreen): a lean meant as 0.35 tiles sideways per tile
    // into the room came out near 0.9, and the patch read as a diagonal ribbon rather
    // than light falling from a window. Tile space has no such trap.
    //
    // The light is almost entirely src-MODULATED — it brightens the wood it lands on
    // the way sunlight does — with only a whisper of flat "air" term: a bigger flat
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

    return float4(lit, src.a);
}

technique FloodLight { pass P0 { PixelShader = compile PS_SHADERMODEL FloodPS(); } }
