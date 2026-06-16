---
name: clibridge4unity-command-buffers
description: Write, edit, or debug `CommandBuffer` work — inserting custom rendering at specific render-pipeline points (Built-in RP `CameraEvent`/`LightEvent`, URP `ScriptableRenderPass`), Blit composites, paint/decal accumulators, post-process chains, Frame Debugger workflow, RT target switching. Auto-trigger on `CommandBuffer`, `cmd.Blit`, `cmd.SetRenderTarget`, `cmd.DrawMesh`, `cmd.DispatchCompute`, `cmd.ClearRenderTarget`, `Camera.AddCommandBuffer`, `Light.AddCommandBuffer`, `CameraEvent.`, `LightEvent.`, `ProfilingScope`, "shows up in Frame Debugger", "command buffer accumulates", "didn't render", "Built-in vs URP custom pass", `ScriptableRenderPass`, `RenderGraph`. CommandBuffer is the imperative recording API for GPU work; pitfalls are mostly about WHEN you attach it and WHEN you tear it down.
---

# Unity Command Buffers

Standard CommandBuffer API, pipeline lifecycle, ping-pong, Release discipline, and the Frame Debugger are general Unity knowledge — apply them normally. Only the project-specific facts below are non-obvious.

## House facts

- **This project runs URP 17.3 on Unity 6 (6000.3), where RenderGraph is the default.** Author new passes against `RecordRenderGraph(RenderGraph, ContextContainer)`. The `Execute(ScriptableRenderContext, ref RenderingData)` signature and `RenderingData` are `[Obsolete]` and only run with **Compatibility Mode (Render Graph Disabled)** enabled in URP Global Settings — keep that path only as the fallback.
- Pipeline check when unsure: `GraphicsSettings.currentRenderPipeline` null → Built-in, otherwise URP/HDRP.
- A `ScriptableRendererFeature` must be added to the active Renderer asset (`Assets/Settings/URP-*-Renderer.asset` → Renderer Features → Add) or `Create()` runs but the pass never executes. If the project has multiple Renderers (mobile/desktop), add it to **each**.
- Frame Debugger naming convention: name the sample/pass the same as the C# class for findability.

## Verification

```bash
clibridge4unity STATUS                  # no script errors blocking the pass from loading
clibridge4unity LOG errors              # any runtime exceptions in the pass
clibridge4unity SCREENSHOT gameview     # camera result + runtime UI Toolkit overlay; catches "composite fine but UI covers it"
clibridge4unity PROFILE hierarchy       # GPU/CPU cost of the pass — a composite can be correct but too expensive
# Open Frame Debugger manually in Unity to inspect step-by-step.
```

## Related
- `clibridge4unity-shaders` — material/uniform discipline
- `clibridge4unity-compute-shaders` — `cmd.DispatchCompute` + `cmd.SetCompute*Param`
- `clibridge4unity-render-textures` — `cmd.GetTemporaryRT` vs the C# pool
- `clibridge4unity-screenshot` — bridge `SCREENSHOT` for render results
