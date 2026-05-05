using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace clibridge4unity;

/// <summary>
/// Unity-faithful per-asmdef compilation. Parses every `*.asmdef` in Assets/ + Packages/,
/// builds the asmdef DAG, routes every `.cs` to its owning asmdef (Unity's nearest-ancestor
/// rule + predefined `Assembly-CSharp` family fallbacks), and exposes the result for
/// downstream compilation.
///
/// Phase 1 (this file): parse, index, route. No compilation yet.
/// Phase 2: DAG topological sort + per-asmdef CSharpCompilation.
/// Phase 3: versionDefines + defineConstraints + csc.rsp.
/// Phase 4: source generators + diagnostics aggregation.
/// </summary>
internal static class LintAsmdef
{
    /// <summary>Parsed asmdef + computed metadata (owning files, resolved refs).</summary>
    public sealed class AsmdefNode
    {
        public string Name;
        public string Guid;                     // from .meta sibling
        public string AsmdefPath;               // absolute path to *.asmdef
        public string RootDir;                  // dir containing the asmdef (scope root)
        public List<string> References = new(); // raw — `GUID:xxx` or bare names
        public List<string> IncludePlatforms = new();
        public List<string> ExcludePlatforms = new();
        public bool AllowUnsafeCode;
        public bool OverrideReferences;
        public List<string> PrecompiledReferences = new();
        public bool AutoReferenced = true;      // default true if missing
        public List<string> DefineConstraints = new();
        public List<VersionDefine> VersionDefines = new();
        public bool NoEngineReferences;
        public List<string> SourceFiles = new();// .cs files routed to this asmdef
        public bool IsEditorOnly => IncludePlatforms.Count == 1
                                 && IncludePlatforms[0].Equals("Editor", StringComparison.OrdinalIgnoreCase);
        public bool IsExcluded(string platform = "Editor")
        {
            if (ExcludePlatforms.Any(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase))) return true;
            if (IncludePlatforms.Count > 0
                && !IncludePlatforms.Any(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }
        public override string ToString() => $"{Name} ({SourceFiles.Count} files, {References.Count} refs)";
    }

    public sealed class VersionDefine
    {
        public string Name;        // package name (e.g. "com.unity.textmeshpro")
        public string Expression;  // semver range (e.g. "4.0.0-preview.0", "[1.0.0,2.0.0)")
        public string Define;      // symbol to define if expression matches installed version
    }

    /// <summary>The full asmdef graph for a project: parsed nodes + GUID/name lookups + the
    /// 4 predefined `Assembly-CSharp*` synthetic asmdefs that catch unrouted .cs files.</summary>
    public sealed class Graph
    {
        public List<AsmdefNode> All = new();
        public Dictionary<string, AsmdefNode> ByGuid = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AsmdefNode> ByName = new(StringComparer.OrdinalIgnoreCase);
        // Predefined assemblies (Unity's defaults for unrouted .cs files):
        public AsmdefNode AssemblyCSharp;             // runtime + Assets/
        public AsmdefNode AssemblyCSharpEditor;       // any /Editor/ folder
        public AsmdefNode AssemblyCSharpFirstpass;    // Assets/Standard Assets/, Assets/Plugins/, Assets/Pro Standard Assets/
        public AsmdefNode AssemblyCSharpEditorFirstpass; // Editor under firstpass roots
    }

    /// <summary>Build the graph for `projectPath`. Scans Assets/ + Library/PackageCache + Packages/.</summary>
    public static Graph Build(string projectPath)
    {
        var graph = new Graph();

        // 1) Find every *.asmdef under Assets/ and Library/PackageCache/.
        var roots = new List<string> {
            Path.Combine(projectPath, "Assets"),
            Path.Combine(projectPath, "Packages"),
            Path.Combine(projectPath, "Library", "PackageCache"),
        };
        var asmdefs = new List<string>();
        foreach (var r in roots)
            if (Directory.Exists(r))
                asmdefs.AddRange(Directory.EnumerateFiles(r, "*.asmdef", SearchOption.AllDirectories));

        // 2) Parse each. Skip on JSON failure (rare — corrupt asmdef).
        foreach (var path in asmdefs)
        {
            var node = ParseAsmdef(path);
            if (node == null) continue;
            graph.All.Add(node);
            if (!string.IsNullOrEmpty(node.Guid)) graph.ByGuid[node.Guid] = node;
            if (!string.IsNullOrEmpty(node.Name)) graph.ByName[node.Name] = node;
        }

        // 3) Synthesize the 4 predefined Assembly-CSharp* asmdefs.
        graph.AssemblyCSharpFirstpass = MakePredefined("Assembly-CSharp-firstpass", projectPath);
        graph.AssemblyCSharpEditorFirstpass = MakePredefined("Assembly-CSharp-Editor-firstpass", projectPath, isEditor: true);
        graph.AssemblyCSharp = MakePredefined("Assembly-CSharp", projectPath);
        graph.AssemblyCSharpEditor = MakePredefined("Assembly-CSharp-Editor", projectPath, isEditor: true);
        foreach (var p in new[] { graph.AssemblyCSharpFirstpass, graph.AssemblyCSharpEditorFirstpass,
                                  graph.AssemblyCSharp, graph.AssemblyCSharpEditor })
        {
            graph.All.Add(p);
            graph.ByName[p.Name] = p;
        }

        // 4) Route every .cs under Assets/ + Packages/ to its owning asmdef.
        //    Rule: nearest ancestor folder containing an asmdef wins. If none, route to
        //    the predefined Assembly-CSharp family per Unity's firstpass / Editor rules.
        var asmdefDirs = graph.All.Where(a => a.AsmdefPath != null)
            .Select(a => (path: a.RootDir, node: a))
            .OrderByDescending(x => x.path.Length).ToList(); // deepest first

        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            foreach (var cs in Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
            {
                var owner = FindOwner(cs, asmdefDirs, graph, projectPath);
                if (owner != null) owner.SourceFiles.Add(cs);
            }
        }

        return graph;
    }

    static AsmdefNode ParseAsmdef(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var node = new AsmdefNode
            {
                AsmdefPath = path,
                RootDir = Path.GetDirectoryName(path),
                Name = root.TryGetProperty("name", out var n) ? n.GetString() : Path.GetFileNameWithoutExtension(path),
                NoEngineReferences = root.TryGetProperty("noEngineReferences", out var ne) && ne.GetBoolean(),
                AllowUnsafeCode = root.TryGetProperty("allowUnsafeCode", out var au) && au.GetBoolean(),
                OverrideReferences = root.TryGetProperty("overrideReferences", out var or) && or.GetBoolean(),
                AutoReferenced = !root.TryGetProperty("autoReferenced", out var ar) || ar.GetBoolean(),
            };
            ReadStringArray(root, "references", node.References);
            ReadStringArray(root, "includePlatforms", node.IncludePlatforms);
            ReadStringArray(root, "excludePlatforms", node.ExcludePlatforms);
            ReadStringArray(root, "precompiledReferences", node.PrecompiledReferences);
            ReadStringArray(root, "defineConstraints", node.DefineConstraints);
            if (root.TryGetProperty("versionDefines", out var vd) && vd.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in vd.EnumerateArray())
                {
                    var v = new VersionDefine
                    {
                        Name = entry.TryGetProperty("name", out var vn) ? vn.GetString() : null,
                        Expression = entry.TryGetProperty("expression", out var ve) ? ve.GetString() : null,
                        Define = entry.TryGetProperty("define", out var vf) ? vf.GetString() : null,
                    };
                    if (!string.IsNullOrEmpty(v.Define)) node.VersionDefines.Add(v);
                }
            }
            // GUID lives in the .meta sibling (`guid: <hash>`).
            string meta = path + ".meta";
            if (File.Exists(meta))
            {
                foreach (var line in File.ReadLines(meta))
                {
                    if (line.StartsWith("guid:", StringComparison.Ordinal))
                    {
                        node.Guid = line.Substring(5).Trim();
                        break;
                    }
                }
            }
            return node;
        }
        catch { return null; }
    }

    static void ReadStringArray(JsonElement root, string key, List<string> dest)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array) return;
        foreach (var v in el.EnumerateArray())
            if (v.ValueKind == JsonValueKind.String) dest.Add(v.GetString());
    }

    static AsmdefNode MakePredefined(string name, string projectPath, bool isEditor = false)
    {
        var node = new AsmdefNode
        {
            Name = name,
            RootDir = Path.Combine(projectPath, "Assets"),
            AutoReferenced = true,
        };
        if (isEditor) node.IncludePlatforms.Add("Editor");
        return node;
    }

    /// <summary>Find which asmdef owns a given .cs file. Returns null if outside Assets/Packages/.</summary>
    static AsmdefNode FindOwner(string csPath, List<(string path, AsmdefNode node)> asmdefDirs, Graph graph, string projectPath)
    {
        string norm = csPath.Replace('\\', '/');
        // 1) Nearest-ancestor explicit asmdef wins.
        string csDir = Path.GetDirectoryName(csPath).Replace('\\', '/');
        foreach (var (dirPath, node) in asmdefDirs)
        {
            string dirNorm = dirPath.Replace('\\', '/');
            if (csDir == dirNorm || csDir.StartsWith(dirNorm + "/", StringComparison.OrdinalIgnoreCase))
                return node;
        }

        // 2) Predefined assemblies — only files under Assets/.
        string projNorm = projectPath.Replace('\\', '/').TrimEnd('/');
        string assetsRoot = projNorm + "/Assets/";
        if (!norm.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase)) return null;

        string rel = norm.Substring(assetsRoot.Length);
        bool firstpass = rel.StartsWith("Standard Assets/", StringComparison.OrdinalIgnoreCase)
                      || rel.StartsWith("Pro Standard Assets/", StringComparison.OrdinalIgnoreCase)
                      || rel.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase);
        bool isEditor = rel.Split('/').Any(seg => string.Equals(seg, "Editor", StringComparison.OrdinalIgnoreCase));

        if (firstpass)
            return isEditor ? graph.AssemblyCSharpEditorFirstpass : graph.AssemblyCSharpFirstpass;
        return isEditor ? graph.AssemblyCSharpEditor : graph.AssemblyCSharp;
    }

    /// <summary>Resolve a reference token (`GUID:xxx` or bare name) to a node, or null if unresolvable.</summary>
    public static AsmdefNode ResolveRef(string token, Graph graph)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (token.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            return graph.ByGuid.TryGetValue(token.Substring(5).Trim(), out var n) ? n : null;
        return graph.ByName.TryGetValue(token, out var b) ? b : null;
    }
}
