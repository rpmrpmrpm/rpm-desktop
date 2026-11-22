using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Analysis;

/// <summary>
/// The explicit flag song by song, for the songs of a record that the release listing gave no flag.
///
/// <para><b>Where the answer comes from.</b> The rpm API's advisory route (<c>GET metadata/advisory</c>). The
/// server matches each song against the iTunes store by title, act and length, and says explicit, not explicit,
/// or not known. A store song marked "cleaned" counts as explicit there: the store marks a song so only when it
/// is the edited version of an explicit song.</para>
///
/// <para><b>How it is asked.</b> First the whole record in one request, by album. Then, for the songs still
/// unknown, one request per song by artist and title, at most <see cref="MostSongSearches"/> for a record and
/// <see cref="SongBreath"/> apart. The store allows each address only about twenty requests a minute, and the
/// server asks it for every listener.</para>
///
/// <para><b>What is changed.</b> The same rules as for the listing: only a song with no flag, or with Clean, and
/// only ever to Explicit. A song marked Clean is changed only when its length is known, so the server matched it by
/// title and length both (within three seconds). Not explicit and not known change nothing.</para>
/// </summary>
public static partial class AdvisoryFill
{
    /// <summary>How many songs of one record are asked about one by one, after the request for the whole
    /// record.</summary>
    public const int MostSongSearches = 8;

    /// <summary>The wait before each request for one song.</summary>
    public static readonly TimeSpan SongBreath = TimeSpan.FromSeconds(3);

    /// <summary>How many songs the API takes in one request.</summary>
    public const int MostSongsAsked = 100;

    /// <summary>What the API said about a record's songs.</summary>
    /// <param name="Explicit">One entry per song given to <see cref="AskStoreAsync"/>, in the same order: true
    /// explicit, false not explicit, null not known or not asked.</param>
    /// <param name="Whole">False when a request for one song was not answered, or the wait before it was cut
    /// short. What came before is still in <paramref name="Explicit"/>.</param>
    public sealed record StoreAnswers(IReadOnlyList<bool?> Explicit, bool Whole);

    /// <summary>A queued file as the match reads it.</summary>
    public static Song SongOf(TrackInfo file)
        => new(file.Title, file.DurationSeconds, file.DiscNumber, file.TrackNumber, file.Advisory, file.Artist);

    /// <summary>A library row as the match reads it.</summary>
    public static Song SongOf(LibraryTrack row)
        => new(row.Title, row.DurationSeconds, row.DiscNumber, row.TrackNumber,
               row.Advisory ?? ContentAdvisory.Unknown, row.Artist);

    /// <summary>
    /// Asks the API about the songs of one record whose flag this fill may change. The others are not asked about
    /// and answer null.
    /// </summary>
    /// <param name="wait">Waits before each request for one song, and says whether to go on. False stops the
    /// asking and makes the answer not whole. The app passes a delay that also checks the pass may still run.</param>
    /// <returns><see cref="Asked{T}.Unanswered"/> when the request for the whole record was not answered.</returns>
    public static async Task<Asked<StoreAnswers>> AskStoreAsync(
        IRpmMetadataApi api, string artist, string album, IReadOnlyList<Song> songs,
        Func<TimeSpan, CancellationToken, Task<bool>> wait, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(wait);

        var answers = new bool?[songs.Count];
        var asked = Enumerable.Range(0, songs.Count)
            .Where(i => Wants(songs[i].Advisory) && TitleKey(songs[i].Title).Length > 0)
            .Take(MostSongsAsked)
            .ToList();
        if (asked.Count == 0 || string.IsNullOrWhiteSpace(artist))
            return Asked.Answer(new StoreAnswers(answers, true));

        if (!(await api.GetAdvisoryAsync(artist, album, [.. asked.Select(i => Wire(songs[i]))], ct))
                .Answers(out var byAlbum, out var why))
            return Asked.Failed<StoreAnswers>(why);

        // One answer per song asked, in the order asked. A different count is not an answer to this question.
        if (byAlbum.Songs.Count == asked.Count)
            for (var k = 0; k < asked.Count; k++)
                answers[asked[k]] = byAlbum.Songs[k].Explicit;

        var searched = 0;
        foreach (var i in asked)
        {
            if (answers[i] is not null)
                continue;
            if (searched == MostSongSearches)
                break;
            if (!await wait(SongBreath, ct))
                return Asked.Answer(new StoreAnswers(answers, false));

            searched++;
            var who = string.IsNullOrWhiteSpace(songs[i].Artist) ? artist : songs[i].Artist!;
            if (!(await api.GetAdvisoryAsync(who, null, [Wire(songs[i])], ct)).Answers(out var one, out _))
                return Asked.Answer(new StoreAnswers(answers, false));
            answers[i] = one.Songs.Count == 1 ? one.Songs[0].Explicit : null;
        }

        return Asked.Answer(new StoreAnswers(answers, true));

        static (string Title, int Seconds) Wire(Song song)
            => (song.Title!.Trim(), song.Seconds > 0 ? (int)Math.Round(song.Seconds) : 0);
    }

    /// <summary>
    /// The flag a song should have from what the API said about it song by song, or null to leave it as it is.
    /// </summary>
    /// <param name="explicitInStore">The API's answer for the song: true explicit, false not, null not known.</param>
    public static ContentAdvisory? DecideBySong(Song song, bool? explicitInStore)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (explicitInStore != true || !Wants(song.Advisory))
            return null;

        // A song marked Clean only on a strong match: without a length, the server matched the title alone.
        if (song.Advisory == ContentAdvisory.Clean && song.Seconds <= 0)
            return null;

        return ContentAdvisory.Explicit;
    }

    /// <summary>
    /// One queued file's wanted tags with the flag filled from the API's song-by-song answer, or the same instance
    /// when nothing changed. A flag set in the queue is left alone, as in <see cref="Fill"/>.
    /// </summary>
    public static TrackInfo FillBySong(TrackInfo wanted, TrackInfo current, bool? explicitInStore)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(current);
        if (wanted.Advisory != current.Advisory)
            return wanted;

        return DecideBySong(SongOf(wanted), explicitInStore) is { } flag && flag != wanted.Advisory
            ? wanted with { Advisory = flag, TagsEdited = true }
            : wanted;
    }

    /// <summary>The changes the API's song-by-song answers give for library rows. <paramref name="answers"/> has
    /// one entry per row, in the same order. Rows named in <paramref name="leave"/> (by relative path) are not
    /// changed.</summary>
    public static IReadOnlyList<Change> PlanBySong(
        IReadOnlyList<LibraryTrack> rows, IReadOnlyList<bool?> answers, IReadOnlySet<string>? leave = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(answers);

        var changes = new List<Change>();
        for (var i = 0; i < rows.Count && i < answers.Count; i++)
        {
            var row = rows[i];
            if (leave?.Contains(row.RelativePath) == true)
                continue;
            var was = row.Advisory ?? ContentAdvisory.Unknown;
            if (DecideBySong(SongOf(row), answers[i]) is { } flag && flag != was)
                changes.Add(new Change(row, was, flag));
        }
        return changes;
    }
}
