using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The explicit flag song by song, for the songs the release listing gave none: how the API is asked (the whole
/// record first, then single songs, a limited number and a pause apart), and what may change (no flag, and Clean
/// only with a known length, and only ever to Explicit).
/// </summary>
public class AdvisoryFillBySongTests
{
    private const ContentAdvisory E = ContentAdvisory.Explicit;
    private const ContentAdvisory C = ContentAdvisory.Clean;
    private const ContentAdvisory U = ContentAdvisory.Unknown;

    private static AdvisoryFill.Song Song(string title, double seconds = 0, ContentAdvisory advisory = U, string? artist = null)
        => new(title, seconds, 0, 0, advisory, artist);

    // ── what may change ──

    [Fact]
    public void A_song_with_no_flag_the_store_says_is_explicit_becomes_explicit()
    {
        Assert.Equal(E, AdvisoryFill.DecideBySong(Song("Crime Wave", 224), true));
        Assert.Equal(E, AdvisoryFill.DecideBySong(Song("Crime Wave", 0), true));
    }

    [Fact]
    public void Not_explicit_and_not_known_change_nothing()
    {
        Assert.Null(AdvisoryFill.DecideBySong(Song("Shake It Off", 219), false));
        Assert.Null(AdvisoryFill.DecideBySong(Song("Flight 187", 251), null));
    }

    /// <summary>Clean is changed only on a strong match: the server matched the title and the length, which it
    /// can only do when the length was sent.</summary>
    [Fact]
    public void A_clean_song_becomes_explicit_only_when_its_length_is_known()
    {
        Assert.Equal(E, AdvisoryFill.DecideBySong(Song("Crime Wave", 224, C), true));
        Assert.Null(AdvisoryFill.DecideBySong(Song("Crime Wave", 0, C), true));
    }

    [Fact]
    public void Nothing_is_made_clean_and_explicit_is_never_taken_away()
    {
        Assert.Null(AdvisoryFill.DecideBySong(Song("Crime Wave", 224, E), false));
        Assert.Null(AdvisoryFill.DecideBySong(Song("Crime Wave", 224, E), null));
        Assert.Null(AdvisoryFill.DecideBySong(Song("Crime Wave", 224, U), false));
    }

    [Fact]
    public void A_queued_file_whose_flag_was_set_in_the_queue_is_left_alone()
    {
        var current = new TrackInfo { FilePath = "/q/7.flac", Extension = ".flac", Title = "Crime Wave", DurationSeconds = 224 };
        var picked = current with { Advisory = C };

        Assert.Same(picked, AdvisoryFill.FillBySong(picked, current, true));

        var filled = AdvisoryFill.FillBySong(current, current, true);
        Assert.Equal(E, filled.Advisory);
        Assert.True(filled.TagsEdited);

        Assert.Same(current, AdvisoryFill.FillBySong(current, current, null));
    }

    private static LibraryTrack Row(string title, uint track, double seconds, ContentAdvisory? advisory = null)
        => new()
        {
            RelativePath = $"50 Cent/Before I Self Destruct/{track:00} {title}.mp3",
            Folder = "50 Cent/Before I Self Destruct",
            FileName = $"{track:00} {title}.mp3",
            Extension = ".mp3",
            Title = title,
            Artist = "50 Cent",
            AlbumArtist = "50 Cent",
            Album = "Before I Self Destruct",
            TrackNumber = track,
            DurationSeconds = seconds,
            Advisory = advisory,
        };

    [Fact]
    public void A_plan_names_only_the_songs_that_change_and_leaves_the_ones_it_is_told_to()
    {
        var rows = new[]
        {
            Row("Crime Wave", 7, 224), Row("Stretch", 8, 248, C), Row("Strong Enough", 9, 0, C),
            Row("Get It Hot", 10, 179), Row("Flight 187", 17, 251),
        };
        var answers = new bool?[] { true, true, true, true, null };

        var plan = AdvisoryFill.PlanBySong(rows, answers, new HashSet<string> { rows[3].RelativePath });

        Assert.Equal(["Crime Wave", "Stretch"], plan.Select(c => c.Row.Title));
        Assert.All(plan, c => Assert.Equal(E, c.Becomes));
        Assert.Equal(C, plan[1].Was);
    }

    // ── how the API is asked ──

    private static readonly AdvisoryFill.Song[] BeforeISelfDestruct =
    [
        Song("The Invitation", 174), Song("Crime Wave", 224, C), Song("Stretch", 248, E),
        Song("Flight 187", 251), Song("Man’s World", 171, artist: "50 Cent feat. Hayes"),
    ];

    [Fact]
    public async Task The_record_is_asked_about_first_and_the_songs_it_left_unknown_one_by_one()
    {
        var api = new AdvisoryApi
        {
            ByAlbum = songs => [.. songs.Select(s => s.Title.StartsWith("Flight") || s.Title.StartsWith("Man") ? (bool?)null : true)],
            BySong = (_, song) => song.Title == "Flight 187" ? false : null,
        };
        var waits = new List<TimeSpan>();

        var asked = await AdvisoryFill.AskStoreAsync(api, "50 Cent", "Before I Self Destruct", BeforeISelfDestruct,
            (wait, _) => { waits.Add(wait); return Task.FromResult(true); });

        Assert.True(asked.Answers(out var store, out _));
        Assert.True(store.Whole);
        Assert.Equal([true, true, null, false, null], store.Explicit);

        // The song already marked explicit is not asked about. Lengths go as whole seconds.
        var (artist, album, songs) = api.Calls[0];
        Assert.Equal(("50 Cent", "Before I Self Destruct"), (artist, album));
        Assert.Equal([("The Invitation", 174), ("Crime Wave", 224), ("Flight 187", 251), ("Man’s World", 171)], songs);

        // Then each unknown song on its own, under its own artist when it has one, a pause before each.
        Assert.Equal(("50 Cent", (string?)null, ("Flight 187", 251)), (api.Calls[1].Artist, api.Calls[1].Album, api.Calls[1].Songs.Single()));
        Assert.Equal("50 Cent feat. Hayes", api.Calls[2].Artist);
        Assert.Equal(3, api.Calls.Count);
        Assert.Equal([AdvisoryFill.SongBreath, AdvisoryFill.SongBreath], waits);
    }

    [Fact]
    public async Task No_more_than_the_limit_of_songs_are_asked_about_one_by_one()
    {
        var songs = Enumerable.Range(1, AdvisoryFill.MostSongSearches + 5).Select(i => Song($"Song {i}", 200)).ToList();
        var api = new AdvisoryApi();

        var asked = await AdvisoryFill.AskStoreAsync(api, "Somebody", "Something", songs, (_, _) => Task.FromResult(true));

        Assert.True(asked.Answers(out var store, out _));
        Assert.True(store.Whole);
        Assert.Equal(1 + AdvisoryFill.MostSongSearches, api.Calls.Count);
    }

    [Fact]
    public async Task A_record_the_api_did_not_answer_is_unanswered()
    {
        var api = new AdvisoryApi { Fails = _ => true };

        var asked = await AdvisoryFill.AskStoreAsync(api, "50 Cent", "Before I Self Destruct", BeforeISelfDestruct,
            (_, _) => Task.FromResult(true));

        Assert.False(asked.Answers(out _, out _));
    }

    /// <summary>A single song not answered stops the asking. What came before is kept, and the answer says it
    /// is not whole, so the pass stops.</summary>
    [Fact]
    public async Task A_song_the_api_did_not_answer_stops_the_asking_and_keeps_what_came_before()
    {
        var api = new AdvisoryApi
        {
            ByAlbum = songs => [.. songs.Select(s => s.Title == "Crime Wave" ? true : (bool?)null)],
            Fails = album => album is null,
        };

        var asked = await AdvisoryFill.AskStoreAsync(api, "50 Cent", "Before I Self Destruct", BeforeISelfDestruct,
            (_, _) => Task.FromResult(true));

        Assert.True(asked.Answers(out var store, out _));
        Assert.False(store.Whole);
        Assert.Equal([null, true, null, null, null], store.Explicit);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task A_wait_that_says_stop_ends_the_asking()
    {
        var api = new AdvisoryApi();

        var asked = await AdvisoryFill.AskStoreAsync(api, "50 Cent", "Before I Self Destruct", BeforeISelfDestruct,
            (_, _) => Task.FromResult(false));

        Assert.True(asked.Answers(out var store, out _));
        Assert.False(store.Whole);
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task A_record_with_nothing_to_fill_is_not_asked_about()
    {
        var api = new AdvisoryApi();

        var asked = await AdvisoryFill.AskStoreAsync(api, "50 Cent", "Before I Self Destruct",
            [Song("Stretch", 248, E)], (_, _) => Task.FromResult(true));

        Assert.True(asked.Answers(out var store, out _));
        Assert.Equal([(bool?)null], store.Explicit);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task An_answer_with_another_number_of_songs_is_not_read()
    {
        var api = new AdvisoryApi { ByAlbum = _ => [true] };

        var asked = await AdvisoryFill.AskStoreAsync(api, "50 Cent", "Before I Self Destruct",
            [Song("Crime Wave", 224), Song("Stretch", 248)], (_, _) => Task.FromResult(false));

        Assert.True(asked.Answers(out var store, out _));
        Assert.Equal([null, null], store.Explicit);
    }

    /// <summary>The API as the fill sees it, with each answer stated per test.</summary>
    private sealed class AdvisoryApi : IRpmMetadataApi
    {
        public Func<IReadOnlyList<(string Title, int Seconds)>, bool?[]> ByAlbum { get; init; } = songs => new bool?[songs.Count];
        public Func<string, (string Title, int Seconds), bool?> BySong { get; init; } = (_, _) => null;
        public Func<string?, bool> Fails { get; init; } = _ => false;

        public List<(string Artist, string? Album, IReadOnlyList<(string Title, int Seconds)> Songs)> Calls { get; } = [];

        public bool IsAvailable => true;

        public Task<Asked<CatalogAdvisoryAnswer>> GetAdvisoryAsync(
            string artist, string? album, IReadOnlyList<(string Title, int Seconds)> songs, CancellationToken ct = default)
        {
            Calls.Add((artist, album, songs));
            if (Fails(album))
                return Task.FromResult(Asked.Failed<CatalogAdvisoryAnswer>("HTTP 503"));
            var answers = album is null ? [BySong(artist, songs[0])] : ByAlbum(songs);
            return Task.FromResult(Asked.Answer(new CatalogAdvisoryAnswer(
                [.. answers.Select((a, i) => new CatalogAdvisorySong(songs[Math.Min(i, songs.Count - 1)].Title, 0, a))])));
        }

        public Task<Asked<IReadOnlyList<CatalogRelease>>> SearchAsync(
            string text, string? artist, int limit, ReleaseMedia media,
            string? catalogNumber, string? country, uint? year, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogPapers>> GetPapersAsync(string artist, string? album, int tracks, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<IReadOnlyList<CatalogArtist>>> GetArtistsNamedAsync(string name, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogArtist?>> GetArtistAsync(string mbid, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogPlays>> GetPlaysAsync(
            string artist, string album, int tracks, string? releaseMbid, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogDisc?>> LookupDiscAsync(string discId, string? toc, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<string?>> GetCoverUrlAsync(string releaseId, int maxSize, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogSongCover>> GetSongCoverAsync(string artist, string title, int size, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
