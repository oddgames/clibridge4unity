# CLI Bridge for Unity

![Unity Version](https://img.shields.io/badge/Unity-2021.3%2B-blue)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

A lightweight CLI tool for automating Unity Editor via Named Pipes. Send commands from your terminal, scripts, or AI tools (like Claude) to control Unity without touching the GUI.

## Installation

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex
```

This downloads the CLI, installs it to `~/.clibridge4unity/`, and adds it to your PATH.

### Manual Install

Download `clibridge4unity` from [Releases](https://github.com/oddgames/clibridge4unity/releases), place it somewhere on your PATH.

### Unity Package (UPM)

The Unity package is installed automatically by `clibridge4unity SETUP`. To install manually via Unity Package Manager, use this git URL:

```
https://github.com/oddgames/clibridge4unity.git?path=Package#v1.0.54
```

**Important:** The `?path=Package` suffix is required — the `package.json` lives in the `Package/` subdirectory, not the repo root. Without it, Unity will report "Repository does not contain a package manifest."

### Build from Source

```bash
cd clibridge4unity
dotnet publish -c Release
# Binary at: bin/Release/net8.0/win-x64/publish/clibridge4unity.exe
```

## Setup

After installing the CLI, open a terminal in your Unity project directory:

```bash
clibridge4unity SETUP
```

This does three things:
1. Adds the UPM package to your Unity project's `Packages/manifest.json` (via git URL with `?path=Package`, version-matched)
2. Checks Unity Editor connectivity
3. Generates `CLAUDE.md` and `AGENTS.md` with tool documentation for AI-assisted development

## Quick Start

```bash
clibridge4unity PING                    # Test connection
clibridge4unity STATUS                  # Editor state, compile/UI Toolkit status
clibridge4unity HELP                    # List all commands from Unity
clibridge4unity COMPILE --wait          # Recompile and wait for results
clibridge4unity LOG errors              # Show bridge-captured Unity errors
clibridge4unity LOG ui errors           # Show current USS/UXML/TSS import errors
```
Commands that reference `.uss`, `.uxml`, or `.tss` assets also append matching UI Toolkit import errors to their normal response.

The CLI auto-detects the Unity project from the current directory. Use `-d <path>` to specify explicitly.

## Commands

Use `clibridge4unity -h` to get the full list from your running Unity instance.

### Core
| Command | Description |
|---------|-------------|
| `PING` | Test connection |
| `STATUS` | Unity status (compiling, playing, version) |
| `HELP` | List all available commands |
| `COMPILE` | Force script recompilation |
| `REFRESH` | Force asset database refresh |
| `LOG [filter]` | Unity console logs (`errors`, `warnings`, `last:N`, `since:ID`) |

### Code Analysis
| Command | Description |
|---------|-------------|
| `SEARCH query` | Search code: `class:Name`, `method:Name`, `inherits:Type`, `attribute:Name` |
| `ANALYZE target` | Analyze class, member, or stack trace with source locations |
| `CODE_EXEC code` | Execute C# code in Unity (fire-and-forget) |
| `CODE_EXEC_RETURN code` | Execute C# code and return result |
| `TEST [filter]` | Run Unity tests |

### Scene & Play Mode
| Command | Description |
|---------|-------------|
| `SCENE` | Scene info and hierarchy |
| `CREATE name` | Create GameObject |
| `FIND name` | Find GameObject by name or path |
| `DELETE path` | Delete GameObject |
| `SAVE` / `LOAD path` | Save/load scenes |
| `SCENEVIEW frame\|2d\|3d` | Control Scene view |
| `WINDOWS` | List editor windows with positions |
| `PLAY [scene]` / `STOP` | Enter/exit play mode |
| `PAUSE` / `STEP` | Pause/step play mode |
| `GAMEVIEW 1280x720` | Set Game view resolution |

### Prefab & Component
| Command | Description |
|---------|-------------|
| `PREFAB_CREATE name path` | Create a prefab asset |
| `PREFAB_INSTANTIATE path [parent]` | Instantiate a prefab in the scene |
| `PREFAB_HIERARCHY path` | Prefab hierarchy with components |
| `INSPECTOR gameObject` | Inspect components on a GameObject |
| `COMPONENT_SET obj comp field val` | Set field/property on a component |
| `ADDCOMPONENT obj type` | Add a component |
| `REMOVECOMPONENT obj type` | Remove a component |

### Asset & UI
| Command | Description |
|---------|-------------|
| `ASSET_SEARCH query` | Search assets using Unity Search syntax |
| `UI_DISCOVER [filter]` | Discover sprites, fonts, prefabs, scenes |
| `RENDER path [path2 ...]` | Render prefab/UXML to PNG |
| `CONVERT_UXML prefabPath` | Convert uGUI prefab to UXML + USS |

### CLI-side (no Unity connection needed)
| Command | Description |
|---------|-------------|
| `SETUP` | Install UPM package + verify Unity + generate CLAUDE.md and AGENTS.md |
| `SCREENSHOT [view]` | Capture Unity window (editor/scene/game/inspector) |
| `WAKEUP` | Bring Unity windows to foreground |
| `DISMISS` | Close modal dialogs |

## Compile and Wait

The `--wait` flag compiles and reports results in one command:

```bash
clibridge4unity COMPILE --wait                    # Show errors
clibridge4unity COMPILE --wait --log-filter warnings  # + warnings
clibridge4unity COMPILE --wait --log-filter all       # All logs
```

Returns exit code 1 if there were compilation errors.

## Architecture

- **Named Pipes**: Secure local IPC, pipe name derived from username + project path hash
- **Async**: All server operations are non-blocking async/await
- **Main Thread**: Uses SynchronizationContext + polling thread (works even when Unity is minimized)
- **Attribute-Based**: Commands use `[BridgeCommand]` attribute for automatic discovery
- **Modular**: 8 separate assembly definitions per command category to minimize recompilation
- **Version-Locked**: CLI version, GitHub tag, and UPM package version are always in sync

## Adding Custom Commands

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

Place in `Package/Editor/Commands/` within an asmdef that references `clibridge4unity.Core`.

## Requirements

- Unity 2021.3+ (LTS)
- .NET 8 SDK (only for building CLI from source)

## Troubleshooting

- **Connection timeout**: Ensure Unity is running with the package. Check console for `[Bridge] Server started`.
- **Commands lose connection during compile**: Expected — Unity reloads assemblies. Use `--wait` to handle automatically.
- **Main thread timeout**: Unity is backgrounded. CLI auto-retries with `WAKEUP`.
- **Package not found warning**: Run `clibridge4unity SETUP` to install the UPM package.
