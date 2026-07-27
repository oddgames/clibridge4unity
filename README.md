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

This installs the UPM package, unpacks per-task AI skills into `.claude/skills/`, and generates assistant docs: `CLAUDE.md` for Claude Code and `AGENTS.md` for ChatGPT/Codex.

Stay current with `clibridge4unity UPDATE` (self-updates the CLI, the UPM package tag, and the skills). `clibridge4unity VSCODE` installs the bundled VSCode/Cursor status-bar extension.

## What It Does

### Execute C# in Unity (built-in Roslyn compiler)
```bash
clibridge4unity CODE_EXEC "Debug.Log(42)"       # Fire-and-forget (alias: EXEC)
clibridge4unity CODE_EXEC_RETURN "return 1+1"   # Get the result back (alias: EVAL)
clibridge4unity CODE_EXEC @script.cs            # Run from file (no size limit)
clibridge4unity CODE_EXEC_RETURN "Selection.activeGameObject" --inspect 3   # Dump object tree
clibridge4unity CODE_EXEC_RETURN @script.cs --trace --vars pos,vel          # Line-by-line trace
```
CODE_EXEC has its own Roslyn compiler — no COMPILE needed, works even when Unity's main thread is busy.

### Understand the Project (offline — no Unity needed)
```bash
clibridge4unity MAP double jump               # Task-oriented dossier: scripts (with attach sites),
                                              #   scenes, prefabs, config assets, UnityEvent wiring
clibridge4unity ANALYZE PlayerController      # Definition, usages, derived types, GetComponent sites,
                                              #   members + which scenes/prefabs have it attached
clibridge4unity ANALYZE PlayerController.Move # Zoom into one member
clibridge4unity ANALYZE method:TakeDamage     # Kind-prefixed search (method:/field:/property:/inherits:/attribute:)
clibridge4unity ANALYZE usedby:Assets/Prefabs/Player.prefab   # Reverse lookup: everything referencing an asset
clibridge4unity LINT                          # Sub-second offline syntax + UXML/USS check
clibridge4unity LINT unity                    # Unity-faithful per-asmdef compile (~5-60s, no domain reload)
```
Served by a persistent Roslyn daemon that indexes both the C# source and the serialized asset graph (GUID wiring in scenes/prefabs), so it sees inspector-assigned references grep can't. `MAP` is the "where do I start?" command; `ANALYZE` (aliases: `CODE_ANALYZE`, `CODE_SEARCH`) is the deep view.

### Inspect Anything (one command, every target)
```bash
clibridge4unity INSPECTOR                                     # Whole active scene hierarchy (brief)
clibridge4unity INSPECTOR Player                              # One scene GameObject with all serialized fields
clibridge4unity INSPECTOR Player --children                   # Scene GameObject + subtree
clibridge4unity INSPECTOR Player --children --brief           # Subtree, components only (no fields)
clibridge4unity INSPECTOR Player --filter Button              # Subtree filtered by GO or component name
clibridge4unity INSPECTOR Assets/Prefabs/Enemy.prefab         # Prefab asset
clibridge4unity INSPECTOR Assets/Data/GameConfig.asset        # ScriptableObject
clibridge4unity INSPECTOR Assets/Materials/Metal.mat          # Material properties
```
INSPECTOR uses `SerializedObject` — shows every field the Unity Inspector would show. `--filter X` matches by GameObject name OR component name (substring). `--brief` skips field dumps for big trees.

### Find by Name
```bash
clibridge4unity FIND Player                                   # Scene GameObject (substring match)
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Button      # Inside a prefab asset
clibridge4unity FIND prefab:Assets/UI/Menu.prefab/Panel,Button  # Multiple names (OR)
```

### Discover & Manage Assets (GUID-preserving)
```bash
clibridge4unity ASSET_DISCOVER                   # Summary of all asset types
clibridge4unity ASSET_DISCOVER ui                # Sprites, fonts, UI prefabs (alias: UI_DISCOVER)
clibridge4unity ASSET_DISCOVER materials         # Materials grouped by shader
clibridge4unity ASSET_DISCOVER shaders:URP       # Shaders matching a filter
clibridge4unity ASSET_SEARCH "t:prefab Boss"     # Unity Search syntax, did-you-mean on miss
clibridge4unity ASSET_MOVE Assets/Old.prefab Assets/New/          # Move/rename (refs stay intact)
clibridge4unity ASSET_COPY scene/Player Assets/Player.prefab      # Extract to its own asset
clibridge4unity ASSET_DELETE Assets/Unused.mat                    # Delete (batch-capable)
clibridge4unity ASSET_LABEL Assets/Enemy.prefab +Boss +Spawnable  # Labels
clibridge4unity REIMPORT Assets/Broken.prefab                     # Re-validate corrupted YAML
```

### Scene & Play Mode
```bash
clibridge4unity CREATE MyObject                  # Create GameObject
clibridge4unity COMPONENT_SET Player Rigidbody mass 5
clibridge4unity COMPONENT_ADD Canvas/Panel BoxCollider
clibridge4unity PLAY                             # Enter play mode (PLAY/STOP/PAUSE/STEP/PLAYMODE)
clibridge4unity GAMEVIEW 2556x1179               # Set Game view resolution (e.g. device aspects)
clibridge4unity MENU Window/General/Console      # Run any Unity menu item
```

### Screenshots & UI Renders
```bash
clibridge4unity SCREENSHOT                       # Whole editor window (also: scene/inspector/console/...)
clibridge4unity SCREENSHOT gameview              # What the player sees (incl. runtime UI)
clibridge4unity SCREENSHOT camera 1920x1080      # Raw camera render, no overlays
clibridge4unity SCREENSHOT Assets/UI/Menu.prefab # Render prefab to PNG (3D prefabs: 8-angle turntable)
clibridge4unity SCREENSHOT Assets/UI/Menu.uxml   # UXML: TWO views — as-authored + all-hidden-revealed,
                                                 #   plus a list of every element it unhid (with reason)
clibridge4unity SCREENSHOT Assets/UI/Menu.uxml --el "#card-grid"       # Crop to one element
clibridge4unity SCREENSHOT Assets/UI/Menu.uxml --show "#dialog" --hide "#overlay"  # Exact visibility state
clibridge4unity SCREENSHOT gameview --output ./shots/iphone.png       # Copy to a chosen path
```
Editor-window captures are CLI-side (work even mid-compile). UXML renders force-reimport the UXML + its USS/TSS deps first, so on-disk edits show immediately.

### Run Tests (streaming results)
```bash
clibridge4unity TEST                                    # All EditMode tests
clibridge4unity TEST playmode                           # PlayMode (or: all)
clibridge4unity TEST PlayerTests,CameraTests            # Groups (OR)
clibridge4unity TEST --category Physics,AI              # [Category(...)] tags
clibridge4unity TEST --tests Foo.TestA,Foo.TestB        # Exact test names
clibridge4unity TEST list [filter]                      # List available tests
```
All filter arrays are OR'd — a test runs if it matches any group **or** category **or** exact name.

### Build the Player
```bash
clibridge4unity BUILD                   # Active target, streams progress + errors
clibridge4unity BUILD --run --dev       # Development build, launch after success
clibridge4unity BUILD --output <path>   # Custom output location
```

### Diagnostics & Debugging
```bash
clibridge4unity STATUS              # Compile state, UI Toolkit errors, play mode, version
clibridge4unity DIAG                # Always answers — even when the main thread is blocked
clibridge4unity LOG errors          # Unity console errors (over the pipe)
clibridge4unity EDITORLOG errors    # On-disk Editor.log — works with NO pipe (clones, crashed Unity)
clibridge4unity EDITORLOG grep "NullReference"   # Regex-filter the log tail
clibridge4unity RELEASENOTES        # Unity release notes: your project's version → latest
clibridge4unity RELEASENOTES -g "Terrain.*crash" # Is this bug a known Unity issue/fix?
clibridge4unity LAST -grep FAIL     # Re-read a cached response without re-running
clibridge4unity CANCEL --all        # Abort in-flight long-running commands
```
`LOG` reads Unity's in-memory console; `EDITORLOG` reads the log file on disk — reach for it when `LOG` is empty or the bridge isn't running in that instance. `RELEASENOTES` (aliases `UNITYNOTES`, `RELNOTES`) pulls Unity's official version-compare notes — grep them when a bug smells like Unity itself.

### Multi-Window Safety
When several terminals/AI agents share one Unity editor, the CLI prints `[conflict] WARNING:` before commands that would stomp another window (COMPILE breaks their pipes, PLAY/STOP changes shared play state, BUILD blocks everyone, same-asset writes clobber). Advisory only — automation never wedges.

### AI Setup
```bash
clibridge4unity SETUP           # UPM package + per-task skills + CLAUDE.md and AGENTS.md
clibridge4unity SETUP chatgpt   # Refreshes AGENTS.md only
clibridge4unity UPDATE          # Self-update CLI + package + skills
```
The generated docs and skills tell AI assistants which commands are available, when to use `LINT` vs `COMPILE` vs `CODE_EXEC`, and how to handle busy/timeout errors.

> **Workflow tip**: After editing C#, do nothing — Unity auto-compiles when it regains focus, and 99% of the time you've already compiled before asking for a test. `LINT`/`COMPILE` are troubleshooting tools for when something isn't working as expected — and never for `.shader`/`.uxml` edits (those are asset imports, not script compiles).

## All Commands

| Category | Commands |
|----------|----------|
| **Core** | `PING` `PROBE` `DIAG` `STATUS` `BRIDGEINFO` `HELP` `COMPILE` `REFRESH` `LOG` `MENU` `PROFILE` `BUILD` `CANCEL` |
| **Code** | `CODE_EXEC` `CODE_EXEC_RETURN` `TEST` `DEBUG` |
| **Scene** | `CREATE` `FIND` `DELETE` `SAVE` `LOAD` `PLAY` `STOP` `PAUSE` `STEP` `PLAYMODE` `SCENEVIEW` `GAMEVIEW` `WINDOWS` |
| **Prefab** | `PREFAB_CREATE` `PREFAB_INSTANTIATE` `PREFAB_SAVE` |
| **Component** | `INSPECTOR` `COMPONENT_SET` `COMPONENT_ADD` `COMPONENT_REMOVE` |
| **Asset** | `ASSET_SEARCH` `ASSET_DISCOVER` `ASSET_MOVE` `ASSET_COPY` `ASSET_DELETE` `ASSET_MKDIR` `ASSET_LABEL` `ASSET_RESERIALIZE` (alias `REIMPORT`) |
| **UI** | `UI_DISCOVER` `SCREENSHOT` |
| **Offline intelligence (no Unity needed)** | `MAP` `ANALYZE` (aliases `CODE_ANALYZE`/`CODE_SEARCH`) `LINT` / `LINT unity` |
| **CLI-side (no pipe needed)** | `SETUP` `UPDATE` `VSCODE` `OPEN` `KILL` `WAKEUP` `DISMISS` `SCREENSHOT` `EDITORLOG` `RELEASENOTES` `LAST` `SERVE` |

Run `clibridge4unity -h` for full usage details, or `HELP` for the live list from your Unity instance.

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
- **Offline intelligence** — MAP/ANALYZE/LINT run against a persistent Roslyn + asset-graph daemon; Unity need not be open

## Development

### Repository Structure

```
├── clibridge4unity/           # CLI tool (.NET 8, single-file publish)
│   ├── skills/                # Per-task AI skill .md files (embedded into the exe)
│   └── vscode/                # Deploy-time staging for the embedded VSCode extension .vsix
├── Package/                   # Unity Editor package (UPM)
│   ├── Editor/Core/           # Pipe server, command registry
│   ├── Editor/Commands/       # Command implementations (8 asmdefs)
│   └── Tools/                 # Pre-built Windows CLI binary
├── vscode-extension/          # VSCode/Cursor status-bar extension source
├── tests/                     # pytest integration tests
└── UnityTestProject/          # Local Unity fixture used by tests (not release content)
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
pytest
```

Tests require Unity Editor running with `UnityTestProject` open, the bridge package compiled, and the CLI installed at `~/.clibridge4unity/clibridge4unity.exe`.

### Releasing

Releases are automated:

```bash
python .claude/scripts/deploy.py 1.1.66   # Build, tag, push, release
```

The pipeline packages the VSCode extension, builds the CLI with embedded skills + extension, commits, tags, creates the GitHub release, and uploads assets. The CLI checks GitHub Releases in the background and notifies users when a new version is available.

## Requirements

- Unity 2021.3+
- Windows (macOS/Linux planned)
