---
name: clibridge4unity-render-textures
description: Use for any `RenderTexture` work — creation/release lifecycle, `Graphics.Blit`, the temporary RT pool (`RenderTexture.GetTemporary` / `ReleaseTemporary`), format/depth choices, sRGB vs linear color space, `enableRandomWrite` for compute writes, `RenderTexture.active` switching, `ReadPixels` for CPU readback, mip maps. Auto-trigger on `RenderTexture`, `Graphics.Blit`, `RenderTexture.GetTemporary`, `RenderTexture.ReleaseTemporary`, `RenderTexture.active`, `ReadPixels`, `enableRandomWrite`, `RenderTextureFormat.`, `DepthStencilFormat`, `MSAA`, "RT not created", "stretched/upside-down", "pink texture", "Asymmetric MSAA", `CommandBuffer.GetTemporaryRT` vs C# pool. RenderTextures are GPU-resident textures you draw INTO; the lifecycle pitfalls are leaks (forgetting Release) and misuse (sampling while bound as target).
---

# Unity Render Textures

A `RenderTexture` is a GPU texture you can render into — the output side of a shader, the input to the next stage, the staging buffer for post-processing. The pitfalls fall into three buckets: **lifetime** (leak via missed `Release` / `ReleaseTemporary`), **format choice** (wrong precision, missing depth, no readback support), and **state ordering** (binding it as both a target and a sample source in the same pass = undefined behaviour).

## Two pools, two policies — pick correctly

- **`RenderTexture.GetTemporary(w,h,depth,fmt,readWrite,antiAliasing)`** → release with `RenderTexture.ReleaseTemporary(rt)`. Pooled, cheap, ideal for transient per-frame RTs (post-process intermediates, screen-space effects). Released RTs return to the pool; next request with matching params reuses.
- **`new RenderTexture(w,h,depth,fmt) { name = "..." }`** + `rt.Create()` → release with `rt.Release()` and `Destroy(rt)` if you instantiated it. Long-lived (a paint mask that survives multiple frames, a UI render target). YOU own the lifetime.
- **`cmd.GetTemporaryRT(id, ...)`** + `cmd.ReleaseTemporaryRT(id)` (inside a `CommandBuffer`) → a third pool, scheduled as part of the command stream. Same paired-call rules; never mix with the C# pool.

## Authoring

```csharp
// Long-lived RT, owned by this MonoBehaviour
public class PaintMaskTarget : MonoBehaviour
{
    private RenderTexture _rt;

    private void OnEnable()
    {
        _rt = new RenderTexture(1024, 1024, depth: 0, RenderTextureFormat.ARGB32)
        {
            name = "PaintMask",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            enableRandomWrite = true,    // required for RWTexture2D writes from compute
        };
        _rt.Create();                    // <-- realises the GPU resource; without this, writes silently fail
    }

    private void OnDisable()
    {
        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }
    }
}

// Transient RT for a one-shot post-process Blit
public class PostProcessPass
{
    public static void RunOnce(Material mat, RenderTexture src, RenderTexture dst)
    {
        var tmp = RenderTexture.GetTemporary(src.descriptor);  // matches all params of src
        try
        {
            Graphics.Blit(src, tmp, mat, 0);
            Graphics.Blit(tmp, dst);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(tmp);                // ALWAYS in finally
        }
    }
}
```

## Pitfall catalog

1. **Forgetting `Create()` on a long-lived RT.** A `new RenderTexture(...)` is configured but not allocated on the GPU. Binding it as a target succeeds; writes go to the void; sampling returns black. Easy to miss because the C# field is non-null.
   → Always `rt.Create()` after construction. `RenderTexture.GetTemporary` does this for you; the constructor doesn't. If using `enableRandomWrite = true`, set it BEFORE `Create()` — toggling after is ignored.
2. **Forgetting `ReleaseTemporary` leaks (mostly silent).** The temp pool grows; the editor stays usable until "Allocator: 4096 MB" appears in the Memory Profiler. On device: VRAM bloat → OOM crash on the next scene transition.
   → Every `RenderTexture.GetTemporary` has a matching `ReleaseTemporary` in a `try/finally`. No exceptions. Treat it like `new ComputeBuffer`.
3. **Sampling an RT while it's the active render target = undefined behaviour.** Some GPUs return black, some return last-frame data, some flicker. Common when a post-process Blit goes A → A (same RT as source AND destination).
   → Always Blit A → B with `B != A`. The temp pool makes this cheap: `var tmp = GetTemporary(...); Blit(A, tmp, mat); Blit(tmp, A); ReleaseTemporary(tmp);`. Some drivers tolerate aliasing on the same RT for trivial copies, but the rule is "don't depend on it."
4. **Forgetting to restore `RenderTexture.active`.** Setting `RenderTexture.active = myRT;` for `ReadPixels` (or any direct GL command) leaves the global state changed. Subsequent code that assumes the default target sees nothing.
   → Save and restore: `var prev = RenderTexture.active; try { RenderTexture.active = myRT; ... } finally { RenderTexture.active = prev; }`. Same pattern as save/restore `GUI.color` in IMGUI.
5. **Depth buffer mismatches between source and destination.** A 0-depth RT can't be used as the depth target for a 24-depth render. Compositing a transparent pass into a depth-less RT loses depth-sorted draws.
   → Match `depthBufferBits` (0 / 16 / 24 / 32). `RenderTextureFormat.Default` is sRGB ARGB32 with 0 depth; specify explicitly when depth matters. For full G-buffer-style compositing, copy depth from the camera's depth target separately.
6. **sRGB vs linear color space corruption.** Unity ships in Linear color space by default. An RT created with `readWrite = RenderTextureReadWrite.sRGB` does the sRGB ↔ linear conversion on read/write; `RenderTextureReadWrite.Linear` doesn't. Mismatch → the same pixel comes out 2× brighter or 2× darker.
   → For color art (sprites, UI), use `sRGB`. For data (masks, normals, depth), use `Linear`. Setting `Default` defers to the project's color space, which is what you usually want.
7. **MSAA on an RT requires `antiAliasing = 2/4/8` AND a single `Graphics.Blit` resolve before sampling.** Most drivers won't sample directly from an MSAA RT; you need to resolve into a 1× sample-count RT first.
   → If you don't need MSAA, leave `antiAliasing = 1` (the default). If you do, allocate a 1× resolve target and `Graphics.Blit(msaa, resolved)` before reading.
8. **`ReadPixels` to a `Texture2D` requires `RenderTexture.active` set to the source AND a `Texture2D` of the matching size.** Caller blocks the main thread while the GPU finishes; can be 50–200 ms on mobile.
   → Use `AsyncGPUReadback.Request(rt)` for non-blocking readback (returns a `NativeArray<T>` 1–3 frames later). Direct `ReadPixels` only in editor scripts where a frame stall doesn't matter.
9. **Resizing an RT requires Release before changing dimensions.** `rt.width = 2048; rt.height = 2048;` on a created RT throws or silently does nothing.
   → `if (_rt != null) { _rt.Release(); Destroy(_rt); } _rt = new RenderTexture(...) { ... }; _rt.Create();`. The pool variant just calls `GetTemporary` with new dimensions — old ones get returned to the pool when you next `Release`.
10. **The pink "missing shader" texture is often a missing RT bind.** Symptom looks identical to a missing shader, but the cause is the camera's target was set to an unCreated RT (#1), or an RT in a wrong format that the consumer doesn't expect.
    → Check the camera's Target Texture field. Inspect the RT in the Project window — if `Width: 0, Height: 0` or "Not created," that's the bug. The Frame Debugger shows the bound target; verify it's the expected RT and dimensions.

## Format cheat sheet

| Format | Bits/channel | Best for |
|---|---|---|
| `ARGB32` (`RGBA8`) | 8 | Default color, UI compositing |
| `RHalf`/`RGHalf`/`RGBAHalf` | 16 float | HDR, masks needing precision |
| `RFloat`/`RGFloat`/`RGBAFloat` | 32 float | Precise data buffers, compute outputs |
| `RInt`/`RGInt`/`RGBAInt` | 32 int | Integer IDs, atomic accumulators |
| `R8`/`RG16` | 8/16 | Single-channel masks (alpha-only) |
| `Depth` | 16/24/32 | Depth-only targets |
| `Shadowmap` | 16/24 | Shadow map targets |

`SystemInfo.SupportsRenderTextureFormat(fmt)` is a runtime check — for any non-default format used on multiple platforms, validate support at startup and fall back gracefully.

## Verification

```bash
clibridge4unity STATUS                          # baseline state
clibridge4unity SCREENSHOT camera 1024x1024     # raw render — no overlays
clibridge4unity SCREENSHOT gameview             # what the player sees
clibridge4unity LOG errors                      # GPU errors / "RT not created" warnings
```

For per-step inspection, open the Frame Debugger (Window → Analysis → Frame Debugger) and click through each draw — Unity shows the bound target dimensions/format right there, faster than logging from C#.

## Related
- `clibridge4unity-shaders` — material setup for the shader that writes into the RT
- `clibridge4unity-compute-shaders` — `RWTexture2D` writes require `enableRandomWrite = true` + `Create()`
- `clibridge4unity-command-buffers` — `cmd.GetTemporaryRT`/`ReleaseTemporaryRT` is a parallel pool to the C# one; never mix
- `clibridge4unity-screenshot` — bridge `SCREENSHOT camera/gameview` captures runtime RT contents
