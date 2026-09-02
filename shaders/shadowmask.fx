// The player's shadow, cut by the map. Applied while the shadow's silhouette is drawn into a
// small world-anchored patch (ShadowRenderer.PlayerPatch.cs), before the game's sorted batch
// opens; the patch is then drawn into that batch in floor-row strips like any other shadow.
//
// Why a shader and not geometry: a character's shadow lives in the game's own sprite batch so
// that depth sorts it against people and furniture, and that batch takes no effect of ours. So
// where the shadow ends against something the MAP paints (the saloon counter, a wall) could only
// ever be a rectangle cut at a guessed distance, and every guess left a sliver on the counter's
// front. Here every pixel asks the map itself: the tile it lies on, and the tiles the light
// crossed to reach it.
//
// Two answers, chosen per cast by which way the shadow runs (KeepOnSolid):
//   - running UP the screen, toward a wall whose visible face the same light is lighting, the
//     shadow lands on the wall and climbs it: pixels on the solid tiles are kept, pixels beyond
//     the far side of that run are dropped;
//   - running DOWN the screen, toward a counter whose visible face is in its own shade, there is
//     nothing for the shadow to land on: pixels on and beyond the solid tiles are dropped.
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// The silhouette bake, bound by SpriteBatch.
sampler2D SourceSampler : register(s0);

// One texel per map tile, white where the Buildings layer holds a tile with no Passable
// property (a counter, a wall, a shelf), transparent elsewhere. World-anchored at tile (0,0).
texture SolidTexture;
sampler2D SolidSampler = sampler_state
{
    Texture = <SolidTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = None;
};

float2 SolidMapTiles;    // the map's size in tiles, which is the texture's size
float2 FeetWorld;        // the caster's feet in world pixels
float2 SpriteOrigin;     // the feet within the bake, in texels
float2 SpriteSize;       // the bake's size in texels
float2 Scale;            // the draw's scale (across, stretch)
float Rotation;          // the draw's lean, radians, SpriteBatch's sense (clockwise, y down)
float KeepOnSolid;       // 1 = the shadow climbs the solid run it meets; 0 = it stops at it

struct PixelInput
{
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float SolidAt(float2 worldPx)
{
    float2 tile = floor(worldPx / 64.0);
    return tex2D(SolidSampler, (tile + 0.5) / SolidMapTiles).r;
}

float4 MaskPS(PixelInput input) : SV_TARGET
{
    float4 colour = tex2D(SourceSampler, input.UV) * input.Color;
    // Where this texel of the silhouette landed in the world: the same transform SpriteBatch
    // applied to the quad, so the answer is exact for every pixel of it. A texel above the feet
    // has a negative y here, and with no lean lands up the screen.
    float2 texel = input.UV * SpriteSize - SpriteOrigin;
    float2 scaled = texel * Scale;
    float cs = cos(Rotation), sn = sin(Rotation);
    float2 world = FeetWorld + float2(scaled.x * cs - scaled.y * sn, scaled.x * sn + scaled.y * cs);

    // The tile under the feet never cuts the caster's own shadow: they are standing on it.
    float2 feetTile = floor(FeetWorld / 64.0);
    float2 hereTile = floor(world / 64.0);
    float hereSolid = all(hereTile == feetTile) ? 0.0 : SolidAt(world);

    // What the light crossed on its way from the feet to this pixel: a solid tile, and then
    // open ground again beyond it. Twelve steps over a shadow at most a few tiles long is a
    // step every half tile or better, and a tile is the smallest thing the map can paint.
    float seenSolid = 0.0;
    float exitedAfterSolid = 0.0;
    [unroll]
    for (int i = 1; i < 12; i++)
    {
        float2 p = lerp(FeetWorld, world, i / 12.0);
        float2 tile = floor(p / 64.0);
        float s = all(tile == feetTile) ? 0.0 : SolidAt(p);
        exitedAfterSolid = max(exitedAfterSolid, seenSolid * (1.0 - s));
        seenSolid = max(seenSolid, s);
    }
    float keep = KeepOnSolid > 0.5
        ? 1.0 - exitedAfterSolid
        : 1.0 - max(seenSolid, hereSolid);
    return colour * keep;
}

technique ShadowMask { pass P0 { PixelShader = compile PS_SHADERMODEL MaskPS(); } }
