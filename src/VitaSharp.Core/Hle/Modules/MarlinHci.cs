using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMarlinHci: the syscalls of os0:kd/marlin_hci.skprx (FW 3.74), the kernel side of the Marlin DRM (the "HCI" key
/// engine the video store's Marlin library, libmln, drives).
/// <para>
/// How the module works: every entry first checks bit 0x80 of the caller's capability (SceSblACMgrForDriver_F5AE24AC;
/// others get 0x80010058). Entries that take a parameter block copy it from user memory, then copy every
/// (size, pointer) pair inside it whose size is 1..0xFFFF and whose pointer is not NULL (other pairs are passed on
/// without data), and run the engine on a 0x4000-byte stack; when the engine call does not fail with a kernel error
/// the entry writes the engine's output word (0 when the engine failed) back to the user and returns that copy's
/// result. The engine's own errors are positive codes (3 bad argument or unknown handle, 2 no memory, 0x1106xxxx file
/// errors, 0x2311xxxx engine errors), so they do not reach the caller of those entries. Every engine object is made
/// from the console's Marlin keys, read from tm0:nphome/sdata/P0000000/P0000001.DAT (P0000004.DAT for the second key
/// kind) and decrypted with the console's device key; the objects are kept in a table of 32 handles (index | 0x1000).
/// The module also exports its start and stop (NID_68A5C796, NID_258C55B6: new mutex, handle table cleared / table
/// cleared, mutex deleted) and the lock and unlock of its "SceMarlinHciMutex" (NID_4406594F, NID_457F6683). The 24
/// entries below 0x8100EA60 are a second set that calls the first one with buffers of their own and copies the
/// results out.
/// </para>
/// <para>
/// VitaSharp does not run the key engine (it needs the console's device key, which an emulated unit does not have):
/// reading the key file answers as when it is absent (0x11060002), so no engine object or handle is ever made and every
/// entry answers as the firmware does then; the user copies, the size limits, the capability check, the mutex and the
/// key file's removal are the module's own. These entries are marked Default in the coverage.
/// </para>
/// </summary>
public sealed partial class SystemServicesVsh
{
    internal const int MarlinCapabilityBit = 0x80;
    internal const int MarlinErrNotAllowed = unchecked((int)0x80010058);
    internal const int MarlinErrTooLarge = unchecked((int)0x8002710B);

    /// <summary>The engine's "bad argument or unknown handle" code.</summary>
    internal const int MarlinEngineInvalid = 3;

    /// <summary>The engine's code for a key file that could not be opened or written.</summary>
    internal const int MarlinEngineFileError = 0x11060001;

    /// <summary>The engine's code for a key file that is not there.</summary>
    internal const int MarlinEngineFileMissing = 0x11060002;

    /// <summary>Where the module keeps the console's Marlin keys (FUN_810059d0: tm0:nphome + sdata/P0000000).</summary>
    internal const string MarlinKeyFolder = "tm0:nphome/sdata/P0000000";

    private const uint MarlinPairLimit = 0xFFFF;

    private bool MarlinAllowed => Emu.Access.HasCapabilityBit(MarlinCapabilityBit);

    /// <summary>The key file of kind 0 (P0000001.DAT) or 1 (P0000004.DAT).</summary>
    internal static string MarlinKeyFile(int kind) => MarlinKeyFolder + (kind == 0 ? "/P0000001.DAT" : "/P0000004.DAT");

    /// <summary>ksceKernelCopyToUser of the engine's output word: 0 when the engine made nothing.</summary>
    private static int MarlinWriteWord(uint address, uint value)
    {
        if (!KernelSpace.IsUserRange(address, 4))
        {
            return KernelSpace.UserCopyFault;
        }

        GuestMemory.W32(address, value);
        return 0;
    }

    /// <summary>
    /// The copy of a (size, pointer) pair's data the entries make: only a size of 1..0xFFFF with a pointer is copied;
    /// <paramref name="pair"/> is the guest address of the size word, the pointer follows it.
    /// </summary>
    private static bool MarlinPairReadable(uint pair)
    {
        uint size = GuestMemory.R32(pair);
        uint data = GuestMemory.R32(pair + 4);
        if (size == 0 || data == 0 || size > MarlinPairLimit)
        {
            return true;
        }

        return KernelSpace.IsUserRange(data, size);
    }

    /// <summary>
    /// The entries that take one parameter block: copy <paramref name="size"/> bytes from <paramref name="block"/>,
    /// then the pairs at <paramref name="pairOffsets"/>; the engine finds no handle (or no key file) and the output
    /// word at <paramref name="outputOffset"/> is written 0.
    /// </summary>
    private int MarlinBlockCall(uint block, uint size, ReadOnlySpan<byte> pairOffsets, uint outputOffset, uint eightByteOffset = 0)
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        if (!KernelSpace.IsUserRange(block, size))
        {
            return KernelSpace.UserCopyFault;
        }

        if (eightByteOffset != 0)
        {
            uint eight = GuestMemory.R32(block + eightByteOffset);
            if (eight != 0 && !KernelSpace.IsUserRange(eight, 8))
            {
                return KernelSpace.UserCopyFault;
            }
        }

        foreach (byte offset in pairOffsets)
        {
            if (!MarlinPairReadable(block + offset))
            {
                return KernelSpace.UserCopyFault;
            }
        }

        return MarlinWriteWord(block + outputOffset, 0);
    }

    /// <summary>A (size, pointer) pair passed by address: the 8 bytes are copied, then its data.</summary>
    private static bool MarlinPairArgumentReadable(uint pair) =>
        pair == 0 || (KernelSpace.IsUserRange(pair, 8) && MarlinPairReadable(pair));

    // ------------------------------------------------------------------ module start, stop and mutex

    /// <summary>The module's mutex, made as its boot start makes it the first time it is needed.</summary>
    private int MarlinMutex()
    {
        VitaSystem vita = Emu.Vita;
        lock (vita.MarlinHciGate)
        {
            if (vita.MarlinHciMutex == 0)
            {
                vita.MarlinHciMutex = MarlinCreateMutex();
            }

            return vita.MarlinHciMutex;
        }
    }

    private int MarlinCreateMutex()
    {
        var mutex = new Mutex { Name = "SceMarlinHciMutex", Attr = 0, InitCount = 0 };
        return K.Objects.Register(mutex);
    }

    /// <summary>NID_68A5C796 (module_start): a new mutex (the earlier one is left as the module leaves it), the key
    /// heap's user count raised, the handle table cleared; 0.</summary>
    [Hle("NID_68A5C796", Nid = 0x68A5C796, Library = "SceMarlinHci")]
    [Hle("NID_55682EDD", Nid = 0x55682EDD, Library = "SceMarlinHci")]
    private int MarlinStart()
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        VitaSystem vita = Emu.Vita;
        lock (vita.MarlinHciGate)
        {
            if (vita.MarlinHciMutex == 0)
            {
                vita.MarlinHciMutex = MarlinCreateMutex();
            }

            vita.MarlinHciMutex = MarlinCreateMutex();
        }

        return 0;
    }

    /// <summary>NID_258C55B6 (module_stop): the handle table cleared, the cached keys freed, the mutex deleted (its
    /// id becomes -1, so a later lock fails until a start); 0.</summary>
    [Hle("NID_258C55B6", Nid = 0x258C55B6, Library = "SceMarlinHci")]
    [Hle("NID_1ACDD25B", Nid = 0x1ACDD25B, Library = "SceMarlinHci")]
    private int MarlinStop()
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        int uid = MarlinMutex();
        if (K.Objects.Get<Mutex>(uid) is { } mutex)
        {
            mutex.Delete();
            K.Objects.Delete(mutex);
        }

        lock (Emu.Vita.MarlinHciGate)
        {
            Emu.Vita.MarlinHciMutex = -1;
        }

        return 0;
    }

    /// <summary>NID_4406594F (): ksceKernelLockMutex(mutex, 1, NULL) on the module's mutex; its result.</summary>
    [Hle("NID_4406594F", Nid = 0x4406594F, Library = "SceMarlinHci", Cost = HleCost.SchedulerWait)]
    [Hle("NID_06F699FF", Nid = 0x06F699FF, Library = "SceMarlinHci", Cost = HleCost.SchedulerWait)]
    private int MarlinLock()
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        return K.Objects.Get<Mutex>(MarlinMutex())?.Lock(GuestThread.Current!, 1, default) ?? SceError.UNKNOWN_MUTEX_ID;
    }

    /// <summary>NID_457F6683 (): frees the cached keys and unlocks the module's mutex (its result is not looked at); 0.</summary>
    [Hle("NID_457F6683", Nid = 0x457F6683, Library = "SceMarlinHci", Cost = HleCost.SchedulerWait)]
    [Hle("NID_6D915D7B", Nid = 0x6D915D7B, Library = "SceMarlinHci", Cost = HleCost.SchedulerWait)]
    private int MarlinUnlock()
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        K.Objects.Get<Mutex>(MarlinMutex())?.Unlock(GuestThread.Current!, 1);
        return 0;
    }

    // ------------------------------------------------------------------ handles and key objects

    /// <summary>NID_C3E201D5 (int kind, SceUID *handle): makes a key object of kind 0..3 from the console's keys; the
    /// key file is not there, so the handle written is 0 and the entry returns the copy's result.</summary>
    [Hle("NID_C3E201D5", Nid = 0xC3E201D5, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_75D04A51", Nid = 0x75D04A51, Library = "SceMarlinHci", Default = true)]
    private int MarlinMakeKeyObject(uint kind, uint handle) => MarlinAllowed ? MarlinWriteWord(handle, 0) : MarlinErrNotAllowed;

    /// <summary>NID_4DADE2FD (SceUID *handle): the key object of the console's second key; as NID_C3E201D5.</summary>
    [Hle("NID_4DADE2FD", Nid = 0x4DADE2FD, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_0E46FA73", Nid = 0x0E46FA73, Library = "SceMarlinHci", Default = true)]
    private int MarlinMakeSecondKeyObject(uint handle) => MarlinMakeKeyObjectThroughEntry(0, handle);

    /// <summary>NID_68315912 (SceUID handle, SceUID *derived), NID_527EB697 (SceUID handle, u32 *value): the handle is
    /// not in the table, the word written is 0 and the entry returns the copy's result.</summary>
    [Hle("NID_68315912", Nid = 0x68315912, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_E20887CB", Nid = 0xE20887CB, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_527EB697", Nid = 0x527EB697, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_E9460764", Nid = 0xE9460764, Library = "SceMarlinHci", Default = true)]
    private int MarlinFromHandle(uint handle, uint output) => MarlinAllowed ? MarlinWriteWord(output, 0) : MarlinErrNotAllowed;

    /// <summary>NID_A626EBBC (SceUID handle): the object's state word, 0 for a handle not in the table.</summary>
    [Hle("NID_A626EBBC", Nid = 0xA626EBBC, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_6FD54616", Nid = 0x6FD54616, Library = "SceMarlinHci", Default = true)]
    private int MarlinHandleState(uint handle) => MarlinAllowed ? 0 : MarlinErrNotAllowed;

    /// <summary>NID_9EA124CA (SceUID handle): releases a key object; a handle not in the table answers the engine's 3
    /// (this entry returns the engine's code as it is).</summary>
    [Hle("NID_9EA124CA", Nid = 0x9EA124CA, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_F955CEA4", Nid = 0xF955CEA4, Library = "SceMarlinHci", Default = true)]
    private int MarlinRelease(uint handle) => MarlinAllowed ? MarlinEngineInvalid : MarlinErrNotAllowed;

    /// <summary>NID_C889F85B (SceUID handle, b, void *output): copies the object's data out and frees the handle; a
    /// handle not in the table answers the engine's 3 without a copy.</summary>
    [Hle("NID_C889F85B", Nid = 0xC889F85B, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_C6901BC8", Nid = 0xC6901BC8, Library = "SceMarlinHci", Default = true)]
    private int MarlinTakeData(uint handle, uint second, uint output) => MarlinAllowed ? MarlinEngineInvalid : MarlinErrNotAllowed;

    /// <summary>NID_CC2BA3B5 (SceUID *handles, u32 *count): the two key objects of the console's keys; no key file,
    /// so count 0 is written, then 0 handles (a copy of no bytes).</summary>
    [Hle("NID_CC2BA3B5", Nid = 0xCC2BA3B5, Library = "SceMarlinHci", Default = true)]
    private int MarlinKeyHandles(uint handles, uint count) => MarlinAllowed ? MarlinWriteWord(count, 0) : MarlinErrNotAllowed;

    /// <summary>NID_910566BD (void *handles, u32 *count, const u32 *size): the second set's NID_CC2BA3B5: the 8-byte
    /// size word is copied first, a handle buffer of more than 0x40 bytes is 0x8002710B; then NID_CC2BA3B5 on its own
    /// buffers and the results copied out.</summary>
    [Hle("NID_910566BD", Nid = 0x910566BD, Library = "SceMarlinHci", Default = true)]
    private int MarlinKeyHandlesCopy(uint handles, uint count, uint sizeWord)
    {
        if (!KernelSpace.IsUserRange(sizeWord, 8))
        {
            return KernelSpace.UserCopyFault;
        }

        uint size = GuestMemory.R32(sizeWord);
        if (handles != 0 && size > 0x40)
        {
            return MarlinErrTooLarge;
        }

        // NID_CC2BA3B5 on the entry's own buffers (no count buffer: its count copy fails)
        int result = MarlinErrNotAllowed;
        if (MarlinAllowed)
        {
            result = count != 0 ? 0 : KernelSpace.UserCopyFault;
        }

        // the zeroed handle buffer and the count are copied out whatever NID_CC2BA3B5 answered
        if (handles != 0)
        {
            if (!KernelSpace.IsUserRange(handles, size))
            {
                return KernelSpace.UserCopyFault;
            }

            GuestMemory.Fill(handles, 0, size);
        }

        if (count != 0)
        {
            int copied = MarlinWriteWord(count, 0);
            if (copied < 0)
            {
                return copied;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ engine calls with parameter blocks

    /// <summary>NID_24D94277 (block of 0x20: handle, b, two (size, pointer) pairs at +8 and +0x10, g, output +0x1C).</summary>
    [Hle("NID_24D94277", Nid = 0x24D94277, Library = "SceMarlinHci", Default = true)]
    private int Marlin24D94277(uint block) => MarlinBlockCall(block, 0x20, [0x08, 0x10], 0x1C);

    /// <summary>NID_52985887 (block of 0x20): the second set's NID_24D94277 (a NULL block is passed on and fails its copy).</summary>
    [Hle("NID_52985887", Nid = 0x52985887, Library = "SceMarlinHci", Default = true)]
    private int Marlin52985887(uint block) => Marlin24D94277ThroughEntry(block);

    /// <summary>NID_00F544DF (block of 0x2C: a, b, four (size, pointer) pairs at +8, +0x10, +0x18, +0x20, output +0x28):
    /// a license is processed with the console's keys.</summary>
    [Hle("NID_00F544DF", Nid = 0x00F544DF, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_77693C89", Nid = 0x77693C89, Library = "SceMarlinHci", Default = true)]
    private int Marlin00F544DF(uint block) => MarlinBlockCall(block, 0x2C, [0x08, 0x10, 0x18, 0x20], 0x28);

    /// <summary>NID_AE0FBC3F (block of 0x24: handle, b, c, an optional pointer to 8 bytes at +0xC, two pairs at +0x10
    /// and +0x18, output +0x20).</summary>
    [Hle("NID_AE0FBC3F", Nid = 0xAE0FBC3F, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_2B5434C9", Nid = 0x2B5434C9, Library = "SceMarlinHci", Default = true)]
    private int MarlinAE0FBC3F(uint block) => MarlinBlockCall(block, 0x24, [0x10, 0x18], 0x20, eightByteOffset: 0x0C);

    /// <summary>NID_CB99E399 and NID_44390C58 (block of 0x1C: handle, b, two pairs at +8 and +0x10, g, output +0x18).</summary>
    [Hle("NID_CB99E399", Nid = 0xCB99E399, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_EEE9D87E", Nid = 0xEEE9D87E, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_44390C58", Nid = 0x44390C58, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_30135081", Nid = 0x30135081, Library = "SceMarlinHci", Default = true)]
    private int MarlinTwoPairBlock(uint block) => MarlinBlockCall(block, 0x1C, [0x08, 0x10], 0x18);

    /// <summary>NID_B7FB96EC (SceUID handle, b, const pair *data, SceUID *output): the optional pair is copied, the
    /// handle is not found, 0 is written.</summary>
    [Hle("NID_B7FB96EC", Nid = 0xB7FB96EC, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_3B79F57F", Nid = 0x3B79F57F, Library = "SceMarlinHci", Default = true)]
    private int MarlinB7FB96EC(uint handle, uint second, uint pair, uint output)
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        if (!MarlinPairArgumentReadable(pair))
        {
            return KernelSpace.UserCopyFault;
        }

        return MarlinWriteWord(output, 0);
    }

    /// <summary>NID_F7303D28 (SceUID handle, const pair *first, const pair *second, SceUID *output): both optional
    /// pairs are copied, the handle is not found, 0 is written.</summary>
    [Hle("NID_F7303D28", Nid = 0xF7303D28, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_F4FD13ED", Nid = 0xF4FD13ED, Library = "SceMarlinHci", Default = true)]
    private int MarlinF7303D28(uint handle, uint first, uint second, uint output)
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        if (!MarlinPairArgumentReadable(first) || !MarlinPairArgumentReadable(second))
        {
            return KernelSpace.UserCopyFault;
        }

        return MarlinWriteWord(output, 0);
    }

    /// <summary>NID_EAD075E3 (SceUID handle, const pair *data, SceUID *output): the pair is required; data of a size
    /// below 0x10000 is always copied (also a NULL pointer, which fails unless the size is 0); then 0 is written.</summary>
    [Hle("NID_EAD075E3", Nid = 0xEAD075E3, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_5C9C7045", Nid = 0x5C9C7045, Library = "SceMarlinHci", Default = true)]
    private int MarlinEAD075E3(uint handle, uint pair, uint output)
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        if (!KernelSpace.IsUserRange(pair, 8))
        {
            return KernelSpace.UserCopyFault;
        }

        uint size = GuestMemory.R32(pair);
        if (size < 0x10000 && !KernelSpace.IsUserRange(GuestMemory.R32(pair + 4), size))
        {
            return KernelSpace.UserCopyFault;
        }

        return MarlinWriteWord(output, 0);
    }

    // ------------------------------------------------------------------ the key files

    /// <summary>
    /// NID_BFBA3A79 (int kind, u32 size, const void *data): writes the console's keys of kind 0 or 1. Data of a size
    /// 1..0xFFFF is copied first; a size above 0x10000 or another kind is 3. Without data the module makes the key
    /// folder and removes the key file (a missing file is 0, another removal failure 0x11060001). With data it encrypts
    /// them with the console's device key and writes the file: VitaSharp has no device key, so it answers as the
    /// module does when the file cannot be written (0x11060001).
    /// </summary>
    [Hle("NID_BFBA3A79", Nid = 0xBFBA3A79, Library = "SceMarlinHci", Default = true)]
    [Hle("NID_DBF7A9E2", Nid = 0xDBF7A9E2, Library = "SceMarlinHci", Default = true)]
    private int MarlinWriteKeys(int kind, uint size, uint data)
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        bool copied = data != 0 && size != 0 && size < 0x10000;
        if (copied && !KernelSpace.IsUserRange(data, size))
        {
            return KernelSpace.UserCopyFault;
        }

        if (size > 0x10000 || kind is not (0 or 1))
        {
            return MarlinEngineInvalid;
        }

        if (size != 0)
        {
            return MarlinEngineFileError;
        }

        return MarlinRemoveKeyFile(kind);
    }

    private int MarlinRemoveKeyFile(int kind)
    {
        if (Emu.Vfs.Resolve(MarlinKeyFolder, forWrite: true) is not { } folder)
        {
            return MarlinEngineFileError;
        }

        try
        {
            Directory.CreateDirectory(folder);
            string? file = Emu.Vfs.Resolve(MarlinKeyFile(kind), forWrite: true);
            if (file != null && File.Exists(file))
            {
                File.Delete(file);
            }

            return 0;
        }
        catch (IOException)
        {
            return MarlinEngineFileError;
        }
        catch (UnauthorizedAccessException)
        {
            return MarlinEngineFileError;
        }
    }

    /// <summary>NID_E8A352C2 (int kind): the module's check of a key kind; 0 for kinds 0 and 1, else 3.</summary>
    [Hle("NID_E8A352C2", Nid = 0xE8A352C2, Library = "SceMarlinHci")]
    [Hle("NID_CFB18A73", Nid = 0xCFB18A73, Library = "SceMarlinHci")]
    private int MarlinCheckKind(int kind)
    {
        if (!MarlinAllowed)
        {
            return MarlinErrNotAllowed;
        }

        return kind is 0 or 1 ? 0 : MarlinEngineInvalid;
    }
}
