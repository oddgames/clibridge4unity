using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace clibridge4unity;

/// <summary>/proc readers standing in for the Win32 process queries (wmic, Restart Manager, Toolhelp).</summary>
static class LinuxProc
{
    public static string[] CommandLineArgs(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").TrimEnd('\0').Split('\0'); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>The argv joined the way a Windows command line reads, so the same -projectPath/-logFile regexes parse it.</summary>
    public static string CommandLine(int pid)
        => string.Join(" ", CommandLineArgs(pid).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    public static string Environ(int pid, string key)
    {
        try
        {
            string prefix = key + "=";
            foreach (var entry in File.ReadAllText($"/proc/{pid}/environ").Split('\0'))
                if (entry.StartsWith(prefix, StringComparison.Ordinal)) return entry.Substring(prefix.Length);
        }
        catch { }
        return null;
    }

    public static int ParentPid(int pid)
    {
        try
        {
            // "pid (comm) state ppid ..." — comm may contain spaces and parens, so parse after the last ')'.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
            return int.Parse(fields[1]);
        }
        catch { return 0; }
    }

    public static string Comm(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
        catch { return ""; }
    }

    /// <summary>Which of <paramref name="candidates"/> hold <paramref name="path"/> open (their fd table links to it).</summary>
    public static List<uint> PidsHoldingFile(string path, IEnumerable<int> candidates)
    {
        var holders = new List<uint>();
        string target = Path.GetFullPath(path);
        foreach (int pid in candidates)
        {
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
                {
                    if (new FileInfo(fd).LinkTarget == target) { holders.Add((uint)pid); break; }
                }
            }
            catch { }
        }
        return holders;
    }

    /// <summary>
    /// Is a server listening on this Unix socket path? Reads /proc/net/unix rather than connecting,
    /// so it costs the bridge nothing and a socket file left behind by a crashed editor reads as dead.
    /// </summary>
    public static bool UnixSocketListening(string socketPath)
    {
        const int SO_ACCEPTCON = 0x10000;
        try
        {
            foreach (var line in File.ReadLines("/proc/net/unix"))
            {
                if (!line.EndsWith(socketPath, StringComparison.Ordinal)) continue;
                var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length >= 8 && cols[7] == socketPath && (Convert.ToInt32(cols[3], 16) & SO_ACCEPTCON) != 0)
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Start a long-lived child in its own session with stdio on /dev/null. A child that inherits the
    /// CLI's stdout keeps an `ssh host clibridge4unity ...` session open until the child exits.
    /// </summary>
    public static void StartDetached(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("setsid \"$@\" </dev/null >/dev/null 2>&1 &");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var sh = Process.Start(psi);
        sh?.WaitForExit(5000);
    }
}
