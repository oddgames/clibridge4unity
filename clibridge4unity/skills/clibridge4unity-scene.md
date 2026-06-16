---
name: clibridge4unity-scene
description: Manipulate the Unity scene and play mode — create/find/delete GameObjects, inspect hierarchy, enter/exit play mode, control scene view. Use when the task is about the running scene's structure, not about asset files or component fields on a specific object.
---

# Scene & play mode

Apply standard Unity/C# knowledge; only the `clibridge4unity` commands, flags, and workflow contracts below are project-specific.

## Hierarchy & objects

```bash
clibridge4unity INSPECTOR                          # whole-scene hierarchy (brief, all roots recursed)
clibridge4unity INSPECTOR Canvas/Panel             # one GameObject + components + serialized fields
clibridge4unity INSPECTOR Canvas/Panel --children --brief   # subtree, components only, no field dumps
clibridge4unity INSPECTOR Canvas/Panel --depth 2
clibridge4unity INSPECTOR Canvas --filter Button   # filter subtree by name OR component
```

`INSPECTOR Assets/Prefabs/Foo.prefab` works the same on prefab assets — see `clibridge4unity-prefab`.

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
clibridge4unity CREATE '{"name":"Panel","components":["Image","CanvasGroup"],"parent":"Canvas"}'  # parented + componented
clibridge4unity DELETE Canvas/OldPanel
```

Component changes after creation: see `clibridge4unity-components`.

## Scene file ops

```bash
clibridge4unity SAVE                          # save current scene
clibridge4unity LOAD Assets/Scenes/Main.unity # load a scene
```

## Editing objects that only exist in play mode

A runtime-instantiated or additively-loaded object that `FIND` locates during play vanishes in edit mode; structural edits only persist if made on the source asset in edit mode then `SAVE`. Locate the source:

```bash
# 1. FIND returns no sceneMatches in edit mode (error + sceneSuggestions instead):
clibridge4unity FIND AILiveryGeneratorUI

# 2. Baked into a prefab? Search via CODE_EXEC_RETURN:
#    foreach AssetDatabase.FindAssets("t:Prefab") → LoadAssetAtPath<GameObject>
#    → GetComponentInChildren<TheComponent>(true) != null  → print the path

# 3. Else it's in an additively-loaded scene — grep scene files for a unique name, then LOAD:
#    foreach AssetDatabase.FindAssets("t:Scene") → File.ReadAllText(path).Contains("TyresButton")
clibridge4unity LOAD Assets/Scenes/liverydesigner.unity
clibridge4unity INSPECTOR AILiveryGeneratorUI/TopPanel --children --brief
```

Make structural changes (easiest via `CODE_EXEC_RETURN` — see `clibridge4unity-run-code`), mark dirty, then `SAVE`:

```csharp
UnityEditor.EditorUtility.SetDirty(rootGO);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rootGO.scene);
```
```bash
clibridge4unity SAVE
```

**Verify layout numerically, not visually** — runtime-set text/labels won't appear in an edit-mode screenshot. Inside one `CODE_EXEC_RETURN` script: `LayoutRebuilder.ForceRebuildLayoutImmediate(container)`, read each child's `anchoredPosition` / `rect.size`, toggle `SetActive(false)` + rebuild + read + restore to confirm collapse (leaves no trace).

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

Use `clibridge4unity-screenshot`, not `INSPECTOR`. `SCREENSHOT camera` renders the main camera; `SCREENSHOT gameview` includes OnGUI / runtime UI.
