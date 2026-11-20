using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// More SceProcessmgrForKernel/ForDriver calls on the process object of FW 3.74 processmgr (decompiled, study only):
/// the two words at +0x20, the per-process random words, the debugging tables the object points at (+0xC: five pairs,
/// +0x10: four pairs, +0x288: one pair; debugging console only) and the two counters at +0x40C and +0x40E. VitaSharp's
/// process object keeps those tables right after the firmware's layout (+0x440, +0x470, +0x498), so the pointers are
/// real.
/// </summary>
public sealed partial class ProcessmgrKernel
{
    /// <summary>The process type of the kernel's process (+0x40): calls that refuse it answer 0x80029002.</summary>
    const uint KernelProcessType = 0x5000000;

    /// <summary>The counters' limits: past them 0x8002450D, below 0 SCE_KERNEL_ERROR_ILLEGAL_COUNT.</summary>
    const int CounterOverflow = unchecked((int)0x8002450D);

    /// <summary>NID_5BDA978F(pid, uint out[2]): the +0x20 and +0x24 words (pid 0: the caller's process); a null out is
    /// SCE_KERNEL_ERROR_ILLEGAL_ADDR.</summary>
    [Hle(Nid = 0x5BDA978F, Library = "SceProcessmgrForDriver")]
    int NID_5BDA978F(int pid, uint output)
    {
        if (output == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint block = ProcessObject(pid);
        if (block == 0)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(output, GuestMemory.R32(block + 0x20));
        GuestMemory.W32(output + 4, GuestMemory.R32(block + 0x24));
        return 0;
    }

    /// <summary>NID_2CEB1C7A(pid, buffer, size): fills the buffer with the process's random words; a buffer or size
    /// that is not word aligned is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x2CEB1C7A, Library = "SceProcessmgrForDriver")]
    int NID_2CEB1C7A(int pid, uint buffer, uint size)
    {
        if (((buffer | size) & 3) != 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return WithProcess(pid, _ =>
        {
            Span<byte> words = GuestMemory.Span(buffer, (int)size);
            System.Security.Cryptography.RandomNumberGenerator.Fill(words);
            return 0;
        });
    }

    /// <summary>NID_060DA466(pid, index, *a, *b): pair <paramref name="index"/> (below 5) of the +0xC table (debugging
    /// console).</summary>
    [Hle(Nid = 0x060DA466, Library = "SceProcessmgrForKernel")]
    int NID_060DA466(int pid, uint index, uint first, uint second) => ReadTablePair(pid, 0xC, 5, index, first, second);

    /// <summary>NID_6898F7A7(pid, index, *a, *b): pair <paramref name="index"/> (below 4) of the +0x10 table.</summary>
    [Hle(Nid = 0x6898F7A7, Library = "SceProcessmgrForKernel")]
    int NID_6898F7A7(int pid, uint index, uint first, uint second) => ReadTablePair(pid, 0x10, 4, index, first, second);

    int ReadTablePair(int pid, uint pointerWord, uint entries, uint index, uint first, uint second)
    {
        if (!DebugConsole(DebugDipsw))
        {
            return NotAllowed;
        }

        if (index >= entries)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (first == 0 || second == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return WithProcess(pid, block =>
        {
            uint table = GuestMemory.R32(block + pointerWord);
            GuestMemory.W32(first, GuestMemory.R32(table + 8 + index * 8));
            GuestMemory.W32(second, GuestMemory.R32(table + 0xC + index * 8));
            return 0;
        });
    }

    /// <summary>NID_E025F5A9(pid, 5, *a, *b): the pair +0x288 points at (debugging console; any kind but 5 is
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT).</summary>
    [Hle(Nid = 0xE025F5A9, Library = "SceProcessmgrForKernel")]
    int NID_E025F5A9(int pid, int kind, uint first, uint second)
    {
        if (!DebugConsole(DebugDipsw))
        {
            return NotAllowed;
        }

        if (kind != 5)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (first == 0 || second == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return WithProcess(pid, block =>
        {
            uint pair = GuestMemory.R32(block + 0x288);
            GuestMemory.W32(first, GuestMemory.R32(pair));
            GuestMemory.W32(second, GuestMemory.R32(pair + 4));
            return 0;
        });
    }

    /// <summary>NID_FC80A606(pid, 5, a, b): sets that pair.</summary>
    [Hle(Nid = 0xFC80A606, Library = "SceProcessmgrForKernel")]
    int NID_FC80A606(int pid, int kind, uint first, uint second)
    {
        if (!DebugConsole(DebugDipsw))
        {
            return NotAllowed;
        }

        if (kind != 5)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return WithProcess(pid, block =>
        {
            uint pair = GuestMemory.R32(block + 0x288);
            GuestMemory.W32(pair, first);
            GuestMemory.W32(pair + 4, second);
            return 0;
        });
    }

    /// <summary>NID_8B4086AF(pid, which): counts one up: the +0x40E counter (which 1, at most 0x20) or the +0x40C one
    /// (at most 0x80); the kernel's process is 0x80029002, and a process of type 0x4000000 has no +0x40E counter.</summary>
    [Hle(Nid = 0x8B4086AF, Library = "SceProcessmgrForKernel")]
    int NID_8B4086AF(int pid, int which) => CountProcess(pid, which, 1);

    /// <summary>NID_F160B4D8(pid, which): counts one down (below 0 is SCE_KERNEL_ERROR_ILLEGAL_COUNT).</summary>
    [Hle(Nid = 0xF160B4D8, Library = "SceProcessmgrForKernel")]
    int NID_F160B4D8(int pid, int which) => CountProcess(pid, which, -1);

    int CountProcess(int pid, int which, int step)
    {
        return WithProcess(pid, block =>
        {
            uint type = GuestMemory.R32(block + 0x40);
            if (type == KernelProcessType || (type == 0x4000000 && which == 1))
            {
                return KernelProcessRefused;
            }

            bool second = which == 1;
            uint address = block + (second ? 0x40Eu : 0x40Cu);
            int limit = second ? 0x20 : 0x80;
            lock (_processObjects)
            {
                int value = GuestMemory.Read<ushort>(address);
                if (step > 0 && value >= limit)
                {
                    return CounterOverflow;
                }

                if (step < 0 && value == 0)
                {
                    return SceError.ILLEGAL_COUNT;
                }

                GuestMemory.Write(address, (ushort)(value + step));
                return 0;
            }
        });
    }
}
