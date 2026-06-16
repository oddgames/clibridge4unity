---
name: clibridge4unity-shaders
description: Write, edit, or debug Unity shaders, material uniforms, and texture import settings via clibridge4unity. Covers Built-in RP + CGPROGRAM surface shaders, mobile precision, GPU instancing, AND the recurring failure mode on these clients — "works in the editor, wrong/crashes on device (iOS/Android)" editor-vs-device divergence. Auto-trigger on .shader / .cginc / .hlsl edits, surface shaders, UNITY_INSTANCING_BUFFER, MaterialPropertyBlock, GetPixels/ReadPixels, ASTC/PVRTC/ETC2, maxTextureSize, "different on device", "shader error", or per-platform texture overrides. Compute shaders → use `clibridge4unity-compute-shaders`; CommandBuffers → use `clibridge4unity-command-buffers`; RenderTextures → use `clibridge4unity-render-textures`.
---

# Unity shaders & GPU work

Standard Unity/HLSL precision, surface-shader, instancing, and texture-import rules apply as you already know them — below is only what's project-specific: the house authoring style and the editor-vs-device divergence that bites these mobile clients (the editor is forgiving; the device is strict).

## Authoring (house style)

- **Built-in RP, CGPROGRAM surface shaders only.** No URP, no HLSLPROGRAM, no ShaderGraph in authored code.
- **Precision convention:** `float` for positions / world-space / any UV that feeds a sub-rect remap; `half` for lighting and interpolants; `fixed`/`fixed4` for colors.
- **Standard pragma block** — strips the deferred/prepass/extra-light paths these clients never use:
  ```
  #pragma surface surf Standard exclude_path:deferred exclude_path:prepass nolightmap nodynlightmap noforwardadd nolppv
  #pragma multi_compile_instancing
  #pragma skip_variants LIGHTPROBE_SH POINT POINT_COOKIE SPOT SHADOWS_DEPTH SHADOWS_CUBE
  #pragma target 3.0
  ```
- **Per-instance color carries through the instancing buffer**; vertex-color channels mask sub-part recolors on one instanced mesh:
  `o.Albedo = lerp(o.Albedo, UNITY_ACCESS_INSTANCED_PROP(Props, _PartColor), IN.color.r);`
- **Optional features are shader keywords, not runtime branches** (`[KeywordEnum]`/`[Toggle(_FEATURE)]` → `#pragma shader_feature` → `#if defined(_FEATURE)`) — mobile dynamic branching is avoided.

## The one rule that prevents most device bugs

**Never compute placement / UV / sampling math from a value that differs between editor and device** — a runtime texture's `.width`/`.height`, `fixed`/`half` precision in coordinates, or which SubShader runs. Bake resolution-independent constants (percentages / UV 0–1) at edit time and feed those to the GPU.

## Editor-vs-device pitfall catalog (this project)

1. **Per-platform texture downsize breaks runtime `texture.width/height` math.** iOS/Android carry a `maxTextureSize` override (e.g. 1024) while editor/Standalone stays 2048, so `tex.width` differs by platform. → Bake UV rects (0–1) at edit time; if deriving at edit time, normalize by the **source** size (`TextureImporter.GetSourceTextureWidthAndHeight`), never `tex.width`.
2. **Categorical / positional textures must be uncompressed on EVERY platform.** Block compression interpolates across blocks — catastrophic for id maps, masks, label/lookup atlases (anything you threshold or value-match). → Pin `textureFormat: 1` (Alpha8) or RGBA32, `textureCompression: 0`, fixed `maxTextureSize` on all platforms, and enforce it in editor code (a reimport silently resets per-platform format back to ASTC).
3. **Reading compressed textures on the CPU is unreliable.** `GetPixel`/`GetPixels32` throw "Unsupported texture format" on many runtime-compressed formats (PVRTC, ETC2, some ASTC). `isReadable: 1` is required regardless. → For CPU reads on a hot path, or where #2's correctness also matters, use an explicitly uncompressed format (Alpha8/RGBA32) on the target platform.
4. **`fixed`/`half` UV quantizes once remapped into a small sub-rect** (error ∝ 1/rectSize), invisible in editor (which promotes to float). → Use `float2` for any UV feeding a sub-rect remap, threshold, or precise lookup.
5. **Parallel high/low SubShader + multiple include paths — edit ALL of them.** The device frequently picks the LOW SubShader while the editor renders the HIGH one. A feature added only to the high include is invisible on device (the classic livery-overlay bug: added to the plain include, missing from livery + livery-low includes). → Grep every include the target materials can hit and add the feature to each.
6. **C# uniform set on a shader that never declares it = silent no-op.** `material.SetVector("_Foo", …)` with no matching property does nothing — no error. → Verify the uniform exists in `Properties{}` AND as a `uniform`/sampler in every active include before debugging the C#.
7. **SubShader `Tags` can be a load-bearing C# contract.** Paint/decal/opacity systems discover renderers by reading custom tags (`PaintableObj_ShaderSupportsPaint`, `DecalableObj_DecalTexPropertyName="_Decal"`, `ObjectOpacity_AlternateShaderName`, …). Renaming a tag or the property it points at breaks the runtime system with **no compile error**. → Grep the C# for the old string before renaming. The tag value is an API.
8. **`shader_feature` keyword combos hide features** (variant cousin of #5). A block under `#if defined(FOUR_CHANNEL)` is invisible when the material is `FOUR_CHANNEL_DECAL`, unless the combined keyword `#define`s the base one. → Enumerate every `[KeywordEnum]` value / `shader_feature` combo the live materials use and confirm the `#if` ladder covers each.
9. **Build-time shader/Addressables validation hard-fails the player build.** The preprocess step runs during the build. Read the `[Preprocess Player]` / `[Packaging assets]` lines — they're the real cause, not the trailing `BUILD FAILED`.

## Inspect `.meta` per-platform overrides (no Unity connection, no compile)

For any texture a shader samples or measures, read its `.meta` `platformSettings` — catches pitfalls #1–#4 offline:
```
platformSettings:
- buildTarget: iOS
  maxTextureSize: 1024     # downsized vs editor's 2048
  textureFormat: 50        # 50 = ASTC_6x6 (lossy); 1 = Alpha8; -1 = auto/RGBA
  textureCompression: 1    # 1 = compressed, 0 = uncompressed
  overridden: 1            # 1 = this platform diverges from Default
```
`overridden: 1` with a smaller `maxTextureSize` or a lossy `textureFormat` is the red flag.

## Verification — fast first, COMPILE last (and rarely)

Same discipline as `clibridge4unity-lint`: **`STATUS` first, escalate only on evidence, do NOT `COMPILE` per edit.** Caveat: **offline `LINT` compiles C#, not HLSL** — it can't catch a typo inside a `.shader`/`.cginc`.

- **C# material wiring (`Set*` / `MaterialPropertyBlock`):** `clibridge4unity LINT`, then confirm the uniform exists in every active include (#6). No COMPILE.
- **Shader body (`.shader`/`.cginc`):** fast path is **reading and grepping** — every include path (#5), the keyword `#define` ladder (#8), and a C# grep for any renamed tag/property (#7).
- **Texture / placement math:** inspect the `.meta` per-platform block above — no compile needed.
- **Only then, once:** if several shader edits need true HLSL ground truth, run a single `COMPILE` then `LOG errors` to read per-variant failures. Batched at the end — never per-file.

```bash
clibridge4unity STATUS               # is compile dirty / are there errors?  (always first)
clibridge4unity LINT                 # C# only, offline, sub-second
clibridge4unity LOG ui errors        # current USS/UXML/TSS import errors
# clibridge4unity COMPILE && clibridge4unity LOG errors   # last resort, batched, breaks pipe
```

## Related
- `clibridge4unity-compute-shaders` — `.compute` kernels, ComputeBuffer, dispatch sizing
- `clibridge4unity-command-buffers` — `CommandBuffer` custom render insertion, Frame Debugger
- `clibridge4unity-render-textures` — `RenderTexture` lifecycle, `Graphics.Blit`, RT pool
- `clibridge4unity-performance` — `MaterialPropertyBlock` + cached `Shader.PropertyToID`
- `clibridge4unity-editor-tools` — `AssetPostprocessor` for enforcing texture import settings
