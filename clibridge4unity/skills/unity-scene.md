---
name: unity-scene
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
