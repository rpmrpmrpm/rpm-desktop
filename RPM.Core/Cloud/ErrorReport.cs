using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>
/// One failure, as the user chose to send it.
///
/// <para>
/// The app keeps every cloud failure in its own activity log and shows them in the settings page.
/// This is the shape that a log line takes when the button beside it is pressed: the reason in
/// full, with enough around it to act on it (what was being done, to what, on which build and which
/// OS). It is sent only when the user presses Report. Crash reports are the one other path, and
/// they too need the user's consent (see <see cref="CrashReport"/>).
/// </para>
/// </summary>
/// <param name="Area">Which part of the app it came from, for example "cloud".</param>
/// <param name="Operation">"upload", "remove" or "restore".</param>
/// <param name="Title">The track, as the user sees it named.</param>
/// <param name="Subtitle">Its album, when there is one.</param>
/// <param name="Detail">The failure's own words, untrimmed. This is the part worth having.</param>
/// <param name="AppVersion">Which build it happened on.</param>
/// <param name="Platform">Which OS it happened on.</param>
/// <param name="AtUtcTicks">When it happened here. This is not when it was reported.</param>
public sealed record ErrorReport(
    string Area,
    string Operation,
    string Title,
    string? Subtitle,
    string Detail,
    string AppVersion,
    string Platform,
    long AtUtcTicks);

/// <summary>What the server says back: the report's id, and nothing about what will happen to it.</summary>
public sealed record ErrorReportResponse(
    [property: JsonPropertyName("errorId")] string? ErrorId);
