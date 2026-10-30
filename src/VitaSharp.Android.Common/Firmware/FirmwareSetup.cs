using VitaSharp.Core.Firmware;

namespace VitaSharp.Android.Firmware;

/// <summary>
/// What the "Firmware" screen shows (docs/android/mockups/firmware.html): the installed version, each partition, the
/// font package (sa0:data/font) and whether the Vita system (vs0:vsh/shell/shell.self) can be booted.
/// </summary>
public sealed record FirmwareStatus(FirmwareInfo? Installed, bool SystemFonts, bool Pd0, bool Shell)
{
    public const string ShellPath = "vs0/vsh/shell/shell.self";

    public static FirmwareStatus Read(string fsRoot) => new(
        FirmwareInfo.Read(fsRoot),
        FirmwareInfo.HasSystemFonts(fsRoot),
        HasEntries(Path.Combine(fsRoot, "pd0")),
        File.Exists(Path.Combine(fsRoot, ShellPath.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>The summary line of the Ajustes list: "3.74 · fontes ausentes", "3.74 + pacote de fontes" or
    /// "não instalado".</summary>
    public string Summary => Installed is not { } fw ? "não instalado"
        : SystemFonts ? fw.Display : $"{fw.Version ?? "versão desconhecida"} · fontes ausentes";

    static bool HasEntries(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
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

    public static Task<FirmwareInfo> InstallAsync(Func<Stream> openDocument, string displayName, string fsRoot,
        IProgress<FirmwareProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        using var stream = openDocument();
        if (!stream.CanSeek) throw new NotSupportedException("the firmware file must be opened as a seekable stream (OpenFileDescriptor)");
        using var pup = new Pup(stream);
        return FirmwareInstaller.Install(pup, displayName, fsRoot, progress, ct);
    }, ct);
}
