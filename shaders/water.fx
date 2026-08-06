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
// UNDILATED mask — true water bodies only. The effect-coverage mask above is dilated
// two tiles (bank rings), which swallows bridges/piers; anything that reasons about
// WHERE WATER REALLY IS (shoreline search, water-on-water damping, the grey-pool
// pixel gate) must use this one.
texture MaskCoreTexture;
sampler2D MaskCoreSampler = sampler_state
{
    Texture = <MaskCoreTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D MaskCoreLinearSampler = sampler_state
{
    Texture = <MaskCoreTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
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
texture SdfTexture;
sampler2D SdfSampler = sampler_state
{
    Texture = <SdfTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

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
float RainAmt;          // 0–1 raining: expanding drop rings on the surface
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
sampler2D ReflectRTSampler = sampler_state
{
    Texture = <ReflectRTTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};
float SceneOn;           // 1 when the sprite-free scenery source below is live
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
    float inSprite = SpriteMaskOn * step(0.05, tex2D(SpriteMaskSampler, uv).a);
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
    // A displaced tap must never land ON a sprite (that smeared duck/player pixels
    // sideways into the water next to them) — fall back to the undisplaced sample there.
    float tapSprite = SpriteMaskOn * step(0.05, tex2D(SpriteMaskSampler, uv + ripple).a);
    float tapPlayer = 0.0;
    {
        float2 tuv = ((uv + ripple) - PlayerRect.xy) / pmSpan;
        float tin = step(0.0, tuv.x) * step(tuv.x, 1.0) * step(0.0, tuv.y) * step(tuv.y, 1.0);
        tapPlayer = step(0.15, tex2D(PlayerMaskSampler, saturate(tuv)).a) * tin;
    }
    ripple *= 1.0 - max(tapSprite, tapPlayer);
    // ...and it must not land on the MAP's art either. The rule above was written for sprites
    // only, so a water pixel beside a boat, a pier post or a fountain statue still displaced its
    // tap onto that art and dragged those pixels sideways with the wave - the whole object read
    // as swaying, even though its own pixels were carved out of the mask perfectly. Reported for
    // Willy's boat at the fish shop, for the boat in BoatTunnel, and visible on the fountain.
    // Turning the ripple down only shortens the smear, which is why it never went away.
    float2 tapWT = (uv + ripple) * TilesPerScreen + WorldTileOffset;
    float tapWater = tex2D(MaskSampler, (tapWT - MaskOrigin) / MaskSize).r;
    ripple *= step(0.02, tapWater);
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
        float rowY = floor(worldTile.y * 16.0) / 16.0;
        float wave = sin(rowY * 34.0 + t * 2.6)
                   + 0.45 * sin(rowY * 61.0 - t * 3.9 + worldTile.x * 0.9);
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
        float tileW = 4.0 / (TilesPerScreen.x * 16.0);
        float coreC = WaterAt(float2(mx, uv.y));
        float coreL = WaterAt(float2(mx - tileW, uv.y));
        float coreR = WaterAt(float2(mx + tileW, uv.y));
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
        float2 reflUv = float2(mx + ripple.x * 3.0,
                               edgeV - depth * 1.25 - 0.08 / TilesPerScreen.y + abs(ripple.y) * 2.0);
        reflUv = clamp(reflUv, float2(0.0, 0.0), float2(1.0, 1.0));
        // Prefer the sprite-free scenery source (P3c): the composed screen contains the
        // player/NPCs, and excluding them left body-shaped sky holes in the reflection.
        float3 refl = SceneOn > 0.5
            ? tex2D(SceneSampler, reflUv).rgb * SceneAmbient
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
        float2 dborder = min(reflUv, float2(1.0, 1.0) - reflUv);
        float onScreen = saturate(min(dborder.x, dborder.y) / 0.06);
        // A TRUE mirror only exists when a shoreline was found, the mirrored source is not
        // itself water, and this column actually had water above it (bank-fringe columns
        // don't). Everywhere else, blend to a soft sky-glaze SHEEN instead of cutting to
        // nothing — the hard rectangles between mirrored and unmirrored water came from
        // those cuts, not from the mirror itself.
        float3 mirrorCol = refl * float3(0.66, 0.76, 0.92);   // cool + darken: reads as "in the water"
        // BOUNDED HEIGHT (see docs/water-v3-research). Nothing standing on a shore is taller than
        // ~8 tiles, so a mirrored sample deeper than that cannot be a real reflection — and flat
        // ground (height zero) reflected across a pond is what read as the "green sheet". The
        // honest end of a reflection is SKY, not absence: dissolve the mirror into the sky tint
        // across 5..9 tiles of depth instead of letting it run or cutting it off.
        float depthTiles = depth * TilesPerScreen.y;
        float3 skySurf = lerp(col.rgb, SkyColor, 0.25);
        // ...and the same resolution when the mirrored SOURCE is upstream water. This used to be
        // a 70% DAMP gated at 1.2 tiles, and that gate was a visible horizontal cut right under
        // every bridge (a bridge is ~1 tile tall, so just past its own art the source is river
        // again). Resolving to sky suppresses the same upstream-water streaks with no seam, and
        // it engages at 2..4 tiles so a bridge plus a person standing on it (~2 tiles of genuine
        // reflection) keeps its full band.
        float toSky = max(smoothstep(5.0, 9.0, depthTiles),
                          srcWater * smoothstep(2.0, 4.0, depthTiles));
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
            float4 ent = tex2D(ReflectRTSampler, saturate(uv + ripple * 0.9));
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
            col.rgb = lerp(col.rgb, ent.rgb * float3(0.66, 0.76, 0.92), entAmt * 0.74);
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
    float foamBand = (1.0 - smoothstep(0.5, 5.0, sdfT)) * step(0.0, sdfT);
    float lapPhase = frac(sdfT * 0.45 - Time * 0.30 + hash(floor(worldTile * 16.0) / 16.0) * 0.25);
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

    // ---- Rain: expanding drop rings, one per cell, staggered by a random phase ----
    if (RainAmt > 0.001)
    {
        float2 rg = worldTile * 2.2;
        float2 rc = floor(rg);
        float rr = hash(rc + 7.7);
        float ph = frac(t * 0.7 + rr);
        float2 dropAt = (float2(rr, frac(rr * 9.3)) - 0.5) * 0.35;
        float ring = smoothstep(0.035, 0.0, abs(length(frac(rg) - 0.5 + dropAt) - ph * 0.42));
        col.rgb += ring * (1.0 - ph) * RainAmt * water * 0.10;
    }

    // Whole-pass presence: fade the finished surface back to the pixel the game drew, so the
    // stage's total contribution is already zero by the time it is dropped from the stage list.
    col.rgb = lerp(tex2D(SourceSampler, uv).rgb, col.rgb, Presence);
    return col;
}

technique Water { pass P0 { PixelShader = compile PS_SHADERMODEL WaterPS(); } }
