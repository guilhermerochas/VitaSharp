using System.Text;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Shaders and programs of libScePiglet. Shader sources (Cg, the language of the PSM shaders and of SceShaccCg) are
/// compiled by the real runtime compiler libshacccg.suprx, run LLE: the copy the program already loaded (its SceShaccCg
/// exports), else the module where the loader looks for a program's modules (its own app0:sce_module, then the system
/// software's vs0:sys/external). Compiled shaders, and
/// those given to glShaderBinary, are GXM programs; linking registers both with the shader patcher and reads their
/// parameter tables: attributes, uniforms (in the default uniform buffers) and samplers (texture units).
/// </summary>
public sealed partial class Piglet
{
    // ------------------------------------------------------------------ the runtime compiler (SceShaccCg, LLE)

    const uint ShaccLib = 0xA05BBEBB;
    const uint NidCompileProgram = 0x66814F35, NidDestroyOutput = 0xAA82EF0C, NidInitCallbacks = 0xA8C2C1C8, NidInitOptions = 0x3B58AFA0,
        NidSetAllocator = 0x6F01D573, NidReleaseCompiler = 0x95F57A23;
    static readonly string[] CompilerPaths = ["app0:sce_module/libshacccg.suprx", "vs0:sys/external/libshacccg.suprx"];

    bool _compilerTried, _compilerReady;
    uint _openFileStub, _mainSourceFile;

    uint ShaccExport(uint nid) => Emu.Linker.TryGetFunctionExport(ShaccLib, nid, out uint a) ? a : 0;

    /// <summary>The compiler module is loaded and started (true when its exports exist).</summary>
    bool EnsureCompiler()
    {
        if (_compilerReady) return true;
        if (ShaccExport(NidCompileProgram) == 0 && !_compilerTried)
        {
            _compilerTried = true;
            foreach (var path in CompilerPaths)
            {
                if (Emu.Vfs.Resolve(path) is not { } host || !File.Exists(host)) continue;
                try
                {
                    var m = Emu.LoadModule(path);
                    if (m.StartEntry != 0) GuestThread.Current!.CallGuest(m.StartEntry, 0, 0);
                    Log.Info($"[piglet] runtime shader compiler {path} loaded");
                    break;
                }
                catch (Exception e) when (e is not OutOfMemoryException) { Log.Warn($"[piglet] {path}: {e.Message}"); }
            }
        }
        if (ShaccExport(NidCompileProgram) == 0)
        {
            Log.WarnOnce("piglet-noshacccg", "[piglet] libshacccg.suprx (the runtime Cg compiler) is not loaded: glCompileShader fails. The program loads it " +
                "itself (from a file copied from your own PS Vita into the emulator's data folder) or ships it in app0:sce_module.");
            return false;
        }
        // the compiler allocates through the C library's malloc/free (the HLE ones)
        uint malloc = LibcStub("malloc"), free = LibcStub("free");
        if (malloc != 0 && free != 0) GuestThread.Current!.CallGuest(ShaccExport(NidSetAllocator), malloc, free);
        _openFileStub = HostStub("shacccg openFile", c => c.Return(OpenFile(GuestMemory.ReadCString(c.Gpr(0)))));
        _mainSourceFile = Alloc(16, "shacccg source file");
        _compilerReady = true;
        return true;
    }

    uint LibcStub(string name)
    {
        var info = NidDatabase.LookupName(name).FirstOrDefault(n => n.Library == "SceLibc" && Emu.Hle.IsImplemented(n.Nid));
        if (info == null) return 0;
        uint a = Alloc(16, "libc " + name, MemoryRegion.Kernel, 16);
        Loader.Linker.WriteHleStub(a, Emu.Hle.GetHleFunctionIndex(info.Nid, 0, "SceLibc"), info.Nid);
        Emu.Code.Invalidate(a, 16);
        return a;
    }

    string _mainFileName = "<built-in>";

    /// <summary>The compiler's openFile callback: the shader source (SceShaccCgSourceFile), no other file.</summary>
    uint OpenFile(string name) => name == _mainFileName ? _mainSourceFile : 0;

    /// <summary>Compiles the shader's Cg source with SceShaccCg: the program (GXP) and the diagnostics as the info log.</summary>
    void CompileShader(PgShader s)
    {
        s.Compiled = false;
        s.Gxp = null;
        if (s.Source == null) { s.InfoLog = "no source"; return; }
        if (!EnsureCompiler()) { s.InfoLog = "error: the runtime shader compiler (libshacccg.suprx) is not available\n"; return; }
        var t = GuestThread.Current!;
        byte[] src = Encoding.UTF8.GetBytes(s.Source);
        uint text = Alloc((uint)src.Length + 1, "shader source", MemoryRegion.User, 16);
        WriteBytes(text, src);
        uint name = Alloc(32, "shader name", MemoryRegion.User, 4), entry = Alloc(8, "entry", MemoryRegion.User, 4);
        GuestMemory.WriteCString(name, _mainFileName);
        GuestMemory.WriteCString(entry, "main");
        GuestMemory.W32(_mainSourceFile, name);
        GuestMemory.W32(_mainSourceFile + 4, text);
        GuestMemory.W32(_mainSourceFile + 8, (uint)src.Length);
        uint opts = Alloc(0x68, "compile options", MemoryRegion.User, 8), cbs = Alloc(0x18, "callbacks", MemoryRegion.User, 8);
        t.CallGuest(ShaccExport(NidInitOptions), opts);
        GuestMemory.W32(opts + 0x00, name);
        GuestMemory.W32(opts + 0x04, s.Type == Gl.VERTEX_SHADER ? 0u : 1u); // SCE_SHACCCG_PROFILE_VP / FP
        GuestMemory.W32(opts + 0x08, entry);
        GuestMemory.W32(opts + 0x2C, 0);  // locale: English
        GuestMemory.W32(opts + 0x30, 1);  // useFx
        GuestMemory.W32(opts + 0x38, 3);  // optimizationLevel
        GuestMemory.W32(opts + 0x3C, 1);  // useFastmath
        GuestMemory.W32(opts + 0x44, 1);  // useFastint
        GuestMemory.W32(opts + 0x58, 1);  // warningLevel
        t.CallGuest(ShaccExport(NidInitCallbacks), cbs, 1); // SCE_SHACCCG_TRIVIAL
        GuestMemory.W32(cbs, _openFileStub);
        uint output = t.CallGuest(ShaccExport(NidCompileProgram), opts, cbs, 0);
        var log = new StringBuilder();
        if (output != 0)
        {
            uint data = GuestMemory.R32(output), size = GuestMemory.R32(output + 4);
            int count = (int)GuestMemory.R32(output + 8);
            uint diags = GuestMemory.R32(output + 12);
            for (int i = 0; i < count && diags != 0; i++)
            {
                uint d = diags + (uint)i * 16;
                uint level = GuestMemory.R32(d), code = GuestMemory.R32(d + 4), loc = GuestMemory.R32(d + 8), msg = GuestMemory.R32(d + 12);
                string where = loc != 0 ? $"({GuestMemory.R32(loc + 4)},{GuestMemory.R32(loc + 8)})" : "";
                log.Append(level switch { 0 => "info", 1 => "warning", _ => "error" }).Append(where).Append(" C").Append(code).Append(": ")
                    .Append(msg != 0 ? GuestMemory.ReadCString(msg) : "").Append('\n');
            }
            if (data != 0 && size > 0)
            {
                s.Gxp = ReadBytes(data, (int)size);
                s.Compiled = GxpProgram.Validate(s.Gxp) == null;
                if (!s.Compiled) log.Append("error: the compiler's output is not a GXM program\n");
            }
            t.CallGuest(ShaccExport(NidDestroyOutput), output);
        }
        else log.Append("error: the compiler returned no output\n");
        s.InfoLog = log.ToString();
        Free(text); Free(name); Free(entry); Free(opts); Free(cbs);
    }

    // ------------------------------------------------------------------ shader objects

    [Hle]
    uint glCreateShader(uint type)
    {
        if (Cur is not { } c) return 0;
        if (type is not (Gl.VERTEX_SHADER or Gl.FRAGMENT_SHADER)) { Err(c, Gl.INVALID_ENUM); return 0; }
        var sh = c.Shared;
        while (sh.Shaders.ContainsKey(sh.NextShaderOrProgram) || sh.Programs.ContainsKey(sh.NextShaderOrProgram)) sh.NextShaderOrProgram++;
        var s = new PgShader(sh.NextShaderOrProgram++, type);
        sh.Shaders[s.Name] = s;
        return s.Name;
    }

    /// <summary>The shader named <paramref name="name"/>: GL_INVALID_VALUE for an unknown name, GL_INVALID_OPERATION for a
    /// program's name.</summary>
    PgShader? ShaderOf(PgContext c, uint name)
    {
        if (c.Shared.Shaders.TryGetValue(name, out var s)) return s;
        Err(c, c.Shared.Programs.ContainsKey(name) ? Gl.INVALID_OPERATION : Gl.INVALID_VALUE);
        return null;
    }

    PgProgram? ProgramOf(PgContext c, uint name)
    {
        if (c.Shared.Programs.TryGetValue(name, out var p)) return p;
        Err(c, c.Shared.Shaders.ContainsKey(name) ? Gl.INVALID_OPERATION : Gl.INVALID_VALUE);
        return null;
    }

    [Hle]
    void glDeleteShader(uint shader)
    {
        if (Cur is not { } c || shader == 0) return;
        if (ShaderOf(c, shader) is not { } s) return;
        s.DeletePending = true;
        if (s.Attachments == 0) c.Shared.Shaders.Remove(shader);
    }

    [Hle] bool glIsShader(uint shader) => Cur is { } c && c.Shared.Shaders.ContainsKey(shader);

    [Hle]
    void glShaderSource(uint shader, int count, uint strings, uint lengths)
    {
        if (Cur is not { } c) return;
        if (ShaderOf(c, shader) is not { } s) return;
        if (count < 0) { Err(c, Gl.INVALID_VALUE); return; }
        var sb = new StringBuilder();
        for (int i = 0; i < count && strings != 0; i++)
        {
            uint p = GuestMemory.R32(strings + (uint)i * 4);
            if (p == 0) continue;
            int len = lengths != 0 ? (int)GuestMemory.R32(lengths + (uint)i * 4) : -1;
            sb.Append(len >= 0 ? Encoding.UTF8.GetString(GuestMemory.ReadSpan(p, len)) : GuestMemory.ReadCString(p, 1 << 20));
        }
        s.Source = sb.ToString();
    }

    [Hle]
    void glGetShaderSource(uint shader, int bufSize, Ptr<int> length, uint source)
    {
        if (Cur is not { } c) return;
        if (ShaderOf(c, shader) is not { } s) return;
        if (bufSize < 0) { Err(c, Gl.INVALID_VALUE); return; }
        WriteString(s.Source ?? "", bufSize, length, source);
    }

    /// <summary>A GL string output: at most bufSize-1 characters and the terminator; the length without it.</summary>
    static void WriteString(string text, int bufSize, Ptr<int> length, uint dst)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        int n = bufSize > 0 ? Math.Min(b.Length, bufSize - 1) : 0;
        if (dst != 0 && bufSize > 0)
        {
            WriteBytes(dst, b.AsSpan(0, n));
            GuestMemory.Write(dst + (uint)n, (byte)0);
        }
        length.TrySet(n);
    }

    [Hle]
    void glCompileShader(uint shader)
    {
        if (Cur is not { } c) return;
        if (ShaderOf(c, shader) is not { } s) return;
        CompileShader(s);
    }

    [Hle]
    void glReleaseShaderCompiler()
    {
        if (_compilerReady && ShaccExport(NidReleaseCompiler) is uint a and not 0) GuestThread.Current!.CallGuest(a);
    }

    /// <summary>glShaderBinary: GXM programs ("GXP\0"), one per shader; the binary format is not checked beyond that.</summary>
    [Hle]
    void glShaderBinary(int n, uint shaders, uint binaryFormat, uint binary, int length)
    {
        if (Cur is not { } c) return;
        if (n < 0 || length < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (binaryFormat != Gl.SHADER_BINARY_GXP_SCE && (length < 4 || GuestMemory.R32(binary) != 0x00505847))
        { Err(c, Gl.INVALID_ENUM); return; }
        byte[] data = ReadBytes(binary, length);
        if (GxpProgram.Validate(data) is { } why) { Log.Warn($"[piglet] glShaderBinary: {why}"); Err(c, Gl.INVALID_VALUE); return; }
        var program = new GxpProgram(data, 0);
        for (int i = 0; i < n; i++)
        {
            uint name = GuestMemory.R32(shaders + (uint)i * 4);
            if (ShaderOf(c, name) is not { } s) return;
            if ((s.Type == Gl.VERTEX_SHADER) != program.IsVertex) { Err(c, Gl.INVALID_OPERATION); return; }
            s.Gxp = data;
            s.Compiled = true;
            s.InfoLog = "";
        }
    }

    /// <summary>Piglet's extension: the GXM program of a compiled shader (inferred signature, as glGetProgramBinaryOES:
    /// shader, bufSize, *length, *binaryFormat, *binary).</summary>
    [Hle]
    void glPigletGetShaderBinarySCE(uint shader, int bufSize, Ptr<int> length, Ptr<uint> binaryFormat, uint binary)
    {
        if (Cur is not { } c) return;
        if (ShaderOf(c, shader) is not { } s) return;
        if (!s.Compiled || s.Gxp == null) { Err(c, Gl.INVALID_OPERATION); return; }
        if (bufSize < s.Gxp.Length) { Err(c, Gl.INVALID_OPERATION); length.TrySet(0); return; }
        if (binary != 0) WriteBytes(binary, s.Gxp);
        length.TrySet(s.Gxp.Length);
        binaryFormat.TrySet(Gl.SHADER_BINARY_GXP_SCE);
    }

    [Hle]
    void glGetShaderiv(uint shader, uint pname, Ptr<int> param)
    {
        if (Cur is not { } c) return;
        if (ShaderOf(c, shader) is not { } s) return;
        int? v = pname switch
        {
            Gl.SHADER_TYPE => (int)s.Type,
            Gl.DELETE_STATUS => s.DeletePending ? 1 : 0,
            Gl.COMPILE_STATUS => s.Compiled ? 1 : 0,
            Gl.INFO_LOG_LENGTH => s.InfoLog.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(s.InfoLog) + 1,
            Gl.SHADER_SOURCE_LENGTH => s.Source == null ? 0 : Encoding.UTF8.GetByteCount(s.Source) + 1,
            _ => null,
        };
        if (v == null) { Err(c, Gl.INVALID_ENUM); return; }
        param.TrySet(v.Value);
    }

    [Hle]
    void glGetShaderInfoLog(uint shader, int bufSize, Ptr<int> length, uint infoLog)
    {
        if (Cur is not { } c) return;
        if (ShaderOf(c, shader) is not { } s) return;
        if (bufSize < 0) { Err(c, Gl.INVALID_VALUE); return; }
        WriteString(s.InfoLog, bufSize, length, infoLog);
    }

    [Hle]
    void glGetShaderPrecisionFormat(uint shaderType, uint precisionType, uint range, uint precision)
    {
        if (Cur is not { } c) return;
        if (shaderType is not (Gl.VERTEX_SHADER or Gl.FRAGMENT_SHADER) || precisionType < Gl.LOW_FLOAT || precisionType > Gl.HIGH_INT)
        { Err(c, Gl.INVALID_ENUM); return; }
        // the USSE computes floats in IEEE single precision (half for the low/medium "fast precision") and 32-bit ints
        (int lo, int hi, int prec) = precisionType switch
        {
            Gl.LOW_FLOAT or Gl.MEDIUM_FLOAT => (15, 15, 10),
            Gl.HIGH_FLOAT => (127, 127, 23),
            _ => (31, 30, 0),
        };
        if (range != 0) { GuestMemory.W32(range, (uint)lo); GuestMemory.W32(range + 4, (uint)hi); }
        if (precision != 0) GuestMemory.W32(precision, (uint)prec);
    }

    // ------------------------------------------------------------------ program objects

    [Hle]
    uint glCreateProgram()
    {
        if (Cur is not { } c) return 0;
        var sh = c.Shared;
        while (sh.Shaders.ContainsKey(sh.NextShaderOrProgram) || sh.Programs.ContainsKey(sh.NextShaderOrProgram)) sh.NextShaderOrProgram++;
        var p = new PgProgram(sh.NextShaderOrProgram++);
        sh.Programs[p.Name] = p;
        return p.Name;
    }

    [Hle] bool glIsProgram(uint program) => Cur is { } c && c.Shared.Programs.ContainsKey(program);

    [Hle]
    void glDeleteProgram(uint program)
    {
        if (Cur is not { } c || program == 0) return;
        if (ProgramOf(c, program) is not { } p) return;
        p.DeletePending = true;
        if (c.Program != p) DestroyProgram(c, p);
    }

    void DestroyProgram(PgContext c, PgProgram p)
    {
        foreach (var s in new[] { p.Vs, p.Fs }) if (s != null) Detach(c, s);
        p.Vs = p.Fs = null;
        ReleaseLink(p);
        c.Shared.Programs.Remove(p.Name);
    }

    void Detach(PgContext c, PgShader s)
    {
        s.Attachments--;
        if (s.DeletePending && s.Attachments <= 0) c.Shared.Shaders.Remove(s.Name);
    }

    [Hle]
    void glAttachShader(uint program, uint shader)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (ShaderOf(c, shader) is not { } s) return;
        ref var slot = ref s.Type == Gl.VERTEX_SHADER ? ref p.Vs : ref p.Fs;
        if (slot != null) { Err(c, Gl.INVALID_OPERATION); return; } // already one of this type (or this one)
        slot = s;
        s.Attachments++;
    }

    [Hle]
    void glDetachShader(uint program, uint shader)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (ShaderOf(c, shader) is not { } s) return;
        if (p.Vs == s) p.Vs = null;
        else if (p.Fs == s) p.Fs = null;
        else { Err(c, Gl.INVALID_OPERATION); return; }
        Detach(c, s);
    }

    [Hle]
    void glGetAttachedShaders(uint program, int maxCount, Ptr<int> count, uint shaders)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (maxCount < 0) { Err(c, Gl.INVALID_VALUE); return; }
        int n = 0;
        foreach (var s in new[] { p.Vs, p.Fs })
            if (s != null && n < maxCount && shaders != 0) GuestMemory.W32(shaders + (uint)n++ * 4, s.Name);
        count.TrySet(n);
    }

    [Hle]
    void glBindAttribLocation(uint program, uint index, string name)
    {
        if (Cur is not { } c) return;
        if (index >= MaxAttribs) { Err(c, Gl.INVALID_VALUE); return; }
        if (ProgramOf(c, program) is not { } p) return;
        if (name.StartsWith("gl_", StringComparison.Ordinal)) { Err(c, Gl.INVALID_OPERATION); return; }
        p.Bindings[name] = index; // takes effect at the next link
    }

    void ReleaseLink(PgProgram p)
    {
        if (p.VsId != 0) G.UnregisterProgram(_patcher, p.VsId);
        if (p.FsId != 0) G.UnregisterProgram(_patcher, p.FsId);
        long seq = Emu.Gpu.Submitted;
        FreeAfterGpu(p.VsGxp, seq);
        FreeAfterGpu(p.FsGxp, seq);
        p.VsGxp = p.FsGxp = p.VsId = p.FsId = 0;
        p.VsProgram = p.FsProgram = null;
    }

    static uint VectorType(int comps, bool integer) => (integer, comps) switch
    {
        (false, 1) => Gl.FLOAT, (false, 2) => Gl.FLOAT_VEC2, (false, 3) => Gl.FLOAT_VEC3, (false, _) => Gl.FLOAT_VEC4,
        (true, 1) => Gl.INT, (true, 2) => Gl.INT_VEC2, (true, 3) => Gl.INT_VEC3, _ => Gl.INT_VEC4,
    };

    uint CopyProgram(byte[] gxp)
    {
        uint a = Alloc((uint)gxp.Length, "gxp program", MemoryRegion.User, 64);
        WriteBytes(a, gxp);
        return a;
    }

    /// <summary>Links the program: both programs registered with the shader patcher; attributes take their bound
    /// locations (the others the lowest free ones); uniforms and samplers of both programs merged by name.</summary>
    void LinkProgram(PgProgram p)
    {
        EnsureGxm();
        p.Linked = false;
        p.Validated = false;
        var log = new StringBuilder();
        if (p.Vs is not { Compiled: true, Gxp: { } vgxp } || p.Fs is not { Compiled: true, Gxp: { } fgxp })
        {
            p.InfoLog = (p.Vs == null || p.Fs == null ? "error: a vertex and a fragment shader must be attached" : "error: an attached shader is not compiled") + "\n";
            return;
        }
        ReleaseLink(p);
        p.VsGxp = CopyProgram(vgxp);
        p.FsGxp = CopyProgram(fgxp);
        p.VsProgram = new GxpProgram(vgxp, p.VsGxp);
        p.FsProgram = new GxpProgram(fgxp, p.FsGxp);
        if (!p.VsProgram.IsVertex || !p.FsProgram.IsFragment) { p.InfoLog = "error: the shaders' program types do not match their stages\n"; return; }
        uint outp = _scratch + 0x208;
        if (G.RegisterProgram(_patcher, p.VsGxp, outp) != 0) { p.InfoLog = "error: the vertex program was refused\n"; return; }
        p.VsId = GuestMemory.R32(outp);
        if (G.RegisterProgram(_patcher, p.FsGxp, outp) != 0) { p.InfoLog = "error: the fragment program was refused\n"; return; }
        p.FsId = GuestMemory.R32(outp);
        // attributes
        var attribs = new List<PgAttrib>();
        var taken = new HashSet<uint>();
        var vsAttrs = p.VsProgram.Parameters.Where(x => x.Category == ParamCategory.Attribute).ToList();
        foreach (var a in vsAttrs)
            if (p.Bindings.TryGetValue(a.RawName, out uint loc)) taken.Add(loc);
        uint next = 0;
        foreach (var a in vsAttrs)
        {
            if (!p.Bindings.TryGetValue(a.RawName, out uint loc))
            {
                while (taken.Contains(next)) next++;
                loc = next;
                taken.Add(loc);
            }
            if (loc >= MaxAttribs) { p.InfoLog = $"error: too many attributes (at most {MaxAttribs})\n"; return; }
            bool integer = a.Type is ParamType.S32 or ParamType.U32 or ParamType.S16 or ParamType.U16 or ParamType.S8 or ParamType.U8;
            attribs.Add(new PgAttrib(a.RawName, loc, a.ResourceIndex, Math.Max(a.ComponentCount, 1), a.Type, VectorType(Math.Max(a.ComponentCount, 1), integer)));
        }
        // uniforms and samplers, merged by name
        var uniforms = new Dictionary<string, PgUniform>(StringComparer.Ordinal);
        void Add(GxpParameter x, bool fragment)
        {
            if (x.Category is not (ParamCategory.Uniform or ParamCategory.Sampler)) return;
            bool sampler = x.Category == ParamCategory.Sampler;
            int comps = sampler ? 1 : Math.Max(x.ComponentCount, 1);
            int size = (int)Math.Max(x.ArraySize, 1);
            bool integer = x.Type is ParamType.S32 or ParamType.U32;
            uint type = sampler ? (x.IsSamplerCube ? Gl.SAMPLER_CUBE : Gl.SAMPLER_2D) : VectorType(comps, integer);
            if (!uniforms.TryGetValue(x.RawName, out var u))
            {
                u = new PgUniform(x.RawName, type, sampler ? 1 : size, comps);
                uniforms[x.RawName] = u;
            }
            else if (u.GlType != type || u.ArraySize != (sampler ? 1 : size)) log.Append($"warning: uniform {x.RawName} differs between the stages\n");
            if (fragment) u.FsParam = x; else u.VsParam = x;
        }
        foreach (var x in p.VsProgram.Parameters) Add(x, false);
        foreach (var x in p.FsProgram.Parameters) Add(x, true);
        p.Attribs = attribs;
        p.Uniforms = uniforms.Values.ToList();
        p.Linked = true;
        p.EverLinked = true;
        p.InfoLog = log.ToString();
    }

    [Hle]
    void glLinkProgram(uint program)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        LinkProgram(p);
    }

    [Hle]
    void glUseProgram(uint program)
    {
        if (Cur is not { } c) return;
        PgProgram? p = null;
        if (program != 0)
        {
            if (ProgramOf(c, program) is not { } q) return;
            if (!q.Linked) { Err(c, Gl.INVALID_OPERATION); return; }
            p = q;
        }
        if (c.Program is { DeletePending: true } old && old != p) DestroyProgram(c, old);
        c.Program = p;
    }

    [Hle]
    void glValidateProgram(uint program)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        // two samplers of different types on one texture unit cannot be drawn with (GLES 2.0 2.10.5)
        var units = p.Uniforms.Where(u => u.IsSampler).GroupBy(u => (int)u.Values[0]);
        bool conflict = units.Any(g => g.Select(u => u.GlType).Distinct().Count() > 1);
        p.Validated = p.Linked && !conflict;
        if (conflict) p.InfoLog += "error: samplers of different types use the same texture unit\n";
    }

    [Hle]
    void glGetProgramiv(uint program, uint pname, Ptr<int> param)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        int? v = pname switch
        {
            Gl.DELETE_STATUS => p.DeletePending ? 1 : 0,
            Gl.LINK_STATUS => p.Linked ? 1 : 0,
            Gl.VALIDATE_STATUS => p.Validated ? 1 : 0,
            Gl.INFO_LOG_LENGTH => p.InfoLog.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(p.InfoLog) + 1,
            Gl.ATTACHED_SHADERS => (p.Vs != null ? 1 : 0) + (p.Fs != null ? 1 : 0),
            Gl.ACTIVE_ATTRIBUTES => p.Attribs.Count,
            Gl.ACTIVE_ATTRIBUTE_MAX_LENGTH => p.Attribs.Count == 0 ? 0 : p.Attribs.Max(a => a.Name.Length) + 1,
            Gl.ACTIVE_UNIFORMS => p.Uniforms.Count,
            Gl.ACTIVE_UNIFORM_MAX_LENGTH => p.Uniforms.Count == 0 ? 0 : p.Uniforms.Max(u => ActiveName(u).Length) + 1,
            _ => null,
        };
        if (v == null) { Err(c, Gl.INVALID_ENUM); return; }
        param.TrySet(v.Value);
    }

    [Hle]
    void glGetProgramInfoLog(uint program, int bufSize, Ptr<int> length, uint infoLog)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (bufSize < 0) { Err(c, Gl.INVALID_VALUE); return; }
        WriteString(p.InfoLog, bufSize, length, infoLog);
    }

    static string ActiveName(PgUniform u) => u.ArraySize > 1 ? u.Name + "[0]" : u.Name;

    [Hle]
    void glGetActiveAttrib(uint program, uint index, int bufSize, Ptr<int> length, Ptr<int> size, Ptr<uint> type, uint name)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (index >= p.Attribs.Count || bufSize < 0) { Err(c, Gl.INVALID_VALUE); return; }
        var a = p.Attribs[(int)index];
        WriteString(a.Name, bufSize, length, name);
        size.TrySet(1);
        type.TrySet(a.GlType);
    }

    [Hle]
    void glGetActiveUniform(uint program, uint index, int bufSize, Ptr<int> length, Ptr<int> size, Ptr<uint> type, uint name)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (index >= p.Uniforms.Count || bufSize < 0) { Err(c, Gl.INVALID_VALUE); return; }
        var u = p.Uniforms[(int)index];
        WriteString(ActiveName(u), bufSize, length, name);
        size.TrySet(u.ArraySize);
        type.TrySet(u.GlType);
    }

    [Hle]
    int glGetAttribLocation(uint program, string name)
    {
        if (Cur is not { } c) return -1;
        if (ProgramOf(c, program) is not { } p) return -1;
        if (!p.Linked) { Err(c, Gl.INVALID_OPERATION); return -1; }
        return p.Attribs.FirstOrDefault(a => a.Name == name) is { } at ? (int)at.Location : -1;
    }

    /// <summary>A uniform location: the uniform's index (high half) and the array element (low half).</summary>
    [Hle]
    int glGetUniformLocation(uint program, string name)
    {
        if (Cur is not { } c) return -1;
        if (ProgramOf(c, program) is not { } p) return -1;
        if (!p.Linked) { Err(c, Gl.INVALID_OPERATION); return -1; }
        int element = 0;
        string baseName = name;
        if (name.EndsWith(']') && name.LastIndexOf('[') is int lb and > 0)
        {
            if (!int.TryParse(name.AsSpan(lb + 1, name.Length - lb - 2), out element) || element < 0) return -1;
            baseName = name[..lb];
        }
        for (int i = 0; i < p.Uniforms.Count; i++)
        {
            var u = p.Uniforms[i];
            if (u.Name != baseName) continue;
            if (element >= Math.Max(u.ArraySize, 1)) return -1;
            if (element > 0 && u.ArraySize <= 1) return -1;
            return (i << 16) | element;
        }
        return -1;
    }

    (PgUniform U, int Element)? UniformAt(PgContext c, int location, out bool error)
    {
        error = false;
        if (c.Program is not { } p) { Err(c, Gl.INVALID_OPERATION); error = true; return null; } // no current program, even for location -1
        if (location == -1) return null; // ignored
        int idx = location >> 16, el = location & 0xFFFF;
        if (location < -1 || idx >= p.Uniforms.Count || el >= Math.Max(p.Uniforms[idx].ArraySize, 1)) { Err(c, Gl.INVALID_OPERATION); error = true; return null; }
        return (p.Uniforms[idx], el);
    }

    /// <summary>glUniform*: <paramref name="comps"/> values per element; sampler uniforms take only glUniform1i(v) with a
    /// valid texture unit; integer setters on floats (and the reverse) are GL_INVALID_OPERATION.</summary>
    void SetUniform(int location, int count, int comps, bool integer, Func<int, float> value)
    {
        if (Cur is not { } c) return;
        if (count < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (UniformAt(c, location, out _) is not var (u, el)) return;
        if (u.Components != comps || u.IsInt != integer) { Err(c, Gl.INVALID_OPERATION); return; }
        if (count > 1 && u.ArraySize <= 1) { Err(c, Gl.INVALID_OPERATION); return; }
        int n = Math.Min(count, Math.Max(u.ArraySize, 1) - el);
        if (u.IsSampler)
            for (int i = 0; i < n; i++)
                if (value(i) is < 0 or >= TextureUnits) { Err(c, Gl.INVALID_VALUE); return; }
        for (int i = 0; i < n * comps; i++) u.Values[el * comps + i] = value(i);
    }

    void UniformF(int loc, int comps, params float[] v) => SetUniform(loc, 1, comps, false, i => v[i]);
    void UniformI(int loc, int comps, params int[] v) => SetUniform(loc, 1, comps, true, i => v[i]);
    void UniformFv(int loc, int count, int comps, uint p) => SetUniform(loc, count, comps, false, i => GuestMemory.Read<float>(p + (uint)i * 4));
    void UniformIv(int loc, int count, int comps, uint p) => SetUniform(loc, count, comps, true, i => (int)GuestMemory.R32(p + (uint)i * 4));

    [Hle] void glUniform1f(int location, float x) => UniformF(location, 1, x);
    [Hle] void glUniform2f(int location, float x, float y) => UniformF(location, 2, x, y);
    [Hle] void glUniform3f(int location, float x, float y, float z) => UniformF(location, 3, x, y, z);
    [Hle] void glUniform4f(int location, float x, float y, float z, float w) => UniformF(location, 4, x, y, z, w);
    [Hle] void glUniform1i(int location, int x) => UniformI(location, 1, x);
    [Hle] void glUniform2i(int location, int x, int y) => UniformI(location, 2, x, y);
    [Hle] void glUniform3i(int location, int x, int y, int z) => UniformI(location, 3, x, y, z);
    [Hle] void glUniform4i(int location, int x, int y, int z, int w) => UniformI(location, 4, x, y, z, w);
    [Hle] void glUniform1fv(int location, int count, uint v) => UniformFv(location, count, 1, v);
    [Hle] void glUniform2fv(int location, int count, uint v) => UniformFv(location, count, 2, v);
    [Hle] void glUniform3fv(int location, int count, uint v) => UniformFv(location, count, 3, v);
    [Hle] void glUniform4fv(int location, int count, uint v) => UniformFv(location, count, 4, v);
    [Hle] void glUniform1iv(int location, int count, uint v) => UniformIv(location, count, 1, v);
    [Hle] void glUniform2iv(int location, int count, uint v) => UniformIv(location, count, 2, v);
    [Hle] void glUniform3iv(int location, int count, uint v) => UniformIv(location, count, 3, v);
    [Hle] void glUniform4iv(int location, int count, uint v) => UniformIv(location, count, 4, v);

    /// <summary>glUniformMatrix*fv: GLES 2.0 requires transpose = GL_FALSE. A Cg matrix parameter is n rows of n
    /// components; the values are stored as given (column-major GL data is what the shader's mul() order expects).</summary>
    void UniformMatrix(int location, int count, bool transpose, uint value, int n)
    {
        if (Cur is not { } c) return;
        if (count < 0 || transpose) { Err(c, Gl.INVALID_VALUE); return; }
        if (UniformAt(c, location, out _) is not var (u, el)) return;
        if (u.Components != n || u.IsInt) { Err(c, Gl.INVALID_OPERATION); return; }
        // the location's element is a row of the GXP parameter (a matrix is n rows of n components)
        int start = el * n;
        int floats = Math.Min(count * n * n, u.Values.Length - start);
        for (int i = 0; i < floats; i++) u.Values[start + i] = GuestMemory.Read<float>(value + (uint)i * 4);
    }

    [Hle] void glUniformMatrix2fv(int location, int count, bool transpose, uint value) => UniformMatrix(location, count, transpose, value, 2);
    [Hle] void glUniformMatrix3fv(int location, int count, bool transpose, uint value) => UniformMatrix(location, count, transpose, value, 3);
    [Hle] void glUniformMatrix4fv(int location, int count, bool transpose, uint value) => UniformMatrix(location, count, transpose, value, 4);

    void GetUniform(uint program, int location, uint dst, bool asInt)
    {
        if (Cur is not { } c) return;
        if (ProgramOf(c, program) is not { } p) return;
        if (!p.Linked) { Err(c, Gl.INVALID_OPERATION); return; }
        int idx = location >> 16, el = location & 0xFFFF;
        if (location < 0 || idx >= p.Uniforms.Count || el >= Math.Max(p.Uniforms[idx].ArraySize, 1)) { Err(c, Gl.INVALID_OPERATION); return; }
        var u = p.Uniforms[idx];
        for (int i = 0; i < u.Components && dst != 0; i++)
        {
            float v = u.Values[el * u.Components + i];
            if (asInt) GuestMemory.W32(dst + (uint)i * 4, (uint)(int)v); else GuestMemory.Write(dst + (uint)i * 4, v);
        }
    }

    [Hle] void glGetUniformfv(uint program, int location, uint pparams) => GetUniform(program, location, pparams, false);
    [Hle] void glGetUniformiv(uint program, int location, uint pparams) => GetUniform(program, location, pparams, true);
}
