using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Tests;

public sealed unsafe partial class NativeJitTests
{
    /// <summary>
    /// The fast x64 encoder produces the same instruction stream as Iced's BlockEncoder: both encodings of random blocks
    /// are decoded, branch targets are compared as instruction indices (branch sizes may differ), everything else as text.
    /// </summary>
    [NativeFact]
    public void FastEncoderMatchesIced()
    {
        EnsureGuestPages();
        var rng = new Random(4242);
        var em = new X64Emitter();
        var slots = (nint*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(4096 * 8);
        long fallbacks0 = X64Emitter.FastEncodeFallbacks;
        int compared = 0;
        try
        {
            for (int it = 0; it < 1500; it++)
            {
                bool thumb = (it & 1) != 0;
                var b = it % 3 == 0 ? MediaBlock(rng, thumb) : RandomBlock(rng, thumb, 12, forUnicorn: false);
                if (b == null) continue;
                var ir = new ArmFrontend().Translate(b);
                IrPasses.DeadCode(ir);
                const ulong Rip = 0x7FF0_0000_0000;
                var stubs = new X64Emitter.Runtime { Epilogue = Rip + 0x100000, Callout = Rip + 0x200000, DispatchTable = Rip + 0x300000 };
                if (!em.Prepare(ir, stubs, k => (nint)(slots + (k & 4095)), it % 5 == 0 ? (nint)slots : 0)) continue;
                X64Emitter.FastEncode = true;
                byte[] fast = em.Assemble(Rip);
                X64Emitter.FastEncode = false;
                byte[] iced = em.Assemble(Rip);
                X64Emitter.FastEncode = true;
                Assert.Equal(Normalize(iced, Rip), Normalize(fast, Rip));
                compared++;
            }
        }
        finally { X64Emitter.FastEncode = true; System.Runtime.InteropServices.NativeMemory.Free(slots); }
        output.WriteLine($"compared {compared} blocks; fast encoder fallbacks {X64Emitter.FastEncodeFallbacks - fallbacks0}");
        Assert.Equal(0, X64Emitter.FastEncodeFallbacks - fallbacks0);

        static string Normalize(byte[] code, ulong rip)
        {
            var dec = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(code), rip);
            var list = new List<Iced.Intel.Instruction>();
            while (dec.IP < rip + (ulong)code.Length) list.Add(dec.Decode());
            var index = new Dictionary<ulong, int>();
            for (int i = 0; i < list.Count; i++) index[list[i].IP] = i;
            var fmt = new Iced.Intel.NasmFormatter();
            var sb = new System.Text.StringBuilder();
            foreach (var ins in list)
            {
                if (ins.IsJmpShortOrNear || ins.IsJccShortOrNear || ins.IsCallNear)
                {
                    string target = index.TryGetValue(ins.NearBranch64, out int ti) ? $"#{ti}" : $"{ins.NearBranch64:X}";
                    sb.Append(ins.Mnemonic).Append(' ').Append(target).Append('\n');
                }
                else
                {
                    var o = new Iced.Intel.StringOutput();
                    fmt.Format(ins, o);
                    sb.Append(o.ToStringAndReset()).Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
