using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// System-imported functions without public names of SceJpegEncArm, SceAppUtil and ScePhotoExport (FW 3.74
// libscejpegencarm, apputil and libScePhotoExport read in their code, study only).

public sealed partial class JpegEnc
{
    internal const int ARM_ERR_STREAM_STATE = unchecked((int)0x80650307);
    internal const int ARM_STREAM_CONTINUE = 1, ARM_STREAM_ONE_MCU = 2;

    /// <summary>libscejpegencarm's header room: the first call of a stream needs more than 0x261 bytes of output.</summary>
    const int ArmHeaderRoom = 0x261;

    sealed partial class Ctx
    {
        /// <summary>The context's output words: the write cursor (0 after a call with its own buffer) and the limit
        /// (the buffer's end less 4 bytes; 0x262 without a buffer).</summary>
        [SnapshotMissing]
        public uint Cursor, Limit;

        /// <summary>The stream of NID_4EE1BAED: the MCU row and column next encoded, the bytes before this call (or the
        /// buffer's start when the context's buffer is used), the bits not yet written and the DC predictors.</summary>
        [SnapshotMissing]
        public int StreamRow, StreamColumn, StreamBitCount, StreamPredY, StreamPredCb, StreamPredCr, StreamRatio;

        [SnapshotMissing]
        public uint StreamBase, StreamBits;

        /// <summary>The bytes the chroma rows have past half the width (the CSC formats round the chroma pitch).</summary>
        [SnapshotMissing]
        public int ChromaPad;
    }

    static void SetCursor(Ctx context, uint outBuffer, uint outSize)
    {
        context.Cursor = outBuffer;
        context.Limit = outBuffer == 0 ? ArmHeaderRoom + 1 : outBuffer + outSize - 4;
    }

    /// <summary>
    /// NID_4EE1BAED (SceJpegEncArm)(ctx, const uint8 *y, const uint8 *cb, const uint8 *cr, uint flags, void *out,
    /// uint outSize, uint32 *written): the planar encoder in parts (the camera app's photos). The context and the
    /// planes are checked first (NULL, the context 4-aligned, each plane 8-aligned: 0x80650305), then the flags 0..3
    /// (0x80650307). With <paramref name="outBuffer"/> each call writes from its start (<paramref name="written"/>
    /// and an <paramref name="outSize"/> of at least 4 required, 0x80650305) and reports its bytes; without, the parts
    /// follow each other in the context's buffer. Bit 0 clear starts the stream: the header (more than 0x261 bytes of
    /// room, else 0x80650301) and the stream is reset; 0. Bit 0 set encodes the next part: one MCU row of the planes
    /// (luma pitch the context width, chroma half of it plus the CSC pad) or with bit 1 one MCU (pitches 16 and 8);
    /// 0x80650307 once every row is done. The call that encodes the last row ends the image (EOI) and returns its
    /// size; the others 0. A part that reaches the end of the room is 0x80650301.
    /// </summary>
    [Hle("NID_4EE1BAED", Nid = 0x4EE1BAED, Library = "SceJpegEncArm")]
    int JpegArmEncoderEncodePart(uint context, uint lumaPlane, uint cbPlane, uint crPlane, uint flags, uint outBuffer, uint outSize, uint written)
    {
        if (context == 0 || (context & 3) != 0 || !PlaneValid(lumaPlane) || !PlaneValid(cbPlane) || !PlaneValid(crPlane))
        {
            return ARM_ERR_INVALID_POINTER;
        }

        if (flags > 3)
        {
            return ARM_ERR_STREAM_STATE;
        }

        lock (_lock)
        {
            if (!_armCtx.TryGetValue(context, out Ctx? c))
            {
                return ARM_ERR_INVALID_POINTER;
            }

            uint start;
            if (outBuffer == 0)
            {
                if (c.Cursor == 0)
                {
                    return ARM_ERR_INVALID_POINTER;
                }

                start = c.Cursor;
            }
            else
            {
                if (written == 0 || outSize < 4)
                {
                    return ARM_ERR_INVALID_POINTER;
                }

                SetCursor(c, outBuffer, outSize);
                start = outBuffer;
            }

            bool begin = (flags & ARM_STREAM_CONTINUE) == 0;
            if (begin && (int)(c.Limit - start) <= ArmHeaderRoom)
            {
                return ARM_ERR_INSUFFICIENT_BUFFER;
            }

            int vdiv = (c.PixelFormat & 9) == 9 ? 1 : 2;
            int mcuHeight = 8 * vdiv;
            int rows = (c.ValidHeight + mcuHeight - 1) / mcuHeight;
            int columns = (c.ValidWidth + 15) >> 4;
            JpegEncoder.Stream stream;
            if (begin)
            {
                c.StreamRatio = c.Ratio;
                stream = NewStream(c, vdiv);
                stream.WriteHeader(c.HeaderMode == HEADER_JPEG);
                c.StreamBase = outBuffer != 0 ? 0 : start;
                c.StreamRow = 0;
                c.StreamColumn = 0;
                SaveStream(c, stream);
                if (!Emit(c, stream))
                {
                    return ARM_ERR_INSUFFICIENT_BUFFER;
                }
            }
            else
            {
                if (rows <= c.StreamRow)
                {
                    return ARM_ERR_STREAM_STATE;
                }

                stream = NewStream(c, vdiv);
                stream.Resume(c.StreamBits, c.StreamBitCount, (c.StreamPredY, c.StreamPredCb, c.StreamPredCr));
                int lumaRows = Math.Min(mcuHeight, c.ValidHeight - c.StreamRow * mcuHeight);
                int chromaRows = (lumaRows + vdiv - 1) / vdiv;
                if ((flags & ARM_STREAM_ONE_MCU) != 0)
                {
                    int lumaColumns = Math.Min(16, c.ValidWidth - c.StreamColumn * 16);
                    EncodePart(stream, lumaPlane, cbPlane, crPlane, 16, 8, 1, lumaColumns, lumaRows, chromaRows);
                    c.StreamColumn++;
                    if (columns <= c.StreamColumn)
                    {
                        c.StreamColumn = 0;
                        c.StreamRow++;
                    }
                }
                else
                {
                    EncodePart(stream, lumaPlane, cbPlane, crPlane, c.Width, c.Width / 2 + c.ChromaPad, columns, c.ValidWidth, lumaRows, chromaRows);
                    c.StreamRow++;
                }

                SaveStream(c, stream);
                if (!Emit(c, stream))
                {
                    return ARM_ERR_INSUFFICIENT_BUFFER;
                }
            }

            if (c.StreamRow < rows)
            {
                ReportPart(c, outBuffer, written);
                return 0;
            }

            stream.Finish();
            if (c.Cursor + (uint)stream.Pending > c.Limit + 8)
            {
                return ARM_ERR_INSUFFICIENT_BUFFER;
            }

            int tail = stream.Take(GuestMemory.Span(c.Cursor, stream.Pending));
            c.Cursor += (uint)tail;
            if (outBuffer != 0)
            {
                uint part = c.Cursor - outBuffer;
                GuestMemory.W32(written, part);
                c.Cursor = 0;
                return (int)(c.StreamBase + part);
            }

            return (int)(c.Cursor - c.StreamBase);
        }
    }

    static bool PlaneValid(uint plane) => plane != 0 && (plane & 7) == 0;

    static JpegEncoder.Stream NewStream(Ctx c, int vdiv)
    {
        (byte[] luma, byte[] chroma) = JpegEncoder.TablesForRatio(c.StreamRatio);
        return new JpegEncoder.Stream(c.ValidWidth, c.ValidHeight, 2, vdiv, luma, chroma);
    }

    static void SaveStream(Ctx c, JpegEncoder.Stream stream)
    {
        c.StreamBits = stream.Bits;
        c.StreamBitCount = stream.BitCount;
        (c.StreamPredY, c.StreamPredCb, c.StreamPredCr) = stream.Predictors;
    }

    /// <summary>The part's bytes at the cursor; false when they reach the end of the room (libscejpegencarm checks
    /// the cursor against the limit after each part).</summary>
    static bool Emit(Ctx c, JpegEncoder.Stream stream)
    {
        int count = stream.Pending;
        if (c.Cursor + (uint)count >= c.Limit)
        {
            return false;
        }

        stream.Take(GuestMemory.Span(c.Cursor, count));
        c.Cursor += (uint)count;
        return true;
    }

    static void ReportPart(Ctx c, uint outBuffer, uint written)
    {
        if (outBuffer == 0)
        {
            return;
        }

        uint part = c.Cursor - outBuffer;
        GuestMemory.W32(written, part);
        c.StreamBase += part;
        c.Cursor = 0;
    }

    /// <summary>MCUs side by side from a strip whose top-left is the first one's.</summary>
    static void EncodePart(JpegEncoder.Stream stream, uint lumaPlane, uint cbPlane, uint crPlane, int lumaPitch, int chromaPitch,
        int count, int lumaWidth, int lumaRows, int chromaRows)
    {
        int chromaWidth = (lumaWidth + 1) / 2;
        ReadOnlySpan<byte> luma = GuestMemory.Span(lumaPlane, lumaRows * lumaPitch);
        ReadOnlySpan<byte> cb = GuestMemory.Span(cbPlane, chromaRows * chromaPitch);
        ReadOnlySpan<byte> cr = GuestMemory.Span(crPlane, chromaRows * chromaPitch);
        for (int i = 0; i < count; i++)
        {
            stream.EncodeMcu(luma, lumaPitch, cb, cr, chromaPitch, i * 16, 0, lumaWidth, lumaRows, i * 8, 0, chromaWidth, chromaRows);
        }
    }
}

public sealed partial class AppUtil
{
    /// <summary>appmgr: the PSP save data root is already mounted (0x80800003).</summary>
    const int APPMGR_ERR_ALREADY_MOUNTED = unchecked((int)0x80800003);

    /// <summary>apputil's key of the system save-data root mount (libcdlg_savedata passes it).</summary>
    const int SaveDataRootKey = 0xCA;

    /// <summary>The bytes one save may write (a PlayStation Now client: 10 MiB).</summary>
    const uint SaveWriteLimit = 0x100000;

    /// <summary>How many system mounts of the PSP save root are open, and the mount point the first one got.</summary>
    [SnapshotMissing]
    int _pspSaveRootMounts;

    [SnapshotMissing]
    string _pspSaveRootMountPoint = "";

    /// <summary>
    /// NID_E8CADCA9 (SceAppUtil)(slot, files, fileNum, mountPoint, SceSize *requiredSizeKiB): the checks of
    /// sceAppUtilSaveDataDataSave without the write (apputil_ext's PS Now save runs them, then sends the list to
    /// SceAppMgr itself). No initialisation needed. 0x80100600 for a slot id of 256 or more, a mount point other than
    /// savedata0:, a bad file list (NULL, 0 or more than 5 files, a file breaking the save rules, more than 1 MiB to
    /// write: each file's size plus the gap past the existing file), a NULL requiredSizeKiB, or a slot whose new param
    /// breaks the param rules (<see cref="SlotParamValid"/>); else 0.
    /// </summary>
    [Hle("NID_E8CADCA9", Nid = 0xE8CADCA9, Library = "SceAppUtil")]
    int AppUtilSaveDataDataSaveCheck(uint slot, uint files, uint fileNum, uint mountPoint, uint requiredKiB)
    {
        if (!SlotArgumentValid(slot) || !SaveMountPointValid(mountPoint, writable: true))
        {
            return ERR_PARAMETER;
        }

        if (files == 0 || fileNum == 0 || fileNum > MaxRemoveItems)
        {
            return ERR_PARAMETER;
        }

        ulong toWrite = 0;
        for (uint i = 0; i < fileNum; i++)
        {
            uint file = files + i * SaveFileSize;
            if (!SaveFileValid(file))
            {
                return ERR_PARAMETER;
            }

            toWrite += BytesToWrite(file);
            if (toWrite > SaveWriteLimit)
            {
                return ERR_PARAMETER;
            }
        }

        if (requiredKiB == 0)
        {
            return ERR_PARAMETER;
        }

        if (slot != 0 && GuestMemory.R32(slot + 4) != 0 && !SlotParamValid(GuestMemory.R32(slot + 4)))
        {
            return ERR_PARAMETER;
        }

        return 0;
    }

    /// <summary>
    /// NID_1CFFE892 (SceAppUtil)(slot, const removeItem *items, uint count, mountPoint): the checks of
    /// sceAppUtilSaveDataDataRemove without the removal (apputil_ext's PS Now remove). No initialisation needed.
    /// 0x80100600 for a slot id of 256 or more, a NULL list, 0 or more than 5 items, an item breaking the remove
    /// rules, or a mount point other than savedata0:; else 0.
    /// </summary>
    [Hle("NID_1CFFE892", Nid = 0x1CFFE892, Library = "SceAppUtil")]
    int AppUtilSaveDataDataRemoveCheck(uint slot, uint items, uint count, uint mountPoint)
    {
        if (!SlotArgumentValid(slot) || items == 0 || count == 0 || count > MaxRemoveItems)
        {
            return ERR_PARAMETER;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!RemoveItemValid(items + i * RemoveItemSize, appUtilRules: true))
            {
                return ERR_PARAMETER;
            }
        }

        return SaveMountPointValid(mountPoint, writable: true) ? 0 : ERR_PARAMETER;
    }

    /// <summary>
    /// NID_C3CB128B (SceAppUtil)(int key, char mountPoint[16]): the save-data dialog's mount of the PSP emulator's save
    /// root. Before sceAppUtilInit 0x80100601; a key other than 0xCA or a NULL mount point 0x80100600. The first
    /// call mounts it (sceAppMgrPspSaveDataRootMount) and keeps the mount point; each call counts one more user and
    /// gets the kept mount point; SceAppMgr's "already mounted" is 0x80100603 with the kept mount point.
    /// </summary>
    [Hle("NID_C3CB128B", Nid = 0xC3CB128B, Library = "SceAppUtil")]
    int AppUtilPspSaveDataRootMount(int key, uint mountPoint)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (key != SaveDataRootKey || mountPoint == 0)
        {
            return ERR_PARAMETER;
        }

        int result = _pspSaveRootMounts > 0 ? APPMGR_ERR_ALREADY_MOUNTED : Am.PspSaveDataRootMountThroughEntry(mountPoint);
        if (result == 0)
        {
            _pspSaveRootMountPoint = GuestMemory.ReadCString(mountPoint, 15);
        }

        if (result is not (0 or APPMGR_ERR_ALREADY_MOUNTED))
        {
            return result;
        }

        _pspSaveRootMounts++;
        GuestMemory.Fill(mountPoint, 0, 16);
        GuestMemory.WriteCString(mountPoint, _pspSaveRootMountPoint, 16);
        return result == 0 ? 0 : ERR_BUSY;
    }

    /// <summary>NID_C0418B1C (SceAppUtil)(int key): one user of the PSP save root less; the last one unmounts it
    /// (sceAppMgrUmount: SceAppMgr's "not mounted" is 0x80100604). Before sceAppUtilInit 0x80100601; a key other
    /// than 0xCA 0x80100600.</summary>
    [Hle("NID_C0418B1C", Nid = 0xC0418B1C, Library = "SceAppUtil")]
    int AppUtilPspSaveDataRootUmount(int key)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (key != SaveDataRootKey)
        {
            return ERR_PARAMETER;
        }

        _pspSaveRootMounts--;
        if (_pspSaveRootMounts > 0)
        {
            return 0;
        }

        int result = Am.PspSaveDataRootUmount(_pspSaveRootMountPoint);
        if (result == 0)
        {
            _pspSaveRootMountPoint = "";
        }

        _pspSaveRootMounts = 0;
        return result == 0 ? 0 : ERR_NOT_MOUNTED;
    }

    /// <summary>apputil 3.74 (0x81001484): NULL is savedata0:; otherwise exactly "savedata0:" or, where a read-only
    /// mount is allowed, "savedata1:".</summary>
    static bool SaveMountPointValid(uint mountPoint, bool writable)
    {
        if (mountPoint == 0)
        {
            return true;
        }

        string name = GuestMemory.ReadCString(mountPoint, 16);
        return name == "savedata0:" || (!writable && name == "savedata1:");
    }

    /// <summary>apputil 3.74 (0x810016F8): what one save file adds, its size plus the gap between the existing file's
    /// end and the offset (the offset plus the size for a new file).</summary>
    ulong BytesToWrite(uint file)
    {
        uint size = GuestMemory.R32(file + 8);
        long offset = GuestMemory.Read<long>(file + 0x10);
        string host = SavePath(GuestMemory.ReadCString(GuestMemory.R32(file)));
        if (!File.Exists(host))
        {
            return (ulong)offset + size;
        }

        long existing = new FileInfo(host).Length;
        return existing < offset ? (ulong)(offset - existing) + size : size;
    }

    /// <summary>
    /// apputil 3.74 (0x8100151A), a slot param a save may set: title, subtitle, detail and icon path terminated within
    /// 64, 128, 512 and 64 bytes; for titles built with SDK 1.80 or later no line breaks in the title or the subtitle;
    /// the three texts valid UTF-8; the icon path of letters, digits, '-', '_', '.', ':' and '/' without ".."; the
    /// 48 reserved bytes zero.
    /// </summary>
    internal bool SlotParamValid(uint param)
    {
        uint title = param + 4, subTitle = param + 0x44, detail = param + 0xC4, iconPath = param + 0x2C4;
        if (!Terminated(title, 64) || !Terminated(subTitle, 128) || !Terminated(detail, 512) || !Terminated(iconPath, 64))
        {
            return false;
        }

        if (MainModuleSdkVersion() > 0x017FFFFF && (HasLineBreak(title, 64) || HasLineBreak(subTitle, 128)))
        {
            return false;
        }

        if (!Utf8Valid(title, 64) || !Utf8Valid(subTitle, 128) || !Utf8Valid(detail, 512))
        {
            return false;
        }

        string icon = GuestMemory.ReadCString(iconPath, 64);
        foreach (char ch in icon)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':' or '/'))
            {
                return false;
            }
        }

        if (icon.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return !GuestMemory.Span(param + 0x31C, 48).ContainsAnyExcept((byte)0);
    }

    static bool Terminated(uint text, int capacity) => GuestMemory.Span(text, capacity).IndexOf((byte)0) >= 0;

    static bool HasLineBreak(uint text, int capacity)
    {
        ReadOnlySpan<byte> bytes = GuestMemory.Span(text, capacity);
        int end = bytes.IndexOf((byte)0);
        return bytes[..(end < 0 ? capacity : end)].IndexOfAny((byte)'\n', (byte)'\r') >= 0;
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static bool Utf8Valid(uint text, int capacity)
    {
        ReadOnlySpan<byte> bytes = GuestMemory.Span(text, capacity);
        int end = bytes.IndexOf((byte)0);
        try
        {
            StrictUtf8.GetCharCount(bytes[..(end < 0 ? capacity : end)]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed partial class AppMgr
{
    /// <summary>The counterpart of <see cref="PspSaveDataRootMount"/>: apputil unmounts the kept mount point with
    /// sceAppMgrUmount.</summary>
    internal int PspSaveDataRootUmount(string mountPoint)
    {
        return Umount(mountPoint);
    }
}

public sealed partial class MediaExport
{
    internal const int PhotoErrInvalid = unchecked((int)0x80101A01), PhotoErrNoFile = unchecked((int)0x80101A02),
        PhotoErrNoMemory = unchecked((int)0x80101A04), PhotoErrUnsupported = unchecked((int)0x80101A09),
        PhotoErrCancelled = unchecked((int)0x80101A0B), PhotoErrPath = unchecked((int)0x80101A0F);

    /// <summary>Sizes of the system batch entries (path[0x400], text pointers, thumbnail) and of their results.</summary>
    internal const int PhotoBatchInSize = 0x448, PhotoBatchOutSize = 0x408, PhotoBatchMax = 10;

    /// <summary>libScePhotoExport's source rules (0x810010C4): a path shorter than 0x400 bytes, not on photo0:, vs0:
    /// or vd0: (0x80101A0F), a param whose reserved bytes (+0x10, 32) are zero (0x80101A01), a file that exists
    /// (0x80101A02), of less than 2 GiB with an image extension (.bmp .gif .jpg .jpeg .png .tif .tiff .mpo;
    /// 0x80101A09).</summary>
    int CheckPhotoSource(uint pathPtr, uint param, out string path)
    {
        path = "";
        if (pathPtr == 0)
        {
            return PhotoErrInvalid;
        }

        path = GuestMemory.ReadCString(pathPtr, PathMax);
        if (path.Length >= PathMax - 1 || OnProtectedDevice(path))
        {
            return PhotoErrPath;
        }

        if (param != 0 && GuestMemory.Span(param + 0x10, 32).ContainsAnyExcept((byte)0))
        {
            return PhotoErrInvalid;
        }

        int file = SourceFile(path, PhotoErrNoFile, PhotoErrUnsupported);
        if (file != 0)
        {
            return file;
        }

        return PhotoExtension(path) ? 0 : PhotoErrUnsupported;
    }

    /// <summary>The devices a picture cannot be exported from (the library itself and the system's).</summary>
    static bool OnProtectedDevice(string path)
    {
        return path.StartsWith("photo0:", StringComparison.Ordinal) || path.StartsWith("vs0:", StringComparison.Ordinal)
            || path.StartsWith("vd0:", StringComparison.Ordinal);
    }

    /// <summary>The file exists (else <paramref name="missing"/>) and is below 2 GiB (else <paramref name="tooBig"/>).</summary>
    int SourceFile(string path, int missing, int tooBig)
    {
        string? host = ResolveSource(path);
        if (host == null)
        {
            return missing;
        }

        return new FileInfo(host).Length > int.MaxValue ? tooBig : 0;
    }

    /// <summary>libScePhotoExport 3.74 (0x81000AB0): the last dot within the last 6 bytes starts an image extension
    /// (case-insensitive).</summary>
    internal static bool PhotoExtension(string path)
    {
        string extension = LastExtension(path);
        return extension is ".bmp" or ".gif" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".mpo";
    }

    static string LastExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot < path.Length - 6)
        {
            return "";
        }

        return path[dot..].ToLowerInvariant();
    }

    /// <summary>
    /// NID_76640642 (ScePhotoExport)(path, param, workingMemory, int flags, cancelCb, user, int64 *id) and
    /// NID_719ACE85 (path, param, workingMemory, int flags, cancelCb, user, char *outPath, int outPathSize): the
    /// system's scePhotoExportFromFile (the flags go to the media server with the request), answering the new
    /// picture's media id or its path. The source rules are <see cref="CheckPhotoSource"/>; then the file is copied
    /// into photo0: as scePhotoExportFromFile does. The media server's id of the copy: the emulated photo library
    /// keeps no database, so it is 0 (a failure writes -1 to both words, as the module does).
    /// </summary>
    [Hle("NID_76640642", Nid = 0x76640642, Library = "ScePhotoExport")]
    int PhotoExportFromFileWithId(uint pathPtr, uint param, uint workingMemory, int flags, uint cancelCb, uint user, uint id)
    {
        int result = PhotoExportSystem(pathPtr, param, cancelCb, user, 0, 0);
        if (id != 0)
        {
            uint word = result == 0 ? 0u : 0xFFFFFFFF;
            GuestMemory.W32(id, word);
            GuestMemory.W32(id + 4, word);
        }

        return result;
    }

    [Hle("NID_719ACE85", Nid = 0x719ACE85, Library = "ScePhotoExport")]
    int PhotoExportFromFileWithPath(uint pathPtr, uint param, uint workingMemory, int flags, uint cancelCb, uint user, uint outPath, int outPathSize)
    {
        int result = PhotoExportSystem(pathPtr, param, cancelCb, user, outPath, outPathSize);
        if (result != 0 && outPath != 0 && outPathSize > 0)
        {
            GuestMemory.Write<byte>(outPath, 0);
        }

        return result;
    }

    int PhotoExportSystem(uint pathPtr, uint param, uint cancelCb, uint user, uint outPath, int outPathSize)
    {
        int result = CheckPhotoSource(pathPtr, param, out string path);
        if (result != 0)
        {
            return result;
        }

        if (Cancelled(cancelCb, user))
        {
            return PhotoErrCancelled;
        }

        return CopyFile("ScePhotoExport system export", "photo0", path, outPath, outPathSize > 0 ? (uint)outPathSize : 0, out _);
    }

    /// <summary>The cancel callback (int (*)(void *user)) asks to stop when it answers non-zero.</summary>
    static bool Cancelled(uint cancelCb, uint user)
    {
        return cancelCb != 0 && GuestThread.Current is { } thread && thread.CallGuest(cancelCb, user) != 0;
    }

    /// <summary>
    /// NID_2EFB7250 (ScePhotoExport)(entries, count, workingMemory, cancelCb, ?, user, ?, results): the system's
    /// registration of pictures already on photo0: (0x448-byte entries: path, the texts, a thumbnail) in the media
    /// server's library. libScePhotoExport 3.74 checks entries != NULL and count 1..10, each path non-empty, existing
    /// (0x80101A02), below 2 GiB (0x80101A09), shorter than 0x400 bytes and on photo0: (0x80101A0F), then results !=
    /// NULL, no video (.mp4 0x80101A09); clears each result (path zeroed, two words -1) and sends the list. The
    /// emulated photo library keeps no database: the files are already where the library reads them, each result
    /// gets its path and id 0 (a default).
    /// </summary>
    [Hle("NID_2EFB7250", Nid = 0x2EFB7250, Library = "ScePhotoExport", Default = true)]
    int PhotoExportRegister(uint entries, uint count, uint workingMemory, uint cancelCb, uint option, uint user, uint reserved, uint results)
    {
        if (entries == 0 || count == 0 || count > PhotoBatchMax)
        {
            return PhotoErrInvalid;
        }

        for (uint i = 0; i < count; i++)
        {
            string path = GuestMemory.ReadCString(entries + i * PhotoBatchInSize, PathMax);
            if (path.Length == 0)
            {
                return PhotoErrInvalid;
            }

            int file = SourceFile(path, PhotoErrNoFile, PhotoErrUnsupported);
            if (file != 0)
            {
                return file;
            }

            if (path.Length >= PathMax || !path.StartsWith("photo0:", StringComparison.Ordinal))
            {
                return PhotoErrPath;
            }
        }

        if (results == 0)
        {
            return PhotoErrInvalid;
        }

        for (uint i = 0; i < count; i++)
        {
            string path = GuestMemory.ReadCString(entries + i * PhotoBatchInSize, PathMax);
            if (LastExtension(path) == ".mp4")
            {
                return PhotoErrUnsupported;
            }

            uint result = results + i * PhotoBatchOutSize;
            GuestMemory.Fill(result, 0, PathMax);
            GuestMemory.W32(result + PathMax, 0xFFFFFFFF);
            GuestMemory.W32(result + PathMax + 4, 0xFFFFFFFF);
        }

        for (uint i = 0; i < count; i++)
        {
            if (Cancelled(cancelCb, user))
            {
                return PhotoErrCancelled;
            }

            uint result = results + i * PhotoBatchOutSize;
            GuestMemory.WriteCString(result, GuestMemory.ReadCString(entries + i * PhotoBatchInSize, PathMax), PathMax);
            GuestMemory.W32(result + PathMax, 0);
            GuestMemory.W32(result + PathMax + 4, 0);
        }

        Log.InfoOnce("photo-2EFB7250", $"ScePhotoExport NID_2EFB7250: {count} pictures already on photo0: (no media database: id 0)");
        return 0;
    }

    /// <summary>
    /// NID_05D5A482 (ScePhotoExport)(path, workingMemory, workingMemorySize, cancelCb, user, int64 *id): the system's
    /// registration of a video on photo0: (the media server's request 4). libScePhotoExport 3.74: NULL path or
    /// working memory, or less than 64 KiB of it, 0x80101A01; a path of 0x400 bytes or more or not on photo0:
    /// 0x80101A0F; not .mp4 0x80101A09; a missing file 0x80101A02. The emulated library keeps no database: the id is
    /// 0 (a default).
    /// </summary>
    [Hle("NID_05D5A482", Nid = 0x05D5A482, Library = "ScePhotoExport", Default = true)]
    int PhotoExportRegisterVideo(uint pathPtr, uint workingMemory, uint workingMemorySize, uint cancelCb, uint user, uint id)
    {
        if (pathPtr == 0 || workingMemory == 0 || workingMemorySize < 0x10000)
        {
            return PhotoErrInvalid;
        }

        string path = GuestMemory.ReadCString(pathPtr, PathMax);
        if (path.Length >= PathMax - 1 || !path.StartsWith("photo0:", StringComparison.Ordinal))
        {
            return PhotoErrPath;
        }

        if (LastExtension(path) != ".mp4")
        {
            return PhotoErrUnsupported;
        }

        if (ResolveSource(path) == null)
        {
            return PhotoErrNoFile;
        }

        if (id != 0)
        {
            GuestMemory.W32(id, 0);
            GuestMemory.W32(id + 4, 0);
        }

        Log.InfoOnce("photo-05D5A482", "ScePhotoExport NID_05D5A482: video already on photo0: (no media database: id 0)");
        return 0;
    }

    /// <summary>NID_16B1028E (ScePhotoExport)(int arg): opens a session with the media server (the argument goes with
    /// it) and closes it again, answering the session's result. The emulated photo library is always there: 0 (a
    /// default).</summary>
    [Hle("NID_16B1028E", Nid = 0x16B1028E, Library = "ScePhotoExport", Default = true)]
    int PhotoExportPing(uint argument)
    {
        Log.InfoOnce("photo-16B1028E", "ScePhotoExport NID_16B1028E: media server session opened and closed");
        return 0;
    }

    /// <summary>NID_F4E0F7FF (ScePhotoExport)(): tells the "avmedia_service:" (a 0x40-byte notification, both ids -1)
    /// to look at the library again; nobody listens here (a default).</summary>
    [Hle("NID_F4E0F7FF", Nid = 0xF4E0F7FF, Library = "ScePhotoExport", Default = true)]
    int PhotoExportNotifyMediaService()
    {
        Log.InfoOnce("photo-F4E0F7FF", "ScePhotoExport NID_F4E0F7FF: avmedia_service notification (no listener)");
        return 0;
    }
}
