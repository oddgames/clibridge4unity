using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace clibridge4unity;

/// <summary>
/// Background-built index of the SERIALIZED side of a Unity project — the wiring that lives
/// in YAML (.unity/.prefab/.asset/...) rather than C#. Code-only analysis can't answer
/// "which scenes actually use this script?" because those links are stored as GUID references
/// in asset files. This index makes them instant:
///   - guid ↔ path (from .meta files)
///   - script attach sites (m_Script guid refs per scene/prefab/SO asset)
///   - cross-asset references (any guid occurrence per container)
///   - UnityEvent persistent calls (m_TargetAssemblyTypeName + m_MethodName pairs)
///   - GameObject names per container (for MAP keyword matching)
/// Built once in the daemon after source parse, updated incrementally via the existing
/// FileSystemWatcher. Line-scan only — no YAML parse, so big scenes stay cheap.
/// </summary>
internal sealed class AssetGraph
{
    sealed class Container
    {
        public string Path;                                  // project-relative, forward slashes
        public HashSet<string> RefGuids = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ScriptAttach = new(StringComparer.OrdinalIgnoreCase); // script guid -> count
        public List<(string TypeName, string Method)> EventCalls = new();
        public HashSet<string> ObjectNames = new(StringComparer.OrdinalIgnoreCase);          // m_Name values
    }

    static readonly Regex GuidRx = new(@"guid: ([0-9a-fA-F]{32})", RegexOptions.Compiled);

    // YAML containers scanned line-by-line for attach sites / refs / events / names.
    static readonly HashSet<string> YamlExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim",
        ".overrideController", ".playable", ".mixer", ".lighting"
    };
    // Text containers scanned for guid refs only (USS url(...guid=...), UXML src attrs).
    static readonly HashSet<string> TextRefExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uxml", ".uss", ".tss"
    };
    // Input System action assets (JSON) — action/map names indexed for MAP keyword matching,
    // so "MAP jump input" surfaces the .inputactions asset defining a "Jump" action.
    static readonly HashSet<string> JsonNameExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".inputactions"
    };
    static readonly Regex JsonNameRx = new(@"""name""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    readonly string _projectPath;
    readonly ConcurrentDictionary<string, string> _guidToPath = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _pathToGuid = new(StringComparer.OrdinalIgnoreCase); // rel path -> guid
    readonly ConcurrentDictionary<string, Container> _containers = new(StringComparer.OrdinalIgnoreCase); // rel path -> info
    long _buildMs;
    volatile bool _ready;

    public bool Ready => _ready;
    public int ContainerCount => _containers.Count;
    public int GuidCount => _guidToPath.Count;
    public long BuildMs => _buildMs;

    public AssetGraph(string projectPath) => _projectPath = projectPath;

    /// <summary>True if a watcher event on this path should update the graph.</summary>
    public static bool IsGraphFile(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
        string ext = Path.GetExtension(fullPath);
        return YamlExts.Contains(ext) || TextRefExts.Contains(ext) || JsonNameExts.Contains(ext);
    }

    // ─── Build ───────────────────────────────────────────────────────

    /// <summary>Scan .meta files (guid map) and container assets. Parallel; safe to call once.</summary>
    public void Build()
    {
        var sw = Stopwatch.StartNew();

        // .meta scan covers PackageCache too — user scenes can attach scripts that live in
        // UPM packages, and resolving those attach sites needs the script's guid.
        var metaRoots = new[]
        {
            Path.Combine(_projectPath, "Assets"),
            Path.Combine(_projectPath, "Packages"),
            Path.Combine(_projectPath, "Library", "PackageCache"),
        };
        var metaFiles = new List<string>();
        foreach (var root in metaRoots)
        {
            if (!Directory.Exists(root)) continue;
            try { metaFiles.AddRange(Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)); }
            catch { }
        }
        Parallel.ForEach(metaFiles, meta => { try { ScanMeta(meta); } catch { } });

        // Containers: user-editable assets only. PackageCache scenes/prefabs are third-party
        // noise for "where is this wired?" questions.
        var containerRoots = new[]
        {
            Path.Combine(_projectPath, "Assets"),
            Path.Combine(_projectPath, "Packages"),
        };
        var containerFiles = new List<string>();
        foreach (var root in containerRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f);
                    if (YamlExts.Contains(ext) || TextRefExts.Contains(ext) || JsonNameExts.Contains(ext))
                        containerFiles.Add(f);
                }
            }
            catch { }
        }
        Parallel.ForEach(containerFiles, file => { try { ScanContainer(file); } catch { } });

        sw.Stop();
        Interlocked.Exchange(ref _buildMs, sw.ElapsedMilliseconds);
        _ready = true;
    }

    /// <summary>Incremental update from the daemon's FileSystemWatcher. Caller debounces.</summary>
    public void OnFileEvent(string fullPath, bool deleted, string oldFullPath = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(oldFullPath)) RemovePath(oldFullPath);
            if (deleted) { RemovePath(fullPath); return; }

            if (fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) { ScanMeta(fullPath); return; }
            string ext = Path.GetExtension(fullPath);
            if (YamlExts.Contains(ext) || TextRefExts.Contains(ext) || JsonNameExts.Contains(ext))
                ScanContainer(fullPath);
        }
        catch { }
    }

    void RemovePath(string fullPath)
    {
        if (fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            string assetRel = ToRel(fullPath.Substring(0, fullPath.Length - ".meta".Length));
            if (_pathToGuid.TryRemove(assetRel, out string guid))
                _guidToPath.TryRemove(guid, out _);
            return;
        }
        _containers.TryRemove(ToRel(fullPath), out _);
    }

    void ScanMeta(string metaPath)
    {
        // guid is in the first few lines of every .meta — read just the head.
        string guid = null;
        using (var reader = new StreamReader(metaPath))
        {
            for (int i = 0; i < 5; i++)
            {
                string line = reader.ReadLine();
                if (line == null) break;
                var m = GuidRx.Match(line);
                if (m.Success) { guid = m.Groups[1].Value; break; }
            }
        }
        if (guid == null) return;
        string assetRel = ToRel(metaPath.Substring(0, metaPath.Length - ".meta".Length));
        _guidToPath[guid] = assetRel;
        _pathToGuid[assetRel] = guid;
    }

    void ScanContainer(string fullPath)
    {
        string rel = ToRel(fullPath);
        string ext = Path.GetExtension(fullPath);
        var c = new Container { Path = rel };

        if (TextRefExts.Contains(ext))
        {
            // UXML/USS — guid refs only.
            string text = File.ReadAllText(fullPath);
            foreach (Match m in GuidRx.Matches(text)) c.RefGuids.Add(m.Groups[1].Value);
            _containers[rel] = c;
            return;
        }
        if (JsonNameExts.Contains(ext))
        {
            // .inputactions — index action/map names so task keywords find the input binding.
            string text = File.ReadAllText(fullPath);
            foreach (Match m in JsonNameRx.Matches(text))
                if (c.ObjectNames.Count < 400) c.ObjectNames.Add(m.Groups[1].Value);
            _containers[rel] = c;
            return;
        }

        using var stream = new StreamReader(fullPath);
        // Binary-serialized assets (LightingData, terrain, force-binary projects) can't be line-scanned.
        string first = stream.ReadLine();
        if (first == null || !first.StartsWith("%YAML", StringComparison.Ordinal)) return;

        string pendingEventType = null;
        string line;
        while ((line = stream.ReadLine()) != null)
        {
            string t = line.TrimStart();

            if (t.StartsWith("m_Script:", StringComparison.Ordinal))
            {
                var m = GuidRx.Match(t);
                if (m.Success)
                {
                    string g = m.Groups[1].Value;
                    c.RefGuids.Add(g);
                    c.ScriptAttach.TryGetValue(g, out int n);
                    c.ScriptAttach[g] = n + 1;
                }
                continue;
            }
            if (t.StartsWith("m_Name:", StringComparison.Ordinal))
            {
                string name = t.Substring("m_Name:".Length).Trim();
                if (name.Length > 0 && c.ObjectNames.Count < 400) c.ObjectNames.Add(name);
                continue;
            }
            if (t.StartsWith("m_TargetAssemblyTypeName:", StringComparison.Ordinal))
            {
                string v = t.Substring("m_TargetAssemblyTypeName:".Length).Trim();
                int comma = v.IndexOf(',');
                if (comma > 0) v = v.Substring(0, comma);
                pendingEventType = v.Length > 0 ? v : null;
                continue;
            }
            if (t.StartsWith("m_MethodName:", StringComparison.Ordinal))
            {
                string method = t.Substring("m_MethodName:".Length).Trim();
                if (method.Length > 0 && pendingEventType != null && c.EventCalls.Count < 200)
                    c.EventCalls.Add((pendingEventType, method));
                continue;
            }
            if (t.Contains("guid:"))
            {
                foreach (Match m in GuidRx.Matches(t)) c.RefGuids.Add(m.Groups[1].Value);
            }
        }
        _containers[rel] = c;
    }

    // ─── Queries ─────────────────────────────────────────────────────

    /// <summary>Guids of .cs files whose stem matches className (Unity convention: MonoBehaviour
    /// / ScriptableObject class name == file name, enforced by the editor for attachable types).</summary>
    List<string> ScriptGuidsFor(string className)
    {
        string suffix = "/" + className + ".cs";
        var guids = new List<string>();
        foreach (var kvp in _guidToPath)
            if (kvp.Value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                guids.Add(kvp.Key);
        return guids;
    }

    /// <summary>Containers with the script attached, as (path, attachCount), scenes/prefabs/SO assets.</summary>
    public List<(string Path, int Count)> AttachSites(string className)
    {
        var guids = ScriptGuidsFor(className);
        if (guids.Count == 0) return new List<(string, int)>();
        var result = new List<(string, int)>();
        foreach (var c in _containers.Values)
        {
            int total = 0;
            foreach (var g in guids)
                if (c.ScriptAttach.TryGetValue(g, out int n)) total += n;
            if (total > 0) result.Add((c.Path, total));
        }
        result.Sort((a, b) => b.Item2 != a.Item2 ? b.Item2.CompareTo(a.Item2) : string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }

    /// <summary>UnityEvent persistent calls targeting className, as (containerPath, methodName).</summary>
    public List<(string Path, string Method)> EventTargets(string className)
    {
        var result = new List<(string, string)>();
        foreach (var c in _containers.Values)
            foreach (var (typeName, method) in c.EventCalls)
            {
                string shortName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                if (shortName.Equals(className, StringComparison.Ordinal))
                    result.Add((c.Path, method));
            }
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }

    /// <summary>Containers referencing target's guid. Target: asset path (Assets/...) or class name.</summary>
    public List<string> UsedBy(string target, out string resolvedNote)
    {
        resolvedNote = null;
        var guids = new List<string>();
        string targetRel = target.Replace('\\', '/').TrimEnd('/');
        if (_pathToGuid.TryGetValue(targetRel, out string g))
        {
            guids.Add(g);
            resolvedNote = $"{targetRel} (guid {g})";
        }
        else
        {
            guids = ScriptGuidsFor(target);
            if (guids.Count > 0)
                resolvedNote = $"script class {target} ({guids.Count} file(s))";
        }
        if (guids.Count == 0) return null;

        var guidSet = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var c in _containers.Values)
        {
            if (c.Path.Equals(targetRel, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.RefGuids.Overlaps(guidSet)) result.Add(c.Path);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    static string KindOf(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".unity", StringComparison.OrdinalIgnoreCase)) return "scene";
        if (ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase)) return "prefab";
        if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase)) return "asset";
        if (TextRefExts.Contains(ext)) return "ui";
        if (JsonNameExts.Contains(ext)) return "input";
        return "other";
    }

    /// <summary>Enabled scenes from EditorBuildSettings.asset (path → build index). Read fresh
    /// each call — the file is tiny and ProjectSettings isn't covered by the daemon watcher.</summary>
    Dictionary<string, int> BuildSettingsScenes()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string file = Path.Combine(_projectPath, "ProjectSettings", "EditorBuildSettings.asset");
            if (!File.Exists(file)) return result;
            bool pendingEnabled = false;
            int index = 0;
            foreach (var raw in File.ReadLines(file))
            {
                string t = raw.TrimStart();
                if (t.StartsWith("- enabled:", StringComparison.Ordinal))
                    pendingEnabled = t.EndsWith("1", StringComparison.Ordinal);
                else if (t.StartsWith("path:", StringComparison.Ordinal))
                {
                    string p = t.Substring("path:".Length).Trim();
                    if (pendingEnabled && p.Length > 0) result[p] = index++;
                }
            }
        }
        catch { }
        return result;
    }

    // ─── Formatting ──────────────────────────────────────────────────

    /// <summary>"Asset wiring" section appended to CODE_ANALYZE deep-type views.
    /// Null when the class has no serialized presence (nothing to say — stay quiet).</summary>
    public string FormatWiring(string className)
    {
        var attach = AttachSites(className);
        var events = EventTargets(className);
        if (attach.Count == 0 && events.Count == 0) return null;

        var scenes = attach.Where(a => KindOf(a.Path) == "scene").ToList();
        var prefabs = attach.Where(a => KindOf(a.Path) == "prefab").ToList();
        var soAssets = attach.Where(a => KindOf(a.Path) == "asset").ToList();
        var other = attach.Where(a => KindOf(a.Path) is not ("scene" or "prefab" or "asset")).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Asset wiring (serialized scenes/prefabs, not code):");
        string Fmt((string Path, int Count) a) => "  " + (a.Count > 1 ? $"{a.Path} ({a.Count})" : a.Path);
        if (scenes.Count > 0) AppendCapped(sb, $"  Attached in scenes ({scenes.Count})", scenes.Select(Fmt).ToList(), 10);
        if (prefabs.Count > 0) AppendCapped(sb, $"  Attached in prefabs ({prefabs.Count})", prefabs.Select(Fmt).ToList(), 10);
        if (soAssets.Count > 0) AppendCapped(sb, $"  ScriptableObject instances ({soAssets.Count})", soAssets.Select(Fmt).ToList(), 10);
        if (other.Count > 0) AppendCapped(sb, $"  Other containers ({other.Count})", other.Select(Fmt).ToList(), 5);
        if (events.Count > 0)
            AppendCapped(sb, $"  UnityEvent targets ({events.Count})",
                events.Select(e => $"  {e.Path} → {className}.{e.Method}").Distinct().ToList(), 10);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Response for `usedby:<asset-path-or-class>` — reverse reference lookup.</summary>
    public string FormatUsedBy(string target)
    {
        target = (target ?? "").Trim();
        if (target.Length == 0)
            return "Error: No target. Usage: usedby:Assets/Path/Foo.prefab | usedby:ClassName";

        var refs = UsedBy(target, out string note);
        if (refs == null)
            return $"Error: '{target}' not found — not an indexed asset path or script class ({_guidToPath.Count} guids indexed)";

        var sb = new StringBuilder();
        sb.AppendLine($"=== usedby:{target} === ({refs.Count} referencing asset(s))");
        sb.AppendLine($"Resolved: {note}");
        if (refs.Count == 0)
        {
            sb.AppendLine("(no serialized references — unused, or referenced only from code/Resources.Load)");
            return sb.ToString().TrimEnd();
        }
        sb.AppendLine();
        foreach (var group in refs.GroupBy(KindOf).OrderBy(g => g.Key, StringComparer.Ordinal))
            AppendCapped(sb, $"{group.Key} ({group.Count()})", group.ToList(), 25);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Task-oriented project map: given free keywords, return the scripts, scenes, prefabs,
    /// config assets, and UnityEvent wiring that relate to them — one compact dossier that
    /// answers "where do I start?" before any symbol name is known.
    /// </summary>
    /// <param name="getTree">Resolves a syntax tree for a path. Package source is not held resident
    /// by the daemon, so this may re-parse — only the top-ranked files below ever ask, so the cost
    /// is bounded to those, not the corpus.</param>
    public string FormatMap(
        Func<string, SyntaxTree> getTree,
        IReadOnlyDictionary<string, string> fileTexts,
        string projectPath,
        string query,
        long elapsedMs)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
            return "Error: No keywords. Usage: MAP <task keywords> (e.g. MAP double jump player input)";

        var sw = Stopwatch.StartNew();

        // ── Code side: rank files by distinct matching terms, then extract matching types.
        var fileScores = new List<(string File, int Distinct)>();
        foreach (var kvp in fileTexts)
        {
            int distinct = terms.Count(t => kvp.Value.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            if (distinct > 0) fileScores.Add((kvp.Key, distinct));
        }
        bool IsAssetsFile(string f) => f.Replace('\\', '/').Contains("/Assets/", StringComparison.OrdinalIgnoreCase);
        var topFiles = fileScores
            .OrderByDescending(f => f.Distinct)
            .ThenByDescending(f => IsAssetsFile(f.File))
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();

        var nameMatchTypes = new List<string>();    // "TypeName — path:line  [attached: ...]"
        var contentMatchTypes = new List<string>(); // "TypeName — path:line (Jump():88, jumpForce:22)"
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        var topTypeNames = new List<string>();
        string nextTypeUserCode = null; // preferred "Next:" suggestion — user code beats package samples
        foreach (var (file, _) in topFiles)
        {
            var tree = getTree(file);
            if (tree == null) continue;
            string rel = CodeAnalysisCore.ToRelativePath(file, projectPath);
            SyntaxNode root;
            try { root = tree.GetRoot(); } catch { continue; }

            foreach (var td in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                string typeName = td.Identifier.Text;
                if (!seenTypes.Add(typeName)) continue;
                int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                bool nameHit = terms.Any(t => typeName.Contains(t, StringComparison.OrdinalIgnoreCase));

                // Kind tag: the base type tells the AI what it's dealing with (MonoBehaviour →
                // scene wiring, ScriptableObject → asset instances, Editor → tooling). [editor]
                // flags editor-assembly scripts so runtime tasks can skip them.
                string kindTag = td switch
                {
                    EnumDeclarationSyntax _ => " (enum)",
                    InterfaceDeclarationSyntax _ => " (interface)",
                    TypeDeclarationSyntax t when t.BaseList?.Types.Count > 0 => $" : {t.BaseList.Types[0].Type}",
                    _ => ""
                };
                if (rel.Replace('\\', '/').Contains("/Editor/", StringComparison.OrdinalIgnoreCase))
                    kindTag += " [editor]";

                // Matching members give the content-match line its substance.
                var memberHits = new List<string>();
                if (td is TypeDeclarationSyntax typeDecl)
                {
                    foreach (var m in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                        if (terms.Any(t => m.Identifier.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                            memberHits.Add($"{m.Identifier.Text}():{m.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    foreach (var f in typeDecl.Members.OfType<FieldDeclarationSyntax>())
                        foreach (var v in f.Declaration.Variables)
                            if (terms.Any(t => v.Identifier.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                                memberHits.Add($"{v.Identifier.Text}:{v.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    foreach (var p in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
                        if (terms.Any(t => p.Identifier.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                            memberHits.Add($"{p.Identifier.Text}:{p.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                }
                if (!nameHit && memberHits.Count == 0) continue;

                var attach = AttachSites(typeName);
                string attachTail = attach.Count > 0
                    ? $"  [attached: {string.Join(", ", attach.Take(3).Select(a => Path.GetFileName(a.Path)))}{(attach.Count > 3 ? $", +{attach.Count - 3}" : "")}]"
                    : "";
                if (nameHit)
                {
                    nameMatchTypes.Add($"{typeName}{kindTag} — {rel}:{line}{attachTail}");
                    topTypeNames.Add(typeName);
                    if (nextTypeUserCode == null && IsAssetsFile(file)) nextTypeUserCode = typeName;
                }
                else
                {
                    string members = string.Join(", ", memberHits.Take(4));
                    contentMatchTypes.Add($"{typeName}{kindTag} — {rel}:{line} ({members}){attachTail}");
                    if (topTypeNames.Count < 8) topTypeNames.Add(typeName);
                }
            }
        }

        // ── Asset side: containers whose file name or GameObject names match.
        var buildScenes = BuildSettingsScenes();
        var assetHits = new Dictionary<string, List<string>>(StringComparer.Ordinal); // kind -> lines
        string topAssetPath = null;
        foreach (var c in _containers.Values.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileNameWithoutExtension(c.Path);
            bool nameHit = terms.Any(t => fileName.Contains(t, StringComparison.OrdinalIgnoreCase));
            var objHits = nameHit
                ? new List<string>()
                : c.ObjectNames.Where(n => terms.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(3).ToList();
            if (!nameHit && objHits.Count == 0) continue;

            string kind = KindOf(c.Path);
            string tag = kind == "scene" && buildScenes.TryGetValue(c.Path, out int bi) ? $" [build #{bi}]" : "";
            string objTail = objHits.Count > 0 ? $" (objects: {string.Join(", ", objHits)})" : "";
            if (!assetHits.TryGetValue(kind, out var list)) assetHits[kind] = list = new List<string>();
            list.Add($"{c.Path}{tag}{objTail}");
            topAssetPath ??= c.Path;
        }

        // ── UnityEvent wiring matching any term (method or target type).
        var eventLines = new List<string>();
        foreach (var c in _containers.Values)
            foreach (var (typeName, method) in c.EventCalls)
            {
                string shortName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                if (terms.Any(t => method.Contains(t, StringComparison.OrdinalIgnoreCase)
                                || shortName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    eventLines.Add($"{c.Path} → {shortName}.{method}");
            }
        eventLines = eventLines.Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();

        sw.Stop();

        // ── Compose.
        var sb = new StringBuilder();
        sb.AppendLine($"=== MAP: {string.Join(" ", terms)} === ({fileScores.Count} code files, "
            + $"{assetHits.Values.Sum(v => v.Count)} assets matched; index {elapsedMs}ms + map {sw.ElapsedMilliseconds}ms)");

        if (nameMatchTypes.Count == 0 && contentMatchTypes.Count == 0
            && assetHits.Count == 0 && eventLines.Count == 0)
        {
            sb.AppendLine("No matches. Try broader or fewer keywords, or ANALYZE <ClassName> if you know a symbol.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        if (nameMatchTypes.Count > 0) AppendCapped(sb, $"Scripts — name match ({nameMatchTypes.Count})", nameMatchTypes, 12);
        if (contentMatchTypes.Count > 0) AppendCapped(sb, $"Scripts — member match ({contentMatchTypes.Count})", contentMatchTypes, 12);
        void AppendAssets(string kind, string heading)
        {
            if (assetHits.TryGetValue(kind, out var list) && list.Count > 0)
                AppendCapped(sb, $"{heading} ({list.Count})", list, 8);
        }
        AppendAssets("scene", "Scenes");
        AppendAssets("prefab", "Prefabs");
        AppendAssets("asset", "Config/SO assets");
        AppendAssets("ui", "UI (UXML/USS)");
        AppendAssets("input", "Input actions");
        AppendAssets("other", "Other assets");
        if (eventLines.Count > 0) AppendCapped(sb, $"UnityEvent wiring ({eventLines.Count})", eventLines, 8);

        var next = new List<string>();
        string nextType = nextTypeUserCode ?? topTypeNames.FirstOrDefault();
        if (nextType != null) next.Add($"ANALYZE {nextType}");
        if (topAssetPath != null) next.Add($"ANALYZE usedby:{topAssetPath}");
        if (next.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Next: {string.Join("  |  ", next)}");
        }
        return sb.ToString().TrimEnd();
    }

    static List<string> Tokenize(string query)
    {
        var terms = new List<string>();
        foreach (var raw in Regex.Split(query ?? "", @"[^A-Za-z0-9_]+"))
        {
            string t = raw.Trim();
            if (t.Length < 2) continue;
            if (!terms.Contains(t, StringComparer.OrdinalIgnoreCase)) terms.Add(t);
            if (terms.Count >= 8) break;
        }
        return terms;
    }

    static void AppendCapped(StringBuilder sb, string heading, List<string> items, int cap)
    {
        sb.AppendLine($"{heading}:");
        foreach (var i in items.Take(cap)) sb.AppendLine($"  {i}");
        if (items.Count > cap) sb.AppendLine($"  ... +{items.Count - cap} more");
    }

    string ToRel(string fullPath)
    {
        string norm = fullPath.Replace('\\', '/');
        string proj = _projectPath.Replace('\\', '/').TrimEnd('/') + "/";
        if (norm.StartsWith(proj, StringComparison.OrdinalIgnoreCase))
            return norm.Substring(proj.Length);
        return norm;
    }
}
