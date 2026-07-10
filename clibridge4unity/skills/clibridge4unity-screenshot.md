---
name: clibridge4unity-screenshot
description: Capture a screenshot of Unity — an editor view, the game view, a camera render, a specific GameObject, a prefab asset, or a UXML file. Use whenever you need to see what's on screen or verify a visual change. Output is a PNG you read back with the Read tool.
---

# Screenshots

`clibridge4unity SCREENSHOT` — one command, smart routing. PNGs land in `%TEMP%/clibridge4unity_screenshots/` (overwrites previous; no accumulation). Editor-window, prefab, GameObject and UXML renders are capped at **1280px** on the long edge; `camera` and `gameview` render at requested/native resolution (not downscaled).

## Routing modes

```bash
# 1. Editor windows — CLI-side Win32 PrintWindow capture, works mid-compile (no pipe needed)
clibridge4unity SCREENSHOT                # whole editor (default = "editor")
clibridge4unity SCREENSHOT scene
clibridge4unity SCREENSHOT inspector
clibridge4unity SCREENSHOT hierarchy
clibridge4unity SCREENSHOT console
clibridge4unity SCREENSHOT project
clibridge4unity SCREENSHOT profiler

# 2. Camera render — Camera.main (or first scene camera if none tagged MainCamera), NO overlays (OnGUI / runtime UIDocument / IMGUI not drawn)
clibridge4unity SCREENSHOT camera
clibridge4unity SCREENSHOT camera 1920x1080

# 3. Game view — what the player sees (OnGUI, runtime UIDocument, chrome included)
clibridge4unity SCREENSHOT gameview

# 4. Scene GameObject — 3D gets a 3-view atlas (front|right|top).
#    Scene UI under a Canvas is NOT rendered here — errors and tells you to use the asset path instead.
clibridge4unity SCREENSHOT Player

# 5. Prefab asset — UI auto-sizes from RectTransform/Canvas; 3D gets an 8-angle turntable
clibridge4unity SCREENSHOT Assets/Prefabs/Player.prefab

# 6. UXML asset — TWO views by default (as-authored + all-revealed + list). Offscreen EditorWindow, root's declared size (fallback 1920x1080). .uss/.tss deps force-reimported first.
clibridge4unity SCREENSHOT Assets/UI/Card.uxml                     # → 2 PNGs: as-authored + all-revealed (+ unhidden list)
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el "#card-grid"   # single view: sub-element only
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el ".active-row"  # by class
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el card-grid      # bare name (tries name then class)
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --reveal            # single view: unhide ALL hidden elements + list them
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --show "#panel,.tab" # single view: force-show; --hide "#overlay" to hide

# 7. Multi-asset grid (one image, labeled cells)
clibridge4unity SCREENSHOT Assets/A.prefab Assets/B.prefab Assets/C.prefab

# --output also copies the PNG to a chosen path
clibridge4unity SCREENSHOT gameview --output ./docs/screenshot.png
```

## How to choose

| Want to see | Use |
|---|---|
| Whole Unity Editor | `SCREENSHOT` (no args) |
| What the player sees incl. runtime UI | `SCREENSHOT gameview` |
| World geometry, no UI | `SCREENSHOT camera` |
| A GameObject in isolation | `SCREENSHOT <name>` |
| A prefab file without opening it | `SCREENSHOT Assets/path.prefab` |
| A UXML layout while iterating styling | `SCREENSHOT Assets/path.uxml` |
| Several assets side-by-side | `SCREENSHOT a.prefab b.prefab c.prefab` |

The result line includes `output: <path>`; Read it back to view the PNG. Editor-window modes are pipe-free, so use them as your fallback when other commands time out mid-compile.

## Seeing hidden UXML elements (default 2-view, then `--show`/`--hide`)
A UXML often has panels gated behind `display:none`, `visibility:hidden`, or `opacity:0` (a `.hidden` class, a runtime-shown dialog) that render blank in the authored state. So a **bare** `SCREENSHOT Foo.uxml` gives you **two images**: the as-authored view, and a second with everything unhidden — plus a **list of each element it unhid** with the reason, e.g. `#settings-panel (VisualElement) [display:none]`. That list is your selector vocabulary.
- Dial in the exact state on a second run: `--show "#a,.b"` force-shows elements (and their ancestors); `--hide "#c"` force-hides them. Selectors are comma-separated `#name` / `.class` / bare name; `--show`/`--hide` apply to *all* matches of a class. Combine: `--reveal --hide "#debug-overlay"` = everything except the overlay.
- `--reveal` alone gives just the revealed view (single image); `--el` gives just that element (single image). Any explicit flag opts out of the 2-view default.

Overrides are inline styles on a throwaway render tree — the on-disk UXML is never modified.

## Verify UI at multiple device resolutions (phone + tablet)
Never sign off UI on one resolution — a layout fine on tablet can clip or strand controls out of thumb reach on a tall phone. Render ≥1 phone + ≥1 tablet aspect and **present both screenshots back to the user**. `gameview` overwrites its temp PNG each call, so each pass needs its own `--output`:

```bash
clibridge4unity GAMEVIEW 2556x1179 && clibridge4unity SCREENSHOT gameview --output ./.ui/iphone.png
clibridge4unity GAMEVIEW 2732x2048 && clibridge4unity SCREENSHOT gameview --output ./.ui/ipad.png
```

Targets (swap W/H for portrait): iPhone 15/14 Pro 2556×1179 (19.5:9) · iPhone SE 1334×750 (16:9) · iPad Pro 12.9" 2732×2048 (4:3) · iPad Air 2360×1640 (~3:2).

For a **UXML asset** (no play mode): set the root width/height to the device size and `SCREENSHOT Assets/UI/Foo.uxml --output …` once per device.
