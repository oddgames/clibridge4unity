---
name: clibridge4unity-ui
description: Work with Unity UI Toolkit assets — discover UXML/USS/TSS files, surface UI Toolkit import errors, render UXML for visual verification. Use when the task touches `.uxml`, `.uss`, or `.tss` files, or when UI is rendering wrong and you suspect a stylesheet import issue.
---

# UI Toolkit workflow

The bridge surfaces UI Toolkit asset state through three places: `UI_DISCOVER` (inventory), `LOG ui errors` (import problems), and `SCREENSHOT *.uxml` (visual check). It also auto-decorates any command that references a `.uss`/`.uxml`/`.tss` path with matching import errors.

## Inventory UI assets + custom components

```bash
clibridge4unity UI_DISCOVER
```

Lists every `.uxml`, `.uss`, `.tss` in the project plus any custom `VisualElement` subclasses registered via `[UxmlElement]` / `UxmlFactory`. Useful when you're trying to find which file owns a class name.

Equivalent: `ASSET_DISCOVER ui` covers UI prefabs/sprites/fonts too.

## Find import errors

UI Toolkit assets fail silently in Unity's UI — broken USS / malformed UXML doesn't pop a dialog, it just looks wrong. Surface them with:

```bash
clibridge4unity LOG ui errors
```

This filters Unity's console for UI Toolkit import errors only. Any command that references a `.uss`/`.uxml`/`.tss` path will also append matching import errors automatically — you don't need to chain `LOG` yourself.

## Render a UXML file

To verify what a UXML actually looks like (without instantiating it into a scene):

```bash
clibridge4unity SCREENSHOT Assets/UI/Card.uxml
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el "#card-grid"   # sub-element by id
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el ".active-row"  # by class
```

The UXML and its referenced `.uss`/`.tss` deps are force-reimported first, so on-disk edits show up immediately. Default render size is 800x450.

## Edit-then-verify loop

```bash
# 1. Edit Assets/UI/Card.uxml or .uss
# 2. Screenshot — also auto-reimports + surfaces import errors
clibridge4unity SCREENSHOT Assets/UI/Card.uxml
# 3. If it looks wrong, check for import errors
clibridge4unity LOG ui errors
```

## When UI is broken at runtime

- `SCREENSHOT gameview` shows what the player sees including runtime `UIDocument`s — use this rather than `SCREENSHOT camera` (which skips overlays).
- Raycast gotcha: all `Image` and `TMP_Text` default to `raycastTarget = true`. Decorative child graphics block parent buttons. Set `raycastTarget = false` on decorative children.

## Related skills

- `unity-screenshot` — for the SCREENSHOT command in full.
- `unity-assets` — for moving/renaming UXML/USS files (use `ASSET_MOVE` to preserve GUIDs and references).
