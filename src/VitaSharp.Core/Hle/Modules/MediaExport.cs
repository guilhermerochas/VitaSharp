using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePhotoExport / SceVideoExport / SceMusicExport: copy a file of the game into the system media library. Here the
/// file is copied (synchronously) into photo0:/video0:/music0: through the VFS, under a &lt;TITLEID&gt; folder, with a
/// numeric suffix when the name is taken; the Vita path of the copy is returned. Error codes are not documented:
/// INVALID_ARGUMENT for bad arguments / unsupported data and ENOENT for a missing source.
/// </summary>
public sealed class MediaExport : HleModule
{
    internal const int ERR_INVALID = SceError.INVALID_ARGUMENT, ERR_NOENT = SceError.IO_ENOENT;
    /// <summary>sizeof(VideoExportInputParam) / sizeof(VideoExportOutputParam); both start with path[1024].</summary>
    internal const int VideoInSize = 0x440, VideoOutSize = 0x408, PathMax = 1024;

    /// <summary>Test hooks: VFS, title id and clock used instead of the emulator's.</summary>
    internal Vfs? VfsOverride { get; set; }
    internal string? TitleIdOverride { get; set; }
    internal Func<DateTime>? NowOverride { get; set; }
    Vfs Vfs => VfsOverride ?? Emu.Vfs;
    string TitleId => TitleIdOverride ?? Emu.TitleId;
    DateTime Now => NowOverride?.Invoke() ?? DateTime.Now;

    /// <summary>Image extension from the magic bytes (JPEG, PNG), or null when unsupported.</summary>
    internal static string? ImageExtension(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head.Length >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ".png";
        return null;
    }

    /// <summary>Host path of a game file (FIOS overlays first, like the game's own I/O).</summary>
    string? ResolveSource(string path)
    {
        string resolved = Emu is not null ? Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault()?.Resolve(path) ?? path : path;
        var host = Vfs.Resolve(resolved);
        return host != null && File.Exists(host) ? host : null;
    }

    /// <summary>Picks "&lt;device&gt;:/&lt;TITLEID&gt;/&lt;name&gt;" (name, name_1, name_2 ...) not yet used and returns
    /// (Vita path, host path).</summary>
    internal (string Vita, string Host) Destination(string device, string fileName)
    {
        string folder = string.IsNullOrEmpty(TitleId) ? "VITASHARP" : TitleId;
        string dirVita = $"{device}:/{folder}";
        string dirHost = Vfs.Resolve(dirVita, forWrite: true) ?? throw new IOException($"{device}: is not mounted");
        Directory.CreateDirectory(dirHost);
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 0; ; i++)
        {
            string name = i == 0 ? stem + ext : $"{stem}_{i}{ext}";
            string host = Path.Combine(dirHost, name);
            if (!File.Exists(host)) return ($"{dirVita}/{name}", host);
        }
    }

    static void WriteOutPath(uint outPath, uint outPathSize, string vitaPath)
    {
        if (outPath != 0 && outPathSize > 0) GuestMemory.WriteCString(outPath, vitaPath, (int)outPathSize);
    }

    /// <summary>Reports completion to the optional progress callback (void (*)(void *user, int percent)).</summary>
    static void Progress(uint progress, uint user)
    {
        if (progress != 0 && GuestThread.Current is { } t) t.CallGuest(progress, user, 100);
    }

    int CopyFile(string fn, string device, string source, uint outPath, uint outPathSize, out string? vita)
    {
        vita = null;
        var host = ResolveSource(source);
        if (host == null) { Log.Warn($"{fn}: '{source}' not found"); return ERR_NOENT; }
        try
        {
            var dst = Destination(device, Path.GetFileName(host));
            File.Copy(host, dst.Host);
            vita = dst.Vita;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{fn}: copy of '{source}' failed: {e.Message}");
            return SceError.ERROR;
        }
        Log.Info($"{fn}: '{source}' -> {vita}");
        WriteOutPath(outPath, outPathSize, vita);
        return 0;
    }

    // ------------------------------------------------------------------ ScePhotoExport

    /// <summary>PhotoExportParam (0x30): version, photoTitle, gameTitle, gameComment (UTF-32), reserved[8].</summary>
    static void LogParam(string fn, uint param)
    {
        var title = ScreenShot.ReadUtf32(GuestMemory.R32(param + 4), 64);
        if (!string.IsNullOrEmpty(title)) Log.Debug($"{fn}: photo title '{title}'");
    }

    [Hle]
    int scePhotoExportFromData(uint data, uint size, uint param, uint workingMemory, uint cancelCb, uint user, uint outPath, uint outPathSize)
    {
        if (data == 0 || size == 0 || param == 0) return ERR_INVALID;
        var bytes = GuestMemory.Span(data, (int)size);
        if (ImageExtension(bytes) is not { } ext) return ERR_INVALID;
        LogParam(nameof(scePhotoExportFromData), param);
        string vita;
        try
        {
            var dst = Destination("photo0", Now.ToString("yyyyMMdd_HHmmss") + ext);
            File.WriteAllBytes(dst.Host, bytes.ToArray());
            vita = dst.Vita;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"scePhotoExportFromData failed: {e.Message}");
            return SceError.ERROR;
        }
        Log.Info($"scePhotoExportFromData: {size} bytes -> {vita}");
        WriteOutPath(outPath, outPathSize, vita);
        return 0;
    }

    /// <summary>Format of a source file: 0 when it is a supported image, else the error.</summary>
    int CheckImageFile(string path, out string? host)
    {
        host = ResolveSource(path);
        if (host == null) return ERR_NOENT;
        Span<byte> head = stackalloc byte[8];
        int n;
        try { using var f = File.OpenRead(host); n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false); }
        catch (IOException) { return ERR_NOENT; }
        return ImageExtension(head[..n]) != null ? 0 : ERR_INVALID;
    }

    [Hle]
    int scePhotoExportFromFile(uint pathPtr, uint param, uint workingMemory, uint cancelCb, uint user, uint outPath, uint outPathSize)
    {
        if (pathPtr == 0 || param == 0) return ERR_INVALID;
        string path = GuestMemory.ReadCString(pathPtr, PathMax);
        int r = CheckImageFile(path, out _);
        if (r < 0) return r;
        LogParam(nameof(scePhotoExportFromFile), param);
        return CopyFile(nameof(scePhotoExportFromFile), "photo0", path, outPath, outPathSize, out _);
    }

    /// <summary>Inferred prototype: 0 when the data is a supported image (JPEG / PNG).</summary>
    [Hle]
    int scePhotoExportIsAvailableFromData(uint data, uint size)
    {
        if (data == 0 || size == 0) return ERR_INVALID;
        return ImageExtension(GuestMemory.Span(data, (int)Math.Min(size, 8u))) != null ? 0 : ERR_INVALID;
    }

    /// <summary>Inferred prototype: 0 when the file exists and is a supported image.</summary>
    [Hle]
    int scePhotoExportIsAvailableFromFile(uint pathPtr) =>
        pathPtr == 0 ? ERR_INVALID : CheckImageFile(GuestMemory.ReadCString(pathPtr, PathMax), out _);

    // ------------------------------------------------------------------ SceVideoExport / SceMusicExport

    [Hle]
    int sceVideoExportFromFile(uint inParam, int unk, uint workingMemory, uint cancelCb, uint progress, uint user, int unk2, uint outParam)
    {
        if (inParam == 0 || outParam == 0) return ERR_INVALID;
        string path = GuestMemory.ReadCString(inParam, PathMax);
        if (path.Length == 0) return ERR_INVALID;
        GuestMemory.Fill(outParam, 0, VideoOutSize);
        int r = CopyFile(nameof(sceVideoExportFromFile), "video0", path, outParam, PathMax, out _);
        if (r == 0) Progress(progress, user);
        return r;
    }

    /// <summary>MusicExportParam (0x80) is reserved.</summary>
    [Hle]
    int sceMusicExportFromFile(uint pathPtr, uint param, uint workingMemory, uint cancelCb, uint progress, uint user, uint outPath, uint outPathSize)
    {
        if (pathPtr == 0) return ERR_INVALID;
        string path = GuestMemory.ReadCString(pathPtr, PathMax);
        if (path.Length == 0) return ERR_INVALID;
        int r = CopyFile(nameof(sceMusicExportFromFile), "music0", path, outPath, outPathSize, out _);
        if (r == 0) Progress(progress, user);
        return r;
    }
}
