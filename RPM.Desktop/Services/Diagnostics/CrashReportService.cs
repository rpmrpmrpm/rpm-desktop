using RPM.Core.Cloud;
using RPM.Core.Crashes;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// Packages the runs that crashed since the last launch and sends them once the user has agreed.
/// The shell decides whether to ask the user (ShellPage.Crashes.cs). This follows the same split as
/// AppUpdates and ShellPage.Updates.
///
/// <para>The pending folder is read whenever a decision is made, not counted once by the harvest, so
/// nothing here acts on a stale number.</para>
/// </summary>
public sealed class CrashReportService(SettingsService settings, RpmCloudClient cloud)
{
    private readonly PendingCrashes _pending = new(CrashHarvest.PendingDirectoryIn(App.CrashDirectory));

    /// <summary>Runs on the thread pool. Never throws. If the harvest fails, every file is kept for the
    /// next launch.</summary>
    public Task HarvestAsync() => Task.Run(() =>
    {
        // Without the process list, a run cannot be shown to have finished. Scope's own sweep works the
        // same way.
        if (Scope.LivePids() is not { } live)
            return;

        try
        {
            new CrashHarvest(
                App.CrashDirectory, Scope.Dir, Environment.ProcessId, live,
                SystemCrashReports.ForThisMachine(),
                CrashEnvironment.ForThisProcess(App.VersionText, App.HeadName)).Run(DateTimeOffset.UtcNow);
        }
        catch (Exception e)
        {
            // Nothing awaits this task, so nothing else would report a failure. Reports from earlier
            // launches must still be offered, so the failure is only logged.
            Scope.Said("crash", $"harvest left for the next launch: {e.GetType().Name}: {e.Message}");
        }
    });

    /// <summary>Decides what the launch does about pending reports. The files are read on the thread
    /// pool: up to ten files of about 1 MB each.</summary>
    public async Task<CrashStep> NextAsync()
    {
        var pending = await Task.Run(() => _pending.Read().Count);
        return CrashConsentRule.Next(settings.CrashConsent, pending);
    }

    /// <summary>Handles the user's answer. Saves the answer, then sends or discards the pending
    /// reports.</summary>
    public Task Answer(bool send)
    {
        settings.CrashConsent = send ? CrashConsent.Send : CrashConsent.DontSend;
        return send ? SendAsync(CancellationToken.None) : DiscardAsync();
    }

    /// <summary>Sends each pending report and removes it once the server has returned its id. Stops as
    /// soon as the setting is not Send. A send already in progress finishes.
    ///
    /// <para>The reports are read on the thread pool. The loop then resumes on the caller's thread, the
    /// UI thread, because the settings are read and written there. Do not add ConfigureAwait(false)
    /// here.</para></summary>
    public async Task SendAsync(CancellationToken ct)
    {
        foreach (var pending in await Task.Run(() => _pending.Read(), ct))
        {
            if (settings.CrashConsent != CrashConsent.Send)
                return;

            var id = await cloud.ReportCrashAsync(pending.ToReport(settings.EnsureCrashInstallId()), ct);
            if (id is not null)
                _pending.Remove(pending.CrashId);
        }
    }

    public Task DiscardAsync() => Task.Run(_pending.RemoveAll);
}
