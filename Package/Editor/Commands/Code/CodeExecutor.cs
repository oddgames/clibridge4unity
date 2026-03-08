using System;
using System.CodeDom.Compiler;
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
    /// Compile and execute C# code using CSharpCodeProvider.
    /// CODE_EXEC: fire-and-forget (returns immediately, check LOG for result)
    /// CODE_EXEC_RETURN: waits for result (25s main thread timeout)
    /// </summary>
    public static class CodeExecutor
    {
        private static ConcurrentDictionary<int, Assembly> cachedAssemblies = new ConcurrentDictionary<int, Assembly>();
        private static string[] referenceAssemblies;
        private static string monoLibDir;
        private static bool initialized = false;

        /// <summary>
        /// If code starts with @, treat it as a file path and read the code from that file.
        /// CLI can write code to a temp file to avoid shell escaping issues.
        /// </summary>
        private static string ResolveCode(string code)
        {
            if (code != null && code.StartsWith("@"))
            {
                string filePath = code.Substring(1).Trim();
                if (File.Exists(filePath))
                {
                    string fileCode = File.ReadAllText(filePath);
                    // Clean up temp file after reading
                    try { File.Delete(filePath); } catch { }
                    return fileCode;
                }
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
                return Response.Success(result?.ToString() ?? "null");
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

        struct CompileResult
        {
            public Assembly Assembly;
            public string Error;
        }

        private static string responseFilePath;

        private static CompileResult Compile(string fullCode)
        {
            // Write assembly references to a response file to avoid command-line length limits.
            // Windows has a ~32KB command line limit and Unity 6 can load 200+ assemblies,
            // each with a long absolute path, easily exceeding this limit.
            if (responseFilePath == null)
            {
                responseFilePath = Path.Combine(Path.GetTempPath(), "clibridge_mcs_refs.rsp");
                var sb = new StringBuilder();
                foreach (var r in referenceAssemblies)
                    sb.AppendLine($"-r:\"{r}\"");
                File.WriteAllText(responseFilePath, sb.ToString());
            }

            using var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = $"-nostdlib -lib:\"{monoLibDir}\" @\"{responseFilePath}\""
            };

            // Don't use ReferencedAssemblies — they go on the command line and hit the length limit.
            // All -r: references are in the response file passed via CompilerOptions.

            var result = provider.CompileAssemblyFromSource(parameters, fullCode);

            if (result.Errors.HasErrors)
            {
                var errors = new List<string>();
                foreach (CompilerError error in result.Errors)
                {
                    if (!error.IsWarning)
                        errors.Add($"Line {error.Line}: {error.ErrorText}");
                }
                return new CompileResult { Error = Response.Error($"Compilation failed:\n{string.Join("\n", errors)}") };
            }

            return new CompileResult { Assembly = result.CompiledAssembly };
        }

        private static object RunAssembly(Assembly assembly)
        {
            var type = assembly.GetType("clibridge4unity.Generated.Runner");
            var method = type?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);

            if (method == null)
                throw new InvalidOperationException("Could not find Run method in compiled code");

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
            var refs = new List<string>();
            var added = new HashSet<string>();

            // Find Unity's Mono lib directory (CSharpCodeProvider uses mono's mcs compiler)
            // The -nostdlib flag prevents mcs from auto-referencing its mscorlib,
            // and -lib: tells it where to find framework assemblies
            string editorPath = Path.GetDirectoryName(UnityEditor.EditorApplication.applicationPath);
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
            // that conflict with Mono's (mscorlib, System.Private.CoreLib, etc)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                        continue;
                    if (!File.Exists(assembly.Location))
                        continue;

                    // Skip CoreCLR framework assemblies - the Mono compiler can't use them
                    string asmName = assembly.GetName().Name;
                    if (asmName == "mscorlib" || asmName == "System.Private.CoreLib" ||
                        asmName == "System.Runtime" || asmName == "netstandard")
                        continue;

                    if (added.Add(assembly.Location))
                        refs.Add(assembly.Location);
                }
                catch { }
            }

            referenceAssemblies = refs.ToArray();
            responseFilePath = null; // Force response file regeneration
            initialized = true;
            Debug.Log($"[Bridge] Code executor initialized with {referenceAssemblies.Length} references (Mono lib: {monoLibDir})");
        }
    }
}
