using VitaSharp.FormatTool;

// build/format-check.ps1 -Fix / build/format-check.sh --fix: after `dotnet format`, splits the statement blocks
// written on one line and folds the accessor lists back to one line (SourceLayout). Usage: FormatTool <repo root>
// <file list>: the list is the scripts' dotnet format response file (--include, then one repository-relative path per
// line: the tracked .cs files outside build/format-pending.txt and .claude/), so nothing else is ever rewritten.
if (args.Length != 2 || !Directory.Exists(args[0]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("usage: FormatTool <repository root> <file list>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string[] list = File.ReadAllLines(args[1])
    .Select(l => l.Trim())
    .Where(l => l.Length > 0 && !l.StartsWith('-'))
    .ToArray();
int files = 0;
int changes = 0;
foreach (string rel in list)
{
    string file = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    if (!rel.EndsWith(".cs", StringComparison.Ordinal) || !File.Exists(file))
    {
        continue;
    }

    byte[] bytes = File.ReadAllBytes(file);
    bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    string text = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
    (string fixedText, int n) = SourceLayout.Fix(text);
    if (n > 0 && fixedText != text)
    {
        // the file keeps its encoding: UTF-8, with a BOM only when it had one
        File.WriteAllText(file, fixedText, new System.Text.UTF8Encoding(bom));
        files++;
        changes += n;
    }
}

Console.WriteLine($"format-tool: {changes} lines changed in {files} files");
return 0;
