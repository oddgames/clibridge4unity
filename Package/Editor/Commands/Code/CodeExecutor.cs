using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Compile and execute C# code using Roslyn (preferred) or CSharpCodeProvider/mcs (fallback).
    /// CODE_EXEC: fire-and-forget (returns immediately, check LOG for result)
    /// CODE_EXEC_RETURN: waits for result (25s main thread timeout)
    /// </summary>
    public static class CodeExecutor
    {
        private static ConcurrentDictionary<int, Assembly> cachedAssemblies = new ConcurrentDictionary<int, Assembly>();
        private static bool initialized = false;

        // Active compiler backend
        private static bool useRoslyn;
        internal static string lastRoslynError; // Debug: last Roslyn error for inspection

        // mcs state
        private static string[] mcsReferences;
        private static string monoLibDir;
        private static string mcsResponseFilePath;

        // Roslyn state (reflection-based, no compile-time dependency on Microsoft.CodeAnalysis)
        private static string[] roslynReferencePaths;
        private static object roslynParseOptions;     // CSharpParseOptions instance
        private static object roslynCompileOptions;   // CSharpCompilationOptions instance
        private static MethodInfo roslynParseText;    // CSharpSyntaxTree.ParseText(string, CSharpParseOptions)
        private static MethodInfo roslynCreateRef;    // MetadataReference.CreateFromFile(string)
        private static MethodInfo roslynCreate;       // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
        private static MethodInfo roslynEmit;         // CSharpCompilation.Emit(Stream, ...)
        private static PropertyInfo roslynSuccess;    // EmitResult.Success
        private static PropertyInfo roslynDiags;      // EmitResult.Diagnostics
        private static PropertyInfo roslynSeverity;   // Diagnostic.Severity
        private static object roslynSeverityError;    // DiagnosticSeverity.Error enum value
        private static Type roslynSyntaxTreeType;     // SyntaxTree base type (for typed arrays)
        private static Type roslynMetaRefType;        // MetadataReference base type (for typed arrays)
        private static Array roslynCachedRefs;        // Pre-built MetadataReference[] array

        /// <summary>
        /// If code starts with @, treat it as a file path and read the code from that file.
        /// CLI can write code to a temp file to avoid shell escaping issues.
        /// </summary>
        private static string ResolveCode(string code)
        {
            if (code != null && code.StartsWith("@"))
            {
                string filePath = code.Substring(1).Trim();
                Debug.Log($"[Bridge] ResolveCode: path=[{filePath}] exists={File.Exists(filePath)}");
                if (File.Exists(filePath))
                    return File.ReadAllText(filePath);
            }
            return code;
        }

        [BridgeCommand("CODE_EXEC", "Compile and execute C# code (fire-and-forget)",
            Category = "Code",
            Usage = "CODE_EXEC <c# code>\n  CODE_EXEC @/path/to/tempfile.cs  (read code from file)",
            RequiresMainThread = false)]
        public static async Task<string> Execute(string code)
        {
            try
            {
                code = ResolveCode(code);
                if (string.IsNullOrEmpty(code))
                    return Response.Error("Code required.\nExample: CODE_EXEC GameObject.CreatePrimitive(PrimitiveType.Cube)");

                if (!initialized) Initialize();

                string fullCode = WrapCode(code);
                int codeHash = fullCode.GetHashCode();

                // Compile (can happen on any thread)
                Assembly assembly;
                if (!cachedAssemblies.TryGetValue(codeHash, out assembly))
                {
                    var compileResult = Compile(fullCode);
                    if (compileResult.Error != null)
                        return compileResult.Error;
                    assembly = compileResult.Assembly;
                    cachedAssemblies[codeHash] = assembly;
                }

                // Execute on main thread and wait for completion
                var asm = assembly;
                string desc = code.Length > 80 ? $"CODE_EXEC|{code.Substring(0, 80)}..." : $"CODE_EXEC|{code}";
                try
                {
                    var result = await CommandRegistry.RunOnMainThreadAsync<object>(() =>
                    {
                        var r = RunAssembly(asm);
                        Debug.Log($"[Bridge] CODE_EXEC completed -> {r ?? "null"}");
                        return null;
                    }, desc);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Bridge] CODE_EXEC failed: {ex}");
                }

                // Wait up to 5s for additional logs (code may trigger async operations)
                // Exit early once logs settle (no new logs for 500ms)
                long lastLogId = CommandRegistry.GetLastLogId?.Invoke() ?? 0;
                int elapsed = 0;
                int stableMs = 0;
                while (elapsed < 5000)
                {
                    await Task.Delay(250);
                    elapsed += 250;
                    long currentId = CommandRegistry.GetLastLogId?.Invoke() ?? 0;
                    if (currentId > lastLogId)
                    {
                        lastLogId = currentId;
                        stableMs = 0; // reset stability counter on new log
                    }
                    else
                    {
                        stableMs += 250;
                        if (stableMs >= 500) break; // no new logs for 500ms, we're done
                    }
                }

                return Response.Success("Code executed on main thread.");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("CODE_EXEC_RETURN", "Compile and execute C# code (waits for result, 25s timeout)",
            Category = "Code",
            Usage = "CODE_EXEC_RETURN <c# code>",
            RequiresMainThread = false)]
        public static async Task<string> ExecuteReturn(string code)
        {
            try
            {
                code = ResolveCode(code);
                if (string.IsNullOrEmpty(code))
                    return Response.Error("Code required.\nExample: CODE_EXEC_RETURN 1+1");

                if (!initialized) Initialize();

                string fullCode = WrapCode(code);
                int codeHash = fullCode.GetHashCode();

                // Compile on background thread (NOT main thread - compilation can be slow)
                Assembly assembly;
                if (!cachedAssemblies.TryGetValue(codeHash, out assembly))
                {
                    var compileResult = Compile(fullCode);
                    if (compileResult.Error != null)
                        return compileResult.Error;
                    assembly = compileResult.Assembly;
                    cachedAssemblies[codeHash] = assembly;
                }

                // Execute on main thread (Unity API access requires it)
                var asm = assembly;
                string desc = code.Length > 80 ? $"CODE_EXEC_RETURN|{code.Substring(0, 80)}..." : $"CODE_EXEC_RETURN|{code}";
                var result = await CommandRegistry.RunOnMainThreadAsync<object>(() => RunAssembly(asm), desc);
                string serialized = SerializeResult(result);

                // Small results inline, large results go to file
                if (serialized.Length <= 2000)
                    return Response.Success(serialized);

                string outputDir = Path.Combine(
                    Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
                    "clibridge4unity_output");
                Directory.CreateDirectory(outputDir);
                string outputFile = Path.Combine(outputDir, $"exec_result_{DateTime.Now:HHmmss}.txt");
                File.WriteAllText(outputFile, serialized);

                // Return summary + file path
                string type = result?.GetType().Name ?? "null";
                string preview = serialized.Length > 200 ? serialized.Substring(0, 200) + "..." : serialized;
                return Response.Success($"type: {type}\nlength: {serialized.Length}\npreview: {preview}\noutput: {outputFile}");
            }
            catch (TargetInvocationException ex)
            {
                return Response.Exception(ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        static string SerializeResult(object obj)
        {
            if (obj == null) return "null";

            var type = obj.GetType();

            // Primitives, strings — direct
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum)
                return obj.ToString();

            // Serialize with Newtonsoft — depth limit, collection truncation, pretty print
            try
            {
                var serialized = SerializeWithLimits(obj, 0);
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    serialized, Newtonsoft.Json.Formatting.Indented);

                if (json.Length > 32000)
                    json = json.Substring(0, 32000) + "\n... (truncated, >32KB)";

                return json;
            }
            catch
            {
                return obj.ToString();
            }
        }

        const int MaxDepth = 4;
        const int MaxItems = 100;

        static object SerializeWithLimits(object obj, int depth)
        {
            if (obj == null) return null;
            if (depth > MaxDepth) return obj.ToString();

            var type = obj.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return obj;
            if (type.IsEnum) return obj.ToString();

            // Unity objects — name + type
            if (obj is UnityEngine.Object uObj)
                return uObj != null ? $"{type.Name}(\"{uObj.name}\")" : $"{type.Name}(destroyed)";

            // Dictionary
            if (obj is IDictionary dict)
            {
                var result = new Dictionary<string, object>();
                int count = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (count++ >= MaxItems) { result["..."] = $"{dict.Count - MaxItems} more"; break; }
                    result[e.Key?.ToString() ?? "null"] = SerializeWithLimits(e.Value, depth + 1);
                }
                return result;
            }

            // Collections
            if (obj is IEnumerable enumerable && type != typeof(string))
            {
                var list = new List<object>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= MaxItems) { list.Add($"... ({count} total, truncated at {MaxItems})"); break; }
                    list.Add(SerializeWithLimits(item, depth + 1));
                }
                return list;
            }

            // Structs/classes — public fields + readable properties
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var readable = new List<PropertyInfo>();
            foreach (var p in props)
                if (p.CanRead && p.GetIndexParameters().Length == 0) readable.Add(p);

            if (fields.Length == 0 && readable.Count == 0)
                return obj.ToString();

            var dict2 = new Dictionary<string, object>();
            dict2["_type"] = type.Name;
            foreach (var f in fields)
            {
                try { dict2[f.Name] = SerializeWithLimits(f.GetValue(obj), depth + 1); }
                catch { dict2[f.Name] = "<error>"; }
            }
            foreach (var p in readable)
            {
                if (dict2.Count >= 30) { dict2["..."] = $"{readable.Count - 30} more properties"; break; }
                try { dict2[p.Name] = SerializeWithLimits(p.GetValue(obj), depth + 1); }
                catch { dict2[p.Name] = "<error>"; }
            }
            return dict2;
        }

        struct CompileResult
        {
            public Assembly Assembly;
            public string Error;
        }

        private static CompileResult Compile(string fullCode)
        {
            if (useRoslyn)
            {
                try
                {
                    var result = CompileWithRoslyn(fullCode);
                    if (result.Error != null)
                        Debug.LogWarning($"[Bridge] Roslyn compilation error (not falling back):\n{result.Error}");
                    return result;
                }
                catch (Exception ex)
                {
                    lastRoslynError = $"{ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}";
                    Debug.LogError($"[Bridge] Roslyn internal error, falling back to mcs: {lastRoslynError}");
                }
            }
            return CompileWithMcs(fullCode);
        }

        private static CompileResult CompileWithRoslyn(string fullCode)
        {
            // Parse source code — fill all optional params with defaults
            var parseParams = roslynParseText.GetParameters();
            var parseArgs = new object[parseParams.Length];
            parseArgs[0] = fullCode;  // string text
            parseArgs[1] = roslynParseOptions;  // CSharpParseOptions options
            for (int i = 2; i < parseArgs.Length; i++)
                parseArgs[i] = parseParams[i].HasDefaultValue ? parseParams[i].DefaultValue : null;
            var syntaxTree = roslynParseText.Invoke(null, parseArgs);

            // Create typed SyntaxTree[] array for CSharpCompilation.Create
            var treesArray = Array.CreateInstance(roslynSyntaxTreeType, 1);
            treesArray.SetValue(syntaxTree, 0);

            // Create compilation — fill all optional params with defaults
            var createParams = roslynCreate.GetParameters();
            var createArgs = new object[createParams.Length];
            createArgs[0] = "CodeExec_" + fullCode.GetHashCode().ToString("X");
            createArgs[1] = treesArray;
            createArgs[2] = roslynCachedRefs;
            createArgs[3] = roslynCompileOptions;
            for (int i = 4; i < createArgs.Length; i++)
                createArgs[i] = createParams[i].HasDefaultValue ? createParams[i].DefaultValue : null;
            var compilation = roslynCreate.Invoke(null, createArgs);

            // Emit to memory stream
            using var ms = new MemoryStream();
            var emitParams = roslynEmit.GetParameters();
            var emitArgs = new object[emitParams.Length];
            emitArgs[0] = ms;
            for (int i = 1; i < emitArgs.Length; i++)
                emitArgs[i] = emitParams[i].HasDefaultValue ? emitParams[i].DefaultValue : null;

            var emitResult = roslynEmit.Invoke(compilation, emitArgs);
            bool success = (bool)roslynSuccess.GetValue(emitResult);

            if (!success)
            {
                var diagnostics = (IEnumerable)roslynDiags.GetValue(emitResult);
                var sourceLines = fullCode.Split('\n');
                var errors = new List<string>();
                foreach (var diag in diagnostics)
                {
                    var severity = roslynSeverity.GetValue(diag);
                    if (severity.Equals(roslynSeverityError))
                    {
                        // Diagnostic.ToString() includes location and message, e.g. "(12,5): error CS1002: ; expected"
                        string msg = diag.ToString();
                        errors.Add(msg);
                    }
                }
                return new CompileResult { Error = Response.Error($"Compilation failed (Roslyn):\n{string.Join("\n", errors)}") };
            }

            ms.Seek(0, SeekOrigin.Begin);
            var assembly = Assembly.Load(ms.ToArray());
            return new CompileResult { Assembly = assembly };
        }

        private static CompileResult CompileWithMcs(string fullCode)
        {
            // Write assembly references to a response file to avoid command-line length limits.
            // Windows has a ~32KB command line limit and Unity 6 can load 200+ assemblies,
            // each with a long absolute path, easily exceeding this limit.
            if (mcsResponseFilePath == null)
            {
                mcsResponseFilePath = Path.Combine(Path.GetTempPath(), "clibridge_mcs_refs.rsp");
                var sb = new StringBuilder();
                foreach (var r in mcsReferences)
                    sb.AppendLine($"-r:\"{r}\"");
                File.WriteAllText(mcsResponseFilePath, sb.ToString());
            }

            using var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = $"-nostdlib -lib:\"{monoLibDir}\" @\"{mcsResponseFilePath}\""
            };

            // Don't use ReferencedAssemblies — they go on the command line and hit the length limit.
            // All -r: references are in the response file passed via CompilerOptions.

            var result = provider.CompileAssemblyFromSource(parameters, fullCode);

            if (result.Errors.HasErrors)
            {
                var sourceLines = fullCode.Split('\n');
                var errors = new List<string>();
                foreach (CompilerError error in result.Errors)
                {
                    if (!error.IsWarning)
                    {
                        string srcLine = (error.Line > 0 && error.Line <= sourceLines.Length)
                            ? sourceLines[error.Line - 1].TrimEnd()
                            : "???";
                        errors.Add($"Line {error.Line}: {error.ErrorText}\n  > {srcLine}");
                    }
                }
                return new CompileResult { Error = Response.Error($"Compilation failed (mcs):\n{string.Join("\n", errors)}") };
            }

            return new CompileResult { Assembly = result.CompiledAssembly };
        }

        private static object RunAssembly(Assembly assembly)
        {
            // Try the generated wrapper class first (from WrapCode)
            var type = assembly.GetType("clibridge4unity.Generated.Runner");
            var method = type?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);

            // For user-provided classes: search all types for Run() or Main() entry points
            if (method == null)
            {
                foreach (var t in assembly.GetExportedTypes())
                {
                    method = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                          ?? t.GetMethod("Main", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    if (method != null) { type = t; break; }
                }
            }

            if (method == null)
                throw new InvalidOperationException(
                    "No entry point found. For full class code, add: public static void Run() or static void Main()");

            // Support Main(string[] args) signature
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]))
                return method.Invoke(null, new object[] { Array.Empty<string>() });

            return method.Invoke(null, null);
        }

        private static string WrapCode(string code)
        {
            code = code.Trim();

            // If it's complete code (has class/namespace), use as-is
            if (code.Contains("class ") || code.Contains("namespace "))
                return code;

            // Parse any custom usings from the code
            var customUsings = new List<string>();
            var codeLines = code.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();

            var nonUsingLines = new List<string>();
            foreach (var line in codeLines)
            {
                if (line.StartsWith("using ") && line.EndsWith(";"))
                    customUsings.Add(line);
                else
                    nonUsingLines.Add(line);
            }

            code = string.Join("\n", nonUsingLines);

            // Auto-wrap single expressions (no semicolons, no braces)
            if (!code.Contains(";") && !code.Contains("{"))
            {
                if (!code.TrimStart().StartsWith("return "))
                    code = $"return {code};";
                else
                    code = $"{code};";
            }
            else if (!code.Contains("return "))
                code = code + "\nreturn null;";

            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");

            foreach (var u in customUsings)
                sb.AppendLine(u);

            sb.AppendLine();
            sb.AppendLine("namespace clibridge4unity.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class Runner");
            sb.AppendLine("    {");
            sb.AppendLine("        public static object Run()");
            sb.AppendLine("        {");
            sb.AppendLine($"            {code}");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static void Initialize()
        {
            string editorPath = Path.GetDirectoryName(UnityEditor.EditorApplication.applicationPath);

            // Try Roslyn first — loaded from Unity's bundled DotNetSdkRoslyn directory
            if (TryInitRoslyn(editorPath))
            {
                CollectRoslynReferences();
                BuildRoslynMetadataRefs();
                Debug.Log($"[Bridge] Code executor: Roslyn backend (C# 11, {roslynCachedRefs.Length} refs)");
            }

            // Always init mcs as fallback
            CollectMcsReferences(editorPath);
            if (!useRoslyn)
                Debug.LogWarning($"[Bridge] Code executor: mcs backend ({mcsReferences.Length} refs) — Roslyn unavailable, C# features limited");

            initialized = true;
        }

        // Directories to search for missing Roslyn dependencies (e.g., System.Reflection.Metadata)
        private static string[] roslynDepDirs;

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (roslynDepDirs == null) return null;
            var asmName = new AssemblyName(args.Name);
            foreach (var dir in roslynDepDirs)
            {
                string path = Path.Combine(dir, asmName.Name + ".dll");
                if (File.Exists(path))
                {
                    try { return Assembly.LoadFrom(path); }
                    catch { }
                }
            }
            return null;
        }

        private static bool TryInitRoslyn(string editorPath)
        {
            try
            {
                Assembly csharpAsm = null, coreAsm = null;

                // Register assembly resolver for Roslyn dependencies BEFORE loading/accessing types.
                // Unity 6 Editor runs on Mono, which doesn't auto-resolve deps like CoreCLR TPA.
                // Missing assemblies (System.Reflection.Metadata, System.Memory, etc.) are found
                // in the NetCoreRuntime shared framework directory.
                string packageRoslynDir = FindPackageRoslynDir();
                string netCoreDir = null;
                string ncBase = Path.Combine(editorPath, "Data", "NetCoreRuntime", "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(ncBase))
                {
                    // Pick the newest version directory
                    var dirs = Directory.GetDirectories(ncBase);
                    if (dirs.Length > 0)
                    {
                        Array.Sort(dirs);
                        netCoreDir = dirs[dirs.Length - 1];
                    }
                }

                var depDirs = new List<string>();
                if (packageRoslynDir != null) depDirs.Add(packageRoslynDir);
                if (netCoreDir != null) depDirs.Add(netCoreDir);
                roslynDepDirs = depDirs.ToArray();
                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve; // idempotent
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

                {
                    // 1. Pre-load dependency DLLs from bundled package
                    if (packageRoslynDir != null)
                    {
                        foreach (var dll in Directory.GetFiles(packageRoslynDir, "*.dll"))
                        {
                            try { Assembly.LoadFrom(dll); }
                            catch { }
                        }
                    }

                    // 2. Try loading from known directories
                    string[] searchDirs = packageRoslynDir != null
                        ? new[] { packageRoslynDir, Path.Combine(editorPath, "Data", "DotNetSdkRoslyn") }
                        : new[] { Path.Combine(editorPath, "Data", "DotNetSdkRoslyn") };

                    foreach (var dir in searchDirs)
                    {
                        string corePath = Path.Combine(dir, "Microsoft.CodeAnalysis.dll");
                        string csharpPath = Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll");
                        if (!File.Exists(corePath) || !File.Exists(csharpPath))
                            continue;

                        try
                        {
                            coreAsm = Assembly.LoadFrom(corePath);
                            csharpAsm = Assembly.LoadFrom(csharpPath);
                            Debug.Log($"[Bridge] Roslyn: loaded from {dir} (v{coreAsm.GetName().Version})");
                            break;
                        }
                        catch (Exception loadEx)
                        {
                            Debug.LogWarning($"[Bridge] Roslyn: failed to load from {dir}: {loadEx.Message}");
                            coreAsm = null;
                            csharpAsm = null;
                        }
                    }

                    // 3. If loading from directories failed, check if already in AppDomain
                    if (coreAsm == null || csharpAsm == null)
                    {
                        coreAsm = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis");
                        csharpAsm = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp");
                        if (coreAsm != null && csharpAsm != null)
                            Debug.Log($"[Bridge] Roslyn: using pre-loaded assemblies (v{coreAsm.GetName().Version})");
                    }
                }

                if (coreAsm == null || csharpAsm == null)
                {
                    Debug.LogWarning("[Bridge] Roslyn: no compatible assemblies found, using mcs fallback");
                    return false;
                }

                // Resolve types
                var syntaxTreeCSharp = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                var compilationType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                var compileOptionsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
                var parseOptionsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
                var langVersionType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion");
                roslynSyntaxTreeType = coreAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree");
                roslynMetaRefType = coreAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
                var portableRefType = coreAsm.GetType("Microsoft.CodeAnalysis.PortableExecutableReference");
                var outputKindType = coreAsm.GetType("Microsoft.CodeAnalysis.OutputKind");
                var diagSeverityType = coreAsm.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity");
                var diagnosticType = coreAsm.GetType("Microsoft.CodeAnalysis.Diagnostic");

                if (syntaxTreeCSharp == null || compilationType == null || roslynSyntaxTreeType == null)
                    return false;

                // Build parse options: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest)
                // Cannot use Activator.CreateInstance — constructor has many optional params, no single-arg overload
                object langLatest = Enum.Parse(langVersionType, "Latest");
                var parseDefault = parseOptionsType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                roslynParseOptions = parseDefault.GetValue(null);
                var withLangVersion = parseOptionsType.GetMethod("WithLanguageVersion");
                roslynParseOptions = withLangVersion.Invoke(roslynParseOptions, new object[] { langLatest });

                // Build compilation options via constructor with all params filled from defaults
                // CSharpCompilationOptions has one constructor with OutputKind as first required param + many optional
                object outputKindDll = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
                var optionsCtor = compileOptionsType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                var ctorParams = optionsCtor.GetParameters();
                var ctorArgs = new object[ctorParams.Length];
                ctorArgs[0] = outputKindDll; // OutputKind (required)
                for (int i = 1; i < ctorParams.Length; i++)
                    ctorArgs[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : null;
                roslynCompileOptions = optionsCtor.Invoke(ctorArgs);
                // Enable unsafe code
                var withUnsafe = compileOptionsType.GetMethod("WithAllowUnsafe");
                if (withUnsafe != null)
                    roslynCompileOptions = withUnsafe.Invoke(roslynCompileOptions, new object[] { true });

                // Cache methods
                // CSharpSyntaxTree.ParseText — pick overload that takes string as first param
                // Use the overload with the most params and fill defaults, to ensure compatibility
                roslynParseText = syntaxTreeCSharp.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "ParseText" && m.GetParameters().Length >= 2
                        && m.GetParameters()[0].ParameterType == typeof(string))
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault();

                // MetadataReference.CreateFromFile(string path, ...)
                roslynCreateRef = roslynMetaRefType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "CreateFromFile")
                    .OrderBy(m => m.GetParameters().Length)
                    .First();

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                roslynCreate = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Create" && m.GetParameters().Length >= 4)
                    .OrderBy(m => m.GetParameters().Length)
                    .First();

                // CSharpCompilation.Emit(Stream, ...)
                roslynEmit = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Stream))
                    .OrderBy(m => m.GetParameters().Length)
                    .First();

                // EmitResult properties
                var emitResultType = roslynEmit.ReturnType;
                roslynSuccess = emitResultType.GetProperty("Success");
                roslynDiags = emitResultType.GetProperty("Diagnostics");

                // Diagnostic.Severity
                roslynSeverity = diagnosticType.GetProperty("Severity");
                roslynSeverityError = Enum.Parse(diagSeverityType, "Error");

                if (roslynParseText == null || roslynCreateRef == null || roslynCreate == null || roslynEmit == null)
                    return false;

                useRoslyn = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bridge] Roslyn init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static string FindPackageRoslynDir()
        {
            // Find Roslyn DLLs bundled with this package (Editor/Commands/Code/Roslyn/)
            // Works for both local development and UPM installations
            try
            {
                // Use the location of this assembly (clibridge4unity.Commands.Code.dll)
                // to find the Roslyn directory relative to it
                var thisAsm = typeof(CodeExecutor).Assembly;
                if (!string.IsNullOrEmpty(thisAsm.Location))
                {
                    // In UPM, DLLs compiled from asmdef are in Library/ScriptAssemblies/
                    // but source files are in Packages/. Use Unity's package path instead.
                }

                // Search known locations for the bundled Roslyn DLLs
                string[] candidates = new[]
                {
                    // UPM package (symlinked or cached)
                    "Packages/au.com.oddgames.clibridge4unity/Editor/Plugins/Roslyn",
                    // Local development
                    "Assets/Editor/Plugins/Roslyn",
                };

                foreach (var candidate in candidates)
                {
                    string fullPath = Path.GetFullPath(candidate);
                    if (Directory.Exists(fullPath) &&
                        File.Exists(Path.Combine(fullPath, "Microsoft.CodeAnalysis.dll")))
                        return fullPath;
                }
            }
            catch { }
            return null;
        }

        private static void CollectRoslynReferences()
        {
            // Roslyn on CoreCLR can use all loaded assemblies directly — no filtering needed
            var refs = new List<string>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                        continue;
                    if (!File.Exists(assembly.Location))
                        continue;
                    if (added.Add(assembly.Location))
                        refs.Add(assembly.Location);
                }
                catch { }
            }

            // Also add CoreCLR trusted platform assemblies that may not be loaded yet
            var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (trustedPlatformAssemblies != null)
            {
                foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
                {
                    if (File.Exists(path) && added.Add(path))
                        refs.Add(path);
                }
            }

            roslynReferencePaths = refs.ToArray();
        }

        private static void BuildRoslynMetadataRefs()
        {
            var refsList = new List<object>();
            foreach (var path in roslynReferencePaths)
            {
                try
                {
                    // MetadataReference.CreateFromFile(path) — call with default optional params
                    var createParams = roslynCreateRef.GetParameters();
                    var args = new object[createParams.Length];
                    args[0] = path;
                    for (int i = 1; i < args.Length; i++)
                        args[i] = createParams[i].HasDefaultValue ? createParams[i].DefaultValue : null;
                    refsList.Add(roslynCreateRef.Invoke(null, args));
                }
                catch { }
            }

            roslynCachedRefs = Array.CreateInstance(roslynMetaRefType, refsList.Count);
            for (int i = 0; i < refsList.Count; i++)
                roslynCachedRefs.SetValue(refsList[i], i);
        }

        private static void CollectMcsReferences(string editorPath)
        {
            var refs = new List<string>();
            var added = new HashSet<string>();

            // Find Unity's Mono lib directory (CSharpCodeProvider uses mono's mcs compiler)
            monoLibDir = Path.Combine(editorPath, "Data", "MonoBleedingEdge", "lib", "mono", "4.5");

            // Add Mono's core framework assemblies (required by mcs compiler)
            foreach (var name in new[] { "mscorlib.dll", "System.dll", "System.Core.dll" })
            {
                string path = Path.Combine(monoLibDir, name);
                if (File.Exists(path) && added.Add(path))
                    refs.Add(path);
            }

            // Add netstandard.dll from Facades (Unity assemblies target netstandard 2.1)
            string netstandardPath = Path.Combine(monoLibDir, "Facades", "netstandard.dll");
            if (File.Exists(netstandardPath) && added.Add(netstandardPath))
                refs.Add(netstandardPath);

            // Add all loaded assemblies (Unity, project, etc) - skip CoreCLR framework assemblies
            // that conflict with Mono's. Mono framework assemblies are added explicitly above.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                        continue;
                    if (!File.Exists(assembly.Location))
                        continue;

                    // Skip ALL System.* and Microsoft.CodeAnalysis* assemblies from loaded assemblies.
                    // CoreCLR loads its own System.Linq, System.Collections, etc. which conflict
                    // with Mono's framework (System.Core.dll, System.dll). The Mono framework
                    // assemblies are added explicitly above, so we don't need any System.* from
                    // the AppDomain.
                    string asmName = assembly.GetName().Name;
                    if (asmName == "mscorlib" || asmName == "netstandard" ||
                        asmName.StartsWith("System.") || asmName.StartsWith("System,") ||
                        asmName == "System.Private.CoreLib" || asmName == "System" ||
                        asmName.StartsWith("Microsoft.CodeAnalysis"))
                        continue;

                    if (added.Add(assembly.Location))
                        refs.Add(assembly.Location);
                }
                catch { }
            }

            mcsReferences = refs.ToArray();
            mcsResponseFilePath = null; // Force response file regeneration
        }
    }
}
