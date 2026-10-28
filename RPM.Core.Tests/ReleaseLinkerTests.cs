using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Linking an album to a Discogs release without asking: only when the names agree, the track count is the same
/// and the lengths line up. A wrong link shows the wrong catalogue number and barcode on the album page, and
/// files the record under the wrong styles, so anything short of proof is left for the listener.
/// </summary>
public class ReleaseLinkerTests
{
    private static readonly double[] Lengths = [230, 247, 204, 285, 218];

    private static LinkCandidate Candidate(
        string id, string title = "Read My Lips", string artist = "Sophie Ellis-Bextor", string format = "CD, Album",
        IEnumerable<double?>? lengths = null)
    {
        var tracks = (lengths ?? Lengths.Select(l => (double?)l))
            .Select((length, i) => new ReleaseTrackInfo((uint)(i + 1), $"track {i + 1}", null, length))
            .ToList();
        var release = new ReleaseSearchResult(new ReleaseRef("discogs", id), title, artist, 2002, "2002", "UK", format, 0);
        var detail = new ReleaseDetail(new ReleaseRef("discogs", id), title, artist, 2002, "UK", format, tracks);
        return new LinkCandidate(release, detail);
    }

    private static LinkDecision Decide(params LinkCandidate[] candidates) =>
        ReleaseLinker.Decide("Sophie Ellis-Bextor", "Read My Lips (Deluxe Version)", Lengths, candidates);

    [Fact]
    public void A_release_with_the_names_the_count_and_the_lengths_is_linked()
    {
        var decision = Decide(Candidate("1"));

        Assert.Equal(LinkVerdict.Link, decision.Verdict);
        Assert.Equal("1", decision.Best!.Release.Ref.Id);
    }

    /// <summary>Two pressings with one listing tie. The digital one is taken: the files most likely came from it.</summary>
    [Fact]
    public void Of_two_pressings_that_fit_the_digital_one_is_taken()
    {
        var decision = Decide(Candidate("cd"), Candidate("file", format: "File, FLAC, Album"), Candidate("lp", format: "Vinyl, LP"));

        Assert.Equal(LinkVerdict.Link, decision.Verdict);
        Assert.Equal("file", decision.Best!.Release.Ref.Id);
    }

    [Fact]
    public void Another_edition_is_left_for_the_listener()
    {
        var decision = Decide(Candidate("deluxe", lengths: [230, 247, 204, 285, 218, 199, 240]));

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
        Assert.Contains("track count", decision.Reason);
    }

    [Fact]
    public void A_listing_with_no_times_is_left_for_the_listener()
    {
        var decision = Decide(Candidate("1", lengths: [null, null, null, null, null]));

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
    }

    [Fact]
    public void Lengths_that_do_not_line_up_are_left_for_the_listener()
    {
        var decision = Decide(Candidate("1", lengths: [200, 280, 230, 250, 190]));

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
    }

    /// <summary>A recording that runs a few percent off every listed time is another master, or another record
    /// with tracks in proportion, not this file.</summary>
    [Fact]
    public void A_listing_the_album_runs_far_off_is_left_for_the_listener()
    {
        var decision = Decide(Candidate("1", lengths: Lengths.Select(l => (double?)(l * 1.06))));

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
    }

    [Fact]
    public void Another_artists_record_of_the_same_name_is_no_match()
    {
        var decision = Decide(Candidate("1", artist: "Someone Else"));

        Assert.Equal(LinkVerdict.NoMatch, decision.Verdict);
    }

    /// <summary>A pressing whose lengths fit better wins over one that only just fits.</summary>
    [Fact]
    public void Of_two_pressings_in_one_format_the_closer_one_is_taken()
    {
        var decision = Decide(
            Candidate("loose", lengths: Lengths.Select(l => (double?)(l + 2))),
            Candidate("exact"));

        Assert.Equal(LinkVerdict.Link, decision.Verdict);
        Assert.Equal("exact", decision.Best!.Release.Ref.Id);
    }

    // ── part of a release ──

    /// <summary>One side of a 7": the single lists two tracks, one is here, and it is on the single by its title
    /// and its length.</summary>
    [Fact]
    public void One_side_of_a_single_is_linked_to_the_single()
    {
        var single = Candidate("7in", title: "Space Age Love Song", artist: "A Flock Of Seagulls", format: "Vinyl, 7\"",
            lengths: [228, 251]);

        var decision = ReleaseLinker.Decide("A Flock Of Seagulls", "Space Age Love Song", [229], [single],
            titles: ["track 1"]);

        Assert.Equal(LinkVerdict.Link, decision.Verdict);
        Assert.True(decision.Partial);
    }

    /// <summary>An album split over folders: two of its tracks here, in any order, each on the album.</summary>
    [Fact]
    public void Part_of_an_album_is_linked_when_every_track_here_is_on_it()
    {
        var decision = ReleaseLinker.Decide("Sophie Ellis-Bextor", "Read My Lips", [285, 247], [Candidate("1")],
            titles: ["track 4", "track 2"]);

        Assert.Equal(LinkVerdict.Link, decision.Verdict);
        Assert.Contains("2 of 5", decision.Reason);
    }

    [Fact]
    public void A_track_here_that_is_not_on_the_release_leaves_the_album_for_the_listener()
    {
        var decision = ReleaseLinker.Decide("Sophie Ellis-Bextor", "Read My Lips", [285, 247], [Candidate("1")],
            titles: ["track 4", "a bonus track"]);

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
    }

    /// <summary>The same song, another version: the extended mix is not the radio edit on the single.</summary>
    [Fact]
    public void A_track_of_another_length_is_another_version_and_not_linked()
    {
        var decision = ReleaseLinker.Decide("Sophie Ellis-Bextor", "Read My Lips", [285 + 30], [Candidate("1")],
            titles: ["track 4"]);

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
    }

    /// <summary>More here than the release lists is another, longer edition: a part cannot be bigger than the whole.</summary>
    [Fact]
    public void An_album_with_more_tracks_than_the_release_is_not_linked_to_it_as_a_part()
    {
        var titles = Enumerable.Range(1, 7).Select(i => $"track {i}").ToList();
        double[] lengths = [.. Lengths, 199, 240];

        var decision = ReleaseLinker.Decide("Sophie Ellis-Bextor", "Read My Lips", lengths, [Candidate("1")], titles);

        Assert.Equal(LinkVerdict.Unsure, decision.Verdict);
    }

    /// <summary>A whole release that fits is taken before one this is only part of.</summary>
    [Fact]
    public void A_whole_release_comes_before_a_bigger_one_this_is_part_of()
    {
        var bigger = Candidate("deluxe", lengths: [230, 247, 204, 285, 218, 199, 240]);
        var decision = ReleaseLinker.Decide("Sophie Ellis-Bextor", "Read My Lips", Lengths, [bigger, Candidate("standard")],
            titles: [.. Enumerable.Range(1, 5).Select(i => $"track {i}")]);

        Assert.Equal("standard", decision.Best!.Release.Ref.Id);
        Assert.False(decision.Partial);
    }

    [Fact]
    public void Nothing_found_is_no_match() =>
        Assert.Equal(LinkVerdict.NoMatch, Decide().Verdict);

    [Theory]
    [InlineData("Sophie Ellis-Bextor", "Sophie Ellis-Bextor", true)]
    [InlineData("Madonna", "Madonna (2)", true)]
    [InlineData("Sting", "Sting*", true)]
    [InlineData("Simon & Garfunkel", "Simon And Garfunkel", true)]
    [InlineData("The Beatles", "Beatles", true)]
    [InlineData("Beyoncé", "Beyonce", true)]
    [InlineData("Earth Wind & Fire", "Earth, Wind & Fire", true)]
    [InlineData("Drake; Travis Scott", "Drake & Travis Scott", true)]
    [InlineData("Various Artists", "Various", true)]
    [InlineData("Madonna", "Kylie Minogue", false)]
    public void Artists_agree_in_any_spelling(string ours, string theirs, bool agree) =>
        Assert.Equal(agree, ReleaseLinker.ArtistsAgree(ours, theirs));

    [Theory]
    [InlineData("Read My Lips (Deluxe Version)", "Read My Lips", true)]
    [InlineData("Connect - Single", "Connect", true)]
    [InlineData("Nevermind [30th Anniversary Super Deluxe]", "Nevermind", true)]
    [InlineData("Meteora", "Hybrid Theory", false)]
    public void Titles_agree_without_their_edition_notes(string ours, string theirs, bool agree) =>
        Assert.Equal(agree, ReleaseLinker.TitlesAgree(ours, theirs));
}
