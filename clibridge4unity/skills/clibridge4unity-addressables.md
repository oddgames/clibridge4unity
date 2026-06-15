---
name: clibridge4unity-addressables
description: Use for any Addressables work — loading, releasing, reference counting, AsyncOperationHandle lifetime, InstantiateAsync vs LoadAssetAsync, AssetReference vs raw address string, build catalogs, label vs key vs reference resolution, "asset not found at runtime but works in editor", leak hunting. Auto-trigger on `Addressables.`, `AsyncOperationHandle`, `LoadAssetAsync`, `LoadAssetsAsync`, `InstantiateAsync`, `Addressables.Release`, `Addressables.ReleaseInstance`, `AssetReference`, `IResourceLocation`, `AddressableAssetSettings`, "addressables InvalidKeyException", "still loaded after release", "double release", "WaitForCompletion freezes", "reentering Update". Addressables make async loading look easy and lifecycle accounting hard; the patterns below stop the leaks.
---

# Unity Addressables

Addressables is Unity's standard system for async asset loading with content catalogs, labels, and content updates. The hard part is not loading — it's lifetime. Every `LoadAssetAsync` returns a handle whose ref-count you must explicitly release; forgetting leaks the bundle, releasing twice corrupts the bookkeeping. The handle is a *value* (no destructors), so the GC won't save you. This skill is the ownership patterns and the Addressables-package landmines.

## The one rule that prevents most of these

**Every successful `LoadAssetAsync` / `LoadAssetsAsync` / `InstantiateAsync` has exactly one matching `Release` / `ReleaseInstance`.** Not zero, not two. The handle's ref-count is the bookkeeping; the package will not warn you if you skip the release — it'll just keep the bundle loaded forever. Track every load at the boundary where it leaves your method.

## Pitfall catalog

### 1. `Addressables.Release(handle)` on an invalid handle throws — wrap with an `IsValid` guard
A handle that failed to load (bad key, missing bundle) is `!IsValid()`. Calling `Release` on it throws. Double-releases also throw (the handle is invalid after the first release).
- **Real convention:** every project ships a `SafeRelease(AsyncOperationHandle h)` helper that `if (h.IsValid()) Addressables.Release(h);`. Treat the raw `Release` as unsafe and route every call through the helper.
- **Rule:** never call `Addressables.Release` directly. Use a `SafeRelease` wrapper and make it the only path. Same for `Addressables.ReleaseInstance(go)` — guard the instance is non-null and still associated with Addressables.

### 2. `LoadAssetAsync` twice on the same `AssetReference` errors — the reference holds the prior handle
`AssetReference.LoadAssetAsync<T>()` caches the handle inside the reference. Calling it again without releasing the first throws "Already loaded — release the previous handle first." This is the most-reported Addressables issue (Unity forum thread #959910).
- **Rule:** treat `AssetReference` as a *singleton handle*. Load once, store the handle on the reference (it does that for you), release explicitly when you're done. If you need to load the same asset twice independently, load via the string address (`Addressables.LoadAssetAsync<T>("MyAddress")`) — that returns separate handles, separately ref-counted.

### 3. `InstantiateAsync` and `LoadAssetAsync` have different release semantics
`Addressables.LoadAssetAsync<T>(key)` → released with `Addressables.Release(handle)`. `Addressables.InstantiateAsync(key)` → released with `Addressables.ReleaseInstance(go)` (which destroys the GameObject AND decrements the ref-count on the prefab bundle). Calling `Object.Destroy(go)` on an Addressables-instantiated object DOES destroy it, but leaks the ref-count.
- **Rule:** track which path made the object. A standard pattern is to tag instantiated objects with an `InstanceBinding` component holding the `AsyncOperationHandle<GameObject>` so the cleanup code can `ReleaseInstance` correctly. Never `Destroy` an Addressables instance directly — always `ReleaseInstance` (with a fallback to `Destroy` for non-Addressables instances).

### 4. Refcounted owners aren't built in — build your own
The Addressables package counts loads/releases per-asset internally but exposes only the raw `Release`. Game code that wants "load asset for the lifetime of a screen, multiple screens share" needs an explicit owner abstraction.
- **Real convention:** an `IResourceOwner` / `AddressableResourceHandle` pattern: each owner holds an `IDisposable` per resource it claims; `Claim(key)` increments, `Release(key)` decrements + only frees when count hits zero. Refcount tracked in the handle wrapper, not just the package.
- **Rule:** any object that loads addressables on behalf of others (a UI screen, a level loader, an asset preloader) wraps loads in a refcount-aware handle, and disposes the handle on its own destruction. The handle's `Dispose()` does the SafeRelease.

### 5. `WaitForCompletion()` inside an Addressables continuation freezes the editor (and device)
Calling `handle.WaitForCompletion()` inside an `AsyncOperationHandle.Completed` callback (or any caller of `ResourceManager.Update`) re-enters the package's update loop and deadlocks. Common when "I just need to load this small thing synchronously after the other one finishes."
- **Real comment from production code:** *"Reentering the Update method is not allowed"* — wrapping a sync call from inside an async continuation hangs the main thread on the editor and crashes on mobile.
- **Rule:** if you're inside a callback / `await` continuation already, the next thing you need is to start another async load, not block. `WaitForCompletion` is for top-level sync code (game init, editor scripts) — never for "just this once" inside an async chain.

### 6. Loading by label and by address use different handle bookkeeping
`Addressables.LoadAssetsAsync<T>("ui-label", null)` loads every asset with that label. The returned handle's `Result` is `IList<T>`. Releasing the *combined* handle releases all loaded assets. But `Addressables.LoadAssetAsync<T>("specific-address")` for one of those addresses gives you a separate handle even though the asset is already loaded — and reports its async as "Done" immediately. Trying to dedupe them confuses the ref-count.
- **Real comment:** *"loading by label will not make loading by address be reported as done immediately"* — the package's internal bookkeeping treats them as separate consumers.
- **Rule:** within a system, pick one access mode (label OR address) and stick with it. Mixing means one path "owns" the data and the other gets stale-handle errors. If you need both, load by label and dispatch by address from the label result.

### 7. `Addressables.LoadAssetAsync<GameObject>` returns the PREFAB, not an instance
The handle's `Result` is the prefab asset. `Instantiate(result)` works but creates an instance NOT tracked by Addressables — releasing the load handle still frees the bundle, but the instance lives independently.
- **Rule:** use `Addressables.InstantiateAsync` if you want an Addressables-managed instance. Use `LoadAssetAsync` only if you want to repeatedly `Object.Instantiate` from the same loaded prefab (a "pool of bullets from one address" pattern). Mixing is fine if you keep clear ownership.

### 8. `AsyncOperationHandle` is a struct — be careful about copying
The handle wraps an internal index. Copies of the same handle are equivalent. But once any copy is released, ALL copies become invalid (`IsValid() == false`). Trying to use a stale copy after release throws.
- **Rule:** hold one canonical reference to each handle. Don't pass `AsyncOperationHandle` by value across long-lived ownership boundaries — wrap it in an `IDisposable` so the owner is explicit.

### 9. Synchronous mode (`Addressables.LoadAssetAsync(...).WaitForCompletion()`) at startup is a frame stall
Common during initial game load: load 20 assets sync to skip the async dance. Each `WaitForCompletion` blocks the main thread until that bundle finishes downloading/loading. On slow disk / mobile / cold cache, the game appears frozen for seconds.
- **Rule:** in production, never `WaitForCompletion`. Use `await` (with UniTask or Task-bridged Addressables) and show a real loading UI. Only the editor-test path and one-off init scripts should call `WaitForCompletion`.

### 10. The build catalog doesn't update unless you rebuild — "works in editor, not in player" is usually this
Addressables in the editor read from your `AddressableAssetSettings` live. The player reads from the *baked catalog* on disk. Adding a new address and not running "Build Player Content" leaves the player with a stale catalog → `InvalidKeyException` at runtime.
- **Rule:** any address change requires a fresh `Window > Asset Management > Addressables > Groups > Build > New Build > Default Build Script`. Wire this into your CI / build pipeline. The "AddressablesValidator"-style preprocess step in the bridge codebase fails the build if expected addresses are missing — copy that pattern into your own pipeline.

## Workflow

1. **At every `LoadAssetAsync` / `InstantiateAsync` call site, write the matching `SafeRelease` / `ReleaseInstance` immediately** (in `OnDestroy`, in a `finally`, in a `IDisposable.Dispose`). Don't defer.
2. **Wrap shared loads in a refcounted owner.** Either Unity's `Addressables.LoadAssetAsync` per-system, or a project-local `IResourceOwner` that handles claim/release counts.
3. **Pick one access mode per system** — label or address — to avoid the bookkeeping divergence.
4. **Never `WaitForCompletion` inside a continuation.** It deadlocks. Make the caller async too.
5. **Rebuild the catalog before any player test that touches a new address.** Wire this into CI.
6. **For Addressables debugging from the bridge:** `INSPECTOR Assets/Path/To/Asset.prefab` works as a sanity check, but the *real* tool is `CODE_EXEC_RETURN` with `Addressables.ResourceLocators.SelectMany(l => l.Keys)` to dump the live catalog.

## Quick reference — SafeRelease + handle wrapper

```csharp
public static void SafeRelease(AsyncOperationHandle h)
{
    if (h.IsValid()) Addressables.Release(h);
}

public sealed class AddressableResourceHandle<T> : IDisposable
{
    private AsyncOperationHandle<T> _handle;
    private int _refCount = 1;

    public AddressableResourceHandle(AsyncOperationHandle<T> handle) { _handle = handle; }
    public T Result => _handle.Result;

    public void IncrementRefCount() => _refCount++;
    public void Dispose()
    {
        if (--_refCount > 0) return;
        SafeRelease(_handle);
    }
}
```

## Quick reference — instantiate with cleanup binding

```csharp
public static async Task<GameObject> SpawnAsync(string address, Transform parent)
{
    var op = Addressables.InstantiateAsync(address, parent);
    var go = await op.Task;
    // Tag the instance so a generic Despawn(go) can ReleaseInstance correctly.
    go.AddComponent<InstanceBinding>().Op = op;
    return go;
}

public static void Despawn(GameObject go)
{
    if (go == null) return;
    if (go.TryGetComponent<InstanceBinding>(out var b))
        Addressables.ReleaseInstance(b.Op);   // destroys + decrements
    else
        UnityEngine.Object.Destroy(go);       // non-Addressables fallback
}

public class InstanceBinding : MonoBehaviour { public AsyncOperationHandle<GameObject> Op; }
```

## Related
- `clibridge4unity-async-mainthread` — Addressables uses `Task`/`AsyncOperationHandle` and follows the same main-thread rules
- `clibridge4unity-domain-reload` — addressables handles die on domain reload; rebuild catalog reference in `[InitializeOnLoad]` if you cache anything
- `clibridge4unity-prefab-workflow` — instantiating via Addressables vs `PrefabUtility.LoadPrefabContents` is a separate path
