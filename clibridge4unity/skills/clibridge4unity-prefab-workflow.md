---
name: clibridge4unity-prefab-workflow
description: How to EDIT a prefab's contents without silently nulling references — the three approaches (CLI instantiate/save, YAML GUID swap, code) and the `SavePrefabAsset`-vs-`SaveAsPrefabAsset` footgun. Also asset-vs-instance-vs-variant, nested prefabs, overrides, prefab mode. Auto-trigger on `PrefabUtility.`, `SavePrefabAsset`, `SaveAsPrefabAsset`, `LoadPrefabContents`, `UnloadPrefabContents`, `LoadAssetAtPath`+prefab, `PrefabStage`, `ApplyPropertyOverride`, `RevertPrefabInstance`, "edit the prefab directly", "edit the .prefab YAML", "swap a GUID", "material/reference disappeared after save", "override disappeared", "nested prefab", "variant". Prefab editing has parallel save paths that are NOT equivalent — the wrong one drops references with no error.
---

# Editing prefabs

Apply standard Unity prefab knowledge (asset vs instance vs variant, `PrefabStageUtility.GetCurrentPrefabStage`, `GetCorrespondingObjectFromSource`, override apply/revert along the variant chain, `RecordPrefabInstancePropertyModifications` + `MarkSceneDirty`). The house-specific part — editing a prefab's contents without nulling references — is below.

## Pick the lightest approach that does the job

1. **CLI instantiate → edit → save (default).** Read with `INSPECTOR Assets/X.prefab --children` / `FIND prefab:Assets/X.prefab/Child`. Change it: `PREFAB_INSTANTIATE Assets/X.prefab` → `COMPONENT_SET/ADD/REMOVE` on the instance (`clibridge4unity-components`) → `PREFAB_SAVE <root> Assets/X.prefab` writes back. Can't corrupt fileIDs, propagates to instances/variants. Use for field/component edits.

2. **YAML — swap a GUID in place (surgical, safest for repointing a reference).** A `.prefab` is YAML text. To point a reference (material, mesh, sprite, font) at a different asset, replace the `guid` in its `{fileID: …, guid: …, type: …}` entry with the target's GUID (from the target's `.meta`), then `REIMPORT Assets/X.prefab`. Never round-trips through Unity's serializer, so it **cannot null unrelated references** — the right tool when you're only changing *what a reference points at*. Rules: keep the whole `fileID`+`guid` pair consistent, never invent fileIDs, never restructure the hierarchy by hand. After: `REIMPORT` → `SCREENSHOT` → `LOG errors`.

3. **Code — and the save method matters.** When the edit needs construction logic (build a subtree, wire a persistent `UnityEvent`), `CODE_EXEC` it. The two save paths are **not** equivalent:
   - **In-place (preferred for an existing prefab):** `AssetDatabase.LoadAssetAtPath<GameObject>(path)` → mutate → `PrefabUtility.SavePrefabAsset(root)`. Edits the real asset in memory; references survive. ODD Framework pattern (`AILiveryBatchTools`) — re-resolve via `LoadAssetAtPath` immediately before saving, since an async reimport can invalidate the object.
   - **Isolated round-trip:** `LoadPrefabContents(path)` → mutate → `SaveAsPrefabAsset(root, path)` → `UnloadPrefabContents(root)` in try/finally. Loads into a temp preview scene and re-serializes the whole prefab; references that don't resolve in that isolated scene are **nulled** on save (the "material/rim disappeared after save" bug). `UnloadPrefabContents` is mandatory; its root dies on domain reload — don't cache it.

   Rule of thumb: **edit an existing prefab in place → `SavePrefabAsset`; build a brand-new one → `SaveAsPrefabAsset`.**

## Related
- `clibridge4unity-prefab` — `PREFAB_CREATE` / `PREFAB_INSTANTIATE` / `PREFAB_SAVE`
- `clibridge4unity-components` — `COMPONENT_SET/ADD/REMOVE` on the instantiated prefab
- `clibridge4unity-assets` — `REIMPORT`/`ASSET_RESERIALIZE` after a YAML edit; GUID-preserving moves
- `clibridge4unity-serialization` — what survives prefab → instance → variant
- `clibridge4unity-run-code` — `CODE_EXEC` for the code path
- `clibridge4unity-domain-reload` — `LoadPrefabContents` roots die on reload
