---
name: clibridge4unity-ui-toolkit
description: Use for any runtime or editor UI work with UI Toolkit (USS+UXML) — building VisualElement trees, styling with USS, custom UxmlElement controls, data binding, layout, the `UIDocument` runtime workflow, retained-mode panels, USS variables/themes. Auto-trigger on `.uxml`, `.uss`, `UIDocument`, `VisualElement`, `rootVisualElement`, `Q<T>()`, `CloneTree`, `styleSheets.Add`, `style.X = ...` (often anti-pattern), `EnableInClassList`, `RegisterCallback<TEvent>`, `RegisterValueChangedCallback`, `schedule.Execute(...).Every(...)`, `GeometryChangedEvent`, `TransitionEndEvent`, `[UxmlElement]`, `BaseField<T>`, controller-split patterns, hidden-via-USS-class show/hide. UI Toolkit's pitfall is doing in C# what USS would do better — and the rules below enforce the right boundary.
---

# Unity UI Toolkit (USS + UXML)

Apply standard UI Toolkit knowledge (USS-first vs inline `style.X`, `EnableInClassList` for state, `GeometryChangedEvent` over per-frame polling, `schedule.Execute().Every()` over `EditorApplication.update`, USS `transition` over hand-rolled lerps, panel-space pointer coords, `[UxmlElement]` custom controls). This project's house conventions on top of that:

- **`.hidden { display: none; }`** in shared USS + `EnableInClassList("hidden", ...)` is the standard show/hide; same pattern for `.collapsed`, `.invisible`.
- **Stylesheet import order in UXML** (`<Style src="...">`): shared tokens first, panel-specific second, custom-control sheets last. The UXML is the source of truth for what a panel has loaded — check it first when a class isn't taking effect.
- **Controller-split for complex windows:** main window owns lifecycle + composition; each major UXML subtree gets a Controller class taking `(VisualElement root, IDataSource data)` in its ctor.
- **Tracked subscriptions:** wrap `RegisterCallback`/`clicked` in a helper that pushes an unwire to a `_cleanups` list; iterate it in `Dispose`/`OnDestroy`. (See `UXMLController` base in this project.)
- **Sanctioned inline-C# exceptions — annotate the reason in-source:** square/aspect-ratio (`GeometryChangedEvent` → `style.height = resolvedStyle.width`, drop when USS ships `aspect-ratio`); mobile safe-area inset (`Screen.safeArea` polled in `Update` — no UI Toolkit event fires); mid-animation `TransitionEndEvent` re-enable.

## Related
- `clibridge4unity-editor-tools` — UI Toolkit for EditorWindow/CustomEditor/PropertyDrawer
- `clibridge4unity-icons` — icon assets + `-unity-background-image-tint-color` theming
- `clibridge4unity-ui` — bridge `UI_DISCOVER` + `SCREENSHOT Assets/foo.uxml` for live iteration
- `clibridge4unity-performance` — element perf (`EnableInClassList` over `style.X`)
