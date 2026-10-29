namespace VitaSharp.Core.Shell;

/// <summary>Helpers for the files carried in RCOs: what they are, by declared type and by content.</summary>
public static class RcoResources
{
    /// <summary>A file extension for an RCO file: from its magic bytes first, then its declared type.</summary>
    public static string Extension(RcoFile f)
    {
        var d = f.Data;
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G') return ".png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
        if (d.Length >= 4 && d[0] == (byte)'G' && d[1] == (byte)'X' && d[2] == (byte)'T' && d[3] == 0) return ".gxt";
        if (d.Length >= 4 && d[0] == (byte)'M' && d[1] == (byte)'I' && d[2] == (byte)'G' && d[3] == (byte)'.') return ".gim";
        if (d.Length >= 4 && d[0] == (byte)'D' && d[1] == (byte)'D' && d[2] == (byte)'S' && d[3] == (byte)' ') return ".dds";
        if (d.Length >= 4 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F') return ".at9";
        if (d.Length >= 4 && d[0] == (byte)'V' && d[1] == (byte)'A' && d[2] == (byte)'G' && d[3] == (byte)'p') return ".vag";
        if (d.Length >= 4 && d[0] == (byte)'R' && d[1] == (byte)'C' && d[2] == (byte)'S' && d[3] == (byte)'F') return ".cxml";
        string t = f.Type;
        int slash = t.LastIndexOf('/');
        return slash >= 0 && slash < t.Length - 1 ? "." + t[(slash + 1)..] : ".bin";
    }
}
