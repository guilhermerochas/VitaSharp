using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The dump queue of SceCoredump (os0:bootimage SceCoredump 3.74, read in the module, study only). A checked request
// (CoredumpRequestError) takes one of the module's two request slots (FUN_81006f10: both taken, 0x8001000C), gets the
// next dump id (from 1, back to 1 at 0x7FFFFFFF), and goes to the dump thread, which writes the process's core file
// "ux0:data/psp2core-<id>-0x<pid>-<title>.psp2dmp" (the module's format psp2core-%010u-0x%010x-%s; the dump id
// and the process id here) (written as .tmp, renamed when done) and frees the slot. A retail
// unit keeps no core file without the QA flag; the emulator keeps them only when asked: VITASHARP_COREDUMP_DIR names
// a host folder (never the user's data folder unless they name it) where the files go. Without it every checked
// request is refused as on a retail unit (0x80020008).
//
// The file is an ELF core (ET_CORE, EM_ARM): one PT_LOAD per memory block of the process (its contents at the time of
// the dump) and a PT_NOTE with one "VITASHARP" note (type 1, text: the process and its threads with their registers).
// The firmware's own notes (PROCESS_INFO, THREAD_INFO, THREAD_REG_INFO, MODULE_INFO and the others the module names)
// are not written yet.
public sealed partial class VshBridge
{
    /// <summary>SceCoredump: both request slots are taken (0x8001000C).</summary>
    const int COREDUMP_ERR_BUSY = unchecked((int)0x8001000C);

    const int CoredumpSlots = 2;

    /// <summary>The host folder the core files go to (<see cref="EnvVars.CoredumpDir"/>); null = none kept.</summary>
    [SnapshotIgnore]
    internal string? CoredumpDirectory = Environment.GetEnvironmentVariable(EnvVars.CoredumpDir) is { Length: > 0 } dir ? dir : null;

    /// <summary>Requests in the two slots (the dump thread frees them).</summary>
    [SnapshotIgnore]
    private int _coredumpSlotsTaken;

    [SnapshotIgnore]
    private int _nextDumpId = 1;

    /// <summary>The dumps queued and not written yet (tests wait on it).</summary>
    [SnapshotIgnore]
    private readonly List<Task> _coredumpWrites = new();

    /// <summary>Tests: holds the dump thread before it writes (the slots stay taken).</summary>
    [SnapshotIgnore]
    internal ManualResetEventSlim? CoredumpGate;

    /// <summary>A checked request: refused without a core folder, else queued in a free slot (or 0x8001000C).</summary>
    private int QueueCoredump(int pid)
    {
        if (CoredumpDirectory is not { } directory)
        {
            return SceError.ILLEGAL_PERMISSION;
        }

        int id;
        lock (_coredumpWrites)
        {
            if (_coredumpSlotsTaken >= CoredumpSlots)
            {
                return COREDUMP_ERR_BUSY;
            }

            _coredumpSlotsTaken++;
            id = _nextDumpId;
            _nextDumpId = _nextDumpId + 1 == 0x7FFFFFFF ? 1 : _nextDumpId + 1;
        }

        Emulator process = (pid == 0 ? null : Emu.Vita.ByPid(pid)) ?? Emu;
        ManualResetEventSlim? gate = CoredumpGate;
        Task write = Task.Run(() =>
        {
            try
            {
                gate?.Wait();
                WriteCoreFile(directory, id, process);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"[coredump] dump {id} of {process.Describe()} not written: {e.Message}");
            }
            finally
            {
                lock (_coredumpWrites)
                {
                    _coredumpSlotsTaken--;
                }
            }
        });
        lock (_coredumpWrites)
        {
            _coredumpWrites.RemoveAll(t => t.IsCompleted);
            _coredumpWrites.Add(write);
        }

        return 0;
    }

    /// <summary>Tests: waits until the queued dumps are written.</summary>
    internal void WaitForCoredumps()
    {
        Task[] pending;
        lock (_coredumpWrites)
        {
            pending = _coredumpWrites.ToArray();
        }

        Task.WaitAll(pending);
    }

    /// <summary>The core file's name, as the module formats it (psp2core-%010u-0x%010x-%s.psp2dmp).</summary>
    internal static string CoreFileName(uint id, uint pid, string titleId) => $"psp2core-{id:D10}-0x{pid:x10}-{titleId}.psp2dmp";

    private void WriteCoreFile(string directory, int id, Emulator process)
    {
        Directory.CreateDirectory(directory);
        string name = CoreFileName((uint)id, (uint)process.Pid, process.TitleId.Length > 0 ? process.TitleId : "unknown");
        string path = Path.Combine(directory, name);
        string temporary = path + ".tmp";
        List<GuestMemory.Block> blocks = Emu.Memory.Blocks().FindAll(b => b.Region != MemoryRegion.Kernel && (b.Space == null || ReferenceEquals(b.Space, process.Space)));
        byte[] note = CoreNote(id, process);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            WriteCore(file, blocks, note, process.Space);
        }

        File.Move(temporary, path, overwrite: true);
        Log.Info($"[coredump] dump {id} of {process.Describe()} written: {path}");
    }

    /// <summary>The text of the VITASHARP note: the dump, the process and its threads with their registers.</summary>
    private static unsafe byte[] CoreNote(int id, Emulator process)
    {
        var text = new StringBuilder();
        text.AppendLine($"dump {id} of {process.Describe()} ({process.TitleId})");
        foreach (GuestThread thread in process.Kernel.Threads)
        {
            Cpu.CpuState* state = thread.Core.State;
            text.Append($"thread 0x{thread.Uid:X8} {thread.Name} status {thread.Status}:");
            for (int r = 0; r < 16; r++)
            {
                text.Append($" r{r}=0x{state->R[r]:X8}");
            }

            text.AppendLine();
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>An ELF32 core: header, program headers (PT_NOTE, then one PT_LOAD per block), the note, the blocks.</summary>
    internal static void WriteCore(Stream file, IReadOnlyList<GuestMemory.Block> blocks, byte[] noteText, AddressSpace? space)
    {
        const int HeaderSize = 0x34, ProgramHeaderSize = 0x20;
        const string NoteName = "VITASHARP";
        int phnum = 1 + blocks.Count;
        byte[] nameBytes = Encoding.ASCII.GetBytes(NoteName + "\0");
        int noteSize = 12 + Align4(nameBytes.Length) + Align4(noteText.Length);
        uint offset = (uint)(HeaderSize + phnum * ProgramHeaderSize);
        using var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
        writer.Write([0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        writer.Write((ushort)4);        // ET_CORE
        writer.Write((ushort)40);       // EM_ARM
        writer.Write(1u);
        writer.Write(0u);               // entry
        writer.Write((uint)HeaderSize); // phoff
        writer.Write(0u);               // shoff
        writer.Write(0x05000000u);      // EABI5
        writer.Write((ushort)HeaderSize);
        writer.Write((ushort)ProgramHeaderSize);
        writer.Write((ushort)phnum);
        writer.Write((ushort)0x28);
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        void ProgramHeader(uint type, uint fileOffset, uint address, uint size, uint flags)
        {
            writer.Write(type);
            writer.Write(fileOffset);
            writer.Write(address);
            writer.Write(address);
            writer.Write(size);
            writer.Write(size);
            writer.Write(flags);
            writer.Write(type == 1 ? 0x1000u : 4u);
        }

        ProgramHeader(4, offset, 0, (uint)noteSize, 4); // PT_NOTE
        uint dataOffset = offset + (uint)noteSize;
        foreach (GuestMemory.Block block in blocks)
        {
            ProgramHeader(1, dataOffset, block.Address, block.Size, 6); // PT_LOAD, read and write
            dataOffset += block.Size;
        }

        writer.Write((uint)nameBytes.Length);
        writer.Write((uint)noteText.Length);
        writer.Write(1u);
        writer.Write(nameBytes);
        writer.Write(new byte[Align4(nameBytes.Length) - nameBytes.Length]);
        writer.Write(noteText);
        writer.Write(new byte[Align4(noteText.Length) - noteText.Length]);
        using (AddressSpace.Use(space))
        {
            foreach (GuestMemory.Block block in blocks)
            {
                for (uint done = 0; done < block.Size; done += 0x10000)
                {
                    int length = (int)Math.Min(0x10000u, block.Size - done);
                    writer.Write(GuestMemory.ReadSpan(block.Address + done, length));
                }
            }
        }
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
