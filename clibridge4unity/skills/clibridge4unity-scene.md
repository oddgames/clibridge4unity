---
name: clibridge4unity-scene
description: Manipulate the Unity scene and play mode — create/find/delete GameObjects, inspect hierarchy, enter/exit play mode, control scene view. Use when the task is about the running scene's structure, not about asset files or component fields on a specific object.
---

# Scene & play mode

## Hierarchy & objects

```bash
# Whole-scene hierarchy (brief, all roots recursed)
clibridge4unity INSPECTOR

# One GameObject with its components + serialized fields
clibridge4unity INSPECTOR Canvas/Panel

# Recurse subtree, components only (no field dumps — concise)
clibridge4unity INSPECTOR Canvas/Panel --children --brief
clibridge4unity INSPECTOR Canvas/Panel --depth 2

# Filter subtree by name OR component
clibridge4unity INSPECTOR Canvas --filter Button
```

For prefab assets (not scene objects), `INSPECTOR Assets/Prefabs/Foo.prefab` works the same way — see `unity-prefab`.

## Finding

```bash
clibridge4unity FIND Player                              # scene (default)
clibridge4unity FIND scene:Player                        # explicit scene scope
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button # inside a prefab asset
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button,Panel  # comma = OR
```

## Create / delete

```bash
clibridge4unity CREATE MyObject

# JSON form for parented + componented creation
clibridge4unity CREATE '{"name":"Panel","components":["Image","CanvasGroup"],"parent":"Canvas"}'

clibridge4unity DELETE Canvas/OldPanel
```

For component changes after creation see `unity-components`.

## Scene file ops

```bash
clibridge4unity SAVE                          # save current scene
clibridge4unity LOAD Assets/Scenes/Main.unity # load a scene
```

## Editing objects that only exist in play mode

An object that `FIND` locates while the game is running can vanish in edit mode — it was instantiated at runtime (from a prefab, Resources/Addressables) or lives in a scene loaded **additively** at play. Structural edits (reparenting, adding a layout group, new children) only persist if you make them on the **source asset in edit mode**, then `SAVE`. Anything changed during play mode is discarded on `STOP`.

Locate the source, then edit it:

```bash
# 1. In edit mode the runtime object is gone — sceneMatches is empty:
clibridge4unity FIND AILiveryGeneratorUI

# 2. Is it baked into a prefab? Search every prefab via CODE_EXEC_RETURN:
#    foreach AssetDatabase.FindAssets("t:Prefab") → LoadAssetAtPath<GameObject>
#    → GetComponentInChildren<TheComponent>(true) != null  → print the path

# 3. Not a prefab → it's in an additively-loaded scene. Grep scene files for a
#    unique name (component type or a child GameObject), then LOAD it:
#    foreach AssetDatabase.FindAssets("t:Scene") → File.ReadAllText(path).Contains("TyresButton")
clibridge4unity LOAD Assets/Scenes/liverydesigner.unity
clibridge4unity INSPECTOR AILiveryGeneratorUI/TopPanel --children --brief
```

Make the structural change (reparenting + layout setup is easiest via `CODE_EXEC_RETURN` — see `unity-run-code`), then in the same script mark dirty and `SAVE`:

```csharp
// after mutating the hierarchy:
UnityEditor.EditorUtility.SetDirty(rootGO);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rootGO.scene);
```
```bash
clibridge4unity SAVE
```

**Verify layout numerically, not visually.** Runtime-set text/labels (anything assigned in `Awake`/`Start`) won't appear in an edit-mode screenshot, so a screenshot of the dormant scene is misleading. Instead call `LayoutRebuilder.ForceRebuildLayoutImmediate(container)` and read back each child's `anchoredPosition` / `rect.size`. To confirm a layout group collapses correctly, toggle the relevant children `SetActive(false)`, rebuild, read positions, then restore — all inside one `CODE_EXEC_RETURN` script (it leaves no trace once you restore).

**Gotcha — overflowing labels wrap to vertical text under a layout group.** Legacy uGUI nav buttons are often wide boxes (e.g. 897px) with left-aligned text that simply *overflows* horizontally; they look evenly spaced only because each box is far wider than its label. Drop them into a `HorizontalLayoutGroup` with `childControlWidth` + a `LayoutElement.preferredWidth` sized to the *spacing* (not the box), and the now-narrow box forces the TMP to word-wrap — one letter per line, reading as vertical text. Fix: set each label `tmp.textWrappingMode = TextWrappingModes.NoWrap` (+ `overflowMode = Overflow`) so it overflows sideways like the original. Confirm with `tmp.preferredWidth` vs the box width — if `preferredWidth > rect.width` and wrap is on, it will stack.

## Play mode

```bash
clibridge4unity PLAY                          # enter play mode
clibridge4unity PLAY Assets/Scenes/Game.unity # enter play mode with a scene
clibridge4unity STOP
clibridge4unity PAUSE                         # toggle
clibridge4unity STEP                          # single frame while paused
clibridge4unity PLAYMODE                      # query current state
```

You **cannot** `COMPILE` while in play mode — `STOP` first.

## Scene view & game view

```bash
clibridge4unity SCENEVIEW frame             # frame current selection
clibridge4unity SCENEVIEW frame:PlayerObj   # frame a specific object
clibridge4unity SCENEVIEW 2d                # switch to 2D
clibridge4unity SCENEVIEW 3d                # switch to 3D
clibridge4unity GAMEVIEW 1920x1080          # resize game view
clibridge4unity WINDOWS                     # list open editor windows + positions
```

## To see what the scene looks like

Don't use `INSPECTOR` for that — use `unity-screenshot`. `SCREENSHOT camera` renders the main camera; `SCREENSHOT gameview` includes OnGUI / runtime UI.
