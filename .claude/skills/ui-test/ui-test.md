---
description: Run a UI automation test session against a Unity scene. Loads a scene, enters play mode, executes UI actions (click, type, swipe, etc.), captures screenshots after each action, and generates a live HTML report viewable at http://localhost:8420/sessions/{id}/
---

# UI Automation Test

Test goal: $ARGUMENTS

## Prerequisites

Both packages must be installed in the Unity project:
- `au.com.oddgames.clibridge4unity`
- `au.com.oddgames.uiautomation`

Verify with: `clibridge4unity HELP` — look for UIACTION and UISESSION in the output.

## Step 1: Ensure serve mode is running

The serve mode hosts screenshots and reports over HTTP so they can be viewed remotely.

```bash
# Check if already running
curl -s http://localhost:8420/ > /dev/null 2>&1 && echo "Running" || echo "Not running"
```

If not running, start it in the background:
```bash
clibridge4unity serve &
```

## Step 2: Load scene and enter play mode

```bash
clibridge4unity LOAD "Assets/Path/To/Scene.unity"
clibridge4unity PLAY
```

Wait 2-3 seconds for play mode to fully initialize before sending actions.

## Step 3: Start a session

```bash
clibridge4unity UISESSION "start --name TestName --desc \"YOUR GOAL DESCRIPTION HERE\""
```

This creates a session directory with a live HTML report. The report auto-refreshes every 3 seconds.

## Step 4: Execute actions

Use UIACTION with JSON to interact with the UI. Each action is queued and executed one at a time. After each action, a screenshot is automatically captured and added to the session report.

```bash
# Click by visible text
clibridge4unity UIACTION '{"action":"click","text":"Settings"}'

# Click by GameObject name
clibridge4unity UIACTION '{"action":"click","name":"SettingsButton"}'

# Click at screen position (normalized 0-1)
clibridge4unity UIACTION '{"action":"click","at":[0.5,0.5]}'

# Type into an input field
clibridge4unity UIACTION '{"action":"type","name":"InputField","value":"hello world"}'

# Press a key
clibridge4unity UIACTION '{"action":"key","key":"space"}'

# Swipe
clibridge4unity UIACTION '{"action":"swipe","direction":"left"}'

# Scroll
clibridge4unity UIACTION '{"action":"scroll","name":"ListView","delta":-120}'

# Wait for animations
clibridge4unity UIACTION '{"action":"wait","seconds":1}'

# Drag from one element to another
clibridge4unity UIACTION '{"action":"drag","from":{"name":"Source"},"to":{"name":"Target"}}'

# Dropdown selection
clibridge4unity UIACTION '{"action":"dropdown","name":"Dropdown","option":2}'
clibridge4unity UIACTION '{"action":"dropdown","name":"Dropdown","option":"Option Label"}'
```

### Action JSON Reference

| Field | Description |
|-------|-------------|
| `action` | **Required.** click, doubleclick, tripleclick, type, textinput, swipe, scroll, scrollto, wait, hold, drag, dropdown, pinch, rotate, key |
| `text` | Search by visible text |
| `name` | Search by GameObject name |
| `type` | Search by component type name |
| `near` | Search for nearest interactable to text |
| `adjacent` | Search for interactable adjacent to label |
| `at` | Screen position as [x, y] normalized 0-1 |
| `value` | Text to type (for type/textinput) |
| `key` | Key name (for key action) |
| `direction` | left/right/up/down (for swipe) |
| `delta` | Scroll amount (for scroll) |
| `seconds` | Duration (for wait/hold) |
| `from`/`to` | Search objects for drag source/target |
| `option` | Dropdown option index (int) or label (string) |
| `scale` | Pinch scale factor |
| `degrees` | Rotation degrees |

## Step 5: Take manual screenshots

If you need a screenshot outside of UIACTION (which auto-captures), use:
```bash
clibridge4unity SCREENSHOT
```

## Step 6: Stop the session

```bash
clibridge4unity UISESSION stop
```

This finalizes the HTML report with a "completed" status and stops auto-refresh.

## Step 7: Review

The session report is available at:
- **Local**: `file:///C:/Users/.../clibridge4unity/sessions/{id}/index.html`
- **Network**: `http://localhost:8420/sessions/{id}/` (if serve mode is running)

Share the network URL with anyone on the same LAN for remote review.

## Step 8: Clean up

```bash
clibridge4unity STOP
```

## Tips

- **Wait between actions**: Add `sleep 1` between actions if UI transitions need time
- **Screenshots are JPEG**: Resized to 1280px max width, 75% quality (~75-130KB each)
- **Actions are queued**: Concurrent UIACTION commands are serialized automatically
- **Session survives domain reloads**: SessionState persists the session across Unity recompiles
- **Check the report live**: Open the HTTP URL in your browser during the test — it auto-refreshes
