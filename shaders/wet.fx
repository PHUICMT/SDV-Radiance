//=============================================================================
// wet.fx  -  SDV-Radiance
// Ground in the rain, built from how wet surfaces really behave (Lagarde, "Water
// drop"; ATI ToyShop) and kept to the game's pixel grid:
//   - wet ground is darker AND glossier, darkening with how porous it is (dirt and
//     stone a lot, grass less, wood and paint hardly at all), never darker alone,
//     which reads as dirt or shade rather than rain;
//   - puddles grow where the ground can hold water, from a continuous field in
//     world space (no tile seams, never against a roof, a fence or the grass),
//     lying flat in the 3/4 view, edged in whole art pixels with a darker wet rim;
//   - in a puddle: a dark pane of water with the overcast sky in it, the people
//     and things beside it upside down, and rain rings one art pixel thick;
//   - at night every lamp leaves a long saturated streak toward the viewer on the
//     wet ground, sharper and brighter where it crosses a puddle, broken into rows
//     by the rain the harder it falls.
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

// One texel per TILE for the whole map, built on the CPU when a location is entered:
// R how porous (how much it darkens), G how glossy it gets, B whether it can hold a
// puddle, A how much rain reaches it (0 under roofs and things, part under trees).
// Sampled smoothly between tile centres: a tile's class is one value, and switching it at the
// tile's edge drew every grass border and every sheltered tile as a hard square.
texture TileTexture;
sampler2D TileSampler = sampler_state
{
    Texture = <TileTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
};

// How far each point lies inside ground that can hold water, four samples to a tile,
// in tiles / 8, in red (Color: an Alpha8 texture reads alpha 1 on OpenGL). Sampled
// smoothly, so a puddle's room changes continuously, not per tile.
texture RoomTexture;
sampler2D RoomSampler = sampler_state
{
    Texture = <RoomTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Linear;
    MagFilter = Linear;
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

// The flipped-entity mirror the water reflection bakes: every character and prop drawn
// upside-down at its own feet. Point sampled at the art-pixel centre, so a reflection
// stays as crisp as the sprite it mirrors.
texture ReflectTexture;
sampler2D ReflectSampler = sampler_state
{
    Texture = <ReflectTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = None;
};

float2 TilesPerScreen;   // world tiles spanned by the frame (w/64, h/64)
float2 WorldTileOffset;  // viewport origin in world tiles (fractional)
float2 MapSizeTiles;     // the whole map, in tiles
float2 MaskOrigin;       // water-mask window origin, world tiles
float2 MaskSize;         // water-mask window size, tiles
float SdfValid;
float Wetness;           // 0..1, the ground's dampness on the game-minute clock
float Gloss;             // 0..1, the shine, which dries before the dampness does
float PuddleLevel;       // 0..1, the standing water, which fills and dries slowest
float Rain;              // 0..1, how hard it is raining right now (0 once it stops)
float Strength;          // the wet-ground dial
float PuddleCoverage;    // the puddles dial (0 with Dynamic Reflections installed)
float PuddleMirror;      // the puddle reflection dial
float StreakAmount;
float StreakStone;       // how much a streak catches on the stone tops, 0 an even streak
float StreakRipples;     // how much rain breaks a streak into twinkling dashes
float DryLampLight;      // on a dry night, the share of the wet look the lamps keep on hard ground
float3 SkyTint;          // the overcast sky, already lit by the ambient
float ReflectOn;
float3 MirrorTint;
float Seconds;           // the mod's own clock (frozen captures hold still)
float2 CloudDrift;       // where the reflected clouds have drifted to, art pixels
float NightGlow;         // 0 by day, 1 by night
float StreakLight;       // how much the lights show: night, or the dark of a rainy day
// Every lamp's streak, drawn as one sprite each into a picture of its own before this pass, so
// any number of lights costs what their streaks cover. Eight lights held as an array here cost
// the square of their count: the compiler reads an element by testing every slot in turn.
texture StreakTexture;
sampler2D StreakSampler = sampler_state
{
    Texture = <StreakTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float SceneMirror;       // 1: puddles mirror what stands behind them, from the screen itself
float PixelLook;         // 1: edges, rings and streaks in whole art pixels; 0: smooth, the river's rings
float SpriteMaskOn;      // 1 when the per-frame sprite mask is live

// Every sprite standing in the world this frame (people, animals, trees' canopies, props), so
// a puddle, its rings and the lamp streaks lie on the ground behind them instead of over them.
texture SpriteMaskTexture;
sampler2D SpriteMaskSampler = sampler_state
{
    Texture = <SpriteMaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// The player is not in the sprite mask: the water reads them per pixel from their own bake, and
// so does this. Bounds in screen UV (x0, y0, x1, y1); an inverted box means no player.
float4 PlayerRect;
texture PlayerMaskTexture;
sampler2D PlayerMaskSampler = sampler_state
{
    Texture = <PlayerMaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

// Every read in this file names its level (0: none of these textures has mips, so the picture is
// the same). With the usual gradient reads the compiler could not branch round them: it ran the
// puddle block, its 48-step walk included, for every pixel on screen, puddle or not. Measured on a
// fast card, the pass went from 0.33 to 0.07 ms a frame, with not one pixel of the picture changed.

// Dave Hoskins' hash: no sin, so it holds on phones that compute at half precision.
float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float ValueNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash(cell);
    float b = Hash(cell + float2(1.0, 0.0));
    float c = Hash(cell + float2(0.0, 1.0));
    float d = Hash(cell + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// A rain ring in one grid of cells, art pixels: 1 on the ring's pixels, 0 elsewhere.
float RingAt(float2 artPixel, float cellSize, float rate, float seed)
{
    float2 cell = floor(artPixel / cellSize);
    float phase = Hash(cell + seed);
    float age = frac(Seconds * rate + phase);
    // Not every cell rings every cycle: a quiet patch between drops.
    float falls = step(Hash(cell + seed + floor(Seconds * rate + phase) * 7.13), 0.55 + 0.4 * Rain);
    // The whole ring stays inside its own cell. A ring that crossed the cell's edge was cut off
    // there and left loose arcs, which in whole pixels read as stray glyphs rather than rings.
    float2 centre = (cell + 0.35 + 0.3 * float2(Hash(cell + seed + 3.1), Hash(cell + seed + 5.7))) * cellSize;
    float2 offset = artPixel + 0.5 - centre;
    offset.y *= 1.6;                                   // a ring on the ground is flatter than it is wide
    float radius = age * cellSize * 0.3;
    float onRing = step(abs(length(offset) - radius), 0.55);
    return onRing * (1.0 - age) * falls;
}

// How much this art pixel wants to hold water: big flat-lying blobs (twice as wide as tall,
// like everything on the ground in 3/4), a little ragged at the edge, and nothing where
// there is no room. Compared with the fill threshold by the caller.
float PuddleScore(float2 artPixel)
{
    float2 artTile = artPixel / 16.0;
    float room = tex2Dlod(RoomSampler, float4(artTile / MapSizeTiles, 0.0, 0.0)).r * 8.0;   // tiles from the nearest dry edge
    float macro = ValueNoise(artPixel * float2(1.0 / 56.0, 1.0 / 28.0));
    float detail = ValueNoise(artPixel * float2(1.0 / 7.0, 1.0 / 5.0) + 17.0);
    // The pixel look leaves the fine noise out: at one art pixel a step it frays the edge into
    // loose single pixels, which read as grit rather than as the edge of a pool.
    float score = macro * lerp(0.88, 1.0, PixelLook) + detail * lerp(0.12, 0.0, PixelLook);
    // Water stops short of a wall or a kerb, and not along its straight line: the distance is
    // wobbled by half a tile of slow noise first. Thresholded as it was, a fence's straight
    // distance contour became the puddle's edge, a ruler line down one side of a soft shape.
    float wobble = (ValueNoise(artPixel * float2(1.0 / 12.0, 1.0 / 9.0) + 41.0) - 0.5) * 0.9;
    return score - saturate((1.3 - (room + wobble)) / 0.9) * 0.75;
}

// The river's rain: an impact, then a ring that widens and dies, scattered cells on their
// own clocks over two scales (water.fx). Returns rings and impacts together.
float SmoothRain(float2 worldTile)
{
    float sum = 0.0;
    [unroll]
    for (int ringLayer = 0; ringLayer < 2; ringLayer++)
    {
        float cellScale = (ringLayer == 0) ? 1.5 : 2.3;
        float2 layerOffset = (ringLayer == 0) ? float2(0.0, 0.0) : float2(0.41, 0.77);
        float2 ringGrid = (worldTile + layerOffset) * cellScale;
        float2 ringCell = floor(ringGrid);
        float rateRandom = Hash(ringCell + 3.1);
        float placeRandom = Hash(ringCell + 19.4);
        float cycle = Seconds * (0.55 + 0.5 * rateRandom) + Hash(ringCell + 7.7) * 7.0;
        float phase = frac(cycle);
        float fires = step(1.0 - (0.45 + 0.4 * Rain), Hash(ringCell + floor(cycle) * 0.137 + 5.3));
        float2 dropAt = (float2(rateRandom, placeRandom) - 0.5) * 0.7;
        float2 offset = frac(ringGrid) - 0.5 - dropAt;
        offset.y *= 1.5;
        float toDrop = length(offset);
        float radius = phase * 0.46;
        float thickness = 0.03 + 0.06 * phase;
        float fade = (1.0 - phase) * (1.0 - phase);
        sum += smoothstep(thickness, 0.0, abs(toDrop - radius)) * fade * fires;
        sum += smoothstep(0.055, 0.0, toDrop) * smoothstep(0.14, 0.0, phase) * fires;
    }
    return saturate(sum);
}

// The light a lamp lays on the ground at this pixel: the wet look calls it on its own surface, the
// dry-night pass calls it alone, so both draw the same streak from the same code.
float3 LampStreakLight(float2 uv, float3 srcRgb, float2 continuousArt, float2 artPixel,
                       float wetForStreaks, float wetShare, float puddle, float ground)
{
    // Rows of the streak go missing in rain, as the drops break the surface. The realistic
    // look only dims them, slowly and softly: fast hard gaps read as a broken streak that
    // flickers as you walk, which is the pixel look's business, not this one's.
    float rowNoise = lerp(ValueNoise(float2(continuousArt.y * 0.35, Seconds * 1.2)),
                          Hash(float2(artPixel.y, floor(Seconds * 6.0))), PixelLook);
    float rowSoftness = lerp(0.25, 0.0, PixelLook);
    float rowKeep = smoothstep(0.42 * Rain - rowSoftness, 0.42 * Rain + rowSoftness + 0.0001, rowNoise);
    // The pixel look reads the streak picture at the art pixel's centre, so it steps in
    // whole art pixels like everything else in that look.
    float2 streakUv = lerp(uv, ((artPixel + 0.5) / 16.0 - WorldTileOffset) / TilesPerScreen, PixelLook);
    // The streak shivers sideways, row by row, as the drops ruffle the water it lies on:
    // smooth and slow in the realistic look, in whole art pixels in the pixel look.
    float shiverRows = lerp(ValueNoise(float2(continuousArt.y * 0.4, Seconds * 1.6)),
                            Hash(float2(artPixel.y, floor(Seconds * 5.0))), PixelLook) - 0.5;
    float shiverTiles = shiverRows * (0.04 + 0.1 * Rain) * (0.6 + 0.4 * puddle) * wetShare;
    streakUv.x += shiverTiles / TilesPerScreen.x;
    float3 streaks = tex2Dlod(StreakSampler, float4(streakUv, 0.0, 0.0)).rgb;
    // and its brightness breathes a little with the surface.
    streaks *= 0.85 + 0.3 * ValueNoise(float2(continuousArt.y * 0.15, Seconds * 0.9) + 31.0);
    [branch] if (max(streaks.r, max(streaks.g, streaks.b)) > 0.002)
    {
        // On the stones: a reflection on rough wet stone lives on what stands up, the tops
        // of the cobbles, and the gaps between them stay dark. Read off the art itself: a
        // point brighter than the ground round it is a top. Smooth water mirrors evenly,
        // so a puddle keeps the streak whole.
        float2 stoneStep = 1.5 / (TilesPerScreen * 16.0);
        float3 luma = float3(0.299, 0.587, 0.114);
        float groundAround = dot(tex2Dlod(SourceSampler, float4(uv + float2(stoneStep.x, 0.0), 0.0, 0.0)).rgb
                               + tex2Dlod(SourceSampler, float4(uv - float2(stoneStep.x, 0.0), 0.0, 0.0)).rgb
                               + tex2Dlod(SourceSampler, float4(uv + float2(0.0, stoneStep.y), 0.0, 0.0)).rgb
                               + tex2Dlod(SourceSampler, float4(uv - float2(0.0, stoneStep.y), 0.0, 0.0)).rgb, luma) * 0.25;
        float standsUp = saturate((dot(srcRgb, luma) - groundAround) * 7.0 + 0.5);
        streaks *= lerp(1.0, 0.25 + 1.5 * standsUp, StreakStone * (1.0 - puddle));
        // Ripples: the rain breaks the water into small waves, and each wave catches the
        // lamp for a moment, so the streak becomes a ladder of short sideways dashes that
        // twinkle as the waves pass.
        float waves = ValueNoise(float2(continuousArt.x / 5.0 + Seconds * 0.4, continuousArt.y / 0.9 - Seconds * 2.6));
        float waveCatch = smoothstep(0.35, 0.65, waves);
        streaks *= lerp(1.0, 0.12 + 1.6 * waveCatch, StreakRipples * (0.5 + 0.5 * Rain) * wetShare);
    }
    // The realistic look runs one streak across stone and puddle with only a gentle lift
    // on the water; the pixel look keeps the strong step between them.
    float onWater = lerp(saturate(wetForStreaks * 2.0) * (0.75 + 0.25 * puddle),
                         lerp(0.55 * wetForStreaks, 1.0, puddle), PixelLook);
    float strength = onWater * StreakLight * StreakAmount;
    return streaks * strength * lerp(1.0, rowKeep, lerp(0.35, 0.7, PixelLook)) * ground;
}

float4 WetPS(PixelInput input) : COLOR0
{
    float4 src = tex2Dlod(SourceSampler, float4(input.UV, 0.0, 0.0));
    float2 worldTile = input.UV * TilesPerScreen + WorldTileOffset;
    // Everything that has a shape is worked out at the centre of the art pixel (a game
    // pixel is 4x4 world pixels, 16 to a tile), so edges step in whole art pixels.
    // Pixel look: on the art pixel's centre, so edges step in whole art pixels. Realistic: on
    // the screen pixel itself, so edges are smooth.
    float2 continuousArt = worldTile * 16.0;
    float2 artPixel = floor(continuousArt);
    float2 shapeAt = lerp(continuousArt, artPixel + 0.5, PixelLook);
    float2 artTile = shapeAt / 16.0;
    float onSprite = SpriteMaskOn * step(0.05, tex2Dlod(SpriteMaskSampler, float4(input.UV, 0.0, 0.0)).a);
    float2 playerSpan = max(PlayerRect.zw - PlayerRect.xy, float2(1e-4, 1e-4));
    float2 playerUv = (input.UV - PlayerRect.xy) / playerSpan;
    float insidePlayerBox = step(0.0, playerUv.x) * step(playerUv.x, 1.0) * step(0.0, playerUv.y) * step(playerUv.y, 1.0);
    float onPlayer = step(0.15, tex2Dlod(PlayerMaskSampler, float4(saturate(playerUv), 0.0, 0.0)).a) * insidePlayerBox;
    float ground = 1.0 - max(onSprite, onPlayer);

    float4 tile = tex2Dlod(TileSampler, float4(artTile / MapSizeTiles, 0.0, 0.0));
    float porosity = tile.r;
    float glossy = tile.g;
    float rainReach = tile.a;

    float wet = Wetness * rainReach * ground;
    // Shoreline damp: the last five texels of land before the water.
    float2 maskUV = (worldTile - MaskOrigin) / MaskSize;
    float inWindow = step(0.0, maskUV.x) * step(0.0, maskUV.y) * step(maskUV.x, 1.0) * step(maskUV.y, 1.0);
    float sdfT = (tex2Dlod(SdfSampler, float4(maskUV, 0.0, 0.0)).a - 0.501961) * 63.75;
    float shoreBand = saturate((sdfT + 5.0) / 5.0) * step(-30.5, sdfT) * inWindow * step(0.01, rainReach) * SdfValid;
    // Damp only while there is wet at all: the pass also runs on a dry night for the lamps alone.
    wet = max(wet, shoreBand * (0.35 + 0.65 * Wetness) * saturate(max(Wetness, Rain) * 50.0));

    // ---- puddles: where, and what shape ----
    float fill = PuddleLevel * PuddleCoverage;
    float threshold = 1.02 - fill * 0.5;
    float hasWater = step(0.001, fill);
    float score = PuddleScore(shapeAt);
    // A hard edge for the pixel look, a soft one about a screen pixel wide for the realistic.
    float edgeSoftness = lerp(0.03, 0.0001, PixelLook);
    float puddle = smoothstep(threshold - edgeSoftness, threshold + edgeSoftness, score) * hasWater * ground;
    float rim = saturate(smoothstep(threshold - 0.045 - edgeSoftness * 2.0, threshold - 0.045 + edgeSoftness * 2.0, score) - puddle)
              * hasWater * ground;

    // ---- wet material: darker and richer, by how porous ----
    float wetLook = saturate(wet * Strength * 1.6);
    float grey = dot(src.rgb, float3(0.299, 0.587, 0.114));
    float3 colour = lerp(grey.xxx, src.rgb, 1.0 + 0.16 * wetLook);
    float darken = 0.42 * porosity * wetLook + 0.16 * rim * Strength;
    colour *= 1.0 - darken;
    // The shine of a wet surface seen from above is the overcast sky in it: faint.
    colour = lerp(colour, SkyTint, 0.06 * glossy * Gloss * wetLook);

    // ---- in a puddle ----
    [branch] if (puddle > 0.004)
    {
        float3 dry = colour;
        // A shallow puddle is clear: the stones under it still show, a little darker, a little
        // cooler and glossier. What says "water" is what it mirrors and what moves on it, not an
        // outline. Every edge treatment tried (a light line, a dark band, either way round) read
        // as the lit edge of a raised slab, so the edge is only where the surface stops.
        float3 sky = SkyTint * (0.86 + 0.28 * ValueNoise((shapeAt + CloudDrift) * float2(1.0 / 70.0, 1.0 / 40.0)));
        float daySky = 1.0 - 0.75 * NightGlow;
        float2 artPixelUv = 1.0 / (TilesPerScreen * 16.0);
        // The bed seen through moving water wavers, and smooth water evens out the stones'
        // own light and shade, so they show through softened.
        float2 waver = float2(ValueNoise(shapeAt * float2(1.0 / 5.0, 1.0 / 3.0) + Seconds * 0.8) - 0.5,
                              ValueNoise(shapeAt * float2(1.0 / 4.0, 1.0 / 6.0) + 11.0 - Seconds * 0.7) - 0.5)
                       * (1.0 - PixelLook) * (0.6 + 0.8 * Rain);
        float2 bedUv = input.UV + waver * artPixelUv;
        float3 bedSharp = tex2Dlod(SourceSampler, float4(bedUv, 0.0, 0.0)).rgb;
        float3 around = (tex2Dlod(SourceSampler, float4(bedUv + float2(3.0, 0.0) * artPixelUv, 0.0, 0.0)).rgb
                       + tex2Dlod(SourceSampler, float4(bedUv - float2(3.0, 0.0) * artPixelUv, 0.0, 0.0)).rgb
                       + tex2Dlod(SourceSampler, float4(bedUv + float2(0.0, 3.0) * artPixelUv, 0.0, 0.0)).rgb
                       + tex2Dlod(SourceSampler, float4(bedUv - float2(0.0, 3.0) * artPixelUv, 0.0, 0.0)).rgb) * 0.25;
        // Under water the bed is darker and RICHER, not hazier: a red brick turns a deeper red.
        // Blurring it much and laying sky over it read as a fog floating above the ground.
        // The pixel look keeps the bed's art pixels crisp: a blur there is a smear between them.
        float3 bed = lerp(bedSharp, around, 0.3 * (1.0 - PixelLook)) * (1.0 - darken);
        float bedGrey = dot(bed, float3(0.299, 0.587, 0.114));
        bed = max(lerp(bedGrey.xxx, bed, 1.25), 0.0);
        // Deeper toward the middle: the bed fades a little further from the light. This is the
        // shading a hollow has, which is what takes the puddle off the street's surface.
        float depth = saturate((score - threshold) / 0.12);
        bed *= 1.0 - 0.18 * depth;
        colour = bed * 0.8 + sky * lerp(0.02, 0.06, daySky);

        float rings = 0.0;
        if (Rain > 0.01)
        {
            if (PixelLook > 0.5)
            {
                rings = RingAt(artPixel, 11.0, 0.9, 1.7);
                if (Rain > 0.5)
                    rings = max(rings, RingAt(artPixel + 5.0, 9.0, 1.3, 9.3));
            }
            else
            {
                rings = SmoothRain(worldTile) * (0.6 + 0.4 * Rain);
            }
        }
        float sceneHit = 0.0;
        [branch] if (SceneMirror > 0.5)
        {
            // The pixel look mirrors from the art pixel's centre, so the image in the water is
            // whole art pixels like the ground round it.
            float2 mirrorFrom = lerp(input.UV, ((artPixel + 0.5) / 16.0 - WorldTileOffset) / TilesPerScreen, PixelLook);
            // What stands behind the puddle, mirrored about the line it stands on. Walk up the
            // screen from this point until something stands up out of the ground (a building's
            // wall, a tree's trunk, a person's feet): that is its base, and its mirror image
            // hangs as far below the base as this point is. Nothing within reach: only sky.
            float baseRows = 0.0;
            [loop]
            for (int probeStep = 1; probeStep <= 48; probeStep++)
            {
                // The pixel look asks once per tile column, from the column's middle, so a whole
                // column agrees on where the thing behind stands and its mirror image comes out as
                // one piece; asked per art pixel, neighbouring columns found different bases and the
                // image came out as a scatter of stray pixels.
                float2 probeFrom = lerp(mirrorFrom, float2(((floor(worldTile.x) + 0.5) - WorldTileOffset.x) / TilesPerScreen.x, mirrorFrom.y), PixelLook);
                float2 probeUv = probeFrom - float2(0.0, probeStep * 2.0) * artPixelUv;
                float2 probeTile = probeUv * TilesPerScreen + WorldTileOffset;
                float standsUp = step(tex2Dlod(TileSampler, float4(probeTile / MapSizeTiles, 0.0, 0.0)).a, 0.3)
                               + SpriteMaskOn * step(0.05, tex2Dlod(SpriteMaskSampler, float4(probeUv, 0.0, 0.0)).a);
                float2 probePlayerUv = (probeUv - PlayerRect.xy) / playerSpan;
                standsUp += step(0.15, tex2Dlod(PlayerMaskSampler, float4(saturate(probePlayerUv), 0.0, 0.0)).a)
                          * step(0.0, probePlayerUv.x) * step(probePlayerUv.x, 1.0) * step(0.0, probePlayerUv.y) * step(probePlayerUv.y, 1.0);
                // The first thing met is the base. The reads are at an explicit level, so the loop
                // may stop here: with gradient reads it could not, and ran all 48 steps for every
                // pixel on screen, puddle or not.
                if (standsUp > 0.5)
                {
                    baseRows = probeStep * 2.0;
                    break;
                }
            }
            [branch] if (baseRows > 0.0)
            {
                // Realistic: a smooth sideways wobble. Pixel: the mirrored art holds still and only
                // a passing ring nudges it a whole art pixel. Any row-by-row shift, even in bands
                // of whole pixels, tears pixel art into stripes, and that read as a mess.
                float smoothShift = (ValueNoise(shapeAt * float2(1.0 / 6.0, 1.0 / 2.0) + Seconds * 1.5) - 0.5) * (0.8 + 1.6 * Rain)
                                  + rings * 0.6;
                float rippleShift = lerp(smoothShift, step(0.5, rings), PixelLook);
                float2 mirrorUv = mirrorFrom - float2(-rippleShift, baseRows * 2.0) * artPixelUv;
                float onScreen = smoothstep(0.0, 0.03, mirrorUv.y);
                float3 scene = tex2Dlod(SourceSampler, float4(mirrorUv, 0.0, 0.0)).rgb;
                // Fresnel from above is low, and what it mirrors far from its base is fainter.
                sceneHit = onScreen * (1.0 - 0.5 * saturate(baseRows / 96.0));
                // Past the top of the thing standing there the mirror would show the ground behind
                // it, and ground mirrored over ground in whole pixels is two cobble patterns crossed:
                // grit. The pixel look mirrors only what stands up (a wall, a trunk, a person).
                float2 mirrorTile = mirrorUv * TilesPerScreen + WorldTileOffset;
                float mirroredStandsUp = saturate(step(tex2Dlod(TileSampler, float4(mirrorTile / MapSizeTiles, 0.0, 0.0)).a, 0.3)
                                       + SpriteMaskOn * step(0.05, tex2Dlod(SpriteMaskSampler, float4(mirrorUv, 0.0, 0.0)).a));
                sceneHit *= lerp(1.0, mirroredStandsUp, PixelLook);
                colour = lerp(colour, scene * MirrorTint, sceneHit * 0.55 * PuddleMirror);
            }
        }
        if (ReflectOn > 0.5 && SceneMirror < 0.5)
        {
            // A ring breaks the mirror: the pixel look shifts a whole art pixel, the realistic a
            // fraction of one.
            float2 mirrorTile = artTile + float2(rings / 16.0 * lerp(0.5, 1.0, PixelLook), 0.0);
            float2 mirrorUV = (mirrorTile - WorldTileOffset) / TilesPerScreen;
            float4 mirrored = tex2Dlod(ReflectSampler, float4(mirrorUV, 0.0, 0.0));
            colour = lerp(colour, mirrored.rgb * MirrorTint, saturate(mirrored.a) * 0.9 * PuddleMirror);
        }
        // A ring is the sky caught on the slope of the little wave: lighter.
        colour += (SkyTint * 0.5 + 0.08) * rings * lerp(1.0, 0.6, PixelLook);
        colour = lerp(dry, colour, puddle);
    }

    // ---- lamps on wet ground ----
    [branch] if (StreakLight > 0.01 && StreakAmount > 0.001)
    {
        float wetForStreaks = max(wetLook * glossy * Gloss, puddle);
        // Dry stone and paths still catch a lamp, faintly: a share of what a soaked street shows,
        // on the glossy ground only, so dry grass and soil stay as they are. Dry ground does not
        // ripple or shiver, so those follow only the wet part.
        float wetPart = wetForStreaks;
        wetForStreaks = max(wetForStreaks, DryLampLight * 0.5 * glossy * ground);
        float wetShare = saturate(wetPart / max(wetForStreaks, 0.0001));
        if (wetForStreaks > 0.01)
        {
            colour += LampStreakLight(input.UV, src.rgb, continuousArt, artPixel, wetForStreaks, wetShare, puddle, ground);
        }
    }

    return float4(colour, src.a);
}

// A bone-dry night: no wetness, no puddles, no rain, so every wet term in WetPS is zero and the only
// thing it can add is the lamps' dry streak. This pass does only that, from the same streak code.
// Measured on a fast card, the whole wet look run for a dry night cost 0.3 ms a frame (about 15
// times that on a weak one); an early return inside WetPS did not help, because the compiler hoists
// every texture read above the branch.
float4 DryLampPS(PixelInput input) : COLOR0
{
    float4 src = tex2Dlod(SourceSampler, float4(input.UV, 0.0, 0.0));
    float2 worldTile = input.UV * TilesPerScreen + WorldTileOffset;
    float2 continuousArt = worldTile * 16.0;
    float2 artPixel = floor(continuousArt);
    float2 shapeAt = lerp(continuousArt, artPixel + 0.5, PixelLook);
    float2 artTile = shapeAt / 16.0;
    float onSprite = SpriteMaskOn * step(0.05, tex2Dlod(SpriteMaskSampler, float4(input.UV, 0.0, 0.0)).a);
    float2 playerSpan = max(PlayerRect.zw - PlayerRect.xy, float2(1e-4, 1e-4));
    float2 playerUv = (input.UV - PlayerRect.xy) / playerSpan;
    float insidePlayerBox = step(0.0, playerUv.x) * step(playerUv.x, 1.0) * step(0.0, playerUv.y) * step(playerUv.y, 1.0);
    float onPlayer = step(0.15, tex2Dlod(PlayerMaskSampler, float4(saturate(playerUv), 0.0, 0.0)).a) * insidePlayerBox;
    float ground = 1.0 - max(onSprite, onPlayer);
    float glossy = tex2Dlod(TileSampler, float4(artTile / MapSizeTiles, 0.0, 0.0)).g;
    float wetForStreaks = DryLampLight * 0.5 * glossy * ground;
    float3 colour = src.rgb;
    if (StreakLight > 0.01 && StreakAmount > 0.001 && wetForStreaks > 0.01)
        colour += LampStreakLight(input.UV, src.rgb, continuousArt, artPixel, wetForStreaks, 0.0, 0.0, ground);
    return float4(colour, src.a);
}

technique WetWorld { pass P0 { PixelShader = compile PS_SHADERMODEL WetPS(); } }
technique WetDryLamps { pass DryStreaks { PixelShader = compile PS_SHADERMODEL DryLampPS(); } }
