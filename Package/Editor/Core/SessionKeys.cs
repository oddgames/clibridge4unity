namespace clibridge4unity
{
    /// <summary>
    /// Centralized SessionState key constants for all state that persists across domain reloads.
    /// All keys use the "Bridge_" prefix to avoid collisions with other packages.
    /// </summary>
    public static class SessionKeys
    {
        public static readonly string LastCompileRequest = "Bridge_LastCompileRequest";
        public static readonly string LastCompileTime = "Bridge_LastCompileTime";
        public static readonly string LogNextId = "Bridge_LogNextId";
        public static readonly string UnityHwnd = "Bridge_UnityHwnd";
        public static readonly string MainThreadId = "Bridge_MainThreadId";
    }
}
