using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Hle;

/// <summary>One name an <c>[Hle]</c> method is exported under: the attribute's name (or the method's), its NID when the
/// attribute gives one (0: looked up by name) and the library it restricts the lookup to.</summary>
public readonly record struct HleExportName(string Name, uint Nid, string? Library);

/// <summary>
/// The exported functions an <c>[Hle]</c> method is (the generated <c>XxxThroughEntry</c> wrappers have one each):
/// the NIDs its attributes give, resolved once, the way <see cref="HleRegistry.RegisterModule"/> resolves them.
/// </summary>
public sealed class HleExportRef(params HleExportName[] names)
{
    private (uint Nid, string? Library)[]? _resolved;

    /// <summary>The attributes' names, as written.</summary>
    public IReadOnlyList<HleExportName> Names => names;

    /// <summary>Each export as (NID, library): the library is the attribute's, else the NID database's for a NID found
    /// by name, else null (any library exporting the NID).</summary>
    internal (uint Nid, string? Library)[] Resolved => Volatile.Read(ref _resolved) ?? Resolve();

    private (uint Nid, string? Library)[] Resolve()
    {
        var exports = new List<(uint Nid, string? Library)>();
        foreach (HleExportName name in names)
        {
            if (name.Nid != 0)
            {
                exports.Add((name.Nid, name.Library));
                continue;
            }

            foreach (NidInfo info in NidDatabase.LookupName(name.Name))
            {
                if (name.Library == null || info.Library == name.Library)
                {
                    exports.Add((info.Nid, info.Library));
                }
            }
        }

        (uint Nid, string? Library)[] resolved = exports.ToArray();
        Volatile.Write(ref _resolved, resolved);
        return resolved;
    }
}

/// <summary>
/// The HLE's own calls of the firmware's exported functions, made the way the firmware makes them: by the function's
/// address. On the Vita a hook on an export (taiHookFunctionExport*, or any code written over the function's start)
/// patches the function's code, so every caller runs the hook, the firmware module's own calls included. In
/// VitaSharp the firmware functions the HLE serves are C# methods that call each other directly; each such call goes
/// through the method's generated wrapper <c>XxxThroughEntry</c> (VitaSharp.Core.Generators.HleEntryCallGenerator,
/// required by the analyzer VSHLE010), which asks <see cref="AnyHook"/> and, when a hook took the function, runs
/// it on the calling thread (<see cref="TryCall"/>); otherwise it calls the C# method directly.
/// <para>
/// A hook is found where the plugin framework put it: the patched entry of the function in the firmware kernel
/// modules (<see cref="FirmwareKernelModules"/>, which also sees code written over an entry without the framework),
/// or the thunk of a user plugin's hook on the function in the module's process (<see cref="HleRegistry"/>'s
/// process redirects, the user shells' exports).
/// </para>
/// </summary>
public static class HleEntryCalls
{
    /// <summary>Whether any hook could take a function <paramref name="module"/> serves: this process has a user
    /// hook on an HLE function, or the firmware kernel modules have a patched entry. Without either it reads a few
    /// fields and allocates nothing (<see cref="FirmwareKernelModules.AnyEntryPatched"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AnyHook(HleModule module)
    {
        Emulator emu = module.Emu;
        if (emu.Hle.HasProcessHooks)
        {
            return true;
        }

        return emu.FirmwareKernelIfShown is { } firmware && firmware.AnyEntryPatched();
    }

    /// <summary>
    /// The call of <paramref name="export"/> through its entry, when a hook took it: runs the hooked code on the
    /// calling guest thread with the AAPCS argument words <paramref name="args"/> (r0-r3, then the stack), puts r0
    /// and r1 in <paramref name="result"/> and returns true. Returns false (the caller runs the C# method) when no
    /// hook took any of the export's NIDs or the caller is not a guest thread. A user plugin's hook in the module's
    /// process comes first, as the process's code is what the hook patched; then a patched firmware kernel entry.
    /// </summary>
    public static bool TryCall(HleModule module, HleExportRef export, out ulong result, ReadOnlySpan<uint> args)
    {
        result = 0;
        if (GuestThread.Current is not { } thread)
        {
            return false;
        }

        Emulator emu = module.Emu;
        FirmwareKernelModules? firmware = emu.FirmwareKernelIfShown;
        foreach ((uint nid, string? library) in export.Resolved)
        {
            uint target = emu.Hle.ProcessHookOf(nid, library);
            if (target == 0 && firmware != null)
            {
                target = firmware.PatchedEntryOf(nid, library);
            }

            if (target == 0)
            {
                continue;
            }

            uint low = thread.CallGuest(target, args);
            result = low | ((ulong)thread.LastCallR1 << 32);
            return true;
        }

        return false;
    }
}
