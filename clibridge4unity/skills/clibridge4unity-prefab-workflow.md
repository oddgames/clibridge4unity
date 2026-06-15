---
name: clibridge4unity-prefab-workflow
description: Use for any prefab work — editing assets vs editing scene instances, prefab variants, nested prefabs, applying/reverting overrides, prefab mode (`PrefabStage`), batch prefab edits, missing-script warnings, "instance went pink", "I edited the prefab and the override disappeared." Auto-trigger on `PrefabUtility.`, `PrefabStage`, `PrefabStageUtility`, `PrefabAssetType`, `PrefabInstanceStatus`, `SaveAsPrefabAsset`, `LoadPrefabContents`, `UnloadPrefabContents`, `ApplyPropertyOverride`, `RevertPrefabInstance`, "open in prefab mode", "edit the prefab directly", "nested prefab", "variant", "override". Prefab editing has two parallel paths (asset vs instance) and four "where am I" contexts (asset, in-scene instance, prefab mode, prefab-as-asset-loaded-into-memory) — getting them confused destroys references silently.
---

# Unity Prefab Workflow

Prefabs are Unity's saved object templates. The same noun ("a prefab") refers to four different things depending on context — an asset, an instance in a scene, an instance opened in prefab mode, or a fully-loaded `GameObject` you got from `PrefabUtility.LoadPrefabContents`. Knowing which one you're holding is the difference between "edit applies to all instances" and "edit lives on this one instance only" and "edit silently does nothing." This skill is the decision tree.

## The one rule that prevents most of these

**Always know which prefab context you're editing.** A `GameObject` you got via `Resources.Load` / `Addressables` / `AssetDatabase.LoadAssetAtPath` is the *asset*; edits to it modify the source and propagate to every instance. A `GameObject.Find(...)` result is an *instance*; edits live as overrides on that instance. `PrefabUtility.LoadPrefabContents(path)` is the asset's contents loaded into a temporary scene; edits don't save until you call `SaveAsPrefabAsset`. If you don't know which of these you have, stop and check.

## Pitfall catalog

### 1. Editing a scene instance ≠ editing the prefab
Modifying a component on a scene instance creates an **instance override**. The prefab asset doesn't change; other instances don't change; the override shows as a blue bar in the inspector. If someone "applies overrides" later, the asset finally updates. If someone "reverts overrides," your change vanishes.
- **Rule:** ask the question explicitly before editing — "do I want this change on every instance (edit the asset) or just this one (override the instance)?" In code: `PrefabUtility.GetCorrespondingObjectFromOriginalSource(obj)` returns the asset; `PrefabUtility.GetPrefabInstanceHandle(obj)` returns the instance root.

### 2. Direct `MonoBehaviour` edits in scene tools can silently fail to mark the prefab dirty
A custom inspector or editor menu that modifies a serialized field on a prefab instance via reflection or direct assignment doesn't always notify Unity — the change appears, but on save the prefab asset reverts and the override is lost.
- **Rule:** use `SerializedObject` + `serializedObject.ApplyModifiedProperties()` for any field mutation. `ApplyModifiedProperties` records an undo step, dirties the right object, and respects multi-object editing. If you genuinely need a non-`SerializedObject` mutation, follow it with `EditorUtility.SetDirty(target)` and (for prefab instances) `PrefabUtility.RecordPrefabInstancePropertyModifications(target)`.

### 3. Loading a prefab asset, modifying components, instantiating, **destroying** = you just edited the asset
`AssetDatabase.LoadAssetAtPath<GameObject>(path)` returns the *asset* itself. Modifying it directly mutates the on-disk asset. You haven't created an instance; you're holding the source. Calling `Destroy` on it tries to destroy the asset. The next save corrupts the prefab.
- **Rule:** if you need to read + mutate prefab contents from code, use `PrefabUtility.LoadPrefabContents(path)` — it returns a `GameObject` loaded into a temporary scene that's independent of the asset. Mutate, then `PrefabUtility.SaveAsPrefabAsset(root, path)` and **always** `PrefabUtility.UnloadPrefabContents(root)` in a `finally`. Forgetting the unload leaks the temp scene and breaks subsequent loads.

### 4. Prefab variants inherit from a base — edits to the base propagate (mostly)
A variant of `Truck.prefab` called `RacingTruck.prefab` automatically pulls in all changes from `Truck.prefab` *unless* the variant has an override on that field. Adding a new field to the base appears on all variants instantly. Removing a field from the base is a destructive op for any variant override depending on it.
- **Rule:** treat base prefabs as the source of truth for shared fields. Add overrides on variants only for the values that genuinely differ. Use `PrefabUtility.GetPrefabAssetType(asset)` (returns `Variant` / `Regular` / `Model`) to detect variants in tooling, and `PrefabUtility.GetCorrespondingObjectFromSource(asset)` to walk up the variant chain.

### 5. Nested prefabs serialize the inner prefab as a reference — modifying it locally creates an override on the OUTER
If `Vehicle.prefab` nests `Wheel.prefab`, editing the wheel inside `Vehicle`'s prefab mode actually creates a property override on `Vehicle.prefab` (it doesn't modify `Wheel.prefab`). To edit the wheel itself, double-click into `Wheel.prefab`.
- **Rule:** check the inspector's blue-bar / breadcrumb header. If you see the inner prefab's name on top, you're editing the inner asset. If you see the outer, your edits are overrides on the outer's reference to the inner. Get this wrong and "fixing the wheel" actually adds an override to every vehicle that uses that wheel.

### 6. Missing scripts on a prefab — Unity keeps the slot, can't recover the data automatically
If you delete a `MonoBehaviour` script that a prefab references, the prefab gets a "Missing (Mono Script)" component. Unity preserves the data slot — once the script is restored (or replaced via GUID-pin), the data comes back. Until then, removing the missing entry by hand discards the saved data.
- **Rule:** never "Remove Component" on a Missing slot unless you've confirmed the data is unrecoverable. If a script was renamed/moved, restore the GUID match: edit `.meta` of the new script to use the old GUID, or use `MissingScriptRedirector` style tooling. Otherwise import the old script back, do a `OnValidate`-style migration, then delete the component cleanly.

### 7. `EditorSceneManager.MarkSceneDirty(scene)` is required when you mutate prefab instances by script in a scene
Otherwise the scene save silently drops your overrides. Same for `PrefabUtility.RecordPrefabInstancePropertyModifications(instance)` on the modified instance.
- **Rule:** when mutating a scene instance from an editor tool, follow each modification with `Undo.RecordObject(target, "label")` (gives you undo + dirty) AND `PrefabUtility.RecordPrefabInstancePropertyModifications(target)` (ensures the override is captured). Test by closing the scene, reopening, and confirming the change persists.

### 8. `PrefabStage.GetCurrentPrefabStage()` is your context check from editor code
Editor tools that act on a selection need to know if the user is in prefab mode or the main scene. Calling `EditorSceneManager.GetActiveScene()` from prefab mode returns a temp scene, not the user's project scenes. Mutating the wrong scene saves to the wrong target.
- **Rule:** `var stage = PrefabStageUtility.GetCurrentPrefabStage();` — if not null, you're in prefab mode and `stage.scene` is the prefab's temp scene, `stage.assetPath` is where to save. Branch behaviour accordingly. Tools that should be no-ops in prefab mode bail out early.

### 9. Applying overrides walks up the prefab variant chain — be explicit about target
`PrefabUtility.ApplyObjectOverride(componentInstance, prefabAssetPath, InteractionMode)` applies *to that path*. Calling it with the variant's path applies only to the variant. Calling it with the base's path applies to the base (and "uses" the existing override). Pick the wrong target and you either over-apply (the override disappears from every variant because it's now on the base) or under-apply (the override only sticks on the variant, not the base where it belongs).
- **Rule:** be explicit about which prefab in the chain you're applying to. Walk the chain with `GetCorrespondingObjectFromSource` to find the right asset before applying. For UI, `PrefabUtility.ApplyPropertyOverride` lets the user pick — prefer that.

### 10. PrefabContents needs an unload — leaking the temp scene corrupts subsequent loads
`var go = PrefabUtility.LoadPrefabContents(path); /* edit */ PrefabUtility.SaveAsPrefabAsset(go, path);` is incomplete. The temp scene Unity created to hold the loaded contents stays open. Subsequent `LoadPrefabContents` calls behave erratically.
- **Rule:** wrap in try/finally. Always `PrefabUtility.UnloadPrefabContents(go)` in `finally`. Even if `SaveAsPrefabAsset` throws.

## Workflow

1. **Decide where the edit lives.** Asset-wide change → load via `PrefabUtility.LoadPrefabContents` (script) or open in prefab mode (manual). Instance-specific → edit in the scene with `SerializedObject` discipline.
2. **In code, always be explicit about target.** Use `PrefabUtility.GetCorrespondingObjectFromOriginalSource` / `GetCorrespondingObjectFromSource` / `GetPrefabInstanceHandle` to confirm "what am I holding right now."
3. **Wrap `LoadPrefabContents` in try/finally with `UnloadPrefabContents`** — no exceptions.
4. **Pair every mutation with the dirty/record APIs.** `Undo.RecordObject` + `PrefabUtility.RecordPrefabInstancePropertyModifications` (for scene instances) or `EditorUtility.SetDirty` (for asset edits).
5. **Test the round-trip.** Save the scene/asset, close, reopen, confirm the change survives. Especially after override-system edits — the inspector lies (shows overrides until save).
6. **Inspect via `clibridge4unity`:** `INSPECTOR Assets/.../Foo.prefab` for the asset; `INSPECTOR Canvas/Panel/Foo` for a scene instance. The same `INSPECTOR` command handles both.

## Quick reference — safe asset edit

```csharp
public static void EditPrefabAsset(string path, Action<GameObject> mutate)
{
    var go = PrefabUtility.LoadPrefabContents(path);
    try
    {
        mutate(go);
        PrefabUtility.SaveAsPrefabAsset(go, path);
    }
    finally
    {
        PrefabUtility.UnloadPrefabContents(go);
    }
}
```

## Quick reference — safe scene-instance edit

```csharp
public static void EditInstance(MonoBehaviour mb, Action edit)
{
    Undo.RecordObject(mb, "Edit instance");
    edit();
    PrefabUtility.RecordPrefabInstancePropertyModifications(mb);
    EditorSceneManager.MarkSceneDirty(mb.gameObject.scene);
}
```

## Quick reference — "what am I holding?"

```csharp
bool isPrefabAsset    = PrefabUtility.IsPartOfPrefabAsset(obj);
bool isSceneInstance  = PrefabUtility.IsPartOfPrefabInstance(obj);
bool isInPrefabMode   = PrefabStageUtility.GetCurrentPrefabStage() != null;
PrefabAssetType type  = PrefabUtility.GetPrefabAssetType(obj); // Regular / Variant / Model / NotAPrefab
GameObject assetRoot  = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance);
```

## Related
- `clibridge4unity-serialization` — what survives across the prefab → instance → variant chain
- `clibridge4unity-prefab` — the bridge `PREFAB_CREATE` / `PREFAB_INSTANTIATE` / `PREFAB_SAVE` commands (handles the asset/instance distinction correctly)
- `clibridge4unity-domain-reload` — `LoadPrefabContents` instances die on reload — don't cache across recompiles
