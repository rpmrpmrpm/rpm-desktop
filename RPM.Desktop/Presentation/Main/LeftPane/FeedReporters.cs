using RPM.Core.Cloud;
using RPM.Core.Library;


namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// Sends what plays on this computer to the reader's profile, as "listening to", as the phones'
/// <c>ListeningShare</c> does: when a song starts, at most once a minute (a song that changes faster waits,
/// and only the newest is sent), and again every twenty minutes while it plays, because the server stops
/// showing a song thirty minutes after it was sent.
///
/// <para><b>The newest song wins, on whichever device it plays.</b> The server keeps one song for the
/// person, the last one sent. Two rules keep this computer from overwriting a newer song from a phone:
/// the twenty-minute send goes only while the server still shows this computer's song, and stopping clears
/// the song only while it is still this computer's. Both read the profile first.</para>
///
/// <para>Every call comes on the UI thread (the page enqueues them), so the fields need no lock. A send
/// waiting for its minute is cancelled by the next song or by a stop.</para>
/// </summary>
internal sealed class ListeningShare
{
    private static readonly TimeSpan Spacing = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(20);

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    /// <summary>What was playing at the last note: the file and what the source announces. A note with the
    /// same identity changes nothing, so the twice-a-second ticks of playback cost one comparison.</summary>
    private string? _identity;
    private RpmSong? _sent;
    private DateTimeOffset _sentAt;
    private CancellationTokenSource? _waiting;

    /// <summary>Playback changed: a new song, a stop, or only the position.</summary>
    public void Note()
    {
        if (Auth.UserId is not { Length: > 0 })
            return;
        var playback = App.Services.GetRequiredService<PlaybackService>();
        var onAir = App.Services.GetRequiredService<RadioService>().Current is not null;
        var identity = onAir || playback.PlayingPath is not { Length: > 0 } path
            ? null
            : path + "\u001F" + playback.NowPlaying?.Title;

        if (identity == _identity)
        {
            if (_sent is { } sent && !playback.IsPaused && _waiting is null && DateTimeOffset.UtcNow - _sentAt >= Refresh)
            {
                _sentAt = DateTimeOffset.UtcNow;
                _ = RefreshAsync(sent);
            }
            return;
        }
        _identity = identity;
        Plan(identity is null ? null : FeedPlaying.Now());
    }

    private void Plan(RpmSong? song)
    {
        _waiting?.Cancel();
        _waiting = null;
        if (song is null)
        {
            if (_sent is { } gone)
            {
                _sent = null;
                _sentAt = default;
                _ = ClearAsync(gone);
            }
            return;
        }
        if (_sent is { } sent && Same(sent, song))
            return;

        var wait = _sentAt == default ? TimeSpan.Zero : Spacing - (DateTimeOffset.UtcNow - _sentAt);
        var waiting = _waiting = new CancellationTokenSource();
        _ = SendAfterAsync(song, wait > TimeSpan.Zero ? wait : TimeSpan.Zero, waiting);
    }

    private async Task SendAfterAsync(RpmSong song, TimeSpan wait, CancellationTokenSource waiting)
    {
        try
        {
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, waiting.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        if (_waiting != waiting)
            return;
        _waiting = null;
        _sent = song;
        _sentAt = DateTimeOffset.UtcNow;
        Say($"listening sent: {song.Title} by {song.Artist}");
        await Quietly(() => Community.SetListeningAsync(song));
    }

    /// <summary>A line on the scope's "profile" channel: what this computer told the profile, and why not.</summary>
    internal static void Say(string line)
    {
        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said("profile", line);
    }

    /// <summary>Sends the song again, while the server still shows it as this computer's.</summary>
    private static async Task RefreshAsync(RpmSong sent)
    {
        var shown = await ShownAsync();
        if (shown is null || Same(shown, sent))
        {
            Say($"listening refreshed: {sent.Title}");
            await Quietly(() => Community.SetListeningAsync(sent));
        }
        else
        {
            Say($"listening not refreshed: another device sent {shown.Title}");
        }
    }

    /// <summary>Clears the song, while the server still shows this computer's: a newer song from another
    /// device stays.</summary>
    private static async Task ClearAsync(RpmSong sent)
    {
        if (await ShownAsync() is { } shown && Same(shown, sent))
        {
            Say($"listening cleared: {sent.Title}");
            await Quietly(() => Community.ClearListeningAsync());
        }
        else
        {
            Say($"listening left: the profile shows another device's song, or none");
        }
    }

    private static async Task<RpmSong?> ShownAsync()
    {
        try
        {
            return (await Community.GetOwnPersonAsync())?.Listening is { } now
                ? new RpmSong(now.Artist, now.Title, now.Album)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Same(RpmSong a, RpmSong b) =>
        string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);

    private static async Task Quietly(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // Offline or signed out: the next song tries again.
        }
    }
}

/// <summary>
/// Works out the badges the reader's own collection unlocks (<see cref="RpmProfileBadges.Unlocked"/>) from
/// the artist shelf the album index already reads, and sends the ones the server does not hold yet. The
/// server keeps every badge once sent and notifies the reader of each new one.
///
/// <para>Only the reader's own collection counts, never a borrowed shelf. The work is a count over the
/// shelf already in memory; the send is a background pass that asks the governor first, on the network
/// lane, last wave, not before the app is ready. A collection whose badges were already sent sends
/// nothing, so the library changing (a play count, an import) costs the count alone.</para>
/// </summary>
internal sealed class BadgeReporter
{
    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    private readonly Services.Shell.Sweep _sweep = App.Services.GetRequiredService<Services.Shell.Governor>()
        .Pass("profile badges", Services.Shell.Lane.Network, Services.Shell.Wave.Last, notBefore: Services.Shell.Phase.Ready);

    /// <summary>The badge list last confirmed as held by the server, joined; a new list is sent.</summary>
    private string _held = "";

    /// <summary>The badge list last worked out, joined, so the log line is written once per change.</summary>
    private string _counted = "";
    private bool _sending;

    /// <summary>The badges this collection unlocks, for the edit page. Empty until the shelf is read.</summary>
    public IReadOnlyList<string> Unlocked { get; private set; } = [];

    /// <summary>The server took new badges: there are notifications to count again.</summary>
    public event Action? Sent;

    /// <summary>The library's albums changed (or were read). Runs on the UI thread.</summary>
    public void Note(MainModel model)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || !ReferenceEquals(library.Shelf, library.Db) || !model.ArtistsRead)
            return;

        var shelf = model.ArtistShelf;
        var genres = new Dictionary<string, int>(StringComparer.Ordinal);
        var tracks = 0;
        foreach (var (folder, count) in shelf.TrackCounts)
        {
            tracks += count;
            var genre = shelf.AlbumGenres.TryGetValue(folder, out var tag)
                ? RPM.Core.Metadata.Genres.Match(tag) ?? GenreWall.Unclassified
                : GenreWall.Unclassified;
            genres[genre] = genres.GetValueOrDefault(genre) + 1;
        }
        Unlocked = RpmProfileBadges.Unlocked(shelf.TrackCounts.Count, tracks, genres);

        var key = string.Join(",", Unlocked);
        if (key != _counted)
        {
            _counted = key;
            ListeningShare.Say($"badges here: {key} ({shelf.TrackCounts.Count} albums, {tracks} tracks)");
        }
        if (key == _held || Unlocked.Count == 0 || _sending || Auth.UserId is not { Length: > 0 })
            return;
        _ = SendAsync(key, Unlocked);
    }

    private async Task SendAsync(string key, IReadOnlyList<string> unlocked)
    {
        _sending = true;
        try
        {
            using var slot = await _sweep.Next(CancellationToken.None);
            if (!slot.Go)
                return;
            var own = await Community.GetOwnPersonAsync();
            if (own is null)
                return;
            var stored = own.Badges ?? [];
            if (unlocked.All(stored.Contains))
            {
                _held = key;
                ListeningShare.Say("badges: the profile already holds them all");
                return;
            }
            await Community.SetBadgesAsync(unlocked, own.Badge ?? "", own.BadgeColour ?? "");
            _held = key;
            ListeningShare.Say($"badges sent: {string.Join(",", unlocked.Where(b => !stored.Contains(b)))}");
            Sent?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // Tried again at the next library change.
        }
        finally
        {
            _sending = false;
        }
    }
}
