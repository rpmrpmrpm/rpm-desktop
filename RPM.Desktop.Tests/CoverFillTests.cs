using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Presentation.Main.Genres.Models;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// How a shelf that is not the wall dresses a record. It also decides what happens when there is no picture
/// to put up.
///
/// <para>A made sleeve costs a Skia paint and a blur per card. If the seed were given as soon as a card is
/// realized, every record on the page would pay that cost, including records whose own artwork covers them
/// a moment later. So the seed depends on a verdict: the record has been looked for everywhere and has no
/// cover. <see cref="CoverFill"/> is the only code that may give that verdict.</para>
/// </summary>
public class CoverFillTests
{
    /// <summary>A card on a shelf, with nothing but the state the fill touches.</summary>
    private sealed class Card : ISleeveCard
    {
        public ImageSource? Cover { get; set; }
        public bool CoverMissing { get; set; }
        public bool HasCover => Cover is not null;
    }

    /// <summary>A picture for the test. It is an ImageSource that is never really constructed, because
    /// creating one needs a dispatcher and a headless test has none. Nothing here asks what it looks like.
    /// <see cref="CoverStreamJobTests"/> uses the same trick, for the same reason.</summary>
    private static ImageSource APicture => (ImageSource)System.Runtime.CompilerServices.RuntimeHelpers
        .GetUninitializedObject(typeof(Microsoft.UI.Xaml.Media.Imaging.BitmapImage));

    private static Task DressAsync(
        Card card, byte[]? bytes, CancellationToken ct = default, ImageSource? art = null)
        => CoverFill.DressAsync(card, _ => Task.FromResult(bytes),
                                _ => Task.FromResult<ImageSource?>(art ?? APicture), ct);

    [Fact]
    public async Task A_picture_that_came_back_goes_up()
    {
        var card = new Card();
        await DressAsync(card, [1, 2, 3]);

        Assert.True(card.HasCover);
        Assert.False(card.CoverMissing);
    }

    /// <summary>This is the main purpose of the method. If nothing comes back, the card may draw its made
    /// sleeve. Until this verdict is given, the card draws nothing at all.</summary>
    [Fact]
    public async Task Nothing_coming_back_is_the_verdict()
    {
        var card = new Card();
        await DressAsync(card, null);

        Assert.True(card.CoverMissing);
        Assert.False(card.HasCover);
    }

    [Fact]
    public async Task An_empty_answer_counts_as_nothing()
    {
        var card = new Card();
        await DressAsync(card, []);

        Assert.True(card.CoverMissing);
    }

    /// <summary>A card that already shows a picture has artwork. So a read that returns nothing means the
    /// read failed, not that the art is missing. A read can fail because of the network, or a file being
    /// written. If the record were latched as having no art, it would wear a made sleeve over its own
    /// cover. See <see cref="CoverTier.Latches"/>.</summary>
    [Fact]
    public async Task A_record_already_wearing_a_picture_is_not_struck_off()
    {
        var card = new Card { Cover = APicture };
        await DressAsync(card, null);

        Assert.False(card.CoverMissing);
    }

    /// <summary>A cancelled read is not a verdict. The page may have been left, or the shelf rebuilt
    /// underneath. If it were believed, the record would show a made sleeve for the rest of the session,
    /// over artwork that nobody asks for again.</summary>
    [Fact]
    public async Task A_cancelled_read_says_nothing_either_way()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var card = new Card();
        await DressAsync(card, null, cts.Token);

        Assert.False(card.CoverMissing);
        Assert.False(card.HasCover);
    }

    [Fact]
    public async Task A_cancelled_read_does_not_put_a_picture_up_either()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var card = new Card();
        await DressAsync(card, [1, 2, 3], cts.Token);

        Assert.False(card.HasCover);
    }

    /// <summary>The genre page's case: one record stands on the newest shelf and in its decade, and only
    /// one of the two cards made the request.</summary>
    [Fact]
    public async Task Every_card_standing_for_the_record_is_dressed()
    {
        var cards = new List<Card> { new(), new() };
        await CoverFill.DressAsync(() => cards, _ => Task.FromResult<byte[]?>([1, 2, 3]),
                                   _ => Task.FromResult<ImageSource?>(APicture), CancellationToken.None);

        Assert.All(cards, c => Assert.True(c.HasCover));
    }

    [Fact]
    public async Task Every_card_standing_for_the_record_gets_the_verdict()
    {
        var cards = new List<Card> { new(), new() };
        await CoverFill.DressAsync(() => cards, _ => Task.FromResult<byte[]?>(null),
                                   _ => Task.FromResult<ImageSource?>(APicture), CancellationToken.None);

        Assert.All(cards, c => Assert.True(c.CoverMissing));
    }

    /// <summary>The cards are looked up after the read finishes, not before it starts. A shelf is rebuilt
    /// on every library change. A list taken before the await could hold cards that are no longer on the
    /// page.</summary>
    [Fact]
    public async Task The_cards_are_found_after_the_read_not_before()
    {
        var cards = new List<Card>();

        await CoverFill.DressAsync(
            () => cards,
            _ =>
            {
                cards.Add(new Card());   // the shelf filled while the read was out
                return Task.FromResult<byte[]?>([1, 2, 3]);
            },
            _ => Task.FromResult<ImageSource?>(APicture),
            CancellationToken.None);

        Assert.Single(cards);
        Assert.True(cards[0].HasCover);
    }

    /// <summary>A decode that returns nothing leaves the card as it was. It is not a verdict either. The
    /// bytes exist, so the record has artwork, and a made sleeve over it would be wrong.</summary>
    [Fact]
    public async Task A_decode_that_failed_leaves_the_card_alone()
    {
        var card = new Card();
        await CoverFill.DressAsync(card, _ => Task.FromResult<byte[]?>([1, 2, 3]),
                                   _ => Task.FromResult<ImageSource?>(null), CancellationToken.None);

        Assert.False(card.HasCover);
        Assert.False(card.CoverMissing);
    }
}

/// <summary>
/// The same rule applies to each shelf that draws a record but is not the wall: an artist's page, a
/// genre's, and the row of suggestions. Each card type answers the seed itself, so each one can go wrong on
/// its own. A figure is then drawn for every card on every page that is opened.
/// </summary>
public class ShelfSleeveSeedTests
{
    private static ArtistRecord Record()
        => new("Talking Heads/Remain in Light", "Remain in Light", "1980", false, new AlbumTileMetrics());

    private static ArtistPlaylist Playlist()
        => new(7, "Late nights", "12 songs", new AlbumTileMetrics());

    private static GenreList List()
        => new(new SmartPlaylistId(SmartPlaylistKind.MostPlayed, "Rock", 0), "Most played", "40 songs",
               new AlbumTileMetrics());

    [Fact]
    public void A_record_nobody_has_read_yet_is_drawn_nothing()
        => Assert.Null(Record().FigureSeed);

    [Fact]
    public void A_record_the_read_came_back_empty_for_is_drawn_from_its_folder()
    {
        var record = Record();
        record.CoverMissing = true;
        Assert.Equal("Talking Heads/Remain in Light", record.FigureSeed);
    }

    /// <summary>Artwork arriving takes the made sleeve down with it, wherever the record is met.</summary>
    [Fact]
    public void Artwork_arriving_takes_the_made_sleeve_away_again()
    {
        var record = Record();
        record.CoverMissing = true;
        record.CoverMissing = false;
        Assert.Null(record.FigureSeed);
    }

    [Fact]
    public void The_verdict_tells_the_surface_the_seed_has_changed()
    {
        var record = Record();
        var said = new List<string?>();
        record.PropertyChanged += (_, e) => said.Add(e.PropertyName);

        record.CoverMissing = true;

        Assert.Contains(nameof(ArtistRecord.FigureSeed), said);
    }

    /// <summary>A playlist is drawn from its NAME, not from any one record on it. Two lists that share a
    /// song are two pictures. It is drawn at the same moment as a record: once the cover has been looked
    /// for and there is none.</summary>
    [Fact]
    public void A_playlist_nobody_has_read_yet_is_drawn_nothing()
        => Assert.Null(Playlist().FigureSeed);

    [Fact]
    public void A_playlist_with_no_artwork_on_it_is_drawn_from_its_name()
    {
        var playlist = Playlist();
        playlist.CoverMissing = true;
        Assert.Equal("Late nights", playlist.FigureSeed);
    }

    [Fact]
    public void A_generated_list_nobody_has_read_yet_is_drawn_nothing()
        => Assert.Null(List().FigureSeed);

    /// <summary>The list's name AND its genre are both used. "Most played" under rock and "Most played"
    /// under jazz are two different squares.</summary>
    [Fact]
    public void A_generated_list_with_no_artwork_is_drawn_from_its_name_and_genre()
    {
        var list = List();
        list.CoverMissing = true;

        Assert.NotNull(list.FigureSeed);
        Assert.Contains("Most played", list.FigureSeed);
        Assert.Contains("Rock", list.FigureSeed);
    }

    [Fact]
    public void A_suggestion_nobody_has_read_yet_is_drawn_nothing()
        => Assert.Null(new SimilarRow(
            new RPM.Core.Metadata.SimilarAlbum("On Air", "Alan Parsons", "Alan Parsons/On Air",
                                               RPM.Core.Metadata.SimilarBecause.SameGenre),
            new SimilarTileMetrics()).FigureSeed);
}
