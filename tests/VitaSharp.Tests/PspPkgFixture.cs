using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core.Crypto;
using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

/// <summary>
/// Builds small synthetic PSP/PSone packages (PS3-format .pkg with key type 1, as the PS Store sells them and pkg2zip
/// reads them): the item table under the PSP key, the PSP content (pspType 0x90) under the PSP key and the PS3 wrapper
/// under the PS3 key; and the EBOOT.PBP inside them, with an NPUMDIMG (PSP) or PSISOIMG (PSone) DATA.PSAR whose keys are
/// derived from its own header. No real game data.
/// </summary>
public static class PspPkgFixture
{
    static readonly byte[] PkgPs3 = Convert.FromHexString("2E7B71D7C9C9A14EA3221F188828B8F8");
    static readonly byte[] PkgPsp = Convert.FromHexString("07F2C68290B50D2C33818D709B60E62B");

    public record struct Item(string Name, byte[]? Data, byte PspType = 0x90);

    /// <summary>A key-type-1 package with content type <paramref name="contentType"/>; <paramref name="installDir"/>
    /// adds info block 10 (a PSP add-on).</summary>
    public static byte[] Build(string contentId, uint contentType, IEnumerable<Item> items, string? installDir = null, bool ps3Format = false)
    {
        var all = items.ToList();
        var area = new MemoryStream();
        area.Write(new byte[all.Count * 32]);
        var names = new List<(int Off, int Len)>();
        foreach (var it in all)
        {
            var n = Encoding.UTF8.GetBytes(it.Name);
            names.Add(((int)area.Length, n.Length));
            area.Write(n);
            while (area.Length % 16 != 0)
            {
                area.WriteByte(0);
            }
        }
        var datas = new List<long>();
        foreach (var it in all)
        {
            datas.Add(area.Length);
            if (it.Data == null)
            {
                continue;
            }

            area.Write(it.Data);
            while (area.Length % 16 != 0)
            {
                area.WriteByte(0);
            }
        }
        var plain = area.ToArray();
        for (int i = 0; i < all.Count; i++)
        {
            var item = plain.AsSpan(i * 32);
            BinaryPrimitives.WriteUInt32BigEndian(item, (uint)names[i].Off);
            BinaryPrimitives.WriteUInt32BigEndian(item[4..], (uint)names[i].Len);
            BinaryPrimitives.WriteUInt64BigEndian(item[8..], (ulong)datas[i]);
            BinaryPrimitives.WriteUInt64BigEndian(item[16..], (ulong)(all[i].Data?.Length ?? 0));
            item[24] = all[i].PspType;
            item[27] = all[i].Data == null ? (byte)4 : (byte)3;
        }

        var iv = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            iv[i] = (byte)(0x31 + i * 7);
        }

        byte[] Encrypted(byte[] key)
        {
            var c = (byte[])plain.Clone();
            using var ctr = new AesUtil.Ctr(key);
            ctr.Transform(c, AesUtil.CounterAt(iv, 0));
            return c;
        }
        var psp = Encrypted(PkgPsp);
        var ps3 = Encrypted(PkgPs3);
        var enc = (byte[])ps3.Clone();
        void Take(byte[] src, long off, long len) => Array.Copy(src, off, enc, off, (len + 15) & ~15L);
        Take(ps3Format ? ps3 : psp, 0, all.Count * 32);
        for (int i = 0; i < all.Count; i++)
        {
            var src = all[i].PspType == 0x90 && !ps3Format ? psp : ps3;
            Take(src, names[i].Off, names[i].Len);
            if (all[i].Data != null)
            {
                Take(src, datas[i], all[i].Data!.Length);
            }
        }

        var info = new MemoryStream();
        void Info(uint type, byte[] body)
        {
            Span<byte> h = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(h, type);
            BinaryPrimitives.WriteUInt32BigEndian(h[4..], (uint)body.Length);
            info.Write(h);
            info.Write(body);
        }
        byte[] Words(uint a, uint b)
        {
            var w = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(w, a);
            BinaryPrimitives.WriteUInt32BigEndian(w.AsSpan(4), b);
            return w;
        }
        int infoCount = 2;
        Info(2, Words(contentType, 0));
        Info(13, Words(0, (uint)(all.Count * 32)));
        if (installDir != null)
        {
            var body = new byte[8 + 0x28];
            Encoding.ASCII.GetBytes(installDir).CopyTo(body, 8);
            Info(10, body);
            infoCount++;
        }

        const int infoOffset = 0x100;
        int dataOffset = (int)((infoOffset + info.Length + 15) & ~15);
        var pkg = new byte[dataOffset + enc.Length];
        var hdr = pkg.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 0x7F504B47);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], ps3Format ? 0x80000001u : 0x80000002u);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[8..], infoOffset);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0xC..], (uint)infoCount);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0x14..], (uint)all.Count);
        BinaryPrimitives.WriteUInt64BigEndian(hdr[0x20..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(hdr[0x28..], (ulong)enc.Length);
        Encoding.ASCII.GetBytes(contentId).CopyTo(hdr[0x30..]);
        iv.CopyTo(hdr[0x70..]);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0xE4..], ps3Format ? 4u : 1u); // a PS3-format header has other data here
        info.ToArray().CopyTo(pkg, infoOffset);
        enc.CopyTo(pkg, dataOffset);
        return pkg;
    }

    /// <summary>The PS3 wrapper's PARAM.SFO of a PSP/PSone package.</summary>
    public static byte[] OuterSfo(string title, string titleId, string category = "EG") =>
        PkgFixture.Sfo(("CATEGORY", category), ("TITLE", title), ("TITLE_ID", titleId));

    /// <summary>An EBOOT.PBP: header, PARAM.SFO (with DISC_ID) and the DATA.PSAR at a 16-aligned offset.</summary>
    public static byte[] Pbp(string discId, string title, byte[] psar, string category = "EG")
    {
        var sfo = PkgFixture.Sfo(("CATEGORY", category), ("DISC_ID", discId), ("TITLE", title));
        int sfoOff = 0x28, end = sfoOff + sfo.Length;
        int psarOff = (end + 15) & ~15;
        var pbp = new byte[psarOff + psar.Length];
        "\0PBP"u8.CopyTo(pbp);
        BinaryPrimitives.WriteUInt32LittleEndian(pbp.AsSpan(4), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(pbp.AsSpan(8), (uint)sfoOff);
        for (int at = 0xC; at <= 0x20; at += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pbp.AsSpan(at), (uint)end);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(pbp.AsSpan(0x24), (uint)psarOff);
        sfo.CopyTo(pbp, sfoOff);
        psar.CopyTo(pbp, psarOff);
        return pbp;
    }

    /// <summary>A stand-in DATA.PSAR (the magic of a PSP UMD image or a PSone image, then filler bytes): the installer
    /// copies the eboot as it is, it never looks inside.</summary>
    public static byte[] Psar(string magic, int size = 0x400)
    {
        var psar = new byte[size];
        for (int i = 0; i < size; i++)
        {
            psar[i] = (byte)(i * 7 + 3);
        }

        Encoding.ASCII.GetBytes(magic).CopyTo(psar, 0);
        return psar;
    }
}
