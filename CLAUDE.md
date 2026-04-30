# Unity Bridge - CLI and Console Tools

## Project Overview
A suite of tools for communicating with Unity Editor via Named Pipes using modern async/await patterns.

### Components
1. **clibridge4unity** - Lightweight CLI tool for single command execution (ideal for scripts, automation, Claude integration)
2. **ConsoleUnityBridge** - Interactive REPL-style console for developers
3. **Package** - Unity Editor package with the bridge server

### Architecture Goals
- **Speed is the #1 goal** — every response must be as fast as possible
- **Pure Async Architecture**: All methods are async Task<T> with no synchronous alternatives
- **Non-Blocking Operations**: No synchronous waits that could freeze Unity Editor or console
- **Concurrent Connections**: Support multiple simultaneous pipe connections (up to 10 instances)
- **Auto-Detection**: Auto-detects Unity project from current working directory
- **Attribute-Based Commands**: Commands use `[BridgeCommand]` attribute for automatic registration
- **CLI-side intelligence**: The CLI should provide actionable, accurate, efficient information about Unity's state WITHOUT needing a pipe connection whenever possible (process detection, window enumeration, lockfile checks, Editor.log tailing)

### CRITICAL: Speed and Diagnostics Mandate
**Every response must be actionable, accurate, efficient, and fast.**

- CLI detects Unity state instantly via process list, window titles, and lockfile — no pipe timeout
- If Unity is busy (importing, compiling), return immediately with what it's doing (window title)
- If main thread is blocked, return heartbeat staleness, open dialog windows, queue state, recommendations
- Compile errors block commands and are returned immediately — the LLM must fix them first
- `CODE_EXEC`/`CODE_EXEC_RETURN` have their own Roslyn compiler and do NOT need `COMPILE` — they work even when Unity's main thread is busy
- `DIAG` always works (no main thread needed) — use it to check Unity state
- Shared code (e.g., StackTraceMinimizer) is linked between CLI and Package via csproj `<Compile Include=".." Link=".."/>` — no duplication

### CRITICAL: Editor Performance Mandate
**NEVER add code that slows down the Unity Editor.** This is non-negotiable.

- All heavy operations (PDB loading, file scanning, etc.) MUST run on background threads
- Use `InitializeAsync()` patterns - start work in background, don't block editor startup
- Cache expensive data at startup in the background (e.g., `PdbCache.InitializeAsync()`)
- Dictionary lookups (O(1)) are acceptable; iterating all assemblies per-request is NOT
- No synchronous file I/O on the main thread during normal operations

### CRITICAL: Testing Mandate
**ALWAYS verify Unity has compiled changes before trusting test results.**

- When editing Package code, Unity MUST recompile for changes to take effect
- Failed tests may indicate Unity hasn't picked up code changes, NOT that code is broken
- Before assuming code is broken, ask user to confirm Unity has compiled successfully
- Use `COMPILE` or `REFRESH` commands to trigger compilation, then verify
- If commands behave unexpectedly, check if Unity Console shows compilation errors

### CRITICAL: Main Thread Execution Mandate
**NEVER use EditorApplication.delayCall or EditorApplication.update for main thread marshaling.**

- Unity is often in the background/minimized, where EditorApplication callbacks don't fire reliably
- **ALWAYS use SynchronizationContext** captured during `[InitializeOnLoad]` for main thread execution
- Current implementation: Dedicated polling thread (10ms cycle) + `SynchronizationContext.Post()`
- CommandRegistry provides `RunOnMainThreadAsync<T>()` utility for commands needing main thread access

### CRITICAL: Waking Unity from Background
**Use Win32 `PostMessage(WM_NULL)` to wake Unity's message pump when it's in the background.**

- When Unity's window doesn't have focus, its message pump goes idle and stops processing
- `SynchronizationContext.Post()` alone won't work — Unity never processes the posted work
- `EditorApplication.QueuePlayerLoopUpdate()` is main-thread-only — CANNOT be called from background threads
- Solution: `PostMessage(hwnd, WM_NULL, 0, 0)` wakes the message pump (WM_NULL is a harmless no-op)
- Both the CLI (externally) and the server-side polling loop (internally) send WM_NULL when work is pending
- CLI finds Unity's window via `Process.GetProcessesByName("Unity")` matching by window title

### CRITICAL: SessionState for Domain Reload Persistence
**ALWAYS use SessionState to persist state that must survive assembly reloads.**

- Unity domain reloads destroy all static fields, so any state needed across reloads MUST use `SessionState`
- Use `SessionState.SetInt/SetString/SetFloat/SetBool` to save, restore in `[InitializeOnLoad]` constructors
- Use unique key prefixes (e.g., `"Bridge_"`) to avoid collisions
- Current uses: compilation timestamps, log ID counter
- Prefer SessionState over EditorPrefs — SessionState is per-session (cleared on Unity restart), EditorPrefs persists forever

### CRITICAL: Assembly Reload and Pipe Connections
**Pipe connections are ALWAYS lost during Unity assembly reload/recompilation.**

- When `CompilationPipeline.RequestScriptCompilation()` is called, Unity reloads all Editor assemblies
- This destroys the `BridgeServer` instance and all active pipe connections
- **DO NOT** try to keep pipes open during compilation - it's architecturally impossible
- Commands that trigger compilation (COMPILE, REFRESH) should:
  - Return immediately after triggering the operation
  - Inform the client that connection will be lost
  - Let the client reconnect after Unity finishes reloading
- Clients should use STATUS command after reconnection to check if compilation finished

## Project Structure

```
tool_claude_unity_bridge/
├── clibridge4unity/           # Lightweight CLI tool (~1,545 lines, single-file)
│   └── clibridge4unity.cs     # Single-command CLI with auto-detection
├── clibridge4unity.Tests/     # Integration tests for CLI (21 tests)
├── ConsoleUnityBridge/        # Interactive console application
├── Package/                   # Unity Editor package (UPM)
│   ├── Editor/
│   │   ├── Core/              # Stable core (rarely changes)
│   │   │   ├── BridgeServer.cs    # Named pipe server
│   │   │   ├── BridgeCommand.cs   # Command attribute
│   │   │   ├── CommandRegistry.cs # Command registration & main thread dispatch
│   │   │   ├── SessionKeys.cs     # SessionState key constants
│   │   │   └── SetupWizard.cs     # CLI installer & PATH setup
│   │   └── Commands/          # Command implementations (one asmdef per category)
│   │       ├── Core/          # PING, STATUS, HELP, COMPILE, REFRESH, LOG
│   │       ├── Scene/         # Scene manipulation, play mode, windows
│   │       ├── Prefab/        # Prefab creation/instantiation
│   │       ├── Component/     # Component inspection & modification
│   │       ├── Asset/         # Asset search, move, copy, delete, labels
│   │       ├── Code/          # SEARCH, ANALYZE, CODE_EXEC, TEST
│   │       └── UI/            # ASSET_DISCOVER, SCREENSHOT
│   ├── Tools/                 # Pre-built CLI executables (win/osx/linux)
│   └── package.json           # UPM manifest (v1.0.93)
└── UnityTestProject/          # Test Unity project
```

## Using clibridge4unity

The CLI auto-detects the Unity project from the current directory:

```bash
# From within a Unity project directory
clibridge4unity PING
clibridge4unity ANALYZE BridgeServer
clibridge4unity SEARCH "class:MonoBehaviour"

# With explicit project path
clibridge4unity -d C:\MyUnityProject PING

# Get help from bridge
clibridge4unity -h
```

### CRITICAL: CLI Usage
**ALWAYS use the `clibridge4unity` binary directly, NEVER `dotnet run`.**

```bash
# CORRECT - use the installed binary directly
clibridge4unity PING
clibridge4unity -d C:\Workspaces\tool_claude_unity_bridge\UnityTestProject STATUS

# WRONG - don't use dotnet run
# dotnet run --project clibridge4unity.csproj -- PING
```

After building, install to PATH: `cp clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe ~/.clibridge4unity/`

## Build Instructions

### Building the CLI
```bash
cd clibridge4unity
dotnet build -c Release
dotnet publish -c Release
# Install to PATH
cp bin/Release/net8.0/win-x64/publish/clibridge4unity.exe ~/.clibridge4unity/
```

The published executable will be at:
`clibridge4unity/bin/Release/net8.0/win-x64/clibridge4unity.exe`

### Building Everything
```bash
dotnet build ConsoleUnityBridge.sln -c Debug
```

## Running Tests

### Test Setup
Tests are located in `clibridge4unity.Tests` and require Unity to be running.

### Unity Requirements
For tests to pass:
1. Unity Editor must be open with the `UnityTestProject`
2. The Unity console should show: `[Bridge] Server started: UnityBridge_{Username}_{Hash}`
3. The Package must be compiled in Unity (check for compilation errors)

## Key Technical Details

### Named Pipe Communication
- Pipe name format: `UnityBridge_{Username}_{ProjectPathHash}`
- Hash calculation: Path is normalized to lowercase with backslashes, then hashed
- CLI auto-detects project by walking up directory tree looking for `Assets` folder

### Adding New Commands
Commands use the `[BridgeCommand]` attribute:

```csharp
[BridgeCommand("MYCOMMAND", "Description shown in HELP",
    Category = "MyCategory",
    Usage = "MYCOMMAND data",
    RequiresMainThread = true)]
public static string MyCommand(string data)
{
    return Response.Success("result");
}
```

Command methods can have these signatures:
- `string Method()` - No data parameter
- `string Method(string data)` - With data parameter
- `Task<string> Method()` - Async without data
- `Task<string> Method(string data)` - Async with data
- `Task Method(string data, NamedPipeServerStream pipe, CancellationToken ct)` - Streaming

### Assembly Definitions
The Package is split into 8 asmdefs to minimize recompilation:
- **clibridge4unity.Core** - BridgeServer, CommandRegistry, SessionKeys (stable, rarely changes)
- **clibridge4unity.Commands.Core** - PING, STATUS, HELP, COMPILE, REFRESH, LOG
- **clibridge4unity.Commands.Scene** - Scene manipulation, play mode, windows
- **clibridge4unity.Commands.Prefab** - Prefab operations
- **clibridge4unity.Commands.Component** - Component inspection & modification
- **clibridge4unity.Commands.Asset** - Asset search
- **clibridge4unity.Commands.Code** - Code analysis (CODE_ANALYZE, CODE_EXEC, TEST)
- **clibridge4unity.Commands.UI** - UI discovery, rendering

## Commands Available

Use `clibridge4unity -h` to get the current list of available commands from Unity.

### Core
- `PING` - Test connection
- `HELP` - List all available commands
- `PROBE` - Quick main thread health check
- `DIAG` - Diagnostic info (no main thread needed)
- `STATUS` - Get Unity Editor status
- `COMPILE` - Force script recompilation
- `REFRESH` - Force asset database refresh
- `LOG [filter]` - Get Unity console logs
- `STACK_MINIMIZE` - Minimize a stack trace for AI
- `MENU path` - Execute a Unity menu item (e.g. `MENU Window/General/Console`)
- `PROFILE [enable|disable|clear|hierarchy]` - Control profiler and read performance data

### Code
- `CODE_ANALYZE query` - Unified code analysis (works offline via Roslyn daemon):
  - `CODE_ANALYZE ClassName` → deep view (definition, usages, derived types, GetComponent sites, own members)
  - `CODE_ANALYZE ClassName.Member` → zoom into one member
  - `CODE_ANALYZE method:Name` | `field:Name` | `property:Name` | `inherits:Type` | `attribute:Name` → kind-prefixed listing across the codebase
- `CODE_EXEC code` - Compile and execute C# code (fire-and-forget)
- `CODE_EXEC_RETURN code` - Compile and execute C# code (waits for result, returns type)
- `CODE_EXEC_RETURN code --inspect [depth] [--private]` - Execute and dump result object tree
- `CODE_EXEC_RETURN code --trace [--maxlines N] [--from N] [--only var] [--vars x,y] [--skip pattern]` - Execute with line-by-line trace
- `TEST [mode] [groups...] [--category X,Y] [--tests Full.Name,Other.Name]` - Run Unity tests (streaming)
  - Mode: default `editmode`, or `playmode`, or `all`
  - Groups: positional args, comma- or space-separated — matches class/namespace paths (regex)
  - `--category X,Y`: filter by `[Category("…")]` attribute (multiple OR)
  - `--tests A,B`: filter by exact test full names (multiple OR)
  - All filter arrays OR'd — a test runs if it matches any group **or** category **or** exact name
- `TEST list [filter]` - List available tests (substring match)
- `DEBUG` - Debugger stub (Phase 2: attach, breakpoints, stepping)

### Scene
- `CREATE name` - Create a new GameObject
- `FIND name` - Find by name. Scope prefixes:
  - `FIND Player` or `FIND scene:Player` — scene (default)
  - `FIND prefab:Assets/UI/Menu.prefab/Button` — inside a prefab asset (comma-separate for OR)
- `DELETE path` - Delete a GameObject
- `SAVE` - Save current scene
- `LOAD scenePath` - Load a scene
- `SCENEVIEW frame|2d|3d` - Control the Scene view
- `WINDOWS` - List open editor windows with positions
- `PLAY [scene]` - Enter play mode
- `STOP` - Exit play mode
- `PAUSE` - Toggle pause
- `STEP` - Single frame step
- `PLAYMODE` - Get current play mode state
- `GAMEVIEW 1280x720` - Set Game view resolution

### Prefab
- `PREFAB_CREATE name path` - Create a prefab asset
- `PREFAB_INSTANTIATE path [parent]` - Instantiate a prefab in the scene

### Component / Inspect
- `INSPECTOR` - Whole-scene hierarchy (brief, all roots recursed)
- `INSPECTOR path` - One scene GameObject or asset with serialized fields
- `INSPECTOR path --children` / `--depth N` - Recurse subtree
- `INSPECTOR path --brief` - Components only, no serialized fields
- `INSPECTOR path --filter X` - Subtree filtered by GameObject name OR component name
- `INSPECTOR Assets/x.prefab [--children] [--brief] [--filter X]` - Prefab asset (absorbs old PREFAB_HIERARCHY)
- `COMPONENT_SET gameObject component field value` - Set field/property on a component
- `COMPONENT_ADD gameObject component` - Add a component
- `COMPONENT_REMOVE gameObject component` - Remove a component

### Asset
- `ASSET_SEARCH query` - Search assets using Unity Search syntax
- `ASSET_DISCOVER [category]` - Discover assets (ui, sprites, prefabs, scenes, fonts, shaders, materials, models, variants)
- `ASSET_MOVE src dst` - Move/rename assets (preserves GUIDs), supports multi-source to folder
- `ASSET_COPY src dst` - Copy assets, supports multi-source to folder
- `ASSET_DELETE path [path2...]` - Delete assets (batch)
- `ASSET_MKDIR path [path2...]` - Create folders (nested, batch)
- `ASSET_LABEL path [+add -remove]` - Get/set asset labels
- `ASSET_RESERIALIZE [paths...]` - Force re-validate and re-import assets (fixes corrupted YAML)

### Screenshot (single command, smart routing)
- `SCREENSHOT [view]` - CLI-side window capture (default `editor`; views: `editor|scene|inspector|hierarchy|console|project|profiler`). Downscaled to max 1280px.
- `SCREENSHOT camera [WxH]` - Raw camera render only, **no overlays** (default 960x540)
- `SCREENSHOT gameview` - GameView tab incl. OnGUI, runtime UI Toolkit, and chrome (use this to see what the player sees)
- `SCREENSHOT <GameObjectName>` - Render scene GameObject (3-view atlas for 3D)
- `SCREENSHOT Assets/Foo.prefab` - Render prefab asset (auto-sized, capped at 1280px)
- `SCREENSHOT Assets/UI/Foo.uxml` - Render UXML at 800x450 (force-reimports the UXML + its .uss/.tss deps first)
- `SCREENSHOT Assets/UI/Foo.uxml --el #card-grid` - Render only a sub-element (--el: `#name`, `.class`, or bare name)
- `SCREENSHOT path1.prefab path2.prefab` - Grid render (multi-asset)

### CLI-side (no Unity connection needed)
- `SETUP` - Install UPM package + verify Unity + generate CLAUDE.md (alias: `INSTALL`)
- `UPDATE` - Self-update CLI exe + UPM package tag (no Unity connection needed)
- `WAKEUP` - Bring Unity to foreground (targets project via -d)
- `WAKEUP refresh` - Bring to foreground + send Ctrl+R to force recompile
- `DISMISS` - Close modal dialogs

