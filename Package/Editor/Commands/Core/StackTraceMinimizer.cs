using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Minimizes stack traces for AI consumption - strips internal/framework frames,
    /// substitutes known paths with keywords, produces concise output.
    /// </summary>
    public static class StackTraceMinimizer
    {
        private static readonly string[] InternalPrefixes =
        {
            "UnityEngine.", "UnityEditor.", "System.", "Microsoft.", "Mono.",
            "TMPro.", "UnityEngineInternal.", "Unity.Collections.", "Unity.Jobs.",
            "Unity.Entities.", "Unity.Burst.", "Unity.Mathematics.", "Unity.IL2CPP.",
            "clibridge4unity.CommandRegistry", "clibridge4unity.BridgeServer",
            "clibridge4unity.LogCommands",
        };

        // .NET: "in path:line" at end of frame
        private static readonly Regex DotNetInRegex = new Regex(
            @"\bin\s+(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>\d+)", RegexOptions.Compiled);

        // Unity: Type:Method(params) (at path:line)
        private static readonly Regex UnityFrameRegex = new Regex(
            @"^(?<method>[\w\.]+:\w+)\s*\([^)]*\)\s*(?:\(at\s+(?<file>[^:]+):(?<line>\d+)\))?",
            RegexOptions.Compiled);

        // Rethrow/wrapper patterns Unity uses
        private static readonly Regex RethrowRegex = new Regex(
            @"^Rethrow as (\w+Exception):", RegexOptions.Compiled);

        // Known path roots (lazy-initialized from Unity APIs)
        private static string _projectRoot;
        private static string _unityDataRoot;
        private static string _packageCacheRoot;
        private static bool _pathsInitialized;

        private static void EnsurePaths()
        {
            if (_pathsInitialized) return;
            try
            {
                _projectRoot = Application.dataPath.Replace("/Assets", "").Replace('\\', '/');
                _unityDataRoot = UnityEditor.EditorApplication.applicationContentsPath?.Replace('\\', '/');
                _packageCacheRoot = _projectRoot + "/Library/PackageCache";
            }
            catch { }
            _pathsInitialized = true;
        }

        /// <summary>
        /// Replaces known absolute paths in arbitrary text with short tokens.
        /// Project root → $WORKSPACE, Unity install → $UNITY.
        /// </summary>
        public static string ShortenPaths(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            EnsurePaths();

            if (_projectRoot != null)
            {
                string winRoot = _projectRoot.Replace('/', '\\');
                text = text.Replace(winRoot + "\\", "$WORKSPACE\\");
                text = text.Replace(_projectRoot + "/", "$WORKSPACE/");
                text = text.Replace(winRoot, "$WORKSPACE");
                text = text.Replace(_projectRoot, "$WORKSPACE");
            }
            if (_unityDataRoot != null)
            {
                string winRoot = _unityDataRoot.Replace('/', '\\');
                text = text.Replace(winRoot + "\\", "$UNITY\\");
                text = text.Replace(_unityDataRoot + "/", "$UNITY/");
                text = text.Replace(winRoot, "$UNITY");
                text = text.Replace(_unityDataRoot, "$UNITY");
            }
            return text;
        }

        /// <summary>
        /// Returns legend header defining path variables. Include once at top of output.
        /// </summary>
        public static string GetPathLegend()
        {
            EnsurePaths();
            if (_projectRoot == null) return null;
            return $"$WORKSPACE={_projectRoot.Replace('/', '\\')}";
        }

        /// <summary>
        /// Minimizes a stack trace: strips internal frames, substitutes known paths,
        /// keeps only project-relevant frames in concise format.
        /// </summary>
        public static string Minimize(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return "";

            EnsurePaths();
            var lines = stackTrace.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            int internalCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Preserve exception messages and rethrow markers
                if (i == 0 && !IsFrameLine(trimmed))
                {
                    sb.AppendLine(trimmed);
                    continue;
                }

                // "Rethrow as FooException: message"
                if (RethrowRegex.IsMatch(trimmed))
                {
                    sb.AppendLine(trimmed);
                    continue;
                }

                // "(wrapper ...)" lines - skip
                if (trimmed.StartsWith("(wrapper"))
                {
                    internalCount++;
                    continue;
                }

                // "--- End of stack trace ..." dividers - skip
                if (trimmed.StartsWith("---"))
                    continue;

                if (IsInternalLine(trimmed))
                {
                    internalCount++;
                    continue;
                }

                var parsed = ParseFrame(trimmed);
                if (parsed == null) continue;

                var (method, file, lineNum) = parsed.Value;
                string shortMethod = ShortenMethod(method);

                if (!string.IsNullOrEmpty(file) && lineNum > 0)
                    sb.AppendLine($"  {shortMethod}() {SubstitutePath(file)}:{lineNum}");
                else
                    sb.AppendLine($"  {shortMethod}()");
            }

            if (internalCount > 0)
                sb.AppendLine($"  [{internalCount} internal]");

            return sb.ToString().TrimEnd();
        }

        [BridgeCommand("STACK_MINIMIZE", "Minimize a stack trace for AI (strips internal frames, shortens paths)",
            Category = "Core",
            Usage = "STACK_MINIMIZE <stack trace text>")]
        public static string MinimizeCommand(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Response.Error("Provide a stack trace to minimize");

            string result = Minimize(data);
            return string.IsNullOrEmpty(result) ? "No frames found" : result;
        }

        private static bool IsFrameLine(string line)
        {
            return line.StartsWith("at ") || line.Contains(" at ") || UnityFrameRegex.IsMatch(line);
        }

        private static bool IsInternalLine(string line)
        {
            string qualified;
            if (line.StartsWith("at "))
                qualified = line.Substring(3).TrimStart();
            else if (line.Contains(" at "))
                qualified = line.Substring(line.IndexOf(" at ") + 4).TrimStart();
            else
                qualified = line;

            foreach (var prefix in InternalPrefixes)
            {
                if (qualified.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static (string method, string file, int line)? ParseFrame(string frameLine)
        {
            // .NET format: at Namespace.Type.Method (params) [offset] in path:line
            if (frameLine.StartsWith("at ") || frameLine.Contains(" at "))
            {
                int atIdx = frameLine.IndexOf("at ");
                string rest = frameLine.Substring(atIdx + 3).TrimStart();

                int endIdx = rest.IndexOfAny(new[] { ' ', '(' });
                string method = endIdx >= 0 ? rest.Substring(0, endIdx).Trim() : rest.Trim();

                var inMatch = DotNetInRegex.Match(rest);
                string file = inMatch.Success ? inMatch.Groups["file"].Value.Trim() : null;
                int line = 0;
                if (inMatch.Success) int.TryParse(inMatch.Groups["line"].Value, out line);

                return (method, file, line);
            }

            // Unity format: Type:Method(params) (at path:line)
            var unityMatch = UnityFrameRegex.Match(frameLine);
            if (unityMatch.Success)
            {
                string method = unityMatch.Groups["method"].Value.Replace(':', '.');
                string file = unityMatch.Groups["file"].Success ? unityMatch.Groups["file"].Value : null;
                int line = 0;
                if (unityMatch.Groups["line"].Success)
                    int.TryParse(unityMatch.Groups["line"].Value, out line);
                return (method, file, line);
            }

            return null;
        }

        private static string ShortenMethod(string fullMethod)
        {
            if (string.IsNullOrEmpty(fullMethod)) return fullMethod;

            // Remove generic backtick: Dictionary`2 -> Dictionary
            fullMethod = Regex.Replace(fullMethod, @"`\d+", "");
            // Remove generic bracket params: Dictionary[TKey,TValue] -> Dictionary
            fullMethod = Regex.Replace(fullMethod, @"\[.*?\]", "");

            // Keep last two segments: Namespace.SubNs.Type.Method -> Type.Method
            var parts = fullMethod.Split('.');
            if (parts.Length > 2)
                return parts[parts.Length - 2] + "." + parts[parts.Length - 1];

            return fullMethod;
        }

        /// <summary>
        /// Substitutes known absolute paths with short keywords.
        /// Project Assets → Assets/..., Package cache → [pkgname]/..., Unity install → [Unity]/...
        /// </summary>
        private static string SubstitutePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace('\\', '/').Trim();

            // Project Assets: C:/Users/.../MyProject/Assets/Scripts/Foo.cs → Assets/Scripts/Foo.cs
            if (_projectRoot != null)
            {
                string assetsPrefix = _projectRoot + "/Assets/";
                if (path.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                    return "Assets/" + path.Substring(assetsPrefix.Length);

                // Package cache: .../Library/PackageCache/com.unity.ugui@1.0/Runtime/... → [ugui]/Runtime/...
                if (_packageCacheRoot != null && path.StartsWith(_packageCacheRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = path.Substring(_packageCacheRoot.Length).TrimStart('/');
                    int slashIdx = rest.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string pkgDir = rest.Substring(0, slashIdx);
                        string pkgRest = rest.Substring(slashIdx);
                        return $"[{ShortenPackageName(pkgDir)}]{pkgRest}";
                    }
                }

                // Project Packages/ folder
                string packagesPrefix = _projectRoot + "/Packages/";
                if (path.StartsWith(packagesPrefix, StringComparison.OrdinalIgnoreCase))
                    return "Packages/" + path.Substring(packagesPrefix.Length);
            }

            // Unity install: .../Editor/Data/... → [Unity]/... or [pkgname]/... for built-in packages
            if (_unityDataRoot != null && path.StartsWith(_unityDataRoot, StringComparison.OrdinalIgnoreCase))
            {
                string rest = path.Substring(_unityDataRoot.Length);

                // Built-in packages: .../BuiltInPackages/com.unity.ugui/Runtime/... → [ugui]/Runtime/...
                int builtInIdx = rest.IndexOf("/BuiltInPackages/", StringComparison.OrdinalIgnoreCase);
                if (builtInIdx >= 0)
                {
                    string afterBuiltIn = rest.Substring(builtInIdx + "/BuiltInPackages/".Length);
                    int slashIdx = afterBuiltIn.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string pkgName = afterBuiltIn.Substring(0, slashIdx);
                        string pkgRest = afterBuiltIn.Substring(slashIdx);
                        return $"[{ShortenPackageName(pkgName)}]{pkgRest}";
                    }
                }

                return "[Unity]" + rest;
            }

            // Generic fallback: look for /Assets/ or /Packages/ markers anywhere in path
            int idx = path.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return path.Substring(idx + 1);

            idx = path.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return path.Substring(idx + 1);

            // Last resort: filename only
            int lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
        }

        /// <summary>
        /// com.unity.ugui@1.0.0 → ugui, com.unity.textmeshpro → textmeshpro
        /// </summary>
        private static string ShortenPackageName(string pkgDirName)
        {
            int atIdx = pkgDirName.IndexOf('@');
            string pkgId = atIdx > 0 ? pkgDirName.Substring(0, atIdx) : pkgDirName;
            if (pkgId.StartsWith("com.unity.")) return pkgId.Substring(10);
            if (pkgId.StartsWith("com.")) return pkgId.Substring(4);
            return pkgId;
        }
    }
}
