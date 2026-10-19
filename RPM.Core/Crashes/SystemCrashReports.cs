using System.Globalization;
using System.Text.Json;

namespace RPM.Core.Crashes;

/// <summary>The report the operating system wrote when a process crashed, as text.</summary>
/// <param name="Kind"><see cref="Ips"/> (macOS) or <see cref="Wer"/> (Windows Error Reporting).</param>
public sealed record SystemCrashReport(string Kind, string Text)
{
    public const string Ips = "ips";
    public const string Wer = "wer";

    /// <summary>A Windows fail-fast (<c>0xc0000409</c>: <c>abort()</c>, <c>Environment.FailFast</c>, a
    /// stack check failure), read from the report's Exception Code.</summary>
    public bool IsWindowsFailFast =>
        Kind == Wer && string.Equals(WindowsCrashReports.ExceptionCode(Text), "c0000409", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where the operating system keeps crash reports, and which one belongs to a run.</summary>
public interface ISystemCrashReports
{
    /// <summary>The report for process <paramref name="pid"/>, written between <paramref name="from"/>
    /// and <paramref name="to"/>, or null when there is none (yet). The time window tells a run apart
    /// from a later process that got the same id.</summary>
    SystemCrashReport? Find(int pid, DateTimeOffset from, DateTimeOffset to);
}

public static class SystemCrashReports
{
    /// <summary>The reader for this operating system. Linux has none.</summary>
    public static ISystemCrashReports ForThisMachine() =>
        OperatingSystem.IsMacOS() ? MacCrashReports.ForThisUser()
        : OperatingSystem.IsWindows() ? WindowsCrashReports.ForThisUser()
        : NoSystemCrashReports.Instance;
}

/// <summary>For a platform whose crash reports are not read. It is an explicit object, not null.</summary>
public sealed class NoSystemCrashReports : ISystemCrashReports
{
    public static readonly NoSystemCrashReports Instance = new();

    private NoSystemCrashReports() { }

    public SystemCrashReport? Find(int pid, DateTimeOffset from, DateTimeOffset to) => null;
}

/// <summary>macOS: <c>~/Library/Logs/DiagnosticReports/rpm-*.ips</c>. The file is one JSON header line
/// and a JSON body. The body's <c>pid</c> and <c>captureTime</c> say which process crashed, and when.
/// </summary>
public sealed class MacCrashReports(string directory) : ISystemCrashReports
{
    public static MacCrashReports ForThisUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "DiagnosticReports"));

    public SystemCrashReport? Find(int pid, DateTimeOffset from, DateTimeOffset to)
    {
        if (!Directory.Exists(directory))
            return null;

        // Named after the process, which is "rpm" in every build (AssemblyName). The prefix keeps
        // other apps' reports, some of them megabytes, from being read at all.
        foreach (var path in Directory.EnumerateFiles(directory, "rpm-*.ips"))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            if (Matches(text, pid, from, to))
                return new SystemCrashReport(SystemCrashReport.Ips, text);
        }
        return null;
    }

    internal static bool Matches(string text, int pid, DateTimeOffset from, DateTimeOffset to)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0)
            return false;

        try
        {
            using var body = JsonDocument.Parse(text[(newline + 1)..]);
            var root = body.RootElement;
            if (!root.TryGetProperty("pid", out var p) || p.ValueKind != JsonValueKind.Number || p.GetInt32() != pid)
                return false;
            if (!root.TryGetProperty("captureTime", out var c) || ParseTime(c.GetString()) is not { } at)
                return false;
            return at >= from && at <= to;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // InvalidOperationException: a field of another type than the one macOS writes.
            return false;
        }
    }

    /// <summary>Reads the time form that macOS writes: a local date and time with fractions of a second,
    /// then a UTC offset such as +0200. Returns null for anything else.</summary>
    internal static DateTimeOffset? ParseTime(string? text)
    {
        if (text is null)
            return null;
        var space = text.LastIndexOf(' ');
        if (space < 1)
            return null;

        var offset = text[(space + 1)..];
        if (offset.Length != 5 || offset[0] is not ('+' or '-')
            || !int.TryParse(offset.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(offset.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return null;

        if (!DateTime.TryParseExact(text[..space], "yyyy-MM-dd HH:mm:ss.FFFF", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;

        var span = new TimeSpan(hours, minutes, 0);
        return new DateTimeOffset(local, offset[0] == '-' ? -span : span);
    }
}

/// <summary>Windows Error Reporting: <c>Report.wer</c> in <c>AppCrash_rpm.exe_*</c> and
/// <c>BEX64_rpm.exe_*</c> folders. The file holds <c>key=value</c> lines in UTF-16. <c>EventTime</c> is a
/// FILETIME. <c>AppSessionGuid</c>, when it is present, starts with the process id in hex. The format was
/// checked against a managed and a native crash on Windows 11. Both were filed under <c>AppCrash_</c> in
/// the machine-wide <c>ReportArchive</c>.
/// </summary>
public sealed class WindowsCrashReports(IReadOnlyList<string> roots) : ISystemCrashReports
{
    /// <summary>The folder names that WER files rpm's crashes under. <c>AppCrash</c> holds most faults and
    /// unhandled .NET exceptions. <c>BEX64</c> is the name that WER gives a fail-fast (<c>0xc0000409</c>:
    /// <c>Environment.FailFast</c>, <c>abort()</c>, a stack check failure). A fail-fast also leaves no
    /// crash file. This <c>BEX64</c> name is not yet confirmed on a real machine.</summary>
    private static readonly string[] Folders = ["AppCrash_rpm.exe_*", "BEX64_rpm.exe_*"];

    public static WindowsCrashReports ForThisUser()
    {
        var machine = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new WindowsCrashReports(
        [
            Path.Combine(machine, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(machine, "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(user, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(user, "Microsoft", "Windows", "WER", "ReportQueue"),
        ]);
    }

    public SystemCrashReport? Find(int pid, DateTimeOffset from, DateTimeOffset to)
    {
        foreach (var root in roots)
        {
            IEnumerable<string> folders;
            try
            {
                folders = Directory.Exists(root)
                    ? Folders.SelectMany(pattern => Directory.EnumerateDirectories(root, pattern)).ToList()
                    : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in folders)
            {
                string text;
                try { text = File.ReadAllText(Path.Combine(folder, "Report.wer")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                if (Matches(text, pid, from, to))
                    return new SystemCrashReport(SystemCrashReport.Wer, text);
            }
        }
        return null;
    }

    internal static bool Matches(string text, int pid, DateTimeOffset from, DateTimeOffset to)
    {
        var fields = Fields(text);
        if (!fields.TryGetValue("EventTime", out var time) || !long.TryParse(time, out var fileTime))
            return false;

        var at = DateTimeOffset.FromFileTime(fileTime);
        if (at < from || at > to)
            return false;

        if (fields.TryGetValue("AppSessionGuid", out var session)
            && session.Split('-')[0] is { Length: 8 } hex
            && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sessionPid))
            return sessionPid == pid;

        return true;
    }

    /// <summary>The value of the Sig field named "Exception Code", or null. The field is found by name,
    /// because its index differs by event type (Sig[6] in an APPCRASH, Sig[7] in a BEX64).</summary>
    internal static string? ExceptionCode(string text)
    {
        var fields = Fields(text);
        for (var n = 0; n < 32; n++)
            if (fields.TryGetValue($"Sig[{n}].Name", out var name) && name == "Exception Code")
                return fields.GetValueOrDefault($"Sig[{n}].Value");
        return null;
    }

    internal static Dictionary<string, string> Fields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var equals = line.IndexOf('=');
            if (equals > 0)
                fields.TryAdd(line[..equals], line[(equals + 1)..].TrimEnd('\r'));
        }
        return fields;
    }
}
