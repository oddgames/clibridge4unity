# Changelog

## v1.1.46 — 2026-05-21

## v1.1.46

### Fixed
- **STATUS no longer times out when Unity's main thread is busy.** Previously, asking for STATUS during an asset import / shader compile / domain reload returned a `TimeoutException` after a long wait. It now degrades gracefully: with a 5s soft deadline on the main-thread dispatch, on timeout it returns `Response.SuccessWithData(...)` with `mainThreadBusy: true`, the busy report (heartbeat staleness, queued commands, open dialogs) embedded as `busyReport`, plus cached values (bridge version, last compile times, compile time stats) from background-safe sources. Same response *type* as the happy path — callers can rely on STATUS to always return something useful.
- **Missing `.meta` file for `BuildCommand.cs`** that caused Unity to log "has no meta file, but it's in an immutable folder. The asset will be ignored" on package install. Added the meta with a fresh GUID.

### Internal
- Refactored `CoreCommands.GetStatus()` from sync into `async Task<string>`; extracted main-thread-only work into `BuildMainThreadStatus()` helper, gated `playModeDuration` on `isPlaying` to avoid stale SessionState leaking after STOP. Moved the profiler marker inside the sync helper so Begin/End don't span an `await`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.45 — 2026-05-18

## v1.1.45

### New
- `SCREENSHOT <uxml> --el <selector>` now force-unhides the target element and every ancestor before rendering: inline `display: Flex`, `visibility: Visible`, `opacity: 1` overrides USS and UXML `style=` attributes. Lets you screenshot elements that production UI keeps hidden until a runtime trigger fires. On-disk UXML is never touched — only the throwaway render clone is mutated.
- Render success message reports `force-unhid N ancestor(s)` so it's obvious the screenshot does not represent the production-runtime state.

### Fixed
- `LINT unity` no longer gets stuck for minutes on big projects. `compilation.Emit()`, `GetDiagnostics()`, and the source-generator driver now all receive a `CancellationToken`, so the wall-clock budget is actually enforceable instead of being silently ignored.
- Added a no-progress watchdog (10s default): if no asmdef completes in 10s, the level is cancelled and partial results are returned. Covers stuck source generators and oversized single asmdefs.
- Lowered the wall-clock budget from 60s to 30s now that cancellation actually works.
- Error message now distinguishes "no asmdef completed in Xms (stuck generator/oversized asmdef)" from "exceeded wall-clock budget" so it's clear which limit fired.
- Zero-size element error in UXML screenshot updated to point at the cases force-unhide can't fix (width:0, position:absolute offscreen, runtime-only data) instead of generic "make it visible".

### Internal
- `LintUnity.Run()` now skips per-asmdef compilation for any user asmdef whose `Library/ScriptAssemblies/<name>.dll` is newer than its `.asmdef` config and every source file. The prebuilt DLL is reused as the downstream MetadataReference. No-change re-runs go from ~30s to sub-second; edits only recompile the touched asmdef + its DAG dependents.
- New `ComputeFreshAsmdefs()` helper centralizes the incremental-skip mtime check; `Format()` now reports `N incremental-skipped` in the mode line so it's visible.
- `LintSourceGenerators.RunGenerators()` accepts and forwards a `CancellationToken` into `CSharpGeneratorDriver.RunGeneratorsAndUpdateCompilation`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.44 — 2026-05-13

## v1.1.44

### Fixed
- Unity-process detection now uses the Windows **Restart Manager** to ask the OS which PIDs hold `<project>/Temp/UnityLockfile` open. Authoritative — works even when multiple projects share the same folder name (e.g. several `test/` projects), or when window titles / command-line paths would otherwise match the wrong instance. The previous window-title and `-projectPath` substring passes are kept as fallbacks for projects that haven't fully booted yet (no lockfile yet).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.43 — 2026-05-13

## v1.1.43

### Fixed
- Unity-process detection now does a normalized-path match pass against `-projectPath` from every running Unity command line. Catches edge cases where the substring `Contains` check failed (trailing slash, mixed slashes, path casing differences, quoted paths) — fixes spurious `state: DifferentProject` when Unity *is* running for the target project.
- `KILL` now prints the expected normalized path + a list of running Unity workspaces (pid + normalized path) when state is `DifferentProject`, instead of the bare "not running" message — makes it obvious why detection didn't match.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.42 — 2026-05-13

## v1.1.42

### New
- `BUILD [--run] [--dev] [--output <path>]` command — builds the Unity Player using the active build target. Streams progress + errors/warnings live over the pipe. `--run` launches Standalone (Win/Mac/Linux) after a successful build; Android/WebGL print install/serve hints. Default output: `Builds/<Target>/<ProductName>`. Other commands auto-block during the build via existing `BuildBypassCommands` allowlist.
- `BridgeCommand.Aliases` — attribute now accepts alternate names that resolve to the same command. HELP renders them inline as `(aliases: X, Y)`. Bypass checks resolve aliases to canonical names so Player-Build / compile-error allowlists behave correctly.
- Command aliases (industry-standard naming, old names still work):
  - `EXEC` → `CODE_EXEC`
  - `EVAL` → `CODE_EXEC_RETURN`
  - `REIMPORT` → `ASSET_RESERIALIZE`

### Removed
- `STACK_MINIMIZE` command — was an internal-only utility never exercised externally. The `StackTraceMinimizer` class is still used internally by `LogCommands` for path shortening; only the public bridge command was dropped.

### Internal
- `SessionKeys.LastBuildPath` / `LastBuildTarget` persist the last build path across domain reloads.
- CLI `hintWorthy` list updated to surface the `LAST` replay hint for `BUILD`, `EXEC`, `EVAL`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.41 — 2026-05-12

## v1.1.41

### New
- `CODE_EXEC --bg` and `CODE_EXEC_RETURN --bg` — run user code on the threadpool instead of Unity's main thread, with a 25s hard timeout. Use for blocking work (CDP / Chrome DevTools, HTTP requests, large file IO, JSON parsing) so the Editor stays responsive while the call is in flight. On timeout the task is abandoned (still consumes the threadpool slot until it returns) and the bridge returns a clear error — the bridge command slot is no longer held hostage by a stuck blocking call.
- Caveat: Unity API calls from `--bg` code throw "main thread only" exceptions, as expected. Keep Unity API touches on the default (main-thread) path and use `--bg` for pure C# blocking work.

### Why
- Headless-Chrome / CDP scripts run synchronously inside `CODE_EXEC_RETURN` wedge Unity's main thread for seconds-to-minutes, blocking every subsequent bridge command (STATUS, PING, DIAG falling back to busy reports) until the user kills Unity. No safe way to abort code already running on Unity's main thread, so the fix is to keep that work off the main thread entirely.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.40 — 2026-05-12

## v1.1.40

### New
- Custom `[BridgeCommand]` methods now picked up from Unity's default editor catch-all assembly (`Assembly-CSharp-Editor` / `Assembly-CSharp-Editor-firstpass`). Previously the registry only scanned assemblies whose name started with `clibridge4unity` or that explicitly referenced `clibridge4unity.Core`, which forced users to create an asmdef. Now: drop a `.cs` under `Assets/Editor/` with `using clibridge4unity;` and a `[BridgeCommand(...)]` static method — it appears in HELP after the next compile, no asmdef plumbing required.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.39 — 2026-05-11

## v1.1.39

### Fixed
- CLI no longer pops the Win32 "Application Error 0xe0434352" dialog when a background thread (daemon, FileSystemWatcher callback, RoslynDaemon, update-check task) throws an unhandled exception. Now installs `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` handlers at startup, calls `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX)` to suppress Windows Error Reporting, and Main's catch block returns exit code 99 instead of rethrowing. Crash details land in `%TEMP%/clibridge4unity.cli.trace`.
- Bridge no longer duplicates main-thread exceptions to Unity's console. `CommandRegistry.ProcessAllPendingWork` previously called `Debug.LogError` AND surfaced the exception via `TaskCompletionSource.TrySetException` — caller already gets the exception in the command response, so the Debug.LogError was pure noise (it also got captured by the per-command log capture, doubling reports). Diagnostic file log retained.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.38 — 2026-05-11

## v1.1.38

### Internal — STATUS perf overhaul
- **Removed full-tree mtime scan from `ScanModifiedScripts`** — previously walked every `.cs/.asmdef/.asmref` under `Assets/`, `Packages/`, and every local `file:` package root on every cache miss (was 870ms + 8.8MB GC alloc per call on a big project). Daemon's FileSystemWatcher is authoritative; no need to re-walk.
- **Removed `GetLocalFilePackageRoots` helper** — only existed to feed the deleted mtime scan.
- **STATUS skips `LogCommands.GetCompileErrorsFromConsole()` when console has 0 errors** — was always-on, even when nothing to find.
- **STATUS computes `ScriptsModifiedSinceCompileCached()` once and reuses 3×** — was called separately for each of `scriptsModified`, `compileRecommended`, and `compileRecommendation`.
- **`ScriptsModifiedSinceCompileCached` TTL bumped 1s → 10s** — daemon catches changes in real time, so STATUS doesn't need 1-second freshness.
- **Dropped per-command file-I/O log spam in `CommandRegistry`** — `BridgeDiagnostics.Log` was called on every main-thread work begin/end/queued (3× `File.AppendAllText` per command). Startup/error logs kept; per-work-item noise removed.

Net effect: STATUS now does roughly zero work on the steady-state path (daemon log present, console clean, cache fresh).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.37 — 2026-05-11

## v1.1.37

### Internal
- More granular Profiler markers inside STATUS to find why a single STATUS tick can spike to ~880ms with 8.8MB GC alloc on big projects. New sub-markers nested under `Bridge.Core.Status`:
  - `Bridge.Core.Status.OpenEditorWindows` — only fires on cache miss (`Resources.FindObjectsOfTypeAll<EditorWindow>()`)
  - `Bridge.Core.Status.ScriptsModifiedSinceCompile` — only on cache miss (file mtime walk)
  - `Bridge.Core.Status.GetCompileTimeStats` — `BridgeServer.GetCompileTimeStats()`
  - `Bridge.Core.Status.GetCompileErrors` — `CommandRegistry.GetCompileErrors()` callback + Regex match
- New markers in `LogCommands` for the Unity-internal `LogEntries` reflection helpers that STATUS funnels through:
  - `Bridge.Core.GetConsoleCounts`
  - `Bridge.Core.GetCompileErrorsFromConsole` — full console iteration
  - `Bridge.Core.GetUiToolkitDiagnosticsFromConsole` — full console iteration + per-asset import-error fetch
- After this release, run STATUS once with the Unity Profiler open and the slow sub-call inside STATUS will be self-evident in the hierarchy.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.36 — 2026-05-11

## v1.1.36

### Internal
- Profiler markers added across all bridge commands and heavy helper methods. Open Unity Profiler → Hierarchy and search "Bridge." to see exactly where time goes during a slow command.
  - Central wrap in `CommandRegistry.InvokeCommand` emits `Bridge.{COMMAND_NAME}` for every dispatched command. No per-command opt-in needed.
  - Per-method `ProfilerMarker.Auto()` scopes added to the heaviest paths:
    - `RenderCommand`: Render, RenderUxml, RenderUxmlSettlePump, RenderGrid, RenderPrefab, RenderUIPrefab, Render3DPrefab, RenderGameObject
    - `UICommands` (ASSET_DISCOVER): DiscoverSummary, DiscoverUI, DiscoverShaders, DiscoverMaterials, DiscoverSprites, DiscoverUIPrefabs, DiscoverScenes, DiscoverFonts, DiscoverModels, DiscoverVariants
    - `AssetSearch`: Search, FindPrefabsWithComponent, FindMaterialsWithShader, FindAssetsWithLabel, FindScriptsInheriting, FindAssetsOfType, GetDependencies, FindReferences
    - `AssetManagement`: Move, Copy, Delete, Mkdir, Label, Reserialize
    - `ComponentCommands`: Inspector, FindType (full AppDomain type sweep), Set, Add, Remove
    - `CodeExecutor`: Execute, ExecuteReturn, Compile, CompileRoslyn, CompileMcs, CollectMcsReferences
    - `PrefabCommands`: Create, Save, Instantiate, Apply, Unpack, List
    - `SceneCommands`: Create, Find, Delete, Save, Load, SceneView
    - `CoreCommands`: Status, Compile, Refresh, Menu, Profile, Diag
    - `LogCommands`: GetLogs, GetCompileErrorsSummary, GetUiToolkitDiagnosticsForCommand, EndCommandCapture
- Markers use struct-based `Unity.Profiling.ProfilerMarker` (zero allocation) and only register the scope when Unity's deep profiler / `Bridge.*` filter is active, so there is no measurable overhead in normal use.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.35 — 2026-05-10

## v1.1.35

### Fixed
- `SCREENSHOT Assets/Foo.uxml` no longer flashes a popup window onscreen and no longer wakes a backgrounded Unity to the foreground. Setting `EditorWindow.position` to negative coords doesn't work because Unity's `ContainerWindow` clamps the position to the nearest monitor — so the popup ended up onscreen, and `ShowPopup` woke Unity's message pump to paint it. Fix: after `ShowPopup`, walk reflection chain `EditorWindow → m_Parent → window → windowPtr` to grab the popup's HWND, then call Win32 `SetWindowPos` to `(-30000, -30000)` with `SWP_NOACTIVATE`. Win32 doesn't clamp arbitrary HWND moves, and `SWP_NOACTIVATE` keeps Unity in the background.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.34 — 2026-05-10

## v1.1.34

### New
- `SAVE Assets/Path/Foo.unity` — explicit path argument writes the active scene directly via `EditorSceneManager.SaveScene`. Bypasses the native "Save Scene As..." file dialog that `SaveOpenScenes` opens for Untitled scenes (and that wedges the bridge because CLI can't dismiss native file dialogs).
- `GAMEVIEW WxH` now sets the resolution via Unity's internal `GameViewSizes` API (adds/selects a `clibridge_WxH` entry in the size dropdown). Docked Game tab stays docked instead of being torn off into a floating window. Falls back to the old window-resize path if the reflection chain breaks (with the failure reason in the response).
- `SCREENSHOT Assets/Foo.uxml` — viewport size now inferred from the UXML root child's declared `style.width`/`style.height` (pixel units). Layouts authored for a specific resolution render at intended size; falls back to 1920x1080.
- `SCREENSHOT Assets/Foo.uxml --el <selector>` — auto-supersamples small elements: after layout settles, if the element's max dimension is under 600px the root tree is rescaled (up to 8x) and re-rendered before crop. Avoids tiny blurry thumbnails like 168x9 for `.checkbox-group`.

### Fixed
- `SAVE` on an Untitled scene no longer opens a native Save dialog that blocks Unity's main thread. Returns a clear error pointing to the `SAVE <path>` form instead.
- `SCREENSHOT gameview` no longer steals focus from a docked tab. Removed the `gameView.Focus()` call — `Repaint` + `RepaintImmediately` is sufficient to refresh the backing buffer.
- `SCREENSHOT Assets/Foo.uxml` — render window position now set BEFORE `ShowPopup` so the offscreen popup never flashes at the default location for one frame.
- `SCREENSHOT Assets/Foo.uxml` — multi-pass repaint pump (RepaintImmediately + MarkDirtyRepaint, 50ms gap, 6 passes) before final GrabPixels so dynamic font atlas / TMP measure-then-relayout cycles settle and text positions stabilize.

### Internal
- `CheckStuckDomainReload` (DIAG): now gathers progress evidence before recommending KILL+OPEN. Samples Editor.log mtime, Unity process CPU% over a 1.2s window, and Win32 progress-bar movement (PBM_GETPOS sent cross-process to msctls_progress32 children of any open dialog). Verdict is "still working" if any signal is active, "appears frozen" only when all three are quiet — then suggests DIAG → WAKEUP → KILL in order.
- `CheckStuckDomainReload`: adaptive threshold for `compiling`/`reloading` states — uses `max(60, max(180, compileAvg * 3))` so a normal 90-180s compile on a big project no longer triggers stuck warnings. `importing` keeps the lower 60s threshold.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.33 — 2026-05-10

## v1.1.33

### New
- `SCREENSHOT Assets/Foo.uxml` now infers viewport size from the UXML root element's declared `style.width`/`style.height` (pixel units). Layouts authored for a specific resolution (e.g. 1920×1080 panel) render at their intended size instead of being forced into an 800×450 box that collapsed flex children. Falls back to 1920×1080 when no pixel size is declared.
- `SCREENSHOT Assets/Foo.uxml --el <selector>` auto-supersamples small elements: after layout settles, if the element's max dimension is under 600px the root tree is rescaled (up to 8×) and re-rendered before crop. Tiny elements like `.checkbox-group` come out as crisp larger images instead of blurry 168×9 thumbnails.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.32 — 2026-05-09

## v1.1.32

### Fixed
- `SCREENSHOT Assets/Foo.uxml`: text positions sometimes captured mid-layout (before dynamic font atlas / TMP measure-then-relayout cycle settled). Added 6-pass repaint pump (RepaintImmediately + MarkDirtyRepaint, 50ms gap) before final GrabPixels so scheduled callbacks, bindings, and GeometryChangedEvent handlers run and text positions stabilize.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.31 — 2026-05-09

### Files changed
```
.../scripts/__pycache__/ui_analyze.cpython-310.pyc | Bin 39055 -> 0 bytes
 .claude/scripts/deploy.py                          | 141 +++-
 .gitignore                                         |   9 +
 CLAUDE.md                                          |   4 +-
 ConsoleUnityBridge.sln                             |  25 +
 Package/CLAUDE.md                                  |   2 +-
 Package/Editor/Commands/Asset/AssetManagement.cs   |  10 +-
 Package/Editor/Commands/Scene/PlayModeCommands.cs  | 173 ++++-
 Package/Editor/Commands/Scene/SceneCommands.cs     |  37 +-
 Package/Editor/Core/BridgeServer.cs                |  17 +-
 Package/Editor/Core/CommandRegistry.cs             |  69 +-
 Package/Editor/Core/SetupWizard.cs                 |  52 +-
 Package/package.json                               |   2 +-
 SUMMARY.md                                         |   2 +-
 clibridge4unity/CodeAnalysisCore.cs                | 140 +++-
 clibridge4unity/LintAsmdef.cs                      | 271 ++++++++
 clibridge4unity/LintCscRsp.cs                      | 134 ++++
 clibridge4unity/LintSemantic.cs                    | 365 ++++++++--
 clibridge4unity/LintSourceGenerators.cs            | 157 +++++
 clibridge4unity/LintUnity.cs                       | 763 +++++++++++++++++++++
 clibridge4unity/ReportServer.cs                    | 184 +++--
 clibridge4unity/RoslynDaemon.cs                    |  92 ++-
 clibridge4unity/clibridge4unity.cs                 | 402 ++++++++---
 clibridge4unity/clibridge4unity.csproj             |   2 +-
 install.ps1                                        |   2 +-
 pyproject.toml                                     |   4 +
 tests/conftest.py                                  |  50 ++
 tests/test_asset.py                                |  27 +
 tests/test_code.py                                 |  59 ++
 tests/test_component.py                            |  65 ++
 tests/test_core.py                                 | 131 ++++
 tests/test_errors.py                               |  37 +
 tests/test_playmode.py                             |  27 +
 tests/test_prefab.py                               |  37 +
 tests/test_scene.py                                |  71 ++
 tests/test_ui.py                                   |  37 +
 36 files changed, 3252 insertions(+), 348 deletions(-)
```

**Diff:** 37 files changed, 3252 insertions(+), 348 deletions(-)

**Full comparison:** https://github.com/oddgames/clibridge4unity/compare/v1.1.30...v1.1.31

**Install:** `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.30 — 2026-05-09

**Full comparison:** https://github.com/oddgames/clibridge4unity/compare/v1.1.29...v1.1.30

**Install:** `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

