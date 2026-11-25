using System.Security.Cryptography;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceCoredumpForDriver (os0:bootimage SceCoredump 3.74, read in the module, study only): the core dump module's kernel
// side. The CAF calls package a crash report (an archive whose header and segments are signed with HMAC and the
// segments AES-encrypted): the same contexts the VSH bridge's calls use (the bridge copies the arguments and calls
// these), with the sizes as arguments instead of the bridge's option blocks. The crash report is
// ux0:data/crash_report.caf. The dump writer and its helpers (the process dump of a crash, the application manager's
// dump requests, the crash dump module's ELF writer) need a crashed process's state: the emulator records crashes
// in its own log and writes no core file, so a requested dump is refused as the module refuses it on a retail unit
// without the QA flag to keep core files (0x80020008).
public sealed partial class VshBridge
{
    /// <summary>SceCoredump: an IV buffer that is not 16 bytes (0x80010016).</summary>
    const int COREDUMP_ERR_INVALID = unchecked((int)0x80010016);

    /// <summary>SceCoredump: a NULL request (0x80010069).</summary>
    const int COREDUMP_ERR_NULL_REQUEST = unchecked((int)0x80010069);

    /// <summary>The crash report the module writes and deletes.</summary>
    const string CrashReportPath = "ux0:data/crash_report.caf";

    /// <summary>The dump session the application manager opened (a positive id) or -1.</summary>
    [SnapshotMissing(-1)]
    int _coredumpSession = -1;

    [SnapshotMissing]
    int _nextCoredumpSession;

    /// <summary>Removes the crash report as the module does (ksceIoRemove's result).</summary>
    int DeleteCrashReportCaf()
    {
        if (Emu.Hle.Modules.OfType<IoFileMgr>().FirstOrDefault() is not { } io)
        {
            return SceError.IO_ENOENT;
        }

        return io.sceIoRemove(CrashReportPath);
    }

    /// <summary>ksceCoredumpCafInit(): starts the archive service: 0.</summary>
    [Hle(Nid = 0x9336009B, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafInit()
    {
        return 0;
    }

    /// <summary>ksceCoredumpCafFinal(): ends it: 0.</summary>
    [Hle(Nid = 0xC90F61AF, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafFinal()
    {
        return 0;
    }

    /// <summary>ksceCoredumpCafContextCreate(): a new context id (the bridge's table).</summary>
    [Hle(Nid = 0x2964AD0A, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafContextCreate()
    {
        return vshCoredumpCafContextCreateThroughEntry();
    }

    /// <summary>ksceCoredumpCafContextDestroy(ctx): unknown 0x800200CB.</summary>
    [Hle(Nid = 0x95402BF3, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafContextDestroy(int ctx)
    {
        return vshCoredumpCafContextDestroyThroughEntry(ctx);
    }

    /// <summary>ksceCoredumpCafHeaderInit(ctx, hmacKeyId).</summary>
    [Hle(Nid = 0x7C8120C5, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafHeaderInit(int ctx, uint keyId)
    {
        return vshCoredumpCafHeaderInitThroughEntry(ctx, keyId);
    }

    /// <summary>ksceCoredumpCafHeaderTransform(ctx, data, size).</summary>
    [Hle(Nid = 0xAE2C2793, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafHeaderTransform(int ctx, uint data, uint size)
    {
        return CafHeaderTransform(ctx, data, size);
    }

    /// <summary>ksceCoredumpCafHeaderFinal(ctx, digest, size).</summary>
    [Hle(Nid = 0x65AA4991, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafHeaderFinal(int ctx, uint digest, uint size)
    {
        return CafDigest(ctx, digest, Math.Min(size, 0x20u), header: true);
    }

    /// <summary>ksceCoredumpCafSegmentInit(ctx, hmacKeyId, mode, aesKeyId, iv, ivSize).</summary>
    [Hle(Nid = 0x07185515, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafSegmentInit(int ctx, uint hmacKeyId, int mode, uint aesKeyId, uint iv, uint ivSize)
    {
        return CafSegmentInit(ctx, hmacKeyId, mode, aesKeyId, iv, Math.Min(ivSize, 0x10u));
    }

    /// <summary>ksceCoredumpCafSegmentTransform(ctx, src, dst, size).</summary>
    [Hle(Nid = 0xFB7AEBFE, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafSegmentTransform(int ctx, uint src, uint dst, uint size)
    {
        return CafSegmentTransform(ctx, src, dst, size);
    }

    /// <summary>ksceCoredumpCafSegmentFinal(ctx, digest, size).</summary>
    [Hle(Nid = 0xDF17420A, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafSegmentFinal(int ctx, uint digest, uint size)
    {
        return CafDigest(ctx, digest, Math.Min(size, 0x20u), header: false);
    }

    /// <summary>ksceCoredumpCafCreateIv(iv, size): 16 random bytes from the security service's generator; a NULL
    /// buffer or another size 0x80010016.</summary>
    [Hle(Nid = 0xE1BCBE8F, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCafCreateIv(uint iv, uint size)
    {
        if (iv == 0 || size != 0x10)
        {
            return COREDUMP_ERR_INVALID;
        }

        RandomNumberGenerator.Fill(GuestMemory.Span(iv, 0x10));
        return 0;
    }

    /// <summary>ksceCoredumpDeleteCrashReportCaf(): removes ux0:data/crash_report.caf (its removal's result).</summary>
    [Hle(Nid = 0xAD070837, Library = "SceCoredumpForDriver")]
    int ksceCoredumpDeleteCrashReportCaf()
    {
        return DeleteCrashReportCaf();
    }

    /// <summary>ksceCoredumpCreateDump(pid, name, nameSize, path, pathSize, ...): the dump of a process on request:
    /// no core file is kept (0x80020008).</summary>
    [Hle(Nid = 0x0C10313F, Library = "SceCoredumpForDriver")]
    int ksceCoredumpCreateDump(int pid, uint name, uint nameSize, uint path, uint pathSize)
    {
        return SceError.ILLEGAL_PERMISSION;
    }

    /// <summary>NID_097AA37D(): whether the module is ready to dump: 1.</summary>
    [Hle(Nid = 0x097AA37D, Library = "SceCoredumpForDriver", ReturnsValue = true)]
    int ksceCoredump_097AA37D()
    {
        return 1;
    }

    /// <summary>NID_340856F7(): the application manager's "dump finished" notice: 0.</summary>
    [Hle(Nid = 0x340856F7, Library = "SceCoredumpForDriver")]
    int ksceCoredump_340856F7()
    {
        return 0;
    }

    /// <summary>
    /// The checks FW 3.74 makes on a dump request (SceCoredump 0x81007100 / 0x810072F4) before it queues it for its
    /// dump thread, in the module's order: a NULL request or one whose size word (+0) is 3 or less is 0x80010016; a
    /// request of 0x34 bytes or more also needs +0x14 below 11 with +0x18 set when it is not 0, and +0x1C at most 0x80
    /// with +0x20 set when it is not 0; one of 0x14 bytes or more a dump level (+8) of 0, 1, 2 or 10, and for 10 a +0xC
    /// of at most 0x400 with +0x10 set when it is not 0; then the third argument must be set. 0 = a valid request.
    /// </summary>
    internal static int CoredumpRequestError(uint target, uint request)
    {
        if (request == 0)
        {
            return COREDUMP_ERR_INVALID;
        }

        uint size = GuestMemory.R32(request);
        if (size <= 3)
        {
            return COREDUMP_ERR_INVALID;
        }

        uint Word(int index) => GuestMemory.R32(request + (uint)index * 4);
        if (size >= 0x34)
        {
            bool names = Word(5) < 0xB && (Word(5) == 0 || Word(6) != 0);
            bool data = Word(7) <= 0x80 && (Word(7) == 0 || Word(8) != 0);
            if (!names || !data)
            {
                return COREDUMP_ERR_INVALID;
            }
        }

        if (size > 0x13)
        {
            uint level = Word(2);
            if (level is not (0 or 1 or 2 or 10))
            {
                return COREDUMP_ERR_INVALID;
            }

            if (level == 10 && (Word(3) > 0x400 || (Word(3) != 0 && Word(4) == 0)))
            {
                return COREDUMP_ERR_INVALID;
            }
        }

        return target == 0 ? COREDUMP_ERR_INVALID : 0;
    }

    /// <summary>NID_A7D214A7(pid, arg, target, request): the application manager's dump of a process. The request is
    /// checked as FW 3.74 checks it (<see cref="CoredumpRequestError"/>); a valid one goes to the dump queue
    /// (VshBridge.CoredumpQueue.cs), or is refused as on a retail unit (0x80020008) when no core folder is set.</summary>
    [Hle(Nid = 0xA7D214A7, Library = "SceCoredumpForDriver", Default = true)]
    int ksceCoredump_A7D214A7(int pid, uint arg, uint target, uint request)
    {
        int error = CoredumpRequestError(target, request);
        return error != 0 ? error : QueueCoredump(pid);
    }

    /// <summary>NID_D064F6DC(pid, arg, target, request): the same for a second dump kind (the same checks and
    /// queue).</summary>
    [Hle(Nid = 0xD064F6DC, Library = "SceCoredumpForDriver", Default = true)]
    int ksceCoredump_D064F6DC(int pid, uint arg, uint target, uint request)
    {
        int error = CoredumpRequestError(target, request);
        return error != 0 ? error : QueueCoredump(pid);
    }

    /// <summary>NID_13EF8516(handler): the crash dump module opens its dump session (the system's "NPXS19998"
    /// writer): a new positive session id.</summary>
    [Hle(Nid = 0x13EF8516, Library = "SceCoredumpForDriver")]
    int ksceCoredump_13EF8516(uint handler)
    {
        _coredumpSession = ++_nextCoredumpSession;
        return _coredumpSession;
    }

    /// <summary>NID_12392973(session, data, size, ?): writes into the open session; another id is ignored (0).</summary>
    [Hle(Nid = 0x12392973, Library = "SceCoredumpForDriver")]
    int ksceCoredump_12392973(int session, uint data, uint size, uint flags)
    {
        return 0;
    }

    /// <summary>NID_10863B61(session): closes the session it names.</summary>
    [Hle(Nid = 0x10863B61, Library = "SceCoredumpForDriver")]
    int ksceCoredump_10863B61(int session)
    {
        if (session == _coredumpSession)
        {
            _coredumpSession = -1;
        }

        return 0;
    }

    /// <summary>NID_EF20949F(request): a dump request of the crash dump module: NULL 0x80010069; no core file is kept
    /// (0x80020008).</summary>
    [Hle(Nid = 0xEF20949F, Library = "SceCoredumpForDriver", Default = true)]
    int ksceCoredump_EF20949F(uint request)
    {
        return request == 0 ? COREDUMP_ERR_NULL_REQUEST : SceError.ILLEGAL_PERMISSION;
    }
}
