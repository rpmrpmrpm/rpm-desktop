using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

using RPM.Core.Crashes;
using RPM.Desktop.Services.Diagnostics;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The shell's part in crash reports. It collects reports a short time after the app is up,
/// then asks, sends or discards them, according to the setting.
/// </summary>
public sealed partial class ShellPage
{
    /// <summary>Shown after the first screen and before the update check (12 s), so the two questions
    /// rarely meet. If they do, DialogHost shows the second over the first.</summary>
    private static readonly TimeSpan CrashWait = TimeSpan.FromSeconds(4);

    /// <summary>The Loaded event fires again each time the shell is re-parented. Only one harvest runs
    /// per launch.</summary>
    private bool _crashesHooked;

    private void HookCrashes()
    {
        if (_crashesHooked)
            return;
        _crashesHooked = true;

        var crashes = App.Services.GetRequiredService<CrashReportService>();
        Services.Shell.Ticker.Once(DispatcherQueue, CrashWait, () => _ = HarvestAndActAsync(crashes));
    }

    private async Task HarvestAndActAsync(CrashReportService crashes)
    {
        await crashes.HarvestAsync();

        switch (await crashes.NextAsync())
        {
            case CrashStep.Ask:
                await AskAboutCrashesAsync(crashes);
                break;
            case CrashStep.Send:
                _ = crashes.SendAsync(CancellationToken.None);
                break;
            case CrashStep.Discard:
                await crashes.DiscardAsync();
                break;
        }
    }

    /// <summary>Asked once. Closing the window without an answer leaves the setting at NotAsked, and
    /// the reports stay pending until the next launch.</summary>
    private async Task AskAboutCrashesAsync(CrashReportService crashes)
    {
        var answer = await Dialogs.PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("Crash_PromptTitle"),
            Loc.Get("Crash_PromptBody"),
            primaryText: Loc.Get("Crash_PromptSend"),
            secondaryText: Loc.Get("Crash_PromptDontSend"),
            defaultButton: ContentDialogButton.Primary);

        if (answer == ContentDialogResult.Primary)
            await crashes.Answer(send: true);
        else if (answer == ContentDialogResult.Secondary)
            await crashes.Answer(send: false);
    }
}
