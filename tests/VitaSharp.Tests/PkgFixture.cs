using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core.Crypto;

namespace VitaSharp.Tests;

/// <summary>
/// Builds a small retail-format Vita .pkg (header, info blocks, PARAM.SFO and the AES-128-CTR data area with the item
/// table, as pkg2zip reads it) without the PFS layer, so the install path can be tested without a real package.
/// </summary>
public static class PkgFixture
{
    static readonly byte[] PkgVita2 = Convert.FromHexString("E31A70C9CE1DD72BF3C0622963F2ECCB");

    public static byte[] Sfo(params (string Key, string Value)[] values)
    {
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var entries = new List<(int KeyOff, int Len, int DataOff)>();
        foreach (var (k, v) in values)
        {
            int keyOff = (int)keys.Length;
            keys.Write(Encoding.UTF8.GetBytes(k + "\0"));
            var bytes = Encoding.UTF8.GetBytes(v + "\0");
            int dataOff = (int)data.Length;
            data.Write(bytes);
            while (data.Length % 4 != 0) data.WriteByte(0);
            entries.Add((keyOff, bytes.Length, dataOff));
        }
        while (keys.Length % 4 != 0) keys.WriteByte(0);
        int keyTable = 0x14 + entries.Count * 16, dataTable = keyTable + (int)keys.Length;
        var sfo = new byte[dataTable + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(sfo, 0x46535000);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(4), 0x101);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(12), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(16), (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = sfo.AsSpan(0x14 + i * 16);
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)entries[i].KeyOff);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], 0x0204);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)entries[i].Len);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)entries[i].Len);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)entries[i].DataOff);
        }
        keys.ToArray().CopyTo(sfo, keyTable);
        data.ToArray().CopyTo(sfo, dataTable);
        return sfo;
    }

    /// <summary>An application package (content type 0x15) with <paramref name="files"/> (a null content is a folder).</summary>
    public static byte[] Build(string contentId, string title, params (string Name, byte[]? Data)[] files)
    {
        string titleId = contentId.Substring(7, 9);
        var sfo = Sfo(("CATEGORY", "gd"), ("CONTENT_ID", contentId), ("TITLE", title), ("TITLE_ID", titleId), ("APP_VER", "01.00"));
        var all = new List<(string Name, byte[]? Data)> { ("sce_sys", null), ("sce_sys/param.sfo", sfo) };
        all.AddRange(files);

        // the data area: item table, names, then the file contents (16-aligned)
        var area = new MemoryStream();
        area.Write(new byte[all.Count * 32]);
        var nameOffs = new List<(int Off, int Len)>();
        foreach (var (name, _) in all)
        {
            var n = Encoding.UTF8.GetBytes(name);
            nameOffs.Add(((int)area.Length, n.Length));
            area.Write(n);
            while (area.Length % 16 != 0) area.WriteByte(0);
        }
        var dataOffs = new List<long>();
        foreach (var (_, data) in all)
        {
            dataOffs.Add(area.Length);
            if (data == null) continue;
            area.Write(data);
            while (area.Length % 16 != 0) area.WriteByte(0);
        }
        var plain = area.ToArray();
        for (int i = 0; i < all.Count; i++)
        {
            var item = plain.AsSpan(i * 32);
            BinaryPrimitives.WriteUInt32BigEndian(item, (uint)nameOffs[i].Off);
            BinaryPrimitives.WriteUInt32BigEndian(item[4..], (uint)nameOffs[i].Len);
            BinaryPrimitives.WriteUInt64BigEndian(item[8..], (ulong)dataOffs[i]);
            BinaryPrimitives.WriteUInt64BigEndian(item[16..], (ulong)(all[i].Data?.Length ?? 0));
            BinaryPrimitives.WriteUInt32BigEndian(item[24..], all[i].Data == null ? 4u : 3u);
        }

        const int infoOffset = 0x100, infoCount = 3, sfoOffset = infoOffset + infoCount * 16;
        int dataOffset = (sfoOffset + sfo.Length + 15) & ~15;
        var pkg = new byte[dataOffset + plain.Length];
        var hdr = pkg.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 0x7F504B47);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[8..], infoOffset);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0xC..], infoCount);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0x14..], (uint)all.Count);
        BinaryPrimitives.WriteUInt64BigEndian(hdr[0x20..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(hdr[0x28..], (ulong)plain.Length);
        Encoding.ASCII.GetBytes(contentId).CopyTo(hdr[0x30..]);
        var iv = new byte[16];
        for (int i = 0; i < 16; i++) iv[i] = (byte)(0xA0 + i);
        iv.CopyTo(hdr[0x70..]);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0xE4..], 2);

        void Info(int index, uint type, uint a, uint b)
        {
            var block = pkg.AsSpan(infoOffset + index * 16);
            BinaryPrimitives.WriteUInt32BigEndian(block, type);
            BinaryPrimitives.WriteUInt32BigEndian(block[4..], 8);
            BinaryPrimitives.WriteUInt32BigEndian(block[8..], a);
            BinaryPrimitives.WriteUInt32BigEndian(block[12..], b);
        }
        Info(0, 2, 0x15, 0);
        Info(1, 13, 0, (uint)(all.Count * 32));
        Info(2, 14, sfoOffset, (uint)sfo.Length);
        sfo.CopyTo(pkg, sfoOffset);

        var key = new byte[16];
        AesUtil.EcbEncrypt(PkgVita2, iv, key);
        using var ctr = new AesUtil.Ctr(key);
        ctr.Transform(plain, AesUtil.CounterAt(iv, 0));
        plain.CopyTo(pkg, dataOffset);
        return pkg;
    }
}
