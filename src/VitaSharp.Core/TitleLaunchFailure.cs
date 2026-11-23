using VitaSharp.Core.Localization;

namespace VitaSharp.Core;

/// <summary>
/// A title whose load failed (<see cref="Emulator.LoadTitle"/> threw): its process never started, and the front ends
/// say why in plain words instead of closing without a message. The system's refusals keep their own text (no
/// license, a key that does not open the files); anything else is the loader's reason with the log's place.
/// </summary>
public static class TitleLaunchFailure
{
    /// <summary>The emulator process's exit code when the title could not be loaded for a reason other than a missing
    /// license (<see cref="Package.TitleLicense.ExitCode"/>); its last line on the error output is <see cref="Message"/>.</summary>
    public const int ExitCode = 4;

    /// <summary>The localized text for the load failure <paramref name="failure"/> of title <paramref name="titleId"/>,
    /// pointing at <paramref name="logPath"/> (the session's log; null: "last.log").</summary>
    public static string Message(string titleId, Exception failure, string? logPath)
    {
        return failure switch
        {
            TitleLicenseMissingException noLicense => noLicense.UserMessage,
            TitleKeyException noKey => noKey.UserMessage,
            _ => Loc.Format("TitleLoadFailed", titleId, failure.Message, logPath ?? "last.log"),
        };
    }
}
