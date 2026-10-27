using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace clibridge4unity;

/// <summary>
/// <c>WORKTREE create|remove|list</c> and the Claude Code <c>WorktreeCreate</c>/<c>WorktreeRemove</c>
/// hook adapters (<c>HOOK worktree-create</c>, <c>HOOK worktree-remove</c>). A worktree is a second
/// checkout of the project on another branch, made beside the launch checkout, so an agent can work
/// there while the launch checkout keeps the Unity editor; the copy carries the marker that
/// <see cref="Worktree"/> reads.
///
/// Plastic: a regular workspace with cloaked rules (images, models, audio, archives and whatever
/// <c>ProjectSettings/clibridge4unity.json</c> adds), so the copy holds code, prefabs, scenes and
/// <c>.meta</c> files only. Git: <c>git worktree add</c>. Both: the branch must already exist (no
/// branch is ever created on a shared repository), a branch whose project folder or root holds a
/// <c>.git</c> is refused on Plastic (Claude Code refuses such a worktree), removal refuses pending
/// or new files, and an existing copy on the same branch is reused.
///
/// Configuration, all optional, under <c>"worktree"</c> in <c>ProjectSettings/clibridge4unity.json</c>:
/// <c>root</c> (folder for copies, relative paths against the checkout root; default a
/// <c>&lt;checkout&gt;_worktrees</c> sibling), <c>namePrefix</c> (Plastic workspace names, default
/// <c>wt_</c>), <c>cloaked</c> (rules added to <see cref="DefaultCloaked"/>), <c>cloakedDefaults</c>
/// (false drops the built-in rules), <c>hooks</c> (register Claude's hooks on SETUP; default true for
/// Plastic, false for git, where Claude's own worktrees already work and are recognised).
/// </summary>
internal static class WorktreeCommands
{
    public const string HookCreateCommand = "clibridge4unity HOOK worktree-create";
    public const string HookRemoveCommand = "clibridge4unity HOOK worktree-remove";
    const int HookCreateTimeoutSeconds = 900;
    const int HookRemoveTimeoutSeconds = 120;

    /// <summary>Plastic cloaked rules every Unity project wants: the asset types that weigh and that
    /// no offline command reads. Everything the lint and index pipeline needs stays: <c>.cs</c>,
    /// <c>.asmdef</c>, <c>.dll</c>, <c>.meta</c>, <c>.prefab</c>, <c>.unity</c>, <c>.mat</c>, <c>.asset</c>.</summary>
    public static readonly string[] DefaultCloaked =
    {
        "*.png", "*.jpg", "*.jpeg", "*.tga", "*.tif", "*.tiff", "*.psd", "*.exr", "*.hdr", "*.gif", "*.bmp", "*.dds", "*.cubemap",
        "*.fbx", "*.obj", "*.blend", "*.spm",
        "*.wav", "*.mp3", "*.ogg", "*.aif", "*.aiff", "*.mp4", "*.mov", "*.webm",
        "*.unitypackage", "*.zip", "*.rar", "*.7z", "*.pdf",
        "*.bundle", "*.so", "*.a", "*.aar", "*.srcaar", "*.jar", "*.exe",
    };

    public sealed class Config
    {
        public string Root;
        public string NamePrefix = "wt_";
        public List<string> Cloaked = new();
        public bool CloakedDefaults = true;
        public bool? Hooks;
    }

    sealed class Fail : Exception { public Fail(string message) : base(message) { } }

    enum Vcs { None, Plastic, Git }

    // ─── Entry points ────────────────────────────────────────────────

    /// <summary><c>WORKTREE create &lt;branch&gt; [--adopt &lt;workspace&gt;] | remove [&lt;path&gt;] | list</c>.</summary>
    public static int Run(string projectPath, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            switch (sub)
            {
                case "create":
                {
                    string name = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                    if (name == null) throw new Fail("usage: WORKTREE create <branch> [--adopt <workspace path>]");
                    string adopt = null;
                    for (int i = 2; i < args.Length - 1; i++)
                        if (args[i].Equals("--adopt", StringComparison.OrdinalIgnoreCase)) adopt = args[i + 1];
                    var here = Worktree.Read(projectPath);
                    if (here != null && here.IsCopy)
                        throw new Fail($"this is already a worktree of {here.Origin}; create worktrees from the origin");
                    string path = Create(name, projectPath, adopt);
                    Console.WriteLine($"Worktree ready: {path}");
                    Console.WriteLine($"Claude Code: EnterWorktree name={name.TrimStart('/')} enters it (the hook reuses this checkout).");
                    return 0;
                }
                case "remove":
                {
                    string path = args.Length > 1 ? Path.GetFullPath(args[1]) : projectPath;
                    Remove(path);
                    Console.WriteLine($"Removed worktree {path}");
                    return 0;
                }
                case "list":
                    return List(projectPath);
                default:
                    Console.Error.WriteLine("Usage: WORKTREE create <branch> [--adopt <workspace path>]");
                    Console.Error.WriteLine("       WORKTREE remove [<worktree project path>]");
                    Console.Error.WriteLine("       WORKTREE list");
                    return 2;
            }
        }
        catch (Fail f)
        {
            Console.Error.WriteLine($"worktree: {f.Message}");
            return 1;
        }
    }

    /// <summary>Claude Code's WorktreeCreate hook: reads <c>{name, cwd}</c> from stdin and prints the
    /// worktree path as the last line of stdout; any failure exits 1 with the reason on stderr.</summary>
    public static int HookCreate()
    {
        try
        {
            using var doc = JsonDocument.Parse(Console.In.ReadToEnd());
            string name = Str(doc.RootElement, "name");
            string cwd = Str(doc.RootElement, "cwd");
            if (string.IsNullOrWhiteSpace(name)) throw new Fail("the hook payload has no worktree name");
            if (string.IsNullOrWhiteSpace(cwd)) throw new Fail("the hook payload has no cwd");
            Console.Out.WriteLine(Create(name, Path.GetFullPath(cwd), adopt: null));
            return 0;
        }
        catch (Fail f) { Console.Error.WriteLine($"worktree: {f.Message}"); return 1; }
        catch (JsonException ex) { Console.Error.WriteLine($"worktree: the hook payload is not JSON ({ex.Message})"); return 1; }
    }

    /// <summary>Claude Code's WorktreeRemove hook: reads <c>{worktree_path}</c> from stdin; exit 0
    /// means removed, exit 1 with the reason on stderr leaves the worktree in place.</summary>
    public static int HookRemove()
    {
        try
        {
            using var doc = JsonDocument.Parse(Console.In.ReadToEnd());
            string path = Str(doc.RootElement, "worktree_path");
            if (string.IsNullOrWhiteSpace(path)) throw new Fail("the hook payload has no worktree_path");
            Remove(Path.GetFullPath(path));
            return 0;
        }
        catch (Fail f) { Console.Error.WriteLine($"worktree: {f.Message}"); return 1; }
        catch (JsonException ex) { Console.Error.WriteLine($"worktree: the hook payload is not JSON ({ex.Message})"); return 1; }
    }

    /// <summary>SETUP step: registers the two hooks in <c>.claude/settings.json</c> when the project's
    /// configuration asks for them (default: Plastic yes, git no). An existing registration of another
    /// command for the same event is left alone and reported.</summary>
    public static void InstallHooks(string projectPath)
    {
        var (vcs, _) = DetectVcs(projectPath);
        var cfg = LoadConfig(projectPath);
        bool wanted = cfg.Hooks ?? vcs == Vcs.Plastic;
        if (!wanted)
        {
            Console.WriteLine(vcs == Vcs.Git
                ? "[--] Worktree hooks not registered: Claude Code's own git worktrees are recognised as copies. Set \"worktree\": {\"hooks\": true} in ProjectSettings/clibridge4unity.json to route them through clibridge4unity."
                : "[--] Worktree hooks not registered (no Plastic workspace or git repository above the project).");
            return;
        }
        string settingsPath = Path.Combine(projectPath, ".claude", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            JsonObject root = File.Exists(settingsPath) && File.ReadAllText(settingsPath).Trim().Length > 0
                ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
                : new JsonObject();
            if (root == null) { Console.WriteLine("[!!] .claude/settings.json is not a JSON object; worktree hooks not registered"); return; }
            var hooks = root["hooks"] as JsonObject;
            if (hooks == null) root["hooks"] = hooks = new JsonObject();
            bool changed = false;
            var notes = new List<string>();
            foreach (var (eventName, command, timeout) in new[]
            {
                ("WorktreeCreate", HookCreateCommand, HookCreateTimeoutSeconds),
                ("WorktreeRemove", HookRemoveCommand, HookRemoveTimeoutSeconds),
            })
            {
                var groups = hooks[eventName] as JsonArray;
                if (groups == null) hooks[eventName] = groups = new JsonArray();
                JsonObject ours = null;
                string other = null;
                string verb = command.Substring(command.IndexOf("HOOK ", StringComparison.Ordinal));
                foreach (var group in groups.OfType<JsonObject>())
                    foreach (var hook in (group["hooks"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        string cmd = hook["command"]?.GetValue<string>();
                        if (cmd == null) continue;
                        // A registration by full path to some clibridge4unity build (a dev exe) is ours too,
                        // and is brought back to the PATH-resolved command.
                        if (cmd.Contains("clibridge4unity", StringComparison.OrdinalIgnoreCase) && cmd.Contains(verb, StringComparison.OrdinalIgnoreCase)) ours = hook;
                        else other ??= cmd;
                    }
                if (ours != null)
                {
                    if (ours["command"]?.GetValue<string>() != command) { ours["command"] = command; changed = true; }
                    if (ours["timeout"]?.GetValue<int>() != timeout) { ours["timeout"] = timeout; changed = true; }
                    continue;
                }
                if (other != null)
                {
                    notes.Add($"[!!] {eventName} already runs `{other}`; remove it from .claude/settings.json to use clibridge4unity's worktrees");
                    continue;
                }
                groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout }),
                });
                changed = true;
            }
            if (changed)
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                    root.WriteTo(writer);
                File.WriteAllText(settingsPath, Encoding.UTF8.GetString(ms.ToArray()) + "\n");
                Console.WriteLine("[OK] Claude Code worktree hooks registered (WorktreeCreate / WorktreeRemove -> clibridge4unity)");
            }
            else if (notes.Count == 0)
                Console.WriteLine("[OK] Claude Code worktree hooks already registered");
            foreach (var n in notes) Console.WriteLine(n);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not register worktree hooks ({ex.Message})");
        }
    }

    // ─── Configuration and detection ─────────────────────────────────

    public static Config LoadConfig(string projectPath)
    {
        var cfg = new Config();
        string path = Path.Combine(projectPath, "ProjectSettings", "clibridge4unity.json");
        if (!File.Exists(path)) return cfg;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("worktree", out var w) || w.ValueKind != JsonValueKind.Object) return cfg;
            cfg.Root = Str(w, "root");
            cfg.NamePrefix = Str(w, "namePrefix") ?? cfg.NamePrefix;
            if (w.TryGetProperty("cloaked", out var cloaked) && cloaked.ValueKind == JsonValueKind.Array)
                foreach (var e in cloaked.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())) cfg.Cloaked.Add(e.GetString());
            if (w.TryGetProperty("cloakedDefaults", out var cd) && cd.ValueKind == JsonValueKind.False) cfg.CloakedDefaults = false;
            if (w.TryGetProperty("hooks", out var h) && (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False))
                cfg.Hooks = h.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"worktree: ignoring unreadable {path} ({ex.Message})");
        }
        return cfg;
    }

    /// <summary>The version control holding <paramref name="startDir"/>: a <c>.plastic</c> folder at
    /// any level wins over a <c>.git</c>, because a Plastic checkout can carry a stray <c>.git</c>
    /// (the TOR repository did) while a git repository never contains a <c>.plastic</c>.</summary>
    static (Vcs vcs, string root) DetectVcs(string startDir)
    {
        string gitRoot = null;
        string dir = Norm(startDir);
        for (int i = 0; i < 20 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".plastic"))) return (Vcs.Plastic, dir);
            if (gitRoot == null && (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))) gitRoot = dir;
            dir = Path.GetDirectoryName(dir);
        }
        return gitRoot != null ? (Vcs.Git, gitRoot) : (Vcs.None, null);
    }

    static string RootFolder(Config cfg, string vcsRoot)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Root))
            return Norm(Path.IsPathRooted(cfg.Root) ? cfg.Root : Path.Combine(vcsRoot, cfg.Root));
        return Path.Combine(Path.GetDirectoryName(vcsRoot) ?? vcsRoot, Path.GetFileName(vcsRoot) + "_worktrees");
    }

    // ─── Create ──────────────────────────────────────────────────────

    /// <summary>Makes (or reuses) the worktree for <paramref name="name"/> beside the checkout that
    /// holds <paramref name="cwd"/> and returns its project folder, the same relative folder as
    /// <paramref name="cwd"/> is in its checkout.</summary>
    static string Create(string name, string cwd, string adopt)
    {
        name = name.Trim().Replace('\\', '/').TrimStart('/');
        if (name.Length == 0 || name.Length > 64 || name.Split('/').Any(seg => seg.Length == 0 || seg == "." || seg == ".." || seg.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))))
            throw new Fail($"'{name}' is not a valid worktree name: letters, digits, '.', '_' and '-' in '/'-separated segments, at most 64 characters");
        cwd = Norm(cwd);
        var (vcs, vcsRoot) = DetectVcs(cwd);
        if (vcs == Vcs.None) throw new Fail($"{cwd} is in neither a Plastic workspace nor a git repository");
        var cfg = LoadConfig(cwd);
        string root = RootFolder(cfg, vcsRoot);
        string slug = name.Replace("/", "--");
        string rel = cwd.Length > vcsRoot.Length ? cwd.Substring(vcsRoot.Length).TrimStart('\\', '/') : "";

        string project = vcs == Vcs.Plastic
            ? CreatePlastic(name, cwd, vcsRoot, rel, root, slug, cfg, adopt)
            : CreateGit(name, cwd, vcsRoot, rel, root, slug);

        // settings.json is personal (permissions, hooks) and usually not versioned, so a fresh copy
        // has none; a versioned settings.local.json is already there and must not be overwritten.
        try
        {
            string src = Path.Combine(cwd, ".claude", "settings.json");
            string dst = Path.Combine(project, ".claude", "settings.json");
            if (File.Exists(src) && !File.Exists(dst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"worktree: could not copy .claude/settings.json ({ex.Message})"); }
        return project;
    }

    static string CreatePlastic(string name, string cwd, string ws, string rel, string root, string slug, Config cfg, string adopt)
    {
        string wsWin = ws;
        var gwp = Cm($"gwp \"{wsWin}\" --format={{wkpath}}");
        if (gwp.code != 0 || string.IsNullOrWhiteSpace(gwp.stdout)) throw new Fail($"{ws} is not a Plastic workspace (cm gwp: {gwp.stderr.Trim()})");
        ws = Norm(gwp.stdout.Trim());
        string selector = FirstLine(Cm($"wi \"{ws}\"").stdout);
        int space = selector.IndexOf(' ');
        int at = space < 0 ? -1 : selector.IndexOf('@', space);
        if (at < 0) throw new Fail($"cannot read the repository of {ws} (cm wi: {selector})");
        string repo = selector.Substring(at + 1).Trim();
        string branch = "/" + name;

        string leaf = name.Substring(name.LastIndexOf('/') + 1);
        var find = Cm($"find \"branch where name='{leaf}' on repository '{repo}'\" --format={{name}} --nototal");
        bool exists = find.stdout.Split('\n').Select(l => l.Trim()).Any(l => string.Equals(l, branch, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            throw new Fail($"branch {branch} does not exist in {repo}. Create it first (cm branch create {branch}); worktrees never create branches on a shared repository");

        foreach (string entry in new[] { "/" + (rel.Length > 0 ? rel.Replace('\\', '/') + "/" : "") + ".git", "/.git" })
        {
            var ls = Cm($"ls \"{entry}\" --tree=\"br:{branch}@{repo}\" --format={{path}}");
            if (ls.code == 0 && string.Equals(FirstLine(ls.stdout), entry, StringComparison.OrdinalIgnoreCase))
                throw new Fail($"{branch} contains {entry}, and Claude Code refuses a worktree whose .git it cannot verify; merge the change that removes it from Plastic, then retry");
        }

        string requestFile = Path.Combine(root, ".requests", slug);
        if (adopt == null && File.Exists(requestFile))
        {
            adopt = FirstLine(File.ReadAllText(requestFile));
            try { File.Delete(requestFile); } catch { }
        }

        string dir, kind;
        if (!string.IsNullOrWhiteSpace(adopt))
        {
            var adoptGwp = Cm($"gwp \"{Norm(adopt)}\" --format={{wkpath}}");
            if (adoptGwp.code != 0 || string.IsNullOrWhiteSpace(adoptGwp.stdout)) throw new Fail($"{adopt} is not a Plastic workspace");
            dir = Norm(adoptGwp.stdout.Trim());
            if (Same(dir, ws)) throw new Fail("the workspace to adopt is the one this command runs in");
            string current = BranchOf(dir);
            if (!string.Equals(current, branch, StringComparison.OrdinalIgnoreCase))
                throw new Fail($"{dir} is on {current ?? "a changeset or label"}, not {branch}; switch it first (cm switch br:{branch}@{repo} --workspace=\"{dir}\")");
            kind = "existing";
        }
        else
        {
            dir = Path.Combine(root, slug);
            SweepHusk(dir);
            if (Directory.Exists(dir))
            {
                var there = Cm($"gwp \"{dir}\" --format={{wkpath}}");
                if (there.code != 0 || !Same(Norm(there.stdout.Trim()), dir))
                    throw new Fail($"{dir} exists but is not a Plastic workspace; move it aside");
                string current = BranchOf(dir);
                if (!string.Equals(current, branch, StringComparison.OrdinalIgnoreCase))
                    throw new Fail($"{dir} is on {current ?? "a changeset or label"}, not {branch}");
            }
            else
            {
                Directory.CreateDirectory(root);
                string wkName = cfg.NamePrefix + slug;
                var create = Cm($"workspace create \"{wkName}\" \"{dir}\" \"{repo}\"");
                if (create.code != 0) throw new Fail($"cm could not create workspace {wkName} at {dir}: {Trim(create.stderr + create.stdout)}");
                // Rules must be in place before the first update, or the cloaked files download.
                File.WriteAllText(Path.Combine(dir, "cloaked.conf"), CloakedConf(cfg));
                Console.Error.WriteLine($"worktree: loading {branch} into {dir} (cloaked: code, prefabs, scenes and .meta only)");
                var sw = Stopwatch.StartNew();
                var sw2 = Cm($"switch \"br:{branch}@{repo}\" --workspace=\"{dir}\" --noinput --silent", timeoutMs: 6 * 60 * 60 * 1000);
                if (sw2.code != 0) throw new Fail($"cm could not load {branch} into {dir}: {Trim(sw2.stderr + sw2.stdout)}");
                Console.Error.WriteLine($"worktree: loaded in {sw.Elapsed.TotalSeconds:F0}s");
            }
            kind = "cloaked";
        }

        string project = rel.Length > 0 ? Path.Combine(dir, rel) : dir;
        for (int i = 0; i < 60 && !Directory.Exists(project); i++) Thread.Sleep(1000);
        if (!Directory.Exists(project)) throw new Fail($"{project} did not appear; does {branch} contain {rel}?");

        Worktree.Write(new Worktree.Info
        {
            ProjectPath = project, Origin = cwd, Editor = kind == "existing", Vcs = "plastic",
            Branch = branch, Kind = kind, Workspace = dir, CreatedBy = "clibridge4unity WORKTREE",
        });
        return project;
    }

    static string CreateGit(string name, string cwd, string repoRoot, string rel, string root, string slug)
    {
        var top = Git(repoRoot, "rev-parse --show-toplevel");
        if (top.code != 0) throw new Fail($"{repoRoot} is not a git repository ({Trim(top.stderr)})");
        repoRoot = Norm(top.stdout.Trim());
        string branch = name;
        bool local = Git(repoRoot, $"rev-parse --verify --quiet \"refs/heads/{branch}\"").code == 0;
        bool remote = !local && Git(repoRoot, $"rev-parse --verify --quiet \"refs/remotes/origin/{branch}\"").code == 0;
        if (!local && !remote)
            throw new Fail($"branch {branch} does not exist locally or on origin. Create it first (git branch {branch}); worktrees never create branches");

        string dir = Path.Combine(root, slug);
        SweepHusk(dir);
        if (Directory.Exists(dir))
        {
            var list = Git(repoRoot, "worktree list --porcelain");
            bool registered = list.stdout.Split('\n').Any(l => l.StartsWith("worktree ", StringComparison.Ordinal) && Same(Norm(l.Substring(9).Trim()), dir));
            if (!registered) throw new Fail($"{dir} exists but is not a worktree of {repoRoot}; move it aside");
            string current = FirstLine(Git(dir, "rev-parse --abbrev-ref HEAD").stdout);
            if (!string.Equals(current, branch, StringComparison.Ordinal))
                throw new Fail($"{dir} is on {current}, not {branch}");
        }
        else
        {
            Directory.CreateDirectory(root);
            var add = local
                ? Git(repoRoot, $"worktree add \"{dir}\" \"{branch}\"", timeoutMs: 30 * 60 * 1000)
                : Git(repoRoot, $"worktree add --track -b \"{branch}\" \"{dir}\" \"origin/{branch}\"", timeoutMs: 30 * 60 * 1000);
            if (add.code != 0) throw new Fail($"git could not add a worktree at {dir}: {Trim(add.stderr + add.stdout)}");
        }

        // The marker must not show up as an untracked file in the copy.
        try
        {
            var common = Git(repoRoot, "rev-parse --git-common-dir");
            string commonDir = common.stdout.Trim();
            if (!Path.IsPathRooted(commonDir)) commonDir = Path.Combine(repoRoot, commonDir);
            string exclude = Path.Combine(commonDir, "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(exclude));
            string[] lines = File.Exists(exclude) ? File.ReadAllLines(exclude) : Array.Empty<string>();
            if (!lines.Any(l => l.Trim() == ".clibridge4unity/" || l.Trim() == ".clibridge4unity"))
                File.AppendAllText(exclude, (lines.Length > 0 && !string.IsNullOrEmpty(lines[^1]) ? "\n" : "") + ".clibridge4unity/\n");
        }
        catch (Exception ex) { Console.Error.WriteLine($"worktree: could not exclude .clibridge4unity in git ({ex.Message})"); }

        string project = rel.Length > 0 ? Path.Combine(dir, rel) : dir;
        if (!Directory.Exists(project)) throw new Fail($"{project} does not exist; does {branch} contain {rel}?");
        Worktree.Write(new Worktree.Info
        {
            ProjectPath = project, Origin = cwd, Editor = false, Vcs = "git",
            Branch = branch, Kind = "git", Workspace = dir, CreatedBy = "clibridge4unity WORKTREE",
        });
        return project;
    }

    // ─── Remove ──────────────────────────────────────────────────────

    static void Remove(string projectPath)
    {
        projectPath = Norm(projectPath);
        var info = Worktree.Read(projectPath);
        if (info == null || info.Kind == "git-linked")
            throw new Fail($"{projectPath} has no {Worktree.MarkerPath(projectPath)}, so clibridge4unity did not make it; leaving it alone");
        switch (info.Kind)
        {
            case "existing":
                try { File.Delete(info.MarkerPath); } catch { }
                Console.Error.WriteLine($"worktree: left {info.Workspace} in place, on its branch");
                return;
            case "cloaked":
                RemovePlastic(info);
                return;
            case "git":
                RemoveGit(info);
                return;
            default:
                throw new Fail($"unknown worktree kind '{info.Kind}' in {info.MarkerPath}; remove it by hand");
        }
    }

    static void RemovePlastic(Worktree.Info info)
    {
        string ws = Norm(info.Workspace ?? throw new Fail($"{info.MarkerPath} names no workspace"));
        var gwp = Cm($"gwp \"{ws}\" --format={{wkpath}}");
        if (gwp.code != 0 || !Same(Norm(gwp.stdout.Trim()), ws)) throw new Fail($"{ws} is no longer a Plastic workspace; remove it by hand");
        string wkName = FirstLine(Cm($"gwp \"{ws}\" --format={{wkname}}").stdout);
        string prefix = LoadConfig(info.OriginExists ? info.Origin : info.ProjectPath).NamePrefix;
        if (!wkName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !wkName.StartsWith("wt_", StringComparison.OrdinalIgnoreCase) && !wkName.StartsWith("tor_wt_", StringComparison.OrdinalIgnoreCase))
            throw new Fail($"workspace '{wkName}' was not made by clibridge4unity (expected the prefix '{prefix}'); leaving it alone");
        if (IsReparsePoint(ws)) throw new Fail($"{ws} is a junction or link; remove it by hand");

        var status = Cm($"status \"{ws}\" --short --controlledchanged --changed --localdeleted --localmoved --private", timeoutMs: 10 * 60 * 1000);
        if (status.code != 0) throw new Fail($"cm status failed in {ws}; leaving it in place ({Trim(status.stderr)})");
        // cloaked.conf is private by design, and the settings.json copied from the origin is the
        // bridge's own doing: neither is the user's work.
        var pending = status.stdout.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !Same(l, Path.Combine(ws, "cloaked.conf")) && !IsCopiedSettings(info, l))
            .ToList();
        if (pending.Count > 0)
            throw new Fail($"{ws} has pending changes or new files; check them in, undo or delete them, then remove it again:\n  " + string.Join("\n  ", pending.Take(20)));

        // cm may ask to confirm and repeats the question endlessly on an empty stdin; it gets its
        // answer and a deadline either way.
        var del = Cm($"workspace delete \"{ws}\"", timeoutMs: 120_000, stdin: "Y\n");
        var listed = Cm("workspace list --format={path}").stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        if (listed.Any(p => Same(Norm(p), ws)))
            throw new Fail($"cm could not delete the workspace {ws}: {Trim(del.stderr + del.stdout)}");
        DeleteTree(ws);
    }

    static void RemoveGit(Worktree.Info info)
    {
        string dir = Norm(info.Workspace ?? throw new Fail($"{info.MarkerPath} names no worktree folder"));
        if (IsReparsePoint(dir)) throw new Fail($"{dir} is a junction or link; remove it by hand");
        var status = Git(dir, "status --porcelain --untracked-files=all");
        if (status.code != 0) throw new Fail($"git status failed in {dir}; leaving it in place ({Trim(status.stderr)})");
        // The settings.json copied from the origin is the bridge's own doing, not the user's work.
        var pending = status.stdout.Split('\n').Where(l => l.Trim().Length > 0)
            .Where(l => !(l.StartsWith("?? ", StringComparison.Ordinal) && IsCopiedSettings(info, Path.Combine(dir, l.Substring(3).Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)))))
            .ToList();
        if (pending.Count > 0)
            throw new Fail($"{dir} has uncommitted changes or untracked files; commit, stash or delete them, then remove it again:\n  " + string.Join("\n  ", pending.Take(20)));
        // git refuses to remove a worktree holding an untracked file, so the copied settings.json goes first.
        string copied = Path.Combine(info.ProjectPath, ".claude", "settings.json");
        if (IsCopiedSettings(info, copied))
        {
            try
            {
                File.Delete(copied);
                string claudeDir = Path.GetDirectoryName(copied);
                if (!Directory.EnumerateFileSystemEntries(claudeDir).Any()) Directory.Delete(claudeDir);
            }
            catch { }
        }
        var common = Git(dir, "rev-parse --git-common-dir");
        string commonDir = common.stdout.Trim();
        if (!Path.IsPathRooted(commonDir)) commonDir = Path.Combine(dir, commonDir);
        string mainRoot = Path.GetDirectoryName(Norm(commonDir)) ?? dir;
        var rm = Git(mainRoot, $"worktree remove \"{dir}\"", timeoutMs: 10 * 60 * 1000);
        if (rm.code != 0) throw new Fail($"git could not remove the worktree {dir}: {Trim(rm.stderr + rm.stdout)}");
        Git(mainRoot, "worktree prune");
        DeleteTree(dir);
    }

    // ─── List ────────────────────────────────────────────────────────

    static int List(string projectPath)
    {
        var (vcs, vcsRoot) = DetectVcs(projectPath);
        if (vcs == Vcs.None) { Console.WriteLine("Not inside a Plastic workspace or git repository."); return 1; }
        var here = Worktree.Read(projectPath);
        string origin = here != null && here.IsCopy && here.OriginExists ? here.Origin : Norm(projectPath);
        var (_, originVcsRoot) = DetectVcs(origin);
        originVcsRoot ??= vcsRoot;
        string root = RootFolder(LoadConfig(origin), originVcsRoot);
        string rel = origin.Length > originVcsRoot.Length ? origin.Substring(originVcsRoot.Length).TrimStart('\\', '/') : "";
        Console.WriteLine($"origin: {origin}");
        Console.WriteLine($"root:   {root}");
        int count = 0;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string project = rel.Length > 0 ? Path.Combine(dir, rel) : dir;
                var info = Worktree.Read(project) ?? (rel.Length > 0 ? Worktree.Read(dir) : null);
                if (info == null) { Console.WriteLine($"  {Path.GetFileName(dir)}  (no worktree marker)"); continue; }
                count++;
                Console.WriteLine($"  {Path.GetFileName(dir)}  {info.Branch ?? "?"}  [{info.Kind ?? "?"}{(info.Editor ? ", own editor" : "")}]  {info.ProjectPath}");
            }
        }
        if (count == 0) Console.WriteLine("  (no worktrees)");
        return 0;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    static string CloakedConf(Config cfg)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by clibridge4unity WORKTREE: these never download into this worktree.\r\n");
        if (cfg.CloakedDefaults) foreach (var r in DefaultCloaked) sb.Append(r).Append("\r\n");
        foreach (var r in cfg.Cloaked) sb.Append(r.Trim()).Append("\r\n");
        return sb.ToString();
    }

    static string BranchOf(string ws)
    {
        string selector = FirstLine(Cm($"wi \"{ws}\"").stdout);
        if (!selector.StartsWith("Branch ", StringComparison.Ordinal)) return null;
        string spec = selector.Substring("Branch ".Length);
        int at = spec.IndexOf('@');
        return at < 0 ? spec : spec.Substring(0, at);
    }

    /// <summary>A removed worktree can leave empty directories behind: the agent's shell still holds
    /// the project folder as its working directory while the remove hook runs, so that folder and
    /// its parents survive the delete. A leftover with no files is not someone's work.</summary>
    static void SweepHusk(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any()) return;
            if (IsReparsePoint(dir)) return;
            Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"worktree: removed the worktree but could not delete every folder under {dir} ({ex.Message}); empty leftovers are swept by the next create");
        }
    }

    /// <summary>True when <paramref name="absPath"/> is the copy's <c>.claude/settings.json</c> and
    /// its content equals the origin's, i.e. the file <see cref="Create"/> copied there.</summary>
    static bool IsCopiedSettings(Worktree.Info info, string absPath)
    {
        try
        {
            string mine = Path.Combine(info.ProjectPath, ".claude", "settings.json");
            if (!Same(Norm(absPath), mine) || !info.OriginExists) return false;
            string theirs = Path.Combine(info.Origin, ".claude", "settings.json");
            return File.Exists(mine) && File.Exists(theirs) && File.ReadAllText(mine) == File.ReadAllText(theirs);
        }
        catch { return false; }
    }

    static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    static (int code, string stdout, string stderr) Cm(string args, int timeoutMs = 120_000, string stdin = null)
        => Exec("cm", args, timeoutMs, stdin);

    static (int code, string stdout, string stderr) Git(string workDir, string args, int timeoutMs = 120_000)
        => Exec("git", $"-C \"{workDir}\" {args}", timeoutMs);

    static (int code, string stdout, string stderr) Exec(string exe, string args, int timeoutMs, string stdin = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            var so = new StringBuilder(); var se = new StringBuilder();
            int lines = 0;
            // Output is capped: a prompt that repeats forever on an unexpected answer must not fill memory.
            p.OutputDataReceived += (_, e) => { if (e.Data != null && lines++ < 2000) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && lines++ < 2000) se.AppendLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (stdin != null)
            {
                try { p.StandardInput.Write(stdin); p.StandardInput.Close(); } catch { }
            }
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, so.ToString(), se.ToString() + $"\n(timed out after {timeoutMs / 1000}s)");
            }
            p.WaitForExit();
            return (p.ExitCode, so.ToString(), se.ToString());
        }
        catch (Exception ex)
        {
            throw new Fail($"could not run {exe} ({ex.Message}); is it on PATH?");
        }
    }

    static string Str(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int nl = s.IndexOf('\n');
        return (nl < 0 ? s : s.Substring(0, nl)).Trim();
    }

    static string Trim(string s) => (s ?? "").Trim();

    static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    static bool Same(string a, string b) => string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
