namespace VitaSharp.Desktop;

/// <summary>
/// memdiff BEFORE AFTER: finds float3 positions that jumped between two --dump-mem snapshots, and for each new value
/// whether it already existed elsewhere before the jump (the source of a teleport / copy).
/// </summary>
static class MemDiff
{
    static Dictionary<uint, byte[]> Load(string path)
    {
        var map = new Dictionary<uint, byte[]>();
        using var r = new BinaryReader(File.OpenRead(path));
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            uint a = r.ReadUInt32(), n = r.ReadUInt32();
            map[a] = r.ReadBytes((int)n);
        }
        return map;
    }

    static bool Plausible(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && Math.Abs(x) < 20000 && Math.Abs(y) < 20000 && Math.Abs(z) < 20000
        && Math.Abs(x) + Math.Abs(y) + Math.Abs(z) > 2 && (Math.Abs(x) > 1e-3 || x == 0) && (Math.Abs(y) > 1e-3 || y == 0) && (Math.Abs(z) > 1e-3 || z == 0);

    public static int Run(string beforePath, string afterPath, float minJump)
    {
        var before = Load(beforePath);
        var after = Load(afterPath);
        // values that became the ARM default NaN or an infinity (results of 0/0, x/0, inf-inf...)
        int nans = 0;
        foreach (var (addr, a) in after)
        {
            if (!before.TryGetValue(addr, out var b) || b.Length != a.Length) continue;
            for (int i = 0; i + 4 <= a.Length; i += 4)
            {
                uint av = BitConverter.ToUInt32(a, i), bv = BitConverter.ToUInt32(b, i);
                bool bad = av is 0x7FC00000 or 0x7F800000 or 0xFF800000;
                if (bad && av != bv && nans++ < 200) Console.WriteLine($"NAN {addr + (uint)i:X8}: {bv:X8} ({BitConverter.UInt32BitsToSingle(bv)}) -> {av:X8}");
            }
        }
        Console.WriteLine($"{nans} values became NaN/Inf");
        var cands = new List<(uint Addr, float[] B, float[] A)>();
        foreach (var (addr, a) in after)
        {
            if (!before.TryGetValue(addr, out var b) || b.Length != a.Length) continue;
            for (int i = 0; i + 12 <= a.Length; i += 4)
            {
                float ax = BitConverter.ToSingle(a, i), ay = BitConverter.ToSingle(a, i + 4), az = BitConverter.ToSingle(a, i + 8);
                float bx = BitConverter.ToSingle(b, i), by = BitConverter.ToSingle(b, i + 4), bz = BitConverter.ToSingle(b, i + 8);
                if (!Plausible(ax, ay, az) || !Plausible(bx, by, bz)) continue;
                float d = MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));
                if (d < minJump) continue;
                cands.Add((addr + (uint)i, [bx, by, bz], [ax, ay, az]));
                i += 8; // do not report overlapping windows of the same vector
            }
        }
        Console.WriteLine($"{cands.Count} jumped float3 values (>= {minJump})");

        // where did the new values come from? look for each new triple anywhere in the before snapshot
        var wanted = new Dictionary<(uint, uint, uint), List<uint>>();
        foreach (var c in cands) wanted.TryAdd(Key(c.A), new List<uint>());
        foreach (var (addr, b) in before)
            for (int i = 0; i + 12 <= b.Length; i += 4)
            {
                var k = (BitConverter.ToUInt32(b, i), BitConverter.ToUInt32(b, i + 4), BitConverter.ToUInt32(b, i + 8));
                if (wanted.TryGetValue(k, out var list) && list.Count < 8) list.Add(addr + (uint)i);
            }

        foreach (var c in cands.OrderByDescending(c => wanted[Key(c.A)].Count > 0).ThenBy(c => c.Addr))
        {
            var src = wanted[Key(c.A)];
            Console.WriteLine($"{c.Addr:X8}: ({c.B[0]:F2}, {c.B[1]:F2}, {c.B[2]:F2}) -> ({c.A[0]:F2}, {c.A[1]:F2}, {c.A[2]:F2})"
                              + (src.Count > 0 ? "   new value existed before at " + string.Join(" ", src.Select(s => s.ToString("X8"))) : ""));
        }
        return 0;

        static (uint, uint, uint) Key(float[] v) => (BitConverter.SingleToUInt32Bits(v[0]), BitConverter.SingleToUInt32Bits(v[1]), BitConverter.SingleToUInt32Bits(v[2]));
    }
}
