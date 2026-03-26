# Unity UI Toolkit Comprehensive Reference Guide
## Unity 6.3 LTS (6000.3)

This document is a comprehensive reference for Unity's UI Toolkit system, compiled from the official Unity 6.3 documentation. It covers all major topics: architecture, UXML markup, USS styling, events, data binding, custom controls, layout, runtime UI, and element references.

---

# Table of Contents

1. [Overview](#1-overview)
2. [UI Systems Comparison](#2-ui-systems-comparison)
3. [Introduction to UI Toolkit](#3-introduction-to-ui-toolkit)
4. [Getting Started](#4-getting-started)
5. [The Visual Tree](#5-the-visual-tree)
6. [Structure UI with UXML](#6-structure-ui-with-uxml)
7. [Reusing UXML Templates](#7-reusing-uxml-templates)
8. [Structure UI with C#](#8-structure-ui-with-c)
9. [Encapsulate UXML with Logic](#9-encapsulate-uxml-with-logic)
10. [Style UI with USS](#10-style-ui-with-uss)
11. [USS Selectors](#11-uss-selectors)
12. [USS Pseudo-Classes](#12-uss-pseudo-classes)
13. [USS Custom Properties (Variables)](#13-uss-custom-properties-variables)
14. [USS Properties Reference](#14-uss-properties-reference)
15. [USS Common Properties Detail](#15-uss-common-properties-detail)
16. [USS Best Practices](#16-uss-best-practices)
17. [Apply Styles with C#](#17-apply-styles-with-c)
18. [USS Transitions](#18-uss-transitions)
19. [Layout Engine (Flexbox)](#19-layout-engine-flexbox)
20. [Events System](#20-events-system)
21. [Event Handling and Callbacks](#21-event-handling-and-callbacks)
22. [Event Types Reference](#22-event-types-reference)
23. [Manipulators](#23-manipulators)
24. [UQuery - Finding Elements](#24-uquery---finding-elements)
25. [Data Binding](#25-data-binding)
26. [Custom Controls](#26-custom-controls)
27. [Runtime UI](#27-runtime-ui)
28. [Editor UI](#28-editor-ui)
29. [UI Renderer](#29-ui-renderer)
30. [Working with Text](#30-working-with-text)
31. [Best Practices for Managing Elements](#31-best-practices-for-managing-elements)
32. [UXML Elements Reference](#32-uxml-elements-reference)
33. [Element Details](#33-element-details)
34. [UI Builder](#34-ui-builder)
35. [Testing and Debugging](#35-testing-and-debugging)
36. [Migration Guides](#36-migration-guides)

---

# 1. Overview

UI Toolkit is a collection of features, resources, and tools for developing user interface (UI) in Unity. It uses a web-inspired approach separating structure (UXML), styling (USS), and logic (C#).

**Core Topics:**
- **UXML** - XML-based markup for UI structure (analogous to HTML)
- **USS** - Unity Style Sheets for visual presentation (analogous to CSS)
- **C# Scripting** - Programmatic control and event handling
- **UI Builder** - Visual editor for UXML/USS files
- **Data Binding** - Connecting properties to UI controls
- **Events** - User interaction handling (input, touch, pointers, drag-and-drop)
- **UI Renderer** - Graphics system built on Unity's device layer

**Additional Resources:**
- Sample projects: Dragon Crashers, QuizU
- E-books on scalable UI design and advanced techniques

---

# 2. UI Systems Comparison

UI Toolkit is intended to become the recommended UI system. However, uGUI and IMGUI remain necessary for certain use cases.

## General Recommendations (Unity 6.3)

| Context | Recommended | Alternative |
|---------|-------------|-------------|
| **Runtime** | uGUI | UI Toolkit |
| **Editor** | UI Toolkit | IMGUI |

## Role-Based Recommendations

| Role | UI Toolkit | uGUI | IMGUI |
|------|-----------|------|-------|
| Programmer | Yes | Yes | Yes |
| Technical Artist | Partial | Yes | No |
| UI Designer | Yes | Partial | No |

## Runtime Features Comparison

| Feature | UI Toolkit | uGUI |
|---------|-----------|------|
| WYSIWYG authoring | Yes | Yes |
| Nesting reusable components | Yes | Yes |
| Layout/Styling Debugger | Yes | Yes |
| In-scene authoring | **No** | Yes |
| Rich text tags | Yes | Yes |
| Scalable text | Yes | Yes |
| Font fallbacks | Yes | Yes |
| Adaptive layout | Yes | Yes |
| Input system support | Yes | Yes |
| Screen-space rendering | Yes | Yes |
| World-space rendering | Yes | Yes |
| Custom materials/shaders | Yes | Yes |
| Sprite support | Yes | Yes |
| Rectangle clipping | Yes | Yes |
| Mask clipping | Yes | Yes |
| Nested masking | Yes | Yes |
| Serialized events | **No** | Yes |
| Animation Clips/Timeline | **No** | Yes |
| Data binding system | Yes | **No** |
| UI transition animations | Yes | **No** |
| Textureless elements | Yes | **No** |
| Advanced flexible layout (Flexbox) | Yes | **No** |
| Global style management | Yes | **No** |
| Dynamic texture atlas | Yes | **No** |
| UI anti-aliasing | Yes | **No** |
| Right-to-left language/emoji | Yes | **No** |
| SVG support | Yes | **No** |

## UI Toolkit Recommended For (Runtime)
- Multi-resolution menus and HUD in intensive UI projects
- World space UI and VR
- UI requiring customized shaders and materials

## uGUI Recommended For (Runtime)
- UI requiring keyframed animations
- Easy referencing from MonoBehaviours

---

# 3. Introduction to UI Toolkit

## Web-Inspired Architecture
UI Toolkit separates concerns similar to web development: UXML for structure, USS for styling, C# for logic.

## Retained Mode
Unlike IMGUI's immediate mode, UI Toolkit maintains a hierarchical structure in memory (the **visual tree**) and handles updates automatically when properties change.

## Visual Tree
An object graph of lightweight nodes that holds all elements in a window or panel.

## Core Systems
- **Layout Engine** - Based on CSS Flexbox for responsive design
- **Event Handling** - Routes events automatically to appropriate visual elements
- **Data Binding** - Links UI element properties to data sources for reactive updates

## Component Library
Standard controls: buttons, toggles, lists, trees, text fields, sliders, dropdowns, etc.

## Development Tools
- **UI Builder** - Visual WYSIWYG editor for UXML/USS
- **UI Debugger** - Real-time inspection tool (like browser dev tools)
- **UI Samples** - Code examples accessible within the Editor

---

# 4. Getting Started

## Creating a Custom Editor Window

1. Create a new Unity project
2. Right-click in Assets > **Create > UI Toolkit > Editor Window**
3. Name the file `SimpleCustomEditor`
4. Keep UXML selected, uncheck USS
5. Open via **Window > UI Toolkit > SimpleCustomEditor**

## Three Methods for Adding UI Controls

### Method A: UI Builder (Visual)
- Open `.uxml` file by double-clicking
- Drag controls from Library > Controls into Hierarchy
- Set properties in Inspector (name, text, label)

### Method B: UXML (Markup)
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements"
    xsi="http://www.w3.org/2001/XMLSchema-instance" engine="UnityEngine.UIElements"
    editor="UnityEditor.UIElements" noNamespaceSchemaLocation="../../UIElementsSchema/UIElements.xsd"
    editor-extension-mode="False">
    <ui:Label text="These controls were created with UXML." />
    <ui:Button text="This is button2" name="button2"/>
    <ui:Toggle label="Number?" name="toggle2"/>
</ui:UXML>
```

Load in C#:
```csharp
var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
    "Assets/Editor/SimpleCustomEditor_uxml.uxml");
VisualElement labelFromUXML = visualTree.Instantiate();
root.Add(labelFromUXML);
```

### Method C: C# (Programmatic)
```csharp
Label label = new Label("These controls were created using C# code.");
root.Add(label);

Button button = new Button();
button.name = "button3";
button.text = "This is button3.";
root.Add(button);

Toggle toggle = new Toggle();
toggle.name = "toggle3";
toggle.label = "Number?";
root.Add(toggle);
```

## Adding Event Handlers
```csharp
public class SimpleCustomEditor : EditorWindow
{
    [SerializeField]
    private VisualTreeAsset m_VisualTreeAsset = default;
    private int m_ClickCount = 0;
    private const string m_ButtonPrefix = "button";

    [MenuItem("Window/UI Toolkit/SimpleCustomEditor")]
    public static void ShowExample()
    {
        SimpleCustomEditor wnd = GetWindow<SimpleCustomEditor>();
        wnd.titleContent = new GUIContent("SimpleCustomEditor");
    }

    public void CreateGUI()
    {
        VisualElement root = rootVisualElement;
        root.Add(m_VisualTreeAsset.Instantiate());
        SetupButtonHandler();
    }

    private void SetupButtonHandler()
    {
        var buttons = rootVisualElement.Query<Button>();
        buttons.ForEach(RegisterHandler);
    }

    private void RegisterHandler(Button button)
    {
        button.RegisterCallback<ClickEvent>(PrintClickMessage);
    }

    private void PrintClickMessage(ClickEvent evt)
    {
        ++m_ClickCount;
        Button button = evt.currentTarget as Button;
        string buttonNumber = button.name.Substring(m_ButtonPrefix.Length);
        Toggle toggle = rootVisualElement.Q<Toggle>("toggle" + buttonNumber);
        Debug.Log("Button clicked!" +
            (toggle.value ? " Count: " + m_ClickCount : ""));
    }
}
```

**Key Concept:** All three approaches (UI Builder, UXML, C#) can be combined in a single UI.

---

# 5. The Visual Tree

## Core Concepts

The fundamental building block is the **VisualElement** -- a node that can be styled, configured with behavior, and displayed. Elements organize hierarchically forming the **visual tree**.

**Root Elements:**
- Editor UI: `EditorWindow.rootVisualElement`
- Runtime UI: `UIDocument.rootVisualElement`

## Customization
- **Styling**: Inline styles and stylesheets
- **Behavior**: Event callbacks and scripted logic

## Built-In Controls
Specialized subclasses extend VisualElement:
- **Button** - Clickable action trigger
- **Toggle** - Checkbox (on/off switch), combines label + box + checkmark
- **Text Fields** - User text entry
- And many more (see Element Reference section)

---

# 6. Structure UI with UXML

## UXML Format
UXML is Unity's markup format inspired by HTML, XAML, and XML.

### Document Structure
```xml
<?xml version="1.0" encoding="utf-8"?>
<engine:UXML xmlns:engine="UnityEngine.UIElements"
             xmlns:editor="UnityEditor.UIElements">
    <!-- UI elements here -->
</engine:UXML>
```

### Namespace Prefixes
- `UnityEngine.UIElements` - Runtime elements (commonly prefixed as `ui:` or `engine:`)
- `UnityEditor.UIElements` - Editor-only elements (commonly prefixed as `uie:` or `editor:`)

Example: `xmlns:ui="UnityEngine.UIElements"` allows `<ui:Button />`.

### Base Attributes (inherited by all elements)
| Attribute | Description |
|-----------|-------------|
| `name` | Unique element identifier (used in USS `#name` selectors and UQuery) |
| `picking-mode` | Controls mouse event responsiveness (`Position` or `Ignore`) |
| `tabindex` | Defines tab order for keyboard navigation |
| `focusable` | Boolean for keyboard focus capability |
| `class` | Space-separated USS class names for styling/selection |
| `tooltip` | Hover text display |
| `view-data-key` | Serialization key for persisting view state |
| `style` | Inline USS styles |
| `enabled` | Local enabled state |

### Schema Validation
Generate updated schema files via **Assets > Update UXML Schema** to validate UXML structure.

## Adding Styles to UXML

### Inline Styles
```xml
<ui:VisualElement style="width: 200px; height: 200px; background-color: red;" />
```

### External Stylesheet Reference
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <Style src="styles.uss" />
    <ui:VisualElement name="root" />
</ui:UXML>
```

### Path Formats for USS References
- **Absolute**: `/Assets/myFolder/myFile.uss` or `project://database/Assets/myFolder/myFile.uss`
- **Relative**: `../myFolder/myFile.uss`
- **Package**: `/Packages/com.unity.package.name/file-name.uss`

---

# 7. Reusing UXML Templates

## Template and Instance Syntax

Define a reusable template (`Portrait.uxml`):
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:VisualElement class="portrait">
        <ui:Image name="portraitImage" style="--unity-image: url(a.png)"/>
        <ui:Label name="nameLabel" text="Name"/>
        <ui:Label name="levelLabel" text="42"/>
    </ui:VisualElement>
</ui:UXML>
```

Import and use:
```xml
<ui:Template src="Portrait.uxml" name="Portrait"/>
<ui:Instance template="Portrait" name="player1"/>
<ui:Instance template="Portrait" name="player2"/>
```

## Attribute Overrides

Override default values per instance:
```xml
<ui:Instance name="player1" template="PlayerTemplate">
    <ui:AttributeOverrides element-name="player-name-label" text="Alice" />
    <ui:AttributeOverrides element-name="player-score-label" text="2" />
</ui:Instance>
```

Override multiple attributes in one declaration:
```xml
<ui:AttributeOverrides element-name="player-name-label"
    text="Alice" tooltip="Tooltip 1" />
```

**Nested template precedence**: The shallowest override takes precedence.

## Style Overrides
Cannot use `AttributeOverrides` on inline `style` attributes. Instead:
1. Remove inline styles from the template
2. Assign unique names to instances
3. Use USS selectors to target specific instances:
```css
#ReversedHotkeys > #Container {
    flex-direction: row-reverse;
}
```

## Content Container
Use `content-container` attribute to specify where child elements nest:
```xml
<ui:VisualElement name="parent-container" content-container="anyValue">
    <!-- Child elements added here -->
</ui:VisualElement>
```

Only one element per template may have `content-container`.

## Limitations
- Cannot override `class`, `name`, or `style` attributes
- Data binding does not work with attribute overrides
- Cannot use USS selectors or UQuery for matching override elements

---

# 8. Structure UI with C#

All UI elements can be created and managed programmatically:

```csharp
var myElement = new VisualElement();
myElement.Add(new Label("Hello World"));
myElement.Add(new TextField());
myElement.style.backgroundColor = Color.blue;
myElement.style.width = 200;
myElement.style.height = 200;
```

**Key access points:**
- Editor: `EditorWindow.rootVisualElement` in `CreateGUI()`
- Runtime: `GetComponent<UIDocument>().rootVisualElement` in `OnEnable()`

---

# 9. Encapsulate UXML with Logic

Create reusable UI components by combining UXML structure, USS styling, and C# logic.

## UXML-First Approach
Best for fixed UI structures. Define child elements in the UXML:

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

UXML:
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <Style src="CardElementUI.uss" />
    <CardElement>
        <ui:VisualElement name="image" />
        <ui:VisualElement name="stats">
            <ui:Label name="attack-badge" class="badge" />
            <ui:Label name="health-badge" class="badge" />
        </ui:VisualElement>
    </CardElement>
</ui:UXML>
```

## Element-First Approach
Best for flexible structures. Load hierarchy from C#:

```csharp
[UxmlElement]
public partial class CardElement : VisualElement
{
    public CardElement() {}

    public CardElement(Texture2D image, int health, int attack)
    {
        var asset = Resources.Load<VisualTreeAsset>("CardElement");
        asset.CloneTree(this);

        this.Q("image").style.backgroundImage = image;
        this.Q<Label>("attack-badge").text = attack.ToString();
        this.Q<Label>("health-badge").text = health.ToString();
    }
}
```

## Instantiation in Parent UXML
```xml
<ui:Template name="CardElement" src="CardElement.uxml"/>
<ui:Instance template="CardElement"/>
<ui:Instance template="CardElement"/>
```

## Instantiation in C#
```csharp
// Element-first
foreach(Card card in GetCards())
{
    var cardElement = new CardElement(card.image, card.health, card.attack);
    cardElement.RegisterCallback<ClickEvent>(SomeInteraction);
    document.rootVisualElement.Add(cardElement);
}
```

---

# 10. Style UI with USS

## What is USS?
USS (Unity Style Sheet) is a text-based asset file (`.uss` extension) that defines styling for UI elements. It separates appearance from structure and logic.

## Basic Syntax
```css
selector {
    property1: value;
    property2: value;
}
```

## Selector Types Overview
- **Type selectors**: `Button { ... }` - match C# element types
- **Class selectors**: `.my-class { ... }` - match USS class assignments
- **Name selectors**: `#my-name { ... }` - match named elements
- **Universal selectors**: `* { ... }` - match any element
- **Descendant selectors**: `parent child { ... }` - match nested elements
- **Child selectors**: `parent > child { ... }` - match direct children only

## Rules
- Selectors must begin with letters or underscores
- Support alphanumeric characters, hyphens, underscores
- Case-sensitive
- Special characters require backslash escaping

---

# 11. USS Selectors

## Simple Selectors

### Type Selector
Matches elements by C# type name:
```css
Button {
    background-color: blue;
}
```

### Name Selector
Matches by element `name` attribute (prefix with `#`):
```css
#my-button {
    background-color: red;
}
```

### Class Selector
Matches by USS class (prefix with `.`):
```css
.highlight {
    background-color: yellow;
}
```

### Universal Selector
Matches any element:
```css
* {
    margin: 5px;
}
```

## Complex Selectors

### Descendant Selector
Matches elements nested anywhere inside another (space-separated):
```css
.container Label {
    color: white;
}
```

### Child Selector
Matches only direct children (use `>`):
```css
.container > Label {
    color: white;
}
```

### Multiple Selectors (Compound)
Combine criteria on the same element (no space):
```css
Button.highlighted#submit {
    background-color: green;
}
```

### Selector List
Comma-separated selectors sharing the same rules:
```css
Button, Toggle, Label {
    font-size: 14px;
}
```

---

# 12. USS Pseudo-Classes

Pseudo-classes narrow a selector's scope to match elements in specific states.

## Supported Pseudo-Classes

| Pseudo-class | Matches When |
|---|---|
| `:hover` | Cursor over the element |
| `:active` | User interacts with Button, RadioButton, or Toggle |
| `:inactive` | User stops interacting |
| `:focus` | Element has focus |
| `:disabled` | Element disabled |
| `:enabled` | Element enabled |
| `:checked` | Toggle or RadioButton selected |
| `:root` | Highest-level element with the stylesheet |
| `:selected` | Not supported; use `:checked` |

## Syntax
```css
Button:hover {
    background-color: palegreen;
}
```

## Chaining
```css
Toggle:checked:hover {
    background-color: yellow;
}
```

## The `:root` Pseudo-Class

Set inherited defaults and custom properties:
```css
:root {
    --my-color: #ff0000;
    font-size: 14px;
}
Button {
    background-color: var(--my-color);
}
```

`:root` matches the element the stylesheet attaches to, not necessarily the topmost element.

---

# 13. USS Custom Properties (Variables)

Variables simplify management when multiple rules share the same values.

## Syntax
```css
/* Declaration */
:root {
    --color-1: blue;
    --color-2: yellow;
}

/* Usage */
.paragraph-regular {
    color: var(--color-1);
    background: var(--color-2);
    padding: 2px;
}

.paragraph-reverse {
    color: var(--color-2);
    background: var(--color-1);
    padding: 2px;
}
```

## Default Values
```css
color: var(--color-1, #FF0000);
```

## Limitations vs CSS
- **No nested functions**: `rgb(var(--red), 0, 0)` is NOT supported
- **No math operations**: Cannot do calculations with variables

---

# 14. USS Properties Reference

Complete list of all USS properties with inheritance and animatability:

### Layout Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `align-content` | No | Discrete | Alignment of children on cross axis (multi-line) |
| `align-items` | No | Discrete | Alignment of children on cross axis |
| `align-self` | No | Discrete | Self-alignment override |
| `flex` | No | Fully | Shorthand for flex-grow, flex-shrink, flex-basis |
| `flex-basis` | No | Fully | Initial main size |
| `flex-direction` | No | Discrete | Main axis direction |
| `flex-grow` | No | Fully | Growth factor |
| `flex-shrink` | No | Fully | Shrink factor |
| `flex-wrap` | No | Discrete | Multi-line placement |
| `justify-content` | No | Discrete | Main axis justification |

### Sizing Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `width` | No | Fully | Fixed width |
| `height` | No | Fully | Fixed height |
| `min-width` | No | Fully | Minimum width |
| `min-height` | No | Fully | Minimum height |
| `max-width` | No | Fully | Maximum width |
| `max-height` | No | Fully | Maximum height |
| `aspect-ratio` | No | Fully | Preferred aspect ratio |

### Position Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `position` | No | Discrete | `relative` or `absolute` |
| `left` | No | Fully | Left offset |
| `top` | No | Fully | Top offset |
| `right` | No | Fully | Right offset |
| `bottom` | No | Fully | Bottom offset |

### Spacing Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `margin` | No | Fully | Shorthand for all margins |
| `margin-left/top/right/bottom` | No | Fully | Individual margins |
| `padding` | No | Fully | Shorthand for all padding |
| `padding-left/top/right/bottom` | No | Fully | Individual padding |

### Background Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `background-color` | No | Fully | Background color |
| `background-image` | No | Discrete | Background image |
| `background-position` | No | Fully | Image position |
| `background-repeat` | No | Discrete | Image repeat |
| `background-size` | No | Fully | Image size |

### Border Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `border-color` | No | Fully | Shorthand for all border colors |
| `border-width` | No | Fully | Shorthand for all border widths |
| `border-radius` | No | Fully | Shorthand for all corner radii |
| `border-left/top/right/bottom-color` | No | Fully | Individual border colors |
| `border-left/top/right/bottom-width` | No | Fully | Individual border widths |
| `border-top-left/top-right/bottom-left/bottom-right-radius` | No | Fully | Individual corner radii |

### Text Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `color` | Yes | Fully | Text color |
| `font-size` | Yes | Fully | Font size in points |
| `-unity-font` | Yes | Discrete | Font object |
| `-unity-font-definition` | Yes | Discrete | FontDefinition (takes precedence over -unity-font) |
| `-unity-font-style` | Yes | Discrete | `normal`, `italic`, `bold`, `bold-and-italic` |
| `-unity-text-align` | Yes | Discrete | Text alignment |
| `white-space` | Yes | Discrete | Word wrap mode |
| `text-overflow` | No | Discrete | Overflow mode (`clip`, `ellipsis`) |
| `text-shadow` | Yes | Fully | Drop shadow |
| `letter-spacing` | Yes | Fully | Character spacing |
| `word-spacing` | Yes | Fully | Word spacing |
| `-unity-paragraph-spacing` | Yes | Fully | Paragraph spacing |
| `-unity-text-outline` | No | Fully | Outline width and color |
| `-unity-text-outline-width` | Yes | Fully | Outline width |
| `-unity-text-outline-color` | Yes | Fully | Outline color |
| `-unity-text-overflow-position` | No | Discrete | `start`, `middle`, `end` |
| `-unity-text-auto-size` | Yes | Non-anim | Auto-scale text within bounds |
| `-unity-text-generator` | Yes | Non-anim | `standard` or `advanced` |

### Transform Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `rotate` | No | Fully | Rotation transformation |
| `scale` | No | Fully | Scaling transformation |
| `translate` | No | Fully | Translation transformation |
| `transform-origin` | No | Fully | Transform pivot point |

### Visual Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `opacity` | No | Fully | Element transparency |
| `visibility` | Yes | Discrete | `visible` or `hidden` |
| `display` | No | Non-anim | `flex` or `none` |
| `overflow` | No | Discrete | `hidden` or `visible` |
| `cursor` | No | Non-anim | Mouse cursor style |
| `filter` | No | Fully | Filter effects |

### Transition Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `transition` | No | Non-anim | Shorthand |
| `transition-property` | No | Non-anim | Which properties transition |
| `transition-duration` | No | Non-anim | Animation duration |
| `transition-timing-function` | No | Non-anim | Easing function |
| `transition-delay` | No | Non-anim | Start delay |

### Unity-Specific Properties
| Property | Inherited | Animatable | Description |
|----------|-----------|------------|-------------|
| `-unity-background-image-tint-color` | No | Fully | Background image tint |
| `-unity-background-scale-mode` | No | Discrete | Image scaling mode |
| `-unity-material` | Yes | Fully | Custom material |
| `-unity-overflow-clip-box` | No | Discrete | Clipping box |
| `-unity-slice-left/top/right/bottom` | No | Fully | 9-slice edges |
| `-unity-slice-scale` | No | Fully | Slice scale |
| `-unity-slice-type` | No | Discrete | `sliced` or `tiled` |

---

# 15. USS Common Properties Detail

## Box Model
**IMPORTANT**: USS uses `border-box` model -- width/height include padding and borders.

### Dimensions
```css
width: 200px;
height: auto;
min-width: 100px;
max-width: 50%;
aspect-ratio: 16 / 9;
```
Values: `<length>`, `<percentage>`, `auto`

### Margins
```css
margin: 10px;                    /* all sides */
margin: 10px 20px;              /* vertical horizontal */
margin: 10px 20px 30px 40px;   /* top right bottom left */
margin-left: auto;               /* auto-center */
```

### Borders
```css
border-width: 2px;
border-color: white;
border-radius: 10px;
border-top-left-radius: 5px;
```
Note: No elliptical corners. Values clamped to half element size.

### Padding
```css
padding: 10px 20px;
```

## Flex Layout

### Container Properties
```css
flex-direction: row | row-reverse | column | column-reverse;
flex-wrap: nowrap | wrap | wrap-reverse;
align-items: flex-start | flex-end | center | stretch;
align-content: flex-start | flex-end | center | stretch;
justify-content: flex-start | flex-end | center | space-between | space-around;
```
Default: `flex-direction: column`, `align-items: stretch`

### Item Properties
```css
flex-grow: 1;
flex-shrink: 0;
flex-basis: auto | 0 | 100px;
flex: 1 0 auto;           /* shorthand: grow shrink basis */
align-self: auto | flex-start | flex-end | center | stretch;
```

## Positioning
```css
position: relative;    /* default, participates in layout */
position: absolute;    /* removed from layout, positioned relative to parent */
left: 10px;
top: 20px;
right: 0;
bottom: 0;
```

## Background
```css
background-color: #FF0000;
background-color: rgba(255, 0, 0, 0.5);
background-image: url("path/to/image.png");
background-image: resource("image-name");
-unity-background-scale-mode: stretch-to-fill | scale-and-crop | scale-to-fit;
-unity-background-image-tint-color: white;
```

## 9-Slice
```css
-unity-slice-left: 10;
-unity-slice-top: 10;
-unity-slice-right: 10;
-unity-slice-bottom: 10;
-unity-slice-scale: 2px;
-unity-slice-type: sliced | tiled;
```

## Text
```css
color: white;
font-size: 14px;
-unity-font-style: normal | italic | bold | bold-and-italic;
-unity-text-align: upper-left | middle-center | lower-right;
/* Full values: upper-left | middle-left | lower-left | upper-center |
   middle-center | lower-center | upper-right | middle-right | lower-right */
white-space: normal | nowrap | pre | pre-wrap;
text-overflow: clip | ellipsis;
-unity-text-overflow-position: start | middle | end;
text-shadow: 2px 2px 4px rgba(0,0,0,0.5);  /* x y blur color */
letter-spacing: 2px;
word-spacing: 5px;
-unity-paragraph-spacing: 10px;
-unity-text-outline: 1px black;
-unity-text-auto-size: none | best-fit 8 24;  /* min max */
```

## Display & Visibility
```css
display: flex;       /* visible, participates in layout */
display: none;       /* hidden, removed from layout */
visibility: visible;
visibility: hidden;  /* invisible but still in layout */
overflow: visible;
overflow: hidden;    /* clips children */
-unity-overflow-clip-box: padding-box | content-box;
opacity: 0.5;
```

## Cursor
```css
/* Editor keywords */
cursor: arrow | text | resize-vertical | resize-horizontal | link |
        slide-arrow | resize-up-right | resize-up-left | move-arrow |
        rotate-arrow | scale-arrow | arrow-plus | arrow-minus |
        pan | orbit | zoom | fps | split-resize-up-down | split-resize-left-right;

/* Runtime: use textures */
cursor: url("cursor.png") 0 0;
```

## Material
```css
-unity-material: resource("MyMaterial");
```

---

# 16. USS Best Practices

## Avoid Inline Styles
Use USS files instead of inline styles. Inline styles are per-element and cause memory overhead.

## Selector Performance
- `:hover` is the main culprit for re-styling performance issues
- Performance decreases linearly as classes are added to elements
- Complexity is `N1 x N2` (N1 = classes on element, N2 = applicable USS files)

## Complex Selector Guidelines
- Prefer child selectors (`>`) over descendant selectors
- Avoid universal selectors (`*`) at end of complex selectors
- Avoid `:hover` on elements with many descendants (mouse movements invalidate entire hierarchy)

## Use BEM Methodology
Block Element Modifier naming convention:
- **Block**: standalone entity (e.g., `menu`)
- **Element**: double underscore separator (e.g., `menu__item`)
- **Modifier**: double hyphen separator (e.g., `menu--disabled`)

```css
.menu { }
.menu__item { }
.menu__item--active { }
.menu--vertical { }
```

This enables styling with only one class name in most cases.

## Making Custom Elements BEM-Friendly
```csharp
public class MyElement : VisualElement
{
    public static readonly string ussClassName = "my-element";
    public static readonly string labelUssClassName = ussClassName + "__label";

    public MyElement()
    {
        AddToClassList(ussClassName);
        var label = new Label();
        label.AddToClassList(labelUssClassName);
        Add(label);
    }
}
```

---

# 17. Apply Styles with C#

## Setting Inline Styles
```csharp
button.style.backgroundColor = Color.red;
button.style.width = 200;
button.style.height = new Length(50, LengthUnit.Percent);
```

**Note**: Layout-related properties (`top`, `left`, `width`, `height`) are calculated by the layout engine. Direct assignment may not override calculated values.

## Adding Stylesheets
```csharp
var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/styles.uss");
// Or: Resources.Load<StyleSheet>("styles");
element.styleSheets.Add(styleSheet);
```

Style rules apply to the element and ALL its descendants, but NOT to parents or siblings.

## Reading Resolved Styles
`style` property = inline styles only. `resolvedStyle` = final calculated values.

```csharp
// Using GeometryChangedEvent
element.RegisterCallback<GeometryChangedEvent>(evt =>
{
    float height = element.resolvedStyle.height;
    Debug.Log("Resolved height: " + height);
});

// Using scheduler
element.schedule.Execute(() =>
{
    Debug.Log(element.resolvedStyle.height);
}).Every(100);
```

---

# 18. USS Transitions

Transitions animate property value changes over time, similar to CSS transitions.

## Core Properties
| Property | Purpose |
|----------|---------|
| `transition-property` | Which USS properties to transition |
| `transition-duration` | Animation duration |
| `transition-timing-function` | Easing function |
| `transition-delay` | Start delay |

## USS Syntax
```css
/* Longhand */
.label {
    transition-property: color, rotate;
    transition-duration: 2s;
    color: black;
}
.label:hover {
    rotate: 10deg;
    color: red;
}

/* Shorthand: property duration timing-function delay */
transition: width 2s ease-out;
transition: margin-right 4s, color 1s;
```

## Duration Values
```css
transition-duration: 2s;
transition-duration: 800ms;
transition-duration: 3s, 1500ms, 1.75s;
```

## Timing Functions
24 easing options available:
- `ease`, `ease-in`, `ease-out`, `ease-in-out`, `linear`
- Specialized: `ease-in-sine`, `ease-out-elastic`, `ease-in-bounce`, `ease-in-out-cubic`, etc.

Default: `ease`

## C# Implementation
```csharp
// Set transition property
element.style.transitionProperty = new List<StylePropertyName> { "rotate" };

// Set duration
element.style.transitionDuration = new List<TimeValue> {
    new TimeValue(2f, TimeUnit.Second),
    new TimeValue(500f, TimeUnit.Millisecond)
};

// Set timing function
element.style.transitionTimingFunction = new List<EasingFunction> {
    EasingMode.Linear
};

// Set delay
element.style.transitionDelay = new List<TimeValue> {
    0.5f,
    new TimeValue(200, TimeUnit.Millisecond)
};
```

## Important Notes
- First frame has no previous state -- start transitions after the first frame
- For animated properties with units, ensure units match (e.g., use `0px` not just `0`)
- Prefer transitions on USS transform properties (`rotate`, `scale`, `translate`) for performance
- Other properties may cause layout recalculations
- Multiple property lists repeat/truncate to match `transition-property` length

## Keywords
- `all`: Default; applies to all properties
- `initial`: Reset to defaults
- `none`: Disable transitions
- `ignored`: Ignore transition parameters

## Animatability Categories
- **Fully animatable**: Smooth transitions with easing
- **Discrete**: Single-step value change (no interpolation)
- **Non-animatable**: Cannot transition

---

# 19. Layout Engine (Flexbox)

UI Toolkit uses the **Yoga** layout engine, implementing a subset of CSS Flexbox.

## Default Behaviors
1. Containers distribute children **vertically** by default (`flex-direction: column`)
2. Container rectangles include their children
3. Text-containing elements incorporate text size in calculations

## Flex Direction
```css
flex-direction: column;          /* top to bottom (default) */
flex-direction: row;             /* left to right */
flex-direction: column-reverse;  /* bottom to top */
flex-direction: row-reverse;     /* right to left */
```

## Flex Grow & Basis
```css
/* Equal distribution ignoring content size */
.child {
    flex-grow: 1;
    flex-basis: 0;
}

/* Content-based with equal remaining space */
.child {
    flex-grow: 1;
    flex-basis: auto;
}
```

## Alignment
```css
/* Cross-axis alignment */
align-items: flex-start | flex-end | center | stretch;

/* Main-axis distribution */
justify-content: flex-start | flex-end | center | space-between | space-around;
```

## Absolute Positioning
```css
/* Removes element from layout flow */
position: absolute;
left: 0;
top: 0;
right: 0;
bottom: 0;
```

When both `left` and `right` are set: `computed-width = parent-width - left - right`

**`left: 0`** means zero offset applied. **`left: unset`** means no offset.

## Common Patterns

### Button in Bottom-Right (Flexbox)
```xml
<ui:VisualElement style="flex-grow: 1; justify-content: flex-end; background-color: blue;">
    <ui:VisualElement style="align-items: flex-end; background-color: orange;">
        <ui:Button text="Button" />
    </ui:VisualElement>
</ui:VisualElement>
```

### Button in Bottom-Left (Absolute)
```xml
<ui:VisualElement style="flex-grow: 1;">
    <ui:VisualElement style="position: absolute; left: 0; bottom: 0;">
        <ui:Button text="Button" />
    </ui:VisualElement>
</ui:VisualElement>
```

### Modal Overlay / Popup
```xml
<ui:VisualElement name="overlay"
    style="position: absolute; left: 0; top: 0; right: 0; bottom: 0;
           background-color: rgba(0,0,0,0.71); align-items: center;
           justify-content: center;">
    <ui:VisualElement name="popup"
        style="background-color: rgba(70,70,70,255);">
        <ui:Label text="Exit?" />
        <ui:Button text="OK" style="width: 108px;" />
    </ui:VisualElement>
</ui:VisualElement>
```

## Best Practices
1. Set explicit `width` and `height` for fixed sizing
2. Use `flex-grow` for responsive sizing
3. Use `flex-direction: row` for horizontal layouts
4. Use relative positioning for layout-aware offsets
5. Reserve absolute positioning for overlays and dropdowns
6. Use `BaseField` elements with `.unity-base-field__aligned` for Editor UI alignment

---

# 20. Events System

UI Toolkit provides an event system for communicating user actions, using terminology from HTML events.

## Event Dispatch
The EventDispatcher listens for OS events and routes them to visual elements.

## Propagation Phases

Events follow a three-step propagation:

1. **Trickle-down**: Path descends from root toward target
2. **Target**: Event reaches the target element
3. **Bubble-up**: Path ascends back toward root

**Hidden/disabled elements** don't receive events, but events still propagate to their ancestors and descendants.

## Key Properties
- **`EventBase.target`**: Element where event originated (e.g., element under pointer)
- **`EventBase.currentTarget`**: Element where the current callback was registered

## Picking Mode
- **`PickingMode.Position`** (default): Uses position rectangle for event targeting
- **`PickingMode.Ignore`**: Element transparent to pointer events

Override `VisualElement.ContainsPoint()` for custom hit testing.

---

# 21. Event Handling and Callbacks

## Registering Callbacks
```csharp
// Default: executes during target and bubble-up phases
myElement.RegisterCallback<PointerDownEvent>(MyCallback);

// Trickle-down: parent responds before children
myElement.RegisterCallback<PointerDownEvent>(MyCallback, TrickleDown.TrickleDown);

// With custom data
myElement.RegisterCallback<PointerDownEvent, MyType>(MyCallbackWithData, myData);

// Unregister
myElement.UnregisterCallback<PointerDownEvent>(MyCallback);
```

**Constraint**: Same callback registered only once per event type and propagation phase.

## Value Change Handling
```csharp
// Getting values
int val = myIntegerField.value;

// Listening for changes
myField.RegisterValueChangedCallback(evt =>
{
    Debug.Log($"Changed from {evt.previousValue} to {evt.newValue}");
});

// Setting value (triggers ChangeEvent)
myControl.value = newValue;

// Silent change (no event)
myControl.SetValueWithoutNotify(newValue);
```

---

# 22. Event Types Reference

| Category | Description |
|----------|-------------|
| **Capture events** | Pointer/mouse capture |
| **Change events** | Element state changes |
| **Click events** | Click interactions |
| **Command events** | User commands |
| **Drag and drop events** | Drag operations |
| **Layout events** | Layout changes |
| **Focus events** | Focus changes |
| **Input events** | Text input |
| **Keyboard events** | Key presses |
| **Mouse events** | Mouse movement |
| **Navigation events** | UI navigation |
| **Panel events** | Panel interactions |
| **Pointer events** | Pointer device interactions |
| **Tooltip events** | Tooltip interactions |
| **Transition events** | CSS transition state changes |
| **ContextualMenu events** | Context menu |
| **IMGUI events** | IMGUI element interactions |

All event types are built on the `EventBase` class hierarchy.

**Common events for controls:**
- `ClickEvent` - Button clicks
- `ChangeEvent<T>` - Value changes on fields/toggles
- `PointerDownEvent`, `PointerMoveEvent`, `PointerUpEvent` - Pointer interactions
- `KeyDownEvent`, `KeyUpEvent` - Keyboard input
- `FocusInEvent`, `FocusOutEvent` - Focus changes
- `GeometryChangedEvent` - Layout/size changes
- `AttachToPanelEvent`, `DetachFromPanelEvent` - Panel lifecycle
- `NavigationSubmitEvent` - Enter/submit key
- `TransitionEndEvent` - Transition completion

---

# 23. Manipulators

Manipulators are **state machines** that encapsulate event-handling logic for user interactions.

## Supported Classes

| Class | Inherits From | Purpose |
|-------|--------------|---------|
| `Manipulator` | -- | Base class |
| `KeyboardNavigationManipulator` | Manipulator | Keyboard navigation |
| `MouseManipulator` | Manipulator | Mouse input with activation filters |
| `ContextualMenuManipulator` | MouseManipulator | Right-click context menus |
| `PointerManipulator` | MouseManipulator | Pointer input with filters |
| `Clickable` | PointerManipulator | Click detection (press + release) |

## Usage
```csharp
// Attach
myElement.AddManipulator(new MyDragger());

// Detach
myElement.RemoveManipulator(manipulator);
```

## Implementation Pattern
```csharp
public class ExampleDragger : PointerManipulator
{
    public ExampleDragger()
    {
        activators.Add(new ManipulatorActivationFilter
        {
            button = MouseButton.LeftMouse
        });
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

    private void OnPointerDown(PointerDownEvent evt) { /* capture, start */ }
    private void OnPointerMove(PointerMoveEvent evt) { /* update position */ }
    private void OnPointerUp(PointerUpEvent evt) { /* release capture */ }
}
```

---

# 24. UQuery - Finding Elements

UQuery provides jQuery/LINQ-inspired methods for searching the visual tree.

## Core Methods
- **`Q()`**: Returns first matching element
- **`Query()`**: Returns all matching elements

## Query by Name
```csharp
VisualElement el = root.Q("my-element");
List<VisualElement> all = root.Query("OK").ToList();
VisualElement second = root.Query("OK").AtIndex(1);
```

## Query by USS Class
```csharp
List<VisualElement> items = root.Query(className: "yellow").ToList();
VisualElement first = root.Q(className: "yellow");
```

## Query by Type
```csharp
Button btn = root.Q<Button>();
Button third = root.Query<Button>().AtIndex(2);
```

## With Predicates
```csharp
var filtered = root.Query(className: "yellow")
    .Where(elem => elem.tooltip == "").ToList();
```

## Complex Queries
```csharp
// Combine name + class + type
var el = root.Query<Button>(className: "yellow", name: "OK").First();

// Hierarchical
var child = root.Query<VisualElement>("container")
    .Children<Button>("Cancel").First();
```

## Filtering Methods
- `First()`, `Last()`, `AtIndex(int)`
- `Children<T>()` - Direct descendants only
- `Where(predicate)` - Custom filter
- `ForEach(action)` - Iterate results

## Best Practices
1. Cache query results at initialization
2. Traverse manually using `.parent` chain for ancestor queries
3. Reuse `QueryState` to avoid repeated list creation
4. Enable incremental GC when creating/releasing many elements

---

# 25. Data Binding

Data binding synchronizes properties between non-UI objects and UI controls.

## Two Systems

### Runtime Data Binding
- Binds plain C# object properties to UI controls
- Works in both runtime and Editor UI
- For any C# objects as data sources

### SerializedObject Data Binding
- Binds SerializedObject properties to UI controls
- Editor UI only
- Better support for undo/redo and multi-selection

## Basic Binding Path (Editor)
```xml
<ui:TextField label="Name" binding-path="m_Name" />
```

```csharp
// In a custom Editor
var nameField = new TextField("Name");
nameField.bindingPath = "m_Name";
rootVisualElement.Add(nameField);
rootVisualElement.Bind(serializedObject);
```

---

# 26. Custom Controls

## Requirements
1. Apply `[UxmlElement]` attribute
2. Declare as `partial class`
3. Inherit from `VisualElement` or a derived class

```csharp
using UnityEngine.UIElements;

[UxmlElement]
public partial class ExampleElement : VisualElement
{
    public ExampleElement() { }
}
```

The control then appears in UXML documents and UI Builder's Library tab. Use `[HideInInspector]` to hide from the Library.

## Initialization
VisualElements don't receive MonoBehaviour lifecycle callbacks. Use:
- **Constructor**: Immediate initialization
- **AttachToPanelEvent**: When element joins the UI hierarchy
- **DetachFromPanelEvent**: When removed from UI

```csharp
[UxmlElement]
public partial class MyControl : VisualElement
{
    public MyControl()
    {
        RegisterCallback<AttachToPanelEvent>(OnAttach);
    }

    private void OnAttach(AttachToPanelEvent evt)
    {
        // Safe to query children, styles are resolved
    }
}
```

## Best Practices
- Use unique namespaces
- Keep UXML attributes primitive types
- Expose functional aspects as UXML properties
- Expose visual aspects as USS properties
- Consider using UXML templates vs. building hierarchy in code

---

# 27. Runtime UI

## Setup Steps

### 1. Create UXML Document
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:VisualElement style="flex-grow: 1;">
        <ui:Label text="This is a Label" />
        <ui:Button text="This is a Button" name="button"/>
        <ui:Toggle label="Display the counter?" name="toggle"/>
        <ui:TextField label="Text Field" text="filler text" name="input-message" />
    </ui:VisualElement>
</ui:UXML>
```

### 2. Create UIDocument in Scene
**GameObject > UI Toolkit > UI Document**

This creates:
- UIDocument component on a GameObject
- Panel Settings asset
- Runtime theme asset

Drag the UXML file into the UIDocument's **Source Asset** field.

### 3. Create MonoBehaviour Controller
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

        var inputField = uiDocument.rootVisualElement.Q("input-message");
        inputField.RegisterCallback<ChangeEvent<string>>(InputMessage);
    }

    private void OnDisable()
    {
        _button.UnregisterCallback<ClickEvent>(PrintClickMessage);
    }

    private void PrintClickMessage(ClickEvent evt)
    {
        ++_clickCount;
        Debug.Log($"Button clicked!" +
            (_toggle.value ? " Count: " + _clickCount : ""));
    }

    public static void InputMessage(ChangeEvent<string> evt)
    {
        Debug.Log($"{evt.newValue} -> {evt.target}");
    }
}
```

### 4. Attach script to UIDocument GameObject

**Key Notes:**
- UXML is instantiated by UIDocument during `OnEnable`, so register callbacks there
- Use `rootVisualElement.Q()` to find controls by name
- Unregister callbacks in `OnDisable`

---

# 28. Editor UI

## Custom Editor Windows
```csharp
public class MyEditorWindow : EditorWindow
{
    [MenuItem("Window/My Window")]
    public static void ShowWindow()
    {
        GetWindow<MyEditorWindow>("My Window");
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        // Add UI elements here
    }
}
```

## Custom Inspectors
```csharp
[CustomEditor(typeof(MyComponent))]
public class MyComponentEditor : Editor
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();
        // Build inspector UI
        return root;
    }
}
```

## Key Capabilities
- Custom editor windows
- Custom inspectors
- Default inspectors
- SerializedObject data binding
- ViewData persistence (state across domain reloads)
- Drag-and-drop support

---

# 29. UI Renderer

The UI Renderer is a rendering system built on Unity's graphics device layer.

## Features
- **Mesh API**: Direct mesh generation for custom graphics
- **Vector API**: Vector graphics rendering
- **Parallel tessellation**: Performance optimization for large meshes

## Use Cases
- Pie charts
- Radial progress indicators
- Custom visual controls
- Vector graphics in UI

---

# 30. Working with Text

## Core Text Controls
- **Label**: Read-only text display (uses `text` attribute)
- **TextElement**: Base class for text elements
- **TextField**: Editable text input (uses `value` attribute)

## Styling Text with USS
```css
Label {
    color: white;
    font-size: 16px;
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    text-shadow: 1px 1px 2px rgba(0,0,0,0.5);
    letter-spacing: 1px;
}
```

## Rich Text
Enable with `enable-rich-text="true"`:
```xml
<ui:Label text="&lt;b&gt;Bold&lt;/b&gt; and &lt;i&gt;italic&lt;/i&gt;" enable-rich-text="true" />
```

## Features
- Rich text tags for inline formatting
- Font asset conversion
- Color gradients (up to four colors per character)
- Right-to-left language support
- Fallback fonts for missing characters
- Sprite assets for emoji
- Advanced Text Generator for Unicode support and text shaping

---

# 31. Best Practices for Managing Elements

## Element Pooling
Reuse elements rather than creating new ones with `new()`. Custom pools require full cleanup (unregister callbacks, reset state).

## Use ListView for Large Collections
ListView automatically pools and recycles elements during scrolling.

## Hiding Elements: Performance Comparison

### `visibility: hidden`
- Layout preserved, styles evaluated for propagation
- Rendering commands removed
- **Best balance**: frees rendering commands, preserves layout

### `opacity: 0`
- One-time CPU cost, but GPU processes vertices every frame
- GPU-intensive with many vertices

### `display: none`
- Removed from layout tree entirely
- May recompute sibling layout
- No GPU cost

### `translate: -5000px -5000px` (with DynamicTransform usage hint)
- Minimal CPU cost when returning
- GPU still processes vertices

### `RemoveFromHierarchy()`
- Complete CPU/GPU memory liberation
- Expensive to recreate
- Best for elements not needed soon

## Summary
Only `RemoveFromHierarchy()` fully frees all resources. `visibility: hidden` offers the best balance for temporarily hidden elements.

---

# 32. UXML Elements Reference

## Base Elements
- **VisualElement** - Foundation container for all UI
- **BindableElement** - Base for data-bindable elements

## Input Fields
| Element | Description |
|---------|-------------|
| `TextField` | Text input |
| `IntegerField` | Integer input |
| `FloatField` | Float input |
| `DoubleField` | Double input |
| `LongField` | Long integer input |
| `UnsignedIntegerField` | Unsigned integer input |
| `UnsignedLongField` | Unsigned long input |
| `Vector2Field` | 2D vector input |
| `Vector2IntField` | 2D integer vector input |
| `Vector3Field` | 3D vector input |
| `Vector3IntField` | 3D integer vector input |
| `Vector4Field` | 4D vector input |

## Specialized Fields
| Element | Description |
|---------|-------------|
| `BoundsField` | Bounds input |
| `BoundsIntField` | Integer bounds input |
| `RectField` | Rectangle input |
| `RectIntField` | Integer rectangle input |
| `Hash128Field` | Hash128 input |
| `ColorField` | Color picker (Editor only) |
| `CurveField` | Animation curve (Editor only) |
| `GradientField` | Gradient editor (Editor only) |

## Selection Controls
| Element | Description |
|---------|-------------|
| `Button` | Clickable button |
| `Toggle` | Checkbox on/off |
| `RadioButton` | Single-select radio button |
| `RadioButtonGroup` | Group of radio buttons |
| `DropdownField` | Drop-down selection |
| `EnumField` | Enum drop-down |
| `EnumFlagsField` | Flags enum multi-select |
| `Slider` | Float slider |
| `SliderInt` | Integer slider |
| `MinMaxSlider` | Range slider |

## Container Elements
| Element | Description |
|---------|-------------|
| `Box` | Bordered container |
| `GroupBox` | Logical group with title |
| `ScrollView` | Scrollable container |
| `Scroller` | Scrollbar control |
| `TwoPaneSplitView` | Resizable split panes |
| `TemplateContainer` | Template instance container |
| `Foldout` | Collapsible section |
| `TabView` | Tab container |
| `Tab` | Individual tab |

## Display Elements
| Element | Description |
|---------|-------------|
| `Label` | Read-only text |
| `TextElement` | Base text element |
| `Image` | Graphical display |
| `ProgressBar` | Progress indicator |
| `HelpBox` | Info/warning/error box |

## List/Tree Elements
| Element | Description |
|---------|-------------|
| `ListView` | Virtualized list |
| `MultiColumnListView` | Multi-column list |
| `TreeView` | Virtualized tree |
| `MultiColumnTreeView` | Multi-column tree |

## Editor-Only Controls
| Element | Description |
|---------|-------------|
| `ObjectField` | Asset/object reference |
| `PropertyField` | Serialized property |
| `LayerField` | Layer selection |
| `LayerMaskField` | Layer mask |
| `TagField` | Tag selection |
| `MaskField` | Bitmask field |
| `InspectorElement` | Full inspector |

## Toolbar Elements (Editor)
| Element | Description |
|---------|-------------|
| `Toolbar` | Toolbar container |
| `ToolbarButton` | Toolbar button |
| `ToolbarToggle` | Toolbar toggle |
| `ToolbarMenu` | Toolbar dropdown menu |
| `ToolbarSearchField` | Search field |
| `ToolbarPopupSearchField` | Search with popup |
| `ToolbarBreadcrumbs` | Breadcrumb navigation |
| `ToolbarSpacer` | Spacing element |

## Templates
| Element | Description |
|---------|-------------|
| `Template` | Reference to external UXML |
| `Instance` | Instantiation of template |
| `AttributeOverrides` | Override template attributes |

## Column Definitions
| Element | Description |
|---------|-------------|
| `Columns` | Column container |
| `Column` | Column definition |

---

# 33. Element Details

## Button
```xml
<ui:Button name="my-btn" text="Click me" icon-image="/path/to/icon.png" />
```

```csharp
var button = new Button(() => Debug.Log("Clicked")) { text = "Click me" };
button.iconImage = Resources.Load<Texture2D>("icon");
```

**USS Classes:** `.unity-button`, `.unity-button--with-icon`, `.unity-button--with-icon-only`

**Icon positioning** via `flex-direction`:
- `row` (default) - icon left of text
- `row-reverse` - icon right
- `column` - icon above
- `column-reverse` - icon below

## Label
```xml
<ui:Label text="Hello World" name="my-label" />
```

```csharp
var label = new Label("Hello World");
label.style.fontSize = 14;
```

**Attributes:** `text`, `selectable`, `enable-rich-text`, `parse-escape-sequences`
**USS Classes:** `.unity-label`, `.unity-text-element`

## Toggle
```xml
<ui:Toggle label="Enable feature" name="my-toggle" value="true" />
```

```csharp
var toggle = new Toggle("Enable feature");
toggle.value = true;
toggle.RegisterValueChangedCallback(evt => Debug.Log(evt.newValue));
```

**Attributes:** `label`, `value`, `text`, `toggle-on-label-click`
**USS Classes:** `.unity-toggle`, `.unity-toggle__label`, `.unity-toggle__input`, `.unity-toggle__checkmark`

Styling checked state:
```css
.my-toggle:checked > .unity-toggle__input > .unity-toggle__checkmark {
    background-image: url("checked.png");
}
```

## TextField
```xml
<ui:TextField label="Name" name="name-field" value="Default" placeholder-text="Enter name..." />
```

```csharp
var textField = new TextField();
textField.label = "Name";
textField.multiline = true;
textField.maxLength = 140;
textField.isDelayed = true;  // Only updates on Enter or blur
```

**Key Attributes:** `multiline`, `value`, `placeholder-text`, `hide-placeholder-on-focus`, `is-delayed`, `max-length`, `password`, `mask-character`, `readonly`
**USS Classes:** `.unity-text-field`, `.unity-text-field__input`, `.unity-base-text-field__input--single-line`, `.unity-base-text-field__input--multiline`

**USS Custom Properties:**
```css
.unity-base-text-field__input {
    --unity-cursor-color: yellow;
    --unity-selection-color: rgba(0,0,255,0.3);
}
```

## ListView
```xml
<ui:ListView class="my-list" fixed-item-height="20" />
```

```csharp
var listView = new ListView();
listView.makeItem = () => new Label();
listView.bindItem = (element, index) => ((Label)element).text = items[index];
listView.itemsSource = items;
listView.selectionType = SelectionType.Multiple;
listView.reorderable = true;
```

**Key Attributes:** `fixed-item-height`, `virtualization-method` (FixedHeight/DynamicHeight), `reorderable`, `selection-type`, `show-add-remove-footer`, `show-foldout-header`, `show-alternating-row-backgrounds`

**Refresh Methods:**
- `RefreshItems()` / `RefreshItem(index)` - Update visible items (preferred)
- `Rebuild()` - Complete reconstruction (use sparingly)

**USS Classes:** `.unity-list-view`, `.unity-list-view__item`, `.unity-collection-view__item--selected`

**Limitation:** No horizontal virtualization.

## ScrollView
```xml
<ui:ScrollView mode="VerticalAndHorizontal">
    <!-- content -->
</ui:ScrollView>
```

**Modes:** Vertical (default), Horizontal, VerticalAndHorizontal
**Scroller visibility:** Auto (default), AlwaysVisible, Hidden

**Text wrapping inside ScrollView:**
```css
#unity-content-container {
    flex-direction: row;
    flex-wrap: wrap;
}
```
**USS Classes:** `.unity-scroll-view`, `.unity-scroll-view__content-container`

## Foldout
```xml
<ui:Foldout text="Settings" value="true">
    <ui:Slider label="Volume" low-value="0" high-value="100" />
    <ui:Toggle label="Mute" />
</ui:Foldout>
```

```csharp
var foldout = new Foldout { text = "Settings" };
foldout.Add(new Slider("Volume", 0, 100));
foldout.RegisterValueChangedCallback(e =>
    Debug.Log(e.newValue ? "Expanded" : "Collapsed"));
```

**USS Classes:** `.unity-foldout`, `.unity-foldout__toggle`, `.unity-foldout__content`

## DropdownField
```xml
<ui:DropdownField label="Option" name="dropdown" />
```

```csharp
var dropdown = new DropdownField(
    new List<string> { "Option 1", "Option 2", "Option 3" }, 0);
dropdown.choices.Add("Option 4");
dropdown.index = 1;
dropdown.RegisterValueChangedCallback(evt => Debug.Log(evt.newValue));
```

**USS Classes:** `.unity-popup-field`, `.unity-base-popup-field__text`, `.unity-base-popup-field__arrow`

## Slider
```xml
<ui:Slider label="Volume" low-value="0" high-value="100" value="50" show-input-field="true" />
```

```csharp
var slider = new Slider();
slider.lowValue = 0;
slider.highValue = 100;
slider.value = 50;
slider.direction = SliderDirection.Horizontal;
```

**Page-size behavior:** When 0, clicking track moves thumb to cursor. When >0, incremental movement.
**USS Classes:** `.unity-slider`, `.unity-base-slider__dragger`

## ProgressBar
```xml
<ui:ProgressBar title="Loading" low-value="0" high-value="100" value="42" />
```

**Percentage:** `100 * (value - lowValue) / (highValue - lowValue)`
**USS Classes:** `.unity-progress-bar`, `.unity-progress-bar__progress`, `.unity-progress-bar__background`, `.unity-progress-bar__title`

## Image
```xml
<ui:Image source="Resources/image.png" scale-mode="ScaleToFit" tint-color="white" />
```

```csharp
var image = new Image();
image.image = Resources.Load<Texture2D>("image");
image.scaleMode = ScaleMode.ScaleToFit;
```

**USS Custom Properties:** `--unity-image`, `--unity-image-size`, `--unity-image-tint-color`

**Image vs backgroundImage:** Use Image when you want the image to drive the element's size. Use `style.backgroundImage` for decoration without affecting layout.

## GroupBox
```xml
<ui:GroupBox text="Group Title">
    <ui:RadioButton text="Option A" />
    <ui:RadioButton text="Option B" />
</ui:GroupBox>
```

**USS Classes:** `.unity-group-box`, `.unity-group-box__label`

## RadioButton / RadioButtonGroup
```xml
<ui:RadioButtonGroup label="Choice" choices="Option A,Option B,Option C" value="0" />
```

```csharp
var group = new RadioButtonGroup("Options",
    new List<string> { "A", "B", "C" });
group.RegisterValueChangedCallback(evt => Debug.Log(evt.newValue));
```

## TabView / Tab
```xml
<ui:TabView>
    <ui:Tab label="Tab A">
        <ui:Label text="Content A" />
    </ui:Tab>
    <ui:Tab label="Tab B">
        <ui:Label text="Content B" />
    </ui:Tab>
</ui:TabView>
```

**Attributes:** `reorderable` (TabView), `closeable` (Tab)
**USS Classes:** `.unity-tab-view`, `.unity-tab-view__content-container`

## TwoPaneSplitView
```xml
<ui:TwoPaneSplitView fixed-pane-index="0" fixed-pane-initial-dimension="200" orientation="Horizontal">
    <ui:VisualElement name="left-pane" />
    <ui:VisualElement name="right-pane" />
</ui:TwoPaneSplitView>
```

Requires exactly two child elements.

## VisualElement (Base)
```xml
<ui:VisualElement name="container" class="my-class"
    style="flex-grow: 1; background-color: blue;">
    <!-- children -->
</ui:VisualElement>
```

```csharp
var el = new VisualElement();
el.name = "container";
el.AddToClassList("my-class");
el.style.flexGrow = 1;
el.style.backgroundColor = Color.blue;
```

**Key Attributes:** `name`, `class`, `style`, `enabled`, `picking-mode`, `focusable`, `tabindex`, `tooltip`, `view-data-key`, `usage-hints`, `data-source`, `data-source-path`, `content-container`, `language-direction`

---

# 34. UI Builder

UI Builder is a visual WYSIWYG editor for creating and editing `.uxml` and `.uss` files.

## Core Features
- Drag-and-drop element placement
- Visual property editing
- USS selector creation and editing
- Template instance management
- Real-time preview

## Optional Package Extensions
- **Vector Graphics** (`com.unity.vectorgraphics`): Assign vector images as background
- **2D Sprite** (`com.unity.2d.sprite`): Assign sprite assets as background, access Sprite Editor

## Workflow
1. Create UI Document (UXML) or StyleSheet (USS)
2. Drag elements from Library into Hierarchy
3. Set attributes in Inspector
4. Position and style elements
5. Test with the built-in preview

---

# 35. Testing and Debugging

## Available Tools

### UI Builder Testing
Debug styles and test directly within UI Builder.

### Live Reload
View changes to UI immediately in both Editor and runtime.

### UI Toolkit Debugger
Inspect and debug elements in real-time. Access layout, styles, and hierarchy information.

### Event Debugger
Inspect and troubleshoot UI events in UI Toolkit.

### Profiler Markers
CPU/GPU performance analysis for UI rendering.

### UI Test Framework
Write and run automated tests for UI elements (external package).

---

# 36. Migration Guides

## Available Guides

### From uGUI to UI Toolkit
Key differences:
- uGUI uses GameObjects/Components; UI Toolkit uses VisualElement tree
- uGUI uses Canvas; UI Toolkit uses UIDocument + PanelSettings
- uGUI uses RectTransform; UI Toolkit uses USS/Flexbox layout
- uGUI has in-scene authoring; UI Toolkit uses UXML files

### From IMGUI to UI Toolkit
Key differences:
- IMGUI is immediate mode (redraw every frame); UI Toolkit is retained mode
- IMGUI uses `OnGUI()`; UI Toolkit uses `CreateGUI()`
- IMGUI requires manual layout; UI Toolkit uses automatic Flexbox

### Custom Controls to Unity 6
Compare conventional custom control authoring with the revamped Unity 6 workflow using `[UxmlElement]`.

---

# Quick Reference: Common Patterns

## Minimum Runtime UI Setup
1. Create `.uxml` file with UI structure
2. Add UIDocument GameObject to scene
3. Assign UXML to UIDocument's Source Asset
4. Create MonoBehaviour with `OnEnable()` that queries `rootVisualElement`

## Minimum Editor Window
```csharp
public class MyWindow : EditorWindow
{
    [MenuItem("Window/My Window")]
    static void Open() => GetWindow<MyWindow>();

    void CreateGUI()
    {
        rootVisualElement.Add(new Label("Hello"));
    }
}
```

## Common USS Reset
```css
:root {
    --primary: #2196F3;
    --bg: #1a1a2e;
    --text: #eee;
}

* {
    margin: 0;
    padding: 0;
}
```

## Responsive Container
```css
.container {
    flex-grow: 1;
    flex-direction: column;
    padding: 10px;
}

.row {
    flex-direction: row;
    justify-content: space-between;
    align-items: center;
}
```

## Common UXML Header
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements"
    editor-extension-mode="False">
    <Style src="styles.uss" />
    <!-- UI content -->
</ui:UXML>
```

---

*Document compiled from Unity 6.3 LTS (6000.3) official documentation at docs.unity3d.com*
