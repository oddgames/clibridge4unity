---
name: clibridge4unity-command-buffers
description: Write, edit, or debug `CommandBuffer` work — inserting custom rendering at specific render-pipeline points (Built-in RP `CameraEvent`/`LightEvent`, URP `ScriptableRenderPass`), Blit composites, paint/decal accumulators, post-process chains, Frame Debugger workflow, RT target switching. Auto-trigger on `CommandBuffer`, `cmd.Blit`, `cmd.SetRenderTarget`, `cmd.DrawMesh`, `cmd.DispatchCompute`, `cmd.ClearRenderTarget`, `Camera.AddCommandBuffer`, `Light.AddCommandBuffer`, `CameraEvent.`, `LightEvent.`, `ProfilingScope`, "shows up in Frame Debugger", "command buffer accumulates", "didn't render", "Built-in vs URP custom pass", `ScriptableRenderPass`, `RenderGraph`. CommandBuffer is the imperative recording API for GPU work; pitfalls are mostly about WHEN you attach it and WHEN you tear it down.
---

# Unity Command Buffers

A `CommandBuffer` is a list of GPU commands you record once and execute many times — Blits, DrawMesh, DispatchCompute, SetRenderTarget, ClearRenderTarget. Attach it at a specific render-pipeline event (`CameraEvent.AfterForwardOpaque`, `LightEvent.AfterShadowMap`, …) and Unity inserts your commands at that point each frame. The Frame Debugger shows you exactly where they ran and what they touched — that's the primary debugging tool.

## Built-in RP vs URP — the architecture matters

| | Built-in RP | URP |
|---|---|---|
| Attach API | `camera.AddCommandBuffer(CameraEvent, buffer)` | `ScriptableRenderPass.Execute` (in a `ScriptableRendererFeature`) |
| Detach | `camera.RemoveCommandBuffer(CameraEvent, buffer)` | Built into the pass lifecycle |
| Insertion points | `CameraEvent.*`, `LightEvent.*` | `RenderPassEvent.*` |
| Frame Debugger naming | `cmd.BeginSample("name")` / `EndSample` | `new ProfilingScope(cmd, profilingSampler)` |
| Rebuild per frame | Once on Awake/OnEnable is fine | Pass `Execute` runs per frame; build commands fresh each call |

Same `CommandBuffer` API, completely different lifecycle. **Built-in patterns transplanted directly into URP simply don't fire** — URP ignores `Camera.AddCommandBuffer`. If you don't know which pipeline the project is on, check `GraphicsSettings.currentRenderPipeline`: null → Built-in, otherwise URP/HDRP.

## Authoring (Built-in RP)

```csharp
public class PaintMaskCompositor : MonoBehaviour
{
    [SerializeField] private Material _compositeMaterial;
    private CommandBuffer _cmd;

    private void OnEnable()
    {
        _cmd = new CommandBuffer { name = "PaintMask Composite" };

        // Take ownership of the source / dest IDs once — `Shader.PropertyToID` is cheap.
        int tmpRT = Shader.PropertyToID("_PaintMaskTmp");
        _cmd.GetTemporaryRT(tmpRT, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.Default);
        _cmd.Blit(BuiltinRenderTextureType.CameraTarget, tmpRT, _compositeMaterial, 0);
        _cmd.Blit(tmpRT, BuiltinRenderTextureType.CameraTarget);
        _cmd.ReleaseTemporaryRT(tmpRT);

        // Insert AFTER opaque geometry but BEFORE skybox/transparents.
        Camera.main.AddCommandBuffer(CameraEvent.AfterForwardOpaque, _cmd);
    }

    private void OnDisable()
    {
        if (_cmd != null && Camera.main != null)
            Camera.main.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, _cmd);
        _cmd?.Release();   // releases native resources held by the buffer
        _cmd = null;
    }
}
```

## Authoring (URP)

```csharp
public class PaintMaskFeature : ScriptableRendererFeature
{
    public Material compositeMaterial;
    public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingOpaques;

    private PaintMaskPass _pass;

    public override void Create()
    {
        _pass = new PaintMaskPass(compositeMaterial) { renderPassEvent = injectionPoint };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
    {
        if (compositeMaterial == null) return;
        renderer.EnqueuePass(_pass);
    }

    private class PaintMaskPass : ScriptableRenderPass
    {
        private readonly Material _mat;
        private readonly ProfilingSampler _sampler = new("PaintMask Composite");

        public PaintMaskPass(Material mat) { _mat = mat; }

        public override void Execute(ScriptableRenderContext ctx, ref RenderingData data)
        {
            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, _sampler))
            {
                // record commands here — they show as a labeled group in Frame Debugger
                cmd.Blit(data.cameraData.renderer.cameraColorTargetHandle, data.cameraData.renderer.cameraColorTargetHandle, _mat, 0);
            }
            ctx.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
```

## Pitfall catalog

1. **Built-in `AddCommandBuffer` accumulates every call — never put it in `Update`.** `camera.AddCommandBuffer(event, buffer)` doesn't replace; it appends. Calling it once per frame leaks command buffers; one minute later you've inserted 3600 copies and the GPU is melting.
   → Add **once** in `OnEnable`, remove in `OnDisable`. If the buffer's contents need to change per frame, mutate the contents (clear and re-record commands inside the existing buffer); don't add a new buffer.
2. **`CommandBuffer.Release()` is required to free native memory.** Not the same as letting it GC. Forgotten releases = persistent native allocations that the Unity Profiler shows as `CommandBuffer (native)`.
   → Treat `CommandBuffer` like `IDisposable`. Cache the field, `Release()` in `OnDisable`/`OnDestroy`. If the buffer's lifetime matches a `using` scope, `using var cmd = new CommandBuffer { name = "..." };` is fine and disposes automatically.
3. **`cmd.GetTemporaryRT` and `cmd.ReleaseTemporaryRT` must be paired *within the same buffer*.** Releasing a temporary RT from C# (`RenderTexture.ReleaseTemporary(rt)`) after recording it via CommandBuffer is a category error — they manage different pools.
   → Use the C# API or the cmd API consistently; never mix. The cmd version means "release it as part of the recorded command stream," which only fires when the buffer executes.
4. **`cmd.SetRenderTarget` doesn't clear unless you ask.** A fresh target may have whatever was in memory; reading from it without explicit clear gives ghost frames.
   → Always `cmd.ClearRenderTarget(clearDepth: true, clearColor: true, Color.clear)` immediately after `SetRenderTarget` for any non-Blit pass. For Blits, the destination is fully written so a clear is wasted work.
5. **Frame Debugger names come from `cmd.BeginSample`/`EndSample` (Built-in) or `ProfilingScope` (URP).** Without them, every step shows as a meaningless `Draw Mesh`/`Blit`. With them, you can find your pass instantly.
   → Every CommandBuffer has at least one named sample wrapping its work. Use the same name as the C# class for findability. `cmd.name = "..."` sets the overall buffer label; `BeginSample` sets per-step labels.
6. **`Camera.main` in `OnEnable` is null when there's no MainCamera in the scene yet.** Common in additive scene loading: the additive scene's MonoBehaviour wakes up before the main scene's camera is registered.
   → Don't assume `Camera.main` exists. Either configure the target camera as a `[SerializeField]`, or subscribe to `Camera.onPreRender`/`SceneManager.sceneLoaded` and attach later. Defensive: `if (Camera.main == null) return;`.
7. **`BuiltinRenderTextureType.CameraTarget` in URP is wrong.** URP doesn't expose `BuiltinRenderTextureType` the same way — use `RenderingData.cameraData.renderer.cameraColorTargetHandle`. Mixing them silently no-ops or renders to the wrong target.
8. **`cmd.DispatchCompute` inside a CommandBuffer behaves slightly differently from a direct `cs.Dispatch`.** Bindings set on the ComputeShader via C# `cs.SetBuffer(...)` are NOT included in the recorded buffer — you must `cmd.SetComputeBufferParam(cs, kernel, "_X", buffer)` so the binding is part of the recorded stream. Recording `cs.Dispatch(kernel, x, y, z)` doesn't capture the bindings.
   → For compute work inside a CommandBuffer, ALL bindings use `cmd.SetCompute*Param(cs, kernel, ...)` and then `cmd.DispatchCompute(cs, kernel, x, y, z)`. Don't mix direct cs binding + cmd dispatch.
9. **`cmd.DrawMesh` requires a properly-set-up material with the right blend modes; defaults to overdraw mistakes.** Drawing a mesh into the camera target with `Blend SrcAlpha OneMinusSrcAlpha` is fine for transparency; without those tags, you replace pixels and lose depth.
   → Author the material correctly first (verify in a regular scene render). Only then record it into a CommandBuffer.
10. **URP `ScriptableRendererFeature` requires the feature added to the active Renderer asset** — otherwise `Create()` runs but the pass never executes. Common confusion: the feature compiles and "exists," but the Frame Debugger shows no trace of it.
    → Open the URP Renderer asset (`Assets/Settings/URP-*-Renderer.asset`), Renderer Features section, click Add → your feature. If the project has multiple Renderers (mobile/desktop), add the feature to each.

## Frame Debugger workflow

The Frame Debugger (Window → Analysis → Frame Debugger) is the primary tool. Click **Enable** to capture; the tree shows every draw/Blit/Dispatch in order, click each step to see source/dest, bound resources, and the resulting framebuffer.

- **Find your pass:** look for the named sample (`cmd.BeginSample("PaintMask Composite")` shows as a labeled group).
- **Verify the inputs:** click "Show Render Target" on the bound texture slot — confirms which RT is actually feeding the shader.
- **Catch wrong-target writes:** the breadcrumb shows the active render target; if it isn't what you expected, your `SetRenderTarget` is wrong or missing.
- **Spot accumulating buffers:** if you see the same pass listed N times in a single frame, you're calling `AddCommandBuffer` somewhere it shouldn't be.

## Verification

```bash
clibridge4unity STATUS                  # no script errors blocking the pass from loading
clibridge4unity LOG errors              # any runtime exceptions in the pass
clibridge4unity SCREENSHOT gameview     # visual confirmation the composite ran
# Open Frame Debugger manually in Unity to inspect step-by-step.
```

`SCREENSHOT gameview` captures the *runtime UI Toolkit* + camera result — useful for catching "the composite is fine but the UI overlay covers it" kinds of mistakes.

## Related
- `clibridge4unity-shaders` — material/uniform discipline; the material a CommandBuffer Blits with must be authored correctly first
- `clibridge4unity-compute-shaders` — `cmd.DispatchCompute` + `cmd.SetCompute*Param` for compute work in a render pass
- `clibridge4unity-render-textures` — `cmd.GetTemporaryRT`/`cmd.ReleaseTemporaryRT` vs the C# pool
- `clibridge4unity-screenshot` — bridge `SCREENSHOT` for capturing render results from CLI
