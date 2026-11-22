using Microsoft.UI.Xaml;
using RPM.Core.Library;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// The collection database card: whether the database is kept off the music folder's disk, and the
/// database folder it is kept in then. Both are settings only. The database moves when the library is next
/// opened (see <see cref="MirrorLocation"/>), because the open database is in use until then.
/// </summary>
public sealed partial class LibrarySettingsPage
{
    private void InitDatabase()
    {
        _loading = true;
        DatabaseOffDiskToggle.IsOn = _settings.LibraryDatabaseOffMusicDisk;
        _loading = false;

        ShowDatabaseFolder();
        ShowDatabaseNow();
        _ = SuggestOffDiskAsync();
    }

    /// <summary>The file the open library is using. Also called after the music folder changes.</summary>
    private void ShowDatabaseNow() =>
        DatabaseNowText.Text = _library.IsOpen ? Loc.Format("Settings_DatabaseNow", _library.Db.DatabasePath) : "";

    private void ShowDatabaseFolder()
    {
        DatabaseFolderText.Text = _settings.LibraryDatabaseFolderInUse;
        DatabaseDefaultButton.Visibility = _settings.LibraryDatabaseFolder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SayDatabaseChanged() => DatabaseStatus.Visibility = Visibility.Visible;

    private void OnDatabaseOffDiskToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.LibraryDatabaseOffMusicDisk = DatabaseOffDiskToggle.IsOn;
        SpinningNote.Visibility = Visibility.Collapsed;
        SayDatabaseChanged();
    }

    private async void OnChangeDatabaseFolder(object sender, RoutedEventArgs e)
    {
        var folder = await _picker.PickFolderAsync(canCreateDirectories: true);
        if (string.IsNullOrWhiteSpace(folder)
            || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_settings.LibraryDatabaseFolderInUse)),
                StringComparison.OrdinalIgnoreCase))
            return;

        _settings.LibraryDatabaseFolder = folder;
        ShowDatabaseFolder();
        SayDatabaseChanged();
    }

    private void OnDefaultDatabaseFolder(object sender, RoutedEventArgs e)
    {
        if (_settings.LibraryDatabaseFolder is null)
            return;

        _settings.LibraryDatabaseFolder = null;
        ShowDatabaseFolder();
        SayDatabaseChanged();
    }

    /// <summary>
    /// Shows the note that suggests the switch, once, when the music folder is on a spinning disk and the
    /// database folder is on a disk with no seek penalty (see <see cref="SpinningDisk"/>). Not for a library
    /// on a NAS, whose database is never in its folder. The disks are asked on a worker: each answer is one
    /// call to the storage driver, but it is still a call to a device.
    /// </summary>
    private async Task SuggestOffDiskAsync()
    {
        if (_settings.LibraryDatabaseOffMusicDisk || _settings.SpinningDiskNoteDismissed
            || _settings.LibraryNas is not null || _settings.LibraryPath is not { } music)
            return;

        var databases = _settings.LibraryDatabaseFolderInUse;
        var (musicSpins, databasesSpin) = await Task.Run(() => (SpinningDisk.Holds(music), SpinningDisk.Holds(databases)));

        if (musicSpins == true && databasesSpin == false && !_settings.LibraryDatabaseOffMusicDisk)
            SpinningNote.Visibility = Visibility.Visible;
    }

    /// <summary>Turns the switch on, as the person asked, through the switch itself so the setting and the
    /// line under the card follow from one place.</summary>
    private void OnSpinningYes(object sender, RoutedEventArgs e)
    {
        _settings.SpinningDiskNoteDismissed = true;
        DatabaseOffDiskToggle.IsOn = true;
        SpinningNote.Visibility = Visibility.Collapsed;
    }

    private void OnSpinningNo(object sender, RoutedEventArgs e)
    {
        _settings.SpinningDiskNoteDismissed = true;
        SpinningNote.Visibility = Visibility.Collapsed;
    }
}
