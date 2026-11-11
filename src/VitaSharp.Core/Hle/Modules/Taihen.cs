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
public sealed partial class Taihen : HleModule
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
        return K.Modules.Values.Where(m => !m.IsKernel).OrderBy(m => m.Uid).FirstOrDefault(m => m.Name == name);

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

    /// <summary>The function a module exports (libNid TAI_ANY_LIBRARY: any of its libraries), 0 when it does not.</summary>
    static uint ExportOf(LoadedModule m, uint libNid, uint funcNid)
    {
        for (uint p = m.ExportsStart; p < m.ExportsEnd;)
        {
            ushort size = GuestMemory.Read<ushort>(p);
            ushort nfunc = GuestMemory.Read<ushort>(p + 6);
            uint lib = GuestMemory.R32(p + 0x10), nids = GuestMemory.R32(p + 0x18), entries = GuestMemory.R32(p + 0x1C);
            if (libNid == TAI_ANY_LIBRARY || libNid == lib)
                for (uint i = 0; i < nfunc; i++)
                    if (GuestMemory.R32(nids + i * 4) == funcNid) return GuestMemory.R32(entries + i * 4);
            if (size == 0) break;
            p += size;
        }
        return 0;
    }

    /// <summary>The struct _tai_hook_user { next, func, old } that *p_hook points to: TAI_CONTINUE calls <c>old</c>.</summary>
    uint HookUser(uint func, uint old)
    {
        uint user = Emu.Memory.Alloc(12, "taihen hook ref");
        GuestMemory.W32(user, 0);
        GuestMemory.W32(user + 4, func);
        GuestMemory.W32(user + 8, old);
        return user;
    }

    /// <summary>
    /// Hooks import stubs: each jumps to <paramref name="func"/>, and the original is reached through a trampoline
    /// holding a copy of the first stub (an SVC stub or a jump stub: both run from anywhere). The stubs' previous
    /// contents are kept for taiHookRelease.
    /// </summary>
    int HookStubs(uint hook, List<uint> stubs, uint func)
    {
        uint tramp = Emu.Memory.Alloc(12, "taihen import trampoline");
        GuestMemory.Span(stubs[0], 12).CopyTo(GuestMemory.Span(tramp, 12));
        Emu.Code.Invalidate(tramp, 12);
        var saved = new List<(uint, byte[])>();
        foreach (uint stub in stubs)
        {
            saved.Add((stub, GuestMemory.Span(stub, 12).ToArray()));
            Linker.WriteJumpStub(stub, func);
            Emu.Code.Invalidate(stub, 12);
        }
        GuestMemory.W32(hook, HookUser(func, tramp));
        lock (_injections)
        {
            int uid = _nextInjection++;
            _stubHooks[uid] = saved;
            return uid;
        }
    }

    // import/export hooks done on stubs: uid -> (stub, its 12 bytes before)
    readonly Dictionary<int, List<(uint Stub, byte[] Original)>> _stubHooks = new();

    /// <summary>tai_hook_args_t { size, module, library_nid, func_nid, hook_func } (0x14 bytes).</summary>
    (LoadedModule? Module, string Name, uint LibNid, uint FuncNid, uint Func)? HookArgs(uint args)
    {
        if (args == 0 || GuestMemory.R32(args) < 0x14) return null;
        uint name = GuestMemory.R32(args + 4);
        return (FindModule(name), name == 0 ? "(main)" : GuestMemory.ReadCString(name), GuestMemory.R32(args + 8), GuestMemory.R32(args + 12), GuestMemory.R32(args + 16));
    }

    /// <summary>
    /// taiHookFunctionImportForUser(tai_hook_ref_t *p_hook, tai_hook_args_t *args): the module's import of func_nid
    /// (from library_nid, any with TAI_ANY_LIBRARY) calls the hook; TAI_CONTINUE reaches what the import was bound to.
    /// </summary>
    [Hle]
    int taiHookFunctionImportForUser(uint hook, uint args)
    {
        if (hook == 0 || HookArgs(args) is not { } a || a.Func == 0) return TAI_ERROR_INVALID_ARGS;
        if (a.Module is not { } m) return TAI_ERROR_NOT_FOUND;
        var stubs = Emu.Linker.BoundStubs(a.FuncNid, a.LibNid == TAI_ANY_LIBRARY ? null : a.LibNid)
            .Where(s => m.Segments.Values.Any(g => s >= g.Address && s - g.Address < g.Size)).ToList();
        if (stubs.Count == 0)
        {
            Log.Warn($"taiHookFunctionImportForUser: {a.Name} does not import 0x{a.FuncNid:X8} ({NidDatabase.NameOf(a.FuncNid)})");
            return TAI_ERROR_NOT_FOUND;
        }
        return HookStubs(hook, stubs, a.Func);
    }

    /// <summary>
    /// taiHookFunctionExportForUser(tai_hook_ref_t *p_hook, tai_hook_args_t *args): every caller of an exported function
    /// calls the hook. A Thumb function of a loaded module is hooked in place (as taiHookFunctionOffsetForUser); when
    /// that cannot be done, and for the firmware functions VitaSharp implements itself, the import stubs bound to it
    /// are redirected instead (as Vita3K falls back to).
    /// </summary>
    [Hle]
    int taiHookFunctionExportForUser(uint hook, uint args)
    {
        if (hook == 0 || HookArgs(args) is not { } a || a.Func == 0) return TAI_ERROR_INVALID_ARGS;
        uint target = a.Module is { } m ? ExportOf(m, a.LibNid, a.FuncNid) : 0;
        if (target != 0)
        {
            if ((target & 1) != 0)
            {
                int r = InlineHook(hook, target & ~1u, a.Func, a.Name);
                if (r >= 0) return r;
            }
            var bound = Emu.Linker.BoundStubs(a.FuncNid).Where(s => JumpTarget(s) == target).ToList();
            if (bound.Count == 0)
            {
                Log.Warn($"taiHookFunctionExportForUser: 0x{a.FuncNid:X8} of {a.Name} at 0x{target:X8} cannot be hooked in place and no import is bound to it");
                return TAI_ERROR_NOT_IMPLEMENTED;
            }
            return HookStubs(hook, bound, a.Func);
        }
        // a firmware function: the imports bound to the HLE implementation (SVC stubs carrying the NID)
        var hle = Emu.Linker.BoundStubs(a.FuncNid).Where(s => (GuestMemory.R32(s) >> 24) == 0xEF && GuestMemory.R32(s + 8) == a.FuncNid).ToList();
        if (hle.Count == 0 || !Emu.Hle.IsImplemented(a.FuncNid))
        {
            Log.Warn($"taiHookFunctionExportForUser: {a.Name} 0x{a.LibNid:X8}/0x{a.FuncNid:X8} ({NidDatabase.NameOf(a.FuncNid)}) not found");
            return TAI_ERROR_NOT_FOUND;
        }
        return HookStubs(hook, hle, a.Func);
    }

    /// <summary>The target of a movw/movt/bx r12 jump stub, 0 for anything else.</summary>
    static uint JumpTarget(uint stub)
    {
        uint movw = GuestMemory.R32(stub), movt = GuestMemory.R32(stub + 4);
        if ((movw & 0xFFF0F000) != 0xE300C000 || (movt & 0xFFF0F000) != 0xE340C000 || GuestMemory.R32(stub + 8) != 0xE12FFF1C) return 0;
        return ((((movt >> 4) & 0xF000) | (movt & 0xFFF)) << 16) | ((movw >> 4) & 0xF000) | (movw & 0xFFF);
    }
    // hooks: uid -> (patched address, the bytes that were there before)
    readonly Dictionary<int, (uint Address, byte[] Original)> _hooks = new();

    /// <summary>
    /// taiHookFunctionOffsetForUser(tai_hook_ref_t *p_hook, tai_offset_args_t *args): args = { size, modid, segidx,
    /// offset, thumb, source (the hook function), source_size }. The function at the offset jumps to the hook; the
    /// hook reaches the original through TAI_CONTINUE, which calls <c>old</c> of the struct _tai_hook_user
    /// { next, func, old } that *p_hook points to: a trampoline with the original's first instructions (moved out by
    /// the patch) and a jump back to the rest. SceShaccCgExt (vitaGL homebrew) hooks the runtime Cg compiler this way.
    /// Only Thumb functions whose first instructions are not PC-relative are hooked.
    /// </summary>
    [Hle]
    int taiHookFunctionOffsetForUser(uint hook, uint args)
    {
        if (hook == 0 || args == 0 || GuestMemory.R32(args) < 0x1C) return TAI_ERROR_INVALID_ARGS;
        int modid = (int)GuestMemory.R32(args + 4), seg = (int)GuestMemory.R32(args + 8);
        uint offset = GuestMemory.R32(args + 12), thumb = GuestMemory.R32(args + 16), func = GuestMemory.R32(args + 20);
        if (func == 0) return TAI_ERROR_INVALID_ARGS;
        if (!K.Modules.TryGetValue(modid, out var m) || !m.Segments.TryGetValue(seg, out var s) || offset >= s.Size) return TAI_ERROR_NOT_FOUND;
        uint target = (s.Address + offset) & ~1u;
        if (thumb == 0)
        {
            Log.WarnOnce($"taihook-arm-{target:X8}", $"taiHookFunctionOffsetForUser: ARM function 0x{target:X8} of {m.Name} not hooked (Thumb only)");
            return TAI_ERROR_NOT_IMPLEMENTED;
        }
        return InlineHook(hook, target, func, m.Name);
    }

    /// <summary>Hooks the Thumb function at <paramref name="target"/> in place (see taiHookFunctionOffsetForUser).</summary>
    int InlineHook(uint hook, uint target, uint func, string moduleName)
    {
        // whole instructions covering the patch (ldr.w pc, [pc, #imm] + the hook's address: 8 bytes)
        uint patch = 8, moved = 0;
        while (moved < patch)
        {
            ushort hw = GuestMemory.Read<ushort>(target + moved);
            bool wide = (hw >> 11) is 0x1D or 0x1E or 0x1F;
            if (PcRelativeThumb(hw, wide ? GuestMemory.Read<ushort>(target + moved + 2) : (ushort)0))
            {
                Log.WarnOnce($"taihook-pcrel-{target:X8}", $"taihen: 0x{target:X8} of {moduleName} starts with a PC-relative instruction (0x{hw:X4}): not hooked in place");
                return TAI_ERROR_NOT_IMPLEMENTED;
            }
            moved += wide ? 4u : 2u;
        }
        // trampoline: the moved instructions, then a jump to the rest of the original
        uint tramp = Emu.Memory.Alloc(moved + 16, "taihen hook");
        GuestMemory.Span(target, (int)moved).CopyTo(GuestMemory.Span(tramp, (int)moved));
        WriteThumbJump(tramp + moved, (target + moved) | 1);
        uint user = HookUser(func, tramp | 1);
        var original = GuestMemory.Span(target, (int)patch).ToArray();
        WriteThumbJump(target, func | 1);
        Emu.Code.Invalidate(target, patch);
        Emu.Code.Invalidate(tramp, moved + 16);
        GuestMemory.W32(hook, user);
        lock (_injections)
        {
            int uid = _nextInjection++;
            _hooks[uid] = (target, original);
            return uid;
        }
    }

    /// <summary>ldr.w pc, [pc, #imm] and the destination right after it (8 bytes).</summary>
    static void WriteThumbJump(uint at, uint dest)
    {
        // the literal is read from Align(at + 4, 4) + imm and sits right after the 4-byte instruction, at at + 4: imm is
        // 2 when the instruction is not word aligned (the literal was written 2 bytes further, so a hook placed at a
        // halfword address jumped through garbage: Dead Space's SceShaccCgExt trampoline went to 0xE7AF0000)
        uint imm = at % 4 == 0 ? 0u : 2u;
        GuestMemory.Write(at, (ushort)0xF8DF);
        GuestMemory.Write(at + 2, (ushort)(0xF000 | imm));
        GuestMemory.W32(at + 4, dest);
    }

    /// <summary>Thumb instructions that read the PC (they cannot run from the trampoline): branches, literal loads,
    /// ADR, ADD/MOV with the PC.</summary>
    static bool PcRelativeThumb(ushort hw, ushort hw2)
    {
        if ((hw >> 11) is 0x1D or 0x1E or 0x1F)
        {
            if ((hw & 0xF800) == 0xF000 && (hw2 & 0x8000) != 0) return true;           // B.W, BL, BLX, conditional B.W
            if ((hw & 0xFF7F) == 0xF85F || (hw & 0xFF7F) == 0xF81F || (hw & 0xFF7F) == 0xF83F || (hw & 0xFF7F) == 0xF91F || (hw & 0xFF7F) == 0xF93F) return true; // LDR*.W literal
            if ((hw & 0xFE5F) == 0xE85F) return true;                                  // LDRD literal
            if ((hw & 0xFBFF) == 0xF20F || (hw & 0xFBFF) == 0xF2AF) return true;       // ADR.W
            if ((hw & 0xFFF0) == 0xE8D0) return true;                                  // TBB/TBH
            return false;
        }
        if ((hw & 0xF800) == 0x4800) return true;                                       // LDR literal
        if ((hw & 0xF800) == 0xA000) return true;                                       // ADR
        if ((hw & 0xF000) == 0xD000 && (hw & 0x0F00) < 0x0E00) return true;             // B<cond>
        if ((hw & 0xF800) == 0xE000) return true;                                       // B
        if ((hw & 0xF500) == 0xB100) return true;                                       // CBZ/CBNZ
        if ((hw & 0xFF78) == 0x4478 || (hw & 0xFF78) == 0x4678) return true;            // ADD/MOV Rd, PC
        if ((hw & 0xFF00) == 0x4700 && ((hw >> 3) & 0xF) == 15) return true;           // BX/BLX PC
        return false;
    }
    // injections: uid -> (address, the bytes that were there before)
    readonly Dictionary<int, (uint Address, byte[] Original)> _injections = new();
    int _nextInjection = 1;

    /// <summary>
    /// taiInjectDataForUser(tai_offset_args_t *args): args = { size, modid, segidx, offset, thumb, source, source_size } (0x1C; 0x18 without thumb in old headers).
    /// Writes source_size bytes over a loaded module's segment at offset and returns an injection id; the translated code
    /// of that range is dropped. SceShaccCgExt (vitaGL homebrew) patches the runtime Cg compiler (ur0:data/libshacccg.suprx)
    /// this way; without it the homebrew logged "Failed to apply patches" and its shaders did not compile as expected.
    /// </summary>
    [Hle]
    int taiInjectDataForUser(uint args)
    {
        if (args == 0) return TAI_ERROR_INVALID_ARGS;
        int modid = (int)GuestMemory.R32(args + 4);
        int seg = (int)GuestMemory.R32(args + 8);
        // the current taihen.h has a thumb field after offset (size 0x1C); without it the struct is 0x18
        uint field = GuestMemory.R32(args) >= 0x1C ? 4u : 0u;
        uint offset = GuestMemory.R32(args + 12), src = GuestMemory.R32(args + 16 + field), size = GuestMemory.R32(args + 20 + field);
        string desc = $"taiInjectDataForUser(size 0x{GuestMemory.R32(args):X}, modid 0x{modid:X}, seg {seg}, offset 0x{offset:X}, source 0x{src:X8}, {size} bytes)";
        Log.Debug(desc);
        if (GuestMemory.R32(args) < 0x18 || src == 0 || size == 0 || size > 0x100000) { Log.Warn($"{desc}: invalid arguments"); return TAI_ERROR_INVALID_ARGS; }
        if (!K.Modules.TryGetValue(modid, out var m) || !m.Segments.TryGetValue(seg, out var s)) return TAI_ERROR_NOT_FOUND;
        if ((ulong)offset + size > s.Size)
        {
            Log.Warn($"taiInjectDataForUser: {size} bytes at offset 0x{offset:X} of segment {seg} of {m.Name} (0x{s.Size:X} bytes) do not fit");
            return TAI_ERROR_INVALID_ARGS;
        }
        return Inject(s.Address + offset, src, size);
    }

    /// <summary>taiInjectAbs(dest, src, size): the same at an absolute address.</summary>
    [Hle]
    int taiInjectAbs(uint dest, uint src, uint size)
    {
        if (dest == 0 || src == 0 || size == 0 || size > 0x100000) return TAI_ERROR_INVALID_ARGS;
        return Inject(dest, src, size);
    }

    int Inject(uint dest, uint src, uint size) => Inject(dest, GuestMemory.ReadSpan(src, (int)size).ToArray());

    /// <summary>Writes <paramref name="data"/> over <paramref name="dest"/> (in the calling thread's address space),
    /// keeping the bytes that were there for taiInjectRelease; returns the injection id.</summary>
    int Inject(uint dest, byte[] data)
    {
        uint size = (uint)data.Length;
        var original = GuestMemory.Span(dest, (int)size).ToArray();
        data.CopyTo(GuestMemory.Span(dest, (int)size));
        Emu.Code.Invalidate(dest, size);

        lock (_injections)
        {
            int uid = _nextInjection++;
            _injections[uid] = (dest, original);
            return uid;
        }
    }

    /// <summary>taiHookRelease(uid, hook): puts the original instructions back.</summary>
    [Hle]
    int taiHookRelease(int uid, uint hook)
    {
        (uint Address, byte[] Original) h;
        List<(uint Stub, byte[] Original)>? stubs;
        lock (_injections)
        {
            if (_stubHooks.Remove(uid, out stubs)) h = (0, []);
            else if (!_hooks.Remove(uid, out h)) return TAI_ERROR_NOT_FOUND;
        }
        if (stubs != null)
        {
            foreach (var (stub, original) in stubs)
            {
                original.CopyTo(GuestMemory.Span(stub, 12));
                Emu.Code.Invalidate(stub, 12);
            }
            return 0;
        }
        h.Original.CopyTo(GuestMemory.Span(h.Address, h.Original.Length));
        Emu.Code.Invalidate(h.Address, (uint)h.Original.Length);
        return 0;
    }

    // ------------------------------------------------------------------ taihenUnsafe

    ModulemgrKernel KernelModules => Emu.Hle.Modules.OfType<ModulemgrKernel>().First();

    SysmemKernel KernelMemory => Emu.Hle.Modules.OfType<SysmemKernel>().First();

    /// <summary>The args and argp of a tai_module_args_t { size, pid, args, argp, flags } (0 and 0 without one).</summary>
    static (uint Args, uint Argp) ModuleArgs(uint args)
    {
        if (args == 0 || GuestMemory.R32(args) < 0x10)
        {
            return (0, 0);
        }

        return (GuestMemory.R32(args + 8), GuestMemory.R32(args + 12));
    }

    /// <summary>taiLoadKernelModule(path, flags, opt): a kernel module (.skprx) through the kernel module manager, not
    /// started yet.</summary>
    [Hle]
    int taiLoadKernelModule(string path, int flags, uint opt) => KernelModules.ksceKernelLoadModule(path, flags, 0);

    /// <summary>taiStartKernelModuleForUser(modid, tai_module_args_t *args, opt, int *res): module_start of a kernel
    /// module with the args of <paramref name="args"/>; *res is what it returned.</summary>
    [Hle]
    int taiStartKernelModuleForUser(int modid, uint args, uint opt, Ptr<int> res)
    {
        var (a, p) = ModuleArgs(args);
        return KernelModules.ksceKernelStartModule(modid, a, p, 0, 0, res.Address);
    }

    /// <summary>taiLoadStartKernelModuleForUser(path, tai_module_args_t *args): load and start a kernel module.</summary>
    [Hle]
    int taiLoadStartKernelModuleForUser(string path, uint args)
    {
        var (a, p) = ModuleArgs(args);
        return KernelModules.ksceKernelLoadStartModule(path, a, p, 0, 0, 0);
    }

    /// <summary>taiLoadStartModuleForPidForUser(path, tai_module_args_t *args): a user module loaded into process
    /// args->pid (0: the caller) and started with args/argp.</summary>
    [Hle]
    int taiLoadStartModuleForPidForUser(string path, uint args)
    {
        var (a, p) = ModuleArgs(args);
        int pid = args != 0 && GuestMemory.R32(args) >= 8 ? (int)GuestMemory.R32(args + 4) : 0;
        return KernelModules.ksceKernelLoadStartModuleForPid(pid, path, a, p, 0, 0, 0);
    }

    /// <summary>taiStopKernelModuleForUser(modid, tai_module_args_t *args, opt, int *res).</summary>
    [Hle]
    int taiStopKernelModuleForUser(int modid, uint args, uint opt, Ptr<int> res)
    {
        var (a, p) = ModuleArgs(args);
        return KernelModules.ksceKernelStopModule(modid, a, p, 0, 0, res.Address);
    }

    /// <summary>taiUnloadKernelModule(modid, flags, opt).</summary>
    [Hle]
    int taiUnloadKernelModule(int modid, int flags, uint opt) => KernelModules.ksceKernelUnloadModule(modid, flags, 0);

    /// <summary>taiStopUnloadKernelModuleForUser(modid, tai_module_args_t *args, opt, int *res).</summary>
    [Hle]
    int taiStopUnloadKernelModuleForUser(int modid, uint args, uint opt, Ptr<int> res)
    {
        var (a, p) = ModuleArgs(args);
        return KernelModules.ksceKernelStopUnloadModule(modid, a, p, 0, 0, res.Address);
    }

    /// <summary>taiMemcpyUserToKernel(kernel_dst, user_src, len): ksceKernelMemcpyUserToKernel (the user range must be
    /// the caller's user memory).</summary>
    [Hle]
    int taiMemcpyUserToKernel(uint dst, uint src, uint len) => KernelMemory.ksceKernelMemcpyUserToKernel(dst, src, len);

    /// <summary>taiMemcpyKernelToUser(user_dst, kernel_src, len): ksceKernelMemcpyKernelToUser.</summary>
    [Hle]
    int taiMemcpyKernelToUser(uint dst, uint src, uint len) => KernelMemory.ksceKernelMemcpyKernelToUser(dst, src, len);

    /// <summary>taiReloadConfig(): taiHEN re-reads its configuration; VitaSharp reads it on every load, so nothing is
    /// cached to drop.</summary>
    [Hle]
    int taiReloadConfig() => 0;

    /// <summary>taiInjectRelease(uid): puts the original bytes back.</summary>
    [Hle]
    int taiInjectRelease(int uid)
    {
        (uint Address, byte[] Original) inj;
        lock (_injections) if (!_injections.Remove(uid, out inj)) return TAI_ERROR_NOT_FOUND;
        inj.Original.CopyTo(GuestMemory.Span(inj.Address, inj.Original.Length));
        Emu.Code.Invalidate(inj.Address, (uint)inj.Original.Length);
        return 0;
    }
}

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
