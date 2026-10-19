using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Core.Import;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>What a saved naming template means for the records already on disk.
///
/// <para>A template decides where a file goes. Saving one can mean two different things under one word.
/// It can apply to what arrives from now on. Or it can describe how the whole collection should look.
/// The app cannot tell which one is meant, so it asks once, at save time. The question uses the user's
/// own terms ("all my music" or "from here on"), not the app's.</para>
///
/// <para>Re-filing moves files on disk, so it runs where it can be watched and stopped. It shows a bar,
/// the file being moved, and a plain count of the result. Stopping leaves the files that have already
/// moved where they are. The library is updated file by file, so it always matches the disk.</para>
/// </summary>
public sealed partial class ApplyNamingDialog : ContentDialog
{
    private readonly LibraryService _library;
    private readonly NamingTemplate _template;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;

    private ApplyNamingDialog(LibraryService library, NamingTemplate template, ILogger log)
    {
        _library = library;
        _template = template;
        _log = log;

        InitializeComponent();
        Shell.AttachResize(this, minWidth: 520, minHeight: 380, maxWidth: 760, defaultHeight: 430);

        // The window names the template and shows the pattern in full. It moves files, so it states the
        // pattern it will use. The user does not have to remember what was selected.
        TemplateText.Text = Loc.Format("Naming_ApplyTemplate", NamingText.Label(template.Name));
        PatternText.Text = template.Preview();

        var count = _library.IsOpen ? _library.Count : 0;
        CountText.Text = Loc.Format("Naming_ApplyCount", count);
        ApplyButton.IsEnabled = count > 0;

        // A run in progress keeps the window open. If the window closed, the move would carry on with
        // nothing showing it. The user must stop the run first.
        Closing += (_, args) => args.Cancel = _cts is not null;
    }

    private void OnlyNew_Click(object sender, RoutedEventArgs e) => Hide();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _cts?.Cancel();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_library.IsOpen || _library.Root is not { } root)
        {
            Hide();
            return;
        }

        ChoicePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        OnlyNewButton.Visibility = Visibility.Collapsed;
        ApplyButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;

        var db = _library.Db;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Built on the UI thread, so its callback comes back to the UI thread.
        var progress = new Progress<RefileProgress>(p =>
        {
            Bar.Maximum = Math.Max(1, p.Total);
            Bar.Value = p.Done;
            ProgressText.Text = Loc.Format("Naming_ApplyProgress", p.Done, p.Total);
            CurrentText.Text = p.Current;
        });

        _log.LogInformation("[refile] starting under template {Template}", _template.Name);

        RefileOutcome outcome;
        bool stopped = false;
        try
        {
            outcome = await Task.Run(
                () => LibraryRefiler.Refile(db, root, _template, progress, token));
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            outcome = RefileOutcome.Empty;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[refile] failed");
            stopped = true;
            outcome = RefileOutcome.Empty;
            CurrentText.Text = Loc.Format("Naming_ApplyFailed", Faults.Note("refile", "re-filing the collection", ex).Say());
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        foreach (var refusal in outcome.Refusals)
            _log.LogWarning("[refile] file refused, left as it was — {Detail}", refusal);
        _log.LogInformation("[refile] done: {Moved} moved, {InPlace} already in place, {Refused} refused, stopped={Stopped}",
            outcome.Moved, outcome.AlreadyInPlace, outcome.Refused, stopped);

        CurrentText.Text = "";
        ProgressText.Text = stopped
            ? Loc.Get("Naming_ApplyStopped")
            : Loc.Format("Naming_ApplyDone", outcome.Moved, outcome.AlreadyInPlace);
        if (outcome.Refused > 0)
        {
            RefusedText.Text = Loc.Format("Naming_ApplyRefused", outcome.Refused);
            RefusedText.Visibility = Visibility.Visible;
        }

        StopButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;

        // The wall, the grids and the deck read file paths, and those paths have just changed. Tell
        // them to reload.
        _library.NotifyChanged();
    }

    /// <summary>Asks what the saved template should mean, and re-files the library if that is the
    /// answer. Returns when the window is closed.</summary>
    public static Task ShowAsync(XamlRoot xamlRoot, IServiceProvider services, NamingTemplate template)
    {
        var dialog = new ApplyNamingDialog(
            services.GetRequiredService<LibraryService>(),
            template,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.Refile"))
        { XamlRoot = xamlRoot };
        return DialogHost.ShowAsync(dialog);
    }
}
