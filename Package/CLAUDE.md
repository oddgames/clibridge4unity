# CLI Bridge for Unity - Package Development Guide

## Package Structure
```
Package/
├── Editor/
│   ├── Core/                  # Stable core (rarely changes)
│   │   ├── BridgeServer.cs    # Named pipe server, connection handling
│   │   ├── CommandRegistry.cs # Attribute-based command discovery & dispatch
│   │   ├── BridgeCommand.cs   # [BridgeCommand] attribute definition
│   │   └── SetupWizard.cs     # CLI installer & PATH setup
│   └── Commands/              # Command implementations (one asmdef per category)
│       ├── Core/              # PING, STATUS, HELP, COMPILE, REFRESH, EXEC, LOG
│       ├── Scene/             # Scene/hierarchy/screenshot commands
│       ├── Prefab/            # Prefab creation/instantiation
│       ├── Component/         # Component field/property/event manipulation
│       ├── Code/              # CODE_EXEC, CODE_EXEC_RETURN, TEST
│       ├── Asset/             # Asset search
│       └── UI/                # UI_DISCOVER, SCREENSHOT
├── Runtime/                   # (Currently unused)
├── Tools/                     # Pre-built CLI executables (win/osx/linux)
└── package.json               # UPM manifest (v1.1.34)
```

## Key Architecture

### Named Pipe Communication
- Pipe name: `UnityBridge_{Username}_{ProjectPathHash}`
- Hash uses lowercase path with backslashes, deterministic across .NET runtimes
- Plain text protocol: `COMMAND|data\n`, responses are plain text

### Adding New Commands
Use the `[BridgeCommand]` attribute on any static method in an assembly named `clibridge4unity.*`:
```csharp
[BridgeCommand("NAME", "Description", Category = "Cat", RequiresMainThread = true)]
public static string MyCommand(string data)
{
    return Response.Success("result");
}
```
Commands are auto-discovered by `CommandRegistry` at startup. No manual registration needed.

### Response Format
```csharp
return Response.Success("message");
return Response.SuccessWithData(new { key = value });
return Response.Error("error message");
return Response.Exception(ex);
```

### Main Thread Execution
**NEVER use `EditorApplication.delayCall`** - it doesn't fire when Unity is minimized.

Use `CommandRegistry.RunOnMainThreadAsync<T>()` or set `RequiresMainThread = true` on the attribute.
The system uses a dedicated polling thread (10ms cycle) + `SynchronizationContext.Post()`.

### Code Analysis & Lint
- `CODE_ANALYZE` and `LINT` are CLI-side (Roslyn daemon process), NOT Unity-side
- Unity-side code commands are limited to `CODE_EXEC`, `CODE_EXEC_RETURN`, `TEST`, `DEBUG`
- LINT works fully offline — never connects to Unity Editor

### Lazy Log Capture
- `Application.logMessageReceived` subscribed ONLY during command execution (not at startup)
- `CommandRegistry.BeginCommandLogCapture` / `EndCommandLogCapture` wraps each command
- Idle Bridge has zero per-frame allocation from log handling
- LOG command queries Unity's Console via `LogEntries` reflection on demand

### Build-Block Guard
- All commands except read-only diagnostics (`PING`, `DIAG`, `STATUS`, `PROBE`, `HELP`, `LOG`, `STACK_MINIMIZE`) auto-block during Unity Player Build
- `BuildPipeline.isBuildingPlayer` polled at 2Hz from `EditorApplication.update` (main-thread only API), cached `volatile bool` read from threadpool
- Returns clear error instead of timing out + bubbling as a build failure

## Dependencies
- `com.unity.nuget.newtonsoft-json` - JSON serialization (for component commands)

## Testing Changes
Unity auto-recompiles on file changes. Check Unity console for:
- `[Bridge] Server started: UnityBridge_...`
- Compilation errors
- USS/UXML/TSS import errors surfaced by `STATUS` and `LOG ui errors`

## Common Patterns
- `SynchronizationContext` captured during `[InitializeOnLoad]` for main thread work
- `SessionState` for persistence across domain reloads
- Keep `[InitializeOnLoad]` paths minimal; expensive analysis/setup work should run on demand
- Assembly reload destroys all pipe connections - commands that trigger compilation must return immediately
