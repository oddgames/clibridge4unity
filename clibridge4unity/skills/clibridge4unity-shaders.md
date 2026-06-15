---
name: clibridge4unity-shaders
description: Write, edit, or debug Unity shaders, material uniforms, and texture import settings via clibridge4unity. Covers Built-in RP + CGPROGRAM surface shaders, mobile precision, GPU instancing, AND the recurring failure mode on these clients — "works in the editor, wrong/crashes on device (iOS/Android)" editor-vs-device divergence. Auto-trigger on .shader / .cginc / .hlsl edits, surface shaders, UNITY_INSTANCING_BUFFER, MaterialPropertyBlock, GetPixels/ReadPixels, ASTC/PVRTC/ETC2, maxTextureSize, "different on device", "shader error", or per-platform texture overrides. Compute shaders → use `clibridge4unity-compute-shaders`; CommandBuffers → use `clibridge4unity-command-buffers`; RenderTextures → use `clibridge4unity-render-textures`.
---

# Unity shaders & GPU work

Two jobs: **author in the house style**, and **avoid editor-vs-device divergence** — the bug class that actually bites on these mobile clients. The editor is forgiving (float precision, uncompressed textures, full-size imports, the high SubShader); the device is strict. The editor will lie to you.

## Authoring

- **Built-in RP, CGPROGRAM surface shaders.** No URP, no HLSLPROGRAM, no ShaderGraph in authored code.
- **Mobile precision:** `float` for positions / world-space / any UV that feeds a sub-rect remap; `half` for lighting and interpolants; `fixed`/`fixed4` for colors.
- **Performance pragma block — strips the deferred/prepass/extra-light paths these clients never use:**
  ```
  #pragma surface surf Standard exclude_path:deferred exclude_path:prepass nolightmap nodynlightmap noforwardadd nolppv
  #pragma multi_compile_instancing
  #pragma skip_variants LIGHTPROBE_SH POINT POINT_COOKIE SPOT SHADOWS_DEPTH SHADOWS_CUBE
  #pragma target 3.0
  ```
- **GPU instancing carries per-instance color.** Declare colors in `UNITY_INSTANCING_BUFFER_START/END`, read with `UNITY_ACCESS_INSTANCED_PROP`. Vertex-color channels can mask sub-part recolors on one instanced mesh: `o.Albedo = lerp(o.Albedo, UNITY_ACCESS_INSTANCED_PROP(Props, _PartColor), IN.color.r);`.
- **Properties decorators:** `[Header(...)]` groups, `[Toggle]`/`[KeywordEnum]` for features, `[PowerSlider(2.0)]` for non-linear ranges, `[NoScaleOffset]` for untiled maps, `[HideInInspector]` for runtime-only props.
- **Optional features are shader keywords, not runtime branches:** `[KeywordEnum]`/`[Toggle(_FEATURE)]` → `#pragma shader_feature` → `#if defined(_FEATURE)`. Mobile dynamic branching is avoided.

### New-shader skeleton (clone, then rename path / props / surf body)

```hlsl
Shader "<Path>/<Name>"
{
    Properties
    {
        [Header(Main Maps)]
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _Color   ("Color", Color) = (1,1,1,1)        // per-instance tint
        [Header(Surface)]
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic   ("Metallic", Range(0,1))   = 0.0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard exclude_path:deferred exclude_path:prepass nolightmap nodynlightmap noforwardadd nolppv
        #pragma multi_compile_instancing
        #pragma skip_variants LIGHTPROBE_SH POINT POINT_COOKIE SPOT SHADOWS_DEPTH SHADOWS_CUBE
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        struct Input { float2 uv_MainTex; float2 uv_BumpMap; fixed4 color : COLOR; };
        half _Glossiness;
        half _Metallic;

        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
            o.Albedo     = c.rgb;
            o.Normal     = UnpackNormal(tex2D(_BumpMap, IN.uv_BumpMap));
            o.Metallic   = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha      = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
```

## The one rule that prevents most device bugs

**Never compute placement / UV / sampling math from a value that differs between editor and device** — a runtime texture's `.width`/`.height`, `fixed`/`half` precision in coordinates, or which SubShader runs. Bake resolution-independent constants (percentages / UV 0–1) at edit time and feed those to the GPU.

## Editor-vs-device pitfall catalog

1. **Per-platform texture downsize breaks runtime `texture.width/height` math.** iOS/Android carry a `maxTextureSize` override (e.g. 1024) while editor/Standalone stays 2048, so `tex.width` differs by platform. `pixelCoord / tex.width` diverges on device only. → Bake UV rects (0–1) at edit time; if you must derive at edit time, normalize by the **source** size (`TextureImporter.GetSourceTextureWidthAndHeight`), never `tex.width`.
2. **Categorical / positional textures must be uncompressed on EVERY platform.** Lossy block compression (ASTC/BC/PVRTC/ETC2) interpolates across blocks — fine for color art, catastrophic for id maps, masks, label/lookup atlases (anything you threshold or value-match). → Pin `textureFormat: 1` (Alpha8) or RGBA32, `textureCompression: 0`, fixed `maxTextureSize` on all platforms, and enforce it in editor code (a reimport silently resets per-platform format back to ASTC).
3. **ASTC/PVRTC textures are NOT CPU-readable.** `GetPixel`/`GetPixels32`/`ReadPixels` crash or return garbage on device; the editor has an uncompressed copy so it "works". → Any CPU-read texture needs `isReadable: 1` AND an uncompressed format on the target platform.
4. **`fixed`/`half` precision differs.** Desktop/editor silently promote to `float`; mobile honors true precision (`fixed` ≈ 8-bit, `half` ≈ 16-bit). A `fixed2`/`half2` UV is fine full-surface but quantizes badly once remapped into a small sub-rect (error ∝ 1/rectSize). → Use `float2` for any UV feeding a sub-rect remap, threshold, or precise lookup. An editor screenshot won't reveal this.
5. **Parallel high/low SubShader + multiple include paths — edit ALL of them.** The device frequently picks the LOW SubShader while the editor renders the HIGH one. A feature added only to the high include works in the editor and is invisible on device (the classic livery-overlay bug: added to the plain include, missing from the livery + livery-low includes). → When adding a body/vehicle-shader feature, grep every include the target materials can hit and add it to each.
6. **C# uniform set on a shader that never declares it = silent no-op.** `material.SetVector("_Foo", …)` with no matching property does nothing — no error. → Verify the uniform exists in `Properties{}` AND as a `uniform`/sampler in every active include before debugging the C#.
7. **SubShader `Tags` can be a load-bearing C# contract.** Paint/decal/opacity systems discover renderers by reading custom tags (`PaintableObj_ShaderSupportsPaint`, `DecalableObj_DecalTexPropertyName="_Decal"`, `ObjectOpacity_AlternateShaderName`, …). Renaming a tag, or the property it points at, breaks the runtime system with **no compile error**. → Grep the C# for the old string before renaming a property/tag. The tag value is an API.
8. **`shader_feature` keyword combos hide features** (the variant cousin of #5). A block under `#if defined(FOUR_CHANNEL)` is invisible when the material is `FOUR_CHANNEL_DECAL`, unless the combined keyword `#define`s the base one. → When adding a feature behind a keyword, enumerate every `[KeywordEnum]` value / `shader_feature` combo the live materials use and confirm the `#if` ladder covers each.
9. **Build-time shader/Addressables validation hard-fails the player build.** The preprocess step runs during the build, not at runtime. Read the `[Preprocess Player]` / `[Packaging assets]` lines — they're the real cause, not the trailing `BUILD FAILED`.

## Inspect `.meta` per-platform overrides (no Unity connection, no compile)

For any texture a shader samples or measures, read its `.meta` `platformSettings` — this catches pitfalls #1–#4 offline:
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

Follow the same discipline as `clibridge4unity-lint`: **`STATUS` first, escalate only on evidence, and do NOT `COMPILE` per edit** — the domain reload is expensive and breaks the pipe.

Shader-specific caveat: **offline `LINT` compiles C#, not HLSL**, so it can't catch a typo inside a `.shader`/`.cginc`. Choose the fast path by what you touched:

- **C# material wiring (`Set*` / `MaterialPropertyBlock`):** `clibridge4unity LINT` is the fast move — lint, confirm the uniform exists in every active include (#6), next. No COMPILE.
- **Shader body (`.shader`/`.cginc`):** the fast path is **reading and grepping** — every include path (#5), the keyword `#define` ladder (#8), and a C# grep for any renamed tag/property (#7). This catches the structural bugs that actually ship-break.
- **Texture / placement math:** inspect the `.meta` per-platform block above — no compile needed.
- **Only then, once:** if several shader edits need true HLSL ground truth (real variant errors, source generators), run a single `COMPILE` then `LOG errors` to read per-variant failures. One batched run at the end — never a per-file habit.

```bash
clibridge4unity STATUS               # is compile dirty / are there errors?  (always first)
clibridge4unity LINT                 # C# only, offline, sub-second
clibridge4unity LOG ui errors        # current USS/UXML/TSS import errors
# clibridge4unity COMPILE && clibridge4unity LOG errors   # last resort, batched, breaks pipe
```

## Related
- `clibridge4unity-compute-shaders` — `.compute` kernels, `RWStructuredBuffer`/`ComputeBuffer`, dispatch + thread-group sizing, mobile binding limits
- `clibridge4unity-command-buffers` — `CommandBuffer` for custom render insertion (post-process, decal compositing, paint masks); Frame Debugger workflow
- `clibridge4unity-render-textures` — `RenderTexture` lifecycle, `Graphics.Blit`, temporary RT pool, format/depth choices
- `clibridge4unity-performance` — `MaterialPropertyBlock` + cached `Shader.PropertyToID` are also perf patterns
- `clibridge4unity-editor-tools` — `AssetPostprocessor` for enforcing texture import settings
