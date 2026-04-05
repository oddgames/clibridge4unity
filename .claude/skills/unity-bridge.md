---
description: Use clibridge4unity to control Unity Editor — compile, play, inspect, test, execute code, automate UI, manage assets. Use when working with Unity projects.
---

# CLI Bridge for Unity

`clibridge4unity` communicates with the Unity Editor via named pipes. It requires Unity to be open with the target project.

## Connection

```bash
# Always specify project path with -d
clibridge4unity -d "C:/path/to/unity/project" COMMAND [args]

# Check connection
clibridge4unity -d "$PROJECT" PING

# Full editor status (compile state, play mode, errors)
clibridge4unity -d "$PROJECT" STATUS
```

**Important:** Detect the project path from context. Check which Unity is running:
```bash
wmic process where "name='Unity.exe'" get CommandLine
```

## Core Workflow: Compile → Play → Test

After modifying SDK C# files, always run the full cycle automatically — never ask the user to compile or enter play mode:

```bash
# 1. Stop play mode if running
clibridge4unity -d "$PROJECT" STOP

# 2. Wait for Unity to settle
sleep 2

# 3. Compile (waits for completion, reports errors)
clibridge4unity -d "$PROJECT" COMPILE

# 4. Check for errors
clibridge4unity -d "$PROJECT" STATUS | head -5

# 5. Enter play mode
clibridge4unity -d "$PROJECT" PLAY

# 6. Verify
clibridge4unity -d "$PROJECT" LOG all | grep "Connected" | tail -1
```

## Console Logs

```bash
LOG                    # Last 20 entries (compact)
LOG errors             # Errors and exceptions only
LOG warnings           # Warnings only
LOG info               # Info logs only
LOG all                # All entries, no cap
LOG errors verbose     # Errors with full stack traces
LOG last:50            # Last 50 entries
LOG since:ID           # Since a specific log ID
LOG clear              # Clear the log buffer
```

Combine filters: `LOG errors verbose last:10`

## Code Execution

```bash
# Fire-and-forget (no return value)
CODE_EXEC Debug.Log("Hello");

# Wait for return value (25s timeout)
CODE_EXEC_RETURN 1 + 2
CODE_EXEC_RETURN GameObject.Find("Player").transform.position

# Inspect an expression (deep object dump)
CODE_EXEC_RETURN myObject --inspect 3 --private

# Trace execution line by line
CODE_EXEC_RETURN myMethod() --trace

# Read code from file (for long scripts)
CODE_EXEC @/tmp/myscript.cs
```

## Scene & GameObjects

```bash
# Scene info and hierarchy
SCENE

# Find a GameObject
FIND PlayerCharacter

# Create GameObjects
CREATE MyObject
CREATE {"name":"Panel","components":["Image","CanvasGroup"],"parent":"Canvas"}

# Delete
DELETE Canvas/OldPanel

# Inspect (components, fields, properties)
INSPECTOR Canvas/Panel
INSPECTOR {"gameObject":"Panel","component":"Image"}

# Component operations
COMPONENT_ADD Canvas/Panel BoxCollider
COMPONENT_REMOVE Canvas/Panel BoxCollider
COMPONENT_SET Canvas/Panel Image m_Color #FF0000
```

## Play Mode

```bash
PLAY                           # Enter play mode
PLAY Assets/Scenes/Game.unity  # Play with specific scene
STOP                           # Exit play mode
PAUSE                          # Toggle pause
STEP                           # Single frame step while paused
PLAYMODE                       # Get current state
```

## Screenshots

```bash
SCREENSHOT camera              # Main camera render (1280x720)
SCREENSHOT camera 1920x1080    # Custom resolution
SCREENSHOT Player              # Render a GameObject from multiple angles
SCREENSHOT Assets/Prefab.prefab # Render a prefab asset
```

Output: `%TEMP%/clibridge4unity_screenshots/`

## UI Automation (Bugpunch)

```bash
# Start a test session (auto-captures screenshots after each action)
UISESSION start --name "Login Test" --desc "Test login flow"

# Execute actions
UIACTION {"action":"click", "text":"Login"}
UIACTION {"action":"type", "name":"EmailField", "value":"test@test.com"}
UIACTION {"action":"click", "at":[0.5, 0.8]}
UIACTION {"action":"swipe", "direction":"left"}
UIACTION {"action":"wait", "seconds":2}
UIACTION {"action":"scroll", "name":"ScrollView", "delta":-120}
UIACTION {"action":"dropdown", "name":"Options", "option":2}

# Stop session (generates HTML report)
UISESSION stop
```

**Search fields** for targeting elements: `text`, `name`, `type`, `near`, `adjacent`, `tag`, `path`, `any`, `at` (normalized [0-1] coordinates).

## Asset Management

```bash
# Discover project assets
ASSET_DISCOVER                  # Summary of all categories
ASSET_DISCOVER ui               # UI assets (prefabs, sprites, fonts)
ASSET_DISCOVER sprites          # Sprite assets
ASSET_DISCOVER prefabs          # Prefab assets
ASSET_DISCOVER scenes           # Scene files
ASSET_DISCOVER materials        # Materials grouped by shader

# Search
ASSET_SEARCH t:prefab
ASSET_SEARCH t:material shader:URP

# Copy/Move/Delete
ASSET_COPY Assets/A.prefab Assets/B.prefab
ASSET_MOVE Assets/Old.prefab Assets/New.prefab
ASSET_DELETE Assets/Unused.prefab
ASSET_MKDIR Assets/Art/Textures/UI

# Extract from prefab/scene
ASSET_COPY Assets/Level.prefab/Enemy Assets/Enemy.prefab
ASSET_COPY scene/Player Assets/Player.prefab
```

## Prefabs

```bash
PREFAB_CREATE MyPrefab Assets/Prefabs
PREFAB_HIERARCHY Assets/Prefabs/UI.prefab
PREFAB_INSTANTIATE Assets/Prefabs/Enemy.prefab Canvas
PREFAB_SAVE PlayerObject Assets/Prefabs/Player.prefab
```

## Code Analysis

```bash
CODE_ANALYZE PlayerController           # Class overview
CODE_ANALYZE PlayerController.Move       # Method details
CODE_ANALYZE "NullReferenceException..." # Parse stack trace

CODE_SEARCH class:PlayerController
CODE_SEARCH method:OnTriggerEnter
CODE_SEARCH inherits:MonoBehaviour
CODE_SEARCH attribute:SerializeField
```

## Testing

```bash
TEST                    # Run EditMode tests
TEST playmode           # Run PlayMode tests
TEST all                # Run all tests
TEST list               # List available tests
TEST MyTestClass        # Run specific test class
```

## Profiling

```bash
PROFILE                 # Status
PROFILE enable          # Start profiling
PROFILE disable         # Stop
PROFILE hierarchy       # Last frame breakdown
PROFILE hierarchy min:1.0 depth:2  # Filter >1ms, 2 levels deep
```

## Scene View Control

```bash
SCENEVIEW frame              # Frame selection
SCENEVIEW frame:PlayerObj    # Frame specific object
SCENEVIEW 2d                 # Switch to 2D
SCENEVIEW 3d                 # Switch to 3D
GAMEVIEW 1280x720            # Set game view resolution
```

## Troubleshooting

```bash
# Unity not responding
DIAG                    # Works even when main thread is blocked
PROBE                   # Quick 2s health check

# Safe Mode (Unity 6+)
# STATUS will show window title contains "SAFE MODE"
# Fix compile errors, exit safe mode in Unity UI

# Stale caches after package changes
# Delete packages-lock.json and Library/ScriptAssemblies/
# Then COMPILE or REFRESH

# Dialog blocking Unity
# Check STATUS output for dialog warnings
# Some dialogs can be dismissed, others need manual action
```

## Tips

- **Timeouts**: Most commands timeout at 10s. COMPILE waits up to 300s.
- **Play mode**: Cannot COMPILE during play mode — STOP first.
- **Multiple Unity instances**: Use `-d` to target the right project.
- **Large projects**: SCENE command can be slow — use FIND for specific objects.
- **Stale logs**: Old errors stay in the console buffer. Use `LOG clear` after fixing issues.
- **Package changes**: Delete `Packages/packages-lock.json` and run `COMPILE` to force re-resolve.
