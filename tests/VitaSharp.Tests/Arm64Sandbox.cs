using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Runs code from the ARM64 backend on an x64 host: Unicorn (QEMU's AArch64 model) executes the generated code
/// against the process's real memory. The guest address space (with the dirty-page table below it) and the code arena
/// are mapped into the emulator at their host addresses with uc_mem_map_ptr; any other host page the code touches
/// (the CpuState, link slots, the exclusive-monitor generation table, literal-pool targets) is mapped on demand by an
/// unmapped-access hook after VirtualQuery confirms it is committed. Callouts go to a stub address whose code hook
/// calls the real callout bridge (<see cref="NativeRuntime.CalloutAddress"/>), so interpreter handlers run natively
/// on the same state. The trampoline, epilogue, linking and dispatch-table code are the backend's own.
/// </summary>
public sealed unsafe partial class Arm64Sandbox : IDisposable
{
    const int UC_ARCH_ARM64 = 2, UC_MODE_ARM = 0, UC_PROT_ALL = 7, UC_PROT_READ = 1, UC_PROT_EXEC = 4;
    const int UC_HOOK_CODE = 4, UC_HOOK_MEM_UNMAPPED = 112;
    const int RegX0 = 199, RegX30 = 2, RegSp = 4, RegPc = 260;
    public const ulong CalloutStub = 0x1000, ReturnStub = 0x2000;
    const ulong Page = 0x1000;

    nint _uc;
    readonly HashSet<ulong> _pages = new();
    readonly byte* _stack;
    const int StackSize = 256 << 10;
    readonly ulong _tramp;
    public readonly Arm64Emitter.Runtime Stubs;
    /// <summary>The sandbox's own indirect-exit table (the process table holds x64 entries).</summary>
    public readonly NativeRuntime.DispatchEntry* Dispatch;
    public long Instructions => _hits;
    long _hits;
    string? _fault;

    [ThreadStatic] static Arm64Sandbox? _current;

    public static string? SkipReason =>
        UnicornAvailability.SkipReason ?? (OperatingSystem.IsWindows() ? null : "the ARM64 sandbox maps host memory with VirtualQuery checks (Windows only)");

    public Arm64Sandbox()
    {
        Check(uc_open(UC_ARCH_ARM64, UC_MODE_ARM, out _uc), "uc_open");
        // stub page: callout stub (RET after the hook ran the bridge) and the return address of the trampoline
        Check(uc_mem_map(_uc, CalloutStub, 0x2000, UC_PROT_ALL), "map stubs");
        uint ret = 0xD65F03C0, brk = 0xD4200000;
        Check(uc_mem_write(_uc, CalloutStub, &ret, 4), "write stub");
        Check(uc_mem_write(_uc, ReturnStub, &brk, 4), "write stub");
        // guest address space incl. the dirty-page table right below it
        ulong gb = (ulong)GuestMemory.Base - DirtyPages.TableBytes;
        MapRange(gb, 0x1_0001_0000UL + DirtyPages.TableBytes, UC_PROT_ALL);
        var code = NativeRuntime.Instance.Code;
        MapRange((ulong)code.ExecBase, ExecutableMemory.ReserveSize, UC_PROT_READ | UC_PROT_EXEC);
        _stack = (byte*)NativeMemory.AlignedAlloc(StackSize, 4096);
        MapRange((ulong)_stack, StackSize, UC_PROT_ALL);
        Dispatch = (NativeRuntime.DispatchEntry*)NativeMemory.AlignedAlloc((nuint)(sizeof(NativeRuntime.DispatchEntry) << NativeRuntime.DispatchBits), 4096);
        for (int i = 0; i < 1 << NativeRuntime.DispatchBits; i++) { Dispatch[i].Key = NativeRuntime.NoKey; Dispatch[i].Code = 0; }
        MapRange((ulong)Dispatch, (ulong)(sizeof(NativeRuntime.DispatchEntry) << NativeRuntime.DispatchBits), UC_PROT_ALL);
        int epi = 0;
        _tramp = (ulong)code.Place(_ => Arm64Emitter.EmitTrampoline((ulong)GuestMemory.Base, out epi));
        Stubs = new Arm64Emitter.Runtime { Epilogue = _tramp + (ulong)epi, Callout = CalloutStub, DispatchTable = (ulong)Dispatch, MemoryBase = (ulong)GuestMemory.Base };
        Check(uc_hook_add(_uc, out _, UC_HOOK_CODE, (void*)(delegate* unmanaged<nint, ulong, uint, void*, void>)&OnCallout, null, CalloutStub, CalloutStub), "hook callout");
        Check(uc_hook_add(_uc, out _, UC_HOOK_MEM_UNMAPPED, (void*)(delegate* unmanaged<nint, int, ulong, int, long, void*, int>)&OnUnmapped, null, 1, 0), "hook unmapped");
    }

    void MapRange(ulong addr, ulong size, int perms)
    {
        ulong lo = addr & ~(Page - 1), hi = (addr + size + Page - 1) & ~(Page - 1);
        Check(uc_mem_map_ptr(_uc, lo, (nuint)(hi - lo), (uint)perms, (void*)lo), $"map {lo:X}");
        for (ulong p = lo; p < hi && hi - lo <= 64 * Page; p += Page) _pages.Add(p); // small ranges: remember the pages
        _big.Add((lo, hi));
    }

    readonly List<(ulong Lo, ulong Hi)> _big = new();

    bool Mapped(ulong page)
    {
        if (_pages.Contains(page)) return true;
        foreach (var (lo, hi) in _big) if (page >= lo && page < hi) return true;
        return false;
    }

    /// <summary>Makes the indirect-exit table point <paramref name="pc"/>/<paramref name="thumb"/> at <paramref name="entry"/>.</summary>
    public void Publish(uint pc, bool thumb, nint entry)
    {
        var e = Dispatch + ((pc >> 1) & ((1u << NativeRuntime.DispatchBits) - 1));
        e->Key = NativeRuntime.NoKey; e->Code = entry; e->Key = pc | (thumb ? 1u : 0u);
    }

    /// <summary>Runs generated code from <paramref name="entry"/> through the trampoline until it returns.</summary>
    public void Run(CpuState* s, nint entry)
    {
        _current = this;
        _fault = null;
        ulong x0 = (ulong)s, x1 = (ulong)entry, lr = ReturnStub, sp = (ulong)(_stack + StackSize - 64);
        uc_reg_write(_uc, RegX0, &x0);
        uc_reg_write(_uc, RegX0 + 1, &x1);
        uc_reg_write(_uc, RegX30, &lr);
        uc_reg_write(_uc, RegSp, &sp);
        int err = uc_emu_start(_uc, _tramp, ReturnStub, 0, 0);
        _current = null;
        if (err != 0 || _fault != null)
        {
            ulong pc;
            uc_reg_read(_uc, RegPc, &pc);
            throw new InvalidOperationException($"ARM64 sandbox: {(_fault ?? Marshal.PtrToStringAnsi(uc_strerror(err)))} at pc={pc:X} (arena +0x{pc - (ulong)NativeRuntime.Instance.Code.ExecBase:X})");
        }
    }

    /// <summary>Calls an AAPCS64 function at <paramref name="fn"/> with two arguments (no trampoline).</summary>
    public void Call(ulong fn, ulong arg0, ulong arg1)
    {
        _current = this;
        _fault = null;
        ulong lr = ReturnStub, sp = (ulong)(_stack + StackSize - 64);
        uc_reg_write(_uc, RegX0, &arg0);
        uc_reg_write(_uc, RegX0 + 1, &arg1);
        uc_reg_write(_uc, RegX30, &lr);
        uc_reg_write(_uc, RegSp, &sp);
        int err = uc_emu_start(_uc, fn, ReturnStub, 0, 0);
        _current = null;
        if (err != 0 || _fault != null) throw new InvalidOperationException($"ARM64 sandbox: {(_fault ?? Marshal.PtrToStringAnsi(uc_strerror(err)))} at pc={Pc:X}");
    }

    public ulong Pc { get { ulong pc; uc_reg_read(_uc, RegPc, &pc); return pc; } }

    [UnmanagedCallersOnly]
    static void OnCallout(nint uc, ulong address, uint size, void* user)
    {
        ulong x0, x1, x2, x3;
        uc_reg_read(uc, RegX0, &x0); uc_reg_read(uc, RegX0 + 1, &x1); uc_reg_read(uc, RegX0 + 2, &x2); uc_reg_read(uc, RegX0 + 3, &x3);
        var bridge = (delegate* unmanaged<CpuState*, int, uint, uint, int>)NativeRuntime.CalloutAddress;
        ulong r = (uint)bridge((CpuState*)x0, (int)x1, (uint)x2, (uint)x3);
        uc_reg_write(uc, RegX0, &r);
        if (_current != null) _current._hits++;
    }

    [UnmanagedCallersOnly]
    static int OnUnmapped(nint uc, int type, ulong address, int size, long value, void* user)
    {
        var sb = _current;
        if (sb == null) return 0;
        ulong page = address & ~(Page - 1);
        ulong last = (address + (ulong)Math.Max(size, 1) - 1) & ~(Page - 1);
        for (ulong p = page; p <= last; p += Page)
        {
            if (sb.Mapped(p)) continue;
            if (!Committed(p)) { sb._fault = $"access to unmapped host address {address:X}"; return 0; }
            if (uc_mem_map_ptr(uc, p, (nuint)Page, UC_PROT_ALL, (void*)p) != 0) { sb._fault = $"map of {p:X} failed"; return 0; }
            sb._pages.Add(p);
        }
        return 1;
    }

    static bool Committed(ulong page)
    {
        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery((void*)page, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return false;
        return mbi.State == 0x1000 && (mbi.Protect & 0x01) == 0 && (mbi.Protect & 0x100) == 0; // MEM_COMMIT, not NOACCESS/GUARD
    }

    static void Check(int err, string what)
    {
        if (err != 0) throw new InvalidOperationException($"Unicorn {what}: {Marshal.PtrToStringAnsi(uc_strerror(err))}");
    }

    public void Dispose()
    {
        if (_uc != 0) { uc_close(_uc); _uc = 0; }
        NativeMemory.AlignedFree(_stack);
        NativeMemory.AlignedFree(Dispatch);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* addr, MEMORY_BASIC_INFORMATION* info, nuint length);

    [LibraryImport("unicorn")] private static partial int uc_open(int arch, int mode, out nint uc);
    [LibraryImport("unicorn")] private static partial int uc_close(nint uc);
    [LibraryImport("unicorn")] private static partial int uc_mem_map(nint uc, ulong address, nuint size, uint perms);
    [LibraryImport("unicorn")] private static partial int uc_mem_map_ptr(nint uc, ulong address, nuint size, uint perms, void* ptr);
    [LibraryImport("unicorn")] private static partial int uc_mem_write(nint uc, ulong address, void* bytes, nuint size);
    [LibraryImport("unicorn")] private static partial int uc_reg_write(nint uc, int regid, void* value);
    [LibraryImport("unicorn")] private static partial int uc_reg_read(nint uc, int regid, void* value);
    [LibraryImport("unicorn")] private static partial int uc_emu_start(nint uc, ulong begin, ulong until, ulong timeout, nuint count);
    [LibraryImport("unicorn")] private static partial nint uc_strerror(int err);
    // uc_hook_add is variadic after "end"; the fixed arguments are passed like a normal call on x64
    [LibraryImport("unicorn")] private static partial int uc_hook_add(nint uc, out nint hh, int type, void* callback, void* userData, ulong begin, ulong end);
}

/// <summary>[Fact] that is skipped when the ARM64 sandbox (Unicorn) cannot run here.</summary>
public sealed class Arm64SandboxFactAttribute : FactAttribute
{
    public Arm64SandboxFactAttribute() { if (Arm64Sandbox.SkipReason is { } r) Skip = r; }
}

/// <summary>[Theory] that is skipped when the ARM64 sandbox (Unicorn) cannot run here.</summary>
public sealed class Arm64SandboxTheoryAttribute : TheoryAttribute
{
    public Arm64SandboxTheoryAttribute() { if (Arm64Sandbox.SkipReason is { } r) Skip = r; }
}
