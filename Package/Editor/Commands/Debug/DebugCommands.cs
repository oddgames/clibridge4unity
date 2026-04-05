using System.Threading.Tasks;

namespace clibridge4unity.Commands
{
    /// <summary>
    /// DEBUG command — Phase 2 stub for Mono soft debugger (attach, breakpoints, stepping).
    /// Phase 1 features (inspect, trace) are on CODE_EXEC_RETURN via --inspect and --trace flags.
    /// </summary>
    public static class DebugCommands
    {
        [BridgeCommand("DEBUG", "Debugger (Phase 2: attach, breakpoints, stepping)",
            Category = "Code",
            Usage = "DEBUG attach [port]     - Connect to Mono debugger agent\n" +
                    "  DEBUG break <file:line>  - Set breakpoint\n" +
                    "  DEBUG step/stepover/stepout/continue\n" +
                    "  DEBUG locals/args        - Inspect variables at breakpoint\n" +
                    "  \n" +
                    "  Phase 1 features are on CODE_EXEC_RETURN:\n" +
                    "    CODE_EXEC_RETURN <expr> --inspect [depth] [--private]\n" +
                    "    CODE_EXEC_RETURN <code> --trace [--maxlines N]",
            RequiresMainThread = false)]
        public static Task<string> Run(string data)
        {
            return Task.FromResult(Response.Error(
                "DEBUG Phase 2 (Mono soft debugger) is not yet implemented.\n" +
                "Available now via CODE_EXEC_RETURN:\n" +
                "  CODE_EXEC_RETURN <expr> --inspect [depth] [--private]  - Object reflection tree\n" +
                "  CODE_EXEC_RETURN <code> --trace [--maxlines N]         - Line-by-line execution trace"));
        }
    }
}
