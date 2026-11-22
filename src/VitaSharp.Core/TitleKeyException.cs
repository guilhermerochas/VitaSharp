using VitaSharp.Core.Localization;

namespace VitaSharp.Core;

/// <summary>
/// Application <see cref="TitleId"/> is still a PFS image (its files encrypted) and the firmware's way to its title key
/// (<see cref="Package.LicenseTitleKey"/>) did not give one that opens it: app0 cannot be mounted, so the launch fails
/// before the process starts. <see cref="Error"/> is the step's error: the secure processor's "not supported"
/// 0x80020004 (the emulated unit has none to verify the licence and decrypt its key), an activation error of SceNpDrm
/// (0x8087xxxx), or 0x80870004 for a key that does not open the image; <see cref="UserMessage"/> is the localized text
/// the launchers show.
/// </summary>
public sealed class TitleKeyException(string titleId, int error, Exception? cause = null)
    : InvalidOperationException($"{titleId}: no title key to mount app0 (error 0x{error:X8}){(cause != null ? ": " + cause.Message : "")}", cause)
{
    public string TitleId { get; } = titleId;

    public int Error { get; } = error;

    public string UserMessage => Loc.Format("TitleKeyNotOpened", TitleId, $"0x{Error:X8}");
}
