---
name: clibridge4unity-ugui
description: Use for any classic uGUI (Canvas-based) UI work — Canvas, RectTransform, Image/RawImage, Button/Toggle/Slider/ScrollRect, TextMeshProUGUI, LayoutGroups, ContentSizeFitter, CanvasScaler, GraphicRaycaster. The central rule is AUTHOR uGUI AS A PREFAB (edit via the CLI or YAML), never build/restyle it from a runtime MonoBehaviour at play time. Auto-trigger on `Canvas`, `RectTransform`, `CanvasRenderer`, `GraphicRaycaster`, `CanvasScaler`, `Image`, `RawImage`, `Button`, `Toggle`, `Slider`, `ScrollRect`, `TextMeshProUGUI`/`TMP_Text`, `HorizontalLayoutGroup`/`VerticalLayoutGroup`/`GridLayoutGroup`, `ContentSizeFitter`, `LayoutElement`, `LayoutRebuilder`, `AddComponent<Image>()` (anti-pattern), `gameObject.AddComponent<...>()` in `Awake`/`Start` to build UI, `anchoredPosition`/`sizeDelta`/`anchorMin`/`pivot` set per-frame, "UI changes disappear when I stop play mode", "built the menu in code", "edited it in play mode and lost it". Distinct from UI Toolkit — see clibridge4unity-ui-toolkit for `.uxml`/`.uss`.
---

# Unity uGUI (Canvas-based UI)

Canvas / RectTransform / anchor / LayoutGroup / driven-property mechanics are standard Unity knowledge — apply them. What's specific here: edit uGUI **through the bridge** (it dirties correctly and can't corrupt fileIDs), keep it in the prefab, and watch the two gotchas below.

## The one rule
**Author and style uGUI in the prefab; use C# only for behaviour and dynamic values.** Hierarchy, anchors, colours, sprites, fonts, layout live in the `.prefab` (set via the CLI or YAML). C# is for wiring `onClick`, pushing a value into a label, animating a fill — never `AddComponent<Image>()`, runtime `new GameObject(...)` UI trees, or `image.color = …` theming. Runtime-built / play-mode-edited uGUI **evaporates on stop**, is invisible to diffs / `INSPECTOR` / `SCREENSHOT`, and thrashes Canvas + `LayoutRebuilder`. `STOP` before authoring edits — treat play mode as read-only for layout/style.

## Match the game's existing style — copy a nearby panel/button/toggle first
Before authoring a new panel, button, or toggle, **inspect an existing one nearby and reuse its setup** so the new UI inherits the game's look instead of drifting off-style. `UI_DISCOVER` (alias for `ASSET_DISCOVER ui`) lists the existing UI prefabs; `INSPECTOR Assets/UI/Existing.prefab --children` shows the exact sprite GUIDs, `m_Color`s, TMP font asset, `RectTransform` sizing, and `LayoutGroup`/`LayoutElement` config to copy. Reuse the same sprites/fonts/colours/corner-treatment and component layout rather than inventing new ones — a one-off button with a different sprite, font, or padding reads as "bolted on." When several panels already share a look, those values **are** the de-facto design tokens: match them. (If the project has a shared button/panel **prefab or variant**, instantiate that — `PREFAB_INSTANTIATE` — instead of rebuilding it.)

## Two gotchas that bite
- **Decorative children block clicks.** All `Image` and `TMP_Text` default to `raycastTarget = true`; a background panel or icon over a `Button` swallows the click. Set `raycastTarget = false` on every decorative/non-interactive graphic. (`Transform.Find` also breaks on names containing `[]` — avoid special characters in UI object names.)
- **Overflowing labels wrap to vertical text under a layout group.** Wide nav buttons (e.g. 897px) with left-aligned text look evenly spaced only because each box dwarfs its label. Drop them into a `HorizontalLayoutGroup` with `childControlWidth` + a `LayoutElement.preferredWidth` sized to the *spacing*, and the now-narrow box forces the TMP to word-wrap — one letter per line, reading as vertical text. Fix: set each label `tmp.textWrappingMode = TextWrappingModes.NoWrap` (+ `overflowMode = Overflow`). Confirm with `tmp.preferredWidth` vs `rect.width` — if preferred > width and wrap is on, it stacks.

## Touch controls — size & place by physical distance, not pixels
Any on-screen **input** (driving wheel/pedals, twin sticks, jump/fire buttons, D-pad) is ergonomics, not decoration — a control that's correct in pixels can be untappable on a dense phone or unreachable for a thumb. Author it as a prefab like everything else, but drive its size/margins from **physical distance via `Screen.dpi`**, not raw pixels.
- **Minimum tap target ≈ 9 mm** (Apple HIG ~44 pt ≈ 7 mm, Material ~48 dp ≈ 9 mm). Convert: `px = mm / 25.4f * Screen.dpi`. A 100 px button is ~13 mm on a 200-dpi tablet but ~5 mm (unhittable) on a 500-dpi phone — size by mm and let it resolve per device. `Screen.dpi` can read 0 on some devices; fall back to a sane default (~160).
- **Thumb-reach zones.** Hold-with-two-hands gameplay reaches with thumbs from the bottom corners — keep primary controls in the lower-left / lower-right arcs; never put a frequently-pressed control top-center. Anchor them to the corners (`anchorMin/Max` in that corner) so they track any aspect ratio, and offset inward by a mm-based margin, not a fixed pixel pad.
- **Spacing & safe area.** Separate adjacent touch targets by ≥ ~2 mm of dead space (fewer mis-taps). Inset from notches/rounded corners/home indicator using `Screen.safeArea` (a `CanvasScaler`/anchor in raw pixels ignores the cutout).
- **Verify on phone *and* tablet, show both.** Don't sign off on one aspect — render at least one phone (e.g. `GAMEVIEW 2556x1179`) and one tablet (e.g. `GAMEVIEW 2732x2048`) resolution with `SCREENSHOT gameview --output <per-device.png>`, then present both screenshots back to the user. See `clibridge4unity-screenshot` for the device table and why `--output` is required.
- `CanvasScaler` "Scale With Screen Size" handles *resolution* but **not DPI/physical size** — two phones at the same resolution but different physical size scale identically yet feel different. For input ergonomics, size the critical controls in mm in code on top of the scaler.

## Edit + verify through the bridge
```bash
# inspect first — exact m_FieldNames + current values
clibridge4unity INSPECTOR Assets/UI/MainMenu.prefab --children
clibridge4unity FIND prefab:Assets/UI/MainMenu.prefab/PlayButton,Panel    # comma = OR

# look / text  (COMPONENT_SET parses "x,y[,z]" vectors and #hex / named colors — see -components)
clibridge4unity COMPONENT_SET Canvas/Panel/PlayButton Image m_Color "#3A7BD5"
clibridge4unity COMPONENT_SET Canvas/Panel/Icon       Image m_RaycastTarget false
clibridge4unity COMPONENT_SET Canvas/Panel/Title TextMeshProUGUI m_text "Start Game"

# layout — configure the group in the prefab, let it drive children (driven fields ignore hand-sets)
clibridge4unity COMPONENT_SET Canvas/Panel RectTransform anchoredPosition "0,40"
clibridge4unity COMPONENT_ADD Canvas/Panel/Row HorizontalLayoutGroup
clibridge4unity COMPONENT_ADD Canvas/Panel/Row/Cell LayoutElement

# save a scene edit back to the asset, then verify without entering play mode
clibridge4unity PREFAB_SAVE Canvas Assets/UI/MainMenu.prefab
clibridge4unity SCREENSHOT Assets/UI/MainMenu.prefab
```

## CLI vs YAML vs code
- **CLI (`COMPONENT_*` / `PREFAB_*` / `INSPECTOR`)** for anything Unity-API-shaped — the default; it can't corrupt fileIDs and propagates to instances.
- **YAML** only for a flat value sweep across many existing nodes (one hex → another, a font/sprite GUID swap); never invent fileIDs or restructure by hand. After: `ASSET_RESERIALIZE` (alias `REIMPORT`) + `SCREENSHOT` + `LOG errors`.
- **Code** (`CODE_EXEC` over `PrefabUtility.LoadPrefabContents`) only when the edit needs construction logic (wire a persistent `UnityEvent`, build a subtree). Compile a new component type before `AddComponent<T>` it; make the edit idempotent. See `clibridge4unity-prefab-workflow`.

## Related
- `clibridge4unity-prefab-workflow` — asset vs instance vs prefab-mode; `LoadPrefabContents` for code-driven edits; override/variant propagation
- `clibridge4unity-components` — `COMPONENT_SET`/`ADD`/`REMOVE`, plain-args vs JSON, vector/color parsing
- `clibridge4unity-prefab` — `PREFAB_CREATE`/`INSTANTIATE`/`SAVE`, `INSPECTOR` on prefab assets
- `clibridge4unity-screenshot` — `SCREENSHOT` prefab/gameview to verify visually
- `clibridge4unity-ui-toolkit` / `clibridge4unity-ui` — the *other* UI system (`.uxml`/`.uss`), not Canvas-based uGUI
