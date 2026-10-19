using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>One track as <see cref="LibraryDatabase.ReadRadioCrate"/> reads it, before the mood readings are turned
/// into percentiles.</summary>
/// <param name="Artist">The credit, as <see cref="RecommendRefusals.CreditOf"/> composes it. Empty when nothing names
/// an act.</param>
/// <param name="Year">Year of release, or 0 when the tags do not say.</param>
/// <param name="Brightness">The mood meter's three readings, or null when the track has not been measured.</param>
/// <param name="Offered">Whether a radio may pick it: at least <see cref="Moods.LeastSeconds"/> long and playable from
/// this computer. A track that is not offered can still be the song a radio starts from.</param>
/// <param name="Bpm">The tempo, or null where nothing says: the analyser's measurement where it is confident, else the
/// file's tag. Whichever octave it was counted in; <see cref="SongRadio.TempoApart"/> does not care.</param>
public sealed record RadioRow(
    string RelativePath, string Folder, string Artist, string Title, int Year,
    double? Brightness, double? Onsets, double? Dynamics, bool Offered, double? Bpm);

/// <summary>A song's three mood readings as percentiles of the collection, each from 0 to 1. Percentiles because the
/// readings are in different units (a share of energy, events a second, decibels), and unscaled the decibels would
/// decide every comparison.</summary>
public readonly record struct RadioSound(double Brightness, double Onsets, double Dynamics);

/// <summary>One song a radio can play or start from.</summary>
/// <param name="Song">The artist and the title with editions and featured credits removed, folded to lower case. Two
/// rows with the same value are the same song on two records (an album and its remaster, or a best-of).</param>
/// <param name="Genre">The record's shelf, as <see cref="MoodBands.GenreByFolder"/> files it, or null.</param>
/// <param name="Sound">Null when the song has not been measured.</param>
/// <param name="Moods">The mood lists the song is in this session (<see cref="MoodShelf.MoodsByPath"/>). Empty for a
/// measured song in none of them, which is most songs.</param>
/// <param name="Bpm">The tempo, or null. See <see cref="RadioRow.Bpm"/>.</param>
public sealed record RadioSong(
    string Path, string Folder, string Artist, string Song, int Year, string? Genre,
    RadioSound? Sound, IReadOnlySet<SmartPlaylistKind> Moods, bool Offered, double? Bpm);

/// <summary>
/// Every song in one collection that a song radio can pick from or start from. Read once when a radio starts and
/// held by it, so picking the next songs never reads the database.
/// </summary>
public sealed class RadioCrate
{
    private static readonly IReadOnlySet<SmartPlaylistKind> NoMoods = new HashSet<SmartPlaylistKind>();

    private readonly Dictionary<string, RadioSong> _byPath;

    private RadioCrate(IReadOnlyList<RadioSong> songs)
    {
        Songs = songs;
        _byPath = new Dictionary<string, RadioSong>(songs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
            _byPath[song.Path] = song;
    }

    /// <summary>A collection that could not be read. A radio on it plays the song it was started from and nothing
    /// after.</summary>
    public static readonly RadioCrate Empty = new([]);

    public IReadOnlyList<RadioSong> Songs { get; }

    /// <summary>The song at a relative path, or null when the crate does not hold it.</summary>
    public RadioSong? Find(string relativePath) => _byPath.GetValueOrDefault(relativePath);

    /// <param name="genreByFolder">The shelf each record is filed under, from
    /// <see cref="MoodBands.GenreByFolder"/>.</param>
    /// <param name="moodsByPath">The mood lists each song is in, from <see cref="MoodShelf.MoodsByPath"/>.</param>
    public static RadioCrate Build(
        IReadOnlyList<RadioRow> rows,
        IReadOnlyDictionary<string, string> genreByFolder,
        IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> moodsByPath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(genreByFolder);
        ArgumentNullException.ThrowIfNull(moodsByPath);

        // Percentiles are taken over the measured songs only. An unmeasured song has no place on any axis.
        var measured = new List<int>();
        for (var i = 0; i < rows.Count; i++)
            if (IsMeasured(rows[i]))
                measured.Add(i);

        var brightness = Percentiles([.. measured.Select(i => rows[i].Brightness!.Value)]);
        var onsets = Percentiles([.. measured.Select(i => rows[i].Onsets!.Value)]);
        var dynamics = Percentiles([.. measured.Select(i => rows[i].Dynamics!.Value)]);

        var sounds = new RadioSound?[rows.Count];
        for (var k = 0; k < measured.Count; k++)
            sounds[measured[k]] = new RadioSound(brightness[k], onsets[k], dynamics[k]);

        var songs = new RadioSong[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            songs[i] = new RadioSong(
                row.RelativePath, row.Folder, row.Artist, SongOf(row), row.Year,
                genreByFolder.TryGetValue(row.Folder, out var genre) ? genre : null,
                sounds[i],
                moodsByPath.TryGetValue(row.RelativePath, out var moods) ? moods : NoMoods,
                row.Offered,
                row.Bpm is > 0 ? row.Bpm : null);
        }

        return new RadioCrate(songs);
    }

    /// <summary>All three readings present and not all nought. Nought on all three is what the meter stores for a
    /// track it measured and found nothing in (see <c>LibraryDatabase.SaveTrackMood</c>), which is not a place on
    /// any axis.</summary>
    private static bool IsMeasured(RadioRow row) =>
        row is { Brightness: { } b, Onsets: { } o, Dynamics: { } d } && !(b == 0 && o == 0 && d == 0);

    /// <summary>Each value's place among the others, from 0 for the lowest to 1 for the highest. Tied values share
    /// the middle of the places they cover, so a run of equal readings does not spread across the axis.</summary>
    private static double[] Percentiles(IReadOnlyList<double> values)
    {
        var count = values.Count;
        var result = new double[count];
        if (count < 2)
        {
            Array.Fill(result, 0.5);
            return result;
        }

        var order = Enumerable.Range(0, count).ToArray();
        Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

        for (var first = 0; first < count;)
        {
            var last = first;
            while (last + 1 < count && values[order[last + 1]] == values[order[first]])
                last++;

            var place = (first + last) / 2.0 / (count - 1);
            for (var k = first; k <= last; k++)
                result[order[k]] = place;

            first = last + 1;
        }

        return result;
    }

    /// <summary>The value two copies of one song share. An untitled file gets its own path, so it is never taken
    /// for another untitled file by the same act.</summary>
    private static string SongOf(RadioRow row)
    {
        var title = TitleAsides.NameOnly(TitleAsides.WithoutCredits(row.Title)).Trim();
        return title.Length == 0
            ? row.RelativePath
            : row.Artist.Trim().ToLowerInvariant() + "\n" + title.ToLowerInvariant();
    }
}

/// <summary>
/// Picks the songs a song radio plays next: songs close to the one playing in mood, era and genre, and, counting
/// for less, tempo.
///
/// <para>Each song the radio plays is the seed for the songs added while it plays. The song the radio was started
/// from still counts for half as much again (<see cref="OriginWeight"/>), so a long session of radio does not drift
/// from 1970s folk to 2010s rock one small step at a time.</para>
///
/// <para>Pure: the crate, the queue and the history are handed in, and the answer is a list of relative paths.</para>
/// </summary>
public static class SongRadio
{
    /// <summary>How many songs a radio keeps queued after the one playing. Each song that starts adds songs until
    /// there are this many again, which is one song in the ordinary case.</summary>
    public const int Ahead = 10;

    /// <summary>How much the song the radio started from counts against the song playing now.</summary>
    internal const double OriginWeight = 0.5;

    /// <summary>Each pick is drawn at random from this many of the closest songs that pass the spacing rules, so two
    /// radios started from one song do not play the same list.</summary>
    internal const int Shortlist = 6;

    /// <summary>An act does not come back until this many other songs have played.</summary>
    internal const int ArtistGap = 5;

    /// <summary>A record does not come back until this many other songs have played.</summary>
    internal const int RecordGap = 12;

    /// <summary>Years apart at which two songs count as a different era. Closer than this, the era term grows in
    /// a straight line from 0.</summary>
    internal const double EraSpan = 15;

    /// <summary>What a term contributes when either song lacks the fact it compares: halfway between a match and
    /// the furthest apart. Nought would make unmeasured or undated songs look like perfect matches and push them to
    /// the front, the same reason <c>MoodFlow.UnknownKeyCost</c> gives.</summary>
    internal const double Unknown = 0.5;

    /// <summary>The genre term for two shelves in one family (<see cref="Genres.Related"/>): Rock and Metal, Jazz and
    /// Blues.</summary>
    internal const double RelatedGenre = 0.5;

    /// <summary>How much of the mood term the shared mood lists decide. The rest is the distance between the
    /// readings.</summary>
    internal const double MoodListShare = 0.25;

    /// <summary>How much tempo counts against mood, era and genre, which count 1 each. Half: tempo is a factor
    /// and not a requirement. It also covers a fifth of a real collection, where mood covers nearly all of it.</summary>
    internal const double TempoWeight = 0.5;

    /// <summary>How far apart two tempos are when they count as far apart: a fifth, as a ratio. Closer than that the
    /// term grows in a straight line on a log scale, so 100 and 110 BPM are as far apart as 140 and 154.</summary>
    internal static readonly double TempoSpan = Math.Log2(1.2);

    /// <summary>
    /// The next songs, closest first in the order they should be queued.
    /// </summary>
    /// <param name="seed">The song playing now, as a relative path.</param>
    /// <param name="origin">The song the radio was started from.</param>
    /// <param name="inQueue">Every song in the queue now, whoever put it there. Never picked.</param>
    /// <param name="history">Every song this radio has queued, oldest first. Not picked again, and the last few
    /// decide which acts and records are too recent. When the crate has fewer unplayed songs left than
    /// <paramref name="count"/>, the older half may be picked again.</param>
    /// <param name="random">Chooses within each shortlist. Stated by the caller so a test can fix it.</param>
    /// <returns>At most <paramref name="count"/> relative paths. Empty when neither the seed nor the origin is in the
    /// crate, since there is then nothing to compare against.</returns>
    public static IReadOnlyList<string> Pick(
        RadioCrate crate, string seed, string origin,
        IReadOnlySet<string> inQueue, IReadOnlyList<string> history, int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(crate);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(inQueue);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(random);

        if (count <= 0 || (crate.Find(seed) ?? crate.Find(origin)) is not { } playing)
            return [];

        var scorer = new Scorer(playing, crate.Find(origin) ?? playing);

        var ranked = Ranked(crate, scorer, Blocked(crate, inQueue, history, seed, origin));
        if (ranked.Count < count && history.Count > 1)
        {
            // The radio has queued nearly everything it can reach. The older half of its history may come back;
            // the newer half and the queue stay out.
            ranked = Ranked(crate, scorer, Blocked(crate, inQueue, history.Skip(history.Count / 2).ToList(), seed, origin));
        }

        var recent = history.TakeLast(RecordGap).Select(crate.Find).OfType<RadioSong>().ToList();
        recent.Add(playing);

        var picked = new List<string>(count);
        var takenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenSongs = new HashSet<string>(StringComparer.Ordinal);
        var shortlist = new List<RadioSong>(Shortlist);

        while (picked.Count < count)
        {
            shortlist.Clear();
            Fill(spaced: true);

            // Nothing passes the spacing rules (a small collection, or one act's shelf): spacing gives way rather
            // than the radio going quiet.
            if (shortlist.Count == 0)
                Fill(spaced: false);

            if (shortlist.Count == 0)
                break;

            var pick = shortlist[random.Next(shortlist.Count)];
            picked.Add(pick.Path);
            takenPaths.Add(pick.Path);
            takenSongs.Add(pick.Song);
            recent.Add(pick);
        }

        return picked;

        void Fill(bool spaced)
        {
            foreach (var song in ranked)
            {
                if (takenPaths.Contains(song.Path) || takenSongs.Contains(song.Song))
                    continue;
                if (spaced && !IsSpaced(song, recent))
                    continue;

                shortlist.Add(song);
                if (shortlist.Count == Shortlist)
                    return;
            }
        }
    }

    /// <summary>How far apart two songs are in mood, era, genre and tempo, from 0 to 1. The first three count
    /// equally and tempo counts <see cref="TempoWeight"/> of one of them.</summary>
    internal static double Apart(RadioSong song, RadioSong to) =>
        Apart(song, to, RelatedTo(to.Genre));

    /// <summary>What a pick is ranked by, lowest first. See <see cref="Scorer"/>.</summary>
    internal static double Score(RadioSong song, RadioSong playing, RadioSong origin) =>
        new Scorer(playing, origin).Score(song);

    private static double Apart(RadioSong song, RadioSong to, IReadOnlySet<string> related) =>
        (MoodApart(song, to) + EraApart(song.Year, to.Year) + GenreApart(song.Genre, to.Genre, related)
         + (TempoWeight * TempoApart(song.Bpm, to.Bpm)))
        / (3 + TempoWeight);

    /// <summary>
    /// How far apart two tempos are, from 0 to 1, with a doubled or halved tempo counted as the same one.
    ///
    /// <para>The same tempo is counted 70 by one tag and 140 by another, and nothing in the audio settles which (see
    /// <c>Analysis/TempoOctave</c>). So the ratio is folded into one octave: 100 against 200 is 0, and the furthest
    /// apart are tempos half an octave apart, 100 against 141. <see cref="Unknown"/> where either has no tempo, which
    /// is most songs.</para>
    /// </summary>
    internal static double TempoApart(double? a, double? b)
    {
        if (a is not > 0 || b is not > 0)
            return Unknown;

        var octaves = Math.Abs(Math.Log2(a.Value / b.Value)) % 1;
        var folded = Math.Min(octaves, 1 - octaves);
        return Math.Min(folded / TempoSpan, 1);
    }

    /// <summary>The distance between the readings, plus whether the two share a mood list. <see cref="Unknown"/>
    /// when either is unmeasured: an unmeasured song is in no mood list because nobody has measured it, not because
    /// it sits in the middle.</summary>
    internal static double MoodApart(RadioSong a, RadioSong b)
    {
        if (a.Sound is not { } x || b.Sound is not { } y)
            return Unknown;

        var sound = Math.Sqrt(
            Squared(x.Brightness - y.Brightness) + Squared(x.Onsets - y.Onsets) + Squared(x.Dynamics - y.Dynamics))
            / Math.Sqrt(3);

        // Both in no list is agreement: both sit in the middle of their genre.
        var lists = (a.Moods.Count == 0 && b.Moods.Count == 0) || a.Moods.Overlaps(b.Moods) ? 0.0 : 1.0;

        return ((1 - MoodListShare) * sound) + (MoodListShare * lists);
    }

    internal static double EraApart(int a, int b) =>
        a > 0 && b > 0 ? Math.Min(Math.Abs(a - b) / EraSpan, 1) : Unknown;

    private static double GenreApart(string? genre, string? to, IReadOnlySet<string> related)
    {
        if (genre is null || to is null)
            return Unknown;
        if (string.Equals(genre, to, StringComparison.OrdinalIgnoreCase))
            return 0;
        return related.Contains(genre) ? RelatedGenre : 1;
    }

    private static IReadOnlySet<string> RelatedTo(string? genre) =>
        new HashSet<string>(Genres.Related(genre), StringComparer.OrdinalIgnoreCase);

    private static double Squared(double value) => value * value;

    /// <summary>Whether the act and the record have been away long enough. An empty credit names nobody, so a shelf
    /// of untagged files is not spaced as one act.</summary>
    private static bool IsSpaced(RadioSong song, IReadOnlyList<RadioSong> recent)
    {
        for (var i = recent.Count - 1; i >= 0 && i >= recent.Count - RecordGap; i--)
        {
            var other = recent[i];
            if (string.Equals(other.Folder, song.Folder, StringComparison.OrdinalIgnoreCase))
                return false;

            var withinArtistGap = i >= recent.Count - ArtistGap;
            if (withinArtistGap && song.Artist.Length > 0
                && string.Equals(other.Artist, song.Artist, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>The paths and the songs that may not be picked: the queue, the history handed in, the seed and the
    /// origin, and any other copy of one of those songs.</summary>
    private static (HashSet<string> Paths, HashSet<string> Songs) Blocked(
        RadioCrate crate, IReadOnlySet<string> inQueue, IReadOnlyList<string> history, string seed, string origin)
    {
        var paths = new HashSet<string>(inQueue, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(history);
        paths.Add(seed);
        paths.Add(origin);

        var songs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            if (crate.Find(path) is { } song)
                songs.Add(song.Song);

        return (paths, songs);
    }

    /// <summary>Every song that may be picked, closest first. The path breaks a tie so one crate and one seed always
    /// rank the same way.</summary>
    private static List<RadioSong> Ranked(
        RadioCrate crate, Scorer scorer, (HashSet<string> Paths, HashSet<string> Songs) blocked)
    {
        var scored = new List<(RadioSong Song, double Score)>();
        foreach (var song in crate.Songs)
        {
            if (!song.Offered || blocked.Paths.Contains(song.Path) || blocked.Songs.Contains(song.Song))
                continue;
            scored.Add((song, scorer.Score(song)));
        }

        scored.Sort((a, b) =>
        {
            var byScore = a.Score.CompareTo(b.Score);
            return byScore != 0 ? byScore : StringComparer.OrdinalIgnoreCase.Compare(a.Song.Path, b.Song.Path);
        });

        return [.. scored.Select(s => s.Song)];
    }

    /// <summary>The distance to the song playing plus <see cref="OriginWeight"/> of the distance to the song the radio
    /// started from. The related genres are worked out once per pick rather than once per song.</summary>
    private sealed class Scorer(RadioSong playing, RadioSong origin)
    {
        private readonly IReadOnlySet<string> _playingRelated = RelatedTo(playing.Genre);
        private readonly IReadOnlySet<string> _originRelated = RelatedTo(origin.Genre);

        public double Score(RadioSong song) =>
            Apart(song, playing, _playingRelated) + (OriginWeight * Apart(song, origin, _originRelated));
    }
}
