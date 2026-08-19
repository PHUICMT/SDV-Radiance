//=============================================================================
// tiltshift.fx  —  SDV-Radiance
// Tilt-shift depth-of-field: sharp middle band, blurred toward the top and
// bottom of the screen (fake miniature look). Separable Gaussian blur + a
// vertical-position composite between sharp and blurred.
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

texture BlurTexture;
sampler2D BlurSampler = sampler_state
{
    Texture = <BlurTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float2 TexelSize;   // blur step
float TopEdge;      // sharp region starts here from the top (0..1)
float BottomEdge;   // sharp region ends here (0..1)
float Strength;     // max blur mix
float Mode;         // 0 = horizontal bands, 1 = radial around Center
float2 Center;      // radial focus point (screen UV)
float Aspect;       // width/height, to keep the radial focus circular
float RadRadius;    // radial mode: distance where blur starts (size of sharp circle)
float Feather;      // 0..1 width of the sharp->blur transition (higher = softer, no harsh border)

static const int TAPS = 5;
static const float W[5] = { 0.227027, 0.194595, 0.121622, 0.054054, 0.016216 };

// Linear-sampling form of the same kernel: one bilinear fetch between two neighbouring
// texels returns their weighted sum, so 9 taps become 5 fetches. See bloom.fx for the
// derivation and the numeric check.
//
// Only BlurV can use it here. BlurH reads the FULL-RES source into a half-res target,
// so its TexelSize is a destination texel and its taps land two source texels apart -
// bilinear can only merge texels that are adjacent, so there is nothing to merge. Fixing
// that would mean halving the blur radius, which is a look change, not a free one.
static const float LW[2] = { 0.316217, 0.070270 };
static const float LO[2] = { 1.384615, 3.230769 };

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

float4 BlurHPS(PixelInput input) : SV_TARGET
{
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 1; i < TAPS; i++)
    {
        float2 o = float2(TexelSize.x * i, 0.0);
        sum += tex2D(SourceSampler, input.UV + o).rgb * W[i];
        sum += tex2D(SourceSampler, input.UV - o).rgb * W[i];
    }
    return float4(sum, 1.0);
}

float4 BlurVPS(PixelInput input) : SV_TARGET
{
    // Half-res to half-res, so the taps are on adjacent texels and the pairs merge.
    float3 sum = tex2D(SourceSampler, input.UV).rgb * W[0];
    [unroll] for (int i = 0; i < 2; i++)
    {
        float2 o = float2(0.0, TexelSize.y * LO[i]);
        sum += tex2D(SourceSampler, input.UV + o).rgb * LW[i];
        sum += tex2D(SourceSampler, input.UV - o).rgb * LW[i];
    }
    return float4(sum, 1.0);
}

float4 CompositePS(PixelInput input) : SV_TARGET
{
    float4 sharp = tex2D(SourceSampler, input.UV);
    float3 blur = tex2D(BlurSampler, input.UV).rgb;

    float amt;
    if (Mode < 0.5)
    {
        // Horizontal bands: sharp middle, blur toward top & bottom edges.
        float y = input.UV.y;
        float fw = lerp(0.04, 0.35, saturate(Feather));   // transition band width
        float top = 1.0 - smoothstep(TopEdge - fw, TopEdge, y);
        float bottom = smoothstep(BottomEdge, BottomEdge + fw, y);
        amt = saturate(top + bottom);
    }
    else
    {
        // Radial: sharp circle around Center (the player), blur outward. Correct
        // for aspect so the focus stays circular, not an ellipse.
        float2 d = input.UV - Center;
        d.x *= Aspect;
        float dist = length(d);
        amt = smoothstep(RadRadius, RadRadius + lerp(0.12, 0.7, saturate(Feather)), dist);
    }
    amt *= Strength;

    return float4(lerp(sharp.rgb, blur, amt), sharp.a);
}

technique BlurH     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurHPS(); } }
technique BlurV     { pass P0 { PixelShader = compile PS_SHADERMODEL BlurVPS(); } }
technique Composite { pass P0 { PixelShader = compile PS_SHADERMODEL CompositePS(); } }
