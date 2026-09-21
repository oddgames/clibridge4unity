using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace clibridge4unity;

/// <summary>
/// Physical-device commands. Neither needs the Unity pipe or a Unity project.
///
///   DEVICE   — the cable. adb (Android) and libimobiledevice (iOS) wrapped so a build lands on
///              the phone with one command, whichever platform it is: list, install, uninstall,
///              launch, log.
///   BUGPUNCH — the network. Talks to a game running the Bugpunch SDK through the Bugpunch
///              server's personal-token API (/api/v1/devices): list, run C#, tap, screenshot,
///              hierarchy, console, raw RPC.
///
/// The two are deliberately separate commands rather than one "install-then-control" flow:
/// the cable sees whatever is plugged in, the server sees whatever is signed in — and those
/// are different sets. The BUGPUNCH surface only ever returns INTERNAL-role devices; that
/// filter is enforced by the server (apiV1Devices.routes.ts), not by this client, so no flag
/// here can widen it to external testers' or players' phones.
/// </summary>
static class DeviceCommands
{
    // ═══════════════════════════════════════════════════════════════════════
    //  DEVICE — adb / libimobiledevice
    // ═══════════════════════════════════════════════════════════════════════

    public static int RunDevice(string[] args, bool fromScript = false)
    {
        _cmdLine = "DEVICE " + string.Join(" ", args);
        DeviceSession.Current?.SetCommand(_cmdLine);
        int code = RunDeviceInner(args);
        // Footer only for a top-level command: inside a script the report collects the paths.
        if (fromScript) DeviceSession.Current?.Log(code == 0 ? "ok" : $"exit {code}"); else DeviceSession.Finish(code);
        return code;
    }
    [ThreadStatic] static string _cmdLine;

    static int RunDeviceInner(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "list": case "ls": case "devices": return ListDevices();
            case "up": case "connect": case "session": return Up(rest);
            case "down": case "disconnect": return Down(rest);
            case "status": return Status();
            case "discover": case "scan": return Discover(rest);
            case "shell": case "repl": return Shell();
            case "script": case "play": return DeviceScript.Run(rest);
            case "bp": case "bugpunch": return BpViaSession(rest);
            case "install": return Install(rest);
            case "uninstall": return Uninstall(rest);
            case "launch": case "run": case "start": return Launch(rest);
            case "log": case "logcat": case "syslog": return Log(rest);
            case "screenshot": case "shot": case "screencap": return Screenshot(rest);
            case "ui": case "dump": return UiDump(rest);
            case "tap": case "click": return Tap(rest);
            case "swipe": case "drag": return Swipe(rest);
            case "type": case "text": return TypeText(rest);
            case "key": case "keyevent": return Key(rest);
            case "tools": return ShowTools();
            case "help": case "-h": case "--help": PrintDeviceUsage(); return 0;
            default:
                // Verbs that only mean something inside the Unity view go to the linked Bugpunch
                // device, so after `DEVICE up` one vocabulary covers both transports.
                if (BpOnlyVerbs.Contains(sub)) return BpViaSession(args);
                Console.Error.WriteLine($"Unknown DEVICE subcommand: {args[0]}");
                PrintDeviceUsage();
                return 1;
        }
    }

    static readonly HashSet<string> BpOnlyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "exec", "eval", "action", "hierarchy", "tree", "scenes", "info", "device-info", "perf",
        "prefs", "playerprefs", "config", "game-config", "memsnap", "snapshot", "memory", "memstats", "mem", "get", "post", "console",
    };

    /// <summary>`DEVICE bp <verb…>` / a bp-only verb: BUGPUNCH against the session's linked device.</summary>
    static int BpViaSession(string[] args)
    {
        var state = DeviceSession.ActiveState();
        bool viaServer = args.Contains("--via-server");
        args = args.Where(a => a != "--via-server").ToArray();
        string target = null;
        string direct = state.GetValueOrDefault("direct");
        if (direct != null && !viaServer)
        {
            if (ProbeDirect(direct, 1500) != null) target = direct;
            else
            {
                // Forward died (iproxy killed, cable re-plugged) — set it up again before giving up on it.
                string key = DeviceSession.ActiveKey;
                var dev = key != null ? ResolveLocalCore(key, null) : null;
                if (dev != null && LinkDirect(dev, DeviceSession.Open(key, _cmdLine), state, 8, quiet: true)) target = state["direct"];
                else Console.Error.WriteLine("[DEVICE] direct route not answering — using the Bugpunch server");
            }
        }
        target ??= state.GetValueOrDefault("bpId");
        if (target == null)
        {
            Console.Error.WriteLine("Error: no Bugpunch route — run `DEVICE up <build>` first (or `BUGPUNCH <device|url> …` directly).");
            return 1;
        }
        var full = new List<string> { target }; full.AddRange(args);
        return RunBugpunch(full.ToArray(), fromScript: true);
    }

    // ─── Direct route: the dev build's LocalIdeServer over USB or LAN ──
    //
    // A Development Build serves the Remote IDE API itself on 47701 (SDK LocalIdeServer). Over
    // USB: `adb forward` / `iproxy` bring it to 127.0.0.1 — no LAN, no permission prompt, no
    // server, no token, no tester role. That is the route `DEVICE up` prefers; the server path
    // stays as the fallback (and the only path for release builds and remote phones).

    const int LocalIdePort = 47701;

    /// <summary>Establish the direct route, poll the hello, record it. Returns true on success.</summary>
    static bool LinkDirect(LocalDevice dev, DeviceSession.Session session, Dictionary<string, string> state, int waitSec, bool quiet = false)
    {
        string url = null;
        if (dev.Platform == "Android")
        {
            string adb = FindAdb(); if (adb == null) return false;
            // tcp:0 lets adb pick a free host port — several phones can be forwarded at once.
            var (code, stdout, stderr) = Exec(adb, $"-s {dev.Serial} forward tcp:0 tcp:{LocalIdePort}", 10000);
            if (code != 0 || !int.TryParse(stdout.Trim(), out var hostPort)) { if (!quiet) Console.Error.WriteLine($"[direct] adb forward failed: {(stderr + stdout).Trim()}"); return false; }
            url = $"http://127.0.0.1:{hostPort}";
        }
        else
        {
            string iproxy = FindIDeviceTool("iproxy");
            if (iproxy == null) { if (!quiet) Console.Error.WriteLine("[direct] iproxy.exe (libimobiledevice) not found — no USB route to iOS; use --lan <ip>"); return false; }
            int hostPort = FreeTcpPort();
            string pidFile = Path.Combine(session.Dir, ".iproxy.pid");
            try { if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var old)) { using var op = Process.GetProcessById(old); op.Kill(true); } } catch { }
            var psi = new ProcessStartInfo(iproxy, $"{hostPort} {LocalIdePort} -u {dev.Serial}") { UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi);
            File.WriteAllText(pidFile, p.Id.ToString());
            url = $"http://127.0.0.1:{hostPort}";
        }

        var sw = Stopwatch.StartNew();
        BpDevice hello = null;
        while (sw.Elapsed.TotalSeconds < Math.Max(waitSec, 2))
        {
            hello = ProbeDirect(url, 2000);
            if (hello != null) break;
            Thread.Sleep(1000);
        }
        if (hello == null)
        {
            if (!quiet) Console.Error.WriteLine($"[direct] nothing at {url} after {waitSec}s — not a Development Build, SDK < 0.8.223, or the app hasn't started.");
            return false;
        }
        state["direct"] = url;
        if (!string.IsNullOrEmpty(hello.Id) && !hello.Id.StartsWith("http")) state["bpId"] = hello.Id;
        state["directApp"] = $"{hello.AppVersion} {hello.Platform}";
        session.WriteState(state);
        if (!quiet) Console.Error.WriteLine($"[direct] {url} → {hello.Model} app {hello.AppVersion} ({hello.Platform}) — Remote IDE API without the server");
        return true;
    }

    static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return port;
    }

    static void UnlinkDirect(LocalDevice dev, DeviceSession.Session session, Dictionary<string, string> state)
    {
        string pidFile = Path.Combine(session.Dir, ".iproxy.pid");
        try { if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var pid)) { using var p = Process.GetProcessById(pid); p.Kill(true); } } catch { }
        try { File.Delete(pidFile); } catch { }
        if (dev?.Platform == "Android" && state.TryGetValue("direct", out var url))
        {
            string adb = FindAdb();
            var m = Regex.Match(url, @":(\d+)$");
            if (adb != null && m.Success) Exec(adb, $"-s {dev.Serial} forward --remove tcp:{m.Groups[1].Value}", 5000);
        }
        state.Remove("direct"); state.Remove("directApp");
        session.WriteState(state);
    }

    /// <summary>`DEVICE discover [sec]`: listen for LocalIdeServer beacons on the LAN.</summary>
    static int Discover(string[] args)
    {
        int sec = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 5;
        var seen = new Dictionary<string, string>();
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 47700));
            var sw = Stopwatch.StartNew();
            Console.Error.WriteLine($"[discover] listening for dev builds on the LAN for {sec}s…");
            while (sw.Elapsed.TotalSeconds < sec)
            {
                udp.Client.ReceiveTimeout = 1000;
                System.Net.IPEndPoint from = new(System.Net.IPAddress.Any, 0);
                byte[] data;
                try { data = udp.Receive(ref from); } catch (System.Net.Sockets.SocketException) { continue; }
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("bugpunch", out var t) || t.GetString() != "local-ide") continue;
                    int port = r.TryGetProperty("port", out var pp) ? pp.GetInt32() : LocalIdePort;
                    string url = $"http://{from.Address}:{port}";
                    if (seen.ContainsKey(url)) continue;
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    seen[url] = S("name");
                    Console.WriteLine($"{url,-28}{S("name"),-24}{S("model"),-16}{S("app")} {S("version")}  ({S("platform")})");
                }
                catch { }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
        if (seen.Count == 0) Console.WriteLine("No dev builds advertising. (Beacon = UDP 47700; the phone must be on this subnet and, on iOS, have allowed Local Network.)");
        else Console.WriteLine("Use one with: BUGPUNCH <url> <verb> …   or   DEVICE up --no-install --lan <ip>");
        return 0;
    }

    // ─── DEVICE up — the one call ─────────────────────────────────────
    //
    // install → launch → log capture → wait for the Bugpunch tunnel → remember all of it. After
    // this, every DEVICE command targets that phone with no --serial, `DEVICE run/tap/…` reach the
    // game through Bugpunch with no device argument, `DEVICE script` and `DEVICE shell` inherit the
    // same link. The individual commands are still there; this is the method that composes them.

    static int Up(string[] args)
    {
        string file = null, serial = null, bpName = null, package = null, lan = null;
        bool install = true, launch = true, viaServer = false; int waitSec = 120;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if ((a == "--serial" || a == "-s") && i + 1 < args.Length) serial = args[++i];
            else if (a == "--bp" && i + 1 < args.Length) bpName = args[++i];
            else if ((a == "--package" || a == "--bundle") && i + 1 < args.Length) package = args[++i];
            else if (a == "--wait" && i + 1 < args.Length) int.TryParse(args[++i], out waitSec);
            else if (a == "--no-install") install = false;
            else if (a == "--no-launch") launch = false;
            else if (a == "--lan" && i + 1 < args.Length) lan = args[++i];
            else if (a == "--via-server") viaServer = true;
            else if (file == null) file = a.Trim('"');
            else file += " " + a;
        }
        if (file == null && install)
        {
            Console.Error.WriteLine("Usage: DEVICE up <file.apk|file.ipa> [--serial S] [--bp <bugpunch device>] [--package ID] [--wait sec] [--no-install] [--no-launch]");
            Console.Error.WriteLine("       DEVICE up --no-install --package <id>     re-use what is already on the phone");
            return 1;
        }

        var dev = ResolveLocal(serial, file != null ? (file.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase) ? "iOS" : "Android") : null);
        if (dev == null) return 1;
        var session = DeviceSession.Current;
        DeviceSession.ActiveKey = dev.Serial;
        var state = session.ReadState();
        state["serial"] = dev.Serial; state["platform"] = dev.Platform; state["model"] = dev.Model;
        var total = Stopwatch.StartNew();
        Console.Error.WriteLine($"[up] {dev.Model} [{dev.Serial}] — session {session.Dir}");

        // 1. install
        string pkg = package ?? state.GetValueOrDefault("package");
        if (install)
        {
            _lastInstalledPkg = null;
            var iargs = new List<string> { file, "--serial", dev.Serial };
            if (package != null) { iargs.Add("--package"); iargs.Add(package); }
            int code = Install(iargs.ToArray());
            if (code != 0) { Console.Error.WriteLine("[up] install failed — stopping."); return code; }
            pkg = _lastInstalledPkg ?? pkg;
            state["build"] = Path.GetFullPath(file); state["installedAt"] = DateTime.Now.ToString("s");
        }
        if (pkg != null) state["package"] = pkg;
        session.WriteState(state);

        // 2. log capture, then launch (capture first so the first lines of the app are in the file)
        EnsureLogCapture(dev.Serial);
        if (launch)
        {
            if (pkg == null) { Console.Error.WriteLine("[up] can't launch: package/bundle id unknown — pass --package <id>."); }
            else
            {
                int code = dev.Platform == "Android"
                    ? (RequireAdb(out var adb) ? LaunchAndroid(adb, dev, pkg) : 1)
                    : LaunchIos(dev, pkg);
                if (code != 0) Console.Error.WriteLine("[up] launch failed — start the app by hand on the phone; continuing to wait for Bugpunch.");
            }
        }

        // 3a. Direct route first: the dev build's own API over USB (or --lan <ip>).
        bool direct = false;
        if (!viaServer)
        {
            if (lan != null)
            {
                string url = lan.StartsWith("http") ? lan : $"http://{lan}:{LocalIdePort}";
                var hello = ProbeDirect(url);
                if (hello != null) { state["direct"] = url; if (!hello.Id.StartsWith("http")) state["bpId"] = hello.Id; session.WriteState(state); direct = true; Console.Error.WriteLine($"[direct] {url} → {hello.Model} app {hello.AppVersion}"); }
                else Console.Error.WriteLine($"[direct] nothing at {url}");
            }
            else direct = LinkDirect(dev, session, state, Math.Min(waitSec, 45));
        }

        // 3b. Bugpunch server link — the fallback, and what release builds / remote phones use.
        var cfg = LoadConfig();
        if (direct)
        {
            Console.Error.WriteLine("[up] direct route up — server link skipped (DEVICE up --via-server to force it)");
        }
        else if (string.IsNullOrEmpty(cfg.Token))
        {
            Console.Error.WriteLine("[up] no Bugpunch token (BUGPUNCH auth <token>) — cable-only session; DEVICE run/hierarchy/… won't work until one is stored.");
        }
        else
        {
            string bp = bpName ?? state.GetValueOrDefault("bpId");
            // Without a name, match the phone: the SDK registers the model name (iPhone12,3 / Pixel 8), which is what the cable reports too.
            string modelHint = bp ?? (dev.Model.Contains('(') ? dev.Model.Substring(dev.Model.IndexOf('(') + 1).TrimEnd(')') : dev.Model);
            Console.Error.WriteLine($"[up] waiting up to {waitSec}s for Bugpunch device '{modelHint}' to come online…");
            try
            {
                string q = modelHint;
                int code = BpWaitOnline(ref q, waitSec);
                if (code == 0)
                {
                    state["bpId"] = q; session.WriteState(state);
                }
                else if (bp == null)
                {
                    // Model didn't match — fall back to "the only online internal device".
                    string any = null;
                    if (BpWaitOnline(ref any, 5) == 0) { state["bpId"] = any; session.WriteState(state); }
                    else Console.Error.WriteLine("[up] no Bugpunch link — the app must be signed in with an INTERNAL tester and the server must have /api/v1/devices. Link later with: DEVICE up --no-install --no-launch --bp <name>");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"[up] Bugpunch unreachable: {ex.Message}");
            }
        }

        DeviceSession.Open(dev.Serial, _cmdLine);
        DeviceSession.Add("session", session.StateFile);
        Console.WriteLine($"Session up on {dev.Model} [{dev.Serial}] in {total.Elapsed.TotalSeconds:F0}s — package={state.GetValueOrDefault("package") ?? "?"} route={(state.ContainsKey("direct") ? "direct " + state["direct"] : state.ContainsKey("bpId") ? "server " + state["bpId"] : "none")}");
        Console.WriteLine("Next: DEVICE status · DEVICE screenshot · DEVICE ui · DEVICE tap \"…\" · DEVICE run \"<C#>\" · DEVICE hierarchy · DEVICE shell · DEVICE script <file>");
        return 0;
    }
    [ThreadStatic] static string _lastInstalledPkg;

    static int Down(string[] args)
    {
        bool uninstall = args.Contains("--uninstall");
        string key = DeviceSession.ActiveKey;
        if (key == null) { Console.WriteLine("No active session."); return 0; }
        var dev = ResolveLocal(key, null);
        var session = DeviceSession.Current ?? DeviceSession.Open(key, _cmdLine);
        var state = session.ReadState();
        bool stopped = LogCapture.Stop(session);
        UnlinkDirect(dev, session, state);
        if (uninstall && dev != null && state.TryGetValue("package", out var pkg))
            Uninstall(new[] { pkg, "--serial", dev.Serial });
        DeviceSession.ActiveKey = null;
        Console.WriteLine($"Session down ({(stopped ? "log capture stopped" : "no capture running")}). Artifacts kept in {session.Dir}");
        return 0;
    }

    static int Status()
    {
        string key = DeviceSession.ActiveKey;
        if (key == null) { Console.WriteLine("No active session — DEVICE up <build> starts one. Plugged in:"); return ListDevices(); }
        var connected = EnumerateLocal(quiet: true).FirstOrDefault(d => d.Serial == key);
        var session = DeviceSession.Open(key, _cmdLine);
        var state = session.ReadState();
        Console.WriteLine($"device:    {state.GetValueOrDefault("model")} [{key}] {state.GetValueOrDefault("platform")} — {(connected == null ? "NOT CONNECTED" : connected.State)}");
        Console.WriteLine($"package:   {state.GetValueOrDefault("package") ?? "?"}   build: {state.GetValueOrDefault("build") ?? "?"}   installed: {state.GetValueOrDefault("installedAt") ?? "?"}");
        bool live = LogCapture.IsRunning(session, out var pid);
        string logf = LogCapture.LogFile(session, state.GetValueOrDefault("platform") == "iOS");
        Console.WriteLine($"log:       {(live ? $"capturing (pid {pid})" : "not capturing")} → {logf}{(File.Exists(logf) ? $" ({new FileInfo(logf).Length / 1024} KB)" : "")}");
        if (state.TryGetValue("direct", out var directUrl))
        {
            var h = ProbeDirect(directUrl, 1500);
            Console.WriteLine($"direct:    {directUrl} — {(h != null ? $"answering ({h.Model}, app {h.AppVersion})" : "NOT answering (DEVICE up --no-install re-links)")}");
        }
        string bp = state.GetValueOrDefault("bpId");
        if (bp == null) Console.WriteLine("bugpunch:  not linked");
        else
        {
            string online = "?";
            try
            {
                var cfg = LoadConfig();
                if (!string.IsNullOrEmpty(cfg.Token))
                {
                    var d = FetchDevices(cfg, all: true, projectId: null).FirstOrDefault(x => x.Id == bp);
                    online = d == null ? "unknown to server" : d.Online ? $"online ({d.Label}, app {d.AppVersion})" : "offline";
                }
            }
            catch (Exception ex) { online = "unreachable: " + ex.Message; }
            Console.WriteLine($"bugpunch:  {bp} — {online}");
        }
        int artifacts = Directory.GetFiles(session.Dir).Count(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{4}_"));
        Console.WriteLine($"artifacts: {artifacts} in {session.Dir}");
        return 0;
    }

    /// <summary>
    /// REPL over the same vocabulary as the CLI and the script files. Interactive at a terminal;
    /// also fine piped (`printf 'ui\ntap Allow\n' | clibridge4unity DEVICE shell`), which is how a
    /// model drives several steps without paying process start-up per step.
    /// </summary>
    static int Shell()
    {
        bool tty = !Console.IsInputRedirected;
        if (DeviceSession.ActiveKey == null) Console.Error.WriteLine("[shell] no active session — commands need --serial until you `up`.");
        if (tty) Console.Error.WriteLine("[shell] one command per line, same words as DEVICE/script; `bp …` for the game; `exit` to leave.");
        int last = 0;
        while (true)
        {
            if (tty) Console.Error.Write("device> ");
            string line = Console.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line is "exit" or "quit" or "q") break;
            var toks = DeviceScript.Tokenize(line);
            if (toks.Count == 0) continue;
            string head = toks[0].ToLowerInvariant();
            if (head == "device" || head == "adb") toks.RemoveAt(0);
            if (toks.Count == 0) continue;
            try { last = RunDevice(toks.ToArray()); }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); last = 1; }
            if (tty) Console.Error.WriteLine(last == 0 ? "" : $"[exit {last}]");
        }
        return last;
    }

    static void PrintDeviceUsage()
    {
        Console.Error.WriteLine("DEVICE — install and drive builds over the cable (adb / libimobiledevice)");
        Console.Error.WriteLine("  DEVICE up <file.apk|file.ipa> [--bp <name>] [--package ID] [--wait sec] [--no-install] [--no-launch]");
        Console.Error.WriteLine("                                                install → launch → log capture → link Bugpunch; then every command below needs no device args");
        Console.Error.WriteLine("        --lan <ip> talks to the dev build over the network instead of USB; --via-server forces the Bugpunch server route");
        Console.Error.WriteLine("  DEVICE discover [sec]                          list Development Builds advertising their local API on the LAN");
        Console.Error.WriteLine("  DEVICE status | down [--uninstall] | shell     session state · end it · REPL (also reads piped stdin)");
        Console.Error.WriteLine("  DEVICE run|hierarchy|info|perf|prefs|memsnap|get|post …   in-game via the linked Bugpunch device (DEVICE bp tap … for Unity-side input)");
        Console.Error.WriteLine("  DEVICE list                                   connected Android + iOS devices");
        Console.Error.WriteLine("  DEVICE install <file.apk|file.ipa> [--serial S] [--launch] [--package ID]");
        Console.Error.WriteLine("  DEVICE uninstall <package|bundleId> [--serial S]");
        Console.Error.WriteLine("  DEVICE launch <package|bundleId> [--serial S]");
        Console.Error.WriteLine("  DEVICE log [--serial S] [--lines N] [--follow] [--all]   Unity-tagged logcat / syslog");
        Console.Error.WriteLine("  DEVICE screenshot [label] [--out f.png]       whole screen incl. native views / dialogs");
        Console.Error.WriteLine("        iOS <=16: live via Developer Disk Image (auto-fetched, as Ipaapk does). iOS 17+: pulled from the camera roll — --roll = latest, --wait N = wait for one you take now");
        Console.Error.WriteLine("  DEVICE ui [filter] [--serial S]               Android native view tree (uiautomator): text, id, bounds, clickable");
        Console.Error.WriteLine("  DEVICE tap <x> <y> | tap \"<label|id>\"        Android: tap pixels, or the view whose text/desc/resource-id matches");
        Console.Error.WriteLine("  DEVICE swipe <x1> <y1> <x2> <y2> [ms]         Android pixels");
        Console.Error.WriteLine("  DEVICE type \"<text>\" | DEVICE key BACK|HOME|ENTER|<KEYCODE>");
        Console.Error.WriteLine("  DEVICE log --start | --stop | --status         background capture to the session dir (auto-started by launch/script)");
        Console.Error.WriteLine("  DEVICE script <steps.txt> [--bp <dev>] [--continue] [--shots]   run DEVICE + bp steps; markdown report with screenshots");
        Console.Error.WriteLine("  DEVICE tools                                  which adb / idevice* binaries are in use");
        Console.Error.WriteLine("  Artifacts: ~/.clibridge4unity/devices/<serial>/  — every command ends with an [artifacts] line naming what it wrote.");
        Console.Error.WriteLine("  Native input is Android-only over USB; iOS needs WebDriverAgent (not libimobiledevice). Unity-side input on both: BUGPUNCH.");
        Console.Error.WriteLine("  --serial matches an adb serial or an iOS UDID (prefix ok). Omit it when one device is plugged in.");
    }

    // ─── Tool discovery ────────────────────────────────────────────────

    /// <summary>
    /// Which adb.exe to run — and the order matters more than it looks.
    ///
    /// There is one adb *server* per machine (port 5037) and every adb *client* checks the
    /// server's version against its own; on a mismatch the client silently kills the server and
    /// restarts it with its own build. That is what breaks Ipaapk (and Unity's own Android
    /// deploy) mid-session: a second tool with a different adb build kills their server, their
    /// device list goes empty and the logcat they were tailing stops. So:
    ///
    ///   1. CLIBRIDGE_ADB — explicit override.
    ///   2. The binary behind the adb server that is ALREADY RUNNING. Same build, same server,
    ///      nothing restarts. This is the case that matters when Ipaapk / Unity are open.
    ///   3. Ipaapk's own adb (its adb_override.txt, then its install's tools/). If no server is
    ///      up yet, starting it from Ipaapk's build means Ipaapk won't restart it later either.
    ///   4. PATH, then ANDROID_HOME / Android Studio / every Unity Hub editor's bundled SDK.
    ///
    /// Nothing here ever issues `adb kill-server`.
    /// </summary>
    static string FindAdb()
    {
        if (_adbCache != null) return _adbCache;
        string env = Environment.GetEnvironmentVariable("CLIBRIDGE_ADB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return _adbCache = env;

        string running = RunningAdbServerExe();
        if (running != null) return _adbCache = running;

        foreach (var c in IpaapkAdbCandidates())
            if (File.Exists(c)) return _adbCache = c;

        string onPath = FindOnPath("adb.exe") ?? FindOnPath("adb");
        if (onPath != null) return _adbCache = onPath;

        foreach (var sdk in AndroidSdkRoots())
        {
            string c = Path.Combine(sdk, "platform-tools", "adb.exe");
            if (File.Exists(c)) return _adbCache = c;
        }
        return null;
    }
    static string _adbCache;

    /// <summary>The exe of the live adb server process (the `fork-server server` child), if any.</summary>
    static string RunningAdbServerExe()
    {
        try
        {
            bool any = false;
            foreach (var p in Process.GetProcessesByName("adb"))
            {
                any = true;
                try
                {
                    string exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;
                }
                catch { /* access denied / bitness — fall through to CIM below */ }
                finally { p.Dispose(); }
            }
            if (!any) return null;
            // MainModule was unreadable (e.g. server started elevated). CIM can still read the path.
            var (code, stdout, _) = Exec("powershell.exe",
                "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter \\\"Name='adb.exe'\\\" | Select-Object -First 1 -ExpandProperty ExecutablePath)\"", 8000);
            string path = stdout.Trim();
            if (code == 0 && path.Length > 0 && File.Exists(path)) return path;
        }
        catch { }
        return null;
    }

    /// <summary>Ipaapk (the in-house ipa/apk installer) keeps adb + libimobiledevice under its Squirrel install's tools/.</summary>
    static IEnumerable<string> IpaapkToolDirs()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = Path.Combine(local, "Ipaapk");
        if (Directory.Exists(root))
        {
            string cur = Path.Combine(root, "current", "tools");
            if (Directory.Exists(cur)) yield return cur;
            // Squirrel keeps app-<version>/ siblings; newest first in case `current` is a stale junction.
            foreach (var d in Directory.GetDirectories(root, "app-*").OrderByDescending(d => d, StringComparer.Ordinal))
            {
                string t = Path.Combine(d, "tools");
                if (Directory.Exists(t)) yield return t;
            }
        }
        string dev = Environment.GetEnvironmentVariable("IPAAPK_TOOLS");
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev)) yield return dev;
    }

    static IEnumerable<string> IpaapkAdbCandidates()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string overrideFile = Path.Combine(local, "Ipaapk", "adb_override.txt");
        string o = null;
        try { if (File.Exists(overrideFile)) o = File.ReadAllText(overrideFile).Trim(); } catch { }
        if (!string.IsNullOrEmpty(o))
        {
            if (!o.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) o += ".exe";
            yield return o;
        }
        foreach (var t in IpaapkToolDirs()) yield return Path.Combine(t, "adb.exe");
    }

    /// <summary>aapt (for the package name inside an .apk) lives in build-tools/&lt;ver&gt;/ beside adb.</summary>
    static string FindAapt()
    {
        foreach (var t in IpaapkToolDirs())
        {
            string c = Path.Combine(t, "aapt.exe");
            if (File.Exists(c)) return c;
        }
        string onPath = FindOnPath("aapt.exe") ?? FindOnPath("aapt2.exe");
        if (onPath != null) return onPath;
        foreach (var sdk in AndroidSdkRoots())
        {
            string bt = Path.Combine(sdk, "build-tools");
            if (!Directory.Exists(bt)) continue;
            foreach (var ver in Directory.GetDirectories(bt).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                string a = Path.Combine(ver, "aapt.exe");
                if (File.Exists(a)) return a;
                a = Path.Combine(ver, "aapt2.exe");
                if (File.Exists(a)) return a;
            }
        }
        return null;
    }

    static IEnumerable<string> AndroidSdkRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            string p = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && seen.Add(p)) yield return p;
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string studio = Path.Combine(local, "Android", "Sdk");
        if (Directory.Exists(studio) && seen.Add(studio)) yield return studio;

        // Unity Hub editors bundle an SDK per version; newest editor first.
        foreach (var hubRoot in new[]
        {
            Environment.GetEnvironmentVariable("UNITY_HUB_EDITORS"),
            @"C:\Program Files\Unity\Hub\Editor",
        })
        {
            if (string.IsNullOrEmpty(hubRoot) || !Directory.Exists(hubRoot)) continue;
            foreach (var ed in Directory.GetDirectories(hubRoot).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                string sdk = Path.Combine(ed, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "SDK");
                if (Directory.Exists(sdk) && seen.Add(sdk)) yield return sdk;
            }
        }
    }

    /// <summary>
    /// libimobiledevice ships no Windows installer; people end up with the binaries from scoop,
    /// from a build, or copied into a tools folder. Accept an explicit dir first, then Ipaapk's
    /// bundled set (a complete, known-working build with its DLLs beside it — the exes are not
    /// relocatable on their own, which is why we run them in place rather than copying), then
    /// PATH, then the usual drop locations.
    /// </summary>
    static string FindIDeviceTool(string exe)
    {
        string dir = Environment.GetEnvironmentVariable("CLIBRIDGE_IMOBILE_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            string c = Path.Combine(dir, exe + ".exe");
            if (File.Exists(c)) return c;
        }
        foreach (var t in IpaapkToolDirs())
        {
            string c = Path.Combine(t, exe + ".exe");
            if (File.Exists(c)) return c;
        }
        string onPath = FindOnPath(exe + ".exe") ?? FindOnPath(exe);
        if (onPath != null) return onPath;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var d in new[]
        {
            Path.Combine(home, ".clibridge4unity", "tools", "libimobiledevice"),
            Path.Combine(home, "scoop", "apps", "libimobiledevice", "current"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "libimobiledevice"),
            @"C:\Program Files\libimobiledevice",
        })
        {
            string c = Path.Combine(d, exe + ".exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    static string FindOnPath(string exe)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string c = Path.Combine(dir.Trim(), exe);
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    static bool RequireAdb(out string adb)
    {
        adb = FindAdb();
        if (adb != null) return true;
        Console.Error.WriteLine("Error: adb not found. Looked at a running adb server, Ipaapk's tools, PATH, ANDROID_HOME / ANDROID_SDK_ROOT,");
        Console.Error.WriteLine("       %LOCALAPPDATA%\\Android\\Sdk and every Unity Hub editor's bundled AndroidPlayer SDK. CLIBRIDGE_ADB=<adb.exe> overrides.");
        return false;
    }

    static bool RequireIDevice(string exe, out string path)
    {
        path = FindIDeviceTool(exe);
        if (path != null) return true;
        Console.Error.WriteLine($"Error: {exe}.exe (libimobiledevice) not found.");
        Console.Error.WriteLine("       Easiest: install Ipaapk (its tools/ folder is picked up automatically), or scoop install libimobiledevice,");
        Console.Error.WriteLine("       or point CLIBRIDGE_IMOBILE_DIR at a folder holding idevice_id.exe, ideviceinstaller.exe, idevicesyslog.exe + their DLLs.");
        Console.Error.WriteLine("       iTunes / Apple Mobile Device Support must be installed for the USB driver.");
        return false;
    }

    static int ShowTools()
    {
        string running = RunningAdbServerExe();
        Console.WriteLine($"adb:               {FindAdb() ?? "(not found)"}");
        Console.WriteLine($"adb server:        {(running != null ? "running — " + running : "not running (the first adb call starts one)")}");
        Console.WriteLine($"aapt:              {FindAapt() ?? "(not found — Android package name will be inferred by diffing installed packages)"}");
        foreach (var t in new[] { "idevice_id", "ideviceinstaller", "idevicesyslog", "ideviceinfo", "idevicedebug" })
            Console.WriteLine($"{t + ":",-19}{FindIDeviceTool(t) ?? "(not found)"}");
        return 0;
    }

    // ─── Device enumeration ────────────────────────────────────────────

    sealed class LocalDevice
    {
        public string Platform;   // "Android" | "iOS"
        public string Serial;     // adb serial or iOS UDID
        public string Model;
        public string State;      // device | unauthorized | offline | …
        public override string ToString() => $"{Platform,-10}{Serial,-30}{Model,-34}{State}";
    }

    static List<LocalDevice> EnumerateLocal(bool quiet = false)
    {
        var list = new List<LocalDevice>();

        string adb = FindAdb();
        if (adb != null)
        {
            var (code, stdout, _) = Exec(adb, "devices -l", 15000);
            if (code == 0)
            {
                foreach (var line in stdout.Split('\n').Skip(1))
                {
                    string l = line.Trim();
                    if (l.Length == 0) continue;
                    var parts = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    string model = parts.FirstOrDefault(p => p.StartsWith("model:"))?.Substring(6)?.Replace('_', ' ') ?? "";
                    list.Add(new LocalDevice { Platform = "Android", Serial = parts[0], State = parts[1], Model = model });
                }
            }
        }
        else if (!quiet) Console.Error.WriteLine("[DEVICE] adb not found — Android devices not listed (CLIBRIDGE_ADB=<adb.exe> to override)");

        string ideviceId = FindIDeviceTool("idevice_id");
        if (ideviceId != null)
        {
            var (code, stdout, _) = Exec(ideviceId, "-l", 15000);
            if (code == 0)
            {
                string info = FindIDeviceTool("ideviceinfo");
                foreach (var raw in stdout.Split('\n'))
                {
                    string udid = raw.Trim();
                    if (udid.Length < 8) continue;
                    string model = "";
                    if (info != null)
                    {
                        var (c2, out2, _) = Exec(info, $"-u {udid} -k ProductType", 10000);
                        if (c2 == 0) model = out2.Trim();
                        var (c3, out3, _) = Exec(info, $"-u {udid} -k DeviceName", 10000);
                        if (c3 == 0 && out3.Trim().Length > 0) model = $"{out3.Trim()} ({model})";
                    }
                    list.Add(new LocalDevice { Platform = "iOS", Serial = udid, State = "device", Model = model });
                }
            }
        }
        else if (!quiet) Console.Error.WriteLine("[DEVICE] idevice_id not found — iOS devices not listed (scoop install libimobiledevice, or CLIBRIDGE_IMOBILE_DIR)");

        return list;
    }

    static int ListDevices()
    {
        var devices = EnumerateLocal();
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices connected.");
            Console.WriteLine("  Android: enable USB debugging and accept the RSA prompt on the phone (adb devices shows 'unauthorized' until then).");
            Console.WriteLine("  iOS:     unlock the phone and tap Trust; iTunes/Apple Mobile Device Support must be installed.");
            return 0;
        }
        Console.WriteLine($"{"PLATFORM",-10}{"SERIAL / UDID",-30}{"MODEL",-34}STATE");
        foreach (var d in devices) Console.WriteLine(d);
        var bad = devices.Where(d => d.State != "device").ToList();
        if (bad.Count > 0)
            Console.WriteLine($"\n{bad.Count} device(s) not ready — 'unauthorized' means accept the USB-debugging prompt on the phone.");
        return 0;
    }

    /// <summary>
    /// Pick the target. An explicit --serial wins (prefix match on serial/UDID). Otherwise the
    /// artifact decides the platform and there must be exactly one ready device of that kind —
    /// silently picking one of two phones is how a build lands on the wrong tester's device.
    /// </summary>
    static LocalDevice ResolveLocal(string serial, string platform)
    {
        var dev = ResolveLocalCore(serial, platform);
        if (dev != null) DeviceSession.Open(dev.Serial, _cmdLine ?? "DEVICE");
        return dev;
    }

    /// <summary>Session key for a script run: the resolved cable device's serial (prints the usual errors if none).</summary>
    public static string ResolveLocalKey(string serial) => ResolveLocalCore(serial, null)?.Serial;

    static LocalDevice ResolveLocalCore(string serial, string platform)
    {
        var all = EnumerateLocal(quiet: true);
        // No --serial: the session `DEVICE up` made active wins, if it is still plugged in.
        if (string.IsNullOrEmpty(serial))
        {
            string active = DeviceSession.ActiveKey;
            if (active != null && all.Any(d => d.Serial == active && (platform == null || d.Platform == platform))) serial = active;
        }
        var candidates = platform == null ? all : all.Where(d => d.Platform == platform).ToList();

        if (!string.IsNullOrEmpty(serial))
        {
            var hit = candidates.Where(d => d.Serial.StartsWith(serial, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 1) return hit[0];
            Console.Error.WriteLine(hit.Count == 0
                ? $"Error: no {(platform ?? "connected")} device matches --serial {serial}."
                : $"Error: --serial {serial} is ambiguous ({hit.Count} matches).");
            foreach (var d in candidates) Console.Error.WriteLine("  " + d);
            return null;
        }

        var ready = candidates.Where(d => d.State == "device").ToList();
        if (ready.Count == 1) return ready[0];
        if (ready.Count == 0)
        {
            Console.Error.WriteLine($"Error: no ready {(platform ?? "")} device connected.");
            foreach (var d in candidates) Console.Error.WriteLine("  " + d);
            if (candidates.Any(d => d.State == "unauthorized"))
                Console.Error.WriteLine("  → accept the USB-debugging prompt on the phone, then retry.");
            return null;
        }
        Console.Error.WriteLine($"Error: {ready.Count} {(platform ?? "")} devices connected — pick one with --serial:");
        foreach (var d in ready) Console.Error.WriteLine("  " + d);
        return null;
    }

    // ─── install / uninstall / launch / log ────────────────────────────

    static int Install(string[] args)
    {
        string file = null, serial = null, package = null;
        bool launch = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--serial" || a == "-s") { if (i + 1 < args.Length) serial = args[++i]; }
            else if (a == "--launch" || a == "--run") launch = true;
            else if (a == "--package" || a == "--bundle") { if (i + 1 < args.Length) package = args[++i]; }
            else if (file == null) file = a.Trim('"');
            else file += " " + a;   // unquoted path with spaces — Main joined it, we rejoin it
        }
        if (file == null) { Console.Error.WriteLine("Usage: DEVICE install <file.apk|file.ipa> [--serial S] [--launch] [--package ID]"); return 1; }
        file = Path.GetFullPath(file.Trim('"'));
        if (!File.Exists(file)) { Console.Error.WriteLine($"Error: file not found: {file}"); return 1; }

        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".aab")
        {
            Console.Error.WriteLine("Error: .aab bundles can't be sideloaded directly — build an .apk (Build Settings → uncheck 'Build App Bundle'),");
            Console.Error.WriteLine("       or use bundletool build-apks + install-apks.");
            return 1;
        }
        if (ext != ".apk" && ext != ".ipa")
        {
            Console.Error.WriteLine($"Error: expected .apk or .ipa, got {ext}");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        long mb = new FileInfo(file).Length / (1024 * 1024);

        if (ext == ".apk")
        {
            if (!RequireAdb(out var adb)) return 1;
            var dev = ResolveLocal(serial, "Android");
            if (dev == null) return 1;

            string pkg = package ?? ApkPackageName(file);
            HashSet<string> before = pkg == null ? AdbPackages(adb, dev.Serial) : null;

            Console.Error.WriteLine($"[DEVICE] Installing {Path.GetFileName(file)} ({mb} MB) → {dev.Model} [{dev.Serial}] …");
            // -r replace, -d allow downgrade, -g grant runtime permissions (a test build shouldn't
            // stall on a permission dialog the first time it captures a screenshot).
            var (code, stdout, stderr) = Exec(adb, $"-s {dev.Serial} install -r -d -g \"{file}\"", 10 * 60 * 1000, stream: true);
            if (code != 0 || (stdout + stderr).Contains("Failure"))
            {
                Console.Error.WriteLine($"Error: install failed (exit {code}).");
                string all = stdout + stderr;
                if (all.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
                    Console.Error.WriteLine("  → signed with a different key than the installed copy. Uninstall first: DEVICE uninstall <package>");
                else if (all.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE"))
                    Console.Error.WriteLine("  → device is out of space.");
                else if (all.Contains("INSTALL_FAILED_NO_MATCHING_ABIS"))
                    Console.Error.WriteLine("  → APK has no build for this device's CPU (check Player Settings → Target Architectures).");
                return 1;
            }
            if (pkg == null && before != null)
            {
                var after = AdbPackages(adb, dev.Serial);
                var added = after.Except(before).ToList();
                if (added.Count == 1) pkg = added[0];
            }
            _lastInstalledPkg = pkg;
            Console.WriteLine($"Installed {Path.GetFileName(file)} on {dev.Model} [{dev.Serial}] in {sw.Elapsed.TotalSeconds:F1}s" + (pkg != null ? $"  package={pkg}" : ""));
            if (launch)
            {
                if (pkg == null)
                {
                    Console.Error.WriteLine("Could not determine the package name to launch (no aapt, and the package was already installed). Pass --package <id>.");
                    return 1;
                }
                return LaunchAndroid(adb, dev, pkg);
            }
            return 0;
        }
        else
        {
            if (!RequireIDevice("ideviceinstaller", out var installer)) return 1;
            if (!RequireIDevice("idevice_id", out _)) return 1;
            var dev = ResolveLocal(serial, "iOS");
            if (dev == null) return 1;

            string bundle = package ?? IpaBundleId(file);
            HashSet<string> before = bundle == null ? IosBundles(installer, dev.Serial) : null;
            // Binary Info.plist and the app already installed (upgrade): the diff will be empty.
            // The .app folder name is the display name ideviceinstaller -l prints — match on that.
            if (bundle == null && before != null) bundle = IosBundleByAppName(installer, dev.Serial, IpaAppName(file));

            Console.Error.WriteLine($"[DEVICE] Installing {Path.GetFileName(file)} ({mb} MB) → {dev.Model} [{dev.Serial}] …");
            var (code, stdout, stderr) = Exec(installer, $"-u {dev.Serial} -i \"{file}\"", 15 * 60 * 1000, stream: true);
            string all = stdout + stderr;
            if (code != 0 || all.Contains("ERROR") || all.Contains("Install: Error"))
            {
                Console.Error.WriteLine($"Error: install failed (exit {code}).");
                if (all.Contains("ApplicationVerificationFailed") || all.Contains("0xe8008015") || all.Contains("provision"))
                    Console.Error.WriteLine("  → the provisioning profile doesn't include this device's UDID, or the signing identity isn't trusted on the phone.");
                else if (all.Contains("DeviceOSVersionTooLow"))
                    Console.Error.WriteLine("  → the build's minimum iOS version is newer than the device.");
                else if (all.Contains("Could not connect") || all.Contains("lockdownd"))
                    Console.Error.WriteLine("  → unlock the phone and tap Trust; check Apple Mobile Device Service is running.");
                return 1;
            }
            if (bundle == null && before != null)
            {
                var after = IosBundles(installer, dev.Serial);
                var added = after.Except(before).ToList();
                if (added.Count == 1) bundle = added[0];
            }
            _lastInstalledPkg = bundle;
            Console.WriteLine($"Installed {Path.GetFileName(file)} on {dev.Model} [{dev.Serial}] in {sw.Elapsed.TotalSeconds:F1}s" + (bundle != null ? $"  bundle={bundle}" : ""));
            if (launch)
            {
                if (bundle == null)
                {
                    Console.Error.WriteLine("Could not determine the bundle id to launch. Pass --package <bundle.id>.");
                    return 1;
                }
                return LaunchIos(dev, bundle);
            }
            return 0;
        }
    }

    static int Uninstall(string[] args)
    {
        string id = null, serial = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else id ??= args[i];
        }
        if (id == null) { Console.Error.WriteLine("Usage: DEVICE uninstall <package|bundleId> [--serial S]"); return 1; }

        var dev = ResolveLocal(serial, null);
        if (dev == null) return 1;
        if (dev.Platform == "Android")
        {
            if (!RequireAdb(out var adb)) return 1;
            var (code, stdout, stderr) = Exec(adb, $"-s {dev.Serial} uninstall {id}", 60000);
            Console.Write(stdout);
            if (code != 0 || stdout.Contains("Failure")) { Console.Error.WriteLine(stderr); return 1; }
            Console.WriteLine($"Uninstalled {id} from {dev.Model}");
            return 0;
        }
        else
        {
            if (!RequireIDevice("ideviceinstaller", out var installer)) return 1;
            var (code, stdout, stderr) = Exec(installer, $"-u {dev.Serial} -U {id}", 120000);
            Console.Write(stdout);
            if (code != 0) { Console.Error.WriteLine(stderr); return 1; }
            Console.WriteLine($"Uninstalled {id} from {dev.Model}");
            return 0;
        }
    }

    static int Launch(string[] args)
    {
        string id = null, serial = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else id ??= args[i];
        }
        if (id == null) { Console.Error.WriteLine("Usage: DEVICE launch <package|bundleId> [--serial S]"); return 1; }
        var dev = ResolveLocal(serial, null);
        if (dev == null) return 1;
        if (dev.Platform == "Android")
        {
            if (!RequireAdb(out var adb)) return 1;
            return LaunchAndroid(adb, dev, id);
        }
        return LaunchIos(dev, id);
    }

    /// <summary>Start the background log capture for the device a launch/script is about to exercise.</summary>
    public static void EnsureLogCapture(string serial)
    {
        var dev = ResolveLocalCore(serial, null);
        if (dev == null) return;
        var session = DeviceSession.Open(dev.Serial, _cmdLine ?? "DEVICE");
        if (LogCapture.IsRunning(session, out _)) return;
        if (dev.Platform == "Android")
        {
            string adb = FindAdb(); if (adb == null) return;
            // -T 1: from now on, not the whole ring; no -c — other readers share that buffer.
            string log = LogCapture.Start(session, adb, $"-s {dev.Serial} logcat -v time -T 1", ios: false);
            Console.Error.WriteLine($"[DEVICE] logcat capture started → {log}");
        }
        else
        {
            string syslog = FindIDeviceTool("idevicesyslog"); if (syslog == null) return;
            string log = LogCapture.Start(session, syslog, $"-u {dev.Serial}", ios: true);
            Console.Error.WriteLine($"[DEVICE] syslog capture started → {log}");
        }
    }

    static int LaunchAndroid(string adb, LocalDevice dev, string pkg)
    {
        EnsureLogCapture(dev.Serial);
        // monkey resolves the launcher activity itself — no need to know Unity's activity name
        // (UnityPlayerActivity vs UnityPlayerGameActivity changed across versions).
        var (code, stdout, stderr) = Exec(adb, $"-s {dev.Serial} shell monkey -p {pkg} -c android.intent.category.LAUNCHER 1", 30000);
        if (code != 0 || (stdout + stderr).Contains("No activities found"))
        {
            Console.Error.WriteLine($"Error: could not launch {pkg} — {(stderr + stdout).Trim()}");
            return 1;
        }
        Console.WriteLine($"Launched {pkg} on {dev.Model}");
        return 0;
    }

    /// <summary>
    /// iOS has no "start app" over USB without a debugger: idevicedebug attaches via the developer
    /// disk image and the app dies when it detaches. So it's spawned detached and left running —
    /// killing it later kills the app, which is why we say so.
    /// </summary>
    static int LaunchIos(LocalDevice dev, string bundle)
    {
        if (!RequireIDevice("idevicedebug", out var dbg)) return 1;
        EnsureLogCapture(dev.Serial);
        if (!TryMountDeveloperImage(dev))
        {
            Console.Error.WriteLine($"Error: can't launch {bundle} over USB on this iOS — tap the icon on the phone.");
            return 1;
        }
        try
        {
            var psi = new ProcessStartInfo(dbg, $"-u {dev.Serial} run {bundle}")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var p = Process.Start(psi);
            // Give it a moment to fail fast (not installed / no developer image) before we claim success.
            if (p.WaitForExit(2500))
            {
                string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                Console.Error.WriteLine($"Error: idevicedebug exited {p.ExitCode}: {err.Trim()}");
                if (err.Contains("Developer") || err.Contains("mount"))
                    Console.Error.WriteLine("  → the Developer Disk Image isn't mounted; enable Developer Mode on the phone and run it from Xcode once.");
                return 1;
            }
            Console.WriteLine($"Launched {bundle} on {dev.Model} (idevicedebug pid {p.Id} stays attached — closing it stops the app; tapping the icon on the phone is the alternative)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static int Log(string[] args)
    {
        string serial = null; int lines = 200; bool follow = false, all = false, start = false, stop = false, status = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if ((a == "--serial" || a == "-s") && i + 1 < args.Length) serial = args[++i];
            else if ((a == "--lines" || a == "-n") && i + 1 < args.Length) int.TryParse(args[++i], out lines);
            else if (a == "--follow" || a == "-f") follow = true;
            else if (a == "--all") all = true;
            else if (a == "--start" || a == "start") start = true;
            else if (a == "--stop" || a == "stop") stop = true;
            else if (a == "--status" || a == "status") status = true;
        }
        var dev = ResolveLocal(serial, null);
        if (dev == null) return 1;
        var session = DeviceSession.Current;
        bool ios = dev.Platform != "Android";

        if (stop)
        {
            Console.WriteLine(LogCapture.Stop(session) ? "Log capture stopped." : "No log capture was running.");
            return 0;
        }
        if (start)
        {
            EnsureLogCapture(dev.Serial);
            Console.WriteLine($"Capturing to {LogCapture.LogFile(session, ios)} — DEVICE log shows the tail, DEVICE log --stop ends it.");
            return 0;
        }
        if (status)
        {
            bool live = LogCapture.IsRunning(session, out var pid);
            string f = LogCapture.LogFile(session, ios);
            Console.WriteLine(live ? $"capturing (pid {pid}) → {f} ({(File.Exists(f) ? new FileInfo(f).Length / 1024 : 0)} KB)" : "not capturing" + (File.Exists(f) ? $" (last file: {f})" : ""));
            return 0;
        }
        // A running capture already has the recent history — read it instead of asking the device again.
        if (!follow && LogCapture.IsRunning(session, out _))
        {
            var tail = LogCapture.Tail(session, ios, lines).ToList();
            var filtered = all ? tail : tail.Where(l => l.Contains("Unity") || l.Contains("ugpunch") || l.Contains("AndroidRuntime") || l.Contains("DEBUG") || l.Contains("CRASH")).ToList();
            foreach (var l in filtered) Console.WriteLine(l.TrimEnd());
            Console.Error.WriteLine($"[DEVICE] {filtered.Count} of the last {tail.Count} captured lines (from the running capture; --all for unfiltered)");
            return 0;
        }

        if (dev.Platform == "Android")
        {
            if (!RequireAdb(out var adb)) return 1;
            // Unity routes Debug.Log through the "Unity" tag; crashes land under DEBUG / AndroidRuntime.
            string filter = all ? "" : " -s Unity:V DEBUG:V AndroidRuntime:E CRASH:V bugpunch:V Bugpunch:V";
            if (follow) return Stream(adb, $"-s {dev.Serial} logcat -v time{filter}");
            var (code, stdout, stderr) = Exec(adb, $"-s {dev.Serial} logcat -v time -d -t {lines}{filter}", 30000);
            if (code != 0) { Console.Error.WriteLine($"Error: logcat failed: {stderr.Trim()}"); return 1; }
            Console.Write(stdout);
            string f = session.Artifact("logcat", null, ".txt"); File.WriteAllText(f, stdout);
            DeviceSession.Add("logcat", f, $"{stdout.Count(c => c == '\n')} lines");
            return 0;
        }
        else
        {
            if (!RequireIDevice("idevicesyslog", out var syslog)) return 1;
            // idevicesyslog has no "last N" — it's a live stream. Without --follow we read for a
            // few seconds and stop, which is what "show me the log" means over a cable anyway.
            string match = all ? "" : " -m Unity -m Bugpunch";
            if (follow) return Stream(syslog, $"-u {dev.Serial}{match}");
            var (_, stdout, _) = Exec(syslog, $"-u {dev.Serial}{match}", 4000, killOnTimeout: true);
            var tail = stdout.Split('\n');
            var shown = tail.Skip(Math.Max(0, tail.Length - lines)).Select(l => l.TrimEnd()).ToList();
            foreach (var l in shown) Console.WriteLine(l);
            string f = session.Artifact("syslog", null, ".txt"); File.WriteAllLines(f, shown);
            DeviceSession.Add("syslog", f, $"{shown.Count} lines");
            return 0;
        }
    }

    // ─── Native screen: see + tap what Unity can't ────────────────────
    //
    // BUGPUNCH's capture/tap live inside the Unity surface. Everything native — the SDK's own
    // consent sheet and crash overlay, OS permission dialogs, IAP sheets, the keyboard — is
    // invisible to it and doesn't receive its taps. Over the cable Android exposes the real
    // screen and input stack (screencap / input / uiautomator), so this is the only way to
    // get past a native dialog unattended. iOS gives the picture (idevicescreenshot) but not
    // the touch: that needs WebDriverAgent driven from a Mac, which libimobiledevice cannot do.

    static string ShotPath(LocalDevice dev, string outPath, string label)
        => outPath ?? DeviceSession.Current.Artifact("screenshot", label, ".png");

    /// <summary>Screenshot for the script runner's --shots: no console output beyond the path; null on failure.</summary>
    public static string QuietScreenshot(string serial, string label)
    {
        var realOut = Console.Out; var realErr = Console.Error;
        var sw = new StringWriter();
        Console.SetOut(sw); Console.SetError(sw);
        int code;
        try { code = Screenshot(serial == null ? new[] { "--label", label } : new[] { "--serial", serial, "--label", label }); }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
        if (code != 0) return null;
        string first = sw.ToString().Split('\n')[0].Trim();
        int cut = first.IndexOf("  (");
        return cut > 0 ? first.Substring(0, cut) : first;
    }

    static int Screenshot(string[] args)
    {
        string serial = null, outPath = null, label = null;
        int rollWait = 0; bool fromRoll = false;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else if ((args[i] == "--out" || args[i] == "-o") && i + 1 < args.Length) outPath = args[++i].Trim('"');
            else if (args[i] == "--label" && i + 1 < args.Length) label = args[++i];
            else if (args[i] == "--wait" && i + 1 < args.Length) int.TryParse(args[++i], out rollWait);
            else if (args[i] == "--roll" || args[i] == "--from-roll") fromRoll = true;
            else if (!args[i].StartsWith("-")) label ??= args[i].Trim('"');
        }
        var dev = ResolveLocal(serial, null);
        if (dev == null) return 1;
        outPath = Path.GetFullPath(ShotPath(dev, outPath, label));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        var sw = Stopwatch.StartNew();

        if (dev.Platform == "Android")
        {
            if (!RequireAdb(out var adb)) return 1;
            // exec-out: raw bytes, no CRLF translation that `shell` applies on Windows.
            var (code, bytes, err) = ExecBytes(adb, $"-s {dev.Serial} exec-out screencap -p", 30000);
            if (code != 0 || bytes.Length < 100 || bytes[1] != (byte)'P')
            {
                Console.Error.WriteLine($"Error: screencap failed (exit {code}): {err.Trim()}");
                return 1;
            }
            File.WriteAllBytes(outPath, bytes);
        }
        else
        {
            if (fromRoll) return PullRollScreenshot(dev, outPath, rollWait);
            if (!RequireIDevice("idevicescreenshot", out var shot)) return 1;
            TryMountDeveloperImage(dev, quiet: true);
            var (code, stdout, stderr) = Exec(shot, $"-u {dev.Serial} \"{outPath}\"", 30000);
            if (code != 0 || !File.Exists(outPath))
            {
                string all = (stdout + stderr).Trim();
                if (all.Contains("screenshotr") || all.Contains("Developer") || all.Contains("mount"))
                {
                    // iOS 17+: the DDI is personalized and libimobiledevice can't mount it, so the
                    // live path is gone. AFC still works — so do what Ipaapk does and take the
                    // screenshot the phone itself made.
                    Console.Error.WriteLine("[DEVICE] live screenshot unavailable (Developer Disk Image) — falling back to the phone's own screenshot via the camera roll.");
                    return PullRollScreenshot(dev, outPath, rollWait > 0 ? rollWait : 20);
                }
                Console.Error.WriteLine($"Error: idevicescreenshot failed (exit {code}): {all}");
                return 1;
            }
            // idevicescreenshot writes TIFF on older builds regardless of extension; say what it is.
            var head = new byte[4];
            using (var f = File.OpenRead(outPath)) f.Read(head, 0, 4);
            if (head[0] == 'M' && head[1] == 'M' || head[0] == 'I' && head[1] == 'I')
                Console.Error.WriteLine("[DEVICE] note: file is TIFF (older idevicescreenshot) — rename to .tiff if a viewer refuses it");
        }
        Console.WriteLine($"{outPath}  ({new FileInfo(outPath).Length / 1024} KB, {sw.ElapsedMilliseconds} ms, {dev.Model})");
        DeviceSession.Add("screenshot", outPath);
        return 0;
    }

    /// <summary>
    /// Pull the newest screenshot from the iOS camera roll over AFC (Ipaapk's `afcshots` helper —
    /// it lives beside the other tools). No Developer Disk Image needed, which is why it is the
    /// only screenshot path left on iOS 17+. With a wait, polls for a screenshot NEWER than "now",
    /// i.e. one the tester takes on request (Side + Volume Up).
    /// </summary>
    static int PullRollScreenshot(LocalDevice dev, string outPath, int waitSec)
    {
        string afc = null;
        foreach (var t in IpaapkToolDirs())
        {
            string c = Path.Combine(t, "afcshots", "afcshots.exe");
            if (File.Exists(c)) { afc = c; break; }
        }
        afc ??= FindOnPath("afcshots.exe");
        if (afc == null)
        {
            Console.Error.WriteLine("Error: afcshots.exe not found (ships in Ipaapk's tools/afcshots). Without it there is no iOS screenshot path on iOS 17+.");
            return 1;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "clibridge4unity", "afcshots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        long since = waitSec > 0 ? DateTimeOffset.Now.ToUnixTimeMilliseconds() - 2000 : 0;
        if (waitSec > 0) Console.Error.WriteLine($"[DEVICE] take a screenshot on the phone now (Side + Volume Up) — waiting up to {waitSec}s…");
        var sw = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var (code, stdout, stderr) = Exec(afc, $"\"{dev.Serial}\" \"{tmp}\" --max 3", 60000);
                if (code != 0) { Console.Error.WriteLine($"Error: afcshots failed: {(stderr + stdout).Trim()}"); return 1; }
                // lines: devicePath \t unixMs \t localFile, newest first
                var rows = stdout.Split('\n').Select(l => l.Trim().Split('\t')).Where(p => p.Length >= 3 && File.Exists(p[2])).ToList();
                var pick = rows.FirstOrDefault(p => long.TryParse(p[1], out var ms) && ms >= since);
                if (pick != null)
                {
                    File.Copy(pick[2], outPath, true);
                    long.TryParse(pick[1], out var ms);
                    Console.WriteLine($"{outPath}  ({new FileInfo(outPath).Length / 1024} KB, camera roll {pick[0]}, taken {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:HH:mm:ss}, {dev.Model})");
                    DeviceSession.Add("screenshot", outPath, waitSec > 0 ? "taken on phone" : "latest in camera roll");
                    return 0;
                }
                if (sw.Elapsed.TotalSeconds >= waitSec) break;
                Thread.Sleep(1500);
            }
            Console.Error.WriteLine(waitSec > 0
                ? $"Error: no new screenshot appeared in the camera roll within {waitSec}s."
                : "Error: the camera roll has no screenshots.");
            return 1;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>
    /// iOS ≤ 16: mount the Developer Disk Image so screenshotr / idevicedebug work, the way Ipaapk
    /// does — images come from the xushuduo/Xcode-iOS-Developer-Disk-Image repo, cloned once into
    /// ~/.clibridge4unity/ddi. iOS 17+ images are personalized per device; that repo has none and
    /// libimobiledevice can't mount them, so this returns false there and says why.
    /// </summary>
    static bool TryMountDeveloperImage(LocalDevice dev, bool quiet = false)
    {
        string info = FindIDeviceTool("ideviceinfo"); string mounter = FindIDeviceTool("ideviceimagemounter");
        if (info == null || mounter == null) return false;
        var (c0, ver, _) = Exec(info, $"-u {dev.Serial} -k ProductVersion", 10000);
        if (c0 != 0 || !Version.TryParse(ver.Trim(), out var v)) return false;
        if (v.Major >= 17)
        {
            if (!quiet) Console.Error.WriteLine($"[DEVICE] iOS {v} uses a personalized Developer Disk Image — libimobiledevice can't mount it (pymobiledevice3 can). Launch by tapping the icon; screenshots come from the camera roll.");
            return false;
        }
        var (c1, mounted, _) = Exec(mounter, $"-u {dev.Serial} -l", 15000);
        if (c1 == 0 && mounted.Contains("ImageSignature") && !mounted.Contains("ImageSignature[0]")) return true;   // already mounted

        string ddiRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity", "ddi");
        string repo = Path.Combine(ddiRoot, "Xcode-iOS-Developer-Disk-Image");
        if (!Directory.Exists(repo))
        {
            string git = FindOnPath("git.exe") ?? FindOnPath("git");
            if (git == null) { if (!quiet) Console.Error.WriteLine("[DEVICE] git not found — can't fetch Developer Disk Images."); return false; }
            Directory.CreateDirectory(ddiRoot);
            Console.Error.WriteLine("[DEVICE] fetching Developer Disk Images (one-time, ~1 GB)…");
            var (cg, _, eg) = Exec(git, $"clone --depth 1 https://github.com/xushuduo/Xcode-iOS-Developer-Disk-Image \"{repo}\"", 20 * 60 * 1000, stream: true);
            if (cg != 0) { Console.Error.WriteLine($"[DEVICE] clone failed: {eg.Trim()}"); return false; }
        }
        string want = $"{v.Major}.{v.Minor}";
        string dmg = Directory.GetFiles(repo, "DeveloperDiskImage.dmg", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Replace('\\', '/').Contains("/" + want + "/"))
            ?? Directory.GetFiles(repo, "DeveloperDiskImage.dmg", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Replace('\\', '/').Contains("/" + v.Major + "."));
        if (dmg == null) { if (!quiet) Console.Error.WriteLine($"[DEVICE] no Developer Disk Image for iOS {want} in the repo."); return false; }
        var (cm, om, em) = Exec(mounter, $"-u {dev.Serial} \"{dmg}\"", 60000);
        bool ok = cm == 0 && !(om + em).Contains("Error");
        if (!quiet || !ok) Console.Error.WriteLine(ok ? $"[DEVICE] mounted {dmg}" : $"[DEVICE] mount failed: {(om + em).Trim()}");
        return ok;
    }

    sealed class UiNode
    {
        public string Text, Desc, Id, Class, Pkg;
        public bool Clickable, Enabled;
        public int X1, Y1, X2, Y2;
        public int CX => (X1 + X2) / 2;
        public int CY => (Y1 + Y2) / 2;
        public string Label => !string.IsNullOrEmpty(Text) ? Text : !string.IsNullOrEmpty(Desc) ? Desc : "";
    }

    /// <summary>uiautomator's XML dump of the current window, flattened. Layout containers with no text/id are dropped — they're noise for "what can I tap".</summary>
    static List<UiNode> DumpUi(string adb, string serial, out string error)
    {
        error = null;
        // /dev/tty output is unreliable across versions; dump to a file then cat it raw.
        var (code, stdout, stderr) = Exec(adb, $"-s {serial} shell uiautomator dump /sdcard/clibridge_ui.xml", 30000);
        if (code != 0 || (stdout + stderr).Contains("ERROR"))
        {
            error = (stdout + stderr).Trim();
            if (error.Contains("could not get idle state"))
                error += "\n  → the screen never went idle (animation / video). Retry, or tap by coordinates.";
            return null;
        }
        var (c2, xmlBytes, e2) = ExecBytes(adb, $"-s {serial} exec-out cat /sdcard/clibridge_ui.xml", 30000);
        Exec(adb, $"-s {serial} shell rm -f /sdcard/clibridge_ui.xml", 10000);
        if (c2 != 0 || xmlBytes.Length == 0) { error = e2.Trim(); return null; }

        var nodes = new List<UiNode>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(xmlBytes));
            foreach (var el in doc.Descendants("node"))
            {
                string A(string n) => el.Attribute(n)?.Value ?? "";
                var m = Regex.Match(A("bounds"), @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
                if (!m.Success) continue;
                var n = new UiNode
                {
                    Text = A("text"), Desc = A("content-desc"), Id = A("resource-id"), Class = A("class"), Pkg = A("package"),
                    Clickable = A("clickable") == "true", Enabled = A("enabled") != "false",
                    X1 = int.Parse(m.Groups[1].Value), Y1 = int.Parse(m.Groups[2].Value),
                    X2 = int.Parse(m.Groups[3].Value), Y2 = int.Parse(m.Groups[4].Value),
                };
                if (n.Text.Length == 0 && n.Desc.Length == 0 && n.Id.Length == 0 && !n.Clickable) continue;
                nodes.Add(n);
            }
        }
        catch (Exception ex) { error = "could not parse uiautomator XML: " + ex.Message; return null; }
        return nodes;
    }

    static int UiDump(string[] args)
    {
        string serial = null, filter = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else filter ??= args[i].Trim('"');
        }
        var dev = ResolveLocal(serial, "Android");
        if (dev == null) return 1;
        if (!RequireAdb(out var adb)) return 1;
        var nodes = DumpUi(adb, dev.Serial, out var err);
        if (nodes == null) { Console.Error.WriteLine($"Error: uiautomator dump failed: {err}"); return 1; }
        if (filter != null)
            nodes = nodes.Where(n => n.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || n.Desc.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || n.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (nodes.Count == 0) { Console.WriteLine(filter != null ? $"No native views match '{filter}'." : "No labelled native views (a full-screen Unity surface shows nothing here — use BUGPUNCH hierarchy)."); return 0; }
        string pkg = nodes.Select(n => n.Pkg).FirstOrDefault(p => p.Length > 0) ?? "";
        Console.WriteLine($"{nodes.Count} native views  (foreground package: {pkg})");
        Console.WriteLine($"{"CENTER",-12}{"TAP",-5}{"TEXT / DESC",-36}{"RESOURCE-ID",-40}CLASS");
        var dump = new StringBuilder();
        foreach (var n in nodes)
        {
            string row = $"{n.CX + "," + n.CY,-12}{(n.Clickable ? (n.Enabled ? "yes" : "off") : "-"),-5}{Truncate(n.Label, 34),-36}{Truncate(ShortId(n.Id), 38),-40}{n.Class.Split('.').Last()}";
            Console.WriteLine(row);
            dump.AppendLine($"[{n.X1},{n.Y1}][{n.X2},{n.Y2}] click={n.Clickable} enabled={n.Enabled} text=\"{n.Text}\" desc=\"{n.Desc}\" id={n.Id} class={n.Class}");
        }
        string f = DeviceSession.Current.Artifact("ui", filter, ".txt"); File.WriteAllText(f, dump.ToString());
        DeviceSession.Add("ui", f, $"{nodes.Count} views");
        Console.Error.WriteLine("[DEVICE] tap a row with: DEVICE tap <x> <y>   or   DEVICE tap \"<text or id>\"");
        return 0;
    }

    /// <summary>Poll the native UI until a view matches (or stops matching). timeoutSec 0 = check once (assert).</summary>
    public static int WaitForUi(string serial, string label, int timeoutSec, bool expectPresent)
    {
        var dev = ResolveLocal(serial, "Android");
        if (dev == null) return NativeInputUnsupported();
        if (!RequireAdb(out var adb)) return 1;
        var cmp = StringComparison.OrdinalIgnoreCase;
        var sw = Stopwatch.StartNew();
        string lastErr = null;
        do
        {
            var nodes = DumpUi(adb, dev.Serial, out lastErr);
            if (nodes != null)
            {
                bool present = nodes.Any(n => n.Text.Contains(label, cmp) || n.Desc.Contains(label, cmp) || n.Id.Contains(label, cmp));
                if (present == expectPresent)
                {
                    Console.WriteLine($"\"{label}\" {(expectPresent ? "present" : "absent")} after {sw.Elapsed.TotalSeconds:F1}s");
                    return 0;
                }
            }
            if (timeoutSec > 0) Thread.Sleep(1000);
        } while (sw.Elapsed.TotalSeconds < timeoutSec);
        Console.Error.WriteLine(timeoutSec > 0
            ? $"Error: \"{label}\" {(expectPresent ? "did not appear" : "still present")} after {timeoutSec}s" + (lastErr != null ? $" (last dump error: {lastErr})" : "")
            : $"Error: assert failed — \"{label}\" is {(expectPresent ? "not on screen" : "on screen")}");
        string shot = QuietScreenshot(serial, "assert-failed");
        if (shot != null) { Console.WriteLine(shot + "  (screen at failure)"); DeviceSession.Add("screenshot", shot, "at failure"); }
        return 1;
    }

    static string ShortId(string id) => id.Contains(":id/") ? id.Substring(id.IndexOf(":id/") + 4) : id;

    static int Tap(string[] args)
    {
        string serial = null; var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else pos.Add(args[i]);
        }
        var dev = ResolveLocal(serial, "Android");
        if (dev == null) return NativeInputUnsupported();
        if (!RequireAdb(out var adb)) return 1;

        if (pos.Count >= 2 && int.TryParse(pos[0], out var x) && int.TryParse(pos[1], out var y))
            return AdbInput(adb, dev, $"tap {x} {y}", $"tapped ({x},{y})");

        string label = string.Join(" ", pos).Trim('"');
        if (label.Length == 0) { Console.Error.WriteLine("Usage: DEVICE tap <x> <y>   |   DEVICE tap \"<text | content-desc | resource-id>\""); return 1; }

        var nodes = DumpUi(adb, dev.Serial, out var err);
        if (nodes == null) { Console.Error.WriteLine($"Error: uiautomator dump failed: {err}"); return 1; }
        // Exact text/desc/id first, then substring; prefer clickable + enabled so "OK" lands on the button, not a title.
        var cmp = StringComparison.OrdinalIgnoreCase;
        var exact = nodes.Where(n => n.Text.Equals(label, cmp) || n.Desc.Equals(label, cmp) || n.Id.Equals(label, cmp) || ShortId(n.Id).Equals(label, cmp)).ToList();
        var hits = exact.Count > 0 ? exact
            : nodes.Where(n => n.Text.Contains(label, cmp) || n.Desc.Contains(label, cmp) || n.Id.Contains(label, cmp)).ToList();
        if (hits.Count > 1 && hits.Any(h => h.Clickable && h.Enabled)) hits = hits.Where(h => h.Clickable && h.Enabled).ToList();
        if (hits.Count == 0)
        {
            Console.Error.WriteLine($"Error: no native view matches '{label}'. DEVICE ui shows what's on screen.");
            return 1;
        }
        if (hits.Count > 1)
        {
            Console.Error.WriteLine($"Error: '{label}' matches {hits.Count} views — tap by coordinates:");
            foreach (var h in hits) Console.Error.WriteLine($"  {h.CX},{h.CY}  {Truncate(h.Label, 40)}  {ShortId(h.Id)}");
            return 1;
        }
        var t = hits[0];
        return AdbInput(adb, dev, $"tap {t.CX} {t.CY}", $"tapped \"{(t.Label.Length > 0 ? t.Label : ShortId(t.Id))}\" at ({t.CX},{t.CY})");
    }

    static int Swipe(string[] args)
    {
        string serial = null; var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else pos.Add(args[i]);
        }
        if (pos.Count < 4 || !pos.Take(4).All(p => int.TryParse(p, out _)))
        { Console.Error.WriteLine("Usage: DEVICE swipe <x1> <y1> <x2> <y2> [durationMs]"); return 1; }
        int ms = pos.Count > 4 && int.TryParse(pos[4], out var d) ? d : 300;
        var dev = ResolveLocal(serial, "Android");
        if (dev == null) return NativeInputUnsupported();
        if (!RequireAdb(out var adb)) return 1;
        return AdbInput(adb, dev, $"swipe {pos[0]} {pos[1]} {pos[2]} {pos[3]} {ms}", $"swiped ({pos[0]},{pos[1]}) → ({pos[2]},{pos[3]}) in {ms} ms");
    }

    static int TypeText(string[] args)
    {
        string serial = null; var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else pos.Add(args[i]);
        }
        string text = string.Join(" ", pos).Trim('"');
        if (text.Length == 0) { Console.Error.WriteLine("Usage: DEVICE type \"<text>\"   (focus a field first — DEVICE tap)"); return 1; }
        var dev = ResolveLocal(serial, "Android");
        if (dev == null) return NativeInputUnsupported();
        if (!RequireAdb(out var adb)) return 1;
        // `input text` takes one shell word: spaces become %s and shell metacharacters are escaped.
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == ' ') sb.Append("%s");
            else if ("()<>|;&*~\"'`\\$".IndexOf(ch) >= 0) sb.Append('\\').Append(ch);
            else sb.Append(ch);
        }
        return AdbInput(adb, dev, $"text \"{sb}\"", $"typed \"{Truncate(text, 40)}\"");
    }

    static int Key(string[] args)
    {
        string serial = null, key = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--serial" || args[i] == "-s") && i + 1 < args.Length) serial = args[++i];
            else key ??= args[i];
        }
        if (key == null) { Console.Error.WriteLine("Usage: DEVICE key BACK|HOME|ENTER|MENU|POWER|VOLUME_UP|TAB|DEL|<KEYCODE_X or number>"); return 1; }
        var dev = ResolveLocal(serial, "Android");
        if (dev == null) return NativeInputUnsupported();
        if (!RequireAdb(out var adb)) return 1;
        string code = int.TryParse(key, out _) ? key : key.StartsWith("KEYCODE_", StringComparison.OrdinalIgnoreCase) ? key.ToUpperInvariant() : "KEYCODE_" + key.ToUpperInvariant();
        return AdbInput(adb, dev, $"keyevent {code}", $"sent {code}");
    }

    static int NativeInputUnsupported()
    {
        // ResolveLocal already printed why (no Android device). Add the iOS reality so nobody retries in circles.
        Console.Error.WriteLine("  Native input over USB is Android-only. iOS needs WebDriverAgent/XCTest from a Mac; libimobiledevice can only screenshot.");
        Console.Error.WriteLine("  For input inside the Unity view on either platform use BUGPUNCH <device> tap/swipe.");
        return 1;
    }

    static int AdbInput(string adb, LocalDevice dev, string inputArgs, string done)
    {
        var (code, stdout, stderr) = Exec(adb, $"-s {dev.Serial} shell input {inputArgs}", 20000);
        string all = (stdout + stderr).Trim();
        if (code != 0 || all.Contains("Error") || all.Contains("Exception"))
        {
            Console.Error.WriteLine($"Error: input failed (exit {code}): {all}");
            return 1;
        }
        Console.WriteLine($"{done} on {dev.Model}");
        return 0;
    }

    static (int code, byte[] bytes, string stderr) ExecBytes(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!Task.WaitAll(new[] { copy, errTask }, timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, ms.ToArray(), $"timed out after {timeoutMs / 1000}s");
            }
            p.WaitForExit();
            return (p.ExitCode, ms.ToArray(), errTask.Result);
        }
        catch (Exception ex) { return (-1, Array.Empty<byte>(), ex.Message); }
    }

    // ─── Package / bundle identification ───────────────────────────────

    static string ApkPackageName(string apk)
    {
        string aapt = FindAapt();
        if (aapt == null) return null;
        var (code, stdout, _) = Exec(aapt, $"dump badging \"{apk}\"", 30000);
        if (code != 0) return null;
        var m = Regex.Match(stdout, @"package: name='([^']+)'");
        return m.Success ? m.Groups[1].Value : null;
    }

    static HashSet<string> AdbPackages(string adb, string serial)
    {
        var (code, stdout, _) = Exec(adb, $"-s {serial} shell pm list packages -3", 30000);
        var set = new HashSet<string>();
        if (code != 0) return set;
        foreach (var l in stdout.Split('\n'))
        {
            string s = l.Trim();
            if (s.StartsWith("package:")) set.Add(s.Substring(8));
        }
        return set;
    }

    /// <summary>
    /// Info.plist inside an .ipa is XML when Unity/Xcode wrote it un-optimised and binary
    /// (bplist00) after an archive export. XML is regex'd; binary is left to the install diff
    /// rather than pulling in a bplist decoder for one string.
    /// </summary>
    static string IpaBundleId(string ipa)
    {
        try
        {
            using var zip = ZipFile.OpenRead(ipa);
            var plist = zip.Entries.FirstOrDefault(e =>
                Regex.IsMatch(e.FullName, @"^Payload/[^/]+\.app/Info\.plist$"));
            if (plist == null) return null;
            using var s = plist.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 6) == "bplist") return null;
            string xml = Encoding.UTF8.GetString(bytes);
            var m = Regex.Match(xml, @"<key>CFBundleIdentifier</key>\s*<string>([^<]+)</string>");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    static string IpaAppName(string ipa)
    {
        try
        {
            using var zip = ZipFile.OpenRead(ipa);
            var app = zip.Entries.Select(e => Regex.Match(e.FullName, @"^Payload/([^/]+)\.app/")).FirstOrDefault(m => m.Success);
            return app?.Groups[1].Value;
        }
        catch { return null; }
    }

    static string IosBundleByAppName(string installer, string udid, string appName)
    {
        if (string.IsNullOrEmpty(appName)) return null;
        var (code, stdout, _) = Exec(installer, $"-u {udid} -l", 60000);
        if (code != 0) return null;
        foreach (var l in stdout.Split('\n'))
        {
            var m = Regex.Match(l.Trim(), @"^([A-Za-z0-9.\-]+)\s*,\s*""[^""]*""\s*,\s*""([^""]*)""");
            if (m.Success && m.Groups[2].Value.Equals(appName, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value;
        }
        return null;
    }

    static HashSet<string> IosBundles(string installer, string udid)
    {
        var (code, stdout, _) = Exec(installer, $"-u {udid} -l", 60000);
        var set = new HashSet<string>();
        if (code != 0) return set;
        foreach (var l in stdout.Split('\n'))
        {
            // "com.foo.bar, "1.2", "Name"" — first CSV column; header line is "CFBundleIdentifier, ..."
            var m = Regex.Match(l.Trim(), @"^([A-Za-z0-9\.\-]+)\s*,");
            if (m.Success && m.Groups[1].Value != "CFBundleIdentifier") set.Add(m.Groups[1].Value);
        }
        return set;
    }

    // ─── Process helpers ───────────────────────────────────────────────

    static (int code, string stdout, string stderr) Exec(string exe, string args, int timeoutMs, bool stream = false, bool killOnTimeout = false)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            var so = new StringBuilder(); var se = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data == null) return; so.AppendLine(e.Data); if (stream) Console.Error.WriteLine("  " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; se.AppendLine(e.Data); if (stream) Console.Error.WriteLine("  " + e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                if (killOnTimeout) { p.WaitForExit(2000); return (0, so.ToString(), se.ToString()); }
                return (-1, so.ToString(), se.ToString() + $"\n(timed out after {timeoutMs / 1000}s)");
            }
            p.WaitForExit();
            return (p.ExitCode, so.ToString(), se.ToString());
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>Run to completion (or Ctrl+C) with output passed straight through.</summary>
    static int Stream(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { p.Kill(true); } catch { } };
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BUGPUNCH — server API device control (internal devices only)
    // ═══════════════════════════════════════════════════════════════════════

    const string DefaultServer = "https://bugpunch.com";
    static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity", "bugpunch.json");

    sealed class BpConfig { public string Server; public string Token; public string Email; public string Scope; }

    static BpConfig LoadConfig()
    {
        var cfg = new BpConfig { Server = DefaultServer };
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var r = doc.RootElement;
                if (r.TryGetProperty("server", out var s)) cfg.Server = s.GetString();
                if (r.TryGetProperty("token", out var t)) cfg.Token = t.GetString();
                if (r.TryGetProperty("email", out var e)) cfg.Email = e.GetString();
                if (r.TryGetProperty("scope", out var sc)) cfg.Scope = sc.GetString();
            }
        }
        catch { }
        // Env wins over the file so CI can inject a token without touching the home dir.
        string envTok = Environment.GetEnvironmentVariable("BUGPUNCH_TOKEN");
        string envSrv = Environment.GetEnvironmentVariable("BUGPUNCH_SERVER");
        if (!string.IsNullOrEmpty(envTok)) cfg.Token = envTok;
        if (!string.IsNullOrEmpty(envSrv)) cfg.Server = envSrv;
        cfg.Server = (cfg.Server ?? DefaultServer).TrimEnd('/');
        return cfg;
    }

    static void SaveConfig(BpConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("server", cfg.Server);
            w.WriteString("token", cfg.Token);
            w.WriteString("email", cfg.Email ?? "");
            w.WriteString("scope", cfg.Scope ?? "");
            w.WriteEndObject();
        }
        File.WriteAllBytes(ConfigPath, ms.ToArray());
    }

    static HttpClient Http(BpConfig cfg, int timeoutSec = 60)
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        h.DefaultRequestHeaders.Add("X-Api-Key", cfg.Token);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("clibridge4unity");
        return h;
    }

    public static int RunBugpunch(string[] args, bool fromScript = false)
    {
        if (args.Length == 0) { PrintBugpunchUsage(); return 1; }
        _cmdLine = "BUGPUNCH " + string.Join(" ", args);
        DeviceSession.Current?.SetCommand(_cmdLine);
        int code = RunBugpunchInner(args);
        if (fromScript) DeviceSession.Current?.Log(code == 0 ? "ok" : $"exit {code}"); else DeviceSession.Finish(code);
        return code;
    }

    static int RunBugpunchInner(string[] args)
    {
        string sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            switch (sub)
            {
                case "auth": case "login": return Auth(rest);
                case "logout": if (File.Exists(ConfigPath)) File.Delete(ConfigPath); Console.WriteLine("Token removed."); return 0;
                case "devices": case "list": case "ls": return BpDevices(rest);
                case "help": case "-h": case "--help": PrintBugpunchUsage(); return 0;
                default: return BpDeviceCommand(args[0], rest);
            }
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 10;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("Error: request timed out.");
            return 14;
        }
    }

    static void PrintBugpunchUsage()
    {
        Console.Error.WriteLine("BUGPUNCH — drive INTERNAL devices running the Bugpunch SDK through the server API");
        Console.Error.WriteLine("  BUGPUNCH auth <token> [--server URL]   verify + store a personal access token (needs 'device control' scope)");
        Console.Error.WriteLine("  BUGPUNCH auth                          show who the stored token is");
        Console.Error.WriteLine("  BUGPUNCH devices [--all] [--project ID] [filter]   internal devices (live; --all adds recently-seen offline)");
        Console.Error.WriteLine("  BUGPUNCH <device> info | perf | hierarchy | prefs | log [sinceId]");
        Console.Error.WriteLine("  BUGPUNCH <device> screenshot [--out file.jpg] [--scale 0.5] [--quality 75]");
        Console.Error.WriteLine("  BUGPUNCH <device> run <C# code | @file.cs>          execute on the device, print the output");
        Console.Error.WriteLine("  BUGPUNCH <device> action <json | @file.json>        JSON action (click/fill/navigate/gesture)");
        Console.Error.WriteLine("  BUGPUNCH <device> tap <x> <y>                       normalised 0–1, origin top-left");
        Console.Error.WriteLine("  BUGPUNCH <device> swipe <x1> <y1> <x2> <y2> [ms]");
        Console.Error.WriteLine("  BUGPUNCH <device> get <path> | post <path> [json|@file]   raw Remote-IDE RPC");
        Console.Error.WriteLine("  BUGPUNCH <device> memsnap [--no-analyze]            Unity memory snapshot on the device, pulled here, summarised by MEMSNAP");
        Console.Error.WriteLine("  BUGPUNCH <device> memsnap list | pull <devicePath> | delete <devicePath>");
        Console.Error.WriteLine("  Artifacts: ~/.clibridge4unity/devices/<id>/ — every command ends with an [artifacts] line naming what it wrote.");
        Console.Error.WriteLine("  <device> = id, id prefix, or a name/nickname/model substring. Only internal-role devices are ever visible.");
        Console.Error.WriteLine("  Env: BUGPUNCH_TOKEN, BUGPUNCH_SERVER override ~/.clibridge4unity/bugpunch.json");
    }

    static int Auth(string[] args)
    {
        string token = null, server = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i].TrimEnd('/');
            else token ??= args[i];
        }
        var cfg = LoadConfig();
        if (server != null) cfg.Server = server;
        if (token != null) cfg.Token = token;
        if (string.IsNullOrEmpty(cfg.Token))
        {
            Console.Error.WriteLine("No token stored. Create one at <server>/settings → API Keys with 'Device control (admin)' ticked, then:");
            Console.Error.WriteLine("  clibridge4unity BUGPUNCH auth bugpunch_pat_…  [--server https://bugpunch.com]");
            return 1;
        }

        using var http = Http(cfg, 20);
        var resp = http.GetAsync($"{cfg.Server}/api/v1/me").GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine($"Error: {cfg.Server} rejected the token (401). Expired, revoked, or the wrong server.");
            return 1;
        }
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error: {cfg.Server}/api/v1/me → {(int)resp.StatusCode}: {Truncate(body, 300)}");
            if ((int)resp.StatusCode == 404)
                Console.Error.WriteLine("  → this server predates the device-control API; deploy a bugpunch-server that has /api/v1/devices.");
            return 1;
        }
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        cfg.Email = r.TryGetProperty("email", out var e) ? e.GetString() : "";
        cfg.Scope = r.TryGetProperty("scope", out var s) ? s.GetString() : "";
        bool canControl = r.TryGetProperty("canControlDevices", out var c) && c.GetBoolean();
        if (token != null || server != null) SaveConfig(cfg);

        Console.WriteLine($"Server:  {cfg.Server}");
        Console.WriteLine($"Account: {cfg.Email}  ({(r.TryGetProperty("displayName", out var dn) ? dn.GetString() : "")})");
        Console.WriteLine($"Scope:   {cfg.Scope}{(canControl ? "  — device control OK" : "  — READ ONLY: listing works, control will 403")}");
        if (!canControl)
            Console.WriteLine("         Mint a token with 'Device control (admin)' ticked (Settings → API Keys) to run/tap/screenshot.");
        if (token != null || server != null) Console.WriteLine($"Stored:  {ConfigPath}");
        return 0;
    }

    static bool RequireToken(out BpConfig cfg)
    {
        cfg = LoadConfig();
        if (!string.IsNullOrEmpty(cfg.Token)) return true;
        Console.Error.WriteLine("Error: no Bugpunch token. Run: clibridge4unity BUGPUNCH auth <token>   (or set BUGPUNCH_TOKEN)");
        return false;
    }

    sealed class BpDevice
    {
        public string Id, Name, Nickname, Platform, Model, Os, AppVersion, ProjectId, Tester;
        public bool Online;
        /// <summary>Set when the device is reached straight over HTTP (dev-build LocalIdeServer) — no server, no token.</summary>
        public string DirectUrl;
        public string Label => string.IsNullOrEmpty(Nickname) ? Name : $"{Nickname} ({Name})";
    }

    static bool IsDirectQuery(string q) => q != null && (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Probe a LocalIdeServer root: its hello JSON identifies the build. Null if nothing answers.</summary>
    static BpDevice ProbeDirect(string url, int timeoutMs = 3000)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            string body = http.GetStringAsync(url.TrimEnd('/') + "/").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (!r.TryGetProperty("bugpunch", out var tag) || tag.GetString() != "local-ide") return null;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
            return new BpDevice
            {
                Id = S("deviceId").Length > 0 ? S("deviceId") : url, Name = S("name").Length > 0 ? S("name") : S("model"),
                Model = S("model"), Platform = S("platform"), AppVersion = S("version"), Online = true, DirectUrl = url.TrimEnd('/'),
            };
        }
        catch { return null; }
    }

    static List<BpDevice> FetchDevices(BpConfig cfg, bool all, string projectId)
    {
        using var http = Http(cfg, 30);
        string url = $"{cfg.Server}/api/v1/devices?all={(all ? 1 : 0)}" + (projectId != null ? $"&projectId={Uri.EscapeDataString(projectId)}" : "");
        var resp = http.GetAsync(url).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{url} → {(int)resp.StatusCode}: {Truncate(body, 300)}");
        var list = new List<BpDevice>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("devices", out var arr)) return list;
        foreach (var d in arr.EnumerateArray())
        {
            string S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
            list.Add(new BpDevice
            {
                Id = S("id"), Name = S("name"), Nickname = S("nickname"), Platform = S("platform"),
                Model = S("model"), Os = S("os"), AppVersion = S("appVersion"), ProjectId = S("projectId"),
                Online = d.TryGetProperty("online", out var o) && o.GetBoolean(),
                Tester = d.TryGetProperty("testerUser", out var tu) && tu.ValueKind == JsonValueKind.Object && tu.TryGetProperty("email", out var em) ? em.GetString() : "",
            });
        }
        return list;
    }

    static int BpDevices(string[] args)
    {
        bool all = false; string project = null; string filter = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--all" || args[i] == "-a") all = true;
            else if ((args[i] == "--project" || args[i] == "-p") && i + 1 < args.Length) project = args[++i];
            else filter ??= args[i];
        }
        if (!RequireToken(out var cfg)) return 1;
        var devices = FetchDevices(cfg, all, project);
        if (filter != null)
            devices = devices.Where(d => Matches(d, filter)).ToList();
        if (devices.Count == 0)
        {
            Console.WriteLine(all ? "No internal devices." : "No internal devices online. (--all lists recently-seen offline ones.)");
            Console.WriteLine("A device shows here only when its signed-in tester has the INTERNAL role and the game is running with the SDK.");
            return 0;
        }
        Console.WriteLine($"{"STATE",-8}{"ID",-14}{"DEVICE",-30}{"PLATFORM",-10}{"APP",-10}{"OS",-16}TESTER");
        foreach (var d in devices.OrderByDescending(d => d.Online).ThenBy(d => d.Label))
            Console.WriteLine($"{(d.Online ? "online" : "offline"),-8}{Truncate(d.Id, 12),-14}{Truncate(d.Label, 28),-30}{Truncate(d.Platform, 8),-10}{Truncate(d.AppVersion, 8),-10}{Truncate(d.Os, 14),-16}{d.Tester}");
        return 0;
    }

    static bool Matches(BpDevice d, string q)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        return d.Id.Equals(q, cmp) || d.Id.StartsWith(q, cmp)
            || (d.Name?.Contains(q, cmp) ?? false) || (d.Nickname?.Contains(q, cmp) ?? false)
            || (d.Model?.Contains(q, cmp) ?? false);
    }

    /// <summary>Exact id → id prefix → name/nickname/model substring; online preferred; ambiguity is an error, never a guess.</summary>
    static BpDevice ResolveBp(BpConfig cfg, string query)
    {
        if (IsDirectQuery(query))
        {
            var direct = ProbeDirect(query);
            if (direct == null)
            {
                Console.Error.WriteLine($"Error: nothing answering at {query} — is the app running, a Development Build, and the USB forward / LAN route up? (DEVICE up re-establishes it)");
                return null;
            }
            // Artifacts: the active session's dir when this URL is that session's route,
            // otherwise a dir named after the endpoint (host_port) so a second build on the
            // LAN doesn't write into the phone's folder.
            string active = DeviceSession.ActiveKey;
            bool isActiveRoute = active != null && string.Equals(DeviceSession.ActiveState().GetValueOrDefault("direct")?.TrimEnd('/'), query.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            DeviceSession.Open(isActiveRoute ? active : new Uri(query).Authority.Replace(':', '_'), _cmdLine ?? "BUGPUNCH");
            return direct;
        }
        var dev = ResolveBpCore(cfg, query);
        if (dev != null) DeviceSession.Open(dev.Id, _cmdLine ?? "BUGPUNCH");
        return dev;
    }

    /// <summary>Script step `bp waitonline [sec]`: wait until the named (or the only) internal device is online; fixes the device for later steps.</summary>
    public static int BpWaitOnline(ref string bpDevice, int timeoutSec)
    {
        if (!RequireToken(out var cfg)) return 1;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var devices = FetchDevices(cfg, all: false, projectId: null);
            string q = bpDevice;
            var hit = q == null ? devices : devices.Where(d => Matches(d, q)).ToList();
            if (hit.Count == 1)
            {
                bpDevice = hit[0].Id;
                DeviceSession.Open(hit[0].Id, _cmdLine ?? "BUGPUNCH");
                Console.WriteLine($"{hit[0].Label} [{hit[0].Id}] online after {sw.Elapsed.TotalSeconds:F1}s");
                return 0;
            }
            if (hit.Count > 1)
            {
                Console.Error.WriteLine($"Error: {hit.Count} internal devices online — name one with --bp:");
                foreach (var h in hit) Console.Error.WriteLine($"  {h.Id}  {h.Label}");
                return 1;
            }
            if (sw.Elapsed.TotalSeconds >= timeoutSec) break;
            Thread.Sleep(2000);
        }
        Console.Error.WriteLine($"Error: no internal device{(bpDevice != null ? $" matching '{bpDevice}'" : "")} came online in {timeoutSec}s.");
        return 10;
    }

    /// <summary>Script step `bp assert "<name>"`: the Unity hierarchy must (not) contain a GameObject with that name.</summary>
    public static int BpAssertHierarchy(ref string bpDevice, string name, bool expectPresent)
    {
        if (!RequireToken(out var cfg)) return 1;
        if (bpDevice == null) { Console.Error.WriteLine("Error: no Bugpunch device — --bp <device> or `bp waitonline` first."); return 1; }
        var dev = ResolveBp(cfg, bpDevice);
        if (dev == null) return 1;
        var (status, bytes, _) = Call(cfg, dev, "GET", "/hierarchy", null, 60);
        if (status != 200) { Console.Error.WriteLine($"Error: /hierarchy → {status}"); return 1; }
        bool present = Encoding.UTF8.GetString(bytes).Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase)
                    || Encoding.UTF8.GetString(bytes).Contains(name, StringComparison.OrdinalIgnoreCase);
        if (present == expectPresent) { Console.WriteLine($"hierarchy {(present ? "contains" : "does not contain")} \"{name}\""); return 0; }
        Console.Error.WriteLine($"Error: assert failed — hierarchy {(present ? "contains" : "does not contain")} \"{name}\"");
        return 1;
    }

    static BpDevice ResolveBpCore(BpConfig cfg, string query)
    {
        var devices = FetchDevices(cfg, all: true, projectId: null);
        var cmp = StringComparison.OrdinalIgnoreCase;
        var exact = devices.Where(d => d.Id.Equals(query, cmp)).ToList();
        if (exact.Count == 1) return exact[0];
        var hits = devices.Where(d => Matches(d, query)).ToList();
        if (hits.Count > 1 && hits.Count(h => h.Online) == 1) hits = hits.Where(h => h.Online).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0)
        {
            Console.Error.WriteLine($"Error: no internal device matches '{query}'.");
            Console.Error.WriteLine(devices.Count == 0
                ? "       No internal devices are known to the server at all — is the tester signed in with an internal account?"
                : "       Run: clibridge4unity BUGPUNCH devices --all");
            return null;
        }
        Console.Error.WriteLine($"Error: '{query}' is ambiguous — {hits.Count} devices match:");
        foreach (var h in hits) Console.Error.WriteLine($"  {h.Id}  {h.Label}  {h.Platform}  {(h.Online ? "online" : "offline")}");
        return null;
    }

    static int BpDeviceCommand(string deviceQuery, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine($"Usage: BUGPUNCH {deviceQuery} <info|perf|hierarchy|prefs|log|screenshot|run|action|tap|swipe|get|post> …");
            return 1;
        }
        BpConfig cfg;
        if (IsDirectQuery(deviceQuery)) cfg = LoadConfig(); else if (!RequireToken(out cfg)) return 1;
        var dev = ResolveBp(cfg, deviceQuery);
        if (dev == null) return 1;
        if (!dev.Online)
        {
            Console.Error.WriteLine($"Error: {dev.Label} [{dev.Id}] is offline — the game isn't running with the SDK connected.");
            return 10;
        }

        string sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "info": case "device-info": return Proxy(cfg, dev, "GET", "/device-info", null, null);
            case "perf": return Proxy(cfg, dev, "GET", "/perf", null, null);
            case "hierarchy": case "tree": return Proxy(cfg, dev, "GET", "/hierarchy", null, null);
            case "scenes": return Proxy(cfg, dev, "GET", "/scenes", null, null);
            case "prefs": case "playerprefs": return Proxy(cfg, dev, "GET", "/playerprefs/list", null, null);
            case "config": case "game-config": return Proxy(cfg, dev, "GET", "/game-config", null, null);
            case "log": case "console":
            {
                string since = rest.Length > 0 ? rest[0] : "0";
                return Proxy(cfg, dev, "GET", $"/log?logId={since}", null, null);
            }
            case "screenshot": case "capture": case "shot":
            {
                string outPath = null; string scale = "0.5", quality = "75";
                for (int i = 0; i < rest.Length; i++)
                {
                    if ((rest[i] == "--out" || rest[i] == "-o") && i + 1 < rest.Length) outPath = rest[++i];
                    else if (rest[i] == "--scale" && i + 1 < rest.Length) scale = rest[++i];
                    else if (rest[i] == "--quality" && i + 1 < rest.Length) quality = rest[++i];
                }
                string label = rest.FirstOrDefault(r => !r.StartsWith("-") && r != scale && r != quality && r != outPath);
                outPath ??= DeviceSession.Current.Artifact("screenshot", label, ".jpg");
                return Proxy(cfg, dev, "GET", $"/capture?scale={scale}&quality={quality}", null, outPath);
            }
            case "run": case "exec": case "eval":
            {
                string code = ReadPayload(rest, joinWithSpaces: true);
                if (string.IsNullOrWhiteSpace(code)) { Console.Error.WriteLine("Usage: BUGPUNCH <device> run <C# code | @file.cs>"); return 1; }
                string label = rest.Length == 1 && rest[0].StartsWith("@") ? Path.GetFileNameWithoutExtension(rest[0].Substring(1)) : null;
                return Proxy(cfg, dev, "POST", "/run", new StringContent(code, Encoding.UTF8, "text/plain"), null, timeoutSec: 180, saveAs: ("run", label));
            }
            case "action":
            {
                string json = ReadPayload(rest, joinWithSpaces: true);
                if (string.IsNullOrWhiteSpace(json)) { Console.Error.WriteLine("Usage: BUGPUNCH <device> action <json | @file.json>"); return 1; }
                return Proxy(cfg, dev, "POST", "/action", new StringContent(json, Encoding.UTF8, "application/json"), null, timeoutSec: 180);
            }
            case "tap": case "click":
            {
                if (rest.Length < 2 || !TryNorm(rest[0], out var x) || !TryNorm(rest[1], out var y))
                { Console.Error.WriteLine("Usage: BUGPUNCH <device> tap <x> <y>   (0–1 normalised, origin top-left)"); return 1; }
                return Proxy(cfg, dev, "POST", "/input/tap", JsonBody($"{{\"x\":{x},\"y\":{y}}}"), null);
            }
            case "swipe": case "drag":
            {
                if (rest.Length < 4 || !TryNorm(rest[0], out var x1) || !TryNorm(rest[1], out var y1)
                    || !TryNorm(rest[2], out var x2) || !TryNorm(rest[3], out var y2))
                { Console.Error.WriteLine("Usage: BUGPUNCH <device> swipe <x1> <y1> <x2> <y2> [durationMs]"); return 1; }
                int ms = rest.Length > 4 && int.TryParse(rest[4], out var d) ? d : 300;
                return Proxy(cfg, dev, "POST", "/input/swipe", JsonBody($"{{\"x1\":{x1},\"y1\":{y1},\"x2\":{x2},\"y2\":{y2},\"duration\":{ms}}}"), null);
            }
            case "memsnap": case "snapshot": case "memory":
                return MemSnap(cfg, dev, rest);
            case "memstats": case "mem":
                return Proxy(cfg, dev, "GET", "/memory/stats", null, null, saveAs: ("memstats", rest.FirstOrDefault()));
            case "get":
            {
                if (rest.Length == 0) { Console.Error.WriteLine("Usage: BUGPUNCH <device> get </path?query>"); return 1; }
                return Proxy(cfg, dev, "GET", NormPath(rest[0]), null, OutArg(rest));
            }
            case "post":
            {
                if (rest.Length == 0) { Console.Error.WriteLine("Usage: BUGPUNCH <device> post </path> [json | @file]"); return 1; }
                string payload = ReadPayload(rest.Skip(1).ToArray(), joinWithSpaces: true);
                HttpContent content = string.IsNullOrEmpty(payload) ? null
                    : payload.TrimStart().StartsWith("{") || payload.TrimStart().StartsWith("[")
                        ? JsonBody(payload)
                        : new StringContent(payload, Encoding.UTF8, "text/plain");
                return Proxy(cfg, dev, "POST", NormPath(rest[0]), content, null, timeoutSec: 180);
            }
            default:
                Console.Error.WriteLine($"Unknown BUGPUNCH device subcommand: {args[0]}");
                PrintBugpunchUsage();
                return 1;
        }
    }

    static string OutArg(string[] rest)
    {
        for (int i = 0; i < rest.Length - 1; i++) if (rest[i] == "--out" || rest[i] == "-o") return rest[i + 1];
        return null;
    }

    static StringContent JsonBody(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    static string NormPath(string p) => p.StartsWith("/") ? p : "/" + p;

    static bool TryNorm(string s, out string norm)
    {
        norm = null;
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (v < 0 || v > 1) return false;
        norm = v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>`@file` reads the file; anything else is the literal payload (args re-joined, since Main split on spaces).</summary>
    static string ReadPayload(string[] parts, bool joinWithSpaces)
    {
        if (parts.Length == 0) return null;
        if (parts.Length == 1 && parts[0].StartsWith("@"))
        {
            string f = parts[0].Substring(1).Trim('"');
            if (!File.Exists(f)) { Console.Error.WriteLine($"Error: file not found: {f}"); return null; }
            return File.ReadAllText(f);
        }
        string joined = joinWithSpaces ? string.Join(" ", parts) : parts[0];
        // A bare existing path also counts (mirrors CODE_EXEC's file-path detection).
        if (joined.Length < 260 && File.Exists(joined)) return File.ReadAllText(joined);
        return joined;
    }

    static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.Length == 0 ? "device" : sb.ToString();
    }

    /// <summary>
    /// One call through /api/v1/devices/:id/proxy/&lt;path&gt;. JSON is pretty-printed, images are
    /// written to disk (the path is the output — a model can read a file, not a byte stream),
    /// and device-side errors keep their status so `$?` means something in a script.
    /// </summary>
    /// <summary>Raw call through the proxy: status, body bytes, content type. No printing.</summary>
    static (int status, byte[] bytes, string ctype) Call(BpConfig cfg, BpDevice dev, string method, string path, HttpContent content, int timeoutSec)
    {
        using var http = dev.DirectUrl != null ? new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) } : Http(cfg, timeoutSec);
        string url = dev.DirectUrl != null
            ? dev.DirectUrl + path
            : $"{cfg.Server}/api/v1/devices/{Uri.EscapeDataString(dev.Id)}/proxy{path}";
        var req = new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
        var resp = http.SendAsync(req).GetAwaiter().GetResult();
        var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        return ((int)resp.StatusCode, bytes, resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    static int Proxy(BpConfig cfg, BpDevice dev, string method, string path, HttpContent content, string outFile, int timeoutSec = 60, (string kind, string label)? saveAs = null)
    {
        var sw = Stopwatch.StartNew();
        var (status, bytes, ctype) = Call(cfg, dev, method, path, content, timeoutSec);

        if (status == 403)
        {
            string b = Encoding.UTF8.GetString(bytes);
            Console.Error.WriteLine($"Error 403: {ExtractError(b)}");
            if (b.Contains("not_controller"))
                Console.Error.WriteLine("  → another user holds the Remote IDE controller lock on this device (dashboard). Ask them to release it, or wait ~30s after they close the tab.");
            else if (b.Contains("consent"))
                Console.Error.WriteLine("  → the device wants on-screen consent; internal devices normally auto-accept — check the tester's role.");
            else
                Console.Error.WriteLine("  → the token lacks device-control scope. Mint one with 'Device control (admin)' ticked, then BUGPUNCH auth <token>.");
            return 1;
        }
        if (status == 404)
        {
            Console.Error.WriteLine($"Error 404: {ExtractError(Encoding.UTF8.GetString(bytes))}");
            Console.Error.WriteLine("  → device gone offline, not internal-role, or the RPC path doesn't exist in this SDK build.");
            return 10;
        }
        if (status == 401)
        {
            Console.Error.WriteLine("Error 401: token rejected — run BUGPUNCH auth <token> again.");
            return 1;
        }

        if (ctype.StartsWith("image/") || (outFile != null && !ctype.StartsWith("application/json") && !ctype.StartsWith("text/")))
        {
            outFile ??= DeviceSession.Current.Artifact("screenshot", null, ctype.Contains("png") ? ".png" : ".jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            File.WriteAllBytes(outFile, bytes);
            Console.WriteLine($"{Path.GetFullPath(outFile)}  ({bytes.Length / 1024} KB, {ctype}, {sw.ElapsedMilliseconds} ms)");
            DeviceSession.Add("screenshot", Path.GetFullPath(outFile));
            return status >= 200 && status < 300 ? 0 : 1;
        }

        string text = Encoding.UTF8.GetString(bytes);
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            File.WriteAllText(outFile, text);
            Console.WriteLine($"{Path.GetFullPath(outFile)}  ({bytes.Length / 1024} KB)");
        }
        else
        {
            string shown = ctype.StartsWith("application/json") ? Pretty(text) : text;
            Console.WriteLine(shown);
            // Big or script-produced bodies also land in the session dir so they can be re-read
            // without another round trip to the device.
            if (saveAs != null || shown.Length > 4000)
            {
                string kind = saveAs?.kind ?? path.Trim('/').Split('?')[0].Replace('/', '-');
                string f = DeviceSession.Current.Artifact(kind, saveAs?.label, ".txt");
                File.WriteAllText(f, shown);
                DeviceSession.Add(kind, f, $"{shown.Length / 1024} KB");
            }
        }
        Console.Error.WriteLine($"[BUGPUNCH] {method} {path} → {status} in {sw.ElapsedMilliseconds} ms ({dev.Label})");
        return status >= 200 && status < 300 ? 0 : 1;
    }

    // ─── Memory snapshots: capture on device, pull here, analyse offline ──
    //
    // The SDK takes a Unity Memory Profiler .snap (/memory/snapshot, Development Build only) and
    // keeps it in persistentDataPath. It is hundreds of MB, so it comes back in 1 MB chunks via
    // /memory/download (resumable; one huge base64 frame is what used to kill the tunnel), then
    // MEMSNAP summarises it right here — no Unity needed to read the result.

    static int MemSnap(BpConfig cfg, BpDevice dev, string[] rest)
    {
        string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "take";
        bool analyze = !rest.Contains("--no-analyze");
        int waitSec = 600;
        for (int i = 0; i < rest.Length - 1; i++) if (rest[i] == "--wait") int.TryParse(rest[i + 1], out waitSec);

        if (sub == "list")
            return Proxy(cfg, dev, "GET", "/memory/list", null, null);
        if (sub == "delete")
        {
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: BUGPUNCH <device> memsnap delete <devicePath>"); return 1; }
            return Proxy(cfg, dev, "POST", $"/memory/delete?path={Uri.EscapeDataString(rest[1])}", null, null);
        }
        if (sub == "pull")
        {
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: BUGPUNCH <device> memsnap pull <devicePath>  (paths from: memsnap list)"); return 1; }
            return PullSnapshot(cfg, dev, rest[1], analyze);
        }
        if (sub != "take" && !sub.StartsWith("-")) { Console.Error.WriteLine("Usage: BUGPUNCH <device> memsnap [take] [--wait sec] [--no-analyze] | list | pull <path> | delete <path>"); return 1; }

        var (st, body, _) = Call(cfg, dev, "POST", "/memory/snapshot", null, 60);
        string text = Encoding.UTF8.GetString(body);
        if (st != 200 || !JsonBool(text, "ok"))
        {
            Console.Error.WriteLine($"Error: snapshot refused ({st}): {ExtractError(text)}");
            if (text.Contains("Development Build")) Console.Error.WriteLine("  → full snapshots need a Development Build (Build Settings → Development Build).");
            else if (text.Contains("budget")) Console.Error.WriteLine("  → device is full of old captures: BUGPUNCH <dev> memsnap list, then memsnap delete <path>.");
            return 1;
        }
        string devicePath = JsonStr(text, "path");
        Console.Error.WriteLine($"[BUGPUNCH] snapshot started on device → {devicePath}");
        var sw = Stopwatch.StartNew();
        string state = JsonStr(text, "state");
        while (state != "done")
        {
            if (sw.Elapsed.TotalSeconds > waitSec) { Console.Error.WriteLine($"Error: snapshot still not done after {waitSec}s (BUGPUNCH <dev> memsnap list later, then memsnap pull <path>)."); return 14; }
            Thread.Sleep(2000);
            var (s2, b2, _) = Call(cfg, dev, "GET", "/memory/status", null, 30);
            string t2 = Encoding.UTF8.GetString(b2);
            state = s2 == 200 ? JsonStr(t2, "state") : "unreachable";
            if (state == "failed") { Console.Error.WriteLine($"Error: snapshot failed on device: {ExtractError(t2)}"); return 1; }
            if (state == "unreachable") { Console.Error.WriteLine($"Error: device stopped answering ({s2}) — a capture can freeze a low-memory phone; check it is still running."); return 10; }
            Console.Error.Write($"\r[BUGPUNCH] capturing… {sw.Elapsed.TotalSeconds:F0}s   ");
            if (state == "done") { devicePath = JsonStr(t2, "path") ?? devicePath; Console.Error.WriteLine(); }
        }
        return PullSnapshot(cfg, dev, devicePath, analyze);
    }

    static int PullSnapshot(BpConfig cfg, BpDevice dev, string devicePath, bool analyze)
    {
        string name = Path.GetFileName(devicePath.Replace('\\', '/'));
        string local = DeviceSession.Current.Artifact("memsnap", Path.GetFileNameWithoutExtension(name), ".snap");
        var sw = Stopwatch.StartNew();
        if (!PullChunks(cfg, dev, devicePath, local, out long size)) return 1;
        DeviceSession.Add("snapshot", local, $"{size / (1024 * 1024)} MB");
        // The .json context beside it (scene, build, memory counters at capture time) is small — always grab it.
        string ctxLocal = Path.ChangeExtension(local, ".snap.json");
        if (PullChunks(cfg, dev, devicePath + ".json", ctxLocal, out _, quiet: true)) DeviceSession.Add("context", ctxLocal);
        Console.WriteLine($"{local}  ({size / (1024 * 1024)} MB in {sw.Elapsed.TotalSeconds:F1}s)");
        if (!analyze) return 0;
        Console.WriteLine();
        int code = MemorySnapshot.Run($"\"{local}\" summary");
        Console.Error.WriteLine($"[BUGPUNCH] more: clibridge4unity MEMSNAP \"{local}\" types|objects|labels|allocators  — or diff against an earlier one");
        return code;
    }

    static bool PullChunks(BpConfig cfg, BpDevice dev, string devicePath, string local, out long size, bool quiet = false)
    {
        size = 0;
        const int chunk = 1024 * 1024;
        long offset = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(local));
        using var fs = new FileStream(local, FileMode.Create, FileAccess.Write);
        int failures = 0;
        while (true)
        {
            var (st, body, _) = Call(cfg, dev, "GET", $"/memory/download?path={Uri.EscapeDataString(devicePath)}&offset={offset}&length={chunk}", null, 120);
            string text = Encoding.UTF8.GetString(body);
            if (offset == 0 && (st == 404 || st == 501))
            {
                // SDK predates /memory/download (< 0.8.223): the only path is /files/read, one
                // base64 frame for the whole file — what the dashboard did before. Big captures may
                // not survive the tunnel; the size check below catches a truncated one.
                if (!quiet) Console.Error.WriteLine("[BUGPUNCH] SDK has no chunked download — pulling in one frame via /files/read (slow; large captures may fail)");
                var (s2, b2, _) = Call(cfg, dev, "GET", $"/files/read?path={Uri.EscapeDataString(devicePath)}&maxBytes={2L * 1024 * 1024 * 1024}", null, 900);
                string t2 = Encoding.UTF8.GetString(b2);
                if (s2 != 200 || !JsonBool(t2, "ok")) { if (!quiet) Console.Error.WriteLine($"Error: /files/read failed ({s2}): {ExtractError(t2)}"); return false; }
                using var d2 = JsonDocument.Parse(t2);
                var r2 = d2.RootElement;
                if (r2.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True) { if (!quiet) Console.Error.WriteLine("Error: device truncated the read."); return false; }
                byte[] all = r2.TryGetProperty("encoding", out var enc) && enc.GetString() == "utf-8"
                    ? Encoding.UTF8.GetBytes(r2.GetProperty("content").GetString())
                    : r2.GetProperty("content").GetBytesFromBase64();
                fs.Write(all, 0, all.Length);
                size = r2.TryGetProperty("size", out var sz) ? sz.GetInt64() : all.Length;
                offset = all.Length;
                break;
            }
            if (st != 200 || !JsonBool(text, "ok"))
            {
                if (st >= 500 || st == 0) { if (++failures <= 5) { Thread.Sleep(1500); continue; } }
                if (!quiet) Console.Error.WriteLine($"Error: download failed at offset {offset} ({st}): {ExtractError(text)}");
                return false;
            }
            failures = 0;
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            size = r.GetProperty("size").GetInt64();
            var data = r.GetProperty("content").GetBytesFromBase64();
            fs.Write(data, 0, data.Length);
            offset += data.Length;
            if (!quiet && size > 0) Console.Error.Write($"\r[BUGPUNCH] pulling {Path.GetFileName(devicePath)}  {offset / (1024 * 1024)}/{size / (1024 * 1024)} MB   ");
            if (r.GetProperty("eof").GetBoolean() || data.Length == 0) break;
        }
        if (!quiet) Console.Error.WriteLine();
        if (offset != size) { if (!quiet) Console.Error.WriteLine($"Error: got {offset} bytes, device reports {size}"); return false; }
        return true;
    }

    static bool JsonBool(string json, string key)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True; } catch { return false; }
    }
    static string JsonStr(string json, string key)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; } catch { return null; }
    }

    static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.ToString();
        }
        catch { }
        return Truncate(body, 300);
    }

    static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // The SDK's /run answer is a JSON string of the output — unwrap it so the result reads
            // like console output rather than an escaped blob.
            if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString();
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch { return json; }
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", "").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
