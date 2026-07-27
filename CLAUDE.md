# Unity Bridge - CLI and Console Tools

## Project Overview
A suite of tools for communicating with Unity Editor via Named Pipes using modern async/await patterns.

### Components
1. **clibridge4unity** - Lightweight CLI tool for single command execution (ideal for scripts, automation, Claude integration)
2. **Package** - Unity Editor package with the bridge server
3. **vscode-extension** - VSCode/Cursor status-bar extension (built to a .vsix, embedded in the CLI)

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

- All heavy operations (file scanning, reflection sweeps, etc.) MUST be command-triggered, not editor-startup work
- Keep `[InitializeOnLoad]` paths limited to essential bridge liveness hooks
- Do not cache expensive data at startup; initialize it lazily when the command actually needs it
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
│   ├── clibridge4unity.cs     # Single-command CLI with auto-detection
│   ├── skills/                # Per-task skill .md files (embedded into the exe)
│   └── vscode/                # Build-staging for the embedded extension .vsix (deploy-time)
├── tests/                     # pytest integration tests (require Unity running with UnityTestProject)
├── Package/                   # Unity Editor package (UPM)
│   ├── Editor/
│   │   ├── Core/              # Stable core (rarely changes)
│   │   │   ├── BridgeServer.cs    # Named pipe server
│   │   │   ├── BridgeCommand.cs   # Command attribute
│   │   │   ├── CommandRegistry.cs # Command registration & main thread dispatch
│   │   │   ├── SessionKeys.cs     # SessionState key constants
│   │   │   └── SetupWizard.cs     # CLI installer & PATH setup
│   │   └── Commands/          # Command implementations (one asmdef per category)
│   │       ├── Core/          # PING, STATUS, HELP, DIAG, PROBE, COMPILE, REFRESH, LOG, MENU, PROFILE, BUILD
│   │       ├── Scene/         # Scene manipulation, play mode, windows
│   │       ├── Prefab/        # Prefab creation/instantiation
│   │       ├── Component/     # Component inspection & modification
│   │       ├── Asset/         # Asset search, move, copy, delete, labels
│   │       ├── Code/          # CODE_EXEC, CODE_EXEC_RETURN, TEST, DEBUG (ANALYZE + LINT are CLI-side)
│   │       └── UI/            # UI_DISCOVER, SCREENSHOT (server-side renders)
│   ├── Tools/                 # Pre-built CLI executables (win/osx/linux)
│   └── package.json           # UPM manifest (v1.1.66)
├── UnityTestProject/          # Test Unity project
└── vscode-extension/          # VSCode/Cursor status-bar extension (built to a .vsix, embedded in the CLI)
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
Tests are pytest-based, located in `tests/`, and require Unity to be running (run with `pytest`).

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

### Code Organization
**Prefer extending existing types over creating new ones for small additions.** When a change needs only a method or two, add them to the static/partial class that already owns that area — do **not** create a new class or file just to hold one or two methods. The CLI (`clibridge4unity.cs`) is intentionally single-file/partial; keep it cohesive. Split into a new class/file only when (a) it's a genuine reusable **utility** with its own identity, or (b) the code is **large enough** that inlining would bloat the host class. When in doubt, extend what exists. (The Package's asmdef split below is the sanctioned "separate when it's a real module" case.)

### Assembly Definitions
The Package is split into 8 asmdefs to minimize recompilation:
- **clibridge4unity.Core** - BridgeServer, CommandRegistry, SessionKeys (stable, rarely changes)
- **clibridge4unity.Commands.Core** - PING, STATUS, HELP, COMPILE, REFRESH, LOG
- **clibridge4unity.Commands.Scene** - Scene manipulation, play mode, windows
- **clibridge4unity.Commands.Prefab** - Prefab operations
- **clibridge4unity.Commands.Component** - Component inspection & modification
- **clibridge4unity.Commands.Asset** - Asset search
- **clibridge4unity.Commands.Code** - Runtime code execution and tests (CODE_EXEC, CODE_EXEC_RETURN, TEST)
- **clibridge4unity.Commands.UI** - UI discovery, rendering

`ANALYZE` (aliases: `CODE_ANALYZE`, `CODE_SEARCH`) is CLI-side only; it must not be registered as a Unity `[BridgeCommand]`.

## Commands Available

Use `clibridge4unity -h` to get the current list of available commands from Unity.

### Core
- `PING` - Test connection
- `HELP` - List all available commands
- `PROBE` - Quick main thread health check
- `DIAG` - Diagnostic info (no main thread needed)
- `BRIDGEINFO` - Stable handshake (no main thread): `bridgeVersion`, `minCompatibleExtensionVersion`, `bridgeProtocol`. **Frozen contract** — consumed by the VSCode extension to decide compatibility; never rename it or repurpose a field (append only). Raise `BridgeServer.MinCompatibleExtensionVersion` only in a release that breaks the extension's interface.
- `STATUS` - Get Unity Editor status, including C# compile and UI Toolkit import errors
- **LINT/COMPILE discipline:** both are reactive troubleshooting tools, never routine steps — Unity auto-compiles on focus and 99% of the time the user has already compiled before asking for a test. Run them only when something isn't working as expected (STATUS errors, stale results, CODE_EXEC can't see a new type). Exception: editing this repo's Package code with Unity backgrounded needs `COMPILE force` (change watcher can't see the external `file:` package).
- `LINT [warnings]` - **Default: offline syntax + UXML/USS well-formedness check (~1s).** Catches missing braces, unclosed strings, bad keywords, malformed C#/UXML/USS. Daemon FileSystemWatcher → catches errors in NEW files Unity hasn't seen. Fails fast at 20s on huge projects.
- `LINT unity [warnings]` - Unity-faithful **per-asmdef** compile (~5-60s). Asmdef-aware (avoids cross-asmdef type collision false positives). Catches missing methods, type errors, missing usings. 60s budget — falls back to COMPILE if exceeded.
- `COMPILE` - Force script recompilation (Unity-side, triggers domain reload, breaks pipe). The ground truth — use when LINT modes give false positives or you need source generators / post-compile callbacks. Bridge auto-blocks all commands during Unity Player Build (returns clear error instead of timing out).
- `REFRESH` - Force asset database refresh
- `LOG [filter]` - Get bridge-captured Unity **console** logs (over the pipe); use `LOG ui errors` for current USS/UXML/TSS import errors
  - Commands that reference `.uss`, `.uxml`, or `.tss` assets append matching UI Toolkit import errors automatically
  - `LOG` needs the bridge running and reads Unity's in-memory console — when it's empty or the pipe is down, use `EDITORLOG` (CLI-side) to read the on-disk `Editor.log` instead
- `MENU path` - Execute a Unity menu item (e.g. `MENU Window/General/Console`)
- `PROFILE [enable|disable|clear|hierarchy]` - Control profiler and read performance data
- `BUILD [--run] [--dev] [--output <path>]` - Build the Unity Player (active target). Streams progress + errors. `--run` launches Standalone after success. Default output: `Builds/<Target>/<ProductName>`. Other commands auto-block during the build.

### Code
- `ANALYZE query` - **The main reference point.** Unified code + asset-wiring analysis (offline, daemon-served; aliases: `CODE_ANALYZE`, `CODE_SEARCH`):
  - `ANALYZE ClassName` → deep view (definition, usages, derived types, GetComponent sites, own members) + **Asset wiring** section (scenes/prefabs the script is attached to, SO instances, UnityEvent targets — from the daemon's serialized asset graph)
  - `ANALYZE ClassName.Member` → zoom into one member
  - `ANALYZE method:Name` | `field:Name` | `property:Name` | `inherits:Type` | `attribute:Name` → kind-prefixed listing across the codebase
  - `ANALYZE usedby:Assets/Foo.prefab` (or `usedby:ClassName`) → reverse GUID lookup: every scene/prefab/SO/UXML referencing the asset (index-backed, instant)
- `MAP task keywords` - Task-oriented project map (offline, daemon-served). Free keywords (e.g. `MAP double jump`) → one dossier: matching scripts with attach sites, scenes (with build index), prefabs, SO config assets, UXML/`.inputactions`, UnityEvent wiring. The "where do I start?" command. See [AssetGraph.cs](clibridge4unity/AssetGraph.cs)
- `CODE_EXEC code` - Compile and execute C# code (fire-and-forget). Alias: `EXEC`
- `CODE_EXEC_RETURN code` - Compile and execute C# code (waits for result, returns type). Alias: `EVAL`
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
- `FIND name` - Find by name **or component type** (both always searched; results labeled `matchedBy` + `active`). A component-type query is inheritance-aware (base class matches derived) and includes inactive objects — never hand-roll `FindObjectsOfType<T>(true)` via CODE_EXEC. Scope prefixes:
  - `FIND Player` or `FIND scene:Player` — scene (default)
  - `FIND prefab:Assets/UI/Menu.prefab/Button` — inside a prefab asset (comma-separate for OR)
- `DELETE path` - Delete a GameObject
- `SAVE` - Save current scene
- `LOAD scenePath` - Load a scene (response confirms the now-active scene path — use instead of CODE_EXEC `OpenScene`)
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
- `PREFAB_SAVE name [path]` - Save scene GameObject as prefab (apply changes if existing instance)

### Component / Inspect
- `INSPECTOR` - Whole-scene hierarchy (brief, all roots recursed)
- `INSPECTOR path` - One scene GameObject or asset with serialized fields (resolves **inactive** objects too)
- `INSPECTOR path --children` / `--depth N` - Recurse subtree
- `INSPECTOR path --brief` - Components only, no serialized fields
- `INSPECTOR path --filter X` - Subtree filtered by GameObject name OR component name
- `INSPECTOR path --component X` - One component's fields only (exact type name)
- `INSPECTOR path --refs` - Wiring audit: every object-reference property (deep-walked through nested classes + array elements) as `propertyPath = Type:'name'` / `None` / `Missing (broken reference)`
- `INSPECTOR Assets/x.prefab [--children] [--brief] [--filter X]` - Prefab asset (absorbs old PREFAB_HIERARCHY)
- Field rendering: object refs print `Type:'name' (assetPath)` (path only for assets); arrays/lists print elements (capped at 10); nested serializable classes recurse (3 levels); missing scripts print `[Missing Script!]`
- `COMPONENT_SET gameObject component field value` - Set field/property on a component
- `COMPONENT_ADD gameObject component` - Add a component
- `COMPONENT_REMOVE gameObject component` - Remove a component

### Asset
- `ASSET_SEARCH query` - Search assets using Unity Search syntax (fuzzy "did you mean" suggestions on miss)
- `ASSET_DISCOVER [category]` - Discover assets (ui, sprites, prefabs, scenes, fonts, shaders, materials, models, variants)
- `UI_DISCOVER` - Alias for `ASSET_DISCOVER ui` (no bridge command enumerates UXML/USS/TSS files — use Grep or ANALYZE)
- `ASSET_MOVE src dst` - Move/rename assets (preserves GUIDs), supports multi-source to folder
- `ASSET_COPY src dst` - Copy assets, supports multi-source to folder
- `ASSET_DELETE path [path2...]` - Delete assets (batch)
- `ASSET_MKDIR path [path2...]` - Create folders (nested, batch)
- `ASSET_LABEL path [+add -remove]` - Get/set asset labels
- `ASSET_RESERIALIZE [paths...]` - Force re-validate and re-import assets (fixes corrupted YAML). Alias: `REIMPORT`

### Screenshot (single command, smart routing)
- `SCREENSHOT [view]` - CLI-side window capture (default `editor`; views: `editor|scene|inspector|hierarchy|console|project|profiler`). Downscaled to max 1280px.
- `SCREENSHOT camera [WxH]` - Raw camera render only, **no overlays** (default 960x540)
- `SCREENSHOT gameview` - GameView tab incl. OnGUI, runtime UI Toolkit, and chrome (use this to see what the player sees)
- `SCREENSHOT <GameObjectName>` - Render scene GameObject (3-view atlas for 3D)
- `SCREENSHOT Assets/Foo.prefab` - Render prefab asset (auto-sized, capped at 1280px)
- `SCREENSHOT Assets/UI/Foo.uxml` - **Renders TWO views by default**: as-authored, and a second with every hidden element unhidden (`display:none`/`visibility:hidden`/`opacity:0`) + a **list of what it unhid** (with the reason). Both PNG paths returned. (force-reimports the UXML + its .uss/.tss deps first)
- `SCREENSHOT Assets/UI/Foo.uxml --el #card-grid` - Single view: render only a sub-element (--el: `#name`, `.class`, or bare name)
- `SCREENSHOT Assets/UI/Foo.uxml --reveal` - Single view: unhide every hidden element + list them (alias: `--unhide`; the bare command already gives this as its 2nd image)
- `SCREENSHOT Assets/UI/Foo.uxml --show #panel,.tab / --hide #overlay` - Force-show/force-hide specific elements (comma-separated `#name`/`.class`/bare). Combine with `--reveal` to reveal-all-then-hide-a-few on a second run
- `SCREENSHOT path1.prefab path2.prefab` - Grid render (multi-asset)

### Cross-window conflict warnings (CLI-side, automatic, no Unity needed)
Multiple Claude/CLI windows share **one** Unity editor, so commands collide (COMPILE breaks others' pipes, PLAY/STOP trample shared play state, BUILD blocks everyone, same-asset edits clobber). The CLI tracks a stable per-window identity and prints an advisory warning before a stomping command — this is automatic; there are no peer commands to run. See [PeerLedger.cs](clibridge4unity/PeerLedger.cs).
- **Identity**: anchored to the parent `claude`/terminal process (stable across CLI invocations, which are ephemeral). Override with `CLIBRIDGE_PEER_ID`. Two chat panels in ONE host process collapse to one id — use the override to disambiguate.
- **Conflict warnings** print as `[conflict] WARNING:` on stderr before COMPILE/REFRESH/PLAY/STOP/BUILD/asset-writes when another live window is active. Advisory only — never blocks.
- **Edit detection**: the PreToolUse `HOOK` (matcher `Edit|Write|MultiEdit|NotebookEdit|Grep`) records file edits per-window, so concurrent edits to the same file are caught — not just CLI asset commands. Set `CLIBRIDGE_BLOCK_ON_CONFLICT=1` to make the edit hook block (exit 2) on a fresh conflict instead of advising.
- **Storage**: `{project}/.clibridge4unity/peers/` (`{id}.peer` presence+activity ring, `{id}.active` in-flight marker). Dead windows pruned by anchor-PID liveness check.

### CLI-side (no Unity connection needed)
- `LINT` / `LINT semantic` - Offline syntax/semantic check (see Core section above)
- `ANALYZE query` - Code + asset-wiring analysis (Roslyn daemon, persistent index); `usedby:` prefix for reverse asset lookups (aliases: CODE_ANALYZE, CODE_SEARCH)
- `MAP task keywords` - Task-oriented project map (code index + serialized asset graph, no Unity needed)
- `SETUP` - Install UPM package + install per-task skills into `.claude/skills/` + verify Unity + generate CLAUDE.md and AGENTS.md (alias: `INSTALL`). Skills are embedded in the CLI exe (`clibridge4unity/skills/*.md`) and unpacked with a SHA-1 trailer; re-running SETUP only overwrites files the user hasn't edited.
- `UPDATE` - Self-update CLI exe + UPM package tag + re-unpack the embedded per-task skills into `.claude/skills/` (no Unity connection needed). Refreshing the skills means a single `UPDATE` ships the new exe's skill content too, not just the binary — same wipe-and-reinstall as `SETUP` (renamed/user-authored skills untouched). Silent on skills when run outside a Unity project.
- `OPEN` - Launch Unity with project (auto-detects Unity version via ProjectVersion.txt)
- `WAKEUP` - Bring Unity to foreground (targets project via -d)
- `WAKEUP refresh` - Bring to foreground + send Ctrl+R to force recompile
- `DISMISS` - Close modal dialogs
- `SCREENSHOT` - CLI-side window capture (see Screenshot section)
- `EDITORLOG [N|errors|grep PAT|path]` - Tail Unity's on-disk `Editor.log` (aliases: `EDITORLOGS`, `ELOG`). No pipe needed — works when the bridge isn't running in that instance (clones, crashed/busy Unity) and surfaces import/compile/crash/load lines `LOG` can't reach. Resolves the per-instance log (`-logFile` arg → header-matched `Editor*.log` → default `%LOCALAPPDATA%\Unity\Editor\Editor.log`) and prints which file it read. `errors` filters to error/exception/fail lines; `grep PAT` is a case-insensitive regex; `path` prints the resolved path only.
- `VSCODE` - Install the bundled VSCode/Cursor status-bar extension into detected editors (`code`/`code-insiders`/`cursor`/`codium`/`windsurf`). The `.vsix` is embedded in the CLI exe (built from `vscode-extension/`, version-locked to the CLI) and installed via `<editor> --install-extension <vsix> --force`; idempotent (skips if the editor already has an equal-or-newer version). `SETUP` prints a hint pointing here but does not auto-install.
- `RELEASENOTES [from] [to] [-c Cat,Cat] [-g regex] [--format md|json|text] [-o file] [--url]` - Fetch Unity Editor release notes for a version range from `release-notes.ds.unity3d.com` (pure HTTP, no Unity/project needed). **Bare** = current project's Unity version (`ProjectVersion.txt`) → latest available; **from-only** = that version → latest. `--list [substr]` browses versions. `-c` filters categories (substring, comma-OR), `-g` regex-filters note text — use it to check whether a hard bug matches a known Unity fix/regression. Issue links (`UUM-####`) resolve to the issue tracker. Aliases: `UNITYNOTES`, `RELNOTES`. See [clibridge4unity-release-notes skill](clibridge4unity/skills/clibridge4unity-release-notes.md).
