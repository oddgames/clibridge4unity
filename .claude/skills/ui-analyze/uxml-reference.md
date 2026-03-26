# UXML Element Reference (Unity 6)

Complete list of UXML elements, attributes, and syntax for Unity 6000.3+.

## UXML Document Structure

```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements"
         editor-extension-mode="False">
    <Style src="MyStyles.uss" />
    <!-- elements here -->
</ui:UXML>
```

- `xmlns:ui` — runtime namespace (always include)
- `xmlns:uie` — editor namespace (only if using editor controls)
- `editor-extension-mode="False"` — set to False for runtime UI
- `<Style src="file.uss" />` — link USS stylesheet (relative path from UXML)

## Common Attributes (all elements inherit from VisualElement)

| Attribute | Type | Description |
|-----------|------|-------------|
| `name` | string | Element name for C# queries and USS `#name` selectors |
| `class` | string | Space-separated USS class names |
| `style` | string | Inline USS styles (avoid — use USS file instead) |
| `focusable` | boolean | Whether element can receive focus (keyboard/gamepad nav) |
| `tabindex` | int | Focus order (< 0 excludes from focus ring) |
| `picking-mode` | PickingMode | `Position` (default, receives pointer events) or `Ignore` |
| `tooltip` | string | Hover tooltip text |
| `enabled` | boolean | Whether element is interactive |
| `view-data-key` | string | Key for persisting view state (scroll position, etc.) |
| `data-source` | Object | Data binding source |
| `data-source-path` | string | Path within data source |

## Runtime Elements (ui: namespace)

### Layout & Structure

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:VisualElement` | Base container — use for all layout groups | All common attrs |
| `ui:ScrollView` | Scrollable container | `mode`, `horizontal-scroller-visibility`, `vertical-scroller-visibility`, `elasticity` |
| `ui:GroupBox` | Logical grouping with optional label | `text` |
| `ui:Foldout` | Collapsible section | `text`, `value` (open/closed) |
| `ui:TabView` | Tab container | — |
| `ui:Tab` | Individual tab | `label`, `icon-image` |
| `ui:TwoPaneSplitView` | Resizable split layout | `fixed-pane-index`, `fixed-pane-initial-dimension`, `orientation` |

### Text & Labels

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:Label` | Text display | `text`, `enable-rich-text`, `display-tooltip-when-elided`, `selectable`, `emoji-fallback-support`, `parse-escape-sequences` |

### Buttons & Interaction

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:Button` | Clickable button | `text`, `icon-image` |
| `ui:RepeatButton` | Button that fires repeatedly while held | `text`, `delay`, `interval` |
| `ui:Toggle` | Checkbox/boolean toggle | `text`, `label`, `value` (bool) |
| `ui:RadioButton` | Exclusive selection (within RadioButtonGroup) | `text`, `value` (bool) |
| `ui:RadioButtonGroup` | Container for radio buttons | `label`, `choices`, `value` (int) |

### Text Input

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:TextField` | Single/multi-line text input | `label`, `value`, `multiline`, `max-length`, `is-delayed`, `placeholder-text` |

### Numeric Input

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:IntegerField` | Integer input | `label`, `value` |
| `ui:FloatField` | Float input | `label`, `value` |
| `ui:LongField` | Long input | `label`, `value` |
| `ui:DoubleField` | Double input | `label`, `value` |
| `ui:Vector2Field` | 2D vector | `label`, `value` |
| `ui:Vector3Field` | 3D vector | `label`, `value` |
| `ui:Vector4Field` | 4D vector | `label`, `value` |

### Sliders

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:Slider` | Horizontal float slider | `label`, `value`, `low-value`, `high-value`, `show-input-field` |
| `ui:SliderInt` | Horizontal int slider | `label`, `value`, `low-value`, `high-value` |
| `ui:MinMaxSlider` | Range slider | `label`, `min-value`, `max-value`, `low-limit`, `high-limit` |

### Selection

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:DropdownField` | Dropdown select | `label`, `choices`, `index`, `value` |
| `ui:EnumField` | Enum selector | `label`, `type`, `value` |

### Lists & Trees

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:ListView` | Virtual scrolling list | `item-height`, `show-border`, `selection-type`, `show-add-remove-footer` |
| `ui:TreeView` | Hierarchical tree | `item-height`, `show-border`, `selection-type` |
| `ui:MultiColumnListView` | Multi-column list | `show-border`, `sorting-enabled` |
| `ui:MultiColumnTreeView` | Multi-column tree | `show-border`, `sorting-enabled` |

### Media

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:Image` | Image display | — (set via C# or USS `background-image`) |

### Progress

| Element | Description | Key Attributes |
|---------|-------------|----------------|
| `ui:ProgressBar` | Progress indicator | `title`, `low-value`, `high-value`, `value` |

## Editor-Only Elements (uie: namespace)

Only available in Editor windows, NOT in runtime builds.

| Element | Description |
|---------|-------------|
| `uie:ObjectField` | Asset/object reference picker |
| `uie:ColorField` | Color picker |
| `uie:CurveField` | Animation curve editor |
| `uie:GradientField` | Gradient editor |
| `uie:LayerField` | Layer selector |
| `uie:LayerMaskField` | Layer mask selector |
| `uie:TagField` | Tag selector |
| `uie:MaskField` | Bitmask selector |
| `uie:PropertyField` | Auto-generates inspector for a serialized property |
| `uie:Toolbar` | Editor toolbar container |
| `uie:ToolbarButton` | Toolbar button |
| `uie:ToolbarToggle` | Toolbar toggle |
| `uie:ToolbarMenu` | Toolbar dropdown menu |
| `uie:ToolbarSpacer` | Toolbar spacing |

## Templates (Reusable UXML Fragments)

```xml
<!-- Define a template reference -->
<Template name="MyWidget" src="MyWidget.uxml" />

<!-- Instantiate it -->
<Instance template="MyWidget" />
```

## Rich Text Tags (in Label/Button text)

When `enable-rich-text="true"`:
```
<b>bold</b>  <i>italic</i>  <color=#ff0000>red</color>
<size=24>large</size>  <u>underline</u>  <s>strikethrough</s>
```

## ScrollView Scroller Visibility Values

- `Auto` — shows when content overflows
- `AlwaysVisible` — always shows scrollbar
- `Hidden` — never shows scrollbar

## Common Patterns

### Flex row container
```xml
<ui:VisualElement style="flex-direction: row;">
    <ui:VisualElement style="flex-grow: 1;" />
    <ui:VisualElement style="flex-grow: 1;" />
</ui:VisualElement>
```

### Scrollable list
```xml
<ui:ScrollView vertical-scroller-visibility="Auto" horizontal-scroller-visibility="Hidden">
    <ui:VisualElement name="item-1" />
    <ui:VisualElement name="item-2" />
</ui:ScrollView>
```

### Interactive button with icon placeholder
```xml
<ui:Button name="MyButton" text="Click Me" focusable="true" class="my-button" />
```

### Named element for C# query
```xml
<ui:VisualElement name="PlayerHealth" class="health-bar">
    <ui:Label name="HealthText" text="100" />
</ui:VisualElement>
```
C#: `root.Q<Label>("HealthText").text = "75";`

## Reusable Custom Elements

Use `[UxmlElement]` attribute to create custom reusable elements:

```csharp
[UxmlElement]
public partial class CardElement : VisualElement
{
    private Label titleLabel => this.Q<Label>("title");

    public CardElement() {}  // Required parameterless constructor

    public void Init(string title) { titleLabel.text = title; }
}
```

**UXML-first** (children in parent UXML):
```xml
<ui:Template name="Card" src="CardElement.uxml"/>
<ui:Instance template="Card"/>
```

**Element-first** (children loaded in constructor):
```xml
<CardElement />
```

C# instantiation:
```csharp
var template = Resources.Load<VisualTreeAsset>("CardElement");
var container = template.Instantiate();
var card = container.Q<CardElement>();
card.Init("My Card");
root.Add(card);
```

## Best Practices

- **Element count** — keep low; use `ListView` for long lists (built-in pooling/recycling)
- **Hiding elements** — prefer `display: none` (removes from layout) or `visibility: hidden` (keeps layout, frees render). Avoid `RemoveFromHierarchy()` for frequently toggled UI
- **`opacity: 0`** — GPU still processes vertices every frame; avoid on GPU-bound projects
- **Pool & reset** — if pooling elements, always unregister callbacks before returning to pool
- **`GeometryChangedEvent`** — use to monitor container size changes for custom recycling

## What NOT to Use in UXML

- `<div>`, `<span>`, `<p>` — these are HTML, not UXML
- `<img>` — use `ui:Image` or a `VisualElement` with `background-image` in USS
- `<input>` — use `ui:TextField`, `ui:IntegerField`, etc.
- `<select>` — use `ui:DropdownField`
- `onclick` or any event attributes — handle in C#

## Full Element Reference

See `uxml-elements-full.md` for the complete per-element attribute reference scraped from Unity docs.

Sources:
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-UXML.html
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-ElementRef.html
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-uxml-element-VisualElement.html
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-uxml-element-Button.html
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-uxml-element-ScrollView.html
