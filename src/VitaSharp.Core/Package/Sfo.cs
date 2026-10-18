using System.Text;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package;

/// <summary>PARAM.SFO key/value reader.</summary>
public sealed class Sfo
{
    public Dictionary<string, object> Values { get; } = new();

    public Sfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x14 || U32LE(data, 0) != 0x46535000)
            throw new InvalidDataException("Not a PARAM.SFO");
        uint keyTable = U32LE(data, 8), dataTable = U32LE(data, 12), count = U32LE(data, 16);
        for (int i = 0; i < count; i++)
        {
            int e = 0x14 + i * 16;
            ushort keyOff = U16LE(data, e);
            ushort fmt = U16LE(data, e + 2);
            uint len = U32LE(data, e + 4);
            uint dataOff = U32LE(data, e + 12);
            string key = CString(data[(int)(keyTable + keyOff)..]);
            var v = data.Slice((int)(dataTable + dataOff), (int)len);
            Values[key] = fmt == 0x0404 ? U32LE(v, 0) : CString(v);
        }
    }

    public string GetString(string key, string def = "") => Values.TryGetValue(key, out var v) && v is string s ? s : def;
    public uint GetUInt(string key, uint def = 0) => Values.TryGetValue(key, out var v) && v is uint u ? u : def;

    public string TitleId => GetString("TITLE_ID");
    public string Title => GetString("TITLE");
    public string Category => GetString("CATEGORY");
    public string ContentId => GetString("CONTENT_ID");
    public string AppVersion => GetString("APP_VER");

    public static Sfo Load(string path) => new(File.ReadAllBytes(path));
}
