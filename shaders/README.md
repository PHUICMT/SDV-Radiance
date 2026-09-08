# Shaders

HLSL `.fx` sources live here. They are compiled to MonoGame `.mgfxo` effects
and loaded at runtime. The compiled `.mgfxo` files are committed under
`../assets/` so the mod runs without users installing a shader compiler.

| File | Purpose |
|------|---------|
| `bloom.fx` | bright-pass + gaussian blur + composite |
| `colorgrade.fx` | tone mapping + palette by time / season / weather |
| `fog.fx` | wispy day fog + night mist (drifting layers of a CPU-baked tileable noise texture) |
| `cloudshadow.fx` | drifting cloud-shadow banks (same baked noise texture as fog) |
| `tiltshift.fx` | top/bottom or radial depth-of-field blur |
| `water.fx` | water ripple, sparkle, and screen-space reflection |
| `finishing.fx` | vignette + chromatic aberration |
| `lighting.fx` | dynamic 2D lighting (darken flat areas, pool light) |
| `floodlight.fx` | flood-propagation GI lightmap composite |
| `cascades.fx` | radiance cascades: the bounce lightmap worked out on the card, as rays over a probe grid |
| `normals.fx` | a normal map for a whole sheet, read off the art itself, for the sprite relief |
| `reliefreplay.fx` | every world sprite drawn a second time into the relief buffer, grouped by sheet with the depth carried in the tint |
| `shadowmask.fx` | the player's shadow cut by the map, per pixel, while it is drawn into its world-anchored patch |
| `sheetscale.fx` | a sheet at twice its size: the Scale2x (EPX) rule, and an xBR-style rule for the softer look |
| `tail.fx` | the fused tail: colour grade and vignette in one full-screen draw |
| `upscale.fx` | render-scale upscale with contrast-adaptive sharpening (RCAS) |
| `wet.fx` | ground that remembers the rain, plus the damp band along a shoreline |

## Compiling

```
mgfxc bloom.fx ../assets/bloom.mgfxo /Profile:OpenGL
```

Target Shader Model `4.0_level_9_x` for broad MonoGame/OpenGL compatibility.
The `.mgfxo` format must match the game's MonoGame build (3.8.0.1641); a newer
compiler produces a binary the game rejects. See the main README for the exact
toolchain.
