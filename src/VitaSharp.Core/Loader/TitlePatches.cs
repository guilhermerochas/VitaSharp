using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Loader;

/// <summary>
/// Per-title code patches applied after the main module is loaded, for behaviour that depends on hardware timing
/// the emulator cannot reproduce exactly. Built-in patches live here (documented); users can add
/// &lt;data&gt;/patches/&lt;TITLEID&gt;.txt with lines "ADDRESS:VALUE" (hex; 4 hex digits = halfword, 8 = word) and "#" comments.
/// Addresses are absolute (the main module loads at its fixed Vita address).
/// </summary>
public static class TitlePatches
{
    static readonly Dictionary<string, (string Module, (uint Addr, uint Value, int Size, string Why)[] Patches)> Builtin = new()
    {
        // Killzone Mercenary: the Havok step loop sub-steps physics while the measured wall time of the previous step
        // is under a 1.5 ms budget. On the Vita a step always exceeds it, so one step runs per frame; on a fast host the
        // loop runs 2-3 steps and ~2 s into a level the character is flagged as fallen through the world (SUICIDE,
        // respawn in the void). Limit the loop to one step per frame: cmp r2, r1 -> cmp r2, #1 (loop head 0x817262A4).
        // (no built-in patches: timing differences are handled by the emulator itself)
    };

    public static void Apply(string titleId, string moduleName, string? patchDir)
    {
        if (Builtin.TryGetValue(titleId, out var b) && (b.Module.Length == 0 || string.Equals(b.Module, moduleName, StringComparison.OrdinalIgnoreCase)))
            foreach (var (addr, value, size, why) in b.Patches) Write(addr, value, size, "built-in: " + why);

        if (patchDir == null) return;
        var file = Path.Combine(patchDir, titleId + ".txt");
        if (!File.Exists(file)) return;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(':');
            if (parts.Length != 2) { Log.Warn($"patch {file}: bad line '{raw}'"); continue; }
            try
            {
                uint addr = Convert.ToUInt32(parts[0], 16), value = Convert.ToUInt32(parts[1], 16);
                Write(addr, value, parts[1].Trim().Length <= 4 ? 2 : 4, Path.GetFileName(file));
            }
            catch (FormatException) { Log.Warn($"patch {file}: bad line '{raw}'"); }
        }
    }

    static void Write(uint addr, uint value, int size, string source)
    {
        if (size == 2) GuestMemory.Write(addr, (ushort)value); else GuestMemory.W32(addr, value);
        Log.Info($"Applied patch {addr:X8} <- {value:X} ({source})");
    }
}
