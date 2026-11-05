using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// The last pictures the media decoders (sceJpeg, sceAvcdec) wrote into guest memory, kept per output address so the
/// renderer can sample the picture the hardware GPU would have read. On the Vita a scene that samples a decoder's
/// output runs milliseconds after its draws were submitted but still before the CPU overwrites the buffer with the next
/// picture; here the GPU worker can run after that overwrite. A texture whose size is the one of an earlier picture
/// at the address (not of the newest) belongs to that earlier picture: Jak and Daxter Collection's menu decodes MJPEG
/// frames of varying width into one buffer and stays one frame ahead of its render thread, and reading the newest
/// frame's pixels with the previous frame's width showed as horizontal stripes. A texture matching the newest
/// picture reads memory as before when every earlier picture was drawn (every movie player). <see cref="EnvVars.DecoderHistory"/>=0 turns it off.
/// </summary>
public static class DecoderOutputs
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable(EnvVars.DecoderHistory) != "0";

    /// <summary>One decoded picture: its bytes from <see cref="Address"/> on and the texture sizes that read it as a
    /// whole (the picture, and for pitched outputs the pitch-wide buffer).</summary>
    public sealed class Picture
    {
        public required uint Address;
        public required uint Width, Height;
        public uint AltWidth, AltHeight;
        public required byte[] Data;
        public long Seq;
        /// <summary>A texture upload read it (or a later picture).</summary>
        public bool Taken;

        public bool Fits(uint w, uint h) => (w == Width && h == Height) || (AltWidth != 0 && w == AltWidth && h == AltHeight);
    }

    const int Depth = 2, MaxAddresses = 8;
    static readonly object Lock = new();
    // per (address space id, address): newest last
    static readonly Dictionary<(int, uint), List<Picture>> History = new();
    static long _seq;
    static long _served;

    /// <summary>Pictures served from the history instead of memory (diagnostics).</summary>
    public static long Served => Interlocked.Read(ref _served);

    static int SpaceOf(uint addr) => GuestMemory.IsSharedAddress(addr) ? 0 : AddressSpace.Current?.Id ?? 0;

    /// <summary>Records the picture a decoder just wrote at <paramref name="addr"/> (call after the write).</summary>
    public static void Record(uint addr, uint width, uint height, ReadOnlySpan<byte> data, uint altWidth = 0, uint altHeight = 0)
    {
        if (!Enabled || addr == 0 || data.IsEmpty) return;
        var key = (SpaceOf(addr), addr);
        lock (Lock)
        {
            if (!History.TryGetValue(key, out var list))
            {
                if (History.Count >= MaxAddresses)
                {
                    // drop the address written longest ago
                    var oldest = History.MinBy(kv => kv.Value[^1].Seq).Key;
                    History.Remove(oldest);
                }
                History[key] = list = new List<Picture>(Depth);
            }
            // reuse the array of the picture that falls out of the history
            byte[]? buf = null;
            if (list.Count == Depth) { buf = list[0].Data; list.RemoveAt(0); }
            if (buf == null || buf.Length != data.Length) buf = new byte[data.Length];
            data.CopyTo(buf);
            list.Add(new Picture { Address = addr, Width = width, Height = height, AltWidth = altWidth, AltHeight = altHeight, Data = buf, Seq = ++_seq });
        }
    }

    /// <summary>
    /// The picture a new upload of the texture at <paramref name="addr"/> (<paramref name="w"/> x <paramref name="h"/>)
    /// reads, or null for memory as it is. Each upload takes the oldest picture of that size no upload has taken yet:
    /// the pictures are drawn in the order they were decoded, also when the GPU worker runs one decode behind (two
    /// pictures of the same size in a row: reading memory showed the newer one a frame early, and the preview
    /// flickered). With no such picture: memory when the newest picture has that size, else the latest earlier picture
    /// of that size. Memory is read whenever it no longer holds the newest picture (something else wrote it since). The
    /// returned bytes must not be modified.
    /// </summary>
    public static Picture? EarlierFor(uint addr, uint w, uint h)
    {
        if (!Enabled) return null;
        Picture newest, match;
        lock (Lock)
        {
            if (History.Count == 0 || !History.TryGetValue((SpaceOf(addr), addr), out var list)) return null;
            newest = list[^1];
            Picture? found = list.FirstOrDefault(p => !p.Taken && p.Fits(w, h));
            if (found != null)
            {
                // every picture before it was drawn or skipped
                foreach (var p in list) { if (p == found) break; p.Taken = true; }
                found.Taken = true;
                if (found == newest) return null;
            }
            else
            {
                if (newest.Fits(w, h)) return null;
                for (int i = list.Count - 2; i >= 0 && found == null; i--)
                    if (list[i].Fits(w, h)) found = list[i];
                if (found == null) return null;
            }
            // a copy: Record reuses the array of a picture leaving the history
            match = new Picture { Address = found.Address, Width = found.Width, Height = found.Height, Data = (byte[])found.Data.Clone(), Seq = found.Seq };
        }
        if (!GuestMemory.ReadSpan(addr, newest.Data.Length).SequenceEqual(newest.Data)) return null;
        Interlocked.Increment(ref _served);
        return match;
    }
}
