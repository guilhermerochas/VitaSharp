using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// What the code precompilation knows about one loaded module's code: where its functions start (the exception index,
/// <see cref="CodePrecompiler.FunctionStarts"/>) and the pointers the module holds (its relocations that write an
/// absolute address, <see cref="LoadedModule.AbsoluteRelocations"/>; an executable loaded at its fixed address has none,
/// and its pointers are read from its bytes instead). A pointer into the module's code is a function pointer: a vtable
/// slot, a callback table entry, a function address loaded with MOVW/MOVT or from a literal pool. Those functions are
/// reached only by computed branches, which the static flow never follows; when a function runs, the functions it
/// references (directly, or through a table of pointers it references, like the vtable a constructor stores) are likely
/// to run soon, so they join the frontier (<see cref="PointersOfFunction"/>). Translating every function some table
/// names before the game starts was measured and rejected: in Killzone: Mercenary 30.9k functions, 140k blocks and
/// 42 MB of code of which 2.8% ran, and the full arena then starved the frontier.
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

    /// <summary>Where the function starting at <paramref name="function"/> ends: the next function start (or 64 KiB on).</summary>
    public uint FunctionEnd(uint function)
    {
        int at = Array.BinarySearch(_functionStarts, function);
        if (at < 0)
        {
            at = ~at - 1;
        }

        return at + 1 < _functionStarts.Length && at + 1 >= 0 ? _functionStarts[at + 1] : function + 0x10000;
    }

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
    /// references, and the function pointers of the tables it references (consecutive pointer slots from the
    /// referenced address, like the vtable a constructor stores). The references are the addresses in its range (up to
    /// the next function start: its code and literal pool): the words its relocations write and, for a module loaded
    /// at a fixed address (an executable has no relocations), the aligned words of its literal pool and the addresses
    /// its MOVW/MOVT pairs build.
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
            Referenced(_values[i], targets);
        }

        if (!GuestMemory.IsMapped(function) || !GuestMemory.IsMapped(end - 1) || end - function > 0x10000)
        {
            return;
        }

        // literal pool words
        for (uint word = (function + 3) & ~3u; word + 4 <= end; word += 4)
        {
            Referenced(GuestMemory.R32(word), targets);
        }

        // MOVW Rd, #low then MOVT Rd, #high a few instructions later (Thumb-2 encoding T3/T1, at any halfword, and
        // the ARM encoding A2/A1 at words): a misread pair only names an address that is checked like any other
        for (uint half = function; half + 4 <= end; half += 2)
        {
            if (ThumbMovw(half, out uint register, out uint low))
            {
                for (uint next = half + 4; next + 4 <= end && next <= half + 4 + MovtWindow; next += 2)
                {
                    if (ThumbMovt(next, register, out uint high))
                    {
                        Referenced(low | (high << 16), targets);
                        break;
                    }
                }
            }

            if ((half & 3) == 0 && ArmMovw(GuestMemory.R32(half), out register, out low))
            {
                for (uint next = half + 4; next + 4 <= end && next <= half + 4 + MovtWindow; next += 4)
                {
                    uint word = GuestMemory.R32(next);
                    if ((word & 0x0FF00000) == 0x03400000 && ((word >> 12) & 0xF) == register)
                    {
                        Referenced(low | ((((word >> 4) & 0xF000) | (word & 0xFFF)) << 16), targets);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>How far after a MOVW (bytes) its MOVT may be (compilers schedule other instructions between them).</summary>
    private const uint MovtWindow = 32;

    /// <summary>An address the function references: a function, or a table of function pointers.</summary>
    private void Referenced(uint value, List<(uint Pc, bool Thumb)> targets)
    {
        if (IsFunction(value, out (uint Pc, bool Thumb) target))
        {
            targets.Add(target);
        }
        else if (InModule(value))
        {
            TableAt(value, targets);
        }
    }

    /// <summary>Whether <paramref name="address"/> is in one of the module's segments.</summary>
    private bool InModule(uint address)
    {
        foreach (KeyValuePair<int, SegmentInfo> segment in _module.Segments)
        {
            if (address >= segment.Value.Address && address - segment.Value.Address < segment.Value.Size)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ThumbMovw(uint at, out uint register, out uint value) => ThumbMoveWide(at, 0xF240, out register, out value);

    private static bool ThumbMovt(uint at, uint register, out uint value) => ThumbMoveWide(at, 0xF2C0, out uint found, out value) && found == register;

    /// <summary>A Thumb-2 MOVW/MOVT (selected by <paramref name="opcode"/>) at <paramref name="at"/>: its register and immediate.</summary>
    private static bool ThumbMoveWide(uint at, int opcode, out uint register, out uint value)
    {
        ushort first = GuestMemory.Read<ushort>(at);
        ushort second = GuestMemory.Read<ushort>(at + 2);
        register = (uint)(second >> 8) & 0xF;
        value = ((uint)(first & 0xF) << 12) | ((uint)(first >> 10) & 1) << 11 | ((uint)(second >> 12) & 7) << 8 | (uint)(second & 0xFF);
        return (first & 0xFBF0) == opcode && (second & 0x8000) == 0;
    }

    /// <summary>An ARM MOVW (A2): its register and immediate.</summary>
    private static bool ArmMovw(uint word, out uint register, out uint value)
    {
        register = (word >> 12) & 0xF;
        value = ((word >> 4) & 0xF000) | (word & 0xFFF);
        return (word & 0x0FF00000) == 0x03000000 && (word >> 28) != 0xF;
    }

    /// <summary>The function pointers of the run of pointer slots that starts at <paramref name="address"/>: the
    /// relocated slots of a relocatable module, else the words in memory, up to the first that is not a function.</summary>
    private void TableAt(uint address, List<(uint Pc, bool Thumb)> targets)
    {
        int i = LowerBound(_sites, address);
        if (i < _sites.Length && _sites[i] == address)
        {
            for (int k = 0; k < MaxTableEntries && i < _sites.Length && _sites[i] == address + 4 * (uint)k; k++, i++)
            {
                if (IsFunction(_values[i], out (uint Pc, bool Thumb) target))
                {
                    targets.Add(target);
                }
            }

            return;
        }

        if ((address & 3) != 0)
        {
            return;
        }

        for (uint k = 0; k < MaxTableEntries && GuestMemory.IsMapped(address + 4 * k + 3); k++)
        {
            if (!IsFunction(GuestMemory.R32(address + 4 * k), out (uint Pc, bool Thumb) target))
            {
                return;
            }

            targets.Add(target);
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
