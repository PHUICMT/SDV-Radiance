//=============================================================================
// water.fx  —  SDV-Radiance
// Ripple + specular sparkle applied ONLY to water tiles. A per-tile mask (built
// on the CPU from GameLocation.isWaterTile and aligned to the viewport) tells
// the shader which pixels are water, refined by a blue-dominance test so banks
// and rocks inside a water tile stay untouched. The game keeps driving the
// water's own vertical scroll; this layers smooth surface detail on top.
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

texture MaskTexture;
sampler2D MaskSampler = sampler_state
{
    Texture = <MaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None; // point = crisp per-tile, no bleed onto land
    AddressU = Clamp; AddressV = Clamp;
};
// (MaskCoreTexture and its two samplers lived here. They fed the old 34-tap shoreline
// march, which was replaced by a single lookup into the precomputed waterline map -
// see EdgeDistAt below. Nothing has read them since, so the whole chain that built and
// uploaded that texture every mask rebuild has gone with them.)
sampler2D MaskLinearSampler = sampler_state
{
    Texture = <MaskTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
// Signed distance to the effect shoreline, chamfer-baked on the CPU alongside the mask:
// alpha = 128 at the waterline, +4 per texel into the water, -4 per texel onto land
// (±31.75 texels of range). One field drives the quantized edge, the foam band and the
// wet ground rim. Linear: the encoding is a distance, so interpolation is meaningful.
// The same distance, measured on the water as it would be with nothing standing in it, so
// its only edges are where water meets real land. Foam reads this one: a bridge is a hole in
// the effect mask, a hole has an edge, and the foam band asks nothing except how far the
// nearest edge is - which is how a bridge grew a drifting lap line down both its sides.
texture RealShoreSdfTexture;
sampler2D RealShoreSdfSampler = sampler_state
{
    Texture = <RealShoreSdfTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

texture SdfTexture;
sampler2D SdfSampler = sampler_state
{
    Texture = <SdfTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

// The caustic net, baked offline (tools/make-caustics.py): a tileable Voronoi ridge. Wrap
// addressing because it is sampled in world space - the net is painted on the bed and the
// camera merely looks at it.
texture CausticTexture;
sampler2D CausticSampler = sampler_state
{
    Texture = <CausticTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Wrap; AddressV = Wrap;
};
float CausticAmt;        // 0 = the term vanishes; strength, weather, night and the toggle's
                         // ease all folded in on the CPU
float CausticDeepFloor;  // what little survives in open water, far from any shore
float DebugCaustic;      // 1 = paint the caustic term as pure red instead of adding it (radiance_debug caustic)

float Presence;         // 0..1 whole-pass presence fade. The CPU already scales Strength,
                        // Sparkle, TintAmt and ReflectStrength by it, but several terms below
                        // (foam, the sky sheen, the moon and lamp glimmers, the lava pulse) are
                        // gated only by the mask, so the pass kept its full look all the way down
                        // to a fade of 0.02 and then vanished in one frame when the stage left the
                        // list - measured as an 8% jump in whole-frame brightness. Blending the
                        // finished result back toward the untouched pixel makes the fade mean what
                        // it says for EVERY term, including any added later.
float Time;             // seconds
float Strength;         // ripple amplitude (UV units are scaled inside)
float Speed;            // ripple animation speed
float Sparkle;          // specular glint intensity
float2 TilesPerScreen;  // how many world tiles span the buffer (w/64, h/64)
float2 WorldTileOffset; // viewport origin in world tiles (viewport.XY / 64), continuous
float2 MaskSize;        // mask texture size in texels (tiles)
float2 MaskOrigin;      // mask window origin in world tiles — the window is PADDED past
                        // the viewport (2 left/right, 4 above), so this is NOT
                        // floor(WorldTileOffset); the CPU passes the true origin.
float WaterKind;        // 0 = still (pond/river), 1 = ocean/beach (big directional swell)
float ReflectStrength;  // 0 = off; screen-space reflection of the scene above the surface
float SparkleDensity;   // ~0.2–2: glint count per area; glint size follows inversely
float SunWarm;          // 0–1 golden-hour factor: sparkle + sheen turn warm at low sun
float NightGlow;        // 0–1 after dusk: star reflections + lamp glimmer fade in
float MoonGlow;         // 0–1 lunar phase × season × clouds: moonlit swell shimmer
float RainAmt;
float RainRingDensity;  // how many strikes, against the amount the rain brings on its own
float RainRingSize;     // how wide one ring grows before it dies
float RainRingStrength; // how plainly the rings and their impacts show          // 0–1 raining: expanding drop rings on the surface
float4 Lights[8];       // xy = screen UV, z = radius (unused), w = intensity
float LightCount;       // how many entries of Lights are live
float PlayerInWater;    // 0..1 eased: the player's feet are on water pixels (wading). C# fades
                        // this in/out over ~a third of a second so the self-reflection never pops.
float TintAmt;          // depth-tint amount (0 when the shimmer toggle is off; reflection may still run)
float3 SkyColor;        // synthesised sky tint: day/golden-hour/night/overcast, ambient-scaled (C#)
float4 PlayerRect;      // player silhouette bounds in screen UV (x0,y0,x1,y1)
float SpriteMaskOn;     // 1 when the per-frame sprite mask below is live
// Per-frame mask of every sprite ON the water (NPCs, farm animals, critters) baked in
// screen space before the world draws — their pixels are excluded from ripple/mirror so
// a duck paddling a pond never distorts, and displaced taps can't smear them sideways.
texture SpriteMaskTexture;
sampler2D SpriteMaskSampler = sampler_state
{
    Texture = <SpriteMaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
texture PlayerMaskTexture;   // the player's baked silhouette — its alpha marks the
                             // player's ACTUAL pixels (not a box) to exclude from
                             // ring-tile effects, so water beside them keeps animating
sampler2D PlayerMaskSampler = sampler_state
{
    Texture = <PlayerMaskTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float ReflectRTOn;       // 1 when the flipped-entity reflection layer below is live
float ReflectRTPlayer;   // 1 when the player was stamped into it (else the wading
                         // silhouette fallback keeps running)
// P3b: every entity (player, NPCs, animals, critters, tree canopies) drawn UPSIDE-DOWN
// anchored at its own ground contact. Sampling this at the CURRENT pixel gives the
// correct reflection by construction — right anchor, no hidden-surface errors, no
// self-hits — where the screen flip can only guess from what happens to be above.
texture ReflectRTTexture;
// Filtered, like the scenery mirror below and for the same reason: the entity layer is read at
// a rippled offset, and a nearest-neighbour read of a displaced sprite drops and doubles rows -
// a reflected person came out notched and jagged beside a tree reflected soft. One surface,
// one resample rule.
sampler2D ReflectRTSampler = sampler_state
{
    Texture = <ReflectRTTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float SceneOn;           // 1 when the sprite-free scenery source below is live
// The share of SceneTexture that lies ABOVE the screen. The scenery source is taller than
// the frame, because a mirror only ever reads upward and the pixels it wanted most - a bank
// sitting at the top edge with trees standing on it - were off the screen entirely. Screen
// v maps to source v * (1 - SceneTopPad) + SceneTopPad, and a NEGATIVE screen v, meaning
// "above the top of the screen", now has somewhere real to land.
float SceneTopPad;
// And the same band down each SIDE. Sideways the mirror barely moves, so this is not about
// reach: it is that a sample landing outside the source is faded rather than clamped, and that
// fade is 6% of the picture - a permanent dimmed strip down both edges of the screen. With real
// pixels out there it stops firing.
float SceneSidePad;
// REFLECTION STYLE. Two numbers, chosen from a named look in the settings.
//   ReflWobble - how much of the surface's own ripple is allowed to displace the MIRROR.
//     The surface wobble and the reflection's clarity used to be one number, so calming a
//     choppy surface also flattened the ripple everywhere and the only way to read the
//     reflection on a rainy day was to turn the water down. They are separate questions.
//   ReflTint   - the cool darkening that makes a reflection read as being IN the water rather
//     than painted on it. Still water sits lighter and cooler, choppy water deeper.
float ReflWobble;
float ReflSoftness;      // scales the depth-driven softening of the mirror: 1 = as shipped, 0 = a
                         // single crisp tap, 2 = twice the spread. Taste, on a slider.
// How many steps per tile the sideways shear is rounded to, or 0 to shear every row on its own.
// 16 is the 4 px banding this shipped with through 1.5.6. See the note at the wave itself: this
// is what decides whether a reflected building bends or comes apart into sliding horizontal bands.
float ShearSteps;
// 1 for the wave shear as designed, 0 for a flat mirror. Applied ONLY where the reflection is
// sampled: the shoreline search below keeps the full wave, because its jitter is what breaks a
// diagonal bank out of 64 px staircase blocks.
float MirrorShear;
float3 ReflTint;
float3 SceneAmbient;     // lighting-stage ambient: the raw layer render carries no
                         // lighting, so the mirror scales it to match the lit scene
// P3c: the map's OWN layers (Back/Buildings/Front families) re-rendered with no
// sprites in them. The mirror reads its source here instead of the composed screen:
// a farmer standing on the bank can't punch a hole in the scenery's reflection,
// because this source never contained them - the true map pixels are behind.
texture SceneTexture;
sampler2D SceneSampler = sampler_state
{
    Texture = <SceneTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
// The SAME texture read with filtering on, for the mirror only.
//
// Everything else in this shader wants point sampling, because the game's art is pixels drawn
// at whole multiples and a filtered read of that is just a blurred game. The mirror is the one
// place where it is wrong: the source is squashed by 1.25 vertically and then displaced by the
// ripple, so a nearest-neighbour read drops and doubles whole rows of pixels. That is the
// stair-stepped, notched edge along a reflected pier - it was never the water, it was the
// resample.
sampler2D SceneSmoothSampler = sampler_state
{
    Texture = <SceneTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

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


// Everything a displaced tap must NOT land on, answered in one place: the map's own solid
// art, a sprite standing in the water, and the player. 1 = this point is water and the
// refraction may read it. Water is water, so nothing else may ever be read into it.
float TapIsWater(float2 p, float2 playerSpan)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float mapWater = tex2D(MaskSampler, (wt - MaskOrigin) / MaskSize).r;
    float onSprite = SpriteMaskOn * step(0.05, tex2D(SpriteMaskSampler, p).a);
    float2 tuv = (p - PlayerRect.xy) / playerSpan;
    float inBox = step(0.0, tuv.x) * step(tuv.x, 1.0) * step(0.0, tuv.y) * step(tuv.y, 1.0);
    // 0.15, not 0.05: the bake fades a shadow penumbra toward the head and its faintest
    // tail must not widen the exclusion past the sprite's visible pixels.
    float onPlayer = step(0.15, tex2D(PlayerMaskSampler, saturate(tuv)).a) * inBox;
    return step(0.02, mapWater) * (1.0 - max(onSprite, onPlayer));
}

// Point-sample the PIXEL-accurate water mask at any screen-UV point. The shoreline
// march runs on this, so a reflection anchors at the PAINTED waterline (the real
// curved pond edge), not at the tile boundary above it — and carved art (pier posts,
// bridges) reads as land, hanging its own reflection from its base.
// G channel = the MARCH mask: like the effect mask but small floating art (lily pads)
// is NOT carved, so only real shorelines and big structures (bridges, pier decks) stop
// the shoreline search.
float WaterAt(float2 p)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - MaskOrigin) / MaskSize;
    return tex2D(MaskSampler, muv).g;
}

// Bilinear read of the EFFECT channel. Unlike the point sample it RAMPS across the mask's
// boundary instead of switching, which is what the refraction needs: a yes/no answer there
// makes a decision, and a decision that moves with the wave is a pattern travelling along
// every edge. This is also the one texture the whole stage is built on, so unlike the
// distance field it cannot quietly be absent.
float EffectWaterSmooth(float2 p)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    return tex2D(MaskLinearSampler, (wt - MaskOrigin) / MaskSize).r;
}

// Smooth (bilinear) sample of the same mask — soft gradient near the waterline.
float WaterAtSmooth(float2 p)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - MaskOrigin) / MaskSize;
    return tex2D(MaskLinearSampler, muv).g;
}

// B channel = the CPU-precomputed WATERLINE MAP: distance (half-texel units) from this
// water pixel up to its body's shoreline, smoothed horizontally so stepped banks read
// as one continuous waterline. 255 = not march-water / edge out of reach.
float EdgeDistAt(float2 p)
{
    float2 wt = p * TilesPerScreen + WorldTileOffset;
    float2 muv = (wt - MaskOrigin) / MaskSize;
    // Bilinear: as a point sample the distance jumps a whole unit between neighbouring texels,
    // and along a diagonal shore those jumps line up into a staircase.
    return tex2D(MaskLinearSampler, muv).b;
}

float4 WaterPS(PixelInput input) : SV_TARGET
{
    float2 uv = input.UV;

    // Continuous world-tile coordinate (locks the shimmer to the water surface
    // as the camera pans, instead of swimming across the screen).
    float2 worldTile = uv * TilesPerScreen + WorldTileOffset;

    // PIXEL-accurate mask (16 texels per tile): true water tiles + the painted water inside
    // shore art, holes carved for piers/bridges/pads. Sampled CONTINUOUSLY (no tile floor) —
    // the effect ends exactly at the painted waterline, never spilling onto land.
    float2 maskUV = (worldTile - MaskOrigin) / MaskSize;
    float tileWater = tex2D(MaskSampler, maskUV).r;

    // Mask ALPHA tags the water TYPE: ~1 normal, ~0 ICE (frozen: mirror kept, no ripple),
    // ~0.5 (128) LAVA (slow molten flow + self-glow, no mirror). Ice/lava have no march
    // channel-driven ripple gate difference — the gate below zeroes ripple only for ice.
    float maskA = tex2D(MaskSampler, maskUV).a;
    float isIce  = 1.0 - step(0.25, maskA);              // a < 0.25
    float isLava = step(0.25, maskA) * (1.0 - step(0.75, maskA)); // 0.25..0.75
    // FLOWING (alpha 192) is a VERTICAL face - a waterfall, a fountain jet. It was left in the
    // ripple because it is liquid, but the ripple is a horizontal SURFACE wave: applied to a
    // falling jet it swings the whole column sideways in time with the pool behind it, which is
    // what "the falling water sways with the waves" is. Water at 255 sits above the 0.85 bar and
    // is unaffected; lava (128) is already excluded by its own tag.
    float isFlow = step(0.75, maskA) * (1.0 - step(0.85, maskA));
    float rippleGate = (1.0 - isIce) * (1.0 - isFlow);    // ice / falling: no surface wave

    // Signed shore distance in TEXELS (+ = inside water). Sampled for every pixel because
    // the wet ground rim below lives just OUTSIDE the water mask.
    float sdfT = (tex2D(SdfSampler, maskUV).a - 0.501961) * 63.75;

    float4 src = tex2D(SourceSampler, uv);
    if (tileWater <= 0.001)
    {
        // NOT WATER: the pass leaves the pixel exactly as it found it.
        //
        // There was a "wet ground rim" here, meant to darken the last few texels of land before
        // a waterline so the ground read as damp. It never did that. Its gate was the mask's
        // alpha CLASS rather than any measure of nearness to water, and its distance term read
        // the neutral value of the distance field as "standing at the water's edge", so on a
        // beach it dimmed 99% of the frame - sand tiles from the water, the cabin, the boat, the
        // player - by about 3.9%, and at the town bridge 80% of the frame by 4.2%. Captured both
        // ways and diffed; the heat map was the whole screen.
        //
        // That is also what made the picture jump while walking, reported since 1.3.0 and by
        // three players: it rode on whether water was inside the mask window at all, so crossing
        // that boundary switched a 4% dimming of EVERYTHING on and off. Removing it takes the
        // step with it, and costs nothing that was ever visible, because a rim that covers the
        // whole screen is not a rim.
        //
        // If a damp shoreline is wanted, it needs writing properly: gated on real distance to
        // water, and with the distance field's "no water here" value distinguishable from its
        // "right at the edge" value. Do not restore this version.
        return src;
    }

    float srcLum = dot(src.rgb, float3(0.299, 0.587, 0.114));
    // The player's own PIXELS never ripple where they overlap painted shore water. Sampled
    // from the baked silhouette so only the sprite is excluded, not a whole box. (The old
    // luminance test for ring tiles is retired: the mask itself is pixel-accurate now, so
    // there are no land pixels inside it to reject — the lum gate only wrongly dimmed dark
    // painted water at pond rims.)
    float2 pmSpan = max(PlayerRect.zw - PlayerRect.xy, float2(1e-4, 1e-4));
    float2 pmuv = (uv - PlayerRect.xy) / pmSpan;
    float pmIn = step(0.0, pmuv.x) * step(pmuv.x, 1.0) * step(0.0, pmuv.y) * step(pmuv.y, 1.0);
    // 0.15, not 0.02: the bake fades a shadow penumbra toward the head, and the faintest
    // tail of that fade must not widen the exclusion past the sprite's visible pixels.
    float inPlayer = step(0.15, tex2D(PlayerMaskSampler, saturate(pmuv)).a) * pmIn;
    // Exclude the player's pixels EVERYWHERE, not just in the bank ring: while swimming
    // the visible half-body sits over core water and used to warp with the ripple.
    float ringGate = 1.0 - inPlayer;
    // Sprites ON the water (ducks, NPCs, critters — per-frame bake): their own pixels
    // never ripple / mirror. Water beside them keeps animating (pixel-accurate mask).
    //
    // PROPORTIONAL, not a threshold. This was step(0.05, a), which made any stamp above five
    // per cent opacity remove the effect completely - fine while everything stamped was solid,
    // and wrong the moment something is not. The game fades a tree out while the player stands
    // behind it, the canopy went into the mask at full strength regardless, and what the player
    // saw through the see-through tree was a canopy-shaped patch of untouched vanilla water.
    // A half-transparent thing should hide half the effect, which is what an alpha already
    // means and what the threshold was throwing away.
    float inSprite = SpriteMaskOn * saturate(tex2D(SpriteMaskSampler, uv).a);
    ringGate *= 1.0 - inSprite;
    // V4: the pixel mask is the ONLY authority on coverage. Every colour test that used to
    // grade or veto here (blueness/greyness/cyan boosts, the 0.75 floor, the warm+saturated
    // sand veto) is gone — the mask is built from the game's own water flags refined by
    // hand-painted labels, so there is no over-coverage left for a colour test to hide, and
    // no recolor mod can ever fool a test that no longer exists.
    float water = tileWater * ringGate;
    // Distance-field edge: the mask's binary texel edge becomes a SHAPE-smooth,
    // RENDER-crisp 3-step ramp over the last texel of water. The floor sat at 0.15
    // over a 1.5-texel ramp and read as a DEAD band along every shoreline — with the
    // vanilla water level bobbing, the band flickered like notches missing from the
    // water. 0.5 over one texel keeps the edge soft without ever looking absent.
    float edgeQ = floor(saturate(sdfT) * 3.0 + 0.5) / 3.0;
    water *= max(edgeQ, 0.5);
    if (water <= 0.002)
        return src;

    // Refraction in WORLD space so the ripple travels with the water:
    //  - pond: fine crossing ripples, small & quick (still surface).
    //  - ocean: long directional swell, bigger & slower.
    //  - lava: the SAME molten motion but crawling (thick, viscous) — slow the phase hard.
    float t = Time * Speed * lerp(1.0, 0.12, isLava);
    float pwx = sin(worldTile.y * 6.3 + t * 6.0) + 0.5 * sin(worldTile.x * 4.1 - t * 4.0);
    float pwy = cos(worldTile.x * 5.7 - t * 5.0) + 0.5 * cos(worldTile.y * 4.7 + t * 3.5);
    float2 pondRipple = float2(pwx, pwy) * (Strength * 0.0025);

    float swell = sin(worldTile.y * 2.1 + t * 1.6) + 0.35 * sin(worldTile.x * 1.4 - t * 1.0);
    float2 oceanRipple = float2(swell * 0.25, swell) * (Strength * 0.006);

    // Puddle pixels (mask < full) always ripple POND-style: an ocean map's long slow swell
    // barely moves inside a 3-tile walk-through pool, which read as "no effect up close".
    float kind = WaterKind * step(0.95, tileWater);
    float2 ripple = lerp(pondRipple, oceanRipple, kind) * water * rippleGate;
    // A displaced tap must never land on a sprite, on the player, or on the map's own solid
    // art. That rule is old and right: a water pixel beside a boat, a pier post or a fountain
    // statue used to drag those pixels sideways with the wave and the whole object read as
    // swaying, even though its own pixels were carved out of the mask perfectly. Reported for
    // Willy's boat at the fish shop, for the boat in BoatTunnel, and visible on the fountain.
    //
    // What is new is what happens WHEN the tap is blocked. It used to set the displacement to
    // zero, which is correct about colour and wrong about motion, and the wrongness was
    // visible: a pixel within one wave-amplitude of an edge froze, a pixel past that did not,
    // and which side of that line a pixel fell on changed as the wave passed, because the test
    // is made at uv + ripple and ripple is the thing oscillating. So the last few pixels along
    // a straight edge flipped between two states in a travelling pattern, and a straight bridge
    // read as a wavy one. Turning the ripple down never helped and never could: a smaller wave
    // only moves the flicker closer to the edge.
    //
    // A wave that meets something solid turns BACK from it. It does not stop dead and it does
    // not reach through. So a blocked tap is MIRRORED about the pixel: the same distance the
    // other way, which is where the water is. Amplitude survives all the way to the edge, so
    // there is no frozen fringe and no moving boundary between frozen and not. Only a sliver
    // of water narrower than the wave itself, blocked in both directions, still holds still.
    // How much of this displacement the water can actually hold, as a SMOOTH number rather
    // than a yes or a no.
    //
    // Two versions of this were tried and both failed the same way. Zeroing a blocked
    // displacement freezes a pixel while its neighbour still moves, and which of the two a
    // pixel is changes as the wave passes, so the edge carries a travelling pattern. Mirroring
    // a blocked displacement keeps everything moving but makes neighbouring pixels sample from
    // opposite sides, and the boundary between the two moves with the wave as well: the same
    // pattern, arrived at from the other direction. Anything that DECIDES per pixel does this.
    //
    // So do not decide. Fade the amplitude to nothing as the water runs out, sampled bilinearly
    // at two points along the displacement so the ramp covers the wave's whole reach rather
    // than the last texel of it. The pixel against the edge does not move at all, the one
    // behind it moves a little, and there is no boundary anywhere for a pattern to live on.
    float reach = min(EffectWaterSmooth(uv + ripple), EffectWaterSmooth(uv + ripple * 0.5));
    ripple *= smoothstep(0.05, 0.85, reach);
    // Backstop for a sliver of water narrower than the wave, where even the faded displacement
    // can still land on art. It fires on almost nothing now, which is the point: a switch is
    // only safe when it is not what shapes the picture.
    ripple *= TapIsWater(uv + ripple, pmSpan);
    float4 col = tex2D(SourceSampler, uv + ripple);

    // Depth tint: cool + deepen for a wetter, more 3D surface. TintAmt drops to 0 when
    // the shimmer toggle is off (the stage may still be running just for the mirror).
    // NOT on lava — molten rock isn't cool/blue.
    float3 tint = col.rgb * float3(0.90, 0.97, 1.12);
    col.rgb = lerp(col.rgb, tint, TintAmt * water * (1.0 - isLava));

    // LAVA self-glow: a slow warm emissive pulse so molten rock lights itself (and blooms).
    if (isLava > 0.5)
    {
        float pulse = 0.6 + 0.4 * sin(Time * 0.5 + worldTile.x * 0.6 + worldTile.y * 0.4);
        col.rgb += float3(0.42, 0.14, 0.02) * pulse * water;
    }

    // Caustics: the net of focused light on a shallow bed. Two copies of the baked ridge
    // texture scroll against each other and are multiplied, so the bright filaments wander
    // and merge instead of translating rigidly. Everything is anchored the way the ripple
    // is - world space, snapped to the world-pixel grid, on the same clock - so the net can
    // neither swim when the camera pans nor slide at a speed of its own.
    //
    // Weighted by the shore distance: full on the first couple of tiles off a bank, fading
    // to a small floor in open water, because a full-strength net across a whole lake reads
    // as pattern, not light. It draws BEFORE the reflection blends, whose lerps then weigh
    // it down exactly where a mirror image covers the bed - under the reflection is where a
    // caustic lives.
    [branch]
    if (CausticAmt > 0.001)
    {
        float causticTime = Time * Speed;
        float2 causticWorldTile = floor(worldTile * 64.0) / 64.0;
        float2 causticUv1 = causticWorldTile * 0.22 + float2( causticTime * 0.020, -causticTime * 0.012);
        float2 causticUv2 = causticWorldTile * 0.31 + float2(-causticTime * 0.014,  causticTime * 0.017);
        // tex2Dlod, not tex2D: an implicit-gradient fetch cannot live inside a real branch,
        // and the texture has no mips for the gradient to choose between anyway.
        float causticNet = tex2Dlod(CausticSampler, float4(causticUv1, 0.0, 0.0)).r
                         * tex2Dlod(CausticSampler, float4(causticUv2, 0.0, 0.0)).r;
        // The baked web is broad; the sharpening lives here, AFTER the product, so the
        // result is an evolving net and not dots at the crossings of two thin ones.
        causticNet = pow(saturate(causticNet * 1.9), 2.2);
        // How far off the bank the shelf reaches depends on what the water IS. The sea
        // (WaterKind 1) gets a real shallow shelf, a tile and a half of sloping sand; a
        // river or pond keeps a ribbon that dies inside the first tile, because a 2-tile
        // stream wearing a full shelf is lit bank to bank and reads as pattern, not light.
        // (16 texels = one tile.)
        float shelfFadeStart = lerp(8.0, 16.0, WaterKind);
        float shelfGone = lerp(14.0, 26.0, WaterKind);
        float shallowBand = smoothstep(0.5, 2.0, sdfT) * (1.0 - smoothstep(shelfFadeStart, shelfGone, sdfT));
        float causticWeight = max(shallowBand, CausticDeepFloor) * water * rippleGate * (1.0 - isLava);
        float causticTerm = causticNet * CausticAmt * causticWeight;
        col.rgb += causticTerm * float3(0.75, 0.95, 0.88);
        // The debug view paints the WEIGHT, not the net: a solid red that is strong where the
        // shelf is strong and faint where the floor is, so the shore band shows as a gradient
        // instead of hiding inside the pattern. (Painting the term itself was tried first, and
        // two nets of different brightness look the same once both are red lines.)
        col.rgb = lerp(col.rgb, float3(1.0, 0.0, 0.0), DebugCaustic * saturate(causticWeight * step(0.001, CausticAmt)));
    }

    // Screen-space reflection: a true vertical mirror. March UP the water mask to
    // find the shoreline (where water ends), then reflect the scene above that edge
    // down onto the water — so shore, trees, buildings and a character standing at
    // the water's edge appear mirrored, rippled, strongest near the edge and fading
    // with depth. Gated to water pixels.
    if (ReflectStrength > 0.001)
    {
        // The reflection sways with the surface — computed FIRST because the same jitter
        // feeds the shoreline search below (dithers the per-tile-column edge steps into
        // organic wavy seams instead of hard 64px vertical banding).
        // This value shears the reflection SIDEWAYS, one horizontal offset per row, so whatever
        // shape it has in y is the shape the reflection is cut into.
        //
        // It used to be sampled on a quantised row, floor(worldTile.y * 16) / 16, which is one
        // step every four world pixels. Every row inside a step shifted by exactly the same
        // amount and then the whole thing jumped at the boundary, so a reflected building came
        // apart into horizontal bands sliding over each other. That was not a side effect of the
        // wave, it WAS the wave: a staircase cannot shear anything smoothly. The banding was
        // deliberate once, to read as drawn pixel-art water rather than per-pixel noise, and the
        // author has since asked for the opposite. Sampled continuously, the shear varies by a
        // fraction of a pixel from one row to the next and the reflection bends instead of
        // breaking.
        //
        // The frequencies matter for the same reason they did while it was quantised. A sine of
        // frequency k has a period of 402/k world pixels:
        //
        //   k = 34   11.8 px
        //   k = 20   20.1 px
        //   k = 61    6.6 px   was here, and at one sample per four pixels it could never appear
        //                      as itself: it folded into a slow beat that crawled with t, which
        //                      is the streaking that was reported
        //
        // 61 stays gone even now that sampling is per row. It resolves cleanly, but a shear that
        // reverses every 6.6 pixels is a finer comb than the reflection has detail to survive,
        // and the request was for less of this, not more.
        //
        // ShearSteps is the setting: steps per tile, or 0 for none. 16 reproduces the 4 px banding
        // this shipped with through 1.5.6 exactly, so the old look is a slider away rather than a
        // rebuild away.
        float rowY = ShearSteps > 0.5 ? floor(worldTile.y * ShearSteps) / ShearSteps : worldTile.y;
        float wave = sin(rowY * 34.0 + t * 2.6)
                   + 0.45 * sin(rowY * 20.0 - t * 3.9 + worldTile.x * 0.9);
        float waveAmp = Strength * 0.0035 + 0.0012;
        // Static per-16px-block dither on the march column: a diagonal shoreline otherwise
        // quantises the mirror into 64px staircase bands; this breaks the steps into ragged
        // pixel-scale water distortion (hash of the WORLD position → stable, no shimmer).
        // Kept SMALL: a wide dither shoved edge pixels onto the land column beside them,
        // stripping the mirror off the left/right rim of pools and pier inlets.
        // Tiny now: the pixel mask draws real curved shorelines, so the old wide dither that
    // hid tile staircases would only smear the clean edge.
    float dith = (hash(floor(worldTile * 4.0) / 4.0) - 0.5) * (0.05 / TilesPerScreen.x);
        float mx = uv.x + wave * waveAmp + dith;   // jittered column for the march + mirror

        // Bank-ring columns (the strip of drawn water inside shore tiles) have NO core water
        // above them — their march found land instantly, leaving the mirror short of the
        // left/right waterline. If this column isn't over core water, borrow the neighbour
        // column on whichever side the real water is.
        // Borrow at TEXEL scale: a full-tile hop was itself quantising the mirror into 64px columns.
        // SMOOTH taps, not point ones. As point samples all three terms are hard 0 or 1, so the
        // borrow is exactly 0 or a whole quarter-tile and it switches the instant the column
        // crosses a texel boundary. On a diagonal shore each screen row crosses on a different
        // column, and the mirror's left/right edge steps in and out by 16 px from row to row -
        // the fine sawtooth along a reflection edge. Read through the linear sampler and a
        // half-covered column borrows half as far, so the edge ramps instead of snapping.
        // This is the same fix, for the same reason, that EdgeDistAt already documents.
        // Only the SAMPLE COLUMN moves. Whether a pixel reflects at all is still decided by the
        // point-sampled `found` below, so nothing here can bleed the mirror onto land.
        float tileW = 4.0 / (TilesPerScreen.x * 16.0);
        float coreC = WaterAtSmooth(float2(mx, uv.y));
        float coreL = WaterAtSmooth(float2(mx - tileW, uv.y));
        float coreR = WaterAtSmooth(float2(mx + tileW, uv.y));
        mx += (1.0 - coreC) * (coreR - coreL) * tileW;

        // Shoreline from the precomputed WATERLINE MAP (one sample replaces the old 34-tap
        // march): per-column distance to this water body's edge, already smoothed across
        // columns on the CPU — stepped tile banks anchor as one continuous line, so the
        // mirror never slices into offset blocks.
        float distHalf = EdgeDistAt(float2(mx, uv.y)) * 255.0;
        float found = WaterAt(float2(mx, uv.y)) * step(distHalf, 252.5);
        float waterOff = (distHalf * 2.0 / 16.0) / TilesPerScreen.y;   // 0.5 unit per texel on the CPU
        float edgeV = uv.y - waterOff;

        // Oblique-view mirror: the world is drawn at a slant, so a reflection must be
        // COMPRESSED vertically (×1/0.8 source distance per unit of depth) — this pulls each
        // reflection up against the object casting it instead of floating a gap below it.
        // The extra 0.6-tile source bias skips the mostly-transparent bottom sliver of shore
        // art (pier post rows, rim edges) so the SOLID body of the object meets the waterline.
        float depth = uv.y - edgeV;                 // how far below the shoreline
        // Source bias is nearly zero now: it existed to skip pier-post art rows, but posts are
        // CARVED out of the pixel mask (the march stops at their base), so the mirror can start
        // right at the painted waterline — bank rims, bridge arches and a player standing at
        // the pond's edge all appear pressed against the water.
        // The mirror reads from its own column. mx carries the full wave because the shoreline
        // search needs the jitter; the reflection takes as much or as little of that shear as the
        // chosen look asks for, so Mirror can be flat without straightening the waterline.
        float mirrorX = uv.x + wave * waveAmp * MirrorShear + dith
                      + (1.0 - coreC) * (coreR - coreL) * tileW;
        float2 reflUv = float2(mirrorX + ripple.x * 3.0 * ReflWobble,
                               edgeV - depth * 1.25 - 0.08 / TilesPerScreen.y + abs(ripple.y) * 2.0 * ReflWobble);
        // Keep the UNCLAMPED coordinate for the scenery source, which reaches past the top of
        // the screen; the composed-screen fallback and the mask lookups have nothing up there
        // and still work on the clamped one.
        float2 reflUvRaw = reflUv;
        reflUv = clamp(reflUv, float2(0.0, 0.0), float2(1.0, 1.0));
        // Prefer the sprite-free scenery source (P3c): the composed screen contains the
        // player/NPCs, and excluding them left body-shaped sky holes in the reflection.
        float2 sceneUv = float2(reflUvRaw.x * (1.0 - 2.0 * SceneSidePad) + SceneSidePad,
                                reflUvRaw.y * (1.0 - SceneTopPad) + SceneTopPad);
        // Softened with DEPTH, the way water actually does it: sharp against the bank it is
        // reflecting and hazier the further out it goes. Three taps up the compression axis,
        // filtered, spread by how deep this pixel sits - at the waterline they land inside one
        // source pixel and the reflection stays crisp, and out in open water they merge. This is
        // both the fix for the stair-stepping and the reason the far end of a reflection now reads
        // as distance rather than as a low-resolution copy.
        float reflSoft = (0.25 + depth * TilesPerScreen.y * 0.10) / max(1.0, TilesPerScreen.y) / 16.0 * ReflSoftness;
        float2 sceneUvC = clamp(sceneUv, float2(0.0, 0.0), float2(1.0, 1.0));
        float3 refl = SceneOn > 0.5
            ? (tex2D(SceneSmoothSampler, sceneUvC).rgb * 0.5
             + tex2D(SceneSmoothSampler, clamp(sceneUv + float2(0.0,  reflSoft), 0.0, 1.0)).rgb * 0.25
             + tex2D(SceneSmoothSampler, clamp(sceneUv - float2(0.0,  reflSoft), 0.0, 1.0)).rgb * 0.25) * SceneAmbient
            : tex2D(SourceSampler, reflUv).rgb;

        // Wide 5-tap smoothing: at pixel-mask resolution a single bilinear sample flips
        // land→water within ~4px, which drew a hard horizontal seam wherever the mirrored
        // source crossed onto upper water (bridges). Spread the mirror-to-sheen transition
        // over ~1.6 tiles so the reflection FADES out instead of ending on a ragged line.
        float tps = 1.0 / TilesPerScreen.y;
        float srcWater = (WaterAtSmooth(reflUv)
                        + WaterAtSmooth(reflUv + float2(0.0, -0.4 * tps))
                        + WaterAtSmooth(reflUv + float2(0.0,  0.4 * tps))
                        + WaterAtSmooth(reflUv + float2(0.0, -0.8 * tps))
                        + WaterAtSmooth(reflUv + float2(0.0,  0.8 * tps))) * 0.2;

        // Distance fade: defined near the shoreline, gone by ~0.75 screen below it. (An
        // always-on base mirrored far-upstream cliffs down entire rivers as dark streaks;
        // 1.6 faded bridges out so fast their reflection looked cut short.)
        // Reach. At 1.3 the mirror was gone 0.77 of a screen below the shoreline, which is about
        // four tiles from a mid-screen waterline: a bridge's reflection stopped long before the
        // river did. It no longer has to cut to nothing either, because what lies past it is the
        // sky glaze rather than bare water.
        float fade = saturate(1.0 - depth * 0.5);
        // Fade the reflection out where the mirrored sample would fall OFF-screen, instead of
        // clamping (which smears the edge row/column across the water near the screen border).
        // Measured against whichever source is actually being read, so the padding around the
        // screen counts as available when the scenery source is live and is still refused when
        // the fallback is reading the composed frame.
        //
        // Two things were wrong with fading by "distance to the edge of the picture".
        //
        // The BOTTOM edge is not an edge this sample can fall off. The mirror reads upward from
        // the waterline and never downward, so the only way sceneUv reaches the bottom of the
        // source is a shoreline sitting low on the screen with almost no depth below it - which is
        // a perfectly good reflection that was being dimmed for no reason. It is dropped from the
        // test entirely.
        //
        // And the band was 6% of the picture, so the left and right of every screen carried a
        // permanently faded strip of reflection about a tile and a quarter wide. With the scenery
        // source padded past all three edges there are TRUE pixels out there for the whole of the
        // reach the mirror can use, so the taper belongs at the very end of the data as a hairline,
        // not as a standing tax on the edges of the view. The composed-screen fallback keeps the
        // wide guard: it has no padding, and there a clamped sample really does smear the edge row
        // across the water.
        float2 borderUv = SceneOn > 0.5 ? sceneUv : reflUvRaw;
        float guardBand = SceneOn > 0.5 ? 0.01 : 0.06;
        float3 dedge = float3(borderUv.x, 1.0 - borderUv.x, borderUv.y);
        float onScreen = saturate(min(min(dedge.x, dedge.y), dedge.z) / guardBand);
        // A TRUE mirror only exists when a shoreline was found, the mirrored source is not
        // itself water, and this column actually had water above it (bank-fringe columns
        // don't). Everywhere else, blend to a soft sky-glaze SHEEN instead of cutting to
        // nothing — the hard rectangles between mirrored and unmirrored water came from
        // those cuts, not from the mirror itself.
        float3 mirrorCol = refl * ReflTint;   // cool + darken: reads as "in the water"
        // BOUNDED HEIGHT (see docs/water-v3-research). A mirrored sample deeper than the tallest
        // thing that can stand on a shore is not a reflection of anything — and flat ground
        // (height zero) mirrored across a pond is what read as the "green sheet". The honest end of
        // a reflection is SKY, not absence, so the mirror dissolves into the sky tint rather than
        // running on or being cut off.
        //
        // The bound was 5..9 tiles, chosen when the mirror could only read what was on the screen.
        // It is what made a wide river read as flat paint: the water more than nine tiles from its
        // own upstream bank had its reflection dissolved away entirely, which is most of a river
        // and most of a lake. Now that the source reaches twelve tiles past the top of the frame
        // there are real pixels to mirror out there, and a cliff or a stand of trees is easily
        // taller than the old bound allowed for, so it runs to 9..16.
        //
        // The upstream-WATER branch moved with it, 2..4 to 4..8, for the same reason and with the
        // same caution: mirroring water onto water is what produced the dark streaks down a river,
        // so it still resolves to sky, just not before the bank above has had its say.
        float depthTiles = depth * TilesPerScreen.y;
        float3 skySurf = lerp(col.rgb, SkyColor, 0.25);
        // ...and the same resolution when the mirrored SOURCE is upstream water. This used to be
        // a 70% DAMP gated at 1.2 tiles, and that gate was a visible horizontal cut right under
        // every bridge (a bridge is ~1 tile tall, so just past its own art the source is river
        // again). Resolving to sky suppresses the same upstream-water streaks with no seam, and
        // it engages at 2..4 tiles so a bridge plus a person standing on it (~2 tiles of genuine
        // reflection) keeps its full band.
        float toSky = max(smoothstep(9.0, 16.0, depthTiles),
                          srcWater * smoothstep(4.0, 8.0, depthTiles));
        // P3b — sprites already reflect via the flipped-entity RT (composited below); the
        // same sprite left in the screen-flip SOURCE would mirror twice at a different
        // offset. With the sprite-free scenery source (P3c) live there is nothing to
        // exclude — the fallback below only runs when the mirror reads the composed
        // screen, and it resolves sprite pixels to sky (the old body-shaped hole).
        // The carve ONLY runs when the mirror is reading the composed screen. Making it
        // unconditional was tried for one build and it brought the hollow straight back:
        // carving at the SOURCE point punches a hole up-screen, while the body's own
        // reflection is stamped down-screen from its feet - for anything whose stamp lands
        // on land (a bird over the bank, a butterfly inland) that is a hole and no
        // reflection, which is the artifact the scenery bake exists to retire. With the bake
        // live there is nothing to carve, so the right answer is to leave it alone.
        if (ReflectRTOn > 0.5 && SceneOn < 0.5)
        {
            float srcSprite = SpriteMaskOn * step(0.05, tex2D(SpriteMaskSampler, reflUv).a);
            float2 pruv = (reflUv - PlayerRect.xy) / pmSpan;
            float prIn = step(0.0, pruv.x) * step(pruv.x, 1.0) * step(0.0, pruv.y) * step(pruv.y, 1.0);
            srcSprite = max(srcSprite, ReflectRTPlayer * prIn * step(0.05, tex2D(PlayerMaskSampler, saturate(pruv)).a));
            toSky = max(toSky, srcSprite);
        }
        mirrorCol = lerp(mirrorCol, skySurf, toSky);
        // The no-mirror fallback is the same surface with a WHISPER of sky, not a brightened copy
        // of the water (two formulas ~40% apart in brightness met at a visible seam). Kept subtle:
        // at 0.35 the glaze washed the mirror itself out — its job is only to remove the seam.
        float3 sheenCol = lerp(col.rgb, SkyColor, 0.12);
        // The old nearSelf damping (x0.6 within ~2 texels of the waterline) is retired: with a
        // brighter sheen it rendered as a pale empty strip hugging every bank, and the physically
        // right content there is the bank's own dark rim reflection at full strength.
        float mirrorness = found;
        float3 reflCol = lerp(sheenCol, mirrorCol, mirrorness);
        // The luminance gate keeps the mirror off water that is genuinely in shadow, but it was
        // reading THIS pixel alone, so anything dark drawn INSIDE the water — a submerged rock, a
        // patch of weed, a fish shadow — lost its reflection while the water touching it kept one.
        // That edge is the thin bright outline around every underwater object. A reflection does
        // not stop existing because the bottom is dark, so the gate now reads the NEIGHBOURHOOD:
        // an object a few texels across no longer punches a hole, while a whole region in shadow
        // still gates exactly as before.
        float lumAvg = (srcLum
                      + dot(tex2D(SourceSampler, uv + float2(0.0, -0.35 * tps)).rgb, float3(0.299, 0.587, 0.114))
                      + dot(tex2D(SourceSampler, uv + float2(0.0,  0.35 * tps)).rgb, float3(0.299, 0.587, 0.114))
                      + dot(tex2D(SourceSampler, uv + float2(-0.35 * tps, 0.0)).rgb, float3(0.299, 0.587, 0.114))
                      + dot(tex2D(SourceSampler, uv + float2( 0.35 * tps, 0.0)).rgb, float3(0.299, 0.587, 0.114))) * 0.2;
        float amt = saturate(ReflectStrength) * water * fade * onScreen
                  * saturate(max(srcLum, lumAvg) * 3.2) * lerp(0.5, 1.0, mirrorness);
        col.rgb = lerp(col.rgb, reflCol, amt);

        // P3b — composite the flipped-entity layer. It is correct by construction, so it
        // rides neither the shoreline march nor the source-luminance gates: just the march
        // mask at THIS pixel (flowing water and lava mirror nothing) and the same ripple
        // wobble + cool grade as the mirror, so both read as one surface.
        if (ReflectRTOn > 0.5)
        {
            // Wobble kept SMALL (was 1.8): a hard sway made the visible scrap of an
            // occluded reflection drift sideways and read as a separate floating blob.
            // Three taps up the same axis the scenery mirror softens on, spread by the same
            // slider, so both halves of one reflection blur together. The spread is a fixed
            // sliver of a screen (there is no shoreline depth to grow it by): at 1 it is under a
            // pixel and reads as filtering, at 2 it is a visible haze.
            float2 entUv = saturate(uv + ripple * 0.9 * ReflWobble);
            float entSoft = 0.6 / max(1.0, TilesPerScreen.y * 64.0) * ReflSoftness;
            float4 ent = tex2D(ReflectRTSampler, entUv) * 0.5
                       + tex2D(ReflectRTSampler, saturate(entUv + float2(0.0, entSoft))) * 0.25
                       + tex2D(ReflectRTSampler, saturate(entUv - float2(0.0, entSoft))) * 0.25;
            // Entities also mirror on the WET FRINGE (effect-only band: beach surf wash, the
            // strip under a bank's overlay art) — the march channel stops there, and clipping
            // a body's shallow half against it left the deep half floating detached below an
            // NPC on the tide line. Plain water only: alpha 192 tags FLOWING water (a body
            // must not print on a waterfall face) and lava/ice sit lower still.
            float2 ewt = uv * TilesPerScreen + WorldTileOffset;
            float4 em = tex2D(MaskSampler, (ewt - MaskOrigin) / MaskSize);
            float entWater = max(em.g, em.r * step(0.9, em.a));
            float entAmt = saturate(ReflectStrength) * entWater * ent.a * (1.0 - inSprite);
            // Opacity walked in two reports: 0.85 read as "too clear" (denser than the bushes
            // mirrored beside it), 0.66 as "a bit too faint" once the feet->head fade landed on
            // top of it. 0.74 with that fade puts the near-feet band back at the old presence
            // while the deep end stays soft.
            col.rgb = lerp(col.rgb, ent.rgb * ReflTint, entAmt * 0.74);
        }

        // SELF-REFLECTION while wading: standing IN the water the player is BELOW the
        // shoreline, so the main mirror can't see them. Mirror the baked silhouette about
        // their own feet line instead — a dark rippling figure that follows them through
        // the pool. Silhouette-based → any outfit/appearance mod works automatically.
        // (The flipped-entity RT covers this exact case when it holds the player — the
        // fallback only runs when it doesn't, e.g. shadows disabled.)
        if (PlayerInWater > 0.02 && ReflectRTPlayer < 0.5)
        {
            float feetFrac = 0.9545;                             // feet row inside the mask RT
            float feetV = PlayerRect.y + feetFrac * pmSpan.y;
            float dvB = (uv.y - feetV) / pmSpan.y;               // box units below the feet
            float2 ruv = float2((uv.x + ripple.x * 2.5 - PlayerRect.x) / pmSpan.x,
                                feetFrac - dvB);
            float inR = step(0.0, dvB) * step(0.0, ruv.x) * step(ruv.x, 1.0)
                      * step(0.0, ruv.y) * step(ruv.y, 1.0);
            float ra = tex2D(PlayerMaskSampler, saturate(ruv)).a * inR;
            // Colour comes from the SCREEN mirrored about the feet line (the player's own
            // drawn sprite) — outfit colours for free; the silhouette alpha keeps the shape
            // so nothing beside the player leaks in.
            float mirrY = feetV - dvB * pmSpan.y;
            float3 selfCol = tex2D(SourceSampler, float2(saturate(uv.x + ripple.x * 2.5), saturate(mirrY))).rgb
                           * float3(0.62, 0.72, 0.88);   // cool + darken: "in the water"
            float rfade = saturate(1.0 - dvB * 0.9);
            col.rgb = lerp(col.rgb, selfCol,
                           saturate(ra * 1.3) * rfade * water * saturate(ReflectStrength) * 0.6
                           * saturate(PlayerInWater));
        }
    }

    // Shore contact shading: water darkens slightly where it meets ANY edge — banks,
    // island rims, lily pads, pier posts. The mirror above only reflects what stands
    // NORTH of the water, so side edges looked bare; this grounds every waterline the
    // way real shallows darken against their bank. Width ~2 mask texels (bilinear).
    float2 mt = 2.0 / (MaskSize * 16.0);
    float rimMin = min(min(tex2D(MaskLinearSampler, maskUV + float2(0.0, -mt.y)).r,
                           tex2D(MaskLinearSampler, maskUV + float2(0.0,  mt.y)).r),
                       min(tex2D(MaskLinearSampler, maskUV + float2(-mt.x, 0.0)).r,
                           tex2D(MaskLinearSampler, maskUV + float2( mt.x, 0.0)).r));
    // Gated to MARCH water (g): wet-shading fringe kept only in the effect mask must
    // not get a dark rim painted onto the bank.
    float rim = saturate(tileWater - rimMin) * tex2D(MaskSampler, maskUV).g;
    // Rim shading belongs to the shimmer look — gone when only the mirror is running.
    col.rgb *= 1.0 - rim * 0.22 * water * saturate(TintAmt * 3.0);

    // Shoreline foam from the distance field: a soft brightening over the first texels of
    // water plus ONE drifting lap line, posterised to read as pixels (never a smooth wash).
    // World-anchored phase jitter per texel so the line breaks up instead of tracing the
    // grid. Ice doesn't lap; lava's edge glows on its own; rain roughens the lap line away.
    float shoreT = (tex2D(RealShoreSdfSampler, maskUV).a - 0.501961) * 63.75;
    float foamBand = (1.0 - smoothstep(0.5, 5.0, shoreT)) * step(0.0, shoreT);
    float lapPhase = frac(shoreT * 0.45 - Time * 0.30 + hash(floor(worldTile * 16.0) / 16.0) * 0.25);
    float lap = step(0.62, lapPhase);
    float foam = foamBand * (0.30 + 0.70 * lap) * water * (1.0 - isIce) * (1.0 - isLava) * (1.0 - RainAmt * 0.5);
    foam = floor(foam * 3.0 + 0.5) / 3.0;
    col.rgb = lerp(col.rgb, float3(0.93, 0.97, 1.02), foam * 0.30);

    // Drifting specular glints — SCATTERED, not a grid. The old "one glint per cell,
    // all the same size" read as a regular dotted pattern. Now: TWO overlapping layers
    // at different scales/drift, each cell only SOMETIMES holds a glint (hash gate), and
    // every glint gets a random SIZE, off-centre wander, and phase — so it reads as
    // organic sun-glitter. Ocean glints are sparser/slower (kind).
    float spulse = lerp(1.1, 0.55, kind);
    float sdrift = lerp(0.05, 0.12, kind);
    float baseDens = lerp(5.0, 3.0, kind) * max(SparkleDensity, 0.05);
    float glint = 0.0;
    [unroll]
    for (int gi = 0; gi < 2; gi++)
    {
        float dens = baseDens * (gi == 0 ? 1.0 : 1.73);                 // two scales
        float2 off = (gi == 0) ? float2(0.0, 0.0) : float2(0.37, 0.63);
        float driftDir = (gi == 0) ? 1.0 : -0.8;                        // layers drift apart
        float2 sg = (worldTile + off + float2(t * sdrift, t * sdrift * 0.6) * driftDir) * dens;
        float2 cell = floor(sg);
        float2 f = frac(sg) - 0.5;                                      // cell-centred
        float h1 = hash(cell + off);
        float h2 = hash(cell + off + float2(19.7, 7.3));
        float h3 = hash(cell + off + float2(41.3, 5.1));
        float has = step(0.55, h1);                                     // ~45% of cells hold a glint
        float2 jit = (float2(h2, frac(h1 * 7.3)) - 0.5) * 0.7;          // wander off-centre
        float rad = lerp(0.09, 0.30, h3 * h3);                          // per-glint size (biased small)
        float d = length(f - jit);
        // Twinkle in BRIGHTNESS, never fully off: floor at 0.35 so a glint dims and
        // brightens instead of blinking out (the surface kept a steady base sparkle,
        // no more moments where it nearly all disappears).
        float pulse = 0.675 + 0.325 * sin(t * spulse + h1 * 6.2831853);
        glint += smoothstep(rad, 0.0, d) * pulse * has;
    }
    glint = saturate(glint);
    // Golden hour: the glints warm up with the low sun instead of staying white.
    float3 glintCol = lerp(float3(1.0, 1.0, 1.0), float3(1.0, 0.82, 0.5), SunWarm);
    col.rgb += glint * Sparkle * water * glintCol * rippleGate * (1.0 - isLava);   // ice/lava: no sun glints

    // ---- Night: starlight on the surface (clear nights only) ----
    if (NightGlow > 0.001)
    {
        float2 sgrid = worldTile * 7.0;
        float2 scell = floor(sgrid);
        float sr1 = hash(scell);
        float sr2 = hash(scell + 41.7);
        float has = step(0.82, sr1);                     // sparse cells hold a star
        float2 sc = float2(frac(sr1 * 13.7), frac(sr2 * 7.3)) * 0.6 + 0.2;
        float sd = length(frac(sgrid) - sc);
        float tw = 0.55 + 0.45 * sin(t * 0.9 + sr2 * 6.2831853);   // slow twinkle
        float star = smoothstep(0.12, 0.0, sd) * has * tw;
        col.rgb += star * NightGlow * (1.0 - RainAmt) * water * float3(0.75, 0.85, 1.0) * 0.9;
    }

    // ---- Night: moonlight shimmering across the swell (phase/season/cloud scaled) ----
    if (NightGlow > 0.001 && MoonGlow > 0.001)
    {
        float sw1 = sin(worldTile.y * 1.3 - t * 0.5) * 0.5 + 0.5;
        float sw2 = sin(worldTile.x * 0.7 + worldTile.y * 0.9 + t * 0.35) * 0.5 + 0.5;
        float sheenM = sw1 * sw2;
        col.rgb += (sheenM * sheenM * 0.14 + 0.03) * MoonGlow * NightGlow * water * float3(0.55, 0.68, 0.95);
    }

    // ---- Night: warm lamp light shimmering down the water below each light ----
    if (NightGlow > 0.001 && LightCount > 0.5)
    {
        [unroll]
        for (int li = 0; li < 8; li++)
        {
            float on = step((float)li + 0.5, LightCount);
            float4 L = Lights[li];
            float2 dl = uv - L.xy;
            float band = exp(-abs(dl.x + ripple.x * 2.0) * 90.0);          // narrow column
            float below = smoothstep(-0.01, 0.03, dl.y) * exp(-dl.y * 6.0); // fades with distance below
            float flick = 0.75 + 0.25 * sin(t * 3.1 + (float)li * 2.4 + worldTile.y * 9.0);
            col.rgb += on * L.w * band * below * flick * water * NightGlow * float3(1.0, 0.74, 0.42) * 0.45;
        }
    }

    // ---- Rain striking the surface: an impact, then a ring that widens and dies ----
    //
    // The old version gave every cell a ring on the same clock, which read as a grid of
    // sonar pings rather than as weather. Real rain lands in scattered points: each cell
    // keeps its own clock at its own rate, only fires on some of its cycles, and drops
    // somewhere other than the middle. Two scales overlap so the surface never shows the
    // spacing of either one.
    if (RainAmt > 0.001)
    {
        float rings = 0.0;
        float impacts = 0.0;
        // The dial does two different things either side of 1. Below it, fewer cells take
        // their turn; above it, every cell does AND the grid gets finer, because once they
        // all fire there is no more rain to be had out of the same number of cells.
        float fireChance = saturate(0.6 * RainRingDensity);
        float cellCrowding = 1.0 + 0.45 * max(0.0, RainRingDensity - 1.0);
        [unroll]
        for (int ri = 0; ri < 2; ri++)
        {
            float cellScale = ((ri == 0) ? 2.0 : 3.4) * cellCrowding;
            float2 layerOffset = (ri == 0) ? float2(0.0, 0.0) : float2(0.41, 0.77);
            float2 ringGrid = (worldTile + layerOffset) * cellScale;
            float2 ringCell = floor(ringGrid);
            float cellRandom = hash(ringCell + 7.7);
            float rateRandom = hash(ringCell + 3.1);
            float placeRandom = hash(ringCell + 19.4);
            float cycle = t * (0.55 + 0.5 * rateRandom) + cellRandom * 7.0;
            float phase = frac(cycle);
            // A different draw every cycle, so a cell that just rang may sit the next one out.
            float fires = step(1.0 - fireChance, hash(ringCell + floor(cycle) * 0.137 + 5.3));
            float2 dropAt = (float2(rateRandom, placeRandom) - 0.5) * 0.7;
            float toDrop = length(frac(ringGrid) - 0.5 - dropAt);
            float radius = phase * 0.46 * RainRingSize;
            // The wall grows with the ring: a big ring drawn with a thin wall reads as a wire
            // circle rather than as water moving.
            float thickness = (0.018 + 0.055 * phase) * RainRingSize;
            float fade = (1.0 - phase) * (1.0 - phase);
            rings += smoothstep(thickness, 0.0, abs(toDrop - radius)) * fade * fires;
            // The strike itself: a hard bright point for an instant before the ring leaves it.
            impacts += smoothstep(0.055 * RainRingSize, 0.0, toDrop) * smoothstep(0.14, 0.0, phase) * fires;
        }
        col.rgb += (rings * 0.15 + impacts * 0.30) * RainAmt * water * RainRingStrength;
    }

    // Whole-pass presence: fade the finished surface back to the pixel the game drew, so the
    // stage's total contribution is already zero by the time it is dropped from the stage list.
    col.rgb = lerp(tex2D(SourceSampler, uv).rgb, col.rgb, Presence);
    return col;
}

technique Water { pass P0 { PixelShader = compile PS_SHADERMODEL WaterPS(); } }
