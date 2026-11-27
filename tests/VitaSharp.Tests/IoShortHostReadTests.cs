using VitaSharp.Core.Hle.Modules;

namespace VitaSharp.Tests;

/// <summary>
/// A read of a regular file on the Vita returns what was asked, or all that is left before the end of the file. A
/// host read(2) may return less (Linux and Android, FUSE storage); Windows reads regular files whole, which is why only
/// the phone got a partly filled buffer (Dead Space reads its 5.5 MB menu theme in one sceIoRead). The host is read
/// again until the request is filled or the file ends.
/// </summary>
public sealed class IoShortHostReadTests
{
    /// <summary>A host file that hands out at most <see cref="Chunk"/> bytes per read, as a FUSE read may.</summary>
    private readonly struct ChunkedFile(byte[] data, int chunk) : IHostFileRead
    {
        public int Chunk => chunk;

        public int Read(Span<byte> destination, long offset)
        {
            if (offset >= data.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(Math.Min(destination.Length, chunk), data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    private static byte[] Data(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31 + 7);
        }

        return data;
    }

    [Fact]
    public void AShortHostReadIsCompletedToTheSizeAsked()
    {
        byte[] file = Data(1 << 20);
        byte[] guest = new byte[600_000];
        long before = Interlocked.Read(ref IoFileMgr.ShortHostReads);
        int n = IoFileMgr.ReadFully(new ChunkedFile(file, 128 * 1024), guest, 1000, "ux0:data/theme.sps");
        Assert.Equal(guest.Length, n);
        Assert.True(guest.AsSpan().SequenceEqual(file.AsSpan(1000, guest.Length)));
        Assert.True(Interlocked.Read(ref IoFileMgr.ShortHostReads) > before);
    }

    [Fact]
    public void AReadPastTheEndReturnsWhatIsLeft()
    {
        byte[] file = Data(300_000);
        byte[] guest = new byte[500_000];
        int n = IoFileMgr.ReadFully(new ChunkedFile(file, 65536), guest, 100_000, "ux0:data/bank.sb");
        Assert.Equal(200_000, n);
        Assert.True(guest.AsSpan(0, n).SequenceEqual(file.AsSpan(100_000)));
        Assert.Equal(0, IoFileMgr.ReadFully(new ChunkedFile(file, 65536), guest, 300_000, "ux0:data/bank.sb"));
    }
}
