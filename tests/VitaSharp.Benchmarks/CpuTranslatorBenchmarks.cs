using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Benchmarks;

/// <summary>Guest code shared by the CPU benchmarks: a fixed code and data page outside the allocator regions.</summary>
static class GuestCode
{
    public const uint CodeBase = 0x0010_0000, CodeSize = 0x0060_0000, DataBase = 0x0080_0000, DataSize = 0x10000;

    public static GuestMemory Map()
    {
        var m = new GuestMemory();
        m.MapFixed(CodeBase, CodeSize, "bench code");
        m.MapFixed(DataBase, DataSize, "bench data");
        return m;
    }

    public static void Write(uint at, uint[] arm)
    {
        var code = GuestMemory.Span(at, arm.Length * 4);
        for (int i = 0; i < arm.Length; i++)
            BitConverter.GetBytes(arm[i]).CopyTo(code[(i * 4)..]);
    }
}

/// <summary>
/// Compile cost of one guest block in each CPU backend: decoding (BlockBuilder), the ARM->IL translator (IL backend:
/// DynamicMethod + the .NET JIT) and the native x64 backend (IR, passes, allocation, encoding). Blocks: fixed ALU,
/// load/store and VFP sequences, or with <see cref="EnvVars.BenchCorpus"/> the first 2,000 blocks of a corpus recorded
/// in a game (<see cref="EnvVars.NativeCorpus"/>), compiled round robin.
/// </summary>
public unsafe class CpuTranslatorBenchmarks
{
    static readonly uint[][] Fixed =
    [
        // integer ALU with a conditional op
        [0xE0810002, 0xE0403001, 0xE1A04103, 0xE0050192, 0xE1550004, 0xC0866004, 0xE2277001, 0xE1888007, 0xE0099008, 0xE12FFF1E],
        // loads and stores (word, halfword, byte, multiple)
        [0xE5910000, 0xE5912004, 0xE0800002, 0xE5810008, 0xE1D130B0, 0xE5C13010, 0xE8910030, 0xE12FFF1E],
        // VFP single precision
        [0xED910A00, 0xED911A01, 0xEE300A01, 0xEE200A01, 0xED810A02, 0xE12FFF1E],
    ];

    GuestMemory? _memory;
    readonly List<Block> _blocks = [];
    int _next, _arenaMark;

    [GlobalSetup]
    public void Setup()
    {
        _memory = GuestCode.Map();
        var corpus = Environment.GetEnvironmentVariable(EnvVars.BenchCorpus);
        uint at = GuestCode.CodeBase;
        if (corpus is { Length: > 0 } && File.Exists(corpus))
        {
            foreach (var line in File.ReadLines(corpus))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;
                bool thumb = parts[0] == "T";
                var bytes = new List<byte>();
                foreach (var p in parts.Skip(2))
                {
                    int c = p.IndexOf(':');
                    int size = int.Parse(p[..c]);
                    uint raw = Convert.ToUInt32(p[(c + 1)..], 16);
                    if (size == 2)
                        bytes.AddRange([(byte)raw, (byte)(raw >> 8)]);
                    else if (thumb)
                        bytes.AddRange([(byte)(raw >> 16), (byte)(raw >> 24), (byte)raw, (byte)(raw >> 8)]);
                    else
                        bytes.AddRange(BitConverter.GetBytes(raw));
                }
                // each block in its own 0x400 slot, followed by a branch-to-self that keeps the builder inside it
                if (bytes.Count > 0x3F0)
                    continue;
                var mem = GuestMemory.Span(at, 0x400);
                mem.Clear();
                bytes.ToArray().CopyTo(mem);
                (thumb ? new byte[] { 0xFE, 0xE7 } : [0xFE, 0xFF, 0xFF, 0xEA]).CopyTo(mem[bytes.Count..]);
                _blocks.Add(BlockBuilder.Build(at, thumb));
                at += 0x400;
                if (_blocks.Count == 2000)
                    break;
            }
        }
        if (_blocks.Count == 0)
            foreach (var b in Fixed)
            {
                GuestCode.Write(at, b);
                _blocks.Add(BlockBuilder.Build(at, false));
                at += 0x100;
            }
        _arenaMark = NativeRuntime.Instance.Code.Used;
    }

    [GlobalCleanup] public void Cleanup() => _memory?.Dispose();

    Block NextBlock()
    {
        var b = _blocks[_next];
        if (++_next == _blocks.Count)
            _next = 0;
        return b;
    }

    [Benchmark]
    public int Decode()
    {
        var b = NextBlock();
        return BlockBuilder.Build(b.Start, b.Thumb).Insts.Length;
    }

    [Benchmark]
    public object? CompileIl() => IlJitCompiler.Instance.Compile(NextBlock());

    [Benchmark]
    public object? CompileNativeX64()
    {
        var r = NativeX64Compiler.Instance.Compile(NextBlock());
        if (NativeRuntime.Instance.Code.Used > _arenaMark + (8 << 20))
            NativeRuntime.Instance.Code.Reset(_arenaMark);
        return r;
    }
}

/// <summary>
/// The dispatcher end to end (<see cref="CpuCore.Run"/>): a 4,096-iteration guest loop with a call per iteration, run by
/// the interpreter, the IL backend or the native backend once its blocks are compiled and linked (setup runs it until
/// the JIT queue drains). Per guest loop: block lookup, entering and leaving generated code, linked exits.
/// </summary>
public unsafe class CpuDispatchBenchmarks
{
    static readonly uint[] Loop =
    [
        0xE3A00000, // 00 mov r0, #0
        0xE3A01000, // 04 mov r1, #0
        0xEB000004, // 08 loop: bl func (0x20)
        0xE2811001, // 0C add r1, r1, #1
        0xE3510A01, // 10 cmp r1, #0x1000
        0xBAFFFFFB, // 14 blt loop
        0xEF000042, // 18 svc #0x42
        0xEAFFFFFE, // 1C b .
        0xE0800001, // 20 func: add r0, r0, r1
        0xE2200003, // 24 eor r0, r0, #3
        0xE12FFF1E, // 28 bx lr
    ];

    [Params("interp", "il", "native")] public string Backend = "native";

    GuestMemory? _memory;
    CpuCore? _core;
    long _oldRate;

    [GlobalSetup]
    public void Setup()
    {
        _memory = GuestCode.Map();
        GuestCode.Write(GuestCode.CodeBase, Loop);
        _oldRate = CpuCore.InstructionsPerSecond;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        IlJitCompiler.Instance.HotInstructions = 1;
        Jit.Enabled = true;
        if (!Jit.Select(Backend))
            throw new InvalidOperationException($"backend {Backend} is not supported here");
        _core = new CpuCore(new BlockCache());
        for (int i = 0; i < 8; i++)
        {
            RunLoop();
            Jit.WaitIdle(TimeSpan.FromSeconds(60));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _core?.Dispose();
        _memory?.Dispose();
        CpuCore.InstructionsPerSecond = _oldRate;
    }

    [Benchmark]
    public uint RunLoop()
    {
        var s = _core!.State;
        s->R[15] = GuestCode.CodeBase;
        s->Thumb = 0;
        s->R[13] = GuestCode.DataBase + 0xF000;
        if (_core.Run() != ExitReason.Svc)
            throw new InvalidOperationException("the loop did not reach its svc");
        return s->R[0];
    }
}
