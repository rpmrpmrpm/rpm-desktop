using System.Runtime.InteropServices;
using RPM.Core.Crashes;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// Sends a report when the Intel Mac build starts and does not show its main window. See
/// <see cref="BootReports"/> for what a report holds and how a run is marked.
///
/// <para>It is on only in the Intel Mac builds (an x64 process on macOS). Those builds run on older
/// Macs and older macOS versions that the developers cannot test on. A person whose start-up shows
/// nothing cannot answer the crash report question, so this report is sent without asking. It is not
/// sent when the person has already said no to crash reports, or when that answer cannot be read.
/// No account goes with it: no token is sent.</para>
///
/// <para>Two reports, from two sides of the same failure. The running app sends one when its window is
/// not shown <see cref="Deadline"/> after launch. The next launch sends one for every earlier run that
/// ended without showing its window, which covers a crash and a person who quit before the deadline.
/// Each has its own crash id, so a retry of either is dropped by the server.</para>
///
/// <para><see cref="Off"/> is an explicit object for every other build, so no call site asks which
/// build it is in.</para>
/// </summary>
internal sealed class BootWatch
{
    /// <summary>After the splash backstop (30 seconds after launch), so the report includes what the
    /// backstop did.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(40);

    public static readonly BootWatch Off = new(null, null, Deadline);

    private readonly BootReports? _reports;
    private readonly BootReports.Run? _run;
    private readonly TimeSpan _deadline;
    private readonly ManualResetEventSlim _shown = new();

    private BootWatch(BootReports? reports, BootReports.Run? run, TimeSpan deadline)
    {
        _reports = reports;
        _run = run;
        _deadline = deadline;
    }

    public static BootWatch ForThisBuild()
    {
        var deadline = Deadline;
        var on = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
#if DEBUG
        // RPM_BOOT_WATCH=on turns the watch on in any build, and RPM_BOOT_WATCH_SECONDS sets the deadline.
        // A deadline shorter than the start-up tests the report from a start-up that works.
        if (DevSwitch.Read("RPM_BOOT_WATCH") == "on")
            on = true;
        if (int.TryParse(DevSwitch.Read("RPM_BOOT_WATCH_SECONDS"), out var seconds) && seconds > 0)
            deadline = TimeSpan.FromSeconds(seconds);
#endif
        if (!on)
            return Off;

        // The same value that CrashRecorder names the crash file with, so the report finds that file.
        var start = new DateTimeOffset(App.ProcessStart);
        return new BootWatch(
            new BootReports(
                Path.Combine(Platform.LocalData.Dir, "boot"),
                Scope.Dir,
                App.CrashDirectory,
                Platform.LocalData.Dir,
                SystemCrashReports.ForThisMachine(),
                CrashEnvironment.ForThisProcess(App.VersionText, App.HeadName)),
            new BootReports.Run(Environment.ProcessId, start.ToUnixTimeMilliseconds()),
            deadline);
    }

    /// <summary>Marks this run as not shown, then starts the watch thread. Called once, from the entry
    /// point, before the app host exists. The marker is written on this thread, so it exists before
    /// <see cref="Shown"/> can be called.</summary>
    public void Start()
    {
        if (_reports is null || _run is null)
            return;

        try { _reports.Begin(_run); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without a marker there is nothing to report from, now or at the next launch.
            Said.Line("boot", $"boot watch not started: {e.Message}");
            return;
        }

        // A thread of its own and not the pool: a start-up that has stopped may be holding pool threads.
        new Thread(Watch) { IsBackground = true, Name = "rpm boot watch" }.Start();
    }

    /// <summary>The main window is on screen. Called by the shell when its reveal ends.</summary>
    public void Shown()
    {
        if (_reports is null || _run is null)
            return;

        _shown.Set();
        try { _reports.Shown(_run); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The marker stays, so the next launch reports a run that did show its window. Said here so
            // that report can be recognised.
            Scope.Said("boot", $"boot marker not removed: {e.Message}");
        }
    }

    private void Watch()
    {
        try
        {
            if (SettingsService.StoredCrashAnswer() is not { } answer || answer.Consent == CrashConsent.DontSend)
                return;

            var installId = _reports!.InstallId(answer.InstallId);
            var cloud = new RPM.Core.Cloud.RpmCloudClient(
                RPM.Core.Net.TransientHttp.Client(RPM.Core.Net.RpmApi.Deployed, TimeSpan.FromSeconds(60)),
                RPM.Core.Net.Wire.Storage(TimeSpan.FromSeconds(60)),
                _ => Task.FromResult<string?>(null));

            SendEnded(cloud, installId);

            var left = _deadline - App.Uptime;
            if ((left > TimeSpan.Zero && _shown.Wait(left)) || _reports.IsShown(_run!))
                return;

            var id = cloud.ReportCrashAsync(_reports.ForStarting(_run!, installId, DateTimeOffset.UtcNow), CancellationToken.None)
                .GetAwaiter().GetResult();
            Scope.Said("boot", id is null ? "boot report not taken by the server" : $"boot report sent: {id}");
        }
        catch (Exception e)
        {
            // This thread is the last place that could report the failure, so it is only logged.
            Scope.Said("boot", $"boot watch failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Reports the earlier runs that ended without showing the window. A run whose report the
    /// server did not take keeps its marker for the next launch.</summary>
    private void SendEnded(RPM.Core.Cloud.RpmCloudClient cloud, string installId)
    {
        if (Scope.LivePids() is not { } live)
            return;

        foreach (var run in _reports!.Ended(Environment.ProcessId, live))
        {
            if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(run.ProcessStartUnixMs) > BootReports.MaxAge)
            {
                _reports.Settled(run);
                continue;
            }

            var id = cloud.ReportCrashAsync(_reports.ForEnded(run, installId), CancellationToken.None)
                .GetAwaiter().GetResult();
            Scope.Said("boot", id is null
                ? $"boot report for run {run.Pid} not taken by the server"
                : $"boot report for run {run.Pid} sent: {id}");
            if (id is not null)
                _reports.Settled(run);
        }
    }
}
