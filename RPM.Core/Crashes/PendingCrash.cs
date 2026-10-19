using System.Runtime.InteropServices;
using RPM.Core.Cloud;

namespace RPM.Core.Crashes;

/// <summary>The running build and machine, as the harvest describes it.</summary>
public sealed record CrashEnvironment(string AppVersion, string Head, string Platform, string Architecture)
{
    public static CrashEnvironment ForThisProcess(string appVersion, string head) => new(
        appVersion, head, RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
}

/// <summary>A crashed run, packaged by <see cref="CrashHarvest"/> and waiting in <c>crashes/pending</c>
/// for the user's consent. It holds everything that a <see cref="CrashReport"/> carries, except the
/// installation id. That id exists only once a report is sent.
/// </summary>
public sealed record PendingCrash(
    string CrashId,
    string Head,
    string AppVersion,
    string Platform,
    string Architecture,
    long CrashedAtUtcTicks,
    string? Exception,
    string? SystemReport,
    string? SystemReportKind,
    string? Recent)
{
    public CrashReport ToReport(string installId) => new(
        CrashId, Head, AppVersion, Platform, Architecture, CrashedAtUtcTicks, installId,
        Exception, SystemReport, SystemReportKind, Recent);
}
