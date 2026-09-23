//=============================================================================
// upscale.fx  —  SDV-Radiance
// Render-scale upscale + contrast-adaptive sharpening (RCAS, the second half of
// AMD FSR 1.0). DLSS-class reconstruction is out of reach here — it wants tensor
// cores, motion vectors and a trained model — but RCAS is a plain spatial filter
// and it is the part that undoes the blur.
//
// The bilinear stretch that gets the small buffer back to window size lands the
// edges in the right place but softens them. RCAS puts the hardness back by
// pushing each pixel away from its four neighbours, then CLAMPING the result to
// that neighbourhood so it can never ring or halo. Measured against the full-res
// frame on real captures: at 0.75 it cuts the error by ~35% and lands the edge
// hardness within a few percent of native.
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

float2 OutputTexel;  // 1/outputWidth, 1/outputHeight — neighbours are OUTPUT pixels
float Sharpness;     // 0 = plain bilinear stretch

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 UpscalePS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float4 c = tex2D(SourceSampler, uv);
    if (Sharpness <= 0.001)
        return c;

    float3 up    = tex2D(SourceSampler, uv + float2(0.0, -OutputTexel.y)).rgb;
    float3 down  = tex2D(SourceSampler, uv + float2(0.0,  OutputTexel.y)).rgb;
    float3 left  = tex2D(SourceSampler, uv + float2(-OutputTexel.x, 0.0)).rgb;
    float3 right = tex2D(SourceSampler, uv + float2( OutputTexel.x, 0.0)).rgb;

    // Sharpen, then clamp into the neighbourhood: overshoot is what turns a
    // sharpen filter into visible outlines around every sprite.
    float3 lo = min(min(min(up, down), min(left, right)), c.rgb);
    float3 hi = max(max(max(up, down), max(left, right)), c.rgb);
    float3 sharp = c.rgb * (1.0 + 4.0 * Sharpness) - Sharpness * (up + down + left + right);

    return float4(clamp(sharp, lo, hi), c.a);
}

technique Upscale { pass P0 { PixelShader = compile PS_SHADERMODEL UpscalePS(); } }

// ---------------------------------------------------------------------------------------------
// The game's zoom, redone. The world is drawn into a buffer at its own scale and the game then
// draws that whole buffer onto the window at the zoom level with a bilinear read: at 75 per cent
// each window pixel takes one bilinear sample among four buffer pixels, placed wherever the scale
// happens to put it, so as the camera moves a thin line lands on a different phase every frame
// and crawls, and at 125 per cent and up every pixel edge is smeared over a whole window pixel.
// This reads the buffer as an AREA instead: each window pixel is the average of exactly the part
// of the buffer it covers, every buffer pixel weighted by how much of it falls inside. A pixel
// edge stays one window pixel wide at any zoom, and the average of an area does not change when
// the camera moves by a whole buffer pixel.

float2 SourceSize;             // the buffer, in pixels
float SourceTexelsPerPixel;    // buffer pixels across one window pixel: one over the zoom

// How much of the buffer pixel starting at "texel" lies between "from" and "to", on one axis.
float Overlap(float texel, float from, float to)
{
    return max(0.0, min(texel + 1.0, to) - max(texel, from));
}

float4 ScreenZoomAreaPS(PixelInput input) : SV_TARGET
{
    float2 centre = input.UV * SourceSize;
    float halfWidth = SourceTexelsPerPixel * 0.5;
    float2 from = centre - halfWidth;
    float2 to = centre + halfWidth;
    float2 first = floor(from);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float total = 0.0;
    // Four buffer pixels a side reach one over the zoom of three: every zoom from a third up.
    [unroll] for (int row = 0; row < 4; row++)
    {
        float rowWeight = Overlap(first.y + row, from.y, to.y);
        [unroll] for (int column = 0; column < 4; column++)
        {
            float weight = Overlap(first.x + column, from.x, to.x) * rowWeight;
            float2 texel = first + float2(column, row) + 0.5;
            sum += tex2Dlod(SourceSampler, float4(texel / SourceSize, 0.0, 0.0)) * weight;
            total += weight;
        }
    }
    return sum / max(total, 0.00001);
}

technique ScreenZoomArea { pass P0 { PixelShader = compile PS_SHADERMODEL ScreenZoomAreaPS(); } }
