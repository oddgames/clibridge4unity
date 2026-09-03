using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace clibridge4unity;

/// <summary>
/// Asking permission before disturbing someone else's play session.
///
/// One editor is shared by a person and several agent windows. When an agent needs to do
/// something that would disrupt a play session it does not own, it files a request here and
/// waits; the owner answers, and the answer decides what actually happens.
///
/// Three answers, because the useful ones are not "yes/no":
///
///   yield    stop play mode and hand the editor over — the agent needs edit mode
///   run-now  run it inside the live session, leaving play mode alone. CODE_EXEC carries
///            its own compiler and does not need edit mode, so this is often what you want
///            and costs the owner nothing
///   deny     leave everything alone
///
/// File-based like PeerLedger, for the same reason: the participants are separate processes
/// that come and go, with no shared runtime to hold state in. A request is a file; a decision
/// is a field written into it. Nothing is lost if either side dies.
/// </summary>
internal static class PlayGate
{
    /// <summary>Long enough for a person to notice and answer; short enough not to hang an agent.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(90);

    /// <summary>Stale requests are swept so an unattended prompt does not accumulate.</summary>
    static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>Owner value meaning a person pressed Play, rather than any agent.</summary>
    public const string OwnerUser = "user";

    /// <summary>Sentinel session id for a grant that outlives any single play session.</summary>
    public const long AnySession = -1;

    /// <summary>Answer meaning "run it, and stop asking me for this window entirely".</summary>
    public const string Always = "always";

    public const string Yield = "yield";
    public const string RunNow = "run-now";
    public const string Deny = "deny";

    static string Dir(string projectPath)
    {
        string dir = Path.Combine(projectPath, ".clibridge4unity", "requests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public sealed class Request
    {
        public string Id { get; set; }
        public string From { get; set; }          // requesting window's peer id
        /// <summary>
        /// The caller's anchor process. A request whose asker has exited is moot — nobody is
        /// waiting for the answer — so it is swept rather than left prompting the owner.
        /// </summary>
        public int FromPid { get; set; }
        public string Command { get; set; }
        public string Data { get; set; }
        public string Owner { get; set; }         // who holds play mode: "user" or an agent id
        public string Created { get; set; }       // ISO-8601 UTC
        public string Decision { get; set; }      // null until answered
        public string DecidedAt { get; set; }

        public DateTime CreatedUtc =>
            DateTime.TryParse(Created, CultureInfo.InvariantCulture,
                              DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.UtcNow;

        /// <summary>What the prompt should say — short enough for a notification body.</summary>
        public string Summary()
        {
            string what = string.IsNullOrWhiteSpace(Data)
                ? Command
                : Command + " " + (Data.Length > 60 ? Data.Substring(0, 57) + "..." : Data);
            return what.Replace("\r", " ").Replace("\n", " ");
        }
    }

    // Hand-rolled JSON, not JsonSerializer: this app publishes trimmed with reflection-based
    // serialization disabled, so Serialize/Deserialize throw at runtime. The failure is
    // especially nasty here because the reads are defensively wrapped — an exception would
    // surface as "no pending requests" rather than an error.
    static string PathFor(string projectPath, string id) =>
        Path.Combine(Dir(projectPath), id + ".json");

    static string Serialize(Request r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("Id", r.Id ?? "");
            w.WriteString("From", r.From ?? "");
            w.WriteNumber("FromPid", r.FromPid);
            w.WriteString("Command", r.Command ?? "");
            w.WriteString("Data", r.Data ?? "");
            w.WriteString("Owner", r.Owner ?? "");
            w.WriteString("Created", r.Created ?? "");
            if (string.IsNullOrEmpty(r.Decision)) w.WriteNull("Decision");
            else w.WriteString("Decision", r.Decision);
            if (string.IsNullOrEmpty(r.DecidedAt)) w.WriteNull("DecidedAt");
            else w.WriteString("DecidedAt", r.DecidedAt);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    static Request Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Get(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        return new Request
        {
            Id = Get("Id"),
            From = Get("From"),
            FromPid = root.TryGetProperty("FromPid", out var pid) && pid.TryGetInt32(out int p) ? p : 0,
            Command = Get("Command"),
            Data = Get("Data"),
            Owner = Get("Owner"),
            Created = Get("Created"),
            Decision = Get("Decision"),
            DecidedAt = Get("DecidedAt"),
        };
    }

    /// <summary>File a request and return its id. Does not wait.</summary>
    public static Request File_(string projectPath, string command, string data, string owner)
    {
        Sweep(projectPath);
        var req = new Request
        {
            Id = DateTime.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4),
            From = PeerLedger.SelfId,
            FromPid = PeerLedger.AnchorPid,
            Command = command,
            Data = data,
            Owner = owner,
            Created = DateTime.UtcNow.ToString("o"),
        };
        Write(projectPath, req);
        return req;
    }

    static void Write(string projectPath, Request req)
    {
        try
        {
            System.IO.File.WriteAllText(PathFor(projectPath, req.Id), Serialize(req));
        }
        catch { }
    }

    public static Request Read(string projectPath, string id)
    {
        try
        {
            string path = PathFor(projectPath, id);
            if (!System.IO.File.Exists(path)) return null;
            return Deserialize(System.IO.File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static List<Request> Pending(string projectPath)
    {
        var list = new List<Request>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(Dir(projectPath), "*.json"))
            {
                try
                {
                    var req = Deserialize(System.IO.File.ReadAllText(file));
                    if (req == null || !string.IsNullOrEmpty(req.Decision)) continue;
                    if (DateTime.UtcNow - req.CreatedUtc >= MaxAge) continue;
                    // The asker has gone: nobody is waiting, so do not prompt for it.
                    if (req.FromPid > 0 && !IsAlive(req.FromPid))
                    {
                        TryDelete(file);
                        continue;
                    }
                    list.Add(req);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderBy(r => r.CreatedUtc).ToList();
    }

    /// <summary>Answer a request. Returns false if it is gone or already answered.</summary>
    public static bool Decide(string projectPath, string id, string decision)
    {
        var req = Read(projectPath, id);
        if (req == null || !string.IsNullOrEmpty(req.Decision)) return false;
        req.Decision = decision;
        req.DecidedAt = DateTime.UtcNow.ToString("o");
        Write(projectPath, req);
        return true;
    }

    /// <summary>
    /// Block until answered or the wait runs out. Returns the decision, or null on timeout.
    /// Polling a file rather than a signal: the answer can come from a notification button,
    /// another CLI process, or an editor window, and none of them share a handle with us.
    /// </summary>
    public static string Await(string projectPath, string id, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var req = Read(projectPath, id);
            if (req == null) return null;
            if (!string.IsNullOrEmpty(req.Decision)) return req.Decision;
            Thread.Sleep(250);
        }
        return null;
    }

    // ---------------------------------------------------------------- grants
    //
    // Asking once per command is unusable: a single task can issue a dozen mutating commands,
    // and the owner is not going to answer a dozen dialogs. A grant records "this window may
    // proceed for THIS play session", so the question is asked once and then honoured.
    //
    // Scoped to (requesting window, play session). `since` is the play session's start tick
    // from the heartbeat, so leaving play mode and re-entering invalidates every grant — a
    // permission given for one session never silently carries into the next.

    static string GrantPath(string projectPath, string peerId)
    {
        string dir = Path.Combine(projectPath, ".clibridge4unity", "grants");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, peerId + ".txt");
    }

    /// <summary>Remember an answer for the rest of this play session.</summary>
    public static void StoreGrant(string projectPath, string owner, long since, string decision)
    {
        try
        {
            System.IO.File.WriteAllText(GrantPath(projectPath, PeerLedger.SelfId),
                                        $"{owner}\t{since}\t{decision}");
        }
        catch { }
    }

    /// <summary>The standing answer for this window in this play session, or null.</summary>
    public static string FindGrant(string projectPath, string owner, long since)
    {
        try
        {
            string path = GrantPath(projectPath, PeerLedger.SelfId);
            if (!System.IO.File.Exists(path)) return null;
            var parts = System.IO.File.ReadAllText(path).Split('\t');
            if (parts.Length < 3) return null;
            if (!long.TryParse(parts[1], out long grantedSince)) return null;
            // AnySession is a standing "always allow" and ignores who owns play mode.
            if (grantedSince == AnySession) return parts[2];
            // Otherwise the grant is valid only for the exact session it was given in.
            if (!string.Equals(parts[0], owner, StringComparison.OrdinalIgnoreCase)) return null;
            if (grantedSince != since) return null;
            return parts[2];
        }
        catch { return null; }
    }

    /// <summary>Revoke this window's standing permission. Returns false if there was none.</summary>
    public static bool ClearGrant(string projectPath)
    {
        try
        {
            string path = GrantPath(projectPath, PeerLedger.SelfId);
            if (!System.IO.File.Exists(path)) return false;
            System.IO.File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Describe the standing permission for this window, or null.</summary>
    public static string DescribeGrant(string projectPath)
    {
        try
        {
            string path = GrantPath(projectPath, PeerLedger.SelfId);
            if (!System.IO.File.Exists(path)) return null;
            var parts = System.IO.File.ReadAllText(path).Split('\t');
            if (parts.Length < 3) return null;
            bool always = long.TryParse(parts[1], out long s) && s == AnySession;
            return always
                ? $"always allow ({parts[2]}) — until revoked"
                : $"{parts[2]} — for the current play session only";
        }
        catch { return null; }
    }

    /// <summary>Same liveness convention as PeerLedger: if the pid cannot be opened, it is gone.</summary>
    static bool IsAlive(int pid)
    {
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { }
    }

    public static void Cancel(string projectPath, string id)
    {
        try { System.IO.File.Delete(PathFor(projectPath, id)); } catch { }
    }

    static void Sweep(string projectPath)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Dir(projectPath), "*.json"))
            {
                try
                {
                    if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(file) > MaxAge)
                        System.IO.File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Commands that only observe. Everything else is treated as capable of changing
    /// something in the editor and is gated while someone else owns play mode.
    ///
    /// Deliberately an allowlist of the harmless ones rather than a list of dangerous ones:
    /// a new command added later should default to asking, not to silently mutating a
    /// session someone is in the middle of.
    /// </summary>
    static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        // liveness / state
        "PING", "PROBE", "DIAG", "STATUS", "HELP", "VERSION", "BRIDGEINFO", "PLAYMODE",
        "WINDOWS", "LAST", "SESSIONS", "PEERS",
        // reading the project
        "LOG", "EDITORLOG", "EDITORLOGS", "ELOG", "FIND", "INSPECTOR",
        "ASSET_SEARCH", "ASSET_DISCOVER", "UI_DISCOVER", "SCREENSHOT",
        // offline analysis — never touches the editor at all
        "ANALYZE", "CODE_ANALYZE", "CODE_SEARCH", "MAP", "LINT", "RELEASENOTES",
        "UNITYNOTES", "RELNOTES", "PROFILE",
        // the gate itself
        "REQUESTS", "ALLOW", "DENY",
    };

    /// <summary>Would this change something in the editor?</summary>
    public static bool NeedsGate(string cmdUpper) => !ReadOnly.Contains(cmdUpper);
}
