---
name: clibridge4unity-domain-reload
description: Use for any work that crosses an assembly reload — entering/exiting PlayMode, `COMPILE`/`REFRESH`, package imports, or restoring state after a recompile. Auto-trigger on `[InitializeOnLoad]`, `[InitializeOnLoadMethod]`, `[RuntimeInitializeOnLoadMethod]`, `AssemblyReloadEvents.beforeAssemblyReload`, `AssemblyReloadEvents.afterAssemblyReload`, `SessionState.`, `EditorPrefs.`, `[DidReloadScripts]`, "state was lost", "static field is null after reload", "callbacks died", "task was cancelled", `static T _instance`, anything that assumes static-field lifetime exceeds a recompile. Domain reload happens every script change, every PlayMode enter/exit, every package import — anything you keep in static memory disappears. The pattern is: save before the reload, restore after.
---

# Unity Domain Reload

A domain reload tears down the entire .NET AppDomain inside the Unity Editor and recreates it. Every static field, every singleton, every running `Task`, every captured `SynchronizationContext`, every named-pipe connection, every cached `IntPtr` — gone. It happens on script compile, package import, `[Reload Domain]` menu, and **twice per PlayMode session** (entering and exiting). Plan for it from the start: anything you want to keep, persist explicitly; anything you don't, register a cleanup.

## The one rule that prevents most of these

**Static memory does not survive a domain reload.** If you can't restore the state from disk / `SessionState` / `EditorPrefs` in 100ms, you've already lost it. Save before, restore after — both halves of every long-lived bridge, server, manager, or cache.

## Pitfall catalog

### 1. Static fields are null after every reload
The most common Unity bug-by-surprise. `private static MyService _instance` is fresh on every recompile. Anything you cached — a port number, a window handle, a discovered list — is gone.
- **Rule:** for state that must survive a recompile, persist it before reload (`AssemblyReloadEvents.beforeAssemblyReload`) and restore on init (`[InitializeOnLoad]` static ctor → defer to first `EditorApplication.update` tick). For state that's free to rebuild, just rebuild it lazily.

### 2. `SessionState` vs `EditorPrefs` — pick the right one
Both survive a domain reload but they have different lifetimes:
- `SessionState` — per Unity-session, wiped when Unity closes. Use for "lasts as long as this editor session" things: heartbeats, queue snapshots, compile timestamps, window HWND.
- `EditorPrefs` — persists across editor restarts (machine-wide). Use for genuine settings (paths, toggles, "don't show this tip again").
- **Rule:** default to `SessionState` for transient runtime state, `EditorPrefs` for user preferences. Mixing them up either pollutes preferences with churn or loses state too aggressively on relaunch. Centralize keys in a `SessionKeys` static class so the prefix and naming stay consistent.

### 3. `[InitializeOnLoad]` runs too early — defer to `EditorApplication.update`
`[InitializeOnLoad]`'s static ctor fires during domain init, before some Editor systems are ready. Creating a `ScriptableObject`, opening a window, calling `EditorWindow.GetWindow`, or hitting `AssetDatabase` from inside the static ctor can throw or no-op.
- **Real pattern (from clibridge4unity itself):** static ctor adds an `EditorApplication.update += InitOnFirstTick;` handler; `InitOnFirstTick` removes itself and does the real init. One frame later everything is ready.
- **Rule:** never do real work in an `[InitializeOnLoad]` static ctor. Subscribe to first `EditorApplication.update` and unsubscribe immediately when it fires. The Editor Performance Mandate (CLAUDE.md) reinforces this: heavy work must be command-triggered or deferred, never on the InitializeOnLoad path itself.

### 4. `AssemblyReloadEvents.beforeAssemblyReload` is your last chance to save state
The Editor calls `beforeAssemblyReload` right before tearing down the AppDomain. You have one synchronous window to push state into `SessionState`. After that, your callbacks die and any in-flight `Task`s never resolve.
- **Rule:** every long-lived component that holds non-trivial state subscribes to `AssemblyReloadEvents.beforeAssemblyReload` and snapshots: in-flight queue items, current window HWND, pending compile request time, anything you'd otherwise have to rediscover after the reload.

### 5. Named pipes, sockets, HttpClient instances all die — don't try to keep them open
You'll see articles online about "graceful reload" patterns. They don't apply to pipe handles or socket file descriptors held by destroyed `AppDomain` objects.
- **Rule:** any IPC, network, or OS-handle-backed connection is per-domain. Tear it down in `beforeAssemblyReload`, recreate in `[InitializeOnLoad]`. Make the client side (CLI / external process) reconnect on its own — don't pretend the connection survived. clibridge4unity's `REFRESH` returns immediately and the CLI polls for the pipe to come back.

### 6. PlayMode reload runs TWICE (enter, exit) — both can lose mid-flight state
Tests, recording tools, and editor-side observers that span a PlayMode session need to handle the reload at PlayMode entry AND at exit. Subscribing once on entry and assuming you're still subscribed at exit will silently break.
- **Real pattern (clibridge4unity TestRunner):** PlayMode test runs persist results to `Temp/clibridge4unity_test.log` + a `.status` file; `[InitializeOnLoad]` checks SessionState on every domain init and re-registers the TestRunnerApi callbacks if a run is still in progress. The CLI tails the log file, not the pipe.
- **Rule:** for any work that spans a PlayMode session, the durable channel is a file on disk + a SessionState flag, not a callback or pipe. Re-hook subscriptions on every `[InitializeOnLoad]` if the flag says a run is mid-flight.

### 7. `IntPtr` / native window handles are stable across reloads, but the .NET reference isn't
The OS `HWND` for Unity's window doesn't change when the AppDomain reloads — but your static `IntPtr _hwnd` field gets reset to `IntPtr.Zero`. The fix is to persist the value (cast `IntPtr.ToInt64()`) into `SessionState` and restore on init.
- **Rule:** native handles fit in a `long`; round-trip via `SessionState.SetString(key, hwnd.ToInt64().ToString())` and `new IntPtr(long.Parse(...))` after reload. Validate the handle is still alive (`IsWindow`) before using.

### 8. `ScriptableObject.CreateInstance` instances created from code do not survive
Editor code commonly does `ScriptableObject.CreateInstance<MyApi>()` to wrap stateful APIs. The instance is destroyed by the next reload (Unity manages SO lifetimes). Calls to it post-reload throw `MissingReferenceException`.
- **Rule:** treat dynamically-created ScriptableObjects as per-domain. Recreate after every reload. Don't cache references across `beforeAssemblyReload`. Persistent SOs are those *saved as assets* (`Assets/.../*.asset`) — they survive because the underlying YAML is on disk.

### 9. `Task` / `UniTask` / `Coroutine` are all per-domain
Every running async operation, including ones awaiting `Task.Delay`, dies. Their continuations never run. If you have a token tied to nothing else, it never gets cancelled — leaking nothing because the whole graph is reaped.
- **Rule:** link your `CancellationTokenSource` to `AssemblyReloadEvents.beforeAssemblyReload` so any cooperative async code that catches `OperationCanceledException` exits cleanly. For UniTask, `AttachExternalCancellation(beforeReloadToken)` does the same. After reload, restart whatever should still be running by reading `SessionState`.

### 10. `[DidReloadScripts]` runs ONCE after every reload — useful, but not first
A `[DidReloadScripts]` static method fires after assemblies are loaded. It's higher-level than `[InitializeOnLoad]` (the static ctor for `[InitializeOnLoad]` types fires *before* `[DidReloadScripts]`). Treat them as a pair: `[InitializeOnLoad]` for "set up the type", `[DidReloadScripts]` for "do something now that all types are loaded."
- **Rule:** use `[InitializeOnLoad]` static ctor (deferred) for instance/state init. Use `[DidReloadScripts]` for one-shot integrations that need every assembly loaded (e.g. scanning for `[Attribute]` decorators across user assemblies, finalizing a code-gen step).

## Workflow

1. **Inventory static state.** Every `static` field/dictionary/cache: is it free to rebuild, or do you need it after reload? Answer this before writing the code.
2. **Centralize keys.** All `SessionState` keys go in one `SessionKeys` (or similar) class with a project prefix (`Bridge_LastCompileRequest`, etc.). One file, one source of truth, easy to grep.
3. **Snapshot before, restore after.** Every component that owns persistent state implements two methods: `OnBeforeReload()` (called from `AssemblyReloadEvents.beforeAssemblyReload`) and `OnAfterReload()` (called from `[InitializeOnLoad]` deferred init).
4. **Make the client robust to reload.** External tools (CLIs, IDE plugins, build pipelines) must tolerate the connection dying mid-call. They poll back; the editor side doesn't pretend.
5. **PlayMode-spanning work uses the disk.** Files in `Temp/` (or `Library/`) + a status file are the durable channel. Don't rely on callbacks or pipes to survive both reloads.
6. **Test it.** Trigger a domain reload (`Ctrl+R` / `clibridge4unity REFRESH`) mid-feature and confirm the post-reload state matches your expectations. If it doesn't, you have a bug.

## Quick reference — long-lived service pattern

```csharp
[InitializeOnLoad]
public static class MyService
{
    private static SynchronizationContext _mainCtx;
    private static int _someState;

    static MyService()
    {
        // Defer real init — InitializeOnLoad is too early for some Editor APIs.
        EditorApplication.update += InitOnFirstTick;
    }

    private static void InitOnFirstTick()
    {
        EditorApplication.update -= InitOnFirstTick;
        _mainCtx = SynchronizationContext.Current;

        // Restore from SessionState (survives domain reload, dies with Unity).
        _someState = SessionState.GetInt("MyService_State", 0);

        // Hook the next reload so we can save again.
        AssemblyReloadEvents.beforeAssemblyReload += SaveBeforeReload;
    }

    private static void SaveBeforeReload()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= SaveBeforeReload;
        SessionState.SetInt("MyService_State", _someState);
        // Tear down per-domain resources here (pipes, file handles, ScriptableObjects).
    }
}
```

## Quick reference — SessionKeys centralisation

```csharp
public static class SessionKeys
{
    public static readonly string LastCompileRequest = "Bridge_LastCompileRequest";
    public static readonly string LastCompileTime    = "Bridge_LastCompileTime";
    public static readonly string TestRunId          = "Bridge_TestRunId";
    public static readonly string TestLogPath        = "Bridge_TestLogPath";
    public static readonly string TestStatusPath     = "Bridge_TestStatusPath";
    public static readonly string UnityHwnd          = "Bridge_UnityHwnd";
}
```
Every key prefixed (`Bridge_`); single file; easy diff / rename. Avoids collisions with other packages also using `SessionState`.

## Related
- `clibridge4unity-async-mainthread` — async patterns + cancellation that has to survive (or die cleanly at) reload
- `clibridge4unity-serialization` — what survives via asset files vs what's transient
- `clibridge4unity-tests` — PlayMode test result capture via `Temp/clibridge4unity_test.log`, the canonical "use a file, not the pipe" pattern
