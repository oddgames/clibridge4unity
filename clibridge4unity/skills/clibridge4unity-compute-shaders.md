---
name: clibridge4unity-compute-shaders
description: Write, edit, or debug Unity Compute Shaders — `.compute` files, kernels, `RWStructuredBuffer`/`RWTexture2D`, `ComputeBuffer` lifetime, `Dispatch` group counts, mobile/gles3/Metal/Vulkan binding limits, `AsyncGPUReadback` for CPU sync, `groupshared` caps. Auto-trigger on `.compute`, `#pragma kernel`, `[numthreads]`, `ComputeBuffer`, `ComputeShader.Dispatch`, `RWStructuredBuffer`, `StructuredBuffer<T>`, `RWTexture2D`, `AsyncGPUReadback`, `groupshared`, "buffer count exceeding hard limit", "Compute Shader not supported", "kernel not found", `SystemInfo.supportsComputeShaders`, `SetBuffer`/`SetTexture` on a ComputeShader. Compute shaders are the workhorse for GPU compute (particles, image processing, mask compositing, paint-decal accumulation) — mobile-binding limits and resource lifetime are real ship-breakers.
---

# Unity Compute Shaders

Standard Unity compute-shader rules (numthreads = group size, Dispatch group counts + CeilToInt + in-kernel bounds checks, mobile 128-thread / 32KB-groupshared ceilings, stride matching, `enableRandomWrite` before `Create`, `.Release()` lifetime, `AsyncGPUReadback` over `GetData`, GLES 3.1+ requirement, per-kernel binding) apply as normal — assume general knowledge. This project hits these on mask compositing / paint-decal accumulation, where a kernel that runs in the desktop editor exceeds GLES 3.1/Metal device limits (`Shader error … on gles3` never seen in editor). Verify with the CLI below.

## Verification

```bash
clibridge4unity STATUS                    # check no script errors
clibridge4unity LOG errors                # shader/compute errors from Unity Console after a dispatch
clibridge4unity CODE_EXEC_RETURN "@%TEMP%\dispatch-test.cs"  # @<path> must point at an existing .cs file (any extension works)
```

For visual verification, `CODE_EXEC_RETURN` a snippet (inline, or `@<path>` to an existing `.cs` file) that dispatches the kernel, reads back the first cell via `AsyncGPUReadback`, and returns the value. Compare expected vs actual cheaply.

## Related
- `clibridge4unity-shaders` — surface shaders, material/texture wiring
- `clibridge4unity-render-textures` — `RWTexture2D` targets need `enableRandomWrite`
- `clibridge4unity-command-buffers` — dispatch compute within a CommandBuffer
- `clibridge4unity-async-mainthread` — don't `.Wait()` `AsyncGPUReadback`
