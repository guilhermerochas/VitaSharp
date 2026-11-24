using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The handler slots of the FW 3.74 sysroot (SceSysrootForDriver/ForKernel in sysmem.skprx; decompiled, study only).
/// The sysroot is the kernel's shared directory: modules loaded later register entry points there (a function, or a
/// table of functions whose first word is its size) and other modules reach them through sysroot exports. Each export
/// here is one of a few shapes on one word of the sysroot: set or clear the word; call the function it holds with the
/// caller's arguments (returning what it returns, or a fixed value when none is registered); register a table after
/// checking its size word (SCE_KERNEL_ERROR_INVALID_ARGUMENT otherwise); call one entry of a table; read or write one
/// word of a registered table. The words are kept by sysroot offset.
/// <para>
/// On the Vita the firmware's own modules fill these slots at boot (the application manager the shell's pid, the
/// update manager the system software version, the process manager its table, ...). VitaSharp runs those modules in
/// HLE, so each slot starts with the boot default standing for the provider (<see cref="DefaultSysrootHandler"/>): the
/// provider's answer where VitaSharp knows it, otherwise 0 (logged once). A module that registers its own handler
/// replaces the default, and one that clears the slot leaves it empty as on the console (the export's own result
/// without a handler).
/// </para>
/// <para>
/// The other slots (thread, module and process managers, licence, DRM and security services, coredump) are in the
/// SysrootSlots part, the sysroot's data words and the KBL parameters in the SysrootData part.
/// </para>
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The stack words of the caller passed on to a registered handler (the firmware branches to it with the
    /// caller's stack, so a handler may read arguments past r3).</summary>
    const int SysrootStackArguments = 8;

    /// <summary>The sysroot words a module wrote, by offset; an offset not written still holds the boot default.</summary>
    [SnapshotMissing] // states of builds before it: no handler had been registered
    readonly Dictionary<int, int> _sysrootWords = new();

    /// <summary>The words written into a default table (by slot offset &lt;&lt; 16 | word offset).</summary>
    [SnapshotMissing] // states of builds before it: none had been written
    readonly Dictionary<int, int> _defaultTableWords = new();

    /// <summary>The default handlers already logged as answering 0 (once each).</summary>
    [SnapshotIgnore]
    readonly HashSet<int> _loggedDefaultHandlers = new();

    /// <summary>The word a module wrote at the offset; false while the boot default is in place.</summary>
    bool TryGetSysrootWord(uint offset, out uint value)
    {
        lock (_sysrootWords)
        {
            bool written = _sysrootWords.TryGetValue((int)offset, out int word);
            value = (uint)word;
            return written;
        }
    }

    /// <summary>The word at the offset; 0 while the boot default is in place.</summary>
    uint SysrootWord(uint offset)
    {
        TryGetSysrootWord(offset, out uint value);
        return value;
    }

    void SetSysrootWord(uint offset, uint value)
    {
        lock (_sysrootWords)
        {
            _sysrootWords[(int)offset] = (int)value;
            MirrorSysrootWord(offset, value);
        }
    }

    /// <summary>Stores r0 at the offset; the registers stay as they are (the firmware's setter returns nothing).</summary>
    void SetSysrootHandler(HleContext context, uint offset) => SetSysrootWord(offset, context.Gpr(0));

    /// <summary>Registers the table in r0 when its first word is <paramref name="size"/>: 0, or
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT for a null table or another size.</summary>
    void RegisterSysrootTable(HleContext context, uint offset, uint size)
    {
        uint table = context.Gpr(0);
        if (table == 0 || GuestMemory.R32(table) != size)
        {
            context.Return(SceError.INVALID_ARGUMENT);
            return;
        }

        SetSysrootWord(offset, table);
        context.Return(0);
    }

    /// <summary>Calls the function at the offset (the boot default while none was written); with the slot cleared the
    /// result is <paramref name="noHandler"/>, or the registers stay as they are when the export returns nothing.
    /// <paramref name="wide"/>: a 64-bit result (r0, r1).</summary>
    void CallSysrootHandler(HleContext context, uint offset, int? noHandler, bool wide = false)
    {
        if (!TryGetSysrootWord(offset, out uint function))
        {
            ReturnDefault(context, offset, NoEntry, wide);
            return;
        }

        CallSysroot(context, function, noHandler, wide);
    }

    /// <summary>The entry argument of <see cref="DefaultSysrootHandler"/> for a slot holding one function.</summary>
    const uint NoEntry = uint.MaxValue;

    /// <summary>Calls entry <paramref name="entry"/> of the table at the offset (the boot default's entry while none
    /// was registered), as <see cref="CallSysrootHandler"/> when the slot was cleared.</summary>
    void CallSysrootTableEntry(HleContext context, uint offset, uint entry, int? noHandler)
    {
        if (!TryGetSysrootWord(offset, out uint table))
        {
            ReturnDefault(context, offset, entry, wide: false);
            return;
        }

        CallSysroot(context, table == 0 ? 0 : GuestMemory.R32(table + entry), noHandler, wide: false);
    }

    void ReturnDefault(HleContext context, uint offset, uint entry, bool wide)
    {
        long value = DefaultSysrootHandler(context, offset, entry);
        if (wide)
        {
            context.Return(value);
        }
        else
        {
            context.Return((int)value);
        }
    }

    /// <summary>
    /// The boot default of a slot (and table entry): what the firmware module that fills it answers, where VitaSharp
    /// knows it. +0x32C (the application manager): the shell's pid. +0x398 (the update manager): the version of the
    /// system software package (type 1). +0x360 entry +0x40 (the process manager; ksceKernelCreateThread asks it):
    /// (pid, *sdkVersion), the SDK version of the process's main module, 0x03740011 for the kernel; entries +0x18 and
    /// +0x1C (pid, size / pointer): a process's kernel-side heap block, served by the kernel's heap (ksceKernelAlloc,
    /// ksceKernelFree). Anything else: 0, logged once.
    /// </summary>
    long DefaultSysrootHandler(HleContext context, uint offset, uint entry)
    {
        switch (offset, entry)
        {
            case (0x32C, NoEntry) when Emu.Vita.Shell is { } shell:
                return shell.Pid;
            case (0x398, NoEntry):
                return SystemSoftware.SpkgVersion(1);
            case (0x360, 0x40):
                return DefaultProcessSdkVersion(context.Gpr(0), context.Gpr(1));
            case (0x360, 0x18):
                return ksceKernelAllocThroughEntry(context.Gpr(1));
            case (0x360, 0x1C):
                return ksceKernelFreeThroughEntry(context.Gpr(1));
        }

        int key = (int)((offset << 16) | (entry & 0xFFFF));
        lock (_loggedDefaultHandlers)
        {
            if (_loggedDefaultHandlers.Add(key))
            {
                string at = entry == NoEntry ? $"+0x{offset:X}" : $"+0x{offset:X} entry +0x{entry:X}";
                Log.Info($"sysroot: default handler {at} answers 0 (its firmware provider runs in HLE)");
            }
        }

        return 0;
    }

    /// <summary>The process manager's (pid, *sdkVersion): the SDK version in the process's SceProcessParam (+0xC), the
    /// system software version without one; the kernel's pid gives 0x03740011.</summary>
    long DefaultProcessSdkVersion(uint pid, uint sdkVersion)
    {
        uint version = SystemMisc.SystemSwVersion;
        if ((int)pid != KernelSpace.KernelPid && KernelSpace.ProcessOf(Emu, (int)pid) is { } process)
        {
            uint processParam = process.Linker.ProcessParam;
            if (processParam != 0 && GuestMemory.R32(processParam + 4) == 0x32505350) // "PSP2"
            {
                version = GuestMemory.R32(processParam + 0xC);
            }
        }

        if (sdkVersion != 0)
        {
            GuestMemory.W32(sdkVersion, version);
        }

        return 0;
    }

    void CallSysroot(HleContext context, uint function, int? noHandler, bool wide)
    {
        if (function == 0)
        {
            if (noHandler is { } value)
            {
                if (wide)
                {
                    context.Return((long)value);
                }
                else
                {
                    context.Return(value);
                }
            }

            return;
        }

        Span<uint> arguments = stackalloc uint[4 + SysrootStackArguments];
        for (int slot = 0; slot < arguments.Length; slot++)
        {
            arguments[slot] = context.Gpr(slot);
        }

        uint result = context.Thread.CallGuest(function, arguments);
        context.Return(result | ((ulong)context.Thread.LastCallR1 << 32));
    }

    /// <summary>Writes word <paramref name="field"/> of the table at the offset when one is registered (into the
    /// default table while none was).</summary>
    void SetSysrootTableWord(HleContext context, uint offset, uint field, uint value)
    {
        if (!TryGetSysrootWord(offset, out uint table))
        {
            lock (_defaultTableWords)
            {
                _defaultTableWords[(int)((offset << 16) | field)] = (int)value;
            }

            return;
        }

        if (table != 0)
        {
            GuestMemory.W32(table + field, value);
        }
    }

    /// <summary>Word <paramref name="field"/> of the table at the offset (of the default table while none was
    /// registered, 0 until written); 0 with the slot cleared.</summary>
    void GetSysrootTableWord(HleContext context, uint offset, uint field)
    {
        if (!TryGetSysrootWord(offset, out uint table))
        {
            lock (_defaultTableWords)
            {
                context.Return(_defaultTableWords.GetValueOrDefault((int)((offset << 16) | field)));
            }

            return;
        }

        context.Return(table == 0 ? 0 : GuestMemory.R32(table + field));
    }

    /// <summary>NID_C94C76FA(handlers): sets the three handler words +0x40C, +0x410 and +0x414 from a 0x10-byte
    /// structure (+0 its size, then the three functions); a null structure or another size clears them.</summary>
    [Hle(Nid = 0xC94C76FA, Library = "SceSysrootForDriver")]
    void NID_C94C76FA(HleContext context)
    {
        uint handlers = context.Gpr(0);
        bool valid = handlers != 0 && GuestMemory.R32(handlers) == 0x10;
        for (uint word = 0; word < 3; word++)
        {
            SetSysrootWord(0x40C + word * 4, valid ? GuestMemory.R32(handlers + 4 + word * 4) : 0);
        }
    }

    /// <summary>
    /// The model capability word (sysroot +0x84): one bit per capability. The emulated unit is a PCH-1000 handheld,
    /// whose word has capabilities 2, 7 and 10 (libpaf keeps them as its flags 0x4A for a game program).
    /// </summary>
    internal const uint ModelCapabilities = (1u << 2) | (1u << 7) | (1u << 10);

    /// <summary>Bit <paramref name="capability"/> (its low byte, as the firmware shifts by it: 32 and above give 0) of
    /// <see cref="ModelCapabilities"/>.</summary>
    internal static int CheckModelCapability(uint capability)
    {
        int shift = (int)(capability & 0xFF);
        return shift < 32 ? (int)((ModelCapabilities >> shift) & 1) : 0;
    }

    /// <summary>ksceKernelSysrootCheckModelCapability(int capability): see <see cref="CheckModelCapability"/>.</summary>
    [Hle(Nid = 0x8AA268D6, Library = "SceSysrootForDriver")]
    int ksceKernelSysrootCheckModelCapability(uint capability) => CheckModelCapability(capability);

    /// <summary>NID_CAE820B2(pid, *sdkVersion): entry +0x40 of the process manager's table at sysroot +0x360 (the
    /// default's answer while none was registered); with the slot cleared, *sdkVersion = 0 and SCE_KERNEL_ERROR_ERROR
    /// (0x80020001).</summary>
    [Hle(Nid = 0xCAE820B2, Library = "SceSysrootForDriver")]
    void NID_CAE820B2(HleContext context)
    {
        if (!TryGetSysrootWord(0x360, out uint table) || table != 0)
        {
            CallSysrootTableEntry(context, 0x360, 0x40, null);
            return;
        }

        uint sdkVersion = context.Gpr(1);
        if (sdkVersion != 0)
        {
            GuestMemory.W32(sdkVersion, 0);
        }

        context.Return(SceError.ERROR);
    }

    /// <summary>NID_F804F761(): 0 (the FW 3.74 export does nothing else).</summary>
    [Hle(Nid = 0xF804F761, Library = "SceSysrootForDriver")]
    int NID_F804F761() => 0;

    /// <summary>NID_9A8871F1: sets the handler word at sysroot +0x328.</summary>
    [Hle(Nid = 0x9A8871F1, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x9A8871F1, Library = "SceSysrootForDriver")]
    void NID_9A8871F1(HleContext context) => SetSysrootHandler(context, 0x328);

    /// <summary>ksceKernelSysrootGetShellPid: calls the handler at sysroot +0x32C with the caller's arguments; without one, 0xffffffff.</summary>
    [Hle(Nid = 0x05093E7B, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootGetShellPid(HleContext context) => CallSysrootHandler(context, 0x32C, unchecked((int)0xffffffff));

    /// <summary>NID_E9B4221F: sets the handler word at sysroot +0x32C.</summary>
    [Hle(Nid = 0xE9B4221F, Library = "SceSysrootForDriver")]
    void NID_E9B4221F(HleContext context) => SetSysrootHandler(context, 0x32C);

    /// <summary>NID_9AF075D4: calls entry +0x24 of the handler table at sysroot +0x360; without the table, 0.</summary>
    [Hle(Nid = 0x9AF075D4, Library = "SceSysrootForDriver")]
    void NID_9AF075D4(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x24, 0);

    /// <summary>NID_1B3FA457: calls entry +0x28 of the handler table at sysroot +0x360; without the table, 0.</summary>
    [Hle(Nid = 0x1B3FA457, Library = "SceSysrootForDriver")]
    void NID_1B3FA457(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x28, 0);

    /// <summary>NID_D29BCA77: registers the handler table (+0 its size, 0x44) at sysroot +0x360.</summary>
    [Hle(Nid = 0xD29BCA77, Library = "SceSysrootForKernel")]
    void NID_D29BCA77(HleContext context) => RegisterSysrootTable(context, 0x360, 0x44);

    /// <summary>NID_B27B7530: calls the handler at sysroot +0x374 with the caller's arguments; without one, nothing.</summary>
    [Hle(Nid = 0xB27B7530, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xB27B7530, Library = "SceSysrootForDriver")]
    void NID_B27B7530(HleContext context) => CallSysrootHandler(context, 0x374, null);

    /// <summary>NID_8747D415: sets the handler word at sysroot +0x374.</summary>
    [Hle(Nid = 0x8747D415, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x8747D415, Library = "SceSysrootForDriver")]
    void NID_8747D415(HleContext context) => SetSysrootHandler(context, 0x374);

    /// <summary>NID_CD4B84F7: calls the handler at sysroot +0x378 with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0xCD4B84F7, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xCD4B84F7, Library = "SceSysrootForDriver")]
    void NID_CD4B84F7(HleContext context) => CallSysrootHandler(context, 0x378, 0);

    /// <summary>NID_82FC6405: sets the handler word at sysroot +0x378.</summary>
    [Hle(Nid = 0x82FC6405, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x82FC6405, Library = "SceSysrootForDriver")]
    void NID_82FC6405(HleContext context) => SetSysrootHandler(context, 0x378);

    /// <summary>ksceKernelSysrootRegisterCoredumpTrigger: sets the handler word at sysroot +0x37C.</summary>
    [Hle(Nid = 0xDD473B05, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootRegisterCoredumpTrigger(HleContext context) => SetSysrootHandler(context, 0x37C);

    /// <summary>NID_400B9793: sets the handler word at sysroot +0x380.</summary>
    [Hle(Nid = 0x400B9793, Library = "SceSysrootForDriver")]
    void NID_400B9793(HleContext context) => SetSysrootHandler(context, 0x380);

    /// <summary>NID_DABD4A47: calls the handler at sysroot +0x384 with the caller's arguments; without one, 0x80010002.</summary>
    [Hle(Nid = 0xDABD4A47, Library = "SceSysrootForDriver")]
    void NID_DABD4A47(HleContext context) => CallSysrootHandler(context, 0x384, unchecked((int)0x80010002));

    /// <summary>NID_9DCD0894: sets the handler word at sysroot +0x384.</summary>
    [Hle(Nid = 0x9DCD0894, Library = "SceSysrootForDriver")]
    void NID_9DCD0894(HleContext context) => SetSysrootHandler(context, 0x384);

    /// <summary>NID_591BB490: calls the handler at sysroot +0x388 with the caller's arguments; without one, 0x80010002.</summary>
    [Hle(Nid = 0x591BB490, Library = "SceSysrootForDriver")]
    void NID_591BB490(HleContext context) => CallSysrootHandler(context, 0x388, unchecked((int)0x80010002));

    /// <summary>NID_AA770EF7: sets the handler word at sysroot +0x388.</summary>
    [Hle(Nid = 0xAA770EF7, Library = "SceSysrootForDriver")]
    void NID_AA770EF7(HleContext context) => SetSysrootHandler(context, 0x388);

    /// <summary>ksceKernelSysrootGetSystemSwVersion: calls the handler at sysroot +0x398 with the caller's arguments; without one, 0xffffffff.</summary>
    [Hle(Nid = 0x67AAB627, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootGetSystemSwVersion(HleContext context) => CallSysrootHandler(context, 0x398, unchecked((int)0xffffffff));

    /// <summary>ksceKernelSysrootSetGetSystemSwVersionFunc: sets the handler word at sysroot +0x398.</summary>
    [Hle(Nid = 0x3276086B, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootSetGetSystemSwVersionFunc(HleContext context) => SetSysrootHandler(context, 0x398);

    /// <summary>NID_9BF782CA: calls the handler at sysroot +0x39C with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x9BF782CA, Library = "SceSysrootForDriver")]
    void NID_9BF782CA(HleContext context) => CallSysrootHandler(context, 0x39C, 0);

    /// <summary>NID_246AAAB3: sets the handler word at sysroot +0x39C.</summary>
    [Hle(Nid = 0x246AAAB3, Library = "SceSysrootForDriver")]
    void NID_246AAAB3(HleContext context) => SetSysrootHandler(context, 0x39C);

    /// <summary>NID_47724459: calls the handler at sysroot +0x3A8 with the caller's arguments; without one, nothing.</summary>
    [Hle(Nid = 0x47724459, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x47724459, Library = "SceSysrootForDriver")]
    void NID_47724459(HleContext context) => CallSysrootHandler(context, 0x3A8, null);

    /// <summary>NID_C5EAF5F7: sets the handler word at sysroot +0x3A8.</summary>
    [Hle(Nid = 0xC5EAF5F7, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xC5EAF5F7, Library = "SceSysrootForDriver")]
    void NID_C5EAF5F7(HleContext context) => SetSysrootHandler(context, 0x3A8);

    /// <summary>NID_CC7A0E63: calls the handler at sysroot +0x3AC with the caller's arguments; without one, nothing.</summary>
    [Hle(Nid = 0xCC7A0E63, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xCC7A0E63, Library = "SceSysrootForDriver")]
    void NID_CC7A0E63(HleContext context) => CallSysrootHandler(context, 0x3AC, null);

    /// <summary>NID_2D6B2A79: sets the handler word at sysroot +0x3AC.</summary>
    [Hle(Nid = 0x2D6B2A79, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x2D6B2A79, Library = "SceSysrootForDriver")]
    void NID_2D6B2A79(HleContext context) => SetSysrootHandler(context, 0x3AC);

    /// <summary>NID_631141E2: calls the handler at sysroot +0x3B4 with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x631141E2, Library = "SceSysrootForDriver")]
    void NID_631141E2(HleContext context) => CallSysrootHandler(context, 0x3B4, 0);

    /// <summary>NID_1FFD2AFA: sets the handler word at sysroot +0x3B4.</summary>
    [Hle(Nid = 0x1FFD2AFA, Library = "SceSysrootForDriver")]
    void NID_1FFD2AFA(HleContext context) => SetSysrootHandler(context, 0x3B4);

    /// <summary>NID_17DD213C: calls the handler at sysroot +0x3B8 with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x17DD213C, Library = "SceSysrootForDriver")]
    void NID_17DD213C(HleContext context) => CallSysrootHandler(context, 0x3B8, 0);

    /// <summary>NID_7C0BE393: sets the handler word at sysroot +0x3B8.</summary>
    [Hle(Nid = 0x7C0BE393, Library = "SceSysrootForDriver")]
    void NID_7C0BE393(HleContext context) => SetSysrootHandler(context, 0x3B8);

    /// <summary>NID_9E96D990: calls the handler at sysroot +0x3BC with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x9E96D990, Library = "SceSysrootForDriver")]
    void NID_9E96D990(HleContext context) => CallSysrootHandler(context, 0x3BC, 0);

    /// <summary>NID_05CD0DD9: sets the handler word at sysroot +0x3BC.</summary>
    [Hle(Nid = 0x05CD0DD9, Library = "SceSysrootForDriver")]
    void NID_05CD0DD9(HleContext context) => SetSysrootHandler(context, 0x3BC);

    /// <summary>NID_F404026C: calls the handler at sysroot +0x3C0 with the caller's arguments; without one, nothing.</summary>
    [Hle(Nid = 0xF404026C, Library = "SceSysrootForDriver")]
    void NID_F404026C(HleContext context) => CallSysrootHandler(context, 0x3C0, null);

    /// <summary>NID_ED688AEE: sets the handler word at sysroot +0x3C0.</summary>
    [Hle(Nid = 0xED688AEE, Library = "SceSysrootForDriver")]
    void NID_ED688AEE(HleContext context) => SetSysrootHandler(context, 0x3C0);

    /// <summary>NID_5A7FFDC1: clears the handler word at sysroot +0x3C4.</summary>
    [Hle(Nid = 0x5A7FFDC1, Library = "SceSysrootForDriver")]
    void NID_5A7FFDC1(HleContext context) => SetSysrootWord(0x3C4, 0);

    /// <summary>NID_571E5B79: calls entry +0x4 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0x571E5B79, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x571E5B79, Library = "SceSysrootForDriver")]
    void NID_571E5B79(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x4, null);

    /// <summary>NID_9D058A32: calls entry +0x8 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0x9D058A32, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x9D058A32, Library = "SceSysrootForDriver")]
    void NID_9D058A32(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x8, null);

    /// <summary>NID_B501E861: calls entry +0xC of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0xB501E861, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xB501E861, Library = "SceSysrootForDriver")]
    void NID_B501E861(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0xC, null);

    /// <summary>NID_C67A1FA9: calls entry +0x10 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0xC67A1FA9, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xC67A1FA9, Library = "SceSysrootForDriver")]
    void NID_C67A1FA9(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x10, null);

    /// <summary>NID_C81B7E2B: calls entry +0x14 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0xC81B7E2B, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xC81B7E2B, Library = "SceSysrootForDriver")]
    void NID_C81B7E2B(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x14, null);

    /// <summary>NID_20D2A0DF: calls entry +0x18 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0x20D2A0DF, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x20D2A0DF, Library = "SceSysrootForDriver")]
    void NID_20D2A0DF(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x18, null);

    /// <summary>NID_421EFC96: calls entry +0x1C of the handler table at sysroot +0x3C4; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x421EFC96, Library = "SceSysrootForDriver")]
    void NID_421EFC96(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x1C, unchecked((int)0x80010086));

    /// <summary>NID_1D61C837: calls entry +0x20 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0x1D61C837, Library = "SceSysrootForDriver")]
    void NID_1D61C837(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x20, null);

    /// <summary>NID_582616EC: calls entry +0x24 of the handler table at sysroot +0x3C4; without the table, nothing.</summary>
    [Hle(Nid = 0x582616EC, Library = "SceSysrootForDriver")]
    void NID_582616EC(HleContext context) => CallSysrootTableEntry(context, 0x3C4, 0x24, null);

    /// <summary>ksceKernelSysrootSetProcessHandler: registers the handler table (+0 its size, 0x28) at sysroot +0x3C4.</summary>
    [Hle(Nid = 0x0F07C3FC, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootSetProcessHandler(HleContext context) => RegisterSysrootTable(context, 0x3C4, 0x28);

    /// <summary>ksceKernelSysrootUnregisterDbgpHandler: clears the handler word at sysroot +0x3C8.</summary>
    [Hle(Nid = 0x7B7ED1E9, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x7B7ED1E9, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootUnregisterDbgpHandler(HleContext context) => SetSysrootWord(0x3C8, 0);

    /// <summary>NID_F4340469: calls entry +0x4 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0xF4340469, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xF4340469, Library = "SceSysrootForDriver")]
    void NID_F4340469(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x4, null);

    /// <summary>NID_8498E23F: calls entry +0x8 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x8498E23F, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x8498E23F, Library = "SceSysrootForDriver")]
    void NID_8498E23F(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x8, null);

    /// <summary>NID_6D44F190: calls entry +0xC of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x6D44F190, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x6D44F190, Library = "SceSysrootForDriver")]
    void NID_6D44F190(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0xC, null);

    /// <summary>NID_5C86E49B: calls entry +0x10 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x5C86E49B, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x5C86E49B, Library = "SceSysrootForDriver")]
    void NID_5C86E49B(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x10, null);

    /// <summary>NID_77CF3FBD: calls entry +0x14 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x77CF3FBD, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x77CF3FBD, Library = "SceSysrootForDriver")]
    void NID_77CF3FBD(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x14, null);

    /// <summary>NID_51F9C118: calls entry +0x18 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x51F9C118, Library = "SceSysrootForDriver")]
    void NID_51F9C118(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x18, null);

    /// <summary>NID_F2BC9E1D: calls entry +0x1C of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0xF2BC9E1D, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xF2BC9E1D, Library = "SceSysrootForDriver")]
    void NID_F2BC9E1D(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x1C, null);

    /// <summary>NID_3F76D531: calls entry +0x20 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x3F76D531, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x3F76D531, Library = "SceSysrootForDriver")]
    void NID_3F76D531(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x20, null);

    /// <summary>NID_84C0BE05: calls entry +0x24 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x84C0BE05, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x84C0BE05, Library = "SceSysrootForDriver")]
    void NID_84C0BE05(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x24, null);

    /// <summary>NID_CA497324: calls entry +0x28 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0xCA497324, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xCA497324, Library = "SceSysrootForDriver")]
    void NID_CA497324(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x28, null);

    /// <summary>NID_AA8D34EE: calls entry +0x2C of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0xAA8D34EE, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xAA8D34EE, Library = "SceSysrootForDriver")]
    void NID_AA8D34EE(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x2C, null);

    /// <summary>NID_6050A467: calls entry +0x30 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x6050A467, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x6050A467, Library = "SceSysrootForDriver")]
    void NID_6050A467(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x30, unchecked((int)0x80010086));

    /// <summary>NID_89532E2F: calls entry +0x34 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x89532E2F, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x89532E2F, Library = "SceSysrootForDriver")]
    void NID_89532E2F(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x34, unchecked((int)0x80010086));

    /// <summary>NID_9CFF80F9: calls entry +0x38 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x9CFF80F9, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x9CFF80F9, Library = "SceSysrootForDriver")]
    void NID_9CFF80F9(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x38, unchecked((int)0x80010086));

    /// <summary>NID_38751FB7: calls entry +0x3C of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x38751FB7, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x38751FB7, Library = "SceSysrootForDriver")]
    void NID_38751FB7(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x3C, unchecked((int)0x80010086));

    /// <summary>NID_73522F65: calls entry +0x40 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x73522F65, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x73522F65, Library = "SceSysrootForDriver")]
    void NID_73522F65(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x40, unchecked((int)0x80010086));

    /// <summary>ksceKernelSysrootDbgpSuspendProcessAndWaitResume: calls entry +0x44 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x256B2394, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x256B2394, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootDbgpSuspendProcessAndWaitResume(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x44, unchecked((int)0x80010086));

    /// <summary>NID_39F1FD07: calls entry +0x48 of the handler table at sysroot +0x3C8; without the table, 0.</summary>
    [Hle(Nid = 0x39F1FD07, Library = "SceSysrootForDriver")]
    void NID_39F1FD07(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x48, 0);

    /// <summary>NID_7B4A377C: calls entry +0x4C of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x7B4A377C, Library = "SceSysrootForDriver")]
    void NID_7B4A377C(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x4C, null);

    /// <summary>NID_65F3D598: calls entry +0x50 of the handler table at sysroot +0x3C8; without the table, nothing.</summary>
    [Hle(Nid = 0x65F3D598, Library = "SceSysrootForDriver")]
    void NID_65F3D598(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x50, null);

    /// <summary>NID_6219CC14: calls entry +0x54 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x6219CC14, Library = "SceSysrootForDriver")]
    void NID_6219CC14(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x54, unchecked((int)0x80010086));

    /// <summary>NID_1160FCA1: calls entry +0x58 of the handler table at sysroot +0x3C8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x1160FCA1, Library = "SceSysrootForDriver")]
    void NID_1160FCA1(HleContext context) => CallSysrootTableEntry(context, 0x3C8, 0x58, unchecked((int)0x80010086));

    /// <summary>ksceKernelSysrootRegisterDbgpHandler: registers the handler table (+0 its size, 0x5C) at sysroot +0x3C8.</summary>
    [Hle(Nid = 0x3999F917, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x3999F917, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootRegisterDbgpHandler(HleContext context) => RegisterSysrootTable(context, 0x3C8, 0x5C);

    /// <summary>NID_6C036B53: calls the handler at sysroot +0x3CC with the caller's arguments; without one, 0x80010086.</summary>
    [Hle(Nid = 0x6C036B53, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x6C036B53, Library = "SceSysrootForDriver")]
    void NID_6C036B53(HleContext context) => CallSysrootHandler(context, 0x3CC, unchecked((int)0x80010086));

    /// <summary>NID_42FC9FFF: clears the handler word at sysroot +0x3CC.</summary>
    [Hle(Nid = 0x42FC9FFF, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x42FC9FFF, Library = "SceSysrootForDriver")]
    void NID_42FC9FFF(HleContext context) => SetSysrootWord(0x3CC, 0);

    /// <summary>NID_ECADF8C3: sets the handler word at sysroot +0x3CC.</summary>
    [Hle(Nid = 0xECADF8C3, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xECADF8C3, Library = "SceSysrootForDriver")]
    void NID_ECADF8C3(HleContext context) => SetSysrootHandler(context, 0x3CC);

    /// <summary>NID_A9510EFE: calls the handler at sysroot +0x3D0 with the caller's arguments; without one, 0x80010086.</summary>
    [Hle(Nid = 0xA9510EFE, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xA9510EFE, Library = "SceSysrootForDriver")]
    void NID_A9510EFE(HleContext context) => CallSysrootHandler(context, 0x3D0, unchecked((int)0x80010086));

    /// <summary>NID_D7207203: clears the handler word at sysroot +0x3D0.</summary>
    [Hle(Nid = 0xD7207203, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xD7207203, Library = "SceSysrootForDriver")]
    void NID_D7207203(HleContext context) => SetSysrootWord(0x3D0, 0);

    /// <summary>NID_A799F22A: sets the handler word at sysroot +0x3D0.</summary>
    [Hle(Nid = 0xA799F22A, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xA799F22A, Library = "SceSysrootForDriver")]
    void NID_A799F22A(HleContext context) => SetSysrootHandler(context, 0x3D0);

    /// <summary>NID_06436EF0: calls the handler at sysroot +0x3D4 with the caller's arguments; without one, 0x80010086.</summary>
    [Hle(Nid = 0x06436EF0, Library = "SceSysrootForDriver")]
    void NID_06436EF0(HleContext context) => CallSysrootHandler(context, 0x3D4, unchecked((int)0x80010086));

    /// <summary>NID_A7A01DF0: clears the handler word at sysroot +0x3D4.</summary>
    [Hle(Nid = 0xA7A01DF0, Library = "SceSysrootForDriver")]
    void NID_A7A01DF0(HleContext context) => SetSysrootWord(0x3D4, 0);

    /// <summary>NID_1834D482: sets the handler word at sysroot +0x3D4.</summary>
    [Hle(Nid = 0x1834D482, Library = "SceSysrootForDriver")]
    void NID_1834D482(HleContext context) => SetSysrootHandler(context, 0x3D4);

    /// <summary>NID_CCB8BB5E: calls the handler at sysroot +0x3DC with the caller's arguments; without one, 0x80010086.</summary>
    [Hle(Nid = 0xCCB8BB5E, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xCCB8BB5E, Library = "SceSysrootForDriver")]
    void NID_CCB8BB5E(HleContext context) => CallSysrootHandler(context, 0x3DC, unchecked((int)0x80010086));

    /// <summary>NID_C728BC61: clears the handler word at sysroot +0x3DC.</summary>
    [Hle(Nid = 0xC728BC61, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xC728BC61, Library = "SceSysrootForDriver")]
    void NID_C728BC61(HleContext context) => SetSysrootWord(0x3DC, 0);

    /// <summary>NID_27D30DB2: sets the handler word at sysroot +0x3DC.</summary>
    [Hle(Nid = 0x27D30DB2, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x27D30DB2, Library = "SceSysrootForDriver")]
    void NID_27D30DB2(HleContext context) => SetSysrootHandler(context, 0x3DC);

    /// <summary>NID_40F28DC6: calls the handler at sysroot +0x3E0 with the caller's arguments; without one, 0x80010086.</summary>
    [Hle(Nid = 0x40F28DC6, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x40F28DC6, Library = "SceSysrootForDriver")]
    void NID_40F28DC6(HleContext context) => CallSysrootHandler(context, 0x3E0, unchecked((int)0x80010086));

    /// <summary>NID_D02281A8: clears the handler word at sysroot +0x3E0.</summary>
    [Hle(Nid = 0xD02281A8, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xD02281A8, Library = "SceSysrootForDriver")]
    void NID_D02281A8(HleContext context) => SetSysrootWord(0x3E0, 0);

    /// <summary>NID_D4457D4F: sets the handler word at sysroot +0x3E0.</summary>
    [Hle(Nid = 0xD4457D4F, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xD4457D4F, Library = "SceSysrootForDriver")]
    void NID_D4457D4F(HleContext context) => SetSysrootHandler(context, 0x3E0);

    /// <summary>NID_2BE874EF: sets the handler word at sysroot +0x3E4.</summary>
    [Hle(Nid = 0x2BE874EF, Library = "SceSysrootForDriver")]
    void NID_2BE874EF(HleContext context) => SetSysrootHandler(context, 0x3E4);

    /// <summary>NID_3B19B06B: sets the handler word at sysroot +0x3E8.</summary>
    [Hle(Nid = 0x3B19B06B, Library = "SceSysrootForDriver")]
    void NID_3B19B06B(HleContext context) => SetSysrootHandler(context, 0x3E8);

    /// <summary>NID_70AD47A9: sets the handler word at sysroot +0x3EC.</summary>
    [Hle(Nid = 0x70AD47A9, Library = "SceSysrootForDriver")]
    void NID_70AD47A9(HleContext context) => SetSysrootHandler(context, 0x3EC);

    /// <summary>NID_E06C14FD: clears the handler word at sysroot +0x3F4.</summary>
    [Hle(Nid = 0xE06C14FD, Library = "SceSysrootForDriver")]
    void NID_E06C14FD(HleContext context) => SetSysrootWord(0x3F4, 0);

    /// <summary>NID_951C7943: calls entry +0x4 of the handler table at sysroot +0x3F4; without the table, 0x80010086.</summary>
    [Hle(Nid = 0x951C7943, Library = "SceSysrootForDriver")]
    void NID_951C7943(HleContext context) => CallSysrootTableEntry(context, 0x3F4, 0x4, unchecked((int)0x80010086));

    /// <summary>NID_D1E54BF4: calls entry +0x8 of the handler table at sysroot +0x3F4; without the table, 0x80010086.</summary>
    [Hle(Nid = 0xD1E54BF4, Library = "SceSysrootForDriver")]
    void NID_D1E54BF4(HleContext context) => CallSysrootTableEntry(context, 0x3F4, 0x8, unchecked((int)0x80010086));

    /// <summary>NID_5A4A7F7E: registers the handler table (+0 its size, 0xC) at sysroot +0x3F4.</summary>
    [Hle(Nid = 0x5A4A7F7E, Library = "SceSysrootForDriver")]
    void NID_5A4A7F7E(HleContext context) => RegisterSysrootTable(context, 0x3F4, 0xC);

    /// <summary>NID_A85FF3B2: clears the handler word at sysroot +0x3F8.</summary>
    [Hle(Nid = 0xA85FF3B2, Library = "SceSysrootForDriver")]
    void NID_A85FF3B2(HleContext context) => SetSysrootWord(0x3F8, 0);

    /// <summary>NID_ED22CF8F: calls entry +0x4 of the handler table at sysroot +0x3F8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0xED22CF8F, Library = "SceSysrootForDriver")]
    void NID_ED22CF8F(HleContext context) => CallSysrootTableEntry(context, 0x3F8, 0x4, unchecked((int)0x80010086));

    /// <summary>NID_DC651D22: calls entry +0x8 of the handler table at sysroot +0x3F8; without the table, 0x80010086.</summary>
    [Hle(Nid = 0xDC651D22, Library = "SceSysrootForDriver")]
    void NID_DC651D22(HleContext context) => CallSysrootTableEntry(context, 0x3F8, 0x8, unchecked((int)0x80010086));

    /// <summary>NID_34281746: registers the handler table (+0 its size, 0xC) at sysroot +0x3F8.</summary>
    [Hle(Nid = 0x34281746, Library = "SceSysrootForDriver")]
    void NID_34281746(HleContext context) => RegisterSysrootTable(context, 0x3F8, 0xC);

    /// <summary>NID_0ECD711E: clears word +0x4 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x0ECD711E, Library = "SceSysrootForDriver")]
    void NID_0ECD711E(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x4, 0);

    /// <summary>NID_483EF108: word +0x4 of the table at sysroot +0x3FC; 0 without the table.</summary>
    [Hle(Nid = 0x483EF108, Library = "SceSysrootForDriver")]
    void NID_483EF108(HleContext context) => GetSysrootTableWord(context, 0x3FC, 0x4);

    /// <summary>NID_A6A0A038: sets word +0x4 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0xA6A0A038, Library = "SceSysrootForDriver")]
    void NID_A6A0A038(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x4, context.Gpr(0));

    /// <summary>NID_5B106EB3: clears word +0x8 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x5B106EB3, Library = "SceSysrootForDriver")]
    void NID_5B106EB3(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x8, 0);

    /// <summary>NID_49843C16: word +0x8 of the table at sysroot +0x3FC; 0 without the table.</summary>
    [Hle(Nid = 0x49843C16, Library = "SceSysrootForDriver")]
    void NID_49843C16(HleContext context) => GetSysrootTableWord(context, 0x3FC, 0x8);

    /// <summary>NID_47F19DD3: sets word +0x8 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x47F19DD3, Library = "SceSysrootForDriver")]
    void NID_47F19DD3(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x8, context.Gpr(0));

    /// <summary>NID_2C9E7797: clears word +0xC of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x2C9E7797, Library = "SceSysrootForDriver")]
    void NID_2C9E7797(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0xC, 0);

    /// <summary>NID_3AE319DA: word +0xC of the table at sysroot +0x3FC; 0 without the table.</summary>
    [Hle(Nid = 0x3AE319DA, Library = "SceSysrootForDriver")]
    void NID_3AE319DA(HleContext context) => GetSysrootTableWord(context, 0x3FC, 0xC);

    /// <summary>NID_709799BB: sets word +0xC of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x709799BB, Library = "SceSysrootForDriver")]
    void NID_709799BB(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0xC, context.Gpr(0));

    /// <summary>NID_D2423D6B: clears word +0x10 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0xD2423D6B, Library = "SceSysrootForDriver")]
    void NID_D2423D6B(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x10, 0);

    /// <summary>NID_525B24C5: word +0x10 of the table at sysroot +0x3FC; 0 without the table.</summary>
    [Hle(Nid = 0x525B24C5, Library = "SceSysrootForDriver")]
    void NID_525B24C5(HleContext context) => GetSysrootTableWord(context, 0x3FC, 0x10);

    /// <summary>NID_1434D90E: sets word +0x10 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x1434D90E, Library = "SceSysrootForDriver")]
    void NID_1434D90E(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x10, context.Gpr(0));

    /// <summary>NID_EB19AAF6: clears word +0x14 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0xEB19AAF6, Library = "SceSysrootForDriver")]
    void NID_EB19AAF6(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x14, 0);

    /// <summary>NID_10788257: word +0x14 of the table at sysroot +0x3FC; 0 without the table.</summary>
    [Hle(Nid = 0x10788257, Library = "SceSysrootForDriver")]
    void NID_10788257(HleContext context) => GetSysrootTableWord(context, 0x3FC, 0x14);

    /// <summary>NID_9CDE3623: sets word +0x14 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x9CDE3623, Library = "SceSysrootForDriver")]
    void NID_9CDE3623(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x14, context.Gpr(0));

    /// <summary>NID_5458BF6D: clears word +0x18 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0x5458BF6D, Library = "SceSysrootForDriver")]
    void NID_5458BF6D(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x18, 0);

    /// <summary>NID_0A52A59C: word +0x18 of the table at sysroot +0x3FC; 0 without the table.</summary>
    [Hle(Nid = 0x0A52A59C, Library = "SceSysrootForDriver")]
    void NID_0A52A59C(HleContext context) => GetSysrootTableWord(context, 0x3FC, 0x18);

    /// <summary>NID_B0E3E791: sets word +0x18 of the table at sysroot +0x3FC (when there is one).</summary>
    [Hle(Nid = 0xB0E3E791, Library = "SceSysrootForDriver")]
    void NID_B0E3E791(HleContext context) => SetSysrootTableWord(context, 0x3FC, 0x18, context.Gpr(0));

    /// <summary>NID_FF2DD7AB: registers the handler table (+0 its size, 0x1C) at sysroot +0x3FC.</summary>
    [Hle(Nid = 0xFF2DD7AB, Library = "SceSysrootForDriver")]
    void NID_FF2DD7AB(HleContext context) => RegisterSysrootTable(context, 0x3FC, 0x1C);

    /// <summary>NID_7AAC4EE7: calls the handler at sysroot +0x404 with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x7AAC4EE7, Library = "SceSysrootForDriver")]
    void NID_7AAC4EE7(HleContext context) => CallSysrootHandler(context, 0x404, 0);

    /// <summary>NID_9421B223: clears the handler word at sysroot +0x404.</summary>
    [Hle(Nid = 0x9421B223, Library = "SceSysrootForDriver")]
    void NID_9421B223(HleContext context) => SetSysrootWord(0x404, 0);

    /// <summary>NID_E541959B: sets the handler word at sysroot +0x404.</summary>
    [Hle(Nid = 0xE541959B, Library = "SceSysrootForDriver")]
    void NID_E541959B(HleContext context) => SetSysrootHandler(context, 0x404);

    /// <summary>NID_39F9C968: calls the handler at sysroot +0x408 with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x39F9C968, Library = "SceSysrootForDriver")]
    void NID_39F9C968(HleContext context) => CallSysrootHandler(context, 0x408, 0);

    /// <summary>NID_2816A813: clears the handler word at sysroot +0x408.</summary>
    [Hle(Nid = 0x2816A813, Library = "SceSysrootForDriver")]
    void NID_2816A813(HleContext context) => SetSysrootWord(0x408, 0);

    /// <summary>NID_D56A2F3E: sets the handler word at sysroot +0x408.</summary>
    [Hle(Nid = 0xD56A2F3E, Library = "SceSysrootForDriver")]
    void NID_D56A2F3E(HleContext context) => SetSysrootHandler(context, 0x408);

    /// <summary>NID_BFD8F2A2: calls the handler at sysroot +0x40C with the caller's arguments; without one, nothing.</summary>
    [Hle(Nid = 0xBFD8F2A2, Library = "SceSysrootForDriver")]
    void NID_BFD8F2A2(HleContext context) => CallSysrootHandler(context, 0x40C, null);

    /// <summary>NID_273EAE53: calls the handler at sysroot +0x410 with the caller's arguments (a 64-bit result); without one, 0.</summary>
    [Hle(Nid = 0x273EAE53, Library = "SceSysrootForDriver")]
    void NID_273EAE53(HleContext context) => CallSysrootHandler(context, 0x410, 0, wide: true);

    /// <summary>NID_6655F48C: calls the handler at sysroot +0x414 with the caller's arguments; without one, 0.</summary>
    [Hle(Nid = 0x6655F48C, Library = "SceSysrootForDriver")]
    void NID_6655F48C(HleContext context) => CallSysrootHandler(context, 0x414, 0);
}
