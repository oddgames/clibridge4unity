# Unity Bridge Project Summary

## What We Built

A Unity Editor automation framework with **47 Unity bridge commands** plus CLI-side tools such as `CODE_ANALYZE`, designed for AI-powered development (Claude integration), CI/CD pipelines, and scripted workflows.

## Project Structure

```
tool_claude_unity_bridge/
├── clibridge4unity/               # Lightweight CLI tool (.NET 8, single file)
│   └── clibridge4unity.cs         # Auto-detects Unity project, sends commands
├── clibridge4unity.Tests/         # Integration tests (MSTest)
├── Package/                       # Unity Editor package (UPM)
│   ├── Editor/
│   │   ├── Core/                  # BridgeServer, CommandRegistry, BridgeCommand
│   │   └── Commands/              # Command implementations by category
│   │       ├── Core/              # PING, STATUS, HELP, DIAG, PROBE, COMPILE, REFRESH, LOG, MENU, PROFILE, BUILD
│   │       ├── Scene/             # Scene/hierarchy/play mode commands
│   │       ├── Prefab/            # Prefab creation/instantiation/save
│   │       ├── Component/         # Component inspection & modification
│   │       ├── Code/              # CODE_EXEC, CODE_EXEC_RETURN, TEST
│   │       ├── Asset/             # Asset search
│   │       └── UI/                # UI_DISCOVER, SCREENSHOT
│   └── Tools/                     # Pre-built CLI executables (win/osx/linux)
└── UnityTestProject/              # Test Unity project
```

## Key Features

### Communication
- Named Pipes (secure local IPC)
- Automatic Unity project detection from current directory
- Manual project path override (`-d`)
- Reconnection after assembly reload

### Commands
- **Core**: PING, HELP, PROBE, DIAG, STATUS, COMPILE, REFRESH, LOG, MENU, PROFILE, BUILD
- **CLI-side**: LINT (syntax + UXML/USS, fails fast at 20s), LINT semantic (type-binding), CODE_ANALYZE, SETUP, UPDATE, OPEN, WAKEUP, DISMISS, SCREENSHOT (window capture)
- **Unity Code**: CODE_EXEC, CODE_EXEC_RETURN, TEST, DEBUG
- **Scene**: CREATE, FIND, DELETE, SAVE, LOAD, SCENEVIEW, WINDOWS, PLAY, STOP, PAUSE, STEP, PLAYMODE, GAMEVIEW
- **Prefab**: PREFAB_CREATE, PREFAB_INSTANTIATE, PREFAB_SAVE
- **Component**: COMPONENT_SET, COMPONENT_ADD, COMPONENT_REMOVE, INSPECTOR (also handles full scene + prefab assets)
- **Asset**: ASSET_SEARCH, ASSET_DISCOVER, ASSET_MOVE, ASSET_COPY, ASSET_DELETE, ASSET_MKDIR, ASSET_LABEL, ASSET_RESERIALIZE
- **UI**: UI_DISCOVER, SCREENSHOT (server-side render: prefab/UXML/scene GO)

### CLI Workflow
```bash
# Offline lint (sub-second, no Unity needed)
clibridge4unity LINT
clibridge4unity LINT semantic            # Adds type-binding (~1-15s)

# Recompile + check
clibridge4unity COMPILE
clibridge4unity STATUS

# Logs (read Unity Console on demand)
clibridge4unity LOG errors
clibridge4unity LOG ui errors
clibridge4unity LOG --filter "MyClass"
```

## Architecture

- **Pure Async**: All methods use async Task<T>
- **Non-Blocking**: Main thread marshaling via SynchronizationContext + polling thread
- **Attribute-Based**: Commands use `[BridgeCommand]` for automatic registration
- **Modular**: Separate assembly definitions minimize recompilation
- **Code Analysis**: CLI-side Roslyn/source analysis, no Unity pipe required

## Version

Current: 1.1.56
