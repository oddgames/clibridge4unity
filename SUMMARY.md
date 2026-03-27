# Unity Bridge Project Summary

## What We Built

A Unity Editor automation framework with **33 commands** for controlling Unity via Named Pipes, designed for AI-powered development (Claude integration), CI/CD pipelines, and scripted workflows.

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
│   │       ├── Core/              # PING, STATUS, HELP, COMPILE, REFRESH, LOG, STACK_MINIMIZE
│   │       ├── Scene/             # Scene/hierarchy/play mode commands
│   │       ├── Prefab/            # Prefab creation/instantiation/save
│   │       ├── Component/         # Component inspection & modification
│   │       ├── Code/              # CODE_SEARCH, CODE_ANALYZE, CODE_EXEC, TEST
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

### Commands (33)
- **Core**: PING, HELP, PROBE, DIAG, STATUS, COMPILE, REFRESH, LOG, STACK_MINIMIZE
- **Code**: CODE_SEARCH, CODE_ANALYZE, CODE_EXEC, CODE_EXEC_RETURN, TEST
- **Scene**: CREATE, FIND, DELETE, SAVE, LOAD, SCENE, SCENEVIEW, WINDOWS, PLAY, STOP, PAUSE, STEP, PLAYMODE, GAMEVIEW
- **Prefab**: PREFAB_CREATE, PREFAB_INSTANTIATE, PREFAB_HIERARCHY, PREFAB_SAVE
- **Component**: COMPONENT_SET, COMPONENT_ADD, COMPONENT_REMOVE, INSPECTOR
- **Asset**: ASSET_SEARCH
- **UI**: UI_DISCOVER, SCREENSHOT

### CLI Workflow
```bash
# Compile and wait for results
clibridge4unity COMPILE --wait

# Compile and get only errors
clibridge4unity COMPILE --wait --log-filter errors

# Get logs
clibridge4unity LOG errors
clibridge4unity LOG last:20
clibridge4unity LOG since:42
```

## Architecture

- **Pure Async**: All methods use async Task<T>
- **Non-Blocking**: Main thread marshaling via SynchronizationContext + polling thread
- **Attribute-Based**: Commands use `[BridgeCommand]` for automatic registration
- **Modular**: Separate assembly definitions minimize recompilation
- **PDB Cache**: Background-loaded debug info for fast code analysis

## Version

Current: 1.0.17
