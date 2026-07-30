using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace clibridge4unity;

/// <summary>
/// CLI-side gate on COMPILE/REFRESH. Refuses a domain reload that cannot accomplish anything,
/// without opening a pipe.
///
/// Why CLI-side and not in the Unity package:
///  - The Unity-side skip (CoreCommands.Compile) is disabled whenever the daemon is down —
///    ScanModifiedScripts sets scanFailed, and the skip requires !scanFailed, so a missing daemon
///    turns every COMPILE into a real reload. That is exactly the case a retry loop hits.
///  - A guard that lives behind the pipe cannot help once Unity is wedged, because reaching it
///    already requires the main thread we are trying to protect.
///  - Everything needed is on disk: source mtimes and Library/ScriptAssemblies mtimes. No Unity,
///    no daemon, no pipe.
///
/// Two independent checks:
///  1. UP-TO-DATE — every source file is older than the newest compiled assembly, so a compile has
///     already run since the last edit. Nothing to do.
///  2. SUCCESSION — repeated attempts with byte-identical inputs (same newest-source and same
///     newest-assembly watermark). Deliberately state-based, not a wall-clock cooldown: a timer
///     either blocks legitimate fast edit→compile→edit cycles or is trivially outrun by a loop
///     that sleeps longer than it. Any real edit moves the watermark and resets the counter, so
///     genuine work is never blocked no matter how fast it arrives.
///
/// Both are advisory in the sense that `COMPILE force` always bypasses them.
/// </summary>
static class CompileGuard
{
    // Attempts with identical state allowed before refusing. Two lets a human retry a transient
    // block (mid-import, momentary play mode) without argument; a loop is stopped on the third.
    const int MaxIdenticalAttempts = 2;

    internal enum Verdict { Allow, UpToDate, Looping }

    internal readonly struct Result
    {
        public readonly Verdict Verdict;
        public readonly string Message;
        public Result(Verdict v, string m) { Verdict = v; Message = m; }
    }

    static string StateFile(string projectPath)
        => Path.Combine(projectPath, ".clibridge4unity", "compile-guard");

    /// <summary>Newest mtime across Library/ScriptAssemblies — when Unity last finished any compile,
    /// regardless of what triggered it. 0 when the project has never compiled.</summary>
    static long NewestAssemblyTicks(string projectPath)
    {
        try
        {
            string dir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
            if (!Directory.Exists(dir)) return 0;
            long newest = 0;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                long t = File.GetLastWriteTimeUtc(dll).Ticks;
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch { return 0; }
    }

    /// <summary>Newest mtime across compile-relevant sources, plus the file that carried it.
    /// Mirrors Unity's compile inputs: .cs, .asmdef, .asmref under Assets/ and Packages/.</summary>
    static (long Ticks, string Path) NewestSource(string projectPath)
    {
        long newest = 0;
        string which = null;
        foreach (var root in new[] { "Assets", "Packages" })
        {
            string dir = Path.Combine(projectPath, root);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (!(f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                       || f.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
                       || f.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))) continue;
                    long t;
                    try { t = File.GetLastWriteTimeUtc(f).Ticks; } catch { continue; }
                    if (t > newest) { newest = t; which = f; }
                }
            }
            catch { }
        }
        return (newest, which);
    }

    static (long Src, long Asm, int Count) ReadState(string projectPath)
    {
        try
        {
            string f = StateFile(projectPath);
            if (!File.Exists(f)) return (0, 0, 0);
            long src = 0, asm = 0; int count = 0;
            foreach (var raw in File.ReadAllLines(f))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq), v = raw.Substring(eq + 1);
                switch (k)
                {
                    case "src": long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out src); break;
                    case "asm": long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out asm); break;
                    case "count": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out count); break;
                }
            }
            return (src, asm, count);
        }
        catch { return (0, 0, 0); }
    }

    static void WriteState(string projectPath, long src, long asm, int count)
    {
        try
        {
            string f = StateFile(projectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f,
                $"src={src.ToString(CultureInfo.InvariantCulture)}\n" +
                $"asm={asm.ToString(CultureInfo.InvariantCulture)}\n" +
                $"count={count.ToString(CultureInfo.InvariantCulture)}\n");
        }
        catch { }
    }

    /// <summary>Decide whether a COMPILE/REFRESH should be sent. Never throws — on any doubt it
    /// returns Allow, because wrongly blocking a needed compile is worse than a redundant one.</summary>
    public static Result Evaluate(string projectPath, bool force)
    {
        if (force) return new Result(Verdict.Allow, null);
        try
        {
            long asm = NewestAssemblyTicks(projectPath);
            var (srcTicks, srcPath) = NewestSource(projectPath);

            // Never compiled, or nothing to measure — no basis to refuse.
            if (asm == 0 || srcTicks == 0) return new Result(Verdict.Allow, null);

            var prev = ReadState(projectPath);
            bool sameState = prev.Src == srcTicks && prev.Asm == asm;
            int count = sameState ? prev.Count + 1 : 1;
            WriteState(projectPath, srcTicks, asm, count);

            // 1. Assemblies are newer than every source — a compile already covered these edits.
            if (asm >= srcTicks)
            {
                var when = new DateTime(asm, DateTimeKind.Utc).ToLocalTime();
                return new Result(Verdict.UpToDate,
                    $"Already compiled — assemblies are newer than every source file.\n" +
                    $"  last compile : {when:yyyy-MM-dd HH:mm:ss} (Library/ScriptAssemblies)\n" +
                    $"  newest source: {Rel(srcPath, projectPath)}\n" +
                    $"  Nothing to compile. Use 'COMPILE force' to reload anyway.");
            }

            // 2. Same inputs as the last N attempts and still not compiled — the request is looping.
            if (count > MaxIdenticalAttempts)
            {
                return new Result(Verdict.Looping,
                    $"Refusing COMPILE — {count} consecutive attempts with no change in between.\n" +
                    $"  newest source: {Rel(srcPath, projectPath)} (unchanged since attempt 1)\n" +
                    $"  assemblies   : unchanged, so no compile has completed\n" +
                    $"  Something is blocking compilation (play mode, a Player Build, an open modal,\n" +
                    $"  or a busy main thread) — retrying will not clear it. Check STATUS or DIAG.\n" +
                    $"  Use 'COMPILE force' to override this guard.");
            }

            return new Result(Verdict.Allow, null);
        }
        catch { return new Result(Verdict.Allow, null); }
    }

    static string Rel(string full, string projectPath)
    {
        if (string.IsNullOrEmpty(full)) return "(none)";
        try
        {
            string p = projectPath.Replace('\\', '/').TrimEnd('/') + "/";
            string f = full.Replace('\\', '/');
            return f.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? f.Substring(p.Length) : f;
        }
        catch { return full; }
    }
}
