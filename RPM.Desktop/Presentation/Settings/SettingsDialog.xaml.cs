using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// Settings: a category rail on the left, the chosen page on the right.
///
/// <para>A dialog on the shared <see cref="ResizableDialogShell"/> card, like every other window in
/// rpm, and not a second OS window. There is one card, one resize grip and one way to close. Being
/// modal costs nothing here. Each page saves its changes as they are made, so dismissing the dialog
/// loses nothing.</para>
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly IServiceProvider _services;

    public SettingsDialog(IServiceProvider services, string? section = null)
    {
        _services = services;
        InitializeComponent();

        Shell.AttachResize(this, minWidth: 760, minHeight: 520, maxWidth: 1280, defaultHeight: 660,
            defaultWidth: 940);

        // Found by tag, not by position. The rail is a hand-written list. A section inserted into it
        // must not silently point every caller's name at its neighbour.
        RailList.SelectedItem = RailList.Items
            .OfType<ListViewItem>()
            .FirstOrDefault(item => item.Tag as string == section)
            ?? RailList.Items[0];
    }

    private void OnRailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RailList.SelectedItem is not ListViewItem { Tag: string tag })
            return;

        // The page is built here, not by Frame.Navigate(type). These pages take their services in the
        // constructor, and a Frame's navigation has no dependency injection.
        PageHost.Content = tag switch
        {
            "Library" => new LibrarySettingsPage(_services),
            "Display" => new DisplaySettingsPage(_services),
            "Sound" => new SoundSettingsPage(_services),
            "Cloud" => new CloudSettingsPage(_services),
            "Catalog" => new CatalogSettingsPage(),
            _ => new ProfileSettingsPage(_services),
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    public static Task ShowAsync(XamlRoot xamlRoot, IServiceProvider services, string? section = null)
        => DialogHost.ShowAsync(
            new SettingsDialog(services, section) { XamlRoot = xamlRoot });
}
