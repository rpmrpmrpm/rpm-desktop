using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// The order a wall of covers reads its pictures in.
///
/// <para>Both walls run this one function, on three workers. The collection can be scrolled, zoomed,
/// filtered and swapped while they run. The function must never spend work on a record nobody can see while
/// a tile on the screen is still empty. It must never offer the same record to two workers. It must also
/// never offer a record again at the size where its read already failed.</para>
/// </summary>
public class CoverStreamJobTests
{
    /// <summary>A record on a wall, with nothing but the state the loader reads.</summary>
    private sealed class Card : ICoverCard
    {
        public Card(string folder) => Folder = folder;
        public string Folder { get; }
        public ImageSource? Cover { get; set; }
        public ImageSource? Sharp { get; set; }
        public ImageSource? Tiny { get; set; }
        public byte[]? ResidentBytes { get; set; }
        public Windows.UI.Color? Tint { get; set; }
        public bool CoverMissing { get; set; }
        public int MissedEdge { get; set; }
        public int CoverEdge { get; set; }
        public override string ToString() => Folder;
    }

    /// <summary>A picture for the test. It is an ImageSource that is never really constructed, because
    /// creating one needs a dispatcher and a headless test has none. The loader only asks whether a record
    /// is wearing a picture, never what the picture looks like.</summary>
    private static ImageSource APicture => (ImageSource)System.Runtime.CompilerServices.RuntimeHelpers
        .GetUninitializedObject(typeof(Microsoft.UI.Xaml.Media.Imaging.BitmapImage));

    /// <summary>A record already wearing a picture at the size the wall is asking for: nothing to
    /// do.</summary>
    private static Card Dressed(string folder, int edge = Wanted) => new(folder)
    {
        Cover = APicture,
        Tiny = APicture,
        ResidentBytes = [1, 2, 3],
        CoverEdge = edge,
    };

    private const int Wanted = 384;
    private const int Resident = 128;

    private static CoverStream.Job? Pick(
        IReadOnlyList<Card> onScreen,
        IReadOnlyList<Card>? collection = null,
        Func<ICoverCard, bool>? busy = null,
        Func<ICoverCard, bool>? errandWanted = null,
        bool sweeping = false,
        bool holdsMoreDecoded = false)
        => CoverStream.Pick(onScreen, collection ?? onScreen, Wanted, Resident,
                            busy ?? (_ => false), errandWanted, sweeping, holdsMoreDecoded);

    /// <summary>An empty tile is a record the listener cannot see. A cover one step off the wanted size is
    /// a record they can see, so blank tiles are read first. A window resize puts every tile one step out
    /// at once. Without this rule, those resized tiles could be read before the blank ones that a scroll
    /// has just uncovered.</summary>
    [Fact]
    public void A_blank_tile_is_read_before_one_that_is_merely_the_wrong_size()
    {
        var wrongSize = Dressed("a", edge: Resident);
        var blank = new Card("b");

        Assert.Equal(blank, Pick([wrongSize, blank])?.Card);
    }

    [Fact]
    public void A_tile_wearing_the_wrong_size_is_read_next()
    {
        var wrongSize = Dressed("a", edge: Resident);

        var job = Pick([Dressed("dressed"), wrongSize]);

        Assert.Equal(wrongSize, job?.Card);
        Assert.Equal(Wanted, job?.Edge);
    }

    /// <summary>A mark that arrives a moment late costs nothing. A picture that arrives late means the wall
    /// is not working.</summary>
    [Fact]
    public void The_walls_errand_comes_after_every_picture_on_screen()
    {
        var blank = new Card("blank");

        Assert.Equal(blank, Pick([blank], errandWanted: _ => true)?.Card);
        Assert.Equal(CoverStream.JobKind.Errand,
                     Pick([Dressed("a")], errandWanted: _ => true)?.Kind);
    }

    /// <summary>The collection is the whole library. It comes last, so it only runs in the gaps.</summary>
    [Fact]
    public void The_collection_is_read_behind_everything_on_screen()
    {
        var unread = new Card("far away");
        var job = Pick([Dressed("on screen")], collection: [Dressed("on screen"), unread]);

        Assert.Equal(unread, job?.Card);
        Assert.Equal(Resident, job?.Edge);
    }

    /// <summary>While the first pass is using the collection, offering the same records here would make the
    /// two race for them.</summary>
    [Fact]
    public void Nothing_of_the_collection_is_offered_while_the_first_pass_is_running()
        => Assert.Null(Pick([Dressed("on screen")], collection: [new Card("far away")], sweeping: true));

    [Fact]
    public void A_record_a_worker_is_already_reading_is_never_offered_twice()
    {
        var blank = new Card("blank");
        Assert.Null(Pick([blank], busy: card => ReferenceEquals(card, blank)));
    }

    /// <summary>A failed read is remembered together with the size it failed at. Without this, the loader
    /// would offer the same record again on every pass. That would spin on the thread that draws.</summary>
    [Fact]
    public void A_read_that_failed_at_this_size_is_not_asked_again_until_the_zoom_moves()
    {
        // The collection is empty here, so this is only about the tile on screen. The record is still worth
        // asking about at other sizes, which the next test checks.
        var missed = new Card("missed") { MissedEdge = Wanted };
        Assert.Null(Pick([missed], collection: []));

        missed.MissedEdge = 512;
        Assert.Equal(missed, Pick([missed], collection: [])?.Card);
    }

    /// <summary>…and it is still worth asking about at a size where it has not failed. A failed read does
    /// not mean the record has no art. See <see cref="CoverTier.Latches"/>. So the small picture the
    /// collection pass wants is a different question from the big one the screen wanted.</summary>
    [Fact]
    public void A_read_that_failed_at_one_size_is_still_asked_for_at_another()
    {
        var missed = new Card("missed") { MissedEdge = Wanted };

        var job = Pick([missed], collection: [missed]);

        Assert.Equal(missed, job?.Card);
        Assert.Equal(Resident, job?.Edge);
    }

    [Fact]
    public void A_record_with_no_art_anywhere_is_never_offered()
        => Assert.Null(Pick([new Card("no art") { CoverMissing = true }]));

    /// <summary>Pixels for a record that nobody has reached yet come last. They are only read for a wall
    /// that keeps a tier for its whole collection.</summary>
    [Fact]
    public void Pixels_ahead_of_the_screen_come_last_and_only_where_the_wall_holds_them()
    {
        var held = new Card("held") { ResidentBytes = [1], CoverEdge = Resident };

        Assert.Null(Pick([Dressed("on screen")], collection: [held], holdsMoreDecoded: false));

        var job = Pick([Dressed("on screen")], collection: [held], holdsMoreDecoded: true);
        Assert.Equal(CoverStream.JobKind.Pixels, job?.Kind);
        Assert.Equal(Resident, job?.Edge);
    }

    [Fact]
    public void A_wall_that_is_fully_dressed_asks_for_nothing()
        => Assert.Null(Pick([Dressed("a"), Dressed("b")], holdsMoreDecoded: true));
}
