namespace RPM.Core.Cloud;

/// <summary>
/// One crashed run, as sent to <c>POST /errors/crash</c>. It is sent only when the user has agreed
/// to crash reports (the setting, asked once). The server parses the texts, and the app sends them
/// raw.
/// </summary>
/// <param name="CrashId">Worked out from the run, so a second send of the same crash is recognised.</param>
/// <param name="Head">"mac" or "windows".</param>
/// <param name="AppVersion">The crashed build's version when the crash file states it, otherwise the
/// running build's. The server prefers the version inside the system report when there is one.</param>
/// <param name="InstallId">A random id for this installation, used only to count machines.</param>
/// <param name="Exception">The managed exceptions, when there were any.</param>
/// <param name="SystemReport">The operating system's report, when there was one.</param>
/// <param name="SystemReportKind">"ips" or "wer".</param>
/// <param name="Recent">The last lines of the run's diagnostic ring.</param>
public sealed record CrashReport(
    string CrashId,
    string Head,
    string AppVersion,
    string Platform,
    string Architecture,
    long CrashedAtUtcTicks,
    string InstallId,
    string? Exception,
    string? SystemReport,
    string? SystemReportKind,
    string? Recent);
