# Unity UI Toolkit UXML Element Reference

Source: Unity 6 (6000.3) Documentation - Element Reference
Total elements: 67

---

## Common Inherited Attributes (VisualElement)

Most elements inherit these attributes from VisualElement. They are listed once here and referenced as "Inherits VisualElement attributes" in each element entry.

| Attribute | Type | Description |
|-----------|------|-------------|
| `name` | string | Element identifier for USS selectors |
| `focusable` | boolean | Controls whether element can receive focus |
| `tabindex` | int | Focus ring ordering (>=0); negative removes from tab navigation |
| `enabled` | boolean | Local enabled state |
| `style` | string | Inline USS style values |
| `tooltip` | string | Hover text (Editor UI only) |
| `picking-mode` | UIElements.PickingMode | Pointer event targeting (Position or Ignore) |
| `content-container` | string | Logical container for child elements |
| `data-source` | Object | Data source override for bindings |
| `data-source-path` | string | Path from data source to value |
| `data-source-type` | System.Type | Type hint for UI Builder |
| `language-direction` | UIElements.LanguageDirection | Text directionality (LTR/RTL) |
| `usage-hints` | UIElements.UsageHints | Performance optimization hints |
| `view-data-key` | string | Key for view data persistence |

---

## Base Elements

### VisualElement
**C#**: `UnityEngine.UIElements.VisualElement` (base: `Focusable`)
**USS**: `.unity-disabled`

The fundamental container element. All other elements inherit from this.

| Attribute | Type | Description |
|-----------|------|-------------|
| All VisualElement attributes listed above | | |

---

### BindableElement
**C#**: `UnityEngine.UIElements.BindableElement` (base: `VisualElement`)
**USS**: `.unity-disabled`

Base class for elements that can connect to a data source via binding.

| Attribute | Type | Description |
|-----------|------|-------------|
| `binding-path` | string | Path of the target property to be bound |
| *Inherits VisualElement attributes* | | |

---

### TextElement
**C#**: `UnityEngine.UIElements.TextElement` (base: `BindableElement`)
**USS**: `.unity-text-element`, `.unity-text-element__selectable`

Base class for text display elements.

| Attribute | Type | Description |
|-----------|------|-------------|
| `text` | string | Text to display |
| `display-tooltip-when-elided` | boolean | Show full text tooltip when elided |
| `double-click-selects-word` | boolean | Word selection on double-click |
| `emoji-fallback-support` | boolean | Emoji character rendering priority |
| `enable-rich-text` | boolean | Parse rich text tags |
| `parse-escape-sequences` | boolean | Transform escape sequences to characters |
| `selectable` | boolean | Whether text is selectable |
| `triple-click-selects-line` | boolean | Line selection on triple-click |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Display Elements

### Label
**C#**: `UnityEngine.UIElements.Label` (base: `TextElement`)
**USS**: `.unity-label`

Text display element for titles, descriptions, or other textual content.

| Attribute | Type | Description |
|-----------|------|-------------|
| `text` | string | Text to display |
| *Inherits TextElement attributes* | | |

---

### Image
**C#**: `UnityEngine.UIElements.Image` (base: `VisualElement`)
**USS**: `.unity-image`

Displays graphical assets (textures, sprites, render textures).

| Attribute | Type | Description |
|-----------|------|-------------|
| `scale-mode` | ScaleMode | How image scales to fit container |
| `tint-color` | Color | Color tint applied to image |
| `uv` | Rect | Base texture coordinates |
| *Inherits VisualElement attributes* | | |

**USS Custom Properties**: `--unity-image`, `--unity-image-size`, `--unity-image-tint-color`

---

### HelpBox
**C#**: `UnityEngine.UIElements.HelpBox` (base: `VisualElement`)
**USS**: `.unity-help-box`, `.unity-help-box__label`, `.unity-help-box__icon`, `.unity-help-box__icon--info`, `.unity-help-box__icon--warning`, `.unity-help-box__icon--error`

Displays messages with type icons (info, warning, error).

| Attribute | Type | Description |
|-----------|------|-------------|
| `message-type` | UIElements.HelpBoxMessageType | Message type (None, Info, Warning, Error) |
| `text` | string | Message text |
| *Inherits VisualElement attributes* | | |

---

### ProgressBar
**C#**: `UnityEngine.UIElements.ProgressBar` (base: `AbstractProgressBar`)
**USS**: `.unity-progress-bar`, `.unity-progress-bar__container`, `.unity-progress-bar__title`, `.unity-progress-bar__progress`, `.unity-progress-bar__background`

Displays progress of an ongoing task.

| Attribute | Type | Description |
|-----------|------|-------------|
| `high-value` | float | Maximum value |
| `low-value` | float | Minimum value |
| `title` | string | Title text displayed in center |
| `value` | float | Current progress value |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Container Elements

### Box
**C#**: `UnityEngine.UIElements.Box` (base: `VisualElement`)
**USS**: `.unity-box`

A VisualElement with default background, border color, and border width styling.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits VisualElement attributes only* | | |

---

### GroupBox
**C#**: `UnityEngine.UIElements.GroupBox` (base: `BindableElement`)
**USS**: `.unity-group-box`, `.unity-group-box__label`

Logically groups buttons, radio buttons, or toggles.

| Attribute | Type | Description |
|-----------|------|-------------|
| `text` | string | Title text on the group box |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### Foldout
**C#**: `UnityEngine.UIElements.Foldout` (base: `BindableElement`)
**USS**: `.unity-foldout`, `.unity-foldout__toggle`, `.unity-foldout__content`, `.unity-foldout__input`, `.unity-foldout__text`

Collapsible section with a toggle header.

| Attribute | Type | Description |
|-----------|------|-------------|
| `text` | string | Label text for the toggle |
| `toggle-on-label-click` | boolean | Toggle on label click |
| `value` | boolean | Open (true) or closed (false) |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### ScrollView
**C#**: `UnityEngine.UIElements.ScrollView` (base: `VisualElement`)
**USS**: `.unity-scroll-view`, `.unity-scroll-view__content-container`, `.unity-scroll-view__horizontal-scroller`, `.unity-scroll-view__vertical-scroller`

Scrollable container for content.

| Attribute | Type | Description |
|-----------|------|-------------|
| `mode` | UIElements.ScrollViewMode | Scroll direction: Vertical, Horizontal, or VerticalAndHorizontal |
| `horizontal-scroller-visibility` | UIElements.ScrollerVisibility | Horizontal scrollbar visibility |
| `vertical-scroller-visibility` | UIElements.ScrollerVisibility | Vertical scrollbar visibility |
| `elasticity` | float | Elasticity amount when scrolling past boundaries |
| `elastic-animation-interval-ms` | long | Min time between elastic animation executions |
| `scroll-deceleration-rate` | float | Rate scrolling slows after touch |
| `touch-scroll-type` | UIElements.ScrollView.TouchScrollBehavior | Behavior past boundaries via touch |
| `nested-interaction-kind` | UIElements.ScrollView.NestedInteractionKind | Nested ScrollView behavior |
| `horizontal-page-size` | float | Horizontal scroll speed |
| `vertical-page-size` | float | Vertical scroll speed |
| `mouse-wheel-scroll-size` | float | Mouse wheel scroll speed |
| *Inherits VisualElement attributes* | | |

---

### TwoPaneSplitView
**C#**: `UnityEngine.UIElements.TwoPaneSplitView` (base: `VisualElement`)
**USS**: (no unique USS class)

Two resizable panes separated by a draggable divider. Must have exactly two children.

| Attribute | Type | Description |
|-----------|------|-------------|
| `fixed-pane-index` | int | Which child is fixed (0 or 1) |
| `fixed-pane-initial-dimension` | float | Initial width/height of fixed pane |
| `orientation` | UIElements.TwoPaneSplitViewOrientation | Horizontal or Vertical split |
| *Inherits VisualElement attributes* | | |

---

### PopupWindow
**C#**: `UnityEngine.UIElements.PopupWindow` (base: `TextElement`)
**USS**: `.unity-popup-window`, `.unity-popup-window__content-container`

Visual container styled as a popup. No interactive logic -- purely visual.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits TextElement attributes* | | |

---

### TemplateContainer
**C#**: `UnityEngine.UIElements.TemplateContainer` (base: `BindableElement`)
**USS**: (no unique USS class)

Root element of a UXML file; acts as parent of all elements in the file.

| Attribute | Type | Description |
|-----------|------|-------------|
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### IMGUIContainer
**C#**: `UnityEngine.UIElements.IMGUIContainer` (base: `VisualElement`)
**USS**: `.unity-imgui-container`

Renders IMGUI code within UI Toolkit.

| Attribute | Type | Description |
|-----------|------|-------------|
| `onGUIHandler` | string | Handler function for IMGUI rendering |
| *Inherits VisualElement attributes* | | |

---

## Tab Elements

### TabView
**C#**: `UnityEngine.UIElements.TabView` (base: `VisualElement`)
**USS**: `.unity-tab-view`, `.unity-tab-view__content-container`, `.unity-tab-view__reorderable`, `.unity-tab-view__vertical`

Container for Tab elements, providing tab-based navigation.

| Attribute | Type | Description |
|-----------|------|-------------|
| `reorderable` | boolean | Allow tab drag reordering (default false) |
| *Inherits VisualElement attributes* | | |

---

### Tab
**C#**: `UnityEngine.UIElements.Tab` (base: `VisualElement`)
**USS**: `.unity-tab`, `.unity-tab__header`, `.unity-tab__header-label`, `.unity-tab__header-underline`, `.unity-tab__content-container`, `.unity-tab__close-button`

A single tab within a TabView.

| Attribute | Type | Description |
|-----------|------|-------------|
| `closeable` | boolean | Allow tab closing (default false) |
| `icon-image` | Object | Icon in tab header |
| `label` | string | Tab header text |
| *Inherits VisualElement attributes* | | |

---

## Button Elements

### Button
**C#**: `UnityEngine.UIElements.Button` (base: `TextElement`)
**USS**: `.unity-button`, `.unity-button--with-icon`, `.unity-button--with-icon-only`

Clickable button element.

| Attribute | Type | Description |
|-----------|------|-------------|
| `icon-image` | Object | Texture, Sprite, or VectorImage for icon |
| `text` | string | Button display text |
| *Inherits TextElement attributes* | | |

---

### RepeatButton
**C#**: `UnityEngine.UIElements.RepeatButton` (base: `TextElement`)
**USS**: `.unity-repeat-button`

Performs a repetitive action while held down. Requires C# for delay/interval logic.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits TextElement attributes* | | |

---

## Toggle / Boolean Elements

### Toggle
**C#**: `UnityEngine.UIElements.Toggle` (base: `BaseBoolField`)
**USS**: `.unity-toggle`, `.unity-toggle__label`, `.unity-toggle__input`, `.unity-toggle__checkmark`, `.unity-toggle__text`, `.unity-toggle--no-text`

Checkbox-style boolean toggle.

| Attribute | Type | Description |
|-----------|------|-------------|
| `label` | string | Label text |
| `text` | string | Optional text after the toggle |
| `toggle-on-label-click` | boolean | Toggle on label click |
| `value` | boolean | Current boolean value |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### RadioButton
**C#**: `UnityEngine.UIElements.RadioButton` (base: `BaseBoolField`)
**USS**: `.unity-radio-button`, `.unity-radio-button__checkmark-background`, `.unity-radio-button__checkmark`

Single selection within a group of RadioButtons.

| Attribute | Type | Description |
|-----------|------|-------------|
| `label` | string | Label text |
| `text` | string | Optional text after the button |
| `toggle-on-label-click` | boolean | Toggle on label click |
| `value` | boolean | Current boolean value |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### RadioButtonGroup
**C#**: `UnityEngine.UIElements.RadioButtonGroup` (base: `BaseField_1`)
**USS**: `.unity-radio-button-group`, `.unity-radio-button-group__container`

Group of radio buttons with single selection.

| Attribute | Type | Description |
|-----------|------|-------------|
| `choices` | IList | List of available choices |
| `label` | string | Label text |
| `value` | int | Selected index |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### ToggleButtonGroup
**C#**: `UnityEngine.UIElements.ToggleButtonGroup` (base: `BaseField_1`)
**USS**: `.unity-toggle-button-group`, `.unity-toggle-button-group__container`

Group of toggle buttons supporting single or multiple selection.

| Attribute | Type | Description |
|-----------|------|-------------|
| `allow-empty-selection` | boolean | Allow all unchecked |
| `is-multiple-selection` | boolean | Allow multi-select |
| `label` | string | Label text |
| `value` | UIElements.ToggleButtonGroupState | Current state |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Text Input Elements

### TextField
**C#**: `UnityEngine.UIElements.TextField` (base: `TextInputBaseField_1`)
**USS**: `.unity-text-field`, `.unity-text-field__label`, `.unity-text-field__input`, `.unity-base-text-field__input--placeholder`

Single or multiline text input.

| Attribute | Type | Description |
|-----------|------|-------------|
| `multiline` | boolean | Enable multiple lines |
| `value` | string | Current text value |
| `auto-correction` | boolean | Touch keyboard auto-correction |
| `hide-mobile-input` | boolean | Hide mobile input field |
| `hide-placeholder-on-focus` | boolean | Hide placeholder when focused |
| `is-delayed` | boolean | Update value only on Enter/blur |
| `keyboard-type` | TouchScreenKeyboardType | Mobile keyboard type |
| `label` | string | Label text |
| `mask-character` | System.Char | Password mask character |
| `max-length` | int | Maximum characters |
| `password` | boolean | Password mode |
| `placeholder-text` | string | Hint text |
| `readonly` | boolean | Read-only mode |
| `select-all-on-focus` | boolean | Select all on focus |
| `select-all-on-mouse-up` | boolean | Select all on first mouse up |
| `select-line-by-triple-click` | boolean | Triple-click line selection |
| `select-word-by-double-click` | boolean | Double-click word selection |
| `vertical-scroller-visibility` | ScrollerVisibility | Vertical scrollbar |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

**USS Custom Properties**: `--unity-cursor-color`, `--unity-selection-color`

---

## Numeric Fields

All numeric fields share a common attribute set inherited from TextValueField. Listed once here:

**Common Numeric Field Attributes:**

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | (varies) | Current numeric value |
| `label` | string | Label text |
| `is-delayed` | boolean | Update only on Enter/blur |
| `support-expressions` | boolean | Enable math expression evaluation |
| `readonly` | boolean | Read-only mode |
| `placeholder-text` | string | Hint text |
| `max-length` | int | Maximum characters |
| `password` | boolean | Password mode |
| `mask-character` | System.Char | Password mask character |
| `select-all-on-focus` | boolean | Select all on focus |
| `select-all-on-mouse-up` | boolean | Select all on first mouse up |
| `select-word-by-double-click` | boolean | Double-click word selection |
| `select-line-by-triple-click` | boolean | Triple-click line selection |
| `hide-placeholder-on-focus` | boolean | Hide placeholder when focused |
| `auto-correction` | boolean | Touch keyboard auto-correction |
| `hide-mobile-input` | boolean | Hide mobile input field |
| `keyboard-type` | TouchScreenKeyboardType | Mobile keyboard type |
| `emoji-fallback-support` | boolean | Emoji rendering priority |
| `vertical-scroller-visibility` | UIElements.ScrollerVisibility | Vertical scrollbar |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### IntegerField
**C#**: `UnityEngine.UIElements.IntegerField` (base: `TextValueField_1`)
**USS**: `.unity-integer-field`, `.unity-integer-field__label`, `.unity-integer-field__input`

| Attribute | Type |
|-----------|------|
| `value` | int |
| *Common Numeric Field Attributes* | |

---

### FloatField
**C#**: `UnityEngine.UIElements.FloatField` (base: `TextValueField_1`)
**USS**: `.unity-float-field`, `.unity-float-field__label`, `.unity-float-field__input`

| Attribute | Type |
|-----------|------|
| `value` | float |
| *Common Numeric Field Attributes* | |

---

### DoubleField
**C#**: `UnityEngine.UIElements.DoubleField` (base: `TextValueField_1`)
**USS**: `.unity-double-field`, `.unity-double-field__label`, `.unity-double-field__input`

| Attribute | Type |
|-----------|------|
| `value` | double |
| *Common Numeric Field Attributes* | |

---

### LongField
**C#**: `UnityEngine.UIElements.LongField` (base: `TextValueField_1`)
**USS**: `.unity-long-field`, `.unity-long-field__label`, `.unity-long-field__input`

| Attribute | Type |
|-----------|------|
| `value` | long |
| *Common Numeric Field Attributes* | |

---

### UnsignedIntegerField
**C#**: `UnityEngine.UIElements.UnsignedIntegerField` (base: `TextValueField_1`)
**USS**: `.unity-unsigned-integer-field`, `.unity-unsigned-integer-field__label`, `.unity-unsigned-integer-field__input`

32-bit unsigned integer (0 to 4,294,967,295).

| Attribute | Type |
|-----------|------|
| `value` | System.UInt32 |
| *Common Numeric Field Attributes* | |

---

### UnsignedLongField
**C#**: `UnityEngine.UIElements.UnsignedLongField` (base: `TextValueField_1`)
**USS**: `.unity-unsigned-long-field`, `.unity-unsigned-long-field__label`, `.unity-unsigned-long-field__input`

64-bit unsigned integer (0 to 18,446,744,073,709,551,615).

| Attribute | Type |
|-----------|------|
| `value` | System.UInt64 |
| *Common Numeric Field Attributes* | |

---

### Hash128Field
**C#**: `UnityEngine.UIElements.Hash128Field` (base: `TextInputBaseField_1`)
**USS**: `.unity-hash128-field`, `.unity-hash128-field__label`, `.unity-hash128-field__input`

Input for Hash128 values.

| Attribute | Type |
|-----------|------|
| `value` | Hash128 |
| `label` | string |
| `is-delayed` | boolean |
| `readonly` | boolean |
| `binding-path` | string |
| *Common text input attributes* | |

---

## Slider Elements

### Slider
**C#**: `UnityEngine.UIElements.Slider` (base: `BaseSlider_1`)
**USS**: `.unity-slider`, `.unity-base-slider--horizontal`, `.unity-base-slider--vertical`, `.unity-base-slider__dragger`

Float value slider.

| Attribute | Type | Description |
|-----------|------|-------------|
| `low-value` | float | Minimum value |
| `high-value` | float | Maximum value |
| `value` | float | Current value |
| `direction` | UIElements.SliderDirection | Horizontal or Vertical |
| `fill` | boolean | Enable fill color |
| `inverted` | boolean | Invert slider direction |
| `page-size` | float | Click-on-track scroll amount |
| `show-input-field` | boolean | Show numeric input field |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### SliderInt
**C#**: `UnityEngine.UIElements.SliderInt` (base: `BaseSlider_1`)
**USS**: `.unity-slider-int`

Integer value slider.

| Attribute | Type | Description |
|-----------|------|-------------|
| `low-value` | int | Minimum value |
| `high-value` | int | Maximum value |
| `value` | int | Current value |
| `direction` | SliderDirection | Horizontal or Vertical |
| `fill` | boolean | Enable fill color |
| `inverted` | boolean | Invert slider direction |
| `page-size` | float | Click-on-track scroll amount (cast to int) |
| `show-input-field` | boolean | Show numeric input field |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### MinMaxSlider
**C#**: `UnityEngine.UIElements.MinMaxSlider` (base: `BaseField_1`)
**USS**: `.unity-min-max-slider`, `.unity-min-max-slider__tracker`, `.unity-min-max-slider__dragger`, `.unity-min-max-slider__min-thumb`, `.unity-min-max-slider__max-thumb`

Range selection slider with min and max thumbs.

| Attribute | Type | Description |
|-----------|------|-------------|
| `low-limit` | float | Lower boundary |
| `high-limit` | float | Upper boundary |
| `value` | Vector2 | Current range (x=min, y=max) |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Dropdown / Selection Elements

### DropdownField
**C#**: `UnityEngine.UIElements.DropdownField` (base: `PopupField_1`)
**USS**: `.unity-popup-field`, `.unity-popup-field__label`, `.unity-popup-field__input`, `.unity-base-popup-field__text`, `.unity-base-popup-field__arrow`

Select one value from a dropdown list.

| Attribute | Type | Description |
|-----------|------|-------------|
| `choices` | IList | List of choices |
| `index` | int | Selected index |
| `value` | int | Selected value |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### EnumField
**C#**: `UnityEngine.UIElements.EnumField` (base: `BaseField_1`)
**USS**: `.unity-enum-field`, `.unity-enum-field__text`, `.unity-enum-field__arrow`, `.unity-enum-field__label`, `.unity-enum-field__input`

Select a single enum value.

| Attribute | Type | Description |
|-----------|------|-------------|
| `include-obsolete-values` | boolean | Include obsolete enum values |
| `value` | string | Current enum value |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### EnumFlagsField (Editor-only)
**C#**: `UnityEditor.UIElements.EnumFlagsField` (base: `BaseMaskField_1`)
**USS**: `.unity-enum-flags-field`, `.unity-enum-flags-field__label`, `.unity-enum-flags-field__input`

Multi-select from a [Flags] enum.

| Attribute | Type | Description |
|-----------|------|-------------|
| `include-obsolete-values` | boolean | Include obsolete enum values |
| `value` | string | Current flags value |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Vector / Composite Fields

### Vector2Field
**C#**: `UnityEngine.UIElements.Vector2Field` (base: `BaseCompositeField_3`)
**USS**: `.unity-vector2-field`, `.unity-vector2-field__label`, `.unity-vector2-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Vector2 | X, Y components |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### Vector3Field
**C#**: `UnityEngine.UIElements.Vector3Field` (base: `BaseCompositeField_3`)
**USS**: `.unity-vector3-field`, `.unity-vector3-field__label`, `.unity-vector3-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Vector3 | X, Y, Z components |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### Vector4Field
**C#**: `UnityEngine.UIElements.Vector4Field` (base: `BaseCompositeField_3`)
**USS**: `.unity-vector4-field`, `.unity-vector4-field__label`, `.unity-vector4-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Vector4 | X, Y, Z, W components |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### Vector2IntField
**C#**: `UnityEngine.UIElements.Vector2IntField` (base: `BaseCompositeField_3`)
**USS**: `.unity-vector2-int-field`, `.unity-vector2-int-field__label`, `.unity-vector2-int-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Vector2Int | X, Y integer components |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### Vector3IntField
**C#**: `UnityEngine.UIElements.Vector3IntField` (base: `BaseCompositeField_3`)
**USS**: `.unity-vector3-int-field`, `.unity-vector3-int-field__label`, `.unity-vector3-int-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Vector3Int | X, Y, Z integer components |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### RectField
**C#**: `UnityEngine.UIElements.RectField` (base: `BaseCompositeField_3`)
**USS**: `.unity-rect-field`, `.unity-rect-field__label`, `.unity-rect-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Rect | X, Y, Width, Height |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### RectIntField
**C#**: `UnityEngine.UIElements.RectIntField` (base: `BaseCompositeField_3`)
**USS**: `.unity-rect-int-field`, `.unity-rect-int-field__label`, `.unity-rect-int-field__input`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | RectInt | X, Y, Width, Height (integers) |
| `is-delayed` | boolean | Delayed value updates |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### BoundsField
**C#**: `UnityEngine.UIElements.BoundsField` (base: `BaseField_1`)
**USS**: `.unity-bounds-field`, `.unity-bounds-field__center-field`, `.unity-bounds-field__extents-field`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Bounds | Center + Extents |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### BoundsIntField
**C#**: `UnityEngine.UIElements.BoundsIntField` (base: `BaseField_1`)
**USS**: `.unity-bounds-int-field`, `.unity-bounds-int-field__position-field`, `.unity-bounds-int-field__size-field`

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | BoundsInt | Position + Size (integers) |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Collection Views

### ListView
**C#**: `UnityEngine.UIElements.ListView` (base: `BaseListView`)
**USS**: `.unity-list-view`, `.unity-list-view__item`, `.unity-list-view__empty-label`, `.unity-list-view__reorderable`, `.unity-list-view__footer`, `.unity-list-view__foldout-header`

Virtualized scrollable list.

| Attribute | Type | Description |
|-----------|------|-------------|
| `item-template` | UIElements.VisualTreeAsset | UXML template for items |
| `allow-add` | boolean | Show add button |
| `allow-remove` | boolean | Show remove button |
| `binding-source-selection-mode` | UIElements.BindingSourceSelectionMode | Auto data source for items |
| `fixed-item-height` | float | Item height in pixels |
| `header-title` | string | Foldout header text |
| `horizontal-scrolling` | boolean | Show horizontal scrollbar |
| `reorder-mode` | UIElements.ListViewReorderMode | Simple or Animated |
| `reorderable` | boolean | Allow drag reordering |
| `selection-type` | UIElements.SelectionType | None, Single, or Multiple |
| `show-add-remove-footer` | boolean | Show +/- footer |
| `show-alternating-row-backgrounds` | UIElements.AlternatingRowBackground | Alternating row colors |
| `show-border` | boolean | Show border |
| `show-bound-collection-size` | boolean | Show size field |
| `show-foldout-header` | boolean | Show foldout header |
| `virtualization-method` | UIElements.CollectionVirtualizationMethod | FixedHeight or DynamicHeight |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### TreeView
**C#**: `UnityEngine.UIElements.TreeView` (base: `BaseTreeView`)
**USS**: `.unity-tree-view`, `.unity-tree-view__item`, `.unity-tree-view__item-toggle`, `.unity-tree-view__item-indents`, `.unity-tree-view__item-indent`, `.unity-tree-view__item-content`

Hierarchical data display with parent-child relationships.

| Attribute | Type | Description |
|-----------|------|-------------|
| `item-template` | UIElements.VisualTreeAsset | UXML template for items |
| `auto-expand` | boolean | Auto-expand new items |
| `fixed-item-height` | float | Item height in pixels |
| `horizontal-scrolling` | boolean | Show horizontal scrollbar |
| `reorderable` | boolean | Allow drag reordering |
| `selection-type` | UIElements.SelectionType | None, Single, or Multiple |
| `show-alternating-row-backgrounds` | UIElements.AlternatingRowBackground | Alternating row colors |
| `show-border` | boolean | Show border |
| `virtualization-method` | UIElements.CollectionVirtualizationMethod | FixedHeight or DynamicHeight |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### MultiColumnListView
**C#**: `UnityEngine.UIElements.MultiColumnListView` (base: `BaseListView`)
**USS**: `.unity-list-view` (shares with ListView)

Tabular list with column headers.

| Attribute | Type | Description |
|-----------|------|-------------|
| `columns` | UIElements.Columns.UxmlSerializedData | Column definitions |
| `sort-column-descriptions` | UIElements.SortColumnDescriptions.UxmlSerializedData | Default sort columns |
| `sorting-mode` | UIElements.ColumnSortingMode | None, Default, or Custom |
| *All BaseListView attributes (same as ListView)* | | |
| *Inherits VisualElement attributes* | | |

**Column child element:**
```xml
<ui:Columns>
    <ui:Column name="colName" title="Title" width="100" stretchable="true" resizable="true" />
</ui:Columns>
```

---

### MultiColumnTreeView
**C#**: `UnityEngine.UIElements.MultiColumnTreeView` (base: `BaseTreeView`)
**USS**: `.unity-tree-view` (shares with TreeView)

Hierarchical tabular data with column headers.

| Attribute | Type | Description |
|-----------|------|-------------|
| `columns` | UIElements.Columns.UxmlSerializedData | Column definitions |
| `sort-column-descriptions` | UIElements.SortColumnDescriptions.UxmlSerializedData | Default sort columns |
| `sorting-mode` | UIElements.ColumnSortingMode | None, Default, or Custom |
| `auto-expand` | boolean | Auto-expand new items |
| *All BaseTreeView attributes (same as TreeView)* | | |
| *Inherits VisualElement attributes* | | |

---

## Scroller

### Scroller
**C#**: `UnityEngine.UIElements.Scroller` (base: `VisualElement`)
**USS**: `.unity-scroller`, `.unity-scroller--horizontal`, `.unity-scroller--vertical`, `.unity-scroller__slider`, `.unity-scroller__low-button`, `.unity-scroller__high-button`

Scrollbar control.

| Attribute | Type | Description |
|-----------|------|-------------|
| `direction` | UIElements.SliderDirection | Horizontal or Vertical |
| `high-value` | float | Maximum value |
| `low-value` | float | Minimum value |
| `value` | float | Current position |
| *Inherits VisualElement attributes* | | |

---

## Editor-Only Elements

### ColorField (Editor-only)
**C#**: `UnityEditor.UIElements.ColorField` (base: `BaseField_1`)
**USS**: `.unity-color-field`, `.unity-color-field__color`, `.unity-color-field__eyedropper`

Color picker with optional alpha and HDR support.

| Attribute | Type | Description |
|-----------|------|-------------|
| `hdr` | boolean | HDR color mode |
| `show-alpha` | boolean | Show alpha component |
| `show-eye-dropper` | boolean | Show eyedropper tool |
| `value` | Color | Current color value |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### CurveField (Editor-only)
**C#**: `UnityEditor.UIElements.CurveField` (base: `BaseField_1`)
**USS**: `.unity-curve-field`, `.unity-curve-field__content`, `.unity-curve-field__border`

Curve editor for animation curves.

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | AnimationCurve | Current curve |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### GradientField (Editor-only)
**C#**: `UnityEditor.UIElements.GradientField` (base: `BaseField_1`)
**USS**: `.unity-gradient-field`, `.unity-gradient-field__content`, `.unity-gradient-field__background`, `.unity-gradient-field__border`

Gradient editor.

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | Gradient | Current gradient |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### ObjectField (Editor-only)
**C#**: `UnityEditor.UIElements.ObjectField` (base: `BaseField_1`)
**USS**: `.unity-object-field`, `.unity-object-field__object`, `.unity-object-field__selector`

Object reference picker.

| Attribute | Type | Description |
|-----------|------|-------------|
| `allow-scene-objects` | boolean | Allow scene object assignment |
| `type` | System.Type | Allowed object type (format: "TypeName, AssemblyName") |
| `value` | Object | Current object reference |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### LayerField (Editor-only)
**C#**: `UnityEditor.UIElements.LayerField` (base: `PopupField_1`)
**USS**: `.unity-layer-field`, `.unity-layer-field__label`, `.unity-layer-field__input`

Single layer selector.

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | int | Selected layer |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### LayerMaskField (Editor-only)
**C#**: `UnityEditor.UIElements.LayerMaskField` (base: `MaskField`)
**USS**: `.unity-layer-mask-field`, `.unity-layer-mask-field__label`, `.unity-layer-mask-field__input`

Multi-layer selector.

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | LayerMask | Selected layers |
| `choices` | IList | Available options |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### MaskField (Editor-only)
**C#**: `UnityEditor.UIElements.MaskField` (base: `BaseMaskField_1`)
**USS**: `.unity-mask-field`, `.unity-mask-field__label`, `.unity-mask-field__input`

32-bit bitmask multi-selector.

| Attribute | Type | Description |
|-----------|------|-------------|
| `choices` | IList | Available options |
| `value` | int | Current 32-bit mask |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### Mask64Field (Editor-only)
**C#**: `UnityEditor.UIElements.Mask64Field` (base: `BaseMask64Field`)
**USS**: `.unity-mask64-field`

64-bit bitmask multi-selector.

| Attribute | Type | Description |
|-----------|------|-------------|
| `choices` | IList | Available options |
| `value` | System.UInt64 | Current 64-bit mask |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### TagField (Editor-only)
**C#**: `UnityEditor.UIElements.TagField` (base: `PopupField_1`)
**USS**: `.unity-tag-field`, `.unity-tag-field__label`, `.unity-tag-field__input`

Tag selector from available tags.

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | string | Selected tag |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### RenderingLayerMaskField (Editor-only)
**C#**: `UnityEditor.UIElements.RenderingLayerMaskField` (base: `BaseMaskField_1`)
**USS**: `.unity-rendering-layer-mask-field`

HDRP/URP rendering layer selector.

| Attribute | Type | Description |
|-----------|------|-------------|
| `value` | RenderingLayerMask | Selected rendering layers |
| `label` | string | Label text |
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

### PropertyField (Editor-only)
**C#**: `UnityEditor.UIElements.PropertyField` (base: `VisualElement`)
**USS**: `.unity-property-field`, `.unity-property-field__label`, `.unity-property-field__input`, `.unity-property-field__inspector-property`

Auto-generates UI for a SerializedProperty based on its type.

| Attribute | Type | Description |
|-----------|------|-------------|
| `binding-path` | string | Target property path |
| `label` | string | Override label text |
| *Inherits VisualElement attributes* | | |

---

### InspectorElement (Editor-only)
**C#**: `UnityEditor.UIElements.InspectorElement` (base: `BindableElement`)
**USS**: `.unity-inspector-element`, `.unity-inspector-element__custom-inspector-container`, `.unity-inspector-element__imgui-container`

Displays SerializedObject properties in the Inspector.

| Attribute | Type | Description |
|-----------|------|-------------|
| `binding-path` | string | Property binding path |
| *Inherits VisualElement attributes* | | |

---

## Toolbar Elements (Editor-only)

### Toolbar
**C#**: `UnityEditor.UIElements.Toolbar` (base: `VisualElement`)
**USS**: `.unity-toolbar`

Horizontal container styled for toolbar UI.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits VisualElement attributes only* | | |

---

### ToolbarButton
**C#**: `UnityEditor.UIElements.ToolbarButton` (base: `Button`)
**USS**: `.unity-toolbar-button`

Button styled for toolbar.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits Button attributes (text, icon-image, etc.)* | | |

---

### ToolbarToggle
**C#**: `UnityEditor.UIElements.ToolbarToggle` (base: `Toggle`)
**USS**: `.unity-toolbar-toggle`

Toggle styled for toolbar.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits Toggle attributes (value, text, label, etc.)* | | |

---

### ToolbarMenu
**C#**: `UnityEditor.UIElements.ToolbarMenu` (base: `TextElement`)
**USS**: `.unity-toolbar-menu`, `.unity-toolbar-menu--popup`, `.unity-toolbar-menu__text`, `.unity-toolbar-menu__arrow`

Menu button styled for toolbar.

| Attribute | Type | Description |
|-----------|------|-------------|
| `text` | string | Menu display text |
| *Inherits TextElement attributes* | | |

---

### ToolbarSearchField
**C#**: `UnityEditor.UIElements.ToolbarSearchField` (base: `SearchFieldBase_2`)
**USS**: `.unity-toolbar-search-field`, `.unity-search-field-base__text-field`, `.unity-search-field-base__search-button`, `.unity-search-field-base__cancel-button`

Search field styled for toolbar.

| Attribute | Type | Description |
|-----------|------|-------------|
| `placeholder-text` | string | Hint text |
| *Inherits VisualElement attributes* | | |

---

### ToolbarPopupSearchField
**C#**: `UnityEditor.UIElements.ToolbarPopupSearchField` (base: `ToolbarSearchField`)
**USS**: `.unity-search-field-base--popup`

Search field with popup menu on the magnifying glass icon.

| Attribute | Type | Description |
|-----------|------|-------------|
| `placeholder-text` | string | Hint text |
| *Inherits ToolbarSearchField attributes* | | |

---

### ToolbarSpacer
**C#**: `UnityEditor.UIElements.ToolbarSpacer` (base: `VisualElement`)
**USS**: `.unity-toolbar-spacer`, `.unity-toolbar-spacer--fixed`, `.unity-toolbar-spacer--flexible`

Empty space for toolbar layout.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits VisualElement attributes only* | | |

---

### ToolbarBreadcrumbs
**C#**: `UnityEditor.UIElements.ToolbarBreadcrumbs` (base: `VisualElement`)
**USS**: `.unity-toolbar-breadcrumbs`

Breadcrumb navigation bar for toolbar.

| Attribute | Type | Description |
|-----------|------|-------------|
| *Inherits VisualElement attributes only* | | |

---

## Common USS Alignment Class

Most field elements support the `.unity-base-field__aligned` USS class which aligns the element's label and input with Inspector window fields. Apply this class when embedding fields in custom Inspector UIs.

## UXML Namespace Prefixes

```xml
<!-- Runtime elements -->
xmlns:ui="UnityEngine.UIElements"

<!-- Editor-only elements -->
xmlns:uie="UnityEditor.UIElements"
```

**Runtime elements** (use `ui:` prefix): VisualElement, Label, Button, Toggle, TextField, IntegerField, FloatField, DoubleField, LongField, UnsignedIntegerField, UnsignedLongField, Hash128Field, Slider, SliderInt, MinMaxSlider, DropdownField, EnumField, RadioButton, RadioButtonGroup, ToggleButtonGroup, ScrollView, ListView, TreeView, MultiColumnListView, MultiColumnTreeView, Foldout, GroupBox, Box, Image, ProgressBar, RepeatButton, TabView, Tab, TwoPaneSplitView, TemplateContainer, BindableElement, IMGUIContainer, Scroller, PopupWindow, HelpBox, Vector2Field, Vector3Field, Vector4Field, Vector2IntField, Vector3IntField, RectField, RectIntField, BoundsField, BoundsIntField, TextElement

**Editor-only elements** (use `uie:` prefix): ColorField, CurveField, GradientField, ObjectField, LayerField, LayerMaskField, MaskField, Mask64Field, TagField, EnumFlagsField, RenderingLayerMaskField, PropertyField, InspectorElement, Toolbar, ToolbarButton, ToolbarToggle, ToolbarMenu, ToolbarSearchField, ToolbarPopupSearchField, ToolbarSpacer, ToolbarBreadcrumbs
