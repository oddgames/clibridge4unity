# UI Toolkit Reference — Contents Index
## For LLM-assisted UXML/USS code generation (Unity 6000.3+)

Total: ~10,700 lines across 8 reference files.

---

## Quick Lookup: "I need to..."

| Task | Read this file | Section |
|------|---------------|---------|
| **Know what USS properties exist** | `uss-reference.md` | Full file (139 lines) |
| **Know what UXML elements exist** | `uxml-elements-full.md` | Full file — 67 elements with all attributes |
| **See UXML element attributes** | `uxml-elements-full.md` | Element name as H3 header |
| **Convert HTML div/span to UXML** | `uxml-reference.md` | "Common Attributes", "Runtime Elements" |
| **Write flex layouts** | `uitoolkit-advanced-guide.md` | "Layouts" → "Responsive layouts: Flexbox" (line ~471) |
| **Position elements (absolute/relative)** | `uitoolkit-advanced-guide.md` | "Positioning visual elements" (line ~510) |
| **Size elements (width/height/flex)** | `uitoolkit-advanced-guide.md` | "Size settings" (line ~552), "Flex settings" (line ~564) |
| **Align elements** | `uitoolkit-advanced-guide.md` | "Align settings" (line ~625) |
| **Style with USS selectors** | `uitoolkit-advanced-guide.md` | "Styling" → "USS selectors" (line ~749) |
| **Use USS variables** | `uitoolkit-advanced-guide.md` | "USS variables" (line ~893) |
| **Animate with transitions** | `uitoolkit-advanced-guide.md` | "USS transitions animations" (line ~909) |
| **Swap themes/styles at runtime** | `uitoolkit-advanced-guide.md` | "Swapping styles on demand" (line ~968), "Themes" (line ~993) |
| **Use BEM naming conventions** | `uitoolkit-advanced-guide.md` | "Naming conventions" (line ~1030) |
| **Handle fonts and rich text** | `uitoolkit-advanced-guide.md` | "Text" (line ~1126) |
| **Use emoji/sprite assets** | `uitoolkit-advanced-guide.md` | "Sprite asset and emojis" (line ~1245) |
| **Set up data binding** | `uitoolkit-advanced-guide.md` | "Data binding" (line ~1326) |
| **Bind a ListView** | `uitoolkit-advanced-guide.md` | "Example: Binding a list to a ListView" (line ~1541+) |
| **Create custom controls** | `uitoolkit-advanced-guide.md` | "Custom controls" — UxmlElement (line ~120 section) |
| **Optimize UI performance** | `uitoolkit-advanced-guide.md` | "Optimizing performance" (line ~130 section) |
| **Hide/show elements efficiently** | `uitoolkit-docs-extra.md` | "8.6 Best Practices for Managing Elements" |
| **Use Template/Instance** | `uxml-reference.md` | "Templates (Reusable UXML Fragments)" |
| **Encapsulate UXML with C# logic** | `uxml-reference.md` | "Reusable Custom Elements" |
| **Handle events (click, hover, etc.)** | `uitoolkit-guide.md` | "Events dispatching" (~line 800+), "Event handling" |
| **Use manipulators (drag, resize)** | `uitoolkit-guide.md` | "Manipulators" section |
| **Query elements from C# (UQuery)** | `uitoolkit-docs-extra.md` | "8.2.6 UQuery - Finding Visual Elements" |
| **Set up runtime UI scene** | `uitoolkit-docs-extra.md` | "3.1 Getting Started with Runtime UI" |
| **Compare UI Toolkit vs uGUI** | `uitoolkit-docs-extra.md` | "1. Comparison of UI Systems" |
| **See real UXML/USS examples** | `uxml-examples.md` | Full file — 16 complete examples |
| **Build a ListView** | `uxml-examples.md` | Examples 2, 3, 4 |
| **Build a TabView** | `uxml-examples.md` | Example 7 |
| **Build a ScrollView** | `uxml-examples.md` | Example 6 |
| **Build custom slide toggle** | `uxml-examples.md` | Example 11 |
| **Build drag-and-drop UI** | `uxml-examples.md` | Example 5 |

---

## File Inventory

### `uss-reference.md` (139 lines) — USS PROPERTY BIBLE
The only file you need for "is this CSS property supported in USS?"
- **Supported**: dimensions, margin, padding, border, border-radius, flex container/item, position, display, overflow, background, 9-slice, text (font, alignment, outline, shadow), transform (rotate/scale/translate), transition, filter, opacity, visibility, CSS variables, pseudo-classes
- **NOT Supported**: grid, clip-path, box-shadow, backdrop-filter, text-transform, z-index, float, ::before/::after, linear-gradient (unreliable), radial-gradient, @media, @keyframes, line-height, gap, pointer-events

### `uxml-reference.md` (257 lines) — UXML QUICK REFERENCE
Compact reference for UXML syntax, document structure, and common patterns.
- Document structure and namespaces
- Common attributes table (name, class, focusable, tabindex, picking-mode, tooltip, etc.)
- All runtime elements organized by category (layout, text, buttons, input, numeric, sliders, selection, lists, media, progress)
- All editor-only elements
- Template/Instance syntax
- Rich text tags
- Reusable custom elements ([UxmlElement] pattern)
- Best practices summary
- What NOT to use in UXML

### `uxml-elements-full.md` (1,198 lines) — COMPLETE ELEMENT ATTRIBUTE TABLES
Every single UXML element with every attribute. 67 elements organized by category:
- **Base**: VisualElement, BindableElement, TextElement
- **Display**: Label, Image, HelpBox, ProgressBar
- **Containers**: Box, GroupBox, Foldout, ScrollView, TwoPaneSplitView, PopupWindow, TemplateContainer, IMGUIContainer
- **Tabs**: TabView, Tab
- **Buttons**: Button, RepeatButton
- **Toggles**: Toggle, RadioButton, RadioButtonGroup, ToggleButtonGroup
- **Text Input**: TextField (with all USS custom properties like --unity-cursor-color)
- **Numeric**: IntegerField, FloatField, DoubleField, LongField, UnsignedIntegerField, UnsignedLongField, Hash128Field
- **Sliders**: Slider, SliderInt, MinMaxSlider
- **Dropdowns**: DropdownField, EnumField, EnumFlagsField
- **Vector/Composite**: Vector2/3/4Field, Vector2/3IntField, RectField, RectIntField, BoundsField, BoundsIntField
- **Collections**: ListView, TreeView, MultiColumnListView, MultiColumnTreeView
- **Scroller**: Scroller
- **Editor-only**: ColorField, CurveField, GradientField, ObjectField, LayerField, MaskField, TagField, PropertyField, InspectorElement
- **Toolbar**: Toolbar, ToolbarButton, ToolbarToggle, ToolbarMenu, ToolbarSpacer, etc.

### `uxml-examples.md` (1,208 lines) — 16 COMPLETE WORKING EXAMPLES
Full UXML + USS + C# code for real UI patterns:
1. Relative and absolute positioning
2. List and tree views (ListView, TreeView, MultiColumn variants)
3. Complex ListView with custom visual elements
4. Runtime character selection UI (full game-like example)
5. Drag-and-drop between collection views
6. ScrollView content wrapping
7. Tabbed menu with TabView/Tab
8. Popup window
9. Toggle conditional UI
10. Custom control with two UxmlAttributes
11. Slide toggle custom control (BaseField<bool>)
12. Radial progress indicator (Mesh API)
13. Bindable custom control
14. Custom USS style properties (CustomStyleProperty<T>)
15. Aspect ratio custom control
16. Custom inventory property drawer with UxmlObject

### `uitoolkit-advanced-guide.md` (3,342 lines) — DEEP KNOWLEDGE (from 148-page PDF)
The most comprehensive file. Covers everything an advanced developer needs:
- **Layouts** (~300 lines): Flexbox deep-dive, Yoga layout engine, positioning, sizing, flex-grow/shrink/basis, alignment, margin/padding, background images, measurement units, templates
- **Styling** (~300 lines): USS selectors (create, assign, edit, override), variables, transitions with timing functions, runtime style swapping, theme system (ThemeStyleSheet)
- **Naming Conventions** (~100 lines): BEM methodology for USS classes
- **Text** (~200 lines): Font assets (SDF), font variants, rich text tags, gradients on text, sprite/emoji assets, Text Style Sheets
- **Data Binding** (~400 lines): Runtime binding, data sources, CreateProperty attribute, binding modes, health bar example, type converters, ListView binding, optimization (versioning, change tracking, update triggers)
- **Localization** (~200 lines): Setup, String Tables, Smart Strings, asset localization
- **Custom Controls** (~100 lines): UxmlElement, UxmlAttribute, slide toggle example
- **Performance** (~200 lines): Batching, vertex buffers, uber shader 8-texture limit, dynamic atlases, masking, animation tips, binding optimization, memory management, profiling tools, Unity 6 enhancements

### `uitoolkit-guide.md` (2,255 lines) — FULL DOCS HUB SCRAPE
Comprehensive web docs covering:
- UI systems comparison tables (UI Toolkit vs uGUI vs IMGUI)
- Architecture (retained mode, visual tree)
- Getting started (3 methods: UI Builder, UXML, C#)
- UXML format, namespaces, schema
- Templates (Template/Instance, AttributeOverrides, content containers)
- USS selectors (type, name, class, universal, descendant, child, compound, list)
- All 9 pseudo-classes with chaining rules
- USS custom properties/variables (declaration, defaults, limitations)
- Full USS property reference (~90 properties with inheritance/animatability)
- USS box model, flex, positioning, text details
- BEM best practices
- Apply styles from C# (inline, stylesheet, resolvedStyle)
- USS transitions (full syntax, timing functions, C# API)
- Layout engine (Flexbox patterns, absolute positioning)
- Events (trickle-down, bubble-up, picking mode)
- Event handling (RegisterCallback, value changes)
- All 17 event type categories
- Manipulators (6 classes: Clickable, Dragger, Resizer, etc.)
- UQuery (Q, Query, filtering, complex queries)
- Data binding overview
- Custom controls (UxmlElement, initialization, best practices)
- Runtime UI setup (4-step guide with code)
- Editor UI (EditorWindow, custom inspectors)
- 17 element detail pages with attributes

### `uitoolkit-docs-extra.md` (2,193 lines) — GAP-FILL REFERENCE
Pages not covered by other files:
- **UI system comparison** with detailed feature tables
- **UI Renderer** overview
- **Runtime UI** complete setup guide with code (Step 1-3)
- **World Space UI** setup
- **Editor UI** overview
- **Working with text** (styling options, 14 sub-page index)
- **Testing UI** tools
- **Migration guides** (uGUI, IMGUI, custom controls)
- **Structure UI deep-dive**: visual tree, coordinate systems, UXML syntax details, styles in UXML, templates/attribute overrides, loading UXML from C#, UQuery patterns
- **USS deep-dive**: selector types, pseudo-classes, transform properties (translate/rotate/scale with C#), transitions (timing functions, shorthand, events), masking (overflow, border-radius, SVG), BEM conventions
- **Events deep-dive**: dispatching phases, handling patterns, all 17 categories, focus order (tabIndex), manipulators
- **Best practices**: layouts (Flexbox/Yoga), styling (selectors/transitions/themes), custom controls (SlideToggle full example), performance (batching, vertex budgets, 8-texture limit, animation, binding, profiling)
- **Appendix**: ~70 additional sub-page URLs for further reference

---

## Coverage Verification

### From Best Practices Hub (bpg-uiad-index.html) — 13/13 topics ✅
All covered via `uitoolkit-advanced-guide.md` (PDF extraction) + `uitoolkit-docs-extra.md` (web scrape)

### From Main UIElements Hub (UIElements.html) — 14/14 topics ✅
| Topic | Covered in |
|-------|-----------|
| UI systems comparison | `uitoolkit-guide.md`, `uitoolkit-docs-extra.md` |
| Introduction | `uitoolkit-guide.md` |
| Get started | `uitoolkit-guide.md` |
| UI Builder | `uitoolkit-advanced-guide.md` |
| Structure UI | `uitoolkit-guide.md`, `uitoolkit-docs-extra.md` |
| Style UI (USS) | `uitoolkit-guide.md`, `uitoolkit-docs-extra.md`, `uss-reference.md` |
| Events | `uitoolkit-guide.md`, `uitoolkit-docs-extra.md` |
| UI Renderer | `uitoolkit-docs-extra.md` |
| Data binding | `uitoolkit-guide.md`, `uitoolkit-advanced-guide.md` |
| Editor UI | `uitoolkit-docs-extra.md` |
| Runtime UI | `uitoolkit-docs-extra.md` |
| Text | `uitoolkit-advanced-guide.md`, `uitoolkit-docs-extra.md` |
| Test UI | `uitoolkit-docs-extra.md` |
| Examples | `uxml-examples.md` |
| Migration | `uitoolkit-docs-extra.md` |

### Element Reference (UIE-ElementRef.html) — 67/67 elements ✅
All covered in `uxml-elements-full.md`
