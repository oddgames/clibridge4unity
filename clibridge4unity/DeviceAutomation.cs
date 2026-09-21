using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace clibridge4unity;

/// <summary>
/// Where device work leaves its evidence, and how a run of it is scripted.
///
/// Every DEVICE / BUGPUNCH command runs inside a per-device session directory
/// (`~/.clibridge4unity/devices/&lt;serial-or-id&gt;/`). Screenshots, UI dumps, script outputs,
/// snapshots and log captures are numbered artifacts in it, every command appends one line to
/// `actions.log`, and every command ends with an `[artifacts]` footer on stdout naming the files
/// it produced — so whoever reads the output (a model, most of the time) knows exactly where to
/// look next instead of guessing paths. A background log capture (`DEVICE log --start`) keeps
/// `logcat.txt` / `syslog.txt` filling while you tap around; the footer reports how many lines
/// arrived since the previous command, which is usually the answer to "did that do anything".
///
/// `DEVICE script &lt;file&gt;` runs a list of those same commands — cable and Bugpunch steps mixed —
/// with waits, `waitfor`, and `assert`, and writes a markdown report with per-step results and
/// the screenshots inline. That is the automation: no new vocabulary, just the commands in a file.
/// </summary>
static class DeviceSession
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity", "devices");

    [ThreadStatic] static Session _current;
    public static Session Current => _current;

    public sealed class Session
    {
        public readonly string Key, Dir;
        readonly List<(string kind, string path, string note)> _items = new();
        readonly Stopwatch _sw = Stopwatch.StartNew();
        string _command;

        internal Session(string key)
        {
            Key = key;
            Dir = Path.Combine(Root, Safe(key));
            Directory.CreateDirectory(Dir);
        }

        public string ActionsLog => Path.Combine(Dir, "actions.log");
        public string StateFile => Path.Combine(Dir, "session.json");

        /// <summary>What `DEVICE up` established: serial, platform, package, build, linked Bugpunch device. Flat string map.</summary>
        public Dictionary<string, string> ReadState()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(StateFile)) return d;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StateFile));
                foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
            }
            catch { }
            return d;
        }

        public void WriteState(Dictionary<string, string> d)
        {
            using var ms = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var kv in d) w.WriteString(kv.Key, kv.Value ?? "");
                w.WriteEndObject();
            }
            File.WriteAllBytes(StateFile, ms.ToArray());
        }

        public void SetState(string key, string value)
        {
            var d = ReadState(); d[key] = value; WriteState(d);
        }

        /// <summary>Next numbered artifact path: `0007_screenshot_after-login.png`.</summary>
        public string Artifact(string kind, string label, string ext)
        {
            int n = NextSeq();
            string name = $"{n:D4}_{kind}" + (string.IsNullOrEmpty(label) ? "" : "_" + Safe(label)) + (ext.StartsWith(".") ? ext : "." + ext);
            return Path.Combine(Dir, name);
        }

        int NextSeq()
        {
            string seqFile = Path.Combine(Dir, ".seq");
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var fs = new FileStream(seqFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var buf = new byte[16]; int len = fs.Read(buf, 0, buf.Length);
                    int n = int.TryParse(Encoding.ASCII.GetString(buf, 0, len).Trim(), out var v) ? v : 0;
                    n++;
                    fs.SetLength(0); fs.Seek(0, SeekOrigin.Begin);
                    var w = Encoding.ASCII.GetBytes(n.ToString()); fs.Write(w, 0, w.Length);
                    return n;
                }
                catch (IOException) { Thread.Sleep(20); }
            }
            return (int)(DateTime.UtcNow.Ticks % 100000);
        }

        /// <summary>Record an artifact for the footer (and the actions log).</summary>
        public void Add(string kind, string path, string note = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            _items.Add((kind, path, note));
        }

        public void SetCommand(string cmd) => _command = cmd;

        /// <summary>One line per command; the file is the device's history across runs.</summary>
        public void Log(string outcome)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ")
                  .Append(_command ?? "?").Append("  → ").Append(outcome).Append($"  ({_sw.ElapsedMilliseconds} ms)");
                foreach (var (kind, path, _) in _items) sb.Append("  ").Append(kind).Append('=').Append(Path.GetFileName(path));
                File.AppendAllText(ActionsLog, sb.ToString() + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>The line a model reads to know where things went. Always last on stdout.</summary>
        public void PrintFooter(int exitCode)
        {
            Log(exitCode == 0 ? "ok" : $"exit {exitCode}");
            var sb = new StringBuilder("[artifacts] dir=").Append(Dir);
            foreach (var (kind, path, note) in _items)
            {
                sb.Append("  ").Append(kind).Append('=').Append(path);
                if (!string.IsNullOrEmpty(note)) sb.Append(" (").Append(note).Append(')');
            }
            var tail = LogCapture.Describe(this);
            if (tail != null) sb.Append("  ").Append(tail);
            sb.Append("  actions=").Append(ActionsLog);
            Console.WriteLine(sb.ToString());
        }
    }

    /// <summary>Open (or switch to) the session for a device; commands call this once they know the device.</summary>
    public static Session Open(string key, string command)
    {
        if (_current == null || _current.Key != key) _current = new Session(key);
        _current.SetCommand(command);
        return _current;
    }

    public static void Add(string kind, string path, string note = null) => _current?.Add(kind, path, note);

    public static void Finish(int exitCode)
    {
        if (_current == null) return;
        _current.PrintFooter(exitCode);
        _current = null;
    }

    /// <summary>The session `DEVICE up` made current — every later command without --serial targets it.</summary>
    static string ActiveFile => Path.Combine(Root, ".active");
    public static string ActiveKey
    {
        get { try { return File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() : null; } catch { return null; } }
        set { Directory.CreateDirectory(Root); if (value == null) { try { File.Delete(ActiveFile); } catch { } } else File.WriteAllText(ActiveFile, value); }
    }

    public static Dictionary<string, string> ActiveState()
    {
        var key = ActiveKey;
        if (key == null) return new Dictionary<string, string>();
        return new Session(key).ReadState();
    }

    public static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' ? ch : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "device" : r.Length > 60 ? r.Substring(0, 60) : r;
    }
}

/// <summary>
/// Background log capture: one detached `adb logcat` / `idevicesyslog` per device writing to
/// `logcat.txt` / `syslog.txt` in the session dir. Started explicitly (`DEVICE log --start`) or
/// automatically by install --launch / launch / script — the moments something is about to
/// happen that you'll want the log for. Never clears the device buffer (`logcat -c`) because
/// Ipaapk / Android Studio may be reading the same buffer.
/// </summary>
static class LogCapture
{
    static string PidFile(DeviceSession.Session s) => Path.Combine(s.Dir, ".logtail.pid");
    static string MarkFile(DeviceSession.Session s) => Path.Combine(s.Dir, ".logtail.mark");
    public static string LogFile(DeviceSession.Session s, bool ios) => Path.Combine(s.Dir, ios ? "syslog.txt" : "logcat.txt");

    public static bool IsRunning(DeviceSession.Session s, out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(PidFile(s))) return false;
            if (!int.TryParse(File.ReadAllText(PidFile(s)).Trim(), out pid)) return false;
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>Start capture through cmd.exe so the redirect outlives us; returns the log path.</summary>
    public static string Start(DeviceSession.Session s, string exe, string args, bool ios)
    {
        if (IsRunning(s, out _)) return LogFile(s, ios);
        string log = LogFile(s, ios);
        try
        {
            // Roll the previous capture so a fresh run doesn't append to yesterday's 40 MB.
            if (File.Exists(log) && new FileInfo(log).Length > 0)
                File.Move(log, Path.Combine(s.Dir, Path.GetFileNameWithoutExtension(log) + "_" + File.GetLastWriteTime(log).ToString("yyyyMMdd_HHmmss") + ".txt"), true);
        }
        catch { }
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args} > \"{log}\" 2>&1\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        var p = Process.Start(psi);
        File.WriteAllText(PidFile(s), p.Id.ToString());
        File.WriteAllText(MarkFile(s), "0");
        return log;
    }

    public static bool Stop(DeviceSession.Session s)
    {
        if (!IsRunning(s, out var pid)) { try { File.Delete(PidFile(s)); } catch { } return false; }
        try { using var p = Process.GetProcessById(pid); p.Kill(true); } catch { }
        try { File.Delete(PidFile(s)); } catch { }
        return true;
    }

    /// <summary>"logcat=…\logcat.txt (+42 lines)" — lines since the last command's footer, then advance the mark.</summary>
    public static string Describe(DeviceSession.Session s)
    {
        bool ios = File.Exists(LogFile(s, true)) && !File.Exists(LogFile(s, false));
        string log = LogFile(s, ios);
        if (!File.Exists(log)) return null;
        bool live = IsRunning(s, out _);
        long len = new FileInfo(log).Length;
        long mark = 0;
        try { long.TryParse(File.ReadAllText(MarkFile(s)).Trim(), out mark); } catch { }
        int newLines = 0;
        if (len > mark)
        {
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(mark, SeekOrigin.Begin);
                var buf = new byte[Math.Min(len - mark, 8 * 1024 * 1024)];
                int n = fs.Read(buf, 0, buf.Length);
                for (int i = 0; i < n; i++) if (buf[i] == (byte)'\n') newLines++;
            }
            catch { }
        }
        try { File.WriteAllText(MarkFile(s), len.ToString()); } catch { }
        return $"{(ios ? "syslog" : "logcat")}={log} (+{newLines} lines{(live ? "" : ", capture stopped")})";
    }

    /// <summary>Last N lines of the capture file, for `DEVICE log` while a capture is running.</summary>
    public static IEnumerable<string> Tail(DeviceSession.Session s, bool ios, int lines)
    {
        string log = LogFile(s, ios);
        if (!File.Exists(log)) return Array.Empty<string>();
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var q = new Queue<string>(lines + 1);
            string l;
            while ((l = sr.ReadLine()) != null) { q.Enqueue(l); if (q.Count > lines) q.Dequeue(); }
            return q;
        }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>
/// `DEVICE script &lt;file&gt;` — the automation runner. One step per line, same words as the CLI:
///
///   # comment
///   install Builds/game.apk --launch
///   waitfor "Allow" 30              # poll the native UI until a view matches (Android)
///   tap "Allow"
///   wait 2
///   screenshot after-permissions
///   bp waitonline 60                # wait for the Bugpunch tunnel
///   bp run @scripts/skip-tutorial.cs
///   bp tap 0.5 0.85
///   bp screenshot main-menu
///   assert "Level 1"                # native UI must contain it
///   bp assert "MainMenu"            # Unity hierarchy must contain it
///   bp memsnap
///
/// Unprefixed lines are DEVICE commands; `bp` / `bugpunch` lines go to BUGPUNCH against the
/// device named by `--bp` (or the first online internal device). Steps stop at the first failure
/// unless `--continue`. `--shots` takes a screenshot after every step. The report is markdown
/// with the step table and every screenshot inline, written into the session dir.
/// </summary>
static class DeviceScript
{
    sealed class Step { public int Line; public string Raw; public string Target; public string[] Args; }
    sealed class Result { public Step Step; public bool Ok; public long Ms; public string Output; public List<string> Shots = new(); }

    public static int Run(string[] args)
    {
        string file = null, serial = null, bpDevice = null, name = null;
        bool cont = false, shots = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if ((a == "--serial" || a == "-s") && i + 1 < args.Length) serial = args[++i];
            else if (a == "--bp" && i + 1 < args.Length) bpDevice = args[++i];
            else if (a == "--name" && i + 1 < args.Length) name = args[++i];
            else if (a == "--continue") cont = true;
            else if (a == "--shots") shots = true;
            else if (file == null) file = a.Trim('"');
        }
        bpDevice ??= DeviceSession.ActiveState().GetValueOrDefault("bpId");
        if (file == null || !File.Exists(file))
        {
            Console.Error.WriteLine("Usage: DEVICE script <steps.txt> [--serial S] [--bp <bugpunch device>] [--continue] [--shots] [--name N]");
            if (file != null) Console.Error.WriteLine($"  file not found: {file}");
            return 1;
        }
        name ??= Path.GetFileNameWithoutExtension(file);

        var steps = Parse(file);
        if (steps.Count == 0) { Console.Error.WriteLine("No steps in file."); return 1; }

        // Session key: the cable device if there is one, else the bugpunch device — whichever the script actually targets.
        bool anyCable = steps.Any(s => s.Target == "device");
        string key = anyCable ? DeviceCommands.ResolveLocalKey(serial) : (bpDevice ?? "bugpunch");
        if (key == null) return 1;
        var session = DeviceSession.Open(key, $"DEVICE script {name}");
        string report = session.Artifact("report", name, ".md");
        Console.Error.WriteLine($"[script] {steps.Count} steps from {file} → {report}");

        if (anyCable) DeviceCommands.EnsureLogCapture(serial);

        var results = new List<Result>();
        var total = Stopwatch.StartNew();
        int failed = 0;
        foreach (var st in steps)
        {
            var r = new Result { Step = st };
            var sw = Stopwatch.StartNew();
            Console.Error.WriteLine($"[step {st.Line}] {st.Raw}");
            var capture = new StringWriter();
            var realOut = Console.Out; var realErr = Console.Error;
            Console.SetOut(new TeeWriter(realOut, capture));
            Console.SetError(new TeeWriter(realErr, capture));
            int code;
            try { code = Exec(st, serial, ref bpDevice, session); }
            catch (Exception ex) { code = 1; Console.Error.WriteLine($"Error: {ex.Message}"); }
            finally { Console.SetOut(realOut); Console.SetError(realErr); }
            r.Ms = sw.ElapsedMilliseconds;
            r.Ok = code == 0;
            r.Output = capture.ToString();
            r.Shots.AddRange(ExtractPaths(r.Output).Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));
            if (shots && st.Target == "device" && !st.Args[0].Equals("screenshot", StringComparison.OrdinalIgnoreCase))
            {
                string p = DeviceCommands.QuietScreenshot(serial, $"step{st.Line}");
                if (p != null) r.Shots.Add(p);
            }
            results.Add(r);
            if (!r.Ok)
            {
                failed++;
                Console.Error.WriteLine($"[step {st.Line}] FAILED");
                if (!cont) break;
            }
        }

        WriteReport(report, name, file, results, total.ElapsedMilliseconds, steps.Count);
        DeviceSession.Open(key, $"DEVICE script {name}");   // re-anchor after nested commands finished their own sessions
        DeviceSession.Add("report", report, $"{results.Count(r => r.Ok)}/{steps.Count} passed");
        Console.WriteLine($"Script {name}: {results.Count(r => r.Ok)}/{steps.Count} steps passed" + (failed > 0 ? $", {failed} failed" : "") + $" in {total.Elapsed.TotalSeconds:F1}s");
        return failed > 0 ? 1 : 0;
    }

    static List<Step> Parse(string file)
    {
        var steps = new List<Step>();
        int ln = 0;
        foreach (var raw in File.ReadAllLines(file))
        {
            ln++;
            string line = raw.Trim();
            int hash = IndexOfComment(line);
            if (hash >= 0) line = line.Substring(0, hash).Trim();
            if (line.Length == 0) continue;
            var toks = Tokenize(line);
            if (toks.Count == 0) continue;
            string target = "device";
            string head = toks[0].ToLowerInvariant();
            if (head == "bp" || head == "bugpunch") { target = "bp"; toks.RemoveAt(0); }
            else if (head == "device" || head == "adb") { toks.RemoveAt(0); }
            if (toks.Count == 0) continue;
            steps.Add(new Step { Line = ln, Raw = line, Target = target, Args = toks.ToArray() });
        }
        return steps;
    }

    static int IndexOfComment(string line)
    {
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') q = !q;
            else if (line[i] == '#' && !q && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return i;
        }
        return -1;
    }

    /// <summary>Shell-ish split: quotes group, backslash escapes a quote. Quotes are kept off the token.</summary>
    public static List<string> Tokenize(string line)
    {
        var toks = new List<string>(); var cur = new StringBuilder(); bool q = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; any = true; }
            else if (c == '"') { q = !q; any = true; }
            else if (char.IsWhiteSpace(c) && !q) { if (any) { toks.Add(cur.ToString()); cur.Clear(); any = false; } }
            else { cur.Append(c); any = true; }
        }
        if (any) toks.Add(cur.ToString());
        return toks;
    }

    static int Exec(Step st, string serial, ref string bpDevice, DeviceSession.Session session)
    {
        string verb = st.Args[0].ToLowerInvariant();
        var rest = st.Args.Skip(1).ToArray();

        // Script-only verbs.
        if (verb == "wait" || verb == "sleep")
        {
            double s = rest.Length > 0 && double.TryParse(rest[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1;
            Thread.Sleep((int)(s * 1000));
            Console.WriteLine($"waited {s}s");
            return 0;
        }
        if (verb == "echo") { Console.WriteLine(string.Join(" ", rest)); return 0; }

        if (st.Target == "bp")
        {
            if (verb == "waitonline")
            {
                int timeout = rest.Length > 0 && int.TryParse(rest[0], out var t) ? t : 60;
                return DeviceCommands.BpWaitOnline(ref bpDevice, timeout);
            }
            if (verb == "assert" || verb == "assert-not")
            {
                if (rest.Length == 0) { Console.Error.WriteLine("Usage: bp assert \"<GameObject name>\""); return 1; }
                return DeviceCommands.BpAssertHierarchy(ref bpDevice, rest[0], verb == "assert");
            }
            // Everything else is a normal BUGPUNCH <device> <verb> …
            if (bpDevice == null)
            {
                Console.Error.WriteLine("Error: no Bugpunch device — pass --bp <device> or add `bp waitonline` first.");
                return 1;
            }
            var args = new List<string> { bpDevice }; args.AddRange(st.Args);
            return DeviceCommands.RunBugpunch(args.ToArray(), fromScript: true);
        }

        // Cable verbs with script sugar.
        var withSerial = new List<string>(st.Args);
        if (serial != null && !withSerial.Contains("--serial") && !withSerial.Contains("-s")) { withSerial.Add("--serial"); withSerial.Add(serial); }
        if (verb == "waitfor" || verb == "waitfor-not")
        {
            if (rest.Length == 0) { Console.Error.WriteLine("Usage: waitfor \"<text|id>\" [timeoutSec]"); return 1; }
            int timeout = rest.Length > 1 && int.TryParse(rest[1], out var t) ? t : 30;
            return DeviceCommands.WaitForUi(serial, rest[0], timeout, verb == "waitfor");
        }
        if (verb == "assert" || verb == "assert-not")
        {
            if (rest.Length == 0) { Console.Error.WriteLine("Usage: assert \"<text|id>\""); return 1; }
            return DeviceCommands.WaitForUi(serial, rest[0], 0, verb == "assert");
        }
        return DeviceCommands.RunDevice(withSerial.ToArray(), fromScript: true);
    }

    static readonly Regex PathRx = new(@"[A-Za-z]:\\[^\s""]+?\.(png|jpg|jpeg|txt|md|snap|json)", RegexOptions.IgnoreCase);
    static IEnumerable<string> ExtractPaths(string output)
        => PathRx.Matches(output).Select(m => m.Value).Distinct();

    static void WriteReport(string report, string name, string file, List<Result> results, long totalMs, int totalSteps)
    {
        var sb = new StringBuilder();
        int ok = results.Count(r => r.Ok);
        sb.AppendLine($"# Script `{name}` — {ok}/{totalSteps} passed{(ok < totalSteps ? " ❌" : " ✅")}");
        sb.AppendLine();
        sb.AppendLine($"- Source: `{Path.GetFullPath(file)}`");
        sb.AppendLine($"- Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {totalMs / 1000.0:F1}s");
        if (results.Count < totalSteps) sb.AppendLine($"- Stopped at step {results.Last().Step.Line}; {totalSteps - results.Count} step(s) not run");
        sb.AppendLine();
        sb.AppendLine("| # | Step | Result | ms |");
        sb.AppendLine("|---|------|--------|----|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Step.Line} | `{r.Step.Raw.Replace("|", "\\|")}` | {(r.Ok ? "ok" : "**FAIL**")} | {r.Ms} |");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"## Step {r.Step.Line}: `{r.Step.Raw}` — {(r.Ok ? "ok" : "FAILED")}");
            var lines = r.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith("[artifacts]") && !l.StartsWith("[step")).ToList();
            if (lines.Count > 0)
            {
                sb.AppendLine("```");
                foreach (var l in lines.Take(60)) sb.AppendLine(l);
                if (lines.Count > 60) sb.AppendLine($"… {lines.Count - 60} more lines");
                sb.AppendLine("```");
            }
            foreach (var shot in r.Shots.Distinct())
                sb.AppendLine($"![{Path.GetFileName(shot)}]({new Uri(shot).AbsoluteUri})  \n`{shot}`");
            sb.AppendLine();
        }
        File.WriteAllText(report, sb.ToString());
    }

    sealed class TeeWriter : TextWriter
    {
        readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }
}
