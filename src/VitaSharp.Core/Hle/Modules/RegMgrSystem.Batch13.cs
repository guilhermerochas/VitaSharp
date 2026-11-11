using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

public sealed partial class RegMgrSystem
{
    /// <summary>The registry manager's region table (SceRegistryMgr 3.74, entries 1..13 of 4 bytes).</summary>
    static readonly string[] RegionTable = ["", "J1", "UC2", "AU3", "CEK", "CEL", "KR2", "E12", "TW1", "RU3", "MX2", "TOL", "BR2", "CN9"];

    /// <summary>sceRegMgrSrvCnvRegionInt(const char *region): the index of a region name in the table (1..13); a
    /// NULL pointer, a name of 4 characters or more, or an unknown one is 0x800D0004. All 4 bytes are compared (the
    /// terminator and what follows it), as the module's memcmp.</summary>
    [Hle("sceRegMgrSrvCnvRegionInt")]
    int sceRegMgrSrvCnvRegionInt(uint region)
    {
        const int Invalid = unchecked((int)0x800D0004);
        if (region == 0) return Invalid;
        var b = GuestMemory.ReadSpan(region, 4);
        if (b.IndexOf((byte)0) < 0) return Invalid;
        Span<byte> entry = stackalloc byte[4];
        for (int i = 1; i < RegionTable.Length; i++)
        {
            entry.Clear();
            System.Text.Encoding.ASCII.GetBytes(RegionTable[i], entry);
            if (b.SequenceEqual(entry)) return i;
        }
        return Invalid;
    }

    /// <summary>sceRegMgrGetInitVals(category, SceRegMgrKeys *keys, count): the factory defaults (the firmware's
    /// registry template, os0:kd/registry.db0) of the keys, in the 16-byte entries of sceRegMgrGetKeys { size 0x10,
    /// name, type, value }. A NULL category or array, or a count below 1, is 0x800D0004; an entry whose size is not 0x10
    /// 0x800D001C; a NULL name 0x800D0004; a key the template does not define 0x800D0017.</summary>
    [Hle("sceRegMgrGetInitVals")]
    int sceRegMgrGetInitVals(uint category, uint keys, int count)
    {
        const int Invalid = unchecked((int)0x800D0004), BadSize = unchecked((int)0x800D001C), NotFound = unchecked((int)0x800D0017);
        string? c = Str(category);
        if (c == null || keys == 0 || count < 1) return Invalid;
        for (int i = 0; i < count; i++)
        {
            uint k = keys + (uint)i * 16;
            if (GuestMemory.R32(k) != 0x10) return BadSize;
            string? n = Str(GuestMemory.R32(k + 4));
            if (n == null) return Invalid;
            if (Reg.Find(c, n) is not { } key) return NotFound;
            var d = key.Default;
            uint v = d.Length >= 4 ? BitConverter.ToUInt32(d, 0) : d.Length > 0 ? d[0] : 0u;
            GuestMemory.W32(k + 12, v);
        }
        return 0;
    }
}
