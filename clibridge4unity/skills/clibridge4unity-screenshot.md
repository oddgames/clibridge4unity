---
name: clibridge4unity-screenshot
description: Capture a screenshot of Unity — an editor view, the game view, a camera render, a specific GameObject, a prefab asset, or a UXML file. Use whenever you need to see what's on screen or verify a visual change. Output is a PNG you read back with the Read tool.
---

# Screenshots

One command, smart routing. PNGs land in `%TEMP%/clibridge4unity_screenshots/` (overwrites previous — the directory does not accumulate). All renders capped at **1280px** on the long edge so vision models don't choke on 4K images.

## Routing modes

```bash
# 1. Editor windows — CLI-side Win32 capture, works even mid-compile (no pipe needed)
clibridge4unity SCREENSHOT                # whole editor (default = "editor")
clibridge4unity SCREENSHOT scene
clibridge4unity SCREENSHOT inspector
clibridge4unity SCREENSHOT hierarchy
clibridge4unity SCREENSHOT console
clibridge4unity SCREENSHOT project
clibridge4unity SCREENSHOT profiler

# 2. Camera render — Camera.main only, NO overlays (OnGUI / runtime UIDocument / IMGUI not drawn)
clibridge4unity SCREENSHOT camera
clibridge4unity SCREENSHOT camera 1920x1080

# 3. Game view — what the player actually sees (OnGUI, runtime UIDocument, chrome included)
clibridge4unity SCREENSHOT gameview

# 4. Scene GameObject — 3D objects get a 3-view atlas (front|right|top); UI under a Canvas renders the canvas
clibridge4unity SCREENSHOT Player
clibridge4unity SCREENSHOT Canvas/SettingsPanel

# 5. Prefab asset — UI auto-sizes from RectTransform/Canvas; 3D gets an 8-angle turntable
clibridge4unity SCREENSHOT Assets/Prefabs/Player.prefab

# 6. UXML asset — rendered at 800x450 via an offscreen EditorWindow. .uss/.tss deps force-reimported first.
clibridge4unity SCREENSHOT Assets/UI/Card.uxml
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el "#card-grid"   # sub-element only
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el ".active-row"  # by class
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el card-grid      # bare name (tries name then class)

# 7. Multi-asset grid (one image, labeled cells)
clibridge4unity SCREENSHOT Assets/A.prefab Assets/B.prefab Assets/C.prefab
```

## Common combos

```bash
clibridge4unity SCREENSHOT gameview --output ./docs/screenshot.png   # also copy to a chosen path
```

## How to choose

| Want to see | Use |
|---|---|
| Whole Unity Editor at a glance | `SCREENSHOT` (no args) |
| What the player sees including runtime UI | `SCREENSHOT gameview` |
| Just the world geometry, no UI | `SCREENSHOT camera` |
| A GameObject in isolation | `SCREENSHOT <name>` |
| A prefab file without opening it | `SCREENSHOT Assets/path.prefab` |
| A UXML layout while iterating styling | `SCREENSHOT Assets/path.uxml` |
| Side-by-side comparison of several assets | `SCREENSHOT a.prefab b.prefab c.prefab` |

## After capturing

The result line includes `output: <path>`. Read it back with the Read tool to view the PNG.

## When Unity is busy

Editor-window modes (`SCREENSHOT scene`, etc.) work even when Unity is mid-compile because they don't need the pipe — they capture the window via Win32 `PrintWindow`. Use these as your fallback when other commands are timing out.
