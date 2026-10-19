using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RPM.Core.Crashes;

/// <summary>
/// Removes from the operating system's crash report the parts that name this machine or this user,
/// before the report is kept for sending.
///
/// <para>A macOS .ips carries the crash reporter's per-machine key, the boot and sleep session ids,
/// the user id and the trial rollouts; those fields are removed. Both kinds carry the user's name in
/// home-folder paths (<c>/Users/name/</c>, <c>C:\Users\name\</c>); the name becomes <c>~</c>. What is
/// left is the crash itself: the exception, the threads, the loaded images, the OS version and the
/// Mac model.</para>
///
/// <para>A Windows Report.wer is cleaned the other way round: only the lines that describe the crash
/// are kept (<see cref="WerKeys"/>, the Sig lines, the loaded modules and the Windows version), and
/// every other line is dropped. The report ids, the boot id, the region, the consent and upload
/// state and the UI text all go. Keeping a list rather than removing one means a field that a later
/// Windows version adds is dropped until someone decides it may be sent.</para>
/// </summary>
internal static partial class SystemReportPrivacy
{
    /// <summary>The .ips body fields that identify the machine or the user rather than the crash.</summary>
    private static readonly string[] IpsIdentifiers =
        ["crashReporterKey", "bootSessionUUID", "sleepWakeUUID", "userID", "trialInfo"];

    /// <summary>The single Report.wer lines kept: what kind of crash, when, and in which build.</summary>
    private static readonly HashSet<string> WerKeys = new(StringComparer.Ordinal)
    {
        "Version", "EventType", "EventTime", "ReportType", "IsFatal", "FriendlyEventName",
        "AppName", "NsAppName", "OriginalFilename", "TargetAppVer", "AppPath",
    };

    /// <summary>The <c>OsInfo</c> entries kept, by their key: the Windows version and architecture.</summary>
    private static readonly HashSet<string> WerOsKeys = new(StringComparer.Ordinal)
    {
        "vermaj", "vermin", "verbld", "ubr", "versp", "arch",
    };

    public static string Clean(SystemCrashReport report)
    {
        var text = WithoutHomeFolders(report.Text);
        return report.Kind == SystemCrashReport.Ips ? WithoutIdentifiers(text) : OnlyTheCrash(text);
    }

    private static string OnlyTheCrash(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // OsInfo[n].Key and OsInfo[n].Value are two lines; the key line decides for both.
        var osKept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
            if (OsInfoKeyLine().Match(line) is { Success: true } m && WerOsKeys.Contains(m.Groups[2].Value))
                osKept.Add(m.Groups[1].Value);

        var kept = new List<string>();
        foreach (var line in lines)
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            var key = line[..equals];
            if (WerKeys.Contains(key) || SigKey().IsMatch(key) || LoadedModuleKey().IsMatch(key)
                || (OsInfoKey().Match(key) is { Success: true } os && osKept.Contains(os.Groups[1].Value)))
                kept.Add(line);
        }
        return string.Join("\r\n", kept);
    }

    private static string WithoutIdentifiers(string text)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0)
            return text;

        JsonObject? body;
        try { body = JsonNode.Parse(text[(newline + 1)..]) as JsonObject; }
        catch (JsonException) { body = null; }

        // Not an .ips we can read: the home folders are already gone, and IpsTrim cuts it as text.
        if (body is null)
            return text;

        foreach (var key in IpsIdentifiers)
            body.Remove(key);
        return IpsTrim.Join(text[..newline], body);
    }

    /// <summary>The user name in every home-folder path, as <c>~</c>. Also used on the app's own text in a
    /// report (the exception and the ring's lines), which repeats file paths as they were given.</summary>
    internal static string WithoutHomeFolders(string text) =>
        WindowsHome().Replace(MacHome().Replace(text, "/Users/~/"), @"$1~\");

    [GeneratedRegex("""/Users/[^/"\\\s]+/""")]
    private static partial Regex MacHome();

    [GeneratedRegex("""([A-Za-z]:\\Users\\)[^\\"\r\n]+\\""")]
    private static partial Regex WindowsHome();

    [GeneratedRegex("""^Sig\[\d+\]\.(Name|Value)$""")]
    private static partial Regex SigKey();

    [GeneratedRegex("""^LoadedModule\[\d+\]$""")]
    private static partial Regex LoadedModuleKey();

    [GeneratedRegex("""^OsInfo\[(\d+)\]\.(Key|Value)$""")]
    private static partial Regex OsInfoKey();

    [GeneratedRegex("""^OsInfo\[(\d+)\]\.Key=(.*)$""")]
    private static partial Regex OsInfoKeyLine();
}
