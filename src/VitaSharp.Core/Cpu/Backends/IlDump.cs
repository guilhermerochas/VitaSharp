using System.Reflection;
using System.Reflection.Emit;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// IL dump mode (VITASHARP_IL_DUMP=&lt;dir&gt;, developer notes in docs/notes/il-jit.md): every block the IL backend
/// compiles is emitted a second time, with the same IL, as a static method of a <see cref="PersistedAssemblyBuilder"/>
/// assembly (il_blocks_NNN.dll, 512 blocks each) that ILSpy opens and decompiles next to VitaSharp.Core.dll. The
/// dump is for reading only: the methods are never run. index.tsv lists every dumped block with its executions so
/// far (written with each assembly and when the process exits), hottest first: the names to pass to DOTNET_JitDisasm
/// for the machine code RyuJIT made of the running DynamicMethod.
/// </summary>
[System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection.Emit")]
internal sealed class IlDump
{
    /// <summary>Methods per assembly file.</summary>
    const int BatchSize = 512;

    readonly string _dir;
    readonly Lock _lock = new();
    readonly List<(Block Block, string Name, int File)> _blocks = [];
    PersistedAssemblyBuilder? _asm;
    TypeBuilder? _type;
    int _file, _inFile;

    IlDump(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        Log.Info($"[il-dump] IL of the IL backend's blocks goes to {dir} (index.tsv, il_blocks_NNN.dll)");
    }

    /// <summary>The dump when VITASHARP_IL_DUMP names a folder, else null.</summary>
    public static IlDump? Create(string? dir) => string.IsNullOrWhiteSpace(dir) ? null : new IlDump(dir);

    /// <summary>Method name of a block: also the DynamicMethod's, so DOTNET_JitDisasm finds it.</summary>
    public static string MethodName(Block b) => $"blk_{b.Start:X8}{(b.Thumb ? "t" : "a")}";

    /// <summary>Adds the block's method (<paramref name="emit"/> writes the body, as for the DynamicMethod).</summary>
    public void Add(Block b, Action<ILGenerator> emit)
    {
        lock (_lock)
        {
            try
            {
                if (_type == null)
                {
                    _asm = new PersistedAssemblyBuilder(new AssemblyName($"il_blocks_{_file:D3}"), typeof(object).Assembly);
                    _type = _asm.DefineDynamicModule($"il_blocks_{_file:D3}").DefineType("GuestBlocks", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                }
                string name = MethodName(b);
                // a block recompiled after an invalidation gets a suffix: method names stay unique in the type
                if (_blocks.Exists(x => x.Name == name && x.File == _file))
                {
                    name += $"_{_blocks.Count}";
                }

                var mb = _type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(CpuState).MakePointerType()]);
                mb.DefineParameter(1, ParameterAttributes.None, "s");
                emit(mb.GetILGenerator());
                _blocks.Add((b, name, _file));
                if (++_inFile >= BatchSize)
                {
                    SaveAssembly();
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[il-dump] {b}: {e.GetType().Name} {e.Message}");
            }
        }
    }

    void SaveAssembly()
    {
        if (_type == null || _asm == null)
        {
            return;
        }

        _type.CreateType();
        _asm.Save(Path.Combine(_dir, $"il_blocks_{_file:D3}.dll"));
        _type = null;
        _asm = null;
        _inFile = 0;
        _file++;
        WriteIndex();
    }

    /// <summary>index.tsv: method, file, guest start, ARM/Thumb, length, executions, executed instructions; hottest first.</summary>
    void WriteIndex()
    {
        var sb = new System.Text.StringBuilder("method\tfile\tstart\tmode\tinstructions\texecutions\texecuted_instructions\n");
        foreach (var (b, name, file) in _blocks.OrderByDescending(x => (long)x.Block.ExecCount * x.Block.Insts.Length))
        {
            sb.Append($"{name}\til_blocks_{file:D3}.dll\t{b.Start:X8}\t{(b.Thumb ? "T" : "A")}\t{b.Insts.Length}\t{b.ExecCount}\t{(long)b.ExecCount * b.Insts.Length}\n");
        }

        File.WriteAllText(Path.Combine(_dir, "index.tsv"), sb.ToString());
    }

    /// <summary>Saves the assembly being filled and the index (process exit; safe to call more than once).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            try
            {
                if (_inFile > 0)
                {
                    SaveAssembly();
                }
                else
                {
                    WriteIndex();
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[il-dump] flush: {e.GetType().Name} {e.Message}");
            }
        }
    }
}
