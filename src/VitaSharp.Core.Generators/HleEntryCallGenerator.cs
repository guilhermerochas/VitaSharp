using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace VitaSharp.Core.Generators;

/// <summary>
/// Emits the <c>XxxThroughEntry</c> wrapper of every <c>[Hle]</c> method some code calls that way: the call the
/// firmware makes to its exported function, by the function's address, so a plugin's hook on the export sees it
/// (on the Vita the hook patches the function's code). The wrapper asks <c>HleEntryCalls.AnyHook</c> (a few field
/// reads, nothing allocated); only when a hook exists does it look the function's NIDs up
/// (<c>HleEntryCalls.TryCall</c>) and run the hooked entry on the calling thread with the arguments as AAPCS words;
/// otherwise, and when no hook took this function, it calls the C# method. Wrappers are made only for the methods
/// called through one (the call sites name <c>XxxThroughEntry</c>), as members of the method's own module, which must
/// be partial; the analyzer <see cref="HleDirectCallAnalyzer"/> asks for them.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HleEntryCallGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor s_notPartial = new(
        "VSHLE011", "HLE module is not partial", "{0}.{1}ThroughEntry: make {0} (and the types containing it) partial so its wrapper can be generated",
        "VitaSharp.Hle", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_unsupported = new(
        "VSHLE012", "HLE method cannot be called through its entry", "{0}.{1}ThroughEntry: {2}",
        "VitaSharp.Hle", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<Wrapper?>> wrappers = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation && CalledName(invocation.Expression) is { } name
                    && name.Length > HleEntrySignature.Suffix.Length && name.EndsWith(HleEntrySignature.Suffix, System.StringComparison.Ordinal),
                static (ctx, ct) => Find((InvocationExpressionSyntax)ctx.Node, ctx.SemanticModel, ct))
            .Where(static w => w != null)
            .Collect();
        context.RegisterSourceOutput(wrappers, static (spc, list) => Emit(spc, list));
    }

    private static string? CalledName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The wrapper an <c>XxxThroughEntry</c> call names, or null when Xxx is not an [Hle] method of the
    /// receiver's type (the compiler reports the missing member).</summary>
    private static Wrapper? Find(InvocationExpressionSyntax invocation, SemanticModel model, System.Threading.CancellationToken ct)
    {
        INamedTypeSymbol? hleAttribute = model.Compilation.GetTypeByMetadataName(HleEntrySignature.HleAttributeName);
        INamedTypeSymbol? moduleBase = model.Compilation.GetTypeByMetadataName(HleEntrySignature.HleModuleName);
        if (hleAttribute == null || moduleBase == null)
        {
            return null;
        }

        string called = CalledName(invocation.Expression)!;
        string target = called.Substring(0, called.Length - HleEntrySignature.Suffix.Length);
        ITypeSymbol? receiver = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => model.GetTypeInfo(access.Expression, ct).Type,
            MemberBindingExpressionSyntax => invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional
                ? model.GetTypeInfo(conditional.Expression, ct).Type
                : null,
            _ => EnclosingType(model.GetEnclosingSymbol(invocation.SpanStart, ct)),
        };
        if (receiver is INamedTypeSymbol { IsValueType: true, OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            receiver = nullable.TypeArguments[0];
        }

        for (ITypeSymbol? type = receiver; type != null; type = type.BaseType)
        {
            foreach (IMethodSymbol method in type.GetMembers(target).OfType<IMethodSymbol>())
            {
                if (HleEntrySignature.IsHleExport(method, hleAttribute, moduleBase))
                {
                    return Wrapper.For(method, hleAttribute, invocation.Expression.GetLocation());
                }
            }
        }

        return null;
    }

    private static ITypeSymbol? EnclosingType(ISymbol? symbol)
    {
        for (ISymbol? s = symbol; s != null; s = s.ContainingSymbol)
        {
            if (s is INamedTypeSymbol type)
            {
                return type;
            }
        }

        return null;
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Wrapper?> list)
    {
        var byType = new SortedDictionary<string, SortedDictionary<string, Wrapper>>(System.StringComparer.Ordinal);
        foreach (Wrapper? wrapper in list)
        {
            if (wrapper == null)
            {
                continue;
            }

            if (wrapper.Error != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(s_unsupported, wrapper.Location, wrapper.TypeName, wrapper.Method, wrapper.Error));
                continue;
            }

            if (!wrapper.Partial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(s_notPartial, wrapper.Location, wrapper.TypeName, wrapper.Method));
                continue;
            }

            if (!byType.TryGetValue(wrapper.TypeKey, out SortedDictionary<string, Wrapper>? methods))
            {
                methods = new SortedDictionary<string, Wrapper>(System.StringComparer.Ordinal);
                byType[wrapper.TypeKey] = methods;
            }

            methods[wrapper.Method] = wrapper;
        }

        foreach (KeyValuePair<string, SortedDictionary<string, Wrapper>> type in byType)
        {
            Wrapper first = type.Value.Values.First();
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/> by VitaSharp.Core.Generators.HleEntryCallGenerator");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable CS0618 // [Obsolete] HLE methods are still called");
            if (first.Namespace.Length > 0)
            {
                sb.AppendLine($"namespace {first.Namespace};");
                sb.AppendLine();
            }

            foreach (string container in first.Containers)
            {
                sb.AppendLine($"partial class {container}");
                sb.AppendLine("{");
            }

            foreach (Wrapper wrapper in type.Value.Values)
            {
                wrapper.AppendTo(sb);
            }

            foreach (string _ in first.Containers)
            {
                sb.AppendLine("}");
            }

            spc.AddSource($"HleEntry.{type.Key.Replace('<', '_').Replace('>', '_')}.g.cs", sb.ToString());
        }
    }

    /// <summary>One wrapper to emit: everything as text, so the pipeline compares models by value.</summary>
    private sealed class Wrapper
    {
        public string TypeKey = "";
        public string TypeName = "";
        public string Namespace = "";
        public List<string> Containers = new();
        public bool Partial;
        public string Method = "";
        public string Access = "";
        public string Return = "";
        public string ResultExpression = "";
        public string Parameters = "";
        public string ParametersWithoutDefaults = "";
        public string Arguments = "";
        public string Words = "";
        public string Exports = "";
        public string? Error;
        public Location? Location;

        public static Wrapper For(IMethodSymbol method, INamedTypeSymbol hleAttribute, Location location)
        {
            INamedTypeSymbol type = method.ContainingType;
            var wrapper = new Wrapper
            {
                TypeKey = type.ToDisplayString(),
                TypeName = type.Name,
                Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
                Method = method.Name,
                Location = location,
                Error = HleEntrySignature.Unsupported(method),
            };
            wrapper.Partial = true;
            for (INamedTypeSymbol? t = type; t != null; t = t.ContainingType)
            {
                wrapper.Containers.Insert(0, t.Name);
                bool partial = t.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax declaration
                    && declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
                wrapper.Partial &= partial;
            }

            if (wrapper.Error != null)
            {
                return wrapper;
            }

            wrapper.Access = method.DeclaredAccessibility switch
            {
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedAndInternal => "private protected",
                _ => "internal",
            };
            wrapper.Return = method.ReturnsVoid ? "void" : HleEntrySignature.Fq(method.ReturnType);
            wrapper.ResultExpression = method.ReturnsVoid ? "" : HleEntrySignature.Result(method, "hleEntryResult");
            wrapper.Parameters = string.Join(", ", method.Parameters.Select(p => $"{HleEntrySignature.Fq(p.Type)} {HleEntrySignature.Escape(p.Name)}{DefaultOf(p)}"));
            wrapper.ParametersWithoutDefaults = string.Join(", ", method.Parameters.Select(p => $"{HleEntrySignature.Fq(p.Type)} {HleEntrySignature.Escape(p.Name)}"));
            wrapper.Arguments = string.Join(", ", method.Parameters.Select(p => HleEntrySignature.Escape(p.Name)));
            wrapper.Words = string.Join(", ", HleEntrySignature.Words(method));
            var exports = new List<string>();
            foreach (AttributeData attribute in method.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, hleAttribute))
                {
                    continue;
                }

                string name = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string n ? n : method.Name;
                uint nid = 0;
                string? library = null;
                foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
                {
                    if (named.Key == "Nid" && named.Value.Value is uint value)
                    {
                        nid = value;
                    }
                    else if (named.Key == "Library")
                    {
                        library = named.Value.Value as string;
                    }
                }

                exports.Add($"new global::VitaSharp.Core.Hle.HleExportName(\"{name}\", 0x{nid:X8}u, {(library == null ? "null" : "\"" + library + "\"")})");
            }

            wrapper.Exports = string.Join(", ", exports);
            return wrapper;
        }

        /// <summary>The parameter's default value as written after its name (" = 0"), or "".</summary>
        private static string DefaultOf(IParameterSymbol parameter)
        {
            if (!parameter.HasExplicitDefaultValue)
            {
                return "";
            }

            object? value = parameter.ExplicitDefaultValue;
            if (value == null)
            {
                return " = default";
            }

            string literal = value switch
            {
                bool b => b ? "true" : "false",
                char c => $"(char){(int)c}",
                _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            };
            return $" = unchecked(({HleEntrySignature.Fq(parameter.Type)})({literal}))";
        }

        public void AppendTo(StringBuilder sb)
        {
            string field = $"s_hleEntry_{Method}";
            sb.AppendLine($"    /// <summary>The exports <see cref=\"{Method}\"/> is (<see cref=\"global::VitaSharp.Core.Hle.HleEntryCalls\"/>).</summary>");
            sb.AppendLine($"    private static readonly global::VitaSharp.Core.Hle.HleExportRef {field} = new({Exports});");
            sb.AppendLine();
            sb.AppendLine($"    /// <summary><see cref=\"{Method}\"/> called the way the firmware calls it, by the function's address: a hook");
            sb.AppendLine("    /// on its entry runs on the calling thread, else the C# method runs directly.</summary>");
            string hooked = Method + HleEntrySignature.Suffix + "Hooked";
            string call = Return == "void" ? "" : "return ";
            // inlined into the caller: without a hook the call costs the check and the method's own call
            sb.AppendLine("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine($"    {Access} {Return} {Method}{HleEntrySignature.Suffix}({Parameters})");
            sb.AppendLine("    {");
            sb.AppendLine("        if (global::VitaSharp.Core.Hle.HleEntryCalls.AnyHook(this))");
            sb.AppendLine("        {");
            sb.AppendLine($"            {call}{hooked}({Arguments});");
            if (Return == "void")
            {
                sb.AppendLine("            return;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        {call}{Method}({Arguments});");
            sb.AppendLine("    }");
            sb.AppendLine();
            // the call through a hook: out of line, so the wrapper without a hook keeps a small frame (no argument
            // words on its stack) and inlines into its caller
            sb.AppendLine($"    /// <summary><see cref=\"{Method}{HleEntrySignature.Suffix}\"/> when a hook may have taken the function.</summary>");
            sb.AppendLine("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
            sb.AppendLine($"    private {Return} {hooked}({ParametersWithoutDefaults})");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (global::VitaSharp.Core.Hle.HleEntryCalls.TryCall(this, {field}, out ulong hleEntryResult, [{Words}]))");
            sb.AppendLine("        {");
            sb.AppendLine(Return == "void" ? "            return;" : $"            return {ResultExpression};");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        {call}{Method}({Arguments});");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        public override bool Equals(object? obj) => obj is Wrapper other && Key == other.Key;

        public override int GetHashCode() => Key.GetHashCode();

        private string Key => $"{TypeKey}|{Method}|{Partial}|{Error}|{Access}|{Return}|{Parameters}|{Words}|{Exports}";
    }
}
