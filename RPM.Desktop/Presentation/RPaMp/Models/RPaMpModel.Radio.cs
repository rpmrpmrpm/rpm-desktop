using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Core.Playback;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// Song radio: "Start radio" on a song plays it, then keeps <see cref="SongRadio.Ahead"/> songs like the one playing
/// queued after it, for as long as the radio's own rows are what is playing.
///
/// <para>Whether the radio is on is read from the row on the deck each time it matters
/// (<see cref="PlaylistTrack.Radio"/>), never kept as a flag. Every other way of filling the queue builds rows with no
/// radio on them, so playing an album, starting a DJ set or clearing the queue ends the radio without saying so. A
/// song added with Play next or Add to now playing is not a radio row: it plays in its turn, and the radio carries on
/// from the radio row after it.</para>
/// </summary>
public sealed partial class RPaMpModel
{
    /// <summary>The queue becomes this one song, which starts now. Going on the deck is what asks the radio for the
    /// songs after it, so they arrive while it plays.</summary>
    public void StartRadio(TrackInfo track, SongRadioRun radio)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(radio);

        // The same clearing PlayAlbumFrom does: the old queue, and any offer to resume inside it, are gone.
        ForgetResume();
        ForgetSource();

        Playlist.Clear();
        Playlist.Add(new PlaylistTrack(track, radio));

        _currentIndex = -1;
        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        PlayIndex(0, "Start radio");
    }

    /// <summary>
    /// A row of a radio went on the deck: pick songs that follow it, on the pool, and queue them when they arrive.
    ///
    /// <para>The queue and the history are copied here, on the dispatcher, because the pick runs on the pool and both
    /// are changed on the dispatcher. What the pick returns is checked again against the queue as it is when the
    /// answer lands (<see cref="TakeRadioPicks"/>), so a copy that has gone stale costs nothing.</para>
    /// </summary>
    private void TopUpRadio(PlaylistTrack playing, SongRadioRun radio)
    {
        var inQueue = Playlist.Select(t => t.FilePath).ToList();
        var history = radio.History;
        var playingPath = playing.FilePath;

        Background.Run(async () =>
        {
            var picks = await radio.NextAsync(playingPath, inQueue, history, SongRadio.Ahead).ConfigureAwait(false);
            if (picks.Count > 0)
                _dispatcher.TryEnqueue(() => TakeRadioPicks(radio, picks));
        });
    }

    /// <summary>
    /// Appends picks until <see cref="SongRadio.Ahead"/> songs follow the deck, if the radio is still on.
    ///
    /// <para>Everything is decided from the queue as it is now, not as it was when the picks were asked for: whether a
    /// row of this radio is on the deck, how many songs follow it, and which picks are already in the queue. So two
    /// answers landing in either order, or one landing after the listener has changed the queue, cannot overfill it,
    /// duplicate a song, or add to a queue the radio is no longer playing.</para>
    ///
    /// <para>Appended at the end and never inserted, which leaves the successor the gapless changeover has opened
    /// (always the row after the cursor) where it was. Nothing here clears the chain.</para>
    /// </summary>
    private void TakeRadioPicks(SongRadioRun radio, IReadOnlyList<TrackInfo> picks)
    {
        if (Deck is not { } deck || !ReferenceEquals(deck.Radio, radio))
            return;

        var at = Playlist.IndexOf(deck);
        if (at < 0)
            return;

        AppendRadioRows(radio, picks, SongRadio.Ahead - (Playlist.Count - 1 - at));
    }

    /// <summary>Appends up to <paramref name="wanted"/> of the picks at the end of the queue as rows of the radio,
    /// leaving out what is queued already and what this radio queued once and the listener took out.</summary>
    private void AppendRadioRows(SongRadioRun radio, IReadOnlyList<TrackInfo> picks, int wanted)
    {
        if (wanted <= 0)
            return;

        var inQueue = new HashSet<string>(Playlist.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var pick in picks)
        {
            if (added == wanted)
                break;

            // Already queued by this radio once and since removed by the listener: not put back.
            if (radio.HasQueued(pick.FilePath) || !inQueue.Add(pick.FilePath))
                continue;

            Playlist.Add(new PlaylistTrack(pick, radio));
            radio.NoteQueued(pick.FilePath);
            added++;
        }

        if (added == 0)
            return;

        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));

        // Cloud songs among the new rows get their addresses signed before they are reached.
        _ = WarmAheadAsync();
    }

    // ── a list that ends goes on as a mixtape ──

    /// <summary>
    /// A row that is not a radio's went on the deck. When it is the last of its list and the setting says so
    /// (<see cref="SettingsService.MixtapeAfter"/>, read here each time, so a change counts from the next list),
    /// a mixtape is tuned from the list (<see cref="SongRadioRun.CarryOn"/>) and its first songs are
    /// queued after this one when they arrive. From its first row on, the mixtape tops itself up as every song
    /// radio does (<see cref="TopUpRadio"/>).
    /// </summary>
    private void CarryOnAsMixtape(PlaylistTrack playing)
    {
        if (_settings?.MixtapeAfter != true)
            return;

        var at = Playlist.IndexOf(playing);
        if (!Mixtape.CarriesOn(at, Playlist.Count, playing.Radio is not null, _repeat, _shuffle, IsDjMixing))
            return;

        var inQueue = Playlist.Select(t => t.FilePath).ToList();
        if (SongRadioRun.CarryOn(inQueue, _library) is not { } radio)
            return;

        var history = radio.History;
        var playingPath = playing.FilePath;
        Said.Line("songradio", $"a list of {inQueue.Count} reached its last song: a mixtape carries on after it");

        Background.Run(async () =>
        {
            var picks = await radio.NextAsync(playingPath, inQueue, history, SongRadio.Ahead).ConfigureAwait(false);
            if (picks.Count > 0)
                _dispatcher.TryEnqueue(() => TakeMixtapePicks(playing, radio, picks));
        });
    }

    /// <summary>The mixtape's first songs arrived: queued after the list's last song, only while that song is still
    /// on the deck and still the last in the queue. A queue changed in the meantime is the listener's, and is left
    /// alone.</summary>
    private void TakeMixtapePicks(PlaylistTrack after, SongRadioRun radio, IReadOnlyList<TrackInfo> picks)
    {
        if (!ReferenceEquals(Deck, after) || Playlist.IndexOf(after) != Playlist.Count - 1)
            return;

        AppendRadioRows(radio, picks, SongRadio.Ahead);
    }
}
