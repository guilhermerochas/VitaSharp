namespace VitaSharp.Core.Loader;

/// <summary>
/// The plugin configuration of the homebrew environment (taiHEN's config.txt), read with the rules of taiHEN's own
/// parser (taihen-parser: lexer.c, parser.c; taiHEN plugin.c, hen.c):
/// <list type="bullet">
/// <item>taiHEN opens ux0:tai/config.txt and, only when that cannot be opened, ur0:tai/config.txt (its "recovery"
/// config): one file is used, never both.</item>
/// <item>A line is read up to CR or LF; leading and trailing whitespace (space, tab, LF, VT, FF, CR) is dropped. An
/// empty line or one starting with '#' is a comment; one starting with '*' starts a section; any other line is a module
/// path (inner spaces kept).</item>
/// <item>A section line is '*', optional whitespace, an optional '!' (halt) and optional whitespace, then the name. Names
/// compare exactly (case-sensitive): KERNEL is loaded into the kernel when taiHEN starts, any other name is a title ID,
/// matched against each process's title ID when its preloaded modules have started ("main" is SceShell), and ALL
/// matches every process but not KERNEL.</item>
/// <item>A matching section marked with '!' ends the reading at the next section line: the sections after it are not
/// read for that process.</item>
/// <item>A file with a line of <see cref="MaxLineLength"/> characters or more, a path before the first section or a
/// section without a name is refused as a whole: no plugin loads.</item>
/// <item>Paths load in file order, each on its own (one that fails does not stop the others).</item>
/// </list>
/// </summary>
public static class PluginConfig
{
    /// <summary>taihen-parser's CONFIG_MAX_LINE_LENGTH: a line of this length or longer makes the file invalid.</summary>
    public const int MaxLineLength = 256;

    /// <summary>The configuration files taiHEN tries, in order (TAIHEN_CONFIG_FILE, TAIHEN_RECOVERY_CONFIG_FILE).</summary>
    public static readonly string[] Paths = ["ux0:tai/config.txt", "ur0:tai/config.txt"];

    private const string KernelSection = "KERNEL";
    private const string AllSection = "ALL";

    private enum LineKind
    {
        Comment,
        Section,
        Path,
    }

    private readonly record struct Line(LineKind Kind, string Text, bool Halt);

    /// <summary>True when taiHEN accepts <paramref name="text"/> (taihen_config_validate).</summary>
    public static bool IsValid(string text)
    {
        return Lex(text) is { } lines && Check(lines);
    }

    /// <summary>
    /// The module paths for <paramref name="section"/> ("KERNEL", or a process's title ID), in file order; empty when
    /// the file is not valid (taihen_config_parse after taihen_config_validate).
    /// </summary>
    public static List<string> ModulesFor(string text, string section)
    {
        List<string> modules = [];
        if (Lex(text) is not { } lines || !Check(lines))
        {
            return modules;
        }

        bool recording = false;
        bool halt = false;
        foreach (Line line in lines)
        {
            if (line.Kind == LineKind.Section)
            {
                if (recording && halt)
                {
                    break;
                }

                halt = line.Halt;
                recording = (line.Text == AllSection && section != KernelSection) || line.Text == section;
            }
            else if (line.Kind == LineKind.Path && recording)
            {
                modules.Add(line.Text);
            }
        }

        return modules;
    }

    private static bool Check(List<Line> lines)
    {
        bool inSection = false;
        foreach (Line line in lines)
        {
            if (line.Kind == LineKind.Section)
            {
                if (line.Text.Length == 0)
                {
                    return false;
                }

                inSection = true;
            }
            else if (line.Kind == LineKind.Path && !inSection)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSpace(char character)
    {
        return character is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
    }

    /// <summary>The lines of <paramref name="text"/> as taiHEN's lexer classifies them; null when a line is too long.</summary>
    private static List<Line>? Lex(string text)
    {
        List<Line> lines = [];
        int position = 0;
        while (position <= text.Length)
        {
            int end = text.IndexOfAny(['\r', '\n'], position);
            if (end < 0)
            {
                end = text.Length;
            }

            if (end - position >= MaxLineLength)
            {
                return null;
            }

            string raw = text[position..end];
            position = end + 1;
            int start = 0;
            while (start < raw.Length && IsSpace(raw[start]))
            {
                start++;
            }

            int stop = raw.Length;
            while (stop > start && IsSpace(raw[stop - 1]))
            {
                stop--;
            }

            string content = raw[start..stop];
            if (content.Length == 0 || content[0] == '#')
            {
                lines.Add(new Line(LineKind.Comment, "", false));
                continue;
            }

            if (content[0] != '*')
            {
                lines.Add(new Line(LineKind.Path, content, false));
                continue;
            }

            string name = content[1..].TrimStart(' ', '\t', '\v', '\f');
            bool halt = name.StartsWith('!');
            if (halt)
            {
                name = name[1..].TrimStart(' ', '\t', '\v', '\f');
            }

            lines.Add(new Line(LineKind.Section, name, halt));
        }

        return lines;
    }
}
