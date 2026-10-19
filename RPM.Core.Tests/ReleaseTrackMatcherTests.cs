using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

public class ReleaseTrackMatcherTests
{
    private static ReleaseTrackInfo Listed(uint position, string title, uint disc = 0, uint discPosition = 0)
        => new(position, title, Artist: null, LengthSeconds: null, Disc: disc, DiscPosition: discPosition);

    private static string?[] Titles(IReadOnlyList<ReleaseTrackInfo?> matched)
        => [.. matched.Select(t => t?.Title)];

    [Fact]
    public void Numbers_match_within_their_own_disc()
    {
        List<ReleaseTrackInfo> listing =
        [
            Listed(1, "One", disc: 1, discPosition: 1),
            Listed(2, "Two", disc: 1, discPosition: 2),
            Listed(3, "Three", disc: 2, discPosition: 1),
            Listed(4, "Four", disc: 2, discPosition: 2),
        ];

        // The failure this exists to prevent: disc 2 track 1 is not disc 1 track 1.
        Assert.Equal(
            ["One", "Two", "Three", "Four"],
            Titles(ReleaseTrackMatcher.Match(
            [
                new TrackPlace(1, 1, null), new TrackPlace(1, 2, null),
                new TrackPlace(2, 1, null), new TrackPlace(2, 2, null),
            ], listing)));
    }

    [Fact]
    public void The_name_already_on_the_file_does_not_veto_the_match()
    {
        // The whole point of the action is that the names on the files are wrong. A rip full of
        // "Track 01", or one named from a different pressing, is exactly what is being repaired.
        List<ReleaseTrackInfo> listing = [Listed(1, "Intro"), Listed(2, "The Hit")];

        Assert.Equal(
            ["Intro", "The Hit"],
            Titles(ReleaseTrackMatcher.Match(
                [new TrackPlace(0, 1, "Track 01"), new TrackPlace(0, 2, "Track 02")], listing)));
    }

    [Fact]
    public void A_title_chooses_between_candidates_that_share_a_number()
    {
        // Two discs, a listing that numbers its rows but does not say which disc each is on: the only
        // thing that can tell 2 from 2 is what they are called.
        List<ReleaseTrackInfo> listing = [Listed(2, "Side A Two"), Listed(2, "Side B Two")];

        Assert.Equal(
            ["Side B Two", null],
            Titles(ReleaseTrackMatcher.Match(
                [new TrackPlace(0, 2, "Side B Two"), new TrackPlace(0, 2, null)], listing)));
    }

    [Fact]
    public void Untitled_rows_are_matched_on_the_number_alone()
    {
        // The half-tagged rip: numbers but no names, which is exactly what this is for.
        List<ReleaseTrackInfo> listing = [Listed(1, "One"), Listed(2, "Two")];

        Assert.Equal(
            ["One", "Two"],
            Titles(ReleaseTrackMatcher.Match(
                [new TrackPlace(0, 1, null), new TrackPlace(0, 2, "")], listing)));
    }

    [Fact]
    public void With_no_numbers_at_all_the_files_are_the_running_order()
    {
        List<ReleaseTrackInfo> listing = [Listed(1, "One"), Listed(2, "Two"), Listed(3, "Three")];

        Assert.Equal(
            ["One", "Two", "Three"],
            Titles(ReleaseTrackMatcher.Match(
            [
                new TrackPlace(0, 0, null), new TrackPlace(0, 0, null), new TrackPlace(0, 0, null),
            ], listing)));
    }

    [Fact]
    public void Order_matching_follows_the_discs_when_both_sides_name_them()
    {
        List<ReleaseTrackInfo> listing =
        [
            Listed(1, "A1", disc: 1, discPosition: 1),
            Listed(2, "A2", disc: 1, discPosition: 2),
            Listed(3, "B1", disc: 2, discPosition: 1),
        ];

        Assert.Equal(
            ["A1", "A2", "B1"],
            Titles(ReleaseTrackMatcher.Match(
            [
                new TrackPlace(1, 0, null), new TrackPlace(1, 0, null), new TrackPlace(2, 0, null),
            ], listing)));
    }

    [Fact]
    public void Discs_on_the_files_and_none_in_the_listing_match_nothing()
    {
        // Two folders, a flat listing: nothing says which half of the record the second folder is, and
        // taking the listing from the top for each would name both discs after the first.
        List<ReleaseTrackInfo> listing = [Listed(1, "One"), Listed(2, "Two")];

        Assert.Equal(
            [null, null],
            Titles(ReleaseTrackMatcher.Match(
                [new TrackPlace(1, 0, null), new TrackPlace(2, 0, null)], listing)));
    }

    [Fact]
    public void Some_rows_numbered_means_the_numbers_are_the_evidence()
    {
        // A gap in the numbers means something: the file with none is not silently taken as next.
        List<ReleaseTrackInfo> listing = [Listed(1, "One"), Listed(2, "Two")];

        Assert.Equal(
            ["One", null],
            Titles(ReleaseTrackMatcher.Match(
                [new TrackPlace(0, 1, null), new TrackPlace(0, 0, null)], listing)));
    }

    [Fact]
    public void More_files_than_the_listing_holds_leaves_the_rest_alone()
    {
        // A hidden track, or the wrong pressing: what matches matches, and the caller says how many.
        List<ReleaseTrackInfo> listing = [Listed(1, "One")];

        Assert.Equal(
            ["One", null],
            Titles(ReleaseTrackMatcher.Match(
                [new TrackPlace(0, 0, null), new TrackPlace(0, 0, null)], listing)));
    }

    // ── files whose disc numbers are simply wrong ─────────────────────────────

    /// <summary>
    /// The double album that a ripper stamped "disc 1" on, every file of it. Trusting the tag would
    /// match the first twelve files against disc one's twelve tracks and leave the other twelve
    /// untouched, still calling themselves disc one. The count agreeing exactly (24 files, 24 tracks)
    /// is the evidence that these files ARE this release and the tag is what is wrong.
    /// </summary>
    [Fact]
    public void Files_that_all_claim_one_disc_are_read_as_the_whole_running_order()
    {
        var places = Enumerable.Range(0, 24).Select(_ => new TrackPlace(1, 0, null)).ToList();
        var listing = new List<ReleaseTrackInfo>();
        for (uint disc = 1; disc <= 2; disc++)
            for (uint n = 1; n <= 12; n++)
                listing.Add(new ReleaseTrackInfo(
                    (disc - 1) * 12 + n, $"D{disc}T{n}", null, null, Disc: disc, DiscPosition: n));

        var matched = ReleaseTrackMatcher.Match(places, listing, out var ignored);

        Assert.True(ignored);
        Assert.All(matched, m => Assert.NotNull(m));
        Assert.Equal("D1T1", matched[0]!.Title);
        Assert.Equal("D2T1", matched[12]!.Title);
        Assert.Equal("D2T12", matched[23]!.Title);
    }

    /// <summary>The same for files that name no disc at all, against a listing that names two: asking
    /// for "the tracks on disc 0" returns nothing, so every file would match nothing.</summary>
    [Fact]
    public void Files_with_no_disc_against_a_two_disc_listing_still_match()
    {
        var places = Enumerable.Range(0, 4).Select(_ => new TrackPlace(0, 0, null)).ToList();
        var listing = new List<ReleaseTrackInfo>
        {
            new(1, "D1T1", null, null, Disc: 1, DiscPosition: 1),
            new(2, "D1T2", null, null, Disc: 1, DiscPosition: 2),
            new(3, "D2T1", null, null, Disc: 2, DiscPosition: 1),
            new(4, "D2T2", null, null, Disc: 2, DiscPosition: 2),
        };

        var matched = ReleaseTrackMatcher.Match(places, listing, out var ignored);

        Assert.True(ignored);
        Assert.Equal(["D1T1", "D1T2", "D2T1", "D2T2"], matched.Select(m => m?.Title).ToArray());
    }

    /// <summary>Disc numbers that DO line up are still trusted. This is the case the override must
    /// not take over: the discs are honoured, and the files' order across the whole listing is not
    /// used.</summary>
    [Fact]
    public void Disc_numbers_that_line_up_are_still_honoured()
    {
        var places = new List<TrackPlace>
        {
            new(2, 0, null), new(2, 0, null), new(1, 0, null), new(1, 0, null),
        };
        var listing = new List<ReleaseTrackInfo>
        {
            new(1, "D1T1", null, null, Disc: 1, DiscPosition: 1),
            new(2, "D1T2", null, null, Disc: 1, DiscPosition: 2),
            new(3, "D2T1", null, null, Disc: 2, DiscPosition: 1),
            new(4, "D2T2", null, null, Disc: 2, DiscPosition: 2),
        };

        var matched = ReleaseTrackMatcher.Match(places, listing, out var ignored);

        Assert.False(ignored);
        Assert.Equal(["D2T1", "D2T2", "D1T1", "D1T2"], matched.Select(m => m?.Title).ToArray());
    }

    /// <summary>Half a record selected is not evidence that the whole record's disc tags are wrong, so
    /// the disc tags are trusted and nothing is overridden.</summary>
    [Fact]
    public void A_partial_selection_does_not_override_what_the_files_say()
    {
        var places = Enumerable.Range(0, 3).Select(_ => new TrackPlace(1, 0, null)).ToList();
        var listing = new List<ReleaseTrackInfo>
        {
            new(1, "D1T1", null, null, Disc: 1, DiscPosition: 1),
            new(2, "D2T1", null, null, Disc: 2, DiscPosition: 1),
            new(3, "D2T2", null, null, Disc: 2, DiscPosition: 2),
            new(4, "D2T3", null, null, Disc: 2, DiscPosition: 3),
        };

        var matched = ReleaseTrackMatcher.Match(places, listing, out var ignored);

        Assert.False(ignored);
        Assert.Equal("D1T1", matched[0]?.Title);
        Assert.Null(matched[1]);
        Assert.Null(matched[2]);
    }
}
