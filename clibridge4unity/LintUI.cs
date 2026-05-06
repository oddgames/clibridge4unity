using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

namespace clibridge4unity;

/// <summary>
/// Offline UXML + USS lint. UXML uses System.Xml for well-formedness; USS uses Unity's bundled
/// ExCSS.Unity.dll loaded via reflection (no new dependency). Catches missing tags, mismatched
/// braces, unclosed strings, bad selectors, malformed property declarations.
///
/// Misses (need Unity validator via `LOG ui errors`): unknown VisualElement types, undefined
/// USS variables, invalid property values, missing referenced assets.
/// </summary>
internal static class LintUI
{
    public sealed class Issue
    {
        public string File;
        public int Line;
        public int Column;
        public string Code;
        public string Message;
    }

    public sealed class RunResult
    {
        public List<Issue> Issues = new();
        public int UxmlScanned;
        public int UssScanned;
        public long ElapsedMs;
        public string Note;
    }

    /// <summary>Lint every *.uxml + *.uss under Assets/ (skips Library/PackageCache).</summary>
    public static RunResult Run(string projectPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new RunResult();
        string assetsDir = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsDir)) { result.Note = "Assets/ not found"; return result; }

        var uxml = Directory.EnumerateFiles(assetsDir, "*.uxml", SearchOption.AllDirectories).ToList();
        var uss = Directory.EnumerateFiles(assetsDir, "*.uss", SearchOption.AllDirectories).ToList();
        result.UxmlScanned = uxml.Count;
        result.UssScanned = uss.Count;

        // UXML — XML well-formedness via System.Xml. Cheap, no deps.
        foreach (var file in uxml) LintUxml(file, result.Issues);

        // USS — load Unity's bundled ExCSS.Unity.dll via reflection. Best-effort: if not found,
        // fall back to a hand-rolled brace/quote balance check.
        var excssParserType = TryLoadExCSSParser();
        foreach (var file in uss) LintUss(file, excssParserType, result.Issues);

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    static void LintUxml(string file, List<Issue> issues)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, ValidationType = ValidationType.None };
            using var reader = XmlReader.Create(file, settings);
            while (reader.Read()) { /* drain */ }
        }
        catch (XmlException ex)
        {
            issues.Add(new Issue
            {
                File = file,
                Line = ex.LineNumber,
                Column = ex.LinePosition,
                Code = "UXML001",
                Message = ex.Message
            });
        }
        catch (Exception ex)
        {
            issues.Add(new Issue
            {
                File = file, Line = 1, Column = 1,
                Code = "UXML999",
                Message = $"{ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    /// <summary>Locate Unity's ExCSS.Unity.dll Parser class. Returns null if unavailable.</summary>
    static Type TryLoadExCSSParser()
    {
        try
        {
            var (_, _, _, editorRoot, _, error) = LintSemantic.Resolve(Directory.GetCurrentDirectory());
            if (error != null || editorRoot == null) return null;
            string excssPath = Path.Combine(editorRoot, "Data", "Managed", "ExCSS.Unity.dll");
            if (!File.Exists(excssPath)) return null;
            var asm = Assembly.LoadFrom(excssPath);
            // ExCSS exposes `Parser` for stylesheet parsing.
            return asm.GetType("ExCSS.Parser") ?? asm.GetType("ExCSS.StylesheetParser");
        }
        catch { return null; }
    }

    static void LintUss(string file, Type parserType, List<Issue> issues)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex)
        {
            issues.Add(new Issue { File = file, Line = 1, Column = 1, Code = "USS999", Message = $"read failed: {ex.Message}" });
            return;
        }

        // Always run cheap brace/quote balance check — catches the common "missing }" / "unclosed string".
        var balanceIssues = CheckBraceAndQuoteBalance(file, text);
        if (balanceIssues.Count > 0) { issues.AddRange(balanceIssues); return; }

        // ExCSS deeper parse if available.
        if (parserType == null) return;
        try
        {
            object parser = Activator.CreateInstance(parserType);
            // Method signatures vary across ExCSS versions — try common ones.
            var parseMethod = parserType.GetMethod("Parse", new[] { typeof(string) });
            if (parseMethod == null) return;
            var stylesheet = parseMethod.Invoke(parser, new object[] { text });
            // Stylesheet has `Errors` collection.
            var errorsProp = stylesheet?.GetType().GetProperty("Errors");
            if (errorsProp == null) return;
            var errorsObj = errorsProp.GetValue(stylesheet);
            if (errorsObj is System.Collections.IEnumerable errors)
            {
                foreach (var err in errors)
                {
                    if (err == null) continue;
                    var msgProp = err.GetType().GetProperty("Message") ?? err.GetType().GetProperty("Description");
                    var lineProp = err.GetType().GetProperty("Line");
                    var colProp = err.GetType().GetProperty("Column");
                    int line = (lineProp?.GetValue(err) as int?) ?? 1;
                    int col = (colProp?.GetValue(err) as int?) ?? 1;
                    string msg = msgProp?.GetValue(err)?.ToString() ?? err.ToString();
                    issues.Add(new Issue { File = file, Line = line, Column = col, Code = "USS001", Message = msg });
                }
            }
        }
        catch { /* ExCSS layout differs across Unity versions — silent fallback is OK */ }
    }

    /// <summary>Cheap brace/quote balance check. Walks USS counting `{`/`}` (skipping strings + comments)
    /// and tracks last unclosed open-brace line for actionable error reporting.</summary>
    static List<Issue> CheckBraceAndQuoteBalance(string file, string text)
    {
        var issues = new List<Issue>();
        int line = 1, col = 1;
        int braceDepth = 0;
        int lastOpenBraceLine = 0;
        bool inString = false; char stringChar = '"';
        bool inLineComment = false, inBlockComment = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '\n') { line++; col = 1; if (inLineComment) inLineComment = false; continue; }
            col++;

            if (inLineComment) continue;
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == stringChar) inString = false;
                continue;
            }

            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '"' || c == '\'') { inString = true; stringChar = c; continue; }
            if (c == '{') { braceDepth++; lastOpenBraceLine = line; continue; }
            if (c == '}')
            {
                braceDepth--;
                if (braceDepth < 0)
                {
                    issues.Add(new Issue { File = file, Line = line, Column = col,
                        Code = "USS002", Message = "Unexpected '}' — no matching '{'." });
                    return issues;
                }
            }
        }
        if (inString)
            issues.Add(new Issue { File = file, Line = line, Column = col,
                Code = "USS003", Message = $"Unclosed string (started with {stringChar})." });
        if (inBlockComment)
            issues.Add(new Issue { File = file, Line = line, Column = col,
                Code = "USS004", Message = "Unclosed block comment '/* ... */'." });
        if (braceDepth > 0)
            issues.Add(new Issue { File = file, Line = lastOpenBraceLine, Column = 1,
                Code = "USS005", Message = $"{braceDepth} unclosed '{{' — missing closing brace." });
        return issues;
    }

    public static string Format(RunResult run, string projectPath)
    {
        var sb = new StringBuilder();
        sb.Append($"UI: {run.UxmlScanned} uxml, {run.UssScanned} uss, {run.Issues.Count} issue(s) ({run.ElapsedMs}ms)");
        if (!string.IsNullOrEmpty(run.Note)) sb.Append($" [{run.Note}]");
        sb.AppendLine();
        if (run.Issues.Count == 0)
        {
            sb.Append("OK — no UXML/USS syntax issues.");
            return sb.ToString();
        }
        foreach (var iss in run.Issues.OrderBy(i => i.File).ThenBy(i => i.Line))
        {
            string rel = iss.File.Replace(projectPath + "\\", "").Replace(projectPath + "/", "");
            sb.AppendLine($"{rel}:{iss.Line}:{iss.Column}: ERROR {iss.Code}: {iss.Message}");
        }
        return sb.ToString().TrimEnd();
    }
}
