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
- **Sanctioned inline-C# exceptions — annotate the reason in-source:** square/aspect-ratio (`GeometryChangedEvent` → `style.height = resolvedStyle.width`, drop when USS ships `aspect-ratio`); mobile safe-area inset (`Screen.safeArea` polled in `Update` — no UI Toolkit event fires); mid-animation `TransitionEndEvent` re-enable.

## Touch controls — size by physical distance, not px
On-screen **input** built in UI Toolkit (driving wheel/pedals, twin sticks, jump/fire buttons, D-pad) is ergonomics, not styling — `px` units don't account for screen density, so a control that looks right in the UXML preview can be untappable on a dense phone or out of thumb reach.
- **Minimum tap target ≈ 9 mm** (Apple HIG ~44 pt, Material ~48 dp). UI Toolkit `px` are panel pixels, not physical — convert in code from `Screen.dpi` (`px = mm / 25.4f * Screen.dpi`, fall back to ~160 when dpi reports 0) and set `style.width/height/minWidth` on the interactive element. This is a **sanctioned inline-C# exception** (no USS unit maps to mm) — annotate the reason in-source, same as the safe-area/aspect-ratio cases above.
- **Thumb-reach zones.** Anchor primary controls to the lower-left / lower-right with `position: absolute` + `bottom`/`left`/`right` so they track aspect ratio; keep frequently-pressed controls out of the top-center. Separate adjacent targets by ≥ ~2 mm dead space.
- **Safe area.** Inset the controls' container by `Screen.safeArea` (already the project's polled-in-`Update` exception) so cutouts/home-indicator don't overlap a button. A `PanelSettings` scale mode handles resolution, **not** physical size — size the critical input elements in mm regardless.
- **Verify on phone *and* tablet, show both.** Don't sign off on one aspect — render at least one phone (e.g. `GAMEVIEW 2556x1179`) and one tablet (e.g. `GAMEVIEW 2732x2048`) resolution with `SCREENSHOT gameview --output <per-device.png>`, then present both screenshots back to the user. For a static UXML, set the root size per device and `SCREENSHOT Assets/UI/Foo.uxml --output ...` each. See `clibridge4unity-screenshot` for the device table.

## Related
- `clibridge4unity-editor-tools` — UI Toolkit for EditorWindow/CustomEditor/PropertyDrawer
- `clibridge4unity-icons` — icon assets + `-unity-background-image-tint-color` theming
- `clibridge4unity-ui` — bridge `UI_DISCOVER` + `SCREENSHOT Assets/foo.uxml` for live iteration
- `clibridge4unity-performance` — element perf (`EnableInClassList` over `style.X`)
