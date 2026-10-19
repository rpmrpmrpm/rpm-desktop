using RPM.Desktop.Presentation.Main.LeftPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="PaneSequence"/>: the order the track info pane is taken down and put back up in.
///
/// <para>The fault it prevents: six errands fill that pane on six clocks. The sleeve is a disk read, but
/// the artist's photograph takes two network round trips. So the new album's cover could sit on the last
/// artist's photograph for a second on every track change. This test makes sure no layer goes up before
/// the layers it must not be seen without, whatever order the answers come back in.</para>
/// </summary>
public class PaneSequenceTests
{
    /// <summary>Takes the pane down, which every build starts with.</summary>
    private static PaneSequence Emptied(PaneScope scope = PaneScope.Artist)
    {
        var order = new PaneSequence(scope);
        while (order.Peel() is not null)
        {
        }

        return order;
    }

    private static void PicturesIn(PaneSequence order)
    {
        order.Answered(PaneErrand.Sleeve, found: true);
        order.Answered(PaneErrand.Photo, found: true);
    }

    private static void WordsIn(PaneSequence order)
    {
        order.Answered(PaneErrand.Facts, found: true);
        order.Answered(PaneErrand.Papers, found: true);
        order.Answered(PaneErrand.Words, found: true);
    }

    [Fact]
    public void The_peel_runs_from_the_bottom_up()
    {
        var order = new PaneSequence(PaneScope.Artist);
        var taken = new List<PaneLayer>();

        while (order.Peel() is { } layer)
            taken.Add(layer);

        Assert.Equal(
        [
            PaneLayer.Facts, PaneLayer.Elsewhere, PaneLayer.Album, PaneLayer.Members,
            PaneLayer.Artist, PaneLayer.Lyrics, PaneLayer.Hero, PaneLayer.Photo,
        ], taken);
        Assert.True(order.Peeled);
    }

    [Fact]
    public void Nothing_goes_up_until_the_pane_is_empty()
    {
        var order = new PaneSequence(PaneScope.Artist);
        PicturesIn(order);
        WordsIn(order);

        Assert.Null(order.Next());
    }

    [Fact]
    public void The_sleeve_alone_puts_nothing_up()
    {
        // The whole fault in one test. The sleeve is local and answers first. It must not appear on the
        // last artist's photograph while this record's own photograph is still being fetched.
        var order = Emptied();
        order.Answered(PaneErrand.Sleeve, found: true);

        Assert.Null(order.Next());
    }

    [Fact]
    public void A_photograph_that_does_not_exist_is_still_an_answer()
    {
        var order = Emptied();
        order.Answered(PaneErrand.Sleeve, found: true);
        order.Answered(PaneErrand.Photo, found: false);

        Assert.Equal(PaneLayer.Hero, order.Next());
    }

    [Fact]
    public void The_pictures_go_up_bottom_to_top()
    {
        var order = Emptied();
        PicturesIn(order);

        Assert.Equal(PaneLayer.Hero, order.Next());
        order.Put(PaneLayer.Hero);
        Assert.Equal(PaneLayer.Photo, order.Next());
    }

    [Fact]
    public void The_credits_wait_for_every_word()
    {
        // The credits are the bottom block, and the spinner covers them. So the whole text region
        // resolves before any of it shows.
        var order = Emptied();
        PicturesIn(order);
        order.Put(PaneLayer.Hero);
        order.Put(PaneLayer.Photo);

        order.Answered(PaneErrand.Facts, found: true);
        order.Answered(PaneErrand.Words, found: true);
        Assert.Null(order.Next());          // the encyclopedias have not answered

        order.Answered(PaneErrand.Papers, found: false);
        Assert.Equal(PaneLayer.Facts, order.Next());
    }

    [Fact]
    public void The_words_go_up_bottom_to_top()
    {
        var order = Emptied();
        PicturesIn(order);
        WordsIn(order);
        order.Put(PaneLayer.Hero);
        order.Put(PaneLayer.Photo);

        var built = new List<PaneLayer>();
        while (order.Next() is { } layer)
        {
            built.Add(layer);
            order.Put(layer);

            // The doors and the three cards are one answer from one errand, so they land together.
            if (layer == PaneLayer.Elsewhere)
            {
                order.Put(PaneLayer.Album);
                order.Put(PaneLayer.Members);
                order.Put(PaneLayer.Artist);
            }
        }

        Assert.Equal([PaneLayer.Facts, PaneLayer.Elsewhere, PaneLayer.Lyrics], built);
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_the_answers_came_in()
    {
        // A cached lookup answers before the disk does. The sequence must not notice.
        var backwards = Emptied();
        backwards.Answered(PaneErrand.Papers, found: true);
        backwards.Answered(PaneErrand.Words, found: true);
        backwards.Answered(PaneErrand.Facts, found: true);
        backwards.Answered(PaneErrand.Photo, found: true);
        backwards.Answered(PaneErrand.Sleeve, found: true);

        Assert.Equal(PaneLayer.Hero, backwards.Next());
    }

    [Fact]
    public void Asking_twice_answers_the_same()
    {
        var order = Emptied();
        PicturesIn(order);

        Assert.Equal(order.Next(), order.Next());
        Assert.Equal(PaneLayer.Hero, order.Next());
    }

    [Fact]
    public void It_is_not_done_until_the_last_block_is_up()
    {
        var order = Emptied();
        PicturesIn(order);
        WordsIn(order);
        Assert.False(order.Done);

        foreach (var layer in Enum.GetValues<PaneLayer>())
            order.Put(layer);

        Assert.True(order.Done);
    }

    // ══ same artist, so most of the pane never moves ══

    [Fact]
    public void The_artists_own_layers_are_never_peeled_off_a_change_of_track()
    {
        var order = new PaneSequence(PaneScope.Record);
        var taken = new List<PaneLayer>();

        while (order.Peel() is { } layer)
            taken.Add(layer);

        // The photograph, the artist card and the members stay exactly where they are.
        Assert.Equal([PaneLayer.Facts, PaneLayer.Elsewhere, PaneLayer.Album, PaneLayer.Lyrics, PaneLayer.Hero], taken);
    }

    [Fact]
    public void The_photograph_is_not_asked_for_again()
    {
        var order = Emptied(PaneScope.Record);

        Assert.Equal(Arrival.Landed, order.Of(PaneErrand.Photo));
        Assert.True(order.Keeps(PaneLayer.Photo));
        Assert.False(order.Keeps(PaneLayer.Hero));
    }

    [Fact]
    public void Only_the_sleeve_holds_the_first_half_up()
    {
        var order = Emptied(PaneScope.Record);
        Assert.False(order.PicturesSettled);
        Assert.True(order.FetchingPictures);

        order.Answered(PaneErrand.Sleeve, found: true);
        Assert.Equal(PaneLayer.Hero, order.Next());
    }

    [Fact]
    public void The_photo_layer_is_skipped_on_the_way_back_up()
    {
        var order = Emptied(PaneScope.Record);
        order.Answered(PaneErrand.Sleeve, found: true);
        WordsIn(order);

        order.Put(PaneLayer.Hero);
        Assert.Equal(PaneLayer.Facts, order.Next());   // not Photo: it never left
    }

    [Fact]
    public void A_change_of_artist_rebuilds_everything()
    {
        var order = new PaneSequence(PaneScope.Artist);

        foreach (var layer in PaneSequence.Layers)
            Assert.False(order.Keeps(layer));
    }

    // ── a pane that was already taken down ──

    /// <summary>The music stopped, the pane was emptied behind the empty state, and the listener has
    /// pressed play. There is nothing to peel, and the build must not spend the peel's length finding
    /// that out one layer at a time.</summary>
    [Fact]
    public void A_pane_already_empty_has_nothing_left_to_peel()
    {
        var order = new PaneSequence(PaneScope.Artist);
        order.AlreadyEmpty();

        Assert.True(order.Peeled);
        Assert.Null(order.Peel());
    }

    /// <summary>And it is a start, not an end: everything below the peel is still to be put up.</summary>
    [Fact]
    public void A_pane_already_empty_still_has_everything_to_build()
    {
        var order = new PaneSequence(PaneScope.Artist);
        order.AlreadyEmpty();

        Assert.False(order.Done);
        foreach (var layer in PaneSequence.Layers)
            Assert.False(order.Up(layer));
    }
}
