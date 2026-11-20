using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of the FW 3.74 sysroot (SceSysrootForKernel/ForDriver in sysmem.skprx; decompiled, study only): the data
/// words next to the handler slots, the boot loader's parameters (the KBL parameters the sysroot points to at +0x6C),
/// the boot-level init callbacks and the tables the thread, module and process managers register.
/// <para>
/// The sysroot is a 0x41C-byte structure (its size word at +0x08; the export 0xEEB867C0 answers the same size). Its
/// words are kept by offset (<see cref="TryGetSysrootWord"/>), each with the boot value VitaSharp's HLE boot leaves
/// there; the structure itself is in kernel memory once a caller asks for it (ksceSysrootGetSysroot, the corelock at
/// +0x2C, the init callbacks at +0x8C..+0x2D3), and every word written after that is written there too.
/// </para>
/// <para>
/// The KBL parameters (0x100 bytes, kernel memory) come from the unit's single sources: <see cref="ConsoleIdentity"/>
/// (pscode +0xA0, OpenPSID +0x70, hardware info +0xD4, secure module info +0x08), the QA flags (+0x20) and the debug
/// switches (+0x40, refreshed each time the parameters are handed out) of <see cref="SystemServicesVsh"/>, and the boot
/// flags word <see cref="BootFlags"/> (+0x6C). A normal boot leaves the 16 boot-mode bytes at +0x30 at 0xFF (no
/// update, safe or manufacturing mode), the wakeup factor (+0xC4) at 0 and the session id (+0xB0) at zero.
/// </para>
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The size of the FW 3.74 sysroot (sysmem checks it at boot: "Sysroot MISMATCH").</summary>
    internal const uint SysrootSize = 0x41C;

    /// <summary>The size of the KBL parameters.</summary>
    internal const uint KblParamSize = 0x100;

    /// <summary>The init level the boot reached: the process manager runs every level (8) when the first process is
    /// created, and VitaSharp's HLE boot has created it before any module runs.</summary>
    const uint BootInitLevel = 8;

    /// <summary>The sysroot status word (+0x28) after the first process (the process manager raises it to 0x100).</summary>
    const uint BootStatus = 0x100;

    const uint InitLevels = 9, InitCallbacksPerLevel = 8;

    /// <summary>The sysroot in kernel memory (0 until a caller needs its address).</summary>
    [SnapshotMissing] // states of builds before it: the sysroot had no guest copy
    uint _sysrootBlock;

    /// <summary>The KBL parameters in kernel memory (0 until first asked).</summary>
    [SnapshotMissing] // states of builds before it: the parameters had not been handed out
    uint _kblParam;

    /// <summary>The sysmem counter 0xEE934615/0xEEF091A7 move by 0x10 (sysmem's boot sets it to 4, 0x14 in
    /// manufacturing mode).</summary>
    [SnapshotMissing] // states of builds before it: the counter was never moved (its boot value)
    int _sysrootCounter = (BootFlags & 4) != 0 ? 0x14 : 4;

    [SnapshotIgnore]
    readonly object _sysrootStatusLock = new();

    /// <summary>The kernel's own SceSelfAuthInfo (0x90 bytes) that ksceSysrootGetSelfAuthInfo copies without a process
    /// manager table: program authority 0x2808000000000001, the kernel's capability and attribute words.</summary>
    static ReadOnlySpan<byte> KernelSelfAuthInfo =>
    [
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x80, 0x00, 0x00, 0x00, 0xC0, 0x00, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x80, 0x09, 0x80, 0x03, 0x00, 0x00, 0xC3, 0x00, 0x00, 0x00, 0x80, 0x09, 0x80, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>The boot value of a data word of the sysroot: its size (+0x08), the status (+0x28), the KBL parameters
    /// (+0x6C), the factory software version (+0x74, the unit's own: no other is recorded), the model capabilities
    /// (+0x84); 0 otherwise.</summary>
    uint DefaultSysrootData(uint offset)
    {
        return offset switch
        {
            0x08 => SysrootSize,
            0x28 => BootStatus,
            0x6C => KblParam(),
            0x74 => SystemMisc.SystemSwVersion,
            0x84 => ModelCapabilities,
            _ => 0,
        };
    }

    /// <summary>The data word at the offset: what a module wrote, else its boot value.</summary>
    uint SysrootData(uint offset)
    {
        return TryGetSysrootWord(offset, out uint value) ? value : DefaultSysrootData(offset);
    }

    /// <summary>The sysroot in kernel memory, made the first time with every word's current value, its corelock
    /// (+0x2C) initialized and the init level (+0x90) the boot reached.</summary>
    uint SysrootBlock()
    {
        lock (_sysrootWords)
        {
            if (_sysrootBlock != 0)
            {
                return _sysrootBlock;
            }

            uint block = KernelAlloc(SysrootSize, "kernel:sysroot", 8);
            GuestMemory.Span(block, (int)SysrootSize).Clear();
            foreach (uint offset in (ReadOnlySpan<uint>)[0x08, 0x28, 0x74, 0x84])
            {
                GuestMemory.W32(block + offset, DefaultSysrootData(offset));
            }

            foreach (KeyValuePair<int, int> word in _sysrootWords)
            {
                GuestMemory.W32(block + (uint)word.Key, (uint)word.Value);
            }

            ksceKernelCorelockInitialize(block + 0x2C);
            GuestMemory.W32(block + 0x90, BootInitLevel);
            _sysrootBlock = block;
        }

        GuestMemory.W32(_sysrootBlock + 0x6C, SysrootData(0x6C));
        return _sysrootBlock;
    }

    /// <summary>Writes the word into the sysroot's guest copy when there is one.</summary>
    void MirrorSysrootWord(uint offset, uint value)
    {
        if (_sysrootBlock != 0 && offset + 4 <= SysrootSize)
        {
            GuestMemory.W32(_sysrootBlock + offset, value);
        }
    }

    /// <summary>The KBL parameters in kernel memory (see the class summary), the debug switches refreshed.</summary>
    uint KblParam()
    {
        lock (_sysrootWords)
        {
            if (_kblParam == 0)
            {
                uint param = KernelAlloc(KblParamSize, "kernel:kbl_param", 8);
                Span<byte> bytes = GuestMemory.Span(param, (int)KblParamSize);
                bytes.Clear();
                GuestMemory.Write(param + 2, (ushort)KblParamSize);
                GuestMemory.W32(param + 4, SystemMisc.SystemSwVersion);
                GuestMemory.W32(param + 8, ConsoleIdentity.SecureModuleInfo);
                SystemServicesVsh.QaFlags.CopyTo(bytes.Slice(0x20, 0x10));
                bytes.Slice(0x30, 0x10).Fill(0xFF);
                GuestMemory.W32(param + 0x6C, BootFlags);
                ConsoleIdentity.OpenPsId.CopyTo(bytes.Slice(0x70, 0x10));
                WriteBigEndian16(param + 0xA0, ConsoleIdentity.CompanyCode);
                WriteBigEndian16(param + 0xA2, ConsoleIdentity.ProductCode);
                WriteBigEndian16(param + 0xA4, ConsoleIdentity.ProductSubCode);
                WriteBigEndian16(param + 0xA6, ConsoleIdentity.FactoryCode);
                GuestMemory.W32(param + 0xD4, ConsoleIdentity.HardwareInfo);
                _kblParam = param;
            }
        }

        if (Emu.Hle.Modules.OfType<SystemServicesVsh>().FirstOrDefault() is { } services)
        {
            for (uint word = 0; word < 8; word++)
            {
                GuestMemory.W32(_kblParam + 0x40 + word * 4, services.DipswWord(word));
            }
        }

        return _kblParam;
    }

    static void WriteBigEndian16(uint address, ushort value)
    {
        GuestMemory.Write(address, (byte)(value >> 8));
        GuestMemory.Write(address + 1, (byte)value);
    }

    uint KblWord(uint offset) => GuestMemory.R32(KblParam() + offset);

    byte KblByte(uint offset) => GuestMemory.Read<byte>(KblParam() + offset);

    /// <summary>The wakeup factor's kind (KBL +0xC4, low 7 bits).</summary>
    uint WakeupKind => KblWord(0xC4) & 0x7F;

    // ------------------------------------------------------------------ the sysroot and the boot parameters

    /// <summary>ksceSysrootGetSysroot (0x3E455842)(): the sysroot's address.</summary>
    [Hle(Nid = 0x3E455842, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceSysrootGetSysroot() => SysrootBlock();

    /// <summary>NID_EEB867C0(): the size of the sysroot, 0x41C.</summary>
    [Hle(Nid = 0xEEB867C0, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_EEB867C0() => SysrootSize;

    /// <summary>NID_085C2BCB(): the word sysmem copies from sysroot +0x30C at boot.</summary>
    [Hle(Nid = 0x085C2BCB, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_085C2BCB() => SysrootData(0x30C);

    /// <summary>ksceKernelSysrootSetSysroot (0x36916C30)(value): sets word +0x308.</summary>
    [Hle(Nid = 0x36916C30, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootSetSysroot(HleContext context) => SetSysrootWord(0x308, context.Gpr(0));

    /// <summary>ksceKernelSysrootGetKblParam (0x9DB56D1F)(): the KBL parameters' address (sysroot +0x6C).</summary>
    [Hle(Nid = 0x9DB56D1F, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceKernelSysrootGetKblParam() => SysrootData(0x6C);

    /// <summary>ksceKernelSysrootGetStatus (0x5C426B19)(): the status word (+0x28).</summary>
    [Hle(Nid = 0x5C426B19, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceKernelSysrootGetStatus() => SysrootData(0x28);

    /// <summary>NID_A84676E3(status): raises the status word (+0x28) to <paramref name="status"/> (never lowers it),
    /// under the sysroot's lock.</summary>
    [Hle(Nid = 0xA84676E3, Library = "SceSysrootForKernel")]
    void NID_A84676E3(HleContext context)
    {
        uint status = context.Gpr(0);
        lock (_sysrootStatusLock)
        {
            if (SysrootData(0x28) < status)
            {
                SetSysrootWord(0x28, status);
            }
        }
    }

    /// <summary>ksceKernelIsColdBoot (0xD7198963)(): 1 unless bit 4 of the boot word (+0x24) is set (a resume); the
    /// HLE boot is a cold boot.</summary>
    [Hle(Nid = 0xD7198963, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceKernelIsColdBoot() => ((SysrootData(0x24) ^ 0x10) & 0x1F) >> 4;

    /// <summary>NID_06182D59(index): entry <paramref name="index"/> (0x20 bytes each) of the table at word +0x20.</summary>
    [Hle(Nid = 0x06182D59, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_06182D59(uint index) => SysrootData(0x20) + index * 0x20;

    /// <summary>ksceKernelSysrootGetModulePrivate (0x37EC12BB)(index): word <paramref name="index"/> of the module
    /// private words (+0x34).</summary>
    [Hle(Nid = 0x37EC12BB, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceKernelSysrootGetModulePrivate(uint index) => SysrootData(0x34 + index * 4);

    /// <summary>NID_7A7E7C0C(index, value): sets module private word <paramref name="index"/> (+0x34).</summary>
    [Hle(Nid = 0x7A7E7C0C, Library = "SceSysrootForKernel")]
    void NID_7A7E7C0C(uint index, uint value) => SetSysrootWord(0x34 + index * 4, value);

    /// <summary>NID_642123EB(): clears word +0x70.</summary>
    [Hle(Nid = 0x642123EB, Library = "SceSysrootForKernel")]
    void NID_642123EB() => SetSysrootWord(0x70, 0);

    /// <summary>ksceSysrootGetFactorySystemSwVersion (0xD3872270)(): word +0x74.</summary>
    [Hle(Nid = 0xD3872270, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceSysrootGetFactorySystemSwVersion() => SysrootData(0x74);

    /// <summary>NID_FFD6E24D(): word +0x78.</summary>
    [Hle(Nid = 0xFFD6E24D, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_FFD6E24D() => SysrootData(0x78);

    /// <summary>NID_403B509E(): word +0x7C.</summary>
    [Hle(Nid = 0x403B509E, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_403B509E() => SysrootData(0x7C);

    /// <summary>NID_4D98B15B(): word +0x80.</summary>
    [Hle(Nid = 0x4D98B15B, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint NID_4D98B15B() => SysrootData(0x80);

    /// <summary>NID_5B5EBFB1(counter): sets word +0x88, the 64-bit time counter the thread manager keeps.</summary>
    [Hle(Nid = 0x5B5EBFB1, Library = "SceSysrootForKernel")]
    void NID_5B5EBFB1(HleContext context) => SetSysrootWord(0x88, context.Gpr(0));

    /// <summary>The thread manager's 64-bit time counter (through word +0x88; the guest's system time in
    /// microseconds while the HLE thread manager has registered none).</summary>
    ulong SysrootTime()
    {
        uint counter = SysrootData(0x88);
        if (counter == 0)
        {
            return K.ProcessTimeUs;
        }

        uint high;
        uint low;
        do
        {
            high = GuestMemory.R32(counter + 4);
            low = GuestMemory.R32(counter);
        }
        while (high != GuestMemory.R32(counter + 4));
        return low | ((ulong)high << 32);
    }

    /// <summary>NID_E20F6FC8(): the 64-bit time counter (r0, r1).</summary>
    [Hle(Nid = 0xE20F6FC8, Library = "SceSysrootForKernel")]
    ulong NID_E20F6FC8() => SysrootTime();

    /// <summary>NID_41636522(): the low word of the time counter.</summary>
    [Hle(Nid = 0x41636522, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_41636522() => (uint)SysrootTime();

    /// <summary>NID_1D8DB3A5(uint64 *time): the time counter into <paramref name="time"/>; NULL 0x80020006.</summary>
    [Hle(Nid = 0x1D8DB3A5, Library = "SceSysrootForKernel")]
    int NID_1D8DB3A5(uint time)
    {
        if (time == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        GuestMemory.Write(time, SysrootTime());
        return 0;
    }

    /// <summary>NID_8E4B61F1(firstCore): takes the sysroot's corelock (+0x2C).</summary>
    [Hle(Nid = 0x8E4B61F1, Library = "SceSysrootForKernel")]
    void NID_8E4B61F1(HleContext context) => CorelockLock(SysrootBlock() + 0x2C, (int)context.Gpr(0), CallerLr(context));

    /// <summary>NID_AE55B7CC(): releases the sysroot's corelock.</summary>
    [Hle(Nid = 0xAE55B7CC, Library = "SceSysrootForKernel")]
    void NID_AE55B7CC() => ksceKernelCorelockUnlock(SysrootBlock() + 0x2C);

    /// <summary>NID_59B76D23(heap): sets word +0x2E0 (the PUID entry heap without a process table).</summary>
    [Hle(Nid = 0x59B76D23, Library = "SceSysrootForKernel")]
    void NID_59B76D23(HleContext context) => SetSysrootWord(0x2E0, context.Gpr(0));

    /// <summary>NID_118657C6(): word +0x314.</summary>
    [Hle(Nid = 0x118657C6, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_118657C6() => SysrootData(0x314);

    /// <summary>NID_611F17A4(value): sets word +0x314.</summary>
    [Hle(Nid = 0x611F17A4, Library = "SceSysrootForKernel")]
    void NID_611F17A4(HleContext context) => SetSysrootWord(0x314, context.Gpr(0));

    /// <summary>ksceKernelSysrootGetVbaseResetVector (0xCC85905B)(): word +0x318.</summary>
    [Hle(Nid = 0xCC85905B, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceKernelSysrootGetVbaseResetVector() => SysrootData(0x318);

    /// <summary>NID_0F2F2B4E(vbase): sets word +0x318.</summary>
    [Hle(Nid = 0x0F2F2B4E, Library = "SceSysrootForKernel")]
    void NID_0F2F2B4E(HleContext context) => SetSysrootWord(0x318, context.Gpr(0));

    /// <summary>ksceKernelSysrootGetSharedMemory (0xC8C8C321)(): word +0x320.</summary>
    [Hle(Nid = 0xC8C8C321, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceKernelSysrootGetSharedMemory() => SysrootData(0x320);

    /// <summary>NID_FBB91741(memory): sets word +0x320.</summary>
    [Hle(Nid = 0xFBB91741, Library = "SceSysrootForKernel")]
    void NID_FBB91741(HleContext context) => SetSysrootWord(0x320, context.Gpr(0));

    /// <summary>NID_340575CB(): word +0x35C.</summary>
    [Hle(Nid = 0x340575CB, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_340575CB() => SysrootData(0x35C);

    /// <summary>NID_DD7821AA(value): sets word +0x35C; 0.</summary>
    [Hle(Nid = 0xDD7821AA, Library = "SceSysrootForKernel")]
    int NID_DD7821AA(uint value)
    {
        SetSysrootWord(0x35C, value);
        return 0;
    }

    /// <summary>NID_14D81797(table): sets the table word +0x400; 0.</summary>
    [Hle(Nid = 0x14D81797, Library = "SceSysrootForKernel")]
    int NID_14D81797(uint table)
    {
        SetSysrootWord(0x400, table);
        return 0;
    }

    /// <summary>NID_CBD377B7(start): sets the I/O file manager's start function (+0x2D8); 0.</summary>
    [Hle(Nid = 0xCBD377B7, Library = "SceSysrootForKernel")]
    int NID_CBD377B7(uint start)
    {
        SetSysrootWord(0x2D8, start);
        return 0;
    }

    /// <summary>NID_BE1EF51C(table): sets the remap check table (+0x324); 0.</summary>
    [Hle(Nid = 0xBE1EF51C, Library = "SceSysrootForKernel")]
    int NID_BE1EF51C(uint table)
    {
        SetSysrootWord(0x324, table);
        return 0;
    }

    /// <summary>The counter: atomically adds <paramref name="delta"/>; the value before.</summary>
    int AddSysrootCounter(int delta) => Interlocked.Add(ref _sysrootCounter, delta) - delta;

    /// <summary>NID_EE934615(): adds 0x10 to sysmem's counter; the value before.</summary>
    [Hle(Nid = 0xEE934615, Library = "SceSysrootForDriver", ReturnsValue = true)]
    int NID_EE934615() => AddSysrootCounter(0x10);

    /// <summary>NID_EEF091A7(): takes 0x10 from sysmem's counter; the value before.</summary>
    [Hle(Nid = 0xEEF091A7, Library = "SceSysrootForDriver", ReturnsValue = true)]
    int NID_EEF091A7() => AddSysrootCounter(-0x10);

    // ------------------------------------------------------------------ KBL parameters

    /// <summary>ksceSysrootGetHardwareInfo (0x930B1342)(): KBL +0xD4.</summary>
    [Hle(Nid = 0x930B1342, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint ksceSysrootGetHardwareInfo() => KblWord(0xD4);

    /// <summary>ksceSysrootGetHardwareFlags (0x46E72428)(uint32 flags[4]): KBL +0xE8..+0xF7; 0.</summary>
    [Hle(Nid = 0x46E72428, Library = "SceSysrootForDriver")]
    int ksceSysrootGetHardwareFlags(uint flags)
    {
        GuestMemory.Copy(flags, KblParam() + 0xE8, 0x10);
        return 0;
    }

    /// <summary>ksceSysrootGetSessionId (0x84783B71)(uint8 id[16]): KBL +0xB0..+0xBF.</summary>
    [Hle(Nid = 0x84783B71, Library = "SceSysrootForKernel")]
    void ksceSysrootGetSessionId(uint id) => GuestMemory.Copy(id, KblParam() + 0xB0, 0x10);

    /// <summary>NID_AB3CC7D0(): the sleep factor (KBL +0xC0).</summary>
    [Hle(Nid = 0xAB3CC7D0, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint NID_AB3CC7D0() => KblWord(0xC0);

    /// <summary>ksceSysrootGetWakeupFactor (0x2F97041A)(): KBL +0xC4.</summary>
    [Hle(Nid = 0x2F97041A, Library = "SceSysrootForKernel", ReturnsValue = true)]
    uint ksceSysrootGetWakeupFactor() => KblWord(0xC4);

    /// <summary>NID_E4EA1960(): the wakeup factor's kind is 1.</summary>
    [Hle(Nid = 0xE4EA1960, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xE4EA1960, Library = "SceSysrootForDriver")]
    bool NID_E4EA1960() => WakeupKind == 1;

    /// <summary>ksceSysrootIsBsodReboot (0x4373AC96)(): the wakeup factor's kind is 0x17 (a reboot after a
    /// panic).</summary>
    [Hle(Nid = 0x4373AC96, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x4373AC96, Library = "SceSysrootForDriver")]
    bool ksceSysrootIsBsodReboot() => WakeupKind == 0x17;

    /// <summary>ksceSysrootIsUsbEnumWakeup (0x79C9AE10)(): sleep factor bits 0x90000, or wakeup kind 0xF.</summary>
    [Hle(Nid = 0x79C9AE10, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x79C9AE10, Library = "SceSysrootForDriver")]
    bool ksceSysrootIsUsbEnumWakeup() => (KblWord(0xC0) & 0x90000) != 0 || WakeupKind == 0xF;

    /// <summary>ksceSysrootIsExternalBootMode (0x89D19090)(): bit 0 of the boot flags (KBL +0x6C).</summary>
    [Hle(Nid = 0x89D19090, Library = "SceSysrootForKernel", ReturnsValue = true)]
    [Hle(Nid = 0x89D19090, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint ksceSysrootIsExternalBootMode() => KblWord(0x6C) & 1;

    /// <summary>ksceSysrootIsManufacturingMode / ksceSysrootUseExternalStorage (0x55392965)(): bit 2 of the boot
    /// flags.</summary>
    [Hle(Nid = 0x55392965, Library = "SceSysrootForKernel", ReturnsValue = true)]
    [Hle(Nid = 0x55392965, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint ksceSysrootIsManufacturingMode() => (KblWord(0x6C) >> 2) & 1;

    /// <summary>ksceSysrootIsSdCardMode (0x7B7F8171)(): bit 19 of the boot flags.</summary>
    [Hle(Nid = 0x7B7F8171, Library = "SceSysrootForKernel", ReturnsValue = true)]
    [Hle(Nid = 0x7B7F8171, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint ksceSysrootIsSdCardMode() => (KblWord(0x6C) >> 19) & 1;

    /// <summary>NID_7918D44E(): bit 16 of the boot flags.</summary>
    [Hle(Nid = 0x7918D44E, Library = "SceSysrootForKernel", ReturnsValue = true)]
    [Hle(Nid = 0x7918D44E, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint NID_7918D44E() => (KblWord(0x6C) >> 16) & 1;

    /// <summary>ksceSysrootIsUpdateMode (0xB0E1FC67)(): boot mode byte +0x30 is not 0xFF.</summary>
    [Hle(Nid = 0xB0E1FC67, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xB0E1FC67, Library = "SceSysrootForDriver")]
    bool ksceSysrootIsUpdateMode() => KblByte(0x30) != 0xFF;

    /// <summary>NID_AE7A8F1D(): boot mode byte +0x31 is not 0xFF.</summary>
    [Hle(Nid = 0xAE7A8F1D, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0xAE7A8F1D, Library = "SceSysrootForDriver")]
    bool NID_AE7A8F1D() => KblByte(0x31) != 0xFF;

    /// <summary>ksceSysrootIsNonRemovableCardMode / ksceSysrootUseInternalStorage (0x50FE3B4D)(): bit 0 of boot mode
    /// byte +0x35 clear.</summary>
    [Hle(Nid = 0x50FE3B4D, Library = "SceSysrootForKernel", ReturnsValue = true)]
    [Hle(Nid = 0x50FE3B4D, Library = "SceSysrootForDriver", ReturnsValue = true)]
    uint ksceSysrootIsNonRemovableCardMode() => (KblByte(0x35) & 1u) ^ 1u;

    /// <summary>ksceSysrootIsSafeMode (0x834439A7)(): boot mode byte +0x33 set; or a wakeup of kind 0x18, 0x19 or
    /// 0x1F; or of kind 0xB (or 4 with KBL +0xD0 set) with the boot loader's bits 0x54 &lt;&lt; 8 and 0xC0 of KBL
    /// +0xCC all clear.</summary>
    [Hle(Nid = 0x834439A7, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x834439A7, Library = "SceSysrootForDriver")]
    bool ksceSysrootIsSafeMode()
    {
        if (KblByte(0x33) != 0xFF)
        {
            return true;
        }

        uint kind = WakeupKind;
        if (kind is 0x18 or 0x19 or 0x1F)
        {
            return true;
        }

        if (kind != 0xB && (kind != 4 || KblWord(0xD0) == 0))
        {
            return false;
        }

        uint inverted = ~KblWord(0xCC);
        return ((inverted >> 8) & 0x54) == 0x54 && (inverted & 0xC0) == 0;
    }

    /// <summary>ksceSysrootGetSelfInfo (0xF10AB792)(index, info): the address and size of boot image
    /// <paramref name="index"/> (0..2) from the table at +0x3A0 into info (+0 its size, 0xC); index 0x80020005, NULL
    /// 0x80020006, another size 0x80020009, no table 0x8001000C, an empty entry 0x80010002.</summary>
    [Hle(Nid = 0xF10AB792, Library = "SceSysrootForKernel")]
    int ksceSysrootGetSelfInfo(uint index, uint info)
    {
        if (index > 2)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestMemory.R32(info) != 0xC)
        {
            return unchecked((int)0x80020009);
        }

        uint table = SysrootData(0x3A0);
        if (table == 0)
        {
            return unchecked((int)0x8001000C);
        }

        uint size = GuestMemory.R32(table + index * 8 + 4);
        if (size == 0)
        {
            return unchecked((int)0x80010002);
        }

        GuestMemory.W32(info + 4, GuestMemory.R32(table + index * 8));
        GuestMemory.W32(info + 8, size);
        return 0;
    }

    // ------------------------------------------------------------------ init callbacks (+0x8C lock, +0x90 level)

    /// <summary>ksceKernelRegisterInitCallback (0x778D0966)(callback, level, argument, int *result): a level below the
    /// current one runs the callback now as callback(0, argument) (its result into *result); otherwise it takes one of
    /// the level's 8 slots. NULL callback or level above 8 0x80020005; no free slot 0x8002000F.</summary>
    [Hle(Nid = 0x778D0966, Library = "SceSysrootForDriver")]
    void NID_778D0966(HleContext context)
    {
        uint callback = context.Gpr(0);
        uint level = context.Gpr(1);
        uint argument = context.Gpr(2);
        uint result = context.Gpr(3);
        if (callback == 0 || level >= InitLevels)
        {
            context.Return(SceError.INVALID_ARGUMENT);
            return;
        }

        uint block = SysrootBlock();
        if (level < GuestMemory.R32(block + 0x90))
        {
            uint value = context.Thread.CallGuest(callback, [0, argument]);
            if (result != 0)
            {
                GuestMemory.W32(result, value);
            }

            context.Return(0);
            return;
        }

        lock (_sysrootStatusLock)
        {
            for (uint slot = 0; slot < InitCallbacksPerLevel; slot++)
            {
                uint entry = InitCallbackEntry(block, level, slot);
                if (GuestMemory.R32(entry) == 0)
                {
                    GuestMemory.W32(entry, callback);
                    GuestMemory.W32(entry + 4, argument);
                    context.Return(0);
                    return;
                }
            }
        }

        context.Return(unchecked((int)0x8002000F));
    }

    static uint InitCallbackEntry(uint block, uint level, uint slot) => block + 0x94 + level * 0x40 + slot * 8;

    /// <summary>Runs the init levels after the current one up to <paramref name="level"/>: each level's callbacks in
    /// slot order as callback(<paramref name="flag"/>, argument), then the level counts as reached.</summary>
    void RunInitCallbacks(HleContext context, uint level, uint flag)
    {
        uint block = SysrootBlock();
        while (true)
        {
            uint next;
            lock (_sysrootStatusLock)
            {
                uint current = GuestMemory.R32(block + 0x90);
                if (level <= current)
                {
                    return;
                }

                next = current + 1;
            }

            for (uint slot = 0; slot < InitCallbacksPerLevel; slot++)
            {
                uint entry = InitCallbackEntry(block, next, slot);
                uint callback = GuestMemory.R32(entry);
                if (callback == 0)
                {
                    break;
                }

                context.Thread.CallGuest(callback, [flag, GuestMemory.R32(entry + 4)]);
            }

            lock (_sysrootStatusLock)
            {
                GuestMemory.W32(block + 0x90, next);
            }
        }
    }

    /// <summary>ksceKernelInvokeInitCallback (0x93CD44CD)(level): runs the init levels up to
    /// <paramref name="level"/> (below 9) with flag 0.</summary>
    [Hle(Nid = 0x93CD44CD, Library = "SceSysrootForDriver")]
    void ksceKernelInvokeInitCallback(HleContext context)
    {
        uint level = context.Gpr(0);
        if (level < InitLevels)
        {
            RunInitCallbacks(context, level, 0);
        }
    }

    /// <summary>NID_7C2C10E2(level, flag): as ksceKernelInvokeInitCallback with the callbacks' first argument
    /// <paramref name="flag"/> (0 or 1; other values do nothing).</summary>
    [Hle(Nid = 0x7C2C10E2, Library = "SceSysrootForDriver")]
    void NID_7C2C10E2(HleContext context)
    {
        uint level = context.Gpr(0);
        uint flag = context.Gpr(1);
        if (level < InitLevels && flag < 2)
        {
            RunInitCallbacks(context, level, flag);
        }
    }

    // ------------------------------------------------------------------ the running thread's kernel state word

    /// <summary>NID_44EA3197(): the running thread's kernel state block (the word at TPIDRPRW + 0x44). VitaSharp's
    /// thread manager runs in HLE and keeps no such block (TPIDRPRW reads 0 on its CPU), so the firmware's own
    /// answer for that case: 0.</summary>
    [Hle(Nid = 0x44EA3197, Library = "SceSysrootForDriver", Default = true)]
    uint NID_44EA3197() => 0;

    /// <summary>NID_D86BD6DC(): state 0 into the thread's kernel state block (+0x40); nothing without the block (see
    /// <see cref="NID_44EA3197"/>).</summary>
    [Hle(Nid = 0xD86BD6DC, Library = "SceSysrootForDriver", Default = true)]
    void NID_D86BD6DC()
    {
    }

    /// <summary>NID_8B8F1D01(): state 1 (see <see cref="NID_D86BD6DC"/>).</summary>
    [Hle(Nid = 0x8B8F1D01, Library = "SceSysrootForDriver", Default = true)]
    void NID_8B8F1D01()
    {
    }

    /// <summary>NID_6E0BC27C(): state 2 (see <see cref="NID_D86BD6DC"/>).</summary>
    [Hle(Nid = 0x6E0BC27C, Library = "SceSysrootForDriver", Default = true)]
    void NID_6E0BC27C()
    {
    }

    /// <summary>NID_2A5DBD38(): state 3 (see <see cref="NID_D86BD6DC"/>).</summary>
    [Hle(Nid = 0x2A5DBD38, Library = "SceSysrootForDriver", Default = true)]
    void NID_2A5DBD38()
    {
    }

    /// <summary>NID_8A760856(): state 4 (see <see cref="NID_D86BD6DC"/>).</summary>
    [Hle(Nid = 0x8A760856, Library = "SceSysrootForDriver", Default = true)]
    void NID_8A760856()
    {
    }

    /// <summary>NID_8CD02748(value): state value | 0x80000000 (see <see cref="NID_D86BD6DC"/>).</summary>
    [Hle(Nid = 0x8CD02748, Library = "SceSysrootForDriver", Default = true)]
    void NID_8CD02748()
    {
    }
}
