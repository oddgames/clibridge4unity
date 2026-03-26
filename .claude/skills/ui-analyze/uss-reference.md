# USS Property Reference (Unity 6)

Complete list of supported USS properties for Unity 6000.3+.
Use this when converting HTML/CSS to UXML/USS — if a CSS property isn't listed here, it's NOT supported.

## Supported USS Properties

### Dimensions
width, height, min-width, min-height, max-width, max-height, aspect-ratio

### Margins (longhand required in practice)
margin-top, margin-bottom, margin-left, margin-right
Shorthand: `margin: <length>{1,4}`

### Padding (longhand required in practice)
padding-top, padding-bottom, padding-left, padding-right
Shorthand: `padding: <length>{1,4}`

### Border Width
border-top-width, border-bottom-width, border-left-width, border-right-width
Shorthand: `border-width: <length>{1,4}`

### Border Color
border-top-color, border-bottom-color, border-left-color, border-right-color
Shorthand: `border-color: <color>{1,4}`

### Border Radius
border-top-left-radius, border-top-right-radius, border-bottom-left-radius, border-bottom-right-radius
Shorthand: `border-radius: <length>{1,4}`

### Flex Container
- `flex-direction: row | row-reverse | column | column-reverse` (default: column)
- `flex-wrap: nowrap | wrap | wrap-reverse`
- `align-content: flex-start | flex-end | center | stretch`
- `align-items: auto | flex-start | flex-end | center | stretch`
- `justify-content: flex-start | flex-end | center | space-between | space-around`

### Flex Item
- `flex-grow: <number>` (default: 0)
- `flex-shrink: <number>` (default: 1)
- `flex-basis: <length> | auto`
- `align-self: auto | flex-start | flex-end | center | stretch`
Shorthand: `flex: none | [<grow> <shrink>? || <basis>]`

### Position
- `position: absolute | relative` (default: relative)
- `top, right, bottom, left: <length> | auto`

### Display & Overflow
- `display: flex | none` (only these two values!)
- `overflow: hidden | visible`

### Background
- `background-color: <color>`
- `background-image: <resource> | url("path") | none`
- `background-position: <position>`
- `background-position-x, background-position-y: <length>`
- `background-repeat: repeat | no-repeat | repeat-x | repeat-y`
- `background-size: <size>`
- `-unity-background-scale-mode: stretch-to-fill | scale-and-crop | scale-to-fit`
- `-unity-background-image-tint-color: <color>`

### 9-Slice
- `-unity-slice-left, -unity-slice-top, -unity-slice-right, -unity-slice-bottom: <integer>`
- `-unity-slice-scale: <length>`
- `-unity-slice-type: sliced | tiled`

### Text
- `color: <color>` (inherited)
- `font-size: <number>` (inherited)
- `-unity-font: <resource> | url("path")` (inherited)
- `-unity-font-definition: <resource> | url("path")` (inherited)
- `-unity-font-style: normal | italic | bold | bold-and-italic` (inherited)
- `-unity-text-align: upper-left | middle-left | lower-left | upper-center | middle-center | lower-center | upper-right | middle-right | lower-right` (inherited)
- `white-space: normal | nowrap | pre | pre-wrap` (inherited)
- `text-overflow: clip | ellipsis`
- `-unity-text-overflow-position: start | middle | end`
- `letter-spacing: <length>` (inherited)
- `word-spacing: <length>` (inherited)
- `-unity-paragraph-spacing: <length>` (inherited)
- `-unity-text-outline-width: <length>`
- `-unity-text-outline-color: <color>`
- `text-shadow: <x-offset> <y-offset> <blur-radius> <color>` (inherited)
- `-unity-text-auto-size: none | best-fit <min> <max>`

### Transform
- `rotate: <angle>` (e.g., `rotate: 45deg`)
- `scale: <number> <number>` (e.g., `scale: 0.5 0.5`)
- `translate: <length> <length>` (e.g., `translate: 10px 20px`)
- `transform-origin: <position>`

### Transition (animation)
- `transition-property: <property-name>`
- `transition-duration: <time>` (e.g., `0.3s`)
- `transition-delay: <time>`
- `transition-timing-function: ease | ease-in | ease-out | ease-in-out | linear`
Shorthand: `transition: <property> <duration> <timing> <delay>`

### Filter (Unity 6+)
- `filter: <filter-function>` — check Unity docs for supported functions

### Visual
- `opacity: <number>` (0-1)
- `visibility: visible | hidden` (inherited)
- `cursor: <cursor-type>`
- `-unity-material: <resource> | url("path") | none`

### CSS Variables
```
:root { --my-color: #ff0000; }
.element { color: var(--my-color); }
```

### Pseudo-classes
`:hover`, `:active`, `:focus`, `:disabled`, `:enabled`, `:checked`, `:root`

## NOT Supported in USS
- `display: grid`, `display: inline`, `display: block` — only `flex` and `none`
- `grid-*` properties — no CSS grid at all
- `clip-path` — use 9-slice sprites instead
- `box-shadow` — use border or overlay elements
- `backdrop-filter` — not available
- `text-transform` — handle in C# or hardcode uppercase in UXML
- `z-index` — use element order (later in UXML = on top)
- `float`, `clear` — not available
- `::before`, `::after` — no pseudo-elements, create real child elements
- `linear-gradient()` in `background-image` — unreliable, use solid `background-color`
- `radial-gradient()` — not supported
- `@media` queries — not supported
- `@keyframes` / `animation` — use `transition` or C# animation instead
- `box-sizing` — always content-box equivalent
- `line-height` — use padding or `-unity-paragraph-spacing`
- `gap` — use margins on children
- `user-select` — not available
- `pointer-events` — use `picking-mode: ignore` instead

Sources:
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-USS-SupportedProperties.html
- https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-USS-Properties-Reference.html
