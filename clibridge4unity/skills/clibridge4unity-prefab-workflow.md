---
name: clibridge4unity-prefab-workflow
description: Use for any prefab work — editing assets vs editing scene instances, prefab variants, nested prefabs, applying/reverting overrides, prefab mode (`PrefabStage`), batch prefab edits, missing-script warnings, "instance went pink", "I edited the prefab and the override disappeared." Auto-trigger on `PrefabUtility.`, `PrefabStage`, `PrefabStageUtility`, `PrefabAssetType`, `PrefabInstanceStatus`, `SaveAsPrefabAsset`, `LoadPrefabContents`, `UnloadPrefabContents`, `ApplyPropertyOverride`, `RevertPrefabInstance`, "open in prefab mode", "edit the prefab directly", "nested prefab", "variant", "override". Prefab editing has two parallel paths (asset vs instance) and four "where am I" contexts (asset, in-scene instance, prefab mode, prefab-as-asset-loaded-into-memory) — getting them confused destroys references silently.
---

# Unity Prefab Workflow

Apply standard Unity prefab knowledge (asset vs instance vs variant, the `LoadPrefabContents`/`SaveAsPrefabAsset`/`UnloadPrefabContents` try-finally pattern, `PrefabStageUtility.GetCurrentPrefabStage`, `GetCorrespondingObjectFromSource`, override apply/revert walking the variant chain, `RecordPrefabInstancePropertyModifications` + `MarkSceneDirty`). Below is only the project-specific bridge surface.

## Inspect via `clibridge4unity`

`INSPECTOR Assets/.../Foo.prefab` for the asset; `INSPECTOR Canvas/Panel/Foo` for a scene instance. The same `INSPECTOR` command handles both.

## Related
- `clibridge4unity-serialization` — what survives prefab → instance → variant
- `clibridge4unity-prefab` — bridge `PREFAB_CREATE` / `PREFAB_INSTANTIATE` / `PREFAB_SAVE`
- `clibridge4unity-domain-reload` — `LoadPrefabContents` roots die on reload; don't cache
