using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Audio;
using RPM.Core.Models;
using RPM.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// The DJ set: handing one record to the next by itself, and what the player shows while it is doing it.
/// </summary>
public sealed partial class RPaMpModel
{
    // ── DJ set ──

    /// <summary>True while the player is playing an auto-mixed set, not a queue of files. The queue still shows the
    /// set's running order, and the current row still follows the mix. The player does not drive playback. The DJ does,
    /// and it plays one continuous mix.</summary>
    public bool IsDjMixing { get; private set; }

    /// <summary>
    /// How the transport reaches the DJ while a set is running. The host sets this. It is called with +1 for next and
    /// -1 for previous. It returns true when the DJ handled the request, and false when there is no set and the player
    /// should do its normal action.
    /// </summary>
    public Func<int, bool>? DjTransport { get; set; }

    /// <summary>
    /// How the scrubber reaches the DJ. A set is one stream to the engine, so there is no single file to reopen at an
    /// offset. The DJ moves the playback position inside the record that is playing instead. This has the same contract as
    /// <see cref="DjTransport"/>: it returns true when the DJ handled the request.
    /// </summary>
    public Func<double, bool>? DjSeek { get; set; }

    /// <summary>Whether the DJ would take a seek right now. It is false between records, when the bar has no single
    /// record to point at.</summary>
    public Func<bool>? DjCanSeek { get; set; }

    /// <summary>The DJ takes over. The set is shown as the queue, with nothing playing yet. The first
    /// <see cref="NoteDjTrack"/> call lights the row up when the record drops.</summary>
    public void BeginDjSet(IReadOnlyList<TrackInfo> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Playlist.Clear();
        foreach (var track in tracks)
            Playlist.Add(new PlaylistTrack(track, radio: null));
        _currentIndex = -1;
        SetDeck(null);           // Nothing is on it yet. The first NoteDjTrack call puts the record on it.
        IsDjMixing = true;
        _djQueueIsTheirs = false;

        // Not ready until the DJ says otherwise. The DJ does that as soon as it has records to play.
        IsDjNextReady = false;
        OnChanged(nameof(IsDjMixing));
        OnChanged(nameof(IsPreviousLive));
        OnChanged(nameof(IsDjNextReady));
        OnChanged(nameof(IsNextLive));
        OnChanged(nameof(NextToolTip));
        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        RaiseNowPlaying();
    }

    /// <summary>True while the DJ is in the middle of a changeover. This runs from the press of "next", or from the
    /// arrival of the outro, until the fade is complete. The transport does not respond during that time. A second
    /// press is ignored, because a mix that is already on its way cannot be started twice.</summary>
    public bool IsDjChangingOver { get; private set; }

    /// <summary>The DJ started or finished a changeover.</summary>
    public void NoteDjChangeover(bool changingOver)
    {
        if (IsDjChangingOver == changingOver)
            return;
        IsDjChangingOver = changingOver;
        OnChanged(nameof(IsDjChangingOver));
        OnChanged(nameof(IsTransportLive));
        OnChanged(nameof(IsTransportBusy));
        OnChanged(nameof(IsNextLive));
        OnChanged(nameof(NextToolTip));

        // The bar goes with them: there is no single record to point at while two are handing over.
        OnChanged(nameof(ScrubberEnabled));
    }

    /// <summary>A changeover that is actually moving. While paused, the DJ is not read at all. The mix is frozen where
    /// it stands, and a busy button would claim work that has stopped. The button stays unclickable, because the mix
    /// picks up where it left off. It only stops showing that it is busy.</summary>
    public bool IsTransportBusy => IsDjChangingOver && IsPlaying;

    /// <summary>False only while a mix is handing over. That is the one time the transport buttons do nothing at
    /// all.</summary>
    public bool IsTransportLive => !IsDjChangingOver;

    /// <summary>Whether the set has another record to go to. The answer is true outside a set, where the question does
    /// not apply.</summary>
    public bool IsDjNextReady { get; private set; } = true;

    /// <summary>
    /// The next key.
    ///
    /// <para>The key is live for the whole of a set, except for its last record. Next plays the next record rather
    /// than mixing into it. So nothing has to be analysed for the key to work.</para>
    /// </summary>
    public bool IsNextLive => IsTransportLive && (!IsDjMixing || IsDjNextReady);

    /// <summary>What the next key says when it is pointed at.</summary>
    public string NextToolTip => Strings.Loc.Get("Transport_Next");

    /// <summary>The DJ says whether the set could move on right now. See
    /// <see cref="RPM.Core.Mixing.Dj.NextReadyChanged"/>.</summary>
    public void NoteDjNextReady(bool ready)
    {
        if (IsDjNextReady == ready)
            return;

        IsDjNextReady = ready;
        OnChanged(nameof(IsDjNextReady));
        OnChanged(nameof(IsNextLive));
        OnChanged(nameof(NextToolTip));
    }

    /// <summary>
    /// False for the whole of a DJ set. A mix has no "back". What has been faded away is gone, and the record that is
    /// playing is part way through being handed over to the next one. The button says so, rather than offering a move
    /// that cannot be made.
    /// </summary>
    public bool IsPreviousLive => !IsDjMixing;

    /// <summary>
    /// True once the listener has changed the queue during a set. That means they added a track, removed one, or
    /// dragged a row.
    ///
    /// <para>After that, the DJ's own order is not written over the queue. The set still plays in the DJ's order. What
    /// stops is this class moving rows around under a listener who has just arranged them. The flag is cleared when a
    /// set begins and when it ends.</para>
    /// </summary>
    private bool _djQueueIsTheirs;

    /// <summary>Set while this class does the rearranging, so its own moves are not read as the listener's. Every move
    /// raises the collection-changed handler, which watches for exactly that.</summary>
    private bool _reordering;

    /// <summary>The listener changed the queue during a set. Nothing is undone. The DJ's order simply stops being
    /// applied on top of the listener's order.</summary>
    private void NoteQueueIsTheirs()
    {
        if (!IsDjMixing || _reordering || _djQueueIsTheirs)
            return;

        _djQueueIsTheirs = true;
    }

    /// <summary>
    /// The DJ's running order: what has been played, then the rest in the order the DJ would take them.
    ///
    /// <para>The rows are moved rather than rebuilt. So the playing row stays the playing row, and a list that is
    /// mid-scroll does not jump. Anything in the queue that the DJ does not know about, such as a track added during the
    /// set, keeps its relative order and follows the rows the DJ does know. It is not dropped from a queue it was
    /// deliberately put into.</para>
    /// </summary>
    public void NoteDjOrder(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!IsDjMixing || _djQueueIsTheirs || paths.Count == 0)
            return;

        _reordering = true;
        try
        {
            int at = 0;
            foreach (var path in paths)
            {
                int from = -1;
                for (int i = at; i < Playlist.Count; i++)
                {
                    if (!string.Equals(Playlist[i].FilePath, path, StringComparison.Ordinal))
                        continue;
                    from = i;
                    break;
                }

                if (from < 0)
                    continue;               // in the DJ order, but not in this queue

                if (from != at)
                    Playlist.Move(from, at);
                at++;
            }
        }
        finally
        {
            _reordering = false;
        }

        var current = Playlist.FirstOrDefault(t => t.IsCurrent);
        _currentIndex = current is null ? -1 : Playlist.IndexOf(current);
        Renumber();
    }

    /// <summary>The mix moved onto another record, so the queue follows it. There is no playback call here. The audio
    /// for it is already coming out of the DJ.</summary>
    public void NoteDjTrack(string filePath)
    {
        if (!IsDjMixing)
            return;
        var at = Playlist.ToList().FindIndex(t => string.Equals(t.FilePath, filePath, StringComparison.Ordinal));
        if (at < 0)
            return;
        _currentIndex = at;
        SetDeck(Playlist[at]);   // the mix moved the record on the deck without the transport touching it
        Renumber();
        RaiseNowPlaying();
    }

    /// <summary>
    /// True from the moment a set ends until the queue is deliberately asked to play something.
    ///
    /// <para>The end of a set is reported twice. The DJ reports it from inside its own read, and the engine reports
    /// that its source ran out. They are the same event, and they arrive in that order. By the time the second one is
    /// handled, the set is already over, so it would look like an ordinary track ending. That would start the queue
    /// playing normally on top of a set that the listener has just watched end. This flag ignores exactly the one that
    /// follows a set.</para>
    /// </summary>
    private bool _setJustEnded;

    /// <summary>The set is over, or was cut short. The queue stays, so the same records can be played normally from
    /// here.</summary>
    public void EndDjSet()
    {
        if (!IsDjMixing)
            return;
        IsDjMixing = false;
        _djQueueIsTheirs = false;
        _setJustEnded = true;
        _currentIndex = -1;
        SetDeck(null);

        // The queue is an ordinary queue again. Next means the next file, and nothing has to be read ahead for it to work.
        IsDjNextReady = true;
        OnChanged(nameof(IsDjMixing));
        OnChanged(nameof(IsPreviousLive));
        OnChanged(nameof(IsDjNextReady));
        OnChanged(nameof(IsNextLive));
        OnChanged(nameof(NextToolTip));

        // A set cut short in the middle of a changeover would leave the transport showing a mix that will never finish.
        NoteDjChangeover(false);
        Renumber();
        RaiseNowPlaying();
    }

    private void OnTrackEnded() => _dispatcher.TryEnqueue(() =>
    {
        // A mix ends when the DJ has no more records. There is nothing to advance to. Stepping into the next queued file
        // would start a second set, with no mix, behind the first.
        if (IsDjMixing) { EndDjSet(); return; }
        if (_setJustEnded) { _setJustEnded = false; return; }
        if (Playlist.Count == 0) return;

        // Which row the cursor is on when the end arrives, and what the engine is on by then. The two
        // differ when a track the listener picked is already starting and the end belongs to the one
        // before it.
        RPM.Core.Diagnostics.Said.Line("queue",
            $"track ended with the cursor on row {_currentIndex + 1} of {Playlist.Count}; " +
            $"the engine is on '{Path.GetFileName(_playback.PlayingPath ?? "nothing")}'");

        if (_repeat) { PlayIndex(_currentIndex, "track ended, repeat"); return; }
        if (_shuffle) { Step(+1, "track ended, shuffle"); return; }
        // Advance; stop at the end of a non-repeating list.
        if (_currentIndex + 1 < Playlist.Count) PlayIndex(_currentIndex + 1, "track ended");
        else { _currentIndex = -1; SetDeck(null); Renumber(); RaiseNowPlaying(); }
    });

    /// <summary>The music was stopped rather than played out, so the deck is empty. Only this takes a record off the
    /// deck. The engine also reports nothing playing in the gap between two queued tracks, and that is a queue still
    /// running.</summary>
    private void OnStopped() => _dispatcher.TryEnqueue(() => SetDeck(null));

    private void OnPlaybackChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (_playback.PlayingPath is null) _positionTimer.Stop();
        else _positionTimer.Start();

        // Something outside the queue took over the player, such as a clip preview from the split editor or a rip
        // audition. Those call the engine directly, so no Stop is seen here. The record is still removed from the deck,
        // and the artwork should follow what is actually playing.
        if (_playback.PlayingPath is { } path && Deck is not null
            && !Playlist.Any(t => string.Equals(t.FilePath, path, StringComparison.Ordinal)))
            SetDeck(null);

        // Stopped means the set is over, whatever the DJ was doing. The DJ reports that a changeover has finished from
        // inside its own read. A set stopped mid-mix never reports it, because the audio is gone and nothing is left to
        // drive the DJ. Anything the DJ had asked the transport to show would then stay on screen. The next key would
        // keep showing that it is busy for a mix that will never finish, and the back key would stay disabled for a set
        // that is not running.
        if (IsDjMixing && _playback.PlayingPath is null)
            EndDjSet();

        // A record on the deck is a different record for the favourite state, and the television has to know about it.
        // This costs nothing when nothing is being cast. See SyncCastFavorite.
        SyncCastFavorite();

        RaiseNowPlaying();
    });
}
