---
name: clibridge4unity-shaders
description: Write, edit, or debug Unity shaders, material uniforms, and texture import settings via clibridge4unity. Covers Built-in RP + CGPROGRAM surface shaders, mobile precision, GPU instancing, runtime material grading (editing a shared/Addressable material's uniforms at runtime), AND the recurring failure mode on these clients — "works in the editor, wrong/crashes on device (iOS/Android)" editor-vs-device divergence. Auto-trigger on .shader / .cginc / .hlsl edits, surface shaders, UNITY_INSTANCING_BUFFER, MaterialPropertyBlock, new Material()/CopyPropertiesFromMaterial, skybox/grading/exposure/tint sliders, shader_feature-vs-multi_compile, GetPixels/ReadPixels, ASTC/PVRTC/ETC2, maxTextureSize, "different on device", "shader error", or per-platform texture overrides. Compute shaders → use `clibridge4unity-compute-shaders`; CommandBuffers → use `clibridge4unity-command-buffers`; RenderTextures → use `clibridge4unity-render-textures`.
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
- **An optional feature with real per-pixel complexity is a compiled-out keyword, not a runtime branch** (`[KeywordEnum]`/`[Toggle(_FEATURE)]` → `#pragma shader_feature`/`multi_compile` → `#if defined(_FEATURE)`) — **always** gate it default-off so the off state compiles the work *out*; mobile dynamic branching is avoided. **Keep the variant count low at the same time, but never by under-gating:** don't gate cheap always-on math (tint/exposure run unconditionally — branch-free, 1 variant), and fold related options into **one** `[KeywordEnum]` (N values = N variants, additive) rather than many independent `_ _A`/`_ _B` toggles (each boolean *doubles* the count → 2ᴺ). For a keyword toggled on a material created at **runtime**, use `multi_compile` not `shader_feature` (else the variant is stripped from the build) — see *Editing a shared material at runtime* below.

## Micro-optimizing the inner loop (mobile ALU) — branch, unroll, vectorize

Three knobs once the math is correct. Each trades against the mobile realities the rest of this skill assumes: scalar dynamic branches and dynamic-length loops are expensive, and the ALU is 4-wide so scalar code wastes ¾ of it.

- **`UNITY_BRANCH` only on a *uniform* condition guarding *expensive* work.** It maps to `[branch]` (a real jump); `UNITY_FLATTEN` (`[flatten]`) runs both sides and selects. A real branch pays off **only** when the condition is coherent across the draw (same for every pixel — i.e. driven by a material/uniform value, not per-pixel data) **and** the skipped block is heavy (a loop, extra texture samples). Then it genuinely skips the cost. For a cheap or per-pixel-**divergent** condition, the branch costs more than the work it guards → use `UNITY_FLATTEN` or branch-free `lerp`/`step`/`saturate` math instead. Best of all, if the toggle is a build-time choice, compile it out with a keyword (the house rule above) — reach for `UNITY_BRANCH` only when the condition is a genuine **runtime uniform** that can't be a `shader_feature`.
- **Unroll fixed-count loops; keep the count a compile-time constant.** `UNITY_UNROLL` (`[unroll]`) drops the per-iteration branch, enables constant-folding and static (not dynamic) register indexing, and lets the compiler batch texture fetches — a clear win for small fixed counts (blur taps, kernel samples). The count must be a literal / `#define` / keyword, **not** a uniform `int`, or it can't unroll. Don't unroll a *large* count: instruction count and register pressure explode and can blow the mobile instruction limit. For a genuinely variable count use `UNITY_LOOP` (`[loop]`) and accept the branch; if the variability is really 2–3 discrete cases, a keyword per case unrolls each.
- **Vectorize scalar math; fold linear combinations into a `mul`.** The ALU is SIMD-4 — four scalar `float` ops occupy one lane and waste the other three, while one `float4` op does all four at the same cost. Operate on `float4`/swizzles, not four separate `float`s. A weighted sum of four terms is one `dot`; four such sums (a channel mix, color grade, or small linear transform) is one matrix `mul` — `mul(M, v)` lowers to hardware `mad`/dot instructions (≈4 MACs apiece), far cheaper than the hand-written scalar expansion. Compose transform/grade matrices **once** (on the CPU into a uniform `float4x4`, or once per vertex) and `mul` per pixel, rather than chaining scalar ops every pixel. Caveat: matrix uniforms consume constant registers — worth it when it replaces real per-pixel ALU, not to dress up a single scalar multiply.

  Worked example — a terrain shader blending 4 layer colors by weight `cL`:
  ```hlsl
  // BEFORE — 12 scalar swizzles rebuild the matrix in every fragment
  half3x4 mL = half3x4(
      _LayerColor0.r, _LayerColor1.r, _LayerColor2.r, _LayerColor3.r,   // row 0 = the .r of each layer
      _LayerColor0.g, _LayerColor1.g, _LayerColor2.g, _LayerColor3.g,   // row 1 = the .g
      _LayerColor0.b, _LayerColor1.b, _LayerColor2.b, _LayerColor3.b);  // row 2 = the .b
  albedo = mul(mL, cL) * 2;

  // AFTER — rows pre-packed CPU-side into 3 half4 uniforms; the shader just muls
  half3x4 mL = half3x4(_LayerColMatA0, _LayerColMatA1, _LayerColMatA2);
  albedo = mul(mL, cL) * 2;
  ```
  Three things this requires (all silent-failure-prone): (a) C# must `SetVector` `_LayerColMatA0/1/2` **and** they must be declared in `Properties{}`/`uniform` in every active include — an unset/undeclared uniform is a no-op (#6), not an error. (b) Pack **per-channel, not per-layer**: row 0 is `(_LayerColor0.r, _LayerColor1.r, _LayerColor2.r, _LayerColor3.r)` — packing per-layer RGBA transposes the matrix and scrambles colors; confirm with a `SCREENSHOT`, not just a clean compile. (c) Once nothing reads the old `_LayerColor0..N`, drop them to reclaim constant registers — grep every SubShader/include first (#5), since a LOW SubShader on device may still reference them while HIGH uses the matrix.

## Editing a shared material at runtime (grading, runtime-tweaked uniforms)

Runtime sliders/grading that write a material's uniforms hit a cluster of traps. The pattern below avoids all of them while keeping the shader/variant count low.

1. **Never `Set*` the shared/cached asset — instance it.** A material returned by an Addressable loader (or `Resources.Load`, or any cached getter) is *shared*: `material.SetColor/SetFloat` on it leaks this caller's look onto every other user of that material **and** dirties the source `.mat` in the editor. → `var graded = new Material(shared);` once, keep and reuse it, `graded.CopyPropertiesFromMaterial(shared)` when re-basing to a new preset, and `Destroy`/`SafeDestroy` the instance in `OnDestroy` (you own its lifetime now).
2. **`MaterialPropertyBlock` is the cheaper override — but not everywhere.** A `Renderer` takes an MPB with zero material instances (see `clibridge4unity-performance`). **Skyboxes can't** — `RenderSettings.skybox` has no renderer/MPB path, so sky grading *must* use an instance per #1. Use MPB for per-renderer tweaks; instance only when there's no renderer to hang an MPB on.
3. **Expose a NEW neutral-default property; don't rebind an existing non-neutral one.** Binding a color picker to a `_Tint` that defaults to `0.5` gray (neutral only because the shader does `* unity_ColorSpaceDouble`) shows gray as "neutral" → confusing UI. → Add a dedicated `_SkyTint` defaulting to white; leave the engine prop alone. The control's *default* must read as neutral to the user, and the new prop must default to a no-op so existing materials render identically.
4. **Cheap, always-applied math → run unconditionally, no keyword.** A tint multiply, an exposure scale, or a `dot`+`lerp` (saturation/vibrance) is cheaper than the branch that would skip it, and costs **0 extra variants**. Even an *optional* cheap op is better left always-on as a no-op (multiply by 1, lerp by 0) than spending a variant to toggle it.
5. **An optional feature with real complexity → ALWAYS gate it behind a keyword, default-off.** Firm rule, not a "maybe": if a feature is (a) not always needed **and** (b) more than a few ALU ops — a per-pixel loop, `sincos`/`cross`/`normalize`, extra texture samples (e.g. a hue rotation over a full-screen background) — put it behind a keyword so the off state compiles the work *out*. Do **not** run it unconditionally (the disabled case would pay full cost every pixel) and do **not** hide it behind a uniform-float `if` (mobile dynamic branching is avoided, and the off path still costs registers + the branch).
6. **Keep the variant count low by gating selectively — never by under-gating.** Low shader count comes from two disciplines, and *neither* is "skip the keyword on a complex optional feature": (a) don't gate cheap always-on math (#4 — that stays 1 variant); (b) fold related sub-options into **one** `[KeywordEnum]` (N values = N variants, additive) instead of N independent boolean toggles, since each boolean `multi_compile` *doubles* the count (2ᴺ). Gate at the granularity of the real feature, then coalesce. Net target: neutral state ≈ the original shader plus a few multiplies, and a tiny, bounded number of variants — each one earning its place by removing genuine per-pixel cost.
7. **Runtime-created material → `multi_compile`, not `shader_feature`, for the keyword you keep.** A `shader_feature` variant on a material you `new` at runtime is referenced by no serialized asset, so the build strips it and the feature silently no-ops on device. → `#pragma multi_compile _ _MYFEATURE_ON` keeps both variants (just 2). Reserve `shader_feature` for keywords set on serialized material *assets* — those ship only the used variants, which is exactly why they're cheaper when they apply.
8. **Don't let the runtime instance leak into edit-mode serialized state.** If the same path also runs in the level editor, return the *shared* material in non-play mode and create the instance only at runtime — otherwise the grading instance can serialize into the scene's lighting/skybox settings. Zero edit-mode regression; grading applies only where the runtime sliders live.
9. **Direction-sampled lookups ignore uniform scale.** A cubemap/skybox is sampled by view direction at infinite distance, so scaling the direction uniformly samples the same texel — a no-op. Only a *non-uniform* tweak moves pixels (`dir.y *= _SkyScale` raises/lowers the apparent horizon). Don't expose a "scale" control that can't actually change the image.

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

## Verification — never `COMPILE` for a shader edit

**`COMPILE` does nothing for a shader.** It triggers C# *script* recompilation + a domain reload (and breaks the pipe) — it does **not** compile HLSL. A `.shader`/`.cginc`/`.hlsl` edit is an **asset import**: Unity reimports and recompiles the shader on save (auto-refresh, or force it with `REFRESH`), and shader compile errors surface in the console. So for shader-body edits, skip `COMPILE` entirely — `REFRESH` (if needed) + `STATUS`/`LOG errors` is the whole loop. Reserve `COMPILE` for when the *C# wiring* genuinely needs a rebuild, never to "make the shader take".

Same discipline as `clibridge4unity-lint`: **`STATUS` first, escalate only on evidence.** Caveat: **offline `LINT` compiles C#, not HLSL** — it can't catch a typo inside a `.shader`/`.cginc`.

- **C# material wiring (`Set*` / `MaterialPropertyBlock`):** `clibridge4unity LINT`, then confirm the uniform exists in every active include (#6). No COMPILE.
- **Shader body (`.shader`/`.cginc`):** fast path is **reading and grepping** — every include path (#5), the keyword `#define` ladder (#8), and a C# grep for any renamed tag/property (#7).
- **Texture / placement math:** inspect the `.meta` per-platform block above — no compile needed.
- **For true HLSL ground truth:** `REFRESH` to reimport the shader, then `LOG errors` to read per-variant shader compile failures. This is the shader equivalent of "compile" — `COMPILE` (script recompile) is the wrong tool and won't surface them.

```bash
clibridge4unity STATUS               # are there errors?  (always first)
clibridge4unity LINT                 # C# only, offline, sub-second
clibridge4unity REFRESH              # reimport changed shaders (what actually recompiles HLSL)
clibridge4unity LOG errors           # read shader compile errors after reimport
# COMPILE is for C# script changes — NOT needed for .shader/.cginc/.hlsl edits
```

## Related
- `clibridge4unity-compute-shaders` — `.compute` kernels, ComputeBuffer, dispatch sizing
- `clibridge4unity-command-buffers` — `CommandBuffer` custom render insertion, Frame Debugger
- `clibridge4unity-render-textures` — `RenderTexture` lifecycle, `Graphics.Blit`, RT pool
- `clibridge4unity-performance` — `MaterialPropertyBlock` + cached `Shader.PropertyToID`
- `clibridge4unity-editor-tools` — `AssetPostprocessor` for enforcing texture import settings
