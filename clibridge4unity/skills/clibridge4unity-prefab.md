---
name: clibridge4unity-prefab
description: Create, instantiate, and save prefab assets. Use when working with .prefab files — making a new prefab, dropping one into the scene, applying scene changes back to the asset, or inspecting prefab contents without opening it.
---

# Prefabs

Standard Unity prefab/PrefabUtility knowledge applies; below are the clibridge4unity-specific commands and contracts.

## Inspect a prefab asset (without opening it)

`INSPECTOR` accepts prefab asset paths (replaces the old `PREFAB_HIERARCHY`):

```bash
clibridge4unity INSPECTOR Assets/Prefabs/Player.prefab
clibridge4unity INSPECTOR Assets/Prefabs/UI/Menu.prefab --children --brief
clibridge4unity INSPECTOR Assets/Prefabs/UI/Menu.prefab --filter Button
```

## Find by name inside a prefab

```bash
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button,Panel   # comma = OR
```

## Create / save / instantiate

```bash
clibridge4unity PREFAB_CREATE MyPrefab Assets/Prefabs
clibridge4unity PREFAB_SAVE PlayerObject Assets/Prefabs/Player.prefab   # save scene GO, or apply changes if it's an instance
clibridge4unity PREFAB_INSTANTIATE Assets/Prefabs/Enemy.prefab
clibridge4unity PREFAB_INSTANTIATE Assets/Prefabs/Enemy.prefab Canvas   # parented
```

## Render a prefab visually

`SCREENSHOT Assets/Foo.prefab` renders the asset without instantiating. UI prefabs auto-size from their RectTransform/Canvas; 3D prefabs render an 8-angle turntable. See `clibridge4unity-screenshot`.

## Modify components on a prefab asset

`COMPONENT_SET` and friends work on scene instances. To modify a prefab asset:
- Instantiate (`PREFAB_INSTANTIATE`), modify via `clibridge4unity-components`, then `PREFAB_SAVE` back.
- Or use `clibridge4unity-run-code` with `PrefabUtility.LoadPrefabContents(path)` → modify → `PrefabUtility.SaveAsPrefabAsset(contentsRoot, path)` → `PrefabUtility.UnloadPrefabContents(contentsRoot)`. The `UnloadPrefabContents` call is mandatory.

## Related
- `clibridge4unity-screenshot` — render prefabs
- `clibridge4unity-components` — modify instances
- `clibridge4unity-run-code` — atomic prefab edits
