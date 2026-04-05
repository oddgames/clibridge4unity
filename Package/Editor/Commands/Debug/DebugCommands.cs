using System;
using System.Collections;
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
    /// DEBUG command — runtime inspection, expression evaluation, and execution tracing.
    /// Phase 1: eval, inspect (reflection tree), trace (instrumented execution), stack.
    /// Phase 2 (planned): Mono soft debugger attach/breakpoint/stepping.
    /// </summary>
    public static class DebugCommands
    {
        const int MaxOutputBytes = 10240;
        const int MaxCollectionItems = 10;
        const int MaxTraceLines = 500;

        [BridgeCommand("DEBUG", "Debug tools: eval, inspect, stack, trace",
            Category = "Code",
            Usage = "DEBUG eval <expression>               - Evaluate C# expression\n" +
                    "  DEBUG inspect <expression> [depth]     - Inspect object tree (reflection)\n" +
                    "  DEBUG inspect <expression> depth --private  - Include private members\n" +
                    "  DEBUG stack                            - Thread call stacks\n" +
                    "  DEBUG trace <code>                     - Execute with line-by-line trace\n" +
                    "  DEBUG trace <code> --maxlines 100      - Limit trace output\n" +
                    "  DEBUG status                           - Debugger capabilities",
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
                    await writer.WriteLineAsync(
                        "Usage: DEBUG <subcommand> [args]\n" +
                        "  eval <expression>              Evaluate C# expression\n" +
                        "  inspect <expression> [depth]   Inspect object tree\n" +
                        "  stack                          Thread call stacks\n" +
                        "  trace <code>                   Execute with line trace\n" +
                        "  status                         Debugger info");
                    return;
                }

                int spaceIdx = data.IndexOf(' ');
                string subcmd = (spaceIdx > 0 ? data.Substring(0, spaceIdx) : data).Trim().ToLowerInvariant();
                string args = spaceIdx > 0 ? data.Substring(spaceIdx + 1).Trim() : "";

                switch (subcmd)
                {
                    case "eval": await HandleEval(writer, args); break;
                    case "inspect": await HandleInspect(writer, args); break;
                    case "stack": await HandleStack(writer); break;
                    case "trace": await HandleTrace(writer, args); break;
                    case "status": await HandleStatus(writer); break;
                    default:
                        await writer.WriteLineAsync($"Unknown: '{subcmd}'. Use: eval, inspect, stack, trace, status");
                        break;
                }
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync($"Error: {ex.Message}");
            }
        }

        #region eval

        static async Task HandleEval(StreamWriter writer, string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                await writer.WriteLineAsync(
                    "Usage: DEBUG eval <expression>\n" +
                    "  DEBUG eval Camera.main.transform.position\n" +
                    "  DEBUG eval EditorApplication.isPlaying\n" +
                    "  DEBUG eval GameObject.FindObjectsOfType<Light>().Length");
                return;
            }

            var (obj, err) = await CompileAndExecute(expression);
            if (err != null) { await writer.WriteLineAsync(err); return; }

            string result = FormatValueInline(obj);
            await writer.WriteLineAsync(result);
        }

        #endregion

        #region inspect

        static async Task HandleInspect(StreamWriter writer, string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                await writer.WriteLineAsync(
                    "Usage: DEBUG inspect <expression> [depth] [--private]\n" +
                    "  DEBUG inspect Camera.main\n" +
                    "  DEBUG inspect Camera.main 2\n" +
                    "  DEBUG inspect Camera.main 2 --private");
                return;
            }

            // Parse: expression [depth] [--private]
            bool includePrivate = args.Contains("--private");
            string clean = args.Replace("--private", "").Trim();

            // Last token might be depth
            int maxDepth = 1;
            var tokens = clean.Split(' ');
            if (tokens.Length > 1 && int.TryParse(tokens.Last(), out int d))
            {
                maxDepth = Math.Max(1, Math.Min(d, 5)); // cap at 5
                clean = string.Join(" ", tokens.Take(tokens.Length - 1));
            }

            var (obj, err) = await CompileAndExecute(clean);
            if (err != null) { await writer.WriteLineAsync(err); return; }
            if (obj == null) { await writer.WriteLineAsync("(null)"); return; }

            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includePrivate) flags |= BindingFlags.NonPublic;

            var sb = new StringBuilder();
            InspectObject(sb, obj, 0, maxDepth, "", flags);

            string output = sb.ToString();
            if (output.Length > MaxOutputBytes)
                output = output.Substring(0, MaxOutputBytes) + "\n... (truncated at 10KB)";

            await writer.WriteAsync(output);
        }

        static void InspectObject(StringBuilder sb, object obj, int depth, int maxDepth, string indent, BindingFlags flags)
        {
            if (obj == null) { sb.AppendLine(indent + "(null)"); return; }

            var type = obj.GetType();
            sb.AppendLine($"{indent}{type.Name} ({type.FullName})");

            if (depth >= maxDepth) return;

            var members = new List<(string name, object value, string typeName, bool isError)>();

            // Properties (non-indexer, readable)
            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                // Skip noisy base properties on Unity objects
                if (depth == 0 && (prop.Name == "gameObject" || prop.Name == "transform")) continue;
                try
                {
                    var val = prop.GetValue(obj);
                    members.Add((prop.Name, val, prop.PropertyType.Name, false));
                }
                catch { members.Add((prop.Name, null, prop.PropertyType.Name, true)); }
            }

            // Fields
            foreach (var field in type.GetFields(flags))
            {
                try
                {
                    var val = field.GetValue(obj);
                    members.Add((field.Name, val, field.FieldType.Name, false));
                }
                catch { members.Add((field.Name, null, field.FieldType.Name, true)); }
            }

            int shown = 0;
            int total = members.Count;
            string childIndent = indent + "  ";

            foreach (var (name, value, typeName, isError) in members)
            {
                if (sb.Length > MaxOutputBytes) break;
                if (shown >= 30)
                {
                    sb.AppendLine($"{indent}... ({total - shown} more)");
                    break;
                }

                string connector = shown < total - 1 ? "├─" : "└─";
                shown++;

                if (isError)
                {
                    sb.AppendLine($"{indent}{connector} {name}: (error reading) ({typeName})");
                    continue;
                }

                string formatted = FormatMemberValue(value, depth + 1, maxDepth, childIndent, flags);
                sb.AppendLine($"{indent}{connector} {name}: {formatted}");
            }
        }

        static string FormatMemberValue(object val, int depth, int maxDepth, string indent, BindingFlags flags)
        {
            if (val == null) return "(null)";
            if (val is string s) return s.Length > 80 ? $"\"{s.Substring(0, 80)}...\"" : $"\"{s}\"";
            var type = val.GetType();
            if (type.IsPrimitive || type == typeof(decimal) || type.IsEnum) return $"{val} ({type.Name})";
            if (type.Namespace == "UnityEngine" && (type.IsValueType || type == typeof(Color)))
                return $"{val} ({type.Name})";

            if (val is ICollection col)
            {
                if (depth >= maxDepth) return $"[{col.Count} items] ({type.Name})";
                var sb = new StringBuilder();
                sb.AppendLine($"[{col.Count} items] ({type.Name})");
                int i = 0;
                foreach (var item in col)
                {
                    if (i >= MaxCollectionItems) { sb.AppendLine($"{indent}  ... ({col.Count - i} more)"); break; }
                    sb.AppendLine($"{indent}  [{i}]: {FormatMemberValue(item, depth + 1, maxDepth, indent + "  ", flags)}");
                    i++;
                }
                return sb.ToString().TrimEnd();
            }

            if (val is UnityEngine.Object uobj)
            {
                if (depth >= maxDepth) return $"{uobj.name} ({type.Name})";
                var sb = new StringBuilder();
                sb.AppendLine($"{uobj.name} ({type.Name})");
                InspectObject(sb, val, depth, maxDepth, indent, flags);
                return sb.ToString().TrimEnd();
            }

            if (depth >= maxDepth) return $"{type.Name} (depth limit)";

            var sb2 = new StringBuilder();
            sb2.AppendLine();
            InspectObject(sb2, val, depth, maxDepth, indent, flags);
            return sb2.ToString().TrimEnd();
        }

        #endregion

        #region stack

        static async Task HandleStack(StreamWriter writer)
        {
            await writer.WriteLineAsync($"=== Current Thread ({Thread.CurrentThread.ManagedThreadId}) ===");
            await writer.WriteLineAsync(FormatStackTrace(new StackTrace(true)));

            try
            {
                var mainStack = await CommandRegistry.RunOnMainThreadAsync(() => new StackTrace(true).ToString());
                await writer.WriteLineAsync($"\n=== Main Thread ===");
                await writer.WriteLineAsync(mainStack.Trim());
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync($"\n=== Main Thread ===\n  (unavailable: {ex.Message})");
            }
        }

        #endregion

        #region trace

        static async Task HandleTrace(StreamWriter writer, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                await writer.WriteLineAsync(
                    "Usage: DEBUG trace <code>\n" +
                    "  DEBUG trace 'int x = 10; int y = x + 5; return y;'\n" +
                    "  DEBUG trace @/path/to/file.cs\n" +
                    "Options: --maxlines 100 --maxtime 5000");
                return;
            }

            // Parse options
            int maxLines = MaxTraceLines;
            int maxTimeMs = 10000;
            if (code.Contains("--maxlines"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(code, @"--maxlines\s+(\d+)");
                if (match.Success) maxLines = int.Parse(match.Groups[1].Value);
                code = System.Text.RegularExpressions.Regex.Replace(code, @"--maxlines\s+\d+", "").Trim();
            }
            if (code.Contains("--maxtime"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(code, @"--maxtime\s+(\d+)");
                if (match.Success) maxTimeMs = int.Parse(match.Groups[1].Value);
                code = System.Text.RegularExpressions.Regex.Replace(code, @"--maxtime\s+\d+", "").Trim();
            }

            // Read from file if @path
            if (code.StartsWith("@"))
            {
                string filePath = code.Substring(1).Trim();
                if (File.Exists(filePath)) code = File.ReadAllText(filePath);
            }

            CodeExecutor.Initialize();

            // Full class code — can't instrument, just run
            if (code.Contains("class ") || code.Contains("namespace "))
            {
                await writer.WriteLineAsync("[trace] Full class code — running without instrumentation");
                var r = CodeExecutor.Compile(code);
                if (r.Error != null) { await writer.WriteLineAsync(r.Error); return; }
                try
                {
                    var obj = await CommandRegistry.RunOnMainThreadAsync(() => CodeExecutor.RunAssembly(r.Assembly));
                    await writer.WriteLineAsync($"[result] {FormatValueInline(obj)}");
                }
                catch (Exception ex) { await writer.WriteLineAsync($"[error] {ex.Message}"); }
                return;
            }

            // Instrument: inject __Trace calls after each statement
            var statements = SplitStatements(code);
            var instrumented = new StringBuilder();
            instrumented.AppendLine($"var __trace = new System.Collections.Generic.List<string>();");
            instrumented.AppendLine($"int __maxLines = {maxLines};");
            instrumented.AppendLine($"var __sw = System.Diagnostics.Stopwatch.StartNew();");
            instrumented.AppendLine($"long __maxMs = {maxTimeMs};");

            // Track declared variables for capture
            var declaredVars = new List<string>();

            for (int i = 0; i < statements.Count; i++)
            {
                string stmt = statements[i].Trim();
                if (string.IsNullOrEmpty(stmt)) continue;

                // Detect variable declarations: "int x = ...", "var x = ...", "string x = ..."
                var declMatch = System.Text.RegularExpressions.Regex.Match(stmt,
                    @"^(?:var|int|long|float|double|decimal|bool|string|char|byte|short|object)\s+(\w+)\s*[=;]");
                if (declMatch.Success)
                    declaredVars.Add(declMatch.Groups[1].Value);

                // Also detect typed declarations: "List<int> x = ..."
                var typedDeclMatch = System.Text.RegularExpressions.Regex.Match(stmt,
                    @"^[\w\.<>,\[\]\s]+\s+(\w+)\s*[=;]");
                if (!declMatch.Success && typedDeclMatch.Success &&
                    !stmt.StartsWith("return ") && !stmt.StartsWith("if ") &&
                    !stmt.StartsWith("for") && !stmt.StartsWith("while"))
                    declaredVars.Add(typedDeclMatch.Groups[1].Value);

                // Emit the statement
                instrumented.AppendLine(stmt);

                // Emit trace capture after each statement
                string stmtEscaped = EscapeForString(stmt);
                if (stmtEscaped.Length > 60) stmtEscaped = stmtEscaped.Substring(0, 60) + "...";

                var varCaptures = new StringBuilder();
                foreach (var v in declaredVars)
                {
                    if (varCaptures.Length > 0) varCaptures.Append(" + \" \" + ");
                    varCaptures.Append($"\"{v}=\" + {v}");
                }

                string traceExpr = varCaptures.Length > 0
                    ? $"$\"[{i + 1}] {stmtEscaped}  → \" + {varCaptures}"
                    : $"\"[{i + 1}] {stmtEscaped}\"";

                instrumented.AppendLine($"__trace.Add({traceExpr});");
                instrumented.AppendLine($"if (__trace.Count >= __maxLines) throw new System.Exception(\"Trace limit (\" + __maxLines + \" lines)\");");
                instrumented.AppendLine($"if (__sw.ElapsedMilliseconds > __maxMs) throw new System.Exception(\"Trace timeout (\" + __maxMs + \"ms)\");");
            }

            instrumented.AppendLine("return string.Join(\"\\n\", __trace);");

            string fullCode = CodeExecutor.WrapCode(instrumented.ToString());
            var result = CodeExecutor.Compile(fullCode);
            if (result.Error != null)
            {
                await writer.WriteLineAsync(result.Error);
                return;
            }

            try
            {
                var obj = await CommandRegistry.RunOnMainThreadAsync(() => CodeExecutor.RunAssembly(result.Assembly));
                await writer.WriteLineAsync(obj?.ToString() ?? "(no output)");
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                // Trace limit/timeout are expected — show partial trace
                if (inner.Message.StartsWith("Trace limit") || inner.Message.StartsWith("Trace timeout"))
                    await writer.WriteLineAsync($"[{inner.Message}]");
                else
                    await writer.WriteLineAsync($"[error at runtime] {inner.Message}");
            }
        }

        #endregion

        #region status

        static async Task HandleStatus(StreamWriter writer)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== DEBUG Status ===");
            sb.AppendLine("phase: 1 (expression evaluation + tracing)");

            bool roslynOk = false;
            try { CodeExecutor.Initialize(); roslynOk = CodeExecutor.Compile("class T{}").Assembly != null; } catch { }
            sb.AppendLine($"roslyn: {(roslynOk ? "available" : "unavailable")}");

            var (playing, compiling) = await CommandRegistry.RunOnMainThreadAsync(() =>
                (EditorApplication.isPlaying, EditorApplication.isCompiling));
            sb.AppendLine($"playing: {playing}");
            sb.AppendLine($"compiling: {compiling}");

            var cmdArgs = Environment.GetCommandLineArgs();
            var debuggerArg = cmdArgs.FirstOrDefault(a => a.Contains("debugger-agent"));
            sb.AppendLine($"debuggerAgent: {(debuggerArg != null ? "enabled" : "not enabled")}");

            bool hasSoftDebugger = false;
            try
            {
                string editorPath = Path.GetDirectoryName(EditorApplication.applicationPath);
                hasSoftDebugger = File.Exists(Path.Combine(editorPath, "Data", "MonoBleedingEdge", "lib", "mono", "4.5", "Mono.Debugger.Soft.dll"));
            }
            catch { }
            sb.AppendLine($"softDebuggerDll: {(hasSoftDebugger ? "found" : "not found")}");

            sb.AppendLine();
            sb.AppendLine("Commands:");
            sb.AppendLine("  eval <expr>                   Evaluate expression");
            sb.AppendLine("  inspect <expr> [depth] [--private]  Object tree");
            sb.AppendLine("  stack                         Thread stacks");
            sb.AppendLine("  trace <code> [--maxlines N]   Instrumented execution");
            sb.AppendLine("  status                        This info");

            await writer.WriteAsync(sb.ToString());
        }

        #endregion

        #region Helpers

        static async Task<(object result, string error)> CompileAndExecute(string expression)
        {
            CodeExecutor.Initialize();

            string fullCode = CodeExecutor.WrapCode(expression);
            var compileResult = CodeExecutor.Compile(fullCode);
            if (compileResult.Error != null)
                return (null, compileResult.Error);

            try
            {
                var asm = compileResult.Assembly;
                var obj = await CommandRegistry.RunOnMainThreadAsync(() => CodeExecutor.RunAssembly(asm));
                return (obj, null);
            }
            catch (TargetInvocationException ex)
            {
                return (null, $"Error: {(ex.InnerException ?? ex).Message}");
            }
            catch (Exception ex)
            {
                return (null, $"Error: {ex.Message}");
            }
        }

        static string FormatValueInline(object obj)
        {
            if (obj == null) return "(null)";
            var type = obj.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum)
                return $"{obj} ({type.Name})";
            if (obj is UnityEngine.Object uobj)
            {
                if (obj is GameObject go) return $"{go.name} [active={go.activeSelf}, components={go.GetComponents<Component>().Length}] (GameObject)";
                if (obj is Component comp) return $"{comp.GetType().Name} on '{comp.gameObject.name}' ({type.Name})";
                return $"{uobj.name} (id={uobj.GetInstanceID()}) ({type.Name})";
            }
            if (obj is ICollection col)
            {
                var items = new List<string>();
                int i = 0;
                foreach (var item in col) { if (i++ >= 5) break; items.Add(FormatValueInline(item)); }
                string preview = string.Join(", ", items);
                if (col.Count > 5) preview += $", ... ({col.Count} total)";
                return $"[{preview}] ({type.Name})";
            }
            if (type.Namespace == "UnityEngine") return $"{obj} ({type.Name})";
            return $"{obj} ({type.Name})";
        }

        static string FormatStackTrace(StackTrace trace)
        {
            var sb = new StringBuilder();
            foreach (var frame in trace.GetFrames())
            {
                var method = frame.GetMethod();
                if (method == null) continue;
                string file = frame.GetFileName();
                int line = frame.GetFileLineNumber();
                string loc = file != null ? $" at {Path.GetFileName(file)}:{line}" : "";
                sb.AppendLine($"  {method.DeclaringType?.Name ?? "?"}.{method.Name}(){loc}");
            }
            return sb.ToString().TrimEnd();
        }

        static List<string> SplitStatements(string code)
        {
            var statements = new List<string>();
            var current = new StringBuilder();
            int braceDepth = 0;
            bool inString = false, inChar = false, escaped = false;

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
            if (!string.IsNullOrEmpty(remaining)) statements.Add(remaining);
            return statements;
        }

        static string EscapeForString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

        #endregion
    }
}
