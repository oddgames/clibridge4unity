---
name: clibridge4unity-compute-shaders
description: Write, edit, or debug Unity Compute Shaders — `.compute` files, kernels, `RWStructuredBuffer`/`RWTexture2D`, `ComputeBuffer` lifetime, `Dispatch` group counts, mobile/gles3/Metal/Vulkan binding limits, `AsyncGPUReadback` for CPU sync, `groupshared` caps. Auto-trigger on `.compute`, `#pragma kernel`, `[numthreads]`, `ComputeBuffer`, `ComputeShader.Dispatch`, `RWStructuredBuffer`, `StructuredBuffer<T>`, `RWTexture2D`, `AsyncGPUReadback`, `groupshared`, "buffer count exceeding hard limit", "Compute Shader not supported", "kernel not found", `SystemInfo.supportsComputeShaders`, `SetBuffer`/`SetTexture` on a ComputeShader. Compute shaders are the workhorse for GPU compute (particles, image processing, mask compositing, paint-decal accumulation) — mobile-binding limits and resource lifetime are real ship-breakers.
---

# Unity Compute Shaders

`.compute` runs arbitrary code on the GPU outside the draw pipeline — perfect for image processing, particle updates, sparse accumulators, batched math. The pitfalls are mostly about *resource lifetime* (ComputeBuffer leaks) and *platform limits* (mobile binding caps that desktop quietly ignores). Same editor-vs-device theme as `clibridge4unity-shaders`: a kernel that runs on desktop may exceed gles3/Metal hard limits on device with `Shader error … on gles3` that never appears in the editor.

## Authoring

- **One kernel per logical pass.** Multiple `#pragma kernel CSName` entries in one `.compute` is fine, but each must be self-contained; sharing `RWStructuredBuffer` between kernels works *only* if both kernels actually use it (otherwise you waste a binding slot).
- **`[numthreads(x,y,z)]` is the GROUP size, not the dispatch count.** A `[numthreads(8,8,1)]` kernel dispatched with `cs.Dispatch(k, 16, 16, 1)` runs `8*8*1 * 16*16*1 = 16384` invocations. `SV_DispatchThreadID` is the global thread id; `SV_GroupThreadID` is within the group.
- **Mobile thread-group ceiling: 128 total** (8×8×2 = 128, 16×8×1 = 128). Desktop accepts up to 1024 silently. Sizes above 128 may compile on desktop and fail on gles3 with `Internal shader error / thread group size exceeds device limit`.
- **`groupshared` is fast but tiny — 16 KB on most mobile GPUs.** Use for inter-thread reductions within a group, not as a scratch table sized to your problem.
- **Texture access:** `Texture2D<float4>` for sampled reads, `RWTexture2D<float4>` for writes. `RWTexture2D` cannot be sampled — only point-read at integer coords via `tex[uint2(x,y)]`.
- **Buffer access:** `StructuredBuffer<T>` for read-only inputs, `RWStructuredBuffer<T>` for read-write. Both use `buffer[i]` indexing; the stride is declared C#-side at `ComputeBuffer` creation and must match the HLSL struct's size + alignment exactly.

### New-compute skeleton (clone, then rename kernel / buffers / body)

```hlsl
#pragma kernel CSAccumulate

// Inputs
StructuredBuffer<float3> _Positions;          // read-only
Texture2D<float4>        _Source;
SamplerState             sampler_LinearClamp;

// Outputs
RWStructuredBuffer<uint> _CountByCell;        // atomic-accumulator
RWTexture2D<float4>      _Result;             // point-write only

// Uniforms
uint  _CountPositions;
float _Threshold;

[numthreads(64, 1, 1)]
void CSAccumulate(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= _CountPositions) return;

    float3 p = _Positions[i];
    uint cell = (uint)(p.x * 16) + (uint)(p.y * 16) * 16;
    InterlockedAdd(_CountByCell[cell], 1);

    float4 s = _Source.SampleLevel(sampler_LinearClamp, p.xy, 0);
    _Result[uint2(p.xy * 256)] = s;
}
```

### Wire it from C#

```csharp
ComputeBuffer _posBuffer;
ComputeBuffer _cellBuffer;

void Dispatch(ComputeShader cs, Vector3[] positions, RenderTexture target)
{
    int kernel = cs.FindKernel("CSAccumulate");

    // Buffer creation must match HLSL stride. float3 = 12 bytes; uint = 4 bytes.
    _posBuffer  ??= new ComputeBuffer(positions.Length, sizeof(float) * 3);
    _cellBuffer ??= new ComputeBuffer(256, sizeof(uint));

    _posBuffer.SetData(positions);
    // Zero-fill _cellBuffer between dispatches if you don't want to accumulate.

    cs.SetBuffer (kernel, "_Positions",   _posBuffer);
    cs.SetBuffer (kernel, "_CountByCell", _cellBuffer);
    cs.SetTexture(kernel, "_Result",      target);
    cs.SetInt    ("_CountPositions",     positions.Length);
    cs.SetFloat  ("_Threshold",          0.5f);

    // Dispatch enough groups to cover positions.Length items at 64 per group.
    int groups = Mathf.CeilToInt(positions.Length / 64f);
    cs.Dispatch(kernel, groups, 1, 1);
}

void OnDisable()
{
    _posBuffer?.Release();  _posBuffer  = null;
    _cellBuffer?.Release(); _cellBuffer = null;
}
```

## Pitfall catalog

1. **`Shader error … 'X': Buffer count exceeding hard limit. No known hw supports this shader … on gles3`** — too many separate buffer bindings for the mobile GL target. gles3 ≈ 8 SSBOs total; Metal/Vulkan are higher but still bounded. → Combine separate buffers into one buffer of struct elements (`RWStructuredBuffer<MyStruct>`), or pack multiple scalars into a `uint4`. Skip the desktop-first design where every input is its own buffer.
2. **`ComputeBuffer` leaks GPU memory if you don't `Release()`.** No GC. A `new ComputeBuffer(n, stride)` lives until you explicitly release; reassigning the C# field doesn't free it. Editor: 5 minutes of recompiles → out-of-memory. Device: silent VRAM bloat → crash on level transition.
   → Pair every `new ComputeBuffer` with a matching `.Release()` in `OnDisable`/`Dispose`. Set the field to null so re-entry can detect "need new buffer." Wrap reusable buffers in an `IDisposable` if they cross system boundaries.
3. **Forgetting to `Create()` a target `RenderTexture` before binding it as `RWTexture2D`.** A fresh `new RenderTexture(...)` is allocated but the GPU resource isn't realised; binding it to a kernel succeeds but writes silently fail. → Always `rt.Create()` after construction OR set `enableRandomWrite = true` before `Create` (random-write is required for `RWTexture2D` regardless).
4. **HLSL struct size ≠ C# struct size + alignment.** A C# `struct { Vector3 pos; float t; }` is 16 bytes (12 + 4). An HLSL `struct { float3 pos; float t; }` packs the same way *by default* — but with `float3 pos` followed by an `int flags` the C# layout might add padding the HLSL doesn't. Mismatch → reads garbage. → Compute stride manually (`sizeof(float)*3 + sizeof(float) = 16`) and pass to `new ComputeBuffer(count, stride)`. Use `[StructLayout(LayoutKind.Sequential)]` on the C# struct. Test with a 1-element buffer dispatched, read back, and assert each field matches.
5. **`AsyncGPUReadback.Request(buffer)` is the only sane CPU-readback path — `GetData` stalls.** `buffer.GetData(array)` blocks the main thread until the GPU finishes. On mobile this can be 100 ms+ → frame hitch. `AsyncGPUReadback.Request` returns a handle; poll `IsDone` or await it; result available 1–3 frames later.
   → Never `GetData` in a hot path. Build async readback into the design; if you need the result *this frame*, redesign so the consumer can wait.
6. **Editor uses different GPU backend than the device.** Windows editor: D3D11/12. Mac editor: Metal. Android device: gles3/Vulkan. iOS device: Metal. The same `.compute` may compile fine on D3D and fail on gles3 (binding limits, precision, syntax extensions). → For mobile targets, periodically switch the editor's graphics API to OpenGL ES 3.0 (Player Settings → Other → Auto Graphics API → off → add GLES3 first) and reproduce there.
7. **`SystemInfo.supportsComputeShaders` is a runtime check.** Always wrap entry points with `if (!SystemInfo.supportsComputeShaders) return;` and provide a CPU fallback (or graceful degradation). WebGL doesn't support compute at all on most browsers.
8. **`FindKernel("Name")` returns -1 silently when the kernel doesn't exist.** Then `Dispatch(-1, ...)` errors with "Invalid kernel index." A typo in the kernel name or `#pragma kernel` line breaks the dispatch at runtime, not at compile.
   → Cache the kernel index in `OnEnable` and `Debug.Assert(_kernel >= 0, "kernel not found")` to fail loudly at the boundary.
9. **`Dispatch(k, 0, 0, 0)` is a silent no-op.** Common when `positions.Length / 64` rounds down to 0 for a partial batch. → Always `Mathf.CeilToInt(count / (float)groupSize)`, and `Math.Max(1, ...)` if you can't be sure `count >= 1`.
10. **Compute shaders ignored by the build if no scene/asset references them.** Player builds strip unreferenced `.compute` files. If you only reference the compute shader by `Resources.Load`, mark it explicitly (or place it under `Resources/` / Addressables). Otherwise the runtime gets `null` and your dispatch silently fails on device.

## Verification

```bash
clibridge4unity STATUS                    # check no script errors
clibridge4unity LOG errors                # shader/compute errors from Unity Console after a dispatch
clibridge4unity CODE_EXEC_RETURN "@/tmp/dispatch-test.cs"   # exercise the kernel via a one-shot
```

For visual verification, `CODE_EXEC_RETURN` a snippet that dispatches the kernel, reads back the first cell of `_CountByCell` via `AsyncGPUReadback`, and returns the value. Compare expected vs actual cheaply.

## Related
- `clibridge4unity-shaders` — surface shaders + material wiring + texture import (the cousin pitfalls)
- `clibridge4unity-render-textures` — `RWTexture2D` targets are RenderTextures with `enableRandomWrite = true`
- `clibridge4unity-command-buffers` — dispatching compute work within a render-frame's CommandBuffer
- `clibridge4unity-async-mainthread` — `AsyncGPUReadback` is async; don't `.Wait()` it on the main thread
