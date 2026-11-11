using System.Collections.Concurrent;
using System.Diagnostics;

namespace VitaSharp.Core.Package;

/// <summary>
/// Writes files on a background thread (below normal priority) while the installer reads and decrypts the next
/// chunk: the producer rents a buffer, fills it, and queues it; the buffer comes back to the pool once written.
/// Errors on the writer thread surface on the producer's next call (or at <see cref="Complete"/>).
/// </summary>
internal sealed class FileWritePipeline : IDisposable
{
    abstract record Cmd;
    sealed record OpenCmd(string Path, long Preallocate) : Cmd;
    sealed record WriteCmd(byte[] Buffer, int Count) : Cmd;
    sealed record CloseCmd(long? Length, DateTime? LastWrite) : Cmd;

    readonly BlockingCollection<byte[]> _free = new();
    readonly BlockingCollection<Cmd> _queue = new();
    readonly CancellationTokenSource _cts;
    readonly Thread _thread;
    readonly Action<long>? _written;
    Exception? _error;

    public int BufferSize
    {
        get;
    }
    /// <summary>Time the writer thread spent in file system calls, and the bytes it wrote.</summary>
    public TimeSpan WriteTime
    {
        get; private set;
    }
    public long BytesWritten
    {
        get; private set;
    }

    /// <param name="written">Called on the writer thread after each chunk with its size.</param>
    public FileWritePipeline(int bufferSize, int buffers, Action<long>? written, CancellationToken ct)
    {
        BufferSize = bufferSize;
        for (int i = 0; i < buffers; i++)
        {
            _free.Add(new byte[bufferSize]);
        }

        _written = written;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _thread = new Thread(Run) { Name = "Install writer", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>A free buffer of <see cref="BufferSize"/> bytes (waits while all of them are queued).</summary>
    public byte[] Rent()
    {
        try
        {
            return _free.Take(_cts.Token);
        }
        catch (OperationCanceledException) { ThrowIfFailed(); throw; }
    }

    public void Open(string path, long preallocate) => Post(new OpenCmd(path, preallocate));
    /// <summary>Queues <paramref name="count"/> bytes of a rented buffer for the open file; the buffer is returned after.</summary>
    public void Write(byte[] buffer, int count) => Post(new WriteCmd(buffer, count));
    /// <summary>Closes the open file, first setting its length and last write time when given.</summary>
    public void Close(long? length = null, DateTime? lastWrite = null) => Post(new CloseCmd(length, lastWrite));
    /// <summary>Gives a rented buffer back without writing it.</summary>
    public void Return(byte[] buffer) => _free.Add(buffer);

    void Post(Cmd c)
    {
        ThrowIfFailed();
        _queue.Add(c, _cts.Token);
    }

    void ThrowIfFailed()
    {
        if (_error != null)
        {
            throw new IOException("Writing the installed files failed: " + _error.Message, _error);
        }
    }

    /// <summary>Waits until everything queued is on disk.</summary>
    public void Complete()
    {
        _queue.CompleteAdding();
        _thread.Join();
        ThrowIfFailed();
        _cts.Token.ThrowIfCancellationRequested();
    }

    void Run()
    {
        FileStream? fs = null;
        var sw = new Stopwatch();
        try
        {
            foreach (var c in _queue.GetConsumingEnumerable(_cts.Token))
            {
                sw.Start();
                switch (c)
                {
                    case OpenCmd o:
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(o.Path)!);
                        fs = new FileStream(o.Path, new FileStreamOptions
                        {
                            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                            BufferSize = 0, PreallocationSize = o.Preallocate,
                        });
                        break;
                    case WriteCmd w:
                        fs!.Write(w.Buffer, 0, w.Count);
                        _free.Add(w.Buffer);
                        BytesWritten += w.Count;
                        break;
                    case CloseCmd cl:
                        if (cl.Length is { } len && fs!.Length != len)
                        {
                            fs.SetLength(len);
                        }

                        var path = fs!.Name;
                        fs.Dispose();
                        fs = null;
                        if (cl.LastWrite is { } t)
                        {
                            try
                            {
                                File.SetLastWriteTime(path, t);
                            }
                            catch (IOException) { }
                            catch (ArgumentException) { }
                        }

                        break;
                }
                sw.Stop();
                if (c is WriteCmd wc)
                {
                    _written?.Invoke(wc.Count);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _error = e;
            _cts.Cancel(); // wakes a producer waiting in Rent/Post
        }
        finally
        {
            fs?.Dispose();
            WriteTime = sw.Elapsed;
        }
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            _cts.Cancel();
            _thread.Join();
        }
        _cts.Dispose();
        _queue.Dispose();
        _free.Dispose();
    }
}
