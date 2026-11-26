using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// build/hle-defaults.ps1 / build/hle-defaults.sh: the functions the HLE answers with a default value.
// Usage: HleDefaultsTool <repository root> <output prefix> [--date=<yyyy-MM-dd>]
// Writes <prefix>.md and <prefix>.html (the script turns the HTML into the PDF). The set is the "default" rows of the
// committed census (docs/hle/data/vitasharp-implemented.tsv, from hle-coverage --status); the value and the reason come
// from the [Hle] bindings in src/VitaSharp.Core/Hle (the returned expression and the doc comment above the function).
if (args.Length < 2 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: HleDefaultsTool <repository root> <output prefix> [--date=<yyyy-MM-dd>]");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string prefix = Path.GetFullPath(args[1]);
string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
foreach (string a in args.Skip(2))
{
    if (a.StartsWith("--date=", StringComparison.Ordinal))
    {
        date = a["--date=".Length..];
    }
}

return DefaultsReport.Run(root, prefix, date);

internal sealed record Binding(string Class, string Name, uint Nid, string? Library, bool IsDefault, string Method, string Returns, string Value, string Comment, string File);

internal sealed record Row(uint Nid, string Name, string Module, string Library, string Value, string Category, string Reason, bool HasComment);

internal static class DefaultsReport
{
    public const string CatDrm = "DRM, licença e segurança";
    public const string CatHw = "Hardware não emulado";
    public const string CatNet = "Rede, contas e serviços online (PSN)";
    public const string CatDebug = "Depuração, medição e diagnóstico";
    public const string CatSys = "Serviço de sistema sem equivalente no emulador";
    public const string CatOther = "Outros";

    public static readonly string[] Categories = [CatDrm, CatHw, CatNet, CatDebug, CatSys, CatOther];

    private static readonly Dictionary<uint, string> Names = [];

    private const string HleFolder = "src/VitaSharp.Core/Hle";
    private const string Census = "docs/hle/data/vitasharp-implemented.tsv";

    public static int Run(string root, string prefix, string date)
    {
        string census = Path.Combine(root, Census);
        if (!File.Exists(census))
        {
            Console.Error.WriteLine($"missing {Census} (run build/hle-status first)");
            return 1;
        }

        Dictionary<string, string> consts = [];
        Dictionary<string, string> helpers = [];
        Dictionary<string, string> classDocs = [];
        List<Binding> bindings = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, HleFolder), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string[] lines = File.ReadAllLines(file);
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            CollectConsts(lines, consts);
            CollectHelpers(lines, helpers);
            CollectClassDocs(lines, classDocs);
            CollectBindings(lines, rel, bindings);
        }

        foreach (string l in File.ReadAllLines(census))
        {
            string[] cc = l.Split('\t');
            if (!l.StartsWith('#') && cc.Length >= 2)
            {
                Names[Convert.ToUInt32(cc[0], 16)] = cc[1];
            }
        }

        List<Row> rows = [];
        int unmatched = 0;
        foreach (string line in File.ReadAllLines(census))
        {
            string[] c = line.Split('\t');
            if (line.StartsWith('#') || c.Length < 6 || c[5] != "default")
            {
                continue;
            }

            uint nid = Convert.ToUInt32(c[0], 16);
            string name = c[1];
            string module = c[2];
            string library = c[3];
            Binding? b = Find(bindings, nid, name, module, library);
            if (b == null)
            {
                unmatched++;
                rows.Add(new Row(nid, name, module, library, "(?)", Categorize(name, library, module, ""), "the census (docs/hle/data/vitasharp-implemented.tsv) lists it as a default but the source binding is not a [Hle(..., Default = true)] one: regenerate the census (build/hle-status)", false));
                continue;
            }

            string value = Resolve(b, consts, helpers, bindings);
            string comment = b.Comment.Length > 0 ? b.Comment : classDocs.GetValueOrDefault(b.Class, "");
            string cat = Categorize(name, library, module, b.File, value);
            string reason = comment.Length > 0 ? comment : CategoryReason(cat);
            rows.Add(new Row(nid, name, module, library, value, cat, reason, comment.Length > 0));
        }

        Write(prefix, date, rows, unmatched);
        Console.WriteLine($"hle-defaults: {rows.Count} functions, {rows.Count(r => r.HasComment)} with a comment in the code, {unmatched} without a binding");
        return 0;
    }

    private static Binding? Find(List<Binding> bindings, uint nid, string name, string module, string library)
    {
        List<Binding> list = bindings.Where(b => b.Nid == nid || (b.Nid == 0 && b.Name == name && (b.Library == null || b.Library == library))).ToList();
        return list.FirstOrDefault(b => b.Class == module && b.IsDefault) ?? list.FirstOrDefault(b => b.IsDefault) ?? list.FirstOrDefault();
    }

    private static void CollectConsts(string[] lines, Dictionary<string, string> consts)
    {
        Regex decl = new(@"\bconst\s+(?:u?int|u?long|ushort|short|byte)\s+([^;]+);");
        Regex one = new(@"^\s*(\w+)\s*=\s*(.+?)\s*$", RegexOptions.Singleline);
        string text = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        foreach (Match m in decl.Matches(text))
        {
            foreach (string part in Regex.Split(m.Groups[1].Value, @",\s*(?=\w+\s*=)"))
            {
                Match d = one.Match(part);
                if (d.Success)
                {
                    consts.TryAdd(d.Groups[1].Value, d.Groups[2].Value.Trim());
                }
            }
        }
    }

    /// <summary>The expression-bodied methods without [Hle] (the helpers the default functions return, like Bbmc()).</summary>
    private static void CollectHelpers(string[] lines, Dictionary<string, string> helpers)
    {
        Regex m = new(@"^\s*(?:(?:private|internal|public|protected|static|readonly)\s+)*(?:u?int|bool|long|ulong)\s+(\w+)\s*\(([^)]*)\)\s*=>\s*(.+?);\s*$");
        foreach (string l in lines)
        {
            Match x = m.Match(l);
            if (x.Success && !l.TrimStart().StartsWith('['))
            {
                helpers.TryAdd(x.Groups[1].Value, x.Groups[3].Value.Trim());
            }
        }
    }

    private static void CollectClassDocs(string[] lines, Dictionary<string, string> docs)
    {
        Regex cls = new(@"\b(?:class|struct|interface)\s+(\w+)");
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = cls.Match(lines[i]);
            if (!m.Success || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            string doc = DocAbove(lines, i);
            if (doc.Length > 0)
            {
                docs.TryAdd(m.Groups[1].Value, doc);
            }
        }
    }

    /// <summary>The /// comment right above line <paramref name="index"/> (attribute lines in between are skipped).</summary>
    private static string DocAbove(string[] lines, int index)
    {
        int i = index - 1;
        while (i >= 0 && lines[i].TrimStart().StartsWith('[') && !lines[i].TrimStart().StartsWith("[Hle(", StringComparison.Ordinal))
        {
            i--;
        }

        List<string> doc = [];
        while (i >= 0 && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
        {
            doc.Insert(0, lines[i].TrimStart()[3..]);
            i--;
        }

        if (doc.Count == 0)
        {
            return "";
        }

        string text = string.Join(' ', doc);
        text = Regex.Replace(text, @"<see\s+cref=""[^""]*?([\w.]+)""\s*/>", "$1");
        text = Regex.Replace(text, @"<c>(.*?)</c>", "$1");
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ").Trim());
        return text.Length > 600 ? text[..600].TrimEnd() + "..." : text;
    }

    private static void CollectBindings(string[] lines, string file, List<Binding> bindings)
    {
        Regex cls = new(@"\b(?:class|struct|interface)\s+(\w+)");
        string current = "";
        for (int i = 0; i < lines.Length; i++)
        {
            Match cm = cls.Match(lines[i]);
            if (cm.Success && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                current = cm.Groups[1].Value;
            }

            if (!lines[i].TrimStart().StartsWith("[Hle(", StringComparison.Ordinal))
            {
                continue;
            }

            int first = i;
            List<string> attrs = [];
            string inline = "";
            while (i < lines.Length && lines[i].TrimStart().StartsWith('['))
            {
                string a = lines[i].Trim();
                while (Balance(a) > 0 && i + 1 < lines.Length)
                {
                    a += " " + lines[++i].Trim();
                }

                int close = CloseIndex(a);
                if (close >= 0 && close + 1 < a.Length && a[(close + 1)..].Trim().Length > 0)
                {
                    inline = a[(close + 1)..].Trim();
                    a = a[..(close + 1)];
                }

                attrs.Add(a);
                i++;
                if (inline.Length > 0)
                {
                    break;
                }
            }

            List<string> src = inline.Length > 0 ? [inline, .. lines.Skip(i)] : [.. lines.Skip(i)];
            StringBuilder sig = new();
            int j = 0;
            while (j < src.Count && !src[j].Contains("=>") && !src[j].Contains('{') && !src[j].TrimEnd().EndsWith(';'))
            {
                sig.Append(src[j].Trim()).Append(' ');
                j++;
            }

            if (j >= src.Count)
            {
                break;
            }

            string rest = string.Join('\n', src.Skip(j));
            string head = src[j];
            int arrow = head.IndexOf("=>", StringComparison.Ordinal);
            int brace = head.IndexOf('{');
            string signature;
            string body;
            if (arrow >= 0 && (brace < 0 || arrow < brace))
            {
                signature = sig + head[..arrow];
                body = "return " + ReadUntil(rest[(arrow + 2)..], false) + ";";
            }
            else if (brace >= 0)
            {
                signature = sig + head[..brace];
                body = ReadUntil(rest[(brace + 1)..], true);
            }
            else
            {
                signature = sig + head;
                body = "";
            }

            Match sm = Regex.Match(signature, @"(\w[\w<>\[\],? ]*?)\s+(\w+)\s*\(");
            string returns = sm.Success ? sm.Groups[1].Value.Trim().Split(' ')[^1] : "";
            string method = sm.Success ? sm.Groups[2].Value : "";
            string comment = DocAbove(lines, first);
            if (comment.Length == 0)
            {
                comment = SectionComment(lines, first);
            }

            string value = ReturnValues(body, returns);
            foreach (string a in attrs.Where(a => a.StartsWith("[Hle(", StringComparison.Ordinal)))
            {
                Match nm = Regex.Match(a, @"^\[Hle\(\s*""([^""]*)""");
                Match nid = Regex.Match(a, @"Nid\s*=\s*(0x[0-9A-Fa-f]+)");
                Match lib = Regex.Match(a, @"Library\s*=\s*""([^""]*)""");
                bool isDefault = Regex.IsMatch(a, @"Default\s*=\s*true");
                bindings.Add(new Binding(current, nm.Success ? nm.Groups[1].Value : method,
                    nid.Success ? Convert.ToUInt32(nid.Groups[1].Value, 16) : 0, lib.Success ? lib.Groups[1].Value : null,
                    isDefault, method, returns, value, comment, file));
            }

            i--;
        }
    }

    /// <summary>The index of the ']' that closes the '[' at the start of <paramref name="a"/>, or -1.</summary>
    private static int CloseIndex(string a)
    {
        int d = 0;
        bool str = false;
        for (int k = 0; k < a.Length; k++)
        {
            char ch = a[k];
            if (ch == '"' && (k == 0 || a[k - 1] != '\\'))
            {
                str = !str;
            }
            else if (!str && ch == '[')
            {
                d++;
            }
            else if (!str && ch == ']' && --d == 0)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>The nearest // comment block above a group of one-line bindings (the section's note), looking up past
    /// other bindings and blank lines, never past the end of a member.</summary>
    private static string SectionComment(string[] lines, int index)
    {
        for (int i = index - 1; i >= 0 && index - i < 80; i--)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("///", StringComparison.Ordinal))
            {
                int top = i;
                while (top - 1 >= 0 && lines[top - 1].Trim().StartsWith("//", StringComparison.Ordinal) && !lines[top - 1].Trim().StartsWith("///", StringComparison.Ordinal))
                {
                    top--;
                }

                string text = string.Join(' ', lines[top..(i + 1)].Select(l => l.Trim().TrimStart('/').Trim()));
                text = Regex.Replace(text, @"^-+\s*|\s*-+$", "").Trim();
                text = Regex.Replace(text, @"\s+", " ");
                return text.Length > 500 ? text[..500].TrimEnd() + "..." : text;
            }

            if (t == "}" || (t.EndsWith('}') && !t.Contains('{')) || t.StartsWith("///", StringComparison.Ordinal))
            {
                return "";
            }
        }

        return "";
    }

    private static int Balance(string s)
    {
        int d = 0;
        bool str = false;
        for (int k = 0; k < s.Length; k++)
        {
            char ch = s[k];
            if (ch == '"' && (k == 0 || s[k - 1] != '\\'))
            {
                str = !str;
            }
            else if (!str && ch is '[' or '(')
            {
                d++;
            }
            else if (!str && ch is ']' or ')')
            {
                d--;
            }
        }

        return d;
    }

    /// <summary>The text up to the ';' (expression body) or the closing '}' (block body) at depth 0.</summary>
    private static string ReadUntil(string s, bool block)
    {
        int depth = 0;
        bool str = false;
        for (int k = 0; k < s.Length; k++)
        {
            char ch = s[k];
            if (ch == '"' && (k == 0 || s[k - 1] != '\\'))
            {
                str = !str;
            }
            else if (str)
            {
                continue;
            }
            else if (ch is '(' or '{' or '[')
            {
                depth++;
            }
            else if (ch is ')' or ']')
            {
                depth--;
            }
            else if (ch == '}')
            {
                if (block && depth == 0)
                {
                    return s[..k];
                }

                depth--;
            }
            else if (!block && ch == ';' && depth <= 0)
            {
                return s[..k];
            }
        }

        return s.Length > 400 ? s[..400] : s;
    }

    private static string ReturnValues(string body, string returns)
    {
        if (returns == "void")
        {
            Match fwd = Regex.Match(body, @"Forward\(\w+,\s*(0x[0-9A-Fa-f]+)");
            return fwd.Success ? "(void):" + fwd.Groups[1].Value : "(void)";
        }

        List<string> vals = [];
        foreach (Match m in Regex.Matches(body, @"\breturn\s+([^;]+);"))
        {
            string v = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
            if (!vals.Contains(v))
            {
                vals.Add(v);
            }
        }

        return vals.Count == 0 ? "(?)" : string.Join(" / ", vals.Take(3)) + (vals.Count > 3 ? " / ..." : "");
    }

    /// <summary>Resolves the constants and casts of a returned expression to a number where it is one.</summary>
    private static string NameOfNid(uint nid)
    {
        return Names.TryGetValue(nid, out string? n) ? n : "";
    }

    private static string Resolve(Binding b, Dictionary<string, string> consts, Dictionary<string, string> helpers, List<Binding> bindings)
    {
        string v = b.Value;
        if (v.StartsWith("(void):", StringComparison.Ordinal))
        {
            uint twin = Convert.ToUInt32(v[7..], 16);
            Binding? other = bindings.FirstOrDefault(x => x.Nid == twin && !x.Value.StartsWith("(void)", StringComparison.Ordinal))
                ?? bindings.FirstOrDefault(x => x.Nid == 0 && !x.Value.StartsWith("(void)", StringComparison.Ordinal) && x.Name == NameOfNid(twin));
            return other == null ? "(repassa para o NID 0x" + twin.ToString("X8", CultureInfo.InvariantCulture) + ")" : "via " + other.Name + ": " + Resolve(other, consts, helpers, bindings);
        }

        if (v is "(void)" or "(?)")
        {
            return v == "(void)" ? "(sem retorno)" : "(expressão não lida)";
        }

        return string.Join(" / ", v.Split(" / ").Select(p => ResolveOne(p, consts, helpers)));
    }

    private static string ResolveOne(string p, Dictionary<string, string> consts, Dictionary<string, string> helpers)
    {
        string t = Strip(p);
        Match call = Regex.Match(t, @"^(\w+)\(.*\)$");
        if (call.Success && helpers.TryGetValue(call.Groups[1].Value, out string? helper) && !helper.Contains(call.Groups[1].Value + "(", StringComparison.Ordinal))
        {
            return t + " = " + ResolveOne(helper, consts, helpers);
        }

        if (consts.TryGetValue(t, out string? c) && c != t)
        {
            return t + " = " + Num(c);
        }

        string num = Num(t);
        if (num != t)
        {
            return num;
        }

        return p.Length > 120 ? p[..120] + "..." : p;
    }

    private static string Strip(string s)
    {
        string t = s.Trim();
        for (int n = 0; n < 3; n++)
        {
            t = Regex.Replace(t, @"^unchecked\(\((?:u?int|long)\)\s*(.+)\)$", "$1").Trim();
            t = Regex.Replace(t, @"^\((?:u?int|long)\)\s*", "").Trim();
        }

        return t.TrimEnd('u', 'U', 'L');
    }

    private static string Num(string s)
    {
        string t = Strip(s);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
        {
            return "0x" + h.ToString("X8", CultureInfo.InvariantCulture);
        }

        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d))
        {
            return d < 0 ? "0x" + ((uint)d).ToString("X8", CultureInfo.InvariantCulture) + " (" + d + ")" : d.ToString(CultureInfo.InvariantCulture);
        }

        return s;
    }

    private static string Categorize(string name, string library, string module, string file, string value = "")
    {
        string all = name + " " + library + " " + module + " " + file;
        if (Regex.IsMatch(all, @"Gc[A-Z]|GameCard|Bbmc|Usb|Udcd|Hdmi|Marlin|Bluetooth|Sdif|Syscon|Mtp|Motion|Grab|Dmac|Codec|IdStorage|Krm|GpuEs4|Camera|Wlan|Bt[A-Z]|Sulpha|Pervasive", RegexOptions.IgnoreCase))
        {
            return CatHw;
        }

        if (value.Contains("0x80020004", StringComparison.Ordinal) || Regex.IsMatch(all, @"Drm|Sbl|MagicGate|MgKey|MgVideo|Keystone|Rif|SecureBoot|Crypto|Cipher|Auth|Certificate|License|Licence|Aes|Rsa|Hmac|Signature|Pfs|Activation", RegexOptions.IgnoreCase))
        {
            return CatDrm;
        }

        if (Regex.IsMatch(all, @"\bNp|Np[A-Z]|Net|Http|Ssl|Sns|Trophy|Commerce|Friend|Matching|Party", RegexOptions.IgnoreCase))
        {
            return CatNet;
        }

        if (Regex.IsMatch(all, @"Debug|Perf|Pamgr|Trace|Dump|Profil", RegexOptions.IgnoreCase))
        {
            return CatDebug;
        }

        return Regex.IsMatch(all, @"Kernel|System|Shell|Vsh|Paf|AppMgr|Util|Event|Power", RegexOptions.IgnoreCase) ? CatSys : CatOther;
    }

    private static string CategoryReason(string cat)
    {
        return cat switch
        {
            CatDrm => "DRM, licença ou segurança: depende do processador seguro (F00D), de chaves do aparelho ou de uma licença que o emulador não valida; devolve o resultado documentado do firmware sem fazer nada",
            CatHw => "hardware que o emulador não tem (modem, USB, Bluetooth, cartão de jogo, sensores, codec/DMA...): devolve o resultado documentado do firmware sem fazer nada",
            CatNet => "serviço online (PSN, contas, rede) que o emulador não fornece: devolve o resultado documentado do firmware",
            CatDebug => "depuração, medição ou diagnóstico do firmware: sem efeito no jogo",
            CatSys => "serviço interno do sistema sem equivalente no emulador: devolve o resultado documentado do firmware",
            _ => "valor padrão documentado do firmware (sem comentário específico no código)"
        };
    }

    private static string TopValues(List<Row> rows)
    {
        return string.Join('\n', rows.GroupBy(r => r.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(25)
            .Select(g => g.Count() + "\t" + g.Key));
    }

    private static void Write(string prefix, string date, List<Row> rows, int unmatched)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        int withComment = rows.Count(r => r.HasComment);
        StringBuilder md = new();
        StringBuilder html = new();
        md.AppendLine("# VitaSharp: funções que respondem um valor padrão no HLE");
        md.AppendLine();
        md.AppendLine($"Gerado em {date} por build/hle-defaults (HleDefaultsTool) a partir do censo (docs/hle/data/vitasharp-implemented.tsv, linhas \"default\") e dos bindings `[Hle(..., Default = true)]` de src/VitaSharp.Core/Hle.");
        md.AppendLine();
        md.AppendLine("Uma função \"padrão\" não é implementação de verdade: precisa de hardware ou serviço que o emulador não tem e devolve o resultado documentado do firmware (sucesso ou o erro específico) sem fazer nada. A coluna Valor é o que ela devolve (expressão do código, com constantes resolvidas); a coluna Motivo é o comentário do código, ou a explicação da categoria quando não há comentário.");
        md.AppendLine();
        md.AppendLine($"Total: {rows.Count} funções ({withComment} com comentário próprio no código, {rows.Count - withComment} só com a categoria; {unmatched} sem binding achado).");
        md.AppendLine();
        md.AppendLine("## Por categoria");
        md.AppendLine();
        md.AppendLine("| Categoria | Funções |");
        md.AppendLine("|---|---:|");
        foreach (string cat in Categories)
        {
            md.AppendLine($"| {cat} | {rows.Count(r => r.Category == cat)} |");
        }

        md.AppendLine();
        md.AppendLine("## Valores devolvidos mais comuns");
        md.AppendLine();
        md.AppendLine("| Valor | Funções |");
        md.AppendLine("|---|---:|");
        foreach (string l in TopValues(rows).Split('\n'))
        {
            string[] p = l.Split('\t');
            md.AppendLine($"| `{p[1].Replace("|", "\\|")}` | {p[0]} |");
        }

        md.AppendLine();
        md.AppendLine("## Por biblioteca");
        md.AppendLine();
        foreach (var lib in rows.GroupBy(r => r.Library).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"- {lib.Key}: {lib.Count()}");
        }

        html.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>VitaSharp: funções com valor padrão</title><style>body{font:10px Arial,sans-serif;margin:12px}h1{font-size:18px}h2{font-size:14px;margin-top:18px;border-bottom:1px solid #999}table{border-collapse:collapse;width:100%;table-layout:fixed}td,th{border:1px solid #bbb;padding:2px 4px;vertical-align:top;overflow-wrap:anywhere}th{background:#eee}code{font-size:9px}</style></head><body>");
        html.Append("<h1>VitaSharp: funções que respondem um valor padrão no HLE</h1>");
        html.Append($"<p>Gerado em {date}. Total: {rows.Count} funções ({withComment} com comentário próprio no código).</p><h2>Por categoria</h2><table><tr><th>Categoria</th><th>Funções</th></tr>");
        foreach (string cat in Categories)
        {
            html.Append($"<tr><td>{E(cat)}</td><td>{rows.Count(r => r.Category == cat)}</td></tr>");
        }

        html.Append("</table><h2>Valores devolvidos mais comuns</h2><table><tr><th>Valor</th><th>Funções</th></tr>");
        foreach (string l in TopValues(rows).Split('\n'))
        {
            string[] p = l.Split('\t');
            html.Append($"<tr><td><code>{E(p[1])}</code></td><td>{p[0]}</td></tr>");
        }

        html.Append("</table>");
        md.AppendLine();
        md.AppendLine("## Funções");
        foreach (var lib in rows.GroupBy(r => r.Library).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine();
            md.AppendLine($"### {lib.Key} ({lib.Count()})");
            md.AppendLine();
            md.AppendLine("| Função | NID | Módulo | Valor | Categoria | Motivo |");
            md.AppendLine("|---|---|---|---|---|---|");
            html.Append($"<h2>{E(lib.Key)} ({lib.Count()})</h2><table><colgroup><col style=\"width:17%\"><col style=\"width:7%\"><col style=\"width:9%\"><col style=\"width:12%\"><col style=\"width:11%\"><col style=\"width:44%\"></colgroup><tr><th>Função</th><th>NID</th><th>Módulo</th><th>Valor</th><th>Categoria</th><th>Motivo</th></tr>");
            foreach (Row r in lib.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                string nid = "0x" + r.Nid.ToString("X8", CultureInfo.InvariantCulture);
                md.AppendLine($"| `{r.Name}` | {nid} | {r.Module} | `{r.Value.Replace("|", "\\|")}` | {r.Category} | {r.Reason.Replace("|", "\\|")} |");
                html.Append($"<tr><td><code>{E(r.Name)}</code></td><td>{nid}</td><td>{E(r.Module)}</td><td><code>{E(r.Value)}</code></td><td>{E(r.Category)}</td><td>{E(r.Reason)}</td></tr>");
            }

            html.Append("</table>");
        }

        html.Append("</body></html>");
        File.WriteAllText(prefix + ".md", md.ToString(), new UTF8Encoding(false));
        File.WriteAllText(prefix + ".html", html.ToString(), new UTF8Encoding(false));
    }

    private static string E(string s)
    {
        return WebUtility.HtmlEncode(s);
    }
}
