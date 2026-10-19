using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The home screen's quick selection: which of the listener's four kinds of item it offers, and in
/// what order.
///
/// <para>Tested separately, like the banner's layout, because it is arithmetic over four lists. The
/// mixing is the whole point, and a selection that quietly stopped mixing would still look like a
/// selection.</para>
/// </summary>
public class QuickPicksTests
{
    private static readonly string[] Songs = ["s1", "s2", "s3", "s4", "s5"];
    private static readonly string[] Albums = ["a1", "a2", "a3"];
    private static readonly string[] Lists = ["l1", "l2"];
    private static readonly string[] Stations = ["r1"];

    [Fact]
    public void One_of_each_kind_before_a_second_of_any()
    {
        var picks = QuickPicks.Choose(Songs, Albums, Lists, Stations, 8);

        Assert.Equal(
            [
                (QuickPickKind.Song, "s1"),
                (QuickPickKind.Album, "a1"),
                (QuickPickKind.Playlist, "l1"),
                (QuickPickKind.Station, "r1"),
                (QuickPickKind.Song, "s2"),
                (QuickPickKind.Album, "a2"),
                (QuickPickKind.Playlist, "l2"),
                (QuickPickKind.Song, "s3"),
            ],
            picks.Select(p => (p.Kind, p.Key)));
    }

    /// <summary>A kind with nothing to offer is skipped, not left as a hole. On a collection where
    /// only songs have been played, sixteen songs is what the listener really has.</summary>
    [Fact]
    public void A_kind_with_nothing_to_offer_is_stepped_past()
    {
        var picks = QuickPicks.Choose(Songs, [], [], [], QuickPicks.Count);

        Assert.Equal(Songs, picks.Select(p => p.Key));
        Assert.All(picks, p => Assert.Equal(QuickPickKind.Song, p.Kind));
    }

    [Fact]
    public void Nothing_played_is_nothing_offered()
        => Assert.Empty(QuickPicks.Choose([], [], [], [], QuickPicks.Count));

    [Fact]
    public void Never_more_than_it_was_asked_for()
        => Assert.Equal(3, QuickPicks.Choose(Songs, Albums, Lists, Stations, 3).Count);

    [Fact]
    public void A_selection_of_none_is_asked_for_nothing()
        => Assert.Empty(QuickPicks.Choose(Songs, Albums, Lists, Stations, 0));

    /// <summary>The loop has to end on a collection too small to fill the selection, which is true of
    /// every new collection.</summary>
    [Fact]
    public void A_shelf_with_less_than_the_selection_holds_ends_the_deal()
    {
        var picks = QuickPicks.Choose(["s1"], ["a1"], [], [], QuickPicks.Count);
        Assert.Equal(2, picks.Count);
    }

    [Fact]
    public void An_empty_key_is_not_offered()
    {
        var picks = QuickPicks.Choose(["", "s1"], [], [], [], QuickPicks.Count);
        Assert.Equal(["s1"], picks.Select(p => p.Key));
    }

    // ── the panels ──

    /// <summary>Down each panel and then across. This makes four columns read as four lists and not as
    /// a grid: the four most played go down the first panel.</summary>
    [Fact]
    public void The_panels_are_filled_down_before_across()
    {
        var picks = Enumerable.Range(1, 16)
            .Select(i => new QuickPick(QuickPickKind.Song, "s" + i))
            .ToList();

        var panels = QuickPicks.InPanels(picks);

        Assert.Equal(QuickPicks.Panels, panels.Count);
        Assert.Equal(["s1", "s2", "s3", "s4"], panels[0].Select(p => p.Key));
        Assert.Equal(["s5", "s6", "s7", "s8"], panels[1].Select(p => p.Key));
        Assert.Equal(["s13", "s14", "s15", "s16"], panels[3].Select(p => p.Key));
    }

    /// <summary>A short selection gives short panels, not padded ones: panels with nothing in them are
    /// not drawn.</summary>
    [Fact]
    public void A_short_selection_gives_fewer_panels()
    {
        var picks = Enumerable.Range(1, 6)
            .Select(i => new QuickPick(QuickPickKind.Song, "s" + i))
            .ToList();

        var panels = QuickPicks.InPanels(picks);

        Assert.Equal(2, panels.Count);
        Assert.Equal(4, panels[0].Count);
        Assert.Equal(2, panels[1].Count);
    }

    [Fact]
    public void Nothing_makes_no_panels()
    {
        Assert.Empty(QuickPicks.InPanels([]));
        Assert.Empty(QuickPicks.InPanels([new QuickPick(QuickPickKind.Song, "s1")], tall: 0));
    }

    /// <summary>The count and the panel height are one fact stated twice, and they must agree.</summary>
    [Fact]
    public void The_shape_is_consistent()
        => Assert.Equal(QuickPicks.Count, QuickPicks.Panels * QuickPicks.Tall);
}
