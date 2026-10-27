using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace clibridge4unity;

/// <summary>
/// A worktree is a second copy of a Unity project that an agent works in while the original
/// checkout (the <c>origin</c>) keeps the Unity editor and the populated <c>Library</c>. The copy
/// is marked by <c>.clibridge4unity/worktree.json</c> in its project folder, written by whatever
/// made the copy:
/// <code>
/// { "origin": "C:\\proj\\UnityProj", "editor": false, "vcs": "plastic", "branch": "/main/x", "createdBy": "..." }
/// </code>
/// <c>origin</c> is the origin's project folder (the one with <c>Assets</c> and <c>Library</c>),
/// not its repository root. <c>editor: true</c> declares a copy that has an editor and Library of
/// its own, which the CLI then treats like any other project.
///
/// In a copy without an editor: commands that need the bridge pipe or target an editor fail at
/// once and name the origin (nothing will ever serve that pipe), the Roslyn daemon does not
/// autostart (it still starts on demand for ANALYZE / MAP / LINT), and every <c>Library</c>
/// lookup in the lint and index pipeline resolves to the origin's, so <c>LINT unity</c> compiles
/// the copy's sources against real ScriptAssemblies and resolved packages.
///
/// A linked git worktree (a <c>.git</c> file pointing into the main repository's
/// <c>.git/worktrees/</c>) is recognised without a marker: its origin is the same project folder
/// under the main checkout, and it counts as a copy until it grows a <c>Library/ScriptAssemblies</c>
/// of its own, which means someone opened Unity on it.
/// </summary>
internal static class Worktree
{
    public sealed class Info
    {
        public string ProjectPath;
        public string MarkerPath;
        /// <summary>The origin's project folder, normalised; null when the marker names none.</summary>
        public string Origin;
        public bool Editor;
        public string Vcs;
        public string Branch;
        public string CreatedBy;
        /// <summary>How the copy was made: <c>cloaked</c> (Plastic workspace with cloaked rules),
        /// <c>existing</c> (an adopted full workspace), <c>git</c> (a worktree the bridge added),
        /// <c>git-linked</c> (a git worktree recognised without a marker), or whatever the writer chose.</summary>
        public string Kind;
        /// <summary>The copy's checkout root (Plastic workspace or git worktree), when known.</summary>
        public string Workspace;

        public bool IsCopy => !Editor;
        public bool OriginExists => Origin != null && Directory.Exists(Origin);
    }

    public static string MarkerPath(string projectPath)
        => Path.Combine(projectPath, ".clibridge4unity", "worktree.json");

    sealed class Entry { public DateTime Mtime; public Info Info; }
    static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The marker of <paramref name="projectPath"/>, or null when the project is not a
    /// worktree (no marker, or one that cannot be parsed). Re-read when the marker changes, so a
    /// long-lived daemon sees a marker written after it started.</summary>
    public static Info Read(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        string full;
        try { full = Path.GetFullPath(projectPath).TrimEnd('\\', '/'); } catch { return null; }
        string marker = MarkerPath(full);
        DateTime mtime;
        try { mtime = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue; }
        catch { mtime = DateTime.MinValue; }
        if (_cache.TryGetValue(full, out var cached) && cached.Mtime == mtime) return cached.Info;
        var info = mtime == DateTime.MinValue ? DetectGitLinked(full) : Parse(full, marker);
        _cache[full] = new Entry { Mtime = mtime, Info = info };
        return info;
    }

    /// <summary>Writes <paramref name="info"/> as the marker of <paramref name="info"/>.ProjectPath.</summary>
    public static void Write(Info info)
    {
        string marker = MarkerPath(info.ProjectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(marker));
        var sb = new System.Text.StringBuilder();
        sb.Append("{\n");
        void Field(string key, string value, bool last = false)
        {
            if (value == null) return;
            sb.Append("  \"").Append(key).Append("\": \"").Append(JsonEncodedText.Encode(value).ToString()).Append(last ? "\"\n" : "\",\n");
        }
        Field("origin", info.Origin);
        sb.Append("  \"editor\": ").Append(info.Editor ? "true" : "false").Append(",\n");
        Field("vcs", info.Vcs);
        Field("branch", info.Branch);
        Field("kind", info.Kind);
        Field("workspace", info.Workspace);
        Field("createdBy", info.CreatedBy ?? "clibridge4unity", last: true);
        sb.Append("}\n");
        File.WriteAllText(marker, sb.ToString());
        _cache.TryRemove(Path.GetFullPath(info.ProjectPath).TrimEnd('\\', '/'), out _);
    }

    /// <summary>Recognises a linked git worktree with no marker. The <c>.git</c> entry nearest
    /// above the project is a file naming a gitdir under the main repository's
    /// <c>.git/worktrees/</c>; the origin is the same relative project folder under that
    /// repository. The copy has an editor once it has its own <c>Library/ScriptAssemblies</c>.</summary>
    static Info DetectGitLinked(string projectPath)
    {
        try
        {
            string dir = projectPath;
            for (int i = 0; i < 15 && dir != null; i++)
            {
                string dotGit = Path.Combine(dir, ".git");
                if (Directory.Exists(dotGit)) return null;
                if (File.Exists(dotGit))
                {
                    string line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
                    if (line == null) return null;
                    string gitDir = line.Substring("gitdir:".Length).Trim();
                    if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(dir, gitDir));
                    gitDir = gitDir.TrimEnd('\\', '/');
                    string worktreesDir = Path.GetDirectoryName(gitDir);
                    if (worktreesDir == null || !string.Equals(Path.GetFileName(worktreesDir), "worktrees", StringComparison.OrdinalIgnoreCase)) return null;
                    string mainGitDir = Path.GetDirectoryName(worktreesDir);
                    string mainRoot = mainGitDir == null ? null : Path.GetDirectoryName(mainGitDir);
                    if (mainRoot == null) return null;
                    string rel = projectPath.Length > dir.Length ? projectPath.Substring(dir.Length).TrimStart('\\', '/') : "";
                    string origin = Path.GetFullPath(Path.Combine(mainRoot, rel)).TrimEnd('\\', '/');
                    if (string.Equals(origin, projectPath, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(origin)) return null;
                    string branch = null;
                    try
                    {
                        string head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                        branch = head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head.Substring("ref: refs/heads/".Length) : head;
                    }
                    catch { }
                    return new Info
                    {
                        ProjectPath = projectPath,
                        MarkerPath = dotGit,
                        Origin = origin,
                        Editor = Directory.Exists(Path.Combine(projectPath, "Library", "ScriptAssemblies")),
                        Vcs = "git",
                        Branch = branch,
                        Kind = "git-linked",
                        Workspace = dir,
                        CreatedBy = "git worktree",
                    };
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    static Info Parse(string projectPath, string marker)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string Str(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string origin = Str("origin");
            if (!string.IsNullOrWhiteSpace(origin))
            {
                try { origin = Path.GetFullPath(origin).TrimEnd('\\', '/'); } catch { }
                if (string.Equals(origin, projectPath, StringComparison.OrdinalIgnoreCase)) origin = null;
            }
            else origin = null;
            return new Info
            {
                ProjectPath = projectPath,
                MarkerPath = marker,
                Origin = origin,
                Editor = root.TryGetProperty("editor", out var editor) && editor.ValueKind == JsonValueKind.True,
                Vcs = Str("vcs"),
                Branch = Str("branch"),
                CreatedBy = Str("createdBy"),
                Kind = Str("kind"),
                Workspace = Str("workspace"),
            };
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="projectPath"/> is a worktree copy with no editor of its own.</summary>
    public static bool IsCopy(string projectPath) => Read(projectPath)?.IsCopy == true;

    /// <summary>The project whose <c>Library</c> serves <paramref name="projectPath"/>: the origin for
    /// a copy whose origin exists on disk, otherwise the project itself. Returned unchanged (not
    /// normalised) in the latter case, so paths built from it are identical to before.</summary>
    public static string LibraryOwner(string projectPath)
    {
        var info = Read(projectPath);
        return info != null && info.IsCopy && info.OriginExists ? info.Origin : projectPath;
    }

    /// <summary><c>Library</c> of the project that <see cref="LibraryOwner"/> names.</summary>
    public static string LibraryRoot(string projectPath) => Path.Combine(LibraryOwner(projectPath), "Library");

    /// <summary>A path under <see cref="LibraryRoot"/>, e.g. <c>LibraryPath(p, "PackageCache")</c>.</summary>
    public static string LibraryPath(string projectPath, params string[] parts)
    {
        string path = LibraryRoot(projectPath);
        foreach (var part in parts) path = Path.Combine(path, part);
        return path;
    }

    /// <summary>True when <paramref name="projectPath"/>'s lookups go to another project's <c>Library</c>.</summary>
    public static bool LibraryIsBorrowed(string projectPath)
        => !ReferenceEquals(LibraryOwner(projectPath), projectPath);

    /// <summary>Notes for a lint report run in a copy: where <c>Library</c> comes from, an origin that
    /// is missing, and package manifests that differ from the origin's (then the origin's resolved
    /// packages may not match the copy's sources). Empty for an ordinary project.</summary>
    public static List<string> LintNotes(string projectPath)
    {
        var notes = new List<string>();
        var info = Read(projectPath);
        if (info == null || !info.IsCopy) return notes;
        if (info.Origin == null)
        {
            notes.Add($"Worktree copy whose marker names no origin ({info.MarkerPath}); using this copy's own Library.");
            return notes;
        }
        if (!info.OriginExists)
        {
            notes.Add($"Worktree copy of {info.Origin}, which does not exist; using this copy's own Library.");
            return notes;
        }
        notes.Add($"Worktree copy of {info.Origin}: Library (ScriptAssemblies, PackageCache) borrowed from there.");
        foreach (var rel in new[] { Path.Combine("Packages", "manifest.json"), Path.Combine("Packages", "packages-lock.json") })
        {
            if (!SameText(Path.Combine(projectPath, rel), Path.Combine(info.Origin, rel)))
                notes.Add($"{rel.Replace('\\', '/')} differs from the origin's, so package references and defines may not match this copy.");
        }
        return notes;
    }

    static bool SameText(string a, string b)
    {
        try
        {
            bool ea = File.Exists(a), eb = File.Exists(b);
            if (!ea || !eb) return ea == eb;
            return File.ReadAllText(a).Replace("\r\n", "\n") == File.ReadAllText(b).Replace("\r\n", "\n");
        }
        catch { return true; }
    }

    /// <summary>The message for a command refused because <paramref name="info"/> describes a copy
    /// without an editor.</summary>
    public static string EditorRefusal(string cmdUpper, Info info)
    {
        string where = info.Origin ?? "the project this copy was made from";
        string branch = string.IsNullOrEmpty(info.Branch) ? "" : $" ({info.Branch})";
        string how = info.Kind == "git-linked" ? "a linked git worktree" : "a worktree copy";
        return $"Error: {cmdUpper} needs a Unity editor, and none runs here.\n" +
               $"       This is {how}{branch} of {where}; see {info.MarkerPath}.\n" +
               $"       Run editor commands from the origin after switching it to this branch.\n" +
               $"       Offline commands work here: ANALYZE, MAP, LINT, LINT unity (against the origin's Library)." +
               (cmdUpper == "OPEN" ? "\n       'OPEN force' opens this copy anyway; Unity then imports the whole project into its own Library." : "");
    }
}
