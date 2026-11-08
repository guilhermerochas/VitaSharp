using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// Finds a title's SceNpCommunicationSignature in its own code. An application passes the signature of its trophy set
/// to sceNpTrophyCreateContext (or sceNpInit) from a constant in its data segment: 0xA0 bytes starting with the magic
/// 0xB9DDE13B, usually right after the SceNpCommunicationId ("NPWR01234") it belongs to. Reading it from the decrypted
/// executable (eboot.bin, then the application's own modules) lets the shell's trophy server get the titles played
/// before VitaSharp kept the signature, without starting each game.
/// </summary>
internal static class TrophySignature
{
    static ReadOnlySpan<byte> Magic => [0xB9, 0xDD, 0xE1, 0x3B];

    /// <summary>The signature of set <paramref name="commId"/> in the application at <paramref name="appDir"/>
    /// (host path of ux0:app/&lt;title id&gt;), or null.</summary>
    public static byte[]? FromApp(string fsRoot, string appDir, string commId)
    {
        byte[]? klic = Klicensee(fsRoot, appDir);
        var files = new List<string>();
        if (File.Exists(Path.Combine(appDir, "eboot.bin"))) files.Add(Path.Combine(appDir, "eboot.bin"));
        if (Directory.Exists(Path.Combine(appDir, "sce_module")))
            files.AddRange(Directory.GetFiles(Path.Combine(appDir, "sce_module")).Where(f => f.EndsWith(".suprx", StringComparison.OrdinalIgnoreCase)
                                                                                        || f.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
        foreach (var file in files)
        {
            try
            {
                if (Find(SelfDecryptor.ToElf(File.ReadAllBytes(file), klic), commId) is { } sign) return sign;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                          or IndexOutOfRangeException or System.Security.Cryptography.CryptographicException)
            {
                Log.Debug($"Trophy: no signature read from {file}: {e.Message}");
            }
        }
        return null;
    }

    /// <summary>The signature in <paramref name="image"/>: the one nearest to the set's id when there are several.</summary>
    internal static byte[]? Find(ReadOnlySpan<byte> image, string commId)
    {
        int idAt = image.IndexOf(System.Text.Encoding.ASCII.GetBytes(commId[..9]));
        int best = -1;
        for (int i = image.IndexOf(Magic); i >= 0 && i + TrophyUnlocks.SignatureSize <= image.Length;)
        {
            if (best < 0 || (idAt >= 0 && Math.Abs(i - idAt) < Math.Abs(best - idAt))) best = i;
            int next = image[(i + 1)..].IndexOf(Magic);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return best < 0 ? null : image.Slice(best, TrophyUnlocks.SignatureSize).ToArray();
    }

    /// <summary>The application's klicensee: its work.bin, or a license in ux0:license/&lt;title id&gt;.</summary>
    internal static byte[]? Klicensee(string fsRoot, string appDir)
    {
        try
        {
            string work = Path.Combine(appDir, "sce_sys", "package", "work.bin");
            if (File.Exists(work)) return PkgInstaller.ReadKlicensee(work);
            string lic = Path.Combine(fsRoot, "ux0", "license", Path.GetFileName(appDir.TrimEnd('/', '\\')));
            if (Directory.Exists(lic) && Directory.GetFiles(lic, "*.rif").Order(StringComparer.Ordinal).FirstOrDefault() is { } rif)
                return PkgInstaller.ReadKlicensee(rif);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return null;
    }
}
