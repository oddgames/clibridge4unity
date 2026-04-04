---
description: Run a UI automation test session against a Unity scene. Loads a scene, enters play mode, executes UI actions (click, type, swipe, etc.), captures screenshots after each action, and generates a live HTML report viewable at http://localhost:8420/sessions/{id}/
---

# UI Automation Test

Test goal: $ARGUMENTS

## Quick Start

### 1. Load scene and play

```bash
clibridge4unity LOAD "Assets/Path/To/Scene.unity"
clibridge4unity PLAY
```

Wait 3-4 seconds for play mode to initialize (splash screens, animations).

### 2. Start session

```bash
clibridge4unity UISESSION "start --name TestName --desc \"YOUR GOAL DESCRIPTION HERE\""
```

### 3. Execute actions and review

Use UIACTION with JSON. Screenshots are auto-captured as PNG after every action.

**IMPORTANT: Prefer `text` search.** It works with ANY component. `name`/`path` only find standard Selectables and will time out on custom components.

**IMPORTANT: If an action fails, just retry.** The bridge uses a 1-second search timeout for fast failure. This is intentional — it's faster to fail and retry than to wait 10 seconds for an element that isn't there. Failures are normal during transitions and animations. Check the screenshot, adjust your search, and try again.

After each UIACTION, read the screenshot to see the UI state:
`C:\Users\David\AppData\Local\Temp\clibridge4unity\sessions\{id}\screenshots\{NNN}_{action}.png`

### 4. Stop session

```bash
clibridge4unity UISESSION stop
clibridge4unity STOP
```

## Actions

### Click / Tap

```bash
# By visible text (PREFERRED — works with any component)
clibridge4unity UIACTION '{"action":"click","text":"Settings"}'

# By screen position (normalized 0-1) — splash screens, non-text elements
clibridge4unity UIACTION '{"action":"click","at":[0.5,0.5]}'

# Double/triple click
clibridge4unity UIACTION '{"action":"doubleclick","text":"Item"}'
clibridge4unity UIACTION '{"action":"tripleclick","text":"TextBlock"}'

# Long press
clibridge4unity UIACTION '{"action":"hold","text":"Button","seconds":2}'
clibridge4unity UIACTION '{"action":"hold","at":[0.5,0.5],"seconds":2}'
```

### Text Input

```bash
# Type into a field (finds input field adjacent to label)
clibridge4unity UIACTION '{"action":"type","adjacent":"Username","value":"hello"}'
clibridge4unity UIACTION '{"action":"type","text":"InputField","value":"hello","clear":true,"enter":false}'

# Type raw text (no field targeting)
clibridge4unity UIACTION '{"action":"typetext","value":"hello world"}'

# Press individual keys
clibridge4unity UIACTION '{"action":"key","key":"space"}'
clibridge4unity UIACTION '{"action":"key","key":"escape"}'
clibridge4unity UIACTION '{"action":"key","key":"enter"}'

# Type a sequence of key presses
clibridge4unity UIACTION '{"action":"keys","value":"Hello!"}'

# Hold a key
clibridge4unity UIACTION '{"action":"holdkey","key":"shift","seconds":1}'

# Hold multiple keys simultaneously
clibridge4unity UIACTION '{"action":"holdkeys","keys":["ctrl","a"],"seconds":0.5}'
```

### Sliders & Scrollbars

```bash
# Set slider value directly (0-1 normalized)
clibridge4unity UIACTION '{"action":"slider","adjacent":"Master Volume","value":0.5}'

# Drag slider from one value to another
clibridge4unity UIACTION '{"action":"slider","adjacent":"Volume","from":1.0,"value":0.5}'

# Set scrollbar value
clibridge4unity UIACTION '{"action":"scrollbar","text":"ScrollView","value":0.5}'
```

### Scroll

```bash
# Scroll by delta (negative = down)
clibridge4unity UIACTION '{"action":"scroll","text":"ListView","delta":-120}'

# Scroll by direction
clibridge4unity UIACTION '{"action":"scroll","text":"ListView","direction":"down","amount":0.3}'

# Scroll at position
clibridge4unity UIACTION '{"action":"scroll","at":[0.5,0.5],"delta":-120}'

# Scroll until element is visible
clibridge4unity UIACTION '{"action":"scrollto","text":"ScrollView","target":{"text":"TargetItem"}}'
```

### Swipe & Drag

```bash
# Swipe (on element, at position, or screen center)
clibridge4unity UIACTION '{"action":"swipe","direction":"left"}'
clibridge4unity UIACTION '{"action":"swipe","text":"Panel","direction":"up","distance":0.3}'
clibridge4unity UIACTION '{"action":"swipe","at":[0.5,0.5],"direction":"right"}'

# Two-finger swipe
clibridge4unity UIACTION '{"action":"twofingerswipe","direction":"up"}'

# Drag between elements
clibridge4unity UIACTION '{"action":"drag","from":{"text":"Source"},"to":{"text":"Target"}}'

# Drag between positions
clibridge4unity UIACTION '{"action":"drag","from":{"at":[0.2,0.5]},"to":{"at":[0.8,0.5]}}'

# Drag by direction vector
clibridge4unity UIACTION '{"action":"drag","text":"Handle","direction":[200,0]}'
```

### Dropdown

```bash
clibridge4unity UIACTION '{"action":"dropdown","text":"Difficulty","option":"Hard"}'
clibridge4unity UIACTION '{"action":"dropdown","text":"Dropdown","option":2}'
```

### Touch Gestures

```bash
# Pinch (>1 zoom in, <1 zoom out)
clibridge4unity UIACTION '{"action":"pinch","scale":2.0}'
clibridge4unity UIACTION '{"action":"pinch","at":[0.5,0.5],"scale":0.5}'

# Rotate
clibridge4unity UIACTION '{"action":"rotate","degrees":45}'
clibridge4unity UIACTION '{"action":"rotate","at":[0.5,0.5],"degrees":-90}'
```

### Waiting & Synchronization

```bash
# Wait for element to appear
clibridge4unity UIACTION '{"action":"waitfor","text":"Loading Complete","seconds":30}'

# Wait for element with specific text
clibridge4unity UIACTION '{"action":"waitfor","text":"Status","expected":"Ready","seconds":10}'

# Wait for element to disappear
clibridge4unity UIACTION '{"action":"waitfornot","text":"Loading...","seconds":30}'

# Wait for stable frame rate
clibridge4unity UIACTION '{"action":"waitfps","minFps":20,"timeout":10}'

# Wait for target frame rate
clibridge4unity UIACTION '{"action":"waitframerate","fps":30,"timeout":60}'

# Wait for scene change
clibridge4unity UIACTION '{"action":"scenechange","seconds":30}'

# Fixed wait (avoid — prefer waitfor)
clibridge4unity UIACTION '{"action":"wait","seconds":1}'
```

### Debug

```bash
# Log full UI hierarchy
clibridge4unity UIACTION '{"action":"snapshot"}'
```

## Search Fields

| Field | Description |
|-------|-------------|
| `text` | **Preferred.** Find by visible text (works with ANY component) |
| `adjacent` | Find interactable adjacent to a label (great for sliders, inputs) |
| `near` | Find nearest interactable to text |
| `at` | Screen position as [x, y] normalized 0-1 |
| `any` | Match any of the given text |
| `name` | Find by GameObject name (only standard Selectables — avoid for custom UI) |
| `type` | Find by component type name |
| `tag` | Find by tag |
| `path` | Find by hierarchy path |

## Tips

- **Retry on failure**: Bridge uses 1s search timeout for fast failure. Just retry — don't wait.
- **Text search is preferred**: Works with custom components (PanelButton, etc.)
- **Use `adjacent` for sliders/inputs**: Finds the control next to a label
- **Splash screens**: Use `click at [0.5,0.5]` — splash screens often use `Input.anyKeyDown` which doesn't respond to text-based clicks
- **Session report**: `http://localhost:8420/sessions/{id}/` (if serve mode is running)