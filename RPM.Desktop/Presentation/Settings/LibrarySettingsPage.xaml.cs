using System.Collections.ObjectModel;
using System.ComponentModel;
using RPM.Core.Library;
using RPM.Core.Import;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>Where the collection lives and how files are named in it. This was the first setting the
/// app had, and others were added around it. This is the page the Library section opens on. The look of
/// the app is on the Display page, next door.</summary>
public sealed partial class LibrarySettingsPage : Page
{
    private readonly IServiceProvider _services;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly FolderPickerService _picker;

    /// <summary>Set while the controls are filled from the service. This stops seeding a switch from
    /// being read as the user turning it.</summary>
    private bool _loading;

    public LibrarySettingsPage(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<SettingsService>();
        _library = services.GetRequiredService<LibraryService>();
        _picker = services.GetRequiredService<FolderPickerService>();

        InitializeComponent();

        _loading = true;
        PathText.Text = _settings.LibraryPath ?? "";
        ImportActionRadios.SelectedIndex = (int)_settings.ImportAction;
        MultiDiscRadios.SelectedIndex = (int)_settings.MultiDiscMode;
        FingerprintToggle.IsOn = _settings.FingerprintLibrary;
        ShareListeningToggle.IsOn = _settings.ShareListening;
        AnalysisTagsToggle.IsOn = _settings.WriteAnalysisTags;
        CatalogueFillToggle.IsOn = _settings.ImportFillFromCatalogue;
        _loading = false;

        BuildJumpLinks();
        InitRequiredTags();
        InitSharing();
        RefreshFingerprintStatus();
        RefreshAnalysisTagStatus();
        InitNaming();
        _ = RefreshDiscDefaultAsync();
    }

    // Table of contents

    /// <summary>
    /// The links under the page heading, one per card, in the order the cards stand in.
    ///
    /// <para>Built in code rather than written out in the markup, because each pair (the label and the
    /// card it jumps to) must stay correct. If the links were written out twice, a card that moved
    /// would keep a link that points at its old neighbour, and nothing in the build would report
    /// it.</para>
    /// </summary>
    private void BuildJumpLinks()
    {
        (string Key, FrameworkElement Card)[] sections =
        [
            ("Settings_MusicFolder", FolderCard),
            ("Share_Title", SharingCard),
            ("Settings_Naming", NamingCard),
            ("Settings_ImportAction", ImportActionCard),
            ("Settings_RequiredTagsTitle", RequiredTagsCard),
            ("Settings_MultiDisc", MultiDiscCard),
            ("Settings_Discs", DiscsCard),
            ("Settings_Fingerprinting", FingerprintCard),
            ("Settings_AnalysisTags", AnalysisTagsCard),
        ];

        const int columns = 3;

        for (var i = 0; i < sections.Length; i++)
        {
            var (key, card) = sections[i];

            var row = i / columns;
            while (JumpLinks.RowDefinitions.Count <= row)
                JumpLinks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var link = new RpmLink
            {
                Content = new TextBlock { Text = Loc.Get(key), FontSize = 12 },
            };
            link.Click += (_, _) => ScrollTo(card);

            Grid.SetRow(link, row);
            Grid.SetColumn(link, i % columns);
            JumpLinks.Children.Add(link);
        }
    }

    /// <summary>Puts a card at the top of the page. The offset is read from the card itself, not summed
    /// while the links are built. The cards above it grow and shrink as they are filled in, so a
    /// position measured at startup would be wrong by the time anyone clicked.</summary>
    private void ScrollTo(FrameworkElement card)
    {
        if (Scroller.Content is not UIElement content)
            return;

        var y = card.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        Scroller.ChangeView(null, Math.Max(0, y - 8), null);
    }

    // Disc default: what opens when a disc is inserted

    /// <summary>Sets the switch from the OS, not from our own settings. The OS is where this setting
    /// lives. The user may have changed it in the system's own settings since the app last looked. A
    /// switch that shows what we last wrote could then be wrong.
    /// </summary>
    private async Task RefreshDiscDefaultAsync()
    {
        var discs = _services.GetRequiredService<DiscAutoPlayService>();

        // A build with no installed app behind it has nothing the system could open. The switch says
        // so, and stays out of the way rather than pretending to work.
        if (!discs.IsSupported)
        {
            DiscDefaultToggle.IsEnabled = false;
            DiscDefaultStatus.Text = Loc.Get("Settings_DiscDefaultUnavailable");
            DiscDefaultStatus.Visibility = Visibility.Visible;
            return;
        }

        var isDefault = await discs.IsDefaultAsync();

        _loading = true;
        DiscDefaultToggle.IsOn = isDefault;
        _loading = false;
    }

    private async void OnDiscDefaultToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var discs = _services.GetRequiredService<DiscAutoPlayService>();
        var wanted = DiscDefaultToggle.IsOn;

        // The switch is disabled while the write runs. It shells out to the system. A second change
        // landing on top of the first would let the two race to be the truth.
        DiscDefaultToggle.IsEnabled = false;
        var actual = await discs.SetDefaultAsync(wanted);
        DiscDefaultToggle.IsEnabled = true;

        // What the OS says now, not what was asked for.
        _loading = true;
        DiscDefaultToggle.IsOn = actual;
        _loading = false;

        DiscDefaultStatus.Text = actual == wanted ? "" : Loc.Get("Settings_DiscDefaultFailed");
        DiscDefaultStatus.Visibility = actual == wanted ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Move or copy on import. Each import reads it fresh, so a change here needs only the write.
    /// There is no live state to push it into.</summary>
    private void OnImportActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ImportActionRadios.SelectedIndex < 0)
            return;

        _settings.ImportAction = (ImportAction)ImportActionRadios.SelectedIndex;
    }

    private void OnMultiDiscModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MultiDiscRadios.SelectedIndex < 0)
            return;

        _settings.MultiDiscMode = (MultiDiscMode)MultiDiscRadios.SelectedIndex;
    }

    // Required tags: what a queued file must have

    /// <summary>
    /// One tick per tag the bench can ask for, built from the store's own list of them.
    ///
    /// <para>There is never zero ticks. A rule that nobody has to satisfy would mark every file
    /// complete, and turn off the one reminder the import pane shows. So the last tick that is left
    /// cannot be removed. It is ticked again, rather than leaving the set empty.</para>
    /// </summary>
    private void InitRequiredTags()
    {
        var required = _settings.ImportRequiredTags;

        foreach (var tag in QueueTags.All)
        {
            var box = new CheckBox
            {
                Content = Loc.Get(TrackRow.LabelKey(tag)),
                IsChecked = required.Contains(tag),
                MinHeight = 0,
                Padding = new Thickness(8, 2, 0, 2),
                Tag = tag,
            };
            box.Checked += OnRequiredTagChanged;
            box.Unchecked += OnRequiredTagChanged;
            RequiredTagsList.Children.Add(box);
        }
    }

    private void OnRequiredTagChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var chosen = RequiredTagsList.Children
            .OfType<CheckBox>()
            .Where(box => box.IsChecked == true)
            .Select(box => (QueueTag)box.Tag)
            .ToList();

        if (chosen.Count == 0 && sender is CheckBox emptied)
        {
            // Put it back rather than accept a rule that asks for nothing.
            _loading = true;
            emptied.IsChecked = true;
            _loading = false;
            return;
        }

        _settings.ImportRequiredTags = chosen;
    }

    /// <summary>Whether added music has its missing genre and year looked up. The fill reads the setting before
    /// each record, so turning it off stops a fill that is running at the next record, and nothing has to be
    /// told.</summary>
    private void OnCatalogueFillToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.ImportFillFromCatalogue = CatalogueFillToggle.IsOn;
    }

    // Fingerprinting

    private void OnFingerprintToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        // Raises Changed. The worker starts, or stops after the file it is working on.
        _settings.FingerprintLibrary = FingerprintToggle.IsOn;
    }

    /// <summary>Read by the reporter when a play finishes and again before it sends. Turning it off
    /// here stops both, without anything having to be told.</summary>
    private void OnShareListeningToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.ShareListening = ShareListeningToggle.IsOn;
    }

    /// <summary>A one-off reading of how far the background pass has got. It is set when the page
    /// opens, like the rest of this page. It is blank when no library is open (first run).</summary>
    private void RefreshFingerprintStatus()
    {
        if (!_library.IsOpen)
        {
            FingerprintStatus.Text = "";
            return;
        }

        // If the read is not answered, the line stays blank. The problem screen is then shown over the app
        // (LibraryService.Read).
        FingerprintStatus.Text = _library.Read(_library.Db, "the fingerprint progress",
                db => (Done: db.CountTrackFingerprints(), Total: db.CountFingerprintableTracks()))
            .Match(counts => Loc.Format("Settings_FingerprintProgress", counts.Done, counts.Total), _ => "");
    }

    // Tempo and key written into the files

    private void OnAnalysisTagsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        // The writer reads this as each track finishes analysing. So the switch applies from the next
        // track. There is no running pass to start or stop.
        _settings.WriteAnalysisTags = AnalysisTagsToggle.IsOn;
    }

    /// <summary>The backfill's scope changed. The count differs for "fill the blanks" and for "write
    /// them all". So the count is read again rather than left showing the other one's number.</summary>
    private void OnBackfillScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            RefreshAnalysisTagStatus();
    }

    /// <summary>Writes what the analyser measured into every track that still waits for it. That
    /// covers the collections analysed before this feature existed, which is every collection played
    /// through the DJ.
    ///
    /// <para>Pressed again while it runs, it stops. A pass over a whole library is minutes of file
    /// writes, and the button that started it is the obvious place to change your mind. Nothing is lost
    /// by stopping. Each track is marked as it is done.</para>
    /// </summary>
    private async void OnBackfillAnalysisTags(object sender, RoutedEventArgs e)
    {
        var tagger = _services.GetRequiredService<AnalysisTagService>();
        if (tagger.IsBackfilling)
        {
            tagger.StopBackfill();
            return;
        }
        if (!_library.IsOpen)
            return;

        bool overwrite = BackfillOverwrite.IsChecked == true;
        // The tick decides what the run does. Changing it mid-run would mean two rules in one pass, so
        // it is disabled until the run ends.
        BackfillOverwrite.IsEnabled = false;
        BackfillLabel.Text = Loc.Get("Settings_AnalysisTagsStop");

        var progress = new Progress<AnalysisTagProgress>(p =>
            AnalysisTagsStatus.Text = Loc.Format("Settings_AnalysisTagsRunning", p.Done, p.Total, p.Written));

        try
        {
            var written = await tagger.BackfillAsync(overwrite, progress);
            AnalysisTagsStatus.Text = Loc.Format("Settings_AnalysisTagsWritten", written);
        }
        catch (Exception)
        {
            RefreshAnalysisTagStatus();
        }
        finally
        {
            BackfillLabel.Text = Loc.Get("Settings_AnalysisTagsBackfill");
            BackfillOverwrite.IsEnabled = true;
        }
    }

    /// <summary>How many analysed tracks are still waiting for their numbers to reach their files. It
    /// is set when the page opens, and read again when the overwrite tick changes. It is blank when no
    /// library is open (first run).</summary>
    private void RefreshAnalysisTagStatus()
    {
        if (!_library.IsOpen)
        {
            AnalysisTagsStatus.Text = "";
            return;
        }

        // If the read is not answered, the line stays blank. The problem screen is shown over the app.
        AnalysisTagsStatus.Text = _services.GetRequiredService<AnalysisTagService>()
            .Pending(BackfillOverwrite.IsChecked == true)
            .Match(pending => pending == 0
                    ? Loc.Get("Settings_AnalysisTagsNoneWaiting")
                    : Loc.Format("Settings_AnalysisTagsWaiting", pending),
                _ => "");
    }

    private async void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        // The same step Choose another folder takes on the problem screen over the library.
        var change = await LibraryFolderChange.PickAndOpenAsync(_picker, _library, _settings);
        if (change is FolderChange.Cancelled)
            return;

        if (change.StatusKey() is { } status)
        {
            FolderStatus.Text = Loc.Get(status);
            return;
        }

        PathText.Text = _settings.LibraryPath;
        FolderStatus.Text = "";

        // The pass over the new collection (reconcile, report, queue the inbox) is run by the library
        // page, when it is raised by Opened. Running it here as well would run two syncs over one
        // mirror and throw away what the second one found.
    }

    // File naming
    //
    // The combo lists the built-in presets (read-only), followed by the user's own templates. Those can be
    // renamed, retyped and deleted. Nothing is written until Save is pressed. Save adopts the template on
    // show as the library's. It then asks whether that rule applies to new files only, or to the whole
    // collection. New rips, vinyl and the import queue all start on the saved default.

    private readonly ObservableCollection<NamingItemVm> _namingItems = new();

    /// <summary>Set while the editor fields are filled from a selection, so setting a box does not
    /// count as the user typing.</summary>
    private bool _loadingNaming;

    private NamingItemVm? Selected => TemplateCombo.SelectedItem as NamingItemVm;

    private void InitNaming()
    {
        _loadingNaming = true;
        foreach (var t in NamingTemplate.Presets)
            _namingItems.Add(new NamingItemVm(t.Name, t.Pattern, builtin: true));
        foreach (var t in _settings.CustomTemplates)
            _namingItems.Add(new NamingItemVm(t.Name, t.Pattern, builtin: false));

        TemplateCombo.ItemsSource = _namingItems;

        var defaultName = _settings.DefaultTemplate.Name;
        var current = _namingItems.FirstOrDefault(i => i.Name == defaultName) ?? _namingItems[0];

        foreach (var token in NamingTemplate.Tokens)
        {
            var chip = new Button
            {
                Content = token,
                Tag = token,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
            };
            chip.Click += OnTokenChipClick;
            TokenPanel.Children.Add(chip);
        }

        _loadingNaming = false;
        TemplateCombo.SelectedItem = current;   // Opens on the template the library files by today
        SetNamingStatus("");
    }

    private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } item)
            return;

        _loadingNaming = true;
        NameBox.Text = item.Label;
        PatternBox.Text = item.Pattern;

        var editable = !item.IsBuiltin;
        NameBox.IsReadOnly = !editable;
        PatternBox.IsReadOnly = !editable;
        DeleteButton.IsEnabled = editable;
        _loadingNaming = false;

        RefreshPreview();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingNaming || Selected is not { IsBuiltin: false } item)
            return;
        item.Name = NameBox.Text;   // Raises PropertyChanged, so the combo label follows as you type
        MarkNamingDirty();
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingNaming || Selected is not { IsBuiltin: false } item)
            return;
        item.Pattern = PatternBox.Text;
        RefreshPreview();
        MarkNamingDirty();
    }

    private void OnTokenChipClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsBuiltin: false } || sender is not Button { Tag: string token })
            return;   // built-in patterns are read-only

        var pos = Math.Clamp(PatternBox.SelectionStart, 0, PatternBox.Text.Length);
        PatternBox.Text = PatternBox.Text.Insert(pos, token);   // Raises OnPatternChanged
        PatternBox.SelectionStart = pos + token.Length;
        PatternBox.Focus(FocusState.Programmatic);
    }

    private void OnNewTemplate(object sender, RoutedEventArgs e)
    {
        var item = new NamingItemVm(
            UniqueNamingName(Loc.Get("Settings_NamingNewName")),
            NamingTemplate.ArtistAlbumNumberedTitle.Pattern,
            builtin: false);
        _namingItems.Add(item);
        TemplateCombo.SelectedItem = item;
        MarkNamingDirty();
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    private void OnDuplicateTemplate(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } src)
            return;
        var item = new NamingItemVm(
            UniqueNamingName(Loc.Format("Settings_NamingCopyName", src.Label)),
            src.Pattern,
            builtin: false);
        _namingItems.Add(item);
        TemplateCombo.SelectedItem = item;
        MarkNamingDirty();
    }

    private void OnDeleteTemplate(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsBuiltin: false } item)
            return;

        var index = _namingItems.IndexOf(item);
        _namingItems.Remove(item);
        TemplateCombo.SelectedIndex = Math.Clamp(index - 1, 0, _namingItems.Count - 1);
        MarkNamingDirty();
    }

    private async void OnSaveNaming(object sender, RoutedEventArgs e)
    {
        // Every template needs a name and a pattern. Names must be distinct across the whole set,
        // built-ins included, so the default always resolves to exactly one. The names shown in the
        // picker must be distinct too. A built-in shows its translated label, and a user's template
        // with that label would be a second row that reads the same.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _namingItems)
        {
            var name = item.Name?.Trim() ?? "";
            if (name.Length == 0 || string.IsNullOrWhiteSpace(item.Pattern))
            {
                SetNamingStatus(Loc.Get("Settings_NamingErrorEmpty"));
                return;
            }

            var clash = false;
            foreach (var said in new[] { name, item.Label.Trim() }.Distinct(StringComparer.OrdinalIgnoreCase))
                clash |= !seen.Add(said);
            if (clash)
            {
                SetNamingStatus(Loc.Format("Settings_NamingErrorDuplicate", name));
                return;
            }
        }

        var custom = _namingItems
            .Where(i => !i.IsBuiltin)
            .Select(i => new NamingTemplate(i.Name.Trim(), i.Pattern.Trim()))
            .ToList();

        // What the library files by right now, read before the write, so that it can be compared with
        // what the write leaves behind.
        var before = _settings.DefaultTemplate.Pattern;

        // The template on show is the one being saved, so it is the one the library adopts. There is no
        // separate "make this the default" step. A user who opened this section, picked a pattern and
        // pressed Save meant that pattern. A second verb for it would only read as a second Save.
        // One write and one Changed event. The import queue rereads the setting.
        _settings.SetNaming(custom, Selected?.Name);
        SetNamingStatus(Loc.Get("Settings_NamingSaved"));

        // Saved is only half the question. A naming template can mean "file what arrives from now on",
        // or "this is how my collection looks". Ask, rather than choosing silently. The second answer
        // moves every file in the library, so it must not happen behind the user's back.
        //
        // Ask only when the answer could differ. The pattern decides where a file lands, so the pattern
        // is what is compared. Renaming a template, or editing one the library does not file by,
        // changes nothing on disk, so there is nothing to ask.
        var after = _settings.DefaultTemplate;
        if (!string.Equals(before, after.Pattern, StringComparison.Ordinal)
            && _library.IsOpen && XamlRoot is { } root)
            await Dialogs.ApplyNamingDialog.ShowAsync(root, _services, after);
    }

    private void RefreshPreview()
    {
        var pattern = PatternBox.Text;
        PreviewText.Text = string.IsNullOrWhiteSpace(pattern)
            ? ""
            : new NamingTemplate(Selected?.Name ?? "", pattern).Preview();
    }

    private string UniqueNamingName(string basis)
    {
        var names = _namingItems.SelectMany(i => new[] { i.Name, i.Label }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(basis))
            return basis;
        for (var n = 2; ; n++)
        {
            var candidate = $"{basis} {n}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private void MarkNamingDirty() => SetNamingStatus(Loc.Get("Settings_NamingUnsaved"));

    private void SetNamingStatus([Shown] string text) => NamingStatus.Text = text;
}

/// <summary>One row in the naming editor: a built-in preset (read-only) or one of the user's own. <see
/// cref="Name"/> raises a change notice, and <see cref="Label"/> with it, so the combo's label follows
/// a rename as it is typed.
///
/// <para>It sits beside the page, not inside it. The binding engine's generated metadata covers only
/// types declared at namespace scope, and a nested type falls back to reflection.</para>
/// </summary>
[RpmBindable]
public sealed partial class NamingItemVm : INotifyPropertyChanged
{
    private string _name;

    public NamingItemVm(string name, string pattern, bool builtin)
    {
        _name = name;
        Pattern = pattern;
        IsBuiltin = builtin;
    }

    public bool IsBuiltin { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    /// <summary>What the picker shows: a built-in's name in the user's language, or the user's own
    /// name as typed. <see cref="Name"/> is what the settings file stores.</summary>
    public string Label => IsBuiltin ? NamingText.Label(_name) : _name;

    public string Pattern { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
