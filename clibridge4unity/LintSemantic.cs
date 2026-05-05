using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace clibridge4unity;

/// <summary>
/// Semantic LINT: full Roslyn type-binding compile against Unity's BCL + engine + editor DLLs
/// + user-compiled assemblies in Library/ScriptAssemblies. Catches errors syntax-only mode misses
/// (missing methods, wrong arg counts, type mismatches, missing usings).
///
/// References + parse options are cached by (UnityEngine.dll mtime, ProjectSettings.asset mtime).
/// First call is ~5-10s loading ~150 DLLs; subsequent calls reuse the cache (~1s for compile).
///
/// Per-file scope: files under any /Editor/ folder get UNITY_EDITOR; runtime files do NOT.
/// This catches editor-only API leaking into runtime code (best-effort, not asmdef-perfect).
/// </summary>
internal static class LintSemantic
{
    sealed class Cache
    {
        public string ProjectPath;
        public DateTime UnityDllMtime;
        public DateTime ProjectSettingsMtime;
        public List<MetadataReference> References;
        public string[] BuiltinDefines;          // UNITY_EDITOR, UNITY_2026_*, etc.
        public string[] UserDefines;             // from ProjectSettings.asset
        public string EditorRoot;
        public string UnityVersion;
    }

    static Cache _cache;
    static readonly object _lock = new();

    /// <summary>Build (or fetch cached) references + parse options for `projectPath`.</summary>
    public static (List<MetadataReference> refs, string[] builtinDefines, string[] userDefines,
                   string editorRoot, string unityVersion, string error)
        Resolve(string projectPath)
    {
        lock (_lock)
        {
            string versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(versionFile))
                return (null, null, null, null, null,
                        "ProjectVersion.txt missing — not a Unity project.");

            string version = ReadEditorVersion(versionFile);
            if (string.IsNullOrEmpty(version))
                return (null, null, null, null, null, "Could not read Unity version.");

            string editorRoot = FindEditorRoot(version);
            if (editorRoot == null)
                return (null, null, null, null, null,
                        $"Unity {version} not installed under C:\\Program Files\\Unity\\Hub\\Editor.");

            string engineDll = Path.Combine(editorRoot, "Data", "Managed", "UnityEngine", "UnityEngine.CoreModule.dll");
            string projSettingsAsset = Path.Combine(projectPath, "ProjectSettings", "ProjectSettings.asset");
            DateTime engineMtime = File.Exists(engineDll) ? File.GetLastWriteTimeUtc(engineDll) : DateTime.MinValue;
            DateTime psMtime = File.Exists(projSettingsAsset) ? File.GetLastWriteTimeUtc(projSettingsAsset) : DateTime.MinValue;

            if (_cache != null && _cache.ProjectPath == projectPath
                && _cache.UnityDllMtime == engineMtime
                && _cache.ProjectSettingsMtime == psMtime)
            {
                return (_cache.References, _cache.BuiltinDefines, _cache.UserDefines,
                        _cache.EditorRoot, _cache.UnityVersion, null);
            }

            var refs = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddDir(string dir, string pattern = "*.dll")
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(f);
                    if (!seen.Add(name)) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
                }
            }

            // 1) BCL — Unity 6 compiles against netstandard 2.1. Use the netstandard shim DLLs
            //    (per-module impl assemblies) — the umbrella `ref/2.1.0/netstandard.dll` is just
            //    type-forwards and is INCOMPATIBLE with adding netfx shims (mscorlib v4) which
            //    re-defines the same types and triggers CS0518 cascades.
            string compatNs = Path.Combine(editorRoot, "Data", "NetStandard", "compat", "2.1.0", "shims", "netstandard");
            string nsRoot = Path.Combine(editorRoot, "Data", "NetStandard", "ref", "2.1.0");
            if (Directory.Exists(compatNs))
            {
                AddDir(compatNs);
                if (Directory.Exists(nsRoot)) AddDir(nsRoot);
                // netfx v4 facades — needed for ScriptAssemblies that forward ValueType/HashSet/etc.
                // through mscorlib v4 / System.Core v4 instead of netstandard.
                string netfxDir = Path.Combine(editorRoot, "Data", "NetStandard", "compat", "2.1.0", "shims", "netfx");
                foreach (var facade in new[] { "mscorlib.dll", "System.Core.dll", "System.dll" })
                {
                    string p = Path.Combine(netfxDir, facade);
                    if (File.Exists(p) && seen.Add(facade))
                        try { refs.Add(MetadataReference.CreateFromFile(p)); } catch { }
                }
            }
            else
            {
                // Fallback: Mono BCL (older Unity / no NetStandard).
                AddDir(Path.Combine(editorRoot, "Data", "MonoBleedingEdge", "lib", "mono", "unityjit-win32"));
            }
            // 2) Unity engine modules
            AddDir(Path.Combine(editorRoot, "Data", "Managed", "UnityEngine"));
            AddDir(Path.Combine(editorRoot, "Data", "Managed"));   // legacy UnityEngine.dll fallbacks
            // 3) Unity editor modules (only used when compiling editor-scoped files)
            AddDir(Path.Combine(editorRoot, "Data", "Managed", "UnityEditor"));
            // 4) User compiled assemblies — Unity emits these to Library/ScriptAssemblies on each compile.
            //    Includes per-package compiled outputs (e.g. Unity.TextMeshPro.dll) AND user asmdef outputs.
            //    This is the right surface — PackageCache itself is mostly source + native plugins (sqlite,
            //    burst-llvm, etc.) that cause CS0009 noise when loaded as managed metadata.
            AddDir(Path.Combine(projectPath, "Library", "ScriptAssemblies"));
            // 5) Package PRECOMPILED .dll refs only — skip any path under known native subdirs to
            //    avoid CS0009. Heuristic: skip Plugins/ and .Runtime/ subdirs.
            string pkgCache = Path.Combine(projectPath, "Library", "PackageCache");
            if (Directory.Exists(pkgCache))
            {
                foreach (var pkgDir in Directory.EnumerateDirectories(pkgCache))
                {
                    foreach (var f in Directory.EnumerateFiles(pkgDir, "*.dll", SearchOption.AllDirectories))
                    {
                        string norm = f.Replace('\\', '/');
                        if (norm.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/.Runtime/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/Lib/Editor/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/Dependencies/Assemblies/", StringComparison.OrdinalIgnoreCase)) continue;
                        // Skip anything that looks native (architecture in path)
                        if (norm.Contains("WINARM64", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/x86_64/", StringComparison.OrdinalIgnoreCase)) continue;
                        string name = Path.GetFileName(f);
                        if (!seen.Add(name)) continue;
                        // Probe PE header — managed DLLs have CLI metadata.
                        if (!IsManagedDll(f)) continue;
                        try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
                    }
                }
            }

            string[] userDefines = ReadScriptingDefines(projSettingsAsset);
            string[] builtinDefines = BuildBuiltinDefines(version);

            _cache = new Cache
            {
                ProjectPath = projectPath,
                UnityDllMtime = engineMtime,
                ProjectSettingsMtime = psMtime,
                References = refs,
                BuiltinDefines = builtinDefines,
                UserDefines = userDefines,
                EditorRoot = editorRoot,
                UnityVersion = version
            };
            return (refs, builtinDefines, userDefines, editorRoot, version, null);
        }
    }

    static string ReadEditorVersion(string versionFile)
    {
        foreach (var line in File.ReadLines(versionFile))
            if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                return line.Substring("m_EditorVersion:".Length).Trim();
        return null;
    }

    /// <summary>Returns the inner `Editor/` directory containing `Data/` — e.g.
    /// `C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor`. All BCL/engine paths
    /// hang off `<this>/Data/`.</summary>
    static string FindEditorRoot(string version)
    {
        string[] bases = {
            @"C:\Program Files\Unity\Hub\Editor",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor"),
        };
        foreach (var b in bases)
        {
            if (!Directory.Exists(b)) continue;
            string exact = Path.Combine(b, version, "Editor");
            if (Directory.Exists(Path.Combine(exact, "Data"))) return exact;
            string baseV = version.Split('f', 'a', 'b', 'p')[0];
            foreach (var dir in Directory.GetDirectories(b))
            {
                string n = Path.GetFileName(dir);
                if (n.StartsWith(baseV))
                {
                    string editorDir = Path.Combine(dir, "Editor");
                    if (Directory.Exists(Path.Combine(editorDir, "Data"))) return editorDir;
                }
            }
        }
        return null;
    }

    /// <summary>Parse ProjectSettings.asset YAML for `scriptingDefineSymbols` (all build targets, deduped).
    /// Format: `  scriptingDefineSymbols:` header followed by `    <buildTarget>: A;B;C` lines.
    /// Strict: only accepts the exact platform-keyed indent pattern to avoid leaking Color values etc.</summary>
    static string[] ReadScriptingDefines(string projSettingsAsset)
    {
        if (!File.Exists(projSettingsAsset)) return Array.Empty<string>();
        var defs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            bool inSection = false;
            int sectionIndent = -1;
            foreach (var raw in File.ReadLines(projSettingsAsset))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;

                string trimmed = line.Substring(indent);
                if (trimmed.StartsWith("scriptingDefineSymbols:", StringComparison.Ordinal))
                {
                    inSection = true;
                    sectionIndent = indent;
                    // Inline: `scriptingDefineSymbols: { 1: A;B, 7: X;Y }`
                    int brace = trimmed.IndexOf('{');
                    if (brace > 0)
                    {
                        string inner = trimmed.Substring(brace + 1).TrimEnd('}');
                        // Split on `,` between platform keys, then strip `<num>:` prefix
                        foreach (var entry in inner.Split(','))
                        {
                            int c = entry.IndexOf(':');
                            string vals = c > 0 ? entry.Substring(c + 1) : entry;
                            AddDefineTokens(vals, defs);
                        }
                        inSection = false;
                    }
                    continue;
                }
                if (!inSection) continue;

                // Section ends when indent drops back to header level or below.
                if (indent <= sectionIndent) { inSection = false; continue; }

                // Accept only exact `<digits>: <values>` lines — platform-target-keyed entries.
                int colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) { inSection = false; continue; }
                string key = trimmed.Substring(0, colonIdx).Trim();
                bool keyIsAllDigits = key.Length > 0;
                for (int i = 0; i < key.Length; i++) if (!char.IsDigit(key[i])) { keyIsAllDigits = false; break; }
                if (!keyIsAllDigits) { inSection = false; continue; }

                AddDefineTokens(trimmed.Substring(colonIdx + 1), defs);
            }
        }
        catch { }
        return defs.ToArray();
    }

    static void AddDefineTokens(string raw, HashSet<string> defs)
    {
        foreach (var tok in raw.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = tok.Trim().Trim('"', '\'');
            if (t.Length == 0) continue;
            if (!IsValidIdentifier(t)) continue;
            defs.Add(t);
        }
    }

    static bool IsValidIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        char c0 = s[0];
        if (!char.IsLetter(c0) && c0 != '_') return false;
        for (int i = 1; i < s.Length; i++)
        {
            char ch = s[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
        }
        return true;
    }

    /// <summary>Unity built-in defines (always-present): UNITY_*, version cumulative defines.</summary>
    static string[] BuildBuiltinDefines(string version)
    {
        var d = new List<string>
        {
            "UNITY_64", "UNITY_STANDALONE_WIN", "UNITY_STANDALONE",
            "ENABLE_MONO", "INCLUDE_DYNAMIC_GI", "ENABLE_PROFILER"
        };
        // Parse version into major.minor.patch (e.g. "6000.3.10f1" → 6000, 3, 10).
        int major = 0, minor = 0;
        var parts = version.Split('.', 'f', 'a', 'b', 'p');
        if (parts.Length > 0) int.TryParse(parts[0], out major);
        if (parts.Length > 1) int.TryParse(parts[1], out minor);

        // Unity emits cumulative version defines: UNITY_<MAJOR>_<MINOR>_OR_NEWER for every (major, minor) ≤ current.
        if (major >= 6000)
        {
            d.Add("UNITY_6000_0_OR_NEWER");
            for (int m = 0; m <= minor; m++) d.Add($"UNITY_6000_{m}_OR_NEWER");
            // Backwards-compat: 6000.x is also "≥ all prior 2017–2023.x" — add a representative subset.
            for (int y = 2017; y <= 2023; y++) d.Add($"UNITY_{y}_1_OR_NEWER");
        }
        else if (major >= 2017)
        {
            for (int y = 2017; y <= major; y++) d.Add($"UNITY_{y}_1_OR_NEWER");
        }
        return d.ToArray();
    }

    /// <summary>Quick PE-header probe: returns false for native DLLs / .NET assemblies without CLI metadata.
    /// Avoids CS0009 errors when Roslyn lazily loads a non-managed DLL.</summary>
    static bool IsManagedDll(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x80) return false;
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = br.ReadInt32();
            if (peOffset < 0 || peOffset > fs.Length - 24) return false;
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return false; // "PE\0\0"
            // Skip COFF (20 bytes) + magic (2)
            fs.Seek(peOffset + 24, SeekOrigin.Begin);
            ushort magic = br.ReadUInt16();
            int dataDirOffset = magic == 0x20B ? 112 : 96; // PE32+ vs PE32
            // Data directory 14 = CLR header (each entry = 8 bytes)
            fs.Seek(peOffset + 24 + dataDirOffset + 14 * 8, SeekOrigin.Begin);
            uint clrRva = br.ReadUInt32();
            uint clrSize = br.ReadUInt32();
            return clrRva != 0 && clrSize != 0;
        }
        catch { return false; }
    }

    /// <summary>True if `path` is under any `/Editor/` folder — gets UNITY_EDITOR define.</summary>
    public static bool IsEditorScope(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Contains("/Editor/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Build a CSharpParseOptions with the right preprocessor symbols for the given file.</summary>
    public static CSharpParseOptions BuildParseOptions(string file, string[] builtin, string[] user)
    {
        var symbols = new List<string>(builtin);
        symbols.AddRange(user);
        if (IsEditorScope(file)) symbols.Add("UNITY_EDITOR");
        return CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols);
    }
}
