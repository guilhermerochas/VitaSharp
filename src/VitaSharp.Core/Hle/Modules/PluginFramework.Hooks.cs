using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Function hooks of the plugin framework libraries (emulator option "Enable Kernel Plugins"). The hooked function's
/// first instructions are replaced by a jump to a per-address thunk that jumps to the first hook; each hook gets the
/// framework's 12-byte reference record (next, func, old) its TAI_CONTINUE reads: <c>next</c> is the next hook's record
/// (0 for the last), <c>old</c> a trampoline that runs the replaced instructions and goes on in the original function.
/// A new hook on an address already hooked goes right after the first one, as the framework links them. A hook on a
/// firmware export that the HLE serves (an SVC stub) also takes the user programs' import stubs of that function, the
/// bound ones and those bound later, since on the Vita every caller goes through the hooked code.
/// </summary>
public sealed partial class PluginFramework
{
    const int ErrorInvalidKernelAddr = unchecked((int)0x90010004);
    const int ErrorHookError = unchecked((int)0x90010006);

    /// <summary>ARM: movw r12; movt r12; bx r12 (the bytes a hooked ARM function loses).</summary>
    const int ArmJumpSize = 12;

    /// <summary>Thumb: movw r12; movt r12; bx r12 (the bytes a hooked Thumb function loses, at least).</summary>
    const int ThumbJumpSize = 10;

    /// <summary>The hooks on one address of one process.</summary>
    internal sealed class HookPatch : KernelObject
    {
        public int Pid;
        public uint Address;
        public bool Thumb;
        public byte[] Saved = [];
        public uint Thunk;
        public uint Trampoline;
        /// <summary>The records of the hooks, the one the thunk jumps to first.</summary>
        public List<uint> Records = new();
        /// <summary>The HLE function whose import stubs were taken (0: none).</summary>
        public uint RedirectedNid;
        /// <summary>The library of <see cref="RedirectedNid"/> (its export entry's library).</summary>
        public uint RedirectedLibNid;

        /// <summary>Save states: the record of the kernel object table (<see cref="SaveState.KernelSnapshot"/>).</summary>
        internal void Save(SaveState.SnapshotWriter writer)
        {
            writer.Write(Pid);
            writer.Write(Address);
            writer.Write(Thumb);
            writer.Write(Saved);
            writer.Write(Thunk);
            writer.Write(Trampoline);
            writer.Write(Records.Count);
            foreach (uint record in Records)
            {
                writer.Write(record);
            }

            writer.Write(RedirectedNid);
            writer.Write(RedirectedLibNid);
        }

        internal static HookPatch Load(SaveState.SnapshotReader reader, string name)
        {
            var patch = new HookPatch
            {
                Name = name,
                Pid = reader.ReadInt32(),
                Address = reader.ReadUInt32(),
                Thumb = reader.ReadBool(),
                Saved = reader.ReadBytes() ?? [],
                Thunk = reader.ReadUInt32(),
                Trampoline = reader.ReadUInt32(),
            };
            int records = reader.ReadInt32();
            for (int i = 0; i < records; i++)
            {
                patch.Records.Add(reader.ReadUInt32());
            }

            patch.RedirectedNid = reader.ReadUInt32();
            // a state saved before the library was kept: 0, found again from the hooked entry after the load
            patch.RedirectedLibNid = reader.AtEnd ? 0 : reader.ReadUInt32();
            return patch;
        }
    }

    readonly List<HookPatch> _hooks = new();

    uint KernelAllocation(uint size, string name)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return Mem.Alloc(size, name, MemoryRegion.Kernel, 16);
        }
    }

    static void WriteThumbJump(uint at, uint target)
    {
        // movw r12, #lo16 ; movt r12, #hi16 ; bx r12
        WriteThumbMove(at, 0xF240, target & 0xFFFF);
        WriteThumbMove(at + 4, 0xF2C0, target >> 16);
        GuestMemory.Write(at + 8, (ushort)0x4760);
    }

    static void WriteThumbMove(uint at, ushort opcode, uint value)
    {
        uint imm4 = (value >> 12) & 0xF;
        uint i = (value >> 11) & 1;
        uint imm3 = (value >> 8) & 7;
        uint imm8 = value & 0xFF;
        GuestMemory.Write(at, (ushort)(opcode | (i << 10) | imm4));
        GuestMemory.Write(at + 2, (ushort)((imm3 << 12) | (12u << 8) | imm8));
    }

    static bool IsHleStub(uint address)
    {
        return (GuestMemory.R32(address) & 0xFF000000) == 0xEF000000 && GuestMemory.R32(address + 4) == 0xE12FFF1E;
    }

    static bool IsJumpStub(uint address)
    {
        return (GuestMemory.R32(address) & 0xFFF0F000) == 0xE300C000 && (GuestMemory.R32(address + 4) & 0xFFF0F000) == 0xE340C000
            && GuestMemory.R32(address + 8) == 0xE12FFF1C;
    }

    /// <summary>The length of the Thumb instruction at <paramref name="address"/> (2 or 4), and whether it can run from
    /// another address unchanged (no PC-relative operand, no branch, no IT block).</summary>
    static int ThumbInstruction(uint address, out bool movable)
    {
        ushort first = GuestMemory.Read<ushort>(address);
        if ((first & 0xE000) == 0xE000 && (first & 0x1800) != 0)
        {
            ushort second = GuestMemory.Read<ushort>(address + 2);
            bool branch = (first & 0xF800) == 0xF000 && (second & 0x8000) != 0;
            bool pcBase = (first & 0x000F) == 0xF && ((first & 0xFE00) == 0xF800 || (first & 0xFA00) == 0xF200);
            movable = !branch && !pcBase;
            return 4;
        }

        bool literal = (first & 0xF800) == 0x4800;
        bool adr = (first & 0xF800) == 0xA000;
        bool conditionalBranch = (first & 0xF000) == 0xD000 && (first & 0x0F00) < 0x0E00;
        bool branch16 = (first & 0xF800) == 0xE000;
        bool compareBranch = (first & 0xF500) == 0xB100;
        bool it = (first & 0xFF00) == 0xBF00 && (first & 0x000F) != 0;
        bool highPc = (first & 0xFC00) == 0x4400 && ((first & 0x0078) == 0x0078 || ((first & 0x0007) | ((first & 0x0080) >> 4)) == 0xF);
        movable = !literal && !adr && !conditionalBranch && !branch16 && !compareBranch && !it && !highPc;
        return 2;
    }

    /// <summary>An ARM instruction that can run from another address unchanged: no branch and no PC operand.</summary>
    static bool ArmMovable(uint instruction)
    {
        if ((instruction & 0x0E000000) == 0x0A000000)
        {
            return false;
        }

        return ((instruction >> 16) & 0xF) != 0xF && ((instruction >> 12) & 0xF) != 0xF && (instruction & 0xF) != 0xF;
    }

    /// <summary>Builds the trampoline that runs the instructions the jump replaces and then the rest of the function;
    /// returns its address (with the Thumb bit for Thumb code), or 0 when an instruction cannot be moved.</summary>
    uint BuildTrampoline(uint address, bool thumb, out int replaced)
    {
        replaced = 0;
        if (!thumb && (IsHleStub(address) || IsJumpStub(address)))
        {
            // an import stub or a firmware export entry: position independent, it returns itself
            replaced = ArmJumpSize;
            uint stub = KernelAllocation(ArmJumpSize, "plugin hook trampoline");
            GuestMemory.Copy(stub, address, ArmJumpSize);
            return stub;
        }

        if (thumb)
        {
            while (replaced < ThumbJumpSize)
            {
                ThumbInstruction(address + (uint)replaced, out bool movable);
                if (!movable)
                {
                    return 0;
                }

                replaced += ThumbInstruction(address + (uint)replaced, out _);
            }

            uint trampoline = KernelAllocation((uint)replaced + ThumbJumpSize + 2, "plugin hook trampoline");
            GuestMemory.Copy(trampoline, address, (uint)replaced);
            WriteThumbJump(trampoline + (uint)replaced, (address + (uint)replaced) | 1);
            return trampoline | 1;
        }

        for (int i = 0; i < ArmJumpSize; i += 4)
        {
            if (!ArmMovable(GuestMemory.R32(address + (uint)i)))
            {
                return 0;
            }
        }

        replaced = ArmJumpSize;
        uint armTrampoline = KernelAllocation(ArmJumpSize * 2, "plugin hook trampoline");
        GuestMemory.Copy(armTrampoline, address, ArmJumpSize);
        Linker.WriteJumpStub(armTrampoline + ArmJumpSize, address + ArmJumpSize);
        return armTrampoline;
    }

    /// <summary>Points the thunk at the first hook of the chain.</summary>
    void AimThunk(HookPatch patch)
    {
        Linker.WriteJumpStub(patch.Thunk, GuestMemory.R32(patch.Records[0] + 4));
        InvalidateEverywhere(patch.Thunk, ArmJumpSize);
    }

    /// <summary>Hooks the function at <paramref name="destination"/> (bit 0: Thumb) of process <paramref name="pid"/>
    /// with <paramref name="hookFunction"/>; writes the hook's reference to <paramref name="hookRefOut"/>. Returns the
    /// hook's UID or an error.</summary>
    int HookAbsolute(int pid, uint hookRefOut, uint destination, uint hookFunction, uint redirectLibNid, uint redirectNid)
    {
        if (hookFunction >= 0xE0000000)
        {
            return pid == KernelSpace.KernelPid ? ErrorInvalidKernelAddr : unchecked((int)0x90010007);
        }

        using ProcessScope.Scope space = EnterPatchSpace(pid);
        uint address = destination & ~1u;
        bool thumb = (destination & 1) != 0;
        if (!KernelSpace.IsMappedRange(address, ArmJumpSize))
        {
            return ErrorHookError;
        }

        lock (_patchLock)
        {
            HookPatch? patch = _hooks.FirstOrDefault(h => h.Pid == pid && h.Address == address);
            if (patch == null)
            {
                lock (_patchLock)
                {
                    if (_injections.Any(i => i.Pid == pid && address < i.Address + (uint)i.Saved.Length && i.Address < address + ArmJumpSize))
                    {
                        return ErrorPatchExists;
                    }
                }

                uint trampoline = BuildTrampoline(address, thumb, out int replaced);
                if (trampoline == 0)
                {
                    return ErrorHookError;
                }

                patch = new HookPatch
                {
                    Pid = pid,
                    Address = address,
                    Thumb = thumb,
                    Saved = GuestMemory.ReadSpan(address, replaced).ToArray(),
                    Trampoline = trampoline,
                    Thunk = KernelAllocation(ArmJumpSize, "plugin hook thunk"),
                    Name = "plugin hook",
                };
                K.Objects.Register(patch);
                _hooks.Add(patch);
            }

            uint record = KernelAllocation(12, "plugin hook reference");
            GuestMemory.W32(record + 4, hookFunction);
            GuestMemory.W32(record + 8, patch.Trampoline);
            if (patch.Records.Count == 0)
            {
                GuestMemory.W32(record, 0);
                patch.Records.Add(record);
                AimThunk(patch);
                Install(patch, redirectLibNid, redirectNid);
            }
            else
            {
                // the framework links a new hook right after the first one (its published source): the first hook
                // stays the one the function jumps to, the newest runs second, the older ones after it
                uint head = patch.Records[0];
                GuestMemory.W32(record, GuestMemory.R32(head));
                GuestMemory.W32(head, record);
                patch.Records.Insert(1, record);
            }

            GuestMemory.W32(hookRefOut, record);
            string what = redirectNid != 0 ? $" ({NidDatabase.NameOf(redirectNid)} of library 0x{redirectLibNid:X8}, its import stubs too)" : "";
            Util.Log.Info($"plugin hook {patch.Records.Count} on 0x{address:X8}{what}: hook function 0x{hookFunction:X8} (pid 0x{pid:X})");
            return patch.Uid;
        }
    }

    /// <summary>Writes the jump to the thunk over the hooked function and takes the import stubs of the HLE function
    /// <paramref name="redirectNid"/> of library <paramref name="redirectLibNid"/> (0: none).</summary>
    void Install(HookPatch patch, uint redirectLibNid, uint redirectNid)
    {
        if (patch.Thumb)
        {
            WriteThumbJump(patch.Address, patch.Thunk);
        }
        else
        {
            Linker.WriteJumpStub(patch.Address, patch.Thunk);
        }

        InvalidateEverywhere(patch.Address, (uint)patch.Saved.Length);
        if (redirectNid == 0)
        {
            return;
        }

        patch.RedirectedNid = redirectNid;
        patch.RedirectedLibNid = redirectLibNid;
        TakeImportStubs(patch);
    }

    /// <summary>The processes whose import stubs a hook on an HLE function takes: for a kernel hook (a firmware kernel
    /// module's export) every process and the kernel; for a user hook (a user shell's export) that process alone, as
    /// on the Vita a user module's code is its process's.</summary>
    IEnumerable<Emulator> RedirectedProcesses(int pid)
    {
        if (pid != KernelSpace.KernelPid)
        {
            return KernelSpace.ProcessOf(Emu, pid) is { } process ? [process] : [];
        }

        return Emu.Vita.ProcessesAndKernel.Append(Emu).Distinct();
    }

    /// <summary>Import stubs of the HLE function bind to <paramref name="target"/> (0: to the function again) in
    /// <paramref name="process"/>: a kernel hook's redirect is the system's, a user hook's the process's own.</summary>
    static void SetRedirect(Emulator process, int pid, uint libNid, uint nid, uint target)
    {
        if (pid == KernelSpace.KernelPid)
        {
            if (target != 0)
            {
                process.Hle.Redirect(libNid, nid, target);
            }
            else
            {
                process.Hle.ClearRedirect(libNid, nid);
            }
        }
        else if (target != 0)
        {
            process.Hle.RedirectInProcess(libNid, nid, target);
        }
        else
        {
            process.Hle.ClearRedirectInProcess(libNid, nid);
        }
    }

    /// <summary>Points every import stub of the HLE function <paramref name="patch"/> redirects (that library's function:
    /// a kernel twin with the same NID in another library is another function), bound now or later, in the processes
    /// the hook holds for (<see cref="RedirectedProcesses"/>; for a kernel hook those made after it too), at the patch's
    /// thunk (each stub written in its own process's address space).</summary>
    void TakeImportStubs(HookPatch patch)
    {
        bool userHook = patch.Pid != KernelSpace.KernelPid;
        foreach (Emulator process in RedirectedProcesses(patch.Pid))
        {
            SetRedirect(process, patch.Pid, patch.RedirectedLibNid, patch.RedirectedNid, patch.Thunk);
            using (ProcessScope.Enter(process))
            {
                foreach (bool kernel in userHook ? new[] { false } : new[] { false, true })
                {
                    foreach (uint stub in process.Linker.BoundStubs(patch.RedirectedNid, patch.RedirectedLibNid, kernel))
                    {
                        Linker.WriteJumpStub(stub, patch.Thunk);
                        process.Code.Invalidate(stub, ArmJumpSize);
                    }
                }
            }
        }
    }

    /// <summary>Puts the hooked function and the taken import stubs back as they were.</summary>
    void Uninstall(HookPatch patch)
    {
        patch.Saved.CopyTo(GuestMemory.Span(patch.Address, patch.Saved.Length));
        InvalidateEverywhere(patch.Address, (uint)patch.Saved.Length);
        if (patch.RedirectedNid == 0)
        {
            return;
        }

        foreach (Emulator process in RedirectedProcesses(patch.Pid))
        {
            SetRedirect(process, patch.Pid, patch.RedirectedLibNid, patch.RedirectedNid, 0);
        }

        // every import stub of the function, taken at the hook or bound while it was in place, binds as it would now
        foreach (Emulator process in RedirectedProcesses(patch.Pid))
        {
            using (ProcessScope.Enter(process))
            {
                process.Linker.RebindFunction(patch.RedirectedNid, patch.RedirectedLibNid);
            }
        }
    }

    /// <summary>Removes the hook whose reference is <paramref name="hookRef"/> from patch <paramref name="uid"/>; the
    /// last one out puts the function back.</summary>
    int ReleaseHook(int uid, uint hookRef)
    {
        lock (_patchLock)
        {
            if (K.Objects.Get<HookPatch>(uid) is not { } patch)
            {
                return SceError.UNKNOWN_UID;
            }

            using ProcessScope.Scope space = EnterPatchSpace(patch.Pid);
            int index = patch.Records.IndexOf(hookRef);
            if (index < 0)
            {
                return ErrorNotFound;
            }

            if (index > 0)
            {
                GuestMemory.W32(patch.Records[index - 1], GuestMemory.R32(hookRef));
            }

            patch.Records.RemoveAt(index);
            if (patch.Records.Count == 0)
            {
                Uninstall(patch);
                _hooks.Remove(patch);
                K.Objects.Remove(uid);
            }
            else if (index == 0)
            {
                AimThunk(patch);
            }

            return Success;
        }
    }

    /// <summary>
    /// Process <paramref name="pid"/> ended: its hooks and data injections go with it, as the framework drops every patch
    /// of a process when the process unloads (its published source, from its hook on the process unload);
    /// the code they patched went with the process, so nothing is written back. The hooks' thunks, trampolines and
    /// reference records are freed (no thread of the process runs them any more).
    /// </summary>
    internal void ForgetProcess(int pid)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return;
        }

        if (pid == Emu.Pid)
        {
            ForgetShells();
        }

        lock (_patchLock)
        {
            for (int i = _hooks.Count - 1; i >= 0; i--)
            {
                HookPatch patch = _hooks[i];
                if (patch.Pid != pid)
                {
                    continue;
                }

                _hooks.RemoveAt(i);
                K.Objects.Remove(patch.Uid);
                FreeKernelAllocation(patch.Thunk);
                FreeKernelAllocation(patch.Trampoline & ~1u);
                foreach (uint record in patch.Records)
                {
                    FreeKernelAllocation(record);
                }
            }

            for (int i = _injections.Count - 1; i >= 0; i--)
            {
                if (_injections[i].Pid == pid)
                {
                    K.Objects.Remove(_injections[i].Uid);
                    _injections.RemoveAt(i);
                }
            }
        }
    }

    void FreeKernelAllocation(uint address)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            Mem.Free(address);
        }
    }

    /// <summary>The number of hooked addresses and data injections this framework keeps for process
    /// <paramref name="pid"/> (tests).</summary>
    internal (int Hooks, int Injections) PatchesOf(int pid)
    {
        lock (_patchLock)
        {
            return (_hooks.Count(h => h.Pid == pid), _injections.Count(i => i.Pid == pid));
        }
    }

    /// <summary>The HLE function a firmware export entry serves (its SVC stub's NID), or 0.</summary>
    static uint HleNidOf(uint address)
    {
        return (address & 1) == 0 && IsHleStub(address) ? GuestMemory.R32(address + 8) : 0;
    }

    int HookExport(int pid, uint hookRefOut, uint namePtr, uint libraryNid, uint functionNid, uint hookFunction)
    {
        int result = ExportFunction(pid, namePtr, libraryNid, functionNid, out uint function, out uint exportLibNid);
        if (result != Success)
        {
            return result;
        }

        // an HLE stub: a firmware kernel module's entry (every process's calls) or a user shell's (that process's calls)
        using ProcessScope.Scope space = EnterPatchSpace(pid);
        uint redirect = HleNidOf(function);
        return HookAbsolute(pid, hookRefOut, function, hookFunction, redirect != 0 ? exportLibNid : 0, redirect);
    }

    int HookImport(int pid, uint hookRefOut, uint namePtr, uint libraryNid, uint functionNid, uint hookFunction)
    {
        string? name = namePtr == 0 ? null : GuestMemory.ReadCString(namePtr, MaxNameLength);
        uint stub = 0;
        int result = ImportStubOf(pid, name, libraryNid, functionNid, ref stub);
        if (result != Success)
        {
            return result;
        }

        return HookAbsolute(pid, hookRefOut, stub, hookFunction, 0, 0);
    }

    int HookOffset(int pid, uint hookRefOut, int moduleId, int segment, uint offset, bool thumb, uint hookFunction)
    {
        int result = SegmentOffset(pid, moduleId, segment, offset, out uint address);
        if (result != Success)
        {
            return result;
        }

        return HookAbsolute(pid, hookRefOut, thumb ? address | 1 : address, hookFunction, 0, 0);
    }

    /// <summary>Kernel: hooks the function at an absolute address of process <paramref name="pid"/>.</summary>
    [Hle(Nid = 0xD6E4F68F, ReturnsValue = true)]
    int HookFunctionAbsForKernel(int pid, uint hookRefOut, uint destination, uint hookFunction)
    {
        if (!Enabled)
        {
            return 0;
        }

        return HookAbsolute(pid, hookRefOut, destination, hookFunction, 0, 0);
    }

    /// <summary>Kernel: hooks a module's export (pid, hook ref, module name, library NID, function NID, hook).</summary>
    [Hle(Nid = 0x45CCFFC0, ReturnsValue = true)]
    int HookFunctionExportForKernel(int pid, uint hookRefOut, uint namePtr, uint libraryNid, uint functionNid, uint hookFunction)
    {
        if (!Enabled)
        {
            return 0;
        }

        return HookExport(pid, hookRefOut, namePtr, libraryNid, functionNid, hookFunction);
    }

    /// <summary>Kernel: hooks a module's import stub (pid, hook ref, module name, imported library NID, function NID,
    /// hook).</summary>
    [Hle(Nid = 0x4519D882, ReturnsValue = true)]
    int HookFunctionImportForKernel(int pid, uint hookRefOut, uint namePtr, uint libraryNid, uint functionNid, uint hookFunction)
    {
        if (!Enabled)
        {
            return 0;
        }

        return HookImport(pid, hookRefOut, namePtr, libraryNid, functionNid, hookFunction);
    }

    /// <summary>Kernel: hooks the code at an offset of a module's segment (pid, hook ref, modid, segment, offset, thumb,
    /// hook).</summary>
    [Hle(Nid = 0x8F319D77, ReturnsValue = true)]
    int HookFunctionOffsetForKernel(int pid, uint hookRefOut, int moduleId, int segment, uint offset, int thumb, uint hookFunction)
    {
        if (!Enabled)
        {
            return 0;
        }

        return HookOffset(pid, hookRefOut, moduleId, segment, offset, thumb != 0, hookFunction);
    }

    /// <summary>Kernel: removes a hook.</summary>
    [Hle(Nid = 0xD22678CA, ReturnsValue = true)]
    int HookReleaseForKernel(int uid, uint hookRef)
    {
        if (!Enabled)
        {
            return 0;
        }

        return ReleaseHook(uid, hookRef);
    }

    /// <summary>User: hooks an export of a module of the calling process (tai_hook_args: size 0x14, module, library
    /// NID, function NID, hook).</summary>
    [Hle(Nid = 0x8746A1CE, ReturnsValue = true)]
    int HookFunctionExportForUser(uint hookRefOut, uint args)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!KernelSpace.IsUserRange(args, 0x14) || GuestMemory.R32(args) != 0x14)
        {
            return ErrorUserMemory;
        }

        return HookExport(Emu.Pid, hookRefOut, GuestMemory.R32(args + 4), GuestMemory.R32(args + 8), GuestMemory.R32(args + 0xC),
            GuestMemory.R32(args + 0x10));
    }

    /// <summary>User: hooks an import stub of a module of the calling process.</summary>
    [Hle(Nid = 0x53636E5D, ReturnsValue = true)]
    int HookFunctionImportForUser(uint hookRefOut, uint args)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!KernelSpace.IsUserRange(args, 0x14) || GuestMemory.R32(args) != 0x14)
        {
            return ErrorUserMemory;
        }

        return HookImport(Emu.Pid, hookRefOut, GuestMemory.R32(args + 4), GuestMemory.R32(args + 8), GuestMemory.R32(args + 0xC),
            GuestMemory.R32(args + 0x10));
    }

    /// <summary>User: hooks the code at a module offset (tai_offset_args: size 0x1C, modid, segment, offset, thumb,
    /// hook).</summary>
    [Hle(Nid = 0x0535225A, ReturnsValue = true)]
    int HookFunctionOffsetForUser(uint hookRefOut, uint args)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!KernelSpace.IsUserRange(args, 0x1C) || GuestMemory.R32(args) != 0x1C)
        {
            return ErrorUserMemory;
        }

        return HookOffset(Emu.Pid, hookRefOut, (int)GuestMemory.R32(args + 4), (int)GuestMemory.R32(args + 8), GuestMemory.R32(args + 0xC),
            GuestMemory.R32(args + 0x10) != 0, GuestMemory.R32(args + 0x14));
    }

    /// <summary>User: removes a hook.</summary>
    [Hle(Nid = 0x1C30E799, ReturnsValue = true)]
    int HookReleaseForUser(int uid, uint hookRef)
    {
        if (!Enabled)
        {
            return 0;
        }

        return ReleaseHook(uid, hookRef);
    }
}
