---
description: Analyze a UI reference image, generate a verified HTML recreation, convert to UXML/USS, and set up a playable Unity scene with controller input. Full pipeline from screenshot to interactive UI.
---

Analyze and recreate the UI from: $ARGUMENTS

## Phase 1: Run the analysis script (Gemini: HTML + visual verification)

Run the UI analysis script. It performs 7 local detection steps (OCR, color palette, fonts, spacing, gradients, icons, structure), then calls Gemini 3.1 Pro to:
1. Generate an HTML/CSS recreation
2. Self-verify by rendering the HTML and comparing to the reference (up to 3 iterations, keeps best score, reverts regressions)

```bash
python .claude/scripts/ui_analyze.py "IMAGE_PATH" "CONTEXT"
```

- Replace `IMAGE_PATH` with the image path from the arguments
- Replace `CONTEXT` with any additional context from the arguments. If none given, use "console HUD settings menu"
- Use a 600000ms timeout (Gemini calls can take several minutes)

After the script runs, you'll have:
- `*_recreation.html` — Best-scoring HTML/CSS recreation
- `*_analysis.json` — Machine-readable analysis data

## Phase 2: Convert HTML to UXML/USS (Claude)

Gather context before converting — read ALL of these:

**UI Toolkit references (what's possible):**
1. **Contents index** — read `.claude/skills/ui-analyze/CONTENTS.md` to find the right reference files for your task
2. **USS reference** — read `.claude/skills/ui-analyze/uss-reference.md` for supported USS properties. Only use properties listed there.
3. **UXML reference** — read `.claude/skills/ui-analyze/uxml-reference.md` for available elements, attributes, and syntax
4. **Element attributes** — look up specific elements in `.claude/skills/ui-analyze/uxml-elements-full.md`

**Source material (what to build):**
5. **Reference image** — look at the original image (from $ARGUMENTS) to understand the visual layout, proportions, and spatial relationships
6. **HTML recreation** — read the `*_recreation.html` file for the full HTML/CSS structure
7. **Analysis JSON** — read the `*_analysis.json` for machine-detected data: color palette (exact hex values), font sizes, panel positions/sizes, spacing patterns, text content, and image dimensions

Use this combined context to produce the UXML and USS. The USS reference tells you what's possible, the HTML gives you element structure, the analysis JSON gives you precise measurements and colors, and the reference image resolves any ambiguity about layout proportions.

### Conversion rules
- `div` → `ui:VisualElement`, text-containing `span`/`div`/`p` → `ui:Label` with `text` attribute
- CSS `display: flex` → USS `flex-direction: row` or `column` (USS defaults to column)
- CSS `display: grid` → convert to nested flex containers (USS has no grid)
- `font-weight: bold` → `-unity-font-style: bold`
- `font-style: italic` → `-unity-font-style: italic`
- `text-align` → `-unity-text-align: middle-left | middle-center | middle-right`
- `gap: Npx` → `margin-top/left` on children
- `border: 1px solid #color` → longhand `border-top-width`, `border-top-color`, etc.
- `border-radius: Npx` → longhand per-corner `border-top-left-radius`, etc.
- `padding: Npx` → longhand `padding-top`, `padding-bottom`, `padding-left`, `padding-right`
- `margin: Npx` → longhand `margin-top`, etc.
- `background: linear-gradient(...)` → use solid `background-color` (midpoint of gradient). USS gradient support is unreliable
- `clip-path`, `transform: skew()`, `box-shadow`, `text-shadow`, `backdrop-filter`, `filter` → NOT supported, remove (add comment suggesting 9-slice sprites)
- `text-transform: uppercase` → NOT supported in USS, manually uppercase text in UXML
- Emoji/unicode icons → replace with `VisualElement` boxes styled as icon placeholders (Unity fonts lack emoji glyphs)
- Scrollable lists → `ui:ScrollView` with `horizontal-scroller-visibility="Hidden"` `vertical-scroller-visibility="Auto"`
- `z-index` → NOT supported, use element order in UXML (later = on top)
- `::before` / `::after` pseudo-elements → create actual child `VisualElement` elements in UXML
- `:hover` pseudo-class → supported in USS, keep for interactive elements
- CSS variables `var(--name)` → supported in USS via `:root { --name: value; }`, keep them

### Layout approach
- Use **flex-based layout** that fills the viewport (`flex-grow: 1`), NOT fixed pixel dimensions
- The screen DPI varies — hardcoded `width: 1920px` will overflow on high-DPI displays
- Structure as: top bar (fixed height) → middle content (flex-grow: 1) → bottom bar (fixed height)
- Use `flex-grow` ratios for proportional sizing of panels
- Use `width: 48%` or similar percentages for left/right splits

### Required attributes
- Add `name=""` attributes on key elements for C# binding (e.g., `name="TopBar"`, `name="PanelStory"`, `name="BottomBar"`)
- Add `focusable="true"` on interactive/clickable elements
- Add `:hover` and `.selected` pseudo/classes for interactive elements

### USS file
- Reference as `<Style src="{Name}.uss" />`
- Use longhand properties only (no shorthand)
- Add `.selected` class with yellow border for controller navigation highlight
- Use CSS variables in `:root` for theme colors

Write the UXML and USS files directly into `Assets/UI/{MenuName}/`.

## Phase 3: Render and verify in Unity

1. Run `clibridge4unity SCREENSHOT Assets/UI/{MenuName}/{Name}.uxml` to render it
2. If the render viewport clips the layout (DPI scaling), check `EditorGUIUtility.pixelsPerPoint` and adjust
3. Verify all major sections are visible: top bar, middle content, bottom bar

## Phase 4: Compare with reference using Gemini

Use gemini_compare.py to compare the Unity render against the original reference:
```bash
python .claude/scripts/gemini_compare.py "REFERENCE_IMAGE" "UNITY_SCREENSHOT" "Compare these UIs. List the top 5 USS property changes needed to make the recreation match the reference more closely. For each, give the exact USS selector and property."
```

Apply any USS fixes Gemini suggests, re-screenshot, and iterate until satisfied.

## Phase 5: Code review — production-ready templates

The UXML/USS must be a **functional production template** that C# can fully drive. A developer or AI should be able to say "hook up player stats" or "populate this list from an API" and the template should make that trivial. Review and fix ALL of these:

### 5a. Dynamic data binding — every data-driven element needs a name
Every label, value, image, or container that displays **data** (not static UI chrome) MUST have a `name=""` attribute so C# can query and update it.

Naming convention — **PascalCase**, descriptive, prefixed by section:
- Container for a stat: `name="StatHealth"`, `name="StatSpeed"`
- Label showing a value: `name="ValueHealth"`, `name="ValuePlayerName"`
- Icon/image placeholder: `name="IconWeapon"`, `name="IconAvatar"`
- Progress/bar fill: `name="BarHealth"`, `name="BarXP"`
- Section titles: `name="HeaderStats"`, `name="HeaderInventory"`

### 5b. Lists and ScrollViews — template row pattern
Any list of repeating items (settings rows, inventory slots, leaderboard entries, etc.):

1. Keep **exactly 1 example row** in the UXML as the template. Add class `template-row` and `style="display:none"` to hide it at runtime
2. The **container** (ScrollView or VisualElement) gets a name: `name="RowContainer"` or `name="ItemList"`
3. C# will: query the template, clone it for each data item, set names/text/classes on the clone, add to container
4. Add a USS comment documenting the template: `/* Template: clone .template-row for each item, set .row-label and .row-value text */`
5. Each element inside the template row that holds data gets a **class** (not name — names must be unique per clone): `.row-label`, `.row-value`, `.row-icon`

Example UXML pattern:
```xml
<ui:ScrollView name="SettingsList">
  <!-- Template: clone for each setting, set .setting-label and .setting-value -->
  <ui:VisualElement class="row template-row" style="display:none">
    <ui:Label class="setting-label" text="TEMPLATE" />
    <ui:Label class="setting-value" text="VALUE" />
  </ui:VisualElement>
</ui:ScrollView>
```

### 5c. USS theme variables — every color and spacing as a variable
```css
:root {
    /* Colors */
    --color-bg-primary: #1a1a1a;
    --color-bg-secondary: #2a2a2a;
    --color-bg-panel: #222222;
    --color-accent: #0082c3;
    --color-text-primary: #ffffff;
    --color-text-secondary: #cccccc;
    --color-text-muted: #888888;
    --color-border: #444444;
    --color-selected: #0082c3;
    --color-hover: #333333;

    /* Spacing */
    --spacing-xs: 2px;
    --spacing-sm: 4px;
    --spacing-md: 8px;
    --spacing-lg: 16px;
    --spacing-xl: 24px;

    /* Typography */
    --font-size-caption: 12px;
    --font-size-body: 14px;
    --font-size-title: 18px;
    --font-size-heading: 24px;

    /* Sizing */
    --row-height: 38px;
    --icon-size: 24px;
}
```
Replace ALL hardcoded values in the USS with `var(--*)` references.

### 5d. Reusable USS classes
Create utility classes that any element can use:
- `.text-heading`, `.text-title`, `.text-body`, `.text-caption` — font sizes + weights
- `.row` — standard list row (flex-direction: row, align-items: center, fixed height)
- `.row:hover`, `.row.selected` — interactive states
- `.panel` — standard container (background, padding, border-radius)
- `.icon-box` — fixed-size box for icon placeholders
- `.separator` — horizontal line between sections
- `.hidden` — `display: none` utility

### 5e. Accessibility and navigation
- All interactive elements: `focusable="true"`
- `.selected` style uses **both** background color AND a visible border (colorblind-safe)
- `:focus` style matches `.selected` (keyboard nav = controller nav)
- Tab order follows visual order (UXML element order = tab order)

### 5f. Document the template
Add a USS comment block at the top of the USS file:
```css
/* ============================================================
 * {MenuName} — UI Template
 *
 * Dynamic elements (query by name in C#):
 *   - SettingsList: ScrollView container, populate with .template-row clones
 *   - ValueHealth: Label, set text to health value
 *   - TooltipText: Label, update on selection change
 *
 * Template rows (clone pattern):
 *   - .template-row: clone for each setting, set .setting-label + .setting-value
 *
 * Theme: override :root variables to re-skin
 * ============================================================ */
```

Apply all fixes directly to the UXML and USS files. Then re-render with `clibridge4unity SCREENSHOT` to verify nothing broke.

## Phase 6: Scene setup

Create a Unity scene with:
1. A `UIDocument` GameObject with the UXML assigned
2. A `PanelSettings` asset with `ScaleWithScreenSize` mode (reference resolution matching the original design, e.g. 1920x887)
3. Camera with black background

Use `clibridge4unity CODE_EXEC_RETURN` to create the scene, PanelSettings, and wire everything up.

## Phase 7: Create C# controller script (if interactive)

If the UI has interactive elements (selectable rows, toggles, buttons), create a MonoBehaviour that:
1. Loads via UIDocument
2. Queries interactive elements by name/class
3. Handles gamepad/keyboard navigation (up/down selection, left/right values)
4. Updates visual selected state (add/remove USS classes)
5. Updates tooltip text based on selection
6. Handles Accept (Enter/A), Cancel (Escape/B) via legacy Input

Write the script file directly into Assets/UI/{MenuName}/.
Then `clibridge4unity REFRESH` to import it.
