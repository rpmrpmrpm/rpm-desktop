using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The banner at the top of the home screen: which cards it holds and in which order.
///
/// <para>Tested for the same reason as the rows: the banner is computed from the same lists the rows
/// use, and a strip that stopped mixing them would still look like a normal strip.</para>
/// </summary>
public class HomeFeaturesTests
{
    private static readonly string[] Continued = ["heard1"];
    private static readonly string[] Added = ["added1", "added2", "added3"];
    private static readonly string[] Lists = ["Favourites", "On repeat"];
    private static readonly string[] Suggested = ["sug1", "sug2"];
    private static readonly string[] Played = ["played1", "played2"];
    private static readonly string[] Listened = ["heard1", "heard2"];
    private static readonly string[] Stations = ["station1", "station2"];

    private static readonly Func<string, bool> NoneVarious = _ => false;

    [Fact]
    public void One_card_per_question_before_a_second_from_any()
    {
        var strip = HomeFeatures.Strip(
            Continued, Added, Lists, Suggested, Played, Listened, Stations, NoneVarious, HomeFeatures.Count);

        Assert.Equal(
            [
                HomeFeatureKind.Continue,
                HomeFeatureKind.Added,
                HomeFeatureKind.MadeForYou,
                HomeFeatureKind.Suggested,
                HomeFeatureKind.Played,
                HomeFeatureKind.Listened,
                HomeFeatureKind.Radio,
            ],
            strip.Select(f => f.Kind));

        // "heard1" is the album that played last, so it is the Continue card. The recent listens
        // would also offer it, so they move on to their second item.
        Assert.Equal(
            ["heard1", "added1", "Favourites", "sug1", "played1", "heard2", "station1"],
            strip.Select(f => f.Key));
    }

    /// <summary>The album that played last always goes to the Continue card, even when other cards
    /// could also use it. If this card were filled later it would never have an item, because the
    /// recent listens always hold the same album.</summary>
    [Fact]
    public void The_record_that_was_on_last_goes_to_the_continue_card()
    {
        var strip = HomeFeatures.Strip(
            continued: ["last"], added: ["last"], lists: [], suggested: ["last"],
            played: ["last"], listened: ["last"], stations: [], isVariousArtists: NoneVarious, count: HomeFeatures.Count);

        Assert.Equal([(HomeFeatureKind.Continue, "last")], strip.Select(f => (f.Kind, f.Key)));
    }

    /// <summary>A collection where nothing has been played has no last album, so there is no Continue
    /// card. Every other card kind with no item is left out in the same way.</summary>
    [Fact]
    public void A_shelf_with_nothing_played_has_no_continue_card()
    {
        var strip = HomeFeatures.Strip([], Added, [], [], [], [], [], NoneVarious, HomeFeatures.Count);

        Assert.DoesNotContain(HomeFeatureKind.Continue, strip.Select(f => f.Kind));
        Assert.Equal(["added1", "added2", "added3"], strip.Select(f => f.Key));
    }

    /// <summary>A new collection has nothing played, so six of the seven card kinds have no item. The
    /// banner shows the newest albums rather than one card and six empty places.</summary>
    [Fact]
    public void A_question_with_no_answer_is_skipped_and_the_rest_fill_the_strip()
    {
        var strip = HomeFeatures.Strip([], Added, [], [], [], [], [], NoneVarious, HomeFeatures.Count);

        Assert.Equal(["added1", "added2", "added3"], strip.Select(f => f.Key));
        Assert.All(strip, f => Assert.Equal(HomeFeatureKind.Added, f.Kind));
    }

    /// <summary>The most played album is usually also a recent listen. Shown twice, the banner looks
    /// stuck, so the second card kind moves on to its next item.</summary>
    [Fact]
    public void A_record_is_featured_once()
    {
        var strip = HomeFeatures.Strip(
            continued: [], added: ["shared"], lists: [], suggested: [], played: ["shared", "played2"],
            listened: ["shared", "heard2"], stations: [], isVariousArtists: NoneVarious, count: HomeFeatures.Count);

        Assert.Equal(["shared", "played2", "heard2"], strip.Select(f => f.Key));
    }

    /// <summary>A list and a folder with the same name are two different things, and showing the list
    /// must not remove the album.</summary>
    [Fact]
    public void A_list_and_a_record_may_share_a_name()
    {
        var strip = HomeFeatures.Strip(
            continued: [], added: ["Favourites"], lists: ["Favourites"], suggested: [], played: [],
            listened: [], stations: [], isVariousArtists: NoneVarious, count: HomeFeatures.Count);

        Assert.Equal(
            [(HomeFeatureKind.Added, "Favourites"), (HomeFeatureKind.MadeForYou, "Favourites")],
            strip.Select(f => (f.Kind, f.Key)));
    }

    /// <summary>
    /// The radio card is added last, so every card kind that had a card still has one. The station
    /// takes the strip's seventh slot, which would otherwise hold a SECOND item from a card kind that
    /// is already on the strip.
    /// </summary>
    /// <remarks>
    /// The station does replace something: at seven cards the strip has started its second round, so
    /// the last slot is never empty. The trade is a second item from a kind already on the strip for
    /// a station, which the shelf cannot offer.
    /// </remarks>
    [Fact]
    public void A_station_takes_the_slot_a_second_record_would_have_had()
    {
        var without = HomeFeatures.Strip(
            Continued, Added, Lists, Suggested, Played, Listened, [], NoneVarious, HomeFeatures.Count);

        var with = HomeFeatures.Strip(
            Continued, Added, Lists, Suggested, Played, Listened, Stations, NoneVarious, HomeFeatures.Count);

        // One card per kind, in the same order, unchanged.
        Assert.Equal(
            without.Take(HomeFeatures.Count - 1).Select(f => (f.Kind, f.Key)),
            with.Take(HomeFeatures.Count - 1).Select(f => (f.Kind, f.Key)));

        // The last slot: without a station it holds the second newest album.
        Assert.Equal((HomeFeatureKind.Added, "added2"), (without[^1].Kind, without[^1].Key));
        Assert.Equal((HomeFeatureKind.Radio, "station1"), (with[^1].Kind, with[^1].Key));
    }

    /// <summary>On a collection that cannot fill the strip, the empty card kinds are skipped, so the
    /// stations appear near the front and more than one is offered.</summary>
    [Fact]
    public void A_shelf_with_little_on_it_offers_more_than_one_station()
    {
        var strip = HomeFeatures.Strip(
            continued: [], added: ["added1"], lists: [], suggested: [], played: [], listened: [],
            stations: Stations, isVariousArtists: NoneVarious, count: HomeFeatures.Count);

        Assert.Equal(
            [
                (HomeFeatureKind.Added, "added1"),
                (HomeFeatureKind.Radio, "station1"),
                (HomeFeatureKind.Radio, "station2"),
            ],
            strip.Select(f => (f.Kind, f.Key)));
    }

    /// <summary>A station's uuid and a folder are separate namespaces, the same as a list's name: a
    /// station whose key is also a folder must not remove that album.</summary>
    [Fact]
    public void A_station_and_a_record_may_share_a_key()
    {
        var strip = HomeFeatures.Strip(
            continued: [], added: ["shared"], lists: [], suggested: [], played: [], listened: [],
            stations: ["shared"], isVariousArtists: NoneVarious, count: HomeFeatures.Count);

        Assert.Equal(
            [(HomeFeatureKind.Added, "shared"), (HomeFeatureKind.Radio, "shared")],
            strip.Select(f => (f.Kind, f.Key)));
    }

    /// <summary>Nothing has been played on the radio for long enough to count (true on a new install).
    /// The strip then has no radio card.</summary>
    [Fact]
    public void A_dial_nothing_has_been_played_on_has_no_card()
    {
        var strip = HomeFeatures.Strip(
            Continued, Added, Lists, Suggested, Played, Listened, [], NoneVarious, HomeFeatures.Count);

        Assert.DoesNotContain(HomeFeatureKind.Radio, strip.Select(f => f.Kind));
    }

    /// <summary>A card is drawn on the artist's photo, and a Various Artists album has no single
    /// artist. Each card kind skips it and offers its next item.</summary>
    [Fact]
    public void A_various_artists_record_is_never_featured()
    {
        var strip = HomeFeatures.Strip(
            continued: [], added: ["va1", "added1"], lists: [], suggested: ["va2", "sug1"],
            played: ["va1"], listened: ["va2", "heard1"], stations: [],
            isVariousArtists: key => key.StartsWith("va"), count: HomeFeatures.Count);

        Assert.Equal(
            [
                (HomeFeatureKind.Added, "added1"),
                (HomeFeatureKind.Suggested, "sug1"),
                (HomeFeatureKind.Listened, "heard1"),
            ],
            strip.Select(f => (f.Kind, f.Key)));
    }

    /// <summary>The Continue card shows the album that played last. When that album is a compilation
    /// there is no Continue card, and the next recent listen does not take its place.</summary>
    [Fact]
    public void A_various_artists_record_that_was_on_last_has_no_continue_card()
    {
        var strip = HomeFeatures.Strip(
            continued: ["va1"], added: [], lists: [], suggested: [], played: [],
            listened: ["va1", "heard1"], stations: [],
            isVariousArtists: key => key.StartsWith("va"), count: HomeFeatures.Count);

        Assert.Equal([(HomeFeatureKind.Listened, "heard1")], strip.Select(f => (f.Kind, f.Key)));
    }

    [Fact]
    public void An_empty_shelf_has_no_banner()
        => Assert.Empty(HomeFeatures.Strip([], [], [], [], [], [], [], NoneVarious, HomeFeatures.Count));

    [Fact]
    public void A_strip_of_no_cards_is_asked_for_nothing()
        => Assert.Empty(
            HomeFeatures.Strip(Continued, Added, Lists, Suggested, Played, Listened, Stations, NoneVarious, 0));

    [Fact]
    public void The_strip_never_holds_more_than_it_was_asked_for()
        => Assert.Equal(
            3,
            HomeFeatures.Strip(Continued, Added, Lists, Suggested, Played, Listened, Stations, NoneVarious, 3).Count);
}
