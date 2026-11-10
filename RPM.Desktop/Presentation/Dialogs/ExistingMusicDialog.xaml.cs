using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Library;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Asks what rpm may do with the music that is already in a folder chosen for the library: keep the files
/// as they are, or organise them. Used by setup, by Library settings when the music folder is changed, and
/// by both places that open a NAS folder that already holds music.
/// </summary>
public sealed partial class ExistingMusicDialog : ContentDialog
{
    /// <summary>The answer; null when the window was closed.</summary>
    public FileHandling? Choice { get; private set; }

    public ExistingMusicDialog(string folderName)
    {
        InitializeComponent();
        Shell.Title = Loc.Get("ExistingMusic_Title");
        BodyText.Text = Loc.Format("ExistingMusic_Body", folderName);
        Shell.AttachResize(this, minWidth: 520, minHeight: 440, maxWidth: 720, defaultHeight: 480, defaultWidth: 600);
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Choice = FileHandling.Kept;
        Hide();
    }

    private void Organise_Click(object sender, RoutedEventArgs e)
    {
        Choice = FileHandling.Organised;
        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Shows the window. Returns the choice, or null when it was closed.</summary>
    /// <param name="folderName">The folder or NAS, as the person knows it.</param>
    public static async Task<FileHandling?> AskAsync(XamlRoot? xamlRoot, string folderName)
    {
        var dialog = new ExistingMusicDialog(folderName) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog.Choice;
    }
}
