using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle;

/// <summary>
/// Requests to the shell's services the way the system libraries make them. On the Vita the client side of netctl,
/// NP manager and similar services (libnetctl.suprx, np_manager.suprx...) is a thin layer over libshellsvc: it takes
/// the process's service object (sceShellSvcGetSvcObj) and calls its synchronous method (vtable slot 5, +0x14) with a
/// method number, the input buffers as {pointer, size} pairs, a result word and the output buffers as {pointer, size,
/// size written} triples; the shell's server does the work. A system program in VitaSharp runs the real libshellsvc
/// and the real shell, so the HLE library forwards the request unchanged and returns what the server answered. A
/// process without libshellsvc (an application started without the system software) has no server to ask.
/// </summary>
internal static unsafe class ShellSvcIpc
{
    /// <summary>sceShellSvcGetSvcObj (libshellsvc.suprx).</summary>
    public const uint NidGetSvcObj = 0xB31E7F1C;

    /// <summary>One buffer of a request: guest memory (<see cref="Buf"/>) or words copied to the request
    /// (<see cref="Val"/>).</summary>
    public readonly struct Arg
    {
        public readonly uint Ptr, Size;
        public readonly uint[]? Words;
        public readonly bool IsCell;
        Arg(uint ptr, uint size, uint[]? words, bool cell = false) { Ptr = ptr; Size = size; Words = words; IsCell = cell; }
        public static Arg Buf(uint ptr, uint size) => new(ptr, size, null);
        public static Arg Val(params uint[] words) => new(0, (uint)words.Length * 4, words);
        /// <summary>An output word of the request itself (the libraries pass a local and copy it out afterwards).</summary>
        public static Arg Cell() => new(0, 4, null, true);
    }

    public static Arg Buf(uint ptr, uint size) => Arg.Buf(ptr, size);
    public static Arg Val(params uint[] words) => Arg.Val(words);
    public static Arg Cell() => Arg.Cell();

    /// <summary>Whether this process can reach the shell's services (libshellsvc is loaded).</summary>
    public static bool Available(Emulator emu) => GuestThread.Current != null && emu.Linker.TryGetFunctionExport(NidGetSvcObj, out _);

    /// <summary>Calls method <paramref name="method"/> of the shell's service object; returns the call's error or, when
    /// it went through, the server's result. <paramref name="unavailable"/> when no libshellsvc is loaded.</summary>
    public static int Invoke(Emulator emu, uint method, ReadOnlySpan<Arg> inArgs, ReadOnlySpan<Arg> outArgs, int unavailable) =>
        Invoke(emu, method, inArgs, outArgs, [], unavailable);

    /// <summary><see cref="Invoke(Emulator, uint, ReadOnlySpan{Arg}, ReadOnlySpan{Arg}, int)"/> that also returns the
    /// output cells (<see cref="Cell"/>), in order.</summary>
    public static int Invoke(Emulator emu, uint method, ReadOnlySpan<Arg> inArgs, ReadOnlySpan<Arg> outArgs, Span<uint> cells, int unavailable)
    {
        if (GuestThread.Current is not { } t || !emu.Linker.TryGetFunctionExport(NidGetSvcObj, out uint getObj)) return unavailable;
        // the request lives on the guest stack below the caller's frame: words, input pairs, output triples, result
        uint words = 0;
        foreach (var a in inArgs) if (a.Words != null) words += a.Size;
        uint size = words + (uint)inArgs.Length * 8 + (uint)outArgs.Length * 16 + 8;
        uint sp = t.Core.State->R[13];
        uint bottom = (sp - 0x40 - size) & ~7u;
        uint w = bottom, inDesc = bottom + words, outDesc = inDesc + (uint)inArgs.Length * 8, cellArea = outDesc + (uint)outArgs.Length * 12, result = cellArea + (uint)outArgs.Length * 4;
        for (int i = 0; i < inArgs.Length; i++)
        {
            var a = inArgs[i];
            uint p = a.Ptr;
            if (a.Words != null)
            {
                p = w;
                foreach (uint v in a.Words) { GuestMemory.W32(w, v); w += 4; }
            }
            GuestMemory.W32(inDesc + (uint)i * 8, p);
            GuestMemory.W32(inDesc + (uint)i * 8 + 4, a.Size);
        }
        for (int i = 0; i < outArgs.Length; i++)
        {
            uint cell = cellArea + (uint)i * 4;
            GuestMemory.W32(cell, 0);
            GuestMemory.W32(outDesc + (uint)i * 12, outArgs[i].IsCell ? cell : outArgs[i].Ptr);
            GuestMemory.W32(outDesc + (uint)i * 12 + 4, outArgs[i].Size);
            GuestMemory.W32(outDesc + (uint)i * 12 + 8, 0);
        }
        GuestMemory.W32(result, 0);
        uint obj = t.CallGuestOnStack(getObj, bottom);
        if (obj == 0) return unavailable;
        uint fn = GuestMemory.R32(GuestMemory.R32(obj) + 0x14);
        int r = (int)t.CallGuestOnStack(fn, bottom, obj, method, inArgs.Length == 0 ? 0 : inDesc, (uint)inArgs.Length, result,
            outArgs.Length == 0 ? 0 : outDesc, (uint)outArgs.Length);
        for (int i = 0, c = 0; i < outArgs.Length && c < cells.Length; i++)
            if (outArgs[i].IsCell) cells[c++] = GuestMemory.R32(cellArea + (uint)i * 4);
        return r != 0 ? r : (int)GuestMemory.R32(result);
    }

    public static int Invoke(Emulator emu, uint method, int unavailable, params Arg[] inArgs) => Invoke(emu, method, inArgs, [], unavailable);

    /// <summary>Calls a guest function of a handler table (the shell gives its own libraries tables that skip the IPC).</summary>
    public static int CallHandler(uint fn, params ReadOnlySpan<uint> args) => (int)GuestThread.Current!.CallGuest(fn, args);
}
