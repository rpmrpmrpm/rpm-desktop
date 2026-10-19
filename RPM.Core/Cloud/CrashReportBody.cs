using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Crashes;

namespace RPM.Core.Cloud;

/// <summary>
/// The body of <c>POST /errors/crash</c>, in bytes, no larger than the server accepts
/// (<see cref="RpmCloudClient.MaxCrashRequestBytes"/>).
///
/// <para>The size is measured in bytes after serialising, because escaping makes the body longer
/// than the text: with the default encoder each quote becomes six bytes, and an .ips is full of
/// quotes. The relaxed encoder writes a quote as two bytes. If the body is still too large, the
/// system report (the only part that can be large) is halved until it fits. An .ips goes through
/// <see cref="IpsTrim"/>, so it stays valid JSON. A report that the server refuses for size would be
/// sent again at every launch and never arrive.</para>
/// </summary>
internal static class CrashReportBody
{
    private static readonly CrashReportJson Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static byte[] For(CrashReport report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json.CrashReport);
        while (bytes.Length > RpmCloudClient.MaxCrashRequestBytes && report.SystemReport is { Length: > 0 } system)
        {
            var max = system.Length / 2;
            var smaller = max < 1_000 ? null
                : report.SystemReportKind == SystemCrashReport.Ips ? IpsTrim.Fit(system, max)
                : system[..max];
            report = report with { SystemReport = smaller, SystemReportKind = smaller is null ? null : report.SystemReportKind };
            bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json.CrashReport);
        }
        return bytes;
    }
}

/// <summary>Source-generated because the Mac build is Native AOT. It is made with the options above
/// and not the attribute's, because the encoder can only be set on an options instance.</summary>
[JsonSerializable(typeof(CrashReport))]
internal sealed partial class CrashReportJson : JsonSerializerContext;
