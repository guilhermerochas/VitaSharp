using Iced.Intel;
using System.Runtime.InteropServices;
using VitaSharp.Core.Memory;
using static Iced.Intel.AssemblerRegisters;
using static VitaSharp.Core.Cpu.Native.Arm64Assembler;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// The SIGSEGV handler of host fault data aborts on Linux and Android ARM64 (<see cref="HostFaultAborts"/>): machine code
/// (a signal handler cannot enter the runtime), installed in front of the runtime's handler (CoreCLR's or Mono's) with
/// the kernel's rt_sigaction and chained to it, as Ryujinx's generated signal handler is. It reads a data block the
/// managed side keeps current (the code arena, the guest windows, the fault sites):
/// <list type="bullet">
/// <item>a fault outside every guest window goes to the previous handler (a null reference, a stack overflow);</item>
/// <item>a fault at a pc in a fault site on a page the mapping table has unmapped: the fault's address and status (WnR
/// from the ESR record of the signal frame) go into the state (x19, the emitter's state register) and the pc to the
/// site's exit;</item>
/// <item>any other fault in a guest window (a mapped or let-through page, host code touching an unmapped one) makes the
/// host page read/write (mprotect) and resumes: the access then runs as without data abort handlers.</item>
/// </list>
/// Signal frame layout (Linux arm64, glibc and bionic alike): siginfo si_addr at 16; ucontext uc_mcontext at 176:
/// fault_address, x0-x30 from 184, sp at 432, pc at 440, the extension records (__reserved) from 464. The handler uses
/// only x0-x17 (x18 is the platform register, the shadow call stack on Android).
/// Only a device run confirms it (the tests run the same logic in the ARM64 sandbox, <see cref="HostFaultAborts.Classify"/>).
/// </summary>
public static unsafe partial class HostFaultSignals
{
    // data block (8-byte words)
    private const int ArenaLo = 0, ArenaHi = 8, WindowCount = 16, Sites = 24, SiteCount = 32, OldHandler = 40,
        OldFlags = 48, OldRestorer = 56, OldMask = 64, HostPage = 72, DefaultAction = 80, Windows = 128;

    private const int MaxWindows = 64, BlockSize = Windows + 8 * MaxWindows;

    // Linux arm64 signal frame
    private const int SiAddr = 16, Mcontext = 176, Regs = Mcontext + 8, Pc = Mcontext + 264, Reserved = Mcontext + 288;
    private const uint EsrMagic = 0x45535201;

    // Linux x86-64 signal frame: siginfo si_addr at 16; uc_mcontext.gregs from 40 (rbx = 11, rip = 16, err = 19)
    private const int GregRbx = 40 + 8 * 11, GregRip = 40 + 8 * 16, GregErr = 40 + 8 * 19;

    // Linux system calls (arm64 / x86-64) and flags
    private static readonly bool s_x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static int SysRtSigaction => s_x64 ? 13 : 134;
    private const int SysMprotect = 226, SysMprotectX64 = 10, SysRtSigactionArm64 = 134, SysRtSigactionX64 = 13, Sigsegv = 11;
    private const ulong SaSiginfo = 4, SaOnstack = 0x0800_0000, SaRestorer = 0x0400_0000;

    private static byte* s_block;
    private static bool s_installed;

    /// <summary>Kernel struct sigaction (arm64): handler, flags, restorer, mask (8 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KernelSigaction
    {
        public ulong Handler, Flags, Restorer, Mask;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number, long a, void* b, void* c, long d);

    private static byte* Block
    {
        get
        {
            if (s_block == null)
            {
                s_block = (byte*)NativeMemory.AllocZeroed(BlockSize);
                *(ulong*)(s_block + HostPage) = (ulong)Environment.SystemPageSize;
            }

            return s_block;
        }
    }

    /// <summary>Installs the handler once (the guest windows that exist are added first); false when it could not be.</summary>
    internal static bool EnsureInstalled()
    {
        if (s_installed)
        {
            return true;
        }

        if (GuestMemory.Instance is { } memory)
        {
            foreach (AddressSpace space in memory.Spaces)
            {
                AddWindow(space.Base);
            }
        }

        byte* block = Block;
        ExecutableMemory code = NativeRuntime.Instance.Code;
        *(ulong*)(block + ArenaLo) = (ulong)code.ExecBase;
        *(ulong*)(block + ArenaHi) = (ulong)code.ExecBase + ExecutableMemory.ReserveSize;
        uint abortAddr = (uint)StateLayout.Of(nameof(CpuState.AbortAddr)), abortStatus = (uint)StateLayout.Of(nameof(CpuState.AbortStatus));
        byte* handler = s_x64
            ? code.Place(rip => EmitHandlerX64(rip, (ulong)block, abortAddr, abortStatus, (ulong)GuestMemory.MappedTableOffset))
            : code.Place(_ => EmitHandler((ulong)block, abortAddr, abortStatus, (ulong)GuestMemory.MappedTableOffset));
        KernelSigaction action = default, old = default;
        if (syscall(SysRtSigaction, Sigsegv, null, &old, 8) != 0)
        {
            Util.Log.Warn("Data aborts: the current SIGSEGV action could not be read; the guards stay");
            return false;
        }

        if (s_x64 && (old.Flags & SaRestorer) == 0)
        {
            // x86-64 returns from a handler through its restorer only: without the runtime's there is none to reuse
            Util.Log.Warn("Data aborts: the current SIGSEGV action has no restorer; the guards stay");
            return false;
        }

        *(ulong*)(block + OldHandler) = old.Handler;
        *(ulong*)(block + OldFlags) = old.Flags;
        *(ulong*)(block + OldRestorer) = old.Restorer;
        *(ulong*)(block + OldMask) = old.Mask;
        action.Handler = (ulong)handler;
        action.Flags = SaSiginfo | SaOnstack | (old.Flags & SaRestorer);
        action.Restorer = old.Restorer;
        action.Mask = 0;
        if (syscall(SysRtSigaction, Sigsegv, &action, null, 8) != 0)
        {
            Util.Log.Warn("Data aborts: the SIGSEGV handler could not be installed; the guards stay");
            return false;
        }

        s_installed = true;
        Util.Log.Info("Data aborts: host faults through a SIGSEGV handler (in front of the runtime's)");
        return true;
    }

    /// <summary>A guest window the handler checks faults against.</summary>
    internal static void AddWindow(byte* guestBase)
    {
        byte* block = Block;
        ulong count = *(ulong*)(block + WindowCount);
        for (ulong i = 0; i < count; i++)
        {
            if (((ulong*)(block + Windows))[i] == (ulong)guestBase)
            {
                return;
            }
        }

        if (count >= MaxWindows)
        {
            return;
        }

        ((ulong*)(block + Windows))[count] = (ulong)guestBase;
        Volatile.Write(ref *(ulong*)(block + WindowCount), count + 1);
    }

    /// <summary>The fault sites the handler searches (the array first, then the count that covers it).</summary>
    internal static void PublishSites(ulong* sites, int count)
    {
        byte* block = Block;
        *(ulong*)(block + Sites) = (ulong)sites;
        Volatile.Write(ref *(ulong*)(block + SiteCount), (ulong)count);
    }

    private const int X0 = 0, X1 = 1, X2 = 2, X3 = 3, X4 = 4, X5 = 5, X6 = 6, X7 = 7, X8 = 8, X9 = 9, X10 = 10, X11 = 11,
        X12 = 12, X13 = 13, X14 = 14, X15 = 15, X16 = 16, X17 = 17;

    /// <summary>The handler (x0 = signal, x1 = siginfo, x2 = ucontext); see the class summary.</summary>
    public static byte[] EmitHandler(ulong block, uint abortAddrOffset, uint abortStatusOffset, ulong mappedTableOffset)
    {
        var a = new Arm64Assembler();
        int chain = a.NewLabel(), stray = a.NewLabel(), loop = a.NewLabel(), found = a.NewLabel(), siteLoop = a.NewLabel();
        int esrLoop = a.NewLabel(), esrNext = a.NewLabel(), esrDone = a.NewLabel(), dfl = a.NewLabel();
        a.LdrLit(X9, block);
        a.LdrX(X3, X1, SiAddr);                 // the fault address
        a.LdrX(X4, X2, Pc);                     // the faulting pc
        // the guest window holding the address: x7 = its guest address 0, x8 = the offset in it
        a.LdrX(X5, X9, WindowCount);
        a.AddImm(X6, X9, Windows, x: true);
        a.Bind(loop);
        a.Cbz(X5, chain, x: true);
        a.LdrX(X7, X6, 0);
        a.AddImm(X6, X6, 8, x: true);
        a.Sub(X8, X3, X7, x: true);
        a.MovImm64(X10, 1UL << 32);
        a.Cmp(X8, X10, x: true);
        a.BCond(Cond.LO, found);
        a.SubImm(X5, X5, 1, x: true);
        a.B(loop);
        a.Bind(found);
        // the page's mapping-table byte (the table sits below guest address 0)
        a.LsrImm(X11, X8, 12, x: true);
        a.LdrLit(X12, mappedTableOffset);
        a.Sub(X12, X7, X12, x: true);
        a.LdStReg(LsKind.Ldrb, X13, X12, X11, xIndex: true);
        a.Cbnz(X13, stray);                     // mapped or let through: make the page accessible
        a.LdrX(X14, X9, ArenaLo);
        a.Cmp(X4, X14, x: true);
        a.BCond(Cond.LO, stray);                // host code touching the page
        a.LdrX(X14, X9, ArenaHi);
        a.Cmp(X4, X14, x: true);
        a.BCond(Cond.HS, stray);
        // the fault site of the pc, newest first: x16 = the entry (start, end, exit)
        a.LdrX(X14, X9, Sites);
        a.LdrX(X15, X9, SiteCount);
        a.Bind(siteLoop);
        a.Cbz(X15, stray, x: true);             // generated code without a site (a guarded block): as without handlers
        a.SubImm(X15, X15, 1, x: true);
        a.Add(X16, X14, X15, Shift.Lsl, 4, x: true);
        a.Add(X16, X16, X15, Shift.Lsl, 3, x: true);
        a.LdrX(X10, X16, 0);
        a.Cmp(X4, X10, x: true);
        a.BCond(Cond.LO, siteLoop);
        a.LdrX(X10, X16, 8);
        a.Cmp(X4, X10, x: true);
        a.BCond(Cond.HS, siteLoop);
        a.LdrX(X10, X16, 16);                   // the exit
        // the fault into the state (x19 of the interrupted code): the address, a translation fault, WnR from the ESR
        a.LdrX(X11, X2, Regs + 8 * 19);
        a.Str(X8, X11, (int)abortAddrOffset);
        a.MovImm(X12, DataAborts.TranslationFault);
        a.AddImm(X13, X2, Reserved, x: true);
        a.MovImm(X15, 16);                      // records looked at, at most
        a.Bind(esrLoop);
        a.Ldr(X16, X13, 0);                     // magic
        a.Cbz(X16, esrDone);
        a.MovImm(X17, EsrMagic);
        a.Cmp(X16, X17);
        a.BCond(Cond.NE, esrNext);
        a.LdrX(X16, X13, 8);                    // ESR
        a.Tbz(X16, 6, esrDone);                 // ISS.WnR
        a.OrrImm(X12, X12, DataAborts.WriteNotRead);
        a.B(esrDone);
        a.Bind(esrNext);
        a.Ldr(X16, X13, 4);                     // size
        a.Cbz(X16, esrDone);
        a.Add(X13, X13, X16, x: true);
        a.SubImm(X15, X15, 1, x: true);
        a.Cbnz(X15, esrLoop, x: true);
        a.Bind(esrDone);
        a.Str(X12, X11, (int)abortStatusOffset);
        a.StrX(X10, X2, Pc);                    // resume at the exit
        a.Ret();
        // a guest page touched otherwise: read/write again (mprotect of the host page), then the access runs again
        a.Bind(stray);
        a.Mov(X13, X0, x: true);
        a.Mov(X14, X1, x: true);
        a.Mov(X15, X2, x: true);
        a.LdrX(X10, X9, HostPage);
        a.SubImm(X11, X10, 1, x: true);
        a.Bic(X0, X3, X11, x: true);           // the host page of the address
        a.Mov(X1, X10, x: true);
        a.MovImm(X2, 3);                        // PROT_READ | PROT_WRITE
        a.MovImm(X8, (uint)SysMprotect);
        a.Emit(0xD4000001);                     // svc #0
        a.Mov(X16, X0, x: true);
        a.Mov(X0, X13, x: true);
        a.Mov(X1, X14, x: true);
        a.Mov(X2, X15, x: true);
        a.Cbnz(X16, chain, x: true);
        a.Ret();
        // not ours: the previous handler, with the signal's arguments
        a.Bind(chain);
        a.LdrX(X10, X9, OldHandler);
        a.CmpImm(X10, 1, x: true);
        a.BCond(Cond.LS, dfl);                  // SIG_DFL / SIG_IGN
        a.Br(X10);
        // the default action again; returning faults again and takes it
        a.Bind(dfl);
        a.AddImm(X1, X9, DefaultAction, x: true);
        a.MovImm(X2, 0);
        a.MovImm(X3, 8);
        a.MovImm(X8, (uint)SysRtSigactionArm64);
        a.Emit(0xD4000001);                     // svc #0 (x0 = the signal)
        a.Ret();
        return a.Finish();
    }

    /// <summary>
    /// The x86-64 handler (System V: rdi = signal, rsi = siginfo, rdx = ucontext), the same logic as
    /// <see cref="EmitHandler"/>: rbx of the interrupted code is the state, the page fault error code (bit 1) the write.
    /// </summary>
    public static byte[] EmitHandlerX64(ulong rip, ulong block, uint abortAddrOffset, uint abortStatusOffset, ulong mappedTableOffset)
    {
        var a = new Assembler(64);
        Label chain = a.CreateLabel(), stray = a.CreateLabel(), loop = a.CreateLabel(), found = a.CreateLabel();
        Label siteLoop = a.CreateLabel(), dfl = a.CreateLabel(), leave = a.CreateLabel();
        a.push(rbx);
        a.push(r12);
        a.push(r13);
        a.push(r14);
        a.push(r15);
        a.mov(r9, block);
        a.mov(rax, __qword_ptr[rsi + SiAddr]);    // the fault address
        a.mov(r10, __qword_ptr[rdx + GregRip]);   // the faulting rip
        a.mov(rcx, __qword_ptr[r9 + WindowCount]);
        a.lea(r8, __qword_ptr[r9 + Windows]);
        a.mov(r12, 1UL << 32);
        a.Label(ref loop);
        a.test(rcx, rcx);
        a.jz(chain);
        a.mov(r11, __qword_ptr[r8]);
        a.add(r8, 8);
        a.mov(rbx, rax);
        a.sub(rbx, r11);                          // the offset in the window
        a.cmp(rbx, r12);
        a.jb(found);
        a.dec(rcx);
        a.jmp(loop);
        a.Label(ref found);
        a.mov(r13, rbx);
        a.shr(r13, 12);
        a.mov(r14, r11);
        a.mov(r15, mappedTableOffset);
        a.sub(r14, r15);
        a.movzx(r15d, __byte_ptr[r14 + r13]);     // the page's mapping-table byte
        a.test(r15d, r15d);
        a.jnz(stray);
        a.cmp(r10, __qword_ptr[r9 + ArenaLo]);
        a.jb(stray);
        a.cmp(r10, __qword_ptr[r9 + ArenaHi]);
        a.jae(stray);
        a.mov(r14, __qword_ptr[r9 + Sites]);
        a.mov(rcx, __qword_ptr[r9 + SiteCount]);
        a.Label(ref siteLoop);
        a.test(rcx, rcx);
        a.jz(stray);
        a.dec(rcx);
        a.lea(r13, __qword_ptr[rcx + rcx * 2]);
        a.shl(r13, 3);
        a.add(r13, r14);                          // the entry (start, end, exit)
        a.cmp(r10, __qword_ptr[r13]);
        a.jb(siteLoop);
        a.cmp(r10, __qword_ptr[r13 + 8]);
        a.jae(siteLoop);
        a.mov(r13, __qword_ptr[r13 + 16]);        // the exit
        a.mov(r15, __qword_ptr[rdx + GregRbx]);   // the state
        a.mov(__dword_ptr[r15 + (int)abortAddrOffset], ebx);
        a.mov(r11, __qword_ptr[rdx + GregErr]);
        a.and(r11d, 2);                           // the page fault error code's W bit
        a.shl(r11d, 10);                          // -> WnR (bit 11)
        a.or(r11d, (int)DataAborts.TranslationFault);
        a.mov(__dword_ptr[r15 + (int)abortStatusOffset], r11d);
        a.mov(__qword_ptr[rdx + GregRip], r13);  // resume at the exit
        a.jmp(leave);
        // a guest page touched otherwise: read/write again (mprotect of the host page), then the access runs again
        a.Label(ref stray);
        a.mov(r12, rdi);
        a.mov(r13, rsi);
        a.mov(r14, rdx);
        a.mov(r15, __qword_ptr[r9 + HostPage]);
        a.mov(rdi, rax);
        a.mov(rcx, r15);
        a.dec(rcx);
        a.not(rcx);
        a.and(rdi, rcx);
        a.mov(rsi, r15);
        a.mov(edx, 3);                            // PROT_READ | PROT_WRITE
        a.mov(eax, SysMprotectX64);
        a.syscall();
        a.mov(rdi, r12);
        a.mov(rsi, r13);
        a.mov(rdx, r14);
        a.test(rax, rax);
        a.jnz(chain);
        a.Label(ref leave);
        a.pop(r15);
        a.pop(r14);
        a.pop(r13);
        a.pop(r12);
        a.pop(rbx);
        a.ret();
        // not ours: the previous handler, with the signal's arguments
        a.Label(ref chain);
        a.mov(rax, __qword_ptr[r9 + OldHandler]);
        a.cmp(rax, 1);
        a.jbe(dfl);
        a.pop(r15);
        a.pop(r14);
        a.pop(r13);
        a.pop(r12);
        a.pop(rbx);
        a.jmp(rax);
        // the default action again; returning faults again and takes it
        a.Label(ref dfl);
        a.lea(rsi, __qword_ptr[r9 + DefaultAction]);
        a.xor(edx, edx);
        a.mov(r10d, 8);
        a.mov(eax, SysRtSigactionX64);
        a.syscall();
        a.jmp(leave);
        using var ms = new MemoryStream();
        a.Assemble(new StreamCodeWriter(ms), rip);
        return ms.ToArray();
    }

    /// <summary>Tests: the data block with the code arena's bounds (no handler installed).</summary>
    internal static byte* PrepareBlockForTests()
    {
        byte* block = Block;
        ExecutableMemory code = NativeRuntime.Instance.Code;
        *(ulong*)(block + ArenaLo) = (ulong)code.ExecBase;
        *(ulong*)(block + ArenaHi) = (ulong)code.ExecBase + ExecutableMemory.ReserveSize;
        return block;
    }

    /// <summary>Tests: the handler a fault that is not ours goes to.</summary>
    internal static void SetPreviousHandlerForTests(ulong handler) => *(ulong*)(Block + OldHandler) = handler;
}
