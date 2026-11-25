using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Benchmarks;

/// <summary>
/// The GPU dump recorder's own cost on the GPU worker, on a real frame stream (<see cref="Core.Util.EnvVars.BenchDump"/>):
/// what the worker pays per command while a capture records (the "recorder cost ... ms/frame on the GPU worker" of the
/// dump's end line). <see cref="Read"/> is the reader alone (the baseline to subtract), <see cref="ReadAndEncode"/> adds
/// the recorder's encoding of every command (programs, draw state, uniform blocks with their repeat/delta coding) and
/// the memory records, as <see cref="GpuDumpRecorder"/> writes them. The difference divided by the dump's frames (printed
/// in the setup) is the encoding's cost per frame; its allocations are the difference in the Allocated column.
/// </summary>
public class GpuDumpRecorderBenchmarks
{
    private byte[] _dump = [];

    [GlobalSetup]
    public void Setup()
    {
        _dump = File.ReadAllBytes(Fixtures.RequireDump());
        (long frames, byte[] chunk) = DumpReencoder.Encode(_dump, out var uniforms, out long recordBytes);
        Console.WriteLine($"// {frames} frames; first raw chunk {chunk.Length / 1048576.0:F1} MB; records {recordBytes / 1048576.0:F1} MB; " +
                          $"uniform blocks {uniforms.Inline} inline, {uniforms.Refs} repeated, {uniforms.Deltas} deltas ({uniforms.RawBytes / 1048576.0:F1} MB -> {uniforms.StoredBytes / 1048576.0:F1} MB)");
    }

    /// <summary>The reader alone (per dump).</summary>
    [Benchmark(Baseline = true)]
    public long Read()
    {
        long n = 0;
        using var reader = new GpuDumpReader(new MemoryStream(_dump, writable: false));
        while (reader.Next())
        {
            n++;
        }

        return n;
    }

    /// <summary>The reader plus the recorder's encoding of every record (per dump).</summary>
    [Benchmark]
    public long ReadAndEncode() => DumpReencoder.Encode(_dump).Frames;
}

/// <summary>
/// The compressor threads' cost while a capture records: one 32 MB chunk of the records of a real dump
/// (<see cref="Core.Util.EnvVars.BenchDump"/>) compressed as the recorder does, at the qualities a recording can use.
/// Time per chunk x raw MB per frame / 32 = CPU per recorded frame.
/// </summary>
public class GpuDumpCompressBenchmarks
{
    [Params(1, 2, 3)] public int Quality;

    private byte[] _chunk = [];
    private byte[]? _output; // the compressor's output buffer, kept as the recorder's compressors keep theirs

    [GlobalSetup]
    public void Setup()
    {
        _chunk = DumpReencoder.Encode(File.ReadAllBytes(Fixtures.RequireDump())).FirstChunk;
        Console.WriteLine($"// quality {Quality}: {GpuDumpCodec.EncodeChunk(GpuDumpFormat.ChunkRecords, _chunk, Quality).Length / 1048576.0:F2} MB of {_chunk.Length / 1048576.0:F1} MB");
    }

    [Benchmark]
    public int CompressChunk() => GpuDumpCodec.EncodeChunk(GpuDumpFormat.ChunkRecords, _chunk, Quality, ref _output).Length;
}

/// <summary>Reads a dump and writes every record again as the recorder does (commands through a fresh codec).</summary>
internal static class DumpReencoder
{
    private const int ChunkBytes = 32 << 20;

    public static (long Frames, byte[] FirstChunk) Encode(byte[] dump) => Encode(dump, out _, out _);

    /// <summary>Also the codec's uniform block statistics and the record bytes written.</summary>
    public static (long Frames, byte[] FirstChunk) Encode(byte[] dump, out (long Inline, long Refs, long Deltas, long RawBytes, long StoredBytes) uniforms, out long recordBytes)
    {
        var codec = new GpuDumpCodec();
        var main = new DumpBuffer(1 << 20);
        var cmd = new DumpBuffer(1 << 12);
        var defs = new DumpBuffer(1 << 12);
        byte[]? first = null;
        long frames = 0;
        long total = 0;
        using var reader = new GpuDumpReader(new MemoryStream(dump, writable: false));
        while (reader.Next())
        {
            if (reader.Kind == GpuDumpRecord.Memory)
            {
                main.U8((byte)GpuDumpRecord.Memory);
                main.U32(reader.Address);
                main.Var((uint)reader.Data.Length);
                main.Bytes(reader.Data);
            }
            else if (reader.Kind is not (GpuDumpRecord.Idle or GpuDumpRecord.BackendState) && reader.Command is { } command)
            {
                cmd.Clear();
                defs.Clear();
                if (codec.Write(command, defs, cmd))
                {
                    main.Append(defs);
                    main.Append(cmd);
                }

                if (command is PresentCommand)
                {
                    frames++;
                }
            }
            if (main.Length > ChunkBytes)
            {
                first ??= main.Span.ToArray();
                total += main.Length;
                main.Clear();
            }
        }
        uniforms = codec.UniformStats;
        recordBytes = total + main.Length;
        return (frames, first ?? main.Span.ToArray());
    }
}
