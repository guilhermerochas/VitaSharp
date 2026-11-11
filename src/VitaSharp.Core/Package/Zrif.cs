using System.IO.Compression;

namespace VitaSharp.Core.Package;

/// <summary>
/// zRIF: the text form of a NoNpDrm license (what NoNpDrm/pkg2zip export from the user's own Vita): base64 of the
/// 512-byte .rif compressed with zlib against a fixed preset dictionary (id 0x627D1D5D, the content-ID fragments
/// the licenses share). <see cref="DeflateStream"/> has no preset-dictionary support, so the dictionary is fed in
/// front of the compressed blocks as one stored block: the output then starts with the dictionary and the back
/// references into it resolve.
/// </summary>
public static class Zrif
{
    public const int RifSize = 512;
    private const uint DictionaryId = 0x627D1D5D;

    // 880 zero bytes and this tail (pkg2zip's zrif_dict, 1024 bytes)
    private const string DictionaryTail =
        "303030303900000000000000000000003030303036303030303730303030380030303030333030303034303030303530" +
        "5F30302D414444434F4E5430303030322D5043534730303030303030303030312D504353453030302D50435346303030" +
        "2D504353433030302D504353443030302D504353413030302D504353423030300001000100010002EFCDAB8967452301";

    private static byte[] Dictionary()
    {
        byte[] tail = Convert.FromHexString(DictionaryTail);
        byte[] d = new byte[1024];
        tail.CopyTo(d, d.Length - tail.Length);
        return d;
    }

    /// <summary>True when <paramref name="text"/> looks like a zRIF (base64 starting with a zlib header) rather than a path.</summary>
    public static bool LooksLikeZrif(string text)
    {
        text = text.Trim();
        if (text.Length < 16 || File.Exists(text))
        {
            return false;
        }

        try
        {
            byte[] raw = Convert.FromBase64String(Pad(text));
            return raw.Length > 6 && (raw[0] & 0xF) == 8 && ((raw[0] << 8) | raw[1]) % 31 == 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Pad(string s) => s.Length % 4 == 0 ? s : s + new string('=', 4 - s.Length % 4);

    /// <summary>Decodes a zRIF into the 512-byte .rif; throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public static byte[] Decode(string zrif)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(Pad(zrif.Trim()));
        }
        catch (FormatException)
        {
            throw new InvalidDataException("zRIF is not valid base64");
        }
        if (raw.Length < 2 + 4)
        {
            throw new InvalidDataException("zRIF is too short");
        }

        if (((raw[0] << 8) | raw[1]) % 31 != 0 || (raw[0] & 0xF) != 8)
        {
            throw new InvalidDataException("zRIF header is corrupted");
        }

        byte[] dict = [];
        int start = 2;
        if ((raw[1] & 0x20) != 0)
        {
            if (raw.Length < 6 + 4)
            {
                throw new InvalidDataException("zRIF is too short");
            }

            uint id = (uint)(raw[2] << 24 | raw[3] << 16 | raw[4] << 8 | raw[5]);
            if (id != DictionaryId)
            {
                throw new InvalidDataException("zRIF uses an unknown dictionary");
            }

            dict = Dictionary();
            start = 6;
        }
        int bodyLen = raw.Length - start - 4;
        if (bodyLen <= 0)
        {
            throw new InvalidDataException("zRIF is too short");
        }

        using var input = new MemoryStream();
        if (dict.Length > 0)
        {
            // stored block, not final: header byte 0, LEN, ~LEN (little endian), then the bytes
            input.WriteByte(0);
            input.WriteByte((byte)dict.Length);
            input.WriteByte((byte)(dict.Length >> 8));
            input.WriteByte((byte)~dict.Length);
            input.WriteByte((byte)(~dict.Length >> 8));
            input.Write(dict);
        }
        input.Write(raw, start, bodyLen);
        input.Position = 0;

        byte[] all;
        try
        {
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            all = output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("zRIF is corrupted (cannot decompress)");
        }
        byte[] rif = all.AsSpan(dict.Length).ToArray();

        uint expected = (uint)(raw[^4] << 24 | raw[^3] << 16 | raw[^2] << 8 | raw[^1]);
        if (Adler32(rif) != expected)
        {
            throw new InvalidDataException("zRIF is corrupted (wrong checksum)");
        }

        if (rif.Length != RifSize)
        {
            throw new InvalidDataException($"zRIF holds {rif.Length} bytes, not a {RifSize}-byte license");
        }

        return rif;
    }

    /// <summary>The content ID a .rif is for (+0x10, 36 characters), e.g. EP0001-PCSE00000_00-0000000000000000.</summary>
    public static string ContentId(ReadOnlySpan<byte> rif) =>
        rif.Length < 0x34 ? "" : System.Text.Encoding.ASCII.GetString(rif.Slice(0x10, 36)).TrimEnd('\0');

    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return b << 16 | a;
    }
}
