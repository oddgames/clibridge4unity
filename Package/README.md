# CLI Bridge for Unity

![Unity Version](https://img.shields.io/badge/Unity-2021.3%2B-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)
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

The Unity package is installed (and version-pinned) automatically by `clibridge4unity SETUP`. To install manually via Unity Package Manager, use this git URL with the latest [release tag](https://github.com/oddgames/clibridge4unity/releases):

```
https://github.com/oddgames/clibridge4unity.git?path=Package#vX.Y.Z
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

This does four things:
1. Adds the UPM package to your Unity project's `Packages/manifest.json` (via git URL with `?path=Package`, version-matched)
2. Unpacks per-task AI skills into `.claude/skills/`
3. Checks Unity Editor connectivity
4. Generates `CLAUDE.md` and `AGENTS.md` with tool documentation for AI-assisted development

Keep everything current with `clibridge4unity UPDATE` (self-updates the CLI, package tag, and skills).

## Quick Start

```bash
clibridge4unity PING                    # Test connection
clibridge4unity STATUS                  # Editor state, compile/UI Toolkit status
clibridge4unity HELP                    # List all commands from Unity
clibridge4unity COMPILE --wait          # Recompile and wait for results
clibridge4unity LOG errors              # Show bridge-captured Unity errors
clibridge4unity EDITORLOG errors        # Read the on-disk Editor.log (no pipe needed)
```
Commands that reference `.uss`, `.uxml`, or `.tss` assets also append matching UI Toolkit import errors to their normal response.

The CLI auto-detects the Unity project from the current directory. Use `-d <path>` to specify explicitly.

## Commands

Use `clibridge4unity -h` for the full list; `HELP` queries your running Unity instance (authoritative).

### Core
| Command | Description |
|---------|-------------|
| `PING` / `PROBE` | Test connection / quick main-thread health check |
| `STATUS` | Unity status (compiling, playing, errors, version) |
| `DIAG` | Diagnostics — answers even when the main thread is blocked |
| `HELP` | List all available commands |
| `COMPILE` / `REFRESH` | Force script recompilation / asset database refresh |
| `LOG [filter]` | Unity console logs (`errors`, `warnings`, `ui errors`, `last:N`, `since:ID`) |
| `MENU path` | Execute any Unity menu item |
| `PROFILE [enable\|disable\|clear\|hierarchy]` | Drive the profiler, dump frame hierarchies |
| `BUILD [--run] [--dev] [--output <path>]` | Build the player (streams progress + errors) |
| `CANCEL <NAME>` / `CANCEL --all` | Abort an in-flight long-running command |

### Code
| Command | Description |
|---------|-------------|
| `CODE_EXEC code` | Execute C# in Unity, fire-and-forget (alias: `EXEC`; `@file` for large scripts) |
| `CODE_EXEC_RETURN code` | Execute and return the result (alias: `EVAL`; `--inspect [depth]`, `--trace`) |
| `TEST [mode] [groups] [--category X] [--tests A,B]` | Run EditMode/PlayMode tests, streaming; `TEST list` to enumerate |
| `DEBUG` | Debugger stub |

### Scene & Play Mode
| Command | Description |
|---------|-------------|
| `INSPECTOR [path] [--children] [--brief] [--filter X]` | Hierarchy / GameObject / prefab / asset inspection with serialized fields |
| `CREATE name` / `DELETE path` | Create / delete GameObjects |
| `FIND name` | Find by name (`prefab:Assets/X.prefab/Child` to search inside prefabs) |
| `SAVE` / `LOAD path` | Save / load scenes |
| `PLAY [scene]` / `STOP` / `PAUSE` / `STEP` / `PLAYMODE` | Play-mode control and state |
| `SCENEVIEW frame\|2d\|3d` | Control the Scene view |
| `GAMEVIEW 1280x720` | Set Game view resolution |
| `WINDOWS` | List editor windows with positions |

### Prefab & Component
| Command | Description |
|---------|-------------|
| `PREFAB_CREATE name path` | Create a prefab asset |
| `PREFAB_INSTANTIATE path [parent]` | Instantiate a prefab in the scene |
| `PREFAB_SAVE name [path]` | Save a scene GameObject as prefab / apply instance changes |
| `COMPONENT_SET obj comp field value` | Set field/property (vectors `"1,2,3"`, colors `#hex`) |
| `COMPONENT_ADD obj type` / `COMPONENT_REMOVE obj type` | Add / remove components |

### Asset & UI
| Command | Description |
|---------|-------------|
| `ASSET_SEARCH query` | Unity Search syntax, did-you-mean suggestions on miss |
| `ASSET_DISCOVER [category]` | Discover assets (ui, sprites, prefabs, scenes, fonts, shaders, materials, models, variants); `UI_DISCOVER` = `ASSET_DISCOVER ui` |
| `ASSET_MOVE` / `ASSET_COPY` / `ASSET_DELETE` / `ASSET_MKDIR` / `ASSET_LABEL` | GUID-preserving asset management (batch-capable) |
| `ASSET_RESERIALIZE paths` | Re-validate and re-import corrupted YAML (alias: `REIMPORT`) |
| `SCREENSHOT <target>` | Editor views, `gameview`, `camera [WxH]`, GameObjects, prefabs, UXML (two views by default: as-authored + all-hidden-revealed; `--el`/`--reveal`/`--show`/`--hide`) |

### CLI-side (no Unity connection needed)
| Command | Description |
|---------|-------------|
| `SETUP` | Install UPM package + skills + generate CLAUDE.md/AGENTS.md (alias: `INSTALL`) |
| `UPDATE` | Self-update CLI, package tag, and skills |
| `MAP task keywords` | Offline task-oriented project map (code index + serialized asset graph) |
| `ANALYZE query` | Offline code + asset-wiring analysis; `usedby:` reverse lookups (aliases: `CODE_ANALYZE`, `CODE_SEARCH`) |
| `LINT [unity]` | Offline syntax check (~1s) / Unity-faithful per-asmdef compile |
| `EDITORLOG [N\|errors\|grep PAT\|path]` | Tail the on-disk Editor.log — works on clones and crashed/busy Unity (aliases: `ELOG`) |
| `RELEASENOTES [from] [to] [-c cats] [-g regex]` | Unity release notes for a version range; bare = project version → latest (aliases: `UNITYNOTES`, `RELNOTES`) |
| `SCREENSHOT [view]` | Window capture (editor/scene/inspector/hierarchy/console/project/profiler) |
| `LAST [-head N\|-tail N\|-grep PAT\|-list]` | Replay cached responses without re-running |
| `OPEN` / `KILL` | Launch / force-terminate Unity for this project |
| `WAKEUP` / `DISMISS` | Bring Unity to foreground / close modal dialogs |
| `VSCODE` | Install the bundled VSCode/Cursor status-bar extension |
| `SERVE [--port N]` | Local file server (default `localhost:8420`) |

### Multi-window coordination
When multiple terminals/AI windows share one Unity editor, the CLI automatically prints `[conflict] WARNING:` on stderr before commands that would stomp another window (COMPILE/REFRESH break pipes, PLAY/STOP change shared play state, BUILD blocks everyone, same-asset writes clobber). Advisory only.

## Compile and Wait

The `--wait` flag compiles and reports results in one command:

```bash
clibridge4unity COMPILE --wait                    # Show errors
clibridge4unity COMPILE --wait --log-filter warnings  # + warnings
clibridge4unity COMPILE --wait --log-filter all       # All logs
```

Returns exit code 1 if there were compilation errors. `COMPILE`/`LINT` are reactive troubleshooting tools — Unity auto-compiles on focus, so don't run them after routine edits; reach for them when something isn't working as expected. Note `COMPILE` recompiles C# only; `.shader`/`.uxml` edits are asset imports (use `REFRESH` + `LOG errors`).

## Architecture

- **Named Pipes**: Secure local IPC, pipe name derived from username + project path hash
- **Async**: All server operations are non-blocking async/await
- **Main Thread**: Uses SynchronizationContext + polling thread (works even when Unity is minimized)
- **Attribute-Based**: Commands use `[BridgeCommand]` attribute for automatic discovery
- **Modular**: 8 separate assembly definitions per command category to minimize recompilation
- **Version-Locked**: CLI version, GitHub tag, UPM package version, and the embedded VSCode extension are always in sync
- **Offline daemon**: `MAP`/`ANALYZE`/`LINT` run against a persistent Roslyn + asset-graph index — Unity need not be open

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

Place in `Package/Editor/Commands/` within an asmdef that references `clibridge4unity.Core`. Aliases via `Aliases = new[] { "SHORTNAME" }`.

## Requirements

- Unity 2021.3+ (LTS)
- Windows
- .NET 8 SDK (only for building CLI from source)

## Troubleshooting

- **Connection timeout**: Ensure Unity is running with the package. Check console for `[Bridge] Server started`.
- **Commands lose connection during compile**: Expected — Unity reloads assemblies. Use `--wait` to handle automatically.
- **Main thread timeout**: Unity is backgrounded. CLI auto-retries with `WAKEUP`. `DIAG` always answers.
- **`LOG` is empty / bridge not running in that instance**: Read the on-disk log instead — `EDITORLOG errors` (works on clones and crashed Unity).
- **Package not found warning**: Run `clibridge4unity SETUP` to install the UPM package.
- **A bug that smells like Unity itself**: `RELEASENOTES -g "<symptom>"` checks the official release notes for a matching known issue/fix.
