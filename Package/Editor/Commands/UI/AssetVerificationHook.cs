using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Hook-based verification system for UI Toolkit and shader assets.
    /// When USS, UXML, SHADER, or CGINC files are edited, this system
    /// force-reimports them and checks the console for errors.
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    public static class AssetVerificationHook
    {
        // Extension → verification category
        private static readonly Dictionary<string, AssetType> _extensionMap = new Dictionary<string, AssetType>(StringComparer.OrdinalIgnoreCase)
        {
            { ".uxml", AssetType.UIToolkit },
            { ".uss", AssetType.UIToolkit },
            { ".tss", AssetType.UIToolkit },
            { ".shader", AssetType.Shader },
            { ".cg", AssetType.Shader },
            { ".cginc", AssetType.Shader },
            { ".hlsl", AssetType.Shader },
        };

        // Regexes for parsing validation errors from Console
        private static readonly Regex _uiToolkitDiagnosticRegex = new Regex(
            @"(?<path>(?:Assets|Packages)/[^\r\n:]*?\.(?:uss|uxml|tss))\s*(?:\((?:line|Line)\s*(?<line>\d+)\))?\s*:\s*(?<severity>error|warning)\s*:\s*(?<message>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _shaderErrorRegex = new Regex(
            @"(?<path>(?:Assets|Packages)/[^\r\n:]*?\.(?:shader|cg|cginc|hlsl))\s*(?:\((?:line|Line)\s*(?<line>\d+)\))?\s*:\s*(?<severity>error|warning)\s*:\s*(?<message>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Shader compiler error patterns (GPU programs, semantic errors, etc.)
        private static readonly Regex _shaderCompileErrorRegex = new Regex(
            @"(?<path>(?:Assets|Packages)/[^\s\r\n]+?\.(?:shader|cg|cginc|hlsl))[^:]*?(?<message>error|warning|invalid|Syntax error|unknown identifier|undeclared identifier|mismatched|Ambiguous|failed to compile)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private enum AssetType { UIToolkit, Shader }

        /// <summary>
        /// Result of a verification check on an asset.
        /// </summary>
        public class VerificationResult
        {
            public string AssetPath { get; set; }
            public bool IsValid { get; set; }
            public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
            public List<ValidationError> Warnings { get; set; } = new List<ValidationError>();
            public int TotalErrors => Errors.Count;
            public int TotalWarnings => Warnings.Count;

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Verification: {Path.GetFileName(AssetPath)}");
                sb.AppendLine($"Valid: {IsValid}");
                if (Errors.Count > 0)
                {
                    sb.AppendLine($"Errors ({Errors.Count}):");
                    foreach (var e in Errors)
                        sb.AppendLine($"  {e}");
                }
                if (Warnings.Count > 0)
                {
                    sb.AppendLine($"Warnings ({Warnings.Count}):");
                    foreach (var w in Warnings)
                        sb.AppendLine($"  {w}");
                }
                return sb.ToString().TrimEnd();
            }
        }

        public class ValidationError
        {
            public string Path { get; set; }
            public int Line { get; set; }
            public string Severity { get; set; }
            public string Message { get; set; }

            public bool IsError => string.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase);

            public override string ToString()
            {
                string line = Line > 0 ? $":{Line}" : "";
                return $"{Path}{line}: {Severity}: {Message}";
            }
        }

        static AssetVerificationHook()
        {
            EditorApplication.update += InitOnFirstTick;
        }

        private static void InitOnFirstTick()
        {
            EditorApplication.update -= InitOnFirstTick;
            RegisterHooks();
        }

        private static void RegisterHooks()
        {
            CommandRegistry.VerifyAsset = VerifyAsset;
            CommandRegistry.VerifyAssets = VerifyAssets;
            BridgeDiagnostics.Log("AssetVerificationHook", "hooks registered");
        }

        /// <summary>
        /// Verifies a single asset by force-reimporting and checking console for errors.
        /// Returns a VerificationResult with any validation errors/warnings found.
        /// </summary>
        public static VerificationResult VerifyAsset(string assetPath)
        {
            var result = new VerificationResult { AssetPath = assetPath };

            if (string.IsNullOrEmpty(assetPath))
                return result;

            string ext = Path.GetExtension(assetPath);
            if (!_extensionMap.TryGetValue(ext, out var assetType))
            {
                result.IsValid = true; // Unknown type, assume valid
                return result;
            }

            // Check if we can verify right now (not mid-compile)
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                result.Warnings.Add(new ValidationError
                {
                    Path = assetPath,
                    Message = "Cannot verify while Unity is compiling/updating",
                    Severity = "warning"
                });
                result.IsValid = true; // Optimistic - may be valid
                return result;
            }

            // Get log ID before import to detect only new errors
            long beforeLogId = LogCommands.GetLastLogId();

            // Force-reimport the asset and its dependencies
            try
            {
                ImportAssetAndDependencies(assetPath, assetType);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError
                {
                    Path = assetPath,
                    Message = $"Import failed: {ex.Message}",
                    Severity = "error"
                });
                return result;
            }

            // Wait briefly for import to complete and errors to appear in console
            WaitForImportCompletion(2);

            // Check console for new errors
            var newErrors = GetNewValidationErrors(beforeLogId, assetType, assetPath);

            foreach (var error in newErrors)
            {
                if (error.IsError)
                    result.Errors.Add(error);
                else
                    result.Warnings.Add(error);
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Verifies multiple assets, returning results for each.
        /// </summary>
        public static List<VerificationResult> VerifyAssets(IEnumerable<string> assetPaths)
        {
            var results = new List<VerificationResult>();
            foreach (var path in assetPaths)
            {
                if (!string.IsNullOrEmpty(path))
                    results.Add(VerifyAsset(path));
            }
            return results;
        }

        private static void ImportAssetAndDependencies(string assetPath, AssetType assetType)
        {
            var opts = ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive;

            // Import the main asset
            AssetDatabase.ImportAsset(assetPath, opts);

            // For UI Toolkit, also import any USS dependencies
            if (assetType == AssetType.UIToolkit)
            {
                var deps = AssetDatabase.GetDependencies(assetPath, recursive: true);
                foreach (var dep in deps)
                {
                    if (dep == assetPath) continue;
                    string depExt = Path.GetExtension(dep);
                    if (_extensionMap.ContainsKey(depExt) || depExt == ".uss" || depExt == ".tss" || depExt == ".uxml")
                    {
                        AssetDatabase.ImportAsset(dep, opts);
                    }
                }
            }
            // For shaders, import all shader dependencies
            else if (assetType == AssetType.Shader)
            {
                var deps = AssetDatabase.GetDependencies(assetPath, recursive: true);
                foreach (var dep in deps)
                {
                    if (dep == assetPath) continue;
                    string depExt = Path.GetExtension(dep);
                    if (depExt == ".shader" || depExt == ".cg" || depExt == ".cginc" || depExt == ".hlsl")
                    {
                        AssetDatabase.ImportAsset(dep, opts);
                    }
                }
            }
        }

        private static void WaitForImportCompletion(int maxSeconds)
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < maxSeconds)
            {
                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                if (!busy) break;
                System.Threading.Thread.Sleep(50);
            }
        }

        private static List<ValidationError> GetNewValidationErrors(long sinceLogId, AssetType assetType, string assetPath)
        {
            var errors = new List<ValidationError>();
            var normalizedPath = assetPath.Replace('\\', '/');

            try
            {
                var logEntries = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntries == null || logEntryType == null) return errors;

                var flags = BindingFlags.Static | BindingFlags.Public;
                var startGetting = logEntries.GetMethod("StartGettingEntries", flags);
                var endGetting = logEntries.GetMethod("EndGettingEntries", flags);
                var getEntry = logEntries.GetMethod("GetEntryInternal", flags);
                var getCount = logEntries.GetMethod("GetCount", flags);
                var messageField = logEntryType.GetField("message");
                var modeField = logEntryType.GetField("mode");
                var timestampField = logEntryType.GetField("timestamp");

                if (startGetting == null || getCount == null || getEntry == null || messageField == null)
                    return errors;

                startGetting.Invoke(null, null);
                try
                {
                    int count = (int)getCount.Invoke(null, null);
                    for (int i = count - 1; i >= 0 && errors.Count < 50; i--)
                    {
                        var entry = Activator.CreateInstance(logEntryType);
                        if (!(bool)getEntry.Invoke(null, new object[] { i, entry })) continue;

                        string message = messageField.GetValue(entry)?.ToString();
                        if (string.IsNullOrWhiteSpace(message)) continue;

                        // Check if this entry is related to our asset
                        string entryLower = message.ToLowerInvariant();
                        string assetNameLower = Path.GetFileName(assetPath).ToLowerInvariant();

                        // Skip entries that don't reference our asset or file type
                        bool isRelevant = false;
                        if (assetType == AssetType.UIToolkit)
                        {
                            if (entryLower.Contains(".uxml") || entryLower.Contains(".uss") || entryLower.Contains(".tss"))
                                isRelevant = true;
                        }
                        else if (assetType == AssetType.Shader)
                        {
                            if (entryLower.Contains(".shader") || entryLower.Contains(".cg") ||
                                entryLower.Contains(".cginc") || entryLower.Contains(".hlsl"))
                                isRelevant = true;
                        }

                        if (!isRelevant) continue;

                        // Parse errors from the message
                        var parsedErrors = ParseErrorsFromMessage(message, assetType, normalizedPath);
                        foreach (var err in parsedErrors)
                        {
                            // Only include if it's related to our asset
                            if (string.IsNullOrEmpty(err.Path) ||
                                err.Path.Contains(Path.GetFileName(assetPath), StringComparison.OrdinalIgnoreCase) ||
                                normalizedPath.Contains(Path.GetFileName(err.Path), StringComparison.OrdinalIgnoreCase))
                            {
                                errors.Add(err);
                            }
                        }
                    }
                }
                finally
                {
                    endGetting?.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("AssetVerificationHook.GetNewValidationErrors", ex);
            }

            return errors;
        }

        private static List<ValidationError> ParseErrorsFromMessage(string message, AssetType assetType, string targetPath)
        {
            var errors = new List<ValidationError>();
            var regex = assetType == AssetType.UIToolkit ? _uiToolkitDiagnosticRegex : _shaderErrorRegex;

            // Standard diagnostic format: path(line): severity: message
            foreach (Match match in regex.Matches(message))
            {
                errors.Add(new ValidationError
                {
                    Path = match.Groups["path"].Value,
                    Line = int.TryParse(match.Groups["line"].Value, out var line) ? line : 0,
                    Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                    Message = match.Groups["message"].Value.Trim()
                });
            }

            // For shaders, also check for compiler error patterns
            if (assetType == AssetType.Shader && errors.Count == 0)
            {
                foreach (Match match in _shaderCompileErrorRegex.Matches(message))
                {
                    errors.Add(new ValidationError
                    {
                        Path = match.Groups["path"].Value,
                        Line = 0,
                        Severity = match.Value.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error" : "warning",
                        Message = match.Groups["message"].Value.Trim()
                    });
                }
            }

            return errors;
        }

        /// <summary>
        /// Formats a verification result as a string for command responses.
        /// </summary>
        public static string FormatVerificationResult(VerificationResult result)
        {
            if (result.IsValid && result.Warnings.Count == 0)
                return null; // No issues to report

            var sb = new StringBuilder();

            if (result.TotalErrors > 0)
            {
                sb.AppendLine($"--- Asset Validation: {result.AssetPath} ---");
                sb.AppendLine($"Errors: {result.TotalErrors}");
                if (result.TotalWarnings > 0)
                    sb.AppendLine($"Warnings: {result.TotalWarnings}");
                sb.AppendLine("---");
                foreach (var e in result.Errors)
                    sb.AppendLine(FormatError(e));
                if (result.Warnings.Count > 0)
                {
                    sb.AppendLine("Warnings:");
                    foreach (var w in result.Warnings)
                        sb.AppendLine(FormatError(w));
                }
            }
            else if (result.Warnings.Count > 0)
            {
                sb.AppendLine($"--- Asset Validation: {result.AssetPath} ---");
                sb.AppendLine($"Warnings: {result.TotalWarnings}");
                sb.AppendLine("---");
                foreach (var w in result.Warnings)
                    sb.AppendLine(FormatError(w));
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatError(ValidationError error)
        {
            string line = error.Line > 0 ? $":{error.Line}" : "";
            string path = Path.GetFileName(error.Path);
            return $"  {path}{line}: {error.Message}";
        }

        /// <summary>
        /// Quick check if a file extension is supported for verification.
        /// </summary>
        public static bool IsVerifiableExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path);
            return _extensionMap.ContainsKey(ext);
        }

        /// <summary>
        /// Returns all verifiable extensions.
        /// </summary>
        public static IEnumerable<string> GetVerifiableExtensions()
        {
            return _extensionMap.Keys;
        }
    }
}