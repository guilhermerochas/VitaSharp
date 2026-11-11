using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

// build/format-check.ps1 / build/format-check.sh: the naming rules of .editorconfig (IDE1006), which dotnet format
// reports but cannot fix. Usage: NamingTool <repository root> [--verify] [--project <path>]... [--include <path>]...
//   --verify   only list the symbols whose name breaks a rule (exit 1 when there is one)
//   --project  a .sln or .csproj to load, relative to the root (default VitaSharp.sln); more than one may be given
//   --include  only the symbols declared under this file or folder (relative to the root); more than one may be given
// Only IDE1006 diagnostics at warning or error are taken (the rules the check enforces), so a rule still at
// :suggestion is left alone. The folders listed in build/format-pending.txt and generated sources are skipped.
// [ThreadStatic] fields take t_camelCase (the attribute cannot be matched by an .editorconfig naming rule).
MSBuildLocator.RegisterDefaults();
return await NamingFixer.RunAsync(args);

internal static class NamingFixer
{
    private const string DiagnosticId = "IDE1006";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: NamingTool <repository root> [--verify] [--project <path>]... [--include <path>]...");
            return 2;
        }

        string root = Path.GetFullPath(args[0]);
        bool verify = false;
        List<string> targets = [];
        List<string> includes = [];
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--verify")
            {
                verify = true;
            }
            else if (args[i] == "--project" && i + 1 < args.Length)
            {
                targets.Add(Path.Combine(root, args[++i]));
            }
            else if (args[i] == "--include" && i + 1 < args.Length)
            {
                includes.Add(Path.GetFullPath(Path.Combine(root, args[++i])));
            }
            else
            {
                Console.Error.WriteLine($"unknown option: {args[i]}");
                return 2;
            }
        }

        if (targets.Count == 0)
        {
            targets.Add(Path.Combine(root, "VitaSharp.sln"));
            // the Android app is not in the solution (it needs the Android workload) but uses Core's members
            string android = Path.Combine(root, "src", "VitaSharp.Android", "VitaSharp.Android.csproj");
            if (File.Exists(android))
            {
                targets.Add(android);
            }
        }

        string[] pending = File.ReadAllLines(Path.Combine(root, "build", "format-pending.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .Select(l => Path.Combine(root, l.Replace('/', Path.DirectorySeparatorChar)) + Path.DirectorySeparatorChar)
            .ToArray();

        // the SDK adds its code-style analyzers (the IDE rules) to a project only with EnforceCodeStyleInBuild
        using MSBuildWorkspace workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["EnforceCodeStyleInBuild"] = "true",
        });
        foreach (string target in targets)
        {
            if (target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                await workspace.OpenSolutionAsync(target);
            }
            else
            {
                try
                {
                    await workspace.OpenProjectAsync(target);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException)
                {
                    // e.g. the Android project without the Android workload: its own symbols are not renamed
                    Console.Error.WriteLine($"naming: {Path.GetFileName(target)} not loaded: {e.Message}");
                }
            }
        }

        foreach (WorkspaceDiagnostic failure in workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure))
        {
            Console.Error.WriteLine($"naming: {failure.Message}");
        }

        Solution solution = workspace.CurrentSolution;
        List<(ISymbol Symbol, string NewName, Location Where)> renames = await FindViolationsAsync(solution, pending);
        if (includes.Count > 0)
        {
            renames = renames.Where(r => includes.Any(p => r.Where.SourceTree!.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        if (verify)
        {
            foreach ((ISymbol symbol, string newName, Location where) in renames)
            {
                FileLinePositionSpan line = where.GetLineSpan();
                Console.WriteLine($"{Path.GetRelativePath(root, line.Path)}({line.StartLinePosition.Line + 1}): {DiagnosticId} {symbol.Name} -> {newName}");
            }

            Console.WriteLine($"naming: {renames.Count} names break a rule");
            return renames.Count == 0 ? 0 : 1;
        }

        int changedFiles = await RenameAsync(solution, renames, pending);
        Console.WriteLine($"naming: {renames.Count} symbols renamed in {changedFiles} files");
        return 0;
    }

    private static bool Skipped(string? path, string[] pending)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "obj" or "bin" or "obj-dev" or "bin-dev") || pending.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<(ISymbol, string, Location)>> FindViolationsAsync(Solution solution, string[] pending)
    {
        List<(ISymbol, string, Location)> result = [];
        HashSet<ISymbol> seen = new(SymbolEqualityComparer.Default);
        foreach (Project project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            Compilation? compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                continue;
            }

            ImmutableArray<DiagnosticAnalyzer> analyzers = project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp))
                .Where(a => a.SupportedDiagnostics.Any(d => d.Id == DiagnosticId))
                .ToImmutableArray();
            if (analyzers.IsEmpty)
            {
                Console.Error.WriteLine($"naming: {project.Name}: the code-style analyzers are not loaded");
                continue;
            }

            CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
            ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Id != DiagnosticId || diagnostic.Severity < DiagnosticSeverity.Warning || diagnostic.IsSuppressed
                    || !diagnostic.Location.IsInSource || Skipped(diagnostic.Location.SourceTree?.FilePath, pending))
                {
                    continue;
                }

                SyntaxTree tree = diagnostic.Location.SourceTree!;
                SemanticModel model = compilation.GetSemanticModel(tree);
                SyntaxToken token = (await tree.GetRootAsync()).FindToken(diagnostic.Location.SourceSpan.Start);
                ISymbol? symbol = null;
                for (SyntaxNode? node = token.Parent; node != null && symbol == null; node = node.Parent)
                {
                    ISymbol? declared = model.GetDeclaredSymbol(node);
                    if (declared != null && declared.Name == token.ValueText)
                    {
                        symbol = declared;
                    }
                }

                if (symbol == null || !seen.Add(symbol)
                    || !diagnostic.Properties.TryGetValue("NamingStyle", out string? style) || style == null)
                {
                    continue;
                }

                string newName = CompliantName(symbol, XElement.Parse(style));
                if ((newName != symbol.Name || IsThreadStatic(symbol)) && SyntaxFacts.IsValidIdentifier(newName))
                {
                    result.Add((symbol, newName, diagnostic.Location));
                }
                else
                {
                    // the analyzer still reports it (dotnet format style fails on it): needs a suppression
                    Console.Error.WriteLine($"naming: {diagnostic.Location.GetLineSpan().Path}: {symbol.Name} kept, but {DiagnosticId} reports it: {diagnostic.GetMessage()}");
                }
            }
        }

        return result;
    }

    private static bool IsThreadStatic(ISymbol symbol)
    {
        return symbol is IFieldSymbol { IsStatic: true } field && field.GetAttributes().Any(a => a.AttributeClass?.Name == "ThreadStaticAttribute");
    }

    private static string CompliantName(ISymbol symbol, XElement style)
    {
        string prefix = (string?)style.Attribute("Prefix") ?? "";
        string suffix = (string?)style.Attribute("Suffix") ?? "";
        string scheme = (string?)style.Attribute("CapitalizationScheme") ?? "";
        bool pascal = scheme == "PascalCase";
        if (IsThreadStatic(symbol))
        {
            prefix = "t_";
            pascal = false;
        }

        string core = symbol.Name;
        foreach (string known in new[] { "s_", "t_", "m_" })
        {
            if (core.StartsWith(known, StringComparison.Ordinal) && core.Length > known.Length)
            {
                core = core[known.Length..];
                break;
            }
        }

        core = core.TrimStart('_');
        if (suffix.Length > 0 && core.EndsWith(suffix, StringComparison.Ordinal))
        {
            core = core[..^suffix.Length];
        }

        if (core.Length == 0)
        {
            return symbol.Name;
        }

        bool allCaps = core.Any(char.IsLetter) && !core.Any(char.IsLower);
        if (core.Contains('_') && !allCaps)
        {
            // snake_case words joined (the rules use no word separator)
            string[] words = core.Split('_', StringSplitOptions.RemoveEmptyEntries);
            core = words[0] + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }

        if (pascal)
        {
            core = char.ToUpperInvariant(core[0]) + core[1..];
        }
        else if (allCaps)
        {
            core = core.ToLowerInvariant();
        }
        else
        {
            // a leading acronym is lowered as one word: IOQueue -> ioQueue
            int upper = 0;
            while (upper < core.Length && char.IsUpper(core[upper]))
            {
                upper++;
            }

            int lower = upper > 1 && upper < core.Length && char.IsLower(core[upper]) ? upper - 1 : Math.Max(upper, 1);
            core = core[..lower].ToLowerInvariant() + core[lower..];
        }

        return prefix + core + suffix;
    }

    private static async Task<int> RenameAsync(Solution solution, List<(ISymbol Symbol, string NewName, Location Where)> renames, string[] pending)
    {
        Dictionary<string, List<(TextSpan Span, string OldName, string NewName)>> edits = new(StringComparer.OrdinalIgnoreCase);
        foreach ((ISymbol symbol, string newName, _) in renames)
        {
            if (IsThreadStatic(symbol))
            {
                // the s_ rule matches every private static field, so a [ThreadStatic] t_ field carries the suppression
                AddThreadStaticSuppression(symbol, edits, pending);
            }

            if (newName == symbol.Name)
            {
                continue;
            }

            if (Conflicts(symbol, newName))
            {
                Console.Error.WriteLine($"naming: {symbol.ToDisplayString()} -> {newName} skipped: the name is already used there");
                continue;
            }

            List<Location> locations = [.. symbol.Locations];
            if (symbol is IMethodSymbol method)
            {
                locations.AddRange(method.PartialImplementationPart?.Locations ?? []);
                locations.AddRange(method.PartialDefinitionPart?.Locations ?? []);
            }

            foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution))
            {
                if (referenced.Definition.Name == symbol.Name)
                {
                    locations.AddRange(referenced.Definition.Locations);
                }

                locations.AddRange(referenced.Locations.Where(l => !l.IsImplicit).Select(l => l.Location));
            }

            // a symbol used from a folder that is not converted yet waits for that folder (its files are not edited)
            Location? pendingUse = locations.FirstOrDefault(l => l.IsInSource && pending.Any(p => l.SourceTree!.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            if (pendingUse != null)
            {
                Console.Error.WriteLine($"naming: {symbol.ToDisplayString()} -> {newName} skipped: used in {pendingUse.SourceTree!.FilePath}, a folder of build/format-pending.txt");
                continue;
            }

            foreach (Location location in locations.Where(l => l.IsInSource))
            {
                string path = location.SourceTree!.FilePath;
                if (Skipped(path, pending) || !File.Exists(path))
                {
                    continue;
                }

                if (!edits.TryGetValue(path, out List<(TextSpan, string, string)>? list))
                {
                    edits[path] = list = [];
                }

                list.Add((location.SourceSpan, symbol.Name, newName));
            }
        }

        int files = 0;
        foreach ((string path, List<(TextSpan Span, string OldName, string NewName)> list) in edits)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            StringBuilder result = new(text);
            foreach ((TextSpan span, string oldName, string newName) in list.Distinct().OrderByDescending(e => e.Span.Start))
            {
                // the span must still hold the old name (a verbatim @name or a stale tree is left alone)
                if (span.End <= text.Length && text.AsSpan(span.Start, span.Length).SequenceEqual(oldName))
                {
                    // the inserted lines are written with the file's own line ends
                    string replacement = span.Length == 0 && !text.Contains("\r\n", StringComparison.Ordinal) ? newName.Replace("\r\n", "\n") : newName;
                    result.Remove(span.Start, span.Length).Insert(span.Start, replacement);
                }
            }

            string fixedText = result.ToString();
            if (fixedText != text)
            {
                File.WriteAllText(path, fixedText, new UTF8Encoding(bom));
                files++;
            }
        }

        return files;
    }

    private const string SuppressionUsing = "using System.Diagnostics.CodeAnalysis;";

    private static void AddThreadStaticSuppression(ISymbol symbol, Dictionary<string, List<(TextSpan, string, string)>> edits, string[] pending)
    {
        SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        SyntaxNode? field = reference?.GetSyntax().Ancestors().FirstOrDefault(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax);
        string path = reference?.SyntaxTree.FilePath ?? "";
        if (field == null || Skipped(path, pending) || field.ToFullString().Contains("IDE1006", StringComparison.Ordinal))
        {
            return;
        }

        SourceText text = field.SyntaxTree.GetText();
        TextLine line = text.Lines.GetLineFromPosition(field.SpanStart);
        string indent = text.ToString(TextSpan.FromBounds(line.Start, field.SpanStart));
        if (!edits.TryGetValue(path, out List<(TextSpan, string, string)>? list))
        {
            edits[path] = list = [];
        }

        list.Add((new TextSpan(line.Start, 0), "", indent + "[SuppressMessage(\"Style\", \"IDE1006\", Justification = \"[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute\")]\r\n"));
        SyntaxNode root = field.SyntaxTree.GetRoot();
        if (!text.ToString().Contains(SuppressionUsing, StringComparison.Ordinal))
        {
            // in order among the file's using directives (they are sorted, System ones not first)
            List<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax> usings = [.. root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>()];
            Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax? after = usings.FirstOrDefault(u => u.Alias == null && u.StaticKeyword.Span.IsEmpty && string.Compare(u.Name?.ToString(), "System.Diagnostics.CodeAnalysis", StringComparison.OrdinalIgnoreCase) > 0);
            int at = after != null ? text.Lines.GetLineFromPosition(after.SpanStart).Start
                : usings.Count > 0 ? text.Lines.GetLineFromPosition(usings[^1].SpanStart).EndIncludingLineBreak : 0;
            string usingLine = SuppressionUsing + "\r\n" + (usings.Count == 0 ? "\r\n" : "");
            if (!list.Contains((new TextSpan(at, 0), "", usingLine)))
            {
                list.Add((new TextSpan(at, 0), "", usingLine));
            }
        }
    }

    // a member of the same name in the type (or its bases), or a symbol of that name in scope of a local function
    private static bool Conflicts(ISymbol symbol, string newName)
    {
        for (INamedTypeSymbol? type = symbol.ContainingType; type != null; type = type.BaseType)
        {
            if (type.GetMembers(newName).Any(m => !SymbolEqualityComparer.Default.Equals(m, symbol)) || type.GetTypeMembers(newName).Any())
            {
                return true;
            }
        }

        return symbol.ContainingType?.Name == newName;
    }
}
