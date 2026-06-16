---
name: clibridge4unity-performance
description: Use for any Unity performance concern — frame-time, GC allocations, hot paths, object pooling, `Update`/`LateUpdate`/`FixedUpdate` cost, `Profiler.BeginSample`, `ProfilerMarker`, `MaterialPropertyBlock`, `NonAlloc` overloads, mobile frame budget, allocator choices for `NativeArray`, Burst-friendly code. Auto-trigger on `Profiler.BeginSample`, `ProfilerMarker`, `ObjectPool`, `ListPool`, `Physics.RaycastNonAlloc`, `string.Format` in Update, `GC.Alloc`, "frame spike", "GC Alloc / 0 B", "hitches on mobile", "Update is slow", `[BurstCompile]`, `NativeArray<T>`, `Allocator.TempJob`, `LayerMask.GetMask`. Profile first, optimize second; the patterns below are the ones this team has standardized on.
---

# Unity Performance

Standard Unity/C# hot-path and GC rules (cache lookups out of `Update`; avoid `LayerMask.GetMask`/`string.Format`/interpolation/LINQ/closures/`RaycastAll` per-frame; use `NonAlloc` overloads, struct enumerators, `MaterialPropertyBlock`, central tick managers, `NativeArray`+Burst with correct allocators, object pools) are assumed knowledge — apply them. Below are only this project's conventions and CLI tooling.

## House conventions

- **Pooling:** ODDFramework's `ObjectPool<T>` (with a `Disposable` ref struct for scope-based `using`) is the canonical pool here, not just `UnityEngine.Pool.ObjectPool<T>`. Hide `Get`+`Release` behind a scoped wrapper: `using (ListPool<T>.GetScoped(out var list)) { ... } // auto-released`. The `ref struct Disposable` makes "forgot to release" a compile error in `using` blocks; manual `Release(ref T)` takes the local by `ref` so the pool can null it and catch double-release.
- **Canonical reminder comments** (the team's idioms — reuse them verbatim):
  - `// Cached layer mask to avoid GC allocation from LayerMask.GetMask params array`
  - `// Cached values to avoid GC allocations in Update - only format when value changes`
- `Profiler.BeginSample("Label") / EndSample` is used widely in this codebase for editor-time profiling (alongside `ProfilerMarker`).

```csharp
using var scope = ListPool<RaycastHit>.GetScoped(out var hits);
int count = Physics.RaycastNonAllocFromList(ray, hits, layerMask); // your wrapper
for (int i = 0; i < count; i++) { /* process hits[i] */ }
```

## Driving the profiler from the CLI

`PROFILE` remotely drives Unity's `ProfilerDriver` and returns frame-hierarchy data over the pipe (main thread). The first word of the argument selects the action; default is `status`.

- `PROFILE` / `PROFILE status` — report `enabled` plus captured frame range (`firstFrame`/`lastFrame`).
- `PROFILE enable` — start profiling (`ProfilerDriver.enabled` + `Profiler.enabled` true).
- `PROFILE disable` — stop profiling.
- `PROFILE clear` — discard all captured frames (`ProfilerDriver.ClearAllFrames()`).
- `PROFILE hierarchy` — dump last frame's call tree (Name / Total / Self / Calls), sorted by total time, merged samples with the same name.

Filters on `hierarchy` (space-separated `key:value`, any combination):
- `min:<ms>` — drop items below this total-time threshold (e.g. `min:1.0`).
- `depth:<n>` — limit tree depth printed (default `3`).
- `frame:<n>` — pick a specific captured frame instead of the last (errors with valid range if out of bounds).
- `thread:<n>` — pick a thread index (default `0`, main thread).

Typical loop: `PROFILE clear` → reproduce the symptom → `PROFILE hierarchy min:1.0 depth:2` for dominant costs, then drill with a higher `depth:` on the offending branch.

## Related
- `clibridge4unity-async-mainthread` — main-thread budget; blocking IO
- `clibridge4unity-shaders` — GPU costs, texture import
- `clibridge4unity-addressables` — async loading without stutter
- `clibridge4unity-run-code` — `CODE_EXEC_RETURN --trace` for per-line cost
