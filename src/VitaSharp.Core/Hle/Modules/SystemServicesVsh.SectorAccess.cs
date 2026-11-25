using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Raw sector access to the memory card (SceMsif) and the internal eMMC (SceSdif host 0): every access is logged with
// its caller (the first of each caller and operation, or all with VITASHARP_TRACE_SECTORS=1), to know who reads the
// devices below the file level (formatting, the system update, homebrew storage drivers). With an image file backing
// the eMMC (VITASHARP_EMMC_IMAGE, HostServices.EmmcImage) its sector commands read and write the image; a range past
// the device's end is the card's ADDRESS_OUT_OF_RANGE (R1 bit 31), which FW 3.74 sdif turns into 0x80320100 plus the
// lowest R1 error bit (FUN_810026e0: 0x8032011F).
public sealed partial class SystemServicesVsh
{
    /// <summary>sdif's code for a command the card answered with ADDRESS_OUT_OF_RANGE (R1 bit 31).</summary>
    internal const int SDIF_ERR_OUT_OF_RANGE = unchecked((int)0x8032011F);

    private static readonly bool s_traceSectors = Environment.GetEnvironmentVariable(EnvVars.TraceSectors) == "1";

    /// <summary>Raw sector accesses made so far (tests, the session's report; not part of a save state).</summary>
    [SnapshotIgnore]
    internal int SectorAccesses;

    /// <summary>The code that made the current call (the HLE stub's return address), symbolized.</summary>
    private unsafe string SectorCaller()
    {
        if (GuestThread.Current is not { } thread)
        {
            return "the host";
        }

        uint returnAddress = thread.Core.State->R[14] & ~1u;
        return $"{K.Symbolize(returnAddress)} ({thread.Name})";
    }

    /// <summary>Logs one raw sector access (operation, first sector, count, result) with its caller.</summary>
    private void LogSectorAccess(string operation, uint sector, uint count, int result)
    {
        Interlocked.Increment(ref SectorAccesses);
        string caller = SectorCaller();
        string line = $"[sectors] {operation} sector 0x{sector:X} x{count} from {caller}: 0x{result:X8}";
        if (s_traceSectors)
        {
            Log.Info(line);
            return;
        }

        int plus = caller.IndexOf('+');
        Log.InfoOnce($"sectors-{operation}-{(plus > 0 ? caller[..plus] : caller)}", line + " (first of this caller; VITASHARP_TRACE_SECTORS=1 logs all)");
    }

    /// <summary>The eMMC's sector command on its image: the device context must be the eMMC's (host 0) and an image
    /// back it, else null (the command reaches no device). A range past the image's end is out of range.</summary>
    private int? EmmcImageTransfer(uint context, uint sector, uint buffer, uint count, bool write)
    {
        if (Emu?.Host.EmmcImage is not { } image || context != SdifDevice(0))
        {
            return null;
        }

        if (!image.Contains(sector, count))
        {
            return SDIF_ERR_OUT_OF_RANGE;
        }

        const uint ChunkSectors = 128;
        for (uint done = 0; done < count; done += ChunkSectors)
        {
            uint sectors = Math.Min(ChunkSectors, count - done);
            int bytes = (int)(sectors * BlockImage.SectorSize);
            uint address = buffer + done * BlockImage.SectorSize;
            if (write)
            {
                image.Write(sector + done, GuestMemory.ReadSpan(address, bytes));
            }
            else
            {
                image.Read(sector + done, GuestMemory.Span(address, bytes));
            }
        }

        return 0;
    }
}
