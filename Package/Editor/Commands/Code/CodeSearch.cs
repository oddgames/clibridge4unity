using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace clibridge4unity
{
    /// <summary>
    /// Cached PDB method information for fast stack trace analysis.
    /// Key: "TypeName.MethodName", Value: (filePath, startLine, endLine)
    /// </summary>
    [InitializeOnLoad]
    public static class PdbCache
    {
        static PdbCache()
        {
            EditorApplication.update += InitOnFirstTick;
        }

        private static void InitOnFirstTick()
        {
            EditorApplication.update -= InitOnFirstTick;
            BridgeDiagnostics.Log("PdbCache", "InitOnFirstTick - scheduling background load");
            InitializeAsync();
        }
        private static Dictionary<string, (string file, int startLine, int endLine)> _methodCache;
        private static bool _isInitialized;
        private static readonly object _lock = new object();

        /// <summary>
        /// Returns true if the cache is initialized and ready for use.
        /// </summary>
        public static bool IsReady => _isInitialized && _methodCache != null;

        /// <summary>
        /// Initializes the PDB cache on a background thread to avoid blocking the editor.
        /// Safe to call multiple times - only runs once.
        /// </summary>
        public static void InitializeAsync()
        {
            if (_isInitialized || _isInitializing) return;
            _isInitializing = true;
            BridgeDiagnostics.Log("PdbCache", "InitializeAsync scheduling background load");
            System.Threading.Tasks.Task.Run(() => Initialize());
        }

        private static volatile bool _isInitializing;

        /// <summary>
        /// Initializes the PDB cache by loading all method debug info from project assemblies.
        /// Runs synchronously - prefer InitializeAsync() to avoid blocking the editor.
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_isInitialized) return;

                BridgeDiagnostics.Log("PdbCache", "Initialize begin");
                _methodCache = new Dictionary<string, (string, int, int)>(StringComparer.Ordinal);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int methodCount = 0;

                foreach (var asm in GetProjectAssemblies())
                {
                    try
                    {
                        var asmPath = asm.Location;
                        if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath)) continue;

                        var pdbPath = Path.ChangeExtension(asmPath, ".pdb");
                        if (!File.Exists(pdbPath)) continue;

                        // Read assembly and PDB into memory to avoid locking
                        byte[] asmBytes = ReadAllBytesShared(asmPath);
                        if (asmBytes == null) continue;

                        byte[] pdbBytes = ReadAllBytesShared(pdbPath);
                        if (pdbBytes == null) continue;

                        var readerParams = new ReaderParameters
                        {
                            ReadSymbols = true,
                            SymbolStream = new MemoryStream(pdbBytes)
                        };

                        using (var asmStream = new MemoryStream(asmBytes))
                        using (var module = ModuleDefinition.ReadModule(asmStream, readerParams))
                        {
                            foreach (var type in module.Types)
                            {
                                CacheTypeMethods(type, ref methodCount);
                            }
                        }
                    }
                    catch { }
                }

                sw.Stop();
                _isInitialized = true;
                UnityEngine.Debug.Log($"[Bridge] PDB cache initialized: {methodCount} methods in {sw.ElapsedMilliseconds}ms");
                BridgeDiagnostics.Log("PdbCache", $"Initialize end, methods={methodCount}, ms={sw.ElapsedMilliseconds}");
            }
        }

        private static void CacheTypeMethods(TypeDefinition type, ref int methodCount)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.DebugInformation == null) continue;

                var seqPoints = method.DebugInformation.SequencePoints;
                if (seqPoints == null || seqPoints.Count == 0) continue;

                var firstPoint = seqPoints.FirstOrDefault(sp => sp.StartLine != 0xFeeFee);
                var lastPoint = seqPoints.LastOrDefault(sp => sp.StartLine != 0xFeeFee);

                if (firstPoint != null && lastPoint != null)
                {
                    var docPath = firstPoint.Document?.Url ?? "";
                    if (string.IsNullOrEmpty(docPath)) continue;

                    // Cache with simple type name for faster lookup
                    var key = $"{type.Name}.{method.Name}";
                    var endLine = lastPoint.EndLine > 0 ? lastPoint.EndLine : lastPoint.StartLine;
                    _methodCache[key] = (docPath, firstPoint.StartLine, endLine);
                    methodCount++;

                    // Also cache with full type name for namespace-qualified lookups
                    if (!string.IsNullOrEmpty(type.Namespace))
                    {
                        var fullKey = $"{type.FullName}.{method.Name}";
                        _methodCache[fullKey] = (docPath, firstPoint.StartLine, endLine);
                    }
                }
            }

            // Process nested types
            foreach (var nested in type.NestedTypes)
            {
                CacheTypeMethods(nested, ref methodCount);
            }
        }

        /// <summary>
        /// Looks up method info from the cache. Returns null if not found.
        /// </summary>
        public static (string file, int startLine, int endLine)? GetMethodInfo(string typeName, string methodName)
        {
            if (!_isInitialized || _methodCache == null) return null;

            // Try simple name first (most common)
            var simpleKey = $"{typeName}.{methodName}";
            if (_methodCache.TryGetValue(simpleKey, out var info))
                return info;

            // Try with full type name if provided with namespace
            if (typeName.Contains('.'))
            {
                if (_methodCache.TryGetValue($"{typeName}.{methodName}", out info))
                    return info;

                // Try just the simple class name
                var simpleTypeName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                simpleKey = $"{simpleTypeName}.{methodName}";
                if (_methodCache.TryGetValue(simpleKey, out info))
                    return info;
            }

            return null;
        }

        /// <summary>
        /// Clears the cache. Call this before domain reload if needed.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                BridgeDiagnostics.Log("PdbCache", "Clear");
                _methodCache?.Clear();
                _methodCache = null;
                _isInitialized = false;
            }
        }

        private static IEnumerable<Assembly> GetProjectAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic &&
                           !string.IsNullOrEmpty(a.Location) &&
                           !a.FullName.StartsWith("UnityEngine") &&
                           !a.FullName.StartsWith("UnityEditor") &&
                           !a.FullName.StartsWith("Unity.") &&
                           !a.FullName.StartsWith("System") &&
                           !a.FullName.StartsWith("mscorlib") &&
                           !a.FullName.StartsWith("Mono") &&
                           !a.FullName.StartsWith("netstandard") &&
                           !a.FullName.StartsWith("Microsoft") &&
                           !a.FullName.StartsWith("Newtonsoft") &&
                           !a.FullName.StartsWith("nunit") &&
                           !a.FullName.StartsWith("Bee.") &&
                           !a.FullName.StartsWith("ExCSS"));
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bytes = new byte[fs.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = fs.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                    return bytes;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Reflection + regex-based code search for Unity projects.
    /// Uses compiled assemblies for accurate type info, regex for source locations.
    /// </summary>
    public static class CodeSearch
    {
        // Cache for source files - cleared when files change
        private static Dictionary<string, string> _fileCache = new Dictionary<string, string>();
        private static List<string> _sourceFilesCache;
        private static DateTime _cacheTime;
        private const int CacheExpirySeconds = 300; // 5 minutes

        private static void RefreshCacheIfNeeded()
        {
            if (_sourceFilesCache == null || (DateTime.Now - _cacheTime).TotalSeconds > CacheExpirySeconds)
            {
                _sourceFilesCache = GetAllSourceFiles().ToList();
                _fileCache.Clear();
                _cacheTime = DateTime.Now;
            }
        }

        private static string GetFileContent(string path)
        {
            if (!_fileCache.TryGetValue(path, out var content))
            {
                try { content = File.ReadAllText(path); }
                catch { content = ""; }
                _fileCache[path] = content;
            }
            return content;
        }

        /// <summary>
        /// Kind-prefixed search helper used internally by CODE_ANALYZE when the query is
        /// `method:Name` / `field:Name` / `inherits:Type` / `attribute:Name` etc. Not exposed
        /// as a bridge command — CODE_ANALYZE is the single entry point.
        /// </summary>
        internal static string Search(string query)
        {
            try
            {
                string searchType = "content";
                string searchTerm = query;

                if (query.Contains(':'))
                {
                    var idx = query.IndexOf(':');
                    searchType = query.Substring(0, idx).ToLower();
                    searchTerm = query.Substring(idx + 1);
                }

                var results = new List<string>();

                switch (searchType)
                {
                    case "class":
                        results = SearchClasses(searchTerm);
                        break;
                    case "method":
                        results = SearchMethods(searchTerm);
                        break;
                    case "field":
                        results = SearchFields(searchTerm);
                        break;
                    case "property":
                        results = SearchProperties(searchTerm);
                        break;
                    case "inherits":
                    case "extends":
                        results = SearchInherits(searchTerm);
                        break;
                    case "attribute":
                        results = SearchAttributes(searchTerm);
                        break;
                    case "refs":
                    case "usages":
                    case "references":
                        results = SearchReferencesAllFolders(searchTerm);
                        break;
                    default:
                        results = SearchContentAllFolders(searchTerm);
                        break;
                }

                if (results.Count == 0)
                    return $"No matches for '{query}'";

                return $"Found {results.Count} matches:\n\n{string.Join("\n", results.Take(50))}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }


        /// <summary>
        /// Analyzes code elements using reflection + source parsing.
        /// Supports: stack traces, "ClassName", "ClassName.MethodName", "ClassName.fieldName"
        /// Searches both Assets and Packages folders automatically.
        /// </summary>
        [BridgeCommand("CODE_ANALYZE", "Analyze a class, member, or stack trace (also handles method:/field:/inherits:/attribute: prefixes)",
            Category = "Code",
            Usage = "CODE_ANALYZE ClassName | ClassName.Member | method:Name | field:Name | inherits:Type | attribute:Name | <stack trace>",
            RelatedCommands = new[] { "CODE_EXEC_RETURN", "CODE_EXEC", "LOG" })]
        public static string Analyze(string query)
        {
            try
            {
                query = query.Trim();

                // Prefix queries → dispatch to Search (unified CODE_ANALYZE entry point)
                int colonIdx = query.IndexOf(':');
                if (colonIdx > 0 && query.IndexOf(' ') < 0)
                {
                    string prefix = query.Substring(0, colonIdx).ToLowerInvariant();
                    if (prefix == "method" || prefix == "field" || prefix == "property" ||
                        prefix == "inherits" || prefix == "extends" || prefix == "attribute" ||
                        prefix == "class" || prefix == "type")
                        return Search(query);
                }

                // Check if it's a stack trace (contains " at " or line numbers like ":line 42")
                if (query.Contains(" at ") || Regex.IsMatch(query, @":\d+\)") || query.Contains(".cs:"))
                    return AnalyzeStackTrace(query);

                // Parse the query: ClassName, ClassName.MemberName, or Namespace.ClassName.MemberName
                var result = ParseMemberQuery(query);

                string analysisResult = null;
                if (result.Type != null)
                {
                    if (!string.IsNullOrEmpty(result.MemberName))
                        analysisResult = AnalyzeMember(result.Type, result.MemberName);
                    else
                        analysisResult = AnalyzeType(result.Type);
                }

                // Append raw source grep — always useful as a supplement
                // Use the last segment as the search term (member name or type name)
                var searchTerm = query.Contains('.') ? query.Split('.').Last() : query;
                var grepResults = SearchContentAllFolders(searchTerm);

                var sb = new StringBuilder();
                if (analysisResult != null)
                {
                    sb.Append(analysisResult);
                    if (grepResults.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine();
                        sb.AppendLine($"--- Source references for '{searchTerm}' ({grepResults.Count} matches) ---");
                        foreach (var line in grepResults.Take(20))
                            sb.AppendLine(line);
                    }
                }
                else if (grepResults.Count > 0)
                {
                    // No reflection match, but found in source
                    sb.AppendLine($"Type '{result.TypeName}' not found via reflection, but found in source:");
                    sb.AppendLine();
                    foreach (var line in grepResults.Take(30))
                        sb.AppendLine(line);
                }
                else
                {
                    return $"Error: '{query}' not found via reflection or source search";
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static (Type Type, string TypeName, string MemberName) ParseMemberQuery(string query)
        {
            // Try full name first (Namespace.Class.Member or Namespace.Class)
            Type targetType = null;
            string memberName = null;

            // Try to find type by progressively removing the last segment
            var segments = query.Split('.');
            for (int i = segments.Length; i >= 1; i--)
            {
                var typeName = string.Join(".", segments.Take(i));
                targetType = FindType(typeName);
                if (targetType != null)
                {
                    memberName = i < segments.Length ? segments[i] : null;
                    return (targetType, typeName, memberName);
                }
            }

            // Fallback: try just the first segment as class name
            targetType = FindType(segments[0]);
            memberName = segments.Length > 1 ? segments[1] : null;
            return (targetType, segments[0], memberName);
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try exact full name match first
                    var type = asm.GetType(name);
                    if (type != null) return type;

                    // Try simple name match
                    type = asm.GetTypes().FirstOrDefault(t => t.Name == name || t.FullName == name);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static string AnalyzeStackTrace(string stackTrace)
        {
            // Parse stack trace lines - standard .NET format
            // Format: at Namespace.Type.Method (params) [IL offset] in Path:Line
            // Note: Windows paths contain colons (C:\), so we match file path as everything up to the last :digits
            var framePattern = @"at\s+(?<fullMethod>(?<type>[\w\.`<>]+)\.(?<method>\w+))\s*\((?<params>[^)]*)\)\s*(?:\[.*?\])?(?:\s+in\s+(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>\d+))?";
            var matches = Regex.Matches(stackTrace, framePattern);

            if (matches.Count == 0)
            {
                // Try Unity-style traces: ClassName:MethodName() (at path:line)
                framePattern = @"(?<type>[\w\.]+):(?<method>\w+)\s*\((?<params>[^)]*)\)\s*(?:\(at\s+(?<file>[^:]+):(?<line>\d+)\))?";
                matches = Regex.Matches(stackTrace, framePattern);
            }

            if (matches.Count == 0)
                return "Error: Could not parse stack trace. Expected .NET or Unity format.";

            // Process frames in parallel
            var frameData = new List<(string frameLine, string typeName, string methodName, string file, int errorLine)>();
            foreach (Match match in matches)
            {
                var frameLine = match.Value.Trim();
                var typeName = match.Groups["type"].Value;
                var methodName = match.Groups["method"].Value;
                var fileGroup = match.Groups["file"];
                var lineGroup = match.Groups["line"];
                var file = fileGroup.Success ? fileGroup.Value : null;
                var lineStr = lineGroup.Success ? lineGroup.Value : null;
                int.TryParse(lineStr, out int errorLine);
                frameData.Add((frameLine, typeName, methodName, file, errorLine));
            }

            // Process all frames in parallel
            var results = new string[frameData.Count];
            System.Threading.Tasks.Parallel.For(0, frameData.Count, i =>
            {
                var (frameLine, typeName, methodName, file, errorLine) = frameData[i];
                var frameSb = new StringBuilder();
                frameSb.AppendLine(frameLine);

                // Extract simple class name for cache lookup
                var simpleClassName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;

                // Fast path: Use PDB cache (loaded at startup)
                var cachedInfo = PdbCache.GetMethodInfo(simpleClassName, methodName);
                if (cachedInfo.HasValue)
                {
                    var (pdbFile, pdbStart, pdbEnd) = cachedInfo.Value;
                    var sourceLines = TryReadSourceLines(pdbFile);
                    if (sourceLines != null)
                    {
                        var relativePath = ToRelativePath(pdbFile);
                        frameSb.AppendLine($"  {relativePath}:{pdbStart}-{pdbEnd} (error at {errorLine})");
                        frameSb.AppendLine();
                        for (int j = pdbStart - 1; j < pdbEnd && j < sourceLines.Length; j++)
                        {
                            var lineNum = j + 1;
                            var marker = (errorLine > 0 && lineNum == errorLine) ? ">" : " ";
                            frameSb.AppendLine($"{marker}{lineNum,4} | {sourceLines[j].TrimEnd()}");
                        }
                        frameSb.AppendLine();
                        results[i] = frameSb.ToString();
                        return;
                    }
                }

                // Fallback: slow path for methods not in cache (triggers on-demand PDB loading)
                var (fallbackFile, fallbackStart, fallbackEnd) = GetMethodLinesFromPdb(simpleClassName, methodName);
                if (!string.IsNullOrEmpty(fallbackFile) && fallbackStart > 0)
                {
                    var sourceLines = TryReadSourceLines(fallbackFile);
                    if (sourceLines != null)
                    {
                        var relativePath = ToRelativePath(fallbackFile);
                        frameSb.AppendLine($"  {relativePath}:{fallbackStart}-{fallbackEnd} (error at {errorLine})");
                        frameSb.AppendLine();
                        for (int j = fallbackStart - 1; j < fallbackEnd && j < sourceLines.Length; j++)
                        {
                            var lineNum = j + 1;
                            var marker = (errorLine > 0 && lineNum == errorLine) ? ">" : " ";
                            frameSb.AppendLine($"{marker}{lineNum,4} | {sourceLines[j].TrimEnd()}");
                        }
                        frameSb.AppendLine();
                        results[i] = frameSb.ToString();
                        return;
                    }
                }

                frameSb.AppendLine($"  (source not found)");
                frameSb.AppendLine();
                results[i] = frameSb.ToString();
            });

            return string.Concat(results).TrimEnd();
        }

        private static (string file, int startLine, int endLine) GetMethodLinesFromPdb(string typeName, string methodName)
        {
            // Search all project assemblies directly with Cecil for PDB symbols
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    var asmPath = asm.Location;
                    if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath)) continue;

                    var pdbPath = Path.ChangeExtension(asmPath, ".pdb");
                    if (!File.Exists(pdbPath)) continue; // Skip assemblies without PDB

                    // Read assembly and PDB bytes into memory with sharing to avoid locking
                    byte[] asmBytes = ReadAllBytesShared(asmPath);
                    if (asmBytes == null) continue;

                    byte[] pdbBytes = ReadAllBytesShared(pdbPath);
                    if (pdbBytes == null) continue;

                    var readerParams = new ReaderParameters
                    {
                        ReadSymbols = true,
                        SymbolStream = new MemoryStream(pdbBytes)
                    };

                    using (var asmStream = new MemoryStream(asmBytes))
                    using (var module = ModuleDefinition.ReadModule(asmStream, readerParams))
                    {
                        // Find the type in Cecil by simple name
                        TypeDefinition cecilType = null;
                        foreach (var t in module.Types)
                        {
                            if (t.Name == typeName)
                            {
                                cecilType = t;
                                break;
                            }
                            // Check nested types (for inner classes)
                            foreach (var nested in t.NestedTypes)
                            {
                                if (nested.Name == typeName)
                                {
                                    cecilType = nested;
                                    break;
                                }
                            }
                            if (cecilType != null) break;
                        }
                        if (cecilType == null) continue;

                        var cecilMethod = cecilType.Methods.FirstOrDefault(m => m.Name == methodName);
                        if (cecilMethod == null) continue;

                        // For async methods, the actual body is in a compiler-generated state machine
                        // Check if this method has debug info, otherwise it might be the "stub"
                        if (!cecilMethod.HasBody || cecilMethod.DebugInformation == null ||
                            cecilMethod.DebugInformation.SequencePoints == null ||
                            cecilMethod.DebugInformation.SequencePoints.Count == 0)
                        {
                            // Try to find the MoveNext method in the async state machine
                            var stateMachineType = cecilType.NestedTypes
                                .FirstOrDefault(n => n.Name.StartsWith("<" + methodName + ">"));
                            if (stateMachineType != null)
                            {
                                var moveNext = stateMachineType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
                                if (moveNext != null && moveNext.HasBody && moveNext.DebugInformation != null)
                                    cecilMethod = moveNext;
                            }
                        }

                        if (!cecilMethod.HasBody || cecilMethod.DebugInformation == null) continue;

                        // Get sequence points from debug info
                        var seqPoints = cecilMethod.DebugInformation.SequencePoints;
                        if (seqPoints == null || seqPoints.Count == 0) continue;

                        var firstPoint = seqPoints.FirstOrDefault(sp => sp.StartLine != 0xFeeFee); // Skip hidden points
                        var lastPoint = seqPoints.LastOrDefault(sp => sp.StartLine != 0xFeeFee);

                        if (firstPoint != null && lastPoint != null)
                        {
                            var docPath = firstPoint.Document?.Url ?? "";
                            return (docPath, firstPoint.StartLine, lastPoint.EndLine > 0 ? lastPoint.EndLine : lastPoint.StartLine);
                        }
                    }
                }
                catch { }
            }
            return (null, 0, 0);
        }


        private static string[] TryReadSourceLines(string path)
        {
            // Try absolute path first
            if (File.Exists(path))
                return File.ReadAllLines(path);

            // Try relative to project
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var fullPath = Path.Combine(projectRoot, path);
            if (File.Exists(fullPath))
                return File.ReadAllLines(fullPath);

            // Search in source folders
            foreach (var folder in GetSourceFolders())
            {
                var searchPath = Path.Combine(folder, Path.GetFileName(path));
                if (File.Exists(searchPath))
                    return File.ReadAllLines(searchPath);
            }

            return null;
        }

        private static Tuple<string, int> FindMethodSourceFile(string className, string methodName)
        {
            // Extract simple class name from full type name
            var simpleClassName = className.Contains(".") ? className.Substring(className.LastIndexOf('.') + 1) : className;
            var classPattern = $@"class\s+{Regex.Escape(simpleClassName)}\b";
            var methodPattern = $@"(?:public|private|protected|internal|static|async|\s)+\s+[\w<>,\[\]\s]+\s+{Regex.Escape(methodName)}\s*\(";

            foreach (var file in GetAllSourceFiles())
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (!Regex.IsMatch(content, classPattern)) continue;

                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (Regex.IsMatch(lines[i], methodPattern))
                            return Tuple.Create(file, i + 1);
                    }
                }
                catch { }
            }
            return null;
        }

        private static string AnalyzeMember(Type type, string memberName)
        {
            var sb = new StringBuilder();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            // Check methods
            var methods = type.GetMethods(flags).Where(m => m.Name == memberName && !m.IsSpecialName).ToArray();
            if (methods.Length > 0)
            {
                // Find source location first to get docs
                var methodInfo = FindMethodInSource(type.Name, memberName);

                // Show XML doc comments if available
                if (methodInfo?.XmlDoc != null)
                {
                    sb.AppendLine(methodInfo.XmlDoc);
                    sb.AppendLine();
                }

                foreach (var m in methods)
                {
                    var mod = m.IsPublic ? "public" : m.IsPrivate ? "private" : "protected";
                    if (m.IsStatic) mod += " static";
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"{mod} {m.ReturnType.Name} {memberName}({parms})");
                }

                if (methodInfo != null)
                    sb.AppendLine($"{methodInfo.FilePath}:{methodInfo.StartLine}-{methodInfo.EndLine}");

                // Find what calls this method
                var callers = FindMethodCallers(type.Name, memberName);
                if (callers.Count > 0)
                {
                    sb.AppendLine($"\nCalled by ({callers.Count}):");
                    foreach (var caller in callers.Take(10))
                        sb.AppendLine($"  {caller}");
                }

                return sb.ToString().TrimEnd();
            }

            // Check fields
            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                sb.AppendLine($"=== Field: {type.Name}.{memberName} ===");

                var fieldDoc = ExtractMemberDocComment(type.Name, memberName);
                if (fieldDoc != null)
                {
                    sb.AppendLine(fieldDoc);
                    sb.AppendLine();
                }

                var mod = field.IsPublic ? "public" : field.IsPrivate ? "private" : "protected";
                if (field.IsStatic) mod += " static";
                if (field.IsInitOnly) mod += " readonly";

                sb.AppendLine($"Signature: {mod} {field.FieldType.Name} {memberName}");

                // Find source location
                var sourceInfo = FindMemberInSource(type.Name, memberName);
                if (sourceInfo != null)
                    sb.AppendLine($"Source: {sourceInfo}");

                return sb.ToString().TrimEnd();
            }

            // Check properties
            var prop = type.GetProperty(memberName, flags);
            if (prop != null)
            {
                sb.AppendLine($"=== Property: {type.Name}.{memberName} ===");

                var propDoc = ExtractMemberDocComment(type.Name, memberName);
                if (propDoc != null)
                {
                    sb.AppendLine(propDoc);
                    sb.AppendLine();
                }

                var getter = prop.GetMethod;
                var setter = prop.SetMethod;
                var mod = (getter?.IsPublic ?? setter?.IsPublic ?? false) ? "public" : "private";
                if (getter?.IsStatic ?? setter?.IsStatic ?? false) mod += " static";

                var accessors = "";
                if (getter != null) accessors += "get; ";
                if (setter != null) accessors += "set; ";

                sb.AppendLine($"Signature: {mod} {prop.PropertyType.Name} {memberName} {{ {accessors}}}");

                return sb.ToString().TrimEnd();
            }

            return $"Error: Member '{memberName}' not found in type '{type.Name}'";
        }

        private class MethodSourceInfo
        {
            public string FilePath;      // Relative path for display
            public string AbsolutePath;  // Absolute path for reading
            public int StartLine;
            public int EndLine;
            public string XmlDoc;        // Extracted /// comments
        }

        /// <summary>
        /// Extracts XML doc comments (/// lines) above a given line number.
        /// </summary>
        private static string ExtractXmlDocComments(string[] lines, int declarationLine)
        {
            if (lines == null || declarationLine <= 1 || declarationLine > lines.Length)
                return null;

            var docLines = new List<string>();
            int idx = declarationLine - 2; // 0-indexed, line before declaration

            // Walk backwards collecting /// comments and [Attributes]
            while (idx >= 0)
            {
                var line = lines[idx].Trim();
                if (line.StartsWith("///"))
                {
                    // Extract content after ///
                    var content = line.Substring(3).TrimStart();
                    docLines.Insert(0, content);
                    idx--;
                }
                else if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    // Skip attributes, keep looking for docs
                    idx--;
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    // Empty line - stop
                    break;
                }
                else
                {
                    // Non-doc content - stop
                    break;
                }
            }

            if (docLines.Count == 0)
                return null;

            return string.Join("\n", docLines);
        }

        /// <summary>
        /// Extracts doc comments above a class declaration.
        /// </summary>
        private static string ExtractClassDocComment(string className)
        {
            RefreshCacheIfNeeded();
            var pattern = $@"class\s+{Regex.Escape(className)}\b";

            foreach (var filePath in _sourceFilesCache)
            {
                var content = GetFileContent(filePath);
                if (!content.Contains(className)) continue;

                var lines = content.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (Regex.IsMatch(lines[i], pattern))
                        return ExtractXmlDocComments(lines, i + 1); // 1-indexed
                }
            }
            return null;
        }

        /// <summary>
        /// Extracts doc comments above a field or property declaration within a class.
        /// </summary>
        private static string ExtractMemberDocComment(string className, string memberName)
        {
            RefreshCacheIfNeeded();
            var classPattern = $@"class\s+{Regex.Escape(className)}\b";
            var memberPattern = $@"\b{Regex.Escape(memberName)}\b";

            foreach (var filePath in _sourceFilesCache)
            {
                var content = GetFileContent(filePath);
                if (!content.Contains(className)) continue;

                var lines = content.Split('\n');
                bool inClass = false;
                int braceDepth = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!inClass && Regex.IsMatch(lines[i], classPattern))
                        inClass = true;

                    if (inClass)
                    {
                        foreach (char c in lines[i])
                        {
                            if (c == '{') braceDepth++;
                            else if (c == '}') braceDepth--;
                        }

                        if (braceDepth <= 0 && i > 0) break;

                        if (Regex.IsMatch(lines[i], memberPattern) && !lines[i].TrimStart().StartsWith("//"))
                            return ExtractXmlDocComments(lines, i + 1);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Finds a method in source with start/end line numbers.
        /// </summary>
        private static MethodSourceInfo FindMethodInSource(string className, string methodName)
        {
            RefreshCacheIfNeeded();
            var classPattern = $@"class\s+{Regex.Escape(className)}\b";
            var methodPattern = $@"(?:public|private|protected|internal|static|async|\s)+\s+[\w<>,\[\]\s]+\s+{Regex.Escape(methodName)}\s*\(";

            foreach (var file in _sourceFilesCache)
            {
                try
                {
                    var content = GetFileContent(file);
                    if (string.IsNullOrEmpty(content) || !content.Contains(className)) continue;
                    if (!Regex.IsMatch(content, classPattern)) continue;

                    var lines = content.Split('\n');
                    int startLine = -1;
                    int braceCount = 0;
                    bool inMethod = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];

                        if (!inMethod && Regex.IsMatch(line, methodPattern))
                        {
                            startLine = i + 1;
                            braceCount = 0;
                            inMethod = true;
                        }

                        if (inMethod)
                        {
                            foreach (char c in line)
                            {
                                if (c == '{') braceCount++;
                                else if (c == '}') braceCount--;
                            }

                            if (braceCount == 0 && line.Contains("}"))
                            {
                                return new MethodSourceInfo
                                {
                                    FilePath = ToRelativePath(file),
                                    AbsolutePath = file,
                                    StartLine = startLine,
                                    EndLine = i + 1,
                                    XmlDoc = ExtractXmlDocComments(lines, startLine)
                                };
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Finds methods that call the specified method. Uses reflection to find candidate types,
        /// then searches only those source files. Limited to 5 callers for performance.
        /// </summary>
        private static List<string> FindMethodCallers(string className, string methodName)
        {
            var callers = new List<string>();

            // Use reflection to find types that might reference this class
            var candidateTypes = new HashSet<string>();
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        // Check if any method body might reference our target
                        // We can't inspect IL easily, so check if the type's source file contains the method name
                        candidateTypes.Add(type.Name);
                    }
                }
                catch { }
            }

            // Now search only source files for types we found
            RefreshCacheIfNeeded();
            var callPattern = $@"\b{Regex.Escape(methodName)}\s*\(";

            foreach (var file in _sourceFilesCache)
            {
                if (callers.Count >= 5) break;

                try
                {
                    var content = GetFileContent(file);
                    if (string.IsNullOrEmpty(content) || !content.Contains(methodName)) continue;

                    // Quick check: does this file contain a call to our method?
                    if (!Regex.IsMatch(content, callPattern)) continue;

                    // Find the class name in this file
                    var classMatch = Regex.Match(content, @"class\s+(\w+)");
                    if (!classMatch.Success) continue;
                    var fileClassName = classMatch.Groups[1].Value;

                    // Find line numbers where the call occurs
                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (Regex.IsMatch(lines[i], callPattern))
                        {
                            var relativePath = ToRelativePath(file);
                            callers.Add($"{fileClassName} at {relativePath}:{i + 1}");
                            if (callers.Count >= 5) break;
                        }
                    }
                }
                catch { }
            }
            return callers;
        }

        private static string AnalyzeType(Type targetType)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {targetType.Name} ===");

            // Extract class-level doc comments from source
            var classDoc = ExtractClassDocComment(targetType.Name);
            if (classDoc != null)
            {
                sb.AppendLine(classDoc);
                sb.AppendLine();
            }

            sb.AppendLine($"Namespace: {targetType.Namespace}");
            sb.AppendLine($"Assembly: {targetType.Assembly.GetName().Name}");

            // Base type
            if (targetType.BaseType != null && targetType.BaseType != typeof(object))
                sb.AppendLine($"Inherits: {targetType.BaseType.Name}");

            // Interfaces
            var interfaces = targetType.GetInterfaces();
            if (interfaces.Length > 0)
                sb.AppendLine($"Implements: {string.Join(", ", interfaces.Select(i => i.Name))}");

            // Attributes
            var attrs = targetType.GetCustomAttributes(false);
            if (attrs.Length > 0)
                sb.AppendLine($"Attributes: {string.Join(", ", attrs.Select(a => $"[{a.GetType().Name}]"))}");

            // Fields
            var fields = targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (fields.Length > 0)
            {
                sb.AppendLine("\nFields:");
                foreach (var f in fields.Take(20))
                {
                    var mod = f.IsPublic ? "public" : f.IsPrivate ? "private" : "protected";
                    if (f.IsStatic) mod += " static";
                    sb.AppendLine($"  {mod} {f.FieldType.Name} {f.Name}");
                }
            }

            // Properties
            var props = targetType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (props.Length > 0)
            {
                sb.AppendLine("\nProperties:");
                foreach (var p in props.Take(20))
                {
                    var getter = p.GetMethod != null ? "get;" : "";
                    var setter = p.SetMethod != null ? "set;" : "";
                    sb.AppendLine($"  {p.PropertyType.Name} {p.Name} {{ {getter} {setter} }}");
                }
            }

            // Methods
            var methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).ToArray();

            if (methods.Length > 0)
            {
                sb.AppendLine("\nMethods:");
                foreach (var m in methods)
                {
                    var mod = m.IsPublic ? "public" : m.IsPrivate ? "private" : "protected";
                    if (m.IsStatic) mod += " static";
                    if (m.IsVirtual) mod += " virtual";
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"  {mod} {m.ReturnType.Name} {m.Name}({parms})");
                }
            }

            // Find source file if available
            var sourceFile = FindSourceFile(targetType.Name);
            if (sourceFile != null)
            {
                sb.AppendLine($"\nSource: {sourceFile}");

                // Find usages in other files
                var usages = FindUsagesInSource(targetType.Name, sourceFile);
                if (usages.Count > 0)
                {
                    sb.AppendLine("\nUsed by:");
                    foreach (var usage in usages.Take(15))
                        sb.AppendLine($"  {usage}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string FindMemberInSource(string className, string memberName)
        {
            var classPattern = $@"class\s+{Regex.Escape(className)}\b";
            var memberPattern = $@"\b{Regex.Escape(memberName)}\b";

            // Search all source folders (Assets and Packages)
            foreach (var file in GetAllSourceFiles())
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (!Regex.IsMatch(content, classPattern)) continue;

                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (Regex.IsMatch(lines[i], memberPattern) &&
                            (lines[i].Contains("void ") || lines[i].Contains("int ") || lines[i].Contains("string ") ||
                             lines[i].Contains("bool ") || lines[i].Contains("float ") || lines[i].Contains("public ") ||
                             lines[i].Contains("private ") || lines[i].Contains("protected ") || lines[i].Contains("static ")))
                        {
                            return $"{ToRelativePath(file)}:{i + 1}";
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Gets code structure using reflection.
        /// </summary>
        public static string GetStructure(string typeName)
        {
            try
            {
                Type targetType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var type = asm.GetTypes().FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
                        if (type != null)
                        {
                            targetType = type;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetType == null)
                    return $"Error: Type not found: {typeName}";

                var sb = new StringBuilder();
                var baseList = targetType.BaseType != null && targetType.BaseType != typeof(object)
                    ? $" : {targetType.BaseType.Name}" : "";
                sb.AppendLine($"class {targetType.Name}{baseList}");

                foreach (var f in targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    sb.AppendLine($"  {f.FieldType.Name} {f.Name}");

                foreach (var m in targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName))
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                    sb.AppendLine($"  {m.ReturnType.Name} {m.Name}({parms})");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        #region Search Helpers

        /// <summary>
        /// Gets all source folders to search: Assets, Packages directory, and local file-linked packages.
        /// </summary>
        private static List<string> GetSourceFolders()
        {
            var folders = new List<string>();
            var projectRoot = Path.GetDirectoryName(Application.dataPath);

            // Add Assets folder
            var assetsFolder = Application.dataPath;
            if (Directory.Exists(assetsFolder))
                folders.Add(assetsFolder);

            // Add Packages folder (sibling to Assets)
            var packagesFolder = Path.Combine(projectRoot, "Packages");
            if (Directory.Exists(packagesFolder))
                folders.Add(packagesFolder);

            // Parse manifest.json to find file-linked local packages
            var manifestPath = Path.Combine(packagesFolder, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifestContent = File.ReadAllText(manifestPath);
                    // Look for "file:" references in dependencies
                    var fileRefs = Regex.Matches(manifestContent, @"""file:([^""]+)""");
                    foreach (Match match in fileRefs)
                    {
                        var relativePath = match.Groups[1].Value;
                        var absolutePath = Path.GetFullPath(Path.Combine(packagesFolder, relativePath));
                        if (Directory.Exists(absolutePath) && !folders.Contains(absolutePath))
                            folders.Add(absolutePath);
                    }
                }
                catch { }
            }

            return folders;
        }

        /// <summary>
        /// Gets all C# files from all source folders.
        /// </summary>
        private static IEnumerable<string> GetAllSourceFiles()
        {
            foreach (var folder in GetSourceFolders())
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                    yield return file;
            }
        }

        /// <summary>
        /// Converts absolute path to Unity-relative path (Assets/... or Packages/...).
        /// For external file-linked packages, returns the package-relative path.
        /// </summary>
        private static string ToRelativePath(string absolutePath)
        {
            absolutePath = absolutePath.Replace("\\", "/");
            var projectRoot = Path.GetDirectoryName(Application.dataPath).Replace("\\", "/");

            // Check if path is within project
            if (absolutePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = absolutePath.Substring(projectRoot.Length).TrimStart('/');
                return relative;
            }

            // For external packages, try to find a recognizable package path
            // Look for common package folder patterns
            var match = Regex.Match(absolutePath, @"[/\\](Package|Packages)[/\\]", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var idx = match.Index;
                return absolutePath.Substring(idx + 1);
            }

            // Fallback: just return filename with parent folder for context
            var dir = Path.GetDirectoryName(absolutePath);
            var parentDir = Path.GetFileName(dir);
            return $"{parentDir}/{Path.GetFileName(absolutePath)}";
        }

        private static List<string> SearchClasses(string term)
        {
            var results = new List<string>();
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes().Where(t => t.IsClass && t.Name.Contains(term)))
                    {
                        var baseType = type.BaseType != null && type.BaseType != typeof(object)
                            ? $" : {type.BaseType.Name}" : "";
                        results.Add($"{type.FullName}{baseType}");
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchMethods(string term)
        {
            var results = new List<string>();
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Where(m => m.Name.Contains(term) && !m.IsSpecialName))
                        {
                            var parms = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
                            results.Add($"{type.Name}.{method.Name}({parms})");
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchFields(string term)
        {
            var results = new List<string>();
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Where(f => f.Name.Contains(term)))
                        {
                            results.Add($"{type.Name}.{field.Name} : {field.FieldType.Name}");
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchProperties(string term)
        {
            var results = new List<string>();
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Where(p => p.Name.Contains(term)))
                        {
                            results.Add($"{type.Name}.{prop.Name} : {prop.PropertyType.Name}");
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchInherits(string term)
        {
            var results = new List<string>();
            // Search project assemblies for types that inherit from ANY type (including Unity types)
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes().Where(t => t.IsClass))
                    {
                        // Check full inheritance chain for base type match
                        bool inheritsFromTerm = false;
                        var baseType = type.BaseType;
                        while (baseType != null && baseType != typeof(object))
                        {
                            if (baseType.Name.Contains(term) || baseType.FullName?.Contains(term) == true)
                            {
                                inheritsFromTerm = true;
                                break;
                            }
                            baseType = baseType.BaseType;
                        }

                        // Also check interfaces
                        if (!inheritsFromTerm)
                            inheritsFromTerm = type.GetInterfaces().Any(i => i.Name.Contains(term) || i.FullName?.Contains(term) == true);

                        if (inheritsFromTerm)
                        {
                            var bases = new List<string>();
                            if (type.BaseType != null && type.BaseType != typeof(object))
                                bases.Add(type.BaseType.Name);
                            bases.AddRange(type.GetInterfaces().Select(i => i.Name));
                            results.Add($"{type.FullName} : {string.Join(", ", bases)}");
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchAttributes(string term)
        {
            var results = new List<string>();
            foreach (var asm in GetProjectAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        // Class attributes
                        foreach (var attr in type.GetCustomAttributes(false).Where(a => a.GetType().Name.Contains(term)))
                            results.Add($"[{attr.GetType().Name}] on class {type.Name}");

                        // Method attributes
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        {
                            foreach (var attr in method.GetCustomAttributes(false).Where(a => a.GetType().Name.Contains(term)))
                                results.Add($"[{attr.GetType().Name}] on {type.Name}.{method.Name}()");
                        }

                        // Field attributes
                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        {
                            foreach (var attr in field.GetCustomAttributes(false).Where(a => a.GetType().Name.Contains(term)))
                                results.Add($"[{attr.GetType().Name}] on {type.Name}.{field.Name}");
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchReferences(string term, string folder)
        {
            var results = new List<string>();
            var pattern = $@"\b{Regex.Escape(term)}\b";

            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var matches = Regex.Matches(content, pattern);
                    if (matches.Count > 0)
                    {
                        var relativePath = file.Replace(Application.dataPath.Replace("/", "\\"), "Assets");
                        var lines = content.Split('\n');
                        var lineNums = new HashSet<int>();

                        int pos = 0;
                        int lineNum = 1;
                        foreach (Match match in matches)
                        {
                            while (pos < match.Index)
                            {
                                if (content[pos] == '\n') lineNum++;
                                pos++;
                            }
                            lineNums.Add(lineNum);
                        }

                        results.Add($"{relativePath}: L{string.Join(",", lineNums.Take(5))} ({matches.Count} refs)");
                    }
                }
                catch { }
            }
            return results;
        }

        /// <summary>
        /// Searches for references across all source folders (Assets and Packages).
        /// </summary>
        private static List<string> SearchReferencesAllFolders(string term)
        {
            var results = new List<string>();
            var pattern = $@"\b{Regex.Escape(term)}\b";

            foreach (var file in GetAllSourceFiles())
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var matches = Regex.Matches(content, pattern);
                    if (matches.Count > 0)
                    {
                        var relativePath = ToRelativePath(file);
                        var lines = content.Split('\n');
                        var lineNums = new HashSet<int>();

                        int pos = 0;
                        int lineNum = 1;
                        foreach (Match match in matches)
                        {
                            while (pos < match.Index)
                            {
                                if (content[pos] == '\n') lineNum++;
                                pos++;
                            }
                            lineNums.Add(lineNum);
                        }

                        results.Add($"{relativePath}: L{string.Join(",", lineNums.Take(5))} ({matches.Count} refs)");
                    }
                }
                catch { }
            }
            return results;
        }

        private static List<string> SearchContent(string term, string folder)
        {
            var results = new List<string>();

            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains(term))
                    {
                        var relativePath = file.Replace(Application.dataPath.Replace("/", "\\"), "Assets");
                        var lines = content.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Contains(term))
                            {
                                results.Add($"{relativePath}:L{i + 1}: {lines[i].Trim().Substring(0, Math.Min(80, lines[i].Trim().Length))}");
                                if (results.Count >= 50) return results;
                            }
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        /// <summary>
        /// Searches content across all source folders (Assets and Packages).
        /// </summary>
        private static List<string> SearchContentAllFolders(string term)
        {
            var results = new List<string>();
            var classPattern = new Regex(@"^\s*(?:public|private|internal|static|abstract|sealed|partial)\s+.*?\b(?:class|struct|interface|enum)\s+(\w+)", RegexOptions.Compiled);

            foreach (var file in GetAllSourceFiles())
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains(term))
                    {
                        var relativePath = ToRelativePath(file);
                        var lines = content.Split('\n');
                        string currentClass = null;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            // Track enclosing class/struct/interface
                            var classMatch = classPattern.Match(lines[i]);
                            if (classMatch.Success)
                                currentClass = classMatch.Groups[1].Value;

                            if (lines[i].Contains(term))
                            {
                                var lineContent = lines[i].Trim();
                                var prefix = currentClass != null ? $"{relativePath}({currentClass}):L{i + 1}" : $"{relativePath}:L{i + 1}";
                                results.Add($"{prefix}: {lineContent.Substring(0, Math.Min(80, lineContent.Length))}");
                                if (results.Count >= 50) return results;
                            }
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        /// <summary>
        /// Gets project assemblies (excluding Unity/System/etc core assemblies).
        /// This returns user project assemblies where we want to search for types.
        /// Note: These types may still inherit from Unity types - we just filter the assembly list,
        /// not the inheritance chain.
        /// </summary>
        private static IEnumerable<Assembly> GetProjectAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic &&
                           !string.IsNullOrEmpty(a.Location) &&
                           !a.FullName.StartsWith("UnityEngine") &&
                           !a.FullName.StartsWith("UnityEditor") &&
                           !a.FullName.StartsWith("Unity.") &&
                           !a.FullName.StartsWith("System") &&
                           !a.FullName.StartsWith("mscorlib") &&
                           !a.FullName.StartsWith("Mono") &&
                           !a.FullName.StartsWith("netstandard") &&
                           !a.FullName.StartsWith("Microsoft") &&
                           !a.FullName.StartsWith("Newtonsoft") &&
                           !a.FullName.StartsWith("nunit") &&
                           !a.FullName.StartsWith("Bee.") &&
                           !a.FullName.StartsWith("ExCSS"));
        }

        private static string FindSourceFile(string className)
        {
            RefreshCacheIfNeeded();
            var pattern = $@"class\s+{Regex.Escape(className)}\b";

            foreach (var file in _sourceFilesCache)
            {
                try
                {
                    var content = GetFileContent(file);
                    if (string.IsNullOrEmpty(content) || !content.Contains(className)) continue;
                    if (Regex.IsMatch(content, pattern))
                    {
                        // Find line number
                        var lines = content.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (Regex.IsMatch(lines[i], pattern))
                            {
                                return $"{ToRelativePath(file)}:{i + 1}";
                            }
                        }
                        return ToRelativePath(file);
                    }
                }
                catch { }
            }
            return null;
        }

        private static List<string> FindUsagesInSource(string className, string excludeFile)
        {
            RefreshCacheIfNeeded();
            var results = new List<string>();

            foreach (var file in _sourceFilesCache)
            {
                if (results.Count >= 10) break; // Limit for performance

                try
                {
                    var relativePath = ToRelativePath(file);
                    if (relativePath == excludeFile) continue;

                    var content = GetFileContent(file);
                    if (string.IsNullOrEmpty(content) || !content.Contains(className)) continue;

                    // Find containing class and line numbers
                    var classMatch = Regex.Match(content, @"class\s+(\w+)");
                    var containingClass = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(file);

                    // Find line numbers where usage occurs
                    var lines = content.Split('\n');
                    var lineNums = new List<int>();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(className))
                            lineNums.Add(i + 1);
                    }

                    var lineInfo = lineNums.Count > 0 ? $" (L{string.Join(",", lineNums.Take(3))})" : "";
                    results.Add($"{containingClass} in {relativePath}{lineInfo}");
                }
                catch { }
            }
            return results.Distinct().ToList();
        }

        /// <summary>
        /// Reads all bytes from a file using shared access to avoid locking issues.
        /// This allows reading DLL/PDB files while Unity has them open.
        /// </summary>
        private static byte[] ReadAllBytesShared(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bytes = new byte[fs.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = fs.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                    return bytes;
                }
            }
            catch { return null; }
        }

        #endregion
    }
}
