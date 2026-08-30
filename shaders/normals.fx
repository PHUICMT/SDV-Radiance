//=============================================================================
// normals.fx  -  SDV-Radiance
// A normal map for a whole sprite sheet, made from the sheet itself. The game's
// art carries no normals, so they are read off two things every sprite does have:
// its SILHOUETTE (the alpha edge, which gives the rounded bevel a pixel-art sprite
// reads as having) and its PAINTED SHADING (a luminance gradient, which is where
// the artist put the form). Both are Sobel gradients; the bevel takes two rings
// so it is two texels wide rather than a hairline. Point-sampled at the sheet's
// own texel size, one output texel per sheet texel, so a sprite drawn from any
// source rectangle of the sheet reads its normals from the same rectangle.
//
// Output: RG = normal xy (bias 0.5), B = normal z, A = the sheet's own alpha, so
// the replay covers exactly what the art covers. FlipX = 1 mirrors x for sprites
// the game draws with SpriteEffects.FlipHorizontally, because a mirrored normal
// map still has to lean the right way.
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

sampler2D SheetSampler : register(s0);

float2 TexelSize;       // 1 / sheet size
float BevelStrength;    // how steeply the silhouette edge leans (2.0 tuned)
float ReliefStrength;   // how much painted shading leans the interior (0.6 tuned)
float FlipX;            // 1 = mirror the x component

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 At(float2 uv, float dx, float dy)
{
    return tex2D(SheetSampler, uv + float2(dx, dy) * TexelSize);
}

float Lum(float4 c)
{
    // Premultiplied by alpha so the transparent surround reads as dark, not as whatever
    // colour the sheet happens to store there.
    return dot(c.rgb, float3(0.299, 0.587, 0.114)) * c.a;
}

float4 NormalsPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 centre = tex2D(SheetSampler, uv);
    if (centre.a < 0.02)
        return float4(0.5, 0.5, 1.0, 0.0);

    // Sobel over alpha at radius 1 and 2: the outer ring at half weight widens the bevel
    // to two texels without turning the whole sprite into a dome.
    float gx = 0.0, gy = 0.0;
    [unroll]
    for (int r = 1; r <= 2; r++)
    {
        float d = (float)r;
        float w = r == 1 ? 1.0 : 0.5;
        float aL = At(uv, -d, 0.0).a, aR = At(uv, d, 0.0).a;
        float aU = At(uv, 0.0, -d).a, aD = At(uv, 0.0, d).a;
        float aUL = At(uv, -d, -d).a, aUR = At(uv, d, -d).a;
        float aDL = At(uv, -d, d).a, aDR = At(uv, d, d).a;
        gx += w * ((aUR + 2.0 * aR + aDR) - (aUL + 2.0 * aL + aDL));
        gy += w * ((aDL + 2.0 * aD + aDR) - (aUL + 2.0 * aU + aUR));
    }
    // Painted shading, radius 1 only: the detail is a texel wide.
    float lL = Lum(At(uv, -1.0, 0.0)), lR = Lum(At(uv, 1.0, 0.0));
    float lU = Lum(At(uv, 0.0, -1.0)), lD = Lum(At(uv, 0.0, 1.0));
    float lUL = Lum(At(uv, -1.0, -1.0)), lUR = Lum(At(uv, 1.0, -1.0));
    float lDL = Lum(At(uv, -1.0, 1.0)), lDR = Lum(At(uv, 1.0, 1.0));
    float lx = (lUR + 2.0 * lR + lDR) - (lUL + 2.0 * lL + lDL);
    float ly = (lDL + 2.0 * lD + lDR) - (lUL + 2.0 * lU + lUR);

    // The surface leans TOWARD the transparent side (a positive alpha gradient to the right
    // means the edge is on the left, so the normal points left) and toward the brighter side
    // of the painted shading (the lit side faces the light).
    float nx = -gx * BevelStrength * 0.25 + lx * ReliefStrength * 0.25;
    float ny = -gy * BevelStrength * 0.25 + ly * ReliefStrength * 0.25;
    // Screen y grows downward; the normal's y is kept in the same convention.
    float3 n = normalize(float3(nx, ny, 1.0));
    if (FlipX > 0.5)
        n.x = -n.x;
    return float4(n.x * 0.5 + 0.5, n.y * 0.5 + 0.5, n.z, centre.a);
}

technique Normals { pass P0 { PixelShader = compile PS_SHADERMODEL NormalsPS(); } }
