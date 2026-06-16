---
name: clibridge4unity-async-mainthread
description: Use for ANY async/await, Task, UniTask, coroutine, or main-thread-marshaling work in Unity — when an `async` method touches Unity API (GameObject.Find, AssetDatabase, EditorUtility) and may hang the Editor, when blocking IO (HTTP, sockets, file reads) lives on the main thread, when a long operation freezes Unity, or when SwitchToMainThread / RunOnMainThreadAsync / SynchronizationContext.Post / EditorApplication.delayCall timing is involved. Auto-trigger on `Task.Run`, `async void`, `.Wait()`, `.Result`, `HttpClient.GetAsync`, `EditorCoroutineUtility`, `IEnumerator` + `StartCoroutine`, "the editor froze", "Unity is unresponsive", "deadlock", "CLIBridge.SwitchToMainThread", or anything that hops threads in a Unity editor or runtime context. Unity is single-threaded for its API; the cost of getting this wrong is a frozen editor + lost work.
---

# Unity async / main-thread

Standard Unity threading rules apply (main-thread-only API; capture the main-thread `SynchronizationContext`; no `async void`/`.Wait()`/`.Result`; off-thread for blocking IO then hop back; cancellation tied to `beforeAssemblyReload`). Apply that general knowledge — the project-specific pieces below are what's not standard.

## clibridge4unity specifics

- **CODE_EXEC helpers:** exec'd code starts on the main thread. Hop with `await CLIBridge.SwitchToBackground();` (blocking IO) and `await CLIBridge.SwitchToMainThread();` (Unity API). Pass `CLIBridge.Token` to cancellable APIs.
  ```csharp
  await CLIBridge.SwitchToBackground();
  var bytes = await http.GetByteArrayAsync(url, CLIBridge.Token);
  await CLIBridge.SwitchToMainThread();
  var tex = new Texture2D(2, 2); tex.LoadImage(bytes);
  ```
- **Detecting a blocked main thread:** a main-thread freeze stops the bridge heartbeat — `DIAG` reports `mainThreadResponsive: no`.
- **Backgrounded message pump:** when Unity is minimized/unfocused, posted continuations + `EditorApplication.update`/`delayCall` stall. clibridge4unity wakes the pump from a dedicated background polling thread via Win32 `PostMessage(hwnd, WM_NULL, 0, 0)` when work is pending. Don't rely on `delayCall` for liveness-critical hooks.
- **Known failure:** an HTML-to-UXML converter called `httpClient.GetByteArrayAsync(url).Result` on the main thread to fetch a Google Font → 30+ minute freeze on a slow CDN. Fix: `await` (not `.Result`) and move the download off the main thread.

## Related
- `clibridge4unity-domain-reload` — async state across recompile
- `clibridge4unity-performance` — GC alloc in async hot paths
- `clibridge4unity-run-code` skill — `CODE_EXEC` async semantics + `CLIBridge.SwitchToMainThread/SwitchToBackground/Token`
