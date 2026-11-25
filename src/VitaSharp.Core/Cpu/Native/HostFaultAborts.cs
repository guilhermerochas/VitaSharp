using System.Runtime.InteropServices;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Data aborts of native code found by the host's memory protection, at no cost on the path that does not abort
/// (<see cref="DataAborts"/> describes the guards it replaces). This is the fastmem fault handling of Dolphin
/// (unchecked accesses, a host fault looked up by host pc in a table of access sites), Ryujinx (a host fault handler
/// in front of the runtime's, chained to it) and FEX/Box64 (per-block metadata from the host pc to the guest state).
/// <list type="bullet">
/// <item>Memory: an armed space keeps every page no block maps inaccessible (<see cref="GuestMemory.ArmHostFaults"/>):
/// private pages decommitted, the shared regions' views protected.</item>
/// <item>Code: an instruction with accesses carries no guard; its data abort exit (the state of the instruction's start,
/// as the guard's exit stores it) is laid out after the block, and the block registers the host code range of the
/// instruction's accesses with it (<see cref="IrBuilder.FaultExits"/>, <see cref="X64Emitter.FaultSites"/>).</item>
/// <item>Fault: a vectored handler in front of the lazy commit handler takes access violations whose RIP is in a
/// registered range and whose guest page is unmapped in the mapping table: it stores the fault's address and kind in the
/// thread's state (rbx) and resumes at the exit, which leaves the block with <see cref="ExitReason.DataAbort"/>. A
/// fault on a mapped or let-through page goes on to the lazy commit handler, as before.</item>
/// </list>
/// Windows x64 for now (the VEH front, the x64 emitter's out-of-line exits). Elsewhere, and in the interpreter, the IL
/// backend and callouts, the explicit guards stay.
/// </summary>
public static unsafe partial class HostFaultAborts
{
    /// <summary>Host faults replace the guards on this host (Windows x64 native backend; VITASHARP_DATA_ABORT_FAULTS=0
    /// keeps the guards).</summary>
    public static bool Supported { get; } = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        && Environment.GetEnvironmentVariable(Util.EnvVars.DataAbortFaults) != "0";

    private static readonly Lock s_lock = new();
    private static readonly List<(ulong Start, ulong End, ulong Stub)> s_sites = new();
    private static byte* s_filter;
    private static nint s_handle;

    /// <summary>Data aborts entered through a host fault (statistics, tests).</summary>
    public static long Entered;

    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, void* handler);

    [LibraryImport("kernel32.dll")]
    private static partial uint RemoveVectoredExceptionHandler(nint handle);

    /// <summary>Installs the fault handler (once).</summary>
    public static void EnsureInstalled()
    {
        if (!Supported)
        {
            return;
        }

        lock (s_lock)
        {
            if (s_filter != null)
            {
                return;
            }

            ExecutableMemory code = NativeRuntime.Instance.Code;
            ulong lo = (ulong)code.ExecBase, hi = lo + ExecutableMemory.ReserveSize;
            s_filter = code.Place(rip => X64Emitter.EmitFaultFilter(rip, lo, hi, (ulong)(delegate* unmanaged<void*, int>)&OnFault));
            s_handle = AddVectoredExceptionHandler(1, s_filter);
        }
    }

    /// <summary>A lazy commit handler was installed after the fault handler (a new address space): the fault handler goes
    /// back in front of it, so it sees an access to an unmapped page before the page is committed.</summary>
    public static void KeepInFront()
    {
        lock (s_lock)
        {
            if (s_handle == 0)
            {
                return;
            }

            RemoveVectoredExceptionHandler(s_handle);
            s_handle = AddVectoredExceptionHandler(1, s_filter);
        }
    }

    /// <summary>The fault sites of a block placed at <paramref name="code"/> (offsets from it).</summary>
    public static void Register(ulong code, List<(uint Start, uint End, uint Stub)> sites)
    {
        if (sites.Count == 0)
        {
            return;
        }

        lock (s_lock)
        {
            foreach ((uint start, uint end, uint stub) in sites)
            {
                s_sites.Add((code + start, code + end, code + stub));
            }
        }
    }

    /// <summary>The code arena from <paramref name="address"/> on was reused (<see cref="ExecutableMemory.Reset"/>).</summary>
    public static void ForgetFrom(ulong address)
    {
        lock (s_lock)
        {
            s_sites.RemoveAll(site => site.Start >= address);
        }
    }

    /// <summary>The exit a fault at <paramref name="rip"/> enters (0: none). The newest site wins.</summary>
    internal static ulong StubAt(ulong rip)
    {
        lock (s_lock)
        {
            for (int i = s_sites.Count - 1; i >= 0; i--)
            {
                (ulong start, ulong end, ulong stub) = s_sites[i];
                if (rip >= start && rip < end)
                {
                    return stub;
                }
            }
        }

        return 0;
    }

    // CONTEXT (x64): the general registers from rax at 0x78 (rbx = the CpuState, r15 = the guest base), rip at 0xF8;
    // EXCEPTION_RECORD: code at 0, ExceptionInformation[0] (1 = write) at 0x20, [1] (the address) at 0x28
    private const int ContextRbx = 0x78 + 8 * 3, ContextR15 = 0x78 + 8 * 15, ContextRip = 0xF8;

    /// <summary>
    /// The vectored handler (behind the native filter, only for RIPs in the code arena): -1 (continue at the exit) for a
    /// data abort, 0 (continue the search) for anything else.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int OnFault(void* pointers)
    {
        byte* record = *(byte**)pointers;
        byte* context = ((byte**)pointers)[1];
        if (*(uint*)record != 0xC0000005)
        {
            return 0;
        }

        ulong stub = StubAt(*(ulong*)(context + ContextRip));
        if (stub == 0)
        {
            return 0;
        }

        byte* guestBase = *(byte**)(context + ContextR15);
        ulong offset = *(ulong*)(record + 0x28) - (ulong)guestBase;
        if (offset > uint.MaxValue)
        {
            return 0;
        }

        uint address = (uint)offset;
        byte* table = GuestMemory.MappedTable(guestBase);
        if (table[address >> 12] != 0 && (address & 0xFFF) >= 0x1000 - 16 && address < 0xFFFF_F000 && table[(address >> 12) + 1] == 0)
        {
            address = ((address >> 12) + 1) << 12; // an access running into the next page, reported at its start
        }

        if (table[address >> 12] != 0)
        {
            return 0; // a mapped or let-through page: the lazy commit handler commits it
        }

        CpuState* s = *(CpuState**)(context + ContextRbx);
        s->AbortAddr = address;
        s->AbortStatus = DataAborts.TranslationFault | (*(ulong*)(record + 0x20) == 1 ? DataAborts.WriteNotRead : 0);
        *(ulong*)(context + ContextRip) = stub;
        Interlocked.Increment(ref Entered);
        return -1;
    }
}
