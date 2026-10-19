using Microsoft.UI.Dispatching;
using RPM.Core.Cloud;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Reports what this listener plays, as counts, so RPM can show which song on a record its listeners
/// choose. This is the other half of the album page's star, for records the catalogues cannot answer for.
///
/// <para><b>What leaves this computer.</b> One number per song of one record: "this song was finished four
/// times since the last report". There is no time, no order, no history, and nothing about any other
/// record. The server keeps the total and never the sender (see <c>AlbumPlayStore</c>). So what is built is
/// a counter per song, not a record of anyone's listening. It can be turned off in Settings. A report is
/// only sent for a record whose shared entry this library already knows. An album whose page has never been
/// opened is not named to the server by being played.</para>
///
/// <para>Reports are batched rather than sent per song. This is good for the listener as well as the
/// server. A request at the end of every track would be sent every three minutes all evening, and each one
/// says when it was sent. A batch says how many times, and it is sent four times an hour.</para>
///
/// <para>Losing a batch does no harm. One play that never reaches the server is one play missing from a
/// total of thousands. So nothing here retries, queues to disk, or survives a restart.</para>
/// </summary>
public sealed class PlayReportService
{
    /// <summary>How often the pending counts are sent. Long enough that an evening is a handful of
    /// requests. Short enough that closing the app loses only a few minutes of listening.</summary>
    private static readonly TimeSpan Flush = TimeSpan.FromMinutes(15);

    /// <summary>The most albums sent per flush, and the most songs per album. These limits cap what one
    /// flush can send.</summary>
    private const int MaxAlbums = 25;

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly RpmCommunityClient _community;
    private readonly DispatcherQueueTimer _timer;

    /// <summary>The plays since the last report: album id, then song, then the count. Guarded by its own
    /// lock. The counter is filled on the dispatcher and emptied on the flush.</summary>
    private readonly Dictionary<string, Dictionary<string, long>> _pending = new(StringComparer.Ordinal);

    public PlayReportService(
        LibraryService library, SettingsService settings, RpmCommunityClient community,
        DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _library = library;
        _settings = settings;
        _community = community;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = Flush;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = SendAsync();
        _timer.Start();
    }

    /// <summary>
    /// A song was played through, named by its path relative to the library root.
    /// </summary>
    /// <remarks>
    /// Only plays that COUNT should be passed here. The question being answered is which song people
    /// choose, and a track skipped after nine seconds is evidence of the opposite. What counts is the rule in
    /// <c>ListenEntry</c>, the same rule that the library's own play counts use.
    /// </remarks>
    public void Note(string relativePath)
    {
        if (!_settings.ShareListening || string.IsNullOrWhiteSpace(relativePath))
            return;

        try
        {
            if (!_library.IsOpen)
                return;

            var folder = Folder(relativePath);
            var meta = _library.Db.GetAlbumMeta(folder);

            // No shared entry, no report. The id is saved when someone opens the record's page. So this
            // reports on records the user has looked at, and stays quiet about the rest. This also stops
            // playback from creating catalogue rows for a library nobody has browsed.
            if (meta.RpmAlbumId is not { Length: > 0 } albumId)
                return;

            var name = Path.GetFileName(relativePath);
            if (_library.Db.FolderTracks(folder)
                    .FirstOrDefault(t => string.Equals(t.FileName, name, StringComparison.OrdinalIgnoreCase))
                is not { } row)
                return;

            // The tagged title, or the file name when there is no tag. The server turns it into a key. A
            // song with no title at all is not worth counting under an empty key.
            var title = row.Title is { Length: > 0 } tagged
                ? tagged
                : Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(title))
                return;

            lock (_pending)
            {
                if (_pending.Count >= MaxAlbums && !_pending.ContainsKey(albumId))
                    return;

                var album = _pending.TryGetValue(albumId, out var songs)
                    ? songs
                    : _pending[albumId] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                album[title] = album.GetValueOrDefault(title) + 1;
            }
        }
        catch (Exception)
        {
            // The library can close during a track change, or the row can be gone. A play that is not
            // counted does not need to interrupt the music.
        }
    }

    /// <summary>Sends the pending counts and clears them. Called on the timer, and also when the app is
    /// closing.</summary>
    public async Task SendAsync()
    {
        Dictionary<string, Dictionary<string, long>> sending;
        lock (_pending)
        {
            if (_pending.Count == 0)
                return;

            // The counts are taken out under the lock and sent outside it. The counter keeps filling while
            // the requests are in flight. A play made during a flush goes into the next batch and is not
            // lost.
            sending = new Dictionary<string, Dictionary<string, long>>(_pending, StringComparer.Ordinal);
            _pending.Clear();
        }

        // The setting is read once for the whole flush. If the listener turns it off part way through,
        // nothing more is sent, including counts that were already pending.
        if (!_settings.ShareListening)
            return;

        foreach (var (albumId, songs) in sending)
        {
            try
            {
                await _community.ReportAlbumPlaysAsync(
                    albumId,
                    [.. songs.Select(s => new RpmTrackPlays(s.Key, s.Value))],
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // Signed out, offline, or the service has a temporary fault. These plays are lost. That is
                // acceptable, since nobody is waiting on this counter.
            }
        }
    }

    /// <summary>The album folder a relative path sits in, in the mirror's own spelling.</summary>
    private static string Folder(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut <= 0 ? "" : relativePath[..cut];
    }
}
