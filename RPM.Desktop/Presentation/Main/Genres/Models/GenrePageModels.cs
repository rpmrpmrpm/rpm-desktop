using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Library;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Genres.Models;

/// <summary>
/// One generated playlist on a genre's page: an essential list or a mood, narrowed to this genre.
///
/// <para><b>The artist page's playlist card, with a different identity.</b> That card holds the node of a
/// playlist that a user made. A generated list has no node, because nothing is stored for it. The list is a
/// query that runs again each time it is opened (see <see cref="SmartPlaylistId"/>). So the two are
/// separate types, not one type with two nullable ids. A card that could be either would need a check for
/// which it is on every surface that draws one.</para>
///
/// <para>Every card is the same square on purpose. The card sits on a shelf between shelves of records, and
/// a card of another size would look like a mistake.</para>
/// </summary>
[RpmBindable]
public sealed partial class GenreList : INotifyPropertyChanged, RPM.Desktop.Services.Art.ISleeveCard
{
    public GenreList(SmartPlaylistId id, string name, string subtitle,
                     Library.Models.AlbumTileMetrics metrics)
    {
        Id = id;
        Name = name;
        Subtitle = subtitle;
        Metrics = metrics;
    }

    /// <summary>Which generated list this is: the kind and the genre together. Those two values identify
    /// the list.</summary>
    public SmartPlaylistId Id { get; }

    public string Name { get; }

    /// <summary>How many songs are in it. The list is read again when it is opened, so this number is what
    /// it held when the page was drawn. It is not a promise about what it will hold.</summary>
    public string Subtitle { get; }

    private bool _coverMissing;

    /// <summary>The loader's result: no track on this list has artwork, so no cover is coming. The card is
    /// then drawn from <see cref="FigureSeed"/>.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            OnChanged(nameof(FigureSeed));
        }
    }

    /// <summary>The text the made figure is drawn from when there is no picture: the list's own name and
    /// its genre. So "Slow Burn" under rock and "Slow Burn" under jazz give two different squares.
    ///
    /// <para>It is null until the lookup has found no cover. Every card in the app follows this rule for
    /// its made sleeve (see <see cref="Artists.Models.ArtistRecord.FigureSeed"/>). A figure drawn before
    /// the lookup ends would show for a moment and then be covered.</para></summary>
    public string? FigureSeed => _coverMissing ? $"{Name}␟{Id.Genre}" : null;

    /// <summary>The shelf's one size measurement, shared by every sleeve on the page.</summary>
    public Library.Models.AlbumTileMetrics Metrics { get; }

    private ImageSource? _cover;

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover = value;
            OnChanged();
            OnChanged(nameof(HasCover));
            OnChanged(nameof(BadgeShown));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>Whether the list mark is drawn in the corner of the picture. The mark shows that this
    /// square is a list, not a record.
    ///
    /// <para>The mark is hidden once a framed cover has loaded. Every generated list's cover now carries
    /// the app's own drawing: the border, the list name down the band, and the app's name in the same
    /// corner. The badge would sit on top of the app's logo. See <c>CoverOverlay</c>, which also reports
    /// whether that drawing is there. If it is not, the badge is shown.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Visibility BadgeShown =>
        _cover is not null && Common.CoverOverlay.Has()
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One genre next to another, shown in the card under the article: the name, and how much of it this
/// collection holds.
///
/// <para><b>The count is what makes the card useful.</b> <see cref="RPM.Core.Metadata.Genres.Related"/>
/// answers a question about music in general. Punk is a kind of Rock, whatever anyone owns. A card that
/// only said that would repeat what the reader is already looking at. With the count, the card says
/// something about this collection. Rock's card says that the collection has ninety punk records and no
/// noise. Both facts are useful.</para>
///
/// <para>A genre the collection holds nothing of is not built at all (see MainPage.Genres), so the count
/// here is always at least one.</para>
/// </summary>
/// <param name="Name">The genre, as the app spells it.</param>
/// <param name="Albums">How many of its records the collection holds.</param>
/// <param name="Count">That number as a sentence. The page builds it, not this record, because a locale
/// decides whether one album reads "1 album".</param>
public sealed record GenreRelation(string Name, int Albums, string Count);

/// <summary>
/// One decade of a genre, shown in the Era row: the label above it and the records made in it.
///
/// <para><b>The row shows records, not a generated list.</b> A generated list cannot be cut by genre and
/// decade at the same time. <see cref="SmartPlaylistId"/> says so. Four moods, thirty genres and six
/// decades would give 720 lists, which is a filing system rather than a shelf. So the era row shows the
/// records themselves, oldest decade first. The collection can always provide them.</para>
/// </summary>
/// <param name="Decade">The first year of the decade, for example 1970 or 1980. The label is built from
/// it.</param>
/// <param name="Label">The decade as it is written over the shelf.</param>
/// <param name="Folders">The album folders made in the decade, newest first.</param>
public sealed record GenreEra(int Decade, string Label, IReadOnlyList<string> Folders);

/// <summary>
/// Everything one genre's shelves hold, built in one pass from the library.
///
/// <para><b>One object, not six loaders.</b> The artist page gives its view one delegate per question,
/// because its shelves come from six different sources: a catalogue, a listen log and a folder scan. All of
/// a genre's shelves come from the same two reads of the same collection. Splitting them into six loaders
/// would make six passes over the collection. Two shelves could then disagree about which records belong to
/// the genre.</para>
///
/// <para>The page that owns the library builds this object (see MainPage.Genres) and hands it over whole.
/// The view draws it and decides nothing.</para>
/// </summary>
/// <param name="Added">The genre's newest records, newest first.</param>
/// <param name="Acts">The acts with the most records in the genre, biggest first. These are the same boxes
/// as on the artists wall, so a press opens the same page.</param>
/// <param name="Popular">The songs of the genre that are played most, one per record. At most twenty-four,
/// drawn as three columns of eight. See <c>MainPage.PopularSongs</c> and
/// <c>LibraryDatabase.QueryPopularUnder</c>.</param>
/// <param name="Essential">The lists the library builds for this genre from the user's own
/// activity.</param>
/// <param name="Moods">The mood lists, cut from how the music sounds.</param>
/// <param name="Eras">Its records by decade, oldest first.</param>
public sealed record GenreShelf(
    List<Artists.Models.ArtistRecord> Added,
    List<Artists.Models.ArtistTile> Acts,
    IReadOnlyList<Artists.Models.ArtistSong> Popular,
    List<GenreList> Essential,
    List<GenreList> Moods,
    IReadOnlyList<GenreEraShelf> Eras)
{
    /// <summary>A genre with nothing on any of its shelves. The page shows this while the library is
    /// closed, and goes back to it when the user leaves. It is a value, not null, so no surface needs a
    /// second path for "not loaded".</summary>
    public static readonly GenreShelf Empty = new([], [], [], [], [], []);
}

/// <summary>One decade's records, drawn as a small shelf under the Era heading.</summary>
/// <param name="Label">The decade as it is written, for example "1970s".</param>
/// <param name="Records">What the collection holds of this genre from that decade, newest first.</param>
public sealed record GenreEraShelf(string Label, List<Artists.Models.ArtistRecord> Records);
