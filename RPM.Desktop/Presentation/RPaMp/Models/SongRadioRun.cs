using System.Diagnostics;
using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Core.Playback;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// One song radio, from "Start radio" until its rows have left the queue.
///
/// <para>The queue rows a radio put in carry it (<see cref="PlaylistTrack.Radio"/>), and that is the only thing that
/// says whether the radio is on: a row of this run on the deck asks for more songs, and a queue rebuilt by anything
/// else holds none of its rows. There is no on/off flag to forget to clear. See RPaMpModel.Radio.</para>
///
/// <para><see cref="History"/>, <see cref="HasQueued"/> and <see cref="NoteQueued"/> are called on the dispatcher
/// only. <see cref="NextAsync"/> runs on the pool and reads only what it is handed and the crate, which does not
/// change after it is read.</para>
/// </summary>
public sealed class SongRadioRun
{
    private readonly LibraryService _library;
    private readonly LibraryDatabase _shelf;
    private readonly string _root;
    private readonly string _origin;
    private readonly Task<RadioCrate> _crate;

    /// <summary>Every song this run has queued, oldest first, as relative paths.</summary>
    private readonly List<string> _history = [];
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this run leaves out the songs heard lately (<see cref="Mixtape.Spares"/>). Only a mixtape
    /// after a list does (<see cref="CarryOn"/>). "Start radio" is left as it is.</summary>
    private readonly bool _sparesHeard;

    /// <param name="history">Songs to count as queued by this run from its start, after the origin: a mixtape's
    /// list. Null for none.</param>
    private SongRadioRun(LibraryService library, TrackHome.Home home, string origin,
                         IEnumerable<string>? history = null, bool sparesHeard = false)
    {
        _library = library;
        _shelf = home.Shelf;
        _root = home.Root;
        _origin = origin;
        _sparesHeard = sparesHeard;
        Note(origin);
        foreach (var song in history ?? [])
            Note(song);

        // Started now so the read overlaps the first seconds of the song rather than following them.
        _crate = Task.Run(ReadCrate);
    }

    /// <summary>A radio on this song, or null when no collection here holds it (a file played from outside the
    /// library). Starts reading the crate.</summary>
    public static SongRadioRun? Tune(TrackInfo track, LibraryService library)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(library);

        if (TrackHome.For(track.FilePath) is not { } home || RelativeTo(home.Root, track.FilePath) is not { } origin)
            return null;

        return new SongRadioRun(library, home, origin);
    }

    /// <summary>
    /// A mixtape after a list that has reached its last song (see <see cref="Mixtape"/>): a radio from the list's
    /// first song, with the whole list in its history, so it plays nothing of the list again, and it leaves out
    /// the songs heard lately. It picks from the collection that holds the last song. Null when that collection
    /// holds none of the list, or no collection holds the last song (a file played from outside the library).
    /// Starts reading the crate.
    /// </summary>
    /// <param name="list">The files of the queue, in play order.</param>
    public static SongRadioRun? CarryOn(IReadOnlyList<string> list, LibraryService library)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(library);

        if (list.Count == 0 || TrackHome.For(list[^1]) is not { } home
            || Mixtape.Seed(list.Select(path => RelativeTo(home.Root, path))) is not { } seed)
            return null;

        return new SongRadioRun(library, home, seed.Origin, seed.History, sparesHeard: true);
    }

    /// <summary>What this run has queued, oldest first. A copy, for handing to <see cref="NextAsync"/>.</summary>
    public IReadOnlyList<string> History => [.. _history];

    /// <summary>Whether this run has queued the file before, including the song it started from.</summary>
    public bool HasQueued(string filePath) =>
        RelativeTo(_root, filePath) is { } relative && _queued.Contains(relative);

    /// <summary>Records that this run put the file in the queue.</summary>
    public void NoteQueued(string filePath)
    {
        if (RelativeTo(_root, filePath) is { } relative)
            Note(relative);
    }

    private void Note(string relative)
    {
        if (_queued.Add(relative))
            _history.Add(relative);
    }

    /// <summary>
    /// The songs to follow <paramref name="playingPath"/>, as queue entries. Waits for the crate on the first call.
    /// </summary>
    /// <param name="inQueue">Every file in the queue when this was asked, taken on the dispatcher.</param>
    /// <param name="history">This run's <see cref="History"/>, taken on the dispatcher at the same moment.</param>
    public async Task<IReadOnlyList<TrackInfo>> NextAsync(
        string playingPath, IReadOnlyList<string> inQueue, IReadOnlyList<string> history, int count)
    {
        var crate = await _crate.ConfigureAwait(false);

        var seed = RelativeTo(_root, playingPath) ?? _origin;
        IReadOnlySet<string> queued = inQueue.Select(path => RelativeTo(_root, path)).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A mixtape after a list also leaves out what was heard lately. Read on every call, so a song heard
        // while the mixtape plays is left out of its next songs too.
        if (_sparesHeard)
            queued = Mixtape.Spared(queued, _library.Read(_shelf, "a mixtape's songs heard lately",
                    db => db.RecentlyHeard(Mixtape.Spares))
                .Match<IReadOnlyList<string>>(heard => heard, _ => []));

        var picks = SongRadio.Pick(crate, seed, _origin, queued, history, count, Random.Shared);
        if (picks.Count == 0)
            return [];

        // In the order picked. A row that has gone from the mirror since the crate was read is skipped.
        return _library.Read(_shelf, "a song radio's next songs",
                db => picks.Select(db.FindByRelativePath).OfType<LibraryTrack>().ToList())
            .Match<IReadOnlyList<TrackInfo>>(rows => [.. rows.Select(row => row.ToTrackInfo(_root))], _ => []);
    }

    private RadioCrate ReadCrate()
    {
        var clock = Stopwatch.StartNew();
        var crate = _library.Read(_shelf, "a song radio's crate", db => db.ReadRadioCrate())
            .Match(read => read, _ => RadioCrate.Empty);

        // Counts and a time only: a ring line names no song.
        Said.Line("songradio",
            $"crate read: {crate.Songs.Count} songs, {crate.Songs.Count(s => s.Sound is not null)} measured, " +
            $"{clock.ElapsedMilliseconds} ms");
        return crate;
    }

    /// <summary>The path relative to the collection's root with forward slashes, as the mirror keys it, or null when
    /// the file is not under that root.</summary>
    private static string? RelativeTo(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }
}
