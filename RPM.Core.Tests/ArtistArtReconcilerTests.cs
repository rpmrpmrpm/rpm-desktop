using RPM.Core.Library;

namespace RPM.Core.Tests;

public class ArtistArtReconcilerTests
{
    private static ArtistArtOrder Stored(bool curated, params (string Url, bool Hidden)[] items)
        => Stored(curated, 0, items);

    private static ArtistArtOrder Stored(bool curated, int rankVersion, params (string Url, bool Hidden)[] items)
        => new("artist", curated,
            items.Select((it, i) => new ArtistBackdrop(it.Url, i, 0, it.Hidden)).ToList(), rankVersion);

    [Fact]
    public void No_stored_order_asks_for_a_rank_and_shows_the_source_order()
    {
        var plan = ArtistArtReconciler.Reconcile(null, ["a", "b", "c"]);

        Assert.True(plan.NeedsRank);
        Assert.Equal(["a", "b", "c"], plan.DisplayUrls);
    }

    [Fact]
    public void A_single_candidate_needs_no_rank()
    {
        var plan = ArtistArtReconciler.Reconcile(null, ["only"]);

        Assert.False(plan.NeedsRank);
        Assert.Equal(["only"], plan.DisplayUrls);
    }

    [Fact]
    public void An_unchanged_auto_order_is_reused_without_ranking()
    {
        var stored = Stored(curated: false, ("b", false), ("a", false), ("c", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"]);

        Assert.False(plan.NeedsRank);
        Assert.False(plan.Changed);
        Assert.Equal(["b", "a", "c"], plan.DisplayUrls);   // the saved order, not the source order
    }

    [Fact]
    public void An_auto_order_re_ranks_when_a_photo_appears()
    {
        var stored = Stored(curated: false, ("a", false), ("b", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"]);

        Assert.True(plan.NeedsRank);
    }

    [Fact]
    public void An_auto_order_re_ranks_when_a_photo_vanishes()
    {
        var stored = Stored(curated: false, ("a", false), ("b", false), ("c", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b"]);

        Assert.True(plan.NeedsRank);
        Assert.DoesNotContain("c", plan.DisplayUrls);
    }

    [Fact]
    public void A_curated_order_is_never_re_ranked()
    {
        var stored = Stored(curated: true, ("c", false), ("a", false), ("b", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"]);

        Assert.False(plan.NeedsRank);
        Assert.Equal(["c", "a", "b"], plan.DisplayUrls);
    }

    [Fact]
    public void A_curated_order_appends_new_photos_at_the_end_and_asks_to_persist()
    {
        var stored = Stored(curated: true, ("c", false), ("a", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "c", "new"]);

        Assert.False(plan.NeedsRank);
        Assert.True(plan.Changed);                     // the new photo must be written into the order
        Assert.Equal(["c", "a", "new"], plan.DisplayUrls);
        Assert.Equal(["c", "a", "new"], plan.Merged.Select(b => b.Url));
        Assert.Equal([0, 1, 2], plan.Merged.Select(b => b.Position));
    }

    [Fact]
    public void Hidden_photos_are_kept_in_storage_but_left_out_of_the_carousel()
    {
        var stored = Stored(curated: true, ("a", false), ("b", true), ("c", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"]);

        Assert.Equal(["a", "c"], plan.DisplayUrls);                 // b hidden → not shown
        Assert.Contains(plan.Merged, b => b.Url == "b" && b.Hidden); // but still stored
    }

    [Fact]
    public void An_auto_order_from_an_older_algorithm_is_re_ranked_even_when_the_photos_are_unchanged()
    {
        // A fix to the chooser raises the version, and an order saved under the old one is re-ranked
        // the next time it is shown rather than being reused as it is.
        var stored = Stored(curated: false, rankVersion: 0, ("a", false), ("b", false), ("c", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"], currentRankVersion: 1);

        Assert.True(plan.NeedsRank);
    }

    [Fact]
    public void An_auto_order_from_the_current_algorithm_is_reused_without_ranking()
    {
        var stored = Stored(curated: false, rankVersion: 1, ("a", false), ("b", false), ("c", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"], currentRankVersion: 1);

        Assert.False(plan.NeedsRank);   // same version → the cache stands, no re-rank
    }

    [Fact]
    public void A_curated_order_is_not_re_ranked_even_when_its_algorithm_is_older()
    {
        // The user's hand-arranged order is never reshuffled, version bump or not.
        var stored = Stored(curated: true, rankVersion: 0, ("c", false), ("a", false), ("b", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"], currentRankVersion: 1);

        Assert.False(plan.NeedsRank);
        Assert.Equal(["c", "a", "b"], plan.DisplayUrls);
    }

    // ── a picture the person chose off their own computer ──

    [Fact]
    public void A_picture_of_the_users_own_survives_a_source_that_does_not_offer_it()
    {
        // No art source will ever hand back a file on this computer, so the rule that drops a saved photo
        // the sources no longer offer would throw the user's own choice away on the very next track.
        var stored = Stored(curated: true, ("file:///Users/x/Pictures/nick.jpg", false), ("a", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b"]);

        Assert.Equal(["file:///Users/x/Pictures/nick.jpg", "a", "b"], plan.DisplayUrls);
    }

    [Fact]
    public void An_own_picture_survives_an_automatic_order_too()
    {
        // Curated is how it is saved today, but the rule belongs to the picture, not to the flag.
        var stored = Stored(curated: false, ("file:///Users/x/own.png", false), ("a", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a"]);

        Assert.Contains("file:///Users/x/own.png", plan.DisplayUrls);
    }

    [Fact]
    public void An_own_picture_alone_is_not_read_as_the_photo_set_changing()
    {
        // If keeping it counted as an arrival or a departure, every visit to this artist would re-rank.
        var stored = Stored(curated: false, rankVersion: 1, ("file:///Users/x/own.png", false), ("a", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a"], currentRankVersion: 1);

        Assert.False(plan.NeedsRank);
        Assert.False(plan.Changed);
    }

    [Fact]
    public void Duplicate_candidate_urls_are_collapsed()
    {
        var plan = ArtistArtReconciler.Reconcile(null, ["a", "a", "b"]);

        Assert.Equal(["a", "b"], plan.DisplayUrls);
    }

    // ── what the ranker is handed, as against what shows ──

    [Fact]
    public void A_photo_the_source_now_leads_with_is_ranked_from_the_front_not_appended_at_the_back()
    {
        // The case that adding a fanart.tv key creates: its higher-resolution photos join at the start
        // of the candidate list, while the saved order knows only the earlier ones. What shows must not
        // jump, but the ranker has to see the new photo at the front: it scores only the first few and
        // uses position as the likes tiebreak, so a photo added at the end would never be judged.
        var stored = Stored(curated: false, ("old1", false), ("old2", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["fanart", "old1", "old2"]);

        Assert.True(plan.NeedsRank);
        Assert.Equal(["old1", "old2", "fanart"], plan.DisplayUrls);   // shown: saved first, no reshuffle
        Assert.Equal(["fanart", "old1", "old2"], plan.RankUrls);      // judged: the source's order
    }

    [Fact]
    public void Hidden_photos_are_kept_out_of_what_the_ranker_is_handed()
    {
        // Hidden means off the carousel, not at the back of the queue for it. A hidden photo must not
        // spend one of the ranker's scoring slots.
        var stored = Stored(curated: false, ("a", false), ("b", true), ("c", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c", "new"]);

        Assert.Equal(["a", "c", "new"], plan.RankUrls);
        Assert.DoesNotContain("b", plan.RankUrls);
    }

    [Fact]
    public void A_re_rank_on_a_version_bump_judges_the_source_order_not_the_previous_ranking()
    {
        // Same photos, older algorithm. The previous ranking is not evidence about likes, and feeding it
        // back in would let the old order bias the tiebreak of the new one.
        var stored = Stored(curated: false, rankVersion: 1, ("c", false), ("a", false), ("b", false));

        var plan = ArtistArtReconciler.Reconcile(stored, ["a", "b", "c"], currentRankVersion: 2);

        Assert.True(plan.NeedsRank);
        Assert.Equal(["c", "a", "b"], plan.DisplayUrls);   // shown meanwhile: the saved order
        Assert.Equal(["a", "b", "c"], plan.RankUrls);      // judged: the source's order
    }

    [Fact]
    public void The_first_sight_of_an_artist_ranks_the_source_order()
    {
        var plan = ArtistArtReconciler.Reconcile(null, ["fanart", "builtin"]);

        Assert.Equal(["fanart", "builtin"], plan.RankUrls);
    }
}
