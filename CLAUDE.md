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
│   │   ├── Capture/           # Capture Context panel (EditorWindow, no bridge commands)
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
│   └── package.json           # UPM manifest (v1.1.80)
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

### CRITICAL: Build/test cadence — batch, don't iterate
**Finish the whole change before compiling or testing it. One verify pass at the end, not one per edit.**

- A `dotnet build` is ~30s and a publish is minutes; a build after each edit turns a 5-edit change into 3 idle minutes and burns a turn each time
- Related edits across several files are one unit of work — make them all, then build once
- Rebuilding mid-change also collides with running processes: the Roslyn `DAEMON` and any in-flight command hold `bin/Debug/.../clibridge4unity.exe`, so a needless build fails with `MSB3027 file is locked` and costs another turn to clear
- Exceptions, where an early build genuinely informs the next edit: an unfamiliar API whose signature you're guessing at, or a syntax-level change to a file you can't otherwise validate
- Same rule for tests — run the suite when the feature is done, not after each piece
- Test failures on Package code usually mean **Unity hasn't recompiled**, not that the code is broken (see the Testing Mandate above)

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
The Package is split into 9 asmdefs to minimize recompilation:
- **clibridge4unity.Core** - BridgeServer, CommandRegistry, SessionKeys (stable, rarely changes)
- **clibridge4unity.Commands.Core** - PING, STATUS, HELP, COMPILE, REFRESH, LOG
- **clibridge4unity.Commands.Scene** - Scene manipulation, play mode, windows
- **clibridge4unity.Commands.Prefab** - Prefab operations
- **clibridge4unity.Commands.Component** - Component inspection & modification
- **clibridge4unity.Commands.Asset** - Asset search
- **clibridge4unity.Commands.Code** - Runtime code execution and tests (CODE_EXEC, CODE_EXEC_RETURN, TEST)
- **clibridge4unity.Commands.UI** - UI discovery, rendering
- **clibridge4unity.Capture** - Capture Context panel (EditorWindow only, no commands; references Core alone)

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
- **LINT/COMPILE discipline:** both are reactive troubleshooting tools, never routine steps — when auto-refresh is on Unity compiles on focus, so the user has usually already compiled before asking for a test. Run them only when something isn't working as expected (STATUS errors, stale results, CODE_EXEC can't see a new type). Two things invert that default:
  - **Auto-refresh may be off** (`Edit > Preferences > Asset Pipeline > Auto Refresh` — commonly disabled on large projects, where constant reimports make the editor unusable). With it Disabled nothing compiles until Ctrl+R or a `COMPILE`, so stale results are the norm and you must compile before trusting anything type-dependent. `STATUS` reports uncompiled script changes either way — trust it over the heuristic.
  - Editing this repo's Package code with Unity backgrounded needs `COMPILE force` (the change watcher can't see the external `file:` package).
- `LINT [warnings]` - **Default: offline syntax + UXML/USS well-formedness check (~1s).** Catches missing braces, unclosed strings, bad keywords, malformed C#/UXML/USS. Daemon FileSystemWatcher → catches errors in NEW files Unity hasn't seen. Fails fast at 20s on huge projects.
- `LINT unity [warnings]` - Unity-faithful **per-asmdef** compile (~5-60s). Asmdef-aware (avoids cross-asmdef type collision false positives). Catches missing methods, type errors, missing usings. 60s budget — falls back to COMPILE if exceeded.
- `COMPILE` - Force script recompilation (Unity-side, triggers domain reload, breaks pipe). The ground truth — use when LINT modes give false positives or you need source generators / post-compile callbacks. Bridge auto-blocks all commands during Unity Player Build (returns clear error instead of timing out).
  - **Never pipe it through `tail`/`head`** — both buffer to EOF, so a multi-minute reload prints nothing and looks hung (this has caused real cancellations mid-compile). The output *is* the progress; run it bare and filter afterwards with `LAST`. Progress goes to stderr, results to stdout.
  - **Never loop it.** Guarded CLI-side ([CompileGuard.cs](clibridge4unity/CompileGuard.cs)): `skipped: uptodate` (exit 0 — assemblies newer than every source, nothing to do) or `skipped: looping` (exit 1 — repeated attempts with nothing changed; something is blocking compilation and retrying cannot clear it). `COMPILE force` bypasses both.
  - **Blocked states are terminal:** play mode → `STOP` first; Player Build → wait. Branch on exit code (`0` ok · `11` compile errors · `12` play mode · `13` safe mode · `14` timeout · `10` no connection), not on grepping stdout — a clean compile emits no error lines.
  - **Don't run `LINT unity` then `COMPILE`** — `LINT unity` is the cheap substitute for `COMPILE`; doing both pays twice and answers nothing new.
  - Wait budget adapts from measured history (`~/.clibridge4unity/.compile_times`), never below the server's 300s hint; overruns are recorded so a slow project raises its own ceiling.
- `REFRESH` - Force asset database refresh
- `LOG [filter]` - Get bridge-captured Unity **console** logs (over the pipe); use `LOG ui errors` for current USS/UXML/TSS import errors
  - Commands that reference `.uss`, `.uxml`, or `.tss` assets append matching UI Toolkit import errors automatically
  - `LOG` needs the bridge running and reads Unity's in-memory console — when it's empty or the pipe is down, use `EDITORLOG` (CLI-side) to read the on-disk `Editor.log` instead
- `CONTEXT [--hierarchy] [--refs] [--brief] [--no-console]` - Snapshot current editor/game state as paste-ready markdown: active scene (+ dirty flag), additively-loaded scenes, Prefab Mode target, play/pause, compiling/importing, Unity + product version, the current `Selection` with serialized fields, console errors. See [ContextCommand.cs](Package/Editor/Commands/Core/ContextCommand.cs).
  - **Prefab Mode is checked first** — `SceneManager.GetActiveScene()` still reports the scene you left, which would describe something the user isn't looking at.
  - One command rather than three (`SELECTION`+`INSPECTOR`+`LOG`) because each pipe round trip queues on the main thread, and a busy editor has none spare. Calls INSPECTOR through `CommandRegistry`'s `MethodInfo` so Commands.Core doesn't depend on Commands.Component.
  - Written automatically beside every `CAPTURE`/`RECORD` artefact as `<name>.context.md` — collected **at capture time**, since by the time the panel is opened the selection has moved, play mode has exited and the console has scrolled.
- `VISIBLE x,y,w,h` - What is inside a screen rectangle (physical desktop pixels). Also reachable as `CONTEXT --rect x,y,w,h`, which is how every `CAPTURE` annotates its screenshot. See [RegionVisibility.cs](Package/Editor/Commands/Core/RegionVisibility.cs).
  - **The rect arrives in physical pixels; every Unity coordinate is in scaled points.** At 175% those differ by 1.75x, so a naive comparison resolves to the wrong view rather than failing. Everything converts through `EditorGUIUtility.pixelsPerPoint`.
  - **Docked tabs overlap** — Scene and Game share a rect, so only the frontmost tab counts (same `m_Panes`/`selected` reflection `WINDOWS` uses). Without it a Game-view region resolves to the Scene view behind it at a convincing 100%.
  - Scene view uses renderer-bounds projection. `HandleUtility.PickRectObjects` would be ideal and is **unusable**: measured, it throws `NullReferenceException` from a bridge command and still throws after `Handles.SetCamera` — it needs state that exists only inside the Scene view's own OnGUI.
  - Game view maps through `GameView.targetInView`/`gameMouseScale` (internal, so reflection; degrades to an explicit "could not map" rather than guessing), then resolves: uGUI and UI Toolkit by **rect overlap, not raycast** (a raycast only sees `raycastTarget=true`, missing every decorative image a UI screenshot is usually about), and world objects by physics rays with renderer-bounds projection as a second tier for collider-less geometry.
  - Inspector/Hierarchy/Console regions are **not** parsed — IMGUI leaves no queryable model, and the useful answer is the Selection or console output already in the response.
  - Field detail for what it found is capped (~8 KB in-Package, 16 KB on the clipboard); the full text is always in the `.context.md` beside the capture.
- `MENU path` - Execute a Unity menu item (e.g. `MENU Window/General/Console`)
- `PROFILE` - Control the profiler and analyse captured performance data. Reads the frame buffer, so **every analysis subcommand works identically on a loaded `.data` capture, a live play-mode session, and a paused game** — `ProfilerDriver` serves all three through the same frame views.
  - `PROFILE` / `enable` / `disable` / `clear` - status (leads with capture flags) and recording control
  - `PROFILE deep on|off` - deep profiling. **This is the lever that decides whether you get per-method rows or only instrumented markers.** Off = engine markers + MonoBehaviour `[Invoke]` rows only; on = every managed method. Toggling recompiles and reloads the domain (drops the pipe, like COMPILE), and inflates absolute timings — read ratios, not milliseconds
  - `PROFILE load <path.data>` / `PROFILE save <path.data>` - read/write a capture. Load replaces the current frames; a multi-GB capture is a synchronous main-thread read, hence the 600s timeout hint
  - `PROFILE breakdown` - **start here.** The evidence pack: capture flags, frame distribution, top-level split (PlayerLoop vs EditorLoop vs profiler overhead, with a "% of frame is overhead" line), thread balance, then four separate rankings (total / self / calls / GC alloc), and spike frames listed apart from the steady state
  - `PROFILE frames [top:N]` - most expensive frames, each with its heaviest marker
  - `PROFILE top [count:N] [by:self|total|calls|gc] [filter:X] [spikes]` - leaf ranking. `filter:MTD2` scopes it to your own code
  - `PROFILE group [by:assembly|namespace|class|prefix] [filter:X] [sort:…]` - **re-aggregate at a coarser grain.** Answers "which assembly/class owns the frame", which a 2000-row leaf list cannot
  - `PROFILE tree <marker> [depth:N] [min:ms] [frame:N]` - drill into the call tree under a marker. Single-frame by design (averaging tree *shapes* invents parents that never co-occurred); defaults to the median steady frame, pin a spike with `frame:N`
  - `PROFILE callers <marker> [spikes]` - reverse view: which parents issue a marker and what each costs. The way to turn "290 SRPBatcher.Flush calls" into "167 of them are the shadow pass"
  - `PROFILE threads [frame:N]` / `PROFILE hierarchy [min:] [depth:] [frame:] [thread:]`
  - Common options: `thread:N from:N to:N`. Rankings default to **steady-state frames** (≤1.5× median); hitches are reported separately rather than averaged in
  - **Interpreting:** an in-editor capture spends a large share of each frame in `EditorLoop` + `Profiler.*`, none of which exists in a player build — `breakdown` labels those rows and prints the overhead share. For trustworthy absolute numbers, capture from a player build with deep profiling off; use deep profiling only to find *which* method inside an already-identified hot marker is responsible
- `BUILD [--run] [--dev] [--output <path>]` - Build the Unity Player (active target). Streams progress + errors. `--run` launches Standalone after success. Default output: `Builds/<Target>/<ProductName>`. Other commands auto-block during the build.

### Code
- `ANALYZE query` - **The main reference point.** Unified code + asset-wiring analysis (offline, daemon-served; aliases: `CODE_ANALYZE`, `CODE_SEARCH`):
  - `ANALYZE ClassName` → deep view (definition, usages, derived types, GetComponent sites, own members) + **Asset wiring** section (scenes/prefabs the script is attached to, SO instances, UnityEvent targets — from the daemon's serialized asset graph)
  - `ANALYZE ClassName.Member` → zoom into one member
  - `ANALYZE method:Name` | `field:Name` | `property:Name` | `inherits:Type` | `attribute:Name` → kind-prefixed listing across the codebase
  - `ANALYZE usedby:Assets/Foo.prefab` (or `usedby:ClassName`) → reverse GUID lookup: every scene/prefab/SO/UXML referencing the asset (index-backed, instant)
- `MAP task keywords` - Task-oriented project map (offline, daemon-served). Free keywords (e.g. `MAP double jump`) → one dossier: matching scripts with attach sites, scenes (with build index), prefabs, SO config assets, UXML/`.inputactions`, UnityEvent wiring. The "where do I start?" command. See [AssetGraph.cs](clibridge4unity/AssetGraph.cs)
- **Daemon memory / package residency:** the daemon holds syntax trees for **user code only** (`Assets/`, non-PackageCache `Packages/`). `Library/PackageCache` keeps its source text (needed for the query pre-filter) plus a harvested type-name set, and is re-parsed on demand when a query matches it. On a large project this is ~400 MB less RSS and a faster index, at the cost of slower broad `kind:` queries (e.g. `method:Update`, which matches thousands of package files). Set `CLIBRIDGE_INDEX_PACKAGES=1` to make packages resident again and trade the memory back for that latency.
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
- `PLAYMODE` - Get current play mode state, including **who owns it** (`owner: user (entered manually)` vs `owner: agent <peer-id>`)

### Play-mode gate (cross-window, automatic)
One editor is shared by a person and several agent windows. A play session someone else started must not be trampled, so anything that could **change** the editor is gated while another window (or the user) owns play mode. See [PlayGate.cs](clibridge4unity/PlayGate.cs), [GateNotify.cs](clibridge4unity/GateNotify.cs), [PlayOwnership.cs](Package/Editor/Core/PlayOwnership.cs).
- **Attribution**: Unity never reports *why* play mode changed, so `PLAY` stamps a short-lived claim (15s) in `SessionState` immediately before the transition; the `playModeStateChanged` handler resolves it. No fresh claim = a human pressed Play. SessionState because entering play mode can domain-reload *between* the claim and its resolution.
- **Scope is an allowlist of read-only commands** (`PING STATUS LOG FIND INSPECTOR SCREENSHOT ANALYZE …`); everything else gates. A command added later defaults to asking rather than silently mutating someone's session.
- **Prompt**: a `TaskDialogIndirect` desktop dialog with three actions — *run it now in my play session* (play mode untouched; right for `CODE_EXEC`, which carries its own compiler), *exit play mode and hand over* (issues `STOP --force` then runs), *not now*. Not a toast: toasts from an unpackaged single-file exe need an AUMID + Start Menu shortcut and a COM/URI activator, all of which can rot silently.
- **Owner is read from the heartbeat file, not the pipe** — the editor is busy in a play session exactly when we need to ask.
- **Answer from any terminal** (works with no pipe): `REQUESTS` lists what's waiting · `ALLOW <id>` runs it in the live session · `ALLOW <id> yield` exits play mode and hands over · `ALLOW <id> always` runs it and stops asking for that window · `DENY <id>`. Requests carry the caller's PID and are swept when that process exits — nobody is waiting for the answer.
- **Grants — the gate asks once, not once per command.** Asking per command is unusable, not safe: one task can issue a dozen mutating commands. The dialog's *"Don't ask again while this play session lasts"* checkbox stores a grant scoped to `(window, playOwnerSince)`, so leaving play mode and re-entering invalidates it — permission given for one session never silently carries into the next. *"Always allow this window"* (or `ALLOW <id> always`) uses a sentinel session id that survives across sessions until revoked. `REQUESTS` reports the standing permission; `REQUESTS --forget` revokes it; `CLIBRIDGE_NO_PLAYGATE=1` disables the gate for a window entirely.
- **When *you* press Play the owner is the literal string `user`**, which matches no peer id — so every window, including your own terminal, is "not the owner". That is why grants matter: without them a manually-started play session prompts on all 31 mutating commands.
- `STOP` refuses to end a session it doesn't own; `STOP --force` overrides.
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
- `CAPTURE [--out <png>] [--full]` - Drag-select a screen region (Esc/right-click cancels). Freezes the desktop first, then lets you select on the still — so the crop is the frame you aimed at, not whatever Unity repainted mid-drag. Works while Unity is busy, importing, or showing the modal dialog you're trying to screenshot. See [RegionCapture.cs](clibridge4unity/RegionCapture.cs).
  - `CAPTURE --daemon` - tray icon + global hotkeys: **PrtScn** grabs a region, **Ctrl+PrtScn** starts/stops a recording (one chord toggles — reaching for a different key to stop is when you fumble it). `--status` / `--stop` manage it; `--autostart on|off` writes/removes a Startup-folder `.cmd`. Refuses to double-arm — two daemons means `RegisterHotKey` fails in the loser and the chord silently dies.
    - Each chord registers independently and reports which one lost. PrtScn is the most contested key on Windows: *Settings > Ease of Access > Keyboard > "Use the PrtScn button to open screen snipping"* claims it at the OS level, as do OneDrive/Dropbox/ShareX.
    - **Windows parks every newly-registered tray icon in the hidden overflow** and there is no supported API to promote one (deliberate — it stops apps fighting over tray space). Click the `^` and drag it out. The daemon says this on startup so a hidden icon doesn't read as a broken one.
- `RECORD [--audio both|mic|system|none] [--seconds N] [--fps N] [--transcribe] [--full]` - Region screen recording with audio via ffmpeg. `--stop` finalises, `--status` reports, `--devices` lists what was auto-detected. See [ScreenRecorder.cs](clibridge4unity/ScreenRecorder.cs).
  - **An assistant cannot watch an mp4 or hear audio**, so a finished recording is post-processed into the two things that *do* carry into a prompt: a 4x3 **contact sheet** of evenly-sampled frames, and (with `--transcribe`) a **transcript** of the narration. The video is kept for the human. Skipping that step yields a feature that looks like it works and communicates nothing.
  - Frames are evenly spaced, not scene-cut detected — a one-frame UI pop is exactly what scene detection discards as insignificant.
  - Stop is signalled by a flag file the recorder polls, never by killing ffmpeg: ffmpeg must write the moov atom on the way out, and a killed one leaves an unplayable mp4 with the whole recording trapped inside.
  - ffmpeg is required and **not** bundled (~80 MB against a ~40 MB exe); absence prints the `scoop`/`winget` line. Audio device names are machine-specific so they are discovered, not configured: a loopback device for system audio (`virtual-audio-capturer`, Stereo Mix, VB-Cable) is not shipped by Windows, and its absence falls back to mic-only rather than failing.
  - `--transcribe` uses ffmpeg's built-in `whisper` filter (needs an `--enable-whisper` build), downloading `ggml-base.en.bin` (~148 MB) to `~/.clibridge4unity/whisper/` on first use.
  - Every capture also refreshes `latest.png` + `latest.txt` under `%TEMP%/clibridge4unity/captures/`, which is how the Unity panel below picks up a hotkey grab it never asked for by path.
- **Capture Context panel** (Unity-side, `Tools > CLI Bridge for Unity > Capture Context`, Ctrl+Shift+K) - builds a paste-ready prompt: your question + the captured region (or a recording's frames + transcript) + INSPECTOR dumps for GameObjects ticked in a hierarchy tree (seeded from the live `Selection`) + console errors, copied to the clipboard. Records via the CLI and tracks state from the recorder's pid file, so a recording started from the tray or another terminal shows up in the panel too. Deliberately **Ctrl+Shift+K, not a PrtScn chord** — a `RegisterHotKey` claim wins system-wide, so sharing the daemon's chord would make the menu shortcut silently dead. Its own asmdef (`clibridge4unity.Capture`) referencing only Core; it reaches INSPECTOR/LOG through `CommandRegistry`'s `MethodInfo` rather than referencing the command assemblies. Nothing runs at rest — no `[InitializeOnLoad]`, and the `EditorApplication.update` hook exists only while a capture child-process is in flight. See [CaptureContextWindow.cs](Package/Editor/Capture/CaptureContextWindow.cs).
- `EDITORLOG [N|errors|grep PAT|path]` - Tail Unity's on-disk `Editor.log` (aliases: `EDITORLOGS`, `ELOG`). No pipe needed — works when the bridge isn't running in that instance (clones, crashed/busy Unity) and surfaces import/compile/crash/load lines `LOG` can't reach. Resolves the per-instance log (`-logFile` arg → header-matched `Editor*.log` → default `%LOCALAPPDATA%\Unity\Editor\Editor.log`) and prints which file it read. `errors` filters to error/exception/fail lines; `grep PAT` is a case-insensitive regex; `path` prints the resolved path only.
- `VSCODE` - Install the bundled VSCode/Cursor status-bar extension into detected editors (`code`/`code-insiders`/`cursor`/`codium`/`windsurf`). The `.vsix` is embedded in the CLI exe (built from `vscode-extension/`, version-locked to the CLI) and installed via `<editor> --install-extension <vsix> --force`; idempotent (skips if the editor already has an equal-or-newer version). `SETUP` prints a hint pointing here but does not auto-install.
- `PACKAGES <sub>` - Asset Store library sync + `.unitypackage` extraction. No pipe, no Editor, no project — downloading an asset is otherwise a Package Manager window operation (i.e. boot Unity to pull a file). See [PackagesCommand.cs](clibridge4unity/PackagesCommand.cs).
  - `PACKAGES auth <cookie>` - store + verify a Unity ID session. **The login is the Unity ID that owns the purchases** — there is no separate Asset Store account. Cookie is `__Secure-next-auth.session-token` from a logged-in `assetstore.unity.com` tab (DevTools > Application > Cookies); stored under `~/.clibridge4unity/packages/<account>/`.
  - `PACKAGES list [filter]` · `PACKAGES cache [filter]` - owned assets / what the Editor already downloaded
  - `PACKAGES download <folder> [filter]` - **sync, not a blind pull.** Anything already in `<folder>` at the right size is skipped on headers alone (no body transferred), so re-running to stay current is cheap. Live per-file progress with rate + ETA; `.part` + `Content-Length` verify + rename, so an interrupted run resumes (via `Range`) and a killed one never leaves a half-file looking complete. `-n` reports what's missing without transferring.
  - `PACKAGES extract <src> [dest] [--meta] [--preview]` - offline, no login. Use `--meta` when the output goes into a real project or Unity regenerates GUIDs and in-package references break.
  - `--account NAME` keeps a separate cookie + library list per Unity ID (a cache can span two accounts).
  - Auth chain: cookie → `GET assetstore.unity.com/api/auth/session` → `.accessToken.genesis_access_token` (~2h) → `Authorization: Bearer` on `packages-v2.unity.com`. `packages-v2` rejects cookies outright and the cookie is host-scoped to the store, so the exchange is mandatory. The cookie outlives the token by weeks: a 401 mid-run re-mints and retries, it does not mean "log in again". A 403 on a signed URL means the signature aged out — that one package is re-resolved once.
  - Delisted assets 404 on resolve. Expected, logged as `UNAVAILABLE`, not a failure.
  - `pathname` inside a package is untrusted input: `..`, `/absolute` and drive-absolute destinations are refused rather than written.
- `RELEASENOTES [from] [to] [-c Cat,Cat] [-g regex] [--format md|json|text] [-o file] [--url]` - Fetch Unity Editor release notes for a version range from `release-notes.ds.unity3d.com` (pure HTTP, no Unity/project needed). **Bare** = current project's Unity version (`ProjectVersion.txt`) → latest available; **from-only** = that version → latest. `--list [substr]` browses versions. `-c` filters categories (substring, comma-OR), `-g` regex-filters note text — use it to check whether a hard bug matches a known Unity fix/regression. Issue links (`UUM-####`) resolve to the issue tracker. Aliases: `UNITYNOTES`, `RELNOTES`. See [clibridge4unity-release-notes skill](clibridge4unity/skills/clibridge4unity-release-notes.md).
