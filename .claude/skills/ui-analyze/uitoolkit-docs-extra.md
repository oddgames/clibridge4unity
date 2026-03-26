# Unity 6 UI Toolkit Documentation - Extended Reference

> Fetched from https://docs.unity3d.com/6000.3/Documentation/Manual/ on 2026-03-18

---

# Table of Contents

1. [Comparison of UI Systems](#1-comparison-of-ui-systems)
2. [UI Renderer](#2-ui-renderer)
3. [Support for Runtime UI](#3-support-for-runtime-ui)
4. [Support for Editor UI](#4-support-for-editor-ui)
5. [Work with Text](#5-work-with-text)
6. [Test UI](#6-test-ui)
7. [Migration Guides](#7-migration-guides)
8. [Structure UI](#8-structure-ui)
9. [Style UI (USS)](#9-style-ui-uss)
10. [Control Behavior with Events](#10-control-behavior-with-events)
11. [Data Binding](#11-data-binding)
12. [Best Practices: Layouts](#12-best-practices-layouts)
13. [Best Practices: Styling](#13-best-practices-styling)
14. [Best Practices: Custom Controls](#14-best-practices-custom-controls)
15. [Best Practices: Optimizing Performance](#15-best-practices-optimizing-performance)

---

# 1. Comparison of UI Systems

Source: `UI-system-compare.html`

## General Recommendations (Unity 6.3)

| Context | Recommendation | Alternative |
|---------|---------------|-------------|
| **Runtime** | uGUI (Unity UI) | UI Toolkit |
| **Editor** | UI Toolkit | IMGUI |

"UI Toolkit is intended to become the recommended UI system for you to develop UI in your projects."

## Runtime Use Cases

| Use Case | Recommendation |
|----------|---------------|
| Multi-resolution menus and HUD in intensive UI projects | UI Toolkit |
| World space UI and VR | UI Toolkit |
| UI requiring customized shaders and materials | UI Toolkit |
| UI requiring keyframed animations | uGUI |

## Detailed Runtime Feature Comparison

| Feature | UI Toolkit | uGUI | IMGUI |
|---------|-----------|------|-------|
| WYSIWYG authoring | Yes | Yes | No |
| Nesting reusable components | Yes | Yes | No |
| Layout and Styling Debugger | Yes | Yes | No |
| In-scene authoring | No | Yes | No |
| Rich text tags | Yes | Yes | No |
| Scalable text | Yes | Yes | No |
| Font fallbacks | Yes | Yes | No |
| Adaptive layout | Yes | Yes | No |
| Input system support | Yes | Yes | No |
| Serialized events | No | Yes | No |
| Screen-space (2D) rendering | Yes | Yes | No |
| World-space (3D) rendering | Yes | Yes | No |
| Custom materials and shaders | Yes | Yes | No |
| Sprites/Sprite atlas support | Yes | Yes | No |
| Rectangle clipping | Yes | Yes | No |
| Mask clipping | Yes | Yes | No |
| Nested masking | Yes | Yes | No |
| Integration with Animation Clips and Timeline | No | Yes | No |
| Data binding system | Yes | No | No |
| UI transition animations | Yes | No | No |
| Textureless elements | Yes | No | No |
| Advanced flexible layout | Yes | No | No |
| Global style management | Yes | No | No |
| Dynamic texture atlas | Yes | No | No |
| UI anti-aliasing | Yes | No | No |
| Right-to-left language and emoji | Yes | No | No |
| SVG support | Yes | No | No |

## Sub-pages

- UIToolkits.html - UI systems
- UIElements.html - UI Toolkit
- com.unity.ugui.html - uGUI (Unity UI)
- GUIScriptingGuide.html - IMGUI
- UIBuilder.html - UI Builder
- UIE-rich-text-tags.html - Rich text tags
- UIE-fallback-font.html - Font fallbacks
- UIE-runtime-binding.html - Data binding system
- UIE-Transitions.html - UI transition animations
- ui-systems/language-direction.html - Right-to-left language
- ui-systems/work-with-vector-graphics.html - SVG support
- UIE-Transitioning-From-UGUI.html - Migrate from uGUI to UI Toolkit
- UIE-IMGUI-migration.html - Migrate from IMGUI to UI Toolkit

---

# 2. UI Renderer

Source: `UIE-ui-renderer.html`

The UI Renderer is a rendering system built directly on top of Unity's graphics device layer for generating visual content.

## Topics

| Topic | Description |
|-------|-------------|
| Generate 2D visual content | Mesh API, Vector API, and graphics creation |
| Work with vector graphics | Creating, manipulating, and rendering vector shapes |
| Create a pie chart | Using Vector API for pie chart implementation |
| Use Vector API for radial progress | Custom radial progress controls for runtime UI |
| Use Mesh API for radial progress | Alternative Mesh API approach |
| Parallel tessellation | Performance optimization for large meshes |

## Sub-pages

- UIE-generate-2d-visual-content.html
- ui-systems/work-with-vector-graphics.html
- UIE-pie-chart.html
- UIE-radial-progress-use-vector-api.html
- UIE-radial-progress.html
- UIE-parallel-tessellation.html
- UIE-create-custom-controls.html

---

# 3. Support for Runtime UI

Source: `UIE-support-for-runtime-ui.html`

"You can use UI Toolkit to create UI for the runtime. You can use the UI Toolkit's event system with Unity's different input systems."

## Topics

| Topic | Purpose |
|-------|---------|
| Get started with runtime UI | Introductory example |
| Configure runtime UI | Panel creation, UI Document components, event system |
| World space UI | Spatial UI in 3D environments |
| Performance consideration | Optimization strategies |
| Runtime UI examples | Demo projects |

## Sub-pages

- UIE-get-started-with-runtime-ui.html
- UIE-runtime-ui-configuration.html (not found - likely different URL)
- ui-systems/world-space-ui.html
- UIE-runtime-performance.html
- UIE-runtime-examples.html

## 3.1 Getting Started with Runtime UI

Source: `UIE-get-started-with-runtime-ui.html`

### Step 1: Create a UI Document (.uxml)

```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements"
        xsi="http://www.w3.org/2001/XMLSchema-instance" engine="UnityEngine.UIElements" editor="UnityEditor.UIElements"
        noNamespaceSchemaLocation="../UIElementsSchema/UIElements.xsd" editor-extension-mode="False">
    <ui:VisualElement style="flex-grow: 1;">
        <ui:Label text="This is a Label" display-tooltip-when-elided="true"/>
        <ui:Button text="This is a Button" display-tooltip-when-elided="true" name="button"/>
        <ui:Toggle label="Display the counter?" name="toggle"/>
        <ui:TextField picking-mode="Ignore" label="Text Field" text="filler text" name="input-message" />
    </ui:VisualElement>
</ui:UXML>
```

### Step 2: Configure the Scene

1. Select **GameObject > UI Toolkit > UI Document**
2. Creates a UI Toolkit folder with Panel Settings and default theme
3. Drag UXML to the UIDocument component's **Source Asset** field

### Step 3: Define UI Behavior with MonoBehaviour

```csharp
using UnityEngine;
using UnityEngine.UIElements;

public class SimpleRuntimeUI : MonoBehaviour
{
    private Button _button;
    private Toggle _toggle;
    private int _clickCount;

    private void OnEnable()
    {
        var uiDocument = GetComponent<UIDocument>();
        _button = uiDocument.rootVisualElement.Q("button") as Button;
        _toggle = uiDocument.rootVisualElement.Q("toggle") as Toggle;

        _button.RegisterCallback<ClickEvent>(PrintClickMessage);

        var _inputFields = uiDocument.rootVisualElement.Q("input-message");
        _inputFields.RegisterCallback<ChangeEvent<string>>(InputMessage);
    }

    private void OnDisable()
    {
        _button.UnregisterCallback<ClickEvent>(PrintClickMessage);
    }

    private void PrintClickMessage(ClickEvent evt)
    {
        ++_clickCount;
        Debug.Log($"{"button"} was clicked!" +
                (_toggle.value ? " Count: " + _clickCount : ""));
    }

    public static void InputMessage(ChangeEvent<string> evt)
    {
        Debug.Log($"{evt.newValue} -> {evt.target}");
    }
}
```

**Key Points:**
- Logic must be added in `OnEnable` since the UXML loads when UIDocument enables
- Query controls using `Q()` method
- Register callbacks for user interactions
- Unregister callbacks in `OnDisable`

## 3.2 World Space UI

Source: `ui-systems/world-space-ui.html`

World Space UI allows creating UI positioned alongside 2D or 3D objects in the Scene, rather than screen-space overlay.

Key areas:
- Set the render mode of Panel Settings to World Space
- Configure panel input for World Space
- Size and position World Space UI elements

Note: Arbitrary shape masking is NOT supported in World Space UI.

---

# 4. Support for Editor UI

Source: `UIE-support-for-editor-ui.html`

"You can use UI Toolkit to create Editor UI and synchronize data between a property and a visual element."

## Topics

| Topic | Description |
|-------|-------------|
| Get started with UI Toolkit | Basic Editor window example |
| Create a custom Editor window | Practical guide with code |
| Create a custom Inspector | Custom Inspector by example |
| Create a default Inspector | Default Inspector example |
| SerializedObject data binding | Data binding concepts and examples |
| ViewData persistence | ViewData API usage |

## Sub-pages

- UIE-HowTo-CreateEditorWindow.html
- UIE-bind-to-custom-data-type.html

---

# 5. Work with Text

Source: `UIE-work-with-text.html`

"Text objects are defined by the `text` attribute of some UI controls, such as Label or TextElement."

## Styling Options

- USS text properties for font size, color, and other attributes
- Rich text tags for styling specific words
- Font assets (requiring conversion before use)

## Sub-pages

- UIE-get-started-with-text.html - Getting Started
- UIE-advanced-text-generator.html - Advanced Text Generator
- UIB-styling-ui-text.html - USS Styling
- UIE-rich-text-tags.html - Rich Text Tags
- UIE-font-asset-landing.html - Font Assets
- UIE-text-effects.html - Text Effects
- UIE-style-sheet.html - Style Sheet Assets
- UIE-sprite.html - Sprites in Text
- UIE-color-gradient.html - Color Gradients
- UIE-color-emojis.html - Color Emojis
- ui-systems/language-direction.html - Language Direction
- UIE-text-setting-asset.html - Text Settings
- UIE-fallback-font.html - Fallback Fonts
- ui-systems/create-custom-text-animation.html - Custom Text Animation

---

# 6. Test UI

Source: `UIE-test-ui.html`

"The UI Toolkit provides tools to help you test and debug your UI elements."

## Topics

| Topic | Purpose |
|-------|---------|
| Test UI in UI Builder | Debug styling and test directly |
| UI Toolkit live reload | View UI changes immediately |
| UI Toolkit Debugger | Inspect and debug UI elements |
| UI Toolkit profiler markers | Profile UI performance |
| Event Debugger | Inspect and troubleshoot events |
| UI Test Framework | Automated UI tests |

## Sub-pages

- UIB-testing-ui.html
- ui-systems/ui-toolkit-live-reload.html
- UIE-ui-debugger.html
- UIE-profiler-markers.html
- ui-systems/event-debugger.html
- https://docs.unity3d.com/Packages/com.unity.ui.test-framework@1.0/manual/index.html

---

# 7. Migration Guides

Source: `UIE-migration-guides.html`

## Topics

| Topic | Description |
|-------|-------------|
| Migrate custom controls to Unity 6 | Traditional vs Unity 6 custom control workflow |
| Migrate from uGUI to UI Toolkit | Similarities and differences |
| Migrate from IMGUI to UI Toolkit | Transition path details |

## Sub-pages

- ui-systems/migrate-custom-control.html
- UIE-Transitioning-From-UGUI.html
- UIE-IMGUI-migration.html

---

# 8. Structure UI

Source: `UIE-structure-ui.html`

## Main Sub-pages

| Topic | Description | Link |
|-------|-------------|------|
| The visual tree | Object graph holding all UI elements | UIE-VisualTree-landing.html |
| Structure UI with UXML | Creating UXML files, styles, templates | UIE-UXML.html |
| Structure UI with C# scripts | Adding controls via code | UIE-Controls.html |
| Custom controls | Creating custom controls | UIE-custom-controls.html |
| Best practices for managing elements | Performance optimization | UIE-best-practices-for-managing-elements.html |
| Encapsulate UXML with logic | Reusable components | UIE-encapsulate-uxml-with-logic.html |
| UXML elements Reference | All available elements | UIE-ElementRef.html |
| Structure UI examples | Practical examples | UIE-uxml-examples.html |

## 8.1 The Visual Tree

Source: `UIE-VisualTree-landing.html`, `UIE-VisualTree.html`

"An object graph, made of lightweight nodes, that holds all the elements in a window or panel."

### Core Concepts

- **Visual Element**: Fundamental building block, instantiated from `VisualElement` class
- **Visual Tree**: Hierarchical structure with parent-child relationships
- **Root**: `EditorWindow.rootVisualElement` (Editor) or `UIDocument.rootVisualElement` (runtime)
- **Customization**: inline styles, stylesheets, event callbacks
- **Built-in Controls**: Buttons, Toggles, Text fields (specialized VisualElement subclasses)

### Sub-pages

- UIE-VisualTree.html - Introduction to visual elements
- UIE-panels.html - Panels
- UIE-draw-order.html - Draw order
- UIE-coordinate-and-position-system.html - Coordinate and position systems
- UIE-relative-absolute-positioning-example.html - Positioning examples

### Coordinate Systems

Source: `UIE-coordinate-and-position-system.html`

- **Relative positioning**: Coordinates relative to the element's calculated position
- **Absolute positioning**: Coordinates relative to the parent element
- **`layout` property**: `Rect` with final computed position relative to parent
- **`transform` property**: `ITransform` for additional local offset (position/rotation)
- **`worldBound`**: Final window space coordinates considering layout + transform
- **Origin**: Top-left corner of every element

Coordinate conversion methods in `VisualElementExtensions`:
- `WorldToLocal` - Panel space to element-local space
- `LocalToWorld` - Element-local to Panel space
- `ChangeCoordinatesTo` - Between two elements' local spaces

## 8.2 Structure UI with UXML

Source: `UIE-UXML.html`

"Unity Extensible Markup Language (UXML) files are text files that define the structure of the UI."

### Sub-pages

- UIE-WritingUXMLTemplate.html - Introduction to UXML
- UIE-add-style-to-uxml.html - Add styles to UXML
- UIE-reuse-uxml-files.html - Reuse UXML files
- UIE-reference-other-files-from-uxml.html - Reference other files
- UIE-manage-asset-reference.html - Load UXML and USS from C#
- UIE-LoadingUXMLcsharp.html - Instantiate UXML with C#
- UIE-UQuery.html - Find elements with UQuery
- UIE-coordinate-and-position-system.html - Coordinate systems

### 8.2.1 Introduction to UXML

Source: `UIE-WritingUXMLTemplate.html`

UXML is inspired by HTML, XAML, and XML.

#### Declaration

```xml
<?xml version="1.0" encoding="utf-8"?>
```

#### Namespaces

- `UnityEngine.UIElements` - Runtime elements
- `UnityEditor.UIElements` - Editor-only elements

```xml
xmlns:engine="UnityEngine.UIElements"
xmlns:editor="UnityEditor.UIElements"
```

Usage: `<engine:Button />` or with default namespace just `<Button />`

#### Universal VisualElement Attributes

All elements inherit:
- `name` - Unique identifier
- `picking-mode` - `Position` (respond to events) or `Ignore` (ignore events)
- `tabindex` - Integer tabbing order
- `focusable` - Boolean focusing capability
- `class` - Space-separated style/selector identifiers
- `tooltip` - Hover text
- `view-data-key` - Serialization key

#### Example

```xml
<?xml version="1.0" encoding="utf-8"?>
<engine:UXML
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xmlns:engine="UnityEngine.UIElements"
    xmlns:editor="UnityEditor.UIElements"
    xsi:noNamespaceSchemaLocation="../../UIElementsSchema/UIElements.xsd"
>
    <engine:Box>
        <engine:Toggle name="boots" label="Boots" value="false" />
        <engine:Toggle name="helmet" label="Helmet" value="false" />
        <engine:Toggle name="cloak" label="Cloak of invisibility" value="false"/>
    </engine:Box>
    <engine:Box>
        <engine:Button name="cancel" text="Cancel" />
        <engine:Button name="ok" text="OK" />
    </engine:Box>
</engine:UXML>
```

### 8.2.2 Add Styles to UXML

Source: `UIE-add-style-to-uxml.html`

#### Inline styles

```xml
<ui:VisualElement style="width: 200px; height: 200px; background-color: red;" />
```

#### External stylesheets

```css
/* styles.uss */
#root {
    width: 200px;
    height: 200px;
    background-color: red;
}
```

```xml
<ui:UXML ...>
    <Style src="<path-to-file>/styles.uss" />
    <ui:VisualElement name="root" />
</ui:UXML>
```

#### Path types
- Absolute: `/Assets/myFolder/myFile.uss` or `project://database/...`
- Relative: `../myFolder/myFile.uss`
- Package: `/Packages/com.unity.package.name/file-name.uss`

### 8.2.3 Reuse UXML Files (Templates)

Source: `UIE-reuse-uxml-files.html`

#### Template definition

```xml
<!-- Portrait.uxml -->
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:VisualElement class="portrait">
        <ui:Image name="portraitImage" style="--unity-image: url(a.png)"/>
        <ui:Label name="nameLabel" text="Name"/>
        <ui:Label name="levelLabel" text="42"/>
    </ui:VisualElement>
</ui:UXML>
```

#### Using templates

```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:Template src="Portrait.uxml" name="Portrait"/>
    <ui:VisualElement name="players">
        <ui:Instance template="Portrait" name="player1"/>
        <ui:Instance template="Portrait" name="player2"/>
    </ui:VisualElement>
</ui:UXML>
```

#### Attribute overrides

```xml
<ui:Instance name="player1" template="PlayerTemplate">
    <ui:AttributeOverrides element-name="player-name-label" text="Alice" />
    <ui:AttributeOverrides element-name="player-score-label" text="2" />
</ui:Instance>
```

**Limitations:**
- Cannot override `class`, `name`, or `style` attributes
- Data binding doesn't work with attribute overrides
- Shallowest override takes precedence in nested templates

#### Content container

```xml
<!-- MyTemplate.uxml -->
<ui:Label text="Group Title" name="groupTitle" />
<ui:VisualElement name="parent-container" content-container="anyValue">
     <!--Child elements inserted here -->
</ui:VisualElement>
```

### 8.2.4 Load UXML and USS from C#

Source: `UIE-manage-asset-reference.html`

#### Method 1: Serialization References

```csharp
public class MyBehaviour : MonoBehaviour
{
    public VisualTreeAsset exampleUI;
    public StyleSheet[] exampleStyle;
}
```

#### Method 2: AssetDatabase (Editor only)

```csharp
VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/main_window.uxml");
StyleSheet uss = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/main_styles.uss");
```

#### Method 3: Resources folder

```csharp
VisualTreeAsset uxml = Resources.Load<VisualTreeAsset>("main_window");
StyleSheet uss = Resources.Load<StyleSheet>("main_styles");
```

Warning: All files in Resources folder are included in the build.

### 8.2.5 Instantiate UXML with C#

Source: `UIE-LoadingUXMLcsharp.html`

```csharp
VisualTreeAsset uiAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/MyWindow.uxml");
VisualElement ui = uiAsset.Instantiate();
w.rootVisualElement.Add(ui);
```

Two approaches:
- `Instantiate()` - creates new `TemplateContainer`
- `CloneTree(parent)` - instantiates within an existing parent

### 8.2.6 UQuery - Finding Visual Elements

Source: `UIE-UQuery.html`

#### By name

```csharp
VisualElement result = root.Q("OK");
List<VisualElement> results = root.Query("OK").ToList();
```

#### By USS class

```csharp
List<VisualElement> result = root.Query(className: "yellow").ToList();
```

#### By element type

```csharp
VisualElement result = root.Q<Button>();
```

#### With predicates

```csharp
List<VisualElement> result = root.Query(className: "yellow")
    .Where(elem => elem.tooltip == "").ToList();
```

#### Complex hierarchical queries

```csharp
VisualElement result = root.Query<Button>(className: "yellow", name: "OK").First();
VisualElement result = root.Query<VisualElement>("container2")
    .Children<Button>("Cancel").First();
```

#### ForEach

```csharp
root.Query().Where(elem => elem.tooltip == "")
    .ForEach(elem => elem.tooltip="This is a tooltip!");
```

**Best Practices:**
- Cache query results at initialization
- Manually traverse `.parent` for ancestors
- Use `QueryState` struct to enumerate without creating lists
- Enable incremental GC when creating/releasing many elements

## 8.3 Structure UI with C# Scripts

Source: `UIE-Controls.html`

### Add controls

```csharp
var newButton = new Button("Click me!");
rootVisualElement.Add(newButton);
```

### Change control value

```csharp
m_MyToggle = new Toggle("Test Toggle") { name = "My Toggle" };
rootVisualElement.Add(m_MyToggle);

Button button01 = new Button() { text = "Toggle" };
button01.clicked += () => { m_MyToggle.value = !m_MyToggle.value; };
rootVisualElement.Add(button01);
```

### Register a callback

```csharp
m_MyToggle.RegisterValueChangedCallback((evt) => { Debug.Log("Change Event received"); });
```

### Manage control states

```csharp
myToggle.SetEnabled(false);
```

USS for disabled state:
```css
.unity-button:disabled {
    background-color: #000000;
}
```

## 8.4 Custom Controls

Source: `UIE-custom-controls.html`, `UIE-create-custom-controls.html`

### Sub-pages

- UIE-create-custom-controls.html - Create custom controls
- ui-systems/custom-control-customize-uxml-tag-names.html - Configure name and visibility
- ui-systems/custom-control-attributes-built-in-types.html - UXML attributes for built-in types
- ui-systems/custom-control-attributes-complex-data-types.html - UXML attributes for complex types
- ui-systems/custom-control-customize-uxml-attributes.html - Customize UXML attributes
- UIE-bind-custom-control-to-data.html - Bind to data
- UIE-define-a-namespace-prefix.html - Define namespace prefix
- UIE-troubleshooting-custom-control-library-compilation.html - Troubleshooting

### Creating Custom Controls

```csharp
using UnityEngine;
using UnityEngine.UIElements;

[UxmlElement]
public partial class ExampleElement : VisualElement {}
```

UXML usage:
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ExampleElement />
</ui:UXML>
```

### Initialization

```csharp
public CustomControl()
{
    RegisterCallback<AttachToPanelEvent>(e => { /* init on UI addition */ });
    RegisterCallback<DetachFromPanelEvent>(e => { /* cleanup on removal */ });
}
```

### Best Practices

- Expose functional properties as UXML attributes; appearance as USS properties
- Use unique, concise namespaces
- Keep UXML attributes primitive
- Expose USS classes as constants for UQuery
- Follow BEM naming conventions
- Use static callbacks to minimize memory allocations
- Render custom geometry through `generateVisualContent` callback

## 8.5 Encapsulate UXML Documents with Logic

Source: `UIE-encapsulate-uxml-with-logic.html`

Two approaches for reusable UI components:

### UXML-First Approach

Custom control with hierarchy in UXML:

```csharp
[UxmlElement]
public partial class CardElement : VisualElement
{
    private VisualElement portraitImage => this.Q("image");
    private Label attackBadge => this.Q<Label>("attack-badge");
    private Label healthBadge => this.Q<Label>("health-badge");

    public void Init(Texture2D image, int health, int attack)
    {
        portraitImage.style.backgroundImage = image;
        attackBadge.text = health.ToString();
        healthBadge.text = attack.ToString();
    }
    public CardElement() {}
}
```

```xml
<CardElement>
    <ui:VisualElement name="image" />
    <ui:VisualElement name="stats">
        <ui:Label name="attack-badge" class="badge" />
        <ui:Label name="health-badge" class="badge" />
    </ui:VisualElement>
</CardElement>
```

Use via Template/Instance:
```xml
<ui:Template name="CardElement" src="CardElement.uxml"/>
<ui:Instance template="CardElement"/>
```

### Element-First Approach

Custom control loads UXML internally:

```csharp
[UxmlElement]
public partial class CardElement : VisualElement
{
    public CardElement(Texture2D image, int health, int attack)
    {
        var asset = Resources.Load<VisualTreeAsset>("CardElement");
        asset.CloneTree(this);
        // set values...
    }
}
```

UXML does NOT include the custom element wrapper:
```xml
<ui:VisualElement name="image" />
<ui:VisualElement name="stats">
    <ui:Label name="attack-badge" class="badge" />
    <ui:Label name="health-badge" class="badge" />
</ui:VisualElement>
```

Use directly:
```xml
<CardElement />
```

### Runtime Instantiation

```csharp
public class UIManager : MonoBehaviour
{
    public void Start()
    {
        UIDocument document = GetComponent<UIDocument>();
        foreach(Card card in GetCards())
        {
            var cardElement = new CardElement(card.image, card.health, card.attack);
            cardElement.RegisterCallback<ClickEvent>(SomeInteraction);
            document.rootVisualElement.Add(cardElement);
        }
    }
}
```

| Aspect | UXML-First | Element-First |
|--------|-----------|---------------|
| UI Structure | Fixed in UXML | Flexible, loaded via C# |
| Initialization | Call Init() after adding | Constructor before adding |
| Navigation | Navigate in UI Builder | Cannot navigate bidirectionally |
| Use Case | Simpler, consistent UI | Complex, conditional UI |

## 8.6 Best Practices for Managing Elements

Source: `UIE-best-practices-for-managing-elements.html`

### Pool Recurring Elements

UI Toolkit does NOT include a built-in pool for VisualElements. Unregister event callbacks before returning to pool to avoid memory leaks.

### Keep Visible Elements Low

Use ListView for automatic pooling/recycling of scrollable content.

### Hiding Approaches Comparison

| Method | Rendering | Layout | Memory | Use Case |
|--------|-----------|--------|--------|----------|
| `visibility: hidden` | Hidden | Included | Freed render cmds | Compromise |
| `opacity: 0` | Rendered (GPU) | Included | Full | Transitions |
| `display: none` | Hidden | Not updated | Retained | Frequent toggling |
| `translate: -5000px` | Rendered | Included | Full | DynamicTransform |
| `RemoveFromHierarchy()` | Hidden | Not updated | Released | Infrequent elements |

## 8.7 UXML Elements Reference

Source: `UIE-ElementRef.html`

### Base Elements
- `BindableElement` - UnityEngine.UIElements
- `VisualElement` - UnityEngine.UIElements

### Input Fields
BoundsField, BoundsIntField, ColorField, DoubleField, FloatField, IntegerField, LongField, RectField, RectIntField, TextField, UnsignedIntegerField, UnsignedLongField, Vector2Field, Vector2IntField, Vector3Field, Vector3IntField, Vector4Field

### Selection Controls
Button, Dropdown/DropdownField, EnumField, EnumFlagsField, LayerField, LayerMaskField, MaskField, Mask64Field, ObjectField, PopupWindow, RadioButton, RadioButtonGroup, TagField, Toggle, ToggleButtonGroup

### Display Controls
Box, GroupBox, HelpBox, Image, Label, TextElement

### Complex Controls
CurveField, GradientField, Hash128Field, ListView, MinMaxSlider, MultiColumnListView, MultiColumnTreeView, ProgressBar, Slider, SliderInt, TreeView, Foldout

### Container Controls
IMGUIContainer, InspectorElement, PropertyField, ScrollView, Scroller, TwoPaneSplitView, Tab, TabView, TemplateContainer

### Editor-Only Controls
RenderingLayerMaskField, Toolbar, ToolbarBreadcrumbs, ToolbarButton, ToolbarMenu, ToolbarPopupSearchField, ToolbarSearchField, ToolbarSpacer, ToolbarToggle

### Other
RepeatButton, GenericDropdownMenu (C# only)

### Templates
- `Template` - References another UXML template (attrs: name, path)
- `Instance` - Creates an instance of a Template (attrs: template)

---

# 9. Style UI (USS)

Source: `UIE-USS.html`

"You can style your UI with a Unity Style Sheet (USS). USS files are text files inspired by Cascading Style Sheets (CSS) from HTML."

## Sub-pages

- UIE-about-uss.html - Introduction to USS
- UIE-USS-Selectors.html - USS selectors
- UIE-uss-properties.html - USS properties
- UIE-USS-variables.html - USS custom properties (variables)
- UIE-apply-styles-with-csharp.html - Apply styles with C#
- UIE-USS-WritingStyleSheets.html - Best practices for USS
- UIE-tss.html - Theme Style Sheet (TSS)
- UIE-masking.html - Apply masking effects
- ui-systems/ui-shader-graph.html - UI Shader Graph

## 9.1 Introduction to USS

Source: `UIE-about-uss.html`

### Core Components

1. **Style rules** - selectors + declaration blocks
2. **Selectors** - identify target visual elements
3. **Declaration blocks** - property-value pairs in curly braces

### Syntax

```
selector {
  property1: value;
  property2: value;
}
```

### Simple Selectors

| Type | Syntax | Purpose |
|------|--------|---------|
| Type | `Type {...}` | Matches C# types |
| Class | `.class {...}` | Matches assigned USS classes |
| Name | `#name {...}` | Matches assigned names |
| Universal | `* {...}` | Matches any element |

### Complex Selectors

| Type | Syntax | Purpose |
|------|--------|---------|
| Descendant | `selector1 selector2 {...}` | Any descendant |
| Child | `selector1 > selector2 {...}` | Direct children |
| Multiple | `selector1selector2 {...}` | All selectors satisfied |

### Selector rules
- Case-sensitive
- Valid starting: letters (A-Z, a-z), underscore (_)
- Valid chars: letters, digits, hyphens, underscores
- Cannot start with digits or hyphen-digit

### Three ways to apply styles
- UI Builder (inline or USS)
- UXML (inline or attached stylesheets)
- C# (direct style property or stylesheet attachment)

## 9.2 USS Selectors

Source: `UIE-USS-Selectors.html`

### Sub-pages

- UIE-USS-Selectors-type.html - Type selectors
- UIE-USS-Selectors-name.html - Name selectors
- UIE-USS-Selectors-class.html - Class selectors
- UIE-USS-Selectors-universal.html - Universal selectors
- UIE-USS-Selectors-descendant.html - Descendant selectors
- UIE-USS-Selectors-child.html - Child selectors
- UIE-USS-Selectors-multiple.html - Multiple selectors
- UIE-USS-Selectors-list.html - Selectors list
- UIE-USS-Selectors-Pseudo-Classes.html - Pseudo-classes
- UIE-uss-selector-precedence.html - Selector precedence

### Pseudo-classes

Source: `UIE-USS-Selectors-Pseudo-Classes.html`

| Pseudo-class | Matches when |
|---|---|
| `:hover` | Cursor over element |
| `:active` | User interacts with Button/RadioButton/Toggle |
| `:inactive` | User stops interacting |
| `:focus` | Element has focus |
| `:disabled` | Element is disabled |
| `:enabled` | Element is enabled |
| `:checked` | Toggle or RadioButton is selected |
| `:root` | Highest-level element with stylesheet |

USS does NOT support `:selected`; use `:checked` instead.

#### Chaining
```css
Toggle:checked:hover {
    background-color: yellow;
}
```

#### Root for variables
```css
:root {
    --my-color: #ff0000;
}
Button {
    background-color: var(--my-color);
}
```

### Selector Specificity Hierarchy (highest to lowest)
1. **Inline styles** - override all USS
2. **ID selectors** (#id)
3. **Class selectors** (.className)
4. **C# Type selectors**

When specificity ties, the selector later in the USS file wins.

## 9.3 USS Common Properties

Source: `UIE-USS-SupportedProperties.html`

### All Property
```css
all: initial
```

### Box Model - Dimensions
```css
width: <length> | auto
height: <length> | auto
min-width: <length> | auto
min-height: <length> | auto
max-width: <length> | none
max-height: <length> | none
aspect-ratio: <ratio> | auto
```

### Box Model - Margins
```css
margin: [<length> | auto]{1,4}  /* Shorthand */
margin-left: <length> | auto
margin-top: <length> | auto
margin-right: <length> | auto
margin-bottom: <length> | auto
```

### Box Model - Borders
```css
border-width: <length>{1,4}  /* Shorthand */
border-left-width: <length>
border-top-width: <length>
border-right-width: <length>
border-bottom-width: <length>
```

### Box Model - Padding
```css
padding: <length>{1,4}  /* Shorthand */
padding-left: <length>
padding-top: <length>
padding-right: <length>
padding-bottom: <length>
```

**USS uses `border-box` sizing model**: width/height include padding and borders (unlike standard CSS content-box).

### Flex Layout - Item Properties
```css
flex-grow: <number>
flex-shrink: <number>
flex-basis: <length> | auto
flex: none | [ <'flex-grow'> <'flex-shrink'>? || <'flex-basis'> ]
align-self: auto | flex-start | flex-end | center | stretch
```

### Flex Layout - Container Properties
```css
flex-direction: row | row-reverse | column | column-reverse
flex-wrap: nowrap | wrap | wrap-reverse
align-content: flex-start | flex-end | center | stretch
align-items: auto | flex-start | flex-end | center | stretch
justify-content: flex-start | flex-end | center | space-between | space-around
```

Default `flex-direction` is `column` (top-to-bottom). Default `align-items` is `stretch`.

### Positioning
```css
position: absolute | relative
left: <length> | auto
top: <length> | auto
right: <length> | auto
bottom: <length> | auto
```

### Background
```css
background-color: <color>
background-image: <resource> | <url> | none
-unity-background-scale-mode: stretch-to-fill | scale-and-crop | scale-to-fit
-unity-background-image-tint-color: <color>
```

### 9-Slice (Slicing)
```css
-unity-slice-left: <integer>
-unity-slice-top: <integer>
-unity-slice-right: <integer>
-unity-slice-bottom: <integer>
-unity-slice-scale: <length>
-unity-slice-type: sliced | tiled
```

### Border Color
```css
border-color: <color>{1,4}  /* Shorthand */
border-left-color: <color>
border-top-color: <color>
border-right-color: <color>
border-bottom-color: <color>
```

### Border Radius
```css
border-radius: <length>{1,4}  /* Shorthand */
border-top-left-radius: <length>
border-top-right-radius: <length>
border-bottom-left-radius: <length>
border-bottom-right-radius: <length>
```

No elliptical corners shorthand. Values clamped to 50% of element size.

### Appearance
```css
overflow: hidden | visible
-unity-overflow-clip-box: padding-box | content-box
visibility: visible | hidden
display: flex | none
```

### Text Properties
```css
color: <color>
-unity-font: <resource> | <url>
-unity-font-definition: <resource> | <url>
font-size: <number>
-unity-font-style: normal | italic | bold | bold-and-italic
-unity-text-align: upper-left | middle-left | lower-left | upper-center |
                   middle-center | lower-center | upper-right | middle-right |
                   lower-right
-unity-text-overflow-position: start | middle | end
white-space: normal | nowrap | pre | pre-wrap
text-overflow: clip | ellipsis
text-shadow: <x-offset> <y-offset> <blur-radius> <color>
letter-spacing: <length>
word-spacing: <length>
-unity-paragraph-spacing: <length>
-unity-text-outline-width: <length>
-unity-text-outline-color: <color>
-unity-text-generator: standard | advanced
-unity-text-auto-size: none | best-fit <min-font-size> <max-font-size>
```

### Cursor
```css
cursor: [ [ <resource> | <url> ] [ <integer> <integer>]? , ]
        [ arrow | text | resize-vertical | resize-horizontal | link |
          slide-arrow | resize-up-right | resize-up-left | move-arrow |
          rotate-arrow | scale-arrow | arrow-plus | arrow-minus | pan |
          orbit | zoom | fps | split-resize-up-down | split-resize-left-right ]
```

Keywords only in Editor UI. Runtime requires textures for custom cursors.

### Opacity
```css
opacity: <number>
```

Parent opacity affects perceived child opacity in USS.

### Material
```css
-unity-material: <resource> | <url> | none
```

Automatically inherited by children.

## 9.4 USS Properties Reference

Source: `UIE-USS-Properties-Reference.html`

### All Properties by Category

**Layout/Positioning**: flex, flex-direction, flex-grow, flex-shrink, flex-basis, flex-wrap, align-content, align-items, align-self, justify-content, position, top, bottom, left, right

**Box Model**: width, height, min-width, max-width, min-height, max-height, margin (all), padding (all), border-width (all), aspect-ratio

**Visual Styling**: background-color, background-image, background-position, background-size, background-repeat, -unity-background-image-tint-color, -unity-background-scale-mode, opacity, filter, visibility

**Borders**: border-color (all), border-radius (all), border-width (all)

**Transforms**: rotate, scale, translate, transform-origin

**Text**: -unity-font, -unity-font-definition, -unity-font-style, font-size, letter-spacing, word-spacing, -unity-paragraph-spacing, color, text-shadow, -unity-text-outline (all), -unity-text-align, text-overflow, -unity-text-overflow-position, white-space, -unity-text-auto-size, -unity-text-generator, -unity-editor-text-rendering-mode

**Slicing**: -unity-slice-type, -unity-slice-top/bottom/left/right, -unity-slice-scale

**Advanced**: -unity-material, display, overflow, -unity-overflow-clip-box, transition (all), cursor, all

### Animatability
- **Fully animatable**: Smooth transitions
- **Discrete**: Step-based changes
- **Non-animatable**: Cannot be animated

### Inheritance
Only text-related and material properties are inherited from parents. Layout and sizing properties are NOT inherited.

## 9.5 USS Transform Properties

Source: `UIE-Transform.html`

Transform operations don't recalculate layout, making them ideal for animations.

**Order**: Scale -> Rotate -> Translate

### transform-origin
```css
transform-origin: center;           /* default */
transform-origin: 0% 100%;
transform-origin: 20px 10px;
transform-origin: top left;
```

### translate
```css
translate: 80%;
translate: 35px;
translate: 5% 10px;
```

### scale
```css
scale: 2.5;
scale: -1 1;     /* flip horizontal */
scale: none;
```

### rotate
```css
rotate: 45deg;
rotate: -100grad;
rotate: -3.14rad;
rotate: 0.75turn;
rotate: none;
```

### C# Implementation
```csharp
element.style.transformOrigin = new TransformOrigin(100, Length.Percent(50));
element.style.translate = new Translate(Length.Percent(10), 50);
element.style.scale = new Scale(new Vector2(0.5f, -1));
element.style.rotate = new Rotate(180);
```

## 9.6 USS Transitions

Source: `UIE-Transitions.html`

### Core Properties

| Property | C# Method | Purpose |
|----------|-----------|---------|
| `transition-property` | `IStyle.transitionProperty` | Which properties animate |
| `transition-duration` | `IStyle.transitionDuration` | Animation time |
| `transition-timing-function` | `IStyle.transitionTimingFunction` | Speed curve |
| `transition-delay` | `IStyle.transitionDelay` | Start delay |

### Basic Example

```css
.labelClass {
    transition-property: color, rotate;
    transition-duration: 2s;
    color: black;
}
.labelClass:hover {
    rotate: 10deg;
    color: red;
}
```

### Shorthand

```css
transition: width 2s ease-out;
transition: margin-right 4s, color 1s;
```

### Timing Functions

Standard: `ease`, `ease-in`, `ease-out`, `ease-in-out`, `linear`
Sine: `ease-in-sine`, `ease-out-sine`, `ease-in-out-sine`
Cubic: `ease-in-cubic`, `ease-out-cubic`, `ease-in-out-cubic`
Circular: `ease-in-circ`, `ease-out-circ`, `ease-in-out-circ`
Elastic: `ease-in-elastic`, `ease-out-elastic`, `ease-in-out-elastic`
Back: `ease-in-back`, `ease-out-back`, `ease-in-out-back`
Bounce: `ease-in-bounce`, `ease-out-bounce`, `ease-in-out-bounce`

### C# Example

```csharp
element.style.transitionProperty = new List<StylePropertyName> { "rotate" };
element.style.transitionDuration = new List<TimeValue> { 2, new(500, TimeUnit.Millisecond) };
element.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.Linear };
element.style.transitionDelay = new List<TimeValue> { 0.5f, new(200, TimeUnit.Millisecond) };
```

### Transition Events
- `TransitionRunEvent` - Transition created
- `TransitionStartEvent` - Delay phase ends
- `TransitionEndEvent` - Transition completes
- `TransitionCancelEvent` - Transition interrupted

### Important Notes
- First frame has no previous state; start transitions AFTER first frame
- Units must match between start and end values
- Set transitions on base state, not pseudo-class states
- Prefer transform properties for performance
- `all` keyword animates all properties (default)
- `none` disables all animations

## 9.7 Apply Styles with C#

Source: `UIE-apply-styles-with-csharp.html`

### Direct style

```csharp
button.style.backgroundColor = Color.red;
```

### Add stylesheet

```csharp
element.styleSheets.Add(styleSheet);
```

### Resolved styles (final computed values)

```csharp
element.RegisterCallback<GeometryChangedEvent>(evt => {
    float height = (evt.target as VisualElement).resolvedStyle.height;
});
```

### Periodic polling

```csharp
element.schedule.Execute(() => {
    Debug.Log(element.resolvedStyle.height);
}).Every(100);
```

## 9.8 Masking Effects

Source: `UIE-masking.html`

### Masking with element

```xml
<VisualElement name="MaskRounded">
    <VisualElement name="Logo" />
</VisualElement>
```

```css
#MaskRounded {
    overflow: hidden;
    border-radius: 50px;
}
```

### Masking with SVG

```css
#MaskSVG {
    overflow: hidden;
    background-image: url("mask.svg");
}
```

Note: Arbitrary shape masking NOT supported in World Space UI.

### Performance
- Rectangle clipping: efficient, preserves batching
- Rounded corners/shapes: stencil masking, may break batches
- Use `UsageHints.MaskContainer` on ancestor to reduce nested mask batch breaks

## 9.9 USS Best Practices

Source: `UIE-USS-WritingStyleSheets.html`

### Avoid inline styles
Inline styles consume more memory per element. Use USS files instead.

### Selector performance
- `:hover` is the main culprit for re-styling
- Complexity: `N1 x N2` (classes on element x applicable USS files)
- Number of elements in hierarchy is the main performance factor

### Complex selector guidelines
- Prefer child selectors (`>`) over descendant selectors
- Avoid universal selectors at end of complex selectors
- Don't use `:hover` on elements with many descendants

### Use BEM convention

```
.menu { }
.menu__item { }
.menu__item--disabled { }
```

- Block: meaningful standalone entity
- Element: part of block, uses `__` separator
- Modifier: appearance/behavior flag, uses `--` separator
- Use single class names for most styling
- Reserve complex selectors for state-dependent styling

### Make elements BEM-friendly

```csharp
AddToClassList("my-block");
childElement.AddToClassList("my-block__child");
```

---

# 10. Control Behavior with Events

Source: `UIE-Events.html`

UI Toolkit events communicate user actions or notifications to visual elements. The event system shares terminology with HTML events.

## Sub-pages

- UIE-Events-Dispatching.html - Dispatch events
- UIE-capture-the-pointer.html - Capture the pointer
- UIE-Events-Handling.html - Handle event callbacks
- UIE-focus-order.html - Focus order
- UIE-events-handling-custom-control.html - Custom control events
- UIE-manipulators.html - Manipulators
- UIE-Events-Synthesizing.html - Synthesize events
- UIE-Events-Reference.html - Event reference

## 10.1 Event Dispatching

Source: `UIE-Events-Dispatching.html`

### Propagation Phases
1. **Trickle-down**: Root down toward target
2. **Target**: Event target receives the event
3. **Bubble-up**: Target back up toward root

### Event Target Properties
- `EventBase.currentTarget` - Element where callback was registered (changes during dispatch)
- `EventBase.target` - Element where event occurred (doesn't change)

### Picking Mode
- `PickingMode.Position` (default) - uses position rectangles
- `PickingMode.Ignore` - prevents pointer event picking
- Override `ContainsPoint()` for custom intersection logic

## 10.2 Handle Event Callbacks

Source: `UIE-Events-Handling.html`

### Register callbacks

```csharp
myElement.RegisterCallback<PointerDownEvent>(MyCallback);
```

### Trickle-down registration

```csharp
myElement.RegisterCallback<PointerDownEvent>(MyCallback, TrickleDown.TrickleDown);
```

### Find child elements

```csharp
var dragContainer = slider.Q("unity-drag-container");
dragContainer.RegisterCallback<PointerUpEvent>(evt => Debug.Log("PointerUpEvent"));
```

### Remove callbacks

```csharp
myElement.UnregisterCallback<EventType>(MyCallback);
```

### Custom data

```csharp
myElement.RegisterCallback<PointerDownEvent, MyType>(MyCallbackWithData, myData);
void MyCallbackWithData(PointerDownEvent evt, MyType data) { }
```

### Value changes

```csharp
// Listen
myIntegerField.RegisterValueChangedCallback(OnIntegerFieldChange);
void OnIntegerFieldChange(ChangeEvent<int> evt) { }

// Set (triggers event)
myControl.value = myNewValue;

// Set without notification
myControl.SetValueWithoutNotify(myNewValue);
```

## 10.3 Focus Order

Source: `UIE-focus-order.html`

Default: depth-first search (DFS) of visual tree.

- `tabIndex = 0` - default order
- `tabIndex > 0` - prioritized ahead of smaller values
- `tabIndex < 0` - excluded from tab navigation
- `focusable` - whether element can receive focus
- `canGrabFocus` - whether currently focusable (considering visibility/enabled)
- `delegatesFocus` - passes focus to suitable child

## 10.4 Manipulators

Source: `UIE-manipulators.html`

Manipulators are state machines for user interaction, handling callback registration/unregistration.

### Classes

| Class | Parent | Purpose |
|-------|--------|---------|
| `Manipulator` | -- | Base class |
| `KeyboardNavigationManipulator` | Manipulator | Keyboard navigation |
| `MouseManipulator` | Manipulator | Mouse input |
| `ContextualMenuManipulator` | MouseManipulator | Right-click menus |
| `PointerManipulator` | MouseManipulator | Pointer input |
| `Clickable` | PointerManipulator | Click detection |

### Dragger Example

```csharp
public class ExampleDragger : PointerManipulator
{
    private Vector3 m_Start;
    protected bool m_Active;
    private int m_PointerId;

    public ExampleDragger()
    {
        m_PointerId = -1;
        activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
        m_Active = false;
    }

    protected override void RegisterCallbacksOnTarget()
    {
        target.RegisterCallback<PointerDownEvent>(OnPointerDown);
        target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
        target.RegisterCallback<PointerUpEvent>(OnPointerUp);
    }

    protected override void UnregisterCallbacksFromTarget()
    {
        target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
        target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
        target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
    }

    protected void OnPointerDown(PointerDownEvent e)
    {
        if (m_Active) { e.StopImmediatePropagation(); return; }
        if (CanStartManipulation(e))
        {
            m_Start = e.localPosition;
            m_PointerId = e.pointerId;
            m_Active = true;
            target.CapturePointer(m_PointerId);
            e.StopPropagation();
        }
    }

    protected void OnPointerMove(PointerMoveEvent e)
    {
        if (!m_Active || !target.HasPointerCapture(m_PointerId)) return;
        Vector2 diff = e.localPosition - m_Start;
        target.style.top = target.layout.y + diff.y;
        target.style.left = target.layout.x + diff.x;
        e.StopPropagation();
    }

    protected void OnPointerUp(PointerUpEvent e)
    {
        if (!m_Active || !target.HasPointerCapture(m_PointerId) || !CanStopManipulation(e)) return;
        m_Active = false;
        target.ReleaseMouse();
        e.StopPropagation();
    }
}
```

### Usage

```csharp
var box = new VisualElement()
{
    style = { left = 100, top = 100, width = 100, height = 100, backgroundColor = Color.red },
    pickingMode = PickingMode.Position,
};
box.AddManipulator(new ExampleDragger());
```

## 10.5 Event Reference

Source: `UIE-Events-Reference.html`

| Category | Purpose |
|----------|---------|
| Capture Events | Capture user interactions |
| Change Events | Element state modified |
| Click Events | Click interactions |
| Command Events | Command invocations |
| Drag and Drop Events | Drag/drop operations |
| Layout Events | Layout engine changes |
| Focus Events | Focus changes |
| Input Events | Text input |
| Keyboard Events | Key presses |
| Mouse Events | Mouse movement |
| Navigation Events | UI navigation |
| Panel Events | Panel interactions |
| Pointer Events | Pointer device interactions |
| Tooltip Events | Tooltip interactions |
| Transition Events | Transition state changes |
| ContextualMenu Events | Context menus |
| IMGUI Events | IMGUI element interactions |

---

# 11. Data Binding

Source: `UIE-data-binding.html`

"A binding refers to the link between the property and the visual control that modifies it."

## Two Binding Systems

| System | Purpose | Use Case |
|--------|---------|----------|
| Runtime Data Binding | Binds plain C# object properties to UI | Runtime + Editor (non-serialized) |
| SerializedObject Data Binding | Binds SerializedObject properties to UI | Editor UI only |

## Sub-pages

- UIE-comparison-binding.html - Comparison of binding systems
- UIE-runtime-binding.html - Runtime data binding
- UIE-editor-binding.html - SerializedObject data binding

## 11.1 Runtime Data Binding

Source: `UIE-runtime-binding.html`

"Runtime data binding binds the properties of any plain C# object to the properties of a UI control."

### Sub-pages

- UIE-get-started-runtime-binding.html - Get started
- UIE-runtime-binding-types.html - Create binding in C#
- UIE-runtime-binding-define-data-source.html - Define data source
- UIE-runtime-binding-mode-update.html - Binding modes and update triggers
- UIE-runtime-binding-data-type-conversion.html - Convert data types
- UIE-runtime-binding-logging-levels.html - Logging levels
- UIE-runtime-binding-custom-types.html - Custom binding types
- UIE-runtime-binding-examples.html - Examples
- UIE-comparison-binding.html - System comparison

---

# 12. Best Practices: Layouts

Source: `best-practice-guides/ui-toolkit-for-advanced-unity-developers/layouts.html`

## Core Runtime Components

- **UI Document Component**: GameObject component defining which UXML displays
- **Panel Settings Asset**: Defines how UI Documents instantiate and render
- **Visual Tree Asset (UXML)**: Hierarchical structure of UI elements

## Responsive Layouts: Flexbox

UI Toolkit uses **Yoga**, an HTML/CSS layout engine implementing a subset of Flexbox.

Advantages:
- Responsive UI across platforms
- Organized complexity via reusable style rules
- Decoupled logic and design

## Position Visual Elements

### Relative Positioning (Default)
- Child elements follow parent's Flexbox rules
- Layout engine handles parent-child conflicts

### Absolute Positioning
- Anchors to parent container
- Ignores flex settings
- Uses Left, Top, Right, Bottom as anchor values
- Good for pop-ups, decorative elements, overlays

## Size Settings

Default in Unity 6: elements have Grow = 1 (occupy all available space).

Properties: Width/Height, Max Width/Max Height, Min Width/Min Height
Units: pixels, percentages, or auto

## Flex Settings

- **Basis**: Default size before Grow/Shrink
- **Grow**: 1 = fill space, 0.5 = half, 0 = no expand
- **Shrink**: 1 = shrink to fit, 0 = maintain size (may overflow)
- **Direction**: row or column
- **Wrap**: nowrap, wrap, wrap-reverse

### Size Calculation Order
1. Compute from Width/Height
2. Check available parent space
3. Distribute extra space to non-zero Grow elements
4. Reduce non-zero Shrink elements if overflow
5. Consider Min-Width, Flex-Basis
6. Apply final resolved size

## Align Settings

- **Align Items**: cross-axis alignment (start, center, end, stretch, auto)
- **Justify Content**: main-axis distribution (flex-start, center, flex-end, space-between, space-around, space-evenly)
- **Align Self**: individual container alignment

## Margin and Padding (Box Model)

- **Content Space**: primary visual elements
- **Padding**: inside border
- **Border**: between padding and margin
- **Margin**: outside border (no effect on absolute elements)

## Measuring Units

- **Auto**: layout system calculates
- **Percentage**: dynamic with parent
- **Pixels**: fixed size
- **Initial**: resets to Unity defaults

### Panel Settings Scale Modes

- **Constant Pixel Size**: fixed, applies Scale Factor
- **Constant Physical Size**: same physical size using Reference DPI
- **Scale with Screen Size**: dynamic based on resolution with Reference Resolution

## UXML as Templates

UXML files function like prefabs. Right-click to create reusable Templates.

---

# 13. Best Practices: Styling

Source: `best-practice-guides/ui-toolkit-for-advanced-unity-developers/styling.html`

## USS Selectors

### Selector Types (Specificity: highest to lowest)
1. **Inline styles** - override all USS
2. **Name/ID selectors** (#id) - blue in UI Builder
3. **Style class selectors** (.className) - dot prefix
4. **Element C# Type selectors** - white, no prefix

### Pseudo-classes
`:active`, `:inactive`, `:hover` - transitions animate automatically

### USS Variables
```css
:root {
    --my-color: #ff0000;
}
```

Types: Float, Color, String, Asset references, Dimensions, Enums

## USS Transitions

### Configuration
- **Property**: what to interpolate (default: all)
- **Duration**: length in seconds/milliseconds
- **Easing Function**: acceleration/deceleration
- **Delay**: wait before start

### Transition Events
- `TransitionRunEvent`, `TransitionStartEvent`, `TransitionEndEvent`, `TransitionCancelEvent`

### Dynamic Style Swapping
```csharp
visualElement.RemoveFromClassList("common");
visualElement.AddToClassList("legendary");
```

## Themes

Theme Style Sheets (TSS) simplify seasonal/alternative color versions.

- Create via **Create > UI Toolkit > TSS theme file**
- Inherit from parent themes (partial customization)
- Reference in Panel Settings' **Theme Style Sheet** field

---

# 14. Best Practices: Custom Controls

Source: `best-practice-guides/ui-toolkit-for-advanced-unity-developers/custom-controls.html`

## The UxmlElement Attribute

```csharp
[UxmlElement]
public partial class ExampleElement : VisualElement
{
    public ExampleElement() { /* initialization */ }
}
```

Controls appear in UI Builder Library under **Custom Controls (C#)**.

Initialize via constructors, or use `AttachToPanelEvent`/`DetachFromPanelEvent` callbacks.

## The UxmlAttribute Attribute

```csharp
[UxmlElement]
public partial class ExampleElement : VisualElement
{
    [UxmlAttribute(name:"my-text")]
    public string myStringValue { get; set; }

    [UxmlAttribute]
    public int myIntValue { get; set; }
}
```

Supports decorator attributes: `Range`, `Tooltip`, `TextArea`, `Header`.

## Slide Toggle Example

```csharp
[UxmlElement]
public partial class SlideToggle : BaseField<bool>
{
    [UxmlAttribute]
    public string EnabledText { get; set; } = "Enabled";

    [UxmlAttribute]
    public string DisabledText { get; set; } = "Disabled";

    [UxmlAttribute]
    public Color EnabledBackgroundColor { get; set; } = new Color(0f, 0.5f, 0.85f, 1f);

    [UxmlAttribute]
    public Color DisabledBackgroundColor { get; set; } = Color.gray;
}
```

### Visual Structure
```csharp
public SlideToggle(string label) : base(label, new VisualElement())
{
    AddToClassList(ussClassName);
    m_Input = this.Q(className: BaseField<bool>.inputUssClassName);
    m_Input.AddToClassList(inputUssClassName);
    m_Input.name = "input";

    m_Knob = new();
    m_Knob.AddToClassList(inputKnobUssClassName);
    m_Knob.name = "knob";
    m_Input.Add(m_Knob);
}
```

### Event Handling
```csharp
RegisterCallback<ClickEvent>(evt => OnClick(evt));
RegisterCallback<KeyDownEvent>(evt => OnKeydownEvent(evt));
```

### Usage
```csharp
SlideToggle slideToggle = root.Q<SlideToggle>("master-audio-toggle");
slideToggle.value = !audioSettings.IsMasterMuted;
slideToggle.RegisterValueChangedCallback(evt =>
    audioSettings.IsMasterMuted = !evt.newValue);
```

---

# 15. Best Practices: Optimizing Performance

Source: `best-practice-guides/ui-toolkit-for-advanced-unity-developers/optimizing-performance.html`

## Update Mechanisms

| Mechanism | Trigger | Impact |
|-----------|---------|--------|
| Style resolution | Class/style changes | Large hierarchies increase cost |
| Layout recalculation | Size/position changes | Frequent updates costly |
| Vertex buffer updates | Geometry changes | Resource-intensive |
| Rendering state changes | Masking, unique textures | Increases CPU overhead |

## Batching

Elements sharing same GPU state batch together. Inserting different element types between similar ones breaks batches. Minimize batch breaks.

## Vertex Buffers

Single vertex buffer per Panel. Exceeding capacity creates additional buffers. Adjust **Vertex Budget** in Panel Settings (default 0 = automatic).

## Uber Shader

Single shader with dynamic branching. Supports up to **8 textures** per batch. Exceeding forces separate batches. Use texture atlases.

## Dynamic Texture Atlases

- **2D Sprite Atlas**: Static, pre-defined content
- **Dynamic Texture Atlas**: Runtime-generated, configure in Panel Settings
- Use `ResetDynamicAtlas` API if atlas fragments

## Masking Performance

- **Rectangular masks**: shader operations, preserves batching, unlimited nesting
- **Rounded corners/complex**: stencil buffer, may break batches, max 7 nested levels
- Use `UsageHints.MaskContainer` for unavoidable nesting

## Animations Best Practices

- **Use transform animations**: `translate`, `scale`, `rotate` (GPU, no layout recalc)
- **Avoid**: `width`, `height`, `top`, `left` (trigger layout recalculations)
- **Usage hints**: `DynamicTransform`, `GroupTransform`
- **Avoid class switching** during animations in large hierarchies

## Runtime Data Binding Optimization

### Property Bags
Default: reflection-based (lazy, runtime overhead).
Optimization: `[GeneratePropertyBag]` attribute for compile-time generation.

```csharp
[GeneratePropertyBag]
public class CarData : ScriptableObject, INotifyBindablePropertyChanged, IDataSourceViewHashProvider
{
    [SerializeField, DontCreateProperty] string _name;

    [CreateProperty]
    public string Name
    {
        get => _name;
        set { _name = value; _version++; Notify(); }
    }

    void Notify([CallerMemberName] string property = "")
    {
        propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
    }

    public long GetViewHashCode() => _version;
}
```

### Change Tracking Interfaces
- `IDataSourceViewHashProvider` - hash-based equality
- `INotifyBindablePropertyChanged` - per-property change notification

## Show/Hide Performance

| Method | Rendering | Layout | Memory | Use Case |
|--------|-----------|--------|--------|----------|
| `opacity = 0` | Rendered | Included | Full | Transitions |
| `visible = false` | Hidden | Included | Stencil | Compromise |
| `display: none` | Hidden | Not updated | Reduced | Frequent toggle |
| `RemoveFromHierarchy()` | Hidden | Not updated | Released | Infrequent |

## Overdraw Mitigation

- Use `display: none` instead of `opacity: 0`
- Remove completely obscured elements
- Use ListView virtualization for scrollable content
- Set `overflow: hidden` to clip

## Memory Management

- USS/UXML load ALL referenced assets immediately
- Use Asset Bundles or Addressables for selective loading
- Break large UXML/USS into smaller modular templates

## Profiling

```csharp
public class PanelChangeReceiver : MonoBehaviour, IDebugPanelChangeReceiver
{
    [SerializeField] PanelSettings m_PanelSettings;

    void Awake() { m_PanelSettings.SetPanelChangeReceiver(this); }
    void OnDestroy() { m_PanelSettings.SetPanelChangeReceiver(null); }

    public void OnVisualElementChange(VisualElement element, VersionChangeType changeType)
    {
        Debug.Log($"{element.name} {changeType}");
    }
}
```

## Unity 6 Performance Enhancements

- Event dispatching: 2x faster
- Mesh generation: jobified, vectorized API now native, parallelized text
- Custom Geometry API: new public API
- Deep Hierarchy Layout: improved caching
- Optimized TreeView: Entities-specific backend

---

# Appendix: Layout Engine Reference

Source: `UIE-LayoutEngine.html`

## Default Behaviors
- Containers distribute children vertically
- Containers include child rectangles
- Text elements include text size

## Key Properties

- `flex-direction: column` (default, vertical) | `row` (horizontal)
- `flex-grow: 1` on multiple children = equal distribution (with `flex-basis: 0`)
- `flex-basis: auto` (content-based, default) | specific value
- `align-items: flex-end` pushes to cross-axis end
- `justify-content: flex-end` pushes to main-axis end

## Absolute Positioning

`position: absolute` removes from Flexbox. Width with both anchors:
```
element-width = parent-width - left-offset - right-offset
```

`left: 0` = zero offset. `left: unset` = other properties define dimensions.

## Layout Examples

### Bottom-Right Button

```xml
<ui:VisualElement name="screen" style="flex-grow: 1; justify-content: flex-end; background-color: blue;">
    <ui:VisualElement name="toolbar" style="align-items: flex-end; background-color: orange;">
        <ui:Button text="Button" />
    </ui:VisualElement>
</ui:VisualElement>
```

### Centered Popup with Overlay

```xml
<ui:VisualElement name="overlay" style="position: absolute; left: 0; top: 0; right: 0; bottom: 0;
    background-color: rgba(0, 0, 0, 0.71); align-items: center; justify-content: center;">
    <ui:VisualElement name="popup" style="background-color: rgba(70, 70, 70, 255);">
        <ui:Label text="Exit?" />
        <ui:Button text="OK" style="width: 108px;" />
    </ui:VisualElement>
</ui:VisualElement>
```

### Best Practice
- Use `.unity-base-field__aligned` class for proper label-control alignment in Editor UI
- Avoid excessive absolute positioning; reserve for overlays/popups

---

# Appendix: Sub-pages Not Fetched (Exist but not retrieved)

These pages exist in the Unity 6.3 documentation but were not fetched due to scope. They may contain additional useful content:

## Structure UI
- UIE-reference-other-files-from-uxml.html - Reference other files from UXML
- UIE-uxml-examples.html - Structure UI examples
- UIE-panels.html - Panels
- UIE-draw-order.html - Draw order
- UIE-relative-absolute-positioning-example.html - Positioning C# example

## USS
- UIE-tss.html - Theme Style Sheet (TSS)
- UIE-USS-PropertyTypes.html - USS property data types
- UIE-uss-color-keywords.html - USS color keywords
- UIB-styling-ui-backgrounds.html - Set background images
- UIE-image-import-settings.html - Image import settings
- ui-systems/ui-shader-graph.html - UI Shader Graph
- ui-systems/create-custom-swirl-filter.html - Custom swirl filter

## USS Selectors (individual pages)
- UIE-USS-Selectors-type.html
- UIE-USS-Selectors-name.html
- UIE-USS-Selectors-class.html
- UIE-USS-Selectors-universal.html
- UIE-USS-Selectors-descendant.html
- UIE-USS-Selectors-child.html
- UIE-USS-Selectors-multiple.html
- UIE-USS-Selectors-list.html
- UIE-uss-selector-precedence.html

## Events
- UIE-capture-the-pointer.html - Capture the pointer
- UIE-events-handling-custom-control.html - Custom control events
- UIE-Events-Synthesizing.html - Synthesize events

## Custom Controls
- ui-systems/custom-control-customize-uxml-tag-names.html
- ui-systems/custom-control-attributes-built-in-types.html
- ui-systems/custom-control-attributes-complex-data-types.html
- ui-systems/custom-control-customize-uxml-attributes.html
- UIE-bind-custom-control-to-data.html
- UIE-define-a-namespace-prefix.html

## Data Binding
- UIE-get-started-runtime-binding.html
- UIE-runtime-binding-types.html
- UIE-runtime-binding-define-data-source.html
- UIE-runtime-binding-mode-update.html
- UIE-runtime-binding-data-type-conversion.html
- UIE-runtime-binding-logging-levels.html
- UIE-runtime-binding-custom-types.html
- UIE-runtime-binding-examples.html
- UIE-comparison-binding.html
- UIE-editor-binding.html

## Text
- UIE-get-started-with-text.html
- UIE-advanced-text-generator.html
- UIE-rich-text-tags.html
- UIE-font-asset-landing.html
- UIE-text-effects.html
- UIE-color-emojis.html

## Runtime UI
- UIE-runtime-performance.html - Performance considerations
- UIE-runtime-examples.html - Runtime examples

## UI Renderer
- UIE-generate-2d-visual-content.html
- UIE-pie-chart.html
- UIE-radial-progress-use-vector-api.html
- UIE-radial-progress.html
- UIE-parallel-tessellation.html

## Migration
- ui-systems/migrate-custom-control.html
- UIE-Transitioning-From-UGUI.html
- UIE-IMGUI-migration.html

## Testing
- UIB-testing-ui.html
- ui-systems/ui-toolkit-live-reload.html
- UIE-ui-debugger.html
- UIE-profiler-markers.html
- ui-systems/event-debugger.html
