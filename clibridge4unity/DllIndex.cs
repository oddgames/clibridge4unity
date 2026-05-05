using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;

namespace clibridge4unity;

/// <summary>
/// Background-built index of types declared in plugin DLLs and precompiled package assemblies.
/// Used as a fallback when CODE_ANALYZE can't find a type in source — many Unity projects
/// ship game logic in `Assets/Plugins/*.dll`, UPM packages distribute as DLLs in
/// `Library/PackageCache/*/Bin/*.dll`, etc. Source-only scan misses these.
/// </summary>
internal sealed class DllIndex
{
    public sealed class TypeInfo
    {
        public string FullName;        // Game.Models.LiveryUnlocked
        public string Name;            // LiveryUnlocked
        public string Namespace;
        public string AssemblyName;    // simple name
        public string AssemblyPath;    // full disk path
        public string Kind;            // class | struct | interface | enum | delegate
        public string BaseType;
        public List<string> Interfaces = new();
        public List<string> Methods = new();
        public List<string> Fields = new();
        public List<string> Properties = new();
        public List<string> Events = new();
        public List<string> NestedTypes = new();
        public List<string> EnumValues = new();
    }

    readonly ConcurrentDictionary<string, List<TypeInfo>> _byName = new(StringComparer.Ordinal);
    readonly string _projectPath;
    int _dllCount;
    int _typeCount;
    long _buildMs;
    volatile bool _ready;

    public bool Ready => _ready;
    public int DllCount => _dllCount;
    public int TypeCount => _typeCount;
    public long BuildMs => _buildMs;

    public DllIndex(string projectPath) => _projectPath = projectPath;

    public List<TypeInfo> Lookup(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;
        if (!_byName.TryGetValue(simpleName, out var list) || list == null) return null;
        // Rank: UnityEngine.* first, then UnityEditor.*, System.* deprioritized, then alphabetical.
        // Plain `Vector3` should surface UnityEngine.Vector3 before random plugin clones.
        return list.OrderBy(Rank).ThenBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    static int Rank(TypeInfo t)
    {
        string ns = t.Namespace ?? "";
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) return 0;
        if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return 1;
        if (ns.StartsWith("Unity.", StringComparison.Ordinal)) return 2;
        if (ns.StartsWith("System", StringComparison.Ordinal)) return 9;
        if (ns.StartsWith("Microsoft", StringComparison.Ordinal)) return 9;
        if (ns.StartsWith("Mono", StringComparison.Ordinal)) return 9;
        return 5;
    }

    /// <summary>Index every readable DLL under Assets/, Packages/, Library/PackageCache/.
    /// Skips Library/ScriptAssemblies — those are compiled from .cs source already in the syntax-tree index.</summary>
    public void Build()
    {
        var sw = Stopwatch.StartNew();
        var dlls = EnumerateDlls(_projectPath).ToList();
        Interlocked.Exchange(ref _dllCount, dlls.Count);

        Parallel.ForEach(dlls, dll =>
        {
            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadSymbols = false });
                string asmName = asm.Name?.Name ?? Path.GetFileNameWithoutExtension(dll);
                foreach (var module in asm.Modules)
                    foreach (var type in EnumerateTypes(module.Types))
                    {
                        if (type.Name.StartsWith("<") || type.Name.Contains("$")) continue; // compiler-generated
                        var info = MakeTypeInfo(type, asmName, dll);
                        var list = _byName.GetOrAdd(info.Name, _ => new List<TypeInfo>());
                        lock (list) list.Add(info);
                        Interlocked.Increment(ref _typeCount);
                    }
            }
            catch { /* unreadable, native, mixed-mode — skip */ }
        });

        sw.Stop();
        Interlocked.Exchange(ref _buildMs, sw.ElapsedMilliseconds);
        _ready = true;
    }

    static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            if (t.HasNestedTypes)
                foreach (var n in EnumerateTypes(t.NestedTypes))
                    yield return n;
        }
    }

    static TypeInfo MakeTypeInfo(TypeDefinition t, string asmName, string asmPath)
    {
        bool isDelegate = t.BaseType?.FullName == "System.MulticastDelegate" || t.BaseType?.FullName == "System.Delegate";
        var info = new TypeInfo
        {
            FullName = t.FullName,
            Name = t.Name,
            Namespace = t.Namespace,
            AssemblyName = asmName,
            AssemblyPath = asmPath,
            BaseType = t.BaseType?.FullName,
            Kind = t.IsEnum ? "enum"
                 : t.IsInterface ? "interface"
                 : isDelegate ? "delegate"
                 : t.IsValueType ? "struct"
                 : "class",
        };
        foreach (var i in t.Interfaces)
            info.Interfaces.Add(i.InterfaceType?.FullName ?? "?");

        foreach (var m in t.Methods)
        {
            // Skip property/event accessors (already covered by Properties/Events) and compiler-generated.
            if (m.IsGetter || m.IsSetter || m.IsAddOn || m.IsRemoveOn) continue;
            if (m.Name.StartsWith("<")) continue;
            string parms = string.Join(", ", m.Parameters.Select(p => $"{ShortName(p.ParameterType)} {p.Name}"));
            string mods = (m.IsStatic ? "static " : "") + Visibility(m);
            string sig = m.IsConstructor
                ? $"{mods}{t.Name}({parms})"
                : $"{mods}{ShortName(m.ReturnType)} {m.Name}({parms})";
            info.Methods.Add(sig);
        }
        foreach (var f in t.Fields)
        {
            if (f.Name == "value__") continue;
            if (t.IsEnum) { info.EnumValues.Add(f.Name); continue; }
            if (f.Name.StartsWith("<")) continue;
            string mods = (f.IsStatic ? "static " : "") + VisibilityField(f);
            info.Fields.Add($"{mods}{ShortName(f.FieldType)} {f.Name}");
        }
        foreach (var p in t.Properties)
            info.Properties.Add($"{ShortName(p.PropertyType)} {p.Name}");
        foreach (var e in t.Events)
            info.Events.Add($"event {ShortName(e.EventType)} {e.Name}");
        foreach (var n in t.NestedTypes)
            if (!n.Name.StartsWith("<") && !n.Name.Contains("$"))
                info.NestedTypes.Add(n.Name);
        return info;
    }

    static string ShortName(TypeReference tr)
    {
        if (tr == null) return "?";
        // Strip generic arity suffix `1 from `List`1` → `List<T>` style.
        string n = tr.Name;
        int tick = n.IndexOf('`');
        if (tick > 0) n = n.Substring(0, tick);
        if (tr is GenericInstanceType git)
        {
            string args = string.Join(", ", git.GenericArguments.Select(ShortName));
            return $"{n}<{args}>";
        }
        if (tr.HasGenericParameters)
        {
            string args = string.Join(", ", tr.GenericParameters.Select(p => p.Name));
            return $"{n}<{args}>";
        }
        return n;
    }

    static string Visibility(MethodDefinition m)
    {
        if (m.IsPublic) return "public ";
        if (m.IsFamily) return "protected ";
        if (m.IsAssembly) return "internal ";
        if (m.IsPrivate) return "private ";
        return "";
    }

    static string VisibilityField(FieldDefinition f)
    {
        if (f.IsPublic) return "public ";
        if (f.IsFamily) return "protected ";
        if (f.IsAssembly) return "internal ";
        if (f.IsPrivate) return "private ";
        return "";
    }

    static IEnumerable<string> EnumerateDlls(string projectPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Project DLLs — plugins and resolved package binaries.
        var roots = new List<string>
        {
            Path.Combine(projectPath, "Assets"),
            Path.Combine(projectPath, "Packages"),
            Path.Combine(projectPath, "Library", "PackageCache"),
        };

        // Unity engine reference DLLs (UnityEngine.CoreModule.dll, etc.) live in the Unity install,
        // not the project. Without these, queries for built-in types like MonoBehaviour or Vector3
        // fall back to noise from unrelated source matches. Find install via ProjectVersion.txt
        // + standard Hub path. Recursive scan covers Managed/UnityEngine + MonoBleedingEdge subdirs.
        string unityManaged = FindUnityManagedDir(projectPath);
        if (unityManaged != null) roots.Add(unityManaged);

        foreach (var dir in roots)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                // Skip native interop stubs and runtime libs that Cecil can't read or that
                // duplicate engine types unhelpfully.
                string n = Path.GetFileName(f);
                if (n.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StartsWith("ucrtbase", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(f)) yield return f;
            }
        }
    }

    /// <summary>Locate `<UnityHub>/<version>/Editor/Data/Managed/UnityEngine/` so engine reference
    /// DLLs (UnityEngine.CoreModule, etc.) get indexed. Returns null if Unity isn't installed at the
    /// expected path — type lookup for built-ins simply won't include them in that case.</summary>
    static string FindUnityManagedDir(string projectPath)
    {
        try
        {
            // Parse ProjectSettings/ProjectVersion.txt: "m_EditorVersion: 6000.0.58f2"
            string vfile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(vfile)) return null;
            string version = null;
            foreach (var line in File.ReadAllLines(vfile))
            {
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                {
                    version = line.Substring("m_EditorVersion:".Length).Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(version)) return null;

            var candidates = new[]
            {
                // Windows Hub
                $@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Data\Managed",
                $@"C:\Program Files (x86)\Unity\Hub\Editor\{version}\Editor\Data\Managed",
                // macOS Hub
                $"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/Managed",
                // Linux Hub
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             "Unity", "Hub", "Editor", version, "Editor", "Data", "Managed"),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
        }
        catch { }
        return null;
    }

    /// <summary>Format hits as a CODE_ANALYZE-style report. Member zoom (e.g. `Foo.Bar`) filters
    /// each hit's method/field/prop list to the matching member.</summary>
    public string Format(string query, List<TypeInfo> hits, string memberFilter = null)
    {
        var sb = new StringBuilder();
        string header = memberFilter != null
            ? $"=== {query} === (compiled DLL, {hits.Count} type match{(hits.Count == 1 ? "" : "es")})"
            : $"=== {query} === (compiled DLL, {hits.Count} match{(hits.Count == 1 ? "" : "es")})";
        sb.AppendLine(header);
        sb.AppendLine();

        int shown = 0;
        foreach (var h in hits.Take(8))
        {
            shown++;
            string rel = ToRel(h.AssemblyPath);
            string fq = string.IsNullOrEmpty(h.Namespace) ? h.Name : $"{h.Namespace}.{h.Name}";
            sb.AppendLine($"--- {fq} ({h.Kind}) ---");
            sb.AppendLine($"Assembly: {h.AssemblyName} — {rel}");
            if (!string.IsNullOrEmpty(h.BaseType) &&
                h.BaseType != "System.Object" &&
                h.BaseType != "System.ValueType" &&
                h.BaseType != "System.Enum" &&
                h.BaseType != "System.MulticastDelegate")
                sb.AppendLine($"Inherits: {h.BaseType}");
            if (h.Interfaces.Count > 0)
                sb.AppendLine($"Implements: {string.Join(", ", h.Interfaces.Take(5))}{(h.Interfaces.Count > 5 ? $" (+{h.Interfaces.Count - 5})" : "")}");

            if (memberFilter != null)
            {
                bool MemberMatch(string sig) => sig.IndexOf(" " + memberFilter + "(", StringComparison.Ordinal) >= 0
                                              || sig.IndexOf(" " + memberFilter + " ", StringComparison.Ordinal) >= 0
                                              || sig.EndsWith(" " + memberFilter, StringComparison.Ordinal);
                var mm = h.Methods.Where(MemberMatch).ToList();
                var ff = h.Fields.Where(MemberMatch).ToList();
                var pp = h.Properties.Where(MemberMatch).ToList();
                var ee = h.Events.Where(MemberMatch).ToList();
                var ev = h.EnumValues.Where(v => v.Equals(memberFilter, StringComparison.Ordinal)).ToList();
                if (mm.Count > 0) AppendCapped(sb, $"Methods matching '{memberFilter}' ({mm.Count})", mm, 12);
                if (ff.Count > 0) AppendCapped(sb, $"Fields matching '{memberFilter}' ({ff.Count})", ff, 12);
                if (pp.Count > 0) AppendCapped(sb, $"Properties matching '{memberFilter}' ({pp.Count})", pp, 12);
                if (ee.Count > 0) AppendCapped(sb, $"Events matching '{memberFilter}' ({ee.Count})", ee, 6);
                if (ev.Count > 0) sb.AppendLine($"Enum value: {string.Join(", ", ev)}");
                if (mm.Count == 0 && ff.Count == 0 && pp.Count == 0 && ee.Count == 0 && ev.Count == 0)
                    sb.AppendLine($"  (no member '{memberFilter}' found)");
            }
            else
            {
                if (h.EnumValues.Count > 0) sb.AppendLine($"Values: {string.Join(", ", h.EnumValues)}");
                if (h.Methods.Count > 0) AppendCapped(sb, $"Methods ({h.Methods.Count})", h.Methods, 15);
                if (h.Fields.Count > 0) AppendCapped(sb, $"Fields ({h.Fields.Count})", h.Fields, 15);
                if (h.Properties.Count > 0) AppendCapped(sb, $"Properties ({h.Properties.Count})", h.Properties, 12);
                if (h.Events.Count > 0) AppendCapped(sb, $"Events ({h.Events.Count})", h.Events, 8);
                if (h.NestedTypes.Count > 0) sb.AppendLine($"Nested types: {string.Join(", ", h.NestedTypes.Take(8))}");
            }
            sb.AppendLine();
        }
        if (hits.Count > shown) sb.AppendLine($"... +{hits.Count - shown} more matches");
        return sb.ToString().TrimEnd();
    }

    string ToRel(string path)
    {
        if (path.StartsWith(_projectPath, StringComparison.OrdinalIgnoreCase))
            return path.Substring(_projectPath.Length).TrimStart('\\', '/');
        return path;
    }

    static void AppendCapped(StringBuilder sb, string heading, List<string> items, int cap)
    {
        sb.AppendLine($"{heading}:");
        foreach (var i in items.Take(cap)) sb.AppendLine($"  {i}");
        if (items.Count > cap) sb.AppendLine($"  ... +{items.Count - cap} more");
    }

    /// <summary>Strip generic + array suffixes so `List&lt;Foo&gt;` / `Foo[]` queries match the bare type name.</summary>
    public static string SimpleName(string query)
    {
        if (string.IsNullOrEmpty(query)) return query;
        string s = query.Trim();
        int lastDot = s.LastIndexOf('.');
        // For dotted queries we treat last segment as the simple name; caller decides if it's
        // a member zoom or namespaced type. This helper just normalises.
        if (lastDot > 0) s = s.Substring(lastDot + 1);
        int lt = s.IndexOf('<');
        if (lt > 0) s = s.Substring(0, lt);
        while (s.EndsWith("[]")) s = s.Substring(0, s.Length - 2);
        return s;
    }
}
