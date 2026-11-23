using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceDebugForKernel (sysmem 3.74, read in the module, study only): the kernel debug console's handler slots (the
// module keeps one pointer per slot and calls it from its own paths: the exception printer, the module and symbol
// lookups of the dumps, the event log's flush), its character ring (one character into the 0x1000-byte ring the
// console reads), and the printers of a syscall frame and of an exception; the printers write to the kernel's
// console, here the log.
public sealed partial class SystemServicesVsh
{
    /// <summary>The handler slots the module keeps, by the NID that sets each.</summary>
    const int DebugSlot38F1 = 0, DebugSlot4C62 = 1, DebugSlot67E9 = 2, DebugSlot7802 = 3, DebugSlot91E7 = 4;

    [SnapshotMissing] // states of builds before it: no handler was ever set
    readonly uint[] _debugHandlers = new uint[5];

    [SnapshotIgnore]
    readonly System.Text.StringBuilder _debugRingLine = new();

    /// <summary>NID_0EDA5174 (SceDebugForKernel)(): the module answers 0.</summary>
    [Hle("NID_0EDA5174", Nid = 0x0EDA5174, Library = "SceDebugForKernel")]
    int DebugKernelZero()
    {
        return 0;
    }

    [Hle("NID_38F1A352", Nid = 0x38F1A352, Library = "SceDebugForKernel")]
    void DebugSetHandler38F1(uint handler)
    {
        _debugHandlers[DebugSlot38F1] = handler;
    }

    [Hle("NID_4C62F8CC", Nid = 0x4C62F8CC, Library = "SceDebugForKernel")]
    void DebugSetHandler4C62(uint handler)
    {
        _debugHandlers[DebugSlot4C62] = handler;
    }

    [Hle("NID_67E9D1EC", Nid = 0x67E9D1EC, Library = "SceDebugForKernel")]
    void DebugSetHandler67E9(uint handler)
    {
        _debugHandlers[DebugSlot67E9] = handler;
    }

    [Hle("NID_78027203", Nid = 0x78027203, Library = "SceDebugForKernel")]
    void DebugSetHandler7802(uint handler)
    {
        _debugHandlers[DebugSlot7802] = handler;
    }

    [Hle("NID_91E7D24F", Nid = 0x91E7D24F, Library = "SceDebugForKernel")]
    void DebugSetHandler91E7(uint handler)
    {
        _debugHandlers[DebugSlot91E7] = handler;
    }

    /// <summary>NID_3A21194E (SceDebugForKernel)(char c): one character into the console's ring (each line goes to
    /// the log); 1.</summary>
    [Hle("NID_3A21194E", Nid = 0x3A21194E, Library = "SceDebugForKernel")]
    int DebugPutRing(int character)
    {
        char c = (char)(byte)character;
        if (c == '\n')
        {
            Log.Info($"[kernel] {_debugRingLine}");
            _debugRingLine.Clear();
        }
        else if (c != '\r')
        {
            _debugRingLine.Append(c);
        }

        return 1;
    }

    /// <summary>NID_BCC8D0B2 (SceDebugForKernel)(SceUID pid, const uint *frame, ...): prints a user process's syscall
    /// frame (r0-r3, r12, ...); nothing for the kernel (0x10005) or without a frame.</summary>
    [Hle("NID_BCC8D0B2", Nid = 0xBCC8D0B2, Library = "SceDebugForKernel")]
    void DebugPrintSyscallFrame(int pid, uint frame)
    {
        if (pid == 0x10005 || frame == 0)
        {
            return;
        }

        Log.Info("=== Syscall Frame ===");
        Log.Info($"r0-r3:     0x{GuestMemory.R32(frame):X8} 0x{GuestMemory.R32(frame + 4):X8} " +
            $"0x{GuestMemory.R32(frame + 8):X8} 0x{GuestMemory.R32(frame + 12):X8}");
        Log.Info($"r12:       0x{GuestMemory.R32(frame + 0x30):X8}");
    }

    /// <summary>NID_CCABDD98 (SceDebugForKernel)(const uint *where, context, ..., const char *kind): prints the head
    /// line of a kernel exception ("*** Kernel %s Exception at CPU%d &lt;0x%08x%08x:0x%08x&gt;").</summary>
    [Hle("NID_CCABDD98", Nid = 0xCCABDD98, Library = "SceDebugForKernel")]
    void DebugPrintKernelException(uint where, uint context, uint unused, uint kind)
    {
        string name = kind == 0 ? "" : GuestMemory.ReadCString(kind, 0x40);
        uint high = where == 0 ? 0 : GuestMemory.R32(where);
        uint low = where == 0 ? 0 : GuestMemory.R32(where + 4);
        uint pc = where == 0 ? 0 : GuestMemory.R32(where + 8);
        Log.Info($"*** Kernel {name} Exception at CPU0 <0x{high:X8}{low:X8}:0x{pc:X8}>");
    }

    /// <summary>NID_5D6F0CFC (SceDebugForKernel)(...): the long form of the exception dump (registers, the module of
    /// the pc); the head line here.</summary>
    [Hle("NID_5D6F0CFC", Nid = 0x5D6F0CFC, Library = "SceDebugForKernel")]
    void DebugPrintExceptionDump(uint where, uint context)
    {
        DebugPrintKernelExceptionThroughEntry(where, context, 0, 0);
    }
}
