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

### Execute C# (built-in Roslyn compiler — no COMPILE needed)
```bash
clibridge4unity CODE_EXEC "Debug.Log(42)"       # Run C# in Unity (fire-and-forget)
clibridge4unity CODE_EXEC_RETURN "return 1+1"   # Run C# and get the result back
clibridge4unity CODE_EXEC @script.cs            # Run from file (no size limit)
```
CODE_EXEC runs on a background thread — works even when Unity's main thread is busy.

### Search & Navigate Code
```bash
clibridge4unity CODE_SEARCH class:PlayerController          # Find types, methods, fields
clibridge4unity CODE_SEARCH inherits:MonoBehaviour           # Inheritance search
clibridge4unity CODE_ANALYZE PlayerController                # Class overview
clibridge4unity CODE_ANALYZE PlayerController.TakeDamage     # Member details
```

### Inspect & Modify the Scene
```bash
clibridge4unity INSPECTOR Player                            # All components and fields
clibridge4unity COMPONENT_SET Player Transform position "(1,2,3)"
clibridge4unity SCENE                                       # Full hierarchy
clibridge4unity SCREENSHOT scene                            # Capture editor windows
```

### AI Setup
```bash
clibridge4unity SETUP       # Generates CLAUDE.md with tool reference for AI assistants
```
Run `SETUP` again after updates to regenerate the docs. The generated `CLAUDE.md` tells AI assistants which commands are available, when to use COMPILE vs CODE_EXEC, and how to handle busy/timeout errors.

## All Commands

| Category | Commands |
|----------|----------|
| **Core** | `PING` `PROBE` `DIAG` `STATUS` `HELP` `COMPILE` `REFRESH` `LOG` `STACK_MINIMIZE` |
| **Code** | `CODE_SEARCH` `CODE_ANALYZE` `CODE_EXEC` `CODE_EXEC_RETURN` `TEST` |
| **Scene** | `SCENE` `CREATE` `FIND` `DELETE` `SAVE` `LOAD` `PLAY` `STOP` `PAUSE` `STEP` `PLAYMODE` `SCENEVIEW` `GAMEVIEW` `WINDOWS` |
| **Prefab** | `PREFAB_CREATE` `PREFAB_INSTANTIATE` `PREFAB_HIERARCHY` `PREFAB_SAVE` |
| **Component** | `INSPECTOR` `COMPONENT_SET` `COMPONENT_ADD` `COMPONENT_REMOVE` |
| **Asset** | `ASSET_SEARCH` `ASSET_DISCOVER` `ASSET_MOVE` `ASSET_COPY` `ASSET_DELETE` `ASSET_MKDIR` `ASSET_LABEL` |
| **UI** | `SCREENSHOT` |
| **CLI-side** | `SETUP` `SCREENSHOT` `WAKEUP` `DISMISS` |

Run `clibridge4unity HELP` for full usage details.

## Development

### Repository Structure

```
├── clibridge4unity/           # CLI tool (.NET 8, single-file publish)
├── clibridge4unity.Tests/     # Integration tests (require running Unity)
├── Package/                   # Unity Editor package (UPM)
│   ├── Editor/Core/           # Pipe server, command registry
│   ├── Editor/Commands/       # Command implementations (8 asmdefs)
│   └── Tools/                 # Pre-built CLI binaries (win/osx/linux)
├── ConsoleUnityBridge/        # Interactive REPL console
└── UnityTestProject/          # Test Unity project
```

### Building

```bash
cd clibridge4unity
dotnet publish -c Release
# Output: bin/Release/net8.0/win-x64/publish/clibridge4unity.exe

# Install locally
cp bin/Release/net8.0/win-x64/publish/clibridge4unity.exe ~/.clibridge4unity/
# Also update the bundled binary in the UPM package
cp bin/Release/net8.0/win-x64/publish/clibridge4unity.exe Package/Tools/win-x64/
```

### Running Tests

```bash
cd clibridge4unity.Tests
dotnet test
```

Tests require Unity Editor running with `UnityTestProject` open and the bridge package compiled.

### Git Workflow

The project uses a single `main` branch with version tags:

```
main ──●──●──●──●── (all development)
       │     │
     v1.0.8  v1.0.10
```

- All commits go directly to `main`
- Each release is a git tag (`v1.0.10`, `v1.0.11`, etc.)
- No feature branches or PRs required — this is a solo-dev workflow

### Versioning

Version is tracked in multiple files, all kept in sync:

| File | Field |
|------|-------|
| `clibridge4unity/clibridge4unity.csproj` | `<Version>` **(source of truth)** |
| `Package/package.json` | `"version"` |
| `Package/Editor/Core/BridgeServer.cs` | `Version` const |

The UPM package is installed via git URL with a version tag:
```
https://github.com/oddgames/clibridge4unity.git?path=Package#v1.0.10
```

### Releasing

Releases are automated via the deploy script:

```bash
# Patch bump (1.0.10 → 1.0.11), build, tag, push, create GitHub release
python .claude/scripts/deploy.py 1.0.11

# Or use the Claude Code slash command which handles version bumping too:
# /deploy        — patch bump
# /deploy minor  — minor bump (1.0.10 → 1.1.0)
# /deploy check  — dry run
```

The deploy script:
1. Builds the CLI (`dotnet publish`)
2. Verifies the version matches in all files
3. Packages the binary into a zip
4. Commits and pushes to `main`
5. Creates a git tag (`v1.0.11`)
6. Creates a GitHub Release with the zip attached
7. Updates the local CLI installation

### Update Mechanism

The CLI checks GitHub Releases in the background and notifies users when a new version is available. Checks are throttled to once per 30 minutes and cached in `~/.clibridge4unity/.last_update_check`.

## Requirements

- Unity 2021.3+
- Windows (macOS/Linux planned)
