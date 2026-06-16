---
name: clibridge4unity-render-textures
description: Use for any `RenderTexture` work — creation/release lifecycle, `Graphics.Blit`, the temporary RT pool (`RenderTexture.GetTemporary` / `ReleaseTemporary`), format/depth choices, sRGB vs linear color space, `enableRandomWrite` for compute writes, `RenderTexture.active` switching, `ReadPixels` for CPU readback, mip maps. Auto-trigger on `RenderTexture`, `Graphics.Blit`, `RenderTexture.GetTemporary`, `RenderTexture.ReleaseTemporary`, `RenderTexture.active`, `ReadPixels`, `enableRandomWrite`, `RenderTextureFormat.`, `DepthStencilFormat`, `MSAA`, "RT not created", "stretched/upside-down", "pink texture", "Asymmetric MSAA", `CommandBuffer.GetTemporaryRT` vs C# pool. RenderTextures are GPU-resident textures you draw INTO; the lifecycle pitfalls are leaks (forgetting Release) and misuse (sampling while bound as target).
---

# Unity Render Textures

Standard RenderTexture lifecycle, `Graphics.Blit`, the `GetTemporary`/`ReleaseTemporary` pool, formats, sRGB vs linear, MSAA resolve, and `AsyncGPUReadback` are normal Unity knowledge — apply it. Below is only the project-specific surface.

- `cmd.GetTemporaryRT(id, ...)` + `cmd.ReleaseTemporaryRT(id)` inside a `CommandBuffer` is a **third, separate pool** scheduled in the command stream — never mix it with the C# `GetTemporary` pool.

## Verification

```bash
clibridge4unity STATUS                          # baseline state
clibridge4unity SCREENSHOT camera 1024x1024     # raw render — no overlays
clibridge4unity SCREENSHOT gameview             # what the player sees
clibridge4unity LOG errors                      # GPU errors / "RT not created" warnings
```

## Related
- `clibridge4unity-shaders` — material/shader that writes into the RT
- `clibridge4unity-compute-shaders` — `RWTexture2D` needs `enableRandomWrite` + `Create()`
- `clibridge4unity-command-buffers` — `cmd.GetTemporaryRT` is a parallel pool; never mix
- `clibridge4unity-screenshot` — bridge capture of runtime RT contents
