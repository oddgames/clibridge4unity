using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace clibridge4unity
{
    /// <summary>
    /// Utilities available to CODE_EXEC / CODE_EXEC_RETURN snippets for moving work between the
    /// Unity main thread and a background thread, plus a cooperative cancellation token.
    ///
    /// Exec'd code STARTS on the Unity main thread (most code needs Unity API). For blocking work
    /// (network, HTTP, large file IO) hop off the main thread so the Editor and the bridge never
    /// freeze, then hop back for the Unity API calls:
    ///
    ///   await CLIBridge.SwitchToBackground();                 // now on a threadpool thread
    ///   var bytes = await http.GetByteArrayAsync(url, CLIBridge.Token);
    ///   await CLIBridge.SwitchToMainThread();                 // back on the Unity main thread
    ///   var tex = new Texture2D(2, 2); tex.LoadImage(bytes);  // Unity API — safe here
    ///
    /// Pass CLIBridge.Token to any cancellable API so a command timeout actually aborts the work.
    /// </summary>
    public static class CLIBridge
    {
        // Per-exec cancellation token. AsyncLocal so it flows across SwitchToBackground/MainThread
        // hops within one exec, and stays isolated between concurrent execs.
        private static readonly AsyncLocal<CancellationToken> _token = new AsyncLocal<CancellationToken>();

        /// <summary>Cancellation token for the current exec (fires on command timeout/shutdown).</summary>
        public static CancellationToken Token => _token.Value;

        /// <summary>Set by the executor immediately before running a snippet. Not for snippet use.</summary>
        public static void SetToken(CancellationToken token) => _token.Value = token;

        /// <summary>Await to continue on Unity's main thread (where Unity API is legal).</summary>
        public static MainThreadAwaitable SwitchToMainThread() => default;

        /// <summary>Await to continue on a background (threadpool) thread, off the main thread.</summary>
        public static ThreadPoolAwaitable SwitchToBackground() => default;
    }

    public readonly struct MainThreadAwaitable
    {
        public MainThreadAwaiter GetAwaiter() => default;
    }

    public readonly struct MainThreadAwaiter : INotifyCompletion
    {
        // Already on the main thread → no hop, continue synchronously.
        public bool IsCompleted => CommandRegistry.IsOnMainThread;
        public void OnCompleted(Action continuation) => CommandRegistry.PostToMainThread(continuation);
        public void GetResult() { }
    }

    public readonly struct ThreadPoolAwaitable
    {
        public ThreadPoolAwaiter GetAwaiter() => default;
    }

    public readonly struct ThreadPoolAwaiter : INotifyCompletion
    {
        // Already off the main thread → no hop needed.
        public bool IsCompleted => !CommandRegistry.IsOnMainThread;
        public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(_ => continuation());
        public void GetResult() { }
    }
}
