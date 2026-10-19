using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The app on the system's own transport: the play/pause key on the keyboard row, the buttons on a pair of
/// headphones, and the Now Playing tile in Control Center.
///
/// <para>Data goes in two directions, and this is the only place where either one crosses. Outwards, what
/// is playing is sent to the system as a whole <see cref="NowPlayingCard"/>. Inwards, a key press arrives
/// as a <see cref="RemoteCommand"/> and runs through the deck's own commands. These are the same
/// commands that the window's buttons and the space bar run, so the keyboard never gives a different
/// answer about what the player is doing.</para>
///
/// <para><b>What arrives on its own clock, and why each one is safe.</b></para>
/// <list type="number">
/// <item>The position tick, every 400ms on the UI thread. Safe: it sends nothing unless the card has
/// changed in a way that the tile's own clock cannot account for (<see
/// cref="NowPlayingCard.NeedsRestating"/>).</item>
/// <item>The deck changing, on the UI thread. Safe: the cover is removed in the same step, so a new title
/// is never shown with the last record's cover.</item>
/// <item>A key press, from AppKit's main thread on the Mac and from a thread-pool thread on Windows. It is
/// moved onto the dispatcher rather than trusted to already be on the UI thread, so the model is only ever
/// touched from one place.</item>
/// <item>The cover, read on a worker thread. It lands whenever its loading finishes, which can be after the
/// listener has moved on two records. It is safe only because the record it was loaded for is checked
/// against the record on the deck now, by path, at the moment it lands (<see cref="NowPlayingSleeve"/>). A
/// cover that lands before the deck has a card is held for the next show, and is not sent as a clear
/// (<see cref="NowPlayingStatement"/>).</item>
/// </list>
/// </summary>
public sealed class NowPlayingService
{
    private readonly RPaMpService _amp;
    private readonly RadioService _radio;
    private readonly LibraryService _library;
    private readonly DispatcherQueue _dispatcher;

    private ISystemTransport? _system;
    private RPaMpModel? _transport;
    private NowPlayingCard? _told;
    private long _toldAt;
    private readonly NowPlayingSleeve _sleeve = new();
    private CancellationTokenSource? _artLoad;

    public NowPlayingService(RPaMpService amp, RadioService radio, LibraryService library, DispatcherQueue dispatcher)
    {
        _amp = amp;
        _radio = radio;
        _library = library;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Puts the app on the system's transport. Called once the window is up, because the transport is built
    /// with it, and a tile cannot state what a player that does not exist yet is playing.
    ///
    /// <para>macOS uses MPNowPlayingInfoCenter. Windows (WinUI head) uses SystemMediaTransportControls,
    /// which needs the handle of <paramref name="window"/>. Linux (MPRIS) is not written yet, and there
    /// this does nothing (see <see cref="SystemTransport.For"/>).</para>
    /// </summary>
    public void Attach(Window window)
    {
        if (_system is not null || SystemTransport.For(window) is not { } system)
            return;

        _system = system;
        _transport = _amp.Transport;
        _transport.PropertyChanged += (_, _) => State(StatementCause.Tick);
        _transport.DeckChanged += OnDeckChanged;
        system.Commanded += OnCommanded;

        OnDeckChanged();
    }

    // ── outwards: what is playing ────────────────────────────────────────────────────────────────

    /// <summary>The record on the deck, as the tile reads it. Null when the player is empty. A station is a
    /// record with no length. Its title is what the stream is announcing, shown under the station's own
    /// name.</summary>
    private NowPlayingCard? Card()
    {
        if (_transport is not { IsLoaded: true } transport)
            return null;

        if (transport.IsLiveStream)
        {
            // Display, so that a title the admin set (StationEdits) reaches the system's media controls,
            // as it reaches every list.
            var station = _radio.Current?.Display;
            return NowPlayingCard.For(
                _radio.NowPlayingTitle ?? station, station, null, 0, 0, transport.IsPlaying);
        }

        if (transport.Deck is not { } deck)
            return null;

        return NowPlayingCard.For(
            deck.Title, deck.Artist, deck.Album,
            transport.Duration, transport.Position, transport.IsPlaying);
    }

    /// <summary>Tells the system what is playing, or that nothing is, when <paramref name="cause"/> calls
    /// for it (<see cref="NowPlayingStatement"/>). It is cheap, so it is called on every tick.</summary>
    private void State(StatementCause cause)
    {
        if (_system is not { } system)
            return;

        var card = Card();
        var since = _told is null ? double.MaxValue : Stopwatch.GetElapsedTime(_toldAt).TotalSeconds;
        var needsRestating = card is { } now && _told is { } told && NowPlayingCard.NeedsRestating(told, since, now);

        switch (NowPlayingStatement.Next(cause, card is not null, _told is not null, needsRestating))
        {
            case Statement.Show:
                system.Show(card!.Value);
                _told = card;
                _toldAt = Stopwatch.GetTimestamp();
                break;

            case Statement.Clear:
                _told = null;
                system.Clear();
                _sleeve.Cleared();
                break;
        }
    }

    // ── the sleeve ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A record was put on the deck. The cover comes off before the new one is read, so the tile
    /// never shows this title with the last record's cover. The state is sent again once the picture is
    /// in.</summary>
    private void OnDeckChanged()
    {
        var path = _transport?.Deck?.FilePath;
        if (!_sleeve.Want(path))
        {
            State(StatementCause.Tick);
            return;
        }

        _artLoad?.Cancel();
        _artLoad = new CancellationTokenSource();
        _system?.SetArtwork(null);

        // Sent now, without a cover, rather than after the picture is read. The title is what the listener
        // pressed for, so it goes on the tile before the disk has answered.
        State(StatementCause.DeckChanged);

        if (path is null)
            return;

        _ = LoadCoverAsync(path, _artLoad.Token);
    }

    private async Task LoadCoverAsync(string path, CancellationToken ct)
    {
        byte[]? cover = null;
        try
        {
            if (FolderOf(path) is { } folder)
                cover = await AlbumCovers.LoadAsync(folder, ct, maxEdge: 512, ownCollection: true)
                    ?? await AlbumCovers.LoadAsync(folder, ct, maxEdge: 512);
        }
        catch
        {
            cover = null;   // No cover then. The title and the buttons matter more, and they still work.
        }

        if (ct.IsCancellationRequested)
            return;

        _dispatcher.TryEnqueue(() =>
        {
            // The record the cover was loaded for is checked against the record on the deck now. Two covers
            // can be loading at once, and the one that lands last is not always the one that is playing.
            if (!_sleeve.Land(path))
                return;

            // The sleeve is part of the statement, so the statement is sent again. If there is no card yet
            // (the deck is still opening the file), nothing is sent here, and the cover waits for the show
            // that the load causes. Clearing here would drop it.
            _system?.SetArtwork(cover);
            State(StatementCause.CoverLanded);
        });
    }

    /// <summary>The track's folder, as the mirror names it: relative to the collection's root, with forward
    /// slashes. Null for a file played from outside the collection, which has no album folder to read a
    /// cover from.</summary>
    private string? FolderOf(string path)
    {
        try
        {
            if (_library is not { IsOpen: true } library || library.Root is not { } root)
                return null;

            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;

            var cut = relative.LastIndexOf('/');
            return cut < 0 ? "" : relative[..cut];
        }
        catch
        {
            return null;
        }
    }

    // ── inwards: the keys ────────────────────────────────────────────────────────────────────────

    /// <summary>A key pressed outside the app. It runs on the dispatcher through the deck's own commands,
    /// so it does the same thing as pressing the button in the window.</summary>
    private void OnCommanded(RemoteCommand command, double seconds) => _dispatcher.TryEnqueue(() =>
    {
        if (_transport is not { } transport)
            return;

        switch (command)
        {
            // The system sends Play and Pause as two separate commands (headphones send them, and so does
            // the tile). The deck has one command for both, so each case is a toggle that only fires when
            // the player is on the other side of it. A second Play while playing must not pause the record.
            case RemoteCommand.Play when !transport.IsPlaying:
            case RemoteCommand.Pause when transport.IsPlaying:
            case RemoteCommand.Toggle:
                transport.PlayPauseCommand.Execute(null);
                break;

            case RemoteCommand.Next:
                transport.NextCommand.Execute(null);
                break;

            case RemoteCommand.Previous:
                transport.PrevCommand.Execute(null);
                break;

            case RemoteCommand.SeekTo:
                transport.SeekTo(seconds, "the system media controls");
                break;
        }

        // The tile asked for this, so it is told the result now, without waiting for the next tick.
        State(StatementCause.Tick);
    });
}
