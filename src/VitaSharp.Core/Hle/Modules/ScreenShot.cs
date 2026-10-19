using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceScreenShot: the game controls whether the system screenshot shortcut (PS + START) is allowed and which metadata and
/// watermark the captures carry. VitaSharp has no system shortcut yet; the state is kept for a host screenshot feature.
/// </summary>
public sealed class ScreenShot : HleModule
{
    internal const int ERR_INVALID_ARGUMENT = unchecked((int)0x80102F01), ERR_NO_MEMORY = unchecked((int)0x80102F02),
        ERR_FILE_NOT_FOUND = unchecked((int)0x80102F03), ERR_NOT_SUPPORTED_FORMAT = unchecked((int)0x80102F04),
        ERR_MEDIA_FULL = unchecked((int)0x80102F05), ERR_INTERNAL = unchecked((int)0x80102F06);

    /// <summary>Max characters (without NUL) of photoTitle, gameTitle and gameComment (SCE_SCREENSHOT_MAX_*_LEN).</summary>
    const int MaxTitleLen = 64, MaxCommentLen = 128;

    readonly object _lock = new();

    /// <summary>The system screenshot shortcut is allowed (sceScreenShotEnable/Disable).</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>The app wants the screenshot app event (sceScreenShotEnable/DisableNotification).</summary>
    public bool Notification { get; private set; }
    public string? PhotoTitle { get; private set; }
    public string? GameTitle { get; private set; }
    public string? GameComment { get; private set; }
    /// <summary>Host path of the watermark image drawn over captures, and its offset.</summary>
    public string? OverlayPath { get; private set; }
    public int OverlayX { get; private set; }
    public int OverlayY { get; private set; }

    [Hle] int sceScreenShotEnable() { Enabled = true; return 0; }
    [Hle] int sceScreenShotDisable() { Enabled = false; return 0; }
    [Hle] int sceScreenShotEnableNotification() { Notification = true; return 0; }
    [Hle] int sceScreenShotDisableNotification() { Notification = false; return 0; }

    /// <summary>Reads a NUL-terminated UTF-32LE string of at most <paramref name="maxChars"/> characters.</summary>
    internal static string? ReadUtf32(uint addr, int maxChars)
    {
        if (addr == 0) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < maxChars; i++)
        {
            uint c = GuestMemory.R32(addr + (uint)i * 4);
            if (c == 0) break;
            if (c <= 0x10FFFF && (c < 0xD800 || c > 0xDFFF)) sb.Append(char.ConvertFromUtf32((int)c));
            else sb.Append('�');
        }
        return sb.ToString();
    }

    /// <summary>Writes <paramref name="s"/> as UTF-32LE with a NUL, truncated to fit <paramref name="maxChars"/> + NUL.</summary>
    internal static void WriteUtf32(uint addr, string? s, int maxChars)
    {
        int n = 0;
        if (s != null)
        {
            for (int i = 0; i < s.Length && n < maxChars; i += char.IsSurrogatePair(s, i) ? 2 : 1, n++)
                GuestMemory.W32(addr + (uint)n * 4, (uint)char.ConvertToUtf32(s, i));
        }
        GuestMemory.W32(addr + (uint)n * 4, 0);
    }

    /// <summary>SceScreenShotParam (0x10): +0 photoTitle, +4 gameTitle, +8 gameComment (UTF-32), +0xC reserved (NULL).
    /// Null string pointers leave the field undefined.</summary>
    [Hle]
    int sceScreenShotSetParam(uint param)
    {
        if (param == 0 || GuestMemory.R32(param + 12) != 0) return ERR_INVALID_ARGUMENT;
        string? photo = ReadUtf32(GuestMemory.R32(param), MaxTitleLen);
        string? game = ReadUtf32(GuestMemory.R32(param + 4), MaxTitleLen);
        string? comment = ReadUtf32(GuestMemory.R32(param + 8), MaxCommentLen);
        lock (_lock) { PhotoTitle = photo; GameTitle = game; GameComment = comment; }
        Log.Debug($"sceScreenShotSetParam(photo='{photo}', game='{game}', comment='{comment}')");
        return 0;
    }

    /// <summary>Inferred counterpart of SetParam: copies the stored strings into the caller's buffers (UTF-32, NUL
    /// terminated, 256/256/512 bytes); an undefined field becomes an empty string.</summary>
    [Hle]
    int sceScreenShotGetParam(uint param)
    {
        if (param == 0) return ERR_INVALID_ARGUMENT;
        string? photo, game, comment;
        lock (_lock) { photo = PhotoTitle; game = GameTitle; comment = GameComment; }
        if (GuestMemory.R32(param) is var p and not 0) WriteUtf32(p, photo, MaxTitleLen - 1);
        if (GuestMemory.R32(param + 4) is var g and not 0) WriteUtf32(g, game, MaxTitleLen - 1);
        if (GuestMemory.R32(param + 8) is var c and not 0) WriteUtf32(c, comment, MaxCommentLen - 1);
        return 0;
    }

    /// <summary>Registers the watermark drawn over captures (normally a PNG in app0:); a null path removes it.</summary>
    [Hle]
    int sceScreenShotSetOverlayImage(uint pathPtr, int offsetX, int offsetY)
    {
        if (pathPtr == 0)
        {
            lock (_lock) { OverlayPath = null; OverlayX = OverlayY = 0; }
            return 0;
        }
        string path = GuestMemory.ReadCString(pathPtr);
        string resolved = Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault()?.Resolve(path) ?? path;
        var host = Emu.Vfs.Resolve(resolved);
        if (host == null || !File.Exists(host))
        {
            Log.Warn($"sceScreenShotSetOverlayImage: '{path}' not found");
            return ERR_FILE_NOT_FOUND;
        }
        lock (_lock) { OverlayPath = host; OverlayX = offsetX; OverlayY = offsetY; }
        Log.Debug($"sceScreenShotSetOverlayImage('{path}', {offsetX}, {offsetY})");
        return 0;
    }

    /// <summary>Programmatic capture to the photo gallery. The prototype is not public (not in the SDK headers, not
    /// implemented by Vita3K): the arguments are logged so a game that calls it reveals them. Nothing is written.</summary>
    [Hle]
    int sceScreenShotCapture(uint a0, uint a1, uint a2, uint a3)
    {
        Log.WarnOnce("screenshot-capture", $"sceScreenShotCapture(0x{a0:X8}, 0x{a1:X8}, 0x{a2:X8}, 0x{a3:X8}): capture not saved (no host capture yet)");
        return 0;
    }
}
