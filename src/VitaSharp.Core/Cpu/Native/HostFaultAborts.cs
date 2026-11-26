using System.Runtime.InteropServices;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Data aborts of native code found by the host's memory protection, at no cost on the path that does not abort
/// (<see cref="DataAborts"/> describes the guards it replaces). This is the fastmem fault handling of Dolphin
/// (unchecked accesses, a host fault looked up by host pc in a table of access sites), Ryujinx (a host fault handler
/// generated per OS and architecture, in front of the runtime's and chained to it) and FEX/Box64 (per-block metadata
/// from the host pc to the guest state).
/// <list type="bullet">
/// <item>Memory: an armed space keeps every page no block maps inaccessible (<see cref="GuestMemory.ArmHostFaults"/>):
/// Windows decommits private pages and protects the shared regions' views; Linux and Android mprotect them.</item>
/// <item>Code: an instruction with accesses carries no guard; its data abort exit (the state of the instruction's start,
/// as the guard's exit stores it) is laid out after the block, and the block registers the host code range of the
/// instruction's accesses with it (<see cref="IrBuilder.FaultExits"/>, <see cref="X64Emitter.FaultSites"/>,
/// <see cref="Arm64Emitter.FaultSites"/>).</item>
/// <item>Fault: a handler in front of the runtime's takes access faults whose pc is in a registered range and whose guest
/// page is unmapped in the mapping table: it stores the fault's address and kind in the thread's state (the emitters'
/// state register) and resumes at the exit, which leaves the block with <see cref="ExitReason.DataAbort"/>. Any other
/// fault on a guest page (a mapped or let-through page, host code touching the page) makes the page accessible and
/// resumes, as without data abort handlers; anything else goes on to the runtime's handler.</item>
/// </list>
/// Hosts: Windows x64 (a vectored handler: native filter + managed <see cref="OnFault"/>, and the lazy commit handler);
/// Linux and Android on ARM64 and Linux on x86-64, with 4 KB host pages (a SIGSEGV handler in machine code,
/// <see cref="HostFaultSignals"/>), opt-in with VITASHARP_DATA_ABORT_FAULTS=1 until confirmed on a device / Linux host.
/// Elsewhere (16 KB host pages: Apple silicon, some Android kernels; macOS x64), and in the interpreter, the IL backend and callouts, the explicit
/// guards stay.
/// </summary>
public static unsafe partial class HostFaultAborts
{
    private static readonly string? s_mode = Environment.GetEnvironmentVariable(Util.EnvVars.DataAbortFaults);

    /// <summary>Windows x64: the vectored handler (on unless VITASHARP_DATA_ABORT_FAULTS=0).</summary>
    private static bool WindowsX64 => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 && s_mode != "0";

    /// <summary>Linux or Android on ARM64 with 4 KB pages: the signal handler (VITASHARP_DATA_ABORT_FAULTS=1).</summary>
    private static bool LinuxArm64 => (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        && Environment.SystemPageSize == (int)GuestMemory.PageSize && s_mode == "1";

    /// <summary>Linux on x86-64 with 4 KB pages: the signal handler (VITASHARP_DATA_ABORT_FAULTS=1).</summary>
    private static bool LinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        && Environment.SystemPageSize == (int)GuestMemory.PageSize && s_mode == "1";

    /// <summary>Host faults replace the guards on this host (the guest windows are armed for them).</summary>
    public static bool Supported { get; } = WindowsX64 || LinuxArm64 || LinuxX64;

    /// <summary>The ARM64 backend lays out fault exits instead of guards (an ARM64 host with <see cref="Supported"/>;
    /// tests set it to run the fault exits in <c>Arm64Sandbox</c>, which emulates the signal handler).</summary>
    public static bool Arm64FaultExits { get; internal set; } = LinuxArm64;

    /// <summary>The x64 backend lays out fault exits instead of guards.</summary>
    public static bool X64FaultExits { get; } = WindowsX64 || LinuxX64;

    private static readonly Lock s_lock = new();
    private static byte* s_filter;
    private static nint s_handle;

    // the fault sites, (start, end, exit) host addresses: a native array the signal handler reads (append only between
    // resets; a bigger array is published before the count that covers it)
    private static ulong* s_sites;
    private static int s_siteCapacity, s_siteCount;

    /// <summary>Data aborts entered through a host fault (statistics, tests).</summary>
    public static long Entered;

    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, void* handler);

    [LibraryImport("kernel32.dll")]
    private static partial uint RemoveVectoredExceptionHandler(nint handle);

    /// <summary>Installs the fault handler (once); false when it is not there (the guards stay).</summary>
    public static bool EnsureInstalled()
    {
        if (!Supported)
        {
            return false;
        }

        lock (s_lock)
        {
            if (WindowsX64)
            {
                if (s_filter != null)
                {
                    return true;
                }

                ExecutableMemory code = NativeRuntime.Instance.Code;
                ulong lo = (ulong)code.ExecBase, hi = lo + ExecutableMemory.ReserveSize;
                s_filter = code.Place(rip => X64Emitter.EmitFaultFilter(rip, lo, hi, (ulong)(delegate* unmanaged<void*, int>)&OnFault));
                s_handle = AddVectoredExceptionHandler(1, s_filter);
                return s_handle != 0;
            }

            PublishSites();
            return HostFaultSignals.EnsureInstalled();
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
            if (s_siteCount + sites.Count > s_siteCapacity)
            {
                int capacity = Math.Max(1024, Math.Max(s_siteCapacity * 2, s_siteCount + sites.Count));
                ulong* bigger = (ulong*)NativeMemory.Alloc((nuint)capacity * 3, sizeof(ulong));
                if (s_sites != null)
                {
                    Buffer.MemoryCopy(s_sites, bigger, (long)capacity * 3 * sizeof(ulong), (long)s_siteCount * 3 * sizeof(ulong));
                }

                // the old array stays allocated: a signal handler may still be reading it
                s_sites = bigger;
                s_siteCapacity = capacity;
                PublishSites();
            }

            foreach ((uint start, uint end, uint stub) in sites)
            {
                ulong* entry = s_sites + 3 * s_siteCount;
                entry[0] = code + start;
                entry[1] = code + end;
                entry[2] = code + stub;
                Volatile.Write(ref s_siteCount, s_siteCount + 1);
            }

            PublishSites();
        }
    }

    /// <summary>The code arena from <paramref name="address"/> on was reused (<see cref="ExecutableMemory.Reset"/>).</summary>
    public static void ForgetFrom(ulong address)
    {
        lock (s_lock)
        {
            int kept = 0;
            for (int i = 0; i < s_siteCount; i++)
            {
                if (s_sites[3 * i] < address)
                {
                    s_sites[3 * kept] = s_sites[3 * i];
                    s_sites[3 * kept + 1] = s_sites[3 * i + 1];
                    s_sites[3 * kept + 2] = s_sites[3 * i + 2];
                    kept++;
                }
            }

            Volatile.Write(ref s_siteCount, kept);
            PublishSites();
        }
    }

    /// <summary>Gives the signal handler the current site array and count (Linux/Android).</summary>
    private static void PublishSites()
    {
        if (!Supported || WindowsX64)
        {
            return;
        }

        HostFaultSignals.PublishSites(s_sites, s_siteCount);
    }

    /// <summary>Diagnostics: the registered sites within 4 KB of <paramref name="pc"/>.</summary>
    internal static string SitesNear(ulong pc)
    {
        var sb = new System.Text.StringBuilder();
        lock (s_lock)
        {
            sb.Append($"{s_siteCount} sites");
            for (int i = 0; i < s_siteCount; i++)
            {
                ulong* entry = s_sites + 3 * i;
                if (entry[0] + 0x1000 > pc && entry[0] < pc + 0x1000)
                {
                    sb.Append($"; [{entry[0]:X}, {entry[1]:X}) -> {entry[2]:X}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>The exit a fault at <paramref name="pc"/> enters (0: none). The newest site wins.</summary>
    internal static ulong StubAt(ulong pc)
    {
        lock (s_lock)
        {
            for (int i = s_siteCount - 1; i >= 0; i--)
            {
                ulong* entry = s_sites + 3 * i;
                if (pc >= entry[0] && pc < entry[1])
                {
                    return entry[2];
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// What the handlers do with a fault at <paramref name="pc"/> on <paramref name="faultAddress"/> in the guest window
    /// whose guest address 0 is at <paramref name="guestBase"/>: the exit to enter (and the fault's address and status,
    /// as the state gets them) when the pc is a registered site and the page is unmapped; 0 otherwise. The managed copy of
    /// the signal handler's logic (the Windows handler, the ARM64 sandbox of the tests).
    /// </summary>
    internal static ulong Classify(ulong pc, byte* guestBase, ulong faultAddress, bool write, out uint address, out uint status)
    {
        address = status = 0;
        ulong offset = faultAddress - (ulong)guestBase;
        if (offset > uint.MaxValue)
        {
            return 0;
        }

        ulong stub = StubAt(pc);
        if (stub == 0)
        {
            return 0;
        }

        address = (uint)offset;
        byte* table = GuestMemory.MappedTable(guestBase);
        if (table[address >> 12] != 0 && (address & 0xFFF) >= 0x1000 - 16 && address < 0xFFFF_F000 && table[(address >> 12) + 1] == 0)
        {
            address = ((address >> 12) + 1) << 12; // an access running into the next page, reported at its start
        }

        if (table[address >> 12] != 0)
        {
            return 0; // a mapped or let-through page: it is made accessible
        }

        status = DataAborts.TranslationFault | (write ? DataAborts.WriteNotRead : 0);
        Interlocked.Increment(ref Entered);
        return stub;
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

        ulong stub = Classify(*(ulong*)(context + ContextRip), *(byte**)(context + ContextR15), *(ulong*)(record + 0x28),
            *(ulong*)(record + 0x20) == 1, out uint address, out uint status);
        if (stub == 0)
        {
            return 0;
        }

        CpuState* s = *(CpuState**)(context + ContextRbx);
        s->AbortAddr = address;
        s->AbortStatus = status;
        *(ulong*)(context + ContextRip) = stub;
        return -1;
    }

    /// <summary>A new guest window (address space): the signal handler knows its range.</summary>
    public static void AddWindow(byte* guestBase)
    {
        if (Supported && !WindowsX64)
        {
            HostFaultSignals.AddWindow(guestBase);
        }
    }
}
