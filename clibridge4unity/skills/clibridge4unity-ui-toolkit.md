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

## Traps that pass code review and fail at runtime

- **`Q<T>()` walks the tree on every call, and returns `null` on a typo.** Resolve once when the panel is built, store the references, and never query inside an event handler, `schedule` tick or per-frame path. A mistyped name yields `null` rather than an error, so the `NullReferenceException` surfaces somewhere unrelated — assert at build time (`Debug.Assert(el != null, "#thing missing from Foo.uxml")`) so a renamed element fails where the mistake is.
- **`CloneTree` wraps the template in a `TemplateContainer`, and that wrapper is a real layout element.** In a flex parent it defaults to not growing, so a template that looks correct in isolation collapses when instantiated. Give the container the growth/size it needs (`flexGrow`, or a USS class on it) — the symptom is a panel that renders but measures zero.
- **Geometry is not valid on the frame you build the tree.** `resolvedStyle`, `layout`, `contentRect` all read zero until the first layout pass. Anything that needs a measured size belongs in `GeometryChangedEvent` — and that fires again on every resize, so make the handler idempotent, and unregister it if it was only needed once.
- **`UIDocument.rootVisualElement` is null before `OnEnable`,** and disabling the component tears the tree down. Cached `VisualElement` references do not survive that: re-resolve on enable rather than in `Awake`. Layering between documents is `UIDocument.sortingOrder`, not sibling order.
- **`opacity: 0` still receives pointer events** — an invisible element keeps swallowing clicks. `visibility: hidden` keeps its layout space but stops picking; `display: none` removes it from layout entirely. For "gone", prefer `display: none`; for click-through on something visible, set `pickingMode = PickingMode.Ignore`.
- **A `null` USS class name or a missing stylesheet fails silently.** If a class has no effect, check the panel actually imported the sheet (see stylesheet import order above) before suspecting the selector.

## Long lists — `ScrollView` materialises every child

`ScrollView` instantiates all of its content up front; `ListView` only builds the rows on screen. Fine for tens of items, not for hundreds — the cost shows up as a hitch when the panel opens, not while scrolling, so it is easy to misattribute.

- **`makeItem` creates, `bindItem` fills, and rows are recycled.** `bindItem(element, i)` must set *every* piece of state, including clearing what a previous binding left behind — classes added via `EnableInClassList`, text, visibility. The signature bug is a row showing the wrong selected/highlighted state after scrolling, because only the "on" path was written.
- **Register callbacks in `makeItem`, not `bindItem`.** `makeItem` runs once per recycled row; `bindItem` runs on every scroll. Subscribing there stacks handlers up until one click fires many times, unless `unbindItem` removes them.
- **`Rebuild()` vs `RefreshItems()`.** `RefreshItems()` re-binds existing rows — use it when the data changed. `Rebuild()` throws the rows away and remakes them — only needed when `itemsSource`, `makeItem` or the item height changed. Reaching for `Rebuild()` on every data change is a common and avoidable cost.
- **`virtualizationMethod`:** `FixedHeight` (with `fixedItemHeight` set) is the fast path; `DynamicHeight` measures every item and is markedly slower. If rows really are uniform, say so.

## Wiring buttons

- **`clicked` vs `RegisterCallback<ClickEvent>`.** `Button.clicked` is the plain "it was activated" hook and also fires on keyboard submit when the button has focus — prefer it for ordinary buttons. Use `ClickEvent` only when you need the event itself: modifiers, position, or propagation control.
- **Wiring runs again every time the panel is rebuilt.** `CloneTree` produces fresh elements, but a wire-up method called twice on the *same* element double-subscribes, and the handler then fires twice per click. Either unsubscribe on teardown (see tracked subscriptions above) or assign rather than accumulate.
- **Events bubble.** A button inside a clickable row triggers the row handler too. `StopPropagation()` on the inner handler is what separates "open the row" from "delete the row" — without it the delete button also opens the thing it just deleted.
- **`SetEnabled(false)` is the correct way to make a control inert:** it blocks pointer events on the whole subtree and applies the `:disabled` pseudo-class for styling. Hiding or greying out visually does neither, so a "disabled" button that only *looks* disabled is still clickable.
- **`PointerDownEvent` is not a click.** It fires on press without waiting for release, so it cannot be cancelled by dragging off the control. Use it for press-and-hold or drag starts, not for buttons.

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
