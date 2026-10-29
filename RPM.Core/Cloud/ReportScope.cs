namespace RPM.Core.Cloud;

/// <summary>
/// Whether a feedback or crash report carries the person who sent it.
///
/// <para>A full report carries the recent log, the screenshot and the reply address, and the server
/// keeps the account. An anonymous report carries the message or the stack, the version, the OS, the
/// architecture and the install id, and nothing else. The server applies the same rule to the
/// <c>scope</c> field, so a report that arrives without "full" is stored anonymously whatever it
/// carries.</para>
///
/// <para>The desktop decides it from the account: full for a closed beta tester, anonymous for
/// everyone else, signed out included. This type is the only way to make a <see cref="FeedbackReport"/>
/// or a <see cref="CrashReport"/>.</para>
/// </summary>
public sealed class ReportScope
{
    public static readonly ReportScope Full = new("full");
    public static readonly ReportScope Anonymous = new(null);

    private ReportScope(string? wire) => Wire = wire;

    /// <summary>The value of the request's <c>scope</c> field: "full", or null.</summary>
    public string? Wire { get; }

    public bool IsFull => Wire is not null;

    /// <param name="isTester">Whether the signed-in account has the closed beta tester mark. False
    /// when nobody is signed in.</param>
    public static ReportScope ForTester(bool isTester) => isTester ? Full : Anonymous;

    public FeedbackReport Feedback(
        string kind, string area, string message, string head, string appVersion, string platform,
        string architecture, string installId, string? recent, string? screenshotPng, long atUtcTicks,
        string? replyTo) => new(
        kind, area, message, head, appVersion, platform, architecture, installId,
        IsFull ? recent : null, IsFull ? screenshotPng : null, atUtcTicks, IsFull ? replyTo : null, Wire);

    public CrashReport Crash(
        string crashId, string head, string appVersion, string platform, string architecture,
        long crashedAtUtcTicks, string installId, string? exception, string? systemReport,
        string? systemReportKind, string? recent) => new(
        crashId, head, appVersion, platform, architecture, crashedAtUtcTicks, installId,
        exception, systemReport, systemReportKind, IsFull ? recent : null, Wire);
}
