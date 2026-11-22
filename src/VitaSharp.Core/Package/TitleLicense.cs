using VitaSharp.Core.Io;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Localization;

namespace VitaSharp.Core.Package;

/// <summary>
/// The license of an installed PS Vita application, found where the firmware looks for it: the application's
/// sce_sys/package/work.bin (a NoNpDrm dump) or its licenses in ux0:license/app/&lt;title id&gt; (the PSM runtime of a
/// PSM title: the PSM title's). An application whose eboot.bin is an NPDRM SELF with encrypted segments needs one; the
/// console's application manager opens it before it creates the process, and a missing file (the I/O manager's
/// 0x80010002) is its launch error 0x80800004 (FW 3.74 SceAppMgr, read in the module, study only), which the shell shows
/// in its launch-error dialog. The emulator, the launchers and the installers all ask this one place.
/// </summary>
public static class TitleLicense
{
    /// <summary>SceAppMgr's launch error for an application without its license file.</summary>
    public const int NoLicenseError = unchecked((int)0x80800004);

    /// <summary>The emulator process's exit code when the launched application's license is missing (the launcher
    /// shows <see cref="Message"/>).</summary>
    public const int ExitCode = 3;

    /// <summary>How much of eboot.bin is read to tell whether it is encrypted (its SCE header and segment info).</summary>
    private const int HeadSize = 0x10000;

    /// <summary>The license file of the application in <paramref name="appDir"/> (title <paramref name="titleId"/>;
    /// null: the folder's name), or null when it has none.</summary>
    public static string? FindFile(string fsRoot, string appDir, string? titleId = null)
    {
        string workBin = Vfs.ResolveCaseInsensitive(appDir, "sce_sys/package/work.bin");
        if (File.Exists(workBin))
        {
            return workBin;
        }

        string id = FirmwareLayout.PsmTitleOfOwnRuntime(fsRoot, appDir) ?? titleId ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(appDir));
        foreach (string rif in FirmwareLayout.ApplicationLicenseFiles(fsRoot, id))
        {
            if (File.Exists(rif))
            {
                return rif;
            }
        }

        return null;
    }

    /// <summary>Whether the executable <paramref name="self"/> of the application in <paramref name="appDir"/> is
    /// encrypted (it can only be loaded with the title's license).</summary>
    public static bool NeedsLicense(string appDir, string self = "eboot.bin")
    {
        // a folder that is still a PFS image is mounted with the licence's key (Pfs.PfsApp0View)
        if (Pfs.PfsApp0View.IsImage(appDir))
        {
            return true;
        }

        string path = Vfs.ResolveCaseInsensitive(appDir, self);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] head = new byte[(int)Math.Min(HeadSize, stream.Length)];
            stream.ReadExactly(head);
            return SelfDecryptor.RequiresKlicensee(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether the application in <paramref name="appDir"/> is encrypted and has no license installed: the
    /// console would refuse to start it (<see cref="NoLicenseError"/>).</summary>
    public static bool IsMissing(string fsRoot, string appDir, string? titleId = null) =>
        NeedsLicense(appDir) && FindFile(fsRoot, appDir, titleId) == null;

    /// <summary>The localized text for an application without its license, with the error code.</summary>
    public static string Message(string titleId) => Loc.Format("LicenseMissing", titleId, $"0x{NoLicenseError:X8}");

    /// <summary>The localized install warning for the application just installed in <paramref name="appDir"/> when it
    /// has no license; null when it can run.</summary>
    public static string? InstallWarning(string fsRoot, string appDir, string titleId) =>
        IsMissing(fsRoot, appDir, titleId) ? Loc.Format("LicenseMissingInstalled", titleId, $"0x{NoLicenseError:X8}") : null;
}
