using Microsoft.Extensions.DependencyInjection;

using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The shell's part when a play is refused because the sound output is not working. It shows
/// what to try. The player knows why it refused (<see cref="PlaybackService.OutputRefused"/>).
/// Putting a window on screen is the shell's job, so the shell also knows whether one is up.
/// </summary>
public sealed partial class ShellPage
{
    /// <summary>The Loaded event fires again each time the shell is re-parented. Without this flag a
    /// second subscription would open a second window.</summary>
    private bool _outputHooked;

    /// <summary>Whether the message is on screen. A play can be refused twice in a row (a double
    /// click, or the next record starting), and one message is enough. Set and read on the UI
    /// thread only.</summary>
    private bool _outputDialogShowing;

    private void HookAudioOutput()
    {
        if (_outputHooked)
            return;
        _outputHooked = true;

        App.Services.GetRequiredService<PlaybackService>().OutputRefused += OnOutputRefused;
    }

    /// <summary>Raised on whichever thread asked to play. The work is queued onto the UI thread.</summary>
    private void OnOutputRefused(OutputTrouble trouble) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_outputDialogShowing)
            return;

        _outputDialogShowing = true;
        try
        {
            await Dialogs.OutputTroubleDialog.ShowAsync(XamlRoot, trouble);
        }
        finally
        {
            _outputDialogShowing = false;
        }
    });
}
