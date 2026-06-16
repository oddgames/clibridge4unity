---
name: clibridge4unity-domain-reload
description: Use for any work that crosses an assembly reload — entering/exiting PlayMode, `COMPILE`/`REFRESH`, package imports, or restoring state after a recompile. Auto-trigger on `[InitializeOnLoad]`, `[InitializeOnLoadMethod]`, `[RuntimeInitializeOnLoadMethod]`, `AssemblyReloadEvents.beforeAssemblyReload`, `AssemblyReloadEvents.afterAssemblyReload`, `SessionState.`, `EditorPrefs.`, `[DidReloadScripts]`, "state was lost", "static field is null after reload", "callbacks died", "task was cancelled", `static T _instance`, anything that assumes static-field lifetime exceeds a recompile. Domain reload happens every script change, every PlayMode enter/exit, every package import — anything you keep in static memory disappears. The pattern is: save before the reload, restore after.
---

# Unity Domain Reload

General domain-reload mechanics (static state wiped, `SessionState` vs `EditorPrefs`, `[InitializeOnLoad]`/`AssemblyReloadEvents`/`[DidReloadScripts]` lifecycle, dead pipes/tasks/handles, save-before/restore-after) are standard Unity knowledge — apply them. Below are only the clibridge4unity-specific contracts.

## Project-specific patterns

- **Deferred init.** `[InitializeOnLoad]` static ctor adds `EditorApplication.update += InitOnFirstTick`; `InitOnFirstTick` unsubscribes itself and does the real init one tick later. Never do real work in the static ctor (CLAUDE.md Editor Performance Mandate: heavy work is command-triggered or deferred, never on the InitializeOnLoad path).
- **Connections don't survive — clients reconnect.** Tear IPC/socket/handle-backed connections down in `beforeAssemblyReload`, recreate in `[InitializeOnLoad]`. `REFRESH` returns immediately and the CLI polls for the pipe to come back; the editor side never pretends the connection survived.
- **PlayMode-spanning work uses the disk, not callbacks/pipes.** PlayMode test runs persist results to `Temp/clibridge4unity_test.log` + a `.status` file; `[InitializeOnLoad]` checks SessionState on every domain init and re-registers `TestRunnerApi` callbacks if a run is still in progress. The CLI tails the log file, not the pipe.
- **HWND across reloads.** Persist native handles via `SessionState.SetString(key, hwnd.ToInt64().ToString())`, restore with `new IntPtr(long.Parse(...))`, validate with `IsWindow` before use.
- **SessionState key convention.** All keys live in one `SessionKeys` class, each prefixed `Bridge_`: `LastCompileRequest`, `LastCompileTime`, `TestRunId`, `TestLogPath`, `TestStatusPath`, `UnityHwnd`. Use `SessionState` for transient session state, `EditorPrefs` for genuine user prefs.

To verify: trigger a reload mid-feature (`Ctrl+R` / `clibridge4unity REFRESH`) and confirm post-reload state matches expectations.

## Related
- `clibridge4unity-async-mainthread` — async/cancellation across reload
- `clibridge4unity-serialization` — asset files vs transient state
- `clibridge4unity-tests` — PlayMode result capture via `Temp/clibridge4unity_test.log`
