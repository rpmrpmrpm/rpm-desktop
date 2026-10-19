using System.IO;
using RPM.Core.Art;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The tag panel beside the import queue. It shows the few fields that are used most when files are being
/// prepared, and it edits whatever is ticked.
///
/// <para>Not every field that the app knows is shown. Every other field is in the full editor, which opens
/// from the right-click menu. The fields come from <see cref="TagFields"/>, not from hand-written layout, so
/// a label, a width or a list of suggestions is declared once and looks the same in every window.</para>
///
/// <para>Nothing is written until Apply, and then only the fields that were changed. Setting a genre across
/// forty rows must not overwrite their titles with the value of the first row. Writing every box would do
/// exactly that.</para>
/// </summary>
/// <summary>
/// What came back from the cover search: the picture for the well that asked, and, where the chosen sleeve
/// came from a source that had one, the disc that goes with it.
/// </summary>
/// <param name="Disc">Null in a disc search, where the disc is the picture. Also null when the chosen result
/// had no disc, which is most results.</param>
public sealed record FoundArt(byte[] Picture, byte[]? Disc);

/// <summary>What one press of Apply did: the rows that were written to, the cover that was written with them,
/// and the disc label, which cannot be written yet. The rows carry everything else, their fields and their
/// sleeve, because a queued row IS the pending edit. A record's disc label does not belong to one row, so it
/// is returned beside the rows.</summary>
/// <param name="Rows">The rows the panel wrote its fields onto.</param>
/// <param name="DiscArt">The disc label to hold against the record until its files land, or null.</param>
/// <param name="Cover">The sleeve that was written on the rows, when one was. It is reported, not acted on,
/// because it is already on the rows. The workbench uses it to say what the press actually wrote.</param>
public sealed record TagPanelEdit(IReadOnlyList<TrackRow> Rows, byte[]? DiscArt, byte[]? Cover = null);

public sealed partial class ImportTagPanel : UserControl
{
    /// <summary>The panel's fields, in the order they are read.</summary>
    private static readonly TagFieldKind[] Fields =
    [
        TagFieldKind.Title,
        TagFieldKind.Artist,
        TagFieldKind.Album,
        TagFieldKind.Year,
        TagFieldKind.TrackNo,
        TagFieldKind.Genre,
        TagFieldKind.Subgenre,
        // Album, EP, single, live, mixtape, compilation or soundtrack: what the artist page shelves the record by.
        TagFieldKind.ReleaseType,
        TagFieldKind.Comment,
        TagFieldKind.AlbumArtist,
        TagFieldKind.Composers,
        TagFieldKind.Disc,
    ];

    /// <summary>
    /// One built field: what it is, how to read the box, and what the box said when it was filled in.
    ///
    /// <para>"Touched" is a COMPARISON, not a flag that someone sets. A flag would have to be set by an event
    /// and cleared after a time window, for example "still loading, so this does not count". An event that
    /// arrives after that window closes would then mark a field that nobody touched, and Revert would be
    /// offered for an edit that does not exist. A box that still shows what it showed when it was filled is
    /// not an edit, whatever raised the event.</para>
    /// </summary>
    private sealed class Entry(TagField field, Func<string> read, Func<string> shown, Action<string> write)
    {
        public TagField Field { get; } = field;

        /// <summary>Puts a value into the box, for example when a release lookup fills the form. This is not an
        /// edit of the rows. The box holds the value until Apply is pressed, the same as any typed value.</summary>
        public Action<string> Write { get; } = write;

        /// <summary>What the field would WRITE: the stored token, and empty where the box says
        /// &lt;Clear&gt;.</summary>
        public Func<string> Read { get; } = read;

        /// <summary>What the box is SHOWING. This is a different question. Where the tracks disagree, an untouched
        /// box and a box that the user has just set to &lt;Clear&gt; would both write nothing. Only the second
        /// is an edit.</summary>
        public Func<string> Shown { get; } = shown;

        /// <summary>What the box showed when it was filled in. Moves on Apply, because what was
        /// written is what the box now says.</summary>
        public string Loaded { get; set; } = "";

        public bool Touched => !string.Equals(Shown(), Loaded, StringComparison.Ordinal);
    }

    private readonly List<Entry> _entries = [];

    /// <summary>The genre box, so the style box built after it can follow it. Fields are built in
    /// TagFields.All order and the genre comes before the style, so it is there by the time it is
    /// wanted; null when this panel is not showing a genre at all.</summary>
    private ComboBox? _genreBox;

    private IReadOnlyList<TrackRow> _rows = [];

    public ImportTagPanel()
    {
        InitializeComponent();
        RefreshButtons();
    }

    /// <summary>
    /// Raised after Apply wrote to the rows, with THE ROWS IT WROTE TO. The workbench saves them and rebuilds
    /// the tree, because an edited album or artist is a row that now belongs somewhere else.
    ///
    /// <para>The rows are sent with the event on purpose. They are the panel's own copies, edited in memory and
    /// not yet written anywhere. A handler that asked the store for the ticked rows again would get clean
    /// copies, and saving those would undo the edit.</para>
    /// </summary>
    public event Action<TagPanelEdit>? Applied;

    /// <summary>Raised when the panel is closed from its own header.</summary>
    public event Action? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>
    /// Points the panel at what is ticked.
    ///
    /// <para>Pending edits are DROPPED when the set changes. If they were kept, a genre typed for one record
    /// could be applied to a different record after a few more ticks, and nothing on screen would show it.</para>
    /// </summary>
    public void SetRows(IReadOnlyList<TrackRow> rows)
    {
        // A picture chosen here and not yet applied survives a rebuild of the form, but it is dropped when the
        // selection changes. These are different events. The panel is also rebuilt when the queue changes for
        // other reasons, such as a scan ending, a duplicate pass or an import. A rebuild must not throw away a
        // sleeve that the user has just chosen. If it did, the well would silently show the old picture again.
        var ticked = Signature(rows);
        if (ticked != _chosenFor)
        {
            _chosenCover = _chosenDisc = null;
            _chosenFor = ticked;
        }

        _rows = rows;
        Build();
    }

    private void Build()
    {
        _entries.Clear();
        _genreBox = null;
        FieldHost.Children.Clear();

        if (_rows.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            FieldScroller.Visibility = Visibility.Collapsed;
            _lookupButton = null;   // The button is a row of the form. With no form, there is no button.
            RefreshButtons();
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        FieldScroller.Visibility = Visibility.Visible;

        {
            // No tick column. Every field in this panel is used by typing into it. A column of checkboxes
            // that nobody uses would only push the inputs off the line they share.
            var group = new DenseFormGroup(ticks: false);

            // The lookup is the first row of the form, in the form's own grid. This puts it on the same left edge
            // as the boxes. The label gutter is as wide as the longest label and no wider (see below). So the edge
            // where the answers start is a number that only this grid knows. A margin set elsewhere would line up
            // in English and nowhere else. The lookup has no label of its own, because it fills every field below
            // it, not one of them.
            group.Add(tick: null, new TextBlock(), _lookupButton = BuildLookupButton());

            foreach (var kind in Fields)
            {
                if (TagFields.All.FirstOrDefault(f => f.Kind == kind) is not { } field)
                    continue;
                var (label, input) = BuildField(field);
                // The dialogs right-align their labels against a fixed 82 gutter. In a panel this narrow, that
                // pushes the words away from the edge where they should start. Here the labels start at the same
                // left edge as every other line of the pane.
                label.HorizontalAlignment = HorizontalAlignment.Left;
                group.Add(tick: null, label, input, topAlign: field.Multiline);
            }

            // No heading. The panel's own header already says "Tags", and a group heading would repeat that word.
            if (group.BuildBare() is Grid rows)
            {
                // The gutter is as wide as the longest label needs, and no wider. The dialogs use a fixed width so
                // that two groups line up down a wide card. There is only one group here, so there is nothing to
                // line up with.
                rows.ColumnDefinitions[0].Width = GridLength.Auto;
                FieldHost.Children.Add(rows);
            }

            // The pictures sit under the form and span its whole width, label gutter included. They are the only
            // row with nothing on the left. In the input column the pair would be only half a panel wide, and the
            // second picture would fall off the edge.
            FieldHost.Children.Add(BuildArtwork());
        }

        RefreshButtons();
    }

    /// <summary>
    /// One field: its label, and the control that holds what the ticked rows say. It is a plain box where the
    /// rows agree, and a pick-or-type list where they do not. This follows the mass editor's convention, so a
    /// field met in two windows does not need learning twice.
    /// </summary>
    /// <summary>
    /// The one action here that fills every field at once. It is rebuilt with the form, like every control in
    /// it. It is held in a field, not found by name: <see cref="_lookupButton"/> is greyed out while the lookup
    /// runs, and is null while there is no form.
    /// </summary>
    private Button BuildLookupButton()
    {
        var button = new Button
        {
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
            Height = 22,
            MinHeight = 0,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = AlbumEditDialog.IconLabel("\uf002", "MassEdit_LookUpRelease"),   // Magnifier icon.
        };
        button.Click += LookUpRelease_Click;
        return button;
    }

    private (TextBlock Label, Control Input) BuildField(TagField field)
    {
        var label = DenseForm.Label(field.LabelKey);
        var values = TagPanelValues.Of(_rows, field);

        Control control;
        Func<string> read;
        Func<string> shown;
        Action<string> write;

        if (field.Options is { Count: > 0 } options)
        {
            var choice = MassEditDialog.ChoiceBox(options, values.AllSame ? values.CommonValue : null);
            choice.IsEnabled = true;
            choice.SelectionChanged += (_, _) => Touch(field);
            control = choice;
            read = () => MassEditDialog.ReadChoice(choice, options);
            // The row that is picked, not the token it stands for. Nothing picked and the Clear row picked both
            // write nothing, but choosing either of them is an edit.
            shown = () => choice.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            write = v => MassEditDialog.WriteChoice(choice, options, v);
        }
        else if (field.SuggestionsFor is { } stylesFor)
        {
            // The list is narrowed by the genre these rows carry, and it follows the genre box. This matters most
            // during an import, when the style list is often wrong.
            var genre = TagPanelValues.Of(_rows, TagFields.Get(TagFieldKind.Genre));
            var combo = MassEditDialog.SuggestBox(
                stylesFor(genre.AllSame ? genre.CommonValue : null),
                values.AllSame ? values.CommonValue : null);
            if (!values.AllSame)
                combo.PlaceholderText = Loc.Get("MassEdit_Multiple");
            combo.TextSubmitted += (_, _) => Touch(field);
            combo.SelectionChanged += (_, _) => Touch(field);
            if (_genreBox is { } genreBox)
                MassEditDialog.FollowGenre(combo, genreBox, stylesFor);
            control = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            shown = () => combo.Text ?? "";
            write = v => MassEditDialog.SetSuggestValue(combo, v);
        }
        else if (field.Suggestions is { Count: > 0 } suggestions)
        {
            var combo = MassEditDialog.SuggestBox(suggestions, values.AllSame ? values.CommonValue : null);
            if (!values.AllSame)
                combo.PlaceholderText = Loc.Get("MassEdit_Multiple");
            combo.TextSubmitted += (_, _) => Touch(field);
            combo.SelectionChanged += (_, _) => Touch(field);
            if (field.Kind == TagFieldKind.Genre)
                _genreBox = combo;
            control = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            shown = () => combo.Text ?? "";
            write = v => MassEditDialog.SetSuggestValue(combo, v);
        }
        else if (values.AllSame)
        {
            var box = new TextBox { Text = values.CommonValue };
            if (field.Multiline)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.Height = 56;
            }
            if (field.Numeric)
                box.AcceptDigitsOnly();
            box.TextChanged += (_, _) => Touch(field);
            control = box;
            read = () => box.Text;
            shown = read;
            write = v => box.Text = v;
        }
        else
        {
            // The rows disagree. The box starts empty, says so, and offers what each row holds.
            var combo = new ComboBox
            {
                IsEditable = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = Loc.Get("MassEdit_Multiple"),
                // Clear first, then the value each row holds. Emptying the field is an answer in the same way as
                // picking one of the values already there.
                ItemsSource = values.Distinct.Prepend(MassEditDialog.ClearEntry).ToList(),
            };
            combo.TextSubmitted += (_, _) => Touch(field);
            combo.SelectionChanged += (_, _) => Touch(field);
            control = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            shown = () => combo.Text ?? "";
            write = v => combo.Text = v;
        }

        if (TagFields.Resolve(field.Width) is { } width)
            control.Narrow(width);

        // What the box says when it is filled in is the baseline for every later check. An edit is a difference
        // from this baseline, not an event that happened later.
        _entries.Add(new Entry(field, read, shown, write) { Loaded = shown() });
        return (label, control);
    }

    // ── the pictures ────────────────────────────────────────────────────────────────────────────

    /// <summary>What the panel is padded by on each side. The bench's own inset, and the number the
    /// heading, the fields and the footer are all laid out on.</summary>
    private const double Inset = 14;

    /// <summary>
    /// How wide the panel wants to be.
    ///
    /// <para>The width a form of single-word fields needs. A narrower panel would leave a title with no room.
    /// At this width a title and a year sit on one line, and the artist does not wrap. This is public because
    /// the workbench sets the column width, and this is the only place that knows the right value.</para>
    /// </summary>
    public const double NaturalWidth = 312;

    /// <summary>The gap between the two wells.</summary>
    private const double WellGap = 8;

    /// <summary>Room kept for the scrollbar down the side of the fields. The form is taller than the panel as soon as a
    /// record has any data, so the scrollbar is the normal case. Two wells sized to the full width without this
    /// room would fit only until the first time the user scrolled.</summary>
    private const double ScrollAllowance = 12;

    /// <summary>
    /// How big each picture is: half the panel, less the gap between them.
    ///
    /// <para>The size is calculated from the PANEL, not from the form's input column. The pictures are the one
    /// row with nothing to show in the label gutter, so they use the whole width and split it. A size set on the
    /// input column would cut off the disc label at the right-hand edge, with nothing to scroll to.</para>
    /// </summary>
    private const double CoverEdge = (NaturalWidth - (2 * Inset) - WellGap - ScrollAllowance) / 2;

    /// <summary>One picture the panel is holding: the well it is shown in, the bytes behind it, and
    /// whether somebody has actually chosen it. Two of these, and no difference between them beyond
    /// where what they hold ends up.</summary>
    private sealed class Picture
    {
        public PictureWell Well { get; } = new(CoverEdge);
        public byte[]? Bytes;
        public string? Mime;
        public bool Chosen;
    }

    /// <summary>The lookup button of the form now on screen, or null while there is none.</summary>
    private Button? _lookupButton;

    /// <summary>A picture chosen in this panel and not yet applied. It is held here, not in the well, because the well is
    /// rebuilt. See <see cref="SetRows"/>.</summary>
    private sealed record Held(byte[] Bytes, string? Mime);

    private Held? _chosenCover;
    private Held? _chosenDisc;

    /// <summary>Which selection the two above were chosen for. A rebuild of the same ticks keeps them;
    /// a different set of ticks is a different record, and a picture chosen for one must never end up
    /// applied to another.</summary>
    private string _chosenFor = "";

    /// <summary>The ticked set as one comparable value: the store's own row ids, in order.</summary>
    private static string Signature(IReadOnlyList<TrackRow> rows)
        => string.Join(",", rows.Select(r => r.QueueId));

    private Picture? _cover;
    private Picture? _disc;

    /// <summary>The sleeve and the disc label, side by side, each with the button that changes it
    /// underneath. Built together because they are one row of the form.</summary>
    private UIElement BuildArtwork()
    {
        _cover = new Picture();

        // There is no disc well unless the selection is a whole record. See Record.
        _disc = Record is null ? null : new Picture();

        // The picture the rows already agree on, if it was chosen earlier in this session. The comparison is by
        // bytes, not by which array each row holds. See TagPanelValues.AgreedPicture.
        if (TagPanelValues.AgreedPicture([.. _rows.Select(r => r.PendingArt)]) is { Length: > 0 } bytes)
        {
            _cover.Bytes = bytes;
            _cover.Mime = _rows[0].PendingArtMime;
            _ = ShowAsync(_cover, bytes);
        }

        // The disc label the record already has. It cannot be stored on a row, because it belongs to the RECORD
        // and is written into the folder the files go to. So it is held against the record in the store and
        // returned here (see DiscArt). Without this, the well would look empty even when a label was set.
        if (_disc is { } well && Record?.DiscArt is { Length: > 0 } label)
        {
            well.Bytes = label;
            well.Mime = "image/png";
            _ = ShowAsync(well, label);
        }

        // Above both of those is whatever was chosen here and not yet applied. It takes priority over what the
        // record already has, for the same reason the boxes take priority over the rows. It is the most recent
        // choice, and it still waits for Apply. Because it is chosen, Apply still writes it.
        Lay(_cover, _chosenCover);
        Lay(_disc, _chosenDisc);

        static void Lay(Picture? picture, Held? held)
        {
            if (picture is null || held is null)
                return;
            picture.Bytes = held.Bytes;
            picture.Mime = held.Mime;
            picture.Chosen = true;
            _ = ShowAsync(picture, held.Bytes);
        }

        var pane = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = WellGap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        pane.Children.Add(Well(_cover, "Queue_TagPanelCover", RPM.Fingerprint.Catalog.CoverKind.Cover));
        if (_disc is { } disc)
            pane.Children.Add(Well(disc, "Queue_TagPanelDiscLabel", RPM.Fingerprint.Catalog.CoverKind.Disc));
        return pane;
    }

    /// <summary>A picture and the button that changes it: the button as wide as the picture and no
    /// wider, or it reads as belonging to the panel rather than to the well above it.</summary>
    private StackPanel Well(Picture picture, string labelKey, RPM.Fingerprint.Catalog.CoverKind kind)
    {
        var change = new Button
        {
            // Named for the picture it changes, not "Change". With two wells side by side, two buttons with the same
            // word would make the user work out which is which from the pictures above them. An empty well has no
            // picture to read.
            Content = AlbumEditDialog.IconLabel("\uf03e", labelKey),
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
            Width = CoverEdge,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        change.Click += (_, _) => _ = ChooseAsync(picture);

        // This is the other way to fill a well. For a record with the wrong picture, it is the usual way. It
        // searches the catalogues and the web for these words, not a file on this disk. The album editor has the
        // same button under its own two pictures, because it does the same job. See AlbumEditDialog.SearchArtAsync.
        var find = new Button
        {
            Content = AlbumEditDialog.IconLabel("\uf002", "Details_FindArt"),   // Magnifier icon.
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
            Width = CoverEdge,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        find.Click += (_, _) => _ = FindAsync(picture, kind);

        var column = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        column.Children.Add(picture.Well.Root);
        column.Children.Add(change);
        column.Children.Add(find);
        return column;
    }

    /// <summary>
    /// Fills a well from the cover search.
    ///
    /// <para>The search uses what the BOXES say, not what the files say. A user who has just corrected the album
    /// name is looking for the corrected name. That is why they open the search at that moment.</para>
    ///
    /// <para>A sleeve chosen from a source that also has the disc fills the disc well too. This is the one place
    /// where a click changes two pictures, and the disc mark on the tile says so. This happens only where there
    /// is a disc well. A selection that is not a whole record has nowhere to put a disc (see <see cref="Record"/>).</para>
    /// </summary>
    private async Task FindAsync(Picture picture, RPM.Fingerprint.Catalog.CoverKind kind)
    {
        if (FindArt is null)
            return;

        if (await FindArt(kind, Said(TagFieldKind.AlbumArtist) ?? Said(TagFieldKind.Artist),
                          Said(TagFieldKind.Album)) is not { Picture.Length: > 0 } found)
            return;

        picture.Bytes = found.Picture;
        picture.Mime = IsPng(found.Picture) ? "image/png" : "image/jpeg";
        await ShowAsync(picture, found.Picture);
        Took(picture);

        if (kind is not RPM.Fingerprint.Catalog.CoverKind.Disc
            && found.Disc is { Length: > 0 } disc && _disc is { } well)
        {
            well.Bytes = disc;
            well.Mime = "image/png";
            await ShowAsync(well, disc);
            Took(well);
        }

        RefreshButtons();
    }

    /// <summary>What a box holds right now, or null when it is empty. It is read from the live control, not
    /// from the rows, for the reason in <see cref="FindAsync"/>.</summary>
    private string? Said(TagFieldKind kind)
        => _entries.FirstOrDefault(e => e.Field.Kind == kind)?.Read().Trim() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>The PNG signature. It is more reliable than the file name the source gave, because web sources often give
    /// wrong names.</summary>
    private static bool IsPng(byte[] bytes) => bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..];

    private async Task ChooseAsync(Picture picture)
    {
        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png");
            if (await picker.PickSingleFileAsync() is not { } file)
                return;

            picture.Bytes = File.ReadAllBytes(file.Path);
            picture.Mime = file.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            await ShowAsync(picture, picture.Bytes);
            Took(picture);
            RefreshButtons();
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("tag-panel", "picking a picture", ex);
        }
    }

    /// <summary>Puts a chosen picture in its well. It uses MemoryStream with AsRandomAccessStream, which works under
    /// Uno-Skia. See MassEditDialog.ShowThumbAsync for why the obvious approach does not.</summary>
    private static async Task ShowAsync(Picture picture, byte[] bytes)
    {
        if (bytes.Length == 0)
            return;
        var preview = await Task.Run(() => ArtScaler.Downscale(bytes, ArtScaler.TileEdge));
        var stream = Presentation.Common.BitmapBytes.Stream(preview);
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        await bmp.SetSourceAsync(stream);
        picture.Well.Show(bmp);
    }

    // ── the release lookup ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How to ask the catalogues about the record these rows are from. Set by the workbench, which is
    /// the only thing here that can open a window.
    /// </summary>
    public Func<IReadOnlyList<TrackRow>, ArtAsk, Task<MassEditLookup?>>? Lookup { get; set; }

    /// <summary>
    /// How to search for a picture: the cover search, over the words the form is showing. The workbench sets
    /// this, for the same reason as <see cref="Lookup"/>: the panel cannot open a window.
    /// </summary>
    public Func<RPM.Fingerprint.Catalog.CoverKind, string?, string?, Task<FoundArt?>>? FindArt { get; set; }

    /// <summary>
    /// The record these rows are the WHOLE of, or null when they are not one. The workbench sets it beside the
    /// rows, for the same reason as <see cref="Lookup"/>: the panel holds rows, and this is a fact about the
    /// record they are from.
    ///
    /// <para>It decides whether there is a disc well at all. A disc label belongs to the record and is written
    /// once into the folder its files go to. So a selection that is not a whole record has nothing to store it
    /// on. See <see cref="QueueRecordSelection"/>. When it is null the well is not built. It is not built and
    /// disabled, because a picker that cannot be used should not be offered.</para>
    ///
    /// <para>The label the record already has is shown, but it is never applied again. It is already held
    /// against the record, so the well fills without the picture counting as chosen, and Apply stays as it was.</para>
    /// </summary>
    public QueueRecordSelection? Record { get; set; }

    /// <summary>
    /// Fills the form from a release the user picks: the record's own fields, its sleeve and its disc
    /// label.
    ///
    /// <para>Values go into the BOXES, not onto the rows. What a catalogue says is only a suggestion until the
    /// user has checked it, and checking is what this panel is for. So a lookup leaves the form changed, and
    /// Apply is pressed as usual.</para>
    /// </summary>
    private async void LookUpRelease_Click(object sender, RoutedEventArgs e)
    {
        if (Lookup is null || _rows.Count == 0)
            return;

        // The search runs over two rate-limited catalogues, then fetches a full-size sleeve. It takes long enough
        // that a second press would start the whole job again.
        if (_lookupButton is { } button) button.IsEnabled = false;
        try
        {
            // The pictures the wells already show decide where the release window's overwrite question starts.
            // See ArtAsk. The disc counts the same as the sleeve, because this window fills both.
            var ask = _cover is { Bytes.Length: > 0 } || _disc is { Bytes.Length: > 0 }
                ? ArtAsk.Held
                : ArtAsk.Missing;

            if (await Lookup(_rows, ask) is not { } found)
                return;

            foreach (var entry in _entries)
            {
                if (found.Fields.TryGetValue(entry.Field.Kind, out var value))
                    entry.Write(value);
            }

            if (found.Art is { Embedded.Length: > 0 } art && _cover is { } cover)
            {
                cover.Bytes = art.Embedded;
                cover.Mime = art.Mime;
                await ShowAsync(cover, art.Embedded);
                Took(cover);
            }

            if (found.DiscArt is { Length: > 0 } discBytes && _disc is { } disc)
            {
                disc.Bytes = discBytes;
                disc.Mime = "image/png";
                await ShowAsync(disc, discBytes);
                Took(disc);
            }
        }
        catch (Exception ex)
        {
            // If a catalogue does not answer, the form is left as it was. The panel says so, because a button that
            // silently does nothing is the hardest kind of broken to find.
            RPM.Core.Diagnostics.Faults.Note("tag-panel", "release lookup", ex);
        }
        finally
        {
            if (_lookupButton is { } again) again.IsEnabled = true;
            RefreshButtons();
        }
    }

    /// <summary>
    /// A picture has been chosen. The well shows it, and it is also stored here, so a rebuild of the form cannot
    /// lose it. Every way of choosing a picture goes through this: a file on the disk, the cover search, or a
    /// release. A well that is marked as chosen but not stored here would keep the picture only until the next
    /// rebuild.
    /// </summary>
    private void Took(Picture picture)
    {
        picture.Chosen = true;
        if (picture.Bytes is not { Length: > 0 } bytes)
            return;

        var held = new Held(bytes, picture.Mime);
        if (ReferenceEquals(picture, _cover))
            _chosenCover = held;
        else if (ReferenceEquals(picture, _disc))
            _chosenDisc = held;
    }

    /// <summary>Whether either picture has been chosen since the last Apply. This counts as an edit, like any other.</summary>
    private bool PictureChosen => _cover is { Chosen: true } || _disc is { Chosen: true };

    /// <summary>A box raised a change. Whether that is an EDIT is for the box to answer. See
    /// <see cref="Entry.Touched"/>. So this only asks the boxes again.</summary>
    private void Touch(TagField field)
    {
        _ = field;
        RefreshButtons();
    }

    /// <summary>
    /// Whether Apply would write something: a box shows a value other than the one it was filled with, or a
    /// picture has been chosen since the last press.
    ///
    /// <para>This is the one check for that question. The buttons use it, and so does the import. The import must
    /// not move files into the library while an edit for those same rows is still in the boxes. See
    /// <see cref="ImportWorkbenchView"/>'s Import button.</para>
    /// </summary>
    public bool HasPendingEdits
        => _rows.Count > 0 && (_entries.Any(entry => entry.Touched) || PictureChosen);

    /// <summary>Presses Apply from somewhere other than the button. Does nothing when there is nothing
    /// to write, so the caller does not have to ask twice.</summary>
    public void ApplyPendingEdits() => ApplyEdits();

    /// <summary>Throws the pending edits away, the same as the Revert button.</summary>
    public void DiscardPendingEdits()
    {
        _chosenCover = _chosenDisc = null;
        Build();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyEdits();

    private void ApplyEdits()
    {
        var touched = _entries.Where(entry => entry.Touched).ToList();
        if ((touched.Count == 0 && !PictureChosen) || _rows.Count == 0)
            return;

        foreach (var entry in touched)
        {
            var value = entry.Read();
            foreach (var row in _rows)
                entry.Field.Set(row, value);
        }

        // The sleeve is a field like any other. It is written only when one was chosen. It is stored on the rows,
        // which is where a cover lives until the file is written.
        byte[]? coverArt = null;
        if (_cover is { Chosen: true, Bytes: { Length: > 0 } bytes } cover)
        {
            coverArt = bytes;
            foreach (var row in _rows)
                row.SetPendingArt(bytes, cover.Mime);
        }

        // The boxes now show what was written, so later checks compare against that. Nothing counts as an edit
        // until the user makes a new change.
        foreach (var entry in _entries)
            entry.Loaded = entry.Shown();

        // The disc label cannot be stored on a row. It belongs to the RECORD and is written into the folder the
        // files go to, and that folder does not exist yet. So it is passed on with the rows, and the workbench
        // holds it against the record until import (see MainModel.SetQueueAlbumIntent).
        var discArt = _disc is { Chosen: true, Bytes: { Length: > 0 } label } ? label : null;

        if (_cover is { } chosenCover)
            chosenCover.Chosen = false;
        if (_disc is { } chosenDisc)
            chosenDisc.Chosen = false;

        // These are now written, so they no longer wait for Apply. The cover is stored on the rows, and the disc
        // label is held against the record. Both come back from the store on the next build.
        _chosenCover = _chosenDisc = null;

        RefreshButtons();
        Applied?.Invoke(new TagPanelEdit(_rows, discArt, coverArt));
    }

    /// <summary>Throws the pending edits away by reading the rows again. Nothing was written, so there
    /// is nothing to undo: the boxes simply go back to saying what the rows say.</summary>
    private void Revert_Click(object sender, RoutedEventArgs e) => DiscardPendingEdits();

    private void RefreshButtons()
    {
        var touched = HasPendingEdits;
        ApplyButton.IsEnabled = touched;
        RevertButton.IsEnabled = touched;
        ApplyButton.Content = _rows.Count == 1
            ? Loc.Get("Queue_TagPanelApplyOne")
            : Loc.Format("Queue_TagPanelApply", _rows.Count);
    }
}
