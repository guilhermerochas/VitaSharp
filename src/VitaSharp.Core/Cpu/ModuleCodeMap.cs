using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// What the code precompilation knows about one loaded module's code: where its functions start (the exception index,
/// <see cref="CodePrecompiler.FunctionStarts"/>) and the pointers the module holds (its relocations that write an
/// absolute address, <see cref="LoadedModule.AbsoluteRelocations"/>). A relocation pointing into the module's code is a
/// function pointer: a vtable slot, a callback table entry, a function address loaded with MOVW/MOVT or from a literal
/// pool. Those functions are reached only by computed branches, which the static flow never follows; when a function
/// runs, the functions it references (directly, or through a table of pointers it references, like the vtable a
/// constructor stores) are likely to run soon, so they join the frontier (<see cref="PointersOfFunction"/>).
/// </summary>
public sealed class ModuleCodeMap
{
    /// <summary>The most consecutive pointers of one table (a vtable, a callback table) taken from one reference.</summary>
    public const int MaxTableEntries = 128;

    private readonly LoadedModule _module;
    private readonly uint[] _functionStarts;
    private readonly uint[] _sites;
    private readonly uint[] _values;

    private ModuleCodeMap(LoadedModule module, uint[] functionStarts, uint[] sites, uint[] values)
    {
        _module = module;
        _functionStarts = functionStarts;
        _sites = sites;
        _values = values;
    }

    public LoadedModule Module => _module;

    /// <summary>Relocations that point into the module's code and look like a function start (statistics, tests).</summary>
    public int CodePointerCount
    {
        get
        {
            int count = 0;
            foreach (uint value in _values)
            {
                if (IsFunction(value, out _))
                {
                    count++;
                }
            }

            return count;
        }
    }

    public static ModuleCodeMap Build(LoadedModule module)
    {
        List<(uint Site, uint Value)> relocations = module.AbsoluteRelocations;
        uint[] sites = new uint[relocations.Count];
        uint[] values = new uint[relocations.Count];
        for (int i = 0; i < relocations.Count; i++)
        {
            sites[i] = relocations[i].Site;
            values[i] = relocations[i].Value;
        }

        Array.Sort(sites, values);
        return new ModuleCodeMap(module, CodePrecompiler.FunctionStarts(module), sites, values);
    }

    /// <summary>Whether <paramref name="address"/> is in the module's code.</summary>
    public bool InCode(uint address) => CodePrecompiler.InCode(_module, address);

    /// <summary>The start of the function containing <paramref name="address"/> (exception index), or 0.</summary>
    public uint FunctionContaining(uint address) => CodePrecompiler.FunctionContaining(_functionStarts, address);

    /// <summary>
    /// Whether a pointer value names a function of the module: in its code, and either bit 0 set (Thumb) at a known
    /// function start or a function prologue (the code segment also holds read-only data, and a pointer to a string
    /// can be odd), or bit 0 clear at an ARM prologue (STMFD SP!, {..., LR}).
    /// </summary>
    public bool IsFunction(uint value, out (uint Pc, bool Thumb) target)
    {
        uint address = value & ~1u;
        bool thumb = (value & 1) != 0;
        target = (address, thumb);
        if (!InCode(address) || !GuestMemory.IsMapped(address) || !GuestMemory.IsMapped(address + 3))
        {
            return false;
        }

        if (thumb)
        {
            if (Array.BinarySearch(_functionStarts, address) >= 0)
            {
                return true;
            }

            ushort first = GuestMemory.Read<ushort>(address);
            return (first & 0xFF00) == 0xB500 || first == 0xE92D; // PUSH {..., LR} / PUSH.W {...}
        }

        return (address & 3) == 0 && (GuestMemory.R32(address) & 0xFFFF4000) == 0xE92D4000;
    }

    /// <summary>
    /// Adds to <paramref name="targets"/> the functions the function starting at <paramref name="function"/>
    /// references: the pointers written into its range (up to the next function start: its code and literal pool) that
    /// name a function, and the function pointers of the tables it references (consecutive pointer slots from the
    /// referenced address, like a vtable).
    /// </summary>
    public void PointersOfFunction(uint function, List<(uint Pc, bool Thumb)> targets)
    {
        int at = Array.BinarySearch(_functionStarts, function);
        if (at < 0)
        {
            return;
        }

        uint end = at + 1 < _functionStarts.Length ? _functionStarts[at + 1] : function + 0x10000;
        for (int i = LowerBound(_sites, function); i < _sites.Length && _sites[i] < end; i++)
        {
            uint value = _values[i];
            if (IsFunction(value, out (uint Pc, bool Thumb) target))
            {
                targets.Add(target);
            }
            else if (!InCode(value & ~1u))
            {
                TableAt(value, targets);
            }
        }
    }

    /// <summary>The function pointers of the run of pointer slots that starts at <paramref name="address"/>.</summary>
    private void TableAt(uint address, List<(uint Pc, bool Thumb)> targets)
    {
        int i = LowerBound(_sites, address);
        for (int k = 0; k < MaxTableEntries && i < _sites.Length && _sites[i] == address + 4 * (uint)k; k++, i++)
        {
            if (IsFunction(_values[i], out (uint Pc, bool Thumb) target))
            {
                targets.Add(target);
            }
        }
    }

    private static int LowerBound(uint[] sorted, uint value)
    {
        int i = Array.BinarySearch(sorted, value);
        if (i < 0)
        {
            return ~i;
        }

        while (i > 0 && sorted[i - 1] == value)
        {
            i--;
        }

        return i;
    }
}
