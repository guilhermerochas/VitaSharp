using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// libScePiglet: the EGL 1.4 + OpenGL ES 2.0 driver of the PlayStation Mobile runtime, which VitaSDK homebrew can also
/// link (libScePiglet_stub). On the Vita it is a client of libgxm; here as well: each GLES object lives in guest memory,
/// the GLES state is a state machine kept in C#, and every clear, draw, scene and flip becomes the same GXM calls a game
/// makes (<see cref="Gxm.PigletApi"/>), so the frames reach the renderer through the GXM path. Shader sources are compiled
/// by the runtime Cg compiler libshacccg.suprx run LLE (Piglet.Shaders.cs); GXM programs load directly with
/// glShaderBinary. Errors follow the GLES 2.0 and EGL 1.4 specifications (glGetError keeps the first error until read).
/// </summary>
public sealed partial class Piglet : HleModule
{
    internal const int TextureUnits = 16, MaxAttribs = 16, MaxTextureSize = 4096, MaxRenderbufferSize = 4096;
    internal const uint DisplayWidth = 960, DisplayHeight = 544;

    Gxm.PigletApi? _gxm;
    Gxm.PigletApi G => _gxm ??= Emu.Hle.Modules.OfType<Gxm>().First().ForPiglet;
    Display? _display;
    Display Disp => _display ??= Emu.Hle.Modules.OfType<Display>().First();

    readonly object _lock = new();

    // ------------------------------------------------------------------ guest helpers

    uint Alloc(uint size, string name, MemoryRegion region = MemoryRegion.User, uint align = 256)
    {
        uint a = Emu.Memory.Alloc(Math.Max(size, 16), "Piglet " + name, region, align);
        if (a != 0) GuestMemory.Fill(a, 0, Math.Max(size, 16));
        return a;
    }

    void Free(uint addr) { if (addr != 0) Emu.Memory.Free(addr); }

    /// <summary>A guest-callable stub that runs <paramref name="body"/> (the display callback, the compiler's allocator).</summary>
    uint HostStub(string name, Action<HleContext> body)
    {
        uint stub = Alloc(16, "stub " + name, MemoryRegion.Kernel, 16);
        Linker.WriteHleStub(stub, Emu.Hle.RegisterHostFunction("piglet:" + name, body), 0);
        Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    static void WriteBytes(uint addr, ReadOnlySpan<byte> data) { if (data.Length > 0) data.CopyTo(GuestMemory.Span(addr, data.Length)); }
    static byte[] ReadBytes(uint addr, int length) => length <= 0 || addr == 0 ? [] : GuestMemory.ReadSpan(addr, length).ToArray();

    internal static int TypeSize(uint type) => type switch
    {
        Gl.BYTE or Gl.UNSIGNED_BYTE => 1,
        Gl.SHORT or Gl.UNSIGNED_SHORT or Gl.HALF_FLOAT_OES => 2,
        _ => 4,
    };

    // ------------------------------------------------------------------ current context per thread

    readonly Dictionary<int, PgContext> _currentByThread = [];
    readonly Dictionary<int, int> _eglErrorByThread = [];
    readonly Dictionary<int, int> _apiByThread = [];

    static int ThreadKey => GuestThread.Current?.Uid ?? 0;

    PgContext? Cur
    {
        get { lock (_lock) return _currentByThread.TryGetValue(ThreadKey, out var c) && !c.Destroyed ? c : _currentByThread.GetValueOrDefault(ThreadKey); }
    }

    /// <summary>Records a GL error: the first one stays until glGetError reads it.</summary>
    static void Err(PgContext c, uint e) { if (c.Error == Gl.NO_ERROR) c.Error = e; }

    // inspection (tests): the objects of the calling thread's current context
    internal PgBuffer? Buffer(uint name) => Cur?.Shared.Buffers.GetValueOrDefault(name);
    internal PgTexture? Texture(uint name) => Cur?.Shared.Textures.GetValueOrDefault(name);
    internal PgProgram? ProgramObject(uint name) => Cur?.Shared.Programs.GetValueOrDefault(name);

    // ------------------------------------------------------------------ the GXM side: initialisation

    bool _gxmReady;
    uint _gxmContext, _patcher, _displayCallbackData, _frameBufParam, _scratch;
    const uint ScratchSize = 0x10000;

    /// <summary>libgxm initialised with Piglet's display queue, one immediate context and a shader patcher.</summary>
    void EnsureGxm()
    {
        if (_gxmReady) return;
        _gxmReady = true;
        uint p = Alloc(0x20, "gxm params");
        GuestMemory.W32(p + 4, 3); // displayQueueMaxPendingCount
        GuestMemory.W32(p + 8, HostStub("display", DisplayCallback));
        GuestMemory.W32(p + 12, 20); // callback data: address, width, height, stride, swap interval
        GuestMemory.W32(p + 16, 16 << 20);
        int r = G.Initialize(p);
        if (r != 0) Log.Warn($"[piglet] sceGxmInitialize: 0x{r:X8} (libgxm already initialised by the application: its display callback flips Piglet's frames)");
        Free(p);

        uint cp = Alloc(0x2C, "gxm context params");
        uint vdm = Alloc(128 << 10, "vdm ring"), vertex = Alloc(4 << 20, "vertex ring"), fragment = Alloc(2 << 20, "fragment ring"),
            usse = Alloc(64 << 10, "fragment usse ring"), host = Alloc(2048, "context host mem");
        GuestMemory.W32(cp, host); GuestMemory.W32(cp + 4, 2048);
        GuestMemory.W32(cp + 8, vdm); GuestMemory.W32(cp + 12, 128 << 10);
        GuestMemory.W32(cp + 16, vertex); GuestMemory.W32(cp + 20, 4 << 20);
        GuestMemory.W32(cp + 24, fragment); GuestMemory.W32(cp + 28, 2 << 20);
        GuestMemory.W32(cp + 32, usse); GuestMemory.W32(cp + 36, 64 << 10);
        uint outp = Alloc(16, "out");
        G.CreateContext(cp, outp);
        _gxmContext = GuestMemory.R32(outp);
        uint pp = Alloc(0x44, "patcher params");
        G.ShaderPatcherCreate(pp, outp);
        _patcher = GuestMemory.R32(outp);
        Free(pp);
        Free(cp);
        Free(outp);
        _displayCallbackData = Alloc(32, "display data");
        _frameBufParam = Alloc(0x20, "framebuf param");
        _scratch = Alloc(ScratchSize, "scratch");
        Log.Info($"[piglet] GXM ready: context 0x{_gxmContext:X8}, shader patcher 0x{_patcher:X8}");
    }

    /// <summary>The display queue callback: shows the finished buffer (sceDisplaySetFrameBuf at the next vblank) and
    /// waits the swap interval's vblanks, as a GXM application's callback does.</summary>
    void DisplayCallback(HleContext c)
    {
        uint data = c.Gpr(0);
        uint addr = GuestMemory.R32(data), width = GuestMemory.R32(data + 4), height = GuestMemory.R32(data + 8), stride = GuestMemory.R32(data + 12);
        int interval = (int)GuestMemory.R32(data + 16);
        uint fb = _frameBufParam;
        GuestMemory.W32(fb, 0x18);
        GuestMemory.W32(fb + 4, addr);
        GuestMemory.W32(fb + 8, stride);
        GuestMemory.W32(fb + 12, 0); // SCE_DISPLAY_PIXELFORMAT_A8B8G8R8
        GuestMemory.W32(fb + 16, width);
        GuestMemory.W32(fb + 20, height);
        Disp.SetFrameBuf(fb, interval > 0 ? 1u : 0u);
        if (!FrameRate.NoWaits)
            for (int i = 1; i < interval; i++) VBlank.WaitNext();
        c.Return(0);
    }

    // ------------------------------------------------------------------ transient memory (client arrays, converted indices)

    sealed class Chunk(uint addr, uint size) { public readonly uint Addr = addr, Size = size; public uint Used; public long Fence; }
    readonly List<Chunk> _arena = [];
    readonly Queue<Chunk> _retired = new();
    readonly List<(uint Addr, long Fence)> _deferredFree = [];

    /// <summary>Guest memory for data one draw reads (client-side arrays, converted indices, generic attribute values):
    /// reused once the GPU executed the draws that read it.</summary>
    uint Transient(uint size, uint align = 16)
    {
        foreach (var c in _arena)
        {
            uint at = (c.Used + align - 1) & ~(align - 1);
            if (at + size <= c.Size) { c.Used = at + size; return c.Addr + at; }
        }
        Chunk? n = null;
        if (_retired.TryPeek(out var old) && old.Fence <= Emu.Gpu.Completed && old.Size >= size) { n = _retired.Dequeue(); n.Used = 0; }
        n ??= new Chunk(Alloc(Math.Max(size, 1u << 20), "transient", MemoryRegion.User, 256), Math.Max(size, 1u << 20));
        _arena.Add(n);
        n.Used = size;
        return n.Addr;
    }

    /// <summary>The frame's transient memory is retired with the GPU fence of its last command.</summary>
    void RetireTransient()
    {
        long fence = Emu.Gpu.Submitted;
        foreach (var c in _arena) { c.Fence = fence; _retired.Enqueue(c); }
        _arena.Clear();
        while (_retired.Count > 8 && _retired.Peek().Fence <= Emu.Gpu.Completed) Free(_retired.Dequeue().Addr);
        for (int i = _deferredFree.Count - 1; i >= 0; i--)
            if (_deferredFree[i].Fence <= Emu.Gpu.Completed) { Free(_deferredFree[i].Addr); _deferredFree.RemoveAt(i); }
    }

    /// <summary>Frees a data store once the GPU no longer reads it.</summary>
    void FreeAfterGpu(uint addr, long seq)
    {
        if (addr == 0) return;
        if (seq <= Emu.Gpu.Completed) Free(addr);
        else _deferredFree.Add((addr, Math.Max(seq, Emu.Gpu.Submitted)));
    }

    /// <summary>Writes <paramref name="data"/> into guest memory in GPU order: right away when nothing outstanding reads
    /// the range, else as a GPU-queue command after the draws that read it.</summary>
    long OrderedWrite(uint addr, byte[] data, long busySeq)
    {
        if (data.Length == 0) return busySeq;
        if (busySeq <= Emu.Gpu.Completed)
        {
            WriteBytes(addr, data);
            return busySeq;
        }
        return Emu.Gpu.Submit(new Gpu.CallbackCommand
        {
            WriteAddress = addr, WriteSize = (uint)data.Length,
            Action = () => WriteBytes(addr, data),
        });
    }
}
