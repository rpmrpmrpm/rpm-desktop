using RPM.Core.Editing;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One field's line in the mass edit window. It holds which field it is (see <see
/// cref="TagFields"/>) and how the selected tracks' values for it combine. When the tracks agree, the
/// control is one box. When they differ, the control is a list to pick from or type into.</summary>
public sealed record MassEditFieldInput(TagField Field, FieldValueAggregate Values)
{
    public TagFieldKind Kind => Field.Kind;
}

/// <summary>What the user chose to change. Only the ticked fields are present. Art is set only when its
/// box was ticked and an image was picked.</summary>
/// <param name="ArtBytes">The copy that goes inside every track file.</param>
/// <param name="FolderArtBytes">The copy written to the album folder as its cover, when it is a bigger
/// picture than the embedded one, such as a catalogue's own scan. Null means the two are the same
/// image, which is what a file picked from disk gives.</param>
/// <param name="DiscArtBytes">The label printed on the disc. It is written to the album folder as
/// disc.png. It is a folder file and never goes into a tag. The record view reads it from disk.</param>
/// <param name="RemoveArt">Take the cover away, from every track's tags and from the folder. A cover is
/// stored in both places. Removing it from only one would leave the wall showing a picture that the
/// files no longer have.</param>
/// <param name="RemoveDiscArt">Take the folder's disc art away. This has its own flag and not empty
/// bytes, because "no new picture" and "no picture at all" are different answers.</param>
public sealed record MassEditResult(
    IReadOnlyDictionary<TagFieldKind, string> Fields,
    byte[]? ArtBytes,
    string? ArtMime,
    byte[]? FolderArtBytes = null,
    byte[]? DiscArtBytes = null,
    bool RemoveDiscArt = false,
    bool RemoveArt = false);

/// <summary>A cover that the host's lookup returned, at the two sizes the app stores it in. See <see
/// cref="RPM.Core.Art.Sleeve"/>.</summary>
public sealed record MassEditArt(byte[] Embedded, string Mime, byte[] Folder);

/// <summary>A pressing that the user picked from the release catalogues, in the form this window fills
/// its boxes from. Only the fields that the catalogue knows are present. A release listing has an
/// album, an artist, a year, a label and a disc count. It has no value for a comment or a BPM. The
/// window fills a ticked field only when the release has a value for it.</summary>
/// <param name="DiscArt">The label on the disc, when one was asked for and found. It is a transparent
/// PNG, so it never has the cover's MIME type.</param>
/// <param name="Release">Which pressing answered, when one did. A caller keeps it, so that it does not
/// ask the same question twice. Naming a record's tracks after the record is settled is the usual next
/// step, and it needs the release that was already chosen.</param>
public sealed record MassEditLookup(
    IReadOnlyDictionary<TagFieldKind, string> Fields,
    MassEditArt? Art = null,
    byte[]? DiscArt = null,
    RPM.Core.Metadata.Providers.ReleaseRef? Release = null);

/// <summary>Mass edit: one checkbox per field, with the control disabled until the box is ticked.
/// Where every selected track already agrees on a field, the control is a plain box with that value.
/// Where the tracks differ, it is an editable list of the distinct values, so one can be picked or a
/// new one typed. Saving writes each ticked field onto every selected track. An empty value clears the
/// field. An album-art row replaces the cover. The caller decides what "save" means. It either sets
/// pending values (import queue) or writes to disk (library).
///
/// <para>The layout is the app's dense form (Themes/DenseForm.xaml): 24 px inputs, labels to the left
/// and right-aligned against them. So every input in a column starts on one vertical line. A field that
/// is empty, or holds an unusual value, shows as a break in that line. Fields are grouped by what they
/// describe. The release and its publishing are on the left, and the performance and the track's own
/// tags are on the right. The short fields (year, disc, BPM) get short boxes.</para>
///
/// <para>The release lookup is the window's own action, not a button on the art row. Tick the fields
/// that a catalogue should settle, pick the pressing, and every field that the catalogue has a value
/// for is filled in at once. The cover is included if it was ticked too. The button stays disabled
/// until something is ticked, because with nothing ticked there is nothing for a release to
/// fill.</para>
/// </summary>
public sealed partial class MassEditDialog : ContentDialog
{
    private sealed record Row(
        TagFieldKind Kind, CheckBox Check, Func<string> Read, Action<string> Write, Control Control);

    private static readonly SolidColorBrush Chip = new(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    // Ticked boxes use the azure gradient from Assets/RpmPalette as their fill. A checkbox is a filled
    // area, so the gradient shows here, unlike a glyph. The check mark is white. The fill is the base
    // colour #0371D0 dimmed to 62%, 51% and 40%.
    private static readonly Brush CheckFill = BuildAzureFill();
    // The border is the gradient's top colour (#014680). It is the same whether the box is ticked or
    // not.
    private static readonly Brush CheckStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x46, 0x80));
    private static readonly Brush CheckGlyph = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static Brush BuildAzureFill()
    {
        var b = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x01, 0x46, 0x80), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x01, 0x39, 0x6A), Offset = 0.5 });
        b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x01, 0x2D, 0x53), Offset = 1 });
        return b;
    }

    private static void StyleCheck(CheckBox cb)
    {
        cb.Resources["CheckBoxCheckBackgroundFillChecked"] = CheckFill;
        cb.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = CheckFill;
        cb.Resources["CheckBoxCheckBackgroundFillCheckedPressed"] = CheckFill;
        cb.Resources["CheckBoxCheckBackgroundStrokeChecked"] = CheckStroke;
        cb.Resources["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = CheckStroke;
        cb.Resources["CheckBoxCheckBackgroundStrokeCheckedPressed"] = CheckStroke;
        cb.Resources["CheckBoxCheckBackgroundStrokeUnchecked"] = CheckStroke;
        cb.Resources["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = CheckStroke;
        cb.Resources["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = CheckStroke;
        cb.Resources["CheckBoxCheckGlyphForegroundChecked"] = CheckGlyph;
        cb.Resources["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = CheckGlyph;
        cb.Resources["CheckBoxCheckGlyphForegroundCheckedPressed"] = CheckGlyph;
    }

    private readonly List<Row> _rows = new();
    private IReadOnlyDictionary<TagFieldKind, MassEditFieldInput> _fields =
        new Dictionary<TagFieldKind, MassEditFieldInput>();

    /// <summary>One of the window's two pictures: the cover, and the label printed on the disc itself.
    /// They behave the same way. A tick arms the row, and there is a thumbnail and a picker. The
    /// difference is where the picture goes. The cover goes into every track file and onto the folder.
    /// The disc art is the folder's disc.png and never goes into a tag.</summary>
    private sealed class PictureRow
    {
        public CheckBox Check = null!;

        /// <summary>The square. It is kept so that the picture is put in through <see
        /// cref="PictureWell.Show"/>, which also removes the record glyph.</summary>
        public PictureWell Well = null!;

        public Border ThumbHost = null!;
        public Image Thumb = null!;
        public Button Pick = null!;

        /// <summary>The picture. For the cover only, this is also the bigger copy that goes on the
        /// folder when a catalogue scan was found. A file picked from disk is one picture at one
        /// size.</summary>
        public byte[]? Bytes, FolderBytes;
        public string Mime = "image/jpeg";

        public bool On => Check.IsChecked == true;
        public bool Chosen => Bytes is { Length: > 0 };
    }

    /// <summary>The two picture rows, or null for a picture the caller did not offer. See <see
    /// cref="_offersCover"/> and <see cref="_offersDiscArt"/>. Null is used rather than an empty row. A
    /// <see cref="PictureRow"/> is made of its controls, and a row whose tick was never built has no
    /// state to read. Reading the tick of a row that was not built would throw. This can happen when
    /// the window opens over a selection that spans several albums.</summary>
    private PictureRow? _cover;
    private PictureRow? _discArt;

    /// <summary>The release catalogues, as this window uses them. The host runs the search, the user
    /// picks a pressing, and the release's facts come back, with its sleeve when one is asked for. This
    /// is null when the caller has no album to look up. Editing a scatter of tracks from all over the
    /// library is not a record, and there is nothing to search for. In that case the lookup button
    /// never appears.</summary>
    /// <remarks>The bool says whether the cover is wanted. Fetching a sleeve at full size is a second
    /// throttled request. So it is not made when the art box is unticked.</remarks>
    private readonly Func<ArtAsk, Task<MassEditLookup?>>? _lookup;

    /// <summary>True when the caller can put a disc.png somewhere. This means the tracks are already
    /// filed in one album folder. Queue rows are not filed yet. A selection that spans several records
    /// has no single folder to put it in. So neither is offered this row.</summary>
    private readonly bool _offersDiscArt;

    /// <summary>True when a cover means something here. A cover is one picture for one record. Over a
    /// scatter of tracks from all over the library, it would put the same sleeve on a dozen albums.
    /// </summary>
    private readonly bool _offersCover;

    private MassEditResult? _result;

    /// <summary>True when the selection reaches across more than one record. This does not narrow what
    /// the window offers. The window exists to repair a scatter, and a wrong album name looks like a
    /// scatter from here. It raises the caution instead, because "this edit lands on four different
    /// records" is the one thing the form cannot show by itself.</summary>
    private readonly bool _spansAlbums;

    public MassEditDialog(
        int selectionCount, IReadOnlyList<MassEditFieldInput> fields,
        Func<ArtAsk, Task<MassEditLookup?>>? lookup = null, bool discArt = false, bool cover = true,
        bool spansAlbums = false)
    {
        InitializeComponent();
        _lookup = lookup;
        _offersDiscArt = discArt;
        _offersCover = cover;
        _spansAlbums = spansAlbums;
        _fields = fields.ToDictionary(f => f.Kind);
        Shell.AttachResize(this, minWidth: 700, minHeight: 560, maxWidth: 1080, defaultHeight: 680);
        BuildForm();

        // Nothing is ticked yet, so there is nothing for a release to fill in.
        LookupHost.Visibility = _lookup is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLookupState();
    }

    // ── the form ──────────────────────────────────────────────────────────────

    /// <summary>Lays out whatever the caller offered, in the catalogue's own groups and order. Nothing
    /// here names a field. The window shows what it was given. So a selection that spans several
    /// records (track fields only, no pictures) and a selection inside one album (everything) both look
    /// like a form, not like the same form with holes in it.</summary>
    private void BuildForm()
    {
        // Basic: what the record is, and the tags carried on the track itself.
        AddGroup(BasicLeft, TagFields.Groups.Release, g => AddFields(g, TagFields.Groups.Release));
        AddGroup(BasicRight, TagFields.Groups.Track, g => AddFields(g, TagFields.Groups.Track));
        // The tempo is under the same heading as in the per-file editor, so "the tempo is under
        // Measured" is true in every window. It has no tab of its own here. This window edits a
        // selection, and the readings on that tab belong to one file.
        AddGroup(BasicRight, TagFields.Groups.Measured, g => AddFields(g, TagFields.Groups.Measured));

        // Credits: who played it, and who put it out.
        AddGroup(CreditsLeft, TagFields.Groups.Credits, g => AddFields(g, TagFields.Groups.Credits));
        AddGroup(CreditsRight, TagFields.Groups.Publishing, g => AddFields(g, TagFields.Groups.Publishing));

        // Work: what it performs, how it sorts, which pressing it is, and its ids. The identifiers go
        // last. They are the least often used and the longest, so placing them earlier pushes the
        // fields that people edit most below the visible area.
        AddGroup(WorkLeft, TagFields.Groups.Work, g => AddFields(g, TagFields.Groups.Work));
        AddGroup(WorkLeft, TagFields.Groups.Pressing, g => AddFields(g, TagFields.Groups.Pressing));
        AddGroup(WorkRight, TagFields.Groups.Sorting, g => AddFields(g, TagFields.Groups.Sorting));
        AddGroup(WorkRight, TagFields.Groups.Identifiers, g => AddFields(g, TagFields.Groups.Identifiers));

        AddPictures();
        BuildTabs();
    }

    /// <summary>The per-file editor's tab strip, over this window's own panels.
    ///
    /// <para>Artwork is dropped where the caller offered neither picture, because a selection spread
    /// across folders has no single cover to set. Lyrics is never here. One set of words across a
    /// selection would give every track the same song.</para>
    /// </summary>
    private void BuildTabs()
    {
        var strip = new RailTabStrip(
            new(MetadataTabs.Basic, "Details_TabBasic", "\uf02b"),
            new(MetadataTabs.Credits, "Details_TabCredits", "\uf0c0"),
            new(MetadataTabs.Work, "Details_TabWork", "\uf02d"),
            new(MetadataTabs.Artwork, "Details_Art", "\uf03e"));

        strip.Register(MetadataTabs.Basic, PanelBasic);
        strip.Register(MetadataTabs.Credits, PanelCredits);
        strip.Register(MetadataTabs.Work, PanelWork);
        strip.Register(MetadataTabs.Artwork, PanelArtwork);

        if (!_offersCover && !_offersDiscArt)
            strip.Hide(MetadataTabs.Artwork);

        TabStripHost.Child = strip;
        strip.Select(MetadataTabs.Basic);
    }

    /// <summary>The pictures, on their own tab, in the album editor's square. See <see
    /// cref="PictureWell"/>. The tick stays. This window writes only the armed fields, and a cover is
    /// no different from any other field in that.</summary>
    private void AddPictures()
    {
        if (_offersCover)
            AddPictureWell(_cover = new PictureRow(), "Details_GroupCover");
        // The disc label is shown beside the cover, because both are the same kind of decision. It is
        // only offered where it can be saved, because it is written as the album folder's disc.png.
        if (_offersDiscArt)
            AddPictureWell(_discArt = new PictureRow(), "Details_GroupDisc");
    }

    /// <summary>Builds one group and hangs it in a column. Nothing is added when the group has no rows.
    /// So a caller that offered none of its fields gets no empty heading.</summary>
    private static void AddGroup(Panel column, string headerKey, Action<DenseFormGroup> rows)
    {
        var group = new DenseFormGroup(ticks: true);
        rows(group);
        group.AddTo(column, headerKey);
    }

    /// <summary>Every offered field of one group, in catalogue order.</summary>
    private void AddFields(DenseFormGroup group, string groupKey)
    {
        foreach (var field in TagFields.All.Where(f => f.GroupKey == groupKey))
        {
            if (!_fields.TryGetValue(field.Kind, out var offered))
                continue;
            // A field drawn by its partner is not given a row of its own, unless the partner was not
            // offered. In that case it stands alone, and it does not disappear with the partner.
            if (Owner(field.Kind) is { } owner && _fields.ContainsKey(owner))
                continue;
            if (field.PairedWith is { } second)
                AddPairRow(group, offered, second);
            else
                AddFieldRow(group, offered);
        }
    }

    /// <summary>The field that draws this one on its row, when it is the second half of a
    /// pair.</summary>
    private static TagFieldKind? Owner(TagFieldKind kind) =>
        TagFields.All.FirstOrDefault(f => f.PairedWith == kind)?.Kind;

    private void AddFieldRow(DenseFormGroup group, MassEditFieldInput field)
    {
        var (tick, label, input) = BuildField(field);
        group.Add(tick, label, input, topAlign: field.Field.Multiline);
    }

    /// <summary>The two halves of one fact on one line, such as "Disc [1] of [2]" or "Track [3] of
    /// [12]". Two rows for it would use more height than this layout has. Each half keeps its own tick.
    /// Setting the count across a selection without changing the numbers is a real edit.</summary>
    private void AddPairRow(DenseFormGroup group, MassEditFieldInput first, TagFieldKind second)
    {
        var (tick, label, input) = BuildField(first);
        var line = DenseForm.Pair(input);
        if (_fields.TryGetValue(second, out var offered))
        {
            var (secondTick, _, secondInput) = BuildField(offered);
            line.Children.Add(DenseForm.Joiner("MassEdit_Of"));
            line.Children.Add(secondTick);
            line.Children.Add(secondInput);
        }
        group.Add(tick, label, line);
    }

    /// <summary>Builds a field: the tick that arms it, its label, and the control holding the value. It is
    /// a plain box where the selection agrees, a pick-or-type list where it does not.</summary>
    private (CheckBox Tick, TextBlock Label, Control Input) BuildField(MassEditFieldInput field)
    {
        var tick = NewTick();
        var label = DenseForm.Label(field.Field.LabelKey);
        bool multiline = field.Field.Multiline;

        Control control;
        Func<string> read;
        Action<string> write;
        if (field.Field.Options is { Count: > 0 } options)
        {
            // A choice field is a closed list, whether or not the selection agrees. There is no extra
            // state to type. "Several values" is shown by starting with no option selected.
            var choice = ChoiceBox(options, field.Values.AllSame ? field.Values.CommonValue : null);
            control = choice;
            read = () => ReadChoice(choice, options);
            write = v => WriteChoice(choice, options, v);
        }
        else if (field.Field.SuggestionsFor is { } stylesFor)
        {
            // The styles are narrowed by genre when the selection agrees on one genre. A mass edit can
            // span records of different genres. Where the genre differs, there is no one set of styles
            // to offer, so the box is a plain one to type into.
            var agreed = _fields.TryGetValue(TagFieldKind.Genre, out var genre) && genre.Values.AllSame
                ? genre.Values.CommonValue
                : null;
            var combo = SuggestBox(stylesFor(agreed), field.Values.AllSame ? field.Values.CommonValue : null);
            combo.IsEnabled = false;
            if (!field.Values.AllSame)
                combo.PlaceholderText = Loc.Get("MassEdit_Multiple");
            if (_rows.FirstOrDefault(r => r.Kind == TagFieldKind.Genre)?.Control is ComboBox genreBox)
                FollowGenre(combo, genreBox, stylesFor);
            control = combo;
            read = () => ReadSuggest(combo);
            write = v => SetSuggestValue(combo, v);
        }
        else if (field.Field.Suggestions is { Count: > 0 } suggestions)
        {
            // The usual answers are offered whether or not the selection agrees. Where it does not
            // agree, the box starts empty. The list is still the fastest way to make them all agree.
            var combo = SuggestBox(suggestions, field.Values.AllSame ? field.Values.CommonValue : null);
            combo.IsEnabled = false;
            if (!field.Values.AllSame)
                combo.PlaceholderText = Loc.Get("MassEdit_Multiple");
            control = combo;
            read = () => ReadSuggest(combo);
            write = v => SetSuggestValue(combo, v);
        }
        else if (field.Values.AllSame)
        {
            var box = new TextBox { Text = field.Values.CommonValue, IsEnabled = false };
            if (multiline)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.Height = 72;
            }
            if (field.Field.Numeric)
                box.AcceptDigitsOnly();
            control = box;
            read = () => box.Text;
            write = v => box.Text = v;
        }
        else
        {
            var combo = new ComboBox
            {
                IsEditable = true,
                IsEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = Loc.Get("MassEdit_Multiple"),
                // Clear is also at the head of this list. The list shows what these files already say,
                // and "none of it" must be selectable in the same way as any value.
                ItemsSource = field.Values.DistinctValues.Prepend(ClearEntry).ToList(),
            };
            control = combo;
            read = () => ReadSuggest(combo);
            // A release's answer is typed into the editable part, not picked from the list. The list
            // holds what the selection already says, and the catalogue may disagree with all of it.
            write = v => combo.Text = v;
        }

        if (TagFields.Resolve(field.Field.Width) is { } w)
            control.Narrow(w);

        // The control only takes part when its box is ticked, so an untouched field is left alone.
        void Sync(object s, RoutedEventArgs e) => control.IsEnabled = tick.IsChecked == true;
        tick.Checked += Sync;
        tick.Unchecked += Sync;

        _rows.Add(new Row(field.Kind, tick, read, write, control));
        return (tick, label, control);
    }

    /// <summary>The first entry in every tag list. Picking it empties the field rather than setting it
    /// to anything.
    ///
    /// <para>Each of these windows can set a tag, but none of them can clear one. An empty box means
    /// "leave this alone". It has to, or opening a window over forty tracks and pressing Apply would
    /// wipe every field that nobody touched. So "make it empty" had nowhere to be said. It is said
    /// here, in the one place where all the editors get their lists.</para>
    /// </summary>
    internal static string ClearEntry => Loc.Get("Tag_Clear");

    /// <summary>A closed list of the values a choice field may take, translated for reading and
    /// selected by the token that is actually stored. The three metadata editors show a choice field
    /// this way, so the advisory flag reads the same in each.</summary>
    ///
    /// <para>Read and written through <see cref="ReadChoice"/> and <see cref="WriteChoice"/>. The Clear
    /// row shifts the indexes, so the index of an option is not the index of its row. Callers should
    /// not do that arithmetic themselves.</para>
    internal static ComboBox ChoiceBox(IReadOnlyList<TagFieldOption> options, string? selected)
        => new()
        {
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // No selection shows as "they do not all agree", which is what it means here.
            PlaceholderText = Loc.Get("MassEdit_Multiple"),
            ItemsSource = options.Select(o => Loc.Get(o.LabelKey)).Prepend(ClearEntry).ToList(),
            SelectedIndex = selected is null ? -1 : ChoiceIndexOf(options, selected),
        };

    /// <summary>What a choice box is currently saying: the option's stored token, or empty for the
    /// Clear row and for a box nobody has answered.</summary>
    internal static string ReadChoice(ComboBox box, IReadOnlyList<TagFieldOption> options)
        => TagPicker.ValueAt(options, box.SelectedIndex);

    /// <summary>Puts a stored token into a choice box. An empty value picks the Clear row, so a field
    /// that is about to be emptied says so on its own line.</summary>
    internal static void WriteChoice(ComboBox box, IReadOnlyList<TagFieldOption> options, string? value)
        => box.SelectedIndex = ChoiceIndexOf(options, value);

    /// <summary>An option's place in the list the box is showing. This is one more than its place in
    /// the options, because the Clear row is in front of them. Empty is the Clear row itself.</summary>
    private static int ChoiceIndexOf(IReadOnlyList<TagFieldOption> options, string? value)
        => TagPicker.IndexOf(options, value);

    /// <summary>A box with a list hanging off it, for a field that has usual answers without having
    /// only those answers. The list is offered, and any other value can still be typed in.
    ///
    /// <para>Shared with the album editor and the per-file editor, so a genre is picked the same way in
    /// all three windows.</para>
    /// </summary>
    /// <param name="text">What is in the box to start with. Null leaves it empty. That is how "these
    /// files do not agree" is shown.</param>
    internal static ComboBox SuggestBox(IReadOnlyList<string> suggestions, string? text)
    {
        var combo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new System.Collections.ObjectModel.ObservableCollection<string>(
                suggestions.Prepend(ClearEntry)),
        };

        SetSuggestValue(combo, text);

        // A value typed in, rather than picked from the list, is still the field's value. It must still
        // be shown when the box is closed again. A closed list shows its selected item, and a value
        // that is not in the list is not selected. So the value is added as an entry in the list.
        //
        // Use the raw text, not the value that the field will be written as. Choosing Clear must leave
        // the closed box showing "<Clear>". If the selection were emptied instead, the box would show
        // nothing. On a field where the tracks disagree, that is the placeholder. So the one action
        // that means "empty this field" would look like no answer at all.
        combo.DropDownClosed += (_, _) => SetSuggestValue(combo, combo.Text);
        combo.LostFocus += (_, _) => SetSuggestValue(combo, combo.Text);
        return combo;
    }

    /// <summary>What a suggest box is currently saying. Never the Clear row's own label: that row
    /// stands for emptiness, and emptiness is what it reads as.</summary>
    internal static string ReadSuggest(ComboBox combo)
        => TagPicker.ValueOfText(combo.Text, ClearEntry);

    /// <summary>Keeps a style box offering the styles of the genre box beside it.
    ///
    /// <para>A style belongs to a genre, so the list of styles depends on the genre. The genre can be
    /// changed while the window is open. Without this, the style list would stay as it was when the
    /// window was built. It would be the wrong list as soon as the genre is corrected, and nothing
    /// would show that.</para>
    ///
    /// <para>Text that was typed survives the refill. The styles under Rock are not the styles under
    /// House. So a value that was right a moment ago may not be in the new list at all. It is still
    /// what the field says. Emptying it because the list changed would discard an answer that nobody
    /// withdrew. This is why the box is editable. The list is only the usual answers.</para>
    /// </summary>
    internal static void FollowGenre(ComboBox styles, ComboBox genre,
                                     Func<string?, IReadOnlyList<string>> stylesFor)
    {
        genre.SelectionChanged += (_, _) => Refill();
        genre.DropDownClosed += (_, _) => Refill();
        genre.LostFocus += (_, _) => Refill();

        void Refill()
        {
            if (styles.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<string> items)
                return;

            var said = styles.Text;
            items.Clear();
            items.Add(ClearEntry);
            foreach (var style in stylesFor(ReadSuggest(genre)))
                items.Add(style);
            SetSuggestValue(styles, said);
        }
    }

    /// <summary>Puts a value into a suggest box and keeps it there.
    ///
    /// <para>A file's genre is whatever the file says. The list holds only the usual answers. So
    /// "Nederpop" must read in the closed box exactly as "Rock" does. A value that is not in the list
    /// is added as an entry just after the Clear row, where a user would look for the answer that this
    /// record already gave.</para>
    ///
    /// <para>Empty is also a real answer. It clears the selection, rather than leaving the last one
    /// selected. So a field that somebody emptied does not fill itself back in.</para>
    /// </summary>
    internal static void SetSuggestValue(ComboBox combo, string? value)
    {
        if (combo.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<string> items)
            return;

        // Clear is chosen, and it stays chosen. The box shows the Clear row on its own line. The value
        // written is empty. ReadSuggest handles that, not this method.
        if (string.Equals(value, ClearEntry, StringComparison.Ordinal))
        {
            combo.SelectedIndex = 0;
            combo.Text = ClearEntry;
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            combo.SelectedIndex = -1;
            combo.Text = "";
            return;
        }

        // Search from the first answer. Entry 0 is the Clear row, and it is not an answer.
        var at = -1;
        for (var i = TagPicker.FirstAnswer; i < items.Count; i++)
        {
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            // Insert after the Clear row, which stays at the head. This is where a user would look for
            // the value that this record already has. Clear is not one of the answers.
            items.Insert(TagPicker.FirstAnswer, value);
            at = TagPicker.FirstAnswer;
        }

        if (combo.SelectedIndex != at)
            combo.SelectedIndex = at;
        combo.Text = items[at];
    }

    /// <summary>Which entry holds this stored token, or the first one. A value that nothing recognises is a
    /// value the file should not keep.</summary>
    internal static int IndexOf(IReadOnlyList<TagFieldOption> options, string? value)
    {
        for (int i = 0; i < options.Count; i++)
            if (string.Equals(options[i].Value, value ?? "", StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    /// <summary>One picture, as the album editor draws it: a tick to arm the row, its name, the
    /// square, what is in it, and the button that changes it.
    /// </summary>
    private void AddPictureWell(PictureRow row, string labelKey)
    {
        row.Check = NewTick();

        var well = new PictureWell(PictureEdge);
        row.Well = well;
        row.Thumb = well.Thumb;
        row.ThumbHost = new Border
        {
            Child = well.Root,
            Opacity = 0.4,   // A Border has no IsEnabled, so it is greyed out until the box is ticked
        };

        row.Pick = new Button
        {
            // The same button as the album editor's Change button, with the same glyph and label
            Content = AlbumEditDialog.IconLabel("\uf03e", "MassEdit_ChooseImage"),
            IsEnabled = false,
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
        };
        row.Pick.Click += (_, _) => _ = ChooseImageAsync(row);

        void Sync(object s, RoutedEventArgs e)
        {
            row.ThumbHost.Opacity = row.On ? 1 : 0.4;
            row.Pick.IsEnabled = row.On;
        }
        row.Check.Checked += Sync;
        row.Check.Unchecked += Sync;

        // Named the same way as the per-file editor names its cover: a group header over a thin rule.
        // The tick sits on that line, not beside the square. At this height, a tick beside the square
        // would float in the middle of a tall row with nothing next to it.
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(row.Check);
        heading.Children.Add(new TextBlock
        {
            Text = Loc.Get(labelKey),
            Style = DenseForm.Res<Style>("DenseGroupHeaderStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var pane = new StackPanel { Spacing = 6, Width = PictureEdge };
        pane.Children.Add(heading);
        pane.Children.Add(new Border
        {
            Height = 1,
            Background = DenseForm.Res<Brush>("DenseRuleBrush"),
            Margin = new Thickness(0, 3, 0, 2),
        });
        pane.Children.Add(row.ThumbHost);
        pane.Children.Add(row.Pick);

        ArtworkHost.Children.Add(pane);
    }

    /// <summary>The album editor's square, at the album editor's size. The two windows show the same
    /// pictures, so they use the same size. 220 is also what the pane needs for a button with a glyph
    /// and a label, without clipping it.</summary>
    private const double PictureEdge = 220;

    /// <summary>Every tick in the window, including the pictures'. The lookup button watches
    /// these.</summary>
    private IEnumerable<CheckBox> AllTicks() =>
        _rows.Select(r => r.Check)
             .Concat(_cover is null ? [] : new[] { _cover.Check })
             .Concat(_discArt is null ? [] : new[] { _discArt.Check });

    private CheckBox NewTick()
    {
        var tick = new CheckBox { MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        StyleCheck(tick);
        tick.Checked += (_, _) => UpdateLookupState();
        tick.Unchecked += (_, _) => UpdateLookupState();
        return tick;
    }

    // ── the release lookup ────────────────────────────────────────────────────

    /// <summary>The lookup only makes sense once something is ticked, because it fills ticked fields.
    /// With none ticked it has nowhere to put what it finds. When it is disabled, the tooltip says why.
    /// The tooltip is on the host, because a disabled control takes no pointer input, so it would never
    /// show its own tooltip.
    /// </summary>
    private void UpdateLookupState()
    {
        UpdateCaution();

        if (_lookup is null)
            return;
        bool any = AllTicks().Any(c => c.IsChecked == true);
        LookupButton.IsEnabled = any;
        ToolTipService.SetToolTip(LookupHost, any ? null : Loc.Get("MassEdit_LookUpNeedsFields"));
    }

    /// <summary>The caution at the head of the window. The selection spans more than one record, so a
    /// ticked field lands on all of them.
    ///
    /// <para>This is re-read whenever a tick changes. The second sentence is only true while a picture
    /// is armed. A cover is written into every folder that the edit touches. Over four records, that is
    /// four records with one sleeve. Ticking the picture makes that worth saying. Unticking it removes
    /// the sentence.</para>
    /// </summary>
    private void UpdateCaution()
    {
        if (!_spansAlbums)
            return;   // The band stays collapsed when the selection is one record

        var text = Loc.Get("MassEdit_CautionManyAlbums");
        if (_cover is { On: true } || _discArt is { On: true })
            text += " " + Loc.Get("MassEdit_CautionManyAlbumsArt");

        CautionBandText.Text = text;
        CautionBand.Visibility = Visibility.Visible;
    }

    /// <summary>Opens the release lookup and puts what the user picked into every ticked field that the
    /// release has a value for. Fields that the release knows nothing about (a comment, a BPM) are left
    /// as they are. They stay ticked, and they are still written with whatever they hold.</summary>
    private async void LookUpRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_lookup is null)
            return;

        // The search covers two rate-limited catalogues, and then fetches a full-size sleeve. This
        // takes long enough that a second click would run the whole lookup again.
        LookupButton.IsEnabled = false;
        try
        {
            // No picture is requested when no picture row is ticked. A sleeve and a disc label would be
            // two more requests with nowhere to go. When one is ticked, what the window already holds
            // sets where the release window's overwrite offer starts.
            var ask = _cover is { On: true } || _discArt is { On: true }
                ? _cover is { Chosen: true } || _discArt is { Chosen: true } ? ArtAsk.Held : ArtAsk.Missing
                : ArtAsk.None;

            if (await _lookup(ask) is not { } found)
                return;

            foreach (var row in _rows)
                if (row.Check.IsChecked == true && found.Fields.TryGetValue(row.Kind, out var value))
                    row.Write(value);

            if (_cover is { On: true } sleeve && found.Art is { Embedded.Length: > 0 } art)
            {
                sleeve.Bytes = art.Embedded;
                sleeve.FolderBytes = art.Folder;
                sleeve.Mime = art.Mime;
                await ShowThumbAsync(sleeve);
            }
            if (_discArt is { On: true } disc && found.DiscArt is { Length: > 0 } discBytes)
            {
                disc.Bytes = discBytes;
                disc.Mime = "image/png";
                await ShowThumbAsync(disc);
            }
        }
        catch (Exception ex)
        {
            // If a catalogue does not answer, the form stays as it was. The failure is still reported,
            // because a button that silently does nothing is the hardest fault to find.
            RPM.Core.Diagnostics.Faults.Note("mass-edit", "release lookup", ex);
        }
        finally
        {
            UpdateLookupState();
        }
    }

    // ── pictures ──────────────────────────────────────────────────────────────

    private async Task ChooseImageAsync(PictureRow row)
    {
        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            row.Bytes = row.FolderBytes = File.ReadAllBytes(file.Path);
            row.Mime = file.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            await ShowThumbAsync(row);
        }
        catch (Exception ex)
        {
            // The picker is unavailable, or the file cannot be read. Leave the picture unset, and
            // report why.
            RPM.Core.Diagnostics.Faults.Note("mass-edit", "choosing an image", ex);
        }
    }

    /// <summary>Puts the chosen picture in its square. This uses MemoryStream and AsRandomAccessStream,
    /// which is the reliable path under Uno Skia. Do not use InMemoryRandomAccessStream with
    /// CryptographicBuffer.CreateFromByteArray here. Uno does not implement that call. The error is
    /// swallowed by the caller's catch, and the square stays empty however good the picture
    /// is.</summary>
    private static async Task ShowThumbAsync(PictureRow row)
    {
        if (row.Bytes is not { Length: > 0 } bytes)
            return;
        // A small preview of the picture for the square. The full bytes still go into the files.
        var preview = await Task.Run(() => ArtScaler.Downscale(bytes, ArtScaler.TileEdge));
        var stream = Presentation.Common.BitmapBytes.Stream(preview);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        // Go through the well, not the Image directly. The record glyph sits over the picture, and it
        // must be hidden with the picture. Otherwise a chosen cover shows a placeholder on top of it.
        row.Well.Show(bmp);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var fields = new Dictionary<TagFieldKind, string>();
        foreach (var row in _rows)
            if (row.Check.IsChecked == true)
                fields[row.Kind] = row.Read().Trim();

        // A ticked picture with nothing chosen is not a change. The user ticked the row but did not
        // pick a picture. Ticking it does not mean clearing the record's cover.
        var cover = _cover is { On: true, Chosen: true } ? _cover : null;
        var disc = _discArt is { On: true, Chosen: true } ? _discArt : null;
        _result = new MassEditResult(
            fields,
            cover?.Bytes,
            cover?.Mime,
            cover?.FolderBytes,
            disc?.Bytes);
        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Opens the mass edit window over <paramref name="fields"/>. Returns the chosen changes, or null when
    /// the user cancels or ticks nothing.</summary>
    /// <param name="lookup">How to ask the release catalogues, when the selection is a record that can
    /// be looked up. When omitted, the window has no lookup button, and the pictures offer only the
    /// file picker.</param>
    /// <param name="discArt">Whether to offer the disc-art row. Only where the tracks are already
    /// filed, because the picture is written as the album folder's disc.png.</param>
    /// <param name="spansAlbums">Whether the selection reaches across more than one record. This raises
    /// the caution band. It does not change what the window offers.</param>
    public static async Task<MassEditResult?> ShowAsync(
        XamlRoot xamlRoot, int selectionCount, IReadOnlyList<MassEditFieldInput> fields,
        Func<ArtAsk, Task<MassEditLookup?>>? lookup = null, bool discArt = false, bool cover = true,
        bool spansAlbums = false)
    {
        var dialog = new MassEditDialog(selectionCount, fields, lookup, discArt, cover, spansAlbums)
        {
            XamlRoot = xamlRoot,
        };
        await DialogHost.ShowAsync(dialog);
        if (dialog._result is not { } r)
            return null;
        return r.Fields.Count == 0 && r.ArtBytes is null && r.DiscArtBytes is null ? null : r;
    }
}
