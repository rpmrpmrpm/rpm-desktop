using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// A library whose files are kept as they are: the line that says so, the button that lets rpm organise
/// them instead, and the actions on this page that would change the files, turned off.
/// </summary>
public sealed partial class LibrarySettingsPage
{
    private void ShowKept()
    {
        var kept = _library.IsOpen && _library.KeepsFiles;
        KeptPanel.Visibility = kept ? Visibility.Visible : Visibility.Collapsed;
        BackfillButton.IsEnabled = !kept;
        ToolTipService.SetToolTip(BackfillButton, kept ? Loc.Get("Kept_Unavailable") : null);
    }

    private async void OnOrganiseKept(object sender, RoutedEventArgs e)
    {
        var answer = await PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("ExistingMusic_Organise"),
            Loc.Get("Settings_OrganiseKeptConfirm"),
            primaryText: Loc.Get("Settings_OrganiseKeptConfirmButton"),
            closeText: Loc.Get("Common_Cancel"));
        if (answer != ContentDialogResult.Primary)
            return;

        OrganiseKeptButton.IsEnabled = false;
        try
        {
            await _library.ReleaseKeptAsync();
            var progress = new Progress<RPM.Core.Library.RefileProgress>(p =>
                FolderStatus.Text = Loc.Format("Library_Organising", p.Done, p.Total));
            if (await _library.OrganiseIfOwedAsync(_settings.DefaultTemplate, progress) is { } done)
                FolderStatus.Text = Loc.Format("Library_Organised", done.Moved);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("library", $"organising the kept library stopped: {ex.Message}");
            FolderStatus.Text = Loc.Get("Library_OrganiseStopped");
        }
        finally
        {
            OrganiseKeptButton.IsEnabled = true;
            ShowKept();
        }
    }
}
