using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// System-imported functions without public names of SceJpegEncArm, SceAppUtil and ScePhotoExport, and the 3.74 codes
/// of NpMatching2, NpParty and sceMusicExportFromFile, each as its FW 3.74 module answers (libscejpegencarm,
/// apputil, libScePhotoExport, np_matching2, np_party, libSceMusicExport read in their code).
/// </summary>
[Collection("guest")]
public sealed class SystemImportsLot2Tests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "sys-lot2");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-sys-lot2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData(0x4EE1BAEDu, false)] // SceJpegEncArm
    [InlineData(0xE8CADCA9u, false)] // SceAppUtil
    [InlineData(0x1CFFE892u, false)]
    [InlineData(0xC3CB128Bu, false)]
    [InlineData(0xC0418B1Cu, false)]
    [InlineData(0x76640642u, false)] // ScePhotoExport
    [InlineData(0x719ACE85u, false)]
    [InlineData(0x2EFB7250u, true)]
    [InlineData(0x05D5A482u, true)]
    [InlineData(0x16B1028Eu, true)]
    [InlineData(0xF4E0F7FFu, true)]
    public void TheLibrarysSystemFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    private uint Aligned(uint size)
    {
        return (_harness.Alloc(size + 16) + 15) & ~15u;
    }

    private uint Put(byte[] data)
    {
        uint address = Aligned((uint)data.Length);
        data.CopyTo(GuestMemory.Span(address, data.Length));
        return address;
    }

    /// <summary>The planes of MCU row <paramref name="row"/> (luma pitch w, chroma pitch w / 2).</summary>
    private (uint Y, uint Cb, uint Cr) Row(JpegHleTests.Planes p, int row, int mcuHeight)
    {
        int chromaRows = mcuHeight / p.VDiv;
        byte[] y = p.Y.AsSpan(row * mcuHeight * p.W, Math.Min(mcuHeight, p.H - row * mcuHeight) * p.W).ToArray();
        byte[] cb = p.Cb!.AsSpan(row * chromaRows * p.CW, Math.Min(chromaRows, p.CH - row * chromaRows) * p.CW).ToArray();
        byte[] cr = p.Cr!.AsSpan(row * chromaRows * p.CW, Math.Min(chromaRows, p.CH - row * chromaRows) * p.CW).ToArray();
        return (Put(y), Put(cb), Put(cr));
    }

    [Theory]
    [InlineData(8, 2)] // 4:2:0, 16x16 MCUs
    [InlineData(9, 1)] // 4:2:2, 16x8 MCUs
    public void JpegArmEncodeInPartsGivesTheWholeImagesBytes(int format, int vdiv)
    {
        JpegEnc e = _harness.Module<JpegEnc>();
        JpegHleTests.Planes src = JpegHleTests.Synthetic(64, 32, 2, vdiv);
        uint ctx = Aligned(0x180), outp = Aligned(0x4000), written = Aligned(4);
        Assert.Equal(0, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)64, (ushort)32, format, outp, 0x4000u));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetValidRegion", ctx, (ushort)64, (ushort)30));

        // the stream with its own buffer per call
        List<byte> parts = [];
        uint part = Aligned(0x1000);
        (uint y0, uint cb0, uint cr0) = Row(src, 0, 8 * vdiv);
        Assert.Equal(0, Call(e, "JpegArmEncoderEncodePart", ctx, y0, cb0, cr0, 0u, part, 0x1000u, written));
        parts.AddRange(GuestMemory.Span(part, (int)GuestMemory.R32(written)).ToArray());
        int rows = (30 + 8 * vdiv - 1) / (8 * vdiv);
        int total = 0;
        for (int row = 0; row < rows; row++)
        {
            (uint y, uint cb, uint cr) = Row(src, row, 8 * vdiv);
            int result = Call(e, "JpegArmEncoderEncodePart", ctx, y, cb, cr, 1u, part, 0x1000u, written);
            parts.AddRange(GuestMemory.Span(part, (int)GuestMemory.R32(written)).ToArray());
            if (row < rows - 1)
            {
                Assert.Equal(0, result);
            }
            else
            {
                total = result;
            }
        }

        (byte[] luma, byte[] chroma) = JpegEncoder.TablesForRatio(JpegEnc.DefaultRatio);
        byte[] whole = JpegEncoder.Encode(src.Y, src.W, src.Cb, src.Cr, src.CW, 64, 30, 2, vdiv, luma, chroma);
        Assert.Equal(whole.Length, total);
        Assert.Equal(whole, parts.ToArray());
        Assert.Equal(JpegEnc.ARM_ERR_STREAM_STATE, Call(e, "JpegArmEncoderEncodePart", ctx, y0, cb0, cr0, 1u, part, 0x1000u, written));

        // the same stream in the context's buffer, one MCU at a time (16-wide blocks)
        // (a call with its own buffer keeps that buffer as the context's: the context's is given again)
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetOutputAddr", ctx, outp, 0x4000u));
        Assert.Equal(0, Call(e, "JpegArmEncoderEncodePart", ctx, y0, cb0, cr0, 0u, 0u, 0u, 0u));
        int last = 0;
        int mcuHeight = 8 * vdiv;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                int lumaRows = Math.Min(mcuHeight, 32 - row * mcuHeight);
                byte[] y = new byte[16 * mcuHeight];
                byte[] cb = new byte[8 * (mcuHeight / vdiv)];
                byte[] cr = new byte[8 * (mcuHeight / vdiv)];
                for (int line = 0; line < lumaRows; line++)
                {
                    src.Y.AsSpan((row * mcuHeight + line) * 64 + column * 16, 16).CopyTo(y.AsSpan(line * 16));
                }

                for (int line = 0; line < lumaRows / vdiv; line++)
                {
                    src.Cb!.AsSpan((row * mcuHeight / vdiv + line) * 32 + column * 8, 8).CopyTo(cb.AsSpan(line * 8));
                    src.Cr!.AsSpan((row * mcuHeight / vdiv + line) * 32 + column * 8, 8).CopyTo(cr.AsSpan(line * 8));
                }

                last = Call(e, "JpegArmEncoderEncodePart", ctx, Put(y), Put(cb), Put(cr), 3u, 0u, 0u, 0u);
            }
        }

        Assert.Equal(whole.Length, last);
        Assert.Equal(whole, GuestMemory.Span(outp, last).ToArray());
    }

    [Fact]
    public void JpegArmEncodePartChecksLikeTheModule()
    {
        JpegEnc e = _harness.Module<JpegEnc>();
        uint ctx = Aligned(0x180), outp = Aligned(0x400), plane = Aligned(0x400), written = Aligned(4);
        Assert.Equal(0, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)16, (ushort)16, 8, 0u, 0u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "JpegArmEncoderEncodePart", ctx + 2, plane, plane, plane, 0u, outp, 0x400u, written));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "JpegArmEncoderEncodePart", ctx, plane + 4, plane, plane, 0u, outp, 0x400u, written));
        Assert.Equal(JpegEnc.ARM_ERR_STREAM_STATE, Call(e, "JpegArmEncoderEncodePart", ctx, plane, plane, plane, 4u, outp, 0x400u, written));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "JpegArmEncoderEncodePart", ctx, plane, plane, plane, 0u, outp, 0x400u, 0u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "JpegArmEncoderEncodePart", ctx, plane, plane, plane, 0u, outp, 3u, written));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "JpegArmEncoderEncodePart", ctx, plane, plane, plane, 0u, 0u, 0u, 0u)); // no buffer anywhere
        Assert.Equal(JpegEnc.ARM_ERR_INSUFFICIENT_BUFFER, Call(e, "JpegArmEncoderEncodePart", ctx, plane, plane, plane, 0u, outp, 0x265u, written));
        Assert.Equal(0, Call(e, "JpegArmEncoderEncodePart", ctx, plane, plane, plane, 0u, outp, 0x400u, written));

        // 3.74's init and the context's other checks
        Assert.Equal(0x180, Call(e, "sceJpegArmEncoderGetContextSize"));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)32, (ushort)16, 8 | 16, outp, 0x400u));
        Assert.Equal(JpegEnc.ARM_ERR_IMAGE_SIZE, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)24, (ushort)16, 8, outp, 0x400u));
        Assert.Equal(JpegEnc.ARM_ERR_IMAGE_SIZE, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)32, (ushort)8, 8, outp, 0x400u));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)32, (ushort)8, 9, outp, 0x400u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "sceJpegArmEncoderInit", ctx + 1, (ushort)32, (ushort)8, 9, outp, 0x400u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)32, (ushort)8, 9, outp, 3u));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetCompressionRatio", ctx, 0u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_COMP_RATIO, Call(e, "sceJpegArmEncoderSetCompressionRatio", ctx, 0x100u));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetOutputAddr", ctx, 0u, 0u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "sceJpegArmEncoderSetOutputAddr", ctx, outp, 3u));
    }

    private AppUtil InitializedAppUtil()
    {
        AppUtil u = _harness.Module<AppUtil>();
        uint init = _harness.Alloc(0x80), boot = _harness.Alloc(0x80);
        Call(u, "sceAppUtilInit", init, boot);
        return u;
    }

    [Fact]
    public void AppUtilSaveChecksNeedNoInitAndCheckLikeTheSave()
    {
        AppUtil u = _harness.Module<AppUtil>();
        uint files = _harness.Alloc(2 * AppUtil.SaveFileSize);
        uint data = _harness.Alloc(16);
        GuestMemory.W32(files, _harness.Str("save.bin"));
        GuestMemory.W32(files + 4, data);
        GuestMemory.W32(files + 8, 16);
        uint required = _harness.Word(0);
        uint slot = _harness.Alloc(8);
        GuestMemory.W32(slot, 3);
        Assert.Equal(0, Call(u, "AppUtilSaveDataDataSaveCheck", slot, files, 1u, 0u, required));
        Assert.Equal(0, Call(u, "AppUtilSaveDataDataSaveCheck", 0u, files, 1u, _harness.Str("savedata0:"), required));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", 0u, files, 1u, _harness.Str("savedata1:"), required));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", 0u, files, 1u, 0u, 0u));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", 0u, files, 6u, 0u, required));
        GuestMemory.W32(slot, 256);
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", slot, files, 1u, 0u, required));

        // more than 1 MiB in one save
        GuestMemory.W32(files + 8, 0x80000);
        GuestMemory.W32(files + AppUtil.SaveFileSize, _harness.Str("other.bin"));
        GuestMemory.W32(files + AppUtil.SaveFileSize + 4, data);
        GuestMemory.W32(files + AppUtil.SaveFileSize + 8, 0x80001);
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", 0u, files, 2u, 0u, required));
        GuestMemory.W32(files + AppUtil.SaveFileSize + 8, 0x80000);
        Assert.Equal(0, Call(u, "AppUtilSaveDataDataSaveCheck", 0u, files, 2u, 0u, required));

        // a new slot param must be terminated, valid UTF-8, with a clean icon path
        GuestMemory.W32(slot, 1);
        uint param = _harness.Alloc((uint)AppUtil.SlotParamSize);
        GuestMemory.W32(slot + 4, param);
        GuestMemory.WriteCString(param + 4, "Title");
        Assert.Equal(0, Call(u, "AppUtilSaveDataDataSaveCheck", slot, files, 1u, 0u, required));
        GuestMemory.Write<byte>(param + 5, 0xFF);
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", slot, files, 1u, 0u, required));
        GuestMemory.WriteCString(param + 4, "Title");
        GuestMemory.WriteCString(param + 0x2C4, "sce_sys/../icon0.png");
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", slot, files, 1u, 0u, required));
        GuestMemory.WriteCString(param + 0x2C4, "ICON0.PNG");
        GuestMemory.Write<byte>(param + 0x31C, 1);
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataSaveCheck", slot, files, 1u, 0u, required));
    }

    [Fact]
    public void AppUtilRemoveCheckFollowsTheRemoveRules()
    {
        AppUtil u = _harness.Module<AppUtil>();
        uint items = _harness.Alloc(AppUtil.RemoveItemSize);
        GuestMemory.W32(items, _harness.Str("save.bin"));
        Assert.Equal(0, Call(u, "AppUtilSaveDataDataRemoveCheck", 0u, items, 1u, 0u));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataRemoveCheck", 0u, 0u, 1u, 0u));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataRemoveCheck", 0u, items, 0u, 0u));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataRemoveCheck", 0u, items, 1u, _harness.Str("savedata1:")));
        GuestMemory.W32(items, _harness.Str("../save.bin"));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilSaveDataDataRemoveCheck", 0u, items, 1u, 0u));
    }

    [Fact]
    public void AppUtilPspSaveRootMountIsCounted()
    {
        AppUtil u = _harness.Module<AppUtil>();
        uint mount = _harness.Alloc(16);
        Assert.Equal(AppUtil.ERR_NOT_INITIALIZED, Call(u, "AppUtilPspSaveDataRootMount", 0xCA, mount));
        Assert.Equal(AppUtil.ERR_NOT_INITIALIZED, Call(u, "AppUtilPspSaveDataRootUmount", 0xCA));
        InitializedAppUtil();
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilPspSaveDataRootMount", 0xCB, mount));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilPspSaveDataRootMount", 0xCA, 0u));
        Assert.Equal(0, Call(u, "AppUtilPspSaveDataRootMount", 0xCA, mount));
        string first = GuestMemory.ReadCString(mount, 16);
        Assert.NotEqual("", first);
        Assert.True(_harness.Emu.Vfs.IsMounted(first.TrimEnd(':'))); // a mount of SceAppMgr's, not a folder path
        uint second = _harness.Alloc(16);
        Assert.Equal(unchecked((int)0x80100603), Call(u, "AppUtilPspSaveDataRootMount", 0xCA, second));
        Assert.Equal(first, GuestMemory.ReadCString(second, 16));
        Assert.Equal(AppUtil.ERR_PARAMETER, Call(u, "AppUtilPspSaveDataRootUmount", 0));
        Assert.Equal(0, Call(u, "AppUtilPspSaveDataRootUmount", 0xCA));
        Assert.True(_harness.Emu.Vfs.IsMounted(first.TrimEnd(':'))); // one user left
        Assert.Equal(0, Call(u, "AppUtilPspSaveDataRootUmount", 0xCA));
        Assert.False(_harness.Emu.Vfs.IsMounted(first.TrimEnd(':')));
        Assert.Equal(AppUtil.ERR_NOT_MOUNTED, Call(u, "AppUtilPspSaveDataRootUmount", 0xCA)); // nothing left to unmount
    }

    private (MediaExport M, Core.Io.Vfs Vfs) NewExport()
    {
        Core.Io.Vfs vfs = new(_root);
        string app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        vfs.Mount("app0", app);
        File.WriteAllBytes(Path.Combine(app, "shot.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        File.WriteAllBytes(Path.Combine(app, "song.mp3"), [0x49, 0x44, 0x33, 1]);
        File.WriteAllBytes(Path.Combine(app, "notes.txt"), [1]);
        return (new MediaExport { VfsOverride = vfs, TitleIdOverride = "NPXS10004" }, vfs);
    }

    [Fact]
    public void PhotoSystemExportsCheckTheSourceThenCopy()
    {
        (MediaExport m, Core.Io.Vfs vfs) = NewExport();
        uint param = _harness.Alloc(0x30), id = _harness.Alloc(8), outPath = _harness.Alloc(0x100);
        Assert.Equal(MediaExport.PhotoErrInvalid, Call(m, "PhotoExportFromFileWithId", 0u, param, 0u, 0, 0u, 0u, id));
        Assert.Equal(MediaExport.PhotoErrPath, Call(m, "PhotoExportFromFileWithId", _harness.Str("photo0:a.jpg"), param, 0u, 0, 0u, 0u, id));
        Assert.Equal(MediaExport.PhotoErrPath, Call(m, "PhotoExportFromFileWithId", _harness.Str("vs0:a.jpg"), param, 0u, 0, 0u, 0u, id));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(id));
        Assert.Equal(MediaExport.PhotoErrNoFile, Call(m, "PhotoExportFromFileWithId", _harness.Str("app0:none.jpg"), param, 0u, 0, 0u, 0u, id));
        Assert.Equal(MediaExport.PhotoErrUnsupported, Call(m, "PhotoExportFromFileWithId", _harness.Str("app0:notes.txt"), param, 0u, 0, 0u, 0u, id));
        GuestMemory.Write<byte>(param + 0x10, 1);
        Assert.Equal(MediaExport.PhotoErrInvalid, Call(m, "PhotoExportFromFileWithId", _harness.Str("app0:shot.jpg"), param, 0u, 0, 0u, 0u, id));
        GuestMemory.Write<byte>(param + 0x10, 0);
        Assert.Equal(0, Call(m, "PhotoExportFromFileWithId", _harness.Str("app0:shot.jpg"), param, 0u, 0, 0u, 0u, id));
        Assert.Equal(0u, GuestMemory.R32(id));
        Assert.Equal(0, Call(m, "PhotoExportFromFileWithPath", _harness.Str("app0:shot.jpg"), param, 0u, 0, 0u, 0u, outPath, 0x100));
        Assert.Equal("photo0:/NPXS10004/shot_1.jpg", GuestMemory.ReadCString(outPath));
        Assert.True(File.Exists(vfs.Resolve("photo0:/NPXS10004/shot_1.jpg")));
    }

    [Fact]
    public void PhotoRegistrationsTakeOnlyFilesOnPhoto0()
    {
        (MediaExport m, Core.Io.Vfs vfs) = NewExport();
        string photoHost = vfs.Resolve("photo0:/DCIM", forWrite: true)!;
        Directory.CreateDirectory(photoHost);
        File.WriteAllBytes(Path.Combine(photoHost, "a.jpg"), [0xFF, 0xD8, 0xFF]);
        File.WriteAllBytes(Path.Combine(photoHost, "v.mp4"), [0, 0, 0, 0x18]);
        uint entries = _harness.Alloc(2 * MediaExport.PhotoBatchInSize), results = _harness.Alloc(2 * MediaExport.PhotoBatchOutSize);
        GuestMemory.WriteCString(entries, "photo0:/DCIM/a.jpg");
        Assert.Equal(MediaExport.PhotoErrInvalid, Call(m, "PhotoExportRegister", 0u, 1u, 0u, 0u, 0u, 0u, 0u, results));
        Assert.Equal(MediaExport.PhotoErrInvalid, Call(m, "PhotoExportRegister", entries, 11u, 0u, 0u, 0u, 0u, 0u, results));
        Assert.Equal(MediaExport.PhotoErrInvalid, Call(m, "PhotoExportRegister", entries, 1u, 0u, 0u, 0u, 0u, 0u, 0u));
        GuestMemory.WriteCString(entries + MediaExport.PhotoBatchInSize, "app0:shot.jpg");
        Assert.Equal(MediaExport.PhotoErrPath, Call(m, "PhotoExportRegister", entries, 2u, 0u, 0u, 0u, 0u, 0u, results));
        GuestMemory.WriteCString(entries + MediaExport.PhotoBatchInSize, "photo0:/DCIM/v.mp4");
        Assert.Equal(MediaExport.PhotoErrUnsupported, Call(m, "PhotoExportRegister", entries, 2u, 0u, 0u, 0u, 0u, 0u, results));
        Assert.Equal(0, Call(m, "PhotoExportRegister", entries, 1u, 0u, 0u, 0u, 0u, 0u, results));
        Assert.Equal("photo0:/DCIM/a.jpg", GuestMemory.ReadCString(results));
        Assert.Equal(0u, GuestMemory.R32(results + 0x400));

        uint work = _harness.Alloc(16), id = _harness.Word(0xAAAAAAAA);
        uint video = _harness.Str("photo0:/DCIM/v.mp4");
        Assert.Equal(MediaExport.PhotoErrInvalid, Call(m, "PhotoExportRegisterVideo", video, work, 0xFFFFu, 0u, 0u, id));
        Assert.Equal(MediaExport.PhotoErrPath, Call(m, "PhotoExportRegisterVideo", _harness.Str("app0:v.mp4"), work, 0x10000u, 0u, 0u, id));
        Assert.Equal(MediaExport.PhotoErrUnsupported, Call(m, "PhotoExportRegisterVideo", _harness.Str("photo0:/DCIM/a.jpg"), work, 0x10000u, 0u, 0u, id));
        Assert.Equal(MediaExport.PhotoErrNoFile, Call(m, "PhotoExportRegisterVideo", _harness.Str("photo0:/DCIM/w.mp4"), work, 0x10000u, 0u, 0u, id));
        Assert.Equal(0, Call(m, "PhotoExportRegisterVideo", video, work, 0x10000u, 0u, 0u, id));
        Assert.Equal(0u, GuestMemory.R32(id));
        Assert.Equal(0, Call(m, "PhotoExportPing", 0u));
        Assert.Equal(0, Call(m, "PhotoExportNotifyMediaService"));
    }

    [Fact]
    public void MusicExportFromFileAnswersTheModulesCodes()
    {
        (MediaExport m, _) = NewExport();
        uint outPath = _harness.Alloc(0x100), param = _harness.Alloc(0x80);
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "sceMusicExportFromFile", 0u, param, 0u, 0u, 0u, 0u, outPath, 0x100));
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "sceMusicExportFromFile", _harness.Str("app0:song.mp3"), param, 0u, 0u, 0u, 0u, 0u, 0x100));
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "sceMusicExportFromFile", _harness.Str("app0:song.mp3"), param, 0u, 0u, 0u, 0u, outPath, -1));
        Assert.Equal(MediaExport.MusicErrPath, Call(m, "sceMusicExportFromFile", _harness.Str("music0:song.mp3"), param, 0u, 0u, 0u, 0u, outPath, 0x100));
        Assert.Equal(MediaExport.MusicErrNoFile, Call(m, "sceMusicExportFromFile", _harness.Str("app0:none.mp3"), param, 0u, 0u, 0u, 0u, outPath, 0x100));
        Assert.Equal(MediaExport.MusicErrUnsupported, Call(m, "sceMusicExportFromFile", _harness.Str("app0:notes.txt"), param, 0u, 0u, 0u, 0u, outPath, 0x100));
        Assert.Equal(0, Call(m, "sceMusicExportFromFile", _harness.Str("app0:song.mp3"), param, 0u, 0u, 0u, 0u, outPath, 0x100));
        Assert.Equal("music0:/NPXS10004/song.mp3", GuestMemory.ReadCString(outPath));
    }

    [Fact]
    public void NpMatching2AnswersThe374ClientCodes()
    {
        NpMatching2 m = _harness.Module<NpMatching2>();
        uint ctxId = _harness.Alloc(4);
        Assert.Equal(unchecked((int)0x80550C03), Call(m, "sceNpMatching2Term"));
        Assert.Equal(unchecked((int)0x80550C03), Call(m, "sceNpMatching2CreateContext", 0u, 0u, 0u, new Ptr<ushort>(ctxId)));
        Assert.Equal(unchecked((int)0x80550C03), Call(m, "sceNpMatching2DestroyContext", (ushort)1));
        Assert.Equal(0, Call(m, "sceNpMatching2Init", 0u, 0, 0, 0u));
        Assert.Equal(unchecked((int)0x80550C02), Call(m, "sceNpMatching2Init", 0u, 0, 0, 0u));
        Assert.Equal(unchecked((int)0x80550C0A), Call(m, "sceNpMatching2CreateContext", 0u, 0u, 0u, new Ptr<ushort>(0)));
        Assert.Equal(unchecked((int)0x80550C06), Call(m, "sceNpMatching2DestroyContext", (ushort)77));
        Assert.Equal(unchecked((int)0x80550C06), Call(m, "sceNpMatching2ContextStart", (ushort)77, 0UL));
        Assert.Equal(unchecked((int)0x80550C3B), Call(m, "sceNpMatching2ContextStart", (ushort)77, 4_999_999UL));
        Assert.Equal(0, Call(m, "sceNpMatching2Term"));
        Assert.Equal(unchecked((int)0x80550C03), Call(m, "sceNpMatching2Term"));
    }

    [Fact]
    public void NpPartyNeedsItsInitialisation()
    {
        NpSocial social = _harness.Module<NpSocial>();
        uint word = _harness.Alloc(8), param = _harness.Alloc(16), handlers = _harness.Alloc(16);
        const int NotInit = NpSocial.PARTY_NOT_INITIALIZED, Invalid = NpSocial.PARTY_INVALID_ARGUMENT, NotInParty = unchecked((int)0x80553402);
        Assert.Equal(NotInit, Call(social, "sceNpPartyTerm"));
        Assert.Equal(NotInit, Call(social, "sceNpPartyCheckCallback"));
        Assert.Equal(NotInit, Call(social, "sceNpPartyRegisterHandler", handlers, 0u));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetState", word));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetId", word));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetMembers", word));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetMemberInfo", word, word));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetMemberVoiceInfo", (ushort)1, word));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetMemberSessionInfo", (ushort)1, word));
        Assert.Equal(NotInit, Call(social, "sceNpPartyGetGameSessionReadyState", word));
        Assert.Equal(NotInit, Call(social, "NpPartyRequest110018"));
        Assert.Equal(NotInit, Call(social, "NpPartyRequest110019"));

        GuestMemory.W32(param, 0x03550000);
        GuestMemory.W32(param + 4, 1);
        Assert.Equal(Invalid, Call(social, "sceNpPartyInit", param));
        GuestMemory.W32(param + 4, 0);
        Assert.Equal(0, Call(social, "sceNpPartyInit", param));
        Assert.Equal(NpSocial.PARTY_ALREADY_INITIALIZED, Call(social, "sceNpPartyInit", param));
        Assert.Equal(0, Call(social, "sceNpPartyCheckCallback"));
        Assert.Equal(Invalid, Call(social, "sceNpPartyGetState", 0u));
        Assert.Equal(0, Call(social, "sceNpPartyGetState", word));
        Assert.Equal(NotInParty, Call(social, "sceNpPartyGetMembers", word));
        Assert.Equal(NpSocial.PARTY_NO_VOICE_HANDLER, Call(social, "sceNpPartyGetMemberVoiceInfo", (ushort)1, word));
        Assert.Equal(NpSocial.PARTY_NO_SESSION_HANDLER, Call(social, "sceNpPartyGetGameSessionReadyState", word));
        GuestMemory.W32(handlers + 8, 0x81000001);
        GuestMemory.W32(handlers + 0xC, 0x81000003);
        Assert.Equal(0, Call(social, "sceNpPartyRegisterHandler", handlers, 0u));
        Assert.Equal(Invalid, Call(social, "sceNpPartyGetMemberVoiceInfo", (ushort)1, 0u));
        Assert.Equal(NotInParty, Call(social, "sceNpPartyGetMemberVoiceInfo", (ushort)1, word));
        Assert.Equal(NotInParty, Call(social, "sceNpPartyGetGameSessionReadyState", word));
        Assert.Equal(NotInParty, Call(social, "NpPartyRequest110018"));
        Assert.Equal(0, Call(social, "sceNpPartyTerm"));
    }
}
