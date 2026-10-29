using System;
using System.Threading;
using System.Threading.Tasks;
using RPM.Core.Cloud;
using RPM.Core.Crashes;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// Packages what a user wrote in the feedback window and sends it.
///
/// <para>This follows the split <see cref="CrashReportService"/> uses: the window decides what the
/// user is asked (ShellPage.Feedback.cs) and this puts the report together. Nothing here is sent
/// on its own schedule. One press of Send is one call to <see cref="SendAsync"/>.</para>
///
/// <para>The activity log is read here rather than in the window, because reading the ring is file
/// work and the window is on the UI thread.</para>
/// </summary>
public sealed class FeedbackService(SettingsService settings, RpmCloudClient cloud, SupporterService supporter)
{
    /// <summary>
    /// Whether a report sent now would carry the log, the screenshot and the reply address: only for
    /// a closed beta tester's account (<see cref="ReportScope"/>). The menu offers Send feedback only
    /// when this is full, and it is read again at the moment of sending, so a sign-out while the
    /// window is open sends nothing about the person. A tester is always signed in, so a reply goes
    /// to the account's address and the window asks for none.
    /// </summary>
    public ReportScope CurrentScope => ReportScope.ForTester(supporter.Standing?.IsTester == true);

    /// <summary>
    /// Sends one report and says whether the server took it.
    /// </summary>
    /// <param name="kind">A problem or a suggestion. <see cref="FeedbackKind"/> holds the
    /// values.</param>
    /// <param name="area">Which part of the app the user picked. <see cref="FeedbackArea"/> holds
    /// the values.</param>
    /// <param name="message">What the user wrote. The caller has already checked it says
    /// something.</param>
    /// <param name="screenshotPng">The window as it was, PNG, or null when the user turned the
    /// screenshot off. A shot over what the server accepts is dropped rather than sent, so the
    /// report still arrives.</param>
    /// <param name="activity">Whether to read this run's diagnostic ring and send it.</param>
    /// <returns>True when the report landed and has an id.</returns>
    public async Task<bool> SendAsync(
        string kind, string area, string message, byte[]? screenshotPng, bool activity,
        CancellationToken ct)
    {
        var scope = CurrentScope;
        var recent = activity && scope.IsFull
            ? await Task.Run(() => RingTail.ActivityForThisRun(Scope.Dir, Environment.ProcessId), ct)
            : null;

        var picture = screenshotPng is { Length: > 0 } png && png.Length <= RpmCloudClient.MaxScreenshotBytes
            ? Convert.ToBase64String(png)
            : null;

        var report = scope.Feedback(
            kind,
            area,
            message,
            App.HeadName,
            App.VersionText,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            settings.EnsureCrashInstallId(),
            recent,
            picture,
            DateTime.UtcNow.Ticks,
            replyTo: null);

        var id = await cloud.SendFeedbackAsync(report, ct);
        if (id is null)
            Scope.Said("feedback", "the report was not accepted");

        return id is not null;
    }
}

/// <summary>
/// Whether the user is reporting something broken or asking for something better, and the string
/// key for each label. The API's <c>FeedbackLimits.Problem</c> and <c>Idea</c>.
/// </summary>
public static class FeedbackKind
{
    /// <summary>The choices, in the order the window lists them. A problem is first: it is the one
    /// that can be costing somebody their afternoon.</summary>
    public static readonly (string Value, string StringKey, string PlaceholderKey)[] All =
    [
        ("problem", "Feedback_KindProblem", "Feedback_PlaceholderProblem"),
        ("idea", "Feedback_KindIdea", "Feedback_PlaceholderIdea"),
    ];
}

/// <summary>
/// The values the feedback window puts in a report's area field, and the string key for each one's
/// label. The server does not check the value, and the admin site shows it as it arrives, so this
/// list is the only place the two sides agree on.
/// </summary>
public static class FeedbackArea
{
    /// <summary>The choices, in the order the window lists them. "Other" is first, because a user
    /// who does not know which part something belongs to must not have to guess.</summary>
    public static readonly (string Value, string StringKey)[] All =
    [
        ("other", "Feedback_AreaOther"),
        ("playback", "Feedback_AreaPlayback"),
        ("library", "Feedback_AreaLibrary"),
        ("artwork", "Feedback_AreaArtwork"),
        ("cloud", "Feedback_AreaCloud"),
        ("radio", "Feedback_AreaRadio"),
        ("sharing", "Feedback_AreaSharing"),
        ("interface", "Feedback_AreaInterface"),
    ];
}
