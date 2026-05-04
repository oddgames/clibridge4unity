using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace clibridge4unity;

/// <summary>
/// Shared Roslyn-syntax analysis used by both the persistent daemon and the single-pass
/// fallback. Callers hand in pre-filtered dictionaries (files containing the query) plus
/// timing + corpus-size metadata, so this layer is pure syntax + formatting — no I/O.
///
/// Entry points:
///   - Analyze(...)       — top-level CODE_ANALYZE dispatch (handles prefix queries,
///                          dotted member zooms, plain type lookup, and the member-fallback
///                          for plain names that aren't types).
///   - SearchByKind(...)  — listing for kind-prefixed queries (method:/field:/...).
/// </summary>
internal static class CodeAnalysisCore
{
    /// <summary>Unified CODE_ANALYZE entry.</summary>
    public static string Analyze(
        IReadOnlyDictionary<string, SyntaxTree> trees,
        IReadOnlyDictionary<string, string> fileTexts,
        string projectPath,
        string query,
        long elapsedMs,
        int totalCorpusSize)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: No query. Usage: CODE_ANALYZE ClassName | ClassName.Member | method:Name | field:Name | inherits:Type | attribute:Name";

        query = query.Trim();

        // Prefix dispatch: method:/field:/property:/inherits:/attribute: → listing.
        // class:/type: → strip prefix and fall through to deep type analysis.
        int colonIdx = query.IndexOf(':');
        if (colonIdx > 0 && query.IndexOf(' ') < 0)
        {
            string prefix = query.Substring(0, colonIdx).ToLowerInvariant();
            string term = query.Substring(colonIdx + 1).Trim();
            switch (prefix)
            {
                case "class":
                case "type":
                    query = term;
                    break;
                case "method":
                case "field":
                case "property":
                case "inherits":
                case "extends":
                case "attribute":
                    return SearchByKind(trees, fileTexts, projectPath, prefix, term, elapsedMs, totalCorpusSize);
            }
        }

        // Support dotted form: `TypeName.Member` (or `Outer.Inner.Member`).
        // Split on the LAST dot so multi-segment type paths behave.
        string className = query;
        string memberName = null;
        int lastDot = query.LastIndexOf('.');
        if (lastDot > 0)
        {
            className = query.Substring(0, lastDot);
            memberName = query.Substring(lastDot + 1);
        }

        // When the class path itself is dotted (e.g. `Generator.Trucks`), identifier
        // matching against a type declaration should compare against the LAST segment
        // (the actual C# identifier — outer names aren't part of `td.Identifier.Text`).
        string classIdent = className.Contains('.')
            ? className.Substring(className.LastIndexOf('.') + 1)
            : className;

        // Strip generic type parameters and array brackets so `List<MyType>` and
        // `MyType[]` match the type's bare identifier.
        classIdent = StripGenericsAndArrays(classIdent);
        if (memberName != null) memberName = StripGenericsAndArrays(memberName);

        // Extraction buckets.
        var sourceFiles = new List<string>();
        var baseTypes = new List<string>();
        var derivedTypes = new List<string>();
        var fieldUsages = new List<string>();
        var paramUsages = new List<string>();
        var returnUsages = new List<string>();
        var getComponentUsages = new List<string>();
        var localVarUsages = new List<string>();
        // Members tracked as (identifier, formatted line) so member-zoom can filter by
        // exact identifier — not substring-match, which falsely hit every method that
        // merely USES the member name as a parameter type.
        var ownMethods = new List<(string name, string formatted)>();
        var ownFields = new List<(string name, string formatted)>();
        // Nested types inside the self-type (class/struct/interface/enum/record).
        // Used to detect cases like `Quality.ShadowQuality` where ShadowQuality is a nested enum.
        var nestedTypes = new List<(string name, string formatted, BaseTypeDeclarationSyntax node)>();
        var grepLines = new List<string>();

        // Grep term is the member (if dotted) else the class name.
        string grepTerm = memberName ?? className;

        foreach (var kvp in trees)
        {
            var root = kvp.Value.GetRoot();
            string rel = ToRelativePath(kvp.Key, projectPath);

            // Raw grep collection
            if (fileTexts.TryGetValue(kvp.Key, out var text))
            {
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(grepTerm))
                    {
                        string lt = lines[i].Trim();
                        if (lt.Length > 120) lt = lt.Substring(0, 120) + "...";
                        grepLines.Add($"{rel}:{i + 1}: {lt}");
                    }
                }
            }

            // Delegates live outside TypeDeclarationSyntax; check them first so a
            // `CODE_ANALYZE MyCallback` hit still populates sourceFiles.
            foreach (var dd in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                if (!dd.Identifier.Text.Equals(classIdent, StringComparison.OrdinalIgnoreCase)) continue;
                int line = dd.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var parms = string.Join(", ", dd.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                sourceFiles.Add($"{rel}:{line} ({dd.Modifiers} delegate {dd.ReturnType} {dd.Identifier}({parms}))");
            }

            // Enum *types* (matches `CODE_ANALYZE PlayerState`).
            foreach (var ed in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                if (!ed.Identifier.Text.Equals(classIdent, StringComparison.OrdinalIgnoreCase)) continue;
                int line = ed.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sourceFiles.Add($"{rel}:{line} ({ed.Modifiers} enum {ed.Identifier})");
                foreach (var em in ed.Members)
                {
                    int eml = em.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    ownFields.Add((em.Identifier.Text, $"{ed.Identifier}.{em.Identifier} — {rel}:{eml}"));
                }
            }

            foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                string enc = td.Identifier.Text;
                bool isSelf = enc.Equals(classIdent, StringComparison.OrdinalIgnoreCase);

                if (isSelf)
                {
                    int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var bases = td.BaseList?.Types.Select(t => t.Type.ToString()).ToArray() ?? Array.Empty<string>();

                    // Surface nested-type context: `Generator.Trucks` instead of bare `Trucks`.
                    var ancestors = td.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().ToList();
                    string qualified = ancestors.Count > 0
                        ? string.Join(".", ancestors.Select(a => a.Identifier.Text)) + "." + enc
                        : enc;
                    sourceFiles.Add($"{rel}:{line} ({td.Modifiers} {td.Keyword} {qualified}" + (bases.Length > 0 ? $" : {string.Join(", ", bases)}" : "") + ")");
                    baseTypes.AddRange(bases);

                    foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                    {
                        int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        ownMethods.Add((m.Identifier.Text, $"{m.Modifiers} {m.ReturnType} {m.Identifier.Text}({parms}) — {rel}:{mLine}"));
                    }
                    foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                        foreach (var v in f.Declaration.Variables)
                        {
                            int fl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            ownFields.Add((v.Identifier.Text, $"{f.Modifiers} {f.Declaration.Type} {v.Identifier} — {rel}:{fl}"));
                        }
                    foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    {
                        int pl = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        ownFields.Add((p.Identifier.Text, $"{p.Modifiers} {p.Type} {p.Identifier} {{ get; set; }} — {rel}:{pl}"));
                    }
                    foreach (var e in td.Members.OfType<EventFieldDeclarationSyntax>())
                        foreach (var v in e.Declaration.Variables)
                        {
                            int el = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            ownFields.Add((v.Identifier.Text, $"{e.Modifiers} event {e.Declaration.Type} {v.Identifier} — {rel}:{el}"));
                        }
                    // Constructors — match both `.ctor` (for direct lookup) and the type name
                    // itself so `CODE_ANALYZE Foo.Foo` or `Foo.ctor` both work.
                    foreach (var c in td.Members.OfType<ConstructorDeclarationSyntax>())
                    {
                        int cl = c.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", c.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        string formatted = $"{c.Modifiers} {c.Identifier.Text}({parms}) — {rel}:{cl}";
                        ownMethods.Add((c.Identifier.Text, formatted));
                        ownMethods.Add((".ctor", formatted));
                    }
                    // Indexers — `public T this[int i]`. Matched via "this" or "Item".
                    foreach (var idx in td.Members.OfType<IndexerDeclarationSyntax>())
                    {
                        int il = idx.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", idx.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        string formatted = $"{idx.Modifiers} {idx.Type} this[{parms}] — {rel}:{il}";
                        ownMethods.Add(("this", formatted));
                        ownMethods.Add(("Item", formatted));
                    }
                    // Operators — `operator +`, `operator implicit`, etc.
                    foreach (var op in td.Members.OfType<OperatorDeclarationSyntax>())
                    {
                        int ol = op.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", op.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        string opToken = op.OperatorToken.Text;
                        string formatted = $"{op.Modifiers} {op.ReturnType} operator {opToken}({parms}) — {rel}:{ol}";
                        ownMethods.Add(($"operator{opToken}", formatted));
                        ownMethods.Add(($"operator {opToken}", formatted));
                    }
                    foreach (var op in td.Members.OfType<ConversionOperatorDeclarationSyntax>())
                    {
                        int ol = op.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", op.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        string impl = op.ImplicitOrExplicitKeyword.Text;
                        string formatted = $"{op.Modifiers} {impl} operator {op.Type}({parms}) — {rel}:{ol}";
                        ownMethods.Add(($"operator {op.Type}", formatted));
                        ownMethods.Add(($"op_{impl}", formatted));
                    }
                    // Destructors — rare but cheap to include.
                    foreach (var d in td.Members.OfType<DestructorDeclarationSyntax>())
                    {
                        int dl = d.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        string formatted = $"{d.Modifiers} ~{d.Identifier}() — {rel}:{dl}";
                        ownMethods.Add(($"~{d.Identifier.Text}", formatted));
                        ownMethods.Add((".finalize", formatted));
                    }
                    // Collect nested types (class/struct/interface/record + enum) so member-zoom
                    // can detect cases like `Quality.ShadowQuality` where ShadowQuality is a nested enum.
                    foreach (var nested in td.Members.OfType<BaseTypeDeclarationSyntax>())
                    {
                        int nl = nested.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        string kind = nested switch
                        {
                            EnumDeclarationSyntax _ => "enum",
                            InterfaceDeclarationSyntax _ => "interface",
                            StructDeclarationSyntax _ => "struct",
                            RecordDeclarationSyntax _ => "record",
                            _ => "class"
                        };
                        nestedTypes.Add((nested.Identifier.Text, $"{nested.Modifiers} {kind} {nested.Identifier} — {rel}:{nl}", nested));
                    }
                    continue;
                }

                // Usages: derived types
                if (td.BaseList?.Types.Any(t => t.Type.ToString().Contains(className)) == true)
                {
                    int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    derivedTypes.Add($"{enc} — {rel}:{line}");
                }

                foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                    if (f.Declaration.Type.ToString().Contains(className))
                        foreach (var v in f.Declaration.Variables)
                        {
                            int fl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            fieldUsages.Add($"{enc}.{v.Identifier} — {rel}:{fl}");
                        }

                foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    if (p.Type.ToString().Contains(className))
                    {
                        int pl = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        fieldUsages.Add($"{enc}.{p.Identifier} (prop) — {rel}:{pl}");
                    }

                foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                {
                    bool hasParam = m.ParameterList.Parameters.Any(p => p.Type?.ToString().Contains(className) == true);
                    bool returnsIt = m.ReturnType.ToString().Contains(className);
                    if (hasParam)
                    {
                        int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        paramUsages.Add($"{enc}.{m.Identifier.Text}({parms}) — {rel}:{ml}");
                    }
                    if (returnsIt)
                    {
                        int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        returnUsages.Add($"{enc}.{m.Identifier.Text}() returns {m.ReturnType} — {rel}:{ml}");
                    }
                    if (m.Body != null)
                    {
                        string bt = m.Body.ToString();
                        if (bt.Contains($"GetComponent<{className}>") || bt.Contains($"GetComponentInChildren<{className}>"))
                        {
                            int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            getComponentUsages.Add($"{enc}.{m.Identifier.Text}() — {rel}:{ml}");
                        }
                    }
                }

                foreach (var ld in td.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                    if (ld.Declaration.Type.ToString().Contains(className))
                    {
                        var method = ld.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        string mn = method?.Identifier.Text ?? "?";
                        foreach (var v in ld.Declaration.Variables)
                        {
                            int vl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            localVarUsages.Add($"{enc}.{mn}() var {v.Identifier} — {rel}:{vl}");
                        }
                    }
            }
        }

        var sb = new StringBuilder();

        if (sourceFiles.Count == 0 && grepLines.Count == 0)
            return $"Error: '{query}' not found ({totalCorpusSize} files indexed, {elapsedMs}ms)";

        // --- Member zoom: query was `Foo.Bar` and we found type `Foo` ---
        if (memberName != null && sourceFiles.Count > 0)
        {
            sb.AppendLine($"=== {className}.{memberName} === ({elapsedMs}ms, {totalCorpusSize} indexed)");
            sb.AppendLine();

            // Is memberName a nested type inside Foo? (e.g. `Quality.ShadowQuality` → nested enum).
            var matchingNested = nestedTypes.Where(n => n.name.Equals(memberName, StringComparison.Ordinal)).ToList();
            // Members with EXACT identifier match — substring match falsely hits every
            // method that uses memberName as a parameter type.
            var matchingMethods = ownMethods.Where(m => m.name.Equals(memberName, StringComparison.Ordinal)).ToList();
            var matchingFields = ownFields.Where(f => f.name.Equals(memberName, StringComparison.Ordinal)).ToList();

            if (matchingNested.Count > 0)
            {
                sb.AppendLine($"Nested type:");
                foreach (var n in matchingNested) sb.AppendLine($"  {n.formatted}");
                // If it's an enum, list its values inline.
                foreach (var n in matchingNested.Where(n => n.node is EnumDeclarationSyntax))
                {
                    var ed = (EnumDeclarationSyntax)n.node;
                    if (ed.Members.Count > 0)
                    {
                        sb.AppendLine($"  values: {string.Join(", ", ed.Members.Select(em => em.Identifier.Text))}");
                    }
                }
            }
            if (matchingMethods.Count > 0) { sb.AppendLine("Methods:"); foreach (var m in matchingMethods) sb.AppendLine($"  {m.formatted}"); }
            if (matchingFields.Count > 0) { sb.AppendLine("Fields/Properties:"); foreach (var f in matchingFields) sb.AppendLine($"  {f.formatted}"); }
            if (matchingNested.Count == 0 && matchingMethods.Count == 0 && matchingFields.Count == 0)
                sb.AppendLine($"Member '{memberName}' not found in {className}");

            sb.AppendLine();
            sb.AppendLine($"{className} defined in:");
            foreach (var s in sourceFiles) sb.AppendLine($"  {s}");
        }
        // --- Deep type view ---
        else if (sourceFiles.Count > 0)
        {
            int matchedFiles = trees.Count;
            sb.AppendLine($"=== {className} === ({matchedFiles} files matched in {elapsedMs}ms, {totalCorpusSize} indexed)");
            sb.AppendLine();
            string partialNote = sourceFiles.Count > 1 ? $" (partial — split across {sourceFiles.Count} files)" : "";
            sb.AppendLine($"Defined in:{partialNote}");
            foreach (var s in sourceFiles) sb.AppendLine($"  {s}");
            if (baseTypes.Count > 0) sb.AppendLine($"Inherits from: {string.Join(", ", baseTypes.Distinct())}");
            if (derivedTypes.Count > 0) AppendCapped(sb, $"Inherited by ({derivedTypes.Count})", derivedTypes, 15);
            if (fieldUsages.Count > 0) AppendCapped(sb, $"Referenced as field/property ({fieldUsages.Count})", fieldUsages, 20);
            if (paramUsages.Count > 0) AppendCapped(sb, $"Passed as parameter ({paramUsages.Count})", paramUsages, 15);
            if (returnUsages.Count > 0) AppendCapped(sb, $"Returned by ({returnUsages.Count})", returnUsages, 10);
            if (getComponentUsages.Count > 0) AppendCapped(sb, $"GetComponent<{className}>() ({getComponentUsages.Count})", getComponentUsages, 10);
            if (localVarUsages.Count > 0) AppendCapped(sb, $"Local variables ({localVarUsages.Count})", localVarUsages, 10);
            if (ownMethods.Count > 0) AppendCapped(sb, $"Methods ({ownMethods.Count})", ownMethods.Select(m => m.formatted).ToList(), 25);
            if (ownFields.Count > 0) AppendCapped(sb, $"Fields/Properties ({ownFields.Count})", ownFields.Select(f => f.formatted).ToList(), 25);
            if (nestedTypes.Count > 0) AppendCapped(sb, $"Nested types ({nestedTypes.Count})", nestedTypes.Select(n => n.formatted).ToList(), 15);
        }
        // --- Not a type — member fallback + namespace fallback ---
        else
        {
            var memberDefs = FindMemberDeclarations(trees, projectPath, query);
            var namespaceHits = FindNamespaceContents(trees, projectPath, query);

            if (memberDefs.Count > 0)
            {
                sb.AppendLine($"'{query}' is not a type. Found {memberDefs.Count} member declaration(s) with that name:");
                foreach (var d in memberDefs.OrderBy(d => d).Take(20)) sb.AppendLine($"  {d}");
                if (memberDefs.Count > 20) sb.AppendLine($"  ... +{memberDefs.Count - 20} more");
            }
            else if (namespaceHits.Count > 0)
            {
                sb.AppendLine($"'{query}' is a namespace with {namespaceHits.Count} type(s):");
                foreach (var t in namespaceHits.OrderBy(t => t).Take(30)) sb.AppendLine($"  {t}");
                if (namespaceHits.Count > 30) sb.AppendLine($"  ... +{namespaceHits.Count - 30} more");
            }
            else
            {
                sb.AppendLine($"Type '{query}' not found as a declaration, but found in source:");
            }
        }

        // Grep tail. In member-zoom mode this IS the usages list, so call it that.
        // In deep-type mode the structured usages are already broken down above, so
        // this is just a supplementary raw-reference tail.
        var sortedGrep = grepLines.OrderBy(g => g).ToList();
        if (sortedGrep.Count > 0)
        {
            bool isMemberZoom = memberName != null && sourceFiles.Count > 0;
            int tailCap = sourceFiles.Count > 0 ? 40 : 20;
            sb.AppendLine();
            string heading = isMemberZoom
                ? $"Usages of '{memberName}' ({sortedGrep.Count} lines)"
                : $"Raw references ({sortedGrep.Count} lines)";
            sb.AppendLine($"--- {heading} ---");
            foreach (var g in sortedGrep.Take(tailCap)) sb.AppendLine(g);
            if (sortedGrep.Count > tailCap) sb.AppendLine($"... +{sortedGrep.Count - tailCap} more");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// List matches for a kind-prefixed query: method:Name / field:Name / property:Name /
    /// inherits:Type / attribute:Name. `trees` holds the pre-filtered (query-matching) files.
    /// </summary>
    public static string SearchByKind(
        IReadOnlyDictionary<string, SyntaxTree> trees,
        IReadOnlyDictionary<string, string> fileTexts,
        string projectPath,
        string kind,
        string term,
        long elapsedMs,
        int totalCorpusSize)
    {
        if (string.IsNullOrWhiteSpace(term))
            return $"Error: No term after '{kind}:'";

        var results = new List<string>();
        foreach (var kvp in trees)
        {
            var root = kvp.Value.GetRoot();
            string rel = ToRelativePath(kvp.Key, projectPath);

            switch (kind)
            {
                case "method":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                        foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                            if (m.Identifier.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                            {
                                int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                                results.Add($"{td.Identifier.Text}.{m.Identifier.Text}({parms}) : {m.ReturnType} — {rel}:{ml}");
                            }
                    break;

                case "field":
                case "property":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    {
                        foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                            foreach (var v in f.Declaration.Variables)
                                if (v.Identifier.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                                {
                                    int fl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                    results.Add($"{td.Identifier.Text}.{v.Identifier} : {f.Declaration.Type} — {rel}:{fl}");
                                }
                        foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                            if (p.Identifier.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                            {
                                int pl = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                results.Add($"{td.Identifier.Text}.{p.Identifier} : {p.Type} (prop) — {rel}:{pl}");
                            }
                    }
                    break;

                case "inherits":
                case "extends":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                        if (td.BaseList?.Types.Any(t => t.Type.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)) == true)
                        {
                            int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            var bases = td.BaseList.Types.Select(t => t.Type.ToString()).ToArray();
                            results.Add($"{td.Identifier.Text} : {string.Join(", ", bases)} — {rel}:{line}");
                        }
                    break;

                case "attribute":
                    foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
                        if (attr.Name.ToString().Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            int al = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            var parent = attr.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                            string pn = parent switch
                            {
                                MethodDeclarationSyntax m => m.Identifier.Text + "()",
                                TypeDeclarationSyntax t => t.Identifier.Text,
                                PropertyDeclarationSyntax p => p.Identifier.Text,
                                FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.Text,
                                _ => "?"
                            };
                            results.Add($"[{attr.Name}] on {pn} — {rel}:{al}");
                        }
                    break;
            }
        }

        results.Sort(StringComparer.Ordinal);
        if (results.Count == 0)
            return $"No matches for '{kind}:{term}' ({totalCorpusSize} files scanned in {elapsedMs}ms)";

        var sb = new StringBuilder();
        sb.AppendLine($"=== {kind}:{term} === ({results.Count} matches, {elapsedMs}ms)");
        sb.AppendLine();
        foreach (var r in results.Take(50)) sb.AppendLine(r);
        if (results.Count > 50) sb.AppendLine($"... +{results.Count - 50} more");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Walk every parsed type declaration and collect member declarations (property / field /
    /// method / event) whose identifier exactly matches <paramref name="name"/>. Used as a
    /// fallback when a plain query isn't a type — e.g. `CODE_ANALYZE PlayerTruck` where
    /// PlayerTruck is actually a static property on `Generator.Trucks`.
    /// </summary>
    static List<string> FindMemberDeclarations(
        IReadOnlyDictionary<string, SyntaxTree> trees,
        string projectPath,
        string name)
    {
        var results = new List<string>();
        foreach (var kvp in trees)
        {
            var root = kvp.Value.GetRoot();
            string rel = ToRelativePath(kvp.Key, projectPath);
            foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                string typeName = td.Identifier.Text;

                foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    if (p.Identifier.Text.Equals(name, StringComparison.Ordinal))
                    {
                        int pl = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        results.Add($"property  {typeName}.{p.Identifier} : {p.Type} — {rel}:{pl}");
                    }

                foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                    foreach (var v in f.Declaration.Variables)
                        if (v.Identifier.Text.Equals(name, StringComparison.Ordinal))
                        {
                            int fl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            results.Add($"field     {typeName}.{v.Identifier} : {f.Declaration.Type} — {rel}:{fl}");
                        }

                foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                    if (m.Identifier.Text.Equals(name, StringComparison.Ordinal))
                    {
                        int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", m.ParameterList.Parameters.Select(pp => $"{pp.Type} {pp.Identifier}"));
                        results.Add($"method    {typeName}.{m.Identifier.Text}({parms}) : {m.ReturnType} — {rel}:{ml}");
                    }

                foreach (var e in td.Members.OfType<EventFieldDeclarationSyntax>())
                    foreach (var v in e.Declaration.Variables)
                        if (v.Identifier.Text.Equals(name, StringComparison.Ordinal))
                        {
                            int el = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            results.Add($"event     {typeName}.{v.Identifier} : {e.Declaration.Type} — {rel}:{el}");
                        }

                // Constructors — matches both `Foo` and `.ctor` queries.
                if (name.Equals(typeName, StringComparison.Ordinal) || name.Equals(".ctor", StringComparison.Ordinal))
                {
                    foreach (var c in td.Members.OfType<ConstructorDeclarationSyntax>())
                    {
                        int cl = c.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", c.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        results.Add($"ctor      {typeName}.{c.Identifier.Text}({parms}) — {rel}:{cl}");
                    }
                }

                // Indexers — match "this" or "Item".
                if (name.Equals("this", StringComparison.Ordinal) || name.Equals("Item", StringComparison.Ordinal))
                {
                    foreach (var idx in td.Members.OfType<IndexerDeclarationSyntax>())
                    {
                        int il = idx.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", idx.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        results.Add($"indexer   {typeName}.this[{parms}] : {idx.Type} — {rel}:{il}");
                    }
                }

                // Operators — match `operator+` or `op_Addition`.
                foreach (var op in td.Members.OfType<OperatorDeclarationSyntax>())
                {
                    string token = op.OperatorToken.Text;
                    if (name.Equals($"operator{token}", StringComparison.Ordinal) ||
                        name.Equals($"operator {token}", StringComparison.Ordinal) ||
                        name.Equals($"op_{token}", StringComparison.Ordinal))
                    {
                        int ol = op.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", op.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        results.Add($"operator  {typeName}.operator {token}({parms}) : {op.ReturnType} — {rel}:{ol}");
                    }
                }
                foreach (var op in td.Members.OfType<ConversionOperatorDeclarationSyntax>())
                {
                    string impl = op.ImplicitOrExplicitKeyword.Text;
                    if (name.Equals($"operator {op.Type}", StringComparison.Ordinal) ||
                        name.Equals($"op_{impl}", StringComparison.Ordinal))
                    {
                        int ol = op.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", op.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        results.Add($"convop    {typeName}.{impl} operator {op.Type}({parms}) — {rel}:{ol}");
                    }
                }
            }

            // Enum members live on EnumDeclarationSyntax (not TypeDeclarationSyntax).
            foreach (var ed in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                string enumName = ed.Identifier.Text;
                foreach (var em in ed.Members)
                    if (em.Identifier.Text.Equals(name, StringComparison.Ordinal))
                    {
                        int eml = em.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        results.Add($"enum      {enumName}.{em.Identifier} — {rel}:{eml}");
                    }
            }

            // Using-alias targets — `using Tex = UnityEngine.Texture2D;` — let `CODE_ANALYZE Tex` hit.
            foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                if (u.Alias != null && u.Alias.Name.Identifier.Text.Equals(name, StringComparison.Ordinal))
                {
                    int ul = u.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add($"alias     using {u.Alias.Name.Identifier.Text} = {u.Name}; — {rel}:{ul}");
                }
        }
        return results;
    }

    /// <summary>
    /// Enumerate types declared directly inside a namespace whose name matches (or starts with) `query`.
    /// Used when `CODE_ANALYZE UnityEngine.UI` is typed — we can at least list the types living there.
    /// </summary>
    static List<string> FindNamespaceContents(
        IReadOnlyDictionary<string, SyntaxTree> trees,
        string projectPath,
        string query)
    {
        var results = new List<string>();
        var seen = new HashSet<string>();
        foreach (var kvp in trees)
        {
            var root = kvp.Value.GetRoot();
            string rel = ToRelativePath(kvp.Key, projectPath);

            IEnumerable<(string name, SyntaxNode node)> namespaces = root.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Select(n => (name: n.Name.ToString(), node: (SyntaxNode)n));

            foreach (var (nsName, nsNode) in namespaces)
            {
                if (!nsName.Equals(query, StringComparison.Ordinal) &&
                    !nsName.StartsWith(query + ".", StringComparison.Ordinal))
                    continue;

                foreach (var td in nsNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    // Only direct children of this namespace block — skip nested types.
                    if (td.Parent != nsNode) continue;
                    int tl = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    string kind = td switch
                    {
                        EnumDeclarationSyntax _ => "enum",
                        InterfaceDeclarationSyntax _ => "interface",
                        StructDeclarationSyntax _ => "struct",
                        RecordDeclarationSyntax _ => "record",
                        _ => "class"
                    };
                    string line = $"{td.Modifiers} {kind} {nsName}.{td.Identifier} — {rel}:{tl}";
                    if (seen.Add(line)) results.Add(line);
                }
            }
        }
        return results;
    }

    /// <summary>Fuzzy-rank declared type names across `allTrees` against `needle`.
    /// Used to add "Did you mean" suggestions to a not-found CODE_ANALYZE response.</summary>
    public static List<string> SuggestTypeNames(IReadOnlyDictionary<string, SyntaxTree> allTrees, string needle, int max = 5)
    {
        if (string.IsNullOrWhiteSpace(needle) || allTrees == null || allTrees.Count == 0)
            return new List<string>();

        // Strip dotted prefix + generics so suggestion matches the actual identifier.
        string ident = needle.Trim();
        int lastDot = ident.LastIndexOf('.');
        if (lastDot > 0) ident = ident.Substring(lastDot + 1);
        ident = StripGenericsAndArrays(ident);
        if (ident.Length == 0) return new List<string>();
        string needleLower = ident.ToLowerInvariant();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scored = new List<(int score, string name)>();
        foreach (var kvp in allTrees)
        {
            SyntaxNode root;
            try { root = kvp.Value.GetRoot(); } catch { continue; }
            foreach (var td in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                string name = td.Identifier.Text;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                int s = FuzzyMatch.Score(name, needleLower);
                if (s >= 30) scored.Add((s, name));
            }
            foreach (var dd in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                string name = dd.Identifier.Text;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                int s = FuzzyMatch.Score(name, needleLower);
                if (s >= 30) scored.Add((s, name));
            }
        }
        return scored.OrderByDescending(x => x.score).Take(max).Select(x => x.name).ToList();
    }

    /// <summary>Append "Did you mean:" lines to a not-found Analyze response. No-op if response
    /// isn't a not-found error or no suggestions found.</summary>
    public static string AppendSuggestionsIfMissing(string analyzeResponse, IReadOnlyDictionary<string, SyntaxTree> allTrees, string query)
    {
        if (string.IsNullOrEmpty(analyzeResponse) || !analyzeResponse.StartsWith("Error: '", StringComparison.Ordinal))
            return analyzeResponse;
        if (analyzeResponse.IndexOf("' not found", StringComparison.Ordinal) < 0)
            return analyzeResponse;
        var suggestions = SuggestTypeNames(allTrees, query);
        if (suggestions.Count == 0) return analyzeResponse;
        var sb = new StringBuilder(analyzeResponse);
        sb.AppendLine();
        sb.AppendLine("Did you mean:");
        foreach (var s in suggestions) sb.AppendLine($"  {s}");
        return sb.ToString().TrimEnd();
    }

    static string StripGenericsAndArrays(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int lt = s.IndexOf('<');
        if (lt > 0) s = s.Substring(0, lt);
        while (s.EndsWith("[]")) s = s.Substring(0, s.Length - 2);
        return s;
    }

    static void AppendCapped(StringBuilder sb, string heading, List<string> items, int cap)
    {
        sb.AppendLine($"{heading}:");
        foreach (var i in items.Take(cap)) sb.AppendLine($"  {i}");
        if (items.Count > cap) sb.AppendLine($"  ... +{items.Count - cap} more");
    }

    static string ToRelativePath(string file, string projectPath)
        => file.Replace(projectPath + "\\", "").Replace(projectPath + "/", "");
}
