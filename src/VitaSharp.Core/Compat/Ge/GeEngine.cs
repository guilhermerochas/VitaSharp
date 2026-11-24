using VitaSharp.Core.Util;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The PSP GE as the compat block of the Vita SoC has it (docs/notes/psp-compat.md, "The GE"): the register block at
/// PSP 0xBD400000 and the EDRAM controller at 0xBD500000 (layout from uofw's hardware.h), and the display list
/// processor. Writing EXEC bit 0 runs the list at LISTADDR until END, or until it reaches STALLADDR (it then waits,
/// EXEC still set, and goes on when STALLADDR moves); CALL keeps two return levels (RADR1/2, OADR1/2, EXEC bits 8 and
/// 9); SIGNAL, END and FINISH set their interrupt status bits (1, 2, 4; 8 for an error), and the interrupt line is
/// high while a status bit is set that the accepted mask (+0x308) lets through. Every command's last word is kept at
/// +0x800 + cmd * 4, the matrices at +0xC00.. (the top 24 bits of each float).
/// <para>
/// The list runs when it is started or its stall address moves, inside that register write (the CPU that started it
/// sees it running until END, as with the hardware's own pace; a host thread for the GE is later work).
/// </para>
/// </summary>
public sealed partial class GeEngine : IDisposable
{
    /// <summary>The register block (PSP physical) and the EDRAM controller's.</summary>
    public const uint PhysicalBase = 0x1D400000, EdramPhysicalBase = 0x1D500000, BlockSize = 0x1000;

    /// <summary>EXEC bits.</summary>
    public const uint ExecRunning = 0x001, ExecBranchFalse = 0x002, ExecDepth1 = 0x100, ExecDepth2 = 0x200;

    /// <summary>Interrupt status bits.</summary>
    public const uint InterruptSignal = 1, InterruptEnd = 2, InterruptFinish = 4, InterruptError = 8;

    /// <summary>The VRAM size (2 MB) as +0x008 reports it, in KB.</summary>
    public const uint VramSizeKb = 0x800;

    /// <summary>The most commands one start runs before it stops on its own (a list looping on itself): it then
    /// keeps running at the next register write or <see cref="Continue"/>.</summary>
    public const int CommandBudget = 1 << 22;

    private const int RegisterReset = 0x000, RegisterUnknown004 = 0x004, RegisterEdramHardwareSize = 0x008,
        RegisterExec = 0x100, RegisterUnknown104 = 0x104, RegisterList = 0x108, RegisterStall = 0x10C,
        RegisterReturn1 = 0x110, RegisterReturn2 = 0x114, RegisterVertex = 0x118, RegisterIndex = 0x11C,
        RegisterOrigin = 0x120, RegisterOrigin1 = 0x124, RegisterOrigin2 = 0x128, RegisterGeometryClock = 0x200,
        RegisterUnknown300 = 0x300, RegisterInterruptStatus = 0x304, RegisterInterruptEnable = 0x308,
        RegisterInterruptClear = 0x30C, RegisterInterruptAcknowledge = 0x310, RegisterEdramEnabledSize = 0x400,
        RegisterCommands = 0x800, RegisterBones = 0xC00, RegisterWorld = 0xD80, RegisterView = 0xDB0,
        RegisterProjection = 0xDE0, RegisterTexGen = 0xE20, RegisterMatricesEnd = 0xE50;

    private readonly IGeMemory _memory;
    private readonly object _lock = new();
    private readonly uint[] _commands = new uint[256];
    private readonly uint[] _edram = new uint[BlockSize / 4];
    private readonly float[] _bones = new float[8 * 12];
    private readonly float[] _world = new float[12];
    private readonly float[] _view = new float[12];
    private readonly float[] _projection = new float[16];
    private readonly float[] _texGen = new float[12];
    private readonly uint[] _returnAddresses = new uint[2];
    private readonly uint[] _savedOrigins = new uint[2];

    private bool _running;
    private bool _branchFalse;
    private int _depth;
    private uint _list;
    private uint _stall;
    private uint _vertexAddress;
    private uint _indexAddress;
    private uint _origin;
    private uint _unknown004;
    private uint _unknown104;
    private uint _unknown300;
    private uint _geometryClock;
    private uint _interruptStatus;
    private uint _interruptEnable;
    private uint _edramEnabledSize;
    private bool _level;
    private int _boneIndex;
    private int _worldIndex;
    private int _viewIndex;
    private int _projectionIndex;
    private int _texGenIndex;
    private int _executing;
    private bool _budgetSpent;

    public GeEngine(IGeMemory memory)
    {
        _memory = memory;
    }

    /// <summary>The GE's interrupt line changed (true: high). Called outside the GE's lock, on the thread that ran
    /// the list. The PSP's interrupt controller takes it as interrupt 25.</summary>
    public Action<bool>? InterruptLevel { get; set; }

    /// <summary>The memory the GE uses.</summary>
    public IGeMemory Memory => _memory;

    /// <summary>A list runs (EXEC bit 0).</summary>
    public bool Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>The list address (where the GE is or stopped).</summary>
    public uint ListAddress
    {
        get
        {
            lock (_lock)
            {
                return _list;
            }
        }
    }

    /// <summary>The last word of a command (the register file +0x800).</summary>
    public uint Command(int command)
    {
        return Volatile.Read(ref _commands[command & 0xFF]);
    }

    /// <summary>The world, view, projection and texgen matrices (4x3, 4x3, 4x4, 4x3, column after column as the
    /// GE loads them) and the bones (8 of 4x3).</summary>
    public ReadOnlySpan<float> WorldMatrix => _world;

    public ReadOnlySpan<float> ViewMatrix => _view;

    public ReadOnlySpan<float> ProjectionMatrix => _projection;

    public ReadOnlySpan<float> TexGenMatrix => _texGen;

    public ReadOnlySpan<float> BoneMatrices => _bones;

    /// <summary>The vertex and index addresses (the registers +0x118 / +0x11C).</summary>
    public uint VertexAddress => _vertexAddress;

    public uint IndexAddress => _indexAddress;

    /// <summary>A read of the register block or the EDRAM controller (PSP physical address).</summary>
    public uint Read32(uint physical)
    {
        if (physical - EdramPhysicalBase < BlockSize)
        {
            uint offset = physical - EdramPhysicalBase;
            lock (_lock)
            {
                return _edram[offset / 4];
            }
        }

        return ReadRegister(physical - PhysicalBase);
    }

    /// <summary>A write of the register block or the EDRAM controller (PSP physical address).</summary>
    public void Write32(uint physical, uint value)
    {
        if (physical - EdramPhysicalBase < BlockSize)
        {
            WriteEdram(physical - EdramPhysicalBase, value);
            return;
        }

        WriteRegister(physical - PhysicalBase, value);
    }

    /// <summary>Runs a list that stopped on its command budget (a list looping on itself) a while longer.</summary>
    public void Continue()
    {
        Execute();
    }

    private uint ReadRegister(uint offset)
    {
        lock (_lock)
        {
            switch (offset)
            {
                case RegisterReset:
                    return 0;
                case RegisterUnknown004:
                    return _unknown004;
                case RegisterEdramHardwareSize:
                    return VramSizeKb;
                case RegisterExec:
                    return ExecValue();
                case RegisterUnknown104:
                    return _unknown104;
                case RegisterList:
                    return _list;
                case RegisterStall:
                    return _stall;
                case RegisterReturn1:
                    return _returnAddresses[0];
                case RegisterReturn2:
                    return _returnAddresses[1];
                case RegisterVertex:
                    return _vertexAddress;
                case RegisterIndex:
                    return _indexAddress;
                case RegisterOrigin:
                    return _origin;
                case RegisterOrigin1:
                    return _savedOrigins[0];
                case RegisterOrigin2:
                    return _savedOrigins[1];
                case RegisterGeometryClock:
                    return _geometryClock;
                case RegisterUnknown300:
                    return _unknown300;
                case RegisterInterruptStatus:
                    return _interruptStatus;
                case RegisterInterruptEnable:
                    return _interruptEnable;
                case RegisterEdramEnabledSize:
                    return _edramEnabledSize;
            }

            if (offset >= RegisterCommands && offset < RegisterBones)
            {
                return _commands[(offset - RegisterCommands) / 4];
            }

            if (offset >= RegisterBones && offset < RegisterMatricesEnd)
            {
                return MatrixRegister(offset);
            }

            return 0;
        }
    }

    private uint ExecValue()
    {
        uint value = _running ? ExecRunning : 0;
        if (_branchFalse)
        {
            value |= ExecBranchFalse;
        }

        if (_depth >= 1)
        {
            value |= ExecDepth1;
        }

        if (_depth >= 2)
        {
            value |= ExecDepth2;
        }

        return value;
    }

    private uint MatrixRegister(uint offset)
    {
        (float[] matrix, uint start) = offset switch
        {
            < RegisterWorld => (_bones, (uint)RegisterBones),
            < RegisterView => (_world, (uint)RegisterWorld),
            < RegisterProjection => (_view, (uint)RegisterView),
            < RegisterTexGen => (_projection, (uint)RegisterProjection),
            _ => (_texGen, (uint)RegisterTexGen),
        };
        return BitConverter.SingleToUInt32Bits(matrix[(offset - start) / 4]) >> 8;
    }

    private void WriteRegister(uint offset, uint value)
    {
        bool start = false;
        bool resume = false;
        lock (_lock)
        {
            switch (offset)
            {
                case RegisterReset:
                    if ((value & 1) != 0)
                    {
                        ResetState();
                    }

                    break;
                case RegisterUnknown004:
                    _unknown004 = value;
                    break;
                case RegisterExec:
                    if ((value & ExecRunning) != 0 && !_running)
                    {
                        _running = true;
                        start = true;
                    }
                    else if ((value & ExecRunning) == 0)
                    {
                        _running = false;
                    }

                    break;
                case RegisterUnknown104:
                    _unknown104 = value;
                    break;
                case RegisterList:
                    _list = value & AddressMask;
                    break;
                case RegisterStall:
                    _stall = value & AddressMask;
                    resume = _running;
                    break;
                case RegisterReturn1:
                    _returnAddresses[0] = value & AddressMask;
                    break;
                case RegisterReturn2:
                    _returnAddresses[1] = value & AddressMask;
                    break;
                case RegisterVertex:
                    _vertexAddress = value & AddressMask;
                    break;
                case RegisterIndex:
                    _indexAddress = value & AddressMask;
                    break;
                case RegisterOrigin:
                    _origin = value;
                    break;
                case RegisterOrigin1:
                    _savedOrigins[0] = value;
                    break;
                case RegisterOrigin2:
                    _savedOrigins[1] = value;
                    break;
                case RegisterGeometryClock:
                    _geometryClock = value;
                    break;
                case RegisterUnknown300:
                    _unknown300 = value;
                    break;
                case RegisterInterruptEnable:
                    _interruptEnable = value & 0xF;
                    break;
                case RegisterInterruptClear:
                case RegisterInterruptAcknowledge:
                    _interruptStatus &= ~value;
                    break;
                case RegisterEdramEnabledSize:
                    _edramEnabledSize = value;
                    break;
            }
        }

        UpdateInterruptLine();
        if (start || resume)
        {
            Execute();
        }
    }

    private void WriteEdram(uint offset, uint value)
    {
        lock (_lock)
        {
            // +0x10 bit 0 starts the EDRAM initialisation, which the emulated EDRAM finishes at once (reads 0)
            _edram[offset / 4] = offset == 0x10 ? value & ~1u : value;
        }
    }

    /// <summary>The reset bit: the GE back to its power-on state, the accepted interrupts too (ge.prx writes
    /// SIGNAL|END|FINISH to +0x308 again after every reset, uofw's sceGeInit/reset path).</summary>
    private void ResetState()
    {
        _running = false;
        _branchFalse = false;
        _depth = 0;
        _interruptStatus = 0;
        _interruptEnable = 0;
        _list = 0;
        _stall = 0;
        _vertexAddress = 0;
        _indexAddress = 0;
        _origin = 0;
        Array.Clear(_returnAddresses);
        Array.Clear(_savedOrigins);
        Array.Clear(_commands);
    }

    /// <summary>PSP physical addresses as the GE keeps them.</summary>
    private const uint AddressMask = 0x1FFFFFFF;

    /// <summary>A list address of a JUMP/CALL/VADDR...: BASE's bits 16..19 above the 24-bit argument, plus the
    /// origin (OFFSETADDR / ORIGIN).</summary>
    private uint Relative(uint argument)
    {
        uint extended = ((_commands[GeCommand.Base] & 0x000F0000) << 8) | (argument & 0x00FFFFFF);
        return (_origin + extended) & 0x0FFFFFFF;
    }

    /// <summary>Runs the list from LISTADDR until END, the stall address or the command budget, on one thread at a
    /// time: a start or stall move while another thread runs the list is seen by that thread before it stops.</summary>
    private void Execute()
    {
        while (Interlocked.CompareExchange(ref _executing, 1, 0) == 0)
        {
            try
            {
                ExecuteCommands();
            }
            finally
            {
                Volatile.Write(ref _executing, 0);
            }

            lock (_lock)
            {
                if (!_running || (_stall != 0 && _list == _stall) || _budgetSpent)
                {
                    return;
                }
            }
        }
    }


    private void ExecuteCommands()
    {
        _budgetSpent = false;
        for (int executed = 0; executed < CommandBudget; executed++)
        {
            uint word;
            uint address;
            lock (_lock)
            {
                if (!_running || (_stall != 0 && _list == _stall))
                {
                    return;
                }

                address = _list;
                _list = (address + 4) & AddressMask;
            }

            word = _memory.Read32(address);
            if (!Run(word, address))
            {
                return;
            }
        }

        _budgetSpent = true;
    }

    /// <summary>One command; false when the list stopped.</summary>
    private bool Run(uint word, uint address)
    {
        int command = (int)(word >> 24);
        uint argument = word & 0x00FFFFFF;
        Volatile.Write(ref _commands[command], word);
        switch (command)
        {
            case GeCommand.VertexAddress:
                _vertexAddress = Relative(argument);
                return true;
            case GeCommand.IndexAddress:
                _indexAddress = Relative(argument);
                return true;
            case GeCommand.Primitive:
                DrawPrimitive((int)((argument >> 16) & 7), (int)(argument & 0xFFFF));
                return true;
            case GeCommand.Bezier:
            case GeCommand.Spline:
                Log.WarnOnce("ge-patches", "GE: Bezier/spline patches are not drawn yet");
                return true;
            case GeCommand.BoundingBox:
                _branchFalse = false; // every box is taken as visible: BJUMP never branches
                return true;
            case GeCommand.Jump:
                lock (_lock)
                {
                    _list = Relative(argument & 0x00FFFFFC);
                }

                return true;
            case GeCommand.BoundingBoxJump:
                if (_branchFalse)
                {
                    lock (_lock)
                    {
                        _list = Relative(argument & 0x00FFFFFC);
                    }
                }

                return true;
            case GeCommand.Call:
                return Call(argument);
            case GeCommand.Return:
                return Return();
            case GeCommand.End:
                lock (_lock)
                {
                    _running = false;
                    _interruptStatus |= InterruptEnd;
                }

                UpdateInterruptLine();
                return false;
            case GeCommand.Signal:
                RaiseStatus(InterruptSignal);
                return true;
            case GeCommand.Finish:
                RaiseStatus(InterruptFinish);
                return true;
            case GeCommand.OffsetAddress:
                _origin = argument << 8;
                return true;
            case GeCommand.Origin:
                _origin = address;
                return true;
            case GeCommand.BoneMatrixNumber:
                _boneIndex = (int)(argument & 0x7F);
                return true;
            case GeCommand.BoneMatrixData:
                LoadMatrix(_bones, ref _boneIndex, argument);
                return true;
            case GeCommand.WorldMatrixNumber:
                _worldIndex = (int)(argument & 0xF);
                return true;
            case GeCommand.WorldMatrixData:
                LoadMatrix(_world, ref _worldIndex, argument);
                return true;
            case GeCommand.ViewMatrixNumber:
                _viewIndex = (int)(argument & 0xF);
                return true;
            case GeCommand.ViewMatrixData:
                LoadMatrix(_view, ref _viewIndex, argument);
                return true;
            case GeCommand.ProjectionMatrixNumber:
                _projectionIndex = (int)(argument & 0xF);
                return true;
            case GeCommand.ProjectionMatrixData:
                LoadMatrix(_projection, ref _projectionIndex, argument);
                return true;
            case GeCommand.TexGenMatrixNumber:
                _texGenIndex = (int)(argument & 0xF);
                return true;
            case GeCommand.TexGenMatrixData:
                LoadMatrix(_texGen, ref _texGenIndex, argument);
                return true;
            default:
                OnStateCommand(command, argument);
                return true;
        }
    }

    private bool Call(uint argument)
    {
        lock (_lock)
        {
            if (_depth >= 2)
            {
                _running = false;
                _interruptStatus |= InterruptError;
            }
            else
            {
                _returnAddresses[_depth] = _list;
                _savedOrigins[_depth] = _origin;
                _depth++;
                _list = Relative(argument & 0x00FFFFFC);
                return true;
            }
        }

        UpdateInterruptLine();
        return false;
    }

    private bool Return()
    {
        lock (_lock)
        {
            if (_depth <= 0)
            {
                _running = false;
                _interruptStatus |= InterruptError;
            }
            else
            {
                _depth--;
                _list = _returnAddresses[_depth];
                _origin = _savedOrigins[_depth];
                return true;
            }
        }

        UpdateInterruptLine();
        return false;
    }

    private static void LoadMatrix(float[] matrix, ref int index, uint argument)
    {
        if (index < matrix.Length)
        {
            matrix[index] = BitConverter.UInt32BitsToSingle(argument << 8);
        }

        index++;
    }

    private void RaiseStatus(uint bits)
    {
        lock (_lock)
        {
            _interruptStatus |= bits;
        }

        UpdateInterruptLine();
    }

    private void UpdateInterruptLine()
    {
        bool level;
        bool changed;
        lock (_lock)
        {
            level = (_interruptStatus & _interruptEnable) != 0;
            changed = level != _level;
            _level = level;
        }

        if (changed)
        {
            InterruptLevel?.Invoke(level);
        }
    }

    /// <summary>PRIM: the batch (drawn by <see cref="DrawBatch"/>), then the vertex or index address moves past it.</summary>
    private void DrawPrimitive(int primitive, int count)
    {
        GeVertexFormat format = new(_commands[GeCommand.VertexType]);
        DrawBatch(primitive, count, format);
        if (format.IndexSize != 0)
        {
            _indexAddress = (_indexAddress + (uint)(count * format.IndexSize)) & AddressMask;
        }
        else
        {
            _vertexAddress = (_vertexAddress + (uint)(count * format.Size)) & AddressMask;
        }
    }

    /// <summary>Draws one batch (the rasterizer's part, next to the state).</summary>
    partial void DrawBatch(int primitive, int count, GeVertexFormat format);

    /// <summary>A command that only sets state the draws read (the rest of the commands).</summary>
    partial void OnStateCommand(int command, uint argument);
}
