---
name: clibridge4unity-performance
description: Use for any Unity performance concern — frame-time, GC allocations, hot paths, object pooling, `Update`/`LateUpdate`/`FixedUpdate` cost, `Profiler.BeginSample`, `ProfilerMarker`, `MaterialPropertyBlock`, `NonAlloc` overloads, mobile frame budget, allocator choices for `NativeArray`, Burst-friendly code. Auto-trigger on `Profiler.BeginSample`, `ProfilerMarker`, `ObjectPool`, `ListPool`, `Physics.RaycastNonAlloc`, `string.Format` in Update, `GC.Alloc`, "frame spike", "GC Alloc / 0 B", "hitches on mobile", "Update is slow", `[BurstCompile]`, `NativeArray<T>`, `Allocator.TempJob`, `LayerMask.GetMask`. Profile first, optimize second; the patterns below are the ones this team has standardized on.
---

# Unity Performance

Unity's runtime cost lives in three places: the CPU main thread (your `Update`/`LateUpdate`/physics callbacks), the GC (allocations on hot paths trigger collections that stutter the frame), and the GPU (draw calls, overdraw, shader complexity). Editor-time perf is a separate axis (import speed, domain reload time, custom inspector responsiveness). This skill is the patterns and pitfall catalog for hot CPU + GC paths — the most common source of frame hitches.

## The one rule that prevents most of these

**Profile before you optimize, then optimize the path the profiler points at.** Random micro-optimizations on cold code are wasted work. The Profiler window + `Profiler.BeginSample("Label") / Profiler.EndSample()` (or `ProfilerMarker.Auto()`) around suspected hot paths give you the actual cost. Without that data, your "fix" might be making it slower.

## Pitfall catalog

### 1. `LayerMask.GetMask("Ground", "Default")` allocates a `string[]` every call
Looks innocent; allocates a params array per invocation. Called from `Update` on a raycast, that's 60 alloc/sec per call site.
- **Real convention:** cache as a static `int` once. `private static int _groundLayerMask = LayerMask.GetMask("Ground", "Wall");` then `Physics.Raycast(..., _groundLayerMask)`. The comment `// Cached layer mask to avoid GC allocation from LayerMask.GetMask params array` is the canonical reminder.
- **Rule:** every `LayerMask.GetMask` / `Resources.Load` / `Shader.PropertyToID` / `GameObject.Find` in a per-frame hot path is wrong. Hoist to `static readonly` or a one-time init.

### 2. `string.Format`, interpolation, and concatenation allocate every call
A `Debug.Log($"player at {pos}")` in Update is fine for debugging but allocates a `string` per frame. UI labels updating every frame is the same — `text.text = $"Score: {score}"` allocates even when the score hasn't changed.
- **Real convention:** cache the previous value and only format when it changes. `if (_lastScore != score) { _scoreText.text = score.ToString(); _lastScore = score; }`. The comment `// Cached values to avoid GC allocations in Update - only format when value changes` is the team's idiom.
- **Rule:** any string operation in a per-frame path needs justification. Use `ToString(format, CultureInfo.InvariantCulture)` over interpolation when you have one number (`StringBuilder` for many). Avoid LINQ on hot paths — it boxes enumerators.

### 3. `Physics.RaycastAll` / `OverlapSphere` allocate arrays; use the `NonAlloc` variants
`Physics.RaycastAll(...)` returns a `RaycastHit[]` allocated each call. Same for `OverlapSphere`, `OverlapBox`, `SphereCast*`. Their `NonAlloc` counterparts (e.g. `Physics.RaycastNonAlloc(ray, _buffer, maxDist, layerMask)`) write into a pre-allocated buffer and return a hit count.
- **Rule:** never use the allocating variants in code that runs more than once. Allocate a `RaycastHit[16]` (or sized to your worst case) as a field, pass to `NonAlloc`, iterate the returned count. Same for `Physics2D`, `GameObject.GetComponentsInChildren<T>(list)` over the allocating overload.

### 4. `foreach` on `List<T>` doesn't allocate, `foreach` on `Dictionary<>` boxes the enumerator
`List<T>.GetEnumerator()` returns a struct; no alloc. `Dictionary<>.GetEnumerator()` returns a struct, but accessing it through `IEnumerable` boxes it. LINQ chains box every enumerator they touch.
- **Rule:** in hot paths, iterate `Dictionary` with `dict.Keys` / `dict.Values` (struct enumerators) instead of `KeyValuePair`. Avoid LINQ entirely (`Where`, `Select`, `OrderBy`) on per-frame data — write the explicit loop. If you need LINQ semantics, batch into a list outside the hot path.

### 5. Capturing local variables in a lambda allocates a closure
`button.onClick.AddListener(() => DoSomething(localState));` allocates a closure class holding `localState` every time you add the listener. Acceptable once at startup; not in `Update`.
- **Rule:** subscriptions go in `Start`/`OnEnable`/`Awake`, not per-frame. If you must do per-frame work that conceptually uses a delegate, cache the delegate field-level (`private static readonly Action<T> _cached = ...`) or use a method reference (no closure if no captures).

### 6. Object pools are the standard answer for "lots of short-lived things"
Bullets, particles, popup UI, raycast buffers, list reuse — anything you create-and-destroy at frame rates needs pooling. Unity ships `UnityEngine.Pool.ObjectPool<T>` (since 2021), but most projects also ship their own (ODDFramework's `ObjectPool<T>` with a `Disposable` ref struct for scope-based `using` patterns is canonical here).
- **Real convention:** `using (ListPool<T>.GetScoped(out var list)) { list.Add(...); /* use list */ } // auto-released`. The `ref struct Disposable` returned by `GetScoped` makes "forgot to release" a compile error in `using` blocks.
- **Rule:** for any pooled type, hide both `Get` and `Release` behind a scoped wrapper that auto-releases on dispose. Manual `Release(ref T)` calls are bug magnets — the `ref` parameter exists so the pool can null-out the caller's local and catch double-release at runtime.

### 7. `MaterialPropertyBlock` instead of cloning materials
Setting `renderer.material.SetColor(...)` clones the material on first access (instance becomes unique). Hundreds of those = hundreds of materials = batches broken.
- **Rule:** for per-instance shader properties, use `MaterialPropertyBlock`. Reusable instance, no clone, batching preserved. `var mpb = new MaterialPropertyBlock(); mpb.SetColor(_colorId, color); renderer.SetPropertyBlock(mpb);` — and cache the property ID with `Shader.PropertyToID("_Color")` as a static.

### 8. `Update()` is called by Unity for every enabled MonoBehaviour with that method — even empty ones
Unity pays a managed→native overhead per Update call. A scene with 5,000 enabled MonoBehaviours each with `void Update() { }` (or `if (something) return;`) has thousands of unnecessary main-thread crossings.
- **Rule:** if a `MonoBehaviour` doesn't need `Update`, don't define it. If most of the time it has nothing to do, hoist into a central tick manager (one Update on a singleton that iterates active items). Same for `LateUpdate`, `FixedUpdate`, and especially `OnGUI` (which fires multiple times per frame).

### 9. `Profiler.BeginSample` (or `ProfilerMarker.Auto()`) tells you the truth
Guessing where time goes is wrong ~80% of the time. The Profiler window with deep mode on, or named samples wrapping suspected code, gives you actual numbers per frame.
- **Real convention:** `using (_marker.Auto()) { /* hot path */ }` — `ProfilerMarker` is the modern API (zero alloc, can be enabled in release). `Profiler.BeginSample("Label") / EndSample` is the older but still-supported call (used widely in this codebase for editor-time profiling).
- **Rule:** wrap a name around every suspicious hot path *during development*. Strip in shipping if it confuses the profile (`[Conditional("UNITY_EDITOR")]` on a helper). When you can't reproduce a frame spike, profile a representative run + take a screenshot of the Profiler — it's the single best bug-tracker artefact you can produce.

### 10. `NativeArray<T>` + Burst is the answer for compute-heavy loops
Big arithmetic over arrays of structs — physics calculations, particle math, lookup tables — runs 10–100× faster as a Burst-compiled job over `NativeArray<T>` than as managed code. The pattern: `new NativeArray<MyStruct>(size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory)`, populate, schedule a `[BurstCompile] IJobParallelFor`, complete, read back, `Dispose()`.
- **Rule:** any per-frame math over >100 items that the profiler points at is a Burst-job candidate. Allocator choice matters: `Temp` for same-frame, `TempJob` for jobs scheduled this frame, `Persistent` for cross-frame state. Forgetting to `Dispose` a `Persistent` NativeArray is a real leak that the Memory Profiler catches.

## Workflow

1. **Profile.** Open the Profiler window, deep mode (or named markers), and reproduce the symptom. Identify the frame with the spike. Click it. Read the call tree.
2. **Confirm the cost is what you think.** A frame at 33ms because of `Physics.Update` is a different fix than 33ms because of `GC.Collect`. Don't optimize blindly.
3. **Fix the largest single contributor first** — even a 50% reduction on the dominant cost is bigger than 90% on a long tail.
4. **Re-profile after the fix.** Confirm the cost actually moved. If it didn't, revert.
5. **GC alloc audit on hot paths.** In the Profiler, switch to "GC Alloc" column. Any non-zero in `Update`/`FixedUpdate` is a candidate. Apply the standard fixes (cached masks/strings, NonAlloc raycasts, pooled lists).
6. **Mobile budgets.** Mobile frame budget at 60fps is 16.6ms / frame; at 30fps it's 33.3ms — and 1–3ms is reserved for the OS/render thread. If you can't fit your CPU work in 10–14ms (60fps target) or 25–28ms (30fps), redesign, don't micro-optimize.

## Quick reference — scoped list pool

```csharp
using var scope = ListPool<RaycastHit>.GetScoped(out var hits);
int count = Physics.RaycastNonAllocFromList(ray, hits, layerMask); // your wrapper
for (int i = 0; i < count; i++) { /* process hits[i] */ }
// scope's Dispose() returns the list to the pool — even on exception
```

## Quick reference — cached static IDs + masks

```csharp
public class Bullet : MonoBehaviour
{
    private static readonly int _colorId      = Shader.PropertyToID("_Color");
    private static readonly int _groundLayer  = LayerMask.GetMask("Ground", "Wall");
    private static readonly RaycastHit[] _hitBuffer = new RaycastHit[8];

    void Update()
    {
        int n = Physics.RaycastNonAlloc(transform.position, transform.forward, _hitBuffer, 100f, _groundLayer);
        for (int i = 0; i < n; i++) { /* ... */ }
    }
}
```

## Quick reference — Profiler markers

```csharp
private static readonly ProfilerMarker _markerHotPath = new ProfilerMarker("Game.HotPath");

void Update()
{
    using (_markerHotPath.Auto())
    {
        // expensive work here
    }
}
```

## Related
- `clibridge4unity-async-mainthread` — the main-thread is a finite budget; blocking IO consumes it
- `clibridge4unity-shaders-gpu` — GPU costs and texture import settings (the other half of perf)
- `clibridge4unity-addressables` — async loading patterns that don't stutter the main thread
- `clibridge4unity-run-code` — `CODE_EXEC_RETURN` with `--trace` lets you measure individual line costs in editor scripts
