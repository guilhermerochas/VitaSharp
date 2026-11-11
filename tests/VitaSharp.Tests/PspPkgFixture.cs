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
    static readonly byte[] Key38 = Convert.FromHexString("12468D7E1C42209BBA5426835EB03303");
    static readonly byte[] Key39 = Convert.FromHexString("C43BB6D653EE67493EA95FBC0CED6F8A");
    static readonly byte[] Key63 = Convert.FromHexString("9C9B1372F8C640CF1C62F5D592DDB582");
    static readonly byte[] Hash3 = Convert.FromHexString("E350ED1D910A1FD029BB1C3EF34077FB");
    static readonly byte[] Hash4 = Convert.FromHexString("135FA47CAB395BA476B8CCA98F3A0445");
    static readonly byte[] Hash5 = Convert.FromHexString("678D7FA32A9CA0D1508AD8385E4B017E");

    public record struct Item(string Name, byte[]? Data, byte PspType = 0x90);

    /// <summary>A key-type-1 package with content type <paramref name="contentType"/>; <paramref name="installDir"/>
    /// adds info block 10 (a PSP add-on).</summary>
    public static byte[] Build(string contentId, uint contentType, IEnumerable<Item> items, string? installDir = null)
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
            while (area.Length % 16 != 0) area.WriteByte(0);
        }
        var datas = new List<long>();
        foreach (var it in all)
        {
            datas.Add(area.Length);
            if (it.Data == null) continue;
            area.Write(it.Data);
            while (area.Length % 16 != 0) area.WriteByte(0);
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
        for (int i = 0; i < 16; i++) iv[i] = (byte)(0x31 + i * 7);
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
        Take(psp, 0, all.Count * 32);
        for (int i = 0; i < all.Count; i++)
        {
            var src = all[i].PspType == 0x90 ? psp : ps3;
            Take(src, names[i].Off, names[i].Len);
            if (all[i].Data != null) Take(src, datas[i], all[i].Data!.Length);
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
        BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], 0x80000002);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[8..], infoOffset);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0xC..], (uint)infoCount);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0x14..], (uint)all.Count);
        BinaryPrimitives.WriteUInt64BigEndian(hdr[0x20..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(hdr[0x28..], (ulong)enc.Length);
        Encoding.ASCII.GetBytes(contentId).CopyTo(hdr[0x30..]);
        iv.CopyTo(hdr[0x70..]);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[0xE4..], 1);
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
        for (int at = 0xC; at <= 0x20; at += 4) BinaryPrimitives.WriteUInt32LittleEndian(pbp.AsSpan(at), (uint)end);
        BinaryPrimitives.WriteUInt32LittleEndian(pbp.AsSpan(0x24), (uint)psarOff);
        sfo.CopyTo(pbp, sfoOff);
        psar.CopyTo(pbp, psarOff);
        return pbp;
    }

    /// <summary>
    /// An NPUMDIMG DATA.PSAR holding <paramref name="sectors"/> (2048 bytes each), one sector per block: block i is
    /// encrypted unless <paramref name="plainBlock"/> == i (table flag 4). The header's key seed (0xC0) is solved so the
    /// derived IV is a chosen one, which lets the test encrypt the header fields it needs.
    /// </summary>
    public static byte[] UmdImage(IReadOnlyList<byte[]> sectors, int plainBlock = -1)
    {
        const int tableAt = 0x100;
        int dataAt = tableAt + sectors.Count * 32;
        var psar = new byte[dataAt + sectors.Count * 2048];
        "NPUMDIMG"u8.CopyTo(psar);
        BinaryPrimitives.WriteUInt32LittleEndian(psar.AsSpan(0xC), 1); // sectors per block
        for (int i = 0x10; i < 0x40; i++) psar[i] = (byte)i;
        for (int i = 0xA0; i < 0xC0; i++) psar[i] = (byte)(i * 3);

        var iv = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        using var cipher = new PspCrypto.BbCipher(Key63, iv);
        var fields = new byte[0x60]; // header 0x40..0xA0
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(0x54 - 0x40), 0);                         // first LBA
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(0x64 - 0x40), (uint)sectors.Count - 1);   // last LBA
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(0x6C - 0x40), tableAt);                    // offset table
        cipher.Decrypt(fields, 0); // a xor stream: decrypting encrypts
        fields.CopyTo(psar, 0x40);

        // seed at 0xC0 so that SectionCipher's derived IV is iv
        var mac = PspCrypto.HeaderMac(psar, 0xC0);
        var x = new byte[16];
        var ivh4 = new byte[16];
        for (int i = 0; i < 16; i++) ivh4[i] = (byte)(iv[i] ^ Hash4[i]);
        AesUtil.EcbEncrypt(Key39, ivh4, x);
        for (int i = 0; i < 16; i++) x[i] ^= (byte)(mac[i] ^ psar[0xA0 + i] ^ Hash3[i] ^ Hash5[i]);
        var seed = new byte[16];
        AesUtil.EcbEncrypt(Key38, x, seed);
        AesUtil.EcbEncrypt(Key63, seed, seed);
        seed.CopyTo(psar, 0xC0);

        for (int b = 0; b < sectors.Count; b++)
        {
            uint offset = (uint)(dataAt + b * 2048), size = 2048, flags = b == plainBlock ? 4u : 0u;
            uint t0 = 0x11111111u * (uint)(b + 1), t1 = 0x2468ACE0u + (uint)b, t2 = 0x0F0F0F0Fu ^ (uint)b, t3 = 0x5A5A5A5Au;
            var e = psar.AsSpan(tableAt + b * 32);
            uint[] t = [t0, t1, t2, t3, offset ^ t2 ^ t3, size ^ t1 ^ t2, flags ^ t0 ^ t3, 0];
            for (int k = 0; k < 8; k++) BinaryPrimitives.WriteUInt32LittleEndian(e[(k * 4)..], t[k]);
            var data = (byte[])sectors[b].Clone();
            if (flags == 0) cipher.Decrypt(data, offset / 16);
            data.CopyTo(psar, offset);
        }
        return psar;
    }

    /// <summary>A PSISOIMG DATA.PSAR whose PGD key header sits at +0x400, and the KEYS.BIN it implies.</summary>
    public static (byte[] Psar, byte[] KeysBin) PsoneImage()
    {
        var psar = new byte[0x400 + 0x90 + 0x100];
        "PSISOIMG0000"u8.CopyTo(psar);
        var pgd = psar.AsSpan(0x400, 0x90);
        "\0PGD"u8.CopyTo(pgd);
        BinaryPrimitives.WriteUInt32LittleEndian(pgd[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pgd[8..], 1);
        for (int i = 0x10; i < 0x90; i++) pgd[i] = (byte)(i ^ 0x5C);
        // KEYS.BIN = CMAC_38(pgd[0..0x70]) ^ AES-dec_38(pgd[0x70..0x80]) ^ amctrl hash key 3 (lusid1/pkg2zip)
        var mac = PspCrypto.Cmac(Key38, pgd[..0x70]);
        var tmp = new byte[16];
        AesUtil.EcbDecrypt(Key38, pgd.Slice(0x70, 16), tmp);
        var keys = new byte[16];
        for (int i = 0; i < 16; i++) keys[i] = (byte)(mac[i] ^ tmp[i] ^ Hash3[i]);
        return (psar, keys);
    }
}
