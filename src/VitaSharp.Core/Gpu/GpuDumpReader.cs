namespace VitaSharp.Core.Gpu;

/// <summary>
/// Pull reader of a GPU dump: <see cref="Next"/> moves to the next memory update, command or idle point. Program
/// descriptions are consumed internally. Spans and draw commands stay valid until the following <see cref="Next"/>.
/// </summary>
public sealed class GpuDumpReader : IDisposable
{
    readonly Stream _input;
    readonly GpuDumpCodec _codec = new();
    readonly DumpCursor _cur = new();
    readonly DrawCommand _draw = new();
    byte[] _chunk = new byte[1 << 20];
    byte[]? _scratch;

    public GpuDumpHeader Header { get; }
    public uint Version { get; }
    /// <summary>The end chunk's summary (null until the reader reached it, or when the dump was cut short).</summary>
    public GpuDumpHeader? Summary { get; private set; }

    public GpuDumpRecord Kind { get; private set; }
    /// <summary>Memory: guest address and bytes.</summary>
    public uint Address { get; private set; }
    public ReadOnlySpan<byte> Data => _cur.Data.AsSpan(_dataOffset, _dataLength);
    int _dataOffset, _dataLength;
    /// <summary>Command records: the command (draws are reused between records).</summary>
    public GpuCommand? Command { get; private set; }
    /// <summary>Present records: host microseconds since the dump started, guest frame, renderer present number.</summary>
    public (long Micros, long GuestFrame, long Present) PresentInfo { get; private set; }

    public long Records { get; private set; }
    public long CompressedBytesRead => _input.Position;

    public GpuDumpReader(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) { }

    public GpuDumpReader(Stream input)
    {
        _input = input;
        Header = GpuDumpCodec.ReadFileHeader(input, out var v);
        Version = v;
    }

    /// <summary>False at the end of the dump.</summary>
    public bool Next()
    {
        while (true)
        {
            while (_cur.AtEnd)
            {
                if (!GpuDumpCodec.ReadChunk(_input, ref _chunk, ref _scratch, out byte type, out int len)) return false;
                if (type == GpuDumpFormat.ChunkEnd) { Summary = GpuDumpHeader.Parse(System.Text.Encoding.UTF8.GetString(_chunk, 0, len)); continue; }
                if (type != GpuDumpFormat.ChunkRecords) continue; // unknown chunk types from newer writers are skipped
                _cur.Reset(_chunk, len);
            }
            var kind = (GpuDumpRecord)_cur.U8();
            Records++;
            Kind = kind;
            Command = null;
            switch (kind)
            {
                case GpuDumpRecord.Memory:
                    Address = _cur.U32();
                    _dataLength = _cur.VarInt();
                    _dataOffset = _cur.Skip(_dataLength);
                    return true;
                case GpuDumpRecord.BackendState:
                    _dataLength = _cur.VarInt();
                    _dataOffset = _cur.Skip(_dataLength);
                    return true;
                case GpuDumpRecord.ProgramInfo or GpuDumpRecord.VertexProgram or GpuDumpRecord.FragmentProgram:
                    _codec.ReadDefinition(kind, _cur);
                    continue;
                case GpuDumpRecord.Idle:
                    return true;
                default:
                    Command = _codec.ReadCommand(kind, _cur, _draw);
                    if (kind == GpuDumpRecord.Present) PresentInfo = GpuDumpCodec.ReadPresentInfo(_cur);
                    return true;
            }
        }
    }

    public void Dispose() => _input.Dispose();
}
