using System.Security.Cryptography;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// taiHEN user API (homebrew ports such as RSDKv5 / Sonic Mania use it through vitasdk's libdl: dlsym hashes the
/// symbol name with sceSblDmac5HashTransform, then walks the module's export table found with taiGetModuleInfo).
/// Hooks and injections are not supported.
/// </summary>
public sealed class Taihen : HleModule
{
    const int TAI_ERROR_NOT_FOUND = unchecked((int)0x90010002);
    const int TAI_ERROR_INVALID_ARGS = unchecked((int)0x90010003);
    const int TAI_ERROR_NOT_IMPLEMENTED = unchecked((int)0x90010007);
    const uint TAI_ANY_LIBRARY = 0xFFFFFFFF;

    /// <summary>A module by name; a null name (TAI_MAIN_MODULE) is the main executable.</summary>
    LoadedModule? FindModule(uint namePtr)
    {
        if (namePtr == 0) return Emu.MainModule;
        string name = GuestMemory.ReadCString(namePtr);
        return K.Modules.Values.OrderBy(m => m.Uid).FirstOrDefault(m => m.Name == name);
    }

    /// <summary>taiGetModuleInfo(module, tai_module_info_t*): size, modid, module_nid, name[27], exports/imports ranges (0x38 bytes).</summary>
    [Hle]
    int taiGetModuleInfo(uint module, uint info)
    {
        if (info == 0 || GuestMemory.R32(info) < 0x38) return TAI_ERROR_INVALID_ARGS;
        var m = FindModule(module);
        if (m == null) return TAI_ERROR_NOT_FOUND;
        GuestMemory.Span(info + 4, 0x34).Clear();
        GuestMemory.W32(info + 0x04, (uint)m.Uid);
        GuestMemory.W32(info + 0x08, m.ModuleNid);
        GuestMemory.WriteCString(info + 0x0C, m.Name, 27);
        GuestMemory.W32(info + 0x28, m.ExportsStart);
        GuestMemory.W32(info + 0x2C, m.ExportsEnd);
        GuestMemory.W32(info + 0x30, m.ImportsStart);
        GuestMemory.W32(info + 0x34, m.ImportsEnd);
        return 0;
    }

    /// <summary>taiGetModuleExportFunc(module, libnid, funcnid, uintptr_t *func).</summary>
    [Hle]
    int taiGetModuleExportFunc(uint module, uint libNid, uint funcNid, Ptr<uint> func)
    {
        var m = FindModule(module);
        if (m == null) return TAI_ERROR_NOT_FOUND;
        for (uint p = m.ExportsStart; p < m.ExportsEnd;)
        {
            ushort size = GuestMemory.Read<ushort>(p);
            ushort nfunc = GuestMemory.Read<ushort>(p + 6);
            uint lib = GuestMemory.R32(p + 0x10), nids = GuestMemory.R32(p + 0x18), entries = GuestMemory.R32(p + 0x1C);
            if (libNid == TAI_ANY_LIBRARY || libNid == lib)
                for (uint i = 0; i < nfunc; i++)
                    if (GuestMemory.R32(nids + i * 4) == funcNid)
                    {
                        func.TrySet(GuestMemory.R32(entries + i * 4));
                        return 0;
                    }
            if (size == 0) break;
            p += size;
        }
        return TAI_ERROR_NOT_FOUND;
    }

    [Hle] int taiHookFunctionExportForUser(uint hook, uint args) => TAI_ERROR_NOT_IMPLEMENTED;
    [Hle] int taiHookFunctionImportForUser(uint hook, uint args) => TAI_ERROR_NOT_IMPLEMENTED;
    [Hle] int taiHookFunctionOffsetForUser(uint hook, uint args) => TAI_ERROR_NOT_IMPLEMENTED;
    [Hle] int taiInjectDataForUser(uint args) => TAI_ERROR_NOT_IMPLEMENTED;
    [Hle] int taiInjectAbs(uint dest, uint src, uint size) => TAI_ERROR_NOT_IMPLEMENTED;
    [Hle] int taiHookRelease(int uid, uint hook) => TAI_ERROR_NOT_FOUND;
    [Hle] int taiInjectRelease(int uid) => TAI_ERROR_NOT_FOUND;
}

/// <summary>SceSblDmac5Mgr: the DMAC5 crypto engine (hash commands) and SceLibRng.</summary>
public sealed class Security : HleModule
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

    /// <summary>sceKernelGetRandomNumber(void *output, SceSize size): at most 64 bytes per call.</summary>
    [Hle]
    int sceKernelGetRandomNumber(uint output, uint size)
    {
        if (size > 0x40) return SceError.ILLEGAL_SIZE;
        if (output == 0) return SceError.ILLEGAL_ADDR;
        RandomNumberGenerator.Fill(GuestMemory.Span(output, (int)size));
        return 0;
    }
}
