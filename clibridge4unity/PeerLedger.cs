using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace clibridge4unity;

/// <summary>
/// Coordination ledger for multiple Claude/CLI windows sharing ONE Unity editor.
///
/// Where <see cref="SessionLedger"/> is per-invocation presence, PeerLedger tracks a *stable
/// per-window* identity (anchored to the parent claude/terminal process that outlives any single
/// CLI invocation) plus recent activity. Unity is a single shared mutable resource — one editor,
/// one pipe server, one scene, one play-mode state — so two windows routinely stomp each other:
///   - COMPILE/REFRESH domain-reloads Unity → breaks every other window's in-flight pipe command.
///   - PLAY/STOP/PAUSE/STEP changes the global play state for everyone.
///   - BUILD blocks ALL bridge commands until it finishes.
///   - Two windows editing the same asset → silent last-writer-wins clobber.
///
/// PeerLedger records what each window is doing and surfaces an *advisory* warning before a command
/// that would stomp another window. It NEVER blocks — the agent/user decides what to do.
///
/// Storage: {projectPath}/.clibridge4unity/peers/
///   {peerId}.peer    durable presence + recent-activity ring (key=value, repeatable keys)
///   {peerId}.active  present only while a command is in flight (the "right now" signal)
///   {peerId}.inbox   directed messages (append-only)
///   {peerId}.cursor  count of inbox lines already surfaced via piggyback
/// </summary>
internal static class PeerLedger
{
    // ───────────────────── Stable per-window identity ─────────────────────

    static string _cachedId;
    static int _anchorPid;
    static string _anchorName;

    /// <summary>Stable id for THIS window (survives across CLI invocations). e.g. "peer-3F2A".</summary>
    public static string SelfId { get { EnsureIdentity(); return _cachedId; } }
    public static int AnchorPid { get { EnsureIdentity(); return _anchorPid; } }

    static void EnsureIdentity()
    {
        if (_cachedId != null) return;

        // Explicit override wins — lets a harness pin a guaranteed-stable per-session id, which
        // also sidesteps the "two panels share one process" collision noted in ResolveAnchor().
        string env = Environment.GetEnvironmentVariable("CLIBRIDGE_PEER_ID");
        if (!string.IsNullOrWhiteSpace(env))
        {
            _cachedId = "peer-" + Sanitize(env.Trim());
            _anchorPid = SafeSelfPid();
            _anchorName = "env";
            return;
        }

        (_anchorPid, _anchorName) = ResolveAnchor();
        _cachedId = $"peer-{_anchorPid:X}";
    }

    static int SafeSelfPid() { try { return Process.GetCurrentProcess().Id; } catch { return 0; } }

    /// <summary>
    /// Walk the process tree to find a process that lives as long as the *window* does — the CLI
    /// itself is ephemeral (one process per command) so it can't anchor identity. We prefer a
    /// "claude" ancestor; otherwise the first ancestor that isn't a shell/terminal/CLI (typically
    /// the node/editor host). Caveat: two chat panels hosted by ONE process (e.g. two panels in the
    /// same VSCode window) collapse to the same id — set CLIBRIDGE_PEER_ID to disambiguate.
    /// Windows-only (toolhelp); other OSes degrade to the current pid (per-invocation, like sessions).
    /// </summary>
    static (int pid, string name) ResolveAnchor()
    {
        int self = SafeSelfPid();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return (self, "self");

        try
        {
            var parent = new Dictionary<int, int>();
            var name = new Dictionary<int, string>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return (self, "self");
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref e))
                {
                    do
                    {
                        parent[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                        name[(int)e.th32ProcessID] = e.szExeFile ?? "";
                    } while (Process32Next(snap, ref e));
                }
            }
            finally { CloseHandle(snap); }

            int cur = self, firstStable = 0; string firstStableName = null;
            var seen = new HashSet<int>();
            for (int depth = 0; depth < 24 && parent.TryGetValue(cur, out int p) && p != 0 && seen.Add(p); depth++)
            {
                string pn = name.TryGetValue(p, out var n) ? n : "";
                string low = pn.ToLowerInvariant();
                if (low.Contains("claude")) return (p, pn);
                if (firstStable == 0 && !IsTransientProcess(low)) { firstStable = p; firstStableName = pn; }
                cur = p;
            }
            if (firstStable != 0) return (firstStable, firstStableName);
        }
        catch { }
        return (self, "self");
    }

    // Shells / terminals / the CLI itself — they respawn per command, so they can't anchor a window.
    static bool IsTransientProcess(string lowerExe)
        => lowerExe.StartsWith("clibridge4unity")
        || lowerExe is "pwsh.exe" or "powershell.exe" or "cmd.exe" or "bash.exe" or "sh.exe"
            or "zsh.exe" or "wsl.exe" or "wslhost.exe" or "conhost.exe" or "windowsterminal.exe"
            or "openconsole.exe" or "dotnet.exe";

    // ───────────────────── Storage ─────────────────────

    static string PeersDir(string projectPath) => Path.Combine(projectPath, ".clibridge4unity", "peers");
    static string PeerFile(string projectPath, string id) => Path.Combine(PeersDir(projectPath), id + ".peer");
    static string ActiveFile(string projectPath, string id) => Path.Combine(PeersDir(projectPath), id + ".active");
    static string InboxFile(string projectPath, string id) => Path.Combine(PeersDir(projectPath), id + ".inbox");
    static string CursorFile(string projectPath, string id) => Path.Combine(PeersDir(projectPath), id + ".cursor");

    // Commands worth recording in the activity ring (skip trivial diagnostics — they're just noise).
    static readonly HashSet<string> MeaningfulCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMPILE","REFRESH","BUILD","PLAY","STOP","PAUSE","STEP","SAVE","LOAD",
        "ASSET_MOVE","ASSET_COPY","ASSET_DELETE","ASSET_RESERIALIZE","REIMPORT","ASSET_LABEL","ASSET_MKDIR",
        "PREFAB_CREATE","PREFAB_INSTANTIATE","PREFAB_SAVE","COMPONENT_SET","COMPONENT_ADD","COMPONENT_REMOVE",
        "CODE_EXEC","EXEC","CODE_EXEC_RETURN","EVAL","CREATE","DELETE","TEST"
    };

    // Diagnostics that don't need a stable Unity and never stomp anyone — used to suppress noise.
    static readonly HashSet<string> TrivialCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "PING","PROBE","DIAG","BRIDGEINFO","STATUS","HELP","VERSION","SESSIONS",
        "PEERS","PEER_SEND","PEER_INBOX","WAKEUP","DISMISS","SCREENSHOT","LAST","LINT","CODE_ANALYZE"
    };

    static bool IsTrivial(string cmdUpper) => cmdUpper != null && TrivialCommands.Contains(cmdUpper);

    /// <summary>Update this window's durable presence record (lastSeen, cwd, play-mode, activity ring).</summary>
    public static void Touch(string projectPath, string command, string data)
    {
        try
        {
            string id = SelfId;
            Directory.CreateDirectory(PeersDir(projectPath));
            var p = ReadPeer(PeerFile(projectPath, id)) ?? new Peer { Id = id, FirstSeenUtc = DateTime.UtcNow };

            p.AnchorPid = AnchorPid;
            p.AnchorName = _anchorName;
            p.Cwd = Environment.CurrentDirectory;
            p.LastSeenUtc = DateTime.UtcNow;

            string cmdUpper = command?.ToUpperInvariant();
            if (cmdUpper == "PLAY") p.PlayMode = true;
            else if (cmdUpper == "STOP") p.PlayMode = false;

            if (cmdUpper != null && MeaningfulCommands.Contains(cmdUpper))
            {
                p.Acts.Add((DateTime.UtcNow, cmdUpper, Summarize(data)));
                while (p.Acts.Count > 8) p.Acts.RemoveAt(0);

                foreach (var path in ExtractPaths(data))
                {
                    p.Touches.Add((DateTime.UtcNow, path));
                }
                while (p.Touches.Count > 12) p.Touches.RemoveAt(0);
            }

            WritePeer(PeerFile(projectPath, id), p);
        }
        catch { }
    }

    /// <summary>Mark a command as in-flight ("right now" signal). Cleared by <see cref="ClearActive"/>.</summary>
    public static void MarkActive(string projectPath, string command, string data)
    {
        try
        {
            string id = SelfId;
            Directory.CreateDirectory(PeersDir(projectPath));
            var sb = new StringBuilder();
            sb.AppendLine($"pid={SafeSelfPid()}");
            sb.AppendLine($"command={command ?? ""}");
            sb.AppendLine($"args={Escape(Summarize(data))}");
            sb.AppendLine($"startedAt={DateTime.UtcNow:O}");
            File.WriteAllText(ActiveFile(projectPath, id), sb.ToString());
        }
        catch { }
    }

    public static void ClearActive(string projectPath)
    {
        try { File.Delete(ActiveFile(projectPath, SelfId)); } catch { }
    }

    // ───────────────────── Reading peers ─────────────────────

    public class Active
    {
        public int Pid;
        public string Command;
        public string Args;
        public DateTime StartedUtc;
    }

    public class Peer
    {
        public string Id;
        public int AnchorPid;
        public string AnchorName;
        public string Cwd;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
        public bool PlayMode;
        public List<(DateTime ts, string cmd, string summary)> Acts = new();
        public List<(DateTime ts, string path)> Touches = new();
        public Active Active; // null unless a command is in flight right now
        public TimeSpan Age => DateTime.UtcNow - LastSeenUtc;
    }

    /// <summary>List live peer windows. Prunes records whose anchor process is gone.</summary>
    public static List<Peer> List(string projectPath, bool excludeSelf = true)
    {
        var results = new List<Peer>();
        string dir = PeersDir(projectPath);
        if (!Directory.Exists(dir)) return results;
        string self = SelfId;

        foreach (var file in Directory.EnumerateFiles(dir, "*.peer"))
        {
            try
            {
                var p = ReadPeer(file);
                if (p == null) { TryDelete(file); continue; }
                if (excludeSelf && p.Id == self) continue;

                // Liveness: the anchor (window) process must still be running.
                if (p.AnchorPid > 0)
                {
                    try { Process.GetProcessById(p.AnchorPid); }
                    catch { PruneePeer(projectPath, p.Id); continue; }
                }

                p.Active = ReadActive(projectPath, p.Id);
                results.Add(p);
            }
            catch { TryDelete(file); }
        }
        return results.OrderBy(p => p.FirstSeenUtc).ToList();
    }

    static Active ReadActive(string projectPath, string id)
    {
        try
        {
            string file = ActiveFile(projectPath, id);
            if (!File.Exists(file)) return null;
            var a = new Active();
            foreach (var raw in File.ReadAllLines(file))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq), v = Unescape(raw.Substring(eq + 1));
                switch (k)
                {
                    case "pid": int.TryParse(v, out a.Pid); break;
                    case "command": a.Command = v; break;
                    case "args": a.Args = v; break;
                    case "startedAt": DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out a.StartedUtc); break;
                }
            }
            // Stale .active (CLI died without clearing) — the invocation pid is gone.
            if (a.Pid > 0) { try { Process.GetProcessById(a.Pid); } catch { TryDelete(file); return null; } }
            return string.IsNullOrEmpty(a.Command) ? null : a;
        }
        catch { return null; }
    }

    static void PruneePeer(string projectPath, string id)
    {
        TryDelete(PeerFile(projectPath, id));
        TryDelete(ActiveFile(projectPath, id));
        TryDelete(InboxFile(projectPath, id));
        TryDelete(CursorFile(projectPath, id));
    }

    // ───────────────────── Conflict detection ─────────────────────

    /// <summary>Return advisory warnings for running <paramref name="cmdUpper"/> right now, given
    /// what other live windows are doing. Empty when there's nothing to worry about.</summary>
    public static List<string> CheckConflicts(string projectPath, string cmdUpper, string data)
    {
        var warns = new List<string>();
        try
        {
            var peers = List(projectPath, excludeSelf: true);
            if (peers.Count == 0) return warns;

            bool isCompile = cmdUpper is "COMPILE" or "REFRESH";
            bool isPlay = cmdUpper is "PLAY" or "STOP" or "PAUSE" or "STEP";
            bool isBuild = cmdUpper == "BUILD";
            var myPaths = ExtractPaths(data);
            bool isAssetWrite = IsAssetWriteCmd(cmdUpper) && myPaths.Count > 0;

            foreach (var pr in peers)
            {
                string activeCmd = pr.Active?.Command?.ToUpperInvariant();

                // (A) Peer is mid heavy op RIGHT NOW → MY command will likely fail.
                if (activeCmd == "COMPILE" || activeCmd == "REFRESH")
                {
                    if (!IsTrivial(cmdUpper))
                        warns.Add($"{pr.Id} is recompiling Unity right now ({activeCmd} started {Ago(pr.Active.StartedUtc)}). Your {cmdUpper} may time out — wait for the reload to finish.");
                }
                else if (activeCmd == "BUILD")
                {
                    if (!IsTrivial(cmdUpper))
                        warns.Add($"{pr.Id} is running a player BUILD right now — Unity blocks all bridge commands during a build, so your {cmdUpper} will be rejected until it completes.");
                }

                // (B) I'm about to COMPILE/REFRESH → I break their pipe + reset shared state.
                if (isCompile)
                {
                    if (pr.Active != null && !IsTrivial(activeCmd))
                        warns.Add($"{cmdUpper} domain-reloads Unity and will BREAK {pr.Id}'s in-flight {pr.Active.Command}. Coordinate before recompiling.");
                    else
                        warns.Add($"{cmdUpper} domain-reloads Unity (breaks pipes, resets play mode). {pr.Id} is active ({LastActDesc(pr)}) and will lose any in-flight command.");
                }

                // (C) Play-mode trample.
                if (isPlay && pr.PlayMode)
                    warns.Add($"{pr.Id} appears to be in PLAY mode — {cmdUpper} changes the shared play state for them too.");

                // (D) BUILD locks everyone out.
                if (isBuild)
                    warns.Add($"BUILD blocks ALL bridge commands for every window until it finishes. {pr.Id} is active and will be locked out.");

                // (E) Same-asset concurrent edit.
                if (isAssetWrite)
                {
                    foreach (var mp in myPaths)
                        foreach (var (tts, tp) in pr.Touches)
                            if (PathsOverlap(mp, tp) && (DateTime.UtcNow - tts).TotalMinutes < 10)
                                warns.Add($"{pr.Id} touched {tp} {Ago(tts)} — you're about to {cmdUpper} {mp}. Possible concurrent edit / clobber; coordinate first.");
                }
            }
        }
        catch { }
        return warns.Distinct().ToList();
    }

    static readonly HashSet<string> AssetWriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASSET_MOVE","ASSET_COPY","ASSET_DELETE","ASSET_RESERIALIZE","REIMPORT","ASSET_LABEL","ASSET_MKDIR",
        "PREFAB_CREATE","PREFAB_SAVE","SAVE","LOAD"
    };
    static bool IsAssetWriteCmd(string cmdUpper) => cmdUpper != null && AssetWriteCommands.Contains(cmdUpper);

    // ───────────────────── Directed messages (thin layer) ─────────────────────

    public struct Msg { public DateTime Ts; public string From; public string Text; }

    /// <summary>Send a message to a peer id (exact or unambiguous prefix) or "all" live peers.
    /// Returns a human-readable result string; sets <paramref name="ok"/>.</summary>
    public static string Send(string projectPath, string target, string text, out bool ok)
    {
        ok = false;
        if (string.IsNullOrWhiteSpace(target)) return "no target specified";
        if (string.IsNullOrWhiteSpace(text)) return "empty message";

        var peers = List(projectPath, excludeSelf: true);
        List<Peer> targets;
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            targets = peers;
        else
        {
            string norm = target.StartsWith("peer-", StringComparison.OrdinalIgnoreCase) ? target : "peer-" + target;
            targets = peers.Where(p => p.Id.Equals(norm, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0)
                targets = peers.Where(p => p.Id.StartsWith(norm, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (targets.Count == 0)
            return peers.Count == 0 ? "no other active windows to message" : $"no peer matches '{target}' (active: {string.Join(", ", peers.Select(p => p.Id))})";

        string from = SelfId;
        int sent = 0;
        foreach (var t in targets)
        {
            try
            {
                File.AppendAllText(InboxFile(projectPath, t.Id), $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{Escape(from)}|{Escape(text)}\n");
                sent++;
            }
            catch { }
        }
        ok = sent > 0;
        return $"sent to {sent} window(s): {string.Join(", ", targets.Select(t => t.Id))}";
    }

    /// <summary>Read inbox lines not yet surfaced, advancing the cursor so each shows exactly once.</summary>
    public static List<Msg> DeliverUnread(string projectPath)
    {
        var result = new List<Msg>();
        try
        {
            string id = SelfId;
            string inbox = InboxFile(projectPath, id);
            if (!File.Exists(inbox)) return result;
            var lines = File.ReadAllLines(inbox);
            int cursor = ReadCursor(projectPath, id);
            for (int i = cursor; i < lines.Length; i++)
            {
                var m = ParseMsg(lines[i]);
                if (m.HasValue) result.Add(m.Value);
            }
            WriteCursor(projectPath, id, lines.Length);
        }
        catch { }
        return result;
    }

    /// <summary>Show the most recent <paramref name="max"/> messages and mark all as read.</summary>
    public static List<Msg> ReadInbox(string projectPath, int max)
    {
        var result = new List<Msg>();
        try
        {
            string id = SelfId;
            string inbox = InboxFile(projectPath, id);
            if (!File.Exists(inbox)) return result;
            var lines = File.ReadAllLines(inbox);
            foreach (var line in lines.Reverse().Take(max).Reverse())
            {
                var m = ParseMsg(line);
                if (m.HasValue) result.Add(m.Value);
            }
            WriteCursor(projectPath, id, lines.Length);
        }
        catch { }
        return result;
    }

    static Msg? ParseMsg(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var parts = line.Split('|');
        if (parts.Length < 3) return null;
        long.TryParse(parts[0], out long ts);
        return new Msg
        {
            Ts = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime,
            From = Unescape(parts[1]),
            Text = Unescape(string.Join("|", parts.Skip(2)))
        };
    }

    static int ReadCursor(string projectPath, string id)
    {
        try { return int.TryParse(File.ReadAllText(CursorFile(projectPath, id)), out int c) ? c : 0; }
        catch { return 0; }
    }
    static void WriteCursor(string projectPath, string id, int count)
    {
        try { File.WriteAllText(CursorFile(projectPath, id), count.ToString()); } catch { }
    }

    // ───────────────────── Formatting helpers ─────────────────────

    public static string Ago(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        if (s < 0) s = 0;
        if (s < 60) return $"{s:F0}s ago";
        if (s < 3600) return $"{s / 60:F0}m ago";
        return $"{s / 3600:F0}h ago";
    }

    static string LastActDesc(Peer p)
    {
        if (p.Active != null) return $"running {p.Active.Command} now";
        if (p.Acts.Count > 0) { var a = p.Acts[^1]; return $"last: {a.cmd} {a.summary} {Ago(a.ts)}"; }
        return $"last seen {Ago(p.LastSeenUtc)}";
    }

    public static string Describe(Peer p)
    {
        var sb = new StringBuilder();
        string state = p.PlayMode ? "IN PLAY MODE" : "edit mode";
        sb.AppendLine($"  {p.Id}  (window pid {p.AnchorPid}, last seen {Ago(p.LastSeenUtc)}; {state})");
        if (p.Active != null)
            sb.AppendLine($"    NOW RUNNING: {p.Active.Command} {p.Active.Args} (started {Ago(p.Active.StartedUtc)})");
        else if (p.Acts.Count > 0)
        {
            var a = p.Acts[^1];
            sb.AppendLine($"    recent: {a.cmd} {a.summary} ({Ago(a.ts)})");
        }
        if (!string.IsNullOrEmpty(p.Cwd)) sb.Append($"    cwd: {p.Cwd}");
        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ───────────────────── Path extraction / overlap ─────────────────────

    static readonly string[] AssetExts =
    {
        ".prefab",".unity",".asset",".mat",".cs",".uxml",".uss",".tss",".controller",".anim",
        ".png",".jpg",".jpeg",".tga",".fbx",".obj",".shader",".shadergraph",".json",".txt",".ttf",".otf"
    };

    static List<string> ExtractPaths(string data)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(data)) return paths;
        foreach (var rawTok in data.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string tok = rawTok.Trim('"', '\'');
            if (tok.StartsWith("-")) continue; // flag, not a path
            string lower = tok.ToLowerInvariant();
            bool looksLikePath = lower.Contains("assets/") || lower.Contains("assets\\")
                || lower.Contains("packages/") || lower.Contains("packages\\")
                || AssetExts.Any(ext => lower.EndsWith(ext));
            if (looksLikePath) paths.Add(NormPath(tok));
        }
        return paths.Distinct().ToList();
    }

    static string NormPath(string p) => p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    static bool PathsOverlap(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (a == b) return true;
        return a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
    }

    // ───────────────────── .peer (de)serialization ─────────────────────

    static Peer ReadPeer(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var p = new Peer();
            foreach (var raw in File.ReadAllLines(file))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq), v = Unescape(raw.Substring(eq + 1));
                switch (k)
                {
                    case "peerId": p.Id = v; break;
                    case "anchorPid": int.TryParse(v, out p.AnchorPid); break;
                    case "anchorName": p.AnchorName = v; break;
                    case "cwd": p.Cwd = v; break;
                    case "firstSeen": DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out p.FirstSeenUtc); break;
                    case "lastSeen": DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out p.LastSeenUtc); break;
                    case "play": p.PlayMode = v == "1"; break;
                    case "act":
                    {
                        var parts = v.Split('|');
                        if (parts.Length >= 3 && long.TryParse(parts[0], out long ts))
                            p.Acts.Add((DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime, parts[1], string.Join("|", parts.Skip(2))));
                        break;
                    }
                    case "touch":
                    {
                        var parts = v.Split('|');
                        if (parts.Length >= 2 && long.TryParse(parts[0], out long ts))
                            p.Touches.Add((DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime, string.Join("|", parts.Skip(1))));
                        break;
                    }
                }
            }
            return string.IsNullOrEmpty(p.Id) ? null : p;
        }
        catch { return null; }
    }

    static void WritePeer(string file, Peer p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"peerId={p.Id}");
        sb.AppendLine($"anchorPid={p.AnchorPid}");
        sb.AppendLine($"anchorName={Escape(p.AnchorName)}");
        sb.AppendLine($"cwd={Escape(p.Cwd)}");
        sb.AppendLine($"firstSeen={p.FirstSeenUtc:O}");
        sb.AppendLine($"lastSeen={p.LastSeenUtc:O}");
        sb.AppendLine($"play={(p.PlayMode ? "1" : "0")}");
        foreach (var a in p.Acts)
            sb.AppendLine($"act={new DateTimeOffset(a.ts, TimeSpan.Zero).ToUnixTimeSeconds()}|{a.cmd}|{Escape(a.summary)}");
        foreach (var t in p.Touches)
            sb.AppendLine($"touch={new DateTimeOffset(t.ts, TimeSpan.Zero).ToUnixTimeSeconds()}|{Escape(t.path)}");
        File.WriteAllText(file, sb.ToString());
    }

    static string Summarize(string data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        const int cap = 80;
        return Escape(data.Length > cap ? data.Substring(0, cap) + "..." : data);
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "anon";
    }

    static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
    static string Unescape(string s) => (s ?? "").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
    static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    // ───────────────────── Win32 (process tree) ─────────────────────

    const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
}
