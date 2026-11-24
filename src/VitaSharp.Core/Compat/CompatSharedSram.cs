namespace VitaSharp.Core.Compat;

/// <summary>
/// SceCompatSharedSram (FW 3.74 SceCompat: ARM physical 0xE8100000, 0x1000 bytes), the block both sides of the
/// compat core read and write: the PSP sees it at 0xBFC00000. The offsets SceCompat uses (docs/notes/psp-compat.md):
/// the shared registers (0x100..0x188), the controller words (0x120..0x128, 0x140), the command entries the core
/// posts (0x800 + slot * 8), the answers (0x840 + slot * 0x20) and the virtual interrupt slots (0x8C0 + n * 8).
/// Both sides touch it from their own thread, so every word is read and written atomically (byte and halfword stores
/// merge into their word with a compare-exchange, as one bus write).
/// </summary>
public sealed class CompatSharedSram
{
    /// <summary>The block's size in bytes.</summary>
    public const int Size = 0x1000;

    /// <summary>The block in the PSP's physical address space (0xBFC00000 without the segment bits).</summary>
    public const uint PspPhysicalBase = 0x1FC00000;

    /// <summary>The ARM physical address SceCompat maps it from.</summary>
    public const uint ArmPhysicalBase = 0xE8100000;

    /// <summary>Shared register 0x80 (sceCompatWriteShared32) / 0 (sceCompatReadShared32).</summary>
    public const int Shared32Base = 0x100;

    /// <summary>sceCompatWriteSharedCtrl's three words, and the copy of the first written last (the "new data" mark).</summary>
    public const int ControllerWords = 0x120;

    public const int ControllerMark = 0x140;

    /// <summary>The command entries the core posts: one per slot (IRQ 0x44 + slot), word 0 bits 16.. the channel,
    /// word 1 the request's address.</summary>
    public const int CommandEntries = 0x800;

    /// <summary>The answers sceCompatReturnValueEx writes: one 0x20-byte block per slot.</summary>
    public const int AnswerBlocks = 0x840;

    public const int AnswerBlockSize = 0x20;

    /// <summary>The virtual interrupt slots sceCompatInterrupt sets to 1 (one 8-byte slot per line 1..15).</summary>
    public const int VirtualInterrupts = 0x8C0;

    private readonly uint[] _words = new uint[Size / 4];

    /// <summary>The word at <paramref name="offset"/> (the low 2 bits are ignored; past the end reads 0).</summary>
    public uint Read32(int offset)
    {
        int index = (offset & (Size - 1)) >> 2;
        return Volatile.Read(ref _words[index]);
    }

    public void Write32(int offset, uint value)
    {
        int index = (offset & (Size - 1)) >> 2;
        Volatile.Write(ref _words[index], value);
    }

    public byte Read8(int offset)
    {
        return (byte)(Read32(offset) >> ((offset & 3) * 8));
    }

    public ushort Read16(int offset)
    {
        return (ushort)(Read32(offset) >> ((offset & 2) * 8));
    }

    public void Write8(int offset, byte value)
    {
        Merge(offset, 0xFFu << ((offset & 3) * 8), (uint)value << ((offset & 3) * 8));
    }

    public void Write16(int offset, ushort value)
    {
        Merge(offset, 0xFFFFu << ((offset & 2) * 8), (uint)value << ((offset & 2) * 8));
    }

    /// <summary>Clears the block (a new boot of the core).</summary>
    public void Clear()
    {
        for (int i = 0; i < _words.Length; i++)
        {
            Volatile.Write(ref _words[i], 0);
        }
    }

    private void Merge(int offset, uint mask, uint bits)
    {
        int index = (offset & (Size - 1)) >> 2;
        uint current = Volatile.Read(ref _words[index]);
        while (true)
        {
            uint merged = (current & ~mask) | bits;
            uint seen = Interlocked.CompareExchange(ref _words[index], merged, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
