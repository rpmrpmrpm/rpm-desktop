using RPM.Core.Art;

namespace RPM.Core.Tests;

public class BackdropDeckTests
{
    private static readonly string[] Three = ["a.jpg", "b.jpg", "c.jpg"];

    private static BackdropDeck Loaded(string current = "b.jpg")
    {
        var deck = new BackdropDeck();
        deck.Load("manu chao", Three, current);
        return deck;
    }

    [Fact]
    public void Paging_moves_one_photo_at_a_time_in_both_directions()
    {
        var deck = Loaded();

        Assert.Equal("c.jpg", deck.Peek(1));
        Assert.Equal("a.jpg", deck.Peek(-1));
    }

    [Fact]
    public void Peeking_does_not_move_the_deck()
    {
        var deck = Loaded();

        deck.Peek(1);
        deck.Peek(1);

        // Both peeks answered from the same place: the picture on screen has not changed, so neither
        // has the deck. It moves on Commit and nowhere else.
        Assert.Equal(1, deck.Index);
        Assert.Equal("b.jpg", deck.Current);
    }

    [Fact]
    public void A_photo_that_never_arrives_leaves_the_deck_where_it_was()
    {
        var deck = Loaded();

        var wanted = deck.Peek(1);
        // ... and the download fails, so Commit is never called.

        Assert.Equal("c.jpg", wanted);
        Assert.Equal("b.jpg", deck.Current);
        Assert.Equal("c.jpg", deck.Peek(1));   // the same step is still there to be retried
    }

    [Fact]
    public void Committing_moves_to_the_photo_that_arrived()
    {
        var deck = Loaded();

        deck.Commit("c.jpg");

        Assert.Equal(2, deck.Index);
        Assert.Equal("c.jpg", deck.Current);
    }

    [Fact]
    public void Committing_a_photo_from_outside_the_deck_is_ignored()
    {
        var deck = Loaded();

        deck.Commit("somebody-elses.jpg");

        Assert.Equal("b.jpg", deck.Current);
    }

    [Fact]
    public void The_deck_clamps_at_both_ends_rather_than_wrapping()
    {
        var deck = Loaded("c.jpg");
        Assert.Null(deck.Peek(1));

        deck.Commit("a.jpg");
        Assert.Null(deck.Peek(-1));
    }

    [Fact]
    public void One_photograph_is_not_a_deck()
    {
        var deck = new BackdropDeck();
        deck.Load("edith piaf", ["only.jpg"], "only.jpg");

        Assert.False(deck.CanPage);
        Assert.Null(deck.Peek(1));
        Assert.Null(deck.Peek(-1));
    }

    [Fact]
    public void An_artist_with_no_backdrops_pages_nowhere()
    {
        var deck = new BackdropDeck();
        deck.Load("nobody", [], null);

        Assert.False(deck.CanPage);
        Assert.Equal(-1, deck.Index);
        Assert.Null(deck.Current);
        Assert.Null(deck.Peek(1));
    }

    [Fact]
    public void From_an_unknown_position_a_step_lands_on_the_first_or_last_photo()
    {
        // Nothing chosen yet: the chooser found no winner, or its pick is not in the list. The gesture
        // should still do something.
        var deck = new BackdropDeck();
        deck.Load("manu chao", Three, current: null);

        Assert.Equal(-1, deck.Index);
        Assert.Equal("a.jpg", deck.Peek(1));
        Assert.Equal("c.jpg", deck.Peek(-1));
    }

    [Fact]
    public void Playing_another_album_by_the_same_artist_keeps_the_hand_picked_photo()
    {
        var deck = Loaded();
        deck.Commit("c.jpg");

        // The next album re-runs the art pipeline, which comes back with the remembered choice.
        deck.Load("Manu Chao", Three, "c.jpg");

        Assert.Equal("c.jpg", deck.Current);
    }

    [Fact]
    public void Reloading_the_same_artist_without_a_choice_holds_its_place()
    {
        var deck = Loaded();

        deck.Load("manu chao", Three, current: null);

        Assert.Equal("b.jpg", deck.Current);
    }

    [Fact]
    public void A_different_artist_starts_again()
    {
        var deck = Loaded();

        deck.Load("edith piaf", ["x.jpg", "y.jpg"], current: null);

        Assert.Equal(-1, deck.Index);
        Assert.Null(deck.Current);
    }

    [Fact]
    public void A_held_place_cannot_fall_off_a_shorter_list()
    {
        var deck = Loaded("c.jpg");

        deck.Load("manu chao", ["a.jpg", "b.jpg"], current: null);

        Assert.Equal(1, deck.Index);
        Assert.Equal("b.jpg", deck.Current);
    }

    [Fact]
    public void Clearing_forgets_the_artist_and_the_deck()
    {
        var deck = Loaded();

        deck.Clear();

        Assert.Null(deck.ArtistKey);
        Assert.Equal(0, deck.Count);
        Assert.Equal(-1, deck.Index);
        Assert.False(deck.CanPage);
    }

    [Fact]
    public void A_step_of_nowhere_goes_nowhere()
    {
        Assert.Null(Loaded().Peek(0));
    }
}
