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
│       ├── Code/              # CODE_ANALYZE, CodeSearch, PdbCache
│       ├── Asset/             # Asset search
│       └── UI/                # UI_DISCOVER, SCREENSHOT
├── Runtime/                   # (Currently unused)
├── Tools/                     # Pre-built CLI executables (win/osx/linux)
└── package.json               # UPM manifest (v1.0.63)
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

### Code Analysis
- `CodeSearch.cs` uses reflection for type info + regex for source locations
- `PdbCache` loads .pdb debug info on background thread at startup (InitializeAsync pattern)
- Query syntax: `class:Name`, `inherits:Type`, `method:Name`, `field:Name`, `attribute:Name`

## Dependencies
- `com.unity.nuget.newtonsoft-json` - JSON serialization (for component commands)
- Mono.Cecil - PDB reading (included in Unity)

## Testing Changes
Unity auto-recompiles on file changes. Check Unity console for:
- `[Bridge] Server started: UnityBridge_...`
- Compilation errors

## Common Patterns
- `SynchronizationContext` captured during `[InitializeOnLoad]` for main thread work
- `SessionState` for persistence across domain reloads
- `Task.Run()` for background work (file I/O, PDB loading)
- Assembly reload destroys all pipe connections - commands that trigger compilation must return immediately
