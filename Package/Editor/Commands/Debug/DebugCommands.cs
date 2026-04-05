using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace clibridge4unity.Commands
{
    /// <summary>
    /// DEBUG command — Phase 1: Expression evaluation, object inspection, and thread stacks.
    /// Phase 2 will add Mono soft debugger attach/breakpoint/stepping.
    /// </summary>
    public static class DebugCommands
    {
        [BridgeCommand("DEBUG", "Debug tools: eval, inspect, stack, trace",
            Category = "Code",
            Usage = "DEBUG eval <expression>       - Evaluate C# expression in Unity's runtime\n" +
                    "  DEBUG inspect <path>          - Inspect a GameObject by hierarchy path\n" +
                    "  DEBUG stack                   - Dump managed thread call stacks\n" +
                    "  DEBUG trace <code>            - Execute code with line-by-line trace output\n" +
                    "  DEBUG status                  - Debugger status and capabilities",
            RequiresMainThread = false,
            Streaming = true,
            TimeoutSeconds = 60)]
        public static async Task Run(string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };

            try
            {
                if (string.IsNullOrWhiteSpace(data))
                {
                    await writer.WriteLineAsync("Usage: DEBUG <subcommand> [args]\n" +
                        "  eval <expression>   - Evaluate C# expression\n" +
                        "  inspect <path>      - Inspect GameObject\n" +
                        "  stack               - Thread call stacks\n" +
                        "  trace <code>        - Execute with line-by-line trace\n" +
                        "  status              - Debugger capabilities");
                    return;
                }

                // Parse subcommand
                int spaceIdx = data.IndexOf(' ');
                string subcmd = (spaceIdx > 0 ? data.Substring(0, spaceIdx) : data).Trim().ToLowerInvariant();
                string args = spaceIdx > 0 ? data.Substring(spaceIdx + 1).Trim() : "";

                switch (subcmd)
                {
                    case "eval":
                        await HandleEval(writer, args);
                        break;
                    case "inspect":
                        await HandleInspect(writer, args);
                        break;
                    case "stack":
                        await HandleStack(writer);
                        break;
                    case "trace":
                        await HandleTrace(writer, args);
                        break;
                    case "status":
                        await HandleStatus(writer);
                        break;
                    default:
                        await writer.WriteLineAsync($"Unknown subcommand: '{subcmd}'. Use: eval, inspect, stack, trace, status");
                        break;
                }
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Evaluate a C# expression in Unity's runtime context.
        /// Expressions are compiled with Roslyn and executed on the main thread.
        /// </summary>
        private static async Task HandleEval(StreamWriter writer, string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                await writer.WriteLineAsync("Usage: DEBUG eval <expression>\n" +
                    "Examples:\n" +
                    "  DEBUG eval Camera.main.transform.position\n" +
                    "  DEBUG eval FindObjectsOfType<Light>().Length\n" +
                    "  DEBUG eval EditorApplication.isPlaying");
                return;
            }

            CodeExecutor.Initialize();

            string fullCode = CodeExecutor.WrapCode(expression);
            var result = CodeExecutor.Compile(fullCode);
            if (result.Error != null)
            {
                await writer.WriteLineAsync(result.Error);
                return;
            }

            try
            {
                var obj = await CommandRegistry.RunOnMainThreadAsync(() =>
                    CodeExecutor.RunAssembly(result.Assembly));

                await writer.WriteLineAsync(FormatValue(obj));
            }
            catch (TargetInvocationException ex)
            {
                await writer.WriteLineAsync($"Error: {(ex.InnerException ?? ex).Message}");
            }
        }

        /// <summary>
        /// Inspect a GameObject by hierarchy path — dumps components, fields, and child objects.
        /// </summary>
        private static async Task HandleInspect(StreamWriter writer, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                await writer.WriteLineAsync("Usage: DEBUG inspect <gameobject path>\n" +
                    "Examples:\n" +
                    "  DEBUG inspect Main Camera\n" +
                    "  DEBUG inspect Canvas/Panel/Button");
                return;
            }

            var output = await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                var go = GameObject.Find(path);
                if (go == null)
                    return $"GameObject not found: '{path}'";

                var sb = new StringBuilder();
                sb.AppendLine($"=== {go.name} ===");
                sb.AppendLine($"active: {go.activeSelf} (hierarchy: {go.activeInHierarchy})");
                sb.AppendLine($"layer: {LayerMask.LayerToName(go.layer)} ({go.layer})");
                sb.AppendLine($"tag: {go.tag}");
                sb.AppendLine($"position: {go.transform.position}");
                sb.AppendLine($"rotation: {go.transform.eulerAngles}");
                sb.AppendLine($"scale: {go.transform.localScale}");
                sb.AppendLine($"children: {go.transform.childCount}");
                sb.AppendLine();

                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var type = comp.GetType();
                    sb.AppendLine($"[{type.Name}]");

                    // Dump serialized fields (same as Inspector would show)
                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        bool serialized = field.IsPublic ||
                            field.GetCustomAttribute<SerializeField>() != null;
                        if (!serialized) continue;
                        if (field.GetCustomAttribute<HideInInspector>() != null) continue;

                        try
                        {
                            var val = field.GetValue(comp);
                            sb.AppendLine($"  {field.Name}: {FormatValueCompact(val)} ({field.FieldType.Name})");
                        }
                        catch { sb.AppendLine($"  {field.Name}: <error reading>"); }
                    }

                    // Public properties (non-indexers, with getters)
                    var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                        .Take(10); // limit to avoid noise
                    foreach (var prop in props)
                    {
                        // Skip common noisy properties
                        if (prop.Name == "gameObject" || prop.Name == "transform" ||
                            prop.Name == "tag" || prop.Name == "name") continue;
                        try
                        {
                            var val = prop.GetValue(comp);
                            sb.AppendLine($"  {prop.Name}: {FormatValueCompact(val)} ({prop.PropertyType.Name})");
                        }
                        catch { }
                    }
                    sb.AppendLine();
                }

                return sb.ToString().TrimEnd();
            });

            await writer.WriteLineAsync(output);
        }

        /// <summary>
        /// Dump managed call stacks for all threads.
        /// </summary>
        private static async Task HandleStack(StreamWriter writer)
        {
            // Current thread stack
            var currentStack = new StackTrace(true);
            await writer.WriteLineAsync($"=== Current Thread ({Thread.CurrentThread.ManagedThreadId}) ===");
            await writer.WriteLineAsync(FormatStackTrace(currentStack));

            // Main thread stack
            try
            {
                var mainStack = await CommandRegistry.RunOnMainThreadAsync(() =>
                {
                    return new StackTrace(true).ToString();
                });
                await writer.WriteLineAsync($"=== Main Thread ===");
                await writer.WriteLineAsync(mainStack.Trim());
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync($"=== Main Thread ===\n  (unavailable: {ex.Message})");
            }
        }

        /// <summary>
        /// Execute code with line-by-line trace output.
        /// Injects Console.WriteLine before each statement so you can see execution flow.
        /// </summary>
        private static async Task HandleTrace(StreamWriter writer, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                await writer.WriteLineAsync("Usage: DEBUG trace <code>\n" +
                    "Executes C# code with line-by-line trace output.\n" +
                    "Example: DEBUG trace int x = 10; int y = x + 5; Debug.Log(y);");
                return;
            }

            // Read from file if @path
            if (code.StartsWith("@"))
            {
                string filePath = code.Substring(1).Trim();
                if (File.Exists(filePath))
                    code = File.ReadAllText(filePath);
            }

            CodeExecutor.Initialize();

            // If it's already a full class, we can't easily instrument it — just compile and run
            if (code.Contains("class ") || code.Contains("namespace "))
            {
                await writer.WriteLineAsync("[trace] Full class code detected — running without instrumentation");
                var compResult = CodeExecutor.Compile(code);
                if (compResult.Error != null) { await writer.WriteLineAsync(compResult.Error); return; }
                try
                {
                    var obj = await CommandRegistry.RunOnMainThreadAsync(() =>
                        CodeExecutor.RunAssembly(compResult.Assembly));
                    await writer.WriteLineAsync($"[result] {FormatValue(obj)}");
                }
                catch (Exception ex) { await writer.WriteLineAsync($"[error] {ex.Message}"); }
                return;
            }

            // Split into statements and instrument each one
            var statements = SplitStatements(code);
            var traceLog = new List<string>();

            // Build instrumented code: after each statement, capture its line and any changed variables
            var sb = new StringBuilder();
            sb.AppendLine("var __trace = new System.Collections.Generic.List<string>();");

            for (int i = 0; i < statements.Count; i++)
            {
                string stmt = statements[i].Trim();
                if (string.IsNullOrEmpty(stmt)) continue;

                sb.AppendLine(stmt);
                sb.AppendLine($"__trace.Add(\"[{i + 1}] {EscapeForString(stmt)}\");");
            }

            sb.AppendLine("return string.Join(\"\\n\", __trace);");

            string fullCode = CodeExecutor.WrapCode(sb.ToString());
            var result = CodeExecutor.Compile(fullCode);
            if (result.Error != null)
            {
                await writer.WriteLineAsync(result.Error);
                return;
            }

            try
            {
                var obj = await CommandRegistry.RunOnMainThreadAsync(() =>
                    CodeExecutor.RunAssembly(result.Assembly));
                string traceOutput = obj?.ToString() ?? "(no output)";
                await writer.WriteLineAsync(traceOutput);
            }
            catch (TargetInvocationException ex)
            {
                await writer.WriteLineAsync($"[error] {(ex.InnerException ?? ex).Message}");
            }
        }

        /// <summary>
        /// Show debugger status and capabilities.
        /// </summary>
        private static async Task HandleStatus(StreamWriter writer)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== DEBUG Status ===");
            sb.AppendLine("phase: 1 (expression evaluation)");
            bool roslynOk = false;
            try { CodeExecutor.Initialize(); roslynOk = CodeExecutor.Compile("class T{}").Assembly != null; } catch { }
            sb.AppendLine($"roslyn: {(roslynOk ? "available" : "unavailable")}");

            var (playing, compiling) = await CommandRegistry.RunOnMainThreadAsync(() =>
                (EditorApplication.isPlaying, EditorApplication.isCompiling));
            sb.AppendLine($"playing: {playing}");
            sb.AppendLine($"compiling: {compiling}");

            // Check if Mono debugger agent is running
            var args = Environment.GetCommandLineArgs();
            var debuggerArg = args.FirstOrDefault(a => a.Contains("debugger-agent"));
            sb.AppendLine($"debuggerAgent: {(debuggerArg != null ? "enabled" : "not enabled")}");
            if (debuggerArg != null)
                sb.AppendLine($"debuggerArgs: {debuggerArg}");

            // Check for Mono.Debugger.Soft availability
            bool hasSoftDebugger = false;
            try
            {
                string editorPath = Path.GetDirectoryName(EditorApplication.applicationPath);
                string softDebuggerPath = Path.Combine(editorPath, "Data", "MonoBleedingEdge", "lib", "mono", "4.5", "Mono.Debugger.Soft.dll");
                hasSoftDebugger = File.Exists(softDebuggerPath);
            }
            catch { }
            sb.AppendLine($"softDebuggerDll: {(hasSoftDebugger ? "found" : "not found")}");

            sb.AppendLine();
            sb.AppendLine("Available commands:");
            sb.AppendLine("  DEBUG eval <expr>     - Evaluate expression");
            sb.AppendLine("  DEBUG inspect <path>  - Inspect GameObject");
            sb.AppendLine("  DEBUG stack           - Thread stacks");
            sb.AppendLine("  DEBUG trace <code>    - Execute with trace");
            sb.AppendLine("  DEBUG status          - This info");
            sb.AppendLine();
            sb.AppendLine("Phase 2 (planned): attach, break, step, locals, continue");

            await writer.WriteAsync(sb.ToString());
        }

        #region Formatting Helpers

        private static string FormatValue(object obj)
        {
            if (obj == null) return "null";
            var type = obj.GetType();

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return $"{obj} ({type.Name})";

            if (obj is UnityEngine.Object uobj)
            {
                if (obj is GameObject go)
                    return $"{go.name} [active={go.activeSelf}, components={go.GetComponents<Component>().Length}] (GameObject)";
                if (obj is Component comp)
                    return $"{comp.GetType().Name} on '{comp.gameObject.name}' ({type.Name})";
                return $"{uobj.name} (instance={uobj.GetInstanceID()}) ({type.Name})";
            }

            if (obj is System.Collections.IEnumerable enumerable && type != typeof(string))
            {
                int count = 0;
                var preview = new List<string>();
                foreach (var item in enumerable)
                {
                    if (count < 5)
                        preview.Add(FormatValueCompact(item));
                    count++;
                }
                string items = string.Join(", ", preview);
                if (count > 5) items += $", ... ({count} total)";
                return $"[{items}] ({type.Name})";
            }

            // Vectors, Quaternions, Colors — use ToString
            if (type.Namespace == "UnityEngine")
                return $"{obj} ({type.Name})";

            return $"{obj} ({type.Name})";
        }

        private static string FormatValueCompact(object obj)
        {
            if (obj == null) return "null";
            if (obj is string s) return s.Length > 50 ? $"\"{s.Substring(0, 50)}...\"" : $"\"{s}\"";
            if (obj is UnityEngine.Object uobj) return uobj.name;
            return obj.ToString();
        }

        private static string FormatStackTrace(StackTrace trace)
        {
            var sb = new StringBuilder();
            foreach (var frame in trace.GetFrames())
            {
                var method = frame.GetMethod();
                if (method == null) continue;
                string typeName = method.DeclaringType?.Name ?? "?";
                string file = frame.GetFileName();
                int line = frame.GetFileLineNumber();

                if (file != null)
                    sb.AppendLine($"  {typeName}.{method.Name}() at {Path.GetFileName(file)}:{line}");
                else
                    sb.AppendLine($"  {typeName}.{method.Name}()");
            }
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Code Helpers

        /// <summary>
        /// Split code into individual statements by semicolons (respecting strings and braces).
        /// </summary>
        private static List<string> SplitStatements(string code)
        {
            var statements = new List<string>();
            var current = new StringBuilder();
            int braceDepth = 0;
            bool inString = false;
            bool inChar = false;
            bool escaped = false;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];

                if (escaped) { current.Append(c); escaped = false; continue; }
                if (c == '\\') { current.Append(c); escaped = true; continue; }
                if (c == '"' && !inChar) { inString = !inString; current.Append(c); continue; }
                if (c == '\'' && !inString) { inChar = !inChar; current.Append(c); continue; }

                if (!inString && !inChar)
                {
                    if (c == '{') braceDepth++;
                    if (c == '}') braceDepth--;
                    if (c == ';' && braceDepth == 0)
                    {
                        current.Append(c);
                        statements.Add(current.ToString().Trim());
                        current.Clear();
                        continue;
                    }
                }
                current.Append(c);
            }

            string remaining = current.ToString().Trim();
            if (!string.IsNullOrEmpty(remaining))
                statements.Add(remaining);

            return statements;
        }

        private static string EscapeForString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }

        #endregion
    }
}
