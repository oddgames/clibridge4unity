using System;
using System.Collections.Generic;
using System.IO;

namespace clibridge4unity;

/// <summary>
/// Parses Unity's `csc.rsp` response files (Assets/csc.rsp + per-asmdef sibling rsp).
/// Captures `-define`, `-nullable`, `-langversion`, `-nowarn`, `-warnaserror` flags so the
/// LINT compile uses the same options as Unity does.
/// </summary>
internal static class LintCscRsp
{
    public sealed class Options
    {
        public List<string> Defines = new();
        public string Nullable;        // "enable" | "disable" | "warnings" | "annotations" | null
        public string LangVersion;     // "latest" | "preview" | "11" | etc | null
        public List<string> NoWarn = new();
        public bool TreatWarningsAsErrors;
        public bool AllowUnsafe;
    }

    /// <summary>Parse `path` if it exists; returns empty Options if missing or unreadable.</summary>
    public static Options Parse(string path)
    {
        var opts = new Options();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return opts;
        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                // Tokenize on whitespace; quoted strings preserved.
                foreach (var tok in Tokenize(line)) ApplyToken(tok, opts);
            }
        }
        catch { }
        return opts;
    }

    /// <summary>Merge per-asmdef rsp on top of project-wide one.</summary>
    public static Options Merge(Options projectWide, Options perAsmdef)
    {
        if (perAsmdef == null) return projectWide ?? new Options();
        if (projectWide == null) return perAsmdef;
        var merged = new Options
        {
            Defines = new List<string>(projectWide.Defines),
            Nullable = perAsmdef.Nullable ?? projectWide.Nullable,
            LangVersion = perAsmdef.LangVersion ?? projectWide.LangVersion,
            NoWarn = new List<string>(projectWide.NoWarn),
            TreatWarningsAsErrors = projectWide.TreatWarningsAsErrors || perAsmdef.TreatWarningsAsErrors,
            AllowUnsafe = projectWide.AllowUnsafe || perAsmdef.AllowUnsafe,
        };
        merged.Defines.AddRange(perAsmdef.Defines);
        merged.NoWarn.AddRange(perAsmdef.NoWarn);
        return merged;
    }

    static void ApplyToken(string tok, Options opts)
    {
        if (tok.Length == 0) return;
        // Normalize: strip leading - or /
        string body = tok;
        if (body.StartsWith("-") || body.StartsWith("/")) body = body.Substring(1);

        // Forms:  flag         | flag:value      | flag+        | flag-
        string flag, value = null;
        int colon = body.IndexOf(':');
        if (colon > 0) { flag = body.Substring(0, colon).ToLowerInvariant(); value = body.Substring(colon + 1); }
        else { flag = body.ToLowerInvariant(); }

        switch (flag)
        {
            case "define":
            case "d":
                if (!string.IsNullOrEmpty(value))
                    foreach (var d in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                        opts.Defines.Add(d.Trim());
                break;
            case "nullable":
                opts.Nullable = string.IsNullOrEmpty(value) ? "enable" : value.ToLowerInvariant();
                break;
            case "langversion":
                opts.LangVersion = value;
                break;
            case "nowarn":
                if (!string.IsNullOrEmpty(value))
                    foreach (var w in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                        opts.NoWarn.Add(NormalizeWarnId(w.Trim()));
                break;
            case "warnaserror":
            case "warnaserror+":
                opts.TreatWarningsAsErrors = true;
                break;
            case "warnaserror-":
                opts.TreatWarningsAsErrors = false;
                break;
            case "unsafe":
            case "unsafe+":
                opts.AllowUnsafe = true;
                break;
        }
    }

    static string NormalizeWarnId(string id) => id.StartsWith("CS", StringComparison.OrdinalIgnoreCase) ? id : "CS" + id;

    /// <summary>Whitespace tokenizer that respects double-quoted strings.</summary>
    static IEnumerable<string> Tokenize(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) yield break;
            int start = i;
            if (line[i] == '"')
            {
                i++;
                int innerStart = i;
                while (i < line.Length && line[i] != '"') i++;
                yield return line.Substring(innerStart, i - innerStart);
                if (i < line.Length) i++; // skip closing quote
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                yield return line.Substring(start, i - start);
            }
        }
    }
}
