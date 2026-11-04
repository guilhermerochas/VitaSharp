using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VitaSharp.Core.Generators;

/// <summary>
/// Save states (docs/notes/save-states.md):
/// <list type="bullet">
/// <item>for every partial type marked <c>[GenerateSnapshot]</c>, an <c>ISnapshotState</c> implementation that writes
/// and reads its instance fields in declaration order (supported field types: see <c>GenerateSnapshotAttribute</c>;
/// any other is error VSSNAP001). A type may add <c>void SaveExtra(SnapshotWriter)</c> / <c>void LoadExtra(SnapshotReader)</c>
/// for hand-written parts and <c>void AfterSnapshotLoad()</c>, which the generated methods call;</item>
/// <item><c>SnapshotSchema</c> (core assembly only): a hash of every generated layout, and which HLE modules have
/// instance state but no <c>ISnapshotState</c> (a save is refused once the game used one of them).</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SnapshotGenerator : IIncrementalGenerator
{
    const string AttrName = "VitaSharp.Core.SaveState.GenerateSnapshotAttribute";
    const string IgnoreName = "VitaSharp.Core.SaveState.SnapshotIgnoreAttribute";
    const string IgnoreFieldsName = "VitaSharp.Core.SaveState.SnapshotIgnoreFieldsAttribute";
    const string StateName = "VitaSharp.Core.SaveState.ISnapshotState";
    const string KernelObjectName = "VitaSharp.Core.Kernel.KernelObject";
    const string HleModuleName = "VitaSharp.Core.Hle.HleModule";

    static readonly DiagnosticDescriptor Unsupported = new(
        "VSSNAP001", "Field not supported by the snapshot generator",
        "{0}.{1}: type {2} cannot be saved; mark the field [SnapshotIgnore] or write ISnapshotState by hand", "VitaSharp.SaveState", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor NotPartial = new(
        "VSSNAP002", "[GenerateSnapshot] type must be partial", "{0} must be partial (and so must its containing types)", "VitaSharp.SaveState", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol)
            .Where(static t => t is not null)
            .Collect();
        context.RegisterSourceOutput(types.Combine(context.CompilationProvider), static (spc, pair) => Emit(spc, pair.Left!, pair.Right));
    }

    sealed class Ctx
    {
        public Compilation C = null!;
        public INamedTypeSymbol? State, KernelObject, Attr, Ignore, IgnoreFields;
        /// <summary>Unique local variable numbers of the generated readers.</summary>
        public int N;
    }

    static bool HasAttr(ISymbol s, INamedTypeSymbol? a) => a != null && s.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, a));

    static bool Implements(ITypeSymbol t, INamedTypeSymbol? i) => i != null && t.AllInterfaces.Any(x => SymbolEqualityComparer.Default.Equals(x, i));

    static bool Derives(ITypeSymbol t, INamedTypeSymbol? b)
    {
        if (b == null) return false;
        for (var x = t.BaseType; x != null; x = x.BaseType) if (SymbolEqualityComparer.Default.Equals(x, b)) return true;
        return false;
    }

    /// <summary>The fields a snapshot covers: instance fields (auto-property backing fields by their property) declared
    /// in <paramref name="t"/>, without [SnapshotIgnore].</summary>
    static IEnumerable<(string Name, ITypeSymbol Type, bool ReadOnly, ISymbol Symbol)> Fields(INamedTypeSymbol t, Ctx c)
    {
        var ignored = new HashSet<string>(t.GetAttributes().Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, c.IgnoreFields))
            .SelectMany(a => a.ConstructorArguments.SelectMany(x => x.Kind == TypedConstantKind.Array ? x.Values : ImmutableArray.Create(x)))
            .Select(x => x.Value as string).Where(x => x != null)!);
        foreach (var m in t.GetMembers())
        {
            if (m is not IFieldSymbol f || f.IsStatic || f.IsConst) continue;
            if (ignored.Contains(f.Name) || (f.AssociatedSymbol != null && ignored.Contains(f.AssociatedSymbol.Name))) continue;
            if (f.AssociatedSymbol is IPropertySymbol p)
            {
                if (HasAttr(p, c.Ignore) || HasAttr(f, c.Ignore)) continue;
                yield return (p.Name, f.Type, p.SetMethod == null || p.SetMethod.IsInitOnly, p);
                continue;
            }
            if (f.IsImplicitlyDeclared || HasAttr(f, c.Ignore)) continue;
            yield return (f.Name, f.Type, f.IsReadOnly, f);
        }
    }

    static void Emit(SourceProductionContext spc, ImmutableArray<INamedTypeSymbol?> candidates, Compilation compilation)
    {
        var c = new Ctx
        {
            C = compilation,
            State = compilation.GetTypeByMetadataName(StateName),
            KernelObject = compilation.GetTypeByMetadataName(KernelObjectName),
            Attr = compilation.GetTypeByMetadataName(AttrName),
            Ignore = compilation.GetTypeByMetadataName(IgnoreName),
            IgnoreFields = compilation.GetTypeByMetadataName(IgnoreFieldsName),
        };
        if (c.State == null || c.Attr == null) return;
        var all = candidates.Where(t => t != null).Select(t => t!).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(t => t.ToDisplayString(), System.StringComparer.Ordinal).ToList();
        var schema = new StringBuilder();
        foreach (var t in all.Where(t => HasAttr(t, c.Attr)))
        {
            if (!IsPartialChain(t))
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartial, t.Locations.FirstOrDefault(), t.Name));
                continue;
            }
            string? src = EmitType(spc, t, c, schema);
            if (src != null) spc.AddSource($"Snapshot.{t.ToDisplayString().Replace('<', '_').Replace('>', '_')}.g.cs", src);
        }
        if (compilation.AssemblyName != "VitaSharp.Core") return;
        var moduleBase = compilation.GetTypeByMetadataName(HleModuleName);
        var stateful = new List<string>();
        foreach (var t in all)
        {
            if (t.IsAbstract || t.TypeKind != TypeKind.Class || !Derives(t, moduleBase) || Implements(t, c.State) || HasAttr(t, c.Attr)) continue;
            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected) continue;
            if (Fields(t, c).Any()) stateful.Add(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
        ulong hash = 14695981039346656037UL;
        foreach (char ch in schema.ToString()) { hash ^= ch; hash *= 1099511628211UL; }
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by VitaSharp.Core.Generators.SnapshotGenerator");
        sb.AppendLine("namespace VitaSharp.Core.SaveState;");
        sb.AppendLine("public static partial class SnapshotSchema");
        sb.AppendLine("{");
        sb.AppendLine($"    const string GeneratedHash = \"{hash:x16}\";");
        sb.AppendLine("    static readonly global::System.Collections.Generic.HashSet<global::System.Type> StatefulModules = new()");
        sb.AppendLine("    {");
        foreach (var s in stateful) sb.AppendLine($"        typeof({s}),");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        spc.AddSource("SnapshotSchema.g.cs", sb.ToString());
    }

    static bool IsPartialChain(INamedTypeSymbol t)
    {
        for (var x = t; x != null; x = x.ContainingType)
            if (!x.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(m => m.Text == "partial"))) return false;
        return true;
    }

    static string? EmitType(SourceProductionContext spc, INamedTypeSymbol t, Ctx c, StringBuilder schema)
    {
        var save = new StringBuilder();
        var load = new StringBuilder();
        bool ok = true;
        schema.Append(t.ToDisplayString()).Append('{');
        foreach (var (name, type, ro, sym) in Fields(t, c))
        {
            string e = $"this.{name}";
            if (ro && !IsMutableInPlace(type, c)) continue; // configuration set at construction
            if (!Supported(type, c, 0))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unsupported, sym.Locations.FirstOrDefault(), t.Name, name, type.ToDisplayString()));
                ok = false;
                continue;
            }
            schema.Append(name).Append(':').Append(type.ToDisplayString()).Append(';');
            EmitWrite(save, type, e, 0, c, "        ");
            if (ro) EmitReadInPlace(load, type, e, c, "        ");
            else
            {
                string v = EmitRead(load, type, 0, c, "        ", e);
                load.AppendLine($"        {e} = {v};");
            }
        }
        // hand-written parts: their code (tokens, without comments and whitespace) is part of the layout
        foreach (var hook in new[] { "SaveExtra", "LoadExtra", "BeforeSnapshotLoad", "AfterSnapshotLoad" })
            foreach (var m in t.GetMembers(hook).OfType<IMethodSymbol>())
                foreach (var r in m.DeclaringSyntaxReferences)
                    foreach (var tok in r.GetSyntax().DescendantTokens()) schema.Append(tok.Text).Append(' ');
        schema.Append('}');
        if (!ok) return null;
        bool HasMethod(string n, int args) => t.GetMembers(n).OfType<IMethodSymbol>().Any(m => !m.IsStatic && m.Parameters.Length == args);
        if (HasMethod("BeforeSnapshotLoad", 0)) load.Insert(0, "        BeforeSnapshotLoad();\n");
        if (HasMethod("SaveExtra", 1)) save.AppendLine("        SaveExtra(w);");
        if (HasMethod("LoadExtra", 1)) load.AppendLine("        LoadExtra(r);");
        if (HasMethod("AfterSnapshotLoad", 0)) load.AppendLine("        AfterSnapshotLoad();");

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by VitaSharp.Core.Generators.SnapshotGenerator");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable CS0618");
        if (!t.ContainingNamespace.IsGlobalNamespace) sb.AppendLine($"namespace {t.ContainingNamespace.ToDisplayString()};");
        var chain = new List<INamedTypeSymbol>();
        for (var x = t; x != null; x = x.ContainingType) chain.Insert(0, x);
        foreach (var x in chain)
        {
            string kind = x.TypeKind == TypeKind.Struct ? (x.IsRecord ? "record struct" : "struct") : (x.IsRecord ? "record" : "class");
            string unsafeMod = x.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(m => m.Text == "unsafe")) ? "unsafe " : "";
            string iface = SymbolEqualityComparer.Default.Equals(x, t) ? " : global::VitaSharp.Core.SaveState.ISnapshotState" : "";
            sb.AppendLine($"{unsafeMod}partial {kind} {x.Name}{TypeParams(x)}{iface}");
            sb.AppendLine("{");
        }
        sb.AppendLine("    public void SaveState(global::VitaSharp.Core.SaveState.SnapshotWriter w)");
        sb.AppendLine("    {");
        sb.Append(save);
        sb.AppendLine("    }");
        sb.AppendLine("    public void LoadState(global::VitaSharp.Core.SaveState.SnapshotReader r)");
        sb.AppendLine("    {");
        sb.Append(load);
        sb.AppendLine("    }");
        foreach (var _ in chain) sb.AppendLine("}");
        return sb.ToString();
    }

    static string TypeParams(INamedTypeSymbol t) => t.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";

    static string Fq(ITypeSymbol t) => t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    enum Coll { None, List, Set, Queue, Dict }

    static Coll CollectionKind(ITypeSymbol t, out ITypeSymbol[] args)
    {
        args = [];
        if (t is not INamedTypeSymbol n || !n.IsGenericType) return Coll.None;
        string def = n.ConstructedFrom.ToDisplayString();
        args = n.TypeArguments.ToArray();
        return def switch
        {
            "System.Collections.Generic.List<T>" => Coll.List,
            "System.Collections.Generic.HashSet<T>" => Coll.Set,
            "System.Collections.Generic.Queue<T>" => Coll.Queue,
            "System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>"
                or "System.Collections.Generic.SortedDictionary<TKey, TValue>" => Coll.Dict,
            _ => Coll.None,
        };
    }

    static bool IsNullableValue(ITypeSymbol t, out ITypeSymbol inner)
    {
        inner = t;
        if (t is INamedTypeSymbol { IsGenericType: true } n && n.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T) { inner = n.TypeArguments[0]; return true; }
        return false;
    }

    static bool IsPrimitive(ITypeSymbol t) => t.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_IntPtr;

    static string ReadPrim(ITypeSymbol t) => t.SpecialType switch
    {
        SpecialType.System_Boolean => "ReadBool", SpecialType.System_Byte => "ReadByte", SpecialType.System_SByte => "ReadSByte",
        SpecialType.System_Int16 => "ReadInt16", SpecialType.System_UInt16 => "ReadUInt16", SpecialType.System_Char => "ReadChar",
        SpecialType.System_Int32 => "ReadInt32", SpecialType.System_UInt32 => "ReadUInt32", SpecialType.System_Int64 => "ReadInt64",
        SpecialType.System_UInt64 => "ReadUInt64", SpecialType.System_Single => "ReadSingle", SpecialType.System_Double => "ReadDouble",
        _ => "ReadIntPtr",
    };

    static bool IsState(ITypeSymbol t, Ctx c) => t.TypeKind == TypeKind.Class && (Implements(t, c.State) || HasAttr(t, c.Attr));

    /// <summary>Unmanaged structs without pointers or host handles, written as raw bytes.</summary>
    static bool IsBlittableStruct(ITypeSymbol t) => t.TypeKind == TypeKind.Struct && t.IsUnmanagedType && !t.IsTupleType && !IsPrimitive(t)
        && t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).All(f => f.Type.TypeKind != TypeKind.Pointer && f.Type.SpecialType != SpecialType.System_IntPtr && (f.Type.TypeKind != TypeKind.Struct || IsPrimitive(f.Type) || f.Type.TypeKind == TypeKind.Enum || IsBlittableStruct(f.Type)));

    static bool Supported(ITypeSymbol t, Ctx c, int depth)
    {
        if (depth > 6) return false;
        if (IsPrimitive(t) || t.TypeKind == TypeKind.Enum || t.SpecialType == SpecialType.System_String) return true;
        if (IsNullableValue(t, out var inner)) return Supported(inner, c, depth + 1);
        if (t is IArrayTypeSymbol { Rank: 1 } a) return Supported(a.ElementType, c, depth + 1);
        if (t.IsTupleType && t is INamedTypeSymbol tt) return tt.TupleElements.All(e => Supported(e.Type, c, depth + 1));
        if (Derives(t, c.KernelObject)) return true;
        if (IsState(t, c)) return true;
        if (IsBlittableStruct(t)) return true;
        if (CollectionKind(t, out var args) != Coll.None) return args.All(x => Supported(x, c, depth + 1));
        return false;
    }

    /// <summary>Readonly fields that still carry state: collections and nested states (filled in place on load).</summary>
    static bool IsMutableInPlace(ITypeSymbol t, Ctx c) => t is IArrayTypeSymbol || CollectionKind(t, out _) != Coll.None || IsState(t, c);

    static void EmitWrite(StringBuilder sb, ITypeSymbol t, string e, int d, Ctx c, string ind)
    {
        if (IsPrimitive(t) || t.SpecialType == SpecialType.System_String) { sb.AppendLine($"{ind}w.Write({e});"); return; }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum } en) { sb.AppendLine($"{ind}w.Write(({Fq(en.EnumUnderlyingType!)}){e});"); return; }
        if (IsNullableValue(t, out var inner))
        {
            sb.AppendLine($"{ind}w.Write({e}.HasValue);");
            sb.AppendLine($"{ind}if ({e}.HasValue) {{");
            EmitWrite(sb, inner, $"{e}.Value", d + 1, c, ind + "    ");
            sb.AppendLine($"{ind}}}");
            return;
        }
        if (Derives(t, c.KernelObject)) { sb.AppendLine($"{ind}w.WriteKernelObject({e});"); return; }
        if (IsState(t, c)) { sb.AppendLine($"{ind}w.Write((global::VitaSharp.Core.SaveState.ISnapshotState){e});"); return; }
        if (t.IsTupleType && t is INamedTypeSymbol tt)
        {
            int i = 1;
            foreach (var el in tt.TupleElements) EmitWrite(sb, el.Type, $"{e}.Item{i++}", d + 1, c, ind);
            return;
        }
        if (IsBlittableStruct(t)) { sb.AppendLine($"{ind}w.WriteStruct({e});"); return; }
        string x = $"x{d}";
        if (t is IArrayTypeSymbol a)
        {
            if (IsPrimitive(a.ElementType) || IsBlittableStruct(a.ElementType)) { sb.AppendLine($"{ind}w.WriteArray({e});"); return; }
            sb.AppendLine($"{ind}w.Write({e} == null ? -1 : {e}.Length);");
            sb.AppendLine($"{ind}if ({e} != null) foreach (var {x} in {e}) {{");
            EmitWrite(sb, a.ElementType, x, d + 1, c, ind + "    ");
            sb.AppendLine($"{ind}}}");
            return;
        }
        var kind = CollectionKind(t, out var args);
        sb.AppendLine($"{ind}w.Write({e} == null ? -1 : {e}.Count);");
        sb.AppendLine($"{ind}if ({e} != null) foreach (var {x} in {e}) {{");
        if (kind == Coll.Dict)
        {
            EmitWrite(sb, args[0], $"{x}.Key", d + 1, c, ind + "    ");
            EmitWrite(sb, args[1], $"{x}.Value", d + 1, c, ind + "    ");
        }
        else EmitWrite(sb, args[0], x, d + 1, c, ind + "    ");
        sb.AppendLine($"{ind}}}");
    }

    /// <summary>Appends statements that read a value of <paramref name="t"/> into a new variable; returns its name.
    /// <paramref name="existing"/> (a field) is reused for collections so their comparers stay.</summary>
    static string EmitRead(StringBuilder sb, ITypeSymbol t, int d, Ctx c, string ind, string? existing = null)
    {
        int id = c.N++;
        string v = $"v{id}";
        string ft = Fq(t);
        if (IsPrimitive(t)) { sb.AppendLine($"{ind}var {v} = r.{ReadPrim(t)}();"); return v; }
        if (t.SpecialType == SpecialType.System_String) { sb.AppendLine($"{ind}var {v} = r.ReadString();"); return v; }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum } en) { sb.AppendLine($"{ind}var {v} = ({ft})r.{ReadPrim(en.EnumUnderlyingType!)}();"); return v; }
        if (IsNullableValue(t, out var inner))
        {
            sb.AppendLine($"{ind}{Fq(inner)}? {v} = null;");
            sb.AppendLine($"{ind}if (r.ReadBool()) {{");
            string iv = EmitRead(sb, inner, d + 1, c, ind + "    ");
            sb.AppendLine($"{ind}    {v} = {iv};");
            sb.AppendLine($"{ind}}}");
            return v;
        }
        if (Derives(t, c.KernelObject)) { sb.AppendLine($"{ind}var {v} = r.ReadKernelObject<{ft}>();"); return v; }
        if (IsState(t, c)) { sb.AppendLine($"{ind}var {v} = r.ReadState(() => new {ft}());"); return v; }
        if (t.IsTupleType && t is INamedTypeSymbol tt)
        {
            var parts = new List<string>();
            foreach (var el in tt.TupleElements) parts.Add(EmitRead(sb, el.Type, d + 1, c, ind));
            sb.AppendLine($"{ind}{ft} {v} = ({string.Join(", ", parts)});");
            return v;
        }
        if (IsBlittableStruct(t)) { sb.AppendLine($"{ind}var {v} = r.ReadStruct<{ft}>();"); return v; }
        string n = $"n{id}", i2 = $"i{id}";
        if (t is IArrayTypeSymbol a)
        {
            string et = Fq(a.ElementType);
            if (IsPrimitive(a.ElementType) || IsBlittableStruct(a.ElementType)) { sb.AppendLine($"{ind}var {v} = r.ReadArray<{et}>();"); return v; }
            sb.AppendLine($"{ind}{ft} {v} = null;");
            sb.AppendLine($"{ind}int {n} = r.ReadInt32();");
            sb.AppendLine($"{ind}if ({n} >= 0) {{");
            sb.AppendLine($"{ind}    {v} = new {ArrayNew(a, n)};");
            sb.AppendLine($"{ind}    for (int {i2} = 0; {i2} < {n}; {i2}++) {{");
            string ev = EmitRead(sb, a.ElementType, d + 1, c, ind + "        ");
            sb.AppendLine($"{ind}        {v}[{i2}] = {ev};");
            sb.AppendLine($"{ind}    }}");
            sb.AppendLine($"{ind}}}");
            return v;
        }
        var kind = CollectionKind(t, out var args);
        sb.AppendLine($"{ind}{ft} {v} = null;");
        sb.AppendLine($"{ind}int {n} = r.ReadInt32();");
        sb.AppendLine($"{ind}if ({n} >= 0) {{");
        sb.AppendLine(existing != null ? $"{ind}    {v} = {existing} ?? new {ft}(); {v}.Clear();" : $"{ind}    {v} = new {ft}();");
        sb.AppendLine($"{ind}    for (int {i2} = 0; {i2} < {n}; {i2}++) {{");
        if (kind == Coll.Dict)
        {
            string k = EmitRead(sb, args[0], d + 1, c, ind + "        ");
            string val = EmitRead(sb, args[1], d + 1, c, ind + "        ");
            sb.AppendLine($"{ind}        {v}[{k}] = {val};");
        }
        else
        {
            string ev = EmitRead(sb, args[0], d + 1, c, ind + "        ");
            sb.AppendLine($"{ind}        {v}.{(kind == Coll.Queue ? "Enqueue" : "Add")}({ev});");
        }
        sb.AppendLine($"{ind}    }}");
        sb.AppendLine($"{ind}}}");
        return v;
    }

    /// <summary>"T[n]" for an array type, also for arrays of arrays ("T[n][]").</summary>
    static string ArrayNew(IArrayTypeSymbol a, string n)
    {
        var e = a.ElementType;
        string suffix = "";
        while (e is IArrayTypeSymbol inner) { suffix += "[]"; e = inner.ElementType; }
        return $"{Fq(e)}[{n}]{suffix}";
    }

    /// <summary>Readonly collection / array / state fields: filled in place (their identity and comparers stay).</summary>
    static void EmitReadInPlace(StringBuilder sb, ITypeSymbol t, string e, Ctx c, string ind)
    {
        if (IsState(t, c)) { sb.AppendLine($"{ind}r.ReadInto({e});"); return; }
        sb.AppendLine($"{ind}{{");
        if (t is IArrayTypeSymbol)
        {
            string v = EmitRead(sb, t, 0, c, ind + "    ");
            sb.AppendLine($"{ind}    if ({v} != null && {e} != null) global::System.Array.Copy({v}, {e}, global::System.Math.Min({v}.Length, {e}.Length));");
        }
        else EmitRead(sb, t, 0, c, ind + "    ", e);
        sb.AppendLine($"{ind}}}");
    }
}
