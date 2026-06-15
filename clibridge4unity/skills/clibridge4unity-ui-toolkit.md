---
name: clibridge4unity-ui-toolkit
description: Use for any runtime or editor UI work with UI Toolkit (USS+UXML) — building VisualElement trees, styling with USS, custom UxmlElement controls, data binding, layout, the `UIDocument` runtime workflow, retained-mode panels, USS variables/themes. Auto-trigger on `.uxml`, `.uss`, `UIDocument`, `VisualElement`, `rootVisualElement`, `Q<T>()`, `CloneTree`, `styleSheets.Add`, `style.X = ...` (often anti-pattern), `EnableInClassList`, `RegisterCallback<TEvent>`, `RegisterValueChangedCallback`, `schedule.Execute(...).Every(...)`, `GeometryChangedEvent`, `TransitionEndEvent`, `[UxmlElement]`, `BaseField<T>`, controller-split patterns, hidden-via-USS-class show/hide. UI Toolkit's pitfall is doing in C# what USS would do better — and the rules below enforce the right boundary.
---

# Unity UI Toolkit (USS + UXML)

UI Toolkit is Unity's retained-mode UI system (`.uxml` for structure, `.uss` for style, C# only for behaviour). It replaces uGUI for new editor windows and is the recommended path for runtime UI in new projects. Most production bugs come from people doing in C# what USS would have done better — inline `style.X` assignments duplicating a class, imperative tree-building when UXML templates would do, per-frame layout polling when `GeometryChangedEvent` would fire once. This skill is the boundary rules.

## The one rule that prevents most of these

**USS first, then a custom `[UxmlElement]`, then inline C# last resort.** Structure in UXML, visual rules in USS, behaviour in C#. Inline `style.color = ...` / `style.display = ...` / `style.backgroundColor = ...` is almost always a USS class doing it better. Imperative `parent.Add(new VisualElement())` loops are almost always a UXML template + `CloneTree`. C# is for events, async, and outside-world wiring — not for what to show or how to style it.

## Pitfall catalog

### 1. Inline `style.X` assignments instead of USS classes
`element.style.backgroundColor = new StyleColor(Color.red);` in C# does the same thing as `.danger { background-color: red; }` in USS + `element.AddToClassList("danger")` — except the C# version locks the colour to one value, doesn't theme, and can't be tweaked at edit time without recompiling.
- **Rule:** if you'd write a CSS class for this in a web project, write a USS class. `EnableInClassList("danger", isDanger)` to toggle. Inline `style.X` only for genuinely dynamic per-element values (a progress-bar's width as a percentage, an animated transform).

### 2. `style.display = DisplayStyle.None` / `Flex` for show/hide → use a `.hidden` class
Manually toggling `display` works but skips USS's animation/transition system and bypasses the class system that the rest of the file uses.
- **Real convention:** `.hidden { display: none; }` in your shared USS + `element.EnableInClassList("hidden", isHidden)`. Same for any other state-driven visibility (`.collapsed`, `.invisible`).
- **Rule:** state-driven visibility → toggle a class, not a style property. Lets the inspector / runtime debugger see the state, lets you add transitions later, keeps C# clean.

### 3. Imperative tree-building (`new VisualElement(); parent.Add(...)` loops) instead of UXML
Code that builds a 30-element tree with `new VisualElement { name = ..., AddToClassList(...) }` and stacks `parent.Add(child)` is just a UXML template written in C#. Hard to read, no preview, can't be edited by a designer.
- **Rule:** the tree's *shape* belongs in `.uxml`. Use `<Template>` / `<Instance>` for repeated subtrees. The C# clones the tree, queries elements by name (`Q<T>("name")`), and wires behaviour. If you really need dynamic shape (a list of N items from data), use `ListView` / `MultiColumnListView` or `CloneTree` an item template per row.

### 4. Per-frame layout polling in `Update` → use `GeometryChangedEvent`
"I need to react when this element resizes" → many people add `void Update() { if (panel.resolvedStyle.width != _lastWidth) Recompute(); }`. Wrong: runs every frame; fires once on actual change.
- **Rule:** `element.RegisterCallback<GeometryChangedEvent>(evt => { /* width/height changed */ });`. Fires only on real layout change. For first-layout one-shot, register a callback that unregisters itself after the first event.

### 5. `schedule.Execute(...).Every(ms)` for periodic UI updates — NEVER `EditorApplication.update`
UI Toolkit elements have a `schedule` member that ticks reliably as long as the element is attached to a visible panel. `EditorApplication.update` only fires when Unity has focus.
- **Rule:** runtime / editor UI Toolkit code uses `element.schedule.Execute(callback).Every(ms)` or `.StartingIn(ms)`. The scheduled item dies with the element — no manual cleanup needed. Capture the return value if you need to cancel early.

### 6. Animation via `schedule.Execute(...).Every(16)` lerps → use USS `transition`
A custom 60Hz schedule that interpolates `style.width` over time is fighting the engine. USS has `transition-property: width; transition-duration: 200ms;` built in — set the target style value once, the transition runs, `TransitionEndEvent` fires.
- **Rule:** animations → USS `transition-*` + a single style write to the target value. Listen for `TransitionEndEvent` if you need to react to completion. Custom-tick animations are for one-off effects USS can't express (physics-based bouncy springs, randomised noise).

### 7. Stylesheets must be added to the panel — adding a class without loading the sheet does nothing
A USS rule only applies if its sheet is in the panel's `styleSheets` list (or imported transitively via `<Style src="..." />` in the UXML). Adding a class whose sheet isn't loaded → invisible element.
- **Real convention:** stylesheets are imported at the top of the UXML file in dependency order: shared tokens first, panel-specific second, custom-control sheets last. The UXML is the source of truth for what styles a panel has.
- **Rule:** if a class isn't taking effect, check the UXML's `<Style src="...">` list FIRST. The most common cause of "the style is there but the element looks wrong" is "the stylesheet isn't loaded."

### 8. `Q<T>("name")` returns null silently — guard or assert
`var btn = root.Q<Button>("save");` returns null if no element named "save" exists. Subsequent `btn.text = "Save"` throws `NullReferenceException` with no hint about the missing element.
- **Rule:** in dev builds, assert on each `Q<T>` (`Debug.Assert(btn != null, "missing #save button in MyWindow.uxml")`) so a missing element fails loudly at startup, not later. In release, fall back gracefully if the element is optional.

### 9. UI Toolkit pointer events use panel coordinates, not screen pixels
`PointerDownEvent.position` is in panel-local space (origin top-left, +Y down). `Mouse.current.position.ReadValue()` is screen pixels (origin bottom-left, +Y up). Mixing produces upside-down or off-by-N-pixel hit detection.
- **Rule:** within UI Toolkit, always use the panel-local event coords (`evt.position`, `evt.localPosition`). Convert to screen / world only at the boundary (e.g. when raycasting from a UI button into the scene).

### 10. Custom `[UxmlElement]` controls when USS can't express the rule
Unity 6's USS still lacks container queries, `clamp()`, `vw`/`em`, `aspect-ratio` (full support pending), and a few other CSS niceties. Repeated USS workarounds (manual aspect-ratio via `GeometryChangedEvent`, JS-style polling for safe-area inset) are good candidates for promotion to a custom `[UxmlElement]`.
- **Real convention:** create a custom control when the same C# scaffolding appears in 2+ places AND it can't be expressed in USS. Example: a `SquareAspectElement` that listens to its own `GeometryChangedEvent` and sets `style.height = resolvedStyle.width`. Drop the manual `RegisterCallback` from each caller; use the element instead.
- **Rule:** before adding inline C# layout logic for the third time, extract to a custom UxmlElement. Annotate each sanctioned exception in-source ("until Unity 6.3 LTS ships `aspect-ratio`, this control polyfills via GeometryChangedEvent").

## Workflow

1. **Sketch in UXML first.** Build the structure as static UXML — nest VisualElements, give them names and classes. Open in UI Builder if a designer needs to tweak.
2. **Style in USS.** Add classes for every visual state. Use USS variables (`--accent-color: ...`) for theming. Keep shared tokens in one root stylesheet, panel-specific styles in a sibling sheet.
3. **C# for behaviour only.** `Q<T>("...")` to grab elements, `RegisterCallback` for events, wire to your data layer. No `style.X = ...` unless you've checked USS can't do it.
4. **Controller-split for complex windows.** Main window owns lifecycle + composition; each major UXML subtree gets its own Controller class that handles wiring + state for that subtree. Each Controller takes `(VisualElement root, IDataSource data)` in its ctor.
5. **Tracked subscriptions.** Wrap `RegisterCallback` + `clicked` subscriptions in a helper that adds an unwire to a list; iterate the list in `OnDestroy` so the panel cleans up perfectly when the window closes. Forgetting one leaves a leaked subscription pointing at a destroyed element.
6. **Test by toggling state classes manually.** Use the Visual Element Inspector (Window > UI Toolkit > Debugger) to add/remove classes at runtime and verify each state renders correctly without recompiling.

## Quick reference — minimal EditorWindow with UXML/USS

```csharp
public class MyWindow : EditorWindow
{
    private const string UxmlPath = "Packages/com.foo.tools/Editor/UI/MyWindow.uxml";

    [MenuItem("Tools/My Window")]
    public static void Open() => GetWindow<MyWindow>("My Window");

    public void CreateGUI()
    {
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
        uxml.CloneTree(rootVisualElement); // USS imports come from <Style src="..."/> in the UXML

        var saveBtn = rootVisualElement.Q<Button>("save");
        Debug.Assert(saveBtn != null, "missing #save in MyWindow.uxml");
        saveBtn.clicked += Save;

        rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => Layout());
    }

    private void Save() { /* … */ }
    private void Layout() { /* recompute when window resizes */ }
}
```

## Quick reference — UXML with stylesheet imports

```xml
<UXML xmlns="UnityEngine.UIElements">
  <Style src="GameStyle.uss" />              <!-- shared tokens: fonts, colours, .hidden -->
  <Style src="MyWindow.uss" />               <!-- this window's local rules -->
  <Style src="PromptTypeToggle.uss" />       <!-- a custom control's sheet -->

  <VisualElement name="root" class="my-window">
    <Label text="Hello" class="title" />
    <Button name="save" text="Save" />
  </VisualElement>
</UXML>
```

## Quick reference — tracked subscription helper

```csharp
public abstract class UXMLController
{
    private readonly List<Action> _cleanups = new();

    protected void WireClick(Button b, Action cb)
    {
        if (b == null) return;
        b.clicked += cb;
        _cleanups.Add(() => b.clicked -= cb);
    }

    protected void WireCallback<TEvt>(VisualElement el, EventCallback<TEvt> cb) where TEvt : EventBase<TEvt>, new()
    {
        if (el == null) return;
        el.RegisterCallback(cb);
        _cleanups.Add(() => el.UnregisterCallback(cb));
    }

    public virtual void Dispose()
    {
        for (int i = _cleanups.Count - 1; i >= 0; i--) _cleanups[i]();
        _cleanups.Clear();
    }
}
```

## Sanctioned USS-first exceptions (annotate in-source)

- **Square / aspect-ratio:** `GeometryChangedEvent` → `style.height = resolvedStyle.width`. Drop once Unity ships full `aspect-ratio` USS.
- **Safe-area inset on mobile** (UI Toolkit fires no event for notch/home-indicator changes; `Screen.safeArea` polling in `Update` is the only option).
- **Mid-animation interaction**: `TransitionEndEvent` listener that re-enables a button or fires a callback.

Each of these uses inline C# *because* USS can't express it. Comment the sanctioned reason — otherwise the next reviewer will (rightfully) flag it as an anti-pattern.

## Related
- `clibridge4unity-editor-tools` — UI Toolkit for `EditorWindow` / `CustomEditor` (PropertyDrawer with UI Toolkit is the modern path)
- `clibridge4unity-icons` — icon assets + `-unity-background-image-tint-color` for theming
- `clibridge4unity-ui` — bridge `UI_DISCOVER` + `SCREENSHOT Assets/foo.uxml` for live UI iteration
- `clibridge4unity-performance` — `MaterialPropertyBlock`-equivalent for elements is `EnableInClassList` over `style.X = ...`
