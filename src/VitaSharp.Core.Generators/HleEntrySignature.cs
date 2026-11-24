using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace VitaSharp.Core.Generators;

/// <summary>
/// What the generated <c>XxxThroughEntry</c> wrapper of an <c>[Hle]</c> method needs to know about the method, shared by
/// <see cref="HleEntryCallGenerator"/> and <see cref="HleDirectCallAnalyzer"/>: whether its call can be made by the
/// function's address (every argument and the result is a value the guest passes in core registers or on the stack:
/// integers, bools, enums, <c>Ptr&lt;T&gt;</c>), and how each argument becomes AAPCS words and r0/r1 the result.
/// A method that takes a C# string, a float, the HLE context or variadic arguments has no wrapper (the guest values of
/// those are not in its arguments any more) and the analyzer leaves its calls alone.
/// </summary>
internal static class HleEntrySignature
{
    public const string HleModuleName = "VitaSharp.Core.Hle.HleModule";
    public const string HleAttributeName = "VitaSharp.Core.Hle.HleAttribute";
    public const string PtrName = "VitaSharp.Core.Memory.Ptr<T>";
    public const string Suffix = "ThroughEntry";

    /// <summary>The method is an instance <c>[Hle]</c> method of an <c>HleModule</c>.</summary>
    public static bool IsHleExport(IMethodSymbol method, INamedTypeSymbol hleAttribute, INamedTypeSymbol moduleBase)
    {
        if (method.IsStatic || method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod)
        {
            return false;
        }

        if (!method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, hleAttribute)))
        {
            return false;
        }

        for (INamedTypeSymbol? type = method.ContainingType.BaseType; type != null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, moduleBase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Why the method's call cannot be made by address (null: it can).</summary>
    public static string? Unsupported(IMethodSymbol method)
    {
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                return $"parameter {parameter.Name} is passed by reference";
            }

            if (Kind(parameter.Type) == ValueKind.None)
            {
                return $"parameter {parameter.Name} ({parameter.Type.ToDisplayString()}) is not a value the guest passes in a core register";
            }
        }

        if (!method.ReturnsVoid && Kind(method.ReturnType) == ValueKind.None)
        {
            return $"the result ({method.ReturnType.ToDisplayString()}) is not returned in r0/r1";
        }

        return null;
    }

    public enum ValueKind
    {
        None,
        Word,
        SignedSmall,
        Bool,
        Enum32,
        Enum64,
        Pointer,
        Wide,
    }

    public static ValueKind Kind(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_UInt32:
                return ValueKind.Word;
            case SpecialType.System_Int32:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Char:
                return ValueKind.SignedSmall;
            case SpecialType.System_Boolean:
                return ValueKind.Bool;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return ValueKind.Wide;
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return underlying.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 ? ValueKind.Enum64 : ValueKind.Enum32;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } generic && generic.ConstructedFrom.ToDisplayString() == PtrName)
        {
            return ValueKind.Pointer;
        }

        return ValueKind.None;
    }

    public static string Fq(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>The AAPCS words of the arguments, as C# expressions: 32-bit values one word each, 64-bit ones an even
    /// pair (r0/r1, r2/r3 or an 8-byte aligned stack slot: a padding word before an odd position).</summary>
    public static List<string> Words(IMethodSymbol method)
    {
        var words = new List<string>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            string name = Escape(parameter.Name);
            ITypeSymbol type = parameter.Type;
            switch (Kind(type))
            {
                case ValueKind.Word:
                    words.Add(name);
                    break;
                case ValueKind.SignedSmall:
                    words.Add($"unchecked((uint){name})");
                    break;
                case ValueKind.Bool:
                    words.Add($"({name} ? 1u : 0u)");
                    break;
                case ValueKind.Enum32:
                    words.Add($"unchecked((uint)({Fq(((INamedTypeSymbol)type).EnumUnderlyingType!)}){name})");
                    break;
                case ValueKind.Pointer:
                    words.Add($"{name}.Address");
                    break;
                case ValueKind.Wide:
                case ValueKind.Enum64:
                    if ((words.Count & 1) != 0)
                    {
                        words.Add("0u");
                    }

                    words.Add($"unchecked((uint)(ulong){name})");
                    words.Add($"unchecked((uint)((ulong){name} >> 32))");
                    break;
            }
        }

        return words;
    }

    /// <summary>The C# expression that turns <paramref name="raw"/> (r0 | r1 &lt;&lt; 32, a ulong) into the method's
    /// result.</summary>
    public static string Result(IMethodSymbol method, string raw)
    {
        ITypeSymbol type = method.ReturnType;
        switch (Kind(type))
        {
            case ValueKind.Word:
                return $"unchecked((uint){raw})";
            case ValueKind.SignedSmall:
                return $"unchecked(({Fq(type)})(uint){raw})";
            case ValueKind.Bool:
                return $"unchecked((uint){raw}) != 0u";
            case ValueKind.Enum32:
                return $"unchecked(({Fq(type)})({Fq(((INamedTypeSymbol)type).EnumUnderlyingType!)})(uint){raw})";
            case ValueKind.Enum64:
                return $"unchecked(({Fq(type)})({Fq(((INamedTypeSymbol)type).EnumUnderlyingType!)}){raw})";
            case ValueKind.Pointer:
                return $"new {Fq(type)}(unchecked((uint){raw}))";
            case ValueKind.Wide:
                return $"unchecked(({Fq(type)}){raw})";
            default:
                return raw;
        }
    }

    public static string Escape(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ? "@" + name : name;
}
