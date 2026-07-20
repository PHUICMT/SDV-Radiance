//=============================================================================
// finishing.fx  —  SDV-Radiance
// Camera-lens finishing pass: vignette (darkened edges) + chromatic aberration
// (radial R/B channel split), plus night fireflies (drifting glow motes).
// Runs last, on the fully graded image.
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

float VignetteStrength; // 0 = off .. ~1 = strong edge darkening
float CAStrength;       // chromatic-aberration UV offset scale (already small, e.g. 0..0.03)
float Time;             // seconds (wrapped)
float2 TilesPerScreen;  // buffer size in world tiles (w/64, h/64)
float2 WorldTileOffset; // viewport origin in world tiles, continuous
float NightAmt;         // 0 by day .. 1 deep night — gates fireflies + a touch more vignette
float Fireflies;        // 1 = fireflies on

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

float4 FinishPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;
    float2 dir = uv - 0.5;
    float dist = length(dir);

    // Chromatic aberration: split R/B outward from the center, growing with
    // distance so the frame stays crisp in the middle (like a real lens).
    float2 offset = dir * CAStrength * dist;
    float r = tex2D(SourceSampler, uv + offset).r;
    float4 mid = tex2D(SourceSampler, uv);
    float b = tex2D(SourceSampler, uv - offset).b;
    float3 col = float3(r, mid.g, b);

    // ---- Night fireflies: sparse warm-green glow motes drifting over the ground,
    // world-anchored (they don't swim as the camera pans) and softly blinking like
    // real fireflies. Two offset layers so the scatter looks organic. Faded out near
    // the top of the screen (sky) and gated to night. ----
    if (Fireflies > 0.5 && NightAmt > 0.01)
    {
        float2 wt = uv * TilesPerScreen + WorldTileOffset;
        float3 fcol = float3(1.0, 0.95, 0.55);
        float yfade = smoothstep(0.12, 0.40, uv.y);   // few in the sky band up top
        float glow = 0.0;
        [unroll]
        for (int fi = 0; fi < 2; fi++)
        {
            float2 off = (fi == 0) ? float2(0.0, 0.0) : float2(0.53, 0.31);
            float2 g = wt * 1.3 + off;
            float2 drift = float2(sin(Time * 0.30 + off.x * 11.0), cos(Time * 0.24 + off.y * 9.0)) * 0.18;
            float2 cell = floor(g + drift);
            float2 f = frac(g + drift) - 0.5;
            float h1 = hash(cell + off);
            float h2 = hash(cell + off + float2(5.3, 1.7));
            float has = step(0.90, h1);                              // very sparse
            float2 jit = (float2(h2, frac(h1 * 7.3)) - 0.5) * 0.6;
            float d = length(f - jit);
            float blink = saturate(sin(Time * 1.05 + h1 * 6.2831853) * 1.5);   // slow on/off
            glow += smoothstep(0.10, 0.0, d) * blink * has;
        }
        col += fcol * glow * NightAmt * yfade * 0.9;
    }

    // Vignette: smooth radial falloff, no darkening until past the mid-radius.
    // A touch stronger at night to draw the eye inward.
    float vig = (VignetteStrength + NightAmt * 0.12) * smoothstep(0.35, 0.80, dist);
    col *= (1.0 - vig);

    return float4(col, mid.a);
}

technique Finishing { pass P0 { PixelShader = compile PS_SHADERMODEL FinishPS(); } }
