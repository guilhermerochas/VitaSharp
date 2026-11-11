using VitaSharp.FormatTool;

// build/format-check.ps1 -Fix / build/format-check.sh --fix: after `dotnet format`, splits the statement blocks
// written on one line and folds the accessor lists back to one line (SourceLayout). Usage: FormatTool <repo root>.
// The folders listed in build/format-pending.txt are skipped.
if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: FormatTool <repository root>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string[] pending = File.ReadAllLines(Path.Combine(root, "build", "format-pending.txt"))
    .Select(l => l.Split('#')[0].Trim())
    .Where(l => l.Length > 0)
    .Select(l => l.Replace('/', Path.DirectorySeparatorChar))
    .ToArray();
string[] outputs = ["obj", "bin", "obj-dev", "bin-dev"];
int files = 0;
int changes = 0;
foreach (string top in new[] { "src", "tests", "tools", "build" })
{
    string dir = Path.Combine(root, top);
    if (!Directory.Exists(dir))
    {
        continue;
    }

    foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(root, file);
        string[] parts = rel.Split(Path.DirectorySeparatorChar);
        if (parts.Any(outputs.Contains) || pending.Any(p => rel.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
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
}

Console.WriteLine($"format-tool: {changes} lines changed in {files} files");
return 0;
