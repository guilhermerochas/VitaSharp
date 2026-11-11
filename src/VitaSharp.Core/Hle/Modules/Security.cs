using System.Security.Cryptography;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceSblDmac5Mgr: the DMAC5 crypto engine (hash commands) and SceLibRng.</summary>
public sealed partial class Security : HleModule
{
    const int SCE_SBL_ERROR_INVALID_ARGUMENT = unchecked((int)0x800F0016);

    /// <summary>
    /// sceSblDmac5HashTransform(SceSblDmac5HashTransformParam *param, command, flags);
    /// param = { src, dst, length, key, keysize, iv }. Command 0x3 = SHA-1, 0x13 = SHA-256, 0x23 = HMAC-SHA1,
    /// 0x33 = HMAC-SHA256 (the HMAC key is taken from param when given).
    /// </summary>
    [Hle]
    int sceSblDmac5HashTransform(uint param, uint command, uint flags)
    {
        if (param == 0) return SCE_SBL_ERROR_INVALID_ARGUMENT;
        uint src = GuestMemory.R32(param), dst = GuestMemory.R32(param + 4), length = GuestMemory.R32(param + 8);
        uint key = GuestMemory.R32(param + 12), keySize = GuestMemory.R32(param + 16);
        if (dst == 0 || (src == 0 && length != 0) || length > 0x10000000) return SCE_SBL_ERROR_INVALID_ARGUMENT;
        var data = length == 0 ? ReadOnlySpan<byte>.Empty : (ReadOnlySpan<byte>)GuestMemory.Span(src, (int)length);
        byte[] digest;
        byte[] Key() => key != 0 ? GuestMemory.Span(key, (int)Math.Min(keySize == 0 ? 32u : keySize, 64u)).ToArray() : [];
        switch (command & 0xFF)
        {
            case 0x03: digest = SHA1.HashData(data); break;
            case 0x13: digest = SHA256.HashData(data); break;
            case 0x23: digest = HMACSHA1.HashData(Key(), data); break;
            case 0x33: digest = HMACSHA256.HashData(Key(), data); break;
            default:
                Log.WarnOnce($"dmac5cmd{command:X}", $"sceSblDmac5HashTransform: unsupported command 0x{command:X}");
                return SCE_SBL_ERROR_INVALID_ARGUMENT;
        }
        digest.CopyTo(GuestMemory.Span(dst, digest.Length));
        return 0;
    }

    /// <summary>SCE_RNG_ERROR_INVALID_ARGUMENT (value used by Vita3K; unconfirmed on hardware).</summary>
    internal const int SCE_RNG_ERROR_INVALID_ARGUMENT = unchecked((int)0x810C0000);

    /// <summary>sceKernelGetRandomNumber(void *output, SceSize size): at most 64 bytes per call.</summary>
    [Hle("sceKernelGetRandomNumber")]
    [Hle("_sceKernelGetRandomNumber")] // the SceSblRng syscall behind it (Vita3K)
    int sceKernelGetRandomNumber(uint output, uint size)
    {
        if (size > 0x40) return SCE_RNG_ERROR_INVALID_ARGUMENT;
        if (size == 0) return 0;
        if (output == 0) return SCE_RNG_ERROR_INVALID_ARGUMENT;
        var dst = GuestMemory.Span(output, (int)size);
        // session capture: a re-run hands back the recorded numbers; a recording logs them (one static read when off)
        if (VitaSharp.Core.Capture.CaptureHooks.Random is not { } hook || !hook.Replay(dst)) RandomNumberGenerator.Fill(dst);
        VitaSharp.Core.Capture.CaptureHooks.Random?.Recorded(dst);
        return 0;
    }
}
