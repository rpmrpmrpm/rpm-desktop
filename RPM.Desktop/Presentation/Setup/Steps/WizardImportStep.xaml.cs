using System.ComponentModel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;

using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;

using SkiaSharp;

using RPM.Desktop.Presentation.Main.Models;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>What the import step finished with. Step three prints it.</summary>
/// <param name="Albums">Records in the collection afterwards.</param>
/// <param name="Tracks">Files filed.</param>
/// <param name="Skipped">True when nothing was imported because the listener stepped past it.</param>
public readonly record struct ImportOutcome(int Albums, int Tracks, bool Skipped);

/// <summary>
/// Step two: the first import.
///
/// <para><b>It drives the ordinary import and adds nothing of its own to the filing.</b> The scan is
/// <see cref="MainModel.AddSourceAsync"/>, and the press is <see cref="MainModel.ImportCommand"/>. What
/// they do is what the library page's import pane does. A second filing path would be a second thing to
/// keep correct.</para>
///
/// <para><b>The two choices are written to settings before the press, not passed to it.</b> The import
/// reads the default template and the move or copy setting. That is correct, not just convenient: the
/// layout chosen during setup is the one later imports should use too. A wizard that filed one way and
/// left the app set to another would teach the listener something untrue.</para>
/// </summary>
public sealed partial class WizardImportStep : UserControl
{
    /// <summary>The import ran, or was stepped past. Carries what to say on step three.</summary>
    public event EventHandler<ImportOutcome>? Finished;

    /// <summary>Nothing was imported and the listener wants the app.</summary>
    public event EventHandler? Skipped;

    /// <summary>The press started or stopped. The host stands the room still while it runs.</summary>
    public event EventHandler<bool>? PressRunningChanged;

    /// <summary>The plan, as grid rows. It is replaced whole whenever the template changes. The grid's
    /// answer to a replace is to clear its selection and go back to the top. That is right here,
    /// because the result is a different plan, not a moved row.</summary>
    private readonly ListGridSource _planRows = new();

    /// <summary>Which plan is current. A template can be changed again while the last plan is still
    /// being built on a worker. The plan that finishes last is not necessarily the one asked for
    /// last.</summary>
    private int _planGeneration;

    /// <summary>How many files the current plan puts in the Unknown Artist folder. It is read off the
    /// plan, not counted in the queue. See <see cref="RefreshButton"/>.</summary>
    private int _unknownArtist;

    private MainModel? _model;
    private string? _libraryPath;
    private string? _sourcePath;
    private NamingTemplate _template = NamingTemplate.ArtistAlbumNumberedTitle;

    public WizardImportStep()
    {
        InitializeComponent();
        Kicker.Text = WizardSteps.Kicker(4);

        PlanGrid.Art = Cover;
        PlanGrid.Columns = PlanColumns();
        PlanGrid.Source = _planRows;
        PlanGrid.Metrics = new GridMetrics { RowHeight = PlanRowHeight, HeaderHeight = 22 };

        Unloaded += (_, _) => DropCovers();
    }

    /// <summary>
    /// Called by the host when this step comes up, with the library that is now open.
    /// </summary>
    public void Begin(string libraryPath)
    {
        _libraryPath = libraryPath;

        // This model is built here and released with the page. MainPage builds its own when the wizard
        // is done. The two never overlap, because this one is discarded by the navigation that creates
        // that one. What they share is the queue. The queue is a temp table in the library's own
        // connection, and it is empty by then.
        _model ??= Microsoft.Extensions.DependencyInjection.ActivatorUtilities
            .CreateInstance<MainModel>(App.Services);
        _model.PropertyChanged -= Model_Changed;
        _model.PropertyChanged += Model_Changed;

        ShowFace(Face.Source);
    }

    /// <summary>
    /// Shown again after the last step sent the user back here.
    ///
    /// <para>It does not call <see cref="Begin"/>. The library is the same, and the model is still the
    /// one this step has been using. Only the face changes. It is the review if a folder was scanned,
    /// and the source card if not.</para>
    /// </summary>
    public void Resume() => ShowFace(_sourcePath is null ? Face.Source : Face.Review);

    // Choosing a source

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var path = await App.Services.GetRequiredService<FolderPickerService>().PickFolderAsync();
        if (path is null)
        {
            // Cancelled. If a folder is already scanned, show its review again. Otherwise the user
            // would see the source card with no way to reach the review of the folder still in the
            // queue.
            if (_sourcePath is not null)
                ShowFace(Face.Review);
            return;
        }

        await ScanAsync(path);
    }

    private void ChangeSource_Click(object sender, RoutedEventArgs e) => ShowFace(Face.Source);

    /// <summary>
    /// Scans a folder and shows what it found.
    ///
    /// <para><b>The queue is emptied first, because here a source REPLACES the last one.</b> The
    /// library page adds folders one after another and keeps them all, which is right there. Here the
    /// question is "where is your music", asked once. Change is somebody correcting their answer. The
    /// clear matters: without it, the new folder was added to the old one. The summary, the chips and
    /// the destination preview then described both, so picking a different folder looked as if it had
    /// done nothing.</para>
    /// </summary>
    private async Task ScanAsync(string source)
    {
        if (_model is null)
            return;

        _sourcePath = source;
        ScanText.Text = Loc.Get("Wizard_ImportTitle");
        ShowFace(Face.Scanning);

        await _model.ClearQueueAsync();

        int found = await _model.AddSourceAsync(source);

        if (found <= 0)
        {
            // A folder with no music in it looks exactly like a scan that failed. So it is reported,
            // not left to an empty review screen. The same rule is kept by ScanSourcesAsync.
            //
            // Go back to no source at all, not to the previous one. The queue it described was emptied
            // above, so a way back to its review would show a plan for nothing.
            _sourcePath = null;
            ShowFace(Face.Source);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("AddMusic_Title"),
                Content = Loc.Format("Queue_ScanNoMusic", source),
                CloseButtonText = Loc.Get("Common_Ok"),
                RequestedTheme = ElementTheme.Dark,
            };
            await dialog.ShowAsync();
            return;
        }

        // EVERY FILE THE SCAN FOUND IS TICKED. Without this, the wizard imports nothing at all.
        //
        // A tick is the listener saying that a file belongs in the collection. On the library page,
        // they tick rows in the queue tree. There is no tree here: the wizard asks for one folder, and
        // the answer is all of it. If nothing is ticked, ImportableRows is empty. The scan strip then
        // reads 0 albums and 0 loose tracks, the destination preview has nothing to plan, and the press
        // files nothing.
        if (App.Services.GetRequiredService<LibraryService>() is { IsOpen: true } opened)
            opened.Db.WholeQueue.CheckAll(true);

        BuildTemplateChoices();
        Refresh();
        ShowFace(Face.Review);
    }

    // The review

    /// <summary>
    /// The naming choices, with the classical shape marked when the scan found any.
    /// </summary>
    private void BuildTemplateChoices()
    {
        Templates.Children.Clear();

        bool classical = HasClassical();

        foreach (var preset in NamingTemplate.Presets)
        {
            var radio = new RadioButton
            {
                GroupName = "Layout",
                FontSize = 13,
                IsChecked = preset.Name == _template.Name,
                Tag = preset,
            };

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new TextBlock { Text = NamingText.Label(preset.Name), VerticalAlignment = VerticalAlignment.Center });

            if (classical && preset.Name == NamingTemplate.ArtistAlbumWorkMovement.Name)
                line.Children.Add(Chip(Loc.Get("Wizard_ClassicalFound"), Cyan));

            radio.Content = line;
            radio.Checked += Template_Checked;
            Templates.Children.Add(radio);
        }
    }

    private void Template_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: NamingTemplate picked })
        {
            _template = picked;
            RefreshPlan();   // The preview is on screen beside the choice, so it must follow the choice.
        }
    }

    private void Originals_Checked(object sender, RoutedEventArgs e) => RefreshButton();

    /// <summary>Everything the review shows, off one read of the queue.</summary>
    private void Refresh()
    {
        if (_model is null)
            return;

        var counts = _model.QueueChipCounts;

        ScanSummary.Text = Loc.Format(
            "Wizard_ScanSummary", counts.Total, AlbumsInQueue(), LooseInQueue());

        Chips.Children.Clear();
        if (counts.NeedsRecognition > 0)
            Chips.Children.Add(Chip(Loc.Format("Queue_StatusNotIdentified", counts.NeedsRecognition), Amber));
        if (counts.Duplicates > 0)
            Chips.Children.Add(Chip(
                counts.Duplicates == 1
                    ? Loc.Get("Queue_StatusDuplicateOne")
                    : Loc.Format("Queue_StatusDuplicates", counts.Duplicates), Amber));
        if (counts.Unreadable > 0)
            Chips.Children.Add(Chip(
                counts.Unreadable == 1
                    ? Loc.Get("Queue_StatusUnreadableOne")
                    : Loc.Format("Queue_StatusUnreadable", counts.Unreadable), Red));

        // Cyan, not amber. Records the account already holds in the cloud, which this computer has no
        // file for, are the good news on this screen. Importing them is what puts the files here.
        if (counts.InCloud > 0)
            Chips.Children.Add(Chip(
                counts.InCloud == 1
                    ? Loc.Get("Queue_StatusInCloudOne")
                    : Loc.Format("Queue_StatusInCloud", counts.InCloud), Cyan));

        SourceText.Text = _sourcePath ?? "";
        SourceChrome.Visibility = Visibility.Visible;

        RefreshPlan();
        RefreshButton();
    }

    /// <summary>
    /// What the press will write, planned against the template that is chosen right now.
    ///
    /// <para><b>It asks the import itself.</b> MainModel.PlanImportAsync runs the same preparation that
    /// the press runs: artist spelling, merging of multi-disc sets, and the folder that a set's earlier
    /// discs already occupy. So this is the filing's own answer, not a second guess. Planning
    /// separately here could give answers that the press would never make. For example, a two-disc
    /// record could be previewed as "The Wall (1)" and "The Wall (2)".</para>
    ///
    /// <para><b>Every row, not a sample.</b> The grid is virtualized. Showing the whole plan costs the
    /// same as showing fifteen lines, and it answers the question the user actually has about a
    /// thousand files. The planning is the expensive part, so it runs on a worker. Rendering a template
    /// over twenty thousand tracks and resolving their collisions is not work for the thread that draws
    /// the screen.</para>
    /// </summary>
    private async void RefreshPlan()
    {
        Clashes.Visibility = Visibility.Collapsed;

        if (_model is null)
        {
            _planRows.Replace([]);
            return;
        }

        // The template as it is right now, captured before the hand-over. It is a field, and the point
        // of this method is that someone is changing it.
        var template = _template;
        var mine = ++_planGeneration;

        var planned = await _model.PlanImportAsync(template);

        // A later template won while this one was being planned. Its own call will land.
        if (mine != _planGeneration)
            return;

        var made = new List<object>(planned.Count);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        int clashes = 0;
        int unknown = 0;

        foreach (var suggestion in planned)
        {
            var relative = suggestion.ProposedRelativePath;
            int slash = relative.LastIndexOf('/');
            var folder = slash > 0 ? relative[..slash] : "";

            made.Add(new PlanRow(Folder: folder,
                                 File: slash > 0 ? relative[(slash + 1)..] : relative));

            // Which source folder this record's cover comes from. The first one wins. A multi-disc set
            // files two source folders into one destination, and both carry the same sleeve.
            if (Path.GetDirectoryName(suggestion.Track.FilePath) is { Length: > 0 } from)
                sources.TryAdd(folder, from);

            // Any segment, not just the first. Which segment carries the artist is decided by the template.
            // {albumartist} templates put it in a different place from {artist} ones.
            if (folder.Length > 0 && folder.Split('/').Contains(NamingTemplate.UnknownArtist))
                unknown++;

            // A proposal that does not match what the template renders was moved aside, because
            // something already has that name. The planner does not report this, and it matters. The
            // template choice cannot explain this result.
            if (!suggestion.IsAlreadyInPlace
                && !string.Equals(relative, template.Render(suggestion.Track), StringComparison.Ordinal))
                clashes++;
        }

        _planRows.Replace(made);
        _coverOf = sources;
        _ = ShowCoversAsync(mine, sources.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        // The button says how many files go in as unidentified. The plan decides that, so the button
        // waits for the plan rather than asking the queue a second question.
        _unknownArtist = unknown;
        RefreshButton();

        if (clashes > 0)
        {
            Clashes.Text = Loc.Format("Wizard_PlanClashes", clashes);
            Clashes.Visibility = Visibility.Visible;
        }
    }

    // The covers

    /// <summary>The longest edge a cover is decoded at. It is twice the row height, so it stays sharp
    /// on a screen with two device pixels per point. It is not larger than that, because there can be
    /// two thousand of these.</summary>
    private const int CoverEdge = (int)PlanRowHeight * 2;

    /// <summary>Source folder to its decoded cover. Null means the folder was checked and has no cover.
    /// That is as useful as a picture. Without this entry, a record with no sleeve would be read from
    /// disk again on every template change.</summary>
    private readonly Dictionary<string, SKImage?> _covers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Destination folder to the source folder its cover comes from. It is rebuilt with each
    /// plan. <see cref="_covers"/> is not rebuilt, because a different template moves a record to a
    /// different folder without changing which sleeve is on it.</summary>
    private Dictionary<string, string> _coverOf = [];

    /// <summary>
    /// What the grid asks for each art cell, by the destination folder the cell shows.
    ///
    /// <para>Reads two dictionaries and nothing else. It runs inside the paint, once per visible
    /// cover per frame: see <see cref="GridFrame.Art"/>.</para>
    /// </summary>
    private SKImage? Cover(string planFolder) =>
        _coverOf.TryGetValue(planFolder, out var source) && _covers.TryGetValue(source, out var art)
            ? art
            : null;

    /// <summary>
    /// Reads and decodes the cover of every album in the plan, and shows them as they arrive.
    ///
    /// <para><b>In batches, not one grid invalidation per cover.</b> A plan can hold two thousand
    /// records; a repaint each would be two thousand full-window rasters on a head with no dirty
    /// rectangles. A batch is drawn when it is ready and the rest keep coming.</para>
    ///
    /// <para>Guarded by the plan's generation, like the plan itself. The covers for a template the user
    /// has since changed are not handed to the grid. Decoding stops as soon as a later plan
    /// starts.</para>
    /// </summary>
    private async Task ShowCoversAsync(int generation, IReadOnlyList<string> folders)
    {
        const int Batch = 24;

        var wanted = folders.Where(f => !_covers.ContainsKey(f)).ToList();
        if (wanted.Count == 0)
        {
            PlanGrid.Invalidate();
            return;
        }

        for (int at = 0; at < wanted.Count; at += Batch)
        {
            if (generation != _planGeneration)
                return;

            var slice = wanted.Skip(at).Take(Batch).ToList();
            var decoded = await Task.Run(() => slice.Select(f => (Folder: f, Art: ReadCover(f))).ToList());

            if (generation != _planGeneration)
            {
                foreach (var (_, art) in decoded)
                    art?.Dispose();
                return;
            }

            foreach (var (folder, art) in decoded)
            {
                if (!_covers.TryAdd(folder, art))
                    art?.Dispose();   // Two plans asked for the same folder. The first one stays.
            }

            PlanGrid.Invalidate();
        }
    }

    /// <summary>
    /// One album folder's cover, off the dispatcher.
    ///
    /// <para>The folder's own picture first, then the art embedded in a track. This is the same order
    /// the rest of the app reads a cover in. The reason is the same too: a folder image is one file to
    /// open, however many tracks the record has. A scan that somebody replaced by hand is the picture
    /// they meant.</para>
    /// </summary>
    private static SKImage? ReadCover(string folder)
    {
        try
        {
            byte[]? bytes = FolderArtWriter.FindFolderImage(folder) is { } image
                ? ThumbCache.GetFile(image, CoverEdge)
                : null;

            if (bytes is not { Length: > 0 })
            {
                // No folder picture. The first track with embedded art answers. A record with no
                // embedded art has no cover. That is a fact, not a failure.
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (TagReader.LoadArt(file)?.Bytes is not { Length: > 0 } embedded)
                        continue;
                    bytes = ThumbCache.Get(embedded, CoverEdge);
                    break;
                }
            }

            return bytes is { Length: > 0 } ? SKImage.FromEncodedData(bytes) : null;
        }
        catch
        {
            // A folder that cannot be read has no cover. Nothing else on this screen depends on it.
            return null;
        }
    }

    private void DropCovers()
    {
        foreach (var art in _covers.Values)
            art?.Dispose();
        _covers.Clear();
        _coverOf = [];
    }

    /// <summary>One line of the plan: a file, and where it will land.</summary>
    /// <param name="Folder">The destination folder, relative to the collection.</param>
    /// <param name="File">What the file will be called once it is there.</param>
    private sealed record PlanRow(string Folder, string File);

    /// <summary>
    /// The plan's two columns.
    /// </summary>
    /// <remarks>The folder column comes first, and it is wider. The shape of the collection that is
    /// about to exist is what the user is judging, and the folder column shows it. The file names
    /// confirm the template's numbering once the folders look right.
    /// </remarks>
    private static GridColumns PlanColumns() => new(
    [
        // The cover, keyed by the destination folder: every track of a record asks for the same
        // picture, so a record's sleeve is decoded once however many rows it fills.
        GridColumn.Art("art", ArtColumnWidth, row => ((PlanRow)row).Folder),
        GridColumn.Text("folder", 300, row => ((PlanRow)row).Folder,
                        header: Loc.Get("Wizard_PlanFolder"), minWidth: 120),
        GridColumn.Text("file", 280, row => ((PlanRow)row).File,
                        header: Loc.Get("Wizard_PlanFile"), minWidth: 120),
    ]);

    /// <summary>The width of the cover column. The picture is square and as tall as a row allows, so
    /// the width is the row height plus the cell padding on each side.</summary>
    private const double ArtColumnWidth = PlanRowHeight + 6;

    /// <summary>Taller than the app's other grids. This grid is read as a picture of a collection, and
    /// a cover in a 22 pt row is too small to see.</summary>
    private const double PlanRowHeight = 34;

    /// <summary>
    /// The press button: which of the two things it is about to do, to how many files, and how many
    /// of them are going in unidentified.
    /// </summary>
    /// <remarks><b>It names the action rather than saying "Import".</b> Move and copy do very
    /// different things to someone's files. Only one of them can be undone by deleting what landed. So
    /// the button says which one it will be. The radio buttons above it say the same thing. A button
    /// that agreed with them only by implication is one that gets pressed without being read.
    ///
    /// <para><b>It states unidentified files. It does not refuse them.</b> Those files land under
    /// Unknown Artist, which is the largest single cause of a collection that somebody has to repair.
    /// So the number is on the button. Blocking would be worse. A wizard that does not let someone past
    /// is worse than one that says what is about to be filed.</para>
    ///
    /// <para><b>The number comes from the plan, not from the tag chip.</b> The chip showed
    /// <c>NeedsRecognition</c>. It counts files missing ANY required tag: artist, album artist, album,
    /// title, track, year and genre. Only a missing artist sends a file to Unknown Artist. In a library
    /// where nobody had set genres, the button read "113 as Unknown Artist" over 113 fully named
    /// records. Their only gap was the genre, and none of them was going there.</para>
    /// </remarks>
    private void RefreshButton()
    {
        if (_model is null)
            return;

        var counts = _model.QueueChipCounts;
        int importable = Math.Max(0, counts.Total - counts.Unreadable);
        bool copying = CopyOption.IsChecked == true;

        ImportButton.Content = _unknownArtist > 0
            ? Loc.Format(copying ? "Wizard_ImportCopyUnknown" : "Wizard_ImportMoveUnknown",
                         importable, _unknownArtist)
            : Loc.Format(copying ? "Wizard_ImportCopy" : "Wizard_ImportMove", importable);

        ImportButton.Visibility = Visibility.Visible;
        ImportButton.IsEnabled = importable > 0 && _model.CanImport;
    }

    // The press

    /// <summary>How long the press runs before the line saying it may take a while comes up.</summary>
    private static readonly TimeSpan PressPatience = TimeSpan.FromSeconds(10);

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || _libraryPath is null)
            return;

        PersistChoices();

        ShowFace(Face.Pressing);
        PressRunningChanged?.Invoke(this, true);

        // A first import of a whole collection takes minutes. After ten seconds a line over the bar says
        // so, so the wait reads as work and not as a hang.
        var slow = new Dissolve(PressSlow, "wizard.press");
        slow.Set(0);
        var patience = DispatcherQueue.CreateTimer();
        patience.Interval = PressPatience;
        patience.IsRepeating = false;
        patience.Tick += (_, _) => slow.To(1, TimeSpan.FromMilliseconds(600));
        patience.Start();

        try
        {
            await _model.RunImportAsync();
        }
        finally
        {
            patience.Stop();
            slow.Set(0);
            PressRunningChanged?.Invoke(this, false);
        }

        // OnThisComputer, because this screen reports what the press just did. Counting Anywhere would
        // also read the cloud catalogue. An account with records in the cloud would then make step
        // three report hundreds of albums, even when no file had moved.
        var library = App.Services.GetRequiredService<LibraryService>();
        int albums = library.IsOpen ? library.Db.CountAlbumFolders(TrackScope.OnThisComputer) : 0;
        int tracks = library.IsOpen ? library.Db.CountTracks(TrackScope.OnThisComputer) : 0;

        Finished?.Invoke(this, new ImportOutcome(albums, tracks, Skipped: false));
    }

    /// <summary>
    /// The two answers on this screen become the app's own settings.
    ///
    /// <para><b>They are the settings, not a copy of them.</b> The import reads
    /// <c>SettingsService.DefaultTemplate</c> and <c>ImportAction</c>. So does the library settings
    /// page, which loads its radio buttons from the same two keys. The layout and the move-or-copy
    /// choice made here are therefore what Settings shows afterwards, and what every later import uses.
    /// A wizard that filed one way and left the app set to another would teach the listener something
    /// untrue about their own collection.</para>
    ///
    /// <para>Written as the user leaves this step, not as each radio button is pressed. Every write
    /// fires <c>settings.Changed</c>, which repaints the backdrop. Here the backdrop is the drifting
    /// room, so one write is enough. This is called on the press and on skip, because a choice the user
    /// made stays their choice whether or not they import anything with it.</para>
    /// </summary>
    private void PersistChoices()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.SetNaming(settings.CustomTemplates, _template.Name);

        settings.ImportAction = CopyOption.IsChecked == true ? ImportAction.Copy : ImportAction.Move;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        PersistChoices();

        var library = App.Services.GetRequiredService<LibraryService>();
        int albums = library.IsOpen ? library.Db.CountAlbumFolders(TrackScope.OnThisComputer) : 0;

        // Skipping on a library that already holds records is not "nothing imported". The listener
        // reopened a collection on step one and stepped past adding to it. Again this counts only this
        // computer. A cloud catalogue with nothing filed under it is not a collection this step put
        // there.
        if (albums > 0)
        {
            Finished?.Invoke(this, new ImportOutcome(
                albums, library.Db.CountTracks(TrackScope.OnThisComputer), Skipped: true));
            return;
        }

        Skipped?.Invoke(this, EventArgs.Empty);
    }

    // Keeping up with the model

    private void Model_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is null)
            return;

        switch (e.PropertyName)
        {
            case nameof(MainModel.ScanCountText):
                ScanText.Text = _model.ScanCountText;
                break;

            case nameof(MainModel.ImportCountText):
                PressText.Text = _model.ImportCountText;
                PressBar.Maximum = _model.ImportProgressMax;
                PressBar.Value = _model.ImportProgressValue;
                PressBar.IsIndeterminate = _model.ImportIsIndeterminate;
                break;

            case nameof(MainModel.QueueChipCounts):
            case nameof(MainModel.CanImport):
                if (ReviewPanel.Visibility == Visibility.Visible)
                    Refresh();
                break;
        }
    }

    // Faces

    private enum Face { Source, Scanning, Review, Pressing }

    private void ShowFace(Face face)
    {
        SourcePanel.Visibility = face is Face.Source ? Visibility.Visible : Visibility.Collapsed;
        ScanPanel.Visibility = face is Face.Scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanSpinner.Spinning = face is Face.Scanning;
        ReviewPanel.Visibility = face is Face.Review ? Visibility.Visible : Visibility.Collapsed;
        PressPanel.Visibility = face is Face.Pressing ? Visibility.Visible : Visibility.Collapsed;

        SourceChrome.Visibility = face is Face.Review ? Visibility.Visible : Visibility.Collapsed;

        // The review fills the window under its heading: the plan is a grid worth all the height there
        // is. Every other face is a small group, centred with its heading.
        var review = face is Face.Review;
        TopSpace.Height = review ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        BottomSpace.Height = TopSpace.Height;
        BodyRow.Height = review ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ImportButton.Visibility = face is Face.Review ? Visibility.Visible : Visibility.Collapsed;

        // Nothing is skippable while the press is running: the files are already moving.
        SkipLink.Visibility = face is Face.Pressing ? Visibility.Collapsed : Visibility.Visible;
    }

    // Chips and colours

    private static readonly Windows.UI.Color Cyan = Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Windows.UI.Color Amber = Windows.UI.Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12);
    private static readonly Windows.UI.Color Red = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);

    private static Brush Dim => new SolidColorBrush(Windows.UI.Color.FromArgb(0x6B, 0xFF, 0xFF, 0xFF));

    private static Border Chip(string text, Windows.UI.Color ink) => new()
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(9, 4, 9, 4),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, ink.R, ink.G, ink.B)),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, ink.R, ink.G, ink.B)),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(ink),
        },
    };

    /// <summary>How many records the queue holds, as the tree counts them.</summary>
    private int AlbumsInQueue() =>
        App.Services.GetRequiredService<LibraryService>() is { IsOpen: true } library
            ? library.Db.WholeQueue.ImportableRows()
                .Select(r => r.Wanted.Album ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(a => a.Length > 0)
            : 0;

    /// <summary>Files that belong to no record. They scatter a collection, so they are counted and
    /// shown before the press, not discovered afterwards.</summary>
    private int LooseInQueue() =>
        App.Services.GetRequiredService<LibraryService>() is { IsOpen: true } library
            ? library.Db.WholeQueue.ImportableRows()
                .Count(r => string.IsNullOrWhiteSpace(r.Wanted.Album))
            : 0;

    private bool HasClassical() =>
        App.Services.GetRequiredService<LibraryService>() is { IsOpen: true } library
        && library.Db.WholeQueue.ImportableRows()
            .Any(r => r.Wanted.Genre is { Length: > 0 } g
                      && g.Contains("classical", StringComparison.OrdinalIgnoreCase));
}
