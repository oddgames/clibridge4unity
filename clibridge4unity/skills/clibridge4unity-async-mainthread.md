---
name: clibridge4unity-async-mainthread
description: Use for ANY async/await, Task, UniTask, coroutine, or main-thread-marshaling work in Unity — when an `async` method touches Unity API (GameObject.Find, AssetDatabase, EditorUtility) and may hang the Editor, when blocking IO (HTTP, sockets, file reads) lives on the main thread, when a long operation freezes Unity, or when SwitchToMainThread / RunOnMainThreadAsync / SynchronizationContext.Post / EditorApplication.delayCall timing is involved. Auto-trigger on `Task.Run`, `async void`, `.Wait()`, `.Result`, `HttpClient.GetAsync`, `EditorCoroutineUtility`, `IEnumerator` + `StartCoroutine`, "the editor froze", "Unity is unresponsive", "deadlock", "CLIBridge.SwitchToMainThread", or anything that hops threads in a Unity editor or runtime context. Unity is single-threaded for its API; the cost of getting this wrong is a frozen editor + lost work.
---

# Unity async / main-thread

Unity API is **main-thread-only**. Almost every editor-freeze in Unity work is one of: (a) blocking I/O on the main thread, (b) `.Wait()`/`.Result` on a Task that needs the main thread, (c) `async void` swallowing the exception, or (d) the assumption that "it's an editor extension so it can be a background thread." The Editor's main thread is the same thread that handles your keystrokes — block it and the entire UI freezes.

## The one rule that prevents most of these

**Do non-Unity work off the main thread, then hop back for the Unity API calls.** Network, file IO, JSON parsing, heavy compute → background. `GameObject`/`AssetDatabase`/`EditorUtility` → main thread only. Never block, never poll, always `await` — and when you must block briefly, do it on a background thread.

## Pitfall catalog

### 1. `async void` swallows exceptions and you can't await it
`async void` is for event handlers only. Anywhere else, an exception goes nowhere — the operation silently fails, you stare at a dead UI wondering why nothing happened. The compiler doesn't warn.
- **Rule:** make everything `async Task` (or `async UniTaskVoid` if you've deliberately chosen UniTask's fire-and-forget). The one legit exception is `event` handler signatures that demand `void`.

### 2. `.Wait()` / `.Result` on a Task that needs the main thread = guaranteed deadlock
`async`/`await` posts the continuation back to the captured SynchronizationContext. In the editor that's Unity's main-thread context. If the *calling* thread is also the main thread and you call `.Wait()`, the continuation waits for the main thread to free, and the main thread waits for `.Wait()` to return → deadlock. The editor hangs.
- **Rule:** never `.Wait()` / `.Result` in editor code. If you absolutely must call async-from-sync, use `.ConfigureAwait(false)` consistently inside the async chain AND start the work on a background thread (`Task.Run(...).GetAwaiter().GetResult()`). Better: make the caller async too.

### 3. `Task.Run(...)` + Unity API inside → `UnityException: get_<api> can only be called from the main thread`
Threadpool threads can't call Unity API. The exception stack is unhelpful — it just points at your assignment.
- **Rule:** wrap the Unity-API part in a main-thread hop. With clibridge4unity's CODE_EXEC, use `await CLIBridge.SwitchToMainThread()`. In runtime/editor code, capture the main-thread `SynchronizationContext` at startup (`[InitializeOnLoad]` / `[RuntimeInitializeOnLoadMethod]`) and `Post(_ => …, null)` to it.

### 4. Blocking IO on the main thread freezes the editor (and the bridge)
`HttpClient.GetByteArrayAsync(url).Result`, `socket.Receive()`, `File.ReadAllText(...)` over a slow share — all freeze the main thread. The UI stops, the Console stops, log files stop, `EditorApplication.update` stops, and clibridge4unity's heartbeat stops (you'll see `mainThreadResponsive: no` in `DIAG`). This is the most common "the editor is dead" call.
- **Real failure mode:** an HTML-to-UXML converter called `httpClient.GetByteArrayAsync(url).Result` on the main thread to fetch a Google Font; 30+ minute freeze when the CDN was slow. Fix: `await` instead of `.Result`, AND move the download off the main thread.
- **Rule:** if a call can block for >50ms unbounded (network, named pipes, file IO over network share, GPU readback), it MUST run on a background thread. `await Task.Run(() => …)`, or in CODE_EXEC: `await CLIBridge.SwitchToBackground(); /* blocking */ await CLIBridge.SwitchToMainThread();`.

### 5. Coroutines run on the main thread — they don't free it
A common misconception: "I'll put it in a coroutine so it doesn't block the editor." Wrong. Coroutines are cooperative scheduling on the main thread. `yield return new WaitForSeconds(1)` lets the main thread tick, then resumes you. But `yield return File.ReadAllText(…)` (synchronous body, no actual yield happens during the read) blocks the main thread for the entire read.
- **Rule:** coroutines are for time-sliced game logic, not for offloading work. For blocking work use `Task.Run`. For frame-spread editor work use `EditorCoroutineUtility` (still main thread) when each yield is genuinely short.

### 6. `IEnumerator` editor coroutines need `EditorCoroutineUtility`, not `StartCoroutine`
`MonoBehaviour.StartCoroutine` doesn't exist outside play mode. Calling it from an editor script either no-ops or throws.
- **Rule:** `EditorCoroutineUtility.StartCoroutine(routine, owner)` (from `com.unity.editorcoroutines`) for editor coroutines. They tick from `EditorApplication.update` — main thread only, ~60 Hz when Unity has focus.

### 7. `await` inside `OnGUI` / `OnInspectorGUI` corrupts the IMGUI layout
IMGUI relies on call-order determinism between `Layout` and `Repaint` events. An `await` re-enters the GUI loop on the wrong event and triggers `ArgumentException: Getting control N's position in a group …` errors that look unrelated.
- **Rule:** never `await` in IMGUI callbacks. Capture the desired action (set a field, raise an event) and have a separate non-GUI method do the async work. If using UI Toolkit (preferred for new editor UI), this isn't an issue.

### 8. `SynchronizationContext.Post` on a backgrounded Unity Editor doesn't fire
When Unity is minimized or not the foreground window, its message pump goes idle. Work posted to its SynchronizationContext queues but doesn't execute. `EditorApplication.update` and `delayCall` also stop firing in this state.
- **Rule:** code that must run while the editor is backgrounded (e.g. an IPC bridge, a long batch job) needs to wake the message pump itself. clibridge4unity does this by running a dedicated background polling thread that sends Win32 `PostMessage(hwnd, WM_NULL, 0, 0)` when work is pending. Don't rely on `delayCall` for liveness-critical hooks.

### 9. Cancellation tokens are not optional — pair every long `await` with one
Long-lived Tasks held by static fields can leak the captured state machine (and its closures). Cancellation tokens aren't only for "give up early" — they're for "let the GC free this when the owner dies."
- **Rule:** every long-running async path in the editor accepts a `CancellationToken`. Bind it to `AssemblyReloadEvents.beforeAssemblyReload` so a recompile cancels pending work. Pair `await` calls that accept a token with one (`await Task.Delay(..., ct)`, `HttpClient.GetAsync(url, ct)`).

### 10. UniTask vs Task — pick one and don't mix mid-chain
`Cysharp.UniTask` is the standard Unity-friendly Task replacement (zero-alloc, hooks into PlayerLoop, ships `SwitchToMainThread`/`SwitchToThreadPool`). Mixing `Task` and `UniTask` in one chain works but adds a closure conversion at every boundary and can re-introduce SyncContext issues.
- **Rule:** if the project already uses UniTask, use it everywhere — `UniTask.SwitchToMainThread()`, `AttachExternalCancellation()`, and `UniTaskCompletionSource` are strictly better than the BCL equivalents for Unity. If it doesn't, plain `Task` + `ConfigureAwait(false)` + an explicit main-thread context is fine.

## Workflow

1. **Before writing async code:** identify each Unity-API touchpoint vs each blocking operation. Group them. Main-thread chunks should be short; blocking chunks long; explicit hops between.
2. **Caller signature:** prefer `async Task` (or `async UniTask`). Never `async void` except event handlers.
3. **Boundary discipline:** at each `await`, ask "what thread does this resume on?" If you can't answer, you're about to write a bug.
4. **CancellationToken:** thread one through every `await`. Tie to `beforeAssemblyReload`.
5. **Verify:** in dev, deliberately throw inside the async path and confirm the exception surfaces (catches `async void` leaks). Sleep the main thread (`Thread.Sleep(5000)`) on a path that should be off-main and confirm the UI stays responsive (catches "thought I was off-main" leaks).
6. **Using `clibridge4unity CODE_EXEC` snippets:** the exec'd code starts on the main thread. Wrap blocking work as: `await CLIBridge.SwitchToBackground(); /* blocking */ await CLIBridge.SwitchToMainThread(); /* Unity API */`. Pass `CLIBridge.Token` to cancellable APIs.

## Quick reference — hopping threads in an editor extension

```csharp
// At [InitializeOnLoad] time, capture the main thread's SynchronizationContext.
private static SynchronizationContext _mainCtx;
[InitializeOnLoadMethod]
private static void Init() => _mainCtx = SynchronizationContext.Current;

// Post work to the main thread and await it from any thread.
public static Task<T> RunOnMain<T>(Func<T> action)
{
    var tcs = new TaskCompletionSource<T>();
    _mainCtx.Post(_ =>
    {
        try { tcs.SetResult(action()); }
        catch (Exception e) { tcs.SetException(e); }
    }, null);
    return tcs.Task;
}

// Usage:
public static async Task<int> CountScenesAsync(CancellationToken ct)
{
    // Heavy non-Unity work — off the main thread.
    var index = await Task.Run(() => BuildSceneIndex(), ct);
    // Hop back for Unity API.
    return await RunOnMain(() => EditorBuildSettings.scenes.Length);
}
```

In `CODE_EXEC` snippets the bridge already gives you the helpers — prefer them:

```csharp
await CLIBridge.SwitchToBackground();                 // off main
var bytes = await http.GetByteArrayAsync(url, CLIBridge.Token);
await CLIBridge.SwitchToMainThread();                 // back on main
var tex = new Texture2D(2, 2); tex.LoadImage(bytes);  // Unity API safe here
```

## Related
- `unity-domain-reload` — what async state survives a recompile (and what doesn't)
- `unity-performance` — GC alloc patterns in async hot paths
- `clibridge4unity` skill — `CODE_EXEC` async semantics + `CLIBridge.SwitchToMainThread/SwitchToBackground/Token`
