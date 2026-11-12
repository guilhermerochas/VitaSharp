using System.Text;
using System.Text.RegularExpressions;

namespace VitaSharp.FormatTool;

/// <summary>
/// The layout rules `dotnet format` cannot apply by itself (docs/agents/regras.md, "Código legível"): no statement
/// block written on one line (`if (x) { a; }`, `catch (E) { Log(); }`, `void M() { a; b; }`), while auto-property
/// accessor lists stay on one line (`{ get; set; }`). Shared by build/FormatTool (format-check --fix) and
/// tests/VitaSharp.Tests/OneLineBlockTests (the check).
/// </summary>
public static partial class SourceLayout
{
    /// <summary>Longest line a folded accessor list may produce.</summary>
    public const int MaxLineLength = 120;

    private const string Accessor =
        @"(?:\[[^\]]*\]\s*)?(?:(?:public|private|protected|internal)\s+)*(?:readonly\s+)?(?:get|set|init)\s*(?:;|=>[^;{}]*;)";

    [GeneratedRegex(@"\{([^{}]*;[^{}]*)\}")]
    private static partial Regex StatementBlock();

    [GeneratedRegex(@"^\s*(?:" + Accessor + @"\s*)+$")]
    private static partial Regex AccessorList();

    [GeneratedRegex(Accessor)]
    private static partial Regex OneAccessor();

    [GeneratedRegex(@"^(if|for|foreach|while|switch|using|lock|fixed|unsafe|checked|unchecked|catch|try|finally|do|else|case|return|var|new)\b")]
    private static partial Regex StatementStart();

    [GeneratedRegex(@"\b(class|struct|interface|enum|record|namespace|delegate|event)\b")]
    private static partial Regex TypeDeclaration();

    /// <summary>The source with string/char literal contents and comments replaced by '_' (same length, line breaks
    /// kept), so braces and semicolons inside them are not code.</summary>
    public static string Mask(string src)
    {
        StringBuilder sb = new(src);
        int n = src.Length;
        int i = 0;

        void Blank(int from, int to)
        {
            for (int k = from; k < Math.Min(to, n); k++)
            {
                if (sb[k] != '\n' && sb[k] != '\r')
                {
                    sb[k] = '_';
                }
            }
        }

        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int j = src.IndexOf('\n', i);
                j = j < 0 ? n : j;
                Blank(i, j);
                i = j;
                continue;
            }

            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int j = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? n : j + 2;
                Blank(i, j);
                i = j;
                continue;
            }

            int p = i;
            bool verbatim = false;
            while (p < n && (src[p] == '$' || src[p] == '@'))
            {
                verbatim |= src[p] == '@';
                p++;
            }

            if (p < n && src[p] == '"')
            {
                int quotes = 0;
                while (p + quotes < n && src[p + quotes] == '"')
                {
                    quotes++;
                }

                if (quotes >= 3)
                {
                    string fence = new('"', quotes);
                    int end = src.IndexOf(fence, p + quotes, StringComparison.Ordinal);
                    end = end < 0 ? n : end;
                    Blank(p + quotes, end);
                    i = end + quotes;
                    continue;
                }

                int k = p + 1;
                while (k < n)
                {
                    if (verbatim && src[k] == '"' && k + 1 < n && src[k + 1] == '"')
                    {
                        k += 2;
                        continue;
                    }

                    if (src[k] == '"' || (!verbatim && src[k] == '\n'))
                    {
                        break;
                    }

                    k += !verbatim && src[k] == '\\' ? 2 : 1;
                }

                Blank(p + 1, k);
                i = k + 1;
                continue;
            }

            if (c == '\'')
            {
                int k = i + 1;
                while (k < n && k < i + 12 && src[k] != '\'' && src[k] != '\n')
                {
                    k += src[k] == '\\' ? 2 : 1;
                }

                if (k < n && src[k] == '\'')
                {
                    Blank(i + 1, k);
                    i = k + 1;
                    continue;
                }
            }

            i++;
        }

        return sb.ToString();
    }

    /// <summary>True when a masked line holds a statement block written on one line (not an accessor list, not a
    /// lambda body `=> { ...; }`).</summary>
    public static bool HasOneLineBlock(string maskedLine)
    {
        foreach (Match match in StatementBlock().Matches(maskedLine))
        {
            string before = maskedLine[..match.Index].TrimEnd();
            if (!before.EndsWith("=>", StringComparison.Ordinal) && !AccessorList().IsMatch(match.Groups[1].Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The one-line statement blocks of a source, as (line number, line).</summary>
    public static IEnumerable<(int Line, string Text)> OneLineBlocks(string source)
    {
        string[] masked = Mask(source).Split('\n');
        string[] lines = source.Split('\n');
        for (int i = 0; i < masked.Length; i++)
        {
            if (HasOneLineBlock(masked[i]))
            {
                yield return (i + 1, lines[i].Trim());
            }
        }
    }

    /// <summary>True when the block opened at <paramref name="open"/> holds statements (a ';' outside parentheses
    /// before its matching '}', also inside nested blocks), as opposed to an initializer, a pattern or an accessor
    /// list.</summary>
    private static bool IsStatementBlock(string masked, int open)
    {
        int depth = 0;
        int parens = 0;
        bool statement = false;
        for (int k = open + 1; k < masked.Length; k++)
        {
            char c = masked[k];
            if (c is '(' or '[')
            {
                parens++;
            }
            else if (c is ')' or ']')
            {
                parens--;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    return statement && !AccessorList().IsMatch(masked[(open + 1)..k]);
                }

                depth--;
            }
            else if (c == ';' && parens == 0)
            {
                statement = true;
            }
        }

        return false;
    }

    /// <summary>Splits one line at the braces and statements of its statement blocks (the indentation is the
    /// formatter's job afterwards). A trailing `// comment` moves to the first line.</summary>
    public static List<string> BreakLine(string line, string masked)
    {
        string indent = line[..(line.Length - line.TrimStart().Length)];
        string comment = "";
        int commentAt = masked.Length;
        int slash = line.IndexOf("//", StringComparison.Ordinal);
        while (slash >= 0)
        {
            if (masked[slash] == '_' && masked[slash..].Trim('_', ' ', '\r').Length == 0)
            {
                commentAt = slash;
                comment = " " + line[slash..].TrimEnd('\r').Trim();
                break;
            }

            slash = line.IndexOf("//", slash + 2, StringComparison.Ordinal);
        }

        StringBuilder sb = new();
        Stack<bool> blocks = new();
        int parens = 0;
        for (int k = 0; k < commentAt; k++)
        {
            char c = masked[k];
            if (c is '(' or '[')
            {
                parens++;
            }
            else if (c is ')' or ']')
            {
                parens--;
            }

            if (c == '{')
            {
                bool statements = IsStatementBlock(masked, k);
                blocks.Push(statements);
                if (statements)
                {
                    sb.Append("\n{\n");
                    continue;
                }
            }
            else if (c == '}' && blocks.Count > 0)
            {
                if (blocks.Pop())
                {
                    sb.Append("\n}\n");
                    continue;
                }
            }
            else if (c == ';' && parens == 0 && blocks.Count > 0 && blocks.Peek())
            {
                sb.Append(";\n");
                continue;
            }

            sb.Append(line[k]);
        }

        List<string> result = [];
        foreach (string part in sb.ToString().Split('\n'))
        {
            string s = part.Trim();
            if (s.Length == 0)
            {
                continue;
            }

            // `}` followed by `)`, `,` or `;` stays on the `}` line: `}, done));`
            if (result.Count > 0 && result[^1].Trim() == "}" && s[0] is ')' or ',' or ';')
            {
                result[^1] += s;
                continue;
            }

            result.Add(indent + s);
        }

        if (comment.Length > 0 && result.Count > 0)
        {
            result[0] += comment;
        }

        return result;
    }

    /// <summary>Folds the accessor lists spread over several lines back to `Name { get; set; }` when the result fits
    /// in <see cref="MaxLineLength"/> columns.</summary>
    public static List<string> FoldAccessors(IReadOnlyList<string> lines)
    {
        List<string> output = [];
        int i = 0;
        while (i < lines.Count)
        {
            string line = lines[i];
            if (i + 3 < lines.Count && TryFold(lines, i, out string folded, out int next))
            {
                output.Add(folded);
                i = next;
                continue;
            }

            output.Add(line);
            i++;
        }

        return output;
    }

    private static bool TryFold(IReadOnlyList<string> lines, int i, out string folded, out int next)
    {
        folded = "";
        next = i;
        string line = lines[i];
        string indent = line[..(line.Length - line.TrimStart().Length)];
        string s = line.Trim();
        if (s.Length == 0 || lines[i + 1] != indent + "{" || s.EndsWith(';') || s.EndsWith('{') || s.EndsWith('}')
            || s.EndsWith(')') || s.EndsWith(',') || s.EndsWith("=>", StringComparison.Ordinal)
            || s.StartsWith("//", StringComparison.Ordinal) || s.StartsWith('#') || StatementStart().IsMatch(s)
            || TypeDeclaration().IsMatch(s))
        {
            return false;
        }

        List<string> accessors = [];
        int k = i + 2;
        while (k < lines.Count && AccessorList().IsMatch(lines[k]))
        {
            foreach (Match match in OneAccessor().Matches(lines[k]))
            {
                accessors.Add(match.Value.Trim());
            }

            k++;
        }

        if (accessors.Count == 0 || k >= lines.Count || !lines[k].StartsWith(indent + "}", StringComparison.Ordinal))
        {
            return false;
        }

        string close = lines[k].Trim();
        if (close != "}" && !close.StartsWith("} =", StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = $"{line.TrimEnd()} {{ {string.Join(" ", accessors)} }}{close[1..]}";
        // a long initializer after the accessors (`} = [...]`) was a long line already: fold it anyway
        if (candidate.Length > MaxLineLength && close == "}")
        {
            return false;
        }

        folded = candidate;
        next = k + 1;
        return true;
    }

    /// <summary>Applies <see cref="BreakLine"/> to every line with a one-line statement block, then
    /// <see cref="FoldAccessors"/>. Returns the new text and how many lines changed.</summary>
    public static (string Text, int Changes) Fix(string text)
    {
        bool bom = text.Length > 0 && text[0] == '﻿';
        string body = bom ? text[1..] : text;
        string nl = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = body.Split(nl);
        string[] masked = Mask(body.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n');
        if (lines.Length != masked.Length)
        {
            return (text, 0);
        }

        List<string> broken = [];
        int changes = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (HasOneLineBlock(masked[i]))
            {
                broken.AddRange(BreakLine(lines[i], masked[i]));
                changes++;
            }
            else
            {
                broken.Add(lines[i]);
            }
        }

        List<string> folded = FoldAccessors(broken);
        changes += broken.Count - folded.Count;
        string result = string.Join(nl, folded);
        return ((bom ? "﻿" : "") + result, changes);
    }
}
