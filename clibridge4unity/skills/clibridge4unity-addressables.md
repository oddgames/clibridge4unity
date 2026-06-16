---
name: clibridge4unity-addressables
description: Use for any Addressables work — loading, releasing, reference counting, AsyncOperationHandle lifetime, InstantiateAsync vs LoadAssetAsync, AssetReference vs raw address string, build catalogs, label vs key vs reference resolution, "asset not found at runtime but works in editor", leak hunting. Auto-trigger on `Addressables.`, `AsyncOperationHandle`, `LoadAssetAsync`, `LoadAssetsAsync`, `InstantiateAsync`, `Addressables.Release`, `Addressables.ReleaseInstance`, `AssetReference`, `IResourceLocation`, `AddressableAssetSettings`, "addressables InvalidKeyException", "still loaded after release", "double release", "WaitForCompletion freezes", "reentering Update". Addressables make async loading look easy and lifecycle accounting hard; apply standard lifetime discipline — this skill adds the bridge-side catalog checks.
---

# Unity Addressables

Addressables loading/release lifetime — one Load = one `Release`/`ReleaseInstance`, `IsValid()` guards before release, `AssetReference` caches its own handle, `WaitForCompletion` stalls/deadlocks (never inside an async continuation), label-vs-address bookkeeping, and rebuilding the content catalog so "works in editor, `InvalidKeyException` in player" goes away — is standard Unity knowledge. Apply it. What's specific to this toolchain is inspecting Addressables state from the bridge.

## Inspect Addressables from the bridge
- `CODE_EXEC_RETURN Addressables.ResourceLocators.SelectMany(l => l.Keys)` — dump the live catalog keys; the real check for "is my address actually registered at runtime?"
- `INSPECTOR Assets/Path/To/Asset.prefab` — sanity-check the asset exists/imports, independent of the catalog.
- `LOG errors` — surfaces runtime `InvalidKeyException` / "no location found" the instant a load fails, instead of chasing a silent null handle.

## Related
- `clibridge4unity-async-mainthread` — Addressables `Task`/`AsyncOperationHandle` follow the same main-thread rules
- `clibridge4unity-domain-reload` — handles die on domain reload; rebuild any cached reference in `[InitializeOnLoad]`
- `clibridge4unity-prefab-workflow` — Addressables instantiation vs `PrefabUtility.LoadPrefabContents` is a separate path
