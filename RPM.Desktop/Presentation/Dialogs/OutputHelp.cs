using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// What to tell the listener about a sound output that is not working: one sentence, and the steps
/// that apply to the cause and the operating system, in the order to try them. Kept apart from the
/// dialog so every case can be held to a test (<c>OutputHelpTests</c>).
/// </summary>
public static class OutputHelp
{
    /// <summary>Which system's settings the steps and the button point at.</summary>
    public enum Desk
    {
        Windows,
        Mac,

        /// <summary>Linux and other systems. The steps stay general, and there is no settings page to
        /// open.</summary>
        Other,
    }

    /// <param name="Sentence">String key of the opening sentence. <c>AudioOut_Refused</c> takes the
    /// device's name as <c>{0}</c>.</param>
    /// <param name="Steps">String keys, in the order to try them. <c>AudioOut_StepPickOther</c> takes
    /// the name of the settings section as <c>{0}</c>.</param>
    /// <param name="SettingsUri">Where "Open sound settings" goes, or null when the system's settings
    /// have nothing to look at for this cause.</param>
    public sealed record Advice(string Sentence, IReadOnlyList<string> Steps, string? SettingsUri);

    /// <summary>The desk this process is running on.</summary>
    public static Desk Here =>
        OperatingSystem.IsWindows() ? Desk.Windows :
        OperatingSystem.IsMacOS() ? Desk.Mac :
        Desk.Other;

    public static Advice For(OutputTrouble trouble, Desk desk) => trouble switch
    {
        // A file that rpm ships is missing. System settings cannot fix this, and no device will help.
        OutputTrouble.LibraryMissing => new Advice(
            "AudioOut_LibraryMissing",
            ["AudioOut_StepReinstall", "AudioOut_StepSecurity"],
            null),

        OutputTrouble.NoDevice => new Advice(
            "AudioOut_NoDevice",
            ["AudioOut_StepConnect", SoundStep(desk), "AudioOut_StepPlayAgain"],
            SettingsUri(desk)),

        // The usual causes, most common first: another app is holding the device, the device is in a bad
        // state, or the wrong device is chosen in rpm.
        OutputTrouble.Refused refused => new Advice(
            refused.Device.Length > 0 ? "AudioOut_Refused" : "AudioOut_RefusedUnnamed",
            ["AudioOut_StepCloseOthers", "AudioOut_StepReconnect", "AudioOut_StepPickOther"],
            SettingsUri(desk)),

        _ => throw new ArgumentOutOfRangeException(nameof(trouble), trouble, "No advice for this trouble."),
    };

    private static string SoundStep(Desk desk) => desk switch
    {
        Desk.Windows => "AudioOut_StepWindowsSound",
        Desk.Mac => "AudioOut_StepMacSound",
        _ => "AudioOut_StepOtherSound",
    };

    /// <summary>The system's own sound page: Settings, System, Sound on Windows; System Settings,
    /// Sound on macOS 13 and later.</summary>
    private static string? SettingsUri(Desk desk) => desk switch
    {
        Desk.Windows => "ms-settings:sound",
        Desk.Mac => "x-apple.systempreferences:com.apple.Sound-Settings.extension",
        _ => null,
    };
}
