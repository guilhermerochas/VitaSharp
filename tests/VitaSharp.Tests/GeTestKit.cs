using System.Buffers.Binary;
using VitaSharp.Core.Compat.Ge;

namespace VitaSharp.Tests;

/// <summary>The PSP memory for the GE tests: 4 MB of RAM at physical 0x08000000 and the 2 MB VRAM at 0x04000000.</summary>
public sealed class GeTestMemory : IGeMemory
{
    public const uint RamBase = 0x08000000, VramBase = 0x04000000;

    private readonly byte[] _ram = new byte[0x400000];
    private readonly byte[] _vram = new byte[0x200000];

    public Span<byte> Span(uint physical, int length)
    {
        physical &= 0x1FFFFFFF;
        if (physical >= RamBase && physical - RamBase + (uint)length <= _ram.Length)
        {
            return _ram.AsSpan((int)(physical - RamBase), length);
        }

        if (physical >= VramBase && physical - VramBase + (uint)length <= _vram.Length)
        {
            return _vram.AsSpan((int)(physical - VramBase), length);
        }

        return [];
    }

    public uint Read32(uint physical)
    {
        Span<byte> word = Span(physical, 4);
        return word.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(word) : 0;
    }

    public void Write32(uint physical, uint value)
    {
        Span<byte> word = Span(physical, 4);
        if (word.Length == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, value);
        }
    }

    public void Write16(uint physical, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Span(physical, 2), value);
    }

    public ushort Read16(uint physical)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(Span(physical, 2));
    }

    public void WriteFloat(uint physical, float value)
    {
        Write32(physical, BitConverter.SingleToUInt32Bits(value));
    }
}

/// <summary>Builds a display list in <see cref="GeTestMemory"/> (our own command words).</summary>
public sealed class GeListBuilder(GeTestMemory memory, uint start)
{
    public uint Start { get; } = start;

    public uint Position { get; private set; } = start;

    public GeListBuilder Command(int command, uint argument = 0)
    {
        memory.Write32(Position, ((uint)command << 24) | (argument & 0xFFFFFF));
        Position += 4;
        return this;
    }

    /// <summary>A 24-bit float argument (the top 24 bits of the float).</summary>
    public GeListBuilder Float(int command, float value)
    {
        return Command(command, BitConverter.SingleToUInt32Bits(value) >> 8);
    }

    /// <summary>BASE for addresses in RAM (bits 24..27 of 0x08000000).</summary>
    public GeListBuilder RamBase()
    {
        return Command(GeCommand.Base, 0x080000);
    }

    public GeListBuilder End()
    {
        return Command(GeCommand.Finish).Command(GeCommand.End);
    }
}

/// <summary>A rasterizer that keeps what it was given (the surfaces of each draw, the flushes) and can paint the
/// colour surface in memory as a GPU would.</summary>
public sealed class GeCapturingRasterizer : IGeRasterizer
{
    public readonly List<(GeTopology Topology, GeVertex[] Vertices)> Draws = [];

    public GeRenderState? LastState;

    public GeSurface LastColor;

    public GeSurface? LastDepth;

    public int Flushes;

    public bool Dirty;

    public Action<GeSurface, IGeMemory>? Paint;

    public void Draw(GeTopology topology, ReadOnlySpan<GeVertex> vertices, GeRenderState state, GeSurface color, GeSurface? depth, IGeMemory memory)
    {
        Draws.Add((topology, vertices.ToArray()));
        LastState = state;
        LastColor = color;
        LastDepth = depth;
        Dirty = true;
        Paint?.Invoke(color, memory);
    }

    public void Flush(IGeMemory memory)
    {
        Flushes++;
        Dirty = false;
    }

    public bool Holds(uint address, int length)
    {
        return Dirty && LastColor.Overlaps(address, length);
    }

    public void Invalidate(uint address, int length)
    {
    }
}

/// <summary>GE register offsets for the tests (PSP physical).</summary>
public static class GeRegisters
{
    public const uint Base = GeEngine.PhysicalBase;
    public const uint Exec = Base + 0x100, List = Base + 0x108, Stall = Base + 0x10C, Return1 = Base + 0x110,
        Return2 = Base + 0x114, Vertex = Base + 0x118, Index = Base + 0x11C, Origin = Base + 0x120,
        InterruptStatus = Base + 0x304, InterruptEnable = Base + 0x308, InterruptAcknowledge = Base + 0x310;

    public static void Run(GeEngine engine, uint list, uint stall = 0)
    {
        engine.Write32(List, list);
        engine.Write32(Stall, stall);
        engine.Write32(Exec, GeEngine.ExecRunning);
    }
}
