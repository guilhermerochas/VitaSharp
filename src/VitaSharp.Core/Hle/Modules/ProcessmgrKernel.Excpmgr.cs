using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of SceExcpmgrForKernel as the FW 3.74 excpmgr.skprx has it (decompiled, study only), beside the handler
/// registration and delivery (<see cref="sceKernelRegisterExceptionHandler"/>):
/// <list type="bullet">
/// <item>unregistering a handler from its kind's chain (NID_DA7BB671);</item>
/// <item>the default handler every chain ends at: a kernel module can put its own in front of the current one
/// (NID_416C0E20; it then ends every chain and enters every kind that has no handler), and NID_4337DD78 answers it;</item>
/// <item>the exception manager's data block (ksceExcpmgrGetData): per-CPU words at +0 (the exception nesting of
/// each core, NID_B615A7DA reads the current core's when no thread runs), the words kernel modules set at +0x44,
/// +0x48, +0x80, +0x84, +0x88, and two more at +0x41D4/+0x41DC. VitaSharp keeps a block of the same layout in kernel
/// memory, made on first use;</item>
/// <item>the fatal-exception reporters the default handlers call (NID_B7B10796, NID_D195E55C, NID_64A057C7): the
/// firmware prints the exception and the thread and writes a core dump; VitaSharp writes the context's words to its
/// log.</item>
/// </list>
/// </summary>
public sealed partial class ProcessmgrKernel
{
    /// <summary>The exception manager's data block: from the firmware's 0x81004000 to its last word at 0x810081DC.</summary>
    const uint ExcpmgrDataSize = 0x4200;

    /// <summary>Unregistering a handler that is not in its kind's chain.</summary>
    const int ExceptionHandlerNotRegistered = unchecked((int)0x80027003);

    [SnapshotMissing] // states of builds before it: no call had made it
    uint _excpmgrData;

    /// <summary>The default handler a kernel module put in front of VitaSharp's own (0: none).</summary>
    [SnapshotMissing] // states of builds before it: no module had registered one
    uint _registeredDefaultHandler;

    /// <summary>The handler every chain ends at: a registered default handler, or VitaSharp's own.</summary>
    uint ChainEnd() => _registeredDefaultHandler != 0 ? _registeredDefaultHandler : DefaultExceptionHandler();

    uint ExcpmgrData()
    {
        lock (_exceptionHandlers)
        {
            if (_excpmgrData == 0)
            {
                _excpmgrData = KernelAlloc(ExcpmgrDataSize, "kernel:SceExcpmgrData");
                GuestMemory.Fill(_excpmgrData, 0, ExcpmgrDataSize);
            }

            return _excpmgrData;
        }
    }

    /// <summary>ksceExcpmgrGetData(): the exception manager's data block.</summary>
    [Hle(Nid = 0x96C2869C, Library = "SceExcpmgrForKernel", ReturnsValue = true)]
    uint ksceExcpmgrGetData() => ExcpmgrData();

    /// <summary>NID_5420ED8F(index): word <paramref name="index"/> of the data block.</summary>
    [Hle(Nid = 0x5420ED8F, Library = "SceExcpmgrForKernel", ReturnsValue = true)]
    uint NID_5420ED8F(int index) => GuestMemory.R32(ExcpmgrData() + (uint)index * 4);

    /// <summary>NID_B615A7DA(): 0 while a thread runs; otherwise the current core's exception nesting word (+0, the
    /// core VitaSharp's host calls run as is core 0).</summary>
    [Hle(Nid = 0xB615A7DA, Library = "SceExcpmgrForKernel", ReturnsValue = true)]
    uint NID_B615A7DA() => GuestThread.Current != null ? 0 : GuestMemory.R32(ExcpmgrData());

    /// <summary>NID_293DFA04(value): the data block's +0x44 word.</summary>
    [Hle(Nid = 0x293DFA04, Library = "SceExcpmgrForKernel")]
    void NID_293DFA04(uint value) => GuestMemory.W32(ExcpmgrData() + 0x44, value);

    /// <summary>NID_E7487AFD(value): the data block's +0x48 word.</summary>
    [Hle(Nid = 0xE7487AFD, Library = "SceExcpmgrForKernel")]
    void NID_E7487AFD(uint value) => GuestMemory.W32(ExcpmgrData() + 0x48, value);

    /// <summary>NID_1FBF5654(value): the data block's +0x80 word.</summary>
    [Hle(Nid = 0x1FBF5654, Library = "SceExcpmgrForKernel")]
    void NID_1FBF5654(uint value) => GuestMemory.W32(ExcpmgrData() + 0x80, value);

    /// <summary>NID_58F7212B(value): the data block's +0x84 word.</summary>
    [Hle(Nid = 0x58F7212B, Library = "SceExcpmgrForKernel")]
    void NID_58F7212B(uint value) => GuestMemory.W32(ExcpmgrData() + 0x84, value);

    /// <summary>NID_9EE59C6E(value): the data block's +0x88 word.</summary>
    [Hle(Nid = 0x9EE59C6E, Library = "SceExcpmgrForKernel")]
    void NID_9EE59C6E(uint value) => GuestMemory.W32(ExcpmgrData() + 0x88, value);

    /// <summary>NID_44CE04B8(a, b): the +0x41DC and +0x41D4 words; 0.</summary>
    [Hle(Nid = 0x44CE04B8, Library = "SceExcpmgrForKernel")]
    int NID_44CE04B8(uint first, uint second)
    {
        uint data = ExcpmgrData();
        GuestMemory.W32(data + 0x41DC, first);
        GuestMemory.W32(data + 0x41D4, second);
        return 0;
    }

    /// <summary>NID_4337DD78(): the default handler every chain ends at.</summary>
    [Hle(Nid = 0x4337DD78, Library = "SceExcpmgrForKernel", ReturnsValue = true)]
    uint NID_4337DD78()
    {
        lock (_exceptionHandlers)
        {
            return ChainEnd();
        }
    }

    /// <summary>
    /// NID_416C0E20(handler): a new default handler in front of the current one: its next is the current one, the chain
    /// of every kind that ended at the current one ends at it, and every kind with no handler enters it. Its first two
    /// words must be zero (SCE_KERNEL_ERROR_ILLEGAL_EXCPHANDLER).
    /// </summary>
    [Hle(Nid = 0x416C0E20, Library = "SceExcpmgrForKernel")]
    int NID_416C0E20(uint handler)
    {
        uint record = handler & ~3u;
        if (handler == 0 || GuestMemory.R32(record) != 0 || GuestMemory.R32(record + 4) != 0)
        {
            return SceError.ILLEGAL_EXCPHANDLER;
        }

        lock (_exceptionHandlers)
        {
            uint earlier = ChainEnd();
            GuestMemory.W32(record, earlier);
            for (uint kind = 0; kind < ExceptionKinds; kind++)
            {
                uint at = _exceptionChainHeads.GetValueOrDefault(kind);
                while (at != 0 && _exceptionHandlers.ContainsKey(at))
                {
                    uint next = GuestMemory.R32(at & ~3u);
                    if (next == earlier)
                    {
                        GuestMemory.W32(at & ~3u, handler);
                        break;
                    }

                    at = next;
                }
            }

            _registeredDefaultHandler = handler;
        }

        return 0;
    }

    /// <summary>
    /// NID_DA7BB671(kind, handler): takes a handler out of its kind's chain (its predecessor, or the chain's start, then
    /// points at its next). A null handler is SCE_KERNEL_ERROR_ILLEGAL_ADDR, a record whose next word is 0 (never
    /// registered) SCE_KERNEL_ERROR_ILLEGAL_EXCPHANDLER, a kind above 7 SCE_KERNEL_ERROR_ILLEGAL_EXCPCODE, a handler not
    /// in the chain 0x80027003.
    /// </summary>
    [Hle(Nid = 0xDA7BB671, Library = "SceExcpmgrForKernel")]
    int NID_DA7BB671(uint kind, uint handler)
    {
        if (handler == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint record = handler & ~3u;
        if (GuestMemory.R32(record) == 0)
        {
            return SceError.ILLEGAL_EXCPHANDLER;
        }

        if (kind >= ExceptionKinds)
        {
            return SceError.ILLEGAL_EXCPCODE;
        }

        lock (_exceptionHandlers)
        {
            uint previous = 0;
            uint at = _exceptionChainHeads.GetValueOrDefault(kind);
            while (at != 0 && _exceptionHandlers.ContainsKey(at))
            {
                uint next = GuestMemory.R32(at & ~3u);
                if (at == handler)
                {
                    if (previous == 0)
                    {
                        if (next == ChainEnd() && _registeredDefaultHandler == 0)
                        {
                            _exceptionChainHeads.Remove(kind);
                        }
                        else
                        {
                            _exceptionChainHeads[kind] = next;
                        }
                    }
                    else
                    {
                        GuestMemory.W32(previous & ~3u, next);
                    }

                    _exceptionHandlers.Remove(handler);
                    return 0;
                }

                previous = at;
                at = next;
            }
        }

        return ExceptionHandlerNotRegistered;
    }

    /// <summary>NID_B7B10796(context): the data abort reporter: logs the context's words (the firmware prints the
    /// exception and writes a core dump).</summary>
    [Hle(Nid = 0xB7B10796, Library = "SceExcpmgrForKernel")]
    void NID_B7B10796(uint context) => ReportException("data abort", context);

    /// <summary>NID_D195E55C(context): the prefetch abort reporter.</summary>
    [Hle(Nid = 0xD195E55C, Library = "SceExcpmgrForKernel")]
    void NID_D195E55C(uint context) => ReportException("prefetch abort", context);

    /// <summary>NID_64A057C7(context): the undefined instruction reporter.</summary>
    [Hle(Nid = 0x64A057C7, Library = "SceExcpmgrForKernel")]
    void NID_64A057C7(uint context) => ReportException("undefined instruction", context);

    void ReportException(string what, uint context)
    {
        if (context == 0)
        {
            Log.Warn($"excpmgr: fatal {what} reported without a context");
            return;
        }

        Log.Warn($"excpmgr: fatal {what}: context 0x{context:X8} +0x44 0x{GuestMemory.R32(context + 0x44):X8} +0x60 0x{GuestMemory.R32(context + 0x60):X8}");
    }
}
