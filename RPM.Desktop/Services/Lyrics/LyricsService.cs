using RPM.Core.Threading;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using RPM.Core.Lyrics;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Lyrics;

/// <summary>The app's one way to ask what is being sung.
///
/// <para><b>Asked, never sent unasked.</b> Nothing here runs on its own schedule. A lookup happens only
/// when something on screen is about to show the words. Two callers ask: the visualizer's lyric view,
/// and the left pane's track sheet while that pane is on screen. Normal listening with neither of them
/// open makes no request at all. This is the right default for a feature that must name the record to a
/// third-party server.</para>
///
/// <para>Every answer is kept, including the empty ones. "Nobody has written this one down" is an answer,
/// just as a sheet is. A track played twice, or a view opened and closed again, must not ask twice. The
/// cache lasts as long as the app runs. It is small, a few hundred lines of text at most, and one session
/// is the span in which a user replays things.</para>
/// </summary>
public sealed class LyricsService
{
    /// <summary>The identifying user agent that the app's other outside requests also send.</summary>
    private const string UserAgent = "rpm/1.0 ( https://rpmrpmrpm.com )";

    private readonly LrcLibLookup _lrclib;
    private readonly Dictionary<string, LyricSheet> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when a record that has already been answered gets a different answer. Today this happens
    /// when the user picks another version. The record is named by its <see cref="Key"/>.
    ///
    /// <para>No lookup happens because of this event. Everything else here is asked for, never sent
    /// unasked, and this event keeps to that. It exists because a record's sheet can change while the
    /// record is still playing, and two views can show it. Without the event, the pane that asked would
    /// swap its words, and the visualizer would keep the old words until the track ended. The result
    /// would be one song's words timed two ways on two screens at once.</para>
    ///
    /// <para>The event is raised outside the lock, so a listener can call any method of this service from
    /// its handler. It runs on the thread that made the change, which is the UI thread in practice. A
    /// listener that touches a view should still move to its own dispatcher rather than rely on
    /// that.</para>
    /// </summary>
    public event Action<string, LyricSheet>? Replaced;

    private readonly Library.LibraryService _library;
    private readonly LyricCloud _cloud;

    public LyricsService(Library.LibraryService library, LyricCloud cloud)
    {
        _library = library;
        _cloud = cloud;

        // The timeout is short on purpose. The lyrics are extra detail on a picture that is already
        // running. A lookup that has not answered in a few seconds has missed its moment, so the view
        // carries on without the words instead of holding a fade open while it waits.
        var http = Wire.Outside(Destination.Unaddressed, TimeSpan.FromSeconds(8));
        _lrclib = LrcLibLookup.Live(http, UserAgent);
    }

    /// <summary>The synced sheet for a record, or <see cref="LyricSheet.Empty"/> when there is not one.
    /// This does not throw for a network problem. The caller shows the same thing for "no lyrics" and for
    /// "no internet".
    /// </summary>
    /// <param name="filePath">The record's own file, where there is one. The file
    /// is checked before the internet. Many files hold a timed sheet in their own lyrics tag, and that copy
    /// belongs to the user.</param>
    /// <param name="rpmId">The record's cloud id, where the caller wants the sheet its owner chose on a
    /// desktop. That sheet comes after the file's own tag and before this machine's cache folder and
    /// LRCLIB, because it is the newest choice the owner made, on any of their computers. It is kept for
    /// the session only, like the tag's: the cloud is where it lives. Leave it out to keep to this
    /// machine and LRCLIB.</param>
    public async Task<LyricSheet> ForAsync(
        string? artist, string? title, string? album, double durationSeconds,
        string? filePath = null, CancellationToken ct = default, string? rpmId = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return LyricSheet.Empty;

        var key = Key(artist, title, album, durationSeconds);

        // Five places to look, cheapest and closest first: this session, the file itself, the sheet the
        // owner chose on a desktop (where the caller gave the record's cloud id), this user's own folder,
        // then LRCLIB.
        lock (_seen)
        {
            if (_seen.TryGetValue(key, out var hit))
                return hit;
        }

        // ── the record's own lyrics tag ──
        //
        // A file whose lyrics tag holds "[00:04.11] Hello, hello" carries a timed sheet. Some views would show
        // such a tag as plain text, with the times printed in the margin, so the tag is parsed first. The
        // same reader that parses the service's own answers reads it.
        //
        // The tag is checked before the disk cache and the network, because it is the closest copy and it
        // belongs to the user. The user put it there, or a tagger did it for them. It needs no request to
        // anyone. The sheet is kept for the session but never written to the cache folder. It is already on
        // this disk, in the file it belongs to, and a second copy could only go out of date.
        if (!string.IsNullOrWhiteSpace(filePath)
            && await Cancellable.RunAsync(() => Embedded(filePath), ct)
                   is { Ran: true, Value: { IsSynced: true } own })
        {
            lock (_seen)
            {
                _seen[key] = own;
            }
            _ = Task.Run(() => Score(filePath, own));
            return own;
        }

        // ── the sheet the owner chose on a desktop ──
        //
        // "Try another" and a sheet of the listener's own are sent to the cloud (see LyricSheetShare), so a
        // second computer shows the sheet chosen on the first. This machine's cache folder may still hold
        // LRCLIB's first answer from an earlier play, so the cloud is asked before it.
        if (!string.IsNullOrWhiteSpace(rpmId) && await _cloud.ChosenSheetAsync(rpmId, ct) is { } chosen)
        {
            lock (_seen)
            {
                _seen[key] = chosen;
            }
            _ = Task.Run(() => Score(filePath, chosen));
            return chosen;
        }

        // Runs off the UI thread. This code runs while a visualizer is drawing, and a frame must not wait
        // for a file read.
        if (await Task.Run(() => LyricCache.Get(key)) is { } remembered)
        {
            lock (_seen)
            {
                _seen[key] = remembered;
            }
            _ = Task.Run(() => Score(filePath, remembered));
            return remembered;
        }

        LyricSheet sheet;
        try
        {
            sheet = await _lrclib.FindAsync(artist, title, album, durationSeconds, ct);
        }
        catch (OperationCanceledException)
        {
            // The track changed, or the view closed. Nothing is saved, because the question was not
            // answered. Saving an empty result here would give the next real attempt a wrong answer.
            throw;
        }
        catch
        {
            // The service could not be reached. This is not an answer, so it must never be saved as one.
            // Saving it would let a short network failure hide the lyrics for a long time. Nothing is
            // saved, not even for this session.
            return LyricSheet.Empty;
        }

        // A real answer, either a sheet or none. Both are kept, because most records have no synced lyrics,
        // and asking again on every play is the cost this cache removes.
        lock (_seen)
        {
            _seen[key] = sheet;
        }
        _ = Task.Run(() =>
        {
            LyricCache.Put(key, sheet);
            Score(filePath, sheet);
        });
        return sheet;
    }

    /// <summary>Reads the words as one number and saves it against the track, when the track is in this
    /// user's library.
    ///
    /// <para><b>Done here because a sheet is already in hand.</b> The slow part of reading lyrics is the
    /// fetch, and by this point something else has already paid for it. The same rule applies to
    /// MoodMeter's loudness reading. It is also why this method never asks LRCLIB for a sheet only to
    /// score it.</para>
    ///
    /// <para><b>Best effort, and silent about every failure.</b> Nothing on screen waits for this number,
    /// so a failure must not throw. These cases leave the stored value as it was: no file, no open
    /// library, a borrowed shelf, a track from outside the collection, or a sheet that the library's own
    /// checks reject. A missing value means the song is placed by its audio measurements alone, which is
    /// how every song was placed before this existed.</para>
    ///
    /// <para><b>Only the user's own library is written, never a borrowed one.</b> That is why the method
    /// uses <see cref="Library.LibraryService.Db"/> and not <c>Shelf</c>. A reading is written into a
    /// mirror, and the only mirror this app may write is its own.</para>
    /// </summary>
    private void Score(string? filePath, LyricSheet sheet)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !_library.IsOpen)
                return;

            if (_library.Root is not { Length: > 0 } root)
                return;

            var relative = Path.GetRelativePath(root, filePath).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return;

            _library.Db.SaveLyricValence(relative, LyricValence.Read(sheet));
        }
        catch
        {
            // Nothing on screen depends on this working, so the error is ignored.
        }
    }

    /// <summary>Returns the next sheet for this record. Used when the sheet on screen is the wrong one.
    ///
    /// <para>A sheet whose words run a beat ahead of the singer all the way through is not broken. It has
    /// the right words, timed against a different cut of the song. LRCLIB has several cuts for many
    /// songs, especially well-covered or remastered ones. The timing data cannot tell the app which cut it
    /// has. The automatic choice takes the first sheet that fits the length, and that is right most of the
    /// time. Only a reader looking at the screen can settle it. This method steps to the next candidate and
    /// returns it.</para>
    ///
    /// <para>The list is circular. After the last sheet, the list starts again from the first. A press
    /// therefore never stops on the worst answer, and the sheet it started on is always one press
    /// away.</para>
    ///
    /// <para>The chosen sheet becomes the answer for everyone. It replaces the session copy and the copy in
    /// the cache folder. <see cref="Replaced"/> sends it to anything already showing the old sheet, so the
    /// visualizer and this pane never show one song's words timed two ways. The file's own tag is never
    /// overwritten. That copy belongs to the user. This method only asks the internet for other
    /// answers.</para>
    /// </summary>
    public async Task<LyricSheet> AnotherAsync(
        string? artist, string? title, string? album, double durationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return LyricSheet.Empty;

        var key = Key(artist, title, album, durationSeconds);

        IReadOnlyList<LyricSheet> all;
        lock (_seen)
        {
            _alternates.TryGetValue(key, out var held);
            all = held.Sheets ?? [];
        }

        if (all.Count == 0)
        {
            // Ask the service directly, not the caches. Each cache holds the one answer that this method
            // is trying to get past.
            try
            {
                all = await _lrclib.FindAllAsync(artist, title, album, durationSeconds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return LyricSheet.Empty;   // Offline. The sheet on screen stays as it is.
            }
        }

        if (all.Count == 0)
            return LyricSheet.Empty;

        LyricSheet next;
        lock (_seen)
        {
            var at = _alternates.TryGetValue(key, out var held) ? held.At : 0;

            // If the list holds only the sheet already on screen, there is nothing to step to. The method
            // returns Empty, so the same words are not faded onto themselves.
            if (all.Count == 1 && _seen.TryGetValue(key, out var only) && Same(only, all[0]))
            {
                _alternates[key] = (all, 0);
                return LyricSheet.Empty;
            }

            at = (at + 1) % all.Count;

            // The first press on a record that has not been changed moves to index 1, not index 0. Index 0
            // is the choice the automatic path made, and it is the sheet on screen when the reader presses.
            if (Same(all[at], _seen.TryGetValue(key, out var showing) ? showing : LyricSheet.Empty))
                at = (at + 1) % all.Count;

            next = all[at];
            _alternates[key] = (all, at);
            _seen[key] = next;
        }

        _ = Task.Run(() => LyricCache.Put(key, next));

        // The event is raised rather than left for others to find. The visualizer asks once per record, so
        // without it the visualizer would keep the replaced sheet for the rest of the track.
        Replaced?.Invoke(key, next);
        return next;
    }

    /// <summary>The reader's own sheet. It replaces anything the catalogues offered.
    ///
    /// <para>This is the last resort for a record whose words nobody has published, or has published
    /// wrongly. LRCLIB has nothing for a bootleg, a live cut or a private recording, and no amount of
    /// pressing "try another" makes a sheet that does not exist. A file that the listener made or found is
    /// then the only answer, and this method stores it.</para>
    ///
    /// <para>The sheet is stored in the same way as an internet answer: in the session copy and the cache
    /// folder. <see cref="Replaced"/> is raised for anything showing the old sheet. A sheet is the same
    /// whoever wrote it, and the views that read it do not need to know where it came from. It is kept
    /// after the session ends, so the listener does not have to find the file again each time the record
    /// plays.</para>
    ///
    /// <para>The alternates list is cleared. That list holds the catalogue's answers, and this sheet is not
    /// one of them. If the list were kept, the next "try another" would step from an index into a list that
    /// does not contain the sheet on screen. Pressing "try another" after this starts the catalogue's list
    /// again. That is how the reader undoes this choice.</para>
    /// </summary>
    /// <param name="sheet">The parsed file. A sheet with no timings is refused. A sheet that follows the
    /// singer needs timings, and a block of untimed words is not one.</param>
    /// <returns>The sheet now in force, or <see cref="LyricSheet.Empty"/> where it was refused.</returns>
    public LyricSheet Adopt(
        string? artist, string? title, string? album, double durationSeconds, LyricSheet sheet)
    {
        if (string.IsNullOrWhiteSpace(title) || !sheet.IsSynced)
            return LyricSheet.Empty;

        var key = Key(artist, title, album, durationSeconds);

        lock (_seen)
        {
            _seen[key] = sheet;
            _alternates.Remove(key);
        }

        _ = Task.Run(() => LyricCache.Put(key, sheet));

        Replaced?.Invoke(key, sheet);
        return sheet;
    }

    /// <summary>Whether two sheets are the same answer: same timings, same words.</summary>
    private static bool Same(LyricSheet one, LyricSheet other)
        => one.Lines.Count == other.Lines.Count
           && !one.Lines.Where((l, i) => Math.Abs(l.Seconds - other.Lines[i].Seconds) > 0.01
                                         || !string.Equals(l.Text, other.Lines[i].Text, StringComparison.Ordinal))
                        .Any();

    /// <summary>Every sheet a record has, and which one is showing. This is used by the reader that steps
    /// through them. It is kept for the session only. Which alternative somebody chose on one play is not
    /// worth a file on disk. The chosen sheet itself is written to the cache, like any other
    /// answer.</summary>
    private readonly Dictionary<string, (IReadOnlyList<LyricSheet> Sheets, int At)> _alternates
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The timed sheet stored in the file's own lyrics tag, or <see cref="LyricSheet.Empty"/> for
    /// a file that has plain words or none. Runs off the calling thread and does not report failures. A
    /// file that cannot be read is treated as a file with no lyrics.</summary>
    private static LyricSheet Embedded(string path)
    {
        try
        {
            return LyricSheet.Parse(RPM.Core.Metadata.TagReader.Read(path).Lyrics);
        }
        catch
        {
            return LyricSheet.Empty;
        }
    }

    /// <summary>The name of one record, built so that a cache can match the same record each time.
    ///
    /// <para>The text is set to lower case with its spacing collapsed. The same recording reaches this
    /// method from a file's tags, from a cloud row and from a rip, and the three do not agree about
    /// capitals or double spaces. The length is part of the key, rounded to the second. LRCLIB is asked by
    /// length too, so two cuts of one song under one name must not share an answer.</para>
    ///
    /// <para>This is public because <see cref="Replaced"/> names the record it means this way. A listener
    /// has to be able to check whether that is the record it is showing. If a listener named records by its
    /// own method, there would be two ideas of when two records are the same one.</para>
    /// </summary>
    public static string Key(string? artist, string? title, string? album, double durationSeconds)
        => string.Join('|',
            Tidy(artist), Tidy(title), Tidy(album),
            ((int)Math.Round(durationSeconds)).ToString(CultureInfo.InvariantCulture));

    private static string Tidy(string? value)
        => string.Join(' ', (value ?? "").ToLowerInvariant()
                                         .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
