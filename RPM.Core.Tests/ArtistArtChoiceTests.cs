using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What paging the carousel to a photograph and leaving it there means for the saved order: that one
/// opens the artist from now on, and the chooser does not get to overrule it again.
/// </summary>
public class ArtistArtChoiceTests
{
    private static IReadOnlyList<ArtistBackdrop> Stored(params (string Url, bool Hidden)[] items)
        => items.Select((it, i) => new ArtistBackdrop(it.Url, i, 0, it.Hidden)).ToList();

    private static string[] Urls(IReadOnlyList<ArtistBackdrop> order)
        => order.Select(b => b.Url).ToArray();

    [Fact]
    public void The_photograph_stopped_on_goes_to_the_front()
    {
        var order = ArtistArtChoice.Prefer(Stored(("a", false), ("b", false), ("c", false)), ["a", "b", "c"], "c");

        Assert.Equal(["c", "a", "b"], Urls(order));
    }

    [Fact]
    public void Everything_else_keeps_the_order_it_was_in()
    {
        // Paging back and forth must not shuffle the pictures around the one being looked at, or the
        // arrows would lead somewhere different every time they were pressed.
        var order = ArtistArtChoice.Prefer(Stored(("a", false), ("b", false), ("c", false), ("d", false)), null, "c");

        Assert.Equal(["c", "a", "b", "d"], Urls(order));
    }

    [Fact]
    public void The_positions_are_renumbered_from_the_front()
        => Assert.Equal([0, 1, 2],
            ArtistArtChoice.Prefer(Stored(("a", false), ("b", false), ("c", false)), null, "b")
                .Select(b => b.Position).ToArray());

    [Fact]
    public void A_photo_that_arrived_this_session_is_kept_rather_than_dropped()
    {
        // The carousel can be ahead of the store: the ranker's order is shown before it is written.
        // Saving the choice must not take the unsaved ones off the artist.
        var order = ArtistArtChoice.Prefer(Stored(("a", false)), ["a", "new"], "a");

        Assert.Equal(["a", "new"], Urls(order));
    }

    [Fact]
    public void Choosing_one_that_was_never_stored_still_leads()
    {
        var order = ArtistArtChoice.Prefer(Stored(("a", false), ("b", false)), ["a", "b", "new"], "new");

        Assert.Equal(["new", "a", "b"], Urls(order));
    }

    [Fact]
    public void Nothing_saved_yet_is_the_carousel_with_the_choice_in_front()
    {
        var order = ArtistArtChoice.Prefer(null, ["a", "b", "c"], "b");

        Assert.Equal(["b", "a", "c"], Urls(order));
    }

    [Fact]
    public void Hidden_photos_stay_hidden_and_stay_saved()
    {
        // Hidden is a standing instruction about a picture, not about the order it sits in.
        var order = ArtistArtChoice.Prefer(Stored(("a", false), ("b", true), ("c", false)), null, "c");

        Assert.Equal(["c", "a", "b"], Urls(order));
        Assert.True(order.Single(b => b.Url == "b").Hidden);
    }

    [Fact]
    public void Choosing_a_hidden_photo_unhides_it()
    {
        // It cannot have been paged to while hidden, but a stale carousel could still offer it, and a
        // picture the user is looking at and has asked for is not one that is off the carousel.
        var order = ArtistArtChoice.Prefer(Stored(("a", false), ("b", true)), null, "b");

        Assert.Equal(["b", "a"], Urls(order));
        Assert.False(order[0].Hidden);
    }

    [Fact]
    public void The_same_photograph_is_not_saved_twice()
    {
        // The carousel and the store hold the same URLs; merging them must not double the list.
        var order = ArtistArtChoice.Prefer(Stored(("a", false), ("b", false)), ["a", "b"], "b");

        Assert.Equal(["b", "a"], Urls(order));
    }

    [Fact]
    public void Choosing_the_one_already_in_front_changes_nothing()
        => Assert.Equal(["a", "b"],
            Urls(ArtistArtChoice.Prefer(Stored(("a", false), ("b", false)), null, "a")));

    [Fact]
    public void A_chosen_order_is_then_left_alone_by_the_chooser()
    {
        // The purpose of this: once saved as curated, the reconciler must not re-rank it away on
        // the next visit. Written as the two halves meeting, because that is where it would break.
        var chosen = ArtistArtChoice.Prefer(Stored(("a", false), ("b", false), ("c", false)), null, "c");
        var stored = new ArtistArtOrder("artist", Curated: true, chosen, RankVersion: 0);

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"], currentRankVersion: 9);

        Assert.False(plan.NeedsRank);
        Assert.Equal(["c", "a", "b"], plan.DisplayUrls.ToArray());
    }

    // ── taking one off the carousel ──

    [Fact]
    public void A_removed_photograph_is_kept_as_a_hidden_row()
    {
        // Kept, because a deleted row says nothing to the source that offers the photograph again.
        var order = ArtistArtChoice.Hide(Stored(("a", false), ("b", false)), ["a", "b"], "b");

        Assert.Equal(["a", "b"], Urls(order));
        Assert.True(order.Single(b => b.Url == "b").Hidden);
    }

    [Fact]
    public void Removing_one_leaves_the_others_where_they_were()
    {
        var order = ArtistArtChoice.Hide(Stored(("a", false), ("b", false), ("c", false)), null, "b");

        Assert.Equal(["a", "b", "c"], Urls(order));
        Assert.Equal([0, 1, 2], order.Select(b => b.Position).ToArray());
        Assert.False(order.Single(b => b.Url == "a").Hidden);
        Assert.False(order.Single(b => b.Url == "c").Hidden);
    }

    [Fact]
    public void A_picture_removed_before_its_choice_was_written_down_is_still_hidden()
    {
        // The case this is for: a picture chosen off the computer and taken straight back off again,
        // with the save of the choice still in flight. Nothing stored knows the URL, so the row has to
        // be made here. Otherwise the removal applies to nothing and the picture comes back.
        var order = ArtistArtChoice.Hide(Stored(("a", false)), ["own", "a"], "own");

        Assert.True(order.Single(b => b.Url == "own").Hidden);
        Assert.False(order.Single(b => b.Url == "a").Hidden);
    }

    [Fact]
    public void Removing_one_nothing_has_ever_heard_of_still_writes_it_down()
    {
        var order = ArtistArtChoice.Hide(null, null, "gone");

        Assert.Equal(["gone"], Urls(order));
        Assert.True(order[0].Hidden);
    }

    [Fact]
    public void A_removal_survives_the_next_visit()
    {
        // The two halves meeting, as above: the sources offer the same three photographs next time,
        // and the one taken off must not be among what the carousel shows.
        var removed = ArtistArtChoice.Hide(Stored(("a", false), ("b", false), ("c", false)), null, "b");
        var stored = new ArtistArtOrder("artist", Curated: true, removed, RankVersion: 9);

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"], currentRankVersion: 9);

        Assert.Equal(["a", "c"], plan.DisplayUrls.ToArray());
    }
}
