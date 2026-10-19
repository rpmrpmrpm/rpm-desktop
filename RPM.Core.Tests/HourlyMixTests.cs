using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// "Right now": songs the listener likes and plays, weighted towards the genres they play at this
/// time of day.
///
/// <para>Tested here because it is the only generated list that is a RANKING rather than a filter,
/// and a ranking cannot be checked on screen: a list with the wrong weights and a list with the
/// right weights both look like fifty songs the user owns. See <see cref="HourlyMix"/>.</para>
/// </summary>
public class HourlyMixTests
{
    private static MixSong Song(
        string path, string folder, bool favorite = false, int plays = 0, long lastPlayed = 0) =>
        new(path, folder, favorite, plays, lastPlayed);

    private static GenreShare Kind(string genre, double share) =>
        new(genre, new ListeningTally((int)(share * 100), 0, share * 1000), share);

    /// <summary>One album per song, so the per-album cap and the turn-taking have no effect and only
    /// the ranking is tested.</summary>
    private static Dictionary<string, string> Filed(params (string Folder, string Genre)[] rows) =>
        rows.ToDictionary(r => r.Folder, r => r.Genre, StringComparer.OrdinalIgnoreCase);

    // ── the one hard rule ──

    /// <summary>A song with no heart and no plays is not in the list, however well its genre suits the
    /// hour. Without this rule the list would be a genre shuffle under a personal heading.</summary>
    [Fact]
    public void A_song_the_listener_has_never_touched_is_not_in_it()
    {
        var songs = new[] { Song("a/1.flac", "a") };

        Assert.Empty(HourlyMix.Songs(
            songs, Filed(("a", "Jazz")), [Kind("Jazz", 1.0)], 10, 1));
    }

    /// <summary>A heart alone is enough, with no plays: it is the clearest signal the listener gives
    /// about a song.</summary>
    [Fact]
    public void A_song_that_was_only_loved_is_in_it()
    {
        var songs = new[] { Song("a/1.flac", "a", favorite: true) };

        Assert.Equal(
            ["a/1.flac"],
            HourlyMix.Songs(songs, Filed(), [], 10, 1));
    }

    /// <summary>Plays alone are also enough, with no heart: most people never use the heart.</summary>
    [Fact]
    public void A_song_that_was_only_played_is_in_it()
    {
        var songs = new[] { Song("a/1.flac", "a", plays: 3) };

        Assert.Equal(
            ["a/1.flac"],
            HourlyMix.Songs(songs, Filed(), [], 10, 1));
    }

    // ── the weight of the hour ──

    /// <summary>The main rule of the list. Of two songs the listener likes equally, the one in the
    /// genre they play at this hour comes first. A list that ignored the hour would be the same as the
    /// favourites row.</summary>
    [Fact]
    public void The_kind_of_music_they_play_now_leads()
    {
        var songs = new[]
        {
            Song("wrong/1.flac", "wrong", favorite: true),
            Song("right/1.flac", "right", favorite: true),
        };

        Assert.Equal(
            ["right/1.flac", "wrong/1.flac"],
            HourlyMix.Songs(
                songs,
                Filed(("right", "Jazz"), ("wrong", "Metal")),
                [Kind("Jazz", 0.9)],
                10, 1));
    }

    /// <summary>The hour does not decide on its own. A favourite in the wrong genre for the hour
    /// still ranks above a song played once in the right genre: the hour adds weight, it does not
    /// filter.</summary>
    [Fact]
    public void The_hour_leans_the_list_rather_than_choosing_it()
    {
        var songs = new[]
        {
            Song("loved/1.flac", "loved", favorite: true),
            Song("fits/1.flac", "fits", plays: 1),
        };

        Assert.Equal(
            ["loved/1.flac", "fits/1.flac"],
            HourlyMix.Songs(
                songs,
                Filed(("fits", "Jazz"), ("loved", "Metal")),
                [Kind("Jazz", 0.5)],
                10, 1));
    }

    /// <summary>A listener with no history at this time of day, or a collection with no genre tags,
    /// gets the same list ranked by heart and plays. Having no hour data is normal (for example on a
    /// new install), not a failure.</summary>
    [Fact]
    public void With_nothing_known_about_the_hour_it_is_still_a_list()
    {
        var songs = new[]
        {
            Song("a/1.flac", "a", plays: 2),
            Song("b/1.flac", "b", favorite: true),
        };

        Assert.Equal(
            ["b/1.flac", "a/1.flac"],
            HourlyMix.Songs(songs, Filed(), [], 10, 1));
    }

    // ── the weight of a play ──

    /// <summary>Above the ceiling a song scores no higher. The difference between forty plays and four
    /// hundred mostly shows how long a song has been in the collection, not how much it is wanted now,
    /// so the hour still decides between them.</summary>
    [Fact]
    public void Plays_stop_counting_above_the_ceiling()
    {
        var songs = new[]
        {
            Song("huge/1.flac", "huge", plays: HourlyMix.PlaysForFull * 40),
            Song("plenty/1.flac", "plenty", plays: HourlyMix.PlaysForFull),
        };

        // Equal on plays, so the one that suits the hour comes first. Without the ceiling the song
        // with four hundred plays would always come first.
        Assert.Equal(
            ["plenty/1.flac", "huge/1.flac"],
            HourlyMix.Songs(
                songs,
                Filed(("plenty", "Jazz"), ("huge", "Metal")),
                [Kind("Jazz", 0.4)],
                10, 1));
    }

    // ── how much of one album ──

    /// <summary>One album cannot fill the list. Otherwise somebody who likes one album would get only
    /// that album, which is not a mix and is already one click away in the library.</summary>
    [Fact]
    public void One_record_cannot_fill_the_list()
    {
        var songs = Enumerable.Range(1, 20)
            .Select(i => Song($"a/{i:D2}.flac", "a", favorite: true))
            .Concat([Song("b/1.flac", "b", plays: 1)])
            .ToList();

        var row = HourlyMix.Songs(songs, Filed(), [], 10, 1);

        Assert.Equal(2, row.Count);
        Assert.Contains("b/1.flac", row);
    }

    /// <summary>The albums take turns, so a listener who likes three albums does not get all of the
    /// first before the second starts. The decade charts use the same rule.</summary>
    [Fact]
    public void The_records_take_turns()
    {
        var songs = new[]
        {
            Song("a/1.flac", "a", favorite: true, plays: 9),
            Song("a/2.flac", "a", favorite: true, plays: 8),
            Song("b/1.flac", "b", favorite: true, plays: 7),
            Song("b/2.flac", "b", favorite: true, plays: 6),
        };

        Assert.Equal(
            ["a/1.flac", "b/1.flac", "a/2.flac", "b/2.flac"],
            HourlyMix.Songs(songs, Filed(), [], 10, 2));
    }

    // ── it is stable ──

    /// <summary>Two reads of an unchanged shelf give the same list in the same order. A generated
    /// list that reorders when the user has done nothing looks like a bug.</summary>
    [Fact]
    public void Two_readings_of_the_same_shelf_agree()
    {
        var songs = Enumerable.Range(1, 30)
            .Select(i => Song($"r{i:D2}/1.flac", $"r{i:D2}", favorite: i % 3 == 0, plays: i % 7))
            .ToList();

        var filed = Filed([.. songs.Select(s => (s.Folder, i: 0))
                                   .Select((s, i) => (s.Folder, i % 2 == 0 ? "Jazz" : "Metal"))]);

        Assert.Equal(
            HourlyMix.Songs(songs, filed, [Kind("Jazz", 0.6)], 10, 1),
            HourlyMix.Songs(songs, filed, [Kind("Jazz", 0.6)], 10, 1));
    }

    /// <summary>Asking for nothing gives nothing, rather than the whole shelf.</summary>
    [Fact]
    public void Asking_for_no_songs_gives_none()
        => Assert.Empty(HourlyMix.Songs(
            [Song("a/1.flac", "a", favorite: true)], Filed(), [], 0, 1));
}
