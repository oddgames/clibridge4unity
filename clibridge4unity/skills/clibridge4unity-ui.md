---
name: clibridge4unity-ui
description: Work with Unity UI Toolkit assets — discover UXML/USS/TSS files, surface UI Toolkit import errors, render UXML for visual verification. Use when the task touches `.uxml`, `.uss`, or `.tss` files, or when UI is rendering wrong and you suspect a stylesheet import issue.
---

# UI Toolkit workflow

Apply standard Unity UI Toolkit knowledge; below is only what's specific to this bridge.

## Inventory

`UI_DISCOVER` is an alias for `ASSET_DISCOVER ui`. Lists UI sprite folders, fonts (incl. TMP), UI/Canvas/RectTransform prefabs, and scenes. It does NOT enumerate `.uxml`/`.uss`/`.tss` files or `VisualElement`/`[UxmlElement]`/`UxmlFactory` registrations — no bridge command does; use `Grep` or `CODE_ANALYZE` to find which file owns a class.

## Import errors

```bash
clibridge4unity LOG ui errors
```

Filters Unity's console for UI Toolkit import errors only. Any command that references a `.uss`/`.uxml`/`.tss` path also appends matching import errors automatically — no need to chain `LOG`.

## Render a UXML

```bash
clibridge4unity SCREENSHOT Assets/UI/Card.uxml
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el "#card-grid"   # sub-element by id
clibridge4unity SCREENSHOT Assets/UI/Card.uxml --el ".active-row"  # by class
```

The UXML and its referenced `.uss`/`.tss` deps are force-reimported first, so on-disk edits show up immediately. Render size is inferred from the UXML root element's declared pixel width/height, falling back to 1920x1080 when the root has no explicit size.

## Runtime

- `SCREENSHOT gameview` shows what the player sees including runtime `UIDocument`s — use this rather than `SCREENSHOT camera` (which skips overlays).

## Related skills

- `clibridge4unity-screenshot` — SCREENSHOT command.
- `clibridge4unity-assets` — moving/renaming UXML/USS (`ASSET_MOVE` preserves GUIDs).
