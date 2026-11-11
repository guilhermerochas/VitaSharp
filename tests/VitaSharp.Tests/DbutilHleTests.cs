using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// SceDbutil HLE (Dbutil.Batch13.cs) against the firmware's dbutil.suprx 3.74: each scenario builds the same object in
/// two buffers, runs the module's export on one and the HLE on the other (both through guest calls, the RMutex at the
/// object's offset made by libpaf's constructor) and compares the result and the bytes. The module comes from the
/// installed firmware (VITASHARP_DATA or %APPDATA%\VitaSharp, read only); without it the test only runs the HLE.
/// </summary>
[Collection("guest")]
public sealed class DbutilHleTests(GuestMemoryFixture fixture, ITestOutputHelper output)
{
    private static string FsRoot => Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs");

    private const string Lib = "SceDbutil";
    private const uint ObjSize = 0x120;

    private sealed class Harness : IDisposable
    {
        public readonly Emulator Emu;
        private readonly string _root;
        private readonly uint _stubs;
        private uint _next;
        public readonly bool HasModule;
        public readonly List<string> Calls = [];
        public uint Callback;

        public Harness(GuestMemoryFixture fixture)
        {
            _root = Path.Combine(Path.GetTempPath(), "vitasharp-dbutil-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "vs0", "sys", "external"));
            string src = Path.Combine(FsRoot, "vs0", "sys", "external", "dbutil.suprx");
            HasModule = File.Exists(src);
            if (HasModule)
            {
                File.Copy(src, Path.Combine(_root, "vs0", "sys", "external", "dbutil.suprx"));
            }

            Emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
            if (HasModule)
            {
                Emu.LoadModule("vs0:sys/external/dbutil.suprx");
            }

            _stubs = _next = Emu.Memory.Alloc(0x1000, "dbutil test stubs");
            Callback = Host("dbutiltest:callback", c => { Calls.Add($"cb arg={c.Gpr(0):X} code={c.Gpr(1):X}"); c.Return(0); });
            Emu.Code.Invalidate(0, uint.MaxValue);
        }

        public uint Host(string name, Action<HleContext> body)
        {
            uint a = _next;
            _next += 16;
            Linker.WriteHleStub(a, Emu.Hle.RegisterHostFunction(name, body), 0);
            Emu.Code.Invalidate(a, 16);
            return a;
        }

        public uint HleStub(uint nid, string lib)
        {
            uint a = _next;
            _next += 16;
            Linker.WriteHleStub(a, Emu.Hle.GetHleFunctionIndex(nid, 0, lib), nid);
            Emu.Code.Invalidate(a, 16);
            return a;
        }

        public uint Lle(uint nid) => Emu.Linker.TryGetFunctionExport(nid, out uint a) ? a : throw new InvalidOperationException($"{nid:X8} not exported");

        public void Run(Action<GuestThread> body)
        {
            Exception? failure = null;
            GuestThread t = Emu.Kernel.CreateThread("dbutil-test", 0, 0x10000100, 0x10000, 0, 0x70000);
            t.HostBody = th =>
            {
                try
                {
                    body(th);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            };
            t.Start(0, 0);
            Assert.True(t.WaitForExit(60_000), "test thread did not finish");
            Emu.Kernel.DeleteThread(t);
            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }

        public void Dispose()
        {
            Emu.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>An object with an RMutex at the offset the function locks (none: <paramref name="mutex"/> 0), the other words
    /// set to a pattern.</summary>
    private static uint MakeObject(Harness h, GuestThread t, uint ctorStub, uint name, uint mutex)
    {
        uint o = h.Emu.Memory.Alloc(ObjSize, "dbutil test object");
        for (uint i = 0; i < ObjSize; i += 4)
        {
            GuestMemory.W32(o + i, 0x01010101u * (i / 4 % 7));
        }

        if (mutex != 0)
        {
            t.CallGuest(ctorStub, o + mutex, name, 0);
        }

        GuestMemory.W32(o + 8, 0); // no sqlite3 handle
        GuestMemory.W32(o + 0x54, h.Callback);
        GuestMemory.W32(o + 0x58, 0x1234);
        return o;
    }

    // (label, export, extra arguments, the RMutex offset it locks (0: none), returns a value)
    private static readonly (string Name, uint Nid, uint[] Args, uint Mutex, bool HasResult)[] s_scenarios =
    [
        ("map error 0", 0xB0BF2396, [0], 0, true), ("map error 5", 0xB0BF2396, [5], 0, true),
        ("interrupt", 0x37AD6852, [], 0x98, false), ("changes", 0xD1D0D3CA, [], 0x98, true), ("total changes", 0x1FD46F4E, [], 0x98, true),
        ("e8098f5a", 0x987DE0C9, [], 0, true),
        ("set flag", 0xD48BBE90, [3, 1], 0x98, true), ("set 44", 0x3CB037F2, [0xCAFE], 0x98, true),
        ("inc 38", 0x6FF905F9, [], 0x40, true), ("inc 20", 0x5CD273F5, [], 0x28, true), ("done 30", 0x055E4365, [], 0x60, true),
        ("result done", 0x0A4E17D5, [], 0x10, false),
        ("clear 30", 0xD0B4D8F2, [], 0, true), ("is empty", 0xA0DA0E0E, [], 0, true), ("reset record", 0x59DCDE85, [], 0, false),
        ("stmt clear row", 0xBC17F930, [], 0, true),
    ];

    [Fact]
    public void MatchesTheFirmwareModule()
    {
        using var h = new Harness(fixture);
        if (!h.HasModule)
        {
            output.WriteLine("dbutil.suprx not installed: HLE only");
        }

        uint ctor = h.HleStub(0xC4BD9753, "ScePafThread");
        uint name = h.Emu.Memory.Alloc(16, "dbutil test name");
        GuestMemory.WriteCString(name, "test");
        var diffs = new List<string>();
        h.Run(t =>
        {
            foreach ((string? label, uint nid, uint[]? args, uint mutex, bool hasResult) in s_scenarios)
            {
                var results = new List<(uint R, byte[] Bytes, string Calls)>();
                foreach (bool lle in h.HasModule ? new[] { true, false } : new[] { false })
                {
                    h.Calls.Clear();
                    uint o = MakeObject(h, t, ctor, name, mutex);
                    uint fn = lle ? h.Lle(nid) : h.HleStub(nid, Lib);
                    uint r = t.CallGuest(fn, [o, .. args]);
                    if (label == "clear 30")
                    {
                        r -= o; // the object's own address comes back
                    }

                    if (!hasResult)
                    {
                        r = 0;
                    }

                    byte[] bytes = GuestMemory.ReadSpan(o, (int)ObjSize).ToArray();
                    // the RMutex words hold kernel ids that differ between the two objects
                    if (mutex != 0)
                    {
                        Array.Clear(bytes, (int)mutex, 0x20);
                    }

                    results.Add((r, bytes, string.Join(";", h.Calls)));
                }
                output.WriteLine($"{label}: r={results[^1].R:X} {results[^1].Calls}");
                if (results.Count == 2)
                {
                    if (results[0].R != results[1].R)
                    {
                        diffs.Add($"{label}: result {results[0].R:X} (module) vs {results[1].R:X} (HLE)");
                    }

                    if (!results[0].Bytes.AsSpan().SequenceEqual(results[1].Bytes))
                    {
                        diffs.Add($"{label}: object bytes differ");
                    }

                    if (results[0].Calls != results[1].Calls)
                    {
                        diffs.Add($"{label}: callbacks {results[0].Calls} vs {results[1].Calls}");
                    }
                }
            }
        });
        Assert.Empty(diffs);
    }
}
