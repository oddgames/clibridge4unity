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
        public bool AllowUnsafeCode;             // ProjectSettings.allowUnsafeCode → predefined Assembly-CSharp gets -unsafe
        public string EditorRoot;
        public string UnityVersion;
    }

    static Cache _cache;
    static readonly object _lock = new();

    /// <summary>Build (or fetch cached) references + parse options for `projectPath`.</summary>
    public static (List<MetadataReference> refs, string[] builtinDefines, string[] userDefines,
                   bool allowUnsafeCode, string editorRoot, string unityVersion, string error)
        Resolve(string projectPath)
    {
        lock (_lock)
        {
            string versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(versionFile))
                return (null, null, null, false, null, null,
                        "ProjectVersion.txt missing — not a Unity project.");

            string version = ReadEditorVersion(versionFile);
            if (string.IsNullOrEmpty(version))
                return (null, null, null, false, null, null, "Could not read Unity version.");

            string editorRoot = FindEditorRoot(version);
            if (editorRoot == null)
                return (null, null, null, false, null, null,
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
                        _cache.AllowUnsafeCode, _cache.EditorRoot, _cache.UnityVersion, null);
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

            // 1) BCL — use Unity's actual ref-assembly set (`UnityReferenceAssemblies/unity-4.8-api/`),
            //    the same dir Unity bakes into the generated `Assembly-CSharp.csproj`'s mscorlib /
            //    System / System.Core / netstandard `<HintPath>` entries. This dir has the full
            //    Mono mscorlib (with ValueType/Enum/Delegate) AND the `Facades/` subdir with
            //    System.Threading.Tasks.Extensions.dll (ValueTask), netstandard.dll, etc. — both
            //    are needed: dropping the mscorlib root → CS0012 (Enum not in mscorlib),
            //    dropping Facades → CS7069 (ValueTask claimed in mscorlib).
            string unityApi = Path.Combine(editorRoot, "Data", "UnityReferenceAssemblies", "unity-4.8-api");
            if (Directory.Exists(unityApi))
            {
                AddDir(unityApi);
                // Facades/ — load most netstandard split assemblies (System.IO.dll, System.Runtime.dll,
                // etc.) but EXCLUDE ones that re-define types already in the full Mono `System.dll`
                // we just added. e.g. `Facades/System.CodeDom.dll` defines `CodeGenerator`, which
                // also lives in `unity-4.8-api/System.dll` → CS0433 ambiguity. Unity itself only
                // adds System.CodeDom when explicitly referenced; mirror that.
                string facadesDir = Path.Combine(unityApi, "Facades");
                if (Directory.Exists(facadesDir))
                {
                    var facadeSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.CodeDom.dll" };
                    foreach (var f in Directory.EnumerateFiles(facadesDir, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        string n = Path.GetFileName(f);
                        if (facadeSkip.Contains(n)) continue;
                        if (!seen.Add(n)) continue;
                        try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
                    }
                }
            }
            else
            {
                // Fallback chain: NetStandard 2.1 ref → Mono BCL (older Unity).
                string compatNs = Path.Combine(editorRoot, "Data", "NetStandard", "compat", "2.1.0", "shims", "netstandard");
                string nsRoot = Path.Combine(editorRoot, "Data", "NetStandard", "ref", "2.1.0");
                if (Directory.Exists(compatNs))
                {
                    AddDir(compatNs);
                    if (Directory.Exists(nsRoot)) AddDir(nsRoot);
                }
                else
                {
                    AddDir(Path.Combine(editorRoot, "Data", "MonoBleedingEdge", "lib", "mono", "unityjit-win32"));
                }
            }
            // 2) Unity engine + editor modules. Filter to UnityEngine.*/UnityEditor.* only —
            //    the dir also contains Unity internals (System.CodeDom.dll, Newtonsoft.Json.dll,
            //    Bee.*, Unity.Cecil.*, etc.) that user code shouldn't see. Including them causes
            //    duplicate-type ambiguity with `unity-4.8-api/System.dll` (CS0433 on CodeGenerator).
            void AddUnityModulesIn(string dir)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "UnityEngine*.dll", SearchOption.TopDirectoryOnly))
                { var n = Path.GetFileName(f); if (seen.Add(n)) try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { } }
                foreach (var f in Directory.EnumerateFiles(dir, "UnityEditor*.dll", SearchOption.TopDirectoryOnly))
                { var n = Path.GetFileName(f); if (seen.Add(n)) try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { } }
            }
            AddUnityModulesIn(Path.Combine(editorRoot, "Data", "Managed", "UnityEngine"));
            AddUnityModulesIn(Path.Combine(editorRoot, "Data", "Managed"));
            AddUnityModulesIn(Path.Combine(editorRoot, "Data", "Managed", "UnityEditor"));
            // 4) Library/ScriptAssemblies — Unity-compiled outputs. Includes packages (e.g.
            //    Unity.TextMeshPro.dll) AND user asmdef outputs. We MUST skip user-asmdef DLLs
            //    here, because LINT semantic re-parses their source from Assets/. Loading both
            //    the source AND the prebuilt DLL gives every type two definitions → cascading
            //    CS0121/CS0433/CS0104 false positives ("ambiguous" / "defined in multiple
            //    assemblies"). Only keep DLLs whose source is OUTSIDE Assets/ (= packages).
            string scriptAsmDir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAsmDir))
            {
                var skipDllNames = CollectUserAsmdefDllNames(projectPath);
                foreach (var dll in Directory.EnumerateFiles(scriptAsmDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    string asmName = Path.GetFileNameWithoutExtension(dll);
                    if (skipDllNames.Contains(asmName)) continue;
                    string fileName = Path.GetFileName(dll);
                    if (!seen.Add(fileName)) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { }
                }
            }
            // 4b) Plugin DLLs under Assets/ (e.g., Photon Fusion, Google APIs, third-party SDKs).
            //     Unity auto-references these for Assembly-CSharp + asmdefs without overrideReferences.
            //     Skip native (architecture suffix) and skip if name collides with Library/ScriptAssemblies entry.
            string assetsDir2 = Path.Combine(projectPath, "Assets");
            if (Directory.Exists(assetsDir2))
            {
                foreach (var f in Directory.EnumerateFiles(assetsDir2, "*.dll", SearchOption.AllDirectories))
                {
                    string norm = f.Replace('\\', '/');
                    if (norm.Contains("WINARM64", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/x86_64/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/Android/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/iOS/", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileName(f);
                    if (!seen.Add(name)) continue;
                    if (!IsManagedDll(f)) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
                }
            }

            // 5) Package PRECOMPILED .dll refs — Library/PackageCache + any `file:` UPM packages
            //    declared in manifest.json (live outside the project tree). Skip native DLLs by
            //    architecture-suffix path heuristic + PE-header probe.
            void ScanPackageDirs(IEnumerable<string> pkgDirs)
            {
                foreach (var pkgDir in pkgDirs)
                {
                    if (!Directory.Exists(pkgDir)) continue;
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(pkgDir, "*.dll", SearchOption.AllDirectories); }
                    catch { continue; }
                    foreach (var f in files)
                    {
                        string norm = f.Replace('\\', '/');
                        if (norm.Contains("WINARM64", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/x86_64/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/Android/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (norm.Contains("/iOS/", StringComparison.OrdinalIgnoreCase)) continue;
                        string name = Path.GetFileName(f);
                        if (!seen.Add(name)) continue;
                        if (!IsManagedDll(f)) continue;
                        try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
                    }
                }
            }
            // Scan only the package dirs Unity actually RESOLVED — not every subdir of
            // Library/PackageCache. Stale leftover folders (e.g. an old `foo@<oldhash>` beside
            // the live `foo@<newhash>`) otherwise get scanned too, and since the dedup above is
            // by DLL filename, the alphabetically-first folder wins — which can be the stale one,
            // binding types against an outdated DLL and producing phantom errors (e.g. CS1739 for
            // a parameter the live package version changed). See LintAsmdef.ResolvedPackageCacheDirs.
            string pkgCacheRoot = Path.Combine(projectPath, "Library", "PackageCache");
            var resolvedPkgDirs = LintAsmdef.ResolvedPackageCacheDirs(projectPath);
            if (resolvedPkgDirs.Count > 0)
                ScanPackageDirs(resolvedPkgDirs);
            else if (Directory.Exists(pkgCacheRoot)) // fallback: pre-resolve checkout, no ProjectCache yet
            {
                try { ScanPackageDirs(Directory.EnumerateDirectories(pkgCacheRoot)); } catch { }
            }
            // `file:` UPM packages — manifest.json `"name": "file:C:/abs/path"` resolves to that
            // dir. Treat each as a single package (NOT a parent containing many packages).
            foreach (var filePkg in LintAsmdef.ResolveFilePackagePaths(projectPath))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(filePkg, "*.dll", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                {
                    string norm = f.Replace('\\', '/');
                    if (norm.Contains("WINARM64", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/x86_64/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/Android/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/iOS/", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileName(f);
                    if (!seen.Add(name)) continue;
                    if (!IsManagedDll(f)) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
                }
            }

            // Defines: prefer Unity's authoritative `Assembly-CSharp.csproj` <DefineConstants>
            // when present — Unity strips scriptingDefineSymbols entries for packages it
            // no longer recognizes (e.g. `PHOTON_UNITY_NETWORKING` when Photon Pun is uninstalled
            // but the user-written define lingers in ProjectSettings.asset). Reading the YAML
            // directly causes false-positive errors on `#if PHOTON_UNITY_NETWORKING` blocks.
            // Fallback: compute defines manually (older or non-IDE-projected projects).
            string asmCSharpCsproj = Path.Combine(projectPath, "Assembly-CSharp.csproj");
            string[] csprojDefines = ReadCsprojDefines(asmCSharpCsproj);
            string[] userDefines;
            string[] builtinDefines;
            if (csprojDefines.Length > 0)
            {
                builtinDefines = csprojDefines;
                userDefines = Array.Empty<string>();
            }
            else
            {
                userDefines = ReadScriptingDefines(projSettingsAsset);
                string[] moduleDefines = BuildModuleDefines(Path.Combine(projectPath, "Packages", "manifest.json"));
                builtinDefines = BuildBuiltinDefines(version).Concat(moduleDefines).ToArray();
            }
            bool allowUnsafe = ReadAllowUnsafeCode(projSettingsAsset);

            _cache = new Cache
            {
                ProjectPath = projectPath,
                UnityDllMtime = engineMtime,
                ProjectSettingsMtime = psMtime,
                References = refs,
                BuiltinDefines = builtinDefines,
                UserDefines = userDefines,
                AllowUnsafeCode = allowUnsafe,
                EditorRoot = editorRoot,
                UnityVersion = version
            };
            return (refs, builtinDefines, userDefines, allowUnsafe, editorRoot, version, null);
        }
    }

    /// <summary>Read `<DefineConstants>X;Y;Z</DefineConstants>` from a Unity-generated csproj.
    /// Returns empty if the file is missing or unreadable. The csproj is regenerated by Unity
    /// on every script reload — its contents are guaranteed to match the active build target's
    /// effective defines (post version-define pruning).</summary>
    static string[] ReadCsprojDefines(string csprojPath)
    {
        if (!File.Exists(csprojPath)) return Array.Empty<string>();
        try
        {
            foreach (var raw in File.ReadLines(csprojPath))
            {
                string line = raw.Trim();
                int o = line.IndexOf("<DefineConstants>", StringComparison.Ordinal);
                if (o < 0) continue;
                int s = o + "<DefineConstants>".Length;
                int e = line.IndexOf("</DefineConstants>", s, StringComparison.Ordinal);
                if (e < 0) continue;
                string body = line.Substring(s, e - s);
                var defs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in body.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = t.Trim();
                    if (trimmed.Length > 0 && IsValidIdentifier(trimmed)) defs.Add(trimmed);
                }
                return defs.ToArray();
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    /// <summary>For each `com.unity.modules.<name>` listed in the project's manifest.json,
    /// emit Unity's corresponding `ENABLE_<NAME>` symbol. Unity sets these automatically when
    /// the module is present; third-party packages gate code with them
    /// (UniTask's `#if ENABLE_UNITYWEBREQUEST`, etc.).</summary>
    static string[] BuildModuleDefines(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return Array.Empty<string>();
        var defines = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return Array.Empty<string>();
            foreach (var prop in deps.EnumerateObject())
            {
                if (!prop.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue;
                string modName = prop.Name.Substring("com.unity.modules.".Length);
                // Convention: ENABLE_<UPPERCASE>. Unity has a few well-known special cases.
                string enableSym = modName.ToUpperInvariant() switch
                {
                    "PHYSICS"          => "ENABLE_PHYSICS",
                    "PHYSICS2D"        => "ENABLE_PHYSICS2D",
                    "PARTICLESYSTEM"   => "ENABLE_PARTICLE_SYSTEM",
                    "TEXTCORE"         => "ENABLE_TEXTCORE",
                    "TEXTRENDERING"    => "ENABLE_TEXT_RENDERING",
                    "UNITYWEBREQUEST"  => "ENABLE_UNITYWEBREQUEST",
                    "UNITYWEBREQUESTASSETBUNDLE" => "ENABLE_UNITYWEBREQUEST_ASSETBUNDLE",
                    "UNITYWEBREQUESTAUDIO"       => "ENABLE_UNITYWEBREQUEST_AUDIO",
                    "UNITYWEBREQUESTTEXTURE"     => "ENABLE_UNITYWEBREQUEST_TEXTURE",
                    "UNITYWEBREQUESTWWW"         => "ENABLE_UNITYWEBREQUEST_WWW",
                    "AUDIO"            => "ENABLE_AUDIO",
                    "ANIMATION"        => "ENABLE_ANIMATION",
                    "VIDEO"            => "ENABLE_VIDEO",
                    "TERRAIN"          => "ENABLE_TERRAIN",
                    "TERRAINPHYSICS"   => "ENABLE_TERRAIN_PHYSICS",
                    "CLOTH"            => "ENABLE_CLOTH",
                    "AI"               => "ENABLE_NAVMESH",
                    "VR"               => "ENABLE_VR",
                    "XR"               => "ENABLE_XR",
                    "AR"               => "ENABLE_AR",
                    "TILEMAP"          => "ENABLE_TILEMAP",
                    "UI"               => "ENABLE_UNET",  // legacy
                    _                   => "ENABLE_" + modName.ToUpperInvariant(),
                };
                defines.Add(enableSym);
            }
        }
        catch { }
        return defines.ToArray();
    }

    /// <summary>Read `allowUnsafeCode: 1` from ProjectSettings.asset. Unity passes `-unsafe` to
    /// the predefined Assembly-CSharp* assemblies when this is set.</summary>
    static bool ReadAllowUnsafeCode(string projSettingsAsset)
    {
        if (!File.Exists(projSettingsAsset)) return false;
        try
        {
            foreach (var line in File.ReadLines(projSettingsAsset))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim();
                if (!key.Equals("allowUnsafeCode", StringComparison.Ordinal)) continue;
                string val = line.Substring(colon + 1).Trim();
                return val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
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

    /// <summary>Unity built-in defines (always-present): UNITY_*, version cumulative defines.
    /// Unity emits the FULL ladder of `UNITY_X_Y_OR_NEWER` symbols from 5.3 (oldest) up to current —
    /// missing any rung causes false-positive errors on legacy code paths gated by `#if UNITY_5_5_OR_NEWER`.</summary>
    static string[] BuildBuiltinDefines(string version)
    {
        var d = new List<string>
        {
            "UNITY_64", "UNITY_STANDALONE_WIN", "UNITY_STANDALONE",
            "ENABLE_MONO", "INCLUDE_DYNAMIC_GI", "ENABLE_PROFILER",
            "CSHARP_7_3_OR_NEWER",
        };
        // Parse version into major.minor.patch (e.g. "6000.3.10f1" → 6000, 3, 10).
        int major = 0, minor = 0;
        var parts = version.Split('.', 'f', 'a', 'b', 'p');
        if (parts.Length > 0) int.TryParse(parts[0], out major);
        if (parts.Length > 1) int.TryParse(parts[1], out minor);

        // Full historical ladder of UNITY_X_Y_OR_NEWER (Unity emits these cumulatively).
        // 5.3 is the floor (when this scheme was introduced).
        for (int m = 3; m <= 6; m++) d.Add($"UNITY_5_{m}_OR_NEWER");
        // 2017 → 2023: each year's 1/2/3/4 minor.
        for (int y = 2017; y <= 2023; y++)
            for (int m = 1; m <= 4; m++) d.Add($"UNITY_{y}_{m}_OR_NEWER");
        // 6000.x: cumulative through current minor.
        if (major >= 6000)
        {
            for (int m = 0; m <= Math.Max(minor, 0); m++) d.Add($"UNITY_6000_{m}_OR_NEWER");
        }
        return d.ToArray();
    }

    /// <summary>Names of DLLs in `Library/ScriptAssemblies/` that LINT semantic must NOT load
    /// because we're recompiling their source. Includes:
    ///   - Every asmdef name found under Assets/ (user-authored asmdefs).
    ///   - Predefined Assembly-CSharp / Assembly-CSharp-Editor / *-firstpass (catch-all asms
    ///     for .cs files outside any asmdef).</summary>
    static HashSet<string> CollectUserAsmdefDllNames(string projectPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assembly-CSharp",
            "Assembly-CSharp-Editor",
            "Assembly-CSharp-firstpass",
            "Assembly-CSharp-Editor-firstpass",
        };
        string assetsDir = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsDir)) return names;
        try
        {
            foreach (var asmdefPath in Directory.EnumerateFiles(assetsDir, "*.asmdef", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(asmdefPath));
                    if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var asmName = n.GetString();
                        if (!string.IsNullOrEmpty(asmName)) names.Add(asmName);
                    }
                }
                catch { }
            }
        }
        catch { }
        return names;
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
