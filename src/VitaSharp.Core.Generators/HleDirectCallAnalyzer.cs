using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace VitaSharp.Core.Generators;

/// <summary>
/// VSHLE010: a call, in VitaSharp.Core, of an exported firmware function the HLE serves (an instance <c>[Hle]</c>
/// method of an <c>HleModule</c>) made directly, outside its generated wrapper <c>XxxThroughEntry</c>
/// (<see cref="HleEntryCallGenerator"/>). On the Vita the firmware calls its exported functions by their address, so a
/// plugin's hook on the export (which patches the function's code) sees the firmware's own calls; a direct C# call
/// skips the hook. A function whose call cannot be made by address (a C# string, a float, the HLE context or variadic
/// arguments, <see cref="HleEntrySignature.Unsupported"/>) is not reported, nor is a method calling itself. A call
/// that is not the firmware's call of the function (a test probe, the emulator's own bookkeeping) is kept direct with
/// <c>#pragma warning disable VSHLE010</c> and a comment saying why.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HleDirectCallAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSHLE010";

    private static readonly DiagnosticDescriptor s_directCall = new(
        Id, "Direct call of an exported firmware function",
        "{0} is an exported firmware function: the firmware calls it by its address, so a hook on its entry sees the call; call {0}ThroughEntry",
        "VitaSharp.Hle", DiagnosticSeverity.Warning, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_directCall);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.AssemblyName != "VitaSharp.Core")
            {
                return;
            }

            INamedTypeSymbol? hleAttribute = start.Compilation.GetTypeByMetadataName(HleEntrySignature.HleAttributeName);
            INamedTypeSymbol? moduleBase = start.Compilation.GetTypeByMetadataName(HleEntrySignature.HleModuleName);
            if (hleAttribute == null || moduleBase == null)
            {
                return;
            }

            start.RegisterOperationAction(operation => Check(operation, hleAttribute, moduleBase), OperationKind.Invocation);
        });
    }

    private static void Check(OperationAnalysisContext context, INamedTypeSymbol hleAttribute, INamedTypeSymbol moduleBase)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod.OriginalDefinition;
        if (!HleEntrySignature.IsHleExport(method, hleAttribute, moduleBase) || HleEntrySignature.Unsupported(method) != null)
        {
            return;
        }

        if (SymbolEqualityComparer.Default.Equals(context.ContainingSymbol, method))
        {
            return;
        }

        Location location = invocation.Syntax is InvocationExpressionSyntax syntax
            ? syntax.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.GetLocation(),
                MemberBindingExpressionSyntax binding => binding.Name.GetLocation(),
                _ => syntax.Expression.GetLocation(),
            }
            : invocation.Syntax.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(s_directCall, location, method.Name));
    }
}
