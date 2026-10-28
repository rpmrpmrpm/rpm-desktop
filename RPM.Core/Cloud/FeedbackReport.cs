namespace RPM.Core.Cloud;

/// <summary>
/// One thing a user decided was worth saying, as sent to <c>POST /errors/feedback</c>: something
/// that went wrong, or something that could be better.
///
/// <para>Nothing in the app failed first. <see cref="Message"/> is what the user typed and is the
/// whole report; the rest is the context needed to act on it. It is sent only when the Send button
/// in the feedback window is pressed. <see cref="ErrorReport"/> is the other way a failure is
/// reported by hand, from the button beside a cloud failure, and <see cref="CrashReport"/> is the
/// one path the app takes on its own, which needs the user's consent.</para>
/// </summary>
/// <param name="Kind">"problem" or "idea". The server stores anything else as "problem".</param>
/// <param name="Area">Which part of the app the user picked: "playback", "library", "artwork",
/// "cloud", "radio", "sharing", "interface" or "other". The server does not check the value.</param>
/// <param name="Message">What the user wrote. A report with nothing here is refused.</param>
/// <param name="Head">"mac" or "windows".</param>
/// <param name="AppVersion">Which build.</param>
/// <param name="Platform">Which OS.</param>
/// <param name="Architecture">"arm64" or "x64".</param>
/// <param name="InstallId">A random id for this installation, used only to tell machines apart. It
/// is the same id the crash reports use.</param>
/// <param name="Recent">The last lines of this run's diagnostic ring, or null.</param>
/// <param name="ScreenshotPng">The window as it was when the report was opened, PNG, base64, or
/// null when the tester turned the screenshot off.</param>
/// <param name="AtUtcTicks">When the report was written, by this machine's clock.</param>
public sealed record FeedbackReport(
    string Kind,
    string Area,
    string Message,
    string Head,
    string AppVersion,
    string Platform,
    string Architecture,
    string InstallId,
    string? Recent,
    string? ScreenshotPng,
    long AtUtcTicks);
