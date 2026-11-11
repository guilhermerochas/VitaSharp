using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Loader;

/// <summary>
/// The user's own code patches, applied after the main module is loaded: &lt;data&gt;/patches/&lt;TITLEID&gt;.txt with lines
/// "ADDRESS:VALUE" (hex; 4 hex digits = halfword, 8 = word) and "#" comments. Addresses are absolute (the main module
/// loads at its fixed Vita address). The emulator itself has no per-title patches (it is generic: a timing difference
/// is fixed in the emulator, for every program).
/// </summary>
public static class TitlePatches
{
    public static void Apply(string titleId, string? patchDir)
    {
        if (patchDir == null)
        {
            return;
        }

        string file = Path.Combine(patchDir, titleId + ".txt");
        if (!File.Exists(file))
        {
            return;
        }

        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(':');
            if (parts.Length != 2)
            {
                Log.Warn($"patch {file}: bad line '{raw}'");
                continue;
            }
            try
            {
                uint addr = Convert.ToUInt32(parts[0], 16), value = Convert.ToUInt32(parts[1], 16);
                Write(addr, value, parts[1].Trim().Length <= 4 ? 2 : 4, Path.GetFileName(file));
            }
            catch (FormatException)
            {
                Log.Warn($"patch {file}: bad line '{raw}'");
            }
        }
    }

    private static void Write(uint addr, uint value, int size, string source)
    {
        if (size == 2)
        {
            GuestMemory.Write(addr, (ushort)value);
        }
        else
        {
            GuestMemory.W32(addr, value);
        }

        Log.Info($"Applied patch {addr:X8} <- {value:X} ({source})");
    }
}
