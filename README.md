# CLI Bridge for Unity

A CLI tool for automating Unity Editor via Named Pipes. Send commands from your terminal, scripts, or AI tools (like Claude).

## Install

```powershell
irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex
```

Then in your Unity project directory:

```bash
clibridge4unity SETUP
```

This installs the UPM package and generates a `CLAUDE.md` for AI-assisted development.

## What AI Assistants Can Do

### Compile, Execute & Analyze
```bash
clibridge4unity COMPILE --wait                  # Recompile, wait, report errors (exit code 1 on failure)
clibridge4unity CODE_EXEC "Debug.Log(42)"       # Run C# in Unity (fire-and-forget)
clibridge4unity CODE_EXEC_RETURN "return 1+1"   # Run C# and get the result back
clibridge4unity CODE_EXEC @script.cs            # Run a file (no command-line length limit)
```

### Search & Navigate Code
```bash
clibridge4unity CODE_SEARCH "class:PlayerController"        # Find types, methods, fields, properties
clibridge4unity CODE_SEARCH "inherits:MonoBehaviour"         # Inheritance search
clibridge4unity CODE_SEARCH "refs:TakeDamage"                # Find usages across source files
clibridge4unity CODE_ANALYZE PlayerController                # Class overview: members, source, inheritance
clibridge4unity CODE_ANALYZE "PlayerController.TakeDamage"   # Member details, XML docs, callers
```

### Inspect & Modify the Scene
```bash
clibridge4unity INSPECTOR Player                            # All components and fields
clibridge4unity COMPONENT_SET Player Transform position "(1,2,3)"
clibridge4unity SCENE                                       # Full hierarchy
clibridge4unity SCREENSHOT scene                            # Capture editor windows
```

## All Commands

| Category | Commands |
|----------|----------|
| **Core** | `PING` `STATUS` `HELP` `COMPILE` `REFRESH` `LOG` `STACK_MINIMIZE` |
| **Code** | `CODE_SEARCH` `CODE_ANALYZE` `CODE_EXEC` `CODE_EXEC_RETURN` `TEST` |
| **Scene** | `SCENE` `CREATE` `FIND` `DELETE` `SAVE` `LOAD` `PLAY` `STOP` `PAUSE` `STEP` `SCENEVIEW` `GAMEVIEW` `WINDOWS` |
| **Prefab** | `PREFAB_CREATE` `PREFAB_INSTANTIATE` `PREFAB_HIERARCHY` `PREFAB_SAVE` |
| **Component** | `INSPECTOR` `COMPONENT_SET` `COMPONENT_ADD` `COMPONENT_REMOVE` |
| **Asset & UI** | `ASSET_SEARCH` `UI_DISCOVER` `UI_RENDER` |
| **CLI-side** | `SETUP` `SCREENSHOT` `WAKEUP` `DISMISS` |

Run `clibridge4unity HELP` for full usage details.

## Requirements

- Unity 2021.3+
- Windows (macOS/Linux planned)
