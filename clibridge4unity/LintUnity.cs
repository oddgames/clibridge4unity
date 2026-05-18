using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    public sealed class StaleDllInfo
    {
        public string AsmdefName;
        public DateTime DllMtime;
        public DateTime NewestSourceMtime;
        public string NewestSourcePath;
    }

    public sealed class RunResult
    {
        public List<CompileResult> PerAsmdef = new();
        public List<StaleDllInfo> StaleDlls = new();
        public string Error;             // top-level fatal error (no Unity install, etc.)
        public string ProjectRspNote;    // e.g. "csc.rsp: nullable=enable, langversion=11, +3 defines"
        public long TotalMs;
        public int RefCount;             // engine refs reused across all compiles
        public int FilterDebugCount;     // debug: how many engine refs were filtered as user dupes
        public int UserAsmdefCount;      // debug: how many user asmdefs detected
        public int SourceGeneratorCount; // discovered Roslyn source generators
        public int SourceGeneratorFailures; // failed to load (version mismatch etc)
        public int FreshAsmdefCount;     // asmdefs skipped because their prebuilt DLL was up-to-date
        public string UnityVersion;
    }

    /// <summary>Run the Unity-faithful lint on USER asmdefs only. Package asmdefs use their
    /// Library/ScriptAssemblies/<name>.dll as MetadataReference (Unity already compiled them).
    /// Sub-10s on most projects (typically <10 user asmdefs).
    ///
    /// Budgets: hard wall-clock cap (default 30s) AND a no-progress watchdog (default 10s).
    /// The watchdog kills the current level if no asmdef has completed in 10s — covers the case
    /// where a single huge asmdef or a runaway source generator stalls everything. Cancellation
    /// is propagated all the way into <c>compilation.Emit</c>, <c>GetDiagnostics</c>, and the
    /// source generator driver, so in-flight work is actually killable (without this, Roslyn
    /// blocks the thread until the compile finishes regardless of the budget).</summary>
    public static RunResult Run(
        string projectPath,
        IReadOnlyDictionary<string, string> fileTexts = null,
        CancellationToken externalCt = default,
        int budgetMsOverride = 0,
        int noProgressMsOverride = 0)
    {
        var swTotal = Stopwatch.StartNew();
        var result = new RunResult();

        // 1) Engine refs (BCL + UnityEngine + UnityEditor + ScriptAssemblies). Reused across asmdefs.
        //    LintSemantic.Resolve already loads ALL Library/ScriptAssemblies DLLs — package asmdef
        //    outputs come "for free" as MetadataReferences without us recompiling them.
        var (engineRefsRaw, builtinDefines, userDefines, projectAllowUnsafe, _, version, refError) = LintSemantic.Resolve(projectPath);
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
        int filteredOut = 0;
        var engineRefs = engineRefsRaw.Where(r =>
        {
            string disp = r.Display ?? "";
            string name = Path.GetFileNameWithoutExtension(disp);
            bool drop = userAsmdefNames.Contains(name);
            if (drop) filteredOut++;
            return !drop;
        }).ToList();
        result.FilterDebugCount = filteredOut;
        result.UserAsmdefCount = userAsmdefNames.Count;

        // 3b) Project-wide csc.rsp + stale-DLL detection. Both surfaced in the Run summary.
        var projectRsp = LintCscRsp.Parse(Path.Combine(projectPath, "Assets", "csc.rsp"));
        if (projectRsp.Defines.Count > 0 || projectRsp.Nullable != null || projectRsp.LangVersion != null)
        {
            var bits = new List<string>();
            if (projectRsp.Nullable != null) bits.Add($"nullable={projectRsp.Nullable}");
            if (projectRsp.LangVersion != null) bits.Add($"langversion={projectRsp.LangVersion}");
            if (projectRsp.Defines.Count > 0) bits.Add($"+{projectRsp.Defines.Count} defines");
            if (projectRsp.NoWarn.Count > 0) bits.Add($"+{projectRsp.NoWarn.Count} nowarn");
            result.ProjectRspNote = "csc.rsp: " + string.Join(", ", bits);
        }
        DetectStaleDlls(projectPath, userAsmdefs, scriptAsmDir, result.StaleDlls);

        // Incremental skip: any user asmdef whose Library/ScriptAssemblies DLL is newer than every
        // input (source files + .asmdef config) can be reused as-is. Unity already compiled it
        // successfully — we hand the prebuilt DLL down the DAG as the MetadataReference instead of
        // recompiling. This is the difference between a 30s run and a sub-second run on no-change
        // re-invocations, and means edits only re-lint the touched asmdef + its dependents.
        //
        // Edge case not detected: a deleted .cs file leaves no source newer than the DLL, so the
        // asmdef stays "fresh" until Unity recompiles. Acceptable — the DLL also still contains
        // the deleted file's symbols, so type-binding would still pass; mismatch self-corrects on
        // next Unity refresh.
        var freshDllRefs = ComputeFreshAsmdefs(userAsmdefs, scriptAsmDir, prebuiltAsmdefRefs);
        result.FreshAsmdefCount = freshDllRefs.Count;

        // Source generators: discover Roslyn-tagged DLLs across project + packages. Cached after first call.
        var (sourceGenerators, genFailures) = LintSourceGenerators.Discover(projectPath);
        result.SourceGeneratorCount = sourceGenerators.Count;
        result.SourceGeneratorFailures = genFailures.Count;

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
        //    Two cooperating cut-offs:
        //     - Hard wall-clock budget (default 30s) — total work cap.
        //     - No-progress watchdog (default 10s) — kills a level if no asmdef has completed
        //       in that window. Covers stuck source generators and oversized single asmdefs.
        //    Both signal via a linked CancellationTokenSource that's passed into Roslyn's
        //    Emit/GetDiagnostics/GeneratorDriver, so in-flight work actually stops (without
        //    this, Roslyn blocks the thread until the compile finishes regardless of budget).
        int budgetMs = budgetMsOverride > 0 ? budgetMsOverride : 30_000;
        int noProgressMs = noProgressMsOverride > 0 ? noProgressMsOverride : 10_000;
        var emittedRefs = new System.Collections.Concurrent.ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        bool budgetBlown = false;
        bool noProgressKilled = false;
        long lastProgressTicks = DateTime.UtcNow.Ticks;
        foreach (var level in levels)
        {
            if (swTotal.ElapsedMilliseconds > budgetMs) { budgetBlown = true; break; }
            if (externalCt.IsCancellationRequested) { budgetBlown = true; break; }
            int remaining = (int)Math.Max(100, budgetMs - swTotal.ElapsedMilliseconds);
            using var levelCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            levelCts.CancelAfter(remaining);

            // Watchdog: cancel the level if no completion in noProgressMs.
            Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
            using var watchdogCts = new CancellationTokenSource();
            var watchdog = Task.Run(async () =>
            {
                while (!watchdogCts.IsCancellationRequested && !levelCts.IsCancellationRequested)
                {
                    try { await Task.Delay(500, watchdogCts.Token); }
                    catch (OperationCanceledException) { return; }
                    long ticks = Interlocked.Read(ref lastProgressTicks);
                    var idleMs = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds;
                    if (idleMs > noProgressMs)
                    {
                        noProgressKilled = true;
                        try { levelCts.Cancel(); } catch { }
                        return;
                    }
                }
            });

            var levelResults = new CompileResult[level.Count];
            try
            {
                Parallel.For(0, level.Count,
                    new ParallelOptions { CancellationToken = levelCts.Token },
                    i =>
                {
                    levelResults[i] = CompileOne(level[i], graph, runtimeRefs, editorRefs,
                        new Dictionary<string, MetadataReference>(emittedRefs, StringComparer.OrdinalIgnoreCase),
                        prebuiltAsmdefRefs, freshDllRefs, builtinDefines, userDefines, fileTexts, projectRsp,
                        sourceGenerators, projectAllowUnsafe, levelCts.Token);
                    Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                });
            }
            catch (OperationCanceledException) { budgetBlown = true; }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
            { budgetBlown = true; }
            finally
            {
                try { watchdogCts.Cancel(); } catch { }
                try { watchdog.Wait(1000); } catch { }
            }
            foreach (var r in levelResults)
            {
                if (r == null) continue;
                result.PerAsmdef.Add(r);
                if (r.EmittedRef != null) emittedRefs[r.Node.Name] = r.EmittedRef;
            }
            if (budgetBlown) break;
        }

        swTotal.Stop();
        result.TotalMs = swTotal.ElapsedMilliseconds;
        if (budgetBlown)
        {
            string reason = noProgressKilled
                ? $"no asmdef completed in {noProgressMs}ms (stuck source generator or oversized asmdef)"
                : $"exceeded {budgetMs}ms wall-clock budget";
            result.Error = $"LINT unity stopped early — {reason}. Partial results above. Run COMPILE for full check.";
        }
        return result;
    }

    /// <summary>Look up installed version of a package by name. Reads `package.json` from
    /// `Library/PackageCache/<name>@<hash>/` or `Packages/<name>/`, or — for Unity built-in
    /// modules (`com.unity.modules.*`) which have no package.json — checks the project's
    /// `Packages/manifest.json` for the named entry. Returns null if not installed.</summary>
    static string LookupPackageVersion(LintAsmdef.Graph graph, string packageName)
    {
        if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(graph?.ProjectPath)) return null;
        // Resolve via package.json in standard locations.
        string Try(string dir)
        {
            string pkg = Path.Combine(dir, "package.json");
            if (!File.Exists(pkg)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkg));
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    return v.GetString();
            }
            catch { }
            return null;
        }
        // Local Packages/<name>/
        string localPkg = Path.Combine(graph.ProjectPath, "Packages", packageName);
        var v1 = Try(localPkg);
        if (v1 != null) return v1;
        // `file:` UPM packages (manifest.json) — match by package.json `name` field.
        foreach (var filePkg in LintAsmdef.ResolveFilePackagePaths(graph.ProjectPath))
        {
            string pkgJson = Path.Combine(filePkg, "package.json");
            if (!File.Exists(pkgJson)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkgJson));
                if (doc.RootElement.TryGetProperty("name", out var nm)
                    && nm.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(nm.GetString(), packageName, StringComparison.OrdinalIgnoreCase)
                    && doc.RootElement.TryGetProperty("version", out var ver)
                    && ver.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return ver.GetString();
                }
            }
            catch { }
        }
        // PackageCache/<name>@<hash>/
        string cache = Path.Combine(graph.ProjectPath, "Library", "PackageCache");
        if (Directory.Exists(cache))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(cache, packageName + "@*", SearchOption.TopDirectoryOnly))
                {
                    var v = Try(dir);
                    if (v != null) return v;
                }
            }
            catch { }
        }
        // Built-in modules (com.unity.modules.*) — listed in manifest.json with a `1.0.0`-shaped
        // version but have no on-disk package. Treat manifest entry as the version.
        if (packageName.StartsWith("com.unity.modules.", StringComparison.Ordinal))
        {
            string manifest = Path.Combine(graph.ProjectPath, "Packages", "manifest.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("dependencies", out var deps)
                        && deps.TryGetProperty(packageName, out var ver)
                        && ver.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return ver.GetString();
                    }
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>Evaluate Unity-style version expression against an installed semver.
    /// Supported forms: bare version (`1.2.3` → installed >= 1.2.3),
    /// range `[a,b)` / `[a,b]` / `(a,b]` / `(a,b)`, single `[a]` (= exact). Pre-release suffixes ignored.</summary>
    static bool SemverSatisfies(string installedVersion, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true;
        var inst = ParseSemver(installedVersion);
        if (inst == null) return false;
        string e = expression.Trim();
        // Range form?
        if (e.StartsWith("[") || e.StartsWith("("))
        {
            bool incLow = e.StartsWith("[");
            bool incHigh = e.EndsWith("]");
            string inner = e.Substring(1, e.Length - 2);
            var parts = inner.Split(',');
            if (parts.Length == 1)
            {
                // [a] form = exact
                var p = ParseSemver(parts[0].Trim());
                return p != null && CompareSemver(inst, p) == 0;
            }
            if (parts.Length == 2)
            {
                int[] lo = string.IsNullOrWhiteSpace(parts[0]) ? null : ParseSemver(parts[0].Trim());
                int[] hi = string.IsNullOrWhiteSpace(parts[1]) ? null : ParseSemver(parts[1].Trim());
                if (lo != null)
                {
                    int c = CompareSemver(inst, lo);
                    if (incLow ? c < 0 : c <= 0) return false;
                }
                if (hi != null)
                {
                    int c = CompareSemver(inst, hi);
                    if (incHigh ? c > 0 : c >= 0) return false;
                }
                return true;
            }
            return false;
        }
        // Bare = `>=` semantics.
        var bare = ParseSemver(e);
        return bare != null && CompareSemver(inst, bare) >= 0;
    }

    static int[] ParseSemver(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        // Strip pre-release/build metadata.
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        var parts = s.Split('.');
        var nums = new int[3];
        for (int i = 0; i < 3 && i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i])) return null;
        return nums;
    }

    static int CompareSemver(int[] a, int[] b)
    {
        for (int i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    static NullableContextOptions NullableFromRsp(string val)
    {
        if (string.IsNullOrEmpty(val)) return NullableContextOptions.Disable;
        return val.ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };
    }

    /// <summary>For each user asmdef, return its prebuilt DLL as a MetadataReference if and
    /// only if the DLL is newer than the .asmdef config AND every source file. Caller skips
    /// the compile entirely for these — Unity already produced a successful build, so the
    /// emitted reference for downstream asmdefs is just the existing DLL.
    /// Reuses entries from <paramref name="prebuiltAsmdefRefs"/> so we don't double-load.</summary>
    static Dictionary<string, MetadataReference> ComputeFreshAsmdefs(
        List<LintAsmdef.AsmdefNode> userAsmdefs,
        string scriptAsmDir,
        Dictionary<string, MetadataReference> prebuiltAsmdefRefs)
    {
        var fresh = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(scriptAsmDir)) return fresh;
        foreach (var node in userAsmdefs)
        {
            if (node.AsmdefPath == null) continue;       // predefined Assembly-CSharp* — always recompile
            if (node.SourceFiles.Count == 0) continue;
            string dll = Path.Combine(scriptAsmDir, node.Name + ".dll");
            if (!File.Exists(dll)) continue;
            DateTime dllMtime;
            try { dllMtime = File.GetLastWriteTimeUtc(dll); } catch { continue; }

            bool stale = false;
            try { if (File.GetLastWriteTimeUtc(node.AsmdefPath) > dllMtime) stale = true; }
            catch { stale = true; }
            if (!stale)
            {
                foreach (var f in node.SourceFiles)
                {
                    try { if (File.GetLastWriteTimeUtc(f) > dllMtime) { stale = true; break; } }
                    catch { stale = true; break; }
                }
            }
            if (stale) continue;

            if (prebuiltAsmdefRefs.TryGetValue(node.Name, out var pre)) fresh[node.Name] = pre;
            else { try { fresh[node.Name] = MetadataReference.CreateFromFile(dll); } catch { } }
        }
        return fresh;
    }

    /// <summary>For each user asmdef, find its compiled DLL in Library/ScriptAssemblies and check
    /// whether any of its source files are NEWER than the DLL. Helps explain false-positive
    /// missing-type errors: "Unity hasn't recompiled this asmdef since the source change".</summary>
    static void DetectStaleDlls(string projectPath, List<LintAsmdef.AsmdefNode> userAsmdefs,
                                string scriptAsmDir, List<StaleDllInfo> stale)
    {
        if (!Directory.Exists(scriptAsmDir)) return;
        foreach (var node in userAsmdefs)
        {
            if (node.AsmdefPath == null) continue;     // predefined have no single source dir
            if (node.SourceFiles.Count == 0) continue;
            string dll = Path.Combine(scriptAsmDir, node.Name + ".dll");
            if (!File.Exists(dll)) continue;
            DateTime dllMtime;
            try { dllMtime = File.GetLastWriteTimeUtc(dll); } catch { continue; }
            DateTime newest = DateTime.MinValue;
            string newestFile = null;
            foreach (var f in node.SourceFiles)
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) { newest = t; newestFile = f; }
                }
                catch { }
            }
            if (newest > dllMtime)
            {
                stale.Add(new StaleDllInfo
                {
                    AsmdefName = node.Name,
                    DllMtime = dllMtime,
                    NewestSourceMtime = newest,
                    NewestSourcePath = newestFile,
                });
            }
        }
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
        int predefinedBase = (depth.Values.Count > 0 ? depth.Values.Max() : 0) + 1;
        // Stagger predefined assemblies so cross-sibling refs resolve in order:
        // firstpass < (CSharp + EditorFirstpass) < CSharpEditor.
        foreach (var p in userAsmdefs.Where(a => a.AsmdefPath == null))
        {
            if (p.Name == "Assembly-CSharp-firstpass") depth[p] = predefinedBase;
            else if (p.Name == "Assembly-CSharp" || p.Name == "Assembly-CSharp-Editor-firstpass") depth[p] = predefinedBase + 1;
            else if (p.Name == "Assembly-CSharp-Editor") depth[p] = predefinedBase + 2;
            else depth[p] = predefinedBase + 1;
        }

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
        IReadOnlyDictionary<string, MetadataReference> freshDllRefs,
        string[] builtinDefines, string[] userDefines,
        IReadOnlyDictionary<string, string> fileTexts,
        LintCscRsp.Options projectRsp,
        IReadOnlyList<Microsoft.CodeAnalysis.ISourceGenerator> sourceGenerators,
        bool projectAllowUnsafe,
        CancellationToken ct = default)
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

        // Incremental fast-path: if Unity's prebuilt DLL is newer than every input, reuse it
        // verbatim as the downstream MetadataReference and skip the compile. Computed up-front
        // in ComputeFreshAsmdefs (see Run for the rationale + edge cases).
        if (freshDllRefs != null && freshDllRefs.TryGetValue(node.Name, out var freshRef))
        {
            result.Skipped = true;
            result.SkipReason = "DLL up-to-date (no source changes)";
            result.EmittedRef = freshRef;
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

        // Build per-asmdef preprocessor symbol set. We always emulate Unity's *editor* compilation
        // (LINT runs at dev time only) so UNITY_EDITOR is always defined, just like Unity itself
        // does when you click Play / open the project. Without it, runtime asmdefs surface
        // "missing member" false positives on UNITY_EDITOR-gated public APIs (e.g. MagicaCloth's
        // GizmoSerializeData property) that the corresponding *.Editor asmdef relies on.
        var defines = new List<string>(builtinDefines);
        defines.AddRange(userDefines);
        defines.Add("UNITY_EDITOR");

        // Per-asmdef csc.rsp (sibling of asmdef file): merge with project-wide rsp.
        LintCscRsp.Options effectiveRsp = projectRsp ?? new LintCscRsp.Options();
        if (node.AsmdefPath != null)
        {
            string asmdefDir = Path.GetDirectoryName(node.AsmdefPath);
            if (asmdefDir != null)
            {
                string asmdefRspPath = Path.Combine(asmdefDir, Path.GetFileNameWithoutExtension(node.AsmdefPath) + ".rsp");
                if (!File.Exists(asmdefRspPath)) asmdefRspPath = Path.Combine(asmdefDir, "csc.rsp");
                if (File.Exists(asmdefRspPath))
                    effectiveRsp = LintCscRsp.Merge(effectiveRsp, LintCscRsp.Parse(asmdefRspPath));
            }
        }
        defines.AddRange(effectiveRsp.Defines);

        // versionDefines: each entry adds a `define` symbol if the named package is installed
        // and its version satisfies the expression. Resolved against Library/PackageCache.
        if (node.VersionDefines.Count > 0)
        {
            foreach (var vd in node.VersionDefines)
            {
                if (string.IsNullOrEmpty(vd.Name) || string.IsNullOrEmpty(vd.Define)) continue;
                string installedVersion = LookupPackageVersion(graph, vd.Name);
                if (installedVersion == null) continue;
                if (SemverSatisfies(installedVersion, vd.Expression))
                    defines.Add(vd.Define);
            }
        }

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

        // Resolve LangVersion: rsp wins, otherwise Latest. `preview` and `latest` keywords + numeric.
        var langVer = LanguageVersion.Latest;
        if (!string.IsNullOrEmpty(effectiveRsp.LangVersion))
        {
            if (LanguageVersionFacts.TryParse(effectiveRsp.LangVersion, out var parsed))
                langVer = parsed;
        }
        var parseOpts = CSharpParseOptions.Default
            .WithLanguageVersion(langVer)
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
        {
            var pool = isEditor ? editorRefs : runtimeRefs;
            if (node.OverrideReferences && node.PrecompiledReferences.Count > 0)
            {
                // overrideReferences=true → keep UnityEngine/UnityEditor modules + only the
                // *named* precompiledReferences entries. Other precompiled DLLs are excluded
                // (Unity does this to let asmdefs pick exactly which version of a duplicated
                // type-bearing DLL they want — e.g. Photon Fusion's CodeGen wants Mono.Cecil
                // but NOT Unity.Burst.Cecil; both define `TypeDefinition` → CS0433 ambiguity).
                var allowedNames = new HashSet<string>(node.PrecompiledReferences, StringComparer.OrdinalIgnoreCase);
                foreach (var r in pool)
                {
                    string fname = Path.GetFileName(r.Display ?? "");
                    if (fname.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)
                        || fname.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase)
                        || fname.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
                        || fname.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase)
                        || fname.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                        || fname.Equals("System.dll", StringComparison.OrdinalIgnoreCase)
                        || allowedNames.Contains(fname))
                    {
                        refs.Add(r);
                    }
                }
            }
            else
            {
                refs.AddRange(pool);
            }
        }

        // Predefined-assembly implicit reference graph (Unity convention):
        //   Assembly-CSharp-firstpass        → autoReferenced asmdefs
        //   Assembly-CSharp-Editor-firstpass → Assembly-CSharp-firstpass + autoReferenced
        //   Assembly-CSharp                  → Assembly-CSharp-firstpass + autoReferenced
        //   Assembly-CSharp-Editor           → Assembly-CSharp + Assembly-CSharp-firstpass +
        //                                      Assembly-CSharp-Editor-firstpass + autoReferenced
        // Wires across the four predefined siblings so editor-side types see their runtime sibling
        // (otherwise FlockChildEditor in Assembly-CSharp-Editor can't find FlockChild from Assembly-CSharp).
        if (node == graph.AssemblyCSharp || node == graph.AssemblyCSharpEditor
            || node == graph.AssemblyCSharpFirstpass || node == graph.AssemblyCSharpEditorFirstpass)
        {
            void TryAddSibling(LintAsmdef.AsmdefNode sibling)
            {
                if (sibling == null || sibling == node) return;
                if (emittedRefs.TryGetValue(sibling.Name, out var er)) refs.Add(er);
                else if (prebuiltAsmdefRefs.TryGetValue(sibling.Name, out var pr)) refs.Add(pr);
            }
            // Sibling chain — only meaningful refs added (skip self).
            if (node == graph.AssemblyCSharpEditor)
            {
                TryAddSibling(graph.AssemblyCSharp);
                TryAddSibling(graph.AssemblyCSharpFirstpass);
                TryAddSibling(graph.AssemblyCSharpEditorFirstpass);
            }
            else if (node == graph.AssemblyCSharp)
            {
                TryAddSibling(graph.AssemblyCSharpFirstpass);
            }
            else if (node == graph.AssemblyCSharpEditorFirstpass)
            {
                TryAddSibling(graph.AssemblyCSharpFirstpass);
            }
            // Auto-referenced asmdefs visible to all predefined assemblies.
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

        // Apply nowarn from rsp via specific-diagnostic options.
        var diagOpts = new Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal);
        foreach (var w in effectiveRsp.NoWarn) diagOpts[w] = ReportDiagnostic.Suppress;
        var compilation = CSharpCompilation.Create(
            assemblyName: node.Name,
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                // Predefined assemblies inherit `allowUnsafeCode` from PlayerSettings; user asmdefs
                // declare their own. Either source can opt in.
                allowUnsafe: node.AllowUnsafeCode || effectiveRsp.AllowUnsafe
                          || (node.AsmdefPath == null && projectAllowUnsafe),
                concurrentBuild: true,
                nullableContextOptions: NullableFromRsp(effectiveRsp.Nullable),
                specificDiagnosticOptions: diagOpts));

        // Run discovered source generators so types they emit (Burst, Mirror, MessagePack-CSharp,
        // Photon Fusion, etc.) are visible to GetDiagnostics + Emit. Cancellation is forwarded
        // — a runaway generator must be killable or the whole lint hangs.
        if (sourceGenerators != null && sourceGenerators.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            compilation = LintSourceGenerators.RunGenerators(compilation, sourceGenerators, parseOpts, ct);
        }

        // Emit to in-memory bytes so downstream asmdefs can ref this one. Cancellation is
        // forwarded so the level watchdog / budget can actually stop a long compile.
        try
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms, cancellationToken: ct);
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
                foreach (var d in compilation.GetDiagnostics(ct)) result.Diagnostics.Add(d);
                // Dedupe.
                result.Diagnostics = result.Diagnostics.Distinct().ToList();
            }
        }
        catch (OperationCanceledException)
        {
            result.Skipped = true;
            result.SkipReason = "cancelled (budget or no-progress watchdog)";
        }
        catch (Exception ex)
        {
            result.SkipReason = $"emit threw: {ex.GetType().Name}: {ex.Message}";
            try { foreach (var d in compilation.GetDiagnostics(ct)) result.Diagnostics.Add(d); }
            catch (OperationCanceledException) { }
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
        string incNote = run.FreshAsmdefCount > 0 ? $", {run.FreshAsmdefCount} incremental-skipped" : "";
        sb.AppendLine($"Files: {totalFiles}  Errors: {totalErrors}{(includeWarnings ? $"  Warnings: {totalWarnings}" : "")}  Mode: unity (asmdef-aware, {asmdefsCompiled} compiled, {asmdefsSkipped} skipped{incNote}, {run.UserAsmdefCount} user asmdefs, {run.RefCount} engine refs, {run.FilterDebugCount} dupe-refs filtered, {run.SourceGeneratorCount} source generators, {run.TotalMs}ms)");
        if (!string.IsNullOrEmpty(run.ProjectRspNote))
            sb.AppendLine(run.ProjectRspNote);
        if (run.StaleDlls.Count > 0)
        {
            sb.AppendLine($"Stale ScriptAssemblies DLLs ({run.StaleDlls.Count}) — Unity hasn't recompiled these asmdefs since their source changed; missing-type errors below may be false positives. Run COMPILE to refresh:");
            foreach (var s in run.StaleDlls.Take(10))
                sb.AppendLine($"  {s.AsmdefName} (DLL {s.DllMtime:yyyy-MM-dd HH:mm} < source {s.NewestSourceMtime:HH:mm})");
            if (run.StaleDlls.Count > 10) sb.AppendLine($"  ... +{run.StaleDlls.Count - 10} more");
        }
        if (totalErrors == 0 && (!includeWarnings || totalWarnings == 0))
        {
            sb.Append("OK — no errors. Per-asmdef compile passed.\nClosest to Unity's compile pipeline without running Unity.");
            return sb.ToString();
        }
        sb.AppendLine();
        lines.Sort(StringComparer.Ordinal);
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
