using RPM.Core.Import;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>What the album editor settled. Only the fields that the user actually touched are here. An
/// untouched box is not an instruction. This matters most where the record disagrees with itself,
/// because the box can show only one of the values.</summary>
/// <param name="Discs">What changed on one disc, rather than on the record: its name, and how many
/// tracks it carries. Keyed by disc number. Only present where the folder holds more than one disc. A
/// set's discs have different names, and one box could only be right for one of them.</param>
/// <param name="Normalize">Whether this record is levelled with the rest of the collection. Null leaves
/// it to the settings. True or false settles it for this record alone. It has a meaning only when
/// <paramref name="NormalizeChanged"/> is set. Null is a real answer here, not a missing one.</param>
/// <param name="Classical">Whether this is a classical record. It is shown as works and movements only
/// where somebody has said so. Read it only when <paramref name="ClassicalChanged"/> is set.</param>
/// <param name="TitleNote">The note to add to every track title that does not already carry it, such as
/// "(Live)" or "(Mixed)", or null for none. Each track keeps its own title, so this is a rule applied per
/// track, not a field with one value. See <see cref="RPM.Core.Library.TitleNote"/>.</param>
/// <param name="Series">The series the record is one volume of, or null where it stands alone. Read it
/// only when <paramref name="SeriesChanged"/> is set.</param>
public sealed record AlbumEditResult(
    IReadOnlyDictionary<TagFieldKind, string> Fields,
    IReadOnlyDictionary<uint, IReadOnlyDictionary<TagFieldKind, string>> Discs,
    byte[]? ArtBytes = null,
    string? ArtMime = null,
    byte[]? FolderArtBytes = null,
    byte[]? DiscArtBytes = null,
    bool RemoveDiscArt = false,
    bool RemoveArt = false,
    bool? Normalize = null,
    bool NormalizeChanged = false,
    bool Classical = false,
    bool ClassicalChanged = false,
    RPM.Core.Metadata.Providers.ReleaseRef? Release = null,
    RPM.Core.Library.TitleNote? TitleNote = null,
    RPM.Core.Library.AlbumSeries? Series = null,
    bool SeriesChanged = false)
{
    public bool IsEmpty =>
        Fields.Count == 0 && Discs.Count == 0
        && ArtBytes is null && DiscArtBytes is null && !RemoveDiscArt && !RemoveArt
        && !NormalizeChanged && !ClassicalChanged && Release is null && TitleNote is null
        && !SeriesChanged;
}

/// <summary>Editing a record as a record.
///
/// <para>This is not a selection with ticks. An album has one name, one year and one label. This window
/// shows them in boxes that already hold those values. What you change is written to every track. What
/// you leave alone stays as it is. That is the whole rule, and it is why there is nothing to tick.
/// Ticks belong to the mass edit. There, no single thing is being edited, and a tick means "this is
/// part of my change", not "this field exists".</para>
///
/// <para>Where the tracks disagree (for example, a record that was split in two, with half of it filed
/// as "Rumors"), the box holds the value that most tracks carry. The row's marker turns amber and says
/// what the other tracks carry. Saving then makes them agree, which is the repair. This is also the
/// only place where a split album is visible without reading twelve rows.</para>
///
/// <para>The two pictures live here and only here, at the record's level. The cover goes inside every
/// track and onto the folder. The disc's printed label is the folder's disc.png. It has no per-track
/// meaning at all.</para>
/// </summary>
public sealed partial class AlbumEditDialog : ContentDialog
{
    /// <summary>One field's input, with the value that the record had when the window opened. So "did
    /// the user change this" is answered by comparing values. It is not answered by a change event,
    /// which also fires when the lookup fills a box that the user never looked at. Both count as a
    /// change, and neither is a tick.
    ///
    /// <para>The value is read through delegates, not from a TextBox, because a choice field (the
    /// advisory flag) is a list. See <see cref="TagField.Options"/>.</para></summary>
    /// <param name="Disc">Which disc this is a fact about, or null for the record as a whole. Only set
    /// where the folder holds more than one disc. Otherwise the folder is the disc, and its fields
    /// belong to the record.</param>
    /// <param name="Disagreed">The tracks did not all carry <paramref name="Seed"/> when the window
    /// opened. This is the amber marker beside the box. Such a field is written on save even when
    /// nobody touched it. That is the rule this window follows. The box holds what most of the record
    /// says, and saving makes the rest agree. Without this, the repair would never happen. A two-disc
    /// set whose second disc had a different album name or year would keep it. And a release lookup
    /// that filled the box with the value the majority already had would write nothing at all, which
    /// looks as if only disc 1 received the data.</param>
    private sealed record Field(
        TagField Tag, Control Input, Func<string> Read, Action<string> Write, string Seed,
        uint? Disc = null, bool Disagreed = false)
    {
        /// <summary>Changed by hand or by a lookup. It also applies when the field stands over tracks
        /// that disagree and now has a value to settle them with. An EMPTY box is never used to settle
        /// a disagreement. Most of the record saying nothing is no reason to erase what the rest of it
        /// says. Clearing a field on purpose still counts, because that is a change from the
        /// seed.</summary>
        public bool Dirty
            => !string.Equals(Read().Trim(), Seed, StringComparison.Ordinal)
               || (Disagreed && Read().Trim().Length > 0);
    }

    private readonly List<Field> _fields = new();
    private readonly IReadOnlyList<TrackRow> _rows;
    private readonly string _folder;
    private readonly Func<ArtAsk, Task<MassEditLookup?>>? _lookup;

    private readonly PictureRow _cover = new();
    private readonly PictureRow _discArt = new();

    private AlbumEditResult? _result;

    /// <summary>A picture at the record's level: the sleeve, or the label printed on the
    /// disc.</summary>
    private sealed class PictureRow
    {
        public Border ThumbHost = null!;
        public Image Thumb = null!;

        /// <summary>The record glyph behind the picture, shown when there is none. It is the library's
        /// own empty-cover square, so "nothing here yet" looks the same everywhere.</summary>
        public FontIcon Placeholder = null!;

        /// <summary>The CD overlay, on the disc's well only. It lets the preview show the disc the way
        /// the app draws it. Null on the cover's well.</summary>
        public RPM.Desktop.Presentation.Controls.CdOverlay? CdDress;

        public TextBlock Info = null!;
        public byte[]? Bytes, FolderBytes;
        public string Mime = "image/jpeg";
        public bool Changed, Removed;
    }

    /// <summary>The two library-side answers this record opened with, so "did the user change it" is a
    /// comparison rather than an event. Every other field in this window follows the same
    /// rule.</summary>
    private readonly bool? _normalizeSeed;
    private readonly bool? _classicalSeed;

    private ComboBox? _normalize;
    private ComboBox? _classical;

    /// <summary>The series this record opened in, or null where it stood alone, and the names the
    /// library already files records under, offered so a new volume joins its series rather than
    /// starting a second one spelled differently.</summary>
    private readonly RPM.Core.Library.AlbumSeries? _seriesSeed;
    private readonly IReadOnlyList<string> _knownSeries;

    private ComboBox? _inSeries;
    private ComboBox? _seriesName;
    private TextBox? _seriesVolume;

    /// <summary>The "(Live)" or "(Mixed)" tick under the release type, or null where the form has no
    /// release type box.</summary>
    private TitleNoteOffer? _titleNote;

    /// <summary>The pressing that a lookup in this window settled on. The caller can keep it. Naming
    /// the tracks is the next step, and it needs this release, not another pick.</summary>
    private RPM.Core.Metadata.Providers.ReleaseRef? _picked;

    /// <summary>The disc label this record is already carrying, where that is not a file in its folder.
    ///
    /// <para>Only a queued record has one. Its files have not been filed yet, so there is no album
    /// folder for a disc.png to sit beside them in. The chosen label is held in the store instead. It
    /// is shown here so that a second visit to this window shows what the first visit chose. Without
    /// this, the picture well would be empty however recently a label was picked, which looks as if the
    /// pick did not take.</para>
    ///
    /// <para>It is seeded, not changed. It is already held, so saving without touching it writes
    /// nothing.</para>
    /// </summary>
    private readonly byte[]? _discArtHeld;

    public AlbumEditDialog(
        string folder, IReadOnlyList<TrackRow> rows, Func<ArtAsk, Task<MassEditLookup?>>? lookup = null,
        bool? normalize = null, bool? classical = null, byte[]? discArtHeld = null,
        RPM.Core.Library.AlbumSeries? series = null, IReadOnlyList<string>? knownSeries = null)
    {
        InitializeComponent();
        _seriesSeed = series;
        _knownSeries = knownSeries ?? [];
        _rows = rows;
        _folder = folder;
        _lookup = lookup;
        _discArtHeld = discArtHeld;
        _normalizeSeed = normalize;
        _classicalSeed = classical;

        FolderText.Text = folder;
        ToolTipService.SetToolTip(FolderText, folder);
        LookupButton.Visibility = lookup is null ? Visibility.Collapsed : Visibility.Visible;
        Shell.AttachResize(this, minWidth: 700, minHeight: 560, maxWidth: 1080, defaultHeight: 680);

        BuildForm();
        _ = LoadPicturesAsync();
    }

    // ── the form ──────────────────────────────────────────────────────────────

    private void BuildForm()
    {
        // Album-scope only in the ordinary groups. A disc-scope field is a fact about ONE disc, and
        // this window writes to every track of the folder. See AddDiscScope for where those go instead.
        AddGroup(BasicLeft, TagFields.Groups.Release, TagScope.Album);
        AddGroup(BasicRight, TagFields.Groups.Publishing, TagScope.Album);
        AddDiscScope(BasicRight);

        // Which pressing this is, how it sorts, and what it is in MusicBrainz. All of these are facts
        // about the record as a whole, so they belong to this window rather than to the per-file one.
        AddGroup(WorkLeft, TagFields.Groups.Pressing, TagScope.Album);
        AddGroup(WorkRight, TagFields.Groups.Sorting, TagScope.Album);
        AddGroup(WorkRight, TagFields.Groups.Identifiers, TagScope.Album);

        AddRecordFacts(SettingsLeft);
        AddSeries(SettingsLeft);

        AddPictures(ArtworkHost);
        BuildTabs();
    }

    /// <summary>The per-file editor's tab strip, over this window's own panels.
    ///
    /// <para>Credits and Lyrics are not offered. Every field in the Credits group is track scope. A
    /// conductor is a fact about a performance, not about a record. This window shows album-scope
    /// fields only, so a Credits tab would open on nothing. Rather than an empty tab, there is no
    /// tab.</para>
    /// </summary>
    private void BuildTabs()
    {
        var strip = new RailTabStrip(
            new(MetadataTabs.Basic, "Details_TabBasic", "\uf02b"),
            new(MetadataTabs.Work, "Details_TabWork", "\uf02d"),
            new(MetadataTabs.Artwork, "Details_Art", "\uf03e"),
            new(MetadataTabs.Settings, "AlbumEdit_TabSettings", "\uf013"));

        strip.Register(MetadataTabs.Basic, PanelBasic);
        strip.Register(MetadataTabs.Work, PanelWork);
        strip.Register(MetadataTabs.Artwork, PanelArtwork);
        strip.Register(MetadataTabs.Settings, PanelSettings);

        TabStripHost.Child = strip;
        strip.Select(MetadataTabs.Basic);
    }

    /// <summary>One group's album-level fields, in catalogue order.</summary>
    private void AddGroup(Panel column, string groupKey, params TagScope[] scopes)
    {
        var group = new DenseFormGroup();
        foreach (var tag in TagFields.All.Where(f => f.GroupKey == groupKey && scopes.Contains(f.Scope)))
            AddField(group, tag, _rows, DenseForm.Label(tag.LabelKey));
        group.AddTo(column, groupKey);
    }

    /// <summary>What this record is and how it is played: the two things in this window that are not
    /// tags. That is why they have a tab of their own.
    ///
    /// <para>Both are the app's own opinion about the record, not facts about it. So both are stored in
    /// the mirror, and neither is written into the files. When this record is exported to another
    /// player, it should carry its music and no instructions about how to treat it.</para>
    ///
    /// <para>Levelling has three answers, not a switch, because its default is an answer in its own
    /// right. When left alone, the settings decide. Yes and no apply to a record where the settings are
    /// wrong in one direction or the other. Once given, they override the settings.</para>
    ///
    /// <para>There are two kinds of record, and only two. Nothing works it out. A record is classical
    /// because somebody said so here. That is why every album starts as an ordinary one.</para>
    /// </summary>
    private void AddRecordFacts(Panel column)
    {
        var group = new DenseFormGroup();

        // The kind of record comes first. It decides whether the tracklist below shows works with their
        // movements. It also excuses the record from levelling.
        _classical = YesNo("AlbumEdit_ClassicalYes", "AlbumEdit_ClassicalNo", _classicalSeed == true);
        group.Add(null, DenseForm.Label("AlbumEdit_Classical"), _classical);

        _normalize = Tristate(
            "AlbumEdit_NormalizeAuto", "AlbumEdit_NormalizeOn", "AlbumEdit_NormalizeOff",
            _normalizeSeed);
        group.Add(null, DenseForm.Label("AlbumEdit_Normalize"), _normalize);

        group.AddTo(column, "AlbumEdit_GroupSettings");
    }

    /// <summary>
    /// Whether this record is one volume of a series, which series, and which volume. Kept in the
    /// library like the two answers above it, and never written into the files: see
    /// <see cref="RPM.Core.Library.AlbumSeries"/>.
    ///
    /// <para>The name and the volume wait behind the yes. Most records stand alone, and two empty boxes
    /// on every one of them would read as two things left undone.</para>
    /// </summary>
    private void AddSeries(Panel column)
    {
        var group = new DenseFormGroup();

        _inSeries = YesNo("AlbumEdit_SeriesYes", "AlbumEdit_SeriesNo", _seriesSeed is not null);
        group.Add(null, DenseForm.Label("AlbumEdit_InSeries"), _inSeries);

        _seriesName = MassEditDialog.SuggestBox(_knownSeries, _seriesSeed?.Name);
        _seriesName.PlaceholderText = Loc.Get("AlbumEdit_SeriesNameHint");
        group.Add(null, DenseForm.Label("AlbumEdit_SeriesName"), _seriesName);

        _seriesVolume = new TextBox
        {
            Text = _seriesSeed?.Volume ?? "",
            PlaceholderText = Loc.Get("AlbumEdit_SeriesVolumeHint"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        group.Add(null, DenseForm.Label("AlbumEdit_SeriesVolume"), _seriesVolume);

        void Follow()
        {
            var on = _inSeries.SelectedIndex == 1;
            _seriesName.IsEnabled = on;
            _seriesVolume.IsEnabled = on;
        }
        _inSeries.SelectionChanged += (_, _) => Follow();
        Follow();

        group.AddTo(column, "AlbumEdit_GroupSeries");
    }

    /// <summary>The series as the window now says it: null for "stands alone", and null too for a yes
    /// with no name, because a series nobody named is no series.</summary>
    private RPM.Core.Library.AlbumSeries? SeriesNow()
        => _inSeries is null ? _seriesSeed
           : _inSeries.SelectedIndex != 1 ? null
           : RPM.Core.Library.AlbumSeries.Of(
               _seriesName is null ? null : MassEditDialog.ReadSuggest(_seriesName), _seriesVolume?.Text);

    /// <summary>One of these answers: work it out, yes, or no. Null, which means "work it out", is
    /// first, because it is the default and the common case.</summary>
    private static ComboBox Tristate(string autoKey, string yesKey, string noKey, bool? seed) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemsSource = new List<string> { Loc.Get(autoKey), Loc.Get(yesKey), Loc.Get(noKey) },
        SelectedIndex = IndexOfTristate(seed),
    };

    /// <summary>Yes or no, with no third answer. "No" is first, because every record is a no until
    /// somebody says otherwise.</summary>
    private static ComboBox YesNo(string yesKey, string noKey, bool seed) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemsSource = new List<string> { Loc.Get(noKey), Loc.Get(yesKey) },
        SelectedIndex = seed ? 1 : 0,
    };

    private static int IndexOfTristate(bool? value) => value switch
    {
        null => 0,
        true => 1,
        false => 2,
    };

    private static bool? TristateOf(int index) => index switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    private void AddField(
        DenseFormGroup group, TagField tag, IReadOnlyList<TrackRow> over, FrameworkElement label,
        uint? disc = null)
    {
        var values = over.Select(tag.Get).Select(v => v.Trim()).ToList();
        var seed = Dominant(values);

        Control input;
        FrameworkElement? placed = null;   // What goes in the row, where that is more than the input
        Func<string> read;
        Action<string> write;
        if (tag.Options is { Count: > 0 } options)
        {
            var choice = MassEditDialog.ChoiceBox(options, seed);
            choice.IsEnabled = true;   // No tick is needed here. This window edits every field it shows
            input = choice;
            read = () => MassEditDialog.ReadChoice(choice, options);
            write = v => MassEditDialog.WriteChoice(choice, options, v);

            // A live record can have "(Live)" added to its track titles, and a mixtape "(Mixed)". The
            // tick sits under the type that offers it, and works on a record already in the library as
            // well as on a queued one.
            if (tag.Kind == TagFieldKind.ReleaseType && disc is null)
            {
                _titleNote = new TitleNoteOffer(choice, options, over.Select(r => r.Title));
                placed = _titleNote.Host;
            }
        }
        else if (tag.SuggestionsFor is { } stylesFor)
        {
            // Narrowed by the genre, and it follows the genre. The styles under Rock are not the styles
            // under House. The genre box is right there to be corrected.
            var genre = Dominant([.. over.Select(TagFields.Get(TagFieldKind.Genre).Get).Select(v => v.Trim())]);
            var combo = MassEditDialog.SuggestBox(stylesFor(genre), seed);
            if (_fields.FirstOrDefault(f => f.Tag.Kind == TagFieldKind.Genre && f.Disc == disc)
                is { Input: ComboBox genreBox })
                MassEditDialog.FollowGenre(combo, genreBox, stylesFor);
            input = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            write = v => MassEditDialog.SetSuggestValue(combo, v);
        }
        else if (tag.Suggestions is { Count: > 0 } suggestions)
        {
            var combo = MassEditDialog.SuggestBox(suggestions, seed);
            input = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            write = v => MassEditDialog.SetSuggestValue(combo, v);
        }
        else
        {
            var box = new TextBox { Text = seed };
            if (tag.Numeric)
                box.AcceptDigitsOnly();
            input = box;
            read = () => box.Text;
            write = v => box.Text = v;
        }

        if (TagFields.Resolve(tag.Width) is { } w)
            input.Narrow(w);

        var disagreement = Disagreement(values);
        _fields.Add(new Field(tag, input, read, write, seed, disc, disagreement is not null));
        // A row with a tick under its box keeps its label level with the box, not with the pair.
        group.Add(null, label, placed ?? input, topAlign: placed is not null,
            trailing: new AlbumMarker(disagreement, canArm: false));
    }

    /// <summary>The fields that are true of a disc rather than of the record: which disc this is, how
    /// many tracks are on it, and what it is called.
    ///
    /// <para>Where they belong depends on what the folder actually holds, not on a setting. The naming
    /// template decides whether a set lands in one folder or in a folder per disc ({disc} in the folder
    /// part). So the window checks the tracks, not the configuration.</para>
    ///
    /// <para><b>One disc in the folder.</b> The folder is a disc. Its number, its track count and its
    /// subtitle each have one value, so they go in with the rest. "This whole folder is disc 2 of a
    /// set" is a real and common repair.</para>
    ///
    /// <para><b>Several discs.</b> One box could only be right for one of them. Writing a disc number
    /// across the folder would put the whole set onto one disc. So each disc gets a row, and the disc
    /// number is not offered at all. Disc 2's number is 2.</para>
    /// </summary>
    private void AddDiscScope(Panel column)
    {
        var discs = _rows
            .Select(DiscOf)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (discs.Count <= 1)
        {
            var group = new DenseFormGroup();
            foreach (var kind in new[] { TagFieldKind.Disc, TagFieldKind.TrackCount, TagFieldKind.DiscSubtitle })
            {
                var tag = TagFields.Get(kind);
                AddField(group, tag, _rows, DenseForm.Label(tag.LabelKey));
            }
            group.AddTo(column, "AlbumEdit_GroupDiscs");
            return;
        }

        // A row per disc: its name, and how many tracks it carries.
        var subtitle = TagFields.Get(TagFieldKind.DiscSubtitle);
        var count = TagFields.Get(TagFieldKind.TrackCount);
        var perDisc = new DenseFormGroup();
        foreach (var disc in discs)
        {
            var onDisc = _rows.Where(r => DiscOf(r) == disc).ToList();
            var values = onDisc.Select(subtitle.Get).Select(v => v.Trim()).ToList();

            var subtitleBox = new TextBox { Text = Dominant(values) };
            _fields.Add(new Field(
                subtitle, subtitleBox, () => subtitleBox.Text, v => subtitleBox.Text = v,
                subtitleBox.Text, disc, Disagreement(values) is not null));

            var countValues = onDisc.Select(count.Get).Select(v => v.Trim()).ToList();
            var countBox = new TextBox { Text = Dominant(countValues) };
            countBox.AcceptDigitsOnly();
            countBox.Narrow(DenseForm.TinyWidth);
            ToolTipService.SetToolTip(countBox, Loc.Get("Details_TrackCount"));
            _fields.Add(new Field(
                count, countBox, () => countBox.Text, v => countBox.Text = v, countBox.Text, disc,
                Disagreement(countValues) is not null));

            var label = new TextBlock
            {
                Text = disc > 0 ? Loc.Format("AlbumEdit_DiscN", disc) : Loc.Get("Details_Disc"),
                Style = DenseForm.Res<Style>("DenseFieldLabelStyle"),
            };
            perDisc.Add(null, label, DenseForm.Pair(subtitleBox, countBox),
                trailing: new AlbumMarker(Disagreement(values), canArm: false));
        }
        perDisc.AddTo(column, "AlbumEdit_GroupDiscs");
    }

    private static uint DiscOf(TrackRow row) => uint.TryParse(row.Disc.Trim(), out var n) ? n : 0u;

    /// <summary>The record's two pictures, side by side, big enough to judge. Each one has its own
    /// controls underneath, so what a button does depends only on the picture it sits under.
    ///
    /// <para>There is no "Artwork" heading over them. The tab already has that name, and each picture
    /// has its own header just below it.</para>
    /// </summary>
    private void AddPictures(Panel column)
    {
        // Use the per-file editor's header keys, COVER and DISC, in capitals like every other group
        // header.
        column.Children.Add(BuildPicture(_cover, "Details_GroupCover", disc: false));
        column.Children.Add(BuildPicture(_discArt, "Details_GroupDisc", disc: true));
    }

    /// <summary>The square, then what it is, then what can be done to it.</summary>
    private UIElement BuildPicture(PictureRow row, string labelKey, bool disc)
    {
        // The library's own empty-cover square (see PictureWell). The per-file editor's cover uses it
        // too, so the two cannot drift apart.
        var well = new PictureWell(PictureEdge);
        row.Thumb = well.Thumb;
        row.Placeholder = well.Placeholder;
        if (disc)
        {
            row.CdDress = new RPM.Desktop.Presentation.Controls.CdOverlay { Visibility = Visibility.Collapsed };
            well.AddOverPicture(row.CdDress);
        }
        row.ThumbHost = new Border { Child = well.Root };

        row.Info = new TextBlock
        {
            FontSize = 11,
            Foreground = DenseForm.Res<Brush>("DenseLabelInkBrush"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var change = new Button
        {
            Content = IconLabel("\uf03e", "Details_ChangeArt"),   // image: pick a picture
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
        };
        change.Click += async (_, _) =>
        {
            if (await PickImageAsync() is not { } picked)
                return;
            row.Bytes = row.FolderBytes = picked;
            row.Mime = IsPng(picked) ? "image/png" : "image/jpeg";
            row.Changed = true;
            row.Removed = false;
            await ShowPictureAsync(row);
        };

        // The third way to get a picture. For a record with the wrong cover, this is the usual way. The
        // file on disk is not the answer, and neither is the machine's guess. This searches what the
        // catalogues and the web hold for these words, and shows the results to choose from.
        var find = new Button
        {
            Content = IconLabel("\uf002", "Details_FindArt"),   // magnifier: search for a picture
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
        };
        find.Click += async (_, _) =>
        {
            // The disc pane searches for discs, and the cover pane searches for sleeves. The pane was
            // chosen when the picture was built, so the window does not need to guess.
            var (picture, alsoDisc) = await SearchArtAsync(disc);
            if (picture is not { Length: > 0 })
                return;

            Take(row, picture);

            // A sleeve chosen from a source that also has the disc fills the disc pane too. This is the
            // only place where one click changes two pictures. The disc mark on the tile the user
            // clicked shows that this will happen.
            if (!disc && alsoDisc is { Length: > 0 })
                Take(_discArt, alsoDisc);

            await ShowPictureAsync(row);
            if (!disc && alsoDisc is { Length: > 0 })
                await ShowPictureAsync(_discArt);
        };

        // Both pictures can be removed, for the same reason: a wrong cover is as real a problem as a
        // wrong disc label. The two differ in what removing means. The disc label is one file beside
        // the tracks. The cover is that file, and also a copy inside every track.
        //
        // This is the only one of the three buttons without a label. Three labelled buttons do not fit
        // in the 220 px of the picture pane in any language. A trash can is the most legible icon in
        // the set, and it needs no reading. The other two are a picture and a magnifier, which only
        // mean "change" and "find" once you have been told. The name moves to the tooltip, so it is not
        // lost.
        //
        // The button is plain white, like the buttons next to it. The glyph already says what it does.
        // The three buttons look like equal choices, not like one with a warning colour.
        var remove = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uf2ed",   // trash
                Style = DenseForm.Res<Style>("FaIcon"),
                FontSize = 11,
            },
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
        };
        IconName.Set(remove, Loc.Get("Details_RemoveArt"));
        remove.Click += async (_, _) =>
        {
            row.Bytes = null;
            row.Changed = false;
            row.Removed = true;
            await ShowPictureAsync(row);
        };

        var buttons = DenseForm.Pair(change, find, remove);

        var pane = new StackPanel { Spacing = 6, Width = PictureEdge };
        // Named the same way as the per-file editor names its cover: a group header over a thin rule.
        // Every other section in these windows is announced this way.
        pane.Children.Add(new TextBlock
        {
            Text = Loc.Get(labelKey),
            Style = DenseForm.Res<Style>("DenseGroupHeaderStyle"),
        });
        pane.Children.Add(new Border
        {
            Height = 1,
            Background = DenseForm.Res<Brush>("DenseRuleBrush"),
            Margin = new Thickness(0, 3, 0, 2),
        });
        pane.Children.Add(row.ThumbHost);
        pane.Children.Add(row.Info);
        pane.Children.Add(buttons);
        return pane;
    }

    /// <summary>A button's face: a glyph, then its label. Both are white. The glyph carries the
    /// meaning. A coloured label would make one button of a pair look like the dangerous one.</summary>
    internal static StackPanel IconLabel(string glyph, string labelKey)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            Style = DenseForm.Res<Style>("FaIcon"),
            FontSize = 11,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.Get(labelKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    /// <summary>The square's edge, and the pane's width with it.
    ///
    /// <para>This is the same 220 that the per-file editor uses. Two panes of 220 with their gap take
    /// 452 px. That fits within this window's 700 px minimum width. The buttons carry glyphs and need
    /// this width, so they are not cut off at the pane's edge.</para>
    /// </summary>
    private const double PictureEdge = 220;

    // ── what the record says, and where it disagrees with itself ──────────────

    /// <summary>The value that most of the tracks carry. Blanks lose to any other value. In a record
    /// where four tracks name the label and eight say nothing, the label is the answer. Showing the
    /// empty string because it is more common would be the wrong answer to "what is this album's
    /// label?"</summary>
    private static string Dominant(IReadOnlyList<string> values)
    {
        var named = values.Where(v => v.Length > 0).ToList();
        if (named.Count == 0)
            return "";
        return named.GroupBy(v => v, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .First().Key;
    }

    /// <summary>What to say when the tracks do not all carry the dominant value, or null when they
    /// do.</summary>
    private static string? Disagreement(IReadOnlyList<string> values)
    {
        var dominant = Dominant(values);
        var others = values.Where(v => !string.Equals(v, dominant, StringComparison.Ordinal)).ToList();
        if (others.Count == 0)
            return null;

        var distinct = others.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1
            ? Loc.Format("Edit_AlbumDisagrees", others.Count,
                distinct[0].Length > 0 ? distinct[0] : Loc.Get("Details_NoArt"))
            : Loc.Get("Edit_AlbumDisagreesMany");
    }

    // ── pictures ──────────────────────────────────────────────────────────────

    private async Task LoadPicturesAsync()
    {
        // The cover is the folder's, at full size. The copy inside a track is the scaled one.
        if (await Task.Run(() => FolderArtWriter.FindFolderImage(_folder)) is { } cover)
            _cover.Bytes = await ReadAsync(cover);
        if (await Task.Run(() => FolderArtWriter.FindDiscImage(_folder)) is { } disc)
            _discArt.Bytes = await ReadAsync(disc);

        // A queued record's label is not beside its files (see _discArtHeld). The folder comes first. A
        // disc.png next to the tracks is what this machine shows, and it outranks anything held for the
        // record.
        if (_discArt.Bytes is not { Length: > 0 } && _discArtHeld is { Length: > 0 } held)
            _discArt.Bytes = held;

        await ShowPictureAsync(_cover);
        await ShowPictureAsync(_discArt);

        // A record whose audio is in the cloud may have no folder on this disk. The picture that the
        // wall shows for it came from the store. Show that picture, and not "No art". An editor that
        // says the record has no cover, while the card behind it shows one, makes the user look for a
        // picture they already have.
        await LoadCloudPicturesAsync();

        static async Task<byte[]?> ReadAsync(string path)
        {
            try { return await Task.Run(() => File.ReadAllBytes(path)); }
            catch { return null; }
        }
    }

    /// <summary>Fills whichever picture the folder could not supply from the cloud, for a record whose
    /// files this machine does not hold.
    ///
    /// <para>This runs only when nothing is on disk. A folder image is what this machine actually
    /// shows, and it outranks the store's copy. Neither counts as a change. The user has not touched
    /// anything, so saving without touching them writes nothing.</para>
    /// </summary>
    private async Task LoadCloudPicturesAsync()
    {
        if (_cover.Bytes is { Length: > 0 } && _discArt.Bytes is { Length: > 0 })
            return;
        if (_rows.FirstOrDefault() is not { } first)
            return;
        // A file on disk is the album's own answer. The cloud is only asked about records whose files
        // are not here.
        if (await Task.Run(() => File.Exists(first.FilePath)))
            return;

        var art = App.Services.GetRequiredService<Services.Cloud.CloudArtService>();

        if (_cover.Bytes is not { Length: > 0 }
            && await art.CoverAsync(first.AlbumArtist, first.Artist, first.Album, rpmId: null) is { Length: > 0 } cover)
        {
            _cover.Bytes = cover;
            await ShowPictureAsync(_cover);
        }

        if (_discArt.Bytes is not { Length: > 0 }
            && await art.DiscAsync(first.AlbumArtist, first.Artist, first.Album) is { Length: > 0 } disc)
        {
            _discArt.Bytes = disc;
            await ShowPictureAsync(_discArt);
        }
    }

    private static async Task ShowPictureAsync(PictureRow row)
    {
        if (row.Bytes is not { Length: > 0 } bytes)
        {
            row.Thumb.Source = null;
            row.Placeholder.Visibility = Visibility.Visible;
            if (row.CdDress is { } empty)
                empty.Visibility = Visibility.Collapsed;
            row.Info.Text = Loc.Get(row.Removed ? "Details_RemoveArt" : "Details_NoArt");
            return;
        }
        row.Placeholder.Visibility = Visibility.Collapsed;

        // The disc is drawn the way the app draws it everywhere. It is cut by DiscStamp.Round, which
        // the album page, the header and Pegasus also use. The CD overlay is shown when the result is
        // the whole disc. A vinyl label is only the middle third, so it has no overlay. The size line
        // below describes the file, which is what gets saved.
        var disc = row.CdDress is not null;
        var (preview, size, whole) = await Task.Run(() =>
        {
            var small = ArtScaler.Downscale(bytes, ArtScaler.TileEdge);
            var bounds = SkiaSharp.SKBitmap.DecodeBounds(bytes);
            if (!disc)
                return (small, bounds, false);
            var cut = Services.Art.DiscStamp.Round(small, vinyl: false);
            return (cut.Bytes, bounds, cut.CoversRecord);
        });
        var stream = Presentation.Common.BitmapBytes.Stream(preview);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        row.Thumb.Source = bmp;
        if (row.CdDress is { } dress)
            dress.Visibility = whole ? Visibility.Visible : Visibility.Collapsed;
        row.Info.Text = $"{size.Width}×{size.Height} · {Loc.Format("Unit_KB", bytes.Length / 1024)}";
    }

    /// <summary>The cover search, opened on what this record calls itself.
    ///
    /// <para>It is seeded from the boxes, not from the files. So a user who has just corrected the
    /// album name searches for the corrected name. That is the usual reason to open it at that moment.
    /// Both halves may be empty, and the window handles that by asking for them.</para>
    ///
    /// <para>The disc's printed label is searched on the same words. No source stores disc art
    /// separately, so the results are sleeves. The user picks the one that is a photograph of the disc.
    /// The import path finds them the same way.</para>
    /// </summary>
    private async Task<(byte[]? Picture, byte[]? Disc)> SearchArtAsync(bool disc)
    {
        // Every source behind this window is reached through the user's own rpm session. When the user
        // is signed out, the grid fills with nothing. So ask the user to sign in first, rather than
        // show an empty grid.
        if (!await SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForCovers")))
            return (null, null);

        var covers = App.Services.GetRequiredService<RPM.Core.Metadata.Providers.ICoverSearchApi>();

        var dialog = new CoverArtSearchDialog(
            covers, Seeded(TagFieldKind.AlbumArtist) ?? Seeded(TagFieldKind.Artist),
            Seeded(TagFieldKind.Album),
            disc ? RPM.Fingerprint.Catalog.CoverKind.Disc : RPM.Fingerprint.Catalog.CoverKind.Cover)
        {
            XamlRoot = XamlRoot,
        };
        await DialogHost.ShowAsync(dialog);
        return (dialog.Picked, dialog.PickedDisc);
    }

    /// <summary>Puts a fetched picture into a pane. The mime is read from the bytes, not from the name
    /// the source gave the file. This is the same as the file picker. Web sources are worse about this
    /// than file systems are.</summary>
    private static void Take(PictureRow row, byte[] bytes)
    {
        row.Bytes = row.FolderBytes = bytes;
        row.Mime = IsPng(bytes) ? "image/png" : "image/jpeg";
        row.Changed = true;
        row.Removed = false;
    }

    /// <summary>What a box holds right now, or null when it is empty. It is read from the live input,
    /// not from the seed. Searching from this window is meant to use what the user has just
    /// typed.</summary>
    private string? Seeded(TagFieldKind kind)
        => _fields.FirstOrDefault(f => f.Tag.Kind == kind && f.Disc is null)?.Read().Trim() is { Length: > 0 } value
            ? value
            : null;

    private static async Task<byte[]?> PickImageAsync()
    {
        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png");
            return await picker.PickSingleFileAsync() is { } file ? File.ReadAllBytes(file.Path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The PNG signature. It is more reliable than the extension that a file happens to
    /// have.</summary>
    private static bool IsPng(byte[] bytes) => bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..];

    // ── the release lookup ────────────────────────────────────────────────────

    /// <summary>Fills the boxes from a pressing. Nothing is written yet. The boxes are what gets saved,
    /// so a lookup that found the wrong record is undone by closing the window.</summary>
    private async void LookUpRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_lookup is null)
            return;
        LookupButton.IsEnabled = false;
        try
        {
            // What the record already shows decides where the release window's overwrite offer starts.
            // A sleeve chosen by hand should not be replaced by a lookup that only fixes a year, unless
            // the user asks for it.
            var ask = _cover.Bytes is { Length: > 0 } || _discArt.Bytes is { Length: > 0 }
                ? ArtAsk.Held
                : ArtAsk.Missing;

            if (await _lookup(ask) is not { } found)
                return;

            _picked = found.Release ?? _picked;

            foreach (var field in _fields)
                if (found.Fields.TryGetValue(field.Tag.Kind, out var value))
                    field.Write(value);

            if (found.Art is { Embedded.Length: > 0 } art)
            {
                _cover.Bytes = art.Embedded;
                _cover.FolderBytes = art.Folder;
                _cover.Mime = art.Mime;
                _cover.Changed = true;
                _cover.Removed = false;
                await ShowPictureAsync(_cover);
            }
            if (found.DiscArt is { Length: > 0 } discArt)
            {
                _discArt.Bytes = _discArt.FolderBytes = discArt;
                _discArt.Mime = "image/png";
                _discArt.Changed = true;
                _discArt.Removed = false;
                await ShowPictureAsync(_discArt);
            }
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("album-edit", "release lookup", ex);
        }
        finally
        {
            LookupButton.IsEnabled = true;
        }
    }

    // ── save / cancel ─────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var touched = _fields.Where(f => f.Dirty).ToList();
        var fields = touched
            .Where(f => f.Disc is null)
            .ToDictionary(f => f.Tag.Kind, f => f.Read().Trim());
        var discs = touched
            .Where(f => f.Disc is not null)
            .GroupBy(f => f.Disc!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<TagFieldKind, string>)
                    g.ToDictionary(f => f.Tag.Kind, f => f.Read().Trim()));

        var normalize = _normalize is null ? _normalizeSeed : TristateOf(_normalize.SelectedIndex);
        bool classical = _classical is null ? _classicalSeed == true : _classical.SelectedIndex == 1;
        var series = SeriesNow();

        _result = new AlbumEditResult(
            fields, discs,
            _cover.Changed ? _cover.Bytes : null,
            _cover.Changed ? _cover.Mime : null,
            _cover.Changed ? _cover.FolderBytes : null,
            _discArt.Changed ? _discArt.Bytes : null,
            _discArt.Removed,
            _cover.Removed,
            normalize,
            normalize != _normalizeSeed,
            classical,
            classical != (_classicalSeed == true),
            _picked,
            TitleNote: _titleNote?.Wanted,
            Series: series,
            SeriesChanged: series != _seriesSeed);
        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Opens the album editor over a record's tracks. Returns what changed, or null when the
    /// window was closed or nothing in it was touched.</summary>
    public static async Task<AlbumEditResult?> ShowAsync(
        XamlRoot xamlRoot, string folder, IReadOnlyList<TrackRow> rows,
        Func<ArtAsk, Task<MassEditLookup?>>? lookup = null, bool? normalize = null, bool? classical = null,
        byte[]? discArtHeld = null, RPM.Core.Library.AlbumSeries? series = null,
        IReadOnlyList<string>? knownSeries = null)
    {
        var dialog = new AlbumEditDialog(folder, rows, lookup, normalize, classical, discArtHeld, series, knownSeries)
        {
            XamlRoot = xamlRoot,
        };
        await DialogHost.ShowAsync(dialog);
        return dialog._result is { IsEmpty: false } r ? r : null;
    }
}
