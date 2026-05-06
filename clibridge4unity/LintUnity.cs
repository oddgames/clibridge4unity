using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace clibridge4unity;

/// <summary>
/// Unity-faithful per-asmdef compilation. Builds the asmdef DAG via <see cref="LintAsmdef"/>,
/// topo-sorts, and runs one <see cref="CSharpCompilation"/> per asmdef with the right refs +
/// defines. Catches errors that the lump-everything-together <see cref="LintSemantic"/>
/// misses (cross-asmdef partial classes, editor-only-leaks-into-runtime, package-version-gated
/// code, asmdef-private types).
///
/// Phase 2: DAG + compile + collect diagnostics. UNITY_EDITOR per-asmdef (asmdef-aware, not
/// path-heuristic). Default Unity built-in defines + ProjectSettings scriptingDefineSymbols.
///
/// Not yet (later phases): versionDefines (semver eval), csc.rsp parsing, source generators.
/// </summary>
internal static class LintUnity
{
    public sealed class CompileResult
    {
        public LintAsmdef.AsmdefNode Node;
        public List<Diagnostic> Diagnostics = new();
        public bool Skipped;            // skipped due to defineConstraints / platform exclusion
        public string SkipReason;
        public long ElapsedMs;
        public MetadataReference EmittedRef; // for downstream refs (in-memory bytes)
    }

    public sealed class RunResult
    {
        public List<CompileResult> PerAsmdef = new();
        public string Error;             // top-level fatal error (no Unity install, etc.)
        public long TotalMs;
        public int RefCount;             // engine refs reused across all compiles
        public int FilterDebugCount;     // debug: how many engine refs were filtered as user dupes
        public int UserAsmdefCount;      // debug: how many user asmdefs detected
        public string UnityVersion;
    }

    /// <summary>Run the Unity-faithful lint on USER asmdefs only. Package asmdefs use their
    /// Library/ScriptAssemblies/<name>.dll as MetadataReference (Unity already compiled them).
    /// Sub-10s on most projects (typically <10 user asmdefs).</summary>
    public static RunResult Run(string projectPath, IReadOnlyDictionary<string, string> fileTexts = null)
    {
        var swTotal = Stopwatch.StartNew();
        var result = new RunResult();

        // 1) Engine refs (BCL + UnityEngine + UnityEditor + ScriptAssemblies). Reused across asmdefs.
        //    LintSemantic.Resolve already loads ALL Library/ScriptAssemblies DLLs — package asmdef
        //    outputs come "for free" as MetadataReferences without us recompiling them.
        var (engineRefsRaw, builtinDefines, userDefines, _, version, refError) = LintSemantic.Resolve(projectPath);
        if (refError != null) { result.Error = refError; return result; }
        result.UnityVersion = version;
        result.RefCount = engineRefsRaw.Count;

        // 2) Build asmdef graph FIRST so we know which asmdefs are user-owned.
        var graph = LintAsmdef.Build(projectPath);

        bool IsUserAsmdef(LintAsmdef.AsmdefNode n)
        {
            if (n.AsmdefPath == null) return true; // predefined Assembly-CSharp — user code
            return !n.AsmdefPath.Replace('\\', '/').Contains("/PackageCache/", StringComparison.OrdinalIgnoreCase);
        }
        var userAsmdefs = graph.All.Where(IsUserAsmdef).ToList();
        var userAsmdefNames = new HashSet<string>(userAsmdefs.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

        // Map: asmdef name → its compiled DLL in Library/ScriptAssemblies (Unity emits one .dll per asmdef).
        // Used as ref for user asmdef → package asmdef deps WITHOUT recompiling.
        var prebuiltAsmdefRefs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        string scriptAsmDir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
        if (Directory.Exists(scriptAsmDir))
        {
            foreach (var dll in Directory.EnumerateFiles(scriptAsmDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string asmName = Path.GetFileNameWithoutExtension(dll);
                try { prebuiltAsmdefRefs[asmName] = MetadataReference.CreateFromFile(dll); } catch { }
            }
        }

        // 3) Filter engine refs: drop ANY DLL whose name matches a user asmdef name. Otherwise
        //    we'd have BOTH the prebuilt user-asmdef DLL AND our recompiled trees containing the
        //    same types → CS0121/CS0433/CS0104 ambiguity cascades (thousands of false positives).
        var debugLog = new StringBuilder();
        debugLog.AppendLine($"=== {DateTime.Now} LintUnity.Run ===");
        debugLog.AppendLine($"userAsmdefNames count: {userAsmdefNames.Count}");
        debugLog.AppendLine($"engineRefsRaw count: {engineRefsRaw.Count}");
        debugLog.AppendLine($"first 5 user asmdef names: {string.Join(", ", userAsmdefNames.Take(5))}");
        debugLog.AppendLine($"first 5 ref displays: {string.Join("\n  ", engineRefsRaw.Take(5).Select(r => r.Display ?? "(null)"))}");
        int filteredOut = 0;
        var engineRefs = engineRefsRaw.Where(r =>
        {
            string disp = r.Display ?? "";
            string name = Path.GetFileNameWithoutExtension(disp);
            bool drop = userAsmdefNames.Contains(name);
            if (drop) { filteredOut++; debugLog.AppendLine($"DROP: {disp}"); }
            return !drop;
        }).ToList();
        debugLog.AppendLine($"filteredOut: {filteredOut}");
        result.FilterDebugCount = filteredOut;
        result.UserAsmdefCount = userAsmdefNames.Count;
        try { File.WriteAllText(@"C:\Workspaces\tool_claude_unity_bridge\lint_debug.log", debugLog.ToString()); }
        catch (Exception logEx) { Console.Error.WriteLine($"[lint:debug] log failed: {logEx.Message}"); }

        // Split engine refs into runtime-only vs editor-included.
        var runtimeRefs = new List<MetadataReference>();
        var editorRefs = new List<MetadataReference>();
        foreach (var r in engineRefs)
        {
            string name = Path.GetFileName(r.Display ?? "");
            if (name.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase)) editorRefs.Add(r);
            else runtimeRefs.Add(r);
        }
        editorRefs.AddRange(runtimeRefs);

        // 4) Topo-sort + group by depth — asmdefs at the same depth have no cross-deps and
        //    can compile in parallel. Cuts wall-clock time on multi-core for big projects.
        var levels = TopoLevels(userAsmdefs, graph);

        // 5) Compile each level in parallel; emitted refs flow into the next level.
        var emittedRefs = new System.Collections.Concurrent.ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in levels)
        {
            var levelResults = new CompileResult[level.Count];
            System.Threading.Tasks.Parallel.For(0, level.Count, i =>
            {
                levelResults[i] = CompileOne(level[i], graph, runtimeRefs, editorRefs,
                    new Dictionary<string, MetadataReference>(emittedRefs, StringComparer.OrdinalIgnoreCase),
                    prebuiltAsmdefRefs, builtinDefines, userDefines, fileTexts);
            });
            foreach (var r in levelResults)
            {
                result.PerAsmdef.Add(r);
                if (r.EmittedRef != null) emittedRefs[r.Node.Name] = r.EmittedRef;
            }
        }

        swTotal.Stop();
        result.TotalMs = swTotal.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Group user asmdefs into topological depth levels — every asmdef at level N
    /// depends only on asmdefs at level &lt;N. Same-level asmdefs are independent and can
    /// compile in parallel. Predefined Assembly-CSharp* go in the deepest level.</summary>
    static List<List<LintAsmdef.AsmdefNode>> TopoLevels(List<LintAsmdef.AsmdefNode> userAsmdefs, LintAsmdef.Graph graph)
    {
        var userSet = new HashSet<LintAsmdef.AsmdefNode>(userAsmdefs);
        var depth = new Dictionary<LintAsmdef.AsmdefNode, int>();

        int Compute(LintAsmdef.AsmdefNode n, HashSet<LintAsmdef.AsmdefNode> stack)
        {
            if (depth.TryGetValue(n, out var d)) return d;
            if (!stack.Add(n)) return 0; // cycle — clamp
            int max = 0;
            foreach (var refTok in n.References)
            {
                var dep = LintAsmdef.ResolveRef(refTok, graph);
                if (dep == null || dep == n || !userSet.Contains(dep)) continue;
                int dd = Compute(dep, stack);
                if (dd + 1 > max) max = dd + 1;
            }
            stack.Remove(n);
            depth[n] = max;
            return max;
        }
        foreach (var n in userAsmdefs.Where(a => a.AsmdefPath != null))
            Compute(n, new HashSet<LintAsmdef.AsmdefNode>());
        int predefinedDepth = (depth.Values.Count > 0 ? depth.Values.Max() : 0) + 1;
        foreach (var p in userAsmdefs.Where(a => a.AsmdefPath == null))
            depth[p] = predefinedDepth;

        return depth.GroupBy(kvp => kvp.Value).OrderBy(g => g.Key)
                    .Select(g => g.Select(x => x.Key).ToList()).ToList();
    }

    /// <summary>Topological sort of the asmdef DAG. Predefined assemblies depend on every
    /// autoReferenced asmdef and so must come last.</summary>
    static List<LintAsmdef.AsmdefNode> TopoSort(LintAsmdef.Graph graph)
    {
        var visited = new HashSet<LintAsmdef.AsmdefNode>();
        var sorted = new List<LintAsmdef.AsmdefNode>();
        // Predefined come last — synthesize a fake "everything autoReferenced" dep set.
        var nonPredefined = graph.All.Where(a => a.AsmdefPath != null).ToList();
        var predefined = graph.All.Where(a => a.AsmdefPath == null).ToList();

        void Visit(LintAsmdef.AsmdefNode n)
        {
            if (!visited.Add(n)) return;
            foreach (var refTok in n.References)
            {
                var dep = LintAsmdef.ResolveRef(refTok, graph);
                if (dep != null && dep != n) Visit(dep);
            }
            sorted.Add(n);
        }
        foreach (var n in nonPredefined) Visit(n);
        // Predefined: depend on every autoReferenced asmdef (Unity adds these implicitly).
        foreach (var p in predefined)
        {
            foreach (var n in nonPredefined.Where(x => x.AutoReferenced)) Visit(n);
            visited.Add(p); sorted.Add(p);
        }
        return sorted;
    }

    static CompileResult CompileOne(LintAsmdef.AsmdefNode node, LintAsmdef.Graph graph,
        List<MetadataReference> runtimeRefs, List<MetadataReference> editorRefs,
        Dictionary<string, MetadataReference> emittedRefs,
        Dictionary<string, MetadataReference> prebuiltAsmdefRefs,
        string[] builtinDefines, string[] userDefines,
        IReadOnlyDictionary<string, string> fileTexts)
    {
        var sw = Stopwatch.StartNew();
        var result = new CompileResult { Node = node };

        // Skip if no source files.
        if (node.SourceFiles.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason = "no .cs files";
            sw.Stop(); result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        // Skip if platform-excluded (we lint for Editor by default — anything excluding Editor skips).
        if (node.IsExcluded("Editor"))
        {
            result.Skipped = true;
            result.SkipReason = $"excluded for Editor (include={string.Join(",", node.IncludePlatforms)} exclude={string.Join(",", node.ExcludePlatforms)})";
            sw.Stop(); result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        // Build per-asmdef preprocessor symbol set.
        var defines = new List<string>(builtinDefines);
        defines.AddRange(userDefines);
        if (node.IsEditorOnly || node.AutoReferenced && node == graph.AssemblyCSharpEditor
            || node.AutoReferenced && node == graph.AssemblyCSharpEditorFirstpass
            || node.IncludePlatforms.Any(p => string.Equals(p, "Editor", StringComparison.OrdinalIgnoreCase)))
            defines.Add("UNITY_EDITOR");
        // TODO Phase 3: defineConstraints + versionDefines.

        // Skip if defineConstraints not satisfied. Format: `SYMBOL` requires defined,
        // `!SYMBOL` requires NOT defined.
        foreach (var c in node.DefineConstraints)
        {
            bool negate = c.StartsWith("!");
            string sym = negate ? c.Substring(1) : c;
            bool present = defines.Contains(sym);
            if (negate ? present : !present)
            {
                result.Skipped = true;
                result.SkipReason = $"defineConstraint failed: {c}";
                sw.Stop(); result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }
        }

        var parseOpts = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(defines);

        var trees = new List<SyntaxTree>(node.SourceFiles.Count);
        foreach (var file in node.SourceFiles)
        {
            try
            {
                string text = (fileTexts != null && fileTexts.TryGetValue(file, out var cached))
                    ? cached
                    : File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOpts, file));
            }
            catch { }
        }

        // Resolve references.
        var refs = new List<MetadataReference>();
        bool isEditor = defines.Contains("UNITY_EDITOR");
        if (!node.NoEngineReferences)
            refs.AddRange(isEditor ? editorRefs : runtimeRefs);

        // Auto-referenced asmdefs are implicitly visible to predefined assemblies (Assembly-CSharp etc.).
        // Use prebuilt DLLs from Library/ScriptAssemblies for the package asmdefs.
        if (node == graph.AssemblyCSharp || node == graph.AssemblyCSharpEditor
            || node == graph.AssemblyCSharpFirstpass || node == graph.AssemblyCSharpEditorFirstpass)
        {
            foreach (var other in graph.All.Where(a => a.AsmdefPath != null && a.AutoReferenced))
            {
                if (emittedRefs.TryGetValue(other.Name, out var er)) refs.Add(er);
                else if (prebuiltAsmdefRefs.TryGetValue(other.Name, out var pr)) refs.Add(pr);
            }
        }

        // Explicit references (asmdef "references" array). Two cases:
        //   1) Dep is another USER asmdef → use its in-memory emitted MetadataReference.
        //   2) Dep is a PACKAGE asmdef → use Library/ScriptAssemblies/<dep>.dll (Unity-compiled).
        foreach (var refTok in node.References)
        {
            var dep = LintAsmdef.ResolveRef(refTok, graph);
            if (dep == null) continue;
            if (emittedRefs.TryGetValue(dep.Name, out var er)) { refs.Add(er); continue; }
            if (prebuiltAsmdefRefs.TryGetValue(dep.Name, out var pr)) { refs.Add(pr); continue; }
        }

        // precompiledReferences: filename matches against engine refs we've already loaded.
        // (No need to add — engineRefs already includes everything in Library/ScriptAssemblies.)

        var compilation = CSharpCompilation.Create(
            assemblyName: node.Name,
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: node.AllowUnsafeCode,
                concurrentBuild: true));

        // Emit to in-memory bytes so downstream asmdefs can ref this one.
        try
        {
            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            // Even if Emit fails, GetDiagnostics gives us all type errors. Use Emit's diagnostics
            // as the authoritative set — they include the emit-stage checks (unsafe, IVT, etc).
            foreach (var d in emit.Diagnostics) result.Diagnostics.Add(d);
            if (emit.Success)
            {
                ms.Seek(0, SeekOrigin.Begin);
                result.EmittedRef = MetadataReference.CreateFromStream(ms);
            }
            else
            {
                // Even if emit failed, still expose diagnostics-only path.
                foreach (var d in compilation.GetDiagnostics()) result.Diagnostics.Add(d);
                // Dedupe.
                result.Diagnostics = result.Diagnostics.Distinct().ToList();
            }
        }
        catch (Exception ex)
        {
            result.SkipReason = $"emit threw: {ex.GetType().Name}: {ex.Message}";
            foreach (var d in compilation.GetDiagnostics()) result.Diagnostics.Add(d);
        }

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Format a RunResult into the same shape as LINT semantic output.</summary>
    public static string Format(RunResult run, string projectPath, bool includeWarnings)
    {
        if (run.Error != null)
            return $"Error: cannot run unity lint — {run.Error}";

        var sb = new StringBuilder();
        int totalErrors = 0, totalWarnings = 0, totalFiles = 0, asmdefsCompiled = 0, asmdefsSkipped = 0;
        var ignoredIds = new HashSet<string>(StringComparer.Ordinal)
        { "CS1701", "CS1702", "CS1705", "CS8019", "CS1591", "CS0436" };
        var lines = new List<string>();
        foreach (var r in run.PerAsmdef)
        {
            if (r.Skipped) { asmdefsSkipped++; continue; }
            asmdefsCompiled++;
            totalFiles += r.Node.SourceFiles.Count;
            // Only surface diagnostics for user-owned asmdefs (not packages).
            bool isUser = r.Node.AsmdefPath == null  // predefined Assembly-CSharp
                        || (r.Node.AsmdefPath != null && !r.Node.AsmdefPath.Contains("PackageCache"));
            if (!isUser) continue;
            foreach (var d in r.Diagnostics)
            {
                if (ignoredIds.Contains(d.Id)) continue;
                if (d.Severity == DiagnosticSeverity.Error) totalErrors++;
                else if (d.Severity == DiagnosticSeverity.Warning) totalWarnings++;
                else continue;
                if (d.Severity == DiagnosticSeverity.Warning && !includeWarnings) continue;
                var pos = d.Location.GetLineSpan().StartLinePosition;
                string sev = d.Severity == DiagnosticSeverity.Error ? "ERROR" : "WARN";
                string file = d.Location.SourceTree?.FilePath ?? "(no file)";
                string rel = file.Replace(projectPath + "\\", "").Replace(projectPath + "/", "");
                lines.Add($"{rel}:{pos.Line + 1}:{pos.Character + 1}: {sev} {d.Id}: {d.GetMessage()}  [{r.Node.Name}]");
            }
        }
        sb.AppendLine($"Files: {totalFiles}  Errors: {totalErrors}{(includeWarnings ? $"  Warnings: {totalWarnings}" : "")}  Mode: unity (asmdef-aware, {asmdefsCompiled} compiled, {asmdefsSkipped} skipped, {run.UserAsmdefCount} user asmdefs, {run.RefCount} engine refs, {run.FilterDebugCount} dupe-refs filtered, {run.TotalMs}ms)");
        if (totalErrors == 0 && (!includeWarnings || totalWarnings == 0))
        {
            sb.Append("OK — no errors. Per-asmdef compile passed.\nThis is the closest we get to Unity's compile pipeline without running Unity.");
            return sb.ToString();
        }
        sb.AppendLine();
        lines.Sort(StringComparer.Ordinal);
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
