using RPM.Fingerprint.Catalog;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Which song on a record the world plays most: picking the release out of a fuzzy search, reading
/// its tracks, and deciding whether the figures name a winner or only look like they do.
///
/// <para>The JSON in here is the shape the two services answer with, taken from a lookup of LP's
/// "Lost on You" and trimmed.</para>
/// </summary>
public class TrackPopularityTests
{
    private const string Search = """
        {"count":23,"releases":[
          {"id":"732bdbf3","score":100,"title":"Lost on You","track-count":13,
           "artist-credit":[{"artist":{"id":"a1","name":"LP"}}]},
          {"id":"e6506f75","score":100,"title":"Lost on You","track-count":16,
           "artist-credit":[{"artist":{"id":"a1","name":"LP"}}]},
          {"id":"04d4dbf0","score":95,"title":"Lost on You","track-count":1,
           "artist-credit":[{"artist":{"id":"a1","name":"LP"}}]}
        ]}
        """;

    private const string Release = """
        {"id":"e6506f75","media":[{"tracks":[
          {"position":1,"title":"Muddy Waters","recording":{"id":"02fc","title":"Muddy Waters"}},
          {"position":3,"title":"Lost on You","recording":{"id":"0f9e","title":"Lost on You"}},
          {"position":16,"title":"Lost on You (live at Harvard and Stone)","recording":{"id":"7867","title":"Lost on You"}}
        ]}]}
        """;

    private static string Plays(params (string Mbid, long Listens)[] rows)
        => "[" + string.Join(",", rows.Select(r =>
            $$"""{"recording_mbid":"{{r.Mbid}}","total_listen_count":{{r.Listens}},"total_user_count":10}""")) + "]";

    // ── reading its tracks ──

    [Fact]
    public void The_tracks_come_back_with_their_recordings()
    {
        var tracks = TrackPopularity.Tracks(Release);

        Assert.Equal(3, tracks.Count);
        Assert.Equal(new ReleaseTrack(1, "Muddy Waters", "02fc"), tracks[0]);
        Assert.Equal(3, tracks[1].Position);
    }

    [Fact]
    public void A_live_take_keeps_the_name_the_sleeve_gives_it()
    {
        // The recording underneath is called "Lost on You"; the track is not. Taking the recording's
        // name would put the same title on the list twice with nothing to tell them apart.
        Assert.Equal(
            "Lost on You (live at Harvard and Stone)",
            TrackPopularity.Tracks(Release).Single(t => t.RecordingMbid == "7867").Title);
    }

    [Fact]
    public void An_unreadable_release_is_no_tracks_rather_than_a_fault()
    {
        Assert.Empty(TrackPopularity.Tracks("not json"));
        Assert.Empty(TrackPopularity.Tracks("{}"));
    }

    // ── the figures ──

    [Fact]
    public void The_plays_are_read_per_recording()
    {
        var plays = TrackPopularity.Plays(Plays(("0f9e", 29870), ("02fc", 4611)));

        Assert.Equal(29870, plays.Single(p => p.RecordingMbid == "0f9e").Listens);
        Assert.Equal(10, plays[0].Listeners);
    }

    [Fact]
    public void The_body_names_each_recording_once()
    {
        var body = TrackPopularity.PlaysBody(["a", "b", "a", "", "  "]);

        Assert.Equal("""{"recording_mbids":["a","b"]}""", body);
    }

    // ── the ids the services count under ──

    private const string Canonical = """
        [{"recording_name":"Sultans of Swing",
          "original_recording_mbid":"5e26","canonical_recording_mbid":"66a1"},
         {"recording_name":"Down to the Waterline",
          "original_recording_mbid":"39d8","canonical_recording_mbid":"39d8"}]
        """;

    [Fact]
    public void The_mapping_is_read_from_what_was_asked_rather_than_the_order_it_came_back_in()
    {
        var map = TrackPopularity.Canonical(Canonical);

        Assert.Equal("66a1", map["5e26"]);
        Assert.Equal("39d8", map["39d8"]);
    }

    [Fact]
    public void The_body_asks_for_one_object_per_recording()
        => Assert.Equal(
            """[{"recording_mbid":"a"},{"recording_mbid":"b"}]""",
            TrackPopularity.CanonicalBody(["a", "b", "a"]));

    [Fact]
    public void A_track_is_pointed_at_the_recording_its_plays_are_counted_under()
    {
        var tracks = new[] { new ReleaseTrack(6, "Sultans of Swing", "5e26") };

        var counted = TrackPopularity.Canonicalised(tracks, TrackPopularity.Canonical(Canonical));

        Assert.Equal("66a1", counted[0].RecordingMbid);
        Assert.Equal("Sultans of Swing", counted[0].Title);   // the sleeve's name is untouched
    }

    [Fact]
    public void A_recording_the_mapping_never_answered_for_keeps_its_own_id()
    {
        var tracks = new[] { new ReleaseTrack(1, "Lions", "1a26") };

        Assert.Equal("1a26", TrackPopularity.Canonicalised(tracks, TrackPopularity.Canonical(Canonical))[0].RecordingMbid);
    }

    [Fact]
    public void An_unreadable_mapping_leaves_every_track_as_it_was()
    {
        var tracks = new[] { new ReleaseTrack(6, "Sultans of Swing", "5e26") };

        Assert.Empty(TrackPopularity.Canonical("not json"));
        Assert.Equal("5e26", TrackPopularity.Canonicalised(tracks, TrackPopularity.Canonical("not json"))[0].RecordingMbid);
    }

    [Fact]
    public void The_pressing_s_own_id_is_not_the_one_the_world_scrobbles()
    {
        // The case this step exists for, on the 1978 Dire Straits album: asked about the release's own
        // recording of "Sultans of Swing" the answer is 43 listens, and the star goes to whichever album
        // track happens to lead. Asked about the canonical recording it is 1.6m.
        var tracks = new[]
        {
            new ReleaseTrack(1, "Down to the Waterline", "39d8"),
            new ReleaseTrack(6, "Sultans of Swing", "5e26"),
        };
        var plays = TrackPopularity.Plays(Plays(("39d8", 276_940), ("5e26", 43), ("66a1", 1_646_993)));

        Assert.Equal("Down to the Waterline", TrackPopularity.MostPlayed(tracks, plays, TrackPopularity.AlbumPageLead));

        var counted = TrackPopularity.Canonicalised(tracks, TrackPopularity.Canonical(Canonical));

        Assert.Equal("Sultans of Swing", TrackPopularity.MostPlayed(counted, plays, TrackPopularity.AlbumPageLead));
    }

    // ── the same choice over plain counts, whoever counted them ──

    [Fact]
    public void A_song_that_leads_by_enough_is_named()
        => Assert.Equal("Royals", TrackPopularity.MostPlayedOf(
            [("Royals", 0, 400), ("Team", 1, 120)], TrackPopularity.AlbumPageLead, minimum: 5));

    [Fact]
    public void A_room_too_small_to_have_an_opinion_names_nothing()
    {
        // RPM's own listeners start at nothing. Without this, the first person to finish a record's
        // second track would make it the most played by two plays to nought, which only describes one
        // evening.
        Assert.Null(TrackPopularity.MostPlayedOf(
            [("Second track", 1, 2), ("First track", 0, 0)],
            TrackPopularity.AlbumPageLead, TrackPopularity.RpmListenersMinimum));

        Assert.Equal("Second track", TrackPopularity.MostPlayedOf(
            [("Second track", 1, 9), ("First track", 0, 0)],
            TrackPopularity.AlbumPageLead, TrackPopularity.RpmListenersMinimum));
    }

    [Fact]
    public void A_near_tie_names_nothing_however_many_plays_it_has()
        => Assert.Null(TrackPopularity.MostPlayedOf(
            [("One", 0, 1000), ("Two", 1, 900)], TrackPopularity.AlbumPageLead, minimum: 5));

    [Fact]
    public void Two_songs_level_go_to_the_one_pressed_first()
        => Assert.Equal("First", TrackPopularity.MostPlayedOf(
            [("Second", 1, 500), ("First", 0, 500)], lead: 0, minimum: 1));

    [Fact]
    public void Nothing_counted_is_nothing_named()
        => Assert.Null(TrackPopularity.MostPlayedOf([], TrackPopularity.AlbumPageLead, minimum: 1));

    // ── and the answer ──

    [Fact]
    public void The_song_the_world_plays_is_the_one_that_is_marked()
    {
        var title = TrackPopularity.MostPlayed(
            TrackPopularity.Tracks(Release),
            TrackPopularity.Plays(Plays(("0f9e", 29870), ("02fc", 4611), ("7867", 1169))),
            TrackPopularity.AlbumPageLead);

        Assert.Equal("Lost on You", title);
    }

    [Fact]
    public void A_record_with_no_clear_favourite_gets_no_star()
    {
        // Three songs within a few plays of each other is a record people play through, and marking one
        // of them would be reporting noise as a fact.
        Assert.Null(TrackPopularity.MostPlayed(
            TrackPopularity.Tracks(Release),
            TrackPopularity.Plays(Plays(("0f9e", 1010), ("02fc", 1000), ("7867", 990))),
            TrackPopularity.AlbumPageLead));
    }

    [Fact]
    public void A_record_nobody_there_has_played_gets_no_star()
    {
        Assert.Null(TrackPopularity.MostPlayed(TrackPopularity.Tracks(Release), [], TrackPopularity.AlbumPageLead));
        Assert.Null(TrackPopularity.MostPlayed(
            TrackPopularity.Tracks(Release),
            TrackPopularity.Plays(Plays(("0f9e", 0), ("02fc", 0))),
            TrackPopularity.AlbumPageLead));
    }

    [Fact]
    public void One_song_played_and_the_rest_not_at_all_is_still_an_answer()
    {
        Assert.Equal("Muddy Waters", TrackPopularity.MostPlayed(
            TrackPopularity.Tracks(Release),
            TrackPopularity.Plays(Plays(("02fc", 40))),
            TrackPopularity.AlbumPageLead));
    }

    [Fact]
    public void A_recording_the_figures_do_not_mention_counts_as_unplayed()
    {
        // ListenBrainz answers about what it knows. A missing row means nobody has played the track, so
        // it is a count of zero, not missing data.
        Assert.Equal("Lost on You", TrackPopularity.MostPlayed(
            TrackPopularity.Tracks(Release),
            TrackPopularity.Plays(Plays(("0f9e", 500))),
            TrackPopularity.AlbumPageLead));
    }
}
