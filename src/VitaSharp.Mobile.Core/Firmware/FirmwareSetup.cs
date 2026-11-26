using VitaSharp.Core.Firmware;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Firmware;

/// <summary>
/// What the "Firmware" screen shows (docs/android/mockups/firmware.html): the installed version, each partition, the
/// font package (sa0:data/font) and whether the Vita system (vs0:vsh/shell/shell.self) can be booted.
/// </summary>
public sealed record FirmwareStatus(FirmwareInfo? Installed, bool SystemFonts, bool Pd0, bool Shell)
{
    public const string ShellPath = FirmwareInfo.ShellPath;

    public static FirmwareStatus Read(string fsRoot) => new(
        FirmwareInfo.Read(fsRoot),
        FirmwareInfo.HasSystemFonts(fsRoot),
        HasEntries(Path.Combine(fsRoot, "pd0")),
        File.Exists(Path.Combine(fsRoot, ShellPath.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>The summary line of the Ajustes list: "3.74 - fontes ausentes", "3.74 + pacote de fontes" or
    /// "nao instalado".</summary>
    public string Summary => Installed is not { } fw ? Loc.Get("MNotInstalled")
        : SystemFonts ? fw.Display : Loc.Format("MFontsMissing", fw.Version ?? Loc.Get("MUnknownVersion"));

    /// <summary>A firmware is installed but sa0 has no system fonts (a system update alone carries none): the Vita
    /// system and many games draw no text, so the screens ask for the font package.</summary>
    public bool FontsNeeded => Installed != null && !SystemFonts;

    /// <summary>The Vita system boots with its text (FirmwareInfo.SystemReady): shell installed and the system fonts in
    /// sa0. A shell without them still opens, after the warning ("Abrir assim mesmo").</summary>
    public bool SystemReady => Shell && SystemFonts;

    private static bool HasEntries(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Installs a system update or the font package chosen with the Storage Access Framework (platform-neutral): the
/// front-end starts ACTION_OPEN_DOCUMENT (MIME "application/octet-stream", the .PUP has no registered type), then
/// passes <c>() =&gt; new FileStream(resolver.OpenFileDescriptor(uri, "r"), ...)</c> or any other seekable stream of
/// the document (src/VitaSharp.Android/Platforms/Android/Saf.cs). The PUP is read in place, never copied:
/// FirmwareInstaller extracts into a staging folder under &lt;data&gt;/fs and swaps the partitions in at the end, so
/// cancelling (the "Cancelar" button) keeps the previous firmware.
/// <para>Remaining work: run it from a foreground service so a long install survives the activity going to the
/// background (the app keeps the screen awake during the install only).</para>
/// </summary>
public static class FirmwareSetup
{
    public const string PickerMimeType = "application/octet-stream";

    /// <summary>"Desinstalar firmware" (the desktop launcher's; no Android screen yet): removes the firmware
    /// partitions and index files of <paramref name="fsRoot"/> (FirmwareInstaller.Uninstall); games, saves and
    /// ux0/ur0 stay. Returns the names removed.</summary>
    public static Task<IReadOnlyList<string>> UninstallAsync(string fsRoot) => Task.Run(() => FirmwareInstaller.Uninstall(fsRoot));

    public static Task<FirmwareInfo> InstallAsync(Func<Stream> openDocument, string displayName, string fsRoot,
        IProgress<FirmwareProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using Stream stream = openDocument();
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("the firmware file must be opened as a seekable stream (OpenFileDescriptor)");
        }

        using var pup = new Pup(stream);
        return FirmwareInstaller.Install(pup, displayName, fsRoot, progress, cancellationToken);
    }, cancellationToken);
}
