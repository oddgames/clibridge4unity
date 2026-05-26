---
name: unity-prefab
description: Create, instantiate, and save prefab assets. Use when working with .prefab files — making a new prefab, dropping one into the scene, applying scene changes back to the asset, or inspecting prefab contents without opening it.
---

# Prefabs

## Inspect a prefab asset (without opening it)

`INSPECTOR` works on prefab asset paths the same way it works on scene objects:

```bash
clibridge4unity INSPECTOR Assets/Prefabs/Player.prefab
clibridge4unity INSPECTOR Assets/Prefabs/UI/Menu.prefab --children --brief
clibridge4unity INSPECTOR Assets/Prefabs/UI/Menu.prefab --filter Button
```

This replaces the old `PREFAB_HIERARCHY` command.

## Find by name inside a prefab

```bash
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button,Panel   # comma = OR
```

## Create a prefab

```bash
# Make a new prefab asset from scratch
clibridge4unity PREFAB_CREATE MyPrefab Assets/Prefabs

# Save a scene GameObject as a prefab (or apply changes if it's an instance)
clibridge4unity PREFAB_SAVE PlayerObject Assets/Prefabs/Player.prefab
```

## Instantiate into the scene

```bash
clibridge4unity PREFAB_INSTANTIATE Assets/Prefabs/Enemy.prefab
clibridge4unity PREFAB_INSTANTIATE Assets/Prefabs/Enemy.prefab Canvas   # parented
```

## Render a prefab visually

See `unity-screenshot` — `SCREENSHOT Assets/Foo.prefab` renders the asset directly without needing to instantiate it. UI prefabs auto-size from their RectTransform/Canvas; 3D prefabs render an 8-angle turntable.

## Modify components on a prefab asset

`COMPONENT_SET` and friends work on scene instances. To modify a prefab asset:
- Either instantiate it (`PREFAB_INSTANTIATE`), modify the instance with `unity-components`, then `PREFAB_SAVE` it back.
- Or use `unity-run-code` with `PrefabUtility.LoadPrefabContents` / `SavePrefabAsset` for atomic edits.
