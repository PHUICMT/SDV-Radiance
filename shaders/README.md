# Shaders

HLSL `.fx` sources live here. They are compiled to MonoGame `.mgfxo` effects
(into `../build/`) and loaded at runtime.

Planned effects (see the roadmap in the main README):

| File | Phase | Purpose |
|------|-------|---------|
| `bloom.fx` | 1 | bright-pass + gaussian blur + composite |
| `colorgrade.fx` | 2 | tone mapping + palette by time/season/weather |
| `fog.fx` | 2 | screen-space volumetric fog / god rays |
| `cloudshadow.fx` | 3 | animated cloud shadow overlay |
| `tiltshift.fx` | 3 | top/bottom depth-of-field blur |
| `water.fx` | 4 | water ripple / distortion |
| `finish.fx` | 4 | vignette / chromatic aberration / DoF |

## Compiling

```
mgfxc bloom.fx ../build/bloom.mgfxo /Profile:OpenGL
```

Target Shader Model `4.0_level_9_x` for broad MonoGame/OpenGL compatibility.
No compiled `.mgfxo` files are committed (they are build artifacts; see `.gitignore`).
