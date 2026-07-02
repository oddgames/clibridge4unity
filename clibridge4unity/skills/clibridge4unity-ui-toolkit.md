---
name: clibridge4unity-ui-toolkit
description: Use for any runtime or editor UI work with UI Toolkit (USS+UXML) — building VisualElement trees, styling with USS, custom UxmlElement controls, data binding, layout, the `UIDocument` runtime workflow, retained-mode panels, USS variables/themes. Auto-trigger on `.uxml`, `.uss`, `UIDocument`, `VisualElement`, `rootVisualElement`, `Q<T>()`, `CloneTree`, `styleSheets.Add`, `style.X = ...` (often anti-pattern), `EnableInClassList`, `RegisterCallback<TEvent>`, `RegisterValueChangedCallback`, `schedule.Execute(...).Every(...)`, `GeometryChangedEvent`, `TransitionEndEvent`, `[UxmlElement]`, `BaseField<T>`, controller-split patterns, hidden-via-USS-class show/hide. UI Toolkit's pitfall is doing in C# what USS would do better — and the rules below enforce the right boundary.
---

# Unity UI Toolkit (USS + UXML)

Apply standard UI Toolkit knowledge (USS-first vs inline `style.X`, `EnableInClassList` for state, `GeometryChangedEvent` over per-frame polling, `schedule.Execute().Every()` over `EditorApplication.update`, USS `transition` over hand-rolled lerps, panel-space pointer coords, `[UxmlElement]` custom controls). This project's house conventions on top of that:

- **`.hidden { display: none; }`** in shared USS + `EnableInClassList("hidden", ...)` is the standard show/hide; same pattern for `.collapsed`, `.invisible`.
- **Stylesheet import order in UXML** (`<Style src="...">`): shared tokens first, panel-specific second, custom-control sheets last. The UXML is the source of truth for what a panel has loaded — check it first when a class isn't taking effect.
- **Reuse the game's shared USS — don't invent a parallel style.** A new panel only inherits the game's look if it imports the **same shared sheets and reuses the same class names** the rest of the UI uses. Before writing any USS, find them: `Grep '<Style src=' Assets/**/*.uxml` to see which `.uss` the existing panels import — the file(s) that show up across many UXMLs are the shared token/theme sheet (colour vars, fonts, spacing, button/panel classes). Import those **first** in the new UXML, then style with the existing classes (`EnableInClassList("primary-button", …)`), adding panel-specific USS only for what's genuinely new. `UI_DISCOVER` inventories the UI assets; `CODE_ANALYZE`/`Grep` finds which class lives where. Writing a fresh `.uss` from scratch when a shared one exists is the main way game style fails to come through.
- **Controller-split for complex windows:** main window owns lifecycle + composition; each major UXML subtree gets a Controller class taking `(VisualElement root, IDataSource data)` in its ctor.
- **Tracked subscriptions:** wrap `RegisterCallback`/`clicked` in a helper that pushes an unwire to a `_cleanups` list; iterate it in `Dispose`/`OnDestroy`. (See `UXMLController` base in this project.)
- **Sanctioned inline-C# exceptions — annotate the reason in-source:** mobile safe-area inset (`Screen.safeArea` polled in `Update` — still no native UI Toolkit support as of 6.4); mid-animation `TransitionEndEvent` re-enable. Aspect ratio is **no longer** an exception on Unity 6.3+ — USS ships `aspect-ratio` (float or `auto`); use it and delete any `GeometryChangedEvent` → `style.height = resolvedStyle.width` hack (pre-6.3 only).

## Touch controls — size by physical mm, not px
UI Toolkit `px` are panel pixels, not physical — on-screen input (wheel/pedals, sticks, jump/fire, D-pad) must be sized in mm via `Screen.dpi` (`px = mm / 25.4f * Screen.dpi`, fall back to ~160 when dpi reads 0), set as `style.width/height/minWidth` in code. A **sanctioned inline-C# exception** (no USS unit maps to mm) — annotate in-source like the safe-area/aspect-ratio cases above. `PanelSettings` scale modes handle resolution, not physical size.
- **Tap targets ≥ 9 mm** (Apple ~44 pt, Material ~48 dp); ≥ 2 mm dead space between adjacent targets.
- **Thumb zones:** `position: absolute` + `bottom`/`left`/`right` in the lower corners — never top-center. Inset the container by `Screen.safeArea` (the project's polled-in-`Update` exception).
- **Verify on phone AND tablet, show both:** `GAMEVIEW 2556x1179` then `2732x2048`, each `SCREENSHOT gameview --output <per-device.png>`, present both screenshots to the user. Static UXML: set root size per device and `SCREENSHOT Assets/UI/Foo.uxml --output …`. See `clibridge4unity-screenshot`.

## Related
- `clibridge4unity-editor-tools` — UI Toolkit for EditorWindow/CustomEditor/PropertyDrawer
- `clibridge4unity-icons` — icon assets + `-unity-background-image-tint-color` theming
- `clibridge4unity-ui` — bridge `UI_DISCOVER` + `SCREENSHOT Assets/foo.uxml` for live iteration
- `clibridge4unity-performance` — element perf (`EnableInClassList` over `style.X`)
