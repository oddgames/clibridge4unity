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
        public static readonly string PlayModeStartTime = "Bridge_PlayModeStartTime";
        public static readonly string LastBuildPath = "Bridge_LastBuildPath";
        public static readonly string LastBuildTarget = "Bridge_LastBuildTarget";

        // Test-run state — survives the PlayMode entry/exit domain reloads so we can re-register
        // the TestRunnerApi callbacks in the new domain and keep persisting results.
        public static readonly string TestRunId = "Bridge_TestRunId";
        public static readonly string TestLogPath = "Bridge_TestLogPath";
        public static readonly string TestStatusPath = "Bridge_TestStatusPath";
        public static readonly string TestMode = "Bridge_TestMode";
    }
}
