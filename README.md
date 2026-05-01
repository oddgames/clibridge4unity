# CLI Bridge for Unity

A CLI tool for automating Unity Editor via Named Pipes. Send commands from your terminal, scripts, or AI assistants like Claude, ChatGPT/Codex, and other coding agents.

## Install

```powershell
irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex
```

Then in your Unity project directory:

```bash
clibridge4unity SETUP
```

This installs the UPM package and generates assistant docs: `CLAUDE.md` for Claude Code and `AGENTS.md` for ChatGPT/Codex.

## What It Does

### Execute C# in Unity (built-in Roslyn compiler)
```bash
clibridge4unity CODE_EXEC "Debug.Log(42)"       # Fire-and-forget
clibridge4unity CODE_EXEC_RETURN "return 1+1"   # Get the result back
clibridge4unity CODE_EXEC @script.cs            # Run from file (no size limit)
```
CODE_EXEC has its own Roslyn compiler — no COMPILE needed, works even when Unity's main thread is busy.

### Analyze Code (offline — no Unity needed)
```bash
clibridge4unity CODE_ANALYZE PlayerController                 # Class overview + connection graph
clibridge4unity CODE_ANALYZE PlayerController.TakeDamage      # Member details + callers
clibridge4unity CODE_ANALYZE method:TakeDamage                # Every method named TakeDamage across codebase
clibridge4unity CODE_ANALYZE inherits:MonoBehaviour           # Derived types
clibridge4unity CODE_ANALYZE attribute:SerializeField         # Attribute usage sites
```

### Inspect Anything (one command, every target)
```bash
clibridge4unity INSPECTOR                                     # Whole active scene hierarchy (brief)
clibridge4unity INSPECTOR Player                              # One scene GameObject with all serialized fields
clibridge4unity INSPECTOR Player --children                   # Scene GameObject + subtree
clibridge4unity INSPECTOR Player --children --brief           # Subtree, components only (no fields)
clibridge4unity INSPECTOR Player --filter Button              # Subtree filtered by GO or component name
clibridge4unity INSPECTOR Assets/Prefabs/Enemy.prefab         # Prefab asset
clibridge4unity INSPECTOR Assets/Prefabs/Enemy.prefab --children --brief --filter Button
clibridge4unity INSPECTOR Assets/Data/GameConfig.asset        # ScriptableObject
clibridge4unity INSPECTOR Assets/Materials/Metal.mat          # Material properties
```
INSPECTOR uses `SerializedObject` — shows every field the Unity Inspector would show. `--filter X` matches by GameObject name OR component name (substring). `--brief` skips field dumps for big trees. Trees over 300 nodes truncate with a hint.

### Find by Name
```bash
clibridge4unity FIND Player                                   # Scene GameObject (substring match)
clibridge4unity FIND scene:Player                             # Explicit scene scope
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button      # Inside a prefab asset
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Panel,Button  # Multiple names (OR)
```

### Discover Project Assets
```bash
clibridge4unity ASSET_DISCOVER                   # Summary of all asset types
clibridge4unity ASSET_DISCOVER ui                # Sprites, fonts, UI prefabs
clibridge4unity ASSET_DISCOVER materials         # Materials grouped by shader
clibridge4unity ASSET_DISCOVER shaders:URP       # Shaders matching a filter
clibridge4unity ASSET_DISCOVER models            # FBX/OBJ with sub-assets
clibridge4unity ASSET_DISCOVER variants          # Prefab variant inheritance chains
```

### Manage Assets (preserves GUID references)
```bash
clibridge4unity ASSET_MOVE Assets/Old.prefab Assets/New/          # Move (single or batch)
clibridge4unity ASSET_COPY Assets/A.prefab Assets/B.prefab        # Copy
clibridge4unity ASSET_DELETE Assets/Unused.mat                    # Delete
clibridge4unity ASSET_MKDIR Assets/Art/Textures/UI                # Create folders
clibridge4unity ASSET_LABEL Assets/Enemy.prefab +Boss +Spawnable  # Tag with labels
```

### Scene & Play Mode
```bash
clibridge4unity SCENE                            # Full hierarchy
clibridge4unity CREATE MyObject                  # Create GameObject
clibridge4unity COMPONENT_SET Player Transform position "(1,2,3)"
clibridge4unity PLAY                             # Enter play mode
clibridge4unity SCREENSHOT scene                 # Capture editor windows
clibridge4unity SCREENSHOT Assets/UI/Menu.prefab # Render prefab to PNG
```

### Run Tests (streaming results)
```bash
clibridge4unity TEST                                    # All EditMode tests
clibridge4unity TEST playmode                           # All PlayMode tests
clibridge4unity TEST all                                # EditMode + PlayMode
clibridge4unity TEST PlayerControllerTests              # One group/class
clibridge4unity TEST PlayerTests,CameraTests            # Multiple groups (OR)
clibridge4unity TEST --category Physics,AI              # Multiple [Category(...)] tags
clibridge4unity TEST --tests Foo.TestA,Foo.TestB        # Exact test names
clibridge4unity TEST list                               # List all available tests
clibridge4unity TEST list MyClass                       # List tests matching filter
```
All filter arrays are OR'd — a test runs if it matches any group **or** category **or** exact name. Combine with `playmode` / `all` to change the mode.

### Diagnostics (no main thread needed)
```bash
clibridge4unity DIAG                # Thread state, HWND, sync context
clibridge4unity STATUS              # Compile state, UI Toolkit errors, play mode, version
clibridge4unity LOG errors          # Unity console errors
clibridge4unity LOG ui errors       # Current USS/UXML/TSS import errors
```
Commands that reference `.uss`, `.uxml`, or `.tss` assets also append matching UI Toolkit import errors to their normal response.

### AI Setup
```bash
clibridge4unity SETUP           # Installs UPM package + generates CLAUDE.md and AGENTS.md
clibridge4unity SETUP chatgpt   # Refreshes AGENTS.md only
```
The generated docs tell AI assistants which commands are available, when to use COMPILE vs CODE_EXEC, and how to handle busy/timeout errors. Run `SETUP` again after updates to regenerate.

## All Commands

| Category | Commands |
|----------|----------|
| **Core** | `PING` `PROBE` `DIAG` `STATUS` `HELP` `COMPILE` `REFRESH` `LOG` `STACK_MINIMIZE` `MENU` `PROFILE` |
| **Code** | `CODE_EXEC` `CODE_EXEC_RETURN` `TEST` |
| **Scene** | `CREATE` `FIND` `DELETE` `SAVE` `LOAD` `PLAY` `STOP` `PAUSE` `STEP` `PLAYMODE` `SCENEVIEW` `GAMEVIEW` `WINDOWS` |
| **Prefab** | `PREFAB_CREATE` `PREFAB_INSTANTIATE` `PREFAB_SAVE` |
| **Component** | `INSPECTOR` `COMPONENT_SET` `COMPONENT_ADD` `COMPONENT_REMOVE` |
| **Asset** | `ASSET_SEARCH` `ASSET_DISCOVER` `ASSET_MOVE` `ASSET_COPY` `ASSET_DELETE` `ASSET_MKDIR` `ASSET_LABEL` `ASSET_RESERIALIZE` |
| **UI** | `SCREENSHOT` |
| **CLI-side** | `CODE_ANALYZE` `SETUP` `UPDATE` `SCREENSHOT` `WAKEUP` `DISMISS` |

Run `clibridge4unity HELP` for full usage details.

## How It Works

The CLI communicates with Unity Editor through Named Pipes. The Unity-side package registers commands via `[BridgeCommand]` attributes — no manual wiring needed.

```
Terminal / AI                    Unity Editor
┌──────────────┐    Named Pipe    ┌──────────────┐
│ clibridge4   │ ──────────────── │ BridgeServer │
│ unity.exe    │  COMMAND|data\n  │ (EditorOnly) │
└──────────────┘                  └──────┬───────┘
                                         │
                                  CommandRegistry
                                   auto-discovers
                                  [BridgeCommand]
                                   attributes in
                                  clibridge4unity.*
                                    assemblies
```

Key design decisions:
- **Speed first** — CLI detects Unity state via process list and window titles before even connecting
- **Background-safe** — uses `SynchronizationContext` + `PostMessage(WM_NULL)` to wake Unity when minimized
- **Compile-error aware** — commands fail fast with error details instead of hanging
- **Dual code execution** — CODE_EXEC uses bundled Roslyn (background thread), COMPILE uses Unity's pipeline (main thread)

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

### Releasing

Releases are automated:

```bash
python .claude/scripts/deploy.py 1.0.15   # Build, tag, push, release
```

The CLI checks GitHub Releases in the background and notifies users when a new version is available.

## Requirements

- Unity 2021.3+
- Windows (macOS/Linux planned)
