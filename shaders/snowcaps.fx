//=============================================================================
// snowcaps.fx  -  SDV-Radiance
// Snow lying on the tops of things in winter: a fence rail, a chest, a keg, a scarecrow's hat,
// a building's sprite. The game already paints snow into its winter trees, bushes and roofs; the
// things a player places are drawn in their summer colours all year, and that is what this adds.
//
// Read off the sprite mask's colour channels, which the mask keeps beside its alpha: green falls
// to zero where a placed thing or a building stands, blue where a person or an animal does. A cap
// is the top edge of a thing, one or two art pixels thick, never where a body covers it.
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

texture SpriteMaskTexture;
sampler2D SpriteMaskSampler = sampler_state
{
    Texture = <SpriteMaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float2 ArtPixelUv;       // one art pixel (a quarter of a tile's sixteenth), in screen UV
float2 TilesPerScreen;   // world tiles across the frame
float2 WorldTileOffset;  // viewport origin in world tiles
float Strength;          // the dial times the stage's fade, 0..1
float3 SnowLit;          // the colour fresh snow takes in this light (white, dimmed and tinted by the scene)

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// A placed thing stands here: its green fell under a half.
float ThingAt(float2 uv)
{
    return step(tex2Dlod(SpriteMaskSampler, float4(uv, 0.0, 0.0)).g, 0.5);
}

float BodyAt(float2 uv)
{
    return step(tex2Dlod(SpriteMaskSampler, float4(uv, 0.0, 0.0)).b, 0.5);
}

float4 SnowCapsPS(PixelInput input) : SV_TARGET
{
    float4 src = tex2Dlod(SourceSampler, float4(input.UV, 0.0, 0.0));
    // Read at this very pixel, a whole art pixel and two above it: the things the mask holds stand
    // on the art grid already, so the cap follows their own steps. The column the snow is decided
    // by is a world one, so a gap in it stays where it is as the camera moves.
    float2 centre = input.UV;
    float2 artPixel = floor((input.UV * TilesPerScreen + WorldTileOffset) * 16.0);
    float here = ThingAt(centre);
    float above1 = ThingAt(centre - float2(0.0, ArtPixelUv.y));
    float above2 = ThingAt(centre - float2(0.0, 2.0 * ArtPixelUv.y));
    // Snow is one art pixel deep everywhere and two on most columns, picked per world column so
    // the line wanders the way real snow does and stays put as the camera moves.
    float deep = step(0.35, Hash(float2(artPixel.x, 7.0)));
    float cap = here * (1.0 - above1) + here * above1 * (1.0 - above2) * deep;
    // A few columns stay bare: a gust, a warm spot, a drip. Without them the line read as an outline.
    cap *= step(0.12, Hash(float2(artPixel.x, 31.0)));
    // A person or an animal in front of the thing keeps their own colours.
    cap *= 1.0 - BodyAt(centre);
    // Art that is already white and colourless (the game's own snow) is left as it is.
    float luma = dot(src.rgb, float3(0.299, 0.587, 0.114));
    float chroma = max(src.r, max(src.g, src.b)) - min(src.r, min(src.g, src.b));
    cap *= 1.0 - smoothstep(0.75, 0.9, luma) * (1.0 - smoothstep(0.06, 0.15, chroma));

    float3 snow = SnowLit * lerp(0.9, 1.0, Hash(artPixel));
    return float4(lerp(src.rgb, snow, cap * Strength), src.a);
}

technique SnowCaps { pass Snow { PixelShader = compile PS_SHADERMODEL SnowCapsPS(); } }
